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

using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.FunctionalTests.Entities.Guides;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

[XUnitCollection(nameof(ReadOnlySampleGuidesFixture))]
public class ProjectionTests(ReadOnlySampleGuidesFixture database)
    : IDisposable, IAsyncDisposable
{
    private readonly GuidesDbContext _db = GuidesDbContext.Create(database.MongoDatabase);

    [Fact]
    public void Select_projection_no_op()
    {
        var results = _db.Planets.Take(10).Select(p => p).ToArray();
        Assert.Equal(8, results.Length);
        Assert.All(results, r =>
        {
            Assert.NotNull(r.name);
            Assert.InRange(r.orderFromSun, 1, 8);
        });
    }

    [Fact]
    public void Select_projection_to_anonymous()
    {
        var results = _db.Planets.Take(10).Select(p => new {Name = p.name, Order = p.orderFromSun});
        Assert.All(results, r =>
        {
            Assert.NotNull(r.Name);
            Assert.InRange(r.Order, 1, 8);
        });
    }

    [Fact]
    public void Select_projection_to_anonymous_via_mql_field()
    {
        var results = _db.Planets.Take(10).Select(p => new
        {
            Name = Mql.Field(p, "name", StringSerializer.Instance),
            Order = Mql.Field(p, "orderFromSun", Int32Serializer.Instance)
        });
        Assert.All(results, r =>
        {
            Assert.NotNull(r.Name);
            Assert.InRange(r.Order, 1, 8);
        });
    }

    [Fact]
    public void Select_projection_to_tuple()
    {
        var results = _db.Planets.Take(10).Select(p => Tuple.Create(p.name, p.orderFromSun, p.hasRings));
        Assert.All(results, r =>
        {
            Assert.NotNull(r.Item1);
            Assert.InRange(r.Item2, 1, 8);
        });
    }

    [Fact]
    public void Select_projection_to_tuple_with_expressions()
    {
        var results = _db.Planets.Take(10).Select(p => Tuple.Create(p.name + "X", p.orderFromSun > 3, p.hasRings ? "Rings" : "No rings"));
        Assert.All(results, r =>
        {
            Assert.NotNull(r.Item1);
            //Assert.InRange(r.Item2, 1, 8);
        });
    }

    [Fact(Skip = "Projections not yet completely supported")]
    public void Select_projection_to_constructor_initializer()
    {
        var results = _db.Planets.Take(10).Select(p => new NamedContainer<Planet> {Name = p.name, Item = p});
        Assert.All(results, r => { Assert.Equal(r.Name, r.Item?.name); });
    }

    [Fact(Skip = "Requires Select projection rewriting")]
    public void Select_projection_to_constructor_params()
    {
        var results = _db.Planets.Take(10).Select(p => new NamedContainer<Planet>(p, p.name));
        Assert.All(results, r => { Assert.Equal(r.Name, r.Item?.name); });
    }

    [Fact(Skip = "Requires Select projection rewriting")]
    public void Select_projection_to_constructor_params_and_initializer()
    {
        var results = _db.Planets.Take(10).Select(p => new NamedContainer<Planet>(p) {Name = p.name});
        Assert.All(results, r => { Assert.Equal(r.Name, r.Item?.name); });
    }

    public void Dispose()
        => _db.Dispose();

    public async ValueTask DisposeAsync()
        => await _db.DisposeAsync();
}
