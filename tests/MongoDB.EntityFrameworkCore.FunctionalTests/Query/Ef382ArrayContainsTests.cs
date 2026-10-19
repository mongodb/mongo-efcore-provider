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
/// Native <c>arrayField.Contains(constant)</c>, the mirror of <c>values.Contains(e.Field)</c> (<c>$in</c>).
/// Translates to MongoDB's implicit array-element match <c>{ field: value }</c>.
/// </summary>
/// <remarks>
/// The item must be serialized with the array's element serializer (<c>IBsonArraySerializer</c>), not
/// <c>BsonValue.Create</c>, which throws for <see cref="Guid"/>. A whole-collection value converter has no
/// per-element serializer, so the translator declines.
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

        // Went native is proven by NativeOnly succeeding, not by MQL shape (identical either way).
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
    // 3: Guid element: BsonValue.Create would throw; the array's element serializer (GuidSerializer) must be
    // used and must match real stored documents.
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
    // 4: the mirror shape values.Contains(e.Field) still goes native via $in.
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
    // 4b: inside an owned SelectMany's inner filter, the predicate's paths are rewritten by
    // MongoFieldPrefixRewriter, which must have an arm for MongoArrayContainsExpression (otherwise it throws).
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
    // 5: a whole-collection value converter has no per-element serializer, so the translator declines (falls
    // back under Native, throws under NativeOnly) rather than comparing against the wrong shape.
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
        // Translate-time only: NativeOnly turns the decline into NativeTranslationNotSupportedException before any
        // document is read. (Under Native, driver-LINQ can't translate this either — a separate limitation.)
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
    // 6: arrayField.Contains(constant) inside an owned-collection Any/All. For All the element predicate is
    // negated via MongoExpressionNegator, exercising MongoArrayContainsExpression's negator arm in the real
    // pipeline. Differential test: same expression evaluated on the server and in memory.
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
            // Empty Posts: Any is vacuously false, All vacuously true.
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

        // Server: must go native (NativeOnly) and agree with the in-memory oracle.
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
    // 7: MongoArrayContainsExpression has only a query-dialect rendering; the aggregation renderer refuses it
    // (on purpose: $eq over an array is whole-array equality, not membership). These pin how the two
    // aggregation-dialect positions reached via TranslateOperand's Contains routing handle it — differently.
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Array_contains_as_a_computed_sort_key_declines_cleanly()
    {
        // Computed sort key: TryTranslateComputedSortKey gates on MongoAggregationExpressionRenderer.CanRender,
        // which is false here, so this is a translate-time decline: falls back under Native, throws under
        // NativeOnly.
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
        // The message is the gate's "forbids the driver-LINQ fallback" one (a translate-time decline), not the
        // renderer's "does not support node type" throw; that contrast with the count position below is the point.
        Assert.Contains("MongoQueryMode.NativeOnly forbids the driver-LINQ fallback", ex.Message);
        Assert.DoesNotContain("MongoAggregationExpressionRenderer", ex.Message);
    }

    [Fact]
    public void Array_contains_as_a_filtered_count_element_predicate_declines_at_render_time()
    {
        // Filtered count's element predicate: this position is intentionally gate-free (a translate-time null
        // there would hard-fail the whole leaf in every mode — see RenderUnary's remarks), so the node reaches the
        // renderer and throws NativeTranslationNotSupportedException at render time. TryBuildPipeline catches that
        // when mode != NativeOnly, so Native falls back gracefully and only NativeOnly throws.
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
            // Pins which gate answered, so an unrelated earlier decline can't make this pass.
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
