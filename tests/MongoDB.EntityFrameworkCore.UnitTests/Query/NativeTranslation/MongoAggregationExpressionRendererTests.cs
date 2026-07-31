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

        // A MongoInExpression.
        yield return
        [
            new MongoInExpression(
                new MongoFieldExpression(age, "Age"), new MongoConstantExpression(new[] { 1, 2 }, age), negated: false)
        ];

        // A MongoUnaryExpression{Not}.
        yield return
        [
            new MongoUnaryExpression(
                MongoUnaryOperator.Not,
                new MongoBinaryExpression(MongoBinaryOperator.Equal, new MongoFieldExpression(age, "Age"),
                    new MongoConstantExpression(5, age)))
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
}
