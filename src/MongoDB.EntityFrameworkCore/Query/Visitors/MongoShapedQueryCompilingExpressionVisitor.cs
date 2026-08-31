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
using System.Diagnostics;
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
using MongoDB.Bson.IO;
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

/// <summary>
/// The native-execution disposition of a query at the compile-time gate: whether it builds a native
/// pipeline, falls back to driver-LINQ gracefully (throwing only under <see cref="MongoQueryMode.NativeOnly"/>),
/// or must hard-decline (throw under <see cref="MongoQueryMode.Native"/> too, because its driver-LINQ
/// fallback returns silently wrong data). This is the single is-native classification the gate consults;
/// it is a superset of <see cref="Expressions.NativeRoute"/> (which answers "which native shape / is it slot
/// representable"), layering on the two is-native signals that are not slot representability: a lifted-out
/// vector search, and the GroupBy+Join wrong-data decline. Streaming-vs-DOM is a SEPARATE axis
/// (<c>AllPendingLookupsAreStreamable</c>) and is not part of this classification.
/// </summary>
internal enum NativeDisposition
{
    /// <summary>Build a native pipeline (via the <see cref="Expressions.NativeRoute"/>-appropriate builder).</summary>
    Native,

    /// <summary>Not natively representable: fall back to driver-LINQ; throw only under <see cref="MongoQueryMode.NativeOnly"/>.</summary>
    Fallback,

