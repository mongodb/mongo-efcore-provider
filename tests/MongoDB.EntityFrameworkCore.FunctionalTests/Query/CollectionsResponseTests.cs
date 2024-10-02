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
public class CollectionsResponseTests(ReadOnlySampleGuidesFixture database)
    : IDisposable, IAsyncDisposable
{
    private readonly GuidesDbContext _db = GuidesDbContext.Create(database.MongoDatabase);

    [Fact]
    public void ToList()
    {
        var result = _db.Planets.ToList();
        Assert.Equal(8, result.Count);
    }

    [Fact]
    public async Task ToListAsync()
    {
        var result = await _db.Planets.ToListAsync();
        Assert.Equal(8, result.Count);
    }

    [Fact]
    public void ToArray()
    {
        var result = _db.Planets.ToArray();
        Assert.Equal(8, result.Length);
    }

    [Fact]
    public async Task ToArrayAsync()
    {
        var result = await _db.Planets.ToArrayAsync();
        Assert.Equal(8, result.Length);
    }

    public void Dispose()
        => _db.Dispose();

    public async ValueTask DisposeAsync()
        => await _db.DisposeAsync();
}
