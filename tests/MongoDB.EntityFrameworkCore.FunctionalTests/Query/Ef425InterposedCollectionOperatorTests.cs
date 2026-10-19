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
/// EF-425: an operator interposed between an owned-collection <c>Select</c> and a terminal (materializing)
/// operator — <c>Distinct</c>, <c>Take</c>, <c>Reverse</c>, <c>DefaultIfEmpty</c>, <c>Concat</c> — used to
/// CRASH at translation time in every query mode with a bare, unnamed <see cref="ArgumentException"/> rather
/// than declining. This file pins the post-fix disposition: a clean
/// <see cref="InvalidOperationException"/> naming the operator and the navigation, in every mode.
/// </summary>
/// <remarks>
/// <b>The ticket's "one root cause" framing is only half right (measured, see the probe results recorded in
/// each test below).</b> The five operators crashed by THREE different routes, not one:
/// <list type="bullet">
/// <item><c>Distinct</c> and <c>DefaultIfEmpty</c> hit the duplicate-key
/// <c>_collectionShaperMapping.Add</c> the ticket describes — they cannot be pushed inside the projection, so
/// the source they see is the rebuilt <c>Enumerable.Select</c> and the generic fall-through Visits that
/// subtree a SECOND time.</item>
/// <item><c>Take</c> and <c>Reverse</c> never reached that <c>.Add</c> at all. EF Core pushes the projection
/// INSIDE them (the tree is <c>Select(Take(Posts, 2), p =&gt; p.Heading)</c>, note the element type is
/// <c>Post</c>, not <c>string</c>), so their source is the raw collection shaper and they crashed one step
/// earlier, in <c>methodCallExpression.Update</c>'s own BCL assignability check:
/// <c>ArgumentException: Expression of type 'List&lt;Post&gt;' cannot be used for parameter of type
/// 'IQueryable&lt;Post&gt;'</c>.</item>
/// <item><c>Concat</c> was never broken. It already produced exactly the clean, named
/// <c>InvalidOperationException</c> this fix delivers for the other four; it is kept here as the shape the
/// other four are being brought INTO LINE WITH, and as a pin that the fix did not disturb it.</item>
/// </list>
/// Both live crash routes are downstream of one structural fact — the generic fall-through at the bottom of
/// <c>MongoProjectionBindingExpressionVisitor.VisitMethodCall</c> re-Visits and rebuilds a <c>Queryable</c>
/// call whose source is no longer an <c>IQueryable&lt;T&gt;</c> — so the fix is one assignability guard, not
/// five per-operator cases.
/// <para>
/// <b>Mode-independence is a property of WHERE this fires, not a coincidence.</b> All of this happens inside
/// projection binding, at translation time, before the compile-time gate reads
/// <see cref="MongoQueryMode"/> — so <c>NativeOnly</c> gets the identical
/// <see cref="InvalidOperationException"/>, NOT a <see cref="NativeTranslationNotSupportedException"/>. The
/// same reasoning is recorded on <c>NativeOwnedCollectionFilteredCountTests</c>'s
/// <c>Bare_correlated_element_predicate_still_hard_fails_in_every_mode</c>.
/// </para>
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
        // Nullable ON PURPOSE, matching the sibling owned-collection fixtures: a missing stored element field
        // must materialize rather than throw, so the regression control below can be seeded raggedly.
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

    // "two" carries a DUPLICATE heading so a working Distinct would be distinguishable from a no-op, and
    // "empty" carries an empty array so DefaultIfEmpty would be distinguishable from a no-op — the fixture is
    // built to be able to prove these operators WORK, should a later slice make them work, rather than only to
    // prove they throw.
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
    /// Asserts the shape declines CLEANLY in every mode, and that the message NAMES the interposed operator.
    /// </summary>
    /// <remarks>
    /// The <paramref name="operatorName"/> assertion is the part that discriminates: the pre-fix failures were
    /// <see cref="ArgumentException"/>s whose messages named either a dictionary key (<c>"Key: p"</c>) or a
    /// pair of CLR types, and neither mentions the operator. Asserting only the exception TYPE would not have
    /// been enough for <c>Concat</c>, which already threw <see cref="InvalidOperationException"/> pre-fix.
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
        // Pre-fix (measured at the parent commit): ArgumentException, "An item with the same key has already
        // been added. Key: p", under Native, DriverLinq AND NativeOnly.
        var collection = Seed(nameof(Interposed_Distinct_declines_cleanly_in_every_mode));

        AssertDeclinesCleanlyInEveryMode(
            collection,
            "Distinct",
            q => q.Select(b => b.Posts.Select(p => p.Heading).Distinct().ToList()).Cast<object>());
    }

    [Fact]
    public void Interposed_Take_declines_cleanly_in_every_mode()
    {
        // Pre-fix: ArgumentException, "Expression of type 'List<Post>' cannot be used for parameter of type
        // 'IQueryable<Post>' ... Take[Post]" — note Post, not string: EF pushed the projection inside Take, so
        // this shape never reached the duplicate-key Add the ticket names.
        var collection = Seed(nameof(Interposed_Take_declines_cleanly_in_every_mode));

        AssertDeclinesCleanlyInEveryMode(
            collection,
            "Take",
            q => q.Select(b => b.Posts.Select(p => p.Heading).Take(2).ToList()).Cast<object>());
    }

    [Fact]
    public void Interposed_Reverse_declines_cleanly_in_every_mode()
    {
        // Pre-fix: the same Update-assignability ArgumentException as Take, for the same reason.
        var collection = Seed(nameof(Interposed_Reverse_declines_cleanly_in_every_mode));

        AssertDeclinesCleanlyInEveryMode(
            collection,
            "Reverse",
            q => q.Select(b => b.Posts.Select(p => p.Heading).Reverse().ToList()).Cast<object>());
    }

    [Fact]
    public void Interposed_DefaultIfEmpty_declines_cleanly_in_every_mode()
    {
        // Pre-fix: the same duplicate-key ArgumentException as Distinct, for the same reason.
        var collection = Seed(nameof(Interposed_DefaultIfEmpty_declines_cleanly_in_every_mode));

        AssertDeclinesCleanlyInEveryMode(
            collection,
            "DefaultIfEmpty",
            q => q.Select(b => b.Posts.Select(p => p.Heading).DefaultIfEmpty().ToList()).Cast<object>());
    }

    [Fact]
    public void Interposed_Concat_declines_cleanly_in_every_mode()
    {
        // The CONTROL of the family: this one already declined cleanly pre-fix (InvalidOperationException,
        // "could not be translated", naming Concat). It is pinned so the guard added for the other four is
        // shown not to have changed it.
        var collection = Seed(nameof(Interposed_Concat_declines_cleanly_in_every_mode));

        AssertDeclinesCleanlyInEveryMode(
            collection,
            "Concat",
            q => q.Select(b => b.Posts.Select(p => p.Heading).Concat(new[] {"z"}).ToList()).Cast<object>());
    }

    [Fact]
    public void Owned_collection_Select_with_no_interposed_operator_still_works()
    {
        // REGRESSION CONTROL, asserting DATA rather than an exception type. This is the shape one Visit away
        // from every failing shape above — the same owned-collection Select, the same terminal ToList, minus
        // the interposed operator — and it is the shape whose FIRST visit registers the
        // _collectionShaperMapping entry the duplicate-key crash tripped over. If the fix had been written as
        // "make the Add idempotent" instead of a decline, this test would still pass while the four shapes
        // above silently returned wrong data, which is why the assertions above check the message and this one
        // checks the elements.
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
        // Companion to the test above: this projection shape is NOT in the native slice (a bare projected
        // collection body never populates Select.Projection), so under NativeOnly it declines with the
        // provider's own gate exception. Recorded because it is the one place in this file where NativeOnly
        // behaves DIFFERENTLY from Native/DriverLinq — the five failing shapes above do not, since they fail
        // before the gate is reached, and asserting that difference here is what proves the distinction is
        // real rather than assumed.
        var collection = Seed(nameof(Owned_collection_Select_with_no_interposed_operator_is_a_driver_linq_fallback));

        using var db = CreateContext(collection, MongoQueryMode.NativeOnly);

        Assert.Throws<NativeTranslationNotSupportedException>(
            () => db.Entities.AsNoTracking().Select(b => b.Posts.Select(p => p.Heading).ToList()).ToList());
    }
}
