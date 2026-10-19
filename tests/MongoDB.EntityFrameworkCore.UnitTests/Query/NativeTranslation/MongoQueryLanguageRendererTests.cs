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

using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Metadata;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Query.Expressions;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query.NativeTranslation;

/// <summary>
/// Unit tests for <see cref="MongoQueryLanguageRenderer"/>, which renders dialect-agnostic
/// <see cref="MongoExpression"/> predicates into MongoDB <c>$match</c>-dialect BSON filter bodies.
/// </summary>
public class MongoQueryLanguageRendererTests
{
    // --- Entity model used across tests ---

    private class Customer
    {
        public MongoDB.Bson.ObjectId Id { get; set; }
        public int Age { get; set; }
        public bool Active { get; set; }
        public int Score { get; set; }
        public string Name { get; set; } = null!;
    }

    private static IProperty GetProperty<T>(string propertyName) where T : class
    {
        using var db = SingleEntityDbContext.Create<T>();
        return db.Model.FindEntityType(typeof(T))!.FindProperty(propertyName)!;
    }

    private class Blog
    {
        public MongoDB.Bson.ObjectId Id { get; set; }
        public string Title { get; set; } = null!;
        public List<Post> Posts { get; set; } = [];
    }

    private class Post
    {
        public string Heading { get; set; } = null!;
        public int Rank { get; set; }
        public int Other { get; set; }
    }

    // A property of the owned COLLECTION ELEMENT type (Post), for building element-relative field refs.
    private static IProperty GetPostProperty(string propertyName)
    {
        using var db = SingleEntityDbContext.Create<Blog>(mb => mb.Entity<Blog>().OwnsMany(b => b.Posts));
        return db.Model.FindEntityType(typeof(Blog))!
            .FindNavigation(nameof(Blog.Posts))!.TargetEntityType.FindProperty(propertyName)!;
    }

    // ------------------------------------------------------------------
    // Test 1: simple GreaterThan comparison → { Age: { $gt: 21 } }
    // ------------------------------------------------------------------

    [Fact]
    public void Renders_greater_than_in_query_dialect()
    {
        var ageProperty = GetProperty<Customer>("Age");
        var field = new MongoFieldExpression(ageProperty, "Age");
        var pred = new MongoBinaryExpression(
            MongoBinaryOperator.GreaterThan,
            field,
            new MongoConstantExpression(21, ageProperty));

        var rendered = new MongoQueryLanguageRenderer().Render(pred, new PlaceholderTable());

        Assert.Equal(BsonDocument.Parse("{ Age: { $gt: 21 } }"), rendered);
    }

    // ------------------------------------------------------------------
    // Test 2: AndAlso of two ranges on the same field merges operator docs
    //         Age > 21 && Age < 65 → { Age: { $gt: 21, $lt: 65 } }
    // ------------------------------------------------------------------

    [Fact]
    public void Merges_two_ranges_on_one_field()
    {
        var ageProperty = GetProperty<Customer>("Age");
        var field = new MongoFieldExpression(ageProperty, "Age");
        var pred = new MongoBinaryExpression(
            MongoBinaryOperator.AndAlso,
            new MongoBinaryExpression(
                MongoBinaryOperator.GreaterThan,
                field,
                new MongoConstantExpression(21, ageProperty)),
            new MongoBinaryExpression(
                MongoBinaryOperator.LessThan,
                field,
                new MongoConstantExpression(65, ageProperty)));

        var rendered = new MongoQueryLanguageRenderer().Render(pred, new PlaceholderTable());

        Assert.Equal(BsonDocument.Parse("{ Age: { $gt: 21, $lt: 65 } }"), rendered);
    }

    // ------------------------------------------------------------------
    // Test 3: Equal comparison → bare { Age: value } (no $eq wrapper)
    // ------------------------------------------------------------------

    [Fact]
    public void Renders_equal_as_bare_value()
    {
        var ageProperty = GetProperty<Customer>("Age");
        var field = new MongoFieldExpression(ageProperty, "Age");
        var pred = new MongoBinaryExpression(
            MongoBinaryOperator.Equal,
            field,
            new MongoConstantExpression(30, ageProperty));

        var rendered = new MongoQueryLanguageRenderer().Render(pred, new PlaceholderTable());

        Assert.Equal(BsonDocument.Parse("{ Age: 30 }"), rendered);
    }

    // ------------------------------------------------------------------
    // Test 4: NotEqual → { Age: { $ne: value } }
    // ------------------------------------------------------------------

    [Fact]
    public void Renders_not_equal_with_ne_operator()
    {
        var ageProperty = GetProperty<Customer>("Age");
        var field = new MongoFieldExpression(ageProperty, "Age");
        var pred = new MongoBinaryExpression(
            MongoBinaryOperator.NotEqual,
            field,
            new MongoConstantExpression(0, ageProperty));

        var rendered = new MongoQueryLanguageRenderer().Render(pred, new PlaceholderTable());

        Assert.Equal(BsonDocument.Parse("{ Age: { $ne: 0 } }"), rendered);
    }

    // ------------------------------------------------------------------
    // Test 5: LessThanOrEqual → { Age: { $lte: value } }
    // ------------------------------------------------------------------

