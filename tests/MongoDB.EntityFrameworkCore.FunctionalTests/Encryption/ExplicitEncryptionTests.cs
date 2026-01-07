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
using MongoDB.Driver.Encryption;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Encryption;

[XUnitCollection("Encryption")]
public sealed class ExplicitEncryptionTests : EncryptionTestsBase, IDisposable
{
    private readonly TemporaryDatabaseFixture _database;
    private readonly MongoClient _explicitClient;
    private ClientEncryption? _clientEncryption;

    public ExplicitEncryptionTests(TemporaryDatabaseFixture database) : base(database)
    {
        _database = database;
        var settings = _database.Client.Settings.Clone();
        settings.AutoEncryptionOptions = new AutoEncryptionOptions(
            keyVaultNamespace: KeyVaultNamespace,
            kmsProviders: KmsProviders,
            bypassAutoEncryption: true);

        _explicitClient = new MongoClient(settings);
        _clientEncryption = new ClientEncryption(new ClientEncryptionOptions(_explicitClient, KeyVaultNamespace, KmsProviders));
    }

    [Fact]
    public void Explicit_encrypted_properties_round_trip_with_encryption()
    {
        var collectionName = TemporaryDatabaseFixture.CreateCollectionName();

        // Generate unencrypted cards and some data keys
        var unencryptedCards = CreateUnencryptedCards();
        var cardNumberKey = CreateDataKey();
        var securityCodeKey = CreateDataKey();

        // Write encrypted cards to the database
        using (var db = CreateContext())
        {
            db.Database.EnsureCreated();
            db.Cards.AddRange(CreateEncryptedCards(unencryptedCards, cardNumberKey, securityCodeKey));
            db.SaveChanges();
        }

        // Read and compare encrypted cards with original unencrypted cards
        using (var db = CreateContext())
        {
            var actualCards = db.Cards.ToList();
            Assert.Equal(unencryptedCards[0].Id, actualCards[0].Id);
            Assert.Equal(unencryptedCards[0].Number, DecryptString(actualCards[0].Number));
            Assert.Equal(unencryptedCards[0].SecurityCode, DecryptString(actualCards[0].SecurityCode));
        }

        // TODO: Ensure they can not be read without key

        BillingContext CreateContext() =>
            this.CreateContext(mb =>
            {
                mb.Entity<EncryptedCard>().ToCollection(collectionName);
            });
    }

    private BillingContext CreateContext(
        Action<ModelBuilder> modelBuilderAction,
        Func<MongoOptionsExtension, MongoOptionsExtension>? mongoOptionsConfigurator = null,
        Action<string>? logger = null)
    {
        var mongoOptions = new MongoOptionsExtension()
            .WithClientSettings(_explicitClient.Settings)
            .WithDatabaseName(_database.MongoDatabase.DatabaseNamespace.DatabaseName)
            .WithKeyVaultNamespace(KeyVaultNamespace)
            .WithKmsProviders(KmsProviders);

        if (mongoOptionsConfigurator != null)
        {
            mongoOptions = mongoOptionsConfigurator.Invoke(mongoOptions);
        }

        var optionsBuilder = new DbContextOptionsBuilder<BillingContext>()
            .UseMongoDB(mongoOptions)
            .ReplaceService<IModelCacheKeyFactory, IgnoreCacheKeyFactory>()
            .LogTo(l => logger?.Invoke(l))
            .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));

        return new BillingContext(optionsBuilder.Options, modelBuilderAction);
    }

    public class BillingContext(DbContextOptions options, Action<ModelBuilder> modelBuilder)
        : DbContext(options)
    {
        public DbSet<EncryptedCard> Cards { get; set; }

        protected override void OnModelCreating(ModelBuilder mb)
        {
            base.OnModelCreating(mb);
            mb.Entity<EncryptedCard>().HasKey(k => k.Id);
            modelBuilder.Invoke(mb);
        }
    }

    public class UnencryptedCard
    {
        public ObjectId Id { get; set; }

        public string Number { get; set; }
        public string SecurityCode { get; set; }
    }

    public class EncryptedCard
    {
        public ObjectId Id { get; set; }

        public byte[] Number { get; set; }
        public byte[] SecurityCode { get; set; }
    }

    private BsonBinaryData EncryptString(string value, Guid key, bool deterministic)
        => _clientEncryption.Encrypt(new BsonString(value),
            new EncryptOptions(
                algorithm: deterministic
                    ? EncryptionAlgorithm.AEAD_AES_256_CBC_HMAC_SHA_512_Deterministic
                    : EncryptionAlgorithm.AEAD_AES_256_CBC_HMAC_SHA_512_Random, keyId: key), CancellationToken.None);

    private string DecryptString(byte[] encryptedValue)
        => _clientEncryption.Decrypt(new BsonBinaryData(encryptedValue, BsonBinarySubType.Encrypted), CancellationToken.None).AsString;

    public List<EncryptedCard> CreateEncryptedCards(
        List<UnencryptedCard> unencryptedCards,
        Guid cardNumberKey,
        Guid securityCodeKey) =>
        unencryptedCards.Select(uc => new EncryptedCard
            {
                Id = uc.Id,
                Number = EncryptString(uc.Number, cardNumberKey, true).Bytes,
                SecurityCode = EncryptString(uc.SecurityCode, securityCodeKey, false).Bytes,
            })
            .ToList();

    public static List<UnencryptedCard> CreateUnencryptedCards() =>
        Enumerable.Range(1, 1)
            .Select(i => new UnencryptedCard
            {
                Id = ObjectId.Parse("692485213351a76d1fbe0c62"),
                Number = $"{i}{i}{i}{i}",
                SecurityCode = $"{i}-{i}-{i}",
            })
            .ToList();

    public void Dispose()
    {
        if (_clientEncryption == null) return;

        _clientEncryption.Dispose();
        _clientEncryption = null;
    }
}
