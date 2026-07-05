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

using System;
using Microsoft.EntityFrameworkCore.Metadata;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Query.Expressions;
using MongoDB.EntityFrameworkCore.Serializers;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>
/// Renders a dialect-agnostic <see cref="MongoExpression"/> subtree to a MongoDB
/// <em>aggregation expression</em> (the body that sits inside <c>{ $expr: … }</c>).
/// Used only for subtrees that have no correct query-dialect rendering (field-to-field
/// comparisons, arithmetic operands); the query renderer wraps the result in <c>$expr</c>.
/// </summary>
internal sealed class MongoAggregationExpressionRenderer
{
    public BsonValue Render(MongoExpression node, PlaceholderTable placeholders)
        => node switch
        {
            MongoFieldExpression field => "$" + field.ElementName,
            MongoConstantExpression or MongoParameterExpression => RenderValue(node, placeholders),
            MongoBinaryExpression binary => RenderBinary(binary, placeholders),
            _ => throw new NativeTranslationNotSupportedException(
                $"MongoAggregationExpressionRenderer does not support node type '{node.GetType().Name}'.")
        };

    private BsonValue RenderBinary(MongoBinaryExpression binary, PlaceholderTable placeholders)
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

    // Constants/parameters serialize exactly as in the query renderer so a constant and a
    // parameter of the same value emit identical BSON.
    private BsonValue RenderValue(MongoExpression node, PlaceholderTable placeholders)
    {
        switch (node)
        {
            case MongoConstantExpression constant:
                return constant.ForSerialization is null
                    ? BsonValue.Create(constant.Value)
                    : SerializeConstant(constant.ForSerialization, constant.Value);
            case MongoParameterExpression parameter:
                if (parameter.ForSerialization is null)
                    return placeholders.CreatePlaceholder(parameter.Name, serializer: null);
                var info = BsonSerializerFactory.GetPropertySerializationInfo(parameter.ForSerialization);
                return placeholders.CreatePlaceholder(parameter.Name, info.Serializer);
            default:
                throw new NativeTranslationNotSupportedException(
                    $"Cannot render value node of type '{node.GetType().Name}'.");
        }
    }

    private static BsonValue SerializeConstant(IProperty property, object? value)
    {
        var info = BsonSerializerFactory.GetPropertySerializationInfo(property);
        value = BsonValueSerializer.Coerce(property.ClrType, value);
        return BsonValueSerializer.SerializeThroughWriter(info.Serializer, value);
    }
}
