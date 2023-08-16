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
using Microsoft.EntityFrameworkCore.Query;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.Query.Expressions;

namespace MongoDB.EntityFrameworkCore.Query.Visitors;

/// <inheritdoc/>
internal class MongoShapedQueryCompilingExpressionVisitor : ShapedQueryCompilingExpressionVisitor
{
    private readonly Type _contextType;
    private readonly bool _threadSafetyChecksEnabled;

    static readonly MethodInfo __translateAndExecuteQuery = typeof(MongoShapedQueryCompilingExpressionVisitor)
        .GetTypeInfo()
        .DeclaredMethods
        .Single(m => m.Name == nameof(TranslateAndExecuteQuery));

    /// <summary>
    /// Create a <see cref="MongoShapedQueryCompilingExpressionVisitor"/> with the required dependencies and compilation context.
    /// </summary>
    /// <param name="dependencies">The <see cref="ShapedQueryCompilingExpressionVisitorDependencies"/> used by this compiler.</param>
    /// <param name="queryCompilationContext">The <see cref="MongoQueryCompilationContext"/> gor this specific query.</param>
    public MongoShapedQueryCompilingExpressionVisitor(
        ShapedQueryCompilingExpressionVisitorDependencies dependencies,
        MongoQueryCompilationContext queryCompilationContext)
        : base(dependencies, queryCompilationContext)
    {
        _contextType = queryCompilationContext.ContextType;
        _threadSafetyChecksEnabled = dependencies.CoreSingletonOptions.AreThreadSafetyChecksEnabled;
    }

    /// <inheritdoc/>
    protected override Expression VisitShapedQuery(ShapedQueryExpression shapedQueryExpression)
    {
        var shaperLambda = CreateShaperLambda(shapedQueryExpression.ShaperExpression);

        var queryExpression = (MongoQueryExpression)shapedQueryExpression.QueryExpression;
        var rootEntityType = queryExpression.CollectionExpression.EntityType;
        var projectedType = shaperLambda.ReturnType;
        bool standAloneStateManager = QueryCompilationContext.QueryTrackingBehavior ==
                                      QueryTrackingBehavior.NoTrackingWithIdentityResolution;

        return Expression.Call(null,
            __translateAndExecuteQuery.MakeGenericMethod(rootEntityType.ClrType, projectedType),
            QueryCompilationContext.QueryContextParameter,
            Expression.Constant(queryExpression),
            Expression.Constant(shaperLambda.Compile()),
            Expression.Constant(_contextType),
            Expression.Constant(standAloneStateManager),
            Expression.Constant(_threadSafetyChecksEnabled),
            Expression.Constant(shapedQueryExpression.ResultCardinality));
    }

    private LambdaExpression CreateShaperLambda(Expression shaperExpression)
    {
        var bsonDocParameter = Expression.Parameter(typeof(BsonDocument), "bsonDoc");

        var shaperBody = new BsonDocumentInjectingExpressionVisitor().Visit(shaperExpression);
        shaperBody = InjectEntityMaterializers(shaperBody);
        shaperBody = new ValueBufferToBsonBindingExpressionVisitor(bsonDocParameter).Visit(shaperBody);

        return Expression.Lambda(
            shaperBody,
            QueryCompilationContext.QueryContextParameter,
            bsonDocParameter);
    }

    private static IEnumerable<TResult> TranslateAndExecuteQuery<TSource, TResult>(
        QueryContext queryContext,
        MongoQueryExpression queryExpression,
        Func<QueryContext, BsonDocument, TResult> shaper,
        Type contextType,
        bool standAloneStateManager,
        bool threadSafetyChecksEnabled,
        ResultCardinality resultCardinality)
    {
        var mongoQueryContext = (MongoQueryContext)queryContext;
        var collectionName = queryExpression.CollectionExpression.CollectionName;
        var source = mongoQueryContext.MongoClient.Database.GetCollection<TSource>(collectionName).AsQueryable();
        var queryTranslator = new MongoEFToLinqTranslatingExpressionVisitor(queryContext, source.Expression);

        var translatedQuery = queryTranslator.Translate(queryExpression.CapturedExpression, resultCardinality);

        IEnumerable<BsonDocument> documents = resultCardinality == ResultCardinality.Enumerable
            ? source.Provider.CreateQuery<BsonDocument>(translatedQuery)
            : new[] {source.Provider.Execute<BsonDocument>(translatedQuery)};

        return new QueryingEnumerable<TResult>(
            mongoQueryContext,
            documents,
            shaper,
            contextType,
            standAloneStateManager,
            threadSafetyChecksEnabled);
    }
}
