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
/// A query's native-execution disposition at the compile-time gate. A superset of
/// <see cref="Expressions.NativeRoute"/> that also accounts for a lifted-out vector search and the GroupBy+Join
/// wrong-data decline. Streaming-vs-DOM (<c>AllPendingLookupsAreStreamable</c>) is a separate axis.
/// </summary>
internal enum NativeDisposition
{
    /// <summary>Build a native pipeline.</summary>
    Native,

    /// <summary>Fall back to driver-LINQ; throw only under <see cref="MongoQueryMode.NativeOnly"/>.</summary>
    Fallback,

    /// <summary>Throw under both <see cref="MongoQueryMode.Native"/> and <see cref="MongoQueryMode.NativeOnly"/>: the driver-LINQ fallback returns wrong rows.</summary>
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

        // HardDecline: the driver-LINQ fallback would return silently wrong data, so throw under Native/NativeOnly.
        // Explicit DriverLinq remains the user's opt-in.
        var mode = ((MongoQueryCompilationContext)QueryCompilationContext).QueryMode;
        if (ClassifyNativeDisposition(mongoQueryExpression, mode) == NativeDisposition.HardDecline)
        {
            // Wrong-data provenances are independent and can co-occur; list every cause that applies.
            var causes = new List<string>();
            if (mongoQueryExpression.Select.IsGroupByFallbackUnsafe)
            {
                causes.Add(
                    "Query combines GroupBy with a Join, which the native translator does not support and whose "
                    + "driver-LINQ fallback returns incorrect results");
            }
            // Guards against an empty message if a cause is added to IsFallbackWrongData without an arm here.
            Debug.Assert(causes.Count > 0, "HardDecline implies at least one wrong-data cause is set.");
            throw new NativeTranslationNotSupportedException(
                string.Join(". ", causes) + "; use MongoQueryMode.DriverLinq to opt in to the driver-LINQ execution of this query.");
        }

        var rootEntityType = mongoQueryExpression.CollectionExpression.EntityType;
        var projectedEntityType = QueryCompilationContext.Model.FindEntityType(
            shapedQueryExpression.ResultCardinality == ResultCardinality.Enumerable
                ? shapedQueryExpression.Type.TryGetItemType()!
                : shapedQueryExpression.Type);

        // Whole-element owned SelectMany (`from o in q from i in o.Items select i`): the lowerer emits
        // $unwind + $replaceRoot, so the owned element is the root document. Root the shaper at the owned type and
        // force DOM (streaming eligibility would otherwise be evaluated against the outer owner).
        //
        // Must run before the projectedEntityType == null fallback below: FindEntityType returns null for a
        // shared-type owned element, but InnerEntityType comes from the navigation and is correct regardless.
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

        // A grouping that never bound a supported aggregate projection still carries the placeholder
        // GroupByShaperExpression, which VerifyNoClientConstant would reject. Surface the coverage decision first:
        // NativeOnly throws the native exception; Native/DriverLinq fall through to the driver's own error.
        if (mongoQueryExpression.Select.Route == NativeRoute.Fallback
            && shapedQueryExpression.ShaperExpression is GroupByShaperExpression)
        {
            ThrowIfNativeOnlyForbidsFallback(queryMode, "Query groups without a supported aggregate projection");
        }

        VerifyNoClientConstant(shapedQueryExpression.ShaperExpression);

        // Native GroupBy: shape each $group row with the DOM shaper, reading members by top-level alias. Placed
        // before the NativeOnly guard so a representable grouping succeeds natively.
        if (queryMode != MongoQueryMode.DriverLinq
            && mongoQueryExpression.Select.Route == NativeRoute.GroupBy)
        {
            return CompileShapedQuery(shapedQueryExpression, mongoQueryExpression, rootEntityType,
                (bsonDoc, behavior) => new MongoProjectionBindingRemovingExpressionVisitor(
                    rootEntityType, mongoQueryExpression, bsonDoc, behavior),
                allowStreaming: false);
        }

