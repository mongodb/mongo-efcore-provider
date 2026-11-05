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
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Options;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.Serializers;

namespace MongoDB.EntityFrameworkCore.Storage;

/// <summary>
/// Helpers used by the shapers to access contents of the <see cref="BsonDocument"/> results.
/// </summary>
internal static class BsonBinding
{
    /// <summary>
    /// Create the expression which will obtain the value or intermediate value required by the shaper.
    /// </summary>
    /// <param name="bsonDocExpression">The expression to obtain the current <see cref="BsonDocument"/>.</param>
    /// <param name="name">The name of the field in the document that contains the desired value.</param>
    /// <param name="required">
    /// <see langword="true"/> if the field is required to be present in the document,
    /// <see langword="false"/> if it is optional.
    /// </param>
    /// <param name="mappedType">What <see cref="Type"/> to the value is to be treated as.</param>
    /// <param name="declaredType">The <see cref="ITypeBase"/> the value will belong to in order to obtaining additional metadata.</param>
    /// <returns>A compilable expression the shaper can use to obtain this value from a <see cref="BsonDocument"/>.</returns>
    /// <exception cref="InvalidOperationException">If we can't find anything mapped to this name.</exception>
    public static Expression CreateGetValueExpression(
        Expression bsonDocExpression,
        string? name,
        bool required,
        Type mappedType,
        ITypeBase declaredType)
    {
        if (name is null)
        {
            return bsonDocExpression;
        }

        if (mappedType == typeof(BsonArray))
        {
            return CreateGetBsonArray(bsonDocExpression, name);
        }

        if (mappedType == typeof(BsonDocument))
        {
            return CreateGetBsonDocument(bsonDocExpression, name, required, declaredType);
        }

        var targetProperty = declaredType.FindProperty(name);
        if (targetProperty != null)
        {
            return CreateGetPropertyValue(bsonDocExpression, Expression.Constant(new PropertyReadInfo(targetProperty)),
                targetProperty.IsNullable ? mappedType.MakeNullable() : mappedType);
        }

        if (declaredType is IEntityType entityType)
        {
            var navigationProperty = entityType.FindNavigation(name);
            if (navigationProperty != null)
            {
                var fieldName = navigationProperty.TargetEntityType.GetContainingElementName()!;
                return CreateGetElementValue(bsonDocExpression, fieldName, mappedType);
            }
        }

        throw new InvalidOperationException(CoreStrings.PropertyNotFound(name, declaredType.DisplayName()));
    }

    /// <summary>
    /// Create the expression which will obtain a projected element using the serializer metadata
    /// from the source property rather than resolving metadata from the projected alias.
    /// </summary>
    /// <param name="bsonDocExpression">The expression to obtain the current <see cref="BsonDocument"/>.</param>
    /// <param name="name">The projected element name in the current document.</param>
    /// <param name="property">The source model property that defines serializer/nullability metadata.</param>
    /// <param name="mappedType">What <see cref="Type"/> the value is to be treated as.</param>
    /// <remarks>
    /// Callers must ensure <paramref name="mappedType"/> matches <paramref name="property"/>'s CLR
    /// type (modulo nullability). The generated call casts the deserialized value to
    /// <paramref name="mappedType"/>; if it differs from the property's CLR type the cast can
    /// throw because the property's serializer produces values of its own type.
    /// </remarks>
    /// <returns>A compilable expression the shaper can use to obtain this value.</returns>
    public static Expression CreateGetValueExpression(
        Expression bsonDocExpression,
        string? name,
        IProperty property,
        Type mappedType)
    {
        if (name is null)
        {
            return bsonDocExpression;
        }

        if (mappedType == typeof(BsonArray))
        {
            return CreateGetBsonArray(bsonDocExpression, name);
        }

        if (mappedType == typeof(BsonDocument))
        {
            return CreateGetBsonDocument(bsonDocExpression, name, !property.IsNullable, property.DeclaringType);
        }

        return CreateGetPropertyValueAtElement(
            bsonDocExpression,
            Expression.Constant(name),
            Expression.Constant(new PropertyReadInfo(property)),
            property.IsNullable ? mappedType.MakeNullable() : mappedType);
    }

