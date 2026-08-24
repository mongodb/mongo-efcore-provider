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
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Diagnostics;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Infrastructure;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// EF-373: <c>Skip</c>/<c>Take</c> written BETWEEN two joins was hoisted above BOTH <c>$lookup</c>s by
/// <c>StripJoinForLookup</c>, so the paging ran before the second join had filtered any rows — a silently
/// wrong page. The residual of EF-370: the composed operator was no longer dropped, it was mispositioned.
/// <para>
/// These tests discriminate on ROW IDENTITY and on MQL STAGE ORDER, not on row count alone and not on
/// navigation-equality: EF's change-tracker identity fix-up can repair an object graph even when the
/// pipeline read the wrong rows, and a 1:1 second join returns the right page even with the defect live
/// (measured), so the second join here deliberately ELIMINATES the first row.
/// </para>
/// </summary>
[XUnitCollection("QueryTests")]
public class Ef373InterleavedPagingTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    // ---- T1: the defect. Paging BETWEEN the two joins must page the SECOND join's row set. ----
    //
    // Seed ordered by Name is N1, N2, N3. The Mid join matches all three; the Other join matches only
    // N2 and N3. Skip(1).Take(2) is written between the two joins, so it applies to {N1, N2, N3} and
    // yields {N2, N3}, both of which survive the Other join. With the defect live BOTH lookups sat above
    // the paging, so the Other join dropped N1 first and Skip(1) then ate N2, returning {N3}.
    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.DriverLinq)]
    public void Paging_between_two_joins_pages_the_rows_the_first_join_produced(MongoQueryMode mode)
    {
        using var db = Setup(mode);

        var result = db.Roots
            .Join(db.Mids, r => r.MidId, m => m._id, (r, m) => r)
            .OrderBy(r => r.Name)
            .Skip(1)
            .Take(2)
            .Join(db.Others, r => r.OtherId, o => o._id, (r, o) => r.Name)
            .ToList();

        Assert.Equal(["N2", "N3"], result);
    }

    // ---- T2: the MQL stage-order pin. Position is the actual defect, so pin it directly. ----
    [Fact]
    public void Paging_between_two_joins_emits_the_second_lookup_above_the_paging()
    {
        using var db = Setup(MongoQueryMode.Native, out var spyLogger);

        var result = db.Roots
            .Join(db.Mids, r => r.MidId, m => m._id, (r, m) => r)
            .OrderBy(r => r.Name)
            .Skip(1)
            .Take(2)
            .Join(db.Others, r => r.OtherId, o => o._id, (r, o) => r.Name)
            .ToList();

        Assert.Equal(["N2", "N3"], result);

        var mql = spyLogger.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery);
        var midLookup = mql.IndexOf("\"as\" : \"_lookup_Mid\"", StringComparison.Ordinal);
        var otherLookup = mql.IndexOf("\"as\" : \"_lookup_Other\"", StringComparison.Ordinal);
        var skip = mql.IndexOf("$skip", StringComparison.Ordinal);
        var limit = mql.IndexOf("$limit", StringComparison.Ordinal);

        Assert.True(midLookup >= 0, mql);
        Assert.True(otherLookup >= 0, mql);
        Assert.True(skip >= 0, mql);
        Assert.True(limit >= 0, mql);

        // The first join's $lookup is BELOW the paging; the second join's is ABOVE it.
        Assert.True(midLookup < skip, mql);
        Assert.True(skip < limit, mql);
        Assert.True(limit < otherLookup, mql);
    }

    // ---- T3: the control. Paging written BELOW all the joins must keep the pre-EF-373 layout: a single
    // contiguous lookup group above the base source, in pending order (Other then Mid), with the paging
    // emitted below both. This is what stops the fix from being over-broad. ----
    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.DriverLinq)]
    public void Paging_below_all_joins_still_emits_both_lookups_above_the_paging(MongoQueryMode mode)
    {
        using var db = Setup(mode, out var spyLogger);

        var result = db.Roots
            .OrderBy(r => r.Name)
            .Skip(1)
            .Take(2)
            .Join(db.Mids, r => r.MidId, m => m._id, (r, m) => r)
            .Join(db.Others, r => r.OtherId, o => o._id, (r, o) => r.Name)
            .ToList();

        // Paging first: {N2, N3}. Both survive the Other join.
        Assert.Equal(["N2", "N3"], result);

        var mql = spyLogger.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery);
        var otherLookup = mql.IndexOf("\"as\" : \"_lookup_Other\"", StringComparison.Ordinal);
        var midLookup = mql.IndexOf("\"as\" : \"_lookup_Mid\"", StringComparison.Ordinal);
        var limit = mql.IndexOf("$limit", StringComparison.Ordinal);

        Assert.True(limit >= 0 && otherLookup >= 0 && midLookup >= 0, mql);

        // The load-bearing assertion: both lookups are emitted ABOVE the paging, as one contiguous group.
        Assert.True(limit < otherLookup, mql);

        // ARBITRARY FLUSH ORDER, asserted only to detect unintended change. Within that contiguous group the
        // two lookups come out in _pendingLookups order, which for this chain happens to be outermost-join
        // first (Other before Mid) — an incidental property of the dependency sort, not a requirement: these
        // two lookups are independent (neither localField reads the other's output), so either order is
        // equally correct. A failure here means the flush order moved, which is only a REGRESSION if
        // something else in this file also went red.
        Assert.True(otherLookup < midLookup, mql);
    }

    // ---- T4: the same defect with a lone interleaved Skip (no Take), so the fix is not tied to the
    // Skip+Take pair being present together. ----
    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.DriverLinq)]
    public void Interleaved_Skip_without_Take_pages_the_rows_the_first_join_produced(MongoQueryMode mode)
    {
        using var db = Setup(mode);

        var result = db.Roots
            .Join(db.Mids, r => r.MidId, m => m._id, (r, m) => r)
            .OrderBy(r => r.Name)
            .Skip(1)
            .Join(db.Others, r => r.OtherId, o => o._id, (r, o) => r.Name)
            .ToList();

        Assert.Equal(["N2", "N3"], result);
    }

    // ---- T5: the ordering the change actually alters. A SORT interleaved between two joins whose SECOND
    // join is 1:N, so the $unwind expands rather than just filtering. The upstream spec test this fix
    // re-baselined (Where_join_orderby_join_select) calls AssertQuery with assertOrder defaulting to FALSE, so
    // nothing committed pinned ORDER for a sort interleave; T1-T4 are all 1:1, where the second $unwind
    // performs no expansion. Roots are inserted in DESCENDING name order, so a pipeline that does not sort at
    // all cannot pass, and each root fans out to exactly 3 leaves, so a pipeline that loses the second join
    // cannot pass either. ----
    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.DriverLinq)]
    public void Sort_between_two_joins_orders_the_rows_a_one_to_many_second_join_expands(MongoQueryMode mode)
    {
        using var db = SetupFanOut(mode, out var spyLogger);

        var result = db.Roots
            .Join(db.Mids, r => r.MidId, m => m._id, (r, m) => r)
            .OrderBy(r => r.Name)
            .Join(db.Leaves, r => r._id, l => l.RootId, (r, l) => r.Name)
            .ToList();

        // ORDERED, not just set-equal: six roots, three leaves each, ascending by name.
        string[] expected =
        [
            "N1", "N1", "N1", "N2", "N2", "N2", "N3", "N3", "N3",
            "N4", "N4", "N4", "N5", "N5", "N5", "N6", "N6", "N6"
        ];
        Assert.Equal(expected, result);

        // The stage-order pin. The data assertion above CANNOT distinguish the pre-EF-373 layout on its own
        // (measured): $sort and a fan-out $unwind commute with respect to key order, because $unwind preserves
        // its input order and expands each document into an adjacent run, so sorting before or after the
        // expansion yields the same ordered key sequence. Position is therefore pinned directly.
        var mql = spyLogger.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery);
        var midLookup = mql.IndexOf("\"as\" : \"_lookup_Mid\"", StringComparison.Ordinal);
        var leafLookup = mql.IndexOf("\"as\" : \"_lookup_Leaves\"", StringComparison.Ordinal);
        var sort = mql.IndexOf("$sort", StringComparison.Ordinal);

        Assert.True(midLookup >= 0, mql);
        Assert.True(leafLookup >= 0, mql);
        Assert.True(sort >= 0, mql);
        Assert.True(midLookup < sort, mql);
        Assert.True(sort < leafLookup, mql);
    }

    private RootDbContext Setup(MongoQueryMode mode, out SpyLoggerProvider spyLogger)
    {
        var (loggerFactory, provider) = SpyLoggerProvider.Create();
        spyLogger = provider;
        return Setup(mode, loggerFactory);
    }

    private RootDbContext SetupFanOut(MongoQueryMode mode, out SpyLoggerProvider spyLogger)
    {
        var (loggerFactory, provider) = SpyLoggerProvider.Create();
        spyLogger = provider;

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var rootsName = TemporaryDatabaseFixtureBase.CreateCollectionName("Ef373FanRoots") + suffix;
        var midsName = TemporaryDatabaseFixtureBase.CreateCollectionName("Ef373FanMids") + suffix;
        var othersName = TemporaryDatabaseFixtureBase.CreateCollectionName("Ef373FanOthers") + suffix;
        var leavesName = TemporaryDatabaseFixtureBase.CreateCollectionName("Ef373FanLeaves") + suffix;

        var m1 = ObjectId.GenerateNewId();
        database.MongoDatabase.GetCollection<BsonDocument>(midsName).InsertMany([
            new BsonDocument { { "_id", m1 }, { "tag", "M1" } }
        ]);

        // Inserted N6..N1 - the REVERSE of the asserted order, so an unsorted pipeline fails.
        var roots = new List<BsonDocument>();
        var leaves = new List<BsonDocument>();
        for (var i = 6; i >= 1; i--)
        {
            var rootId = ObjectId.GenerateNewId();
            roots.Add(new BsonDocument { { "_id", rootId }, { "name", "N" + i }, { "mid_id", m1 } });
            for (var j = 0; j < 3; j++)
            {
                leaves.Add(new BsonDocument
                {
                    { "_id", ObjectId.GenerateNewId() }, { "root_id", rootId }, { "label", $"N{i}-L{j}" }
                });
            }
        }

        database.MongoDatabase.GetCollection<BsonDocument>(rootsName).InsertMany(roots);
        database.MongoDatabase.GetCollection<BsonDocument>(leavesName).InsertMany(leaves);

        return new RootDbContext(database, rootsName, midsName, othersName, leavesName, mode, loggerFactory);
    }

    private RootDbContext Setup(MongoQueryMode mode, ILoggerFactory? loggerFactory = null)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var rootsName = TemporaryDatabaseFixtureBase.CreateCollectionName("Ef373Roots") + suffix;
        var midsName = TemporaryDatabaseFixtureBase.CreateCollectionName("Ef373Mids") + suffix;
        var othersName = TemporaryDatabaseFixtureBase.CreateCollectionName("Ef373Others") + suffix;

        var m1 = ObjectId.GenerateNewId();
        var o1 = ObjectId.GenerateNewId();

        database.MongoDatabase.GetCollection<BsonDocument>(midsName).InsertMany([
            new BsonDocument { { "_id", m1 }, { "tag", "M1" } }
        ]);
        database.MongoDatabase.GetCollection<BsonDocument>(othersName).InsertMany([
            new BsonDocument { { "_id", o1 }, { "label", "O1" } }
        ]);
        database.MongoDatabase.GetCollection<BsonDocument>(rootsName).InsertMany([
            // N1 has no Other, so the SECOND (inner) join drops it.
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "name", "N1" }, { "mid_id", m1 } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "name", "N2" }, { "mid_id", m1 }, { "other_id", o1 } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "name", "N3" }, { "mid_id", m1 }, { "other_id", o1 } }
        ]);

        return new RootDbContext(
            database, rootsName, midsName, othersName,
            TemporaryDatabaseFixtureBase.CreateCollectionName("Ef373Leaves") + suffix, mode, loggerFactory);
    }

    public class Mid
    {
        public ObjectId _id { get; set; }
        public string Tag { get; set; }
        public List<Root> Roots { get; set; }
    }

    public class Other
    {
        public ObjectId _id { get; set; }
        public string Label { get; set; }
        public List<Root> Roots { get; set; }
    }

    // A collection navigation off Root, so the SECOND join in T5 fans out 1:N.
    public class Leaf
    {
        public ObjectId _id { get; set; }
        public ObjectId RootId { get; set; }
        public string Label { get; set; }
        public Root Root { get; set; }
    }

    public class Root
    {
        public ObjectId _id { get; set; }
        public string Name { get; set; }
        public ObjectId? MidId { get; set; }
        public Mid Mid { get; set; }
        public ObjectId? OtherId { get; set; }
        public Other Other { get; set; }
        public List<Leaf> Leaves { get; set; }
    }

    public class RootDbContext : DbContext
    {
        private readonly string _roots;
        private readonly string _mids;
        private readonly string _others;
        private readonly string _leaves;

        public DbSet<Root> Roots { get; set; }
        public DbSet<Mid> Mids { get; set; }
        public DbSet<Other> Others { get; set; }
        public DbSet<Leaf> Leaves { get; set; }

        public RootDbContext(
            TemporaryDatabaseFixture db, string roots, string mids, string others, string leaves,
            MongoQueryMode mode, ILoggerFactory? loggerFactory)
            : base(BuildOptions(db, mode, loggerFactory))
        {
            _roots = roots;
            _mids = mids;
            _others = others;
            _leaves = leaves;
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<Mid>(b =>
            {
                b.ToCollection(_mids);
                b.Property(m => m.Tag).HasElementName("tag");
            });

            modelBuilder.Entity<Other>(b =>
            {
                b.ToCollection(_others);
                b.Property(o => o.Label).HasElementName("label");
            });

            modelBuilder.Entity<Leaf>(b =>
            {
                b.ToCollection(_leaves);
                b.Property(l => l.RootId).HasElementName("root_id");
                b.Property(l => l.Label).HasElementName("label");
            });

            modelBuilder.Entity<Root>(b =>
            {
                b.ToCollection(_roots);
                b.Property(r => r.Name).HasElementName("name");
                b.Property(r => r.MidId).HasElementName("mid_id");
                b.Property(r => r.OtherId).HasElementName("other_id");
                b.HasOne(r => r.Mid).WithMany(m => m.Roots).HasForeignKey(r => r.MidId);
                b.HasOne(r => r.Other).WithMany(o => o.Roots).HasForeignKey(r => r.OtherId);
                b.HasMany(r => r.Leaves).WithOne(l => l.Root).HasForeignKey(l => l.RootId);
            });
        }

        private static DbContextOptions<RootDbContext> BuildOptions(
            TemporaryDatabaseFixture db, MongoQueryMode mode, ILoggerFactory? loggerFactory)
        {
            var builder = new DbContextOptionsBuilder<RootDbContext>()
                .UseMongoDB(db.Client, db.MongoDatabase.DatabaseNamespace.DatabaseName, o => o.UseQueryMode(mode))
                .ReplaceService<IModelCacheKeyFactory, IgnoreCacheKeyFactory>()
                .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
            if (loggerFactory != null)
            {
                builder = builder.UseLoggerFactory(loggerFactory).EnableSensitiveDataLogging();
            }

            return builder.Options;
        }

        sealed class IgnoreCacheKeyFactory : IModelCacheKeyFactory
        {
            private static int _count;
            public object Create(DbContext context, bool designTime) => Interlocked.Increment(ref _count);
        }
    }
}
