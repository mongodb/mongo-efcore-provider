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
using MongoDB.Driver.Encryption;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Encryption;

[XUnitCollection("EncryptionTests")]
public class EncryptionTests(TemporaryDatabaseFixture database)
    : EncryptionTestsBase(database)
{
    private readonly TemporaryDatabaseFixture _database = database;

    public static TheoryData<CryptProvider, EncryptionMode> CryptProviderAndEncryptionModeData
    {
        get
        {
            var data = new TheoryData<CryptProvider, EncryptionMode>
            {
                { CryptProvider.Mongocryptd, EncryptionMode.ClientSideFieldLevelEncryption },
                { CryptProvider.AutoEncryptSharedLibrary, EncryptionMode.ClientSideFieldLevelEncryption }
            };
            if (ShouldRunQueryableEncryptionTests)
            {
                data.Add(CryptProvider.Mongocryptd, EncryptionMode.QueryableEncryption);
                data.Add(CryptProvider.AutoEncryptSharedLibrary, EncryptionMode.QueryableEncryption);
            }
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(CryptProviderAndEncryptionModeData))]
    public void Encrypted_data_can_not_be_read_without_encrypted_client(CryptProvider cryptProvider, EncryptionMode encryptionMode)
    {
        var collection = _database.CreateCollection<Patient>(values: [cryptProvider, encryptionMode]);
        SetupEncryptedTestData(cryptProvider, collection.CollectionNamespace.CollectionName, encryptionMode);

        using var db = SingleEntityDbContext.Create(collection);

        Assert.Throws<FormatException>(() => db.Entities.First());
    }

    [Theory]
    [MemberData(nameof(CryptProviderAndEncryptionModeData))]
    public void Encrypted_data_can_not_be_read_with_wrong_master_key(CryptProvider cryptProvider, EncryptionMode encryptionMode)
    {
        // Setup data with a master key
        var collection = _database.CreateCollection<Patient>(values: [cryptProvider, encryptionMode]);
        var collectionNamespace = collection.CollectionNamespace;
        SetupEncryptedTestData(cryptProvider, collectionNamespace.CollectionName, encryptionMode);

        // Create a second MongoDB encrypted client with a different (wrong) master key
        var kmsWrongMaster = CreateKmsProvidersWithLocalMasterKey(CreateMasterKey());
        var schemaMap = encryptionMode == EncryptionMode.ClientSideFieldLevelEncryption
            ? CreateSchemaMap(collectionNamespace.CollectionName)
            : null;
        var encryptedFieldsMap = encryptionMode == EncryptionMode.QueryableEncryption
            ? CreateEncryptedFieldsMap(collectionNamespace.CollectionName)
            : null;
        var wrongClient = CreateEncryptedClient(collectionNamespace, kmsWrongMaster, cryptProvider, schemaMap, encryptedFieldsMap);

        var alternateCollection = wrongClient
            .GetDatabase(_database.MongoDatabase.DatabaseNamespace.DatabaseName)
            .GetCollection<Patient>(collectionNamespace.CollectionName);
        using var db = SingleEntityDbContext.Create(alternateCollection);

        var ex = Assert.Throws<MongoEncryptionException>(() => db.Entities.First());
        Assert.Contains("not all keys requested were satisfied", ex.Message);
    }

    internal static bool IsBuggyMongocryptd =>
        Environment.GetEnvironmentVariable("MONGODB_VERSION") == "latest";

    [Theory]
    [MemberData(nameof(CryptProviderAndEncryptionModeData))]
    public void Encrypted_data_can_round_trip(CryptProvider cryptProvider, EncryptionMode encryptionMode)
    {
        // Remove me once mongocryptd is fixed for Windows on latest
        if (cryptProvider == CryptProvider.Mongocryptd && IsBuggyMongocryptd) return;

        var collection = _database.CreateCollection<Patient>(values: [cryptProvider, encryptionMode]);
        var encryptedCollection =
            SetupEncryptedTestData(cryptProvider, collection.CollectionNamespace.CollectionName, encryptionMode);

        // Test driver load
        Assert.Equal("145014000", encryptedCollection.AsQueryable().First().SSN);

        // Test EF Core load & save
        {
            using var db = SingleEntityDbContext.Create(encryptedCollection);
            var patient = db.Entities.First();
            Assert.Equal("145014000", patient.SSN);
            patient.BloodType = "O-";
            db.SaveChanges();
        }

        // Ensure saved data is correct via Driver & EF
        Assert.Equal("O-", encryptedCollection.AsQueryable().First().BloodType);
        Assert.Equal("O-", SingleEntityDbContext.Create(encryptedCollection).Entities.First().BloodType);

        // EF Query
        {
            using var db = SingleEntityDbContext.Create(encryptedCollection);
            var patient = db.Entities.First(p => p.SSN == "145014000");
            Assert.Equal("145014000", patient.SSN);
        }
    }

    [QueryableEncryptionTheory]
    [InlineData(CryptProvider.Mongocryptd)]
    [InlineData(CryptProvider.AutoEncryptSharedLibrary)]
    public void Encrypted_data_can_be_queried_with_range_for_queryable_encryption(CryptProvider cryptProvider)
    {
        if (!ShouldRunQueryableEncryptionTests) return;

        var collection = _database.CreateCollection<Patient>(values: [cryptProvider]);
        var encryptedCollection =
            SetupEncryptedTestData(cryptProvider, collection.CollectionNamespace.CollectionName,
                EncryptionMode.QueryableEncryption);

        using var db = SingleEntityDbContext.Create(encryptedCollection);

        var patientOne = db.Entities.First(e => e.Sequence > 10);
        Assert.Equal(20, patientOne.Sequence);

        var patientTwo = db.Entities.First(e => e.DateOfBirth < new DateTime(2000, 01, 26, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(1985, patientTwo.DateOfBirth.Year);
    }

    private IMongoCollection<Patient> SetupEncryptedTestData(
        CryptProvider cryptProvider,
        string collectionName,
        EncryptionMode encryptionMode)
    {
        // Create the schema map and an encrypted client
        var schemaMap = encryptionMode == EncryptionMode.ClientSideFieldLevelEncryption
            ? CreateSchemaMap(collectionName)
            : null;
        var encryptedFieldsMap = encryptionMode == EncryptionMode.QueryableEncryption
            ? CreateEncryptedFieldsMap(collectionName)
            : null;
        var encryptedClient =
            CreateEncryptedClient(KeyVaultNamespace, KmsProviders, cryptProvider, schemaMap, encryptedFieldsMap);

        // Insert test data using the driver
        var collection = encryptedClient.GetDatabase(_database.MongoDatabase.DatabaseNamespace.DatabaseName)
            .GetCollection<Patient>(collectionName);

        collection.InsertMany(CreateSamplePatients);

        return collection;
    }

    private Dictionary<string, BsonDocument> CreateSchemaMap(string collectionName)
        => new()
        {
            { _database.MongoDatabase.DatabaseNamespace.DatabaseName + "." + collectionName, CreatePatientEncryptionSchema() }
        };

    private const string AesDeterministic = "AEAD_AES_256_CBC_HMAC_SHA_512-Deterministic";
    private const string AesRandom = "AEAD_AES_256_CBC_HMAC_SHA_512-Random";

    private BsonDocument CreatePatientEncryptionSchema()
        => new()
        {
            { "bsonType", "object" },
            {
                "encryptMetadata",
                new BsonDocument { { "algorithm", AesDeterministic }, { "keyId", new BsonArray([AsBsonBinary(CreateDataKey())]) } }
            },
            {
                "properties",
                new BsonDocument
                {
                    { "SSN", new BsonDocument { { "encrypt", new BsonDocument { { "bsonType", "string" } } } } },
                    {
                        "BloodType",
                        new BsonDocument
                        {
                            { "encrypt", new BsonDocument { { "bsonType", "string" }, { "algorithm", AesRandom } } }
                        }
                    },
                    {
                        "MedicalRecords",
                        new BsonDocument
                        {
                            { "encrypt", new BsonDocument { { "bsonType", "array" }, { "algorithm", AesRandom } } }
                        }
                    },
                    { "Sequence", new BsonDocument { { "encrypt", new BsonDocument { { "bsonType", "int" } } } } }
                }
            }
        };

    public enum EncryptionMode
    {
        ClientSideFieldLevelEncryption,
        QueryableEncryption
    }
}
