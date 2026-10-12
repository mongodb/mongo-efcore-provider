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
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.Query.Visitors;

// Join rewriting for the driver's LINQ provider: (1) EF's LeftJoin becomes the driver's Join (it has no LeftJoin translator); (2) the
// $lookup fallback (StripJoinForLookup, AppendLookupStages, ...) peels a Join chain to its root and emits manual $lookup + $unwind where the
// driver join can't express the shape. Visit/VisitMethodCall dispatch stays in the main visitor file.
internal sealed partial class MongoEFToLinqTranslatingExpressionVisitor : System.Linq.Expressions.ExpressionVisitor
{
    /// <summary>
    /// For join queries, strips the outermost Select EF adds to extract from TransparentIdentifier, then rewrites every LeftJoin in the residual
    /// chain. The driver only recognizes "Join" (<c>JoinMethodToPipelineTranslator</c> requires <c>QueryableMethod.Join</c>), so each
    /// <c>Queryable.LeftJoin</c> becomes <c>Queryable.Join</c> with a <see cref="LeftJoinResult{TOuter,TInner}"/> result selector; the driver's
    /// documents then have _outer/_inner fields matching the LeftJoinResult property names.
    /// </summary>
    private Expression? StripOuterSelectForJoin(Expression expression)
    {
        if (expression is not MethodCallExpression call)
        {
            return null;
        }

        if (call.Method.Name == "Select" && call.Method.DeclaringType == typeof(Queryable))
        {
            return RewriteLeftJoins(call.Arguments[0], convertExplicitJoins: true);
        }

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

        return RewriteLeftJoins(expression, convertExplicitJoins: true);
    }

