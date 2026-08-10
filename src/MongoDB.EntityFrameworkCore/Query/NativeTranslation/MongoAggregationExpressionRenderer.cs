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

using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Query.Expressions;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>
/// Renders a dialect-agnostic <see cref="MongoExpression"/> subtree to a MongoDB
/// <em>aggregation expression</em> (the body that sits inside <c>{ $expr: … }</c>).
/// Used only for subtrees that have no correct query-dialect rendering (field-to-field
/// comparisons, arithmetic operands); the query renderer wraps the result in <c>$expr</c>.
/// </summary>
internal static class MongoAggregationExpressionRenderer
{
    /// <summary>
    /// Renders <paramref name="node"/> to an aggregation-expression <see cref="BsonValue"/>
    /// (the body that sits inside <c>{ $expr: … }</c>).
    /// </summary>
    /// <param name="node">The root <see cref="MongoExpression"/> subtree to render.</param>
    /// <param name="placeholders">
    /// Receives one entry per <see cref="MongoParameterExpression"/> encountered.
    /// Each entry's corresponding sentinel is embedded in the returned <see cref="BsonValue"/>.
    /// </param>
    /// <param name="elementVariable">
    /// The <c>$filter</c>/<c>$map</c> <c>as</c> variable name currently in scope, or <see langword="null"/> at
    /// the document root. When non-null, a field reference renders as <c>"$$" + elementVariable + "." + path</c>
    /// instead of <c>"$" + path</c> — the enclosing document is no longer addressable as <c>$path</c> once a
    /// <see cref="MongoFilteredSizeExpression"/>'s <c>$filter</c> has bound the element to a variable. Every
    /// pre-existing call site omits this (it defaults to <see langword="null"/>), which is what keeps their
    /// emitted MQL byte-identical.
    /// </param>
    /// <returns>
    /// A <see cref="BsonValue"/> representing the aggregation-expression body.
    /// </returns>
    /// <exception cref="NativeTranslationNotSupportedException">
    /// Thrown for any node type or operator not handled by this renderer.
    /// </exception>
    public static BsonValue Render(MongoExpression node, PlaceholderTable placeholders, string? elementVariable = null)
        => node switch
        {
            MongoFieldExpression field => FieldRef(field.ElementName, elementVariable),
            MongoElementRefExpression elementRef => FieldRef(elementRef.Path, elementVariable),
            MongoConstantExpression or MongoParameterExpression => MongoValueRenderer.RenderValue(node, placeholders),
            MongoBinaryExpression binary => RenderBinary(binary, placeholders, elementVariable),
            MongoSizeExpression size => RenderSize(size, elementVariable),
            MongoFilteredSizeExpression filtered => RenderFilteredSize(filtered, placeholders, elementVariable),
            MongoConvertExpression convert
                => new BsonDocument(
                    MongoConvertExpression.ToOperatorFor(convert.Type)
                        ?? throw new NativeTranslationNotSupportedException(
                            $"MQL has no conversion operator for '{convert.Type.Name}'. A convert to an "
                            + "unrenderable target should have been declined at translate time."),
                    Render(convert.Operand, placeholders, elementVariable)),
            _ => throw new NativeTranslationNotSupportedException(
                $"MongoAggregationExpressionRenderer does not support node type '{node.GetType().Name}'.")
        };

    /// <summary>
    /// Returns whether <see cref="Render"/> would render <paramref name="node"/> without throwing.
    /// </summary>
    /// <remarks>
    /// <b>This method and <see cref="Render"/> must be changed together.</b> It is the aggregation-dialect
    /// counterpart of <c>MongoQueryLanguageRenderer.IsQueryDialectRenderable</c>, and exists for the same
    /// reason: a caller that builds a node the renderer cannot express turns a clean translate-time decline
    /// into a render-time throw. That matters for a filtered count in particular
    /// (<c>Select(b =&gt; new { N = b.Posts.Count(pred) })</c>), which has no working driver-LINQ fallback.
    /// <para>
    /// <b>Known missing arms:</b> <c>MongoInExpression</c> and <c>MongoUnaryExpression</c> have no
    /// aggregation-dialect rendering and fall to the <c>_ =&gt; false</c> catch-all below — fail-closed and
    /// safe, but it means a client-collection <c>Contains</c> or a unary <c>Not</c> in a computed sort key
    /// (gated via <c>NativeSlotPopulator.TryTranslateComputedSortKey</c>) declines to fallback rather than
    /// going native. Adding either arm here means adding it to <see cref="Render"/> at the same time.
    /// </para>
    /// </remarks>
    public static bool CanRender(MongoExpression node)
        => node switch
        {
            MongoFieldExpression or MongoElementRefExpression => true,
            MongoConstantExpression or MongoParameterExpression => true,
            MongoBinaryExpression binary
                => IsRenderableOperator(binary.Operator) && CanRender(binary.Left) && CanRender(binary.Right),
            MongoSizeExpression => true,
            MongoFilteredSizeExpression filtered => CanRender(filtered.ElementPredicate),
            MongoConvertExpression convert
                => MongoConvertExpression.ToOperatorFor(convert.Type) is not null && CanRender(convert.Operand),
            _ => false
        };

