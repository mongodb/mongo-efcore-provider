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
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.Metadata;
using MongoDB.EntityFrameworkCore.Serializers;
using MongoDB.EntityFrameworkCore.Storage;

namespace MongoDB.EntityFrameworkCore.Infrastructure;

/// <summary>
/// Generates MongoDB Queryable Encryption schemas for the EF Core models.
/// </summary>
public static class QueryableEncryptionSchemaGenerator
{
    /// <summary>
    /// Generate a dictionary of Queryable Encryption schemas for a given EF Core model.
    /// </summary>
    /// <param name="model">The EF Core <see cref="IModel"/>.</param>
    /// <returns>A <see cref="Dictionary{TKey,TValue}"/> containing Queryable Encryption schemas keyed by collection name.</returns>
    /// <exception cref="ArgumentNullException">If the <paramref name="model"/> is null.</exception>
    public static Dictionary<string, BsonDocument> GenerateSchemas(IReadOnlyModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var schemas = new Dictionary<string, BsonDocument>();

        foreach (var entityType in model.GetEntityTypes().Where(e => e.IsDocumentRoot()))
        {
            var schema = GenerateSchema(entityType);
            if (schema["fields"].AsBsonArray.Count > 0)
            {
                schemas.Add(entityType.GetCollectionName(), schema);
            }
        }

        return schemas;
    }

    /// <summary>
    /// Generate an individual Queryable Encryption schema for the given entity type.
    /// </summary>
    /// <param name="entityType">The <see cref="IReadOnlyEntityType"/> to generate the schema for.</param>
    /// <returns>The <see cref="BsonDocument"/> containing the Queryable Encryption schema.</returns>
    /// <exception cref="ArgumentNullException">If the <paramref name="entityType"/> is null.</exception>
    public static BsonDocument GenerateSchema(IReadOnlyEntityType entityType)
    {
        ArgumentNullException.ThrowIfNull(entityType);

        var fields = new BsonArray();
        GenerateSchemaFields(entityType, fields, "");
        return new BsonDocument { { "fields", fields } };
    }

    private static void GenerateSchemaFields(IReadOnlyEntityType entityType, BsonArray fields, string prefix)
    {
        foreach (var property in entityType.GetProperties())
        {
            var encryptionQueryType = property.GetQueryableEncryptionType();
            if (encryptionQueryType != null)
            {
                var fieldSchema = GenerateSchemaField(property, prefix);
                fields.Add(BsonDocument.Create(fieldSchema));
            }
        }

        foreach (var targetEntityType in entityType.GetNavigations().Select(n => n.TargetEntityType))
        {
            if (targetEntityType.IsOwned())
            {
                var collectionName = targetEntityType.GetContainingElementName();
                GenerateSchemaFields(targetEntityType, fields, prefix + collectionName + ".");
            }
        }
    }

    private static BsonDocument GenerateSchemaField(IReadOnlyProperty property, string prefix)
    {
        var dataKeyId = GetEncryptionDataKeyId(property.FindAnnotation(MongoAnnotationNames.EncryptionDataKeyId));
        var fieldSchema = new BsonDocument
        {
            { "path", prefix + property.GetElementName() },
            { "keyId", dataKeyId != null ? new BsonBinaryData(dataKeyId.Value, GuidRepresentation.Standard) : BsonNull.Value },
            { "bsonType", BsonTypeHelper.BsonTypeToString(BsonTypeHelper.GetBsonType(property)) }
        };

        var queryTypeAnnotation = property.GetQueryableEncryptionType();
        switch (queryTypeAnnotation)
        {
            case null:
            case QueryableEncryptionType.NotQueryable:
                break;

            case QueryableEncryptionType.Equality:
                fieldSchema["queries"] = new BsonDocument { { "queryType", "equality" } };
                break;

            case QueryableEncryptionType.Range:
                fieldSchema["queries"] = GenerateRangeQuerySchema(property);
                break;
        }

        return fieldSchema;
    }

    private static BsonDocument GenerateRangeQuerySchema(IReadOnlyProperty property)
    {
        var rangeQuerySchema = new BsonDocument();

        using var writer = new BsonDocumentWriter(rangeQuerySchema);
        var context = BsonSerializationContext.CreateRoot(writer);
        var args = new BsonSerializationArgs();

        writer.WriteStartDocument();
        writer.WriteName("queryType");
        writer.WriteString("range");

        IBsonSerializer? serializer = null;

        if (property.FindAnnotation(MongoAnnotationNames.QueryableEncryptionRangeMin)?.Value is { } rangeMin)
        {
            writer.WriteName("min");
            serializer ??= BsonSerializerFactory.CreateTypeSerializer(property);
            serializer.Serialize(context, args, rangeMin);
        }

        if (property.FindAnnotation(MongoAnnotationNames.QueryableEncryptionRangeMax)?.Value is { } rangeMax)
        {
            writer.WriteName("max");
            serializer ??= BsonSerializerFactory.CreateTypeSerializer(property);
            serializer.Serialize(context, args, rangeMax);
        }

        writer.WriteEndDocument();

        return rangeQuerySchema;
    }

    private static Guid? GetEncryptionDataKeyId(IAnnotation? annotation) =>
        annotation?.Value switch
        {
            null => null,
            Guid guid => guid,
            string str => Guid.Parse(str),
            _ => throw new InvalidOperationException(
                $"Unsupported EncryptionDataKeyId type '{annotation.Value.GetType().ShortDisplayName()}'.")
        };
}
