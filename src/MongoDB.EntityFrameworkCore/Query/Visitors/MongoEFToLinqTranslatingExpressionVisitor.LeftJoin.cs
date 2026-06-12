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

// TODO(EF-317): Join rewriting for the C# driver's LINQ provider. This file groups the join-handling
// helpers so the LeftJoin-related code is visible in one place ahead of native driver LeftJoin support.
// It mixes two concerns that will diverge when the driver ships that support:
//   * Driver-native LeftJoin rewrite (EXPECTED TO REMAIN / SIMPLIFY): StripOuterSelectForJoin,
//     RewriteLeftJoins, RewriteJoinNode, TryBuildDriverNativeLeftJoinPipeline, BuildLeftJoinResultSerializer,
//     BuildProjectedLeftOuterJoin, RewriteLambdaForLeftJoinResult, TransparentIdentifierToLeftJoinResultRewriter,
//     TryGetKeyFieldPath, AppendRawStage. These rewrite EF's LeftJoin into
//     the driver's Join (the driver has no LeftJoin translator today); they stay relevant but should shrink
//     once the driver accepts LeftJoin directly.
//   * $lookup fallback plumbing (EXPECTED TO BE REMOVED): StripJoinForLookup, IsJoinRelatedMethod,
//     FindBaseSourceThroughJoin, and the $lookup-stage emission (AppendLookupStages,
//     InjectAfterRootLookupStages, EmitLookupStages). These peel a Join chain back to its root and emit
//     the manual $lookup + $unwind stages that stand in where the driver join cannot express the shape.
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
    /// </summary>
    private static Expression? StripJoinForLookup(Expression expression)
    {
        if (expression is not MethodCallExpression outerCall)
            return null;

        var baseSource = FindBaseSourceThroughJoin(outerCall);
        if (baseSource != null && IsJoinRelatedMethod(outerCall))
            return baseSource;

        var source = outerCall.Arguments[0];
        baseSource = FindBaseSourceThroughJoin(source);
        if (baseSource == null)
            return null;

        var newArgs = outerCall.Arguments.ToArray();
        newArgs[0] = baseSource;

        var method = outerCall.Method;
        if (method.IsGenericMethod)
        {
            var baseItemType = baseSource.Type.TryGetItemType();
            if (baseItemType != null)
            {
                var genericDef = method.GetGenericMethodDefinition();
                if (genericDef.GetGenericArguments().Length == 1)
                    method = genericDef.MakeGenericMethod(baseItemType);
            }
        }

        return Expression.Call(null, method, newArgs);
    }

    private static bool IsJoinRelatedMethod(MethodCallExpression call)
        => call.Method.Name is "Select" or "LeftJoin" or "Join" or "GroupJoin" or "SelectMany" or "Where";

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
            _pendingLookups.Where(l => !l.InjectAfterRoot && (_appendForceUnwindLookups || !l.ForceUnwind)));

    /// <summary>
    /// Emit the $lookup stages flagged <see cref="LookupExpression.InjectAfterRoot"/> immediately after the
    /// root collection source, so the user's downstream pipeline stages (e.g. a <c>$match</c>/<c>$project</c>
    /// that reads the <c>_lookup_&lt;Nav&gt;</c> array via <c>{ $size: ... }</c>) see the array already present.
    /// </summary>
    private Expression InjectAfterRootLookupStages(Expression query)
        => EmitLookupStages(query, _pendingLookups.Where(l => l.InjectAfterRoot));

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
            BsonDocument lookupDoc;
            if (lookup.HasPipeline)
            {
                // Pipeline form: used for filtered Includes (OrderBy, Skip, Take on the included collection).
                var pipeline = new BsonArray
                {
                    new BsonDocument("$match",
                        new BsonDocument("$expr",
                            new BsonDocument("$eq", new BsonArray { $"${lookup.ForeignField}", "$$localField" })))
                };
                foreach (var stage in lookup.PipelineStages)
                {
                    pipeline.Add(stage);
                }

                lookupDoc = new BsonDocument("$lookup", new BsonDocument
                {
                    { "from", lookup.From },
                    { "let", new BsonDocument("localField", $"${lookup.LocalField}") },
                    { "pipeline", pipeline },
                    { "as", lookup.As }
                });
            }
            else
            {
                lookupDoc = new BsonDocument("$lookup", new BsonDocument
                {
                    { "from", lookup.From },
                    { "localField", lookup.LocalField },
                    { "foreignField", lookup.ForeignField },
                    { "as", lookup.As }
                });
            }

            query = Expression.Call(null, appendStageMethod, query,
                Expression.New(stageConstructor,
                    Expression.Constant(lookupDoc),
                    Expression.Constant(null, serializerType)),
                Expression.Constant(null, serializerType));

            if (lookup.ShouldUnwind)
            {
                var unwindDoc = new BsonDocument("$unwind", new BsonDocument
                {
                    { "path", $"${lookup.As}" },
                    { "preserveNullAndEmptyArrays", true }
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
