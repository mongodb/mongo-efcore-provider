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

using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.Diagnostics;
using MongoDB.EntityFrameworkCore.Infrastructure;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

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

    // UtcDateTime alone under NativeOnly: sibling members (TimeOfDay) in Select_DateTimeOffset_remaining_components make that query fall back.
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

        // DateTimeOffset.UtcNow.Year is a static MemberExpression with a null Expression; it must decline, not throw NullReferenceException.
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

    private SingleEntityDbContext<DateTimeOffsetEntity> CreateModeContext(MongoQueryMode mode, string name) =>
        SingleEntityDbContext.Create(CreateSeededCollection(name), optionsBuilderAction: b =>
        {
            b.ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.ManyServiceProvidersCreatedWarning));
            new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
        });

    [Theory, InlineData(MongoQueryMode.Native), InlineData(MongoQueryMode.DriverLinq)]
    public void Select_DateTimeOffset_ToString_matches_client_evaluation(MongoQueryMode mode)
    {
        using var db = CreateModeContext(mode, $"{nameof(Select_DateTimeOffset_ToString_matches_client_evaluation)}_{mode}");

        var result = db.Entities.Select(e => e.DateTimeOffset.ToString()).Single();

        Assert.Equal(TestValue.ToString(), result);
    }

    [Theory, InlineData(MongoQueryMode.Native), InlineData(MongoQueryMode.DriverLinq)]
    public void Select_DateTimeOffset_ToString_format_matches_client_evaluation(MongoQueryMode mode)
    {
        using var db = CreateModeContext(mode, $"{nameof(Select_DateTimeOffset_ToString_format_matches_client_evaluation)}_{mode}");

        var result = db.Entities.Select(e => e.DateTimeOffset.ToString("o")).Single();

        Assert.Equal(TestValue.ToString("o"), result);
    }

    [Theory, InlineData(MongoQueryMode.Native), InlineData(MongoQueryMode.DriverLinq)]
    public void Select_optional_DateTimeOffset_Value_ToString_matches_client_evaluation(MongoQueryMode mode)
    {
        using var db = CreateModeContext(mode, $"{nameof(Select_optional_DateTimeOffset_Value_ToString_matches_client_evaluation)}_{mode}");

        var result = db.Entities.Select(e => e.OptionalDateTimeOffset!.Value.ToString()).Single();

        Assert.Equal(TestValue.ToString(), result);
    }

    [Theory, InlineData(MongoQueryMode.Native), InlineData(MongoQueryMode.DriverLinq)]
    public void Select_DateTimeOffset_ToString_in_anonymous_projection_matches_client_evaluation(MongoQueryMode mode)
    {
        using var db = CreateModeContext(mode, $"{nameof(Select_DateTimeOffset_ToString_in_anonymous_projection_matches_client_evaluation)}_{mode}");

        var result = db.Entities.Select(e => new { e.Id, S = e.DateTimeOffset.ToString() }).Single();

        Assert.Equal(SeedEntity.Id, result.Id);
        Assert.Equal(TestValue.ToString(), result.S);
    }

    [Theory, InlineData(MongoQueryMode.Native), InlineData(MongoQueryMode.DriverLinq)]
    public void Where_DateTimeOffset_ToString_throws_instead_of_matching_bson_json(MongoQueryMode mode)
    {
        using var db = CreateModeContext(mode, $"{nameof(Where_DateTimeOffset_ToString_throws_instead_of_matching_bson_json)}_{mode}");
        var expected = TestValue.ToString();

        Assert.Throws<InvalidOperationException>(() => db.Entities.Where(e => e.DateTimeOffset.ToString() == expected).ToList());
    }

    [Theory, InlineData(MongoQueryMode.Native), InlineData(MongoQueryMode.DriverLinq)]
    public void Select_DateTimeOffset_ToString_consumed_by_server_computation_throws(MongoQueryMode mode)
    {
        using var db = CreateModeContext(mode, $"{nameof(Select_DateTimeOffset_ToString_consumed_by_server_computation_throws)}_{mode}");

        Assert.Throws<InvalidOperationException>(() => db.Entities.Select(e => e.DateTimeOffset.ToString().Length).Single());
    }

    // A boxed receiver (`((object)x.Dto).ToString()`) is the same call: evaluated client-side over the typed read
    // rather than rendered as $toString over the stored sub-document, which yields BSON JSON (EF-217).
    [Theory, InlineData(MongoQueryMode.Native), InlineData(MongoQueryMode.DriverLinq), InlineData(MongoQueryMode.NativeOnly)]
    public void Select_boxed_DateTimeOffset_ToString_matches_client_evaluation(MongoQueryMode mode)
    {
        using var db = CreateModeContext(mode, $"{nameof(Select_boxed_DateTimeOffset_ToString_matches_client_evaluation)}_{mode}");

        AssertCorrectOrNativeOnlyDecline(
            mode, TestValue.ToString(), () => db.Entities.Select(e => ((object)e.DateTimeOffset).ToString()).Single()!);
        AssertCorrectOrNativeOnlyDecline(
            mode, TestValue.ToString(),
            () => db.Entities.Select(e => new { e.Id, S = ((object)e.DateTimeOffset).ToString() }).Single().S!);
        AssertCorrectOrNativeOnlyDecline(
            mode, TestValue.ToString(), () => db.Entities.Select(e => ((object?)e.OptionalDateTimeOffset)!.ToString()).Single()!);
    }

    [Theory, InlineData(MongoQueryMode.Native), InlineData(MongoQueryMode.DriverLinq), InlineData(MongoQueryMode.NativeOnly)]
    public void Where_boxed_DateTimeOffset_ToString_throws_instead_of_matching_bson_json(MongoQueryMode mode)
    {
        using var db = CreateModeContext(mode, $"{nameof(Where_boxed_DateTimeOffset_ToString_throws_instead_of_matching_bson_json)}_{mode}");
        var expected = TestValue.ToString();

        var exception = AssertLoud(mode, () => db.Entities.Where(e => ((object)e.DateTimeOffset).ToString() == expected).Count());
        if (mode != MongoQueryMode.NativeOnly)
        {
            Assert.Contains("DateTimeOffset.ToString()", exception.Message);
        }
    }

    public static TheoryData<string, MongoQueryMode> ConcatenationShapes => CreateConcatenationShapes();

    private static TheoryData<string, MongoQueryMode> CreateConcatenationShapes()
    {
        var data = new TheoryData<string, MongoQueryMode>();
        foreach (var shape in ConcatenationQueries.Keys)
        {
            foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq, MongoQueryMode.NativeOnly })
            {
                data.Add(shape, mode);
            }
        }

        return data;
    }

    // Each concatenation boxes the DateTimeOffset operand (the object overloads of string.Concat), which both paths
    // render as $toString over the stored sub-document: BSON JSON, or no matching rows in a predicate (EF-217).
    private static readonly Dictionary<string, Func<IQueryable<DateTimeOffsetEntity>, object>> ConcatenationQueries = new()
    {
        ["prefix"] = q => q.Select(e => "a" + e.DateTimeOffset).Single(),
        ["suffix"] = q => q.Select(e => e.DateTimeOffset + "x").Single(),
        ["nullable"] = q => q.Select(e => "a" + e.OptionalDateTimeOffset).Single(),
        ["nested"] = q => q.Select(e => "a" + ("b" + e.DateTimeOffset)).Single(),
        ["concat_two"] = q => q.Select(e => string.Concat(e.DateTimeOffset, e.DateTimeOffset)).Single(),
        ["concat_three"] = q => q.Select(e => string.Concat("a", e.DateTimeOffset, "b")).Single(),
        ["concat_array"] = q => q.Select(e => string.Concat(new object[] { "a", e.DateTimeOffset, "b", "c", "d" })).Single(),
        ["anonymous"] = q => q.Select(e => new { e.Id, S = "a" + e.DateTimeOffset }).Single().S,
        ["where"] = q => q.Where(e => "a" + e.DateTimeOffset == "a" + TestValue.ToString()).Count(),
        ["order_by"] = q => q.OrderBy(e => "a" + e.DateTimeOffset).Select(e => e.Id).ToList(),
    };

    [Theory, MemberData(nameof(ConcatenationShapes))]
    public void Concatenating_DateTimeOffset_throws_instead_of_bson_json(string shape, MongoQueryMode mode)
    {
        using var db = CreateModeContext(mode, $"Concatenating_DateTimeOffset_{shape}_{mode}");

        var exception = AssertLoud(mode, () => ConcatenationQueries[shape](db.Entities));
        if (mode != MongoQueryMode.NativeOnly)
        {
            Assert.Contains("Concatenating a DateTimeOffset", exception.Message);
        }
    }

    // With a whole-entity leaf the shaper reads whole documents and evaluates the concatenation over the typed read.
    [Theory, InlineData(MongoQueryMode.Native), InlineData(MongoQueryMode.DriverLinq), InlineData(MongoQueryMode.NativeOnly)]
    public void Select_entity_and_concatenated_DateTimeOffset_matches_client_evaluation(MongoQueryMode mode)
    {
        using var db = CreateModeContext(mode, $"{nameof(Select_entity_and_concatenated_DateTimeOffset_matches_client_evaluation)}_{mode}");

        AssertCorrectOrNativeOnlyDecline(
            mode, "a" + TestValue, () => db.Entities.Select(e => new { e, S = "a" + e.DateTimeOffset }).Single().S);
    }

    [Theory, InlineData(MongoQueryMode.Native), InlineData(MongoQueryMode.DriverLinq), InlineData(MongoQueryMode.NativeOnly)]
    public void Formatting_DateTimeOffset_throws(MongoQueryMode mode)
    {
        using var db = CreateModeContext(mode, $"{nameof(Formatting_DateTimeOffset_throws)}_{mode}");

        foreach (var query in new Expression<Func<DateTimeOffsetEntity, string>>[]
                 {
                     e => string.Format("{0}", e.DateTimeOffset),
                     e => $"{e.DateTimeOffset}",
                     e => $"{e.DateTimeOffset:o}"
                 })
        {
            if (mode == MongoQueryMode.NativeOnly)
            {
                Assert.Throws<NativeTranslationNotSupportedException>(() => db.Entities.Select(query).Single());
            }
            else
            {
                Assert.Throws<MongoDB.Driver.Linq.ExpressionNotSupportedException>(() => db.Entities.Select(query).Single());
            }
        }
    }

    // NativeOnly declines these shapes (the result is computed client-side); the other modes answer as .NET does.
    private static void AssertCorrectOrNativeOnlyDecline(MongoQueryMode mode, string expected, Func<string> query)
    {
        if (mode == MongoQueryMode.NativeOnly)
        {
            Assert.Throws<NativeTranslationNotSupportedException>(query);
        }
        else
        {
            Assert.Equal(expected, query());
        }
    }

    private static Exception AssertLoud(MongoQueryMode mode, Func<object> query)
        => mode == MongoQueryMode.NativeOnly
            ? Assert.Throws<NativeTranslationNotSupportedException>(query)
            : Assert.Throws<InvalidOperationException>(query);

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
