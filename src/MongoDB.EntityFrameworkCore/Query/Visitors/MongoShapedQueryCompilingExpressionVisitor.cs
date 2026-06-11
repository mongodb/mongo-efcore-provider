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
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
#if !EF8
using System.Diagnostics;
using System.Threading;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
#endif
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
using MongoDB.EntityFrameworkCore.Storage;

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
    /// <param name="queryCompilationContext">The <see cref="QueryCompilationContext"/> for this specific query.</param>
    public MongoShapedQueryCompilingExpressionVisitor(
        ShapedQueryCompilingExpressionVisitorDependencies dependencies,
        MongoShapedQueryCompilingExpressionVisitorDependencies mongoDependencies,
        QueryCompilationContext queryCompilationContext)
        : base(dependencies, queryCompilationContext)
    {
        _contextType = queryCompilationContext.ContextType;
        _threadSafetyChecksEnabled = dependencies.CoreSingletonOptions.AreThreadSafetyChecksEnabled;
        _bsonSerializerFactory = mongoDependencies.BsonSerializerFactory;
    }

#if !EF8
    /// <inheritdoc/>
    protected override Expression VisitExtension(Expression extensionExpression)
        => extensionExpression is MongoNonQueryExpression nonQueryExpression
            ? VisitNonQuery(nonQueryExpression)
            : base.VisitExtension(extensionExpression);

    private Expression VisitNonQuery(MongoNonQueryExpression nonQueryExpression)
    {
        var entityType = nonQueryExpression.SourceQuery.CollectionExpression.EntityType;

        if (nonQueryExpression.Strategy == MongoNonQueryExpression.BulkStrategy.TwoPhase)
        {
            // Two-phase: phase 1 materializes matched entities and extracts their stored _id,
            // phase 2 deletes/updates by { _id: { $in: [...] } } — both inside a transaction. Works for
            // scalar and composite primary keys because we read the raw _id BsonValue from the serialized doc.
            EnsureBulkKeyOrThrow(entityType, nonQueryExpression);
            var twoPhaseExecutor = nonQueryExpression.Kind == MongoNonQueryExpression.OperationKind.Update
                ? (QueryCompilationContext.IsAsync ? ExecuteTwoPhaseUpdateAsyncMethodInfo : ExecuteTwoPhaseUpdateMethodInfo)
                : (QueryCompilationContext.IsAsync ? ExecuteTwoPhaseDeleteAsyncMethodInfo : ExecuteTwoPhaseDeleteMethodInfo);
            return Expression.Call(null,
                twoPhaseExecutor.MakeGenericMethod(entityType.ClrType),
                QueryCompilationContext.QueryContextParameter,
                Expression.Constant(entityType),
                Expression.Constant(_bsonSerializerFactory),
                Expression.Constant(nonQueryExpression));
        }

        var executor = nonQueryExpression.Kind switch
        {
            MongoNonQueryExpression.OperationKind.Update =>
                QueryCompilationContext.IsAsync ? ExecuteUpdateAsyncMethodInfo : ExecuteUpdateMethodInfo,
            _ => QueryCompilationContext.IsAsync ? ExecuteDeleteAsyncMethodInfo : ExecuteDeleteMethodInfo
        };

        return Expression.Call(null,
            executor.MakeGenericMethod(entityType.ClrType),
            QueryCompilationContext.QueryContextParameter,
            Expression.Constant(entityType),
            Expression.Constant(_bsonSerializerFactory),
            Expression.Constant(nonQueryExpression));
    }
