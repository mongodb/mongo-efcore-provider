using Microsoft.EntityFrameworkCore;
using MongoDB.EntityFrameworkCore.FunctionalTests.Entities.Guides;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

[XUnitCollection(nameof(SampleGuidesFixture))]
public class CollectionsResponseTests
{
    private readonly GuidesDbContext _db;

    public CollectionsResponseTests(SampleGuidesFixture fixture)
    {
        _db = GuidesDbContext.Create(fixture.MongoDatabase);
    }

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
}
