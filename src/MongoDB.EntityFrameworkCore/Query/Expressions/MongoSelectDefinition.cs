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
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Linq.Expressions;

namespace MongoDB.EntityFrameworkCore.Query.Expressions;

/// <summary>
/// The native-translation logical query IR (filter / sort / paging / projection) for a single collection.
/// Populated by <c>NativeSlotPopulator</c> / <c>NativeProjectionBinder</c>, read by the compile-time gate and the
/// lowerer. Dialect-neutral: holds <see cref="MongoExpression"/> nodes, never BSON.
/// </summary>
/// <remarks>
/// A plain data-holder, not an <see cref="System.Linq.Expressions.Expression"/> (hence <c>Definition</c>).
/// Cross-collection <c>$lookup</c> state stays on <see cref="MongoQueryExpression"/> because it is entangled with
/// the driver-LINQ fallback shaper.
/// </remarks>
internal sealed class MongoSelectDefinition
{
    private readonly List<MongoProjection> _projections = [];

    private readonly List<MongoSelectOp> _pipelineOps = [];

    /// <summary>
    /// The ordered filter/sort/page operations, emitted verbatim by the lowerer. Arrival order is emission
    /// order, which is how non-canonical Skip/Take is represented.
    /// </summary>
    public IReadOnlyList<MongoSelectOp> PipelineOps => _pipelineOps;

    // Ops recorded after a set op attaches, so they filter/sort/page the combined result rather than source1's
    // pre-set-op rows.
    private readonly List<MongoSelectOp> _trailingOps = [];

    /// <summary>
    /// The ordered filter/sort/page operations recorded after a set op was attached; emitted after the set-op
    /// stage.
    /// </summary>
    public IReadOnlyList<MongoSelectOp> TrailingOps => _trailingOps;

    // Ops recorded once JoinInnerAccessConfirmed flips, incl. the confirming Where/OrderBy and any later reducer
    // $limit: the Where decides "first", so it must run before the reducer's $limit. Exclusive with SetOperation.
    private readonly List<MongoSelectOp> _postJoinOps = [];

    /// <summary>
    /// The ordered filter/sort/page operations recorded after a <c>Where</c> predicate or <c>OrderBy</c>/
    /// <c>ThenBy</c> key reaching a single-level join's Inner side confirmed that join's <c>$lookup</c> (with no
    /// confirming <c>Select</c>). Emitted immediately after that <c>$lookup</c>/<c>$unwind</c> block.
    /// </summary>
    public IReadOnlyList<MongoSelectOp> PostJoinOps => _postJoinOps;

    // Populated once, by DeferPipelineOpsPastConfirmedJoin, when EF Core hoisted Skip/Take (and everything before
    // it) ahead of a join's pending result selector and the join is not in the pre-lookup-safe (left-outer,
    // non-collection navigation) set.
    private readonly List<MongoSelectOp> _postLookupPagingOps = [];

    /// <summary>
    /// The ops moved out of <see cref="PipelineOps"/> by <see cref="DeferPipelineOpsPastConfirmedJoin"/>. Emitted
    /// immediately after the confirmed join's <c>$lookup</c>/<c>$unwind</c> block(s), before any <c>$project</c>.
    /// </summary>
    public IReadOnlyList<MongoSelectOp> PostLookupPagingOps => _postLookupPagingOps;

    /// <summary>
    /// Moves the whole current <see cref="PipelineOps"/> snapshot, in order, into <see cref="PostLookupPagingOps"/>.
    /// </summary>
    internal void DeferPipelineOpsPastConfirmedJoin()
    {
        _postLookupPagingOps.AddRange(_pipelineOps);
        _pipelineOps.Clear();
    }

    // Sort/page ops composed after a projected Distinct (not a genuine GroupBy), whose keys resolved against the
    // Distinct's own flattened output alias rather than the root entity (which could collide with a
    // differently-sourced projection member of the same name).
    private readonly List<MongoSelectOp> _postGroupOps = [];

    /// <summary>
    /// The ordered sort/page operations recorded after a degenerate/keyed <c>$group</c>; emitted immediately
    /// after the flattening <c>$project</c> that follows it.
    /// </summary>
    public IReadOnlyList<MongoSelectOp> PostGroupOps => _postGroupOps;

    private bool _joinInnerAccessConfirmed;

    /// <summary>
    /// <see langword="true"/> once a <c>Where</c> or <c>OrderBy</c>/<c>ThenBy</c> expression reaching a
    /// single-level join's Inner side has been resolved (via <c>NativeJoinScopeTranslator</c>). Routes
    /// <see cref="ActiveOps"/> to <see cref="PostJoinOps"/> from this point on.
    /// </summary>
    internal bool JoinInnerAccessConfirmed => _joinInnerAccessConfirmed;

    internal void MarkJoinInnerAccessConfirmed() => _joinInnerAccessConfirmed = true;

    private bool _referenceCollectionCountPredicateConfirmed;

    /// <summary>
    /// <see langword="true"/> once <see cref="NativeTranslation.NativeReferenceCollectionCountPredicateBinder"/>
    /// registered its own <c>$lookup</c> for a reference-collection-nav <c>Count</c> comparison in a
    /// <c>Where</c>. Routed like <see cref="JoinInnerAccessConfirmed"/> but kept separate so <see cref="Route"/>
    /// can decline this shape alone, and so <c>IsSingleEligibleNativeJoinScope</c>'s join check isn't tripped by
    /// a query with no join.
    /// </summary>
    /// <remarks>
    /// The <c>$lookup</c> is registered eagerly, before it is known whether a set op, projected
    /// <c>Distinct</c>/keyed <c>GroupBy</c>, or <c>Join</c> will attach. Those lowerer branches don't flush
    /// <see cref="PostJoinOps"/> where this <c>$match</c> needs it, so <see cref="Route"/> declines the whole
    /// combination to driver-LINQ instead of teaching every branch a new emission point.
    /// </remarks>
    internal bool ReferenceCollectionCountPredicateConfirmed => _referenceCollectionCountPredicateConfirmed;

    internal void MarkReferenceCollectionCountPredicateConfirmed() => _referenceCollectionCountPredicateConfirmed = true;

    /// <summary>
    /// Moves a trailing <see cref="MongoSortOp"/> (earlier Outer-only keys of the same <c>OrderBy</c>/<c>ThenBy</c>
    /// chain) from <see cref="PipelineOps"/> into <see cref="PostJoinOps"/> before a <c>ThenBy</c> key reaching the
    /// join's Inner side extends it. No-op when the tail isn't a sort.
    /// <para>
    /// A later <c>$sort</c> re-orders the whole input and uses arrival order only as a tie-break, so splitting one
    /// logical ordering across two <c>$sort</c> stages would silently make the post-lookup key primary
    /// (pinned by <c>NorthwindMiscellaneousQueryMongoTest.OrderBy_object_type_server_evals</c>).
    /// </para>
    /// </summary>
    internal void DeferTrailingSortPastConfirmedJoin()
    {
        if (_pipelineOps.Count > 0 && _pipelineOps[^1] is MongoSortOp trailingSort)
        {
            _pipelineOps.RemoveAt(_pipelineOps.Count - 1);
            _postJoinOps.Add(trailingSort);
        }
    }

