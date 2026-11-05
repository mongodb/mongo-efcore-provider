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
/// Unit tests for <see cref="MongoExpressionNegator"/>, which produces the exact logical complement of a
/// translated predicate, or declines.
/// </summary>
public class MongoExpressionNegatorTests
{

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
        public List<string> Tags { get; set; } = [];
    }

    // A property of the owned collection element type (Post), for element-relative field refs.
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

    // Four [Fact]s rather than a [Theory]: MongoBinaryOperator is internal, and a public test method can't expose
    // it in its signature (CS0051).

    private static void AssertRelationalOperatorIsNotWrappedNeverInverted(MongoBinaryOperator op, string mql)
    {
        // $gt and $lte don't partition the value space (neither matches a missing or null field), so inverting
        // would make All report true for an element lacking the field where LINQ says false. $not over the
        // operator document is the exact complement.
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

    // An $in over a MongoValueListExpression (per-element parameterized values) must flip Negated like the
    // constant-enumerable case, and not be dropped by TryNegate's query-dialect gate.
    [Fact]
    public void In_over_value_list_flips_to_nin()
    {
        var rank = GetPostProperty(nameof(Post.Rank));
        var inExpr = new MongoInExpression(
            new MongoFieldExpression(rank, "Rank"),
            new MongoValueListExpression(
            [
                new MongoParameterExpression("p0", rank),
                new MongoParameterExpression("p1", rank)
            ]),
            negated: false);

        Assert.True(MongoExpressionNegator.TryNegate(inExpr, out var negated));
        var negatedIn = Assert.IsType<MongoInExpression>(negated);
        Assert.True(negatedIn.Negated);

        var placeholders = new PlaceholderTable();
        var rendered = Assert.IsType<BsonDocument>(new MongoQueryLanguageRenderer().Render(negatedIn, placeholders));
        var rankCond = Assert.IsType<BsonDocument>(rendered["Rank"]);
        var ninArray = Assert.IsType<BsonArray>(rankCond["$nin"]);
        Assert.Equal(2, ninArray.Count);
        Assert.Equal("p0", placeholders.Entries[0].Name);
        Assert.Equal("p1", placeholders.Entries[1].Name);
    }

    // MongoArrayContainsExpression's negator arm. Reached via All(p => p.ArrayField.Contains(c)), which negates
    // the element predicate to build a negated $elemMatch — not via the top-level Not path, which only sees this
    // shape after EF's AllAnyToContainsRewritingExpressionVisitor and never reaches the negator.
    [Fact]
    public void Array_contains_flips_negated()
    {
        var tags = GetPostProperty(nameof(Post.Tags));
        var containsExpr = new MongoArrayContainsExpression(
            new MongoFieldExpression(tags, "Tags"),
            new MongoConstantExpression(new BsonString("keep"), forSerialization: null),
            negated: false);

        Assert.True(MongoExpressionNegator.TryNegate(containsExpr, out var negated));
        var flipped = Assert.IsType<MongoArrayContainsExpression>(negated);
        Assert.True(flipped.Negated);
        Assert.Equal(BsonDocument.Parse("{ Tags: { $ne: \"keep\" } }"), RenderOf(negated));

        Assert.True(MongoExpressionNegator.TryNegate(flipped, out var doubleNegated));
        Assert.False(Assert.IsType<MongoArrayContainsExpression>(doubleNegated).Negated);
        Assert.Equal(BsonDocument.Parse("{ Tags: \"keep\" }"), RenderOf(doubleNegated));
    }

    // End to end: Posts.All(p => p.Tags.Contains("keep")) →
    // { Posts: { $not: { $elemMatch: { Tags: { $ne: "keep" } } } } }. All(pred) negates the element predicate
    // (firing the arm above in the real pipeline) and wraps in a negated $elemMatch.
    [Fact]
    public void All_over_array_contains_negates_via_the_new_arm_and_renders_the_expected_elem_match()
    {
        using var db = SingleEntityDbContext.Create<Blog>(mb => mb.Entity<Blog>().OwnsMany(b => b.Posts));
        var entityType = db.Model.FindEntityType(typeof(Blog))!;
        var translator = new MongoExpressionTranslator(entityType);

        System.Linq.Expressions.Expression<System.Func<Blog, bool>> predicate =
            b => b.Posts.All(p => p.Tags.Contains("keep"));

        Assert.True(translator.TryTranslate(predicate.Body, out var result));
        var elemMatch = Assert.IsType<MongoElemMatchExpression>(result);
        Assert.True(elemMatch.Negated);
        var innerContains = Assert.IsType<MongoArrayContainsExpression>(elemMatch.ElementPredicate);
        Assert.True(innerContains.Negated);

        Assert.Equal(
            BsonDocument.Parse("{ Posts: { $not: { $elemMatch: { Tags: { $ne: \"keep\" } } } } }"),
            RenderOf(result!));
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

    // A field-to-field MongoRegexExpression (e.g. `c.ContactName.StartsWith(c.ContactName)`) fails
    // IsQueryDialectRenderable ($regularExpression needs a literal pattern). TryNegate special-cases it, like
    // MongoQuantifierExpression, because its only negation call site (NativeCardinalityBinder's root-level
    // All(pred) arm) puts it in a top-level $match conjunct, where $expr is legal. Negation just flips Negated.
    [Fact]
    public void Regex_with_field_to_field_term_negates_despite_not_being_query_dialect_renderable()
    {
        var heading = GetPostProperty(nameof(Post.Heading));
        var regex = new MongoRegexExpression(
            new MongoFieldExpression(heading, "Heading"),
            MongoRegexKind.StartsWith,
            new MongoFieldExpression(heading, "Heading"),
            negated: false);

        Assert.True(MongoExpressionNegator.TryNegate(regex, out var negated));
        var negatedRegex = Assert.IsType<MongoRegexExpression>(negated);
        Assert.True(negatedRegex.Negated);
        Assert.IsType<MongoFieldExpression>(negatedRegex.Term);
    }

    [Fact]
    public void ElemMatch_flips_negated_so_a_nested_quantifier_composes()
    {
        var elemMatch = new MongoElemMatchExpression(
            "Comments", Comparison(MongoBinaryOperator.Equal, 1), negated: false);

        Assert.True(MongoExpressionNegator.TryNegate(elemMatch, out var negated));
        Assert.True(Assert.IsType<MongoElemMatchExpression>(negated).Negated);
    }

    // MongoQuantifierExpression (correlated Any/All) has no Negated flag: $anyElementTrue/$allElementsTrue are
    // De Morgan duals, so negation flips Kind and recurses into ElementPredicate. Reached from
    // NativeCardinalityBinder's root-level All(pred) arm.
    [Fact]
    public void Quantifier_Any_negates_to_All_with_the_negated_element_predicate()
    {
        var quantifier = new MongoQuantifierExpression(
            new MongoElementRefExpression("Posts", typeof(object)),
            Comparison(MongoBinaryOperator.Equal, 1),
            MongoExpressionTranslator.MongoQuantifierKind.Any);

        Assert.True(MongoExpressionNegator.TryNegate(quantifier, out var negated));
        var negatedQuantifier = Assert.IsType<MongoQuantifierExpression>(negated);
        Assert.Equal(MongoExpressionTranslator.MongoQuantifierKind.All, negatedQuantifier.Kind);
        Assert.Same(quantifier.ArrayPath, negatedQuantifier.ArrayPath);
        var negatedElement = Assert.IsType<MongoBinaryExpression>(negatedQuantifier.ElementPredicate);
        Assert.Equal(MongoBinaryOperator.NotEqual, negatedElement.Operator);
    }

    [Fact]
    public void Quantifier_All_negates_to_Any_with_the_negated_element_predicate()
    {
        var quantifier = new MongoQuantifierExpression(
            new MongoElementRefExpression("Posts", typeof(object)),
            Comparison(MongoBinaryOperator.GreaterThan, 5),
            MongoExpressionTranslator.MongoQuantifierKind.All);

        Assert.True(MongoExpressionNegator.TryNegate(quantifier, out var negated));
        var negatedQuantifier = Assert.IsType<MongoQuantifierExpression>(negated);
        Assert.Equal(MongoExpressionTranslator.MongoQuantifierKind.Any, negatedQuantifier.Kind);
        Assert.Same(quantifier.ArrayPath, negatedQuantifier.ArrayPath);
        // A relational operator is $not-wrapped, not inverted.
        var negatedElement = Assert.IsType<MongoUnaryExpression>(negatedQuantifier.ElementPredicate);
        Assert.Equal(MongoUnaryOperator.Not, negatedElement.Operator);
    }

    [Fact]
    public void Quantifier_negation_is_an_involution()
    {
        var quantifier = new MongoQuantifierExpression(
            new MongoElementRefExpression("Posts", typeof(object)),
            Comparison(MongoBinaryOperator.Equal, 1),
            MongoExpressionTranslator.MongoQuantifierKind.Any);

        Assert.True(MongoExpressionNegator.TryNegate(quantifier, out var once));
        Assert.True(MongoExpressionNegator.TryNegate(once, out var twice));
        var twiceQuantifier = Assert.IsType<MongoQuantifierExpression>(twice);
        Assert.Equal(MongoExpressionTranslator.MongoQuantifierKind.Any, twiceQuantifier.Kind);
        Assert.Equal(
            MongoBinaryOperator.Equal, Assert.IsType<MongoBinaryExpression>(twiceQuantifier.ElementPredicate).Operator);
    }

    [Fact]
    public void Quantifier_negation_declines_when_the_element_predicate_has_no_exact_complement()
    {
        // A declining element predicate must decline the whole negation; a quantifier with an un-negated
        // ElementPredicate would be silently wrong. Arithmetic (not a predicate) is used because field-to-field
        // comparisons now negate exactly inside a quantifier (see Field_to_field_within_a_quantifier...).
        var rank = GetPostProperty(nameof(Post.Rank));
        var arithmetic = new MongoBinaryExpression(
            MongoBinaryOperator.Add,
            new MongoFieldExpression(rank, "Rank"),
            new MongoConstantExpression(1, rank));
        var quantifier = new MongoQuantifierExpression(
            new MongoElementRefExpression("Posts", typeof(object)),
            arithmetic,
            MongoExpressionTranslator.MongoQuantifierKind.Any);

        Assert.False(MongoExpressionNegator.TryNegate(quantifier, out var negated));
        Assert.Null(negated);
    }

    // A correlated quantifier's ElementPredicate shape (p.Field == b.Field; in real output the outer side is a
    // MongoOuterFieldExpression, but the negator treats any non-query-dialect comparison alike). Only the wider
    // in-aggregation comparison case admits it, not the ordinary IsQueryNativeComparison-gated one.
    [Fact]
    public void Field_to_field_comparison_inside_a_quantifier_element_predicate_negates_exactly()
    {
        var rank = GetPostProperty(nameof(Post.Rank));
        var fieldToField = new MongoBinaryExpression(
            MongoBinaryOperator.Equal,
            new MongoFieldExpression(rank, "Rank"),
            new MongoFieldExpression(rank, "Rank"));

        // The bare comparison alone still declines through the gated path, which $elemMatch call sites rely on.
        Assert.False(MongoExpressionNegator.TryNegate(fieldToField, out _));

        var quantifier = new MongoQuantifierExpression(
            new MongoElementRefExpression("Posts", typeof(object)),
            fieldToField,
            MongoExpressionTranslator.MongoQuantifierKind.Any);

        Assert.True(MongoExpressionNegator.TryNegate(quantifier, out var negated));
        var negatedQuantifier = Assert.IsType<MongoQuantifierExpression>(negated);
        Assert.Equal(MongoExpressionTranslator.MongoQuantifierKind.All, negatedQuantifier.Kind);
        var negatedElement = Assert.IsType<MongoBinaryExpression>(negatedQuantifier.ElementPredicate);
        Assert.Equal(MongoBinaryOperator.NotEqual, negatedElement.Operator);
    }

    // The wider in-aggregation comparison case must be structurally unreachable outside a quantifier's
    // ElementPredicate recursion. Tested with the shape nested in a conjunction reached from the public TryNegate:
    // if inAggregationContext stopped propagating as false through AndAlso/OrElse, this would succeed with a
    // result that is wrong for $elemMatch.
    [Fact]
    public void Field_to_field_comparison_nested_in_a_top_level_conjunction_still_declines()
    {
        var rank = GetPostProperty(nameof(Post.Rank));
        var fieldToField = new MongoBinaryExpression(
            MongoBinaryOperator.Equal,
            new MongoFieldExpression(rank, "Rank"),
            new MongoFieldExpression(rank, "Rank"));
        var and = new MongoBinaryExpression(
            MongoBinaryOperator.AndAlso, Comparison(MongoBinaryOperator.Equal, 1), fieldToField);

        Assert.False(MongoExpressionNegator.TryNegate(and, out var negated));
        Assert.Null(negated);
    }

    [Fact]
    public void Bare_Any_elem_match_flips_to_exists_false()
    {
        // Bare Any() is represented as "Count >= 1" (see MongoElemMatchExpression). !Any() inverts to Count < 1,
        // rendered through the same array-index existence form.
        var bareAny = new MongoBinaryExpression(
            MongoBinaryOperator.GreaterThanOrEqual,
            new MongoSizeExpression("Comments", typeof(int), nullSafe: true),
            new MongoConstantExpression(1, null));

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
        // Guarded by `!field.Property.IsNullable`: a nullable bool isn't admitted as a bare predicate (false vs.
        // null/missing is ambiguous), so its negation must decline.
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
        // No query-dialect rendering means no query-dialect complement. Must decline here: a mirrored node would go
        // to the $expr catch-all, and $expr inside $elemMatch is a server error, not a clean fallback.
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
    public void Parameterized_regex_term_is_query_dialect_renderable_and_negates_by_flipping()
    {
        // A parameterized regex term is query-dialect-renderable (escape/anchor deferred to a placeholder resolved
        // at Build time — see PlaceholderTable.CreateRegexPlaceholder), so it negates like a constant term.
        var heading = GetPostProperty(nameof(Post.Heading));
        var regex = new MongoRegexExpression(
            new MongoFieldExpression(heading, "Heading"),
            MongoRegexKind.Contains,
            new MongoParameterExpression("term", heading),
            negated: false);

        Assert.True(MongoExpressionNegator.TryNegate(regex, out var negated));
        var negatedRegex = Assert.IsType<MongoRegexExpression>(negated);
        Assert.True(negatedRegex.Negated);
    }

    [Fact]
    public void Every_successful_negation_is_query_dialect_renderable_and_renders_without_expr()
    {
        // Output-domain invariant: negations are emitted inside $elemMatch, where $expr is a server error, so no
        // negation may produce a node the renderer sends to the $expr catch-all.
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
            new MongoBinaryExpression(
                MongoBinaryOperator.GreaterThanOrEqual,
                new MongoSizeExpression("Comments", typeof(int), nullSafe: true),
                new MongoConstantExpression(1, null)),
            new MongoUnaryExpression(MongoUnaryOperator.Not, Comparison(MongoBinaryOperator.GreaterThan)),
            new MongoFieldExpression(flag, "Flag"),
            new MongoArrayContainsExpression(
                new MongoFieldExpression(GetPostProperty(nameof(Post.Tags)), "Tags"),
                new MongoConstantExpression(new BsonString("keep"), forSerialization: null),
                negated: false),
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

    private static MongoBinaryExpression Count(MongoBinaryOperator op, int threshold)
        => new(op,
            new MongoSizeExpression("Posts", typeof(int), nullSafe: true),
            new MongoConstantExpression(threshold, null));

    // Count-comparison tests use [Fact]s + a private helper rather than [Theory] (CS0051, as above). A count comparison
    // renders as { "path.k": { $exists: … } } and $exists partitions the document set, so inverting the operator is the
    // exact complement, unlike { $gt: 5 } / { $lte: 5 } on a scalar, which both miss a missing field.
    private static void AssertCountComparisonIsInvertedNotWrapped(
        MongoBinaryOperator op, MongoBinaryOperator expected)
    {
        Assert.True(MongoExpressionNegator.TryNegate(Count(op, 2), out var negated));

        var comparison = Assert.IsType<MongoBinaryExpression>(negated);
        Assert.Equal(expected, comparison.Operator);
        Assert.IsType<MongoSizeExpression>(comparison.Left);
    }

    [Fact]
    public void Count_comparison_inverts_GreaterThan_to_LessThanOrEqual()
        => AssertCountComparisonIsInvertedNotWrapped(
            MongoBinaryOperator.GreaterThan, MongoBinaryOperator.LessThanOrEqual);

    [Fact]
    public void Count_comparison_inverts_GreaterThanOrEqual_to_LessThan()
        => AssertCountComparisonIsInvertedNotWrapped(
            MongoBinaryOperator.GreaterThanOrEqual, MongoBinaryOperator.LessThan);

    [Fact]
    public void Count_comparison_inverts_LessThan_to_GreaterThanOrEqual()
        => AssertCountComparisonIsInvertedNotWrapped(
            MongoBinaryOperator.LessThan, MongoBinaryOperator.GreaterThanOrEqual);

    [Fact]
    public void Count_comparison_inverts_LessThanOrEqual_to_GreaterThan()
        => AssertCountComparisonIsInvertedNotWrapped(
            MongoBinaryOperator.LessThanOrEqual, MongoBinaryOperator.GreaterThan);

    [Fact]
    public void Count_comparison_inverts_Equal_to_NotEqual()
        => AssertCountComparisonIsInvertedNotWrapped(
            MongoBinaryOperator.Equal, MongoBinaryOperator.NotEqual);

    [Fact]
    public void Count_comparison_inverts_NotEqual_to_Equal()
        => AssertCountComparisonIsInvertedNotWrapped(
            MongoBinaryOperator.NotEqual, MongoBinaryOperator.Equal);

    [Fact]
    public void The_admitted_count_set_is_closed_under_inversion()
    {
        // Every inverse of an admissible count comparison is itself admissible, so the negator never hands the
        // renderer a form the classifier rejects. One property test over the whole admitted set.
        (MongoBinaryOperator Op, int Threshold)[] admitted =
        [
            (MongoBinaryOperator.GreaterThan, 0), (MongoBinaryOperator.GreaterThan, 5),
            (MongoBinaryOperator.GreaterThanOrEqual, 1), (MongoBinaryOperator.GreaterThanOrEqual, 5),
            (MongoBinaryOperator.LessThan, 1), (MongoBinaryOperator.LessThan, 5),
            (MongoBinaryOperator.LessThanOrEqual, 0), (MongoBinaryOperator.LessThanOrEqual, 5),
            (MongoBinaryOperator.Equal, 0), (MongoBinaryOperator.Equal, 5),
            (MongoBinaryOperator.NotEqual, 0), (MongoBinaryOperator.NotEqual, 5)
        ];

        foreach (var (op, threshold) in admitted)
        {
            var because = $"{op} vs {threshold}";
            var original = Count(op, threshold);
            Assert.True(MongoQueryLanguageRenderer.IsQueryDialectRenderable(original), because);

            Assert.True(MongoExpressionNegator.TryNegate(original, out var negated), because);
            Assert.True(MongoQueryLanguageRenderer.IsQueryDialectRenderable(negated), because);

            // Involution: negating twice returns the original.
            Assert.True(MongoExpressionNegator.TryNegate(negated, out var twice), because);
            Assert.Equal(op, Assert.IsType<MongoBinaryExpression>(twice).Operator);
        }
    }

    [Fact]
    public void A_parameterized_count_comparison_declines()
    {
        // The negator's entry gate is IsQueryDialectRenderable, and the $expr tier isn't query dialect. Inversion
        // would be exact there (operands are numbers via $ifNull), so !(Count > @param) falling back is a coverage
        // gap, not a correctness issue.
        var parameterized = new MongoBinaryExpression(
            MongoBinaryOperator.GreaterThan,
            new MongoSizeExpression("Posts", typeof(int), nullSafe: true),
            new MongoParameterExpression("__n", null));

        Assert.False(MongoExpressionNegator.TryNegate(parameterized, out _));
    }

    [Fact]
    public void A_degenerate_count_comparison_declines()
    {
        Assert.False(MongoExpressionNegator.TryNegate(
            Count(MongoBinaryOperator.GreaterThanOrEqual, 0), out _));
    }

    [Fact]
    public void A_count_comparison_negates_inside_a_conjunction_via_de_morgan()
    {
        var conjunction = new MongoBinaryExpression(
            MongoBinaryOperator.AndAlso,
            Count(MongoBinaryOperator.GreaterThan, 2),
            Count(MongoBinaryOperator.LessThan, 9));

        Assert.True(MongoExpressionNegator.TryNegate(conjunction, out var negated));

        var or = Assert.IsType<MongoBinaryExpression>(negated);
        Assert.Equal(MongoBinaryOperator.OrElse, or.Operator);
        Assert.Equal(MongoBinaryOperator.LessThanOrEqual, Assert.IsType<MongoBinaryExpression>(or.Left).Operator);
        Assert.Equal(MongoBinaryOperator.GreaterThanOrEqual, Assert.IsType<MongoBinaryExpression>(or.Right).Operator);
    }

    [Fact]
    public void Negation_is_an_involution_on_the_supported_set()
    {
        // ¬¬X must render identically to X; a non-exact complement generally fails this.
        var flag = GetPostProperty(nameof(Post.Flag));
        MongoExpression[] inputs =
        [
            Comparison(MongoBinaryOperator.Equal),
            Comparison(MongoBinaryOperator.GreaterThan),
            new MongoBinaryExpression(MongoBinaryOperator.AndAlso, Comparison(MongoBinaryOperator.Equal, 1), Comparison(MongoBinaryOperator.GreaterThan, 2)),
            new MongoElemMatchExpression("Comments", Comparison(MongoBinaryOperator.Equal, 1), negated: false),
            new MongoFieldExpression(flag, "Flag"),
            new MongoArrayContainsExpression(
                new MongoFieldExpression(GetPostProperty(nameof(Post.Tags)), "Tags"),
                new MongoConstantExpression(new BsonString("keep"), forSerialization: null),
                negated: false),
        ];

        foreach (var input in inputs)
        {
            Assert.True(MongoExpressionNegator.TryNegate(input, out var once));
            Assert.True(MongoExpressionNegator.TryNegate(once, out var twice));
            Assert.Equal(RenderOf(input), RenderOf(twice));
        }
    }

    [Fact]
    public void Negator_declines_a_conditional_expression_rather_than_mis_negating_it()
    {
        var conditional = new MongoConditionalExpression(
            new MongoConstantExpression(true, forSerialization: null),
            new MongoConstantExpression(1, forSerialization: null),
            new MongoConstantExpression(2, forSerialization: null));

        Assert.False(MongoExpressionNegator.TryNegate(conditional, out _));
    }
}
