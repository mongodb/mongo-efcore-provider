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
using System.Linq;
using Microsoft.EntityFrameworkCore.Metadata;

namespace MongoDB.EntityFrameworkCore.Extensions;

/// <summary>
/// Extension methods for getting MongoDB metadata from an <see cref="IModel"/>.
/// </summary>
public static class MongoModelExtensions
{
    /// <summary>
    /// Returns all the MongoDB collections that entity types in the model are mapped to.
    /// </summary>
    /// <param name="model">The EF Core <see cref="IModel"/></param>
    /// <returns>All the MongoDB collections that are mapped in the given model.</returns>
    public static IReadOnlyList<string> GetCollectionNames(this IModel model)
    {
        var collectionNames = new List<string>();
        foreach (var entityType in model.GetEntityTypes().Where(e => e.IsDocumentRoot()))
        {
            var collectionName = entityType.GetCollectionName();
            if (!collectionNames.Contains(collectionName, StringComparer.Ordinal))
            {
                collectionNames.Add(collectionName);
            }
        }
        return collectionNames;
    }
}
