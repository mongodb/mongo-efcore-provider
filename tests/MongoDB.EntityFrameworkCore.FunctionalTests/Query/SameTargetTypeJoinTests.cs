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
/// EF-375: two joins onto the SAME target entity type must still flatten. The trigger used to count
/// distinct inner entity types, so a same-typed pair collapsed to one entry, flattening never fired, and
/// the driver's second <c>LeftJoin</c> re-nested the document a level deeper than the shaper expected.
/// </summary>
[XUnitCollection("QueryTests")]
public class SameTargetTypeJoinTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    [Fact]
    public void Chained_join_onto_same_target_type_returns_root_entities()
    {
        var names = Seed();
        List<string> logs = [];
        using var db = new StJoinDbContext(database, names, logs.Add);

        var results = db.Roots
            .Join(db.Mids, r => r.MidId, m => m.Id, (r, m) => r)
            .Join(db.Mids, r => r.MidId, m => m.Id, (r, m) => r)
            .ToList();

        Assert.Single(results);
        Assert.Equal("R1", results[0].Id);
        Assert.Equal("M1", results[0].MidId);

        // The document must be flattened, never doubly nested under _outer.
        Assert.DoesNotContain(logs, l => l.Contains("_outer._outer"));
    }

    [Fact]
    public void Two_same_typed_sibling_joins_project_the_distinct_joined_entities()
    {
        var names = Seed();
        using var db = new StJoinDbContext(database, names, null);

        var results = db.Roots
            .Join(db.Mids, r => r.MidId, m => m.Id, (r, m) => new { r, First = m })
            .Join(db.Mids, x => x.r.OtherMidId, m => m.Id, (x, m) => new { x.First, Second = m })
            .ToList();

        Assert.Single(results);
        Assert.Equal("M1", results[0].First.Id);
        Assert.Equal("M2", results[0].Second.Id);
    }

    // Cross-collection Include is not translated at all on EF8/EF9 (EF-X020), so this shape can only be
    // exercised on EF10. The explicit-Join cases above cover the same defect on every version.
#if !EF8 && !EF9
    [Fact]
    public void Self_referencing_entity_with_two_same_typed_navigations_includes_both()
    {
        var names = Seed();
        using var db = new StJoinDbContext(database, names, null);

        var results = db.Employees
            .Include(e => e.Manager)
            .Include(e => e.Mentor)
            .Where(e => e.Id == "E1")
            .ToList();

        var employee = Assert.Single(results);
        Assert.NotNull(employee.Manager);
        Assert.Equal("E2", employee.Manager!.Id);
        Assert.NotNull(employee.Mentor);
        Assert.Equal("E3", employee.Mentor!.Id);
    }
