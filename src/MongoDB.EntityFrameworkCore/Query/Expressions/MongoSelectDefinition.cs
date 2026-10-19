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
/// <para>
/// The <c>Has*</c>/<c>Is*</c> members come in three kinds, which the name alone does not tell apart:
/// <b>derived</b> (a getter over the recorded ops or projections, e.g. <see cref="HasPaging"/>, <see cref="Route"/>; never stored),
/// <b>recorded</b> (a set-once fact with a private backing field and a <c>Mark*</c> method, read-only outside, e.g.
/// <see cref="HasConfirmedJoinLookup"/>, <see cref="HasUnsupportedOperator"/>; it cannot be unset, so a recognizer stages locally and
/// records only after every gate passes), and <b>shape flags</b> (plain <c>{ get; set; }</c> projection-shape facts a binder assigns at
/// its commit point, e.g. <see cref="HasArrayProjectionLeaf"/>).
/// </para>
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

    /// <summary>Ops recorded after a set op was attached; emitted after the set-op stage.</summary>
    public IReadOnlyList<MongoSelectOp> TrailingOps => _trailingOps;

    // Ops recorded once JoinInnerAccessConfirmed flips, incl. the confirming Where/OrderBy and any later reducer
    // $limit: the Where decides "first", so it must run before the reducer's $limit. Recording stops once a set op
    // attaches (ActiveOps then targets TrailingOps); a projected join-scope source1 keeps what it recorded before.
    private readonly List<MongoSelectOp> _postJoinOps = [];

    /// <summary>
    /// Ops recorded after a <c>Where</c>/<c>OrderBy</c>/<c>ThenBy</c> reaching a single-level join's Inner side
    /// confirmed that join's <c>$lookup</c>. Emitted immediately after the <c>$lookup</c>/<c>$unwind</c> block.
    /// </summary>
    public IReadOnlyList<MongoSelectOp> PostJoinOps => _postJoinOps;

    // Populated once, by DeferPipelineOpsPastConfirmedJoin, when EF Core hoisted Skip/Take (and everything before
    // it) ahead of a join's pending result selector and the join is not in the pre-lookup-safe (left-outer,
    // non-collection navigation) set.
    private readonly List<MongoSelectOp> _postLookupPagingOps = [];

    /// <summary>
    /// Ops moved out of <see cref="PipelineOps"/> by <see cref="DeferPipelineOpsPastConfirmedJoin"/>; emitted right
    /// after the confirmed join's <c>$lookup</c>/<c>$unwind</c> block(s), before any <c>$project</c>.
    /// </summary>
    public IReadOnlyList<MongoSelectOp> PostLookupPagingOps => _postLookupPagingOps;

    internal void DeferPipelineOpsPastConfirmedJoin()
    {
        _postLookupPagingOps.AddRange(_pipelineOps);
        _pipelineOps.Clear();
    }

    // Sort/page ops composed after a projected Distinct (not a genuine GroupBy), whose keys resolved against the
    // Distinct's own flattened output alias rather than the root entity (which could collide with a
    // differently-sourced projection member of the same name).
    private readonly List<MongoSelectOp> _postGroupOps = [];

    /// <summary>Sort/page ops recorded after a degenerate/keyed <c>$group</c>; emitted after its flattening <c>$project</c>.</summary>
    public IReadOnlyList<MongoSelectOp> PostGroupOps => _postGroupOps;

    private bool _joinInnerAccessConfirmed;

    /// <summary>
    /// <see langword="true"/> once a <c>Where</c>/<c>OrderBy</c>/<c>ThenBy</c> reaching a single-level join's Inner side
    /// has been resolved. Routes <see cref="ActiveOps"/> to <see cref="PostJoinOps"/> from then on.
    /// </summary>
    internal bool JoinInnerAccessConfirmed => _joinInnerAccessConfirmed;

    internal void MarkJoinInnerAccessConfirmed() => _joinInnerAccessConfirmed = true;

    private bool _referenceCollectionCountPredicateConfirmed;

    /// <summary>
    /// <see langword="true"/> once <see cref="NativeTranslation.NativeReferenceCollectionCountPredicateBinder"/>
    /// registered its own <c>$lookup</c> for a reference-collection <c>Count</c> comparison in a <c>Where</c>. Routed
    /// like <see cref="JoinInnerAccessConfirmed"/> but separate so <see cref="Route"/> can decline this shape alone.
    /// </summary>
    /// <remarks>
    /// The <c>$lookup</c> is registered before it is known whether a set op, projected <c>Distinct</c>/keyed
    /// <c>GroupBy</c> or <c>Join</c> will attach, and those lowerer branches don't flush <see cref="PostJoinOps"/>
    /// where this <c>$match</c> needs it, so <see cref="Route"/> declines the whole combination.
    /// </remarks>
    internal bool ReferenceCollectionCountPredicateConfirmed => _referenceCollectionCountPredicateConfirmed;

    internal void MarkReferenceCollectionCountPredicateConfirmed() => _referenceCollectionCountPredicateConfirmed = true;

    /// <summary>
    /// Moves a trailing <see cref="MongoSortOp"/> (earlier Outer-only keys of the same ordering chain) from
    /// <see cref="PipelineOps"/> into <see cref="PostJoinOps"/> before a <c>ThenBy</c> key reaching the join's Inner side
    /// extends it. No-op when the tail isn't a sort.
    /// <para>
    /// A later <c>$sort</c> re-orders the whole input, using arrival order only as a tie-break, so splitting one
    /// ordering across two <c>$sort</c> stages would make the post-lookup key primary
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
    /// The op list the merge methods currently target: <see cref="TrailingOps"/> after a set op;
    /// <see cref="PostGroupOps"/> once a projected Distinct's or keyed GroupBy's <c>$group</c> is finalized;
    /// <see cref="PostJoinOps"/> once a join's Inner access (or reference-collection Count predicate) is confirmed;
    /// otherwise <see cref="PipelineOps"/>. A finalized grouping takes precedence over a confirmed join, because the
    /// lowerer emits the join's <c>$lookup</c>s and <see cref="PostJoinOps"/> before <c>$group</c>: a post-group op
    /// routed to <see cref="PostJoinOps"/> would run over ungrouped rows lacking the group's aliases.
    /// </summary>
    /// <remarks>
    /// The two <see cref="Grouping"/> branches are exclusive: a GroupBy nested on a projected Distinct (both flags true,
    /// see <see cref="PriorGrouping"/>) must fall through to <see cref="PipelineOps"/>; <c>MongoSelectLowerer</c>
    /// places its post-group ops structurally.
    /// </remarks>
    private List<MongoSelectOp> ActiveOps
        => SetOperation != null ? _trailingOps
            : IsProjectedDistinctOutput ? _postGroupOps
            : IsKeyedGroupOutput ? _postGroupOps
            : _joinInnerAccessConfirmed || _referenceCollectionCountPredicateConfirmed ? _postJoinOps
            : _pipelineOps;

    /// <summary>
    /// ANDs <paramref name="conjunct"/> into the tail <see cref="MongoMatchOp"/>, else appends a new one so a predicate
    /// after a sort or paging stays sequential. Targets <see cref="ActiveOps"/>.
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

    /// <summary>OrderBy: replaces a tail <see cref="MongoSortOp"/> (<c>OrderBy(a).OrderBy(b)</c> keeps only b), else appends.</summary>
    public void StartOrReplaceSort(MongoOrdering first)
    {
        var ops = ActiveOps;
        if (ops.Count > 0 && ops[^1] is MongoSortOp)
            ops[^1] = new MongoSortOp([first]);
        else
            ops.Add(new MongoSortOp([first]));
    }

    /// <summary>
    /// ThenBy: extends the tail sort; if the preceding key failed to translate (select already Fallback) the tail isn't
    /// a sort, so start a fresh one rather than throw. A bare-field key already in the sort is dropped, since it would
    /// render a duplicate <c>$sort</c> field name (CSHARP-5690; mirrors <c>ElideRedundantOrderings</c>). Computed keys
    /// are never elided.
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

    /// <summary>Whole-entity <c>Distinct()</c>; a projected Distinct uses <see cref="Grouping"/>.</summary>
    public void AppendDistinct() => ActiveOps.Add(new MongoDistinctOp());

    /// <summary>
    /// Whole-entity <c>Distinct()</c> over a bare join leaf (<see cref="BareJoinEntityLeaf"/>): goes in
    /// <see cref="PostJoinOps"/> so it dedups the joined rows, not the un-joined outer rows.
    /// </summary>
    internal void AppendPostJoinDistinct(MongoDistinctOp op) => _postJoinOps.Add(op);

    // HasPaging/HasOrdering/HasLimit scan _pipelineOps only: they gate a pre-terminal GroupBy, which is unreachable
    // after a set op, so they must not see _trailingOps.
    /// <summary><see langword="true"/> when any $skip or $limit op is present.</summary>
    internal bool HasPaging => _pipelineOps.Exists(o => o is MongoSkipOp or MongoLimitOp);

    /// <summary>
    /// <see langword="true"/> when <see cref="PostJoinOps"/> holds an op that does not commute with a later row-changing
    /// join (<c>$skip</c>, <c>$limit</c>, <see cref="MongoDistinctOp"/>): PostJoinOps lower after EVERY join, including
    /// one recorded after the op, so it would page/dedup that later join's rows. <c>$match</c>/<c>$sort</c> commute.
    /// </summary>
    internal bool HasNonCommutingPostJoinOp
        => _postJoinOps.Exists(o => o is MongoSkipOp or MongoLimitOp or MongoDistinctOp);

    /// <summary>Currently unused: a pre-<c>GroupBy</c> sort is a no-op or defines the row set for a following Skip/Take, so it is not a reason to decline.</summary>
    internal bool HasOrdering => _pipelineOps.Exists(o => o is MongoSortOp);

    /// <summary>Currently unused: consecutive <c>$limit</c> stages narrow monotonically, so an existing limit is not a reason for a reducer to decline.</summary>
    internal bool HasLimit => _pipelineOps.Exists(o => o is MongoLimitOp);

    /// <summary>
    /// Flips every ordering in the tail <see cref="MongoSortOp"/> of <see cref="ActiveOps"/> (MQL has no "reverse"
    /// stage). Returns <see langword="false"/> without mutating when the tail isn't a sort; Last then uses
    /// <see cref="MongoCardinality.UnorderedLastRow"/>, Reverse declines.
    /// </summary>
    internal bool TryFlipTrailingSortDirection()
    {
        var ops = ActiveOps;
        if (ops.Count == 0 || ops[^1] is not MongoSortOp sort)
            return false;

        ops[^1] = new MongoSortOp([.. sort.Orderings.Select(o => o with { Ascending = !o.Ascending })]);
        return true;
    }

    /// <summary>The <c>$project</c> output fields, in order. Empty means whole-entity results.</summary>
    public IReadOnlyList<MongoProjection> Projection => _projections;

    public void AddProjection(MongoProjection projection)
        => _projections.Add(projection);

    /// <summary>Lets <c>NativeGroupByBinder.TryBindDistinctFromProjection</c> replace the projection with the flattening one.</summary>
    internal void ClearProjections() => _projections.Clear();

    /// <summary>Renames the single projection so a bare scalar set-op operand emits the first operand's alias (<c>CanAlignBareScalarAliases</c>).</summary>
    internal void ReplaceSingleProjectionAlias(string alias)
    {
        if (_projections.Count != 1)
        {
            throw new InvalidOperationException(
                $"Expected exactly one projection to re-alias, found {_projections.Count}.");
        }

        _projections[0] = _projections[0] with { Alias = alias };
    }

    // Projection-alias overrides: the single source for every site that would otherwise derive a $project alias
    // (and the name the shaper reads by) from ProjectionMember.Last?.Name. A map so a member can decouple alias from
    // document path ("Notes" -> "Home.Notes").

    /// <summary>Override-table key for a bare selector body; a sentinel since dictionaries reject null keys, and the leading space can't collide with a member name.</summary>
    internal const string BareProjectionMemberKey = " bare";

    private Dictionary<string, (string Alias, ProjectionAliasTier Tier)>? _projectionAliasOverrides;

    /// <summary>
    /// Overrides the <c>$project</c> output element name (and the shaper's read name) for a projection member
    /// (<see cref="BareProjectionMemberKey"/> for a bare body). Written only by <c>NativeProjectionBinder</c>, in the
    /// same commit block as the matching <see cref="AddProjection"/>.
    /// </summary>
    /// <remarks>
    /// Write-once: two aliases for one member would let the emitted <c>$project</c> key and the shaper's read name
    /// disagree, silently yielding null/empty values. The tier is carried as data because the late-fallback strip is
    /// tier-conditional and must not re-derive it from the alias string.
    /// </remarks>
    internal void AddProjectionAliasOverride(string memberName, string alias, ProjectionAliasTier tier)
        => (_projectionAliasOverrides ??= new Dictionary<string, (string, ProjectionAliasTier)>()).Add(
            memberName, (alias, tier));

    /// <summary>Looks up the override; a null <paramref name="memberName"/> (bare body) maps to <see cref="BareProjectionMemberKey"/>.</summary>
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

    /// <summary>The alias <paramref name="memberName"/> is emitted under: its override, else the name itself.</summary>
    [return: NotNullIfNotNull(nameof(memberName))]
    internal string? ResolveProjectionAlias(string? memberName)
        => TryGetProjectionAlias(memberName, out var overriddenAlias) ? overriddenAlias : memberName;

    /// <summary>
    /// Looks up the <see cref="MongoDocumentConstructionExpression"/> the emit side staged into
    /// <see cref="Projection"/> for <paramref name="memberName"/> (its own name, not the alias) whose original type is
    /// <paramref name="expectedType"/>.
    /// </summary>
    /// <remarks>
    /// The single lookup shared by the plain-root, join-scope (<c>BindResultMember</c>) and GroupBy
    /// (<c>BindGroupMember</c>) leaves, so none can use looser admission rules; it reads the emit side's committed
    /// result, so the bind side never admits a shape the emit side declined. Admits <see cref="NativeRoute.GroupBy"/>
    /// too, because a <see cref="Grouping"/>-bearing select routes as GroupBy while its nested construction is staged
    /// in <see cref="Projection"/>.
    /// </remarks>
    internal bool TryGetDocumentConstructionProjection(
        string? memberName, Type expectedType, [NotNullWhen(true)] out MongoDocumentConstructionExpression? construction)
    {
        construction = null;

        if (Route is not (NativeRoute.Projection or NativeRoute.GroupBy) || memberName is null)
        {
            return false;
        }

        var alias = ResolveProjectionAlias(memberName);

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

    /// <summary>Rewrites every <see cref="MongoProjection.Source"/> from the selector parameter to the source shaper, as the projection binding visitor sees it.</summary>
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

    /// <summary>Looks up the leaf staged into <see cref="Projection"/> for <paramref name="memberName"/> (null for a bare body), applying any alias override.</summary>
    internal bool TryGetProjectionLeaf(string? memberName, [NotNullWhen(true)] out MongoProjection? leaf)
    {
        leaf = null;

        if (Route != NativeRoute.Projection)
        {
            return false;
        }

        var alias = ResolveProjectionAlias(memberName);
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
    /// The staged output under <paramref name="alias"/> with <see cref="MongoProjection.ThrowsOnNull"/> set, in
    /// <see cref="Projection"/> or a projected set-op operand: combined rows are all read through this select's shaper,
    /// so an operand's null-propagated leaf must throw on null there too. The read side's only source for the flag.
    /// </summary>
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

    /// <summary>Whether <paramref name="alias"/> is a native <c>DateTime.TimeOfDay</c> leaf here or in a set-op operand, read through <c>BsonSerializerFactory.TimeOfDayMillisecondsSerializer</c>.</summary>
    internal bool IsTimeOfDayProjection(string alias)
    {
        foreach (var projection in _projections)
        {
            if (projection.Alias == alias)
            {
                return MongoDatePartExpression.IsTimeOfDay(projection.Expression);
            }
        }

        foreach (var link in _setOperations)
        {
            if (link.OperandsProjected && link.OperandSelect.IsTimeOfDayProjection(alias))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary><see langword="true"/> when a bare selector body populated <see cref="Projection"/>.</summary>
    internal bool IsBareProjection
        => _projectionAliasOverrides?.ContainsKey(BareProjectionMemberKey) == true;

    /// <summary>The bare-body override's tier, or null; the late native-factory fallback can read a <see cref="ProjectionAliasTier.DocumentPath"/> alias off a whole document, not a <see cref="ProjectionAliasTier.Synthetic"/> one.</summary>
    internal ProjectionAliasTier? BareProjectionTier
        => _projectionAliasOverrides != null
           && _projectionAliasOverrides.TryGetValue(BareProjectionMemberKey, out var entry)
            ? entry.Tier
            : null;

    /// <summary>
    /// <see langword="true"/> when any override is a <see cref="ProjectionAliasTier.DocumentPath"/> alias, i.e. the shaper
    /// reads a leaf by a name the driver-LINQ bridge would not emit. Read by the late native-factory-failure strip in
    /// <c>MongoShapedQueryCompilingExpressionVisitor</c>, which removes the pushed-down <c>Select</c> so the fallback read hits.
    /// </summary>
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

    /// <summary>The terminal cardinality reducer or scalar aggregate, or null for a plain enumerable result.</summary>
    public MongoCardinality? Cardinality
    {
        get => _cardinality;
        set
        {
            // Else Route becomes ScalarAggregate while the lowerer still emits the grouping pipeline, and the scalar shaper
            // reads a nonexistent element.
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
            Debug.Assert(value == null || _cardinality == null,
                "Grouping and Cardinality are mutually exclusive (see the NativeCardinalityBinder post-group guard).");
            _grouping = value;
        }
    }

    /// <summary>
    /// Installs both <see cref="Grouping"/> and <see cref="Cardinality"/>, the one sanctioned exception to their mutual
    /// exclusion (a scalar aggregate directly on a bare <c>GroupBy(key)</c>); writes the backing fields so the setter
    /// asserts still guard every other path.
    /// </summary>
    internal void SetGroupedTerminalAggregate(
        MongoGrouping grouping, MongoCardinality cardinality, MongoExpression? postGroupPredicate)
    {
        _grouping = grouping;
        _cardinality = cardinality;
        PostGroupPredicate = postGroupPredicate;
    }

    /// <summary>
    /// The prior stage's <c>$group</c>, snapshotted by <see cref="SnapshotPriorGroupingForNestedGroupBy"/> when a
    /// <c>GroupBy(key).Select(aggregate)</c> composes on an already-finalized grouping. Emitted before the outer
    /// <see cref="Grouping"/>; the outer selectors resolve against its flattened alias schema
    /// (<see cref="NativeTranslation.MongoExpressionTranslator.DistinctAliasScope"/>).
    /// </summary>
    internal MongoGrouping? PriorGrouping { get; private set; }

    /// <summary>The prior stage's flattening <c>$project</c>; emitted right after its <c>$group</c>, before <see cref="PostGroupOps"/> and the outer grouping.</summary>
    internal IReadOnlyList<MongoProjection> PriorGroupingProjection { get; private set; } = [];

    /// <summary>
    /// The prior stage's HAVING predicate, snapshotted so the outer GroupBy's reset of <see cref="GroupHavingPredicate"/>
    /// doesn't silently drop it. Emitted after the prior <c>$group</c>, before its flattening <c>$project</c>.
    /// </summary>
    internal MongoExpression? PriorGroupHavingPredicate { get; private set; }

    /// <summary>
    /// Moves the current grouping/projection/HAVING into the <c>Prior*</c> slots so a nested <c>GroupBy(key).Select(aggregate)</c>
    /// binds independently. Called once by <c>TranslateGroupBy</c>, before <c>TryBindGroupKey</c>, only when a finalized
    /// <see cref="Grouping"/> exists.
    /// </summary>
    internal void SnapshotPriorGroupingForNestedGroupBy()
    {
        PriorGrouping = _grouping;

        // A stale Grouping matched the post-GroupBy.Select aggregate path, which re-emitted the prior $group and dropped
        // the nested key.
        _grouping = null;
        PriorGroupingProjection = [.. _projections];
        PriorGroupHavingPredicate = GroupHavingPredicate;
        GroupHavingPredicate = null;
        ClearProjections();
    }

    /// <summary>Transient binder state: key parts from <c>TryBindGroupKey</c>, consumed by <c>TryBindGroupProjection</c>.</summary>
    internal IReadOnlyList<MongoGroupingKeyPart>? PendingGroupKey { get; set; }

    /// <summary>A <c>$match</c> right after <c>$group</c> over accumulator outputs, for a scalar aggregate on a bare <c>GroupBy(key)</c> (<c>.Any(g =&gt; g.Count() &gt; 1)</c>).</summary>
    internal MongoExpression? PostGroupPredicate { get; set; }

    /// <summary>Transient binder state: the accumulator comparison from a <c>Where</c> on a bare <c>GroupBy(key)</c> (EF normalizes <c>Any(pred)</c>/<c>Count(pred)</c> to <c>Where(pred).Any()</c>); consumed by <c>TryBindGroupTerminalAggregate</c>.</summary>
    internal (MongoGroupAccumulator? Accumulator, MongoExpression Comparison)? PendingGroupPredicate { get; set; }

    /// <summary>
    /// Transient binder state: <c>OrderBy</c>/<c>ThenBy</c> selectors on a not-yet-finalized <c>GroupBy(key)</c>. They can't
    /// resolve when recorded because an aggregate key (<c>g.Count()</c>) needs an accumulator that exists only after the
    /// terminal Select; <c>TryBindGroupProjection</c> resolves them into <see cref="GroupOrderOp"/>.
    /// </summary>
    internal List<(bool Ascending, LambdaExpression KeySelector)>? PendingGroupOrderings { get; set; }

    /// <summary>
    /// The sort after <c>$group</c>, before its flattening <c>$project</c>. Separate from <see cref="PostGroupOps"/>
    /// because it may reference an accumulator the final projection never flattens (sort by <c>Count()</c>, project only <c>Sum(...)</c>).
    /// </summary>
    internal MongoSortOp? GroupOrderOp { get; set; }

    /// <summary>Transient binder state: translated Skip/Take ops on a not-yet-finalized <c>GroupBy(key)</c>; moved into <see cref="GroupPagingOps"/> by <c>TryBindGroupProjection</c>.</summary>
    internal List<MongoSelectOp>? PendingGroupPaging { get; set; }

    private List<MongoSelectOp> _groupPagingOps = [];

    /// <summary><c>$skip</c>/<c>$limit</c> ops run after <see cref="GroupOrderOp"/>, before the flattening <c>$project</c>.</summary>
    public IReadOnlyList<MongoSelectOp> GroupPagingOps
    {
        get => _groupPagingOps;
        internal set => _groupPagingOps = [.. value];
    }

    /// <summary>
    /// The HAVING <c>$match</c>: a <c>Where</c> between <c>GroupBy(key)</c> and the terminal <c>Select</c>, emitted after
    /// <c>$group</c>, before its flattening <c>$project</c>. Unlike <see cref="PostGroupPredicate"/> (no Select) and
    /// <see cref="PostGroupOps"/> (filters the flattened output).
    /// </summary>
    internal MongoExpression? GroupHavingPredicate { get; set; }

    /// <summary>
    /// <see langword="true"/> once a <c>GroupBy</c> has been seen, whether or not it bound natively; used to hard-decline
    /// a grouped source feeding a Join-family operator (see <see cref="IsGroupByFallbackUnsafe"/>).
    /// </summary>
    internal bool IsGroupBy { get; set; }

    /// <summary>
    /// <see langword="true"/> once a projected <c>Distinct</c> has bound natively. Separate from <see cref="IsGroupBy"/>
    /// because Distinct-then-Join falls back gracefully (driver-LINQ joins flat rows correctly), GroupBy-then-Join must hard-decline.
    /// </summary>
    internal bool IsDistinct { get; set; }

    /// <summary>
    /// <see langword="true"/> once a native terminal (GroupBy, projected Distinct, set op, finalized grouping, or
    /// unwind) exists. Any later operator must fall back rather than resolve against the base entity and emit a stage
    /// before the terminal. <see cref="JoinScope"/> is metadata only and does not count.
    /// </summary>
    internal bool HasTerminalOperator
        => IsGroupBy || IsDistinct || IsSetOp || Grouping != null || UnwindSources.Count > 0;

    /// <summary>
    /// <see langword="true"/> for a finalized keyed <c>GroupBy(key).Select(...)</c> with no terminal yet, where a predicate
    /// recorded now lowers right after this grouping's flattening <c>$project</c> and so can filter the Select's aliases.
    /// </summary>
    /// <remarks>
    /// Excludes a GroupBy nested on a prior grouping (its <see cref="PostGroupOps"/> lower after the <i>prior</i> flatten,
    /// ahead of this <c>$group</c>) and any set op (<see cref="TrailingOps"/> lower ahead of a later <c>$group</c>). In
    /// both the predicate would run before the aliases it reads exist and silently answer over the wrong rows.
    /// </remarks>
    internal bool IsFinalizedKeyedGroupOutput
        => IsKeyedGroupOutput && Cardinality == null
           && PriorGrouping == null && SetOperation == null;

    /// <summary>
    /// <see langword="true"/> once a projected <c>Distinct()</c>'s <c>$group</c> is finalized with no <c>GroupBy</c> nested
    /// on it: later operators resolve against its key-part aliases
    /// (<see cref="NativeTranslation.MongoExpressionTranslator.DistinctAliasScope"/>), not the entity.
    /// </summary>
    [MemberNotNullWhen(true, nameof(Grouping))]
    internal bool IsProjectedDistinctOutput
        => IsDistinct && !IsGroupBy && Grouping != null;

    /// <summary>
    /// <see langword="true"/> once a keyed <c>GroupBy(key).Select(...)</c>'s grouping is finalized and not nested on a
    /// projected <c>Distinct</c>. Unlike <see cref="IsFinalizedKeyedGroupOutput"/>, a terminal, prior grouping or set op may be attached.
    /// </summary>
    [MemberNotNullWhen(true, nameof(Grouping))]
    internal bool IsKeyedGroupOutput
        => IsGroupBy && !IsDistinct && Grouping != null;

    /// <summary>
    /// <see langword="true"/> when a set op is the only thing done so far (no grouping/distinct/unwind/projection);
    /// relaxes the post-terminal guards for operators after a set op. Flips once a trailing projection is populated.
    /// </summary>
    internal bool IsSetOpTerminalOnly
        => IsSetOp && !IsGroupBy && !IsDistinct && Grouping == null && UnwindSources.Count == 0 && Projection.Count == 0;

    /// <summary>
    /// The Atlas <c>$vectorSearch</c> anchoring this query, if any. A dedicated slot rather than a
    /// <see cref="PipelineOps"/> entry so being the first stage (a server requirement) is structural. Not a terminal.
    /// </summary>
    internal MongoVectorSearch? VectorSearch { get; set; }

    // In LINQ source order. More than one entry for a left-nested whole-entity Concat/Union chain
    // (A.Concat(B).Concat(C)); the lowerer emits one $unionWith per entry.
    private readonly List<MongoSetOperation> _setOperations = [];

    /// <summary>The terminal set operations, in LINQ source order; see <c>IsChainableSetOpSelect</c> for what may chain.</summary>
    internal IReadOnlyList<MongoSetOperation> SetOperations => _setOperations;

    /// <summary>The first set operation; the canonical "is this a set-op query?" test. Anything walking operands must iterate <see cref="SetOperations"/>.</summary>
    internal MongoSetOperation? SetOperation => _setOperations.Count > 0 ? _setOperations[0] : null;

    /// <remarks>
    /// Ops between links (<c>A.Union(B).OrderBy(..).Take(1).Union(C)</c>) land in <see cref="TrailingOps"/> but belong
    /// before the new link; moving them keeps <see cref="TrailingOps"/> meaning "after the last link", else the
    /// <c>Take</c> would run after the later union.
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

    /// <summary>A terminal set operation is attached; part of <see cref="HasTerminalOperator"/>.</summary>
    internal bool IsSetOp { get; set; }

    /// <summary>
    /// <see langword="true"/> when <see cref="Projection"/> has an owned collection array leaf (<c>b.Posts</c>) or owned
    /// reference leaf (<c>b.Address</c>). Read by <c>IsPlainProjectedSelect</c>, which declines such a set-op operand:
    /// either leaf forces the owner key into the document, so value-based dedup would silently become identity-based.
    /// </summary>
    internal bool HasArrayProjectionLeaf { get; set; }

    /// <summary>
    /// <see langword="true"/> when <see cref="Projection"/> has a string-to-char-sequence leaf
    /// (<c>e.City.AsEnumerable()</c>/<c>.ToList()</c>/<c>.ToArray()</c>): only the string field is pushed down and the
    /// shaper materializes it.
    /// </summary>
    /// <remarks>
    /// The driver can't translate <c>Enumerable.*</c> over a string, and registering the leaf as a projection member
    /// erases the call from the shaper, hiding it from <c>UntranslatableProjectionFinder</c>. <c>VisitProjectedQuery</c>
    /// reads this flag beside <c>ProjectionAnalyzer.CanPushDown</c> and routes non-native execution to the client/mixed shaper.
    /// </remarks>
    internal bool HasStringSequenceProjectionLeaf { get; set; }

    /// <summary>
    /// <see langword="true"/> when a <see cref="Projection"/> leaf is the receiver of a client-reapplied
    /// <c>ToLower</c>/<c>ToUpper</c>: the projected document holds the raw receiver, so a later operator reading
    /// projected values must decline natively (see <c>IsProjectedValueFreeOperator</c>).
    /// </summary>
    internal bool HasClientCaseMappingProjectionLeaf { get; set; }

    /// <summary>
    /// <see langword="true"/> when the committed projection has a row-independent leaf the shaper evaluates client-side
    /// and never projects (<c>NativeProjectionBinder.IsRowIndependentLeaf</c>). A <c>$literal</c>-rendered constant or
    /// parameter is server-side and never sets it.
    /// </summary>
    /// <remarks>
    /// The server never sees the value, so no server operator may read it: a set op (the shared shaper would show
    /// source1's value on source2's rows) and every other value-reading operator decline. <c>Distinct</c> stays native:
    /// the value is the same for every row of one execution. When every leaf is client-evaluated the binder projects a
    /// constant sentinel (<c>ClientEvaluatedSentinelAlias</c>) so <see cref="Route"/> stays <see cref="NativeRoute.Projection"/>.
    /// </remarks>
    internal bool HasClientEvaluatedProjectionLeaf { get; set; }

    private HashSet<string>? _clientConditionalMembers;

    /// <summary>
    /// Records that <paramref name="memberName"/>'s leaf is a ternary over client-only constructions
    /// (<c>c.City == "Seattle" ? new P { Id = "PAY" } : new P { ... }</c>): only its test is projected, under that member's
    /// alias, and the shaper evaluates the conditional.
    /// </summary>
    internal void AddClientConditionalMember(string memberName)
        => (_clientConditionalMembers ??= new HashSet<string>(StringComparer.Ordinal)).Add(memberName);

    /// <summary>
    /// <see langword="true"/> when some leaf is a client-evaluated ternary. Unlike a row-independent leaf its value differs
    /// between rows, so <c>Distinct</c> (which would dedup by the projected test and branch reads) declines too.
    /// </summary>
    internal bool HasClientConditionalProjectionLeaf
        => _clientConditionalMembers is { Count: > 0 };

    /// <summary>
    /// Whether <paramref name="memberName"/> holds a client-evaluated ternary whose staged test is
    /// <paramref name="test"/> (structurally, after <see cref="RebaseProjectionSources"/>); the read side's only admission test.
    /// </summary>
    internal bool IsClientConditionalLeaf(string? memberName, Expression test)
        => IsRecordedClientConditionalLeaf(memberName, test);

    private HashSet<string>? _clientConditionalReadAliases;

    /// <summary>Records that <paramref name="alias"/> is a row read inside a client-evaluated ternary's branch (<c>Name = x.Name</c>).</summary>
    internal void AddClientConditionalRead(string alias)
        => (_clientConditionalReadAliases ??= new HashSet<string>(StringComparer.Ordinal)).Add(alias);

    /// <summary>
    /// The alias the emit side staged a ternary branch read under, matched structurally. The read side takes the alias
    /// from here, never from the member path, and registers it explicitly (<c>MongoQueryExpression.ApplyProjection</c>).
    /// </summary>
    internal bool TryGetClientConditionalReadAlias(Expression read, [NotNullWhen(true)] out string? alias)
    {
        alias = null;
        if (_clientConditionalReadAliases is null)
        {
            return false;
        }

        foreach (var projection in _projections)
        {
            if (_clientConditionalReadAliases.Contains(projection.Alias)
                && projection.Source is { } source
                && Microsoft.EntityFrameworkCore.Query.ExpressionEqualityComparer.Instance.Equals(source, read))
            {
                alias = projection.Alias;
                return true;
            }
        }

        return false;
    }

    private bool IsRecordedClientConditionalLeaf(string? memberName, Expression test)
        => _clientConditionalMembers?.Contains(memberName ?? BareProjectionMemberKey) == true
           && TryGetProjectionLeaf(memberName, out var leaf)
           && leaf.Value.Source is { } source
           && Microsoft.EntityFrameworkCore.Query.ExpressionEqualityComparer.Instance.Equals(source, test);

    /// <summary>
    /// <see langword="true"/> when <see cref="Route"/> is <see cref="NativeRoute.WholeEntity"/> only because the selector
    /// wraps the entity client-side (<c>x =&gt; new Dto(x)</c>): the shaper's result is not the entity itself.
    /// </summary>
    /// <remarks>
    /// Keeps such an operand out of a native set-op combine: deduping raw documents is wrong for a wrapped result, and an
    /// unordered <c>$unionWith</c> + <c>FirstOrDefault()</c> can surface a different row than the in-memory baseline
    /// (<c>Client_eval_Union_FirstOrDefault</c>).
    /// </remarks>
    internal bool HasClientWrappedWholeEntityShaper { get; set; }

    /// <summary>
    /// <see langword="true"/> for a multi-argument positional-ctor DTO projection (<c>new Item(x.A, x.B)</c>;
    /// <c>NewExpression.Members</c> is null). <c>TranslateSelect</c> then builds an index-based shaper, because
    /// <c>VisitNew</c> only enters a projection member per argument when <c>Members</c> is non-null, so every argument
    /// would otherwise share one member.
    /// </summary>
    internal bool HasPositionalCtorProjectionShaper { get; set; }

    /// <summary>The native join scope chain, or null if this select has no eligible native join.</summary>
    internal MongoJoinScope? JoinScope { get; set; }

    /// <summary>
    /// Set when the grouped source is a native join scope that is safe to group over. <see cref="NativeTranslation.NativeGroupByBinder"/>
    /// resolves key parts, accumulators and element predicates through it and confirms the join chain only once the whole
    /// grouping has bound. Null means root-entity resolution (any join stays an unconfirmed candidate and the query falls back).
    /// </summary>
    internal MongoJoinScope? GroupByJoinScope { get; set; }

    private bool _hasConfirmedJoinLookup;

    /// <summary>
    /// <see langword="true"/> once the join (chain) in <see cref="JoinScope"/> has been confirmed and its <c>$lookup</c>(s)
    /// registered (<c>NativeJoinScopeProjectionBinder.ConfirmEntireChain</c>). Never unset.
    /// </summary>
    /// <remarks>
    /// A post-confirmation gate: once confirmed <see cref="Route"/> is native, so an operator composed after the
    /// confirming Select must be checked or it goes native with wrong data: (1) paging/reducing before the join
    /// (<see cref="PipelineOps"/> is emitted before the <c>$lookup</c>, so <c>Join(...).Take(5)</c> would page un-joined
    /// outer rows); (2) a stale root entity type (a trailing <c>Where</c> after <c>Select(x =&gt; x.Inner)</c> would filter
    /// outer rows by an inner key). Only the reducer case is reachable today (nav-expansion hoists slot operators ahead of
    /// the pending selector); the slot check is defence-in-depth. Not part of <see cref="HasTerminalOperator"/>, which is
    /// evaluated at join-recording time and would break reference-<c>Include</c> confirmation.
    /// </remarks>
    internal bool HasConfirmedJoinLookup => _hasConfirmedJoinLookup;

    internal void MarkJoinLookupConfirmed()
        => _hasConfirmedJoinLookup = true;

    private bool _hasPagingRecordedBeforeAnyJoin;

    /// <summary>
    /// <see langword="true"/> once a <c>Skip</c>/<c>Take</c> was recorded while no join existed, i.e. it genuinely pages
    /// the outer sequence (<c>Customers.Take(1).GroupJoin(...)</c>) rather than being hoisted forward by EF Core.
    /// Deferring it past the join would change which rows it keeps, so <c>TryAdmitSingleEligibleNativeJoinScope</c> keeps it
    /// ahead of the <c>$lookup</c> when a join may multiply rows (or declines if <see cref="HasPagingRecordedAfterAJoin"/> is also set).
    /// </summary>
    internal bool HasPagingRecordedBeforeAnyJoin => _hasPagingRecordedBeforeAnyJoin;

    internal void MarkPagingRecordedBeforeAnyJoin() => _hasPagingRecordedBeforeAnyJoin = true;

    private bool _hasBareJoinInnerEntityLeaf;

    /// <summary>
    /// <see langword="true"/> once a bare whole-entity-leaf <c>Select(ti =&gt; ti.Inner)</c> confirmed a join through its
    /// INNER side, so the entity is read from the whole document's <c>_lookup_&lt;Nav&gt;</c> field. Native emits no
    /// <c>$project</c> there, but the driver-LINQ fallback renders the captured <c>Select</c> as
    /// <c>{ _v: "$_lookup_&lt;Nav&gt;" }</c>, which the shaper reads as a silent null entity, so the entity path strips that
    /// <c>Select</c>. Not set for the Outer unwrap (<c>ti.Outer</c>). The strip covers an OUTERMOST captured Select (or one
    /// under a reducer); a trailing <c>Distinct()</c> keeps the Select under it, and the bridge moves the deduplicated
    /// <c>_v</c> back under <c>_lookup_&lt;Nav&gt;</c> instead (<c>RepresentBareInnerJoinLeaf</c>).
    /// </summary>
    internal bool HasBareJoinInnerEntityLeaf => _hasBareJoinInnerEntityLeaf;

    internal void MarkBareJoinInnerEntityLeaf() => _hasBareJoinInnerEntityLeaf = true;

    /// <summary>
    /// The join <c>$lookup</c> and side selected by a bare whole-entity leaf (<c>Select(ti =&gt; ti.Outer)</c>/<c>.Inner</c>).
    /// The row is still the flattened join document, so a following <c>Distinct()</c> must compare only the selected
    /// entity, not the (outer, inner) pair (see <see cref="MongoDistinctOp"/>).
    /// </summary>
    internal (LookupExpression Lookup, bool IsInner)? BareJoinEntityLeaf { get; private set; }

    internal void MarkBareJoinEntityLeaf(LookupExpression lookup, bool isInner) => BareJoinEntityLeaf = (lookup, isInner);

    private bool _hasPagingRecordedAfterAJoin;

    /// <summary>
    /// <see langword="true"/> once a <c>Skip</c>/<c>Take</c> was recorded while a join already existed (EF-hoisted
    /// <c>Join(...).Take(n)</c>, which pages the JOINED rows). With <see cref="HasPagingRecordedBeforeAnyJoin"/> also set,
    /// paging belongs on BOTH sides of the <c>$lookup</c>, which neither keeping nor deferring it can represent, so
    /// <c>TryAdmitSingleEligibleNativeJoinScope</c> declines.
    /// </summary>
    internal bool HasPagingRecordedAfterAJoin => _hasPagingRecordedAfterAJoin;

    /// <summary>Paging exists and was recorded after a join, so it would have to be deferred past the <c>$lookup</c> chain; chain-scope arms decline on this.</summary>
    internal bool HasPagingDeferredPastAChainJoin => HasPaging && HasPagingRecordedAfterAJoin;

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
    /// <see langword="true"/> when paging was written BETWEEN two joins of a chain
    /// (<c>Join(a, …).Skip(1).Take(2).Join(b, …)</c>), i.e. recorded while fewer than <paramref name="finalJoinCount"/>
    /// joins existed. The single deferred <see cref="PostLookupPagingOps"/> snapshot runs after EVERY join's
    /// <c>$lookup</c>, so it would page the fully joined result: silently wrong rows whenever a later join changes the row
    /// count. <c>TryAdmitSingleEligibleNativeJoinScope</c> declines such a chain.
    /// </summary>
    internal bool HasPagingRecordedBetweenJoins(int finalJoinCount)
        => _minJoinCountAtPagingAfterAJoin is { } min && min < finalJoinCount;

    private readonly List<MongoUnwindSource> _unwindSources = [];

    /// <summary>
    /// The terminal SelectMany unwind sources: one for single-level, two for a nested reference SelectMany correlated off
    /// the first's element (<see cref="NativeTranslation.NativeSelectManyBinder.TryBindNestedReferenceNavUnwind"/>).
    /// </summary>
    public IReadOnlyList<MongoUnwindSource> UnwindSources => _unwindSources;

    internal void AddUnwindSource(MongoUnwindSource source) => _unwindSources.Add(source);

    /// <summary>The last (terminal) unwind source; what every single-source consumer needs.</summary>
    internal MongoUnwindSource? UnwindSource => _unwindSources.Count > 0 ? _unwindSources[^1] : null;

    /// <summary>Exactly one reference unwind is the only terminal: the carve-out <c>TranslateSelectMany</c> checks before its <see cref="HasTerminalOperator"/> guard, letting a second SelectMany try nested-reference recognition.</summary>
    internal bool IsSingleReferenceUnwindTerminalOnly
        => UnwindSources.Count == 1 && UnwindSources[0].Kind == MongoUnwindSourceKind.Reference
           && !IsGroupBy && !IsDistinct && !IsSetOp && Grouping == null;

    private bool _isGroupByFallbackUnsafe;

    /// <summary>
    /// <see langword="true"/> when a <c>GroupBy</c> is combined with a Join-family operator producing a non-entity result.
    /// The driver-LINQ fallback silently returns empty joined entities, so the gate throws under <c>Native</c>/<c>NativeOnly</c>
    /// (explicit <c>DriverLinq</c> is left alone).
    /// </summary>
    internal bool IsGroupByFallbackUnsafe => _isGroupByFallbackUnsafe;

    /// <summary>Sets <see cref="IsGroupByFallbackUnsafe"/> and marks the query non-native.</summary>
    internal void MarkGroupByFallbackUnsafe()
    {
        _isGroupByFallbackUnsafe = true;
        _hasUnsupportedOperator = true;
    }

    /// <summary>The driver-LINQ fallback is known to return wrong rows (currently only <see cref="IsGroupByFallbackUnsafe"/>); the gate hard-declines.</summary>
    internal bool IsFallbackWrongData => _isGroupByFallbackUnsafe;

    /// <summary>Copies wrong-data provenance from a join's inner select; the gate reads only the outermost select, so a nested offending shape would silently run the fallback.</summary>
    internal void PropagateFallbackWrongDataFrom(MongoSelectDefinition inner)
    {
        if (inner._isGroupByFallbackUnsafe)
        {
            MarkGroupByFallbackUnsafe();
        }
    }

    private bool _hasUnsupportedOperator;

    /// <summary>Records a shape the native path cannot handle, forcing <see cref="Route"/> to <see cref="NativeRoute.Fallback"/>. Never unset.</summary>
    internal void MarkNotNativelyRepresentable()
        => _hasUnsupportedOperator = true;

    /// <summary>
    /// Whether <see cref="MarkNotNativelyRepresentable"/> was called. Not the same as <c>Route == Fallback</c>, which is
    /// also true while a candidate join is unconfirmed. A join-confirming arm checks this before registering a
    /// <c>$lookup</c>, since registration flips <c>UsesDriverJoinFields</c> and would change an already-destined fallback's document shape.
    /// </summary>
    internal bool HasUnsupportedOperator => _hasUnsupportedOperator;

    // Reference-Include candidate joins. The join node is visited before the trailing Select that identifies the
    // Include, and _hasUnsupportedOperator can't be unset, so candidates and confirmations are counted and Route
    // decides. Default-deny. Counts, not booleans: one confirmation must not admit sibling candidates, and
    // InnerCollections is keyed by entity type so can't tell either.

    private int _candidateReferenceIncludeJoins;
    private int _confirmedReferenceIncludes;

    /// <summary>Records a Join-family node that might be EF's nav-expansion of a reference <c>Include</c>; admits nothing on its own.</summary>
    internal void MarkSawCandidateReferenceIncludeJoin()
        => _candidateReferenceIncludeJoins++;

    /// <summary>Records one confirmed reference-<c>Include</c> chain level, once per navigation.</summary>
    internal void MarkReferenceIncludeConfirmed()
        => _confirmedReferenceIncludes++;

    /// <summary>Candidate joins confirmed so far.</summary>
    internal int ConfirmedReferenceIncludeCount => _confirmedReferenceIncludes;

    /// <summary>A candidate join no trailing Include confirmed, so the query must fall back. <c>!=</c> so a broken invariant (more confirmations than candidates) fails closed.</summary>
    internal bool HasUnconfirmedCandidateJoin
        => _candidateReferenceIncludeJoins != _confirmedReferenceIncludes;

    private bool _sawNonBareJoinInner;

    /// <summary>
    /// Records that a Join-family operator's inner side was not a bare collection scan.
    /// <para>
    /// Keyed on the inner select's shape rather than metadata so every query-filter spelling (TPH-root inherited, EF10
    /// named) is caught; the flat <c>$lookup</c> can't apply them and would return wrong rows.
    /// </para>
    /// <para>
    /// Known open gap: TPH discriminator narrowing on a derived Include target is not recorded on the inner select, so it
    /// is admitted natively. No wrong data observed, but not proven safe.
    /// </para>
    /// </summary>
    internal void MarkSawNonBareJoinInner() => _sawNonBareJoinInner = true;

    internal bool SawNonBareJoinInner => _sawNonBareJoinInner;

    /// <summary>
    /// Whether nothing at all is recorded on this select (including a declined operator, which records no op yet
    /// disqualifies). Required of a reference-<c>Include</c> join's inner side, because the flat <c>$lookup</c> carries no sub-pipeline.
    /// </summary>
    /// <remarks>
    /// <see cref="_postJoinOps"/> must be checked: a reference-collection Count predicate records its <c>$match</c> there,
    /// and omitting it would silently drop the inner filter (<c>Orders.Join(Owners.Where(o =&gt; o.Orders.Count &gt; 1), ...)</c>).
    /// </remarks>
    internal bool IsBareCollectionScan
        => _pipelineOps.Count == 0 && HasNothingBeyondPipelineOps;

    /// <summary>
    /// Whether the only thing recorded is <see cref="MongoMatchOp"/>s. A join inner of this shape can carry its filter in
    /// the <c>$lookup</c> sub-pipeline (<see cref="LookupExpression.InnerPredicate"/>); any other op (ordering, paging,
    /// dedup, projection) changes which rows the inner contributes and can't. Shares
    /// <see cref="HasNothingBeyondPipelineOps"/> with <see cref="IsBareCollectionScan"/> so the two can't drift; the gate must call this, never restate it.
    /// </summary>
    internal bool IsFilterOnlyCollectionScan
        => _pipelineOps.Count > 0
           && _pipelineOps.All(op => op is MongoMatchOp)
           && HasNothingBeyondPipelineOps;

    private bool HasNothingBeyondPipelineOps
        => !_hasUnsupportedOperator
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
    /// The authoritative slot/projection representability decision; the gate's <c>ClassifyNativeDisposition</c> layers
    /// vector search and <see cref="IsGroupByFallbackUnsafe"/> on top. Fallback is forced by an unsupported operator, an
    /// unconfirmed candidate join, or a <see cref="ReferenceCollectionCountPredicateConfirmed"/> predicate combined with a set
    /// op, grouping, join scope or unwind (a SelectMany's <c>$lookup</c> can land on the same path, corrupting the read side).
    /// </summary>
    internal NativeRoute Route
        => _hasUnsupportedOperator || HasUnconfirmedCandidateJoin
            || (_referenceCollectionCountPredicateConfirmed
                && (SetOperation != null || Grouping != null || JoinScope != null || _unwindSources.Count > 0))
            ? NativeRoute.Fallback
            // A GroupBy key was bound but no aggregate Select finalized it: fall back rather than emit an ungrouped scan.
            : PendingGroupKey != null && Grouping == null ? NativeRoute.Fallback
            : Cardinality?.Aggregate != null ? NativeRoute.ScalarAggregate
            : Grouping != null ? NativeRoute.GroupBy
            : _projections.Count > 0 ? NativeRoute.Projection
            : NativeRoute.WholeEntity;
}

/// <summary>Which alias family a projection-alias override belongs to; read by the late-fallback strip instead of inspecting the alias string.</summary>
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

/// <summary>The native-execution route the compile-time gate takes, derived from <see cref="MongoSelectDefinition.Route"/>.</summary>
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
