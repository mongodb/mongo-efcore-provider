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
using Microsoft.EntityFrameworkCore;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Mapping;

[XUnitCollection("MappingTests")]
public class ClrTypeMappingTests(AtlasTemporaryDatabaseFixture database)
    : IClassFixture<AtlasTemporaryDatabaseFixture>
{
    private readonly Random _random = new();

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

    class BsonGuidEntity : IdEntity
    {
        [BsonGuidRepresentation(GuidRepresentation.Standard)]
        public Guid aGuid { get; set; }
    }

    class StringEntity : IdEntity
    {
        public string aString { get; set; }
    }

    class StringEntityGetOnly : IdEntity
    {
        public StringEntityGetOnly()
        {
        }

        public StringEntityGetOnly(ObjectId id, string aString)
        {
            _id = id;
            this.aString = aString;
        }

        public string aString { get; }
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

    class ListOfOwnedEntityListEntity : IdEntity  //TODO This is never used
    {
        public List<List<SomeOwnedEntity>>? aListOfLists { get; set; }
    }

    class DateOnlyEntity : IdEntity
    {
        public DateOnly aDateOnly { get; set; }
    }

    class TimeOnlyEntity : IdEntity
    {
        public TimeOnly aTimeOnly { get; set; }
    }

    class ByteArrayEntity : IdEntity
    {
        public Byte[] aByteArray { get; set; }
    }

    class ByteArrayEntityGetOnly : IdEntity
    {
        public ByteArrayEntityGetOnly()
        {
        }

        public ByteArrayEntityGetOnly(Byte[] aByteArray)
        {
            this.aByteArray = aByteArray;
        }

        public Byte[] aByteArray { get; }
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

    class ListSubclass<T> : List<T>
    {
        public ListSubclass()
        {
        }

        public ListSubclass(IEnumerable<T> items) : base(items)
        {
        }
    }

    [Fact]
    public void Guid_read_and_update()
    {
        var inserted = Guid.NewGuid();
        var updated = Guid.NewGuid();

        database.CreateCollection<BsonGuidEntity>().InsertOne(
            new BsonGuidEntity {_id = ObjectId.GenerateNewId(), aGuid = inserted});

        var collection = database.GetCollection<GuidEntity>();

        using (var db = SingleEntityDbContext.Create(collection))
        {
            var actual = db.Entities.FirstOrDefault();

            Assert.NotNull(actual);
            Assert.Equal(inserted, actual.aGuid);
            actual.aGuid = updated;
            db.SaveChanges();
        }

        using (var db = SingleEntityDbContext.Create(collection))
        {
            var actual = db.Entities.FirstOrDefault();

            Assert.NotNull(actual);
            Assert.Equal(updated, actual.aGuid);
        }
    }

    [Fact]
    public void String_read_update()
    {
        var collection = database.CreateCollection<StringEntity>();

        var expected = "What do you get when you multiply six by nine?";
        collection.InsertOne(new StringEntity {_id = ObjectId.GenerateNewId(), aString = expected});

        using (var db = SingleEntityDbContext.Create(collection))
        {
            var actual = db.Entities.FirstOrDefault();

            Assert.NotNull(actual);
            Assert.Equal(expected, actual.aString);
            actual.aString = "42";
            db.SaveChanges();
        }

        using (var db = SingleEntityDbContext.Create(collection))
        {
            var actual = db.Entities.FirstOrDefault();

            Assert.NotNull(actual);
            Assert.Equal("42", actual.aString);
        }
    }

    [Fact]
    public void String_read_get_only()
    {
        var expected = "What do you get when you multiply six by nine?";
        database.CreateCollection<StringEntity>().InsertOne(new StringEntity {_id = ObjectId.GenerateNewId(), aString = expected});

        var collection = database.GetCollection<StringEntityGetOnly>();
        using var db = SingleEntityDbContext.Create(collection, mb => mb.Entity<StringEntityGetOnly>().Property(s => s.aString));
        var actual = db.Entities.FirstOrDefault();

        Assert.NotNull(actual);
        Assert.Equal(expected, actual.aString);
    }

    [Fact]
    public void DateOnly_read_update()
    {
        var collection = database.CreateCollection<DateOnlyEntity>();

        var expected = new DateOnly(_random.Next(1, 9999), _random.Next(1, 12), _random.Next(1, 28));
        collection.InsertOne(new DateOnlyEntity {_id = ObjectId.GenerateNewId(), aDateOnly = expected});

        using (var db = SingleEntityDbContext.Create(collection))
        {
            var actual = db.Entities.FirstOrDefault();

            Assert.NotNull(actual);
            Assert.Equal(expected, actual.aDateOnly);
            actual.aDateOnly = new(2000, 12, 31);
            db.SaveChanges();
        }

        using (var db = SingleEntityDbContext.Create(collection))
        {
            var actual = db.Entities.FirstOrDefault();

            Assert.NotNull(actual);
            Assert.Equal(new(2000, 12, 31), actual.aDateOnly);
        }
    }

    [Fact]
    public void TimeOnly_read_update()
    {
        var collection = database.CreateCollection<TimeOnlyEntity>();

        var expected = new TimeOnly(_random.Next(0, 24), _random.Next(0, 60), _random.Next(0, 60));
        collection.InsertOne(new TimeOnlyEntity {_id = ObjectId.GenerateNewId(), aTimeOnly = expected});

        using (var db = SingleEntityDbContext.Create(collection))
        {
            var actual = db.Entities.FirstOrDefault();

            Assert.NotNull(actual);
            Assert.Equal(expected, actual.aTimeOnly);
            actual.aTimeOnly = new(3, 33, 33);
            db.SaveChanges();
        }

        using (var db = SingleEntityDbContext.Create(collection))
        {
            var actual = db.Entities.FirstOrDefault();

            Assert.NotNull(actual);
            Assert.Equal(new(3, 33, 33), actual.aTimeOnly);
        }
    }

    [Fact]
    public void ByteArray_read_update()
    {
        var expected = new byte[4096];
        Random.Shared.NextBytes(expected);
        var updated = new byte[4096];
        Random.Shared.NextBytes(expected);

        var collection = database.CreateCollection<ByteArrayEntity>();
        collection.InsertOne(new ByteArrayEntity { _id = ObjectId.GenerateNewId(), aByteArray = expected});

        using (var db = SingleEntityDbContext.Create(collection))
        {
            var actual = db.Entities.FirstOrDefault();

            Assert.NotNull(actual);
            Assert.Equal(expected, actual.aByteArray);
            actual.aByteArray = updated;
            db.SaveChanges();
        }

        using (var db = SingleEntityDbContext.Create(collection))
        {
            var actual = db.Entities.FirstOrDefault();

            Assert.NotNull(actual);
            Assert.Equal(updated, actual.aByteArray);
        }
    }

    [Fact]
    public void ByteArray_read_get_only()
    {
        var expected = new byte[4096];
        Random.Shared.NextBytes(expected);

        database.CreateCollection<ByteArrayEntity>().InsertOne(new ByteArrayEntity {_id = ObjectId.GenerateNewId(), aByteArray = expected});

        var collection = database.GetCollection<ByteArrayEntityGetOnly>();
        using var db = SingleEntityDbContext.Create(collection, mb => mb.Entity<ByteArrayEntityGetOnly>().Property(s => s.aByteArray));
        var actual = db.Entities.FirstOrDefault();

        Assert.NotNull(actual);
        Assert.Equal(expected, actual.aByteArray);
    }

    [Theory]
    [InlineData(short.MaxValue, short.MinValue)]
    [InlineData((short)-1, short.MaxValue)]
    [InlineData((short)0, (short)-1)]
    [InlineData((short)1, (short)0)]
    [InlineData(short.MinValue, 1)]
    public void Int16_read_update(short inserted, short updated)
    {
        var collection = database.CreateCollection<Int16Entity>(values: [inserted, updated]);
        var id = ObjectId.GenerateNewId();
        collection.InsertOne(new Int16Entity {_id = id, anInt16 = inserted});

        using (var db = SingleEntityDbContext.Create(collection))
        {
            var actual = db.Entities.First(e => e._id == id);

            Assert.Equal(inserted, actual.anInt16);
            actual.anInt16 = updated;
            db.SaveChanges();
        }

        using (var db = SingleEntityDbContext.Create(collection))
        {
            var actual = db.Entities.First(e => e._id == id);

            Assert.NotNull(actual);
            Assert.Equal(updated, actual.anInt16);
        }
    }

    [Theory]
    [InlineData(int.MaxValue, int.MinValue)]
    [InlineData(-1, int.MaxValue)]
    [InlineData(0, -1)]
    [InlineData(1, 0)]
    [InlineData(int.MinValue, 1)]
    public void Int32_read_update(int inserted, int updated)
    {
        var collection = database.CreateCollection<Int32Entity>(values: [inserted, updated]);
        var id = ObjectId.GenerateNewId();
        collection.InsertOne(new Int32Entity {_id = id, anInt32 = inserted});

        using (var db = SingleEntityDbContext.Create(collection))
        {
            var actual = db.Entities.First(e => e._id == id);

            Assert.NotNull(actual);
            Assert.Equal(inserted, actual.anInt32);
            actual.anInt32 = updated;
            db.SaveChanges();
        }

        using (var db = SingleEntityDbContext.Create(collection))
        {
            var actual = db.Entities.First(e => e._id == id);

            Assert.NotNull(actual);
            Assert.Equal(updated, actual.anInt32);
        }
    }

    [Theory]
    [InlineData(long.MaxValue, long.MinValue)]
    [InlineData((long)-1, long.MaxValue)]
    [InlineData((long)0, (long)-1)]
    [InlineData((long)1, (long)0)]
    [InlineData(long.MinValue, 1)]
    public void Int64_read_update(long inserted, long updated)
    {
        var collection = database.CreateCollection<Int64Entity>(values: [inserted, updated]);
        var id = ObjectId.GenerateNewId();
        collection.InsertOne(new Int64Entity {_id = id, anInt64 = inserted});

        using (var db = SingleEntityDbContext.Create(collection))
        {
            var actual = db.Entities.First(e => e._id == id);

            Assert.NotNull(actual);
            Assert.Equal(inserted, actual.anInt64);
            actual.anInt64 = updated;
            db.SaveChanges();
        }

        using (var db = SingleEntityDbContext.Create(collection))
        {
            var actual = db.Entities.First(e => e._id == id);

            Assert.NotNull(actual);
            Assert.Equal(updated, actual.anInt64);
        }
    }

    [Theory]
    [InlineData(byte.MaxValue, byte.MinValue)]
    [InlineData((byte)0, byte.MaxValue)]
    [InlineData((byte)1, (byte)0)]
    [InlineData(byte.MinValue, 1)]
    public void Byte_read_update(byte inserted, byte updated)
    {
        var collection = database.CreateCollection<ByteEntity>(values: [inserted, updated]);
        var id = ObjectId.GenerateNewId();
        collection.InsertOne(new ByteEntity {_id = id, aByte = inserted});

        using (var db = SingleEntityDbContext.Create(collection))
        {
            var actual = db.Entities.First(e => e._id == id);

            Assert.NotNull(actual);
            Assert.Equal(inserted, actual.aByte);
            actual.aByte = updated;
            db.SaveChanges();
        }

        using (var db = SingleEntityDbContext.Create(collection))
        {
            var actual = db.Entities.First(e => e._id == id);

            Assert.NotNull(actual);
            Assert.Equal(updated, actual.aByte);
        }
    }

    [Theory]
    [InlineData(char.MaxValue, char.MinValue)]
    [InlineData((char)0, char.MaxValue)]
    [InlineData((char)1, (char)0)]
    [InlineData(char.MinValue, 1)]
    public void Char_read_update(char inserted, char updated)
    {
        var collection = database.CreateCollection<CharEntity>(values: [(int)inserted, (int)updated]);
        var id = ObjectId.GenerateNewId();
        collection.InsertOne(new CharEntity {_id = id, aChar = inserted});

        using (var db = SingleEntityDbContext.Create(collection))
        {
            var actual = db.Entities.First(e => e._id == id);

            Assert.NotNull(actual);
            Assert.Equal(inserted, actual.aChar);
            actual.aChar = updated;
            db.SaveChanges();
        }

        using (var db = SingleEntityDbContext.Create(collection))
        {
            var actual = db.Entities.First(e => e._id == id);

            Assert.NotNull(actual);
            Assert.Equal(updated, actual.aChar);
        }
    }

    [Theory]
    [InlineData("79228162514264337593543950335", "-79228162514264337593543950335")]
    [InlineData("-1.1", "79228162514264337593543950335")]
    [InlineData("0", "-1.1")]
    [InlineData("1.1", "0")]
    [InlineData("-79228162514264337593543950335", "1.1")]
    public void Decimal_read_update(string insertedString,string updatedString)
    {
        var inserted = decimal.Parse(insertedString);
        var updated = decimal.Parse(updatedString);

        var collection = database.CreateCollection<DecimalEntity>(values: [inserted, updated]);
        var id = ObjectId.GenerateNewId();
        collection.InsertOne(new DecimalEntity {_id = id, aDecimal = inserted});

        using (var db = SingleEntityDbContext.Create(collection))
        {
            var actual = db.Entities.First(e => e._id == id);

            Assert.NotNull(actual);
            Assert.Equal(inserted, actual.aDecimal);
            actual.aDecimal = updated;
            db.SaveChanges();
        }

        using (var db = SingleEntityDbContext.Create(collection))
        {
            var actual = db.Entities.First(e => e._id == id);

            Assert.NotNull(actual);
            Assert.Equal(updated, actual.aDecimal);
        }
    }

    [Theory]
    [InlineData(float.MaxValue, float.MinValue)]
    [InlineData((float)0, float.MaxValue)]
    [InlineData((float)1, (float)0)]
    [InlineData(float.MinValue, 1)]
    public void Single_read_update(float inserted, float updated)
    {
        var collection = database.CreateCollection<SingleFloatEntity>(values: [inserted, updated]);

        var id = ObjectId.GenerateNewId();
        collection.InsertOne(new SingleFloatEntity {_id = id, aSingle = inserted});

        using (var db = SingleEntityDbContext.Create(collection))
        {
            var actual = db.Entities.First(e => e._id == id);

            Assert.NotNull(actual);
            Assert.Equal(inserted, actual.aSingle);
            actual.aSingle = updated;
            db.SaveChanges();
        }

        using (var db = SingleEntityDbContext.Create(collection))
        {
            var actual = db.Entities.First(e => e._id == id);

            Assert.NotNull(actual);
            Assert.Equal(updated, actual.aSingle);
        }
    }

    [Theory]
    [InlineData(double.MaxValue, double.MinValue)]
    [InlineData((double)0, double.MaxValue)]
    [InlineData((double)1, (double)0)]
    [InlineData(double.MinValue, 1)]
    public void Double_read_update(double inserted, double updated)
    {
        var collection = database.CreateCollection<DoubleFloatEntity>(values: [inserted, updated]);

        var id = ObjectId.GenerateNewId();
        collection.InsertOne(new DoubleFloatEntity {_id = id, aDouble = inserted});

        using (var db = SingleEntityDbContext.Create(collection))
        {
            var actual = db.Entities.First(e => e._id == id);

            Assert.NotNull(actual);
            Assert.Equal(inserted, actual.aDouble);
            actual.aDouble = updated;
            db.SaveChanges();
        }

        using (var db = SingleEntityDbContext.Create(collection))
        {
            var actual = db.Entities.First(e => e._id == id);

            Assert.NotNull(actual);
            Assert.Equal(updated, actual.aDouble);
        }
    }

    [Fact]
    public void List_write_read_with_items()
    {
        var collection = database.CreateCollection<ListEntity>();
        var expected = new List<string> {"a", "b", "c"};

        {
            var item = new ListEntity {_id = ObjectId.GenerateNewId(), aList = expected};
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
        var collection = database.CreateCollection<ListEntity>();
        collection.InsertOne(new ListEntity {_id = ObjectId.GenerateNewId(), aList = []});

        using var db = SingleEntityDbContext.Create(collection);
        var actual = db.Entities.FirstOrDefault();

        Assert.NotNull(actual);
        Assert.NotNull(actual.aList);
        Assert.Empty(actual.aList);
    }

    [Fact]
    public void IList_write_read_with_items()
    {
        var collection = database.CreateCollection<IListEntity>();
        var expected = new List<string> {"a", "b", "c"};

        {
            var item = new IListEntity {_id = ObjectId.GenerateNewId(), aList = expected};
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
        var collection = database.CreateCollection<IListEntity>();
        collection.InsertOne(new IListEntity {_id = ObjectId.GenerateNewId(), aList = []});

        using var db = SingleEntityDbContext.Create(collection);
        var actual = db.Entities.FirstOrDefault();

        Assert.NotNull(actual);
        Assert.NotNull(actual.aList);
        Assert.Empty(actual.aList);
    }

    [Fact]
    public void IList_read_null()
    {
        var collection = database.CreateCollection<IListEntity>();
        collection.InsertOne(new IListEntity {_id = ObjectId.GenerateNewId(), aList = null});

        using var db = SingleEntityDbContext.Create(collection);
        var actual = db.Entities.FirstOrDefault();

        Assert.NotNull(actual);
        Assert.Null(actual.aList);
    }

    [Fact]
    public void ListOfLists_read_empty()
    {
        var collection = database.CreateCollection<ListOfListEntity>();
        collection.InsertOne(new ListOfListEntity {_id = ObjectId.GenerateNewId(), aListOfLists = []});

        using var db = SingleEntityDbContext.Create(collection);
        var actual = db.Entities.FirstOrDefault();

        Assert.NotNull(actual);
        Assert.NotNull(actual.aListOfLists);
        Assert.Empty(actual.aListOfLists);
    }

    [Fact]
    public void ListOfLists_write_read_with_items()
    {
        var collection = database.CreateCollection<ListOfListEntity>();
        var expected = new List<List<string>>
        {
            new()
            {
                "a",
                "e",
                "i",
                "o",
                "u"
            },
            new()
            {
                "b",
                "c",
                "d",
                "f",
                "g",
                "h"
            }
        };

        {
            var item = new ListOfListEntity {_id = ObjectId.GenerateNewId(), aListOfLists = expected};
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
        var collection = database.CreateCollection<IListOfListEntity>();
        collection.InsertOne(new IListOfListEntity {_id = ObjectId.GenerateNewId(), aListOfLists = []});

        using var db = SingleEntityDbContext.Create(collection);
        var actual = db.Entities.FirstOrDefault();

        Assert.NotNull(actual);
        Assert.NotNull(actual.aListOfLists);
        Assert.Empty(actual.aListOfLists);
    }

    [Fact]
    private void IListOfLists_write_read_with_items()
    {
        var collection = database.CreateCollection<IListOfListEntity>();
        var expected = new List<List<string>>
        {
            new()
            {
                "a",
                "e",
                "i",
                "o",
                "u"
            },
            new()
            {
                "b",
                "c",
                "d",
                "f",
                "g",
                "h"
            }
        };

        {
            var item = new IListOfListEntity {_id = ObjectId.GenerateNewId(), aListOfLists = expected};
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
        var collection = database.CreateCollection<ListSubclassOfListSubclassEntity>();
        collection.InsertOne(new ListSubclassOfListSubclassEntity {_id = ObjectId.GenerateNewId(), aListOfLists = []});

        using var db = SingleEntityDbContext.Create(collection);
        var actual = db.Entities.FirstOrDefault();

        Assert.NotNull(actual);
        Assert.NotNull(actual.aListOfLists);
        Assert.Empty(actual.aListOfLists);
    }

    [Fact]
    private void ListSubclassOfListSubclass_write_read_with_items()
    {
        var collection = database.CreateCollection<ListSubclassOfListSubclassEntity>();
        var expected = new ListSubclass<ListSubclass<string>>
        {
            new()
            {
                "a",
                "e",
                "i",
                "o",
                "u"
            },
            new()
            {
                "b",
                "c",
                "d",
                "f",
                "g",
                "h"
            }
        };

        {
            var item = new ListSubclassOfListSubclassEntity {_id = ObjectId.GenerateNewId(), aListOfLists = expected};
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
        var collection = database.CreateCollection<ReadOnlyCollectionEntity>();
        var expected = new ReadOnlyCollection<string>(["z", "x", "y", "1", "2", "3"]);

        {
            var item = new ReadOnlyCollectionEntity {_id = ObjectId.GenerateNewId(), aCollection = expected};
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
        var collection = database.CreateCollection<CollectionEntity>();
        var expected = new Collection<string>(["z", "x", "y", "1", "2", "3"]);

        {
            var item = new CollectionEntity {_id = ObjectId.GenerateNewId(), aCollection = expected};
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
        var collection = database.CreateCollection<ObservableCollectionEntity>();
        var expected = new ObservableCollection<string>(["z", "x", "y", "1", "2", "3"]);

        {
            var item = new ObservableCollectionEntity {_id = ObjectId.GenerateNewId(), aCollection = expected};
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
        var collection = database.CreateCollection<IReadOnlyListEntity>();
        var expected = new List<string>(["z", "x", "y", "1", "2", "3"]).AsReadOnly();

        {
            var item = new IReadOnlyListEntity {_id = ObjectId.GenerateNewId(), aList = expected};
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
        var collection = database.CreateCollection<IEnumerableEntity>();
        var expected = new List<string>(["z", "x", "y", "1", "2", "3"]).AsEnumerable();

        {
            var item = new IEnumerableEntity {_id = ObjectId.GenerateNewId(), anEnumerable = expected};
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
    [InlineData(typeof(int[]), new[] {-5, 0, 128, 10})]
    [InlineData(typeof(string[]), null)]
    [InlineData(typeof(string[]), new[] {"one", "two"})]
    [InlineData(typeof(List<int>), null)]
    [InlineData(typeof(List<int>), new[] {-5, 0, 128, 10})]
    [InlineData(typeof(IList<int>), null)]
    [InlineData(typeof(IList<int>), new[] {-5, 0, 128, 10})]
    [InlineData(typeof(IReadOnlyList<int>), null)]
    [InlineData(typeof(IReadOnlyList<int>), new[] {-5, 0, 128, 10})]
    [InlineData(typeof(ReadOnlyCollection<int>), null)]
    [InlineData(typeof(ReadOnlyCollection<int>), new[] {-5, 0, 128, 10})]
    [InlineData(typeof(Collection<int>), null)]
    [InlineData(typeof(Collection<int>), new[] {-100, 100, 0, 1234})]
    [InlineData(typeof(ObservableCollection<int>), null)]
    [InlineData(typeof(ObservableCollection<int>), new[] {-100, 100, 0, 1234})]
    [InlineData(typeof(ListSubclass<int>), null)]
    [InlineData(typeof(ListSubclass<int>), new[] {-5, 0, 128, 10})]
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

    [Fact]
    public void Type_mapping_test_list_nullable_int()
    {
        ClrTypeMappingTestImpl(new List<int?>(new int?[] {1, 2, 3, null, 4}));
    }

    private void ClrTypeMappingTestImpl<TValue>(TValue value)
    {
        var collection = database.CreateCollection<Entity<TValue>>(values: [typeof(TValue), value!]);
        collection.InsertOne(new Entity<TValue> {_id = ObjectId.GenerateNewId(), Value = value});

        using var db = SingleEntityDbContext.Create(collection);
        var actual = db.Entities.FirstOrDefault();

        Assert.NotNull(actual);
        Assert.Equal(value, actual.Value);
    }

    [Fact]
    public void Type_mapping_test_list_byte_array()
    {
        var inserted = new byte[8192];
        Random.Shared.NextBytes(inserted);
        var updated = new byte[8192];
        Random.Shared.NextBytes(inserted);

        var collection = database.CreateCollection<Entity<byte[]>>();
        collection.InsertOne(new Entity<byte[]> {_id = ObjectId.GenerateNewId(), Value = inserted});

        using (var db = SingleEntityDbContext.Create(collection))
        {
            var actual = db.Entities.FirstOrDefault();

            Assert.NotNull(actual);
            Assert.Equal(inserted, actual.Value);
            actual.Value = updated;
            db.SaveChanges();
        }

        using (var db = SingleEntityDbContext.Create(collection))
        {
            var actual = db.Entities.FirstOrDefault();

            Assert.NotNull(actual);
            Assert.Equal(updated, actual.Value);
        }
    }

    class CustomStructEntity : IdEntity
    {
        public MonetaryAmount money { get; set; }
    }

    record struct MonetaryAmount
    {
        public string Currency { get; set; }
        public decimal Amount  { get; set; }
    }

    [Fact]
    public void Custom_struct_read_update()
    {
        var collection = database.CreateCollection<CustomStructEntity>();

        var expected = new MonetaryAmount { Currency = "USD", Amount = 1.99m };
        collection.InsertOne(new CustomStructEntity {_id = ObjectId.GenerateNewId(), money = expected});

        using (var db = SingleEntityDbContext.Create(collection))
        {
            var actual = db.Entities.FirstOrDefault();

            Assert.NotNull(actual);
            Assert.Equal(expected, actual.money);
            actual.money = new() { Currency = "USD", Amount = 2.99m };
            db.SaveChanges();
        }

        using (var db = SingleEntityDbContext.Create(collection))
        {
            var actual = db.Entities.FirstOrDefault();

            Assert.NotNull(actual);
            Assert.Equal(new MonetaryAmount { Currency = "USD", Amount = 2.99m }, actual.money);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadOnlyMemory_read_update(bool async)
    {
        var collection = database.CreateCollection<ReadOnlyMemoryEntity>(values: [async]);

        collection.InsertOne(new ReadOnlyMemoryEntity
        {
            ReadOnlyMemoryBytes = new([1, 2, 3, 4]),
            ReadOnlyMemorySBytes = new([-1, -2, -3, -4]),
            ReadOnlyMemoryInts = new([1, 2, 3, 4]),
            ReadOnlyMemoryDoubles = new([1.1, 2.2, 3.3, 4.4]),
            ReadOnlyMemoryDecimals = new([1.1m, 2.2m, 3.3m, 4.4m]),
            ReadOnlyMemoryFloats = new([1.1f, 2.2f, 3.3f, 4.4f])
        });

        using (var db = SingleEntityDbContext.Create(collection))
        {
            var actual = async ? await db.Entities.FirstOrDefaultAsync() : db.Entities.FirstOrDefault();

            Assert.NotNull(actual);
            Assert.Equal(new ReadOnlyMemory<byte>([1, 2, 3, 4]), actual.ReadOnlyMemoryBytes);
            Assert.Equal(new ReadOnlyMemory<sbyte>([-1, -2, -3, -4]), actual.ReadOnlyMemorySBytes);
            Assert.Equal(new ReadOnlyMemory<int>([1, 2, 3, 4]), actual.ReadOnlyMemoryInts);
            Assert.Equal(new ReadOnlyMemory<double>([1.1, 2.2, 3.3, 4.4]), actual.ReadOnlyMemoryDoubles);
            Assert.Equal(new ReadOnlyMemory<decimal>([1.1m, 2.2m, 3.3m, 4.4m]), actual.ReadOnlyMemoryDecimals);
            Assert.Equal(new ReadOnlyMemory<float>([1.1f, 2.2f, 3.3f, 4.4f]), actual.ReadOnlyMemoryFloats);

            actual.ReadOnlyMemoryBytes = new([4, 3, 2, 1]);
            actual.ReadOnlyMemorySBytes = new([-4, -3, -2, -1]);
            actual.ReadOnlyMemoryInts = new([4, 3, 2, 1]);
            actual.ReadOnlyMemoryDoubles = new([4.1, 3.2, 2.3, 1.4]);
            actual.ReadOnlyMemoryDecimals = new([4.1m, 3.2m, 2.3m, 1.4m]);
            actual.ReadOnlyMemoryFloats = new([4.1f, 3.2f, 2.3f, 1.4f]);

            Assert.Equal(1, async ? await db.SaveChangesAsync() : db.SaveChanges());
        }

        // Validate storage as correct BSON
        var document = database.GetCollection<BsonDocument>(collection.CollectionNamespace).AsQueryable().First().ToJson();

        Assert.Contains(
            """
            "ReadOnlyMemoryBytes" : { "$binary" : { "base64" : "BAMCAQ==", "subType" : "00" } }
            """, document);

        Assert.Contains(
            """
            "ReadOnlyMemorySBytes" : [-4, -3, -2, -1]
            """, document);

        Assert.Contains(
            """
            "ReadOnlyMemoryInts" : [4, 3, 2, 1]
            """, document);

        Assert.Contains(
            """
            "ReadOnlyMemoryDoubles" : [4.0999999999999996, 3.2000000000000002, 2.2999999999999998, 1.3999999999999999]
            """, document);

        Assert.Contains(
            """
            "ReadOnlyMemoryDecimals" : [{ "$numberDecimal" : "4.1" }, { "$numberDecimal" : "3.2" }, { "$numberDecimal" : "2.3" }, { "$numberDecimal" : "1.4" }]
            """, document);

        Assert.Contains(
            """
            "ReadOnlyMemoryFloats" : [4.0999999046325684, 3.2000000476837158, 2.2999999523162842, 1.3999999761581421] }
            """, document);

        using (var db = SingleEntityDbContext.Create(collection))
        {
            var actual = async ? await db.Entities.FirstOrDefaultAsync() : db.Entities.FirstOrDefault();

            Assert.NotNull(actual);
            Assert.Equal(new ReadOnlyMemory<byte>([4, 3, 2, 1]), actual.ReadOnlyMemoryBytes);
            Assert.Equal(new ReadOnlyMemory<sbyte>([-4, -3, -2, -1]), actual.ReadOnlyMemorySBytes);
            Assert.Equal(new ReadOnlyMemory<int>([4, 3, 2, 1]), actual.ReadOnlyMemoryInts);
            Assert.Equal(new ReadOnlyMemory<double>([4.1, 3.2, 2.3, 1.4]), actual.ReadOnlyMemoryDoubles);
            Assert.Equal(new ReadOnlyMemory<decimal>([4.1m, 3.2m, 2.3m, 1.4m]), actual.ReadOnlyMemoryDecimals);
            Assert.Equal(new ReadOnlyMemory<float>([4.1f, 3.2f, 2.3f, 1.4f]), actual.ReadOnlyMemoryFloats);
        }

        // Verify that the driver can still read these back after they have been updated by EF Core
        var fromDriver = collection.AsQueryable().FirstOrDefault();

        Assert.NotNull(fromDriver);
        Assert.Equal(new ReadOnlyMemory<byte>([4, 3, 2, 1]), fromDriver.ReadOnlyMemoryBytes);
        Assert.Equal(new ReadOnlyMemory<sbyte>([-4, -3, -2, -1]), fromDriver.ReadOnlyMemorySBytes);
        Assert.Equal(new ReadOnlyMemory<int>([4, 3, 2, 1]), fromDriver.ReadOnlyMemoryInts);
        Assert.Equal(new ReadOnlyMemory<double>([4.1, 3.2, 2.3, 1.4]), fromDriver.ReadOnlyMemoryDoubles);
        Assert.Equal(new ReadOnlyMemory<decimal>([4.1m, 3.2m, 2.3m, 1.4m]), fromDriver.ReadOnlyMemoryDecimals);
    }

    class ReadOnlyMemoryEntity : IdEntity
    {
        public ReadOnlyMemory<byte> ReadOnlyMemoryBytes { get; set; }
        public ReadOnlyMemory<sbyte> ReadOnlyMemorySBytes { get; set; }
        public ReadOnlyMemory<int> ReadOnlyMemoryInts { get; set; }
        public ReadOnlyMemory<double> ReadOnlyMemoryDoubles { get; set; }
        public ReadOnlyMemory<decimal> ReadOnlyMemoryDecimals { get; set; }
        public ReadOnlyMemory<float> ReadOnlyMemoryFloats { get; set; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Memory_read_update(bool async)
    {
        var collection = database.CreateCollection<MemoryEntity>(values: [async]);

        collection.InsertOne(new MemoryEntity
        {
            MemoryBytes = new([1, 2, 3, 4]),
            MemorySBytes = new([-1, -2, -3, -4]),
            MemoryInts = new([1, 2, 3, 4]),
            MemoryDoubles = new([1.1, 2.2, 3.3, 4.4]),
            MemoryDecimals = new([1.1m, 2.2m, 3.3m, 4.4m]),
            MemoryFloats = new([1.1f, 2.2f, 3.3f, 4.4f])
        });

        using (var db = SingleEntityDbContext.Create(collection))
        {
            var actual = async ? await db.Entities.FirstOrDefaultAsync() : db.Entities.FirstOrDefault();

            Assert.NotNull(actual);
            Assert.Equal(new Memory<byte>([1, 2, 3, 4]), actual.MemoryBytes);
            Assert.Equal(new Memory<sbyte>([-1, -2, -3, -4]), actual.MemorySBytes);
            Assert.Equal(new Memory<int>([1, 2, 3, 4]), actual.MemoryInts);
            Assert.Equal(new Memory<double>([1.1, 2.2, 3.3, 4.4]), actual.MemoryDoubles);
            Assert.Equal(new Memory<decimal>([1.1m, 2.2m, 3.3m, 4.4m]), actual.MemoryDecimals);
            Assert.Equal(new Memory<float>([1.1f, 2.2f, 3.3f, 4.4f]), actual.MemoryFloats);

            actual.MemoryBytes = new([4, 3, 2, 1]);
            actual.MemorySBytes = new([-4, -3, -2, -1]);
            actual.MemoryInts = new([4, 3, 2, 1]);
            actual.MemoryDoubles = new([4.1, 3.2, 2.3, 1.4]);
            actual.MemoryDecimals = new([4.1m, 3.2m, 2.3m, 1.4m]);
            actual.MemoryFloats = new([4.1f, 3.2f, 2.3f, 1.4f]);

            Assert.Equal(1, async ? await db.SaveChangesAsync() : db.SaveChanges());
        }

        // Validate storage as correct BSON
        var document = database.GetCollection<BsonDocument>(collection.CollectionNamespace).AsQueryable().First().ToJson();

        Assert.Contains(
            """
            "MemoryBytes" : { "$binary" : { "base64" : "BAMCAQ==", "subType" : "00" } }
            """, document);

        Assert.Contains(
            """
            "MemorySBytes" : [-4, -3, -2, -1]
            """, document);

        Assert.Contains(
            """
            "MemoryInts" : [4, 3, 2, 1]
            """, document);

        Assert.Contains(
            """
            "MemoryDoubles" : [4.0999999999999996, 3.2000000000000002, 2.2999999999999998, 1.3999999999999999]
            """, document);

        Assert.Contains(
            """
            "MemoryDecimals" : [{ "$numberDecimal" : "4.1" }, { "$numberDecimal" : "3.2" }, { "$numberDecimal" : "2.3" }, { "$numberDecimal" : "1.4" }]
            """, document);

        Assert.Contains(
            """
            "MemoryFloats" : [4.0999999046325684, 3.2000000476837158, 2.2999999523162842, 1.3999999761581421] }
            """, document);

        using (var db = SingleEntityDbContext.Create(collection))
        {
            var actual = async ? await db.Entities.FirstOrDefaultAsync() : db.Entities.FirstOrDefault();

            Assert.NotNull(actual);
            Assert.Equal(new Memory<byte>([4, 3, 2, 1]), actual.MemoryBytes);
            Assert.Equal(new Memory<sbyte>([-4, -3, -2, -1]), actual.MemorySBytes);
            Assert.Equal(new Memory<int>([4, 3, 2, 1]), actual.MemoryInts);
            Assert.Equal(new Memory<double>([4.1, 3.2, 2.3, 1.4]), actual.MemoryDoubles);
            Assert.Equal(new Memory<decimal>([4.1m, 3.2m, 2.3m, 1.4m]), actual.MemoryDecimals);
            Assert.Equal(new Memory<float>([4.1f, 3.2f, 2.3f, 1.4f]), actual.MemoryFloats);
        }

        // Verify that the driver can still read these back after they have been updated by EF Core
        var fromDriver = collection.AsQueryable().FirstOrDefault();

        Assert.NotNull(fromDriver);
        Assert.Equal(new Memory<byte>([4, 3, 2, 1]), fromDriver.MemoryBytes);
        Assert.Equal(new Memory<sbyte>([-4, -3, -2, -1]), fromDriver.MemorySBytes);
        Assert.Equal(new Memory<int>([4, 3, 2, 1]), fromDriver.MemoryInts);
        Assert.Equal(new Memory<double>([4.1, 3.2, 2.3, 1.4]), fromDriver.MemoryDoubles);
        Assert.Equal(new Memory<decimal>([4.1m, 3.2m, 2.3m, 1.4m]), fromDriver.MemoryDecimals);
    }

    class MemoryEntity : IdEntity
    {
        public Memory<byte> MemoryBytes { get; set; }
        public Memory<sbyte> MemorySBytes { get; set; }
        public Memory<int> MemoryInts { get; set; }
        public Memory<double> MemoryDoubles { get; set; }
        public Memory<decimal> MemoryDecimals { get; set; }
        public Memory<float> MemoryFloats { get; set; }
    }

    [AtlasTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BinaryVectorPackedBit_read_update(bool async)
    {
        var collection = database.CreateCollection<BinaryVectorPackedBitEntity>(values: [async]);

        collection.InsertOne(new BinaryVectorPackedBitEntity
        {
            Bytes = [1, 2, 3, 4],
            MemoryBytes = new([1, 2, 3, 4]),
            ReadOnlyMemoryBytes = new([1, 2, 3, 4]),
            BinaryVector = new(new([1, 2, 3, 4]), 2)
        });

        using (var db = SingleEntityDbContext.Create(collection))
        {
            var actual = async ? await db.Entities.FirstOrDefaultAsync() : db.Entities.FirstOrDefault();

            Assert.NotNull(actual);
            Assert.Equal([1, 2, 3, 4], actual.Bytes);
            Assert.Equal(new Memory<byte>([1, 2, 3, 4]), actual.MemoryBytes);
            Assert.Equal(new ReadOnlyMemory<byte>([1, 2, 3, 4]), actual.ReadOnlyMemoryBytes);
            Assert.Equal(new([1, 2, 3, 4]), actual.BinaryVector.Data);
            Assert.Equal(2, actual.BinaryVector.Padding);

            actual.Bytes = [4, 3, 2, 1];
            actual.MemoryBytes = new([4, 3, 2, 1]);
            actual.ReadOnlyMemoryBytes = new([4, 3, 2, 1]);
            actual.BinaryVector = new(new([4, 3, 2, 1]), 0);

            Assert.Equal(1, async ? await db.SaveChangesAsync() : db.SaveChanges());
        }

        // Validate storage as correct BSON
        var document = database.GetCollection<BsonDocument>(collection.CollectionNamespace).AsQueryable().First().ToJson();

        Assert.Contains(
            """
            "Bytes" : { "$binary" : { "base64" : "EAAEAwIB", "subType" : "09" } }
            """, document);

        Assert.Contains(
            """
            "MemoryBytes" : { "$binary" : { "base64" : "EAAEAwIB", "subType" : "09" } }
            """, document);

        Assert.Contains(
            """
            "ReadOnlyMemoryBytes" : { "$binary" : { "base64" : "EAAEAwIB", "subType" : "09" } }
            """, document);

        Assert.Contains(
            """
            "BinaryVector" : { "$binary" : { "base64" : "EAAEAwIB", "subType" : "09" } }
            """, document);

        using (var db = SingleEntityDbContext.Create(collection))
        {
            var actual = async ? await db.Entities.FirstOrDefaultAsync() : db.Entities.FirstOrDefault();

            Assert.NotNull(actual);
            Assert.Equal([4, 3, 2, 1], actual.Bytes);
            Assert.Equal(new Memory<byte>([4, 3, 2, 1]), actual.MemoryBytes);
            Assert.Equal(new ReadOnlyMemory<byte>([4, 3, 2, 1]), actual.ReadOnlyMemoryBytes);
            Assert.Equal(new([4, 3, 2, 1]), actual.BinaryVector.Data);
            Assert.Equal(0, actual.BinaryVector.Padding);
        }

        // Verify that the driver can still read these back after they have been updated by EF Core
        var fromDriver = collection.AsQueryable().FirstOrDefault();

        Assert.NotNull(fromDriver);
        Assert.Equal([4, 3, 2, 1], fromDriver.Bytes);
        Assert.Equal(new Memory<byte>([4, 3, 2, 1]), fromDriver.MemoryBytes);
        Assert.Equal(new ReadOnlyMemory<byte>([4, 3, 2, 1]), fromDriver.ReadOnlyMemoryBytes);
        Assert.Equal(new([4, 3, 2, 1]), fromDriver.BinaryVector.Data);
        Assert.Equal(0, fromDriver.BinaryVector.Padding);
    }

    class BinaryVectorPackedBitEntity : IdEntity
    {
        [BinaryVector(BinaryVectorDataType.PackedBit)]
        public byte[] Bytes { get; set; }

        [BinaryVector(BinaryVectorDataType.PackedBit)]
        public Memory<byte> MemoryBytes { get; set; }

        [BinaryVector(BinaryVectorDataType.PackedBit)]
        public ReadOnlyMemory<byte> ReadOnlyMemoryBytes { get; set; }

        public BinaryVectorPackedBit BinaryVector { get; set; }
    }

    [AtlasTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BinaryVectorFloat32_read_update(bool async)
    {
        var collection = database.CreateCollection<BinaryVectorFloat32Entity>(values: [async]);

        collection.InsertOne(new BinaryVectorFloat32Entity
        {
            Floats = [1.1f, 2.2f, 3.3f, 4.4f],
            MemoryFloats = new([1.1f, 2.2f, 3.3f, 4.4f]),
            ReadOnlyMemoryFloats = new([1.1f, 2.2f, 3.3f, 4.4f]),
            BinaryVector = new(new([1.1f, 2.2f, 3.3f, 4.4f]))
        });

        using (var db = SingleEntityDbContext.Create(collection))
        {
            var actual = async ? await db.Entities.FirstOrDefaultAsync() : db.Entities.FirstOrDefault();

            Assert.NotNull(actual);
            Assert.Equal([1.1f, 2.2f, 3.3f, 4.4f], actual.Floats);
            Assert.Equal(new Memory<float>([1.1f, 2.2f, 3.3f, 4.4f]), actual.MemoryFloats);
            Assert.Equal(new ReadOnlyMemory<float>([1.1f, 2.2f, 3.3f, 4.4f]), actual.ReadOnlyMemoryFloats);
            Assert.Equal(new([1.1f, 2.2f, 3.3f, 4.4f]), actual.BinaryVector.Data);

            actual.Floats = [4.1f, 3.2f, 2.3f, 1.4f];
            actual.MemoryFloats = new([4.1f, 3.2f, 2.3f, 1.4f]);
            actual.ReadOnlyMemoryFloats = new([4.1f, 3.2f, 2.3f, 1.4f]);
            actual.BinaryVector = new(new([4.1f, 3.2f, 2.3f, 1.4f]));

            Assert.Equal(1, async ? await db.SaveChangesAsync() : db.SaveChanges());
        }

        // Validate storage as correct BSON
        var document = database.GetCollection<BsonDocument>(collection.CollectionNamespace).AsQueryable().First().ToJson();

        Assert.Contains(
            """
            "Floats" : { "$binary" : { "base64" : "JwAzM4NAzcxMQDMzE0AzM7M/", "subType" : "09" } }
            """, document);

        Assert.Contains(
            """
            "MemoryFloats" : { "$binary" : { "base64" : "JwAzM4NAzcxMQDMzE0AzM7M/", "subType" : "09" } }
            """, document);

        Assert.Contains(
            """
            "ReadOnlyMemoryFloats" : { "$binary" : { "base64" : "JwAzM4NAzcxMQDMzE0AzM7M/", "subType" : "09" } }
            """, document);

        Assert.Contains(
            """
            "BinaryVector" : { "$binary" : { "base64" : "JwAzM4NAzcxMQDMzE0AzM7M/", "subType" : "09" } }
            """, document);

        using (var db = SingleEntityDbContext.Create(collection))
        {
            var actual = async ? await db.Entities.FirstOrDefaultAsync() : db.Entities.FirstOrDefault();

            Assert.NotNull(actual);
            Assert.Equal([4.1f, 3.2f, 2.3f, 1.4f], actual.Floats);
            Assert.Equal(new Memory<float>([4.1f, 3.2f, 2.3f, 1.4f]), actual.MemoryFloats);
            Assert.Equal(new ReadOnlyMemory<float>([4.1f, 3.2f, 2.3f, 1.4f]), actual.ReadOnlyMemoryFloats);
            Assert.Equal(new([4.1f, 3.2f, 2.3f, 1.4f]), actual.BinaryVector.Data);
        }

        // Verify that the driver can still read these back after they have been updated by EF Core
        var fromDriver = collection.AsQueryable().FirstOrDefault();

        Assert.NotNull(fromDriver);
        Assert.Equal([4.1f, 3.2f, 2.3f, 1.4f], fromDriver.Floats);
        Assert.Equal(new Memory<float>([4.1f, 3.2f, 2.3f, 1.4f]), fromDriver.MemoryFloats);
        Assert.Equal(new ReadOnlyMemory<float>([4.1f, 3.2f, 2.3f, 1.4f]), fromDriver.ReadOnlyMemoryFloats);
        Assert.Equal(new([4.1f, 3.2f, 2.3f, 1.4f]), fromDriver.BinaryVector.Data);
    }

    class BinaryVectorFloat32Entity : IdEntity
    {
        [BinaryVector(BinaryVectorDataType.Float32)]
        public float[] Floats { get; set; }

        [BinaryVector(BinaryVectorDataType.Float32)]
        public Memory<float> MemoryFloats { get; set; }

        [BinaryVector(BinaryVectorDataType.Float32)]
        public ReadOnlyMemory<float> ReadOnlyMemoryFloats { get; set; }

        public BinaryVectorFloat32 BinaryVector { get; set; }
    }

    [AtlasTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BinaryVectorDataType_Int8_read_update(bool async)
    {
        var collection = database.CreateCollection<BinaryVectorInt8Entity>(values: [async]);

        collection.InsertOne(new BinaryVectorInt8Entity
        {
            SBytes = [1, -2, 3, -4],
            MemorySBytes = new([1, -2, 3, -4]),
            ReadOnlyMemorySBytes = new([1, -2, 3, -4]),
            BinaryVector = new(new([1, -2, 3, -4]))
        });

        using (var db = SingleEntityDbContext.Create(collection))
        {
            var actual = async ? await db.Entities.FirstOrDefaultAsync() : db.Entities.FirstOrDefault();

            Assert.NotNull(actual);
            Assert.Equal([1, -2, 3, -4], actual.SBytes);
            Assert.Equal(new Memory<sbyte>([1, -2, 3, -4]), actual.MemorySBytes);
            Assert.Equal(new ReadOnlyMemory<sbyte>([1, -2, 3, -4]), actual.ReadOnlyMemorySBytes);
            Assert.Equal(new([1, -2, 3, -4]), actual.BinaryVector.Data);

            actual.SBytes = [-4, 3, -2, 1];
            actual.MemorySBytes = new([-4, 3, -2, 1]);
            actual.ReadOnlyMemorySBytes = new([-4, 3, -2, 1]);
            actual.BinaryVector = new(new([-4, 3, -2, 1]));

            Assert.Equal(1, async ? await db.SaveChangesAsync() : db.SaveChanges());
        }

        // Validate storage as correct BSON
        var document = database.GetCollection<BsonDocument>(collection.CollectionNamespace).AsQueryable().First().ToJson();

        Assert.Contains(
            """
            "SBytes" : { "$binary" : { "base64" : "AwD8A/4B", "subType" : "09" } }
            """, document);

        Assert.Contains(
            """
            "MemorySBytes" : { "$binary" : { "base64" : "AwD8A/4B", "subType" : "09" } }
            """, document);

        Assert.Contains(
            """
            "ReadOnlyMemorySBytes" : { "$binary" : { "base64" : "AwD8A/4B", "subType" : "09" } }
            """, document);

        Assert.Contains(
            """
            "BinaryVector" : { "$binary" : { "base64" : "AwD8A/4B", "subType" : "09" } }
            """, document);

        using (var db = SingleEntityDbContext.Create(collection))
        {
            var actual = async ? await db.Entities.FirstOrDefaultAsync() : db.Entities.FirstOrDefault();

            Assert.NotNull(actual);
            Assert.Equal([-4, 3, -2, 1], actual.SBytes);
            Assert.Equal(new Memory<sbyte>([-4, 3, -2, 1]), actual.MemorySBytes);
            Assert.Equal(new ReadOnlyMemory<sbyte>([-4, 3, -2, 1]), actual.ReadOnlyMemorySBytes);
            Assert.Equal(new([-4, 3, -2, 1]), actual.BinaryVector.Data);
        }

        // Verify that the driver can still read these back after they have been updated by EF Core
        var fromDriver = collection.AsQueryable().FirstOrDefault();

        Assert.Equal([-4, 3, -2, 1], fromDriver.SBytes);
        Assert.Equal(new Memory<sbyte>([-4, 3, -2, 1]), fromDriver.MemorySBytes);
        Assert.Equal(new ReadOnlyMemory<sbyte>([-4, 3, -2, 1]), fromDriver.ReadOnlyMemorySBytes);
        Assert.Equal(new([-4, 3, -2, 1]), fromDriver.BinaryVector.Data);
    }

    class BinaryVectorInt8Entity : IdEntity
    {
        [BinaryVector(BinaryVectorDataType.Int8)]
        public sbyte[] SBytes { get; set; }

        [BinaryVector(BinaryVectorDataType.Int8)]
        public Memory<sbyte> MemorySBytes { get; set; }

        [BinaryVector(BinaryVectorDataType.Int8)]
        public ReadOnlyMemory<sbyte> ReadOnlyMemorySBytes { get; set; }

        public BinaryVectorInt8 BinaryVector { get; set; }
    }

    [AtlasTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BinaryVectorPackedBit_via_model_builder_read_update(bool async)
    {
        var collection = database.CreateCollection<BinaryVectorPackedBitEntityNoAttributes>(values: [async]);

        using (var db = CreateContext())
        {
            db.Add(new BinaryVectorPackedBitEntityNoAttributes
                {
                    Bytes = [1, 2, 3, 4],
                    MemoryBytes = new([1, 2, 3, 4]),
                    ReadOnlyMemoryBytes = new([1, 2, 3, 4]),
                });

            Assert.Equal(1, async ? await db.SaveChangesAsync() : db.SaveChanges());
        }

        using (var db = CreateContext())
        {
            var actual = async ? await db.Entities.FirstOrDefaultAsync() : db.Entities.FirstOrDefault();

            Assert.NotNull(actual);
            Assert.Equal([1, 2, 3, 4], actual.Bytes);
            Assert.Equal(new Memory<byte>([1, 2, 3, 4]), actual.MemoryBytes);
            Assert.Equal(new ReadOnlyMemory<byte>([1, 2, 3, 4]), actual.ReadOnlyMemoryBytes);

            actual.Bytes = [4, 3, 2, 1];
            actual.MemoryBytes = new([4, 3, 2, 1]);
            actual.ReadOnlyMemoryBytes = new([4, 3, 2, 1]);

            Assert.Equal(1, async ? await db.SaveChangesAsync() : db.SaveChanges());
        }

        // Validate storage as correct BSON
        var document = database.GetCollection<BsonDocument>(collection.CollectionNamespace).AsQueryable().First().ToJson();

        Assert.Contains(
            """
            "Bytes" : { "$binary" : { "base64" : "EAAEAwIB", "subType" : "09" } }
            """, document);

        Assert.Contains(
            """
            "MemoryBytes" : { "$binary" : { "base64" : "EAAEAwIB", "subType" : "09" } }
            """, document);

        Assert.Contains(
            """
            "ReadOnlyMemoryBytes" : { "$binary" : { "base64" : "EAAEAwIB", "subType" : "09" } }
            """, document);

        using (var db = CreateContext())
        {
            var actual = async ? await db.Entities.FirstOrDefaultAsync() : db.Entities.FirstOrDefault();

            Assert.NotNull(actual);
            Assert.Equal([4, 3, 2, 1], actual.Bytes);
            Assert.Equal(new Memory<byte>([4, 3, 2, 1]), actual.MemoryBytes);
            Assert.Equal(new ReadOnlyMemory<byte>([4, 3, 2, 1]), actual.ReadOnlyMemoryBytes);
        }

        SingleEntityDbContext<BinaryVectorPackedBitEntityNoAttributes> CreateContext()
            => SingleEntityDbContext.Create(collection, b =>
            {
                b.Entity<BinaryVectorPackedBitEntityNoAttributes>(b =>
                {
                    b.Property(e => e.Bytes).HasBinaryVectorDataType(BinaryVectorDataType.PackedBit);
                    b.Property(e => e.MemoryBytes).HasBinaryVectorDataType(BinaryVectorDataType.PackedBit);
                    b.Property(e => e.ReadOnlyMemoryBytes).HasBinaryVectorDataType(BinaryVectorDataType.PackedBit);
                });
            });
    }

    class BinaryVectorPackedBitEntityNoAttributes : IdEntity
    {
        public byte[] Bytes { get; set; }
        public Memory<byte> MemoryBytes { get; set; }
        public ReadOnlyMemory<byte> ReadOnlyMemoryBytes { get; set; }
    }

    [AtlasTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BinaryVectorFloat32_via_model_builder_read_update(bool async)
    {
        var collection = database.CreateCollection<BinaryVectorFloat32EntityNoAttributes>(values: [async]);

        using (var db = CreateContext())
        {
            db.Add(new BinaryVectorFloat32EntityNoAttributes
            {
                Floats = [1.1f, 2.2f, 3.3f, 4.4f],
                MemoryFloats = new([1.1f, 2.2f, 3.3f, 4.4f]),
                ReadOnlyMemoryFloats = new([1.1f, 2.2f, 3.3f, 4.4f]),
            });

            Assert.Equal(1, async ? await db.SaveChangesAsync() : db.SaveChanges());
        }

        using (var db = CreateContext())
        {
            var actual = async ? await db.Entities.FirstOrDefaultAsync() : db.Entities.FirstOrDefault();

            Assert.NotNull(actual);
            Assert.Equal([1.1f, 2.2f, 3.3f, 4.4f], actual.Floats);
            Assert.Equal(new Memory<float>([1.1f, 2.2f, 3.3f, 4.4f]), actual.MemoryFloats);
            Assert.Equal(new ReadOnlyMemory<float>([1.1f, 2.2f, 3.3f, 4.4f]), actual.ReadOnlyMemoryFloats);

            actual.Floats = [4.1f, 3.2f, 2.3f, 1.4f];
            actual.MemoryFloats = new([4.1f, 3.2f, 2.3f, 1.4f]);
            actual.ReadOnlyMemoryFloats = new([4.1f, 3.2f, 2.3f, 1.4f]);

            Assert.Equal(1, async ? await db.SaveChangesAsync() : db.SaveChanges());
        }

        // Validate storage as correct BSON
        var document = database.GetCollection<BsonDocument>(collection.CollectionNamespace).AsQueryable().First().ToJson();

        Assert.Contains(
            """
            "Floats" : { "$binary" : { "base64" : "JwAzM4NAzcxMQDMzE0AzM7M/", "subType" : "09" } }
            """, document);

        Assert.Contains(
            """
            "MemoryFloats" : { "$binary" : { "base64" : "JwAzM4NAzcxMQDMzE0AzM7M/", "subType" : "09" } }
            """, document);

        Assert.Contains(
            """
            "ReadOnlyMemoryFloats" : { "$binary" : { "base64" : "JwAzM4NAzcxMQDMzE0AzM7M/", "subType" : "09" } }
            """, document);

        using (var db = CreateContext())
        {
            var actual = async ? await db.Entities.FirstOrDefaultAsync() : db.Entities.FirstOrDefault();

            Assert.NotNull(actual);
            Assert.Equal([4.1f, 3.2f, 2.3f, 1.4f], actual.Floats);
            Assert.Equal(new Memory<float>([4.1f, 3.2f, 2.3f, 1.4f]), actual.MemoryFloats);
            Assert.Equal(new ReadOnlyMemory<float>([4.1f, 3.2f, 2.3f, 1.4f]), actual.ReadOnlyMemoryFloats);
        }

        SingleEntityDbContext<BinaryVectorFloat32EntityNoAttributes> CreateContext()
            => SingleEntityDbContext.Create(collection, b =>
            {
                b.Entity<BinaryVectorFloat32EntityNoAttributes>(b =>
                {
                    b.Property(e => e.Floats).HasBinaryVectorDataType(BinaryVectorDataType.Float32);
                    b.Property(e => e.MemoryFloats).HasBinaryVectorDataType(BinaryVectorDataType.Float32);
                    b.Property(e => e.ReadOnlyMemoryFloats).HasBinaryVectorDataType(BinaryVectorDataType.Float32);
                });
            });
    }

    class BinaryVectorFloat32EntityNoAttributes : IdEntity
    {
        public float[] Floats { get; set; }
        public Memory<float> MemoryFloats { get; set; }
        public ReadOnlyMemory<float> ReadOnlyMemoryFloats { get; set; }
    }

    [AtlasTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BinaryVectorDataType_via_model_builder_Int8_read_update(bool async)
    {
        var collection = database.CreateCollection<BinaryVectorInt8EntityNoAttributes>(values: [async]);

        using (var db = CreateContext())
        {
            db.Add(new BinaryVectorInt8EntityNoAttributes
            {
                SBytes = [1, -2, 3, -4],
                MemorySBytes = new([1, -2, 3, -4]),
                ReadOnlyMemorySBytes = new([1, -2, 3, -4]),
            });

            Assert.Equal(1, async ? await db.SaveChangesAsync() : db.SaveChanges());
        }

        using (var db = CreateContext())
        {
            var actual = async ? await db.Entities.FirstOrDefaultAsync() : db.Entities.FirstOrDefault();

            Assert.NotNull(actual);
            Assert.Equal([1, -2, 3, -4], actual.SBytes);
            Assert.Equal(new Memory<sbyte>([1, -2, 3, -4]), actual.MemorySBytes);
            Assert.Equal(new ReadOnlyMemory<sbyte>([1, -2, 3, -4]), actual.ReadOnlyMemorySBytes);

            actual.SBytes = [-4, 3, -2, 1];
            actual.MemorySBytes = new([-4, 3, -2, 1]);
            actual.ReadOnlyMemorySBytes = new([-4, 3, -2, 1]);

            Assert.Equal(1, async ? await db.SaveChangesAsync() : db.SaveChanges());
        }

        // Validate storage as correct BSON
        var document = database.GetCollection<BsonDocument>(collection.CollectionNamespace).AsQueryable().First().ToJson();

        Assert.Contains(
            """
            "SBytes" : { "$binary" : { "base64" : "AwD8A/4B", "subType" : "09" } }
            """, document);

        Assert.Contains(
            """
            "MemorySBytes" : { "$binary" : { "base64" : "AwD8A/4B", "subType" : "09" } }
            """, document);

        Assert.Contains(
            """
            "ReadOnlyMemorySBytes" : { "$binary" : { "base64" : "AwD8A/4B", "subType" : "09" } }
            """, document);

        using (var db = CreateContext())
        {
            var actual = async ? await db.Entities.FirstOrDefaultAsync() : db.Entities.FirstOrDefault();

            Assert.NotNull(actual);
            Assert.Equal([-4, 3, -2, 1], actual.SBytes);
            Assert.Equal(new Memory<sbyte>([-4, 3, -2, 1]), actual.MemorySBytes);
            Assert.Equal(new ReadOnlyMemory<sbyte>([-4, 3, -2, 1]), actual.ReadOnlyMemorySBytes);
        }

        SingleEntityDbContext<BinaryVectorInt8EntityNoAttributes> CreateContext()
            => SingleEntityDbContext.Create(collection, b =>
            {
                b.Entity<BinaryVectorInt8EntityNoAttributes>(b =>
                {
                    b.Property(e => e.SBytes).HasBinaryVectorDataType(BinaryVectorDataType.Int8);
                    b.Property(e => e.MemorySBytes).HasBinaryVectorDataType(BinaryVectorDataType.Int8);
                    b.Property(e => e.ReadOnlyMemorySBytes).HasBinaryVectorDataType(BinaryVectorDataType.Int8);
                });
            });
    }

    class BinaryVectorInt8EntityNoAttributes : IdEntity
    {
        public sbyte[] SBytes { get; set; }
        public Memory<sbyte> MemorySBytes { get; set; }
        public ReadOnlyMemory<sbyte> ReadOnlyMemorySBytes { get; set; }
    }
}
