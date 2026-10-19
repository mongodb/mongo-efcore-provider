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
using Microsoft.EntityFrameworkCore.Infrastructure;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.Infrastructure;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// A non-nullable scalar of an optional reference navigation projected over the join scope (<c>o.Customer!.Rank</c>):
/// an unmatched order (and a matched customer that omits the field) has no element after the <c>$lookup</c>, which
/// reads <c>default</c>, as driver-LINQ's <c>$project</c> push-down did (decision D-F10, controller ruling in the
/// Phase 1 final review); an explicit null still throws, except a <c>bool</c>, which the driver's
/// <c>BooleanSerializer</c> reads as false. Each row set is checked against canonical main (upstream/main dec7e26f,
/// EF10 probe) in all three modes.
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeJoinScopeMissingScalarTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    private class Customer
    {
        public int Id { get; set; }
        public int Rank { get; set; }
        public bool Flag { get; set; }
    }

    private class Order
    {
        public int Id { get; set; }
        public int? CustomerId { get; set; }
        public Customer? Customer { get; set; }
    }

    private const string Throws = "throws";

    // Row sets: unmatched (12: null FK), matched explicit null (13), matched missing (14), well-formed + unmatched.
    private static readonly int[][] RowSets = [[12], [13], [14], [10, 11, 12]];

    private static readonly Dictionary<string, (Func<IQueryable<Order>, string> Run, string[] Main)> MainParityShapes = new()
    {
        ["bare_rank"] = (q => Join(q.OrderBy(o => o.Id).Select(o => o.Customer!.Rank)), ["0", Throws, "0", "5,7,0"]),
        ["anon_rank"] = (q => Join(q.OrderBy(o => o.Id).Select(o => new { o.Id, R = o.Customer!.Rank }).AsEnumerable()
            .Select(a => $"{a.Id}:{a.R}")), ["12:0", Throws, "14:0", "10:5,11:7,12:0"]),
        ["bare_flag"] = (q => Join(q.OrderBy(o => o.Id).Select(o => o.Customer!.Flag)), ["False", "False", "False", "True,False,False"]),
        ["anon_flag"] = (q => Join(q.OrderBy(o => o.Id).Select(o => new { o.Id, F = o.Customer!.Flag }).AsEnumerable()
            .Select(a => $"{a.Id}:{a.F}")), ["12:False", "13:False", "14:False", "10:True,11:False,12:False"]),
        // Already matched main before the fix (Task 1.15's wrapped reduction: a MISSING winner reads default).
        ["select_min"] = (q => q.Select(o => o.Customer!.Rank).Min().ToString(), ["0", Throws, "0", "0"]),
        ["select_max"] = (q => q.Select(o => o.Customer!.Rank).Max().ToString(), ["0", Throws, "0", "7"]),
    };

    public static TheoryData<string> MainParityShapeNames => [.. MainParityShapes.Keys];

    [Theory]
    [MemberData(nameof(MainParityShapeNames))]
    public void Join_scope_scalar_over_a_missing_or_null_element_matches_main(string shape)
    {
        var (run, main) = MainParityShapes[shape];
        var prefix = Seed();

        foreach (var mode in new[] { MongoQueryMode.NativeOnly, MongoQueryMode.Native, MongoQueryMode.DriverLinq })
        {
            var actual = new List<string>();
            foreach (var ids in RowSets)
            {
                using var db = CreateContext(prefix, mode);
                try
                {
                    actual.Add(run(db.Orders.Where(o => ids.Contains(o.Id))));
                }
                // A NativeOnly decline (NativeTranslationNotSupportedException) is not a row outcome: it fails the test.
                catch (Exception ex) when (ex is FormatException or InvalidOperationException)
                {
                    actual.Add(Throws);
                }
            }

            Assert.True(main.SequenceEqual(actual), $"{mode}: expected [{string.Join("; ", main)}], got [{string.Join("; ", actual)}]");
        }
    }

    private static string Join<T>(IEnumerable<T> values)
        => string.Join(",", values.ToList());

    private string Seed()
    {
        var prefix = TemporaryDatabaseFixtureBase.CreateCollectionName("JSM") + Guid.NewGuid().ToString("N")[..8];
        database.MongoDatabase.GetCollection<BsonDocument>(prefix + "c").InsertMany(
        [
            new() { { "_id", 1 }, { "Rank", 5 }, { "Flag", true } },
            new() { { "_id", 2 }, { "Rank", 7 }, { "Flag", false } },
            new() { { "_id", 3 }, { "Rank", BsonNull.Value }, { "Flag", BsonNull.Value } },
            new() { { "_id", 4 } }
        ]);
        database.MongoDatabase.GetCollection<BsonDocument>(prefix + "o").InsertMany(
        [
            new() { { "_id", 10 }, { "CustomerId", 1 } },
            new() { { "_id", 11 }, { "CustomerId", 2 } },
            new() { { "_id", 12 }, { "CustomerId", BsonNull.Value } },
            new() { { "_id", 13 }, { "CustomerId", 3 } },
            new() { { "_id", 14 }, { "CustomerId", 4 } }
        ]);
        return prefix;
    }

    private Ctx CreateContext(string prefix, MongoQueryMode mode)
        => new(
            new DbContextOptionsBuilder<Ctx>()
                .UseMongoDB(database.Client, database.MongoDatabase.DatabaseNamespace.DatabaseName, b => b.UseQueryMode(mode))
                .ReplaceService<IModelCacheKeyFactory, IgnoreCacheKeyFactory>()
                .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
                .Options,
            prefix);

    private class Ctx(DbContextOptions options, string prefix) : DbContext(options)
    {
        public DbSet<Customer> Customers { get; set; } = null!;
        public DbSet<Order> Orders { get; set; } = null!;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Customer>().ToCollection(prefix + "c");
            modelBuilder.Entity<Order>().ToCollection(prefix + "o")
                .HasOne(o => o.Customer).WithMany().HasForeignKey(o => o.CustomerId);
        }
    }
}
