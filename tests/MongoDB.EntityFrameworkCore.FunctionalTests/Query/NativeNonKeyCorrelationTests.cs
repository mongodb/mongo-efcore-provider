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
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// A correlated subquery over another <c>DbSet</c> (<c>db.Orders.Where(o =&gt; o.CustomerId == c.X)</c>) binds as
/// the collection navigation only when <c>c.X</c> is that navigation's principal key: binding emits
/// <c>$lookup {localField: principalKey, foreignField: fk}</c>, so a correlation on any other outer member would
/// silently read the key-correlated rows (F11).
/// </summary>
/// <remarks>
/// <para>
/// <c>Code</c> deliberately differs from <c>Id</c> (and <c>Order.Ref</c> from <c>Order.Id</c>), so a key-bound
/// result is distinguishable from the correct one. Seeds are raw <see cref="BsonDocument"/> inserts.
/// </para>
/// <para>
/// Canonical <c>main</c> (dec7e26f, EF8/EF9/EF10, built and run) throws <see cref="InvalidOperationException"/> for
/// every shape in <see cref="DecliningShapes"/>, key-correlated or not ("could not be translated" for the
/// <c>SelectMany</c> and reducer shapes, "Unsupported cross-DbSet query" for the count predicates), and returns the
/// key-correlated counts for the bare projected count in both spellings (M35).
/// </para>
/// </remarks>
[XUnitCollection("QueryTests")]
public class NativeNonKeyCorrelationTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    private class Customer
    {
        public int Id { get; set; }
        public int Code { get; set; }
        public List<Order> Orders { get; set; } = null!;
    }

    private class Order
    {
        public int Id { get; set; }
        public int CustomerId { get; set; }
        public int Ref { get; set; }
        public string? Note { get; set; }
        public List<Line> Lines { get; set; } = null!;
    }

    private class Line
    {
        public int Id { get; set; }
        public int OrderId { get; set; }
    }

    private class NCustomer
    {
        public int Id { get; set; }
        public int Code { get; set; }
        public List<NOrder> Orders { get; set; } = null!;
    }

    private class NOrder
    {
        public int Id { get; set; }
        public int? CustomerId { get; set; }
    }

    // Alternate principal key: Orders.CustomerCode references Customer.Code (HasPrincipalKey), not Id.
    private class ACustomer
    {
        public int Id { get; set; }
        public int Code { get; set; }
        public List<AOrder> Orders { get; set; } = null!;
    }

    private class AOrder
    {
        public int Id { get; set; }
        public int CustomerCode { get; set; }
    }

    // Composite alternate principal key (Code, Region).
    private class KCustomer
    {
        public int Id { get; set; }
        public int Code { get; set; }
        public int Region { get; set; }
        public List<KOrder> Orders { get; set; } = null!;
    }

    private class KOrder
    {
        public int Id { get; set; }
        public int CustomerCode { get; set; }
        public int CustomerRegion { get; set; }
    }

    /// <summary>
    /// Correlations that must not bind the navigation. All but the <c>NoteGuard*</c> shapes correlate on a non-key
    /// outer member when <c>key</c> is false; the <c>NoteGuard*</c> shapes are key-correlated but guarded by a null
    /// check on an inner member, a user filter the bound navigation would drop (they ignore <c>key</c>).
    /// </summary>
    public static TheoryData<string> DecliningShapes =>
    [
        "SelectMany", "SelectManyReversed", "SelectManyConjunct", "FirstOrDefault", "WhereCount", "Nested",
        "NullableSelectMany", "NullableFirstOrDefault", "NullableWhereCount", "NoteGuardFirstOrDefault",
        "NoteGuardWhereCount"
    ];

    /// <summary>
    /// The count-predicate shapes reach the gate, so NativeOnly refuses them with
    /// <see cref="NativeTranslationNotSupportedException"/>. The others (reference <c>SelectMany</c>, correlated
    /// reducer) have no driver-LINQ oracle and fail EF translation in every mode once the recognizer declines.
    /// </summary>
    private static bool IsCountPredicateShape(string shape)
        => shape is "WhereCount" or "NullableWhereCount" or "NoteGuardWhereCount";

    /// <summary>The failure canonical <c>main</c> reports for <paramref name="shape"/> (EF8/EF9/EF10).</summary>
    private static string MainFailureMessage(string shape)
        => IsCountPredicateShape(shape) ? "Unsupported cross-DbSet query" : "could not be translated";

    [Theory, MemberData(nameof(DecliningShapes))]
    public void Non_key_correlation_declines_in_NativeOnly(string shape)
    {
        using var db = CreateContext(MongoQueryMode.NativeOnly);
        if (IsCountPredicateShape(shape))
        {
            Assert.Throws<NativeTranslationNotSupportedException>(() => Run(db, shape, key: false));
        }
        else
        {
            var ex = Assert.Throws<InvalidOperationException>(() => Run(db, shape, key: false));
            Assert.Contains("could not be translated", ex.Message);
        }
    }

    [Theory, MemberData(nameof(DecliningShapes))]
    public void Non_key_correlation_in_Native_matches_main(string shape)
    {
        // Native falls back and fails as main does.
        using var db = CreateContext(MongoQueryMode.Native);
        var ex = Assert.Throws<InvalidOperationException>(() => Run(db, shape, key: false));
        Assert.Contains(MainFailureMessage(shape), ex.Message);
    }

    [Theory, MemberData(nameof(DecliningShapes))]
    public void Non_key_correlation_in_DriverLinq_matches_main(string shape)
    {
        // The count-predicate shapes were answered (wrongly) here too: the shared slot populator registers the count
        // $lookup through the same matcher before routing.
        using var db = CreateContext(MongoQueryMode.DriverLinq);
        var ex = Assert.Throws<InvalidOperationException>(() => Run(db, shape, key: false));
        Assert.Contains(MainFailureMessage(shape), ex.Message);
    }

    [Theory]
    [InlineData(MongoQueryMode.NativeOnly)]
    [InlineData(MongoQueryMode.Native)]
    public void Key_correlation_stays_native_and_correct(MongoQueryMode mode)
    {
        using var db = CreateContext(mode);
        Assert.Equal(["1-10", "1-11", "1-12", "2-20"], Run(db, "SelectMany", key: true));
        Assert.Equal(["1-10", "1-11", "1-12", "2-20"], Run(db, "SelectManyReversed", key: true));
        Assert.Equal(["1-11", "1-12", "2-20"], Run(db, "SelectManyConjunct", key: true));
        Assert.Equal(["1:10", "2:20", "3:0"], Run(db, "FirstOrDefault", key: true));
        Assert.Equal(["1", "2"], Run(db, "WhereCount", key: true));
        Assert.Equal(["1-10-500", "1-10-501", "1-11-503", "2-20-502"], Run(db, "Nested", key: true));
        // Nullable FK through the navigation: EF's nav-expansion adds `outerKey != null &&` on the principal key.
        Assert.Equal(["1-10", "1-11", "1-12", "2-20"], Run(db, "NullableSelectMany", key: true));
        Assert.Equal(["1:10", "2:20", "3:0"], Run(db, "NullableFirstOrDefault", key: true));
        Assert.Equal(["1", "2"], Run(db, "NullableWhereCount", key: true));
    }

    [Fact]
    public void Key_correlated_count_predicate_still_reads_key_rows_under_DriverLinq()
    {
        // Branch-only: main throws "Unsupported cross-DbSet query" here; the shared slot populator answers it.
        using var db = CreateContext(MongoQueryMode.DriverLinq);
        Assert.Equal(["1", "2"], Run(db, "WhereCount", key: true));
        Assert.Equal(["1", "2"], Run(db, "NullableWhereCount", key: true));
    }

    [Fact]
    public void Bare_projected_non_key_count_declines_natively_and_falls_back_to_main_behavior()
    {
        using (var db = CreateContext(MongoQueryMode.NativeOnly))
        {
            Assert.Throws<NativeTranslationNotSupportedException>(() => Run(db, "BareCount", key: false));
        }

        // M35: main and DriverLinq return the key-correlated counts [3, 1, 0] (correct: [1, 0, 3]). Parity with main,
        // not correctness; flip this when M35 is fixed on main.
        using var native = CreateContext(MongoQueryMode.Native);
        using var driver = CreateContext(MongoQueryMode.DriverLinq);
        Assert.Equal(["3", "1", "0"], Run(driver, "BareCount", key: false));
        Assert.Equal(Run(driver, "BareCount", key: false), Run(native, "BareCount", key: false));
    }

    [Theory]
    [InlineData(MongoQueryMode.NativeOnly)]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.DriverLinq)]
    public void Bare_projected_key_count_is_correct(MongoQueryMode mode)
    {
        using var db = CreateContext(mode);
        Assert.Equal(["3", "1", "0"], Run(db, "BareCount", key: true));
    }

    /// <summary>
    /// An alternate principal key (<c>HasPrincipalKey(c =&gt; c.Code)</c>): a correlation on <c>c.Code</c> is the
    /// navigation's principal key, so it binds natively and reads the Code-correlated rows (a key-by-Id binding would
    /// read <c>1-12, 2-10, 2-11, 3-20</c>). Main throws for every spelling (see the class remarks), so no DriverLinq leg.
    /// </summary>
    [Theory]
    [InlineData(MongoQueryMode.NativeOnly)]
    [InlineData(MongoQueryMode.Native)]
    public void Alternate_principal_key_correlation_binds_natively_and_correctly(MongoQueryMode mode)
    {
        using var db = CreateContext(mode);
        Assert.Equal(["1-10", "1-11", "2-20", "3-12"], Format(
            from c in db.ACustomers
            from o in db.AOrders.Where(o => o.CustomerCode == c.Code)
            select new { C = c.Id, O = o.Id },
            a => $"{a.C}-{a.O}"));
        Assert.Equal(["1:10", "2:20", "3:12", "4:0"], Format(
            db.ACustomers.Select(c => new
            {
                c.Id, F = db.AOrders.Where(o => o.CustomerCode == c.Code).OrderBy(o => o.Id).Select(o => o.Id).FirstOrDefault()
            }),
            a => $"{a.Id}:{a.F}"));
        Assert.Equal(["1", "2", "3"], Format(
            db.ACustomers.Where(c => db.AOrders.Where(o => o.CustomerCode == c.Code).Count() > 0).Select(c => c.Id),
            i => i.ToString()));
    }

    [Fact]
    public void Alternate_principal_key_entity_correlated_on_Id_declines()
    {
        // Id is not the navigation's principal key here: binding would read the Code-correlated rows.
        using var db = CreateContext(MongoQueryMode.NativeOnly);
        Assert.Contains("could not be translated", Assert.Throws<InvalidOperationException>(() => Format(
            from c in db.ACustomers
            from o in db.AOrders.Where(o => o.CustomerCode == c.Id)
            select new { C = c.Id, O = o.Id },
            a => $"{a.C}-{a.O}")).Message);
    }

    [Fact]
    public void Composite_principal_key_correlation_declines()
    {
        // NativeCorrelationMatcher proves a single principal-key property only; a composite key declines (and, with no
        // driver-LINQ oracle for the reference SelectMany, fails translation, as on main).
        using var db = CreateContext(MongoQueryMode.NativeOnly);
        Assert.Contains("could not be translated", Assert.Throws<InvalidOperationException>(() => Format(
            from c in db.KCustomers
            from o in db.KOrders.Where(o => o.CustomerCode == c.Code && o.CustomerRegion == c.Region)
            select new { C = c.Id, O = o.Id },
            a => $"{a.C}-{a.O}")).Message);
    }

    // The `(int?)c.Code != null` guards are deliberate: they model a user-written guard on a nullable key.
