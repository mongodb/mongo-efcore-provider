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
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.RegularExpressions;
using MongoDB.Bson;

namespace MongoDB.EntityFrameworkCore.Query;

/// <summary>
/// Provides conversion functions from BSON used by the EF shaper via <see cref="MongoBsonShaperRebindingExpressionVisitor" />.
/// </summary>
internal static class BsonConverter
{
    public static Expression BsonValueToType(Expression bsonValueExpression, Type type) =>
        Expression.Call(null, __convertBsonValueMethodInfo, bsonValueExpression, Expression.Constant(type));

    public static Expression BsonArrayToArray(Expression bsonArrayExpression, Type elementType) =>
        Expression.Call(null, __toArrayMethodInfo.MakeGenericMethod(elementType), bsonArrayExpression);

    public static Expression BsonArrayToEnumerable(Expression bsonArrayExpression, ConstructorInfo constructor, Type elementType) =>
        Expression.New(constructor,
            Expression.Call(null, __asEnumerableMethodInfo.MakeGenericMethod(elementType), bsonArrayExpression));

    public static T[] ToArray<T>(BsonArray array)
    {
        var newArray = new T[array.Count];
        for (int i = 0; i < array.Count; i++)
            newArray[i] = (T)ConvertValue(array[i], typeof(T));
        return newArray;
    }

    public static IEnumerable<T> AsEnumerable<T>(BsonArray array)
    {
        foreach (var item in array)
            yield return (T)ConvertValue(item, typeof(T));
    }

    private static readonly MethodInfo __toArrayMethodInfo
        = typeof(BsonConverter).GetMethod(nameof(ToArray), BindingFlags.Static | BindingFlags.Public)!;

    private static readonly MethodInfo __asEnumerableMethodInfo
        = typeof(BsonConverter).GetMethod(nameof(AsEnumerable), BindingFlags.Static | BindingFlags.Public)!;

    private static readonly MethodInfo __convertBsonValueMethodInfo
        = typeof(BsonConverter).GetMethods(BindingFlags.Static | BindingFlags.Public)
            .Single(mi => mi.Name == nameof(ConvertValue) && mi.GetParameters().Length == 2)!;

    public static object? ConvertValue(BsonValue value, Type type)
    {
        if (type == typeof(string)) return value.AsString;

        // Non-nullable CLR types with direct map
        if (type == typeof(bool)) return value.AsBoolean;
        if (type == typeof(decimal)) return value.AsDecimal;
        if (type == typeof(double)) return value.AsDouble;
        if (type == typeof(int)) return value.AsInt32;
        if (type == typeof(long)) return value.AsInt64;
        if (type == typeof(Guid)) return value.AsGuid;
        if (type == typeof(Regex)) return value.AsRegex;
        if (type == typeof(DateTime)) return value.ToUniversalTime();

        // Non-nullable MongoDB types
        if (type == typeof(ObjectId)) return value.AsObjectId;
        if (type == typeof(Decimal128)) return value.AsDecimal128;

        // Non-nullable CLR types that require conversion
        if (type == typeof(byte)) return Convert.ToByte(value.AsInt32);
        if (type == typeof(sbyte)) return Convert.ToSByte(value.AsString);
        if (type == typeof(char)) return Convert.ToChar(value.AsString);
        if (type == typeof(float)) return Convert.ToSingle(value.AsDouble);
        if (type == typeof(short)) return Convert.ToInt16(value.AsInt32);
        if (type == typeof(ushort)) return Convert.ToUInt16(value.AsInt32);
        if (type == typeof(uint)) return Convert.ToUInt32(value.AsInt32);
        if (type == typeof(ulong)) return Convert.ToUInt32(value.AsInt64);

        // Nullable CLR types with direct map
        if (type == typeof(bool?)) return value.AsNullableBoolean;
        if (type == typeof(decimal?)) return value.AsNullableDecimal;
        if (type == typeof(double?)) return value.AsNullableDouble;
        if (type == typeof(int?)) return value.AsNullableInt32;
        if (type == typeof(long?)) return value.AsNullableInt64;
        if (type == typeof(Guid?)) return value.AsNullableGuid;
        if (type == typeof(DateTime?)) return value.ToNullableUniversalTime();

        // Nullable MongoDB types
        if (type == typeof(ObjectId?)) return value.AsNullableObjectId;
        if (type == typeof(Decimal128?)) return value.AsNullableDecimal128;

        // Nullable CLR types that require conversion
        if (type == typeof(byte?)) return value.IsBsonNull ? null : Convert.ToByte(value.AsInt32);
        if (type == typeof(sbyte?)) return value.IsBsonNull ? null : Convert.ToSByte(value.AsString);
        if (type == typeof(char?)) return value.IsBsonNull ? null : Convert.ToChar(value.AsString);
        if (type == typeof(float?)) return value.IsBsonNull ? null : Convert.ToSingle(value.AsDouble);
        if (type == typeof(short?)) return value.IsBsonNull ? null : Convert.ToInt16(value.AsInt32);
        if (type == typeof(ushort?)) return value.IsBsonNull ? null : Convert.ToUInt16(value.AsInt32);
        if (type == typeof(uint?)) return value.IsBsonNull ? null : Convert.ToUInt32(value.AsInt32);
        if (type == typeof(ulong?)) return value.IsBsonNull ? null : Convert.ToUInt32(value.AsInt64);

        throw new NotSupportedException($"Type {type.Name} can not be materialized from the BSON.");
    }
}
