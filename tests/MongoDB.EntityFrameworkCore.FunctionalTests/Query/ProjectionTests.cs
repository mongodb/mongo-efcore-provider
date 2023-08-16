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

using MongoDB.EntityFrameworkCore.FunctionalTests.Entities.Guides;
using XUnitCollection = Xunit.CollectionAttribute;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

[XUnitCollection(nameof(SampleGuidesFixture))]
public class ProjectionTests
{
    private readonly IQueryable<Planet> _planets;

    public ProjectionTests(SampleGuidesFixture fixture)
    {
        var db = GuidesDbContext.Create(fixture.MongoDatabase);
        _planets = db.Planets.Take(10);
    }

    [Fact]
    public void Select_projection_no_op()
    {
        var results = _planets.Select(p => p).ToArray();
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
        var results = _planets.Select(p => new {Name = p.name, Order = p.orderFromSun});
        Assert.All(results, r =>
        {
            Assert.NotNull(r.Name);
            Assert.InRange(r.Order, 1, 8);
        });
    }

    [Fact]
    public void Select_projection_to_array()
    {
        var results = _planets.Select(p => new object[] {p.name, p.orderFromSun});
        Assert.All(results, r =>
        {
            Assert.NotNull(r[0]);
            Assert.InRange(Assert.IsType<int>(r[1]), 1, 8);
        });
    }

    [Fact]
    public void Select_projection_to_tuple()
    {
        var results = _planets.Select(p => Tuple.Create(p.name, p.orderFromSun, p.hasRings));
        Assert.All(results, r =>
        {
            Assert.NotNull(r.Item1);
            Assert.InRange(r.Item2, 1, 8);
        });
    }

    [Fact]
    public void Select_projection_to_constructor_initializer()
    {
        var results = _planets.Select(p => new NamedContainer<Planet> {Name = p.name, Item = p});
        Assert.All(results, r => { Assert.Equal(r.Name, r?.Item?.name); });
    }

    [Fact(Skip = "Requires Select projection rewriting")]
    public void Select_projection_to_constructor_params()
    {
        var results = _planets.Select(p => new NamedContainer<Planet>(p, p.name));
        Assert.All(results, r => { Assert.Equal(r.Name, r?.Item?.name); });
    }


    [Fact(Skip = "Requires Select projection rewriting")]
    public void Select_projection_to_constructor_params_and_initializer()
    {
        var results = _planets.Select(p => new NamedContainer<Planet>(p) {Name = p.name});
        Assert.All(results, r => { Assert.Equal(r.Name, r?.Item?.name); });
    }
}
