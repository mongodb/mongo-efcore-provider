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
using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Query.Visitors;

namespace MongoDB.EntityFrameworkCore.Storage;

/// <summary>
/// Provides conversion functions from BSON used by the EF shaper via <see cref="MongoProjectionBindingRemovingExpressionVisitor" />.
/// </summary>
internal static class BsonConverter
{
    /// <summary>
    /// Get a value as a specific type by name.
    /// </summary>
    /// <param name="doc">The <see cref="BsonDocument"/> to obtain the value from.</param>
    /// <param name="name">The string name of the element within the BSON.</param>
    /// <typeparam name="T">The <see cref="Type"/> of value to obtain.</typeparam>
    /// <returns>The converted value.</returns>
    public static T GetValueAs<T>(BsonDocument doc, string name)
    {
        var result = doc.GetValue(name, BsonNull.Value);
        return (T)(ConvertValue(result, typeof(T)) ?? default(T));
    }

    /// <summary>
    /// Get a value as an array of typed values.
    /// </summary>
    /// <param name="doc">The <see cref="BsonDocument"/> to obtain the values from.</param>
    /// <param name="name">The string name of the element within the BSON.</param>
    /// <typeparam name="T">The type to which each item should be converted.</typeparam>
    /// <returns>The newly created array containing the converted values.</returns>
    public static T[] GetArrayOf<T>(BsonDocument doc, string name)
    {
        var result = doc.GetValue(name, BsonNull.Value);
        return ToArray<T>(result.IsBsonNull ? new BsonArray() : result.AsBsonArray);
    }

    /// <summary>
    /// Get a value as an array of typed values.
    /// </summary>
    /// <param name="doc">The <see cref="BsonDocument"/> to obtain the values from.</param>
    /// <param name="name">The string name of the element within the BSON.</param>
    /// <typeparam name="T">The type to which each item should be converted.</typeparam>
    /// <returns>A <see cref="IEnumerable{T}"/> containing the converted values.</returns>
    public static IEnumerable<T> GetEnumerableOf<T>(BsonDocument doc, string name)
    {
        var result = doc.GetValue(name, BsonNull.Value);
        return ToEnumerable<T>(result.IsBsonNull ? new BsonArray() : result.AsBsonArray);
    }

    /// <summary>
    /// Create an <see cref="Array"/> of <typeparamref name="T"/> from
    /// an array of <see cref="BsonArray"/> converting each item via <see cref="ConvertValue"/>.
    /// </summary>
    /// <param name="array">The <see cref="BsonArray"/> containing the items to be converted.</param>
    /// <typeparam name="T">The type to which each item should be converted.</typeparam>
    /// <returns>The newly created array containing the converted values.</returns>
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
    public static IEnumerable<T> ToEnumerable<T>(BsonArray array)
    {
        foreach (var item in array)
            yield return (T)ConvertValue(item, typeof(T))!;
    }

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
            // Used for nesting entities
            not null when type == typeof(BsonDocument) => value.AsBsonDocument,

            // CLR reference types with a direct map
            not null when type == typeof(string) => value.AsString,

            // Non-nullable CLR types with direct map
            not null when type == typeof(bool) => value.AsBoolean,
            not null when type == typeof(decimal) => decimal.Parse(value.AsString),
            not null when type == typeof(double) => value.AsDouble,
            not null when type == typeof(int) => value.AsInt32,
            not null when type == typeof(long) => value.AsInt64,
            not null when type == typeof(Guid) => value.AsGuid,
            not null when type == typeof(Regex) => value.AsRegex,
            not null when type == typeof(DateTime) => value.ToUniversalTime(),

            // Non-nullable MongoDB types
            not null when type == typeof(ObjectId) => value.AsObjectId,
            not null when type == typeof(Decimal128) => Decimal128.Parse(value.AsString),

            // Non-nullable CLR types that require conversion
            not null when type == typeof(byte) => Convert.ToByte(value.AsInt32),
            not null when type == typeof(sbyte) => Convert.ToSByte(value.AsString),
            not null when type == typeof(char) => char.ConvertFromUtf32(value.AsInt32)[0],
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
            not null when type == typeof(char?) => value.IsBsonNull ? null : char.ConvertFromUtf32(value.AsInt32)[0],
            not null when type == typeof(float?) => value.IsBsonNull ? null : Convert.ToSingle(value.AsDouble),
            not null when type == typeof(short?) => value.IsBsonNull ? null : Convert.ToInt16(value.AsInt32),
            not null when type == typeof(ushort?) => value.IsBsonNull ? null : Convert.ToUInt16(value.AsInt32),
            not null when type == typeof(uint?) => value.IsBsonNull ? null : Convert.ToUInt32(value.AsInt32),
            not null when type == typeof(ulong?) => value.IsBsonNull ? null : Convert.ToUInt32(value.AsInt64),

            _ => throw new NotSupportedException($"Type {type?.Name} can not be materialized from the BSON.")
        };
    }
}
