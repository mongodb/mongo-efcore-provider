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

using System.Collections.Generic;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Infrastructure;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// A no-tracking Include marks the navigation loaded through the entity's <see cref="ILazyLoader"/> (EF's
/// <c>SetIsLoadedWhenNoTracking</c>). The fix-up skips that call for a navigation whose entity hierarchy has no
/// <see cref="ILazyLoader"/> service property, because it is then a no-op that only allocates; these tests pin that an entity that
/// does have one is still told, for both a reference and a collection Include, and that an entity without one is unaffected.
/// </summary>
public class NativeNoTrackingLazyLoaderTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    private class LazyParent
    {
        private readonly ILazyLoader _lazyLoader;

        public LazyParent(ILazyLoader lazyLoader) => _lazyLoader = lazyLoader;

        public int Id { get; set; }
        public int ChildId { get; set; }
        public LazyChild? Child { get; set; }
        public List<LazyItem> Items { get; set; } = [];

        public bool IsChildLoaded => _lazyLoader.IsLoaded(this, nameof(Child));
        public bool IsItemsLoaded => _lazyLoader.IsLoaded(this, nameof(Items));
    }

    private class LazyChild
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
    }

    private class LazyItem
    {
        public int Id { get; set; }
        public int LazyParentId { get; set; }
    }

    private class PlainParent
    {
        public int Id { get; set; }
        public int ChildId { get; set; }
        public LazyChild? Child { get; set; }
    }

    [Fact]
    public void A_no_tracking_reference_include_tells_the_lazy_loader_the_navigation_is_loaded()
    {
        using var db = CreateSeededContext(nameof(A_no_tracking_reference_include_tells_the_lazy_loader_the_navigation_is_loaded));

        var withInclude = db.Parents.AsNoTracking().Include(p => p.Child).Single();
        var withoutInclude = db.Parents.AsNoTracking().Single();

        Assert.Equal("child", withInclude.Child!.Name);
        Assert.True(withInclude.IsChildLoaded);
        Assert.False(withoutInclude.IsChildLoaded);
    }

    [Fact]
    public void A_no_tracking_collection_include_tells_the_lazy_loader_the_navigation_is_loaded()
    {
        using var db = CreateSeededContext(nameof(A_no_tracking_collection_include_tells_the_lazy_loader_the_navigation_is_loaded));

        var withInclude = db.Parents.AsNoTracking().Include(p => p.Items).Single();
        var withoutInclude = db.Parents.AsNoTracking().Single();

        Assert.Equal(2, withInclude.Items.Count);
        Assert.True(withInclude.IsItemsLoaded);
        Assert.False(withoutInclude.IsItemsLoaded);
    }

    [Fact]
    public void A_no_tracking_include_of_an_entity_without_a_lazy_loader_still_loads_the_navigation()
    {
        using var db = CreateSeededContext(nameof(A_no_tracking_include_of_an_entity_without_a_lazy_loader_still_loads_the_navigation));

        var plain = db.PlainParents.AsNoTracking().Include(p => p.Child).Single();

        Assert.Equal("child", plain.Child!.Name);
    }

    private LazyDbContext CreateSeededContext(string name)
    {
        var suffix = name.Length > 40 ? name[..40] : name;
        var db = new LazyDbContext(database, suffix);
        db.Database.EnsureDeleted();
        db.Children.Add(new LazyChild { Id = 1, Name = "child" });
        db.Parents.Add(new LazyParent(null!) { Id = 1, ChildId = 1 });
        db.PlainParents.Add(new PlainParent { Id = 1, ChildId = 1 });
        db.Items.AddRange(new LazyItem { Id = 1, LazyParentId = 1 }, new LazyItem { Id = 2, LazyParentId = 1 });
        db.SaveChanges();
        db.ChangeTracker.Clear();
        return db;
    }

    private class LazyDbContext : DbContext
    {
        private readonly string _suffix;

        public LazyDbContext(TemporaryDatabaseFixture database, string suffix)
            : base(new DbContextOptionsBuilder<LazyDbContext>()
                .UseMongoDB(database.Client, database.MongoDatabase.DatabaseNamespace.DatabaseName)
                .ReplaceService<IModelCacheKeyFactory, IgnoreCacheKeyFactory>()
                .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
                .Options)
            => _suffix = suffix;

        public DbSet<LazyParent> Parents => Set<LazyParent>();
        public DbSet<PlainParent> PlainParents => Set<PlainParent>();
        public DbSet<LazyChild> Children => Set<LazyChild>();
        public DbSet<LazyItem> Items => Set<LazyItem>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<LazyChild>().ToCollection("lz_children_" + _suffix);
            modelBuilder.Entity<LazyItem>().ToCollection("lz_items_" + _suffix);
            modelBuilder.Entity<LazyParent>(b =>
            {
                b.ToCollection("lz_parents_" + _suffix);
                b.HasOne(p => p.Child).WithMany().HasForeignKey(p => p.ChildId);
                b.HasMany(p => p.Items).WithOne().HasForeignKey(i => i.LazyParentId);
            });
            modelBuilder.Entity<PlainParent>(b =>
            {
                b.ToCollection("lz_plain_" + _suffix);
                b.HasOne(p => p.Child).WithMany().HasForeignKey(p => p.ChildId);
            });
        }
    }
}
