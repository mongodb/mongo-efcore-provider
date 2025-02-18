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

[XUnitCollection(nameof(ReadOnlySampleGuidesFixture))]
public sealed class TopSetTests(ReadOnlySampleGuidesFixture database)
    : IDisposable, IAsyncDisposable
{
    private readonly GuidesDbContext _db = GuidesDbContext.Create(database.MongoDatabase);

    [Fact]
    public void Cast_to_parent()
    {
        var all = _db.Planets.Cast<object>().ToList();

        Assert.Equal(8, all.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Concat(bool overlap)
    {
        var a = _db.Planets.Take(3);
        var b = _db.Planets.Skip(overlap ? 2 : 3).Take(2);

        var union = a.Concat(b).ToList();

        Assert.Equal(5, union.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Distinct(bool overlap)
    {
        var a = _db.Planets.Take(3);
        var b = _db.Planets.Skip(overlap ? 2 : 3).Take(2);

        var union = a.Concat(b).Distinct().ToList();

        Assert.Equal(overlap ? 4 : 5, union.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Union(bool overlap)
    {
        var source1 = _db.Planets.Take(3);
        var source2 = _db.Planets.Skip(overlap ? 2 : 3).Take(2);

        var union = source1.Union(source2).ToList();

        Assert.Equal(overlap ? 4 : 5, union.Count);
    }

    public void Dispose()
        => _db.Dispose();

    public async ValueTask DisposeAsync()
        => await _db.DisposeAsync();
}
