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
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;

namespace MongoDB.EntityFrameworkCore.Serializers;

/// <summary>
/// Provides the interface between the EFCore <see cref="ValueConverter"/>
/// and the MongoDB LINQ provider's <see cref="IBsonDocumentSerializer"/> interface.
/// </summary>
/// <typeparam name="TActual">The CLR type mapped on the entity by this serializer.</typeparam>
/// <typeparam name="TStorage">The CLR type being used for storage this serializer.</typeparam>
internal class ValueConverterSerializer<TActual, TStorage> : IBsonSerializer<TActual>
{
    protected readonly ValueConverter<TActual, TStorage> _valueConverter;
    protected readonly IBsonSerializer<TStorage> _storageSerializer;

    /// <summary>
    /// Create a new instance of <see cref="ValueConverterSerializer{TActual,TStorage}"/>.
    /// </summary>
    /// <param name="valueConverter">The <see cref="ValueConverter{TModel,TProvider}"/> provided by EF.</param>
    /// <param name="storageSerializer">The <see cref="IBsonSerializer"/> for the underlying storage.</param>
    public ValueConverterSerializer(
        ValueConverter<TActual, TStorage> valueConverter,
        IBsonSerializer<TStorage> storageSerializer)
    {
        ArgumentNullException.ThrowIfNull(valueConverter);
        ArgumentNullException.ThrowIfNull(storageSerializer);

        _valueConverter = valueConverter;
        _storageSerializer = storageSerializer;
    }

    /// <inheritdoc />
    public Type ValueType => typeof(TActual);

    /// <inheritdoc />
    public TActual Deserialize(BsonDeserializationContext context, BsonDeserializationArgs args)
        => _valueConverter.ConvertFromProviderTyped(_storageSerializer.Deserialize(context, args));

    /// <inheritdoc />
    public void Serialize(BsonSerializationContext context, BsonSerializationArgs args, TActual value)
        => SerializeValueOrNull(context, args, value);

    /// <inheritdoc />
    object IBsonSerializer.Deserialize(BsonDeserializationContext context, BsonDeserializationArgs args)
        => Deserialize(context, args)!;

    /// <inheritdoc />
    void IBsonSerializer.Serialize(BsonSerializationContext context, BsonSerializationArgs args, object? value)
        => SerializeValueOrNull(context, args, value);

    private void SerializeValueOrNull(BsonSerializationContext context, BsonSerializationArgs args, object? value)
    {
        if (value == null)
        {
            BsonNullSerializer.Instance.Serialize(context, args, BsonNull.Value);
        }
        else
        {
            _storageSerializer.Serialize(context, args, _valueConverter.ConvertToProviderTyped((TActual)value));
        }
    }
}