    /// <summary>
    /// Create the expression which reads a bare scalar projection leaf like <see cref="CreateGetValueExpression(Expression, string?, IProperty, Type)"/>, but
    /// via <see cref="GetScalarProjectionValueAtElement{T}"/>: a missing element of a required property reads
    /// <see langword="default"/>.
    /// </summary>
    internal static Expression CreateGetScalarProjectionValueExpression(
        Expression bsonDocExpression,
        string name,
        IProperty property,
        Type mappedType)
        => Expression.Call(
            null,
            GetScalarProjectionValueAtElementMethodInfo.MakeGenericMethod(
                property.IsNullable ? mappedType.MakeNullable() : mappedType),
            bsonDocExpression,
            Expression.Constant(name),
            Expression.Constant(new PropertyReadInfo(property)));

    /// <summary>
    /// A read of a value the streaming projection reader has already produced for <paramref name="name"/> with <paramref name="serializer"/>
    /// (see <see cref="Query.NativeTranslation.MongoStreamedCollectionExpression"/>). It exists only for the planner to take over.
    /// </summary>
    internal static MethodCallExpression CreateGetStreamedValueExpression(
        Expression bsonDocExpression, string name, IBsonSerializer serializer, Type type, string? missingMessage)
        => Expression.Call(
            null,
            typeof(BsonBinding).GetMethod(nameof(GetStreamedValueAtElement), BindingFlags.Static | BindingFlags.NonPublic)!.MakeGenericMethod(type),
            bsonDocExpression,
            Expression.Constant(name),
            Expression.Constant(serializer, typeof(IBsonSerializer)),
            Expression.Constant(missingMessage, typeof(string)));

    internal static T GetStreamedValueAtElement<T>(
        BsonDocument document, string elementName, IBsonSerializer serializer, string? missingMessage)
        => throw new InvalidOperationException(
            $"Element '{elementName}' is read by the streaming projection reader and has no document form.");

    internal static MethodCallExpression CreateGetBsonArray(Expression bsonDocExpression, string name)
        => Expression.Call(null, GetBsonArrayMethodInfo, bsonDocExpression, Expression.Constant(name));

    private static readonly MethodInfo GetBsonArrayMethodInfo
        = typeof(BsonBinding).GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(mi => mi.Name == nameof(GetBsonArray));

    private static BsonArray? GetBsonArray(BsonDocument document, string name)
    {
        // A dotted name is normally a root-relative path (see TryGetValueAtPath), but before path walking it was read as
        // one literal key, so fall back to the literal key when the walk misses.
        if (!TryGetValueAtPath(document, name, out var bsonValue)
            && !document.TryGetValue(name, out bsonValue))
        {
            return null;
        }

        return bsonValue switch
        {
            {IsBsonArray: true} => bsonValue.AsBsonArray,
            {IsBsonNull: true} => null,
            _ => throw new InvalidOperationException(
                $"Document element '{name}' is {bsonValue?.BsonType} when {nameof(BsonArray)} is required.")
        };
    }

    /// <summary>
    /// Resolves <paramref name="name"/> against <paramref name="document"/>, walking a dotted name segment by
    /// segment rather than as one literal key.
    /// </summary>
    /// <remarks>
    /// A dotted name here is a root-relative document path, and MongoDB renders a dotted <c>$project</c> key as
    /// nested documents, so the two reads coincide. An absent segment, or a non-document intermediate, yields
    /// <see langword="false"/> rather than throwing.
    /// </remarks>
    private static bool TryGetValueAtPath(BsonDocument document, string name, out BsonValue? value)
    {
        if (!name.Contains('.'))
        {
            return document.TryGetValue(name, out value);
        }

        BsonValue current = document;
        foreach (var segment in name.Split('.'))
        {
            if (current is not BsonDocument segmentDocument || !segmentDocument.TryGetValue(segment, out current!))
            {
                value = null;
                return false;
            }
        }

        value = current;
        return true;
    }

    private static MethodCallExpression CreateGetBsonDocument(
        Expression bsonDocExpression, string name, bool required, ITypeBase declaredType)
        => Expression.Call(null, GetBsonDocumentMethodInfo, bsonDocExpression, Expression.Constant(name),
            Expression.Constant(required),
            Expression.Constant(declaredType));

