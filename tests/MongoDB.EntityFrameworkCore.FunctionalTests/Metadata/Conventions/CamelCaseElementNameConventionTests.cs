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

using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.Metadata.Conventions;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Metadata.Conventions;

[XUnitCollection("ConventionsTests")]
public class CamelCaseElementNameConventionTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    class CamelCaseDbContext<T>(IMongoCollection<T> collection) : DbContext where T : class
    {
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
            => optionsBuilder
                .ReplaceService<IModelCacheKeyFactory, IgnoreCacheKeyFactory>()
                .UseMongoDB(collection.Database.Client, collection.Database.DatabaseNamespace.DatabaseName)
                .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));

        protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
        {
            configurationBuilder.Conventions.Add(_ => new CamelCaseElementNameConvention());
            configurationBuilder.Conventions.Add(_ => new CamelCaseElementNameConvention());
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<T>().ToCollection(collection.CollectionNamespace.CollectionName);
    }

    class IntendedStorageEntity
    {
        public ObjectId _id { get; set; }

        public string unchanged { get; set; }
        public string alsoUnchanged { get; set; }
        public string lowercaseFirstWord { get; set; }
        public string removeUnderscores { get; set; }
        public string treatUpperCase { get; set; }
        public string numeric123Separator { get; set; }
        public IntendedStoragesSubDoc ownedEntity1 { get; set; }
        public IntendedStoragesSubDoc ownedEntity2 { get; set; }

        public List<IntendedStoragesSubDoc> ownedItems { get; set; }
    }

    class IntendedStoragesSubDoc
    {
        public string subUnchanged { get; set; }
        public string subChanged { get; set; }
    }

    class RemappedEntity
    {
        [Column("_id")] public ObjectId _id { get; set; }

        public string unchanged { get; set; }
        public string alsoUnchanged { get; set; }
        public string LowercaseFirstWord { get; set; }
        public string remove_underscores { get; set; }
        public string treatUPPERCase { get; set; }
        public string numeric123separator { get; set; }
        public OwnedEntity OwnedEntity1 { get; set; }
        public OwnedEntity owned_entity_2 { get; set; }
        public List<OwnedEntity> owned_items { get; set; }
    }

    class OwnedEntity
    {
        public string subUnchanged { get; set; }
        public string SubChanged { get; set; }
    }

    [Fact]
    public void CamelCase_redefines_element_name_for_insert_and_query()
    {
        var collection = database.CreateCollection<RemappedEntity>();

        var id = ObjectId.GenerateNewId();
        const string unchangedText = "Unchanged as is a single already-lowercase word";
        const string alsoUnchangedText = "Unchanged as is already fully camel cased";
        const string changedLowerText = "Changed as first word needs to be lower cased";
        const string underscoredText = "Changed as underscores need removing and second word capitalizing";
        const string treatUpperText = "Treated UPPER as a separate word and title cased it";
        const string numericText = "Treated 123 as part of numeric and title cased word after";
        const string subUnchanged1 = "Unchanged as is already fully camel cased inside an owned entity";
        const string subChangedText1 = "Changed as first word needs to be lower cased inside an owned entity";
        const string subUnchanged2 = "Unchanged2 as is already fully camel cased inside an owned entity";
        const string subChangedText2 = "Changed2 as first word needs to be lower cased inside an owned entity";

        {
            using var db = new CamelCaseDbContext<RemappedEntity>(collection);
            db.Add(new RemappedEntity
            {
                _id = id,
                unchanged = unchangedText,
                alsoUnchanged = alsoUnchangedText,
                LowercaseFirstWord = changedLowerText,
                remove_underscores = underscoredText,
                treatUPPERCase = treatUpperText,
                numeric123separator = numericText,
                OwnedEntity1 = new OwnedEntity {subUnchanged = subUnchanged1, SubChanged = subChangedText1},
                owned_entity_2 = new OwnedEntity {subUnchanged = subUnchanged2, SubChanged = subChangedText2},
                owned_items =
                [
                    new OwnedEntity {subUnchanged = subUnchanged1, SubChanged = subChangedText1},
                    new OwnedEntity {subUnchanged = subUnchanged2, SubChanged = subChangedText2}
                ]
            });
            db.SaveChanges();
        }

        {
            var actual = collection.Database.GetCollection<IntendedStorageEntity>(collection.CollectionNamespace.CollectionName);
            var directFound = actual.Find(f => f._id == id).Single();
            Assert.Equal(unchangedText, directFound.unchanged);
            Assert.Equal(alsoUnchangedText, directFound.alsoUnchanged);
            Assert.Equal(changedLowerText, directFound.lowercaseFirstWord);
            Assert.Equal(underscoredText, directFound.removeUnderscores);
            Assert.Equal(treatUpperText, directFound.treatUpperCase);
            Assert.Equal(numericText, directFound.numeric123Separator);
            Assert.Equal(subUnchanged1, directFound.ownedEntity1.subUnchanged);
            Assert.Equal(subChangedText1, directFound.ownedEntity1.subChanged);
            Assert.Equal(subUnchanged2, directFound.ownedEntity2.subUnchanged);
            Assert.Equal(subChangedText2, directFound.ownedEntity2.subChanged);
        }
    }

    private class DbPreserves(IMongoCollection<Preserves> collection)
        : CamelCaseDbContext<Preserves>(collection)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<Marmalade>();
            modelBuilder.Entity<Jam>();
        }
    }

    private class DbPreservesExplicitTph(IMongoCollection<Preserves> collection)
        : CamelCaseDbContext<Preserves>(collection)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<Preserves>()
                .HasDiscriminator<string>("_t")
                .HasValue<Marmalade>("Md")
                .HasValue<Jam>("Jm");
        }
    }

    private class DbPreservesExplicitElementName(IMongoCollection<Preserves> collection)
        : CamelCaseDbContext<Preserves>(collection)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<Preserves>().Property<string>("Discriminator").HasElementName("D");
            modelBuilder.Entity<Marmalade>();
            modelBuilder.Entity<Jam>();
        }
    }

    private abstract class Preserves
    {
        public ObjectId Id { get; set; }
        public string Fruit { get; set; }
    }

    private class Marmalade : Preserves
    {
        public bool ThickCut { get; set; }
    }

    private class Jam : Preserves
    {
        public Seeds Seeds { get; set; }
    }

    private enum Seeds
    {
        None,
        Some,
        Lots
    }

    [Theory] // EF-285
    [InlineData(false)]
    [InlineData(true)]
    public async Task CamelCase_convention_ignores_special_names(bool async)
    {
        var collection = database.CreateCollection<Preserves>(values: async);

        await using var db = new DbPreserves(collection);
        await DiscriminatorTest(db, collection, async, "_t", "Jam", "Marmalade");
    }

    [Theory] // EF-285
    [InlineData(false)]
    [InlineData(true)]
    public async Task CamelCase_convention_ignores_special_names_when_inheritance_explicitly_configured(bool async)
    {
        var collection = database.CreateCollection<Preserves>(values: async);

        await using var db = new DbPreservesExplicitTph(collection);
        await DiscriminatorTest(db, collection, async, "_t", "Jm", "Md");
    }

    [Theory] // EF-285
    [InlineData(false)]
    [InlineData(true)]
    public async Task CamelCase_convention_ignores_explicitly_configured_special_names(bool async)
    {
        var collection = database.CreateCollection<Preserves>(values: async);

        await using var db = new DbPreservesExplicitElementName(collection);
        await DiscriminatorTest(db, collection, async, "D", "Jam", "Marmalade");
    }

    private static async Task DiscriminatorTest(
        DbContext db, IMongoCollection<Preserves> collection, bool async,
        string discriminatorName, BsonValue jamValue, BsonValue marmaladeValue)
    {
        db.AddRange(
            new Jam { Fruit = "Strawberry", Seeds = Seeds.Some },
            new Marmalade { Fruit = "Orange", ThickCut = true });

        _ = async ? await db.SaveChangesAsync() : db.SaveChanges();

        var savedDocuments = collection.Database.GetCollection<BsonDocument>(collection.CollectionNamespace.CollectionName)
            .AsQueryable().ToList()
            .OrderBy(d => d[discriminatorName].AsString).ToList();

        Assert.Equal(2, savedDocuments.Count);

        savedDocuments[0].Contains("_id");
        Assert.Equal(jamValue, savedDocuments[0].GetValue(discriminatorName));

        savedDocuments[1].Contains("_id");
        Assert.Equal(marmaladeValue, savedDocuments[1].GetValue(discriminatorName));
    }
}
