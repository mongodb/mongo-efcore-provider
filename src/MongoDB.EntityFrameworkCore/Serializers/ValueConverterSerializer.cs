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
using MongoDB.Bson.Serialization;

namespace MongoDB.EntityFrameworkCore.Serializers;

internal interface IDifferentStorageType
{
    public Type StorageType { get; }
}

internal class ValueConverterSerializer<TActual, TStorage> : IBsonSerializer<TActual>, IDifferentStorageType
{
    private readonly ValueConverter<TActual, TStorage> _valueConverter;
    private readonly IBsonSerializer<TStorage> _storageSerializer;

    public ValueConverterSerializer(
        ValueConverter<TActual, TStorage> valueConverter,
        IBsonSerializer<TStorage> storageSerializer)
    {
        _valueConverter = valueConverter;
        _storageSerializer = storageSerializer;
    }

    public Type ValueType => typeof(TActual);

    public Type StorageType => typeof(TStorage);

    public TActual Deserialize(BsonDeserializationContext context, BsonDeserializationArgs args)
    {
        var storage = _storageSerializer.Deserialize(context, args);
        return _valueConverter.ConvertFromProviderTyped(storage);
    }

    public void Serialize(BsonSerializationContext context, BsonSerializationArgs args, TActual value)
    {
        var storage = _valueConverter.ConvertToProviderTyped(value);
        _storageSerializer.Serialize(context, args, storage);
    }

    object IBsonSerializer.Deserialize(BsonDeserializationContext context, BsonDeserializationArgs args)
        => Deserialize(context, args);

    void IBsonSerializer.Serialize(BsonSerializationContext context, BsonSerializationArgs args, object value)
        => Serialize(context, args, (TActual)value);
}
