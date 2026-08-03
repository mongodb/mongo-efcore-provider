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
using System.Threading;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.Infrastructure;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// A Join/GroupJoin/LeftJoin whose INNER sequence pages itself (Skip/Take) is declined by the provider, because
/// the MongoDB driver's LINQ provider mistranslates it — CSHARP-6017 — by folding the uncorrelated inner's
/// $sort/$skip/$limit into the CORRELATED $lookup sub-pipeline, where they run per-outer-row over the
/// key-matched subset for that one outer row (at most one document when the join key is unique in the inner
/// collection, as in this file's fixture) instead of once over the whole inner sequence. The fallback therefore
/// returns silently WRONG rows, so the shape must hard-decline rather than fall back.
/// TODO(CSHARP-6017): delete MOST of this file when the driver is fixed — NOT all of it. See the removal
/// checklist in docs/superpowers/specs/2026-07-31-groupby-join-uncorrelated-inner-decline-design.md §2.6, which
/// enumerates the split; the tripwire test below is what announces the fix. The split, restated here so it is
/// visible at the point of deletion:
///   DELETE (they exist only because the driver folds): Join_with_paged_inner_declines_under_native,
///   Join_with_paged_inner_declines_under_native_only, Join_with_paged_inner_never_returns_the_wrong_rows_under_native,
///   Join_with_paged_inner_still_runs_under_driver_linq, GroupJoin_with_paged_inner_declines_under_native,
///   Join_with_inner_paged_after_a_set_operation_declines_under_native,
///   Join_with_a_projected_Distinct_then_paged_inner_declines_under_native, and the tripwire itself.
///   KEEP (general join-correctness controls and this branch's own over-decline nets — they must still pass
///   after the guard is gone, and re-verifying them is how you prove the removal did not break anything):
///   Join_with_paged_outer_still_runs_and_is_correct, Join_with_reshaped_unpaged_inner_still_runs_and_is_correct.
///   KEEP, and do not confuse with the above: Join_whose_inner_subquery_is_grouped_and_joined_declines_under_native
///   pins PropagateFallbackWrongDataFrom, the PERMANENT, independent EF-344 fix, and has no paging at all.
///   Join_with_grouped_outer_and_paged_inner_reports_both_causes stays but its message assertion degenerates to
///   the single GroupBy+Join fragment (see its own TODO).
/// If every KEEP test is removed along with the guard, the file's deletion silently drops general join coverage.
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeJoinPagedInnerDeclineTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    // The correct answer for PagedInnerJoin below: Regions ordered by Country are FR, UK, US; Take(2) keeps
    // FR and UK; the orders in those two countries are FR/300, UK/50, UK/25.
    private static readonly string[] CorrectRows = ["FR:EU", "UK:EU", "UK:EU"];

    // What the CSHARP-6017 fold returns instead: the $sort/$limit run inside the per-order $lookup, where every
    // order's single key match survives, so all five orders join.
    private const int FoldedWrongRowCount = 5;

    private static string[] PagedInnerJoin(PagedJoinDbContext db)
        => db.Orders
            .Join(db.Regions.OrderBy(r => r.Country).Take(2),
                o => o.Country, r => r.Country, (o, r) => new { o.Country, r.Continent })
            .AsEnumerable()
            .Select(x => x.Country + ":" + x.Continent)
            .OrderBy(s => s)
            .ToArray();

    [Fact]
    public void Join_with_paged_outer_still_runs_and_is_correct()
    {
        // CONTROL for an over-broad predicate that looks at the OUTER side too. Paging on the outer is emitted
        // at pipeline TOP LEVEL, before the $lookup, and is correct — it must keep working.
        using var db = CreateContext(MongoQueryMode.Native, nameof(Join_with_paged_outer_still_runs_and_is_correct));

        var rows = db.Orders.OrderBy(o => o.Amount).Take(2)
            .Join(db.Regions, o => o.Country, r => r.Country, (o, r) => new { o.Country, r.Continent })
            .AsEnumerable()
            .Select(x => x.Country + ":" + x.Continent)
            .OrderBy(s => s)
            .ToArray();

        // The two cheapest orders are UK/25 and UK/50.
        Assert.Equal(["UK:EU", "UK:EU"], rows);
    }

    [Fact]
    public void Join_with_reshaped_unpaged_inner_still_runs_and_is_correct()
    {
        // CONTROL for an over-broad predicate keyed on "the inner is a reshaping subquery". Driver 3.10 folds an
        // unpaged inner's $sort (or nothing at all) into the $lookup sub-pipeline, which is BENIGN: order within
        // a single-document key match is a no-op. Measured correct; must not be declined.
        using var db = CreateContext(MongoQueryMode.Native,
            nameof(Join_with_reshaped_unpaged_inner_still_runs_and_is_correct));

        var rows = db.Orders
            .Join(db.Regions.OrderBy(r => r.Country).Select(r => new { r.Country, r.Continent }),
                o => o.Country, r => r.Country, (o, r) => new { o.Country, r.Continent })
            .AsEnumerable()
            .Select(x => x.Country + ":" + x.Continent)
            .OrderBy(s => s)
            .ToArray();

        Assert.Equal(["FR:EU", "UK:EU", "UK:EU", "US:NA", "US:NA"], rows);
    }

    [Fact]
    public void Join_with_paged_inner_still_runs_under_driver_linq()
    {
        // Explicit DriverLinq is the user's documented opt-in to the previous path, wrong-data caveat included —
        // exactly as for the GroupBy+Join decline. It must never throw NativeTranslationNotSupportedException.
        using var db = CreateContext(MongoQueryMode.DriverLinq,
            nameof(Join_with_paged_inner_still_runs_under_driver_linq));

        var ex = Record.Exception(() => PagedInnerJoin(db));

        Assert.IsNotType<NativeTranslationNotSupportedException>(ex);
    }

    [Fact]
    public void Driver_still_folds_a_paged_join_inner_into_the_lookup_subpipeline_CSHARP_6017()
    {
        // EXPIRY TRIPWIRE, not a desired behavior. It pins the CSHARP-6017 driver defect that the provider guard
        // exists for, using the only mode that still reaches the driver. The CORRECT answer is CorrectRows (3
        // rows); the driver returns 5 because it folds $sort/$limit into the correlated $lookup sub-pipeline.
        //
        // WHEN THIS TEST FAILS, THE DRIVER HAS BEEN FIXED. Follow the removal checklist in
        // docs/superpowers/specs/2026-07-31-groupby-join-uncorrelated-inner-decline-design.md §2.6:
        // delete the guard-only tests in this file (NOT the whole file — see the DELETE/KEEP split in the
        // class-level comment above), the HasPagingAnywhere block in TranslateJoinCore, MongoSelectDefinition's
        // MarkPagedJoinInnerFallbackUnsafe/IsPagedJoinInnerFallbackUnsafe/HasPagingAnywhere/MarkSawUnrecordedPaging
        // and the three NativeSlotPopulator MarkSawUnrecordedPaging call sites, collapse
        // IsFallbackWrongData back to IsGroupByFallbackUnsafe, revert the spec-suite retargets whose baseline
        // is CSHARP-6017-only (Join_customers_orders_with_subquery_with_take and its five siblings, plus
        // Reverse_in_join_inner_with_skip — NOT Join_GroupBy_Aggregate_in_subquery, whose decline is permanent,
        // see below), and revert ONLY the paged-inner sentences of the Query/AGENTS.md decline note, restoring
        // its single "falls back correctly and is unaffected" sentence for join-then-group. Do NOT delete
        // PropagateFallbackWrongDataFrom or the group-first GroupBy+Join paragraph in that same AGENTS.md
        // note — both are permanent and unrelated to CSHARP-6017 (PropagateFallbackWrongDataFrom fixes an
        // unrelated EF-344 nesting hole).
        using var db = CreateContext(MongoQueryMode.DriverLinq,
            nameof(Driver_still_folds_a_paged_join_inner_into_the_lookup_subpipeline_CSHARP_6017));

        var rows = PagedInnerJoin(db);

        Assert.Equal(FoldedWrongRowCount, rows.Length);
        Assert.NotEqual(CorrectRows, rows);
    }

    [Fact]
    public void Join_with_paged_inner_declines_under_native()
    {
        using var db = CreateContext(MongoQueryMode.Native, nameof(Join_with_paged_inner_declines_under_native));

        Assert.Throws<NativeTranslationNotSupportedException>(() => PagedInnerJoin(db));
    }

    [Fact]
    public void Join_with_paged_inner_declines_under_native_only()
    {
        // NOT a mutation pin for the paging guard — GUARD-INDEPENDENT, and the design spec's §2.8 row says so
        // too. QueryableMethods.Join is absent from NativeSlotPopulator.IsNativeRepresentableSlotOperator, and
        // PopulateNativeSlots still runs for Join after the switch, so its catch-all sets Route = Fallback for
        // EVERY join query — NativeOnly therefore throws with or without the HasPagingAnywhere block. This test
        // documents the NativeOnly disposition (it must be the same clean decline, not a different failure);
        // Join_with_paged_inner_declines_under_native and Join_with_paged_inner_never_returns_the_wrong_rows_under_native
        // are the tests that actually pin the guard.
        using var db = CreateContext(MongoQueryMode.NativeOnly,
            nameof(Join_with_paged_inner_declines_under_native_only));

        Assert.Throws<NativeTranslationNotSupportedException>(() => PagedInnerJoin(db));
    }

    [Fact]
    public void Join_with_a_projected_Distinct_then_paged_inner_declines_under_native()
    {
        // REGRESSION PIN for MongoSelectDefinition.MarkSawUnrecordedPaging (EF-366 final fix wave). Every other
        // paged-inner fact in this file pages via a Skip/Take that NativeSlotPopulator RECORDS as a MongoSkipOp /
        // MongoLimitOp, so HasPagingAnywhere finds it by scanning the op lists. This one cannot be found that
        // way: Select(r => new {r.Country}).Distinct() binds natively (TryBindDistinctFromProjection sets
        // IsDistinct), which makes HasTerminalOperator true, so the following Take(1) hits PopulateNativeSlots'
        // post-terminal early return and is SWALLOWED — no paging op is appended to either list.
        //
        // MEASURED before the fix: HasPagingAnywhere was false, TranslateJoinCore took the graceful
        // `else if (IsDistinct)` arm instead of the hard decline, and the query executed on driver-LINQ with the
        // Take(1) still in the captured chain — returning ALL FIVE orders where at most two is correct, silently,
        // under DEFAULT Native as well as explicit DriverLinq. The captured MQL showed the inner's
        // $project/$group/$replaceRoot/$limit:1 folded bodily into the correlated $lookup's own sub-pipeline.
        //
        // The correct answer is the orders of whichever ONE country Take(1) keeps. Regions are FR, UK, US and the
        // degenerate $group reorders, so which one is unspecified: FR -> 1 row, UK -> 2 rows, US -> 2 rows. The
        // fold instead keeps every order's single key match and returns 5. So "at most 2, never 5" is the
        // wrong-data pin, and it is asserted in the `ex is null` branch — the branch that RUNS under mutation.
        using var db = CreateContext(MongoQueryMode.Native,
            nameof(Join_with_a_projected_Distinct_then_paged_inner_declines_under_native));

        string[]? rows = null;
        var ex = Record.Exception(() => rows = db.Orders
            .Join(db.Regions.Select(r => new {r.Country}).Distinct().Take(1),
                o => o.Country, r => r.Country, (o, r) => new {o.Country, o.Amount})
            .AsEnumerable()
            .Select(x => x.Country + ":" + x.Amount)
            .OrderBy(s => s)
            .ToArray());

        if (ex is null)
        {
            Assert.InRange(rows!.Length, 1, 2);
            Assert.Single(rows.Select(r => r.Split(':')[0]).Distinct());
        }
        else
        {
            Assert.IsType<NativeTranslationNotSupportedException>(ex);
        }
    }

    [Fact]
    public void Join_with_paged_inner_never_returns_the_wrong_rows_under_native()
    {
        // MUTATION PIN for the data, deliberately NOT phrased as "it throws" — that is the job of
        // Join_with_paged_inner_declines_under_native, and a wrong-rows assertion placed AFTER a decline
        // assertion in the same method is unreachable exactly when the guard is deleted. Here the data
        // comparison is the branch that RUNS under mutation: delete the guard and the query executes, returns
        // the folded 5 rows, and Assert.Equal fails. Only two outcomes are acceptable — a clean decline, or the
        // correct rows (which is also what makes this test survive the eventual driver fix).
        using var db = CreateContext(MongoQueryMode.Native,
            nameof(Join_with_paged_inner_never_returns_the_wrong_rows_under_native));

        string[]? rows = null;
        var ex = Record.Exception(() => rows = PagedInnerJoin(db));

        if (ex is null)
        {
            Assert.Equal(CorrectRows, rows);
        }
        else
        {
            Assert.IsType<NativeTranslationNotSupportedException>(ex);
        }
    }