    [Fact]
    public void Renders_less_than_or_equal()
    {
        var ageProperty = GetProperty<Customer>("Age");
        var field = new MongoFieldExpression(ageProperty, "Age");
        var pred = new MongoBinaryExpression(
            MongoBinaryOperator.LessThanOrEqual,
            field,
            new MongoConstantExpression(100, ageProperty));

        var rendered = new MongoQueryLanguageRenderer().Render(pred, new PlaceholderTable());

        Assert.Equal(BsonDocument.Parse("{ Age: { $lte: 100 } }"), rendered);
    }

    // ------------------------------------------------------------------
    // Test 6: GreaterThanOrEqual → { Age: { $gte: value } }
    // ------------------------------------------------------------------

    [Fact]
    public void Renders_greater_than_or_equal()
    {
        var ageProperty = GetProperty<Customer>("Age");
        var field = new MongoFieldExpression(ageProperty, "Age");
        var pred = new MongoBinaryExpression(
            MongoBinaryOperator.GreaterThanOrEqual,
            field,
            new MongoConstantExpression(18, ageProperty));

        var rendered = new MongoQueryLanguageRenderer().Render(pred, new PlaceholderTable());

        Assert.Equal(BsonDocument.Parse("{ Age: { $gte: 18 } }"), rendered);
    }

    // ------------------------------------------------------------------
    // Test 7: OrElse → { $or: [ { Age: { $lt: 18 } }, { Age: { $gt: 65 } } ] }
    // ------------------------------------------------------------------

    [Fact]
    public void Renders_or_else_as_or_array()
    {
        var ageProperty = GetProperty<Customer>("Age");
        var field = new MongoFieldExpression(ageProperty, "Age");
        var pred = new MongoBinaryExpression(
            MongoBinaryOperator.OrElse,
            new MongoBinaryExpression(
                MongoBinaryOperator.LessThan,
                field,
                new MongoConstantExpression(18, ageProperty)),
            new MongoBinaryExpression(
                MongoBinaryOperator.GreaterThan,
                field,
                new MongoConstantExpression(65, ageProperty)));

        var rendered = new MongoQueryLanguageRenderer().Render(pred, new PlaceholderTable());

        Assert.Equal(
            BsonDocument.Parse("{ $or: [ { Age: { $lt: 18 } }, { Age: { $gt: 65 } } ] }"),
            rendered);
    }

    // ------------------------------------------------------------------
    // Test 8: bare bool field → { Active: true }
    // ------------------------------------------------------------------

    [Fact]
    public void Renders_bare_bool_field_as_true()
    {
        var activeProperty = GetProperty<Customer>("Active");
        var field = new MongoFieldExpression(activeProperty, "Active");

        var rendered = new MongoQueryLanguageRenderer().Render(field, new PlaceholderTable());

        Assert.Equal(BsonDocument.Parse("{ Active: true }"), rendered);
    }

    // ------------------------------------------------------------------
    // Test 9: Not(bool field) → { Active: { $ne: true } }
    // ------------------------------------------------------------------

    [Fact]
    public void Renders_not_bool_field_as_ne_true()
    {
        var activeProperty = GetProperty<Customer>("Active");
        var field = new MongoFieldExpression(activeProperty, "Active");
        var pred = new MongoUnaryExpression(MongoUnaryOperator.Not, field);

        var rendered = new MongoQueryLanguageRenderer().Render(pred, new PlaceholderTable());

        Assert.Equal(BsonDocument.Parse("{ Active: { $ne: true } }"), rendered);
    }

    // ------------------------------------------------------------------
    // Test 10: B2 parameter placeholder — renders sentinel, records in PlaceholderTable
    // ------------------------------------------------------------------

    [Fact]
    public void Renders_parameter_as_sentinel_and_records_in_placeholder_table()
    {
        var ageProperty = GetProperty<Customer>("Age");
        var field = new MongoFieldExpression(ageProperty, "Age");
        var pred = new MongoBinaryExpression(
            MongoBinaryOperator.GreaterThan,
            field,
            new MongoParameterExpression("p0", ageProperty));

        var placeholders = new PlaceholderTable();
        var rendered = new MongoQueryLanguageRenderer().Render(pred, placeholders);

        // The placeholder table must record one entry named "p0" with a non-null serializer.
        Assert.Single(placeholders.Entries);
        Assert.Equal("p0", placeholders.Entries[0].Name);
        Assert.NotNull(placeholders.Entries[0].Serializer);

        // The rendered body must be { Age: { $gt: <sentinel> } } where the sentinel is
        // a placeholder marker document that TryGetPlaceholderIndex recognises as index 0.
        var rendered_doc = Assert.IsType<BsonDocument>(rendered);
        var ageCond = Assert.IsType<BsonDocument>(rendered_doc["Age"]);
        var sentinelValue = ageCond["$gt"];
        Assert.True(PlaceholderTable.TryGetPlaceholderIndex(sentinelValue, out var index));
        Assert.Equal(0, index);
    }

    // ------------------------------------------------------------------
    // Test 11: AndAlso with two different fields — no merge, remain flat { f1: ..., f2: ... }
    // ------------------------------------------------------------------

    [Fact]
    public void And_with_two_different_fields_stays_flat()
    {
        var ageProperty = GetProperty<Customer>("Age");
        var activeProperty = GetProperty<Customer>("Active");

        var pred = new MongoBinaryExpression(
            MongoBinaryOperator.AndAlso,
            new MongoBinaryExpression(
                MongoBinaryOperator.GreaterThan,
                new MongoFieldExpression(ageProperty, "Age"),
                new MongoConstantExpression(21, ageProperty)),
            new MongoFieldExpression(activeProperty, "Active"));

        var rendered = new MongoQueryLanguageRenderer().Render(pred, new PlaceholderTable());

        Assert.Equal(BsonDocument.Parse("{ Age: { $gt: 21 }, Active: true }"), rendered);
    }