    // Exactly the operators RenderBinary's own switch maps below — every MongoBinaryOperator member, as it
    // happens (RenderBinary has no unmapped member today), but this must be re-checked against RenderBinary's
    // switch whenever either changes, not assumed to track the enum automatically.
    private static bool IsRenderableOperator(MongoBinaryOperator op)
        => op is MongoBinaryOperator.Equal
            or MongoBinaryOperator.NotEqual
            or MongoBinaryOperator.LessThan
            or MongoBinaryOperator.LessThanOrEqual
            or MongoBinaryOperator.GreaterThan
            or MongoBinaryOperator.GreaterThanOrEqual
            or MongoBinaryOperator.AndAlso
            or MongoBinaryOperator.OrElse
            or MongoBinaryOperator.Add
            or MongoBinaryOperator.Subtract
            or MongoBinaryOperator.Multiply
            or MongoBinaryOperator.Divide
            or MongoBinaryOperator.Modulo;

    // Inside a $filter's cond the enclosing document is no longer addressable as "$path" — the element is bound to
    // a variable, so a field of it is "$$<var>.<path>". elementVariable is null everywhere else, which is what
    // keeps every pre-existing call site's emitted MQL byte-identical.
    private static BsonValue FieldRef(string path, string? elementVariable)
        => elementVariable is null ? "$" + path : "$$" + elementVariable + "." + path;

    // A missing or explicitly-null array makes $size a hard server error that aborts the whole aggregate, so an
    // EMBEDDED array path is wrapped in $ifNull (count 0 — what LINQ answers for a missing embedded array). A
    // $lookup output alias is always an array, so that path keeps the plain form and its committed spec
    // baselines stay byte-identical. See MongoSizeExpression's remarks.
    private static BsonValue RenderSize(MongoSizeExpression size, string? elementVariable)
        => size.NullSafe
            ? new BsonDocument("$size",
                new BsonDocument("$ifNull", new BsonArray { FieldRef(size.FieldName, elementVariable), new BsonArray() }))
            : new BsonDocument("$size", FieldRef(size.FieldName, elementVariable));

    private static BsonValue RenderFilteredSize(
        MongoFilteredSizeExpression node, PlaceholderTable placeholders, string? elementVariable)
    {
        // Each nesting level needs its own variable name. Deriving it from the enclosing one ("e", "ee", "eee")
        // keeps them distinct without threading a counter, and keeps every name lowercase-initial, as the
        // server requires of a $filter `as` name.
        var variable = elementVariable is null ? "e" : elementVariable + "e";

        return new BsonDocument("$size",
            new BsonDocument("$filter", new BsonDocument
            {
                // $ifNull is MANDATORY: $filter over a missing or explicitly-null array is a hard server error
                // that aborts the whole aggregate command. [] yields 0, which is what LINQ answers for a missing
                // array.
                { "input", new BsonDocument("$ifNull", new BsonArray { FieldRef(node.ArrayPath, elementVariable), new BsonArray() }) },
                { "as", variable },
                { "cond", Render(node.ElementPredicate, placeholders, variable) }
            }));
    }

    private static BsonValue RenderBinary(MongoBinaryExpression binary, PlaceholderTable placeholders, string? elementVariable)
    {
        var op = binary.Operator switch
        {
            MongoBinaryOperator.Equal => "$eq",
            MongoBinaryOperator.NotEqual => "$ne",
            MongoBinaryOperator.LessThan => "$lt",
            MongoBinaryOperator.LessThanOrEqual => "$lte",
            MongoBinaryOperator.GreaterThan => "$gt",
            MongoBinaryOperator.GreaterThanOrEqual => "$gte",
            MongoBinaryOperator.AndAlso => "$and",
            MongoBinaryOperator.OrElse => "$or",
            MongoBinaryOperator.Add => "$add",
            MongoBinaryOperator.Subtract => "$subtract",
            MongoBinaryOperator.Multiply => "$multiply",
            MongoBinaryOperator.Divide => "$divide",
            MongoBinaryOperator.Modulo => "$mod",
            _ => throw new NativeTranslationNotSupportedException(
                $"Unsupported aggregation operator '{binary.Operator}'.")
        };

        var left = Render(binary.Left, placeholders, elementVariable);
        var right = Render(binary.Right, placeholders, elementVariable);
        return new BsonDocument(op, new BsonArray { left, right });
    }
}
