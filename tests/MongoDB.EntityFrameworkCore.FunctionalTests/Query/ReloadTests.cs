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
using MongoDB.Bson;
using MongoDB.Driver;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// Regression tests for EF-192: <c>context.Entry(entity).ReloadAsync()</c> threw
/// <see cref="ArgumentNullException"/> ("Value cannot be null. (Parameter 'expression')")
/// from <c>MongoProjectionBindingExpressionVisitor.VisitNewArray</c> when the reloaded
/// entity had an array- or collection-shaped property. Reload re-queries the entity and
/// re-materialises every property, so any array projection flows through <c>VisitNewArray</c>.
/// The ticket was blocked on CSHARP-5516 (polymorphic array creation in LINQ), now closed.
/// </summary>
[XUnitCollection("QueryTests")]
public class ReloadTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    private record GeoPoint
    {
        public decimal latitude { get; set; }
        public decimal longitude { get; set; }
    }

    private record Owner
    {
        public ObjectId _id { get; set; }
        public string name { get; set; } = null!;
        public List<int> numbers { get; set; } = null!;
        public string[] tags { get; set; } = null!;
        public List<GeoPoint> points { get; set; } = null!;
    }

    private static readonly Action<ModelBuilder> ConfigureOwner = mb =>
        mb.Entity<Owner>().OwnsMany(e => e.points);

    [Fact]
    public async Task ReloadAsync_refreshes_entity_with_array_and_owned_collection_properties()
    {
        var collection = database.CreateCollection<Owner>();
        var id = ObjectId.GenerateNewId();

        using var db = SingleEntityDbContext.Create(collection, ConfigureOwner);
        var owner = new Owner
        {
            _id = id,
            name = "before",
            numbers = [1, 2, 3],
            tags = ["a", "b"],
            points = [new GeoPoint { latitude = 1.5m, longitude = 2.5m }]
        };
        db.Entities.Add(owner);
        await db.SaveChangesAsync();

        // Mutate the stored document out-of-band, as a background service would.
        var raw = database.GetCollection<BsonDocument>(collection.CollectionNamespace);
        await raw.UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", id),
            Builders<BsonDocument>.Update
                .Set("name", "after")
                .Set("numbers", new BsonArray(new[] { 4, 5, 6, 7 })));

        // EF-192: this threw ArgumentNullException in MongoProjectionBindingExpressionVisitor.VisitNewArray.
        await db.Entry(owner).ReloadAsync();

        Assert.Equal("after", owner.name);
        Assert.Equal(new[] { 4, 5, 6, 7 }, owner.numbers);
        Assert.Equal(new[] { "a", "b" }, owner.tags);
        Assert.Equal(1.5m, owner.points.Single().latitude);
    }

    private record PrimitiveListOwner
    {
        public ObjectId _id { get; set; }
        public List<int> numbers { get; set; } = null!;
    }

    [Fact]
    public async Task ReloadAsync_refreshes_entity_with_primitive_list()
    {
        var collection = database.CreateCollection<PrimitiveListOwner>();
        var id = ObjectId.GenerateNewId();

        using var db = SingleEntityDbContext.Create(collection);
        var owner = new PrimitiveListOwner { _id = id, numbers = [1, 2, 3] };
        db.Entities.Add(owner);
        await db.SaveChangesAsync();

        var raw = database.GetCollection<BsonDocument>(collection.CollectionNamespace);
        await raw.UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", id),
            Builders<BsonDocument>.Update.Set("numbers", new BsonArray(new[] { 4, 5, 6, 7 })));

        await db.Entry(owner).ReloadAsync();

        Assert.Equal(new[] { 4, 5, 6, 7 }, owner.numbers);
    }

    private record PrimitiveArrayOwner
    {
        public ObjectId _id { get; set; }
        public string[] tags { get; set; } = null!;
    }

    [Fact]
    public async Task ReloadAsync_refreshes_entity_with_primitive_array()
    {
        var collection = database.CreateCollection<PrimitiveArrayOwner>();
        var id = ObjectId.GenerateNewId();

        using var db = SingleEntityDbContext.Create(collection);
        var owner = new PrimitiveArrayOwner { _id = id, tags = ["x", "y", "z"] };
        db.Entities.Add(owner);
        await db.SaveChangesAsync();

        var raw = database.GetCollection<BsonDocument>(collection.CollectionNamespace);
        await raw.UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", id),
            Builders<BsonDocument>.Update.Set("tags", new BsonArray(new[] { "p", "q" })));

        await db.Entry(owner).ReloadAsync();

        Assert.Equal(new[] { "p", "q" }, owner.tags);
    }

    private record OwnedCollectionOwner
    {
        public ObjectId _id { get; set; }
        public List<GeoPoint> points { get; set; } = null!;
    }

    [Fact]
    public async Task ReloadAsync_succeeds_for_entity_with_owned_collection()
    {
        // EF Core does not refresh owned-entity values on Reload — only the root entity's
        // scalar properties are updated. This test verifies Reload does not throw.
        var collection = database.CreateCollection<OwnedCollectionOwner>();
        var id = ObjectId.GenerateNewId();

        using var db = SingleEntityDbContext.Create(collection,
            mb => mb.Entity<OwnedCollectionOwner>().OwnsMany(e => e.points));
        var owner = new OwnedCollectionOwner
        {
            _id = id,
            points = [new GeoPoint { latitude = 1.5m, longitude = 2.5m }]
        };
        db.Entities.Add(owner);
        await db.SaveChangesAsync();

        await db.Entry(owner).ReloadAsync();

        Assert.Equal(1.5m, owner.points.Single().latitude);
    }

    // Synchronous Reload() goes through the same projection path (QueryingEnumerable's
    // sync enumerator) as ReloadAsync(), so it shares the EF-192 root cause and is
    // covered in parallel here.

    [Fact]
    public void Reload_refreshes_entity_with_array_and_owned_collection_properties()
    {
        var collection = database.CreateCollection<Owner>();
        var id = ObjectId.GenerateNewId();

        using var db = SingleEntityDbContext.Create(collection, ConfigureOwner);
        var owner = new Owner
        {
            _id = id,
            name = "before",
            numbers = [1, 2, 3],
            tags = ["a", "b"],
            points = [new GeoPoint { latitude = 1.5m, longitude = 2.5m }]
        };
        db.Entities.Add(owner);
        db.SaveChanges();

        var raw = database.GetCollection<BsonDocument>(collection.CollectionNamespace);
        raw.UpdateOne(
            Builders<BsonDocument>.Filter.Eq("_id", id),
            Builders<BsonDocument>.Update
                .Set("name", "after")
                .Set("numbers", new BsonArray(new[] { 4, 5, 6, 7 })));

        db.Entry(owner).Reload();

        Assert.Equal("after", owner.name);
        Assert.Equal(new[] { 4, 5, 6, 7 }, owner.numbers);
        Assert.Equal(new[] { "a", "b" }, owner.tags);
        Assert.Equal(1.5m, owner.points.Single().latitude);
    }

    [Fact]
    public void Reload_refreshes_entity_with_primitive_list()
    {
        var collection = database.CreateCollection<PrimitiveListOwner>();
        var id = ObjectId.GenerateNewId();

        using var db = SingleEntityDbContext.Create(collection);
        var owner = new PrimitiveListOwner { _id = id, numbers = [1, 2, 3] };
        db.Entities.Add(owner);
        db.SaveChanges();

        var raw = database.GetCollection<BsonDocument>(collection.CollectionNamespace);
        raw.UpdateOne(
            Builders<BsonDocument>.Filter.Eq("_id", id),
            Builders<BsonDocument>.Update.Set("numbers", new BsonArray(new[] { 4, 5, 6, 7 })));

        db.Entry(owner).Reload();

        Assert.Equal(new[] { 4, 5, 6, 7 }, owner.numbers);
    }

    [Fact]
    public void Reload_refreshes_entity_with_primitive_array()
    {
        var collection = database.CreateCollection<PrimitiveArrayOwner>();
        var id = ObjectId.GenerateNewId();

        using var db = SingleEntityDbContext.Create(collection);
        var owner = new PrimitiveArrayOwner { _id = id, tags = ["x", "y", "z"] };
        db.Entities.Add(owner);
        db.SaveChanges();

        var raw = database.GetCollection<BsonDocument>(collection.CollectionNamespace);
        raw.UpdateOne(
            Builders<BsonDocument>.Filter.Eq("_id", id),
            Builders<BsonDocument>.Update.Set("tags", new BsonArray(new[] { "p", "q" })));

        db.Entry(owner).Reload();

        Assert.Equal(new[] { "p", "q" }, owner.tags);
    }

    [Fact]
    public void Reload_succeeds_for_entity_with_owned_collection()
    {
        // EF Core does not refresh owned-entity values on Reload — only the root entity's
        // scalar properties are updated. This test verifies Reload does not throw.
        var collection = database.CreateCollection<OwnedCollectionOwner>();
        var id = ObjectId.GenerateNewId();

        using var db = SingleEntityDbContext.Create(collection,
            mb => mb.Entity<OwnedCollectionOwner>().OwnsMany(e => e.points));
        var owner = new OwnedCollectionOwner
        {
            _id = id,
            points = [new GeoPoint { latitude = 1.5m, longitude = 2.5m }]
        };
        db.Entities.Add(owner);
        db.SaveChanges();

        db.Entry(owner).Reload();

        Assert.Equal(1.5m, owner.points.Single().latitude);
    }
}
