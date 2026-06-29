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

namespace MongoDB.EntityFrameworkCore.Query.Expressions;

// EF-323 SP1 — native-query-translation logical slots.
// These slots are what the design document calls "MongoSelectExpression". They are implemented
// in-place on MongoQueryExpression (controller decision) to avoid churning the QMTEV, shaper,
// and factory plumbing. The existing CapturedExpression + projection machinery is untouched and
// continues to serve the current fallback path.
internal sealed partial class MongoQueryExpression
{
    private readonly List<MongoOrdering> _orderings = [];

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

    // ── Native-representable gate ─────────────────────────────────────────────────

    /// <summary>
    /// Whether this query can be rendered to native MongoDB aggregation pipeline stages.
    /// Starts as <see langword="true"/>; the QMTEV (Task 6) flips it to <see langword="false"/>
    /// when it encounters a shape the native path cannot handle. The lowerer gate (Task 14)
    /// reads this to decide whether to attempt native emission.
    /// </summary>
    public bool IsNativeRepresentable { get; set; } = true;

    // ── Lookups accessor ─────────────────────────────────────────────────────────

    /// <summary>
    /// The ordered list of <c>$lookup</c> stages the native pipeline must emit for cross-collection
    /// Include operations. Surfaces the reference-lookup reconstruction from
    /// <see cref="GetStreamingReferenceLookups"/>: when no pending lookups are registered (the driver's
    /// native LeftJoin path), the single-level reference lookups are synthesized from
    /// <see cref="InnerCollections"/>; otherwise the already-registered pending lookups are returned
    /// directly. Consumed by the native lowerer (Task 14) to emit <c>$lookup</c> + <c>$unwind</c> stages.
    /// </summary>
    /// <remarks>
    /// This is NOT a stored slot: each access <b>recomputes</b> <see cref="GetStreamingReferenceLookups"/>,
    /// an O(navigations) reconstruction off <see cref="InnerCollections"/>. Callers should not treat it as a
    /// cheap field read. It is slated for structural replacement (a populated <c>Lookups</c> slot) in the
    /// Collection Includes sub-project.
    /// </remarks>
    public IReadOnlyList<LookupExpression> Lookups => GetStreamingReferenceLookups();
}
