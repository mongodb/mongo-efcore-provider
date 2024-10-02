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

[XUnitCollection(nameof(ReadOnlySampleGuidesFixture))]
public class FindTests(ReadOnlySampleGuidesFixture database)
    : IDisposable, IAsyncDisposable
{
    private readonly GuidesDbContext _db = GuidesDbContext.Create(database.MongoDatabase);

    [Fact]
    public void Find_with_primitive_key_found()
    {
        var result = _db.Planets.Find(ObjectId.Parse("621ff30d2a3e781873fcb661"));
        Assert.NotNull(result);
        Assert.Equal("Earth", result.name);
    }

    [Fact]
    public void Find_with_primitive_key_not_found()
    {
        var result = _db.Planets.Find(ObjectId.Parse("a21ff30d2a3e781873fcb661"));
        Assert.Null(result);
    }

    [Fact]
    public async Task FindAsync_with_primitive_key_found()
    {
        var result = await _db.Planets.FindAsync(ObjectId.Parse("621ff30d2a3e781873fcb661"));
        Assert.NotNull(result);
        Assert.Equal("Earth", result.name);
    }

    [Fact]
    public async Task FindAsync_with_primitive_nullable_constant_key_found()
    {
        ObjectId? key = ObjectId.Parse("621ff30d2a3e781873fcb661");
        var result = await _db.Planets.FindAsync(key);
        Assert.NotNull(result);
        Assert.Equal("Earth", result.name);
    }

    [Fact]
    public async Task FindAsync_with_primitive_key_not_found()
    {
        var result = await _db.Planets.FindAsync(ObjectId.Parse("a21ff30d2a3e781873fcb661"));
        Assert.Null(result);
    }

    [Fact]
    public void Find_with_compound_key_found()
    {
        var result = _db.Moons.Find(ObjectId.Parse("621ff30d2a3e781873fcb663"), "VI");
        Assert.NotNull(result);
        Assert.Equal("Titan", result.name);
    }

    [Fact]
    public void Find_with_compound_key_not_found()
    {
        var result = _db.Moons.Find(ObjectId.Parse("a21ff30d2a3e781873fcb663"), "VI");
        Assert.Null(result);
    }

    [Fact]
    public async Task FindAsync_with_compound_key_found()
    {
        var result = await _db.Moons.FindAsync(ObjectId.Parse("621ff30d2a3e781873fcb663"), "VI");
        Assert.NotNull(result);
        Assert.Equal("Titan", result.name);
    }

    [Fact]
    public async Task FindAsync_with_compound_key_not_found()
    {
        var result = await _db.Moons.FindAsync(ObjectId.Parse("a21ff30d2a3e781873fcb663"), "VI");
        Assert.Null(result);
    }

    [Fact]
    public void Find_equivalent_in_LINQ_works_v3()
    {
        // Just ensures underling LINQ provider accepting translation
        var result = _db.Planets.First(p => Equals(p._id, ObjectId.Parse("621ff30d2a3e781873fcb661")));
        Assert.NotNull(result);
        Assert.Equal("Earth", result.name);
    }

    [Fact]
    public void Find_equivalent_in_LINQ_v3_works_when_constant_nullable()
    {
        ObjectId? key = ObjectId.Parse("621ff30d2a3e781873fcb661");
        var result = _db.Planets.First(p => Equals(p._id, key));
        Assert.NotNull(result);
        Assert.Equal("Earth", result.name);
    }

    public void Dispose()
        => _db.Dispose();

    public async ValueTask DisposeAsync()
        => await _db.DisposeAsync();
}
