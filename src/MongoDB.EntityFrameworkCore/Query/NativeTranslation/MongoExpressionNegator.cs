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
/// is merely close returns WRONG ROWS rather than falling back, under the default <c>Native</c> mode, which is
/// the one failure mode the provider's not-a-breaking-change rubric does not cover.
/// </para>
/// <para>
/// <b>Why relational operators are <c>$not</c>-wrapped but <c>$eq</c>/<c>$ne</c> are inverted.</b> MongoDB's
/// relational operators are type-bracketed and do not match a missing or null field, so
/// <c>{f: {$gt: 5}}</c> and <c>{f: {$lte: 5}}</c> do NOT partition the value space — an element with no
/// <c>f</c> is matched by neither. Inverting them would make <c>All(p =&gt; p.Rank &gt; 5)</c> report
/// <see langword="true"/> for a document containing an element with no <c>Rank</c>, where LINQ evaluates
/// <c>null &gt; 5</c> as <see langword="false"/>. Wrapping in <c>$not</c> yields the exact complement instead.
/// <c>$eq</c>/<c>$ne</c> DO partition every BSON value including missing and null, so for that one pair
/// inversion is exact — and it keeps the common case rendering as idiomatic <c>{f: {$ne: v}}</c>.
/// </para>
/// <para>
/// <b>Domain invariants, both pinned by <c>MongoExpressionNegatorTests</c>:</b> the admitted input set is a
/// subset of <see cref="MongoQueryLanguageRenderer.IsQueryDialectRenderable"/>'s (enforced directly, by
/// gating on it), and every node produced is itself query-dialect renderable — it never routes to the
/// <c>$expr</c> catch-all, which inside <c>$elemMatch</c> is a hard server error rather than a slow path.
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

        // A node with no query-dialect rendering has no query-dialect COMPLEMENT either. Gating here makes
        // the output-domain invariant unconditional and makes every "not query-native" decline (field-to-
        // field comparison, arithmetic, a parameterized regex term, an unsupported $in values node) fall out
        // of one check instead of being re-derived per case.
        if (!MongoQueryLanguageRenderer.IsQueryDialectRenderable(node))
            return false;

        return TryNegateCore(node, out negated);
    }

    private static bool TryNegateCore(MongoExpression node, [NotNullWhen(true)] out MongoExpression? negated)
    {
        negated = null;

        switch (node)
        {
            // De Morgan. Recurses; a declining child declines the whole tree with no partial output.
            //
            // Producing an $or/$and ARRAY of negated conjuncts (rather than wrapping the conjunction in a
            // single Not node) is MANDATORY, not stylistic: the server rejects { $not: { $or: [...] } } with
            // "unknown operator: $or" (spike-measured). IsQueryDialectRenderable independently refuses a Not
            // over a conjunction, so the illegal form cannot be built — but the reason it must not be is here.
            case MongoBinaryExpression { Operator: MongoBinaryOperator.AndAlso } and:
            {
                if (!TryNegateCore(and.Left, out var left) || !TryNegateCore(and.Right, out var right))
                    return false;
                negated = new MongoBinaryExpression(MongoBinaryOperator.OrElse, left, right);
                return true;
            }

            case MongoBinaryExpression { Operator: MongoBinaryOperator.OrElse } or:
            {
                if (!TryNegateCore(or.Left, out var left) || !TryNegateCore(or.Right, out var right))
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

            case MongoInExpression inExpr:
                // $nin is defined as the complement of $in.
                negated = new MongoInExpression(inExpr.Field, inExpr.Values, !inExpr.Negated);
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
