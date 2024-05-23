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
using System.Reflection;
using MongoDB.Bson;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Mapping;

[XUnitCollection("MappingTests")]
public class ClrTypeMappingTests(TemporaryDatabaseFixture tempDatabase)
    : IClassFixture<TemporaryDatabaseFixture>
{
    private readonly Random random = new();

    class Entity<TValue>
    {
        public ObjectId _id { get; set; }
        public TValue Value { get; set; }
    }

    class IdEntity
    {
        public ObjectId _id { get; set; }
    }

    class GuidEntity : IdEntity
    {
        public Guid aGuid { get; set; }
    }

    class StringEntity : IdEntity
    {
        public string aString { get; set; }
    }

    class Int16Entity : IdEntity
    {
        public Int16 anInt16 { get; set; }
    }

    class Int32Entity : IdEntity
    {
        public Int32 anInt32 { get; set; }
    }

    class Int64Entity : IdEntity
    {
        public Int64 anInt64 { get; set; }
    }

    class ByteEntity : IdEntity
    {
        public Byte aByte { get; set; }
    }

    class CharEntity : IdEntity
    {
        public Char aChar { get; set; }
    }

    class DecimalEntity : IdEntity
    {
        public Decimal aDecimal { get; set; }
    }

    class SingleFloatEntity : IdEntity
    {
        public Single aSingle { get; set; }
    }

    class DoubleFloatEntity : IdEntity
    {
        public Double aDouble { get; set; }
    }

    class ListEntity : IdEntity
    {
        public List<string>? aList { get; set; }
    }

    class ReadOnlyCollectionEntity : IdEntity
    {
        public ReadOnlyCollection<string> aCollection { get; set; }
    }

    class CollectionEntity : IdEntity
    {
        public Collection<string> aCollection { get; set; }
    }

    class ObservableCollectionEntity : IdEntity
    {
        public ObservableCollection<string> aCollection { get; set; }
    }

    class IReadOnlyListEntity : IdEntity
    {
        public IReadOnlyList<string> aList { get; set; }
    }

    class IEnumerableEntity : IdEntity
    {
        public IEnumerable<string> anEnumerable { get; set; }
    }

    class IListEntity : IdEntity
    {
        public IList<string>? aList { get; set; }
    }

    class ListOfListEntity : IdEntity
    {
        public List<List<string>>? aListOfLists { get; set; }
    }

    class ListOfOwnedEntityListEntity : IdEntity
    {
        public List<List<SomeOwnedEntity>>? aListOfLists { get; set; }
    }

    class SomeOwnedEntity
    {
        public string name { get; set; }
    }

    class IListOfListEntity : IdEntity
    {
        public IList<List<string>>? aListOfLists { get; set; }
    }

    class ListSubclassOfListSubclassEntity : IdEntity
    {
        public ListSubclass<ListSubclass<string>>? aListOfLists { get; set; }
    }

    class DictionaryStringValuesEntity : IdEntity
    {
        public Dictionary<string, string>? aDictionary { get; set; }
    }

    class DictionaryIntValuesEntity : IdEntity
    {
        public Dictionary<string, int>? aDictionary { get; set; }
    }

    class IDictionaryObjectValuesEntity : IdEntity
    {
        public Dictionary<string, object>? aDictionary { get; set; }
    }

    class DictionaryOfListsEntity : IdEntity
    {
        public Dictionary<string, List<string>>? aDictionary { get; set; }
    }

    class ListSubclass<T> : List<T>
    {
        public ListSubclass() : base()
        {
        }

        public ListSubclass(IEnumerable<T> items) : base(items)
        {
        }
    }

    [Fact]
    public void Guid_read()
    {
        var collection = tempDatabase.CreateTemporaryCollection<GuidEntity>();

        var expected = Guid.NewGuid();
        collection.InsertOne(new GuidEntity
        {
            _id = ObjectId.GenerateNewId(), aGuid = expected
        });

        using var db = SingleEntityDbContext.Create(collection);
        var actual = db.Entities.FirstOrDefault();

        Assert.NotNull(actual);
        Assert.Equal(expected, actual.aGuid);
    }

    [Fact]
    public void String_read()
    {
        var collection = tempDatabase.CreateTemporaryCollection<StringEntity>();

        var expected = Guid.NewGuid().ToString();
        collection.InsertOne(new StringEntity
        {
            _id = ObjectId.GenerateNewId(), aString = expected
        });

        using var db = SingleEntityDbContext.Create(collection);
        var actual = db.Entities.FirstOrDefault();

        Assert.NotNull(actual);
        Assert.Equal(expected, actual.aString);
    }

    [Fact]
    public void Int16_positive_read()
    {
        var collection = tempDatabase.CreateTemporaryCollection<Int16Entity>();

        var expected = (Int16)random.Next(1, Int16.MaxValue);
        collection.InsertOne(new Int16Entity
        {
            _id = ObjectId.GenerateNewId(), anInt16 = expected
        });

        using var db = SingleEntityDbContext.Create(collection);
        var actual = db.Entities.FirstOrDefault();

        Assert.NotNull(actual);
        Assert.Equal(expected, actual.anInt16);
    }

    [Fact]
    public void Int16_zero_read()
    {
        var collection = tempDatabase.CreateTemporaryCollection<Int16Entity>();

        collection.InsertOne(new Int16Entity
        {
            _id = ObjectId.GenerateNewId(), anInt16 = 0
        });

        using var db = SingleEntityDbContext.Create(collection);
        var actual = db.Entities.FirstOrDefault();

        Assert.NotNull(actual);
        Assert.Equal(0, actual.anInt16);
    }

    [Fact]
    public void Int16_negative_read()
    {
        var collection = tempDatabase.CreateTemporaryCollection<Int16Entity>();

        var expected = (Int16)random.Next(Int16.MinValue, -1);
        collection.InsertOne(new Int16Entity
        {
            _id = ObjectId.GenerateNewId(), anInt16 = expected
        });

        using var db = SingleEntityDbContext.Create(collection);
        var actual = db.Entities.FirstOrDefault();

        Assert.NotNull(actual);
        Assert.Equal(expected, actual.anInt16);
    }

    [Fact]
    public void Int32_positive_read()
    {
        var collection = tempDatabase.CreateTemporaryCollection<Int32Entity>();

        var expected = random.Next(1, Int32.MaxValue);
        collection.InsertOne(new Int32Entity
        {
            _id = ObjectId.GenerateNewId(), anInt32 = expected
        });

        using var db = SingleEntityDbContext.Create(collection);
        var actual = db.Entities.FirstOrDefault();

        Assert.NotNull(actual);
        Assert.Equal(expected, actual.anInt32);
    }

    [Fact]
    public void Int32_zero_read()
    {
        var collection = tempDatabase.CreateTemporaryCollection<Int32Entity>();

        collection.InsertOne(new Int32Entity
        {
            _id = ObjectId.GenerateNewId(), anInt32 = 0
        });

        using var db = SingleEntityDbContext.Create(collection);
        var actual = db.Entities.FirstOrDefault();

        Assert.NotNull(actual);
        Assert.Equal(0, actual.anInt32);
    }

    [Fact]
    public void Int32_negative_read()
    {
        var collection = tempDatabase.CreateTemporaryCollection<Int32Entity>();

        var expected = random.Next(Int32.MinValue, -1);
        collection.InsertOne(new Int32Entity
        {
            _id = ObjectId.GenerateNewId(), anInt32 = expected
        });

        using var db = SingleEntityDbContext.Create(collection);
        var actual = db.Entities.FirstOrDefault();

        Assert.NotNull(actual);
        Assert.Equal(expected, actual.anInt32);
    }

    [Fact]
    public void Int64_positive_read()
    {
        var collection = tempDatabase.CreateTemporaryCollection<Int64Entity>();

        var expected = random.NextInt64(1, Int64.MaxValue);
        collection.InsertOne(new Int64Entity
        {
            _id = ObjectId.GenerateNewId(), anInt64 = expected
        });

        using var db = SingleEntityDbContext.Create(collection);
        var actual = db.Entities.FirstOrDefault();

        Assert.NotNull(actual);
        Assert.Equal(expected, actual.anInt64);
    }

    [Fact]
    public void Int64_zero_read()
    {
        var collection = tempDatabase.CreateTemporaryCollection<Int64Entity>();

        collection.InsertOne(new Int64Entity
        {
            _id = ObjectId.GenerateNewId(), anInt64 = 0
        });

        using var db = SingleEntityDbContext.Create(collection);
        var actual = db.Entities.FirstOrDefault();

        Assert.NotNull(actual);
        Assert.Equal(0, actual.anInt64);
    }

    [Fact]
    public void Int64_negative_read()
    {
        var collection = tempDatabase.CreateTemporaryCollection<Int64Entity>();

        var expected = random.NextInt64(Int64.MinValue, -1);
        collection.InsertOne(new Int64Entity
        {
            _id = ObjectId.GenerateNewId(), anInt64 = expected
        });

        using var db = SingleEntityDbContext.Create(collection);
        var actual = db.Entities.FirstOrDefault();

        Assert.NotNull(actual);
        Assert.Equal(expected, actual.anInt64);
    }

    [Fact]
    public void Byte_read()
    {
        var collection = tempDatabase.CreateTemporaryCollection<ByteEntity>();

        var expected = (Byte)random.Next(0, Byte.MaxValue);
        collection.InsertOne(new ByteEntity
        {
            _id = ObjectId.GenerateNewId(), aByte = expected
        });

        using var db = SingleEntityDbContext.Create(collection);
        var actual = db.Entities.FirstOrDefault();

        Assert.NotNull(actual);
        Assert.Equal(expected, actual.aByte);
    }

    [Fact]
    public void Char_read()
    {
        var collection = tempDatabase.CreateTemporaryCollection<CharEntity>();

        var expected = (Char)random.Next(1, 0xD799);
        collection.InsertOne(new CharEntity
        {
            _id = ObjectId.GenerateNewId(), aChar = expected
        });

        using var db = SingleEntityDbContext.Create(collection);
        var actual = db.Entities.FirstOrDefault();

        Assert.NotNull(actual);
        Assert.Equal(expected, actual.aChar);
    }

    [Fact]
    public void Decimal_positive_read()
    {
        var collection = tempDatabase.CreateTemporaryCollection<DecimalEntity>();

        var expected = 0m;
        while (expected <= 0)
            expected = random.NextDecimal();

        collection.InsertOne(new DecimalEntity
        {
            _id = ObjectId.GenerateNewId(), aDecimal = expected
        });

        using var db = SingleEntityDbContext.Create(collection);
        var actual = db.Entities.FirstOrDefault();

        Assert.NotNull(actual);
        Assert.Equal(expected, actual.aDecimal);
    }

    [Fact]
    public void Decimal_zero_read()
    {
        var collection = tempDatabase.CreateTemporaryCollection<DecimalEntity>();

        collection.InsertOne(new DecimalEntity
        {
            _id = ObjectId.GenerateNewId(), aDecimal = 0m
        });

        using var db = SingleEntityDbContext.Create(collection);
        var actual = db.Entities.FirstOrDefault();

        Assert.NotNull(actual);
        Assert.Equal(0, actual.aDecimal);
    }

    [Fact]
    public void Decimal_negative_read()
    {
        var collection = tempDatabase.CreateTemporaryCollection<DecimalEntity>();

        var expected = 0m;
        while (expected >= 0)
            expected = random.NextDecimal();

        collection.InsertOne(new DecimalEntity
        {
            _id = ObjectId.GenerateNewId(), aDecimal = expected
        });

        using var db = SingleEntityDbContext.Create(collection);
        var actual = db.Entities.FirstOrDefault();

        Assert.NotNull(actual);
        Assert.Equal(expected, actual.aDecimal);
    }

    [Fact]
    public void Single_positive_read()
    {
        var collection = tempDatabase.CreateTemporaryCollection<SingleFloatEntity>();

        var expected = random.NextSingle() * Single.MaxValue;
        collection.InsertOne(new SingleFloatEntity
        {
            _id = ObjectId.GenerateNewId(), aSingle = expected
        });

        using var db = SingleEntityDbContext.Create(collection);
        var actual = db.Entities.FirstOrDefault();

        Assert.NotNull(actual);
        Assert.Equal(expected, actual.aSingle);
    }

    [Fact]
    public void Single_zero_read()
    {
        var collection = tempDatabase.CreateTemporaryCollection<SingleFloatEntity>();

        collection.InsertOne(new SingleFloatEntity
        {
            _id = ObjectId.GenerateNewId(), aSingle = 0
        });

        using var db = SingleEntityDbContext.Create(collection);
        var actual = db.Entities.FirstOrDefault();

        Assert.NotNull(actual);
        Assert.Equal(0, actual.aSingle);
    }

    [Fact]
    public void Single_negative_read()
    {
        var collection = tempDatabase.CreateTemporaryCollection<SingleFloatEntity>();

        var expected = random.NextSingle() * Single.MinValue;
        collection.InsertOne(new SingleFloatEntity
        {
            _id = ObjectId.GenerateNewId(), aSingle = expected
        });

        using var db = SingleEntityDbContext.Create(collection);
        var actual = db.Entities.FirstOrDefault();

        Assert.NotNull(actual);
        Assert.Equal(expected, actual.aSingle);
    }

    [Fact]
    public void Double_positive_read()
    {
        var collection = tempDatabase.CreateTemporaryCollection<DoubleFloatEntity>();

        var expected = random.NextDouble() * Double.MaxValue;
        collection.InsertOne(new DoubleFloatEntity
        {
            _id = ObjectId.GenerateNewId(), aDouble = expected
        });

        using var db = SingleEntityDbContext.Create(collection);
        var actual = db.Entities.FirstOrDefault();

        Assert.NotNull(actual);
        Assert.Equal(expected, actual.aDouble);
    }

    [Fact]
    public void Double_zero_read()
    {
        var collection = tempDatabase.CreateTemporaryCollection<DoubleFloatEntity>();

        collection.InsertOne(new DoubleFloatEntity
        {
            _id = ObjectId.GenerateNewId(), aDouble = 0
        });

        using var db = SingleEntityDbContext.Create(collection);
        var actual = db.Entities.FirstOrDefault();

        Assert.NotNull(actual);
        Assert.Equal(0, actual.aDouble);
    }

    [Fact]
    public void Double_negative_read()
    {
        var collection = tempDatabase.CreateTemporaryCollection<DoubleFloatEntity>();

        var expected = random.NextDouble() * Double.MinValue;
        collection.InsertOne(new DoubleFloatEntity
        {
            _id = ObjectId.GenerateNewId(), aDouble = expected
        });

        using var db = SingleEntityDbContext.Create(collection);
        var actual = db.Entities.FirstOrDefault();

        Assert.NotNull(actual);
        Assert.Equal(expected, actual.aDouble);
    }

    [Fact]
    public void List_write_read_with_items()
    {
        var collection = tempDatabase.CreateTemporaryCollection<ListEntity>();
        var expected = new List<string>
        {
            "a", "b", "c"
        };

        {
            var item = new ListEntity
            {
                _id = ObjectId.GenerateNewId(), aList = expected
            };
            using var db = SingleEntityDbContext.Create(collection);
            db.Entities.Add(item);
            db.SaveChanges();
            Assert.Equal(expected, item.aList);
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var actual = db.Entities.FirstOrDefault();
            Assert.NotNull(actual);
            Assert.Equal(expected, actual.aList);
        }
    }

    [Fact]
    public void List_read_empty()
    {
        var collection = tempDatabase.CreateTemporaryCollection<ListEntity>();
        collection.InsertOne(new ListEntity
        {
            _id = ObjectId.GenerateNewId(), aList = []
        });

        using var db = SingleEntityDbContext.Create(collection);
        var actual = db.Entities.FirstOrDefault();

        Assert.NotNull(actual);
        Assert.Empty(actual.aList);
    }

    [Fact]
    public void IList_write_read_with_items()
    {
        var collection = tempDatabase.CreateTemporaryCollection<IListEntity>();
        var expected = new List<string>
        {
            "a", "b", "c"
        };

        {
            var item = new IListEntity
            {
                _id = ObjectId.GenerateNewId(), aList = expected
            };
            using var db = SingleEntityDbContext.Create(collection);
            db.Entities.Add(item);
            db.SaveChanges();
            Assert.Equal(expected, item.aList);
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var actual = db.Entities.FirstOrDefault();
            Assert.NotNull(actual);
            Assert.Equal(expected, actual.aList);
        }
    }

    [Fact]
    public void IList_read_empty()
    {
        var collection = tempDatabase.CreateTemporaryCollection<IListEntity>();
        collection.InsertOne(new IListEntity
        {
            _id = ObjectId.GenerateNewId(), aList = []
        });

        using var db = SingleEntityDbContext.Create(collection);
        var actual = db.Entities.FirstOrDefault();

        Assert.NotNull(actual);
        Assert.Empty(actual.aList);
    }

    [Fact]
    public void IList_read_null()
    {
        var collection = tempDatabase.CreateTemporaryCollection<IListEntity>();
        collection.InsertOne(new IListEntity
        {
            _id = ObjectId.GenerateNewId(), aList = null
        });

        using var db = SingleEntityDbContext.Create(collection);
        var actual = db.Entities.FirstOrDefault();

        Assert.NotNull(actual);
        Assert.Null(actual.aList);
    }

    [Fact]
    public void ListOfLists_read_empty()
    {
        var collection = tempDatabase.CreateTemporaryCollection<ListOfListEntity>();
        collection.InsertOne(new ListOfListEntity
        {
            _id = ObjectId.GenerateNewId(), aListOfLists = []
        });

        using var db = SingleEntityDbContext.Create(collection);
        var actual = db.Entities.FirstOrDefault();

        Assert.NotNull(actual);
        Assert.Empty(actual.aListOfLists);
    }

    [Fact]
    public void ListOfLists_write_read_with_items()
    {
        var collection = tempDatabase.CreateTemporaryCollection<ListOfListEntity>();
        var expected = new List<List<string>>
        {
            new() {"a", "e", "i", "o", "u" },
            new() { "b", "c", "d", "f", "g", "h" }
        };

        {
            var item = new ListOfListEntity
            {
                _id = ObjectId.GenerateNewId(), aListOfLists = expected
            };
            using var db = SingleEntityDbContext.Create(collection);
            db.Entities.Add(item);
            db.SaveChanges();
            Assert.Equal(expected, item.aListOfLists);
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var actual = db.Entities.FirstOrDefault();
            Assert.NotNull(actual);
            Assert.Equal(expected, actual.aListOfLists);
        }
    }

    [Fact]
    public void IListOfLists_read_empty()
    {
        var collection = tempDatabase.CreateTemporaryCollection<IListOfListEntity>();
        collection.InsertOne(new IListOfListEntity
        {
            _id = ObjectId.GenerateNewId(), aListOfLists = []
        });

        using var db = SingleEntityDbContext.Create(collection);
        var actual = db.Entities.FirstOrDefault();

        Assert.NotNull(actual);
        Assert.Empty(actual.aListOfLists);
    }

    [Fact]
    private void IListOfLists_write_read_with_items()
    {
        var collection = tempDatabase.CreateTemporaryCollection<IListOfListEntity>();
        var expected = new List<List<string>>
        {
            new() {"a", "e", "i", "o", "u" },
            new() { "b", "c", "d", "f", "g", "h" }
        };

        {
            var item = new IListOfListEntity
            {
                _id = ObjectId.GenerateNewId(), aListOfLists = expected
            };
            using var db = SingleEntityDbContext.Create(collection);
            db.Entities.Add(item);
            db.SaveChanges();
            Assert.Equal(expected, item.aListOfLists);
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var actual = db.Entities.FirstOrDefault();
            Assert.NotNull(actual);
            Assert.Equal(expected, actual.aListOfLists);
        }
    }

    [Fact]
    public void ListSubclassOfListSubclass_read_empty()
    {
        var collection = tempDatabase.CreateTemporaryCollection<ListSubclassOfListSubclassEntity>();
        collection.InsertOne(new ListSubclassOfListSubclassEntity
        {
            _id = ObjectId.GenerateNewId(), aListOfLists = []
        });

        using var db = SingleEntityDbContext.Create(collection);
        var actual = db.Entities.FirstOrDefault();

        Assert.NotNull(actual);
        Assert.Empty(actual.aListOfLists);
    }

    [Fact]
    private void ListSubclassOfListSubclass_write_read_with_items()
    {
        var collection = tempDatabase.CreateTemporaryCollection<ListSubclassOfListSubclassEntity>();
        var expected = new ListSubclass<ListSubclass<string>>
        {
            new() {"a", "e", "i", "o", "u" },
            new() { "b", "c", "d", "f", "g", "h" }
        };

        {
            var item = new ListSubclassOfListSubclassEntity
            {
                _id = ObjectId.GenerateNewId(), aListOfLists = expected
            };
            using var db = SingleEntityDbContext.Create(collection);
            db.Entities.Add(item);
            db.SaveChanges();
            Assert.Equal(expected, item.aListOfLists);
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var actual = db.Entities.FirstOrDefault();
            Assert.NotNull(actual);
            Assert.Equal(expected, actual.aListOfLists);
        }
    }

    [Fact]
    public void ReadOnlyCollection_write_read_with_items()
    {
        var collection = tempDatabase.CreateTemporaryCollection<ReadOnlyCollectionEntity>();
        var expected = new ReadOnlyCollection<string>(["z", "x", "y", "1", "2", "3"]);

        {
            var item = new ReadOnlyCollectionEntity
            {
                _id = ObjectId.GenerateNewId(), aCollection = expected
            };
            using var db = SingleEntityDbContext.Create(collection);
            db.Entities.Add(item);
            db.SaveChanges();
            Assert.Equal(expected, item.aCollection);
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var actual = db.Entities.FirstOrDefault();
            Assert.NotNull(actual);
            Assert.Equal(expected, actual.aCollection);
        }
    }

    [Fact]
    public void Collection_write_read_with_items()
    {
        var collection = tempDatabase.CreateTemporaryCollection<CollectionEntity>();
        var expected = new Collection<string>(["z", "x", "y", "1", "2", "3"]);

        {
            var item = new CollectionEntity
            {
                _id = ObjectId.GenerateNewId(), aCollection = expected
            };
            using var db = SingleEntityDbContext.Create(collection);
            db.Entities.Add(item);
            db.SaveChanges();
            Assert.Equal(expected, item.aCollection);
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var actual = db.Entities.FirstOrDefault();
            Assert.NotNull(actual);
            Assert.Equal(expected, actual.aCollection);
        }
    }

    [Fact]
    public void ObservableCollection_write_read_with_items()
    {
        var collection = tempDatabase.CreateTemporaryCollection<ObservableCollectionEntity>();
        var expected = new ObservableCollection<string>(["z", "x", "y", "1", "2", "3"]);

        {
            var item = new ObservableCollectionEntity
            {
                _id = ObjectId.GenerateNewId(), aCollection = expected
            };
            using var db = SingleEntityDbContext.Create(collection);
            db.Entities.Add(item);
            db.SaveChanges();
            Assert.Equal(expected, item.aCollection);
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var actual = db.Entities.FirstOrDefault();
            Assert.NotNull(actual);
            Assert.Equal(expected, actual.aCollection);
        }
    }

    [Fact]
    public void IReadOnlyList_write_read_with_items()
    {
        var collection = tempDatabase.CreateTemporaryCollection<IReadOnlyListEntity>();
        var expected = new List<string>(["z", "x", "y", "1", "2", "3"]).AsReadOnly();

        {
            var item = new IReadOnlyListEntity
            {
                _id = ObjectId.GenerateNewId(), aList = expected
            };
            using var db = SingleEntityDbContext.Create(collection);
            db.Entities.Add(item);
            db.SaveChanges();
            Assert.Equal(expected, item.aList);
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var actual = db.Entities.FirstOrDefault();
            Assert.NotNull(actual);
            Assert.Equal(expected, actual.aList);
        }
    }

    [Fact]
    public void IEnumerable_write_read_with_items()
    {
        var collection = tempDatabase.CreateTemporaryCollection<IEnumerableEntity>();
        var expected = new List<string>(["z", "x", "y", "1", "2", "3"]).AsEnumerable();

        {
            var item = new IEnumerableEntity
            {
                _id = ObjectId.GenerateNewId(), anEnumerable = expected
            };
            using var db = SingleEntityDbContext.Create(collection);
            db.Entities.Add(item);
            db.SaveChanges();
            Assert.Equal(expected, item.anEnumerable);
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var actual = db.Entities.FirstOrDefault();
            Assert.NotNull(actual);
            Assert.Equal(expected, actual.anEnumerable);
        }
    }

    [Theory]
    [InlineData(typeof(short), (short)0)]
    [InlineData(typeof(short), (short)-15)]
    [InlineData(typeof(short), (short)42)]
    [InlineData(typeof(short?), null)]
    [InlineData(typeof(short?), (short)0)]
    [InlineData(typeof(short?), (short)-15)]
    [InlineData(typeof(short?), (short)42)]
    [InlineData(typeof(int), 0)]
    [InlineData(typeof(int), -15)]
    [InlineData(typeof(int), 42)]
    [InlineData(typeof(int?), null)]
    [InlineData(typeof(int?), 0)]
    [InlineData(typeof(int?), -15)]
    [InlineData(typeof(int?), 42)]
    [InlineData(typeof(TestEnum), TestEnum.EnumValue0)]
    [InlineData(typeof(TestEnum), TestEnum.EnumValue1)]
    [InlineData(typeof(TestEnum?), TestEnum.EnumValue0)]
    [InlineData(typeof(TestEnum?), TestEnum.EnumValue1)]
    [InlineData(typeof(TestEnum?), null)]
    [InlineData(typeof(TestByteEnum), TestByteEnum.EnumValue0)]
    [InlineData(typeof(TestByteEnum), TestByteEnum.EnumValue1)]
    [InlineData(typeof(TestByteEnum?), TestByteEnum.EnumValue0)]
    [InlineData(typeof(TestByteEnum?), TestByteEnum.EnumValue1)]
    [InlineData(typeof(TestByteEnum?), null)]
    [InlineData(typeof(int[]), null)]
    [InlineData(typeof(int[]), new[]
    {
        -5, 0, 128, 10
    })]
    [InlineData(typeof(string[]), null)]
    [InlineData(typeof(string[]), new[]
    {
        "one", "two"
    })]
    [InlineData(typeof(List<int>), null)]
    [InlineData(typeof(List<int>), new[]
    {
        -5, 0, 128, 10
    })]
    [InlineData(typeof(IList<int>), null)]
    [InlineData(typeof(IList<int>), new[]
    {
        -5, 0, 128, 10
    })]
    [InlineData(typeof(IReadOnlyList<int>), null)]
    [InlineData(typeof(IReadOnlyList<int>), new[]
    {
        -5, 0, 128, 10
    })]
    [InlineData(typeof(ReadOnlyCollection<int>), null)]
    [InlineData(typeof(ReadOnlyCollection<int>), new[]
    {
        -5, 0, 128, 10
    })]
    [InlineData(typeof(Collection<int>), null)]
    [InlineData(typeof(Collection<int>), new[]
    {
        -100, 100, 0, 1234
    })]
    [InlineData(typeof(ObservableCollection<int>), null)]
    [InlineData(typeof(ObservableCollection<int>), new[]
    {
        -100, 100, 0, 1234
    })]
    [InlineData(typeof(ListSubclass<int>), null)]
    [InlineData(typeof(ListSubclass<int>), new[]
    {
        -5, 0, 128, 10
    })]
    public void Type_mapping_test(Type valueType, object? value)
    {
        if (value != null && !value.GetType().IsAssignableTo(valueType))
        {
            value = Activator.CreateInstance(valueType, value);
        }

        GetType()
            .GetMethod(nameof(ClrTypeMappingTestImpl), BindingFlags.Instance | BindingFlags.NonPublic)!
            .MakeGenericMethod(valueType)
            .Invoke(this, [value]);
    }

    [Fact]
    public void Type_mapping_test_list_nullable_int()
    {
        ClrTypeMappingTestImpl(new List<int?>(new int?[]
        {
            1, 2, 3, null, 4
        }));
    }

    private void ClrTypeMappingTestImpl<TValue>(TValue value)
    {
        var collection = tempDatabase.CreateTemporaryCollection<Entity<TValue>>("ClrTypeMapping", typeof(TValue), value);
        collection.InsertOne(new Entity<TValue>
        {
            _id = ObjectId.GenerateNewId(), Value = value
        });

        using var db = SingleEntityDbContext.Create(collection);
        var actual = db.Entities.FirstOrDefault();

        Assert.NotNull(actual);
        Assert.Equal(value, actual.Value);
    }

    private enum TestEnum
    {
        EnumValue0 = 0,
        EnumValue1 = 1
    }

    private enum TestByteEnum : byte
    {
        EnumValue0 = 0,
        EnumValue1 = 1
    }
}
