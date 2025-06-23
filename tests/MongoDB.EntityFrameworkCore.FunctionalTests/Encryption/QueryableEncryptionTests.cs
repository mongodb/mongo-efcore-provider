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
using MongoDB.EntityFrameworkCore.Extensions;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Encryption;

[XUnitCollection("Encryption")]
public class QueryableEncryptionTests(TemporaryDatabaseFixture database)
    : EncryptionTestsBase(database)
{
    private readonly TemporaryDatabaseFixture _database = database;

    [Theory]
    [InlineData(CryptProvider.Mongocryptd)]
    [InlineData(CryptProvider.AutoEncryptSharedLibrary)]
    public void IsEncrypted_with_no_encryption_key_id_throws(CryptProvider cryptProvider)
    {
        var db = CreateContext(cryptProvider,
            mb => mb.Entity<Patient>().ToUniqueCollection(values: cryptProvider).Property(p => p.Doctor).IsEncrypted());

        var ex = Assert.Throws<InvalidOperationException>(() => db.Model);
        Assert.Contains(nameof(Patient.Doctor), ex.Message);
        Assert.Contains("encryption data key id", ex.Message);
    }

    [Theory]
    [InlineData(CryptProvider.Mongocryptd)]
    [InlineData(CryptProvider.AutoEncryptSharedLibrary)]
    public void IsEncryptedForEquality_on_unsupported_type_throws(CryptProvider cryptProvider)
    {
        var db = CreateContext(cryptProvider, mb =>
        {
            mb.Entity<Patient>()
                .ToUniqueCollection(values: cryptProvider)
                .Property(p => p.Balance)
                .IsEncryptedForEquality(CreateDataKey());
        });

        var ex = Assert.Throws<InvalidOperationException>(() => db.Model);
        Assert.Contains(nameof(Patient.Balance), ex.Message);
        Assert.Contains(nameof(Decimal), ex.Message);
        Assert.Contains("equality", ex.Message);
    }

    [Theory]
    [InlineData(CryptProvider.Mongocryptd)]
    [InlineData(CryptProvider.AutoEncryptSharedLibrary)]
    public void IsEncryptedForRange_on_unsupported_type_throws(CryptProvider cryptProvider)
    {
        var db = CreateContext(cryptProvider, mb =>
        {
            mb.Entity<Patient>()
                .ToUniqueCollection(values: cryptProvider)
                .Property(p => p.Doctor).IsEncryptedForRange(CreateDataKey());
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
        var db = CreateContext(cryptProvider, mb =>
        {
            mb.Entity<Patient>()
                .ToUniqueCollection(values: cryptProvider)
                .Property(p => p.MonthlySubscription)
                .HasBsonRepresentation(BsonType.String)
                .IsEncryptedForRange(CreateDataKey());
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
        var db = CreateContext(cryptProvider, mb =>
        {
            mb.EncryptionDefaults(CreateDataKey());
            mb.Entity<Patient>()
                .ToUniqueCollection(values: cryptProvider)
                .Property(p => p.MonthlySubscription).IsEncryptedForRange();
        });

        var ex = Assert.Throws<InvalidOperationException>(() => db.Model);
        Assert.Contains(nameof(Patient.MonthlySubscription), ex.Message);
        Assert.Contains(nameof(Decimal128), ex.Message);
        Assert.Contains("range", ex.Message);
    }

    [Theory]
    [InlineData(CryptProvider.Mongocryptd)]
    [InlineData(CryptProvider.AutoEncryptSharedLibrary)]
    public void IsEncrypted_with_no_encryption_key_uses_one_from_entity_if_needed(CryptProvider cryptProvider)
    {
        var encryptionKeyId = Guid.NewGuid();
        var db = CreateContext(cryptProvider, mb =>
        {
            mb.Entity<Patient>()
                .ToUniqueCollection(values: cryptProvider)
                .EncryptionDefaults(encryptionKeyId)
                .Property(p => p.Doctor).IsEncrypted();
        });
    }

    [Theory]
    [InlineData(CryptProvider.Mongocryptd)]
    [InlineData(CryptProvider.AutoEncryptSharedLibrary)]
    public void IsEncrypted_with_no_encryption_key_uses_one_from_model_if_needed(CryptProvider cryptProvider)
    {
        var dataKeyId = CreateDataKey();
        var samplePatients = CreateSamplePatients;
        var collectionName = TemporaryDatabaseFixture.CreateCollectionName(values: cryptProvider);

        {
            using var db = CreateContext(cryptProvider, mb =>
            {
                mb.EncryptionDefaults(dataKeyId);
                mb.Entity<Patient>()
                    .ToCollection(collectionName)
                    .Property(p => p.Doctor).IsEncrypted();
            });
            db.Patients.AddRange(samplePatients);
            db.SaveChanges();
        }

        {
            AssertCantReadEncrypted<Patient>(collectionName, "Doctor");
        }
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
            mb.EncryptionDefaults(dataKeyId);
            mb.Entity<Patient>(p =>
            {
                p.ToCollection(collectionName);
                p.Property(x => x.SSN).IsEncrypted();
            });
        }
    }

    [Theory]
    [InlineData(CryptProvider.Mongocryptd)]
    [InlineData(CryptProvider.AutoEncryptSharedLibrary)]
    public void IsEncrypted_can_use_one_data_key_for_multiple_properties(CryptProvider cryptProvider)
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
            mb.EncryptionDefaults(dataKeyId);
            mb.Entity<Patient>(p =>
            {
                p.ToCollection(collectionName);
                p.Property(x => x.SSN).IsEncrypted();
                p.Property(x => x.Doctor).IsEncrypted();
                p.Property(x => x.DateOfBirth).IsEncrypted();
            });
        }
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
            mb.EncryptionDefaults(dataKeyId);
            mb.Entity<Patient>(p =>
            {
                p.ToCollection(collectionName);
                p.Property(x => x.SSN).IsEncryptedForEquality();
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
                var expected = samplePatients.Single(p => p.Sequence > actual.Sequence -1 && p.Sequence < actual.Sequence + 1);
                Assert.Equal(expected.SSN, actual.SSN);
            }
        }

        {
            AssertCantReadEncrypted<Patient>(collectionName, "Sequence");
        }

        void ModelConfig(ModelBuilder mb)
        {
            mb.EncryptionDefaults(dataKeyId);
            mb.Entity<Patient>(p =>
            {
                p.ToCollection(collectionName);
                p.Property(x => x.Sequence).IsEncryptedForRange(0, 100);
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
                var expected = samplePatients.Single(p => p.BillingNumber > actual.BillingNumber -1 && p.BillingNumber < actual.BillingNumber + 1);
                Assert.Equal(expected.BillingNumber, actual.BillingNumber);
            }
        }

        {
            AssertCantReadEncrypted<Patient>(collectionName, "BillingNumber");
        }

        void ModelConfig(ModelBuilder mb)
        {
            mb.EncryptionDefaults(dataKeyId);
            mb.Entity<Patient>(p =>
            {
                p.ToCollection(collectionName);
                p.Property(x => x.BillingNumber).IsEncryptedForRange(-100000, 100000);
            });
        }
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
            mb.EncryptionDefaults(dataKeyId);
            mb.Entity<Patient>(p =>
            {
                p.ToCollection(collectionName);
                p.Property(x => x.BillingNumber).IsEncryptedForRange(-10000, 10000);
            });
        }
    }

    private void AssertCantReadEncrypted<T>(string? collectionName, string propertyName)
    {
        var collection = _database.MongoDatabase.GetCollection<T>(collectionName);
        var ex = Assert.Throws<FormatException>(() => collection.AsQueryable().ToList());
        Assert.Contains(propertyName, ex.Message);
        Assert.Contains("Cannot deserialize", ex.Message);
        Assert.Contains("from BsonType 'Binary'", ex.Message);
    }

    private MedicalContext CreateContext(CryptProvider cryptProvider, Action<ModelBuilder> modelBuilderAction)
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
            .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));

        return new MedicalContext(optionsBuilder.Options, modelBuilderAction);
    }

    public class MedicalContext(DbContextOptions options, Action<ModelBuilder> modelBuilder)
        : DbContext(options)
    {
        public DbSet<Patient> Patients { get; set; }

        protected override void OnModelCreating(ModelBuilder mb)
        {
            base.OnModelCreating(mb);
            mb.Entity<Patient>().HasKey(k => k.Id);
            modelBuilder.Invoke(mb);
        }
    }
}
