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
using Microsoft.EntityFrameworkCore.Metadata;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Query.Expressions;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query.NativeTranslation;

/// <summary>
/// Unit tests for <see cref="MongoAggregationExpressionRenderer"/>.
/// </summary>
public class MongoAggregationExpressionRendererTests
{
    // --- Entity model used across tests ---

    private class Customer
    {
        public MongoDB.Bson.ObjectId Id { get; set; }
        public int Age { get; set; }
        public int Score { get; set; }
        public string Status { get; set; } = "";
        public string Nickname { get; set; } = "";
        public bool IsActive { get; set; }
    }

    private static IProperty GetProperty<T>(string propertyName) where T : class
    {
        using var db = SingleEntityDbContext.Create<T>();
        return db.Model.FindEntityType(typeof(T))!.FindProperty(propertyName)!;
    }

    // ------------------------------------------------------------------
    // Field-to-field comparison → { $eq: ['$Age', '$Score'] }
    // ------------------------------------------------------------------

    [Fact]
    public void Renders_field_to_field_comparison()
    {
        var age = GetProperty<Customer>("Age");
        var score = GetProperty<Customer>("Score");
        var expr = new MongoBinaryExpression(
            MongoBinaryOperator.Equal,
            new MongoFieldExpression(age, "Age"),
            new MongoFieldExpression(score, "Score"));

        var rendered = MongoAggregationExpressionRenderer.Render(expr, new PlaceholderTable());

        Assert.Equal(BsonValue.Create(BsonDocument.Parse("{ $eq: ['$Age', '$Score'] }")), rendered);
    }

    // ------------------------------------------------------------------
    // Arithmetic operand → { $gt: [ { $add: ['$Age', '$Score'] }, 5 ] }
    // ------------------------------------------------------------------

    [Fact]
    public void Renders_arithmetic_operand()
    {
        var age = GetProperty<Customer>("Age");
        var score = GetProperty<Customer>("Score");
        // Age + Score > 5
        var expr = new MongoBinaryExpression(
            MongoBinaryOperator.GreaterThan,
            new MongoBinaryExpression(MongoBinaryOperator.Add,
                new MongoFieldExpression(age, "Age"),
                new MongoFieldExpression(score, "Score")),
            new MongoConstantExpression(5, age));

        var rendered = MongoAggregationExpressionRenderer.Render(expr, new PlaceholderTable());

        Assert.Equal(BsonValue.Create(BsonDocument.Parse("{ $gt: [ { $add: ['$Age', '$Score'] }, 5 ] }")), rendered);
    }

    // ------------------------------------------------------------------
    // MongoFilteredSizeExpression rendering
    // ------------------------------------------------------------------

    [Fact]
    public void Filtered_size_renders_as_size_over_filter_with_ifNull()
    {
        var placeholders = new PlaceholderTable();
        var node = new MongoFilteredSizeExpression(
            "Posts",
            new MongoBinaryExpression(
                MongoBinaryOperator.GreaterThan,
                new MongoElementRefExpression("Rank", typeof(int)),
                new MongoConstantExpression(0, forSerialization: null)),
            typeof(int));

        var rendered = MongoAggregationExpressionRenderer.Render(node, placeholders);

        Assert.Equal(
            BsonDocument.Parse(
                """
                { "$size": { "$filter": {
                    "input": { "$ifNull": ["$Posts", []] },
                    "as": "e",
                    "cond": { "$gt": ["$$e.Rank", 0] } } } }
                """),
            rendered);
    }

    [Fact]
    public void Nested_filtered_size_gives_each_level_its_own_variable()
    {
        var placeholders = new PlaceholderTable();
        var inner = new MongoBinaryExpression(
            MongoBinaryOperator.GreaterThan,
            new MongoFilteredSizeExpression(
                "Comments",
                new MongoBinaryExpression(
                    MongoBinaryOperator.GreaterThan,
                    new MongoElementRefExpression("Age", typeof(int)),
                    new MongoConstantExpression(0, forSerialization: null)),
                typeof(int)),
            new MongoConstantExpression(1, forSerialization: null));

        var rendered = MongoAggregationExpressionRenderer.Render(
            new MongoFilteredSizeExpression("Posts", inner, typeof(int)), placeholders);

        // The inner array path is relative to the outer variable, and the inner predicate to the inner
        // variable. Getting either wrong reads the wrong array.
        var json = rendered.ToJson();
        Assert.Contains("\"$$e.Comments\"", json);
        Assert.Contains("\"$$ee.Age\"", json);
    }

