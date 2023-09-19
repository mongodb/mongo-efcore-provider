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
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Serializers;

namespace MongoDB.EntityFrameworkCore.Storage;

internal static class BsonBinding
{
    private static Expression CreateGetBsonArray(Expression bsonDocExpression, string name)
        => Expression.Call(null, __getBsonArray, bsonDocExpression, Expression.Constant(name));

    private static readonly MethodInfo __getBsonArray
        = typeof(BsonBinding).GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(mi => mi.Name == nameof(GetBsonArray));

    private static BsonArray GetBsonArray(BsonDocument document, string name)
        => document[name].AsBsonArray;

    private static Expression CreateGetBsonDocument(Expression bsonDocExpression, string name)
        => Expression.Call(null, __getBsonDocument, bsonDocExpression, Expression.Constant(name));

    private static readonly MethodInfo __getBsonDocument
        = typeof(BsonBinding).GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(mi => mi.Name == nameof(GetBsonDocument));

    private static BsonDocument GetBsonDocument(BsonDocument document, string name)
        => document[name].AsBsonDocument;

    public static Expression CreateGetValueExpression(Expression bsonDocExpression, string? name, Type mappedType, IEntityType entityType)
    {
        if (name is null) return bsonDocExpression;
        if (mappedType == typeof(BsonArray)) return CreateGetBsonArray(bsonDocExpression, name);
        if (mappedType == typeof(BsonDocument)) return CreateGetBsonDocument(bsonDocExpression, name);

        var targetProperty = entityType.FindProperty(name);
        if (targetProperty != null)
        {
            if (targetProperty.IsNullable)
            {
                mappedType = mappedType.MakeNullable();
            }

            return CreateGetPropertyValue(bsonDocExpression, Expression.Constant(targetProperty), mappedType);
        }

        var navigationProperty = entityType.FindNavigation(name);
        if (navigationProperty != null)
        {
            var elementName = navigationProperty.GetElementName();
            return CreateGetElementValue(bsonDocExpression, elementName, mappedType);
        }

        throw new InvalidOperationException(CoreStrings.PropertyNotFound(name, entityType.DisplayName()));
    }

    private static Expression CreateGetPropertyValue(Expression bsonDocExpression, Expression propertyExpression, Type resultType) =>
        Expression.Call(null, __getPropertyValueMethodInfo.MakeGenericMethod(resultType), bsonDocExpression, propertyExpression);

    private static Expression CreateGetElementValue(Expression bsonDocExpression, string name, Type type) =>
        Expression.Call(null, __getElementValueMethodInfo.MakeGenericMethod(type), bsonDocExpression, Expression.Constant(name));

    private static readonly MethodInfo __getPropertyValueMethodInfo
        = typeof(SerializationHelper).GetMethods(BindingFlags.Static | BindingFlags.Public)
            .Single(mi => mi.Name == nameof(SerializationHelper.GetPropertyValue));

    private static readonly MethodInfo __getElementValueMethodInfo
        = typeof(SerializationHelper).GetMethods(BindingFlags.Static | BindingFlags.Public)
            .Single(mi => mi.Name == nameof(SerializationHelper.GetElementValue));
}