    // ------------------------------------------------------------------
    // Test 12: MongoConstantExpression with null ForSerialization (Skip/Take count)
    //          → BsonValue.Create(value), no throw
    // ------------------------------------------------------------------

    [Fact]
    public void Renders_constant_with_null_ForSerialization_as_BsonValue()
    {
        // MongoConstantExpression(5, forSerialization: null) — Skip/Take count
        var constant = new MongoConstantExpression(5, forSerialization: null);
        var placeholders = new PlaceholderTable();

        var result = MongoValueRenderer.RenderValue(constant, placeholders);

        Assert.Equal(new BsonInt32(5), result);
    }

    // ------------------------------------------------------------------
    // Test 13: MongoParameterExpression with null ForSerialization (Skip/Take count)
    //          → placeholder with null serializer, no throw
    // ------------------------------------------------------------------

    [Fact]
    public void Renders_parameter_with_null_ForSerialization_as_null_serializer_placeholder()
    {
        // MongoParameterExpression("p", forSerialization: null) — Skip/Take count
        var parameter = new MongoParameterExpression("p", forSerialization: null);
        var placeholders = new PlaceholderTable();

        var result = MongoValueRenderer.RenderValue(parameter, placeholders);

        // Must record a placeholder entry with null serializer.
        Assert.Single(placeholders.Entries);
        Assert.Equal("p", placeholders.Entries[0].Name);
        Assert.Null(placeholders.Entries[0].Serializer);

        // The returned value must be a valid sentinel.
        Assert.True(PlaceholderTable.TryGetPlaceholderIndex(result, out var index));
        Assert.Equal(0, index);
    }

    // ------------------------------------------------------------------
    // Test 14: field-to-field comparison → { $expr: { $eq: ['$Age', '$Score'] } }
    // ------------------------------------------------------------------

    [Fact]
    public void Field_to_field_comparison_renders_as_expr()
    {
        var age = GetProperty<Customer>("Age");
        var score = GetProperty<Customer>("Score");
        var pred = new MongoBinaryExpression(MongoBinaryOperator.Equal,
            new MongoFieldExpression(age, "Age"), new MongoFieldExpression(score, "Score"));

        var rendered = new MongoQueryLanguageRenderer().Render(pred, new PlaceholderTable());

        Assert.Equal(BsonDocument.Parse("{ $expr: { $eq: ['$Age', '$Score'] } }"), rendered);
    }

    // ------------------------------------------------------------------
    // Test 15: mixed AND keeps the indexable branch in query dialect
    // ------------------------------------------------------------------

    [Fact]
    public void Mixed_and_keeps_indexable_branch_in_query_dialect()
    {
        var age = GetProperty<Customer>("Age");
        var score = GetProperty<Customer>("Score");
        // (Age == Score) && (Age > 20)
        var pred = new MongoBinaryExpression(MongoBinaryOperator.AndAlso,
            new MongoBinaryExpression(MongoBinaryOperator.Equal,
                new MongoFieldExpression(age, "Age"), new MongoFieldExpression(score, "Score")),
            new MongoBinaryExpression(MongoBinaryOperator.GreaterThan,
                new MongoFieldExpression(age, "Age"), new MongoConstantExpression(20, age)));

        var rendered = new MongoQueryLanguageRenderer().Render(pred, new PlaceholderTable());

        Assert.Equal(
            BsonDocument.Parse("{ $and: [ { $expr: { $eq: ['$Age', '$Score'] } }, { Age: { $gt: 20 } } ] }"),
            rendered);
    }

    // ------------------------------------------------------------------
    // Test 16: `== null` renders as a bare null value → { Name: null }
    // Matches the driver-LINQ fallback's rendering, which also relies on
    // MongoDB's standard { field: null } semantics (matches explicit null
    // OR a missing field) for `== null` predicates.
    // ------------------------------------------------------------------

    [Fact]
    public void Renders_is_null_as_bare_null()
    {
        var name = GetProperty<Customer>("Name");
        var pred = new MongoBinaryExpression(MongoBinaryOperator.Equal,
            new MongoFieldExpression(name, "Name"), new MongoConstantExpression(null, name));

        var rendered = new MongoQueryLanguageRenderer().Render(pred, new PlaceholderTable());

        Assert.Equal(BsonDocument.Parse("{ Name: null }"), rendered);
    }

    // ------------------------------------------------------------------
    // Test 17: MongoInExpression over an inline constant collection → { field: { $in: [...] } }
    // ------------------------------------------------------------------

    [Fact]
    public void Renders_in_for_inline_collection()
    {
        var age = GetProperty<Customer>("Age");
        var expr = new MongoInExpression(
            new MongoFieldExpression(age, "Age"),
            new MongoConstantExpression(new[] { 1, 2, 3 }, age),
            negated: false);
        var rendered = new MongoQueryLanguageRenderer().Render(expr, new PlaceholderTable());
        Assert.Equal(BsonDocument.Parse("{ Age: { $in: [1, 2, 3] } }"), rendered);
    }

    // ------------------------------------------------------------------
    // Test 18: negated MongoInExpression over an inline constant collection → { field: { $nin: [...] } }
    // ------------------------------------------------------------------

