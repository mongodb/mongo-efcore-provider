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

using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;

namespace MongoDB.EntityFrameworkCore.Query.Expressions;

/// <summary>
/// The native-translation logical query IR (filter / sort / paging / projection) for a single collection.
/// Populated by <c>NativeSlotPopulator</c> / <c>NativeProjectionBinder</c> (invoked by the QMTEV), read by the
/// compile-time gate and the lowerer. Dialect-neutral: holds <see cref="MongoExpression"/> nodes, never BSON.
/// </summary>
/// <remarks>
/// A plain data-holder, not a <see cref="System.Linq.Expressions.Expression"/> — hence <c>Definition</c>
/// rather than <c>Expression</c> in the name, which is reserved for types that actually derive from
/// <see cref="System.Linq.Expressions.Expression"/>. Composed into <see cref="MongoQueryExpression"/> via its
/// <see cref="MongoQueryExpression.Select"/> property. Cross-collection <c>$lookup</c> state stays on
/// <see cref="MongoQueryExpression"/> (it is entangled with the driver-LINQ fallback shaper).
/// </remarks>
internal sealed class MongoSelectDefinition
{
    private readonly List<MongoProjection> _projections = [];

    // ── Ordered filter/sort/page pipeline ─────────────────────────────────────────
    private readonly List<MongoSelectOp> _pipelineOps = [];

    /// <summary>
    /// The ordered filter/sort/page operations, emitted verbatim by the lowerer. Arrival order IS emission
    /// order — this is what represents non-canonical Skip/Take. Terminal shapes (projection/grouping/
    /// cardinality/set-op/unwind) still follow this block; see <see cref="Route"/> and the lowerer.
    /// </summary>
    public IReadOnlyList<MongoSelectOp> PipelineOps => _pipelineOps;

    // ── Trailing ops (post-set-op composition) ─────────────────────────────────────
    // A SECOND ordered filter/sort/page list, emitted by the lowerer AFTER the set-op stage. A set op is
    // terminal for every operator except Where/OrderBy/ThenBy/Skip/Take + aggregates/reducers, which record
    // here instead of PipelineOps so they filter/sort/page the COMBINED set-op result, not source1's
    // pre-set-op rows. Once SetOperation is attached, ActiveOps is _trailingOps (see below).
    private readonly List<MongoSelectOp> _trailingOps = [];

    /// <summary>
    /// The ordered filter/sort/page operations recorded AFTER a set op was attached. The lowerer emits these
    /// verbatim after the set-op stage. Empty for every non-set-op query and for a set op with no
    /// post-composition.
    /// </summary>
    public IReadOnlyList<MongoSelectOp> TrailingOps => _trailingOps;

    /// <summary>
    /// The op list the five merge methods currently target: <see cref="TrailingOps"/> once a set op has been
    /// attached (so post-set-op ops are trailing), otherwise <see cref="PipelineOps"/> (source1's own /
    /// pre-terminal ops). The single flip point for the post-set-op composition machinery.
    /// </summary>
    private List<MongoSelectOp> ActiveOps => SetOperation != null ? _trailingOps : _pipelineOps;

    /// <summary>
    /// ANDs <paramref name="conjunct"/> into the tail <see cref="MongoMatchOp"/> if the last op is one
    /// (so consecutive Where's merge into a single $match); otherwise appends a new <see cref="MongoMatchOp"/>
    /// at the current tail (so a Where/OfType/aggregate-predicate applied AFTER a sort or paging lands as a
    /// later $match — the sequential semantics MongoDB's pipeline gives us). Targets <see cref="ActiveOps"/>.
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
    /// OrderBy: if the tail op is a <see cref="MongoSortOp"/>, REPLACE it (a fresh primary sort, so
    /// <c>OrderBy(a).OrderBy(b)</c> keeps only b); otherwise append a new sort (e.g. an OrderBy after paging).
    /// Targets <see cref="ActiveOps"/>.
    /// </summary>
    public void StartOrReplaceSort(MongoOrdering first)
    {
        var ops = ActiveOps;
        if (ops.Count > 0 && ops[^1] is MongoSortOp)
            ops[^1] = new MongoSortOp([first]);
        else
            ops.Add(new MongoSortOp([first]));
    }

