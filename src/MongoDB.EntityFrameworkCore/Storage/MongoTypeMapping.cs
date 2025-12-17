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

using System.Reflection;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Storage.Json;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace MongoDB.EntityFrameworkCore.Storage;

/// <summary>
/// Represents the mapping between a .NET type and a MongoDB database type.
/// </summary>
public class MongoTypeMapping : CoreTypeMapping
{
    /// <summary>
    /// Get the default <see cref="MongoTypeMapping"/> instance.
    /// </summary>
    public static MongoTypeMapping Default { get; } = new(typeof(object));

    /// <summary>
    /// Create a <see cref="MongoTypeMapping"/> with the supplied CLR type and value comparers.
    /// </summary>
    /// <param name="clrType">The CLR Type this mapping maps to.</param>
    /// <param name="comparer">The <see cref="ValueComparer"/> to be used to compare non-key values.</param>
    /// <param name="keyComparer">The <see cref="ValueComparer"/> to be used to compare keys.</param>
    public MongoTypeMapping(Type clrType, ValueComparer? comparer = null, ValueComparer? keyComparer = null)
        : base(new CoreTypeMappingParameters(clrType, converter: null, comparer, keyComparer))
    {
    }

    /// <summary>
    /// Create a <see cref="MongoTypeMapping"/> as described by the mapping parameters.
    /// </summary>
    /// <param name="parameters">The <see cref="CoreTypeMapping.CoreTypeMappingParameters"/> use to define this mapping.</param>
    protected MongoTypeMapping(CoreTypeMappingParameters parameters)
        : base(parameters)
    {
    }

    /// <inheritdoc/>
    protected override CoreTypeMapping Clone(CoreTypeMappingParameters parameters)
        => new MongoTypeMapping(parameters);


    /// <inheritdoc/>
    public override Expression GenerateCodeLiteral(object value)
    {
        return value switch
        {
            ObjectId => Expression.Call(ObjectIdParseStringMethod, Expression.Constant(value.ToString())),
            Decimal128 => Expression.Call(Decimal128ParseStringMethod, Expression.Constant(value.ToString())),
            _ => base.GenerateCodeLiteral(value)
        };
    }

    /// <inheritdoc/>
    public override CoreTypeMapping WithComposedConverter(
        ValueConverter? converter,
        ValueComparer? comparer = null,
        ValueComparer? keyComparer = null,
        CoreTypeMapping? elementMapping = null,
        JsonValueReaderWriter? jsonValueReaderWriter = null)
        => new MongoTypeMapping(Parameters.WithComposedConverter(converter, comparer, keyComparer, elementMapping,
            jsonValueReaderWriter));

    private static readonly MethodInfo ObjectIdParseStringMethod
        = typeof(ObjectId).GetRuntimeMethod(nameof(ObjectId.Parse), [typeof(string)])!;

    private static readonly MethodInfo Decimal128ParseStringMethod
        = typeof(Decimal128).GetRuntimeMethod(nameof(Decimal128.Parse), [typeof(string)])!;
}
