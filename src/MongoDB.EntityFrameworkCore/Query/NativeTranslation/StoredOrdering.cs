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
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.Query.Expressions;
using MongoDB.EntityFrameworkCore.Serializers;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>
/// Whether the server orders (and reduces) a property's stored BSON the way C# orders its CLR value (EF-337).
/// </summary>
/// <remarks>
/// Relational comparisons, sort keys and <c>Sum</c>/<c>Min</c>/<c>Max</c>/<c>Average</c> run on the stored value, which for a value
/// converter or non-default <c>BsonRepresentation</c> can silently differ from the CLR answer (an <c>int</c> stored as a string sorts
/// "10" &lt; "100" &lt; "9"; <c>$sum</c> ignores it). Equality is never gated: the constant is serialized through the property's
/// serializer, which is injective.
/// <para>
/// <see cref="PreservesClrOrdering"/> is shared by the native translator's declines and the driver-LINQ bridge's refusal
/// (<c>MongoEFToLinqTranslatingExpressionVisitor.ThrowIfStoredOrderingDiffers</c>); declining alone would only move the query to the
/// driver, which is wrong the same way. Native aggregates stay stricter (<see cref="StoredSerialization.HasDefaultKeySerialization"/>):
/// their result is read back through a generic serializer, not the property's.
/// </para>
/// <para>
/// A custom value converter is opaque (even <c>v =&gt; v * 2</c> can't be proven order-preserving) and refused. Admitted: a
/// <c>BsonRepresentation</c> (<see cref="IsExactlyOrderedRepresentation"/>, or equal to the type's default BSON type) or EF's built-in
/// numeric casting / enum-to-number converter (<see cref="ClassifyNumericConversion"/>).
/// </para>
/// <para>
/// Two strengths: <see cref="PreservesClrOrdering"/> (comparison against a value) also needs the constant to survive serialization, so
/// a narrowing converter (<c>long</c> stored as <c>int</c>) is excluded (<c>Big &gt; 3_000_000_000L</c> would wrap the constant).
/// <see cref="PreservesClrOrderingForAggregateAndSort"/> admits integral narrowing too: sorting and reducing only read stored values,
/// which EF wrote from in-range model values.
/// </para>
/// </remarks>
internal static class StoredOrdering
{
    /// <summary>Whether a relational comparison of <paramref name="property"/> against a (serialized) value gives the CLR answer.</summary>
    internal static bool PreservesClrOrdering(IProperty property)
        => StoredSerialization.HasDefaultKeySerialization(property)
           || IsIdentityRepresentation(property)
           || IsExactlyOrderedRepresentation(property)
           || ClassifyNumericConversion(property) == NumericConversion.Widening;

    /// <summary>Whether sorting or aggregating gives the CLR answer: <see cref="PreservesClrOrdering"/> plus integral narrowing.</summary>
    internal static bool PreservesClrOrderingForAggregateAndSort(IProperty property)
        => PreservesClrOrdering(property) || ClassifyNumericConversion(property) == NumericConversion.IntegralNarrowing;

    /// <summary>
    /// Native query-dialect comparison: <see cref="PreservesClrOrdering"/> plus integral narrowing, provided the constant is serialized by
    /// its CLR type (<see cref="ConstantsSerializeByClrType"/>) so it cannot wrap. The driver-LINQ bridge keeps <see cref="PreservesClrOrdering"/>
    /// since it serializes the constant through the converter.
    /// </summary>
    internal static bool PreservesClrOrderingForNativeComparison(IProperty property)
        => PreservesClrOrdering(property) || ClassifyNumericConversion(property) == NumericConversion.IntegralNarrowing;

    /// <summary>
    /// Whether a comparison constant must be serialized by its CLR type, not the converter: an unchecked narrowing converter wraps an
    /// out-of-range constant (<c>Big == 3_000_000_000L</c> would match a stored <c>-1294967296</c>), while the server compares an
    /// Int64 constant with a stored Int32 numerically.
    /// </summary>
    internal static bool ConstantsSerializeByClrType(IProperty property)
        => ClassifyNumericConversion(property) == NumericConversion.IntegralNarrowing;

    /// <summary>
    /// Whether a server-side function of the property (a string method or <c>Length</c>, a date part, arithmetic, <c>Math</c>) computed
    /// from its stored value gives the CLR answer (EF-465): stored as itself (a <c>BsonRepresentation</c> equal to the type's default
    /// included), or a number the server holds exactly (EF's numeric casting / enum-to-number converter, a numeric
    /// <c>BsonRepresentation</c> that represents every value). A custom converter, a string stored as an <c>ObjectId</c> or any
    /// non-numeric representation fails: the function would run over the stored text, not the .NET value.
    /// </summary>
    internal static bool PreservesClrValueForComputation(IProperty property)
    {
        if (StoredSerialization.HasDefaultKeySerialization(property) || IsIdentityRepresentation(property))
        {
            return true;
        }

        var clrType = property.ClrType.UnwrapNullableType();
        if (clrType.IsEnum)
        {
            clrType = Enum.GetUnderlyingType(clrType);
        }

        return (IsIntegral(clrType) || clrType == typeof(float) || clrType == typeof(double) || clrType == typeof(decimal))
               && (IsExactlyOrderedRepresentation(property) || ClassifyNumericConversion(property) != NumericConversion.None);
    }

