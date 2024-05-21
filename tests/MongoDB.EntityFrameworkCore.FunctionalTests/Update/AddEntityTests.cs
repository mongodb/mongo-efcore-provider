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
using MongoDB.Driver;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Update;

[XUnitCollection("UpdateTests")]
public class AddEntityTests : IClassFixture<TemporaryDatabaseFixture>
{
    private static readonly Random Random = new();
    private readonly TemporaryDatabaseFixture _tempDatabase;

    public AddEntityTests(TemporaryDatabaseFixture tempDatabase) => _tempDatabase = tempDatabase;

    private class Entity<TValue>
    {
        public ObjectId _id { get; set; }
        public TValue Value { get; set; }
    }

    private class SimpleEntity
    {
        public ObjectId _id { get; set; }
        public string name { get; set; }
    }

    private class NumericTypesEntity
    {
        public int _id { get; set; }
        public decimal aDecimal { get; set; }
        public float aSingle { get; set; }
        public double aDouble { get; set; }
        public byte aByte { get; set; }
        public short anInt16 { get; set; }
        public int anInt32 { get; set; }
        public long anInt64 { get; set; }
    }

    private class OtherClrTypeEntity
    {
        public Guid _id { get; set; }
        public string aString { get; set; }
        public char aChar { get; set; }
        public DateTime aDateTime { get; set; }
        public Guid aGuid { get; set; }
    }

    private class MongoSpecificTypeEntity
    {
        public ObjectId _id { get; set; }
        public Decimal128 aDecimal128 { get; set; }
    }

    [Fact]
    public void Add_simple_entity_with_generated_ObjectId()
    {
        var collection = _tempDatabase.CreateTemporaryCollection<SimpleEntity>();
        using var db = SingleEntityDbContext.Create(collection);

        var expected = new SimpleEntity
        {
            _id = ObjectId.GenerateNewId(), name = "Generated"
        };
        db.Entities.Add(expected);
        db.SaveChanges();

        Assert.Same(expected, db.Entities.First());

        // Check with C# Driver for second opinion
        var directFound = collection.Find(f => f._id == expected._id).Single();
        Assert.Equal(expected._id, directFound._id);
        Assert.Equal(expected.name, directFound.name);
    }

    [Fact]
    public void Add_simple_entity_with_unset_ObjectId()
    {
        var collection = _tempDatabase.CreateTemporaryCollection<SimpleEntity>();
        using var db = SingleEntityDbContext.Create(collection);

        var expected = new SimpleEntity
        {
            name = "Not Set"
        };
        db.Entities.Add(expected);
        db.SaveChanges();

        Assert.Same(expected, db.Entities.First());

        // Check with C# Driver for second opinion
        var directFound = collection.Find(f => f._id == expected._id).Single();
        Assert.NotEqual(ObjectId.Empty, directFound._id);
        Assert.NotEqual(default, directFound._id);
        Assert.Equal(expected._id, directFound._id);
        Assert.Equal(expected.name, directFound.name);
    }

    [Fact]
    public void Add_simple_entity_with_empty_ObjectId()
    {
        var collection = _tempDatabase.CreateTemporaryCollection<SimpleEntity>();
        using var db = SingleEntityDbContext.Create(collection);

        var expected = new SimpleEntity
        {
            _id = ObjectId.Empty, name = "Empty"
        };
        db.Entities.Add(expected);
        db.SaveChanges();

        Assert.Same(expected, db.Entities.First());

        // Check with C# Driver for second opinion
        var directFound = collection.Find(f => f._id == expected._id).Single();
        Assert.NotEqual(ObjectId.Empty, directFound._id);
        Assert.NotEqual(default, directFound._id);
        Assert.Equal(expected._id, directFound._id);
        Assert.Equal(expected.name, directFound.name);
    }