    private static readonly MethodInfo GetBsonDocumentMethodInfo
        = typeof(BsonBinding).GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(mi => mi.Name == nameof(GetBsonDocument));

    private static BsonDocument? GetBsonDocument(BsonDocument parent, string name, bool required, ITypeBase declaredType)
    {
        var value = parent.GetValue(name, BsonNull.Value);
        if (value == BsonNull.Value && required)
        {
            throw new InvalidOperationException($"Field '{name}' required but not present in BsonDocument for a '{
                declaredType.DisplayName()}'.");
        }

        return value == BsonNull.Value ? null : value.AsBsonDocument;
    }

    private static MethodCallExpression
        CreateGetPropertyValue(Expression bsonDocExpression, Expression propertyExpression, Type resultType) =>
        Expression.Call(null, GetPropertyValueMethodInfo.MakeGenericMethod(resultType), bsonDocExpression, propertyExpression);

    private static MethodCallExpression CreateGetPropertyValueAtElement(
        Expression bsonDocExpression,
        Expression elementNameExpression,
        Expression propertyExpression,
        Type resultType)
        => Expression.Call(
            null,
            GetPropertyValueAtElementMethodInfo.MakeGenericMethod(resultType),
            bsonDocExpression,
            elementNameExpression,
            propertyExpression);

    internal static MethodCallExpression CreateGetElementValue(Expression bsonDocExpression, string name, Type type) =>
        Expression.Call(null, GetElementValueMethodInfo.MakeGenericMethod(type), bsonDocExpression, Expression.Constant(name));

    /// <summary>
    /// A <c>float</c> (or <c>float?</c>) serializer that narrows a BSON double to a <c>float</c> with rounding instead
    /// of throwing <see cref="TruncationException"/>, but still throws on overflow.
    /// </summary>
    /// <remarks>
    /// For an aliased server-computed value (an <c>$avg</c> accumulator, arithmetic), which the server returns as a
    /// double that is generally not exactly representable as a <c>float</c>. The BCL narrows the same way: LINQ to
    /// objects accumulates a <c>float</c> <c>Average</c> in <c>double</c> and casts the result to <c>float</c>.
    /// </remarks>
    internal static IBsonSerializer CreateNarrowingFloatSerializer(Type type)
    {
        var single = new SingleSerializer(BsonType.Double, new RepresentationConverter(allowOverflow: false, allowTruncation: true));
        return type == typeof(float) ? single : new NullableSerializer<float>(single);
    }

    /// <summary>
    /// As <see cref="CreateGetElementValue(Expression, string, Type)"/>, but when <paramref name="dateTimeKindSource"/>
    /// is non-null a <see cref="DateTime"/> reads back with that property's configured <see cref="DateTimeKind"/>.
    /// </summary>
    /// <remarks>
    /// For an element holding the unchanged stored value of <paramref name="dateTimeKindSource"/> whose alias has no
    /// backing property of its own (a <c>$group</c> key or <c>$min</c>/<c>$max</c> output, a cast leaf). Only the
    /// kind is taken from the property; the serializer is still the one for <paramref name="type"/>, so nullability
    /// follows <paramref name="type"/> rather than the property. The serializer is built once here, at shaper
    /// compile time, not per row.
    /// </remarks>
    internal static MethodCallExpression CreateGetElementValue(
        Expression bsonDocExpression, string name, Type type, IReadOnlyProperty? dateTimeKindSource) =>
        dateTimeKindSource == null
            ? CreateGetElementValue(bsonDocExpression, name, type)
            : CreateGetElementValue(
                bsonDocExpression, name, type, BsonSerializerFactory.CreateTypeSerializer(type, dateTimeKindSource));

    /// <summary>
    /// As <see cref="CreateGetElementValue(Expression, string, Type)"/>, reading the element through
    /// <paramref name="serializer"/> (built once, at shaper compile time) instead of the generic serializer for
    /// <paramref name="type"/>.
    /// </summary>
    internal static MethodCallExpression CreateGetElementValue(
        Expression bsonDocExpression, string name, Type type, IBsonSerializer serializer)
        => Expression.Call(null, GetKindAwareElementValueMethodInfo.MakeGenericMethod(type), bsonDocExpression,
            Expression.Constant(name), Expression.Constant(serializer, typeof(IBsonSerializer)));

