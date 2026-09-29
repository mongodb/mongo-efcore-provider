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
using MongoDB.EntityFrameworkCore.Infrastructure;

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

    // UtcDateTime alone, in a wrapped projection and under NativeOnly: the sibling members in
    // Select_DateTimeOffset_remaining_components (TimeOfDay) make that query fall back, which hid a native decline here.
    [Fact]
    public void Select_DateTimeOffset_UtcDateTime_goes_native()
    {
        using var db = SingleEntityDbContext.Create(CreateSeededCollection(), optionsBuilderAction: b =>
        {
            b.ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.ManyServiceProvidersCreatedWarning));
            new MongoDbContextOptionsBuilder(b).UseQueryMode(MongoQueryMode.NativeOnly);
        });

        var result = db.Entities.Select(e => new { e.Id, U = e.DateTimeOffset.UtcDateTime }).Single();

        Assert.Equal(TestValue.UtcDateTime, result.U);
        Assert.Equal(DateTimeKind.Utc, result.U.Kind);
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
        // with a null Expression, which must decline rather than throw NullReferenceException. Only the NRE is
        // asserted against.
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

        // Proves the member renders server-side ($dateAdd over the stored sub-fields) rather than evaluating
        // client-side and coincidentally producing the right value.
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
            e.DateTimeOffset.LocalDateTime,
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

        // LocalDateTime translates like DateTime (stored offset, not the machine's time zone; see
        // MongoEFToLinqTranslatingExpressionVisitor), so assert against TestValue.DateTime.
        Assert.Equal(TestValue.DateTime, result.LocalDateTime);
    }
}

public class DateTimeOffsetEntity
{
    public ObjectId Id { get; set; }
    public DateTimeOffset DateTimeOffset { get; set; }
    public DateTimeOffset? OptionalDateTimeOffset { get; set; }
}
