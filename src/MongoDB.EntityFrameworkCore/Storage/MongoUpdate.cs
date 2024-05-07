﻿/* Copyright 2023-present MongoDB Inc.
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
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking.Internal;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Update;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.Serializers;

namespace MongoDB.EntityFrameworkCore.Storage;

/// <summary>
/// Represents an update to a MongoDB database by specifying the collection
/// name and the appropriate document inside a <see cref="WriteModel{BsonDocument}"/> which indicates
/// the type of operation.
/// </summary>
/// <param name="collectionName">The name of the collection this update applies to.</param>
/// <param name="model">The <see cref="WriteModel{BsonDocument}"/> containing the update.</param>
public class MongoUpdate(string collectionName, WriteModel<BsonDocument> model)
{
    /// <summary>
    /// The name of the collection this update applies to.
    /// </summary>
    public string CollectionName
    {
        get => collectionName;
    }

    /// <summary>
    /// The <see cref="WriteModel{BsonDocument}"/> that contains both the document
    /// being modified and indication of the type of update being performed.
    /// </summary>
    public WriteModel<BsonDocument> Model
    {
        get => model;
    }

    /// <summary>
    /// Create a enumeration of <see cref="MongoUpdate"/> from an enumeration of EF-supplied
    /// <see cref="IUpdateEntry"/>.
    /// </summary>
    /// <param name="entries">The EF-supplied <see cref="IUpdateEntry"/> to process.</param>
    /// <returns>An enumeration of <see cref="MongoUpdate"/> that corresponds to these updates.</returns>
    public static IEnumerable<MongoUpdate> CreateAll(IEnumerable<IUpdateEntry> entries)
        => entries.Select(Create).OfType<MongoUpdate>();

    private static MongoUpdate? Create(IUpdateEntry entry)
        => entry.EntityState switch
        {
            EntityState.Added => ConvertAdded(entry),
            EntityState.Deleted => ConvertDeleted(entry),
            EntityState.Modified => ConvertModified(entry),
            EntityState.Detached => null,
            EntityState.Unchanged => null,
            _ => throw new NotSupportedException($"Unexpected entity state: {entry.EntityState}.")
        };

    private static MongoUpdate ConvertAdded(IUpdateEntry entry)
    {
        var document = new BsonDocument();
        using var writer = new BsonDocumentWriter(document);
        WriteEntity(writer, entry);

        var model = new InsertOneModel<BsonDocument>(document);
        return new MongoUpdate(entry.EntityType.GetCollectionName(), model);
    }

    private static MongoUpdate ConvertDeleted(IUpdateEntry entry)
    {
        var model = new DeleteOneModel<BsonDocument>(CreateIdFilter(entry));
        return new MongoUpdate(entry.EntityType.GetCollectionName(), model);
    }

    private static MongoUpdate ConvertModified(IUpdateEntry entry)
    {
        var document = new BsonDocument();
        using var writer = new BsonDocumentWriter(document);
        WriteEntity(writer, entry);

        var updateDocument = new BsonDocument("$set", document);
        var updateDefinition = new BsonDocumentUpdateDefinition<BsonDocument>(updateDocument);

        var model = new UpdateOneModel<BsonDocument>(CreateIdFilter(entry), updateDefinition);
        return new MongoUpdate(entry.EntityType.GetCollectionName(), model);
    }

    private static FilterDefinition<BsonDocument> CreateIdFilter(IUpdateEntry entry)
    {
        _ = entry.EntityType.FindPrimaryKey() ??
            throw new InvalidOperationException($"Cannot find the primary key for the entity: {entry.EntityType.Name}");

        var document = new BsonDocument();
        using var writer = new BsonDocumentWriter(document);
        WriteEntity(writer, entry, p => p.IsPrimaryKey());

        // MongoDB requires primary key named as "_id";
        return Builders<BsonDocument>.Filter.Eq("_id", document["_id"]);
    }

    private static void WriteEntity(IBsonWriter writer, IUpdateEntry entry, Func<IProperty, bool>? propertyFilter = null)
    {
        if (propertyFilter == null && entry.EntityState == EntityState.Modified)
        {
            propertyFilter = entry.IsModified;
        }

        AcceptTemporaryValues(entry);

        writer.WriteStartDocument();

        WriteKeyProperties(writer, entry);
        WriteNonKeyProperties(writer, entry, propertyFilter);

        foreach (var navigation in entry.EntityType.GetNavigations())
        {
            var fk = navigation.ForeignKey;
            if (!fk.IsOwnership
                || navigation.IsOnDependent
                || fk.DeclaringEntityType.IsDocumentRoot())
            {
                continue;
            }

            writer.WriteName(navigation.TargetEntityType.GetContainingElementName());
            var embeddedValue = entry.GetCurrentValue(navigation);

            if (embeddedValue == null)
            {
                writer.WriteNull();
            }
            else
            {
                if (navigation.IsCollection)
                {
                    // Set temporary ordinals if existing ones are invalid
                    SetTemporaryOrdinals(entry, fk, embeddedValue);

                    writer.WriteStartArray();
                    var ordinal = 1;
                    foreach (var dependent in (IEnumerable)embeddedValue)
                    {
                        var embeddedEntry =
                            ((InternalEntityEntry)entry).StateManager.TryGetEntry(dependent,
                                navigation.ForeignKey.DeclaringEntityType)!;

                        // Owned entities have a synthetic key based on order, apply that here
                        var ordinalKeyProperty = FindOrdinalKeyProperty(embeddedEntry.EntityType);
                        if (ordinalKeyProperty != null && embeddedEntry.HasTemporaryValue(ordinalKeyProperty))
                        {
                            embeddedEntry.SetStoreGeneratedValue(ordinalKeyProperty, ordinal);
                        }

                        WriteEntity(writer, embeddedEntry, _ => true);
                        ordinal++;
                    }

                    writer.WriteEndArray();
                }
                else
                {
                    var embeddedEntry =
                        ((InternalEntityEntry)entry).StateManager.TryGetEntry(embeddedValue,
                            navigation.ForeignKey.DeclaringEntityType)!;
                    WriteEntity(writer, embeddedEntry, _ => true);
                }
            }
        }

        writer.WriteEndDocument();
    }

    private static IProperty? FindOrdinalKeyProperty(IEntityType entityType)
        => entityType.FindPrimaryKey()!.Properties.FirstOrDefault(
            p => p.GetElementName().Length == 0 && p.IsOwnedCollectionShadowKey());

    private static void AcceptTemporaryValues(IUpdateEntry entry)
    {
        foreach (var property in entry.EntityType.GetProperties())
        {
            if (entry.HasTemporaryValue(property))
                entry.SetStoreGeneratedValue(property, entry.GetCurrentValue(property));
        }
    }

    private static void WriteKeyProperties(IBsonWriter writer, IUpdateEntry entry)
    {
        var keyProperties = entry.EntityType.FindPrimaryKey()?
            .Properties
            .Where(p => !p.IsShadowProperty() && p.GetElementName() != "").ToArray() ?? [];

        if (!keyProperties.Any()) return;

        var compoundKey = keyProperties.Length > 1;
        if (compoundKey)
        {
            writer.WriteName("_id");
            writer.WriteStartDocument();
        }

        foreach (var property in keyProperties)
        {
            var serializationInfo = SerializationHelper.GetPropertySerializationInfo(property);
            var elementName = serializationInfo.ElementPath?.Last() ?? serializationInfo.ElementName;
            writer.WriteName(elementName);
            var root = BsonSerializationContext.CreateRoot(writer);
            serializationInfo.Serializer.Serialize(root, entry.GetCurrentValue(property));
        }

        if (compoundKey)
        {
            writer.WriteEndDocument();
        }
    }

    internal static void WriteNonKeyProperties(IBsonWriter writer, IUpdateEntry entry, Func<IProperty, bool>? propertyFilter = null)
    {
        var properties = entry.EntityType.GetProperties()
            .Where(p => !p.IsShadowProperty() && !p.IsPrimaryKey() && p.GetElementName() != "")
            .Where(p => propertyFilter == null || propertyFilter(p))
            .ToArray();

        foreach (var property in properties)
        {
            var serializationInfo = SerializationHelper.GetPropertySerializationInfo(property);
            var elementName = serializationInfo.ElementPath?.Last() ?? serializationInfo.ElementName;
            writer.WriteName(elementName);
            var root = BsonSerializationContext.CreateRoot(writer);
            serializationInfo.Serializer.Serialize(root, entry.GetCurrentValue(property));
        }
    }

    private static void SetTemporaryOrdinals(
        IUpdateEntry entry,
        IForeignKey fk,
        object embeddedValue)
    {
        var embeddedOrdinal = 1;
        var ordinalKeyProperty = FindOrdinalKeyProperty(fk.DeclaringEntityType);
        if (ordinalKeyProperty == null) return;

        var stateManager = ((InternalEntityEntry)entry).StateManager;
        var shouldSetTemporaryKeys = false;
        foreach (var dependent in (IEnumerable)embeddedValue)
        {
            var embeddedEntry = stateManager.TryGetEntry(dependent, fk.DeclaringEntityType)!;

            if ((int)embeddedEntry.GetCurrentValue(ordinalKeyProperty)! != embeddedOrdinal
                && !embeddedEntry.HasTemporaryValue(ordinalKeyProperty))
            {
                // We have old persisted ordinals that are no longer valid
                // Set temporary ones to avoid key conflicts when creating new
                // non-temporary keys.
                shouldSetTemporaryKeys = true;
                break;
            }

            embeddedOrdinal++;
        }

        if (shouldSetTemporaryKeys)
        {
            var temporaryOrdinal = -1;
            foreach (var dependent in (IEnumerable)embeddedValue)
            {
                var embeddedEntry = stateManager.TryGetEntry(dependent, fk.DeclaringEntityType)!;
                embeddedEntry.SetTemporaryValue(ordinalKeyProperty, temporaryOrdinal, setModified: false);
                temporaryOrdinal--;
            }
        }
    }
}