    [Fact]
    public void Renders_nin_for_negated_inline_collection()
    {
        var age = GetProperty<Customer>("Age");
        var expr = new MongoInExpression(
            new MongoFieldExpression(age, "Age"),
            new MongoConstantExpression(new[] { 1, 2, 3 }, age),
            negated: true);
        var rendered = new MongoQueryLanguageRenderer().Render(expr, new PlaceholderTable());
        Assert.Equal(BsonDocument.Parse("{ Age: { $nin: [1, 2, 3] } }"), rendered);
    }

    // ------------------------------------------------------------------
    // Test 19-23: MongoRegexExpression (EF-329) → $regularExpression, matching the driver-LINQ v3
    // rendering shape empirically captured under MongoQueryMode.DriverLinq (see Task 6 report):
    // { field: { $regularExpression: { pattern: "<anchored/escaped>", options: "s" } } }.
    // ------------------------------------------------------------------

    [Fact]
    public void Renders_starts_with_as_anchored_regex()
    {
        var name = GetProperty<Customer>("Name");
        var expr = new MongoRegexExpression(new MongoFieldExpression(name, "Name"),
            MongoRegexKind.StartsWith, new MongoConstantExpression("A.b", name), negated: false);
        var rendered = new MongoQueryLanguageRenderer().Render(expr, new PlaceholderTable());
        Assert.Equal(
            BsonDocument.Parse("{ Name: { $regularExpression: { pattern: '^A\\\\.b', options: 's' } } }"),
            rendered);
    }

    [Fact]
    public void Renders_ends_with_as_anchored_regex()
    {
        var name = GetProperty<Customer>("Name");
        var expr = new MongoRegexExpression(new MongoFieldExpression(name, "Name"),
            MongoRegexKind.EndsWith, new MongoConstantExpression("A.b", name), negated: false);
        var rendered = new MongoQueryLanguageRenderer().Render(expr, new PlaceholderTable());
        Assert.Equal(
            BsonDocument.Parse("{ Name: { $regularExpression: { pattern: 'A\\\\.b$', options: 's' } } }"),
            rendered);
    }

    [Fact]
    public void Renders_contains_as_unanchored_regex()
    {
        var name = GetProperty<Customer>("Name");
        var expr = new MongoRegexExpression(new MongoFieldExpression(name, "Name"),
            MongoRegexKind.Contains, new MongoConstantExpression("A.b", name), negated: false);
        var rendered = new MongoQueryLanguageRenderer().Render(expr, new PlaceholderTable());
        Assert.Equal(
            BsonDocument.Parse("{ Name: { $regularExpression: { pattern: 'A\\\\.b', options: 's' } } }"),
            rendered);
    }

    [Fact]
    public void Renders_negated_starts_with_as_not_wrapped_regex()
    {
        var name = GetProperty<Customer>("Name");
        var expr = new MongoRegexExpression(new MongoFieldExpression(name, "Name"),
            MongoRegexKind.StartsWith, new MongoConstantExpression("A", name), negated: true);
        var rendered = new MongoQueryLanguageRenderer().Render(expr, new PlaceholderTable());
        Assert.Equal(
            BsonDocument.Parse(
                "{ Name: { $not: { $regularExpression: { pattern: '^A', options: 's' } } } }"),
            rendered);
    }

    [Fact]
    public void Regex_with_parameterized_term_throws_not_supported()
    {
        var name = GetProperty<Customer>("Name");
        var expr = new MongoRegexExpression(new MongoFieldExpression(name, "Name"),
            MongoRegexKind.Contains, new MongoParameterExpression("term", name), negated: false);

        Assert.Throws<NativeTranslationNotSupportedException>(
            () => new MongoQueryLanguageRenderer().Render(expr, new PlaceholderTable()));
    }

    // ------------------------------------------------------------------
    // $elemMatch over an owned (embedded) array
    // ------------------------------------------------------------------

    [Fact]
    public void Renders_elem_match_with_element_relative_child()
    {
        var heading = GetPostProperty(nameof(Post.Heading));
        var pred = new MongoElemMatchExpression(
            "Posts",
            new MongoBinaryExpression(
                MongoBinaryOperator.Equal,
                new MongoFieldExpression(heading, "Heading"),   // element-relative, NOT "Posts.Heading"
                new MongoConstantExpression("x", heading)),
            negated: false);

        var rendered = new MongoQueryLanguageRenderer().Render(pred, new PlaceholderTable());

        Assert.Equal(BsonDocument.Parse("{ Posts: { $elemMatch: { Heading: 'x' } } }"), rendered);
    }

    [Fact]
    public void Renders_multi_condition_elem_match_as_a_single_element_match()
    {
        // The whole point of $elemMatch over the dotted-path alternative: BOTH conditions must hold for
        // the SAME element. Pinning the rendered shape locks that semantic.
        var heading = GetPostProperty(nameof(Post.Heading));
        var rank = GetPostProperty(nameof(Post.Rank));
        var pred = new MongoElemMatchExpression(
            "Posts",
            new MongoBinaryExpression(
                MongoBinaryOperator.AndAlso,
                new MongoBinaryExpression(
                    MongoBinaryOperator.Equal,
                    new MongoFieldExpression(heading, "Heading"),
                    new MongoConstantExpression("x", heading)),
                new MongoBinaryExpression(
                    MongoBinaryOperator.GreaterThan,
                    new MongoFieldExpression(rank, "Rank"),
                    new MongoConstantExpression(2, rank))),
            negated: false);

        var rendered = new MongoQueryLanguageRenderer().Render(pred, new PlaceholderTable());

        Assert.Equal(BsonDocument.Parse("{ Posts: { $elemMatch: { Heading: 'x', Rank: { $gt: 2 } } } }"), rendered);
    }

