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
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Infrastructure;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// EF-425: an operator (<c>Distinct</c>, <c>Take</c>, <c>Reverse</c>, <c>DefaultIfEmpty</c>, <c>Concat</c>)
/// between an owned-collection <c>Select</c> and a materializing terminal must fail with an
/// <see cref="InvalidOperationException"/> naming the operator and navigation, in every mode.
/// </summary>
/// <remarks>
/// The failure fires during projection binding, before the gate reads <see cref="MongoQueryMode"/>, so
/// <c>NativeOnly</c> gets the same <see cref="InvalidOperationException"/>, not
/// <see cref="NativeTranslationNotSupportedException"/>. <c>Concat</c> already failed cleanly and is the control.
/// </remarks>
[XUnitCollection("QueryTests")]
public class Ef425InterposedCollectionOperatorTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    public class Blog
    {
        public ObjectId Id { get; set; }
        public string Title { get; set; } = "";
        public List<Post> Posts { get; set; } = [];
    }

    public class Post
    {
        // Nullable so a missing stored element field materializes rather than throws (ragged seed).
        public string? Heading { get; set; }
    }

    private static readonly Action<ModelBuilder> BlogModel = mb => mb.Entity<Blog>().OwnsMany(b => b.Posts);

    private static readonly MongoQueryMode[] AllModes =
        [MongoQueryMode.Native, MongoQueryMode.DriverLinq, MongoQueryMode.NativeOnly];

    private static SingleEntityDbContext<Blog> CreateContext(IMongoCollection<Blog> collection, MongoQueryMode mode)
        => SingleEntityDbContext.Create(
            collection,
            modelBuilderAction: BlogModel,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });

    private string UniqueCollectionName(string name)
        => TemporaryDatabaseFixtureBase.CreateCollectionName(name) + Guid.NewGuid().ToString("N")[..8];

    // "two" has a duplicate heading and "empty" an empty array, so working Distinct/DefaultIfEmpty would be
    // distinguishable from no-ops.
    private IMongoCollection<Blog> Seed(string name)
    {
        var coll = database.MongoDatabase.GetCollection<BsonDocument>(UniqueCollectionName(name));
        coll.InsertMany([
            new BsonDocument
            {
                { "_id", ObjectId.GenerateNewId() },
                { "Title", "two" },
                {
                    "Posts",
                    new BsonArray
                    {
                        new BsonDocument("Heading", "a"),
                        new BsonDocument("Heading", "b"),
                        new BsonDocument("Heading", "a")
                    }
                }
            },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Title", "empty" }, { "Posts", new BsonArray() } }
        ]);
        return database.MongoDatabase.GetCollection<Blog>(coll.CollectionNamespace.CollectionName);
    }

    /// <summary>
    /// Asserts a clean decline in every mode with a message naming the interposed operator.
    /// </summary>
    /// <remarks>
    /// The operator-name check discriminates: the old <see cref="ArgumentException"/> messages did not name it.
    /// </remarks>
    private void AssertDeclinesCleanlyInEveryMode(
        IMongoCollection<Blog> collection,
        string operatorName,
        Func<IQueryable<Blog>, IQueryable<object>> query)
    {
        foreach (var mode in AllModes)
        {
            using var db = CreateContext(collection, mode);

            var ex = Assert.Throws<InvalidOperationException>(() => query(db.Entities.AsNoTracking()).ToList());

            Assert.Contains("could not be translated", ex.Message);
            Assert.Contains(operatorName, ex.Message);
        }
    }

    [Fact]
    public void Interposed_Distinct_declines_cleanly_in_every_mode()
    {
        var collection = Seed(nameof(Interposed_Distinct_declines_cleanly_in_every_mode));

        AssertDeclinesCleanlyInEveryMode(
            collection,
            "Distinct",
            q => q.Select(b => b.Posts.Select(p => p.Heading).Distinct().ToList()).Cast<object>());
    }

    [Fact]
    public void Interposed_Take_declines_cleanly_in_every_mode()
    {
        // EF pushes the projection inside Take.
        var collection = Seed(nameof(Interposed_Take_declines_cleanly_in_every_mode));

        AssertDeclinesCleanlyInEveryMode(
            collection,
            "Take",
            q => q.Select(b => b.Posts.Select(p => p.Heading).Take(2).ToList()).Cast<object>());
    }

    [Fact]
    public void Interposed_Reverse_declines_cleanly_in_every_mode()
    {
        var collection = Seed(nameof(Interposed_Reverse_declines_cleanly_in_every_mode));

        AssertDeclinesCleanlyInEveryMode(
            collection,
            "Reverse",
            q => q.Select(b => b.Posts.Select(p => p.Heading).Reverse().ToList()).Cast<object>());
    }

    [Fact]
    public void Interposed_DefaultIfEmpty_declines_cleanly_in_every_mode()
    {
        var collection = Seed(nameof(Interposed_DefaultIfEmpty_declines_cleanly_in_every_mode));

        AssertDeclinesCleanlyInEveryMode(
            collection,
            "DefaultIfEmpty",
            q => q.Select(b => b.Posts.Select(p => p.Heading).DefaultIfEmpty().ToList()).Cast<object>());
    }

    [Fact]
    public void Interposed_Concat_declines_cleanly_in_every_mode()
    {
        // Control: already declined cleanly naming Concat; pins that the guard didn't change it.
        var collection = Seed(nameof(Interposed_Concat_declines_cleanly_in_every_mode));

        AssertDeclinesCleanlyInEveryMode(
            collection,
            "Concat",
            q => q.Select(b => b.Posts.Select(p => p.Heading).Concat(new[] {"z"}).ToList()).Cast<object>());
    }

    [Fact]
    public void Owned_collection_Select_with_no_interposed_operator_still_works()
    {
        // Regression control asserting data: the same Select/ToList without an interposed operator. An "idempotent Add"
        // fix would pass this while the shapes above silently returned wrong data.
        var collection = Seed(nameof(Owned_collection_Select_with_no_interposed_operator_still_works));

        foreach (var mode in new[] {MongoQueryMode.Native, MongoQueryMode.DriverLinq})
        {
            using var db = CreateContext(collection, mode);

            var rows = db.Entities.AsNoTracking()
                .OrderBy(b => b.Title)
                .Select(b => b.Posts.Select(p => p.Heading).ToList())
                .ToList();

            Assert.Equal(2, rows.Count);
            Assert.Empty(rows[0]);                                  // "empty"
            Assert.Equal(new[] { "a", "b", "a" }, rows[1].ToArray());       // "two"
        }
    }

    [Fact]
    public void Owned_collection_Select_with_no_interposed_operator_is_a_driver_linq_fallback()
    {
        // A bare projected collection body never populates Select.Projection, so NativeOnly throws the gate exception here.
        var collection = Seed(nameof(Owned_collection_Select_with_no_interposed_operator_is_a_driver_linq_fallback));

        using var db = CreateContext(collection, MongoQueryMode.NativeOnly);

        Assert.Throws<NativeTranslationNotSupportedException>(
            () => db.Entities.AsNoTracking().Select(b => b.Posts.Select(p => p.Heading).ToList()).ToList());
    }
}