    /// <summary>ThenBy: extends the current (tail) sort. LINQ typing puts an OrderBy/ThenBy immediately before
    /// a ThenBy, so the tail op is normally a <see cref="MongoSortOp"/> and this appends <paramref name="next"/>
    /// to it. The one exception is when the preceding OrderBy/ThenBy could NOT be translated to a field (e.g. an
    /// owned sub-property key) — that arm calls <see cref="MarkNotNativelyRepresentable"/> and appends no sort op.
    /// In that case the tail is not a sort op; start a fresh one so this never throws. The recorded op is inert
    /// — a Fallback query never lowers. Targets <see cref="ActiveOps"/>.
    ///
    /// <paramref name="next"/> is dropped, rather than appended, when it re-orders by a
    /// <see cref="MongoFieldExpression"/> already present earlier in the same sort (same
    /// <see cref="MongoFieldExpression.ElementName"/>) — once a field fully determines the sort order,
    /// re-ordering by it again is a no-op, and rendering both into one <c>$sort</c> document would collide on a
    /// duplicate field name (EF-253 / CSHARP-5690; mirrors the driver-LINQ bridge's
    /// <c>MongoQueryableMethodTranslatingExpressionVisitor.ElideRedundantOrderings</c>). A computed key (not a
    /// bare <see cref="MongoFieldExpression"/>) is never elided, matching that method's single-hop restriction.
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

    /// <summary>Skip → append a <see cref="MongoSkipOp"/> to <see cref="ActiveOps"/>.</summary>
    public void AppendSkip(MongoExpression count) => ActiveOps.Add(new MongoSkipOp(count));

    /// <summary>Take (and the synthesized reducer limit) → append a <see cref="MongoLimitOp"/> to
    /// <see cref="ActiveOps"/>.</summary>
    public void AppendLimit(MongoExpression count) => ActiveOps.Add(new MongoLimitOp(count));

    // HasPaging/HasOrdering/HasLimit deliberately scan _pipelineOps only: they gate a PRE-terminal GroupBy
    // (NativeGroupByBinder), which is unreachable after a set op (a trailing GroupBy is rejected by
    // HasTerminalOperator), so they must not see the post-set-op _trailingOps.
    /// <summary><see langword="true"/> when any $skip or $limit op is present.</summary>
    internal bool HasPaging => _pipelineOps.Exists(o => o is MongoSkipOp or MongoLimitOp);

    /// <summary><see langword="true"/> when any $sort op is present.</summary>
    internal bool HasOrdering => _pipelineOps.Exists(o => o is MongoSortOp);

    /// <summary>
    /// <see langword="true"/> when any $limit op is present.
    /// <para>
    /// No production call site as of EF-397: <c>NativeCardinalityBinder.TryBindReducer</c> was the sole
    /// consumer and its guard was deleted (a reducer's own <c>$limit</c> composes with a preceding Take's —
    /// consecutive <c>$limit</c> stages narrow monotonically). Kept as the sibling of
    /// <see cref="HasPaging"/>/<see cref="HasOrdering"/>, but do NOT reintroduce it as a "a limit already
    /// exists, so decline" test without re-deriving why that would be true — it was not.
    /// </para>
    /// </summary>
    internal bool HasLimit => _pipelineOps.Exists(o => o is MongoLimitOp);

    // ── Projection ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The output fields of a server-side <c>$project</c> stage, in order. Empty means no projection
    /// (whole-entity results) — the entity path never populates this.
    /// </summary>
    public IReadOnlyList<MongoProjection> Projection => _projections;

    /// <summary>
    /// Appends <paramref name="projection"/> to the projection list.
    /// </summary>
    public void AddProjection(MongoProjection projection)
        => _projections.Add(projection);

    /// <summary>
    /// Clears the projection list. Used by <c>NativeGroupByBinder.TryBindDistinctFromProjection</c> to
    /// replace a terminal <c>$project</c>'s output fields with the flattening projection that reads the
    /// value back out of the degenerate-<c>$group</c> <c>_id</c>.
    /// </summary>
    internal void ClearProjections() => _projections.Clear();

    // ── Projection-alias overrides ──────────────────────────────────────────────────
    //
    // The ONE fact, written once by the emit side and read by every site that would otherwise derive a
    // $project alias (and therefore the element name the DOM shaper reads by) from
    // ProjectionMember.Last?.Name. Empty ⇒ every one of those sites behaves as if no override existed.
    //
    // A MAP rather than a single "bare projection alias" string: a NAMED member can also need the same
    // alias/document-path decoupling ("Notes" -> "Home.Notes"), so a keyed table lets that case add binder
    // logic without touching any alias-reading site.

    /// <summary>
    /// The override-table key standing in for a BARE selector body, which has no member name at all.
    /// A LITERAL SENTINEL, not <see langword="null"/>: <see cref="Dictionary{TKey,TValue}"/> rejects a null
    /// key (<see cref="System.ArgumentNullException"/> on both <c>Add</c> and <c>ContainsKey</c>), and the
    /// leading space makes it unrepresentable as a real CLR member name, so it cannot collide with a member
    /// key registered for a named projection member.
    /// </summary>
    internal const string BareProjectionMemberKey = " bare";

    private Dictionary<string, (string Alias, ProjectionAliasTier Tier)>? _projectionAliasOverrides;

