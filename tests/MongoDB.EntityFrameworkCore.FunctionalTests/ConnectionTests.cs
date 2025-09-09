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

namespace MongoDB.EntityFrameworkCore.FunctionalTests;

public class ConnectionTests : IClassFixture<TemporaryDatabaseFixture>
{
    public ConnectionTests(TemporaryDatabaseFixture fixture)
    {
        Fixture = fixture;
    }

    private TemporaryDatabaseFixture Fixture { get; }

    [Fact]
    public void Can_connect_using_connection_string_with_database_name()
    {
        var optionsBuilder = StartOptions()
            .UseMongoDB(Fixture.TestServer.ConnectionString, TestDatabaseNamer.GetUniqueDatabaseName());

        using var context = new SingleEntityDbContext<BasicEntity>(optionsBuilder.Options, nameof(Can_connect_using_connection_string_with_database_name));

        context.Database.EnsureCreated();
    }

    [Fact]
    public void Can_connect_using_connection_string_without_database_name()
    {
        var databaseName = TestDatabaseNamer.GetUniqueDatabaseName();
        var collectionName = nameof(Can_connect_using_connection_string_without_database_name);
        var mongoUrl = new MongoUrlBuilder(Fixture.TestServer.ConnectionString) { DatabaseName = databaseName };

        var optionsBuilder = StartOptions()
            .UseMongoDB(mongoUrl.ToString());

        using var context = new SingleEntityDbContext<BasicEntity>(optionsBuilder.Options, collectionName);

        context.Database.EnsureCreated();

        var client = new MongoClient(Fixture.TestServer.ConnectionString);
        var database = client.GetDatabase(databaseName);
        var collections = database.ListCollectionNames().ToList();
        Assert.Contains(collectionName, collections);
    }

    [Fact]
    public void Can_connect_using_mongo_client_settings()
    {
        var clientSettings = MongoClientSettings.FromConnectionString(Fixture.TestServer.ConnectionString);

        var optionsBuilder = StartOptions()
            .UseMongoDB(clientSettings, TestDatabaseNamer.GetUniqueDatabaseName());

        using var context = new SingleEntityDbContext<BasicEntity>(optionsBuilder.Options, nameof(Can_connect_using_mongo_client_settings));

        context.Database.EnsureCreated();
    }

    [Fact]
    public void Can_connect_using_mongo_client()
    {
        var client = new MongoClient(Fixture.TestServer.ConnectionString);

        var optionsBuilder = StartOptions()
            .UseMongoDB(client, TestDatabaseNamer.GetUniqueDatabaseName());

        using var context = new SingleEntityDbContext<BasicEntity>(optionsBuilder.Options, nameof(Can_connect_using_mongo_client));

        context.Database.EnsureCreated();
    }

    private static DbContextOptionsBuilder<SingleEntityDbContext<BasicEntity>> StartOptions()
        => new DbContextOptionsBuilder<SingleEntityDbContext<BasicEntity>>()
            .ReplaceService<IModelCacheKeyFactory, IgnoreCacheKeyFactory>()
            .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));

    private class BasicEntity
    {
        public ObjectId _id { get; set; }
    }
}
