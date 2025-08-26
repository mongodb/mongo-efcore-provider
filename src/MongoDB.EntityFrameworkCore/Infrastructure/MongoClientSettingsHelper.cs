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
            clientSettings.AutoEncryptionOptions = new AutoEncryptionOptions(
                keyVaultNamespace,
                kmsProviders,
                encryptedFieldsMap: queryableEncryptionSchema?.ToDictionary(d => options?.DatabaseName + "." + d.Key, d => d.Value),
                extraOptions: autoEncryptionExtraOptions);
        }

        return clientSettings;
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