    /// <summary>
    /// Overrides the <c>$project</c> OUTPUT ELEMENT NAME (and therefore the name the DOM shaper reads by)
    /// for a projection member, keyed by the member's own name — <see cref="BareProjectionMemberKey"/> for a
    /// BARE selector body. Written ONLY by <c>NativeProjectionBinder</c>, in the same commit block as the
    /// matching <see cref="AddProjection"/>, so "the emit gate opened" and "the override exists" are the same
    /// event rather than two events to keep ordered.
    /// </summary>
    /// <param name="memberName">
    /// The projection member's own name, or <see cref="BareProjectionMemberKey"/> for a bare selector body.
    /// </param>
    /// <param name="alias">The output element name to emit and to read back by.</param>
    /// <param name="tier">
    /// Whether <paramref name="alias"/> is the leaf's root-relative document path
    /// (<see cref="ProjectionAliasTier.DocumentPath"/>) or a synthetic name
    /// (<see cref="ProjectionAliasTier.Synthetic"/>). Carried as DATA because the late-fallback strip is
    /// tier-conditional; sniffing the alias STRING there would re-create a second, independently derived
    /// copy of a fact the emit side already knows, which is the failure mode this carrier exists to remove.
    /// </param>
    /// <remarks>
    /// WRITE-ONCE, enforced by <see cref="Dictionary{TKey,TValue}.Add"/> rather than an indexer assignment: a
    /// second write for the same member would mean the emit side committed two different aliases for one
    /// projection member, so the emitted <c>$project</c> key and the name the shaper reads by could silently
    /// disagree — a missed read returns <see langword="null"/> for a nullable/reference leaf and an empty
    /// collection for an array leaf, with no exception anywhere. Failing loudly here is strictly better than
    /// that. <c>NativeProjectionBinder.TryPopulateNativeProjection</c> declines a bare body outright when
    /// <see cref="Projection"/> is already populated, so a second write is currently unreachable rather than
    /// merely unlikely; if a future writer widens this, the <c>Add</c> will surface it immediately.
    /// </remarks>
    internal void AddProjectionAliasOverride(string memberName, string alias, ProjectionAliasTier tier)
        => (_projectionAliasOverrides ??= new Dictionary<string, (string, ProjectionAliasTier)>()).Add(
            memberName, (alias, tier));

    /// <summary>
    /// Looks up the registered alias override for <paramref name="memberName"/>, mapping
    /// <see langword="null"/> (a BARE selector body, whose <c>ProjectionMember</c> has no last member) onto
    /// <see cref="BareProjectionMemberKey"/>. The parameter is deliberately nullable so an alias-reading site
    /// can pass <c>projectionMember.Last?.Name</c> straight through — that keeps the null handling in exactly
    /// one place instead of at every call site.
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
    /// <see langword="true"/> when a BARE selector body populated <see cref="Projection"/>.
    /// </summary>
    internal bool IsBareProjection
        => _projectionAliasOverrides?.ContainsKey(BareProjectionMemberKey) == true;

    /// <summary>
    /// The tier of the bare-body override, or <see langword="null"/> when there is no bare-body override.
    /// Read by the late native-factory-failure fallback in
    /// <c>MongoShapedQueryCompilingExpressionVisitor</c>: a <see cref="ProjectionAliasTier.DocumentPath"/>
    /// alias is readable off a WHOLE document, a <see cref="ProjectionAliasTier.Synthetic"/> one is not.
    /// </summary>
    internal ProjectionAliasTier? BareProjectionTier
        => _projectionAliasOverrides != null
           && _projectionAliasOverrides.TryGetValue(BareProjectionMemberKey, out var entry)
            ? entry.Tier
            : null;

    /// <summary>
    /// <see langword="true"/> when ANY registered override — bare or named — is a
    /// <see cref="ProjectionAliasTier.DocumentPath"/> alias, i.e. when the shaper reads at least one leaf by a
    /// name the driver-LINQ bridge would NOT emit for the same projection member. Read by the late
    /// native-factory-failure strip in <c>MongoShapedQueryCompilingExpressionVisitor</c>.
    /// </summary>
    /// <remarks>
    /// Deliberately keyed on the TIER rather than on which member the override belongs to. Every override
    /// family reaches the same conclusion for the same reason: the emit side picked a name the driver would
    /// not pick (<c>_v</c> for a bare body, the member name for an <c>OwnsOne</c>-hop array leaf), and a
    /// <see cref="ProjectionAliasTier.DocumentPath"/> alias is readable off a whole document, so removing the
    /// pushed-down <c>Select</c> is what makes the fallback's read hit. Asking "is it the bare override?"
    /// instead would silently mishandle any other family with the same tier.
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

