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

namespace MongoDB.EntityFrameworkCore.Query.Expressions;

/// <summary>
/// The native-translation logical query IR (filter / sort / paging / projection) for a single collection —
/// the "MongoSelectExpression" of the EF-323 design. Populated by <c>NativeSlotPopulator</c> /
/// <c>NativeProjectionBinder</c> (invoked by the QMTEV), read by the compile-time gate and the lowerer.
/// Dialect-neutral: holds <see cref="MongoExpression"/> nodes, never BSON.
/// </summary>
/// <remarks>
/// This is a plain data-holder, NOT a <see cref="System.Linq.Expressions.Expression"/> — hence the
/// <c>Definition</c> name rather than the design document's "MongoSelectExpression": the
/// <c>Expression</c> suffix is reserved for types that actually derive from
/// <see cref="System.Linq.Expressions.Expression"/>. The <see cref="MongoExpression"/> base already
/// carries dead/inconsistent visitor plumbing; there is nothing to gain from repeating it here, so
/// this is a plain <see langword="internal"/> <see langword="sealed"/> class. It is composed into
/// <see cref="MongoQueryExpression"/> via its <see cref="MongoQueryExpression.Select"/> property.
/// Cross-collection <c>$lookup</c> state stays on <see cref="MongoQueryExpression"/> (it is entangled
/// with the driver-LINQ fallback shaper).
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

    // ── Trailing ops (post-set-op composition, EF-347 slice B) ────────────────────
    // A SECOND ordered filter/sort/page list, emitted by the lowerer AFTER the set-op stage. A set op is
    // terminal for everything except the operators relaxed in slice B (Where/OrderBy/ThenBy/Skip/Take +
    // aggregates/reducers); those record here instead of PipelineOps so they filter/sort/page the COMBINED
    // set-op result, not source1's pre-set-op rows. The flip point is single and well-defined: once
    // SetOperation is attached, ActiveOps is _trailingOps (see below).
    private readonly List<MongoSelectOp> _trailingOps = [];

    /// <summary>
    /// The ordered filter/sort/page operations recorded AFTER a set op was attached (EF-347 slice B). The
    /// lowerer emits these verbatim after the set-op stage. Empty for every non-set-op query and for a set op
    /// with no post-composition.
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
    /// later $match — the sequential semantics MongoDB's pipeline gives us). Targets <see cref="TrailingOps"/>
    /// once a set op is attached (EF-347 slice B), else <see cref="PipelineOps"/>.
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
    /// OrderBy: if the tail op is a <see cref="MongoSortOp"/>, REPLACE it (a fresh primary sort, reproducing
    /// the previous ResetOrderings semantics, so <c>OrderBy(a).OrderBy(b)</c> keeps only b); otherwise append
    /// a new sort (e.g. an OrderBy after paging). Targets <see cref="TrailingOps"/> once a set op is attached
    /// (EF-347 slice B), else <see cref="PipelineOps"/>.
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
    /// owned sub-property key) — that arm calls <see cref="MarkNotNativelyRepresentable"/> and appends no sort op,
    /// so this query is already <see cref="NativeRoute.Fallback"/>. In that case the tail is not a sort op; start
    /// a fresh one so this never throws (matching the old append-always <c>AppendOrdering</c> behavior). The
    /// recorded op is inert — a Fallback query never lowers — so this cannot change the native pass-set. Targets
    /// <see cref="TrailingOps"/> once a set op is attached (EF-347 slice B), else <see cref="PipelineOps"/>.</summary>
    public void AppendThenBy(MongoOrdering next)
    {
        var ops = ActiveOps;
        if (ops.Count > 0 && ops[^1] is MongoSortOp sort)
            ops[^1] = new MongoSortOp([.. sort.Orderings, next]);
        else
            ops.Add(new MongoSortOp([next]));
    }

    /// <summary>Skip → append a <see cref="MongoSkipOp"/>. Targets <see cref="TrailingOps"/> once a set op is
    /// attached (EF-347 slice B), else <see cref="PipelineOps"/>.</summary>
    public void AppendSkip(MongoExpression count) => ActiveOps.Add(new MongoSkipOp(count));

    /// <summary>Take (and the synthesized reducer limit) → append a <see cref="MongoLimitOp"/>. Targets
    /// <see cref="TrailingOps"/> once a set op is attached (EF-347 slice B), else <see cref="PipelineOps"/>.</summary>
    public void AppendLimit(MongoExpression count) => ActiveOps.Add(new MongoLimitOp(count));

    // HasPaging/HasOrdering/HasLimit deliberately scan _pipelineOps only: they gate a PRE-terminal GroupBy
    // (NativeGroupByBinder), which is unreachable after a set op (a trailing GroupBy is rejected by
    // HasTerminalOperator), so they must not see the post-set-op _trailingOps (EF-347 slice B).
    /// <summary><see langword="true"/> when any $skip or $limit op is present.</summary>
    internal bool HasPaging => _pipelineOps.Exists(o => o is MongoSkipOp or MongoLimitOp);

    /// <summary><see langword="true"/> when any $sort op is present.</summary>
    internal bool HasOrdering => _pipelineOps.Exists(o => o is MongoSortOp);

    /// <summary><see langword="true"/> when any $limit op is present.</summary>
    internal bool HasLimit => _pipelineOps.Exists(o => o is MongoLimitOp);

    private bool _sawUnrecordedPaging;

    /// <summary>
    /// Records that a <c>Skip</c>/<c>Take</c> was SEEN on this sequence but NOT recorded as a
    /// <see cref="MongoSkipOp"/>/<see cref="MongoLimitOp"/> — the operator was declined (the query falls back to
    /// driver-LINQ) rather than lowered. The paging is still in the captured method chain the fallback executes,
    /// so it still reaches the driver and CSHARP-6017 still applies; <see cref="HasPagingAnywhere"/> must
    /// therefore see it. Deliberately does NOT mark the query non-native — every caller already does that for
    /// its own reason.
    /// TODO(CSHARP-6017): delete together with <see cref="HasPagingAnywhere"/> and
    /// <see cref="MarkPagedJoinInnerFallbackUnsafe"/> when the driver stops folding.
    /// </summary>
    internal void MarkSawUnrecordedPaging() => _sawUnrecordedPaging = true;

    /// <summary>
    /// <see langword="true"/> when a <c>$skip</c> or <c>$limit</c> op is present in EITHER op list, OR a
    /// <c>Skip</c>/<c>Take</c> was seen but declined rather than recorded (see
    /// <see cref="MarkSawUnrecordedPaging"/>). Unlike <see cref="HasPaging"/> — which deliberately scans
    /// <c>_pipelineOps</c> only, because its consumer gates a PRE-terminal GroupBy that is unreachable after a
    /// set op — this must see paging wherever it appeared, including a <c>Take</c> composed AFTER a set operation
    /// (which lands in <see cref="TrailingOps"/>) and a <c>Take</c> composed after a NON-set-op terminal (which
    /// lands in neither list).
    /// Read by the QMTEV's <c>TranslateJoinCore</c> CSHARP-6017 guard. The question it answers is "was a
    /// <c>Skip</c>/<c>Take</c> RECORDED ANYWHERE on this sequence?", which is the right question because the
    /// guard's real subject is the captured method chain the driver-LINQ fallback executes, not the native op
    /// lists — a <c>Skip</c>/<c>Take</c> the native path declined is still in that chain and still folded.
    /// TODO(CSHARP-6017): delete together with <see cref="MarkPagedJoinInnerFallbackUnsafe"/> when the driver
    /// stops folding an uncorrelated join inner's paging into the correlated <c>$lookup</c> sub-pipeline.
    /// </summary>
    internal bool HasPagingAnywhere
        => HasPaging || _trailingOps.Exists(o => o is MongoSkipOp or MongoLimitOp) || _sawUnrecordedPaging;

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
            // Mutually exclusive with Grouping. A scalar aggregate/reducer set on an already-grouped select
            // is the EF-344 pass-2 bug: it flips Route to ScalarAggregate (prioritized above GroupBy) while
            // the lowerer still emits the [$group, $project] grouping pipeline, so the scalar shaper reads a
            // nonexistent element and crashes. The post-group guard in NativeCardinalityBinder.TryBindAggregate
            // / TryBindReducer (both gate on IsGroupBy) prevents this at population — this assert catches any
            // future path that forgets it. Both may legitimately co-occur with Projection, so that is not asserted.
            Debug.Assert(value == null || _grouping == null,
                "Cardinality and Grouping are mutually exclusive (see the NativeCardinalityBinder post-group guard / EF-344 pass-2).");
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
                "Grouping and Cardinality are mutually exclusive (see the NativeCardinalityBinder post-group guard / EF-344 pass-2).");
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
    /// machinery, so it shares the SAME post-group operator guards as <see cref="IsGroupBy"/>
    /// (<c>NativeSlotPopulator</c>'s post-group slot guard and <c>NativeCardinalityBinder</c>'s aggregate/reducer
    /// guards, both keyed on <c>IsGroupBy || IsDistinct</c>) — an operator applied AFTER the Distinct must fall
    /// back cleanly. It is a SEPARATE flag from <see cref="IsGroupBy"/> because the Join-family decline is
    /// grouping-semantics-specific: a real <c>GroupBy</c> joined via driver-LINQ returns silently-wrong (empty)
    /// joins, so <c>TranslateJoinCore</c> HARD-declines it (<see cref="MarkGroupByFallbackUnsafe"/>). A
    /// projected <c>Distinct</c> is just a flat set of rows the driver-LINQ path joins correctly, so
    /// <c>Distinct</c>-then-<c>Join</c> must instead fall back GRACEFULLY
    /// (<see cref="MarkNotNativelyRepresentable"/>) — not hard-throw. Keeping the flags distinct is what lets
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
    /// <see cref="IsGroupBy"/> or <see cref="IsDistinct"/> by construction, so including it here is a no-op
    /// in practice, not an additional case). <see cref="UnwindSource"/> joins the same gate (EF-347 slice 3):
    /// a native owned-collection SelectMany is terminal-only, exactly like Distinct/GroupBy/Union/Concat.
    /// </summary>
    internal bool HasTerminalOperator => IsGroupBy || IsDistinct || IsSetOp || Grouping != null || UnwindSources.Count > 0;

    /// <summary>
    /// <see langword="true"/> when the ONLY terminal on this select is a set operation — i.e. a set op is
    /// attached and no grouping/distinct/unwind terminal is (EF-347 slice B). A set op only ever attaches to a
    /// plain whole-entity select, so <see cref="IsSetOp"/> already implies the rest; the explicit conjunction
    /// is defensive so the slice-B guard relaxation can never accidentally open a GroupBy/Distinct/SelectMany
    /// terminal. Used to relax the two catch-all post-terminal guards (NativeSlotPopulator,
    /// NativeCardinalityBinder) for the operators composed after a set op, while every deferred operator's own
    /// HasTerminalOperator guard stays tripped.
    /// The <c>Projection.Count == 0</c> conjunct (EF-347 slice C2) makes this read as "a set op is the ONLY
    /// thing done so far": it stays true while a trailing projection is being pushed down (Projection is still
    /// empty at that moment, so TranslateSelect admits the projection), then flips to false once the projection
    /// is populated — so any operator composed AFTER the trailing projection falls back rather than resolving
    /// against the entity type (the composition-after-projection seam).
    /// </summary>
    internal bool IsSetOpTerminalOnly
        => IsSetOp && !IsGroupBy && !IsDistinct && Grouping == null && UnwindSources.Count == 0 && Projection.Count == 0;

    /// <summary>The terminal set operation (Union/Concat), when this select is a set-op query (EF-347 slice 2).</summary>
    internal MongoSetOperation? SetOperation { get; set; }

    /// <summary>
    /// <see langword="true"/> when a terminal set operation is attached. A SEPARATE provenance flag (like
    /// <see cref="IsDistinct"/>) that joins the post-terminal guard <see cref="HasTerminalOperator"/> so any
    /// operator applied AFTER the union falls back (terminal-only scope).
    /// </summary>
    internal bool IsSetOp { get; set; }

    /// <summary>
    /// <see langword="true"/> when <see cref="Projection"/> contains an owned entity-COLLECTION array leaf
    /// (EF-322 owned-data slice 8 — <c>Select(b =&gt; new { b.Title, b.Posts })</c>). Provenance only: it records what the
    /// projection CONTAINS, and nothing on the ordinary projection path reads it.
    /// </summary>
    /// <remarks>
    /// It exists for one consumer — the projected-set-op-OPERAND scope gate
    /// (<c>MongoQueryableMethodTranslatingExpressionVisitor.IsPlainProjectedSelect</c>, EF-347 slice C1) — which
    /// must DECLINE such a projection as a set-op operand. See that predicate's remarks for the measured reason:
    /// an array leaf forces the owner key into the projected document, and a projected-operand set op dedups /
    /// source-tags over that whole projected document by value, so the leaked <c>_id</c> silently changes the set
    /// operation's own semantics from value-based to identity-based. A TRAILING projection after a whole-entity
    /// set op (slice C2) is unaffected and stays native — its dedup runs over whole entities BEFORE the
    /// <c>$project</c>, so neither the array nor the owner key ever reaches the value comparison.
    /// </remarks>
    internal bool HasArrayProjectionLeaf { get; set; }

    private readonly List<MongoUnwindSource> _unwindSources = [];

    /// <summary>
    /// The ordered chain of terminal SelectMany unwind sources (EF-347 nested-reference slice). Index 0 is
    /// the first (outermost) SelectMany's unwind; index 1, when present, is a SECOND, chained SelectMany's
    /// own unwind — correlated off the first's unwound element (see
    /// <see cref="NativeTranslation.NativeSelectManyBinder.TryBindNestedReferenceNavUnwind"/>). Every
    /// existing single-level SelectMany populates exactly one entry (byte-identical behavior, preserved via
    /// the <see cref="UnwindSource"/> shim below); a 2-level nested reference SelectMany populates two.
    /// Write only via <see cref="AddUnwindSource"/> — there is no direct setter, so every write site is
    /// grep-visible.
    /// </summary>
    public IReadOnlyList<MongoUnwindSource> UnwindSources => _unwindSources;

    /// <summary>Appends a new terminal SelectMany unwind source to the chain (EF-347 nested-reference slice).</summary>
    internal void AddUnwindSource(MongoUnwindSource source) => _unwindSources.Add(source);

    /// <summary>
    /// The LAST (most-recently-appended) terminal SelectMany unwind source, or <see langword="null"/> when
    /// none is set — a read-only "last source" shim over <see cref="UnwindSources"/> (EF-347 nested-reference
    /// slice) that keeps every existing single-source read site (the lowerer, the whole-element gate in
    /// <c>TranslateSelect</c>, both projection binders) working unchanged: every current consumer cares about
    /// the TERMINAL unwind source, which for a single-level SelectMany is its only source and for the 2-level
    /// nested case is the SECOND (innermost) source. There is no setter — write via
    /// <see cref="AddUnwindSource"/>.
    /// </summary>
    internal MongoUnwindSource? UnwindSource => _unwindSources.Count > 0 ? _unwindSources[^1] : null;

    /// <summary>
    /// <see langword="true"/> when the terminal seen so far on this select is EXACTLY a single REFERENCE
    /// unwind source, with no grouping/distinct/set-op mixed in (EF-347 nested-reference slice). This is the
    /// narrow carve-out condition <c>TranslateSelectMany</c> checks BEFORE its ordinary
    /// <see cref="HasTerminalOperator"/> guard: only when this holds does a SECOND, chained SelectMany get a
    /// chance at nested-reference recognition (<see cref="NativeTranslation.NativeSelectManyBinder.TryBindNestedReferenceNavUnwind"/>);
    /// every other post-terminal shape (a 2nd SelectMany after GroupBy/Distinct/a set-op/an OWNED unwind, or a
    /// query already 2+ levels deep) still hits the unmodified guard exactly as before this slice.
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

    private bool _isPagedJoinInnerFallbackUnsafe;

    /// <summary>
    /// <see langword="true"/> when this query contains a <c>Join</c>/<c>GroupJoin</c>/<c>LeftJoin</c> whose
    /// INNER sequence pages itself (<c>Skip</c>/<c>Take</c>). Driver 3.10 mistranslates that shape —
    /// <b>CSHARP-6017</b> — by folding the uncorrelated inner's <c>$sort</c>/<c>$skip</c>/<c>$limit</c> into the
    /// CORRELATED <c>$lookup</c> sub-pipeline, where they run per-outer-row over a key-matched subset of at most
    /// one document instead of once over the whole inner sequence. The driver-LINQ fallback therefore executes
    /// and returns <em>silently wrong</em> rows (measured: 0 rows where 453 is correct; 830 where 181 is
    /// correct). Like <see cref="IsGroupByFallbackUnsafe"/> this must HARD-decline under
    /// <c>Native</c>/<c>NativeOnly</c> rather than fall back; explicit <c>DriverLinq</c> stays the user's opt-in.
    /// A separate flag from <see cref="IsGroupByFallbackUnsafe"/> so the decline message can name the real cause
    /// and so this one can be deleted wholesale when the driver is fixed.
    /// TODO(CSHARP-6017): delete this flag, its setter and <see cref="HasPagingAnywhere"/> on driver fix — see
    /// the removal checklist in docs/superpowers/specs/2026-07-31-groupby-join-uncorrelated-inner-decline-design.md §2.6.
    /// </summary>
    internal bool IsPagedJoinInnerFallbackUnsafe => _isPagedJoinInnerFallbackUnsafe;

    /// <summary>
    /// Records that this query joins against an inner sequence that pages itself, whose driver-LINQ fallback
    /// returns wrong rows (see <see cref="IsPagedJoinInnerFallbackUnsafe"/>). Also marks the query non-native.
    /// </summary>
    internal void MarkPagedJoinInnerFallbackUnsafe()
    {
        _isPagedJoinInnerFallbackUnsafe = true;
        _hasUnsupportedOperator = true;
    }

    /// <summary>
    /// <see langword="true"/> when ANY wrong-data-on-fallback provenance has been recorded — a GroupBy combined
    /// with a join (<see cref="IsGroupByFallbackUnsafe"/>) or a self-paging join inner
    /// (<see cref="IsPagedJoinInnerFallbackUnsafe"/>). This is the single signal the gate reads: both mean "the
    /// driver-LINQ fallback executes and returns wrong rows", so both hard-decline identically. See
    /// <c>MongoShapedQueryCompilingExpressionVisitor.ClassifyNativeDisposition</c>.
    /// </summary>
    internal bool IsFallbackWrongData => _isGroupByFallbackUnsafe || _isPagedJoinInnerFallbackUnsafe;

    /// <summary>
    /// Copies any wrong-data provenance from <paramref name="inner"/> onto this select. A join whose inner is
    /// itself a SUBQUERY containing an offending shape records the verdict on the INTERMEDIATE
    /// <c>MongoQueryExpression</c>, and the gate only ever reads the OUTERMOST one — measured: the spec's
    /// <c>Join_GroupBy_Aggregate_in_subquery</c> inner declines correctly when promoted to top level but
    /// executes and returns 0 rows (expected 133) when nested. Propagation is what makes the verdict
    /// nesting-insensitive <em>along join-inner chains</em> — deliberately not more than that: this has exactly
    /// one call site, <c>TranslateJoinCore</c>, so a verdict recorded on a subquery used in any position OTHER
    /// than a join's inner still never reaches the gate.
    /// NOT part of the CSHARP-6017 guard: this closes an independent EF-344 hole and must SURVIVE the driver
    /// fix. Do not delete it with the paged-inner flag.
    /// </summary>
    internal void PropagateFallbackWrongDataFrom(MongoSelectDefinition inner)
    {
        if (inner._isGroupByFallbackUnsafe)
        {
            MarkGroupByFallbackUnsafe();
        }

        if (inner._isPagedJoinInnerFallbackUnsafe)
        {
            MarkPagedJoinInnerFallbackUnsafe();
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

    /// <summary>
    /// The single authoritative native-execution decision for this query, computed from the populated
    /// slots. <see cref="NativeRoute.Fallback"/> when any unsupported operator was seen; otherwise
    /// <see cref="NativeRoute.Projection"/> when a <c>$project</c> was populated; otherwise
    /// <see cref="NativeRoute.WholeEntity"/>. This is authoritative for <em>slot/projection</em> representability;
    /// the full is-native decision is the gate's <c>ClassifyNativeDisposition</c> (EF-334), which layers vector
    /// search (<c>ContainsVectorSearch</c> over the captured chain — not on <see cref="MongoSelectDefinition"/>)
    /// and the GroupBy+Join hard-decline (<see cref="IsGroupByFallbackUnsafe"/>) onto this route. <c>$lookup</c>
    /// streamability is a separate axis (streaming-vs-DOM), not an is-native signal.
    /// </summary>
    internal NativeRoute Route
        => _hasUnsupportedOperator ? NativeRoute.Fallback
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
