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
        public string Nickname { get; set; } = null!;

        // A value-converted bool, for the truthiness guard in RenderUnary's fall-to-$expr branch (see
        // Not_over_a_bare_outer_field_with_non_default_serialization_declines...).
        public bool ConvertedActive { get; set; }
    }

    private static IProperty GetProperty<T>(string propertyName) where T : class
    {
        using var db = SingleEntityDbContext.Create<T>();
        return db.Model.FindEntityType(typeof(T))!.FindProperty(propertyName)!;
    }

    private static IProperty GetConvertedActiveProperty()
    {
        using var db = SingleEntityDbContext.Create<Customer>(
            mb => mb.Entity<Customer>().Property(c => c.ConvertedActive).HasConversion<string>());
        return db.Model.FindEntityType(typeof(Customer))!.FindProperty(nameof(Customer.ConvertedActive))!;
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
    // simple GreaterThan comparison → { Age: { $gt: 21 } }
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
    // AndAlso of two ranges on the same field merges operator docs
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
    // Equal comparison → bare { Age: value } (no $eq wrapper)
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
    // NotEqual → { Age: { $ne: value } }
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
    // LessThanOrEqual → { Age: { $lte: value } }
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
    // GreaterThanOrEqual → { Age: { $gte: value } }
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
    // OrElse → { $or: [ { Age: { $lt: 18 } }, { Age: { $gt: 65 } } ] }
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
    // bare bool field → { Active: true }
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
    // Not(bool field) → { Active: { $ne: true } }
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
    // Parameter placeholder — renders sentinel, records in PlaceholderTable
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

        // { Age: { $gt: <sentinel> } }, where the sentinel is recognized as placeholder index 0.
        var rendered_doc = Assert.IsType<BsonDocument>(rendered);
        var ageCond = Assert.IsType<BsonDocument>(rendered_doc["Age"]);
        var sentinelValue = ageCond["$gt"];
        Assert.True(PlaceholderTable.TryGetPlaceholderIndex(sentinelValue, out var index));
        Assert.Equal(0, index);
    }

    // ------------------------------------------------------------------
    // AndAlso with two different fields — no merge, remain flat { f1: ..., f2: ... }
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
    // MongoConstantExpression with null ForSerialization (Skip/Take count)
    //          → BsonValue.Create(value), no throw
    // ------------------------------------------------------------------

    [Fact]
    public void Renders_constant_with_null_ForSerialization_as_BsonValue()
    {
        var constant = new MongoConstantExpression(5, forSerialization: null);
        var placeholders = new PlaceholderTable();

        var result = MongoValueRenderer.RenderValue(constant, placeholders);

        Assert.Equal(new BsonInt32(5), result);
    }

    // ------------------------------------------------------------------
    // MongoParameterExpression with null ForSerialization (Skip/Take count)
    //          → placeholder with null serializer, no throw
    // ------------------------------------------------------------------

    [Fact]
    public void Renders_parameter_with_null_ForSerialization_as_null_serializer_placeholder()
    {
        var parameter = new MongoParameterExpression("p", forSerialization: null);
        var placeholders = new PlaceholderTable();

        var result = MongoValueRenderer.RenderValue(parameter, placeholders);

        Assert.Single(placeholders.Entries);
        Assert.Equal("p", placeholders.Entries[0].Name);
        Assert.Null(placeholders.Entries[0].Serializer);

        Assert.True(PlaceholderTable.TryGetPlaceholderIndex(result, out var index));
        Assert.Equal(0, index);
    }

    // ------------------------------------------------------------------
    // field-to-field comparison → { $expr: { $eq: ['$Age', '$Score'] } }
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
    // mixed AND keeps the indexable branch in query dialect
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
    // `== null` renders as a bare null value → { Name: null } (matches null or missing)
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
    // MongoInExpression over an inline constant collection → { field: { $in: [...] } }
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
    // negated MongoInExpression over an inline constant collection → { field: { $nin: [...] } }
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
    // MongoInExpression over a MongoValueListExpression of separate parameters
    // (`new[] { prm1, prm2 }.Contains(c.Age)`) → { Age: { $in: [<sentinel0>, <sentinel1>] } }, one placeholder
    // per element (not a single array placeholder).
    // ------------------------------------------------------------------

    [Fact]
    public void Renders_in_for_value_list_of_parameters_as_independent_sentinels()
    {
        var age = GetProperty<Customer>("Age");
        var expr = new MongoInExpression(
            new MongoFieldExpression(age, "Age"),
            new MongoValueListExpression(
            [
                new MongoParameterExpression("p0", age),
                new MongoParameterExpression("p1", age)
            ]),
            negated: false);

        var placeholders = new PlaceholderTable();
        var rendered = new MongoQueryLanguageRenderer().Render(expr, placeholders);

        Assert.Equal(2, placeholders.Entries.Count);
        Assert.Equal("p0", placeholders.Entries[0].Name);
        Assert.Equal("p1", placeholders.Entries[1].Name);

        var doc = Assert.IsType<BsonDocument>(rendered);
        var ageCond = Assert.IsType<BsonDocument>(doc["Age"]);
        var inArray = Assert.IsType<BsonArray>(ageCond["$in"]);
        Assert.Equal(2, inArray.Count);
        Assert.True(PlaceholderTable.TryGetPlaceholderIndex(inArray[0], out var index0));
        Assert.Equal(0, index0);
        Assert.True(PlaceholderTable.TryGetPlaceholderIndex(inArray[1], out var index1));
        Assert.Equal(1, index1);
    }

    // ------------------------------------------------------------------
    // MongoRegexExpression → { field: { $regularExpression: { pattern, options: "s" } } }, the same
    // shape the driver-LINQ path emits.
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
    public void Renders_parameterized_regex_term_as_a_placeholder_sentinel()
    {
        // A parameterized term can't be escaped/anchored at render time; it defers to a placeholder resolved per
        // execution (see MongoPipelineFactoryTests).
        var name = GetProperty<Customer>("Name");
        var placeholders = new PlaceholderTable();
        var expr = new MongoRegexExpression(new MongoFieldExpression(name, "Name"),
            MongoRegexKind.Contains, new MongoParameterExpression("term", name), negated: false);

        var rendered = new MongoQueryLanguageRenderer().Render(expr, placeholders);

        Assert.True(PlaceholderTable.TryGetPlaceholderIndex(rendered["Name"], out var index));
        Assert.Equal(0, index);
    }

    [Fact]
    public void Renders_negated_parameterized_regex_term_wraps_sentinel_in_not()
    {
        var name = GetProperty<Customer>("Name");
        var placeholders = new PlaceholderTable();
        var expr = new MongoRegexExpression(new MongoFieldExpression(name, "Name"),
            MongoRegexKind.StartsWith, new MongoParameterExpression("term", name), negated: true);

        var rendered = new MongoQueryLanguageRenderer().Render(expr, placeholders);

        Assert.True(
            PlaceholderTable.TryGetPlaceholderIndex(rendered["Name"].AsBsonDocument["$not"], out var index));
        Assert.Equal(0, index);
    }

    [Fact]
    public void Renders_pattern_kind_as_regex_with_its_own_options()
    {
        // Regex.IsMatch(field, constantPattern): pattern is unescaped and options come verbatim from
        // PatternOptions, not from CaseInsensitive/"s".
        var name = GetProperty<Customer>("Name");
        var expr = new MongoRegexExpression(new MongoFieldExpression(name, "Name"),
            MongoRegexKind.Pattern, new MongoConstantExpression("^S", name), negated: false, patternOptions: "");
        var rendered = new MongoQueryLanguageRenderer().Render(expr, new PlaceholderTable());
        Assert.Equal(
            BsonDocument.Parse("{ Name: { $regularExpression: { pattern: '^S', options: '' } } }"),
            rendered);
    }

    [Fact]
    public void Renders_negated_pattern_kind_as_not_wrapped_regex()
    {
        var name = GetProperty<Customer>("Name");
        var expr = new MongoRegexExpression(new MongoFieldExpression(name, "Name"),
            MongoRegexKind.Pattern, new MongoConstantExpression("^S", name), negated: true, patternOptions: "");
        var rendered = new MongoQueryLanguageRenderer().Render(expr, new PlaceholderTable());
        Assert.Equal(
            BsonDocument.Parse("{ Name: { $not: { $regularExpression: { pattern: '^S', options: '' } } } }"),
            rendered);
    }

    // EF-247: $regularExpression needs a literal, so a Pattern whose pattern is a field or a parameter must render
    // through $expr/$regexMatch (MongoExpressionNodeCoverageTests can't see this shape-conditional routing).
    [Fact]
    public void Pattern_kind_with_field_pattern_falls_through_to_expr_regexMatch()
    {
        var name = GetProperty<Customer>("Name");
        var nickname = GetProperty<Customer>("Nickname");
        var expr = new MongoRegexExpression(
            new MongoFieldExpression(name, "Name"), MongoRegexKind.Pattern,
            new MongoFieldExpression(nickname, "Nickname"), negated: false, patternOptions: "i");

        var result = new MongoQueryLanguageRenderer().Render(expr, new PlaceholderTable());

        Assert.Equal(
            """{ "$expr" : { "$regexMatch" : { "input" : "$Name", "regex" : "$Nickname", "options" : "i" } } }""",
            result.ToJson());
    }

    [Fact]
    public void Pattern_kind_with_parameter_pattern_falls_through_to_expr_regexMatch()
    {
        var name = GetProperty<Customer>("Name");
        var placeholders = new PlaceholderTable();
        var expr = new MongoRegexExpression(
            new MongoFieldExpression(name, "Name"), MongoRegexKind.Pattern,
            new MongoParameterExpression("p", forSerialization: null), negated: false);

        var result = new MongoQueryLanguageRenderer().Render(expr, placeholders);

        Assert.Contains("\"$expr\"", result.ToJson());
        Assert.DoesNotContain("$regularExpression", result.ToJson());
        Assert.Contains("$regexMatch", result.ToJson());
    }

    [Fact]
    public void Field_to_field_regex_falls_through_to_expr()
    {
        var name = GetProperty<Customer>("Name");
        var nickname = GetProperty<Customer>("Nickname");
        var expr = new MongoRegexExpression(
            new MongoFieldExpression(name, "Name"), MongoRegexKind.StartsWith,
            new MongoFieldExpression(nickname, "Nickname"), negated: false);

        var result = new MongoQueryLanguageRenderer().Render(expr, new PlaceholderTable());

        Assert.Equal(
            """{ "$expr" : { "$eq" : [{ "$indexOfCP" : ["$Name", "$Nickname"] }, 0] } }""",
            result.ToJson());
    }

    // A computed receiver ((c.Name + "x").StartsWith("A")) has no query-dialect form: RenderRegex would emit
    // { <path>: /re/ } over a bogus path and match nothing. Both the classifier and RenderNode must send it to $expr.
    private static MongoRegexExpression ComputedReceiverRegex(bool negated = false)
    {
        var name = GetProperty<Customer>("Name");
        return new MongoRegexExpression(
            new MongoConcatExpression([new MongoFieldExpression(name, "Name"), new MongoConstantExpression("x", null)]),
            MongoRegexKind.StartsWith, new MongoConstantExpression("A", name), negated);
    }

    [Fact]
    public void IsQueryDialectRenderable_rejects_a_regex_over_a_computed_receiver()
        => Assert.False(MongoQueryLanguageRenderer.IsQueryDialectRenderable(ComputedReceiverRegex()));

    [Fact]
    public void IsQueryDialectRenderable_accepts_a_regex_over_a_field_or_element_ref_receiver()
    {
        var name = GetProperty<Customer>("Name");
        Assert.True(MongoQueryLanguageRenderer.IsQueryDialectRenderable(new MongoRegexExpression(
            new MongoFieldExpression(name, "Name"), MongoRegexKind.StartsWith, new MongoConstantExpression("A", name),
            negated: false)));
        Assert.True(MongoQueryLanguageRenderer.IsQueryDialectRenderable(new MongoRegexExpression(
            new MongoElementRefExpression("k", typeof(string)), MongoRegexKind.StartsWith,
            new MongoConstantExpression("A", name), negated: false)));
    }

    [Fact]
    public void Regex_over_a_computed_receiver_renders_via_expr_regexMatch()
    {
        var result = new MongoQueryLanguageRenderer().Render(ComputedReceiverRegex(), new PlaceholderTable());

        Assert.Equal(
            """{ "$expr" : { "$regexMatch" : { "input" : { "$concat" : [{ "$ifNull" : ["$Name", ""] }, { "$literal" : "x" }] }, "regex" : { "$regularExpression" : { "pattern" : "^A", "options" : "s" } } } } }""",
            result.ToJson());
    }

    [Fact]
    public void Negated_regex_over_a_computed_receiver_renders_via_expr_not_regexMatch()
    {
        var result = new MongoQueryLanguageRenderer().Render(ComputedReceiverRegex(negated: true), new PlaceholderTable());

        Assert.Equal(
            """{ "$expr" : { "$not" : [{ "$regexMatch" : { "input" : { "$concat" : [{ "$ifNull" : ["$Name", ""] }, { "$literal" : "x" }] }, "regex" : { "$regularExpression" : { "pattern" : "^A", "options" : "s" } } } }] } }""",
            result.ToJson());
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
        // $elemMatch requires both conditions to hold for the same element, unlike the dotted-path alternative.
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
        // Bare Any() is represented as Count >= 1. { "Posts.0": { $exists: true } } is index-usable and correct
        // for empty, missing, and null arrays ({ Posts: { $ne: [] } } would wrongly match the last two).
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
        // The inner array path is element-relative ("Comments"), not root-relative ("Posts.Comments").
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
        // Field-to-field has no query-dialect form; it would render as $expr, which is illegal inside $elemMatch.
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
        // RenderUnary renders this as { Rank: { $not: { $eq: 2 } } }, the exact complement.
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
        // The only document-valued RenderComparison output RenderUnary's '$'-prefix check sees is the parameter
        // sentinel { __mongoef_param__: N }. It isn't '$'-prefixed, so it still gets the $eq wrap and
        // !(x.Rank == capturedLocal) doesn't render as the illegal { Rank: { $not: { __mongoef_param__: 0 } } }.
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
        // The query dialect can't express this, so the classifier must reject it (keeping $expr out of
        // $elemMatch). At the top level RenderUnary falls to the aggregation renderer instead:
        // { $expr: { $not: [ { $gt: ["$Rank", "$Other"] } ] } }.
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
    public void Not_over_a_bare_outer_field_with_non_default_serialization_declines_instead_of_expr_truthiness()
    {
        // RenderUnary's fall-to-$expr branch calls CanRender on the operand directly, bypassing CanRender's Not
        // arm and its AllFieldsDefaultSerialized guard; a bare outer field is unconditionally renderable there.
        // Without its own guard, a value-converted bool under Not would render as raw truthiness on the stored
        // "True"/"False" strings (both truthy). The exception becomes a driver-LINQ fallback in TryBuildPipeline.
        var convertedActive = GetConvertedActiveProperty();
        var pred = new MongoUnaryExpression(
            MongoUnaryOperator.Not, new MongoOuterFieldExpression(convertedActive, "ConvertedActive"));

        Assert.Throws<NativeTranslationNotSupportedException>(
            () => new MongoQueryLanguageRenderer().Render(pred, new PlaceholderTable()));
    }

    [Fact]
    public void Not_over_a_bare_outer_field_with_default_serialization_still_renders_via_expr()
    {
        // Control: a default-serialized outer bool under Not must still render via $expr.
        var active = GetProperty<Customer>("Active");
        var pred = new MongoUnaryExpression(MongoUnaryOperator.Not, new MongoOuterFieldExpression(active, "Active"));

        var rendered = new MongoQueryLanguageRenderer().Render(pred, new PlaceholderTable());

        Assert.Equal(BsonDocument.Parse("{ $expr: { $not: [ \"$Active\" ] } }"), rendered);
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
        // Bare Any() is a count comparison, not a MongoElemMatchExpression.
        Assert.True(MongoQueryLanguageRenderer.IsQueryDialectRenderable(
            new MongoBinaryExpression(
                MongoBinaryOperator.GreaterThanOrEqual,
                new MongoSizeExpression("Posts", typeof(int), nullSafe: true),
                new MongoConstantExpression(1, null))));
    }

    // ------------------------------------------------------------------
    // MongoSizeExpression.NullSafe
    // ------------------------------------------------------------------

    [Fact]
    public void Renders_a_null_safe_size_in_the_expr_dialect_with_ifNull()
    {
        // $size on a missing or null array is a server error that aborts the aggregate. $ifNull maps both to [],
        // giving 0, which is what LINQ's Count answers.
        var pred = new MongoBinaryExpression(
            MongoBinaryOperator.GreaterThan,
            new MongoSizeExpression("Posts", typeof(int), nullSafe: true),
            new MongoParameterExpression("__n", null));

        var rendered = new MongoQueryLanguageRenderer().Render(pred, new PlaceholderTable());

        // The parameter is the lower side of $gt, so the comparison is the second conjunct of its null guard.
        var expr = rendered.AsBsonDocument["$expr"].AsBsonDocument;
        var size = expr["$and"].AsBsonArray[1].AsBsonDocument["$gt"].AsBsonArray[0].AsBsonDocument;
        Assert.Equal(
            BsonDocument.Parse("{ $size: { $ifNull: [ '$Posts', [] ] } }"),
            size);
    }

    [Fact]
    public void Renders_a_non_null_safe_size_without_ifNull_so_the_lookup_alias_form_is_unchanged()
    {
        // The projected reference-collection Count uses nullSafe: false, since a $lookup alias is always an
        // array. Spec baselines pin { "$size" : "$_lookup_Orders" }.
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

    // Five [Fact]s via a private helper rather than a [Theory]: MongoBinaryOperator is internal, and a public
    // [Theory] can't expose it in its signature (CS0051).

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
        // Reachable only from a hand-built tree: in real LINQ, `Count > 2.5` inserts Convert(count, double), which
        // TranslateOperand's convert guard rejects first, so the predicate falls back before this renderer.
        var rendered = RenderCount(MongoBinaryOperator.GreaterThan, 2.5).AsBsonDocument;

        Assert.True(rendered.Contains("$expr"));
        Assert.False(MongoQueryLanguageRenderer.IsQueryDialectRenderable(
            Count(MongoBinaryOperator.GreaterThan, 2.5)));
    }

    [Fact]
    public void A_parameterized_count_threshold_is_not_query_dialect_renderable()
    {
        // A parameterized count inside $elemMatch declines with no extra guard: the quantifier arm gates its
        // child on this classifier, and $expr inside $elemMatch is a server error.
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
        // The constant tier is pure query dialect, so it's legal inside $elemMatch (where $expr is a server
        // error). The inner array path is element-relative, as $elemMatch requires.
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

    [Fact]
    public void IsQueryDialectRenderable_rejects_a_conditional_expression()
    {
        var conditional = new MongoConditionalExpression(
            new MongoConstantExpression(true, forSerialization: null),
            new MongoConstantExpression(1, forSerialization: null),
            new MongoConstantExpression(2, forSerialization: null));

        Assert.False(MongoQueryLanguageRenderer.IsQueryDialectRenderable(conditional));
    }

    [Fact]
    public void IsQueryDialectRenderable_rejects_a_date_part_expression()
    {
        var datePart = new MongoDatePartExpression(
            new MongoConstantExpression(DateTime.UtcNow, forSerialization: null), MongoDatePart.Year);

        Assert.False(MongoQueryLanguageRenderer.IsQueryDialectRenderable(datePart));
    }
}