    [Fact]
    public void Renders_negated_elem_match_with_not()
    {
        var heading = GetPostProperty(nameof(Post.Heading));
        var pred = new MongoElemMatchExpression(
            "Posts",
            new MongoBinaryExpression(
                MongoBinaryOperator.Equal,
                new MongoFieldExpression(heading, "Heading"),
                new MongoConstantExpression("x", heading)),
            negated: true);

        var rendered = new MongoQueryLanguageRenderer().Render(pred, new PlaceholderTable());

        Assert.Equal(BsonDocument.Parse("{ Posts: { $not: { $elemMatch: { Heading: 'x' } } } }"), rendered);
    }

    [Fact]
    public void Renders_bare_Any_as_array_index_exists()
    {
        // Bare Any() IS "Count >= 1" and is represented as exactly that, so the two cannot render differently.
        // { "Posts.0": { $exists: true } } is index-usable AND correct for an empty array, a MISSING field, and
        // an explicitly-null one ({ Posts: { $ne: [] } } would wrongly match the last two).
        var pred = new MongoBinaryExpression(
            MongoBinaryOperator.GreaterThanOrEqual,
            new MongoSizeExpression("Posts", typeof(int), nullSafe: true),
            new MongoConstantExpression(1, null));

        var rendered = new MongoQueryLanguageRenderer().Render(pred, new PlaceholderTable());

        Assert.Equal(BsonDocument.Parse("{ 'Posts.0': { $exists: true } }"), rendered);
    }

    [Fact]
    public void Renders_negated_bare_Any_as_array_index_not_exists()
    {
        // !Any() needs no dedicated handling: the negator inverts >= to <, giving Count < 1.
        var bareAny = new MongoBinaryExpression(
            MongoBinaryOperator.GreaterThanOrEqual,
            new MongoSizeExpression("Posts", typeof(int), nullSafe: true),
            new MongoConstantExpression(1, null));

        Assert.True(MongoExpressionNegator.TryNegate(bareAny, out var negated));

        var rendered = new MongoQueryLanguageRenderer().Render(negated, new PlaceholderTable());

        Assert.Equal(BsonDocument.Parse("{ 'Posts.0': { $exists: false } }"), rendered);
    }

    [Fact]
    public void Renders_nested_elem_match_with_relative_inner_path()
    {
        // The inner array path is relative to the ELEMENT ("Comments"), not the root ("Posts.Comments").
        var heading = GetPostProperty(nameof(Post.Heading));
        var inner = new MongoElemMatchExpression(
            "Comments",
            new MongoBinaryExpression(
                MongoBinaryOperator.Equal,
                new MongoFieldExpression(heading, "Text"),   // property identity is irrelevant to rendering
                new MongoConstantExpression("t", heading)),
            negated: false);
        var pred = new MongoElemMatchExpression("Posts", inner, negated: false);

        var rendered = new MongoQueryLanguageRenderer().Render(pred, new PlaceholderTable());

        Assert.Equal(
            BsonDocument.Parse("{ Posts: { $elemMatch: { Comments: { $elemMatch: { Text: 't' } } } } }"),
            rendered);
    }

    // ------------------------------------------------------------------
    // IsQueryDialectRenderable — the classifier the translator gates $elemMatch children on
    // ------------------------------------------------------------------

    [Fact]
    public void IsQueryDialectRenderable_accepts_a_field_to_constant_comparison()
    {
        var rank = GetPostProperty(nameof(Post.Rank));
        var pred = new MongoBinaryExpression(
            MongoBinaryOperator.GreaterThan,
            new MongoFieldExpression(rank, "Rank"),
            new MongoConstantExpression(2, rank));

        Assert.True(MongoQueryLanguageRenderer.IsQueryDialectRenderable(pred));
    }

    [Fact]
    public void IsQueryDialectRenderable_rejects_a_field_to_field_comparison()
    {
        // Field-to-field has no query-dialect form: RenderNode would fall through to RenderAsExpr ($expr),
        // which is not usable inside $elemMatch.
        var rank = GetPostProperty(nameof(Post.Rank));
        var pred = new MongoBinaryExpression(
            MongoBinaryOperator.GreaterThan,
            new MongoFieldExpression(rank, "Rank"),
            new MongoFieldExpression(rank, "Other"));

        Assert.False(MongoQueryLanguageRenderer.IsQueryDialectRenderable(pred));
    }

    [Fact]
    public void IsQueryDialectRenderable_accepts_Not_over_a_query_native_comparison()
    {
        // Flipped by the owned-collection All slice: RenderUnary now renders this as
        // { Rank: { $not: { $eq: 2 } } }, the exact complement. Previously it threw, so the classifier
        // correctly rejected it.
        var rank = GetPostProperty(nameof(Post.Rank));
        var pred = new MongoUnaryExpression(
            MongoUnaryOperator.Not,
            new MongoBinaryExpression(
                MongoBinaryOperator.Equal,
                new MongoFieldExpression(rank, "Rank"),
                new MongoConstantExpression(2, rank)));

        Assert.True(MongoQueryLanguageRenderer.IsQueryDialectRenderable(pred));
        Assert.Equal(
            BsonDocument.Parse("{ Rank: { $not: { $eq: 2 } } }"),
            new MongoQueryLanguageRenderer().Render(pred, new PlaceholderTable()));
    }

