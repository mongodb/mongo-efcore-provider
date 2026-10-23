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
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Infrastructure;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// Exercises <see cref="NativeModeAssert.NativeAndExpected{T}"/> and <see cref="NativeModeAssert.TwiceWithDifferentValues{T}"/>
/// over a trivial model.
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeModeAssertTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    public class Item
    {
        public ObjectId Id { get; set; }
        public string Title { get; set; } = "";
        public int? Score { get; set; }
    }

    [Fact]
    public void NativeAndExpected_over_a_simple_where()
    {
        var collection = Seed(nameof(NativeAndExpected_over_a_simple_where));

        var result = NativeModeAssert.NativeAndExpected(
            mode =>
            {
                using var db = CreateContext(collection, mode);
                return db.Entities.AsNoTracking().Where(x => x.Score > 5).OrderBy(x => x.Title)
                    .Select(x => x.Title).ToList();
            },
            ["ten"]);

        Assert.Equal(["ten"], result);
    }

    [Fact]
    public void NativeAndParity_returns_the_rows_when_non_empty()
    {
        var collection = Seed(nameof(NativeAndParity_returns_the_rows_when_non_empty));

        var result = NativeModeAssert.NativeAndParity(mode => Titles(collection, mode, x => x.Score > 5));

        Assert.Equal(["ten"], result);
    }

    [Fact]
    public void NativeAndParity_fails_when_both_modes_are_empty()
    {
        var collection = Seed(nameof(NativeAndParity_fails_when_both_modes_are_empty));

        var ex = Assert.ThrowsAny<Xunit.Sdk.XunitException>(
            () => NativeModeAssert.NativeAndParity(mode => Titles(collection, mode, x => x.Score > 100)));

        Assert.Contains("allowEmpty", ex.Message);
    }

    [Fact]
    public void NativeAndParity_allows_empty_when_opted_in()
    {
        var collection = Seed(nameof(NativeAndParity_allows_empty_when_opted_in));

        var result = NativeModeAssert.NativeAndParity(
            mode => Titles(collection, mode, x => x.Score > 100), allowEmpty: true);

        Assert.Empty(result);
    }

    [Fact]
    public void NativeAndParity_still_fails_on_disagreement_even_with_allowEmpty()
    {
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(
            () => NativeModeAssert.NativeAndParity(
                mode => mode == MongoQueryMode.NativeOnly ? ["a"] : new List<string>(), allowEmpty: true));
    }

    private static List<string> Titles(
        IMongoCollection<Item> collection, MongoQueryMode mode, System.Linq.Expressions.Expression<Func<Item, bool>> predicate)
    {
        using var db = CreateContext(collection, mode);
        return db.Entities.AsNoTracking().Where(predicate).OrderBy(x => x.Title).Select(x => x.Title).ToList();
    }

    [Fact]
    public void TwiceWithDifferentValues_hits_the_cached_plan_with_new_values()
    {
        var collection = Seed(nameof(TwiceWithDifferentValues_hits_the_cached_plan_with_new_values));

        // One context for both runs: SingleEntityDbContext's IgnoreCacheKeyFactory gives every instance its own
        // model, and the compiled-query cache key includes the model, so separate instances never share a plan.
        var compilations = 0;
        using var db = SingleEntityDbContext.Create(
            collection,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(MongoQueryMode.NativeOnly);
                b.LogTo(_ => compilations++, [CoreEventId.QueryCompilationStarting]);
            });

        NativeModeAssert.TwiceWithDifferentValues(
            p =>
            {
                var value = (int?)p;
                return db.Entities.AsNoTracking().Where(x => x.Score == value).OrderBy(x => x.Title)
                    .Select(x => x.Title).ToList();
            },
            null, ["missing", "null"],
            1, ["one"]);

        Assert.Equal(1, compilations);
    }

    private IMongoCollection<Item> Seed(string name)
    {
        var collectionName = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + Guid.NewGuid().ToString("N")[..8];
        var raw = database.MongoDatabase.GetCollection<BsonDocument>(collectionName);
        raw.InsertMany(
        [
            new BsonDocument {{"_id", ObjectId.GenerateNewId()}, {"Title", "ten"}, {"Score", 10}},
            new BsonDocument {{"_id", ObjectId.GenerateNewId()}, {"Title", "one"}, {"Score", 1}},
            new BsonDocument {{"_id", ObjectId.GenerateNewId()}, {"Title", "null"}, {"Score", BsonNull.Value}},
            new BsonDocument {{"_id", ObjectId.GenerateNewId()}, {"Title", "missing"}}
        ]);

        return database.MongoDatabase.GetCollection<Item>(collectionName);
    }

    private static SingleEntityDbContext<Item> CreateContext(IMongoCollection<Item> collection, MongoQueryMode mode)
        => SingleEntityDbContext.Create(
            collection,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });
}
