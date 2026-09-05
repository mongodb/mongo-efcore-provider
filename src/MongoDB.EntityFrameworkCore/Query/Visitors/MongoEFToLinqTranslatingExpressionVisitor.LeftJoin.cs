/* Copyright 2023-present MongoDB Inc.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 * http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Driver;
using MongoDB.Driver.Linq;
using MongoDB.EntityFrameworkCore.Diagnostics;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.Serializers;
using MongoDB.EntityFrameworkCore.Query.Expressions;

namespace MongoDB.EntityFrameworkCore.Query.Visitors;

// Join rewriting for the C# driver's LINQ provider. This file groups the join-handling helpers, which
// mix two concerns:
//   * Driver-native LeftJoin rewrite: StripOuterSelectForJoin, RewriteLeftJoins, RewriteJoinNode,
//     TryBuildDriverNativeLeftJoinPipeline, BuildLeftJoinResultSerializer, BuildProjectedLeftOuterJoin,
//     RewriteLambdaForLeftJoinResult, TransparentIdentifierToLeftJoinResultRewriter, TryGetKeyFieldPath,
//     AppendRawStage. These rewrite EF's LeftJoin into the driver's Join, since the driver has no
//     LeftJoin translator.
//   * $lookup fallback plumbing: StripJoinForLookup, IsJoinRelatedMethod, FindBaseSourceThroughJoin, and
//     the $lookup-stage emission (AppendLookupStages, InjectAfterRootLookupStages, EmitLookupStages).
//     These peel a Join chain back to its root and emit the manual $lookup + $unwind stages that stand in
//     where the driver join cannot express the shape.
// The Visit/VisitMethodCall dispatch remains in the main visitor file and calls into the helpers here.
internal sealed partial class MongoEFToLinqTranslatingExpressionVisitor : System.Linq.Expressions.ExpressionVisitor
{
    /// <summary>
    /// For join queries, strip the outermost Select that EF adds to extract from TransparentIdentifier,
    /// then rewrite every LeftJoin in the residual chain. The driver's LINQ v3 pipeline has no LeftJoin
    /// translator: it dispatches on method name and only recognizes "Join" (routed to
    /// <c>JoinMethodToPipelineTranslator</c>, which requires <c>method.Is(QueryableMethod.Join)</c>).
    /// So every <c>Queryable.LeftJoin</c> the provider emits must be
    /// rewritten to <c>Queryable.Join</c> with a
    /// <see cref="LeftJoinResult{TOuter,TInner}"/> result selector — the driver produces documents with
    /// _outer/_inner fields which match the LeftJoinResult property names.
    /// </summary>
    private Expression? StripOuterSelectForJoin(Expression expression)
    {
        if (expression is not MethodCallExpression call)
        {
            return null;
        }

        // The outermost IS the Select (enumerable cardinality, no terminal op): drop the projection
        // Select (the shaper runs client-side) and rewrite joins in its source.
        if (call.Method.Name == "Select" && call.Method.DeclaringType == typeof(Queryable))
        {
            return RewriteLeftJoins(call.Arguments[0], convertExplicitJoins: true);
        }

        // Terminal op (First, etc.) wrapping a Select: drop the inner Select, keep the terminal op.
        if (call.Arguments.Count >= 1 && call.Arguments[0] is MethodCallExpression innerCall
            && innerCall.Method.Name == "Select" && innerCall.Method.DeclaringType == typeof(Queryable))
        {
            var selectSource = RewriteLeftJoins(innerCall.Arguments[0], convertExplicitJoins: true);
            var newArgs = call.Arguments.ToArray();
            newArgs[0] = selectSource;

            var method = call.Method;
            if (method.IsGenericMethod)
            {
                var sourceItemType = selectSource.Type.TryGetItemType();
                if (sourceItemType != null)
                {
                    var genericDef = method.GetGenericMethodDefinition();
                    if (genericDef.GetGenericArguments().Length == 1)
                    {
                        method = genericDef.MakeGenericMethod(sourceItemType);
                    }
                }
            }

            return Expression.Call(null, method, newArgs);
        }

        // No outer Select to strip (it was already stripped by the mixed path, or the query is a bare
        // join/terminal-op chain): just rewrite joins throughout.
        return RewriteLeftJoins(expression, convertExplicitJoins: true);
    }

    /// <summary>
    /// Recursively rewrites every <c>Queryable.LeftJoin</c> node in a
    /// method-call chain into <c>Queryable.Join</c> with a
    /// <see cref="LeftJoinResult{TOuter,TInner}"/> result selector, and cascades the resulting element-type
    /// change (TransparentIdentifier&lt;O,I&gt; → LeftJoinResult&lt;O,I&gt;) into every downstream operator
    /// (Select / Where / OrderBy / chained Join+LeftJoin / terminal ops) so member accesses
    /// <c>.Outer</c>/<c>.Inner</c> become <c>._outer</c>/<c>._inner</c> and generic arguments stay consistent.
    /// Works bottom-up so multi-level Includes (chained joins) and joins nested inside other operators are
    /// all handled.
    ///
    /// <paramref name="convertExplicitJoins"/> controls whether an explicit user <c>Queryable.Join</c> also
    /// gets a <see cref="LeftJoinResult{TOuter,TInner}"/> result selector. The shaped (entity-materializing)
    /// path needs this — its client-side shaper reads <c>_outer</c>/<c>_inner</c> from the joined document.
    /// The projected path must NOT convert explicit Joins: their result selector is the user's projection and
    /// has to be emitted verbatim. (A <c>LeftJoin</c> is always converted regardless, since the driver has no
    /// LeftJoin translator.)
    /// </summary>
    private Expression RewriteLeftJoins(Expression expression, bool convertExplicitJoins)
    {
        if (expression is not MethodCallExpression call || call.Method.DeclaringType != typeof(Queryable))
        {
            return expression;
        }

        // Rewrite the source first (bottom-up).
        var originalSource = call.Arguments[0];
        var newSource = RewriteLeftJoins(originalSource, convertExplicitJoins);
        var sourceChanged = !ReferenceEquals(newSource, originalSource);
        var newSourceItemType = sourceChanged ? newSource.Type.TryGetItemType() : null;

        var isLeftJoin = call.Method.Name == "LeftJoin";
        var isJoin = call.Method.Name == "Join";

        if (isLeftJoin || isJoin)
        {
            return RewriteJoinNode(call, newSource, newSourceItemType,
                convertToJoin: isLeftJoin || (isJoin && convertExplicitJoins),
                isLeftJoin: isLeftJoin,
                shapedPath: convertExplicitJoins);
        }

        // Non-join operator. If the source element type changed, rebuild this node to consume the new
        // type, remapping any lambda arguments (predicate/selector/key) whose first parameter was the
        // old TransparentIdentifier.
        if (!sourceChanged || newSourceItemType == null)
        {
            return call;
        }

        var oldSourceItemType = originalSource.Type.TryGetItemType();
        var genericArgs = call.Method.GetGenericArguments().ToArray();
        var rebuiltArgs = call.Arguments.ToArray();
        rebuiltArgs[0] = newSource;

        // Source element type is (by EF convention) the first generic argument of these single-source operators.
        for (var i = 0; i < genericArgs.Length; i++)
        {
            if (genericArgs[i] == oldSourceItemType)
            {
                genericArgs[i] = newSourceItemType;
            }
        }

        for (var i = 1; i < rebuiltArgs.Length; i++)
        {
            if (rebuiltArgs[i] is UnaryExpression { NodeType: ExpressionType.Quote, Operand: LambdaExpression lambda }
                && lambda.Parameters.Count > 0 && lambda.Parameters[0].Type == oldSourceItemType)
            {
                rebuiltArgs[i] = Expression.Quote(RewriteLambdaForLeftJoinResult(lambda, newSourceItemType));
            }
        }

        var rebuiltMethod = call.Method.GetGenericMethodDefinition().MakeGenericMethod(genericArgs);
        return Expression.Call(null, rebuiltMethod, rebuiltArgs);
    }

    /// <summary>
    /// Rewrites a single Join/LeftJoin node given its (possibly already-rewritten) source. A LeftJoin is
    /// converted to a <c>Queryable.Join</c> with a <see cref="LeftJoinResult{TOuter,TInner}"/> result
    /// selector so the driver's Join translator (which has no LeftJoin equivalent) accepts it. An explicit
    /// user <c>Queryable.Join</c> keeps its own result selector — it must round-trip unchanged so the
    /// emitted projection matches what the user wrote; only its parameter/generic types are remapped when a
    /// nested join below it changed the outer element type. Key selectors are remapped when the outer element
    /// type changed.
    /// </summary>
    private Expression RewriteJoinNode(
        MethodCallExpression call, Expression newSource, Type? newSourceItemType, bool convertToJoin, bool isLeftJoin, bool shapedPath)
    {
        var sourceChanged = newSourceItemType != null;
        if (!convertToJoin && !sourceChanged)
        {
            // Explicit user Join whose source is unchanged — emit it verbatim.
            return call;
        }

        var genericArgs = call.Method.GetGenericArguments();
        var resultSelector = call.Arguments[4].UnwrapLambdaFromQuote();
        var innerType = resultSelector.Parameters[1].Type;
        var oldOuterType = resultSelector.Parameters[0].Type;

        // When the outer source was rewritten, its element type changed (TransparentIdentifier → LeftJoinResult).
        var outerType = newSourceItemType ?? oldOuterType;

        // A reference Include is a LEFT-OUTER join: the principal (outer) row must survive even when the
        // related (inner) entity is absent (optional/nullable FK). The driver has no LeftJoin pipeline
        // translator, and its Join translator emits a plain `$unwind` (INNER join — it silently drops
        // null-FK principals). So for a bare single-reference LeftJoin (the outer is the root entity, not a
        // nested LeftJoinResult from a prior join) we emit the equivalent `$lookup` + `$unwind` pipeline
        // ourselves, but with `preserveNullAndEmptyArrays: true` — producing the same root-level
        // _outer/_inner document shape the shaper already reads, only left-outer instead of inner.
        if (isLeftJoin && shapedPath && outerType == oldOuterType)
        {
            var leftOuter = TryBuildDriverNativeLeftJoinPipeline(call, newSource);
            if (leftOuter != null)
            {
                return leftOuter;
            }
        }

        // The PROJECTED (push-down, non-shaped) path turns an Include/optional-navigation LeftJoin into a
        // driver query whose element type is LeftJoinResult<TOuter,TInner>. Emitting Queryable.Join here would
        // give the driver an INNER join ($unwind without preserve), silently dropping principals whose related
        // entity is absent. Instead emit the canonical driver-translatable left-outer shape
        // outer.GroupJoin(inner, ok, ik, (o, g) => carrier).SelectMany(c => c._inner.DefaultIfEmpty(),
        //   (c, i) => new LeftJoinResult<TOuter,TInner>(c._outer, i))
        // which the driver renders as $lookup (array) + $map/$cond + $unwind, preserving unmatched principals
        // while producing the SAME root-level _outer/_inner document shape the inner-Join path produced (so the
        // downstream result selector / shaper is unchanged). The shaped (entity-materializing) path keeps using
        // its own manual $lookup + preserve-$unwind pipeline (TryBuildDriverNativeLeftJoinPipeline) above.
        if (convertToJoin && isLeftJoin && !shapedPath)
        {
            return BuildProjectedLeftOuterJoin(
                call, newSource, outerType, innerType, genericArgs[2], oldOuterType);
        }

        Type resultType;
        LambdaExpression newResultSelector;
        if (convertToJoin)
        {
            // LeftJoin → LeftJoinResult-constructing result selector with the correct outer parameter type.
            resultType = typeof(LeftJoinResult<,>).MakeGenericType(outerType, innerType);
            var ctor = resultType.GetConstructors()[0];
            var newOuterParam = Expression.Parameter(outerType, resultSelector.Parameters[0].Name);
            var newInnerParam = resultSelector.Parameters[1];
            newResultSelector = Expression.Lambda(
                Expression.New(ctor, newOuterParam, newInnerParam),
                newOuterParam, newInnerParam);
        }
        else
        {
            // Explicit Join with a changed source: keep the user's result selector but remap its outer
            // parameter type so it can read _outer/_inner from the rewritten LeftJoinResult source.
            resultType = call.Method.GetGenericArguments()[^1];
            newResultSelector = outerType != oldOuterType
                ? RewriteLambdaForLeftJoinResult(resultSelector, outerType)
                : resultSelector;
        }

        // Emit Queryable.Join (the driver has no LeftJoin pipeline translator); an explicit Join stays a Join.
        var joinDefinition = convertToJoin ? QueryableJoinMethod : call.Method.GetGenericMethodDefinition();
        var newMethod = joinDefinition.MakeGenericMethod(outerType, innerType, genericArgs[2], resultType);

        var newArgs = call.Arguments.ToArray();
        newArgs[0] = newSource;
        // Rewrite the outer key selector when the outer element type changed.
        if (outerType != oldOuterType
            && call.Arguments[2] is UnaryExpression { NodeType: ExpressionType.Quote, Operand: LambdaExpression outerKey })
        {
            newArgs[2] = Expression.Quote(RewriteLambdaForLeftJoinResult(outerKey, outerType));
        }

        newArgs[4] = Expression.Quote(newResultSelector);

        return Expression.Call(null, newMethod, newArgs);
    }

    /// <summary>
    /// Emits the driver-native left-outer join pipeline for a single-reference <c>LeftJoin</c> as raw
    /// aggregation stages (via <see cref="MongoQueryable.AppendStage"/>), mirroring the driver's
    /// <c>JoinMethodToPipelineTranslator</c> output but with a <c>preserveNullAndEmptyArrays: true</c>
    /// <c>$unwind</c> so principals with a null/absent related entity are preserved:
    /// <code>
    /// { $project: { _outer: "$$ROOT", _id: 0 } }
    /// { $lookup:  { from: &lt;inner collection&gt;, localField: "_outer.&lt;fk&gt;", foreignField: "&lt;pk&gt;", as: "_inner" } }
    /// { $unwind:  { path: "$_inner", preserveNullAndEmptyArrays: true } }
    /// { $project: { _outer: "$_outer", _inner: "$_inner", _id: 0 } }
    /// </code>
    /// The output document shape (root-level <c>_outer</c>/<c>_inner</c>) is identical to the driver's Join,
    /// so the client-side shaper is unchanged. Returns <see langword="null"/> (falling back to the driver's
    /// inner Join) for any join shape this builder does not recognise (e.g. composite/anonymous join keys),
    /// preserving prior behaviour for those cases.
    /// </summary>
    private Expression? TryBuildDriverNativeLeftJoinPipeline(
        MethodCallExpression call, Expression newSource)
    {
        // Inner source must be a bare DbSet root so we can resolve its collection name.
        if (call.Arguments[1] is not EntityQueryRootExpression innerRoot)
        {
            return null;
        }

        var innerEntityType = innerRoot.EntityType;
        var outerKey = call.Arguments[2].UnwrapLambdaFromQuote();
        var innerKey = call.Arguments[3].UnwrapLambdaFromQuote();

        var outerEntityType = _queryContext.Context.Model.FindEntityType(outerKey.Parameters[0].Type);
        if (outerEntityType == null)
        {
            return null;
        }

        var outerField = TryGetKeyFieldPath(outerKey.Body, outerEntityType);
        var innerField = TryGetKeyFieldPath(innerKey.Body, innerEntityType);
        if (outerField == null || innerField == null)
        {
            return null;
        }

        var collectionName = innerEntityType.GetCollectionName();
        var outerClrType = newSource.Type.TryGetItemType()!;
        var innerClrType = innerEntityType.ClrType;

        var projectOuter = new BsonDocument("$project", new BsonDocument { { "_outer", "$$ROOT" }, { "_id", 0 } });
        var lookup = new BsonDocument("$lookup", new BsonDocument
        {
            { "from", collectionName },
            { "localField", $"_outer.{outerField}" },
            { "foreignField", innerField },
            { "as", "_inner" }
        });
        // Unconditionally true: this builder is only reached from LeftJoin handling, which is left-outer by
        // definition. The flag that follows the LINQ operator (LookupExpression.PreserveNullAndEmptyArrays)
        // is only consulted on the flat-lookup path (EmitLookupStages); no inner Join routes through here.
        var unwind = new BsonDocument("$unwind",
            new BsonDocument { { "path", "$_inner" }, { "preserveNullAndEmptyArrays", true } });
        var projectResult = new BsonDocument("$project",
            new BsonDocument { { "_outer", "$_outer" }, { "_inner", "$_inner" }, { "_id", 0 } });

        // Type the manual pipeline's final stage as LeftJoinResult<TOuter,TInner> — the SAME element type the
        // driver's own Queryable.Join produces — so that any downstream operators (OrderBy/Where/etc.) that
        // read LeftJoinResult.Outer/.Inner (rewritten to _outer/_inner member access) translate against a
        // source that actually has those members, and the terminal shaper reads the same root-level
        // _outer/_inner document shape as the driver Join path. The intermediate $project/$lookup/$unwind
        // stages stay BsonDocument-typed; only the result-shaping $project carries the LeftJoinResult
        // serializer (built from the outer/inner entity serializers, mirroring the driver's Join translator).
        var resultType = typeof(LeftJoinResult<,>).MakeGenericType(outerClrType, innerClrType);
        var resultSerializer = BuildLeftJoinResultSerializer(outerClrType, innerClrType, outerEntityType, innerEntityType);

        var query = AppendRawStage(newSource, outerClrType, projectOuter, typeof(BsonDocument));
        query = AppendRawStage(query, typeof(BsonDocument), lookup);
        query = AppendRawStage(query, typeof(BsonDocument), unwind);
        query = AppendRawStage(query, typeof(BsonDocument), projectResult, resultType, resultSerializer);
        return query;
    }

    /// <summary>
    /// Builds an <see cref="IBsonSerializer"/> for <see cref="LeftJoinResult{TOuter,TInner}"/> whose
    /// <c>_outer</c>/<c>_inner</c> members serialize with the provider's entity serializers, mirroring the
    /// serializer the driver's own Join translator synthesizes for its <c>_outer</c>/<c>_inner</c> result
    /// documents. Returning a <see cref="BsonClassMapSerializer{TClass}"/> (an
    /// <c>IBsonDocumentSerializer</c>) lets the driver translate downstream <c>_outer</c>/<c>_inner</c> member
    /// access exactly as for the driver Join path.
    /// </summary>
    private IBsonSerializer BuildLeftJoinResultSerializer(
        Type outerClrType, Type innerClrType, IEntityType outerEntityType, IEntityType innerEntityType)
    {
        var resultType = typeof(LeftJoinResult<,>).MakeGenericType(outerClrType, innerClrType);
        var outerSerializer = _bsonSerializerFactory.GetEntitySerializer(outerEntityType);
        var innerSerializer = _bsonSerializerFactory.GetEntitySerializer(innerEntityType);

        var classMap = new BsonClassMap(resultType);
        classMap.MapMember(resultType.GetProperty(nameof(LeftJoinResult<object, object>._outer))!)
            .SetElementName("_outer").SetSerializer(outerSerializer);
        classMap.MapMember(resultType.GetProperty(nameof(LeftJoinResult<object, object>._inner))!)
            .SetElementName("_inner").SetSerializer(innerSerializer);
        classMap.Freeze();

        var serializerType = typeof(BsonClassMapSerializer<>).MakeGenericType(resultType);
        return (IBsonSerializer)Activator.CreateInstance(serializerType, classMap)!;
    }

    /// <summary>
    /// Resolves the dotted BSON field path for a simple <c>EF.Property(p, "Name")</c> / member-access join
    /// key selector body. Returns <see langword="null"/> for shapes we don't handle (composite/anonymous
    /// keys, conversions, computed keys).
    /// </summary>
    private static string? TryGetKeyFieldPath(Expression keyBody, IEntityType entityType)
    {
        var propertyName = keyBody.TryGetSimplePropertyName();
        if (propertyName == null)
        {
            return null;
        }

        var property = entityType.FindProperty(propertyName);
        if (property == null)
        {
            return null;
        }

        var elementName = property.GetElementName();
        if (property.IsPrimaryKey() && entityType.FindPrimaryKey()?.Properties.Count > 1)
        {
            return $"_id.{elementName}";
        }

        return elementName;
    }

    /// <summary>
    /// Appends a single raw aggregation stage via <see cref="MongoQueryable.AppendStage"/>, keeping the
    /// queryable's element type unless <paramref name="newElementType"/> changes it (used on the final
    /// stage to surface the LeftJoinResult shape downstream). When <paramref name="outSerializer"/> is
    /// supplied it is registered as the output serializer so the driver can introspect the new element type's
    /// members for downstream operators; otherwise the driver resolves the serializer for the output type.
    /// </summary>
    private static Expression AppendRawStage(
        Expression query, Type elementType, BsonDocument stage, Type? newElementType = null,
        IBsonSerializer? outSerializer = null)
    {
        var outType = newElementType ?? elementType;
        var appendStageMethod = typeof(MongoQueryable).GetMethod(nameof(MongoQueryable.AppendStage))!
            .MakeGenericMethod(elementType, outType);
        var outSerializerType = typeof(IBsonSerializer<>).MakeGenericType(outType);
        var stageDefinitionType = typeof(BsonDocumentPipelineStageDefinition<,>).MakeGenericType(elementType, outType);
        var stageConstructor = stageDefinitionType.GetConstructor([typeof(BsonDocument), outSerializerType])!;

        var serializerExpression = Expression.Constant(outSerializer, outSerializerType);

        return Expression.Call(null, appendStageMethod, query,
            Expression.New(stageConstructor,
                Expression.Constant(stage),
                serializerExpression),
            serializerExpression);
    }

    private static readonly MethodInfo QueryableJoinMethod =
        typeof(Queryable).GetMethods()
            .Single(m => m.Name == nameof(Queryable.Join) && m.GetParameters().Length == 5);

    private static readonly MethodInfo QueryableGroupJoinMethod =
        typeof(Queryable).GetMethods()
            .Single(m => m.Name == nameof(Queryable.GroupJoin) && m.GetParameters().Length == 5);

    private static readonly MethodInfo QueryableSelectManyMethod =
        typeof(Queryable).GetMethods()
            .Single(m => m.Name == nameof(Queryable.SelectMany)
                         && m.GetParameters().Length == 3
                         && m.GetParameters()[1].ParameterType.GetGenericArguments()[0]
                             .GetGenericArguments().Length == 2);

    private static readonly MethodInfo EnumerableDefaultIfEmptyMethod =
        typeof(Enumerable).GetMethods()
            .Single(m => m.Name == nameof(Enumerable.DefaultIfEmpty) && m.GetParameters().Length == 1);

    /// <summary>
    /// Builds the canonical driver-translatable left-outer join for the PROJECTED (non-shaped) path:
    /// <code>
    /// outer.GroupJoin(inner, outerKey, innerKey, (o, g) => new LeftJoinResult&lt;TOuter, IEnumerable&lt;TInner&gt;&gt;(o, g))
    ///      .SelectMany(c => c._inner.DefaultIfEmpty(), (c, i) => new LeftJoinResult&lt;TOuter, TInner&gt;(c._outer, i))
    /// </code>
    /// The driver renders this as <c>$lookup</c> (array) + <c>$map</c>/<c>$cond</c> (substituting a single
    /// <see langword="null"/> when the matched array is empty) + <c>$unwind</c>, so principals with no matching
    /// related entity survive — and the terminal element type / document shape is the same
    /// <see cref="LeftJoinResult{TOuter,TInner}"/> (<c>_outer</c>/<c>_inner</c>) the inner-Join path produced,
    /// keeping the downstream result selector and shaper unchanged.
    /// </summary>
    private Expression BuildProjectedLeftOuterJoin(
        MethodCallExpression call, Expression newSource, Type outerType, Type innerType, Type keyType, Type oldOuterType)
    {
        var outerKey = call.Arguments[2].UnwrapLambdaFromQuote();
        var innerKey = call.Arguments[3].UnwrapLambdaFromQuote();

        // When the outer source element type changed (a prior join rewrote it to LeftJoinResult), remap the
        // outer key selector so it reads _outer/_inner from the rewritten source — mirroring the Join path.
        if (outerType != oldOuterType)
        {
            outerKey = RewriteLambdaForLeftJoinResult(outerKey, outerType);
        }

        // GroupJoin carrier: reuse LeftJoinResult<TOuter, IEnumerable<TInner>> (_outer = principal, _inner = group).
        var groupType = typeof(IEnumerable<>).MakeGenericType(innerType);
        var carrierType = typeof(LeftJoinResult<,>).MakeGenericType(outerType, groupType);
        var carrierCtor = carrierType.GetConstructors()[0];
        var groupJoinOuterParam = Expression.Parameter(outerType, "o");
        var groupJoinGroupParam = Expression.Parameter(groupType, "g");
        var groupJoinResultSelector = Expression.Lambda(
            Expression.New(carrierCtor, groupJoinOuterParam, groupJoinGroupParam),
            groupJoinOuterParam, groupJoinGroupParam);

        var groupJoin = Expression.Call(
            null,
            QueryableGroupJoinMethod.MakeGenericMethod(outerType, innerType, keyType, carrierType),
            newSource,
            call.Arguments[1],
            Expression.Quote(outerKey),
            Expression.Quote(innerKey),
            Expression.Quote(groupJoinResultSelector));

        // SelectMany collection selector: c => c._inner.DefaultIfEmpty()
        var carrierParam = Expression.Parameter(carrierType, "c");
        var carrierInner = Expression.Property(carrierParam, nameof(LeftJoinResult<object, object>._inner));
        var defaultIfEmpty = Expression.Call(
            null, EnumerableDefaultIfEmptyMethod.MakeGenericMethod(innerType), carrierInner);
        var collectionSelector = Expression.Lambda(defaultIfEmpty, carrierParam);

        // SelectMany result selector: (c, i) => new LeftJoinResult<TOuter, TInner>(c._outer, i)
        var resultType = typeof(LeftJoinResult<,>).MakeGenericType(outerType, innerType);
        var resultCtor = resultType.GetConstructors()[0];
        var smCarrierParam = Expression.Parameter(carrierType, "c");
        var smInnerParam = Expression.Parameter(innerType, "i");
        var carrierOuter = Expression.Property(smCarrierParam, nameof(LeftJoinResult<object, object>._outer));
        var resultSelector = Expression.Lambda(
            Expression.New(resultCtor, carrierOuter, smInnerParam),
            smCarrierParam, smInnerParam);

        return Expression.Call(
            null,
            QueryableSelectManyMethod.MakeGenericMethod(carrierType, innerType, resultType),
            groupJoin,
            Expression.Quote(collectionSelector),
            Expression.Quote(resultSelector));
    }

    /// <summary>
    /// Rewrite a lambda expression to change its first parameter type from TransparentIdentifier to
    /// LeftJoinResult, mapping field accesses from Outer/Inner to _outer/_inner.
    /// </summary>
    private static LambdaExpression RewriteLambdaForLeftJoinResult(LambdaExpression lambda, Type newParameterType)
    {
        var oldParam = lambda.Parameters[0];
        var newParam = Expression.Parameter(newParameterType, oldParam.Name);
        var body = new TransparentIdentifierToLeftJoinResultRewriter(oldParam, newParam).Visit(lambda.Body);
        var newParams = lambda.Parameters.Count == 1
            ? new[] { newParam }
            : lambda.Parameters.Select((p, i) => i == 0 ? newParam : p).ToArray();
        return Expression.Lambda(body, newParams);
    }

    /// <summary>
    /// Rewrites member accesses from TransparentIdentifier fields (Outer/Inner) to
    /// LeftJoinResult properties (_outer/_inner) when the parameter is replaced.
    /// </summary>
    private sealed class TransparentIdentifierToLeftJoinResultRewriter(
        ParameterExpression oldParam,
        ParameterExpression newParam) : System.Linq.Expressions.ExpressionVisitor
    {
        protected override Expression VisitMember(MemberExpression node)
        {
            // Rewrite TransparentIdentifier field accesses (Outer/Inner) to LeftJoinResult
            // property accesses (_outer/_inner). Check by member declaring type since the
            // expression may be a parameter, another field access, or any rewritten expression.
            if (node.Member.Name is "Outer" or "Inner"
                && node.Member.DeclaringType is { IsGenericType: true } dt
                && dt.Name.StartsWith("TransparentIdentifier"))
            {
                var visitedExpression = Visit(node.Expression!);
                var propertyName = node.Member.Name == "Outer" ? "_outer" : "_inner";
                return Expression.Property(visitedExpression, propertyName);
            }

            return base.VisitMember(node);
        }

        protected override Expression VisitParameter(ParameterExpression node)
            => node.Type == oldParam.Type ? newParam : base.VisitParameter(node);
    }

    /// <summary>
    /// For explicit Join queries, strip the Join chain and return just the base source.
    /// The $lookup stages appended by AppendLookupStages handle the actual join.
    /// <para>
    /// The chain reaching here is flat:
    /// <c>root [.Where/.OrderBy/.Skip/.Take]* (.LeftJoin(...) [.Where/.OrderBy/...]*)+ .Select(...) [.Count()]</c>.
    /// Everything below the innermost join is the base source and survives verbatim. The join nodes
    /// themselves are replaced by the pending <c>$lookup</c> stages, and the EF-synthesized trailing
    /// <c>Select</c> that unpacks the TransparentIdentifier is dropped (the shaper runs client-side).
    /// Any OTHER operator sitting between or above the joins is <b>user-composed</b> and must be
    /// reattached — dropping it silently returns unfiltered/unordered results. Because such an
    /// operator was written against the TransparentIdentifier element type produced by the joins, its
    /// lambdas are rewritten to read the flattened <c>_lookup_&lt;Nav&gt;</c> fields the $lookup stages
    /// produce, and those lookups are recorded in <see cref="_injectedEarlyLookups"/> so they are
    /// emitted immediately above the base source — below the reattached stages, but still above nothing
    /// the user wrote below the joins.
    /// </para>
    /// Returns <see langword="null"/> when the shape cannot be handled. The join then survives in the
    /// returned tree and the callers (<c>TranslateProjected</c> / <c>Translate</c>) fall back to letting
    /// the driver render it natively, suppressing the forced-unwind <c>$lookup</c> stages that would
    /// otherwise duplicate it; if the driver cannot render it either, translation fails there. Falling
    /// back is deliberately preferred over emitting a pipeline whose row set does not match the query.
    /// </summary>
    private Expression? StripJoinForLookup(Expression expression)
    {
        if (expression is not MethodCallExpression outerCall)
            return null;

        // Flatten the chain, outermost first.
        var chain = new List<MethodCallExpression>();
        var node = (Expression)outerCall;
        while (node is MethodCallExpression call
               && call.Method.DeclaringType == typeof(Queryable)
               && call.Arguments.Count > 0)
        {
            chain.Add(call);
            node = call.Arguments[0];
        }

        var innermostJoin = chain.FindLastIndex(IsJoinMethod);

        // This path applies to EVERY join chain, not only all-LeftJoin ones: each pending lookup carries its
        // own PreserveNullAndEmptyArrays taken from the LINQ join operator EF produced (see
        // LookupExpression), so whether EF synthesized the join from a required navigation or the user wrote
        // it directly doesn't matter — both are inner.
        if (innermostJoin >= 0)
        {
            var baseSource = chain[innermostJoin].Arguments[0];
            var baseItemType = baseSource.Type.TryGetItemType();

            // Operators at or above the innermost join that are not join plumbing, innermost-first, paired
            // with their chain index so an operator INTERLEAVED BETWEEN two joins can be told apart from one
            // sitting above every join.
            var composed = new List<MethodCallExpression>();
            var composedIndexes = new List<int>();
            for (var i = innermostJoin - 1; i >= 0; i--)
            {
                if (!IsJoinMethod(chain[i]) && !IsSynthesizedIdentifierSelect(chain[i]))
                {
                    composed.Add(chain[i]);
                    composedIndexes.Add(i);
                }
            }

            if (composed.Count == 0)
            {
                // Nothing user-composed above the joins — the whole chain above the base source is
                // plumbing.
                return baseSource;
            }

            if (baseItemType != null)
            {
                var joinsInnermostFirst = new List<MethodCallExpression>();
                for (var i = chain.Count - 1; i >= 0; i--)
                {
                    if (IsJoinMethod(chain[i]))
                    {
                        joinsInnermostFirst.Add(chain[i]);
                    }
                }

                var rewriter = new TransparentIdentifierToLookupFieldRewriter(
                    baseItemType, joinsInnermostFirst, _pendingLookups, _bsonSerializerFactory, MqlFieldMethodInfo);

                // An operator sitting between two joins has a chain index ABOVE the innermost join (so it is
                // in `composed`) and BELOW the outermost one. When there is no such operator every lookup
                // belongs above the base source and the contiguous group is emitted verbatim.
                var outermostJoin = chain.FindIndex(IsJoinMethod);
                var hasInterleavedOperator = composedIndexes.Any(i => i > outermostJoin);

                if (hasInterleavedOperator)
                {
                    return StripInterleavedJoinChain(chain, innermostJoin, baseSource, rewriter);
                }

                var result = baseSource;
                foreach (var call in composed)
                {
                    var rebuilt = ReattachComposedOperator(call, result, rewriter);
                    if (rebuilt == null)
                    {
                        // Cannot rewrite this operator safely. Refuse the strip so the join survives and
                        // the callers fall back to the driver rendering it natively (see the method
                        // summary) rather than silently ignoring the operator.
                        return null;
                    }

                    result = rebuilt;
                }

                // The reattached stages read the $lookup output fields, so the lookups have to be emitted
                // BELOW them rather than tail-appended after them — but no lower than the base source,
                // which the user wrote below the joins. That distinction is load-bearing: an inner
                // $unwind DROPS rows, so hoisting it above a base-source Skip/Take/Distinct would change
                // which rows those operators see. Emitting immediately after the base source therefore
                // preserves the ordering for every operator the user wrote BELOW the joins.
                //
                // This branch is reached only when NO operator is interleaved between two joins, so every
                // lookup genuinely belongs above the base source and the contiguous group below is correct.
                // The interleaved case is handled by StripInterleavedJoinChain, which splits the group along
                // the join order instead; it stays a separate path so this one keeps a simpler, stable
                // emission order (_pendingLookups order here vs. join order there).
                //
                // Flag ALL of the join-replacing lookups, not just the ones the reattached lambdas read: a
                // transitive lookup's localField can point into an earlier lookup's unwound output (e.g.
                // "_lookup_Customer.region_id"), so splitting them across the reattached stages would break
                // that chain — and any tail-appended remainder would also land after a scalar terminal such
                // as Count. A hop that enters transitive resolution either gets a scoped prefix or declines
                // translation outright; it is never emitted with a silently-missing prefix. The join-hop
                // classification (MongoQueryableMethodTranslatingExpressionVisitor.AnalyzeKeySelectorTarget,
                // via PeelEmbeddedSegments for an owned/embedded receiver — e.g. Orders.Include(o => o.Buyer)
                // .ThenInclude(b => b.Address).ThenInclude(a => a.Region), EF-380) decides root-vs-transitive
                // from the key selector's receiver structure before either root tier runs, so a transitive
                // hop can't be short-circuited by an unrelated root navigation. Verified (EF-407) that this
                // embedded-hop shape resolves correctly end to end — a scoped, correctly-prefixed localField,
                // matching data under Native (fallback) and DriverLinq alike; see
                // NativeReferenceIncludeTests.Deep_ThenInclude_through_embedded_hop_returns_correct_data_via_fallback.
                var contiguousGroup = _pendingLookups.Where(l => l.ForceUnwind).ToList();
                foreach (var lookup in contiguousGroup)
                {
                    _injectedEarlyLookups.Add(lookup);
                }

                _injectAboveNodeLookups[baseSource] = contiguousGroup;

                return result;
            }

            return null;
        }

        // Pre-existing behaviour for explicit-Join chains.
        var explicitBase = FindBaseSourceThroughJoin(outerCall);
        if (explicitBase != null && IsJoinRelatedMethod(outerCall))
        {
            GuardAgainstDiscardedFilter(outerCall, explicitBase, expression);
            return explicitBase;
        }

        var explicitSource = outerCall.Arguments[0];
        explicitBase = FindBaseSourceThroughJoin(explicitSource);
        if (explicitBase == null)
            return null;

        GuardAgainstDiscardedFilter(explicitSource, explicitBase, expression);

        var newArgs = outerCall.Arguments.ToArray();
        newArgs[0] = explicitBase;

        var method = outerCall.Method;
        if (method.IsGenericMethod)
        {
            var explicitItemType = explicitBase.Type.TryGetItemType();
            if (explicitItemType != null)
            {
                var genericDef = method.GetGenericMethodDefinition();
                if (genericDef.GetGenericArguments().Length == 1)
                    method = genericDef.MakeGenericMethod(explicitItemType);
            }
        }

        return Expression.Call(null, method, newArgs);
    }

    /// <summary>
    /// Rebuilds a join chain that has a user-composed operator INTERLEAVED BETWEEN two joins, by splitting
    /// the join-replacing <c>$lookup</c> stages along the join order instead of emitting them as one
    /// contiguous group above the base source.
    /// <para>
    /// A contiguous group is wrong here: emitting it below the reattached operator would let the SECOND
    /// join's row-dropping <c>$unwind</c> run before a <c>Skip</c>/<c>Take</c> the user wrote before that
    /// join existed — a silently wrong page. Each lookup is instead emitted at the reattachment boundary its
    /// own join occupied, so an interleaved operator lands between the two <c>$lookup</c>s.
    /// </para>
    /// <para>
    /// A TRANSITIVE lookup's <c>localField</c> can read an earlier lookup's unwound output (e.g.
    /// <c>"_lookup_Customer.region_id"</c>), forming a dependency chain that cannot be reordered. Splitting
    /// along the JOIN order preserves that chain by construction (a later join can only depend on an
    /// earlier one); <see cref="DependenciesPrecede"/> re-verifies the emitted order against the actual
    /// <c>localField</c> prefixes, and the whole strip is declined if it does not hold.
    /// </para>
    /// <para>
    /// Returns <see langword="null"/> whenever the split cannot be established (an unresolvable/ambiguous
    /// lookup, a dependency-order violation, or an operator that cannot be reattached) — the caller then
    /// leaves the join for the driver to render natively rather than emitting a mispositioned group.
    /// </para>
    /// </summary>
    private Expression? StripInterleavedJoinChain(
        List<MethodCallExpression> chain,
        int innermostJoin,
        Expression baseSource,
        TransparentIdentifierToLookupFieldRewriter rewriter)
    {
        var forceUnwindLookups = _pendingLookups.Where(l => l.ForceUnwind).ToList();
        var groups = new List<(Expression Node, List<LookupExpression> Lookups)>();
        var emissionOrder = new List<LookupExpression>();
        var currentGroup = new List<LookupExpression>();
        var assigned = new HashSet<LookupExpression>();

        // The lookup resolved for the previous (one level inner) join. The loop walks the chain
        // innermost-first, so this is exactly the "previous" DisambiguateJoinLookupByChain needs to break a
        // type/key tie — a self-referencing chain is ambiguous at every hop without it.
        LookupExpression? previousLookup = null;

        var result = baseSource;
        for (var i = innermostJoin; i >= 0; i--)
        {
            var call = chain[i];

            if (IsJoinMethod(call))
            {
                var lookup = ResolveLookupForJoin(call, forceUnwindLookups, previousLookup);
                if (lookup == null || !assigned.Add(lookup))
                {
                    // Unresolvable or double-assigned: no defensible position for this join's lookup.
                    return null;
                }

                previousLookup = lookup;
                currentGroup.Add(lookup);
                continue;
            }

            if (IsSynthesizedIdentifierSelect(call))
            {
                continue;
            }

            if (currentGroup.Count > 0)
            {
                // Everything joined so far has to be in the document before this operator runs.
                groups.Add((result, currentGroup));
                emissionOrder.AddRange(currentGroup);
                currentGroup = [];
            }

            var rebuilt = ReattachComposedOperator(call, result, rewriter);
            if (rebuilt == null)
            {
                return null;
            }

            result = rebuilt;
        }

        // Lookups with no composed operator above them are left to AppendLookupStages, which tail-appends
        // them in _pendingLookups order - the same position and order they would occupy today, and the path
        // that already handles a scalar terminal (it appends below the terminal, not above it).
        emissionOrder.AddRange(forceUnwindLookups.Where(currentGroup.Contains));

        if (assigned.Count != forceUnwindLookups.Count || !DependenciesPrecede(emissionOrder))
        {
            return null;
        }

        foreach (var (node, lookups) in groups)
        {
            // Add, not indexer assignment: two groups keyed on the same node would mean one join's lookups
            // silently replacing another's, i.e. a $lookup vanishing from the pipeline and unjoined rows
            // being returned. Everything else on this path is fail-closed; this keeps that property here too.
            _injectAboveNodeLookups.Add(node, lookups);
            foreach (var lookup in lookups)
            {
                _injectedEarlyLookups.Add(lookup);
            }
        }

        return result;
    }

    /// <summary>
    /// The one resolver for "which <c>$lookup</c> stands in for this join operator": matched on the join's
    /// inner entity CLR type AND its outer key (the FK property), so two navigations to the same entity type
    /// stay distinguishable. Only <c>ForceUnwind</c> lookups are considered. Returns <see langword="null"/>
    /// when the match is missing or ambiguous.
    /// </summary>
    /// <remarks>
    /// Shared by the two sites that MUST agree about which lookup a join owns: the group split in
    /// <see cref="StripInterleavedJoinChain"/> (which decides WHERE that lookup is emitted) and
    /// <see cref="TransparentIdentifierToLookupFieldRewriter.ResolveLookup"/> (which decides WHICH field the
    /// reattached lambdas READ). A future edit to only one would desynchronise the position from the read —
    /// a <c>$lookup</c> on the wrong side of an interleaved operator, i.e. a silently wrong page.
    /// </remarks>
    /// <param name="joinCall">The join operator whose lookup is wanted.</param>
    /// <param name="innerType">
    /// The join's inner entity CLR type, passed in rather than re-derived here because the two call sites
    /// have different authoritative inputs for it: the rewriter has the TransparentIdentifier's
    /// <c>Inner</c> MEMBER type (what the lambda being rewritten actually reads), while the group split has
    /// the join's inner SEQUENCE type. EF Core's own nav-expansion normalizes every join onto its own
    /// TransparentIdentifier result selector before this bridge sees the chain, so in practice the two types
    /// always agree; each site still reads the input that is authoritative for it, in case that ever
    /// changes.
    /// </param>
    /// <param name="candidates">The lookups to match against.</param>
    /// <param name="previous">
    /// The lookup resolved for the join one level INNER of this one, or <see langword="null"/> when this is
    /// the innermost join. Only consulted when type+key matching leaves more than one candidate — see
    /// <see cref="DisambiguateJoinLookupByChain"/>. Threading it is what keeps a SELF-REFERENCING chain
    /// (<c>Employee.Manager.Manager</c>, where every hop has the same target type AND the same foreign key)
    /// resolvable; without it such a chain is ambiguous at every hop and the whole strip declines.
    /// </param>
    private static LookupExpression? ResolveJoinLookup(
        MethodCallExpression joinCall,
        Type innerType,
        IReadOnlyList<LookupExpression> candidates,
        LookupExpression? previous)
    {
        if (joinCall.Arguments.Count < 3)
        {
            return null;
        }

        var keyName = joinCall.Arguments[2].UnwrapLambdaFromQuote().Body.TryGetSimplePropertyName();

        // TargetEntityType, not Navigation.TargetEntityType: a Join hop with no model navigation (EF-377)
        // carries a null Navigation, and TargetEntityType is set by BOTH LookupExpression constructors. Such
        // a hop also has no ForeignKey to narrow by, so it matches on inner CLR type alone; any residual
        // ambiguity is settled structurally below, exactly as for a self-referencing navigation chain.
        var matches = candidates
            .Where(l => l.ForceUnwind
                        && l.TargetEntityType.ClrType == innerType
                        && (keyName == null
                            || l.Navigation is null
                            || l.Navigation.ForeignKey.Properties.Any(p => p.Name == keyName)
                            || l.Navigation.ForeignKey.PrincipalKey.Properties.Any(p => p.Name == keyName)))
            .ToList();

        return DisambiguateJoinLookupByChain(matches, previous);
    }

    /// <summary>
    /// Settles a type/key-ambiguous lookup match structurally, using the <c>localField</c> dependency
    /// relation <c>OrderLookupsByDependency</c> already relies on.
    /// </summary>
    /// <remarks>
    /// Ambiguity is not exotic: a self-referencing chain (<c>Employee.Manager.Manager</c>) registers one hop
    /// per level against the SAME navigation and the SAME target entity type, so type+key narrowing cannot
    /// separate them, and a navigation-less hop (EF-377) has no foreign key to narrow by at all. The
    /// structure does separate them: the innermost hop's <c>$lookup</c> reads straight off the root document,
    /// so its <c>localField</c> is not prefixed by any sibling candidate's alias, while every deeper hop's
    /// <c>localField</c> is chained onto the alias of the hop immediately before it. Returning
    /// <see langword="null"/> rather than guessing keeps the caller's decline fail-closed.
    /// </remarks>
    private static LookupExpression? DisambiguateJoinLookupByChain(
        List<LookupExpression> matches, LookupExpression? previous)
        => matches.Count switch
        {
            1 => matches[0],
            0 => null,
            _ => previous == null
                ? matches.FirstOrDefault(candidate => matches.All(other =>
                    ReferenceEquals(other, candidate)
                    || !candidate.LocalField.StartsWith(other.As + ".", StringComparison.Ordinal)))
                : matches.FirstOrDefault(candidate =>
                    candidate.LocalField.StartsWith(previous.As + ".", StringComparison.Ordinal))
        };

    /// <summary>
    /// Resolves the <c>$lookup</c> for one join in the chain being split, deriving the inner entity CLR type
    /// from the join's inner SEQUENCE (see <see cref="ResolveJoinLookup"/> for why the derivation lives at
    /// the call site rather than in the shared resolver).
    /// </summary>
    private static LookupExpression? ResolveLookupForJoin(
        MethodCallExpression joinCall, IReadOnlyList<LookupExpression> candidates, LookupExpression? previous)
    {
        var innerType = joinCall.Arguments.Count > 1 ? joinCall.Arguments[1].Type.TryGetItemType() : null;

        return innerType == null ? null : ResolveJoinLookup(joinCall, innerType, candidates, previous);
    }

    /// <summary>
    /// Whether the given emission order respects the <c>localField</c> dependency chain: a lookup whose
    /// <c>localField</c> reads another lookup's unwound output (<c>"&lt;other.As&gt;."</c> prefix) must be
    /// emitted after it. This is the invariant the contiguous group preserved for free, re-checked here
    /// because the interleaved path splits the group.
    /// </summary>
    /// <remarks>
    /// A fail-closed re-check with no known reachable violating input: splitting along the JOIN order
    /// preserves the chain by construction (a later join can only read an earlier join's output), so this
    /// is expected to hold for every shape. Kept <c>internal</c> so its ordering logic can be pinned
    /// directly by a unit test.
    /// </remarks>
    internal static bool DependenciesPrecede(List<LookupExpression> emissionOrder)
    {
        for (var i = 0; i < emissionOrder.Count; i++)
        {
            for (var j = i + 1; j < emissionOrder.Count; j++)
            {
                if (emissionOrder[i].LocalField.StartsWith(emissionOrder[j].As + ".", StringComparison.Ordinal))
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Prevents <see cref="StripJoinForLookup"/> from silently discarding a <c>Where</c> while peeling a
    /// join chain to its base source — that would return the unfiltered cross product with no error, so
    /// this fails translation instead (mirrors EF-X021's filtered-Include guard). A <c>Where</c> at or
    /// below <paramref name="baseSource"/> becomes a leading <c>$match</c> and is unaffected. Translating
    /// rather than rejecting the discarded filter is tracked as EF-X024.
    /// </summary>
    private static void GuardAgainstDiscardedFilter(
        Expression discardedFrom, Expression baseSource, Expression fullQuery)
    {
        var node = discardedFrom;
        while (!ReferenceEquals(node, baseSource) && node is MethodCallExpression call)
        {
            if (call.Method.Name == "Where" && call.Arguments.Count >= 2)
            {
                throw new InvalidOperationException(
                    Microsoft.EntityFrameworkCore.Diagnostics.CoreStrings.TranslationFailed(fullQuery.Print()));
            }

            if (call.Arguments.Count == 0)
            {
                break;
            }

            node = call.Arguments[0];
        }
    }

    private static bool IsJoinRelatedMethod(MethodCallExpression call)
        => call.Method.Name is "Select" or "LeftJoin" or "Join" or "GroupJoin" or "SelectMany" or "Where";

    /// <summary>
    /// Rebuilds one user-composed operator on top of <paramref name="newSource"/> (whose element type is
    /// the root entity), rewriting its TransparentIdentifier-typed lambdas via
    /// <paramref name="rewriter"/>. Returns <see langword="null"/> for anything it cannot rewrite.
    /// </summary>
    private static Expression? ReattachComposedOperator(
        MethodCallExpression call, Expression newSource, TransparentIdentifierToLookupFieldRewriter rewriter)
    {
        var newSourceItemType = newSource.Type.TryGetItemType();
        if (newSourceItemType == null || !call.Method.IsGenericMethod)
        {
            return null;
        }

        var oldSourceItemType = call.Arguments[0].Type.TryGetItemType();
        if (oldSourceItemType == null)
        {
            return null;
        }

        var newArgs = call.Arguments.ToArray();
        newArgs[0] = newSource;

        for (var i = 1; i < newArgs.Length; i++)
        {
            if (newArgs[i] is not UnaryExpression { NodeType: ExpressionType.Quote, Operand: LambdaExpression lambda })
            {
                continue;
            }

            if (lambda.Parameters.Count != 1 || lambda.Parameters[0].Type != oldSourceItemType)
            {
                return null;
            }

            var rewritten = rewriter.RewriteLambda(lambda, newSourceItemType);
            if (rewritten == null)
            {
                return null;
            }

            newArgs[i] = Expression.Quote(rewritten);
        }

        var genericArgs = call.Method.GetGenericArguments().ToArray();
        for (var i = 0; i < genericArgs.Length; i++)
        {
            if (genericArgs[i] == oldSourceItemType)
            {
                genericArgs[i] = newSourceItemType;
            }
        }

        // Only a genuine residual TransparentIdentifier reference is a problem. A composed operator
        // positioned ABOVE the synthesized flattening Select (e.g. the 2-arg Queryable.First(source,
        // predicate) that Include(o => o.Carrier).First(o => o.Carrier == null) folds into — see
        // IsSynthesizedIdentifierSelect) has an oldSourceItemType that is ALREADY the flattened root entity
        // type, not the join's TransparentIdentifier, because its own immediate source in the original
        // captured chain is that synthesized Select, not the join. So oldSourceItemType can equal
        // newSourceItemType (both the flattened root type, e.g. Order) even though none of this operator's
        // generic arguments ever mentioned the TransparentIdentifier — a plain "generic arg still equals
        // oldSourceItemType" check would false-positive there, refusing the strip and leaving the join to
        // be rendered by the driver's own native LeftJoin, a shape the shaper (already committed to the
        // flat _lookup_<Nav> layout before this rewrite runs) cannot read — corrupting results rather than
        // merely missing an optimization. Gating on IsTransparentIdentifierType(oldSourceItemType) restores
        // the intent: decline only when a TI-shaped generic argument genuinely couldn't be eliminated by
        // the substitution above.
        if (oldSourceItemType.IsTransparentIdentifierType()
            && (genericArgs.Contains(oldSourceItemType) || genericArgs.Any(a => ContainsType(a, oldSourceItemType))))
        {
            // A generic argument still mentions the (now non-existent) TransparentIdentifier type. When
            // oldSourceItemType isn't a TransparentIdentifier to begin with (e.g. a terminal First()/
            // Single() sitting above an Include-shaping Select that already collapsed the join's
            // TransparentIdentifier to the entity type), old and new legitimately coincide and this must
            // not reject the reattachment.
            return null;
        }

        try
        {
            return Expression.Call(null, call.Method.GetGenericMethodDefinition().MakeGenericMethod(genericArgs), newArgs);
        }
        // Narrowly the two exception types the reconstruction above can legitimately raise for a shape this
        // method cannot rebuild: MakeGenericMethod throws ArgumentException when a substituted type argument
        // violates the method's constraints, and Expression.Call throws ArgumentException /
        // InvalidOperationException when the rewritten arguments no longer match the constructed signature.
        // Anything else is a bug here and must not be laundered into an ordinary shape rejection.
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static bool ContainsType(Type candidate, Type target)
    {
        if (candidate == target)
        {
            return true;
        }

        return candidate.IsGenericType && candidate.GetGenericArguments().Any(a => ContainsType(a, target));
    }

    private static bool IsJoinMethod(MethodCallExpression call)
        => call.Method.Name is "LeftJoin" or "Join" or "GroupJoin";

    /// <summary>
    /// Distinguishes the EF-synthesized trailing <c>Select</c> that merely unpacks the join's
    /// TransparentIdentifier back to the root entity (join plumbing the <c>$lookup</c> replaces, so it
    /// must be dropped) from a user-composed <c>Select</c> projection (which must be reattached).
    /// The synthesized one is recognised structurally: its selector body is either an
    /// <see cref="IncludeExpression"/> (the shaped Include path) or a bare chain of <c>.Outer</c> field
    /// accesses back to the parameter — neither of which a user selector can be.
    /// </summary>
    private static bool IsSynthesizedIdentifierSelect(MethodCallExpression call)
    {
        if (call.Method.Name != "Select"
            || call.Arguments.Count < 2
            || call.Arguments[1] is not UnaryExpression { NodeType: ExpressionType.Quote, Operand: LambdaExpression selector }
            || selector.Parameters.Count != 1
            || !selector.Parameters[0].Type.IsTransparentIdentifierType())
        {
            return false;
        }

        var body = selector.Body;
        while (true)
        {
            switch (body)
            {
                case IncludeExpression include:
                    body = include.EntityExpression;
                    continue;
                case MemberExpression { Member.Name: "Outer" } member
                    when member.Member.DeclaringType.IsTransparentIdentifierType():
                    body = member.Expression!;
                    continue;
                case ParameterExpression parameter:
                    return parameter == selector.Parameters[0];
                default:
                    return false;
            }
        }
    }

    /// <summary>
    /// Rewrites a lambda written against a join's TransparentIdentifier element type so it reads from the
    /// flattened document the <c>$lookup</c> stages produce: <c>.Outer</c> chains collapse to the root
    /// document and each <c>.Inner</c> becomes an <c>Mql.Field(root, "_lookup_&lt;Nav&gt;", serializer)</c>
    /// read. Sets <see cref="Failed"/> (and the caller bails) for anything it cannot resolve
    /// unambiguously.
    /// </summary>
    private sealed class TransparentIdentifierToLookupFieldRewriter
    {
        private readonly Type _rootType;
        private readonly List<MethodCallExpression> _joinsInnermostFirst;
        private readonly IReadOnlyList<LookupExpression> _pendingLookups;
        private readonly BsonSerializerFactory _bsonSerializerFactory;
        private readonly MethodInfo _mqlFieldMethod;
        private ParameterExpression _rootParam;

        public TransparentIdentifierToLookupFieldRewriter(
            Type rootType,
            List<MethodCallExpression> joinsInnermostFirst,
            IReadOnlyList<LookupExpression> pendingLookups,
            BsonSerializerFactory bsonSerializerFactory,
            MethodInfo mqlFieldMethod)
        {
            _rootType = rootType;
            _joinsInnermostFirst = joinsInnermostFirst;
            _pendingLookups = pendingLookups;
            _bsonSerializerFactory = bsonSerializerFactory;
            _mqlFieldMethod = mqlFieldMethod;
            _rootParam = Expression.Parameter(rootType, "e");
        }

        public bool Failed { get; private set; }

        public LambdaExpression? RewriteLambda(LambdaExpression lambda, Type newParameterType)
        {
            if (newParameterType != _rootType)
            {
                return null;
            }

            Failed = false;
            _rootParam = Expression.Parameter(_rootType, lambda.Parameters[0].Name);
            var visitor = new Rewriter(this, lambda.Parameters[0]);
            var body = visitor.Visit(lambda.Body);
            return Failed ? null : Expression.Lambda(body, _rootParam);
        }

        /// <summary>
        /// Resolves the <c>$lookup</c> that supplies the <c>Inner</c> of the TransparentIdentifier at
        /// <paramref name="depth"/> (0 = innermost join), through the shared
        /// <see cref="ResolveJoinLookup"/> — the same match the group split uses, deliberately not a second
        /// copy of it. The inner entity CLR type is the <c>Inner</c> MEMBER type the lambda actually reads
        /// (see that method's remarks). Returns <see langword="null"/> when missing or ambiguous.
        /// </summary>
        private LookupExpression? ResolveLookup(int depth, Type innerType)
            => depth < 0 || depth >= _joinsInnermostFirst.Count
                ? null
                : ResolveJoinLookup(
                    _joinsInnermostFirst[depth],
                    innerType,
                    _pendingLookups,
                    // The hop one level inner of this one, needed only to break a type/key tie — see
                    // DisambiguateJoinLookupByChain. Recursion terminates at depth 0, which passes null.
                    depth == 0 ? null : ResolveLookup(depth - 1, innerType));

        private sealed class Rewriter(TransparentIdentifierToLookupFieldRewriter owner, ParameterExpression oldParam)
            : System.Linq.Expressions.ExpressionVisitor
        {
            protected override Expression VisitMember(MemberExpression node)
            {
                if (node.Member.Name is "Outer" or "Inner"
                    && node.Member.DeclaringType.IsTransparentIdentifierType()
                    && TryGetDepth(node.Expression, out var depth))
                {
                    if (node.Member.Name == "Outer")
                    {
                        // .Outer at depth d is the source of join d: either the TI of join d-1
                        // (handled by the caller re-entering here) or the root document.
                        return depth == 0 ? owner._rootParam : Descend(depth - 1);
                    }

                    var lookup = owner.ResolveLookup(depth, node.Type);
                    if (lookup == null)
                    {
                        owner.Failed = true;
                        return node;
                    }

                    // TargetEntityType, not Navigation.TargetEntityType: a navigation-less Join hop (EF-377)
                    // has a null Navigation, and both LookupExpression constructors set TargetEntityType.
                    var serializer = owner._bsonSerializerFactory.GetEntitySerializer(lookup.TargetEntityType);
                    var mqlField = owner._mqlFieldMethod.MakeGenericMethod(owner._rootType, node.Type);
                    return Expression.Call(null, mqlField, owner._rootParam,
                        Expression.Constant(lookup.As),
                        Expression.Constant(serializer, typeof(IBsonSerializer<>).MakeGenericType(node.Type)));
                }

                return base.VisitMember(node);
            }

            protected override Expression VisitParameter(ParameterExpression node)
            {
                if (node == oldParam)
                {
                    // A bare TransparentIdentifier reference (e.g. projecting the whole identifier) has
                    // no flattened equivalent.
                    owner.Failed = true;
                    return node;
                }

                return base.VisitParameter(node);
            }

            /// <summary>
            /// Depth of the TransparentIdentifier an expression denotes: the lambda's parameter is the
            /// identifier produced by join number (its own nesting depth - 1) — a lambda composed BETWEEN
            /// two joins sees a shallower identifier than one composed above both — and each
            /// <c>.Outer</c> hop moves one level inwards.
            /// </summary>
            private bool TryGetDepth(Expression? expression, out int depth)
            {
                depth = NestingDepth(oldParam.Type) - 1;
                while (true)
                {
                    switch (expression)
                    {
                        case ParameterExpression p when p == oldParam:
                            return depth >= 0;
                        case MemberExpression { Member.Name: "Outer" } m
                            when m.Member.DeclaringType.IsTransparentIdentifierType():
                            depth--;
                            expression = m.Expression;
                            continue;
                        default:
                            return false;
                    }
                }
            }

            /// <summary>Never reached for a valid tree: <c>.Outer</c> of depth d &gt; 0 is itself a
            /// TransparentIdentifier, so the enclosing member access resolves it.</summary>
            private Expression Descend(int depth)
            {
                _ = depth;
                owner.Failed = true;
                return owner._rootParam;
            }

            /// <summary>Number of nested TransparentIdentifier levels in a join element type.</summary>
            private static int NestingDepth(Type type)
                => type.IsTransparentIdentifierType() ? 1 + NestingDepth(type.GetGenericArguments()[0]) : 0;
        }
    }

    private static Expression? FindBaseSourceThroughJoin(Expression expression)
    {
        if (expression is not MethodCallExpression call)
            return null;

        if (call.Method.Name == "Select" && call.Arguments.Count >= 2)
            return FindBaseSourceThroughJoin(call.Arguments[0]);

        if (call.Method.Name is "LeftJoin" or "Join" or "GroupJoin")
            return FindBaseSourceThroughJoin(call.Arguments[0]) ?? call.Arguments[0];

        if (call.Method.Name == "SelectMany")
            return FindBaseSourceThroughJoin(call.Arguments[0]);

        if (call.Method.Name == "Where" && call.Arguments.Count >= 2)
            return FindBaseSourceThroughJoin(call.Arguments[0]);

        return null;
    }

    /// <summary>
    /// Appends $lookup stages for cross-collection collection Includes.
    /// Uses the same AppendStage pattern as VectorSearch.
    /// </summary>
    private Expression AppendLookupStages(Expression query)
        // Tail-append every pending $lookup except those flagged to be injected right after the root source
        // (projected collection-navigation counts) - those are emitted by InjectAfterRootLookupStages.
        // forceUnwind lookups are suppressed when a surviving native Join already supplies the join (see
        // _appendForceUnwindLookups).
        => EmitLookupStages(query,
            _pendingLookups.Where(l => !IsInjectedEarly(l) && (_appendForceUnwindLookups || !l.ForceUnwind)));

    /// <summary>
    /// Emit the $lookup stages flagged <see cref="LookupExpression.InjectAfterRoot"/> immediately after the
    /// root collection source, so the user's downstream pipeline stages (e.g. a <c>$match</c>/<c>$project</c>
    /// that reads the <c>_lookup_&lt;Nav&gt;</c> array via <c>{ $size: ... }</c>) see the array already present.
    /// </summary>
    private Expression InjectAfterRootLookupStages(Expression query)
        => EmitLookupStages(query, _pendingLookups.Where(l => l.InjectAfterRoot));

    /// <summary>
    /// Whether a lookup is emitted somewhere below the tail of the pipeline, and so must not also be
    /// tail-appended: either because the projection binder marked it for injection right after the root
    /// source at model/compile time (<see cref="LookupExpression.InjectAfterRoot"/>), or because THIS
    /// execution's <see cref="StripJoinForLookup"/> reattached user-composed operators that read its
    /// output field and so scheduled it above the join chain's base source
    /// (<see cref="_injectedEarlyLookups"/>). The latter is kept as per-execution visitor state
    /// rather than written back onto the shared <see cref="LookupExpression"/>, which is compile-time
    /// state owned by <see cref="Expressions.MongoQueryExpression"/> and reused across executions.
    /// </summary>
    private bool IsInjectedEarly(LookupExpression lookup)
        => lookup.InjectAfterRoot || _injectedEarlyLookups.Contains(lookup);

    private Expression EmitLookupStages(Expression query, IEnumerable<LookupExpression> lookups)
    {
        var lookupList = lookups as IReadOnlyList<LookupExpression> ?? lookups.ToList();
        if (lookupList.Count == 0)
        {
            return query;
        }

        var sourceType = query.Type.TryGetItemType() ?? _source.Type.TryGetItemType()!;
        var appendStageMethod = typeof(MongoQueryable).GetMethod(nameof(MongoQueryable.AppendStage))!
            .MakeGenericMethod(sourceType, sourceType);
        var serializerType = typeof(IBsonSerializer<>).MakeGenericType(sourceType);
        var stageDefinitionType = typeof(BsonDocumentPipelineStageDefinition<,>).MakeGenericType(sourceType, sourceType);
        var stageConstructor = stageDefinitionType.GetConstructor([typeof(BsonDocument), serializerType])!;

        foreach (var lookup in lookupList)
        {
            var lookupDoc = lookup.ToLookupStageDocument();

            query = Expression.Call(null, appendStageMethod, query,
                Expression.New(stageConstructor,
                    Expression.Constant(lookupDoc),
                    Expression.Constant(null, serializerType)),
                Expression.Constant(null, serializerType));

            if (lookup.ShouldUnwind)
            {
                // Left-outer vs inner comes from the LINQ operator the lookup was registered for (see
                // LookupExpression.PreserveNullAndEmptyArrays): an Include or LeftJoin/GroupJoin preserves
                // the principal, a plain Join - including EF's lowering of a REQUIRED reference navigation -
                // drops it when the foreign key matched nothing.
                var unwindDoc = new BsonDocument("$unwind", new BsonDocument
                {
                    { "path", $"${lookup.As}" },
                    { "preserveNullAndEmptyArrays", lookup.PreserveNullAndEmptyArrays }
                });

                query = Expression.Call(null, appendStageMethod, query,
                    Expression.New(stageConstructor,
                        Expression.Constant(unwindDoc),
                        Expression.Constant(null, serializerType)),
                    Expression.Constant(null, serializerType));
            }
        }

        return query;
    }
}
