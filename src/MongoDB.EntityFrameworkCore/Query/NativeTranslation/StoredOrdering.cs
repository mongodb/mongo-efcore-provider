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
/// <para>
/// A relational comparison (<c>&lt;</c>, <c>&gt;</c>, <c>&lt;=</c>, <c>&gt;=</c>), a sort key and a
/// <c>Sum</c>/<c>Min</c>/<c>Max</c>/<c>Average</c> all run on the stored value. For a value converter or a
/// non-default <c>BsonRepresentation</c> that can silently differ from the CLR answer: an <c>int</c> stored as a
/// string sorts "10" &lt; "100" &lt; "9" and <c>$sum</c> ignores it. Equality is unaffected (the constant is
/// serialized through the property's serializer, and serialization is injective), so it is never gated by this.
/// </para>
/// <para>
/// <see cref="PreservesClrOrdering"/> is the single predicate shared by the native translator's declines (relational
/// comparison, sort key) and the driver-LINQ bridge's refusal
/// (<c>MongoEFToLinqTranslatingExpressionVisitor.ThrowIfStoredOrderingDiffers</c>); declining alone would only move
/// the query to the driver, which is wrong the same way. Native aggregates stay stricter
/// (<see cref="NativeGroupByBinder.HasDefaultKeySerialization"/>): their result is read back through a generic
/// serializer, not the property's.
/// </para>
/// <para>
/// A custom value converter is opaque (even an order-preserving one such as <c>v =&gt; v * 2</c> can't be proven so,
/// and <c>$sum</c> over it is still wrong), so it is refused. The admitted non-default storage is where the stored
/// value is exactly the CLR value in an equally ordered BSON type: a <c>BsonRepresentation</c>
/// (<see cref="IsExactlyOrderedRepresentation"/>, or one equal to the type's default BSON type) or EF's built-in
/// numeric casting / enum-to-number converter (<see cref="ClassifyNumericConversion"/>).
/// </para>
/// <para>
/// Two strengths. <see cref="PreservesClrOrdering"/> (relational comparison against a value) also needs the
/// constant to survive serialization, so a narrowing converter (<c>long</c> stored as <c>int</c>) is excluded there:
/// <c>Big &gt; 3_000_000_000L</c> would wrap the constant. <see cref="PreservesClrOrderingForAggregateAndSort"/>
/// admits integral narrowing too, because sorting and reducing only read stored values, which EF wrote from
/// in-range model values.
/// </para>
/// </remarks>
internal static class StoredOrdering
{
    /// <summary>
    /// Whether a relational comparison of <paramref name="property"/>'s stored value against a (serialized) value gives
    /// the CLR answer.
    /// </summary>
    internal static bool PreservesClrOrdering(IProperty property)
        => NativeGroupByBinder.HasDefaultKeySerialization(property)
           || IsIdentityRepresentation(property)
           || IsExactlyOrderedRepresentation(property)
           || ClassifyNumericConversion(property) == NumericConversion.Widening;

    /// <summary>
    /// Whether sorting or aggregating <paramref name="property"/>'s stored values gives the CLR answer: everything
    /// <see cref="PreservesClrOrdering"/> admits, plus an integral narrowing converter (see the class remarks).
    /// </summary>
    internal static bool PreservesClrOrderingForAggregateAndSort(IProperty property)
        => PreservesClrOrdering(property) || ClassifyNumericConversion(property) == NumericConversion.IntegralNarrowing;

    /// <summary>
    /// Whether every property a sort key reads preserves CLR ordering. A bare field is tested with
    /// <see cref="PreservesClrOrderingForAggregateAndSort"/>; a computed key must be default-serialized throughout, as computed values
    /// already are (<see cref="MongoExpressionTranslator.AllFieldsDefaultSerialized"/>).
    /// </summary>
    internal static bool SortKeyPreservesClrOrdering(MongoExpression key)
        => key switch
        {
            MongoFieldExpression field => PreservesClrOrderingForAggregateAndSort(field.Property),
            MongoOuterFieldExpression outerField => PreservesClrOrderingForAggregateAndSort(outerField.Property),
            _ => MongoExpressionTranslator.AllFieldsDefaultSerialized(key)
        };

    /// <summary>
    /// A <c>BsonRepresentation</c> (with no value converter) whose stored value is exactly the CLR value in a BSON type
    /// the server orders the same way: a numeric CLR type (or an enum, by its underlying value, which is how C#
    /// compares enums) stored as a numeric BSON type that represents every value of that type exactly; or a
    /// <c>string</c> ↔ <c>ObjectId</c> hex representation, whose lowercase fixed-width hex string sorts exactly as
    /// the 12 ObjectId bytes do.
    /// </summary>
    /// <remarks>
    /// Deliberately a closed list, each entry covered by <c>RepresentedPropertyPushdownTests</c>. Lossy pairs
    /// (<c>long</c> as <c>Double</c>, <c>double</c> as <c>Decimal128</c>) and overflow-permitting narrowing pairs are
    /// excluded: they can tie or reorder distinct CLR values.
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
    /// EF's built-in <c>CastingConverter&lt;TModel, TProvider&gt;</c> (e.g. <c>HasConversion&lt;int&gt;()</c> on a
    /// <c>long</c>) or <c>EnumToNumberConverter&lt;TEnum, TNumber&gt;</c> between numeric types where one type represents
    /// every value of the other exactly: the stored number is the CLR number (for an enum, its underlying value), so
    /// the server compares, sorts and sums it the same way. Signed/unsigned pairs of the same width and lossy pairs
    /// (<c>long</c> ↔ <c>double</c>) are excluded.
    /// </summary>
    /// <remarks>
    /// <see cref="NumericConversion.Widening"/>: the provider type holds every model value, so values and constants
    /// both convert exactly. <see cref="NumericConversion.IntegralNarrowing"/>: both integral, the model type holds
    /// every provider value (<c>long</c> stored as <c>int</c>); stored values read back exactly, but a constant outside
    /// the provider range wraps, so only sort/aggregate admit it. A fractional model stored as an integral type
    /// (<c>decimal</c> as <c>int</c>) truncates constants and values and is never admitted.
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

    /// <summary>
    /// A <c>BsonRepresentation</c> equal to the type's default BSON type (e.g. <c>[BsonRepresentation(String)]</c> on a
    /// <c>string</c>): the stored form is the default one.
    /// </summary>
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
