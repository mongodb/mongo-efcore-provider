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
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.Extensions;

namespace MongoDB.EntityFrameworkCore.Query;

/// <summary>
/// EF-337: a numeric property stored as a BSON string (a string <c>BsonRepresentation</c> or a
/// <c>HasConversion&lt;string&gt;()</c>) must not be compared, sorted or aggregated on the server in its stored form:
/// "10" &lt; "9", and <c>$sum</c> ignores strings. The driver pushes those operators onto the field it is given, so
/// the field is read through <c>$convert</c> to the CLR numeric type instead.
/// </summary>
internal static class StoredValueConversion
{
    private static readonly MethodInfo ConvertMethod = typeof(Mql).GetMethods()
        .Single(m => m.Name == nameof(Mql.Convert) && m.GetParameters().Length == 2);

    /// <summary>The server-side types <c>$convert</c> can produce from a string for a CLR numeric type.</summary>
    private static bool IsConvertibleNumeric(Type type)
        => type == typeof(int) || type == typeof(long) || type == typeof(double) || type == typeof(decimal);

    /// <summary>
    /// <see langword="true"/> if the stored form of <paramref name="property"/> is a string holding a number that
    /// <see cref="CreateConvertedField"/> can convert back. Mirrors how <c>BsonSerializerFactory</c> chooses the stored
    /// form: a built-in numeric converter to string, or a string representation (optionally over a built-in numeric
    /// cast). A custom converter's stored form is unknown, so it is left alone.
    /// </summary>
    public static bool RequiresConversion(IReadOnlyProperty property)
    {
        var clrType = property.ClrType.UnwrapNullableType();
        if (!IsConvertibleNumeric(clrType))
        {
            return false;
        }

        var storedAsString = property.GetBsonRepresentation() is { BsonType: BsonType.String };

        if (property.FindTypeMapping() is not { Converter: { } converter })
        {
            return storedAsString;
        }

        var converterType = converter.GetType();
        if (!converterType.IsGenericType || converter.ModelClrType.UnwrapNullableType() != clrType)
        {
            return false;
        }

        var definition = converterType.GetGenericTypeDefinition();
        return converter.ProviderClrType == typeof(string) && definition == typeof(NumberToStringConverter<>)
               || storedAsString && definition == typeof(CastingConverter<,>)
                   && IsConvertibleNumeric(converter.ProviderClrType.UnwrapNullableType());
    }

    /// <summary>Reads <paramref name="property"/> from <paramref name="doc"/> as <c>$convert</c> to its CLR type.</summary>
    public static Expression CreateConvertedField(IReadOnlyProperty property, Expression doc, MethodInfo mqlField)
    {
        var field = Expression.Call(null, mqlField.MakeGenericMethod(doc.Type, typeof(string)), doc,
            Expression.Constant(property.GetElementName()), Expression.Constant(StringSerializer.Instance));

        var optionsType = typeof(ConvertOptions<>).MakeGenericType(property.ClrType);
        return Expression.Call(null, ConvertMethod.MakeGenericMethod(typeof(string), property.ClrType), field,
            Expression.New(optionsType));
    }
}
