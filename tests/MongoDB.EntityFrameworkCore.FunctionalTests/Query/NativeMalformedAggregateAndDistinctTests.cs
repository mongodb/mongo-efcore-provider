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
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Infrastructure;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// Malformed documents (a non-nullable element MISSING, or an explicit BSON null) reaching a value that isn't a
/// projection read: a terminal <c>Min</c>/<c>Max</c>/<c>Average</c>, and a projected <c>Distinct</c> over a bare stored
/// field. Each row set is checked against what canonical main (driver LINQ, upstream/main dec7e26f, EF10 probe) answered.
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeMalformedAggregateAndDistinctTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    public class Item
    {
        public ObjectId Id { get; set; }
        public string Title { get; set; } = "";
        public int Rank { get; set; }
        public int? Score { get; set; }
        public long Big { get; set; }
        public bool Flag { get; set; }
        public DateTime When { get; set; }
        public int Conv { get; set; }
        public Color E { get; set; }
        public Color EStr { get; set; }
    }

    public enum Color { Red, Green, Blue }

    private const string Throws = "throws";

    // Row sets: c (Rank/Score/Big missing), d (explicit null), abc, abd, cd. a: Rank 1, Score 5, Big 10; b: Rank 2, Big 20.
    private static readonly string[] RowSets = ["c", "d", "abc", "abd", "cd"];

    // Not compared: the row set's result order is unspecified on the server (a missing and a null sort key tie).
    private const string Unordered = "unordered";

    /// <summary>
    /// Main's answers per row set. Main's <c>Min</c>/<c>Max</c> reduce <c>{_v: value}</c> documents, which order a MISSING
    /// value (<c>{}</c>, read as <c>default</c>) below a null (read: throws) below any number; its <c>Average</c> answers null
    /// (throws) when no row has a number; its projected <c>Distinct</c> groups the projected document, so MISSING
    /// (<c>default</c>) and null (throws) are two values. A type-changing cast (<c>(long)x.Rank</c>) renders as
    /// <c>$toLong</c>, null for both.
    /// </summary>
    public static readonly Dictionary<string, (Func<IQueryable<Item>, string> Run, string[] Main)> MainParityShapes = new()
    {
        ["max_bare"] = (q => One(q.Max(x => x.Rank)), ["0", Throws, "2", "2", Throws]),
        ["min_bare"] = (q => One(q.Min(x => x.Rank)), ["0", Throws, "0", Throws, "0"]),
        ["avg_bare"] = (q => One(q.Average(x => x.Rank)), [Throws, Throws, "1.5", "1.5", Throws]),
        ["selmax_bare"] = (q => One(q.Select(x => x.Rank).Max()), ["0", Throws, "2", "2", Throws]),
        ["selmin_bare"] = (q => One(q.Select(x => x.Rank).Min()), ["0", Throws, "0", Throws, "0"]),
        ["max_condelse"] = (q => One(q.Max(x => x.Rank > 1 ? -1 : x.Rank)), ["0", Throws, "1", "1", Throws]),
        ["min_condelse"] = (q => One(q.Min(x => x.Rank > 1 ? -1 : x.Rank)), ["0", Throws, "0", Throws, "0"]),
        ["avg_condelse"] = (q => One(q.Average(x => x.Rank > 1 ? -1 : x.Rank)), [Throws, Throws, "0", "0", Throws]),
        ["max_add1"] = (q => One(q.Max(x => x.Rank + 1)), [Throws, Throws, "3", "3", Throws]),
        ["min_add1"] = (q => One(q.Min(x => x.Rank + 1)), [Throws, Throws, Throws, Throws, Throws]),
        ["avg_add1"] = (q => One(q.Average(x => x.Rank + 1)), [Throws, Throws, "2.5", "2.5", Throws]),
        ["max_castlong"] = (q => One(q.Max(x => (long)x.Rank)), [Throws, Throws, "2", "2", Throws]),
        ["selmax_castlong"] = (q => One(q.Select(x => (long)x.Rank).Max()), [Throws, Throws, "2", "2", Throws]),
        ["min_castlong_nullable"] = (q => One(q.Min(x => (long)x.Score!)), [Throws, Throws, Throws, Throws, Throws]),
        ["max_castint"] = (q => One(q.Max(x => (int)x.Rank)), ["0", Throws, "2", "2", Throws]),
        ["max_long"] = (q => One(q.Max(x => x.Big)), ["0", Throws, "20", "20", Throws]),
        ["max_scorevalue"] = (q => One(q.Max(x => x.Score!.Value)), ["0", Throws, "5", "5", Throws]),
        ["min_scorevalue"] = (q => One(q.Min(x => x.Score!.Value)), ["0", Throws, "0", "0", "0"]),
        ["distinct_bare"] = (q => Sorted(q.Select(x => x.Rank).Distinct()), ["0", Throws, "0,1,2", Throws, Throws]),
        ["distinct_castint"] = (q => Sorted(q.Select(x => (int)x.Rank).Distinct()), ["0", Throws, "0,1,2", Throws, Throws]),
        ["distinct_castlong"] = (q => Sorted(q.Select(x => (long)x.Rank).Distinct()), [Throws, Throws, Throws, Throws, Throws]),
        // The driver's $toLong answers null for MISSING too, so a MISSING and a null row are one group.
        ["distinct_castlong_count"] = (q => One(q.Select(x => (long)x.Rank).Distinct().Count()), ["1", "1", "3", "3", "1"]),
        ["distinct_long"] = (q => Sorted(q.Select(x => x.Big).Distinct()), ["0", Throws, "0,10,20", Throws, Throws]),
        ["distinct_castint_nullable"] = (q => Sorted(q.Select(x => (int)x.Score!).Distinct()), ["0", Throws, "0,5", Throws, Throws]),
        ["distinct_castlong_nullable"] = (q => Sorted(q.Select(x => (long)x.Score!).Distinct()), [Throws, Throws, Throws, Throws, Throws]),
        ["distinct_anon"] = (q => Sorted(q.Select(x => new { x.Rank }).Distinct().AsEnumerable().Select(a => a.Rank)), ["0", Throws, "0,1,2", Throws, Throws]),
        ["distinct_anon2"] = (q => Sorted(q.Select(x => new { x.Title, x.Rank }).Distinct().AsEnumerable().Select(a => a.Rank)), ["0", Throws, "0,1,2", Throws, Throws]),
        ["distinct_count"] = (q => One(q.Select(x => x.Rank).Distinct().Count()), ["1", "1", "3", "3", "2"]),
        ["distinct_max"] = (q => One(q.Select(x => x.Rank).Distinct().Max()), ["0", Throws, "2", "2", Throws]),
        ["distinct_min"] = (q => One(q.Select(x => x.Rank).Distinct().Min()), ["0", Throws, "0", Throws, "0"]),
        ["distinct_first"] = (q => One(q.Select(x => x.Rank).Distinct().OrderBy(v => v).First()), ["0", Throws, "0", Throws, Unordered]),
        // An enum's cast to its underlying type: the driver drops it, so MISSING reads default and null throws.
        ["castint_enum"] = (q => Sorted(q.Select(x => (int)x.E)), ["0", Throws, "0,1,2", Throws, Throws]),
        ["anon_castint_enum"] = (q => Sorted(q.Select(x => new { V = (int)x.E }).AsEnumerable().Select(a => a.V)), ["0", Throws, "0,1,2", Throws, Throws]),
        ["distinct_castint_enum"] = (q => Sorted(q.Select(x => (int)x.E).Distinct()), ["0", Throws, "0,1,2", Throws, Throws]),
        ["distinct_castint_enum_count"] = (q => One(q.Select(x => (int)x.E).Distinct().Count()), ["1", "1", "3", "3", "2"]),
        ["castlong_nullable"] = (q => Sorted(q.Select(x => (long)x.Score!)), [Throws, Throws, Throws, Throws, Throws]),
        // The driver's BooleanSerializer reads an explicit null as false, so a malformed bool never throws on main.
        ["flag_bare"] = (q => Sorted(q.Select(x => x.Flag)), ["False", "False", "False,False,True", "False,False,True", "False,False"]),
        ["flag_anon"] = (q => Sorted(q.Select(x => new { x.Flag }).AsEnumerable().Select(a => a.Flag)), ["False", "False", "False,False,True", "False,False,True", "False,False"]),
        ["flag_cond"] = (q => Sorted(q.Select(x => x.Title == "zz" ? true : x.Flag)), ["False", "False", "False,False,True", "False,False,True", "False,False"]),
        ["flag_first"] = (q => One(q.OrderBy(x => x.Title).Select(x => x.Flag).First()), ["False", "False", "True", "True", "False"]),
        ["flag_max"] = (q => One(q.Max(x => x.Flag)), ["False", "False", "True", "True", "False"]),
        ["flag_min"] = (q => One(q.Min(x => x.Flag)), ["False", "False", "False", "False", "False"]),
        ["flag_distinct_anon2"] = (q => Sorted(q.Select(x => new { x.Title, x.Flag }).Distinct().AsEnumerable().Select(a => a.Flag)), ["False", "False", "False,False,True", "False,False,True", "False,False"]),
        // Parity pins: already matched main before Task 1.15.
        ["sum_bare"] = (q => One(q.Sum(x => x.Rank)), ["0", "0", "3", "3", "0"]),
        ["sum_add1"] = (q => One(q.Sum(x => x.Rank + 1)), ["0", "0", "5", "5", "0"]),
        ["distinct_add1"] = (q => Sorted(q.Select(x => x.Rank + 1).Distinct()), [Throws, Throws, Throws, Throws, Throws]),
        ["avg_nullable"] = (q => One(q.Average(x => x.Score)), ["null", "null", "5", "5", "null"]),
        ["max_nullable"] = (q => One(q.Max(x => x.Score)), ["null", "null", "5", "5", "null"]),
        ["first_bare"] = (q => One(q.OrderBy(x => x.Title).Select(x => x.Rank).First()), ["0", Throws, "1", "1", "0"]),
    };

    public static TheoryData<string> MainParityShapeNames => [.. MainParityShapes.Keys];

    [Theory]
    [MemberData(nameof(MainParityShapeNames))]
    public void Aggregate_or_distinct_over_a_missing_or_null_required_element_matches_main(string shape)
    {
        var collection = Seed(nameof(Aggregate_or_distinct_over_a_missing_or_null_required_element_matches_main) + shape);
        var (run, main) = MainParityShapes[shape];

        foreach (var mode in new[] { MongoQueryMode.NativeOnly, MongoQueryMode.Native, MongoQueryMode.DriverLinq })
        {
            Assert.True(main.SequenceEqual(RunPerRowSet(collection, mode, run, main)),
                $"{mode}: expected [{string.Join("; ", main)}], got [{string.Join("; ", RunPerRowSet(collection, mode, run, main))}]");
        }
    }

    /// <summary>
    /// Where main is wrong and native is right, pinned with main's answer alongside. A nullable <c>Min</c> reduces main's
    /// <c>{_v: value}</c> documents, so a MISSING or null row wins over every number (LINQ skips nulls); a nullable
    /// projected <c>Distinct</c> keeps a MISSING and a null value apart on main, so it returns null twice.
    /// </summary>
    public static readonly Dictionary<string, (Func<IQueryable<Item>, string> Run, string[] Native, string[] Main)> MainWrongShapes = new()
    {
        ["min_nullable"] = (q => One(q.Min(x => x.Score)), ["null", "null", "5", "5", "null"], ["null", "null", "null", "null", "null"]),
        ["distinct_nullable"] = (q => Sorted(q.Select(x => x.Score).Distinct()), ["null", "null", "5,null", "5,null", "null"],
            ["null", "null", "5,null", "5,null,null", "null,null"]),
        // A bool's MISSING and null both read false, yet group apart from a stored false (both paths), and on main also
        // from each other.
        ["distinct_flag"] = (q => Sorted(q.Select(x => x.Flag).Distinct()), ["False", "False", "False,False,True", "False,False,True", "False"],
            ["False", "False", "False,False,True", "False,False,True", "False,False"]),
    };

    public static TheoryData<string> MainWrongShapeNames => [.. MainWrongShapes.Keys];

    [Theory]
    [MemberData(nameof(MainWrongShapeNames))]
    public void Nullable_aggregate_or_distinct_where_main_is_wrong_stays_correct(string shape)
    {
        var collection = Seed(nameof(Nullable_aggregate_or_distinct_where_main_is_wrong_stays_correct) + shape);
        var (run, native, main) = MainWrongShapes[shape];

        foreach (var mode in new[] { MongoQueryMode.NativeOnly, MongoQueryMode.Native, MongoQueryMode.DriverLinq })
        {
            var expected = mode == MongoQueryMode.DriverLinq ? main : native;
            var actual = RunPerRowSet(collection, mode, run, expected);
            Assert.True(expected.SequenceEqual(actual), $"{mode}: expected [{string.Join("; ", expected)}], got [{string.Join("; ", actual)}]");
        }
    }

    /// <summary>
    /// A marked bare Distinct key over a default-serialized property still reads through that property, so a Local-kind
    /// <see cref="DateTime"/> keeps its kind (a generic read of the restored key would answer Utc).
    /// </summary>
    [Fact]
    public void Distinct_over_a_bare_local_kind_date_keeps_its_kind()
    {
        var collection = Seed(nameof(Distinct_over_a_bare_local_kind_date_keeps_its_kind));

        Assert.Equal(
            ["Local", "Local"],
            NativeModeAssert.NativeAndParity(mode =>
            {
                using var db = CreateContext(collection, mode);
                return db.Entities.AsNoTracking().Where(x => x.Title == "a" || x.Title == "b")
                    .Select(x => x.When).Distinct().AsEnumerable().Select(v => v.Kind.ToString()).ToList();
            }));
    }

    /// <summary>
    /// A converted bare Distinct key gets no missing marker (it would take the key off the converted set-op paths), so it
    /// keeps its previous read: a MISSING row reads <c>default</c>, as on main. (An explicit-null row also reads
    /// <c>default</c> natively, where main's converter throws: a known residual, Task 1.15.)
    /// </summary>
    [Fact]
    public void Distinct_over_a_converted_key_reads_a_missing_row_as_default()
    {
        var collection = Seed(nameof(Distinct_over_a_converted_key_reads_a_missing_row_as_default));

        Assert.Equal(
            [0, 1, 2],
            NativeModeAssert.NativeAndParity(mode =>
            {
                using var db = CreateContext(collection, mode);
                return db.Entities.AsNoTracking().Where(x => x.Title != "d")
                    .Select(x => x.Conv).Distinct().AsEnumerable().Order().ToList();
            }));
    }

    /// <summary>
    /// An enum stored as a string isn't relabeled bare (its stored value isn't the integer): the cast still declines natively.
    /// </summary>
    [Fact]
    public void Underlying_cast_of_a_string_stored_enum_still_declines()
    {
        var collection = Seed(nameof(Underlying_cast_of_a_string_stored_enum_still_declines));

        foreach (var distinct in new[] { false, true })
        {
            using var db = CreateContext(collection, MongoQueryMode.NativeOnly);
            var q = db.Entities.AsNoTracking().Where(x => x.Title == "a").Select(x => (int)x.EStr);
            Assert.Throws<MongoDB.EntityFrameworkCore.Query.NativeTranslation.NativeTranslationNotSupportedException>(
                () => (distinct ? q.Distinct() : q).ToList());
        }
    }

    private static List<string> RunPerRowSet(
        IMongoCollection<Item> collection, MongoQueryMode mode, Func<IQueryable<Item>, string> run, string[] expected)
    {
        var perRowSet = new List<string>();
        for (var i = 0; i < RowSets.Length; i++)
        {
            if (expected[i] == Unordered)
            {
                perRowSet.Add(Unordered);
                continue;
            }

            using var db = CreateContext(collection, mode);
            try
            {
                perRowSet.Add(run(Rows(db.Entities.AsNoTracking(), RowSets[i])));
            }
            // A NativeOnly decline (NativeTranslationNotSupportedException) is not a row outcome: it fails the test.
            catch (Exception ex) when (ex is FormatException or InvalidOperationException)
            {
                perRowSet.Add(Throws);
            }
        }

        return perRowSet;
    }

    private static IQueryable<Item> Rows(IQueryable<Item> q, string rowSet)
        => rowSet switch
        {
            "c" => q.Where(x => x.Title == "c"),
            "d" => q.Where(x => x.Title == "d"),
            "abc" => q.Where(x => x.Title != "d"),
            "abd" => q.Where(x => x.Title != "c"),
            _ => q.Where(x => x.Title == "c" || x.Title == "d"),
        };

    private static string One<T>(T value)
        => value is null ? "null" : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)!;

    private static string Sorted<T>(IEnumerable<T> values)
        => string.Join(",", values.ToList().Select(One).Order(StringComparer.Ordinal));

    private IMongoCollection<Item> Seed(string name)
    {
        var raw = database.MongoDatabase.GetCollection<BsonDocument>(
            TemporaryDatabaseFixtureBase.CreateCollectionName(name) + Guid.NewGuid().ToString("N")[..8]);
        raw.InsertMany(
        [
            new() { { "_id", ObjectId.GenerateNewId() }, { "Title", "a" }, { "Rank", 1 }, { "Score", 5 }, { "Big", 10L }, { "Flag", true }, { "When", new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc) }, { "Conv", "1" }, { "E", 1 }, { "EStr", "Green" } },
            new() { { "_id", ObjectId.GenerateNewId() }, { "Title", "b" }, { "Rank", 2 }, { "Big", 20L }, { "Flag", false }, { "When", new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc) }, { "Conv", "2" }, { "E", 2 }, { "EStr", "Blue" } },
            new() { { "_id", ObjectId.GenerateNewId() }, { "Title", "c" } },
            new()
            {
                { "_id", ObjectId.GenerateNewId() }, { "Title", "d" }, { "Rank", BsonNull.Value }, { "Score", BsonNull.Value },
                { "Big", BsonNull.Value }, { "Flag", BsonNull.Value }, { "When", BsonNull.Value }, { "Conv", BsonNull.Value }, { "E", BsonNull.Value }, { "EStr", BsonNull.Value }
            },
        ]);
        return database.MongoDatabase.GetCollection<Item>(raw.CollectionNamespace.CollectionName);
    }

    private static SingleEntityDbContext<Item> CreateContext(IMongoCollection<Item> collection, MongoQueryMode mode)
        => SingleEntityDbContext.Create(
            collection,
            modelBuilderAction: mb =>
            {
                mb.Entity<Item>().Property(x => x.When).HasDateTimeKind(DateTimeKind.Local);
                mb.Entity<Item>().Property(x => x.Conv).HasConversion<string>();
                mb.Entity<Item>().Property(x => x.EStr).HasConversion<string>();
            },
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });
}