    [Fact]
    public void Not_over_a_parameterized_equality_wraps_the_sentinel_in_eq()
    {
        // Final-review finding I-1: the ONLY document-valued RenderComparison output RenderUnary's '$'-prefix
        // check can actually see here is PlaceholderTable's parameter sentinel, { __mongoef_param__: N } —
        // NOT "equality against a document-valued property" (RenderComparison only ever receives a mapped
        // SCALAR IProperty leaf, so that input never occurs). The sentinel IS a BsonDocument but is NOT
        // '$'-prefixed, so this pins that the $eq wrap still applies to it exactly as it does for an inline
        // constant — i.e. !(x.Rank == capturedLocal) renders correctly, not as the illegal
        // { Rank: { $not: { __mongoef_param__: 0 } } } bare-value-under-$not form.
        var rank = GetPostProperty(nameof(Post.Rank));
        var pred = new MongoUnaryExpression(
            MongoUnaryOperator.Not,
            new MongoBinaryExpression(
                MongoBinaryOperator.Equal,
                new MongoFieldExpression(rank, "Rank"),
                new MongoParameterExpression("p0", rank)));

        var placeholders = new PlaceholderTable();
        var rendered = new MongoQueryLanguageRenderer().Render(pred, placeholders);

        var doc = Assert.IsType<BsonDocument>(rendered);
        var rankCond = Assert.IsType<BsonDocument>(doc["Rank"]);
        var notCond = Assert.IsType<BsonDocument>(rankCond["$not"]);
        var sentinel = Assert.IsType<BsonDocument>(notCond["$eq"]);
        Assert.True(PlaceholderTable.TryGetPlaceholderIndex(sentinel, out var index));
        Assert.Equal(0, index);
    }

    [Fact]
    public void IsQueryDialectRenderable_still_rejects_but_Render_now_falls_to_expr_for_Not_over_a_field_to_field_comparison()
    {
        // RenderUnary's query-dialect arm is still gated on IsQueryNativeComparison, so the QUERY dialect
        // still cannot express this shape and the classifier must still reject it (unchanged — this keeps
        // the $expr-inside-$elemMatch invariant intact, since IsQueryDialectRenderable is what a caller like
        // an $elemMatch element predicate consults).
        //
        // EF-396: at the TOP level (not inside $elemMatch), RenderUnary no longer throws for this shape —
        // it falls to MongoAggregationExpressionRenderer via CanRender/Render and renders
        // { $expr: { $not: [ { $gt: ["$Rank", "$Other"] } ] } } instead of declining.
        var rank = GetPostProperty(nameof(Post.Rank));
        var pred = new MongoUnaryExpression(
            MongoUnaryOperator.Not,
            new MongoBinaryExpression(
                MongoBinaryOperator.GreaterThan,
                new MongoFieldExpression(rank, "Rank"),
                new MongoFieldExpression(rank, "Other")));

        Assert.False(MongoQueryLanguageRenderer.IsQueryDialectRenderable(pred));

        var rendered = new MongoQueryLanguageRenderer().Render(pred, new PlaceholderTable());

        Assert.Equal(
            BsonDocument.Parse("{ $expr: { $not: [ { $gt: [\"$Rank\", \"$Other\"] } ] } }"),
            rendered);
    }

    [Fact]
    public void IsQueryDialectRenderable_still_rejects_Not_over_a_conjunction()
    {
        var rank = GetPostProperty(nameof(Post.Rank));
        var cmp = new MongoBinaryExpression(
            MongoBinaryOperator.Equal,
            new MongoFieldExpression(rank, "Rank"),
            new MongoConstantExpression(1, rank));
        var pred = new MongoUnaryExpression(
            MongoUnaryOperator.Not,
            new MongoBinaryExpression(MongoBinaryOperator.AndAlso, cmp, cmp));

        Assert.False(MongoQueryLanguageRenderer.IsQueryDialectRenderable(pred));
    }

    [Fact]
    public void Not_over_a_relational_comparison_renders_as_not_over_the_operator_document()
    {
        var rank = GetPostProperty(nameof(Post.Rank));
        var pred = new MongoUnaryExpression(
            MongoUnaryOperator.Not,
            new MongoBinaryExpression(
                MongoBinaryOperator.GreaterThan,
                new MongoFieldExpression(rank, "Rank"),
                new MongoConstantExpression(5, rank)));

        Assert.Equal(
            BsonDocument.Parse("{ Rank: { $not: { $gt: 5 } } }"),
            new MongoQueryLanguageRenderer().Render(pred, new PlaceholderTable()));
    }

    [Fact]
    public void IsQueryDialectRenderable_recurses_through_elem_match_and_conjunctions()
    {
        var rank = GetPostProperty(nameof(Post.Rank));
        var good = new MongoElemMatchExpression(
            "Comments",
            new MongoBinaryExpression(
                MongoBinaryOperator.Equal,
                new MongoFieldExpression(rank, "Rank"),
                new MongoConstantExpression(1, rank)),
            negated: false);
        var bad = new MongoElemMatchExpression(
            "Comments",
            new MongoBinaryExpression(
                MongoBinaryOperator.GreaterThan,
                new MongoFieldExpression(rank, "Rank"),
                new MongoFieldExpression(rank, "Other")),
            negated: false);

        Assert.True(MongoQueryLanguageRenderer.IsQueryDialectRenderable(
            new MongoBinaryExpression(MongoBinaryOperator.AndAlso, good, good)));
        Assert.False(MongoQueryLanguageRenderer.IsQueryDialectRenderable(
            new MongoBinaryExpression(MongoBinaryOperator.AndAlso, good, bad)));
        // Bare Any() IS "Count >= 1" — represented as a count comparison, not a MongoElemMatchExpression.
        Assert.True(MongoQueryLanguageRenderer.IsQueryDialectRenderable(
            new MongoBinaryExpression(
                MongoBinaryOperator.GreaterThanOrEqual,
                new MongoSizeExpression("Posts", typeof(int), nullSafe: true),
                new MongoConstantExpression(1, null))));
    }