    // ── Cardinality / aggregate ───────────────────────────────────────────────────

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
            // Mutually exclusive with Grouping: a scalar aggregate/reducer set on an already-grouped select
            // would flip Route to ScalarAggregate (prioritized above GroupBy) while the lowerer still emits
            // the [$group, $project] grouping pipeline, so the scalar shaper would read a nonexistent element
            // and crash. NativeCardinalityBinder.TryBindAggregate/TryBindReducer gate on IsGroupBy to prevent
            // this at population; this assert catches any future path that forgets it. Both may legitimately
            // co-occur with Projection, so that is not asserted.
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
    /// Group key parts parsed by <c>NativeGroupByBinder.TryBindGroupKey</c> and consumed by
    /// <c>NativeGroupByBinder.TryBindGroupProjection</c> to build <see cref="Grouping"/>. This is transient
    /// binder-owned state, not itself part of <see cref="Route"/> — <see cref="Route"/> only turns to
    /// <see cref="NativeRoute.GroupBy"/> once <see cref="Grouping"/> is finalized.
    /// </summary>
    internal IReadOnlyList<MongoGroupingKeyPart>? PendingGroupKey { get; set; }

    // ── GroupBy provenance / fallback safety ──────────────────────────────────────

    /// <summary>
    /// <see langword="true"/> once a <c>GroupBy</c> operator has been seen on this query (set
    /// unconditionally by the QMTEV's <c>TranslateGroupBy</c>, whether or not the grouping bound
    /// natively). Used to recognize a grouped source feeding a <c>Join</c>/<c>GroupJoin</c>/<c>LeftJoin</c>
    /// — a shape whose driver-LINQ fallback silently returns wrong data (the joined row is empty for
    /// every group), so it must fail cleanly rather than fall back. See <see cref="IsGroupByFallbackUnsafe"/>.
    /// </summary>
    internal bool IsGroupBy { get; set; }

    /// <summary>
    /// <see langword="true"/> once a projected <c>Distinct</c> has bound natively on this query (set by
    /// <c>NativeGroupByBinder.TryBindDistinctFromProjection</c>). Distinct reuses the degenerate-<c>$group</c>
    /// machinery, so it shares the SAME post-group operator guards as <see cref="IsGroupBy"/> (both keyed on
    /// <c>IsGroupBy || IsDistinct</c>) — an operator applied AFTER the Distinct must fall back cleanly. It is a
    /// SEPARATE flag from <see cref="IsGroupBy"/> because the Join-family decline differs by provenance: a real
    /// <c>GroupBy</c> joined via driver-LINQ returns silently-wrong (empty) joins, so <c>TranslateJoinCore</c>
    /// HARD-declines it (<see cref="MarkGroupByFallbackUnsafe"/>); a projected <c>Distinct</c> is just a flat
    /// set of rows the driver-LINQ path joins correctly, so <c>Distinct</c>-then-<c>Join</c> must instead fall
    /// back GRACEFULLY (<see cref="MarkNotNativelyRepresentable"/>). Keeping the flags distinct lets
    /// <c>TranslateJoinCore</c> pick the right (hard vs. graceful) path per provenance.
    /// </summary>
    internal bool IsDistinct { get; set; }

    /// <summary>
    /// <see langword="true"/> once this query has seen ANY native terminal grouping/distinct/set-op
    /// provenance — <see cref="IsGroupBy"/>, <see cref="IsDistinct"/>, <see cref="IsSetOp"/>, or a finalized
    /// <see cref="Grouping"/>. Centralizes the post-terminal gate that is otherwise duplicated across
    /// <c>NativeCardinalityBinder</c>, <c>NativeSlotPopulator</c>, and the QMTEV's
    /// <c>TranslateSelect</c>/<c>TranslateGroupBy</c>: any operator reached after a native <c>GroupBy</c>,
    /// projected <c>Distinct</c>, or terminal <c>Union</c>/<c>Concat</c> must fall back rather than resolve
    /// against the base entity type and silently emit a pre-<c>$group</c>/pre-<c>$unionWith</c> stage.
    /// <c>Grouping != null</c> is included for completeness (a finalized grouping always also sets
    /// <see cref="IsGroupBy"/> or <see cref="IsDistinct"/> by construction, so it's a no-op in practice).
    /// <see cref="UnwindSource"/> joins the same gate: a native owned-collection SelectMany is terminal-only,
    /// exactly like Distinct/GroupBy/Union/Concat.
    /// </summary>
    internal bool HasTerminalOperator => IsGroupBy || IsDistinct || IsSetOp || Grouping != null || UnwindSources.Count > 0;

