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
/// A correlated subquery over a referenced collection (<c>Orders.Where(o =&gt; ...).Count()</c>, which is what EF Core makes of
/// a <c>GroupJoin</c> with an aggregate or a collection-navigation access) may bind to the collection navigation's
/// <c>$lookup</c> ONLY when its predicate is exactly the foreign-key correlation of that navigation. Binding any other
/// predicate to the navigation silently answers with the foreign-key rows (design:
/// <c>docs/superpowers/specs/2026-10-08-native-referenced-collection-aggregates-design.md</c>, Phase 0).
/// <para>
/// Hand-computed expectations over raw seeds. The wrong-rows cases accept a loud refusal in any mode (a shape the provider
/// does not support must fail, never answer with the wrong rows); the controls must answer correctly.
/// </para>
/// </summary>
[XUnitCollection("QueryTests")]
public class ReferencedCollectionCorrelationTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    public class Cust
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public List<Ord> Orders { get; set; } = [];
    }

    public class Ord
    {
        public int Id { get; set; }
        public int Total { get; set; }
        public string? CustId { get; set; }
        public string? Tag { get; set; }
        public Cust? Cust { get; set; }
    }

    // Customers A1 (Alpha1), A2, B1 and C0 (no orders). Orders: 1(A1,10,Tag Alpha1) 2(A1,20) 3(A1,600,Tag Alpha1) 4(A2,700) 5(B1,30);
    // 6 has a dangling customer and 7 none.
    private string[] Seed()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var names = new[]
        {
            TemporaryDatabaseFixtureBase.CreateCollectionName("rccCusts") + suffix,
            TemporaryDatabaseFixtureBase.CreateCollectionName("rccOrds") + suffix
        };

        database.MongoDatabase.GetCollection<BsonDocument>(names[0]).InsertMany(
        [
            new BsonDocument { { "_id", "A1" }, { "Name", "Alpha1" } },
            new BsonDocument { { "_id", "A2" }, { "Name", "Alpha2" } },
            new BsonDocument { { "_id", "B1" }, { "Name", "Beta1" } },
            new BsonDocument { { "_id", "C0" }, { "Name", "Nobody" } }
        ]);
        database.MongoDatabase.GetCollection<BsonDocument>(names[1]).InsertMany(
        [
            new BsonDocument { { "_id", 1 }, { "Total", 10 }, { "CustId", "A1" }, { "Tag", "Alpha1" } },
            new BsonDocument { { "_id", 2 }, { "Total", 20 }, { "CustId", "A1" } },
            new BsonDocument { { "_id", 3 }, { "Total", 600 }, { "CustId", "A1" }, { "Tag", "Alpha1" } },
            new BsonDocument { { "_id", 4 }, { "Total", 700 }, { "CustId", "A2" } },
            new BsonDocument { { "_id", 5 }, { "Total", 30 }, { "CustId", "B1" } },
            new BsonDocument { { "_id", 6 }, { "Total", 40 }, { "CustId", "ZZ" } },
            new BsonDocument { { "_id", 7 }, { "Total", 50 }, { "CustId", BsonNull.Value } }
        ]);
        return names;
    }

    private Ctx Create(string[] names, MongoQueryMode mode)
    {
        var options = new DbContextOptionsBuilder<Ctx>()
            .UseMongoDB(database.Client, database.MongoDatabase.DatabaseNamespace.DatabaseName, o => o.UseQueryMode(mode))
            .ReplaceService<IModelCacheKeyFactory, UniqueModelCacheKeyFactory>()
            .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
            .Options;
        return new Ctx(options, names);
    }

    /// <summary>The control: every mode that does not refuse must answer exactly <paramref name="expected"/>.</summary>
    private void AssertAnswersOrRefuses(Func<Ctx, List<string>> query, List<string> expected, bool mustNotRefuse)
    {
        var names = Seed();
        foreach (var mode in new[] { MongoQueryMode.NativeOnly, MongoQueryMode.Native, MongoQueryMode.DriverLinq })
        {
            using var db = Create(names, mode);
            List<string>? rows = null;
            try
            {
                rows = query(db);
            }
            catch (NativeTranslationNotSupportedException) when (mode == MongoQueryMode.NativeOnly)
            {
                // The native path may decline a control; the other modes must still answer.
            }
            catch (Exception) when (!mustNotRefuse)
            {
                // A loud refusal is correct for a shape the provider does not support.
            }

            if (rows is not null)
            {
                Assert.True(expected.SequenceEqual(rows), $"{mode} returned [{string.Join(", ", rows)}], expected [{string.Join(", ", expected)}]");
            }
        }
    }

    // ---- wrong-rows cases: the predicate is NOT the navigation's FK correlation ----

    [Fact]
    public void GroupJoin_on_non_key_properties_never_counts_by_the_foreign_key()
        => AssertAnswersOrRefuses(
            db => db.Set<Cust>()
                .GroupJoin(db.Set<Ord>(), c => c.Name, o => o.Tag, (c, g) => new { c.Id, N = g.Count() })
                .AsEnumerable().Select(z => z.Id + ":" + z.N).OrderBy(s => s).ToList(),
            // Only A1's name matches a Tag (orders 1 and 3).
            ["A1:2", "A2:0", "B1:0", "C0:0"], mustNotRefuse: false);

    [Fact]
    public void Uncorrelated_subquery_count_is_not_bound_to_the_navigation()
        => AssertAnswersOrRefuses(
            db => db.Set<Cust>()
                .Select(c => new { c.Id, N = db.Set<Ord>().Where(o => o.Total > 100).Count() })
                .AsEnumerable().Select(z => z.Id + ":" + z.N).OrderBy(s => s).ToList(),
            // Orders 3 and 4 exceed 100, for every customer alike.
            ["A1:2", "A2:2", "B1:2", "C0:2"], mustNotRefuse: false);

    [Fact]
    public void Correlation_on_a_non_key_outer_member_is_not_bound_to_the_navigation()
        => AssertAnswersOrRefuses(
            db => db.Set<Cust>()
                .Select(c => new { c.Id, N = db.Set<Ord>().Where(o => o.CustId == c.Name).Count() })
                .AsEnumerable().Select(z => z.Id + ":" + z.N).OrderBy(s => s).ToList(),
            // No order's CustId equals any customer's Name.
            ["A1:0", "A2:0", "B1:0", "C0:0"], mustNotRefuse: false);

    [Fact]
    public void Correct_and_incorrect_correlation_in_one_projection_are_not_mixed()
        => AssertAnswersOrRefuses(
            db => db.Set<Cust>()
                .Select(c => new { c.Id, A = c.Orders.Count, B = db.Set<Ord>().Where(o => o.Total > 100).Count() })
                .AsEnumerable().Select(z => z.Id + ":" + z.A + ":" + z.B).OrderBy(s => s).ToList(),
            // A is the FK count; B must stay the uncorrelated count rather than being rewritten to A's lookup.
            ["A1:3:2", "A2:1:2", "B1:1:2", "C0:0:2"], mustNotRefuse: false);

    [Fact]
    public void Uncorrelated_list_subquery_is_not_bound_to_the_navigation()
        => AssertAnswersOrRefuses(
            db => db.Set<Cust>()
                .Select(c => new { c.Id, L = db.Set<Ord>().Where(o => o.Total > 100).ToList() })
                .AsEnumerable().Select(z => z.Id + ":" + string.Join(",", z.L.Select(o => o.Id).OrderBy(i => i))).OrderBy(s => s).ToList(),
            ["A1:3,4", "A2:3,4", "B1:3,4", "C0:3,4"], mustNotRefuse: false);

    [Fact]
    public void Correlation_on_a_non_key_outer_member_list_is_not_bound_to_the_navigation()
        => AssertAnswersOrRefuses(
            db => db.Set<Cust>()
                .Select(c => new { c.Id, L = db.Set<Ord>().Where(o => o.CustId == c.Name).ToList() })
                .AsEnumerable().Select(z => z.Id + ":" + string.Join(",", z.L.Select(o => o.Id).OrderBy(i => i))).OrderBy(s => s).ToList(),
            ["A1:", "A2:", "B1:", "C0:"], mustNotRefuse: false);

    // ---- controls: genuine FK correlations keep working ----

    [Fact]
    public void Navigation_count_is_the_foreign_key_count()
        => AssertAnswersOrRefuses(
            db => db.Set<Cust>().Select(c => new { c.Id, N = c.Orders.Count })
                .AsEnumerable().Select(z => z.Id + ":" + z.N).OrderBy(s => s).ToList(),
            ["A1:3", "A2:1", "B1:1", "C0:0"], mustNotRefuse: true);

    [Fact]
    public void GroupJoin_on_the_foreign_key_counts_each_customers_orders()
        => AssertAnswersOrRefuses(
            db => db.Set<Cust>()
                .GroupJoin(db.Set<Ord>(), c => c.Id, o => o.CustId, (c, g) => new { c.Id, N = g.Count() })
                .AsEnumerable().Select(z => z.Id + ":" + z.N).OrderBy(s => s).ToList(),
            ["A1:3", "A2:1", "B1:1", "C0:0"], mustNotRefuse: true);

    [Fact]
    public void Where_on_the_navigation_count_is_unchanged()
        => AssertAnswersOrRefuses(
            db => db.Set<Cust>().Where(c => c.Orders.Count > 1).AsEnumerable().Select(c => c.Id).OrderBy(s => s).ToList(),
            ["A1"], mustNotRefuse: true);

    [Fact]
    public void GroupJoin_count_then_Where_is_unchanged()
        => AssertAnswersOrRefuses(
            db => db.Set<Cust>()
                .GroupJoin(db.Set<Ord>(), c => c.Id, o => o.CustId, (c, g) => new { c.Id, N = g.Count() })
                .Where(z => z.N > 1).AsEnumerable().Select(z => z.Id + ":" + z.N).OrderBy(s => s).ToList(),
            ["A1:3"], mustNotRefuse: true);

    private sealed class UniqueModelCacheKeyFactory : IModelCacheKeyFactory
    {
        private static int _count;
        public object Create(DbContext context, bool designTime) => Interlocked.Increment(ref _count);
    }

    private sealed class Ctx(DbContextOptions<Ctx> options, string[] names) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder mb)
        {
            mb.Entity<Cust>().ToCollection(names[0]);
            mb.Entity<Ord>(b =>
            {
                b.ToCollection(names[1]);
                b.HasOne(o => o.Cust).WithMany(c => c.Orders).HasForeignKey(o => o.CustId);
            });
        }
    }
}