    [Fact]
    public void Existing_nodes_render_unchanged_when_no_element_variable_is_in_scope()
    {
        var placeholders = new PlaceholderTable();
        var rendered = MongoAggregationExpressionRenderer.Render(
            new MongoSizeExpression("Posts", typeof(int), nullSafe: true), placeholders);

        Assert.Equal(
            BsonDocument.Parse("""{ "$size": { "$ifNull": ["$Posts", []] } }"""),
            rendered);
    }

    // ------------------------------------------------------------------
    // Integer division
    // ------------------------------------------------------------------

    [Fact]
    public void IntegerDivide_renders_as_trunc_over_divide()
    {
        var age = GetProperty<Customer>("Age");
        var score = GetProperty<Customer>("Score");

        var rendered = MongoAggregationExpressionRenderer.Render(
            new MongoBinaryExpression(
                MongoBinaryOperator.IntegerDivide,
                new MongoFieldExpression(age, "Age"),
                new MongoFieldExpression(score, "Score")),
            new PlaceholderTable());

        Assert.Equal(
            BsonDocument.Parse("""{ "$trunc": { "$divide": ["$Age", "$Score"] } }"""),
            rendered);
    }

    [Fact]
    public void Plain_Divide_still_renders_unwrapped()
    {
        // Non-integral division must not be truncated; separate from the row above so merging the two arms fails.
        var age = GetProperty<Customer>("Age");
        var score = GetProperty<Customer>("Score");

        var rendered = MongoAggregationExpressionRenderer.Render(
            new MongoBinaryExpression(
                MongoBinaryOperator.Divide,
                new MongoFieldExpression(age, "Age"),
                new MongoFieldExpression(score, "Score")),
            new PlaceholderTable());

        Assert.Equal(
            BsonDocument.Parse("""{ "$divide": ["$Age", "$Score"] }"""),
            rendered);
    }

    // ------------------------------------------------------------------
    // MongoInExpression / MongoUnaryExpression aggregation-dialect arms
    // ------------------------------------------------------------------

    [Fact]
    public void CanRender_reports_true_for_MongoInExpression()
    {
        var status = GetProperty<Customer>("Status");
        var field = new MongoFieldExpression(status, "Status");
        var values = new MongoConstantExpression(new[] { "A", "B" }, status);
        var node = new MongoInExpression(field, values, negated: false);

        Assert.True(MongoAggregationExpressionRenderer.CanRender(node));

        var placeholders = new PlaceholderTable();
        var rendered = MongoAggregationExpressionRenderer.Render(node, placeholders);
        Assert.Equal(new BsonDocument("$in", new BsonArray { "$Status", new BsonArray { "A", "B" } }), rendered);
    }

    [Fact]
    public void CanRender_reports_true_for_negated_MongoInExpression()
    {
        var status = GetProperty<Customer>("Status");
        var field = new MongoFieldExpression(status, "Status");
        var values = new MongoConstantExpression(new[] { "A" }, status);
        var node = new MongoInExpression(field, values, negated: true);

        Assert.True(MongoAggregationExpressionRenderer.CanRender(node));

        var placeholders = new PlaceholderTable();
        var rendered = MongoAggregationExpressionRenderer.Render(node, placeholders);
        Assert.Equal(
            new BsonDocument("$not", new BsonArray { new BsonDocument("$in", new BsonArray { "$Status", new BsonArray { "A" } }) }),
            rendered);
    }

    [Fact]
    public void CanRender_reports_true_for_MongoUnaryExpression_Not_over_a_renderable_operand()
    {
        var isActive = GetProperty<Customer>("IsActive");
        var field = new MongoFieldExpression(isActive, "IsActive");
        var node = new MongoUnaryExpression(MongoUnaryOperator.Not, field);

        Assert.True(MongoAggregationExpressionRenderer.CanRender(node));

        var placeholders = new PlaceholderTable();
        var rendered = MongoAggregationExpressionRenderer.Render(node, placeholders);
        Assert.Equal(new BsonDocument("$not", new BsonArray { "$IsActive" }), rendered);
    }

