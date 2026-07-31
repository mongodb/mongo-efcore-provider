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

using MongoDB.EntityFrameworkCore.Query.Expressions;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>
/// Rewrites every <see cref="MongoFieldExpression"/> in a translated predicate tree so its element name is
/// prefixed with a document scope path (e.g. the <c>_lookup_&lt;Nav&gt;</c> alias of a reference SelectMany's
/// unwound element), turning a predicate translated against the inner target entity type
/// (<c>Total</c>) into one that matches the unwound-and-prefixed document (<c>_lookup_Refs.Total</c>).
/// Generalizes the single-field prefixing <c>NativeSelectManyBinder.TryTranslateScopedField</c> performs.
/// </summary>
internal static class MongoFieldPrefixRewriter
{
    public static MongoExpression Rewrite(MongoExpression expr, string prefix)
        => expr switch
        {
            MongoFieldExpression f => new MongoFieldExpression(f.Property, prefix + "." + f.ElementName),
            MongoBinaryExpression b => new MongoBinaryExpression(
                b.Operator, Rewrite(b.Left, prefix), Rewrite(b.Right, prefix)),
            MongoUnaryExpression u => new MongoUnaryExpression(u.Operator, Rewrite(u.Operand, prefix)),
            MongoInExpression i => new MongoInExpression(
                (MongoFieldExpression)Rewrite(i.Field, prefix), Rewrite(i.Values, prefix), i.Negated),
            MongoRegexExpression r => new MongoRegexExpression(
                (MongoFieldExpression)Rewrite(r.Field, prefix), r.Kind, Rewrite(r.Term, prefix), r.Negated),
            // Prefix the ARRAY path only. The element predicate's field paths are ELEMENT-relative (that is
            // what $elemMatch requires), so rewriting them would mis-address every field inside the
            // $elemMatch and silently match nothing.
            MongoElemMatchExpression e => new MongoElemMatchExpression(
                prefix + "." + e.ArrayPath, e.ElementPredicate, e.Negated),
            // A size node's FieldName is a document path like any field reference, so it prefixes the same way.
            // This case is LOAD-BEARING, not defensive: an owned SelectMany's inner filter reaches Rewrite, so a
            // count inside one (SelectMany(b => b.Posts.Where(p => p.Comments.Count > 1), …)) would otherwise
            // hit the throw below — turning a clean decline into a crash inside pre-existing code.
            MongoSizeExpression s => new MongoSizeExpression(prefix + "." + s.FieldName, s.Type, s.NullSafe),
            // Prefix the ARRAY path only, for the same reason as MongoElemMatchExpression above: the element predicate's
            // field paths are ELEMENT-relative (that is what the $filter variable addresses), so rewriting them would
            // mis-address every field inside the $filter.
            MongoFilteredSizeExpression f => new MongoFilteredSizeExpression(prefix + "." + f.ArrayPath, f.ElementPredicate, f.Type),
            MongoConstantExpression or MongoParameterExpression => expr,
            _ => throw new NativeTranslationNotSupportedException(
                $"Cannot prefix-rewrite MongoExpression node '{expr.GetType().Name}'.")
        };
}