#endif

    /// <inheritdoc/>
    protected override Expression VisitShapedQuery(ShapedQueryExpression shapedQueryExpression)
    {
        if (shapedQueryExpression.QueryExpression is not MongoQueryExpression mongoQueryExpression)
        {
            throw new NotSupportedException($" Unhandled expression node type '{nameof(shapedQueryExpression.QueryExpression)}'");
        }

        var rootEntityType = mongoQueryExpression.CollectionExpression.EntityType;
        var projectedEntityType = QueryCompilationContext.Model.FindEntityType(
            shapedQueryExpression.ResultCardinality == ResultCardinality.Enumerable
                ? shapedQueryExpression.Type.TryGetItemType()!
                : shapedQueryExpression.Type);

        if (projectedEntityType == null)
        {
            return VisitProjectedQuery(shapedQueryExpression, rootEntityType, mongoQueryExpression);
        }

        // Entity path: full BsonDocuments shaped into tracked/untracked entity instances
        return CompileShapedQuery(shapedQueryExpression, mongoQueryExpression, rootEntityType,
            (bsonDoc, behavior) => new MongoProjectionBindingRemovingExpressionVisitor(
                rootEntityType, mongoQueryExpression, bsonDoc, behavior));
    }

    private MethodCallExpression VisitProjectedQuery(
        ShapedQueryExpression shapedQueryExpression,
        IEntityType rootEntityType,
        MongoQueryExpression mongoQueryExpression)
    {
        VerifyNoClientConstant(shapedQueryExpression.ShaperExpression);

        if (ProjectionAnalyzer.CanPushDown(shapedQueryExpression.ShaperExpression))
        {
            // Push-down path: scalar/anonymous projections handled entirely by LINQ V3
            return Expression.Call(null,
                ExecuteProjectedQueryMethodInfo.MakeGenericMethod(rootEntityType.ClrType,
                    shapedQueryExpression.ShaperExpression.Type),
                QueryCompilationContext.QueryContextParameter,
                Expression.Constant(rootEntityType),
                Expression.Constant(_bsonSerializerFactory),
                Expression.Constant(mongoQueryExpression),
                Expression.Constant(_contextType),
                Expression.Constant(_threadSafetyChecksEnabled),
                Expression.Constant(shapedQueryExpression.ResultCardinality));
        }

        // Mixed path: projection contains entity references that LINQ V3 can't handle.
        // Strip the Select so the driver returns full BsonDocuments keyed by EF-configured
        // element names; the client-side shaper handles the projection. The Select may sit
        // directly on the captured expression, or under a no-arg cardinality terminator
        // (Single/First/etc.) which we also need to rebind to the un-projected source type.
        mongoQueryExpression.CapturedExpression = StripPushedDownSelect(mongoQueryExpression.CapturedExpression);

        return CompileShapedQuery(shapedQueryExpression, mongoQueryExpression, rootEntityType,
            (bsonDoc, behavior) => new MongoMixedProjectionBindingRemovingExpressionVisitor(
                rootEntityType, mongoQueryExpression, bsonDoc, behavior));
    }

    /// <summary>
    /// Remove the projection <c>Select</c> from the captured query chain so the shaper runs client-side
    /// over full <see cref="BsonDocument"/>s. The Select may be the outermost node, or wrapped by a single
    /// no-arg cardinality terminator (e.g. <c>First</c>, <c>Single</c>) emitted by EF Core for cardinality
    /// reducers such as <c>AssertFirst</c>. The terminal operator is preserved with its generic argument
    /// retargeted to the Select's source element type.
    /// </summary>
    private static Expression? StripPushedDownSelect(Expression? captured)
    {
        if (captured is not MethodCallExpression call || call.Method.DeclaringType != typeof(Queryable))
        {
            return captured;
        }

        if (call.Method.Name == nameof(Queryable.Select) && call.Arguments.Count == 2)
        {
            return call.Arguments[0];
        }

        if (call.Method.IsGenericMethod
            && call.Method.GetParameters().Length == 1
            && call.Method.Name is nameof(Queryable.Single) or nameof(Queryable.SingleOrDefault)
                or nameof(Queryable.First) or nameof(Queryable.FirstOrDefault)
                or nameof(Queryable.Last) or nameof(Queryable.LastOrDefault)
            && call.Arguments is [MethodCallExpression { Method: { Name: nameof(Queryable.Select), DeclaringType: var st } } innerSelect]
            && st == typeof(Queryable))
        {
            var newSource = innerSelect.Arguments[0];
            var newSourceType = newSource.Type.GetGenericArguments()[0];
            var rebound = call.Method.GetGenericMethodDefinition().MakeGenericMethod(newSourceType);
            return Expression.Call(rebound, newSource);
        }

        return captured;
    }

    private MethodCallExpression CompileShapedQuery(
        ShapedQueryExpression shapedQueryExpression,
        MongoQueryExpression mongoQueryExpression,
        IEntityType rootEntityType,
        Func<ParameterExpression, QueryTrackingBehavior, System.Linq.Expressions.ExpressionVisitor> createBindingRemover)
    {
        var bsonDocParameter = Expression.Parameter(typeof(BsonDocument), "bsonDoc");
        var trackingBehavior = QueryCompilationContext.QueryTrackingBehavior;

        var shaperBody = shapedQueryExpression.ShaperExpression;
        var bsonInjector = new BsonDocumentInjectingExpressionVisitor();
        shaperBody = bsonInjector.Visit(shaperBody);
#if EF8 || EF9
        shaperBody = InjectEntityMaterializers(shaperBody);
#else
        shaperBody = InjectStructuralTypeMaterializers(shaperBody);
#endif
        shaperBody = createBindingRemover(bsonDocParameter, trackingBehavior).Visit(shaperBody);

        // Lift all BsonDocument/BsonArray variables to the lambda level so they are
        // accessible across entity boundaries in join projections.
        if (bsonInjector.AllVariables.Count > 0)
        {
            shaperBody = Expression.Block(
                shaperBody.Type,
                bsonInjector.AllVariables,
                shaperBody);
        }

        var shaperLambda = Expression.Lambda(
            shaperBody,
            QueryCompilationContext.QueryContextParameter,
            bsonDocParameter);
        var compiledShaper = shaperLambda.Compile();

        var projectedType = shaperLambda.ReturnType;
        var standAloneStateManager = QueryCompilationContext.QueryTrackingBehavior ==
                                     QueryTrackingBehavior.NoTrackingWithIdentityResolution;

        return Expression.Call(null,
            ExecuteShapedQueryMethodInfo.MakeGenericMethod(rootEntityType.ClrType, projectedType),
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

    private static (MongoQueryContext, MongoExecutableQuery) TranslateQuery<TSource>(
        QueryContext queryContext,
        IReadOnlyEntityType entityType,
        BsonSerializerFactory bsonSerializerFactory,
        MongoQueryExpression queryExpression,
        ResultCardinality resultCardinality,
        Func<MongoEFToLinqTranslatingExpressionVisitor, Expression?, Expression> translate)
    {
        var mongoQueryContext = (MongoQueryContext)queryContext;
        var collection = mongoQueryContext.MongoClient.GetCollection<TSource>(queryExpression.CollectionExpression.CollectionName);

        var transaction = mongoQueryContext.Context.Database.CurrentTransaction as MongoTransaction;
        var queryable = transaction == null ? collection.AsQueryable() : collection.AsQueryable(transaction.Session);
        var source = queryable.As((IBsonSerializer<TSource>)bsonSerializerFactory.GetEntitySerializer(entityType));

        var innerSources = new Dictionary<IEntityType, Expression>();
        if (queryExpression.IsJoinQuery)
        {
            foreach (var (innerEntityType, innerCollectionExpression) in queryExpression.InnerCollections)
            {
                innerSources[innerEntityType] = CreateInnerSource(
                    mongoQueryContext, bsonSerializerFactory, innerEntityType, innerCollectionExpression.CollectionName, transaction);
            }
        }

        var queryTranslator = new MongoEFToLinqTranslatingExpressionVisitor(
            queryContext, source.Expression, bsonSerializerFactory, queryExpression.GetPendingLookups(), innerSources);
        var translatedQuery = translate(queryTranslator, queryExpression.CapturedExpression);

        var executableQuery = new MongoExecutableQuery(
            translatedQuery,
            resultCardinality,
            (IMongoQueryProvider)source.Provider,
            collection.CollectionNamespace,
            new(queryTranslator.AdditionalState));

        return (mongoQueryContext, executableQuery);
    }

    private static Action<MongoQueryContext, MongoExecutableQuery>? GetOnZeroResultsAction(MongoQueryExpression queryExpression)
    {
        if (queryExpression.CapturedExpression is MethodCallExpression methodCallExpression)
        {
            if (methodCallExpression.Method.Name == "Select" && methodCallExpression.Arguments is [MethodCallExpression mce, _])
            {
                methodCallExpression = mce;
            }

            if (methodCallExpression.IsVectorSearch())
            {
                return (qc, eq) => qc.QueryLogger.VectorSearchReturnedZeroResults(
                    (IProperty)eq.AdditionalState[MongoExecutableQuery.VectorQueryProperty],
                    (string)eq.AdditionalState[MongoExecutableQuery.VectorQueryIndexName]);
            }
        }

        return null;
    }

    private static QueryingEnumerable<TResult, TResult> ExecuteProjectedQuery<TSource, TResult>(
        QueryContext queryContext,
        IReadOnlyEntityType entityType,
        BsonSerializerFactory bsonSerializerFactory,
        MongoQueryExpression queryExpression,
        Type contextType,
        bool threadSafetyChecksEnabled,
        ResultCardinality resultCardinality)
    {
        var (mongoQueryContext, executableQuery) = TranslateQuery<TSource>(
            queryContext, entityType, bsonSerializerFactory, queryExpression, resultCardinality,
            (translator, expression) => translator.TranslateProjected(expression));

        return new QueryingEnumerable<TResult, TResult>(
            mongoQueryContext,
            executableQuery,
            (_, e) => e,
            contextType,
            standAloneStateManager: false,
            threadSafetyChecksEnabled,
            GetOnZeroResultsAction(queryExpression));
    }

    private static QueryingEnumerable<BsonDocument, TResult> ExecuteShapedQuery<TSource, TResult>(
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
        var (mongoQueryContext, executableQuery) = TranslateQuery<TSource>(
            queryContext, entityType, bsonSerializerFactory, queryExpression, resultCardinality,
            (translator, expression) => translator.Translate(expression, resultCardinality));

        return new QueryingEnumerable<BsonDocument, TResult>(
            mongoQueryContext,
            executableQuery,
            shaper,
            contextType,
            standAloneStateManager,
            threadSafetyChecksEnabled,
            GetOnZeroResultsAction(queryExpression));
    }

#if !EF8
    // Architecture note: unlike the read path (which hands a MongoExecutableQuery to
    // MongoClientWrapper.Execute in Storage), these bulk executors call DeleteMany/UpdateMany on the
    // driver collection directly and read the ambient session from Database.CurrentTransaction. This is
    // a deliberate, narrow exception to the "Query never touches the driver" boundary: ExecuteDelete/
    // ExecuteUpdate have no cursor/shaper to run, the collection is still obtained via the wrapper
    // (GetCollection), and the transaction lifecycle (begin/commit/rollback) remains entirely in
    // Storage's transaction manager — only the existing session handle is read here. Routing the raw
    // write through a new IMongoClientWrapper method would expand that observable interface for marginal
    // benefit (the session would still be resolved from the query context). Revisit if bulk execution
    // grows retry/cursor concerns that belong in the wrapper.

    // Two-phase bulk needs phase 1 (read) and phase 2 (write) to observe one snapshot, so both run inside a
    // single transaction. If the user already opened one we join it (and never commit it — they own it);
    // otherwise we auto-start one, commit on success, abort on failure. AutoTransactionBehavior.Never means
    // "I manage transactions" — we refuse to auto-start and tell the caller to open one.
    private static int RunInBulkTransaction(QueryContext queryContext, MongoNonQueryExpression nonQuery, Func<int> body)
    {
        var database = queryContext.Context.Database;
        if (database.CurrentTransaction != null)
        {
            return body();
        }

        if (database.AutoTransactionBehavior == AutoTransactionBehavior.Never)
        {
            throw NoAutoTransactionError();
        }

        // BeginTransaction is inside the try: on a non-transactional deployment the session's StartTransaction
        // (reached synchronously via MongoTransaction.Start) throws here, and we want that mapped to the
        // actionable StandaloneTransactionError like any other transaction-unsupported failure.
        IDbContextTransaction? transaction = null;
        try
        {
            transaction = database.BeginTransaction();
            var result = body();
            transaction.Commit();
            return result;
        }
        catch (Exception exception)
        {
            if (transaction != null)
            {
                SafeRollback(transaction);
            }

            if (IsTransactionsUnsupported(exception))
            {
                throw StandaloneTransactionError(exception);
            }

            throw;
        }
        finally
        {
            transaction?.Dispose();
        }
    }

    private static async Task<int> RunInBulkTransactionAsync(
        QueryContext queryContext, MongoNonQueryExpression nonQuery, Func<Task<int>> body)
    {
        var database = queryContext.Context.Database;
        if (database.CurrentTransaction != null)
        {
            return await body().ConfigureAwait(false);
        }

        if (database.AutoTransactionBehavior == AutoTransactionBehavior.Never)
        {
            throw NoAutoTransactionError();
        }

        var cancellationToken = queryContext.CancellationToken;
        // BeginTransactionAsync is inside the try for the same reason as the sync path: a non-transactional
        // deployment throws during transaction startup, and that must map to StandaloneTransactionError.
        IDbContextTransaction? transaction = null;
        try
        {
            transaction = await database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var result = await body().ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (Exception exception)
        {
            if (transaction != null)
            {
                await SafeRollbackAsync(transaction, cancellationToken).ConfigureAwait(false);
            }

            if (IsTransactionsUnsupported(exception))
            {
                throw StandaloneTransactionError(exception);
            }

            throw;
        }
        finally
        {
            if (transaction != null)
            {
                await transaction.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static void SafeRollback(IDbContextTransaction transaction)
    {
        try { transaction.Rollback(); }
        catch { /* a failed/standalone transaction may not be rollback-able; the original error wins */ }
    }

    private static async Task SafeRollbackAsync(IDbContextTransaction transaction, CancellationToken cancellationToken)
    {
        try { await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false); }
        catch { /* see SafeRollback */ }
    }

    private static InvalidOperationException NoAutoTransactionError()
        => new(
            "This bulk delete or update uses ordering, paging, or 'Distinct', which the MongoDB provider executes "
            + "as a two-phase operation requiring a transaction. The context's AutoTransactionBehavior is 'Never', "
            + "so open an explicit transaction (Database.BeginTransaction) around the call.");

    private static InvalidOperationException StandaloneTransactionError(Exception inner)
        => new(
            "This bulk delete or update uses ordering, paging, or 'Distinct', which the MongoDB provider executes "
            + "as a two-phase operation requiring a transaction. The current MongoDB deployment does not support "
            + "multi-document transactions (a replica set or sharded cluster is required).", inner);

    // Multi-document transactions are rejected when the deployment doesn't support them, and the failure can
    // surface in three shapes: (1) the provider's own transaction startup (MongoTransaction.Start) intercepts the
    // driver's "Standalone servers do not support transactions." and rethrows a NotSupportedException whose message
    // contains "does not support transactions"; (2) a raw driver MongoCommandException (code 20 / IllegalOperation);
    // (3) a "Transaction numbers are only allowed on a replica set member or mongos" message. Match all three so
    // two-phase bulk on a non-transactional deployment is wrapped in the actionable StandaloneTransactionError.
    // Match conservatively so unrelated failures propagate untouched.
    private static bool IsTransactionsUnsupported(Exception exception)
        => exception is MongoCommandException { Code: 20 }
           || (exception is MongoException && exception.Message.Contains("Transaction numbers are only allowed", StringComparison.Ordinal))
           || (exception is NotSupportedException && exception.Message.Contains("does not support transactions", StringComparison.Ordinal));

    private static int ExecuteTwoPhaseDelete<TSource>(
        QueryContext queryContext, IReadOnlyEntityType entityType, BsonSerializerFactory bsonSerializerFactory,
        MongoNonQueryExpression nonQuery)
        => RunInBulkTransaction(queryContext, nonQuery, () =>
        {
            var ids = SelectTargetIds<TSource>(queryContext, entityType, bsonSerializerFactory, nonQuery);
            return ids.Count == 0 ? 0 : DeleteByIds(queryContext, nonQuery, ids);
        });

    private static Task<int> ExecuteTwoPhaseDeleteAsync<TSource>(
        QueryContext queryContext, IReadOnlyEntityType entityType, BsonSerializerFactory bsonSerializerFactory,
        MongoNonQueryExpression nonQuery)
        => RunInBulkTransactionAsync(queryContext, nonQuery, async () =>
        {
            var ids = await SelectTargetIdsAsync<TSource>(
                queryContext, entityType, bsonSerializerFactory, nonQuery).ConfigureAwait(false);
            return ids.Count == 0
                ? 0
                : await DeleteByIdsAsync(queryContext, nonQuery, ids).ConfigureAwait(false);
        });

    private static int ExecuteTwoPhaseUpdate<TSource>(
        QueryContext queryContext, IReadOnlyEntityType entityType, BsonSerializerFactory bsonSerializerFactory,
        MongoNonQueryExpression nonQuery)
        => RunInBulkTransaction(queryContext, nonQuery, () =>
        {
            var ids = SelectTargetIds<TSource>(queryContext, entityType, bsonSerializerFactory, nonQuery);
            if (ids.Count == 0)
            {
                return 0;
            }

            var update = TranslateBulkOrThrow(nonQuery,
                () => BuildUpdate<TSource>(queryContext, entityType, bsonSerializerFactory, nonQuery));
            return UpdateByIds(queryContext, nonQuery, ids, update);
        });

    private static Task<int> ExecuteTwoPhaseUpdateAsync<TSource>(
        QueryContext queryContext, IReadOnlyEntityType entityType, BsonSerializerFactory bsonSerializerFactory,
        MongoNonQueryExpression nonQuery)
        => RunInBulkTransactionAsync(queryContext, nonQuery, async () =>
        {
            var ids = await SelectTargetIdsAsync<TSource>(
                queryContext, entityType, bsonSerializerFactory, nonQuery).ConfigureAwait(false);
            if (ids.Count == 0)
            {
                return 0;
            }

            var update = TranslateBulkOrThrow(nonQuery,
                () => BuildUpdate<TSource>(queryContext, entityType, bsonSerializerFactory, nonQuery));
            return await UpdateByIdsAsync(queryContext, nonQuery, ids, update).ConfigureAwait(false);
        });

    private static int UpdateByIds(
        QueryContext queryContext, MongoNonQueryExpression nonQuery, List<BsonValue> ids,
        UpdateDefinition<BsonDocument> update)
    {
        var (collection, session) = GetBulkCollectionAndSession(queryContext, nonQuery);
        var filter = Builders<BsonDocument>.Filter.In("_id", ids);

        var updateLogger = GetUpdateLogger(queryContext);
        var stopwatch = Stopwatch.StartNew();
        updateLogger.ExecutingBulkUpdate(stopwatch.Elapsed, collection.CollectionNamespace, ids.Count);

        var result = session == null
            ? collection.UpdateMany(filter, update)
            : collection.UpdateMany(session, filter, update);

        updateLogger.ExecutedBulkUpdate(stopwatch.Elapsed, collection.CollectionNamespace, result.ModifiedCount);
        return checked((int)result.MatchedCount);
    }

    private static async Task<int> UpdateByIdsAsync(
        QueryContext queryContext, MongoNonQueryExpression nonQuery, List<BsonValue> ids,
        UpdateDefinition<BsonDocument> update)
    {
        var (collection, session) = GetBulkCollectionAndSession(queryContext, nonQuery);
        var filter = Builders<BsonDocument>.Filter.In("_id", ids);

        var updateLogger = GetUpdateLogger(queryContext);
        var stopwatch = Stopwatch.StartNew();
        updateLogger.ExecutingBulkUpdate(stopwatch.Elapsed, collection.CollectionNamespace, ids.Count);

        var cancellationToken = queryContext.CancellationToken;
        var result = session == null
            ? await collection.UpdateManyAsync(filter, update, options: null, cancellationToken).ConfigureAwait(false)
            : await collection.UpdateManyAsync(session, filter, update, options: null, cancellationToken)
                .ConfigureAwait(false);

        updateLogger.ExecutedBulkUpdate(stopwatch.Elapsed, collection.CollectionNamespace, result.ModifiedCount);
        return checked((int)result.MatchedCount);
    }

    private static int DeleteByIds(
        QueryContext queryContext, MongoNonQueryExpression nonQuery, List<BsonValue> ids)
    {
        var (collection, session) = GetBulkCollectionAndSession(queryContext, nonQuery);
        var filter = Builders<BsonDocument>.Filter.In("_id", ids);

        var updateLogger = GetUpdateLogger(queryContext);
        var stopwatch = Stopwatch.StartNew();
        updateLogger.ExecutingBulkDelete(stopwatch.Elapsed, collection.CollectionNamespace, ids.Count);

        var result = session == null ? collection.DeleteMany(filter) : collection.DeleteMany(session, filter);

        updateLogger.ExecutedBulkDelete(stopwatch.Elapsed, collection.CollectionNamespace, result.DeletedCount);
        return checked((int)result.DeletedCount);
    }

    private static async Task<int> DeleteByIdsAsync(
        QueryContext queryContext, MongoNonQueryExpression nonQuery, List<BsonValue> ids)
    {
        var (collection, session) = GetBulkCollectionAndSession(queryContext, nonQuery);
        var filter = Builders<BsonDocument>.Filter.In("_id", ids);

        var updateLogger = GetUpdateLogger(queryContext);
        var stopwatch = Stopwatch.StartNew();
        updateLogger.ExecutingBulkDelete(stopwatch.Elapsed, collection.CollectionNamespace, ids.Count);

        var cancellationToken = queryContext.CancellationToken;
        var result = session == null
            ? await collection.DeleteManyAsync(filter, cancellationToken).ConfigureAwait(false)
            : await collection.DeleteManyAsync(session, filter, options: null, cancellationToken).ConfigureAwait(false);

        updateLogger.ExecutedBulkDelete(stopwatch.Elapsed, collection.CollectionNamespace, result.DeletedCount);
        return checked((int)result.DeletedCount);
    }

    private static (IMongoCollection<BsonDocument> collection, IClientSessionHandle? session) GetBulkCollectionAndSession(
        QueryContext queryContext, MongoNonQueryExpression nonQuery)
    {
        var mongoQueryContext = (MongoQueryContext)queryContext;
        var collection =
            mongoQueryContext.MongoClient.GetCollection<BsonDocument>(nonQuery.SourceQuery.CollectionExpression.CollectionName);
        var session = (mongoQueryContext.Context.Database.CurrentTransaction as MongoTransaction)?.Session;
        return (collection, session);
    }

    private static int ExecuteDelete<TSource>(
        QueryContext queryContext,
        IReadOnlyEntityType entityType,
        BsonSerializerFactory bsonSerializerFactory,
        MongoNonQueryExpression nonQuery)
    {
        var (collection, session, filter) = PrepareBulk<TSource>(queryContext, entityType, bsonSerializerFactory, nonQuery);

        var updateLogger = GetUpdateLogger(queryContext);
        var stopwatch = Stopwatch.StartNew();
        updateLogger.ExecutingBulkDelete(stopwatch.Elapsed, collection.CollectionNamespace);

        var result = session == null
            ? collection.DeleteMany(filter)
            : collection.DeleteMany(session, filter);

        updateLogger.ExecutedBulkDelete(stopwatch.Elapsed, collection.CollectionNamespace, result.DeletedCount);

        // DeletedCount is long; EF's ExecuteDelete contract returns int — overflow past int.MaxValue throws by design.
        return checked((int)result.DeletedCount);
    }

    private static async Task<int> ExecuteDeleteAsync<TSource>(
        QueryContext queryContext,
        IReadOnlyEntityType entityType,
        BsonSerializerFactory bsonSerializerFactory,
        MongoNonQueryExpression nonQuery)
    {
        var (collection, session, filter) = PrepareBulk<TSource>(queryContext, entityType, bsonSerializerFactory, nonQuery);

        var updateLogger = GetUpdateLogger(queryContext);
        var stopwatch = Stopwatch.StartNew();
        updateLogger.ExecutingBulkDelete(stopwatch.Elapsed, collection.CollectionNamespace);

        var cancellationToken = queryContext.CancellationToken;
        var result = session == null
            ? await collection.DeleteManyAsync(filter, cancellationToken).ConfigureAwait(false)
            : await collection.DeleteManyAsync(session, filter, options: null, cancellationToken).ConfigureAwait(false);

        updateLogger.ExecutedBulkDelete(stopwatch.Elapsed, collection.CollectionNamespace, result.DeletedCount);

        // DeletedCount is long; EF's ExecuteDelete contract returns int — overflow past int.MaxValue throws by design.
        return checked((int)result.DeletedCount);
    }

    private static int ExecuteUpdate<TSource>(
        QueryContext queryContext,
        IReadOnlyEntityType entityType,
        BsonSerializerFactory bsonSerializerFactory,
        MongoNonQueryExpression nonQuery)
    {
        var (collection, session, filter) = PrepareBulk<TSource>(queryContext, entityType, bsonSerializerFactory, nonQuery);
        var update = TranslateBulkOrThrow(nonQuery, () => BuildUpdate<TSource>(queryContext, entityType, bsonSerializerFactory, nonQuery));

        var updateLogger = GetUpdateLogger(queryContext);
        var stopwatch = Stopwatch.StartNew();
        updateLogger.ExecutingBulkUpdate(stopwatch.Elapsed, collection.CollectionNamespace);

        var result = session == null
            ? collection.UpdateMany(filter, update)
            : collection.UpdateMany(session, filter, update);

        updateLogger.ExecutedBulkUpdate(stopwatch.Elapsed, collection.CollectionNamespace, result.ModifiedCount);

        // EF's ExecuteUpdate contract returns the number of rows matched by the predicate (the same as relational
        // providers, which count a row even when SET writes its existing value), not just those whose stored
        // values actually changed. MongoDB's UpdateResult exposes both; MatchedCount is the affected-row count.
        // (The ExecutedBulkUpdate event above still reports ModifiedCount — the genuinely-modified subset.)
        // MatchedCount is long; overflow past int.MaxValue throws by design.
        return checked((int)result.MatchedCount);
    }

    private static async Task<int> ExecuteUpdateAsync<TSource>(
        QueryContext queryContext,
        IReadOnlyEntityType entityType,
        BsonSerializerFactory bsonSerializerFactory,
        MongoNonQueryExpression nonQuery)
    {
        var (collection, session, filter) = PrepareBulk<TSource>(queryContext, entityType, bsonSerializerFactory, nonQuery);
        var update = TranslateBulkOrThrow(nonQuery, () => BuildUpdate<TSource>(queryContext, entityType, bsonSerializerFactory, nonQuery));

        var updateLogger = GetUpdateLogger(queryContext);
        var stopwatch = Stopwatch.StartNew();
        updateLogger.ExecutingBulkUpdate(stopwatch.Elapsed, collection.CollectionNamespace);

        var cancellationToken = queryContext.CancellationToken;
        var result = session == null
            ? await collection.UpdateManyAsync(filter, update, options: null, cancellationToken).ConfigureAwait(false)
            : await collection.UpdateManyAsync(session, filter, update, options: null, cancellationToken).ConfigureAwait(false);

        updateLogger.ExecutedBulkUpdate(stopwatch.Elapsed, collection.CollectionNamespace, result.ModifiedCount);

        // EF's ExecuteUpdate contract returns the number of rows matched by the predicate (the same as relational
        // providers, which count a row even when SET writes its existing value), not just those whose stored
        // values actually changed. MongoDB's UpdateResult exposes both; MatchedCount is the affected-row count.
        // (The ExecutedBulkUpdate event above still reports ModifiedCount — the genuinely-modified subset.)
        // MatchedCount is long; overflow past int.MaxValue throws by design.
        return checked((int)result.MatchedCount);
    }

    // Server-side ExecuteDelete/ExecuteUpdate bypass SaveChanges, so the bulk-write logging in MongoDatabaseWrapper
    // never fires for them. Resolve the Update-category logger from the context's service provider to emit the
    // dedicated bulk delete/update events. IDiagnosticsLogger<> is registered as an open generic in EF Core's DI.
    private static IDiagnosticsLogger<DbLoggerCategory.Update> GetUpdateLogger(QueryContext queryContext)
        => queryContext.Context.GetService<IDiagnosticsLogger<DbLoggerCategory.Update>>();

    // The bulk filter/update is translated from user expressions at execution time. When a predicate or
    // setter shape that slipped past compile-time validation can't be translated (e.g. a GroupBy subquery
    // in the Where), the driver/LINQ layer throws a raw ExpressionNotSupportedException / ArgumentException.
    // Convert those into EF Core's canonical non-query translation failure so callers see a consistent
    // "could not be translated" error. InvalidOperationException is left as-is — it already carries either
    // that canonical message or the provider's cross-DbSet rejection.
    private static T TranslateBulkOrThrow<T>(MongoNonQueryExpression nonQuery, Func<T> translate)
    {
        try
        {
            return translate();
        }
        catch (Exception exception) when (exception is not InvalidOperationException)
        {
            throw new InvalidOperationException(
                CoreStrings.NonQueryTranslationFailedWithDetails(
                    nonQuery.SourceQuery.CapturedExpression?.Print(),
                    exception.Message),
                exception);
        }
    }

    private static (IMongoCollection<BsonDocument> collection, IClientSessionHandle? session, FilterDefinition<BsonDocument> filter) PrepareBulk<TSource>(
        QueryContext queryContext,
        IReadOnlyEntityType entityType,
        BsonSerializerFactory bsonSerializerFactory,
        MongoNonQueryExpression nonQuery)
    {
        var mongoQueryContext = (MongoQueryContext)queryContext;
        var collection =
            mongoQueryContext.MongoClient.GetCollection<BsonDocument>(nonQuery.SourceQuery.CollectionExpression.CollectionName);
        var session = (mongoQueryContext.Context.Database.CurrentTransaction as MongoTransaction)?.Session;
        var filter = TranslateBulkOrThrow(nonQuery, () => BuildFilter<TSource>(queryContext, entityType, bsonSerializerFactory, nonQuery));

        return (collection, session, filter);
    }

    // Validates that the entity has a primary key mapped to _id (always true for a MongoDB root entity).
    // Keyless entities cannot use two-phase delete and fall back to the canonical non-query failure.
    private static void EnsureBulkKeyOrThrow(IReadOnlyEntityType entityType, MongoNonQueryExpression nonQuery)
    {
        if (entityType.FindPrimaryKey() == null)
        {
            throw new InvalidOperationException(
                CoreStrings.NonQueryTranslationFailedWithDetails(
                    nonQuery.SourceQuery.CapturedExpression?.Print(),
                    "the entity must have a primary key to use ordering, paging, or Distinct in a bulk delete or update."));
        }
    }

    // Phase 1: run the bulk source (Where + OrderBy/Skip/Take/Distinct) as a read that yields the raw stored
    // BsonDocuments — reusing the read-path translation — and collect each document's _id. This works
    // uniformly for scalar and composite primary keys (MongoDB stores a composite key as the _id sub-document)
    // and never goes through EntitySerializer's (unimplemented) round-trip serialization.
    private static List<BsonValue> SelectTargetIds<TSource>(
        QueryContext queryContext,
        IReadOnlyEntityType entityType,
        BsonSerializerFactory bsonSerializerFactory,
        MongoNonQueryExpression nonQuery)
    {
        var documents = BuildIdDocumentQuery<TSource>(queryContext, entityType, bsonSerializerFactory, nonQuery);

        var ids = new List<BsonValue>();
        foreach (var document in documents)
        {
            ids.Add(document["_id"]);
        }

        return ids;
    }

    private static async Task<List<BsonValue>> SelectTargetIdsAsync<TSource>(
        QueryContext queryContext,
        IReadOnlyEntityType entityType,
        BsonSerializerFactory bsonSerializerFactory,
        MongoNonQueryExpression nonQuery)
    {
        var documents = BuildIdDocumentQuery<TSource>(queryContext, entityType, bsonSerializerFactory, nonQuery);

        var materialized = await IAsyncCursorSourceExtensions
            .ToListAsync((IAsyncCursorSource<BsonDocument>)documents, queryContext.CancellationToken).ConfigureAwait(false);

        var ids = new List<BsonValue>(materialized.Count);
        foreach (var document in materialized)
        {
            ids.Add(document["_id"]);
        }

        return ids;
    }

    // Builds a driver query that yields the raw stored BsonDocuments for the bulk source, by reusing the
    // read path's TranslateQuery (which applies Where/OrderBy/Skip/Take/Distinct via the driver and reads the
    // ambient transaction session) and asking the driver provider for BsonDocument results.
    // Note: this fetches whole documents and keeps only _id; a future optimization could push a
    // { _id: 1 } projection server-side to reduce transfer for large target sets.
    private static IQueryable<BsonDocument> BuildIdDocumentQuery<TSource>(
        QueryContext queryContext,
        IReadOnlyEntityType entityType,
        BsonSerializerFactory bsonSerializerFactory,
        MongoNonQueryExpression nonQuery)
    {
        var (_, executableQuery) = TranslateQuery<TSource>(
            queryContext, entityType, bsonSerializerFactory, nonQuery.SourceQuery, ResultCardinality.Enumerable,
            (translator, expression) =>
                translator.Translate(MongoNonQueryExpression.UnwrapBulkOperator(expression)!, ResultCardinality.Enumerable));

        return executableQuery.Provider.CreateQuery<BsonDocument>(executableQuery.Query);
    }

    /// <summary>
    /// Builds the server-side <see cref="FilterDefinition{BsonDocument}"/> that scopes a bulk operation by combining the
    /// predicates of every <c>Where</c> in the captured chain. Each predicate body is lowered through the EF→driver-LINQ
    /// visitor (rewriting <c>EF.Property</c> to <c>Mql.Field</c>) and rebound to a single shared parameter, then rendered
    /// with the EF entity serializer so element names honor the EF model. An empty chain matches every document.
    /// </summary>
    private static FilterDefinition<BsonDocument> BuildFilter<TSource>(
        QueryContext queryContext,
        IReadOnlyEntityType entityType,
        BsonSerializerFactory bsonSerializerFactory,
        MongoNonQueryExpression nonQuery)
    {
        var sharedParameter = Expression.Parameter(typeof(TSource), "e");
        var translator = new MongoEFToLinqTranslatingExpressionVisitor(
            queryContext, Expression.Constant(null, typeof(IQueryable<TSource>)), bsonSerializerFactory);

        Expression? combinedBody = null;
        var expression = MongoNonQueryExpression.UnwrapBulkOperator(nonQuery.SourceQuery.CapturedExpression);
        // Invariant: ValidateBulkSource (in the method-translating visitor) has already rejected any operator other than
        // Queryable.Where, so this walk is exhaustive — a non-Where node here would be silently dropped.
        while (expression is MethodCallExpression { Method: { DeclaringType: var declaringType, Name: nameof(Queryable.Where) } }
                   whereCall
               && declaringType == typeof(Queryable))
        {
            var predicate = whereCall.Arguments[1].UnwrapLambdaFromQuote();
            var translatedBody = translator.Visit(predicate.Body)!;
            translatedBody = ReplacingExpressionVisitor.Replace(predicate.Parameters[0], sharedParameter, translatedBody);

            combinedBody = combinedBody == null ? translatedBody : Expression.AndAlso(combinedBody, translatedBody);

            expression = whereCall.Arguments[0];
        }

        if (combinedBody == null)
        {
            return FilterDefinition<BsonDocument>.Empty;
        }

        var predicateLambda = Expression.Lambda<Func<TSource, bool>>(combinedBody, sharedParameter);
        var efSerializer = (IBsonSerializer<TSource>)bsonSerializerFactory.GetEntitySerializer(entityType);
        var rendered = new ExpressionFilterDefinition<TSource>(predicateLambda)
            .Render(new RenderArgs<TSource>(efSerializer, BsonSerializer.SerializerRegistry));

        return rendered;
    }

    /// <summary>
    /// Builds the server-side update for a bulk update. When no setter is self-referencing the update is a simple
    /// <c>$set</c> document of serialized literal values. When any setter references the entity being updated the update
    /// becomes an aggregation pipeline containing a single <c>$set</c> stage; self-referencing setters contribute a
    /// rendered aggregation expression and constant setters contribute their serialized literal (the two can mix freely).
    /// </summary>
    private static UpdateDefinition<BsonDocument> BuildUpdate<TSource>(
        QueryContext queryContext,
        IReadOnlyEntityType entityType,
        BsonSerializerFactory bsonSerializerFactory,
        MongoNonQueryExpression nonQuery)
    {
        var setters = nonQuery.Setters;

        if (!setters.Any(s => s.IsSelfReferencing))
        {
            var setDoc = new BsonDocument();
            foreach (var setter in setters)
            {
                setDoc[setter.Property.GetElementName()] = SerializeConstant(queryContext, setter);
            }

            return new BsonDocumentUpdateDefinition<BsonDocument>(new BsonDocument("$set", setDoc));
        }

        // Pipeline-form update: required as soon as any setter references the document being updated.
        var efSerializer = (IBsonSerializer<TSource>)bsonSerializerFactory.GetEntitySerializer(entityType);
        var entityParameter = Expression.Parameter(typeof(TSource), "e");
        var setStageDoc = new BsonDocument();

        foreach (var setter in setters)
        {
            if (setter.IsSelfReferencing)
            {
                setStageDoc[setter.Property.GetElementName()] = RenderSelfReferencingValue<TSource>(
                    queryContext, bsonSerializerFactory, entityParameter, efSerializer, setter);
            }
            else
            {
                setStageDoc[setter.Property.GetElementName()] = SerializeConstant(queryContext, setter);
            }
        }

        var setStage = new BsonDocument("$set", setStageDoc);
        var pipeline = PipelineDefinition<BsonDocument, BsonDocument>.Create(new[] { setStage });
        return Builders<BsonDocument>.Update.Pipeline(pipeline);
    }

    /// <summary>
    /// Evaluates a constant/parameter setter value to a CLR constant and serializes it to a <see cref="BsonValue"/>
    /// using the same property serializer the write pipeline uses, so enum / Guid / representation handling matches
    /// inserts and SaveChanges updates.
    /// </summary>
    private static BsonValue SerializeConstant(QueryContext queryContext, MongoNonQueryExpression.Setter setter)
    {
        var value = EvaluateToConstant(queryContext, setter.ValueExpression);
        var serializationInfo = BsonSerializerFactory.GetPropertySerializationInfo(setter.Property);
        return serializationInfo.SerializeValue(value);
    }

    /// <summary>
    /// Renders a self-referencing setter value (e.g. <c>o =&gt; o.Quantity + 1</c>) to an aggregation-expression
    /// <see cref="BsonValue"/>. The value body is lowered through the EF→driver-LINQ visitor, rebound to a single shared
    /// parameter, and rendered with the EF entity serializer so element names honor the EF model.
    /// </summary>
    private static BsonValue RenderSelfReferencingValue<TSource>(
        QueryContext queryContext,
        BsonSerializerFactory bsonSerializerFactory,
        ParameterExpression entityParameter,
        IBsonSerializer<TSource> efSerializer,
        MongoNonQueryExpression.Setter setter)
    {
        var translator = new MongoEFToLinqTranslatingExpressionVisitor(
            queryContext, Expression.Constant(null, typeof(IQueryable<TSource>)), bsonSerializerFactory);
        var translatedBody = translator.Visit(setter.ValueExpression)!;

        // The translated body still references the original setter parameter(s); rebind to the shared parameter.
        translatedBody = new ParameterRebindingExpressionVisitor(entityParameter).Visit(translatedBody);

        var resultType = setter.Property.ClrType;
        var renderer = RenderAggregateExpressionMethodInfo.MakeGenericMethod(typeof(TSource), resultType);
        return (BsonValue)renderer.Invoke(null, [entityParameter, translatedBody, efSerializer])!;
    }

    private static BsonValue RenderAggregateExpression<TSource, TResult>(
        ParameterExpression entityParameter,
        Expression body,
        IBsonSerializer<TSource> efSerializer)
    {
        var lambda = Expression.Lambda<Func<TSource, TResult>>(
            body.Type == typeof(TResult) ? body : Expression.Convert(body, typeof(TResult)),
            entityParameter);
        return new ExpressionAggregateExpressionDefinition<TSource, TResult>(lambda)
            .Render(new RenderArgs<TSource>(efSerializer, BsonSerializer.SerializerRegistry));
    }

    /// <summary>
    /// Evaluates a setter value expression (constant, captured closure, or query parameter) to a CLR value.
    /// Unlike <c>MongoEFToLinqTranslatingExpressionVisitor.TryEvaluateToConstant</c>, this method intentionally
    /// lets compile-and-evaluate failures propagate as exceptions — throwing on an un-evaluatable setter value
    /// is preferable to silently writing null.
    /// </summary>
    private static object? EvaluateToConstant(QueryContext queryContext, Expression expression)
    {
        while (expression is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert)
        {
            expression = convert.Operand;
        }

        if (expression is ConstantExpression constant)
        {
            return constant.Value;
        }

        if (expression is MemberExpression { Expression: ConstantExpression closureConstant } member)
        {
            return member.Member switch
            {
                FieldInfo field => field.GetValue(closureConstant.Value),
                PropertyInfo prop => prop.GetValue(closureConstant.Value),
                _ => CompileAndEvaluate(expression)
            };
        }

#if EF8 || EF9
        if (expression is ParameterExpression param
            && param.Name?.StartsWith(QueryCompilationContext.QueryParameterPrefix, StringComparison.Ordinal) == true
            && queryContext.ParameterValues.TryGetValue(param.Name, out var value))
        {
            return value;
        }
#else
        if (expression is Microsoft.EntityFrameworkCore.Query.QueryParameterExpression queryParam)
        {
            return queryContext.Parameters[queryParam.Name];
        }
#endif

        return CompileAndEvaluate(expression);
    }

    private static object? CompileAndEvaluate(Expression expression)
        => Expression.Lambda<Func<object?>>(Expression.Convert(expression, typeof(object))).Compile()();

    /// <summary>
    /// Rebinds every <see cref="ParameterExpression"/> in a translated self-referencing setter body to a single shared
    /// parameter, so the assembled value lambda has exactly one parameter as required by the renderer.
    /// </summary>
    private sealed class ParameterRebindingExpressionVisitor(ParameterExpression target)
        : System.Linq.Expressions.ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node)
            // Rebind by type rather than by identity: translation through MongoEFToLinqTranslatingExpressionVisitor
            // discards the original parameter identity, and the value body has exactly one TSource-typed parameter
            // (the setter's own), so type-based rebinding is safe for the supported single-setter value shapes.
            => node.Type == target.Type ? target : base.VisitParameter(node);
    }

    private static readonly MethodInfo ExecuteDeleteMethodInfo =
        typeof(MongoShapedQueryCompilingExpressionVisitor)
            .GetTypeInfo()
            .DeclaredMethods
            .Single(m => m.Name == nameof(ExecuteDelete));

    private static readonly MethodInfo ExecuteDeleteAsyncMethodInfo =
        typeof(MongoShapedQueryCompilingExpressionVisitor)
            .GetTypeInfo()
            .DeclaredMethods
            .Single(m => m.Name == nameof(ExecuteDeleteAsync));

    private static readonly MethodInfo ExecuteUpdateMethodInfo =
        typeof(MongoShapedQueryCompilingExpressionVisitor)
            .GetTypeInfo()
            .DeclaredMethods
            .Single(m => m.Name == nameof(ExecuteUpdate));

    private static readonly MethodInfo ExecuteUpdateAsyncMethodInfo =
        typeof(MongoShapedQueryCompilingExpressionVisitor)
            .GetTypeInfo()
            .DeclaredMethods
            .Single(m => m.Name == nameof(ExecuteUpdateAsync));

    private static readonly MethodInfo ExecuteTwoPhaseDeleteMethodInfo =
        typeof(MongoShapedQueryCompilingExpressionVisitor)
            .GetTypeInfo()
            .DeclaredMethods
            .Single(m => m.Name == nameof(ExecuteTwoPhaseDelete));

    private static readonly MethodInfo ExecuteTwoPhaseDeleteAsyncMethodInfo =
        typeof(MongoShapedQueryCompilingExpressionVisitor)
            .GetTypeInfo()
            .DeclaredMethods
            .Single(m => m.Name == nameof(ExecuteTwoPhaseDeleteAsync));

    private static readonly MethodInfo ExecuteTwoPhaseUpdateMethodInfo =
        typeof(MongoShapedQueryCompilingExpressionVisitor)
            .GetTypeInfo()
            .DeclaredMethods
            .Single(m => m.Name == nameof(ExecuteTwoPhaseUpdate));

    private static readonly MethodInfo ExecuteTwoPhaseUpdateAsyncMethodInfo =
        typeof(MongoShapedQueryCompilingExpressionVisitor)
            .GetTypeInfo()
            .DeclaredMethods
            .Single(m => m.Name == nameof(ExecuteTwoPhaseUpdateAsync));

    private static readonly MethodInfo RenderAggregateExpressionMethodInfo =
        typeof(MongoShapedQueryCompilingExpressionVisitor)
            .GetTypeInfo()
            .DeclaredMethods
            .Single(m => m.Name == nameof(RenderAggregateExpression));
#endif

    private static readonly MethodInfo ExecuteShapedQueryMethodInfo =
        typeof(MongoShapedQueryCompilingExpressionVisitor)
            .GetTypeInfo()
            .DeclaredMethods
            .Single(m => m.Name == nameof(ExecuteShapedQuery));

    private static readonly MethodInfo ExecuteProjectedQueryMethodInfo =
        typeof(MongoShapedQueryCompilingExpressionVisitor)
            .GetTypeInfo()
            .DeclaredMethods
            .Single(m => m.Name == nameof(ExecuteProjectedQuery));

    private static readonly MethodInfo CreateInnerSourceMethodInfo =
        typeof(MongoShapedQueryCompilingExpressionVisitor)
            .GetTypeInfo()
            .GetDeclaredMethod(nameof(CreateInnerSourceTyped))!;

    private static Expression CreateInnerSource(
        MongoQueryContext mongoQueryContext,
        BsonSerializerFactory bsonSerializerFactory,
        IReadOnlyEntityType innerEntityType,
        string collectionName,
        MongoTransaction? transaction)
    {
        return (Expression)CreateInnerSourceMethodInfo
            .MakeGenericMethod(innerEntityType.ClrType)
            .Invoke(null, [mongoQueryContext, bsonSerializerFactory, innerEntityType, collectionName, transaction])!;
    }

    private static Expression CreateInnerSourceTyped<TInner>(
        MongoQueryContext mongoQueryContext,
        BsonSerializerFactory bsonSerializerFactory,
        IReadOnlyEntityType innerEntityType,
        string collectionName,
        MongoTransaction? transaction)
    {
        // The driver's Join/GroupJoin pipeline translator requires the inner operand to be a bare
        // IMongoQueryable backed by a collection (a ConstantExpression). It rejects an operand wrapped
        // in .As(serializer) (a MethodCallExpression), so we cannot use .As(...) here as we do for the
        // outer source. Instead we wrap the collection so its DocumentSerializer returns EF's entity
        // serializer; the driver derives the inner pipeline-input serializer from collection.DocumentSerializer,
        // which keeps EF's element-name / discriminator / BsonRepresentation mappings on the inner side.
        var innerCollection = new SerializerOverrideCollection<TInner>(
            mongoQueryContext.MongoClient.GetCollection<TInner>(collectionName),
            (IBsonSerializer<TInner>)bsonSerializerFactory.GetEntitySerializer(innerEntityType));
        var innerQueryable = transaction == null
            ? innerCollection.AsQueryable()
            : innerCollection.AsQueryable(transaction.Session);
        return innerQueryable.Expression;
    }
}
