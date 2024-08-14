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

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

[XUnitCollection("DictionaryTests")]
public class WhereDictionaryTests(TemporaryDatabaseFixture tempDatabase)
    : IClassFixture<TemporaryDatabaseFixture>
{
    class DictionaryEntity
    {
        public ObjectId _id { get; set; }
        public Dictionary<string, string> Dictionary { get; set; }
    }

    class DictionaryIntEntity
    {
        public ObjectId _id { get; set; }
        public Dictionary<string, int> Dictionary { get; set; }
    }

    class IDictionaryIntEntity
    {
        public ObjectId _id { get; set; }
        public IDictionary<string, int> Dictionary { get; set; }
    }

    class IReadOnlyDictionaryIntEntity
    {
        public ObjectId _id { get; set; }
        public IReadOnlyDictionary<string, int> Dictionary { get; set; }
    }

    [Fact]
    public void Where_Dictionary_contains_key()
    {
        var collection = tempDatabase.CreateTemporaryCollection<DictionaryEntity>();

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entities.AddRange(
                new DictionaryEntity
                {
                    _id = ObjectId.GenerateNewId(),
                    Dictionary = new Dictionary<string, string> {{"key1", "value1"}, {"key2", null!}}
                },
                new DictionaryEntity
                {
                    _id = ObjectId.GenerateNewId(), Dictionary = new Dictionary<string, string> {{"key2", "value2"}}
                });
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var results = db.Entities.Where(e => e.Dictionary.ContainsKey("key1")).ToArray();
            var found = Assert.Single(results);
            Assert.Equal(2, found.Dictionary.Count);
        }
    }

    [Fact]
    public void Where_Dictionary_key_equals_value()
    {
        var collection = tempDatabase.CreateTemporaryCollection<DictionaryEntity>();

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entities.AddRange(
                new DictionaryEntity
                {
                    _id = ObjectId.GenerateNewId(),
                    Dictionary = new Dictionary<string, string> {{"key1", "value1"}, {"key2", null!}}
                },
                new DictionaryEntity
                {
                    _id = ObjectId.GenerateNewId(), Dictionary = new Dictionary<string, string> {{"key2", "value2"}}
                });
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var results = db.Entities.Where(e => e.Dictionary["key2"] == "value2").ToArray();
            var found = Assert.Single(results);
            var entry = Assert.Single(found.Dictionary);
            Assert.Equal("key2", entry.Key);
            Assert.Equal("value2", entry.Value);
        }
    }

    [Fact]
    public void Where_Dictionary_key_equals_null()
    {
        var collection = tempDatabase.CreateTemporaryCollection<DictionaryEntity>();

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entities.AddRange(
                new DictionaryEntity
                {
                    _id = ObjectId.GenerateNewId(),
                    Dictionary = new Dictionary<string, string> {{"key1", "value1"}, {"key2", null!}}
                },
                new DictionaryEntity
                {
                    _id = ObjectId.GenerateNewId(), Dictionary = new Dictionary<string, string> {{"key2", "value2"}}
                });
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var results = db.Entities.Where(e => e.Dictionary["key2"] == null!).ToArray();
            var found = Assert.Single(results);
            Assert.Equal("value1", Assert.Contains("key1", found.Dictionary));
        }
    }

    [Fact]
    public void Where_Dictionary_key_not_equals_null()
    {
        var collection = tempDatabase.CreateTemporaryCollection<DictionaryEntity>();

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entities.AddRange(
                new DictionaryEntity
                {
                    _id = ObjectId.GenerateNewId(),
                    Dictionary = new Dictionary<string, string> {{"key1", "value1"}, {"key2", null!}}
                },
                new DictionaryEntity
                {
                    _id = ObjectId.GenerateNewId(), Dictionary = new Dictionary<string, string> {{"key1", null!}}
                });
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection);

            var results = db.Entities.Where(e => e.Dictionary["key1"] != null!).ToArray();
            var found = Assert.Single(results);
            Assert.Equal("value1", Assert.Contains("key1", found.Dictionary));
        }
    }

    [Fact]
    public void Where_Dictionary_value_in_range()
    {
        var collection = tempDatabase.CreateTemporaryCollection<DictionaryIntEntity>();

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entities.AddRange(
                new DictionaryIntEntity
                {
                    _id = ObjectId.GenerateNewId(), Dictionary = new Dictionary<string, int> {{"key1", 10}, {"key2", 50}}
                },
                new DictionaryIntEntity
                {
                    _id = ObjectId.GenerateNewId(), Dictionary = new Dictionary<string, int> {{"key1", 72}, {"key2", 100}}
                },
                new DictionaryIntEntity
                {
                    _id = ObjectId.GenerateNewId(), Dictionary = new Dictionary<string, int> {{"key1", 100}, {"key2", 500}}
                });
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var results = db.Entities.Where(e => e.Dictionary["key1"] >= 72 && e.Dictionary["key2"] < 101).ToArray();
            var found = Assert.Single(results);
            Assert.Equal(100, Assert.Contains("key2", found.Dictionary));
        }
    }

    [Fact]
    public void Where_IDictionary_contains_key()
    {
        var collection = tempDatabase.CreateTemporaryCollection<IDictionaryIntEntity>();

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entities.AddRange(
                new IDictionaryIntEntity {_id = ObjectId.GenerateNewId(), Dictionary = new Dictionary<string, int> {{"key1", 1}}},
                new IDictionaryIntEntity {_id = ObjectId.GenerateNewId(), Dictionary = new Dictionary<string, int> {{"key2", 2}}});
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var results = db.Entities.Where(e => e.Dictionary.ContainsKey("key1")).ToArray();
            Assert.Single(results);
        }
    }

    [Fact]
    public void Where_IDictionary_key_equals_value()
    {
        var collection = tempDatabase.CreateTemporaryCollection<IDictionaryIntEntity>();

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entities.AddRange(
                new IDictionaryIntEntity {_id = ObjectId.GenerateNewId(), Dictionary = new Dictionary<string, int> {{"key1", 1}}},
                new IDictionaryIntEntity {_id = ObjectId.GenerateNewId(), Dictionary = new Dictionary<string, int> {{"key2", 2}}});
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var results = db.Entities.Where(e => e.Dictionary["key2"] == 2).ToArray();
            var found = Assert.Single(results);
            var entry = Assert.Single(found.Dictionary);
            Assert.Equal("key2", entry.Key);
            Assert.Equal(2, entry.Value);
        }
    }

    [Fact]
    public void Where_IDictionary_value_in_range()
    {
        var collection = tempDatabase.CreateTemporaryCollection<IDictionaryIntEntity>();

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entities.AddRange(
                new IDictionaryIntEntity
                {
                    _id = ObjectId.GenerateNewId(), Dictionary = new Dictionary<string, int> {{"key1", 10}, {"key2", 50}}
                },
                new IDictionaryIntEntity
                {
                    _id = ObjectId.GenerateNewId(), Dictionary = new Dictionary<string, int> {{"key1", 72}, {"key2", 100}}
                },
                new IDictionaryIntEntity
                {
                    _id = ObjectId.GenerateNewId(), Dictionary = new Dictionary<string, int> {{"key1", 100}, {"key2", 500}}
                });
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var results = db.Entities.Where(e => e.Dictionary["key1"] >= 72 && e.Dictionary["key2"] < 101).ToArray();
            var found = Assert.Single(results);
            Assert.Equal(100, Assert.Contains("key2", found.Dictionary));
        }
    }

    [Fact]
    public void Where_ReadOnlyDictionary_contains_key()
    {
        var collection = tempDatabase.CreateTemporaryCollection<IReadOnlyDictionaryIntEntity>();

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entities.AddRange(
                new IReadOnlyDictionaryIntEntity
                {
                    _id = ObjectId.GenerateNewId(), Dictionary = new Dictionary<string, int> {{"key1", 1}}.AsReadOnly()
                },
                new IReadOnlyDictionaryIntEntity
                {
                    _id = ObjectId.GenerateNewId(), Dictionary = new Dictionary<string, int> {{"key2", 2}}.AsReadOnly()
                });
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var results = db.Entities.Where(e => e.Dictionary.ContainsKey("key1")).ToArray();
            Assert.Single(results);
        }
    }

    [Fact]
    public void Where_ReadOnlyDictionary_key_equals_value()
    {
        var collection = tempDatabase.CreateTemporaryCollection<IReadOnlyDictionaryIntEntity>();

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entities.AddRange(
                new IReadOnlyDictionaryIntEntity
                {
                    _id = ObjectId.GenerateNewId(), Dictionary = new Dictionary<string, int> {{"key1", 1}}.AsReadOnly()
                },
                new IReadOnlyDictionaryIntEntity
                {
                    _id = ObjectId.GenerateNewId(), Dictionary = new Dictionary<string, int> {{"key2", 2}}.AsReadOnly()
                });
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var results = db.Entities.Where(e => e.Dictionary["key2"] == 2).ToArray();
            var found = Assert.Single(results);
            var entry = Assert.Single(found.Dictionary);
            Assert.Equal("key2", entry.Key);
            Assert.Equal(2, entry.Value);
        }
    }

    [Fact]
    public void Where_ReadOnlyDictionary_value_in_range()
    {
        var collection = tempDatabase.CreateTemporaryCollection<IReadOnlyDictionaryIntEntity>();

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entities.AddRange(
                new IReadOnlyDictionaryIntEntity
                {
                    _id = ObjectId.GenerateNewId(), Dictionary = new Dictionary<string, int>().AsReadOnly()
                },
                new IReadOnlyDictionaryIntEntity
                {
                    _id = ObjectId.GenerateNewId(),
                    Dictionary = new Dictionary<string, int> {{"key1", 10}, {"key2", 50}}.AsReadOnly()
                },
                new IReadOnlyDictionaryIntEntity
                {
                    _id = ObjectId.GenerateNewId(),
                    Dictionary = new Dictionary<string, int> {{"key1", 100}, {"key2", 500}}.AsReadOnly()
                });
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var results = db.Entities.ToList();
            Assert.Equal(3, results.Count);
        }
    }
}
