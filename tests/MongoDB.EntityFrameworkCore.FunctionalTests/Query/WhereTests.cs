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

using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.FunctionalTests.Entities.Guides;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

[XUnitCollection(nameof(SampleGuidesFixture))]
public class WhereTests
{
    private readonly IMongoDatabase _mongoDatabase;
    private readonly GuidesDbContext _db;

    public WhereTests(SampleGuidesFixture fixture)
    {
        _mongoDatabase = fixture.MongoDatabase;
        _db = GuidesDbContext.Create(fixture.MongoDatabase);
    }

    [Fact]
    public void Where_string_equal()
    {
        var results = _db.Planets.Where(p => p.name == "Saturn").ToArray();
        Assert.Single(results);
        Assert.Equal("Saturn", results[0].name);
    }

    [Fact]
    public void Where_string_not_equal()
    {
        var results = _db.Planets.Where(p => p.name != "Saturn").ToArray();
        Assert.All(results, p => Assert.NotEqual("Saturn", p.name));
    }

    [Fact]
    public void Where_bool_equal_true()
    {
        var results = _db.Planets.Where(p => p.hasRings == true).ToArray();
        Assert.All(results, p => Assert.True(p.hasRings));
    }

    [Fact]
    public void Where_bool_true()
    {
        var results = _db.Planets.Where(p => p.hasRings).ToArray();
        Assert.All(results, p => Assert.True(p.hasRings));
    }

    [Fact]
    public void Where_bool_equal_false()
    {
        var results = _db.Planets.Where(p => p.hasRings == false).ToArray();
        Assert.Equal(4, results.Length);
        Assert.All(results, p => Assert.False(p.hasRings));
    }

    [Fact]
    public void Where_bool_false()
    {
        var results = _db.Planets.Where(p => !p.hasRings).ToArray();
        Assert.Equal(4, results.Length);
        Assert.All(results, p => Assert.False(p.hasRings));
    }

    [Fact]
    public void Where_int_equal()
    {
        var results = _db.Planets.Where(p => p.orderFromSun == 1).ToArray();
        Assert.Single(results);
        Assert.Equal("Mercury", results[0].name);
        Assert.Equal(1, results[0].orderFromSun);
    }

    [Fact]
    public void Where_int_not_equal()
    {
        var results = _db.Planets.Where(p => p.orderFromSun != 1).ToArray();
        Assert.Equal(7, results.Length);
        Assert.All(results, p => Assert.NotEqual(1, p.orderFromSun));
    }

    [Fact]
    public void Where_int_greater_than()
    {
        var results = _db.Planets.Where(p => p.orderFromSun > 3).ToArray();
        Assert.Equal(5, results.Length);
        Assert.All(results, p => Assert.True(p.orderFromSun > 3));
    }

    [Fact]
    public void Where_int_greater_or_equal()
    {
        var results = _db.Planets.Where(p => p.orderFromSun >= 5).ToArray();
        Assert.Equal(4, results.Length);
        Assert.All(results, p => Assert.True(p.orderFromSun >= 5));
    }

    [Fact]
    public void Where_int_less_than()
    {
        var results = _db.Planets.Where(p => p.orderFromSun < 3).ToArray();
        Assert.Equal(2, results.Length);
        Assert.All(results, p => Assert.True(p.orderFromSun < 3));
    }

    [Fact]
    public void Where_int_less_or_equal()
    {
        var results = _db.Planets.Where(p => p.orderFromSun <= 6).ToArray();
        Assert.Equal(6, results.Length);
        Assert.All(results, p => Assert.True(p.orderFromSun <= 6));
    }

    [Fact]
    public void Where_int_eq_with_param()
    {
        int? prm = 1655;
        var results = _db.Moons.Where(m => m.yearOfDiscovery == prm).ToArray();
        Assert.Single(results);
    }

    [Fact]
    public void Where_int_eq_with_param_null()
    {
        int? prm = null;
        var results = _db.Moons.Where(m => m.yearOfDiscovery == prm).ToArray();
        Assert.Single(results);
    }

