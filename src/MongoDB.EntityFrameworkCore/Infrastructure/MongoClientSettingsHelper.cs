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
using MongoDB.Bson;
using MongoDB.Driver;

namespace MongoDB.EntityFrameworkCore.Infrastructure;

internal static class MongoClientSettingsHelper
{
    internal static bool HasMongoClientOptions(MongoOptionsExtension? options) =>
        options?.CryptExtraOptions != null ||
        options?.CryptProvider != null ||
        options?.CryptProviderPath != null ||
        options?.KeyVaultNamespace != null ||
        options?.KmsProviders != null;

    /// <summary>
    /// Resolves the database name the context will actually use, which may come from either the
    /// <see cref="MongoOptionsExtension.DatabaseName"/> option or the path of the connection string.
    /// </summary>
    internal static string? ResolveDatabaseName(MongoOptionsExtension? options)
    {
        if (options?.DatabaseName != null)
        {
            return options.DatabaseName;
        }

        // MongoUrl.Create caches the parsed instance, so if the connection string was already parsed elsewhere
        // (e.g. by MongoClientSettings.FromConnectionString in CreateSettings) this call is free. A malformed
        // connection string throws here, wherever it is first parsed.
        return options?.ConnectionString == null ? null : MongoUrl.Create(options.ConnectionString).DatabaseName;
    }

    internal static MongoClientSettings CreateSettings(MongoOptionsExtension? options, Dictionary<string, BsonDocument>? queryableEncryptionSchema)
    {
        var clientSettings = options?.ConnectionString != null
            ? MongoClientSettings.FromConnectionString(options.ConnectionString)
            : options?.ClientSettings?.Clone();

        if (clientSettings == null)
        {
            throw new InvalidOperationException(
                "Unable to create or obtain a MongoClient. Either provide ClientSettings, a ConnectionString, or a " +
                "MongoClient via the DbContextOptions, or register an implementation of IMongoClient with the ServiceProvider.");
        }

        var autoEncryptionExtraOptions = options?.CryptProvider switch
        {
            CryptProvider.AutoEncryptSharedLibrary => ExtraOptionsForCryptShared(options.CryptProviderPath!),
            CryptProvider.Mongocryptd => ExtraOptionsForMongocryptd(options.CryptProviderPath!),
            _ => new Dictionary<string, object>()
        };

        ApplyOptions(autoEncryptionExtraOptions, clientSettings.AutoEncryptionOptions?.ExtraOptions);
        ApplyOptions(autoEncryptionExtraOptions, options?.CryptExtraOptions);

        var usesEncryption = queryableEncryptionSchema?.Count > 0 || options?.CryptProvider != null;

        // EncryptedFieldsMap is keyed by full namespace, so the database name must be the one the context will
        // actually use - it may only be present in the connection string. Keying against a null database name
        // produces namespaces that match no collection, silently storing declared-encrypted fields as plaintext.
        var databaseName = ResolveDatabaseName(options);
        if (databaseName == null && queryableEncryptionSchema?.Count > 0)
        {
            throw new InvalidOperationException(
                "No database name could be determined for the Queryable Encryption schema. Either specify a database name " +
                "via DbContextOptions or include one in the connection string.");
        }

        var keyVaultNamespace = clientSettings.AutoEncryptionOptions?.KeyVaultNamespace ?? options?.KeyVaultNamespace;
        if (keyVaultNamespace == null && usesEncryption)
        {
            throw new InvalidOperationException(
                "No KeyVaultNamespace specified for encryption. Either specify it via DbContextOptions or MongoClientSettings.");
        }

        var kmsProviders = clientSettings.AutoEncryptionOptions?.KmsProviders ?? options?.KmsProviders;
        if (kmsProviders == null && usesEncryption)
        {
            throw new InvalidOperationException(
                "No KmsProviders specified for encryption. Either specify it via DbContextOptions or MongoClientSettings.");
        }

        if (usesEncryption)
        {
            var existingAutoEncryptionOptions = clientSettings.AutoEncryptionOptions;
            var encryptedFieldsMap = MergeEncryptedFieldsMap(
                existingAutoEncryptionOptions?.EncryptedFieldsMap, queryableEncryptionSchema, databaseName);

            clientSettings.AutoEncryptionOptions = existingAutoEncryptionOptions != null
                ? existingAutoEncryptionOptions.With(
                    keyVaultNamespace: new Optional<CollectionNamespace>(keyVaultNamespace!),
                    kmsProviders: new Optional<IReadOnlyDictionary<string, IReadOnlyDictionary<string, object>>>(kmsProviders!),
                    extraOptions: new Optional<IReadOnlyDictionary<string, object>>(autoEncryptionExtraOptions),
                    encryptedFieldsMap: new Optional<IReadOnlyDictionary<string, BsonDocument>>(encryptedFieldsMap!))
                : new AutoEncryptionOptions(
                    keyVaultNamespace!,
                    kmsProviders!,
                    encryptedFieldsMap: new Optional<IReadOnlyDictionary<string, BsonDocument>>(encryptedFieldsMap!),
                    extraOptions: new Optional<IReadOnlyDictionary<string, object>>(autoEncryptionExtraOptions));
        }

        return clientSettings;
    }

    private static IReadOnlyDictionary<string, BsonDocument>? MergeEncryptedFieldsMap(
        IReadOnlyDictionary<string, BsonDocument>? existingEncryptedFieldsMap,
        Dictionary<string, BsonDocument>? queryableEncryptionSchema,
        string? databaseName)
    {
        if (queryableEncryptionSchema is not { Count: > 0 })
        {
            return existingEncryptedFieldsMap;
        }

        var mergedEncryptedFieldsMap = existingEncryptedFieldsMap != null
            ? new Dictionary<string, BsonDocument>(existingEncryptedFieldsMap)
            : new Dictionary<string, BsonDocument>();

        foreach (var (collectionName, schema) in queryableEncryptionSchema)
        {
            mergedEncryptedFieldsMap[databaseName + "." + collectionName] = schema;
        }

        return mergedEncryptedFieldsMap;
    }

    private static void ApplyOptions(
        Dictionary<string, object> combinedOptions,
        IReadOnlyDictionary<string, object>? extraOptions)
    {
        if (extraOptions == null) return;

        foreach (var kvp in extraOptions)
        {
            combinedOptions[kvp.Key] = kvp.Value;
        }
    }

    private static Dictionary<string, object> ExtraOptionsForCryptShared(string cryptSharedLibPath) =>
        new() { { "cryptSharedLibPath", cryptSharedLibPath }, { "cryptSharedLibRequired", true } };

    private static Dictionary<string, object> ExtraOptionsForMongocryptd(string mongocryptdSpawnPath) =>
        new() { { "mongocryptdSpawnPath", mongocryptdSpawnPath } };
}
