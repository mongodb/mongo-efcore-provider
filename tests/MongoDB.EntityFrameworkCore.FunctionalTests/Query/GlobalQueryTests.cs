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

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

[XUnitCollection(nameof(SampleGuidesFixture))]
public class GlobalQueryTests
{
    private readonly IMongoDatabase _mongoDatabase;

    public GlobalQueryTests(SampleGuidesFixture fixture)
    {
        _mongoDatabase = fixture.MongoDatabase;
    }

    [Fact]
    public void Global_query_filter_applies()
    {
        var db = SingleEntityDbContext.Create(_mongoDatabase.GetCollection<WhereTests.PlanetListVersion>("planets"), mb =>
        {
            mb.Entity<WhereTests.PlanetListVersion>().HasQueryFilter(p => p.hasRings == true);
        });

        var results = db.Entities.ToList();

        Assert.All(results, p => Assert.True(p.hasRings));
    }

    [Fact]
    public void Global_query_filter_combines_with_where()
    {
        var db = SingleEntityDbContext.Create(_mongoDatabase.GetCollection<WhereTests.PlanetListVersion>("planets"), mb =>
        {
            mb.Entity<WhereTests.PlanetListVersion>().HasQueryFilter(p => p.hasRings == true);
        });

        var results = db.Entities.Where(e => e.orderFromSun < 7).ToList();

        Assert.Equal(2, results.Count);
        Assert.All(results, p => Assert.True(p.hasRings && p.orderFromSun < 7));
    }

    [Fact]
    public void Global_query_filter_applies_to_first()
    {
        var db = SingleEntityDbContext.Create(_mongoDatabase.GetCollection<WhereTests.PlanetListVersion>("planets"), mb =>
        {
            mb.Entity<WhereTests.PlanetListVersion>().HasQueryFilter(p => p.hasRings == true);
        });

        var results = db.Entities.First();

        Assert.True(results.hasRings);
    }

    [Fact]
    public void Global_query_filter_can_be_ignored()
    {
        var db = SingleEntityDbContext.Create(_mongoDatabase.GetCollection<WhereTests.PlanetListVersion>("planets"), mb =>
        {
            mb.Entity<WhereTests.PlanetListVersion>().HasQueryFilter(p => p.hasRings == true);
        });

        var results = db.Entities.IgnoreQueryFilters().Where(e => e.orderFromSun < 7).ToList();

        Assert.Equal(6, results.Count);
        Assert.All(results, p => Assert.True(p.orderFromSun < 7));
    }
}