    // ------------------------------------------------------------------
    // MongoSizeExpression.NullSafe (EF-322 Task 2)
    // ------------------------------------------------------------------

    [Fact]
    public void Renders_a_null_safe_size_in_the_expr_dialect_with_ifNull()
    {
        // $size against a MISSING or explicitly-null embedded array is a HARD SERVER ERROR that aborts the
        // whole aggregate — the same failure mode the driver's own count translation has. $ifNull maps both
        // states to [], giving 0, which is what LINQ's Count answers for a missing embedded array.
        var pred = new MongoBinaryExpression(
            MongoBinaryOperator.GreaterThan,
            new MongoSizeExpression("Posts", typeof(int), nullSafe: true),
            new MongoParameterExpression("__n", null));

        var rendered = new MongoQueryLanguageRenderer().Render(pred, new PlaceholderTable());

        var expr = rendered.AsBsonDocument["$expr"].AsBsonDocument;
        var size = expr["$gt"].AsBsonArray[0].AsBsonDocument;
        Assert.Equal(
            BsonDocument.Parse("{ $size: { $ifNull: [ '$Posts', [] ] } }"),
            size);
    }

    [Fact]
    public void Renders_a_non_null_safe_size_without_ifNull_so_the_lookup_alias_form_is_unchanged()
    {
        // The projected reference-collection Count path constructs MongoSizeExpression with the DEFAULT
        // nullSafe: false, because a $lookup output alias is always an array. Several committed spec
        // baselines pin { "$size" : "$_lookup_Orders" }; this test is what keeps them from moving.
        var rendered = MongoAggregationExpressionRenderer.Render(
            new MongoSizeExpression("_lookup_Orders", typeof(int)), new PlaceholderTable());

        Assert.Equal(BsonDocument.Parse("{ $size: '$_lookup_Orders' }"), rendered);
    }

    // --- Array cardinality: the query-dialect array-index existence form ---

    private static MongoBinaryExpression Count(MongoBinaryOperator op, object threshold)
        => new(op,
            new MongoSizeExpression("Posts", typeof(int), nullSafe: true),
            new MongoConstantExpression(threshold, null));

    private static BsonValue RenderCount(MongoBinaryOperator op, object threshold)
        => new MongoQueryLanguageRenderer().Render(Count(op, threshold), new PlaceholderTable());

    [Fact]
    public void Renders_count_greater_than_as_index_exists()
        => Assert.Equal(
            BsonDocument.Parse("{ 'Posts.2': { $exists: true } }"),
            RenderCount(MongoBinaryOperator.GreaterThan, 2));

    [Fact]
    public void Renders_count_greater_than_or_equal_as_one_lower_index_exists()
        => Assert.Equal(
            BsonDocument.Parse("{ 'Posts.1': { $exists: true } }"),
            RenderCount(MongoBinaryOperator.GreaterThanOrEqual, 2));

    [Fact]
    public void Renders_count_less_than_as_one_lower_index_absent()
        => Assert.Equal(
            BsonDocument.Parse("{ 'Posts.1': { $exists: false } }"),
            RenderCount(MongoBinaryOperator.LessThan, 2));

    [Fact]
    public void Renders_count_less_than_or_equal_as_index_absent()
        => Assert.Equal(
            BsonDocument.Parse("{ 'Posts.2': { $exists: false } }"),
            RenderCount(MongoBinaryOperator.LessThanOrEqual, 2));

    [Fact]
    public void Renders_count_equal_as_a_merged_two_key_document()
        // C == 2 ⇔ more than 1 AND at most 2. CombineAnd merges the two distinct keys into one document.
        => Assert.Equal(
            BsonDocument.Parse("{ 'Posts.1': { $exists: true }, 'Posts.2': { $exists: false } }"),
            RenderCount(MongoBinaryOperator.Equal, 2));

    [Fact]
    public void Renders_count_equal_zero_as_a_single_absent_index()
        // C == 0 needs only the upper bound — and it is TRUE for a missing or explicitly-null array, which is
        // what LINQ answers. { Posts: { $size: 0 } } would wrongly answer false for both.
        => Assert.Equal(
            BsonDocument.Parse("{ 'Posts.0': { $exists: false } }"),
            RenderCount(MongoBinaryOperator.Equal, 0));

    [Fact]
    public void Renders_count_not_equal_as_an_or_of_the_two_flips()
        => Assert.Equal(
            BsonDocument.Parse(
                "{ $or: [ { 'Posts.1': { $exists: false } }, { 'Posts.2': { $exists: true } } ] }"),
            RenderCount(MongoBinaryOperator.NotEqual, 2));

