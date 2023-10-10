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
public class FindTests
{
    private readonly IMongoDatabase _mongoDatabase;

    public FindTests(SampleGuidesFixture fixture)
    {
        _mongoDatabase = fixture.MongoDatabase;
    }

    [Fact]
    public void Find_with_primitive_key_found()
    {
        using var db = GuidesDbContext.Create(_mongoDatabase);
        var result = db.Planets.Find(ObjectId.Parse("621ff30d2a3e781873fcb661"));
        Assert.NotNull(result);
        Assert.Equal("Earth", result.name);
    }

    [Fact]
    public void Find_with_primitive_key_not_found()
    {
        using var db = GuidesDbContext.Create(_mongoDatabase);
        var result = db.Planets.Find(ObjectId.Parse("a21ff30d2a3e781873fcb661"));
        Assert.Null(result);
    }

    [Fact]
    public async Task FindAsync_with_primitive_key_found()
    {
        await using var db = GuidesDbContext.Create(_mongoDatabase);
        var result = await db.Planets.FindAsync(ObjectId.Parse("621ff30d2a3e781873fcb661"));
        Assert.NotNull(result);
        Assert.Equal("Earth", result.name);
    }

    [Fact]
    public async Task FindAsync_with_primitive_key_not_found()
    {
        await using var db = GuidesDbContext.Create(_mongoDatabase);
        var result = await db.Planets.FindAsync(ObjectId.Parse("a21ff30d2a3e781873fcb661"));
        Assert.Null(result);
    }

    [Fact]
    public void Find_with_compound_key_found()
    {
        using var db = GuidesDbContext.Create(_mongoDatabase);
        var result = db.Moons.Find(ObjectId.Parse("621ff30d2a3e781873fcb663"), "VI");
        Assert.NotNull(result);
        Assert.Equal("Titan", result.name);
    }

    [Fact]
    public void Find_with_compound_key_not_found()
    {
        using var db = GuidesDbContext.Create(_mongoDatabase);
        var result = db.Moons.Find(ObjectId.Parse("a21ff30d2a3e781873fcb663"), "VI");
        Assert.Null(result);
    }

    [Fact]
    public async Task FindAsync_with_compound_key_found()
    {
        await using var db = GuidesDbContext.Create(_mongoDatabase);
        var result = await db.Moons.FindAsync(ObjectId.Parse("621ff30d2a3e781873fcb663"), "VI");
        Assert.NotNull(result);
        Assert.Equal("Titan", result.name);
    }

    [Fact]
    public async Task FindAsync_with_compound_key_not_found()
    {
        await using var db = GuidesDbContext.Create(_mongoDatabase);
        var result = await db.Moons.FindAsync(ObjectId.Parse("a21ff30d2a3e781873fcb663"), "VI");
        Assert.Null(result);
    }

    [Fact]
    public void Find_equivalent_in_LINQ_works_v3()
    {
        using var db = GuidesDbContext.Create(_mongoDatabase);
        // Just ensures underling LINQ provider accepting translation
        var result = db.Planets.First(p => Equals(p._id, ObjectId.Parse("621ff30d2a3e781873fcb661")));
        Assert.NotNull(result);
        Assert.Equal("Earth", result.name);
    }
}
