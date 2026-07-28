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
/// Unit tests for <see cref="MongoExpressionNegator"/>, which produces the EXACT logical complement of a
/// translated predicate, or declines.
/// </summary>
public class MongoExpressionNegatorTests
{
    // --- Entity model used across tests ---

    private class Blog
    {
        public ObjectId Id { get; set; }
        public string Title { get; set; } = null!;
        public List<Post> Posts { get; set; } = [];
    }

    private class Post
    {
        public string Heading { get; set; } = null!;
        public int Rank { get; set; }
        public bool Flag { get; set; }
        public bool? OptionalFlag { get; set; }
    }

    // A property of the owned COLLECTION ELEMENT type (Post), for building element-relative field refs.
    private static IProperty GetPostProperty(string propertyName)
    {
        using var db = SingleEntityDbContext.Create<Blog>(mb => mb.Entity<Blog>().OwnsMany(b => b.Posts));
        return db.Model.FindEntityType(typeof(Blog))!
            .FindNavigation(nameof(Blog.Posts))!.TargetEntityType.FindProperty(propertyName)!;
    }

    private static MongoBinaryExpression Comparison(MongoBinaryOperator op, int value = 5)
    {
        var rank = GetPostProperty(nameof(Post.Rank));
        return new MongoBinaryExpression(
            op, new MongoFieldExpression(rank, "Rank"), new MongoConstantExpression(value, rank));
    }

    private static BsonValue RenderOf(MongoExpression node)
        => new MongoQueryLanguageRenderer().Render(node, new PlaceholderTable());

    [Fact]
    public void Equality_is_inverted_not_wrapped_because_eq_and_ne_partition()
    {
        Assert.True(MongoExpressionNegator.TryNegate(Comparison(MongoBinaryOperator.Equal), out var negated));
        var binary = Assert.IsType<MongoBinaryExpression>(negated);
        Assert.Equal(MongoBinaryOperator.NotEqual, binary.Operator);
        Assert.Equal(BsonDocument.Parse("{ Rank: { $ne: 5 } }"), RenderOf(negated));
    }

    [Fact]
    public void Inequality_is_inverted_back_to_equality()
    {
        Assert.True(MongoExpressionNegator.TryNegate(Comparison(MongoBinaryOperator.NotEqual), out var negated));
        Assert.Equal(MongoBinaryOperator.Equal, Assert.IsType<MongoBinaryExpression>(negated).Operator);
        Assert.Equal(BsonDocument.Parse("{ Rank: 5 }"), RenderOf(negated));
    }

    // NOTE: the brief specified a single [Theory]/[InlineData(MongoBinaryOperator..., ...)] here, but
    // MongoBinaryOperator is internal and a public [Theory] method cannot expose an internal type in its
    // signature (CS0051) while the class itself stays public — public is required to match every sibling
    // test class's convention in this directory (needed for xUnit discovery either way). Split into four
    // [Fact]s carrying the identical assertions instead.

    private static void AssertRelationalOperatorIsNotWrappedNeverInverted(MongoBinaryOperator op, string mql)
    {
        // The whole safety argument of this slice: $gt and $lte do NOT partition the value space (neither
        // matches a missing or null field), so inverting them would report All == true for a document whose
        // element lacks the field, where LINQ says false. $not over the operator document IS the exact
        // complement. Deleting the $not-wrap in favour of an inversion must make this test red.
        Assert.True(MongoExpressionNegator.TryNegate(Comparison(op), out var negated));
        var unary = Assert.IsType<MongoUnaryExpression>(negated);
        Assert.Equal(MongoUnaryOperator.Not, unary.Operator);
        Assert.Equal(BsonDocument.Parse($"{{ Rank: {{ $not: {{ {mql}: 5 }} }} }}"), RenderOf(negated));
    }

    [Fact]
    public void Relational_operators_are_not_wrapped_never_inverted_LessThan()
        => AssertRelationalOperatorIsNotWrappedNeverInverted(MongoBinaryOperator.LessThan, "$lt");

