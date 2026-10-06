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
    private const string DatabaseName = "mydb";

    private static readonly CollectionNamespace KeyVaultNamespace = CollectionNamespace.FromFullName("keyvault.datakeys");

    private static readonly Dictionary<string, IReadOnlyDictionary<string, object>> KmsProviders = new()
    {
        {"local", new Dictionary<string, object> {{"key", new byte[96]}}}
    };

    private static readonly Dictionary<string, BsonDocument> QueryableEncryptionSchema = new()
    {
        {"Patients", BsonDocument.Parse("{ fields: [{ path: 'ssn', bsonType: 'string', queries: { queryType: 'equality' } }] }")}
    };

    [Fact]
    public static void CreateSettings_keys_the_model_schema_with_the_database_from_the_connection_string()
    {
        var options = new MongoOptionsExtension()
            .WithConnectionString($"mongodb://localhost:27017/{DatabaseName}")
            .WithKeyVaultNamespace(KeyVaultNamespace)
            .WithKmsProviders(KmsProviders);

        var settings = MongoClientSettingsHelper.CreateSettings(options, QueryableEncryptionSchema);

        Assert.Equal(
            QueryableEncryptionSchema["Patients"],
            settings.AutoEncryptionOptions.EncryptedFieldsMap[$"{DatabaseName}.Patients"]);
    }

    [Fact]
    public static void CreateSettings_keys_the_model_schema_with_the_database_name_option()
    {
        var options = new MongoOptionsExtension()
            .WithClientSettings(new MongoClientSettings())
            .WithDatabaseName(DatabaseName)
            .WithKeyVaultNamespace(KeyVaultNamespace)
            .WithKmsProviders(KmsProviders);

        var settings = MongoClientSettingsHelper.CreateSettings(options, QueryableEncryptionSchema);

        Assert.Equal(
            QueryableEncryptionSchema["Patients"],
            settings.AutoEncryptionOptions.EncryptedFieldsMap[$"{DatabaseName}.Patients"]);
    }

    [Fact]
    public static void ResolveDatabaseName_rejects_malformed_connection_strings()
    {
        // The driver's exception type here is its own business, and it may throw as early as WithConnectionString
        // or as late as here depending on what else validates the connection string - only assert it is rejected
        // by the time the database name is needed.
        Assert.ThrowsAny<Exception>(() =>
        {
            var options = new MongoOptionsExtension().WithConnectionString("not-a-connection-string");
            return MongoClientSettingsHelper.ResolveDatabaseName(options);
        });
    }

    [Theory]
    [InlineData("mongodb://localhost:27017")]
    [InlineData("mongodb://localhost:27017/")]
    public static void CreateSettings_throws_when_the_model_schema_cannot_be_keyed_to_a_database(string connectionString)
    {
        var options = new MongoOptionsExtension()
            .WithConnectionString(connectionString)
            .WithKeyVaultNamespace(KeyVaultNamespace)
            .WithKmsProviders(KmsProviders);

        var ex = Assert.Throws<InvalidOperationException>(
            () => MongoClientSettingsHelper.CreateSettings(options, QueryableEncryptionSchema));

        Assert.Contains("database name", ex.Message);
    }

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