    [Fact]
    public void Where_string_eq_with_param()
    {
        var prm = "Earth";
        var results = _db.Planets.Where(p => p.name == prm).ToArray();
        Assert.Single(results);
    }

    [Fact]
    public void Where_string_eq_with_param_null()
    {
        string prm = null;
        var results = _db.Planets.Where(p => p.name == prm).ToArray();
        Assert.Empty(results);
    }

    [Fact]
    public void Where_string_array_contains()
    {
        var results = _db.Planets.Where(p => p.mainAtmosphere.Contains("H2")).ToArray();
        Assert.Equal(4, results.Length);
        Assert.All(results, p => Assert.Contains("H2", p.mainAtmosphere));
    }

    [Fact]
    public void Where_string_array_not_contains()
    {
        var results = _db.Planets.Where(p => !p.mainAtmosphere.Contains("H2")).ToArray();
        Assert.Equal(4, results.Length);
        Assert.All(results, p => Assert.DoesNotContain("H2", p.mainAtmosphere));
    }

    [Fact]
    public void Where_string_array_length()
    {
        var results = _db.Planets.Where(p => p.mainAtmosphere.Length == 3).ToArray();
        Assert.Equal(6, results.Length);
        Assert.All(results, p => Assert.Equal(3, p.mainAtmosphere.Length));
    }

    [Fact]
    public void Where_string_array_count()
    {
        var results = _db.Planets.Where(p => p.mainAtmosphere.Count() == 2).ToArray();
        Assert.Single(results);
        Assert.Equal(2, results[0].mainAtmosphere.Length);
    }

    [Fact]
    public void Where_string_array_any()
    {
        var results = _db.Planets.Where(p => p.mainAtmosphere.Any()).ToArray();
        Assert.Equal(7, results.Length);
        Assert.All(results, p => Assert.NotEmpty(p.mainAtmosphere));
    }

    [Fact]
    public void Where_objectId_equal()
    {
        var expectedId = new ObjectId("621ff30d2a3e781873fcb660");
        var results = _db.Planets.Where(p => p._id == expectedId).ToArray();
        Assert.Single(results);
        Assert.Equal(expectedId, results[0]._id);
    }

    internal class PlanetListVersion
    {
        public ObjectId _id { get; set; }
        public string name { get; set; }
        public int orderFromSun { get; set; }
        public bool hasRings { get; set; }
        public List<string> mainAtmosphere { get; set; }
    }

    [Fact]
    public void Where_string_list_contains()
    {
        var db = SingleEntityDbContext.Create(_mongoDatabase.GetCollection<PlanetListVersion>("planets"));
        var results = db.Entitites.Where(p => p.mainAtmosphere.Contains("H2")).ToArray();
        Assert.Equal(4, results.Length);
        Assert.All(results, p => Assert.Contains("H2", p.mainAtmosphere));
    }

    [Fact]
    public void Where_string_list_not_contains()
    {
        var db = SingleEntityDbContext.Create(_mongoDatabase.GetCollection<PlanetListVersion>("planets"));
        var results = db.Entitites.Where(p => !p.mainAtmosphere.Contains("H2")).ToArray();
        Assert.Equal(4, results.Length);
        Assert.All(results, p => Assert.DoesNotContain("H2", p.mainAtmosphere));
    }

    [Fact]
    public void Where_string_list_count()
    {
        var db = SingleEntityDbContext.Create(_mongoDatabase.GetCollection<PlanetListVersion>("planets"));
        var results = db.Entitites.Where(p => p.mainAtmosphere.Count == 2).ToArray();
        Assert.Single(results);
        Assert.Equal(2, results[0].mainAtmosphere.Count);
    }

    [Fact]
    public void Where_string_list_any()
    {
        var db = SingleEntityDbContext.Create(_mongoDatabase.GetCollection<PlanetListVersion>("planets"));
        var results = db.Entitites.Where(p => p.mainAtmosphere.Any()).ToArray();
        Assert.Equal(7, results.Length);
        Assert.All(results, p => Assert.NotEmpty(p.mainAtmosphere));
    }
}
