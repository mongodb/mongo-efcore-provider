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
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Encryption;
using MongoDB.EntityFrameworkCore.Diagnostics;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.Infrastructure;
using MongoDB.EntityFrameworkCore.Metadata;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Encryption;

[XUnitCollection("Encryption")]
public class QueryableEncryptionTests(TemporaryDatabaseFixture database)
    : AutoEncryptionTestsBase(database)
{
    private readonly TemporaryDatabaseFixture _database = database;

    private void CreateEncryptedCollections(IModel model)
    {
        var queryableEncryptionSchemas = QueryableEncryptionSchemaGenerator.GenerateSchemas(model);
        foreach (var entityType in model.GetEntityTypes().Where(e => e.IsDocumentRoot()))
        {
            var collectionName = entityType.GetCollectionName();
            if (queryableEncryptionSchemas.TryGetValue(collectionName, out var value))
            {
                _database.MongoDatabase.CreateCollection(collectionName,  new CreateCollectionOptions { EncryptedFields = value });
            }
        }
    }

    [QueryableEncryptionTheory]
    [InlineData(CryptProvider.Mongocryptd)]
    [InlineData(CryptProvider.AutoEncryptSharedLibrary)]
    public void Server_schema_is_used_when_mode_is_ignore(CryptProvider cryptProvider)
    {
        var collectionName = TemporaryDatabaseFixture.CreateCollectionName(values: cryptProvider);
        var dataKeyId = CreateDataKey();
        var expectedPatients = CreateSamplePatients;

        // Create data with a server schema
        {
            using var db = CreateContext(cryptProvider, mb =>
            {
                mb.Entity<Patient>(p =>
                {
                    p.ToCollection(collectionName);
                    p.Property(x => x.SSN).IsEncrypted(dataKeyId);
                });
            });

            CreateEncryptedCollections(db.Model);
            db.Database.EnsureCreated();

            db.Patients.AddRange(expectedPatients);
            db.SaveChanges();
        }

        // Read it ignoring client schema (wrong encryption data key id would throw if used)
        {
            using var db = CreateContext(cryptProvider, mb =>
            {
                mb.Entity<Patient>(p =>
                {
                    p.ToCollection(collectionName);
                    p.Property(x => x.SSN).IsEncrypted(Guid.NewGuid());
                });
            }, ob => ob.WithQueryableEncryptionSchemaMode(QueryableEncryptionSchemaMode.Ignore));

            var actualPatients = db.Patients.ToList();

            Assert.Contains(expectedPatients[0].SSN, actualPatients.Select(p => p.SSN));
            Assert.Contains(expectedPatients[1].SSN, actualPatients.Select(p => p.SSN));
            Assert.Equal(2, actualPatients.Count);
        }
    }

    [QueryableEncryptionTheory]
    [InlineData(CryptProvider.Mongocryptd)]
    [InlineData(CryptProvider.AutoEncryptSharedLibrary)]
    public async Task Server_schema_is_used_when_mode_is_ignore_async(CryptProvider cryptProvider)
    {
        var collectionName = TemporaryDatabaseFixture.CreateCollectionName(values: cryptProvider);
        var dataKeyId = CreateDataKey();
        var expectedPatients = CreateSamplePatients;

        // Create a server schema for this data
        {
            await using var db = CreateContext(cryptProvider, mb =>
            {
                mb.Entity<Patient>(p =>
                {
                    p.ToCollection(collectionName);
                    p.Property(x => x.SSN).IsEncrypted(dataKeyId);
                });
            });

            CreateEncryptedCollections(db.Model);

            await db.Database.EnsureCreatedAsync();
            await db.Patients.AddRangeAsync(expectedPatients);
            await db.SaveChangesAsync();
        }

        // Read it ignoring client schema (wrong encryption data key id would throw if used)
        {
            await using var db = CreateContext(cryptProvider, mb =>
            {
                mb.Entity<Patient>(p =>
                {
                    p.ToCollection(collectionName);
                    p.Property(x => x.SSN).IsEncrypted(Guid.NewGuid());
                });
            }, ob => ob.WithQueryableEncryptionSchemaMode(QueryableEncryptionSchemaMode.Ignore));

            var actualPatients = await db.Patients.ToListAsync();

            Assert.Contains(expectedPatients[0].SSN, actualPatients.Select(p => p.SSN));
            Assert.Contains(expectedPatients[1].SSN, actualPatients.Select(p => p.SSN));
            Assert.Equal(2, actualPatients.Count);
        }
    }

    [QueryableEncryptionTheory]
    [InlineData(CryptProvider.Mongocryptd)]
    [InlineData(CryptProvider.AutoEncryptSharedLibrary)]
    public void Server_schema_is_used_when_no_client_schema_available(CryptProvider cryptProvider)
    {
        var collectionName = TemporaryDatabaseFixture.CreateCollectionName(values: cryptProvider);
        var expectedPatients = CreateSamplePatients;
        var dataKeyId = CreateDataKey();

        // Create a server schema for this data
        {
            using var db = CreateContext(cryptProvider, mb =>
            {
                mb.Entity<Patient>(p =>
                {
                    p.ToCollection(collectionName);
                    p.Property(x => x.SSN).IsEncrypted(dataKeyId);
                });
            });

            CreateEncryptedCollections(db.Model);
            db.Database.EnsureCreated();
            db.Patients.AddRange(expectedPatients);
            db.SaveChanges();
        }

        // Read it with ignored client schema
        {
            using var db = CreateContext(cryptProvider, mb => { mb.Entity<Patient>(p => { p.ToCollection(collectionName); }); },
                ob => ob.WithQueryableEncryptionSchemaMode(QueryableEncryptionSchemaMode.Ignore));

            var actualPatients = db.Patients.ToList();

            Assert.Contains(expectedPatients[0].SSN, actualPatients.Select(p => p.SSN));
            Assert.Contains(expectedPatients[1].SSN, actualPatients.Select(p => p.SSN));
            Assert.Equal(2, actualPatients.Count);
        }

        // Read it with no client schema
        {
            using var db = CreateContext(cryptProvider, mb => { mb.Entity<Patient>(p => { p.ToCollection(collectionName); }); });

            var actualPatients = db.Patients.ToList();

            Assert.Contains(expectedPatients[0].SSN, actualPatients.Select(p => p.SSN));
            Assert.Contains(expectedPatients[1].SSN, actualPatients.Select(p => p.SSN));
            Assert.Equal(2, actualPatients.Count);
        }
    }

    [QueryableEncryptionTheory]
    [InlineData(CryptProvider.Mongocryptd)]
    [InlineData(CryptProvider.AutoEncryptSharedLibrary)]
    public async Task Server_schema_is_used_when_no_client_schema_available_async(CryptProvider cryptProvider)
    {
        var collectionName = TemporaryDatabaseFixture.CreateCollectionName(values: cryptProvider);
        var expectedPatients = CreateSamplePatients;
        var dataKeyId = CreateDataKey();

        // Create a server schema for this data
        {
            await using var db = CreateContext(cryptProvider, mb =>
            {
                mb.Entity<Patient>(p =>
                {
                    p.ToCollection(collectionName);
                    p.Property(x => x.SSN).IsEncrypted(dataKeyId);
                });
            });

            CreateEncryptedCollections(db.Model);
            await db.Database.EnsureCreatedAsync();
            await db.Patients.AddRangeAsync(expectedPatients);
            await db.SaveChangesAsync();
        }

        // Read it without a client schema (wrong encryption data key id)
        {
            await using var db = CreateContext(cryptProvider,
                mb => { mb.Entity<Patient>(p => { p.ToCollection(collectionName); }); },
                ob => ob.WithQueryableEncryptionSchemaMode(QueryableEncryptionSchemaMode.Ignore));

            var actualPatients = await db.Patients.ToListAsync();

            Assert.Contains(expectedPatients[0].SSN, actualPatients.Select(p => p.SSN));
            Assert.Contains(expectedPatients[1].SSN, actualPatients.Select(p => p.SSN));
            Assert.Equal(2, actualPatients.Count);
        }
    }

    [QueryableEncryptionTheory]
    [InlineData(CryptProvider.Mongocryptd, QueryableEncryptionType.NotQueryable)]
    [InlineData(CryptProvider.Mongocryptd, QueryableEncryptionType.Equality)]
    [InlineData(CryptProvider.Mongocryptd, QueryableEncryptionType.Range)]
    [InlineData(CryptProvider.AutoEncryptSharedLibrary, QueryableEncryptionType.NotQueryable)]
    [InlineData(CryptProvider.AutoEncryptSharedLibrary, QueryableEncryptionType.Equality)]
    [InlineData(CryptProvider.AutoEncryptSharedLibrary, QueryableEncryptionType.Range)]
    public void IsEncryptedForAnything_on_unsupported_array_throws(
        CryptProvider cryptProvider,
        QueryableEncryptionType encryptionType)
    {
        var dataKeyId = CreateDataKey();
        using var db = CreateContext(cryptProvider, mb =>
        {
            var p = mb.Entity<Patient>()
                .ToUniqueCollection(values: [cryptProvider, encryptionType])
                .Property(p => p.Tags);
            SetPropertyEncryption(p, encryptionType, dataKeyId);
        });

        var ex = Assert.Throws<InvalidOperationException>(() => db.Model);
        Assert.Contains(nameof(Patient.Tags), ex.Message);
        Assert.Contains("array", ex.Message);
        Assert.Contains("encryption", ex.Message);
    }

    [QueryableEncryptionTheory]
    [InlineData(CryptProvider.Mongocryptd)]
    [InlineData(CryptProvider.AutoEncryptSharedLibrary)]
    public void IsEncrypted_on_unsupported_owned_entity_collection_property_throws(CryptProvider cryptProvider)
    {
        var dataKeyId = CreateDataKey();
        using var db = CreateContext(cryptProvider, mb =>
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

    [QueryableEncryptionTheory]
    [InlineData(CryptProvider.Mongocryptd)]
    [InlineData(CryptProvider.AutoEncryptSharedLibrary)]
    public void IsEncrypted_on_unsupported_owned_entity_single_inside_collection_property_throws(CryptProvider cryptProvider)
    {
        var dataKeyId = CreateDataKey();
        using var db = CreateContext(cryptProvider, mb =>
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

    [QueryableEncryptionTheory]
    [InlineData(CryptProvider.Mongocryptd)]
    [InlineData(CryptProvider.AutoEncryptSharedLibrary)]
    public void IsEncrypted_on_non_nullable_property_does_not_log_warning(CryptProvider cryptProvider)
    {
        List<string> logs = [];

        var dataKeyId = CreateDataKey();
        using var db = CreateContext(cryptProvider, mb =>
        {
            mb.Entity<Patient>()
                .ToUniqueCollection(values: cryptProvider)
                .Property(p => p.DateOfBirth)
                .IsEncrypted(dataKeyId);
        }, logger: logs.Add);

        _ = db.Model;

        Assert.DoesNotContain(logs, s => s.Contains(nameof(MongoEventId.EncryptedNullablePropertyEncountered)));
    }

    [QueryableEncryptionTheory]
    [InlineData(CryptProvider.Mongocryptd)]
    [InlineData(CryptProvider.AutoEncryptSharedLibrary)]
    public void IsEncrypted_on_nullable_property_logs_warning(CryptProvider cryptProvider)
    {
        List<string> logs = [];

        var dataKeyId = CreateDataKey();
        using var db = CreateContext(cryptProvider, mb =>
        {
            mb.Entity<Patient>()
                .ToUniqueCollection(values: cryptProvider)
                .Property(p => p.InsuranceCompany)
                .IsEncrypted(dataKeyId);
        }, logger: logs.Add);

        _ = db.Model;

        var logEntry = Assert.Single(logs, s => s.Contains(nameof(MongoEventId.EncryptedNullablePropertyEncountered)));
        Assert.Contains(nameof(Patient) + "." + nameof(Patient.InsuranceCompany), logEntry);
    }

    [QueryableEncryptionTheory]
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
            AssertCantReadEncrypted<Patient>(collectionName, nameof(Patient.SSN));
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

    [QueryableEncryptionTheory]
    [InlineData(CryptProvider.Mongocryptd, BsonType.Int32)]
    [InlineData(CryptProvider.Mongocryptd, BsonType.Int64)]
    [InlineData(CryptProvider.AutoEncryptSharedLibrary, BsonType.Int32)]
    [InlineData(CryptProvider.AutoEncryptSharedLibrary, BsonType.Int64)]
    public void IsEncrypted_round_trips_integer(
        CryptProvider cryptProvider,
        BsonType storageType)
    {
        var dataKeyId = CreateDataKey();
        var samplePatients = CreateSamplePatients;
        var collectionName = TemporaryDatabaseFixture.CreateCollectionName(values: [cryptProvider, storageType]);

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
                Assert.Equal(expected.Sequence, actual.Sequence);
            }
        }

        {
            AssertCantReadEncrypted<Patient>(collectionName, nameof(Patient.Sequence));
        }

        void ModelConfig(ModelBuilder mb)
        {
            mb.Entity<Patient>(p =>
            {
                p.ToCollection(collectionName);
                p.Property(x => x.Sequence)
                    .HasBsonRepresentation(storageType)
                    .IsEncrypted(dataKeyId);
            });
        }
    }

    [QueryableEncryptionTheory]
    [InlineData(CryptProvider.Mongocryptd, BsonType.Decimal128)]
    [InlineData(CryptProvider.Mongocryptd, BsonType.Double)]
    [InlineData(CryptProvider.AutoEncryptSharedLibrary, BsonType.Decimal128)]
    [InlineData(CryptProvider.AutoEncryptSharedLibrary, BsonType.Double)]
    public void IsEncrypted_round_trips_float(
        CryptProvider cryptProvider,
        BsonType storageType)
    {
        var dataKeyId = CreateDataKey();
        var samplePatients = CreateSamplePatients;
        var collectionName = TemporaryDatabaseFixture.CreateCollectionName(values: [cryptProvider, storageType]);

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
                Assert.Equal(expected.Balance, actual.Balance);
            }
        }

        {
            AssertCantReadEncrypted<Patient>(collectionName, nameof(Patient.Balance));
        }

        void ModelConfig(ModelBuilder mb)
        {
            mb.Entity<Patient>(p =>
            {
                p.ToCollection(collectionName);
                p.Property(x => x.Balance)
                    .HasBsonRepresentation(storageType)
                    .IsEncrypted(dataKeyId);
            });
        }
    }

    [QueryableEncryptionTheory]
    [InlineData(CryptProvider.Mongocryptd, BsonType.DateTime)]
    [InlineData(CryptProvider.Mongocryptd, BsonType.Int64)]
    [InlineData(CryptProvider.Mongocryptd, BsonType.String)]
    [InlineData(CryptProvider.AutoEncryptSharedLibrary, BsonType.DateTime)]
    [InlineData(CryptProvider.AutoEncryptSharedLibrary, BsonType.Int64)]
    [InlineData(CryptProvider.AutoEncryptSharedLibrary, BsonType.String)]
    public void IsEncrypted_round_trips_datetime(
        CryptProvider cryptProvider,
        BsonType storageType)
    {
        var dataKeyId = CreateDataKey();
        var samplePatients = CreateSamplePatients;
        var collectionName = TemporaryDatabaseFixture.CreateCollectionName(values: [cryptProvider, storageType]);

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
                Assert.Equal(expected.DateOfBirth, actual.DateOfBirth);
            }
        }

        {
            AssertCantReadEncrypted<Patient>(collectionName, nameof(Patient.DateOfBirth));
        }

        void ModelConfig(ModelBuilder mb)
        {
            mb.Entity<Patient>(p =>
            {
                p.ToCollection(collectionName);
                p.Property(x => x.DateOfBirth)
                    .HasBsonRepresentation(storageType)
                    .IsEncrypted(dataKeyId);
            });
        }
    }

    [QueryableEncryptionTheory]
    [InlineData(CryptProvider.Mongocryptd)]
    [InlineData(CryptProvider.AutoEncryptSharedLibrary)]
    public void IsEncrypted_round_trips_guid(CryptProvider cryptProvider)
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
                Assert.Equal(expected.ExternalRef, actual.ExternalRef);
            }
        }

        {
            AssertCantReadEncrypted<Patient>(collectionName, nameof(Patient.ExternalRef));
        }

        void ModelConfig(ModelBuilder mb)
        {
            mb.Entity<Patient>(p =>
            {
                p.ToCollection(collectionName);
                p.Property(x => x.ExternalRef)
                    .IsEncrypted(dataKeyId);
            });
        }
    }

    [QueryableEncryptionTheory]
    [InlineData(CryptProvider.Mongocryptd)]
    [InlineData(CryptProvider.AutoEncryptSharedLibrary)]
    public void IsEncrypted_round_trips_objectid(CryptProvider cryptProvider)
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
                Assert.Equal(expected.ExternalObjectId, actual.ExternalObjectId);
            }
        }

        {
            AssertCantReadEncrypted<Patient>(collectionName, nameof(Patient.ExternalObjectId));
        }

        void ModelConfig(ModelBuilder mb)
        {
            mb.Entity<Patient>(p =>
            {
                p.ToCollection(collectionName);
                p.Property(x => x.ExternalObjectId)
                    .IsEncrypted(dataKeyId);
            });
        }
    }

    [QueryableEncryptionTheory]
    [InlineData(CryptProvider.Mongocryptd)]
    [InlineData(CryptProvider.AutoEncryptSharedLibrary)]
    public void IsEncrypted_round_trips_boolean(CryptProvider cryptProvider)
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
                var expected = samplePatients.FirstOrDefault(p => p.IsActive == actual.IsActive);
                Assert.Equal(expected.IsActive, actual.IsActive);
                Assert.Equal(expected.Id, actual.Id);
            }
        }

        {
            AssertCantReadEncrypted<Patient>(collectionName, nameof(Patient.IsActive));
        }

        void ModelConfig(ModelBuilder mb)
        {
            mb.Entity<Patient>(p =>
            {
                p.ToCollection(collectionName);
                p.Property(x => x.IsActive)
                    .IsEncrypted(dataKeyId);
            });
        }
    }

    [QueryableEncryptionTheory]
    [InlineData(CryptProvider.Mongocryptd)]
    [InlineData(CryptProvider.AutoEncryptSharedLibrary)]
    public void IsEncrypted_round_trips_owned_entity_doc(CryptProvider cryptProvider)
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
                Assert.Equal(expected.LongTermCarePlan.Room, actual.LongTermCarePlan.Room);
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
                    .IsEncrypted(dataKeyId);
            });
        }
    }

    [QueryableEncryptionTheory]
    [InlineData(CryptProvider.Mongocryptd)]
    [InlineData(CryptProvider.AutoEncryptSharedLibrary)]
    public void IsEncrypted_round_trips_owned_entity_doc_with_equality_sub_property(CryptProvider cryptProvider)
    {
        var dataKeyId1 = CreateDataKey();
        var dataKeyId2 = CreateDataKey();
        var collectionName = TemporaryDatabaseFixture.CreateCollectionName(values: cryptProvider);

        using var db = CreateContext(cryptProvider, mb => mb.Entity<Patient>(p =>
        {
            p.ToCollection(collectionName);
            p.OwnsOne(x => x.LongTermCarePlan, q =>
            {
                q.IsEncrypted(dataKeyId1);
                q.Property(r => r.Instructions).IsEncryptedForEquality(dataKeyId2);
            });
        }));

        var ex = Assert.Throws<InvalidOperationException>(() => db.Model);
        Assert.Contains(nameof(Patient.LongTermCarePlan), ex.Message);
        Assert.Contains(nameof(Patient.LongTermCarePlan.Instructions), ex.Message);
        Assert.Contains("alternative", ex.Message);
    }

    [QueryableEncryptionTheory]
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

    [QueryableEncryptionTheory]
    [InlineData(CryptProvider.Mongocryptd, BsonType.Int32)]
    [InlineData(CryptProvider.Mongocryptd, BsonType.Int64)]
    [InlineData(CryptProvider.AutoEncryptSharedLibrary, BsonType.Int32)]
    [InlineData(CryptProvider.AutoEncryptSharedLibrary, BsonType.Int64)]
    public void IsEncrypted_round_trips_owned_entity_integer(CryptProvider cryptProvider, BsonType storageType)
    {
        var dataKeyId = CreateDataKey();
        var samplePatients = CreateSamplePatients;
        var collectionName = TemporaryDatabaseFixture.CreateCollectionName(values: [cryptProvider, storageType]);

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
                Assert.Equal(expected.LongTermCarePlan.Room, actual.LongTermCarePlan.Room);
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
                    .Property(x => x.Room)
                    .HasBsonRepresentation(storageType)
                    .IsEncrypted(dataKeyId);
            });
        }
    }

    [QueryableEncryptionTheory]
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

    [QueryableEncryptionTheory]
    [InlineData(CryptProvider.Mongocryptd)]
    [InlineData(CryptProvider.AutoEncryptSharedLibrary)]
    public void IsEncrypted_sharing_data_keys_between_properties_throws(CryptProvider cryptProvider)
    {
        var dataKeyId = CreateDataKey();
        var collectionName = TemporaryDatabaseFixture.CreateCollectionName(values: cryptProvider);

        using var db = CreateContext(cryptProvider, mb => mb.Entity<Patient>(p =>
        {
            p.ToCollection(collectionName);
            p.Property(q => q.IsActive).IsEncrypted(dataKeyId);
            p.Property(q => q.SSN).IsEncrypted(dataKeyId);
        }));

        var ex = Assert.Throws<InvalidOperationException>(() => db.Model);
        Assert.Contains(nameof(Patient), ex.Message);
        Assert.Contains(ex.Message.Contains("SSN") ? nameof(Patient.SSN) : nameof(Patient.IsActive), ex.Message);
        Assert.Contains("already been used", ex.Message);
        Assert.DoesNotContain(ex.Message, dataKeyId.ToString());
    }

    [QueryableEncryptionTheory]
    [InlineData(CryptProvider.Mongocryptd)]
    [InlineData(CryptProvider.AutoEncryptSharedLibrary)]
    public void IsEncrypted_sharing_data_keys_between_navigations_throws(CryptProvider cryptProvider)
    {
        var dataKeyId = CreateDataKey();
        var collectionName = TemporaryDatabaseFixture.CreateCollectionName(values: cryptProvider);

        using var db = CreateContext(cryptProvider, mb => mb.Entity<Patient>(p =>
        {
            p.ToCollection(collectionName);
            p.OwnsOne(q => q.LongTermCarePlan).IsEncrypted(dataKeyId);
            p.OwnsOne(q => q.BillingAddress).IsEncrypted(dataKeyId);
        }));

        var ex = Assert.Throws<InvalidOperationException>(() => db.Model);
        Assert.Contains(nameof(Patient), ex.Message);
        Assert.Contains(ex.Message.Contains("BillingAddress") ? nameof(Patient.BillingAddress) : nameof(Patient.LongTermCarePlan),
            ex.Message);
        Assert.Contains("already been used", ex.Message);
        Assert.DoesNotContain(ex.Message, dataKeyId.ToString());
    }

    [QueryableEncryptionTheory]
    [InlineData(CryptProvider.Mongocryptd)]
    [InlineData(CryptProvider.AutoEncryptSharedLibrary)]
    public void IsEncrypted_sharing_data_keys_between_navigation_and_property_throws(CryptProvider cryptProvider)
    {
        var dataKeyId = CreateDataKey();
        var collectionName = TemporaryDatabaseFixture.CreateCollectionName(values: cryptProvider);

        using var db = CreateContext(cryptProvider, mb => mb.Entity<Patient>(p =>
        {
            p.ToCollection(collectionName);
            p.Property(q => q.IsActive).IsEncrypted(dataKeyId);
            p.OwnsOne(q => q.BillingAddress).IsEncrypted(dataKeyId);
        }));

        var ex = Assert.Throws<InvalidOperationException>(() => db.Model);
        Assert.Contains(nameof(Patient), ex.Message);
        Assert.Contains(ex.Message.Contains("BillingAddress") ? nameof(Patient.BillingAddress) : nameof(Patient.IsActive),
            ex.Message);
        Assert.Contains("already been used", ex.Message);
        Assert.DoesNotContain(ex.Message, dataKeyId.ToString());
    }

    [QueryableEncryptionTheory]
    [InlineData(CryptProvider.Mongocryptd)]
    [InlineData(CryptProvider.AutoEncryptSharedLibrary)]
    public void IsEncrypted_sharing_data_keys_between_different_entities_is_permitted(CryptProvider cryptProvider)
    {
        var dataKeyId = CreateDataKey();

        using var db = CreateContext(cryptProvider,
            mb =>
            {
                mb.Entity<Patient>()
                    .ToUniqueCollection(null, values: [cryptProvider, "Patient"])
                    .Property(q => q.IsActive).IsEncrypted(dataKeyId);

                mb.Entity<Root>()
                    .ToUniqueCollection(null, values: [cryptProvider, "Root"])
                    .Property(s => s.Name).IsEncrypted(dataKeyId);
            });

        db.Patients.AddRange(CreateSamplePatients);
        db.Roots.Add(new Root { Name = "Root" });
        db.SaveChanges();
    }

    [QueryableEncryptionTheory]
    [InlineData(CryptProvider.Mongocryptd, BsonType.Decimal128)]
    [InlineData(CryptProvider.Mongocryptd, BsonType.Double)]
    [InlineData(CryptProvider.AutoEncryptSharedLibrary, BsonType.Decimal128)]
    [InlineData(CryptProvider.AutoEncryptSharedLibrary, BsonType.Double)]
    public void IsEncryptedForEquality_on_unsupported_float_throws(
        CryptProvider cryptProvider,
        BsonType storageType)
    {
        var dataKeyId = CreateDataKey();
        using var db = CreateContext(cryptProvider, mb =>
        {
            mb.Entity<Patient>()
                .ToUniqueCollection(values: [cryptProvider, storageType])
                .Property(p => p.Name)
                .HasBsonRepresentation(storageType)
                .IsEncryptedForEquality(dataKeyId);
        });

        var ex = Assert.Throws<InvalidOperationException>(() => db.Model);
        Assert.Contains(nameof(Patient.Name), ex.Message);
        Assert.Contains(storageType.ToString(), ex.Message);
        Assert.Contains("equality", ex.Message);
    }

    [QueryableEncryptionTheory]
    [InlineData(CryptProvider.Mongocryptd)]
    [InlineData(CryptProvider.AutoEncryptSharedLibrary)]
    public void IsEncryptedForEquality_queries_equality_on_string(CryptProvider cryptProvider)
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

    [QueryableEncryptionTheory]
    [InlineData(CryptProvider.Mongocryptd)]
    [InlineData(CryptProvider.AutoEncryptSharedLibrary)]
    public void IsEncryptedForEquality_queries_equality_on_guid(CryptProvider cryptProvider)
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
                var expected = samplePatients.Single(p => p.ExternalRef == actual.ExternalRef);
                Assert.Equal(expected.ExternalRef, actual.ExternalRef);
            }
        }

        {
            AssertCantReadEncrypted<Patient>(collectionName, "ExternalRef");
        }

        void ModelConfig(ModelBuilder mb)
        {
            mb.Entity<Patient>(p =>
            {
                p.ToCollection(collectionName);
                p.Property(x => x.ExternalRef).IsEncryptedForEquality(dataKeyId);
            });
        }
    }

    [QueryableEncryptionTheory]
    [InlineData(CryptProvider.Mongocryptd)]
    [InlineData(CryptProvider.AutoEncryptSharedLibrary)]
    public void IsEncryptedForEquality_queries_equality_on_objectid(CryptProvider cryptProvider)
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
                var expected = samplePatients.Single(p => p.ExternalObjectId == actual.ExternalObjectId);
                Assert.Equal(expected.ExternalObjectId, actual.ExternalObjectId);
            }
        }

        {
            AssertCantReadEncrypted<Patient>(collectionName, "ExternalObjectId");
        }

        void ModelConfig(ModelBuilder mb)
        {
            mb.Entity<Patient>(p =>
            {
                p.ToCollection(collectionName);
                p.Property(x => x.ExternalObjectId).IsEncryptedForEquality(dataKeyId);
            });
        }
    }

    [QueryableEncryptionTheory]
    [InlineData(CryptProvider.Mongocryptd, BsonType.Int32)]
    [InlineData(CryptProvider.Mongocryptd, BsonType.Int64)]
    [InlineData(CryptProvider.AutoEncryptSharedLibrary, BsonType.Int32)]
    [InlineData(CryptProvider.AutoEncryptSharedLibrary, BsonType.Int64)]
    public void IsEncryptedForEquality_queries_equality_on_integer(
        CryptProvider cryptProvider,
        BsonType storageType)
    {
        var dataKeyId = CreateDataKey();
        var samplePatients = CreateSamplePatients;
        var collectionName = TemporaryDatabaseFixture.CreateCollectionName(values: [cryptProvider, storageType]);

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
                p.Property(x => x.BillingNumber)
                    .HasBsonRepresentation(storageType)
                    .IsEncryptedForEquality(dataKeyId);
            });
        }
    }

    [QueryableEncryptionTheory]
    [InlineData(CryptProvider.Mongocryptd, BsonType.Int32)]
    [InlineData(CryptProvider.Mongocryptd, BsonType.Int64)]
    [InlineData(CryptProvider.AutoEncryptSharedLibrary, BsonType.Int32)]
    [InlineData(CryptProvider.AutoEncryptSharedLibrary, BsonType.Int64)]
    public void IsEncryptedForEquality_with_contention_queries_equality_on_integer(
        CryptProvider cryptProvider,
        BsonType storageType)
    {
        var dataKeyId = CreateDataKey();
        var samplePatients = CreateSamplePatients;
        var collectionName = TemporaryDatabaseFixture.CreateCollectionName(values: [cryptProvider, storageType]);

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
                p.Property(x => x.BillingNumber)
                    .HasBsonRepresentation(storageType)
                    .IsEncryptedForEquality(dataKeyId, e => e.WithContention(1));
            });
        }
    }

    [QueryableEncryptionTheory]
    [InlineData(CryptProvider.Mongocryptd)]
    [InlineData(CryptProvider.AutoEncryptSharedLibrary)]
    public void IsEncryptedForRange_on_unsupported_string_throws(CryptProvider cryptProvider)
    {
        var dataKeyId = CreateDataKey();
        using var db = CreateContext(cryptProvider, mb =>
        {
            mb.Entity<Patient>()
                .ToUniqueCollection(values: cryptProvider)
                .Property(p => p.MonthlySubscription)
                .HasBsonRepresentation(BsonType.String)
                .IsEncryptedForRange(0m, 100m, dataKeyId);
        });

        var ex = Assert.Throws<InvalidOperationException>(() => db.Model);
        Assert.Contains(nameof(Patient.MonthlySubscription), ex.Message);
        Assert.Contains(nameof(String), ex.Message);
        Assert.Contains("range", ex.Message);
    }

    [QueryableEncryptionTheory]
    [InlineData(CryptProvider.Mongocryptd)]
    [InlineData(CryptProvider.AutoEncryptSharedLibrary)]
    public void IsEncryptedForRange_on_unsupported_guid_throws(CryptProvider cryptProvider)
    {
        var dataKeyId = CreateDataKey();
        using var db = CreateContext(cryptProvider, mb =>
        {
            mb.Entity<Patient>()
                .ToUniqueCollection(values: cryptProvider)
                .Property(p => p.ExternalRef)
                .IsEncryptedForRange(Guid.Empty, Guid.Empty, dataKeyId);
            ;
        });

        var ex = Assert.Throws<InvalidOperationException>(() => db.Model);
        Assert.Contains(nameof(Patient.ExternalRef), ex.Message);
        Assert.Contains(nameof(BsonType.Binary), ex.Message);
        Assert.Contains("range", ex.Message);
    }

    [QueryableEncryptionTheory]
    [InlineData(CryptProvider.Mongocryptd)]
    [InlineData(CryptProvider.AutoEncryptSharedLibrary)]
    public void IsEncryptedForRange_on_unsupported_objectid_throws(CryptProvider cryptProvider)
    {
        var dataKeyId = CreateDataKey();
        using var db = CreateContext(cryptProvider, mb =>
        {
            mb.Entity<Patient>()
                .ToUniqueCollection(values: cryptProvider)
                .Property(p => p.ExternalObjectId)
                .IsEncryptedForRange(new ObjectId(), new ObjectId(), dataKeyId);
        });

        var ex = Assert.Throws<InvalidOperationException>(() => db.Model);
        Assert.Contains(nameof(Patient.ExternalObjectId), ex.Message);
        Assert.Contains(nameof(BsonType.ObjectId), ex.Message);
        Assert.Contains("range", ex.Message);
    }

    [QueryableEncryptionTheory]
    [InlineData(CryptProvider.Mongocryptd, BsonType.Int32)]
    [InlineData(CryptProvider.Mongocryptd, BsonType.Int64)]
    [InlineData(CryptProvider.AutoEncryptSharedLibrary, BsonType.Int32)]
    [InlineData(CryptProvider.AutoEncryptSharedLibrary, BsonType.Int64)]
    public void IsEncryptedForRange_query_throws_when_integer_out_of_range(
        CryptProvider cryptProvider,
        BsonType storageType)
    {
        var dataKeyId = CreateDataKey();
        var samplePatients = CreateSamplePatients;
        var collectionName = TemporaryDatabaseFixture.CreateCollectionName(values: [cryptProvider, storageType]);

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
                p.Property(x => x.BillingNumber)
                    .HasBsonRepresentation(storageType)
                    .IsEncryptedForRange(-10000, 10000, dataKeyId);
            });
        }
    }

    [QueryableEncryptionTheory]
    [InlineData(CryptProvider.Mongocryptd, BsonType.Int32)]
    [InlineData(CryptProvider.Mongocryptd, BsonType.Int64)]
    [InlineData(CryptProvider.AutoEncryptSharedLibrary, BsonType.Int32)]
    [InlineData(CryptProvider.AutoEncryptSharedLibrary, BsonType.Int64)]
    public void IsEncryptedForRange_queries_range_on_integer(
        CryptProvider cryptProvider,
        BsonType storageType)
    {
        var dataKeyId = CreateDataKey();
        var samplePatients = CreateSamplePatients;
        var collectionName = TemporaryDatabaseFixture.CreateCollectionName(values: [cryptProvider, storageType]);

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
                p.Property(x => x.Sequence)
                    .HasBsonRepresentation(storageType)
                    .IsEncryptedForRange(0, 100, dataKeyId);
            });
        }
    }

    [QueryableEncryptionTheory]
    [InlineData(CryptProvider.Mongocryptd)]
    [InlineData(CryptProvider.AutoEncryptSharedLibrary)]
    public void IsEncryptedForRange_queries_ranges_on_datetime(CryptProvider cryptProvider)
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
                    p.DateOfBirth >= actual.DateOfBirth && p.DateOfBirth <= actual.DateOfBirth);
                Assert.Equal(expected.DateOfBirth, actual.DateOfBirth);
            }
        }

        {
            AssertCantReadEncrypted<Patient>(collectionName, "DateOfBirth");
        }

        void ModelConfig(ModelBuilder mb)
        {
            mb.Entity<Patient>(p =>
            {
                p.ToCollection(collectionName);
                p.Property(x => x.DateOfBirth)
                    .IsEncryptedForRange(DateTime.MinValue, DateTime.MaxValue, dataKeyId);
            });
        }
    }

    [QueryableEncryptionTheory]
    [InlineData(CryptProvider.Mongocryptd)]
    [InlineData(CryptProvider.AutoEncryptSharedLibrary)]
    public void IsEncryptedForRange_without_recommended_min_max_datetime_logs_warning(CryptProvider cryptProvider)
    {
        List<string> logs = [];

        var dataKeyId = CreateDataKey();
        using var db = CreateContext(cryptProvider, mb =>
        {
            mb.Entity<Patient>()
                .ToUniqueCollection(values: cryptProvider)
                .Property(p => p.DateOfBirth)
                .HasBsonRepresentation(BsonType.DateTime)
                .IsEncryptedForRange(DateTime.MinValue, DateTime.MaxValue, dataKeyId)
                .HasAnnotation(MongoAnnotationNames.QueryableEncryptionRangeMin, null);
        }, logger: logs.Add);

        _ = db.Model;

        var logEntry = Assert.Single(logs, s => s.Contains(nameof(MongoEventId.RecommendedMinMaxRangeMissing)));
        Assert.Contains(nameof(Patient) + "." + nameof(Patient.DateOfBirth), logEntry);
        Assert.Contains(nameof(BsonType.DateTime), logEntry);
        Assert.Contains("missing the recommended min/max", logEntry);
    }

    [QueryableEncryptionTheory]
    [InlineData(CryptProvider.Mongocryptd, BsonType.Decimal128)]
    [InlineData(CryptProvider.Mongocryptd, BsonType.Double)]
    [InlineData(CryptProvider.AutoEncryptSharedLibrary, BsonType.Decimal128)]
    [InlineData(CryptProvider.AutoEncryptSharedLibrary, BsonType.Double)]
    public void IsEncryptedForRange_queries_ranges_on_float(
        CryptProvider cryptProvider,
        BsonType storageType)
    {
        var dataKeyId = CreateDataKey();
        var samplePatients = CreateSamplePatients;
        var collectionName = TemporaryDatabaseFixture.CreateCollectionName(values: [cryptProvider, storageType]);

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
                    p.Balance >= actual.Balance && p.Balance <= actual.Balance);
                Assert.Equal(expected.Balance, actual.Balance);
            }
        }

        {
            AssertCantReadEncrypted<Patient>(collectionName, "Balance");
        }

        void ModelConfig(ModelBuilder mb)
        {
            mb.Entity<Patient>(p =>
            {
                p.ToCollection(collectionName);
                p.Property(x => x.Balance)
                    .HasBsonRepresentation(storageType)
                    .IsEncryptedForRange(0m, 100000m, 2, dataKeyId);
            });
        }
    }

    [QueryableEncryptionTheory]
    [InlineData(CryptProvider.Mongocryptd, BsonType.Decimal128)]
    [InlineData(CryptProvider.Mongocryptd, BsonType.Double)]
    [InlineData(CryptProvider.AutoEncryptSharedLibrary, BsonType.Decimal128)]
    [InlineData(CryptProvider.AutoEncryptSharedLibrary, BsonType.Double)]
    public void IsEncryptedForRange_with_all_options_queries_ranges_on_float(
        CryptProvider cryptProvider,
        BsonType storageType)
    {
        var dataKeyId = CreateDataKey();
        var samplePatients = CreateSamplePatients;
        var collectionName = TemporaryDatabaseFixture.CreateCollectionName(values: [cryptProvider, storageType]);

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
                    p.Balance >= actual.Balance && p.Balance <= actual.Balance);
                Assert.Equal(expected.Balance, actual.Balance);
            }
        }

        {
            AssertCantReadEncrypted<Patient>(collectionName, "Balance");
        }

        void ModelConfig(ModelBuilder mb)
        {
            mb.Entity<Patient>(p =>
            {
                p.ToCollection(collectionName);
                p.Property(x => x.Balance)
                    .HasBsonRepresentation(storageType)
                    .IsEncryptedForRange(0m, 100000m, dataKeyId,
                        e => e.WithPrecision(0).WithContention(4).WithSparsity(1).WithTrimFactor(2));
            });
        }
    }

    [QueryableEncryptionTheory]
    [InlineData(CryptProvider.Mongocryptd, BsonType.Decimal128)]
    [InlineData(CryptProvider.Mongocryptd, BsonType.Double)]
    [InlineData(CryptProvider.AutoEncryptSharedLibrary, BsonType.Decimal128)]
    [InlineData(CryptProvider.AutoEncryptSharedLibrary, BsonType.Double)]
    public void IsEncryptedForRange_without_required_min_max_float_throws(
        CryptProvider cryptProvider,
        BsonType storageType)
    {
        var dataKeyId = CreateDataKey();
        using var db = CreateContext(cryptProvider, mb =>
        {
            mb.Entity<Patient>()
                .ToUniqueCollection(values: [cryptProvider, storageType])
                .Property(p => p.MonthlySubscription)
                .HasBsonRepresentation(storageType)
                .IsEncryptedForRange(0, 10000m, dataKeyId)
                .HasAnnotation(MongoAnnotationNames.QueryableEncryptionRangeMax, null);
        });

        var ex = Assert.Throws<InvalidOperationException>(() => db.Model);
        Assert.Contains(nameof(Patient.MonthlySubscription), ex.Message);
        Assert.Contains(storageType.ToString(), ex.Message);
        Assert.Contains("range", ex.Message);
    }

    [QueryableEncryptionTheory]
    [InlineData(CryptProvider.Mongocryptd)]
    [InlineData(CryptProvider.AutoEncryptSharedLibrary)]
    public void IsEncryptedForRange_and_equality_queries_mixed(CryptProvider cryptProvider)
    {
        var dataKeyId1 = CreateDataKey();
        var dataKeyId2 = CreateDataKey();

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
                var expected = samplePatients.Single(p => p.SSN == actual.SSN && p.DateOfBirth == actual.DateOfBirth);
                Assert.Equal(expected.SSN, actual.SSN);
                Assert.Equal(expected.DateOfBirth, actual.DateOfBirth);
            }
        }

        {
            AssertCantReadEncrypted<Patient>(collectionName, "DateOfBirth");
        }

        void ModelConfig(ModelBuilder mb)
        {
            mb.Entity<Patient>(p =>
            {
                p.ToCollection(collectionName);
                p.Property(x => x.SSN).IsEncryptedForEquality(dataKeyId1);
                p.Property(x => x.DateOfBirth)
                    .IsEncryptedForRange(new DateTime(1900, 01, 01), new DateTime(2050, 12, 31), dataKeyId2);
            });
        }
    }

    private void AssertCantReadEncrypted<T>(string? collectionName, string? propertyName = null)
    {
        var collection = _database.MongoDatabase.GetCollection<T>(collectionName);
        var ex = Assert.Throws<FormatException>(() => collection.AsQueryable().ToList());
        Assert.Contains("deserializ", ex.Message);

        // The C# Driver doesn't detect encrypted values when unencrypted values were expected in all scenarios
        // so we get a few different messages depending on type of property.
        // Either way it should contain either the words "Encrypted" or "Binary".
        Assert.Contains(ex.Message.Contains("Encrypted") ? "Encrypted" : "Binary", ex.Message);

        if (propertyName != null)
        {
            Assert.Contains(propertyName, ex.Message);
        }
    }

    private static void SetPropertyEncryption<T>(PropertyBuilder<T> p, QueryableEncryptionType encryptionType, Guid dataKeyId)
    {
        switch (encryptionType)
        {
            case QueryableEncryptionType.NotQueryable:
                p.IsEncrypted(dataKeyId);
                break;
            case QueryableEncryptionType.Equality:
                p.IsEncryptedForEquality(dataKeyId);
                break;
            case QueryableEncryptionType.Range:
                p!.IsEncryptedForRange(default, default, dataKeyId);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(encryptionType), encryptionType, null);
        }
    }

    private MedicalContext CreateContext(
        CryptProvider cryptProvider,
        Action<ModelBuilder> modelBuilderAction,
        Func<MongoOptionsExtension, MongoOptionsExtension>? mongoOptionsConfigurator = null,
        Action<string>? logger = null)
    {
        var mongoOptions = new MongoOptionsExtension()
            .WithClientSettings(_database.Client.Settings)
            .WithDatabaseName(_database.MongoDatabase.DatabaseNamespace.DatabaseName)
            .WithKeyVaultNamespace(KeyVaultNamespace)
            .WithKmsProviders(KmsProviders);

        // Remove me once mongocryptd is fixed for Windows on latest
        if (cryptProvider == CryptProvider.Mongocryptd && AutoEncryptionTests.IsBuggyMongocryptd)
        {
            cryptProvider = CryptProvider.AutoEncryptSharedLibrary;
        }

        mongoOptions = cryptProvider switch
        {
            CryptProvider.AutoEncryptSharedLibrary => mongoOptions.WithCryptProvider(CryptProvider.AutoEncryptSharedLibrary,
                Environment.GetEnvironmentVariable("CRYPT_SHARED_LIB_PATH")),
            CryptProvider.Mongocryptd => mongoOptions.WithCryptProvider(CryptProvider.Mongocryptd,
                Environment.GetEnvironmentVariable("MONGODB_BINARIES")),
            _ => mongoOptions
        };

        if (mongoOptionsConfigurator != null)
        {
            mongoOptions = mongoOptionsConfigurator.Invoke(mongoOptions);
        }

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
        public long Sequence { get; set; }
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