    /// <summary>
    /// Create the expression which reads an element nested under one or more parent documents, walking
    /// <paramref name="path"/> segment by segment.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="CreateGetElementValue(Expression, string, Type)"/> because <see cref="GetElementValue{T}"/> treats its name
    /// as a literal key, and existing callers may pass aliases containing dots.
    /// </remarks>
    internal static MethodCallExpression CreateGetElementValueAtPath(Expression bsonDocExpression, string[] path, Type type) =>
        Expression.Call(null, GetElementValueAtPathMethodInfo.MakeGenericMethod(type), bsonDocExpression,
            Expression.Constant(path));

    /// <summary>
    /// As <see cref="CreateGetElementValueAtPath(Expression, string[], Type)"/>, honouring
    /// <paramref name="dateTimeKindSource"/>'s configured <see cref="DateTimeKind"/> as
    /// <see cref="CreateGetElementValue(Expression, string, Type, IReadOnlyProperty?)"/> does.
    /// </summary>
    internal static MethodCallExpression CreateGetElementValueAtPath(
        Expression bsonDocExpression, string[] path, Type type, IReadOnlyProperty? dateTimeKindSource) =>
        dateTimeKindSource == null
            ? CreateGetElementValueAtPath(bsonDocExpression, path, type)
            : Expression.Call(null, GetKindAwareElementValueAtPathMethodInfo.MakeGenericMethod(type), bsonDocExpression,
                Expression.Constant(path),
                Expression.Constant(BsonSerializerFactory.CreateTypeSerializer(type, dateTimeKindSource), typeof(IBsonSerializer)));

    /// <summary>
    /// Create the expression which reads a value nested under one or more parent documents, walking
    /// <paramref name="path"/> and reading the last segment through <paramref name="property"/>'s serializer and
    /// nullability.
    /// </summary>
    /// <remarks>
    /// Property-aware sibling of <see cref="CreateGetElementValueAtPath(Expression, string[], Type)"/> (which can't honor value converters or
    /// non-default representations). Used when a leaf's alias differs from its document path and the shaper
    /// reads whole, un-projected documents.
    /// </remarks>
    internal static MethodCallExpression CreateGetPropertyValueAtPath(
        Expression bsonDocExpression, string[] path, IProperty property, Type mappedType)
        => Expression.Call(
            null,
            GetPropertyValueAtPathMethodInfo.MakeGenericMethod(
                property.IsNullable ? mappedType.MakeNullable() : mappedType),
            bsonDocExpression,
            Expression.Constant(path),
            Expression.Constant(new PropertyReadInfo(property)));

    private static readonly MethodInfo GetPropertyValueAtPathMethodInfo
        = typeof(BsonBinding).GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(mi => mi.Name == nameof(GetPropertyValueAtPath));

    internal static T? GetPropertyValueAtPath<T>(BsonDocument document, string[] path, PropertyReadInfo property)
    {
        var current = document;
        for (var i = 0; i < path.Length - 1; i++)
        {
            if (!current.TryGetValue(path[i], out var segmentValue) || segmentValue is not BsonDocument segmentDocument)
            {
                // Absent intermediate segment = unmatched left-outer join row. Dispatch on whether T can hold absence
                // (as GetElementValue{T} does), not property.IsNullable, or the DriverLinq leg throws where Native yields null.
                if (typeof(T).IsNullableType())
                {
                    return default;
                }

                throw new InvalidOperationException(
                    $"Document element '{string.Join(".", path)}' is missing for required non-nullable property '{
                        property.Property.Name}'.");
            }

            current = segmentDocument;
        }

        return GetPropertyValueAtElement<T>(current, path[^1], property);
    }

    private static readonly MethodInfo GetPropertyValueMethodInfo
        = typeof(BsonBinding).GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(mi => mi.Name == nameof(GetPropertyValue));

    private static readonly MethodInfo GetPropertyValueAtElementMethodInfo
        = typeof(BsonBinding).GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(mi => mi.Name == nameof(GetPropertyValueAtElement));

