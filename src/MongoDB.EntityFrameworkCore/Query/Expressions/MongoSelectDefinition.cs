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
    private readonly List<MongoOrdering> _orderings = [];
    private readonly List<MongoProjection> _projections = [];

    // ── Predicate ────────────────────────────────────────────────────────────────

    /// <summary>
    /// The conjunction of all <c>Where</c> predicates pushed down so far.
    /// <see langword="null"/> means no predicate (match-all).
    /// </summary>
    public MongoExpression? Predicate { get; set; }

    /// <summary>
    /// AND-combines <paramref name="conjunct"/> into <see cref="Predicate"/>.
    /// If <see cref="Predicate"/> is currently <see langword="null"/>, sets it directly;
    /// otherwise wraps both sides in a <see cref="MongoBinaryExpression"/> with
    /// <see cref="MongoBinaryOperator.AndAlso"/>.
    /// </summary>
    public void AddPredicateConjunct(MongoExpression conjunct)
        => Predicate = Predicate is null
            ? conjunct
            : new MongoBinaryExpression(MongoBinaryOperator.AndAlso, Predicate, conjunct);

    // ── Orderings ────────────────────────────────────────────────────────────────

    /// <summary>
    /// The ordered sequence of sort keys for this query.
    /// </summary>
    public IReadOnlyList<MongoOrdering> Orderings => _orderings;

    /// <summary>
    /// Clears any existing orderings and sets <paramref name="first"/> as the sole ordering.
    /// </summary>
    public void ResetOrderings(MongoOrdering first)
    {
        _orderings.Clear();
        _orderings.Add(first);
    }

    /// <summary>
    /// Appends <paramref name="next"/> to the end of the orderings list.
    /// </summary>
    public void AppendOrdering(MongoOrdering next)
        => _orderings.Add(next);

    // ── Limit / Offset ───────────────────────────────────────────────────────────

    /// <summary>
    /// The maximum number of documents to return, or <see langword="null"/> for no limit.
    /// </summary>
    public MongoExpression? Limit { get; set; }

    /// <summary>
    /// The number of documents to skip before returning results, or <see langword="null"/> for no offset.
    /// </summary>
    public MongoExpression? Offset { get; set; }

    /// <summary>
    /// <see langword="true"/> when either <see cref="Offset"/> or <see cref="Limit"/> is populated —
    /// i.e. paging has already been applied to this select.
    /// </summary>
    internal bool HasPaging => Offset != null || Limit != null;

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
    /// <see langword="true"/> once this query has seen ANY native terminal grouping/distinct provenance —
    /// <see cref="IsGroupBy"/>, <see cref="IsDistinct"/>, or a finalized <see cref="Grouping"/>. Centralizes
    /// the post-terminal gate that is otherwise duplicated across <c>NativeCardinalityBinder</c>,
    /// <c>NativeSlotPopulator</c>, and the QMTEV's <c>TranslateSelect</c>/<c>TranslateGroupBy</c>: any operator
    /// reached after a native <c>GroupBy</c> or projected <c>Distinct</c> must fall back rather than resolve
    /// against the base entity type and silently emit a pre-<c>$group</c> stage. <c>Grouping != null</c> is
    /// included for completeness (a finalized grouping always also sets <see cref="IsGroupBy"/> or
    /// <see cref="IsDistinct"/> by construction, so including it here is a no-op in practice, not an
    /// additional case).
    /// </summary>
    internal bool HasTerminalGrouping => IsGroupBy || IsDistinct || Grouping != null;

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
