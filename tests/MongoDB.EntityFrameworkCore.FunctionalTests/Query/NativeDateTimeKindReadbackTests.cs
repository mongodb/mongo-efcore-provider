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
/// Native read-back of <c>DateTime</c> values from properties configured with <c>HasDateTimeKind</c>. Every value a
/// native pipeline surfaces from such a property (a GroupBy key, a <c>$min</c>/<c>$max</c> accumulator, a terminal
/// <c>Max</c>/<c>Min</c>, a cast, a join-scope or owned-reference leaf) must read back with the property's
/// configured <c>Kind</c> and instant, exactly as materializing the entity does. A computed date over a
/// <c>Local</c>-kind property (<c>AddDays</c>, <c>.Date</c>, a ternary mixing kinds) has no property to read it back
/// through, so it declines.
/// </summary>
/// <remarks>
/// The oracle is the same LINQ run in memory over the tracked entities (so each date is what the property's own
/// serializer materializes). Values are formatted as <c>Kind@Ticks</c>. The seeded instants are chosen so their
/// local and UTC representations differ in any time zone with a non-zero offset on those dates; in a UTC host zone
/// the tick comparison can't tell Local from Utc, so every test also asserts the <c>Kind</c> explicitly.
/// </remarks>
[XUnitCollection("QueryTests")]
public class NativeDateTimeKindReadbackTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    public class Owner
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
        public DateTime LocalDate { get; set; }
        public List<Order> Orders { get; set; } = [];
    }

    public class Order
    {
        public ObjectId Id { get; set; }
        public string Country { get; set; } = "";
        public DateTime LocalDate { get; set; }
        public DateTime? NLocalDate { get; set; }
        public DateTime UtcDate { get; set; }
        public DateTime PlainDate { get; set; }
        public ObjectId OwnerId { get; set; }
        public Owner Owner { get; set; } = null!;
        public Detail Detail { get; set; } = new();
    }

    public class KeyDto
    {
        public string Country { get; set; } = "";
        public DateTime D { get; set; }
    }

    public class Detail
    {
        public string Note { get; set; } = "";
        public DateTime LocalDate { get; set; }
    }

    private sealed class KindContext(TemporaryDatabaseFixture database, string owners, string orders, MongoQueryMode mode)
        : DbContext(new DbContextOptionsBuilder<KindContext>()
            .UseMongoDB(database.Client, database.MongoDatabase.DatabaseNamespace.DatabaseName, b => b.UseQueryMode(mode))
            .ReplaceService<IModelCacheKeyFactory, IgnoreCacheKeyFactory>()
            .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
            .Options)
    {
        public DbSet<Owner> Owners { get; set; } = null!;
        public DbSet<Order> Orders { get; set; } = null!;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Owner>(b =>
            {
                b.ToCollection(owners);
                b.Property(w => w.LocalDate).HasDateTimeKind(DateTimeKind.Local);
                b.HasMany(w => w.Orders).WithOne(o => o.Owner).HasForeignKey(o => o.OwnerId);
            });
            modelBuilder.Entity<Order>(b =>
            {
                b.ToCollection(orders);
                b.Property(o => o.LocalDate).HasDateTimeKind(DateTimeKind.Local);
                b.Property(o => o.NLocalDate).HasDateTimeKind(DateTimeKind.Local);
                b.Property(o => o.UtcDate).HasDateTimeKind(DateTimeKind.Utc);
                b.OwnsOne(o => o.Detail, d => d.Property(x => x.LocalDate).HasDateTimeKind(DateTimeKind.Local));
            });
        }

        private sealed class IgnoreCacheKeyFactory : IModelCacheKeyFactory
        {
            private static int _count;
            public object Create(DbContext context, bool designTime) => Interlocked.Increment(ref _count);
        }
    }

    // Summer, winter and summer instants, so a zone with DST still has a non-zero offset on at least one of them.
    private static readonly DateTime D1 = new(2020, 6, 15, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime D2 = new(2021, 1, 10, 8, 30, 0, DateTimeKind.Utc);
    private static readonly DateTime D3 = new(2022, 7, 4, 16, 45, 0, DateTimeKind.Utc);

    private static readonly DateTime Threshold = new(2021, 1, 1, 0, 0, 0, DateTimeKind.Local);

    private static string F(DateTime d) => $"{d.Kind}@{d.Ticks}";
    private static string F(DateTime? d) => d is { } v ? F(v) : "null";

    // Owners: A (D1), B (D2). Orders: UK/D1/owner A, UK/D2 (NLocalDate null)/owner B, FR/D3/owner A.
    private (string Owners, string Orders) Seed()
    {
        var owners = "own" + Guid.NewGuid().ToString("N")[..8];
        var orders = "ord" + Guid.NewGuid().ToString("N")[..8];
        using var db = new KindContext(database, owners, orders, MongoQueryMode.Native);
        var a = new Owner { Id = ObjectId.GenerateNewId(), Name = "A", LocalDate = D1 };
        var b = new Owner { Id = ObjectId.GenerateNewId(), Name = "B", LocalDate = D2 };
        db.Owners.AddRange(a, b);
        db.Orders.AddRange(
            new Order
            {
                Id = ObjectId.GenerateNewId(), Country = "UK", LocalDate = D1, NLocalDate = D1, UtcDate = D1, PlainDate = D1,
                OwnerId = a.Id, Detail = new Detail { Note = "x", LocalDate = D1 }
            },
            new Order
            {
                Id = ObjectId.GenerateNewId(), Country = "UK", LocalDate = D2, NLocalDate = null, UtcDate = D2, PlainDate = D2,
                OwnerId = b.Id, Detail = new Detail { Note = "y", LocalDate = D2 }
            },
            new Order
            {
                Id = ObjectId.GenerateNewId(), Country = "FR", LocalDate = D3, NLocalDate = D3, UtcDate = D3, PlainDate = D3,
                OwnerId = a.Id, Detail = new Detail { Note = "z", LocalDate = D3 }
            });
        db.SaveChanges();
        return (owners, orders);
    }

    // The reference: the same query over the tracked entities in memory, sorted for an order-independent compare.
    private List<string> Reference((string Owners, string Orders) seed, Func<IQueryable<Order>, IQueryable<Owner>, List<string>> query)
    {
        using var db = new KindContext(database, seed.Owners, seed.Orders, MongoQueryMode.Native);
        var owners = db.Owners.ToList();
        var orders = db.Orders.ToList();
        return Sorted(query(orders.AsQueryable(), owners.AsQueryable()));
    }

    private List<string> Execute(
        (string Owners, string Orders) seed, MongoQueryMode mode, Func<IQueryable<Order>, IQueryable<Owner>, List<string>> query)
    {
        using var db = new KindContext(database, seed.Owners, seed.Orders, mode);
        return Sorted(query(db.Orders, db.Owners));
    }

    private static List<string> Sorted(List<string> values) => values.OrderBy(s => s, StringComparer.Ordinal).ToList();

    // Goes native, agrees with driver-LINQ, and equals the tracked-entity reference, which must carry `expectedKind`.
    private void AssertNativeMatchesEntities(
        DateTimeKind expectedKind, Func<IQueryable<Order>, IQueryable<Owner>, List<string>> query)
    {
        var seed = Seed();
        var reference = Reference(seed, query);
        Assert.Contains(reference, s => s.Contains(expectedKind + "@"));

        var native = NativeModeAssert.NativeAndParity(mode => Execute(seed, mode, query));
        Assert.Equal(reference, native);
    }

    // Declines under NativeOnly; Native falls back to the same answer as explicit DriverLinq.
    private void AssertDeclines(Func<IQueryable<Order>, IQueryable<Owner>, List<string>> query)
    {
        var seed = Seed();
        NativeModeAssert.DeclinesCleanly(mode => Execute(seed, mode, query));
    }

    // ---- GroupBy keys ----

    [Fact]
    public void GroupBy_scalar_key_reads_back_with_the_property_kind()
        => AssertNativeMatchesEntities(DateTimeKind.Local, (q, _) =>
            q.GroupBy(o => o.LocalDate).Select(g => new { g.Key, C = g.Count() }).AsEnumerable()
                .Select(x => F(x.Key) + "#" + x.C).ToList());

    [Fact]
    public void GroupBy_composite_key_part_reads_back_with_the_property_kind()
        => AssertNativeMatchesEntities(DateTimeKind.Local, (q, _) =>
            q.GroupBy(o => new { o.Country, o.LocalDate }).Select(g => new { g.Key.Country, g.Key.LocalDate, C = g.Count() })
                .AsEnumerable().Select(x => x.Country + F(x.LocalDate) + "#" + x.C).ToList());

    [Fact]
    public void GroupBy_nullable_key_reads_back_with_the_property_kind()
        => AssertNativeMatchesEntities(DateTimeKind.Local, (q, _) =>
            q.GroupBy(o => o.NLocalDate).Select(g => new { g.Key, C = g.Count() }).AsEnumerable()
                .Select(x => F(x.Key) + "#" + x.C).ToList());

    [Fact]
    public void GroupBy_coalesced_key_reads_back_with_the_property_kind()
        => AssertNativeMatchesEntities(DateTimeKind.Local, (q, _) =>
            q.GroupBy(o => o.NLocalDate ?? o.LocalDate).Select(g => new { g.Key, C = g.Count() }).AsEnumerable()
                .Select(x => F(x.Key) + "#" + x.C).ToList());

    [Fact]
    public void GroupBy_owned_reference_key_reads_back_with_the_property_kind()
        => AssertNativeMatchesEntities(DateTimeKind.Local, (q, _) =>
            q.GroupBy(o => o.Detail.LocalDate).Select(g => new { g.Key, C = g.Count() }).AsEnumerable()
                .Select(x => F(x.Key) + "#" + x.C).ToList());

    [Fact]
    public void GroupBy_reference_navigation_key_reads_back_with_the_property_kind()
        => AssertNativeMatchesEntities(DateTimeKind.Local, (q, _) =>
            q.GroupBy(o => o.Owner.LocalDate).Select(g => new { g.Key, M = g.Max(x => x.LocalDate) }).AsEnumerable()
                .Select(x => F(x.Key) + F(x.M)).ToList());

    [Fact]
    public void GroupBy_key_ordered_and_filtered_after_the_group_reads_back_with_the_property_kind()
        => AssertNativeMatchesEntities(DateTimeKind.Local, (q, _) =>
            q.GroupBy(o => o.LocalDate).Select(g => new { g.Key, C = g.Count() }).Where(x => x.Key > Threshold)
                .AsEnumerable().Select(x => F(x.Key)).ToList());

    [Fact]
    public void GroupBy_key_inside_a_nested_construction_reads_back_with_the_property_kind()
        => AssertNativeMatchesEntities(DateTimeKind.Local, (q, _) =>
            q.GroupBy(o => o.LocalDate).Select(g => new { C = g.Count(), Inner = new { K = g.Key, M = g.Max(x => x.NLocalDate) } })
                .AsEnumerable().Select(x => F(x.Inner.K) + F(x.Inner.M)).ToList());

    [Fact]
    public void Nested_GroupBy_key_and_accumulator_read_back_with_the_property_kind()
        => AssertNativeMatchesEntities(DateTimeKind.Local, (q, _) =>
            q.GroupBy(o => new { o.Country, o.LocalDate }).Select(g => new { g.Key.Country, g.Key.LocalDate, C = g.Count() })
                .GroupBy(x => x.LocalDate).Select(g => new { g.Key, M = g.Max(y => y.LocalDate), N = g.Count() })
                .AsEnumerable().Select(x => F(x.Key) + F(x.M) + "#" + x.N).ToList());

    [Fact]
    public void Nested_GroupBy_over_a_prior_max_reads_back_with_the_property_kind()
        // The outer key and accumulator read the prior stage's $max alias, which has no property of its own; it is
        // traced back through the prior flattened $project to the prior $group's operand.
        => AssertNativeMatchesEntities(DateTimeKind.Local, (q, _) =>
            q.GroupBy(o => o.Country).Select(g => new { g.Key, M = g.Max(x => x.LocalDate) })
                .GroupBy(x => x.M).Select(g => new { g.Key, MM = g.Min(y => y.M), N = g.Count() })
                .AsEnumerable().Select(x => F(x.Key) + F(x.MM) + "#" + x.N).ToList());

    // A whole composite g.Key is otherwise read back through its type's class map, which ignores the parts' kind.
    [Fact]
    public void Whole_composite_key_reads_back_with_the_property_kind()
        => AssertNativeMatchesEntities(DateTimeKind.Local, (q, _) =>
            q.GroupBy(o => new { o.Country, o.LocalDate }).Select(g => new { g.Key, C = g.Count() }).AsEnumerable()
                .Select(x => x.Key.Country + F(x.Key.LocalDate) + "#" + x.C).ToList());

    [Fact]
    public void Whole_composite_key_with_a_nullable_part_reads_back_with_the_property_kind()
        => AssertNativeMatchesEntities(DateTimeKind.Local, (q, _) =>
            q.GroupBy(o => new { o.Country, N = o.NLocalDate }).Select(g => new { g.Key, C = g.Count() }).AsEnumerable()
                .Select(x => x.Key.Country + F(x.Key.N) + "#" + x.C).ToList());

    [Fact]
    public void Whole_DTO_key_reads_back_with_the_property_kind()
        => AssertNativeMatchesEntities(DateTimeKind.Local, (q, _) =>
            q.GroupBy(o => new KeyDto { Country = o.Country, D = o.LocalDate }).Select(g => new { g.Key, C = g.Count() })
                .AsEnumerable().Select(x => x.Key.Country + F(x.Key.D) + "#" + x.C).ToList());

    [Fact]
    public void Whole_composite_key_beside_its_part_and_an_accumulator_reads_back_with_the_property_kind()
        // Key.LocalDate and LocalDate must agree in the same row.
        => AssertNativeMatchesEntities(DateTimeKind.Local, (q, _) =>
            q.GroupBy(o => new { o.Country, o.LocalDate })
                .Select(g => new { g.Key, g.Key.LocalDate, M = g.Max(x => x.NLocalDate) })
                .AsEnumerable().Select(x => x.Key.Country + F(x.Key.LocalDate) + F(x.LocalDate) + F(x.M)).ToList());

    [Fact]
    public void Whole_composite_key_nested_and_filtered_after_the_group_reads_back_with_the_property_kind()
        => AssertNativeMatchesEntities(DateTimeKind.Local, (q, _) =>
            q.GroupBy(o => new { o.Country, o.LocalDate }).Select(g => new { I = new { g.Key }, C = g.Count() })
                .Where(x => x.C > 0)
                .AsEnumerable().Select(x => x.I.Key.Country + F(x.I.Key.LocalDate)).ToList());

    // ---- $min / $max accumulators ----

    [Fact]
    public void Group_min_and_max_read_back_with_the_property_kind()
        => AssertNativeMatchesEntities(DateTimeKind.Local, (q, _) =>
            q.GroupBy(o => o.Country)
                .Select(g => new { g.Key, Mx = g.Max(x => x.LocalDate), Mn = g.Min(x => x.LocalDate), NMx = g.Max(x => x.NLocalDate), NMn = g.Min(x => x.NLocalDate) })
                .AsEnumerable().Select(x => x.Key + F(x.Mx) + F(x.Mn) + F(x.NMx) + F(x.NMn)).ToList());

    [Fact]
    public void Group_element_selector_max_reads_back_with_the_property_kind()
        => AssertNativeMatchesEntities(DateTimeKind.Local, (q, _) =>
            q.GroupBy(o => o.Country, o => o.LocalDate).Select(g => new { g.Key, M = g.Max() })
                .AsEnumerable().Select(x => x.Key + F(x.M)).ToList());

    [Fact]
    public void Bare_group_max_reads_back_with_the_property_kind()
        => AssertNativeMatchesEntities(DateTimeKind.Local, (q, _) =>
            q.GroupBy(o => o.Country).Select(g => g.Max(x => x.LocalDate)).AsEnumerable().Select(F).ToList());

    [Fact]
    public void Group_distinct_max_and_min_read_back_with_the_property_kind()
        // $addToSet + a flattening $max/$min over the set: the result is one of the set's elements.
        => AssertNativeMatchesEntities(DateTimeKind.Local, (q, _) =>
            q.GroupBy(o => o.Country)
                .Select(g => new
                {
                    g.Key,
                    M = g.Select(x => x.LocalDate).Distinct().Max(),
                    N = g.Select(x => x.NLocalDate).Distinct().Min()
                })
                .AsEnumerable().Select(x => x.Key + F(x.M) + F(x.N)).ToList());

    [Fact]
    public void Bare_group_distinct_max_reads_back_with_the_property_kind()
        => AssertNativeMatchesEntities(DateTimeKind.Local, (q, _) =>
            q.GroupBy(o => o.Country).Select(g => g.Select(x => x.LocalDate).Distinct().Max()).AsEnumerable().Select(F).ToList());

    [Fact]
    public void Group_filtered_max_reads_back_with_the_property_kind()
        // The filter becomes {$cond: [pred, field, "$$REMOVE"]}; the sentinel contributes no value of its own.
        => AssertNativeMatchesEntities(DateTimeKind.Local, (q, _) =>
            q.GroupBy(o => o.Country).Select(g => new { g.Key, M = g.Where(x => x.PlainDate > Threshold).Max(x => x.NLocalDate) })
                .AsEnumerable().Select(x => x.Key + F(x.M)).ToList());

    // ---- terminal scalar aggregates ----

    [Fact]
    public void Terminal_max_and_min_read_back_with_the_property_kind()
        => AssertNativeMatchesEntities(DateTimeKind.Local, (q, _) =>
            [F(q.Max(o => o.LocalDate)), F(q.Min(o => o.LocalDate)), F(q.Max(o => o.NLocalDate))]);

    [Fact]
    public void Terminal_selectorless_max_reads_back_with_the_property_kind()
        => AssertNativeMatchesEntities(DateTimeKind.Local, (q, _) => [F(q.Select(o => o.LocalDate).Max())]);

    [Fact]
    public void Terminal_max_after_a_group_or_distinct_reads_back_with_the_property_kind()
        => AssertNativeMatchesEntities(DateTimeKind.Local, (q, _) =>
        [
            "g" + F(q.GroupBy(o => o.Country).Select(g => g.Max(x => x.LocalDate)).Max()),
            "s" + F(q.GroupBy(o => o.Country).Select(g => new { g.Key, M = g.Min(x => x.LocalDate) }).Max(x => x.M)),
            "d" + F(q.Select(o => new { o.Country, o.LocalDate }).Distinct().Max(x => x.LocalDate)),
        ]);

    [Fact]
    public void Terminal_max_over_an_owned_reference_reads_back_with_the_property_kind()
        => AssertNativeMatchesEntities(DateTimeKind.Local, (q, _) => [F(q.Max(o => o.Detail.LocalDate))]);

    // ---- projection leaves ----

    [Fact]
    public void Nullable_cast_leaf_reads_back_with_the_property_kind()
        => AssertNativeMatchesEntities(DateTimeKind.Local, (q, _) =>
            q.Select(o => (DateTime?)o.LocalDate).AsEnumerable().Select(F)
                .Concat(q.Select(o => new { D = (DateTime?)o.LocalDate }).AsEnumerable().Select(x => "w" + F(x.D))).ToList());

    [Fact]
    public void Ternary_over_two_same_kind_properties_reads_back_with_that_kind()
        => AssertNativeMatchesEntities(DateTimeKind.Local, (q, _) =>
            q.Select(o => new { D = o.Country == "UK" ? o.LocalDate : o.Detail.LocalDate }).AsEnumerable()
                .Select(x => F(x.D)).ToList());

    [Fact]
    public void Ternary_with_a_null_branch_reads_back_with_the_property_kind()
        => AssertNativeMatchesEntities(DateTimeKind.Local, (q, _) =>
            q.Select(o => new { D = o.Country == "UK" ? (DateTime?)o.LocalDate : null }).AsEnumerable()
                .Select(x => F(x.D)).ToList());

    [Fact]
    public void Reference_navigation_leaf_reads_back_with_the_property_kind()
        => AssertNativeMatchesEntities(DateTimeKind.Local, (q, _) =>
            q.Select(o => o.Owner.LocalDate).AsEnumerable().Select(F)
                .Concat(q.Select(o => new { o.Country, o.Owner.LocalDate }).AsEnumerable().Select(x => x.Country + F(x.LocalDate)))
                .ToList());

    [Fact]
    public void Set_operation_over_same_kind_properties_reads_back_with_that_kind()
        => AssertNativeMatchesEntities(DateTimeKind.Local, (q, _) =>
            q.Select(o => new { D = o.LocalDate }).Concat(q.Select(o => new { D = o.Detail.LocalDate })).AsEnumerable()
                .Select(x => F(x.D)).ToList());

    [Fact]
    public void Set_operation_mixing_local_and_default_kind_properties_declines()
        // Every combined row is read through the first operand's leaf, so the second operand's Local rows can't keep
        // their kind. The driver-LINQ fallback is wrong too (it reads every row with a single kind), so the parity leg
        // here only pins that Native falls back rather than answering.
        => AssertDeclines((q, _) =>
            q.Select(o => new { D = o.PlainDate }).Concat(q.Select(o => new { D = o.LocalDate })).AsEnumerable()
                .Select(x => F(x.D)).ToList());

    [Fact]
    public void Set_operation_mixing_kinds_inside_a_nested_construction_declines()
    {
        AssertDeclines((q, _) =>
            q.Select(o => new { X = new { D = o.PlainDate } }).Concat(q.Select(o => new { X = new { D = o.LocalDate } }))
                .AsEnumerable().Select(x => F(x.X.D)).ToList());
        AssertDeclines((q, _) =>
            q.Select(o => new { X = new { D = o.LocalDate } }).Concat(q.Select(o => new { X = new { D = o.PlainDate } }))
                .AsEnumerable().Select(x => F(x.X.D)).ToList());
    }

    // Bare scalar operands with different aliases (`PlainDate` vs `UtcDate`, or a column vs a grouped aggregate's `_v`):
    // the second operand is re-aliased to the first's alias, so the kind-shape comparison (which pairs operand leaves
    // by alias) sees both leaves. (A bare computed date such as `o.PlainDate.AddDays(1)` doesn't bind natively at all
    // yet, so it isn't covered here.)

    [Fact]
    public void Set_operation_of_bare_default_kind_date_columns_with_different_aliases_reads_back_with_the_default_kind()
        => AssertNativeMatchesEntities(DateTimeKind.Utc, (q, _) =>
            q.Select(o => o.PlainDate).Union(q.Select(o => o.UtcDate)).AsEnumerable().Select(F).ToList());

    [Fact]
    public void Set_operation_of_a_bare_local_kind_column_and_a_local_kind_aggregate_reads_back_with_that_kind()
    {
        AssertNativeMatchesEntities(DateTimeKind.Local, (q, _) =>
            q.Select(o => o.LocalDate).Concat(q.GroupBy(o => o.Country).Select(g => g.Max(o => o.LocalDate)))
                .AsEnumerable().Select(F).ToList());
        AssertNativeMatchesEntities(DateTimeKind.Local, (q, _) =>
            q.GroupBy(o => o.Country).Select(g => g.Max(o => o.LocalDate)).Concat(q.Select(o => o.LocalDate))
                .AsEnumerable().Select(F).ToList());
    }

    [Fact]
    public void Set_operation_of_bare_date_operands_mixing_local_and_default_kinds_declines()
    {
        AssertDeclines((q, _) =>
            q.Select(o => o.LocalDate).Concat(q.Select(o => o.PlainDate)).AsEnumerable().Select(F).ToList());
        AssertDeclines((q, _) =>
            q.Select(o => o.PlainDate).Concat(q.Select(o => o.LocalDate)).AsEnumerable().Select(F).ToList());
        AssertDeclines((q, _) =>
            q.Select(o => o.PlainDate).Concat(q.GroupBy(o => o.Country).Select(g => g.Max(o => o.LocalDate)))
                .AsEnumerable().Select(F).ToList());
    }

    [Fact]
    public void Correlated_reducer_leaf_reads_back_with_the_property_kind()
    {
        // No driver-LINQ oracle for a correlated reducer leaf (it hard-fails there), so NativeOnly is compared with the
        // tracked-entity reference only.
        Func<IQueryable<Order>, IQueryable<Owner>, List<string>> query = (_, w) =>
        [
            ..w.Select(x => new { x.Name, D = x.Orders.OrderByDescending(o => o.LocalDate).FirstOrDefault()!.LocalDate })
                .AsEnumerable().Select(x => "d" + x.Name + F(x.D)),
            ..w.Select(x => new { x.Name, D = x.Orders.OrderBy(o => o.LocalDate).First().LocalDate })
                .AsEnumerable().Select(x => "f" + x.Name + F(x.D)),
            ..w.Select(x => new { x.Name, D = x.Orders.OrderBy(o => o.LocalDate).FirstOrDefault()!.NLocalDate })
                .AsEnumerable().Select(x => "n" + x.Name + F(x.D)),
        ];

        var seed = Seed();
        var reference = Reference(seed, (q, w) =>
        {
            var orders = q.ToList();
            foreach (var owner in w)
                owner.Orders = orders.Where(o => o.OwnerId == owner.Id).ToList();
            return query(q, w);
        });
        Assert.Contains(reference, s => s.Contains("Local@"));
        Assert.Equal(reference, Execute(seed, MongoQueryMode.NativeOnly, query));
    }

    [Fact]
    public void Root_side_leaf_of_a_manual_join_reads_back_with_the_property_kind()
        => AssertNativeMatchesEntities(DateTimeKind.Local, (q, w) =>
        [
            ..w.Join(q, ow => ow.Id, o => o.OwnerId, (ow, o) => new { A = ow.LocalDate, o.Country })
                .AsEnumerable().Select(x => "a" + x.Country + F(x.A)),
            ..q.Join(w, o => o.OwnerId, ow => ow.Id, (o, ow) => new { o.LocalDate, ow.Name })
                .AsEnumerable().Select(x => "b" + x.Name + F(x.LocalDate)),
        ]);

    // Whole composite keys across a set op: a kind-sensitive key is rebuilt part by part while a default-kind key of the
    // same type is read whole by class map, and every combined row reads through the outer side's leaf, so a pairing
    // whose date parts differ in kind can't be reproduced. The driver-LINQ fallback is wrong too (it reads every row
    // with the other side's kind), so the parity leg here only pins that Native falls back rather than answering.
    [Fact]
    public void Set_operation_over_whole_composite_keys_mixing_kinds_declines()
    {
        AssertDeclines((q, _) =>
            q.GroupBy(o => new { o.Country, D = o.LocalDate }).Select(g => new { g.Key })
                .Concat(q.GroupBy(o => new { o.Country, D = o.PlainDate }).Select(g => new { g.Key }))
                .AsEnumerable().Select(x => x.Key.Country + F(x.Key.D)).ToList());
        AssertDeclines((q, _) =>
            q.GroupBy(o => new { o.Country, D = o.PlainDate }).Select(g => new { g.Key })
                .Concat(q.GroupBy(o => new { o.Country, D = o.LocalDate }).Select(g => new { g.Key }))
                .AsEnumerable().Select(x => x.Key.Country + F(x.Key.D)).ToList());
        AssertDeclines((q, _) =>
            q.GroupBy(o => new { o.Country, D = o.LocalDate }).Select(g => new { g.Key })
                .Union(q.GroupBy(o => new { o.Country, D = o.UtcDate }).Select(g => new { g.Key }))
                .AsEnumerable().Select(x => x.Key.Country + F(x.Key.D)).ToList());
    }

    [Fact]
    public void Set_operation_over_whole_composite_keys_of_the_same_kind_reads_back_with_that_kind()
        => AssertNativeMatchesEntities(DateTimeKind.Local, (q, _) =>
            q.GroupBy(o => new { o.Country, D = o.LocalDate }).Select(g => new { g.Key })
                .Concat(q.GroupBy(o => new { o.Country, D = o.Detail.LocalDate }).Select(g => new { g.Key }))
                .AsEnumerable().Select(x => x.Key.Country + F(x.Key.D)).ToList());

    // ---- computed dates over a Local-kind property decline ----

    [Fact]
    public void Computed_date_projection_over_a_local_kind_property_declines()
        => AssertDeclines((q, _) => q.Select(o => new { D = o.LocalDate.AddDays(1) }).AsEnumerable().Select(x => F(x.D)).ToList());

    [Fact]
    public void Date_component_projection_over_a_local_kind_property_declines()
        => AssertDeclines((q, _) => q.Select(o => new { D = o.LocalDate.Date }).AsEnumerable().Select(x => F(x.D)).ToList());

    // The server computes TimeOfDay from the UTC instant, where C# reads the Local-kind value's local time of day
    // (EF-459); unlike Year/Hour, this shape is new to the native path, so it declines rather than join that gap.
    [Fact]
    public void TimeOfDay_projection_over_a_local_kind_property_declines()
        => AssertDeclines((q, _) => q.Select(o => new { T = o.LocalDate.TimeOfDay }).AsEnumerable().Select(x => x.T.Ticks.ToString()).ToList());

    [Fact]
    public void Bare_TimeOfDay_projection_over_a_local_kind_property_declines()
        => AssertDeclines((q, _) => q.Select(o => o.LocalDate.TimeOfDay).AsEnumerable().Select(t => t.Ticks.ToString()).ToList());

    [Fact]
    public void TimeOfDay_projection_over_a_utc_kind_property_stays_native()
    {
        var seed = Seed();
        var native = NativeModeAssert.NativeAndParity(mode => Execute(seed, mode, (q, _) =>
            q.Select(o => o.UtcDate.TimeOfDay).AsEnumerable().Select(t => t.Ticks.ToString()).ToList()));
        Assert.Equal(Sorted([D1.TimeOfDay.Ticks.ToString(), D2.TimeOfDay.Ticks.ToString(), D3.TimeOfDay.Ticks.ToString()]), native);
    }

    [Fact]
    public void Computed_group_key_over_a_local_kind_property_declines()
        => AssertDeclines((q, _) =>
            q.GroupBy(o => o.LocalDate.AddDays(1)).Select(g => new { g.Key, C = g.Count() }).AsEnumerable()
                .Select(x => F(x.Key) + "#" + x.C).ToList());

    [Fact]
    public void Computed_group_max_over_a_local_kind_property_declines()
        => AssertDeclines((q, _) =>
            q.GroupBy(o => o.Country).Select(g => new { g.Key, M = g.Max(x => x.LocalDate.AddDays(1)) }).AsEnumerable()
                .Select(x => x.Key + F(x.M)).ToList());

    [Fact]
    public void Computed_terminal_max_over_a_local_kind_property_declines()
        => AssertDeclines((q, _) => [F(q.Max(o => o.LocalDate.AddDays(1)))]);

    [Fact]
    public void Ternary_mixing_local_and_default_kind_properties_declines()
    {
        // The driver-LINQ fallback rejects this shape itself ("IfTrue and IfFalse expressions have different
        // serializers"), so there is no parity leg: the point is only that native must not return wrong Kinds.
        var seed = Seed();
        Assert.Throws<NativeTranslationNotSupportedException>(() => Execute(seed, MongoQueryMode.NativeOnly, (q, _) =>
            q.Select(o => new { D = o.Country == "UK" ? o.LocalDate : o.PlainDate }).AsEnumerable().Select(x => F(x.D)).ToList()));
    }

    // ---- unchanged: filters, and Utc / unconfigured properties stay native ----

    [Fact]
    public void Filters_comparing_a_local_kind_property_are_unchanged()
    {
        var seed = Seed();
        var reference = Reference(seed, (q, _) =>
        [
            ..q.Where(o => o.LocalDate > Threshold).Select(o => "a" + o.Country + F(o.LocalDate)),
            ..q.Where(o => o.NLocalDate > Threshold).Select(o => "b" + o.Country + F(o.LocalDate)),
            ..q.Where(o => o.LocalDate.AddDays(1) > Threshold).Select(o => "c" + o.Country + F(o.LocalDate)),
        ]);
        Assert.Equal(["aFR" + F(D3.ToLocalTime()), "aUK" + F(D2.ToLocalTime()), "bFR" + F(D3.ToLocalTime()),
            "cFR" + F(D3.ToLocalTime()), "cUK" + F(D2.ToLocalTime())], reference);

        var native = NativeModeAssert.NativeAndParity(mode => Execute(seed, mode, (q, _) =>
        [
            ..q.Where(o => o.LocalDate > Threshold).AsEnumerable().Select(o => "a" + o.Country + F(o.LocalDate)),
            ..q.Where(o => o.NLocalDate > Threshold).AsEnumerable().Select(o => "b" + o.Country + F(o.LocalDate)),
            ..q.Where(o => o.LocalDate.AddDays(1) > Threshold).AsEnumerable().Select(o => "c" + o.Country + F(o.LocalDate)),
        ]));
        Assert.Equal(reference, native);
    }

    [Fact]
    public void Utc_kind_property_read_back_stays_native()
        => AssertNativeMatchesEntities(DateTimeKind.Utc, (q, _) =>
        [
            ..q.GroupBy(o => o.UtcDate).Select(g => new { g.Key, C = g.Count() }).AsEnumerable().Select(x => "k" + F(x.Key)),
            ..q.GroupBy(o => o.Country).Select(g => new { g.Key, M = g.Max(x => x.UtcDate) }).AsEnumerable().Select(x => "m" + x.Key + F(x.M)),
            ..q.Select(o => new { D = o.UtcDate.AddDays(1) }).AsEnumerable().Select(x => "a" + F(x.D)),
            "t" + F(q.Max(o => o.UtcDate)),
        ]);

    [Fact]
    public void Unconfigured_property_read_back_stays_native()
        => AssertNativeMatchesEntities(DateTimeKind.Utc, (q, _) =>
        [
            ..q.GroupBy(o => o.PlainDate).Select(g => new { g.Key, C = g.Count() }).AsEnumerable().Select(x => "k" + F(x.Key)),
            ..q.GroupBy(o => o.Country).Select(g => new { g.Key, M = g.Max(x => x.PlainDate) }).AsEnumerable().Select(x => "m" + x.Key + F(x.M)),
            ..q.Select(o => new { D = o.PlainDate.AddDays(1) }).AsEnumerable().Select(x => "a" + F(x.D)),
            ..q.Select(o => new { D = o.Country == "UK" ? o.UtcDate : o.PlainDate }).AsEnumerable().Select(x => "c" + F(x.D)),
            "t" + F(q.Max(o => o.PlainDate)),
        ]);
}
