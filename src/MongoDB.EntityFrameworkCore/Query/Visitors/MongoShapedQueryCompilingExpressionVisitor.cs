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
using MongoDB.EntityFrameworkCore.Diagnostics;
using MongoDB.EntityFrameworkCore.Query.Expressions;
using MongoDB.EntityFrameworkCore.Query.Visitors.Dependencies;
using MongoDB.EntityFrameworkCore.Serializers;

namespace MongoDB.EntityFrameworkCore.Query.Visitors;

/// <inheritdoc/>
internal sealed class MongoShapedQueryCompilingExpressionVisitor : ShapedQueryCompilingExpressionVisitor
{
    private readonly Type _contextType;
    private readonly bool _threadSafetyChecksEnabled;
    private readonly BsonSerializerFactory _bsonSerializerFactory;

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
        _bsonSerializerFactory = mongoDependencies.BsonSerializerFactory;
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
            if (mongoQueryExpression.CapturedExpression is MethodCallExpression { Method.IsGenericMethod: true } mce
                && mce.Method.GetGenericMethodDefinition() == QueryableMethods.Select)
            {
            }
            else
            {
                // We are relying on raw/scalar values coming back from LINQ V3 provider for now - no shaper required
                return Expression.Call(null,
                    TranslateAndExecuteUnshapedQueryMethodInfo.MakeGenericMethod(rootEntityType.ClrType,
                        shapedQueryExpression.ShaperExpression.Type),
                    QueryCompilationContext.QueryContextParameter,
                    Expression.Constant(rootEntityType),
                    Expression.Constant(_bsonSerializerFactory),
                    Expression.Constant(mongoQueryExpression),
                    Expression.Constant(_contextType),
                    Expression.Constant(_threadSafetyChecksEnabled),
                    Expression.Constant(shapedQueryExpression.ResultCardinality));
            }
        }

        var bsonDocParameter = Expression.Parameter(typeof(BsonDocument), "bsonDoc");
        var trackQueryResults = QueryCompilationContext.QueryTrackingBehavior == QueryTrackingBehavior.TrackAll;

        var shaperBody = shapedQueryExpression.ShaperExpression;
        shaperBody = new BsonDocumentInjectingExpressionVisitor().Visit(shaperBody);
        shaperBody = InjectEntityMaterializers(shaperBody);
        shaperBody = new MongoProjectionBindingRemovingExpressionVisitor(mongoQueryExpression, bsonDocParameter, trackQueryResults)
            .Visit(shaperBody);

        var shaperLambda = Expression.Lambda(
            shaperBody,
            QueryCompilationContext.QueryContextParameter,
            bsonDocParameter);
        var compiledShaper = shaperLambda.Compile();

        var projectedType = shaperLambda.ReturnType;
        var standAloneStateManager = QueryCompilationContext.QueryTrackingBehavior ==
                                     QueryTrackingBehavior.NoTrackingWithIdentityResolution;

        return Expression.Call(null,
            TranslateAndExecuteQueryMethodInfo.MakeGenericMethod(rootEntityType.ClrType, projectedType),
            QueryCompilationContext.QueryContextParameter,
            Expression.Constant(rootEntityType),
            Expression.Constant(_bsonSerializerFactory),
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
        BsonSerializerFactory bsonSerializerFactory,
        MongoQueryExpression queryExpression,
        Type contextType,
        bool threadSafetyChecksEnabled,
        ResultCardinality resultCardinality)
    {
        var mongoQueryContext = (MongoQueryContext)queryContext;
        var serializer = (IBsonSerializer<TSource>)bsonSerializerFactory.GetEntitySerializer(entityType);
        var collection = mongoQueryContext.MongoClient.GetCollection<TSource>(queryExpression.CollectionExpression.CollectionName);
        var source = collection.AsQueryable().As(serializer);

        var queryTranslator = new MongoEFToLinqTranslatingExpressionVisitor(queryContext, source.Expression, bsonSerializerFactory);
        var translatedQuery = queryTranslator.Visit(queryExpression.CapturedExpression)!;

        var executableQuery =
            new MongoExecutableQuery(translatedQuery, resultCardinality, (IMongoQueryProvider)source.Provider,
                collection.CollectionNamespace);

        return new QueryingEnumerable<TResult, TResult>(
            mongoQueryContext,
            executableQuery,
            (_, e) => e,
            contextType,
            standAloneStateManager: false,
            threadSafetyChecksEnabled,
            onZeroResults: null);
    }

    private static QueryingEnumerable<BsonDocument, TResult> TranslateAndExecuteQuery<TSource, TResult>(
        QueryContext queryContext,
        IReadOnlyEntityType entityType,
        BsonSerializerFactory bsonSerializerFactory,
        MongoQueryExpression queryExpression,
        Func<QueryContext, BsonDocument, TResult> shaper,
        Type contextType,
        bool standAloneStateManager,
        bool threadSafetyChecksEnabled,
        ResultCardinality resultCardinality)
    {
        var mongoQueryContext = (MongoQueryContext)queryContext;
        var collection = mongoQueryContext.MongoClient.GetCollection<TSource>(queryExpression.CollectionExpression.CollectionName);
        var source = collection.AsQueryable().As((IBsonSerializer<TSource>)bsonSerializerFactory.GetEntitySerializer(entityType));

        var queryTranslator = new MongoEFToLinqTranslatingExpressionVisitor(queryContext, source.Expression, bsonSerializerFactory);
        var translatedQuery = queryTranslator.Translate(queryExpression.CapturedExpression, resultCardinality);

        var executableQuery = new MongoExecutableQuery(translatedQuery, resultCardinality, (IMongoQueryProvider)source.Provider,
            collection.CollectionNamespace);

        Action<MongoQueryContext, MongoExecutableQuery>? onZeroResults = null;
        if (queryExpression.CapturedExpression is MethodCallExpression methodCallExpression)
        {
            if (methodCallExpression.Method.Name == "Select" && methodCallExpression.Arguments is [MethodCallExpression mce, _])
            {
                methodCallExpression = mce;
            }

            if (methodCallExpression.IsVectorSearch())
            {
                onZeroResults = (qc, eq) => qc.QueryLogger.VectorSearchReturnedZeroResults(eq);
            }
        }

        return new QueryingEnumerable<BsonDocument, TResult>(
            mongoQueryContext,
            executableQuery,
            shaper,
            contextType,
            standAloneStateManager,
            threadSafetyChecksEnabled,
            onZeroResults);
    }

    private static readonly MethodInfo TranslateAndExecuteQueryMethodInfo =
        typeof(MongoShapedQueryCompilingExpressionVisitor)
            .GetTypeInfo()
            .DeclaredMethods
            .Single(m => m.Name == nameof(TranslateAndExecuteQuery));

    private static readonly MethodInfo TranslateAndExecuteUnshapedQueryMethodInfo =
        typeof(MongoShapedQueryCompilingExpressionVisitor)
            .GetTypeInfo()
            .DeclaredMethods
            .Single(m => m.Name == nameof(TranslateAndExecuteUnshapedQuery));
}
