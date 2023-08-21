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
public class FirstSingleTests
{
    private readonly GuidesDbContext _db;

    public FirstSingleTests(SampleGuidesFixture fixture)
    {
        _db = GuidesDbContext.Create(fixture.MongoDatabase);
    }

    [Fact]
    public void First()
    {
        var result = _db.Planets.OrderBy(p => p.name).First();
        Assert.Equal("Earth", result.name);
    }

    [Fact]
    public void First_with_no_result()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => _db.Planets.Skip(100).First());
        Assert.Equal("Sequence contains no elements", ex.Message);
    }

    [Fact]
    public void First_predicate()
    {
        var result = _db.Planets.First(p => p.orderFromSun == 1);
        Assert.Equal("Mercury", result.name);
    }

    [Fact]
    public void First_predicate_no_results()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => _db.Planets.First(p => p.orderFromSun == 10));
        Assert.Equal("Sequence contains no elements", ex.Message);
    }

    [Fact]
    public void Single()
    {
        var result = _db.Planets.Where(p => p.name == "Earth").Single();
        Assert.Equal("Earth", result.name);
    }

    [Fact]
    public void Single_with_no_results()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => _db.Planets.Skip(100).Single());
        Assert.Equal("Sequence contains no elements", ex.Message);
    }

    [Fact]
    public void Single_with_more_than_one_result()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => _db.Planets.Skip(4).Single());
        Assert.Equal("Sequence contains more than one element", ex.Message);
    }

    [Fact]
    public void Single_predicate()
    {
        var result = _db.Planets.Single(p => p.orderFromSun == 1);
        Assert.Equal("Mercury", result.name);
    }

    [Fact]
    public void Single_predicate_no_results()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => _db.Planets.Single(p => p.orderFromSun == 10));
        Assert.Equal("Sequence contains no elements", ex.Message);
    }

    [Fact]
    public void Single_predicate_with_more_than_one_result()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => _db.Planets.Single(p => p.orderFromSun > 5));
        Assert.Equal("Sequence contains more than one element", ex.Message);
    }
}