#if !EF8 && !EF9
    [Fact]
    public void GroupJoin_with_paged_inner_declines_under_native()
    {
        // Empirically confirmed (temporary instrumentation in TranslateJoin/TranslateGroupJoin/TranslateLeftJoin,
        // removed before commit): "join ... into rs / from r in rs select ..." WITHOUT DefaultIfEmpty collapses
        // to an ordinary inner Join and reaches TranslateJoin — a near-duplicate of
        // Join_with_paged_inner_declines_under_native, giving TranslateGroupJoin/TranslateLeftJoin no coverage
        // of their own. Adding ".DefaultIfEmpty()" here instead reaches TranslateLeftJoin (confirmed via the
        // same instrumentation) — but ONLY on EF10: on EF8/EF9 the identical query-syntax spelling normalizes
        // to GroupJoin(...).SelectMany(rs.DefaultIfEmpty(), resultSelector), which this provider cannot
        // translate at all (a pre-existing, unrelated SelectMany-over-a-GroupJoin-grouping gap, nothing to do
        // with paging or CSHARP-6017) — it throws InvalidOperationException before ever reaching
        // TranslateJoinCore. So this test is EF10-only; EF8/EF9 have no reachable ordinary-LINQ spelling that
        // exercises TranslateGroupJoin/TranslateLeftJoin for a paged inner, and are left uncovered here rather
        // than covered by a misleading duplicate.
        using var db = CreateContext(MongoQueryMode.Native, nameof(GroupJoin_with_paged_inner_declines_under_native));

        Assert.Throws<NativeTranslationNotSupportedException>(() =>
            (from o in db.Orders
             join r in db.Regions.OrderBy(x => x.Country).Take(2) on o.Country equals r.Country into rs
             from r in rs.DefaultIfEmpty()
             select new { o.Country, Continent = r != null ? r.Continent : null }).ToArray());
    }
