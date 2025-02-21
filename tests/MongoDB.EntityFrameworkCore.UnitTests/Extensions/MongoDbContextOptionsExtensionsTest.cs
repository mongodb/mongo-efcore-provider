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
using Microsoft.EntityFrameworkCore.TestModels.ConferencePlanner;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;

namespace MongoDB.EntityFrameworkCore.UnitTests.Extensions;

public static class MongoDbContextOptionsExtensionsTest
{
    [Theory]
    [InlineData("mongodb://localhost:1234", "myDatabaseName")]
    [InlineData("mongodb://localhost:1234,localhost:27017", "replicaSet")]
    public static void Can_configure_connection_string_and_database_name(string connectionString, string databaseName)
    {
        var serviceCollection = new ServiceCollection();
        serviceCollection.AddMongoDB<ApplicationDbContext>(connectionString, databaseName, _ => { },
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
    public static void Can_configure_with_mongo_client_and_database_name(string databaseName)
    {
        var mongoClient = new MongoClient();

        var serviceCollection = new ServiceCollection();
        serviceCollection.AddMongoDB<ApplicationDbContext>(mongoClient, databaseName, _ => { },
            dbContextOptions => { dbContextOptions.EnableDetailedErrors(); });

        var services = serviceCollection.BuildServiceProvider(validateScopes: true);
        using var serviceScope = services.GetRequiredService<IServiceScopeFactory>().CreateScope();
        var mongoOptions = serviceScope.ServiceProvider
            .GetRequiredService<DbContextOptions<ApplicationDbContext>>().GetExtension<MongoOptionsExtension>();

        Assert.Equal(mongoClient, mongoOptions.MongoClient);
        Assert.Equal(databaseName, mongoOptions.DatabaseName);
    }

    [Fact]
    public static void LogFragment_does_not_contain_password()
    {
        var optionsBuilder = new DbContextOptionsBuilder()
            .UseMongoDB(
                "mongodb://myDbUsr:NotActuallyAP%40ssw0rd@m0.example.com:27017,m2.example.com:27017,m2.example.com:27017/?authSource=admin",
                "db")
            .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));

        var extension = optionsBuilder.Options.FindExtension<MongoOptionsExtension>();
        var logFragment = extension?.Info.LogFragment;

        Assert.DoesNotContain("NotActuallyA", logFragment);
        Assert.Contains("myDbUsr:redacted@", logFragment);
        Assert.Contains("?authSource=admin", logFragment);
    }
}
