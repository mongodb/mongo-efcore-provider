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

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Mapping;

[XUnitCollection("MappingTests")]
public class DictionaryChangeTrackingTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    class EntityWithStringDictionary
    {
        public ObjectId _id { get; set; }
        public Dictionary<string, string> tags { get; set; } = new();
    }

    class EntityWithIntDictionary
    {
        public ObjectId _id { get; set; }
        public Dictionary<string, int> scores { get; set; } = new();
    }

    [Fact]
    public void String_dictionary_change_detection_and_update()
    {
        var collection = database.CreateCollection<EntityWithStringDictionary>();

        using (var db = SingleEntityDbContext.Create(collection))
        {
            db.Entities.Add(new EntityWithStringDictionary
            {
                tags = new Dictionary<string, string> { ["color"] = "red", ["size"] = "large" }
            });
            db.SaveChanges();
        }

        using (var db = SingleEntityDbContext.Create(collection))
        {
            var entity = db.Entities.First();
            entity.tags["color"] = "blue";
            entity.tags.Add("shape", "circle");
            db.SaveChanges();
        }

        using (var db = SingleEntityDbContext.Create(collection))
        {
            var entity = db.Entities.First();
            Assert.Equal("blue", entity.tags["color"]);
            Assert.Equal("large", entity.tags["size"]);
            Assert.Equal("circle", entity.tags["shape"]);
        }
    }

    [Fact]
    public void Int_dictionary_change_detection_and_update()
    {
        var collection = database.CreateCollection<EntityWithIntDictionary>();

        using (var db = SingleEntityDbContext.Create(collection))
        {
            db.Entities.Add(new EntityWithIntDictionary
            {
                scores = new Dictionary<string, int> { ["math"] = 90, ["science"] = 85 }
            });
            db.SaveChanges();
        }

        using (var db = SingleEntityDbContext.Create(collection))
        {
            var entity = db.Entities.First();
            entity.scores["math"] = 95;
            db.SaveChanges();
        }

        using (var db = SingleEntityDbContext.Create(collection))
        {
            var entity = db.Entities.First();
            Assert.Equal(95, entity.scores["math"]);
            Assert.Equal(85, entity.scores["science"]);
        }
    }

    [Fact]
    public void Dictionary_with_no_changes_does_not_trigger_update()
    {
        var collection = database.CreateCollection<EntityWithStringDictionary>();

        using (var db = SingleEntityDbContext.Create(collection))
        {
            db.Entities.Add(new EntityWithStringDictionary
            {
                tags = new Dictionary<string, string> { ["key"] = "value" }
            });
            db.SaveChanges();
        }

        using (var db = SingleEntityDbContext.Create(collection))
        {
            var entity = db.Entities.First();
            // Read but don't modify
            _ = entity.tags["key"];
            var changes = db.ChangeTracker.HasChanges();
            Assert.False(changes);
        }
    }
}
