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
using MongoDB.EntityFrameworkCore.Diagnostics;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Infrastructure;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// EF-322 Task 2 gate tests: a whole-entity query over an entity with an owned SINGLE-REFERENCE navigation
/// (auto-included eagerly by EF Core convention) must go native — the gate must admit the synthetic
/// <c>Select(x => IncludeExpression(x, ownedNav))</c> auto-include instead of marking the query non-natively
/// representable. See <c>.superpowers/sdd/EF-322-owned-ref-whole-entity-spike.md</c> for the spike that
/// found the exact gate site and admit condition this predicate implements.
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeOwnedReferenceWholeEntityTests(TemporaryDatabaseFixture database)
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

    // MQL-capture idiom mirrored from NativeOwnedCollectionCountTests.cs: FunctionalTests has no
    // TestMqlLoggerFactory/AssertMql (those live in the SpecificationTests project), so MQL is captured
    // through SpyLoggerProvider instead.
    private static SingleEntityDbContext<T> CreateContextWithLogging<T>(
        IMongoCollection<T> collection, MongoQueryMode mode, Action<ModelBuilder>? modelBuilderAction,
        out SpyLoggerProvider spyLogger)
        where T : class
    {
        var (loggerFactory, provider) = SpyLoggerProvider.Create();
        spyLogger = provider;

        return SingleEntityDbContext.Create(
            collection,
            loggerFactory,
            modelBuilderAction: modelBuilderAction,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                b.EnableSensitiveDataLogging();
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });
    }

    private static void AssertMql(SpyLoggerProvider spyLogger, string expected)
        => Assert.Contains(expected, spyLogger.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery));

    private string UniqueCollectionName(string name)
        => TemporaryDatabaseFixtureBase.CreateCollectionName(name) + Guid.NewGuid().ToString("N")[..8];

    // ════════════════════════════════════════════════════════════════════════════════════════════
    //  Goes native: owned single-reference whole-entity query
    // ════════════════════════════════════════════════════════════════════════════════════════════

    private class Blog
    {
        public ObjectId Id { get; set; }
        public string Title { get; set; } = "";
        public Address Address { get; set; } = null!;
    }

    private class Address
    {
        public string City { get; set; } = "";
        public string Zip { get; set; } = "";
    }

    private static readonly Action<ModelBuilder> BlogModel = mb => mb.Entity<Blog>().OwnsOne(b => b.Address);

    private IMongoCollection<Blog> SeedBlogs(string name)
    {
        var coll = database.MongoDatabase.GetCollection<BsonDocument>(UniqueCollectionName(name));
        coll.InsertMany([
            new BsonDocument
            {
                { "_id", ObjectId.GenerateNewId() }, { "Title", "Alpha" },
                { "Address", new BsonDocument { { "City", "NYC" }, { "Zip", "10001" } } }
            },
            new BsonDocument
            {
                { "_id", ObjectId.GenerateNewId() }, { "Title", "Beta" },
                { "Address", new BsonDocument { { "City", "LA" }, { "Zip", "90001" } } }
            },
        ]);
        return database.MongoDatabase.GetCollection<Blog>(coll.CollectionNamespace.CollectionName);
    }

    [Fact]
    public void Owned_single_reference_whole_entity_goes_native()
    {
        var collection = SeedBlogs(nameof(Owned_single_reference_whole_entity_goes_native));
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel);

        // Under NativeOnly a shape that falls back throws NativeTranslationNotSupportedException; success
        // here proves the owned auto-include Select went through the native whole-entity path.
        var results = db.Entities.AsNoTracking().ToList();

        Assert.Equal(2, results.Count);
        Assert.Contains(results, b => b.Title == "Alpha" && b.Address.City == "NYC");
        Assert.Contains(results, b => b.Title == "Beta" && b.Address.City == "LA");
    }

    [Fact]
    public void Owned_single_reference_whole_entity_with_root_where_goes_native()
    {
        var collection = SeedBlogs(nameof(Owned_single_reference_whole_entity_with_root_where_goes_native));
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel);

        var results = db.Entities.AsNoTracking().Where(b => b.Title == "Alpha").ToList();

        var blog = Assert.Single(results);
        Assert.Equal("NYC", blog.Address.City);
    }

    [Fact]
    public void Owned_single_reference_whole_entity_with_root_orderby_goes_native()
    {
        var collection = SeedBlogs(nameof(Owned_single_reference_whole_entity_with_root_orderby_goes_native));
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel);

        var results = db.Entities.AsNoTracking().OrderBy(b => b.Title).ToList();

        Assert.Equal(["Alpha", "Beta"], results.Select(b => b.Title));
        Assert.Equal("NYC", results[0].Address.City);
        Assert.Equal("LA", results[1].Address.City);
    }

    // ════════════════════════════════════════════════════════════════════════════════════════════
    //  Guard: the admit stays narrow — non-owned reference still falls back
    // ════════════════════════════════════════════════════════════════════════════════════════════

    private class BlogWithTags
    {
        public ObjectId Id { get; set; }
        public string Title { get; set; } = "";
        public List<Tag> Tags { get; set; } = [];
    }

    private class Tag
    {
        public string Name { get; set; } = "";
    }

    private static readonly Action<ModelBuilder> BlogWithTagsModel = mb => mb.Entity<BlogWithTags>().OwnsMany(b => b.Tags);

    [Fact]
    public void Owned_collection_whole_entity_goes_native()
    {
        var coll = database.MongoDatabase.GetCollection<BsonDocument>(
            UniqueCollectionName(nameof(Owned_collection_whole_entity_goes_native)));
        coll.InsertOne(new BsonDocument
        {
            { "_id", ObjectId.GenerateNewId() }, { "Title", "Alpha" },
            { "Tags", new BsonArray { new BsonDocument("Name", "a"), new BsonDocument("Name", "b") } }
        });
        var collection = database.MongoDatabase.GetCollection<BlogWithTags>(coll.CollectionNamespace.CollectionName);

        using var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogWithTagsModel);

        // Under NativeOnly a shape that falls back throws; success here proves the owned-COLLECTION
        // auto-include Select went through the native whole-entity path (EF-322 owned-collection slice).
        var blog = Assert.Single(db.Entities.AsNoTracking().ToList());
        Assert.Equal("Alpha", blog.Title);
        Assert.Equal(["a", "b"], blog.Tags.Select(t => t.Name));
    }

    private class Order
    {
        public ObjectId Id { get; set; }
        public string OrderDescription { get; set; } = "";
        public ObjectId CustomerId { get; set; }
        public Customer Customer { get; set; } = null!;
    }

    private class Customer
    {
        public ObjectId Id { get; set; }
        public string FullName { get; set; } = "";
    }

    private static readonly Action<ModelBuilder> OrderCustomerModel = mb =>
        mb.Entity<Order>().HasOne(o => o.Customer).WithMany().HasForeignKey(o => o.CustomerId);

    [Fact]
    public void Non_owned_reference_include_now_goes_native()
    {
        // EF-368 Task 5: this test's premise predates that ticket (it used to be
        // "Non_owned_reference_include_still_falls_back", asserting a NativeOnly throw). It still pins the
        // ORIGINAL point — a NON-owned (reference) navigation's explicit Include Select must NOT be admitted
        // by IsOwnedEmbeddedIncludeSelector's !navigation.IsEmbedded() guard (EF-322) — but the CONSEQUENCE of
        // that decline has changed: the shape is now admitted by a DIFFERENT, later mechanism, the
        // single-level reference-Include recognizer/confirm (TryConfirmReferenceInclude), so it goes native
        // instead of falling back. Customer here is a REQUIRED navigation (non-nullable CustomerId), so the
        // native path's INNER $unwind drops any row whose CustomerId is dangling: NativeOnly now SUCCEEDS
        // rather than throwing.
        //
        // EF-368 final fix wave, Finding 7: this test's assertion used to be a bare Assert.Empty over a
        // seed whose ONLY row had a deliberately dangling CustomerId — which is also exactly what a BROKEN
        // query returns, so it no longer discriminated the stated purpose (that the shape reaches the
        // reference-Include machinery at all rather than being admitted or dropped elsewhere). A second,
        // RESOLVABLE row is now seeded so the test asserts a populated navigation as well as the drop.
        var customersName = UniqueCollectionName(nameof(Non_owned_reference_include_now_goes_native)) + "Cust";
        var resolvableCustomerId = ObjectId.GenerateNewId();
        database.MongoDatabase.GetCollection<BsonDocument>(customersName).InsertOne(new BsonDocument
        {
            { "_id", resolvableCustomerId }, { "FullName", "Ada" }
        });

        var coll = database.MongoDatabase.GetCollection<BsonDocument>(
            UniqueCollectionName(nameof(Non_owned_reference_include_now_goes_native)));
        coll.InsertMany(
        [
            new BsonDocument
            {
                { "_id", ObjectId.GenerateNewId() }, { "OrderDescription", "Widget" },
                { "CustomerId", ObjectId.GenerateNewId() } // dangling: dropped by the inner $unwind.
            },
            new BsonDocument
            {
                { "_id", ObjectId.GenerateNewId() }, { "OrderDescription", "Gadget" },
                { "CustomerId", resolvableCustomerId } // resolvable: kept, navigation populated.
            }
        ]);
        var collection = database.MongoDatabase.GetCollection<Order>(coll.CollectionNamespace.CollectionName);

        using var db = CreateContext(collection, MongoQueryMode.NativeOnly,
            mb =>
            {
                OrderCustomerModel(mb);
                mb.Entity<Customer>().ToCollection(customersName);
            });

        var results = db.Entities.AsNoTracking().Include(o => o.Customer).ToList();

        var kept = Assert.Single(results);
        Assert.Equal("Gadget", kept.OrderDescription);
        Assert.NotNull(kept.Customer);
        Assert.Equal("Ada", kept.Customer.FullName);
    }

    // ════════════════════════════════════════════════════════════════════════════════════════════
    //  Reducer spot-check: Single/First over an owned-ref whole entity (routes DOM via $limit)
    // ════════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Owned_single_reference_whole_entity_Single_returns_correct_entity()
    {
        var collection = SeedBlogs(nameof(Owned_single_reference_whole_entity_Single_returns_correct_entity));
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel);

        var blog = db.Entities.AsNoTracking().Single(b => b.Title == "Alpha");

        Assert.Equal("Alpha", blog.Title);
        Assert.Equal("NYC", blog.Address.City);
    }

    [Fact]
    public void Owned_single_reference_whole_entity_First_returns_correct_entity()
    {
        var collection = SeedBlogs(nameof(Owned_single_reference_whole_entity_First_returns_correct_entity));
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel);

        var blog = db.Entities.AsNoTracking().OrderBy(b => b.Title).First();

        Assert.Equal("Alpha", blog.Title);
        Assert.Equal("NYC", blog.Address.City);
    }

    // ════════════════════════════════════════════════════════════════════════════════════════════
    //  Task 3: parity (Native == DriverLinq) + edge-case matrix for owned single-reference
    //  whole-entity queries. See .superpowers/sdd/task-3-brief.md and
    //  .superpowers/sdd/EF-322-owned-ref-whole-entity-spike.md for the edge cases this codifies.
    // ════════════════════════════════════════════════════════════════════════════════════════════

    // ── (1) Present owned sub-document: Native == DriverLinq ──────────────────────────────────────

    [Fact]
    public void Present_owned_reference_matches_driver_linq()
    {
        var collection = SeedBlogs(nameof(Present_owned_reference_matches_driver_linq));

        List<Blog> driver;
        using (var db = CreateContext(collection, MongoQueryMode.DriverLinq, BlogModel))
        {
            driver = db.Entities.AsNoTracking().OrderBy(b => b.Title).ToList();
        }

        List<Blog> native;
        using (var db = CreateContext(collection, MongoQueryMode.Native, BlogModel))
        {
            native = db.Entities.AsNoTracking().OrderBy(b => b.Title).ToList();
        }

        Assert.Equal(driver.Count, native.Count);
        foreach (var (d, n) in driver.Zip(native))
        {
            Assert.Equal(d.Title, n.Title);
            Assert.Equal(d.Address.City, n.Address.City);
            Assert.Equal(d.Address.Zip, n.Address.Zip);
        }
    }

    // ── (2) Absent / null owned sub-document (optional owned reference not set) ───────────────────

    private class BlogOptionalAddress
    {
        public ObjectId Id { get; set; }
        public string Title { get; set; } = "";
        public Address? Address { get; set; }
    }

    private static readonly Action<ModelBuilder> BlogOptionalAddressModel = mb =>
    {
        mb.Entity<BlogOptionalAddress>().OwnsOne(b => b.Address);
        mb.Entity<BlogOptionalAddress>().Navigation(b => b.Address).IsRequired(false);
    };

    [Fact]
    public void Absent_owned_reference_yields_null_matching_driver_linq()
    {
        var coll = database.MongoDatabase.GetCollection<BsonDocument>(
            UniqueCollectionName(nameof(Absent_owned_reference_yields_null_matching_driver_linq)));
        coll.InsertMany([
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Title", "NoAddr" } },
            new BsonDocument
            {
                { "_id", ObjectId.GenerateNewId() }, { "Title", "WithAddr" },
                { "Address", new BsonDocument { { "City", "NYC" }, { "Zip", "10001" } } }
            }
        ]);
        var collection = database.MongoDatabase.GetCollection<BlogOptionalAddress>(coll.CollectionNamespace.CollectionName);

        List<BlogOptionalAddress> driver;
        using (var db = CreateContext(collection, MongoQueryMode.DriverLinq, BlogOptionalAddressModel))
        {
            driver = db.Entities.AsNoTracking().OrderBy(b => b.Title).ToList();
        }

        List<BlogOptionalAddress> native;
        using (var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogOptionalAddressModel))
        {
            // NativeOnly: proves the optional-owned-ref shape genuinely goes native rather than
            // silently falling back when the sub-document happens to be absent.
            native = db.Entities.AsNoTracking().OrderBy(b => b.Title).ToList();
        }

        Assert.Equal(2, driver.Count);
        Assert.Equal(driver.Count, native.Count);
        foreach (var (d, n) in driver.Zip(native))
        {
            Assert.Equal(d.Title, n.Title);
            Assert.Equal(d.Address is null, n.Address is null);
            if (d.Address is not null)
            {
                Assert.Equal(d.Address.City, n.Address!.City);
                Assert.Equal(d.Address.Zip, n.Address!.Zip);
            }
        }

        Assert.Null(driver.Single(b => b.Title == "NoAddr").Address);
        Assert.Null(native.Single(b => b.Title == "NoAddr").Address);
        Assert.NotNull(native.Single(b => b.Title == "WithAddr").Address);
    }

    // ── (2b) Explicit BSON null VALUE for the whole owned-reference element (distinct from the ─────
    //        key-ABSENT case above) — same optional-owned-ref model, but the "Address" element is
    //        present with an explicit BsonNull value rather than omitted entirely.

    [Fact]
    public void Explicit_null_owned_reference_yields_null_matching_driver_linq()
    {
        var coll = database.MongoDatabase.GetCollection<BsonDocument>(
            UniqueCollectionName(nameof(Explicit_null_owned_reference_yields_null_matching_driver_linq)));
        coll.InsertMany([
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Title", "NullAddr" }, { "Address", BsonNull.Value } },
            new BsonDocument
            {
                { "_id", ObjectId.GenerateNewId() }, { "Title", "WithAddr" },
                { "Address", new BsonDocument { { "City", "NYC" }, { "Zip", "10001" } } }
            }
        ]);
        var collection = database.MongoDatabase.GetCollection<BlogOptionalAddress>(coll.CollectionNamespace.CollectionName);

        List<BlogOptionalAddress> driver;
        using (var db = CreateContext(collection, MongoQueryMode.DriverLinq, BlogOptionalAddressModel))
        {
            driver = db.Entities.AsNoTracking().OrderBy(b => b.Title).ToList();
        }

        List<BlogOptionalAddress> native;
        using (var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogOptionalAddressModel))
        {
            // NativeOnly: proves the explicit-BsonNull owned-ref shape genuinely goes native rather than
            // silently falling back — success here (rather than NativeTranslationNotSupportedException)
            // is the routing proof.
            native = db.Entities.AsNoTracking().OrderBy(b => b.Title).ToList();
        }

        Assert.Equal(2, driver.Count);
        Assert.Equal(driver.Count, native.Count);
        foreach (var (d, n) in driver.Zip(native))
        {
            Assert.Equal(d.Title, n.Title);
            Assert.Equal(d.Address is null, n.Address is null);
            if (d.Address is not null)
            {
                Assert.Equal(d.Address.City, n.Address!.City);
                Assert.Equal(d.Address.Zip, n.Address!.Zip);
            }
        }

        // Explicit BsonNull behaves identically to key-absent (see Absent_owned_reference_yields_null_
        // matching_driver_linq above): both materialize a null owned-reference navigation.
        Assert.Null(driver.Single(b => b.Title == "NullAddr").Address);
        Assert.Null(native.Single(b => b.Title == "NullAddr").Address);
        Assert.NotNull(native.Single(b => b.Title == "WithAddr").Address);
    }

    // ── (3) Required owned reference with the sub-document missing: both modes throw the SAME ─────
    //       exception type (and, per the spike, the same message).

    private class BlogRequiredAddress
    {
        public ObjectId Id { get; set; }
        public string Title { get; set; } = "";
        public Address Address { get; set; } = null!;
    }

    private static readonly Action<ModelBuilder> BlogRequiredAddressModel = mb =>
    {
        mb.Entity<BlogRequiredAddress>().OwnsOne(b => b.Address);
        mb.Entity<BlogRequiredAddress>().Navigation(b => b.Address).IsRequired();
    };

    [Fact]
    public void Required_owned_reference_missing_throws_matching_driver_linq()
    {
        var coll = database.MongoDatabase.GetCollection<BsonDocument>(
            UniqueCollectionName(nameof(Required_owned_reference_missing_throws_matching_driver_linq)));
        coll.InsertOne(new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Title", "NoAddr" } });
        var collection = database.MongoDatabase.GetCollection<BlogRequiredAddress>(coll.CollectionNamespace.CollectionName);

        InvalidOperationException driverEx;
        using (var db = CreateContext(collection, MongoQueryMode.DriverLinq, BlogRequiredAddressModel))
        {
            driverEx = Assert.Throws<InvalidOperationException>(() => db.Entities.AsNoTracking().ToList());
        }

        InvalidOperationException nativeEx;
        using (var db = CreateContext(collection, MongoQueryMode.Native, BlogRequiredAddressModel))
        {
            nativeEx = Assert.Throws<InvalidOperationException>(() => db.Entities.AsNoTracking().ToList());
        }

        Assert.Equal(driverEx.GetType(), nativeEx.GetType());
        Assert.Equal(driverEx.Message, nativeEx.Message);

        // And it must genuinely go native (not silently fall back to driver-LINQ under NativeOnly).
        using var nativeOnly = CreateContext(collection, MongoQueryMode.NativeOnly, BlogRequiredAddressModel);
        Assert.Throws<InvalidOperationException>(() => nativeOnly.Entities.AsNoTracking().ToList());
    }

    // ── (4) Nested owned reference (Root → A → B): deep-value parity, succeeds under NativeOnly ───

    private class BlogNested
    {
        public ObjectId Id { get; set; }
        public string Title { get; set; } = "";
        public NestedAddress Address { get; set; } = null!;
    }

    private class NestedAddress
    {
        public string City { get; set; } = "";
        public Geo Geo { get; set; } = null!;
    }

    private class Geo
    {
        public double Lat { get; set; }
        public double Lng { get; set; }
    }

    private static readonly Action<ModelBuilder> BlogNestedModel = mb =>
        mb.Entity<BlogNested>().OwnsOne(b => b.Address, a => a.OwnsOne(x => x.Geo));

    [Fact]
    public void Nested_owned_reference_deep_values_match_driver_linq_and_goes_native()
    {
        var coll = database.MongoDatabase.GetCollection<BsonDocument>(
            UniqueCollectionName(nameof(Nested_owned_reference_deep_values_match_driver_linq_and_goes_native)));
        coll.InsertMany([
            new BsonDocument
            {
                { "_id", ObjectId.GenerateNewId() }, { "Title", "Alpha" },
                {
                    "Address", new BsonDocument
                    {
                        { "City", "NYC" },
                        { "Geo", new BsonDocument { { "Lat", 40.7 }, { "Lng", -74.0 } } }
                    }
                }
            },
            new BsonDocument
            {
                { "_id", ObjectId.GenerateNewId() }, { "Title", "Beta" },
                {
                    "Address", new BsonDocument
                    {
                        { "City", "LA" },
                        { "Geo", new BsonDocument { { "Lat", 34.0 }, { "Lng", -118.2 } } }
                    }
                }
            }
        ]);
        var collection = database.MongoDatabase.GetCollection<BlogNested>(coll.CollectionNamespace.CollectionName);

        List<BlogNested> driver;
        using (var db = CreateContext(collection, MongoQueryMode.DriverLinq, BlogNestedModel))
        {
            driver = db.Entities.AsNoTracking().OrderBy(b => b.Title).ToList();
        }

        List<BlogNested> native;
        using (var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogNestedModel))
        {
            // NativeOnly: proves the 2-level nested owned-reference chain genuinely goes native (the
            // admit predicate unwraps nested IncludeExpression layers) rather than falling back.
            native = db.Entities.AsNoTracking().OrderBy(b => b.Title).ToList();
        }

        Assert.Equal(driver.Count, native.Count);
        foreach (var (d, n) in driver.Zip(native))
        {
            Assert.Equal(d.Title, n.Title);
            Assert.Equal(d.Address.City, n.Address.City);
            Assert.Equal(d.Address.Geo.Lat, n.Address.Geo.Lat);
            Assert.Equal(d.Address.Geo.Lng, n.Address.Geo.Lng);
        }
    }

    // ── (5) Shared-type owned reference: same owned CLR type used by two navigations ───────────────

    private class BlogTwoAddresses
    {
        public ObjectId Id { get; set; }
        public string Title { get; set; } = "";
        public Address HomeAddress { get; set; } = null!;
        public Address WorkAddress { get; set; } = null!;
    }

    private static readonly Action<ModelBuilder> BlogTwoAddressesModel = mb =>
    {
        mb.Entity<BlogTwoAddresses>().OwnsOne(b => b.HomeAddress);
        mb.Entity<BlogTwoAddresses>().OwnsOne(b => b.WorkAddress);
    };

    [Fact]
    public void Shared_type_owned_reference_matches_driver_linq()
    {
        var coll = database.MongoDatabase.GetCollection<BsonDocument>(
            UniqueCollectionName(nameof(Shared_type_owned_reference_matches_driver_linq)));
        coll.InsertOne(new BsonDocument
        {
            { "_id", ObjectId.GenerateNewId() }, { "Title", "Alpha" },
            { "HomeAddress", new BsonDocument { { "City", "NYC" }, { "Zip", "10001" } } },
            { "WorkAddress", new BsonDocument { { "City", "SF" }, { "Zip", "94105" } } }
        });
        var collection = database.MongoDatabase.GetCollection<BlogTwoAddresses>(coll.CollectionNamespace.CollectionName);

        BlogTwoAddresses driver;
        using (var db = CreateContext(collection, MongoQueryMode.DriverLinq, BlogTwoAddressesModel))
        {
            driver = db.Entities.AsNoTracking().Single();
        }

        BlogTwoAddresses native;
        using (var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogTwoAddressesModel))
        {
            native = db.Entities.AsNoTracking().Single();
        }

        Assert.Equal("NYC", driver.HomeAddress.City);
        Assert.Equal("SF", driver.WorkAddress.City);
        Assert.Equal(driver.HomeAddress.City, native.HomeAddress.City);
        Assert.Equal(driver.HomeAddress.Zip, native.HomeAddress.Zip);
        Assert.Equal(driver.WorkAddress.City, native.WorkAddress.City);
        Assert.Equal(driver.WorkAddress.Zip, native.WorkAddress.Zip);
    }

    // ── (6) Tracked query (default tracking): entities tracked; mutate + SaveChanges round-trips ───

    [Fact]
    public void Tracked_owned_reference_query_tracks_entities_and_round_trips_mutation()
    {
        var collection = SeedBlogs(nameof(Tracked_owned_reference_query_tracks_entities_and_round_trips_mutation));

        using (var db = CreateContext(collection, MongoQueryMode.Native, BlogModel))
        {
            // Default (tracked) query — no AsNoTracking.
            var results = db.Entities.OrderBy(b => b.Title).ToList();

            Assert.Equal(2, results.Count);
            Assert.Equal(2, db.ChangeTracker.Entries<Blog>().Count());

            var alpha = results.Single(b => b.Title == "Alpha");
            alpha.Address.City = "Brooklyn";
            db.SaveChanges();
        }

        using (var db = CreateContext(collection, MongoQueryMode.DriverLinq, BlogModel))
        {
            var alpha = db.Entities.AsNoTracking().Single(b => b.Title == "Alpha");
            Assert.Equal("Brooklyn", alpha.Address.City);
        }
    }

    // ── (7) Streamed-vs-DOM equality: the flat owned-ref shape genuinely streams (NativeOnly ───────
    //       succeeds) AND returns entities identical to the driver-LINQ oracle.

    [Fact]
    public void Owned_single_reference_flat_shape_streams_and_returns_identical_entities_to_driver_linq()
    {
        var collection = SeedBlogs(nameof(Owned_single_reference_flat_shape_streams_and_returns_identical_entities_to_driver_linq));

        List<Blog> driver;
        using (var db = CreateContext(collection, MongoQueryMode.DriverLinq, BlogModel))
        {
            driver = db.Entities.AsNoTracking().OrderBy(b => b.Title).ToList();
        }

        // NativeOnly + Enumerable cardinality (.ToList(), no reducer) forces the one-pass streaming
        // materializer; success here proves the flat owned-ref shape genuinely streams rather than
        // silently falling back to driver-LINQ.
        List<Blog> native;
        using (var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel))
        {
            native = db.Entities.AsNoTracking().OrderBy(b => b.Title).ToList();
        }

        Assert.Equal(driver.Count, native.Count);
        for (var i = 0; i < driver.Count; i++)
        {
            Assert.Equal(driver[i].Title, native[i].Title);
            Assert.Equal(driver[i].Address.City, native[i].Address.City);
            Assert.Equal(driver[i].Address.Zip, native[i].Address.Zip);
        }
    }

    // ════════════════════════════════════════════════════════════════════════════════════════════
    //  EF-322 owned-collection slice: mixed owned-ref + owned-collection now goes native — a routing
    //  proof (see also NativeMaterializerOnePassTests.Owned_reference_and_owned_collection_
    //  materialize_correct_nested_values, which asserts Native==DriverLinq parity for this shape).
    // ════════════════════════════════════════════════════════════════════════════════════════════

    private class BlogMixed
    {
        public ObjectId Id { get; set; }
        public string Title { get; set; } = "";
        public Address Address { get; set; } = null!;
        public List<Tag> Tags { get; set; } = [];
    }

    private static readonly Action<ModelBuilder> BlogMixedModel = mb =>
    {
        mb.Entity<BlogMixed>().OwnsOne(b => b.Address);
        mb.Entity<BlogMixed>().OwnsMany(b => b.Tags);
    };

    [Fact]
    public void Mixed_owned_reference_and_owned_collection_goes_native_under_NativeOnly()
    {
        var coll = database.MongoDatabase.GetCollection<BsonDocument>(
            UniqueCollectionName(nameof(Mixed_owned_reference_and_owned_collection_goes_native_under_NativeOnly)));
        coll.InsertOne(new BsonDocument
        {
            { "_id", ObjectId.GenerateNewId() }, { "Title", "Alpha" },
            { "Address", new BsonDocument { { "City", "NYC" }, { "Zip", "10001" } } },
            { "Tags", new BsonArray { new BsonDocument("Name", "a") } }
        });
        var collection = database.MongoDatabase.GetCollection<BlogMixed>(coll.CollectionNamespace.CollectionName);

        using var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogMixedModel);

        // The admit predicate now accepts EVERY embedded navigation in the auto-include chain — an owned
        // reference (Address) mixed with an owned COLLECTION (Tags) on the same root is admitted as a whole
        // and goes native (EF-322 owned-collection slice; previously this fell back).
        var blog = Assert.Single(db.Entities.AsNoTracking().ToList());
        Assert.Equal("NYC", blog.Address.City);
        Assert.Equal(["a"], blog.Tags.Select(t => t.Name));
    }

    // ════════════════════════════════════════════════════════════════════════════════════════════
    //  EF-441 Task 1: an owned single-reference navigation ENTITY LEAF inside a projection
    //  (Select(b => new { b.Address, b.Title })) goes native. See NativeProjectionBinder's
    //  TryGetOwnedReferenceNavigationLeaf / TryTranslateLeaf arm and .superpowers/sdd/
    //  2026-08-28-ef441-navigation-entity-leaf-projection/task-0-report.md §2 for the mechanism.
    // ════════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Owned_reference_entity_leaf_beside_field_leaf_goes_native_and_reads_correct_values()
    {
        var collection = SeedBlogs(nameof(Owned_reference_entity_leaf_beside_field_leaf_goes_native_and_reads_correct_values));
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel);

        // NativeOnly: success here (rather than NativeTranslationNotSupportedException) is the routing
        // proof that the mixed field+entity-nav projection genuinely goes native.
        var results = db.Entities.AsNoTracking()
            .Select(b => new { b.Address, b.Title })
            .OrderBy(r => r.Title)
            .ToList();

        Assert.Equal(2, results.Count);
        Assert.Equal("Alpha", results[0].Title);
        Assert.Equal("NYC", results[0].Address.City);
        Assert.Equal("10001", results[0].Address.Zip);
        Assert.Equal("Beta", results[1].Title);
        Assert.Equal("LA", results[1].Address.City);
    }

    [Fact]
    public void Owned_reference_entity_leaf_beside_field_leaf_emits_expected_project_stage_with_retained_id()
    {
        var collection = SeedBlogs(nameof(Owned_reference_entity_leaf_beside_field_leaf_emits_expected_project_stage_with_retained_id));
        using var db = CreateContextWithLogging(collection, MongoQueryMode.NativeOnly, BlogModel, out var spyLogger);

        _ = db.Entities.AsNoTracking().Select(b => new { b.Address, b.Title }).ToList();

        // Matches spike report §2b's verified shape: the nav's own document path as the alias, the field
        // sibling by its own name, and the retained owner _id (task 1, site 4) that the owned Address
        // element's shadow-key read requires.
        AssertMql(spyLogger, "{ \"$project\" : { \"Address\" : \"$Address\", \"Title\" : \"$Title\", \"_id\" : \"$_id\" } }");
    }

    [Fact]
    public void Owned_reference_entity_leaf_parity_between_native_and_driver_linq()
    {
        var collection = SeedBlogs(nameof(Owned_reference_entity_leaf_parity_between_native_and_driver_linq));

        List<(string Title, string City, string Zip)> driver;
        using (var db = CreateContext(collection, MongoQueryMode.DriverLinq, BlogModel))
        {
            driver = db.Entities.AsNoTracking().Select(b => new { b.Address, b.Title })
                .OrderBy(r => r.Title)
                .ToList()
                .Select(r => (r.Title, r.Address.City, r.Address.Zip)).ToList();
        }

        List<(string Title, string City, string Zip)> native;
        using (var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel))
        {
            native = db.Entities.AsNoTracking().Select(b => new { b.Address, b.Title })
                .OrderBy(r => r.Title)
                .ToList()
                .Select(r => (r.Title, r.Address.City, r.Address.Zip)).ToList();
        }

        Assert.Equal(driver, native);
    }

    // Fix round 1 (post-review): the LATE-FALLBACK leg for the field-sibling nav-entity-leaf projection was
    // completely uncovered — reverting site 4's unconditional _id/DocumentPath-alias-override registration
    // (NativeProjectionBinder.TryPopulateNativeProjection) leaves the ENTIRE unit/spec/functional suite green
    // while silently breaking this exact shape under plain `Native` mode.
    //
    // This test originally used a captured-local `StartsWith` to force that late-fallback leg (translate-time
    // routes native, Route == Projection, then MongoQueryLanguageRenderer.RenderRegex declined at render
    // time). That trigger no longer declines — a parameterized StartsWith term is now natively representable
    // — so this shape goes fully native under both Native and NativeOnly. The mixed/whole-document shaper
    // read this test used to force via that trigger remains covered directly by the explicit-DriverLinq leg
    // below (same shape, same read). This test now just proves the field-sibling projection stays correct
    // with a genuine (non-baked) query parameter, under both Native and NativeOnly.
    [Fact]
    public void Field_sibling_projection_behind_a_parameterized_where_reads_correct_values()
    {
        var collection = SeedBlogs(nameof(Field_sibling_projection_behind_a_parameterized_where_reads_correct_values));
        var titlePrefix = "A"; // a captured local, not a constant — a genuine query parameter

        using (var nativeOnly = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel))
        {
            var nativeOnlyResults = nativeOnly.Entities.AsNoTracking()
                .Where(b => b.Title.StartsWith(titlePrefix))
                .Select(b => new { b.Address, b.Title })
                .ToList();

            var nativeOnlyRow = Assert.Single(nativeOnlyResults);
            Assert.Equal("Alpha", nativeOnlyRow.Title);
            Assert.Equal("NYC", nativeOnlyRow.Address.City);
            Assert.Equal("10001", nativeOnlyRow.Address.Zip);
        }

        using (var native = CreateContext(collection, MongoQueryMode.Native, BlogModel))
        {
            var results = native.Entities.AsNoTracking()
                .Where(b => b.Title.StartsWith(titlePrefix))
                .Select(b => new { b.Address, b.Title })
                .ToList();

            var row = Assert.Single(results);
            Assert.Equal("Alpha", row.Title);
            Assert.Equal("NYC", row.Address.City);
            Assert.Equal("10001", row.Address.Zip);
        }

        // And explicit DriverLinq — the OTHER leg that needs the same shape to read correctly, per the spike's
        // §5a finding (the mixed shaper's ReadsUnprojectedDocuments handles this leaf's alias without any
        // null-out, since the alias names a real element on a whole document).
        using (var driver = CreateContext(collection, MongoQueryMode.DriverLinq, BlogModel))
        {
            var results = driver.Entities.AsNoTracking()
                .Where(b => b.Title.StartsWith(titlePrefix))
                .Select(b => new { b.Address, b.Title })
                .ToList();

            var row = Assert.Single(results);
            Assert.Equal("Alpha", row.Title);
            Assert.Equal("NYC", row.Address.City);
            Assert.Equal("10001", row.Address.Zip);
        }
    }

    // Renamed member ("Addr" instead of "Address") must decline outright — the late-fallback leg's
    // correctness depends on the emitted alias naming a real element the driver-LINQ bridge also renders
    // under that same name (see TryTranslateLeaf's alias-must-equal-document-path conjunct).
    [Fact]
    public void Renamed_owned_reference_entity_leaf_falls_back_but_still_reads_correct_values()
    {
        var collection = SeedBlogs(nameof(Renamed_owned_reference_entity_leaf_falls_back_but_still_reads_correct_values));
        using var nativeOnly = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel);
        Assert.Throws<NativeTranslationNotSupportedException>(
            () => nativeOnly.Entities.AsNoTracking().Select(b => new { Addr = b.Address, b.Title }).ToList());

        using var db = CreateContext(collection, MongoQueryMode.Native, BlogModel);
        var results = db.Entities.AsNoTracking().Select(b => new { Addr = b.Address, b.Title })
            .OrderBy(r => r.Title).ToList();

        Assert.Equal(2, results.Count);
        Assert.Equal("NYC", results[0].Addr.City);
        Assert.Equal("LA", results[1].Addr.City);
    }

    // The computed-sibling carve-out (mirrors EF-444's Task-4 carve-out): a nav-entity leaf mixed with a
    // COMPUTED leaf (no document path of its own) still declines the whole projection — there is no correct
    // late-fallback rendering for that combination (spike report §5). This is EXPECTED, not a residual gap.
    private class BlogWithRank
    {
        public ObjectId Id { get; set; }
        public string Title { get; set; } = "";
        public int Rank { get; set; }
        public Address Address { get; set; } = null!;
    }

    private static readonly Action<ModelBuilder> BlogWithRankModel = mb => mb.Entity<BlogWithRank>().OwnsOne(b => b.Address);

    private IMongoCollection<BlogWithRank> SeedBlogsWithRank(string name)
    {
        var coll = database.MongoDatabase.GetCollection<BsonDocument>(UniqueCollectionName(name));
        coll.InsertOne(new BsonDocument
        {
            { "_id", ObjectId.GenerateNewId() }, { "Title", "Alpha" }, { "Rank", 3 },
            { "Address", new BsonDocument { { "City", "NYC" }, { "Zip", "10001" } } }
        });
        return database.MongoDatabase.GetCollection<BlogWithRank>(coll.CollectionNamespace.CollectionName);
    }

    [Fact]
    public void Owned_reference_entity_leaf_beside_computed_leaf_declines_but_still_reads_correct_values()
    {
        var collection = SeedBlogsWithRank(
            nameof(Owned_reference_entity_leaf_beside_computed_leaf_declines_but_still_reads_correct_values));

        using (var nativeOnly = CreateContext(collection, MongoQueryMode.NativeOnly, BlogWithRankModel))
        {
            Assert.Throws<NativeTranslationNotSupportedException>(
                () => nativeOnly.Entities.AsNoTracking()
                    .Select(b => new { b.Address, Total = b.Rank * b.Rank }).ToList());
        }

        using var db = CreateContext(collection, MongoQueryMode.Native, BlogWithRankModel);
        var result = Assert.Single(
            db.Entities.AsNoTracking().Select(b => new { b.Address, Total = b.Rank * b.Rank }).ToList());
        Assert.Equal("NYC", result.Address.City);
        Assert.Equal(9, result.Total);
    }

    // ── Set-op-gate regression (spike report §4's flagged-not-decided item) ────────────────────────
    //
    // A nav-entity-leaf projection's emitted _id would leak into a set operation's whole-document
    // comparison/dedup key exactly like the owned-ARRAY leaf's does (see NativeProjectionBinder's own
    // comment on HasArrayProjectionLeaf and MongoQueryableMethodTranslatingExpressionVisitor.
    // IsPlainProjectedSelect). This leaf kind now sets that SAME flag (see NativeProjectionBinder's commit
    // block), so it must decline as a set-op operand rather than silently emitting a wrong-comparison-key
    // set op.
    //
    // NOT covered at THIS (functional/end-to-end) level, by design: a genuine Union/Concat of two operands
    // that both push down a WRAPPED (entity/array-typed) projection — and, separately, a plain
    // Select(...).Distinct() over one (no set op at all) — hits a SEPARATE, PRE-EXISTING gap in EF's own
    // nav-expansion, confirmed by the reviewer's mutation testing to reproduce on the UNMODIFIED base commit
    // too (i.e. it is not introduced by EF-441, and it is not specific to the nav-entity leaf — it would
    // equally affect the pre-existing owned-ARRAY-leaf projection, which had simply never been combined with a
    // set op or a projected Distinct in this repo's test suite before). Nav-expansion re-enters
    // MongoProjectionBindingExpressionVisitor's shaper-building for what is really the same logical Select a
    // second time; the second pass finds state left over from the first and throws InvalidCastException
    // instead of returning either a correct native pipeline or a graceful NativeTranslationNotSupportedException
    // decline. This is a real, loud failure (not silent wrong data), but the WRONG exception type, and it is
    // outside this ticket's file scope (NativeProjectionBinder.cs) to fix — it lives in
    // MongoProjectionBindingExpressionVisitor.cs and warrants its OWN follow-up ticket, since it predates
    // EF-441 and is not specific to this leaf kind. The unit-level regression test
    // (SlotPopulationTests.Owned_reference_entity_leaf_projection_sets_HasArrayProjectionLeaf_for_the_set_op_gate)
    // proves the ONE fact this task owns — the gate's flag is set correctly — via a harness that bypasses
    // nav-expansion and therefore does not hit this separate concern.
}
