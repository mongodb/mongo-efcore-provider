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
            MongoBulkPlanBuilder.EnsureKeyOrThrow(entityType, nonQueryExpression);
        }

        // The plan closes over compile-time constants and is embedded in the compiled query; its delegates translate at
        // runtime and MongoBulkOperationExecutor (Storage) runs the writes.
        var plan = MongoBulkPlanBuilder.Create(entityType, _bsonSerializerFactory, nonQueryExpression);

        var executor = QueryCompilationContext.IsAsync
            ? MongoBulkExecuteAsyncMethodInfo
            : MongoBulkExecuteMethodInfo;

        return Expression.Call(
            null,
            executor,
            QueryCompilationContext.QueryContextParameter,
            Expression.Constant(plan));
    }

#endif

    /// <inheritdoc/>
    protected override Expression VisitShapedQuery(ShapedQueryExpression shapedQueryExpression)
    {
        if (shapedQueryExpression.QueryExpression is not MongoQueryExpression mongoQueryExpression)
        {
            throw new NotSupportedException($" Unhandled expression node type '{nameof(shapedQueryExpression.QueryExpression)}'");
        }

        // A non-deterministic call (Random.Next, Guid.NewGuid) would be evaluated once and baked into the pipeline: refuse in every mode.
        NonDeterministicCalls.ThrowIfAnyNonDeterministic(mongoQueryExpression.CapturedExpression);

        // HardDecline: the driver-LINQ fallback returns silently wrong data, so throw under Native/NativeOnly (explicit DriverLinq opts in).
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
            Debug.Assert(causes.Count > 0, "HardDecline implies at least one wrong-data cause is set.");
            throw new NativeTranslationNotSupportedException(
                string.Join(". ", causes) + "; use MongoQueryMode.DriverLinq to opt in to the driver-LINQ execution of this query.");
        }

        var rootEntityType = mongoQueryExpression.CollectionExpression.EntityType;
        var projectedEntityType = QueryCompilationContext.Model.FindEntityType(
            shapedQueryExpression.ResultCardinality == ResultCardinality.Enumerable
                ? shapedQueryExpression.Type.TryGetItemType()!
                : shapedQueryExpression.Type);

        // Whole-element owned SelectMany: the lowerer emits $unwind + $replaceRoot, so root the shaper at the owned type and force DOM.
        // Must precede the projectedEntityType == null fallback: FindEntityType returns null for a shared-type owned element, but
        // InnerEntityType (from the navigation) is correct.
        if (mongoQueryExpression.Select.UnwindSource is { WholeElement: true } wholeElementUnwind)
        {
            var elementType = wholeElementUnwind.InnerEntityType;
            return CompileShapedQuery(shapedQueryExpression, mongoQueryExpression, elementType,
                CreateDomBindingRemover(elementType, mongoQueryExpression),
                allowStreaming: false);
        }

        if (projectedEntityType == null)
        {
            return VisitProjectedQuery(shapedQueryExpression, rootEntityType, mongoQueryExpression);
        }

        // Entity path. A bare join INNER leaf (Select(ti => ti.Inner)) is read from the whole document's _lookup_<Nav> field, which only the
        // native pipeline returns (the driver pushes the Select down as `_v`), so strip that Select on fallback.
        return CompileShapedQuery(shapedQueryExpression, mongoQueryExpression, rootEntityType,
            CreateDomBindingRemover(rootEntityType, mongoQueryExpression),
            stripBareProjectionOnFallback: mongoQueryExpression.Select.HasBareJoinInnerEntityLeaf);
    }

    private MethodCallExpression VisitProjectedQuery(
        ShapedQueryExpression shapedQueryExpression,
        IEntityType rootEntityType,
        MongoQueryExpression mongoQueryExpression)
    {
        var queryMode = ((MongoQueryCompilationContext)QueryCompilationContext).QueryMode;

        // A grouping with no bound aggregate projection still carries the placeholder GroupByShaperExpression, which VerifyNoClientConstant
        // would reject: surface the coverage decision first (NativeOnly throws the native exception; other modes get the driver's error).
        if (mongoQueryExpression.Select.Route == NativeRoute.Fallback
            && shapedQueryExpression.ShaperExpression is GroupByShaperExpression)
        {
            ThrowIfNativeOnlyForbidsFallback(queryMode, "Query groups without a supported aggregate projection");
            ThrowIfDriverLinqCaseMapsGroupedResult(mongoQueryExpression.CapturedExpression);
        }

        VerifyNoClientConstant(shapedQueryExpression.ShaperExpression);

        // The native GroupBy, projection and scalar-aggregate branches below sit before the NativeOnly guard so representable shapes succeed natively.
        if (queryMode != MongoQueryMode.DriverLinq
            && mongoQueryExpression.Select.Route == NativeRoute.GroupBy)
        {
            return CompileShapedQuery(shapedQueryExpression, mongoQueryExpression, rootEntityType,
                CreateDomBindingRemover(rootEntityType, mongoQueryExpression),
                allowStreaming: false);
        }

        if (queryMode != MongoQueryMode.DriverLinq
            && mongoQueryExpression.Select.Route == NativeRoute.Projection)
        {
            // Read the strip tier here, on the only branch that builds the alias-addressed projection shaper, so
            // it stays disjoint from the mixed path's StripPushedDownSelect below (never stripped twice).
            return CompileShapedQuery(shapedQueryExpression, mongoQueryExpression, rootEntityType,
                CreateDomBindingRemover(rootEntityType, mongoQueryExpression),
                allowStreaming: false,
                stripBareProjectionOnFallback: ShouldStripBareProjectionOnFallback(mongoQueryExpression.Select),
                createFallbackBindingRemover: HasJoinScopeInnerEntityProjectionLeaf(mongoQueryExpression)
                                              || HasDocumentConstructionProjectionLeaf(mongoQueryExpression)
                                              // Its shaper reads the ternary's test and branch reads by aliases the
                                              // driver's push-down of the same Select doesn't produce; the mixed
                                              // reader evaluates both off whole documents.
                                              || mongoQueryExpression.Select.HasClientConditionalProjectionLeaf
                    ? (bsonDoc, behavior) => new MongoMixedProjectionBindingRemovingExpressionVisitor(
                        rootEntityType, mongoQueryExpression, bsonDoc, behavior)
                    : null);
        }

        // Native scalar aggregate. The predicate may still hold a shape the lowerer can't emit (e.g. parameterized string.Contains), so lowering can fall back.
        if (queryMode != MongoQueryMode.DriverLinq
            && mongoQueryExpression.Select.Route == NativeRoute.ScalarAggregate)
        {
            var aggregateFactory = TryBuildPipeline(mongoQueryExpression, queryMode);
            if (aggregateFactory != null)
            {
                var cardinality = mongoQueryExpression.Select.Cardinality!;

                // Min/Max return a stored value, so a bare-property operand reads back through that property's serializer (enum, DateOnly, char,
                // Local-kind DateTime); a Local-kind DateTime reached through a ternary/coalesce reads back with that kind too.
                var minMaxOperand =
                    cardinality is { Aggregate: MongoAggregateOperator.Min or MongoAggregateOperator.Max, Selector: { } operand }
                        ? operand
                        : null;
                var readProperty = minMaxOperand == null
                    ? null
                    : NativeAggregateReadBack.FindOperandProperty(mongoQueryExpression.Select, minMaxOperand);
                var dateTimeKindSource = readProperty == null && minMaxOperand != null
                    ? NativeDateTimeKindReadBack.FindForAggregateOperand(mongoQueryExpression.Select, minMaxOperand)
                    : null;
                var scalarSerializer = readProperty != null
                    ? BsonSerializerFactory.CreateTypeSerializer(readProperty)
                    : dateTimeKindSource != null
                        ? BsonSerializerFactory.CreateTypeSerializer(cardinality.ResultType, dateTimeKindSource)
                        : null;
                return Expression.Call(null,
                    ExecuteAggregateMethodInfo.MakeGenericMethod(rootEntityType.ClrType, cardinality.ResultType),
                    QueryCompilationContext.QueryContextParameter,
                    Expression.Constant(rootEntityType),
                    Expression.Constant(_bsonSerializerFactory),
                    Expression.Constant(mongoQueryExpression),
                    Expression.Constant(_contextType),
                    Expression.Constant(_threadSafetyChecksEnabled),
                    Expression.Constant(cardinality),
                    Expression.Constant(aggregateFactory),
                    Expression.Constant(
                        scalarSerializer,
                    typeof(IBsonSerializer)));
            }

            // Fell back (Native only): the predicate/selector couldn't be lowered; continue to the driver-LINQ aggregate path.
        }

        // Native whole-entity ctor-wrap (`x => new SomeDto(x)`): stays on NativeRoute.WholeEntity with no $project; DOM only.
        // WholeEntity is Route's fallthrough answer, not an affirmative binding, so the shape is also checked (IsCtorWrappedEntityShaper).
        if (queryMode != MongoQueryMode.DriverLinq
            && mongoQueryExpression.Select.Route == NativeRoute.WholeEntity
            && IsCtorWrappedEntityShaper(
                shapedQueryExpression.ShaperExpression, mongoQueryExpression.Select.HasClientWrappedWholeEntityShaper))
        {
            return CompileShapedQuery(shapedQueryExpression, mongoQueryExpression, rootEntityType,
                CreateDomBindingRemover(rootEntityType, mongoQueryExpression),
                allowStreaming: false);
        }

        // Any other projected query runs through driver-LINQ push-down or the mixed client-side shaper, so it is
        // a coverage failure under NativeOnly.
        ThrowIfNativeOnlyForbidsFallback(queryMode, "Query projects a non-entity result");
        ThrowIfDriverLinqCaseMapsGroupedResult(mongoQueryExpression.CapturedExpression);

        // HasStringSequenceProjectionLeaf restores what ProjectionAnalyzer.CanPushDown can no longer see: the native string-sequence leaf
        // erases the Enumerable.* call from the shaper, and without this the driver throws ("unable to determine which serializer to use").
        // The mixed shaper re-applies the operator. A projected ToLower/ToUpper (driver's are ASCII-only) also takes the mixed shaper, but only
        // when the Select can be stripped (not with a Distinct after it). An index-based positional-ctor shaper stays on push-down: its
        // synthetic `_ctorArg<N>` aliases aren't in a whole document.
        if (!mongoQueryExpression.Select.HasStringSequenceProjectionLeaf
            && ProjectionAnalyzer.CanPushDown(shapedQueryExpression.ShaperExpression)
            && !(ProjectionAnalyzer.HasCaseMappingProjectedValue(shapedQueryExpression.ShaperExpression)
                 && !mongoQueryExpression.Select.HasPositionalCtorProjectionShaper
                 && !ReferenceEquals(
                     StripPushedDownSelect(mongoQueryExpression.CapturedExpression), mongoQueryExpression.CapturedExpression)))
        {
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

        // Mixed path: projection contains entity references LINQ V3 can't handle. Strip the Select so the driver returns whole documents and
        // the client-side shaper projects. The Select may be outermost or under a no-arg cardinality terminator, which is rebound to the
        // un-projected source type. An operator composed over the Select (Distinct, set op) survives the strip and yields projected documents
        // lacking an owned-reference leaf's owner key; detected by the remaining chain no longer yielding entity documents.
        mongoQueryExpression.CapturedExpression = StripPushedDownSelect(mongoQueryExpression.CapturedExpression);
        var pushedDownSelectRetained = mongoQueryExpression.CapturedExpression is { } strippedCaptured
                                       && rootEntityType.Model.FindEntityType(
                                           GetQueryableElementType(strippedCaptured.Type)) == null;

        return CompileShapedQuery(shapedQueryExpression, mongoQueryExpression, rootEntityType,
            (bsonDoc, behavior) => new MongoMixedProjectionBindingRemovingExpressionVisitor(
                rootEntityType, mongoQueryExpression, bsonDoc, behavior, pushedDownSelectRetained));
    }

    /// <summary>
    /// True when <paramref name="shaperExpression"/> is a whole-entity wrap: a ctor-only DTO
    /// <see cref="NewExpression"/> (<c>Members == null</c>) over the entity shaper, an opaque client
    /// <see cref="MethodCallExpression"/> with exactly one operand being the entity shaper (possibly wrapped in
    /// <see cref="IncludeExpression"/>s), or a client-only body/construction around those. Narrows the
    /// <see cref="NativeRoute.WholeEntity"/> fallthrough route in <see cref="VisitProjectedQuery"/>.
    /// </summary>
    /// <remarks>
    /// The method-call and general arms share the emit side's shape rule (<see cref="NativeClientWholeEntityShape"/>). The general arm needs
    /// HasClientWrappedWholeEntityShaper so a plain translatable tree keeps falling through to push-down
    /// (Ternary_Null_Equals_Non_Numeric_First_Part).
    /// </remarks>
    private static bool IsCtorWrappedEntityShaper(Expression shaperExpression, bool hasClientWrappedWholeEntityShaper)
        => shaperExpression switch
        {
            NewExpression { Members: null, Arguments: [var ctorArgument] } => IsEntityShaperOperand(ctorArgument),
            MethodCallExpression methodCall => NativeClientWholeEntityShape.HasSoleWholeEntityOperand(
                methodCall, ClassifyClientWholeEntityShaperOperand),
            ConditionalExpression or BinaryExpression or UnaryExpression or MemberExpression
                or NewArrayExpression or ListInitExpression or MemberInitExpression or NewExpression
                when hasClientWrappedWholeEntityShaper =>
                NativeClientWholeEntityShape.IsClientOnlyTree(shaperExpression, ClassifyClientWholeEntityShaperOperand),
            _ => false
        };

    /// <summary>
    /// The read-side classifier for <see cref="NativeClientWholeEntityShape"/>: over the bound shaper the selector
    /// parameter has become the entity shaper. A <see cref="ProjectionBindingExpression"/> reads the row, so it
    /// classifies as <see cref="ClientWholeEntityOperand.Walk"/> and the walk declines it: resolved by its projection
    /// member off the whole raw document (no $project narrowed it), it would read null.
    /// </summary>
    internal static ClientWholeEntityOperand ClassifyClientWholeEntityShaperOperand(Expression node)
        => IsEntityShaperOperand(node)
            ? ClientWholeEntityOperand.WholeEntity
            : RowReadFinder.ReadsRow(node)
                ? ClientWholeEntityOperand.Walk
                : ClientWholeEntityOperand.EntityFree;

    private static bool IsEntityShaperOperand(Expression operand)
        => operand.UnwrapIncludes() is StructuralTypeShaperExpression;

    // Finds anything in a shaper subtree that reads the row: an entity shaper, a projection binding, or any other
    // extension node (conservatively, since it can't be proven row-free).
    private sealed class RowReadFinder : System.Linq.Expressions.ExpressionVisitor
    {
        private bool _found;

        internal static bool ReadsRow(Expression node)
        {
            var finder = new RowReadFinder();
            finder.Visit(node);
            return finder._found;
        }

        public override Expression? Visit(Expression? node)
        {
            if (_found || node == null)
            {
                return node;
            }

            if (node.NodeType == ExpressionType.Extension)
            {
                _found = true;
                return node;
            }

            return base.Visit(node);
        }
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

    // The element type of an IQueryable<T> chain, or the type itself for a cardinality terminal (First, Single, ...).
    private static Type GetQueryableElementType(Type type)
        => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IQueryable<>)
            ? type.GetGenericArguments()[0]
            : type.GetInterfaces()
                  .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IQueryable<>))
                  ?.GetGenericArguments()[0]
              ?? type;

    /// <summary>
    /// Throws when the captured chain, about to run on driver-LINQ, is a grouped query holding a case mapping
    /// (<c>ToUpper</c>/<c>ToLower</c>, incl. Invariant) in the <c>GroupBy</c>'s key/element/result selector, in an
    /// operator composed after it, or in any lambda of an ungrouped source it groups or combines with (either side of a
    /// set op or join). The driver would compute it with <c>$toUpper</c>/<c>$toLower</c>, which are ASCII-only and map
    /// null to <c>""</c>, so the answer would be silently wrong.
    /// </summary>
    /// <remarks>Only reached when the query doesn't run natively; the native <c>$group</c> re-applies the call client-side.</remarks>
    private static void ThrowIfDriverLinqCaseMapsGroupedResult(Expression? captured)
    {
        if (Scan(captured).CaseMapped)
        {
            throw new InvalidOperationException(
                "A grouped query uses ToUpper/ToLower (or ToUpperInvariant/ToLowerInvariant), which this query could only "
                + "evaluate on the server with $toUpper/$toLower. Those are ASCII-only and map null to an empty string, so "
                + "the results would not match .NET. Apply the case mapping after the query (e.g. after AsEnumerable()), "
                + "or use a GroupBy projection the native query translator supports.");
        }

        static (bool Grouped, bool CaseMapped) Scan(Expression? expression)
        {
            if (expression is not MethodCallExpression call)
            {
                return (false, false);
            }

            var grouped = false;
            var caseMapped = false;
            var sources = new List<(Expression Argument, bool Grouped)>();
            foreach (var argument in call.Arguments)
            {
                var (sourceGrouped, sourceCaseMapped) = Scan(argument);
                grouped |= sourceGrouped;
                caseMapped |= sourceCaseMapped;
                sources.Add((argument, sourceGrouped));
            }

            grouped |= call.Method.DeclaringType == typeof(Queryable) && call.Method.Name == nameof(Queryable.GroupBy);

            caseMapped |= grouped
                          && (HasCaseMappingLambda(call)
                              || sources.Any(source => !source.Grouped && HasCaseMappingInSource(source.Argument)));

            return (grouped, caseMapped);
        }

        static bool HasCaseMappingLambda(MethodCallExpression call)
            => call.Arguments.Select(a => a.UnwrapQuote()).OfType<LambdaExpression>()
                .Any(l => NativeCaseMapping.ContainsCaseMappingCall(l.Body));

        // A case mapping in any lambda anywhere in an ungrouped source, through every source argument (both sides of a set op or join).
        static bool HasCaseMappingInSource(Expression source)
            => source is MethodCallExpression sourceCall
               && (HasCaseMappingLambda(sourceCall) || sourceCall.Arguments.Any(HasCaseMappingInSource));
    }

    /// <summary>
    /// Whether a native-factory decline on the <see cref="NativeRoute.Projection"/> route must strip the
    /// pushed-down <c>Select</c> from the captured chain before handing it to the driver-LINQ bridge.
    /// </summary>
    /// <remarks>
    /// The shaper is built before native-vs-driver is decided, so a late decline hands the alias-addressed DOM shaper the driver's own
    /// <c>$project</c>. That breaks when the emit side registered an alias override (a bare selector the driver names <c>_v</c>, or an
    /// <c>OwnsOne</c>-hop array leaf, which silently yields empty collections); stripping yields whole documents, which a
    /// <see cref="ProjectionAliasTier.DocumentPath"/> alias reads correctly. <see cref="ProjectionAliasTier.Synthetic"/> must not strip:
    /// its <c>_v</c> alias relies on the driver's push-down ("Document element '_v' is missing but required"). Read the tier as data,
    /// never by sniffing the alias. <c>internal</c> for unit tests.
    /// </remarks>
    internal static bool ShouldStripBareProjectionOnFallback(MongoSelectDefinition select)
        => select.HasDocumentPathAliasOverride;

    /// <summary>
    /// Whether the <see cref="NativeRoute.Projection"/> route staged a whole-entity join inner leaf, whose alias
    /// (the <c>$lookup</c> prefix, e.g. <c>_lookup_Orders</c>) the driver-LINQ bridge never renders.
    /// </summary>
    /// <remarks>
    /// On a late <c>TryBuildNativeFactory</c> decline the bridge renders <c>{"o": "$$ROOT", "r": "$_lookup_Orders"}</c>, so the inner entity would
    /// come back silently null. Stripping the <c>Select</c> and shaping with <see cref="MongoMixedProjectionBindingRemovingExpressionVisitor"/>
    /// matches explicit DriverLinq. Not applied to other Projection fallbacks (member-name leaves read correctly; computed leaves would break).
    /// The <see cref="MongoElementRefExpression"/> check avoids relying on <c>NativeJoinScopeProjectionBinder</c>'s alias-collision invariant.
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

    private static Func<ParameterExpression, QueryTrackingBehavior, System.Linq.Expressions.ExpressionVisitor>
        CreateDomBindingRemover(IEntityType entityType, MongoQueryExpression mongoQueryExpression)
        => (bsonDoc, behavior) => new MongoProjectionBindingRemovingExpressionVisitor(
            entityType, mongoQueryExpression, bsonDoc, behavior);

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

        // The gate is decided once at compile time; per execution the factory is only re-bound (factory.Build), so exactly one shaper is compiled.
        var nativeFactory = TryBuildNativeFactory(mode, mongoQueryExpression);
        if (nativeFactory == null)
        {
            ThrowIfDriverLinqCaseMapsGroupedResult(mongoQueryExpression.CapturedExpression);
        }

        // Late-fallback strip: the driver renders the pushed-down Select with its own aliases, which disagree with the alias-addressed shaper
        // above (see ShouldStripBareProjectionOnFallback, HasJoinScopeInnerEntityProjectionLeaf). Arms are disjoint; one branch so it never runs twice.
        var useFallbackBindingRemover = nativeFactory == null && createFallbackBindingRemover != null;
        if (nativeFactory == null && (stripBareProjectionOnFallback || useFallbackBindingRemover))
        {
            mongoQueryExpression.CapturedExpression =
                StripPushedDownSelect(mongoQueryExpression.CapturedExpression);
        }

        var streaming = allowStreaming
            && nativeFactory != null
            && shapedQueryExpression.ResultCardinality == ResultCardinality.Enumerable
            && StreamingEligibility.IsEligible(rootEntityType)
            // A bare join-INNER entity leaf (`Clients.Select(c => c.Company)`) materializes the joined entity, not the root, so
            // the root-typed streaming rewriter has no locals for it: DOM shaper.
            && !mongoQueryExpression.Select.HasBareJoinInnerEntityLeaf
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
            // One-pass "deserialize IS materialize": the compiled shaper reads one document off the cursor's IBsonReader and becomes the
            // output serializer's Deserialize body, so the cursor yields TEntity directly. Un-streamable shapes fall back to DOM (except NativeOnly).
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

        return BuildExecuteCall(
            typeof(BsonDocument),
            Expression.Constant(compiledShaper),
            projectedType,
            streaming: false);

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

        // Scalar aggregates are native but built by VisitProjectedQuery's ScalarAggregate arm, so decline them here. An unbound
        // vector search classifies as Fallback, so the lowerer is never reached without a $vectorSearch slot.
        if (ClassifyNativeDisposition(mongoQueryExpression, mode) != NativeDisposition.Native
            || mongoQueryExpression.Select.Route == NativeRoute.ScalarAggregate)
        {
            ThrowIfNativeOnlyForbidsFallback(mode, "Query is not natively representable");
            return null;
        }

        return TryBuildPipeline(mongoQueryExpression, mode);
    }

    private static MongoPipelineFactory? TryBuildPipeline(MongoQueryExpression mongoQueryExpression, MongoQueryMode mode)
    {
        try
        {
            var stages = new MongoSelectLowerer().Lower(mongoQueryExpression);
            return MongoPipelineFactory.Create(stages, new MongoQueryLanguageRenderer());
        }
        catch (NativeTranslationNotSupportedException) when (mode != MongoQueryMode.NativeOnly)
        {
            return null;
        }
    }

    // Runs a native scalar-aggregate pipeline with the empty-input contract. Returns SingleValueEnumerable (sync + async) because EF picks
    // Single or SingleAsync by IsAsync and SingleAsync needs an IAsyncEnumerable<TResult>.
    private static SingleValueEnumerable<TResult> ExecuteAggregate<TEntity, TResult>(
        QueryContext queryContext,
        IReadOnlyEntityType entityType,
        BsonSerializerFactory bsonSerializerFactory,
        MongoQueryExpression queryExpression,
        Type contextType,
        bool threadSafetyChecksEnabled,
        MongoCardinality cardinality,
        MongoPipelineFactory nativeFactory,
        IBsonSerializer? kindAwareScalarSerializer)
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

                // A non-nullable Min/Max reduced {_v: operand} documents: a MISSING winner reads default and a null one
                // throws, as on driver-LINQ; a value reads like a bare reduction's.
                var wrapped = NativeAggregateReadBack.ReducesWrappedValue(cardinality);
                var wrappedValue = wrapped ? NativeAggregateReadBack.ReadWrappedValue(doc[BsonValueSerializer.ScalarField], typeof(TResult)) : null;

                // Any/All are presence-only: Any's $match holds the predicate; All's holds the negated predicate,
                // so a surviving row means All is false.
                if (cardinality.PresenceOnly)
                {
                    value = (TResult)cardinality.PresentValue!;
                }
                else if (wrapped && wrappedValue is null)
                {
                    value = default!;
                }
                else
                {
                    var scalarDoc = wrapped ? new BsonDocument(BsonValueSerializer.ScalarField, wrappedValue) : doc;
                    value = kindAwareScalarSerializer != null
                        ? DeserializeKindAwareScalar<TResult>(scalarDoc, kindAwareScalarSerializer)
                        : DeserializeScalar<TResult>(scalarDoc);
                }
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

    // Reads a Min/Max "v" field holding a Local-kind DateTime property's stored value through the TResult serializer
    // with that property's kind (BsonTypeMapper would return Kind=Utc), built at compile time. BSON null is Min/Max over
    // all-null values (see ReadNullScalar).
    private static TResult DeserializeKindAwareScalar<TResult>(BsonDocument doc, IBsonSerializer serializer)
    {
        var bsonValue = doc[BsonValueSerializer.ScalarField];
        if (bsonValue.IsBsonNull)
        {
            return ReadNullScalar<TResult>();
        }

        var serializationInfo = new BsonSerializationInfo(BsonValueSerializer.ScalarField, serializer, serializer.ValueType);
        var value = serializationInfo.DeserializeValue(bsonValue);

        // The property's serializer yields its own CLR type (e.g. int? for a Max over an int? property); TResult may
        // differ only by nullability.
        return value is TResult typed
            ? typed
            : (TResult)Convert.ChangeType(value, Nullable.GetUnderlyingType(typeof(TResult)) ?? typeof(TResult));
    }

    // A non-empty aggregate's BSON null: null for a nullable/reference TResult. For a non-nullable value type it is an Average with no numeric
    // row (malformed document), which driver-LINQ rejected (FormatException): throw EF's "Nullable object must have a value.", not read 0.
    private static TResult ReadNullScalar<TResult>()
        => typeof(TResult).IsValueType && Nullable.GetUnderlyingType(typeof(TResult)) is null
            ? throw new InvalidOperationException("Nullable object must have a value.")
            : default!;

    private static TResult DeserializeScalar<TResult>(BsonDocument doc)
    {
        var bsonValue = doc[BsonValueSerializer.ScalarField];
        var mapped = BsonTypeMapper.MapToDotNetValue(bsonValue);

        // A non-empty aggregate can yield BSON null (e.g. Min over all-null values); Convert.ChangeType throws for null.
        if (mapped is null)
        {
            return ReadNullScalar<TResult>();
        }

        var targetType = Nullable.GetUnderlyingType(typeof(TResult)) ?? typeof(TResult);
        var converted = targetType.IsInstanceOfType(mapped) ? mapped : ConvertNumericNarrowing(mapped, targetType);

        return (TResult)converted!;
    }

    // $sum widens past int32/int64 server-side. LINQ's Sum is checked, so a total that doesn't fit the CLR type throws OverflowException
    // (divergence: a total overflowing only in an intermediate partial sum would have thrown in BCL Sum).
    private static object ConvertNumericNarrowing(object mapped, Type targetType) => mapped switch
    {
        long l when targetType == typeof(int) => checked((int)l),
        double d when targetType == typeof(int) => checked((int)d),
        double d when targetType == typeof(long) => checked((long)d),
        _ => Convert.ChangeType(mapped, targetType)
    };

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

    internal static (MongoQueryContext, MongoExecutableQuery) TranslateQuery<TEntity>(
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

    // TSource is RawBsonDocument when streaming, otherwise BsonDocument; the one compiled shaper matches it.
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
        // The driver's Join/GroupJoin translator needs the inner operand to be a bare IMongoQueryable over a collection (a ConstantExpression), so
        // .As(serializer) can't be used as for the outer source. Wrap the collection so its DocumentSerializer returns EF's entity serializer,
        // keeping EF's element-name / discriminator / BsonRepresentation mappings on the inner side.
        var innerCollection = new SerializerOverrideCollection<TInner>(
            mongoQueryContext.MongoClient.GetCollection<TInner>(collectionName),
            (IBsonSerializer<TInner>)bsonSerializerFactory.GetEntitySerializer(innerEntityType));
        var innerQueryable = transaction == null
            ? innerCollection.AsQueryable()
            : innerCollection.AsQueryable(transaction.Session);
        return innerQueryable.Expression;
    }
}
