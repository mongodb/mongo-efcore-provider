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
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking.Internal;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Update;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.Extensions;

namespace MongoDB.EntityFrameworkCore.Storage;

/// <summary>
/// Provides database-level operations against the underlying <see cref="IMongoClientWrapper"/>.
/// </summary>
public class MongoDatabaseWrapper : Database
{
    private readonly IMongoClientWrapper _mongoClient;
    private readonly bool _sensitiveLoggingEnabled;

    /// <summary>
    /// Creates a <see cref="MongoDatabaseWrapper"/> with the required dependencies, client wrapper and logging options.
    /// </summary>
    /// <param name="dependencies">The <see cref="DatabaseDependencies"/> this object should use.</param>
    /// <param name="mongoClient">The <see cref="IMongoClientWrapper"/> this should use to interact with MongoDB.</param>
    /// <param name="loggingOptions">The <see cref="ILoggingOptions"/> that specify how this class should log failures, warnings and informational messages.</param>
    public MongoDatabaseWrapper(
        DatabaseDependencies dependencies,
        IMongoClientWrapper mongoClient,
        ILoggingOptions loggingOptions)
        : base(dependencies)
    {
        _mongoClient = mongoClient;

        if (loggingOptions.IsSensitiveDataLoggingEnabled)
        {
            _sensitiveLoggingEnabled = true;
        }
    }

    /// <summary>
    /// Save all the changes detected in the entities collection to the underlying MongoDB database.
    /// </summary>
    /// <param name="entries">A list of <see cref="IUpdateEntry"/> indicating which changes to process.</param>
    /// <returns>Number of documents affected during saving changes.</returns>
    public override int SaveChanges(IList<IUpdateEntry> entries)
    {
        int docsAffected = 0;

        // TODO: Consider splitting the changes up by type and using the Mongo bulk APIs instead

        foreach (var entry in GetAllChangedRootEntries(entries))
        {
            // TODO: Exception handling
            if (Save(entry))
            {
                docsAffected++;
            }
        }

        return docsAffected;
    }

    /// <summary>
    /// Save all the changes detected in the entities collection to the underlying MongoDB database asynchronously.
    /// </summary>
    /// <param name="entries">A list of <see cref="IUpdateEntry"/> indicating which changes to process.</param>
    /// <param name="cancellationToken">A <see cref="CancellationToken"/> that might be cancelled during the save.</param>
    /// <returns>Task that when resolved contains the number of documents affected during saving changes.</returns>
    public override async Task<int> SaveChangesAsync(IList<IUpdateEntry> entries, CancellationToken cancellationToken = default)
    {
        int docsAffected = 0;

        // TODO: Consider splitting the changes up by type and using the Mongo bulk APIs instead

        foreach (var entry in GetAllChangedRootEntries(entries))
        {
            // TODO: Exception handling
            if (await SaveAsync(entry, cancellationToken))
            {
                docsAffected++;
            }
        }

        return docsAffected;
    }

    /// <summary>
    /// We only care about updating root entities as non-root/owned entities must be contained within one.
    /// Convert the list of entries modified by EF Core into a list of modified root entries that were either
    /// directly modified themselves or indirectly modified by one of the entries they own.
    /// </summary>
    /// <param name="entries">The list of modified <see cref="IUpdateEntry"/> as determined by EF Core.</param>
    /// <returns>The actual list of changed root entities as required by MongoDB.</returns>
    private static HashSet<IUpdateEntry> GetAllChangedRootEntries(IList<IUpdateEntry> entries)
    {
        var changedRootEntries = new HashSet<IUpdateEntry>(entries);
        foreach (var entry in entries)
        {
            if (!entry.EntityType.IsDocumentRoot())
            {
                var root = GetRootEntry((InternalEntityEntry)entry);
                if (root.EntityState != EntityState.Unchanged)
                {
                    root.EntityState = EntityState.Modified;
                }

                changedRootEntries.Remove(entry);
                changedRootEntries.Add(root);
            }
        }

        return changedRootEntries;
    }

