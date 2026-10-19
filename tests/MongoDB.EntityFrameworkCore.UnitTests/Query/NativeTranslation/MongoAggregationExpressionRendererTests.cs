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
/// Unit tests for <see cref="MongoAggregationExpressionRenderer"/>, which renders dialect-agnostic
/// <see cref="MongoExpression"/> subtrees into MongoDB aggregation expressions (the body inside <c>{ $expr: … }</c>).
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
        public bool IsActive { get; set; }
    }

    private static IProperty GetProperty<T>(string propertyName) where T : class
    {
        using var db = SingleEntityDbContext.Create<T>();
        return db.Model.FindEntityType(typeof(T))!.FindProperty(propertyName)!;
    }

    // ------------------------------------------------------------------
    // Test 1: field-to-field comparison → { $eq: ['$Age', '$Score'] }
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
    // Test 2: arithmetic operand → { $gt: [ { $add: ['$Age', '$Score'] }, 5 ] }
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

        // The INNER array path is element-relative to the OUTER variable, and the inner element
        // predicate is relative to the inner variable. Getting either wrong reads the wrong array.
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
    // EF-434: integer division
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
        // The negative half of the pair: a non-integral division must NOT be truncated. Asserted separately
        // from the row above so that collapsing the two operators into one arm goes red rather than green.
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
    // EF-413: MongoInExpression / MongoUnaryExpression aggregation-dialect arms
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
    public void CanRender_reports_false_for_MongoInExpression_over_unrenderable_values()
    {
        // Neither a constant enumerable nor a parameter — CanRenderInValues must decline this shape the same
        // way RenderInValues would throw on it, so the two never disagree.
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

    // NOTE ON TEST SHAPE: MongoExpression is internal, and a public [Theory] method cannot expose an internal
    // type in its signature (CS0051) while the test class stays public (required for xUnit discovery — see the
    // identical, already-established idiom in MongoExpressionNegatorTests.cs). The [MemberData] rows are boxed
    // as `object` here and cast back to `MongoExpression` inside the method, which keeps the brief's requested
    // [Theory]/[MemberData]-over-node-collections shape intact rather than falling back to per-row [Fact]s.

    [Theory]
    [MemberData(nameof(RenderableNodes))]
    public void CanRender_admits_exactly_what_Render_renders(object node)
    {
        var expr = (MongoExpression)node;
        Assert.True(MongoAggregationExpressionRenderer.CanRender(expr));
        // Non-vacuous: prove Render really does handle it, so the two cannot drift silently.
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

        // EF-413: a MongoInExpression (client-collection Contains → $in).
        yield return
        [
            new MongoInExpression(
                new MongoFieldExpression(age, "Age"), new MongoConstantExpression(new[] { 1, 2 }, age), negated: false)
        ];

        // EF-413: a MongoUnaryExpression{Not} over a renderable operand.
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
        var age = GetProperty<Customer>("Age");

        // A MongoRegexExpression.
        yield return
        [
            new MongoRegexExpression(
                new MongoFieldExpression(age, "Age"), MongoRegexKind.StartsWith,
                new MongoConstantExpression("x", forSerialization: null), negated: false)
        ];

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
                new MongoRegexExpression(
                    new MongoFieldExpression(age, "Age"), MongoRegexKind.StartsWith,
                    new MongoConstantExpression("x", forSerialization: null), negated: false),
                typeof(int))
        ];
    }

    // ------------------------------------------------------------------
    // MongoConvertExpression — the $toX node (EF-322 slice A1, Task 3)
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
        // MQL has no $toShort/$toUInt/$toFloat, and the driver's own LINQ provider throws for these targets too —
        // so declining is the same boundary the oracle has, not a coverage choice.
        Assert.Null(MongoConvertExpression.ToOperatorFor(target));
        Assert.False(MongoAggregationExpressionRenderer.CanRender(
            new MongoConvertExpression(new MongoElementRefExpression("I", typeof(int)), target)));
    }

    [Fact]
    public void Convert_node_is_NOT_query_dialect_renderable()
    {
        // LOAD-BEARING: $expr is a hard server error inside $elemMatch, so a node that only the aggregation
        // dialect can express must never be admitted by the query-dialect classifier.
        Assert.False(MongoQueryLanguageRenderer.IsQueryDialectRenderable(
            new MongoConvertExpression(new MongoElementRefExpression("D", typeof(double)), typeof(int))));
    }

    [Fact]
    public void Convert_node_reports_unrenderable_when_its_OPERAND_is()
    {
        // MongoRegexExpression is one of the node kinds the aggregation dialect cannot express (CanRender admits
        // field/element refs, constants/parameters, binaries over its listed operators, the two size nodes, $in,
        // and Not over a renderable operand — nothing else). Wrapping it in a convert must not launder it into
        // renderability.
        var age = GetProperty<Customer>("Age");
        var unrenderable = new MongoRegexExpression(
            new MongoFieldExpression(age, "Age"), MongoRegexKind.StartsWith,
            new MongoConstantExpression("x", forSerialization: null), negated: false);

        Assert.False(MongoAggregationExpressionRenderer.CanRender(
            new MongoConvertExpression(unrenderable, typeof(int))));
    }
}
