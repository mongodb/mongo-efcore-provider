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
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using MongoDB.Driver.Linq;
using MongoDB.EntityFrameworkCore.Query.Expressions;
using MongoDB.EntityFrameworkCore.Query.Visitors.Dependencies;
using MongoDB.EntityFrameworkCore.Serializers;

namespace MongoDB.EntityFrameworkCore.Query.Visitors;

/// <inheritdoc/>
internal sealed class MongoShapedQueryCompilingExpressionVisitor : ShapedQueryCompilingExpressionVisitor
{
    private readonly Type _contextType;
    private readonly bool _threadSafetyChecksEnabled;
    private readonly EntitySerializerCache _entitySerializerCache;

    /// <summary>
    /// Create a <see cref="MongoShapedQueryCompilingExpressionVisitor"/> with the required dependencies and compilation context.
    /// </summary>
    /// <param name="dependencies">The <see cref="ShapedQueryCompilingExpressionVisitorDependencies"/> used by this visitor.</param>
    /// <param name="mongoDependencies">MongoDB-specific dependencies used by this visitor.</param>
    /// <param name="queryCompilationContext">The <see cref="MongoQueryCompilationContext"/> for this specific query.</param>
    public MongoShapedQueryCompilingExpressionVisitor(
        ShapedQueryCompilingExpressionVisitorDependencies dependencies,
        MongoShapedQueryCompilingExpressionVisitorDependencies mongoDependencies,
        MongoQueryCompilationContext queryCompilationContext)
        : base(dependencies, queryCompilationContext)
    {
        _contextType = queryCompilationContext.ContextType;
        _threadSafetyChecksEnabled = dependencies.CoreSingletonOptions.AreThreadSafetyChecksEnabled;
        _entitySerializerCache = mongoDependencies.EntitySerializerCache;
    }

    /// <inheritdoc/>
    protected override Expression VisitShapedQuery(ShapedQueryExpression shapedQueryExpression)
    {
        if (shapedQueryExpression.QueryExpression is not MongoQueryExpression mongoQueryExpression)
            throw new NotSupportedException(
                $" Unhandled expression node type '{nameof(shapedQueryExpression.QueryExpression)}'");

        var rootEntityType = mongoQueryExpression.CollectionExpression.EntityType;
        var projectedEntityType = QueryCompilationContext.Model.FindEntityType(
            shapedQueryExpression.ResultCardinality == ResultCardinality.Enumerable
                ? shapedQueryExpression.Type.TryGetItemType()!
                : shapedQueryExpression.Type);

        // TODO: Handle select expressions and non-EF shapers more comprehensively
        if (projectedEntityType == null)
        {
            // We are relying on raw/scalar values coming back from LINQ V3 provider for now - no shaper required
            return Expression.Call(null,
                __translateAndExecuteUnshapedQuery.MakeGenericMethod(rootEntityType.ClrType,
                    shapedQueryExpression.ShaperExpression.Type),
                QueryCompilationContext.QueryContextParameter,
                Expression.Constant(rootEntityType),
                Expression.Constant(_entitySerializerCache),
                Expression.Constant(mongoQueryExpression),
                Expression.Constant(_contextType),
                Expression.Constant(_threadSafetyChecksEnabled),
                Expression.Constant(shapedQueryExpression.ResultCardinality));
        }

        var bsonDocParameter = Expression.Parameter(typeof(BsonDocument), "bsonDoc");
        bool trackQueryResults = QueryCompilationContext.QueryTrackingBehavior == QueryTrackingBehavior.TrackAll;

        var shaperBody = shapedQueryExpression.ShaperExpression;
        shaperBody = new BsonDocumentInjectingExpressionVisitor().Visit(shaperBody);
        shaperBody = InjectEntityMaterializers(shaperBody);
        shaperBody = new MongoProjectionBindingRemovingExpressionVisitor(
                rootEntityType, mongoQueryExpression, bsonDocParameter, trackQueryResults)
            .Visit(shaperBody);

        var shaperLambda = Expression.Lambda(
            shaperBody,
            QueryCompilationContext.QueryContextParameter,
            bsonDocParameter);
        var compiledShaper = shaperLambda.Compile();

        var projectedType = shaperLambda.ReturnType;
        bool standAloneStateManager = QueryCompilationContext.QueryTrackingBehavior ==
                                      QueryTrackingBehavior.NoTrackingWithIdentityResolution;

        return Expression.Call(null,
            __translateAndExecuteQuery.MakeGenericMethod(rootEntityType.ClrType, projectedType),
            QueryCompilationContext.QueryContextParameter,
            Expression.Constant(rootEntityType),
            Expression.Constant(_entitySerializerCache),
            Expression.Constant(mongoQueryExpression),
            Expression.Constant(compiledShaper),
            Expression.Constant(_contextType),
            Expression.Constant(standAloneStateManager),
            Expression.Constant(_threadSafetyChecksEnabled),
            Expression.Constant(shapedQueryExpression.ResultCardinality));
    }

