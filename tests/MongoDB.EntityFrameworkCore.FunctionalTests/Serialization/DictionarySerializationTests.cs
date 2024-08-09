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

using System.Collections.ObjectModel;
using MongoDB.Bson;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Serialization;

public class DictionarySerializationTests(TemporaryDatabaseFixture tempDatabase)
    : IClassFixture<TemporaryDatabaseFixture>
{
    class DictionaryEntityBase<T>
    {
        public ObjectId _id { get; set; }
        public Dictionary<string, T>? aDictionary { get; set; } = new();
    }

    class IDictionaryEntityBase<T>
    {
        public ObjectId _id { get; set; }
        public IDictionary<string, T>? aDictionary { get; set; } = new Dictionary<string, T>();
    }

    class IReadOnlyDictionaryEntityBase<T>
    {
        public ObjectId _id { get; set; }
        public IReadOnlyDictionary<string, T>? aDictionary { get; set; } = ReadOnlyDictionary<string, T>.Empty;
    }

    class DictionaryStringValuesEntity : DictionaryEntityBase<string>;

    class IDictionaryStringValuesEntity : IDictionaryEntityBase<string>;

    class IReadOnlyDictionaryStringValuesEntity : IReadOnlyDictionaryEntityBase<string>;

    class DictionaryIntValuesEntity : DictionaryEntityBase<int>;

    class IDictionaryIntValuesEntity : IDictionaryEntityBase<int>;

    class DictionaryStringArrayValuesEntity : DictionaryEntityBase<string[]>;

    [Fact]
    public void Dictionary_string_values_read_empty()
    {
        var collection = tempDatabase.CreateTemporaryCollection<DictionaryStringValuesEntity>();
        collection.InsertOne(new DictionaryStringValuesEntity {_id = ObjectId.GenerateNewId()});

        using var db = SingleEntityDbContext.Create(collection);
        var actual = db.Entities.FirstOrDefault();

        Assert.NotNull(actual);
        Assert.NotNull(actual.aDictionary);
        Assert.Empty(actual.aDictionary);
    }

    [Fact]
    public void Dictionary_string_values_read_null()
    {
        var collection = tempDatabase.CreateTemporaryCollection<DictionaryStringValuesEntity>();
        collection.InsertOne(new DictionaryStringValuesEntity {_id = ObjectId.GenerateNewId(), aDictionary = null});

        using var db = SingleEntityDbContext.Create(collection);
        var actual = db.Entities.FirstOrDefault();

        Assert.NotNull(actual);
        Assert.Null(actual.aDictionary);
    }

    [Fact]
    public void Dictionary_string_values_write_read_with_items()
    {
        var collection = tempDatabase.CreateTemporaryCollection<DictionaryStringValuesEntity>();
        var expected = new Dictionary<string, string>
        {
            {"Season", "Summer"}, {"Temperature", "35'"}, {"Clouds", "None"}, {"Wind", "Breeze"}
        };
        var item = new DictionaryStringValuesEntity {_id = ObjectId.GenerateNewId(), aDictionary = expected};

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entities.Add(item);
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var actual = db.Entities.FirstOrDefault();
            Assert.NotNull(actual);
            Assert.Equal(expected, actual.aDictionary);
        }
    }

    [Fact]
    public void Dictionary_int_values_read_empty()
    {
        var collection = tempDatabase.CreateTemporaryCollection<DictionaryIntValuesEntity>();
        collection.InsertOne(new DictionaryIntValuesEntity {_id = ObjectId.GenerateNewId()});

        using var db = SingleEntityDbContext.Create(collection);
        var actual = db.Entities.FirstOrDefault();

        Assert.NotNull(actual);
        Assert.NotNull(actual.aDictionary);
        Assert.Empty(actual.aDictionary);
    }

    [Fact]
    public void Dictionary_int_values_write_read_with_items()
    {
        var collection = tempDatabase.CreateTemporaryCollection<DictionaryIntValuesEntity>();
        var expected = new Dictionary<string, int> {{"Season", 2}, {"Temperature", 35}, {"Clouds", 0}, {"Wind", 11}};
        var item = new DictionaryIntValuesEntity {_id = ObjectId.GenerateNewId(), aDictionary = expected};

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entities.Add(item);
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var actual = db.Entities.FirstOrDefault();
            Assert.NotNull(actual);
            Assert.Equal(expected, actual.aDictionary);
        }
    }

    [Fact]
    public void IDictionary_string_values_read_empty()
    {
        var collection = tempDatabase.CreateTemporaryCollection<IDictionaryStringValuesEntity>();
        collection.InsertOne(new IDictionaryStringValuesEntity {_id = ObjectId.GenerateNewId()});

        using var db = SingleEntityDbContext.Create(collection);
        var actual = db.Entities.FirstOrDefault();

        Assert.NotNull(actual);
        Assert.NotNull(actual.aDictionary);
        Assert.Empty(actual.aDictionary);
    }

    [Fact]
    public void IDictionary_string_values_read_null()
    {
        var collection = tempDatabase.CreateTemporaryCollection<IDictionaryStringValuesEntity>();
        collection.InsertOne(new IDictionaryStringValuesEntity {_id = ObjectId.GenerateNewId(), aDictionary = null});

        using var db = SingleEntityDbContext.Create(collection);
        var actual = db.Entities.FirstOrDefault();

        Assert.NotNull(actual);
        Assert.Null(actual.aDictionary);
    }

    [Fact]
    public void IDictionary_string_values_write_read_with_items()
    {
        var collection = tempDatabase.CreateTemporaryCollection<IDictionaryStringValuesEntity>();
        var expected = new Dictionary<string, string>
        {
            {"Season", "Summer"}, {"Temperature", "35'"}, {"Clouds", "None"}, {"Wind", "Breeze"}
        };
        var item = new IDictionaryStringValuesEntity {_id = ObjectId.GenerateNewId(), aDictionary = expected};

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entities.Add(item);
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var actual = db.Entities.FirstOrDefault();
            Assert.NotNull(actual);
            Assert.Equal(expected, actual.aDictionary);
        }
    }

    [Fact]
    public void IDictionary_int_values_read_empty()
    {
        var collection = tempDatabase.CreateTemporaryCollection<IDictionaryIntValuesEntity>();
        collection.InsertOne(new IDictionaryIntValuesEntity
        {
            _id = ObjectId.GenerateNewId(), aDictionary = new Dictionary<string, int>()
        });

        using var db = SingleEntityDbContext.Create(collection);
        var actual = db.Entities.FirstOrDefault();

        Assert.NotNull(actual);
        Assert.NotNull(actual.aDictionary);
        Assert.Empty(actual.aDictionary);
    }

    [Fact]
    public void IDictionary_int_values_write_read_with_items()
    {
        var collection = tempDatabase.CreateTemporaryCollection<IDictionaryIntValuesEntity>();
        var expected = new Dictionary<string, int> {{"Season", 2}, {"Temperature", 35}, {"Clouds", 0}, {"Wind", 11}};
        var item = new IDictionaryIntValuesEntity {_id = ObjectId.GenerateNewId(), aDictionary = expected};

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entities.Add(item);
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var actual = db.Entities.FirstOrDefault();
            Assert.NotNull(actual);
            Assert.Equal(expected, actual.aDictionary);
        }
    }

    [Fact(Skip = "Dictionary with collection value types is currently broken")]
    public void Dictionary_string_array_values_write_read_with_items()
    {
        var collection = tempDatabase.CreateTemporaryCollection<DictionaryStringArrayValuesEntity>();
        var expected = new Dictionary<string, string[]>
        {
            {"Seasons", ["Summer", "Autumn", "Winter", "Spring"]},
            {"Temperature", ["35'", "42'"]},
            {"Clouds", ["None", "Light", "Many"]},
            {"Wind", ["Breeze", "Gale", "Storm"]}
        };
        var item = new DictionaryStringArrayValuesEntity {_id = ObjectId.GenerateNewId(), aDictionary = expected};

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entities.Add(item);
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var actual = db.Entities.FirstOrDefault();
            Assert.NotNull(actual);
            Assert.Equal(expected, actual.aDictionary);
        }
    }

    [Fact]
    public void IReadOnlyDictionary_string_values_read_empty()
    {
        var collection = tempDatabase.CreateTemporaryCollection<IReadOnlyDictionaryStringValuesEntity>();
        collection.InsertOne(new IReadOnlyDictionaryStringValuesEntity {_id = ObjectId.GenerateNewId()});

        using var db = SingleEntityDbContext.Create(collection);
        var actual = db.Entities.FirstOrDefault();

        Assert.NotNull(actual);
        Assert.NotNull(actual.aDictionary);
        Assert.Empty(actual.aDictionary);
    }

    [Fact]
    public void IReadOnlyDictionary_string_values_read_null()
    {
        var collection = tempDatabase.CreateTemporaryCollection<IReadOnlyDictionaryStringValuesEntity>();
        collection.InsertOne(new IReadOnlyDictionaryStringValuesEntity {_id = ObjectId.GenerateNewId(), aDictionary = null});

        using var db = SingleEntityDbContext.Create(collection);
        var actual = db.Entities.FirstOrDefault();

        Assert.NotNull(actual);
        Assert.Null(actual.aDictionary);
    }

    [Fact]
    public void IReadOnlyDictionary_string_values_write_read_with_items()
    {
        var collection = tempDatabase.CreateTemporaryCollection<IReadOnlyDictionaryStringValuesEntity>();
        var expected = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>
        {
            {"Season", "Summer"}, {"Temperature", "35'"}, {"Clouds", "None"}, {"Wind", "Breeze"}
        });
        var item = new IReadOnlyDictionaryStringValuesEntity {_id = ObjectId.GenerateNewId(), aDictionary = expected};

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entities.Add(item);
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var actual = db.Entities.FirstOrDefault();
            Assert.NotNull(actual);
            Assert.Equal(expected, actual.aDictionary);
        }
    }
}