    [Fact]
    public void Add_numeric_types_entity()
    {
        var collection = _tempDatabase.CreateTemporaryCollection<NumericTypesEntity>();

        var expected = new NumericTypesEntity
        {
            _id = Random.Next(),
            aDecimal = Random.NextDecimal(),
            aSingle = Random.NextSingle(),
            aDouble = Random.NextDouble() * double.MaxValue,
            aByte = Random.NextByte(),
            anInt16 = Random.NextInt16(),
            anInt32 = Random.Next(),
            anInt64 = Random.NextInt64()
        };

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entities.Add(expected);
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var foundEntity = db.Entities.Single();
            Assert.Equal(expected._id, foundEntity._id);
            Assert.Equal(expected.aDecimal, foundEntity.aDecimal);
            Assert.Equal(expected.aSingle, foundEntity.aSingle);
            Assert.Equal(expected.aDouble, foundEntity.aDouble);
            Assert.Equal(expected.aByte, foundEntity.aByte);
            Assert.Equal(expected.anInt16, foundEntity.anInt16);
            Assert.Equal(expected.anInt32, foundEntity.anInt32);
            Assert.Equal(expected.anInt64, foundEntity.anInt64);
        }
    }

    [Fact]
    public void Add_clr_types_entity()
    {
        var collection = _tempDatabase.CreateTemporaryCollection<OtherClrTypeEntity>();

        var expected = new OtherClrTypeEntity
        {
            _id = Guid.NewGuid(),
            aString = "Some kind of string",
            aChar = 'z',
            aGuid = Guid.NewGuid(),
            aDateTime = DateTime.UtcNow
        };

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entities.Add(expected);
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var foundEntity = db.Entities.Single();
            Assert.Equal(expected._id, foundEntity._id);
            Assert.Equal(expected.aString, foundEntity.aString);
            Assert.Equal(expected.aChar, foundEntity.aChar);
            Assert.Equal(expected.aGuid, foundEntity.aGuid);
            Assert.Equal(expected.aDateTime.ToExpectedPrecision(), foundEntity.aDateTime.ToExpectedPrecision());
        }
    }

    [Fact]
    public void Add_mongo_types_entity()
    {
        var collection = _tempDatabase.CreateTemporaryCollection<MongoSpecificTypeEntity>();

        var expected = new MongoSpecificTypeEntity
        {
            _id = ObjectId.GenerateNewId(), aDecimal128 = new Decimal128(Random.NextDecimal())
        };

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entities.Add(expected);
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var foundEntity = db.Entities.Single();
            Assert.Equal(expected._id, foundEntity._id);
            Assert.Equal(expected.aDecimal128, foundEntity.aDecimal128);
        }
    }

    [Theory]
    [InlineData(typeof(TestEnum), TestEnum.EnumValue0)]
    [InlineData(typeof(TestEnum), TestEnum.EnumValue1)]
    [InlineData(typeof(TestEnum?), TestEnum.EnumValue0)]
    [InlineData(typeof(TestEnum?), TestEnum.EnumValue1)]
    [InlineData(typeof(TestEnum?), null)]
    [InlineData(typeof(int[]), null)]
    [InlineData(typeof(int[]), new[]
    {
        -5, 0, 128, 10
    })]
    [InlineData(typeof(IList<int>), null)]
    [InlineData(typeof(IList<int>), new[]
    {
        -5, 0, 128, 10
    })]
    [InlineData(typeof(IReadOnlyList<int>), new[]
    {
        -5, 0, 128, 10
    })]
    [InlineData(typeof(List<int>), new[]
    {
        -5, 0, 128, 10
    })]
    [InlineData(typeof(string[]), null)]
    [InlineData(typeof(string[]), new[]
    {
        "one", "two"
    })]
    [InlineData(typeof(IList<string>), null)]
    [InlineData(typeof(List<string>), new[]
    {
        "one", "two"
    })]
    [InlineData(typeof(Collection<int>), null)]
    [InlineData(typeof(ObservableCollection<int>), null)]
    public void Entity_add_tests(Type valueType, object? value)
    {
        if (value != null && !value.GetType().IsAssignableTo(valueType))
        {
            value = Activator.CreateInstance(valueType, value);
        }

        GetType()
            .GetMethod(nameof(EntityAddTestImpl), BindingFlags.Instance | BindingFlags.NonPublic)!
            .MakeGenericMethod(valueType)
            .Invoke(this, [value]);
    }

    private enum TestEnum
    {
        EnumValue0 = 0,
        EnumValue1 = 1
    }

    private void EntityAddTestImpl<TValue>(TValue value)
    {
        var collection = _tempDatabase.CreateTemporaryCollection<Entity<TValue>>("EntityAddTestImpl", typeof(TValue), value);

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entities.Add(new Entity<TValue>
            {
                _id = ObjectId.GenerateNewId(), Value = value
            });
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var foundEntity = db.Entities.Single();
            Assert.Equal(value, foundEntity.Value);
        }
    }
}
