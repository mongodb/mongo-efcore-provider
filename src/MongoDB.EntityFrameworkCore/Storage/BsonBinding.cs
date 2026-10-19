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
    /// Resolves <paramref name="name"/> against <paramref name="document"/>, walking a DOTTED name segment by
    /// segment instead of looking it up as a single literal key.
    /// </summary>
    /// <remarks>
    /// A dotted name reaches here only from an alias that is a leaf's root-relative document path, so a
    /// dotted-path read and a nested-document read are the same read — MongoDB itself renders
    /// <c>$project: {"Home.Notes": "$Home.Notes"}</c> as a nested output document, not a flat dotted key. An
    /// absent segment anywhere along the path yields <see langword="false"/>, same as a missing top-level
    /// element; an intermediate segment that is present but not a document also yields
    /// <see langword="false"/> rather than throwing, so this never turns a readable document into a cast
    /// failure.
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
    /// Create the expression which reads an element nested under one or more parent documents, walking
    /// <paramref name="path"/> segment by segment.
    /// </summary>
    /// <remarks>
    /// This is deliberately a SEPARATE entry point from <see cref="CreateGetElementValue"/> rather than a
    /// dotted-name overload of it: <see cref="GetElementValue{T}"/> looks its name up as a single LITERAL
    /// document key (a dotted name there finds nothing), and several existing callers pass aliases that may
    /// legitimately contain dots, so widening that method's semantics would change their reads. Callers that
    /// genuinely mean "walk into a sub-document" say so explicitly here.
    /// </remarks>
    internal static MethodCallExpression CreateGetElementValueAtPath(Expression bsonDocExpression, string[] path, Type type) =>
        Expression.Call(null, GetElementValueAtPathMethodInfo.MakeGenericMethod(type), bsonDocExpression,
            Expression.Constant(path));

    /// <summary>
    /// Create the expression which reads a value nested under one or more parent documents, walking
    /// <paramref name="path"/> segment by segment and reading the LAST segment through
    /// <paramref name="property"/>'s own serializer / nullability, exactly as
    /// <see cref="GetPropertyValueAtElement{T}"/> does for a top-level element.
    /// </summary>
    /// <remarks>
    /// The path-walking sibling of the <c>(name, property, mappedType)</c> overload of
    /// <see cref="CreateGetValueExpression(Expression, string?, IProperty, Type)"/>, and the property-aware
    /// sibling of <see cref="CreateGetElementValueAtPath"/> (which uses a bare TYPE serializer and so cannot
    /// honour a value converter or a non-default BSON representation). Used when a projection leaf's alias and
    /// its root-relative document path differ and the shaper is reading WHOLE, un-projected documents.
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
                // An absent INTERMEDIATE segment is the ordinary shape of an unmatched left-outer join row: the
                // whole joined sub-document ("_lookup_<Nav>") is not there. That is a structurally different
                // condition from "the document is here but this leaf is missing", so it is deliberately NOT
                // dispatched on property.IsNullable (the rule the leaf read below uses) but on whether the
                // REQUESTED CLR TYPE can hold absence — i.e. exactly the rule GetElementValue{T} applies.
                //
                // MEASURED, and this is why it matters (EF-444 Task 4): the native leg reads the same unmatched
                // row's leaf through GetElementValue{T} and yields null for a `string Region` — dispatching on
                // property.IsNullable here instead made the DriverLinq/late-fallback leg THROW for that same
                // row while Native succeeded, a mode-dependent divergence. See NativeJoinTests
                // .LeftJoin_unmatched_row_reads_a_dotted_scalar_leaf_through_the_whole_document_path, which pins
                // the two legs against EACH OTHER rather than against a hard-coded disposition.
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
    {
        var type = typeof(T);
        var serializationInfo =
            BsonSerializationInfo.CreateWithPath(path, BsonSerializerFactory.CreateTypeSerializer(type), type);
        if (TryReadElementValue(document, serializationInfo, out T? value) || type.IsNullableType())
        {
            return value;
        }

        throw new InvalidOperationException($"Document element '{string.Join(".", path)}' is missing but required.");
    }

    internal static T? GetElementValue<T>(BsonDocument document, string elementName)
    {
        var type = typeof(T);
        var serializationInfo = new BsonSerializationInfo(elementName, BsonSerializerFactory.CreateTypeSerializer(type), type);
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