    [Fact]
    public void CanRender_reports_true_for_MongoInExpression_over_value_list_of_parameters()
    {
        // `new[] { prm1, prm2 }.Contains(c.Age)` with separately-parameterized locals — the
        // MongoValueListExpression shape TranslateInValues produces.
        var status = GetProperty<Customer>("Status");
        var field = new MongoFieldExpression(status, "Status");
        var values = new MongoValueListExpression(
        [
            new MongoParameterExpression("p0", status),
            new MongoParameterExpression("p1", status)
        ]);
        var node = new MongoInExpression(field, values, negated: false);

        Assert.True(MongoAggregationExpressionRenderer.CanRender(node));

        var placeholders = new PlaceholderTable();
        var rendered = Assert.IsType<BsonDocument>(MongoAggregationExpressionRenderer.Render(node, placeholders));
        var operands = Assert.IsType<BsonArray>(rendered["$in"]);
        Assert.Equal("$Status", operands[0]);
        var inArray = Assert.IsType<BsonArray>(operands[1]);
        Assert.Equal(2, inArray.Count);
        Assert.True(PlaceholderTable.TryGetPlaceholderIndex(inArray[0], out var index0));
        Assert.Equal(0, index0);
        Assert.True(PlaceholderTable.TryGetPlaceholderIndex(inArray[1], out var index1));
        Assert.Equal(1, index1);
    }

    [Fact]
    public void CanRender_reports_false_for_MongoInExpression_over_unrenderable_values()
    {
        // Neither constant enumerable nor parameter: CanRenderInValues must decline what RenderInValues throws on.
        var status = GetProperty<Customer>("Status");
        var field = new MongoFieldExpression(status, "Status");
        var node = new MongoInExpression(field, new MongoFieldExpression(status, "Other"), negated: false);

        Assert.False(MongoAggregationExpressionRenderer.CanRender(node));
        Assert.Throws<NativeTranslationNotSupportedException>(
            () => MongoAggregationExpressionRenderer.Render(node, new PlaceholderTable()));
    }

    // ------------------------------------------------------------------
    // CanRender
    // ------------------------------------------------------------------

    // MemberData rows are boxed as object: a public [Theory] can't expose internal MongoExpression (CS0051).
    // Same idiom as MongoExpressionNegatorTests.

    [Theory]
    [MemberData(nameof(RenderableNodes))]
    public void CanRender_admits_exactly_what_Render_renders(object node)
    {
        var expr = (MongoExpression)node;
        Assert.True(MongoAggregationExpressionRenderer.CanRender(expr));
        // Prove Render really handles it, so CanRender and Render can't drift.
        _ = MongoAggregationExpressionRenderer.Render(expr, new PlaceholderTable());
    }

    [Theory]
    [MemberData(nameof(UnrenderableNodes))]
    public void CanRender_declines_what_Render_would_throw_on(object node)
    {
        var expr = (MongoExpression)node;
        Assert.False(MongoAggregationExpressionRenderer.CanRender(expr));
        Assert.Throws<NativeTranslationNotSupportedException>(
            () => MongoAggregationExpressionRenderer.Render(expr, new PlaceholderTable()));
    }