    private static IUpdateEntry GetRootEntry(InternalEntityEntry entry)
    {
        while (true)
        {
            var stateManager = entry.StateManager;
            var ownership = entry.EntityType.FindOwnership()!;
            var principal = stateManager.FindPrincipal(entry, ownership);
            if (principal == null)
            {
                throw new InvalidOperationException(
                    $"The entity of type '{entry.EntityType.DisplayName()}' is mapped as a part of the document mapped to '{ownership.PrincipalEntityType.DisplayName()}', but there is no tracked entity of this type with the corresponding key value.");
            }

            if (principal.EntityType.IsDocumentRoot()) return principal;
            entry = principal;
        }
    }

    /// <summary>
    /// Save an individual <see cref="IUpdateEntry"/> synchronously according to its <see cref="EntityState"/>.
    /// </summary>
    /// <param name="entry">The entry to save.</param>
    /// <returns><see langref="true"/> if the entry was successfully saved, <see langref="false"/> if not.</returns>
    private bool Save(IUpdateEntry entry)
    {
        string id = GetId(entry);
        string collectionName = entry.EntityType.GetCollectionName();

        var state = entry.EntityState;
        // TODO: Consider SharedIdentityEntry

        return state switch
        {
            EntityState.Deleted => _mongoClient.Database.GetCollection<BsonDocument>(collectionName).DeleteOne(b => false /* TODO: Add _id filter */).IsAcknowledged,
            EntityState.Added => throw new NotSupportedException("Adding entities not yet supported."),
            EntityState.Modified => throw new NotSupportedException("Modifying entities not yet supported."),
            _ => false
        };
    }

    /// <summary>
    /// Save an individual <see cref="IUpdateEntry"/> asynchronously according to its <see cref="EntityState"/>.
    /// </summary>
    /// <param name="entry">The entry to save.</param>
    /// <param name="cancellationToken">A <see cref="CancellationToken"/> that can be used to cancel the asynchronous operation.</param>
    /// <returns>A task that when resolved contains <see langref="true"/> if the entry was successfully saved, <see langref="false"/> if not.</returns>
    private async Task<bool> SaveAsync(IUpdateEntry entry, CancellationToken cancellationToken)
    {
        string id = GetId(entry);
        string collectionName = entry.EntityType.GetCollectionName();

        var state = entry.EntityState;
        // TODO: Consider SharedIdentityEntry

        return state switch
        {
            EntityState.Deleted
                => (await _mongoClient.Database.GetCollection<BsonDocument>(collectionName).DeleteOneAsync(b => false /* TODO: Add _id filter */, cancellationToken)).IsAcknowledged,
            EntityState.Added => throw new NotSupportedException("Adding entities not yet supported."),
            EntityState.Modified => throw new NotSupportedException("Modifying entities not yet supported."),
            _ => false
        };
    }

    /// <summary>
    /// Get the unique _id for a given <see cref="IUpdateEntry"/>.
    /// </summary>
    /// <param name="entry">The <see cref="IUpdateEntry"/> to obtain the _id for.</param>
    /// <returns>The _id for this entry expressed as a string.</returns>
    /// <exception cref="InvalidOperationException">If no property could be identified on the <see cref="IUpdateEntry"/> as holding the _id.</exception>
    private static string GetId(IUpdateEntry entry)
    {
        var idProperty = entry.EntityType.GetIdProperty();
        if (idProperty == null)
        {
            throw new InvalidOperationException(
                $"The entity type '{entry.EntityType.DisplayName()}' does not have a property mapped to the 'id' property in the database. Add a property mapped to 'id'");
        }

        return entry.GetCurrentProviderValue(idProperty)! switch
        {
            string idAsString => idAsString,
            ObjectId idAsObjectId => idAsObjectId.ToString(),
            _ => throw new NotSupportedException($"Unknown type for _id property on entity type ${entry.EntityType.DisplayName()}")
        };
    }
}
