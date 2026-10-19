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
using System.Linq;
using MongoDB.EntityFrameworkCore.Query.Expressions;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>
/// Prefixes every <see cref="MongoFieldExpression"/> (and other document path) in a translated tree with a scope
/// path, e.g. <c>Total</c> → <c>_lookup_Refs.Total</c> for a reference SelectMany's unwound element.
/// </summary>
/// <remarks>
/// Declines, never throws, on an unhandled node kind, so a node kind added to <c>TryTranslateValue</c> without an
/// arm here falls back rather than crashing every <c>MongoQueryMode</c>. <c>MongoExpressionNodeCoverageTests</c>
/// pins which kinds reach the decline.
/// </remarks>
internal static class MongoFieldPrefixRewriter
{
    /// <summary>
    /// Prefixes every document path in <paramref name="expr"/> with <paramref name="prefix"/>, or returns
    /// <see langword="false"/> if the tree contains a node kind with no prefixing rule.
    /// </summary>
    /// <remarks>
    /// The recursion uses a throw as its internal decline channel, caught here; it never escapes this class.
    /// </remarks>
    public static bool TryRewrite(MongoExpression expr, string prefix, [NotNullWhen(true)] out MongoExpression? result)
    {
        try
        {
            result = Rewrite(expr, prefix);
            return true;
        }
        catch (NativeTranslationNotSupportedException)
        {
            result = null;
            return false;
        }
    }

    private static MongoExpression Rewrite(MongoExpression expr, string prefix)
        => expr switch
        {
            MongoFieldExpression f => new MongoFieldExpression(f.Property, prefix + "." + f.ElementName),
            MongoBinaryExpression b => new MongoBinaryExpression(
                b.Operator, Rewrite(b.Left, prefix), Rewrite(b.Right, prefix)),
            MongoUnaryExpression u => new MongoUnaryExpression(u.Operator, Rewrite(u.Operand, prefix)),
            MongoInExpression i => new MongoInExpression(
                (MongoFieldExpression)Rewrite(i.Field, prefix), Rewrite(i.Values, prefix), i.Negated),
            // The needle may be computed (e.g. $concat), so it recurses rather than being cast to a field.
            MongoComputedInExpression ci => new MongoComputedInExpression(
                Rewrite(ci.Needle, prefix), Rewrite(ci.Values, prefix), ci.Negated),
            // Field is a document path like MongoInExpression.Field; Value recursion is for consistency.
            MongoArrayContainsExpression ac => new MongoArrayContainsExpression(
                (MongoFieldExpression)Rewrite(ac.Field, prefix), Rewrite(ac.Value, prefix), ac.Negated),
            // Field recurses rather than being cast: it may be an element ref or a computed receiver
            // ((c.A + "").Contains("1")), which the $expr rendering reads whole.
            MongoRegexExpression r => new MongoRegexExpression(
                Rewrite(r.Field, prefix), r.Kind, Rewrite(r.Term, prefix), r.Negated, r.CaseInsensitive,
                r.PatternOptions),
            // Prefix the array path only: the element predicate is element-relative, as $elemMatch requires.
            MongoElemMatchExpression e => new MongoElemMatchExpression(
                prefix + "." + e.ArrayPath, e.ElementPredicate, e.Negated),
            // Required: an owned SelectMany's inner filter can contain a count
            // (SelectMany(b => b.Posts.Where(p => p.Comments.Count > 1), …)).
            MongoSizeExpression s => new MongoSizeExpression(prefix + "." + s.FieldName, s.Type, s.NullSafe),
            // Array path only; the element predicate is relative to the $filter variable.
            MongoFilteredSizeExpression f => new MongoFilteredSizeExpression(prefix + "." + f.ArrayPath, f.ElementPredicate, f.Type),
            MongoConvertExpression c => new MongoConvertExpression(Rewrite(c.Operand, prefix), c.Type),
            MongoNumericTypeBracketExpression b => new MongoNumericTypeBracketExpression(
                (MongoFieldExpression)Rewrite(b.Field, prefix)),
            MongoConditionalExpression c2 => new MongoConditionalExpression(
                Rewrite(c2.Test, prefix), Rewrite(c2.IfTrue, prefix), Rewrite(c2.IfFalse, prefix)),
            MongoCoalesceExpression co => new MongoCoalesceExpression(
                Rewrite(co.Left, prefix), Rewrite(co.Right, prefix)),
            MongoDatePartExpression dp => new MongoDatePartExpression(Rewrite(dp.Operand, prefix), dp.Part),
            MongoDateAddExpression da => new MongoDateAddExpression(
                Rewrite(da.StartDate, prefix), da.Unit, Rewrite(da.Amount, prefix)),
            MongoStringIndexOfExpression io => new MongoStringIndexOfExpression(
                Rewrite(io.Haystack, prefix), Rewrite(io.Needle, prefix),
                io.Start is null ? null : Rewrite(io.Start, prefix)),
            MongoStringLengthExpression sl => new MongoStringLengthExpression(Rewrite(sl.Operand, prefix)),
            MongoMathExpression m => new MongoMathExpression(
                m.Function, m.Operands.Select(o => Rewrite(o, prefix)).ToList(), m.Type),
            MongoTrimExpression t => new MongoTrimExpression(
                Rewrite(t.Source, prefix), t.Side, t.Chars is null ? null : Rewrite(t.Chars, prefix)),
            MongoSubstringExpression s => new MongoSubstringExpression(
                Rewrite(s.Source, prefix), Rewrite(s.Start, prefix), s.Length is null ? null : Rewrite(s.Length, prefix)),
            MongoReplaceExpression r => new MongoReplaceExpression(
                Rewrite(r.Input, prefix), Rewrite(r.Find, prefix), Rewrite(r.Replacement, prefix)),
            MongoStringCompareExpression cmp => new MongoStringCompareExpression(
                Rewrite(cmp.Left, prefix), Rewrite(cmp.Right, prefix)),
            MongoStringFirstOrLastExpression fl => new MongoStringFirstOrLastExpression(Rewrite(fl.Source, prefix), fl.Kind),
            MongoDateTimeOffsetLocalExpression l => new MongoDateTimeOffsetLocalExpression(
                (MongoFieldExpression)Rewrite(l.Operand, prefix)),
            // Carry NullSafe: dropping it removes the $ifNull, so a missing element stops comparing equal to null
            // and `d.Ship == null` in a prefixed scope loses rows.
            MongoElementRefExpression er => new MongoElementRefExpression(
                prefix + "." + er.Path, er.Type, er.NullSafe, er.ValueProperty, er.ThrowsOnNull),
            // Root-anchored by definition, so prefixing would be wrong.
            MongoOuterFieldExpression => expr,
            MongoConcatExpression concat => new MongoConcatExpression(
                concat.Operands.Select(o => Rewrite(o, prefix)).ToList()),
            MongoConstantExpression or MongoParameterExpression => expr,
            // Elements are constants/parameters today; recursion is for consistency.
            MongoValueListExpression list => new MongoValueListExpression(
                list.Elements.Select(el => Rewrite(el, prefix)).ToList()),
            MongoTupleExpression tuple => new MongoTupleExpression(
                tuple.Elements.Select(el => Rewrite(el, prefix)).ToList()),
            _ => throw new NativeTranslationNotSupportedException(
                $"Cannot prefix-rewrite MongoExpression node '{expr.GetType().Name}'.")
        };
}
