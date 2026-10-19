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
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Metadata;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Options;
using MongoDB.EntityFrameworkCore.Query.Expressions;
using MongoDB.EntityFrameworkCore.Serializers;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>
/// <see cref="MongoExpressionTranslator"/> — string-keyed dictionary properties: <c>d.Dict["key"]</c> (an entry compared
/// with a value) and <c>d.Dict.ContainsKey("key")</c>.
/// </summary>
/// <remarks>
/// A dictionary property is always stored as a BSON document (<c>DictionaryRepresentation.Document</c>; the serializer is
/// checked, so a converter or a future option declines instead of mis-rendering), so an entry is the sub-field
/// <c>&lt;dictionary path&gt;.&lt;key&gt;</c>. Only a constant key that a dotted path can address is admitted (not empty,
/// not starting with <c>$</c>, no <c>.</c>, no NUL). A missing key reads as null here, where C# throws
/// <c>KeyNotFoundException</c>: the same as driver-LINQ, so no parity difference.
/// </remarks>
internal sealed partial class MongoExpressionTranslator
{
    private static bool TryGetStringKeyedValueType(Type type, [NotNullWhen(true)] out Type? valueType)
    {
        valueType = null;
        var candidates = type.IsInterface ? type.GetInterfaces().Prepend(type) : type.GetInterfaces();
        foreach (var candidate in candidates)
        {
            if (candidate.IsGenericType
                && (candidate.GetGenericTypeDefinition() == typeof(IDictionary<,>)
                    || candidate.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>))
                && candidate.GetGenericArguments() is [var keyType, var entryType]
                && keyType == typeof(string))
            {
                valueType = entryType;
                return true;
            }
        }

