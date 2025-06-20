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
using MongoDB.EntityFrameworkCore.Extensions;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Encryption;

[XUnitCollection("Encryption")]
public class QueryableEncryptionTests(TemporaryDatabaseFixture database)
    : EncryptionTestsBase(database)
{
    private readonly Guid _unregisteredDataKey = Guid.NewGuid();
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
                .IsEncryptedForEquality(_unregisteredDataKey);
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
                .Property(p => p.Doctor).IsEncryptedForRange(_unregisteredDataKey);
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
                .IsEncryptedForRange(_unregisteredDataKey);
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
            mb.EncryptionDefaults(_unregisteredDataKey);
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
        var encryptionKeyId = Guid.NewGuid();
        var db = CreateContext(cryptProvider, mb =>
        {
            mb.EncryptionDefaults(encryptionKeyId);
            mb.Entity<Patient>()
                .ToUniqueCollection(values: cryptProvider)
                .Property(p => p.Doctor).IsEncrypted();
        });
        var model = db.Model;
    }

    [Theory]
    [InlineData(CryptProvider.Mongocryptd)]
    [InlineData(CryptProvider.AutoEncryptSharedLibrary)]
    public void IsEncrypted_round_trips_string(CryptProvider cryptProvider)
    {
        var dataKey = CreateDataKey();
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
            AssertIsActuallyEncrypted<Patient>(collectionName, "SSN");
        }

        void ModelConfig(ModelBuilder mb)
        {
            mb.EncryptionDefaults(dataKey);
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
    public void IsEncryptedForEquality_query_equality_on_string(CryptProvider cryptProvider)
    {
        var dataKey = CreateDataKey();
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
            AssertIsActuallyEncrypted<Patient>(collectionName, "SSN");
        }

        void ModelConfig(ModelBuilder mb)
        {
            mb.EncryptionDefaults(dataKey);
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
        var dataKey = CreateDataKey();
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
            AssertIsActuallyEncrypted<Patient>(collectionName, "Sequence");
        }

        void ModelConfig(ModelBuilder mb)
        {
            mb.EncryptionDefaults(dataKey);
            mb.Entity<Patient>(p =>
            {
                p.ToCollection(collectionName);
                p.Property(x => x.Sequence).IsEncryptedForRange(0, 100);
            });
        }
    }

    private void AssertIsActuallyEncrypted<T>(string? collectionName, string propertyName)
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