    private static QueryingEnumerable<TResult, TResult> TranslateAndExecuteUnshapedQuery<TSource, TResult>(
        QueryContext queryContext,
        IReadOnlyEntityType entityType,
        EntitySerializerCache entitySerializerCache,
        MongoQueryExpression queryExpression,
        Type contextType,
        bool threadSafetyChecksEnabled,
        ResultCardinality resultCardinality)
    {
        var mongoQueryContext = (MongoQueryContext)queryContext;
        var serializer = (IBsonSerializer<TSource>)entitySerializerCache.GetOrCreateSerializer(entityType);
        var collection = mongoQueryContext.MongoClient.Database.GetCollection<TSource>(queryExpression.CollectionExpression.CollectionName);
        var source = collection.AsQueryable().As(serializer);

        var queryTranslator = new MongoEFToLinqTranslatingExpressionVisitor(queryContext, source.Expression);
        var translatedQuery = queryTranslator.Visit(queryExpression.CapturedExpression)!;

        var executableQuery = new MongoExecutableQuery(translatedQuery, resultCardinality, source.Provider, collection.CollectionNamespace);

        return new QueryingEnumerable<TResult, TResult>(
            mongoQueryContext,
            executableQuery,
            (_, e) => e,
            contextType,
            false,
            threadSafetyChecksEnabled);
    }

    private static QueryingEnumerable<BsonDocument, TResult> TranslateAndExecuteQuery<TSource, TResult>(
        QueryContext queryContext,
        IReadOnlyEntityType entityType,
        EntitySerializerCache entitySerializerCache,
        MongoQueryExpression queryExpression,
        Func<QueryContext, BsonDocument, TResult> shaper,
        Type contextType,
        bool standAloneStateManager,
        bool threadSafetyChecksEnabled,
        ResultCardinality resultCardinality)
    {
        var mongoQueryContext = (MongoQueryContext)queryContext;
        var collection = mongoQueryContext.MongoClient.Database.GetCollection<TSource>(queryExpression.CollectionExpression.CollectionName);
        var source = collection.AsQueryable().As((IBsonSerializer<TSource>)entitySerializerCache.GetOrCreateSerializer(entityType));

        var queryTranslator = new MongoEFToLinqTranslatingExpressionVisitor(queryContext, source.Expression);
        var translatedQuery = queryTranslator.Translate(queryExpression.CapturedExpression, resultCardinality);

        var executableQuery = new MongoExecutableQuery(translatedQuery, resultCardinality, source.Provider, collection.CollectionNamespace);

        return new QueryingEnumerable<BsonDocument, TResult>(
            mongoQueryContext,
            executableQuery,
            shaper,
            contextType,
            standAloneStateManager,
            threadSafetyChecksEnabled);
    }

    private static readonly MethodInfo __translateAndExecuteQuery = typeof(MongoShapedQueryCompilingExpressionVisitor)
        .GetTypeInfo()
        .DeclaredMethods
        .Single(m => m.Name == nameof(TranslateAndExecuteQuery));

    private static readonly MethodInfo __translateAndExecuteUnshapedQuery = typeof(MongoShapedQueryCompilingExpressionVisitor)
        .GetTypeInfo()
        .DeclaredMethods
        .Single(m => m.Name == nameof(TranslateAndExecuteUnshapedQuery));
}
