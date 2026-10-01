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
    // (0, as for a bare `x.Rank`); native has always thrown for a missing computed alias, and still does (now through
    // the strict read). A loud, pre-existing divergence, pinned so a change is noticed.
    [Fact]
    public void Conditional_selecting_a_missing_required_element_throws_natively_and_reads_default_on_driver_linq()
    {
        var collection = Seed(
            nameof(Conditional_selecting_a_missing_required_element_throws_natively_and_reads_default_on_driver_linq), nullRow: false);

        foreach (var mode in new[] { MongoQueryMode.NativeOnly, MongoQueryMode.Native })
        {
            using var db = CreateContext(collection, mode);
            var ex = Assert.Throws<InvalidOperationException>(() => RunComputed(db, "condelse", wellFormedOnly: false));
            Assert.Contains("Nullable object must have a value", ex.Message);
        }

        using (var db = CreateContext(collection, MongoQueryMode.DriverLinq))
        {
            Assert.Equal([1, -1, 0], RunComputed(db, "condelse", wellFormedOnly: false));
        }
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
            new() {{"_id", ObjectId.GenerateNewId()}, {"Title", "a"}, {"Rank", 1}},
            new() {{"_id", ObjectId.GenerateNewId()}, {"Title", "b"}, {"Rank", 2}},
            new() {{"_id", ObjectId.GenerateNewId()}, {"Title", "c"}},
        };
        if (nullRow)
        {
            docs.Add(new BsonDocument {{"_id", ObjectId.GenerateNewId()}, {"Title", "d"}, {"Rank", BsonNull.Value}});
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
