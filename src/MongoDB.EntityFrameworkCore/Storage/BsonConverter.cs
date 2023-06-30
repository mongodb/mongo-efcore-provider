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
using MongoDB.EntityFrameworkCore.Query.Visitors;

namespace MongoDB.EntityFrameworkCore.Storage;

/// <summary>
/// Provides conversion functions from BSON used by the EF shaper via <see cref="MongoBsonShaperRebindingExpressionVisitor" />.
/// </summary>
internal static class BsonConverter
{
    /// <summary>
    /// Create an expression that will convert the <see cref="BsonValue"/> held in <see cref="bsonValueExpression"/> to
    /// the desired type <see cref="type"/> using <see cref="ConvertValue"/>.
    /// </summary>
    /// <param name="bsonValueExpression">The expression containing the <see cref="BsonValue"/> to convert.</param>
    /// <param name="type">The <see cref="Type"/> to convert the value to.</param>
    /// <returns>The converted value.</returns>
    public static Expression BsonValueToType(Expression bsonValueExpression, Type type) =>
        Expression.Call(null, __convertBsonValueMethodInfo, bsonValueExpression, Expression.Constant(type));

    /// <summary>
    /// Create an expression that will convert the <see cref="BsonArray"/> held in <see cref="bsonArrayExpression"/> to
    /// the desired array of <see cref="elementType"/> items using <see cref="ToArray{T}"/>.
    /// </summary>
    /// <param name="bsonArrayExpression">The expression containing the <see cref="BsonArray"/> to convert.</param>
    /// <param name="elementType">The <see cref="Type"/> of elements in the array.</param>
    /// <returns>The converted array.</returns>
    public static Expression BsonArrayToArray(Expression bsonArrayExpression, Type elementType) =>
        Expression.Call(null, __toArrayMethodInfo.MakeGenericMethod(elementType), bsonArrayExpression);

    /// <summary>
    /// Create an expression that will convert the <see cref="BsonArray"/> held in <see cref="bsonArrayExpression"/> to
    /// the an enumerable <see cref="elementType"/> container that will be created using <see cref="constructor"/>.
    /// </summary>
    /// <param name="bsonArrayExpression">The expression containing the <see cref="BsonArray"/> to convert.</param>
    /// <param name="constructor">The constructor to use to create the array.</param>
    /// <param name="elementType">The <see cref="Type"/> of elements in the array.</param>
    /// <returns>The new enumerable type containing the converted elements.</returns>
    public static Expression BsonArrayToEnumerable(Expression bsonArrayExpression, ConstructorInfo constructor, Type elementType) =>
        Expression.New(constructor,
            Expression.Call(null, __asEnumerableMethodInfo.MakeGenericMethod(elementType), bsonArrayExpression));

    /// <summary>
    /// Create an <see cref="Array"/> of <typeparamref name="T"/> from
    /// an array of <see cref="BsonArray"/> converting each item via <see cref="ConvertValue"/>.
    /// </summary>
    /// <param name="array">The <see cref="BsonArray"/> containing the items to be converted.</param>
    /// <typeparam name="T">The type to which each item should be converted.</typeparam>
    /// <returns>The newly created and converted array.</returns>
    public static T[] ToArray<T>(BsonArray array)
    {
        var newArray = new T[array.Count];
        for (int i = 0; i < array.Count; i++)
            newArray[i] = (T)ConvertValue(array[i], typeof(T))!;
        return newArray;
    }

    /// <summary>
    /// Enumerate through a <see cref="BsonArray"/> converting each item to a <typeparamref name="T"/>
    /// sequentially.
    /// </summary>
    /// <param name="array">The <see cref="BsonArray"/> of items to convert.</param>
    /// <typeparam name="T">The type to convert each item to.</typeparam>
    /// <returns>An <see cref="IEnumerable{T}"/> yielding each converted item.</returns>
    public static IEnumerable<T> AsEnumerable<T>(BsonArray array)
    {
        foreach (var item in array)
            yield return (T)ConvertValue(item, typeof(T))!;
    }

    private static readonly MethodInfo __toArrayMethodInfo
        = typeof(BsonConverter).GetMethod(nameof(ToArray), BindingFlags.Static | BindingFlags.Public)!;

