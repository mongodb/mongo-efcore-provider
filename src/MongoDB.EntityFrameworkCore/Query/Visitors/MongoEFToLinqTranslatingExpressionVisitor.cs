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
using MongoDB.EntityFrameworkCore.Metadata;
using MongoDB.EntityFrameworkCore.Serializers;

namespace MongoDB.EntityFrameworkCore.Query.Visitors;

/// <summary>
/// Visits the tree resolving any query context parameter bindings and EF references so the query can be used with the MongoDB V3 LINQ provider.
/// </summary>
internal sealed class MongoEFToLinqTranslatingExpressionVisitor : System.Linq.Expressions.ExpressionVisitor
{
    private static readonly MethodInfo MqlFieldMethodInfo =
        typeof(Mql).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(m => m.Name == nameof(Mql.Field) && m.GetParameters().Length == 3);

    private readonly QueryContext _queryContext;
    private readonly Expression _source;
    private readonly BsonSerializerFactory _bsonSerializerFactory;
    private readonly IReadOnlyList<LookupExpression> _pendingLookups;
    private readonly Dictionary<IEntityType, Expression> _innerSources;
    private EntityQueryRootExpression? _foundEntityQueryRootExpression;

    internal MongoEFToLinqTranslatingExpressionVisitor(
        QueryContext queryContext,
        Expression source,
        BsonSerializerFactory bsonSerializerFactory,
        IReadOnlyList<LookupExpression>? pendingLookups = null,
        Dictionary<IEntityType, Expression>? innerSources = null)
    {
        _queryContext = queryContext;
        _source = source;
        _bsonSerializerFactory = bsonSerializerFactory;
        _pendingLookups = pendingLookups ?? Array.Empty<LookupExpression>();
        _innerSources = innerSources ?? new Dictionary<IEntityType, Expression>();
    }

    public Dictionary<string, object> AdditionalState { get; } = new();

    /// <summary>
    /// Translate a projected query (anonymous types with entity members).
    /// Strips joins for lookup-based queries and appends any pending $lookup stages.
    /// </summary>
    public Expression TranslateProjected(Expression? efQueryExpression)
    {
        if (efQueryExpression == null)
        {
            return AppendLookupStages(_source);
        }

        // For explicit Join queries with pending lookups, strip the join and use $lookup instead.
        var expressionToTranslate = _pendingLookups.Count > 0
            ? StripJoinForLookup(efQueryExpression) ?? efQueryExpression
            : efQueryExpression;

        var query = Visit(expressionToTranslate)!;
        return AppendLookupStages(query);
    }

    public MethodCallExpression Translate(
        Expression? efQueryExpression,
        ResultCardinality resultCardinality)
    {
        if (efQueryExpression == null) // No LINQ methods, e.g. Direct ToList() against DbSet
        {
            var source = AppendLookupStages(_source);
            return ApplyAsSerializer(source, BsonDocumentSerializer.Instance, typeof(BsonDocument));
        }

        // For explicit Join queries with pending lookups (forceUnwind), strip the join
        // and let AppendLookupStages handle it. For Include LeftJoins, strip the outer Select
        // and let the driver handle the LeftJoin natively.
        var expressionToTranslate = efQueryExpression;
        if (_pendingLookups.Any(l => l.ForceUnwind))
        {
            expressionToTranslate = StripJoinForLookup(efQueryExpression) ?? efQueryExpression;
        }
        else if (_innerSources.Count > 0)
        {
            expressionToTranslate = StripOuterSelectForJoin(efQueryExpression) ?? efQueryExpression;
        }

        var query = (MethodCallExpression)Visit(expressionToTranslate)!;

        if (resultCardinality == ResultCardinality.Enumerable)
        {
            var withLookups = AppendLookupStages(query);
            return ApplyAsSerializer(withLookups, BsonDocumentSerializer.Instance, typeof(BsonDocument));
        }

        var withLookupsSingle = AppendLookupStages(query.Arguments[0]);
        var documentQueryableSource = ApplyAsSerializer(withLookupsSingle, BsonDocumentSerializer.Instance, typeof(BsonDocument));

        return Expression.Call(
            null,
            query.Method.GetGenericMethodDefinition().MakeGenericMethod(typeof(BsonDocument)),
            documentQueryableSource);
    }

