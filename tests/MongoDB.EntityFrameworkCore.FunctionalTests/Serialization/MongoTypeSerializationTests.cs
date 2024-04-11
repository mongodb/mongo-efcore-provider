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

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Serialization;

public class MongoTypeSerializationTests : BaseSerializationTests
{
    public MongoTypeSerializationTests(TemporaryDatabaseFixture tempDatabase)
        : base(tempDatabase)
    {
    }

    [Theory]
    [InlineData("652446393021fe289cf2c197")]
    [InlineData("64a8583aa1ee84d292c009dd")]
    public void ObjectId_round_trips(string expectedString)
    {
        ObjectId expected = ObjectId.Parse(expectedString);
        var collection = TempDatabase.CreateTemporaryCollection<ObjectIdEntity>(nameof(ObjectId_round_trips) + expectedString);

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entitites.Add(new ObjectIdEntity
            {
                anObjectId = expected
            });
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var result = db.Entitites.FirstOrDefault();
            Assert.NotNull(result);
            Assert.Equal(expected, result.anObjectId);
        }
    }

    [Fact]
    public void Missing_ObjectId_throws()
    {
        var collection = SetupIdOnlyCollection<ObjectIdEntity>();
        using var db = SingleEntityDbContext.Create(collection);

        Assert.Throws<InvalidOperationException>(() => db.Entitites.FirstOrDefault());
    }

    class ObjectIdEntity : BaseIdEntity
    {
        public ObjectId anObjectId { get; set; }
    }

    [Theory]
    [InlineData("652446393021fe289cf2c197")]
    [InlineData("64a8583aa1ee84d292c009dd")]
    [InlineData(null)]
    public void Nullable_ObjectId_round_trips(string? expectedString)
    {
        ObjectId? expected = expectedString == null ? null : ObjectId.Parse(expectedString);
        var collection =
            TempDatabase.CreateTemporaryCollection<NullableObjectIdEntity>(nameof(Nullable_ObjectId_round_trips) + expectedString);

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entitites.Add(new NullableObjectIdEntity
            {
                aNullableObjectId = expected
            });
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var result = db.Entitites.FirstOrDefault();
            Assert.NotNull(result);
            Assert.Equal(expected, result.aNullableObjectId);
        }
    }

    [Fact]
    public void Missing_nullable_ObjectId_defaults_to_null()
    {
        var collection = SetupIdOnlyCollection<NullableObjectIdEntity>();
        using var db = SingleEntityDbContext.Create(collection);

        var result = db.Entitites.FirstOrDefault();
        Assert.NotNull(result);
        Assert.Null(result.aNullableObjectId);
    }

    class NullableObjectIdEntity : BaseIdEntity
    {
        public ObjectId? aNullableObjectId { get; set; }
    }

    [Theory]
    [InlineData("123456789.12345")]
    [InlineData("0")]
    [InlineData("-987654321.01234")]
    public void Decimal128_round_trips(string expectedString)
    {
        Decimal128 expected = Decimal128.Parse(expectedString);
        var collection = TempDatabase.CreateTemporaryCollection<Decimal128Entity>(nameof(Decimal128_round_trips) + expectedString);

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entitites.Add(new Decimal128Entity
            {
                anDecimal128 = expected
            });
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var result = db.Entitites.FirstOrDefault();
            Assert.NotNull(result);
            Assert.Equal(expected, result.anDecimal128);
        }
    }

    [Fact]
    public void Missing_Decimal128_throws()
    {
        var collection = SetupIdOnlyCollection<Decimal128Entity>();
        using var db = SingleEntityDbContext.Create(collection);

        Assert.Throws<InvalidOperationException>(() => db.Entitites.FirstOrDefault());
    }

    class Decimal128Entity : BaseIdEntity
    {
        public Decimal128 anDecimal128 { get; set; }
    }

    [Theory]
    [InlineData("123456789.12345")]
    [InlineData("0")]
    [InlineData("-987654321.01234")]
    [InlineData(null)]
    public void Nullable_Decimal128_round_trips(string? expectedString)
    {
        Decimal128? expected = expectedString == null ? null : Decimal128.Parse(expectedString);
        var collection =
            TempDatabase.CreateTemporaryCollection<NullableDecimal128Entity>(nameof(Nullable_Decimal128_round_trips) +
                                                                             expectedString);

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entitites.Add(new NullableDecimal128Entity
            {
                aNullableDecimal128 = expected
            });
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var result = db.Entitites.FirstOrDefault();
            Assert.NotNull(result);
            Assert.Equal(expected, result.aNullableDecimal128);
        }
    }

    [Fact]
    public void Missing_nullable_Decimal128_defaults_to_null()
    {
        var collection = SetupIdOnlyCollection<NullableDecimal128Entity>();
        using var db = SingleEntityDbContext.Create(collection);

        var result = db.Entitites.FirstOrDefault();
        Assert.NotNull(result);
        Assert.Null(result.aNullableDecimal128);
    }

    class NullableDecimal128Entity : BaseIdEntity
    {
        public Decimal128? aNullableDecimal128 { get; set; }
    }
}