    private static readonly MethodInfo GetScalarProjectionValueAtElementMethodInfo
        = typeof(BsonBinding).GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(mi => mi.Name == nameof(GetScalarProjectionValueAtElement));

    private static readonly MethodInfo GetElementValueMethodInfo
        = typeof(BsonBinding).GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(mi => mi.Name == nameof(GetElementValue));

    private static readonly MethodInfo GetElementValueAtPathMethodInfo
        = typeof(BsonBinding).GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(mi => mi.Name == nameof(GetElementValueAtPath));

    private static readonly MethodInfo GetKindAwareElementValueMethodInfo
        = typeof(BsonBinding).GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(mi => mi.Name == nameof(GetKindAwareElementValue));

    private static readonly MethodInfo GetKindAwareElementValueAtPathMethodInfo
        = typeof(BsonBinding).GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(mi => mi.Name == nameof(GetKindAwareElementValueAtPath));

    internal static T? GetPropertyValue<T>(BsonDocument? document, PropertyReadInfo property)
    {
        // A null parent document means the owning entity is absent (e.g. a nested optional reference whose $lookup
        // matched nothing). Treat every property as absent so the materializer's null-key check yields a null entity.
        if (document == null)
        {
            return default;
        }

        if (TryReadElementValue(document, property.ElementName, property.ElementPath, property.Serializer, property.NominalType, out T? value))
        {
            ThrowIfNullForRequired(value, property.Property);
            return value;
        }

        if (property.Property.IsNullable) return default;

        throw new InvalidOperationException(RequiredPropertyMissingMessage(property.Property));
    }

    /// <summary>
    /// The message thrown when a required non-nullable property's element is missing. Shared with the streaming
    /// materializer (<c>MongoStreamingEntityMaterializerRewriter</c>), which mirrors <see cref="GetPropertyValue{T}"/>.
    /// </summary>
    internal static string RequiredPropertyMissingMessage(IReadOnlyProperty property)
        => $"Document element is missing for required non-nullable property '{property.Name}'.";

    /// <summary>
    /// The message thrown when a required non-nullable property's element is BSON null. Shared with the streaming
    /// materializer (<c>MongoStreamingEntityMaterializerRewriter</c>), which mirrors <see cref="GetPropertyValue{T}"/>.
    /// </summary>
    internal static string RequiredPropertyNullMessage(IReadOnlyProperty property)
        => $"Document element is null for required non-nullable property '{property.Name}'.";

    // A null read of a required non-nullable property. Never fires for a non-nullable value-typed T.
    private static void ThrowIfNullForRequired<T>(T? value, IReadOnlyProperty property)
    {
        if (value == null && !property.IsNullable)
        {
            throw new InvalidOperationException(RequiredPropertyNullMessage(property));
        }
    }

    /// <summary>
    /// Create the expression which reads <paramref name="property"/> like <see cref="GetPropertyValue{T}"/>, but yields
    /// <paramref name="placeholder"/> instead of throwing when the element is absent from the document.
    /// </summary>
    /// <remarks>
    /// Only for an owner key the shaped document legitimately lacks, where the value is unobservable (see
    /// <c>MongoProjectionBindingRemovingExpressionVisitor.OwnerKeyMayBeAbsent</c>). The placeholder must be non-null
    /// so the materializer's null-key check doesn't turn the owned entity into <see langword="null"/>.
    /// </remarks>
    internal static MethodCallExpression CreateGetPropertyValueOrPlaceholder(
        Expression bsonDocExpression, IReadOnlyProperty property, Type resultType, object placeholder)
        => Expression.Call(
            null,
            GetPropertyValueOrPlaceholderMethodInfo.MakeGenericMethod(resultType),
            bsonDocExpression,
            Expression.Constant(new PropertyReadInfo(property)),
            Expression.Constant(placeholder, resultType));

    private static readonly MethodInfo GetPropertyValueOrPlaceholderMethodInfo
        = typeof(BsonBinding).GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(mi => mi.Name == nameof(GetPropertyValueOrPlaceholder));

