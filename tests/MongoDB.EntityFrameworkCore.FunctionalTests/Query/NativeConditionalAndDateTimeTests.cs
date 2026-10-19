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
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Infrastructure;

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
        // A single parameterized test asserting each part's SQL-level correctness would need per-part
        // projection expressions, which C# cannot build from a string at compile time — so this drives one
        // fixed projection covering the part under test via a switch, keeping the test data/oracle shared.
        //
        // NOTE: the projection below returns each part's own natural type (int, or DayOfWeek) and boxes to
        // `object` only AFTER materialization (outside the LINQ expression tree), not inside the `Select`.
        // Boxing INSIDE the tree (`Select(x => (object)x.Foo.Year)`) compiles to a `Convert(..., typeof(object))`
        // node that reaches the native translator, which has no `$toX` target for `object`
        // (`MongoConvertExpression.ToOperatorFor` only maps int/long/double/decimal) — so it declines the whole
        // projection under `NativeOnly`. That is a pre-existing, unrelated gap in boxing-cast support (it
        // affects a boxed PLAIN field leaf identically, not just a date-part leaf), so working around it here in
        // the test is the correct fix rather than patching the translator for a shape outside this task's scope.
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
