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

public class FloatingSerializationTests : BaseSerializationTests
{
    public FloatingSerializationTests(TemporaryDatabaseFixture tempDatabase)
        : base(tempDatabase)
    {
    }

    [Theory]
    [InlineData(1234.56f)]
    [InlineData(-4587.498f)]
    [InlineData(0f)]
    public void Float_round_trips(float expected)
    {
        var collection = TempDatabase.CreateTemporaryCollection<FloatEntity>(nameof(Float_round_trips) + expected);

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entitites.Add(new FloatEntity
            {
                aFloat = expected
            });
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var result = db.Entitites.FirstOrDefault();
            Assert.NotNull(result);
            Assert.Equal(expected, result.aFloat);
        }
    }

    [Fact]
    public void Missing_float_throws()
    {
        var collection = SetupIdOnlyCollection<FloatEntity>();
        using var db = SingleEntityDbContext.Create(collection);

        Assert.Throws<InvalidOperationException>(() => db.Entitites.FirstOrDefault());
    }

    class FloatEntity : BaseIdEntity
    {
        public float aFloat { get; set; }
    }

    [Theory]
    [InlineData(1234.56f)]
    [InlineData(-4587.498f)]
    [InlineData(0f)]
    [InlineData(null)]
    public void Nullable_Float_round_trips(float? expected)
    {
        var collection = TempDatabase.CreateTemporaryCollection<NullableFloatEntity>(nameof(Nullable_Float_round_trips) + expected);

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entitites.Add(new NullableFloatEntity
            {
                aNullableFloat = expected
            });
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var result = db.Entitites.FirstOrDefault();
            Assert.NotNull(result);
            Assert.Equal(expected, result.aNullableFloat);
        }
    }

    [Fact]
    public void Missing_nullable_float_defaults_to_null()
    {
        var collection = SetupIdOnlyCollection<NullableFloatEntity>();
        using var db = SingleEntityDbContext.Create(collection);

        var result = db.Entitites.FirstOrDefault();
        Assert.NotNull(result);
        Assert.Null(result.aNullableFloat);
    }

    class NullableFloatEntity : BaseIdEntity
    {
        public float? aNullableFloat { get; set; }
    }

    [Theory]
    [InlineData(1234.56)]
    [InlineData(-4587.498)]
    [InlineData(0.0)]
    public void Double_round_trips(double expected)
    {
        var collection = TempDatabase.CreateTemporaryCollection<DoubleEntity>(nameof(Double_round_trips) + expected);

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entitites.Add(new DoubleEntity
            {
                aDouble = expected
            });
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var result = db.Entitites.FirstOrDefault();
            Assert.NotNull(result);
            Assert.Equal(expected, result.aDouble);
        }
    }

    [Fact]
    public void Missing_double_throws()
    {
        var collection = SetupIdOnlyCollection<DoubleEntity>();
        using var db = SingleEntityDbContext.Create(collection);

        Assert.Throws<InvalidOperationException>(() => db.Entitites.FirstOrDefault());
    }

    class DoubleEntity : BaseIdEntity
    {
        public double aDouble { get; set; }
    }

    [Theory]
    [InlineData(1234.56)]
    [InlineData(-4587.498)]
    [InlineData(0.0)]
    [InlineData(null)]
    public void Nullable_Double_round_trips(double? expected)
    {
        var collection =
            TempDatabase.CreateTemporaryCollection<NullableDoubleEntity>(nameof(Nullable_Double_round_trips) + expected);

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entitites.Add(new NullableDoubleEntity
            {
                aNullableDouble = expected
            });
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var result = db.Entitites.FirstOrDefault();
            Assert.NotNull(result);
            Assert.Equal(expected, result.aNullableDouble);
        }
    }

    [Fact]
    public void Missing_nullable_double_defaults_to_null()
    {
        var collection = SetupIdOnlyCollection<NullableDoubleEntity>();
        using var db = SingleEntityDbContext.Create(collection);

        var result = db.Entitites.FirstOrDefault();
        Assert.NotNull(result);
        Assert.Null(result.aNullableDouble);
    }

    class NullableDoubleEntity : BaseIdEntity
    {
        public double? aNullableDouble { get; set; }
    }
}
