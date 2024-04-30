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
using System.Collections.Concurrent;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using MongoDB.Bson;

namespace MongoDB.EntityFrameworkCore.Storage.ValueConversion;

/// <summary>
/// A <see cref="ValueConverterSelector"/> that adds support for MongoDB-specific value converters.
/// </summary>
public class MongoValueConverterSelector : ValueConverterSelector
{
    private readonly ConcurrentDictionary<(Type ModelClrType, Type ProviderClrType), ValueConverterInfo> _mongoConverters = new();

    /// <summary>
    /// Creates a new instance of <see cref="MongoValueConverterSelector"/>.
    /// </summary>
    /// <param name="dependencies">Parameter object containing dependencies for this service.</param>
    public MongoValueConverterSelector(ValueConverterSelectorDependencies dependencies)
        : base(dependencies)
    {
    }

    /// <inheritdoc />
    public override IEnumerable<ValueConverterInfo> Select(
        Type modelClrType,
        Type? providerClrType = null)
    {
        if (modelClrType == typeof(ObjectId))
        {
            if (providerClrType == null
                || providerClrType == typeof(string))
            {
                yield return _mongoConverters.GetOrAdd(
                    (modelClrType, typeof(string)),
                    _ => ObjectIdToStringConverter.DefaultInfo);
            }
        }
        else if (modelClrType == typeof(string))
        {
            if (providerClrType == typeof(ObjectId))
            {
                yield return _mongoConverters.GetOrAdd(
                    (modelClrType, typeof(ObjectId)),
                    _ => StringToObjectIdConverter.DefaultInfo);
            }
        }

        foreach (var converter in base.Select(modelClrType, providerClrType))
        {
            yield return converter;
        }
    }
}
