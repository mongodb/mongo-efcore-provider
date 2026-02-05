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
public class DictionaryTypeMappingTests(AtlasTemporaryDatabaseFixture database)
    : IClassFixture<AtlasTemporaryDatabaseFixture>
{
    class EntityWithDictionaryOfStrings
    {
        public ObjectId _id { get; set; }
        public Dictionary<string, string>? aDictionary { get; set; }
    }

    class EntityWithIDictionaryOfStrings
    {
        public ObjectId _id { get; set; }
        public IDictionary<string, int>? aDictionary { get; set; }
    }

    class EntityWithDictionaryOfListOfStrings
    {
        public ObjectId _id { get; set; }
        public Dictionary<string, List<string>>? aNestedDictionary { get; set; }
    }

    class EntityWithDictionaryOfDictionaryOfStrings
    {
        public ObjectId _id { get; set; }
        public Dictionary<string, Dictionary<string, string>>? aDictionaryOfLists { get; set; }
    }

    class Address
    {
        public string Street { get; set; }
        public string City { get; set; }
    }

    class EntityWithDictionaryOfClasses
    {
        public ObjectId _id { get; set; }
        public Dictionary<string, Address>? Addresses { get; set; }
    }

    public struct Point
    {
        public int X { get; set; }
        public int Y { get; set; }
    }

    class EntityWithDictionaryOfStructs
    {
        public ObjectId _id { get; set; }
        public Dictionary<string, Point>? Points { get; set; }
    }

    class EntityWithDictionaryOfDictionaryOfStructs
    {
        public ObjectId _id { get; set; }
        public Dictionary<string, Dictionary<string, Point>>? aDictionaryOfLists { get; set; }
    }

    [Fact]
    public void Dictionary_strings_write_read_with_items()
    {
        var collection = database.CreateCollection<EntityWithDictionaryOfStrings>();
        var expected = new Dictionary<string, string> { { "a", "1" }, { "b", "2" }, { "c", "3" } };

        {
            var item = new EntityWithDictionaryOfStrings { _id = ObjectId.GenerateNewId(), aDictionary = expected };
            using var db = SingleEntityDbContext.Create(collection);
            db.Entities.Add(item);
            db.SaveChanges();
            Assert.Equal(expected, item.aDictionary);
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var actual = db.Entities.FirstOrDefault();
            Assert.NotNull(actual);
            Assert.Equal(expected, actual.aDictionary);
        }
    }

    [Fact]
    public void Dictionary_strings_read_empty()
    {
        var collection = database.CreateCollection<EntityWithDictionaryOfStrings>();
        collection.InsertOne(new EntityWithDictionaryOfStrings
        {
            _id = ObjectId.GenerateNewId(), aDictionary = new Dictionary<string, string>()
        });

        using var db = SingleEntityDbContext.Create(collection);
        var actual = db.Entities.FirstOrDefault();

        Assert.NotNull(actual);
        Assert.NotNull(actual.aDictionary);
        Assert.Empty(actual.aDictionary);
    }

    [Fact]
    public void Dictionary_strings_read_null()
    {
        var collection = database.CreateCollection<EntityWithDictionaryOfStrings>();
        collection.InsertOne(new EntityWithDictionaryOfStrings { _id = ObjectId.GenerateNewId(), aDictionary = null });

        using var db = SingleEntityDbContext.Create(collection);
        var actual = db.Entities.FirstOrDefault();

        Assert.NotNull(actual);
        Assert.Null(actual.aDictionary);
    }

    [Fact]
    public void Dictionary_strings_update_items()
    {
        var collection = database.CreateCollection<EntityWithDictionaryOfStrings>();
        var id = ObjectId.GenerateNewId();
        var initial = new Dictionary<string, string> { { "key1", "value1" } };
        collection.InsertOne(new EntityWithDictionaryOfStrings { _id = id, aDictionary = initial });

        {
            using var db = SingleEntityDbContext.Create(collection);
            var entity = db.Entities.First(e => e._id == id);
            entity.aDictionary!["key1"] = "updated1";
            entity.aDictionary!["key2"] = "value2";
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var entity = db.Entities.First(e => e._id == id);
            Assert.Equal(2, entity.aDictionary!.Count);
            Assert.Equal("updated1", entity.aDictionary["key1"]);
            Assert.Equal("value2", entity.aDictionary["key2"]);
        }
    }

    [Fact]
    public void IDictionary_strings_write_read_with_items()
    {
        var collection = database.CreateCollection<EntityWithIDictionaryOfStrings>();
        var expected = new Dictionary<string, int> { { "a", 1 }, { "b", 2 }, { "c", 3 } };

        {
            var item = new EntityWithIDictionaryOfStrings { _id = ObjectId.GenerateNewId(), aDictionary = expected };
            using var db = SingleEntityDbContext.Create(collection);
            db.Entities.Add(item);
            db.SaveChanges();
            Assert.Equal(expected, item.aDictionary);
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var actual = db.Entities.FirstOrDefault();
            Assert.NotNull(actual);
            Assert.Equal(expected, actual.aDictionary);
        }
    }

#if !EF8 && !EF9 // Broken support for dictionary of lists prior to EF10
    [Fact]
    public void Dictionary_list_strings_write_read_with_items()
    {
        var collection = database.CreateCollection<EntityWithDictionaryOfListOfStrings>();
        var expected = new Dictionary<string, List<string>>
        {
            { "vowels", ["a", "e", "i", "o", "u"] }, { "consonants", ["b", "c", "d"] }
        };

        {
            var item = new EntityWithDictionaryOfListOfStrings { _id = ObjectId.GenerateNewId(), aNestedDictionary = expected };
            using var db = SingleEntityDbContext.Create(collection);
            db.Entities.Add(item);
            db.SaveChanges();
            Assert.Equal(expected, item.aNestedDictionary);
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var actual = db.Entities.FirstOrDefault();
            Assert.NotNull(actual);
            Assert.Equal(expected, actual.aNestedDictionary);
        }
    }
#endif

    [Fact]
    public void Dictionary_dictionary_strings_write_read_with_items()
    {
        var collection = database.CreateCollection<EntityWithDictionaryOfDictionaryOfStrings>();
        var expected = new Dictionary<string, Dictionary<string, string>>
        {
            { "english", new Dictionary<string, string> { { "one", "1" }, { "two", "2" } } },
            { "french", new Dictionary<string, string> { { "un", "1" }, { "deux", "2" } } }
        };

        {
            var item = new EntityWithDictionaryOfDictionaryOfStrings
            {
                _id = ObjectId.GenerateNewId(), aDictionaryOfLists = expected
            };
            using var db = SingleEntityDbContext.Create(collection);
            db.Entities.Add(item);
            db.SaveChanges();
            Assert.Equal(expected, item.aDictionaryOfLists);
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var actual = db.Entities.FirstOrDefault();
            Assert.NotNull(actual);
            Assert.Equal(expected, actual.aDictionaryOfLists);
        }
    }

    [Fact]
    public void Dictionary_dictionary_structs_write_read_with_items()
    {
        var collection = database.CreateCollection<EntityWithDictionaryOfDictionaryOfStructs>();
        var expected = new Dictionary<string, Dictionary<string, Point>>
        {
            { "set1", new Dictionary<string, Point> { { "p1", new Point { X = 1, Y = 1 } }, { "p2", new Point { X = 2, Y = 2 } } } },
            { "set2", new Dictionary<string, Point> { { "p3", new Point { X = 3, Y = 3 } } } }
        };

        {
            var item = new EntityWithDictionaryOfDictionaryOfStructs
            {
                _id = ObjectId.GenerateNewId(), aDictionaryOfLists = expected
            };
            using var db = SingleEntityDbContext.Create(collection);
            db.Entities.Add(item);
            db.SaveChanges();
            Assert.Equal(expected, item.aDictionaryOfLists);
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var actual = db.Entities.FirstOrDefault();
            Assert.NotNull(actual);
            Assert.Equal(expected, actual.aDictionaryOfLists);
        }
    }

    [Fact]
    public void Dictionary_struct_add_delete_update()
    {
        var collection = database.CreateCollection<EntityWithDictionaryOfStructs>();
        var id = ObjectId.GenerateNewId();
        var initial = new Dictionary<string, Point> { { "A", new Point { X = 1, Y = 1 } }, { "B", new Point { X = 2, Y = 2 } } };
        collection.InsertOne(new EntityWithDictionaryOfStructs { _id = id, Points = initial });

        {
            using var db = SingleEntityDbContext.Create(collection);
            var entity = db.Entities.First(e => e._id == id);
            entity.Points!["A"] = new Point { X = 10, Y = 10 }; // Update
            entity.Points!.Remove("B"); // Delete
            entity.Points!["C"] = new Point { X = 3, Y = 3 }; // Add
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var actual = db.Entities.First(e => e._id == id);
            Assert.Equal(2, actual.Points!.Count);
            Assert.Equal(10, actual.Points["A"].X);
            Assert.False(actual.Points.ContainsKey("B"));
            Assert.Equal(3, actual.Points["C"].X);
        }
    }

    [Fact]
    public void Dictionary_struct_update_only()
    {
        var collection = database.CreateCollection<EntityWithDictionaryOfStructs>();
        var id = ObjectId.GenerateNewId();
        var initial = new Dictionary<string, Point> { { "A", new Point { X = 1, Y = 1 } } };
        collection.InsertOne(new EntityWithDictionaryOfStructs { _id = id, Points = initial });

        {
            using var db = SingleEntityDbContext.Create(collection);
            var entity = db.Entities.First(e => e._id == id);
            entity.Points!["A"] = new Point { X = 20, Y = 20 };
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var actual = db.Entities.First(e => e._id == id);
            Assert.Equal(20, actual.Points!["A"].X);
        }
    }

    [Fact]
    public void Dictionary_entity_currently_throws_model_validation_error()
    {
        var collection = database.CreateCollection<EntityWithDictionaryOfClasses>();

        using var db = SingleEntityDbContext.Create(collection);
        var ex = Assert.Throws<InvalidOperationException>(() => db.Entities.FirstOrDefault());
        Assert.Contains("Unable to determine the relationship represented by navigation 'EntityWithDictionaryOfClasses.Addresses'",
            ex.Message);
    }
}
