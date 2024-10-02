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

using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.FunctionalTests.Entities.Guides;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Storage;

[XUnitCollection("StorageTests")]
public class MongoDatabaseCreatorTests
{
    [Fact]
    public void EnsureCreated_returns_true_when_database_did_not_exist()
    {
        var database = new TemporaryDatabaseFixture();
        using var db = GuidesDbContext.Create(database.MongoDatabase);

        Assert.True(db.Database.EnsureCreated());
    }

    [Fact]
    public void EnsureCreated_returns_false_when_database_already_exists()
    {
        var database = new TemporaryDatabaseFixture();
        using var db = GuidesDbContext.Create(database.MongoDatabase);

        database.CreateCollection<Planet>(); // Force DB to actually exist

        Assert.False(db.Database.EnsureCreated());
    }

    [Fact]
    public async Task EnsureCreatedAsync_returns_true_when_database_did_not_exist()
    {
        var database = new TemporaryDatabaseFixture();
        await using var db = GuidesDbContext.Create(database.MongoDatabase);

        Assert.True(await db.Database.EnsureCreatedAsync());
    }

    [Fact]
    public async Task EnsureCreatedAsync_returns_false_when_database_already_exists()
    {
        var database = new TemporaryDatabaseFixture();
        await using var db = GuidesDbContext.Create(database.MongoDatabase);

        database.CreateCollection<Planet>(); // Force DB to actually exist

        Assert.False(await db.Database.EnsureCreatedAsync());
    }

    [Fact]
    public void EnsureDeleted_returns_true_and_deletes_database_when_it_exists()
    {
        var database = new TemporaryDatabaseFixture();
        using var db = GuidesDbContext.Create(database.MongoDatabase);

        database.CreateCollection<Planet>(); // Force DB to actually exist

        Assert.True(db.Database.EnsureDeleted());
        var databaseNames = database.Client.ListDatabaseNames().ToList();
        Assert.DoesNotContain(database.MongoDatabase.DatabaseNamespace.DatabaseName, databaseNames);
    }

    [Fact]
    public void EnsureDeleted_returns_false_when_it_does_not_exist()
    {
        var database = new TemporaryDatabaseFixture();
        using var db = GuidesDbContext.Create(database.MongoDatabase);

        Assert.False(db.Database.EnsureDeleted());
    }

    [Fact]
    public async Task EnsureDeletedAsync_returns_true_and_deletes_database_when_it_exists()
    {
        var database = new TemporaryDatabaseFixture();
        await using var db = GuidesDbContext.Create(database.MongoDatabase);

        database.CreateCollection<Planet>(); // Force DB to actually exist

        Assert.True(await db.Database.EnsureDeletedAsync());
        var databaseNames = (await database.Client.ListDatabaseNamesAsync()).ToList();
        Assert.DoesNotContain(database.MongoDatabase.DatabaseNamespace.DatabaseName, databaseNames);
    }

    [Fact]
    public async Task EnsureDeletedAsync_returns_false_when_it_does_not_exist()
    {
        var database = new TemporaryDatabaseFixture();
        await using var db = GuidesDbContext.Create(database.MongoDatabase);

        Assert.False(await db.Database.EnsureDeletedAsync());
    }

    [Fact]
    public void CanConnect_returns_true_when_it_can_connect()
    {
        var database = new TemporaryDatabaseFixture();
        using var db = GuidesDbContext.Create(database.MongoDatabase);

        Assert.True(db.Database.CanConnect());
    }

    [Fact]
    public void CanConnect_returns_false_when_it_cannot_connect()
    {
        using var db = GuidesDbContext.Create(_fastFailClient.GetDatabase("will-never-connect"));

        Assert.False(db.Database.CanConnect());
    }

    [Fact]
    public async Task CanConnectAsync_returns_false_when_it_cannot_connect()
    {
        await using var db = GuidesDbContext.Create(_fastFailClient.GetDatabase("will-never-connect"));

        Assert.False(await db.Database.CanConnectAsync());
    }

    private readonly MongoClient _fastFailClient
        = new(new MongoClientSettings {
            Server = MongoServerAddress.Parse("localhost:27999"),
            ServerSelectionTimeout = TimeSpan.FromSeconds(0),
            ConnectTimeout = TimeSpan.FromSeconds(1) });
}
