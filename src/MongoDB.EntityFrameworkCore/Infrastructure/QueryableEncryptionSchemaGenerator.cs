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
    /// Generate a Queryable Encryption schema for the given entity type.
    /// </summary>
    /// <param name="entityType">The <see cref="IReadOnlyEntityType"/> to generate the schema for.</param>
    /// <returns>The <see cref="BsonDocument"/> containing the Queryable Encryption schema.</returns>
    /// <exception cref="ArgumentNullException">If the <paramref name="entityType"/> is null.</exception>
    private static BsonDocument GenerateSchema(IReadOnlyEntityType entityType)
    {
        ArgumentNullException.ThrowIfNull(entityType);

        var fields = new BsonArray();
        AddSchemaFields(entityType, fields, "");
        return new BsonDocument { { "fields", fields } };
    }

    private static void AddSchemaFields(IReadOnlyEntityType entityType, BsonArray fields, string prefix)
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

        foreach (var navigation in entityType.GetNavigations())
        {
            var targetEntityType = navigation.TargetEntityType;
            if (targetEntityType.IsOwned())
            {
                var navigationName = targetEntityType.GetContainingElementName();

                switch (navigation.ForeignKey.GetQueryableEncryptionType())
                {
                    case QueryableEncryptionType.Equality:
                    case QueryableEncryptionType.Range:
                        throw new NotSupportedException("Equality and Range queries are not supported for owned entities.");

                    case QueryableEncryptionType.NotQueryable:
                        var dataKeyId = navigation.ForeignKey.GetEncryptionDataKeyId();
                        var fieldSchema = new BsonDocument
                        {
                            {
                                "keyId", dataKeyId != null
                                    ? new BsonBinaryData(dataKeyId.Value, GuidRepresentation.Standard)
                                    : BsonNull.Value
                            },
                            { "path", prefix + navigationName },
                            { "bsonType", "object" }
                        };
                        AddSchemaFields(targetEntityType, fields, prefix + navigationName + ".");

                        fields.Add(BsonDocument.Create(fieldSchema));
                        break;

                    default:
                        AddSchemaFields(targetEntityType, fields, prefix + navigationName + ".");
                        break;
                }
            }
        }
    }

    private static BsonDocument GenerateSchemaField(IReadOnlyProperty property, string prefix)
    {
        var dataKeyId = property.GetEncryptionDataKeyId();
        var fieldSchema = new BsonDocument
        {
            { "keyId", dataKeyId != null ? new BsonBinaryData(dataKeyId.Value, GuidRepresentation.Standard) : BsonNull.Value },
            { "path", prefix + property.GetElementName() },
            { "bsonType", BsonTypeHelper.BsonTypeToString(BsonTypeHelper.GetBsonType(property)) }
        };

        var queryTypeAnnotation = property.GetQueryableEncryptionType();
        switch (queryTypeAnnotation)
        {
            case QueryableEncryptionType.NotQueryable:
                return fieldSchema;

            case QueryableEncryptionType.Equality:
                fieldSchema["queries"] = new BsonDocument { { "queryType", "equality" } };
                break;

            case QueryableEncryptionType.Range:
                fieldSchema["queries"] = GenerateRangeQuerySchema(property);
                break;
        }

        if (property.FindAnnotation(MongoAnnotationNames.QueryableEncryptionContention)?.Value is { } contention)
        {
            fieldSchema["queries"]["contention"] = BsonValue.Create(contention);
        }

        return fieldSchema;
    }

    private static BsonDocument GenerateRangeQuerySchema(IReadOnlyProperty property)
    {
        var rangeQuerySchema = new BsonDocument();

        using var writer = new BsonDocumentWriter(rangeQuerySchema);
        var context = BsonSerializationContext.CreateRoot(writer);
        var args = new BsonSerializationArgs();
        var propertyTypeSerializer = BsonSerializerFactory.CreateTypeSerializer(property);

        writer.WriteStartDocument();
        writer.WriteName("queryType");
        writer.WriteString("range");

        if (property.FindAnnotation(MongoAnnotationNames.QueryableEncryptionRangeMin)?.Value is { } rangeMin)
        {
            writer.WriteName("min");
            propertyTypeSerializer.Serialize(context, args, rangeMin);
        }

        if (property.FindAnnotation(MongoAnnotationNames.QueryableEncryptionRangeMax)?.Value is { } rangeMax)
        {
            writer.WriteName("max");
            propertyTypeSerializer.Serialize(context, args, rangeMax);
        }

        if (property.FindAnnotation(MongoAnnotationNames.QueryableEncryptionTrimFactor)?.Value is int trimFactor)
        {
            writer.WriteInt32("trimFactor", trimFactor);
        }

        if (property.FindAnnotation(MongoAnnotationNames.QueryableEncryptionSparsity)?.Value is int sparsity)
        {
            writer.WriteInt32("sparsity", sparsity);
        }

        if (property.FindAnnotation(MongoAnnotationNames.QueryableEncryptionPrecision)?.Value is int precision)
        {
            writer.WriteInt32("precision", precision);
        }

        writer.WriteEndDocument();

        return rangeQuerySchema;
    }
}