    internal static T? GetPropertyValueOrPlaceholder<T>(BsonDocument? document, PropertyReadInfo property, T placeholder)
    {
        if (document == null)
        {
            return default;
        }

        if (!TryReadElementValue(document, property.ElementName, property.ElementPath, property.Serializer, property.NominalType, out T? value))
        {
            return placeholder;
        }

        ThrowIfNullForRequired(value, property.Property);
        return value;
    }

    /// <summary>
    /// Create the expression which reads a looked-up reference's document as <see langword="null"/> when it holds no key, which is what
    /// <c>$lookup</c> leaves for a parent that matched nothing but holds a lookup of its own (see <see cref="ReferenceDocumentOrNull"/>).
    /// </summary>
    internal static MethodCallExpression CreateReferenceDocumentOrNull(Expression bsonDocExpression, IReadOnlyProperty keyProperty)
    {
        var key = new PropertyReadInfo(keyProperty);
        return Expression.Call(null, ReferenceDocumentOrNullMethodInfo, bsonDocExpression, Expression.Constant(key.ElementPath?[0] ?? key.ElementName));
    }

    private static readonly MethodInfo ReferenceDocumentOrNullMethodInfo
        = typeof(BsonBinding).GetMethod(nameof(ReferenceDocumentOrNull), BindingFlags.Static | BindingFlags.NonPublic)!;

    /// <summary>
    /// A nested <c>$lookup</c> (a collection ThenInclude) under an optional reference that matched nothing still creates the reference's
    /// document to hold its array: a placeholder with no key. No document stored in a collection lacks its key, so it is no match.
    /// </summary>
    internal static BsonDocument? ReferenceDocumentOrNull(BsonDocument? document, string keyElementName)
        => document != null && document.Contains(keyElementName) ? document : null;

    internal static T? GetPropertyValueAtElement<T>(BsonDocument document, string elementName, PropertyReadInfo property)
    {
        // Intentionally drop any ElementPath from the source serialization info: in a projection the
        // value lives at the flat alias name in the projected document, not at the property's original
        // (possibly nested, e.g. ["_id", name] for a composite key) path. Re-introducing the path here
        // would break aliased projections of composite-key parts.
        if (TryReadElementValue(document, elementName, null, property.Serializer, property.NominalType, out T? value))
        {
            ThrowIfNullForRequired(value, property.Property);
            return value;
        }

        if (property.Property.IsNullable) return default;

        throw new InvalidOperationException($"Document element '{elementName}' is missing for required non-nullable property '{property.Property.Name}'.");
    }

    /// <summary>
    /// Reads a bare scalar projection leaf from the flat alias in the projected document. Identical to
    /// <see cref="GetPropertyValueAtElement{T}"/> except that a MISSING element of a required property reads
    /// <c>default(T)</c> instead of throwing, as driver-LINQ's <c>$project</c> push-down did. Only the Projection
    /// route uses this; whole-entity materialization stays strict. An explicit BSON null still throws. A reference-typed
    /// required leaf (e.g. <c>string</c>) reads <see langword="null"/> for a missing element, deliberately mirroring
    /// the driver.
    /// </summary>
    internal static T? GetScalarProjectionValueAtElement<T>(BsonDocument document, string elementName, PropertyReadInfo property)
    {
        // As GetPropertyValueAtElement: the value lives at the flat alias, not the property's original element path.
        //
        // Check the RAW element: for a value-typed T the deserialized value is never null, so a post-read null check
        // can't see an explicit BSON null (driver-LINQ throws FormatException for it). Except through the driver's
        // BooleanSerializer, which reads null as false, as driver-LINQ did (this mirrors the driver; a value-converted
        // bool has a ValueConverterSerializer, not a BooleanSerializer, so it takes the throwing branch; see
        // MongoAggregationExpressionRenderer.DriverReadsNullAsDefault).
        if (!property.Property.IsNullable && document.TryGetValue(elementName, out var raw) && raw.IsBsonNull
            && property.Serializer is not BooleanSerializer)
        {
            throw new InvalidOperationException(
                $"Document element '{elementName}' is null for required non-nullable property '{property.Property.Name}'.");
        }

        return TryReadElementValue(document, elementName, null, property.Serializer, property.NominalType, out T? value) ? value : default;
    }