    private static readonly MethodInfo __asEnumerableMethodInfo
        = typeof(BsonConverter).GetMethod(nameof(AsEnumerable), BindingFlags.Static | BindingFlags.Public)!;

    private static readonly MethodInfo __convertBsonValueMethodInfo
        = typeof(BsonConverter).GetMethods(BindingFlags.Static | BindingFlags.Public)
            .Single(mi => mi.Name == nameof(ConvertValue) && mi.GetParameters().Length == 2);

    /// <summary>
    /// Convert a <see cref="BsonValue"/> to the desired <see cref="Type"/> by using
    /// the appropriate Bson conversion properties and any additional conversion mechanism.
    /// </summary>
    /// <param name="value">The <see cref="BsonValue"/> to convert.</param>
    /// <param name="type">The <see cref="Type"/> to convert it to.</param>
    /// <returns>The converted type.</returns>
    /// <exception cref="NotSupportedException">Thrown for any <see cref="Type"/> that is not supported for conversion.</exception>
    public static object? ConvertValue(BsonValue value, Type type)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(type);

        return type switch
        {
            // CLR reference types with a direct map
            not null when type == typeof(string) => value.AsString,

            // Non-nullable CLR types with direct map
            not null when type == typeof(bool) => value.AsBoolean,
            not null when type == typeof(decimal) => value.AsDecimal,
            not null when type == typeof(double) => value.AsDouble,
            not null when type == typeof(int) => value.AsInt32,
            not null when type == typeof(long) => value.AsInt64,
            not null when type == typeof(Guid) => value.AsGuid,
            not null when type == typeof(Regex) => value.AsRegex,
            not null when type == typeof(DateTime) => value.ToUniversalTime(),

            // Non-nullable MongoDB types
            not null when type == typeof(ObjectId) => value.AsObjectId,
            not null when type == typeof(Decimal128) => value.AsDecimal128,

            // Non-nullable CLR types that require conversion
            not null when type == typeof(byte) => Convert.ToByte(value.AsInt32),
            not null when type == typeof(sbyte) => Convert.ToSByte(value.AsString),
            not null when type == typeof(char) => Convert.ToChar(value.AsString),
            not null when type == typeof(float) => Convert.ToSingle(value.AsDouble),
            not null when type == typeof(short) => Convert.ToInt16(value.AsInt32),
            not null when type == typeof(ushort) => Convert.ToUInt16(value.AsInt32),
            not null when type == typeof(uint) => Convert.ToUInt32(value.AsInt32),
            not null when type == typeof(ulong) => Convert.ToUInt32(value.AsInt64),

            // Nullable CLR types with direct map
            not null when type == typeof(bool?) => value.AsNullableBoolean,
            not null when type == typeof(decimal?) => value.AsNullableDecimal,
            not null when type == typeof(double?) => value.AsNullableDouble,
            not null when type == typeof(int?) => value.AsNullableInt32,
            not null when type == typeof(long?) => value.AsNullableInt64,
            not null when type == typeof(Guid?) => value.AsNullableGuid,
            not null when type == typeof(DateTime?) => value.ToNullableUniversalTime(),

            // Nullable MongoDB types
            not null when type == typeof(ObjectId?) => value.AsNullableObjectId,
            not null when type == typeof(Decimal128?) => value.AsNullableDecimal128,

            // Nullable CLR types that require conversion
            not null when type == typeof(byte?) => value.IsBsonNull ? null : Convert.ToByte(value.AsInt32),
            not null when type == typeof(sbyte?) => value.IsBsonNull ? null : Convert.ToSByte(value.AsString),
            not null when type == typeof(char?) => value.IsBsonNull ? null : Convert.ToChar(value.AsString),
            not null when type == typeof(float?) => value.IsBsonNull ? null : Convert.ToSingle(value.AsDouble),
            not null when type == typeof(short?) => value.IsBsonNull ? null : Convert.ToInt16(value.AsInt32),
            not null when type == typeof(ushort?) => value.IsBsonNull ? null : Convert.ToUInt16(value.AsInt32),
            not null when type == typeof(uint?) => value.IsBsonNull ? null : Convert.ToUInt32(value.AsInt32),
            not null when type == typeof(ulong?) => value.IsBsonNull ? null : Convert.ToUInt32(value.AsInt64),

            _ => throw new NotSupportedException($"Type {type?.Name} can not be materialized from the BSON.")
        };
    }
}
