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
/// Boundary and over-decline coverage for a Join/GroupJoin/LeftJoin whose inner sequence is not a bare
/// collection scan.
/// <para>
/// Driver 3.10 mis-folded an uncorrelated inner's $sort/$skip/$limit into the correlated $lookup sub-pipeline
/// (CSHARP-6017), returning silently wrong rows. The pinned driver (3.11+) rejects such shapes with
/// <c>ExpressionNotSupportedException</c>, so no provider-side guard is needed. A bare or projection-only inner
/// still translates; any inner with $match/$sort/$skip/$limit/$group is rejected (EF-X022 in
/// docs/failing-spec-tests.md).
/// </para>
/// <para>
/// Also pins the provider's own driver-independent GroupBy+Join wrong-data decline
/// (<c>MongoSelectDefinition.PropagateFallbackWrongDataFrom</c> and its message), and the join-then-group
/// counterpart, which the driver rejection already closes.
/// </para>
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeJoinInnerDeclineTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    [Fact]
    public void Join_with_paged_outer_still_runs_and_is_correct()
    {
        // Control for an over-broad guard: outer paging is emitted at top level, before the $lookup, and is
        // correct.
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
        // Over-decline net: a projection-only inner still translates on driver 3.11, so no provider guard may
        // decline it. The ordered sibling below is the rejected case.
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
        // Boundary pin: adding OrderBy to the inner makes 3.11 reject the expression rather than mis-fold it
        // into the correlated $lookup (EF-X022 in the spec suite).
        using var db = CreateContext(MongoQueryMode.Native, nameof(Join_with_ordered_inner_is_rejected_by_the_driver));

        Assert.Throws<MongoDB.Driver.Linq.ExpressionNotSupportedException>(() => db.Orders
            .Join(db.Regions.OrderBy(r => r.Country).Select(r => new { r.Country, r.Continent }),
                o => o.Country, r => r.Country, (o, r) => new { o.Country, r.Continent })
            .ToArray());
    }

    [Fact]
    public void Join_with_grouped_outer_and_paged_inner_reports_the_GroupBy_cause()
    {
        // The native path can't represent GroupBy+Join, and its driver-LINQ fallback returns an empty joined
        // entity per grouped row, so the gate must throw (IsGroupByFallbackUnsafe) and name the GroupBy cause.
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
        // Mirrors the spec's Join_GroupBy_Aggregate_in_subquery, projecting i.r rather than the aggregate
        // (re-projecting the aggregate through two join levels hits an unrelated "ProjectionBindingExpression: 1
        // could not be translated" crash in every mode). The grouped join is in a subquery, so
        // MarkGroupByFallbackUnsafe lands on the intermediate MongoQueryExpression; only
        // PropagateFallbackWrongDataFrom makes this decline instead of returning wrong rows.
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

    [Fact]
    public void GroupBy_over_Join_with_uncorrelated_ordered_paged_inner_never_returns_wrong_data()
    {
        // GroupBy over a Join whose inner has an uncorrelated OrderBy/Skip/Take (join-then-group). On driver 3.10
        // this returned silently wrong data via the fallback (CSHARP-6017); the pinned driver (a minimum
        // version, so 3.10 can't resolve) rejects it, so it fails cleanly under Native. If a future driver
        // accepts it again, it must return correct data or this fails.
        using var db = CreateContext(MongoQueryMode.Native,
            nameof(GroupBy_over_Join_with_uncorrelated_ordered_paged_inner_never_returns_wrong_data));

        Assert.Throws<MongoDB.Driver.Linq.ExpressionNotSupportedException>(() => db.Orders
            .Join(db.Regions.OrderBy(r => r.Country).Skip(0).Take(2), o => o.Country, r => r.Country,
                (o, r) => new { o.Country, r.Continent, o.Amount })
            .GroupBy(x => x.Continent)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToArray());

        // Same hazard with a Where between the join and the GroupBy.
        Assert.Throws<MongoDB.Driver.Linq.ExpressionNotSupportedException>(() => db.Orders
            .Join(db.Regions.OrderBy(r => r.Country).Take(2), o => o.Country, r => r.Country,
                (o, r) => new { o.Country, r.Continent, o.Amount })
            .Where(x => x.Amount > 0)
            .GroupBy(x => x.Continent)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToArray());
    }

    [Fact]
    public void GroupBy_over_Join_with_bare_inner_still_returns_correct_data()
    {
        // Over-decline net: a bare inner has no CSHARP-6017 hazard and must keep returning correct results; a
        // guard keyed merely on "GroupBy over a Join" would fail this.
        using var db = CreateContext(MongoQueryMode.Native,
            nameof(GroupBy_over_Join_with_bare_inner_still_returns_correct_data));

        var rows = db.Orders
            .Join(db.Regions, o => o.Country, r => r.Country, (o, r) => new { o.Country, r.Continent, o.Amount })
            .GroupBy(x => x.Continent)
            .Select(g => new { g.Key, Count = g.Count() })
            .AsEnumerable()
            .Select(x => x.Key + ":" + x.Count)
            .OrderBy(s => s)
            .ToArray();

        // Seed: US x2 (NA), UK x2 + FR x1 (EU).
        Assert.Equal(["EU:3", "NA:2"], rows);
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
