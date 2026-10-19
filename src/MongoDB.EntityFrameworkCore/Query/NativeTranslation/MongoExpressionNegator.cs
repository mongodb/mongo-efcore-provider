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
/// Used for <c>All(pred)</c> (negated <c>$elemMatch</c> / top-level <c>$match</c> conjunct) and to De Morgan a
/// negated <c>&amp;&amp;</c>/<c>||</c>, which must not become an aggregation <c>$not</c>.
/// </para>
/// <para>
/// <b>Exact complement or decline, never an approximation</b> — an approximate complement returns wrong rows
/// rather than falling back.
/// </para>
/// <para>
/// <b>Relational operators are <c>$not</c>-wrapped; <c>$eq</c>/<c>$ne</c> are inverted.</b> Relational operators
/// don't match a missing or null field, so <c>{f: {$gt: 5}}</c> and <c>{f: {$lte: 5}}</c> don't partition;
/// inverting would make <c>All(p =&gt; p.Rank &gt; 5)</c> true for an element with no <c>Rank</c>. An array-count
/// comparison is inverted too: it renders as <c>$exists</c>, which partitions.
/// </para>
/// <para>
/// <b>Output is query-dialect renderable</b> (<c>$expr</c> inside <c>$elemMatch</c> is a server error), including each
/// <c>AndAlso</c>/<c>OrElse</c> operand. The exceptions are <see cref="MongoQuantifierExpression"/> and a
/// field-to-field <see cref="MongoRegexExpression"/>, admitted on their own only because their callers place the
/// negation at a top-level <c>$match</c> conjunct or re-check renderability; a new caller must check placement,
/// not just result type.
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
            MongoDictionaryContainsKeyExpression e => new MongoDictionaryContainsKeyExpression(e.Path, !e.Negated),
            MongoFieldPresenceExpression e => e.Negate(),
            MongoCompositeKeyInExpression e => new MongoCompositeKeyInExpression(e.KeyProperties, e.Values, !e.Negated),
            MongoComputedInExpression e => new MongoComputedInExpression(e.Needle, e.Values, !e.Negated),
            MongoArrayContainsExpression e => new MongoArrayContainsExpression(e.Field, e.Value, !e.Negated),
            MongoRegexExpression e => new MongoRegexExpression(e.Field, e.Kind, e.Term, !e.Negated, e.CaseInsensitive, e.PatternOptions),
            MongoElemMatchExpression e => new MongoElemMatchExpression(e.ArrayPath, e.ElementPredicate, !e.Negated),
            _ => null
        };

        return flipped is not null;
    }

    /// <summary>
    /// Builds the exact logical complement of <paramref name="node"/>; <see langword="false"/> means the caller must decline.
    /// </summary>
    public static bool TryNegate(MongoExpression node, [NotNullWhen(true)] out MongoExpression? negated)
    {
        negated = null;

        // Quantifiers are aggregation-only, but the one caller (NativeCardinalityBinder's root-level All arm) places
        // the result as a top-level $match conjunct, where $expr is legal. Only on its own: De Morgan'ing just the
        // query-dialect half of a mixed AndAlso/OrElse would drop the quantifier.
        if (node is MongoQuantifierExpression)
            return TryNegateCore(node, out negated, inAggregationContext: false);

        // A field-to-field regex has no query-dialect form ($regex needs a literal). Callers are safe because the
        // root-level All arm uses a top-level $match conjunct and the $elemMatch All arm re-checks
        // IsQueryDialectRenderable on the result.
        if (node is MongoRegexExpression { Term: MongoFieldExpression })
            return TryNegateCore(node, out negated, inAggregationContext: false);

        // No query-dialect rendering means no query-dialect complement.
        if (!MongoQueryLanguageRenderer.IsQueryDialectRenderable(node))
            return false;

        return TryNegateCore(node, out negated, inAggregationContext: false);
    }

    /// <summary>
    /// <c>$eq</c>/<c>$ne</c> are inverted, relational operators <c>$not</c>-wrapped, anything else declines. Applies no
    /// dialect gate; callers decide where the result may be placed. Also used by
    /// <c>NativeGroupByBinder.TryNegateGroupComparison</c>.
    /// </summary>
    internal static bool TryNegateComparison(
        MongoBinaryExpression comparison, [NotNullWhen(true)] out MongoExpression? negated)
    {
        switch (comparison.Operator)
        {
            // $eq and $ne partition every BSON value (including missing/null), so inversion is exact.
            case MongoBinaryOperator.Equal:
                negated = new MongoBinaryExpression(MongoBinaryOperator.NotEqual, comparison.Left, comparison.Right);
                return true;

            case MongoBinaryOperator.NotEqual:
                negated = new MongoBinaryExpression(MongoBinaryOperator.Equal, comparison.Left, comparison.Right);
                return true;

            // Relational operators do NOT partition: wrap, never invert.
            case MongoBinaryOperator.LessThan:
            case MongoBinaryOperator.LessThanOrEqual:
            case MongoBinaryOperator.GreaterThan:
            case MongoBinaryOperator.GreaterThanOrEqual:
                negated = new MongoUnaryExpression(MongoUnaryOperator.Not, comparison);
                return true;

            default:
                negated = null;
                return false;
        }
    }

    // inAggregationContext is true only inside a MongoQuantifierExpression.ElementPredicate (rendered in the
    // quantifier's $map). It structurally confines the non-query-native comparison case to that recursion, even if
    // the query-dialect classifier is later widened.
    private static bool TryNegateCore(
        MongoExpression node, [NotNullWhen(true)] out MongoExpression? negated, bool inAggregationContext)
    {
        negated = null;

        switch (node)
        {
            // De Morgan; a declining child declines the whole tree. Must not be Not(conjunction): the server
            // rejects { $not: { $or: [...] } }.
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

            // The IsQueryNativeComparison guard is redundant given TryNegate's gate, but kept because getting this
            // case wrong is silent wrong data.
            case MongoBinaryExpression comparison
                when MongoQueryLanguageRenderer.IsQueryNativeComparison(comparison):
                return TryNegateComparison(comparison, out negated);

            // Array-count comparison: inverted, not wrapped, because it renders as $exists
            // (MongoQueryLanguageRenderer.TryRenderSizeComparison), which partitions.
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
                    _ => (MongoBinaryOperator?)null
                };

                if (inverted is null)
                    return false;

                negated = new MongoBinaryExpression(
                    inverted.Value, sizeComparison.Left, sizeComparison.Right);
                return true;
            }

            // A non-query-native comparison (e.g. against a MongoOuterFieldExpression in a correlated quantifier's
            // ElementPredicate); same rules as above, rendered by MongoAggregationExpressionRenderer.RenderUnary.
            case MongoBinaryExpression comparison3 when inAggregationContext:
                return TryNegateComparison(comparison3, out negated);

            // Self-negating node kinds; see TryFlipNegatedFlag.
            case var selfNegating when TryFlipNegatedFlag(selfNegating, out var flipped):
                negated = flipped;
                return true;

            // !Any(pred) = All(!pred) and vice versa: flip Kind and negate ElementPredicate (the only recursion
            // passing inAggregationContext: true).
            case MongoQuantifierExpression quantifier:
            {
                if (!TryNegateCore(quantifier.ElementPredicate, out var negatedElementPredicate, inAggregationContext: true))
                    return false;

                var negatedKind = quantifier.Kind == MongoExpressionTranslator.MongoQuantifierKind.Any
                    ? MongoExpressionTranslator.MongoQuantifierKind.All
                    : MongoExpressionTranslator.MongoQuantifierKind.Any;

                negated = new MongoQuantifierExpression(quantifier.ArrayPath, negatedElementPredicate, negatedKind);
                return true;
            }

            case MongoUnaryExpression { Operator: MongoUnaryOperator.Not } not:
                // Double negation; the operand is renderable by TryNegate's gate (a Not is admitted only over a bare
                // field or a query-native comparison).
                negated = not.Operand;
                return true;

            case MongoFieldExpression field
                when field.Property.ClrType == typeof(bool) && !field.Property.IsNullable:
                // { f: true } -> { f: { $ne: true } }; non-nullable only, mirroring the translator's bare-bool set.
                negated = new MongoUnaryExpression(MongoUnaryOperator.Not, field);
                return true;

            case MongoConstantExpression { Value: bool literalBool }:
                negated = new MongoConstantExpression(!literalBool, forSerialization: null);
                return true;

            default:
                return false;
        }
    }
}
