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
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.ValueGeneration;
using MongoDB.Bson;

namespace MongoDB.EntityFrameworkCore.ValueGeneration;

/// <summary>
/// Implements <see cref="ValueGeneratorSelector"/> adding temporary ID
/// functionality for owned entity collections.
/// </summary>
public class MongoValueGeneratorSelector : ValueGeneratorSelector
{
    /// <summary>
    /// Create a new <see cref="MongoValueGeneratorSelector"/>.
    /// </summary>
    /// <param name="dependencies">The <see cref="ValueGeneratorSelectorDependencies"/> to use.</param>
    public MongoValueGeneratorSelector(ValueGeneratorSelectorDependencies dependencies)
        : base(dependencies)
    {
    }

    /// <inheritdoc />
    protected override ValueGenerator? FindForType(IProperty property, ITypeBase typeBase, Type clrType)
    {
        // Required to ensure we generate unique IDs for owned entity collections
        if (typeBase.ContainingEntityType.IsOwned() && property.IsOwnedCollectionShadowKey())
        {
            return new TemporaryNumberValueGeneratorFactory().Create(property, typeBase);
        }

        // Allow ObjectId's to be automatically generated
        if (clrType == typeof(ObjectId))
        {
            return new ObjectIdValueGenerator();
        }

        // Allow strings stored as ObjectId's to be generated too
        if (clrType == typeof(string) && IsStoredAsObjectId(property))
        {
            return new StringObjectIdValueGenerator();
        }

        return base.FindForType(property, typeBase, clrType);
    }

    private static bool IsStoredAsObjectId(IProperty property)
        => property.GetValueConverter() is { } converter && converter.ModelClrType == typeof(ObjectId)
           || property.GetBsonRepresentation() is {BsonType: BsonType.ObjectId};
}
