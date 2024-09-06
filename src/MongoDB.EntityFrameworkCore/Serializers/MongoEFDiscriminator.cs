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
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization.Conventions;

namespace MongoDB.EntityFrameworkCore.Serializers;

/// <summary>
/// Provides a bridge between EF Core and the MongoDB C# driver for handling discriminator values.
/// </summary>
/// <param name="entityType">The <see cref="IReadOnlyEntityType"/> entity that forms part of the hierarchy.</param>
internal class MongoEFDiscriminator(IReadOnlyEntityType entityType)
    : IDiscriminatorConvention
{
    public Type GetActualType(IBsonReader bsonReader, Type nominalType)
        => throw new NotImplementedException();

    public BsonValue GetDiscriminator(Type nominalType, Type actualType)
    {
        var childEntityType = entityType.Model.FindEntityType(actualType)
            ?? throw new InvalidOperationException($"Entity type '{actualType.ShortDisplayName()}' not found in model.");
        return BsonValue.Create(childEntityType.GetDiscriminatorValue());
    }

    public string ElementName { get; } = entityType.FindDiscriminatorProperty()?.GetElementName() ?? "_t";
}
