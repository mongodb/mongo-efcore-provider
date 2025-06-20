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
using BsonSerializationArgs = MongoDB.Bson.Serialization.BsonSerializationArgs;
using BsonSerializationContext = MongoDB.Bson.Serialization.BsonSerializationContext;

namespace MongoDB.EntityFrameworkCore.Infrastructure;

internal static class QueryableEncryptionSchemaGenerator
{
    public static Dictionary<string, BsonDocument> GenerateSchemas(IReadOnlyModel model)
    {
        var modelEncryptionDataKeyId = GetEncryptionDataKeyId(model.FindAnnotation(MongoAnnotationNames.EncryptionDataKeyId));

        var schemas = new Dictionary<string, BsonDocument>();

        foreach (var entityType in model.GetEntityTypes())
        {
            var fields = GenerateSchemaFields(entityType, modelEncryptionDataKeyId);
            if (fields.Count > 0)
            {
                schemas.Add(entityType.GetCollectionName(), new BsonDocument { { "fields", fields } });
            }
        }

        return schemas;
    }

    private static BsonArray GenerateSchemaFields(IReadOnlyEntityType entityType, Guid? modelEncryptionDataKeyId)
    {
        var entityEncryptionDataKeyId = GetEncryptionDataKeyId(entityType.FindAnnotation(MongoAnnotationNames.EncryptionDataKeyId));

        var fields = new BsonArray();

        foreach (var property in entityType.GetProperties())
        {
            var encryptionQueryType = property.FindAnnotation(MongoAnnotationNames.QueryableEncryptionType);
            if (encryptionQueryType != null)
            {
                var fieldSchema = GenerateSchemaField(property, entityEncryptionDataKeyId ?? modelEncryptionDataKeyId);
                fields.Add(BsonDocument.Create(fieldSchema));
            }
        }

        return fields;
    }

    private static BsonDocument GenerateSchemaField(IReadOnlyProperty property, Guid? encryptionDataKeyId)
    {
        var propertyEncryptionKeyId =
            GetEncryptionDataKeyId(property.FindAnnotation(MongoAnnotationNames.EncryptionDataKeyId)) ?? encryptionDataKeyId;
        if (propertyEncryptionKeyId is null)
        {
            throw new InvalidOperationException(
                $"Property '{property.Name}' has no encryption data key id available directly, on the entity or at model level.");
        }

        var bsonType = BsonTypeHelper.BsonTypeToString(BsonTypeHelper.GetBsonType(property));

        var fieldSchema = new BsonDocument
        {
            { "path", property.GetElementName() },
            { "keyId", new BsonBinaryData(propertyEncryptionKeyId.Value, GuidRepresentation.Standard) },
            { "bsonType", bsonType }
        };

        var queryTypeAnnotation = property.FindAnnotation(MongoAnnotationNames.QueryableEncryptionType);

        switch (queryTypeAnnotation?.Value)
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

    private static Guid? GetEncryptionDataKeyId(IAnnotation? annotation)
    {
        if (annotation?.Value is null) return null;

        return annotation.Value switch
        {
            Guid guid => guid,
            string str => Guid.Parse(str),
            _ => throw new InvalidOperationException(
                $"Unsupported EncryptionKeyId type '{annotation.Value.GetType().ShortDisplayName()}'.")
        };
    }
}
