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

using System.Diagnostics.CodeAnalysis;
using MongoDB.EntityFrameworkCore.Query.Expressions;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>
/// Produces the EXACT logical complement of a translated predicate, or declines.
/// </summary>
/// <remarks>
/// <para>
/// Used to translate a universal quantifier: <c>All(pred)</c> is true exactly when NO element satisfies
/// <c>¬pred</c>, so it renders as a negated <c>$elemMatch</c> over the complement
/// (<c>MongoExpressionTranslator</c>'s quantifier arm), and to negate a top-level <c>All</c> aggregate's
/// predicate into a <c>$match</c> conjunct (<c>NativeCardinalityBinder</c>).
/// </para>
/// <para>
/// <b>The contract is EXACT complement or decline — never an approximation.</b> A predicate whose complement
/// is merely close returns wrong rows rather than falling back.
/// </para>
/// <para>
/// <b>Relational operators are <c>$not</c>-wrapped; <c>$eq</c>/<c>$ne</c> are inverted.</b> MongoDB's
/// relational operators are type-bracketed and do not match a missing or null field, so <c>{f: {$gt: 5}}</c>
/// and <c>{f: {$lte: 5}}</c> do not partition the value space — neither matches an element with no <c>f</c>.
/// Inverting one would make <c>All(p =&gt; p.Rank &gt; 5)</c> report <see langword="true"/> for a document
/// with an element that has no <c>Rank</c>, where LINQ evaluates <c>null &gt; 5</c> as false; wrapping in
/// <c>$not</c> is the exact complement instead. <c>$eq</c>/<c>$ne</c> do partition every BSON value including
/// missing and null, so for that pair inversion is exact and keeps the idiomatic <c>{f: {$ne: v}}</c> form.
/// </para>
/// <para>
/// <b>Exception: an array-count comparison IS inverted.</b> The rule is "does the rendered pair partition",
/// not "relational operators are always wrapped". An array-count comparison (<c>MongoSizeExpression</c> on
/// the left) renders as <c>{"path.k": {$exists: true|false}}</c>, and <c>$exists</c> does partition — every
/// document either has <c>path.k</c> or not — so inverting is exact there, and the admitted set is closed
/// under inversion.
/// </para>
/// <para>
/// <b>The output is query-dialect renderable — with one deliberate, narrow exception: the
/// <see cref="MongoQuantifierExpression"/> family.</b> For every OTHER input, the admitted set is a subset of
/// <see cref="MongoQueryLanguageRenderer.IsQueryDialectRenderable"/>'s (enforced by gating on it directly),
/// and the node produced is itself query-dialect renderable — it never routes to the <c>$expr</c> catch-all,
/// which is a hard server error inside <c>$elemMatch</c>. <see cref="MongoQuantifierExpression"/> is exempt
/// from both halves of that rule on both sides — <see cref="TryNegate"/> admits it even though
/// <c>IsQueryDialectRenderable</c> rejects it, and the negated result it produces (like the un-negated node)
/// DOES route to the <c>$expr</c> catch-all — because a quantifier's negation-consuming caller
/// (<c>NativeCardinalityBinder</c>'s root-level <c>All(pred)</c> arm) places the result at a TOP-LEVEL
/// <c>$match</c> conjunct, where <c>$expr</c> is legal, and never inside <c>$elemMatch</c>, where it is not.
/// Any FUTURE <c>TryNegate</c>/<c>TryNegateCore</c> caller that might place a quantifier's negation inside an
/// <c>$elemMatch</c> would violate this exception's own precondition — check placement, not just result type,
/// before adding one.
/// </para>
/// </remarks>
internal static class MongoExpressionNegator
{
    /// <summary>
    /// Attempts to build the exact logical complement of <paramref name="node"/>.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> and the complement, or <see langword="false"/> with no output when
    /// <paramref name="node"/> has no exact query-dialect complement (the caller must then decline, so the
    /// query falls back to driver-LINQ).
    /// </returns>
    public static bool TryNegate(MongoExpression node, [NotNullWhen(true)] out MongoExpression? negated)
    {
        negated = null;

        // MongoQuantifierExpression is a DELIBERATE, NARROW exception to the query-dialect gate below: it is
        // aggregation-expression-ONLY (IsQueryDialectRenderable declines it unconditionally, by design — see
        // its own remarks) because it can never legally appear NESTED inside an $elemMatch. But its one
        // negation call site (NativeCardinalityBinder's root-level All(pred) arm) never nests the result
        // inside an $elemMatch either — it appends the negated node as a top-level $match CONJUNCT, a
        // position MongoQueryLanguageRenderer.RenderNode's own "no dialect form -> wrap in $expr" catch-all
        // already handles for the UN-negated node the same way. So admitting it here just for its own case
        // (not through the AndAlso/OrElse recursion below, which stays gated — a composite predicate mixing a
        // quantifier with a query-dialect clause still declines, since De Morgan'ing just the query-dialect
        // half would drop the quantifier leaf) is safe.
        if (node is MongoQuantifierExpression)
            return TryNegateCore(node, out negated, inAggregationContext: false);

        // A node with no query-dialect rendering has no query-dialect COMPLEMENT either. Gating here makes
        // the output-domain invariant unconditional and makes every "not query-native" decline (field-to-
        // field comparison, arithmetic, a parameterized regex term, an unsupported $in values node) fall out
        // of one check instead of being re-derived per case.
        if (!MongoQueryLanguageRenderer.IsQueryDialectRenderable(node))
            return false;

        return TryNegateCore(node, out negated, inAggregationContext: false);
    }

