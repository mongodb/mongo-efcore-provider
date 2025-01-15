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
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;

namespace MongoDB.EntityFrameworkCore.Serializers;

/// <summary>
/// Provides the interface between the EFCore <see cref="ValueConverter"/>
/// and the MongoDB LINQ provider's <see cref="IBsonDocumentSerializer"/> interface.
/// </summary>
/// <typeparam name="TActual">The CLR type mapped on the entity by this serializer.</typeparam>
/// <typeparam name="TStorage">The CLR type being used for storage this serializer.</typeparam>
internal class NullableValueConverterSerializer<TActual, TStorage> : ValueConverterSerializer<TActual, TStorage>, INullableSerializer
{
    /// <summary>
    /// Create a new instance of <see cref="NullableValueConverterSerializer{TActual,TStorage}"/>.
    /// </summary>
    /// <param name="valueConverter">The <see cref="ValueConverter{TModel,TProvider}"/> provided by EF.</param>
    /// <param name="storageSerializer">The <see cref="IBsonSerializer"/> for the underlying storage.</param>
    public NullableValueConverterSerializer(
        ValueConverter<TActual, TStorage> valueConverter,
        IBsonSerializer<TStorage> storageSerializer)
        :base(valueConverter, storageSerializer)
    {
    }

    public IBsonSerializer ValueSerializer
    {
        get
        {
            var nonNullableClrType = typeof(TActual).UnwrapNullableType();
            var nonNullableUnderlyingType = typeof(TStorage).UnwrapNullableType();

            var valueConverterSerializerType = typeof(ValueConverterSerializer<,>)
                .MakeGenericType(nonNullableClrType, nonNullableUnderlyingType);

            var storageSerializer = typeof(TStorage).IsNullableValueType() ? ((INullableSerializer)_storageSerializer).ValueSerializer : _storageSerializer; ;

            return (IBsonSerializer?)Activator.CreateInstance(valueConverterSerializerType, _valueConverter, storageSerializer)
                         ?? throw new InvalidOperationException($"Unable to create serializer to handle '{valueConverterSerializerType.GetType().ShortDisplayName()}'");

        }
    }
}
