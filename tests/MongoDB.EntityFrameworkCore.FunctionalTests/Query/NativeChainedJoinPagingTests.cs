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
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Infrastructure;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// Real-database tests for paging written between two explicit joins of a chain, after a filter on the first
/// join's Inner side (<c>Join(Ords).Where(x =&gt; x.r.Total &gt; 0).OrderBy(..).Take(1).Join(Lines)</c>). The
/// filter routes later ops into <c>PostJoinOps</c>, which lower after EVERY <c>$lookup</c>/<c>$unwind</c>, so the
/// <c>$limit</c> would page the row-multiplied result of the second join. Such a shape must decline (NativeOnly
/// throws) and Native must return the driver-LINQ rows.
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeChainedJoinPagingTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    private class Owner
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public List<Ord> Ords { get; set; } = [];
    }

    private class Ord
    {
        public int Id { get; set; }
        public int OwnerId { get; set; }
        public Owner? Owner { get; set; }
        public decimal Total { get; set; }
        public List<Line> Lines { get; set; } = [];
    }

    private class Line
    {
        public int Id { get; set; }
        public int OrdId { get; set; }
        public Ord? Ord { get; set; }
        public string Sku { get; set; } = "";
    }

    // Take(1) keeps order 10 (lowest Total), which has three lines, so three owner rows. Paging after the second
    // $unwind would keep one.
    [Fact]
    public void Paging_between_joins_after_a_first_level_filter_pages_before_the_second_join()
    {
        var result = DeclinesCleanly(ctx =>
            ctx.Owners.Join(ctx.Ords, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
                .Where(x => x.r.Total > 0).OrderBy(x => x.r.Total).Take(1)
                .Join(ctx.Lines, e => e.r.Id, l => l.OrdId, (e, l) => new { e.o, e.r, l })
                .Select(x => x.o)
                .AsEnumerable().Select(o => o.Name).ToList());

        Assert.Equal(["Ann", "Ann", "Ann"], result);
    }

    // As above, with a second-level filter: order 10's two "A" lines survive.
    [Fact]
    public void Paging_between_joins_after_a_first_level_filter_then_a_second_level_filter()
    {
        var result = DeclinesCleanly(ctx =>
            ctx.Owners.Join(ctx.Ords, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
                .Where(x => x.r.Total > 0).OrderBy(x => x.r.Total).Take(1)
                .Join(ctx.Lines, e => e.r.Id, l => l.OrdId, (e, l) => new { e.o, e.r, l })
                .Where(x => x.l.Sku == "A")
                .Select(x => x.o)
                .AsEnumerable().Select(o => o.Name).ToList());

        Assert.Equal(["Ann", "Ann"], result);
    }

    // The wrapped-projection arm must decline too.
    [Fact]
    public void Paging_between_joins_after_a_first_level_filter_with_a_wrapped_projection()
    {
        var result = DeclinesCleanly(ctx =>
            ctx.Owners.Join(ctx.Ords, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
                .Where(x => x.r.Total > 0).OrderBy(x => x.r.Total).Take(1)
                .Join(ctx.Lines, e => e.r.Id, l => l.OrdId, (e, l) => new { e.o.Name, l.Sku })
                .AsEnumerable().Select(x => x.Name + ":" + x.Sku).OrderBy(s => s, StringComparer.Ordinal).ToList());

        Assert.Equal(["Ann:A", "Ann:A", "Ann:B"], result);
    }

    // Skip written between the joins: skipping order 10 leaves order 20 (one line).
    [Fact]
    public void Skip_between_joins_after_a_first_level_filter_skips_before_the_second_join()
    {
        var result = DeclinesCleanly(ctx =>
            ctx.Owners.Join(ctx.Ords, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
                .Where(x => x.r.Total > 0).OrderBy(x => x.r.Total).Skip(1)
                .Join(ctx.Lines, e => e.r.Id, l => l.OrdId, (e, l) => new { e.o, e.r, l })
                .Select(x => x.o)
                .AsEnumerable().Select(o => o.Name).ToList());

        Assert.Equal(["Bob"], result);
    }

    // Control: the same filter and paging written AFTER both joins pages the fully-joined rows, which is what
    // PostJoinOps emits, so it goes native.
    [Fact]
    public void Paging_after_both_joins_following_a_first_level_filter_goes_native()
    {
        var (owners, ords, lines) = CreateCollectionNames(nameof(Paging_after_both_joins_following_a_first_level_filter_goes_native));
        SeedData(owners, ords, lines);

        var result = NativeModeAssert.NativeAndParity(mode =>
        {
            using var ctx = new PagingDbContext(database, owners, ords, lines, mode);
            return ctx.Owners.Join(ctx.Ords, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
                .Join(ctx.Lines, e => e.r.Id, l => l.OrdId, (e, l) => new { e.o, e.r, l })
                .Where(x => x.l.Sku == "A").OrderBy(x => x.o.Id).Take(2)
                .Select(x => x.o)
                .AsEnumerable().Select(o => o.Name).ToList();
        });

        Assert.Equal(["Ann", "Ann"], result);
    }

    private List<T> DeclinesCleanly<T>(
        Func<PagingDbContext, List<T>> query, [System.Runtime.CompilerServices.CallerMemberName] string testName = "")
    {
        var (owners, ords, lines) = CreateCollectionNames(testName);
        SeedData(owners, ords, lines);

        return NativeModeAssert.DeclinesCleanly(mode =>
        {
            using var ctx = new PagingDbContext(database, owners, ords, lines, mode);
            return query(ctx);
        });
    }

    private void SeedData(string owners, string ords, string lines)
    {
        using var seed = new PagingDbContext(database, owners, ords, lines, MongoQueryMode.DriverLinq);
        seed.Owners.AddRange(new Owner { Id = 1, Name = "Ann" }, new Owner { Id = 2, Name = "Bob" });
        seed.Ords.AddRange(
            new Ord { Id = 10, OwnerId = 1, Total = 5 },
            new Ord { Id = 20, OwnerId = 2, Total = 7 });
        seed.Lines.AddRange(
            new Line { Id = 100, OrdId = 10, Sku = "A" },
            new Line { Id = 101, OrdId = 10, Sku = "A" },
            new Line { Id = 102, OrdId = 10, Sku = "B" },
            new Line { Id = 200, OrdId = 20, Sku = "A" });
        seed.SaveChanges();
    }

    private static (string Owners, string Ords, string Lines) CreateCollectionNames(string testName)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var baseName = TemporaryDatabaseFixtureBase.CreateCollectionName(testName);
        return (baseName + "W" + suffix, baseName + "O" + suffix, baseName + "L" + suffix);
    }

    private class PagingDbContext : DbContext
    {
        private readonly string _owners;
        private readonly string _ords;
        private readonly string _lines;

        public PagingDbContext(
            TemporaryDatabaseFixture database, string owners, string ords, string lines, MongoQueryMode mode)
            : base(new DbContextOptionsBuilder<PagingDbContext>()
                .UseMongoDB(database.Client, database.MongoDatabase.DatabaseNamespace.DatabaseName,
                    b => b.UseQueryMode(mode))
                .ReplaceService<IModelCacheKeyFactory, IgnoreCacheKeyFactory>()
                .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
                .Options)
        {
            _owners = owners;
            _ords = ords;
            _lines = lines;
        }

        public DbSet<Owner> Owners => Set<Owner>();
        public DbSet<Ord> Ords => Set<Ord>();
        public DbSet<Line> Lines => Set<Line>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<Owner>(b =>
            {
                b.ToCollection(_owners);
                b.HasMany(o => o.Ords).WithOne(r => r.Owner).HasForeignKey(r => r.OwnerId);
            });
            modelBuilder.Entity<Ord>(b =>
            {
                b.ToCollection(_ords);
                b.HasMany(r => r.Lines).WithOne(l => l.Ord).HasForeignKey(l => l.OrdId);
            });
            modelBuilder.Entity<Line>().ToCollection(_lines);
        }

        private sealed class IgnoreCacheKeyFactory : IModelCacheKeyFactory
        {
            private static int _count;
            public object Create(DbContext context, bool designTime) => Interlocked.Increment(ref _count);
        }
    }
}
