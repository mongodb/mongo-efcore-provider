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
using Microsoft.EntityFrameworkCore.Infrastructure;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Extensions;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// EF-377: a chained Join with a bare key-equality hop (no model navigation) previously left a
/// dependent hop's <c>$lookup</c> localField unscoped, or dropped the hop's own <c>$lookup</c>
/// entirely, once a later join forced flat mode — silently dropping every row. Covers the bare hop
/// in first, second, and both positions, plus the shared <c>GroupJoin</c> translator path.
/// </summary>
[XUnitCollection("QueryTests")]
public class NavigationlessJoinChainTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    [Fact]
    public void Bare_first_hop_then_navigation_backed_second_hop_returns_correct_rows_and_scopes_lookup()
    {
        var (rootsName, midsName, leavesName) = Seed();
        var logs = new List<string>();

        using var db = new NavigationlessJoinDbContext(database, rootsName, midsName, leavesName, logs.Add);

        var results =
            db.NRoots
                .Join(db.NMids, r => r.MidKey, m => m.Id, (r, m) => new { r, m })
                .Join(db.NLeaves, x => x.m.LeafId, l => l.Id, (x, l) => new { x.r, l })
                .ToList();

        var result = Assert.Single(results);
        Assert.Equal("R1", result.r.Name);
        Assert.Equal("A", result.l.Label);

        // NMid has no navigation from NRoot, so its $lookup uses the "_lookup_NMid" fallback alias;
        // the second hop's $lookup must be scoped under that alias, not the root document.
        Assert.Contains(logs, l => l.Contains("\"as\" : \"_lookup_NMid\""));
        Assert.Contains(logs, l => l.Contains("\"localField\" : \"_lookup_NMid.LeafId\""));
    }

    [Fact]
    public void Navigation_backed_first_hop_then_bare_second_hop_returns_correct_rows_and_scopes_lookup()
    {
        var (rootsName, midsName, leavesName) = Seed();
        var logs = new List<string>();

        using var db = new NavigationlessJoinDbContext(
            database, rootsName, midsName, leavesName, logs.Add, firstHopHasNavigation: true, secondHopHasNavigation: false);

        var results =
            db.NRoots
                .Join(db.NMids, r => r.MidKey, m => m.Id, (r, m) => new { r, m })
                .Join(db.NLeaves, x => x.m.LeafId, l => l.Id, (x, l) => new { x.r, l })
                .ToList();

        var result = Assert.Single(results);
        Assert.Equal("R1", result.r.Name);
        Assert.Equal("A", result.l.Label);

        // This time NMid.Leaf is the bare hop; its $lookup must still scope under the first
        // hop's real navigation alias ("_lookup_Mid").
        Assert.Contains(logs, l => l.Contains("\"as\" : \"_lookup_Mid\""));
        Assert.Contains(logs, l => l.Contains("\"localField\" : \"_lookup_Mid.LeafId\""));
    }

    [Fact]
    public void Both_hops_bare_key_equality_joins_return_correct_rows_and_scope_lookup()
    {
        var (rootsName, midsName, leavesName) = Seed();
        var logs = new List<string>();

        using var db = new NavigationlessJoinDbContext(
            database, rootsName, midsName, leavesName, logs.Add, secondHopHasNavigation: false);

        var results =
            db.NRoots
                .Join(db.NMids, r => r.MidKey, m => m.Id, (r, m) => new { r, m })
                .Join(db.NLeaves, x => x.m.LeafId, l => l.Id, (x, l) => new { x.r, l })
                .ToList();

        var result = Assert.Single(results);
        Assert.Equal("R1", result.r.Name);
        Assert.Equal("A", result.l.Label);

        Assert.Contains(logs, l => l.Contains("\"as\" : \"_lookup_NMid\""));
        Assert.Contains(logs, l => l.Contains("\"localField\" : \"_lookup_NMid.LeafId\""));
    }

