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

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Serialization;

public class IntegerSerializationTests : BaseSerializationTests
{
    public IntegerSerializationTests(TemporaryDatabaseFixture tempDatabase)
        : base(tempDatabase)
    {
    }

    [Theory]
    [InlineData(78910)]
    [InlineData(-123)]
    [InlineData(0)]
    public void Int_round_trips(int expected)
    {
        var collection = TempDatabase.CreateTemporaryCollection<IntEntity>(nameof(Int_round_trips) + expected);

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entitites.Add(new IntEntity
            {
                anInt = expected
            });
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var result = db.Entitites.FirstOrDefault();
            Assert.NotNull(result);
            Assert.Equal(expected, result.anInt);
        }
    }

    [Fact]
    public void Missing_int_throws()
    {
        var collection = SetupIdOnlyCollection<IntEntity>();
        using var db = SingleEntityDbContext.Create(collection);

        Assert.Throws<InvalidOperationException>(() => db.Entitites.FirstOrDefault());
    }

    class IntEntity : BaseIdEntity
    {
        public int anInt { get; set; }
    }

    [Theory]
    [InlineData(78910)]
    [InlineData(-123)]
    [InlineData(0)]
    [InlineData(null)]
    public void Nullable_int_round_trips(int? expected)
    {
        var collection = TempDatabase.CreateTemporaryCollection<NullableIntEntity>(nameof(Nullable_int_round_trips) + expected);

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entitites.Add(new NullableIntEntity
            {
                aNullableInt = expected
            });
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var result = db.Entitites.FirstOrDefault();
            Assert.NotNull(result);
            Assert.Equal(expected, result.aNullableInt);
        }
    }

    [Fact]
    public void Missing_nullable_int_defaults_to_null()
    {
        var collection = SetupIdOnlyCollection<NullableIntEntity>();
        using var db = SingleEntityDbContext.Create(collection);

        var result = db.Entitites.FirstOrDefault();
        Assert.NotNull(result);
        Assert.Null(result.aNullableInt);
    }

    class NullableIntEntity : BaseIdEntity
    {
        public int? aNullableInt { get; set; }
    }

    [Theory]
    [InlineData(78910L)]
    [InlineData(-123L)]
    [InlineData(0L)]
    public void Long_round_trips(long expected)
    {
        var collection = TempDatabase.CreateTemporaryCollection<LongEntity>(nameof(Long_round_trips) + expected);

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entitites.Add(new LongEntity
            {
                aLong = expected
            });
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var result = db.Entitites.FirstOrDefault();
            Assert.NotNull(result);
            Assert.Equal(expected, result.aLong);
        }
    }

    [Fact]
    public void Missing_long_throws()
    {
        var collection = SetupIdOnlyCollection<LongEntity>();
        using var db = SingleEntityDbContext.Create(collection);

        Assert.Throws<InvalidOperationException>(() => db.Entitites.FirstOrDefault());
    }

    class LongEntity : BaseIdEntity
    {
        public long aLong { get; set; }
    }

    [Theory]
    [InlineData(78910L)]
    [InlineData(-123L)]
    [InlineData(0L)]
    [InlineData(null)]
    public void Nullable_long_round_trips(long? expected)
    {
        var collection = TempDatabase.CreateTemporaryCollection<NullableLongEntity>(nameof(Nullable_long_round_trips) + expected);

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entitites.Add(new NullableLongEntity
            {
                aNullableLong = expected
            });
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var result = db.Entitites.FirstOrDefault();
            Assert.NotNull(result);
            Assert.Equal(expected, result.aNullableLong);
        }
    }

    [Fact]
    public void Missing_nullable_long_defaults_to_null()
    {
        var collection = SetupIdOnlyCollection<NullableLongEntity>();
        using var db = SingleEntityDbContext.Create(collection);

        var result = db.Entitites.FirstOrDefault();
        Assert.NotNull(result);
        Assert.Null(result.aNullableLong);
    }

    class NullableLongEntity : BaseIdEntity
    {
        public long? aNullableLong { get; set; }
    }

    [Theory]
    [InlineData(7890)]
    [InlineData(-123)]
    [InlineData(0)]
    public void Short_round_trips(short expected)
    {
        var collection = TempDatabase.CreateTemporaryCollection<ShortEntity>(nameof(Short_round_trips) + expected);

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entitites.Add(new ShortEntity
            {
                aShort = expected
            });
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var result = db.Entitites.FirstOrDefault();
            Assert.NotNull(result);
            Assert.Equal(expected, result.aShort);
        }
    }

    [Fact]
    public void Missing_short_throws()
    {
        var collection = SetupIdOnlyCollection<ShortEntity>();
        using var db = SingleEntityDbContext.Create(collection);

        Assert.Throws<InvalidOperationException>(() => db.Entitites.FirstOrDefault());
    }

    class ShortEntity : BaseIdEntity
    {
        public short aShort { get; set; }
    }

    [Theory]
    [InlineData(7890)]
    [InlineData(-123)]
    [InlineData(0)]
    public void Nullable_short_round_trips(int? expectedInt)
    {
        var expected = Convert.ToInt16(expectedInt);
        var collection = TempDatabase.CreateTemporaryCollection<NullableShortEntity>(nameof(Nullable_short_round_trips) + expected);

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entitites.Add(new NullableShortEntity
            {
                aNullableShort = expected
            });
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var result = db.Entitites.FirstOrDefault();
            Assert.NotNull(result);
            Assert.Equal(expected, result.aNullableShort);
        }
    }

    [Fact]
    public void Missing_nullable_short_defaults_to_null()
    {
        var collection = SetupIdOnlyCollection<NullableShortEntity>();
        using var db = SingleEntityDbContext.Create(collection);

        var result = db.Entitites.FirstOrDefault();
        Assert.NotNull(result);
        Assert.Null(result.aNullableShort);
    }

    class NullableShortEntity : BaseIdEntity
    {
        public short? aNullableShort { get; set; }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(67)]
    [InlineData(-128)]
    [InlineData(127)]
    public void Sbyte_round_trips(sbyte expected)
    {
        var collection = TempDatabase.CreateTemporaryCollection<SByteEntity>(nameof(Sbyte_round_trips) + expected);

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entitites.Add(new SByteEntity
            {
                aByte = expected
            });
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var result = db.Entitites.FirstOrDefault();
            Assert.NotNull(result);
            Assert.Equal(expected, result.aByte);
        }
    }

    [Fact]
    public void Missing_sbyte_throws()
    {
        var collection = SetupIdOnlyCollection<SByteEntity>();
        using var db = SingleEntityDbContext.Create(collection);

        Assert.Throws<InvalidOperationException>(() => db.Entitites.FirstOrDefault());
    }

    class SByteEntity : BaseIdEntity
    {
        public sbyte aByte { get; set; }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(67)]
    [InlineData(-128)]
    [InlineData(127)]
    [InlineData(null)]
    public void Nullable_sbyte_round_trips(int? expectedInt)
    {
        sbyte? expected = expectedInt == null ? null : Convert.ToSByte(expectedInt);
        var collection = TempDatabase.CreateTemporaryCollection<NullableSByteEntity>(nameof(Nullable_sbyte_round_trips) + expected);

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entitites.Add(new NullableSByteEntity
            {
                aNullableSByte = expected
            });
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var result = db.Entitites.FirstOrDefault();
            Assert.NotNull(result);
            Assert.Equal(expected, result.aNullableSByte);
        }
    }

    [Fact]
    public void Missing_nullable_sbyte_defaults_to_null()
    {
        var collection = SetupIdOnlyCollection<NullableSByteEntity>();
        using var db = SingleEntityDbContext.Create(collection);

        var result = db.Entitites.FirstOrDefault();
        Assert.NotNull(result);
        Assert.Null(result.aNullableSByte);
    }

    class NullableSByteEntity : BaseIdEntity
    {
        public sbyte? aNullableSByte { get; set; }
    }
}
