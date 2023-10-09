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

public class BooleanSerializationTests : BaseSerializationTests
{
    public BooleanSerializationTests(TemporaryDatabaseFixture tempDatabase)
        : base(tempDatabase)
    {
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Boolean_round_trips(bool expected)
    {
        var collection = TempDatabase.CreateTemporaryCollection<BooleanEntity>(nameof(Boolean_round_trips) + expected);

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entitites.Add(new BooleanEntity {aBoolean = expected});
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var result = db.Entitites.FirstOrDefault();
            Assert.NotNull(result);
            Assert.Equal(expected, result.aBoolean);
        }
    }

    [Fact]
    public void Missing_bool_throws()
    {
        var collection = SetupIdOnlyCollection<BooleanEntity>();
        using var db = SingleEntityDbContext.Create(collection);

        Assert.Throws<KeyNotFoundException>(() => db.Entitites.FirstOrDefault());
    }

    class BooleanEntity : BaseIdEntity
    {
        public Boolean aBoolean { get; set; }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [InlineData(null)]
    public void Nullable_bool_round_trips(bool? expected)
    {
        var collection =
            TempDatabase.CreateTemporaryCollection<NullableBooleanEntity>(nameof(Nullable_bool_round_trips) + expected);

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entitites.Add(new NullableBooleanEntity {aNullableBoolean = expected});
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var result = db.Entitites.FirstOrDefault();
            Assert.NotNull(result);
            Assert.Equal(expected, result.aNullableBoolean);
        }
    }

    [Fact]
    public void Missing_nullable_bool_defaults_to_null()
    {
        var collection = SetupIdOnlyCollection<NullableBooleanEntity>();
        using var db = SingleEntityDbContext.Create(collection);

        var result = db.Entitites.FirstOrDefault();
        Assert.NotNull(result);
        Assert.Null(result.aNullableBoolean);
    }

    class NullableBooleanEntity : BaseIdEntity
    {
        public Boolean? aNullableBoolean { get; set; }
    }
}