    /// <summary>
    /// <see langword="true"/> when the ONLY terminal on this select is a set operation — a set op is attached
    /// and no grouping/distinct/unwind terminal is. A set op only ever attaches to a plain whole-entity select,
    /// so <see cref="IsSetOp"/> already implies the rest; the explicit conjunction is defensive so this can
    /// never accidentally open a GroupBy/Distinct/SelectMany terminal. Used to relax the two catch-all
    /// post-terminal guards (NativeSlotPopulator, NativeCardinalityBinder) for operators composed after a set
    /// op, while every deferred operator's own HasTerminalOperator guard stays tripped.
    /// The <c>Projection.Count == 0</c> conjunct makes this read as "a set op is the ONLY thing done so far":
    /// it stays true while a trailing projection is being pushed down (Projection is still empty at that
    /// moment, so TranslateSelect admits the projection), then flips to false once the projection is
    /// populated — so any operator composed AFTER the trailing projection falls back rather than resolving
    /// against the entity type.
    /// </summary>
    internal bool IsSetOpTerminalOnly
        => IsSetOp && !IsGroupBy && !IsDistinct && Grouping == null && UnwindSources.Count == 0 && Projection.Count == 0;

    /// <summary>
    /// The Atlas <c>$vectorSearch</c> anchoring this query, or <see langword="null"/> when it has none.
    /// </summary>
    /// <remarks>
    /// A DEDICATED slot rather than a <see cref="PipelineOps"/> entry: the server requires
    /// <c>$vectorSearch</c> to be the FIRST stage of the pipeline, and the lowerer emits
    /// <see cref="PipelineOps"/> verbatim in arrival order — so a vector search recorded there would only
    /// HAPPEN to come first. Its own slot, emitted ahead of the op list, makes first-ness structural.
    /// It deliberately does NOT join <see cref="HasTerminalOperator"/>: a vector search is a root ANCHOR, not
    /// a terminal — a <c>Where</c>/<c>OrderBy</c>/paging composed after it keeps recording into
    /// <see cref="PipelineOps"/> exactly as over a plain collection scan. <see cref="Route"/> is unaffected
    /// too: a bare vector search stays <see cref="NativeRoute.WholeEntity"/>, and one with a bound projection
    /// stays <see cref="NativeRoute.Projection"/>.
    /// </remarks>
    internal MongoVectorSearch? VectorSearch { get; set; }

    /// <summary>The terminal set operation (Union/Concat), when this select is a set-op query.</summary>
    internal MongoSetOperation? SetOperation { get; set; }

    /// <summary>
    /// <see langword="true"/> when a terminal set operation is attached. A SEPARATE provenance flag (like
    /// <see cref="IsDistinct"/>) that joins the post-terminal guard <see cref="HasTerminalOperator"/> so any
    /// operator applied AFTER the union falls back (terminal-only scope).
    /// </summary>
    internal bool IsSetOp { get; set; }

    /// <summary>
    /// <see langword="true"/> when <see cref="Projection"/> contains an owned entity-COLLECTION array leaf
    /// (e.g. <c>Select(b =&gt; new { b.Title, b.Posts })</c>). Provenance only: it records what the projection
    /// CONTAINS, and nothing on the ordinary projection path reads it.
    /// </summary>
    /// <remarks>
    /// Exists for one consumer — the projected-set-op-OPERAND scope gate
    /// (<c>MongoQueryableMethodTranslatingExpressionVisitor.IsPlainProjectedSelect</c>) — which must DECLINE
    /// such a projection as a set-op operand: an array leaf forces the owner key into the projected document,
    /// and a projected-operand set op dedups/source-tags over that whole projected document by value, so the
    /// leaked <c>_id</c> would silently change the set operation's semantics from value-based to
    /// identity-based. A TRAILING projection after a whole-entity set op is unaffected and stays native — its
    /// dedup runs over whole entities BEFORE the <c>$project</c>, so neither the array nor the owner key ever
    /// reaches the value comparison.
    /// </remarks>
    internal bool HasArrayProjectionLeaf { get; set; }

    private readonly List<MongoUnwindSource> _unwindSources = [];

    /// <summary>
    /// The ordered chain of terminal SelectMany unwind sources. Index 0 is the first (outermost) SelectMany's
    /// unwind; index 1, when present, is a SECOND, chained SelectMany's own unwind — correlated off the
    /// first's unwound element (see
    /// <see cref="NativeTranslation.NativeSelectManyBinder.TryBindNestedReferenceNavUnwind"/>). A single-level
    /// SelectMany populates exactly one entry (see the <see cref="UnwindSource"/> shim below); a 2-level
    /// nested reference SelectMany populates two. Write only via <see cref="AddUnwindSource"/> — there is no
    /// direct setter, so every write site is grep-visible.
    /// </summary>
    public IReadOnlyList<MongoUnwindSource> UnwindSources => _unwindSources;