#endif

    [Fact]
    public void Control_chained_join_onto_different_target_types_still_flattens()
    {
        var names = Seed();
        List<string> logs = [];
        using var db = new StJoinDbContext(database, names, logs.Add);

        var results = db.Roots
            .Join(db.Mids, r => r.MidId, m => m.Id, (r, m) => r)
            .Join(db.Others, r => r.OtherId, o => o.Id, (r, o) => r)
            .ToList();

        Assert.Single(results);
        Assert.Equal("R1", results[0].Id);
        Assert.Contains(logs, l => l.Contains("_lookup_Mid") && l.Contains("_lookup_Other"));
    }

    [Fact]
    public void Two_same_typed_collection_joins_get_a_lookup_each_and_cross_product()
    {
        var names = Seed();
        // A second root on the same Mid, so each join contributes 2 rows and the cross product is 2 x 2.
        // Both joins resolve the SAME navigation (Mid.Roots), so they can only be kept apart by giving
        // each its own $lookup alias — sharing one would collapse the cross product to 2 rows.
        database.MongoDatabase.GetCollection<BsonDocument>(names.Roots).InsertOne(
            new BsonDocument { { "_id", "R2" }, { "MidId", "M1" }, { "OtherMidId", "M2" }, { "OtherId", "O1" } });

        List<string> logs = [];
        using var db = new StJoinDbContext(database, names, logs.Add);

        var query =
            from m in db.Mids
            join r1 in db.Roots on m.Id equals r1.MidId
            join r2 in db.Roots on m.Id equals r2.MidId
            select new { First = r1, Second = r2 };

        // Filtering client-side: Mid "M2" has no roots, and a flattened join's $unwind currently always
        // preserves empty arrays regardless of the join being an inner one (EF-X024), so it contributes a
        // null-paired row. That gap is not what this test is pinning.
        var results = query.ToList().Where(r => r.First != null && r.Second != null).ToList();

        Assert.Equal(4, results.Count);
        Assert.Contains(results, r => r.First.Id == "R1" && r.Second.Id == "R1");
        Assert.Contains(results, r => r.First.Id == "R1" && r.Second.Id == "R2");
        Assert.Contains(results, r => r.First.Id == "R2" && r.Second.Id == "R1");
        Assert.Contains(results, r => r.First.Id == "R2" && r.Second.Id == "R2");
        Assert.Contains(logs, l => l.Contains("_lookup_Roots") && l.Contains("_lookup_Roots_1"));
    }

    [Fact]
    public void Filter_after_a_flattened_multi_join_chain_is_applied_not_dropped()
    {
        var names = Seed();
        List<string> logs = [];
        using var db = new StJoinDbContext(database, names, logs.Add);

        // Each join in the chain resolves a DIFFERENT navigation (Mid vs OtherMid), so the reattachment
        // logic that carries a composed Where onto the flattened _lookup_* fields (EF-369) can tell them
        // apart unambiguously - by the FK property name, not just by target entity type - and the
        // predicate is genuinely applied rather than either dropped (the pre-EF-369 bug) or rejected.
        var sameTyped =
            from r in db.Roots
            join m1 in db.Mids on r.MidId equals m1.Id
            join m2 in db.Mids on r.OtherMidId equals m2.Id
            where m2.Name == "Mid two"
            select r;

        var differentTyped =
            from r in db.Roots
            join m in db.Mids on r.MidId equals m.Id
            join o in db.Others on r.OtherId equals o.Id
            where o.Name == "Other one"
            select r;

        foreach (var query in new[] { sameTyped, differentTyped })
        {
            logs.Clear();
            var results = query.ToList();

            var result = Assert.Single(results);
            Assert.Equal("R1", result.Id);
            Assert.Contains(logs, l => l.Contains("$match"));
        }
    }

    private CollectionNames Seed()
    {
        var names = new CollectionNames(
            TemporaryDatabaseFixtureBase.CreateCollectionName("StRoots") + Guid.NewGuid().ToString("N")[..8],
            TemporaryDatabaseFixtureBase.CreateCollectionName("StMids") + Guid.NewGuid().ToString("N")[..8],
            TemporaryDatabaseFixtureBase.CreateCollectionName("StOthers") + Guid.NewGuid().ToString("N")[..8],
            TemporaryDatabaseFixtureBase.CreateCollectionName("StEmployees") + Guid.NewGuid().ToString("N")[..8]);

        database.MongoDatabase.GetCollection<BsonDocument>(names.Roots).InsertMany([
            new BsonDocument { { "_id", "R1" }, { "MidId", "M1" }, { "OtherMidId", "M2" }, { "OtherId", "O1" } }
        ]);
        database.MongoDatabase.GetCollection<BsonDocument>(names.Mids).InsertMany([
            new BsonDocument { { "_id", "M1" }, { "Name", "Mid one" } },
            new BsonDocument { { "_id", "M2" }, { "Name", "Mid two" } }
        ]);
        database.MongoDatabase.GetCollection<BsonDocument>(names.Others).InsertMany([
            new BsonDocument { { "_id", "O1" }, { "Name", "Other one" } }
        ]);
        database.MongoDatabase.GetCollection<BsonDocument>(names.Employees).InsertMany([
            new BsonDocument { { "_id", "E1" }, { "Name", "Employee one" }, { "ManagerId", "E2" }, { "MentorId", "E3" } },
            new BsonDocument { { "_id", "E2" }, { "Name", "Employee two" }, { "ManagerId", BsonNull.Value }, { "MentorId", BsonNull.Value } },
            new BsonDocument { { "_id", "E3" }, { "Name", "Employee three" }, { "ManagerId", BsonNull.Value }, { "MentorId", BsonNull.Value } }
        ]);

        return names;
    }

    private sealed record CollectionNames(string Roots, string Mids, string Others, string Employees);

    class StRoot
    {
        public string Id { get; set; }
        public string MidId { get; set; }
        public string OtherMidId { get; set; }
        public string OtherId { get; set; }
        public StMid Mid { get; set; }
        public StMid OtherMid { get; set; }
        public StOther Other { get; set; }
    }

    class StMid
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public List<StRoot> Roots { get; set; }
    }

    class StOther
    {
        public string Id { get; set; }
        public string Name { get; set; }
    }

    class StEmployee
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string ManagerId { get; set; }
        public string MentorId { get; set; }
        public StEmployee Manager { get; set; }
        public StEmployee Mentor { get; set; }
    }

    class StJoinDbContext : DbContext
    {
        private readonly CollectionNames _names;

        public StJoinDbContext(TemporaryDatabaseFixture database, CollectionNames names, Action<string>? logTo)
            : base(Build(database, logTo))
        {
            _names = names;
        }

        private static DbContextOptions Build(TemporaryDatabaseFixture database, Action<string>? logTo)
        {
            var builder = new DbContextOptionsBuilder<StJoinDbContext>()
                .UseMongoDB(database.Client, database.MongoDatabase.DatabaseNamespace.DatabaseName)
                .ReplaceService<IModelCacheKeyFactory, IgnoreCacheKeyFactory>()
                .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));

            if (logTo != null)
            {
                builder.LogTo(logTo).EnableSensitiveDataLogging();
            }

            return builder.Options;
        }

        public DbSet<StRoot> Roots { get; set; }
        public DbSet<StMid> Mids { get; set; }
        public DbSet<StOther> Others { get; set; }
        public DbSet<StEmployee> Employees { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<StRoot>(b =>
            {
                b.ToCollection(_names.Roots);
                b.HasKey(r => r.Id);
                b.Property(r => r.Id).HasElementName("_id");
                b.HasOne(r => r.Mid).WithMany(m => m.Roots).HasForeignKey(r => r.MidId);
                b.HasOne(r => r.OtherMid).WithMany().HasForeignKey(r => r.OtherMidId);
                b.HasOne(r => r.Other).WithMany().HasForeignKey(r => r.OtherId);
            });

            modelBuilder.Entity<StMid>(b =>
            {
                b.ToCollection(_names.Mids);
                b.HasKey(m => m.Id);
                b.Property(m => m.Id).HasElementName("_id");
            });

            modelBuilder.Entity<StOther>(b =>
            {
                b.ToCollection(_names.Others);
                b.HasKey(o => o.Id);
                b.Property(o => o.Id).HasElementName("_id");
            });

            modelBuilder.Entity<StEmployee>(b =>
            {
                b.ToCollection(_names.Employees);
                b.HasKey(e => e.Id);
                b.Property(e => e.Id).HasElementName("_id");
                b.Property(e => e.ManagerId).IsRequired(false);
                b.Property(e => e.MentorId).IsRequired(false);
                b.HasOne(e => e.Manager).WithMany().HasForeignKey(e => e.ManagerId);
                b.HasOne(e => e.Mentor).WithMany().HasForeignKey(e => e.MentorId);
            });
        }

        private sealed class IgnoreCacheKeyFactory : IModelCacheKeyFactory
        {
            private static int _count;
            public object Create(DbContext context, bool designTime) => Interlocked.Increment(ref _count);
        }
    }
}
