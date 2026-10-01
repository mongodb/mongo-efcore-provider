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
using Microsoft.EntityFrameworkCore.Diagnostics;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.Infrastructure;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// EF-255: a non-deterministic call (Random, Guid.NewGuid) inside a query must never be evaluated once and baked into
/// the pipeline as a constant, which silently gives every row the same value.
/// </summary>
[XUnitCollection("QueryTests")]
public class NonDeterministicFunctionTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    public class Row
    {
        public ObjectId Id { get; set; }
        public int Foo { get; set; }
        public DateTime Created { get; set; }
    }

    private SingleEntityDbContext<Row> CreateContext(MongoQueryMode mode, string name)
    {
        var collection = database.CreateCollection<Row>(name);
        collection.InsertMany(
        [
            new Row { Id = ObjectId.GenerateNewId(), Foo = 1, Created = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
            new Row { Id = ObjectId.GenerateNewId(), Foo = 2, Created = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
            new Row { Id = ObjectId.GenerateNewId(), Foo = 3, Created = new DateTime(2100, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
        ]);
        return SingleEntityDbContext.Create(collection, optionsBuilderAction: b =>
        {
            b.ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.ManyServiceProvidersCreatedWarning));
            new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
        });
    }

    [Theory, InlineData(MongoQueryMode.Native), InlineData(MongoQueryMode.DriverLinq)]
    public void Random_in_predicate_throws_rather_than_being_evaluated_once(MongoQueryMode mode)
    {
        using var db = CreateContext(mode, $"rp_{mode}");
        var ex = Assert.Throws<InvalidOperationException>(
            () => db.Entities.Where(o => o.Foo > Random.Shared.Next(0, 2) + o.Foo - o.Foo).ToList());
        Assert.Contains("Next", ex.Message);
    }

    [Theory, InlineData(MongoQueryMode.Native), InlineData(MongoQueryMode.DriverLinq)]
    public void Random_compared_to_column_in_predicate_throws(MongoQueryMode mode)
    {
        using var db = CreateContext(mode, $"rc_{mode}");
        Assert.Throws<InvalidOperationException>(
            () => db.Entities.Where(o => o.Foo > Random.Shared.Next()).ToList());
    }

    [Theory, InlineData(MongoQueryMode.Native), InlineData(MongoQueryMode.DriverLinq)]
    public void Random_in_ordering_throws(MongoQueryMode mode)
    {
        using var db = CreateContext(mode, $"ro_{mode}");
        Assert.Throws<InvalidOperationException>(
            () => db.Entities.OrderBy(o => o.Foo + Random.Shared.Next()).ToList());
    }

    [Theory, InlineData(MongoQueryMode.Native), InlineData(MongoQueryMode.DriverLinq)]
    public void Guid_NewGuid_in_predicate_throws(MongoQueryMode mode)
    {
        using var db = CreateContext(mode, $"gp_{mode}");
        Assert.Throws<InvalidOperationException>(
            () => db.Entities.Where(o => o.Foo > 0 && Guid.NewGuid() != Guid.Empty && o.Foo == o.Foo).ToList());
    }

    // EF leaves Guid.NewGuid() in the tree, so a closed DateTime receiver of a native $dateAdd that contains one reaches
    // MongoExpressionTranslator.TryEvaluateClosedSubtree, which would otherwise evaluate it once at translation time.
    // The native path must decline (NativeOnly throws) and the driver-LINQ bridge then throws.
    [Theory, InlineData(MongoQueryMode.Native), InlineData(MongoQueryMode.DriverLinq), InlineData(MongoQueryMode.NativeOnly)]
    public void Guid_NewGuid_in_closed_date_add_receiver_throws(MongoQueryMode mode)
    {
        using var db = CreateContext(mode, $"gd_{mode}");

        Func<object> query = () => db.Entities
            .Where(o => new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks(Guid.NewGuid().GetHashCode()).AddDays(o.Foo)
                        > o.Created)
            .ToList();

        if (mode == MongoQueryMode.NativeOnly)
        {
            Assert.Throws<MongoDB.EntityFrameworkCore.Query.NativeTranslation.NativeTranslationNotSupportedException>(query);
        }
        else
        {
            Assert.Contains("Guid.NewGuid", Assert.Throws<InvalidOperationException>(query).Message);
        }
    }

    [Theory, InlineData(MongoQueryMode.Native), InlineData(MongoQueryMode.NativeOnly), InlineData(MongoQueryMode.DriverLinq)]
    public void UtcNow_in_predicate_still_works(MongoQueryMode mode)
    {
        using var db = CreateContext(mode, $"un_{mode}");
        Assert.Equal(2, db.Entities.Where(o => o.Created < DateTime.UtcNow).Count());
    }

    [Theory, InlineData(MongoQueryMode.Native), InlineData(MongoQueryMode.NativeOnly), InlineData(MongoQueryMode.DriverLinq)]
    public void Now_in_predicate_still_works(MongoQueryMode mode)
    {
        using var db = CreateContext(mode, $"nw_{mode}");
        Assert.Equal(2, db.Entities.Where(o => o.Created < DateTime.Now).Count());
    }

    [Theory, InlineData(MongoQueryMode.Native), InlineData(MongoQueryMode.NativeOnly), InlineData(MongoQueryMode.DriverLinq)]
    public void Today_add_days_in_predicate_works(MongoQueryMode mode)
    {
        using var db = CreateContext(mode, $"td_{mode}");
        Assert.Equal(2, db.Entities.Where(o => o.Created < DateTime.Today.AddDays(-1)).Count());
    }

    [Theory, InlineData(MongoQueryMode.Native), InlineData(MongoQueryMode.NativeOnly), InlineData(MongoQueryMode.DriverLinq)]
    public void DateTimeOffset_UtcNow_add_days_in_predicate_works(MongoQueryMode mode)
    {
        using var db = CreateContext(mode, $"do_{mode}");
        Assert.Equal(2, db.Entities.Where(o => o.Created < DateTimeOffset.UtcNow.AddDays(-1).UtcDateTime).Count());
    }

    // F2: a closed clock subtree must be evaluated per EXECUTION. The native plan is compiled and cached once, so
    // evaluating the clock at translation time bakes the first reading into every later execution.
    // One helper method => one lambda => one EF compiled-query cache entry.
    private static int CountRecent(SingleEntityDbContext<Row> db)
        => db.Entities.Count(r => r.Created <= DateTime.UtcNow.AddMilliseconds(0));

    [Theory, InlineData(MongoQueryMode.Native), InlineData(MongoQueryMode.NativeOnly), InlineData(MongoQueryMode.DriverLinq)]
    public async Task UtcNow_add_receiver_is_evaluated_per_execution_not_baked_into_the_cached_plan(MongoQueryMode mode)
    {
        var collection = database.CreateCollection<Row>($"st_{mode}");
        collection.InsertOne(new Row { Id = ObjectId.GenerateNewId(), Foo = 1, Created = DateTime.UtcNow.AddMilliseconds(600) });

        // ONE context for both executions: SingleEntityDbContext's IgnoreCacheKeyFactory gives every instance its own
        // model, so separate instances would each compile the query and could never observe a stale cached plan.
        var compilations = 0;
        using var db = CreateClockContext(collection, mode, () => compilations++);

        Assert.Equal(0, CountRecent(db)); // the row is 600 ms in the future
        await Task.Delay(1200);
        Assert.Equal(1, CountRecent(db)); // a baked first reading keeps answering 0

        // Proves the second execution reused the cached plan (otherwise the test could not detect baking).
        Assert.Equal(1, compilations);
    }

    // The correct answer is time-zone independent: only the past row. Relabelling Local `Now` as UTC (rather than
    // converting it) answers wrongly whenever the host's |UTC offset| exceeds 30 minutes. Run under
    // TZ=Pacific/Auckland and TZ=America/Los_Angeles to exercise it (TZ is read at process start).
    [Theory, InlineData(MongoQueryMode.Native), InlineData(MongoQueryMode.NativeOnly), InlineData(MongoQueryMode.DriverLinq)]
    public void Local_Now_add_receiver_compares_instants_in_any_time_zone(MongoQueryMode mode)
    {
        var collection = database.CreateCollection<Row>($"lz_{mode}");
        var now = DateTime.UtcNow;
        collection.InsertMany(
        [
            new Row { Id = ObjectId.GenerateNewId(), Foo = 1, Created = now.AddMinutes(-30) },
            new Row { Id = ObjectId.GenerateNewId(), Foo = 2, Created = now.AddMinutes(30) },
        ]);
        using var db = CreateClockContext(collection, mode);

        Assert.Equal([1], db.Entities.Where(r => r.Created <= DateTime.Now.AddMinutes(0)).Select(r => r.Foo).ToList());
        Assert.Equal([1], db.Entities.Where(r => r.Created <= DateTime.Now.AddMinutes(r.Foo * 0)).Select(r => r.Foo).ToList());
        Assert.Equal([1], db.Entities.Where(r => r.Created <= DateTime.Now).Select(r => r.Foo).ToList());
        Assert.Equal([1, 2], db.Entities.Where(r => r.Created > DateTime.Today.AddYears(-1)).OrderBy(r => r.Foo)
            .Select(r => r.Foo).ToList());
    }

    // A row-independent predicate holding a clock (the spec Where_datetime_* shape) goes native as a per-execution
    // boolean, and is re-evaluated on the cached plan's second execution.
    [Theory, InlineData(MongoQueryMode.Native), InlineData(MongoQueryMode.NativeOnly), InlineData(MongoQueryMode.DriverLinq)]
    public void Closed_clock_predicate_is_evaluated_per_execution(MongoQueryMode mode)
    {
        var collection = database.CreateCollection<Row>($"cp_{mode}");
        collection.InsertOne(new Row { Id = ObjectId.GenerateNewId(), Foo = 1, Created = DateTime.UtcNow });

        var compilations = 0;
        using var db = CreateClockContext(collection, mode, () => compilations++);

        // The same threshold both times: only the clock moves past it between the two executions.
        var threshold = DateTime.UtcNow.AddMilliseconds(600);
        Assert.Equal(0, CountAfter(db, threshold));
        Thread.Sleep(1200);
        Assert.Equal(1, CountAfter(db, threshold)); // a baked first reading keeps answering 0
        Assert.Equal(1, compilations);

        static int CountAfter(SingleEntityDbContext<Row> db, DateTime threshold)
            => db.Entities.Count(r => DateTime.UtcNow > threshold);
    }

    // A projected closed clock subtree is a per-execution value too (it was baked, or fell back). Like driver-LINQ, it
    // round-trips as a BSON date, so Local `Now` reads back as its UTC instant.
    private static (DateTime Shifted, DateTime Now) ProjectClock(SingleEntityDbContext<Row> db)
        => db.Entities.Select(r => new { r.Foo, Shifted = DateTime.UtcNow.AddDays(1), Now = DateTime.Now })
            .AsEnumerable().Select(x => (x.Shifted, x.Now)).Single();

    [Theory, InlineData(MongoQueryMode.Native), InlineData(MongoQueryMode.NativeOnly), InlineData(MongoQueryMode.DriverLinq)]
    public void Projected_clock_is_evaluated_per_execution(MongoQueryMode mode)
    {
        var collection = database.CreateCollection<Row>($"pj_{mode}");
        collection.InsertOne(new Row { Id = ObjectId.GenerateNewId(), Foo = 1, Created = DateTime.UtcNow });

        var compilations = 0;
        using var db = CreateClockContext(collection, mode, () => compilations++);

        var before = DateTime.UtcNow;
        var first = ProjectClock(db);
        Thread.Sleep(1200);
        var second = ProjectClock(db);
        var after = DateTime.UtcNow;

        Assert.Equal(1, compilations);
        Assert.True(second.Shifted - first.Shifted >= TimeSpan.FromSeconds(1), $"{first.Shifted:o} -> {second.Shifted:o}");
        Assert.InRange(first.Shifted, before.AddDays(1).AddMilliseconds(-1), after.AddDays(1));
        Assert.Equal(DateTimeKind.Utc, first.Now.Kind);
        Assert.InRange(second.Now, before.AddMilliseconds(-1), after.AddMilliseconds(1));
    }

    private static SingleEntityDbContext<Row> CreateClockContext(
        IMongoCollection<Row> collection, MongoQueryMode mode, Action? onCompilation = null)
        => SingleEntityDbContext.Create(collection, optionsBuilderAction: b =>
        {
            b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
            new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            if (onCompilation is not null)
                b.LogTo(_ => onCompilation(), [CoreEventId.QueryCompilationStarting]);
        });

    // Pinned: before EF-255 a Random call in a projection was also evaluated once and returned the same value for every
    // row (the driver folds it into a constant), so it was never a working per-row client evaluation. It now fails loudly.
    [Theory, InlineData(MongoQueryMode.Native), InlineData(MongoQueryMode.DriverLinq)]
    public void Random_in_projection_throws_rather_than_repeating_one_value(MongoQueryMode mode)
    {
        using var db = CreateContext(mode, $"rj_{mode}");
        Assert.Throws<InvalidOperationException>(
            () => db.Entities.Select(o => new { o.Foo, R = Random.Shared.Next() }).ToList());
    }

    [Theory, InlineData(MongoQueryMode.Native), InlineData(MongoQueryMode.DriverLinq)]
    public void Random_evaluated_client_side_before_the_query_works(MongoQueryMode mode)
    {
        using var db = CreateContext(mode, $"rv_{mode}");
        var threshold = Random.Shared.Next(-10, 0);
        Assert.Equal(3, db.Entities.Where(o => o.Foo > threshold).Count());
    }
}