    /// <summary>Must throw under <see cref="MongoQueryMode.Native"/> AND <see cref="MongoQueryMode.NativeOnly"/>: the driver-LINQ fallback returns wrong rows (GroupBy+Join, or a self-paging join inner).</summary>
    HardDecline
}

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

        // The is-native disposition is centralized in ClassifyNativeDisposition. Here we act only on
        // HardDecline: a GroupBy+Join whose driver-LINQ fallback returns silently wrong data must throw under
        // Native/NativeOnly rather than route to that fallback (explicit DriverLinq stays
        // the user's opt-in). (The classification also evaluates ContainsVectorSearch, which the HardDecline
        // outcome never depends on — a deliberate, negligible compile-time extra walk kept so the disposition
        // has one source of truth.)
        var mode = ((MongoQueryCompilationContext)QueryCompilationContext).QueryMode;
        if (ClassifyNativeDisposition(mongoQueryExpression, mode) == NativeDisposition.HardDecline)
        {
            // A list rather than a single string: wrong-data provenances are independent and more than one can
            // be set on the SAME query, so list every cause that applies rather than picking one. Today there
            // is exactly one provenance (GroupBy+Join); a future one gets its own arm here.
            var causes = new List<string>();
            if (mongoQueryExpression.Select.IsGroupByFallbackUnsafe)
            {
                causes.Add(
                    "Query combines GroupBy with a Join, which the native translator does not support and whose "
                    + "driver-LINQ fallback returns incorrect results");
            }
            // Unreachable today: HardDecline is classified exactly when IsFallbackWrongData is true, which is
            // exactly the flag checked above, so at least one cause always fired. The
            // old ternary was total by construction (always produced a message); this list is not, structurally
            // — guard against a silently empty ("; use MongoQueryMode.DriverLinq...") message if a future cause
            // is added to IsFallbackWrongData without a matching arm here.
            Debug.Assert(causes.Count > 0, "HardDecline implies at least one wrong-data cause is set.");
            throw new NativeTranslationNotSupportedException(
                string.Join(". ", causes) + "; use MongoQueryMode.DriverLinq to opt in to the driver-LINQ execution of this query.");
        }

        var rootEntityType = mongoQueryExpression.CollectionExpression.EntityType;
        var projectedEntityType = QueryCompilationContext.Model.FindEntityType(
            shapedQueryExpression.ResultCardinality == ResultCardinality.Enumerable
                ? shapedQueryExpression.Type.TryGetItemType()!
                : shapedQueryExpression.Type);

        // Whole-element owned SelectMany: the QMTEV's TranslateSelect sets UnwindSource.WholeElement for a
        // bare-nav owned SelectMany whose trailing selector projects the whole inner element (`from o in q
        // from i in o.Items select i`). The lowerer emits $unwind(includeArrayIndex) + $replaceRoot
        // ($mergeObjects) for this shape, so after that stage the unwound OWNED element IS the root document
        // — root the shaper at the ELEMENT (owned) entity type, using the SAME standard
        // MongoProjectionBindingRemovingExpressionVisitor (just constructed with rootEntityType = the owned
        // type), and force the DOM shaper (allowStreaming: false): owned/collection elements are streaming-
        // ineligible anyway, and StreamingEligibility.IsEligible(rootEntityType) would otherwise be evaluated
        // against the OUTER root (Owner), which has no bearing on whether the re-rooted element document can be
        // streamed, and would read the wrong root regardless.
        //
        // This check MUST run before the projectedEntityType == null fallback immediately below:
        // projectedEntityType is looked up via QueryCompilationContext.Model.FindEntityType on the result CLR
        // type, which returns null for a SHARED-TYPE entity type (the same owned CLR type reused by more than
        // one owner/navigation) even when that type is otherwise perfectly representable via the WholeElement
        // mechanism above. wholeElementUnwind.InnerEntityType came from the binder's
        // navigation.TargetEntityType (the owner-scoped IEntityType the model actually built for this
        // navigation), never from FindEntityType(clrType) — so it is correct regardless of whether the element
        // CLR type happens to be shared. Running this check first means a shared-type owned element still
        // routes through the correct re-rooted shaper instead of silently falling into VisitProjectedQuery.
        if (mongoQueryExpression.Select.UnwindSource is { WholeElement: true } wholeElementUnwind)
        {
            var elementType = wholeElementUnwind.InnerEntityType;
            return CompileShapedQuery(shapedQueryExpression, mongoQueryExpression, elementType,
                (bsonDoc, behavior) => new MongoProjectionBindingRemovingExpressionVisitor(
                    elementType, mongoQueryExpression, bsonDoc, behavior),
                allowStreaming: false);
        }

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
        var queryMode = ((MongoQueryCompilationContext)QueryCompilationContext).QueryMode;

        // A grouping that never bound a supported aggregate projection (bare IGrouping sequence, an element
        // selector, or a computed/unsupported grouped shape we could not rewrite) still carries the placeholder
        // GroupByShaperExpression. That leaks into VerifyNoClientConstant below (which rejects it), so surface
        // the coverage decision first: under NativeOnly this is a native-coverage failure (throw the native
        // exception, not the raw "VisitChildren" error); under Native/DriverLinq fall through to the driver,
        // which reports its own inability to materialize a bare IGrouping.
        if (mongoQueryExpression.Select.Route == NativeRoute.Fallback
            && shapedQueryExpression.ShaperExpression is GroupByShaperExpression)
        {
            ThrowIfNativeOnlyForbidsFallback(queryMode, "Query groups without a supported aggregate projection");
        }

        VerifyNoClientConstant(shapedQueryExpression.ShaperExpression);

        // Native GroupBy: GroupBy(key).Select(aggregate) was bound to MongoSelectDefinition.Grouping
        // and lowered to a $group + flattening $project. Emit the native pipeline and shape each grouped row
        // with the DOM binding-removing shaper (which reads each result member by its top-level alias). Placed
        // before the NativeOnly guard so a representable grouping succeeds natively instead of being rejected.
        if (queryMode != MongoQueryMode.DriverLinq
            && mongoQueryExpression.Select.Route == NativeRoute.GroupBy)
        {
            return CompileShapedQuery(shapedQueryExpression, mongoQueryExpression, rootEntityType,
                (bsonDoc, behavior) => new MongoProjectionBindingRemovingExpressionVisitor(
                    rootEntityType, mongoQueryExpression, bsonDoc, behavior),
                allowStreaming: false);
        }

        // Native projection pushdown: a terminal member-access anonymous/DTO Select was lowered to a
        // $project slot in the QMTEV. Emit it as a native pipeline and shape the projected documents with the
        // DOM binding-removing shaper (which reads each field by its projection alias). Placed before the
        // NativeOnly guard so a representable projection succeeds natively instead of being rejected.
        if (queryMode != MongoQueryMode.DriverLinq
            && mongoQueryExpression.Select.Route == NativeRoute.Projection)
        {
            // stripBareProjectionOnFallback: the tier is read HERE, on the one branch that builds the
            // alias-addressed DOM shaper for a projection, and acted on inside CompileShapedQuery the moment
            // TryBuildNativeFactory declines. Reading it here rather than there is what keeps the strip
            // structurally disjoint from the mixed path's own StripPushedDownSelect call below — the two can
            // never both fire on one query, so the captured chain is never stripped twice.
            return CompileShapedQuery(shapedQueryExpression, mongoQueryExpression, rootEntityType,
                (bsonDoc, behavior) => new MongoProjectionBindingRemovingExpressionVisitor(
                    rootEntityType, mongoQueryExpression, bsonDoc, behavior),
                allowStreaming: false,
                stripBareProjectionOnFallback: ShouldStripBareProjectionOnFallback(mongoQueryExpression.Select),
                createFallbackBindingRemover: HasJoinScopeInnerEntityProjectionLeaf(mongoQueryExpression)
                                              || HasDocumentConstructionProjectionLeaf(mongoQueryExpression)
                    ? (bsonDoc, behavior) => new MongoMixedProjectionBindingRemovingExpressionVisitor(
                        rootEntityType, mongoQueryExpression, bsonDoc, behavior)
                    : null);
        }

        // Native scalar-aggregate path: Count/LongCount/Sum/Min/Max/Average/Any/All were
        // bound to MongoSelectDefinition.Cardinality by NativeCardinalityBinder and lowered to a terminal
        // $count/$group/$limit stage by MongoSelectLowerer. Emit the native pipeline and read the single
        // "v" field, applying the empty-input contract. Placed before the NativeOnly guard so a
        // representable aggregate succeeds natively instead of being rejected. The aggregate's predicate
        // (Where clauses, or the negated predicate for All) may still contain a shape the lowerer/renderer
        // can't emit (e.g. a parameterized string.Contains, which only supports constant terms) — that is
        // an execution-time lowering failure, not a routing decision, so it is tried (and can fall back)
        // here rather than baked unconditionally into a compile-time route.
        if (queryMode != MongoQueryMode.DriverLinq
            && mongoQueryExpression.Select.Route == NativeRoute.ScalarAggregate)
        {
            var aggregateFactory = TryBuildAggregateFactory(queryMode, mongoQueryExpression);
            if (aggregateFactory != null)
            {
                var cardinality = mongoQueryExpression.Select.Cardinality!;
                return Expression.Call(null,
                    ExecuteAggregateMethodInfo.MakeGenericMethod(rootEntityType.ClrType, cardinality.ResultType),
                    QueryCompilationContext.QueryContextParameter,
                    Expression.Constant(rootEntityType),
                    Expression.Constant(_bsonSerializerFactory),
                    Expression.Constant(mongoQueryExpression),
                    Expression.Constant(_contextType),
                    Expression.Constant(_threadSafetyChecksEnabled),
                    Expression.Constant(cardinality),
                    Expression.Constant(aggregateFactory));
            }

            // Fell back (Native mode only; NativeOnly already threw inside TryBuildAggregateFactory): the
            // aggregate itself is representable (Route == ScalarAggregate) but its predicate/selector could
            // not be lowered/rendered. Continue below to the ordinary driver-LINQ push-down aggregate path.
        }

        // A projected query (anonymous/scalar projection, scalar aggregate, or a mixed projection containing
        // entity references) is never shaped from a full native document — it runs through the driver-LINQ
        // push-down path or the mixed client-side shaper. The native pipeline only covers full-entity results,
        // so a projected query is outside the native parity slice. Under NativeOnly the driver fallback is
        // forbidden, so any projected query is a compile-time coverage failure. (The gate now keys off the
        // single Select.Route predicate — NativeRoute.Projection encodes both "representable" and "has a
        // projection" — so this projected-path gate keys off routing here, not a separate representable flag.)
        ThrowIfNativeOnlyForbidsFallback(queryMode, "Query projects a non-entity result");

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

    /// <summary>
    /// Whether a native-factory failure on the <see cref="NativeRoute.Projection"/> route must strip the
    /// pushed-down <c>Select</c> out of the captured chain before that chain is handed to the driver-LINQ
    /// bridge.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The shaper is built FIRST and native-vs-driver is decided SECOND (see <see cref="CompileShapedQuery"/>),
    /// so a late <c>TryBuildNativeFactory</c> decline hands the ALREADY alias-addressed DOM shaper a pipeline
    /// the driver rendered from the captured chain — <c>$project</c> and all. For a
    /// <see cref="NativeRoute.Projection"/> shape whose aliases are all projection-member names that is
    /// harmless, because the driver picks the same names. It stops being harmless the moment the emit side
    /// registers an alias OVERRIDE, which is exactly the set of shapes that reach this predicate as
    /// <see langword="true"/>:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// a BARE selector body — the driver names a bare projection <c>_v</c>, the emit side named it the leaf's
    /// document path;
    /// </description></item>
    /// <item><description>
    /// an <c>OwnsOne</c>-hop array leaf — the driver names it by MEMBER (<c>Notes</c>), the emit side named it
    /// by full document path (<c>Home.Notes</c>). The driver's alias being the member name is the problem — it
    /// is not the name the shaper reads by; left un-stripped, the wrapped hop shape returns EMPTY collections
    /// silently under the default mode.
    /// </description></item>
    /// </list>
    /// <para>
    /// Stripping makes the fallback yield WHOLE documents instead, which a
    /// <see cref="ProjectionAliasTier.DocumentPath"/> alias reads correctly precisely because that alias IS the
    /// leaf's root-relative document path — for a dotted one via
    /// <c>BsonBinding</c>'s segment walk, which resolves identically against a projected document and an
    /// un-projected one. The SIBLING leaves of a stripped projection are whole-document-readable too, and that
    /// is guaranteed rather than hoped: an array leaf forces
    /// <c>NativeProjectionBinder.IsWholeDocumentReadableLeaf</c> on every non-array sibling, and a bare body has
    /// no siblings at all.
    /// </para>
    /// <para>
    /// So the strip is TIER-CONDITIONAL, and both arms matter: strip for
    /// <see cref="ProjectionAliasTier.DocumentPath"/> (path-addressable, therefore whole-document-readable);
    /// do NOT strip for <see cref="ProjectionAliasTier.Synthetic"/>, whose <c>_v</c> alias has no document path
    /// at all — leaving the driver's own push-down in place is exactly what makes that read hit. Stripping it
    /// instead turns a working query into <c>Document element '_v' is missing but required</c>.
    /// </para>
    /// <para>
    /// The <see cref="ProjectionAliasTier.Synthetic"/> arm's long-term disposition is open — depending on the
    /// driver's own <c>_v</c> push-down is what makes that arm work, so the tier cannot simply be deleted when
    /// the driver-LINQ fallback is retired.
    /// </para>
    /// <para>
    /// The tier is read as DATA off the override the emit side registered — never by sniffing the alias string
    /// for <c>"_v"</c>, and never by asking whether the override is the bare one. A string sniff would be a
    /// second, independently derived copy of a fact the emit side already knows, which is the failure mode the
    /// alias carrier exists to remove.
    /// </para>
    /// <para>
    /// <see cref="NativeRoute"/> is deliberately NOT checked here: the sole call site is the
    /// <see cref="NativeRoute.Projection"/> branch of <see cref="VisitProjectedQuery"/>, and by the time the
    /// gate runs every route flip has already happened, so the branch itself is the routing gate. The method is
    /// <c>internal</c> rather than <c>private</c> only so the unit tests can pin the tier-vs-alias-string
    /// decision directly.
    /// </para>
    /// </remarks>
    internal static bool ShouldStripBareProjectionOnFallback(MongoSelectDefinition select)
        => select.HasDocumentPathAliasOverride;

    /// <summary>
    /// Whether the <see cref="NativeRoute.Projection"/> route staged a whole-entity INNER leaf for a genuine
    /// two-sided join (EF-444) — the one alias in this provider that is neither a projection-member name nor a
    /// <see cref="ProjectionAliasTier.DocumentPath"/> override, and therefore the one that a late
    /// <c>TryBuildNativeFactory</c> decline cannot read correctly off the driver-LINQ bridge's own rendering.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MEASURED, not predicted (EF-444 Task 4). For <c>Join(…).Where(parameterized).Select(x =&gt; new { x.Outer,
    /// x.Inner })</c> the bridge renders <c>{"o": "$$ROOT", "r": "$_lookup_Orders"}</c> — every leaf under its
    /// MEMBER name. The Outer leaf is fine (its native alias IS the member name, which is exactly the
    /// coincidence EF-412's own late-fallback leg relies on), but the Inner leaf's native alias is the join's
    /// <c>$lookup</c> prefix (<c>MongoJoinScope.InnerPrefix</c>, e.g. <c>_lookup_Orders</c>) — forced, because
    /// <c>MongoProjectionBindingRemovingExpressionVisitor</c>'s cross-collection arm derives the field name from
    /// the NAVIGATION and discards the projection alias entirely. So the shaper reads <c>doc["_lookup_Orders"]</c>
    /// against a document that only has <c>"r"</c>, the read is non-required, and the inner entity came back
    /// SILENTLY NULL — no exception, wrong data.
    /// </para>
    /// <para>
    /// The fix routes that leg to the same place an explicit <see cref="MongoQueryMode.DriverLinq"/> already
    /// sends this shape: strip the pushed-down <c>Select</c> so the bridge yields WHOLE, un-projected documents
    /// (which still carry the join's own <c>_lookup_&lt;Nav&gt;</c> field, since the <c>$lookup</c>/<c>$unwind</c>
    /// were registered at translation time and are mode-independent) and shape them with
    /// <see cref="MongoMixedProjectionBindingRemovingExpressionVisitor"/>, whose
    /// <c>ReadsUnprojectedDocuments</c> is what makes the sibling Outer leaf's <c>$$ROOT</c> alias resolve to the
    /// document itself. That combination is not new machinery and is not a guess about the driver's rendering:
    /// it is the pre-EF-444 behaviour of this exact shape, still exercised on every explicit-DriverLinq run.
    /// </para>
    /// <para>
    /// Deliberately NOT applied to every <see cref="NativeRoute.Projection"/> late fallback. EF-412's
    /// root-entity leaf and every scalar/computed leaf are read by member-name alias and the bridge renders
    /// member-name aliases, so they are correct against the driver's own <c>$project</c> and would be made
    /// WORSE by stripping it (a computed leaf has no whole-document field to read at all). The gate is
    /// therefore the presence of the one alias that disagrees.
    /// </para>
    /// </remarks>
    /// <remarks>
    /// The <see cref="MongoElementRefExpression"/> conjunct is not redundant with the alias match. Only
    /// <c>NativeJoinScopeProjectionBinder</c>'s Inner arm stages that alias, and only as an element ref — but
    /// that is a fact two files away (kept true there by its <c>seenAliases</c> seeding, which declines a user
    /// member spelled exactly like the lookup prefix). Checking the node kind here makes the predicate
    /// self-evidently correct instead of correct-by-remote-invariant.
    /// </remarks>
    private static bool HasJoinScopeInnerEntityProjectionLeaf(MongoQueryExpression mongoQueryExpression)
        => mongoQueryExpression.Select.JoinScope is { } scope
           && mongoQueryExpression.Select.Projection.Any(
               p => p.Alias == scope.InnerPrefix && p.Expression is MongoElementRefExpression);

    /// <summary>
    /// A THIRD trigger (EF-447) for the same late-fallback problem <see cref="HasJoinScopeInnerEntityProjectionLeaf"/>
    /// guards: a constructed sub-entity leaf (<c>Select(b =&gt; new { Copy = new Book { Id = b.Id, ... } })</c>)
    /// has no whole-document field it can be read at all — its members live NESTED under its own <c>$project</c>
    /// alias (<c>"Copy.Id"</c>), a path that exists ONLY in the native <c>$project</c> output, never on a raw
    /// stored document. So a late <c>TryBuildNativeFactory</c> decline can't reuse the ordinary alias-addressed
    /// DOM shaper the way EF-412's whole-root-entity leaf or a plain scalar leaf can (their aliases ARE real
    /// document paths); it must strip the pushed-down <c>Select</c> AND swap in the mixed removing visitor,
    /// whose <c>ReadDocumentConstructionMember</c> override reads each member at its own NATURAL root-relative
    /// path off the whole document instead.
    /// </summary>
    private static bool HasDocumentConstructionProjectionLeaf(MongoQueryExpression mongoQueryExpression)
        => mongoQueryExpression.Select.Projection.Any(p => p.Expression is MongoDocumentConstructionExpression);

    private MethodCallExpression CompileShapedQuery(
        ShapedQueryExpression shapedQueryExpression,
        MongoQueryExpression mongoQueryExpression,
        IEntityType rootEntityType,
        Func<ParameterExpression, QueryTrackingBehavior, System.Linq.Expressions.ExpressionVisitor> createBindingRemover,
        bool allowStreaming = true,
        bool stripBareProjectionOnFallback = false,
        Func<ParameterExpression, QueryTrackingBehavior, System.Linq.Expressions.ExpressionVisitor>?
            createFallbackBindingRemover = null)
    {
        var bsonDocParameter = Expression.Parameter(typeof(BsonDocument), "bsonDoc");
        var trackingBehavior = QueryCompilationContext.QueryTrackingBehavior;
        var mode = ((MongoQueryCompilationContext)QueryCompilationContext).QueryMode;

        // ── The native-vs-driver gate ───────────────────────────────────────────────────────────
        // This decision is made deterministically here at COMPILE time, not per execution. A native query
        // that can be lowered/rendered yields a MongoPipelineFactory captured into the executor; per
        // execution it is only re-bound (factory.Build over a MongoNativeBuildContext, which also
        // constructs any deferred stage slot) — never re-translated. Because fallback happens here (not at
        // run time), we compile EXACTLY ONE shaper (streaming, DOM-native, or driver-DOM) and need no
        // run-time dual-shaper dispatch.
        var nativeFactory = TryBuildNativeFactory(mode, mongoQueryExpression);

        // The late-fallback bare-projection strip. The shaper above was built
        // alias-addressed for the native $project; the driver-LINQ fallback renders the captured chain
        // INCLUDING the pushed-down bare Select, which the driver aliases `_v`. For a path-addressable
        // (tier-1) bare leaf the two disagree, so remove the Select and let the fallback yield whole
        // documents — the read the tier-1 alias is correct against. See ShouldStripBareProjectionOnFallback
        // for why this is tier-conditional and why the flag is decided at the call site.
        // Only CapturedExpression is touched, and only the driver-LINQ execution path reads it, so the shaper
        // built above is unaffected.
        //
        // createFallbackBindingRemover is the second, EF-444 arm of the SAME late-fallback problem: a join's
        // whole-entity INNER leaf whose native alias is the join's own $lookup prefix, which the bridge never
        // renders. That arm strips for the same reason and additionally swaps in the MIXED removing visitor,
        // because the whole-document read it lands on needs ReadsUnprojectedDocuments for its sibling $$ROOT
        // Outer leaf. See HasJoinScopeInnerEntityProjectionLeaf. The two arms are disjoint by construction
        // (a DocumentPath alias override and a join-scope inner leaf cannot coexist on one projection — the
        // join binder stages neither array nor bare leaves), but the strip is written as one branch so it can
        // never run twice.
        var useFallbackBindingRemover = nativeFactory == null && createFallbackBindingRemover != null;
        if (nativeFactory == null && (stripBareProjectionOnFallback || useFallbackBindingRemover))
        {
            mongoQueryExpression.CapturedExpression =
                StripPushedDownSelect(mongoQueryExpression.CapturedExpression);
        }

        // Streaming is only chosen when the native factory was built, the entity shape is streaming-eligible,
        // and every cross-collection join is a streamable single-level reference lookup the streaming reader
        // can read back. Otherwise the native pipeline (if any) returns full BsonDocuments shaped by the DOM
        // shaper, exactly as the driver-LINQ path does.
        // NOTE: native-vs-driver was already decided above by TryBuildNativeFactory (the lowerer); this
        // predicate only gates streaming-vs-DOM for a pipeline that is already native.
        var streaming = allowStreaming
            && nativeFactory != null
            && shapedQueryExpression.ResultCardinality == ResultCardinality.Enumerable
            && StreamingEligibility.IsEligible(rootEntityType)
            && AllPendingLookupsAreStreamable(mongoQueryExpression);

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
            // One-pass "deserialize IS materialize": rewrite the post-injection materializer to read
            // exactly one document off a passed-in IBsonReader (ReadStartDocument … fill loop … ReadEndDocument;
            // no open, no dispose — the driver cursor owns the reader). The compiled shaper becomes the
            // Deserialize body of a custom IBsonSerializer<TEntity> supplied to Aggregate as the pipeline output
            // serializer, so the driver's own deserialization pass produces the finished entity — the cursor
            // yields TEntity directly and the QueryingEnumerable shaper is an identity map. The rewriter throws
            // on any shape it cannot stream; because the native factory is fixed at compile time, an
            // un-streamable shape is a compile-time decision — fall back to the DOM shaper over the (still
            // native) BsonDocument pipeline, except under NativeOnly where the un-streamable shape must surface.
            var readerParameter = Expression.Parameter(typeof(IBsonReader), "__reader");
            var contextParameter = Expression.Parameter(typeof(BsonDeserializationContext), "__context");
            try
            {
                var onePassBody = new MongoStreamingEntityMaterializerRewriter(rootEntityType, _bsonSerializerFactory)
                    .Rewrite(injectedBody, readerParameter, contextParameter);

                var onePassLambda = Expression.Lambda(
                    onePassBody,
                    QueryCompilationContext.QueryContextParameter,
                    readerParameter,
                    contextParameter);
                var compiledOnePassShaper = onePassLambda.Compile(); // Func<QueryContext, IBsonReader, BsonDeserializationContext, TResult>

                // The cursor yields the finished TResult, so TSource == TResult and the QueryingEnumerable
                // shaper is identity. The per-execution output serializer is built in TranslateQuery from this
                // compiled shaper + the live MongoQueryContext (see ExecuteShapedQuery / onePassShaper).
                var resultType = onePassLambda.ReturnType;
                var rowParameter = Expression.Parameter(resultType, "row");
                var identityShaper = Expression.Lambda(
                    rowParameter, QueryCompilationContext.QueryContextParameter, rowParameter).Compile();

                return BuildExecuteCall(
                    resultType,
                    Expression.Constant(identityShaper),
                    resultType,
                    streaming: true,
                    onePassShaper: Expression.Constant(
                        compiledOnePassShaper,
                        typeof(Func<,,,>).MakeGenericType(
                            typeof(QueryContext), typeof(IBsonReader), typeof(BsonDeserializationContext), resultType)));
            }
            catch (NativeTranslationNotSupportedException) when (mode != MongoQueryMode.NativeOnly)
            {
                // The entity shape itself isn't streamable; fall through to the DOM path (still native).
                // Narrowed from a broad `catch (Exception)` (EF-340): that swallowed ANY failure of the
                // rewrite/compile above — including an unintended bug in the rewriter itself — as a silent
                // fall-through to DOM. NativeTranslationNotSupportedException is the rewriter's only
                // intended "can't stream this shape" signal; every other exception should surface.
                streaming = false;
            }
        }

        var domShaperBody = (useFallbackBindingRemover ? createFallbackBindingRemover! : createBindingRemover)(
            bsonDocParameter, trackingBehavior).Visit(injectedBody);

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
        // except the row generic type, the compiled shaper, the return-type generic, the streaming flag, and
        // the one-pass output shaper — those vary; the rest are baked in here. onePassShaper is a
        // Func<QueryContext, IBsonReader, BsonDeserializationContext, TResult> only on the one-pass streaming
        // path (null otherwise), from which ExecuteShapedQuery builds the per-execution output serializer.
        MethodCallExpression BuildExecuteCall(
            Type rowType, Expression compiledShaper, Type returnType, bool streaming, Expression? onePassShaper = null)
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
                Expression.Constant(streaming),
                onePassShaper ?? Expression.Constant(
                    null,
                    typeof(Func<,,,>).MakeGenericType(
                        typeof(QueryContext), typeof(IBsonReader), typeof(BsonDeserializationContext), returnType)));
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
    /// projection path (ExecuteProjectedQuery) is never routed here. An entity reducer (First/Single/…)
    /// stays on the native path when representable — it synthesizes a $limit and EF Core's base
    /// cardinality reduction runs over the returned IEnumerable&lt;T&gt;. A scalar aggregate (Count/Sum/…)
    /// still stays on the driver-LINQ path — it is not yet lowered by the native pipeline.
    /// </remarks>
    private static MongoPipelineFactory? TryBuildNativeFactory(
        MongoQueryMode mode,
        MongoQueryExpression mongoQueryExpression)
    {
        if (mode == MongoQueryMode.DriverLinq)
        {
            return null;
        }

        // An entity reducer (First/Single/…) keeps Route == WholeEntity with a synthesized $limit
        // (NativeCardinalityBinder) and stays on the native path here: it shapes from the same full
        // documents as an enumerable result, and EF Core's base cardinality reduction runs over the
        // returned IEnumerable<T> to apply First/Single semantics. Scalar aggregates (Route ==
        // ScalarAggregate) are not yet lowered by the pipeline and remain on the driver-LINQ path.
        //
        // A VectorSearch query is native when — and only when — NativeSlotPopulator bound it into
        // MongoSelectDefinition.VectorSearch. The lowerer emits the $vectorSearch stage (plus its
        // $addFields{__score} companion) from that slot, ahead of every other stage, and
        // MongoPipelineFactory's deferred slot runs the SAME VectorSearchStageBuilder the driver-LINQ bridge
        // does — so the index resolution, the VectorSearchNeedsIndex warning and the AdditionalState the
        // zero-results diagnostic reads are all produced identically on both paths. An UNBOUND vector search
        // (the binder declined) classifies as Fallback via hasUnboundVectorSearch, so it keeps the driver path;
        // the lowerer can never be reached with a vector search it has no slot to emit.
        // This builder handles the whole-entity / reducer / projection / group native pipelines. It declines
        // when the query is not native at all (ClassifyNativeDisposition != Native) AND, additionally,
        // when Route == ScalarAggregate: that shape IS native but is built by TryBuildAggregateFactory, so
        // control must fall through to it here. (A HardDecline was already thrown in VisitShapedQuery before
        // reaching this builder under Native/NativeOnly; under DriverLinq no native factory is attempted.)
        if (ClassifyNativeDisposition(mongoQueryExpression, mode) != NativeDisposition.Native
            || mongoQueryExpression.Select.Route == NativeRoute.ScalarAggregate)
        {
            ThrowIfNativeOnlyForbidsFallback(mode, "Query is not natively representable");
            return null;
        }

        return TryBuildPipeline(mongoQueryExpression, mode);
    }

    // Builds the native pipeline template for a scalar-aggregate query (Route == NativeRoute.ScalarAggregate),
    // mirroring TryBuildNativeFactory's try/lower/render/fallback shape but without its ScalarAggregate
    // exclusion (that exclusion exists precisely to route here instead). The aggregate's own shape (Count,
    // Any, a plain member-access selector, ...) was already confirmed representable by NativeCardinalityBinder
    // at bind time, but the predicate it composed (user Where clauses, or the negated predicate for All) can
    // still contain something the lowerer/renderer can't emit (e.g. a parameterized string.Contains, which
    // only supports constant terms) — that failure surfaces here as an ordinary NativeTranslationNotSupportedException.
    private static MongoPipelineFactory? TryBuildAggregateFactory(
        MongoQueryMode mode,
        MongoQueryExpression mongoQueryExpression)
        => TryBuildPipeline(mongoQueryExpression, mode);

    // Shared lower→render tail for TryBuildNativeFactory and TryBuildAggregateFactory: each factory keeps its
    // own up-front route/guard checks, then delegates to this common lower/render/fallback body.
    private static MongoPipelineFactory? TryBuildPipeline(MongoQueryExpression mongoQueryExpression, MongoQueryMode mode)
    {
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

    // Executes a native scalar-aggregate pipeline (Count/LongCount/Sum/Min/Max/Average/Any/All) and applies
    // the empty-input contract. Reuses TranslateQuery<TEntity> exactly like the entity path — the native
    // factory built at compile time (TryBuildAggregateFactory) takes the same nativeFactory-!= null branch,
    // so the passed `translate` delegate is never invoked. Returns exactly one element (mirrors
    // ExecuteScalar's historical [result] contract) so EF Core's base .Single() cardinality reduction yields
    // the scalar.
    // The return type is the concrete SingleValueEnumerable<TResult> — not the IEnumerable<TResult> interface
    // — because EF Core's base VisitExtension picks Single(...) or SingleAsync(...) depending on
    // QueryCompilationContext.IsAsync and calls Expression.Call(method, ourResultExpression); SingleAsync's
    // parameter type is IAsyncEnumerable<TResult>. An IEnumerable<TResult>-typed expression is not assignable
    // to that parameter, so the same executor must return a type implementing both sync and async enumeration
    // (mirrors why the entity/projection paths use the dual-interface QueryingEnumerable<,>).
    private static SingleValueEnumerable<TResult> ExecuteAggregate<TEntity, TResult>(
        QueryContext queryContext,
        IReadOnlyEntityType entityType,
        BsonSerializerFactory bsonSerializerFactory,
        MongoQueryExpression queryExpression,
        Type contextType,
        bool threadSafetyChecksEnabled,
        MongoCardinality cardinality,
        MongoPipelineFactory nativeFactory)
    {
        var (mongoQueryContext, executableQuery) = TranslateQuery<TEntity>(
            queryContext, entityType, bsonSerializerFactory, queryExpression, ResultCardinality.Single,
            nativeFactory, streaming: false, static (_, _) => Expression.Empty());

        using var rows = mongoQueryContext.MongoClient
            .Execute<BsonDocument>(executableQuery, out var log)
            .GetEnumerator();

        TResult value;
        try
        {
            if (rows.MoveNext())
            {
                var doc = rows.Current;

                // Presence-only aggregates (Any/All): presence of a surviving row is the signal, not the field
                // value. Any's $match already includes the user predicate (if any), so a surviving row means at
                // least one element matched. All's $match holds the NEGATED predicate, so a surviving row means
                // at least one element failed it — i.e. All is false. See MongoSelectLowerer /
                // NativeCardinalityBinder.TryBindAggregate.
                value = cardinality.PresenceOnly ? (TResult)cardinality.PresentValue! : DeserializeScalar<TResult>(doc);
            }
            else
            {
                value = cardinality.EmptyBehavior switch
                {
                    MongoEmptyAggregateBehavior.DefaultValue => (TResult)cardinality.EmptyValue!,
                    MongoEmptyAggregateBehavior.ReturnNull => default!,
                    MongoEmptyAggregateBehavior.Throw => throw new InvalidOperationException(
                        "Sequence contains no elements"),
                    _ => throw new InvalidOperationException("Sequence contains no elements")
                };
            }
        }
        finally
        {
            log();
        }

        return new SingleValueEnumerable<TResult>(value);
    }

    // A one-element sequence implementing both IEnumerable<T> and IAsyncEnumerable<T> so the same eagerly-
    // computed ExecuteAggregate result can satisfy either EF Core's base Single(...) or SingleAsync(...)
    // reduction (chosen at compile time based on QueryCompilationContext.IsAsync) without a second executor.
    private sealed class SingleValueEnumerable<T>(T value) : IEnumerable<T>, IAsyncEnumerable<T>
    {
        public IEnumerator<T> GetEnumerator()
        {
            yield return value;
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

        public async IAsyncEnumerator<T> GetAsyncEnumerator(System.Threading.CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask.ConfigureAwait(false);
            yield return value;
        }
    }

    // Reads the "v" field written by the $count/$group terminal stage and coerces it to TResult. BsonTypeMapper
    // maps the BSON numeric value to its natural CLR type (int/long/double/decimal/...); Convert.ChangeType then
    // coerces to the exact result type the aggregate operator promises (e.g. long for LongCount, double for
    // Average), honoring Nullable<T> result types.
    private static TResult DeserializeScalar<TResult>(BsonDocument doc)
    {
        var bsonValue = doc[BsonValueSerializer.ScalarField];
        var mapped = BsonTypeMapper.MapToDotNetValue(bsonValue);

        // A non-empty aggregate can still yield a BSON-null accumulator (e.g. Min/Max/Average over a
        // nullable field where every row's value is null/missing) — the empty-input path above is not
        // taken since a $group row was returned. Convert.ChangeType(null, ...) throws for a value type,
        // so short-circuit here: null for a nullable TResult; unreachable for non-nullable TResult since
        // that case takes EmptyBehavior.Throw when there are no rows and can't yield null otherwise.
        if (mapped is null)
        {
            return default!;
        }

        var targetType = Nullable.GetUnderlyingType(typeof(TResult)) ?? typeof(TResult);
        var converted = targetType.IsInstanceOfType(mapped) ? mapped : ConvertNumericNarrowing(mapped, targetType);

        return (TResult)converted!;
    }

    // $sum widens its accumulator as the running total grows past int32's then int64's range (server-side,
    // unconditionally — there is no way to ask Mongo for checked BCL semantics), so the value read back can
    // be wider than TResult's underlying numeric type. Convert.ChangeType performs a *checked* narrowing and
    // throws OverflowException the moment the widened total falls outside TResult's range — which does not
    // reproduce BCL Sum's per-element checked-overflow semantics (that would require throwing at the exact
    // element where the running total first overflowed, not after the fact against the final widened total)
    // and is an accepted, documented divergence — but an unhandled exception here would still be worse than
    // that divergence. Narrow with an explicit unchecked cast instead, so an overflowing/imprecise total is
    // returned (wrapped or rounded) rather than throwing.
    private static object ConvertNumericNarrowing(object mapped, Type targetType) => mapped switch
    {
        long l when targetType == typeof(int) => unchecked((int)l),
        double d when targetType == typeof(int) => unchecked((int)d),
        double d when targetType == typeof(long) => unchecked((long)d),
        _ => Convert.ChangeType(mapped, targetType)
    };

    // Under MongoQueryMode.NativeOnly the driver-LINQ fallback is forbidden, so a query the native path cannot
    // handle is a compile-time coverage failure rather than a silent fallback. Centralizes that policy so every
    // gate site throws the same way; callers guard on the specific non-representable condition and pass the
    // reason phrase. A no-op under Native / DriverLinq.
    private static void ThrowIfNativeOnlyForbidsFallback(MongoQueryMode mode, string reason)
    {
        if (mode == MongoQueryMode.NativeOnly)
        {
            throw new NativeTranslationNotSupportedException(
                $"{reason} and MongoQueryMode.NativeOnly forbids the driver-LINQ fallback.");
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
    /// Classify a query's native disposition from the three authoritative is-native signals, read here in one
    /// place. This is the single source of truth for the is-native gate decision; all gate sites
    /// consult it rather than re-deriving. Pure over its inputs so it is unit-testable in isolation.
    /// </summary>
    /// <param name="route">The slot/projection representability route (<see cref="MongoSelectDefinition.Route"/>).</param>
    /// <param name="isFallbackWrongData">Whether this query's driver-LINQ fallback returns silently wrong rows —
    /// today exactly a GroupBy combined with a join. See
    /// <see cref="MongoSelectDefinition.IsFallbackWrongData"/>.</param>
    /// <param name="hasUnboundVectorSearch">
    /// Whether the captured chain contains a lifted-out <c>VectorSearch</c> that the native slot populator did
    /// NOT bind into <see cref="MongoSelectDefinition.VectorSearch"/>. This is one fact read twice: a BOUND
    /// slot makes this <see langword="false"/>, so the query classifies Native AND the lowerer has a
    /// <c>$vectorSearch</c> stage to emit; an UNBOUND one makes it <see langword="true"/>, and the binder's
    /// only other exit is <c>MarkNotNativelyRepresentable()</c>, so <paramref name="route"/> is
    /// <see cref="NativeRoute.Fallback"/> as well. The dangerous middle state — native route, no stage emitted,
    /// which returns the right ROW COUNT in INSERTION order rather than score order with no exception — is
    /// therefore unreachable rather than merely avoided.
    /// </param>
    /// <param name="mode">The active <see cref="MongoQueryMode"/>.</param>
    internal static NativeDisposition ClassifyNativeDisposition(
        NativeRoute route,
        bool isFallbackWrongData,
        bool hasUnboundVectorSearch,
        MongoQueryMode mode)
    {
        // A query whose driver-LINQ fallback returns wrong rows must hard-decline under Native/NativeOnly.
        // Explicit DriverLinq is the user's opt-in and runs it (not a hard decline). Checked first so it takes
        // precedence over the graceful-fallback signals.
        if (mode != MongoQueryMode.DriverLinq && isFallbackWrongData)
        {
            return NativeDisposition.HardDecline;
        }

        // Not natively representable (an unsupported slot/projection shape, or a lifted-out vector search the
        // slot populator did not bind, so the native lowerer would never emit it) -> graceful driver-LINQ
        // fallback, which still carries the VectorSearch in the captured chain and runs it correctly.
        if (route == NativeRoute.Fallback || hasUnboundVectorSearch)
        {
            return NativeDisposition.Fallback;
        }

        return NativeDisposition.Native;
    }

    /// <summary>
    /// Gather the three is-native signals from <paramref name="q"/> and classify (see the pure
    /// <see cref="ClassifyNativeDisposition(NativeRoute, bool, bool, MongoQueryMode)"/> overload). The one
    /// signal that cannot live wholly on <see cref="MongoSelectDefinition"/> is vector search: the
    /// <c>VectorSearch</c> call is lifted out of the tree before the Select is built, so its PRESENCE is read
    /// from the captured chain here — and paired with the slot the native binder either did or did not fill, so
    /// the two gates open and close together (see the <c>hasUnboundVectorSearch</c> parameter documentation).
    /// </summary>
    private static NativeDisposition ClassifyNativeDisposition(MongoQueryExpression q, MongoQueryMode mode)
        => ClassifyNativeDisposition(
            q.Select.Route,
            q.Select.IsFallbackWrongData,
            ContainsVectorSearch(q.CapturedExpression) && q.Select.VectorSearch is null,
            mode);

    /// <summary>
    /// The STREAMING gate: whether every cross-collection join on <paramref name="mongoQueryExpression"/> is a
    /// single-level reference lookup the streaming reader can read back from a root-level
    /// <c>_lookup_&lt;Nav&gt;</c> field. This does NOT decide native vs. driver-LINQ — that decision is made
    /// upstream by <c>TryBuildNativeFactory</c> (the lowerer). This predicate only decides, for a pipeline that
    /// is already native, whether it may additionally use the forward-only streaming shaper instead of the DOM
    /// shaper. A query with no joins is trivially streamable. A native **collection** Include lookup is
    /// deliberately treated as NOT streamable here (it stays on the native-DOM path) — do not change this to
    /// admit collection lookups; a query whose joins cannot ALL be expressed as streamable reference lookups
    /// (a collection include, a filtered include with pipeline stages, a transitive/nested lookup, or any join
    /// shape not mappable to a direct root reference navigation) stays on the DOM / driver-LINQ path.
    /// <para>
    /// Also NOT streamable when the looked-up navigation's TARGET has an eager-loaded navigation of its own
    /// (an owned/embedded reference or collection, auto-included by EF Core convention — a shape
    /// <c>MongoQueryableMethodTranslatingExpressionVisitor</c>'s <c>HasNonEmbeddedThenInclude</c> lets through
    /// at confirmation time). <c>MongoStreamingEntityMaterializerRewriter</c> has no plan for a nested include
    /// on a looked-up reference's target and throws <c>NativeTranslationNotSupportedException</c> the moment
    /// the STREAMING shaper is attempted for a query in this shape. Routing straight to the DOM shaper here
    /// (which already materializes nested owned data correctly) instead of attempting streaming and catching
    /// the failure makes the shape succeed under <c>NativeOnly</c> too — mirroring the identical pattern
    /// <c>MongoQueryableMethodTranslatingExpressionVisitor.IsWholeElementRepresentable</c> already applies for
    /// the analogous owned-SelectMany bare-entity-result gap.
    /// </para>
    /// </summary>
    private static bool AllPendingLookupsAreStreamable(MongoQueryExpression mongoQueryExpression)
    {
        var referenceLookups = mongoQueryExpression.GetStreamingReferenceLookups();

        // Every join must be covered by a streamable reference lookup; otherwise a join would be silently
        // dropped on the native path.
        if (mongoQueryExpression.IsJoinQuery
            && referenceLookups.Count < mongoQueryExpression.InnerCollections.Count)
        {
            return false;
        }

        // TargetEntityType, not Navigation.TargetEntityType: IsStreamableReference is satisfied by a
        // navigation-LESS Join hop too (EF-377 — IsReference is `Navigation is not { IsCollection: true }`,
        // which a null Navigation passes), so dereferencing Navigation here would throw for that shape.
        return referenceLookups.All(lookup =>
            lookup.IsStreamableReference
            && !lookup.TargetEntityType.GetNavigations().Any(n => n.IsEagerLoaded));
    }

    private static (MongoQueryContext, MongoExecutableQuery) TranslateQuery<TEntity>(
        QueryContext queryContext,
        IReadOnlyEntityType entityType,
        BsonSerializerFactory bsonSerializerFactory,
        MongoQueryExpression queryExpression,
        ResultCardinality resultCardinality,
        MongoPipelineFactory? nativeFactory,
        bool streaming,
        Func<MongoEFToLinqTranslatingExpressionVisitor, Expression?, Expression> translate,
        Func<MongoQueryContext, IBsonSerializer>? outputSerializerFactory = null)
    {
        var mongoQueryContext = (MongoQueryContext)queryContext;
        var collection = mongoQueryContext.MongoClient.GetCollection<TEntity>(queryExpression.CollectionExpression.CollectionName);

        var transaction = mongoQueryContext.Context.Database.CurrentTransaction as MongoTransaction;

        // Native path: the factory was built once at compile time. Per execution we only bind the
        // current parameter values into the cached template (factory.Build) and run the resulting pipeline via
        // MongoClientWrapper.Execute. The driver-LINQ Query is left as Expression.Empty() — it is never used.
        if (nativeFactory != null)
        {
            // A deferred stage slot (today: $vectorSearch) is CONSTRUCTED during Build, and may both raise a
            // diagnostic and record state the executor reads back afterwards — the zero-results vector-search
            // warning reads VectorQueryProperty/VectorQueryIndexName out of AdditionalState. So the dictionary
            // is created here, filled by Build, and handed to the executable query; a baked-only template
            // simply leaves it empty, exactly as before.
            var additionalState = new Dictionary<string, object>();
            var pipeline = nativeFactory.Build(new MongoNativeBuildContext(
                GetParameterValues(queryContext),
                bsonSerializerFactory,
                mongoQueryContext.QueryLogger,
                additionalState));

            var queryable = transaction == null ? collection.AsQueryable() : collection.AsQueryable(transaction.Session);
            var nativeExecutable = new MongoExecutableQuery(
                Expression.Empty(),
                resultCardinality,
                (IMongoQueryProvider)queryable.Provider,
                collection.CollectionNamespace,
                new(additionalState))
            {
                NativePipeline = pipeline,
                Session = transaction?.Session,
                Streaming = streaming,
                // One-pass streaming only: build the output serializer from the live MongoQueryContext so its
                // Deserialize (which runs during cursor creation — the driver eagerly materializes batch 1)
                // sees this execution's initialized state manager on the tracked path.
                OutputSerializer = outputSerializerFactory?.Invoke(mongoQueryContext)
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
        bool streaming,
        Func<QueryContext, IBsonReader, BsonDeserializationContext, TResult>? onePassShaper)
    {
        // One-pass streaming: build the per-execution output serializer from the compiled reader-based shaper
        // and the live MongoQueryContext. The serializer's Deserialize runs the materializer off the cursor's
        // own IBsonReader (deserialize IS materialize), so TSource == TResult and `shaper` above is identity.
        // Null for the DOM / RawBsonDocument-fallback / driver-LINQ paths.
        Func<MongoQueryContext, IBsonSerializer>? outputSerializerFactory =
            onePassShaper is null
                ? null
                : qc => new MongoEntityMaterializerSerializer<TResult>(onePassShaper, qc);

        var (mongoQueryContext, executableQuery) = TranslateQuery<TEntity>(
            queryContext, entityType, bsonSerializerFactory, queryExpression, resultCardinality,
            nativeFactory, streaming,
            (translator, expression) => translator.Translate(expression, resultCardinality),
            outputSerializerFactory);

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
    //
    // ARCHITECTURE NOTE: this is the coupling that stops the driver-LINQ bridge being retired along with the
    // query fallback — the EF9+ bulk path routes through it unconditionally, so
    // MongoEFToLinqTranslatingExpressionVisitor stays live even once no READ query falls back.
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
                // guardUnstrippableForceUnwindJoin: false. That guard exists because the READ path's
                // whole-entity shaper is pre-built assuming the flat _lookup_<Nav> shape; the bulk path builds
                // no shaper at all (it asks the driver for raw BsonDocuments and keeps only _id), so the
                // guard's premise does not hold and it would be a pure false-positive throw surface for
                // ExecuteUpdate/ExecuteDelete.
                translator.Translate(
                    MongoNonQueryExpression.UnwrapBulkOperator(expression)!, ResultCardinality.Enumerable,
                    guardUnstrippableForceUnwindJoin: false));

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

    private static readonly MethodInfo ExecuteAggregateMethodInfo =
        typeof(MongoShapedQueryCompilingExpressionVisitor)
            .GetTypeInfo()
            .DeclaredMethods
            .Single(m => m.Name == nameof(ExecuteAggregate));

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
