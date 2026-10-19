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
/// EF-382 — native predicate translator support for <c>arrayField.Contains(constant)</c>, the mirror shape
/// of <c>values.Contains(e.Field)</c> (native since EF-329 as <c>$in</c>). MongoDB's implicit array-element
/// match (<c>{ field: value }</c>) already means "value is an element of field" for an array-typed field, so
/// no new operator is needed — only resolving the receiver as a genuine stored array FIELD and the argument
/// as a value.
/// </summary>
/// <remarks>
/// The <see cref="Guid"/> case pins the correctness guard the ticket called out: the item's serializer MUST
/// be resolved from the array field's own ELEMENT serializer (<c>IBsonArraySerializer.TryGetItemSerializationInfo</c>),
/// never a blind <c>BsonValue.Create</c> over the CLR value — <c>BsonValue.Create(Guid)</c> itself throws
/// <see cref="ArgumentException"/> (confirmed empirically), so a naive implementation would crash outright for
/// this element type instead of matching the driver's own <c>GuidSerializer</c> encoding. The
/// value-converted-array-element case pins the sibling guard: a WHOLE-COLLECTION value converter (the only
/// value-converter shape this provider supports on a collection-typed property — there is no per-element
/// converter mechanism) has no per-element serializer to resolve safely, so the translator declines rather
/// than risk comparing the constant against the wrong (whole-list) shape.
/// </remarks>
[XUnitCollection("QueryTests")]
public class Ef382ArrayContainsTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    public class Row
    {
        public ObjectId Id { get; set; }
        public string Label { get; set; } = "";
        public List<string> Tags { get; set; } = [];
        public List<Guid> Codes { get; set; } = [];
    }

    private static readonly Guid CodeA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid CodeB = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid CodeC = Guid.Parse("33333333-3333-3333-3333-333333333333");

    // ---------------------------------------------------------------------------------------------------
    // 1: the capability — a stored List<string> field, a constant item.
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Array_field_contains_constant_goes_native_and_returns_correct_rows()
    {
        var collection = Seed(nameof(Array_field_contains_constant_goes_native_and_returns_correct_rows));

        using var native = CreateContext(collection, MongoQueryMode.Native);
        var nativeLabels = native.Entities
            .Where(e => e.Tags.Contains("keep"))
            .OrderBy(e => e.Label)
            .Select(e => e.Label)
            .ToList();
        Assert.Equal(["A", "C"], nativeLabels);

        // "Goes native" is proven by NativeOnly succeeding, never by MQL shape (identical $match either way).
        using var nativeOnly = CreateContext(collection, MongoQueryMode.NativeOnly);
        var nativeOnlyLabels = nativeOnly.Entities
            .Where(e => e.Tags.Contains("keep"))
            .OrderBy(e => e.Label)
            .Select(e => e.Label)
            .ToList();
        Assert.Equal(["A", "C"], nativeOnlyLabels);
    }

    // ---------------------------------------------------------------------------------------------------
    // 2: negation — !arrayField.Contains(constant) → { field: { $ne: value } }, the exact complement.
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Negated_array_field_contains_constant_goes_native_and_returns_correct_rows()
    {
        var collection = Seed(nameof(Negated_array_field_contains_constant_goes_native_and_returns_correct_rows));

        using var nativeOnly = CreateContext(collection, MongoQueryMode.NativeOnly);
        var labels = nativeOnly.Entities
            .Where(e => !e.Tags.Contains("keep"))
            .OrderBy(e => e.Label)
            .Select(e => e.Label)
            .ToList();

        Assert.Equal(["B"], labels);
    }

    // ---------------------------------------------------------------------------------------------------
    // 3: the correctness guard — an element type BsonValue.Create cannot handle (Guid). A naive
    // forSerialization=null implementation would throw ArgumentException; the correct one resolves the
    // array's own element serializer (GuidSerializer) and matches real stored documents.
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Array_field_of_guids_contains_constant_goes_native_and_returns_correct_rows()
    {
        var collection = Seed(nameof(Array_field_of_guids_contains_constant_goes_native_and_returns_correct_rows));

        using var nativeOnly = CreateContext(collection, MongoQueryMode.NativeOnly);
        var labels = nativeOnly.Entities
            .Where(e => e.Codes.Contains(CodeA))
            .OrderBy(e => e.Label)
            .Select(e => e.Label)
            .ToList();

        Assert.Equal(["A", "C"], labels);
    }

    // ---------------------------------------------------------------------------------------------------
    // 4: regression — the MIRROR shape (a client-side collection containing a stored FIELD, e.g.
    // values.Contains(e.Field)) must be entirely unaffected: it still goes native via $in.
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Values_contains_field_is_unaffected_and_still_goes_native_as_in()
    {
        var collection = Seed(nameof(Values_contains_field_is_unaffected_and_still_goes_native_as_in));
        var labelsToMatch = new[] { "A", "B" };

        using var nativeOnly = CreateContext(collection, MongoQueryMode.NativeOnly);
        var labels = nativeOnly.Entities
            .Where(e => labelsToMatch.Contains(e.Label))
            .OrderBy(e => e.Label)
            .Select(e => e.Label)
            .ToList();

        Assert.Equal(["A", "B"], labels);
    }

    // ---------------------------------------------------------------------------------------------------
    // 4b: end-to-end proof that MongoArrayContainsExpression survives MongoFieldPrefixRewriter — reachable
    // when the shape appears inside an owned SelectMany's inner filter, where the translated predicate gets
    // its field paths prefixed to the unwound-element document scope before rendering. Without the rewriter
    // arm this would throw NativeTranslationNotSupportedException from Rewrite's catch-all instead of
    // working end-to-end.
    // ---------------------------------------------------------------------------------------------------

    public class SelectManyOwner
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
        public List<SelectManyItem> Items { get; set; } = [];
    }

    public class SelectManyItem
    {
        public string Name { get; set; } = "";
        public List<string> Tags { get; set; } = [];
    }

    [Fact]
    public void Array_contains_inside_owned_select_many_inner_filter_goes_native()
    {
        var collectionName = TemporaryDatabaseFixtureBase.CreateCollectionName(
            nameof(Array_contains_inside_owned_select_many_inner_filter_goes_native)) + Guid.NewGuid().ToString("N")[..8];
        var mongoCollection = database.MongoDatabase.GetCollection<SelectManyOwner>(collectionName);
        mongoCollection.InsertMany([
            new SelectManyOwner
            {
                Name = "Alice",
                Items =
                [
                    new SelectManyItem { Name = "Widget", Tags = ["keep"] },
                    new SelectManyItem { Name = "Gadget", Tags = ["discard"] }
                ]
            },
            new SelectManyOwner { Name = "Bob", Items = [new SelectManyItem { Name = "Thing", Tags = ["keep"] }] }
        ]);

        using var nativeOnly = SingleEntityDbContext.Create(
            mongoCollection,
            modelBuilderAction: mb => mb.Entity<SelectManyOwner>().OwnsMany(o => o.Items),
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(MongoQueryMode.NativeOnly);
            });

        var names = nativeOnly.Entities
            .SelectMany(o => o.Items.Where(i => i.Tags.Contains("keep")), (o, i) => new { OwnerName = o.Name, ItemName = i.Name })
            .ToList()
            .Select(n => n.ItemName)
            .OrderBy(n => n)
            .ToList();

        Assert.Equal(["Thing", "Widget"], names);
    }

    // ---------------------------------------------------------------------------------------------------
    // 5: the decline guard — a WHOLE-COLLECTION value converter has no per-element serializer to resolve
    // safely, so the translator declines (falls back under Native; throws under NativeOnly) rather than
    // silently comparing the constant against the wrong shape.
    // ---------------------------------------------------------------------------------------------------

    public class ConvertedRow
    {
        public ObjectId Id { get; set; }
        public string Label { get; set; } = "";
        public List<Tier> Ratings { get; set; } = [];
    }

    public enum Tier { Bronze, Silver, Gold }

    [Fact]
    public void Whole_collection_value_converted_array_contains_declines_at_translate_time()
    {
        // This is a translate-time-only assertion (no data needed): the point is that
        // MongoExpressionTranslator's new arm DECLINES (returns null from TryTranslate) for a whole-collection
        // value-converted array — never that it silently emits a wrong-shape comparison. Confirmed via
        // NativeOnly, the only reliable "did this go native" signal (see the Query AGENTS.md "MQL shape cannot
        // prove a query went native" pitfall): a clean decline throws NativeTranslationNotSupportedException
        // from EF Core's own query compilation, before any document is read.
        //
        // Under the default Native mode this shape falls back to driver-LINQ exactly as it always has (even
        // before EF-382, the old $in arm already declined it — the item is a constant, not a resolvable
        // field) — and the driver's OWN LINQ v3 provider also cannot translate Contains over a
        // whole-collection-converted property (ArraySerializerHelper.GetItemSerializer requires
        // IBsonArraySerializer), a pre-existing, unrelated limitation this test does not re-assert.
        var collectionName = TemporaryDatabaseFixtureBase.CreateCollectionName(
            nameof(Whole_collection_value_converted_array_contains_declines_at_translate_time)) + Guid.NewGuid().ToString("N")[..8];
        var mongoCollection = database.MongoDatabase.GetCollection<ConvertedRow>(collectionName);

        Action<ModelBuilder> configureConverter = mb => mb.Entity<ConvertedRow>().Property(e => e.Ratings)
            .HasConversion(
                v => v.Select(x => x.ToString()).ToList(),
                v => v.Select(x => Enum.Parse<Tier>(x)).ToList());

        using var nativeOnly = SingleEntityDbContext.Create(
            mongoCollection,
            configureConverter,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(MongoQueryMode.NativeOnly);
            });

        Assert.Throws<NativeTranslationNotSupportedException>(
            () => nativeOnly.Entities.Where(e => e.Ratings.Contains(Tier.Gold)).ToList());
    }

    // ---------------------------------------------------------------------------------------------------
    // 6 (EF-382 review fix): p.ArrayField.Contains(constant) INSIDE an owned-collection Any/All quantifier.
    // Before this ticket the element-scoped translator's own Contains arm ($in) rejected a constant item
    // exactly like the top-level one did, so this shape declined; now the new arm admits it, which WIDENS
    // Any/All's native shape matrix (MongoExpressionTranslator's quantifier arm at ~line 457-517 calls
    // TryTranslate on the element predicate using an element-scoped translator, and — for All — negates it
    // via MongoExpressionNegator, which is exactly where MongoArrayContainsExpression's own negator arm
    // actually fires inside the real pipeline, not just in isolation). This was correct but untested and
    // unmentioned in the original report — this differential test closes that gap for both Any and All in
    // one shape, per the Query AGENTS.md differential-oracle pattern (same Expression sent to the server and,
    // compiled, evaluated in memory).
    // ---------------------------------------------------------------------------------------------------

    public class QuantifierOwner
    {
        public ObjectId Id { get; set; }
        public string Title { get; set; } = "";
        public List<QuantifierPost> Posts { get; set; } = [];
    }

    public class QuantifierPost
    {
        public string Heading { get; set; } = "";
        public List<string> Tags { get; set; } = [];
    }

    private static readonly Action<ModelBuilder> QuantifierModel =
        mb => mb.Entity<QuantifierOwner>().OwnsMany(o => o.Posts);

    public static TheoryData<string, System.Linq.Expressions.Expression<Func<QuantifierOwner, bool>>> QuantifierContainsCases() => new()
    {
        { "any",          o => o.Posts.Any(p => p.Tags.Contains("keep")) },
        { "all",          o => o.Posts.All(p => p.Tags.Contains("keep")) },
        { "any-negated",  o => !o.Posts.Any(p => p.Tags.Contains("keep")) },
        { "all-negated",  o => !o.Posts.All(p => p.Tags.Contains("keep")) },
    };

    [Theory]
    [MemberData(nameof(QuantifierContainsCases))]
    public void Quantifier_over_array_contains_equals_the_in_memory_oracle(
        string name, System.Linq.Expressions.Expression<Func<QuantifierOwner, bool>> predicate)
    {
        var collectionName = TemporaryDatabaseFixtureBase.CreateCollectionName(
            $"Quantifier_over_array_contains_{name}") + Guid.NewGuid().ToString("N")[..8];
        var mongoCollection = database.MongoDatabase.GetCollection<QuantifierOwner>(collectionName);
        mongoCollection.InsertMany([
            // Discriminates Any (true) from All (false): the FIRST post has "keep", the second doesn't.
            new QuantifierOwner
            {
                Title = "mixed",
                Posts = [new QuantifierPost { Tags = ["keep", "x"] }, new QuantifierPost { Tags = ["y"] }]
            },
            // Discriminates All (true): EVERY post has "keep".
            new QuantifierOwner
            {
                Title = "allKeep",
                Posts = [new QuantifierPost { Tags = ["keep"] }, new QuantifierPost { Tags = ["keep", "z"] }]
            },
            // Neither Any nor All is satisfied by any post.
            new QuantifierOwner
            {
                Title = "noneKeep",
                Posts = [new QuantifierPost { Tags = ["y"] }, new QuantifierPost { Tags = ["z"] }]
            },
            // Empty Posts: Any is vacuously false, All is vacuously true — the classic quantifier-over-empty
            // discriminator this codebase always seeds for exactly this reason.
            new QuantifierOwner { Title = "emptyPosts", Posts = [] },
            // A post whose Tags array is itself empty: Contains is false for that element, so it fails All
            // and contributes nothing to Any.
            new QuantifierOwner
            {
                Title = "emptyTagsElement",
                Posts = [new QuantifierPost { Tags = [] }]
            }
        ]);

        // Oracle: materialize every row, then evaluate the SAME predicate in memory.
        List<string> expected;
        using (var db = SingleEntityDbContext.Create(
                   mongoCollection, QuantifierModel,
                   optionsBuilderAction: b =>
                   {
                       b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                       new MongoDbContextOptionsBuilder(b).UseQueryMode(MongoQueryMode.Native);
                   }))
        {
            expected = db.Entities.AsNoTracking().ToList()
                .Where(predicate.Compile()).Select(o => o.Title).OrderBy(t => t).ToList();
        }

        // Server: the query must go NATIVE (NativeOnly is the only reliable "went native" signal) and agree
        // exactly with the in-memory oracle.
        List<string> actual;
        using (var db = SingleEntityDbContext.Create(
                   mongoCollection, QuantifierModel,
                   optionsBuilderAction: b =>
                   {
                       b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                       new MongoDbContextOptionsBuilder(b).UseQueryMode(MongoQueryMode.NativeOnly);
                   }))
        {
            actual = db.Entities.AsNoTracking().Where(predicate).ToList()
                .Select(o => o.Title).OrderBy(t => t).ToList();
        }

        Assert.Equal(expected, actual);
    }

    // ---------------------------------------------------------------------------------------------------
    // 7 (final whole-branch review): the CROSS-TASK interaction between this ticket's node type and the two
    // AGGREGATION-dialect positions EF-413 opened up via MongoExpressionTranslator.TranslateOperand's
    // Contains routing. MongoArrayContainsExpression has a QUERY-dialect rendering only
    // (MongoQueryLanguageRenderer.RenderArrayContains); MongoAggregationExpressionRenderer has NO arm for it
    // and refuses it at Render's catch-all / CanRender's `_ => false` — deliberately, since $eq over an array
    // field is whole-array equality, not element membership (see MongoArrayContainsExpression's own remarks).
    // Both positions were verified safe by inspection during the review but had no test; these two pin the
    // observable disposition of each, which is DIFFERENT for the two positions and is the point of the pair.
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Array_contains_as_a_computed_sort_key_declines_cleanly()
    {
        // Position 1: a computed SORT key. NativeSlotPopulator.TryTranslateComputedSortKey gates on
        // MongoAggregationExpressionRenderer.CanRender BEFORE recording the $set/$sort/$unset, and CanRender's
        // catch-all answers false for this node type — so the whole query is marked non-native at TRANSLATE
        // time. That is a decline, not a throw: under Native it falls back to driver-LINQ and returns the
        // right rows; under NativeOnly the gate turns the same decline into
        // NativeTranslationNotSupportedException.
        var collection = Seed(nameof(Array_contains_as_a_computed_sort_key_declines_cleanly));

        using var native = CreateContext(collection, MongoQueryMode.Native);
        var labels = native.Entities
            .OrderBy(e => e.Tags.Contains("keep"))
            .ThenBy(e => e.Label)
            .Select(e => e.Label)
            .ToList();

        // false sorts before true: B (no "keep") first, then A and C.
        Assert.Equal(["B", "A", "C"], labels);

        using var nativeOnly = CreateContext(collection, MongoQueryMode.NativeOnly);
        var ex = Assert.Throws<NativeTranslationNotSupportedException>(
            () => nativeOnly.Entities
                .OrderBy(e => e.Tags.Contains("keep"))
                .ThenBy(e => e.Label)
                .Select(e => e.Label)
                .ToList());
        // MEASURED: the message is the GATE's generic "forbids the driver-LINQ fallback" one, i.e. a
        // TRANSLATE-time decline — NOT MongoAggregationExpressionRenderer's "does not support node type"
        // render-time throw. That contrast with the filtered-count position below is the whole point of this
        // pair, so it is asserted rather than left to the exception type (which is the same for both).
        Assert.Contains("MongoQueryMode.NativeOnly forbids the driver-LINQ fallback", ex.Message);
        Assert.DoesNotContain("MongoAggregationExpressionRenderer", ex.Message);
    }

    [Fact]
    public void Array_contains_as_a_filtered_count_element_predicate_declines_at_render_time()
    {
        // Position 2: a filtered COUNT's element predicate — the deliberately GATE-FREE position (see
        // MongoExpressionTranslator's count branch and MongoAggregationExpressionRenderer.RenderUnary's
        // remarks: a translate-time null there hard-fails the whole leaf in EVERY mode, so that branch
        // intentionally lets the render throw instead). MongoArrayContainsExpression therefore survives
        // translation, is handed to MongoAggregationExpressionRenderer inside the $filter cond, and hits
        // Render's catch-all: NativeTranslationNotSupportedException.
        //
        // MEASURED, not assumed: that render-time throw is caught by
        // MongoShapedQueryCompilingExpressionVisitor.TryBuildPipeline's
        // `catch (NativeTranslationNotSupportedException) when (mode != NativeOnly)`, so this position
        // GRACEFULLY FALLS BACK under Native (correct rows via driver-LINQ) and surfaces the exception only
        // under NativeOnly — it is NOT the "hard-fails in every mode" family. That asymmetry is precisely
        // what the gate-free design buys, and it is why this test asserts a working Native result rather
        // than a throw.
        var collectionName = TemporaryDatabaseFixtureBase.CreateCollectionName(
            nameof(Array_contains_as_a_filtered_count_element_predicate_declines_at_render_time))
            + Guid.NewGuid().ToString("N")[..8];
        var mongoCollection = database.MongoDatabase.GetCollection<QuantifierOwner>(collectionName);
        mongoCollection.InsertMany([
            new QuantifierOwner
            {
                Title = "mixed",
                Posts = [new QuantifierPost { Tags = ["keep", "x"] }, new QuantifierPost { Tags = ["y"] }]
            },
            new QuantifierOwner
            {
                Title = "noneKeep",
                Posts = [new QuantifierPost { Tags = ["y"] }, new QuantifierPost { Tags = ["z"] }]
            }
        ]);

        using (var native = CreateQuantifierContext(mongoCollection, MongoQueryMode.Native))
        {
            var counts = native.Entities
                .OrderBy(o => o.Title)
                .Select(o => new {o.Title, N = o.Posts.Count(p => p.Tags.Contains("keep"))})
                .ToList();

            Assert.Equal([("mixed", 1), ("noneKeep", 0)], counts.Select(c => (c.Title, c.N)));
        }

        using (var nativeOnly = CreateQuantifierContext(mongoCollection, MongoQueryMode.NativeOnly))
        {
            var ex = Assert.Throws<NativeTranslationNotSupportedException>(
                () => nativeOnly.Entities
                    .OrderBy(o => o.Title)
                    .Select(o => new {o.Title, N = o.Posts.Count(p => p.Tags.Contains("keep"))})
                    .ToList());
            // Pins WHICH gate answered — measured, so the test cannot silently start passing because some
            // earlier, unrelated decline began firing first.
            Assert.Contains("MongoAggregationExpressionRenderer does not support node type "
                + "'MongoArrayContainsExpression'", ex.Message);
        }
    }

    private static SingleEntityDbContext<QuantifierOwner> CreateQuantifierContext(
        IMongoCollection<QuantifierOwner> collection, MongoQueryMode mode)
        => SingleEntityDbContext.Create(
            collection,
            QuantifierModel,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });

    // ── Seed and helpers ────────────────────────────────────────────────────────────────────────────

    private IMongoCollection<Row> Seed(string name)
    {
        var collection = database.MongoDatabase.GetCollection<Row>(UniqueCollectionName(name));
        collection.InsertMany([
            new Row { Label = "A", Tags = ["keep", "other"], Codes = [CodeA, CodeB] },
            new Row { Label = "B", Tags = ["other"], Codes = [CodeB] },
            new Row { Label = "C", Tags = ["keep"], Codes = [CodeA, CodeC] }
        ]);
        return collection;
    }

    private string UniqueCollectionName(string name)
        => TemporaryDatabaseFixtureBase.CreateCollectionName(name) + Guid.NewGuid().ToString("N")[..8];

    private static SingleEntityDbContext<Row> CreateContext(IMongoCollection<Row> collection, MongoQueryMode mode)
        => SingleEntityDbContext.Create(
            collection,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });
}