        // Native projection pushdown: shape the $project output with the DOM shaper, reading fields by alias.
        // Placed before the NativeOnly guard so a representable projection succeeds natively.
        if (queryMode != MongoQueryMode.DriverLinq
            && mongoQueryExpression.Select.Route == NativeRoute.Projection)
        {
            // Read the strip tier here, on the only branch that builds the alias-addressed projection shaper, so
            // it stays disjoint from the mixed path's StripPushedDownSelect below (never stripped twice).
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

        // Native scalar aggregate (Count/Sum/Min/Max/Average/Any/All): read the single "v" field and apply the
        // empty-input contract. Placed before the NativeOnly guard. The predicate may still contain a shape the
        // lowerer can't emit (e.g. parameterized string.Contains), so lowering is tried here and can fall back.
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

            // Fell back (Native only; NativeOnly already threw): the predicate/selector couldn't be lowered.
            // Continue to the driver-LINQ push-down aggregate path.
        }

        // Native whole-entity ctor-wrap (`x => new SomeDto(x)`): stays on NativeRoute.WholeEntity with no $project,
        // and arrives here only because the outer CLR type is the DTO. DOM only, like the Projection branch.
        //
        // WholeEntity is Route's fallthrough answer, not an affirmative binding, so the shape is also checked
        // (IsCtorWrappedEntityShaper); any other non-entity shaper falls through to the projected path below
        // rather than being mis-shaped here.
        if (queryMode != MongoQueryMode.DriverLinq
            && mongoQueryExpression.Select.Route == NativeRoute.WholeEntity
            && IsCtorWrappedEntityShaper(
                shapedQueryExpression.ShaperExpression, mongoQueryExpression.Select.HasClientWrappedWholeEntityShaper))
        {
            return CompileShapedQuery(shapedQueryExpression, mongoQueryExpression, rootEntityType,
                (bsonDoc, behavior) => new MongoProjectionBindingRemovingExpressionVisitor(
                    rootEntityType, mongoQueryExpression, bsonDoc, behavior),
                allowStreaming: false);
        }

        // Any other projected query runs through driver-LINQ push-down or the mixed client-side shaper, so it is
        // a coverage failure under NativeOnly.
        ThrowIfNativeOnlyForbidsFallback(queryMode, "Query projects a non-entity result");

        // HasStringSequenceProjectionLeaf restores what ProjectionAnalyzer.CanPushDown can no longer see: the
        // native string-sequence leaf (an Enumerable.* operator applied to a string) erases that call from the
        // shaper, and NativeProjectionBinder runs even under DriverLinq. Without this, CanPushDown flips to true
        // and the driver throws ("unable to determine which serializer to use" / "StringSerializer must implement
        // IBsonArraySerializer"). The mixed shaper re-applies the operator to the materialized value.
        if (!mongoQueryExpression.Select.HasStringSequenceProjectionLeaf
            && ProjectionAnalyzer.CanPushDown(shapedQueryExpression.ShaperExpression))
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
    /// True when <paramref name="shaperExpression"/> is a whole-entity wrap: a ctor-only DTO
    /// <see cref="NewExpression"/> (<c>Members == null</c>) or an opaque client <see cref="MethodCallExpression"/>
    /// with exactly one operand being the entity shaper (possibly wrapped in <see cref="IncludeExpression"/>s).
    /// Narrows the <see cref="NativeRoute.WholeEntity"/> fallthrough route in <see cref="VisitProjectedQuery"/>.
    /// </summary>
    private static bool IsCtorWrappedEntityShaper(Expression shaperExpression, bool hasClientWrappedWholeEntityShaper)
        => shaperExpression switch
        {
            NewExpression { Members: null, Arguments: [var ctorArgument] } => IsEntityShaperOperand(ctorArgument),
            MethodCallExpression methodCall => HasExactlyOneEntityShaperOperand(methodCall),
            // A client-only body (conditional, concat, cast, member chain) wrapping an opaque call on the whole
            // entity; mirrors NativeProjectionBinder.IsClientOnlyWholeEntityExpression on the shaper-replaced tree.
            // Gated on HasClientWrappedWholeEntityShaper so a plain translatable tree with no opaque call keeps
            // falling through to driver-LINQ push-down (otherwise Ternary_Null_Equals_Non_Numeric_First_Part
            // regresses).
            ConditionalExpression or BinaryExpression or UnaryExpression or MemberExpression
                when hasClientWrappedWholeEntityShaper =>
                IsClientOnlyWholeEntityShaperExpression(shaperExpression),
            _ => false
        };

