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

using Microsoft.EntityFrameworkCore;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Serialization;

public class DateAndTimeSerializationTests(TemporaryDatabaseFixture database)
    : BaseSerializationTests(database)
{
    [Fact]
    public void DateTime_round_trips_as_utc_with_expected_precision()
    {
        var expected = DateTime.UtcNow;

        {
            var collection = Database.CreateCollection<DateTimeEntity>();
            using var db = SingleEntityDbContext.Create(collection);
            db.Entities.Add(new DateTimeEntity {aDateTime = expected});
            db.SaveChanges();
        }

        {
            var collection = Database.GetCollection<UtcDateTimeEntity>();
            var result = collection.AsQueryable().First();
            Assert.NotNull(result);
            Assert.Equal(expected.ToBsonPrecision(), result.aDateTime.ToBsonPrecision());
        }
    }

    [Fact]
    public void DateTime_round_trips_as_local()
    {
        var expected = DateTime.Now;

        {
            var collection = Database.CreateCollection<DateTimeEntity>();
            using var db = SingleEntityDbContext.Create(collection,
                model => model.Entity<DateTimeEntity>().Property(e => e.aDateTime).HasDateTimeKind(DateTimeKind.Local));
            db.Entities.Add(new DateTimeEntity {aDateTime = expected});
            db.SaveChanges();
        }

        {
            var collection = Database.GetCollection<LocalDateTimeEntity>();
            var result = collection.AsQueryable().First();
            Assert.NotNull(result);
            Assert.Equal(expected.ToBsonPrecision(), result.aDateTime.ToBsonPrecision());
        }
    }

    [Fact]
    public void Missing_DateTime_throws()
    {
        var collection = SetupIdOnlyCollection<DateTimeEntity>();
        using var db = SingleEntityDbContext.Create(collection);

        Assert.Throws<InvalidOperationException>(() => db.Entities.FirstOrDefault());
    }

    class DateTimeEntity : BaseIdEntity
    {
        public DateTime aDateTime { get; set; }
    }

    class LocalDateTimeEntity : BaseIdEntity
    {
        [BsonDateTimeOptions(Kind = DateTimeKind.Local)]
        public DateTime aDateTime { get; set; }
    }

    class UtcDateTimeEntity : BaseIdEntity
    {
        [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
        public DateTime aDateTime { get; set; }
    }

    [Fact]
    public void Nullable_DateTime_round_trips_as_utc_with_expected_precision()
    {
        DateTime? expected = DateTime.UtcNow;
        var collection = Database.CreateCollection<NullableDateTimeEntity>();

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entities.Add(new NullableDateTimeEntity {aNullableDateTime = expected});
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var result = db.Entities.FirstOrDefault();
            Assert.NotNull(result);
            Assert.NotNull(result.aNullableDateTime);
            Assert.Equal(expected.Value.ToBsonPrecision(), result.aNullableDateTime.Value.ToBsonPrecision());
        }
    }

    [Fact]
    public void Nullable_DateTime_round_trips_as_null()
    {
        DateTime? expected = null;
        var collection = Database.CreateCollection<NullableDateTimeEntity>();

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entities.Add(new NullableDateTimeEntity {aNullableDateTime = expected});
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var result = db.Entities.FirstOrDefault();
            Assert.NotNull(result);
            Assert.Null(result.aNullableDateTime);
        }
    }

    [Fact]
    public void Missing_nullable_DateTime_defaults_to_null()
    {
        var collection = SetupIdOnlyCollection<NullableDateTimeEntity>();
        using var db = SingleEntityDbContext.Create(collection);

        var result = db.Entities.FirstOrDefault();
        Assert.NotNull(result);
        Assert.Null(result.aNullableDateTime);
    }

    class NullableDateTimeEntity : BaseIdEntity
    {
        public DateTime? aNullableDateTime { get; set; }
    }

    [Fact]
    public void DateTimeOffset_round_trips()
    {
        var expected = DateTimeOffset.Now;
        var collection = Database.CreateCollection<DateTimeOffsetEntity>();

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entities.Add(new DateTimeOffsetEntity {aDateTimeOffset = expected});
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var result = db.Entities.FirstOrDefault();
            Assert.NotNull(result);
            Assert.Equal(expected, result.aDateTimeOffset);
        }
    }

    [Fact]
    public void Missing_DateTimeOffset_throws()
    {
        var collection = SetupIdOnlyCollection<DateTimeOffsetEntity>();
        using var db = SingleEntityDbContext.Create(collection);

        Assert.Throws<InvalidOperationException>(() => db.Entities.FirstOrDefault());
    }

    class DateTimeOffsetEntity : BaseIdEntity
    {
        public DateTimeOffset aDateTimeOffset { get; set; }
    }

    [Fact]
    public void Nullable_DateTimeOffset_round_trips_as_utc_with_expected_precision()
    {
        DateTimeOffset? expected = DateTimeOffset.Now;
        var collection = Database.CreateCollection<NullableDateTimeOffsetEntity>();

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entities.Add(new NullableDateTimeOffsetEntity {aNullableDateTimeOffset = expected});
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var result = db.Entities.FirstOrDefault();
            Assert.NotNull(result);
            Assert.NotNull(result.aNullableDateTimeOffset);
            Assert.Equal(expected.Value, result.aNullableDateTimeOffset.Value);
        }
    }

    [Fact]
    public void Nullable_DateTimeOffset_round_trips_as_null()
    {
        DateTimeOffset? expected = null;
        var collection = Database.CreateCollection<NullableDateTimeOffsetEntity>();

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entities.Add(new NullableDateTimeOffsetEntity {aNullableDateTimeOffset = expected});
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var result = db.Entities.FirstOrDefault();
            Assert.NotNull(result);
            Assert.Null(result.aNullableDateTimeOffset);
        }
    }

    [Fact]
    public void Missing_nullable_DateTimeOffset_defaults_to_null()
    {
        var collection = SetupIdOnlyCollection<NullableDateTimeOffsetEntity>();
        using var db = SingleEntityDbContext.Create(collection);

        var result = db.Entities.FirstOrDefault();
        Assert.NotNull(result);
        Assert.Null(result.aNullableDateTimeOffset);
    }

    class NullableDateTimeOffsetEntity : BaseIdEntity
    {
        public DateTimeOffset? aNullableDateTimeOffset { get; set; }
    }


    [Fact]
    public void TimeSpan_round_trips()
    {
        var expected = TimeSpan.FromTicks(Random.Shared.NextInt64());
        var collection = Database.CreateCollection<TimeSpanEntity>();

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entities.Add(new TimeSpanEntity {aTimeSpan = expected});
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var result = db.Entities.FirstOrDefault();
            Assert.NotNull(result);
            Assert.Equal(expected, result.aTimeSpan);
        }
    }

    [Fact]
    public void Missing_TimeSpan_throws()
    {
        var collection = SetupIdOnlyCollection<TimeSpanEntity>();
        using var db = SingleEntityDbContext.Create(collection);

        Assert.Throws<InvalidOperationException>(() => db.Entities.FirstOrDefault());
    }

    class TimeSpanEntity : BaseIdEntity
    {
        public TimeSpan aTimeSpan { get; set; }
    }

    [Theory]
    [InlineData(1234567L)]
    [InlineData(-1234L)]
    [InlineData(null)]
    public void Nullable_TimeSpan_round_trips(long? expectedTicks)
    {
        TimeSpan? expected = expectedTicks == null ? null : TimeSpan.FromTicks(expectedTicks.Value);

        var collection = Database.CreateCollection<NullableTimeSpanEntity>(values: expected);

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entities.Add(new NullableTimeSpanEntity {aNullableTimeSpan = expected});
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var result = db.Entities.FirstOrDefault();
            Assert.NotNull(result);
            Assert.Equal(expected, result.aNullableTimeSpan);
        }
    }

    [Fact]
    public void Missing_nullable_TimeSpan_defaults_to_null()
    {
        var collection = SetupIdOnlyCollection<NullableTimeSpanEntity>();
        using var db = SingleEntityDbContext.Create(collection);

        var result = db.Entities.FirstOrDefault();
        Assert.NotNull(result);
        Assert.Null(result.aNullableTimeSpan);
    }

    class NullableTimeSpanEntity : BaseIdEntity
    {
        public TimeSpan? aNullableTimeSpan { get; set; }
    }

    [Fact]
    public void DateOnly_round_trips()
    {
        var expected = new DateOnly(2024, 10, 20);
        var collection = Database.CreateCollection<DateOnlyEntity>();

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entities.Add(new DateOnlyEntity {aDateOnly = expected});
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var result = db.Entities.FirstOrDefault();
            Assert.NotNull(result);
            Assert.Equal(expected, result.aDateOnly);
        }
    }

    [Fact]
    public void Nullable_DateOnly_round_trips()
    {
        var expected = new DateOnly(2024, 10, 20);

        var collection = Database.CreateCollection<NullableDateOnlyEntity>(values: expected);

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entities.Add(new NullableDateOnlyEntity { aNullableDateOnly = expected});
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var result = db.Entities.FirstOrDefault();
            Assert.NotNull(result);
            Assert.Equal(expected, result.aNullableDateOnly);
        }
    }

    [Fact]
    public void Missing_nullable_DateOnly_defaults_to_null()
    {
        var collection = SetupIdOnlyCollection<NullableDateOnlyEntity>();
        using var db = SingleEntityDbContext.Create(collection);

        var result = db.Entities.FirstOrDefault();
        Assert.NotNull(result);
        Assert.Null(result.aNullableDateOnly);
    }

    class DateOnlyEntity : BaseIdEntity
    {
        public DateOnly aDateOnly { get; set; }
    }

    class NullableDateOnlyEntity: BaseIdEntity
    {
        public DateOnly? aNullableDateOnly { get; set; }
    }

    [Fact]
    public void TimeOnly_round_trips()
    {
        var expected = new TimeOnly(12, 10, 20);
        var collection = Database.CreateCollection<TimeOnlyEntity>();

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entities.Add(new TimeOnlyEntity { aTimeOnly = expected });
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var result = db.Entities.FirstOrDefault();
            Assert.NotNull(result);
            Assert.Equal(expected, result.aTimeOnly);
        }
    }

    [Fact]
    public void Nullable_TimeOnly_round_trips()
    {
        var expected = new TimeOnly(12, 10, 20);
        var collection = Database.CreateCollection<NullableTimeOnlyEntity>(values: expected);

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entities.Add(new NullableTimeOnlyEntity { aNullableTimeOnly = expected});
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var result = db.Entities.FirstOrDefault();
            Assert.NotNull(result);
            Assert.Equal(expected, result.aNullableTimeOnly);
        }
    }

    [Fact]
    public void Missing_nullable_TimeOnly_defaults_to_null()
    {
        var collection = SetupIdOnlyCollection<NullableTimeOnlyEntity>();
        using var db = SingleEntityDbContext.Create(collection);

        var result = db.Entities.FirstOrDefault();
        Assert.NotNull(result);
        Assert.Null(result.aNullableTimeOnly);
    }

    class TimeOnlyEntity : BaseIdEntity
    {
        public TimeOnly aTimeOnly { get; set; }
    }

    class NullableTimeOnlyEntity: BaseIdEntity
    {
        public TimeOnly? aNullableTimeOnly { get; set; }
    }
}
