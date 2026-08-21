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

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Metadata.Conventions;

[XUnitCollection("ConventionsTests")]
public class MongoPrimaryKeyDiscoveryConventionTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    class UnderscoreIdNamedProperty
    {
        public ObjectId _id { get; set; }

        public string name { get; set; }
    }

    class IdNamedProperty
    {
        public ObjectId Id { get; set; }

        public string name { get; set; }
    }

    class ColumnAttributedIdProperty
    {
        [Column("_id")] public ObjectId MyPrimaryKey { get; set; }

        public string name { get; set; }
    }

    class Product
    {
        public string ProductId { get; set; }
        public string name { get; set; }
    }

    class StoredProduct
    {
        public string _id { get; set; }
        public string name { get; set; }
    }

    [Fact]
    public void PrimaryKeyDiscovery_discovers_underscore_id_named_property()
    {
        var collection = database.CreateCollection<UnderscoreIdNamedProperty>();

        var id = ObjectId.GenerateNewId();
        var name = Guid.NewGuid().ToString();

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entities.Add(new UnderscoreIdNamedProperty {_id = id, name = name});
            db.SaveChanges();
        }

        {
            // Find with CSharpDriver
            var actual = collection.Database.GetCollection<UnderscoreIdNamedProperty>(collection.CollectionNamespace
                .CollectionName);
            var directFound = actual.Find(f => f._id == id).Single();
            Assert.Equal(name, directFound.name);
        }

        {
            // Find with EF
            using var db = SingleEntityDbContext.Create(collection);
            var found = db.Entities.Single(f => f._id == id);
            Assert.Equal(name, found.name);
        }
    }

    [Fact]
    public void PrimaryKeyDiscovery_discovers_Id_named_property()
    {
        var collection = database.CreateCollection<IdNamedProperty>();

        var id = ObjectId.GenerateNewId();
        var name = Guid.NewGuid().ToString();

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entities.Add(new IdNamedProperty {Id = id, name = name});
            db.SaveChanges();
        }

        {
            // Find with CSharpDriver
            var actual = collection.Database.GetCollection<IdNamedProperty>(collection.CollectionNamespace.CollectionName);
            var directFound = actual.Find(f => f.Id == id).Single();
            Assert.Equal(name, directFound.name);
        }

        {
            // Find with EF
            using var db = SingleEntityDbContext.Create(collection);
            var found = db.Entities.Single(f => f.Id == id);
            Assert.Equal(name, found.name);
        }
    }

    [Fact]
    public void PrimaryKeyDiscovery_discovers_ColumnAttributed_named_property()
    {
        var collection = database.CreateCollection<ColumnAttributedIdProperty>();

        var id = ObjectId.GenerateNewId();
        var name = Guid.NewGuid().ToString();

        {
            using var db = SingleEntityDbContext.Create(collection);
            var entity = new ColumnAttributedIdProperty {MyPrimaryKey = id, name = name};
            db.Entities.Add(entity);
            db.SaveChanges();
        }

        {
            // Find with CSharpDriver
            var actual = collection.Database.GetCollection<UnderscoreIdNamedProperty>(collection.CollectionNamespace
                .CollectionName);
            var found = actual.Find(f => f._id == id).Single();
            Assert.Equal(name, found.name);
        }

        {
            // Find with EF
            using var db = SingleEntityDbContext.Create(collection);
            var found = db.Entities.Single(f => f.MyPrimaryKey == id);
            Assert.Equal(name, found.name);
        }
    }

    [Fact]
    public void PrimaryKeyDiscovery_discovers_EntityId_named_property()
    {
        var collection = database.CreateCollection<Product>();

        var id = Guid.NewGuid().ToString();
        var name = Guid.NewGuid().ToString();

        {
            using var db = SingleEntityDbContext.Create(collection);
            var entity = new Product {ProductId = id, name = name};
            db.Entities.Add(entity);
            db.SaveChanges();
        }

        {
            // Find with CSharpDriver
            var actual = collection.Database.GetCollection<StoredProduct>(collection.CollectionNamespace.CollectionName);
            var found = actual.Find(f => f._id == id).Single();
            Assert.Equal(name, found.name);
        }

        {
            // Find with EF
            using var db = SingleEntityDbContext.Create(collection);
            var found = db.Entities.Single(f => f.ProductId == id);
            Assert.Equal(name, found.name);
        }
    }

    [Fact]
    public void Many_to_many_join_document_has_no_duplicate_shadow_property()
    {
        var fruitsCollection = TemporaryDatabaseFixtureBase.CreateCollectionName("Fruits") + Guid.NewGuid().ToString("N")[..8];
        var jamsCollection = TemporaryDatabaseFixtureBase.CreateCollectionName("Jams") + Guid.NewGuid().ToString("N")[..8];
        var joinCollection = TemporaryDatabaseFixtureBase.CreateCollectionName("FruitJam") + Guid.NewGuid().ToString("N")[..8];

        var fruit = new Fruit {Id = Guid.NewGuid(), Name = "Apple"};
        var jam = new Jam {Id = Guid.NewGuid(), Name = "Strawberry"};
        fruit.Jams.Add(jam);

        using (var db = new FruitJamDbContext(database, fruitsCollection, jamsCollection, joinCollection))
        {
            db.Fruits.Add(fruit);
            db.SaveChanges();
        }

        var joinDocuments = database.MongoDatabase.GetCollection<BsonDocument>(joinCollection)
            .Find(FilterDefinition<BsonDocument>.Empty).ToList();

        Assert.Single(joinDocuments);

        // The composite key {FruitsId, JamsId} is nested under the single top-level "_id" field,
        // each property keeping its own natural element name.
        var document = joinDocuments[0];
        Assert.Equal(["_id"], document.Names.ToArray());

        var idDocument = document["_id"].AsBsonDocument;
        Assert.Equal(["FruitsId", "JamsId"], idDocument.Names.OrderBy(n => n).ToArray());
    }

    class Fruit
    {
        public Guid Id { get; set; }
        public string Name { get; set; }
        public List<Jam> Jams { get; } = new();
    }

    class Jam
    {
        public Guid Id { get; set; }
        public string Name { get; set; }
        public List<Fruit> Fruits { get; } = new();
    }

    class FruitJamDbContext : DbContext
    {
        private readonly string _fruitsCollection;
        private readonly string _jamsCollection;
        private readonly string _joinCollection;

        public DbSet<Fruit> Fruits { get; set; }
        public DbSet<Jam> Jams { get; set; }

        public FruitJamDbContext(
            TemporaryDatabaseFixture database,
            string fruitsCollection,
            string jamsCollection,
            string joinCollection)
            : base(new DbContextOptionsBuilder<FruitJamDbContext>()
                .UseMongoDB(database.Client, database.MongoDatabase.DatabaseNamespace.DatabaseName)
                .ReplaceService<IModelCacheKeyFactory, IgnoreCacheKeyFactory>()
                .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
                .Options)
        {
            _fruitsCollection = fruitsCollection;
            _jamsCollection = jamsCollection;
            _joinCollection = joinCollection;
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<Fruit>().ToCollection(_fruitsCollection);
            modelBuilder.Entity<Jam>().ToCollection(_jamsCollection);
            modelBuilder.Entity<Fruit>()
                .HasMany(f => f.Jams)
                .WithMany(j => j.Fruits)
                .UsingEntity(j => j.ToCollection(_joinCollection));
        }

        sealed class IgnoreCacheKeyFactory : IModelCacheKeyFactory
        {
            private static int _count;
            public object Create(DbContext context, bool designTime)
                => Interlocked.Increment(ref _count);
        }
    }
}
