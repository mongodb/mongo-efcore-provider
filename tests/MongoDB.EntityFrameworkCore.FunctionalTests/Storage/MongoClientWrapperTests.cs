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
using MongoDB.Bson;
using MongoDB.Driver;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.Metadata;
using MongoDB.EntityFrameworkCore.Storage;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Storage;

[XUnitCollection("StorageTests")]
public class MongoClientWrapperTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CreateDatabase_creates_database_and_collections(bool async)
    {
        var database = await TemporaryDatabaseFixture.CreateInitializedAsync();

        var context = MyContext.CreateCollectionOptions(database.MongoDatabase);
        var client = context.GetService<IMongoClientWrapper>();

        {
            var didCreate = async
                ? await client.CreateDatabaseAsync(context.GetService<IDesignTimeModel>())
                : client.CreateDatabase(context.GetService<IDesignTimeModel>());
            Assert.True(didCreate);
            var collectionNames = database.MongoDatabase.ListCollectionNames().ToList();
            Assert.Equal(3, collectionNames.Count);
            Assert.Contains("Customers", collectionNames);
            Assert.Contains("Orders", collectionNames);
            Assert.Contains("Addresses", collectionNames);
        }

        {
            var didCreate = async
                ? await client.CreateDatabaseAsync(context.GetService<IDesignTimeModel>())
                : client.CreateDatabase(context.GetService<IDesignTimeModel>());
            Assert.False(didCreate);
            var collectionNames = database.MongoDatabase.ListCollectionNames().ToList();
            Assert.Equal(3, collectionNames.Count);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CreateDatabase_can_be_configured_to_not_create_missing_collections(bool async)
    {
        var database = await TemporaryDatabaseFixture.CreateInitializedAsync();

        var context = MyContext.CreateCollectionOptions(database.MongoDatabase);
        var client = context.GetService<IMongoClientWrapper>();

        var options = new MongoDatabaseCreationOptions(CreateMissingCollections: false);

        var didCreate = async
            ? await client.CreateDatabaseAsync(context.GetService<IDesignTimeModel>(), options, seedAsync: null)
            : client.CreateDatabase(context.GetService<IDesignTimeModel>(), options, seed: null);

        Assert.True(didCreate);
        Assert.Empty(database.MongoDatabase.ListCollectionNames().ToList());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CreateDatabase_creates_indexes(bool async)
    {
        var database = await TemporaryDatabaseFixture.CreateInitializedAsync();

        var context = MyContext.CreateCollectionOptions(database.MongoDatabase, mb =>
        {
            mb.Entity<Customer>().HasIndex(c => c.Name);
            mb.Entity<Order>().HasIndex(o => o.OrderRef).IsUnique();
            mb.Entity<Address>().HasIndex(o => o.PostCode, "custom_index_name");
        });
        var client = context.GetService<IMongoClientWrapper>();

        var didCreate = async
            ? await client.CreateDatabaseAsync(context.GetService<IDesignTimeModel>())
            : client.CreateDatabase(context.GetService<IDesignTimeModel>());

        Assert.True(didCreate);
        Assert.Equal(2, GetIndexes(database.MongoDatabase, "Customers").Count);
        Assert.Equal(2, GetIndexes(database.MongoDatabase, "Orders").Count);
        Assert.Equal(2, GetIndexes(database.MongoDatabase, "Addresses").Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CreateDatabase_index_creation_can_be_deferred(bool async)
    {
        var database = await TemporaryDatabaseFixture.CreateInitializedAsync();

        var context = MyContext.CreateCollectionOptions(database.MongoDatabase, mb =>
        {
            mb.Entity<Customer>().HasIndex(c => c.Name);
            mb.Entity<Order>().HasIndex(o => o.OrderRef).IsUnique();
            mb.Entity<Address>().HasIndex(o => o.PostCode, "custom_index_name");
        });
        var client = context.GetService<IMongoClientWrapper>();

        var designTimeModel = context.GetService<IDesignTimeModel>();
        var options = new MongoDatabaseCreationOptions(CreateMissingIndexes: false);

        var didCreate = async
            ? await client.CreateDatabaseAsync(designTimeModel, options, seedAsync: null)
            : client.CreateDatabase(designTimeModel, options, seed: null);

        Assert.True(didCreate);
        Assert.Single(GetIndexes(database.MongoDatabase, "Customers"));
        Assert.Single(GetIndexes(database.MongoDatabase, "Orders"));
        Assert.Single(GetIndexes(database.MongoDatabase, "Addresses"));

        if (async)
        {
            await client.CreateMissingIndexesAsync(designTimeModel.Model);
        }
        else
        {
            client.CreateMissingIndexes(designTimeModel.Model);
        }

        Assert.Equal(2, GetIndexes(database.MongoDatabase, "Customers").Count);
        Assert.Equal(2, GetIndexes(database.MongoDatabase, "Orders").Count);
        Assert.Equal(2, GetIndexes(database.MongoDatabase, "Addresses").Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CreateDatabase_creates_nested_index_on_owns_one(bool async)
    {
        var database = await TemporaryDatabaseFixture.CreateInitializedAsync();

        var collection = database.CreateCollection<Product>(values: async);
        var context = SingleEntityDbContext.Create(collection, mb =>
        {
            mb.Entity<Product>(p =>
            {
                p.HasIndex(o => o.Name);
                p.OwnsOne(q => q.PrimaryCertificate, q => { q.HasIndex(r => r.Name); });
            });
        });
        var client = context.GetService<IMongoClientWrapper>();

        _ = async
            ? await client.CreateDatabaseAsync(context.GetService<IDesignTimeModel>())
            : client.CreateDatabase(context.GetService<IDesignTimeModel>());

        var indexList = async
            ? (await collection.Indexes.ListAsync()).ToList()
            : collection.Indexes.List().ToList();

        Assert.Equal(3, indexList.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CreateDatabase_creates_nested_index_on_owns_one_can_be_deferred(bool async)
    {
        var database = await TemporaryDatabaseFixture.CreateInitializedAsync();

        var collection = database.CreateCollection<Product>(values: async);
        var context = SingleEntityDbContext.Create(collection, mb =>
        {
            mb.Entity<Product>(p =>
            {
                p.HasIndex(o => o.Name);
                p.OwnsOne(q => q.PrimaryCertificate, q => { q.HasIndex(r => r.Name); });
            });
        });
        var client = context.GetService<IMongoClientWrapper>();

        var designTimeModel = context.GetService<IDesignTimeModel>();
        var options = new MongoDatabaseCreationOptions(CreateMissingIndexes: false);

        _ = async
            ? await client.CreateDatabaseAsync(designTimeModel, options, seedAsync: null)
            : client.CreateDatabase(designTimeModel, options, seed: null);

        var indexList = async
            ? (await collection.Indexes.ListAsync()).ToList()
            : collection.Indexes.List().ToList();

        Assert.Single(indexList);

        if (async)
        {
            await client.CreateMissingIndexesAsync(designTimeModel.Model);
        }
        else
        {
            client.CreateMissingIndexes(designTimeModel.Model);
        }

        indexList = async
            ? (await collection.Indexes.ListAsync()).ToList()
            : collection.Indexes.List().ToList();

        Assert.Equal(3, indexList.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CreateDatabase_creates_nested_index_on_owns_many(bool async)
    {
        var database = await TemporaryDatabaseFixture.CreateInitializedAsync();

        var collection = database.CreateCollection<Product>(values: async);
        var context = SingleEntityDbContext.Create(collection, mb =>
        {
            mb.Entity<Product>(p =>
            {
                p.HasIndex(o => o.Name);
                p.OwnsMany(q => q.SecondaryCertificates, q => { q.HasIndex(r => r.Name); });
            });
        });
        var client = context.GetService<IMongoClientWrapper>();

        _ = async
            ? await client.CreateDatabaseAsync(context.GetService<IDesignTimeModel>())
            : client.CreateDatabase(context.GetService<IDesignTimeModel>());

        var indexList = async
            ? (await collection.Indexes.ListAsync()).ToList()
            : collection.Indexes.List().ToList();

        Assert.Equal(3, indexList.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CreateDatabase_creates_nested_index_on_owns_many_can_be_deferred(bool async)
    {
        var database = await TemporaryDatabaseFixture.CreateInitializedAsync();

        var collection = database.CreateCollection<Product>(values: async);
        var context = SingleEntityDbContext.Create(collection, mb =>
        {
            mb.Entity<Product>(p =>
            {
                p.HasIndex(o => o.Name);
                p.OwnsMany(q => q.SecondaryCertificates, q => { q.HasIndex(r => r.Name); });
            });
        });
        var client = context.GetService<IMongoClientWrapper>();

        var designTimeModel = context.GetService<IDesignTimeModel>();
        var options = new MongoDatabaseCreationOptions(CreateMissingIndexes: false);

        _ = async
            ? await client.CreateDatabaseAsync(designTimeModel, options, seedAsync: null)
            : client.CreateDatabase(designTimeModel, options, seed: null);

        var indexList = async
            ? (await collection.Indexes.ListAsync()).ToList()
            : collection.Indexes.List().ToList();

        Assert.Single(indexList);

        if (async)
        {
            await client.CreateMissingIndexesAsync(designTimeModel.Model);
        }
        else
        {
            client.CreateMissingIndexes(designTimeModel.Model);
        }

        indexList = async
            ? (await collection.Indexes.ListAsync()).ToList()
            : collection.Indexes.List().ToList();

        Assert.Equal(3, indexList.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CreateDatabase_creates_nested_index_on_owns_many_owns_one(bool async)
    {
        var database = await TemporaryDatabaseFixture.CreateInitializedAsync();

        var collection = database.CreateCollection<Product>(values: async);
        var context = SingleEntityDbContext.Create(collection, mb =>
        {
            mb.Entity<Product>(p =>
            {
                p.OwnsMany(q => q.SecondaryCertificates, q =>
                {
                    q.OwnsOne(c => c.Issuer, i =>
                    {
                        i.HasIndex(j => j.OrganizationName);
                        i.HasIndex(j => j.Country);
                    });
                });
            });
        });
        var client = context.GetService<IMongoClientWrapper>();

        _ = async
            ? await client.CreateDatabaseAsync(context.GetService<IDesignTimeModel>())
            : client.CreateDatabase(context.GetService<IDesignTimeModel>());

        var indexList = async
            ? (await collection.Indexes.ListAsync()).ToList()
            : collection.Indexes.List().ToList();

        Assert.Equal(3, indexList.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CreateDatabase_creates_alternate_keys(bool async)
    {
        var database = await TemporaryDatabaseFixture.CreateInitializedAsync();

        var context = MyContext.CreateCollectionOptions(database.MongoDatabase, mb =>
        {
            mb.Entity<Customer>(e =>
            {
                e.HasAlternateKey(c => c.SSN);
                e.HasAlternateKey(c => c.TIN);
                e.Property(c => c.SSN).HasElementName("ssn");
            });
            mb.Entity<Order>().HasAlternateKey(o => new { o.OrderRef, o.CustomerId });
            mb.Entity<Address>().HasAlternateKey(o => o.UniqueRef);
        });
        var client = context.GetService<IMongoClientWrapper>();

        var didCreate = async
            ? await client.CreateDatabaseAsync(context.GetService<IDesignTimeModel>())
            : client.CreateDatabase(context.GetService<IDesignTimeModel>());

        Assert.True(didCreate);

        var customerIndexes = GetIndexes(database.MongoDatabase, "Customers");
        Assert.Equal(3, customerIndexes.Count);
        var customerSsnKey = Assert.Single(customerIndexes, i => i["name"] == "ssn_1");
        Assert.Equal(BsonBoolean.True, customerSsnKey["unique"]);
        Assert.Equal(new BsonDocument("ssn", 1), customerSsnKey["key"]);
        var customerTinKey = Assert.Single(customerIndexes, i => i["name"] == "TIN_1");
        Assert.Equal(BsonBoolean.True, customerTinKey["unique"]);
        Assert.Equal(new BsonDocument("TIN", 1), customerTinKey["key"]);

        var orderIndexes = GetIndexes(database.MongoDatabase, "Orders");
        Assert.Equal(2, orderIndexes.Count);
        var orderAlternateKeyIndex = Assert.Single(orderIndexes, i => i["name"] == "OrderRef_1_CustomerId_1");
        Assert.Equal(BsonBoolean.True, orderAlternateKeyIndex["unique"]);
        Assert.Equal(new BsonDocument { ["OrderRef"] = 1, ["CustomerId"] = 1 }, orderAlternateKeyIndex["key"]);

        var addressIndexes = GetIndexes(database.MongoDatabase, "Addresses");
        Assert.Equal(2, addressIndexes.Count);
        var addressAlternateKeyIndex = Assert.Single(addressIndexes, i => i["name"] == "UniqueRef_1");
        Assert.Equal(BsonBoolean.True, addressAlternateKeyIndex["unique"]);
        Assert.Equal(new BsonDocument("UniqueRef", 1), addressAlternateKeyIndex["key"]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CreateDatabase_does_not_duplicate_indexes(bool async)
    {
        var database = await TemporaryDatabaseFixture.CreateInitializedAsync();


        {
            var context = MyContext.CreateCollectionOptions(database.MongoDatabase,
                mb =>
                {
                    mb.Entity<Address>().HasIndex(o => o.PostCode, "custom_index_name");
                    mb.Entity<Order>().HasAlternateKey(o => o.OrderRef);
                }
            );
            var client = context.GetService<IMongoClientWrapper>();

            var didCreate = async
                ? await client.CreateDatabaseAsync(context.GetService<IDesignTimeModel>())
                : client.CreateDatabase(context.GetService<IDesignTimeModel>());

            Assert.True(didCreate);
            Assert.Single(GetIndexes(database.MongoDatabase, "Customers"));
            Assert.Equal(2, GetIndexes(database.MongoDatabase, "Addresses").Count);
            Assert.Equal(2, GetIndexes(database.MongoDatabase, "Orders").Count);
        }

        {
            var context = MyContext.CreateCollectionOptions(database.MongoDatabase, mb =>
            {
                mb.Entity<Customer>().HasIndex(c => c.Name);
                mb.Entity<Address>().HasIndex(o => o.PostCode, "custom_index_name");
                mb.Entity<Order>().HasAlternateKey(o => o.OrderRef);
            });
            var client = context.GetService<IMongoClientWrapper>();

            var didCreate = async
                ? await client.CreateDatabaseAsync(context.GetService<IDesignTimeModel>())
                : client.CreateDatabase(context.GetService<IDesignTimeModel>());

            Assert.False(didCreate);
            Assert.Equal(2, GetIndexes(database.MongoDatabase, "Customers").Count);
            Assert.Equal(2, GetIndexes(database.MongoDatabase, "Addresses").Count);
            Assert.Equal(2, GetIndexes(database.MongoDatabase, "Orders").Count);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CreateDatabase_creates_index_from_string_named_properties(bool async)
    {
        var database = await TemporaryDatabaseFixture.CreateInitializedAsync();

        var context = MyContext.CreateCollectionOptions(database.MongoDatabase,
            mb => mb.Entity<Address>().HasIndex("PostCode"));
        var client = context.GetService<IMongoClientWrapper>();

        _ = async
            ? await client.CreateDatabaseAsync(context.GetService<IDesignTimeModel>())
            : client.CreateDatabase(context.GetService<IDesignTimeModel>());

        var indexes = GetIndexes(database.MongoDatabase, "Addresses");
        Assert.Equal(2, indexes.Count);
        Assert.Single(indexes, i => i["key"].AsBsonDocument.Names.Single() == "PostCode");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CreateDatabase_creates_index_from_multiple_string_named_properties(bool async)
    {
        var database = await TemporaryDatabaseFixture.CreateInitializedAsync();

        var context = MyContext.CreateCollectionOptions(database.MongoDatabase,
            mb => mb.Entity<Address>().HasIndex("Country", "PostCode"));
        var client = context.GetService<IMongoClientWrapper>();

        _ = async
            ? await client.CreateDatabaseAsync(context.GetService<IDesignTimeModel>())
            : client.CreateDatabase(context.GetService<IDesignTimeModel>());

        var indexes = GetIndexes(database.MongoDatabase, "Addresses");
        Assert.Equal(2, indexes.Count);

        var foundIndex = Assert.Single(indexes, i => i["key"].AsBsonDocument.Names.Count() == 2);
        Assert.Contains(foundIndex["key"].AsBsonDocument, key => key.Name == "Country");
        Assert.Contains(foundIndex["key"].AsBsonDocument, key => key.Name == "PostCode");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CreateDatabase_creates_index_with_descending_property(bool async)
    {
        var database = await TemporaryDatabaseFixture.CreateInitializedAsync();

        var context = MyContext.CreateCollectionOptions(database.MongoDatabase,
            mb => mb.Entity<Address>().HasIndex(a => a.PostCode).IsDescending(true));
        var client = context.GetService<IMongoClientWrapper>();

        _ = async
            ? await client.CreateDatabaseAsync(context.GetService<IDesignTimeModel>())
            : client.CreateDatabase(context.GetService<IDesignTimeModel>());

        var indexes = GetIndexes(database.MongoDatabase, "Addresses");
        Assert.Equal(2, indexes.Count);

        var foundIndex = Assert.Single(indexes, i => i["key"].AsBsonDocument.Names.Contains("PostCode"));
        Assert.Equal(-1, foundIndex["key"].AsBsonDocument["PostCode"].AsInt32);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CreateDatabase_creates_index_with_two_descending_properties(bool async)
    {
        var database = await TemporaryDatabaseFixture.CreateInitializedAsync();

        var context = MyContext.CreateCollectionOptions(database.MongoDatabase,
            mb => mb.Entity<Address>().HasIndex(a => new { a.PostCode, a.Country }).IsDescending());
        var client = context.GetService<IMongoClientWrapper>();

        _ = async
            ? await client.CreateDatabaseAsync(context.GetService<IDesignTimeModel>())
            : client.CreateDatabase(context.GetService<IDesignTimeModel>());

        var indexes = GetIndexes(database.MongoDatabase, "Addresses");
        Assert.Equal(2, indexes.Count);

        var foundIndex = Assert.Single(indexes, i => i["key"].AsBsonDocument.Names.Contains("PostCode"));
        Assert.Equal(-1, foundIndex["key"].AsBsonDocument["PostCode"].AsInt32);
        Assert.Equal(-1, foundIndex["key"].AsBsonDocument["Country"].AsInt32);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CreateDatabase_creates_index_with_two_properties_mixed_sort_order(bool async)
    {
        var database = await TemporaryDatabaseFixture.CreateInitializedAsync();

        var context = MyContext.CreateCollectionOptions(database.MongoDatabase,
            mb => mb.Entity<Address>().HasIndex(a => new { a.PostCode, a.Country }).IsDescending(false, true));
        var client = context.GetService<IMongoClientWrapper>();

        _ = async
            ? await client.CreateDatabaseAsync(context.GetService<IDesignTimeModel>())
            : client.CreateDatabase(context.GetService<IDesignTimeModel>());

        var indexes = GetIndexes(database.MongoDatabase, "Addresses");
        Assert.Equal(2, indexes.Count);

        var foundIndex = Assert.Single(indexes, i => i["key"].AsBsonDocument.Names.Contains("PostCode"));
        Assert.Equal(1, foundIndex["key"].AsBsonDocument["PostCode"].AsInt32);
        Assert.Equal(-1, foundIndex["key"].AsBsonDocument["Country"].AsInt32);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CreateDatabase_creates_index_with_two_properties_unique_descending(bool async)
    {
        var database = await TemporaryDatabaseFixture.CreateInitializedAsync();

        var context = MyContext.CreateCollectionOptions(database.MongoDatabase,
            mb => mb.Entity<Address>().HasIndex(a => new { a.PostCode, a.Country }).IsUnique().IsDescending());
        var client = context.GetService<IMongoClientWrapper>();

        _ = async
            ? await client.CreateDatabaseAsync(context.GetService<IDesignTimeModel>())
            : client.CreateDatabase(context.GetService<IDesignTimeModel>());

        var indexes = GetIndexes(database.MongoDatabase, "Addresses");
        Assert.Equal(2, indexes.Count);

        var foundIndex = Assert.Single(indexes, i => i["key"].AsBsonDocument.Names.Contains("PostCode"));
        Assert.Equal(-1, foundIndex["key"].AsBsonDocument["PostCode"].AsInt32);
        Assert.Equal(-1, foundIndex["key"].AsBsonDocument["Country"].AsInt32);
        Assert.Single(indexes, i => i.Names.Contains("unique") && i["unique"].AsBoolean);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CreateDatabase_creates_index_with_filter(bool async)
    {
        var filter = Builders<BsonDocument>.Filter.Eq(a => a["Country"], "UK");
        var options = new CreateIndexOptions<BsonDocument> { PartialFilterExpression = filter };
        var database = await TemporaryDatabaseFixture.CreateInitializedAsync();

        var context = MyContext.CreateCollectionOptions(database.MongoDatabase,
            mb => mb.Entity<Address>().HasIndex(a => a.PostCode).HasCreateIndexOptions(options));
        var client = context.GetService<IMongoClientWrapper>();

        _ = async
            ? await client.CreateDatabaseAsync(context.GetService<IDesignTimeModel>())
            : client.CreateDatabase(context.GetService<IDesignTimeModel>());

        var indexes = database.MongoDatabase.GetCollection<BsonDocument>("Addresses").Indexes.List().ToList();
        Assert.Single(indexes,
            i => i.Names.Contains("partialFilterExpression")
                 && i["partialFilterExpression"].ToString() == "{ \"Country\" : \"UK\" }");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CreateDatabase_creates_index_with_create_index_options(bool async)
    {
        var options = new CreateIndexOptions { Sparse = true, Unique = true };
        var database = await TemporaryDatabaseFixture.CreateInitializedAsync();

        var context = MyContext.CreateCollectionOptions(database.MongoDatabase,
            mb => mb.Entity<Address>().HasIndex(a => a.PostCode).HasCreateIndexOptions(options));
        var client = context.GetService<IMongoClientWrapper>();

        _ = async
            ? await client.CreateDatabaseAsync(context.GetService<IDesignTimeModel>())
            : client.CreateDatabase(context.GetService<IDesignTimeModel>());

        var indexes = database.MongoDatabase.GetCollection<BsonDocument>("Addresses").Indexes.List().ToList();
        Assert.Single(indexes,
            i => i.Names.Contains("sparse") && i["sparse"].AsBoolean && i.Names.Contains("unique") && i["unique"].AsBoolean);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CreateDatabase_does_not_affect_existing_collections(bool async)
    {
        const int expectedMaxDocs = 1024;
        const int expectedMaxSize = 4096;

        var database = await TemporaryDatabaseFixture.CreateInitializedAsync();

        {
            var collection = database.MongoDatabase.GetCollection<Customer>("Customers");
            collection.InsertOne(new Customer { Name = "John Doe" });
            database.MongoDatabase.CreateCollection("Orders",
                new CreateCollectionOptions { MaxDocuments = expectedMaxDocs, MaxSize = expectedMaxSize, Capped = true });
            database.MongoDatabase.CreateCollection("Orders2");
        }

        {
            var context = MyContext.CreateCollectionOptions(database.MongoDatabase);
            var client = context.GetService<IMongoClientWrapper>();

            var didCreate = async
                ? await client.CreateDatabaseAsync(context.GetService<IDesignTimeModel>())
                : client.CreateDatabase(context.GetService<IDesignTimeModel>());

            Assert.False(didCreate);
            var collections = database.MongoDatabase.ListCollections().ToList();
            var allNames = collections.Select(c => c["name"].AsString).ToArray();
            Assert.Equal(4, allNames.Length);
            Assert.Contains("Customers", allNames);
            Assert.Contains("Orders", allNames);
            Assert.Contains("Addresses", allNames);

            var customerCollectionOptions = collections.Single(c => c["name"].AsString == "Orders")["options"];
            Assert.True(customerCollectionOptions["capped"].AsBoolean);
            Assert.Equal(expectedMaxDocs, customerCollectionOptions["max"].AsInt32);
            Assert.Equal(expectedMaxSize, customerCollectionOptions["size"].AsInt32);

            var collection = database.MongoDatabase.GetCollection<Customer>("Customers");
            Assert.Equal(1, collection.CountDocuments(FilterDefinition<Customer>.Empty));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DeleteDatabase_deletes_database(bool async)
    {
        var database = await TemporaryDatabaseFixture.CreateInitializedAsync();

        var context = MyContext.CreateCollectionOptions(database.MongoDatabase);
        var client = context.GetService<IMongoClientWrapper>();

        Assert.False(client.DatabaseExists());

        client.CreateDatabase(context.GetService<IDesignTimeModel>());
        Assert.True(client.DatabaseExists());

        _ = async ? await client.DeleteDatabaseAsync() : client.DeleteDatabase();
        Assert.False(client.DatabaseExists());
    }

    class MyContext(
        DbContextOptions options,
        Action<ModelBuilder>? modelBuilderAction = null)
        : DbContext(options)
    {
        public DbSet<Customer> Customers { get; set; }
        public DbSet<Order> Orders { get; set; }
        public DbSet<Address> Addresses { get; set; }

        private static DbContextOptions<MyContext> CreateOptions(IMongoDatabase database)
            => new DbContextOptionsBuilder<MyContext>()
                .UseMongoDB(database.Client, database.DatabaseNamespace.DatabaseName)
                .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
                .ReplaceService<IModelCacheKeyFactory, IgnoreCacheKeyFactory>()
                .Options;

        public static MyContext CreateCollectionOptions(
            IMongoDatabase database,
            Action<ModelBuilder>? modelBuilderAction = null)
            => new(CreateOptions(database), modelBuilderAction);

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilderAction?.Invoke(modelBuilder);
        }
    }

    class Customer
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; }
        public string SSN { get; set; }
        public string TIN { get; set; }
    }

    class Order
    {
        public ObjectId Id { get; set; }
        public string CustomerId { get; set; }
        public string OrderRef { get; set; }
    }

    class Address
    {
        public ObjectId Id { get; set; }
        public string PostCode { get; set; }
        public string Country { get; set; }
        public string Region { get; set; }
        public string UniqueRef { get; set; }
    }

    class Product
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; }
        public Certificate PrimaryCertificate { get; set; }
        public List<Certificate> SecondaryCertificates { get; set; }
    }

    class Certificate
    {
        public string Name { get; set; }
        public Issuer? Issuer { get; set; }
    }

    class Issuer
    {
        public string OrganizationName { get; set; }
        public string Country { get; set; }
    }

    private static List<BsonDocument> GetIndexes(IMongoDatabase database, string collectionName)
        => database.GetCollection<BsonDocument>(collectionName).Indexes.List().ToList();
}
