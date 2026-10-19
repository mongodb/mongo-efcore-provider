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

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// Composition-seam regression tests for the post-terminal invariant: a native <c>SelectMany</c> composed
/// AFTER a native terminal (<c>Union</c>/<c>Concat</c>). Before the fix, <c>TranslateSelectMany</c> set
/// <c>UnwindSource</c> without gating on <see cref="Expressions.MongoSelectDefinition.HasTerminalOperator"/>,
/// so the SelectMany's <c>UnwindSource</c> coexisted with the earlier <c>SetOperation</c> on the same select;
/// the lowerer (<c>MongoSelectLowerer.Lower</c>) selects exactly ONE terminal by fixed precedence
/// (<c>SetOperation &gt; UnwindSource &gt; Grouping &gt; Projection &gt; Cardinality</c>) and returns early, so
/// the SelectMany's <c>$unwind</c>/<c>$project</c> was SILENTLY DROPPED — the query returned whole outer rows
/// (wrong row count, or a shaper crash when a projected alias is absent at top level) under BOTH
/// <see cref="MongoQueryMode.Native"/> and <see cref="MongoQueryMode.NativeOnly"/> (Route stayed non-Fallback,
/// so NativeOnly did not even throw).
/// <para>
/// The fix adds the missing <c>HasTerminalOperator</c> guard at the top of <c>TranslateSelectMany</c>, which
/// returns <see langword="null"/> (reaching EF Core's own translation-failure path) for this shape — a clean
/// HARD-FAIL in EVERY <see cref="MongoQueryMode"/>, never silent wrong data. A graceful driver-LINQ fallback
/// is not viable here because the native SelectMany builds a by-index projection shaper the fallback cannot
/// re-read (the same shaper-rebuild limitation that makes operators composed AFTER a SelectMany hard-fail in
/// every mode — see <c>NativeSelectManyTests</c>).
/// </para>
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeCompositionSeamAuditTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    private class Owner
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
        public List<Item> Items { get; set; } = [];
    }

    private class Item
    {
        public string Name { get; set; } = "";
        public decimal Price { get; set; }
    }

    private static Owner[] SeedOwners() =>
    [
        new()
        {
            Id = ObjectId.GenerateNewId(), Name = "Alice",
            Items = [new Item { Name = "Widget", Price = 9.99m }, new Item { Name = "Gadget", Price = 19.99m }],
        },
        new() { Id = ObjectId.GenerateNewId(), Name = "Bob", Items = [new Item { Name = "Bolt", Price = 1m }] },
        new()
        {
            Id = ObjectId.GenerateNewId(), Name = "Carol",
            Items = [new Item { Name = "Thing", Price = 5m }],
        },
    ];

    private SingleEntityDbContext<Owner> CreateContext(Owner[] seed, MongoQueryMode mode, string name)
    {
        var collectionName = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + Guid.NewGuid().ToString("N")[..8];
        var collection = database.MongoDatabase.GetCollection<Owner>(collectionName);
        if (seed.Length > 0)
            collection.InsertMany(seed);

        return SingleEntityDbContext.Create(
            collection,
            modelBuilderAction: mb => mb.Entity<Owner>().OwnsMany(o => o.Items),
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });
    }

    [Fact]
    public void Union_then_SelectMany_hard_fails_cleanly_in_every_mode()
    {
        // Regression: this shape used to go native, silently drop the SelectMany, and return whole outer rows
        // (or crash the shaper) with NO exception under Native/NativeOnly. It must now hard-fail cleanly (an
        // exception, never silent wrong data) in every mode.
        var seed = SeedOwners();
        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq, MongoQueryMode.NativeOnly })
        {
            using var db = CreateContext(seed, mode, nameof(Union_then_SelectMany_hard_fails_cleanly_in_every_mode) + mode);

            Assert.ThrowsAny<Exception>(() =>
                db.Entities.Where(o => o.Name == "Alice")
                    .Union(db.Entities.Where(o => o.Name == "Carol"))
                    .SelectMany(o => o.Items.Select(i => new { o.Name, i.Price }))
                    .ToList());
        }
    }

    [Fact]
    public void Concat_then_SelectMany_hard_fails_cleanly_in_every_mode()
    {
        var seed = SeedOwners();
        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq, MongoQueryMode.NativeOnly })
        {
            using var db = CreateContext(seed, mode, nameof(Concat_then_SelectMany_hard_fails_cleanly_in_every_mode) + mode);

            Assert.ThrowsAny<Exception>(() =>
                db.Entities.Where(o => o.Name == "Alice")
                    .Concat(db.Entities.Where(o => o.Name == "Carol"))
                    .SelectMany(o => o.Items.Select(i => new { o.Name, i.Price }))
                    .ToList());
        }
    }

    [Fact]
    public void Union_then_SelectMany_projecting_only_outer_member_does_not_silently_return_wrong_rows()
    {
        // The pure silent-wrong-data manifestation of the pre-fix bug: projecting ONLY an outer member that
        // exists at top level on the Owner document did NOT crash — it silently returned one row per OWNER
        // (2: Alice, Carol) instead of one row per flattened ITEM (3). Post-fix it must not silently return a
        // result at all; it hard-fails. Guarding the negative directly (no result, or if some future change
        // makes it succeed, the row count must be correct — never the wrong 2).
        var seed = SeedOwners();
        using var db = CreateContext(seed, MongoQueryMode.Native,
            nameof(Union_then_SelectMany_projecting_only_outer_member_does_not_silently_return_wrong_rows));

        var query = db.Entities.Where(o => o.Name == "Alice")
            .Union(db.Entities.Where(o => o.Name == "Carol"))
            .SelectMany(o => o.Items.Select(i => new { o.Name }));

        List<string>? rows = null;
        try
        {
            rows = query.AsEnumerable().Select(r => r.Name).OrderBy(n => n).ToList();
        }
        catch
        {
            // Hard-fail is the accepted outcome — never the silent wrong result below.
            return;
        }

        Assert.Equal(3, rows.Count); // one per item (Alice, Alice, Carol) — MUST NOT be the pre-fix 2
    }

    [Fact]
    public void Plain_SelectMany_without_a_preceding_terminal_still_goes_native()
    {
        // Guard against over-gating: a FIRST SelectMany (no preceding terminal) must still bind natively.
        // Succeeding under NativeOnly is the "went native" signal.
        var seed = SeedOwners();
        using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(Plain_SelectMany_without_a_preceding_terminal_still_goes_native));

        var result = db.Entities
            .SelectMany(o => o.Items.Select(i => new { o.Name, i.Price }))
            .AsEnumerable()
            .OrderBy(r => r.Name).ThenBy(r => r.Price)
            .ToList();

        var expected = seed
            .SelectMany(o => o.Items.Select(i => new { o.Name, i.Price }))
            .OrderBy(r => r.Name).ThenBy(r => r.Price)
            .ToList();

        Assert.Equal(expected, result);
    }

    // ── Include (cross-collection) then owned SelectMany ─────────────────────────────────────────
    // The SelectMany guard keys on Select.HasTerminalOperator, which does NOT track cross-collection
    // $lookup (Include) state (that lives on MongoQueryExpression, not MongoSelectDefinition). So
    // Include(collection).SelectMany(owned) bypasses the guard. This asserts the combination is NOT
    // silent wrong data: EF drops the dangling Include (the result projects to a non-entity type), so
    // the SelectMany result is correct — one row per owned Tag, per Blog.

    private class Blog
    {
        public ObjectId Id { get; set; }
        public string Title { get; set; } = "";
        public List<Tag> Tags { get; set; } = [];    // owned collection (SelectMany target)
        public List<Post> Posts { get; set; } = [];  // reference collection (Include target)
    }

    private class Tag
    {
        public string Label { get; set; } = "";
    }

    private class Post
    {
        public ObjectId Id { get; set; }
        public string Heading { get; set; } = "";
        public ObjectId? BlogId { get; set; }
        public Blog? Blog { get; set; }
    }

    private sealed class BlogDbContext(TemporaryDatabaseFixture database, string blogs, string posts, MongoQueryMode mode)
        : DbContext(BuildOptions(database, mode))
    {
        public DbSet<Blog> Blogs { get; set; } = null!;
        public DbSet<Post> Posts { get; set; } = null!;

        private static DbContextOptions BuildOptions(TemporaryDatabaseFixture database, MongoQueryMode mode)
        {
            var ob = new DbContextOptionsBuilder<BlogDbContext>()
                .UseMongoDB(database.Client, database.MongoDatabase.DatabaseNamespace.DatabaseName)
                .ReplaceService<IModelCacheKeyFactory, IgnoreCacheKeyFactory>()
                .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
            new MongoDbContextOptionsBuilder(ob).UseQueryMode(mode);
            return ob.Options;
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Blog>(b =>
            {
                b.ToCollection(_blogs);
                b.OwnsMany(x => x.Tags);
                b.HasMany(x => x.Posts).WithOne(p => p.Blog).HasForeignKey(p => p.BlogId);
            });
            modelBuilder.Entity<Post>(b => b.ToCollection(_posts));
        }

        private readonly string _blogs = blogs;
        private readonly string _posts = posts;

        private sealed class IgnoreCacheKeyFactory : IModelCacheKeyFactory
        {
            private static int _count;
            public object Create(DbContext context, bool designTime) => Interlocked.Increment(ref _count);
        }
    }

    [Fact]
    public void Include_collection_then_owned_SelectMany_returns_correct_results_under_Native()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var blogsName = TemporaryDatabaseFixtureBase.CreateCollectionName("SeamBlogs") + suffix;
        var postsName = TemporaryDatabaseFixtureBase.CreateCollectionName("SeamPosts") + suffix;

        var alice = new Blog
        {
            Id = ObjectId.GenerateNewId(), Title = "Alice",
            Tags = [new Tag { Label = "x" }, new Tag { Label = "y" }],
        };
        var bob = new Blog { Id = ObjectId.GenerateNewId(), Title = "Bob", Tags = [new Tag { Label = "z" }] };

        using (var seed = new BlogDbContext(database, blogsName, postsName, MongoQueryMode.Native))
        {
            seed.Blogs.AddRange(alice, bob);
            seed.Posts.AddRange(
                new Post { Id = ObjectId.GenerateNewId(), Heading = "P1", BlogId = alice.Id },
                new Post { Id = ObjectId.GenerateNewId(), Heading = "P2", BlogId = bob.Id });
            seed.SaveChanges();
        }

        using var db = new BlogDbContext(database, blogsName, postsName, MongoQueryMode.Native);

        List<(string, string)> Run() =>
            db.Blogs.Include(b => b.Posts)
                .SelectMany(b => b.Tags.Select(t => new { b.Title, t.Label }))
                .AsEnumerable()
                .Select(r => (r.Title, r.Label))
                .OrderBy(r => r.Item1).ThenBy(r => r.Item2)
                .ToList();

        // Correct = one row per owned Tag per Blog: (Alice,x),(Alice,y),(Bob,z). The Include is dangling
        // (the query projects to an anonymous type) so EF drops it — the SelectMany result is unaffected.
        // (A clean hard-fail would also be acceptable per the audit invariant; empirically EF drops the
        // Include and this returns the correct rows.)
        Assert.Equal([("Alice", "x"), ("Alice", "y"), ("Bob", "z")], Run());
    }
}