    /// <summary>
    /// Whether the stored value is exactly what <c>ToString()</c> of the CLR value produces: a non-nullable property with EF's built-in
    /// Guid/Number/EnumToStringConverter and no <c>BsonRepresentation</c>. So <c>x.P.ToString()</c> is the stored field itself. (A floating-point
    /// number is formatted by the converter with the invariant culture where <c>ToString()</c> uses the current one; the stored form is what the
    /// driver compared too.) Shared by the native stored-<c>ToString</c> comparison and the driver-LINQ bridge's computation refusal (EF-465).
    /// </summary>
    internal static bool IsStoredAsToString(IProperty property)
    {
        if (property.ClrType.IsNullableValueType() || property.GetBsonRepresentation() != null)
        {
            return false;
        }

        var converter = property.GetValueConverter() ?? property.GetTypeMapping().Converter;
        if (converter is null || converter.ProviderClrType != typeof(string))
        {
            return false;
        }

        var converterType = converter.GetType();
        return converterType == typeof(GuidToStringConverter)
               || (converterType.IsGenericType
                   && converterType.GetGenericTypeDefinition() is var definition
                   && (definition == typeof(NumberToStringConverter<>) || definition == typeof(EnumToStringConverter<>)));
    }

    /// <summary>
    /// Whether <c>$sum</c>/<c>$avg</c> over the stored value gives the CLR answer: stored as itself, or via EF's numeric casting converter
    /// (including integral narrowing). A <c>BsonRepresentation</c> is excluded: the server-side sum comes back as the represented BSON
    /// type (<c>Decimal128</c>, string, ...) and is not read back as the CLR type.
    /// </summary>
    internal static bool PreservesClrValueForNumericReduction(IProperty property)
        => StoredSerialization.HasDefaultKeySerialization(property)
           || ClassifyNumericConversion(property) != NumericConversion.None;

    /// <summary>
    /// Whether every property a sort key reads preserves CLR ordering: a bare field via <see cref="PreservesClrOrderingForAggregateAndSort"/>;
    /// a computed key must be default-serialized throughout (<see cref="MongoExpressionTranslator.AllFieldsDefaultSerialized"/>).
    /// </summary>
    internal static bool SortKeyPreservesClrOrdering(MongoExpression key)
        => key switch
        {
            MongoFieldExpression field => PreservesClrOrderingForAggregateAndSort(field.Property),
            MongoOuterFieldExpression outerField => PreservesClrOrderingForAggregateAndSort(outerField.Property),
            _ => MongoExpressionTranslator.AllFieldsDefaultSerialized(key)
        };

    /// <summary>
    /// A <c>BsonRepresentation</c> (no value converter) whose stored value is exactly the CLR value in a BSON type ordered the same way:
    /// a numeric CLR type (or enum, by underlying value) stored as a numeric BSON type representing every value exactly; or a
    /// <c>string</c> ↔ <c>ObjectId</c> hex representation (fixed-width lowercase hex sorts as the 12 bytes do).
    /// </summary>
    /// <remarks>
    /// Deliberately a closed list, each entry covered by <c>RepresentedPropertyPushdownTests</c>. Lossy pairs (<c>long</c> as <c>Double</c>,
    /// <c>double</c> as <c>Decimal128</c>) and overflow-permitting narrowing pairs are excluded: they can tie or reorder distinct values.
    /// </remarks>
    private static bool IsExactlyOrderedRepresentation(IProperty property)
    {
        if (property.GetValueConverter() != null
            || property.GetTypeMapping().Converter != null
            || property.GetBsonRepresentation() is not { } representation)
        {
            return false;
        }

        var clrType = property.ClrType.UnwrapNullableType();
        if (clrType.IsEnum)
        {
            clrType = Enum.GetUnderlyingType(clrType);
        }

        return representation.BsonType switch
        {
            BsonType.Int32 => FitsInt32(clrType),
            BsonType.Int64 => FitsInt64(clrType),
            BsonType.Double => FitsInt32(clrType) || clrType == typeof(uint) || clrType == typeof(float)
                               || clrType == typeof(double),
            BsonType.Decimal128 => FitsInt64(clrType) || clrType == typeof(ulong) || clrType == typeof(decimal),
            BsonType.ObjectId => clrType == typeof(string),
            BsonType.String => clrType == typeof(ObjectId),
            _ => false
        };
    }

