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

using System.Security.Cryptography;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Encryption;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Compatibility;

[XUnitCollection("FieldLevelEncryption")]
public class FieldLevelEncryptionTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    private readonly Dictionary<string, IReadOnlyDictionary<string, object>> _kmsProviders =
        CreateKmsProvidersWithLocalMasterKey(CreateMasterKey());

    private readonly CollectionNamespace _keyVaultNamespace =
        CollectionNamespace.FromFullName(database.MongoDatabase.DatabaseNamespace.DatabaseName + "._keyVault");

    static FieldLevelEncryptionTests()
    {
        MongoClientSettings.Extensions.AddAutoEncryption();
    }

    [Theory]
    [InlineData(CryptProvider.Mongocryptd)]
    [InlineData(CryptProvider.AutoEncryptSharedLibrary)]
    public void Encrypted_data_can_not_be_read_without_encrypted_client(CryptProvider cryptProvider)
    {
        var collection = database.CreateCollection<Patient>(null, cryptProvider);
        SetupEncryptedTestData(cryptProvider, collection.CollectionNamespace.CollectionName);

        using var db = SingleEntityDbContext.Create(collection);

        Assert.Throws<FormatException>(() => db.Entities.First());
    }

    [Theory]
    [InlineData(CryptProvider.Mongocryptd)]
    [InlineData(CryptProvider.AutoEncryptSharedLibrary)]
    public void Encrypted_data_can_not_be_read_with_wrong_master_key(CryptProvider cryptProvider)
    {
        // Setup data with a master key
        var collection = database.CreateCollection<Patient>(null, cryptProvider);
        var collectionNamespace = collection.CollectionNamespace;
        SetupEncryptedTestData(cryptProvider, collectionNamespace.CollectionName);

        // Create a second MongoDB encrypted client with a different (wrong) master key
        var kmsWrongMaster = CreateKmsProvidersWithLocalMasterKey(CreateMasterKey());
        var patientDataKey = CreateDataKey(database.Client, _keyVaultNamespace, kmsWrongMaster);
        var schemaMap = CreateSchemaMap(collectionNamespace.CollectionName, patientDataKey);
        var wrongClient = CreateEncryptedClient(collectionNamespace, kmsWrongMaster, cryptProvider,
            schemaMap);

        var alternateCollection = wrongClient
            .GetDatabase(database.MongoDatabase.DatabaseNamespace.DatabaseName)
            .GetCollection<Patient>(collectionNamespace.CollectionName);
        using var db = SingleEntityDbContext.Create(alternateCollection);
        Assert.Throws<MongoEncryptionException>(() => db.Entities.First());
    }

    [Theory]
    [InlineData(CryptProvider.Mongocryptd)]
    [InlineData(CryptProvider.AutoEncryptSharedLibrary)]
    public void Encrypted_data_can_round_trip(CryptProvider cryptProvider)
    {
        var collection = database.CreateCollection<Patient>(null, cryptProvider);
        var encryptedCollection = SetupEncryptedTestData(cryptProvider, collection.CollectionNamespace.CollectionName);

        // Test driver load
        Assert.Equal("145014000", encryptedCollection.AsQueryable().First().ssn);

        // Test EF Core load & save
        {
            using var db = SingleEntityDbContext.Create(encryptedCollection);
            var patient = db.Entities.First();
            Assert.Equal("145014000", patient.ssn);
            patient.bloodType = "O-";
            db.SaveChanges();
        }

        // Ensure saved data is correct via Driver & EF
        Assert.Equal("O-", encryptedCollection.AsQueryable().First().bloodType);
        Assert.Equal("O-", SingleEntityDbContext.Create(encryptedCollection).Entities.First().bloodType);

        // EF Query
        {
            using var db = SingleEntityDbContext.Create(encryptedCollection);
            var patient = db.Entities.First(p => p.ssn == "145014000");
            Assert.Equal("145014000", patient.ssn);
        }
    }

    private IMongoCollection<Patient> SetupEncryptedTestData(CryptProvider cryptProvider, string collectionName)
    {
        // Create new key for this collection
        var patientDataKey = CreateDataKey(database.Client, _keyVaultNamespace, _kmsProviders);

        // Create the schema map and an encrypted client
        var schemaMap = CreateSchemaMap(collectionName, patientDataKey);
        var encryptedClient = CreateEncryptedClient(_keyVaultNamespace, _kmsProviders, cryptProvider, schemaMap);

        // Insert test data using the driver
        var collection = encryptedClient.GetDatabase(database.MongoDatabase.DatabaseNamespace.DatabaseName)
            .GetCollection<Patient>(collectionName);

        collection.InsertMany([
            new Patient
            {
                name = "Jon Doe",
                ssn = "145014000",
                bloodType = "AB-",
                medicalRecords = [new MedicalRecord {weight = 180, bloodPressure = "120/80"}]
            },
            new Patient {name = "Tom Smith", ssn = "1234567", bloodType = "O-", medicalRecords = []}
        ]);

        return collection;
    }

    private Dictionary<string, BsonDocument> CreateSchemaMap(string collectionName, Guid patientDataKey)
        => new()
        {
            {
                database.MongoDatabase.DatabaseNamespace.DatabaseName + "." + collectionName,
                CreatePatientEncryptionSchema(patientDataKey)
            }
        };

    private static byte[] CreateMasterKey()
        => RandomNumberGenerator.GetBytes(96);

    private static Guid CreateDataKey(
        IMongoClient client,
        CollectionNamespace keyVaultNamespace,
        Dictionary<string, IReadOnlyDictionary<string, object>> kmsProviders)
    {
        var clientEncryptionOptions = new ClientEncryptionOptions(client, keyVaultNamespace, kmsProviders);
        using var clientEncryption = new ClientEncryption(clientEncryptionOptions);
        return clientEncryption.CreateDataKey("local", new DataKeyOptions(), CancellationToken.None);
    }

    private static Dictionary<string, IReadOnlyDictionary<string, object>> CreateKmsProvidersWithLocalMasterKey(byte[] masterKey)
        => new() {{"local", new Dictionary<string, object> {{"key", masterKey}}}};

    private const string AesDeterministic = "AEAD_AES_256_CBC_HMAC_SHA_512-Deterministic";
    private const string AesRandom = "AEAD_AES_256_CBC_HMAC_SHA_512-Random";

    private static BsonDocument CreatePatientEncryptionSchema(Guid dataKey)
        => new()
        {
            {"bsonType", "object"},
            {
                "encryptMetadata",
                new BsonDocument
                {
                    {"algorithm", AesDeterministic},
                    {"keyId", new BsonArray([new BsonBinaryData(dataKey, GuidRepresentation.Standard)])}
                }
            },
            {
                "properties",
                new BsonDocument
                {
                    {"ssn", new BsonDocument {{"encrypt", new BsonDocument {{"bsonType", "string"}}}}},
                    {
                        "bloodType",
                        new BsonDocument {{"encrypt", new BsonDocument {{"bsonType", "string"}, {"algorithm", AesRandom}}}}
                    },
                    {
                        "medicalRecords",
                        new BsonDocument {{"encrypt", new BsonDocument {{"bsonType", "array"}, {"algorithm", AesRandom}}}}
                    }
                }
            }
        };

    public enum CryptProvider
    {
        AutoEncryptSharedLibrary,
        Mongocryptd
    };

    private MongoClient CreateEncryptedClient(
        CollectionNamespace keyVaultNamespace,
        Dictionary<string, IReadOnlyDictionary<string, object>> kmsProviders,
        CryptProvider cryptProvider,
        Dictionary<string, BsonDocument> schemaMap)
    {
        var extraOptions = cryptProvider == CryptProvider.Mongocryptd
            ? GetExtraOptionsForMongocryptd()
            : GetExtraOptionsForCryptShared();

        var clientSettings = database.Client.Settings.Clone();
        clientSettings.AutoEncryptionOptions = new AutoEncryptionOptions(
            keyVaultNamespace,
            kmsProviders,
            schemaMap: schemaMap,
            extraOptions: extraOptions);
        return new MongoClient(clientSettings);
    }

    private static Dictionary<string, object> GetExtraOptionsForCryptShared()
        => new() {{"cryptSharedLibPath", GetEnvironmentVariableOrThrow("CRYPT_SHARED_LIB_PATH")}, {"cryptSharedLibRequired", true}};

    private static Dictionary<string, object> GetExtraOptionsForMongocryptd()
        => new() {{"mongocryptdSpawnPath", GetEnvironmentVariableOrThrow("MONGODB_BINARIES")}};

    private static string GetEnvironmentVariableOrThrow(string variable)
        => Environment.GetEnvironmentVariable(variable) ?? throw new Exception($"Environment variable \"{variable}\" not set.");

    class Patient
    {
        public ObjectId _id { get; set; }
        public string name { get; set; }
        public string ssn { get; set; }
        public string bloodType { get; set; }
        public List<MedicalRecord> medicalRecords { get; set; }
    }

    class MedicalRecord
    {
        public DateTimeOffset when { get; set; }
        public int weight { get; set; }
        public string bloodPressure { get; set; }
    }
}