    /// <summary>
    /// Recursively rewrites every <c>Queryable.LeftJoin</c> into <c>Queryable.Join</c> with a <see cref="LeftJoinResult{TOuter,TInner}"/> result
    /// selector, cascading the element-type change (TransparentIdentifier&lt;O,I&gt; to LeftJoinResult&lt;O,I&gt;) into every downstream operator so
    /// <c>.Outer</c>/<c>.Inner</c> become <c>._outer</c>/<c>._inner</c>. Bottom-up, so chained joins and nested joins are handled.
    /// <paramref name="convertExplicitJoins"/>: whether an explicit user <c>Queryable.Join</c> also gets a LeftJoinResult selector. The shaped
    /// path needs it (its shaper reads <c>_outer</c>/<c>_inner</c>); the projected path must NOT, since the user's selector is the projection and
    /// is emitted verbatim. A <c>LeftJoin</c> is always converted.
    /// </summary>
    private Expression RewriteLeftJoins(Expression expression, bool convertExplicitJoins)
    {
        if (expression is not MethodCallExpression call
            || (call.Method.DeclaringType != typeof(Queryable)
                && !MongoQueryableMethodTranslatingExpressionVisitor.IsEf8Ef9LeftJoinShim(call.Method)))
        {
            return expression;
        }

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

        if (!sourceChanged || newSourceItemType == null)
        {
            return call;
        }

        var oldSourceItemType = originalSource.Type.TryGetItemType();
        var genericArgs = call.Method.GetGenericArguments().ToArray();
        var rebuiltArgs = call.Arguments.ToArray();
        rebuiltArgs[0] = newSource;

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
    /// Rewrites one Join/LeftJoin node given its (possibly already-rewritten) source. A LeftJoin becomes a <c>Queryable.Join</c> with a
    /// <see cref="LeftJoinResult{TOuter,TInner}"/> selector. An explicit user Join keeps its own result selector (it must round-trip unchanged);
    /// only its parameter/generic types and key selectors are remapped when a nested join changed the outer element type.
    /// </summary>
    private Expression RewriteJoinNode(
        MethodCallExpression call, Expression newSource, Type? newSourceItemType, bool convertToJoin, bool isLeftJoin, bool shapedPath)
    {
        var sourceChanged = newSourceItemType != null;
        if (!convertToJoin && !sourceChanged)
        {
            return call;
        }

        var genericArgs = call.Method.GetGenericArguments();
        var resultSelector = call.Arguments[4].UnwrapLambdaFromQuote();
        var innerType = resultSelector.Parameters[1].Type;
        var oldOuterType = resultSelector.Parameters[0].Type;

        var outerType = newSourceItemType ?? oldOuterType;

        // A reference Include is a LEFT-OUTER join, but the driver's Join emits an INNER `$unwind` that drops null-FK principals. For a bare
        // single-reference LeftJoin (outer is the root entity) emit `$lookup` + `$unwind` with `preserveNullAndEmptyArrays: true` in the same
        // root-level _outer/_inner shape the shaper reads.
        if (isLeftJoin && shapedPath && outerType == oldOuterType)
        {
            var leftOuter = TryBuildDriverNativeLeftJoinPipeline(call, newSource);
            if (leftOuter != null)
            {
                return leftOuter;
            }
        }

        // The projected (push-down) path yields LeftJoinResult<TOuter,TInner>. Queryable.Join would be INNER and drop principals with no related
        // entity, so emit outer.GroupJoin(inner, ok, ik, (o, g) => carrier).SelectMany(c => c._inner.DefaultIfEmpty(),
        // (c, i) => new LeftJoinResult<TOuter,TInner>(c._outer, i)), the same _outer/_inner shape. The shaped path uses TryBuildDriverNativeLeftJoinPipeline.
        if (convertToJoin && isLeftJoin && !shapedPath)
        {
            return BuildProjectedLeftOuterJoin(
                call, newSource, outerType, innerType, genericArgs[2], oldOuterType);
        }

        Type resultType;
        LambdaExpression newResultSelector;
        if (convertToJoin)
        {
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
            resultType = call.Method.GetGenericArguments()[^1];
            newResultSelector = outerType != oldOuterType
                ? RewriteLambdaForLeftJoinResult(resultSelector, outerType)
                : resultSelector;
        }

        var joinDefinition = convertToJoin ? QueryableJoinMethod : call.Method.GetGenericMethodDefinition();
        var newMethod = joinDefinition.MakeGenericMethod(outerType, innerType, genericArgs[2], resultType);

        var newArgs = call.Arguments.ToArray();
        newArgs[0] = newSource;
        if (outerType != oldOuterType
            && call.Arguments[2] is UnaryExpression { NodeType: ExpressionType.Quote, Operand: LambdaExpression outerKey })
        {
            newArgs[2] = Expression.Quote(RewriteLambdaForLeftJoinResult(outerKey, outerType));
        }

        newArgs[4] = Expression.Quote(newResultSelector);

        return Expression.Call(null, newMethod, newArgs);
    }

    /// <summary>
    /// Emits the left-outer join for a single-reference <c>LeftJoin</c> as raw aggregation stages (<see cref="MongoQueryable.AppendStage"/>),
    /// mirroring the driver's <c>JoinMethodToPipelineTranslator</c> but with <c>preserveNullAndEmptyArrays: true</c> on the <c>$unwind</c>:
    /// <code>
    /// { $project: { _outer: "$$ROOT", _id: 0 } }
    /// { $lookup:  { from: &lt;inner collection&gt;, localField: "_outer.&lt;fk&gt;", foreignField: "&lt;pk&gt;", as: "_inner" } }
    /// { $unwind:  { path: "$_inner", preserveNullAndEmptyArrays: true } }
    /// { $project: { _outer: "$_outer", _inner: "$_inner", _id: 0 } }
    /// </code>
    /// The root-level <c>_outer</c>/<c>_inner</c> shape matches the driver's Join, so the shaper is unchanged. Returns <see langword="null"/>
    /// (fall back to the driver's inner Join) for shapes it doesn't recognise, e.g. composite/anonymous join keys.
    /// </summary>
    private Expression? TryBuildDriverNativeLeftJoinPipeline(
        MethodCallExpression call, Expression newSource)
    {
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
        // Always true: only reached from LeftJoin handling. LookupExpression.PreserveNullAndEmptyArrays is consulted only on the flat-lookup path.
        var unwind = LookupExpression.UnwindStageDocument("_inner", preserveNullAndEmptyArrays: true);
        var projectResult = new BsonDocument("$project",
            new BsonDocument { { "_outer", "$_outer" }, { "_inner", "$_inner" }, { "_id", 0 } });

        // Type the final stage as LeftJoinResult<TOuter,TInner>, the element type the driver's Queryable.Join produces, so
        // downstream operators reading Outer/Inner (rewritten to _outer/_inner) translate against a source that has those
        // members. Intermediate stages stay BsonDocument-typed; only the result-shaping $project carries the serializer.
        var resultType = typeof(LeftJoinResult<,>).MakeGenericType(outerClrType, innerClrType);
        var resultSerializer = BuildLeftJoinResultSerializer(outerClrType, innerClrType, outerEntityType, innerEntityType);

        var query = AppendRawStage(newSource, outerClrType, projectOuter, typeof(BsonDocument));
        query = AppendRawStage(query, typeof(BsonDocument), lookup);
        query = AppendRawStage(query, typeof(BsonDocument), unwind);
        query = AppendRawStage(query, typeof(BsonDocument), projectResult, resultType, resultSerializer);
        return query;
    }

    /// <summary>
    /// Builds the serializer for <see cref="LeftJoinResult{TOuter,TInner}"/> with <c>_outer</c>/<c>_inner</c> using the provider's entity serializers,
    /// mirroring the driver Join's synthesized one. A <see cref="BsonClassMapSerializer{TClass}"/> (an <c>IBsonDocumentSerializer</c>) lets the
    /// driver translate downstream member access as for its own Join.
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

    /// <summary>The dotted BSON path for a simple <c>EF.Property</c>/member-access join key; <see langword="null"/> for composite/anonymous/computed keys.</summary>
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
    /// Appends a raw stage via <see cref="MongoQueryable.AppendStage"/>, keeping the element type unless <paramref name="newElementType"/> changes
    /// it (the final stage, to surface LeftJoinResult). <paramref name="outSerializer"/>, when supplied, lets the driver introspect the new type's members.
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
    /// Builds the driver-translatable left-outer join for the PROJECTED (non-shaped) path:
    /// <code>
    /// outer.GroupJoin(inner, outerKey, innerKey, (o, g) => new LeftJoinResult&lt;TOuter, IEnumerable&lt;TInner&gt;&gt;(o, g))
    ///      .SelectMany(c => c._inner.DefaultIfEmpty(), (c, i) => new LeftJoinResult&lt;TOuter, TInner&gt;(c._outer, i))
    /// </code>
    /// Principals with no match survive, and the element type / document shape equals the inner-Join path's, so the downstream selector and shaper
    /// are unchanged.
    /// </summary>
    private Expression BuildProjectedLeftOuterJoin(
        MethodCallExpression call, Expression newSource, Type outerType, Type innerType, Type keyType, Type oldOuterType)
    {
        var outerKey = call.Arguments[2].UnwrapLambdaFromQuote();
        var innerKey = call.Arguments[3].UnwrapLambdaFromQuote();

        if (outerType != oldOuterType)
        {
            outerKey = RewriteLambdaForLeftJoinResult(outerKey, outerType);
        }

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

        var carrierParam = Expression.Parameter(carrierType, "c");
        var carrierInner = Expression.Property(carrierParam, nameof(LeftJoinResult<object, object>._inner));
        var defaultIfEmpty = Expression.Call(
            null, EnumerableDefaultIfEmptyMethod.MakeGenericMethod(innerType), carrierInner);
        var collectionSelector = Expression.Lambda(defaultIfEmpty, carrierParam);

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

    private sealed class TransparentIdentifierToLeftJoinResultRewriter(
        ParameterExpression oldParam,
        ParameterExpression newParam) : System.Linq.Expressions.ExpressionVisitor
    {
        protected override Expression VisitMember(MemberExpression node)
        {
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
                        // Cannot rewrite this operator safely: refuse the strip so the join survives and the callers fall back to the driver.
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

                // The reattached stages read $lookup output, so the lookups go below them but no lower than the base source: an inner $unwind drops
                // rows, so hoisting it above a base-source Skip/Take/Distinct would change which rows those see (the interleaved case is
                // StripInterleavedJoinChain). Flag all join-replacing lookups, not only those the lambdas read: a transitive lookup's localField can point
                // into an earlier one ("_lookup_Customer.region_id"), and a tail-appended remainder could land after a scalar terminal like Count.
                // See NativeReferenceIncludeTests.Deep_ThenInclude_through_embedded_hop_returns_correct_data_via_fallback.
                foreach (var lookup in contiguousGroup)
                {
                    _injectedEarlyLookups.Add(lookup);
                }

                _injectAboveNodeLookups[baseSource] = contiguousGroup;

                return result;
            }

            return null;
        }

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
                    || !candidate.ReadsOutputOf(other)))
                : matches.FirstOrDefault(candidate =>
                    candidate.ReadsOutputOf(previous))
        };

    /// <summary><see cref="ResolveJoinLookup"/> for one join in the chain being split, using the join's inner sequence type.</summary>
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
                if (emissionOrder[i].ReadsOutputOf(emissionOrder[j]))
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Fails translation rather than let <see cref="StripJoinForLookup"/> silently discard a <c>Where</c> while peeling a join chain (that would
    /// return the unfiltered cross product); mirrors EF-X021's filtered-Include guard. A <c>Where</c> at or below <paramref name="baseSource"/>
    /// becomes a leading <c>$match</c> and is unaffected. Translating the discarded filter is tracked as EF-X024.
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

        // Only a genuine residual TransparentIdentifier reference is a problem. An operator above the synthesized flattening Select (e.g. the
        // First(predicate) Include(...).First(...) folds into) already has the flattened root type as oldSourceItemType, so a plain "generic
        // arg equals oldSourceItemType" check would wrongly refuse the strip and leave a driver LeftJoin the shaper can't read.
        if (oldSourceItemType.IsTransparentIdentifierType()
            && (genericArgs.Contains(oldSourceItemType) || genericArgs.Any(a => ContainsType(a, oldSourceItemType))))
        {
            // A generic argument still mentions the now non-existent TransparentIdentifier type. When oldSourceItemType isn't one to begin with
            // (e.g. a terminal First() above an Include-shaping Select), old and new legitimately coincide and this must not reject.
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
           && ExpressionSearch.Contains(selector.Body, node => node is IncludeExpression);

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

        if (_pendingLookups.Any(l => !l.ForceUnwind && joinLookups.Any(l.ReadsOutputOf)))
        {
            return null;
        }

        return AppendBsonStage(source, new BsonDocument("$unset", new BsonArray(joinLookups.Select(l => l.As))));
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
        => AppendRawStage(query, query.Type.TryGetItemType()!, stage);

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

        /// <summary>The <c>$lookup</c> supplying the <c>Inner</c> of the TransparentIdentifier at <paramref name="depth"/> (0 = innermost), or <see langword="null"/>.</summary>
        private LookupExpression? ResolveLookup(int depth, Type innerType)
            => depth < 0 || depth >= _joinsInnermostFirst.Count
                ? null
                : ResolveJoinLookup(
                    _joinsInnermostFirst[depth],
                    innerType,
                    _pendingLookups,
                    depth == 0 ? null : ResolveLookup(depth - 1, innerType));

        private sealed class Rewriter(TransparentIdentifierToLookupFieldRewriter owner, ParameterExpression oldParam)
            : System.Linq.Expressions.ExpressionVisitor
        {
            protected override Expression VisitMember(MemberExpression node)
            {
                // The lambda's parameter is the identifier produced by join number (its own nesting depth - 1): a lambda
                // composed BETWEEN two joins sees a shallower identifier than one composed above both. Scope 0 is the
                // root document (an .Outer chain all the way down); scope k >= 1 is the Inner of join k - 1 (0 =
                // innermost). A partial .Outer chain is itself a TransparentIdentifier with no flattened equivalent: it
                // doesn't resolve, so the walk reaches the bare parameter and fails there.
                if (node.Member.Name is "Outer" or "Inner"
                    && node.Member.DeclaringType.IsTransparentIdentifierType()
                    && MongoTransparentScopeResolver.TryResolveScopeDepth(
                        node, oldParam, MongoTransparentScopeResolver.TransparentIdentifierHops, NestingDepth(oldParam.Type),
                        out var scopeIndex))
                {
                    if (scopeIndex == 0)
                    {
                        return owner._rootParam;
                    }

                    var lookup = owner.ResolveLookup(scopeIndex - 1, node.Type);
                    if (lookup == null)
                    {
                        owner.Failed = true;
                        return node;
                    }

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
                    owner.Failed = true;
                    return node;
                }

                return base.VisitParameter(node);
            }

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

    /// <summary>Appends $lookup stages for cross-collection collection Includes.</summary>
    private Expression AppendLookupStages(Expression query)
        // Tail-append every pending $lookup except those flagged InjectAfterRoot (InjectAfterRootLookupStages emits them), and suppress forceUnwind
        // lookups when a surviving native Join already supplies the join (_appendForceUnwindLookups).
        => EmitLookupStages(query,
            _pendingLookups.Where(l => !IsInjectedEarly(l) && !l.IsBridgeIgnorable && (_appendForceUnwindLookups || !l.ForceUnwind)));

    /// <summary>
    /// Emit the $lookup stages flagged <see cref="LookupExpression.InjectAfterRoot"/> immediately after the
    /// root collection source, so the user's downstream pipeline stages (e.g. a <c>$match</c>/<c>$project</c>
    /// that reads the <c>_lookup_&lt;Nav&gt;</c> array via <c>{ $size: ... }</c>) see the array already present.
    /// </summary>
    private Expression InjectAfterRootLookupStages(Expression query)
        => EmitLookupStages(
            query, _pendingLookups.Where(l => l.InjectAfterRoot || l.IsBridgeReadableArray).Concat(_bridgeOwnedLookups));

    /// <summary>
    /// Whether a lookup is emitted below the pipeline tail and so must not be tail-appended: either marked
    /// <see cref="LookupExpression.InjectAfterRoot"/> at compile time, or scheduled above the base source by this
    /// execution's <see cref="StripJoinForLookup"/> (<see cref="_injectedEarlyLookups"/>). The latter is
    /// per-execution state, not written to the shared, cached <see cref="LookupExpression"/>.
    /// </summary>
    private bool IsInjectedEarly(LookupExpression lookup)
        => lookup.InjectAfterRoot || lookup.IsBridgeReadableArray || _injectedEarlyLookups.Contains(lookup);

    private Expression EmitLookupStages(Expression query, IEnumerable<LookupExpression> lookups)
    {
        var lookupList = lookups as IReadOnlyList<LookupExpression> ?? lookups.ToList();
        if (lookupList.Count == 0)
        {
            return query;
        }

        var sourceType = query.Type.TryGetItemType() ?? _source.Type.TryGetItemType()!;

        foreach (var lookup in lookupList)
        {
            query = AppendRawStage(query, sourceType, lookup.ToLookupStageDocument());

            if (lookup.ShouldUnwind)
            {
                // Left-outer vs inner comes from the LINQ operator the lookup was registered for (see
                // LookupExpression.PreserveNullAndEmptyArrays): an Include or LeftJoin/GroupJoin preserves
                // the principal, a plain Join - including EF's lowering of a REQUIRED reference navigation -
                // drops it when the foreign key matched nothing.
                query = AppendRawStage(query, sourceType, lookup.ToUnwindStageDocument(lookup.PreserveNullAndEmptyArrays));

                // A left-outer join's unmatched row leaves the joined field MISSING (not null) after
                // `$unwind { preserveNullAndEmptyArrays: true }`, and the driver's TransparentIdentifier null check
                // (`$ne: ["$_lookup_<Nav>", null]`) is TRUE for a missing field, so the ternary would take the dereferencing branch and
                // read a default instead of the fallback. Normalize missing to explicit null right after the $unwind. Scoped to
                // forced-unwind (join) lookups; Include's reference lookups are untouched.
                if (lookup.ForceUnwind && lookup.PreserveNullAndEmptyArrays)
                {
                    var setDoc = new BsonDocument("$set", new BsonDocument(lookup.As,
                        new BsonDocument("$ifNull", new BsonArray { "$" + lookup.As, BsonNull.Value })));
                    query = AppendRawStage(query, sourceType, setDoc);
                }
            }
        }

        return query;
    }
}
