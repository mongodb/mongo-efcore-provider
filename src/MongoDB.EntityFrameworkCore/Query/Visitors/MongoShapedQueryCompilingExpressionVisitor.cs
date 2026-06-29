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
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
#endif
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using MongoDB.Driver.Linq;
using MongoDB.EntityFrameworkCore.Diagnostics;
using MongoDB.EntityFrameworkCore.Infrastructure;
using MongoDB.EntityFrameworkCore.Query.Expressions;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;
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
            // Two-phase needs the entity's _id key to project phase-1 targets and act by { _id: $in }.
            EnsureBulkKeyOrThrow(entityType, nonQueryExpression);
        }

        // The plan closes over the entity type / serializer factory / non-query expression (all compile-time
        // constants) and is embedded into the compiled query. Its delegates perform the runtime translation;
        // MongoBulkOperationExecutor (Storage) runs the writes, transaction, and diagnostics.
        var plan = (MongoBulkPlan)CreateBulkPlanMethodInfo
            .MakeGenericMethod(entityType.ClrType)
            .Invoke(null, [entityType, _bsonSerializerFactory, nonQueryExpression])!;

        var executor = QueryCompilationContext.IsAsync
            ? MongoBulkExecuteAsyncMethodInfo
            : MongoBulkExecuteMethodInfo;

        return Expression.Call(
            null,
            executor,
            QueryCompilationContext.QueryContextParameter,
            Expression.Constant(plan));
    }

    // Builds the compile-time plan for a bulk operation. Generic over TSource so the deferred translation
    // delegates can close over the correctly-typed serializer/queryable; invoked once via reflection from
    // VisitNonQuery with entityType.ClrType. The translation helpers stay here in the query pipeline; only
    // the resulting FilterDefinition / UpdateDefinition / IQueryable<BsonDocument> cross to the executor.
    private static MongoBulkPlan CreateBulkPlan<TSource>(
        IReadOnlyEntityType entityType,
        BsonSerializerFactory bsonSerializerFactory,
        MongoNonQueryExpression nonQuery)
    {
        var isUpdate = nonQuery.Kind == MongoNonQueryExpression.OperationKind.Update;
        var isTwoPhase = nonQuery.Strategy == MongoNonQueryExpression.BulkStrategy.TwoPhase;

        return new MongoBulkPlan
        {
            Kind = isUpdate ? MongoBulkOperationKind.Update : MongoBulkOperationKind.Delete,
            Strategy = isTwoPhase ? MongoBulkStrategy.TwoPhase : MongoBulkStrategy.SingleCommand,
            CollectionName = nonQuery.SourceQuery.CollectionExpression.CollectionName,
            BuildFilter = isTwoPhase
                ? null
                : qc => TranslateBulkOrThrow(nonQuery, () => BuildFilter<TSource>(qc, entityType, bsonSerializerFactory, nonQuery)),
            BuildUpdate = isUpdate
                ? qc => TranslateBulkOrThrow(nonQuery, () => BuildUpdate<TSource>(qc, entityType, bsonSerializerFactory, nonQuery))
                : null,
            BuildTargetIdQuery = isTwoPhase
                ? qc => BuildIdDocumentQuery<TSource>(qc, entityType, bsonSerializerFactory, nonQuery)
                : null,
        };
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

        // A projected query (anonymous/scalar projection, scalar aggregate, or a mixed projection containing
        // entity references) is never shaped from a full native document — it runs through the driver-LINQ
        // push-down path or the mixed client-side shaper. The native pipeline only covers full-entity results,
        // so a projected query is outside the native parity slice. Under NativeOnly the driver fallback is
        // forbidden, so any projected query is a compile-time coverage failure. (The QMTEV does not always flip
        // IsNativeRepresentable for a pushed-down projection — the projection can be realized entirely in the
        // shaper without a translated Select node — so the projected-path gate keys off routing here, not the
        // representable flag.)
        if (((MongoQueryCompilationContext)QueryCompilationContext).QueryMode == MongoQueryMode.NativeOnly)
        {
            throw new NativeTranslationNotSupportedException(
                "Query projects a non-entity result and MongoQueryMode.NativeOnly forbids the driver-LINQ fallback.");
        }

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
        var mode = ((MongoQueryCompilationContext)QueryCompilationContext).QueryMode;

        // ── The native-vs-driver gate (EF-323 B2) ──────────────────────────────────────────────
        // Unlike the spike (B1), which builds the native pipeline per execution inside TranslateQuery
        // and falls back to driver-LINQ at run time, this decision is made deterministically here at
        // COMPILE time. A native query that can be lowered/rendered yields a MongoPipelineFactory captured
        // into the executor; per execution it is only re-bound (factory.Build(parameterValues)) — never
        // re-translated. Because fallback happens here (not at run time), we compile EXACTLY ONE shaper
        // (streaming, DOM-native, or driver-DOM) and need no run-time dual-shaper dispatch.
        var nativeFactory = TryBuildNativeFactory(mode, mongoQueryExpression, shapedQueryExpression.ResultCardinality);

        // Streaming is only chosen when the native factory was built, the entity shape is streaming-eligible,
        // and every cross-collection join is a streamable single-level reference lookup the streaming reader
        // can read back. Otherwise the native pipeline (if any) returns full BsonDocuments shaped by the DOM
        // shaper, exactly as the driver-LINQ path does.
        var streaming = nativeFactory != null
            && shapedQueryExpression.ResultCardinality == ResultCardinality.Enumerable
            && StreamingEligibility.IsEligible(rootEntityType)
            && AllPendingLookupsAreStreamableReferences(mongoQueryExpression);

        var shaperBody = shapedQueryExpression.ShaperExpression;
        var bsonInjector = new BsonDocumentInjectingExpressionVisitor();
        shaperBody = bsonInjector.Visit(shaperBody);
#if EF8 || EF9
        var injectedBody = InjectEntityMaterializers(shaperBody);
#else
        var injectedBody = InjectStructuralTypeMaterializers(shaperBody);
#endif

        var standAloneStateManager = QueryCompilationContext.QueryTrackingBehavior ==
                                     QueryTrackingBehavior.NoTrackingWithIdentityResolution;

        if (streaming)
        {
            // Forward-only streaming materialization: rewrite the post-injection materializer to read each
            // native-path RawBsonDocument row via a single forward IBsonReader pass into typed locals. The
            // rewriter throws on any shape it cannot stream; because the native factory is fixed at compile
            // time, an un-streamable shape is a compile-time decision — fall back to the DOM shaper over the
            // (still native) BsonDocument pipeline rather than to a run-time dual-shaper, except under
            // NativeOnly where the un-streamable shape must surface.
            var rawRowParameter = Expression.Parameter(typeof(RawBsonDocument), "rawRow");
            try
            {
                var streamingBody = new MongoStreamingEntityMaterializerRewriter(
                        rootEntityType, _bsonSerializerFactory, rawRowParameter)
                    .Rewrite(injectedBody);
                var streamingLambda = Expression.Lambda(
                    streamingBody,
                    QueryCompilationContext.QueryContextParameter,
                    rawRowParameter);
                var compiledStreamingShaper = streamingLambda.Compile();

                return BuildExecuteCall(
                    typeof(RawBsonDocument),
                    Expression.Constant(compiledStreamingShaper),
                    streamingLambda.ReturnType,
                    streaming: true);
            }
            catch (Exception) when (mode != MongoQueryMode.NativeOnly)
            {
                // The entity shape itself isn't streamable; fall through to the DOM path (still native).
                streaming = false;
            }
        }

        var domShaperBody = createBindingRemover(bsonDocParameter, trackingBehavior).Visit(injectedBody);

        // Lift all BsonDocument/BsonArray variables to the lambda level so they are
        // accessible across entity boundaries in join projections.
        if (bsonInjector.AllVariables.Count > 0)
        {
            domShaperBody = Expression.Block(
                domShaperBody.Type,
                bsonInjector.AllVariables,
                domShaperBody);
        }

        var shaperLambda = Expression.Lambda(
            domShaperBody,
            QueryCompilationContext.QueryContextParameter,
            bsonDocParameter);
        var compiledShaper = shaperLambda.Compile();

        var projectedType = shaperLambda.ReturnType;

        // Both the native DOM path and the driver-LINQ path produce full BsonDocuments shaped by this lambda;
        // they differ only in whether nativeFactory is non-null (native pipeline) or null (driver-LINQ).
        return BuildExecuteCall(
            typeof(BsonDocument),
            Expression.Constant(compiledShaper),
            projectedType,
            streaming: false);

        // Builds the ExecuteShapedQuery call. Every argument is shared across the streaming and DOM paths
        // except the row generic type, the compiled shaper, the return-type generic, and the trailing
        // streaming flag — those four vary; the rest are baked in here.
        MethodCallExpression BuildExecuteCall(Type rowType, Expression compiledShaper, Type returnType, bool streaming)
            => Expression.Call(null,
                ExecuteShapedQueryMethodInfo.MakeGenericMethod(
                    rowType, rootEntityType.ClrType, returnType),
                QueryCompilationContext.QueryContextParameter,
                Expression.Constant(rootEntityType),
                Expression.Constant(_bsonSerializerFactory),
                Expression.Constant(mongoQueryExpression),
                compiledShaper,
                Expression.Constant(_contextType),
                Expression.Constant(standAloneStateManager),
                Expression.Constant(_threadSafetyChecksEnabled),
                Expression.Constant(shapedQueryExpression.ResultCardinality),
                Expression.Constant(nativeFactory, typeof(MongoPipelineFactory)),
                Expression.Constant(streaming));
    }

    /// <summary>
    /// The compile-time native-vs-driver gate honoring <see cref="MongoQueryMode"/>. Returns a
    /// <see cref="MongoPipelineFactory"/> when the query should execute as a native aggregation pipeline,
    /// or <see langword="null"/> to use the driver-LINQ path.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><see cref="MongoQueryMode.DriverLinq"/>: never native (returns null).</item>
    /// <item><see cref="MongoQueryMode.Native"/> (default): native when the query is representable and
    /// lowers/renders without throwing <see cref="NativeTranslationNotSupportedException"/>; otherwise null
    /// (driver fallback).</item>
    /// <item><see cref="MongoQueryMode.NativeOnly"/>: native, or throw at compile time for a non-representable
    /// query (the coverage instrument) — never falls back.</item>
    /// </list>
    /// The native pipeline returns full BsonDocuments (or RawBsonDocuments when streamed); the push-down
    /// projection path (ExecuteProjectedQuery) is never routed here. A cardinality reducer (First/Single/…)
    /// stays on the driver-LINQ path — the native path covers enumerable results.
    /// </remarks>
    private static MongoPipelineFactory? TryBuildNativeFactory(
        MongoQueryMode mode,
        MongoQueryExpression mongoQueryExpression,
        ResultCardinality resultCardinality)
    {
        if (mode == MongoQueryMode.DriverLinq)
        {
            return null;
        }

        // The native pipeline shapes from full documents via the client-side entity/mixed shaper, which only
        // runs for an enumerable result. A cardinality reducer uses the driver-LINQ path. Under NativeOnly the
        // gate still measures representability (below) — a representable reducer is not a coverage failure.
        if (resultCardinality != ResultCardinality.Enumerable)
        {
            if (mode == MongoQueryMode.NativeOnly && !mongoQueryExpression.IsNativeRepresentable)
            {
                throw new NativeTranslationNotSupportedException(
                    "Query is not natively representable and MongoQueryMode.NativeOnly forbids the driver-LINQ fallback.");
            }

            return null;
        }

        // A VectorSearch query is realized by the driver-LINQ path ($vectorSearch stage built from the captured
        // VectorSearch call) and carries the index-resolution / zero-results diagnostics. The native lowerer
        // reads only the logical slots and never the captured chain, so it would silently drop the vector
        // search. Vector search is therefore not natively representable; keep it on the driver path.
        if (!mongoQueryExpression.IsNativeRepresentable || ContainsVectorSearch(mongoQueryExpression.CapturedExpression))
        {
            if (mode == MongoQueryMode.NativeOnly)
            {
                throw new NativeTranslationNotSupportedException(
                    "Query is not natively representable and MongoQueryMode.NativeOnly forbids the driver-LINQ fallback.");
            }

            return null;
        }

        try
        {
            var stages = new MongoSelectLowerer().Lower(mongoQueryExpression);
            return MongoPipelineFactory.Create(stages, new MongoQueryLanguageRenderer());
        }
        catch (NativeTranslationNotSupportedException) when (mode != MongoQueryMode.NativeOnly)
        {
            // A representable query whose lookup shape (or other stage) the native pipeline can't emit:
            // fall back to the driver-LINQ path. Under NativeOnly this rethrows as the coverage instrument.
            return null;
        }
    }

    // Walks the captured Queryable method chain looking for a VectorSearch call. VectorSearch sits at the root
    // (optionally under a single pre-Where), so this descends through the source argument of each call.
    private static bool ContainsVectorSearch(Expression? captured)
    {
        while (captured is MethodCallExpression call)
        {
            if (call.IsVectorSearch())
            {
                return true;
            }

            captured = call.Arguments.Count > 0 ? call.Arguments[0] : null;
        }

        return false;
    }

    /// <summary>
    /// Whether every cross-collection join on <paramref name="mongoQueryExpression"/> is a single-level
    /// reference lookup the native pipeline can emit and the streaming materializer can read back from a
    /// root-level <c>_lookup_&lt;Nav&gt;</c> field — the same set the native lowerer emits. A query with no
    /// joins is trivially streamable; a query whose joins cannot ALL be expressed as such reference lookups
    /// (a collection include, a filtered include with pipeline stages, a transitive/nested lookup, or any
    /// join shape not mappable to a direct root reference navigation) stays on the DOM / driver-LINQ path.
    /// </summary>
    private static bool AllPendingLookupsAreStreamableReferences(MongoQueryExpression mongoQueryExpression)
    {
        var referenceLookups = mongoQueryExpression.GetStreamingReferenceLookups();

        // Every join must be covered by a streamable reference lookup; otherwise a join would be silently
        // dropped on the native path.
        if (mongoQueryExpression.IsJoinQuery
            && referenceLookups.Count < mongoQueryExpression.InnerCollections.Count)
        {
            return false;
        }

        return referenceLookups.All(lookup => lookup.IsStreamableReference);
    }

    private static (MongoQueryContext, MongoExecutableQuery) TranslateQuery<TEntity>(
        QueryContext queryContext,
        IReadOnlyEntityType entityType,
        BsonSerializerFactory bsonSerializerFactory,
        MongoQueryExpression queryExpression,
        ResultCardinality resultCardinality,
        MongoPipelineFactory? nativeFactory,
        bool streaming,
        Func<MongoEFToLinqTranslatingExpressionVisitor, Expression?, Expression> translate)
    {
        var mongoQueryContext = (MongoQueryContext)queryContext;
        var collection = mongoQueryContext.MongoClient.GetCollection<TEntity>(queryExpression.CollectionExpression.CollectionName);

        var transaction = mongoQueryContext.Context.Database.CurrentTransaction as MongoTransaction;

        // Native path (EF-323 B2): the factory was built once at compile time. Per execution we only bind the
        // current parameter values into the cached template (factory.Build) and run the resulting pipeline via
        // MongoClientWrapper.Execute. The driver-LINQ Query is left as Expression.Empty() — it is never used.
        if (nativeFactory != null)
        {
            var pipeline = nativeFactory.Build(GetParameterValues(queryContext));

            var queryable = transaction == null ? collection.AsQueryable() : collection.AsQueryable(transaction.Session);
            var nativeExecutable = new MongoExecutableQuery(
                Expression.Empty(),
                resultCardinality,
                (IMongoQueryProvider)queryable.Provider,
                collection.CollectionNamespace,
                new(new Dictionary<string, object>()))
            {
                NativePipeline = pipeline,
                Session = transaction?.Session,
                Streaming = streaming
            };

            return (mongoQueryContext, nativeExecutable);
        }

        var driverQueryable = transaction == null ? collection.AsQueryable() : collection.AsQueryable(transaction.Session);
        var source = driverQueryable.As((IBsonSerializer<TEntity>)bsonSerializerFactory.GetEntitySerializer(entityType));

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

    // Bridges QueryContext's per-execution parameter values to MongoPipelineFactory.Build, which takes an
    // IReadOnlyDictionary<string, object?>. EF8/EF9 expose ParameterValues; EF10 renamed this to Parameters.
    private static IReadOnlyDictionary<string, object?> GetParameterValues(QueryContext queryContext)
#if EF8 || EF9
        => queryContext.ParameterValues;
#else
        => queryContext.Parameters;
#endif

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
            nativeFactory: null, streaming: false,
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

    // TSource is the driver row type: RawBsonDocument when streaming, BsonDocument otherwise (the native-DOM
    // and driver-LINQ paths both produce BsonDocument). TEntity is the root entity CLR type; TResult is the
    // shaped result. When nativeFactory is non-null the query runs as a native aggregation pipeline (bound per
    // execution via factory.Build); otherwise it runs through the driver-LINQ provider. The decision was made
    // deterministically at compile time (see CompileShapedQuery / TryBuildNativeFactory), so exactly one shaper
    // — matching this TSource — was compiled, and MongoClientWrapper.Execute reads the matching collection type.
    private static QueryingEnumerable<TSource, TResult> ExecuteShapedQuery<TSource, TEntity, TResult>(
        QueryContext queryContext,
        IReadOnlyEntityType entityType,
        BsonSerializerFactory bsonSerializerFactory,
        MongoQueryExpression queryExpression,
        Func<QueryContext, TSource, TResult> shaper,
        Type contextType,
        bool standAloneStateManager,
        bool threadSafetyChecksEnabled,
        ResultCardinality resultCardinality,
        MongoPipelineFactory? nativeFactory,
        bool streaming)
    {
        var (mongoQueryContext, executableQuery) = TranslateQuery<TEntity>(
            queryContext, entityType, bsonSerializerFactory, queryExpression, resultCardinality,
            nativeFactory, streaming,
            (translator, expression) => translator.Translate(expression, resultCardinality));

        return new QueryingEnumerable<TSource, TResult>(
            mongoQueryContext,
            executableQuery,
            shaper,
            contextType,
            standAloneStateManager,
            threadSafetyChecksEnabled,
            GetOnZeroResultsAction(queryExpression));
    }

#if !EF8
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
            nativeFactory: null, streaming: false,
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
        // Invariant: ClassifyBulkSource (in the method-translating visitor) has already rejected, for the single-command
        // path, any operator other than Queryable.Where — so this walk should consume the whole chain down to the root.
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

        // Fail loud rather than silently drop: if a non-Where Queryable operator remains, the single-command classifier
        // (ClassifyBulkSource) admitted an operator this filter builder doesn't handle — that would otherwise scope the
        // operation incorrectly (wrong documents affected). Surfaces as a translation failure via TranslateBulkOrThrow.
        if (expression is MethodCallExpression { Method.DeclaringType: var remainingType } remaining
            && remainingType == typeof(Queryable))
        {
            throw new InvalidOperationException(
                $"Bulk filter construction encountered an unsupported '{remaining.Method.Name}' operator in the source "
                + "chain. Only 'Where' can scope a single-command bulk filter; this indicates the bulk-source classifier "
                + "admitted an operator the filter builder does not handle.");
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

    private static readonly MethodInfo CreateBulkPlanMethodInfo =
        typeof(MongoShapedQueryCompilingExpressionVisitor)
            .GetTypeInfo()
            .DeclaredMethods
            .Single(m => m.Name == nameof(CreateBulkPlan));

    private static readonly MethodInfo MongoBulkExecuteMethodInfo =
        typeof(MongoBulkOperationExecutor)
            .GetTypeInfo()
            .DeclaredMethods
            .Single(m => m.Name == nameof(MongoBulkOperationExecutor.Execute));

    private static readonly MethodInfo MongoBulkExecuteAsyncMethodInfo =
        typeof(MongoBulkOperationExecutor)
            .GetTypeInfo()
            .DeclaredMethods
            .Single(m => m.Name == nameof(MongoBulkOperationExecutor.ExecuteAsync));

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