    /// <summary>Appends a new terminal SelectMany unwind source to the chain.</summary>
    internal void AddUnwindSource(MongoUnwindSource source) => _unwindSources.Add(source);

    /// <summary>
    /// The LAST (most-recently-appended) terminal SelectMany unwind source, or <see langword="null"/> when
    /// none is set — a read-only "last source" shim over <see cref="UnwindSources"/> that keeps every
    /// single-source read site (the lowerer, the whole-element gate in <c>TranslateSelect</c>, both
    /// projection binders) simple: every current consumer cares about the TERMINAL unwind source, which for a
    /// single-level SelectMany is its only source and for the 2-level nested case is the SECOND (innermost)
    /// source. There is no setter — write via <see cref="AddUnwindSource"/>.
    /// </summary>
    internal MongoUnwindSource? UnwindSource => _unwindSources.Count > 0 ? _unwindSources[^1] : null;

    /// <summary>
    /// <see langword="true"/> when the terminal seen so far on this select is EXACTLY a single REFERENCE
    /// unwind source, with no grouping/distinct/set-op mixed in. This is the narrow carve-out condition
    /// <c>TranslateSelectMany</c> checks BEFORE its ordinary <see cref="HasTerminalOperator"/> guard: only
    /// when this holds does a SECOND, chained SelectMany get a chance at nested-reference recognition (see
    /// <see cref="NativeTranslation.NativeSelectManyBinder.TryBindNestedReferenceNavUnwind"/>); every other
    /// post-terminal shape (a 2nd SelectMany after GroupBy/Distinct/a set-op/an OWNED unwind, or a query
    /// already 2+ levels deep) still hits the unmodified guard.
    /// </summary>
    internal bool IsSingleReferenceUnwindTerminalOnly
        => UnwindSources.Count == 1 && UnwindSources[0].Kind == MongoUnwindSourceKind.Reference
           && !IsGroupBy && !IsDistinct && !IsSetOp && Grouping == null;

    private bool _isGroupByFallbackUnsafe;

    /// <summary>
    /// <see langword="true"/> when this query combines a <c>GroupBy</c> with a <c>Join</c> family operator
    /// producing a non-entity result. The native path cannot represent it, and — unlike an ordinary
    /// unsupported shape — its driver-LINQ fallback executes and returns <em>silently wrong</em> data
    /// (the joined entity is empty for every grouped row). The gate therefore throws
    /// <c>NativeTranslationNotSupportedException</c> for this shape under <c>Native</c>/<c>NativeOnly</c>
    /// rather than routing to the wrong-data fallback (explicit <c>DriverLinq</c> is the user's opt-in and
    /// is left untouched).
    /// </summary>
    internal bool IsGroupByFallbackUnsafe => _isGroupByFallbackUnsafe;

    /// <summary>
    /// Records that this query is a <c>GroupBy</c> combined with a <c>Join</c> family operator, whose
    /// driver-LINQ fallback would silently return wrong data. Forces the gate to fail cleanly (see
    /// <see cref="IsGroupByFallbackUnsafe"/>). Also marks the query non-native.
    /// </summary>
    internal void MarkGroupByFallbackUnsafe()
    {
        _isGroupByFallbackUnsafe = true;
        _hasUnsupportedOperator = true;
    }

    /// <summary>
    /// <see langword="true"/> when ANY wrong-data-on-fallback provenance has been recorded — today exactly a
    /// GroupBy combined with a join (<see cref="IsGroupByFallbackUnsafe"/>). This is the single signal the gate
    /// reads: it means "the driver-LINQ fallback executes and returns wrong rows", so it hard-declines. See
    /// <c>MongoShapedQueryCompilingExpressionVisitor.ClassifyNativeDisposition</c>.
    /// </summary>
    internal bool IsFallbackWrongData => _isGroupByFallbackUnsafe;

    /// <summary>
    /// Copies any wrong-data provenance from <paramref name="inner"/> onto this select. A join whose inner is
    /// itself a SUBQUERY containing an offending shape records the verdict on the INTERMEDIATE
    /// <c>MongoQueryExpression</c>, and the gate only ever reads the OUTERMOST one, so without propagation a
    /// nested offending shape would silently execute and return wrong rows where the same shape promoted to
    /// top level correctly declines. Propagation makes the verdict nesting-insensitive <em>along join-inner
    /// chains</em> only — this has exactly one call site, <c>TranslateJoinCore</c>, so a verdict recorded on a
    /// subquery used in any position OTHER than a join's inner still never reaches the gate. This closes an
    /// independent nesting hole (EF-344) and is permanent.
    /// </summary>
    internal void PropagateFallbackWrongDataFrom(MongoSelectDefinition inner)
    {
        if (inner._isGroupByFallbackUnsafe)
        {
            MarkGroupByFallbackUnsafe();
        }
    }

