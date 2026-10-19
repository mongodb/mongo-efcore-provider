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
/// Produces the exact logical complement of a translated predicate, or declines.
/// </summary>
/// <remarks>
/// <para>
/// Used for universal quantifiers: <c>All(pred)</c> renders as a negated <c>$elemMatch</c> over <c>¬pred</c>
/// (<c>MongoExpressionTranslator</c>), and a top-level <c>All</c> aggregate's predicate is negated into a
/// <c>$match</c> conjunct (<c>NativeCardinalityBinder</c>). <c>MongoExpressionTranslator</c>'s <c>Not</c> case also
/// uses it to De Morgan a negated <c>&amp;&amp;</c>/<c>||</c>, which must not become an aggregation <c>$not</c>.
/// </para>
/// <para>
/// <b>Exact complement or decline, never an approximation</b> — an approximate complement returns wrong rows
/// rather than falling back.
/// </para>
/// <para>
/// <b>Relational operators are <c>$not</c>-wrapped; <c>$eq</c>/<c>$ne</c> are inverted.</b> Relational operators
/// don't match a missing or null field, so <c>{f: {$gt: 5}}</c> and <c>{f: {$lte: 5}}</c> don't partition;
/// inverting would make <c>All(p =&gt; p.Rank &gt; 5)</c> true for an element with no <c>Rank</c>.
/// <c>$eq</c>/<c>$ne</c> partition every value including missing/null. An array-count comparison
/// (<c>MongoSizeExpression</c> on the left) is inverted too: it renders as <c>$exists</c>, which partitions.
/// </para>
/// <para>
/// <b>Output is query-dialect renderable</b> (never the <c>$expr</c> catch-all, a server error inside
/// <c>$elemMatch</c>), including each operand of an <c>AndAlso</c>/<c>OrElse</c> complement. The exceptions are
/// <see cref="MongoQuantifierExpression"/> and a field-to-field <see cref="MongoRegexExpression"/>, admitted on
/// their own only (never as an <c>AndAlso</c>/<c>OrElse</c> operand) because their callers place the negation at
/// a top-level <c>$match</c> conjunct or re-check renderability; a new caller must check placement, not just
/// result type.
/// </para>
/// </remarks>
internal static class MongoExpressionNegator
{
    /// <summary>
    /// Flips the <c>Negated</c> flag of a node whose rendered negated/un-negated pair are exact complements:
    /// <c>$in</c>/<c>$nin</c>, computed-needle <c>$in</c>, array-contains (<c>{f: v}</c> vs <c>{f: {$ne: v}}</c>),
    /// regex, and <c>$elemMatch</c>.
    /// </summary>
    /// <remarks>
    /// Shared with <see cref="MongoExpressionTranslator"/>'s <c>Not</c> case, which can't call
    /// <see cref="TryNegate"/> because its query-dialect gate declines some of these nodes in positions the
    /// translator legitimately reaches.
    /// </remarks>
    internal static bool TryFlipNegatedFlag(MongoExpression node, [NotNullWhen(true)] out MongoExpression? flipped)
    {
        flipped = node switch
        {
            MongoInExpression e => new MongoInExpression(e.Field, e.Values, !e.Negated),
            MongoComputedInExpression e => new MongoComputedInExpression(e.Needle, e.Values, !e.Negated),
            MongoArrayContainsExpression e => new MongoArrayContainsExpression(e.Field, e.Value, !e.Negated),
            MongoRegexExpression e => new MongoRegexExpression(e.Field, e.Kind, e.Term, !e.Negated, e.CaseInsensitive, e.PatternOptions),
            MongoElemMatchExpression e => new MongoElemMatchExpression(e.ArrayPath, e.ElementPredicate, !e.Negated),
            _ => null
        };

        return flipped is not null;
    }

