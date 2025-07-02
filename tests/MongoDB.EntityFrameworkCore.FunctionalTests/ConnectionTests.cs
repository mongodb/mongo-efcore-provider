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

public class ConnectionTests
{
    [Fact]
    public void Can_connect_using_connection_string()
    {
        var optionsBuilder = StartOptions()
            .UseMongoDB(TestServer.ConnectionString, TemporaryDatabaseFixture.GetUniqueDatabaseName);

        using var context = new SingleEntityDbContext<BasicEntity>(optionsBuilder.Options, nameof(Can_connect_using_connection_string));

        context.Database.EnsureCreated();
    }

    [Fact]
    public void Can_connect_using_mongo_client_settings()
    {
        var clientSettings = MongoClientSettings.FromConnectionString(TestServer.ConnectionString);

        var optionsBuilder = StartOptions()
            .UseMongoDB(clientSettings, TemporaryDatabaseFixture.GetUniqueDatabaseName);

        using var context = new SingleEntityDbContext<BasicEntity>(optionsBuilder.Options, nameof(Can_connect_using_mongo_client_settings));

        context.Database.EnsureCreated();
    }

    [Fact]
    public void Can_connect_using_mongo_client()
    {
        var client = new MongoClient(TestServer.ConnectionString);

        var optionsBuilder = StartOptions()
            .UseMongoDB(client, TemporaryDatabaseFixture.GetUniqueDatabaseName);

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
