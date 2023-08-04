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
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking.Internal;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Update;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.Serializers;

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
        var updates = ConvertUpdateEntriesToMongoUpdates(entries);
        return (int)SaveMongoUpdates(updates);
    }

    /// <summary>
    /// Save all the changes detected in the entities collection to the underlying MongoDB database asynchronously.
    /// </summary>
    /// <param name="entries">A list of <see cref="IUpdateEntry"/> indicating which changes to process.</param>
    /// <param name="cancellationToken">A <see cref="CancellationToken"/> that might be cancelled during the save.</param>
    /// <returns>Task that when resolved contains the number of documents affected during saving changes.</returns>
    public override async Task<int> SaveChangesAsync(IList<IUpdateEntry> entries, CancellationToken cancellationToken = default)
    {
        var updates = ConvertUpdateEntriesToMongoUpdates(entries);
        return (int)await SaveMongoUpdatesAsync(updates, cancellationToken).ConfigureAwait(false);
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
                if (root.EntityState == EntityState.Unchanged)
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
    /// Get the unique _id for a given <see cref="IUpdateEntry"/>.
    /// </summary>
    /// <param name="entry">The <see cref="IUpdateEntry"/> to obtain the _id for.</param>
    /// <returns>The _id for this entry expressed as an object.</returns>
    /// <exception cref="InvalidOperationException">If no property could be identified on the <see cref="IUpdateEntry"/> as holding the _id.</exception>
    private static object GetId(IUpdateEntry entry)
    {
        var idProperty = entry.EntityType.GetIdProperty();
        if (idProperty == null)
        {
            throw new InvalidOperationException(
                $"The entity type '{entry.EntityType.DisplayName()}' does not have a property mapped to the 'id' property in the database. Add a property mapped to 'id'");
        }

        return entry.GetCurrentProviderValue(idProperty)!; // TODO: use _id serializer from EntitySerializer
    }

    /// <summary>
    /// Convert a list of <see cref="IUpdateEntry"/> from EF core into <see cref="MongoUpdate"/> we
    /// can send to MongoDB.
    /// </summary>
    /// <param name="entries">The list of <see cref="IUpdateEntry"/> to process from EF Core.</param>
    /// <returns>The enumerable <see cref="MongoUpdate"/> sequence that will be sent to MongoDB.</returns>
    private IEnumerable<MongoUpdate> ConvertUpdateEntriesToMongoUpdates(IList<IUpdateEntry> entries)
    {
        return
            GetAllChangedRootEntries(entries)
                .Select(ConvertUpdateEntryToMongoUpdate)
                .OfType<MongoUpdate>();
    }

    private MongoUpdate? ConvertUpdateEntryToMongoUpdate(IUpdateEntry entry)
    {
        string collectionName = entry.EntityType.GetCollectionName();
        string id = (string)GetId(entry); // TODO: handle other _id types besides string

        var state = entry.EntityState;

        // This entry may share its identity with another that it is replacing or being
        // replaced by. If this one is the deleted side do nothing, if it is the replacement
        // then treat it as a database update.
        if (entry.SharedIdentityEntry != null)
        {
            if (state == EntityState.Deleted) return null;
            if (state == EntityState.Added) state = EntityState.Modified;
        }

        return state switch
        {
            EntityState.Added => ConvertAddedEntryToMongoUpdate(collectionName, id, entry),
            EntityState.Deleted => ConvertDeletedEntryToMongoUpdate(collectionName, id, entry),
            EntityState.Detached => null,
            EntityState.Modified => ConvertModifiedEntryToMongoUpdate(collectionName, id, entry),
            EntityState.Unchanged => null,
            _ => throw new NotSupportedException($"Unexpected entity state: {entry.EntityState}.")
        };
    }

    private static MongoUpdate ConvertAddedEntryToMongoUpdate(string collectionName, string id, IUpdateEntry entry)
    {
        var entityType = entry.EntityType;
        var entitySerializer = (IBsonDocumentSerializer)EntitySerializer.Create(entityType);

        var document = new BsonDocument();
        using (var writer = new BsonDocumentWriter(document))
        {
            writer.WriteStartDocument();

            foreach (var property in entityType.GetProperties())
            {
                var propertyValue = entry.GetCurrentValue(property);
                var propertySerializationInfo = GetPropertySerializationInfo(entitySerializer, property);
                var elementName = propertySerializationInfo.ElementName;
                var propertySerializer = propertySerializationInfo.Serializer;                   
                var context = BsonSerializationContext.CreateRoot(writer);

                writer.WriteName(elementName);
                propertySerializer.Serialize(context, propertyValue);
            }

            writer.WriteEndDocument();
        }

        var model = new InsertOneModel<BsonDocument>(document);
        return new MongoUpdate(collectionName, model);

        static BsonSerializationInfo GetPropertySerializationInfo(IBsonDocumentSerializer entitySerializer, IReadOnlyProperty property)
        {
            if (entitySerializer.TryGetMemberSerializationInfo(property.Name, out var serializationInfo))
            {
                return serializationInfo;
            }

            throw new InvalidOperationException($"Unable to get serialization info for property: {property.Name}.");
        }
    }

    private static MongoUpdate ConvertDeletedEntryToMongoUpdate(string collectionName, string id, IUpdateEntry entry)
    {
        var filter = new BsonDocument("_id", id);
        var model = new DeleteOneModel<BsonDocument>(filter);
        return new MongoUpdate(collectionName, model);
    }

    private static MongoUpdate ConvertModifiedEntryToMongoUpdate(string collectionName, string id, IUpdateEntry entry)
    {
        throw new NotImplementedException(); // EF-15
    }

    private long SaveMongoUpdates(IEnumerable<MongoUpdate> updates)
    {
        var database = _mongoClient.Database;
        var client = database.Client;
        using var session = client.StartSession();
        return SaveMongoUpdates(session, database, updates);
    }

    private static long SaveMongoUpdates(
        IClientSessionHandle session,
        IMongoDatabase database,
        IEnumerable<MongoUpdate> updates)
    {
        long documentsAffected = 0;
        foreach (var batch in BatchUpdatesByCollection(updates))
        {
            var collection = database.GetCollection<BsonDocument>(batch.CollectionName);
            var result = collection.BulkWrite(session, batch.Models);
            documentsAffected += result.ModifiedCount;
        }

        return documentsAffected;
    }

    private async Task<long> SaveMongoUpdatesAsync(IEnumerable<MongoUpdate> updates, CancellationToken cancellationToken)
    {
        var database = _mongoClient.Database;
        var client = database.Client;
        using var session = await client.StartSessionAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        return await SaveMongoUpdatesAsync(session, database, updates, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<long> SaveMongoUpdatesAsync(
        IClientSessionHandle session,
        IMongoDatabase database,
        IEnumerable<MongoUpdate> updates,
        CancellationToken cancellationToken)
    {
        long documentsAffected = 0;
        foreach (var batch in BatchUpdatesByCollection(updates))
        {
            var collection = database.GetCollection<BsonDocument>(batch.CollectionName);
            var result = await collection.BulkWriteAsync(session, batch.Models, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            documentsAffected += result.ModifiedCount;
        }

        return documentsAffected;
    }

    private static IEnumerable<MongoUpdateBatch> BatchUpdatesByCollection(IEnumerable<MongoUpdate> updates)
    {
        MongoUpdateBatch? batch = null;
        foreach (var update in updates)
        {
            if (batch == null)
            {
                batch = MongoUpdateBatch.Create(update);
            }
            else
            {
                if (batch.CollectionName == update.CollectionName)
                {
                    batch.Models.Add(update.Model);
                }
                else
                {
                    yield return batch;
                    batch = MongoUpdateBatch.Create(update);
                }
            }
        }

        if (batch != null)
        {
            yield return batch;
        }
    }

    private class MongoUpdate
    {
        public MongoUpdate(
            string collectionName,
            WriteModel<BsonDocument> model)
        {
            CollectionName = collectionName;
            Model = model;
        }

        public string CollectionName { get; }
        public WriteModel<BsonDocument> Model { get; }
    }

    private class MongoUpdateBatch
    {
        public static MongoUpdateBatch Create(MongoUpdate update)
        {
            return new MongoUpdateBatch(update.CollectionName, new List<WriteModel<BsonDocument>> {update.Model});
        }

        private MongoUpdateBatch(
            string collectionName,
            List<WriteModel<BsonDocument>> models)
        {
            CollectionName = collectionName;
            Models = models;
        }

        public string CollectionName { get; }
        public List<WriteModel<BsonDocument>> Models { get; }
    }
}
