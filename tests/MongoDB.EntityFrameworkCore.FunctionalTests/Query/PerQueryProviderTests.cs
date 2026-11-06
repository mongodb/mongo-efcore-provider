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
/// <c>DbSet.WithQueryProvider</c> sets the <see cref="MongoQueryMode"/> for one query, overriding the context-wide mode.
/// </summary>
/// <remarks>
/// The mode is observed through a query the native translator declines (a whole-collection value converter has no
/// per-element serializer): under <see cref="MongoQueryMode.NativeOnly"/> that throws
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

    private SingleEntityDbContext<Row> CreateContext(string name, MongoQueryMode? contextMode)
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
                    new MongoDbContextOptionsBuilder(b).UseQueryMode(contextMode.Value);
                }
            });
    }

    [Fact]
    public void Query_can_opt_in_to_NativeOnly_when_the_context_is_Native()
    {
        using var db = CreateContext(nameof(Query_can_opt_in_to_NativeOnly_when_the_context_is_Native), MongoQueryMode.Native);

        Assert.Throws<NativeTranslationNotSupportedException>(
            () => db.Entities.WithQueryProvider(MongoQueryMode.NativeOnly).Where(e => e.Ratings.Contains(Tier.Gold)).ToList());

        // Other queries on the same context are unaffected.
        Assert.Equal(2, db.Entities.Count());
    }

    [Fact]
    public void Query_can_opt_out_of_NativeOnly_when_the_context_is_NativeOnly()
    {
        using var db = CreateContext(nameof(Query_can_opt_out_of_NativeOnly_when_the_context_is_NativeOnly), MongoQueryMode.NativeOnly);

        Assert.Throws<NativeTranslationNotSupportedException>(
            () => db.Entities.Where(e => e.Ratings.Contains(Tier.Gold)).ToList());

        var ex = Record.Exception(
            () => db.Entities.WithQueryProvider(MongoQueryMode.Native).Where(e => e.Ratings.Contains(Tier.Gold)).ToList());
        Assert.IsNotType<NativeTranslationNotSupportedException>(ex);
    }

    [Fact]
    public void Query_can_opt_out_of_NativeOnly_with_DriverLinq()
    {
        using var db = CreateContext(nameof(Query_can_opt_out_of_NativeOnly_with_DriverLinq), MongoQueryMode.NativeOnly);

        var labels = db.Entities.WithQueryProvider(MongoQueryMode.DriverLinq).OrderBy(e => e.Label).Select(e => e.Label).ToList();

        Assert.Equal(["A", "B"], labels);
    }

    [Fact]
    public void Per_query_mode_applies_when_the_context_mode_is_not_configured()
    {
        using var db = CreateContext(nameof(Per_query_mode_applies_when_the_context_mode_is_not_configured), null);

        Assert.Throws<NativeTranslationNotSupportedException>(
            () => db.Entities.WithQueryProvider(MongoQueryMode.NativeOnly).Where(e => e.Ratings.Contains(Tier.Gold)).ToList());
    }

    [Fact]
    public void Compiled_query_cache_keeps_modes_apart_for_the_same_query_shape()
    {
        using var db = CreateContext(nameof(Compiled_query_cache_keeps_modes_apart_for_the_same_query_shape), MongoQueryMode.Native);

        foreach (var mode in new[] { MongoQueryMode.NativeOnly, MongoQueryMode.Native, MongoQueryMode.NativeOnly })
        {
            var ex = Record.Exception(() => db.Entities.WithQueryProvider(mode).Where(e => e.Ratings.Contains(Tier.Gold)).ToList());
            if (mode == MongoQueryMode.NativeOnly)
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
    public async Task Per_query_mode_works_for_async_queries()
    {
        using var db = CreateContext(nameof(Per_query_mode_works_for_async_queries), MongoQueryMode.Native);

        var labels = await db.Entities.WithQueryProvider(MongoQueryMode.NativeOnly).OrderBy(e => e.Label).Select(e => e.Label).ToListAsync();

        Assert.Equal(["A", "B"], labels);
    }

    [Fact]
    public void Same_mode_on_two_roots_is_allowed_but_conflicting_modes_throw()
    {
        using var db = CreateContext(nameof(Same_mode_on_two_roots_is_allowed_but_conflicting_modes_throw), MongoQueryMode.Native);

        var labels = db.Entities.WithQueryProvider(MongoQueryMode.Native)
            .Join(db.Entities.WithQueryProvider(MongoQueryMode.Native), e => e.Label, o => o.Label, (e, o) => e.Label)
            .OrderBy(l => l)
            .ToList();
        Assert.Equal(["A", "B"], labels);

        var ex = Assert.Throws<InvalidOperationException>(
            () => db.Entities.WithQueryProvider(MongoQueryMode.Native)
                .Join(db.Entities.WithQueryProvider(MongoQueryMode.NativeOnly), e => e.Label, o => o.Label, (e, o) => e.Label)
                .ToList());
        Assert.Contains("conflicting", ex.Message);
    }
}