    [Fact]
    public void Renders_count_not_equal_zero_as_a_single_present_index()
        => Assert.Equal(
            BsonDocument.Parse("{ 'Posts.0': { $exists: true } }"),
            RenderCount(MongoBinaryOperator.NotEqual, 0));

    [Fact]
    public void Renders_a_long_threshold_in_the_index_form()
        => Assert.Equal(
            BsonDocument.Parse("{ 'Posts.2': { $exists: true } }"),
            RenderCount(MongoBinaryOperator.GreaterThan, 2L));

    // NOTE: the brief specified a single [Theory]/[InlineData(MongoBinaryOperator..., ...)] here, but
    // MongoBinaryOperator is internal and a public [Theory] method cannot expose an internal type in its
    // signature (CS0051) while the class itself stays public — the same CS0051 already documented in
    // MongoExpressionNegatorTests.cs. Split into five [Fact]s (via a private, non-public helper) carrying
    // the identical assertions instead.

    // Tautologies and contradictions: no index arithmetic is possible, so these are NOT admissible in the
    // query dialect and must route to $expr, which handles them correctly and generally.
    private static void AssertDegenerateCountThresholdRoutesToExpr(MongoBinaryOperator op, int threshold)
    {
        var rendered = RenderCount(op, threshold).AsBsonDocument;

        Assert.True(rendered.Contains("$expr"));
        Assert.False(MongoQueryLanguageRenderer.IsQueryDialectRenderable(Count(op, threshold)));
    }

    [Fact]
    public void Degenerate_count_threshold_greater_than_or_equal_zero_routes_to_expr() // always true
        => AssertDegenerateCountThresholdRoutesToExpr(MongoBinaryOperator.GreaterThanOrEqual, 0);

    [Fact]
    public void Degenerate_count_threshold_greater_than_minus_one_routes_to_expr() // always true
        => AssertDegenerateCountThresholdRoutesToExpr(MongoBinaryOperator.GreaterThan, -1);

    [Fact]
    public void Degenerate_count_threshold_less_than_zero_routes_to_expr() // always false
        => AssertDegenerateCountThresholdRoutesToExpr(MongoBinaryOperator.LessThan, 0);

    [Fact]
    public void Degenerate_count_threshold_less_than_or_equal_minus_one_routes_to_expr() // always false
        => AssertDegenerateCountThresholdRoutesToExpr(MongoBinaryOperator.LessThanOrEqual, -1);

    [Fact]
    public void Degenerate_count_threshold_equal_minus_one_routes_to_expr() // always false
        => AssertDegenerateCountThresholdRoutesToExpr(MongoBinaryOperator.Equal, -1);

    [Fact]
    public void A_hand_built_non_integer_count_threshold_routes_to_expr()
    {
        // This node is constructed directly (Count(...) below builds a MongoBinaryExpression by hand), not
        // translated from LINQ — that framing matters here. `Where(b => b.Posts.Count > 2.5)` written as
        // ordinary C# LINQ never reaches TryGetIntegerThreshold at all: C# promotes the int count via a
        // compiler-inserted Convert(count, double), and TranslateOperand's convert guard (allowNumericWidening:
        // false on the comparison path) rejects that convert first, so the whole predicate falls back to
        // driver-LINQ before this renderer is ever consulted. TryGetIntegerThreshold's non-integral rejection
        // is real code, but it is reachable only from a hand-built tree like this one — the same class of
        // statement as the Count() > 0 upstream-rewrite finding recorded in Query/AGENTS.md.
        var rendered = RenderCount(MongoBinaryOperator.GreaterThan, 2.5).AsBsonDocument;

        Assert.True(rendered.Contains("$expr"));
        Assert.False(MongoQueryLanguageRenderer.IsQueryDialectRenderable(
            Count(MongoBinaryOperator.GreaterThan, 2.5)));
    }

    [Fact]
    public void A_parameterized_count_threshold_is_not_query_dialect_renderable()
    {
        // This is what makes a parameterized count nested inside $elemMatch decline with NO new guard: the
        // quantifier arm already gates its child on this classifier, and $expr inside $elemMatch is a hard
        // server error.
        var parameterized = new MongoBinaryExpression(
            MongoBinaryOperator.GreaterThan,
            new MongoSizeExpression("Posts", typeof(int), nullSafe: true),
            new MongoParameterExpression("__n", null));

        Assert.False(MongoQueryLanguageRenderer.IsQueryDialectRenderable(parameterized));
    }

    [Fact]
    public void An_admissible_count_comparison_is_query_dialect_renderable()
        => Assert.True(MongoQueryLanguageRenderer.IsQueryDialectRenderable(
            Count(MongoBinaryOperator.GreaterThan, 2)));

    [Fact]
    public void A_count_comparison_composes_inside_an_elem_match()
    {
        // The whole point of the constant tier: pure query dialect, so it is legal inside $elemMatch, where
        // $expr is a hard server error. The inner array path is ELEMENT-relative ("Comments"), as $elemMatch
        // requires.
        var pred = new MongoElemMatchExpression(
            "Posts",
            new MongoBinaryExpression(
                MongoBinaryOperator.GreaterThan,
                new MongoSizeExpression("Comments", typeof(int), nullSafe: true),
                new MongoConstantExpression(1, null)),
            negated: false);

        var rendered = new MongoQueryLanguageRenderer().Render(pred, new PlaceholderTable());

        Assert.Equal(
            BsonDocument.Parse("{ Posts: { $elemMatch: { 'Comments.1': { $exists: true } } } }"),
            rendered);
    }
}
