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
/// Bare-leaf selectors (<c>Select(b =&gt; b.Title)</c>, <c>b.Posts</c>, <c>b.Id</c>) emit a native <c>$project</c>.
/// Routing is proven by <see cref="MongoQueryMode.NativeOnly"/>, never by MQL shape.
/// </summary>
/// <remarks>
/// Each leaf kind has a parameterized-<c>Where</c> leg: a captured local in <c>StartsWith</c> makes the native
/// factory decline late under <see cref="MongoQueryMode.Native"/>, handing the native shaper a driver-rendered
/// pipeline (whose bare alias is <c>_v</c>). Legs assert values, because that failure is silent (null scalars,
/// empty arrays) for everything except non-nullable value types.
/// </remarks>
[XUnitCollection("QueryTests")]
public class NativeBareProjectionTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    // No `= []` initializers on collection navigations, so null-vs-empty is observable. Mixes nullable and
    // non-nullable leaves because only non-nullable ones fail loudly on an alias miss.
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

    // Owned-hop scalar: the document path is dotted ("Home.City"), which a $project alias can't round-trip
    // (read back as a literal key, rendered as a nested document), so the bare arm declines. See EF-362.
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

    // Separate flat model for test 14: raw-seeded owned elements have no owner FK, which breaks whole-entity
    // queries over the ragged fixture for unrelated reasons.
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

        // Captured `prefix` makes the native renderer refuse the regex term, so TryBuildNativeFactory declines
        // late and the shaper gets a driver-rendered pipeline. Correct only because the late-fallback strip
        // removes the pushed-down Select, leaving whole documents.
        var prefix = "p";

        // Default Native mode: NativeOnly throws on the decline and DriverLinq never builds a native factory.
        using var db = CreateContext(collection, MongoQueryMode.Native);

        // Nullable leaves run first: without the strip they silently return all nulls, while Title throws;
        // running Title first would hide the silent half.
        var notes = db.Entities.AsNoTracking()
            .Where(b => b.Title.StartsWith(prefix)).OrderBy(b => b.Title)
            .Select(b => b.Note).ToList();
        Assert.Equal(["n1", null, "n3", null, "n5"], notes);

        var scores = db.Entities.AsNoTracking()
            .Where(b => b.Title.StartsWith(prefix)).OrderBy(b => b.Title)
            .Select(b => b.Score).ToList();
        Assert.Equal([10, null, 30, null, 50], scores);

        // The non-nullable leaf, which fails loudly.
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

        // Not a routing proof. Pins the alias: the PK element is `_id`, so RenderProject must not also add
        // `_id : 0` (an invalid inclusion/exclusion mix).
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

        // The silent case: under DriverLinq, EF won't push down an entity/collection leaf, so the shaper reads
        // whole documents and is correct only because the alias equals the element name. Any other alias yields
        // empty collections.
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

        // Not a routing proof; pins the emitted alias plus the owner key an array leaf must carry so
        // shadow-keyed elements can materialize.
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

        // Two distinct documents share "p2": whole-entity dedup before the trailing $project keeps both, while
        // dedup over the projected value would collapse them.
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
        // No DriverLinq leg: the driver doesn't translate cross-view Intersect/Except, so assert against the seed.
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

    // ── 13. Owned-hop scalar declines (EF-362) ────────────────────────────────────

    [Fact]
    public void Bare_owned_hop_scalar_projection_declines()
    {
        var collection = SeedHop(nameof(Bare_owned_hop_scalar_projection_declines));

        // Deliberate decline: a dotted alias ("Home.City") is read by the shaper as a literal key but rendered by
        // $project as a nested document.
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

        // `Select(x => x)` is returned unchanged at the top of TranslateSelect and never reaches the binder.
        using var db = CreateFlatContext(collection, MongoQueryMode.NativeOnly);

        Assert.Equal(
            ["a", "b", "c"],
            db.Entities.AsNoTracking().OrderBy(b => b.Title).Select(x => x).ToList().Select(b => b.Title));

        Assert.Equal(
            ["b", "c"],
            db.Entities.AsNoTracking().Where(b => b.Rank >= 2).OrderBy(b => b.Title).Select(x => x).ToList()
                .Select(b => b.Title));
    }

    // ── 15. Bare projected set-op operand goes native ─────────────────────────────

    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.DriverLinq)]
    [InlineData(MongoQueryMode.NativeOnly)]
    public void Bare_projected_union_and_concat_operands_go_native_and_return_correct_values(MongoQueryMode mode)
    {
        var collection = SeedSetOps(
            nameof(Bare_projected_union_and_concat_operands_go_native_and_return_correct_values) + mode);
        using var db = CreateContext(collection, mode);

        // IsPlainProjectedSelect admits a bare operand like a wrapped one. For a scalar leaf the projected
        // document is exactly the compared value, so whole-document dedup equals value dedup; array leaves are
        // still declined by HasArrayProjectionLeaf. Unlike test 8, the two "p2" documents collapse to one.
        Assert.Equal(
            ["p2", "q0", "r_mid", "s_hi"],
            Sorted(db.Entities.AsNoTracking().Where(b => b.Rank <= 3).Select(b => b.Title)
                .Union(db.Entities.AsNoTracking().Where(b => b.Rank >= 3).Select(b => b.Title))));

        Assert.Equal(
            ["p2", "p2", "q0", "r_mid", "r_mid", "s_hi"],
            Sorted(db.Entities.AsNoTracking().Where(b => b.Rank <= 3).Select(b => b.Title)
                .Concat(db.Entities.AsNoTracking().Where(b => b.Rank >= 3).Select(b => b.Title))));
    }

    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.NativeOnly)]
    public void Bare_projected_intersect_and_except_operands_go_native_and_return_correct_values(MongoQueryMode mode)
    {
        // No DriverLinq leg: the driver doesn't translate cross-view Intersect/Except (see test 8).
        var collection = SeedSetOps(
            nameof(Bare_projected_intersect_and_except_operands_go_native_and_return_correct_values) + mode);
        using var db = CreateContext(collection, mode);

        // No driver-LINQ baseline. op1 = {p2, q0, r_mid}, op2 = {p2, r_mid, s_hi}; Intersect = {p2, r_mid}, Except = {q0}.
        Assert.Equal(
            ["p2", "r_mid"],
            Sorted(db.Entities.AsNoTracking().Where(b => b.Rank <= 3).Select(b => b.Title)
                .Intersect(db.Entities.AsNoTracking().Where(b => b.Rank >= 3).Select(b => b.Title))));

        Assert.Equal(
            ["q0"],
            Sorted(db.Entities.AsNoTracking().Where(b => b.Rank <= 3).Select(b => b.Title)
                .Except(db.Entities.AsNoTracking().Where(b => b.Rank >= 3).Select(b => b.Title))));
    }

    // Test 15's safety rests on HasArrayProjectionLeaf declining a bare owned-array operand; the
    // NativeArrayProjectionTests guards only cover the wrapped spelling, so this pins the bare one.
    [Fact]
    public void Bare_owned_array_projection_operand_still_declines_under_native_only()
    {
        var collection = SeedSetOps(nameof(Bare_owned_array_projection_operand_still_declines_under_native_only));
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly);

        // Union has a driver-LINQ baseline, so a declined shape falls back gracefully under Native and only
        // throws NativeTranslationNotSupportedException under NativeOnly.
        Assert.Throws<NativeTranslationNotSupportedException>(
            () => db.Entities.AsNoTracking().Where(b => b.Rank <= 3).Select(b => b.Posts)
                .Union(db.Entities.AsNoTracking().Where(b => b.Rank >= 3).Select(b => b.Posts)).ToList());

        // Intersect/Except have no driver-LINQ baseline, so a decline fails in every mode via
        // TryTranslateSetOperation's null return (InvalidOperationException).
        Assert.Throws<InvalidOperationException>(
            () => db.Entities.AsNoTracking().Where(b => b.Rank <= 3).Select(b => b.Posts)
                .Intersect(db.Entities.AsNoTracking().Where(b => b.Rank >= 3).Select(b => b.Posts)).ToList());

        Assert.Throws<InvalidOperationException>(
            () => db.Entities.AsNoTracking().Where(b => b.Rank <= 3).Select(b => b.Posts)
                .Except(db.Entities.AsNoTracking().Where(b => b.Rank >= 3).Select(b => b.Posts)).ToList());
    }

    // ── 16. Bare projection then Distinct goes native ─────────────────────────────

    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.DriverLinq)]
    [InlineData(MongoQueryMode.NativeOnly)]
    public void Bare_projection_then_Distinct_goes_native_and_returns_correct_values(MongoQueryMode mode)
    {
        var collection = SeedSetOps(nameof(Bare_projection_then_Distinct_goes_native_and_returns_correct_values) + mode);
        using var db = CreateContext(collection, mode);

        // Distinct flips Route to GroupBy, so ApplyProjection also honors the alias override when
        // Select.IsDistinct is set (only TryBindDistinctFromProjection sets it, and the flatten re-adds the same
        // alias); otherwise the alias reverts to null and the shaper crashes.
        Assert.Equal(
            ["p2", "q0", "r_mid", "s_hi"],
            Sorted(db.Entities.AsNoTracking().Select(b => b.Title).Distinct()));
    }

    // ── Minimal set-op / Distinct probes ──────────────────────────────────────────

    [Fact]
    public void Bare_projection_as_set_operation_operand_goes_native()
    {
        var collection = SeedSetOps(nameof(Bare_projection_as_set_operation_operand_goes_native));
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly);

        var titles = db.Entities.AsNoTracking().Select(b => b.Title)
            .Union(db.Entities.AsNoTracking().Select(b => b.Title))
            .ToList();
        Assert.NotEmpty(titles);
    }

    [Fact]
    public void Bare_projection_then_Distinct_goes_native()
    {
        var collection = SeedSetOps(nameof(Bare_projection_then_Distinct_goes_native));
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly);

        var titles = db.Entities.AsNoTracking().Select(b => b.Title).Distinct().ToList();
        Assert.NotEmpty(titles);
    }

    // ── 17. Cardinality operator after a bare projection ──────────────────────────

    [Fact]
    public void Bare_projection_then_cardinality_operator_goes_native()
    {
        var collection = SeedRagged(nameof(Bare_projection_then_cardinality_operator_goes_native));

        // Neither bare-arm narrowing is on the cardinality path, so this needs its own pin.
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

        // The bare arm declines when Projection is already populated, which keeps the alias override write-once
        // (AddProjectionAliasOverride uses Dictionary.Add). EF may fuse the two Selects before the provider sees
        // them, so assert on values either way.
        Assert.Equal(
            [6, 8, 10, 7, 6],
            db.Entities.AsNoTracking().OrderBy(b => b.Title).Select(b => b.Title).Select(t => t.Length).ToList());
    }

    // ── Seeds and helpers ─────────────────────────────────────────────────────────

    // The array states: populated, empty, missing, explicit BSON null, plus a fifth populated row. Every Title
    // starts with "p", so parameterized-Where legs select all rows and share expectations.
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

        // Self-check the seed: missing vs. null are indistinguishable from results alone.
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

    // null `tags`/`posts` omits the element; BsonNull.Value writes explicit null. Note/Score are omitted when
    // null so nullable leaves are tested against a truly absent element.
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

    // Two distinct documents share Title "p2" (different Rank): whole-entity dedup (test 8) keeps both, value
    // dedup (test 15) collapses them, so the shapes give different answers.
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

    // Set operations don't preserve order, so set-op assertions sort client-side.
    private static List<string> Sorted(IQueryable<string> query)
        => query.ToList().OrderBy(t => t, StringComparer.Ordinal).ToList();

    // Printed rather than compared structurally, so null vs "" and absent vs empty stay distinguishable.
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

    // FunctionalTests has no AssertMql, so MQL is captured via SpyLoggerProvider (as in NativeArrayProjectionTests).
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