        return false;
    }

    // A dotted path can't address "", a key containing '.', one starting with '$', or one containing NUL.
    private static bool IsAddressableDictionaryKey(string key)
        => key.Length > 0 && key[0] != '$' && !key.Contains('.') && !key.Contains('\0');

    private bool TryResolveDictionaryProperty(
        Expression receiver, [NotNullWhen(true)] out IProperty? property, [NotNullWhen(true)] out string? path)
    {
        if (!TryResolveMember(Unwrap(receiver), out property, out path, out var isOuter)
            || isOuter
            || !TryGetStringKeyedValueType(property.ClrType, out _)
            || !NativeGroupByBinder.HasDefaultKeySerialization(property))
        {
            return false;
        }

        return BsonSerializerFactory.GetPropertySerializationInfo(property).Serializer
            is IBsonDictionarySerializer { DictionaryRepresentation: DictionaryRepresentation.Document };
    }

    /// <summary><c>d["key"]</c> with a constant key over a dictionary property; <see langword="false"/> otherwise.</summary>
    private bool TryResolveDictionaryEntry(Expression node, [NotNullWhen(true)] out MongoDictionaryEntryExpression? entry)
    {
        entry = null;
        if (node is not MethodCallExpression { Method.Name: "get_Item", Object: { } receiver, Arguments: [var keyArgument] }
            || !TryGetStringKeyedValueType(receiver.Type, out var valueType)
            || Unwrap(keyArgument) is not ConstantExpression { Value: string key }
            || !IsAddressableDictionaryKey(key)
            || !TryResolveDictionaryProperty(receiver, out var property, out var path))
        {
            return false;
        }

        entry = new MongoDictionaryEntryExpression(property, path + "." + key, valueType);
        return true;
    }

    private bool TryTranslateDictionaryContainsKey(MethodCallExpression call, [NotNullWhen(true)] out MongoExpression? result)
    {
        result = null;
        if (call is not { Method.Name: "ContainsKey", Object: { } receiver, Arguments: [var keyArgument] }
            || !TryGetStringKeyedValueType(receiver.Type, out _)
            || Unwrap(keyArgument) is not ConstantExpression { Value: string key }
            || !IsAddressableDictionaryKey(key)
            || !TryResolveDictionaryProperty(receiver, out _, out var path))
        {
            return false;
        }

        result = new MongoDictionaryContainsKeyExpression(path + "." + key, negated: false);
        return true;
    }

    // The value side is serialized by the dictionary's own value serializer (what SaveChanges wrote), not by an IProperty.
    private static MongoExpression? TranslateDictionaryValue(Expression valueNode, MongoDictionaryEntryExpression entry)
    {
        var valueSerializer = ((IBsonDictionarySerializer)BsonSerializerFactory
            .GetPropertySerializationInfo(entry.DictionaryProperty).Serializer).ValueSerializer;

        switch (valueNode)
        {
            case ConstantExpression { Value: null }:
                return new MongoConstantExpression(BsonNull.Value, forSerialization: null);

            case ConstantExpression constant:
                try
                {
                    var coerced = BsonValueSerializer.Coerce(entry.Type, constant.Value);
                    return new MongoConstantExpression(
                        BsonValueSerializer.SerializeThroughWriter(valueSerializer, coerced), forSerialization: null);
                }
                catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException
                                               or InvalidOperationException)
                {
                    return null;
                }

            case var parameter when NativeQueryParameter.TryGetQueryParameterName(parameter, out var name):
                return new MongoParameterExpression(
                    name, forSerialization: null, valueType: parameter.Type, explicitSerializer: valueSerializer);

            default:
                return null;
        }
    }

    // Equality is exact for any value type (a struct or sub-document included). A relational operator runs on the stored
    // form, so only types whose stored order is their CLR order.
    private static bool IsOrderPreservingEntryType(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        return underlying == typeof(int) || underlying == typeof(long) || underlying == typeof(short)
               || underlying == typeof(byte) || underlying == typeof(double) || underlying == typeof(string);
    }

    // Peels boxing and nullable lifts; any other convert (a numeric cast) is not absorbed into the entry comparison.
    private static bool HasNonIdentityConvert(Expression node, Type entryType)
    {
        var underlyingEntry = Nullable.GetUnderlyingType(entryType) ?? entryType;
        while (node is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert)
        {
            var to = Nullable.GetUnderlyingType(convert.Type) ?? convert.Type;
            var from = Nullable.GetUnderlyingType(convert.Operand.Type) ?? convert.Operand.Type;
            if (to != typeof(object) && !(to == underlyingEntry && from == underlyingEntry))
                return true;

            node = convert.Operand;
        }

        return false;
    }

    /// <summary>
    /// <c>d.Dict["key"] OP value</c> (either side). Returns <see langword="true"/> when it owns the comparison, so an
    /// unresolvable value declines the whole comparison instead of falling through to the general path, which would
    /// mis-handle a dictionary <c>get_Item</c>.
    /// </summary>
    private bool TryTranslateDictionaryEntryComparison(
        Expression left, Expression right, ExpressionType nodeType, out MongoBinaryExpression? result)
    {
        result = null;
        var unwrappedLeft = Unwrap(left);
        var unwrappedRight = Unwrap(right);

        MongoDictionaryEntryExpression? entry;
        Expression entrySide, valueSide;
        bool mirrored;
        if (TryResolveDictionaryEntry(unwrappedLeft, out entry))
        {
            (entrySide, valueSide, mirrored) = (left, unwrappedRight, false);
        }
        else if (TryResolveDictionaryEntry(unwrappedRight, out entry))
        {
            (entrySide, valueSide, mirrored) = (right, unwrappedLeft, true);
        }
        else
        {
            return false;
        }

        if (HasNonIdentityConvert(entrySide, entry.Type) || !IsSimpleValue(valueSide))
            return true;

        var op = MapComparisonOperator(mirrored ? Mirror(nodeType) : nodeType);
        if (op is null || (IsRelationalComparison(nodeType) && !IsOrderPreservingEntryType(entry.Type)))
            return true;

        var value = TranslateDictionaryValue(valueSide, entry);
        if (value is not null)
            result = new MongoBinaryExpression(op.Value, entry, value);

        return true;
    }
}
