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
using MongoDB.EntityFrameworkCore.FunctionalTests.Entities.Guides;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

public static class WhereTests
{
    private static readonly GuidesDbContext __db = GuidesDbContext.Create(TestServer.GetClient());

    [Fact]
    public static void Where_string_equal()
    {
        var results = __db.Planets.Where(p => p.name == "Saturn").ToArray();

        Assert.Single(results);
        Assert.Equal("Saturn", results[0].name);
    }

    [Fact]
    public static void Where_string_not_equal()
    {
        var results = __db.Planets.Where(p => p.name != "Saturn").ToArray();

        Assert.All(results, p => Assert.NotEqual("Saturn", p.name));
    }

    [Fact]
    public static void Where_bool_equal_true()
    {
        var results = __db.Planets.Where(p => p.hasRings == true).ToArray();

        Assert.All(results, p => Assert.True(p.hasRings));
    }

    [Fact]
    public static void Where_bool_equal_true_no_constant()
    {
        var results = __db.Planets.Where(p => p.hasRings).ToArray();

        Assert.All(results, p => Assert.True(p.hasRings));
    }

    [Fact]
    public static void Where_bool_equal_false()
    {
        var results = __db.Planets.Where(p => p.hasRings == false).ToArray();

        Assert.All(results, p => Assert.False(p.hasRings));
    }

    [Fact]
    public static void Where_bool_equal_false_no_constant()
    {
        var results = __db.Planets.Where(p => !p.hasRings).ToArray();

        Assert.All(results, p => Assert.False(p.hasRings));
    }

    [Fact]
    public static void Where_int_equal()
    {
        var results = __db.Planets.Where(p => p.orderFromSun == 1).ToArray();

        Assert.Single(results);
        Assert.Equal("Mercury", results[0].name);
        Assert.Equal(1, results[0].orderFromSun);
    }

    [Fact]
    public static void Where_int_not_equal()
    {
        var results = __db.Planets.Where(p => p.orderFromSun != 1).ToArray();

        Assert.All(results, p => Assert.NotEqual(1, p.orderFromSun));
    }

    [Fact]
    public static void Where_int_greater_than()
    {
        var results = __db.Planets.Where(p => p.orderFromSun > 3).ToArray();

        Assert.All(results, p => Assert.True(p.orderFromSun > 3));
    }

    [Fact]
    public static void Where_int_greater_or_equal()
    {
        var results = __db.Planets.Where(p => p.orderFromSun >= 5).ToArray();

        Assert.All(results, p => Assert.True(p.orderFromSun >= 5));
    }

    [Fact]
    public static void Where_int_less_than()
    {
        var results = __db.Planets.Where(p => p.orderFromSun < 3).ToArray();

        Assert.All(results, p => Assert.True(p.orderFromSun < 3));
    }

    [Fact]
    public static void Where_int_less_or_equal()
    {
        var results = __db.Planets.Where(p => p.orderFromSun <= 6).ToArray();

        Assert.All(results, p => Assert.True(p.orderFromSun <= 6));
    }

    [Fact]
    public static void Where_string_array_contains()
    {
        var results = __db.Planets.Where(p => p.mainAtmosphere.Contains("H2")).ToArray();

        Assert.All(results, p => Assert.Contains("H2", p.mainAtmosphere));
    }

    [Fact]
    public static void Where_string_array_not_contains()
    {
        var results = __db.Planets.Where(p => !p.mainAtmosphere.Contains("H2")).ToArray();

        Assert.All(results, p => Assert.DoesNotContain("H2", p.mainAtmosphere));
    }

    [Fact]
    public static void Where_objectId_equal()
    {
        var expectedId = new ObjectId("621ff30d2a3e781873fcb660");
        var results = __db.Planets.Where(p => p._id == expectedId).ToArray();

        Assert.Single(results);
        Assert.Equal(expectedId, results[0]._id);
    }
}
