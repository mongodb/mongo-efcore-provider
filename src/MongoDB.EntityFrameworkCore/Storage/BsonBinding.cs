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
    /// <param name="bsonDocExpression">The expression to obtain the current <see cref="BsonDocument"/>.</param>
    /// <param name="elementName">The name of the field in the document that contains the desired value.</param>
    /// <param name="mappedType">What <see cref="Type"/> to the value is to be treated as.</param>
    /// <returns>A compilable expression the shaper can use to obtain this value from a <see cref="BsonDocument"/>.</returns>
    /// <exception cref="InvalidOperationException">If we can't find anything mapped to this name.</exception>
    public static Expression CreateGetValueExpression(
        Expression bsonDocExpression,
        string? elementName,
        Type mappedType)
    {
        if (elementName is null)
        {
            return bsonDocExpression;
        }

        if (mappedType == typeof(BsonArray))
        {
            return CreateGetBsonArray(bsonDocExpression, elementName);
        }

        if (mappedType == typeof(BsonDocument))
        {
            return CreateGetBsonDocument(bsonDocExpression, elementName);
        }

        return CreateGetElementValue(bsonDocExpression, elementName, mappedType);
    }

    private static Expression CreateGetBsonArray(Expression bsonDocExpression, string name)
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
        Expression bsonDocExpression, string name)
        => Expression.Call(null, GetBsonDocumentMethodInfo, bsonDocExpression, Expression.Constant(name));

    private static readonly MethodInfo GetBsonDocumentMethodInfo
        = typeof(BsonBinding).GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(mi => mi.Name == nameof(GetBsonDocument));

    private static BsonDocument? GetBsonDocument(BsonDocument parent, string name)
    {
        var value = parent.GetValue(name, BsonNull.Value);
        return value == BsonNull.Value ? null : value.AsBsonDocument;
    }

    private static MethodCallExpression CreateGetElementValue(Expression bsonDocExpression, string name, Type type) =>
        Expression.Call(null, GetElementValueMethodInfo.MakeGenericMethod(type), bsonDocExpression, Expression.Constant(name));

    private static readonly MethodInfo GetElementValueMethodInfo
        = typeof(BsonBinding).GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(mi => mi.Name == nameof(GetElementValue));

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
