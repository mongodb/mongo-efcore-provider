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
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.TestModels.ConferencePlanner;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;

namespace MongoDB.EntityFrameworkCore.Tests.Extensions;

public class MongoDbContextOptionsExtensionsTest
{
    [Theory]
    [InlineData("mongodb://localhost:1234", "myDatabaseName")]
    public void Can_configure_connection_string_and_database_name(string connectionString, string databaseName)
    {
        var serviceCollection = new ServiceCollection();
        serviceCollection.AddMongo<ApplicationDbContext>(connectionString, databaseName, mongoOptions => { },
            dbContextOptions => { dbContextOptions.EnableDetailedErrors(); });

        var services = serviceCollection.BuildServiceProvider(validateScopes: true);
        using var serviceScope = services.GetRequiredService<IServiceScopeFactory>().CreateScope();
        var coreOptions = serviceScope.ServiceProvider
            .GetRequiredService<DbContextOptions<ApplicationDbContext>>().GetExtension<CoreOptionsExtension>();

        Assert.True(coreOptions.DetailedErrorsEnabled);

        var mongoOptions = serviceScope.ServiceProvider
            .GetRequiredService<DbContextOptions<ApplicationDbContext>>().GetExtension<MongoOptionsExtension>();

        Assert.Equal(connectionString, mongoOptions.ConnectionString);
        Assert.Equal(databaseName, mongoOptions.DatabaseName);
    }

    [Theory]
    [InlineData("myDatabaseName")]
    public void Can_configure_with_mongo_client_and_database_name(string databaseName)
    {
        var mongoClient = new MongoClient();

        var serviceCollection = new ServiceCollection();
        serviceCollection.AddMongo<ApplicationDbContext>(mongoClient, databaseName, mongoOptions => { },
            dbContextOptions => { dbContextOptions.EnableDetailedErrors(); });

        var services = serviceCollection.BuildServiceProvider(validateScopes: true);
        using var serviceScope = services.GetRequiredService<IServiceScopeFactory>().CreateScope();
        var mongoOptions = serviceScope.ServiceProvider
            .GetRequiredService<DbContextOptions<ApplicationDbContext>>().GetExtension<MongoOptionsExtension>();

        Assert.Equal(mongoClient, mongoOptions.MongoClient);
        Assert.Equal(databaseName, mongoOptions.DatabaseName);
    }

    [Theory]
    [InlineData("mongodb://localhost:1234", "myDatabaseName")]
    public void Throws_when_multiple_ef_providers_specified(string connectionString, string databaseName)
    {
        var options = new DbContextOptionsBuilder()
            .UseMongo(connectionString, databaseName)
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        var context = new DbContext(options);

        Assert.Contains(
            "Only a single database provider can be registered",
            Assert.Throws<InvalidOperationException>(() => context.Model).Message);
    }
}