    /// <summary>
    /// EF's built-in <c>CastingConverter</c> (<c>HasConversion&lt;int&gt;()</c> on a <c>long</c>) or <c>EnumToNumberConverter</c> between
    /// numeric types where one represents every value of the other exactly, so the stored number is the CLR number. Signed/unsigned pairs
    /// of the same width and lossy pairs (<c>long</c> ↔ <c>double</c>) are excluded.
    /// </summary>
    /// <remarks>
    /// <see cref="NumericConversion.Widening"/>: values and constants both convert exactly. <see cref="NumericConversion.IntegralNarrowing"/>:
    /// both integral, the model type holds every provider value; stored values read back exactly but a constant outside the provider range
    /// wraps, so only sort/aggregate admit it. A fractional model stored as an integral type truncates and is never admitted.
    /// </remarks>
    private static NumericConversion ClassifyNumericConversion(IProperty property)
    {
        if (property.GetBsonRepresentation() != null)
        {
            return NumericConversion.None;
        }

        var configured = property.GetValueConverter();
        var mapped = property.GetTypeMapping().Converter;
        var converter = configured ?? mapped;
        if (converter is null
            || (configured != null && mapped != null && !ReferenceEquals(configured, mapped))
            || !converter.GetType().IsGenericType)
        {
            return NumericConversion.None;
        }

        var definition = converter.GetType().GetGenericTypeDefinition();
        if (definition != typeof(CastingConverter<,>) && definition != typeof(EnumToNumberConverter<,>))
        {
            return NumericConversion.None;
        }

        var model = converter.ModelClrType.UnwrapNullableType();
        if (model.IsEnum)
        {
            model = Enum.GetUnderlyingType(model);
        }

        var provider = converter.ProviderClrType.UnwrapNullableType();
        if (ExactlyContains(provider, model))
        {
            return NumericConversion.Widening;
        }

        return IsIntegral(model) && IsIntegral(provider) && ExactlyContains(model, provider)
            ? NumericConversion.IntegralNarrowing
            : NumericConversion.None;
    }

    private enum NumericConversion
    {
        None,
        Widening,
        IntegralNarrowing
    }

    private static bool IsIntegral(Type t)
        => FitsInt64(t) || t == typeof(ulong);

    /// <summary>A <c>BsonRepresentation</c> equal to the type's default BSON type (<c>[BsonRepresentation(String)]</c> on a <c>string</c>): the stored form is the default one.</summary>
    private static bool IsIdentityRepresentation(IProperty property)
        => property.GetValueConverter() == null
           && property.GetTypeMapping().Converter == null
           && property.GetBsonRepresentation() is { } representation
           && representation.BsonType == BsonSerializerFactory.GetBsonType(property.ClrType.UnwrapNullableType());

    // wide -> the numeric types whose every value it represents exactly (identity included).
    private static readonly Dictionary<Type, Type[]> ExactNumericSubsets = new()
    {
        [typeof(sbyte)] = [typeof(sbyte)],
        [typeof(byte)] = [typeof(byte)],
        [typeof(short)] = [typeof(short), typeof(sbyte), typeof(byte)],
        [typeof(ushort)] = [typeof(ushort), typeof(byte)],
        [typeof(int)] = [typeof(int), typeof(short), typeof(ushort), typeof(sbyte), typeof(byte)],
        [typeof(uint)] = [typeof(uint), typeof(ushort), typeof(byte)],
        [typeof(long)] = [typeof(long), typeof(int), typeof(uint), typeof(short), typeof(ushort), typeof(sbyte), typeof(byte)],
        [typeof(ulong)] = [typeof(ulong), typeof(uint), typeof(ushort), typeof(byte)],
        [typeof(float)] = [typeof(float), typeof(short), typeof(ushort), typeof(sbyte), typeof(byte)],
        [typeof(double)] = [typeof(double), typeof(float), typeof(int), typeof(uint), typeof(short), typeof(ushort), typeof(sbyte), typeof(byte)],
        [typeof(decimal)] =
        [
            typeof(decimal), typeof(long), typeof(ulong), typeof(int), typeof(uint), typeof(short), typeof(ushort),
            typeof(sbyte), typeof(byte)
        ]
    };

    private static bool ExactlyContains(Type wide, Type narrow)
        => ExactNumericSubsets.TryGetValue(wide, out var subset) && Array.IndexOf(subset, narrow) >= 0;

    private static bool FitsInt32(Type t)
        => t == typeof(int) || t == typeof(short) || t == typeof(ushort) || t == typeof(byte) || t == typeof(sbyte);

    private static bool FitsInt64(Type t)
        => FitsInt32(t) || t == typeof(uint) || t == typeof(long);
}
