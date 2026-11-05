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
/// A correlated subquery over another collection whose correlation is NOT a collection navigation's foreign key (a
/// <c>GroupJoin</c> or explicit subquery on arbitrary properties, or a model with no collection navigation). Hand-computed
/// over raw seeds. The null-key rules are the point: EF's <c>GroupJoin</c> rewrite guards with <c>outerKey != null</c>, so a null
/// key matches nothing; an explicit subquery has no guard, so null equals null. <c>$lookup</c> matches null/missing to
/// null/missing, which is only the second rule. The DriverLinq leg may answer correctly or refuse, never answer wrongly.
/// </summary>
public class NativeNavigationlessCorrelationTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    public class Cust
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string? Region { get; set; }
        public List<Ord> Orders { get; set; } = [];
    }

    public class Ord
    {
        public int Id { get; set; }
        public int Total { get; set; }
        public string? CustId { get; set; }
        public string? Tag { get; set; }
        public string? Region { get; set; }
        public Cust? Cust { get; set; }
    }

    // A TPH hierarchy for the derived-target case: an owner whose navigation points at the DERIVED type Dog.
    public class Owner
    {
        public string Id { get; set; } = "";
        public List<Dog> Dogs { get; set; } = [];
    }

    public class Animal
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string? OwnerId { get; set; }
    }

    public class Dog : Animal;

    public class Cat : Animal;

    // Customers: A1 (Alpha1, Region R1), A2 (Alpha2, Region explicit null), B1 (Beta1, Region field missing), C0 (Nobody, Region R9).
    // Orders: 1 A1 10 Tag Alpha1 R1 | 2 A1 20 null Region | 3 A1 600 Tag Alpha1 missing Region | 4 A2 700 | 5 B1 30 | 6 ZZ 40 | 7 null 50.
    // Orders 2..7 have a null or missing Region; only order 1 has one (R1).
    private string[] Seed()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var names = new[]
        {
            TemporaryDatabaseFixtureBase.CreateCollectionName("nncCusts") + suffix,
            TemporaryDatabaseFixtureBase.CreateCollectionName("nncOrds") + suffix
        };

        database.MongoDatabase.GetCollection<BsonDocument>(names[0]).InsertMany(
        [
            new BsonDocument { { "_id", "A1" }, { "Name", "Alpha1" }, { "Region", "R1" } },
            new BsonDocument { { "_id", "A2" }, { "Name", "Alpha2" }, { "Region", BsonNull.Value } },
            new BsonDocument { { "_id", "B1" }, { "Name", "Beta1" } },
            new BsonDocument { { "_id", "C0" }, { "Name", "Nobody" }, { "Region", "R9" } }
        ]);
        database.MongoDatabase.GetCollection<BsonDocument>(names[1]).InsertMany(
        [
            new BsonDocument { { "_id", 1 }, { "Total", 10 }, { "CustId", "A1" }, { "Tag", "Alpha1" }, { "Region", "R1" } },
            new BsonDocument { { "_id", 2 }, { "Total", 20 }, { "CustId", "A1" }, { "Region", BsonNull.Value } },
            new BsonDocument { { "_id", 3 }, { "Total", 600 }, { "CustId", "A1" }, { "Tag", "Alpha1" } },
            new BsonDocument { { "_id", 4 }, { "Total", 700 }, { "CustId", "A2" } },
            new BsonDocument { { "_id", 5 }, { "Total", 30 }, { "CustId", "B1" } },
            new BsonDocument { { "_id", 6 }, { "Total", 40 }, { "CustId", "ZZ" } },
            new BsonDocument { { "_id", 7 }, { "Total", 50 }, { "CustId", BsonNull.Value } }
        ]);
        return names;
    }

    private string[] SeedAnimals()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var names = new[]
        {
            TemporaryDatabaseFixtureBase.CreateCollectionName("nncOwners") + suffix,
            TemporaryDatabaseFixtureBase.CreateCollectionName("nncAnimals") + suffix
        };

        using var seeding = CreateAnimals(names, MongoQueryMode.DriverLinq);
        seeding.AddRange(
            new Owner { Id = "A1" }, new Owner { Id = "B1" },
            new Dog { Id = 1, Name = "Rex", OwnerId = "A1" },
            new Dog { Id = 2, Name = "Zed", OwnerId = "A1" },
            new Cat { Id = 3, Name = "Tom", OwnerId = "A1" },
            new Dog { Id = 4, Name = "Fido", OwnerId = "B1" });
        seeding.SaveChanges();
        return names;
    }

    private AnimalCtx CreateAnimals(string[] names, MongoQueryMode mode)
    {
        var options = new DbContextOptionsBuilder<AnimalCtx>()
            .UseMongoDB(database.Client, database.MongoDatabase.DatabaseNamespace.DatabaseName, o => o.UseQueryMode(mode))
            .ReplaceService<IModelCacheKeyFactory, UniqueModelCacheKeyFactory>()
            .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
            .Options;
        return new AnimalCtx(options, names);
    }

    private void Assert3(bool withNavigation, Func<Ctx, List<string>> query, List<string> expected)
    {
        var names = Seed();
        foreach (var mode in new[] { MongoQueryMode.NativeOnly, MongoQueryMode.Native, MongoQueryMode.DriverLinq })
        {
            using var db = Create(names, mode, withNavigation);
            var rows = query(db);
            Assert.True(expected.SequenceEqual(rows), $"{mode} returned [{string.Join(", ", rows)}], expected [{string.Join(", ", expected)}]");
        }
    }

    private static List<string> Rows<T>(IEnumerable<T> rows, Func<T, string> format) => rows.Select(format).OrderBy(s => s).ToList();

    [Fact]
    public void GroupJoin_Count_on_the_foreign_key_without_a_navigation()
        => Assert3(false,
            db => Rows(db.Set<Cust>().GroupJoin(db.Set<Ord>(), c => c.Id, o => o.CustId, (c, g) => new { c.Id, N = g.Count() }).AsEnumerable(), z => z.Id + ":" + z.N),
            ["A1:3", "A2:1", "B1:1", "C0:0"]);

    [Fact]
    public void GroupJoin_Sum_on_the_foreign_key_without_a_navigation()
        => Assert3(false,
            db => Rows(db.Set<Cust>().GroupJoin(db.Set<Ord>(), c => c.Id, o => o.CustId, (c, g) => new { c.Id, T = g.Sum(o => o.Total) }).AsEnumerable(), z => z.Id + ":" + z.T),
            ["A1:630", "A2:700", "B1:30", "C0:0"]);

    [Fact]
    public void Explicit_subquery_Any_and_Count_without_a_navigation()
        => Assert3(false,
            db => Rows(db.Set<Cust>().Select(c => new
            {
                c.Id,
                Has = db.Set<Ord>().Where(o => o.CustId == c.Id).Any(),
                Big = db.Set<Ord>().Where(o => o.CustId == c.Id).Count(o => o.Total > 100)
            }).AsEnumerable(), z => z.Id + ":" + z.Has + ":" + z.Big),
            ["A1:True:1", "A2:True:1", "B1:True:0", "C0:False:0"]);

    [Fact]
    public void Where_Any_over_an_explicit_subquery_without_a_navigation()
        => Assert3(false,
            db => Rows(db.Set<Cust>().Where(c => db.Set<Ord>().Where(o => o.CustId == c.Id).Any(o => o.Total > 100)).AsEnumerable(), c => c.Id),
            ["A1", "A2"]);

    [Fact]
    public void OrderBy_Sum_over_an_explicit_subquery_without_a_navigation()
        => Assert3(false,
            db => db.Set<Cust>().OrderBy(c => db.Set<Ord>().Where(o => o.CustId == c.Id).Sum(o => o.Total)).ThenBy(c => c.Id).AsEnumerable().Select(c => c.Id).ToList(),
            ["C0", "B1", "A1", "A2"]);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GroupJoin_on_non_key_properties_counts_by_the_written_keys(bool withNavigation)
        // Name == Tag: only A1's name matches a Tag (orders 1 and 3).
        => Assert3(withNavigation,
            db => Rows(db.Set<Cust>().GroupJoin(db.Set<Ord>(), c => c.Name, o => o.Tag, (c, g) => new { c.Id, N = g.Count() }).AsEnumerable(), z => z.Id + ":" + z.N),
            ["A1:2", "A2:0", "B1:0", "C0:0"]);

    [Fact]
    public void GroupJoin_null_outer_key_matches_nothing()
        // EF guards the subquery with `c.Region != null`, so A2 (explicit null) and B1 (missing) count 0 although orders 2..7 have a
        // null or missing Region, which $lookup alone would match to them.
        => Assert3(false,
            db => Rows(db.Set<Cust>().GroupJoin(db.Set<Ord>(), c => c.Region, o => o.Region, (c, g) => new { c.Id, N = g.Count() }).AsEnumerable(), z => z.Id + ":" + z.N),
            ["A1:1", "A2:0", "B1:0", "C0:0"]);

    [Fact]
    public void Explicit_subquery_unguarded_null_equals_null()
        // No guard, so C# semantics: null (and a missing field, which reads as null) equals null. Orders 2..7 have no Region.
        => Assert3(false,
            db => Rows(db.Set<Cust>().Select(c => new { c.Id, N = db.Set<Ord>().Where(o => o.Region == c.Region).Count() }).AsEnumerable(), z => z.Id + ":" + z.N),
            ["A1:1", "A2:6", "B1:6", "C0:0"]);

    [Fact]
    public void Two_different_filters_over_the_same_keys_do_not_share_an_array()
        // Same correlation, different uncorrelated filters, in one projection: each filter set needs its own looked-up array, or both
        // counts would read the first one.
        => Assert3(false,
            db => Rows(db.Set<Cust>().Select(c => new
            {
                c.Id,
                Big = db.Set<Ord>().Where(o => o.CustId == c.Id).Where(o => o.Total > 100).Count(),
                Small = db.Set<Ord>().Where(o => o.CustId == c.Id).Where(o => o.Total < 100).Count()
            }).AsEnumerable(), z => z.Id + ":" + z.Big + ":" + z.Small),
            ["A1:1:2", "A2:1:0", "B1:0:1", "C0:0:0"]);

    [Fact]
    public void Count_over_a_navigation_to_a_derived_type_counts_only_that_type()
    {
        // Owner A1 has dogs Rex and Zed and the cat Tom (all with OwnerId A1). The lookup must be narrowed by discriminator: a keyed
        // fallback that skipped the narrowing would count the cat too (3).
        var names = SeedAnimals();
        foreach (var mode in new[] { MongoQueryMode.NativeOnly, MongoQueryMode.Native })
        {
            using var db = CreateAnimals(names, mode);
            List<string>? rows = null;
            try
            {
                rows = db.Set<Owner>().Select(o => new { o.Id, N = o.Dogs.Count }).AsEnumerable()
                    .Select(z => z.Id + ":" + z.N).OrderBy(s => s).ToList();
            }
            catch (Exception e) when (e is NativeTranslationNotSupportedException or InvalidOperationException)
            {
                // A loud refusal is acceptable for a derived target; only a wrong count is not.
            }

            if (rows is not null)
                Assert.Equal(["A1:2", "B1:1"], rows);
        }
    }

    private static List<string> CountAbove(Ctx db, int cutoff)
        => Rows(db.Set<Cust>().GroupJoin(db.Set<Ord>().Where(o => o.Total > cutoff), c => c.Id, o => o.CustId, (c, g) => new { c.Id, N = g.Count() }).AsEnumerable(),
            z => z.Id + ":" + z.N);

    [Theory]
    [InlineData(MongoQueryMode.NativeOnly)]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.DriverLinq)]
    public void GroupJoin_over_a_filtered_inner_is_read_per_execution(MongoQueryMode mode)
    {
        var names = Seed();
        using var db = Create(names, mode, false);
        NativeModeAssert.TwiceWithDifferentValues(
            v => CountAbove(db, (int)v!), 15, ["A1:2", "A2:1", "B1:1", "C0:0"], 650, ["A1:0", "A2:1", "B1:0", "C0:0"]);
    }

    private Ctx Create(string[] names, MongoQueryMode mode, bool withNavigation)
    {
        var options = new DbContextOptionsBuilder<Ctx>()
            .UseMongoDB(database.Client, database.MongoDatabase.DatabaseNamespace.DatabaseName, o => o.UseQueryMode(mode))
            .ReplaceService<IModelCacheKeyFactory, UniqueModelCacheKeyFactory>()
            .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
            .Options;
        return new Ctx(options, names, withNavigation);
    }

    private sealed class UniqueModelCacheKeyFactory : IModelCacheKeyFactory
    {
        private static int _count;
        public object Create(DbContext context, bool designTime) => Interlocked.Increment(ref _count);
    }

    private sealed class AnimalCtx(DbContextOptions<AnimalCtx> options, string[] names) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder mb)
        {
            mb.Entity<Owner>(b =>
            {
                b.ToCollection(names[0]);
                b.HasMany(o => o.Dogs).WithOne().HasForeignKey(d => d.OwnerId);
            });
            mb.Entity<Animal>().ToCollection(names[1]);
            mb.Entity<Dog>();
            mb.Entity<Cat>();
        }
    }

    private sealed class Ctx(DbContextOptions<Ctx> options, string[] names, bool withNavigation) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder mb)
        {
            mb.Entity<Cust>(b =>
            {
                b.ToCollection(names[0]);
                if (!withNavigation)
                    b.Ignore(c => c.Orders);
            });
            mb.Entity<Ord>(b =>
            {
                b.ToCollection(names[1]);
                if (withNavigation)
                    b.HasOne(o => o.Cust).WithMany(c => c.Orders).HasForeignKey(o => o.CustId);
                else
                    b.Ignore(o => o.Cust);
            });
        }
    }
}
