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
using MongoDB.Bson;

namespace MongoDB.EntityFrameworkCore.Infrastructure;

/// <summary>
/// Checks that a server and client schema are compatible for QueryableEncryption.
/// </summary>
internal static class QueryableEncryptionSchemaChecker
{
    private class BsonSchemaNameComparer : IEqualityComparer<BsonValue>
    {
        public bool Equals(BsonValue? server, BsonValue? client)
            => server != null && server["path"].Equals(client?["path"]);

        public int GetHashCode(BsonValue schema)
            => schema["path"].AsString.GetHashCode();

        public static readonly BsonSchemaNameComparer Instance = new();
    }

    private class BsonSchemaFullComparer : IEqualityComparer<BsonValue>
    {
        public bool Equals(BsonValue? server, BsonValue? client)
            => server != null && server["path"].Equals(client?["path"]) && server["bsonType"].Equals(client?["bsonType"]) &&
               (client["keyId"] == BsonNull.Value || server["keyId"].Equals(client["keyId"]));

        public int GetHashCode(BsonValue schema)
            => schema["path"].AsString.GetHashCode() ^ schema["bsonType"].AsString.GetHashCode() ^ schema["keyId"].GetHashCode();

        public static readonly BsonSchemaFullComparer Instance = new();
    }

    public static Dictionary<string, BsonValue[]> GetFieldsWithMissingDataKeyIds(Dictionary<string, BsonDocument> queryableEncryptionSchema)
        => queryableEncryptionSchema.Select(s => Tuple.Create(s.Key, GetFieldsWithMissingDataKeyIds(s.Value["fields"]))).Where(e => e.Item2.Any()).ToDictionary(e => e.Item1, e => e.Item2);

    public static BsonValue[] GetFieldsWithMissingDataKeyIds(BsonValue? encryptedFields)
        => encryptedFields.AsBsonArray.Where(f => f["keyId"] == BsonNull.Value || f["keyId"] == null).ToArray();

    /// <summary>
    /// Check that a server and client schema for Queryable Encryption is compatible, i.e. no encrypted fields are missing
    /// on either side and that the types and data key ids match (the client data key ids may also be Bson null)
    /// </summary>
    /// <param name="collectionName">The name of the collection that is being checked for compatibility.</param>
    /// <param name="serverEncryptedFields">The encryptedFields from the collection options on the server.</param>
    /// <param name="clientEncryptedFields">The encryptedFields from the client settings.</param>
    /// <exception cref="InvalidOperationException">If there is any incompatibility between the schemas.</exception>
    public static void CheckCompatibleSchemas(
        string collectionName,
        BsonDocument? serverEncryptedFields,
        BsonDocument? clientEncryptedFields)
    {
        var serverFields = serverEncryptedFields?.TryGetValue("fields", out var serverFieldsElement) == true
            ? serverFieldsElement.AsBsonArray
            : [];

        var clientFields = clientEncryptedFields?.TryGetValue("fields", out var clientFieldsElement) == true
            ? clientFieldsElement.AsBsonArray
            : [];

        var missingOnServer = clientFields.Except(serverFields, BsonSchemaNameComparer.Instance).ToArray();
        if (missingOnServer.Any())
            throw new InvalidOperationException(
                $"Collection '{collectionName}' is missing the following encrypted schema paths on the server: {ListPaths(missingOnServer)}.");

        var missingOnClient = serverFields.Except(clientFields, BsonSchemaNameComparer.Instance).ToArray();
        if (missingOnClient.Any())
            throw new InvalidOperationException(
                $"Collection '{collectionName}' is missing the following encrypted schema paths on the client: {ListPaths(missingOnClient)}.");

        var matched = serverFields
            .Join(clientFields, c => c["path"].AsString, s => s["path"].AsString, (c, s) => new[] { c, s }).ToArray();
        var mismatchedKeys = matched.Where(m => m[0]["keyId"] != m[1]["keyId"]).ToArray();
        if (mismatchedKeys.Any())
            throw new InvalidOperationException(
                $"Collection '{collectionName}' has mismatched keys for the following encrypted schema paths: {ListClientPaths(mismatchedKeys)}.");

        var mismatchedTypes = matched.Where(m => m[0]["bsonType"] != m[1]["bsonType"]).ToArray();
        if (mismatchedTypes.Any())
            throw new InvalidOperationException(
                $"Collection '{collectionName}' has mismatched types for the following encrypted schema paths: {ListClientPaths(mismatchedTypes)}.");

        string ListClientPaths(BsonValue[][] fields)
            => string.Join(", ", fields.Select(f => f[0]["path"].AsString));

        string ListPaths(BsonValue[] fields)
            => string.Join(", ", fields.Select(f => f["path"].AsString));
    }
}