    [Fact]
    public void Relational_operators_are_not_wrapped_never_inverted_LessThanOrEqual()
        => AssertRelationalOperatorIsNotWrappedNeverInverted(MongoBinaryOperator.LessThanOrEqual, "$lte");

    [Fact]
    public void Relational_operators_are_not_wrapped_never_inverted_GreaterThan()
        => AssertRelationalOperatorIsNotWrappedNeverInverted(MongoBinaryOperator.GreaterThan, "$gt");

    [Fact]
    public void Relational_operators_are_not_wrapped_never_inverted_GreaterThanOrEqual()
        => AssertRelationalOperatorIsNotWrappedNeverInverted(MongoBinaryOperator.GreaterThanOrEqual, "$gte");

    [Fact]
    public void Conjunction_becomes_a_disjunction_of_complements_de_morgan()
    {
        var and = new MongoBinaryExpression(
            MongoBinaryOperator.AndAlso,
            Comparison(MongoBinaryOperator.Equal, 1),
            Comparison(MongoBinaryOperator.GreaterThan, 2));

        Assert.True(MongoExpressionNegator.TryNegate(and, out var negated));
        Assert.Equal(MongoBinaryOperator.OrElse, Assert.IsType<MongoBinaryExpression>(negated).Operator);
        Assert.Equal(
            BsonDocument.Parse("{ $or: [ { Rank: { $ne: 1 } }, { Rank: { $not: { $gt: 2 } } } ] }"),
            RenderOf(negated));
    }

    [Fact]
    public void Disjunction_becomes_a_conjunction_of_complements_de_morgan()
    {
        var or = new MongoBinaryExpression(
            MongoBinaryOperator.OrElse,
            Comparison(MongoBinaryOperator.Equal, 1),
            Comparison(MongoBinaryOperator.Equal, 2));

        Assert.True(MongoExpressionNegator.TryNegate(or, out var negated));
        Assert.Equal(MongoBinaryOperator.AndAlso, Assert.IsType<MongoBinaryExpression>(negated).Operator);
    }

    [Fact]
    public void In_flips_to_nin()
    {
        var rank = GetPostProperty(nameof(Post.Rank));
        var inExpr = new MongoInExpression(
            new MongoFieldExpression(rank, "Rank"),
            new MongoConstantExpression(new[] { 1, 2 }, rank),
            negated: false);

        Assert.True(MongoExpressionNegator.TryNegate(inExpr, out var negated));
        Assert.True(Assert.IsType<MongoInExpression>(negated).Negated);
        Assert.Equal(BsonDocument.Parse("{ Rank: { $nin: [1, 2] } }"), RenderOf(negated));
    }

    [Fact]
    public void Regex_flips_negated()
    {
        var heading = GetPostProperty(nameof(Post.Heading));
        var regex = new MongoRegexExpression(
            new MongoFieldExpression(heading, "Heading"),
            MongoRegexKind.StartsWith,
            new MongoConstantExpression("a", heading),
            negated: false);

        Assert.True(MongoExpressionNegator.TryNegate(regex, out var negated));
        Assert.True(Assert.IsType<MongoRegexExpression>(negated).Negated);
    }

    [Fact]
    public void ElemMatch_flips_negated_so_a_nested_quantifier_composes()
    {
        var elemMatch = new MongoElemMatchExpression(
            "Comments", Comparison(MongoBinaryOperator.Equal, 1), negated: false);

        Assert.True(MongoExpressionNegator.TryNegate(elemMatch, out var negated));
        Assert.True(Assert.IsType<MongoElemMatchExpression>(negated).Negated);
    }

    [Fact]
    public void Bare_Any_elem_match_flips_to_exists_false()
    {
        var bareAny = new MongoElemMatchExpression("Comments", elementPredicate: null, negated: false);

        Assert.True(MongoExpressionNegator.TryNegate(bareAny, out var negated));
        Assert.Equal(BsonDocument.Parse("{ 'Comments.0': { $exists: false } }"), RenderOf(negated));
    }

