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
using Microsoft.EntityFrameworkCore.Internal;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Linq;
using MongoDB.EntityFrameworkCore.FunctionalTests.Entities.Guides;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

[XUnitCollection(nameof(ReadOnlySampleGuidesFixture))]
public sealed class UnsupportedQueriesTests(ReadOnlySampleGuidesFixture database)
    : IDisposable, IAsyncDisposable
{
    private readonly GuidesDbContext _db = GuidesDbContext.Create(database.MongoDatabase);

    [Fact]
    public void Join_cannot_be_translated()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            _db.Planets.Join(_db.Moons, p => p._id, m => m.planetId, (p, m) => new { p, m }).ToList());
        Assert.Contains(".Join(", ex.Message);
        Assert.Contains(" could not be translated", ex.Message);
    }

#if EF10
    [Fact]
    public void LeftJoin_throws_not_supported_exception()
    {
        Assert.Throws<NotSupportedException>(
            () => _db.Planets.LeftJoin(_db.Moons, p => p._id, m => m.planetId, (p, m) => new {p, m}).ToList());
    }
#endif

    [Fact]
    public void SelectMany_throws_because_target_is_not_primitive()
    {
        var collection = database.MongoDatabase.GetCollection<Customer>("Customers");
        var db = SingleEntityDbContext.Create(collection);

        Assert.Throws<InvalidOperationException>(() => db.Entities.SelectMany(c => c.aliases).ToList());
    }

    [Fact]
    public void Contains_cannot_match_entities()
    {
        var earth = _db.Planets.Single(p => p.name == "Earth");

        var ex = Assert.Throws<NotSupportedException>(() => _db.Planets.Contains(earth));

        Assert.Contains("Entity to entity comparison is not supported", ex.Message);
        Assert.Contains("_id", ex.Message);
    }

    [Fact]
    public async Task ContainsAsync_cannot_match_entities()
    {
        var earth = await EntityFrameworkQueryableExtensions.SingleAsync(_db.Planets, p => p.name == "Earth");

        var ex = await Assert.ThrowsAsync<NotSupportedException>(() => _db.Planets.ContainsAsync(earth));

        Assert.Contains("Entity to entity comparison is not supported", ex.Message);
        Assert.Contains("_id", ex.Message);
    }

    [Fact]
    public void Except_can_not_be_translated()
    {
        var closerThanEarth = new List<Planet>([new Planet { _id = ObjectId.GenerateNewId(), name = "Earth" }]);

        var ex = Assert.Throws<InvalidOperationException>(() => _db.Planets.Except(closerThanEarth).ToList());

        Assert.Contains(".Except(", ex.Message);
        Assert.Contains(" could not be translated", ex.Message);
    }

    [Fact]
    public void Intersect_cannot_be_translated()
    {
        var closerThanEarth = new List<Planet>([new Planet { _id = ObjectId.GenerateNewId(), name = "Earth" }]);

        var ex = Assert.Throws<InvalidOperationException>(() => _db.Planets.Intersect(closerThanEarth).ToList());

        Assert.Contains(".Intersect(", ex.Message);
        Assert.Contains(" could not be translated", ex.Message);
    }

    [Fact]
    public void Select_cannot_select_foreign_navigation()
    {
        using var db = new ClientContext(database.MongoDatabase);

        var ex = Assert.Throws<InvalidOperationException>(() => db.Clients.Select(p => p.Company).ToList());

        Assert.Contains(".Select(", ex.Message);
        Assert.Contains(" could not be translated", ex.Message);
        Assert.Contains("p.Company", ex.Message);
    }

    [Fact]
    public void Cast_to_child_not_supported_in_driver()
    {
        var ex = Assert.Throws<ExpressionNotSupportedException>(() => _db.Planets.Cast<SuperPlanet>().ToList());

        Assert.Contains(".As", ex.Message);
    }

    class SuperPlanet : Planet;

    [Fact]
    public void SelectMany_cannot_translate_array()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => _db.Planets.SelectMany(p => p.mainAtmosphere).ToList());

        Assert.Contains("p => p.mainAtmosphere", ex.Message);
    }

    [Fact]
    public void GroupBy_cannot_be_translated()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => _db.Planets.GroupBy(p => p.hasRings).ToList());

        Assert.Contains(".GroupBy(", ex.Message);
        Assert.Contains(" could not be translated", ex.Message);
        Assert.Contains("p.hasRings", ex.Message);
    }

    [Fact]
    public void GroupBy_with_element_selector_cannot_be_translated()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => _db.Planets.GroupBy(p => p.hasRings, p => p.name).ToList());

        Assert.Contains(".GroupBy(", ex.Message);
        Assert.Contains(" could not be translated", ex.Message);
        Assert.Contains("p.hasRings", ex.Message);
    }

    public void Dispose()
        => _db.Dispose();

    public async ValueTask DisposeAsync()
        => await _db.DisposeAsync();

    public class Customer
    {
        public ObjectId _id { get; set; }
        public string name { get; set; }
        public string[] aliases { get; set; }
    }

    public class Client : Customer
    {
        public Company Company { get; set; }
    }

    public class Company
    {
        public ObjectId _id { get; set; }
        public string name { get; set; }
    }

    public class ClientContext(IMongoDatabase mongoDatabase) : DbContext
    {
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
            => base.OnConfiguring(optionsBuilder
                .UseMongoDB(mongoDatabase.Client, mongoDatabase.DatabaseNamespace.DatabaseName)
                .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
            );

        public DbSet<Client> Clients { get; set; }
        public DbSet<Company> Companies { get; set; }
    }
}
