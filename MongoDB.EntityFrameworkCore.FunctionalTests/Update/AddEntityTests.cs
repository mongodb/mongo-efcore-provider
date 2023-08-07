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
using MongoDB.Driver;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Update;

public sealed class AddEntityTests : IDisposable
{
    private static readonly Random __random = new();
    private readonly TemporaryDatabase _tempDatabase = TestServer.CreateTemporaryDatabase();
    public void Dispose() => _tempDatabase.Dispose();

    class SimpleEntity
    {
        public ObjectId _id { get; set; }
        public string name { get; set; }
    }

    class NumericTypesEntity
    {
        public int _id { get; set; }
        public decimal aDecimal { get; set; }
        public Single aSingle { get; set; }
        public Double aDouble { get; set; }
        public byte aByte { get; set; }
        public Int16 anInt16 { get; set; }
        public Int32 anInt32 { get; set; }
        public Int64 anInt64 { get; set; }
    }

    class OtherClrTypeEntity
    {
        public Guid _id { get; set; }
        public string aString { get; set; }
        public char aChar { get; set; }
        public DateTime aDateTime { get; set; }
        public Guid aGuid { get; set; }
    }

    class MongoSpecificTypeEntity
    {
        public ObjectId _id { get; set; }
        public Decimal128 aDecimal128 { get; set; }
    }

    [Fact]
    public void Add_simple_entity()
    {
        var collection = _tempDatabase.CreateTemporaryCollection<SimpleEntity>();
        var dbContext = SingleEntityDbContext.Create(collection);

        var expected = new SimpleEntity {_id = ObjectId.GenerateNewId(), name = "AddMe"};
        dbContext.Entitites.Add(expected);
        dbContext.SaveChanges();

        {
            var foundEntity = dbContext.Entitites.Single();
            Assert.Equal(expected._id, foundEntity._id);
            Assert.Equal(expected.name, foundEntity.name);
        }

        {
            var directFound = collection.Find(f => f._id == expected._id).Single();
            Assert.Equal(expected._id, directFound._id);
            Assert.Equal(expected.name, directFound.name);
        }
    }

    [Fact]
    public void Add_numeric_types_entity()
    {
        var collection = _tempDatabase.CreateTemporaryCollection<NumericTypesEntity>();

        var expected = new NumericTypesEntity
        {
            _id = __random.Next(),
            aDecimal = __random.NextDecimal(),
            aSingle = __random.NextSingle(),
            aDouble = __random.NextDouble() * Double.MaxValue,
            aByte = __random.NextByte(),
            anInt16 = __random.NextInt16(),
            anInt32 = __random.Next(),
            anInt64 = __random.NextInt64()
        };

        {
            var dbContext = SingleEntityDbContext.Create(collection);
            dbContext.Entitites.Add(expected);
            dbContext.SaveChanges();
        }

        {
            var newDbContext = SingleEntityDbContext.Create(collection);
            var foundEntity = newDbContext.Entitites.Single();
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
            var dbContext = SingleEntityDbContext.Create(collection);
            dbContext.Entitites.Add(expected);
            dbContext.SaveChanges();
        }

        {
            var newDbContext = SingleEntityDbContext.Create(collection);
            var foundEntity = newDbContext.Entitites.Single();
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
            _id = ObjectId.GenerateNewId(), aDecimal128 = new Decimal128(__random.NextDecimal())
        };

        {
            var dbContext = SingleEntityDbContext.Create(collection);
            dbContext.Entitites.Add(expected);
            dbContext.SaveChanges();
        }

        {
            var newDbContext = SingleEntityDbContext.Create(collection);
            var foundEntity = newDbContext.Entitites.Single();
            Assert.Equal(expected._id, foundEntity._id);
            Assert.Equal(expected.aDecimal128, foundEntity.aDecimal128);
        }
    }
}