    /// <summary>
    /// The op list the merge methods currently target: <see cref="TrailingOps"/> once a set op is attached;
    /// <see cref="PostJoinOps"/> once a join's Inner access (or a reference-collection Count predicate) is
    /// confirmed; <see cref="PostGroupOps"/> once a projected Distinct's or a keyed GroupBy's <c>$group</c> is
    /// finalized; otherwise <see cref="PipelineOps"/>. A finalized grouping (keyed GroupBy or projected Distinct)
    /// takes precedence over a confirmed join's Inner access, because the lowerer always emits the join's
    /// <c>$lookup</c>s (and <see cref="PostJoinOps"/>) before <c>$group</c>: a post-group op routed to
    /// <see cref="PostJoinOps"/> would run over ungrouped rows lacking the group's aliases (a GroupBy over a join
    /// scope after an inner-side <c>Where</c>).
    /// </summary>
    /// <remarks>
    /// The two <see cref="Grouping"/> branches are kept exclusive (<c>IsDistinct &amp;&amp; !IsGroupBy</c> /
    /// <c>IsGroupBy &amp;&amp; !IsDistinct</c>): a GroupBy nested on a projected Distinct (both true, see
    /// <see cref="PriorGrouping"/>) must fall through to <see cref="PipelineOps"/>; <c>MongoSelectLowerer</c>
    /// places its post-group ops structurally.
    /// </remarks>
    private List<MongoSelectOp> ActiveOps
        => SetOperation != null ? _trailingOps
            : IsDistinct && !IsGroupBy && Grouping != null ? _postGroupOps
            : IsGroupBy && !IsDistinct && Grouping != null ? _postGroupOps
            : _joinInnerAccessConfirmed || _referenceCollectionCountPredicateConfirmed ? _postJoinOps
            : _pipelineOps;

    /// <summary>
    /// ANDs <paramref name="conjunct"/> into the tail <see cref="MongoMatchOp"/> if there is one; otherwise appends
    /// a new <see cref="MongoMatchOp"/> so a predicate after a sort or paging stays sequential. Targets
    /// <see cref="ActiveOps"/>.
    /// </summary>
    public void AddPredicateConjunct(MongoExpression conjunct)
    {
        var ops = ActiveOps;
        if (ops.Count > 0 && ops[^1] is MongoMatchOp match)
            ops[^1] = new MongoMatchOp(
                new MongoBinaryExpression(MongoBinaryOperator.AndAlso, match.Predicate, conjunct));
        else
            ops.Add(new MongoMatchOp(conjunct));
    }

    /// <summary>
    /// OrderBy: replaces a tail <see cref="MongoSortOp"/> (<c>OrderBy(a).OrderBy(b)</c> keeps only b), otherwise
    /// appends a new sort. Targets <see cref="ActiveOps"/>.
    /// </summary>
    public void StartOrReplaceSort(MongoOrdering first)
    {
        var ops = ActiveOps;
        if (ops.Count > 0 && ops[^1] is MongoSortOp)
            ops[^1] = new MongoSortOp([first]);
        else
            ops.Add(new MongoSortOp([first]));
    }

    /// <summary>ThenBy: extends the tail sort. If the preceding key failed to translate (the select is already
    /// Fallback), the tail isn't a sort, so start a fresh one rather than throw. Targets <see cref="ActiveOps"/>.
    ///
    /// A bare-field key already present in the sort is dropped: it is a no-op and would render a duplicate field
    /// name in <c>$sort</c> (CSHARP-5690; mirrors <c>ElideRedundantOrderings</c> on the driver-LINQ path).
    /// Computed keys are never elided.
    /// </summary>
    public void AppendThenBy(MongoOrdering next)
    {
        var ops = ActiveOps;
        if (ops.Count > 0 && ops[^1] is MongoSortOp sort)
        {
            if (next.KeySelector is MongoFieldExpression nextField
                && sort.Orderings.Any(o => o.KeySelector is MongoFieldExpression f && f.ElementName == nextField.ElementName))
            {
                return;
            }

            ops[^1] = new MongoSortOp([.. sort.Orderings, next]);
        }
        else
        {
            ops.Add(new MongoSortOp([next]));
        }
    }

    public void AppendSkip(MongoExpression count) => ActiveOps.Add(new MongoSkipOp(count));

    public void AppendLimit(MongoExpression count) => ActiveOps.Add(new MongoLimitOp(count));

    /// <summary>
    /// Whole-entity <c>Distinct()</c> (no preceding <c>Select</c>). A projected Distinct uses <see cref="Grouping"/>.
    /// </summary>
    public void AppendDistinct() => ActiveOps.Add(new MongoDistinctOp());

    /// <summary>
    /// A whole-entity <c>Distinct()</c> over a bare join leaf (<see cref="BareJoinEntityLeaf"/>): appended to
    /// <see cref="PostJoinOps"/> so it dedups the joined rows after the <c>$lookup</c>/<c>$unwind</c>, not the
    /// un-joined outer rows before it.
    /// </summary>
    internal void AppendPostJoinDistinct(MongoDistinctOp op) => _postJoinOps.Add(op);

    // HasPaging/HasOrdering/HasLimit scan _pipelineOps only: they gate a pre-terminal GroupBy, which is unreachable
    // after a set op, so they must not see _trailingOps.
    /// <summary><see langword="true"/> when any $skip or $limit op is present.</summary>
    internal bool HasPaging => _pipelineOps.Exists(o => o is MongoSkipOp or MongoLimitOp);

    /// <summary>
    /// <see langword="true"/> when <see cref="PostJoinOps"/> holds an op that does not commute with a later
    /// row-changing join: <c>$skip</c>, <c>$limit</c> or a dedup (<see cref="MongoDistinctOp"/>). PostJoinOps lower
    /// after EVERY join's <c>$lookup</c>/<c>$unwind</c>, including a join recorded after the op, so such an op would
    /// page or dedup that later join's rows instead. <c>$match</c>/<c>$sort</c> commute. See
    /// <c>TranslateJoinCore</c>.
    /// </summary>
    internal bool HasNonCommutingPostJoinOp
        => _postJoinOps.Exists(o => o is MongoSkipOp or MongoLimitOp or MongoDistinctOp);

    /// <summary>
    /// <see langword="true"/> when any $sort op is present. Currently unused: a pre-<c>GroupBy</c> sort is either
    /// a no-op or defines the row set for a following Skip/Take, so it is not a reason to decline.
    /// </summary>
    internal bool HasOrdering => _pipelineOps.Exists(o => o is MongoSortOp);

    /// <summary>
    /// <see langword="true"/> when any $limit op is present. Currently unused: consecutive <c>$limit</c> stages
    /// narrow monotonically, so an existing limit is not a reason for a reducer to decline.
    /// </summary>
    internal bool HasLimit => _pipelineOps.Exists(o => o is MongoLimitOp);

    /// <summary>
    /// Flips every ordering in the tail <see cref="MongoSortOp"/> of <see cref="ActiveOps"/>, so Reverse/Last can
    /// reuse the ascending-sort machinery (MQL has no "reverse" stage). Returns <see langword="false"/> without
    /// mutating when the tail isn't a sort; Last then uses <see cref="MongoCardinality.UnorderedLastRow"/>
    /// instead, while Reverse declines.
    /// </summary>
    internal bool TryFlipTrailingSortDirection()
    {
        var ops = ActiveOps;
        if (ops.Count == 0 || ops[^1] is not MongoSortOp sort)
            return false;

        ops[^1] = new MongoSortOp([.. sort.Orderings.Select(o => o with { Ascending = !o.Ascending })]);
        return true;
    }

    /// <summary>
    /// The output fields of a server-side <c>$project</c> stage, in order. Empty means whole-entity results.
    /// </summary>
    public IReadOnlyList<MongoProjection> Projection => _projections;

    public void AddProjection(MongoProjection projection)
        => _projections.Add(projection);

    /// <summary>
    /// Clears the projection list, so <c>NativeGroupByBinder.TryBindDistinctFromProjection</c> can replace it with
    /// the flattening projection that reads values back out of the degenerate-<c>$group</c> <c>_id</c>.
    /// </summary>
    internal void ClearProjections() => _projections.Clear();