    /// <param name="node">The node to negate.</param>
    /// <param name="negated">The exact complement, or <see langword="null"/> when none exists.</param>
    /// <param name="inAggregationContext">
    /// <see langword="true"/> only when negating <see cref="MongoQuantifierExpression.ElementPredicate"/> (or a
    /// subtree reached FROM it through the AndAlso/OrElse recursion below) — a position that always renders
    /// inside the quantifier's own <c>$map</c> "in" clause, an aggregation-expression context, never
    /// <c>$elemMatch</c>. Every other caller — the public <see cref="TryNegate"/> entry point, and every OTHER
    /// recursive call below — passes <see langword="false"/>. This is what STRUCTURALLY confines the
    /// comparison3 case (below) to that one recursion: its <c>when</c> guard requires this flag, so widening
    /// <see cref="MongoQueryLanguageRenderer.IsQueryDialectRenderable"/> to admit some new binary shape in the
    /// future cannot silently make comparison3 reachable from an ordinary (non-aggregation-context) caller —
    /// it would still need this flag threaded in, which only the quantifier recursion does.
    /// </param>
    private static bool TryNegateCore(
        MongoExpression node, [NotNullWhen(true)] out MongoExpression? negated, bool inAggregationContext)
    {
        negated = null;

        switch (node)
        {
            // De Morgan. Recurses; a declining child declines the whole tree with no partial output.
            //
            // Producing an $or/$and of negated conjuncts (rather than wrapping the conjunction in a single Not
            // node) is mandatory: the server rejects { $not: { $or: [...] } } with "unknown operator: $or".
            // IsQueryDialectRenderable independently refuses a Not over a conjunction, so the illegal form
            // can't be built — but the reason it must not be is here.
            case MongoBinaryExpression { Operator: MongoBinaryOperator.AndAlso } and:
            {
                if (!TryNegateCore(and.Left, out var left, inAggregationContext)
                    || !TryNegateCore(and.Right, out var right, inAggregationContext))
                    return false;
                negated = new MongoBinaryExpression(MongoBinaryOperator.OrElse, left, right);
                return true;
            }

            case MongoBinaryExpression { Operator: MongoBinaryOperator.OrElse } or:
            {
                if (!TryNegateCore(or.Left, out var left, inAggregationContext)
                    || !TryNegateCore(or.Right, out var right, inAggregationContext))
                    return false;
                negated = new MongoBinaryExpression(MongoBinaryOperator.AndAlso, left, right);
                return true;
            }

            // A comparison. The IsQueryNativeComparison guard is redundant given TryNegate's gate above, but
            // is kept explicit because this is the one case where getting it wrong is silent wrong data.
            case MongoBinaryExpression comparison
                when MongoQueryLanguageRenderer.IsQueryNativeComparison(comparison):
            {
                switch (comparison.Operator)
                {
                    // $eq and $ne partition every BSON value (including missing/null) — inversion is exact.
                    case MongoBinaryOperator.Equal:
                        negated = new MongoBinaryExpression(
                            MongoBinaryOperator.NotEqual, comparison.Left, comparison.Right);
                        return true;

                    case MongoBinaryOperator.NotEqual:
                        negated = new MongoBinaryExpression(
                            MongoBinaryOperator.Equal, comparison.Left, comparison.Right);
                        return true;

                    // Relational operators do NOT partition — wrap, never invert. See the class remarks.
                    case MongoBinaryOperator.LessThan:
                    case MongoBinaryOperator.LessThanOrEqual:
                    case MongoBinaryOperator.GreaterThan:
                    case MongoBinaryOperator.GreaterThanOrEqual:
                        negated = new MongoUnaryExpression(MongoUnaryOperator.Not, comparison);
                        return true;

                    // An arithmetic operator is not a predicate; nothing to complement.
                    default:
                        return false;
                }
            }

            // An array-count comparison is INVERTED, not $not-wrapped — the documented exception to the
            // relational rule (see class remarks): it renders as { "path.k": { $exists: true|false } } (see
            // MongoQueryLanguageRenderer.TryRenderSizeComparison), and $exists does partition the document
            // set, so inverting is exact here. The admitted set is closed under inversion (C > n ↔ C <= n,
            // C >= n ↔ C < n, C == n ↔ C != n each preserve "every required array index >= 0"), so the result
            // is renderable whenever the input was.
            case MongoBinaryExpression { Left: MongoSizeExpression } sizeComparison:
            {
                var inverted = sizeComparison.Operator switch
                {
                    MongoBinaryOperator.GreaterThan => MongoBinaryOperator.LessThanOrEqual,
                    MongoBinaryOperator.GreaterThanOrEqual => MongoBinaryOperator.LessThan,
                    MongoBinaryOperator.LessThan => MongoBinaryOperator.GreaterThanOrEqual,
                    MongoBinaryOperator.LessThanOrEqual => MongoBinaryOperator.GreaterThan,
                    MongoBinaryOperator.Equal => MongoBinaryOperator.NotEqual,
                    MongoBinaryOperator.NotEqual => MongoBinaryOperator.Equal,
                    // An arithmetic operator is not a predicate; nothing to complement.
                    _ => (MongoBinaryOperator?)null
                };

                if (inverted is null)
                    return false;

                negated = new MongoBinaryExpression(
                    inverted.Value, sizeComparison.Left, sizeComparison.Right);
                return true;
            }

            // A comparison whose shape is NOT query-dialect-native (e.g. a field-to-OUTER-field comparison,
            // MongoOuterFieldExpression on the right — the exact shape a correlated quantifier's
            // ElementPredicate is built from). GUARDED on `inAggregationContext`, not merely reachable-in-
            // practice: every OTHER path into this switch passes inAggregationContext: false (see the
            // parameter's own doc comment above TryNegateCore), so even if a FUTURE change widens
            // MongoQueryLanguageRenderer.IsQueryDialectRenderable to admit some new binary shape the earlier
            // cases don't already intercept, this case still cannot fire outside the quantifier's own
            // ElementPredicate recursion — the guard is structural, not emergent from today's classifier
            // shape. This is sound because ElementPredicate is rendered inside the quantifier's own $map "in"
            // clause — an aggregation-expression context, never $elemMatch — so it never needs a query-DIALECT
            // form in the first place. $eq/$ne still partition every BSON value there exactly as in the query
            // dialect (inversion is exact regardless of whether either operand is a field, an outer field, or
            // a constant); the relational operators still do NOT partition a missing/null value there either,
            // so they are $not-WRAPPED, never inverted — same rule as the query-dialect case above, just
            // rendered by MongoAggregationExpressionRenderer.RenderUnary instead of the query dialect's $not.
            case MongoBinaryExpression comparison3 when inAggregationContext:
            {
                switch (comparison3.Operator)
                {
                    case MongoBinaryOperator.Equal:
                        negated = new MongoBinaryExpression(
                            MongoBinaryOperator.NotEqual, comparison3.Left, comparison3.Right);
                        return true;

                    case MongoBinaryOperator.NotEqual:
                        negated = new MongoBinaryExpression(
                            MongoBinaryOperator.Equal, comparison3.Left, comparison3.Right);
                        return true;

                    case MongoBinaryOperator.LessThan:
                    case MongoBinaryOperator.LessThanOrEqual:
                    case MongoBinaryOperator.GreaterThan:
                    case MongoBinaryOperator.GreaterThanOrEqual:
                        negated = new MongoUnaryExpression(MongoUnaryOperator.Not, comparison3);
                        return true;

                    // An arithmetic operator (or anything else reaching here) is not a predicate; nothing to
                    // complement.
                    default:
                        return false;
                }
            }

            case MongoInExpression inExpr:
                // $nin is defined as the complement of $in.
                negated = new MongoInExpression(inExpr.Field, inExpr.Values, !inExpr.Negated);
                return true;

            case MongoArrayContainsExpression arrayContains:
                // { field: { $ne: value } } is the exact complement of { field: value } — see
                // RenderArrayContains's remarks.
                negated = new MongoArrayContainsExpression(arrayContains.Field, arrayContains.Value, !arrayContains.Negated);
                return true;

            case MongoRegexExpression regex:
                // The renderer negates via an enclosing $not, an exact complement.
                negated = new MongoRegexExpression(regex.Field, regex.Kind, regex.Term, !regex.Negated);
                return true;

            case MongoElemMatchExpression elemMatch:
                // $not complements the $elemMatch; the bare Any() form flips $exists. This is what makes a
                // nested quantifier compose in either order (All-in-Any, Any-in-All, All-in-All).
                negated = new MongoElemMatchExpression(
                    elemMatch.ArrayPath, elemMatch.ElementPredicate, !elemMatch.Negated);
                return true;

            // De Morgan over a CORRELATED quantifier: !Any(pred) ≡ All(!pred), !All(pred) ≡ Any(!pred).
            // $anyElementTrue/$allElementsTrue are each other's exact De Morgan dual with no separate
            // negation flag (unlike MongoElemMatchExpression's Negated bool) — so the fix is to flip Kind and
            // recurse into ElementPredicate, not to wrap or flag anything. ArrayPath is unchanged either way.
            //
            // The recursion is via TryNegateCore, not the public TryNegate, WITH inAggregationContext: true —
            // ElementPredicate is rendered inside the quantifier's own $map "in" clause (an aggregation-
            // expression context, never $elemMatch), so it never needs to be query-DIALECT-renderable, and
            // this is the ONE call site that passes true (see the parameter's own doc comment on
            // TryNegateCore), which is what makes the comparison3 case below reachable at all. Every other
            // TryNegateCore case still enforces its own negation-correctness guard independently (e.g.
            // IsQueryNativeComparison, MongoSizeExpression-on-the-left) via its own pattern guard, so an
            // unsupported shape still declines through the switch's own default rather than slipping through
            // unguarded.
            case MongoQuantifierExpression quantifier:
            {
                if (!TryNegateCore(quantifier.ElementPredicate, out var negatedElementPredicate, inAggregationContext: true))
                    return false; // no exact complement for the element predicate — decline, don't approximate

                var negatedKind = quantifier.Kind == MongoExpressionTranslator.MongoQuantifierKind.Any
                    ? MongoExpressionTranslator.MongoQuantifierKind.All
                    : MongoExpressionTranslator.MongoQuantifierKind.Any;

                negated = new MongoQuantifierExpression(quantifier.ArrayPath, negatedElementPredicate, negatedKind);
                return true;
            }

            case MongoUnaryExpression { Operator: MongoUnaryOperator.Not } not:
                // Double negation. Exact for any operand, and the operand is renderable by TryNegate's gate
                // (IsQueryDialectRenderable admits a Not only over a bare field or a query-native comparison).
                negated = not.Operand;
                return true;

            case MongoFieldExpression field
                when field.Property.ClrType == typeof(bool) && !field.Property.IsNullable:
                // Complement of a bare-bool predicate { f: true } is { f: { $ne: true } }. Restricted to a
                // non-nullable bool to mirror the translator's own bare-bool acceptance set.
                negated = new MongoUnaryExpression(MongoUnaryOperator.Not, field);
                return true;

            default:
                return false;
        }
    }
}
