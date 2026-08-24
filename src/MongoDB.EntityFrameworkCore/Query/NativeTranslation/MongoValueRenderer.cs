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
/// Renders a <see cref="MongoConstantExpression"/> or <see cref="MongoParameterExpression"/> value node
/// to a <see cref="BsonValue"/>. Shared by both dialect renderers (<see cref="MongoQueryLanguageRenderer"/>
/// and <see cref="MongoAggregationExpressionRenderer"/>) so a constant and a parameter of the same value
/// always emit identical BSON, and so the serializer-failure diagnostics are applied uniformly.
/// </summary>
internal static class MongoValueRenderer
{
    /// <summary>
    /// Renders <paramref name="node"/> (a constant or parameter) to a <see cref="BsonValue"/>, recording
    /// parameters as placeholders in <paramref name="placeholders"/>.
    /// </summary>
    /// <param name="node">The value node to render.</param>
    /// <param name="placeholders">The placeholder table that records parameter sentinels.</param>
    /// <returns>The rendered <see cref="BsonValue"/> (a concrete value or a placeholder sentinel).</returns>
    /// <exception cref="NativeTranslationNotSupportedException">
    /// Thrown when <paramref name="node"/> is not a value node, or a constant cannot be serialized.
    /// </exception>
    internal static BsonValue RenderValue(MongoExpression node, PlaceholderTable placeholders)
    {
        switch (node)
        {
            case MongoConstantExpression constant:
                return constant.ForSerialization is null
                    ? BsonValue.Create(constant.Value)
                    : ToBsonValue(constant.ForSerialization, constant.Value);

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

    // Serializes value to a BsonValue using the property's serializer, coercing the CLR type first so the
    // serializer's hard cast succeeds. Coerces to the property's CLR type (compile-time path); the factory
    // coerces to the serializer's ValueType — these differ for value-converted properties, so the caller's
    // IProperty target is used. Serializer failures are surfaced as NativeTranslationNotSupportedException
    // so the query falls back (or throws under NativeOnly) rather than crashing with a raw cast error.
    private static BsonValue ToBsonValue(IProperty property, object? value)
    {
        var info = BsonSerializerFactory.GetPropertySerializationInfo(property);
        try
        {
            value = BsonValueSerializer.Coerce(property.ClrType, value);
            return BsonValueSerializer.SerializeThroughWriter(info.Serializer, value);
        }
        catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException
                                       or InvalidOperationException)
        {
            throw new NativeTranslationNotSupportedException(
                $"Native predicate translation cannot serialize the constant value for property '{property.Name}'.");
        }
    }
}