    public static IEnumerable<object[]> RenderableNodes()
    {
        var age = GetProperty<Customer>("Age");
        var score = GetProperty<Customer>("Score");

        // A field ref, an element ref, a constant, a parameter.
        yield return [new MongoFieldExpression(age, "Age")];
        yield return [new MongoElementRefExpression("Age", typeof(int))];
        yield return [new MongoConstantExpression(5, age)];
        yield return [new MongoParameterExpression("__p_0", age)];

        // Each comparison operator.
        foreach (var op in new[]
                 {
                     MongoBinaryOperator.Equal,
                     MongoBinaryOperator.NotEqual,
                     MongoBinaryOperator.LessThan,
                     MongoBinaryOperator.LessThanOrEqual,
                     MongoBinaryOperator.GreaterThan,
                     MongoBinaryOperator.GreaterThanOrEqual
                 })
        {
            yield return
            [
                new MongoBinaryExpression(op, new MongoFieldExpression(age, "Age"), new MongoConstantExpression(5, age))
            ];
        }

        // AndAlso, OrElse.
        yield return
        [
            new MongoBinaryExpression(
                MongoBinaryOperator.AndAlso,
                new MongoBinaryExpression(MongoBinaryOperator.GreaterThan, new MongoFieldExpression(age, "Age"),
                    new MongoConstantExpression(5, age)),
                new MongoBinaryExpression(MongoBinaryOperator.LessThan, new MongoFieldExpression(score, "Score"),
                    new MongoConstantExpression(100, score)))
        ];
        yield return
        [
            new MongoBinaryExpression(
                MongoBinaryOperator.OrElse,
                new MongoBinaryExpression(MongoBinaryOperator.GreaterThan, new MongoFieldExpression(age, "Age"),
                    new MongoConstantExpression(5, age)),
                new MongoBinaryExpression(MongoBinaryOperator.LessThan, new MongoFieldExpression(score, "Score"),
                    new MongoConstantExpression(100, score)))
        ];

        // Each arithmetic operator.
        foreach (var op in new[]
                 {
                     MongoBinaryOperator.Add,
                     MongoBinaryOperator.Subtract,
                     MongoBinaryOperator.Multiply,
                     MongoBinaryOperator.Divide,
                     MongoBinaryOperator.IntegerDivide,
                     MongoBinaryOperator.Modulo
                 })
        {
            yield return
            [
                new MongoBinaryExpression(op, new MongoFieldExpression(age, "Age"), new MongoFieldExpression(score, "Score"))
            ];
        }

        // A MongoSizeExpression, and a MongoFilteredSizeExpression.
        yield return [new MongoSizeExpression("Posts", typeof(int), nullSafe: true)];
        yield return
        [
            new MongoFilteredSizeExpression(
                "Posts",
                new MongoBinaryExpression(
                    MongoBinaryOperator.GreaterThan,
                    new MongoElementRefExpression("Rank", typeof(int)),
                    new MongoConstantExpression(0, forSerialization: null)),
                typeof(int))
        ];

        // A MongoInExpression (client-collection Contains → $in).
        yield return
        [
            new MongoInExpression(
                new MongoFieldExpression(age, "Age"), new MongoConstantExpression(new[] { 1, 2 }, age), negated: false)
        ];

        // A MongoUnaryExpression{Not} over a renderable operand.
        yield return
        [
            new MongoUnaryExpression(
                MongoUnaryOperator.Not,
                new MongoBinaryExpression(MongoBinaryOperator.Equal, new MongoFieldExpression(age, "Age"),
                    new MongoConstantExpression(5, age)))
        ];
    }

    public static IEnumerable<object[]> UnrenderableNodes()
    {
        // A MongoElemMatchExpression.
        yield return
        [
            new MongoElemMatchExpression(
                "Posts",
                new MongoBinaryExpression(MongoBinaryOperator.GreaterThan,
                    new MongoElementRefExpression("Rank", typeof(int)), new MongoConstantExpression(0, forSerialization: null)),
                negated: false)
        ];

        // A MongoFilteredSizeExpression whose element predicate is one of the above — proves CanRender recurses.
        yield return
        [
            new MongoFilteredSizeExpression(
                "Posts",
                new MongoElemMatchExpression(
                    "Comments",
                    new MongoBinaryExpression(MongoBinaryOperator.GreaterThan,
                        new MongoElementRefExpression("Rank", typeof(int)), new MongoConstantExpression(0, forSerialization: null)),
                    negated: false),
                typeof(int))
        ];
    }

