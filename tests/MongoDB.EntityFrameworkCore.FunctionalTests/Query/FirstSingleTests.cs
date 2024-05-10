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
using MongoDB.EntityFrameworkCore.FunctionalTests.Entities.Guides;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

[XUnitCollection(nameof(SampleGuidesFixture))]
public class FirstSingleTests(SampleGuidesFixture fixture) : IDisposable, IAsyncDisposable
{
    private readonly GuidesDbContext _db = GuidesDbContext.Create(fixture.MongoDatabase);

    [Fact]
    public void First()
    {
        var result = _db.Planets.OrderBy(p => p.name).First();
        Assert.Equal("Earth", result.name);
    }

    [Fact]
    public async Task FirstAsync()
    {
        var result = await _db.Planets.OrderBy(p => p.name).FirstAsync();
        Assert.Equal("Earth", result.name);
    }

    [Fact]
    public void FirstOrDefault()
    {
        var result = _db.Planets.OrderBy(p => p.name).FirstOrDefault();
        Assert.NotNull(result);
        Assert.Equal("Earth", result.name);
    }

    [Fact]
    public async Task FirstOrDefaultAsync()
    {
        var result = await _db.Planets.OrderBy(p => p.name).FirstOrDefaultAsync();
        Assert.NotNull(result);
        Assert.Equal("Earth", result.name);
    }

    [Fact]
    public void FirstOrDefault_with_two_different_types_sequentially()
    {
        var planet = _db.Planets.FirstOrDefault();
        Assert.NotNull(planet);

        var moon = _db.Moons.FirstOrDefault();
        Assert.NotNull(moon);
    }

