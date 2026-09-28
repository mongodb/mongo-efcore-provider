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

// Join rewriting for the C# driver's LINQ provider. Two concerns:
//   * Driver-native LeftJoin rewrite (StripOuterSelectForJoin, RewriteLeftJoins, RewriteJoinNode,
//     TryBuildDriverNativeLeftJoinPipeline, ...): rewrites EF's LeftJoin into the driver's Join, since the
//     driver has no LeftJoin translator.
//   * $lookup fallback (StripJoinForLookup, IsJoinRelatedMethod, FindBaseSourceThroughJoin, AppendLookupStages,
//     InjectAfterRootLookupStages, EmitLookupStages): peels a Join chain back to its root and emits manual
//     $lookup + $unwind stages where the driver join can't express the shape.
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
        if (expression is not MethodCallExpression call
            || (call.Method.DeclaringType != typeof(Queryable)
                && !MongoQueryableMethodTranslatingExpressionVisitor.IsEf8Ef9LeftJoinShim(call.Method)))
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
        var unwind = LookupExpression.UnwindStageDocument("_inner", preserveNullAndEmptyArrays: true);
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
    /// For explicit Join queries, strip the Join chain and return just the base source; the <c>$lookup</c>
    /// stages appended by AppendLookupStages do the join.
    /// <para>
    /// Chain shape: <c>root [ops]* (.LeftJoin(...) [ops]*)+ .Select(...) [.Count()]</c>. The base source survives
    /// verbatim and EF's trailing TransparentIdentifier-unpacking <c>Select</c> is dropped. Any other operator
    /// between or above the joins is user-composed and must be reattached (dropping it silently returns
    /// unfiltered/unordered rows); its lambdas are rewritten to read <c>_lookup_&lt;Nav&gt;</c> fields, and those
    /// lookups go in <see cref="_injectedEarlyLookups"/> to be emitted right above the base source.
    /// </para>
    /// Returns <see langword="null"/> when the shape can't be handled; the join is then left for the driver to
    /// render (or fail), which is preferred over emitting a pipeline with the wrong row set.
    /// </summary>
    private Expression? StripJoinForLookup(Expression expression)
    {
        if (expression is not MethodCallExpression outerCall)
            return null;

        // Flatten the chain, outermost first.
        var chain = new List<MethodCallExpression>();
        var node = (Expression)outerCall;
        while (node is MethodCallExpression call
               && (call.Method.DeclaringType == typeof(Queryable)
                   || MongoQueryableMethodTranslatingExpressionVisitor.IsEf8Ef9LeftJoinShim(call.Method))
               && call.Arguments.Count > 0)
        {
            chain.Add(call);
            node = call.Arguments[0];
        }

        var innermostJoin = chain.FindLastIndex(IsJoinMethod);

        // Applies to every join chain, not only LeftJoin: each pending lookup carries its own
        // PreserveNullAndEmptyArrays from the join operator EF produced (see LookupExpression).
        if (innermostJoin >= 0)
        {
            var baseSource = chain[innermostJoin].Arguments[0];
            var baseItemType = baseSource.Type.TryGetItemType();

            // Non-plumbing operators at or above the innermost join, innermost-first, with their chain index so
            // an operator interleaved between two joins can be told apart from one above every join.
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

                // An interleaved operator sits above the innermost join and below the outermost one. Without
                // one, every lookup belongs above the base source as a contiguous group.
                var outermostJoin = chain.FindIndex(IsJoinMethod);
                var hasInterleavedOperator = composedIndexes.Any(i => i > outermostJoin);

                if (hasInterleavedOperator)
                {
                    return StripInterleavedJoinChain(chain, innermostJoin, baseSource, rewriter);
                }

                var contiguousGroup = _pendingLookups.Where(l => l.ForceUnwind).ToList();

                // Whether the element is still the flattened root document (the bare Outer leaf), and, once a
                // bare Inner leaf Select has been reattached, the lookup field it reads. See
                // _bareInnerJoinLeafAlias and ExcludeJoinFieldsBeforeDistinct.
                // An Include-shaping Select (Select(ti => Include(ti.Outer, ti.Inner))) is plumbing too, but its
                // entity DOES read the join field, so it must survive a Distinct.
                var elementIsRootDocument = !chain.Any(IsIncludeShapingSelect);
                string? bareInnerJoinLeafAlias = null;

                var result = baseSource;
                foreach (var call in composed)
                {
                    var source = result;
                    if (elementIsRootDocument && IsQueryableDistinct(call))
                    {
                        var excluded = ExcludeJoinFieldsBeforeDistinct(result, contiguousGroup);
                        if (excluded == null)
                        {
                            return null;
                        }

                        source = excluded;
                    }

                    var rebuilt = ReattachComposedOperator(call, source, rewriter);
                    if (rebuilt == null)
                    {
                        // Cannot rewrite this operator safely. Refuse the strip so the join survives and
                        // the callers fall back to the driver rendering it natively (see the method
                        // summary) rather than silently ignoring the operator.
                        return null;
                    }

                    if (!IsElementPreservingOperator(call))
                    {
                        elementIsRootDocument = false;
                        bareInnerJoinLeafAlias = TryGetBareLookupFieldSelectAlias(rebuilt);
                    }

                    result = rebuilt;
                }

                _bareInnerJoinLeafAlias = bareInnerJoinLeafAlias;

                // The reattached stages read $lookup output, so the lookups go below them but no lower than the
                // base source: an inner $unwind drops rows, so hoisting it above a base-source Skip/Take/Distinct
                // would change which rows those see. The interleaved case is handled separately by
                // StripInterleavedJoinChain.
                //
                // Flag all join-replacing lookups, not only those the lambdas read: a transitive lookup's
                // localField can point into an earlier lookup's output ("_lookup_Customer.region_id"), so
                // splitting them would break the chain, and a tail-appended remainder could land after a scalar
                // terminal like Count. Root-vs-transitive classification happens earlier in
                // AnalyzeKeySelectorTarget; see
                // NativeReferenceIncludeTests.Deep_ThenInclude_through_embedded_hop_returns_correct_data_via_fallback.
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
    /// Rebuilds a join chain with a user-composed operator interleaved between two joins by emitting each
    /// <c>$lookup</c> at its own join's boundary, rather than as one group above the base source.
    /// <para>
    /// A contiguous group would let the second join's row-dropping <c>$unwind</c> run before a
    /// <c>Skip</c>/<c>Take</c> written before that join, returning a silently wrong page. Splitting along join
    /// order also preserves transitive <c>localField</c> dependencies; <see cref="DependenciesPrecede"/> re-checks
    /// that. Returns <see langword="null"/> (leave the join to the driver) if the split can't be established.
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

        // Lookup resolved for the previous (one level inner) join; DisambiguateJoinLookupByChain needs it to
        // break type/key ties in self-referencing chains.
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

        // Lookups with no composed operator above them are left to AppendLookupStages, which tail-appends them
        // (and already handles a scalar terminal).
        emissionOrder.AddRange(forceUnwindLookups.Where(currentGroup.Contains));

        if (assigned.Count != forceUnwindLookups.Count || !DependenciesPrecede(emissionOrder))
        {
            return null;
        }

        foreach (var (node, lookups) in groups)
        {
            // Add, not indexer: a duplicate key would silently drop a join's $lookup and return unjoined rows.
            _injectAboveNodeLookups.Add(node, lookups);
            foreach (var lookup in lookups)
            {
                _injectedEarlyLookups.Add(lookup);
            }
        }

        return result;
    }

    /// <summary>
    /// Resolves which <c>ForceUnwind</c> <c>$lookup</c> stands in for a join operator, matching on inner entity
    /// CLR type and outer key (FK) so two navigations to the same type stay distinct. Returns
    /// <see langword="null"/> when missing or ambiguous.
    /// </summary>
    /// <remarks>
    /// Shared by <see cref="StripInterleavedJoinChain"/> (where the lookup is emitted) and
    /// <see cref="TransparentIdentifierToLookupFieldRewriter.ResolveLookup"/> (which field lambdas read); they must
    /// agree or a <c>$lookup</c> lands on the wrong side of an interleaved operator.
    /// </remarks>
    /// <param name="joinCall">The join operator whose lookup is wanted.</param>
    /// <param name="innerType">
    /// The join's inner entity CLR type. Each caller passes its own authoritative source (the rewriter the
    /// TransparentIdentifier <c>Inner</c> member type, the group split the inner sequence type); they agree today.
    /// </param>
    /// <param name="candidates">The lookups to match against.</param>
    /// <param name="previous">
    /// The lookup for the join one level inner, or <see langword="null"/> for the innermost. Used only to break
    /// ties via <see cref="DisambiguateJoinLookupByChain"/>, which keeps self-referencing chains
    /// (<c>Employee.Manager.Manager</c>) resolvable.
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

        // TargetEntityType, not Navigation.TargetEntityType: a navigation-less Join hop has a null Navigation
        // (and no ForeignKey, so it matches on CLR type alone; ambiguity is settled structurally below).
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
    /// Settles a type/key-ambiguous lookup match structurally, via the <c>localField</c> dependency relation.
    /// </summary>
    /// <remarks>
    /// Self-referencing chains and navigation-less hops can't be separated by type/key. The innermost hop's
    /// <c>localField</c> isn't prefixed by any sibling's alias, while each deeper hop's is chained onto the
    /// previous hop's alias. Returns <see langword="null"/> rather than guessing.
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
    /// <see cref="ResolveJoinLookup"/> for one join in the chain being split, using the join's inner sequence
    /// type.
    /// </summary>
    private static LookupExpression? ResolveLookupForJoin(
        MethodCallExpression joinCall, IReadOnlyList<LookupExpression> candidates, LookupExpression? previous)
    {
        var innerType = joinCall.Arguments.Count > 1 ? joinCall.Arguments[1].Type.TryGetItemType() : null;

        return innerType == null ? null : ResolveJoinLookup(joinCall, innerType, candidates, previous);
    }

    /// <summary>
    /// Whether an emission order respects the <c>localField</c> dependency chain: a lookup reading another's
    /// output (<c>"&lt;other.As&gt;."</c> prefix) must come after it.
    /// </summary>
    /// <remarks>
    /// Fail-closed re-check; splitting along join order should always satisfy it. Internal for unit testing.
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

            // An operator composed after a user Select (e.g. the Where in `Select(x => x.Inner).Distinct().Where(r
            // => ...)`) already reads the projected element, not the join's TransparentIdentifier or the root
            // document, so it has nothing to rewrite and is kept verbatim over the rebuilt source.
            if (oldSourceItemType == newSourceItemType
                && newSourceItemType != rewriter.RootType
                && !oldSourceItemType.IsTransparentIdentifierType())
            {
                continue;
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

        // Only a genuine residual TransparentIdentifier reference is a problem. An operator above the
        // synthesized flattening Select (e.g. the First(predicate) that Include(...).First(o => o.Carrier == null)
        // folds into) already has the flattened root type as oldSourceItemType, so a plain "generic arg equals
        // oldSourceItemType" check would wrongly refuse the strip and leave a driver LeftJoin the shaper can't
        // read, corrupting results.
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
        // Only the exceptions reconstruction can legitimately raise for an unrebuildable shape
        // (MakeGenericMethod constraint violations, Expression.Call signature mismatch); anything else is a bug.
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static bool IsIncludeShapingSelect(MethodCallExpression call)
        => call.Method.Name == nameof(Queryable.Select)
           && call.Arguments.Count == 2
           && call.Arguments[1] is UnaryExpression { NodeType: ExpressionType.Quote, Operand: LambdaExpression selector }
           && new IncludeExpressionFinder().Find(selector.Body);

    private sealed class IncludeExpressionFinder : System.Linq.Expressions.ExpressionVisitor
    {
        private bool _found;

        public bool Find(Expression expression)
        {
            Visit(expression);
            return _found;
        }

        public override Expression? Visit(Expression? node)
        {
            if (node is IncludeExpression)
            {
                _found = true;
                return node;
            }

            return _found ? node : base.Visit(node);
        }
    }

    private static bool IsQueryableDistinct(MethodCallExpression call)
        => call.Method.DeclaringType == typeof(Queryable)
           && call.Method.Name == nameof(Queryable.Distinct)
           && call.Arguments.Count == 1;

    /// <summary>
    /// Operators whose element is the element of their source (filter, order, page, dedup, or a cardinality
    /// terminal), so a bare join leaf selected below them is still what the query returns.
    /// </summary>
    private static bool IsElementPreservingOperator(MethodCallExpression call)
        => call.Method.DeclaringType == typeof(Queryable)
           && call.Method.Name is nameof(Queryable.Where) or nameof(Queryable.OrderBy)
               or nameof(Queryable.OrderByDescending) or nameof(Queryable.ThenBy) or nameof(Queryable.ThenByDescending)
               or nameof(Queryable.Skip) or nameof(Queryable.Take) or nameof(Queryable.Distinct)
               or nameof(Queryable.First) or nameof(Queryable.FirstOrDefault) or nameof(Queryable.Single)
               or nameof(Queryable.SingleOrDefault) or nameof(Queryable.Last) or nameof(Queryable.LastOrDefault);

    /// <summary>
    /// A <c>Distinct()</c> over the bare Outer leaf of a join (<c>Join(...).Select(x =&gt; x.Outer).Distinct()</c>)
    /// runs over the flattened root document, which still carries each join's <c>_lookup_&lt;Nav&gt;</c> field, so
    /// it would dedup (outer, inner) pairs and return an outer entity once per matched inner row. Unset those
    /// fields first so only the outer entity is compared; nothing above the Distinct can read the inner side.
    /// Returns <see langword="null"/> (refuse the strip) if a tail-appended lookup chains off an unset field.
    /// </summary>
    private Expression? ExcludeJoinFieldsBeforeDistinct(Expression source, List<LookupExpression> joinLookups)
    {
        if (joinLookups.Count == 0)
        {
            return source;
        }

        var aliases = joinLookups.Select(l => l.As).ToList();
        if (_pendingLookups.Any(l => !l.ForceUnwind
                                     && aliases.Any(a => l.LocalField.StartsWith(a + ".", StringComparison.Ordinal))))
        {
            return null;
        }

        return AppendBsonStage(source, new BsonDocument("$unset", new BsonArray(aliases)));
    }

    /// <summary>
    /// The <c>_lookup_&lt;Nav&gt;</c> field a reattached bare Inner leaf <c>Select</c> reads
    /// (<c>Select(e =&gt; Mql.Field(e, "_lookup_&lt;Nav&gt;", serializer))</c>, the rewrite of
    /// <c>Select(ti =&gt; ti.Inner)</c>), or <see langword="null"/> for any other operator.
    /// </summary>
    private string? TryGetBareLookupFieldSelectAlias(Expression rebuilt)
        => rebuilt is MethodCallExpression
           {
               Method.Name: nameof(Queryable.Select),
               Arguments: [_, UnaryExpression { NodeType: ExpressionType.Quote, Operand: LambdaExpression selector }]
           }
           && selector.Body is MethodCallExpression { Method.IsGenericMethod: true } field
           && field.Method.GetGenericMethodDefinition() == MqlFieldMethodInfo
           && field.Arguments[0] == selector.Parameters[0]
           && field.Arguments[1] is ConstantExpression { Value: string alias }
           && _pendingLookups.Any(l => l.ForceUnwind && l.As == alias)
            ? alias
            : null;

    /// <summary>Appends a raw pipeline stage that keeps the element type (and serializer) of <paramref name="query"/>.</summary>
    private static Expression AppendBsonStage(Expression query, BsonDocument stage)
    {
        var sourceType = query.Type.TryGetItemType()!;
        var serializerType = typeof(IBsonSerializer<>).MakeGenericType(sourceType);
        return Expression.Call(
            null,
            typeof(MongoQueryable).GetMethod(nameof(MongoQueryable.AppendStage))!.MakeGenericMethod(sourceType, sourceType),
            query,
            Expression.New(
                typeof(BsonDocumentPipelineStageDefinition<,>).MakeGenericType(sourceType, sourceType)
                    .GetConstructor([typeof(BsonDocument), serializerType])!,
                Expression.Constant(stage),
                Expression.Constant(null, serializerType)),
            Expression.Constant(null, serializerType));
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
    /// TransparentIdentifier (dropped; the <c>$lookup</c> replaces it) from a user-composed <c>Select</c>
    /// (reattached). Recognized structurally: its body is an <see cref="IncludeExpression"/> or a bare
    /// <c>.Outer</c> chain back to the parameter, which a user selector can't be.
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

        /// <summary>The flattened root document type the rewritten lambdas take as their parameter.</summary>
        public Type RootType => _rootType;

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
        /// <paramref name="depth"/> (0 = innermost join), via the shared <see cref="ResolveJoinLookup"/>, using
        /// the <c>Inner</c> member type the lambda reads. Returns <see langword="null"/> when missing or ambiguous.
        /// </summary>
        private LookupExpression? ResolveLookup(int depth, Type innerType)
            => depth < 0 || depth >= _joinsInnermostFirst.Count
                ? null
                : ResolveJoinLookup(
                    _joinsInnermostFirst[depth],
                    innerType,
                    _pendingLookups,
                    // The hop one level inner, used only to break type/key ties; null at depth 0.
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

                    // TargetEntityType: a navigation-less Join hop has a null Navigation.
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
    /// Whether a lookup is emitted below the pipeline tail and so must not be tail-appended: either marked
    /// <see cref="LookupExpression.InjectAfterRoot"/> at compile time, or scheduled above the base source by this
    /// execution's <see cref="StripJoinForLookup"/> (<see cref="_injectedEarlyLookups"/>). The latter is
    /// per-execution state, not written to the shared, cached <see cref="LookupExpression"/>.
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
                var unwindDoc = lookup.ToUnwindStageDocument(lookup.PreserveNullAndEmptyArrays);

                query = Expression.Call(null, appendStageMethod, query,
                    Expression.New(stageConstructor,
                        Expression.Constant(unwindDoc),
                        Expression.Constant(null, serializerType)),
                    Expression.Constant(null, serializerType));

                // A left-outer join's unmatched row comes out of `$unwind { preserveNullAndEmptyArrays: true }`
                // with the joined field MISSING, not null, and the driver renders a TransparentIdentifier null
                // check over this flattened `_lookup_<Nav>` shape (`ti.Inner != null ? ti.Inner.X : fallback`) as
                // `$ne: ["$_lookup_<Nav>", null]` — TRUE for a missing field in aggregation expressions, so the
                // ternary took the dereferencing branch and read a default (0/null) instead of the fallback.
                // MEASURED: NativeJoinScopeConditionalProjectionTests' two-level-chain DriverLinq test returned
                // null where "<none>" was expected, and a single left join over a collection/reference navigation
                // did the same once a native Select arm registered its lookup at translation time (flipping the
                // fallback onto this flattened shape). Normalizing missing -> explicit null right after the
                // $unwind makes the null check answer correctly; a matched row's sub-document is unchanged. Scoped
                // to forced-unwind (join) lookups — Include's reference lookups are untouched.
                if (lookup.ForceUnwind && lookup.PreserveNullAndEmptyArrays)
                {
                    var setDoc = new BsonDocument("$set", new BsonDocument(lookup.As,
                        new BsonDocument("$ifNull", new BsonArray { "$" + lookup.As, BsonNull.Value })));
                    query = Expression.Call(null, appendStageMethod, query,
                        Expression.New(stageConstructor,
                            Expression.Constant(setDoc),
                            Expression.Constant(null, serializerType)),
                        Expression.Constant(null, serializerType));
                }
            }
        }

        return query;
    }
}
