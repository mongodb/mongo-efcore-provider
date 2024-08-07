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
using MongoDB.EntityFrameworkCore.Storage;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Storage;

public class MongoClientWrapperTests
{
    [Fact]
    public void CreateDatabase_creates_database()
    {
        using var database = new TemporaryDatabaseFixture();
        var context = MyContext.CreateCollectionOptions(database.MongoDatabase);
        var client = context.GetService<IMongoClientWrapper>();

        {
            var didCreate = client.CreateDatabase(context.Model);
            Assert.True(didCreate);
            var collectionNames = database.MongoDatabase.ListCollectionNames().ToList();
            Assert.Equal(3, collectionNames.Count);
            Assert.Contains("Customers", collectionNames);
            Assert.Contains("Orders", collectionNames);
            Assert.Contains("Addresses", collectionNames);
        }

        {
            var didCreateSecondTime = client.CreateDatabase(context.Model);
            Assert.False(didCreateSecondTime);
            var collectionNames = database.MongoDatabase.ListCollectionNames().ToList();
            Assert.Equal(3, collectionNames.Count);
        }
    }

    [Fact]
    public async Task CreateDatabaseAsync_creates_database()
    {
        await using var database = new TemporaryDatabaseFixture();
        var context = MyContext.CreateCollectionOptions(database.MongoDatabase);
        var client = context.GetService<IMongoClientWrapper>();

        {
            var didCreate = await client.CreateDatabaseAsync(context.Model);
            Assert.True(didCreate);
            var collectionNames = (await database.MongoDatabase.ListCollectionNamesAsync()).ToList();
            Assert.Equal(3, collectionNames.Count);
            Assert.Contains("Customers", collectionNames);
            Assert.Contains("Orders", collectionNames);
            Assert.Contains("Addresses", collectionNames);
        }

        {
            var didCreateSecondTime = await client.CreateDatabaseAsync(context.Model);
            Assert.False(didCreateSecondTime);
            var collectionNames = (await database.MongoDatabase.ListCollectionNamesAsync()).ToList();
            Assert.Equal(3, collectionNames.Count);
        }
    }

    [Fact]
    public void CreateDatabase_does_not_affect_existing_collections()
    {
        const int expectedMaxDocs = 1234;
        const int expectedMaxSize = 56789;

        using var database = new TemporaryDatabaseFixture();

        {
            database.MongoDatabase.CreateCollection("Customers",
                new CreateCollectionOptions {MaxDocuments = expectedMaxDocs, MaxSize = expectedMaxSize, Capped = true});
            var collection = database.MongoDatabase.GetCollection<Customer>("Customers");
            collection.InsertOne(new Customer {Name = "John Doe"});
        }

        {
            var context = MyContext.CreateCollectionOptions(database.MongoDatabase);
            var client = context.GetService<IMongoClientWrapper>();

            var didCreate = client.CreateDatabase(context.Model);
            Assert.False(didCreate);
            var collections = database.MongoDatabase.ListCollections().ToList();

            Assert.Equal(3, collections.Count);
            var allNames = collections.Select(c => c["name"].AsString).ToArray();
            Assert.Contains("Customers", allNames);
            Assert.Contains("Orders", allNames);
            Assert.Contains("Addresses", allNames);

            var collection = database.MongoDatabase.GetCollection<Customer>("Customers");
            Assert.Equal(1, collection.CountDocuments(FilterDefinition<Customer>.Empty));

            var customerCollectionOptions = collections.Single(c => c["name"].AsString == "Customers")["options"];
            Assert.True(customerCollectionOptions["capped"].AsBoolean);
            Assert.Equal(expectedMaxDocs, customerCollectionOptions["max"].AsInt32);
            Assert.Equal(expectedMaxSize, customerCollectionOptions["size"].AsInt32);
        }
    }

    [Fact]
    public async Task CreateDatabaseAsync_does_not_affect_existing_collections()
    {
        const long expectedMaxDocs = 1234;
        const long expectedMaxSize = 56789;

        await using var database = new TemporaryDatabaseFixture();

        {
            await database.MongoDatabase.CreateCollectionAsync("Customers",
                new CreateCollectionOptions {MaxDocuments = expectedMaxDocs, MaxSize = expectedMaxSize, Capped = true});
            var collection = database.MongoDatabase.GetCollection<Customer>("Customers");
            await collection.InsertOneAsync(new Customer {Name = "John Doe"});
        }

        {
            var context = MyContext.CreateCollectionOptions(database.MongoDatabase);
            var client = context.GetService<IMongoClientWrapper>();

            var didCreate = await client.CreateDatabaseAsync(context.Model);
            Assert.False(didCreate);
            var collections = (await database.MongoDatabase.ListCollectionsAsync()).ToList();

            var allNames = collections.Select(c => c["name"].AsString).ToArray();
            Assert.Contains("Customers", allNames);
            Assert.Contains("Orders", allNames);
            Assert.Contains("Addresses", allNames);

            var collection = database.MongoDatabase.GetCollection<Customer>("Customers");
            Assert.Equal(1, await collection.CountDocumentsAsync(FilterDefinition<Customer>.Empty));

            var customerCollectionOptions = collections.Single(c => c["name"].AsString == "Customers")["options"];
            Assert.True(customerCollectionOptions["capped"].AsBoolean);
            Assert.Equal(expectedMaxDocs, customerCollectionOptions["max"].AsInt32);
            Assert.Equal(expectedMaxSize, customerCollectionOptions["size"].AsInt32);
        }
    }

    [Fact]
    public void DeleteDatabase_deletes_database()
    {
        using var database = new TemporaryDatabaseFixture();
        var context = MyContext.CreateCollectionOptions(database.MongoDatabase);
        var client = context.GetService<IMongoClientWrapper>();

        Assert.False(client.DatabaseExists());

        client.CreateDatabase(context.Model);
        Assert.True(client.DatabaseExists());

        client.DeleteDatabase();
        Assert.False(client.DatabaseExists());
    }

    [Fact]
    public async Task DeleteDatabaseAsync_deletes_database()
    {
        await using var database = new TemporaryDatabaseFixture();
        var context = MyContext.CreateCollectionOptions(database.MongoDatabase);
        var client = context.GetService<IMongoClientWrapper>();

        Assert.False(await client.DatabaseExistsAsync());

        await client.CreateDatabaseAsync(context.Model);
        Assert.True(await client.DatabaseExistsAsync());

        await client.DeleteDatabaseAsync();
        Assert.False(await client.DatabaseExistsAsync());
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
    }

    class Order
    {
        public ObjectId Id { get; set; }
        public string OrderRef { get; set; }
    }

    class Address
    {
        public ObjectId Id { get; set; }
        public string PostCode { get; set; }
    }
}
