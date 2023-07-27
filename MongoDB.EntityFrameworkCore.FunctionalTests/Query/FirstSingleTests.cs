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

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

public static class FirstSingleTests
{
    private static readonly GuidesDbContext __db = GuidesDbContext.Create(TestServer.GetClient());

    [Fact]
    public static void First()
    {
        var result = __db.Planets.OrderBy(p => p.name).First();
        Assert.Equal("Earth", result.name);
    }

    [Fact]
    public static void First_with_no_result()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => __db.Planets.Skip(100).First());
        Assert.Equal("Sequence contains no elements", ex.Message);
    }

    [Fact]
    public static void First_predicate()
    {
        var result = __db.Planets.First(p => p.orderFromSun == 1);
        Assert.Equal("Mercury", result.name);
    }

    [Fact]
    public static void First_predicate_no_results()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => __db.Planets.First(p => p.orderFromSun == 10));
        Assert.Equal("Sequence contains no elements", ex.Message);
    }

    [Fact]
    public static void Single()
    {
        var result = __db.Planets.Where(p => p.name == "Earth").Single();
        Assert.Equal("Earth", result.name);
    }

    [Fact]
    public static void Single_with_no_results()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => __db.Planets.Skip(100).Single());
        Assert.Equal("Sequence contains no elements", ex.Message);
    }

    [Fact]
    public static void Single_with_more_than_one_result()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => __db.Planets.Skip(4).Single());
        Assert.Equal("Sequence contains more than one element", ex.Message);
    }

    [Fact]
    public static void Single_predicate()
    {
        var result = __db.Planets.Single(p => p.orderFromSun == 1);
        Assert.Equal("Mercury", result.name);
    }

    [Fact]
    public static void Single_predicate_no_results()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => __db.Planets.Single(p => p.orderFromSun == 10));
        Assert.Equal("Sequence contains no elements", ex.Message);
    }

    [Fact]
    public static void Single_predicate_with_more_than_one_result()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => __db.Planets.Single(p => p.orderFromSun > 5));
        Assert.Equal("Sequence contains more than one element", ex.Message);
    }

}
