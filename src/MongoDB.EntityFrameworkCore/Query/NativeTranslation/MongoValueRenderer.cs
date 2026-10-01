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
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore.Metadata;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.EntityFrameworkCore.Query.Expressions;
using MongoDB.EntityFrameworkCore.Serializers;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>
/// Renders a <see cref="MongoConstantExpression"/> or <see cref="MongoParameterExpression"/> to a
/// <see cref="BsonValue"/>, and an <c>$in</c> haystack to a <see cref="BsonArray"/>. Shared by <see cref="MongoQueryLanguageRenderer"/> and
/// <see cref="MongoAggregationExpressionRenderer"/> so a constant and a parameter of the same value emit identical
/// BSON and serializer failures are handled uniformly.
/// </summary>
internal static class MongoValueRenderer
{
    /// <summary>
    /// Renders <paramref name="node"/> to a concrete value, or to a placeholder sentinel recorded in
    /// <paramref name="placeholders"/> for a parameter.
    /// </summary>
    /// <exception cref="NativeTranslationNotSupportedException">
    /// <paramref name="node"/> is not a value node, or a constant cannot be serialized.
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
                if (parameter.ArrayElementIndex is int elementIndex)
                    return placeholders.CreateArrayElementPlaceholder(
                        parameter.Name,
                        elementIndex,
                        parameter.ForSerialization is null
                            ? null
                            : BsonSerializerFactory.GetPropertySerializationInfo(parameter.ForSerialization).Serializer);

                if (parameter.ForSerialization is null)
                    return placeholders.CreatePlaceholder(parameter.Name, serializer: null);
                var info = BsonSerializerFactory.GetPropertySerializationInfo(parameter.ForSerialization);
                return parameter.ExtractFromEntityValue
                    ? placeholders.CreateEntityMemberPlaceholder(parameter.Name, parameter.ForSerialization, info.Serializer)
                    : placeholders.CreatePlaceholder(parameter.Name, info.Serializer);

            default:
                throw new NativeTranslationNotSupportedException(
                    $"Cannot render value node of type '{node.GetType().Name}'.");
        }
    }

    /// <summary>
    /// Whether <see cref="RenderInValues"/> renders <paramref name="values"/> without throwing.
    /// </summary>
    internal static bool IsRenderableInValues(MongoExpression values)
        => values is MongoConstantExpression { Value: System.Collections.IEnumerable } or MongoParameterExpression
            or MongoValueListExpression;

    /// <summary>
    /// Renders the haystack of an <c>$in</c>/<c>$nin</c> (either dialect) to a BSON array, or to an array placeholder
    /// for a parameter collection.
    /// </summary>
    /// <exception cref="NativeTranslationNotSupportedException">
    /// <paramref name="values"/> is not a shape <see cref="IsRenderableInValues"/> admits.
    /// </exception>
    internal static BsonValue RenderInValues(MongoExpression values, PlaceholderTable placeholders)
    {
        switch (values)
        {
            case MongoConstantExpression { Value: System.Collections.IEnumerable items } constant:
            {
                var array = new BsonArray();
                foreach (var item in items)
                    array.Add(RenderValue(new MongoConstantExpression(item, constant.ForSerialization!), placeholders));
                return array;
            }
            case MongoParameterExpression parameter:
            {
                // No backing property: only for a computed needle (TranslateInValuesRaw), so use a default
                // serializer for RawElementType.
                var elementSerializer = parameter.ForSerialization is not null
                    ? BsonSerializerFactory.GetPropertySerializationInfo(parameter.ForSerialization).Serializer
                    : parameter.RawElementType is { } rawElementType
                        ? typeof(ITuple).IsAssignableFrom(rawElementType)
                            // The driver's own Tuple/ValueTuple serializers write one BSON array per tuple, matching how
                            // a MongoTupleExpression needle renders; BsonSerializerFactory.CreateTypeSerializer would fall
                            // through to a class-map (document) serializer for a tuple type.
                            ? BsonSerializer.LookupSerializer(rawElementType)
                            : BsonSerializerFactory.CreateTypeSerializer(rawElementType)
                        : StringSerializer.Instance;
                return parameter.ExtractEntityKeyFromArrayElements
                    ? placeholders.CreateEntityKeyArrayPlaceholder(parameter.Name, parameter.ForSerialization!, elementSerializer)
                    : placeholders.CreateArrayPlaceholder(parameter.Name, elementSerializer);
            }
            case MongoValueListExpression list:
            {
                var array = new BsonArray();
                foreach (var element in list.Elements)
                    array.Add(RenderValue(element, placeholders));
                return array;
            }
            default:
                throw new NativeTranslationNotSupportedException(
                    $"Cannot render 'in' values of type '{values.GetType().Name}'.");
        }
    }

    // Coerces to the property's CLR type before serializing so the serializer's hard cast succeeds (the factory
    // coerces to the serializer's ValueType, which differs for value-converted properties). Serializer failures
    // become NativeTranslationNotSupportedException so the query falls back rather than crashing on a raw cast.
    private static BsonValue ToBsonValue(IProperty property, object? value)
    {
        var info = BsonSerializerFactory.GetPropertySerializationInfo(property);
        try
        {
            value = BsonValueSerializer.Coerce(property.ClrType, value);
            return BsonValueSerializer.SerializeNullAware(info.Serializer, value);
        }
        catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException
                                       or InvalidOperationException)
        {
            throw new NativeTranslationNotSupportedException(
                $"Native predicate translation cannot serialize the constant value for property '{property.Name}'.");
        }
    }
}
