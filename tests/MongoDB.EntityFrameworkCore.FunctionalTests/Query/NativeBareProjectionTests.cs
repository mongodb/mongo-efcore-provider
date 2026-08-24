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
/// EF-322 step 3a — the BARE-projection boundary. A selector body that is a plain leaf rather than an
/// anonymous-type/DTO construction (<c>Select(b =&gt; b.Title)</c>, <c>Select(b =&gt; b.Posts)</c>,
/// <c>Select(b =&gt; b.Id)</c>) now emits a native <c>$project</c> instead of falling back to driver-LINQ and
/// folding the projection client-side. Routing is proven by <see cref="MongoQueryMode.NativeOnly"/>, never by
/// MQL shape.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every leaf kind carries a PARAMETERIZED-<c>Where</c> leg, and that is not decoration.</b> A bare body's
/// <c>$project</c> alias is chosen by the provider, while the driver names a bare projection <c>_v</c> — so on
/// any route where the DOM shaper built for the native <c>$project</c> is handed a DRIVER-rendered pipeline,
/// the two disagree. The route that does that is a LATE native-factory decline under the DEFAULT
/// <see cref="MongoQueryMode.Native"/> mode, and the cheapest way to reach it is a captured local inside a
/// <c>string.StartsWith</c> (the native renderer refuses a parameterized regex term). A constant-only
/// <c>Where</c> never reaches it, because the native factory succeeds.
/// </para>
/// <para>
/// <b>The legs assert VALUES, never absence-of-throw</b>, because the failure is SILENT for everything except a
/// non-nullable value type: a nullable scalar comes back <see langword="null"/> and an array comes back EMPTY,
/// with no exception anywhere. Each scalar leg therefore mixes a non-nullable leaf (the only loud one) with a
/// nullable string and a nullable int.
/// </para>
/// </remarks>
[XUnitCollection("QueryTests")]
public class NativeBareProjectionTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    // The ragged fixture. Deliberately un-masked: NO `= []` initializer on either collection navigation, so a
    // null-vs-empty read-back is observable rather than papered over by the POCO (the same un-masking rule
    // NativeArrayProjectionTests and the EF-358 fixtures follow).
    //
    // Note the nullable/non-nullable mix — Title/Rank non-nullable, Note/Score nullable. The parameterized-Where
    // legs need both: only a non-nullable leaf fails LOUDLY on an alias miss, so a fixture of only non-nullable
    // leaves would have caught the late-fallback bug by luck rather than by design.
    public class Blog
    {
        public ObjectId Id { get; set; }
        public string Title { get; set; } = "";
        public string? Note { get; set; }
        public int? Score { get; set; }
        public int Rank { get; set; }
        public List<string>? Tags { get; set; }
        public List<Post> Posts { get; set; } = null!;
    }

    public class Post
    {
        public int PostId { get; set; }
        public string? Heading { get; set; }
    }

    private static readonly Action<ModelBuilder> BlogModel = mb =>
        mb.Entity<Blog>().OwnsMany(b => b.Posts, p => p.HasKey(x => x.PostId));

    // Test 13's EF-362 tripwire model: the scalar leaf is reached THROUGH an owned single reference, so its
    // document path is DOTTED ("Home.City") while a $project alias would have to be dotted too — and a dotted
    // alias is read back as a LITERAL key while MongoDB renders it as a NESTED document. The bare arm declines
    // it deliberately; EF-362 is the ticket that flips this test.
    public class HopBlog
    {
        public ObjectId Id { get; set; }
        public string Title { get; set; } = "";
        public Home Home { get; set; } = null!;
    }

    public class Home
    {
        public string City { get; set; } = "";
    }

    private static readonly Action<ModelBuilder> HopModel = mb =>
        mb.Entity<HopBlog>().OwnsOne(b => b.Home);

    // Test 14's positive control gets its OWN clean flat model — no owned data at all. The ragged Blog fixture
    // cannot settle it: a raw-seeded owned element carries no owner FK, which makes a WHOLE-ENTITY query over it
    // fail for reasons that have nothing to do with this slice (that is exactly what left the control
    // inconclusive when it was first measured).
    public class FlatBlog
    {
        public ObjectId Id { get; set; }
        public string Title { get; set; } = "";
        public int Rank { get; set; }
    }

    private static readonly Action<ModelBuilder> FlatModel = _ => { };

    // ── 1. Bare scalar ────────────────────────────────────────────────────────────

    [Fact]
    public void Bare_scalar_projection_goes_native()
    {
        var collection = SeedRagged(nameof(Bare_scalar_projection_goes_native));

        using var db = CreateContext(collection, MongoQueryMode.NativeOnly);

        Assert.Equal(
            ["p1_two", "p2_empty", "p3_missing", "p4_null", "p5_one"],
            db.Entities.AsNoTracking().OrderBy(b => b.Title).Select(b => b.Title).ToList());

        Assert.Equal(
            ["n1", null, "n3", null, "n5"],
            db.Entities.AsNoTracking().OrderBy(b => b.Title).Select(b => b.Note).ToList());

        Assert.Equal(
            [10, null, 30, null, 50],
            db.Entities.AsNoTracking().OrderBy(b => b.Title).Select(b => b.Score).ToList());

        Assert.Equal(
            [1, 2, 3, 4, 5],
            db.Entities.AsNoTracking().OrderBy(b => b.Title).Select(b => b.Rank).ToList());
    }

    [Fact]
    public void Bare_scalar_projection_behind_a_parameterized_predicate_returns_correct_values()
    {
        var collection = SeedRagged(nameof(Bare_scalar_projection_behind_a_parameterized_predicate_returns_correct_values));

        // The REQUIRED parameterized-Where leg. `prefix` is a captured local, so the native renderer refuses the
        // regex term, TryBuildNativeFactory declines LATE, and the alias-addressed shaper is handed a pipeline
        // the driver rendered from the captured chain. It is correct only because the late-fallback strip removes
        // the pushed-down bare Select, leaving whole documents that a document-path alias reads correctly.
        var prefix = "p";

        // DEFAULT Native mode, deliberately: this route does not exist under NativeOnly (which throws on the
        // decline) and is never taken under DriverLinq (which never builds a native factory at all).
        using var db = CreateContext(collection, MongoQueryMode.Native);

        // The two NULLABLE leaves are EXECUTED AND ASSERTED FIRST, deliberately, and the ordering is
        // load-bearing rather than stylistic. Without the late-fallback strip a nullable leaf comes back
        // <null>,<null>,<null>,<null>,<null> with NO exception at all, while the non-nullable Title leaf throws
        // at materialization ("Document element 'Title' is missing for required non-nullable property"). Because
        // ToList() materializes eagerly, running the Title query first would abort the test before either silent
        // row was ever observed — so it would prove only the loud half of the failure mode, which is the half
        // that would have been caught anyway.
        var notes = db.Entities.AsNoTracking()
            .Where(b => b.Title.StartsWith(prefix)).OrderBy(b => b.Title)
            .Select(b => b.Note).ToList();
        Assert.Equal(["n1", null, "n3", null, "n5"], notes);

        var scores = db.Entities.AsNoTracking()
            .Where(b => b.Title.StartsWith(prefix)).OrderBy(b => b.Title)
            .Select(b => b.Score).ToList();
        Assert.Equal([10, null, 30, null, 50], scores);

        // And the non-nullable leaf, which is the only one that fails loudly, kept for exactly that contrast.
        var titles = db.Entities.AsNoTracking()
            .Where(b => b.Title.StartsWith(prefix)).OrderBy(b => b.Title)
            .Select(b => b.Title).ToList();
        Assert.Equal(["p1_two", "p2_empty", "p3_missing", "p4_null", "p5_one"], titles);
    }

    // ── 2. Bare primary key ───────────────────────────────────────────────────────

    [Fact]
    public void Bare_primary_key_projection_goes_native()
    {
        var collection = SeedRagged(nameof(Bare_primary_key_projection_goes_native));

        var expected = collection.Database
            .GetCollection<BsonDocument>(collection.CollectionNamespace.CollectionName)
            .Find(FilterDefinition<BsonDocument>.Empty).ToList()
            .Select(d => d["_id"].AsObjectId).OrderBy(id => id).ToList();

        using var db = CreateContext(collection, MongoQueryMode.NativeOnly);
        Assert.Equal(expected, db.Entities.AsNoTracking().Select(b => b.Id).ToList().OrderBy(id => id));
    }

    [Fact]
    public void Bare_primary_key_projection_emits_id_and_no_id_exclusion()
    {
        var collection = SeedRagged(nameof(Bare_primary_key_projection_emits_id_and_no_id_exclusion));

        // NOT a routing proof — a driver-LINQ push-down emits a $project too. What this pins is the ALIAS: a
        // single-property PK's element name is `_id`, so the emitted body already contains `_id` and
        // RenderProject must therefore NOT add its default `_id : 0` exclusion on top (which would be a
        // malformed inclusion/exclusion mix).
        using var db = CreateContextWithLogging(collection, MongoQueryMode.Native, out var spy);
        _ = db.Entities.AsNoTracking().Select(b => b.Id).ToList();

        var mql = spy.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery)!;
        Assert.Contains("\"_id\" : \"$_id\"", mql);
        Assert.DoesNotContain("\"_id\" : 0", mql);
    }

    [Fact]
    public void Bare_primary_key_projection_behind_a_parameterized_predicate_returns_correct_values()
    {
        var collection =
            SeedRagged(nameof(Bare_primary_key_projection_behind_a_parameterized_predicate_returns_correct_values));
        var prefix = "p";

        var expected = collection.Database
            .GetCollection<BsonDocument>(collection.CollectionNamespace.CollectionName)
            .Find(FilterDefinition<BsonDocument>.Empty).ToList()
            .Select(d => d["_id"].AsObjectId).OrderBy(id => id).ToList();

        using var db = CreateContext(collection, MongoQueryMode.Native);
        var actual = db.Entities.AsNoTracking()
            .Where(b => b.Title.StartsWith(prefix)).Select(b => b.Id).ToList().OrderBy(id => id).ToList();

        Assert.Equal(expected, actual);
    }

    // ── 3. Bare primitive collection ──────────────────────────────────────────────

    [Fact]
    public void Bare_primitive_collection_projection_goes_native()
    {
        var collection = SeedRagged(nameof(Bare_primitive_collection_projection_goes_native));

        using var db = CreateContext(collection, MongoQueryMode.NativeOnly);
        Assert.Equal(
            ExpectedTags,
            db.Entities.AsNoTracking().OrderBy(b => b.Title).Select(b => b.Tags).ToList().Select(Print));
    }

    [Fact]
    public void Bare_primitive_collection_projection_matches_driver_linq()
    {
        var collection = SeedRagged(nameof(Bare_primitive_collection_projection_matches_driver_linq));

        static List<string> Run(SingleEntityDbContext<Blog> db)
            => db.Entities.AsNoTracking().OrderBy(b => b.Title).Select(b => b.Tags).ToList().Select(Print).ToList();

        using var native = CreateContext(collection, MongoQueryMode.Native);
        using var driver = CreateContext(collection, MongoQueryMode.DriverLinq);

        Assert.Equal(ExpectedTags, Run(driver));
        Assert.Equal(Run(driver), Run(native));
    }

    [Fact]
    public void Bare_primitive_collection_projection_behind_a_parameterized_predicate_returns_correct_values()
    {
        var collection =
            SeedRagged(nameof(Bare_primitive_collection_projection_behind_a_parameterized_predicate_returns_correct_values));
        var prefix = "p";

        using var db = CreateContext(collection, MongoQueryMode.Native);
        Assert.Equal(
            ExpectedTags,
            db.Entities.AsNoTracking().Where(b => b.Title.StartsWith(prefix)).OrderBy(b => b.Title)
                .Select(b => b.Tags).ToList().Select(Print));
    }

    // ── 4-6. Bare owned entity collection ─────────────────────────────────────────

    [Fact]
    public void Bare_owned_collection_projection_goes_native()
    {
        var collection = SeedRagged(nameof(Bare_owned_collection_projection_goes_native));

        using var db = CreateContext(collection, MongoQueryMode.NativeOnly);
        Assert.Equal(
            ExpectedPosts,
            db.Entities.AsNoTracking().OrderBy(b => b.Title).Select(b => b.Posts).ToList().Select(PrintPosts));
    }

    [Fact]
    public void Bare_owned_collection_projection_matches_driver_linq()
    {
        var collection = SeedRagged(nameof(Bare_owned_collection_projection_matches_driver_linq));

        // THE silent case, and the target of the alias mutation. Under explicit DriverLinq an entity/collection
        // leaf makes EF's own ProjectionAnalyzer refuse to push the projection down, so the alias-addressed
        // shaper runs over WHOLE documents from aggregate([]) — correct only because the alias IS the element
        // name. Alias the leaf anything else and every row comes back as an EMPTY collection, silently.
        static List<string> Run(SingleEntityDbContext<Blog> db)
            => db.Entities.AsNoTracking().OrderBy(b => b.Title).Select(b => b.Posts).ToList()
                .Select(PrintPosts).ToList();

        using var native = CreateContext(collection, MongoQueryMode.Native);
        using var driver = CreateContext(collection, MongoQueryMode.DriverLinq);

        Assert.Equal(ExpectedPosts, Run(driver));
        Assert.Equal(Run(driver), Run(native));
    }

    [Fact]
    public void Bare_owned_collection_projection_emits_the_element_name_alias()
    {
        var collection = SeedRagged(nameof(Bare_owned_collection_projection_emits_the_element_name_alias));

        // NOT a routing proof (see the class remarks) — it pins the emitted ALIAS, which is the thing the whole
        // slice turns on, plus the owner key an array leaf must drag along so a shadow-keyed element can still
        // materialize per row.
        using var db = CreateContextWithLogging(collection, MongoQueryMode.Native, out var spy);
        _ = db.Entities.AsNoTracking().Select(b => b.Posts).ToList();

        var mql = spy.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery)!;
        Assert.Contains("\"Posts\" : \"$Posts\"", mql);
        Assert.Contains("\"_id\" : \"$_id\"", mql);
        Assert.DoesNotContain("aggregate([])", mql);
    }

    [Fact]
    public void Bare_owned_collection_projection_behind_a_parameterized_predicate_returns_correct_values()
    {
        var collection =
            SeedRagged(nameof(Bare_owned_collection_projection_behind_a_parameterized_predicate_returns_correct_values));
        var prefix = "p";

        using var db = CreateContext(collection, MongoQueryMode.Native);
        Assert.Equal(
            ExpectedPosts,
            db.Entities.AsNoTracking().Where(b => b.Title.StartsWith(prefix)).OrderBy(b => b.Title)
                .Select(b => b.Posts).ToList().Select(PrintPosts));
    }

    // ── 7. Composition with filter / sort / paging ─────────────────────────────────

    [Fact]
    public void Bare_projection_composed_with_filter_sort_and_paging_goes_native()
    {
        var collection = SeedRagged(nameof(Bare_projection_composed_with_filter_sort_and_paging_goes_native));

        using var db = CreateContext(collection, MongoQueryMode.NativeOnly);
        Assert.Equal(
            ["p2_empty", "p3_missing"],
            db.Entities.AsNoTracking().Where(b => b.Rank >= 2).OrderBy(b => b.Title).Skip(0).Take(2)
                .Select(b => b.Title).ToList());
    }

    [Fact]
    public void Bare_projection_composed_with_a_parameterized_filter_and_paging_returns_correct_values()
    {
        var collection =
            SeedRagged(nameof(Bare_projection_composed_with_a_parameterized_filter_and_paging_returns_correct_values));
        var prefix = "p";

        using var db = CreateContext(collection, MongoQueryMode.Native);
        Assert.Equal(
            ["p2_empty", "p3_missing"],
            db.Entities.AsNoTracking().Where(b => b.Title.StartsWith(prefix)).OrderBy(b => b.Title).Skip(1).Take(2)
                .Select(b => b.Title).ToList());
    }

    // ── 8. Trailing bare projection after a set operation ─────────────────────────

    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.DriverLinq)]
    [InlineData(MongoQueryMode.NativeOnly)]
    public void Bare_projection_after_a_union_or_concat_goes_native(MongoQueryMode mode)
    {
        var collection = SeedSetOps(nameof(Bare_projection_after_a_union_or_concat_goes_native) + mode);
        using var db = CreateContext(collection, mode);

        // The DUPLICATED "p2" is the load-bearing row: two DISTINCT documents share that Title, so whole-entity
        // dedup before the trailing $project keeps both while dedup over the PROJECTED value would collapse them.
        // Its presence is what proves the trailing projection cannot change the set operation's semantics — which
        // is why this shape is admitted while a projected OPERAND is not (see the operand tripwire below).
        Assert.Equal(
            ["p2", "p2", "q0", "r_mid", "s_hi"],
            Sorted(db.Entities.AsNoTracking().Where(b => b.Rank <= 3)
                .Union(db.Entities.AsNoTracking().Where(b => b.Rank >= 3))
                .Select(b => b.Title)));

        Assert.Equal(
            ["p2", "p2", "q0", "r_mid", "r_mid", "s_hi"],
            Sorted(db.Entities.AsNoTracking().Where(b => b.Rank <= 3)
                .Concat(db.Entities.AsNoTracking().Where(b => b.Rank >= 3))
                .Select(b => b.Title)));
    }

    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.NativeOnly)]
    public void Bare_projection_after_an_intersect_or_except_goes_native(MongoQueryMode mode)
    {
        // No DriverLinq leg: the driver's own LINQ provider does not translate a cross-view Intersect/Except at
        // all, so there is no oracle for these two in any mode — the assertion is against the seed.
        var collection = SeedSetOps(nameof(Bare_projection_after_an_intersect_or_except_goes_native) + mode);
        using var db = CreateContext(collection, mode);

        Assert.Equal(
            ["r_mid"],
            Sorted(db.Entities.AsNoTracking().Where(b => b.Rank <= 3)
                .Intersect(db.Entities.AsNoTracking().Where(b => b.Rank >= 3))
                .Select(b => b.Title)));

        Assert.Equal(
            ["p2", "q0"],
            Sorted(db.Entities.AsNoTracking().Where(b => b.Rank <= 3)
                .Except(db.Entities.AsNoTracking().Where(b => b.Rank >= 3))
                .Select(b => b.Title)));
    }

    // ── 13. Owned-hop scalar: the EF-362 tripwire ─────────────────────────────────

    [Fact]
    public void Bare_owned_hop_scalar_projection_declines()
    {
        var collection = SeedHop(nameof(Bare_owned_hop_scalar_projection_declines));

        // A DELIBERATE decline, and the shape EF-362 flips: for an owned hop the document path is DOTTED
        // ("Home.City"), and a dotted alias is looked up by the shaper as a literal key while MongoDB's $project
        // renders it as a nested document. Declining keeps the shape byte-identical to pre-3a.
        foreach (var mode in new[] {MongoQueryMode.Native, MongoQueryMode.DriverLinq})
        {
            using var db = CreateHopContext(collection, mode);
            Assert.Equal(
                ["Bristol", "Cardiff"],
                db.Entities.AsNoTracking().OrderBy(b => b.Title).Select(b => b.Home.City).ToList());
        }

        using var nativeOnly = CreateHopContext(collection, MongoQueryMode.NativeOnly);
        Assert.Throws<NativeTranslationNotSupportedException>(
            () => nativeOnly.Entities.AsNoTracking().OrderBy(b => b.Title).Select(b => b.Home.City).ToList());
    }

    // ── 14. Bare entity: the positive control ─────────────────────────────────────

    [Fact]
    public void Bare_entity_projection_is_unchanged_and_still_native()
    {
        var collection = SeedFlat(nameof(Bare_entity_projection_is_unchanged_and_still_native));

        // `Select(x => x)` is returned unchanged by TranslateSelect's very first line, so it never reaches the
        // projection binder and 3a adds no arm that could match a bare ParameterExpression. This is the control
        // that 3a did not disturb what already worked.
        using var db = CreateFlatContext(collection, MongoQueryMode.NativeOnly);

        Assert.Equal(
            ["a", "b", "c"],
            db.Entities.AsNoTracking().OrderBy(b => b.Title).Select(x => x).ToList().Select(b => b.Title));

        Assert.Equal(
            ["b", "c"],
            db.Entities.AsNoTracking().Where(b => b.Rank >= 2).OrderBy(b => b.Title).Select(x => x).ToList()
                .Select(b => b.Title));
    }

    // ── 15. Bare projected set-op OPERAND: the narrowing tripwire ─────────────────

    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.DriverLinq)]
    public void Bare_projected_union_and_concat_operands_still_decline_and_return_correct_values(MongoQueryMode mode)
    {
        var collection = SeedSetOps(
            nameof(Bare_projected_union_and_concat_operands_still_decline_and_return_correct_values) + mode);
        using var db = CreateContext(collection, mode);

        // The DELIBERATE §7.2 narrowing. A projected-OPERAND set op dedups/source-tags over the whole PROJECTED
        // document, so a bare projected operand changes what $$ROOT is; admitting it was measured to flip
        // Intersect/Except from throwing to answering, on the two operators with no oracle. Declining restores
        // the pre-3a disposition exactly: Union/Concat fall back and answer correctly here.
        //
        // Note the answers DIFFER from test 8's: dedup here is over the projected VALUES, so the two distinct
        // "p2" documents collapse to one.
        Assert.Equal(
            ["p2", "q0", "r_mid", "s_hi"],
            Sorted(db.Entities.AsNoTracking().Where(b => b.Rank <= 3).Select(b => b.Title)
                .Union(db.Entities.AsNoTracking().Where(b => b.Rank >= 3).Select(b => b.Title))));

        Assert.Equal(
            ["p2", "p2", "q0", "r_mid", "r_mid", "s_hi"],
            Sorted(db.Entities.AsNoTracking().Where(b => b.Rank <= 3).Select(b => b.Title)
                .Concat(db.Entities.AsNoTracking().Where(b => b.Rank >= 3).Select(b => b.Title))));
    }

    [Fact]
    public void Bare_projected_union_and_concat_operands_decline_under_native_only()
    {
        var collection = SeedSetOps(nameof(Bare_projected_union_and_concat_operands_decline_under_native_only));
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly);

        Assert.Throws<NativeTranslationNotSupportedException>(
            () => db.Entities.AsNoTracking().Where(b => b.Rank <= 3).Select(b => b.Title)
                .Union(db.Entities.AsNoTracking().Where(b => b.Rank >= 3).Select(b => b.Title)).ToList());

        Assert.Throws<NativeTranslationNotSupportedException>(
            () => db.Entities.AsNoTracking().Where(b => b.Rank <= 3).Select(b => b.Title)
                .Concat(db.Entities.AsNoTracking().Where(b => b.Rank >= 3).Select(b => b.Title)).ToList());
    }

    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.DriverLinq)]
    [InlineData(MongoQueryMode.NativeOnly)]
    public void Bare_projected_intersect_and_except_operands_hard_fail_in_every_mode(MongoQueryMode mode)
    {
        var collection = SeedSetOps(nameof(Bare_projected_intersect_and_except_operands_hard_fail_in_every_mode) + mode);
        using var db = CreateContext(collection, mode);

        // The pre-3a disposition for these two, preserved: Intersect/Except have NO driver-LINQ baseline, so a
        // declined shape hard-fails rather than landing on a working fallback. Asserting the FAILURE is the
        // point — without the narrowing they start ANSWERING, which is the dangerous direction because nothing
        // checks the answer.
        Assert.ThrowsAny<Exception>(
            () => db.Entities.AsNoTracking().Where(b => b.Rank <= 3).Select(b => b.Title)
                .Intersect(db.Entities.AsNoTracking().Where(b => b.Rank >= 3).Select(b => b.Title)).ToList());

        Assert.ThrowsAny<Exception>(
            () => db.Entities.AsNoTracking().Where(b => b.Rank <= 3).Select(b => b.Title)
                .Except(db.Entities.AsNoTracking().Where(b => b.Rank >= 3).Select(b => b.Title)).ToList());
    }

    // ── 16. Bare projection then Distinct: the narrowing tripwire ─────────────────

    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.DriverLinq)]
    public void Bare_projection_then_Distinct_still_declines_and_returns_correct_values(MongoQueryMode mode)
    {
        var collection = SeedSetOps(nameof(Bare_projection_then_Distinct_still_declines_and_returns_correct_values) + mode);
        using var db = CreateContext(collection, mode);

        // The DELIBERATE §7.3 narrowing, and it is a CORRECTNESS tripwire rather than a scope one: binding this
        // natively flips Route to GroupBy AFTER the emit side committed the bare alias, ApplyProjection's own
        // Route conjunct then reverts the alias to null, and the shaper's result type becomes BsonDocument —
        // measured as 4 specification cases hard-failing from a base state of passing.
        Assert.Equal(
            ["p2", "q0", "r_mid", "s_hi"],
            Sorted(db.Entities.AsNoTracking().Select(b => b.Title).Distinct()));
    }

    [Fact]
    public void Bare_projection_then_Distinct_declines_under_native_only()
    {
        var collection = SeedSetOps(nameof(Bare_projection_then_Distinct_declines_under_native_only));
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly);

        Assert.Throws<NativeTranslationNotSupportedException>(
            () => db.Entities.AsNoTracking().Select(b => b.Title).Distinct().ToList());
    }

    // ── 17. Cardinality operator after a bare projection ──────────────────────────

    [Fact]
    public void Bare_projection_then_cardinality_operator_goes_native()
    {
        var collection = SeedRagged(nameof(Bare_projection_then_cardinality_operator_goes_native));

        // An INCIDENTAL widening that arrives with 3a — neither narrowing is on the cardinality path — so it
        // needs its own pin rather than being left to a later composition slice.
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly);

        Assert.Equal(5, db.Entities.AsNoTracking().Select(b => b.Title).Count());
        Assert.Equal("p1_two", db.Entities.AsNoTracking().OrderBy(b => b.Title).Select(b => b.Title).First());
    }

    [Fact]
    public void Bare_projection_then_cardinality_operator_behind_a_parameterized_predicate_is_correct()
    {
        var collection =
            SeedRagged(nameof(Bare_projection_then_cardinality_operator_behind_a_parameterized_predicate_is_correct));
        var prefix = "p";

        using var db = CreateContext(collection, MongoQueryMode.Native);

        Assert.Equal(5, db.Entities.AsNoTracking().Where(b => b.Title.StartsWith(prefix)).Select(b => b.Title).Count());
        Assert.Equal(
            "p1_two",
            db.Entities.AsNoTracking().Where(b => b.Title.StartsWith(prefix)).OrderBy(b => b.Title)
                .Select(b => b.Title).First());
    }

    // ── The write-once guard's own shape ──────────────────────────────────────────

    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.DriverLinq)]
    public void A_second_projection_after_a_bare_one_returns_correct_values(MongoQueryMode mode)
    {
        var collection = SeedRagged(nameof(A_second_projection_after_a_bare_one_returns_correct_values) + mode);
        using var db = CreateContext(collection, mode);

        // The composition-after-projection seam, from the bare side. The bare arm declines outright when
        // Projection is already populated, which is both what keeps this shape's pre-3a disposition and what
        // makes the alias override provably write-once (AddProjectionAliasOverride uses Dictionary.Add). NOTE:
        // EF Core's own pending-selector machinery fuses two member-access Selects before the provider sees
        // them, so this may never reach a second binder call at all — the assertion is on the VALUES either way.
        Assert.Equal(
            [6, 8, 10, 7, 6],
            db.Entities.AsNoTracking().OrderBy(b => b.Title).Select(b => b.Title).Select(t => t.Length).ToList());
    }

    // ── Seeds and helpers ─────────────────────────────────────────────────────────

    // The four array states the count and array paths have historically diverged on — populated, empty, field
    // MISSING, field explicitly BSON NULL — plus a fifth populated row, and a nullable/non-nullable value mix.
    // Every Title shares the prefix "p", so the parameterized-Where legs select ALL FIVE rows and can therefore
    // compare against the same expectations the unfiltered legs use.
    private static readonly string[] ExpectedTags = ["t1|t2", "<empty>", "<null>", "<null>", "t9"];

    private static readonly string[] ExpectedPosts = ["h1|h2", "<empty>", "<empty>", "<empty>", "h9"];

    private IMongoCollection<Blog> SeedRagged(string name)
    {
        var raw = database.MongoDatabase.GetCollection<BsonDocument>(UniqueCollectionName(name));
        raw.InsertMany(
        [
            RaggedRow("p1_two", 1, "n1", 10, new BsonArray {"t1", "t2"}, new BsonArray {PostDoc(1, "h1"), PostDoc(2, "h2")}),
            RaggedRow("p2_empty", 2, null, null, new BsonArray(), new BsonArray()),
            RaggedRow("p3_missing", 3, "n3", 30, null, null),
            RaggedRow("p4_null", 4, null, null, BsonNull.Value, BsonNull.Value),
            RaggedRow("p5_one", 5, "n5", 50, new BsonArray {"t9"}, new BsonArray {PostDoc(9, "h9")})
        ]);

        // Read the stored documents back and assert the four states really are stored as intended: "missing" and
        // "present but null" are otherwise indistinguishable from results alone, so an un-self-checked seed could
        // silently degrade to three states.
        var stored = raw.Find(FilterDefinition<BsonDocument>.Empty).ToList().ToDictionary(d => d["Title"].AsString);
        Assert.Equal(5, stored.Count);
        Assert.Equal(2, stored["p1_two"]["Posts"].AsBsonArray.Count);
        Assert.Empty(stored["p2_empty"]["Posts"].AsBsonArray);
        Assert.False(stored["p3_missing"].Contains("Posts"));
        Assert.False(stored["p3_missing"].Contains("Tags"));
        Assert.True(stored["p4_null"]["Posts"].IsBsonNull);
        Assert.True(stored["p4_null"]["Tags"].IsBsonNull);
        Assert.False(stored["p2_empty"].Contains("Note"));
        Assert.Single(stored["p5_one"]["Posts"].AsBsonArray);

        return database.MongoDatabase.GetCollection<Blog>(raw.CollectionNamespace.CollectionName);
    }

    // A null `tags`/`posts` means OMIT the element entirely — distinct from BsonNull.Value, which writes an
    // explicit BSON null. Those are the two absent states. Note/Score are omitted when null for the same reason:
    // a nullable leaf's late-fallback read has to be correct for a genuinely absent element, not just a
    // present-but-null one.
    private static BsonDocument RaggedRow(
        string title, int rank, string? note, int? score, BsonValue? tags, BsonValue? posts)
    {
        var doc = new BsonDocument {{"_id", ObjectId.GenerateNewId()}, {"Title", title}, {"Rank", rank}};
        if (note is not null)
        {
            doc.Add("Note", note);
        }

        if (score is not null)
        {
            doc.Add("Score", score.Value);
        }

        if (tags is not null)
        {
            doc.Add("Tags", tags);
        }

        if (posts is not null)
        {
            doc.Add("Posts", posts);
        }

        return doc;
    }

    // "PostId", not "_id": PrimaryKeyDiscoveryConvention returns early for an OWNED type that already has an
    // explicit primary key, so the stored element name is just the property name.
    private static BsonDocument PostDoc(int postId, string heading)
        => new() {{"PostId", postId}, {"Heading", heading}};

    // Two DISTINCT documents share the Title "p2" while differing in Rank, which is what each operand's own
    // Where selects on. That is what makes the set-op tests non-vacuous: whole-entity dedup (test 8) keeps both,
    // dedup over the projected value (test 15) collapses them, so the two shapes give DIFFERENT answers and a
    // test cannot pass by accident on the wrong one.
    private IMongoCollection<Blog> SeedSetOps(string name)
    {
        var raw = database.MongoDatabase.GetCollection<BsonDocument>(UniqueCollectionName(name));
        raw.InsertMany(
        [
            SetOpRow("p2", 2),
            SetOpRow("p2", 4),
            SetOpRow("q0", 1),
            SetOpRow("r_mid", 3),
            SetOpRow("s_hi", 5)
        ]);

        var stored = raw.Find(FilterDefinition<BsonDocument>.Empty).ToList();
        Assert.Equal(2, stored.Count(d => d["Title"].AsString == "p2"));
        Assert.Equal(2, stored.Where(d => d["Title"].AsString == "p2").Select(d => d["Rank"].AsInt32).Distinct().Count());

        return database.MongoDatabase.GetCollection<Blog>(raw.CollectionNamespace.CollectionName);
    }

    private static BsonDocument SetOpRow(string title, int rank)
        => new()
        {
            {"_id", ObjectId.GenerateNewId()}, {"Title", title}, {"Rank", rank},
            {"Tags", new BsonArray()}, {"Posts", new BsonArray()}
        };

    private IMongoCollection<HopBlog> SeedHop(string name)
    {
        var raw = database.MongoDatabase.GetCollection<BsonDocument>(UniqueCollectionName(name));
        raw.InsertMany(
        [
            new BsonDocument
            {
                {"_id", ObjectId.GenerateNewId()}, {"Title", "a"},
                {"Home", new BsonDocument {{"City", "Bristol"}}}
            },
            new BsonDocument
            {
                {"_id", ObjectId.GenerateNewId()}, {"Title", "b"},
                {"Home", new BsonDocument {{"City", "Cardiff"}}}
            }
        ]);
        return database.MongoDatabase.GetCollection<HopBlog>(raw.CollectionNamespace.CollectionName);
    }

    private IMongoCollection<FlatBlog> SeedFlat(string name)
    {
        var raw = database.MongoDatabase.GetCollection<BsonDocument>(UniqueCollectionName(name));
        raw.InsertMany(
        [
            new BsonDocument {{"_id", ObjectId.GenerateNewId()}, {"Title", "a"}, {"Rank", 1}},
            new BsonDocument {{"_id", ObjectId.GenerateNewId()}, {"Title", "b"}, {"Rank", 2}},
            new BsonDocument {{"_id", ObjectId.GenerateNewId()}, {"Title", "c"}, {"Rank", 3}}
        ]);
        return database.MongoDatabase.GetCollection<FlatBlog>(raw.CollectionNamespace.CollectionName);
    }

    // Set operations carry no ordering guarantee (their re-unifying $group stages reorder rows), so every set-op
    // assertion sorts client-side rather than asserting server order.
    private static List<string> Sorted(IQueryable<string> query)
        => query.ToList().OrderBy(t => t, StringComparer.Ordinal).ToList();

    // A collection is printed rather than compared structurally: a null element must be distinguishable from an
    // empty string, and "absent" from "empty", or an assertion silently stops discriminating exactly where the
    // ragged states matter.
    private static string Print(List<string>? tags)
        => tags is null ? "<null>" : tags.Count == 0 ? "<empty>" : string.Join("|", tags);

    private static string PrintPosts(List<Post>? posts)
        => posts is null
            ? "<null>"
            : posts.Count == 0
                ? "<empty>"
                : string.Join("|", posts.Select(p => p.Heading ?? "<nullheading>"));

    private static SingleEntityDbContext<Blog> CreateContext(IMongoCollection<Blog> collection, MongoQueryMode mode)
        => SingleEntityDbContext.Create(
            collection,
            modelBuilderAction: BlogModel,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });

    private static SingleEntityDbContext<HopBlog> CreateHopContext(
        IMongoCollection<HopBlog> collection, MongoQueryMode mode)
        => SingleEntityDbContext.Create(
            collection,
            modelBuilderAction: HopModel,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });

    private static SingleEntityDbContext<FlatBlog> CreateFlatContext(
        IMongoCollection<FlatBlog> collection, MongoQueryMode mode)
        => SingleEntityDbContext.Create(
            collection,
            modelBuilderAction: FlatModel,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });

    // MQL-capture idiom mirrored from NativeArrayProjectionTests: FunctionalTests has no TestMqlLoggerFactory /
    // AssertMql (those live in the SpecificationTests project), so MQL is captured through SpyLoggerProvider.
    private static SingleEntityDbContext<Blog> CreateContextWithLogging(
        IMongoCollection<Blog> collection, MongoQueryMode mode, out SpyLoggerProvider spyLogger)
    {
        var (loggerFactory, provider) = SpyLoggerProvider.Create();
        spyLogger = provider;

        return SingleEntityDbContext.Create(
            collection,
            loggerFactory,
            modelBuilderAction: BlogModel,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                b.EnableSensitiveDataLogging();
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });
    }

    private string UniqueCollectionName(string name)
        => TemporaryDatabaseFixtureBase.CreateCollectionName(name) + Guid.NewGuid().ToString("N")[..8];
}
