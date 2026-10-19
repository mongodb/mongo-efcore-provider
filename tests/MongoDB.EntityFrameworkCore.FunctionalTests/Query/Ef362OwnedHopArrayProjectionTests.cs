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
using MongoDB.EntityFrameworkCore.Diagnostics;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Infrastructure;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// EF-362: an owned entity-COLLECTION leaf reached through an <c>OwnsOne</c> hop —
/// <c>Select(b =&gt; new { b.Title, b.Home.Notes })</c> — now goes native, emitting
/// <c>{"Home.Notes": "$Home.Notes"}</c> and reading it back by walking that same dotted path.
/// <para>
/// <b>Every failure mode in this file is SILENT.</b> A missed alias read yields <see langword="null"/>, and the
/// EF-358 coalesce turns a missed ARRAY read into an EMPTY collection with no exception anywhere — so a test
/// that asserted "does not throw", or a count, or <c>!= null</c>, would have stayed green through the exact
/// defect this feature had while it was being built. Every assertion below is on VALUES.
/// </para>
/// <para>
/// <b>The parameterized-<c>Where</c> legs are not decoration.</b> A captured local in a
/// <c>string.StartsWith</c> is the measured trigger for a LATE native-factory decline: the query is routed
/// native at translation time, the renderer then declines the parameterized regex, and the driver-LINQ bridge
/// executes the captured chain — <c>$project</c> and all — under the DEFAULT <c>Native</c> mode. That route
/// keys the projection by MEMBER name while the shaper reads by document PATH, and it returned empty
/// collections until the late-fallback strip was widened to fire on any document-path alias override. A
/// constant-only <c>Where</c> never reaches it and would leave this file falsely green.
/// </para>
/// </summary>
[XUnitCollection("QueryTests")]
public class Ef362OwnedHopArrayProjectionTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    // No `= []` on Notes, deliberately: a field initializer masks a null-vs-empty read-back, which is exactly
    // the class of defect this feature can have. Same reason NativeArrayProjectionTests omits it.
    public class NestedOwnerBlog
    {
        public ObjectId Id { get; set; }
        public string Title { get; set; } = "";
        public Home Home { get; set; } = null!;
    }

    public class Home
    {
        public string? City { get; set; }
        public List<Note> Notes { get; set; } = null!;
    }

    public class Note
    {
        public int NoteId { get; set; }
        public string? Text { get; set; }
    }

    // Explicit key on the element: the element shaper never reads its owner's key, so this model isolates the
    // dotted alias from the owner-key emission.
    private static readonly Action<ModelBuilder> KeyedModel = mb =>
        mb.Entity<NestedOwnerBlog>().OwnsOne(b => b.Home, h => h.OwnsMany(x => x.Notes, n => n.HasKey(x => x.NoteId)));

    // No HasKey: EF builds a SHADOW composite key and the element shaper reads the ROOT's _id out of the row it
    // is handed. That is the model almost every real user has, and it is what makes the owner-key emission
    // (`_id : "$_id"`) load-bearing for a hop leaf just as it is for a root-declared one.
    private static readonly Action<ModelBuilder> ShadowKeyModel = mb =>
        mb.Entity<NestedOwnerBlog>().OwnsOne(b => b.Home, h => h.OwnsMany(x => x.Notes));

    private static readonly MongoQueryMode[] AllModes =
        [MongoQueryMode.Native, MongoQueryMode.DriverLinq, MongoQueryMode.NativeOnly];

    // ── 1. The ragged matrix, all three modes ─────────────────────────────────────

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Owned_hop_array_leaf_reads_the_dotted_path_for_every_array_state(bool shadowKey)
    {
        var collection = Seed(nameof(Owned_hop_array_leaf_reads_the_dotted_path_for_every_array_state) + shadowKey);
        var model = shadowKey ? ShadowKeyModel : KeyedModel;

        foreach (var mode in AllModes)
        {
            using var db = CreateContext(collection, model, mode);

            var rows = db.Entities.AsNoTracking().OrderBy(b => b.Title)
                .Select(b => new {b.Title, b.Home.Notes})
                .ToList();

            Assert.Equal(new[] {"a_populated", "b_empty", "c_missing", "d_null"}, rows.Select(r => r.Title));

            // Element VALUES, not counts and not != null: an alias miss presents as an empty collection.
            Assert.Equal(new[] {"n1", "n2"}, rows[0].Notes.Select(n => n.Text));
            Assert.Equal(new[] {1, 2}, rows[0].Notes.Select(n => n.NoteId));

            // EF-358: a missing or explicitly-null stored array materializes EMPTY, never null.
            Assert.Empty(rows[1].Notes);
            Assert.Empty(rows[2].Notes);
            Assert.Empty(rows[3].Notes);
        }
    }

    // ── 2. The late-fallback route — the leg that was silently empty ──────────────

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Owned_hop_array_leaf_behind_a_parameterized_where_reads_correct_values(bool shadowKey)
    {
        var collection = Seed(nameof(Owned_hop_array_leaf_behind_a_parameterized_where_reads_correct_values) + shadowKey);
        var model = shadowKey ? ShadowKeyModel : KeyedModel;

        // A CAPTURED LOCAL, not a constant. The native renderer declines a parameterized regex term, so
        // TryBuildNativeFactory returns null AFTER the alias-addressed shaper has been built, and the
        // driver-LINQ bridge runs the captured chain instead — under the DEFAULT Native mode.
        var prefix = "a_";

        // Native and DriverLinq both execute; NativeOnly must decline at the gate rather than answer.
        foreach (var mode in new[] {MongoQueryMode.Native, MongoQueryMode.DriverLinq})
        {
            using var db = CreateContext(collection, model, mode);

            var rows = db.Entities.AsNoTracking().Where(b => b.Title.StartsWith(prefix)).OrderBy(b => b.Title)
                .Select(b => new {b.Title, b.Home.Notes})
                .ToList();

            var row = Assert.Single(rows);
            Assert.Equal("a_populated", row.Title);
            Assert.Equal(new[] {"n1", "n2"}, row.Notes.Select(n => n.Text));
        }

        using var nativeOnly = CreateContext(collection, model, MongoQueryMode.NativeOnly);
        Assert.Throws<NativeTranslationNotSupportedException>(
            () => nativeOnly.Entities.AsNoTracking().Where(b => b.Title.StartsWith(prefix))
                .Select(b => new {b.Title, b.Home.Notes}).ToList());
    }

    // ── 3. The emitted alias ──────────────────────────────────────────────────────

    [Fact]
    public void Owned_hop_array_leaf_emits_the_dotted_document_path_alias()
    {
        var collection = Seed(nameof(Owned_hop_array_leaf_emits_the_dotted_document_path_alias));

        using var db = CreateContextWithLogging(collection, KeyedModel, MongoQueryMode.Native, out var spy);
        _ = db.Entities.AsNoTracking().Select(b => new {b.Title, b.Home.Notes}).ToList();

        var mql = spy.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery)!;

        // NOT a routing proof — the driver-LINQ bridge can emit a structurally similar $project. Test 1's
        // NativeOnly leg is the routing proof. What this pins is the ALIAS: a DOTTED key, which is the whole
        // mechanism, and which MongoDB renders as NESTED output (measured) so the shaper's segment walk
        // resolves it identically against a projected document and a whole one.
        Assert.Contains("\"Home.Notes\" : \"$Home.Notes\"", mql);
        // The owner key rides along for any array leaf, which also suppresses RenderProject's default `_id : 0`.
        Assert.Contains("\"_id\" : \"$_id\"", mql);
        Assert.DoesNotContain("aggregate([])", mql);
    }

    // ── 4. The narrowings that must SURVIVE the widening ──────────────────────────

    [Fact]
    public void Renamed_owned_hop_array_alias_is_still_declined_and_returns_correct_data()
    {
        // The renamed-alias narrowing is orthogonal to the hop and must not be widened by accident.
        // DeriveWrappedLeafAlias only ever replaces a member name that ALREADY agreed with the navigation's own
        // containing element name, so `N = b.Home.Notes` keeps the alias `N`, which is not the document path,
        // and IsNativeArrayProjectionLeaf declines the whole projection.
        //
        // MUTATION: drop DeriveWrappedLeafAlias's `memberName == GetContainingElementName()` conjunct and the
        // NativeOnly leg here stops throwing.
        var collection = Seed(nameof(Renamed_owned_hop_array_alias_is_still_declined_and_returns_correct_data));

        static List<string> Run(SingleEntityDbContext<NestedOwnerBlog> db)
            => db.Entities.AsNoTracking().OrderBy(b => b.Title)
                .Select(b => new {b.Title, N = b.Home.Notes})
                .ToList()
                .Select(r => $"{r.Title}=[{string.Join("|", r.N.Select(n => n.Text))}]")
                .ToList();

        var expected = new[] {"a_populated=[n1|n2]", "b_empty=[]", "c_missing=[]", "d_null=[]"};

        foreach (var mode in new[] {MongoQueryMode.Native, MongoQueryMode.DriverLinq})
        {
            using var db = CreateContext(collection, KeyedModel, mode);
            Assert.Equal(expected, Run(db));
        }

        using var nativeOnly = CreateContext(collection, KeyedModel, MongoQueryMode.NativeOnly);
        Assert.Throws<NativeTranslationNotSupportedException>(() => Run(nativeOnly));
    }

    [Fact]
    public void Owned_hop_SCALAR_leaf_alongside_the_array_leaf_declines_and_the_fallback_returns_the_scalar_correctly()
    {
        // The sibling-readability rule is unchanged by EF-362, and this pins that: a DOTTED SCALAR
        // (`b.Home.City`) resolves to a MongoFieldExpression whose ElementName is "Home.City" while its alias
        // is the member name "City", so IsWholeDocumentReadableLeaf declines it — and, because an array leaf is
        // present, declines the WHOLE projection. The NativeOnly leg below is that decline.
        //
        // THIS TEST WAS FLIPPED, NOT RE-BASELINED. It used to pin a silent-wrong-data shape: the fallback's
        // shaper derived the element name for a dotted owned scalar from the projection MEMBER ("City") and
        // read it at the top level of a whole document, where nothing is stored, so `City` came back NULL
        // under the default Native mode and under explicit DriverLinq alike, while the array leaf beside it
        // was correct. That was tracked as EF-390 and is now FIXED on the main-bound line - the read half of
        // BsonBinding.GetPropertyValueAtElement walks a dotted owned scalar's path instead of treating it as
        // a literal key - so the assertion below is the SEEDED TRUTH rather than a measured wrong answer.
        //
        // What this test still pins is the DECLINE, which is unchanged: the dotted scalar's alias ("City")
        // differs from its element name ("Home.City"), so IsWholeDocumentReadableLeaf rejects it and - because
        // an array leaf is present - declines the WHOLE projection. The NativeOnly leg at the end is that
        // decline; the two fallback-capable modes now return correct values through it.
        var collection = Seed(nameof(Owned_hop_SCALAR_leaf_alongside_the_array_leaf_declines_and_the_fallback_returns_the_scalar_correctly));

        static List<string> Run(SingleEntityDbContext<NestedOwnerBlog> db)
            => db.Entities.AsNoTracking().OrderBy(b => b.Title)
                .Select(b => new {b.Home.City, b.Home.Notes})
                .ToList()
                .Select(r => $"{r.City ?? "<null>"}=[{string.Join("|", r.Notes.Select(n => n.Text))}]")
                .ToList();

        // The seeded truth, which both fallback-capable modes now return.
        var expected = new[] {"NYC=[n1|n2]", "LA=[]", "SF=[]", "DC=[]"};

        foreach (var mode in new[] {MongoQueryMode.Native, MongoQueryMode.DriverLinq})
        {
            using var db = CreateContext(collection, KeyedModel, mode);
            Assert.Equal(expected, Run(db));
        }

        using var nativeOnly = CreateContext(collection, KeyedModel, MongoQueryMode.NativeOnly);
        Assert.Throws<NativeTranslationNotSupportedException>(() => Run(nativeOnly));
    }

    // ── 5. Composition, and the tracking contract ─────────────────────────────────

    [Fact]
    public void Owned_hop_array_leaf_composed_with_filter_sort_and_paging_goes_native()
    {
        var collection = Seed(nameof(Owned_hop_array_leaf_composed_with_filter_sort_and_paging_goes_native));

        using var db = CreateContext(collection, KeyedModel, MongoQueryMode.NativeOnly);

        var rows = db.Entities.AsNoTracking()
            .Where(b => b.Title != "b_empty")
            .OrderByDescending(b => b.Title)
            .Skip(1).Take(2)
            .Select(b => new {b.Title, b.Home.Notes})
            .ToList();

        Assert.Equal(new[] {"c_missing", "a_populated"}, rows.Select(r => r.Title));
        Assert.Empty(rows[0].Notes);
        Assert.Equal(new[] {"n1", "n2"}, rows[1].Notes.Select(n => n.Text));
    }

    // ── Fixture ───────────────────────────────────────────────────────────────────

    private IMongoCollection<NestedOwnerBlog> Seed(string name)
    {
        var coll = database.MongoDatabase.GetCollection<BsonDocument>(
            TemporaryDatabaseFixtureBase.CreateCollectionName(name) + Guid.NewGuid().ToString("N")[..8]);

        // Home itself is present on every row: it is a REQUIRED owned reference, so a document missing it fails
        // materialization for reasons unrelated to the array leaf under test. The four states below are the
        // states of the ARRAY: populated, empty, field missing, field explicitly BSON null.
        coll.InsertMany(
        [
            Row("a_populated", "NYC", new BsonArray([NoteDoc(1, "n1"), NoteDoc(2, "n2")])),
            Row("b_empty", "LA", new BsonArray()),
            Row("c_missing", "SF", null),
            Row("d_null", "DC", BsonNull.Value)
        ]);

        return database.MongoDatabase.GetCollection<NestedOwnerBlog>(coll.CollectionNamespace.CollectionName);
    }

    private static BsonDocument Row(string title, string city, BsonValue? notes)
    {
        var home = new BsonDocument {{"City", city}};
        if (notes != null)
        {
            home.Add("Notes", notes);
        }

        return new BsonDocument {{"_id", ObjectId.GenerateNewId()}, {"Title", title}, {"Home", home}};
    }

    private static BsonDocument NoteDoc(int noteId, string text)
        => new() {{"NoteId", noteId}, {"Text", text}};

    private static SingleEntityDbContext<NestedOwnerBlog> CreateContext(
        IMongoCollection<NestedOwnerBlog> collection, Action<ModelBuilder> modelBuilderAction, MongoQueryMode mode)
        => SingleEntityDbContext.Create(
            collection,
            modelBuilderAction: modelBuilderAction,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });

    private static SingleEntityDbContext<NestedOwnerBlog> CreateContextWithLogging(
        IMongoCollection<NestedOwnerBlog> collection, Action<ModelBuilder> modelBuilderAction, MongoQueryMode mode,
        out SpyLoggerProvider spyLogger)
    {
        var (loggerFactory, provider) = SpyLoggerProvider.Create();
        spyLogger = provider;

        return SingleEntityDbContext.Create(
            collection,
            loggerFactory,
            modelBuilderAction: modelBuilderAction,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                b.EnableSensitiveDataLogging();
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });
    }
}
