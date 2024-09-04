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
    /// <see langref="true"/> if the field is required to be present in the document,
    /// <see langref="false"/> if it is optional.
    /// </param>
    /// <param name="mappedType">What <see cref="Type"/> to the value is to be treated as.</param>
    /// <param name="declaredType">The <see cref="IEntityType"/> the value will belong to in order to obtaining additional metadata.</param>
    /// <returns>A compilable expression the shaper can use to obtain this value from a <see cref="BsonDocument"/>.</returns>
    /// <exception cref="InvalidOperationException">If we can't find anything mapped to this name.</exception>
    public static Expression CreateGetValueExpression(
        Expression bsonDocExpression,
        string? name,
        bool required,
        Type mappedType,
        ITypeBase? declaredType)
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
            return CreateGetBsonDocument(bsonDocExpression, name, required);
        }

        if (declaredType != null)
        {
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
        }

        throw new InvalidOperationException(CoreStrings.PropertyNotFound(name, declaredType.DisplayName()));
    }

    private static MethodCallExpression CreateGetBsonArray(Expression bsonDocExpression, string name)
        => Expression.Call(null, GetBsonArrayMethodInfo, bsonDocExpression, Expression.Constant(name));

    private static readonly MethodInfo GetBsonArrayMethodInfo
        = typeof(BsonBinding).GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(mi => mi.Name == nameof(GetBsonArray));

    private static BsonArray? GetBsonArray(BsonDocument document, string name)
    {
        if (!document.TryGetValue(name, out var bsonValue))
        {
            throw new InvalidOperationException($"Document element '{name}' is mapped collection but missing.");
        }

        return bsonValue switch
        {
            {IsBsonArray: true} => bsonValue.AsBsonArray,
            {IsBsonNull: true} => null,
            _ => throw new InvalidOperationException(
                $"Document element '{name}' is {bsonValue.BsonType} when {nameof(BsonArray)} is required.")
        };
    }

    private static MethodCallExpression CreateGetBsonDocument(
        Expression bsonDocExpression, string name, bool required)
        => Expression.Call(null, GetBsonDocumentMethodInfo, bsonDocExpression, Expression.Constant(name),
            Expression.Constant(required));

    private static readonly MethodInfo GetBsonDocumentMethodInfo
        = typeof(BsonBinding).GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(mi => mi.Name == nameof(GetBsonDocument));

    private static BsonDocument? GetBsonDocument(BsonDocument parent, string name, bool required)
    {
        var value = parent.GetValue(name, BsonNull.Value);
        if (value == BsonNull.Value && required)
        {
            throw new InvalidOperationException($"Field '{name}' required but not present in BsonDocument.");
        }

        return value == BsonNull.Value ? null : value.AsBsonDocument;
    }

    private static MethodCallExpression
        CreateGetPropertyValue(Expression bsonDocExpression, Expression propertyExpression, Type resultType) =>
        Expression.Call(null, GetPropertyValueMethodInfo.MakeGenericMethod(resultType), bsonDocExpression, propertyExpression);

    private static MethodCallExpression CreateGetElementValue(Expression bsonDocExpression, string name, Type type) =>
        Expression.Call(null, GetElementValueMethodInfo.MakeGenericMethod(type), bsonDocExpression, Expression.Constant(name));

    private static readonly MethodInfo GetPropertyValueMethodInfo
        = typeof(BsonBinding).GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(mi => mi.Name == nameof(GetPropertyValue));

    private static readonly MethodInfo GetElementValueMethodInfo
        = typeof(BsonBinding).GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(mi => mi.Name == nameof(GetElementValue));

    internal static T? GetPropertyValue<T>(BsonDocument document, IReadOnlyProperty property)
    {
        var serializationInfo = BsonSerializerFactory.GetPropertySerializationInfo(property);
        if (TryReadElementValue(document, serializationInfo, out T? value))
        {
            if (value == null && !property.IsNullable)
            {
                throw new InvalidOperationException($"Document element is null for required non-nullable property '{property.Name
                }'.");
            }

            return value;
        }

        if (property.IsNullable) return default;

        throw new InvalidOperationException($"Document element is missing for required non-nullable property '{property.Name}'.");
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

        if (rawValue != null)
        {
            value = (T)elementSerializationInfo.DeserializeValue(rawValue);
            return true;
        }

        value = default;
        return false;
    }
}