    internal static T? GetElementValueAtPath<T>(BsonDocument document, string[] path)
        => ReadElementValueAtPath<T>(document, path, TypeSerializer<T>.Instance);

    // `serializer` is BsonSerializerFactory.CreateTypeSerializer(typeof(T), dateTimeKindSource), built at compile time.
    internal static T? GetKindAwareElementValueAtPath<T>(BsonDocument document, string[] path, IBsonSerializer serializer)
        => ReadElementValueAtPath<T>(document, path, serializer);

    private static T? ReadElementValueAtPath<T>(BsonDocument document, string[] path, IBsonSerializer serializer)
    {
        var type = typeof(T);
        if (TryReadElementValue(document, null, path, serializer, type, out T? value) || type.IsNullableType())
        {
            return value;
        }

        throw new InvalidOperationException($"Document element '{string.Join(".", path)}' is missing but required.");
    }

    internal static T? GetElementValue<T>(BsonDocument document, string elementName)
        => ReadElementValue<T>(document, elementName, TypeSerializer<T>.Instance);

    // `serializer` is built at compile time: BsonSerializerFactory.CreateTypeSerializer(typeof(T), dateTimeKindSource),
    // or BsonSerializerFactory.CreateTimeOfDaySerializer for a native TimeOfDay leaf.
    internal static T? GetKindAwareElementValue<T>(BsonDocument document, string elementName, IBsonSerializer serializer)
        => ReadElementValue<T>(document, elementName, serializer);

    private static T? ReadElementValue<T>(BsonDocument document, string elementName, IBsonSerializer serializer)
    {
        var type = typeof(T);
        if (TryReadElementValue(document, elementName, null, serializer, type, out T? value) || type.IsNullableType())
        {
            return value;
        }

        throw new InvalidOperationException($"Document element '{elementName}' is missing but required.");
    }

    private static bool TryReadElementValue<T>(
        BsonDocument document,
        string? elementName,
        string[]? elementPath,
        IBsonSerializer serializer,
        Type nominalType,
        out T? value)
    {
        BsonValue? rawValue;
        if (elementPath == null)
        {
            document.TryGetValue(elementName!, out rawValue);
        }
        else
        {
            rawValue = document;
            foreach (var node in elementPath)
            {
                var doc = (BsonDocument)rawValue;
                if (!doc.TryGetValue(node, out rawValue))
                {
                    rawValue = null;
                    break;
                }
            }
        }

        if (rawValue == BsonNull.Value)
        {
            value = default;
            return true;
        }

        if (rawValue != null)
        {
            value = TryReadDirect(serializer, rawValue, out T? direct)
                ? direct
                : (T)new BsonSerializationInfo(elementName ?? "value", serializer, nominalType).DeserializeValue(rawValue);
            return true;
        }

        value = default;
        return false;
    }

    // The serializer for a bare CLR type, resolved once per T instead of per read.
    private static class TypeSerializer<T>
    {
        public static readonly IBsonSerializer Instance = BsonSerializerFactory.CreateTypeSerializer(typeof(T));
    }

