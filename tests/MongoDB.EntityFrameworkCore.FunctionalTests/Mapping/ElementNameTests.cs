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
using MongoDB.Driver;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Mapping;

[XUnitCollection("MappingTests")]
public class ElementNameTests : IClassFixture<TemporaryDatabaseFixture>
{
    private readonly TemporaryDatabaseFixture _tempDatabase;

    public ElementNameTests(TemporaryDatabaseFixture tempDatabase)
    {
        _tempDatabase = tempDatabase;
    }

    class RenamedKeyElement
    {
        public ObjectId PrimaryKey { get; set; }
        public string Name { get; set; }
    }

    class StoredKeyElement
    {
        public ObjectId _id { get; set; }
        public string Name { get; set; }
    }

    class RenamedNonKeyElements
    {
        public ObjectId _id { get; set; }

        public string FirstName { get; set; }
        public string LastName { get; set; }
    }

    class StoredNonKeyElements
    {
        public ObjectId _id { get; set; }

        public string forename { get; set; }
        public string surname { get; set; }
    }

    [Fact]
    public void ElementName_on_primary_key_round_trips()
    {
        var collection = _tempDatabase.CreateTemporaryCollection<RenamedKeyElement>();

        var id = ObjectId.GenerateNewId();
        var expectedName = Guid.NewGuid().ToString();

        var modelBuilder = (ModelBuilder mb) =>
        {
            mb.Entity<RenamedKeyElement>().HasKey(e => e.PrimaryKey);
            mb.Entity<RenamedKeyElement>().Property(e => e.PrimaryKey).HasElementName("_id");
        };

        {
            var dbContext = SingleEntityDbContext.Create(collection, modelBuilder);
            dbContext.Entities.Add(new RenamedKeyElement {PrimaryKey = id, Name = expectedName});
            dbContext.SaveChanges();
        }

        {
            // Find with CSharpDriver
            var actual = collection.Database.GetCollection<StoredKeyElement>(collection.CollectionNamespace.CollectionName);
            var found = actual.Find(f => f._id == id).Single();
            Assert.Equal(expectedName, found.Name);
        }

        {
            // Find with EF
            var dbContext = SingleEntityDbContext.Create(collection, modelBuilder);
            var found = dbContext.Entities.Single(f => f.PrimaryKey == id);
            Assert.Equal(expectedName, found.Name);
        }
    }

    [Fact]
    public void ElementName_on_non_primary_key_round_trips()
    {
        var collection = _tempDatabase.CreateTemporaryCollection<RenamedNonKeyElements>();

        var id = ObjectId.GenerateNewId();
        var expectedFirstName = Guid.NewGuid().ToString();
        var expectedLastName = Guid.NewGuid().ToString();

        var modelBuilder = (ModelBuilder mb) =>
        {
            mb.Entity<RenamedNonKeyElements>().Property(e => e.FirstName).HasElementName("forename");
            mb.Entity<RenamedNonKeyElements>().Property(e => e.LastName).HasElementName("surname");
        };

        {
            var dbContext = SingleEntityDbContext.Create(collection, modelBuilder);
            dbContext.Entities.Add(
                new RenamedNonKeyElements {_id = id, FirstName = expectedFirstName, LastName = expectedLastName});
            dbContext.SaveChanges();
        }

        {
            // Find with CSharpDriver
            var actual = collection.Database.GetCollection<StoredNonKeyElements>(collection.CollectionNamespace.CollectionName);
            var found = actual.Find(f => f._id == id).Single();
            Assert.Equal(expectedFirstName, found.forename);
            Assert.Equal(expectedLastName, found.surname);
        }

        {
            // Find with EF
            var dbContext = SingleEntityDbContext.Create(collection, modelBuilder);
            var found = dbContext.Entities.Single(f => f._id == id);
            Assert.Equal(expectedFirstName, found.FirstName);
            Assert.Equal(expectedLastName, found.LastName);
        }
    }
}