    [Fact]
    public void Bare_non_nullable_bool_field_is_negated_to_not_ne_true()
    {
        var flag = GetPostProperty(nameof(Post.Flag));
        var field = new MongoFieldExpression(flag, "Flag");

        Assert.True(MongoExpressionNegator.TryNegate(field, out var negated));
        var unary = Assert.IsType<MongoUnaryExpression>(negated);
        Assert.Equal(MongoUnaryOperator.Not, unary.Operator);
        Assert.Same(field, unary.Operand);
        Assert.Equal(BsonDocument.Parse("{ Flag: { $ne: true } }"), RenderOf(negated));
    }

    [Fact]
    public void Nullable_bool_field_declines()
    {
        // The guard is `!field.Property.IsNullable`: a nullable bool field is NOT admitted as a bare
        // predicate by the translator in the first place (a nullable bool used as a predicate is ambiguous
        // between false and null/missing), so its negation must decline rather than guess.
        var optionalFlag = GetPostProperty(nameof(Post.OptionalFlag));
        var field = new MongoFieldExpression(optionalFlag, "OptionalFlag");

        Assert.False(MongoExpressionNegator.TryNegate(field, out var negated));
        Assert.Null(negated);
    }

    [Fact]
    public void Double_negation_returns_the_inner_node()
    {
        var inner = Comparison(MongoBinaryOperator.GreaterThan);
        var not = new MongoUnaryExpression(MongoUnaryOperator.Not, inner);

        Assert.True(MongoExpressionNegator.TryNegate(not, out var negated));
        Assert.Same(inner, negated);
    }

    [Fact]
    public void Field_to_field_comparison_declines()
    {
        // No query-dialect rendering ⇒ no query-dialect COMPLEMENT. This must decline in the negator itself,
        // not downstream: mirroring Equal→NotEqual here would produce a node RenderNode sends to the $expr
        // catch-all, and $expr inside $elemMatch is a HARD SERVER ERROR — an execution-time throw rather than
        // a clean fallback.
        var rank = GetPostProperty(nameof(Post.Rank));
        var fieldToField = new MongoBinaryExpression(
            MongoBinaryOperator.GreaterThan,
            new MongoFieldExpression(rank, "Rank"),
            new MongoFieldExpression(rank, "Rank"));

        Assert.False(MongoExpressionNegator.TryNegate(fieldToField, out var negated));
        Assert.Null(negated);
    }

    [Fact]
    public void Arithmetic_node_declines()
    {
        var rank = GetPostProperty(nameof(Post.Rank));
        var arithmetic = new MongoBinaryExpression(
            MongoBinaryOperator.Add,
            new MongoFieldExpression(rank, "Rank"),
            new MongoConstantExpression(1, rank));

        Assert.False(MongoExpressionNegator.TryNegate(arithmetic, out _));
    }

    [Fact]
    public void A_declining_child_declines_the_whole_conjunction_with_no_partial_output()
    {
        var rank = GetPostProperty(nameof(Post.Rank));
        var and = new MongoBinaryExpression(
            MongoBinaryOperator.AndAlso,
            Comparison(MongoBinaryOperator.Equal, 1),
            new MongoBinaryExpression(
                MongoBinaryOperator.GreaterThan,
                new MongoFieldExpression(rank, "Rank"),
                new MongoFieldExpression(rank, "Rank")));

        Assert.False(MongoExpressionNegator.TryNegate(and, out var negated));
        Assert.Null(negated);
    }

    [Fact]
    public void Node_that_is_not_query_dialect_renderable_declines_even_when_its_own_case_would_flip_it()
    {
        // TryNegateCore's `case MongoRegexExpression regex:` flips Negated unconditionally, with no check on
        // Term — in isolation it would happily "negate" this node. It must never get the chance: a
        // parameterized regex term is declined by IsQueryDialectRenderable (only a constant term is baked
        // into a native pattern; see RenderRegex), so TryNegate's outer gate must refuse this node BEFORE
        // TryNegateCore's per-case switch ever runs. Removing that outer gate must make this test red.
        var heading = GetPostProperty(nameof(Post.Heading));
        var regex = new MongoRegexExpression(
            new MongoFieldExpression(heading, "Heading"),
            MongoRegexKind.Contains,
            new MongoParameterExpression("term", heading),
            negated: false);

        Assert.False(MongoExpressionNegator.TryNegate(regex, out var negated));
        Assert.Null(negated);
    }

