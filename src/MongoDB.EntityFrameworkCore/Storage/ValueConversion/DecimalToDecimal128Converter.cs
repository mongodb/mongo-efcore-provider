﻿/* Copyright 2023-present MongoDB Inc.
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

using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using MongoDB.Bson;

namespace MongoDB.EntityFrameworkCore.Storage.ValueConversion;

/// <summary>
/// Converts a <see cref="decimal"/> to and from <see cref="Decimal128" /> values.
/// </summary>
/// <remarks>
/// See <see href="https://aka.ms/efcore-docs-value-converters">EF Core value converters</see> for more information and examples.
/// </remarks>
public class DecimalToDecimal128Converter : Decimal128DecimalConverter<decimal, Decimal128>
{
    /// <summary>
    /// Creates a new instance of the <see cref="StringToObjectIdConverter"/> class.
    /// </summary>
    public DecimalToDecimal128Converter()
        : this(null)
    {
    }

    /// <summary>
    /// Creates a new instance of the <see cref="DecimalToDecimal128Converter"/> class.
    /// </summary>
    /// <param name="mappingHints">
    /// Hints that can be used by the <see cref="ITypeMappingSource" /> to create data types with appropriate
    /// facets for the converted data.
    /// </param>
    public DecimalToDecimal128Converter(ConverterMappingHints? mappingHints = null)
        : base(
            ConvertToDecimal128(),
            ConvertToDecimal(),
            mappingHints)
    {
    }

    /// <summary>
    /// A <see cref="ValueConverterInfo" /> for the default use of this converter.
    /// </summary>
    public static ValueConverterInfo DefaultInfo { get; }
        = new(typeof(string), typeof(ObjectId), i => new DecimalToDecimal128Converter(i.MappingHints));
}
