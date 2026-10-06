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
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.Diagnostics;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

[XUnitCollection("QueryTests")]
public class DateTimeOffsetMemberProjectionTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    // A non-zero, non-round offset so UTC-vs-local math is actually exercised by the test.
    private static readonly DateTimeOffset TestValue =
        new(2024, 3, 15, 13, 45, 30, 250, TimeSpan.FromHours(-5));

    private static readonly DateTimeOffsetEntity SeedEntity = new()
    {
        Id = ObjectId.GenerateNewId(),
        DateTimeOffset = TestValue,
        OptionalDateTimeOffset = TestValue
    };

    private IMongoCollection<DateTimeOffsetEntity> CreateSeededCollection([System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        var collection = database.CreateCollection<DateTimeOffsetEntity>(name);
        collection.InsertOne(SeedEntity);
        return collection;
    }

    [Fact]
    public void Select_DateTimeOffset_DateTime_component()
    {
        using var db = SingleEntityDbContext.Create(CreateSeededCollection());

        var result = db.Entities.Select(e => e.DateTimeOffset.DateTime).Single();

        Assert.Equal(TestValue.DateTime, result);
    }

    [Fact]
    public void Select_DateTimeOffset_Date_component()
    {
        using var db = SingleEntityDbContext.Create(CreateSeededCollection());

        var result = db.Entities.Select(e => e.DateTimeOffset.Date).Single();

        Assert.Equal(TestValue.Date, result);
    }

    [Fact]
    public void Select_DateTimeOffset_Year_component()
    {
        using var db = SingleEntityDbContext.Create(CreateSeededCollection());

        var result = db.Entities.Select(e => e.DateTimeOffset.Year).Single();

        Assert.Equal(TestValue.Year, result);
    }

    [Fact]
    public void Where_DateTimeOffset_DateTime_component()
    {
        using var db = SingleEntityDbContext.Create(CreateSeededCollection());

        var result = db.Entities.Where(e => e.DateTimeOffset.DateTime == TestValue.DateTime).Single();

        Assert.Equal(SeedEntity.Id, result.Id);
    }

    [Fact]
    public void Select_optional_DateTimeOffset_Value_DateTime_Date_component()
    {
        using var db = SingleEntityDbContext.Create(CreateSeededCollection());

        var result = db.Entities
            .Select(e => e.OptionalDateTimeOffset == null ? (DateTime?)null : e.OptionalDateTimeOffset.Value.DateTime.Date)
            .Single();

        Assert.Equal(TestValue.Date, result);
    }

    [Fact]
    public void Select_optional_DateTimeOffset_null_short_circuits()
    {
        var entityWithNull = new DateTimeOffsetEntity
        {
            Id = ObjectId.GenerateNewId(),
            DateTimeOffset = TestValue,
            OptionalDateTimeOffset = null
        };
        var collection = database.CreateCollection<DateTimeOffsetEntity>();
        collection.InsertOne(entityWithNull);
        using var db = SingleEntityDbContext.Create(collection);

        var result = db.Entities
            .Select(e => e.OptionalDateTimeOffset == null ? (DateTime?)null : e.OptionalDateTimeOffset.Value.DateTime.Date)
            .Single();

        Assert.Null(result);
    }

    [Fact]
    public void Select_DateTimeOffset_DateTime_component_with_string_representation_throws()
    {
        var collection = database.CreateCollection<DateTimeOffsetEntity>();
        collection.InsertOne(SeedEntity);
        using var db = SingleEntityDbContext.Create(collection, modelBuilderAction: mb =>
            mb.Entity<DateTimeOffsetEntity>().Property(e => e.DateTimeOffset).HasBsonRepresentation(BsonType.String));

        Assert.Throws<NotSupportedException>(() => db.Entities.Select(e => e.DateTimeOffset.DateTime).Single());
    }

    [Fact]
    public void Select_DateTimeOffset_DateTime_component_with_value_converter_throws()
    {
        var collection = database.CreateCollection<DateTimeOffsetEntity>();
        collection.InsertOne(SeedEntity);
        using var db = SingleEntityDbContext.Create(collection, modelBuilderAction: mb =>
            mb.Entity<DateTimeOffsetEntity>().Property(e => e.DateTimeOffset)
                .HasConversion(v => v.Ticks, v => new DateTimeOffset(v, TimeSpan.Zero)));

        Assert.Throws<NotSupportedException>(() => db.Entities.Select(e => e.DateTimeOffset.DateTime).Single());
    }

    [Fact]
    public void Select_static_DateTimeOffset_UtcNow_Year_does_not_throw_NullReferenceException()
    {
        using var db = SingleEntityDbContext.Create(CreateSeededCollection());

        // DateTimeOffset.UtcNow.Year reaches TryResolveDateTimeOffsetElementAccess as a static MemberExpression
        // with a null Expression. A prior version null-forgave that and threw NullReferenceException; the fix
        // falls through to `return false` instead, letting the query translate/evaluate normally. The
        // regression guarded against is specifically the NullReferenceException, not any particular exception.
        var exception = Record.Exception(() => db.Entities.Select(e => DateTimeOffset.UtcNow.Year).Single());

        Assert.False(exception is NullReferenceException,
            $"Expected no NullReferenceException; got {exception?.GetType().FullName ?? "no exception"}.");
    }

    [Fact]
    public void Select_DateTimeOffset_DateTime_component_renders_as_server_side_mql()
    {
        var (loggerFactory, spyLogger) = SpyLoggerProvider.Create();
        var collection = CreateSeededCollection();
        using var db = SingleEntityDbContext.Create(collection, loggerFactory,
            optionsBuilderAction: b => b.EnableSensitiveDataLogging());

        var result = db.Entities.Select(e => e.DateTimeOffset.DateTime).Single();

        Assert.Equal(TestValue.DateTime, result);

        // Confirms the rewrite actually renders as a server-side aggregation expression (reading the
        // stored DateTime/Offset sub-fields and reconstructing via $dateAdd) rather than silently
        // falling back to client-side evaluation and coincidentally producing the right value.
        var message = spyLogger.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery);
        Assert.Contains("Executed MQL query", message);
        Assert.Contains("\"$dateAdd\"", message);
        Assert.Contains("\"startDate\" : \"$DateTimeOffset.DateTime\"", message);
        Assert.Contains("\"unit\" : \"minute\"", message);
        Assert.Contains("\"amount\" : \"$DateTimeOffset.Offset\"", message);
    }

    [Fact]
    public void Select_DateTimeOffset_remaining_components()
    {
        using var db = SingleEntityDbContext.Create(CreateSeededCollection());

        var result = db.Entities.Select(e => new
        {
            e.DateTimeOffset.Month,
            e.DateTimeOffset.Day,
            e.DateTimeOffset.Hour,
            e.DateTimeOffset.Minute,
            e.DateTimeOffset.Second,
            e.DateTimeOffset.Millisecond,
            e.DateTimeOffset.DayOfWeek,
            e.DateTimeOffset.DayOfYear,
            e.DateTimeOffset.TimeOfDay,
            e.DateTimeOffset.UtcDateTime
        }).Single();

        // Scalar member components work correctly
        Assert.Equal(TestValue.Month, result.Month);
        Assert.Equal(TestValue.Day, result.Day);
        Assert.Equal(TestValue.Hour, result.Hour);
        Assert.Equal(TestValue.Minute, result.Minute);
        Assert.Equal(TestValue.Second, result.Second);
        Assert.Equal(TestValue.Millisecond, result.Millisecond);
        Assert.Equal(TestValue.DayOfWeek, result.DayOfWeek);
        Assert.Equal(TestValue.DayOfYear, result.DayOfYear);
        Assert.Equal(TestValue.TimeOfDay, result.TimeOfDay);

        Assert.Equal(TestValue.UtcDateTime, result.UtcDateTime);
    }

    [Fact]
    public void Select_DateTimeOffset_LocalDateTime_throws()
    {
        using var db = SingleEntityDbContext.Create(CreateSeededCollection());

        // Depends on the executing machine's time zone, which the server cannot know.
        var exception = Assert.Throws<NotSupportedException>(() => db.Entities.Select(e => e.DateTimeOffset.LocalDateTime).Single());
        Assert.Contains("LocalDateTime", exception.Message);
    }

    [Fact]
    public void Where_DateTimeOffset_DateTime_component_with_non_utc_host_zone_value()
    {
        // A July value: hosts in zones observing DST (e.g. London, New York) have a non-zero UTC offset here, so an
        // Unspecified-kind comparison constant mistakenly converted with the host zone would not match (EF-473).
        var value = new DateTimeOffset(2024, 7, 15, 13, 45, 30, 250, TimeSpan.FromHours(1));
        var collection = database.CreateCollection<DateTimeOffsetEntity>();
        collection.InsertOne(new DateTimeOffsetEntity { Id = ObjectId.GenerateNewId(), DateTimeOffset = value });
        using var db = SingleEntityDbContext.Create(collection);

        Assert.Single(db.Entities.Where(e => e.DateTimeOffset.DateTime == value.DateTime));
        Assert.Single(db.Entities.Where(e => value.DateTime == e.DateTimeOffset.DateTime));
        Assert.Single(db.Entities.Where(e => e.DateTimeOffset.DateTime >= value.DateTime));
        Assert.Empty(db.Entities.Where(e => e.DateTimeOffset.DateTime > value.DateTime));
        Assert.Single(db.Entities.Where(e => e.DateTimeOffset.Date == value.Date));
    }

    [Fact]
    public void Select_and_Where_DateTimeOffset_component_of_composite_key()
    {
        var value = new DateTimeOffset(2024, 3, 15, 13, 45, 30, 250, TimeSpan.FromHours(-5));
        var collection = database.CreateCollection<CompositeKeyDateTimeOffsetEntity>();
        using var db = SingleEntityDbContext.Create(collection,
            modelBuilderAction: mb => mb.Entity<CompositeKeyDateTimeOffsetEntity>().HasKey(e => new { e.K1, e.K2 }));
        db.Entities.AddRange(
            new CompositeKeyDateTimeOffsetEntity { K1 = 1, K2 = value },
            new CompositeKeyDateTimeOffsetEntity { K1 = 2, K2 = value.AddYears(-1) });
        db.SaveChanges();
        db.ChangeTracker.Clear();

        Assert.Equal([2023, 2024], db.Entities.Select(e => e.K2.Year).ToList().Order());
        Assert.Equal(1, db.Entities.Where(e => e.K2.Year == 2024).Single().K1);
        Assert.Equal(value.DateTime, db.Entities.Where(e => e.K1 == 1).Select(e => e.K2.DateTime).Single());
    }
}

public class DateTimeOffsetEntity
{
    public ObjectId Id { get; set; }
    public DateTimeOffset DateTimeOffset { get; set; }
    public DateTimeOffset? OptionalDateTimeOffset { get; set; }
}

public class CompositeKeyDateTimeOffsetEntity
{
    public int K1 { get; set; }
    public DateTimeOffset K2 { get; set; }
}