    [Fact]
    public void First_with_no_result()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => _db.Planets.Skip(100).First());
        Assert.Equal("Sequence contains no elements", ex.Message);
    }

    [Fact]
    public async Task FirstAsync_with_no_result()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _db.Planets.Skip(100).FirstAsync());
        Assert.Equal("Sequence contains no elements", ex.Message);
    }

    [Fact]
    public void FirstOrDefault_with_no_result()
    {
        var result = _db.Planets.Skip(100).FirstOrDefault();
        Assert.Null(result);
    }

    [Fact]
    public async Task FirstOrDefaultAsync_with_no_result()
    {
        var result = await _db.Planets.Skip(100).FirstOrDefaultAsync();
        Assert.Null(result);
    }

    [Fact]
    public void First_predicate()
    {
        var result = _db.Planets.First(p => p.orderFromSun == 1);
        Assert.Equal("Mercury", result.name);
    }

    [Fact]
    public async Task FirstAsync_predicate()
    {
        var result = await _db.Planets.FirstAsync(p => p.orderFromSun == 1);
        Assert.Equal("Mercury", result.name);
    }

    [Fact]
    public void FirstOrDefault_predicate()
    {
        var result = _db.Planets.FirstOrDefault(p => p.orderFromSun == 1);
        Assert.NotNull(result);
        Assert.Equal("Mercury", result.name);
    }

    [Fact]
    public async Task FirstOrDefaultAsync_predicate()
    {
        var result = await _db.Planets.FirstOrDefaultAsync(p => p.orderFromSun == 1);
        Assert.NotNull(result);
        Assert.Equal("Mercury", result.name);
    }

    [Fact]
    public void First_predicate_no_results()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => _db.Planets.First(p => p.orderFromSun == 10));
        Assert.Equal("Sequence contains no elements", ex.Message);
    }

    [Fact]
    public async Task FirstAsync_predicate_no_results()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _db.Planets.FirstAsync(p => p.orderFromSun == 10));
        Assert.Equal("Sequence contains no elements", ex.Message);
    }

    [Fact]
    public void FirstOrDefault_predicate_no_results()
    {
        var result = _db.Planets.FirstOrDefault(p => p.orderFromSun == 10);
        Assert.Null(result);
    }

    [Fact]
    public async Task FirstOrDefaultAsync_predicate_no_results()
    {
        var result = await _db.Planets.FirstOrDefaultAsync(p => p.orderFromSun == 10);
        Assert.Null(result);
    }

    [Fact]
    public void Single()
    {
        var result = _db.Planets.Where(p => p.name == "Earth").Single();
        Assert.Equal("Earth", result.name);
    }

    [Fact]
    public async Task SingleAsync()
    {
        var result = await _db.Planets.Where(p => p.name == "Earth").SingleAsync();
        Assert.Equal("Earth", result.name);
    }

    [Fact]
    public void SingleOrDefault()
    {
        var result = _db.Planets.Where(p => p.name == "Earth").SingleOrDefault();
        Assert.NotNull(result);
        Assert.Equal("Earth", result.name);
    }

    [Fact]
    public async Task SingleOrDefaultAsync()
    {
        var result = await _db.Planets.Where(p => p.name == "Earth").SingleOrDefaultAsync();
        Assert.NotNull(result);
        Assert.Equal("Earth", result.name);
    }

    [Fact]
    public void Single_with_no_results()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => _db.Planets.Skip(100).Single());
        Assert.Equal("Sequence contains no elements", ex.Message);
    }

    [Fact]
    public async Task SingleAsync_with_no_results()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _db.Planets.Skip(100).SingleAsync());
        Assert.Equal("Sequence contains no elements", ex.Message);
    }

    [Fact]
    public void SingleOrDefault_with_no_results()
    {
        var result = _db.Planets.Skip(100).SingleOrDefault();
        Assert.Null(result);
    }

    [Fact]
    public async Task SingleOrDefaultAsync_with_no_results()
    {
        var results = await _db.Planets.Skip(100).SingleOrDefaultAsync();
        Assert.Null(results);
    }

    [Fact]
    public void Single_with_more_than_one_result()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => _db.Planets.Skip(4).Single());
        Assert.Equal("Sequence contains more than one element", ex.Message);
    }

    [Fact]
    public async Task SingleAsync_with_more_than_one_result()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _db.Planets.Skip(4).SingleAsync());
        Assert.Equal("Sequence contains more than one element", ex.Message);
    }

    [Fact]
    public void SingleOrDefault_with_more_than_one_result()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => _db.Planets.Skip(4).SingleOrDefault());
        Assert.Equal("Sequence contains more than one element", ex.Message);
    }

    [Fact]
    public async Task SingleOrDefaultAsync_with_more_than_one_result()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _db.Planets.Skip(4).SingleOrDefaultAsync());
        Assert.Equal("Sequence contains more than one element", ex.Message);
    }

    [Fact]
    public void Single_predicate()
    {
        var result = _db.Planets.Single(p => p.orderFromSun == 1);
        Assert.Equal("Mercury", result.name);
    }

    [Fact]
    public async Task SingleAsync_predicate()
    {
        var result = await _db.Planets.SingleAsync(p => p.orderFromSun == 1);
        Assert.Equal("Mercury", result.name);
    }

    [Fact]
    public void SingleOrDefault_predicate()
    {
        var result = _db.Planets.SingleOrDefault(p => p.orderFromSun == 1);
        Assert.NotNull(result);
        Assert.Equal("Mercury", result.name);
    }

    [Fact]
    public async Task SingleOrDefaultAsync_predicate()
    {
        var result = await _db.Planets.SingleOrDefaultAsync(p => p.orderFromSun == 1);
        Assert.NotNull(result);
        Assert.Equal("Mercury", result.name);
    }

    [Fact]
    public void Single_predicate_no_results()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => _db.Planets.Single(p => p.orderFromSun == 10));
        Assert.Equal("Sequence contains no elements", ex.Message);
    }

    [Fact]
    public async Task SingleAsync_predicate_no_results()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _db.Planets.SingleAsync(p => p.orderFromSun == 10));
        Assert.Equal("Sequence contains no elements", ex.Message);
    }

    [Fact]
    public void SingleOrDefault_predicate_no_results()
    {
        var result = _db.Planets.SingleOrDefault(p => p.orderFromSun == 10);
        Assert.Null(result);
    }

    [Fact]
    public async Task SingleOrDefaultAsync_predicate_no_results()
    {
        var result = await _db.Planets.SingleOrDefaultAsync(p => p.orderFromSun == 10);
        Assert.Null(result);
    }

    [Fact]
    public void Single_predicate_with_more_than_one_result()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => _db.Planets.Single(p => p.orderFromSun > 5));
        Assert.Equal("Sequence contains more than one element", ex.Message);
    }

    [Fact]
    public async Task SingleAsync_predicate_with_more_than_one_result()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _db.Planets.SingleAsync(p => p.orderFromSun > 5));
        Assert.Equal("Sequence contains more than one element", ex.Message);
    }

    [Fact]
    public void SingleOrDefault_predicate_with_more_than_one_result()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => _db.Planets.SingleOrDefault(p => p.orderFromSun > 5));
        Assert.Equal("Sequence contains more than one element", ex.Message);
    }

    [Fact]
    public async Task SingleOrDefaultAsync_predicate_with_more_than_one_result()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _db.Planets.SingleOrDefaultAsync(p => p.orderFromSun > 5));
        Assert.Equal("Sequence contains more than one element", ex.Message);
    }

    public void Dispose()
        => _db.Dispose();

    public async ValueTask DisposeAsync()
        => await _db.DisposeAsync();
}