    // ── Native-representable gate ─────────────────────────────────────────────────

    private bool _hasUnsupportedOperator;

    /// <summary>
    /// Records that this query contains a shape the native path cannot handle, forcing
    /// <see cref="Route"/> to <see cref="NativeRoute.Fallback"/>. Population-time signal set by the
    /// slot populator / projection binder / QMTEV overrides; never unset.
    /// </summary>
    internal void MarkNotNativelyRepresentable()
        => _hasUnsupportedOperator = true;

    // ── Reference-Include candidate join ────────────────────────────────────────────
    //
    // PopulateNativeSlots visits the JOIN node before the trailing Select that identifies a reference
    // Include, so the gate has to decide on the join before the IncludeExpression has been seen. Because
    // _hasUnsupportedOperator is never unset (by design), "mark non-native at the join then un-mark at the
    // Select" is not available. Instead the two signals below are recorded and Route COMPUTES the decision,
    // the same way UsesDriverJoinFields computes the document shape rather than tracking it as mutable state.
    //
    // DEFAULT-DENY: a user join with no trailing Include, or one whose Include fails any recognizer conjunct,
    // is never confirmed and therefore routes to Fallback.
    //
    // COUNTS, NOT FLAT BOOLEANS: a query can have MULTIPLE candidate joins — e.g. a multi-hop chain, or two
    // independent single-level reference Includes on the same query. A flat "confirmed" boolean cannot
    // distinguish "every candidate confirmed" from "one of several confirmed" — it would go true the moment
    // ANY join confirms, wrongly treating an untouched sibling candidate as admitted too and defeating
    // default-deny. Counting lets HasUnconfirmedCandidateJoin ask "are there exactly as many confirmations as
    // candidates?" rather than "has at least one confirmed?".
    //
    // NOT closed by InnerCollections.Count elsewhere in the gate: that dictionary is keyed by IEntityType, so
    // two joins against the SAME entity type collapse to ONE entry — a shape like
    // Orders.Include(o => o.Buyer).Join(db.Buyers, ...) slips past that guard too. The counts here are the
    // only place that distinguishes "one candidate" from "more than one".

    private int _candidateReferenceIncludeJoins;
    private int _confirmedReferenceIncludes;

    /// <summary>
    /// Records that a <c>Join</c>/<c>LeftJoin</c>/<c>GroupJoin</c> was seen which MIGHT be EF's
    /// nav-expansion of a single-level reference <c>Include</c>. Does not admit anything on its own.
    /// </summary>
    internal void MarkSawCandidateReferenceIncludeJoin()
        => _candidateReferenceIncludeJoins++;

    /// <summary>
    /// Records that a trailing <c>Select</c> was recognized as a single-level reference <c>Include</c>
    /// (see <c>MongoQueryableMethodTranslatingExpressionVisitor.IsSingleLevelReferenceIncludeSelector</c>),
    /// confirming ONE of the candidate joins recorded by <see cref="MarkSawCandidateReferenceIncludeJoin"/>.
    /// </summary>
    internal void MarkReferenceIncludeConfirmed()
        => _confirmedReferenceIncludes++;

    /// <summary>
    /// A candidate join that no trailing Include confirmed — the query must fall back. Strict inequality
    /// (<c>!=</c>, not <c>&gt;</c>): if confirmations ever exceeded candidates that would itself mean a
    /// confirmation arrived without a matching candidate join, a broken invariant that must also fail
    /// closed (force <see cref="NativeRoute.Fallback"/>) rather than be silently read as "all confirmed".
    /// </summary>
    internal bool HasUnconfirmedCandidateJoin
        => _candidateReferenceIncludeJoins != _confirmedReferenceIncludes;

    private bool _sawNonBareJoinInner;

    /// <summary>
    /// Records that some <c>Join</c>/<c>LeftJoin</c>/<c>GroupJoin</c> on this query had an INNER side that is
    /// not a bare collection scan — i.e. its own <see cref="MongoSelectDefinition"/> carried at least one
    /// recorded operation (a <c>$match</c>/<c>$sort</c>/<c>$skip</c>/<c>$limit</c> op, a projection, a
    /// terminal, a cardinality, or an operator that was declined outright). Set by the QMTEV's
    /// <c>TranslateJoinCore</c>, read by <c>TryConfirmReferenceInclude</c>.
    /// <para>
    /// This replaces an earlier metadata-only guard that consulted <c>navigation.TargetEntityType.GetQueryFilter()</c>,
    /// which misses a filter declared on the ROOT of a TPH hierarchy when read from a DERIVED target, and
    /// misses an EF10 <em>named</em> query filter (which lives in <c>GetDeclaredQueryFilters()</c> instead) —
    /// each gap would admit a reference <c>Include</c> whose filtered target the flat <c>$lookup</c> cannot
    /// filter, returning silently wrong rows in every query mode. Keying the decline on the INNER SELECT'S OWN
    /// SHAPE closes query filters in all spellings (anonymous, TPH-root-inherited, EF10 named) by
    /// construction rather than by enumerating metadata shapes.
    /// </para>
    /// <para>
    /// It does NOT close TPH discriminator narrowing: a TPH derived-type Include target is currently admitted
    /// natively, because EF does not record a discriminator predicate on the join's inner select for that
    /// shape. No wrong data has been observed for it, but that reflects the probes run, not a proof about the
    /// shape — treat it as a known open gap, not a closed case.
    /// </para>
    /// </summary>
    internal void MarkSawNonBareJoinInner() => _sawNonBareJoinInner = true;

