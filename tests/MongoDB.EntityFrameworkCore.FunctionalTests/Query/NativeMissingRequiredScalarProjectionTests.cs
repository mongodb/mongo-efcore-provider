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

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// A bare projection of a stored required scalar whose element is MISSING reads <c>default(T)</c> (as driver-LINQ's
/// <c>$project</c> push-down did); an explicit BSON null still throws, and whole-entity materialization stays strict.
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeMissingRequiredScalarProjectionTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    public class Item
    {
        public ObjectId Id { get; set; }
        public string Title { get; set; } = "";
        public int Rank { get; set; }
        public int? Score { get; set; }
        public long Big { get; set; }
    }

    [Fact]
    public void Bare_scalar_projection_of_a_missing_required_element_reads_default()
    {
        var collection = Seed(nameof(Bare_scalar_projection_of_a_missing_required_element_reads_default), nullRow: false);

        Assert.Equal(
            [1, 2, 0],
            NativeModeAssert.NativeAndExpected(
                mode =>
                {
                    using var db = CreateContext(collection, mode);
                    return db.Entities.AsNoTracking().OrderBy(x => x.Title).Select(x => x.Rank).ToList();
                },
                [1, 2, 0]));
    }

    [Fact]
    public void Anonymous_projection_of_a_missing_required_element_reads_default()
    {
        var collection = Seed(nameof(Anonymous_projection_of_a_missing_required_element_reads_default), nullRow: false);

        NativeModeAssert.NativeAndExpected(
            mode =>
            {
                using var db = CreateContext(collection, mode);
                return db.Entities.AsNoTracking().OrderBy(x => x.Title).Select(x => new { x.Rank }).ToList()
                    .Select(a => a.Rank).ToList();
            },
            [1, 2, 0]);

        NativeModeAssert.NativeAndExpected(
            mode =>
            {
                using var db = CreateContext(collection, mode);
                return db.Entities.AsNoTracking().OrderBy(x => x.Title).Select(x => new { x.Title, x.Rank }).ToList()
                    .Select(a => $"{a.Title}:{a.Rank}").ToList();
            },
            ["a:1", "b:2", "c:0"]);
    }

    [Fact]
    public void Bare_scalar_projection_of_an_explicit_null_still_throws()
    {
        var collection = Seed(nameof(Bare_scalar_projection_of_an_explicit_null_still_throws), nullRow: true);

        // Driver-LINQ throws FormatException; native throws InvalidOperationException. Only a MISSING element reads default.
        using (var db = CreateContext(collection, MongoQueryMode.DriverLinq))
        {
            Assert.Throws<FormatException>(
                () => db.Entities.AsNoTracking().Where(x => x.Title == "d").Select(x => x.Rank).ToList());
        }

        foreach (var mode in new[] { MongoQueryMode.NativeOnly, MongoQueryMode.Native })
        {
            using var db = CreateContext(collection, mode);
            var ex = Assert.Throws<InvalidOperationException>(
                () => db.Entities.AsNoTracking().Where(x => x.Title == "d").Select(x => x.Rank).ToList());
            Assert.Contains("is null for required non-nullable property 'Rank'", ex.Message);
        }
    }

    [Fact]
    public void Whole_entity_read_of_a_missing_required_element_still_throws()
    {
        var collection = Seed(nameof(Whole_entity_read_of_a_missing_required_element_still_throws), nullRow: false);

        foreach (var mode in new[] { MongoQueryMode.NativeOnly, MongoQueryMode.Native, MongoQueryMode.DriverLinq })
        {
            using var db = CreateContext(collection, mode);
            var ex = Assert.Throws<InvalidOperationException>(
                () => db.Entities.AsNoTracking().Where(x => x.Title == "c").ToList());
            Assert.Contains("missing for required non-nullable property", ex.Message);
        }
    }

    public static TheoryData<string> ThrowingShapes => ["add", "mul", "abs", "anon"];

    public static TheoryData<string> ComputedShapes => ["add", "mul", "abs", "cond", "condelse", "anon"];

    /// <summary>
    /// A non-nullable computed leaf over a MISSING non-nullable operand: the server's <c>$add</c>/<c>$multiply</c>/
    /// <c>$abs</c> answer null for row "c". Driver-LINQ's deserializer can't read that null as Int32 and throws; native
    /// reads the leaf strictly and throws too, instead of reading <c>0</c>. (A bare <c>x.Rank</c> leaf reads
    /// <c>default</c>, as driver-LINQ does; see above.)
    /// </summary>
    [Theory]
    [MemberData(nameof(ThrowingShapes))]
    public void Computed_projection_over_a_missing_required_element_throws(string shape)
    {
        var collection = Seed(nameof(Computed_projection_over_a_missing_required_element_throws) + shape, nullRow: false);

        foreach (var mode in new[] { MongoQueryMode.NativeOnly, MongoQueryMode.Native })
        {
            using var db = CreateContext(collection, mode);
            var ex = Assert.Throws<InvalidOperationException>(() => RunComputed(db, shape, wellFormedOnly: false));
            Assert.Contains("Nullable object must have a value", ex.Message);
        }

        using (var db = CreateContext(collection, MongoQueryMode.DriverLinq))
        {
            var ex = Assert.Throws<FormatException>(() => RunComputed(db, shape, wellFormedOnly: false));
            Assert.Contains("Cannot deserialize a 'Int32' from BsonType 'Null'", ex.ToString());
        }
    }

    // `$gt: [missing, 1]` is false in the aggregation dialect, so the conditional answers -1 (non-null) for the
    // missing row on both paths: nothing to throw for.
    [Fact]
    public void Conditional_over_a_missing_required_element_takes_the_false_branch_on_both_paths()
    {
        var collection = Seed(nameof(Conditional_over_a_missing_required_element_takes_the_false_branch_on_both_paths), nullRow: false);

        Assert.Equal(
            [-1, 2, -1],
            NativeModeAssert.NativeAndParity(mode =>
            {
                using var db = CreateContext(collection, mode);
                return RunComputed(db, "cond", wellFormedOnly: false);
            }));
    }

    // `x.Rank > 1 ? -1 : x.Rank`: for row "c" the conditional selects the missing field itself, so the server answers
    // MISSING (not null) and $project omits the element. Driver-LINQ's deserializer reads an omitted member as default
    // (0, as for a bare `x.Rank`), and so does native.
    [Fact]
    public void Conditional_selecting_a_missing_required_element_reads_default()
    {
        var collection = Seed(nameof(Conditional_selecting_a_missing_required_element_reads_default), nullRow: false);

        Assert.Equal(
            [1, -1, 0],
            NativeModeAssert.NativeAndExpected(
                mode =>
                {
                    using var db = CreateContext(collection, mode);
                    return RunComputed(db, "condelse", wellFormedOnly: false);
                },
                [1, -1, 0]));
    }

    [Theory]
    [MemberData(nameof(ComputedShapes))]
    public void Computed_projection_over_well_formed_rows_is_unchanged(string shape)
    {
        var collection = Seed(nameof(Computed_projection_over_well_formed_rows_is_unchanged) + shape, nullRow: false);

        var expected = shape switch
        {
            "add" or "anon" => new List<int> { 2, 3 },
            "mul" => [2, 4],
            "abs" => [1, 2],
            "cond" => [-1, 2],
            _ => [1, -1],
        };

        Assert.Equal(
            expected,
            NativeModeAssert.NativeAndParity(mode =>
            {
                using var db = CreateContext(collection, mode);
                return RunComputed(db, shape, wellFormedOnly: true);
            }));
    }

    // The strict read is read-side only: Distinct and Sum over a computed leaf don't decline.
    [Fact]
    public void Distinct_and_Sum_over_a_computed_leaf_stay_native()
    {
        var collection = Seed(nameof(Distinct_and_Sum_over_a_computed_leaf_stay_native), nullRow: false);

        Assert.Equal(
            [2, 3],
            NativeModeAssert.NativeAndParity(mode =>
            {
                using var db = CreateContext(collection, mode);
                return db.Entities.AsNoTracking().Where(x => x.Title != "c")
                    .Select(x => x.Rank + 1).Distinct().AsEnumerable().Order().ToList();
            }));

        Assert.Equal(
            [5],
            NativeModeAssert.NativeAndParity(mode =>
            {
                using var db = CreateContext(collection, mode);
                return new List<int> { db.Entities.AsNoTracking().Where(x => x.Title != "c").Select(x => x.Rank + 1).Sum() };
            }));
    }

    public static TheoryData<string> DistinctShapes => ["bare", "anon"];

    /// <summary>
    /// Distinct over a computed leaf groups on it, so a missing operand makes the <c>$group</c> key null. Driver-LINQ's
    /// deserializer rejects that null; native reads the flattened key strictly and throws too, instead of yielding 0.
    /// </summary>
    [Theory]
    [MemberData(nameof(DistinctShapes))]
    public void Distinct_over_a_computed_leaf_over_a_missing_required_element_throws(string shape)
    {
        var collection = Seed(nameof(Distinct_over_a_computed_leaf_over_a_missing_required_element_throws) + shape, nullRow: false);

        foreach (var mode in new[] { MongoQueryMode.NativeOnly, MongoQueryMode.Native })
        {
            using var db = CreateContext(collection, mode);
            var ex = Assert.Throws<InvalidOperationException>(() => RunDistinct(db, shape, wellFormedOnly: false));
            Assert.Contains("Nullable object must have a value", ex.Message);
        }

        using (var db = CreateContext(collection, MongoQueryMode.DriverLinq))
        {
            var ex = Assert.Throws<FormatException>(() => RunDistinct(db, shape, wellFormedOnly: false));
            Assert.Contains("Cannot deserialize a 'Int32' from BsonType 'Null'", ex.ToString());
        }
    }

    [Theory]
    [MemberData(nameof(DistinctShapes))]
    public void Distinct_over_a_computed_leaf_over_well_formed_rows_stays_native(string shape)
    {
        var collection = Seed(nameof(Distinct_over_a_computed_leaf_over_well_formed_rows_stays_native) + shape, nullRow: false);

        Assert.Equal(
            [2, 3],
            NativeModeAssert.NativeAndParity(mode =>
            {
                using var db = CreateContext(collection, mode);
                return RunDistinct(db, shape, wellFormedOnly: true);
            }));
    }

    /// <summary>
    /// Per row (a: Rank 1, Score 5, Big 10; b: Rank 2, Big 20; c: nothing; d: Rank/Score/Big explicit null), what
    /// canonical main (driver LINQ, upstream/main dec7e26f, EF10 probe) answered; "throws" is FormatException there.
    /// Native must match per row: a leaf the server answers MISSING for (a conditional branch or coalesce fallback that
    /// selects the non-nullable field itself, an identity cast) reads default like driver-LINQ's deserializer; one it
    /// answers null for (an operator or a type-changing cast over the field, or an explicit BSON null) throws.
    /// </summary>
    public static readonly Dictionary<string, (Func<IQueryable<Item>, List<string>> Run, string[] Main)> MainParityShapes = new()
    {
        ["condelse"] = (q => Text(q.Select(x => x.Rank > 1 ? -1 : x.Rank)), ["1", "-1", "0", "throws"]),
        ["condboth"] = (q => Text(q.Select(x => x.Rank > 1 ? x.Rank : x.Rank)), ["1", "2", "0", "throws"]),
        ["condtitle"] = (q => Text(q.Select(x => x.Title == "c" ? x.Rank : 0)), ["0", "0", "0", "0"]),
        ["condnested"] = (q => Text(q.Select(x => x.Rank > 1 ? -1 : (x.Rank > 0 ? 5 : x.Rank))), ["5", "-1", "0", "throws"]),
        ["condcastint"] = (q => Text(q.Select(x => x.Rank > 1 ? -1 : (int)x.Rank)), ["1", "-1", "0", "throws"]),
        ["condlong"] = (q => Text(q.Select(x => x.Rank > 1 ? -1L : x.Big)), ["10", "-1", "0", "throws"]),
        ["coalesce"] = (q => Text(q.Select(x => x.Score ?? x.Rank)), ["5", "2", "0", "throws"]),
        ["castint"] = (q => Text(q.Select(x => (int)x.Rank)), ["1", "2", "0", "throws"]),
        ["bare"] = (q => Text(q.Select(x => x.Rank)), ["1", "2", "0", "throws"]),
        ["anon_condelse"] = (q => Text(q.Select(x => new { V = x.Rank > 1 ? -1 : x.Rank }).AsEnumerable().Select(a => a.V)), ["1", "-1", "0", "throws"]),
        ["anon2_condelse"] = (q => Text(q.Select(x => new { x.Title, V = x.Rank > 1 ? -1 : x.Rank }).AsEnumerable().Select(a => a.V)), ["1", "-1", "0", "throws"]),
        ["anon_coalesce"] = (q => Text(q.Select(x => new { V = x.Score ?? x.Rank }).AsEnumerable().Select(a => a.V)), ["5", "2", "0", "throws"]),
        ["anon_castint"] = (q => Text(q.Select(x => new { V = (int)x.Rank }).AsEnumerable().Select(a => a.V)), ["1", "2", "0", "throws"]),
        ["first_condtitle"] = (q => [q.Select(x => x.Title == "c" ? x.Rank : 0).FirstOrDefault().ToString()], ["0", "0", "0", "0"]),
        ["where_condelse"] = (q => Text(q.Select(x => x.Rank > 1 ? -1 : x.Rank).Where(v => v < 100)), ["1", "-1", "0", "throws"]),
        ["orderby_condelse"] = (q => Text(q.Select(x => x.Rank > 1 ? -1 : x.Rank).OrderBy(v => v)), ["1", "-1", "0", "throws"]),
        ["skiptake_condelse"] = (q => Text(q.Select(x => x.Rank > 1 ? -1 : x.Rank).Skip(0).Take(5)), ["1", "-1", "0", "throws"]),
        ["concat_condelse"] = (q => Text(q.Select(x => x.Rank > 1 ? -1 : x.Rank).Concat(q.Select(x => x.Rank > 1 ? -1 : x.Rank))), ["1,1", "-1,-1", "0,0", "throws"]),
        ["union_condelse"] = (q => Text(q.Select(x => x.Rank > 1 ? -1 : x.Rank).Union(q.Select(x => x.Rank > 1 ? -1 : x.Rank))), ["1", "-1", "0", "throws"]),
        // A projected Distinct groups natively on the value itself, and a lone `$group` key answers null for MISSING: the
        // explicit-null row still throws as on main (the missing row's divergence is pinned below).
        ["distinct_condelse_null"] = (q => Text(q.Where(x => x.Title != "c").Select(x => x.Rank > 1 ? -1 : x.Rank).Distinct()), ["1", "-1", "", "throws"]),
        // Strict: the server answers null for the missing row as well.
        ["condcomputed"] = (q => Text(q.Select(x => x.Rank > 1 ? -1 : x.Rank + 1)), ["2", "-1", "throws", "throws"]),
        ["condcastlong"] = (q => Text(q.Select(x => x.Rank > 1 ? -1L : (long)x.Rank)), ["1", "-1", "throws", "throws"]),
        ["castlongcond"] = (q => Text(q.Select(x => (long)(x.Rank > 1 ? -1 : x.Rank))), ["1", "-1", "throws", "throws"]),
        ["distinct_castlongcond"] = (q => Text(q.Select(x => (long)(x.Rank > 1 ? -1 : x.Rank)).Distinct()), ["1", "-1", "throws", "throws"]),
        ["coalesce_add"] = (q => Text(q.Select(x => x.Score ?? x.Rank + 1)), ["5", "3", "throws", "throws"]),
        ["castlong"] = (q => Text(q.Select(x => (long)x.Rank)), ["1", "2", "throws", "throws"]),
        ["castdouble"] = (q => Text(q.Select(x => (double)x.Rank)), ["1", "2", "throws", "throws"]),
        ["anon_castlong"] = (q => Text(q.Select(x => new { V = (long)x.Rank }).AsEnumerable().Select(a => a.V)), ["1", "2", "throws", "throws"]),
        ["neg"] = (q => Text(q.Select(x => -x.Rank)), ["-1", "-2", "throws", "throws"]),
        ["abs"] = (q => Text(q.Select(x => Math.Abs(x.Rank))), ["1", "2", "throws", "throws"]),
        // A nullable target reads the missing and the null as null.
        ["castnullable"] = (q => Text(q.Select(x => (int?)x.Rank)), ["1", "2", "null", "null"]),
        ["nullablecond"] = (q => Text(q.Select(x => (int?)(x.Rank > 1 ? -1 : x.Rank))), ["1", "-1", "null", "null"]),
    };

    public static TheoryData<string> MainParityShapeNames => [.. MainParityShapes.Keys];

    [Theory]
    [MemberData(nameof(MainParityShapeNames))]
    public void Projection_over_a_missing_or_null_required_element_matches_main(string shape)
    {
        var collection = Seed(nameof(Projection_over_a_missing_or_null_required_element_matches_main) + shape, nullRow: true);
        var (run, main) = MainParityShapes[shape];

        foreach (var mode in new[] { MongoQueryMode.NativeOnly, MongoQueryMode.Native, MongoQueryMode.DriverLinq })
        {
            var perRow = new List<string>();
            foreach (var row in new[] { "a", "b", "c", "d" })
            {
                using var db = CreateContext(collection, mode);
                try
                {
                    perRow.Add(string.Join(",", run(db.Entities.AsNoTracking().Where(x => x.Title == row))));
                }
                // A NativeOnly decline (NativeTranslationNotSupportedException) is not a row outcome: it fails the test.
                catch (Exception ex) when (ex is FormatException or InvalidOperationException)
                {
                    perRow.Add("throws");
                }
            }

            Assert.True(main.SequenceEqual(perRow), $"{mode}: expected [{string.Join("; ", main)}], got [{string.Join("; ", perRow)}]");
        }
    }

    /// <summary>
    /// Residual divergences from main, all loud, pinned so a change is noticed. Driver-LINQ simplifies `x.Rank + 0` to
    /// `"$Rank"` (MISSING, so 0), also for a captured `0` parameter, which a compiled-once native template can't follow;
    /// native renders `$add` (null) and throws. A long conditional mixing a long field with a widened int field: native
    /// drops the widening the driver renders as `$toLong`, so both branches are bare and the read can't tell which one
    /// the server took; it reads strictly, where main read the long field's MISSING as 0. A projected Distinct over a
    /// leaf selecting the field: driver-LINQ groups the projected document (MISSING kept, read 0); native groups the
    /// value, and a lone `$group` key answers null for MISSING, indistinguishable from an explicit null, so it throws.
    /// </summary>
    [Fact]
    public void Missing_required_element_divergences_from_main_are_loud()
    {
        var collection = Seed(nameof(Missing_required_element_divergences_from_main_are_loud), nullRow: false);

        foreach (var mode in new[] { MongoQueryMode.NativeOnly, MongoQueryMode.Native })
        {
            using var db = CreateContext(collection, mode);
            var row = db.Entities.AsNoTracking().Where(x => x.Title == "c");
            Assert.Contains("Nullable object must have a value",
                Assert.Throws<InvalidOperationException>(() => row.Select(x => x.Rank + 0).ToList()).Message);
            Assert.Contains("Nullable object must have a value",
                Assert.Throws<InvalidOperationException>(() => row.Select(x => x.Title == "c" ? x.Big : x.Rank).ToList()).Message);
            Assert.Contains("Nullable object must have a value",
                Assert.Throws<InvalidOperationException>(() => row.Select(x => x.Rank > 1 ? -1 : x.Rank).Distinct().ToList()).Message);
        }

        using (var db = CreateContext(collection, MongoQueryMode.DriverLinq))
        {
            var row = db.Entities.AsNoTracking().Where(x => x.Title == "c");
            Assert.Equal([0], row.Select(x => x.Rank + 0).ToList());
            Assert.Equal([0L], row.Select(x => x.Title == "c" ? x.Big : x.Rank).ToList());
            Assert.Equal([0], row.Select(x => x.Rank > 1 ? -1 : x.Rank).Distinct().ToList());
        }
    }

    private static List<string> Text<T>(IEnumerable<T> values)
        => values.Select(v => v?.ToString() ?? "null").ToList();

    private static List<int> RunDistinct(SingleEntityDbContext<Item> db, string shape, bool wellFormedOnly)
    {
        var source = db.Entities.AsNoTracking();
        var q = wellFormedOnly ? source.Where(x => x.Title != "c") : source;
        return shape == "bare"
            ? q.Select(x => x.Rank + 1).Distinct().AsEnumerable().Order().ToList()
            : q.Select(x => new { V = x.Rank + 1, W = x.Rank * 2 }).Distinct().AsEnumerable().Select(a => a.V).Order().ToList();
    }

    private static List<int> RunComputed(SingleEntityDbContext<Item> db, string shape, bool wellFormedOnly)
    {
        var source = db.Entities.AsNoTracking();
        var q = (wellFormedOnly ? source.Where(x => x.Title != "c") : source).OrderBy(x => x.Title);
        return shape switch
        {
            "add" => q.Select(x => x.Rank + 1).ToList(),
            "mul" => q.Select(x => x.Rank * 2).ToList(),
            "abs" => q.Select(x => Math.Abs(x.Rank)).ToList(),
            "cond" => q.Select(x => x.Rank > 1 ? x.Rank : -1).ToList(),
            "condelse" => q.Select(x => x.Rank > 1 ? -1 : x.Rank).ToList(),
            _ => q.Select(x => new { V = x.Rank + 1 }).ToList().Select(a => a.V).ToList(),
        };
    }

    private IMongoCollection<Item> Seed(string name, bool nullRow)
    {
        var raw = database.MongoDatabase.GetCollection<BsonDocument>(UniqueCollectionName(name));
        var docs = new List<BsonDocument>
        {
            new() {{"_id", ObjectId.GenerateNewId()}, {"Title", "a"}, {"Rank", 1}, {"Score", 5}, {"Big", 10L}},
            new() {{"_id", ObjectId.GenerateNewId()}, {"Title", "b"}, {"Rank", 2}, {"Big", 20L}},
            new() {{"_id", ObjectId.GenerateNewId()}, {"Title", "c"}},
        };
        if (nullRow)
        {
            docs.Add(new BsonDocument
            {
                {"_id", ObjectId.GenerateNewId()}, {"Title", "d"}, {"Rank", BsonNull.Value}, {"Score", BsonNull.Value},
                {"Big", BsonNull.Value}
            });
        }

        raw.InsertMany(docs);
        return database.MongoDatabase.GetCollection<Item>(raw.CollectionNamespace.CollectionName);
    }

    private static SingleEntityDbContext<Item> CreateContext(IMongoCollection<Item> collection, MongoQueryMode mode)
        => SingleEntityDbContext.Create(
            collection,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });

    private string UniqueCollectionName(string name)
        => TemporaryDatabaseFixtureBase.CreateCollectionName(name) + Guid.NewGuid().ToString("N")[..8];
}
