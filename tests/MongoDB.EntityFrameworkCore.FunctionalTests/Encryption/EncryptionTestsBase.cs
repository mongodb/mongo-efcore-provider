﻿/* Copyright 2023-present MongoDB Inc.
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
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Driver;
using MongoDB.Driver.Encryption;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Encryption;

public abstract class EncryptionTestsBase(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    protected readonly Dictionary<string, IReadOnlyDictionary<string, object>> KmsProviders =
        CreateKmsProvidersWithLocalMasterKey(CreateMasterKey());

    protected readonly CollectionNamespace KeyVaultNamespace =
        CollectionNamespace.FromFullName(database.MongoDatabase.DatabaseNamespace.DatabaseName + "._keyVault");

    static EncryptionTestsBase()
    {
        MongoClientSettings.Extensions.AddAutoEncryption();
        BsonSerializer.RegisterSerializer(new GuidSerializer(GuidRepresentation.Standard));
    }

    protected MongoClient CreateEncryptedClient(
        CollectionNamespace keyVaultNamespace,
        Dictionary<string, IReadOnlyDictionary<string, object>> kmsProviders,
        CryptProvider cryptProvider,
        Dictionary<string, BsonDocument>? schemaMap,
        Dictionary<string, BsonDocument>? encryptedFieldsMap)
    {
        var extraOptions = cryptProvider == CryptProvider.Mongocryptd
            ? GetExtraOptionsForMongocryptd()
            : GetExtraOptionsForCryptShared();

        var clientSettings = database.Client.Settings.Clone();
        clientSettings.AutoEncryptionOptions = new AutoEncryptionOptions(
            keyVaultNamespace,
            kmsProviders,
            schemaMap: schemaMap,
            encryptedFieldsMap: encryptedFieldsMap,
            extraOptions: extraOptions);
        return new MongoClient(clientSettings);
    }

    protected class QueryableEncryptionTheory : TheoryAttribute
    {
        public override string? Skip
        {
            get => ShouldRunQueryableEncryptionTests
                ? null
                : "These Queryable Encryption tests require MongoDB 8.0 or later as declared by the VERSION environment variable.";
        }
    }

    protected static bool ShouldRunQueryableEncryptionTests =>
        Environment.GetEnvironmentVariable("MONGODB_VERSION") switch
        {
            null => true,
            "latest" => true,
            var v when Version.TryParse(v, out var parsedVersion) && parsedVersion >= new Version(8, 0) => true,
            _ => false
        };

    protected static byte[] CreateMasterKey()
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

    protected Dictionary<string, BsonDocument> CreateEncryptedFieldsMap(string collectionName)
        => new()
        {
            { database.MongoDatabase.DatabaseNamespace.DatabaseName + "." + collectionName, CreatePatientEncryptedFieldsMap() }
        };

    protected Guid CreateDataKey() =>
        CreateDataKey(database.Client, KeyVaultNamespace, KmsProviders);

    protected static BsonBinaryData AsBsonBinary(Guid dataKey)
        => new(dataKey, GuidRepresentation.Standard);

    private BsonDocument CreatePatientEncryptedFieldsMap()
        => new()
        {
            {
                "fields",
                new BsonArray
                {
                    new BsonDocument
                    {
                        { "keyId", AsBsonBinary(CreateDataKey()) },
                        { "path", "SSN" },
                        { "bsonType", "string" },
                        { "queries", new BsonDocument("queryType", "equality") }
                    },
                    new BsonDocument
                    {
                        { "keyId", AsBsonBinary(CreateDataKey()) },
                        { "path", "Sequence" },
                        { "bsonType", "int" },
                        { "queries", new BsonDocument("queryType", "range") }
                    },
                    new BsonDocument
                    {
                        { "keyId", AsBsonBinary(CreateDataKey()) },
                        { "path", "DateOfBirth" },
                        { "bsonType", "date" },
                        { "queries", new BsonDocument { { "queryType", "range" }, } }
                    }
                }
            }
        };

    protected static Dictionary<string,
        IReadOnlyDictionary<string, object>> CreateKmsProvidersWithLocalMasterKey(byte[] masterKey)
        => new() { { "local", new Dictionary<string, object> { { "key", masterKey } } } };

    private static Dictionary<string, object> GetExtraOptionsForCryptShared()
        => new()
        {
            { "cryptSharedLibPath", GetEnvironmentVariableOrThrow("CRYPT_SHARED_LIB_PATH") }, { "cryptSharedLibRequired", true }
        };

    private static Dictionary<string, object> GetExtraOptionsForMongocryptd()
        => new() { { "mongocryptdSpawnPath", GetEnvironmentVariableOrThrow("MONGODB_BINARIES") } };

    private static string GetEnvironmentVariableOrThrow(string variable)
        => Environment.GetEnvironmentVariable(variable) ?? throw new Exception($"Environment variable \"{variable}\" not set.");

    protected static Patient[] CreateSamplePatients =>
    [
        new()
        {
            Name = "Calvin McFly",
            IsActive = true,
            SSN = "145014000",
            DateOfBirth = new DateTime(1985, 10, 26, 0, 0, 0, DateTimeKind.Utc),
            BloodType = "AB-",
            Doctor = "Emmett Grey",
            Sequence = 10,
            Balance = 456.78m,
            BillingNumber = 12345,
            BloodPressureReadings = [new BloodPressureReading { Diastolic = 120, Systolic = 80 }],
            ExternalRef = Guid.NewGuid(),
            ExternalObjectId = ObjectId.GenerateNewId(),
            WeightMeasurements =
                [new WeightMeasurement { WeightKilograms = 75, When = new DateTime(2024, 10, 26, 0, 0, 0, DateTimeKind.Utc) }],
            LongTermCarePlan = new LongTermCarePlan { Instructions = "Take it easy and enjoy life." },
            Tags = ["tired", "happy"],
            BillingAddress = new Address { Street = "123 Anywhere", City = "New New York", State = "NY" }
        },
        new()
        {
            Name = "Red Tyler",
            IsActive = false,
            SSN = "1234567",
            DateOfBirth = new DateTime(2000, 1, 26, 0, 0, 0, DateTimeKind.Utc),
            Doctor = "John Smith",
            BloodType = "O-",
            Sequence = 20,
            Balance = 789.00m,
            BillingNumber = -123,
            BloodPressureReadings = [],
            ExternalRef = Guid.NewGuid(),
            ExternalObjectId = ObjectId.GenerateNewId(),
            LongTermCarePlan = new LongTermCarePlan { Instructions = "Live long and prosper." },
            Tags = ["new"],
            BillingAddress = new Address { Street = "321 Nowhere", City = "Old York", State = "UK" }
        }
    ];

    public class Patient
    {
        public ObjectId Id { get; set; }

        public string Name { get; set; }

        public bool IsActive { get; set; }

        [BsonElement("ssn")]
        public string SSN { get; set; }

        [BsonElement("dateOfBirth")]
        public DateTime DateOfBirth { get; set; }

        [BsonElement("doctor")]
        public string? Doctor { get; set; }

        public decimal MonthlySubscription { get; set; }
        public string BloodType { get; set; }
        public decimal Balance { get; set; }
        public string? InsuranceCompany { get; set; }
        public string? PolicyReference { get; set; }
        public int Sequence { get; set; }
        public long BillingNumber { get; set; }
        public string[] Tags { get; set; }

        public Guid ExternalRef { get; set; }
        public ObjectId ExternalObjectId { get; set; }

        public List<BloodPressureReading> BloodPressureReadings { get; set; }
        public List<WeightMeasurement> WeightMeasurements { get; set; }

        [BsonElement("longTermCarePlan")]
        public LongTermCarePlan? LongTermCarePlan { get; set; }

        public Address BillingAddress { get; set; }
    }

    public class WeightMeasurement
    {
        public DateTime When { get; set; }
        public decimal WeightKilograms { get; set; }
    }

    public class BloodPressureReading
    {
        public DateTime When { get; set; }
        public int Diastolic { get; set; }
        public int Systolic { get; set; }
    }

    public class LongTermCarePlan
    {
        [BsonElement("instructions")]
        public string Instructions { get; set; }

        public int Room { get; set; }
    }

    public class Address
    {
        public string Street { get; set; }
        public string City { get; set; }
        public string State { get; set; }
    }
}
