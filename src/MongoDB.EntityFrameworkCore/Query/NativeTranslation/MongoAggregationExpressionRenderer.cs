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
    /// <returns>
    /// A <see cref="BsonValue"/> representing the aggregation-expression body.
    /// </returns>
    /// <exception cref="NativeTranslationNotSupportedException">
    /// Thrown for any node type or operator not handled by this renderer.
    /// </exception>
    public static BsonValue Render(MongoExpression node, PlaceholderTable placeholders)
        => node switch
        {
            MongoFieldExpression field => "$" + field.ElementName,
            MongoElementRefExpression elementRef => "$" + elementRef.Path,
            MongoConstantExpression or MongoParameterExpression => MongoValueRenderer.RenderValue(node, placeholders),
            MongoBinaryExpression binary => RenderBinary(binary, placeholders),
            MongoSizeExpression size => new BsonDocument("$size", "$" + size.FieldName),
            _ => throw new NativeTranslationNotSupportedException(
                $"MongoAggregationExpressionRenderer does not support node type '{node.GetType().Name}'.")
        };

    private static BsonValue RenderBinary(MongoBinaryExpression binary, PlaceholderTable placeholders)
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

        var left = Render(binary.Left, placeholders);
        var right = Render(binary.Right, placeholders);
        return new BsonDocument(op, new BsonArray { left, right });
    }
}
