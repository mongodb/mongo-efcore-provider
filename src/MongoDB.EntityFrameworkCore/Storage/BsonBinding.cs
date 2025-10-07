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
using System.Diagnostics;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
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
    /// <param name="documentExpression">The expression to obtain the current <see cref="BsonDocument"/>.</param>
    /// <param name="alias">The name of the field in the document that contains the desired value.</param>
    /// <param name="propertyBase">The <see cref="INavigation"/> or <see cref="IProperty"/> mapping to the field.</param>
    /// <returns>A compilable expression the shaper can use to obtain this value from a <see cref="BsonDocument"/>.</returns>
    /// <exception cref="InvalidOperationException">If we can't find anything mapped to this name.</exception>
    public static Expression CreateGetValueExpression(
        Expression documentExpression,
        string? alias,
        IPropertyBase? propertyBase = null)
    {
        if (propertyBase is null && alias is null)
        {
            return documentExpression;
        }

        if (propertyBase is IProperty property)
        {
            return CreateGetPropertyValue(documentExpression, alias, property);
        }

        Debug.Assert(propertyBase is INavigationBase,
            $"Not a property and not a navigation, but a {propertyBase.GetType().ShortDisplayName()}");

        var navigationBase = (INavigationBase)propertyBase!;
        return navigationBase.IsCollection
            ? CreateGetBsonArray(documentExpression, alias, navigationBase)
            : CreateGetBsonDocument(documentExpression, alias, navigationBase);
    }

    private static MethodCallExpression CreateGetBsonArray(Expression documentExpression, string? alias, INavigationBase navigation)
        => Expression.Call(
            GetBsonArrayMethodInfo,
            documentExpression,
            Expression.Constant(alias ?? navigation.Name, typeof(string)));

    private static readonly MethodInfo GetBsonArrayMethodInfo
        = typeof(BsonBinding).GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(mi => mi.Name == nameof(GetBsonArray));

    private static BsonArray? GetBsonArray(BsonDocument document, string name)
    {
        if (!document.TryGetValue(name, out var bsonValue)) return null;

        return bsonValue switch
        {
            {IsBsonArray: true} => bsonValue.AsBsonArray,
            {IsBsonNull: true} => null,
            _ => throw new InvalidOperationException(
                $"Document element '{name}' is {bsonValue.BsonType} when {nameof(BsonArray)} is required.")
        };
    }

    private static MethodCallExpression CreateGetBsonDocument(
        Expression documentExpression, string? alias, INavigationBase navigationBase)
        => Expression.Call(null, GetBsonDocumentMethodInfo, documentExpression, Expression.Constant(alias ?? navigationBase.Name),
            Expression.Constant(navigationBase is INavigation { ForeignKey.IsRequiredDependent: true }),
            Expression.Constant(navigationBase.DeclaringEntityType));

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
        CreateGetPropertyValue(Expression documentExpression, string? alias, IProperty property)
        => Expression.Call(
            GetPropertyValueMethodInfo.MakeGenericMethod(property.IsNullable ? property.ClrType.MakeNullable() : property.ClrType),
            documentExpression,
            Expression.Constant(alias ?? property.GetElementName(), typeof(string)),
            Expression.Constant(property));

    private static readonly MethodInfo GetPropertyValueMethodInfo
        = typeof(BsonBinding).GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(mi => mi.Name == nameof(GetPropertyValue));

    internal static T? GetPropertyValue<T>(BsonDocument document, string? alias, IReadOnlyProperty property)
    {
        var serializationInfo = BsonSerializerFactory.GetPropertySerializationInfo(alias, property);
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

        throw new InvalidOperationException($"Document element is missing for required non-nullable property '{alias ?? property.Name}'.");
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