    /// <summary>
    /// For join queries, strip the outermost Select that EF adds to extract from TransparentIdentifier,
    /// and rewrite any LeftJoin result selectors from TransparentIdentifier to LeftJoinResult so the
    /// driver can serialize them. The driver produces documents with _outer/_inner fields which match
    /// the LeftJoinResult property names.
    /// </summary>
    private static Expression? StripOuterSelectForJoin(Expression expression)
    {
        if (expression is not MethodCallExpression call)
        {
            return null;
        }

        // Terminal op (First, etc.) wrapping a Select
        if (call.Arguments.Count >= 1 && call.Arguments[0] is MethodCallExpression innerCall
            && innerCall.Method.Name == "Select" && innerCall.Method.DeclaringType == typeof(Queryable))
        {
            var selectSource = RewriteLeftJoinResultSelectors(innerCall.Arguments[0]);
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

        // The outermost IS the Select (enumerable cardinality, no terminal op)
        if (call.Method.Name == "Select" && call.Method.DeclaringType == typeof(Queryable))
        {
            return RewriteLeftJoinResultSelectors(call.Arguments[0]);
        }

        // The outermost IS the Join/LeftJoin itself (Select was already stripped by the mixed path)
        if (call.Method.Name is "Join" or "LeftJoin" && call.Method.DeclaringType == typeof(Queryable))
        {
            return RewriteLeftJoinResultSelectors(call);
        }

        // The outermost is Where/OrderBy wrapping a Join (Select was already stripped by the mixed path)
        if (call.Method.DeclaringType == typeof(Queryable)
            && call.Method.Name is "Where" or "OrderBy" or "OrderByDescending" or "ThenBy" or "ThenByDescending")
        {
            return RewriteLeftJoinResultSelectors(call);
        }

        return null;
    }

    /// <summary>
    /// Recursively rewrite LeftJoin result selectors from TransparentIdentifier to LeftJoinResult.
    /// Handles nested joins (multi-level Include) by recursing into the outer source.
    /// </summary>
    private static Expression RewriteLeftJoinResultSelectors(Expression expression)
    {
        if (expression is not MethodCallExpression call)
        {
            return expression;
        }

        // For Select nodes between joins, recurse into the source and rewrite the Select's generic type
        if (call.Method.Name == "Select" && call.Method.DeclaringType == typeof(Queryable))
        {
            var rewrittenSource = RewriteLeftJoinResultSelectors(call.Arguments[0]);
            if (rewrittenSource != call.Arguments[0])
            {
                // Rebuild the Select with updated generic types
                var sourceItemType = rewrittenSource.Type.TryGetItemType()!;
                var selectMethod = call.Method.GetGenericMethodDefinition();
                var selectReturnType = call.Method.ReturnType.TryGetItemType()!;
                var newSelectMethod = selectMethod.MakeGenericMethod(sourceItemType, selectReturnType);
                return Expression.Call(null, newSelectMethod, rewrittenSource, call.Arguments[1]);
            }

            return call;
        }

        // For Where/OrderBy nodes between joins, recurse into the source and rewrite the lambda
        if (call.Method.DeclaringType == typeof(Queryable)
            && call.Method.Name is "Where" or "OrderBy" or "OrderByDescending" or "ThenBy" or "ThenByDescending")
        {
            var rewrittenSource = RewriteLeftJoinResultSelectors(call.Arguments[0]);
            if (rewrittenSource != call.Arguments[0])
            {
                var sourceItemType = rewrittenSource.Type.TryGetItemType()!;
                var lambda = call.Arguments[1].UnwrapLambdaFromQuote();
                var rewrittenLambda = RewriteLambdaForLeftJoinResult(lambda, sourceItemType);

                var updatedGenericArgs = call.Method.GetGenericArguments().ToArray();
                updatedGenericArgs[0] = sourceItemType;
                var updatedMethod = call.Method.GetGenericMethodDefinition().MakeGenericMethod(updatedGenericArgs);

                return Expression.Call(null, updatedMethod, rewrittenSource, Expression.Quote(rewrittenLambda));
            }

            return call;
        }

        if (call.Method.Name is not ("LeftJoin" or "Join") || call.Method.DeclaringType != typeof(Queryable))
        {
            return expression;
        }

        var genericArgs = call.Method.GetGenericArguments();
        var resultType = genericArgs[^1];
        if (resultType == typeof(LeftJoinResult<,>).MakeGenericType(genericArgs[0], genericArgs[1]))
        {
            return expression; // Already rewritten
        }

        // Recurse into the outer source for nested joins
        var outerSource = RewriteLeftJoinResultSelectors(call.Arguments[0]);

        var resultSelectorQuoted = call.Arguments[4];
        var resultSelector = resultSelectorQuoted.UnwrapLambdaFromQuote();
        var innerType = resultSelector.Parameters[1].Type;

        // When the outer source was rewritten, its element type changed from
        // TransparentIdentifier to LeftJoinResult. Use the actual source element type.
        var outerType = outerSource.Type.TryGetItemType() ?? resultSelector.Parameters[0].Type;

        var joinResultType = typeof(LeftJoinResult<,>).MakeGenericType(outerType, innerType);
        var ctor = joinResultType.GetConstructors()[0];

        // Rebuild the result selector with the correct outer parameter type
        var newOuterParam = Expression.Parameter(outerType, resultSelector.Parameters[0].Name);
        var newInnerParam = resultSelector.Parameters[1];
        var newResultSelector = Expression.Lambda(
            Expression.New(ctor, newOuterParam, newInnerParam),
            newOuterParam, newInnerParam);

        var newMethod = call.Method.GetGenericMethodDefinition()
            .MakeGenericMethod(outerType, innerType, genericArgs[2], joinResultType);

        var newArgs = call.Arguments.ToArray();
        newArgs[0] = outerSource;
        // Also rewrite key selectors that reference the outer type
        if (outerSource != call.Arguments[0])
        {
            newArgs[2] = RewriteKeySelectorForNewOuterType(call.Arguments[2], resultSelector.Parameters[0], newOuterParam);
        }
        newArgs[4] = Expression.Quote(newResultSelector);

        return Expression.Call(null, newMethod, newArgs);
    }

    /// <summary>
    /// Rewrite a key selector expression when the outer source type changed from TransparentIdentifier
    /// to LeftJoinResult due to nested join rewriting.
    /// </summary>
    private static Expression RewriteKeySelectorForNewOuterType(
        Expression keySelectorArg,
        ParameterExpression oldParam,
        ParameterExpression newParam)
    {
        // Unwrap Quote if present, but keep it as-is if it's not a Quote wrapping a lambda
        if (keySelectorArg is UnaryExpression { NodeType: ExpressionType.Quote } quote
            && quote.Operand is LambdaExpression lambda)
        {
            var rewritten = RewriteLambdaForLeftJoinResult(lambda, newParam.Type);
            return Expression.Quote(rewritten);
        }

        return keySelectorArg;
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

    private static MethodCallExpression ApplyAsSerializer(
        Expression query,
        IBsonSerializer resultSerializer,
        Type resultType)
    {
        var asMethodInfo = AsMethodInfo.MakeGenericMethod(query.Type.GenericTypeArguments[0], resultType);
        var serializerExpression = Expression.Constant(resultSerializer, resultSerializer.GetType());

        return Expression.Call(
            null,
            asMethodInfo,
            query,
            serializerExpression
        );
    }

    private static bool IsAsQueryableMethod(MethodInfo method)
        => method.Name == "AsQueryable" && method.DeclaringType == typeof(Queryable);

    public override Expression? Visit(Expression? expression)
    {
        switch (expression)
        {
            // Replace materialization collection expression with the actual nav property in order for Mql.Exists etc. to work.
            case MaterializeCollectionNavigationExpression materializeCollectionNavigationExpression:
                var subQuery = Visit(materializeCollectionNavigationExpression.Subquery);
                if (subQuery is MethodCallExpression mce && IsAsQueryableMethod(mce.Method))
                {
                    return Visit(mce.Arguments[0]);
                }

                return subQuery;

#if EF8 || EF9
            // Replace the QueryContext parameter values with constant values for this execution.
            case ParameterExpression parameterExpression:
                if (parameterExpression.Name?.StartsWith(QueryCompilationContext.QueryParameterPrefix, StringComparison.Ordinal)
                    == true)
                {
                    if (_queryContext.ParameterValues.TryGetValue(parameterExpression.Name, out var value))
                    {
                        return Expression.Constant(value).ConvertIfRequired(expression.Type);
                    }
                }

                break;
#else
            case QueryParameterExpression queryParameterExpression:
                return Expression.Constant(_queryContext.Parameters[queryParameterExpression.Name]).ConvertIfRequired(expression.Type);
#endif

            // Wrap OfType<T> with As(serializer) to re-attach the custom serializer in LINQ3
            case MethodCallExpression
                {
                    Method.Name: nameof(Queryable.OfType), Method.IsGenericMethod: true, Arguments.Count: 1
                } ofTypeCall
                when ofTypeCall.Method.DeclaringType == typeof(Queryable):
                var resultType = ofTypeCall.Method.GetGenericArguments()[0];
                var resultEntityType = _queryContext.Context.Model.FindEntityType(resultType)
                                       ?? throw new NotSupportedException($"OfType type '{resultType.ShortDisplayName()
                                       }' does not map to an entity type.");
                var resultSerializer = _bsonSerializerFactory.GetEntitySerializer(resultEntityType);
                var translatedOfTypeCall = Expression.Call(null, ofTypeCall.Method, Visit(ofTypeCall.Arguments[0])!);
                return ApplyAsSerializer(translatedOfTypeCall, resultSerializer, resultType);

            // Rewrite instance entity.Equals(other) to key comparisons (e.g. composite key entities)
            case MethodCallExpression { Method.Name: nameof(object.Equals), Object: not null, Arguments.Count: 1 } instanceEqualsCall:
                var instanceRewrite = TryRewriteEntityEquality(
                    instanceEqualsCall.Object!,
                    instanceEqualsCall.Arguments[0],
                    ExpressionType.Equal);
                if (instanceRewrite != null)
                    return instanceRewrite;
                break;

            // Replace object.Equals(Property(p, "propName"), ConstantExpression) elements generated by EF's Find.
            case MethodCallExpression { Method.Name: nameof(object.Equals), Object: null, Arguments.Count: 2 } methodCallExpression:
                // Check for entity equality before visiting (composite keys use Equals instead of ==)
                var entityRewrite = TryRewriteEntityEquality(
                    RemoveObjectConvert(methodCallExpression.Arguments[0]),
                    RemoveObjectConvert(methodCallExpression.Arguments[1]),
                    ExpressionType.Equal);
                if (entityRewrite != null)
                    return entityRewrite;

                var left = Visit(RemoveObjectConvert(methodCallExpression.Arguments[0]))!;
                var right = Visit(RemoveObjectConvert(methodCallExpression.Arguments[1]))!;
                var method = methodCallExpression.Method;

                if (left.Type == right.Type)
                {
                    return Expression.Equal(RemoveObjectConvert(left), RemoveObjectConvert(right));
                }

                var parameters = method.GetParameters();
                left = left.ConvertIfRequired(parameters[0].ParameterType);
                right = right.ConvertIfRequired(parameters[1].ParameterType);
                return Expression.Call(null, method, left, right);

            // Replace EF-generated Property(p, "propName") with Property(p.propName) or Mql.Field(p, "propName", serializer)
            case MethodCallExpression methodCallExpression
                when methodCallExpression.Method.IsEFPropertyMethod()
                     && methodCallExpression.Arguments[1] is ConstantExpression propertyNameExpression:
                var source = Visit(methodCallExpression.Arguments[0])
                             ?? throw new InvalidOperationException("Unsupported source to EF.Property expression.");

                var propertyName = propertyNameExpression.GetConstantValue<string>();
                var entityType = _queryContext.Context.Model.FindEntityType(source.Type);
                if (entityType != null)
                {
                    // Try an EF property
                    var efProperty = entityType.FindProperty(propertyName);
                    if (efProperty != null)
                    {
                        var doc = source;

                        // Composite keys need to go via the _id document
                        var isCompositeKeyAccess = efProperty.IsPrimaryKey() && entityType.FindPrimaryKey()?.Properties.Count > 1;
                        if (isCompositeKeyAccess)
                        {
                            var mqlFieldDoc = MqlFieldMethodInfo.MakeGenericMethod(source.Type, typeof(BsonValue));
                            doc = Expression.Call(null, mqlFieldDoc, source, Expression.Constant("_id"),
                                Expression.Constant(BsonValueSerializer.Instance));
                        }

                        var mqlField = MqlFieldMethodInfo.MakeGenericMethod(doc.Type, efProperty.ClrType);
                        var serializer = BsonSerializerFactory.CreateTypeSerializer(efProperty);
                        var callExpression = Expression.Call(null, mqlField, doc,
                            Expression.Constant(efProperty.GetElementName()),
                            Expression.Constant(serializer));
                        return callExpression.ConvertIfRequired(methodCallExpression.Method.ReturnType);
                    }

                    // Try an EF navigation if no property
                    var efNavigation = entityType.FindNavigation(propertyName);
                    if (efNavigation != null)
                    {
                        var elementName = efNavigation.TargetEntityType.GetContainingElementName();
                        var mqlField = MqlFieldMethodInfo.MakeGenericMethod(source.Type, efNavigation.ClrType);
                        var serializer = _bsonSerializerFactory.GetNavigationSerializer(efNavigation);
                        var callExpression = Expression.Call(null, mqlField, source,
                            Expression.Constant(elementName),
                            Expression.Constant(serializer));
                        return callExpression.ConvertIfRequired(methodCallExpression.Method.ReturnType);
                    }
                }

                // Try CLR property
                // This should not really be required but is kept here for backwards compatibility with any edge cases.
                var clrProperty = source.Type.GetProperties().FirstOrDefault(p => p.Name == propertyName);
                if (clrProperty != null)
                {
                    var propertyExpression = Expression.Property(source, clrProperty);
                    return propertyExpression.ConvertIfRequired(methodCallExpression.Method.ReturnType);
                }

                var defaultSerializer = BsonSerializer.LookupSerializer(methodCallExpression.Type);
                if (defaultSerializer != null)
                {
                    var mqlField = MqlFieldMethodInfo.MakeGenericMethod(source.Type, methodCallExpression.Type);
                    var callExpression = Expression.Call(null, mqlField, source,
                        propertyNameExpression,
                        Expression.Constant(defaultSerializer));
                    return callExpression.ConvertIfRequired(methodCallExpression.Method.ReturnType);
                }

                return VisitMethodCall(methodCallExpression);

            // Handle method call to VectorQuery
            case MethodCallExpression methodCallExpression
                when methodCallExpression.IsVectorSearch():
                return ProcessVectorSearch(methodCallExpression);

            case MethodCallExpression methodCallExpression:
                return VisitMethodCall(methodCallExpression);

            // Unwrap include expressions.
            case IncludeExpression includeExpression:
                return Visit(includeExpression.EntityExpression);

            // Replace the root with the MongoDB LINQ V3 provider source.
            case EntityQueryRootExpression entityQueryRootExpression:
                if (_foundEntityQueryRootExpression == null)
                {
                    _foundEntityQueryRootExpression = entityQueryRootExpression;
                    return _source;
                }

                // Check inner sources first (handles self-joins where outer and inner are the same type)
                if (_innerSources.TryGetValue(entityQueryRootExpression.EntityType, out var innerSource))
                {
                    return innerSource;
                }

                if (_foundEntityQueryRootExpression.EntityType == entityQueryRootExpression.EntityType)
                {
                    return _source;
                }

                throw new InvalidOperationException($"Unsupported cross-DbSet query between '{_foundEntityQueryRootExpression.EntityType.Name}' " +
                                                    $"and '{entityQueryRootExpression.EntityType.Name}'. " +
                                                    "The MongoDB EF Core Provider does not support this cross-collection query. " +
                                                    "Consider using Join, Include, or restructuring your query.");
        }

        return base.Visit(expression);

        Expression ProcessVectorSearch(MethodCallExpression methodCallExpression)
        {
            var propertyExpression = methodCallExpression.Arguments[1].UnwrapLambdaFromQuote();
            var preFilterExpression = methodCallExpression.Arguments[2] is UnaryExpression
                ? methodCallExpression.Arguments[2].UnwrapLambdaFromQuote()
                : null;
            var queryVector = ParamValue<QueryVector>(3);
            var limit = ParamValue<int>(4);
            var options = ParamValue<VectorQueryOptions?>(5);

            var concreteOptions = options ?? new();

            if (concreteOptions is { NumberOfCandidates: not null, Exact: true })
            {
                throw new InvalidOperationException(
                    "The option 'Exact' is set to 'true' on a call to 'VectorQuery', indicating an exact nearest neighbour (ENN) search, and the number of candidates has also been set. Either 'NumberOfCandidates' or 'Exact' can be set, but not both.");
            }

            var members = propertyExpression.GetMemberAccess<MemberInfo>();
            var entityType = _queryContext.Context.Model.FindEntityType(_source.Type.TryGetItemType()!);
            var memberMetadata = entityType?.FindMember(members[0].Name);

            if (memberMetadata == null)
            {
                throw new InvalidOperationException(
                    $"Could not create a vector query for '{(entityType?.ClrType ?? _source.Type).ShortDisplayName()}.{members[0].Name}'. Make sure the entity type is included in the EF Core model and that the property or field is mapped.");
            }

            foreach (var memberInfo in members.Skip(1))
            {
                memberMetadata = (memberMetadata as INavigation)?.TargetEntityType.FindMember(memberInfo.Name);
            }

            AdditionalState[MongoExecutableQuery.VectorQueryProperty] = memberMetadata!;

            var vectorIndexesInModel = memberMetadata?.DeclaringType.ContainingEntityType
                .GetIndexes().Where(i => i.GetVectorIndexOptions() != null && i.Properties[0] == memberMetadata).ToList();

            if (concreteOptions.IndexName == null)
            {
                // Index to use was not specified in the query. Throw or warn if there is anything but one index in the model.
                if (vectorIndexesInModel == null || vectorIndexesInModel.Count == 0)
                {
                    ThrowForBadOptions(
                        "the vector index for this query could not be found. Use 'HasIndex' on the EF model builder to specify the index, or " +
                        "specify the index name in the call to 'VectorQuery' if indexes are being managed outside of EF Core.");
                }

                if (vectorIndexesInModel!.Count > 1)
                {
                    ThrowForBadOptions(
                        "multiple vector indexes are defined for this property in the EF Core model. Specify the index to use in the call to 'VectorSearch'.");
                }

                // There is only one index and none was specified, so use that index.
                concreteOptions = concreteOptions with { IndexName = vectorIndexesInModel[0].Name };
            }
            else
            {
                // Index to use was specified in the query. Throw or warn if it doesn't match any index in the model.
                if (vectorIndexesInModel == null || vectorIndexesInModel.All(i => i.Name != concreteOptions.IndexName))
                {
                    _queryContext.QueryLogger.VectorSearchNeedsIndex((IProperty)memberMetadata!);
                }
                // Index name in query already matches, so just continue.
            }

            AdditionalState[MongoExecutableQuery.VectorQueryIndexName] = concreteOptions.IndexName!;

            var searchOptionsType = typeof(VectorSearchOptions<>).MakeGenericType(entityType!.ClrType);
            var searchOptions = Activator.CreateInstance(searchOptionsType)!;

            searchOptionsType.GetProperty(nameof(VectorSearchOptions<object>.IndexName))!.SetValue(searchOptions,
                concreteOptions.IndexName);
            searchOptionsType.GetProperty(nameof(VectorSearchOptions<object>.NumberOfCandidates))!.SetValue(searchOptions,
                concreteOptions.NumberOfCandidates);
            searchOptionsType.GetProperty(nameof(VectorSearchOptions<object>.Exact))!.SetValue(searchOptions,
                concreteOptions.Exact);

            if (preFilterExpression != null)
            {
                var convertedExpression = Activator.CreateInstance(
                    typeof(ExpressionFilterDefinition<>).MakeGenericType(entityType.ClrType),
                    Visit(preFilterExpression));

                searchOptionsType.GetProperty(nameof(VectorSearchOptions<object>.Filter))!.SetValue(searchOptions,
                    convertedExpression);
            }

            var vectorSearchPipelineStage = typeof(PipelineStageDefinitionBuilder)
                .GetTypeInfo().GetDeclaredMethods(nameof(PipelineStageDefinitionBuilder.VectorSearch))
                .Single(mi =>
                    mi.GetParameters()[0].ParameterType.IsGenericType
                    && mi.GetParameters()[0].ParameterType.GetGenericTypeDefinition() == typeof(Expression<>))
                .MakeGenericMethod(entityType.ClrType, memberMetadata!.ClrType)
                .Invoke(null, [propertyExpression, queryVector, limit, searchOptions]);

            var appendStageMethod = typeof(MongoQueryable).GetMethod(nameof(MongoQueryable.AppendStage))!
                .MakeGenericMethod(entityType.ClrType, entityType.ClrType);

            var serializerType = typeof(IBsonSerializer<>).MakeGenericType(entityType.ClrType);

            var vectorSource = Expression.Call(
                null,
                appendStageMethod,
                Visit(methodCallExpression.Arguments[0])!,
                Expression.Constant(vectorSearchPipelineStage),
                Expression.Constant(null, serializerType));

            return Expression.Call(
                null,
                appendStageMethod,
                vectorSource,
                Expression.New(
                    typeof(BsonDocumentPipelineStageDefinition<,>)
                        .MakeGenericType(entityType.ClrType, entityType.ClrType)
                        .GetConstructor([typeof(BsonDocument), serializerType])!,
                    Expression.Constant(AddScoreField),
                    Expression.Constant(null, serializerType)),
                Expression.Constant(null, serializerType));

            void ThrowForBadOptions(string reason)
            {
                throw new InvalidOperationException(
                    $"A vector query for '{entityType!.DisplayName()}.{members[0].Name}' could not be executed because {reason}");
            }

#if EF8 || EF9
            TValue? ParamValue<TValue>(int index)
                => (TValue?)_queryContext.ParameterValues[((ParameterExpression)methodCallExpression.Arguments[index]).Name!];
#else
            TValue? ParamValue<TValue>(int index)
                => (TValue?)_queryContext.Parameters[((QueryParameterExpression)methodCallExpression.Arguments[index]).Name];
#endif
        }
    }

    /// <summary>
    /// Appends $lookup stages for cross-collection collection Includes.
    /// Uses the same AppendStage pattern as VectorSearch.
    /// </summary>
    private Expression AppendLookupStages(Expression query)
    {
        if (_pendingLookups.Count == 0)
        {
            return query;
        }

        var sourceType = query.Type.TryGetItemType() ?? _source.Type.TryGetItemType()!;
        var appendStageMethod = typeof(MongoQueryable).GetMethod(nameof(MongoQueryable.AppendStage))!
            .MakeGenericMethod(sourceType, sourceType);
        var serializerType = typeof(IBsonSerializer<>).MakeGenericType(sourceType);
        var stageDefinitionType = typeof(BsonDocumentPipelineStageDefinition<,>).MakeGenericType(sourceType, sourceType);
        var stageConstructor = stageDefinitionType.GetConstructor([typeof(BsonDocument), serializerType])!;

        foreach (var lookup in _pendingLookups)
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

    private static readonly BsonDocument AddScoreField =
        new("$addFields", new BsonDocument { { "__score", new BsonDocument("$meta", "vectorSearchScore") } });

    protected override Expression VisitMethodCall(MethodCallExpression node)
    {
        if (node.Method.Name == nameof(Enumerable.Contains))
        {
            var rewrite = VisitContainsMethod(node);
            if (rewrite != null)
                return rewrite;
        }

        return base.VisitMethodCall(node);
    }

    private Expression? VisitContainsMethod(MethodCallExpression node)
    {
        Expression collectionExpr;
        Expression itemExpr;

        if (node is { Object: not null, Arguments.Count: 1 })
        {
            // Instance: list.Contains(item)
            collectionExpr = node.Object;
            itemExpr = node.Arguments[0];
        }
        else if (node.Object == null && node.Arguments.Count == 2
                 && (node.Method.DeclaringType == typeof(Enumerable)
                     || node.Method.DeclaringType == typeof(Queryable)))
        {
            // Static: Enumerable/Queryable.Contains(source, item)
            collectionExpr = node.Arguments[0];
            itemExpr = node.Arguments[1];
        }
        else
        {
            return null;
        }

        var rewrite = TryRewriteEntityContains(collectionExpr, itemExpr);
        return rewrite != null ? Visit(rewrite)! : null;
    }

    protected override Expression VisitBinary(BinaryExpression node)
    {
        if (node.NodeType is ExpressionType.Equal or ExpressionType.NotEqual)
        {
            var rewrite = TryRewriteEntityEquality(node.Left, node.Right, node.NodeType);
            if (rewrite != null)
                return rewrite;
        }

        return base.VisitBinary(node);
    }

    /// <summary>
    /// Find an entity type in the model, preferring owned types (which require searching all entity types).
    /// </summary>
    private IEntityType? FindEntityType(Type clrType)
        => _queryContext.Context.Model.GetEntityTypes().FirstOrDefault(e => e.ClrType == clrType && e.IsOwned())
           ?? _queryContext.Context.Model.FindEntityType(clrType);

    /// <summary>
    /// Get the properties to use for entity comparison: primary key properties for non-owned entities,
    /// all CLR-mapped properties for owned entities.
    /// Returns null if no usable properties are found.
    /// </summary>
    private static IReadOnlyList<IProperty>? GetComparisonProperties(IEntityType entityType)
    {
        if (entityType.IsOwned())
        {
            var properties = entityType.GetProperties()
                .Where(p => p.PropertyInfo != null || p.FieldInfo != null) // exclude shadow properties — they have no CLR member to read a constant value from
                .ToList();
            return properties.Count > 0 ? properties : null;
        }

        return entityType.FindPrimaryKey()?.Properties;
    }

    /// <summary>
    /// Build a balanced equality expression comparing a query-side expression to a constant entity value
    /// across all comparison properties. For owned entities uses direct member access, for non-owned uses EF.Property.
    /// </summary>
    private static Expression CreateEntityEqualityExpression(
        Expression querySide, object constantEntity, IReadOnlyList<IProperty> properties,
        bool isOwned, ExpressionType nodeType = ExpressionType.Equal)
    {
        var comparisons = properties.Select(p =>
        {
            Expression queryAccess;
            Expression constantExpr;

            if (isOwned)
            {
                queryAccess = p.PropertyInfo != null
                    ? Expression.Property(querySide, p.PropertyInfo)
                    : Expression.Field(querySide, p.FieldInfo!);
                // Shadow properties are excluded by GetComparisonProperties so PropertyInfo/FieldInfo is always non-null here
                var value = p.PropertyInfo?.GetValue(constantEntity) ?? p.FieldInfo?.GetValue(constantEntity);
                constantExpr = Expression.Constant(value, p.ClrType);
            }
            else
            {
                queryAccess = CreateEfPropertyExpression(querySide, p);
                constantExpr = ExtractKeyValue(constantEntity, p);
            }

            return nodeType == ExpressionType.Equal
                ? Expression.Equal(queryAccess, constantExpr)
                : Expression.NotEqual(queryAccess, constantExpr);
        }).ToList();

        return CombineBalanced(comparisons, nodeType == ExpressionType.Equal
            ? Expression.AndAlso
            : Expression.OrElse);
    }

    /// <summary>
    /// Combine a list of expressions into a balanced binary tree using the given combiner.
    /// </summary>
    private static Expression CombineBalanced(IReadOnlyList<Expression> expressions,
        Func<Expression, Expression, BinaryExpression> combiner)
    {
        return expressions.Count switch
        {
            1 => expressions[0],
            2 => combiner(expressions[0], expressions[1]),
            _ => combiner(
                CombineBalanced(expressions.Take(expressions.Count / 2).ToList(), combiner),
                CombineBalanced(expressions.Skip(expressions.Count / 2).ToList(), combiner))
        };
    }

    private Expression? TryRewriteEntityEquality(Expression left, Expression right, ExpressionType nodeType)
    {
        var (entitySide, constantValue) = ResolveEntityComparison(left, right);
        if (entitySide == null || constantValue == null)
            return null;

        var entityType = FindEntityType(entitySide.RemoveConvert().Type);
        if (entityType == null)
            return null;

        var properties = GetComparisonProperties(entityType);
        if (properties == null)
            return null;

        return Visit(CreateEntityEqualityExpression(entitySide, constantValue, properties, entityType.IsOwned(), nodeType))!;
    }

    private Expression? TryRewriteEntityContains(Expression collection, Expression item)
    {
        var elementType = collection.Type.TryGetItemType();
        if (elementType == null)
            return null;

        var entityType = FindEntityType(elementType);
        if (entityType == null)
            return null;

        var properties = GetComparisonProperties(entityType);
        if (properties == null)
            return null;

        // Item is constant
        var constantValue = TryEvaluateToConstant(item);
        if (constantValue != null)
        {
            var parameter = Expression.Parameter(elementType, "__x");
            var predicate = CreateEntityEqualityExpression(parameter, constantValue, properties, entityType.IsOwned());
            var lambda = Expression.Lambda(predicate, parameter);

            var isQueryable = collection.Type.IsGenericType
                              && collection.Type.GetGenericTypeDefinition() == typeof(IQueryable<>);
            var anySource = isQueryable ? typeof(Queryable) : typeof(Enumerable);
            var anyMethod = anySource.GetMethods()
                .First(m => m.Name == nameof(Enumerable.Any) && m.GetParameters().Length == 2)
                .MakeGenericMethod(elementType);

            return Expression.Call(null, anyMethod, collection,
                isQueryable ? Expression.Quote(lambda) : lambda);
        }

        // Collection is constant
        var constantCollection = TryEvaluateToConstant(collection);
        if (constantCollection is System.Collections.IEnumerable enumerable)
        {
            var allEntities = enumerable.Cast<object?>().ToList();
            if (allEntities.Count == 0)
                return Expression.Constant(false);

            var parts = new List<Expression>();

            if (allEntities.Any(e => e == null))
                parts.Add(Expression.Equal(item, Expression.Constant(null, item.Type)));

            foreach (var entity in allEntities.Where(e => e != null))
                parts.Add(CreateEntityEqualityExpression(item, entity!, properties, entityType.IsOwned()));

            return CombineBalanced(parts, Expression.OrElse);
        }

        return null;
    }

    private (Expression? entitySide, object? constantValue) ResolveEntityComparison(Expression left, Expression right)
    {
        var result = TryResolveEntityComparison(left, right);
        return result.entitySide != null
            ? result :
            TryResolveEntityComparison(right, left); // Try swapped
    }

    private (Expression? entitySide, object? constantValue) TryResolveEntityComparison(
        Expression candidateEntity, Expression candidateConstant)
    {
        var unwrappedEntity = candidateEntity.RemoveConvert();
        var entityType = FindEntityType(unwrappedEntity.Type);
        if (entityType == null)
            return (null, null);

        // If neither side evaluates to a constant (e.g. entity == entity), we return null and let EF Core handle it
        var constantValue = TryEvaluateToConstant(candidateConstant);
        if (constantValue == null)
            return (null, null);

        return (candidateEntity, constantValue);
    }

    private object? TryEvaluateToConstant(Expression expression)
    {
        expression = expression.RemoveConvert();

        if (expression is ConstantExpression constant)
            return constant.Value;

        if (expression is MemberExpression { Expression: ConstantExpression closureConstant } member)
        {
            return member.Member switch
            {
                FieldInfo field => field.GetValue(closureConstant.Value),
                PropertyInfo prop => prop.GetValue(closureConstant.Value),
                _ => null
            };
        }

#if EF8 || EF9
        if (expression is ParameterExpression param
            && param.Name?.StartsWith(QueryCompilationContext.QueryParameterPrefix, StringComparison.Ordinal) == true
            && _queryContext.ParameterValues.TryGetValue(param.Name, out var value))
            return value;
#else
        if (expression is QueryParameterExpression queryParam)
            return _queryContext.Parameters[queryParam.Name];
#endif

        // Fallback: try to compile and evaluate the expression (handles MemberInitExpression, nested closures, etc.)
        try
        {
            var lambda = Expression.Lambda<Func<object>>(Expression.Convert(expression, typeof(object)));
            return lambda.Compile()();
        }
        catch
        {
            return null;
        }
    }

    private static MethodCallExpression CreateEfPropertyExpression(Expression source, IProperty property)
    {
        var method = EFPropertyMethodInfo.MakeGenericMethod(property.ClrType);
        return Expression.Call(null, method, source, Expression.Constant(property.Name));
    }

    private static ConstantExpression ExtractKeyValue(object entity, IProperty property)
    {
        if (property.PropertyInfo == null && property.FieldInfo == null)
            throw new NotSupportedException($"Entity comparison on shadow key property '{property.Name}' is not supported.");

        var value = property.PropertyInfo?.GetValue(entity) ?? property.FieldInfo?.GetValue(entity);
        return Expression.Constant(value, property.ClrType);
    }

    private static Expression RemoveObjectConvert(Expression expression)
        => expression is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unaryExpression
           && unaryExpression.Type == typeof(object)
            ? unaryExpression.Operand
            : expression;

    private static readonly MethodInfo AsMethodInfo = typeof(MongoQueryable)
        .GetMethods()
        .First(mi => mi is { Name: nameof(MongoQueryable.As), IsPublic: true, IsStatic: true } && mi.GetParameters().Length == 2);
}