#pragma warning disable CS0472
    private static List<string> Run(Ctx db, string shape, bool key)
        => shape switch
        {
            "SelectMany" => Format(
                key
                    ? from c in db.Customers
                      from o in db.Orders.Where(o => o.CustomerId == c.Id)
                      select new { C = c.Id, O = o.Id }
                    : from c in db.Customers
                      from o in db.Orders.Where(o => o.CustomerId == c.Code)
                      select new { C = c.Id, O = o.Id },
                a => $"{a.C}-{a.O}"),
            "SelectManyReversed" => Format(
                key
                    ? from c in db.Customers
                      from o in db.Orders.Where(o => c.Id == o.CustomerId)
                      select new { C = c.Id, O = o.Id }
                    : from c in db.Customers
                      from o in db.Orders.Where(o => c.Code == o.CustomerId)
                      select new { C = c.Id, O = o.Id },
                a => $"{a.C}-{a.O}"),
            "SelectManyConjunct" => Format(
                key
                    ? from c in db.Customers
                      from o in db.Orders.Where(o => o.CustomerId == c.Id && o.Id > 10)
                      select new { C = c.Id, O = o.Id }
                    : from c in db.Customers
                      from o in db.Orders.Where(o => o.CustomerId == c.Code && o.Id > 10)
                      select new { C = c.Id, O = o.Id },
                a => $"{a.C}-{a.O}"),
            "FirstOrDefault" => Format(
                key
                    ? db.Customers.Select(c => new
                    {
                        c.Id, F = db.Orders.Where(o => o.CustomerId == c.Id).OrderBy(o => o.Id).Select(o => o.Id).FirstOrDefault()
                    })
                    : db.Customers.Select(c => new
                    {
                        c.Id, F = db.Orders.Where(o => o.CustomerId == c.Code).OrderBy(o => o.Id).Select(o => o.Id).FirstOrDefault()
                    }),
                a => $"{a.Id}:{a.F}"),
            "WhereCount" => Format(
                key
                    ? db.Customers.Where(c => db.Orders.Where(o => o.CustomerId == c.Id).Count() > 0).Select(c => c.Id)
                    : db.Customers.Where(c => db.Orders.Where(o => o.CustomerId == c.Code).Count() > 0).Select(c => c.Id),
                i => i.ToString()),
            // Row order matters here (M35 compares per-customer counts), so not re-sorted.
            "BareCount" => (key
                    ? db.Customers.OrderBy(c => c.Id).Select(c => db.Orders.Where(o => o.CustomerId == c.Id).Count())
                    : db.Customers.OrderBy(c => c.Id).Select(c => db.Orders.Where(o => o.CustomerId == c.Code).Count()))
                .ToList().Select(n => n.ToString()).ToList(),
            // Level 1 is key-correlated; level 2 correlates on the level-1 key o.Id, or the non-key o.Ref (exercises
            // the synthetic level-1 parameter of NativeSelectManyBinder.TryBindNestedReferenceNavUnwind).
            "Nested" => Format(
                key
                    ? from c in db.Customers
                      from o in db.Orders.Where(o => o.CustomerId == c.Id)
                      from l in db.Lines.Where(l => l.OrderId == o.Id)
                      select new { C = c.Id, O = o.Id, L = l.Id }
                    : from c in db.Customers
                      from o in db.Orders.Where(o => o.CustomerId == c.Id)
                      from l in db.Lines.Where(l => l.OrderId == o.Ref)
                      select new { C = c.Id, O = o.Id, L = l.Id },
                a => $"{a.C}-{a.O}-{a.L}"),
            // Nullable FK. key: through the navigation (EF's own principal-key null guard). Non-key: a user-written
            // guarded correlation on the non-key Code, which the guard check alone must not admit.
            "NullableSelectMany" => Format(
                key
                    ? from c in db.NCustomers
                      from o in c.Orders
                      select new { C = c.Id, O = o.Id }
                    : from c in db.NCustomers
                      from o in db.NOrders.Where(o => (int?)c.Code != null && o.CustomerId == c.Code)
                      select new { C = c.Id, O = o.Id },
                a => $"{a.C}-{a.O}"),
            "NullableFirstOrDefault" => Format(
                key
                    ? db.NCustomers.Select(c => new { c.Id, F = c.Orders.OrderBy(o => o.Id).Select(o => o.Id).FirstOrDefault() })
                    : db.NCustomers.Select(c => new
                    {
                        c.Id,
                        F = db.NOrders.Where(o => (int?)c.Code != null && o.CustomerId == c.Code)
                            .OrderBy(o => o.Id).Select(o => o.Id).FirstOrDefault()
                    }),
                a => $"{a.Id}:{a.F}"),
            "NullableWhereCount" => Format(
                key
                    ? db.NCustomers.Where(c => c.Orders.Count() > 0).Select(c => c.Id)
                    : db.NCustomers.Where(c => db.NOrders.Where(o => (int?)c.Code != null && o.CustomerId == c.Code).Count() > 0)
                        .Select(c => c.Id),
                i => i.ToString()),
            // Correct: [1:10, 2:0, 3:0] (order 20 has a null Note). Binding the navigation drops the Note filter.
            "NoteGuardFirstOrDefault" => Format(
                db.Customers.Select(c => new
                {
                    c.Id,
                    F = db.Orders.Where(o => o.Note != null && o.CustomerId == c.Id).OrderBy(o => o.Id).Select(o => o.Id).FirstOrDefault()
                }),
                a => $"{a.Id}:{a.F}"),
            // Correct: [1]. Binding the navigation drops the Note filter and reads [1, 2].
            "NoteGuardWhereCount" => Format(
                db.Customers.Where(c => db.Orders.Where(o => o.Note != null && o.CustomerId == c.Id).Count() > 0).Select(c => c.Id),
                i => i.ToString()),
            _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, null)
        };