    // ------------------------------------------------------------------
    // MongoConvertExpression — the $toX node
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(typeof(int), "$toInt")]
    [InlineData(typeof(long), "$toLong")]
    [InlineData(typeof(double), "$toDouble")]
    [InlineData(typeof(decimal), "$toDecimal")]
    public void Convert_node_renders_the_matching_to_operator(Type target, string op)
    {
        var node = new MongoConvertExpression(new MongoElementRefExpression("D", typeof(double)), target);

        var rendered = MongoAggregationExpressionRenderer.Render(node, new PlaceholderTable());

        Assert.Equal(BsonDocument.Parse($$"""{ "{{op}}" : "$D" }"""), rendered.AsBsonDocument);
    }

    [Theory]
    [InlineData(typeof(short))]
    [InlineData(typeof(uint))]
    [InlineData(typeof(float))]
    public void Convert_node_to_a_target_MQL_cannot_express_is_not_renderable(Type target)
    {
        // MQL has no $toShort/$toUInt/$toFloat (driver-LINQ throws for these too), so declining is correct.
        Assert.Null(MongoConvertExpression.ToOperatorFor(target));
        Assert.False(MongoAggregationExpressionRenderer.CanRender(
            new MongoConvertExpression(new MongoElementRefExpression("I", typeof(int)), target)));
    }

    [Fact]
    public void Convert_node_is_NOT_query_dialect_renderable()
    {
        // $expr is a server error inside $elemMatch, so an aggregation-only node must never be admitted by the
        // query-dialect classifier.
        Assert.False(MongoQueryLanguageRenderer.IsQueryDialectRenderable(
            new MongoConvertExpression(new MongoElementRefExpression("D", typeof(double)), typeof(int))));
    }

    [Fact]
    public void Convert_node_reports_unrenderable_when_its_OPERAND_is()
    {
        // $elemMatch is $match-only (server error inside $expr); wrapping it in a convert must not make it
        // renderable.
        var unrenderable = new MongoElemMatchExpression(
            "Posts",
            new MongoBinaryExpression(MongoBinaryOperator.GreaterThan,
                new MongoElementRefExpression("Rank", typeof(int)), new MongoConstantExpression(0, forSerialization: null)),
            negated: false);

        Assert.False(MongoAggregationExpressionRenderer.CanRender(
            new MongoConvertExpression(unrenderable, typeof(int))));
    }

    // ------------------------------------------------------------------
    // MongoOuterFieldExpression — always renders at document root
    // ------------------------------------------------------------------

    [Fact]
    public void MongoOuterFieldExpression_renders_at_document_root_with_no_element_variable()
    {
        var status = GetProperty<Customer>("Status");
        var node = new MongoOuterFieldExpression(status, "Status");

        var rendered = MongoAggregationExpressionRenderer.Render(node, new PlaceholderTable());

        Assert.Equal((BsonValue)"$Status", rendered);
    }

    [Fact]
    public void MongoOuterFieldExpression_renders_at_document_root_even_inside_a_filter_scope()
    {
        // Unlike MongoFieldExpression, an elementVariable in scope must not change its rendering: it always
        // means the enclosing document.
        var status = GetProperty<Customer>("Status");
        var node = new MongoOuterFieldExpression(status, "Status");

        var rendered = MongoAggregationExpressionRenderer.Render(node, new PlaceholderTable(), elementVariable: "e");

        Assert.Equal((BsonValue)"$Status", rendered);
    }

    [Fact]
    public void MongoOuterFieldExpression_can_render()
    {
        var status = GetProperty<Customer>("Status");
        Assert.True(MongoAggregationExpressionRenderer.CanRender(new MongoOuterFieldExpression(status, "Status")));
    }

    [Fact]
    public void Renders_not_equal_null_lookup_check_as_ne_against_the_alias_field()
    {
        var node = new MongoLookupNullCheckExpression("_lookup_Manager", isNotNull: true);
        var placeholders = new PlaceholderTable();

        var rendered = MongoAggregationExpressionRenderer.Render(node, placeholders);

        // $ifNull-wrapped: after $lookup+$unwind(preserveNullAndEmptyArrays), an unmatched row's alias field is
        // missing, and aggregation $ne doesn't equate missing with null, so a bare comparison says "not null".
        Assert.Equal(
            new BsonDocument("$ne",
                new BsonArray { new BsonDocument("$ifNull", new BsonArray { "$_lookup_Manager", BsonNull.Value }), BsonNull.Value }),
            rendered);
    }

    [Fact]
    public void Renders_equal_null_lookup_check_as_eq_against_the_alias_field()
    {
        var node = new MongoLookupNullCheckExpression("_lookup_Manager", isNotNull: false);
        var placeholders = new PlaceholderTable();

        var rendered = MongoAggregationExpressionRenderer.Render(node, placeholders);

        // Same $ifNull wrap as the $ne case above.
        Assert.Equal(
            new BsonDocument("$eq",
                new BsonArray { new BsonDocument("$ifNull", new BsonArray { "$_lookup_Manager", BsonNull.Value }), BsonNull.Value }),
            rendered);
    }

    [Fact]
    public void CanRender_reports_true_for_a_lookup_null_check()
    {
        Assert.True(MongoAggregationExpressionRenderer.CanRender(
            new MongoLookupNullCheckExpression("_lookup_Manager", isNotNull: true)));
    }

    [Fact]
    public void MongoQuantifierExpression_Any_renders_as_anyElementTrue_over_map()
    {
        var elementPredicate = new MongoBinaryExpression(
            MongoBinaryOperator.GreaterThan,
            new MongoFieldExpression(NameProperty(), "Rank"),
            new MongoConstantExpression(5, forSerialization: null));
        var node = new MongoQuantifierExpression(
            new MongoElementRefExpression("Posts", typeof(object)), elementPredicate, MongoExpressionTranslator.MongoQuantifierKind.Any);

        var rendered = MongoAggregationExpressionRenderer.Render(node, new PlaceholderTable());

        Assert.Equal(
            BsonDocument.Parse(
                "{ $anyElementTrue: { $map: { input: { $ifNull: ['$Posts', []] }, as: 'e', "
                + "in: { $gt: ['$$e.Rank', 5] } } } }"),
            rendered);
    }

    [Fact]
    public void MongoQuantifierExpression_All_renders_as_allElementsTrue_over_map()
    {
        var elementPredicate = new MongoBinaryExpression(
            MongoBinaryOperator.GreaterThan,
            new MongoFieldExpression(NameProperty(), "Rank"),
            new MongoConstantExpression(5, forSerialization: null));
        var node = new MongoQuantifierExpression(
            new MongoElementRefExpression("Posts", typeof(object)), elementPredicate, MongoExpressionTranslator.MongoQuantifierKind.All);

        var rendered = MongoAggregationExpressionRenderer.Render(node, new PlaceholderTable());

        Assert.Equal(
            BsonDocument.Parse(
                "{ $allElementsTrue: { $map: { input: { $ifNull: ['$Posts', []] }, as: 'e', "
                + "in: { $gt: ['$$e.Rank', 5] } } } }"),
            rendered);
    }

    [Fact]
    public void MongoQuantifierExpression_can_render()
    {
        var node = new MongoQuantifierExpression(
            new MongoElementRefExpression("Posts", typeof(object)),
            new MongoFieldExpression(NameProperty(), "Active"),
            MongoExpressionTranslator.MongoQuantifierKind.Any);

        Assert.True(MongoAggregationExpressionRenderer.CanRender(node));
    }

    // ------------------------------------------------------------------
    // MongoRegexExpression with a field Term — $indexOfCP/$strLenCP rendering
    // ------------------------------------------------------------------

    [Fact]
    public void Renders_field_to_field_starts_with_via_indexOfCP()
    {
        var status = GetProperty<Customer>("Status");
        var nickname = GetProperty<Customer>("Nickname");
        var expr = new MongoRegexExpression(
            new MongoFieldExpression(status, "Status"), MongoRegexKind.StartsWith,
            new MongoFieldExpression(nickname, "Nickname"), negated: false);

        var result = MongoAggregationExpressionRenderer.Render(expr, new PlaceholderTable());

        Assert.Equal(
            """{ "$eq" : [{ "$indexOfCP" : ["$Status", "$Nickname"] }, 0] }""",
            result.ToJson());
    }

    [Fact]
    public void Renders_field_to_field_contains_via_indexOfCP()
    {
        var status = GetProperty<Customer>("Status");
        var expr = new MongoRegexExpression(
            new MongoFieldExpression(status, "Status"), MongoRegexKind.Contains,
            new MongoFieldExpression(status, "Status"), negated: false);

        var result = MongoAggregationExpressionRenderer.Render(expr, new PlaceholderTable());

        Assert.Equal(
            """{ "$gte" : [{ "$indexOfCP" : ["$Status", "$Status"] }, 0] }""",
            result.ToJson());
    }

    [Fact]
    public void Renders_negated_field_to_field_ends_with_via_indexOfCP_and_strLenCP()
    {
        var status = GetProperty<Customer>("Status");
        var nickname = GetProperty<Customer>("Nickname");
        var expr = new MongoRegexExpression(
            new MongoFieldExpression(status, "Status"), MongoRegexKind.EndsWith,
            new MongoFieldExpression(nickname, "Nickname"), negated: true);

        var result = MongoAggregationExpressionRenderer.Render(expr, new PlaceholderTable());

        Assert.Equal(
            """{ "$not" : [{ "$let" : { "vars" : { "start" : { "$subtract" : [{ "$strLenCP" : "$Status" }, { "$strLenCP" : "$Nickname" }] } }, "in" : { "$and" : [{ "$gte" : ["$$start", 0] }, { "$eq" : [{ "$indexOfCP" : ["$Status", "$Nickname", "$$start"] }, "$$start"] }] } } }] }""",
            result.ToJson());
    }

    // ------------------------------------------------------------------
    // MongoRegexExpression with a constant Term — every CanRender caller is in an aggregation scope with no
    // $regularExpression alternative, so it renders like the field-term case via $indexOfCP/$strLenCP.
    // ------------------------------------------------------------------

    [Fact]
    public void CanRender_reports_true_for_a_constant_term_regex()
    {
        var status = GetProperty<Customer>("Status");
        var node = new MongoRegexExpression(
            new MongoFieldExpression(status, "Status"), MongoRegexKind.StartsWith,
            new MongoConstantExpression("S", forSerialization: null), negated: false);

        Assert.True(MongoAggregationExpressionRenderer.CanRender(node));
    }

    [Fact]
    public void Renders_constant_term_starts_with_via_indexOfCP()
    {
        var status = GetProperty<Customer>("Status");
        var expr = new MongoRegexExpression(
            new MongoFieldExpression(status, "Status"), MongoRegexKind.StartsWith,
            new MongoConstantExpression("S", forSerialization: null), negated: false);

        var result = MongoAggregationExpressionRenderer.Render(expr, new PlaceholderTable());

        Assert.Equal(
            """{ "$eq" : [{ "$indexOfCP" : ["$Status", "S"] }, 0] }""",
            result.ToJson());
    }

    [Fact]
    public void Remove_sentinel_renders_as_the_REMOVE_system_variable()
    {
        var node = new MongoElementRefExpression(MongoElementRefExpression.RemoveSentinelPath, typeof(object));

        var rendered = MongoAggregationExpressionRenderer.Render(node, new PlaceholderTable());

        Assert.Equal("$$REMOVE", rendered.AsString);
    }

    // ------------------------------------------------------------------
    // A "$"-prefixed constant used as a $ifNull/$cond branch must be $literal-wrapped (as RenderProject does at
    // top level); otherwise MongoDB reads it as a field path.
    // ------------------------------------------------------------------

    [Fact]
    public void Coalesce_right_branch_dollar_prefixed_constant_is_literal_wrapped()
    {
        var status = GetProperty<Customer>("Status");
        var expr = new MongoCoalesceExpression(
            new MongoFieldExpression(status, "Status"),
            new MongoConstantExpression("$Year", forSerialization: null));

        var result = MongoAggregationExpressionRenderer.Render(expr, new PlaceholderTable());

        Assert.Equal(
            """{ "$ifNull" : ["$Status", { "$literal" : "$Year" }] }""",
            result.ToJson());
    }

    [Fact]
    public void Conditional_else_branch_dollar_prefixed_constant_is_literal_wrapped()
    {
        var status = GetProperty<Customer>("Status");
        var nickname = GetProperty<Customer>("Nickname");
        var expr = new MongoConditionalExpression(
            new MongoBinaryExpression(
                MongoBinaryOperator.Equal, new MongoFieldExpression(status, "Status"), new MongoFieldExpression(nickname, "Nickname")),
            new MongoConstantExpression("match", forSerialization: null),
            new MongoConstantExpression("$Year", forSerialization: null));

        var result = MongoAggregationExpressionRenderer.Render(expr, new PlaceholderTable());

        Assert.Equal(
            """{ "$cond" : { "if" : { "$eq" : ["$Status", "$Nickname"] }, "then" : { "$literal" : "match" }, "else" : { "$literal" : "$Year" } } }""",
            result.ToJson());
    }

    // --- Helper methods ---

    private static IProperty NameProperty() => GetProperty<Customer>("Age");
}
