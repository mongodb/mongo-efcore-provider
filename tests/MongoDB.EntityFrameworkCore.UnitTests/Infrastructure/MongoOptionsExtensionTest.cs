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
using MongoDB.Driver;

namespace MongoDB.EntityFrameworkCore.UnitTests.Infrastructure;

public static class MongoOptionsExtensionTest
{
    [Fact]
    public static void Can_set_connection_string_and_database_name()
    {
        var dbOptions = new MongoOptionsExtension()
            .WithConnectionString("mongodb://localhost:1234")
            .WithDatabaseName("MyDatabase");

        Assert.Equal("mongodb://localhost:1234", dbOptions.ConnectionString);
        Assert.Equal("MyDatabase", dbOptions.DatabaseName);
    }

    [Theory]
    [InlineData("SomeDatabase")]
    public static void Can_set_mongo_client_and_database_name(string databaseName)
    {
        var mongoClient = new MongoClient();

        var dbOptions = new MongoOptionsExtension()
            .WithMongoClient(mongoClient)
            .WithDatabaseName(databaseName);

        Assert.Same(mongoClient, dbOptions.MongoClient);
        Assert.Equal(databaseName, dbOptions.DatabaseName);
    }

    [Theory]
    [InlineData("SomeDatabase")]
    public static void Can_set_mongo_client_settings_and_database_name(string databaseName)
    {
        var mongoClientSettings = new MongoClientSettings();

        var dbOptions = new MongoOptionsExtension()
            .WithMongoClientSettings(mongoClientSettings)
            .WithDatabaseName(databaseName);

        Assert.Same(mongoClientSettings, dbOptions.MongoClientSettings);
        Assert.Equal(databaseName, dbOptions.DatabaseName);
    }

    [Fact]
    public static void Throws_if_both_connection_string_and_mongo_client_set()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new MongoOptionsExtension()
            .WithMongoClient(new MongoClient())
            .WithConnectionString("mongodb://localhost:1234"));
        Assert.Contains(nameof(MongoClient), ex.Message);
        Assert.Contains(nameof(MongoOptionsExtension.ConnectionString), ex.Message);
    }

    [Fact]
    public static void Throws_if_both_connection_string_and_mongo_client_settings_set()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new MongoOptionsExtension()
            .WithMongoClientSettings(new MongoClientSettings())
            .WithConnectionString("mongodb://localhost:1234"));
        Assert.Contains(nameof(MongoClientSettings), ex.Message);
        Assert.Contains(nameof(MongoOptionsExtension.ConnectionString), ex.Message);
    }

    [Fact]
    public static void Throws_if_both_mongo_client_and_mongo_client_settings_set()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new MongoOptionsExtension()
            .WithMongoClient(new MongoClient())
            .WithMongoClientSettings(new MongoClientSettings()));
        Assert.Contains(nameof(MongoClientSettings), ex.Message);
        Assert.Contains(nameof(MongoClient), ex.Message);
    }
}
