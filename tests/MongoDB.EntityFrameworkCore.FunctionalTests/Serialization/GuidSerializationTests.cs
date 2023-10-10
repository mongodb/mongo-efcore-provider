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

public class GuidSerializationTests : BaseSerializationTests
{
    public GuidSerializationTests(TemporaryDatabaseFixture tempDatabase)
        : base(tempDatabase)
    {
    }

    [Theory]
    [InlineData("dd2838d8-66bf-11ee-8c99-0242ac120002")]
    [InlineData("305b397d-98d3-4f4e-aff6-5e4693d59f6a")]
    [InlineData("d04381cb-2988-50f0-8bca-43fbc99146ad")]
    [InlineData("00ccebbc-13e0-7000-8b18-6150ad2d0c01")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void Guid_round_trips(string expectedString)
    {
        Guid expected = Guid.Parse(expectedString);
        var collection = TempDatabase.CreateTemporaryCollection<GuidEntity>(nameof(Guid_round_trips) + expected);

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entitites.Add(new GuidEntity {aGuid = expected});
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var result = db.Entitites.FirstOrDefault();
            Assert.NotNull(result);
            Assert.Equal(expected, result.aGuid);
        }
    }

    [Fact]
    public void Missing_guid_throws()
    {
        var collection = SetupIdOnlyCollection<GuidEntity>();
        using var db = SingleEntityDbContext.Create(collection);

        Assert.Throws<KeyNotFoundException>(() => db.Entitites.FirstOrDefault());
    }

    class GuidEntity : BaseIdEntity
    {
        public Guid aGuid { get; set; }
    }

    [Theory]
    [InlineData("dd2838d8-66bf-11ee-8c99-0242ac120002")]
    [InlineData("305b397d-98d3-4f4e-aff6-5e4693d59f6a")]
    [InlineData("d04381cb-2988-50f0-8bca-43fbc99146ad")]
    [InlineData("00ccebbc-13e0-7000-8b18-6150ad2d0c01")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData(null)]
    public void Nullable_guid_round_trips(string? expectedString)
    {
        Guid? expected = expectedString == null ? null : Guid.Parse(expectedString);
        var collection = TempDatabase.CreateTemporaryCollection<NullableGuidEntity>(nameof(Nullable_guid_round_trips) + expected);

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entitites.Add(new NullableGuidEntity {aNullableGuid = expected});
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var result = db.Entitites.FirstOrDefault();
            Assert.NotNull(result);
            Assert.Equal(expected, result.aNullableGuid);
        }
    }

    [Fact]
    public void Missing_nullable_guid_defaults_to_null()
    {
        var collection = SetupIdOnlyCollection<NullableGuidEntity>();
        using var db = SingleEntityDbContext.Create(collection);

        var result = db.Entitites.FirstOrDefault();
        Assert.NotNull(result);
        Assert.Null(result.aNullableGuid);
    }

    class NullableGuidEntity : BaseIdEntity
    {
        public Guid? aNullableGuid { get; set; }
    }
}