    // Projection-alias overrides: the single source for every site that would otherwise derive a $project alias
    // (and the name the DOM shaper reads by) from ProjectionMember.Last?.Name. A map rather than one string so a
    // named member can also decouple alias from document path ("Notes" -> "Home.Notes").

    /// <summary>
    /// The override-table key for a bare selector body. A sentinel rather than <see langword="null"/> because
    /// <see cref="Dictionary{TKey,TValue}"/> rejects null keys; the leading space means it can't collide with a
    /// real member name.
    /// </summary>
    internal const string BareProjectionMemberKey = " bare";

    private Dictionary<string, (string Alias, ProjectionAliasTier Tier)>? _projectionAliasOverrides;

    /// <summary>
    /// Overrides the <c>$project</c> output element name (and the name the DOM shaper reads by) for a projection
    /// member. Written only by <c>NativeProjectionBinder</c>, in the same commit block as the matching
    /// <see cref="AddProjection"/>.
    /// </summary>
    /// <param name="memberName">
    /// The projection member's own name, or <see cref="BareProjectionMemberKey"/> for a bare selector body.
    /// </param>
    /// <param name="alias">The output element name to emit and to read back by.</param>
    /// <param name="tier">
    /// Whether <paramref name="alias"/> is a root-relative document path or a synthetic name. Carried as data
    /// because the late-fallback strip is tier-conditional and must not re-derive it from the alias string.
    /// </param>
    /// <remarks>
    /// Write-once via <see cref="Dictionary{TKey,TValue}.Add"/>: two aliases for one member would let the emitted
    /// <c>$project</c> key and the shaper's read name disagree, silently yielding null/empty values. Throwing is
    /// preferable.
    /// </remarks>
    internal void AddProjectionAliasOverride(string memberName, string alias, ProjectionAliasTier tier)
        => (_projectionAliasOverrides ??= new Dictionary<string, (string, ProjectionAliasTier)>()).Add(
            memberName, (alias, tier));

    /// <summary>
    /// Looks up the alias override for <paramref name="memberName"/>; <see langword="null"/> (a bare selector body)
    /// maps to <see cref="BareProjectionMemberKey"/>, so callers can pass <c>projectionMember.Last?.Name</c> directly.
    /// </summary>
    internal bool TryGetProjectionAlias(string? memberName, [NotNullWhen(true)] out string? alias)
    {
        if (_projectionAliasOverrides != null
            && _projectionAliasOverrides.TryGetValue(memberName ?? BareProjectionMemberKey, out var entry))
        {
            alias = entry.Alias;
            return true;
        }

        alias = null;
        return false;
    }

