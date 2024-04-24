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
public class TopScalarTests
{
    private readonly GuidesDbContext _db;

    public TopScalarTests(SampleGuidesFixture fixture)
    {
        _db = GuidesDbContext.Create(fixture.MongoDatabase);
    }

    [Fact]
    public void Count_with_no_predicate()
    {
        var result = _db.Planets.Count();
        Assert.Equal(8, result);
    }

    [Fact]
    public void Count_with_no_predicate_after_where()
    {
        var result = _db.Planets.Where(p => p.hasRings).Count();
        Assert.Equal(4, result);
    }

    [Fact]
    public void Count_with_predicate()
    {
        var result = _db.Planets.Count(p => p.hasRings);
        Assert.Equal(4, result);
    }

    [Fact]
    public void Count_with_predicate_after_where()
    {
        var result = _db.Planets.Where(p => p.orderFromSun > 5).Count(p => p.hasRings);
        Assert.Equal(3, result);
    }

    [Fact]
    public async Task CountAsync_with_no_predicate()
    {
        var result = await _db.Planets.CountAsync();
        Assert.Equal(8, result);
    }


    [Fact]
    public async Task CountAsync_with_no_predicate_after_where()
    {
        var result = await _db.Planets.Where(p => p.hasRings).CountAsync();
        Assert.Equal(4, result);
    }

    [Fact]
    public async Task CountAsync_with_predicate()
    {
        var result = await _db.Planets.CountAsync(p => p.hasRings);
        Assert.Equal(4, result);
    }

    [Fact]
    public async Task CountAsync_with_predicate_after_where()
    {
        var result = await _db.Planets.Where(p => p.orderFromSun > 5).CountAsync(p => p.hasRings);
        Assert.Equal(3, result);
    }

    [Fact]
    public void LongCount_with_no_predicate()
    {
        var result = _db.Planets.LongCount();
        Assert.Equal(8, result);
    }

    [Fact]
    public void LongCount_with_no_predicate_after_where()
    {
        var result = _db.Planets.Where(p => p.hasRings).LongCount();
        Assert.Equal(4, result);
    }

    [Fact]
    public void LongCount_with_predicate()
    {
        var result = _db.Planets.LongCount(p => p.hasRings);
        Assert.Equal(4, result);
    }

    [Fact]
    public void LongCount_with_predicate_after_where()
    {
        var result = _db.Planets.Where(p => p.orderFromSun > 5).LongCount(p => p.hasRings);
        Assert.Equal(3, result);
    }

    [Fact]
    public async Task LongCountAsync_with_no_predicate()
    {
        var result = await _db.Planets.LongCountAsync();
        Assert.Equal(8, result);
    }

    [Fact]
    public async Task LongCountAsync_with_no_predicate_after_where()
    {
        var result = await _db.Planets.Where(p => p.hasRings).LongCountAsync();
        Assert.Equal(4, result);
    }

    [Fact]
    public async Task LongCountAsync_with_predicate()
    {
        var result = await _db.Planets.LongCountAsync(p => p.hasRings);
        Assert.Equal(4, result);
    }

    [Fact]
    public async Task LongCountAsync_with_predicate_after_where()
    {
        var result = await _db.Planets.Where(p => p.orderFromSun > 6).LongCountAsync(p => p.hasRings);
        Assert.Equal(2, result);
    }

    [Fact]
    public void Any_with_no_predicate()
    {
        var result = _db.Planets.Any();
        Assert.True(result);
    }

    [Fact]
    public void Any_with_no_predicate_after_where()
    {
        var result = _db.Planets.Where(p => p.orderFromSun < 1).Any();
        Assert.False(result);
    }

    [Fact]
    public void Any_with_predicate()
    {
        var result = _db.Planets.Any(p => p.hasRings);
        Assert.True(result);
    }

    [Fact]
    public void Any_with_predicate_after_where()
    {
        var result = _db.Planets.Where(p => p.orderFromSun < 5).Any(p => p.hasRings);
        Assert.False(result);
    }

    [Fact]
    public async Task AnyAsync_with_no_predicate()
    {
        var result = await _db.Planets.AnyAsync();
        Assert.True(result);
    }

    [Fact]
    public async Task AnyAsync_with_no_predicate_after_where()
    {
        var result = await _db.Planets.Where(p => p.orderFromSun < 1).AnyAsync();
        Assert.False(result);
    }

    [Fact]
    public async Task AnyAsync_with_predicate()
    {
        var result = await _db.Planets.AnyAsync(p => p.hasRings);
        Assert.True(result);
    }

    [Fact]
    public async Task AnyAsync_with_predicate_after_where()
    {
        var result = await _db.Planets.Where(p => p.orderFromSun < 5).AnyAsync(p => p.hasRings);
        Assert.False(result);
    }
}
