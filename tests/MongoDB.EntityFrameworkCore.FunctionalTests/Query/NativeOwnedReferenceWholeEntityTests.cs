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
/// A whole-entity query over an entity with an owned single-reference navigation (auto-included by EF
/// convention) must go native: the gate admits the synthetic <c>Select(x => IncludeExpression(x, ownedNav))</c>.
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

    private string UniqueCollectionName(string name)
        => TemporaryDatabaseFixtureBase.CreateCollectionName(name) + Guid.NewGuid().ToString("N")[..8];

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
        // A non-owned navigation's Include is not admitted by IsOwnedEmbeddedIncludeSelector's
        // !navigation.IsEmbedded() guard, but is picked up by the reference-Include recognizer
        // (TryConfirmReferenceInclude). Customer is required, so the inner $unwind drops the dangling row; the
        // resolvable row pins that the navigation is populated (an empty result alone would also match a broken query).
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

        // Explicit BsonNull behaves like key-absent: both materialize a null navigation.
        Assert.Null(driver.Single(b => b.Title == "NullAddr").Address);
        Assert.Null(native.Single(b => b.Title == "NullAddr").Address);
        Assert.NotNull(native.Single(b => b.Title == "WithAddr").Address);
    }

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

        using var nativeOnly = CreateContext(collection, MongoQueryMode.NativeOnly, BlogRequiredAddressModel);
        Assert.Throws<InvalidOperationException>(() => nativeOnly.Entities.AsNoTracking().ToList());
    }

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

    [Fact]
    public void Tracked_owned_reference_query_tracks_entities_and_round_trips_mutation()
    {
        var collection = SeedBlogs(nameof(Tracked_owned_reference_query_tracks_entities_and_round_trips_mutation));

        using (var db = CreateContext(collection, MongoQueryMode.Native, BlogModel))
        {
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

        // The admit predicate accepts every embedded navigation in the auto-include chain, reference and collection.
        var blog = Assert.Single(db.Entities.AsNoTracking().ToList());
        Assert.Equal("NYC", blog.Address.City);
        Assert.Equal(["a"], blog.Tags.Select(t => t.Name));
    }

    // Owned reference entity leaf inside a projection (see TryGetOwnedReferenceNavigationLeaf).

    [Fact]
    public void Owned_reference_entity_leaf_beside_field_leaf_goes_native_and_reads_correct_values()
    {
        var collection = SeedBlogs(nameof(Owned_reference_entity_leaf_beside_field_leaf_goes_native_and_reads_correct_values));
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel);

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

        // The nav's document path as the alias, the field sibling by name, and the retained owner _id that the owned
        // element's shadow-key read requires.
        spyLogger.AssertExecutedMqlContains("{ \"$project\" : { \"Address\" : \"$Address\", \"Title\" : \"$Title\", \"_id\" : \"$_id\" } }");
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

    // Guards the unconditional _id / DocumentPath-alias-override registration in
    // NativeProjectionBinder.TryPopulateNativeProjection: reverting it breaks this shape silently. Uses a
    // parameterized StartsWith to exercise a genuine query parameter; the DriverLinq leg covers the mixed shaper.
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

        // Explicit DriverLinq must read the same shape: the mixed shaper's ReadsUnprojectedDocuments handles the alias
        // without null-out, since it names a real element on a whole document.
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

    // A renamed member ("Addr") stages the owned element under its own name with a DocumentPath alias override
    // (member -> element), as an array leaf does, so the alias-addressed shaper and a whole-document fallback read the
    // same element.
    [Fact]
    public void Renamed_owned_reference_entity_leaf_goes_native_and_reads_correct_values()
    {
        var collection = SeedBlogs(nameof(Renamed_owned_reference_entity_leaf_goes_native_and_reads_correct_values));

        foreach (var mode in new[] { MongoQueryMode.NativeOnly, MongoQueryMode.Native, MongoQueryMode.DriverLinq })
        {
            using var db = CreateContext(collection, mode, BlogModel);
            var results = db.Entities.AsNoTracking().Select(b => new { Addr = b.Address, b.Title })
                .OrderBy(r => r.Title).ToList();

            Assert.Equal(2, results.Count);
            Assert.Equal("NYC", results[0].Addr.City);
            Assert.Equal("LA", results[1].Addr.City);
        }
    }

    // `Address` (a scalar member) shares its name with the element the renamed leaf is staged under: the duplicate alias
    // declines the whole projection rather than letting the read side rename one of them.
    [Fact]
    public void Renamed_owned_reference_entity_leaf_colliding_with_another_alias_declines()
    {
        var collection = SeedBlogs(nameof(Renamed_owned_reference_entity_leaf_colliding_with_another_alias_declines));

        using var nativeOnly = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel);
        Assert.Throws<NativeTranslationNotSupportedException>(
            () => nativeOnly.Entities.AsNoTracking().Select(b => new { Address = b.Title, Addr = b.Address }).ToList());

        using var db = CreateContext(collection, MongoQueryMode.Native, BlogModel);
        var results = db.Entities.AsNoTracking().Select(b => new { Address = b.Title, Addr = b.Address })
            .OrderBy(r => r.Address).ToList();

        Assert.Equal(2, results.Count);
        Assert.Equal("NYC", results[0].Addr.City);
        Assert.Equal("LA", results[1].Addr.City);
    }

    // A nav-entity leaf mixed with a computed leaf (no document path) declines the whole projection; there is no
    // correct late-fallback rendering for that combination. Expected, not a gap.
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

    // A nav-entity-leaf projection's owner _id would leak into a set operation's or projected Distinct's dedup key, so it
    // sets HasArrayProjectionLeaf and declines natively there. Nav-expansion appends an identity re-projection after the
    // Distinct/set op; on the driver-LINQ path it can't be stripped, so the driver returns documents without the owner
    // _id and, under NoTracking, the owned shadow key reads as a placeholder.

    public static TheoryData<MongoQueryMode> FallbackModes()
        => new() { MongoQueryMode.Native, MongoQueryMode.DriverLinq };

    [Theory]
    [MemberData(nameof(FallbackModes))]
    public void Owned_reference_entity_leaf_in_anonymous_type_with_Distinct(MongoQueryMode mode)
    {
        var collection = SeedBlogs(nameof(Owned_reference_entity_leaf_in_anonymous_type_with_Distinct) + mode);
        using var db = CreateContext(collection, mode, BlogModel);

        var result = db.Entities.AsNoTracking().Select(b => new { b.Address }).Distinct().ToList();

        Assert.Equal(["LA", "NYC"], result.Select(r => r.Address.City).OrderBy(c => c).ToArray());
    }

    [Theory]
    [MemberData(nameof(FallbackModes))]
    public void Owned_reference_entity_leaf_in_anonymous_type_with_Union(MongoQueryMode mode)
    {
        var collection = SeedBlogs(nameof(Owned_reference_entity_leaf_in_anonymous_type_with_Union) + mode);
        using var db = CreateContext(collection, mode, BlogModel);

        var result = db.Entities.AsNoTracking().Where(b => b.Title == "Alpha").Select(b => new { b.Title, b.Address })
            .Union(db.Entities.AsNoTracking().Select(b => new { b.Title, b.Address }))
            .ToList();

        Assert.Equal(
            [("Alpha", "NYC", "10001"), ("Beta", "LA", "90001")],
            result.Select(r => (r.Title, r.Address.City, r.Address.Zip)).OrderBy(r => r.Title).ToArray());
    }

    [Theory]
    [MemberData(nameof(FallbackModes))]
    public void Owned_reference_entity_leaf_in_anonymous_type_with_Concat(MongoQueryMode mode)
    {
        var collection = SeedBlogs(nameof(Owned_reference_entity_leaf_in_anonymous_type_with_Concat) + mode);
        using var db = CreateContext(collection, mode, BlogModel);

        var result = db.Entities.AsNoTracking().Where(b => b.Title == "Alpha").Select(b => new { b.Title, b.Address })
            .Concat(db.Entities.AsNoTracking().Select(b => new { b.Title, b.Address }))
            .ToList();

        Assert.Equal(
            [("Alpha", "NYC"), ("Alpha", "NYC"), ("Beta", "LA")],
            result.Select(r => (r.Title, r.Address.City)).OrderBy(r => r.Title).ToArray());
    }

    [Fact]
    public void Owned_reference_entity_leaf_in_anonymous_type_with_Union_declines_under_NativeOnly()
    {
        var collection = SeedBlogs(nameof(Owned_reference_entity_leaf_in_anonymous_type_with_Union_declines_under_NativeOnly));
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel);

        Assert.Throws<NativeTranslationNotSupportedException>(() =>
            db.Entities.AsNoTracking().Select(b => new { b.Title, b.Address })
                .Union(db.Entities.AsNoTracking().Select(b => new { b.Title, b.Address }))
                .ToList());
    }

    // Like the set-op gate, a native projected Distinct declines: its $group would include the leaked owner _id.
    [Fact]
    public void Owned_reference_entity_leaf_in_anonymous_type_with_Distinct_declines_under_NativeOnly()
    {
        var collection = SeedBlogs(nameof(Owned_reference_entity_leaf_in_anonymous_type_with_Distinct_declines_under_NativeOnly));
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel);

        Assert.Throws<NativeTranslationNotSupportedException>(() =>
            db.Entities.AsNoTracking().Select(b => new { b.Address }).Distinct().ToList());
    }

    // Two distinct blogs with equal Title and Address: dedup compares projected values, so they collapse to one row.
    [Theory]
    [MemberData(nameof(FallbackModes))]
    public void Owned_reference_entity_leaf_Distinct_dedups_by_value_not_owner_identity(MongoQueryMode mode)
    {
        var collection = SeedDuplicateBlogs(nameof(Owned_reference_entity_leaf_Distinct_dedups_by_value_not_owner_identity) + mode);
        using var db = CreateContext(collection, mode, BlogModel);

        var result = db.Entities.AsNoTracking().Select(b => new { b.Title, b.Address }).Distinct().ToList();

        var row = Assert.Single(result);
        Assert.Equal(("Same", "NYC"), (row.Title, row.Address.City));
    }

    [Theory]
    [MemberData(nameof(FallbackModes))]
    public void Owned_reference_entity_leaf_Union_dedups_by_value_not_owner_identity(MongoQueryMode mode)
    {
        var collection = SeedDuplicateBlogs(nameof(Owned_reference_entity_leaf_Union_dedups_by_value_not_owner_identity) + mode);
        using var db = CreateContext(collection, mode, BlogModel);

        var result = db.Entities.AsNoTracking().Select(b => new { b.Title, b.Address })
            .Union(db.Entities.AsNoTracking().Select(b => new { b.Title, b.Address }))
            .ToList();

        var row = Assert.Single(result);
        Assert.Equal(("Same", "NYC"), (row.Title, row.Address.City));
    }

    // TrackAll rejects an owned entity projected without its owner (EF Core's own check). Identity resolution needs
    // the real owner key, which the projected documents don't carry, so it must fail rather than merge rows.
    [Theory]
    [InlineData(QueryTrackingBehavior.TrackAll)]
    [InlineData(QueryTrackingBehavior.NoTrackingWithIdentityResolution)]
    public void Owned_reference_entity_leaf_Concat_with_tracking_or_identity_resolution_does_not_return_merged_rows(
        QueryTrackingBehavior trackingBehavior)
    {
        var collection = SeedDuplicateBlogs(
            nameof(Owned_reference_entity_leaf_Concat_with_tracking_or_identity_resolution_does_not_return_merged_rows)
            + trackingBehavior);
        using var db = CreateContext(collection, MongoQueryMode.Native, BlogModel);
        db.ChangeTracker.QueryTrackingBehavior = trackingBehavior;

        Assert.Throws<InvalidOperationException>(() =>
            db.Entities.Select(b => new { b.Title, b.Address })
                .Concat(db.Entities.Select(b => new { b.Title, b.Address }))
                .ToList());
    }

    private IMongoCollection<Blog> SeedDuplicateBlogs(string name)
    {
        var coll = database.MongoDatabase.GetCollection<BsonDocument>(UniqueCollectionName(name));
        coll.InsertMany([
            new BsonDocument
            {
                { "_id", ObjectId.GenerateNewId() }, { "Title", "Same" },
                { "Address", new BsonDocument { { "City", "NYC" }, { "Zip", "10001" } } }
            },
            new BsonDocument
            {
                { "_id", ObjectId.GenerateNewId() }, { "Title", "Same" },
                { "Address", new BsonDocument { { "City", "NYC" }, { "Zip", "10001" } } }
            },
        ]);
        return database.MongoDatabase.GetCollection<Blog>(coll.CollectionNamespace.CollectionName);
    }

    // The same identity re-projection follows an owned-collection leaf; its elements also key off the owner _id.
    [Theory]
    [MemberData(nameof(FallbackModes))]
    public void Owned_collection_leaf_in_anonymous_type_with_Concat(MongoQueryMode mode)
    {
        var coll = database.MongoDatabase.GetCollection<BsonDocument>(UniqueCollectionName(
            nameof(Owned_collection_leaf_in_anonymous_type_with_Concat) + mode));
        coll.InsertMany([
            new BsonDocument
            {
                { "_id", ObjectId.GenerateNewId() }, { "Title", "Alpha" },
                { "Tags", new BsonArray { new BsonDocument("Name", "a1"), new BsonDocument("Name", "a2") } }
            },
            new BsonDocument
            {
                { "_id", ObjectId.GenerateNewId() }, { "Title", "Beta" },
                { "Tags", new BsonArray { new BsonDocument("Name", "b1") } }
            },
        ]);
        var collection = database.MongoDatabase.GetCollection<BlogWithTags>(coll.CollectionNamespace.CollectionName);
        using var db = CreateContext(collection, mode, BlogWithTagsModel);

        var result = db.Entities.AsNoTracking().Where(b => b.Title == "Alpha").Select(b => new { b.Title, b.Tags })
            .Concat(db.Entities.AsNoTracking().Where(b => b.Title == "Beta").Select(b => new { b.Title, b.Tags }))
            .ToList();

        Assert.Equal(
            [("Alpha", "a1,a2"), ("Beta", "b1")],
            result.Select(r => (r.Title, string.Join(",", r.Tags.Select(t => t.Name)))).OrderBy(r => r.Title).ToArray());
    }

    // A string owner key reads as a non-null placeholder too, so the owned entity isn't materialized as null.
    private class StringKeyedBlog
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public Address Address { get; set; } = null!;
    }

    [Theory]
    [MemberData(nameof(FallbackModes))]
    public void Owned_reference_entity_leaf_with_string_owner_key_in_anonymous_type_with_Union(MongoQueryMode mode)
    {
        var coll = database.MongoDatabase.GetCollection<BsonDocument>(UniqueCollectionName(
            nameof(Owned_reference_entity_leaf_with_string_owner_key_in_anonymous_type_with_Union) + mode));
        coll.InsertMany([
            new BsonDocument
            {
                { "_id", "a" }, { "Title", "Alpha" },
                { "Address", new BsonDocument { { "City", "NYC" }, { "Zip", "10001" } } }
            },
            new BsonDocument
            {
                { "_id", "b" }, { "Title", "Beta" },
                { "Address", new BsonDocument { { "City", "LA" }, { "Zip", "90001" } } }
            },
        ]);
        var collection = database.MongoDatabase.GetCollection<StringKeyedBlog>(coll.CollectionNamespace.CollectionName);
        using var db = CreateContext(collection, mode, mb => mb.Entity<StringKeyedBlog>().OwnsOne(b => b.Address));

        var result = db.Entities.AsNoTracking().Where(b => b.Title == "Alpha").Select(b => new { b.Title, b.Address })
            .Union(db.Entities.AsNoTracking().Where(b => b.Title == "Beta").Select(b => new { b.Title, b.Address }))
            .ToList();

        Assert.Equal(
            [("Alpha", "NYC"), ("Beta", "LA")],
            result.Select(r => (r.Title, r.Address.City)).OrderBy(r => r.Title).ToArray());
    }
}