    /// <summary>See <see cref="MarkSawNonBareJoinInner"/>.</summary>
    internal bool SawNonBareJoinInner => _sawNonBareJoinInner;

    /// <summary>
    /// Whether this select is a bare collection scan — nothing at all recorded on it. Used as the
    /// admissibility signal for a candidate reference-<c>Include</c> join's INNER side (see
    /// <see cref="MarkSawNonBareJoinInner"/>): the flat <c>$lookup</c> the reference-Include path emits can
    /// carry NO sub-pipeline, so the inner side must be the whole target collection and nothing else.
    /// Deliberately includes <c>_hasUnsupportedOperator</c>: an inner operator that was declined rather than
    /// lowered records no op at all, yet is exactly as disqualifying as one that did.
    /// </summary>
    internal bool IsBareCollectionScan
        => !_hasUnsupportedOperator
           && _pipelineOps.Count == 0
           && _trailingOps.Count == 0
           && _projections.Count == 0
           && Cardinality == null
           && Grouping == null
           && PendingGroupKey == null
           && SetOperation == null
           // A vector search is as disqualifying as any other recorded operation: the flat $lookup a
           // reference Include emits can carry no sub-pipeline, so an inner side anchored on one is not a
           // bare scan of the target collection. No reachable shape currently puts a vector search on a
           // join's INNER side (VectorSearch must sit at the query root), so this conjunct is
           // defence-in-depth rather than a live discriminator.
           && VectorSearch == null
           && !IsGroupBy
           && !IsDistinct
           && !IsSetOp
           && _unwindSources.Count == 0
           && _candidateReferenceIncludeJoins == 0
           && !_sawNonBareJoinInner;

    /// <summary>
    /// The single authoritative native-execution decision for this query, computed from the populated
    /// slots. <see cref="NativeRoute.Fallback"/> when any unsupported operator was seen; otherwise
    /// <see cref="NativeRoute.Projection"/> when a <c>$project</c> was populated; otherwise
    /// <see cref="NativeRoute.WholeEntity"/>. This is authoritative for <em>slot/projection</em>
    /// representability; the full is-native decision is the gate's <c>ClassifyNativeDisposition</c>, which
    /// layers vector search (<c>ContainsVectorSearch</c> over the captured chain — not on
    /// <see cref="MongoSelectDefinition"/>) and the GroupBy+Join hard-decline
    /// (<see cref="IsGroupByFallbackUnsafe"/>) onto this route. <c>$lookup</c> streamability is a separate
    /// axis (streaming-vs-DOM), not an is-native signal. An unconfirmed reference-Include candidate join
    /// also forces Fallback — see <see cref="HasUnconfirmedCandidateJoin"/>.
    /// </summary>
    internal NativeRoute Route
        => _hasUnsupportedOperator || HasUnconfirmedCandidateJoin ? NativeRoute.Fallback
            // A GroupBy key was bound but no aggregate Select finalized the grouping (e.g. a bare GroupBy(key)
            // that terminates on the IGrouping sequence, or a group followed by an unsupported operator): the
            // native path cannot represent this, so fall back rather than silently emit an ungrouped scan.
            : PendingGroupKey != null && Grouping == null ? NativeRoute.Fallback
            : Cardinality?.Aggregate != null ? NativeRoute.ScalarAggregate
            : Grouping != null ? NativeRoute.GroupBy
            : _projections.Count > 0 ? NativeRoute.Projection
            : NativeRoute.WholeEntity;
}

/// <summary>
/// Which alias family a registered projection-alias override belongs to — read as DATA by the late-fallback
/// strip, so that decision never has to be re-derived by inspecting the alias string.
/// </summary>
internal enum ProjectionAliasTier
{
    /// <summary>
    /// The alias IS the leaf's root-relative document path, so reading that element off a WHOLE (un-projected)
    /// document is the same read as reading it off the projected one.
    /// </summary>
    DocumentPath,

    /// <summary>
    /// A computed leaf with no document path, carrying a synthetic alias. NOT whole-document-readable.
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