    /// <summary>
    /// Attempts to build the exact logical complement of <paramref name="node"/>.
    /// </summary>
    /// <returns>
    /// <see langword="false"/> when there is no exact query-dialect complement; the caller must then decline.
    /// </returns>
    public static bool TryNegate(MongoExpression node, [NotNullWhen(true)] out MongoExpression? negated)
    {
        negated = null;

        // Quantifiers are aggregation-only (IsQueryDialectRenderable rejects them), but the one caller
        // (NativeCardinalityBinder's root-level All arm) places the result as a top-level $match conjunct, where
        // $expr is legal. Only admitted on its own: AndAlso/OrElse recursion stays gated, since De Morgan'ing only
        // the query-dialect half of a mixed predicate would drop the quantifier.
        if (node is MongoQuantifierExpression)
            return TryNegateCore(node, out negated, inAggregationContext: false);

        // A field-to-field regex (c.ContactName.StartsWith(c.ContactName)) has no query-dialect form ($regex
        // needs a literal). Its two callers are safe for different reasons: NativeCardinalityBinder's root-level
        // All arm uses a top-level $match conjunct, and MongoExpressionTranslator's $elemMatch All arm re-checks
        // IsQueryDialectRenderable on the result and declines. Negation is just the regex flag flip.
        if (node is MongoRegexExpression { Term: MongoFieldExpression })
            return TryNegateCore(node, out negated, inAggregationContext: false);

        // No query-dialect rendering means no query-dialect complement; this one gate covers every
        // "not query-native" decline.
        if (!MongoQueryLanguageRenderer.IsQueryDialectRenderable(node))
            return false;

        return TryNegateCore(node, out negated, inAggregationContext: false);
    }

    /// <summary>
    /// Negation switch shared by <see cref="TryNegate"/> and its recursions.
    /// </summary>
    /// <param name="node">The node to negate.</param>
    /// <param name="negated">The exact complement, or <see langword="null"/> when none exists.</param>
    /// <param name="inAggregationContext">
    /// <see langword="true"/> only inside a <see cref="MongoQuantifierExpression.ElementPredicate"/>, which renders
    /// in the quantifier's <c>$map</c> (aggregation dialect, never <c>$elemMatch</c>). Structurally confines the
    /// non-query-native comparison case to that recursion, even if the query-dialect classifier is later widened.
    /// </param>
    private static bool TryNegateCore(
        MongoExpression node, [NotNullWhen(true)] out MongoExpression? negated, bool inAggregationContext)
    {
        negated = null;

        switch (node)
        {
            // De Morgan; a declining child declines the whole tree. Must produce $or/$and of negations, not
            // Not(conjunction): the server rejects { $not: { $or: [...] } }.
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

            // Array-count comparison: inverted, not wrapped, because it renders as $exists (see
            // MongoQueryLanguageRenderer.TryRenderSizeComparison), which partitions. The admitted set is closed
            // under inversion.
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

            // A non-query-native comparison (e.g. against a MongoOuterFieldExpression, as in a correlated
            // quantifier's ElementPredicate). Only reachable with inAggregationContext; same rules as above —
            // $eq/$ne invert, relational operators are $not-wrapped (rendered by
            // MongoAggregationExpressionRenderer.RenderUnary).
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

            // Self-negating node kinds; see TryFlipNegatedFlag.
            case var selfNegating when TryFlipNegatedFlag(selfNegating, out var flipped):
                negated = flipped;
                return true;

            // Correlated quantifier: !Any(pred) ≡ All(!pred) and vice versa. $anyElementTrue/$allElementsTrue
            // are exact duals with no negation flag, so flip Kind and negate ElementPredicate — the only
            // recursion passing inAggregationContext: true.
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

            case MongoConstantExpression { Value: bool literalBool }:
                // Complement of a literal boolean predicate root (see MongoExpressionTranslator's own case)
                // is simply the other literal — exact by construction, no query-dialect involvement needed.
                negated = new MongoConstantExpression(!literalBool, forSerialization: null);
                return true;

            default:
                return false;
        }
    }
}
