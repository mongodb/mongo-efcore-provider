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

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Infrastructure;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

[XUnitCollection("QueryTests")]
public class NativeConditionalAndDateTimeTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    public class Row
    {
        public ObjectId Id { get; set; }
        public string Label { get; set; } = "";
        public bool Flag { get; set; }
        public DateTime Occurred { get; set; }
        public DateTimeOffset? OccurredOffset { get; set; }
    }

    // Positive offset (+02:00), negative offset (-05:00), and a null OccurredOffset row — covers the
    // conditional's null branch and both reconstruction directions.
    private static readonly (string Label, bool Flag, DateTime Occurred, DateTimeOffset? OccurredOffset)[] Rows =
    [
        ("a", true, new DateTime(2024, 3, 10, 23, 30, 15, DateTimeKind.Utc),
            new DateTimeOffset(2024, 3, 10, 23, 30, 15, TimeSpan.FromHours(2))),
        ("b", false, new DateTime(2024, 3, 10, 1, 15, 45, DateTimeKind.Utc),
            new DateTimeOffset(2024, 3, 10, 1, 15, 45, TimeSpan.FromHours(-5))),
        ("c", true, new DateTime(2024, 3, 10, 12, 0, 0, DateTimeKind.Utc), null)
    ];

    private IMongoCollection<Row> Seed(string name)
    {
        var collection = database.CreateCollection<Row>(name);
        collection.InsertMany(Rows.Select(r => new Row
        {
            Label = r.Label, Flag = r.Flag, Occurred = r.Occurred, OccurredOffset = r.OccurredOffset
        }));
        return collection;
    }

    private static SingleEntityDbContext<Row> CreateContext(IMongoCollection<Row> collection, MongoQueryMode mode)
        => SingleEntityDbContext.Create(
            collection,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });

    [Fact]
    public void Conditional_null_check_over_DateTimeOffset_matches_across_all_three_modes()
    {
        var collection = Seed(nameof(Conditional_null_check_over_DateTimeOffset_matches_across_all_three_modes));

        using var nativeOnly = CreateContext(collection, MongoQueryMode.NativeOnly);
        var nativeOnlyResult = nativeOnly.Entities.AsNoTracking().OrderBy(x => x.Label)
            .Select(x => new { x.Label, DT = x.OccurredOffset == null ? (DateTime?)null : x.OccurredOffset.Value.DateTime.Date })
            .ToList();

        using var native = CreateContext(collection, MongoQueryMode.Native);
        var nativeResult = native.Entities.AsNoTracking().OrderBy(x => x.Label)
            .Select(x => new { x.Label, DT = x.OccurredOffset == null ? (DateTime?)null : x.OccurredOffset.Value.DateTime.Date })
            .ToList();

        using var driverLinq = CreateContext(collection, MongoQueryMode.DriverLinq);
        var driverLinqResult = driverLinq.Entities.AsNoTracking().OrderBy(x => x.Label)
            .Select(x => new { x.Label, DT = x.OccurredOffset == null ? (DateTime?)null : x.OccurredOffset.Value.DateTime.Date })
            .ToList();

        var inMemoryResult = Rows.OrderBy(r => r.Label)
            .Select(r => (r.Label, DT: r.OccurredOffset == null ? (DateTime?)null : r.OccurredOffset.Value.DateTime.Date))
            .ToList();

        Assert.Equal(inMemoryResult, nativeOnlyResult.Select(r => (r.Label, r.DT)));
        Assert.Equal(nativeOnlyResult.Select(r => (r.Label, r.DT)), nativeResult.Select(r => (r.Label, r.DT)));
        Assert.Equal(nativeResult.Select(r => (r.Label, r.DT)), driverLinqResult.Select(r => (r.Label, r.DT)));
    }

    [Theory]
    [InlineData("Year")]
    [InlineData("Month")]
    [InlineData("Day")]
    [InlineData("Hour")]
    [InlineData("Minute")]
    [InlineData("Second")]
    [InlineData("DayOfWeek")]
    [InlineData("DayOfYear")]
    public void DateTimeOffset_date_part_matches_in_memory_LINQ_under_NativeOnly(string part)
    {
        // One fixed projection per part via a switch, since a per-part expression can't be built from a string.
        // Each part is projected in its natural type and boxed only after materialization: a boxing cast inside the
        // tree (`(object)x.Foo.Year`) has no `$toX` target (MongoConvertExpression.ToOperatorFor) and declines
        // under NativeOnly for any leaf, an unrelated gap.
        var collection = Seed(nameof(DateTimeOffset_date_part_matches_in_memory_LINQ_under_NativeOnly) + part);
        using var nativeOnly = CreateContext(collection, MongoQueryMode.NativeOnly);

        var nonNullRows = Rows.Where(r => r.OccurredOffset is not null).ToArray();

        object[] actual = part switch
        {
            "Year" => nativeOnly.Entities.AsNoTracking().Where(x => x.OccurredOffset != null)
                .OrderBy(x => x.Label).Select(x => x.OccurredOffset!.Value.Year).ToArray().Cast<object>().ToArray(),
            "Month" => nativeOnly.Entities.AsNoTracking().Where(x => x.OccurredOffset != null)
                .OrderBy(x => x.Label).Select(x => x.OccurredOffset!.Value.Month).ToArray().Cast<object>().ToArray(),
            "Day" => nativeOnly.Entities.AsNoTracking().Where(x => x.OccurredOffset != null)
                .OrderBy(x => x.Label).Select(x => x.OccurredOffset!.Value.Day).ToArray().Cast<object>().ToArray(),
            "Hour" => nativeOnly.Entities.AsNoTracking().Where(x => x.OccurredOffset != null)
                .OrderBy(x => x.Label).Select(x => x.OccurredOffset!.Value.Hour).ToArray().Cast<object>().ToArray(),
            "Minute" => nativeOnly.Entities.AsNoTracking().Where(x => x.OccurredOffset != null)
                .OrderBy(x => x.Label).Select(x => x.OccurredOffset!.Value.Minute).ToArray().Cast<object>().ToArray(),
            "Second" => nativeOnly.Entities.AsNoTracking().Where(x => x.OccurredOffset != null)
                .OrderBy(x => x.Label).Select(x => x.OccurredOffset!.Value.Second).ToArray().Cast<object>().ToArray(),
            "DayOfWeek" => nativeOnly.Entities.AsNoTracking().Where(x => x.OccurredOffset != null)
                .OrderBy(x => x.Label).Select(x => x.OccurredOffset!.Value.DayOfWeek).ToArray().Cast<object>().ToArray(),
            "DayOfYear" => nativeOnly.Entities.AsNoTracking().Where(x => x.OccurredOffset != null)
                .OrderBy(x => x.Label).Select(x => x.OccurredOffset!.Value.DayOfYear).ToArray().Cast<object>().ToArray(),
            _ => throw new ArgumentOutOfRangeException(nameof(part))
        };

        object[] expected = part switch
        {
            "Year" => nonNullRows.OrderBy(r => r.Label).Select(r => (object)r.OccurredOffset!.Value.Year).ToArray(),
            "Month" => nonNullRows.OrderBy(r => r.Label).Select(r => (object)r.OccurredOffset!.Value.Month).ToArray(),
            "Day" => nonNullRows.OrderBy(r => r.Label).Select(r => (object)r.OccurredOffset!.Value.Day).ToArray(),
            "Hour" => nonNullRows.OrderBy(r => r.Label).Select(r => (object)r.OccurredOffset!.Value.Hour).ToArray(),
            "Minute" => nonNullRows.OrderBy(r => r.Label).Select(r => (object)r.OccurredOffset!.Value.Minute).ToArray(),
            "Second" => nonNullRows.OrderBy(r => r.Label).Select(r => (object)r.OccurredOffset!.Value.Second).ToArray(),
            "DayOfWeek" => nonNullRows.OrderBy(r => r.Label).Select(r => (object)r.OccurredOffset!.Value.DayOfWeek).ToArray(),
            "DayOfYear" => nonNullRows.OrderBy(r => r.Label).Select(r => (object)r.OccurredOffset!.Value.DayOfYear).ToArray(),
            _ => throw new ArgumentOutOfRangeException(nameof(part))
        };

        Assert.Equal(expected, actual);
    }

    // ---- DateTime.TimeOfDay ----

    public class TimeRow
    {
        public ObjectId Id { get; set; }
        public string Label { get; set; } = "";
        public DateTime When { get; set; }
        public DateTime? MaybeWhen { get; set; }
        public TimeSpan Duration { get; set; }
    }

    // Sub-second values (BSON dates keep milliseconds), one exactly at midnight, one a millisecond before it, one before
    // the Unix epoch, and a null MaybeWhen row. All Utc, as an unconfigured DateTime property materializes.
    private static readonly (string Label, DateTime When, DateTime? MaybeWhen)[] TimeRows =
    [
        ("a", new DateTime(2024, 3, 10, 23, 30, 15, 123, DateTimeKind.Utc), new DateTime(2024, 3, 10, 1, 2, 3, 456, DateTimeKind.Utc)),
        ("b", new DateTime(2024, 3, 11, 0, 0, 0, 0, DateTimeKind.Utc), new DateTime(1999, 12, 31, 23, 59, 59, 999, DateTimeKind.Utc)),
        ("c", new DateTime(2024, 3, 11, 23, 59, 59, 999, DateTimeKind.Utc), null),
        ("d", new DateTime(1970, 1, 1, 12, 0, 0, 1, DateTimeKind.Utc), new DateTime(2024, 2, 29, 12, 0, 0, 0, DateTimeKind.Utc)),
        // Before the Unix epoch: a negative stored millisecond count, still a positive time of day.
        ("e", new DateTime(1969, 12, 31, 23, 0, 0, 500, DateTimeKind.Utc), new DateTime(1969, 7, 20, 20, 17, 40, 0, DateTimeKind.Utc))
    ];

    private IMongoCollection<TimeRow> SeedTimeRows(string name)
    {
        var collection = database.CreateCollection<TimeRow>(name);
        collection.InsertMany(TimeRows.Select(r => new TimeRow
        {
            Label = r.Label, When = r.When, MaybeWhen = r.MaybeWhen, Duration = TimeSpan.FromHours(12) + TimeSpan.FromMilliseconds(1)
        }));
        return collection;
    }

    private static SingleEntityDbContext<TimeRow> CreateTimeContext(IMongoCollection<TimeRow> collection, MongoQueryMode mode)
        => SingleEntityDbContext.Create(
            collection,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });

    [Fact]
    public void TimeOfDay_bare_projection_goes_native_and_matches_the_driver()
    {
        var collection = SeedTimeRows(nameof(TimeOfDay_bare_projection_goes_native_and_matches_the_driver));

        var native = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateTimeContext(collection, mode);
            return db.Entities.AsNoTracking().OrderBy(x => x.Label).Select(x => x.When.TimeOfDay).ToList();
        });

        // Hand oracle: a unit mismatch (milliseconds read as ticks) would give 10000x smaller values.
        Assert.Equal(
            [
                new TimeSpan(0, 23, 30, 15, 123), TimeSpan.Zero, new TimeSpan(0, 23, 59, 59, 999), new TimeSpan(0, 12, 0, 0, 1),
                new TimeSpan(0, 23, 0, 0, 500)
            ],
            native);
    }

    [Fact]
    public void TimeOfDay_member_projection_goes_native_and_matches_the_driver()
    {
        var collection = SeedTimeRows(nameof(TimeOfDay_member_projection_goes_native_and_matches_the_driver));

        var native = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateTimeContext(collection, mode);
            return db.Entities.AsNoTracking().OrderBy(x => x.Label)
                .Select(x => new { x.Label, T = x.When.TimeOfDay, N = x.MaybeWhen })
                .AsEnumerable().Select(x => (x.Label, x.T)).ToList();
        });

        Assert.Equal(TimeRows.OrderBy(r => r.Label).Select(r => (r.Label, r.When.TimeOfDay)).ToList(), native);
    }

    [Fact]
    public void TimeOfDay_over_a_nullable_date_goes_native_for_non_null_rows()
    {
        var collection = SeedTimeRows(nameof(TimeOfDay_over_a_nullable_date_goes_native_for_non_null_rows));

        var native = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateTimeContext(collection, mode);
            return db.Entities.AsNoTracking().Where(x => x.MaybeWhen != null).OrderBy(x => x.Label)
                .Select(x => x.MaybeWhen!.Value.TimeOfDay).ToList();
        });

        Assert.Equal(
            TimeRows.Where(r => r.MaybeWhen != null).OrderBy(r => r.Label).Select(r => r.MaybeWhen!.Value.TimeOfDay).ToList(),
            native);
    }

    // C# (and EF) throw "Nullable object must have a value." for `.Value` over a null; the null row must not read as
    // 00:00. Hand oracle (C#'s behaviour), not driver-LINQ.
    [Fact]
    public void TimeOfDay_over_a_null_date_throws_like_CSharp()
    {
        var collection = SeedTimeRows(nameof(TimeOfDay_over_a_null_date_throws_like_CSharp));
        using var db = CreateTimeContext(collection, MongoQueryMode.NativeOnly);

        var ex = Assert.Throws<InvalidOperationException>(
            () => db.Entities.AsNoTracking().OrderBy(x => x.Label).Select(x => x.MaybeWhen!.Value.TimeOfDay).ToList());
        Assert.Equal(NullableValueMessage(), ex.Message);
    }

    // The same hazard for every other date part: `.Value.Year` over a null must throw, not read 0.
    [Fact]
    public void Year_over_a_null_date_throws_like_CSharp()
    {
        var collection = SeedTimeRows(nameof(Year_over_a_null_date_throws_like_CSharp));
        using var db = CreateTimeContext(collection, MongoQueryMode.NativeOnly);

        var ex = Assert.Throws<InvalidOperationException>(
            () => db.Entities.AsNoTracking().OrderBy(x => x.Label).Select(x => new { x.Label, Y = x.MaybeWhen!.Value.Year }).ToList());
        Assert.Equal(NullableValueMessage(), ex.Message);
    }

    [Fact]
    public void Year_over_a_nullable_date_goes_native_for_non_null_rows()
    {
        var collection = SeedTimeRows(nameof(Year_over_a_nullable_date_goes_native_for_non_null_rows));

        var native = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateTimeContext(collection, mode);
            return db.Entities.AsNoTracking().Where(x => x.MaybeWhen != null).OrderBy(x => x.Label)
                .Select(x => x.MaybeWhen!.Value.Year).ToList();
        });

        Assert.Equal([2024, 1999, 2024, 1969], native);
    }

    // Operators over the projected TimeOfDay alias. Each must either read the milliseconds correctly or decline; the
    // oracle is in-memory LINQ over the seeded rows.
    private static readonly TimeSpan SeededDuration = TimeSpan.FromHours(12) + TimeSpan.FromMilliseconds(1);

    private static List<TimeSpan> WhenTimes() => TimeRows.Select(r => r.When.TimeOfDay).ToList();

    [Fact]
    public void TimeOfDay_projection_then_Distinct_declines()
    {
        var collection = SeedTimeRows(nameof(TimeOfDay_projection_then_Distinct_declines));

        var result = NativeModeAssert.DeclinesCleanly(mode =>
        {
            using var db = CreateTimeContext(collection, mode);
            return db.Entities.AsNoTracking().Select(x => x.When.TimeOfDay).Distinct().AsEnumerable().Order().ToList();
        });

        Assert.Equal(WhenTimes().Distinct().Order().ToList(), result);
    }

    [Fact]
    public void TimeOfDay_projection_then_terminal_Max_declines()
    {
        var collection = SeedTimeRows(nameof(TimeOfDay_projection_then_terminal_Max_declines));

        var result = NativeModeAssert.DeclinesCleanly(mode =>
        {
            using var db = CreateTimeContext(collection, mode);
            return new List<TimeSpan> { db.Entities.AsNoTracking().Select(x => x.When.TimeOfDay).Max() };
        });

        Assert.Equal(new List<TimeSpan> { WhenTimes().Max() }, result);
    }

    // NativeOnly only: the Native/DriverLinq fallback for a GroupBy over a projected member crashes in EF's shaper
    // verification ("Calling 'ShapedQueryExpression.VisitChildren' is not allowed") for any member, not just TimeOfDay.
    [Fact]
    public void TimeOfDay_projection_then_GroupBy_over_it_declines()
    {
        var collection = SeedTimeRows(nameof(TimeOfDay_projection_then_GroupBy_over_it_declines));
        using var db = CreateTimeContext(collection, MongoQueryMode.NativeOnly);

        Assert.Throws<NativeTranslationNotSupportedException>(() => db.Entities.AsNoTracking()
            .Select(x => new { T = x.When.TimeOfDay }).GroupBy(a => a.T).Select(g => g.Key).ToList());
    }

    [Fact]
    public void TimeOfDay_projections_combined_by_Union_match_the_driver()
    {
        var collection = SeedTimeRows(nameof(TimeOfDay_projections_combined_by_Union_match_the_driver));

        var result = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateTimeContext(collection, mode);
            return db.Entities.AsNoTracking().Where(x => x.Label != "a").Select(x => x.When.TimeOfDay)
                .Union(db.Entities.AsNoTracking().Where(x => x.Label != "d").Select(x => x.When.TimeOfDay))
                .AsEnumerable().Order().ToList();
        });

        Assert.Equal(WhenTimes().Distinct().Order().ToList(), result);
    }

    // The two operands store the alias differently (milliseconds vs. the TimeSpan property's own string form).
    [Fact]
    public void TimeOfDay_concatenated_with_a_TimeSpan_property_declines()
    {
        var collection = SeedTimeRows(nameof(TimeOfDay_concatenated_with_a_TimeSpan_property_declines));

        var result = NativeModeAssert.DeclinesCleanly(mode =>
        {
            using var db = CreateTimeContext(collection, mode);
            return db.Entities.AsNoTracking().Select(x => x.When.TimeOfDay)
                .Concat(db.Entities.AsNoTracking().Select(x => x.Duration))
                .AsEnumerable().Order().ToList();
        });

        Assert.Equal(WhenTimes().Concat(TimeRows.Select(_ => SeededDuration)).Order().ToList(), result);
    }

    [Fact]
    public void TimeOfDay_projection_ordered_by_it_declines()
    {
        var collection = SeedTimeRows(nameof(TimeOfDay_projection_ordered_by_it_declines));

        var result = NativeModeAssert.DeclinesCleanly(mode =>
        {
            using var db = CreateTimeContext(collection, mode);
            return db.Entities.AsNoTracking().Select(x => new { x.Label, T = x.When.TimeOfDay }).OrderBy(a => a.T)
                .Select(a => a.Label).ToList();
        });

        Assert.Equal(["b", "d", "e", "a", "c"], result);
    }

    // Outside a projection leaf TimeOfDay is not translated: a filter would compare the server's milliseconds with a
    // TimeSpan rendered in its own representation, and a group key or accumulator would read the milliseconds back
    // through the generic TimeSpan serializer (as ticks).
    [Fact]
    public void TimeOfDay_in_a_filter_declines()
    {
        var collection = SeedTimeRows(nameof(TimeOfDay_in_a_filter_declines));
        var noon = TimeSpan.FromHours(12);

        var result = NativeModeAssert.DeclinesCleanly(mode =>
        {
            using var db = CreateTimeContext(collection, mode);
            return db.Entities.AsNoTracking().Where(x => x.When.TimeOfDay > noon).OrderBy(x => x.Label).Select(x => x.Label).ToList();
        });

        Assert.Equal(["a", "c", "d", "e"], result);
    }

    [Fact]
    public void TimeOfDay_group_key_declines()
    {
        var collection = SeedTimeRows(nameof(TimeOfDay_group_key_declines));

        var result = NativeModeAssert.DeclinesCleanly(mode =>
        {
            using var db = CreateTimeContext(collection, mode);
            return db.Entities.AsNoTracking().GroupBy(x => x.When.TimeOfDay).Select(g => new { g.Key, C = g.Count() })
                .AsEnumerable().OrderBy(x => x.Key).Select(x => (x.Key, x.C)).ToList();
        });

        Assert.Equal(TimeRows.GroupBy(r => r.When.TimeOfDay).OrderBy(g => g.Key).Select(g => (g.Key, g.Count())).ToList(), result);
    }

    [Fact]
    public void DateTimeOffset_TimeOfDay_projection_still_declines()
    {
        var collection = Seed(nameof(DateTimeOffset_TimeOfDay_projection_still_declines));
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly);

        Assert.Throws<NativeTranslationNotSupportedException>(
            () => db.Entities.AsNoTracking().Where(x => x.OccurredOffset != null)
                .Select(x => x.OccurredOffset!.Value.TimeOfDay).ToList());
    }

    // Every reader shape the leaf can land in must read the milliseconds, not the generic TimeSpan serializer's ticks.
    [Fact]
    public void TimeOfDay_in_a_positional_container_matches_the_driver()
    {
        var collection = SeedTimeRows(nameof(TimeOfDay_in_a_positional_container_matches_the_driver));

        var result = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateTimeContext(collection, mode);
            return db.Entities.AsNoTracking().OrderBy(x => x.Label).Select(x => new object[] { x.Label, x.When.TimeOfDay })
                .AsEnumerable().Select(a => ((string)a[0], (TimeSpan)a[1])).ToList();
        });

        Assert.Equal(TimeRows.OrderBy(r => r.Label).Select(r => (r.Label, r.When.TimeOfDay)).ToList(), result);
    }

    // A computed leaf inside a nested anonymous construction isn't native; the fallback must still read it right.
    [Fact]
    public void TimeOfDay_in_a_nested_construction_declines()
    {
        var collection = SeedTimeRows(nameof(TimeOfDay_in_a_nested_construction_declines));

        var result = NativeModeAssert.DeclinesCleanly(mode =>
        {
            using var db = CreateTimeContext(collection, mode);
            return db.Entities.AsNoTracking().OrderBy(x => x.Label).Select(x => new { x.Label, Inner = new { T = x.When.TimeOfDay } })
                .AsEnumerable().Select(a => (a.Label, a.Inner.T)).ToList();
        });

        Assert.Equal(TimeRows.OrderBy(r => r.Label).Select(r => (r.Label, r.When.TimeOfDay)).ToList(), result);
    }

    [Fact]
    public void TimeOfDay_beside_the_whole_entity_matches_the_driver()
    {
        var collection = SeedTimeRows(nameof(TimeOfDay_beside_the_whole_entity_matches_the_driver));

        var result = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateTimeContext(collection, mode);
            return db.Entities.AsNoTracking().OrderBy(x => x.Label).Select(x => new { x, T = x.When.TimeOfDay })
                .AsEnumerable().Select(a => (a.x.Label, a.T)).ToList();
        });

        Assert.Equal(TimeRows.OrderBy(r => r.Label).Select(r => (r.Label, r.When.TimeOfDay)).ToList(), result);
    }

    // The walker arm reaches a DateTimeOffset's local reconstruction too: `.Value.Year` over a null offset throws.
    [Fact]
    public void DateTimeOffset_Year_over_a_null_offset_throws_like_CSharp()
    {
        var collection = Seed(nameof(DateTimeOffset_Year_over_a_null_offset_throws_like_CSharp));
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly);

        var ex = Assert.Throws<InvalidOperationException>(
            () => db.Entities.AsNoTracking().OrderBy(x => x.Label).Select(x => new { x.Label, Y = x.OccurredOffset!.Value.Year }).ToList());
        Assert.Equal(NullableValueMessage(), ex.Message);
    }

    // A projected member set op must not combine a TimeOfDay leaf (milliseconds) with a TimeSpan property (its string
    // form): the server would dedup a long against a string, and only source1's reader applies. The decline falls back
    // to driver-LINQ, so Native returns exactly what explicit DriverLinq returns. That shared answer is itself WRONG
    // (TimeOfDay-first returns 12:00:00.001 twice; Duration-first reads the milliseconds as ticks): driver-LINQ has
    // the same mismatch, a pre-existing released behaviour, not this path's. The equality pins the decline and that
    // the fallback is the driver's own answer, not the correctness of that answer. Sorted client-side: Union order
    // is unspecified.
    [Fact]
    public void TimeOfDay_member_Union_with_a_TimeSpan_property_declines()
    {
        var collection = SeedTimeRows(nameof(TimeOfDay_member_Union_with_a_TimeSpan_property_declines));

        NativeModeAssert.DeclinesCleanly(mode =>
        {
            using var db = CreateTimeContext(collection, mode);
            return db.Entities.AsNoTracking()
                .Select(x => new { T = x.When.TimeOfDay })
                .Union(db.Entities.AsNoTracking().Select(x => new { T = x.Duration }))
                .AsEnumerable().Select(a => a.T).Order().ToList();
        });
    }

    [Fact]
    public void TimeSpan_property_member_Union_with_TimeOfDay_declines()
    {
        var collection = SeedTimeRows(nameof(TimeSpan_property_member_Union_with_TimeOfDay_declines));

        NativeModeAssert.DeclinesCleanly(mode =>
        {
            using var db = CreateTimeContext(collection, mode);
            return db.Entities.AsNoTracking()
                .Select(x => new { T = x.Duration })
                .Union(db.Entities.AsNoTracking().Select(x => new { T = x.When.TimeOfDay }))
                .AsEnumerable().Select(a => a.T).Order().ToList();
        });
    }

    // Intersect has no driver-LINQ oracle: a declined Intersect is a translation failure in every mode (NativeOnly,
    // Native, whose fallback has nothing to fall back to, and explicit DriverLinq).
    [Theory]
    [InlineData(MongoQueryMode.NativeOnly)]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.DriverLinq)]
    public void TimeOfDay_member_Intersect_with_a_TimeSpan_property_declines(MongoQueryMode mode)
    {
        var collection = SeedTimeRows(nameof(TimeOfDay_member_Intersect_with_a_TimeSpan_property_declines) + mode);
        using var db = CreateTimeContext(collection, mode);

        Assert.Throws<InvalidOperationException>(() => db.Entities.AsNoTracking()
            .Select(x => new { T = x.When.TimeOfDay })
            .Intersect(db.Entities.AsNoTracking().Select(x => new { T = x.Duration })).ToList());
    }

    [Theory]
    [InlineData(MongoQueryMode.NativeOnly)]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.DriverLinq)]
    public void TimeSpan_property_member_Intersect_with_TimeOfDay_declines(MongoQueryMode mode)
    {
        var collection = SeedTimeRows(nameof(TimeSpan_property_member_Intersect_with_TimeOfDay_declines) + mode);
        using var db = CreateTimeContext(collection, mode);

        Assert.Throws<InvalidOperationException>(() => db.Entities.AsNoTracking()
            .Select(x => new { T = x.Duration })
            .Intersect(db.Entities.AsNoTracking().Select(x => new { T = x.When.TimeOfDay })).ToList());
    }

    // Both operands TimeOfDay: both store milliseconds, and source1's reader reads them as such.
    [Fact]
    public void TimeOfDay_member_Union_with_TimeOfDay_matches_the_driver()
    {
        var collection = SeedTimeRows(nameof(TimeOfDay_member_Union_with_TimeOfDay_matches_the_driver));

        var result = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateTimeContext(collection, mode);
            return db.Entities.AsNoTracking().Where(x => x.Label != "a").Select(x => new { T = x.When.TimeOfDay })
                .Union(db.Entities.AsNoTracking().Where(x => x.Label != "d").Select(x => new { T = x.When.TimeOfDay }))
                .AsEnumerable().Select(a => a.T).Order().ToList();
        });

        Assert.Equal(WhenTimes().Distinct().Order().ToList(), result);
    }

    [Fact]
    public void TimeOfDay_member_Intersect_with_TimeOfDay_matches_the_oracle()
    {
        var collection = SeedTimeRows(nameof(TimeOfDay_member_Intersect_with_TimeOfDay_matches_the_oracle));
        using var db = CreateTimeContext(collection, MongoQueryMode.NativeOnly);

        var result = db.Entities.AsNoTracking().Where(x => x.Label != "a").Select(x => new { T = x.When.TimeOfDay })
            .Intersect(db.Entities.AsNoTracking().Where(x => x.Label != "d").Select(x => new { T = x.When.TimeOfDay }))
            .AsEnumerable().Select(a => a.T).Order().ToList();

        Assert.Equal(
            TimeRows.Where(r => r.Label != "a").Select(r => r.When.TimeOfDay)
                .Intersect(TimeRows.Where(r => r.Label != "d").Select(r => r.When.TimeOfDay)).Order().ToList(),
            result);
    }

    [Fact]
    public void Boxed_TimeOfDay_projection_declines()
    {
        var collection = SeedTimeRows(nameof(Boxed_TimeOfDay_projection_declines));

        var result = NativeModeAssert.DeclinesCleanly(mode =>
        {
            using var db = CreateTimeContext(collection, mode);
            return db.Entities.AsNoTracking().OrderBy(x => x.Label).Select(x => (object)x.When.TimeOfDay).ToList();
        });

        Assert.Equal(TimeRows.OrderBy(r => r.Label).Select(r => (object)r.When.TimeOfDay).ToList(), result);
    }

    // Review Focus #5: a DateTime-typed computed leaf over a null date must throw like C#, not read DateTime.MinValue.
    [Fact]
    public void AddDays_over_a_null_date_throws_like_CSharp()
    {
        var collection = SeedTimeRows(nameof(AddDays_over_a_null_date_throws_like_CSharp));
        using var db = CreateTimeContext(collection, MongoQueryMode.NativeOnly);

        var ex = Assert.Throws<InvalidOperationException>(() => db.Entities.AsNoTracking().OrderBy(x => x.Label)
            .Select(x => new { x.Label, D = x.MaybeWhen!.Value.AddDays(1) }).ToList());
        Assert.Equal(NullableValueMessage(), ex.Message);
    }

    // A captured non-nullable amount is never null: `x.When.AddDays(days)` over a non-nullable date has no null behind
    // its DateTime type, so a Distinct key over it is not flagged ThrowsOnNull and Max/Min over that alias stay native
    // (the $dateAdd walker arm judges a parameter amount by its CLR type). A bare `Select(x => x.When.AddDays(days))`
    // is not the host: a bare computed date doesn't bind natively at all, independent of this classification.
    [Fact]
    public void AddDays_by_a_captured_non_nullable_amount_Max_and_Min_over_a_Distinct_alias_go_native()
    {
        var collection = SeedTimeRows(nameof(AddDays_by_a_captured_non_nullable_amount_Max_and_Min_over_a_Distinct_alias_go_native));
        var days = 3;

        var result = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateTimeContext(collection, mode);
            return new List<DateTime>
            {
                db.Entities.AsNoTracking().Select(x => new { D = x.When.AddDays(days) }).Distinct().Max(a => a.D),
                db.Entities.AsNoTracking().Select(x => new { D = x.When.AddDays(days) }).Distinct().Min(a => a.D)
            };
        });

        Assert.Equal([TimeRows.Max(r => r.When.AddDays(days)), TimeRows.Min(r => r.When.AddDays(days))], result);
    }

    [Fact]
    public void AddDays_over_a_nullable_date_goes_native_for_non_null_rows()
    {
        var collection = SeedTimeRows(nameof(AddDays_over_a_nullable_date_goes_native_for_non_null_rows));

        var result = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateTimeContext(collection, mode);
            return db.Entities.AsNoTracking().Where(x => x.MaybeWhen != null).OrderBy(x => x.Label)
                .Select(x => new { x.Label, D = x.MaybeWhen!.Value.AddDays(1) }).AsEnumerable().Select(a => a.D).ToList();
        });

        Assert.Equal(TimeRows.Where(r => r.MaybeWhen != null).OrderBy(r => r.Label).Select(r => r.MaybeWhen!.Value.AddDays(1)).ToList(), result);
    }

    [Fact]
    public void DateTimeOffset_DateTime_over_a_null_offset_throws_like_CSharp()
    {
        var collection = Seed(nameof(DateTimeOffset_DateTime_over_a_null_offset_throws_like_CSharp));
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly);

        var ex = Assert.Throws<InvalidOperationException>(
            () => db.Entities.AsNoTracking().OrderBy(x => x.Label).Select(x => x.OccurredOffset!.Value.DateTime).ToList());
        Assert.Equal(NullableValueMessage(), ex.Message);
    }

    private static string NullableValueMessage()
    {
        try
        {
            _ = ((int?)null)!.Value;
        }
        catch (InvalidOperationException e)
        {
            return e.Message;
        }

        throw new InvalidOperationException("unreachable");
    }

    [Fact]
    public void Plain_DateTime_Date_matches_in_memory_LINQ_under_NativeOnly()
    {
        var collection = Seed(nameof(Plain_DateTime_Date_matches_in_memory_LINQ_under_NativeOnly));
        using var nativeOnly = CreateContext(collection, MongoQueryMode.NativeOnly);

        var actual = nativeOnly.Entities.AsNoTracking().OrderBy(x => x.Label).Select(x => x.Occurred.Date).ToList();
        var expected = Rows.OrderBy(r => r.Label).Select(r => r.Occurred.Date).ToList();

        Assert.Equal(expected, actual);
    }
}
