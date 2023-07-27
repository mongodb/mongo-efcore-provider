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
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

public static class ProjectionTests
{
    private static readonly GuidesDbContext __db = GuidesDbContext.Create(TestServer.GetClient());

    // TODO: Remove take 2 when projection capture fixed
    private static readonly IQueryable<Planet> __planets = __db.Planets.Take(2);

    [Fact]
    public static void Select_projection_to_anonymous()
    {
        var results = __planets.Select(p => new {Name = p.name, Order = p.orderFromSun}).ToList();

        Assert.All(results, r =>
        {
            Assert.NotNull(r.Name);
            Assert.InRange(r.Order, 1, 8);
        });
    }

    [Fact]
    public static void Select_projection_to_tuple()
    {
        var results = __planets.Select(p => Tuple.Create(p.name, p.orderFromSun, p.hasRings)).ToList();

        Assert.All(results, r =>
        {
            Assert.NotNull(r.Item1);
            Assert.InRange(r.Item2, 1, 8);
        });
    }

    [Fact]
    public static void Select_projection_to_constructor_params()
    {
        var results = __planets.Select(p => new NamedContainer<Planet>(p, p.name));

        Assert.All(results, r => { Assert.Equal(r.Name, r?.Item?.name); });
    }

    [Fact]
    public static void Select_projection_to_constructor_initializer()
    {
        var results = __planets.Select(p => new NamedContainer<Planet> {Name = p.name, Item = p});

        Assert.All(results, r => { Assert.Equal(r.Name, r?.Item?.name); });
    }

    [Fact]
    public static void Select_projection_to_constructor_params_and_initializer()
    {
        var results = __planets.Select(p => new NamedContainer<Planet>(p) {Name = p.name});

        Assert.All(results, r => { Assert.Equal(r.Name, r?.Item?.name); });
    }
}
