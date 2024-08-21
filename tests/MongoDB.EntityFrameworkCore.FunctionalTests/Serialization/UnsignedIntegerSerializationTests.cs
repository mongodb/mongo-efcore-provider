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

public class UnsignedIntegerSerializationTests(TemporaryDatabaseFixture database)
    : BaseSerializationTests(database)
{
    [Theory]
    [InlineData(78910u)]
    [InlineData(0u)]
    public void Uint_round_trips(uint expected)
    {
        var collection = Database.CreateCollection<UIntEntity>(values: expected);

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entities.Add(new UIntEntity {aUint = expected});
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var result = db.Entities.FirstOrDefault();
            Assert.NotNull(result);
            Assert.Equal(expected, result.aUint);
        }
    }

    [Fact]
    public void Missing_uint_throws()
    {
        var collection = SetupIdOnlyCollection<UIntEntity>();
        using var db = SingleEntityDbContext.Create(collection);

        Assert.Throws<InvalidOperationException>(() => db.Entities.FirstOrDefault());
    }

    class UIntEntity : BaseIdEntity
    {
        public uint aUint { get; set; }
    }

    [Theory]
    [InlineData(78910U)]
    [InlineData(0U)]
    [InlineData(null)]
    public void Nullable_uint_round_trips(uint? expected)
    {
        var collection = Database.CreateCollection<NullableUIntEntity>(values: expected);

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entities.Add(new NullableUIntEntity {aNullableUint = expected});
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var result = db.Entities.FirstOrDefault();
            Assert.NotNull(result);
            Assert.Equal(expected, result.aNullableUint);
        }
    }

    [Fact]
    public void Missing_nullable_uint_defaults_to_null()
    {
        var collection = SetupIdOnlyCollection<NullableUIntEntity>();
        using var db = SingleEntityDbContext.Create(collection);

        var result = db.Entities.FirstOrDefault();
        Assert.NotNull(result);
        Assert.Null(result.aNullableUint);
    }

    class NullableUIntEntity : BaseIdEntity
    {
        public uint? aNullableUint { get; set; }
    }

    [Theory]
    [InlineData(78910UL)]
    [InlineData(0L)]
    public void Ulong_round_trips(ulong expected)
    {
        var collection = Database.CreateCollection<UlongEntity>(values: expected);

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entities.Add(new UlongEntity {aUlong = expected});
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var result = db.Entities.FirstOrDefault();
            Assert.NotNull(result);
            Assert.Equal(expected, result.aUlong);
        }
    }

    [Fact]
    public void Missing_ulong_throws()
    {
        var collection = SetupIdOnlyCollection<UlongEntity>();
        using var db = SingleEntityDbContext.Create(collection);

        Assert.Throws<InvalidOperationException>(() => db.Entities.FirstOrDefault());
    }

    class UlongEntity : BaseIdEntity
    {
        public ulong aUlong { get; set; }
    }

    [Theory]
    [InlineData(78910UL)]
    [InlineData(0UL)]
    [InlineData(null)]
    public void Nullable_ulong_round_trips(ulong? expected)
    {
        var collection = Database.CreateCollection<NullableUlongEntity>(values: expected);

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entities.Add(new NullableUlongEntity {aNullableUlong = expected});
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var result = db.Entities.FirstOrDefault();
            Assert.NotNull(result);
            Assert.Equal(expected, result.aNullableUlong);
        }
    }

    [Fact]
    public void Missing_nullable_ulong_defaults_to_null()
    {
        var collection = SetupIdOnlyCollection<NullableUlongEntity>();
        using var db = SingleEntityDbContext.Create(collection);

        var result = db.Entities.FirstOrDefault();
        Assert.NotNull(result);
        Assert.Null(result.aNullableUlong);
    }

    class NullableUlongEntity : BaseIdEntity
    {
        public ulong? aNullableUlong { get; set; }
    }

    [Theory]
    [InlineData(7890U)]
    [InlineData(0)]
    public void Ushort_round_trips(ushort expected)
    {
        var collection = Database.CreateCollection<UshortEntity>(values: expected);

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entities.Add(new UshortEntity {aUshort = expected});
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var result = db.Entities.FirstOrDefault();
            Assert.NotNull(result);
            Assert.Equal(expected, result.aUshort);
        }
    }

    [Fact]
    public void Missing_ushort_throws()
    {
        var collection = SetupIdOnlyCollection<UshortEntity>();
        using var db = SingleEntityDbContext.Create(collection);

        Assert.Throws<InvalidOperationException>(() => db.Entities.FirstOrDefault());
    }

    class UshortEntity : BaseIdEntity
    {
        public ushort aUshort { get; set; }
    }

    [Theory]
    [InlineData(7890)]
    [InlineData(0)]
    public void Nullable_ushort_round_trips(int? expectedInt)
    {
        ushort? expected = expectedInt == null ? null : Convert.ToUInt16(expectedInt);
        var collection =
            Database.CreateCollection<NullableShortEntity>(values: expected);

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entities.Add(new NullableShortEntity {aNullableUshort = expected});
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var result = db.Entities.FirstOrDefault();
            Assert.NotNull(result);
            Assert.Equal(expected, result.aNullableUshort);
        }
    }

    [Fact]
    public void Missing_nullable_ushort_defaults_to_null()
    {
        var collection = SetupIdOnlyCollection<NullableShortEntity>();
        using var db = SingleEntityDbContext.Create(collection);

        var result = db.Entities.FirstOrDefault();
        Assert.NotNull(result);
        Assert.Null(result.aNullableUshort);
    }

    class NullableShortEntity : BaseIdEntity
    {
        public ushort? aNullableUshort { get; set; }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(67)]
    [InlineData(255)]
    public void Byte_round_trips(byte expected)
    {
        var collection = Database.CreateCollection<ByteEntity>(values: expected);

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entities.Add(new ByteEntity {aByte = expected});
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var result = db.Entities.FirstOrDefault();
            Assert.NotNull(result);
            Assert.Equal(expected, result.aByte);
        }
    }

    [Fact]
    public void Missing_byte_throws()
    {
        var collection = SetupIdOnlyCollection<ByteEntity>();
        using var db = SingleEntityDbContext.Create(collection);

        Assert.Throws<InvalidOperationException>(() => db.Entities.FirstOrDefault());
    }

    class ByteEntity : BaseIdEntity
    {
        public byte aByte { get; set; }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(67)]
    [InlineData(255)]
    [InlineData(null)]
    public void Nullable_byte_round_trips(int? expectedInt)
    {
        byte? expected = expectedInt == null ? null : Convert.ToByte(expectedInt);
        var collection = Database.CreateCollection<NullableByteEntity>(values: expected);

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entities.Add(new NullableByteEntity {aNullableByte = expected});
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var result = db.Entities.FirstOrDefault();
            Assert.NotNull(result);
            Assert.Equal(expected, result.aNullableByte);
        }
    }

    [Fact]
    public void Missing_nullable_byte_defaults_to_null()
    {
        var collection = SetupIdOnlyCollection<NullableByteEntity>();
        using var db = SingleEntityDbContext.Create(collection);

        var result = db.Entities.FirstOrDefault();
        Assert.NotNull(result);
        Assert.Null(result.aNullableByte);
    }

    class NullableByteEntity : BaseIdEntity
    {
        public byte? aNullableByte { get; set; }
    }
}