#pragma warning restore CS0472

    private static List<string> Format<T>(IQueryable<T> query, Func<T, string> format)
        => query.ToList().Select(format).OrderBy(s => s, StringComparer.Ordinal).ToList();

    private Ctx CreateContext(MongoQueryMode mode)
    {
        var prefix = TemporaryDatabaseFixtureBase.CreateCollectionName("F11") + Guid.NewGuid().ToString("N")[..8];
        Seed(prefix);
        return new Ctx(
            new DbContextOptionsBuilder<Ctx>()
                .UseMongoDB(database.Client, database.MongoDatabase.DatabaseNamespace.DatabaseName, b => b.UseQueryMode(mode))
                .ReplaceService<IModelCacheKeyFactory, IgnoreCacheKeyFactory>()
                .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
                .Options,
            prefix);
    }

    private void Seed(string prefix)
    {
        BsonDocument[] customers =
        [
            new() { { "_id", 1 }, { "Code", 2 } },
            new() { { "_id", 2 }, { "Code", 3 } },
            new() { { "_id", 3 }, { "Code", 1 } }
        ];
        database.MongoDatabase.GetCollection<BsonDocument>(prefix + "c").InsertMany(customers);
        // Note: populated, missing, explicit null, explicit null.
        database.MongoDatabase.GetCollection<BsonDocument>(prefix + "o").InsertMany(
        [
            new() { { "_id", 10 }, { "CustomerId", 1 }, { "Ref", 20 }, { "Note", "a" } },
            new() { { "_id", 11 }, { "CustomerId", 1 }, { "Ref", 10 } },
            new() { { "_id", 12 }, { "CustomerId", 1 }, { "Ref", 99 }, { "Note", BsonNull.Value } },
            new() { { "_id", 20 }, { "CustomerId", 2 }, { "Ref", 11 }, { "Note", BsonNull.Value } }
        ]);
        database.MongoDatabase.GetCollection<BsonDocument>(prefix + "l").InsertMany(
        [
            new() { { "_id", 500 }, { "OrderId", 10 } },
            new() { { "_id", 501 }, { "OrderId", 10 } },
            new() { { "_id", 502 }, { "OrderId", 20 } },
            new() { { "_id", 503 }, { "OrderId", 11 } }
        ]);
        database.MongoDatabase.GetCollection<BsonDocument>(prefix + "nc").InsertMany(customers.Select(c => c.DeepClone().AsBsonDocument));
        // CustomerId: populated, explicit null, missing.
        database.MongoDatabase.GetCollection<BsonDocument>(prefix + "no").InsertMany(
        [
            new() { { "_id", 10 }, { "CustomerId", 1 } },
            new() { { "_id", 11 }, { "CustomerId", 1 } },
            new() { { "_id", 12 }, { "CustomerId", 1 } },
            new() { { "_id", 20 }, { "CustomerId", 2 } },
            new() { { "_id", 30 }, { "CustomerId", BsonNull.Value } },
            new() { { "_id", 31 } }
        ]);
        database.MongoDatabase.GetCollection<BsonDocument>(prefix + "ac").InsertMany(
        [
            new() { { "_id", 1 }, { "Code", 2 } },
            new() { { "_id", 2 }, { "Code", 3 } },
            new() { { "_id", 3 }, { "Code", 1 } },
            new() { { "_id", 4 }, { "Code", 9 } }
        ]);
        database.MongoDatabase.GetCollection<BsonDocument>(prefix + "ao").InsertMany(
        [
            new() { { "_id", 10 }, { "CustomerCode", 2 } },
            new() { { "_id", 11 }, { "CustomerCode", 2 } },
            new() { { "_id", 12 }, { "CustomerCode", 1 } },
            new() { { "_id", 20 }, { "CustomerCode", 3 } }
        ]);
        database.MongoDatabase.GetCollection<BsonDocument>(prefix + "kc").InsertMany(
        [
            new() { { "_id", 1 }, { "Code", 2 }, { "Region", 1 } },
            new() { { "_id", 2 }, { "Code", 2 }, { "Region", 2 } }
        ]);
        database.MongoDatabase.GetCollection<BsonDocument>(prefix + "ko").InsertMany(
        [
            new() { { "_id", 10 }, { "CustomerCode", 2 }, { "CustomerRegion", 1 } },
            new() { { "_id", 20 }, { "CustomerCode", 2 }, { "CustomerRegion", 2 } }
        ]);
    }

    private class Ctx(DbContextOptions options, string prefix) : DbContext(options)
    {
        public DbSet<Customer> Customers { get; set; } = null!;
        public DbSet<Order> Orders { get; set; } = null!;
        public DbSet<Line> Lines { get; set; } = null!;
        public DbSet<NCustomer> NCustomers { get; set; } = null!;
        public DbSet<NOrder> NOrders { get; set; } = null!;
        public DbSet<ACustomer> ACustomers { get; set; } = null!;
        public DbSet<AOrder> AOrders { get; set; } = null!;
        public DbSet<KCustomer> KCustomers { get; set; } = null!;
        public DbSet<KOrder> KOrders { get; set; } = null!;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Customer>().ToCollection(prefix + "c");
            modelBuilder.Entity<Order>().ToCollection(prefix + "o");
            modelBuilder.Entity<Line>().ToCollection(prefix + "l");
            modelBuilder.Entity<NCustomer>().ToCollection(prefix + "nc");
            modelBuilder.Entity<NOrder>().ToCollection(prefix + "no");
            modelBuilder.Entity<Customer>().HasMany(c => c.Orders).WithOne().HasForeignKey(o => o.CustomerId);
            modelBuilder.Entity<Order>().HasMany(o => o.Lines).WithOne().HasForeignKey(l => l.OrderId);
            modelBuilder.Entity<NCustomer>().HasMany(c => c.Orders).WithOne().HasForeignKey(o => o.CustomerId);
            modelBuilder.Entity<ACustomer>().ToCollection(prefix + "ac");
            modelBuilder.Entity<AOrder>().ToCollection(prefix + "ao");
            modelBuilder.Entity<KCustomer>().ToCollection(prefix + "kc");
            modelBuilder.Entity<KOrder>().ToCollection(prefix + "ko");
            modelBuilder.Entity<ACustomer>().HasMany(c => c.Orders).WithOne()
                .HasForeignKey(o => o.CustomerCode).HasPrincipalKey(c => c.Code);
            modelBuilder.Entity<KCustomer>().HasMany(c => c.Orders).WithOne()
                .HasForeignKey(o => new { o.CustomerCode, o.CustomerRegion }).HasPrincipalKey(c => new { c.Code, c.Region });
        }
    }
}
