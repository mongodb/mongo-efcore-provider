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
using System.Threading;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Infrastructure;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// A join whose inner side carries its own <c>Where</c>. Expectations are hand-computed over raw seeds, because the
/// driver-LINQ oracle is wrong or absent here: with an <c>Include</c> it silently drops the inner filter, without one it
/// throws. The explicit <see cref="MongoQueryMode.DriverLinq"/> leg therefore asserts a LOUD failure, never rows.
/// </summary>
public class NativeJoinFilteredInnerTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    public class Region
    {
        public string Id { get; set; } = "";
        public string Label { get; set; } = "";
    }

    public class Cust
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string? RegionId { get; set; }
        public Region? Region { get; set; }
    }

    public class Line
    {
        public int Id { get; set; }
        public int? OrdId { get; set; }
        public string Sku { get; set; } = "";
    }

    public class Ord
    {
        public int Id { get; set; }
        public int Total { get; set; }
        public string? CustId { get; set; }
        public string? Tag { get; set; }
        public Cust? Cust { get; set; }
        public List<Line> Lines { get; set; } = [];
    }

    // A TPH hierarchy for the derived-target case: a join onto Dogs narrows the lookup by discriminator.
    public class Animal
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string? OwnerId { get; set; }
    }

    public class Dog : Animal;

    public class Cat : Animal;

    // The same orders collection with a REQUIRED foreign key: EF synthesizes an inner join for a required dependent-side
    // reference, the one inner join whose paging used to be deferred past it (dangling data being the only way to drop a row).
    public class ReqOrd
    {
        public int Id { get; set; }
        public int Total { get; set; }
        public string CustId { get; set; } = "";
        public Cust? Cust { get; set; }
    }

    private sealed record Names(string Custs, string Ords, string Lines, string Regions, string Animals);

    private enum DriverLeg
    {
        /// <summary>The explicit DriverLinq leg must throw: it would otherwise drop the inner filter or cannot translate it.</summary>
        Throws,

        /// <summary>The explicit DriverLinq leg is a trustworthy oracle for the shape.</summary>
        EqualsExpected
    }

    // Customers A1, A2, B1. Orders: 1(A1,10) 2(A1,20) 3(A1,600) 4(A2,700) 5(B1,30), plus 6(ZZ,40) with a dangling
    // customer and 7(null,50) with none: both pass a Total < 500 filter yet can never join.
    private Names Seed()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var names = new Names(
            TemporaryDatabaseFixtureBase.CreateCollectionName("fiCusts") + suffix,
            TemporaryDatabaseFixtureBase.CreateCollectionName("fiOrds") + suffix,
            TemporaryDatabaseFixtureBase.CreateCollectionName("fiLines") + suffix,
            TemporaryDatabaseFixtureBase.CreateCollectionName("fiRegions") + suffix,
            TemporaryDatabaseFixtureBase.CreateCollectionName("fiAnimals") + suffix);

        database.MongoDatabase.GetCollection<BsonDocument>(names.Custs).InsertMany(
        [
            new BsonDocument { { "_id", "A1" }, { "Name", "Alpha1" }, { "RegionId", "R1" } },
            new BsonDocument { { "_id", "A2" }, { "Name", "Alpha2" } },
            new BsonDocument { { "_id", "B1" }, { "Name", "Beta1" } }
        ]);
        database.MongoDatabase.GetCollection<BsonDocument>(names.Ords).InsertMany(
        [
            new BsonDocument { { "_id", 1 }, { "Total", 10 }, { "CustId", "A1" }, { "Tag", "Alpha1" } },
            new BsonDocument { { "_id", 2 }, { "Total", 20 }, { "CustId", "A1" } },
            new BsonDocument { { "_id", 3 }, { "Total", 600 }, { "CustId", "A1" }, { "Tag", "Alpha1" } },
            new BsonDocument { { "_id", 4 }, { "Total", 700 }, { "CustId", "A2" } },
            new BsonDocument { { "_id", 5 }, { "Total", 30 }, { "CustId", "B1" } },
            new BsonDocument { { "_id", 6 }, { "Total", 40 }, { "CustId", "ZZ" } },
            new BsonDocument { { "_id", 7 }, { "Total", 50 }, { "CustId", BsonNull.Value } }
        ]);
        database.MongoDatabase.GetCollection<BsonDocument>(names.Regions).InsertOne(
            new BsonDocument { { "_id", "R1" }, { "Label", "North" } });
        database.MongoDatabase.GetCollection<BsonDocument>(names.Lines).InsertMany(
        [
            new BsonDocument { { "_id", 1 }, { "OrdId", 1 }, { "Sku", "s1" } },
            new BsonDocument { { "_id", 2 }, { "OrdId", 1 }, { "Sku", "s2" } },
            new BsonDocument { { "_id", 3 }, { "OrdId", 3 }, { "Sku", "s3" } }
        ]);

        // Animals go through EF so the TPH discriminator is stored exactly as the provider reads it.
        using (var seeding = Create(names, MongoQueryMode.DriverLinq))
        {
            seeding.AddRange(
                new Dog { Id = 1, Name = "Rex", OwnerId = "A1" },
                new Dog { Id = 2, Name = "Zed", OwnerId = "A1" },
                new Cat { Id = 3, Name = "Tom", OwnerId = "A1" },
                new Dog { Id = 4, Name = "Fido", OwnerId = "B1" });
            seeding.SaveChanges();
        }

        return names;
    }

    private void AssertModes(Func<Ctx, List<string>> query, List<string> expected, DriverLeg driverLeg = DriverLeg.Throws)
    {
        var names = Seed();

        foreach (var mode in new[] { MongoQueryMode.NativeOnly, MongoQueryMode.Native })
        {
            using var db = Create(names, mode);
            Assert.True(expected.SequenceEqual(query(db)), $"{mode}: expected [{string.Join(", ", expected)}]");
        }

        using var driverDb = Create(names, MongoQueryMode.DriverLinq);
        if (driverLeg == DriverLeg.Throws)
        {
            Assert.ThrowsAny<Exception>(() => query(driverDb));
        }
        else
        {
            Assert.Equal(expected, query(driverDb));
        }
    }

    [Fact]
    public void Filter_only_inner_of_an_inner_join()
        => AssertModes(
            db => (from c in db.Set<Cust>()
                   join o in db.Set<Ord>().Where(o => o.Total < 500) on c.Id equals o.CustId
                   select new { c, o }).AsNoTracking().AsEnumerable()
                .Select(x => $"{x.c.Id}:{x.o.Id}").OrderBy(s => s).ToList(),
            ["A1:1", "A1:2", "B1:5"]);

    [Fact]
    public void Filter_only_inner_of_a_left_join()
        => AssertModes(
            db => (from c in db.Set<Cust>()
                   join o in db.Set<Ord>().Where(o => o.Total < 500) on c.Id equals o.CustId into g
                   from o in g.DefaultIfEmpty()
                   select new { c, o }).AsNoTracking().AsEnumerable()
                .Select(x => $"{x.c.Id}:{(x.o == null ? "null" : x.o.Id.ToString())}").OrderBy(s => s).ToList(),
            // A2's only order (700) is filtered out: the customer survives with a null inner.
            ["A1:1", "A1:2", "A2:null", "B1:5"]);

    [Fact]
    public void Filter_and_reference_Include_on_the_inner_keeps_one_customer_instance()
        => AssertModes(
            db => (from c in db.Set<Cust>().Where(c => c.Id.StartsWith("A"))
                   join o in db.Set<Ord>().Where(o => o.Total < 500).Include(o => o.Cust) on c.Id equals o.CustId
                   select new { c, o }).AsNoTrackingWithIdentityResolution().AsEnumerable()
                .Select(x => $"{x.c.Id}:{x.o.Id}:{x.o.Cust?.Name}:same={ReferenceEquals(x.c, x.o.Cust)}").OrderBy(s => s).ToList(),
            // The spec shape (Perform_identity_resolution_reuses_same_instances_across_joins): orders 3 and 4 are
            // filtered out. Driver-LINQ returns them, so its leg must refuse rather than answer.
            ["A1:1:Alpha1:same=True", "A1:2:Alpha1:same=True"]);

    [Fact]
    public void Reference_Include_on_a_bare_inner_is_unchanged()
        => AssertModes(
            db => (from c in db.Set<Cust>()
                   join o in db.Set<Ord>().Include(o => o.Cust) on c.Id equals o.CustId
                   select new { c, o }).AsNoTracking().AsEnumerable()
                .Select(x => $"{x.c.Id}:{x.o.Id}:{x.o.Cust?.Name}").OrderBy(s => s).ToList(),
            ["A1:1:Alpha1", "A1:2:Alpha1", "A1:3:Alpha1", "A2:4:Alpha2", "B1:5:Beta1"],
            DriverLeg.EqualsExpected);

    [Fact]
    public void Conjunction_in_the_inner_filter()
        => AssertModes(
            db => (from c in db.Set<Cust>()
                   join o in db.Set<Ord>().Where(o => o.Total < 500 && o.Id != 2) on c.Id equals o.CustId
                   select new { c, o }).AsNoTracking().AsEnumerable()
                .Select(x => $"{x.c.Id}:{x.o.Id}").OrderBy(s => s).ToList(),
            ["A1:1", "B1:5"]);

    private static List<string> ParameterizedQuery(Ctx db, int? cutoff)
        => (from c in db.Set<Cust>()
            join o in db.Set<Ord>().Where(o => o.Total < cutoff).Include(o => o.Cust) on c.Id equals o.CustId
            select new { c, o }).AsNoTrackingWithIdentityResolution().AsEnumerable()
            .Select(x => $"{x.c.Id}:{x.o.Id}").OrderBy(s => s).ToList();

    [Theory]
    [InlineData(MongoQueryMode.NativeOnly)]
    [InlineData(MongoQueryMode.Native)]
    public void Parameterized_inner_filter_is_read_per_execution(MongoQueryMode mode)
    {
        var names = Seed();

        // One context, one lambda: the second run hits the compiled-query cache, so a baked value would repeat the
        // first run's rows.
        using var db = Create(names, mode);
        NativeModeAssert.TwiceWithDifferentValues(
            v => ParameterizedQuery(db, (int?)v), 25, ["A1:1", "A1:2"], 500, ["A1:1", "A1:2", "B1:5"]);
        NativeModeAssert.TwiceWithDifferentValues(
            v => ParameterizedQuery(db, (int?)v), 5, [], null, []);
    }

    [Fact]
    public void Parameterized_inner_filter_refuses_under_explicit_DriverLinq()
    {
        var names = Seed();
        using var db = Create(names, MongoQueryMode.DriverLinq);
        Assert.ThrowsAny<Exception>(() => ParameterizedQuery(db, 25));
    }

    // EF applies a Take written BEFORE a join to the outer rows first. Orders 7 and 6 (highest ids) have no customer, so
    // `OrderByDescending(Id).Take(2).Join(...)` pages [7, 6] and the inner join drops both: the answer is empty. A native
    // pipeline that pages AFTER the $lookup/$unwind would page the joined rows [5, 4] instead. NativeOnly may decline;
    // what it must never do is answer with the wrong rows.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Take_before_a_row_dropping_join_pages_the_outer_rows_first(bool filteredInner)
    {
        var names = Seed();

        List<string> Run(Ctx db)
            => (filteredInner
                    ? db.Set<Ord>().OrderByDescending(o => o.Id).Take(2)
                        .Join(db.Set<Cust>().Where(c => c.Id != "ZZZ"), o => o.CustId, c => c.Id, (o, c) => new { o, c })
                    : db.Set<Ord>().OrderByDescending(o => o.Id).Take(2)
                        .Join(db.Set<Cust>(), o => o.CustId, c => c.Id, (o, c) => new { o, c }))
                .AsNoTracking().AsEnumerable().Select(x => $"{x.o.Id}:{x.c.Id}").OrderBy(s => s).ToList();

        foreach (var mode in new[] { MongoQueryMode.NativeOnly, MongoQueryMode.Native })
        {
            using var db = Create(names, mode);
            try
            {
                Assert.Empty(Run(db));
            }
            catch (NativeTranslationNotSupportedException) when (mode == MongoQueryMode.NativeOnly)
            {
            }
        }
    }

    // A join whose inner side is FILTERED drops rows legitimately even over a required foreign key, so the Take written
    // before it must still page the outer rows first. Orders 1 and 2 (A1) are the page; the inner keeps only B1, so
    // the answer is empty. Paging after the join would page the joined rows [5] instead and answer [5:B1].
    [Fact]
    public void Take_before_a_filtered_join_over_a_required_foreign_key_pages_the_outer_rows_first()
    {
        var names = Seed();

        foreach (var mode in new[] { MongoQueryMode.NativeOnly, MongoQueryMode.Native })
        {
            using var db = Create(names, mode);
            try
            {
                Assert.Empty(
                    db.Set<ReqOrd>().OrderBy(o => o.Id).Take(2)
                        .Join(db.Set<Cust>().Where(c => c.Id == "B1"), o => o.CustId, c => c.Id, (o, c) => new { o, c })
                        .AsNoTracking().AsEnumerable().Select(x => $"{x.o.Id}:{x.c.Id}").OrderBy(s => s).ToList());
            }
            catch (NativeTranslationNotSupportedException) when (mode == MongoQueryMode.NativeOnly)
            {
            }
        }
    }

    // The companion: the first two orders do have customers, so the page survives the join intact.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Take_before_a_join_keeps_the_paged_rows_that_match(bool filteredInner)
    {
        var names = Seed();

        List<string> Run(Ctx db)
            => (filteredInner
                    ? db.Set<Ord>().OrderBy(o => o.Id).Take(2)
                        .Join(db.Set<Cust>().Where(c => c.Id != "ZZZ"), o => o.CustId, c => c.Id, (o, c) => new { o, c })
                    : db.Set<Ord>().OrderBy(o => o.Id).Take(2)
                        .Join(db.Set<Cust>(), o => o.CustId, c => c.Id, (o, c) => new { o, c }))
                .AsNoTracking().AsEnumerable().Select(x => $"{x.o.Id}:{x.c.Id}").OrderBy(s => s).ToList();

        foreach (var mode in new[] { MongoQueryMode.NativeOnly, MongoQueryMode.Native })
        {
            using var db = Create(names, mode);
            try
            {
                Assert.Equal(["1:A1", "2:A1"], Run(db));
            }
            catch (NativeTranslationNotSupportedException) when (mode == MongoQueryMode.NativeOnly)
            {
            }
        }
    }

    // A collection Include on the inner side composes with the carried filter: the nested stages follow the filter in the
    // same sub-pipeline, so each kept order carries its own lines and a filtered-out order's lines are never read.
    [Fact]
    public void Collection_Include_on_a_filtered_inner_reads_the_kept_orders_lines()
        => AssertModes(
            db => (from c in db.Set<Cust>()
                   join o in db.Set<Ord>().Where(o => o.Total < 500).Include(o => o.Lines) on c.Id equals o.CustId
                   select new { c, o }).AsNoTracking().AsEnumerable()
                .Select(x => $"{x.c.Id}:{x.o.Id}:{string.Join(",", x.o.Lines.Select(l => l.Sku).OrderBy(k => k))}")
                .OrderBy(s => s).ToList(),
            ["A1:1:s1,s2", "A1:2:", "B1:5:"]);

    // Shapes the carried filter does NOT cover. Each must decline in NativeOnly and may never answer with unfiltered rows
    // in the other modes: they either throw or return exactly the filtered rows.

    [Fact]
    public void Composite_anonymous_key_with_a_filtered_inner_declines_cleanly()
        => AssertDeclinesNeverUnfiltered(
            db => (from c in db.Set<Cust>()
                   join o in db.Set<Ord>().Where(o => o.Total < 500) on new { c.Id, Nm = c.Name } equals new { Id = o.CustId, Nm = o.Tag }
                   select new { c, o }).AsNoTracking().AsEnumerable().Select(x => $"{x.c.Id}:{x.o.Id}").OrderBy(s => s).ToList(),
            ["A1:1"]);

    // A TPH-derived entity set (Set<Dog>()) must be narrowed by discriminator wherever it appears. Animals: Dogs Rex, Zed,
    // Fido, and the Cat Tom. Without the narrowing a Cat is materialized as a Dog (a crash, or, when only scalars are
    // projected, a silently wrong row).

    [Fact]
    public void Derived_entity_set_returns_only_the_derived_type()
        => AssertModes(
            db => db.Set<Dog>().AsNoTracking().AsEnumerable().Select(d => d.Name).OrderBy(n => n).ToList(),
            ["Fido", "Rex", "Zed"], DriverLeg.EqualsExpected);

    [Fact]
    public void Derived_entity_set_with_a_filter_returns_only_the_derived_type()
        => AssertModes(
            db => db.Set<Dog>().Where(d => d.Name != "Zed").AsNoTracking().AsEnumerable().Select(d => d.Name).OrderBy(n => n).ToList(),
            ["Fido", "Rex"], DriverLeg.EqualsExpected);

    [Fact]
    public void Sibling_derived_entity_set_returns_only_its_own_type()
        => AssertModes(
            db => db.Set<Cat>().AsNoTracking().AsEnumerable().Select(c => c.Name).OrderBy(n => n).ToList(),
            ["Tom"], DriverLeg.EqualsExpected);

    [Fact]
    public void Derived_entity_set_as_the_outer_side_of_a_join_returns_only_the_derived_type()
        => AssertModes(
            db => (from d in db.Set<Dog>()
                   join c in db.Set<Cust>() on d.OwnerId equals c.Id
                   select new { c, d }).AsNoTracking().AsEnumerable().Select(x => $"{x.c.Id}:{x.d.Name}").OrderBy(s => s).ToList(),
            ["A1:Rex", "A1:Zed", "B1:Fido"], DriverLeg.EqualsExpected);

    [Fact]
    public void Derived_entity_set_as_a_join_inner_returns_only_the_derived_type()
        => AssertModes(
            db => (from c in db.Set<Cust>()
                   join d in db.Set<Dog>() on c.Id equals d.OwnerId
                   select new { c, d }).AsNoTracking().AsEnumerable().Select(x => $"{x.c.Id}:{x.d.Name}").OrderBy(s => s).ToList(),
            ["A1:Rex", "A1:Zed", "B1:Fido"]);

    // Projecting only scalars off a derived join inner is a separate native limitation (the inner scope is the root type,
    // the member's is the derived type) and declines. It is the silent case: without the narrowing, Tom the Cat comes back
    // as a Dog named Tom.
    [Fact]
    public void Derived_entity_set_as_a_join_inner_projected_to_scalars_never_returns_the_sibling_type()
        => AssertDeclinesNeverUnfiltered(
            db => (from c in db.Set<Cust>()
                   join d in db.Set<Dog>() on c.Id equals d.OwnerId
                   select new { c.Id, d.Name }).AsEnumerable().Select(x => $"{x.Id}:{x.Name}").OrderBy(s => s).ToList(),
            ["A1:Rex", "A1:Zed", "B1:Fido"]);

    [Fact]
    public void Derived_entity_set_with_a_filtered_inner_keeps_the_discriminator_and_the_filter()
        => AssertModes(
            db => (from c in db.Set<Cust>()
                   join d in db.Set<Dog>().Where(d => d.Name != "Zed") on c.Id equals d.OwnerId
                   select new { c, d }).AsNoTracking().AsEnumerable().Select(x => $"{x.c.Id}:{x.d.Name}").OrderBy(s => s).ToList(),
            ["A1:Rex", "B1:Fido"]);

    [Fact]
    public void ThenInclude_on_the_inner_reference_Include_with_a_filtered_inner_declines_cleanly()
        => AssertDeclinesNeverUnfiltered(
            db => (from c in db.Set<Cust>()
                   join o in db.Set<Ord>().Where(o => o.Total < 500).Include(o => o.Cust).ThenInclude(c => c!.Region) on c.Id equals o.CustId
                   select new { c, o }).AsNoTracking().AsEnumerable()
                .Select(x => $"{x.c.Id}:{x.o.Id}:{x.o.Cust?.Region?.Label ?? "-"}").OrderBy(s => s).ToList(),
            ["A1:1:North", "A1:2:North", "B1:5:-"]);

    private void AssertDeclinesNeverUnfiltered(Func<Ctx, List<string>> query, List<string> expected)
    {
        var names = Seed();

        using (var nativeOnly = Create(names, MongoQueryMode.NativeOnly))
        {
            Assert.Throws<NativeTranslationNotSupportedException>(() => query(nativeOnly));
        }

        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq })
        {
            using var db = Create(names, mode);
            List<string>? rows = null;
            try
            {
                rows = query(db);
            }
            catch (Exception)
            {
                // Refusing loudly is correct.
            }

            if (rows is not null)
            {
                Assert.True(expected.SequenceEqual(rows), $"{mode} returned [{string.Join(", ", rows)}], expected [{string.Join(", ", expected)}]");
            }
        }
    }

    // Declining pins: shapes outside "filter only" stay on today's path. NativeOnly refuses; Native and DriverLinq
    // must agree (rows, or both fail), so the inner's ordering/paging/projection/dedup is never dropped silently.

    [Fact]
    public void Inner_with_ordering_and_paging_declines_cleanly()
        => AssertDeclines(db => (from c in db.Set<Cust>()
                                 join o in db.Set<Ord>().Where(o => o.Total < 500).OrderBy(o => o.Total).Take(1) on c.Id equals o.CustId
                                 select new { c, o }).AsNoTracking().AsEnumerable()
            .Select(x => $"{x.c.Id}:{x.o.Id}").OrderBy(s => s).ToList());

    [Fact]
    public void Inner_projection_over_a_filter_applies_the_filter()
        => AssertModes(
            // EF keeps an inner Select as a pending selector applied to the join result, so at join time the inner is
            // just the filtered scan: the same carried-filter shape as the first test, projected afterwards.
            db => (from c in db.Set<Cust>()
                   join o in db.Set<Ord>().Where(o => o.Total < 500).Select(o => new { o.CustId, o.Total }) on c.Id equals o.CustId
                   select new { c.Id, o.Total }).AsEnumerable()
                .Select(x => $"{x.Id}:{x.Total}").OrderBy(s => s).ToList(),
            ["A1:10", "A1:20", "B1:30"]);

    [Fact]
    public void Inner_with_Distinct_declines_cleanly()
        => AssertDeclines(db => (from c in db.Set<Cust>()
                                 join o in db.Set<Ord>().Where(o => o.Total < 500).Distinct() on c.Id equals o.CustId
                                 select new { c, o }).AsNoTracking().AsEnumerable()
            .Select(x => $"{x.c.Id}:{x.o.Id}").OrderBy(s => s).ToList());

    private void AssertDeclines(Func<Ctx, List<string>> query)
    {
        var names = Seed();

        using (var nativeOnly = Create(names, MongoQueryMode.NativeOnly))
        {
            Assert.Throws<NativeTranslationNotSupportedException>(() => query(nativeOnly));
        }

        static (List<string>? Rows, Type? Exception) Run(Func<List<string>> run)
        {
            try
            {
                return (run(), null);
            }
            catch (Exception e)
            {
                return (null, e.GetType());
            }
        }

        using var nativeDb = Create(names, MongoQueryMode.Native);
        using var driverDb = Create(names, MongoQueryMode.DriverLinq);
        var native = Run(() => query(nativeDb));
        var driver = Run(() => query(driverDb));

        Assert.Equal(driver.Rows is null, native.Rows is null);
        if (driver.Rows is not null)
        {
            Assert.Equal(driver.Rows, native.Rows);
        }
    }

    private Ctx Create(Names names, MongoQueryMode mode)
    {
        var builder = new DbContextOptionsBuilder<Ctx>()
            .UseMongoDB(
                database.Client, database.MongoDatabase.DatabaseNamespace.DatabaseName,
                o => o.UseQueryMode(mode))
            .ReplaceService<IModelCacheKeyFactory, UniqueModelCacheKeyFactory>()
            .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
        return new Ctx(builder.Options, names);
    }

    private sealed class UniqueModelCacheKeyFactory : IModelCacheKeyFactory
    {
        private static int _count;
        public object Create(DbContext context, bool designTime) => Interlocked.Increment(ref _count);
    }

    private sealed class Ctx(DbContextOptions<Ctx> options, Names names) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder mb)
        {
            mb.Entity<Region>().ToCollection(names.Regions);
            mb.Entity<Cust>(b =>
            {
                b.ToCollection(names.Custs);
                b.HasOne(c => c.Region).WithMany().HasForeignKey(c => c.RegionId);
            });
            mb.Entity<Line>().ToCollection(names.Lines);
            mb.Entity<Ord>(b =>
            {
                b.ToCollection(names.Ords);
                b.HasOne(o => o.Cust).WithMany().HasForeignKey(o => o.CustId);
                b.HasMany(o => o.Lines).WithOne().HasForeignKey(l => l.OrdId);
            });
            mb.Entity<Animal>().ToCollection(names.Animals);
            mb.Entity<Dog>();
            mb.Entity<Cat>();
            mb.Entity<ReqOrd>(b =>
            {
                b.ToCollection(names.Ords);
                b.HasOne(o => o.Cust).WithMany().HasForeignKey(o => o.CustId).IsRequired();
            });
        }
    }
}
