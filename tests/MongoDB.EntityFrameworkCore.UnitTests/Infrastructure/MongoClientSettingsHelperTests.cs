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

using MongoDB.Bson;
using MongoDB.Driver;
using Microsoft.EntityFrameworkCore;
using MongoDB.EntityFrameworkCore.Infrastructure;


namespace MongoDB.EntityFrameworkCore.UnitTests.Infrastructure;

public static class MongoClientSettingsHelperTests
{
    [Fact]
    public static void CreateSettings_preserves_existing_AutoEncryptionOptions_when_applying_queryable_encryption_schema()
    {
        var keyVaultNamespace = CollectionNamespace.FromFullName("keyvault.datakeys");
        var kmsProviders = new Dictionary<string, IReadOnlyDictionary<string, object>>
        {
            ["local"] = new Dictionary<string, object> { ["key"] = new byte[96] }
        };
        var tlsOptions = new Dictionary<string, SslSettings> { ["local"] = new SslSettings() };
        var schemaMap = new Dictionary<string, BsonDocument> { ["db.schema"] = new BsonDocument() };
        var existingEncryptedFieldsMap = new Dictionary<string, BsonDocument> { ["db.other"] = new BsonDocument("fields", new BsonArray()) };

        var clientSettings = new MongoClientSettings
        {
            AutoEncryptionOptions = new AutoEncryptionOptions(
                keyVaultNamespace,
                kmsProviders,
                bypassAutoEncryption: true,
                bypassQueryAnalysis: true,
                tlsOptions: tlsOptions,
                schemaMap: schemaMap,
                encryptedFieldsMap: existingEncryptedFieldsMap)
        };

        var options = new MongoOptionsExtension()
            .WithClientSettings(clientSettings)
            .WithDatabaseName("db");

        var queryableEncryptionSchema = new Dictionary<string, BsonDocument>
        {
            ["encrypted"] = new BsonDocument("fields", new BsonArray())
        };

        var result = MongoClientSettingsHelper.CreateSettings(options, queryableEncryptionSchema);

        Assert.NotNull(result.AutoEncryptionOptions);
        Assert.True(result.AutoEncryptionOptions.BypassAutoEncryption);
        Assert.True(result.AutoEncryptionOptions.BypassQueryAnalysis);
        Assert.Same(tlsOptions, result.AutoEncryptionOptions.TlsOptions);
        Assert.Same(schemaMap, result.AutoEncryptionOptions.SchemaMap);
        Assert.Equal(existingEncryptedFieldsMap["db.other"], result.AutoEncryptionOptions.EncryptedFieldsMap["db.other"]);
        Assert.Equal(queryableEncryptionSchema["encrypted"], result.AutoEncryptionOptions.EncryptedFieldsMap["db.encrypted"]);
    }
}