    /// <summary>
    /// Looks up the <see cref="MongoDocumentConstructionExpression"/> an emit-side recognizer already staged into
    /// <see cref="Projection"/> for <paramref name="memberName"/>, if any.
    /// </summary>
    /// <param name="memberName">
    /// The projection member's own name, not the emitted alias; any alias override is applied here.
    /// </param>
    /// <param name="expectedType">
    /// The CLR type the read side expects; matched against the node's original expression type.
    /// </param>
    /// <param name="construction">The staged node, when one is found.</param>
    /// <remarks>
    /// <para>
    /// The single lookup shared by the plain-root leaf (<c>TryGetNativeDocumentConstructionLeaf</c>), the
    /// join-scope nested leaf (<c>BindResultMember</c>) and the GroupBy nested leaf (<c>BindGroupMember</c>), so
    /// none can use looser admission rules and read a node back under a member it doesn't describe. Admits
    /// <see cref="NativeRoute.GroupBy"/> too, because a <see cref="Grouping"/>-bearing select routes as GroupBy
    /// even though its nested construction is staged in <see cref="Projection"/>.
    /// </para>
    /// <para>
    /// Reads the emit side's committed result rather than re-deriving admissibility, so the bind side can never
    /// admit a shape the emit side declined.
    /// </para>
    /// </remarks>
    internal bool TryGetDocumentConstructionProjection(
        string? memberName, Type expectedType, [NotNullWhen(true)] out MongoDocumentConstructionExpression? construction)
    {
        construction = null;

        if (Route is not (NativeRoute.Projection or NativeRoute.GroupBy) || memberName is null)
        {
            return false;
        }

        var alias = TryGetProjectionAlias(memberName, out var overriddenAlias) ? overriddenAlias : memberName;

        foreach (var projection in Projection)
        {
            if (projection.Alias == alias
                && projection.Expression is MongoDocumentConstructionExpression candidate
                && candidate.OriginalExpression.Type == expectedType)
            {
                construction = candidate;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Rewrites every recorded <see cref="MongoProjection.Source"/> (built over the selector parameter) into the
    /// form the projection binding visitor sees (the parameter replaced by the source shaper).
    /// </summary>
    internal void RebaseProjectionSources(Func<Expression, Expression> rebase)
    {
        for (var i = 0; i < _projections.Count; i++)
        {
            if (_projections[i].Source is { } source)
            {
                _projections[i] = _projections[i] with { Source = rebase(source) };
            }
        }
    }

    /// <summary>
    /// Looks up the leaf the emit side staged into <see cref="Projection"/> for <paramref name="memberName"/>
    /// (<see langword="null"/> for a bare selector body), applying any alias override.
    /// </summary>
    internal bool TryGetProjectionLeaf(string? memberName, [NotNullWhen(true)] out MongoProjection? leaf)
    {
        leaf = null;

        if (Route != NativeRoute.Projection)
        {
            return false;
        }

        var alias = TryGetProjectionAlias(memberName, out var overriddenAlias) ? overriddenAlias : memberName;
        if (alias is null)
        {
            return false;
        }

        foreach (var projection in Projection)
        {
            if (projection.Alias == alias)
            {
                leaf = projection;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The staged output under <paramref name="alias"/> whose <see cref="MongoProjection.ThrowsOnNull"/> is set, in
    /// <see cref="Projection"/> or in a projected set-op operand's own projection: the combined rows are all read
    /// through this select's shaper, so an operand's null-propagated leaf must throw on null there too.
    /// </summary>
    /// <remarks>
    /// The read side's only source for the flag (<c>MongoProjectionBindingRemovingExpressionVisitor</c>); the emit side
    /// set it from the same <c>ClassifyNonNullableValueRead</c> call that admitted the leaf.
    /// </remarks>
    internal MongoProjection? FindThrowOnNullProjection(string alias)
    {
        foreach (var projection in _projections)
        {
            if (projection.ThrowsOnNull && projection.Alias == alias)
            {
                return projection;
            }
        }

        foreach (var link in _setOperations)
        {
            if (link.OperandsProjected && link.OperandSelect.FindThrowOnNullProjection(alias) is { } operandProjection)
            {
                return operandProjection;
            }
        }

        return null;
    }

    /// <summary>
    /// <see langword="true"/> when a bare selector body populated <see cref="Projection"/>.
    /// </summary>
    internal bool IsBareProjection
        => _projectionAliasOverrides?.ContainsKey(BareProjectionMemberKey) == true;

    /// <summary>
    /// The tier of the bare-body override, or <see langword="null"/> if none. Used by the late native-factory
    /// fallback: a <see cref="ProjectionAliasTier.DocumentPath"/> alias is readable off a whole document, a
    /// <see cref="ProjectionAliasTier.Synthetic"/> one is not.
    /// </summary>
    internal ProjectionAliasTier? BareProjectionTier
        => _projectionAliasOverrides != null
           && _projectionAliasOverrides.TryGetValue(BareProjectionMemberKey, out var entry)
            ? entry.Tier
            : null;

    /// <summary>
    /// <see langword="true"/> when any override (bare or named) is a <see cref="ProjectionAliasTier.DocumentPath"/>
    /// alias, i.e. the shaper reads some leaf by a name the driver-LINQ bridge would not emit. Read by the late
    /// native-factory-failure strip in <c>MongoShapedQueryCompilingExpressionVisitor</c>.
    /// </summary>
    /// <remarks>
    /// Keyed on tier, not on which member: any document-path alias is readable off a whole document, so stripping
    /// the pushed-down <c>Select</c> is what makes the fallback read hit.
    /// </remarks>
    internal bool HasDocumentPathAliasOverride
    {
        get
        {
            if (_projectionAliasOverrides == null)
            {
                return false;
            }

            foreach (var entry in _projectionAliasOverrides.Values)
            {
                if (entry.Tier == ProjectionAliasTier.DocumentPath)
                {
                    return true;
                }
            }

            return false;
        }
    }

    private MongoCardinality? _cardinality;
    private MongoGrouping? _grouping;

    /// <summary>
    /// The terminal cardinality reducer or scalar aggregate, or <see langword="null"/> for a plain
    /// enumerable result. Set by <c>NativeCardinalityBinder</c>.
    /// </summary>
    public MongoCardinality? Cardinality
    {
        get => _cardinality;
        set
        {
            // Mutually exclusive with Grouping: otherwise Route becomes ScalarAggregate while the lowerer still
            // emits the grouping pipeline, and the scalar shaper reads a nonexistent element.
            Debug.Assert(value == null || _grouping == null,
                "Cardinality and Grouping are mutually exclusive (see the NativeCardinalityBinder post-group guard).");
            _cardinality = value;
        }
    }

    /// <summary>The native grouping ($group), or null when the query does not group.</summary>
    internal MongoGrouping? Grouping
    {
        get => _grouping;
        set
        {
            // Mutually exclusive with Cardinality (see the Cardinality setter for the failure mode).
            Debug.Assert(value == null || _cardinality == null,
                "Grouping and Cardinality are mutually exclusive (see the NativeCardinalityBinder post-group guard).");
            _grouping = value;
        }
    }

    /// <summary>
    /// Installs both <see cref="Grouping"/> and <see cref="Cardinality"/>, the one sanctioned exception to their
    /// mutual exclusion; writes the backing fields so the setter asserts still guard every other path. Used only by
    /// <c>NativeGroupByBinder.TryBindGroupTerminalAggregate</c> for a scalar aggregate directly on a bare
    /// <c>GroupBy(key)</c>, which needs both a <c>$group</c> and the aggregate-terminal stages.
    /// </summary>
    internal void SetGroupedTerminalAggregate(
        MongoGrouping grouping, MongoCardinality cardinality, MongoExpression? postGroupPredicate)
    {
        _grouping = grouping;
        _cardinality = cardinality;
        PostGroupPredicate = postGroupPredicate;
    }

    /// <summary>
    /// The prior stage's own <c>$group</c>, snapshotted by <see cref="SnapshotPriorGroupingForNestedGroupBy"/> when a
    /// <c>GroupBy(key).Select(aggregate)</c> composes on an already-finalized grouping (a projected Distinct or an
    /// earlier GroupBy). <see langword="null"/> otherwise. The lowerer emits it before the outer
    /// <see cref="Grouping"/>; <c>NativeGroupByBinder</c> resolves the outer selectors against its flattened alias
    /// schema (<see cref="NativeTranslation.MongoExpressionTranslator.DistinctAliasScope"/>).
    /// </summary>
    internal MongoGrouping? PriorGrouping { get; private set; }

    /// <summary>
    /// The prior stage's flattening <c>$project</c>, snapshotted with <see cref="PriorGrouping"/>. Emitted right
    /// after its <c>$group</c>, before <see cref="PostGroupOps"/> and the outer grouping.
    /// </summary>
    internal IReadOnlyList<MongoProjection> PriorGroupingProjection { get; private set; } = [];

    /// <summary>
    /// The prior stage's HAVING predicate, snapshotted with <see cref="PriorGrouping"/>; otherwise the outer
    /// GroupBy would reset <see cref="GroupHavingPredicate"/> and silently drop the first grouping's filter.
    /// Emitted after <see cref="PriorGrouping"/>'s <c>$group</c> and before its flattening <c>$project</c>.
    /// </summary>
    internal MongoExpression? PriorGroupHavingPredicate { get; private set; }

    /// <summary>
    /// Moves the current <see cref="Grouping"/>/<see cref="Projection"/>/<see cref="GroupHavingPredicate"/> into the
    /// <c>Prior*</c> slots so a nested <c>GroupBy(key).Select(aggregate)</c> can bind an independent grouping
    /// without overwriting the prior stage's dedup/aggregation/HAVING. Called once by <c>TranslateGroupBy</c>,
    /// before <c>NativeGroupByBinder.TryBindGroupKey</c>, only when a finalized <see cref="Grouping"/> exists.
    /// </summary>
    internal void SnapshotPriorGroupingForNestedGroupBy()
    {
        PriorGrouping = _grouping;

        // Cleared so nothing reads the prior stage's grouping as this stage's: a stale Grouping matched the
        // post-GroupBy.Select aggregate path, which re-emitted the prior $group and dropped the nested key.
        _grouping = null;
        PriorGroupingProjection = [.. _projections];
        PriorGroupHavingPredicate = GroupHavingPredicate;
        GroupHavingPredicate = null;
        ClearProjections();
    }

    /// <summary>
    /// Transient binder state: group key parts parsed by <c>NativeGroupByBinder.TryBindGroupKey</c>, consumed by
    /// <c>TryBindGroupProjection</c> to build <see cref="Grouping"/>. Not part of <see cref="Route"/>.
    /// </summary>
    internal IReadOnlyList<MongoGroupingKeyPart>? PendingGroupKey { get; set; }

    /// <summary>
    /// A <c>$match</c> emitted right after <c>$group</c>, over the group's accumulator outputs, for a scalar
    /// aggregate on a bare <c>GroupBy(key)</c> (e.g. <c>GroupBy(o =&gt; o.CustomerID).Any(g =&gt; g.Count() &gt; 1)</c>).
    /// Set only by <c>NativeGroupByBinder.TryBindGroupTerminalAggregate</c>.
    /// </summary>
    internal MongoExpression? PostGroupPredicate { get; set; }

    /// <summary>
    /// Transient binder state: the group-level accumulator comparison from a <c>Where</c> on a bare
    /// <c>GroupBy(key)</c> (EF normalizes <c>Any(pred)</c>/<c>Count(pred)</c> to <c>Where(pred).Any()</c>). Written by
    /// <c>TryBindGroupWherePredicate</c>; consumed and cleared by <c>TryBindGroupTerminalAggregate</c>.
    /// </summary>
    internal (MongoGroupAccumulator? Accumulator, MongoExpression Comparison)? PendingGroupPredicate { get; set; }

    /// <summary>
    /// Transient binder state: unresolved <c>OrderBy</c>/<c>ThenBy</c> selectors composed on a not-yet-finalized
    /// <c>GroupBy(key)</c>. Can't be resolved when recorded because an aggregate key (<c>g.Count()</c>) needs a
    /// <c>$group</c> accumulator that doesn't exist until the terminal Select. Resolved into
    /// <see cref="GroupOrderOp"/> by <c>TryBindGroupProjection</c>.
    /// </summary>
    internal List<(bool Ascending, LambdaExpression KeySelector)>? PendingGroupOrderings { get; set; }

    /// <summary>
    /// The sort run after <c>$group</c> and before its flattening <c>$project</c>. Separate from
    /// <see cref="PostGroupOps"/> (after the flatten) because it may reference an accumulator the final projection
    /// never flattens (sorting by <c>Count()</c> while projecting only <c>Sum(...)</c>).
    /// </summary>
    internal MongoSortOp? GroupOrderOp { get; set; }

    /// <summary>
    /// Transient binder state: already-translated Skip/Take ops composed on a not-yet-finalized <c>GroupBy(key)</c>,
    /// in arrival order. "Pending" only because they are committed once the terminal Select finalizes the grouping;
    /// moved verbatim into <see cref="GroupPagingOps"/> by <c>TryBindGroupProjection</c>.
    /// </summary>
    internal List<MongoSelectOp>? PendingGroupPaging { get; set; }

    private List<MongoSelectOp> _groupPagingOps = [];

    /// <summary>
    /// The <c>$skip</c>/<c>$limit</c> ops run after <see cref="GroupOrderOp"/> and before the flattening
    /// <c>$project</c>.
    /// </summary>
    public IReadOnlyList<MongoSelectOp> GroupPagingOps
    {
        get => _groupPagingOps;
        internal set => _groupPagingOps = [.. value];
    }

    /// <summary>
    /// The HAVING <c>$match</c>: a <c>Where</c> between <c>GroupBy(key)</c> and the terminal <c>Select</c>, emitted
    /// after <c>$group</c> and before its flattening <c>$project</c>. Distinct from <see cref="PostGroupPredicate"/>
    /// (bare-GroupBy terminal aggregate, no Select) and <see cref="PostGroupOps"/> (filters the flattened output).
    /// </summary>
    internal MongoExpression? GroupHavingPredicate { get; set; }

    /// <summary>
    /// <see langword="true"/> once a <c>GroupBy</c> has been seen, whether or not it bound natively. Used to
    /// hard-decline a grouped source feeding a Join-family operator, whose driver-LINQ fallback silently returns
    /// empty joins. See <see cref="IsGroupByFallbackUnsafe"/>.
    /// </summary>
    internal bool IsGroupBy { get; set; }

    /// <summary>
    /// <see langword="true"/> once a projected <c>Distinct</c> has bound natively. Shares the post-group guards
    /// with <see cref="IsGroupBy"/>, but kept separate because a Distinct-then-Join falls back gracefully
    /// (driver-LINQ joins flat rows correctly), whereas GroupBy-then-Join must hard-decline.
    /// </summary>
    internal bool IsDistinct { get; set; }

    /// <summary>
    /// <see langword="true"/> once a native terminal (GroupBy, projected Distinct, set op, finalized
    /// <see cref="Grouping"/>, or owned-collection unwind) exists. Any later operator must fall back rather than
    /// resolve against the base entity and silently emit a stage before the terminal. <see cref="JoinScope"/>
    /// is metadata only and does not count.
    /// </summary>
    internal bool HasTerminalOperator
        => IsGroupBy || IsDistinct || IsSetOp || Grouping != null || UnwindSources.Count > 0;

    /// <summary>
    /// <see langword="true"/> for a finalized keyed <c>GroupBy(key).Select(...)</c> with no terminal yet, where a
    /// predicate recorded now (<see cref="AddPredicateConjunct"/> into <see cref="PostGroupOps"/>) lowers right
    /// after this grouping's flattening <c>$project</c>, so it can filter the <c>Select</c>'s output aliases.
    /// </summary>
    /// <remarks>
    /// Excludes a GroupBy nested on a prior grouping (<see cref="PriorGrouping"/>: <see cref="PostGroupOps"/> then
    /// lower after the <i>prior</i> flatten, ahead of this <c>$group</c>) and any set op (<see cref="ActiveOps"/> is
    /// then <see cref="TrailingOps"/>, which lower ahead of a <c>$group</c> composed after the combine). In both, the
    /// predicate would run before the aliases it reads exist, and silently answer over the wrong rows.
    /// </remarks>
    internal bool IsFinalizedKeyedGroupOutput
        => IsGroupBy && !IsDistinct && Grouping != null && Cardinality == null
           && PriorGrouping == null && SetOperation == null;

    /// <summary>
    /// <see langword="true"/> when a set op is the only thing done so far (no grouping/distinct/unwind, no
    /// projection yet). Relaxes the catch-all post-terminal guards for operators composed after a set op. Stays
    /// true while a trailing projection is being pushed down, then flips once it is populated so later operators
    /// fall back.
    /// </summary>
    internal bool IsSetOpTerminalOnly
        => IsSetOp && !IsGroupBy && !IsDistinct && Grouping == null && UnwindSources.Count == 0 && Projection.Count == 0;

    /// <summary>
    /// The Atlas <c>$vectorSearch</c> anchoring this query, or <see langword="null"/> when it has none.
    /// </summary>
    /// <remarks>
    /// A dedicated slot rather than a <see cref="PipelineOps"/> entry so being the first stage (a server
    /// requirement) is structural. Not a terminal: later Where/OrderBy/paging record into <see cref="PipelineOps"/>
    /// as usual, and <see cref="Route"/> is unaffected.
    /// </remarks>
    internal MongoVectorSearch? VectorSearch { get; set; }

    // In LINQ source order. More than one entry for a left-nested whole-entity Concat/Union chain
    // (A.Concat(B).Concat(C)); the lowerer emits one $unionWith per entry.
    private readonly List<MongoSetOperation> _setOperations = [];

    /// <summary>
    /// The terminal set operations attached to this select, in LINQ source order; empty for a non-set-op query.
    /// See <c>IsChainableSetOpSelect</c> for what may form a chain.
    /// </summary>
    internal IReadOnlyList<MongoSetOperation> SetOperations => _setOperations;

    /// <summary>
    /// The first set operation, or <see langword="null"/>; the canonical "is this a set-op query?" test. Its
    /// <see cref="MongoSetOperation.OperandsProjected"/> is uniform across a chain (chains are whole-entity only);
    /// anything walking operands must iterate <see cref="SetOperations"/>.
    /// </summary>
    internal MongoSetOperation? SetOperation => _setOperations.Count > 0 ? _setOperations[0] : null;

    /// <summary>
    /// Attaches <paramref name="setOperation"/> as the next link of the set-operation chain, handing it any
    /// ops recorded since the previous link.
    /// </summary>
    /// <remarks>
    /// Ops between links (<c>A.Union(B).OrderBy(..).Take(1).Union(C)</c>) land in <see cref="TrailingOps"/> but
    /// belong before the new link. Moving them keeps <see cref="TrailingOps"/> meaning "after the last link";
    /// otherwise the <c>Take</c> would silently run after the later union.
    /// </remarks>
    internal void AppendSetOperation(MongoSetOperation setOperation)
    {
        if (_trailingOps.Count > 0)
        {
            setOperation.SetPrecedingOps([.. _trailingOps]);
            _trailingOps.Clear();
        }

        _setOperations.Add(setOperation);
    }

    /// <summary>
    /// <see langword="true"/> when a terminal set operation is attached; part of <see cref="HasTerminalOperator"/>.
    /// </summary>
    internal bool IsSetOp { get; set; }

    /// <summary>
    /// <see langword="true"/> when <see cref="Projection"/> contains an owned entity-collection array leaf
    /// (<c>b.Posts</c>) or an owned single-reference entity leaf (<c>b.Address</c>). Both share this flag.
    /// </summary>
    /// <remarks>
    /// Read only by <c>IsPlainProjectedSelect</c>, which declines such a projection as a set-op operand: either
    /// leaf forces the owner key into the projected document, and value-based dedup over it would silently become
    /// identity-based. A trailing projection after a whole-entity set op is unaffected.
    /// </remarks>
    internal bool HasArrayProjectionLeaf { get; set; }

    /// <summary>
    /// <see langword="true"/> when <see cref="Projection"/> contains a string-to-char-sequence leaf
    /// (<c>e.City.AsEnumerable()</c>/<c>.ToList()</c>/<c>.ToArray()</c>). It pushes down only the string field and
    /// materializes in the shaper, so it is correct only when this provider's shaper reads it back.
    /// </summary>
    /// <remarks>
    /// The driver can't translate <c>Enumerable.*</c> over a string, and registering the leaf as a projection
    /// member erases the call from the shaper, hiding it from <c>ProjectionAnalyzer</c>'s
    /// <c>UntranslatableProjectionFinder</c>. This flag carries that answer instead: <c>VisitProjectedQuery</c>
    /// reads it beside <c>ProjectionAnalyzer.CanPushDown</c> and routes non-native execution to the client/mixed
    /// shaper.
    /// </remarks>
    internal bool HasStringSequenceProjectionLeaf { get; set; }

    /// <summary>
    /// <see langword="true"/> when a <see cref="Projection"/> leaf is the receiver of a client-reapplied
    /// <c>ToLower</c>/<c>ToUpper</c> (<c>NativeProjectionBinder.IsCaseMappingCall</c>): the projected document holds
    /// the raw receiver, not the value the query sees.
    /// </summary>
    /// <remarks>
    /// So a later operator that reads projected values (Distinct, a set op, Where, OrderBy, an aggregate, a
    /// predicate terminal) must decline natively; see
    /// <c>MongoQueryableMethodTranslatingExpressionVisitor.IsProjectedValueFreeOperator</c>.
    /// </remarks>
    internal bool HasClientCaseMappingProjectionLeaf { get; set; }

    /// <summary>
    /// <see langword="true"/> when the committed projection has a row-independent leaf the shaper evaluates
    /// client-side and that is never projected (<c>NativeProjectionBinder.IsRowIndependentLeaf</c>: a closed
    /// <c>new</c>, <c>new { }</c>, a captured value the <c>$literal</c> path can't render, a constructor argument of a
    /// <c>MemberInit</c>). Set only in <c>NativeProjectionBinder</c>'s commit block. A <c>$literal</c>-rendered
    /// constant or parameter is server-side and never sets it.
    /// </summary>
    /// <remarks>
    /// The server never sees the value, so no server operator may read it: a set op (the shaper reused for every
    /// combined row would show source1's value on source2's rows, and dedup would ignore the leaf) declines in
    /// <c>IsPlainProjectedSelect</c>/<c>IsPlainDistinctSelect</c>, and every other operator that reads projected
    /// values declines in <c>MongoQueryableMethodTranslatingExpressionVisitor.VisitMethodCall</c>. <c>Distinct</c>
    /// stays native: the value is the same for every row of one execution, so deduplicating by the projected leaves
    /// alone is exact. When every leaf is client-evaluated the binder projects a constant sentinel
    /// (<c>NativeProjectionBinder.ClientEvaluatedSentinelAlias</c>) so <see cref="Route"/> stays
    /// <see cref="NativeRoute.Projection"/>.
    /// </remarks>
    internal bool HasClientEvaluatedProjectionLeaf { get; set; }

    /// <summary>
    /// <see langword="true"/> when <see cref="Route"/> is <see cref="NativeRoute.WholeEntity"/> only because the
    /// selector wraps the entity client-side (<c>x =&gt; new Dto(x)</c> or <c>x =&gt; context.ClientMethod(x)</c>):
    /// nothing is projected, but the shaper's result is not the entity itself.
    /// </summary>
    /// <remarks>
    /// Read by <c>IsPlainWholeEntitySelect</c> to keep such an operand out of a native set-op combine: deduping
    /// raw documents is wrong for a wrapped result, and an unordered <c>$unionWith</c> + <c>FirstOrDefault()</c>
    /// can surface a different row than the in-memory baseline (<c>Client_eval_Union_FirstOrDefault</c>).
    /// </remarks>
    internal bool HasClientWrappedWholeEntityShaper { get; set; }

    /// <summary>
    /// <see langword="true"/> when <c>NativeProjectionBinder</c> committed a multi-argument positional-ctor DTO
    /// projection (<c>x =&gt; new Item(x.CustomerID, x.City)</c>; <c>NewExpression.Members</c> is null).
    /// </summary>
    /// <remarks>
    /// <c>TranslateSelect</c> then builds an index-based shaper (as the GroupBy/SelectMany ctor-DTO selectors do)
    /// because <c>MongoProjectionBindingExpressionVisitor.VisitNew</c> only enters a projection member per
    /// argument when <c>Members</c> is non-null, so every argument would otherwise share one member.
    /// </remarks>
    internal bool HasPositionalCtorProjectionShaper { get; set; }

    /// <summary>The native join scope chain recorded by <c>TranslateJoinCore</c>, or <see
    /// langword="null"/> if this select has no eligible native join.</summary>
    internal MongoJoinScope? JoinScope { get; set; }

    /// <summary>
    /// Set by <c>TranslateGroupBy</c> when the grouped source is a native join scope that is safe to group over
    /// (<c>TryGetGroupByJoinScope</c>). <see cref="NativeTranslation.NativeGroupByBinder"/> then resolves key parts, accumulator operands
    /// and element predicates over the join's <c>TransparentIdentifier</c> element through this scope, and confirms the
    /// join chain only once the whole grouping has bound. <see langword="null"/> means root-entity resolution (the join,
    /// if any, stays an unconfirmed candidate and the query falls back).
    /// </summary>
    internal MongoJoinScope? GroupByJoinScope { get; set; }

    private bool _hasConfirmedJoinLookup;

    /// <summary>
    /// <see langword="true"/> once the two-sided join (chain) described by <see cref="JoinScope"/> has been
    /// confirmed and its <c>$lookup</c>(s) registered, by <c>NativeJoinScopeProjectionBinder.ConfirmEntireChain</c>
    /// (reached from <c>TranslateSelect</c>'s two arms or from <c>NativeCardinalityBinder.TryBindAggregate</c> for a
    /// selector-less aggregate). Never unset.
    /// </summary>
    /// <remarks>
    /// A post-confirmation gate: once confirmed, <see cref="Route"/> is native, so an operator composed after the
    /// confirming Select must be checked or it goes native with wrong data: (1) paging/reducing before the join
    /// (<see cref="PipelineOps"/> is emitted before the <c>$lookup</c> + <c>$unwind</c>, so <c>Join(...).Take(5)</c>
    /// would page un-joined outer rows); (2) a stale root entity type (after <c>Select(x =&gt; x.Inner)</c> a trailing
    /// <c>Where</c> would filter outer rows by an inner key). Only the reducer case is reachable today, since
    /// nav-expansion hoists later slot operators ahead of the pending selector; the slot-operator check is
    /// defence-in-depth. Not part of <see cref="HasTerminalOperator"/>, which is evaluated at join-recording time and
    /// would break native reference-<c>Include</c> confirmation.
    /// </remarks>
    internal bool HasConfirmedJoinLookup => _hasConfirmedJoinLookup;

    internal void MarkJoinLookupConfirmed()
        => _hasConfirmedJoinLookup = true;

    private bool _hasPagingRecordedBeforeAnyJoin;

    /// <summary>
    /// <see langword="true"/> once a <c>Skip</c>/<c>Take</c> was recorded while <c>MongoQueryExpression.Joins</c> was
    /// still empty, i.e. it genuinely pages the outer sequence before a later join
    /// (<c>Customers.Take(1).GroupJoin(...)</c>) rather than being hoisted forward by EF Core. Deferring such an op
    /// past the join would change which rows it keeps, so <c>IsSingleEligibleNativeJoinScope</c> keeps it ahead of
    /// the <c>$lookup</c> when a join may multiply rows (or declines, if <see cref="HasPagingRecordedAfterAJoin"/> is
    /// also set).
    /// </summary>
    internal bool HasPagingRecordedBeforeAnyJoin => _hasPagingRecordedBeforeAnyJoin;

    internal void MarkPagingRecordedBeforeAnyJoin() => _hasPagingRecordedBeforeAnyJoin = true;

    private bool _hasBareJoinInnerEntityLeaf;

    /// <summary>
    /// <see langword="true"/> once <c>TranslateSelect</c>'s bare whole-entity-leaf arm confirmed a join through
    /// its INNER side (<c>Select(ti =&gt; ti.Inner)</c>), so the entity is read from the join's
    /// <c>_lookup_&lt;Nav&gt;</c> field of a WHOLE document. Native emits no <c>$project</c> for that arm, but the
    /// driver-LINQ fallback renders the captured <c>Select</c> as <c>{ _v: "$_lookup_&lt;Nav&gt;" }</c>, which the entity
    /// shaper reads as a silent null entity; the entity path strips that pushed-down <c>Select</c> when this is set.
    /// Deliberately NOT set for the Outer unwrap (<c>ti.Outer</c>), whose root-document read push-down already
    /// satisfies. The strip covers an OUTERMOST captured Select (or one under a reducer). A trailing <c>Distinct()</c>
    /// is not hoisted, so the Select stays under it; the bridge then moves the deduplicated <c>_v</c> back under
    /// <c>_lookup_&lt;Nav&gt;</c> instead (<c>MongoEFToLinqTranslatingExpressionVisitor.RepresentBareInnerJoinLeaf</c>).
    /// </summary>
    internal bool HasBareJoinInnerEntityLeaf => _hasBareJoinInnerEntityLeaf;

    internal void MarkBareJoinInnerEntityLeaf() => _hasBareJoinInnerEntityLeaf = true;

    /// <summary>
    /// The join <c>$lookup</c> whose Outer or Inner side <c>TranslateSelect</c>'s bare whole-entity-leaf arm selected
    /// (<c>Select(ti =&gt; ti.Outer)</c> / <c>Select(ti =&gt; ti.Inner)</c>), and which side; <see langword="null"/>
    /// otherwise. The row is still the flattened join document, so a following whole-entity <c>Distinct()</c> must
    /// compare only the selected entity, not the (outer, inner) pair (see <see cref="MongoDistinctOp"/>).
    /// </summary>
    internal (LookupExpression Lookup, bool IsInner)? BareJoinEntityLeaf { get; private set; }

    internal void MarkBareJoinEntityLeaf(LookupExpression lookup, bool isInner) => BareJoinEntityLeaf = (lookup, isInner);

    private bool _hasPagingRecordedAfterAJoin;

    /// <summary>
    /// The complement of <see cref="HasPagingRecordedBeforeAnyJoin"/>: <see langword="true"/> once a
    /// <c>Skip</c>/<c>Take</c> was recorded into <see cref="PipelineOps"/> while at least one join already
    /// existed — the EF-hoisted-forward <c>Join(...).Take(n)</c> shape, whose paging applies to the JOINED rows.
    /// When both flags are set the single <see cref="PipelineOps"/> snapshot holds paging that belongs on BOTH
    /// sides of the <c>$lookup</c>/<c>$unwind</c>, which neither keeping it in place nor deferring it wholesale
    /// can represent, so <c>IsSingleEligibleNativeJoinScope</c> declines.
    /// </summary>
    internal bool HasPagingRecordedAfterAJoin => _hasPagingRecordedAfterAJoin;

    /// <summary>Records a <c>Skip</c>/<c>Take</c> seen while <paramref name="joinCount"/> (at least one) joins existed.</summary>
    internal void MarkPagingRecordedAfterAJoin(int joinCount)
    {
        _hasPagingRecordedAfterAJoin = true;
        _minJoinCountAtPagingAfterAJoin = _minJoinCountAtPagingAfterAJoin is { } existing
            ? Math.Min(existing, joinCount)
            : joinCount;
    }

    private int? _minJoinCountAtPagingAfterAJoin;

    /// <summary>
    /// <see langword="true"/> when a <c>Skip</c>/<c>Take</c> recorded after a join was recorded while FEWER than
    /// <paramref name="finalJoinCount"/> joins existed, i.e. it was written BETWEEN two joins of a chain
    /// (<c>Join(a, …).Skip(1).Take(2).Join(b, …)</c>) and pages the rows the earlier join(s) produced. The single
    /// deferred <see cref="PostLookupPagingOps"/> snapshot runs after EVERY join's <c>$lookup</c>/<c>$unwind</c>, so it
    /// would page the fully joined result instead — silently wrong rows whenever a later join changes the row count.
    /// <c>IsSingleEligibleNativeJoinScope</c> declines such a chain (the driver-LINQ fallback emits each
    /// <c>$lookup</c> at its own boundary). Paging hoisted forward past the whole chain's pending selector is
    /// recorded with every join present and is unaffected.
    /// </summary>
    internal bool HasPagingRecordedBetweenJoins(int finalJoinCount)
        => _minJoinCountAtPagingAfterAJoin is { } min && min < finalJoinCount;

    private readonly List<MongoUnwindSource> _unwindSources = [];

    /// <summary>
    /// The ordered chain of terminal SelectMany unwind sources: one entry for single-level SelectMany, two for a
    /// nested reference SelectMany correlated off the first's element
    /// (<see cref="NativeTranslation.NativeSelectManyBinder.TryBindNestedReferenceNavUnwind"/>). Written only via
    /// <see cref="AddUnwindSource"/>.
    /// </summary>
    public IReadOnlyList<MongoUnwindSource> UnwindSources => _unwindSources;

    internal void AddUnwindSource(MongoUnwindSource source) => _unwindSources.Add(source);

    /// <summary>
    /// The last (terminal) unwind source, or <see langword="null"/>; what every single-source consumer needs.
    /// </summary>
    internal MongoUnwindSource? UnwindSource => _unwindSources.Count > 0 ? _unwindSources[^1] : null;

    /// <summary>
    /// <see langword="true"/> when the only terminal so far is exactly one reference unwind source. The carve-out
    /// <c>TranslateSelectMany</c> checks before its <see cref="HasTerminalOperator"/> guard, letting a second
    /// SelectMany try nested-reference recognition; every other post-terminal shape still hits the guard.
    /// </summary>
    internal bool IsSingleReferenceUnwindTerminalOnly
        => UnwindSources.Count == 1 && UnwindSources[0].Kind == MongoUnwindSourceKind.Reference
           && !IsGroupBy && !IsDistinct && !IsSetOp && Grouping == null;

    private bool _isGroupByFallbackUnsafe;

    /// <summary>
    /// <see langword="true"/> when this query combines a <c>GroupBy</c> with a Join-family operator producing a
    /// non-entity result. Its driver-LINQ fallback silently returns empty joined entities, so the gate throws
    /// under <c>Native</c>/<c>NativeOnly</c> instead (explicit <c>DriverLinq</c> is left alone).
    /// </summary>
    internal bool IsGroupByFallbackUnsafe => _isGroupByFallbackUnsafe;

    /// <summary>Sets <see cref="IsGroupByFallbackUnsafe"/> and marks the query non-native.</summary>
    internal void MarkGroupByFallbackUnsafe()
    {
        _isGroupByFallbackUnsafe = true;
        _hasUnsupportedOperator = true;
    }

    /// <summary>
    /// <see langword="true"/> when the driver-LINQ fallback is known to return wrong rows (currently only
    /// <see cref="IsGroupByFallbackUnsafe"/>); the gate hard-declines. See <c>ClassifyNativeDisposition</c>.
    /// </summary>
    internal bool IsFallbackWrongData => _isGroupByFallbackUnsafe;

    /// <summary>
    /// Copies wrong-data provenance from a join's <paramref name="inner"/> select. The gate reads only the
    /// outermost select, so without this an offending shape nested in a join inner would silently run the
    /// fallback. Only covers join-inner nesting (sole caller: <c>TranslateJoinCore</c>).
    /// </summary>
    internal void PropagateFallbackWrongDataFrom(MongoSelectDefinition inner)
    {
        if (inner._isGroupByFallbackUnsafe)
        {
            MarkGroupByFallbackUnsafe();
        }
    }

    private bool _hasUnsupportedOperator;

    /// <summary>
    /// Records that this query contains a shape the native path cannot handle, forcing <see cref="Route"/> to
    /// <see cref="NativeRoute.Fallback"/>. Never unset.
    /// </summary>
    internal void MarkNotNativelyRepresentable()
        => _hasUnsupportedOperator = true;

    /// <summary>
    /// Whether <see cref="MarkNotNativelyRepresentable"/> has already been called on this select.
    /// </summary>
    /// <remarks>
    /// Not the same as <c>Route == NativeRoute.Fallback</c>, which is also true while a candidate join is merely
    /// unconfirmed. A join-confirming arm checks this before registering a <c>$lookup</c>: registration flips
    /// <c>UsesDriverJoinFields</c>, which would needlessly change an already-destined fallback's document shape.
    /// </remarks>
    internal bool HasUnsupportedOperator => _hasUnsupportedOperator;

    // Reference-Include candidate joins. The join node is visited before the trailing Select that identifies the
    // Include, and _hasUnsupportedOperator can't be unset, so candidates and confirmations are counted and Route
    // computes the decision. Default-deny: an unconfirmed candidate falls back. Counts, not booleans, because a
    // query can have several candidates and one confirmation must not admit its siblings; InnerCollections can't
    // tell either, since it is keyed by entity type.

    private int _candidateReferenceIncludeJoins;
    private int _confirmedReferenceIncludes;

    /// <summary>
    /// Records a Join-family node that might be EF's nav-expansion of a single-level reference <c>Include</c>.
    /// Admits nothing on its own.
    /// </summary>
    internal void MarkSawCandidateReferenceIncludeJoin()
        => _candidateReferenceIncludeJoins++;

    /// <summary>
    /// Records one confirmed reference-<c>Include</c> chain level (<c>TryConfirmReferenceIncludeChain</c>); called
    /// once per navigation, so N sibling Includes confirm N candidates.
    /// </summary>
    internal void MarkReferenceIncludeConfirmed()
        => _confirmedReferenceIncludes++;

    /// <summary>
    /// A candidate join no trailing Include confirmed; the query must fall back. Uses <c>!=</c> so more
    /// confirmations than candidates (a broken invariant) also fails closed.
    /// </summary>
    internal bool HasUnconfirmedCandidateJoin
        => _candidateReferenceIncludeJoins != _confirmedReferenceIncludes;

    private bool _sawNonBareJoinInner;

    /// <summary>
    /// Records that a Join-family operator's inner side was not a bare collection scan. Set by
    /// <c>TranslateJoinCore</c>, read by <c>TryConfirmReferenceIncludeChain</c>.
    /// <para>
    /// Keyed on the inner select's own shape rather than metadata so every query-filter spelling (TPH-root
    /// inherited, EF10 named) is caught; the flat <c>$lookup</c> can't apply them and would return wrong rows.
    /// </para>
    /// <para>
    /// Known open gap: TPH discriminator narrowing on a derived Include target is not recorded on the inner
    /// select, so it is admitted natively. No wrong data observed, but not proven safe.
    /// </para>
    /// </summary>
    internal void MarkSawNonBareJoinInner() => _sawNonBareJoinInner = true;

    internal bool SawNonBareJoinInner => _sawNonBareJoinInner;

    /// <summary>
    /// Whether nothing at all is recorded on this select. Required of a reference-<c>Include</c> join's inner side,
    /// because the flat <c>$lookup</c> carries no sub-pipeline. Includes <c>_hasUnsupportedOperator</c>, since a
    /// declined inner operator records no op yet is just as disqualifying.
    /// </summary>
    /// <remarks>
    /// <see cref="_postJoinOps"/> must be checked: a reference-collection Count predicate records its <c>$match</c>
    /// there, and omitting it would silently drop the inner filter
    /// (<c>Orders.Join(Owners.Where(o =&gt; o.Orders.Count &gt; 1), ...)</c>). <see cref="_postLookupPagingOps"/> and
    /// <see cref="_postGroupOps"/> are only populated when an earlier conjunct already fails.
    /// </remarks>
    internal bool IsBareCollectionScan
        => !_hasUnsupportedOperator
           && _pipelineOps.Count == 0
           && _trailingOps.Count == 0
           && _postJoinOps.Count == 0
           && _projections.Count == 0
           && Cardinality == null
           && Grouping == null
           && PendingGroupKey == null
           && SetOperation == null
           // Defence-in-depth: a vector search can't currently reach a join's inner side.
           && VectorSearch == null
           && !IsGroupBy
           && !IsDistinct
           && !IsSetOp
           && _unwindSources.Count == 0
           && _candidateReferenceIncludeJoins == 0
           && !_sawNonBareJoinInner;

    /// <summary>
    /// The authoritative slot/projection representability decision for this query. The gate's
    /// <c>ClassifyNativeDisposition</c> layers vector search and <see cref="IsGroupByFallbackUnsafe"/> on top.
    /// Fallback is forced by an unsupported operator, an unconfirmed candidate join, or a
    /// <see cref="ReferenceCollectionCountPredicateConfirmed"/> predicate combined with a set op,
    /// <see cref="Grouping"/>, <see cref="JoinScope"/> or an unwind (a SelectMany's <c>$lookup</c> can even land on
    /// the same path, corrupting the read side).
    /// </summary>
    internal NativeRoute Route
        => _hasUnsupportedOperator || HasUnconfirmedCandidateJoin
            || (_referenceCollectionCountPredicateConfirmed
                && (SetOperation != null || Grouping != null || JoinScope != null || _unwindSources.Count > 0))
            ? NativeRoute.Fallback
            // A GroupBy key was bound but no aggregate Select finalized the grouping: fall back rather than
            // silently emit an ungrouped scan.
            : PendingGroupKey != null && Grouping == null ? NativeRoute.Fallback
            : Cardinality?.Aggregate != null ? NativeRoute.ScalarAggregate
            : Grouping != null ? NativeRoute.GroupBy
            : _projections.Count > 0 ? NativeRoute.Projection
            : NativeRoute.WholeEntity;
}

/// <summary>
/// Which alias family a projection-alias override belongs to; read by the late-fallback strip instead of
/// inspecting the alias string.
/// </summary>
internal enum ProjectionAliasTier
{
    /// <summary>
    /// The alias is the leaf's root-relative document path, so it reads the same off a whole document.
    /// </summary>
    DocumentPath,

    /// <summary>
    /// A computed leaf with a synthetic alias; not readable off a whole document.
    /// </summary>
    Synthetic
}

/// <summary>
/// The native-execution route the compile-time gate takes for a query, derived from
/// <see cref="MongoSelectDefinition.Route"/>.
/// </summary>
internal enum NativeRoute
{
    /// <summary>Not natively representable — use the driver-LINQ fallback (or throw under NativeOnly).</summary>
    Fallback,

    /// <summary>Native pipeline over whole-entity results.</summary>
    WholeEntity,

    /// <summary>Native pipeline ending in a pushed-down <c>$project</c>.</summary>
    Projection,

    /// <summary>Native pipeline ending in a scalar aggregate ($count / $group) producing a single value.</summary>
    ScalarAggregate,

    /// <summary>Native pipeline ending in a keyed <c>$group</c> producing a grouped-aggregate sequence.</summary>
    GroupBy
}