    /// <summary>
    /// Reads <paramref name="raw"/> without the driver's per-value round trip through a temporary document and reader
    /// (<see cref="BsonSerializationInfo.DeserializeValue"/>, ~500 B and ~300 ns), when the value is stored exactly as
    /// <paramref name="serializer"/> writes it by default: a BSON int32 through a default <see cref="Int32Serializer"/>, and so on.
    /// </summary>
    /// <remarks>
    /// Only the exact default combinations qualify; anything else (another BSON type, a non-default representation, a value
    /// converter, <see cref="DateTime"/>, <see cref="Guid"/>, enums, decimals) returns <see langword="false"/> and takes the
    /// serializer, so the converting and strict behavior of the driver is unchanged. A BSON null never reaches here.
    /// </remarks>
    private static bool TryReadDirect<T>(IBsonSerializer serializer, BsonValue raw, out T? value)
    {
        if (typeof(T) == typeof(int))
        {
            if (raw is BsonInt32 bsonInt && serializer is Int32Serializer { Representation: BsonType.Int32 })
            {
                var v = bsonInt.Value;
                value = Unsafe.As<int, T>(ref v);
                return true;
            }
        }
        else if (typeof(T) == typeof(int?))
        {
            if (raw is BsonInt32 bsonInt
                && serializer is NullableSerializer<int> { ValueSerializer: Int32Serializer { Representation: BsonType.Int32 } })
            {
                int? v = bsonInt.Value;
                value = Unsafe.As<int?, T>(ref v);
                return true;
            }
        }
        else if (typeof(T) == typeof(long))
        {
            if (raw is BsonInt64 bsonLong && serializer is Int64Serializer { Representation: BsonType.Int64 })
            {
                var v = bsonLong.Value;
                value = Unsafe.As<long, T>(ref v);
                return true;
            }
        }
        else if (typeof(T) == typeof(long?))
        {
            if (raw is BsonInt64 bsonLong
                && serializer is NullableSerializer<long> { ValueSerializer: Int64Serializer { Representation: BsonType.Int64 } })
            {
                long? v = bsonLong.Value;
                value = Unsafe.As<long?, T>(ref v);
                return true;
            }
        }
        else if (typeof(T) == typeof(double))
        {
            if (raw is BsonDouble bsonDouble && serializer is DoubleSerializer { Representation: BsonType.Double })
            {
                var v = bsonDouble.Value;
                value = Unsafe.As<double, T>(ref v);
                return true;
            }
        }
        else if (typeof(T) == typeof(double?))
        {
            if (raw is BsonDouble bsonDouble
                && serializer is NullableSerializer<double> { ValueSerializer: DoubleSerializer { Representation: BsonType.Double } })
            {
                double? v = bsonDouble.Value;
                value = Unsafe.As<double?, T>(ref v);
                return true;
            }
        }
        else if (typeof(T) == typeof(bool))
        {
            if (raw is BsonBoolean bsonBool && serializer is BooleanSerializer)
            {
                var v = bsonBool.Value;
                value = Unsafe.As<bool, T>(ref v);
                return true;
            }
        }
        else if (typeof(T) == typeof(bool?))
        {
            if (raw is BsonBoolean bsonBool && serializer is NullableSerializer<bool> { ValueSerializer: BooleanSerializer })
            {
                bool? v = bsonBool.Value;
                value = Unsafe.As<bool?, T>(ref v);
                return true;
            }
        }
        else if (typeof(T) == typeof(string))
        {
            if (raw is BsonString bsonString && serializer is StringSerializer { Representation: BsonType.String })
            {
                value = (T)(object)bsonString.Value;
                return true;
            }
        }
        else if (typeof(T) == typeof(ObjectId))
        {
            if (raw is BsonObjectId bsonObjectId && serializer is ObjectIdSerializer { Representation: BsonType.ObjectId })
            {
                var v = bsonObjectId.Value;
                value = Unsafe.As<ObjectId, T>(ref v);
                return true;
            }
        }
        else if (typeof(T) == typeof(ObjectId?))
        {
            if (raw is BsonObjectId bsonObjectId
                && serializer is NullableSerializer<ObjectId> { ValueSerializer: ObjectIdSerializer { Representation: BsonType.ObjectId } })
            {
                ObjectId? v = bsonObjectId.Value;
                value = Unsafe.As<ObjectId?, T>(ref v);
                return true;
            }
        }

        value = default;
        return false;
    }
}

/// <summary>
/// What a shaper needs to read a mapped property from a document, resolved once when the shaper is compiled and carried as a
/// constant: the property (for nullability and messages) and its serialization info (element name or path, serializer, nominal type).
/// </summary>
internal sealed class PropertyReadInfo
{
    public PropertyReadInfo(IReadOnlyProperty property)
    {
        Property = property;
        var info = BsonSerializerFactory.GetPropertySerializationInfo(property);
        // The driver throws from ElementName when the info has a path (a composite-key part), so read only the one that is set.
        ElementPath = info.ElementPath?.ToArray();
        ElementName = ElementPath == null ? info.ElementName : null;
        Serializer = info.Serializer;
        NominalType = info.NominalType;
    }

    public IReadOnlyProperty Property { get; }
    public string? ElementName { get; }
    public string[]? ElementPath { get; }
    public IBsonSerializer Serializer { get; }
    public Type NominalType { get; }
}