    [Fact]
    public void Every_successful_negation_is_query_dialect_renderable_and_renders_without_expr()
    {
        // THE OUTPUT-DOMAIN INVARIANT. This negation is emitted inside $elemMatch, where $expr is a hard
        // server error, so a negator that produced a node the renderer sent to the $expr catch-all would make
        // the whole query throw at execution time under Native as well as NativeOnly.
        var rank = GetPostProperty(nameof(Post.Rank));
        var heading = GetPostProperty(nameof(Post.Heading));
        var flag = GetPostProperty(nameof(Post.Flag));
        MongoExpression[] inputs =
        [
            Comparison(MongoBinaryOperator.Equal),
            Comparison(MongoBinaryOperator.NotEqual),
            Comparison(MongoBinaryOperator.LessThan),
            Comparison(MongoBinaryOperator.LessThanOrEqual),
            Comparison(MongoBinaryOperator.GreaterThan),
            Comparison(MongoBinaryOperator.GreaterThanOrEqual),
            new MongoBinaryExpression(MongoBinaryOperator.AndAlso, Comparison(MongoBinaryOperator.Equal, 1), Comparison(MongoBinaryOperator.GreaterThan, 2)),
            new MongoBinaryExpression(MongoBinaryOperator.OrElse, Comparison(MongoBinaryOperator.Equal, 1), Comparison(MongoBinaryOperator.Equal, 2)),
            new MongoInExpression(new MongoFieldExpression(rank, "Rank"), new MongoConstantExpression(new[] { 1 }, rank), negated: false),
            new MongoRegexExpression(new MongoFieldExpression(heading, "Heading"), MongoRegexKind.Contains, new MongoConstantExpression("a", heading), negated: false),
            new MongoElemMatchExpression("Comments", Comparison(MongoBinaryOperator.Equal, 1), negated: false),
            new MongoElemMatchExpression("Comments", elementPredicate: null, negated: false),
            new MongoUnaryExpression(MongoUnaryOperator.Not, Comparison(MongoBinaryOperator.GreaterThan)),
            new MongoFieldExpression(flag, "Flag"),
        ];

        foreach (var input in inputs)
        {
            Assert.True(MongoExpressionNegator.TryNegate(input, out var negated), $"failed to negate {input.GetType().Name}");
            Assert.True(
                MongoQueryLanguageRenderer.IsQueryDialectRenderable(negated),
                $"negation of {input.GetType().Name} is not query-dialect renderable");
            var rendered = RenderOf(negated).AsBsonDocument;
            Assert.False(rendered.Contains("$expr"), $"negation of {input.GetType().Name} rendered $expr");
        }
    }

    [Fact]
    public void Negation_is_an_involution_on_the_supported_set()
    {
        // ¬¬X must render identically to X. A rule that is not an exact complement generally fails this.
        var flag = GetPostProperty(nameof(Post.Flag));
        MongoExpression[] inputs =
        [
            Comparison(MongoBinaryOperator.Equal),
            Comparison(MongoBinaryOperator.GreaterThan),
            new MongoBinaryExpression(MongoBinaryOperator.AndAlso, Comparison(MongoBinaryOperator.Equal, 1), Comparison(MongoBinaryOperator.GreaterThan, 2)),
            new MongoElemMatchExpression("Comments", Comparison(MongoBinaryOperator.Equal, 1), negated: false),
            new MongoFieldExpression(flag, "Flag"),
        ];

        foreach (var input in inputs)
        {
            Assert.True(MongoExpressionNegator.TryNegate(input, out var once));
            Assert.True(MongoExpressionNegator.TryNegate(once, out var twice));
            Assert.Equal(RenderOf(input), RenderOf(twice));
        }
    }
}