#if !EF8 && !EF9
    // EF8's and EF9's left-join pattern detection doesn't recognize this idiom when the outer
    // sequence comes from a prior bare Join, so it fails to translate on EF8/EF9 (fixed in EF10).
    [Fact]
    public void Bare_first_hop_then_GroupJoin_left_join_pattern_returns_correct_rows()
    {
        var (rootsName, midsName, leavesName) = Seed();

        using var db = new NavigationlessJoinDbContext(database, rootsName, midsName, leavesName);

        var results =
            db.NRoots
                .Join(db.NMids, r => r.MidKey, m => m.Id, (r, m) => new { r, m })
                .GroupJoin(db.NLeaves, x => x.m.LeafId, l => l.Id, (x, leaves) => new { x.r, leaves })
                .SelectMany(x => x.leaves.DefaultIfEmpty(), (x, l) => new { x.r, l })
                .ToList();

        var result = Assert.Single(results);
        Assert.Equal("R1", result.r.Name);
        Assert.NotNull(result.l);
        Assert.Equal("A", result.l!.Label);
    }
#endif

    private (string rootsName, string midsName, string leavesName) Seed()
    {
        var rootsName = TemporaryDatabaseFixtureBase.CreateCollectionName("NRoots") + Guid.NewGuid().ToString("N")[..8];
        var midsName = TemporaryDatabaseFixtureBase.CreateCollectionName("NMids") + Guid.NewGuid().ToString("N")[..8];
        var leavesName = TemporaryDatabaseFixtureBase.CreateCollectionName("NLeaves") + Guid.NewGuid().ToString("N")[..8];

        database.MongoDatabase.GetCollection<BsonDocument>(rootsName).InsertMany([
            new BsonDocument { { "_id", 1 }, { "Name", "R1" }, { "MidKey", 100 } }
        ]);
        database.MongoDatabase.GetCollection<BsonDocument>(midsName).InsertMany([
            new BsonDocument { { "_id", 100 }, { "LeafId", 1000 } }
        ]);
        database.MongoDatabase.GetCollection<BsonDocument>(leavesName).InsertMany([
            new BsonDocument { { "_id", 1000 }, { "Label", "A" } }
        ]);

        return (rootsName, midsName, leavesName);
    }

    class NRoot
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public int MidKey { get; set; }
        public NMid Mid { get; set; }
    }

    class NMid
    {
        public int Id { get; set; }
        public int LeafId { get; set; }
        public NLeaf Leaf { get; set; }
    }

    class NLeaf
    {
        public int Id { get; set; }
        public string Label { get; set; }
    }

    class NavigationlessJoinDbContext(
        TemporaryDatabaseFixture database, string rootsCollection, string midsCollection, string leavesCollection,
        Action<string>? logAction = null, bool firstHopHasNavigation = false, bool secondHopHasNavigation = true)
        : DbContext(new DbContextOptionsBuilder<NavigationlessJoinDbContext>()
            .UseMongoDB(database.Client, database.MongoDatabase.DatabaseNamespace.DatabaseName)
            .ReplaceService<IModelCacheKeyFactory, IgnoreCacheKeyFactory>()
            .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
            .LogTo(l => logAction?.Invoke(l))
            .EnableSensitiveDataLogging()
            .Options)
    {
        public DbSet<NRoot> NRoots { get; set; }
        public DbSet<NMid> NMids { get; set; }
        public DbSet<NLeaf> NLeaves { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<NRoot>(b =>
            {
                b.ToCollection(rootsCollection);
                b.HasKey(r => r.Id);
                if (firstHopHasNavigation)
                {
                    b.HasOne(r => r.Mid).WithMany().HasForeignKey(r => r.MidKey);
                }
                else
                {
                    // Otherwise EF's convention-based discovery would still turn the CLR navigation
                    // property into an (unwanted) relationship, adding a shadow FK not present in
                    // the seed data.
                    b.Ignore(r => r.Mid);
                }
            });

            modelBuilder.Entity<NMid>(b =>
            {
                b.ToCollection(midsCollection);
                b.HasKey(m => m.Id);
                if (secondHopHasNavigation)
                {
                    b.HasOne(m => m.Leaf).WithMany().HasForeignKey(m => m.LeafId);
                }
                else
                {
                    b.Ignore(m => m.Leaf);
                }
            });

            modelBuilder.Entity<NLeaf>(b =>
            {
                b.ToCollection(leavesCollection);
                b.HasKey(l => l.Id);
            });
        }

        private sealed class IgnoreCacheKeyFactory : IModelCacheKeyFactory
        {
            private static int _count;
            public object Create(DbContext context, bool designTime) => Interlocked.Increment(ref _count);
        }
    }
}
