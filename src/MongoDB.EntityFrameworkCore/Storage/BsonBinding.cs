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
            return CreateGetPropertyValue(bsonDocExpression, Expression.Constant(targetProperty),
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
            Expression.Constant(property),
            property.IsNullable ? mappedType.MakeNullable() : mappedType);
    }

    internal static MethodCallExpression CreateGetBsonArray(Expression bsonDocExpression, string name)
        => Expression.Call(null, GetBsonArrayMethodInfo, bsonDocExpression, Expression.Constant(name));

    private static readonly MethodInfo GetBsonArrayMethodInfo
        = typeof(BsonBinding).GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(mi => mi.Name == nameof(GetBsonArray));

    private static BsonArray? GetBsonArray(BsonDocument document, string name)
    {
        if (!TryGetValueAtPath(document, name, out var bsonValue)) return null;

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
            : Expression.Call(null, GetKindAwareElementValueMethodInfo.MakeGenericMethod(type), bsonDocExpression,
                Expression.Constant(name),
                Expression.Constant(BsonSerializerFactory.CreateTypeSerializer(type, dateTimeKindSource), typeof(IBsonSerializer)));

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
            Expression.Constant(property));

    private static readonly MethodInfo GetPropertyValueAtPathMethodInfo
        = typeof(BsonBinding).GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(mi => mi.Name == nameof(GetPropertyValueAtPath));

    internal static T? GetPropertyValueAtPath<T>(BsonDocument document, string[] path, IReadOnlyProperty property)
    {
        var current = document;
        for (var i = 0; i < path.Length - 1; i++)
        {
            if (!current.TryGetValue(path[i], out var segmentValue) || segmentValue is not BsonDocument segmentDocument)
            {
                // An absent intermediate segment is an unmatched left-outer join row (no "_lookup_<Nav>"). Dispatch
                // on whether T can hold absence, as GetElementValue{T} does, not on property.IsNullable — otherwise
                // the DriverLinq/late-fallback leg throws where Native yields null. Pinned by NativeJoinTests
                // .LeftJoin_unmatched_row_reads_a_dotted_scalar_leaf_through_the_whole_document_path.
                if (typeof(T).IsNullableType())
                {
                    return default;
                }

                throw new InvalidOperationException(
                    $"Document element '{string.Join(".", path)}' is missing for required non-nullable property '{
                        property.Name}'.");
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

    internal static T? GetPropertyValue<T>(BsonDocument? document, IReadOnlyProperty property)
    {
        // A null parent document means the owning entity is absent entirely (e.g. an optional
        // cross-collection reference nested inside a collection Include whose $lookup matched no
        // document). Treat every property as absent so the entity materializer's null-key check
        // produces a null entity rather than dereferencing the missing document.
        if (document == null)
        {
            return default;
        }

        var serializationInfo = BsonSerializerFactory.GetPropertySerializationInfo(property);
        if (TryReadElementValue(document, serializationInfo, out T? value))
        {
            if (value == null && !property.IsNullable)
            {
                throw new InvalidOperationException($"Document element is null for required non-nullable property '{property.Name}'.");
            }

            return value;
        }

        if (property.IsNullable) return default;

        throw new InvalidOperationException($"Document element is missing for required non-nullable property '{property.Name}'.");
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
            Expression.Constant(property, typeof(IReadOnlyProperty)),
            Expression.Constant(placeholder, resultType));

    private static readonly MethodInfo GetPropertyValueOrPlaceholderMethodInfo
        = typeof(BsonBinding).GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(mi => mi.Name == nameof(GetPropertyValueOrPlaceholder));

    internal static T? GetPropertyValueOrPlaceholder<T>(BsonDocument? document, IReadOnlyProperty property, T placeholder)
    {
        if (document == null)
        {
            return default;
        }

        var serializationInfo = BsonSerializerFactory.GetPropertySerializationInfo(property);
        if (!TryReadElementValue(document, serializationInfo, out T? value))
        {
            return placeholder;
        }

        if (value == null && !property.IsNullable)
        {
            throw new InvalidOperationException($"Document element is null for required non-nullable property '{property.Name}'.");
        }

        return value;
    }

    internal static T? GetPropertyValueAtElement<T>(BsonDocument document, string elementName, IReadOnlyProperty property)
    {
        var serializationInfo = BsonSerializerFactory.GetPropertySerializationInfo(property);

        // Intentionally drop any ElementPath from the source serialization info: in a projection the
        // value lives at the flat alias name in the projected document, not at the property's original
        // (possibly nested, e.g. ["_id", name] for a composite key) path. Re-introducing the path here
        // would break aliased projections of composite-key parts.
        var projectedSerializationInfo = new BsonSerializationInfo(
            elementName,
            serializationInfo.Serializer,
            serializationInfo.NominalType);

        if (TryReadElementValue(document, projectedSerializationInfo, out T? value))
        {
            if (value == null && !property.IsNullable)
            {
                throw new InvalidOperationException($"Document element is null for required non-nullable property '{property.Name}'.");
            }

            return value;
        }

        if (property.IsNullable) return default;

        throw new InvalidOperationException($"Document element '{elementName}' is missing for required non-nullable property '{property.Name}'.");
    }

    internal static T? GetElementValueAtPath<T>(BsonDocument document, string[] path)
        => ReadElementValueAtPath<T>(document, path, BsonSerializerFactory.CreateTypeSerializer(typeof(T)));

    // `serializer` is BsonSerializerFactory.CreateTypeSerializer(typeof(T), dateTimeKindSource), built at compile time.
    internal static T? GetKindAwareElementValueAtPath<T>(BsonDocument document, string[] path, IBsonSerializer serializer)
        => ReadElementValueAtPath<T>(document, path, serializer);

    private static T? ReadElementValueAtPath<T>(BsonDocument document, string[] path, IBsonSerializer serializer)
    {
        var type = typeof(T);
        var serializationInfo = BsonSerializationInfo.CreateWithPath(path, serializer, type);
        if (TryReadElementValue(document, serializationInfo, out T? value) || type.IsNullableType())
        {
            return value;
        }

        throw new InvalidOperationException($"Document element '{string.Join(".", path)}' is missing but required.");
    }

    internal static T? GetElementValue<T>(BsonDocument document, string elementName)
        => ReadElementValue<T>(document, elementName, BsonSerializerFactory.CreateTypeSerializer(typeof(T)));

    // `serializer` is built at compile time: BsonSerializerFactory.CreateTypeSerializer(typeof(T), dateTimeKindSource),
    // or BsonSerializerFactory.CreateTimeOfDaySerializer for a native TimeOfDay leaf.
    internal static T? GetKindAwareElementValue<T>(BsonDocument document, string elementName, IBsonSerializer serializer)
        => ReadElementValue<T>(document, elementName, serializer);

    private static T? ReadElementValue<T>(BsonDocument document, string elementName, IBsonSerializer serializer)
    {
        var type = typeof(T);
        var serializationInfo = new BsonSerializationInfo(elementName, serializer, type);
        if (TryReadElementValue(document, serializationInfo, out T? value) || type.IsNullableType())
        {
            return value;
        }

        throw new InvalidOperationException($"Document element '{elementName}' is missing but required.");
    }

    private static bool TryReadElementValue<T>(BsonDocument document, BsonSerializationInfo elementSerializationInfo, out T? value)
    {
        BsonValue? rawValue;
        if (elementSerializationInfo.ElementPath == null)
        {
            document.TryGetValue(elementSerializationInfo.ElementName, out rawValue);
        }
        else
        {
            rawValue = document;
            foreach (var node in elementSerializationInfo.ElementPath)
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
            value = (T)elementSerializationInfo.DeserializeValue(rawValue);
            return true;
        }

        value = default;
        return false;
    }
}
