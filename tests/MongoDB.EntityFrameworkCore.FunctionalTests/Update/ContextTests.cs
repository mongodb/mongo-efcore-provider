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
using MongoDB.Bson;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Update;

[XUnitCollection("UpdateTests")]
public class ContextTests(TemporaryDatabaseFixture tempDatabase) : IClassFixture<TemporaryDatabaseFixture>
{
    [Fact]
    public void SaveChanges_includes_insertion_counts()
    {
        using var db = SingleEntityDbContext.Create(tempDatabase.CreateCollection<Customer>());

        const int insertCount = 10;

        db.Entities.AddRange(
            Enumerable.Range(0, insertCount).Select(i => new Customer("Generated " + i)));

        Assert.Equal(insertCount, db.SaveChanges());
        Assert.Equal(insertCount, db.Entities.Count());
    }

    [Fact]
    public async Task SaveChangesAsync_includes_insertion_counts()
    {
        await using var db = SingleEntityDbContext.Create(tempDatabase.CreateCollection<Customer>());

        const int insertCount = 9;

        db.Entities.AddRange(
            Enumerable.Range(0, insertCount).Select(i => new Customer("Generated " + i)));

        Assert.Equal(insertCount, await db.SaveChangesAsync());
        Assert.Equal(insertCount, await db.Entities.CountAsync());
    }

    [Fact]
    public void SaveChanges_includes_update_counts()
    {
        using var db = SingleEntityDbContext.Create(tempDatabase.CreateCollection<Customer>());

        const int updateCount = 8;
        var items = Enumerable.Range(0, updateCount * 2)
            .Select(i => new Customer("Generated " + i))
            .ToArray();

        db.Entities.AddRange(items);
        db.SaveChanges();

        for (var i = 0; i < updateCount; i++)
            items[i].name = "Updated " + i;

        Assert.Equal(updateCount, db.SaveChanges());
        Assert.Equal(updateCount, db.Entities.Count(c => c.name.StartsWith("Updated ")));
    }

    [Fact]
    public async Task SaveChangesAsync_includes_update_counts()
    {
        await using var db = SingleEntityDbContext.Create(tempDatabase.CreateCollection<Customer>());

        const int updateCount = 7;
        var items = Enumerable.Range(0, updateCount * 2)
            .Select(i => new Customer("Generated " + i))
            .ToArray();

        db.Entities.AddRange(items);
        await db.SaveChangesAsync();

        for (var i = 0; i < updateCount; i++)
            items[i].name = "Updated " + i;

        Assert.Equal(updateCount, await db.SaveChangesAsync());
        Assert.Equal(updateCount, await db.Entities.CountAsync(c => c.name.StartsWith("Updated ")));
    }

    [Fact]
    public void SaveChanges_includes_delete_counts()
    {
        using var db = SingleEntityDbContext.Create(tempDatabase.CreateCollection<Customer>());

        const int deleteCount = 6;
        var items = Enumerable.Range(0, deleteCount + 10)
            .Select(i => new Customer("Generated " + i))
            .ToArray();

        db.Entities.AddRange(items);
        db.SaveChanges();

        db.Entities.RemoveRange(items.Take(deleteCount));

        Assert.Equal(deleteCount, db.SaveChanges());
        Assert.Equal(items.Length - deleteCount, db.Entities.Count());
    }

    [Fact]
    public async Task SaveChangesAsync_includes_delete_counts()
    {
        await using var db = SingleEntityDbContext.Create(tempDatabase.CreateCollection<Customer>());

        const int deleteCount = 6;
        var items = Enumerable.Range(0, deleteCount * 2)
            .Select(i => new Customer("Generated " + i))
            .ToArray();

        await db.Entities.AddRangeAsync(items);
        await db.SaveChangesAsync();

        db.Entities.RemoveRange(items.Take(deleteCount));

        Assert.Equal(deleteCount, await db.SaveChangesAsync());
        Assert.Equal(deleteCount, await db.Entities.CountAsync());
    }

    [Fact]
    public void SaveChanges_combines_counts()
    {
        using var db = SingleEntityDbContext.Create(tempDatabase.CreateCollection<Customer>());

        var items = Enumerable.Range(0, 4)
            .Select(i => new Customer("Generated " + i))
            .ToArray();

        db.Entities.AddRange(items);
        db.SaveChanges();

        db.Entities.Remove(items[0]);
        items[2].name = "Updated 2";
        items[3].name = "Updated 3";
        db.Entities.Add(new Customer("Generated x"));

        Assert.Equal(4, db.SaveChanges());
        Assert.Equal(4, db.Entities.Count());
    }

    [Fact]
    public async Task SaveChangesAsync_combines_counts()
    {
        await using var db = SingleEntityDbContext.Create(tempDatabase.CreateCollection<Customer>());

        var items = Enumerable.Range(0, 4)
            .Select(i => new Customer("Generated " + i))
            .ToArray();

        await db.Entities.AddRangeAsync(items);
        await db.SaveChangesAsync();

        db.Entities.Remove(items[0]);
        items[2].name = "Updated 2";
        items[3].name = "Updated 3";
        await db.Entities.AddAsync(new Customer("Generated x"));

        Assert.Equal(4, await db.SaveChangesAsync());
        Assert.Equal(4, await db.Entities.CountAsync());
    }

    [Fact]
    public void SaveChanges_counts_only_documents_not_owned_entities()
    {
        using var db = SingleEntityDbContext.Create(tempDatabase.CreateCollection<PeopleOnMoons>());

        var item = new PeopleOnMoons("Space Adventurer");
        db.Entities.Add(item);
        db.SaveChanges();

        item.Moons.Add(new Moon("Titan"));
        item.Moons.Add(new Moon("Mimas"));

        Assert.Equal(1, db.SaveChanges());
        Assert.Equal(1, db.Entities.Count());
    }

    [Fact]
    public async Task SaveChangesAsync_counts_only_documents_not_owned_entities()
    {
        await using var db = SingleEntityDbContext.Create(tempDatabase.CreateCollection<PeopleOnMoons>());

        var item1 = new PeopleOnMoons("Captain A");
        var item2 = new PeopleOnMoons("Captain B");
        db.Entities.Add(item1);
        db.Entities.Add(item2);
        await db.SaveChangesAsync();

        item1.Moons.Add(new Moon("Io"));
        item1.Moons.Add(new Moon("Phoebe"));
        item2.Moons.Add(new Moon("Ganymede"));
        item1.name = "Captain J";

        Assert.Equal(2, await db.SaveChangesAsync());
        Assert.Equal(2, await db.Entities.CountAsync());
    }

    class Customer(string name)
    {
        public ObjectId _id { get; set; } = ObjectId.GenerateNewId();
        public string name { get; set; } = name;
    }

    class PeopleOnMoons(string name) : Customer(name)
    {
        public List<Moon> Moons { get; set; } = [];
    }

    class Moon(string name)
    {
        public string name { get; set; } = name;
    }
}