    /// <summary>
    /// Backs <see cref="IsCtorWrappedEntityShaper"/>'s general-expression arm: true when every appearance of the
    /// entity shaper sits behind a client-only combinator, never as a separately server-computed operand.
    /// </summary>
    private static bool IsClientOnlyWholeEntityShaperExpression(Expression node)
    {
        if (IsEntityShaperOperand(node))
        {
            return true;
        }

        return node switch
        {
            ConstantExpression => true,

            // A scalar member read already folded into a projection binding; resolved off the whole raw document
            // (no $project narrowed it).
            ProjectionBindingExpression => true,

            ConditionalExpression conditional =>
                IsClientOnlyWholeEntityShaperExpression(conditional.Test)
                && IsClientOnlyWholeEntityShaperExpression(conditional.IfTrue)
                && IsClientOnlyWholeEntityShaperExpression(conditional.IfFalse),

            BinaryExpression binary =>
                IsClientOnlyWholeEntityShaperExpression(binary.Left)
                && IsClientOnlyWholeEntityShaperExpression(binary.Right),

            UnaryExpression unary => IsClientOnlyWholeEntityShaperExpression(unary.Operand),

            MemberExpression { Expression: not null } member =>
                IsClientOnlyWholeEntityShaperExpression(member.Expression),

            MethodCallExpression methodCall => HasExactlyOneEntityShaperOperand(methodCall),

            _ => false
        };
    }

    private static bool IsEntityShaperOperand(Expression operand)
    {
        var inner = operand;
        while (inner is IncludeExpression include)
        {
            inner = include.EntityExpression;
        }

        return inner is StructuralTypeShaperExpression;
    }

