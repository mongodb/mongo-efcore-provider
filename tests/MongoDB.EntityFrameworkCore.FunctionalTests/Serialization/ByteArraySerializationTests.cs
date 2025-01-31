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

public class ByteArraySerializationTests(TemporaryDatabaseFixture database)
    : BaseSerializationTests(database)
{
    [Theory]
    [InlineData("dd2838d866bf")]
    [InlineData("305b397d98d34f4eaff65e4693d59f6a")]
    [InlineData("d04381cb298850f08bca43fbc99146adff9faa00ff")]
    [InlineData("00ccebbc13e070008b186150ad2d0c01")]
    [InlineData("00")]
    [InlineData("")]
    public void ByteArray_round_trips(string expectedString)
    {
        var expected = Convert.FromHexString(expectedString);
        var collection = Database.CreateCollection<ByteArrayEntity>(values: expected);

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entities.Add(new ByteArrayEntity
            {
                aByteArray = expected
            });
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var result = db.Entities.FirstOrDefault();
            Assert.NotNull(result);
            Assert.Equal(expected, result.aByteArray);
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var result = db.Entities.FirstOrDefault(e => e.aByteArray == expected);
            Assert.NotNull(result);
            Assert.Equal(expected, result.aByteArray);
        }
    }

    [Fact]
    public void Missing_ByteArray_throws()
    {
        var collection = SetupIdOnlyCollection<ByteArrayEntity>();
        using var db = SingleEntityDbContext.Create(collection);

        Assert.Throws<InvalidOperationException>(() => db.Entities.FirstOrDefault());
    }

    class ByteArrayEntity : BaseIdEntity
    {
        public byte[] aByteArray { get; set; }
    }

    [Theory]
    [InlineData("dd2838d866bf")]
    [InlineData("305b397d98d34f4eaff65e4693d59f6a")]
    [InlineData("d04381cb298850f08bca43fbc99146adff9faa00ff")]
    [InlineData("00ccebbc13e070008b186150ad2d0c01")]
    [InlineData("00")]
    [InlineData("")]
    [InlineData(null)]
    public void Nullable_ByteArray_round_trips(string? expectedString)
    {
        var expected = expectedString == null
            ? null
            : Convert.FromHexString(expectedString);
        var collection = Database.CreateCollection<NullableByteArrayEntity>(values: expected);

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entities.Add(new NullableByteArrayEntity
            {
                aNullableByteArray = expected
            });
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var result = db.Entities.FirstOrDefault();
            Assert.NotNull(result);
            Assert.Equal(expected, result.aNullableByteArray);
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var result = db.Entities.FirstOrDefault(e => e.aNullableByteArray == expected);
            Assert.NotNull(result);
            Assert.Equal(expected, result.aNullableByteArray);
        }
    }

    [Fact]
    public void Missing_nullable_guid_defaults_to_null()
    {
        var collection = SetupIdOnlyCollection<NullableByteArrayEntity>();
        using var db = SingleEntityDbContext.Create(collection);

        var result = db.Entities.FirstOrDefault();
        Assert.NotNull(result);
        Assert.Null(result.aNullableByteArray);
    }

    class NullableByteArrayEntity : BaseIdEntity
    {
        public byte[]? aNullableByteArray { get; set; }
    }
}
