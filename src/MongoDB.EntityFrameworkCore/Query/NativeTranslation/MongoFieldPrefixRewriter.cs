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
/// Rewrites every <see cref="MongoFieldExpression"/> in a translated predicate tree so its element name is
/// prefixed with a document scope path (e.g. the <c>_lookup_&lt;Nav&gt;</c> alias of a reference SelectMany's
/// unwound element), turning a predicate translated against the inner target entity type
/// (<c>Total</c>) into one that matches the unwound-and-prefixed document (<c>_lookup_Refs.Total</c>).
/// Generalizes the single-field prefixing <c>NativeSelectManyBinder.TryTranslateScopedField</c> performs.
/// </summary>
/// <remarks>
/// <para>
/// <b>Declines, never throws, for an unhandled node kind.</b> This used to be a <c>Rewrite</c> that threw from
/// its own <c>switch</c> default, which meant a node kind added to <c>TryTranslateValue</c> without a matching
/// arm here silently converted a shape that had been declining cleanly (and falling back to driver-LINQ) into a
/// hard failure in every <c>MongoQueryMode</c>. Returning <see langword="false"/> makes the miss a
/// fallback instead of a crash; every caller already has a decline path, because they all sit in
/// <see cref="bool"/>-returning <c>Try*</c> methods. <c>MongoExpressionNodeCoverageTests</c> pins which node
/// kinds currently reach the decline.
/// </para>
/// </remarks>
internal static class MongoFieldPrefixRewriter
{
    /// <summary>
    /// Prefixes every document path in <paramref name="expr"/> with <paramref name="prefix"/>, or returns
    /// <see langword="false"/> if the tree contains a node kind with no prefixing rule.
    /// </summary>
    /// <remarks>
    /// The recursive walk keeps using the throw as its internal decline channel and this method catches it,
    /// rather than threading a <see cref="bool"/> through all seventeen arms — deliberately contained, since the
    /// throw never escapes this class. (The recursion is slated to be replaced by a
    /// <c>MongoExpression.VisitChildren</c>-based structural rewriter, at which point the default arm, and this
    /// bridge with it, disappear.)
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
            // The computed-needle sibling of MongoInExpression above — the needle is a general expression
            // (e.g. a $concat), not necessarily a bare field, so it recurses through Rewrite rather than
            // being cast to MongoFieldExpression.
            MongoComputedInExpression ci => new MongoComputedInExpression(
                Rewrite(ci.Needle, prefix), Rewrite(ci.Values, prefix), ci.Negated),
            // EF-382: the array-contains-value mirror of MongoInExpression above — same treatment, since
            // Field addresses a genuine document path exactly like MongoInExpression.Field does (Value is
            // always a MongoConstantExpression today, which passes through unchanged below, but Rewrite is
            // still called on it for the same reason RenderIn recurses into its Values: consistency, not a
            // currently-exercised need).
            MongoArrayContainsExpression ac => new MongoArrayContainsExpression(
                (MongoFieldExpression)Rewrite(ac.Field, prefix), Rewrite(ac.Value, prefix), ac.Negated),
            MongoRegexExpression r => new MongoRegexExpression(
                (MongoFieldExpression)Rewrite(r.Field, prefix), r.Kind, Rewrite(r.Term, prefix), r.Negated),
            // Prefix the ARRAY path only. The element predicate's field paths are ELEMENT-relative (that is
            // what $elemMatch requires), so rewriting them would mis-address every field inside the
            // $elemMatch and silently match nothing.
            MongoElemMatchExpression e => new MongoElemMatchExpression(
                prefix + "." + e.ArrayPath, e.ElementPredicate, e.Negated),
            // A size node's FieldName is a document path like any field reference, so it prefixes the same way.
            // Required, not defensive: an owned SelectMany's inner filter reaches Rewrite, so a count inside
            // one (SelectMany(b => b.Posts.Where(p => p.Comments.Count > 1), …)) would otherwise hit the
            // throw below.
            MongoSizeExpression s => new MongoSizeExpression(prefix + "." + s.FieldName, s.Type, s.NullSafe),
            // Prefix the ARRAY path only, for the same reason as MongoElemMatchExpression above: the element predicate's
            // field paths are ELEMENT-relative (that is what the $filter variable addresses), so rewriting them would
            // mis-address every field inside the $filter.
            MongoFilteredSizeExpression f => new MongoFilteredSizeExpression(prefix + "." + f.ArrayPath, f.ElementPredicate, f.Type),
            // The operand carries the field path; the conversion itself has nothing to prefix.
            MongoConvertExpression c => new MongoConvertExpression(Rewrite(c.Operand, prefix), c.Type),
            // This switch's default THROWS rather than declining gracefully (unlike every other gate in this
            // area — see the durable-invariants list in Query/AGENTS.md). A SelectMany result-selector
            // projection leaf that needs cross-scope field prefixing reaches here for any node kind
            // TryTranslateValue can produce; a new node kind added there must get an arm here too, or it
            // silently regresses a shape that used to decline cleanly and fall back to driver-LINQ into a
            // hard failure in every MongoQueryMode.
            MongoConditionalExpression c2 => new MongoConditionalExpression(
                Rewrite(c2.Test, prefix), Rewrite(c2.IfTrue, prefix), Rewrite(c2.IfFalse, prefix)),
            MongoDatePartExpression dp => new MongoDatePartExpression(Rewrite(dp.Operand, prefix), dp.Part),
            MongoDateTimeOffsetLocalExpression l => new MongoDateTimeOffsetLocalExpression(
                (MongoFieldExpression)Rewrite(l.Operand, prefix)),
            // NullSafe MUST be carried across — dropping it (as this arm used to) silently removed the $ifNull
            // wrapper the aggregation renderer keys off, so a MISSING element stopped comparing equal to null
            // and an owned-nav null check (`d.Ship == null`) inside a prefixed scope quietly lost rows. The
            // sibling MongoSizeExpression arm above threads its own NullSafe for the same reason.
            MongoElementRefExpression er => new MongoElementRefExpression(
                prefix + "." + er.Path, er.Type, er.NullSafe),
            // Root-anchored BY DEFINITION — it renders at the document root regardless of any enclosing element
            // scope (see the node's own remarks and the aggregation renderer's elementVariable: null arm), so
            // prefixing it would be actively wrong. Pass through unchanged. This arm previously did not exist,
            // which meant an outer-scoped field inside a rewritten subtree hit the throwing default and turned a
            // clean fallback into a hard failure in every MongoQueryMode.
            MongoOuterFieldExpression => expr,
            MongoConcatExpression concat => new MongoConcatExpression(
                concat.Operands.Select(o => Rewrite(o, prefix)).ToList()),
            MongoConstantExpression or MongoParameterExpression => expr,
            _ => throw new NativeTranslationNotSupportedException(
                $"Cannot prefix-rewrite MongoExpression node '{expr.GetType().Name}'.")
        };
}