    private static bool HasExactlyOneEntityShaperOperand(MethodCallExpression methodCall)
    {
        var sawEntityShaper = false;

        if (methodCall.Object != null && IsEntityShaperOperand(methodCall.Object))
        {
            sawEntityShaper = true;
        }

        foreach (var argument in methodCall.Arguments)
        {
            if (IsEntityShaperOperand(argument))
            {
                if (sawEntityShaper)
                {
                    return false;
                }

                sawEntityShaper = true;
            }
        }

        return sawEntityShaper;
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
    /// Whether a native-factory decline on the <see cref="NativeRoute.Projection"/> route must strip the
    /// pushed-down <c>Select</c> from the captured chain before handing it to the driver-LINQ bridge.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The shaper is built before native-vs-driver is decided, so a late decline hands the alias-addressed DOM
    /// shaper the driver's own <c>$project</c>. That breaks when the emit side registered an alias override: a
    /// bare selector (driver names it <c>_v</c>) or an <c>OwnsOne</c>-hop array leaf (driver names it by member,
    /// which silently yields empty collections). Stripping yields whole documents, which a
    /// <see cref="ProjectionAliasTier.DocumentPath"/> alias reads correctly; sibling leaves are guaranteed
    /// whole-document-readable (<c>NativeProjectionBinder.IsWholeDocumentReadableLeaf</c>).
    /// </para>
    /// <para>
    /// <see cref="ProjectionAliasTier.Synthetic"/> must not strip: its <c>_v</c> alias relies on the driver's
    /// push-down, and stripping fails with <c>Document element '_v' is missing but required</c>. The tier is
    /// read as data off the override, never by sniffing the alias string. <c>internal</c> for unit tests.
    /// </para>
    /// </remarks>
    internal static bool ShouldStripBareProjectionOnFallback(MongoSelectDefinition select)
        => select.HasDocumentPathAliasOverride;

    /// <summary>
    /// Whether the <see cref="NativeRoute.Projection"/> route staged a whole-entity join inner leaf, whose alias
    /// (the <c>$lookup</c> prefix, e.g. <c>_lookup_Orders</c>) the driver-LINQ bridge never renders.
    /// </summary>
    /// <remarks>
    /// On a late <c>TryBuildNativeFactory</c> decline the bridge renders <c>{"o": "$$ROOT", "r": "$_lookup_Orders"}</c>,
    /// so the inner entity would come back silently null. Stripping the <c>Select</c> and shaping with
    /// <see cref="MongoMixedProjectionBindingRemovingExpressionVisitor"/> (whose <c>ReadsUnprojectedDocuments</c>
    /// resolves the <c>$$ROOT</c> outer leaf) matches the explicit-DriverLinq behavior. Not applied to other
    /// Projection fallbacks: member-name leaves read correctly off the driver's <c>$project</c> and computed
    /// leaves would break. The <see cref="MongoElementRefExpression"/> check keeps this correct without relying
    /// on <c>NativeJoinScopeProjectionBinder</c>'s alias-collision invariant.
    /// </remarks>
    private static bool HasJoinScopeInnerEntityProjectionLeaf(MongoQueryExpression mongoQueryExpression)
        => mongoQueryExpression.Select.JoinScope is { } scope
           && mongoQueryExpression.Select.Projection.Any(
               p => p.Alias == scope.Levels[0].InnerPrefix && p.Expression is MongoElementRefExpression);

    /// <summary>
    /// Like <see cref="HasJoinScopeInnerEntityProjectionLeaf"/>, for a constructed sub-entity leaf
    /// (<c>new { Copy = new Book { Id = b.Id } }</c>): its members exist only nested under the native
    /// <c>$project</c> alias, so a late decline must strip the <c>Select</c> and use the mixed removing visitor,
    /// whose <c>ReadDocumentConstructionMember</c> reads each member at its natural path.
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

        // The native-vs-driver gate is decided once at compile time; per execution the factory is only re-bound
        // (factory.Build), never re-translated. So exactly one shaper is compiled.
        var nativeFactory = TryBuildNativeFactory(mode, mongoQueryExpression);

        // Late-fallback strip: the driver renders the pushed-down Select with its own aliases, which disagree with
        // the alias-addressed shaper built above. See ShouldStripBareProjectionOnFallback (DocumentPath tier) and
        // HasJoinScopeInnerEntityProjectionLeaf (join inner leaf, which also swaps in the mixed removing visitor).
        // Only CapturedExpression is touched, and the arms are disjoint; one branch so it never runs twice.
        var useFallbackBindingRemover = nativeFactory == null && createFallbackBindingRemover != null;
        if (nativeFactory == null && (stripBareProjectionOnFallback || useFallbackBindingRemover))
        {
            mongoQueryExpression.CapturedExpression =
                StripPushedDownSelect(mongoQueryExpression.CapturedExpression);
        }

        // Streaming only when native, the entity shape is streaming-eligible, and every join is a streamable
        // single-level reference lookup. Otherwise the DOM shaper is used.
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
            // One-pass "deserialize IS materialize": the compiled shaper reads one document off the cursor's
            // IBsonReader and becomes the Deserialize body of the pipeline output serializer, so the cursor yields
            // TEntity directly. An un-streamable shape falls back to DOM (still native), except under NativeOnly.
            var readerParameter = Expression.Parameter(typeof(IBsonReader), "__reader");
            var contextParameter = Expression.Parameter(typeof(BsonDeserializationContext), "__context");
            try
            {
                var onePassBody = new MongoStreamingEntityMaterializerRewriter(rootEntityType)
                    .Rewrite(injectedBody, readerParameter, contextParameter);

                var onePassLambda = Expression.Lambda(
                    onePassBody,
                    QueryCompilationContext.QueryContextParameter,
                    readerParameter,
                    contextParameter);
                var compiledOnePassShaper = onePassLambda.Compile(); // Func<QueryContext, IBsonReader, BsonDeserializationContext, TResult>

                // The cursor yields the finished TResult, so the QueryingEnumerable shaper is identity. The output
                // serializer is built per execution (see ExecuteShapedQuery).
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
                // Entity shape isn't streamable; use DOM (still native). Only the rewriter's intended signal is
                // caught; any other exception surfaces.
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

        // Native DOM and driver-LINQ both shape full BsonDocuments with this lambda; nativeFactory selects which.
        return BuildExecuteCall(
            typeof(BsonDocument),
            Expression.Constant(compiledShaper),
            projectedType,
            streaming: false);

        // onePassShaper is non-null only on the one-pass streaming path; ExecuteShapedQuery builds the
        // per-execution output serializer from it.
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
    /// The compile-time native-vs-driver gate. Returns a <see cref="MongoPipelineFactory"/> for native execution,
    /// or <see langword="null"/> for driver-LINQ (always under DriverLinq; on a lowering failure under Native).
    /// Under NativeOnly a lowering failure throws.
    /// </summary>
    private static MongoPipelineFactory? TryBuildNativeFactory(
        MongoQueryMode mode,
        MongoQueryExpression mongoQueryExpression)
    {
        if (mode == MongoQueryMode.DriverLinq)
        {
            return null;
        }

        // Scalar aggregates are native but built by TryBuildAggregateFactory, so decline them here. An unbound
        // vector search classifies as Fallback, so the lowerer is never reached without a $vectorSearch slot.
        if (ClassifyNativeDisposition(mongoQueryExpression, mode) != NativeDisposition.Native
            || mongoQueryExpression.Select.Route == NativeRoute.ScalarAggregate)
        {
            ThrowIfNativeOnlyForbidsFallback(mode, "Query is not natively representable");
            return null;
        }

        return TryBuildPipeline(mongoQueryExpression, mode);
    }

    // Native pipeline for Route == ScalarAggregate. The aggregate shape was confirmed at bind time, but its composed
    // predicate may still fail to lower (e.g. parameterized string.Contains) and fall back.
    private static MongoPipelineFactory? TryBuildAggregateFactory(
        MongoQueryMode mode,
        MongoQueryExpression mongoQueryExpression)
        => TryBuildPipeline(mongoQueryExpression, mode);

    private static MongoPipelineFactory? TryBuildPipeline(MongoQueryExpression mongoQueryExpression, MongoQueryMode mode)
    {
        try
        {
            var stages = new MongoSelectLowerer().Lower(mongoQueryExpression);
            return MongoPipelineFactory.Create(stages, new MongoQueryLanguageRenderer());
        }
        catch (NativeTranslationNotSupportedException) when (mode != MongoQueryMode.NativeOnly)
        {
            // A representable query whose stages can't be emitted: fall back. Under NativeOnly this rethrows.
            return null;
        }
    }

    // Executes a native scalar-aggregate pipeline and applies the empty-input contract, returning one element for
    // EF Core's base Single reduction. Returns SingleValueEnumerable (sync + async) because EF picks Single or
    // SingleAsync by IsAsync and SingleAsync needs an IAsyncEnumerable<TResult>.
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

                // Any/All are presence-only: Any's $match holds the predicate; All's holds the negated predicate,
                // so a surviving row means All is false.
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

    // One element, both IEnumerable<T> and IAsyncEnumerable<T>, so either Single or SingleAsync can reduce it.
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

    // Reads the terminal stage's "v" field and coerces it to TResult (e.g. long for LongCount, double for Average).
    private static TResult DeserializeScalar<TResult>(BsonDocument doc)
    {
        var bsonValue = doc[BsonValueSerializer.ScalarField];
        var mapped = BsonTypeMapper.MapToDotNetValue(bsonValue);

        // A non-empty aggregate can yield BSON null (e.g. Min over all-null values); Convert.ChangeType throws for
        // null, so return null for a nullable TResult. Non-nullable TResult can't reach this.
        if (mapped is null)
        {
            return default!;
        }

        var targetType = Nullable.GetUnderlyingType(typeof(TResult)) ?? typeof(TResult);
        var converted = targetType.IsInstanceOfType(mapped) ? mapped : ConvertNumericNarrowing(mapped, targetType);

        return (TResult)converted!;
    }

    // $sum widens past int32/int64 server-side, and Convert.ChangeType's checked narrowing would throw. BCL Sum's
    // per-element overflow semantics can't be reproduced anyway (accepted divergence), so narrow unchecked and
    // return the wrapped/rounded value rather than throwing.
    private static object ConvertNumericNarrowing(object mapped, Type targetType) => mapped switch
    {
        long l when targetType == typeof(int) => unchecked((int)l),
        double d when targetType == typeof(int) => unchecked((int)d),
        double d when targetType == typeof(long) => unchecked((long)d),
        _ => Convert.ChangeType(mapped, targetType)
    };

    // Under NativeOnly a query the native path can't handle is a compile-time coverage failure. No-op otherwise.
    private static void ThrowIfNativeOnlyForbidsFallback(MongoQueryMode mode, string reason)
    {
        if (mode == MongoQueryMode.NativeOnly)
        {
            throw new NativeTranslationNotSupportedException(
                $"{reason} and MongoQueryMode.NativeOnly forbids the driver-LINQ fallback.");
        }
    }

    /// <summary>
    /// The single source of truth for the is-native gate decision. Pure, so it is unit-testable.
    /// </summary>
    /// <param name="route">The representability route (<see cref="MongoSelectDefinition.Route"/>).</param>
    /// <param name="isFallbackWrongData">Whether the driver-LINQ fallback returns silently wrong rows. See
    /// <see cref="MongoSelectDefinition.IsFallbackWrongData"/>.</param>
    /// <param name="hasUnboundVectorSearch">
    /// Whether the captured chain has a lifted-out <c>VectorSearch</c> not bound into
    /// <see cref="MongoSelectDefinition.VectorSearch"/>. An unbound one also forces <paramref name="route"/> to
    /// <see cref="NativeRoute.Fallback"/>, so a native route with no <c>$vectorSearch</c> stage (right row count,
    /// wrong order, no exception) is unreachable.
    /// </param>
    /// <param name="mode">The active <see cref="MongoQueryMode"/>.</param>
    internal static NativeDisposition ClassifyNativeDisposition(
        NativeRoute route,
        bool isFallbackWrongData,
        bool hasUnboundVectorSearch,
        MongoQueryMode mode)
    {
        // Checked first: wrong-data fallback hard-declines except under explicit DriverLinq.
        if (mode != MongoQueryMode.DriverLinq && isFallbackWrongData)
        {
            return NativeDisposition.HardDecline;
        }

        // Graceful fallback; the driver path still carries the VectorSearch in the captured chain.
        if (route == NativeRoute.Fallback || hasUnboundVectorSearch)
        {
            return NativeDisposition.Fallback;
        }

        return NativeDisposition.Native;
    }

    /// <summary>
    /// Gathers the is-native signals from <paramref name="q"/> and classifies. Vector search presence is read
    /// from the captured chain (the call is lifted out before the Select is built) and paired with the bound slot.
    /// </summary>
    private static NativeDisposition ClassifyNativeDisposition(MongoQueryExpression q, MongoQueryMode mode)
        => ClassifyNativeDisposition(
            q.Select.Route,
            q.Select.IsFallbackWrongData,
            q.CapturedExpression.ContainsVectorSearch() && q.Select.VectorSearch is null,
            mode);

    /// <summary>
    /// Streaming gate (not native-vs-driver): whether every join is a single-level reference lookup the streaming
    /// reader can read from a root-level <c>_lookup_&lt;Nav&gt;</c> field. Collection includes, filtered includes
    /// and nested lookups stay on DOM; do not admit collection lookups here.
    /// <para>
    /// Also not streamable when the looked-up target has an eager-loaded (e.g. owned) navigation of its own:
    /// <c>MongoStreamingEntityMaterializerRewriter</c> would throw, whereas DOM handles it and lets the shape
    /// succeed under NativeOnly.
    /// </para>
    /// </summary>
    private static bool AllPendingLookupsAreStreamable(MongoQueryExpression mongoQueryExpression)
    {
        var referenceLookups = mongoQueryExpression.GetStreamingReferenceLookups();

        // Every join must be covered by a streamable reference lookup, or it would be silently dropped.
        if (mongoQueryExpression.IsJoinQuery
            && referenceLookups.Count < mongoQueryExpression.InnerCollections.Count)
        {
            return false;
        }

        // TargetEntityType, not Navigation.TargetEntityType: a navigation-less Join hop is also a streamable
        // reference, and its Navigation is null.
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

        // Native path: bind this execution's parameters into the compile-time template and run it.
        if (nativeFactory != null)
        {
            // A deferred stage slot ($vectorSearch) is built during Build and records state (e.g. for the
            // zero-results warning) that the executor reads back from AdditionalState.
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
                // Built from the live context because Deserialize runs during cursor creation (batch 1) and must
                // see this execution's state manager.
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

    // EF8/EF9 expose ParameterValues; EF10 renamed it to Parameters.
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

    // TSource is RawBsonDocument when streaming, otherwise BsonDocument. The native-vs-driver decision was made at
    // compile time, so exactly one shaper matching TSource was compiled.
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
        // One-pass streaming only (null otherwise): Deserialize runs the materializer off the cursor's reader.
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
    // This coupling keeps the driver-LINQ bridge alive even once no read query falls back.
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
                // guardUnstrippableForceUnwindJoin: false — the guard protects a pre-built read-path shaper; the
                // bulk path builds none, so it would only be a false-positive throw.
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
