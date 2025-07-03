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

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Encryption;
using MongoDB.EntityFrameworkCore.Diagnostics;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.Infrastructure;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Encryption;

[XUnitCollection("Encryption")]
public class QueryableEncryptionTests(TemporaryDatabaseFixture database)
    : EncryptionTestsBase(database)
{
    private readonly TemporaryDatabaseFixture _database = database;

    [Theory]
    [InlineData(CryptProvider.Mongocryptd)]
    [InlineData(CryptProvider.AutoEncryptSharedLibrary)]
    public void IsEncryptedForEquality_on_unsupported_type_throws(CryptProvider cryptProvider)
    {
        var dataKeyId = CreateDataKey();
        var db = CreateContext(cryptProvider, mb =>
        {
            mb.Entity<Patient>()
                .ToUniqueCollection(values: cryptProvider)
                .Property(p => p.Balance)
                .IsEncryptedForEquality(dataKeyId);
        });

        var ex = Assert.Throws<InvalidOperationException>(() => db.Model);
        Assert.Contains(nameof(Patient.Balance), ex.Message);
        Assert.Contains(nameof(Decimal), ex.Message);
        Assert.Contains("equality", ex.Message);
    }

    [Theory]
    [InlineData(CryptProvider.Mongocryptd)]
    [InlineData(CryptProvider.AutoEncryptSharedLibrary)]
    public void IsEncryptedForEquality_on_unsupported_bson_representation_throws(CryptProvider cryptProvider)
    {
        var dataKeyId = CreateDataKey();
        var db = CreateContext(cryptProvider, mb =>
        {
            mb.Entity<Patient>()
                .ToUniqueCollection(values: cryptProvider)
                .Property(p => p.Name)
                .HasBsonRepresentation(BsonType.Decimal128)
                .IsEncryptedForEquality(dataKeyId);
        });

        var ex = Assert.Throws<InvalidOperationException>(() => db.Model);
        Assert.Contains(nameof(Patient.Name), ex.Message);
        Assert.Contains(nameof(Decimal), ex.Message);
        Assert.Contains("equality", ex.Message);
    }

    [Theory]
    [InlineData(CryptProvider.Mongocryptd)]
    [InlineData(CryptProvider.AutoEncryptSharedLibrary)]
    public void IsEncryptedForEquality_query_equality_on_string(CryptProvider cryptProvider)
    {
        var dataKeyId = CreateDataKey();
        var samplePatients = CreateSamplePatients;
        var collectionName = TemporaryDatabaseFixture.CreateCollectionName(values: cryptProvider);

        {
            using var db = CreateContext(cryptProvider, ModelConfig);
            db.Patients.AddRange(samplePatients);
            db.SaveChanges();
        }

        {
            using var db = CreateContext(cryptProvider, ModelConfig);
            foreach (var actual in db.Patients)
            {
                var expected = samplePatients.Single(p => p.SSN == actual.SSN);
                Assert.Equal(expected.SSN, actual.SSN);
            }
        }

        {
            AssertCantReadEncrypted<Patient>(collectionName, "SSN");
        }

        void ModelConfig(ModelBuilder mb)
        {
            mb.Entity<Patient>(p =>
            {
                p.ToCollection(collectionName);
                p.Property(x => x.SSN).IsEncryptedForEquality(dataKeyId);
            });
        }
    }

    [Theory]
    [InlineData(CryptProvider.Mongocryptd)]
    [InlineData(CryptProvider.AutoEncryptSharedLibrary)]
    public void IsEncryptedForEquality_query_equality_on_long(CryptProvider cryptProvider)
    {
        var dataKeyId = CreateDataKey();
        var samplePatients = CreateSamplePatients;
        var collectionName = TemporaryDatabaseFixture.CreateCollectionName(values: cryptProvider);

        {
            using var db = CreateContext(cryptProvider, ModelConfig);
            db.Patients.AddRange(samplePatients);
            db.SaveChanges();
        }

        {
            using var db = CreateContext(cryptProvider, ModelConfig);
            foreach (var actual in db.Patients)
            {
                var expected = samplePatients.Single(p => p.BillingNumber == actual.BillingNumber);
                Assert.Equal(expected.BillingNumber, actual.BillingNumber);
            }
        }

        {
            AssertCantReadEncrypted<Patient>(collectionName, "BillingNumber");
        }

        void ModelConfig(ModelBuilder mb)
        {
            mb.Entity<Patient>(p =>
            {
                p.ToCollection(collectionName);
                p.Property(x => x.BillingNumber).IsEncryptedForEquality(dataKeyId);
            });
        }
    }

    [Theory]
    [InlineData(CryptProvider.Mongocryptd)]
    [InlineData(CryptProvider.AutoEncryptSharedLibrary)]
    public void IsEncryptedForRange_on_unsupported_type_throws(CryptProvider cryptProvider)
    {
        var dataKeyId = CreateDataKey();
        var db = CreateContext(cryptProvider, mb =>
        {
            mb.Entity<Patient>()
                .ToUniqueCollection(values: cryptProvider)
                .Property(p => p.Doctor).IsEncryptedForRange(dataKeyId);
        });

        var ex = Assert.Throws<InvalidOperationException>(() => db.Model);
        Assert.Contains(nameof(Patient.Doctor), ex.Message);
        Assert.Contains(nameof(String), ex.Message);
        Assert.Contains("range", ex.Message);
    }

    [Theory]
    [InlineData(CryptProvider.Mongocryptd)]
    [InlineData(CryptProvider.AutoEncryptSharedLibrary)]
    public void IsEncryptedForRange_on_unsupported_bson_representation_throws(CryptProvider cryptProvider)
    {
        var dataKeyId = CreateDataKey();
        var db = CreateContext(cryptProvider, mb =>
        {
            mb.Entity<Patient>()
                .ToUniqueCollection(values: cryptProvider)
                .Property(p => p.MonthlySubscription)
                .HasBsonRepresentation(BsonType.String)
                .IsEncryptedForRange(dataKeyId);
        });

        var ex = Assert.Throws<InvalidOperationException>(() => db.Model);
        Assert.Contains(nameof(Patient.MonthlySubscription), ex.Message);
        Assert.Contains(nameof(String), ex.Message);
        Assert.Contains("range", ex.Message);
    }

    [Theory]
    [InlineData(CryptProvider.Mongocryptd)]
    [InlineData(CryptProvider.AutoEncryptSharedLibrary)]
    public void IsEncryptedForRange_without_required_min_max_throws(CryptProvider cryptProvider)
    {
        var dataKeyId = CreateDataKey();
        var db = CreateContext(cryptProvider, mb =>
        {
            mb.Entity<Patient>()
                .ToUniqueCollection(values: cryptProvider)
                .Property(p => p.MonthlySubscription).IsEncryptedForRange(dataKeyId);
        });

        var ex = Assert.Throws<InvalidOperationException>(() => db.Model);
        Assert.Contains(nameof(Patient.MonthlySubscription), ex.Message);
        Assert.Contains(nameof(Decimal128), ex.Message);
        Assert.Contains("range", ex.Message);
    }

    [Theory]
    [InlineData(CryptProvider.Mongocryptd)]
    [InlineData(CryptProvider.AutoEncryptSharedLibrary)]
    public void IsEncryptedForRange_without_recommended_min_max_logs_warning(CryptProvider cryptProvider)
    {
        List<string> logs = [];

        var dataKeyId = CreateDataKey();
        var db = CreateContext(cryptProvider, mb =>
        {
            mb.Entity<Patient>()
                .ToUniqueCollection(values: cryptProvider)
                .Property(p => p.DateOfBirth).IsEncryptedForRange(dataKeyId);
        }, logs.Add);

        _ = db.Model;

        var logEntry = Assert.Single(logs, s => s.Contains(nameof(MongoEventId.RecommendedMinMaxRangeMissing)));
        Assert.Contains(nameof(Patient) + "." + nameof(Patient.DateOfBirth), logEntry);
        Assert.Contains("missing the recommended min/max", logEntry);
    }

    [Theory]
    [InlineData(CryptProvider.Mongocryptd)]
    [InlineData(CryptProvider.AutoEncryptSharedLibrary)]
    public void IsEncryptedForRange_query_throws_when_out_of_range(CryptProvider cryptProvider)
    {
        var dataKeyId = CreateDataKey();
        var samplePatients = CreateSamplePatients;
        var collectionName = TemporaryDatabaseFixture.CreateCollectionName(values: cryptProvider);

        {
            using var db = CreateContext(cryptProvider, ModelConfig);
            db.Patients.AddRange(samplePatients);
            var ex = Assert.Throws<MongoEncryptionException>(() => db.SaveChanges());
            Assert.Contains("12345", ex.Message);
            Assert.Contains("-10000", ex.Message);
            Assert.Contains("10000", ex.Message);
        }

        void ModelConfig(ModelBuilder mb)
        {
            mb.Entity<Patient>(p =>
            {
                p.ToCollection(collectionName);
                p.Property(x => x.BillingNumber).IsEncryptedForRange(dataKeyId, -10000, 10000);
            });
        }
    }

    [Theory]
    [InlineData(CryptProvider.Mongocryptd)]
    [InlineData(CryptProvider.AutoEncryptSharedLibrary)]
    public void IsEncryptedForRange_query_ranges_on_int(CryptProvider cryptProvider)
    {
        var dataKeyId = CreateDataKey();
        var samplePatients = CreateSamplePatients;
        var collectionName = TemporaryDatabaseFixture.CreateCollectionName(values: cryptProvider);

        {
            using var db = CreateContext(cryptProvider, ModelConfig);
            db.Patients.AddRange(samplePatients);
            db.SaveChanges();
        }

        {
            using var db = CreateContext(cryptProvider, ModelConfig);
            foreach (var actual in db.Patients)
            {
                var expected = samplePatients.Single(p => p.Sequence > actual.Sequence - 1 && p.Sequence < actual.Sequence + 1);
                Assert.Equal(expected.SSN, actual.SSN);
            }
        }

        {
            AssertCantReadEncrypted<Patient>(collectionName, "Sequence");
        }

        void ModelConfig(ModelBuilder mb)
        {
            mb.Entity<Patient>(p =>
            {
                p.ToCollection(collectionName);
                p.Property(x => x.Sequence).IsEncryptedForRange(dataKeyId, 0, 100);
            });
        }
    }

    [Theory]
    [InlineData(CryptProvider.Mongocryptd)]
    [InlineData(CryptProvider.AutoEncryptSharedLibrary)]
    public void IsEncryptedForRange_query_ranges_on_long(CryptProvider cryptProvider)
    {
        var dataKeyId = CreateDataKey();
        var samplePatients = CreateSamplePatients;
        var collectionName = TemporaryDatabaseFixture.CreateCollectionName(values: cryptProvider);

        {
            using var db = CreateContext(cryptProvider, ModelConfig);
            db.Patients.AddRange(samplePatients);
            db.SaveChanges();
        }

        {
            using var db = CreateContext(cryptProvider, ModelConfig);
            foreach (var actual in db.Patients)
            {
                var expected = samplePatients.Single(p =>
                    p.BillingNumber > actual.BillingNumber - 1 && p.BillingNumber < actual.BillingNumber + 1);
                Assert.Equal(expected.BillingNumber, actual.BillingNumber);
            }
        }

        {
            AssertCantReadEncrypted<Patient>(collectionName, "BillingNumber");
        }

        void ModelConfig(ModelBuilder mb)
        {
            mb.Entity<Patient>(p =>
            {
                p.ToCollection(collectionName);
                p.Property(x => x.BillingNumber).IsEncryptedForRange(dataKeyId, -100000, 100000);
            });
        }
    }

    [Theory]
    [InlineData(CryptProvider.Mongocryptd)]
    [InlineData(CryptProvider.AutoEncryptSharedLibrary)]
    public void IsEncrypted_on_primitive_array_property_throws(CryptProvider cryptProvider)
    {
        var dataKeyId = CreateDataKey();
        var db = CreateContext(cryptProvider, mb =>
        {
            mb.Entity<Patient>()
                .ToUniqueCollection(values: cryptProvider)
                .Property(p => p.Tags).IsEncrypted(dataKeyId);
        });

        var ex = Assert.Throws<InvalidOperationException>(() => db.Model);
        Assert.Contains(nameof(Patient.Tags), ex.Message);
        Assert.Contains("array", ex.Message);
    }

    [Theory]
    [InlineData(CryptProvider.Mongocryptd)]
    [InlineData(CryptProvider.AutoEncryptSharedLibrary)]
    public void IsEncrypted_on_owned_entity_collection_property_throws(CryptProvider cryptProvider)
    {
        var dataKeyId = CreateDataKey();
        var db = CreateContext(cryptProvider, mb =>
        {
            mb.Entity<Patient>()
                .ToUniqueCollection(values: cryptProvider)
                .OwnsMany(p => p.BloodPressureReadings)
                    .Property(p => p.Diastolic).IsEncrypted(dataKeyId);
        });

        var ex = Assert.Throws<InvalidOperationException>(() => db.Model);
        Assert.Contains(nameof(BloodPressureReading.Diastolic), ex.Message);
        Assert.Contains("BSON array", ex.Message);
        Assert.Contains("collection", ex.Message);
        Assert.Contains("owned entity", ex.Message);
        Assert.Contains("not support", ex.Message);
    }

    [Theory]
    [InlineData(CryptProvider.Mongocryptd)]
    [InlineData(CryptProvider.AutoEncryptSharedLibrary)]
    public void IsEncrypted_on_owned_entity_single_inside_collection_property_throws(CryptProvider cryptProvider)
    {
        var dataKeyId = CreateDataKey();
        var db = CreateContext(cryptProvider, mb =>
        {
            mb.Entity<Root>()
                .ToUniqueCollection(values: cryptProvider)
                .OwnsMany(r => r.Level1Multi)
                .OwnsOne(l1 => l1.Level2Single, l2 => l2.Property(x => x.Level2Name).IsEncrypted(dataKeyId));
        });

        var ex = Assert.Throws<InvalidOperationException>(() => db.Model);
        Assert.Contains(nameof(Level2Single.Level2Name), ex.Message);
        Assert.Contains("BSON array", ex.Message);
        Assert.Contains("collection", ex.Message);
        Assert.Contains("owned entity", ex.Message);
        Assert.Contains("not support", ex.Message);
    }

    [Theory]
    [InlineData(CryptProvider.Mongocryptd)]
    [InlineData(CryptProvider.AutoEncryptSharedLibrary)]
    public void IsEncrypted_round_trips_string(CryptProvider cryptProvider)
    {
        var dataKeyId = CreateDataKey();
        var samplePatients = CreateSamplePatients;
        var collectionName = TemporaryDatabaseFixture.CreateCollectionName(values: cryptProvider);

        {
            using var db = CreateContext(cryptProvider, ModelConfig);
            db.Patients.AddRange(samplePatients);
            db.SaveChanges();
        }

        {
            using var db = CreateContext(cryptProvider, ModelConfig);
            foreach (var actual in db.Patients)
            {
                var expected = samplePatients.FirstOrDefault(p => p.Id == actual.Id);
                Assert.Equal(expected.SSN, actual.SSN);
            }
        }

        {
            AssertCantReadEncrypted<Patient>(collectionName, "SSN");
        }

        void ModelConfig(ModelBuilder mb)
        {
            mb.Entity<Patient>(p =>
            {
                p.ToCollection(collectionName);
                p.Property(x => x.SSN)
                    .IsEncrypted(dataKeyId);
            });
        }
    }

    [Theory]
    [InlineData(CryptProvider.Mongocryptd)]
    [InlineData(CryptProvider.AutoEncryptSharedLibrary)]
    public void IsEncrypted_can_encrypt_multiple_properties(CryptProvider cryptProvider)
    {
        var ssnDataKey = CreateDataKey();
        var doctorDataKey = CreateDataKey();
        var dateOfBirthDataKey = CreateDataKey();

        var samplePatients = CreateSamplePatients;
        var collectionName = TemporaryDatabaseFixture.CreateCollectionName(values: cryptProvider);

        {
            using var db = CreateContext(cryptProvider, ModelConfig);
            db.Patients.AddRange(samplePatients);
            db.SaveChanges();
        }

        {
            using var db = CreateContext(cryptProvider, ModelConfig);
            foreach (var actual in db.Patients)
            {
                var expected = samplePatients.FirstOrDefault(p => p.Id == actual.Id);
                Assert.Equal(expected.SSN, actual.SSN);
            }
        }

        {
            AssertCantReadEncrypted<Patient>(collectionName);
        }

        void ModelConfig(ModelBuilder mb)
        {
            mb.Entity<Patient>(p =>
            {
                p.ToCollection(collectionName);
                p.Property(x => x.SSN).IsEncrypted(ssnDataKey);
                p.Property(x => x.Doctor).IsEncrypted(doctorDataKey);
                p.Property(x => x.DateOfBirth).IsEncrypted(dateOfBirthDataKey);
            });
        }
    }


    [Theory]
    [InlineData(CryptProvider.Mongocryptd)]
    [InlineData(CryptProvider.AutoEncryptSharedLibrary)]
    public void IsEncrypted_round_trips_owned_entity_string(CryptProvider cryptProvider)
    {
        var dataKeyId = CreateDataKey();
        var samplePatients = CreateSamplePatients;
        var collectionName = TemporaryDatabaseFixture.CreateCollectionName(values: cryptProvider);

        {
            using var db = CreateContext(cryptProvider, ModelConfig);
            db.Patients.AddRange(samplePatients);
            db.SaveChanges();
        }

        {
            using var db = CreateContext(cryptProvider, ModelConfig);
            foreach (var actual in db.Patients)
            {
                var expected = samplePatients.FirstOrDefault(p => p.Id == actual.Id);
                Assert.Equal(expected.LongTermCarePlan.Instructions, actual.LongTermCarePlan.Instructions);
            }
        }

        {
            AssertCantReadEncrypted<Patient>(collectionName, nameof(Patient.LongTermCarePlan));
        }

        void ModelConfig(ModelBuilder mb)
        {
            mb.Entity<Patient>(p =>
            {
                p.ToCollection(collectionName);
                p.OwnsOne(x => x.LongTermCarePlan)
                    .Property(x => x.Instructions)
                    .IsEncrypted(dataKeyId);
            });
        }
    }

    [Theory]
    [InlineData(CryptProvider.Mongocryptd)]
    [InlineData(CryptProvider.AutoEncryptSharedLibrary)]
    public void Test_stuff(CryptProvider cryptProvider)
    {
        var dataKeyId = CreateDataKey();
        var samplePatients = CreateSamplePatients;
        var collectionName = TemporaryDatabaseFixture.CreateCollectionName(values: cryptProvider);

        using var db = CreateContext(cryptProvider, ModelConfig);

        var clientEncryptionOptions = new ClientEncryptionOptions(
            _database.Client,
            KeyVaultNamespace,
            KmsProviders);

        var clientEncryption = new ClientEncryption(clientEncryptionOptions);
        var entityType = db.Model.FindEntityType(typeof(Patient))!;
        var schema = QueryableEncryptionSchemaGenerator.GenerateSchema(entityType);
        var createCollectionOptions = new CreateCollectionOptions { EncryptedFields = schema };

        var x = clientEncryption.CreateEncryptedCollection(_database.MongoDatabase, collectionName, createCollectionOptions,
            "local", new DataKeyOptions());

        void ModelConfig(ModelBuilder mb)
        {
            mb.Entity<Patient>(p =>
            {
                p.ToCollection(collectionName);
                p.OwnsOne(x => x.LongTermCarePlan)
                    .Property(x => x.Instructions)
                    .IsEncrypted(dataKeyId);
            });
        }
    }

    private void AssertCantReadEncrypted<T>(string? collectionName, string? propertyName = null)
    {
        var collection = _database.MongoDatabase.GetCollection<T>(collectionName);
        var ex = Assert.Throws<FormatException>(() => collection.AsQueryable().ToList());
        Assert.Contains("Cannot deserialize", ex.Message);
        Assert.Contains("from BsonType 'Binary'", ex.Message);
        if (propertyName != null)
        {
            Assert.Contains(propertyName, ex.Message);
        }
    }

    private MedicalContext CreateContext(CryptProvider cryptProvider, Action<ModelBuilder> modelBuilderAction,
        Action<string>? logger = null)
    {
        var mongoOptions = new MongoOptionsExtension()
            .WithClientSettings(_database.Client.Settings)
            .WithDatabaseName(_database.MongoDatabase.DatabaseNamespace.DatabaseName)
            .WithKeyVaultNamespace(KeyVaultNamespace)
            .WithKmsProviders(KmsProviders);

        mongoOptions = cryptProvider switch
        {
            CryptProvider.AutoEncryptSharedLibrary => mongoOptions.WithCryptProvider(CryptProvider.AutoEncryptSharedLibrary,
                Environment.GetEnvironmentVariable("CRYPT_SHARED_LIB_PATH")),
            CryptProvider.Mongocryptd => mongoOptions.WithCryptProvider(CryptProvider.Mongocryptd,
                Environment.GetEnvironmentVariable("MONGODB_BINARIES")),
            _ => mongoOptions
        };

        var optionsBuilder = new DbContextOptionsBuilder<MedicalContext>()
            .UseMongoDB(mongoOptions)
            .ReplaceService<IModelCacheKeyFactory, IgnoreCacheKeyFactory>()
            .LogTo(l => logger?.Invoke(l))
            .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));

        return new MedicalContext(optionsBuilder.Options, modelBuilderAction);
    }

    public class MedicalContext(DbContextOptions options, Action<ModelBuilder> modelBuilder)
        : DbContext(options)
    {
        public DbSet<Patient> Patients { get; set; }
        public DbSet<Root> Roots { get; set; }

        protected override void OnModelCreating(ModelBuilder mb)
        {
            base.OnModelCreating(mb);
            mb.Entity<Patient>().HasKey(k => k.Id);
            modelBuilder.Invoke(mb);
        }
    }

    public class Root
    {
        public ObjectId Id { get; set; }
        public string? Name { get; set; }
        public List<Level1Multi> Level1Multi { get; set; }
    }

    public class Level1Multi
    {
        public string Level1Name { get; set; }
        public Level2Single Level2Single { get; set; }
    }

    public class Level2Single
    {
        public string Level2Name { get; set; }
    }
}
