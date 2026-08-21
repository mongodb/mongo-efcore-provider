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
/// Boundary and over-decline coverage for a Join/GroupJoin/LeftJoin whose INNER sequence is something other
/// than a bare collection scan.
/// <para>
/// History, because the boundary moved: driver 3.10 silently MIS-FOLDED an uncorrelated join inner's
/// $sort/$skip/$limit into the CORRELATED $lookup sub-pipeline, where they ran per-outer-row over the
/// key-matched subset for that one outer row instead of once over the whole inner sequence — returning
/// silently WRONG rows. The provider carried a native-path guard that hard-declined that shape. Driver 3.11
/// (the version pinned in Versions.props) instead REJECTS such shapes outright with
/// <c>MongoDB.Driver.Linq.ExpressionNotSupportedException</c> ("...because expression must be a MongoDB
/// IQueryable against a collection"), so the wrong-data hazard is gone and the guard has been removed.
/// </para>
/// <para>
/// The 3.11 boundary, as measured: a BARE inner and a PROJECTION-ONLY inner still translate correctly; any
/// inner carrying $match/$sort/$skip/$limit/$group is rejected. That boundary is documented provider-wide as
/// EF-X022 in docs/failing-spec-tests.md, and the spec-suite overrides there assert the rejection.
/// </para>
/// <para>
/// What lives here now: (1) over-decline nets proving the shapes that DO work are not declined
/// (Join_with_paged_outer_still_runs_and_is_correct,
/// Join_with_reshaped_unpaged_inner_still_runs_and_is_correct); (2) a pin on the 3.11 rejection itself
/// (Join_with_ordered_inner_is_rejected_by_the_driver); and (3) two pins on the provider's own PERMANENT,
/// driver-independent GroupBy+Join wrong-data hard-decline
/// (Join_whose_inner_subquery_is_grouped_and_joined_declines_under_native pins
/// MongoSelectDefinition.PropagateFallbackWrongDataFrom — the EF-344 nesting fix — and
/// Join_with_grouped_outer_and_paged_inner_reports_the_GroupBy_cause pins the decline message).
/// </para>
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeJoinInnerDeclineTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
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
        // OVER-DECLINE NET, keyed on "the inner is a reshaping subquery". A projection-only inner is one of the
        // two inner shapes driver 3.11 still translates correctly (the other being a bare collection), so it
        // must not be declined by any provider-side guard. Its ordered sibling below is the rejected case.
        using var db = CreateContext(MongoQueryMode.Native,
            nameof(Join_with_reshaped_unpaged_inner_still_runs_and_is_correct));

        var rows = db.Orders
            .Join(db.Regions.Select(r => new { r.Country, r.Continent }),
                o => o.Country, r => r.Country, (o, r) => new { o.Country, r.Continent })
            .AsEnumerable()
            .Select(x => x.Country + ":" + x.Continent)
            .OrderBy(s => s)
            .ToArray();

        Assert.Equal(["FR:EU", "UK:EU", "UK:EU", "US:NA", "US:NA"], rows);
    }

    [Fact]
    public void Join_with_ordered_inner_is_rejected_by_the_driver()
    {
        // BOUNDARY PIN for driver 3.11. The sibling above (projection-only inner) translates; adding an
        // OrderBy to the inner crosses the line — 3.11 refuses to fold the uncorrelated sub-pipeline into the
        // correlated $lookup and rejects the whole expression instead of mis-translating it as 3.10 did. This
        // is the same rejection the spec suite records as EF-X022; pinned here so the boundary is explicit
        // rather than only visible as a spec-suite failure list.
        using var db = CreateContext(MongoQueryMode.Native, nameof(Join_with_ordered_inner_is_rejected_by_the_driver));

        Assert.Throws<MongoDB.Driver.Linq.ExpressionNotSupportedException>(() => db.Orders
            .Join(db.Regions.OrderBy(r => r.Country).Select(r => new { r.Country, r.Continent }),
                o => o.Country, r => r.Country, (o, r) => new { o.Country, r.Continent })
            .ToArray());
    }

    [Fact]
    public void Join_with_grouped_outer_and_paged_inner_reports_the_GroupBy_cause()
    {
        // Pins the wrong-data decline MESSAGE for a GroupBy+Join. This provenance
        // (MongoSelectDefinition.IsGroupByFallbackUnsafe) is driver-independent and permanent: the native path
        // cannot represent the shape and its driver-LINQ fallback returns an empty joined entity for every
        // grouped row, so the gate must throw rather than route to that fallback. The message must name the
        // GroupBy cause; the gate builds a cause LIST so a future second provenance can be added without
        // silently dropping this one.
        using var db = CreateContext(MongoQueryMode.Native,
            nameof(Join_with_grouped_outer_and_paged_inner_reports_the_GroupBy_cause));

        var ex = Assert.Throws<NativeTranslationNotSupportedException>(() =>
            db.Orders.GroupBy(o => o.Country).Select(g => new { g.Key, Max = g.Max(o => o.Amount) })
                .Join(db.Regions.OrderBy(r => r.Country).Take(2), a => a.Key, r => r.Country, (a, r) => new { r.Continent })
                .ToArray());

        Assert.Contains("Query combines GroupBy with a Join", ex.Message);
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
        // on the one the gate reads. Only PropagateFallbackWrongDataFrom (the permanent EF-344 nesting fix)
        // makes this decline: delete that call and this test fails (the query executes and returns wrong rows)
        // while every other test in this file still passes.
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

    private JoinDeclineDbContext CreateContext(MongoQueryMode mode, string name)
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

        return new JoinDeclineDbContext(database, ordersName, regionsName, mode);
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

    private class JoinDeclineDbContext : DbContext
    {
        private readonly string _ordersCollection;
        private readonly string _regionsCollection;

        public JoinDeclineDbContext(
            TemporaryDatabaseFixture database, string ordersCollection, string regionsCollection, MongoQueryMode mode)
            : base(new DbContextOptionsBuilder<JoinDeclineDbContext>()
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
