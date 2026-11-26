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
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Infrastructure;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// <c>DbSet.WithQueryProvider</c> sets the <see cref="MongoQueryProvider"/> for one query, overriding the context-wide mode.
/// </summary>
/// <remarks>
/// The mode is observed through a query the native translator declines (a whole-collection value converter has no
/// per-element serializer): under <see cref="MongoQueryProvider.NativeOnly"/> that throws
/// <see cref="NativeTranslationNotSupportedException"/> at translate time; under the other modes it does not.
/// </remarks>
public class PerQueryProviderTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    public enum Tier { Bronze, Silver, Gold }

    public class Row
    {
        public ObjectId Id { get; set; }
        public string Label { get; set; } = "";
        public List<Tier> Ratings { get; set; } = [];
    }

    private static readonly Action<ModelBuilder> ConvertRatings = mb => mb.Entity<Row>().Property(e => e.Ratings)
        .HasConversion(
            v => v.Select(x => x.ToString()).ToList(),
            v => v.Select(x => Enum.Parse<Tier>(x)).ToList());

    private SingleEntityDbContext<Row> CreateContext(string name, MongoQueryProvider? contextMode)
    {
        var collection = database.MongoDatabase.GetCollection<Row>(
            TemporaryDatabaseFixtureBase.CreateCollectionName(name) + Guid.NewGuid().ToString("N")[..8]);
        collection.InsertMany([new Row { Label = "A" }, new Row { Label = "B" }]);

        return SingleEntityDbContext.Create(
            collection,
            ConvertRatings,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                if (contextMode != null)
                {
                    new MongoDbContextOptionsBuilder(b).UseQueryProvider(contextMode.Value);
                }
            });
    }

    [Fact]
    public void Query_can_opt_in_to_NativeOnly_when_the_context_is_Native()
    {
        using var db = CreateContext(nameof(Query_can_opt_in_to_NativeOnly_when_the_context_is_Native), MongoQueryProvider.Native);

        Assert.Throws<NativeTranslationNotSupportedException>(
            () => db.Entities.WithQueryProvider(MongoQueryProvider.NativeOnly).Where(e => e.Ratings.Contains(Tier.Gold)).ToList());

        // Other queries on the same context are unaffected.
        Assert.Equal(2, db.Entities.Count());
    }

    [Fact]
    public void Query_can_opt_out_of_NativeOnly_when_the_context_is_NativeOnly()
    {
        using var db = CreateContext(nameof(Query_can_opt_out_of_NativeOnly_when_the_context_is_NativeOnly), MongoQueryProvider.NativeOnly);

        Assert.Throws<NativeTranslationNotSupportedException>(
            () => db.Entities.Where(e => e.Ratings.Contains(Tier.Gold)).ToList());

        var ex = Record.Exception(
            () => db.Entities.WithQueryProvider(MongoQueryProvider.Native).Where(e => e.Ratings.Contains(Tier.Gold)).ToList());
        Assert.IsNotType<NativeTranslationNotSupportedException>(ex);
    }

    [Fact]
    public void Query_can_opt_out_of_NativeOnly_with_DriverLinq()
    {
        using var db = CreateContext(nameof(Query_can_opt_out_of_NativeOnly_with_DriverLinq), MongoQueryProvider.NativeOnly);

        var labels = db.Entities.WithQueryProvider(MongoQueryProvider.DriverLinq).OrderBy(e => e.Label).Select(e => e.Label).ToList();

        Assert.Equal(["A", "B"], labels);
    }

    [Fact]
    public void Per_query_provider_applies_when_the_context_mode_is_not_configured()
    {
        using var db = CreateContext(nameof(Per_query_provider_applies_when_the_context_mode_is_not_configured), null);

        Assert.Throws<NativeTranslationNotSupportedException>(
            () => db.Entities.WithQueryProvider(MongoQueryProvider.NativeOnly).Where(e => e.Ratings.Contains(Tier.Gold)).ToList());
    }

    [Fact]
    public void Compiled_query_cache_keeps_modes_apart_for_the_same_query_shape()
    {
        using var db = CreateContext(nameof(Compiled_query_cache_keeps_modes_apart_for_the_same_query_shape), MongoQueryProvider.Native);

        foreach (var mode in new[] { MongoQueryProvider.NativeOnly, MongoQueryProvider.Native, MongoQueryProvider.NativeOnly })
        {
            var ex = Record.Exception(() => db.Entities.WithQueryProvider(mode).Where(e => e.Ratings.Contains(Tier.Gold)).ToList());
            if (mode == MongoQueryProvider.NativeOnly)
            {
                Assert.IsType<NativeTranslationNotSupportedException>(ex);
            }
            else
            {
                Assert.IsNotType<NativeTranslationNotSupportedException>(ex);
            }
        }
    }

    [Fact]
    public async Task Per_query_provider_works_for_async_queries()
    {
        using var db = CreateContext(nameof(Per_query_provider_works_for_async_queries), MongoQueryProvider.Native);

        var labels = await db.Entities.WithQueryProvider(MongoQueryProvider.NativeOnly).OrderBy(e => e.Label).Select(e => e.Label).ToListAsync();

        Assert.Equal(["A", "B"], labels);
    }

    [Fact]
    public void Same_mode_on_two_roots_is_allowed_but_conflicting_modes_throw()
    {
        using var db = CreateContext(nameof(Same_mode_on_two_roots_is_allowed_but_conflicting_modes_throw), MongoQueryProvider.Native);

        var labels = db.Entities.WithQueryProvider(MongoQueryProvider.Native)
            .Join(db.Entities.WithQueryProvider(MongoQueryProvider.Native), e => e.Label, o => o.Label, (e, o) => e.Label)
            .OrderBy(l => l)
            .ToList();
        Assert.Equal(["A", "B"], labels);

        var ex = Assert.Throws<InvalidOperationException>(
            () => db.Entities.WithQueryProvider(MongoQueryProvider.Native)
                .Join(db.Entities.WithQueryProvider(MongoQueryProvider.NativeOnly), e => e.Label, o => o.Label, (e, o) => e.Label)
                .ToList());
        Assert.Contains("conflicting", ex.Message);
    }

    [Fact]
    public void WithQueryProvider_can_be_chained_on_the_root()
    {
        using var db = CreateContext(nameof(WithQueryProvider_can_be_chained_on_the_root), MongoQueryProvider.Native);

        IRootQueryable<Row> root = db.Entities.WithQueryProvider(MongoQueryProvider.NativeOnly).WithQueryProvider(MongoQueryProvider.NativeOnly);

        Assert.Throws<NativeTranslationNotSupportedException>(() => root.Where(e => e.Ratings.Contains(Tier.Gold)).ToList());
    }

    [Fact]
    public void Conflicting_WithQueryProvider_calls_on_the_same_root_throw()
    {
        using var db = CreateContext(nameof(Conflicting_WithQueryProvider_calls_on_the_same_root_throw), MongoQueryProvider.Native);

        var ex = Assert.Throws<InvalidOperationException>(
            () => db.Entities.WithQueryProvider(MongoQueryProvider.Native).WithQueryProvider(MongoQueryProvider.NativeOnly).ToList());
        Assert.Contains("conflicting", ex.Message);
    }

    [Fact]
    public async Task Root_queryable_supports_sync_and_async_enumeration()
    {
        var collection = database.MongoDatabase.GetCollection<Row>(
            TemporaryDatabaseFixtureBase.CreateCollectionName(nameof(Root_queryable_supports_sync_and_async_enumeration)) + Guid.NewGuid().ToString("N")[..8]);
        collection.InsertMany([new Row { Label = "A" }, new Row { Label = "B" }]);
        using var db = SingleEntityDbContext.Create(
            collection,
            mb => mb.Entity<Row>().Ignore(e => e.Ratings),
            optionsBuilderAction: b => b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning)));

        var root = db.Entities.WithQueryProvider(MongoQueryProvider.NativeOnly);

        Assert.Equal(2, root.ToList().Count);
        Assert.Equal(2, (await root.ToListAsync()).Count);
        Assert.Equal(2, await root.CountAsync());
    }
}