#endif

    [Fact]
    public void Join_with_grouped_outer_and_paged_inner_reports_both_causes()
    {
        // Regression pin for the dual-cause finding carried forward from Task 2's review: a query that is BOTH
        // GroupBy+Join (outer) AND paged-join-inner sets BOTH MongoSelectDefinition.IsGroupByFallbackUnsafe and
        // IsPagedJoinInnerFallbackUnsafe on the SAME outer select — confirmed empirically (a temporary debug
        // print at MongoShapedQueryCompilingExpressionVisitor.VisitShapedQuery showed both flags true for
        // exactly this shape). MongoShapedQueryCompilingExpressionVisitor.VisitShapedQuery must report BOTH
        // causes in the thrown message, not silently drop one to the other (the pre-Task-4 ternary did exactly
        // that, always naming the paged-inner cause and never the GroupBy+Join one).
        // TODO(CSHARP-6017): once the paged-inner cause is removed from the gate, this test's message assertion
        // degenerates to the single GroupBy+Join fragment; update it together with the rest of the removal
        // checklist (see the TODO at MongoShapedQueryCompilingExpressionVisitor.cs and
        // MongoQueryableMethodTranslatingExpressionVisitor.TranslateJoinCore).
        using var db = CreateContext(MongoQueryMode.Native,
            nameof(Join_with_grouped_outer_and_paged_inner_reports_both_causes));

        var ex = Assert.Throws<NativeTranslationNotSupportedException>(() =>
            db.Orders.GroupBy(o => o.Country).Select(g => new { g.Key, Max = g.Max(o => o.Amount) })
                .Join(db.Regions.OrderBy(r => r.Country).Take(2), a => a.Key, r => r.Country, (a, r) => new { r.Continent })
                .ToArray());

        Assert.Contains("Query combines GroupBy with a Join", ex.Message);
        Assert.Contains("CSHARP-6017", ex.Message);
    }

    [Fact]
    public void Join_with_inner_paged_after_a_set_operation_declines_under_native()
    {
        // Regression pin for MongoSelectDefinition.HasPagingAnywhere vs. the narrower HasPaging (Minor 5). All
        // four other paged-inner facts in this file page via an ordinary MongoSkipOp/MongoLimitOp landing in
        // PipelineOps, which even HasPaging (PipelineOps-only) would already see — so none of them can tell
        // HasPagingAnywhere apart from HasPaging. The entire reason HasPagingAnywhere exists instead of reusing
        // HasPaging is a Take/Skip composed AFTER a set operation (Union/Concat/Intersect/Except): EF-347 slice B
        // records that into TrailingOps, not PipelineOps, so HasPaging (which deliberately scans PipelineOps
        // only — its own consumer gates a PRE-terminal GroupBy that is unreachable after a set op) would MISS
        // it, while HasPagingAnywhere's extra _trailingOps.Exists check sees it. This inner pages via a
        // Union-then-Take, so its $limit lands in TrailingOps; the join must still decline.
        using var db = CreateContext(MongoQueryMode.Native,
            nameof(Join_with_inner_paged_after_a_set_operation_declines_under_native));

        Assert.Throws<NativeTranslationNotSupportedException>(() =>
            db.Orders.Join(
                db.Regions.Union(db.Regions).OrderBy(r => r.Country).Take(2),
                o => o.Country, r => r.Country, (o, r) => new { o.Country, r.Continent }).ToArray());
    }

    [Fact]
    public void Join_whose_inner_subquery_is_grouped_and_joined_declines_under_native()
    {
        // Mirrors the spec's Join_GroupBy_Aggregate_in_subquery (the actual EF Core base test projects
        // { o, i.c, i.c.CustomerID } at the outer level -- the entity plus one of its own properties, and
        // deliberately NEVER re-projects the grouped aggregate scalar itself, i.e. i.LastOrderID, at the
        // outermost level; only the MIDDLE select, { c, a.LastOrderID }, touches it). This test follows that
        // same shape -- projecting i.r and i.r.Country rather than i.Max -- because re-projecting the
        // aggregate scalar itself through TWO levels of join rebinding hits an unrelated, pre-existing
        // translation-time crash in this provider (confirmed identical under both Native and DriverLinq, so
        // independent of this ticket's gate/mode machinery entirely): "The LINQ expression
        // 'ProjectionBindingExpression: 1' could not be translated" from MongoProjectionBindingExpressionVisitor.
        // The wrong-data shape under test here (a join over a GROUPED source) is in a SUBQUERY used as the
        // outer join's inner, so MarkGroupByFallbackUnsafe lands on the intermediate MongoQueryExpression, not
        // on the one the gate reads. There is NO paging anywhere here, so the CSHARP-6017 guard cannot fire:
        // only PropagateFallbackWrongDataFrom makes this decline. Deleting that call makes this test fail (the
        // query executes and returns wrong rows) while every other test in this file still passes.
        using var db = CreateContext(MongoQueryMode.Native,
            nameof(Join_whose_inner_subquery_is_grouped_and_joined_declines_under_native));

        Assert.Throws<NativeTranslationNotSupportedException>(() =>
            (from o in db.Orders
             join i in (from r in db.Regions
                        join a in db.Orders.GroupBy(x => x.Country)
                                .Select(g => new { Country = g.Key, Max = g.Max(x => x.Amount) })
                            on r.Country equals a.Country
                        select new { r, a.Max })
                 on o.Country equals i.r.Country
             select new { o.Year, i.r, i.r.Country }).ToArray());
    }

    private PagedJoinDbContext CreateContext(MongoQueryMode mode, string name)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var ordersName = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + "O" + suffix;
        var regionsName = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + "R" + suffix;

        database.MongoDatabase.GetCollection<Order>(ordersName).InsertMany(
        [
            new() { Id = ObjectId.GenerateNewId(), Country = "US", Year = 2020, Amount = 100 },
            new() { Id = ObjectId.GenerateNewId(), Country = "US", Year = 2021, Amount = 200 },
            new() { Id = ObjectId.GenerateNewId(), Country = "UK", Year = 2020, Amount = 50 },
            new() { Id = ObjectId.GenerateNewId(), Country = "UK", Year = 2020, Amount = 25 },
            new() { Id = ObjectId.GenerateNewId(), Country = "FR", Year = 2021, Amount = 300 },
        ]);
        database.MongoDatabase.GetCollection<Region>(regionsName).InsertMany(
        [
            new() { Id = ObjectId.GenerateNewId(), Country = "US", Continent = "NA" },
            new() { Id = ObjectId.GenerateNewId(), Country = "UK", Continent = "EU" },
            new() { Id = ObjectId.GenerateNewId(), Country = "FR", Continent = "EU" },
        ]);

        return new PagedJoinDbContext(database, ordersName, regionsName, mode);
    }

    private class Order
    {
        public ObjectId Id { get; set; }
        public string Country { get; set; } = "";
        public int Year { get; set; }
        public decimal Amount { get; set; }
    }

    private class Region
    {
        public ObjectId Id { get; set; }
        public string Country { get; set; } = "";
        public string Continent { get; set; } = "";
    }

    private class PagedJoinDbContext : DbContext
    {
        private readonly string _ordersCollection;
        private readonly string _regionsCollection;

        public PagedJoinDbContext(
            TemporaryDatabaseFixture database, string ordersCollection, string regionsCollection, MongoQueryMode mode)
            : base(new DbContextOptionsBuilder<PagedJoinDbContext>()
                .UseMongoDB(database.Client, database.MongoDatabase.DatabaseNamespace.DatabaseName,
                    b => b.UseQueryMode(mode))
                .ReplaceService<IModelCacheKeyFactory, IgnoreCacheKeyFactory>()
                .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
                .Options)
        {
            _ordersCollection = ordersCollection;
            _regionsCollection = regionsCollection;
        }

        public DbSet<Order> Orders { get; set; } = null!;
        public DbSet<Region> Regions { get; set; } = null!;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<Order>().ToCollection(_ordersCollection);
            modelBuilder.Entity<Region>().ToCollection(_regionsCollection);
        }

        private sealed class IgnoreCacheKeyFactory : IModelCacheKeyFactory
        {
            private static int _count;
            public object Create(DbContext context, bool designTime) => Interlocked.Increment(ref _count);
        }
    }
}
