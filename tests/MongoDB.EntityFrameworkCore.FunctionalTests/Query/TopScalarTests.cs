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

[XUnitCollection(nameof(ReadOnlySampleGuidesFixture))]
public class TopScalarTests(ReadOnlySampleGuidesFixture database)
    : IDisposable, IAsyncDisposable
{
    private readonly GuidesDbContext _db = GuidesDbContext.Create(database.MongoDatabase);

    [Fact]
    public void All_with_selector()
    {
        var all = _db.Planets.All(p => p.orderFromSun > 0);

        Assert.True(all);
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

    [Fact]
    public void Average_with_selector()
    {
        var average = _db.Planets.Average(p => p.orderFromSun);

        Assert.Equal(4.5, average);
    }

    [Fact]
    public void Average_without_selector()
    {
        var average = _db.Planets.Select(p => p.orderFromSun).Average();

        Assert.Equal(4.5, average);
    }

    [Fact]
    public async Task AverageAsync_with_selector()
    {
        var average = await _db.Planets.AverageAsync(p => p.orderFromSun);

        Assert.Equal(4.5, average);
    }

    [Fact]
    public async Task AverageAsync_without_selector()
    {
        var average = await _db.Planets.Select(p => p.orderFromSun).AverageAsync();

        Assert.Equal(4.5, average);
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
    public void Sum_with_selector()
    {
        var sum = _db.Planets.Sum(p => p.orderFromSun);

        Assert.Equal(36, sum);
    }

    [Fact]
    public void Sum_without_selector()
    {
        var sum = _db.Planets.Select(p => p.orderFromSun).Sum();

        Assert.Equal(36, sum);
    }

    [Fact]
    public async Task SumSync_with_selector()
    {
        var sum = await _db.Planets.SumAsync(p => p.orderFromSun);

        Assert.Equal(36, sum);
    }

    [Fact]
    public async Task SumSync_without_selector()
    {
        var sum = await _db.Planets.Select(p => p.orderFromSun).SumAsync();

        Assert.Equal(36, sum);
    }

    [Fact]
    public void Max_with_selector()
    {
        var sum = _db.Planets.Max(p => p.orderFromSun);

        Assert.Equal(8, sum);
    }

    [Fact]
    public void Max_without_selector()
    {
        var sum = _db.Planets.Select(p => p.orderFromSun).Max();

        Assert.Equal(8, sum);
    }

    [Fact]
    public async Task MaxAsync_with_selector()
    {
        var sum = await _db.Planets.MaxAsync(p => p.orderFromSun);

        Assert.Equal(8, sum);
    }

    [Fact]
    public async Task MaxAsync_without_selector()
    {
        var sum = await _db.Planets.Select(p => p.orderFromSun).MaxAsync();

        Assert.Equal(8, sum);
    }

    [Fact]
    public void Min_with_selector()
    {
        var sum = _db.Planets.Min(p => p.orderFromSun);

        Assert.Equal(1, sum);
    }

    [Fact]
    public void Min_without_selector()
    {
        var sum = _db.Planets.Select(p => p.orderFromSun).Min();

        Assert.Equal(1, sum);
    }

    [Fact]
    public async Task MinAsync_with_selector()
    {
        var sum = await _db.Planets.MinAsync(p => p.orderFromSun);

        Assert.Equal(1, sum);
    }

    [Fact]
    public async Task MinAsync_without_selector()
    {
        var sum = await _db.Planets.Select(p => p.orderFromSun).MinAsync();

        Assert.Equal(1, sum);
    }

    public void Dispose()
        => _db.Dispose();

    public async ValueTask DisposeAsync()
        => await _db.DisposeAsync();
}
