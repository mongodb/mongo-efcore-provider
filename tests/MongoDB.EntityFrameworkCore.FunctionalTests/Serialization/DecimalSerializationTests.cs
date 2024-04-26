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

public class DecimalSerializationTests : BaseSerializationTests
{
    public DecimalSerializationTests(TemporaryDatabaseFixture tempDatabase)
        : base(tempDatabase)
    {
    }

    [Theory]
    [InlineData("12345.678")]
    [InlineData("-912345.6781")]
    [InlineData("0")]
    public void Decimal_round_trips(string expectedString)
    {
        var expected = Decimal.Parse(expectedString);
        var collection = TempDatabase.CreateTemporaryCollection<DecimalEntity>(nameof(Decimal_round_trips) + expected);

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entities.Add(new DecimalEntity
            {
                aDecimal = expected
            });
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var result = db.Entities.FirstOrDefault();
            Assert.NotNull(result);
            Assert.Equal(expected, result.aDecimal);
        }
    }

    [Fact]
    public void Missing_decimal_throws()
    {
        var collection = SetupIdOnlyCollection<DecimalEntity>();
        using var db = SingleEntityDbContext.Create(collection);

        Assert.Throws<InvalidOperationException>(() => db.Entities.FirstOrDefault());
    }

    class DecimalEntity : BaseIdEntity
    {
        public decimal aDecimal { get; set; }
    }

    [Theory]
    [InlineData("12345.678")]
    [InlineData("-912345.6781")]
    [InlineData("0")]
    [InlineData(null)]
    public void Nullable_Decimal_round_trips(string expectedString)
    {
        Decimal? expected = Decimal.TryParse(expectedString, out var parsedDecimal) ? parsedDecimal : null;

        var collection =
            TempDatabase.CreateTemporaryCollection<NullableDecimalEntity>(nameof(Nullable_Decimal_round_trips) + expected);

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entities.Add(new NullableDecimalEntity
            {
                aNullableDecimal = expected
            });
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var result = db.Entities.FirstOrDefault();
            Assert.NotNull(result);
            Assert.Equal(expected, result.aNullableDecimal);
        }
    }

    [Fact]
    public void Missing_nullable_decimal_defaults_to_null()
    {
        var collection = SetupIdOnlyCollection<NullableDecimalEntity>();
        using var db = SingleEntityDbContext.Create(collection);

        var result = db.Entities.FirstOrDefault();
        Assert.NotNull(result);
        Assert.Null(result.aNullableDecimal);
    }

    class NullableDecimalEntity : BaseIdEntity
    {
        public decimal? aNullableDecimal { get; set; }
    }
}
