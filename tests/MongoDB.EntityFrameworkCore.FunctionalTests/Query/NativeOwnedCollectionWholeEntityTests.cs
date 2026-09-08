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

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.Infrastructure;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// EF-322 owned-collection slice: a whole-entity query over an entity with an owned COLLECTION navigation
/// (OwnsMany, auto-included eagerly) goes native and streams — the gate predicate
/// IsOwnedEmbeddedIncludeSelector admits the synthetic Select(x => IncludeExpression(x, ownedCollection)).
/// Owned data round-trips through the driver, so each case asserts Native == DriverLinq parity plus a
/// NativeOnly routing proof.
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeOwnedCollectionWholeEntityTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    private static SingleEntityDbContext<T> CreateContext<T>(
        IMongoCollection<T> collection, MongoQueryMode mode, Action<ModelBuilder>? modelBuilderAction = null)
        where T : class
        => SingleEntityDbContext.Create(
            collection,
            modelBuilderAction: modelBuilderAction,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });

    private string UniqueCollectionName(string name)
        => TemporaryDatabaseFixtureBase.CreateCollectionName(name) + Guid.NewGuid().ToString("N")[..8];

    private class Blog
    {
        public ObjectId Id { get; set; }
        public string Title { get; set; } = "";
        public List<Post> Posts { get; set; } = [];
    }

    private class Post
    {
        public string Heading { get; set; } = "";
        public int Rank { get; set; }
    }

    private static readonly Action<ModelBuilder> BlogModel = mb => mb.Entity<Blog>().OwnsMany(b => b.Posts);

    private IMongoCollection<Blog> Seed(string name, params BsonDocument[] docs)
    {
        var coll = database.MongoDatabase.GetCollection<BsonDocument>(UniqueCollectionName(name));
        coll.InsertMany(docs);
        return database.MongoDatabase.GetCollection<Blog>(coll.CollectionNamespace.CollectionName);
    }

    private static BsonDocument BlogDoc(string title, params (string heading, int rank)[] posts)
        => new()
        {
            { "_id", ObjectId.GenerateNewId() },
            { "Title", title },
            { "Posts", new BsonArray(posts.Select(p =>
                new BsonDocument { { "Heading", p.heading }, { "Rank", p.rank } })) }
        };

    // Runs the query under NativeOnly (routing proof) and under Native vs DriverLinq (parity), returning
    // the NativeOnly result for the caller to assert values on.
    private List<Blog> AssertNativeAndParity(IMongoCollection<Blog> collection, Func<IQueryable<Blog>, IQueryable<Blog>> query)
    {
        List<Blog> nativeOnly;
        using (var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel))
        {
            nativeOnly = query(db.Entities.AsNoTracking()).ToList();
        }

        List<Blog> driver;
        using (var db = CreateContext(collection, MongoQueryMode.DriverLinq, BlogModel))
        {
            driver = query(db.Entities.AsNoTracking()).ToList();
        }

        Assert.Equal(driver.Count, nativeOnly.Count);
        for (var i = 0; i < driver.Count; i++)
        {
            Assert.Equal(driver[i].Title, nativeOnly[i].Title);
            Assert.Equal(
                driver[i].Posts.Select(p => (p.Heading, p.Rank)),
                nativeOnly[i].Posts.Select(p => (p.Heading, p.Rank)));
        }

        return nativeOnly;
    }

    [Fact]
    public void Populated_owned_collection_goes_native_and_preserves_order()
    {
        var collection = Seed(nameof(Populated_owned_collection_goes_native_and_preserves_order),
            BlogDoc("Alpha", ("intro", 1), ("body", 2), ("outro", 3)));

        var blog = Assert.Single(AssertNativeAndParity(collection, q => q));

        Assert.Equal("Alpha", blog.Title);
        Assert.Equal(["intro", "body", "outro"], blog.Posts.Select(p => p.Heading));
        Assert.Equal([1, 2, 3], blog.Posts.Select(p => p.Rank));
    }

    [Fact]
    public void Empty_owned_collection_materializes_empty_list()
    {
        var collection = Seed(nameof(Empty_owned_collection_materializes_empty_list),
            BlogDoc("Empty"));

        var blog = Assert.Single(AssertNativeAndParity(collection, q => q));

        Assert.Equal("Empty", blog.Title);
        Assert.Empty(blog.Posts);
    }

    [Fact]
    public void Owned_collection_with_root_where_goes_native()
    {
        var collection = Seed(nameof(Owned_collection_with_root_where_goes_native),
            BlogDoc("Alpha", ("a", 1)), BlogDoc("Beta", ("b", 2)));

        var blog = Assert.Single(AssertNativeAndParity(collection, q => q.Where(b => b.Title == "Beta")));

        Assert.Equal("Beta", blog.Title);
        Assert.Equal(["b"], blog.Posts.Select(p => p.Heading));
    }

    // ── Mixed owned reference + owned collection on the same entity: the whole chain now goes native ──

    private class Shop
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
        public ShopAddress Address { get; set; } = null!;
        public List<Item> Items { get; set; } = [];
    }

    private class ShopAddress
    {
        public string City { get; set; } = "";
    }

    private class Item
    {
        public string Sku { get; set; } = "";
    }

    private static readonly Action<ModelBuilder> ShopModel = mb =>
    {
        mb.Entity<Shop>().OwnsOne(s => s.Address);
        mb.Entity<Shop>().OwnsMany(s => s.Items);
    };

    [Fact]
    public void Mixed_owned_reference_and_owned_collection_goes_native()
    {
        var coll = database.MongoDatabase.GetCollection<BsonDocument>(
            UniqueCollectionName(nameof(Mixed_owned_reference_and_owned_collection_goes_native)));
        coll.InsertOne(new BsonDocument
        {
            { "_id", ObjectId.GenerateNewId() }, { "Name", "Acme" },
            { "Address", new BsonDocument { { "City", "NYC" } } },
            { "Items", new BsonArray { new BsonDocument("Sku", "A1"), new BsonDocument("Sku", "B2") } }
        });
        var collection = database.MongoDatabase.GetCollection<Shop>(coll.CollectionNamespace.CollectionName);

        // Routing proof: NativeOnly succeeds (mixed owned-ref + owned-collection chain admitted as a whole).
        using (var native = CreateContext(collection, MongoQueryMode.NativeOnly, ShopModel))
        {
            var shop = Assert.Single(native.Entities.AsNoTracking().ToList());
            Assert.Equal("NYC", shop.Address.City);
            Assert.Equal(["A1", "B2"], shop.Items.Select(i => i.Sku));
        }

        // Parity with driver-LINQ.
        using var driver = CreateContext(collection, MongoQueryMode.DriverLinq, ShopModel);
        var shopD = Assert.Single(driver.Entities.AsNoTracking().ToList());
        Assert.Equal("NYC", shopD.Address.City);
        Assert.Equal(["A1", "B2"], shopD.Items.Select(i => i.Sku));
    }

    // ── Nested owned reference inside a collection element ──

    private class Team
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
        public List<Member> Members { get; set; } = [];
    }

    private class Member
    {
        public string Name { get; set; } = "";
        public Badge Badge { get; set; } = null!;
    }

    private class Badge
    {
        public string Code { get; set; } = "";
    }

    private static readonly Action<ModelBuilder> TeamModel = mb =>
        mb.Entity<Team>().OwnsMany(t => t.Members, m => m.OwnsOne(x => x.Badge));

    [Fact]
    public void Owned_collection_element_with_nested_owned_reference_goes_native()
    {
        var coll = database.MongoDatabase.GetCollection<BsonDocument>(
            UniqueCollectionName(nameof(Owned_collection_element_with_nested_owned_reference_goes_native)));
        coll.InsertOne(new BsonDocument
        {
            { "_id", ObjectId.GenerateNewId() }, { "Name", "Red" },
            { "Members", new BsonArray
                {
                    new BsonDocument { { "Name", "Ann" }, { "Badge", new BsonDocument("Code", "X1") } },
                    new BsonDocument { { "Name", "Bob" }, { "Badge", new BsonDocument("Code", "X2") } }
                }
            }
        });
        var collection = database.MongoDatabase.GetCollection<Team>(coll.CollectionNamespace.CollectionName);

        using (var native = CreateContext(collection, MongoQueryMode.NativeOnly, TeamModel))
        {
            var team = Assert.Single(native.Entities.AsNoTracking().ToList());
            Assert.Equal(["Ann", "Bob"], team.Members.Select(m => m.Name));
            Assert.Equal(["X1", "X2"], team.Members.Select(m => m.Badge.Code));
        }

        using var driver = CreateContext(collection, MongoQueryMode.DriverLinq, TeamModel);
        var teamD = Assert.Single(driver.Entities.AsNoTracking().ToList());
        Assert.Equal(["Ann", "Bob"], teamD.Members.Select(m => m.Name));
        Assert.Equal(["X1", "X2"], teamD.Members.Select(m => m.Badge.Code));
    }

    // ── Collection-of-collection: still correct, via DOM (streaming-ineligible) ──

    private class Library
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
        public List<Shelf> Shelves { get; set; } = [];
    }

    private class Shelf
    {
        public string Label { get; set; } = "";
        public List<Book> Books { get; set; } = [];
    }

    private class Book
    {
        public string Title { get; set; } = "";
    }

    private static readonly Action<ModelBuilder> LibraryModel = mb =>
        mb.Entity<Library>().OwnsMany(l => l.Shelves, s => s.OwnsMany(x => x.Books));

    [Fact]
    public void Collection_of_collection_goes_native_via_dom_and_materializes_correctly()
    {
        var coll = database.MongoDatabase.GetCollection<BsonDocument>(
            UniqueCollectionName(nameof(Collection_of_collection_goes_native_via_dom_and_materializes_correctly)));
        coll.InsertOne(new BsonDocument
        {
            { "_id", ObjectId.GenerateNewId() }, { "Name", "Main" },
            { "Shelves", new BsonArray
                {
                    new BsonDocument
                    {
                        { "Label", "SciFi" },
                        { "Books", new BsonArray { new BsonDocument("Title", "Dune") } }
                    }
                }
            }
        });
        var collection = database.MongoDatabase.GetCollection<Library>(coll.CollectionNamespace.CollectionName);

        using (var native = CreateContext(collection, MongoQueryMode.NativeOnly, LibraryModel))
        {
            var lib = Assert.Single(native.Entities.AsNoTracking().ToList());
            var shelf = Assert.Single(lib.Shelves);
            Assert.Equal("SciFi", shelf.Label);
            Assert.Equal(["Dune"], shelf.Books.Select(b => b.Title));
        }

        using var driver = CreateContext(collection, MongoQueryMode.DriverLinq, LibraryModel);
        var libD = Assert.Single(driver.Entities.AsNoTracking().ToList());
        Assert.Equal(["Dune"], Assert.Single(libD.Shelves).Books.Select(b => b.Title));
    }
}
