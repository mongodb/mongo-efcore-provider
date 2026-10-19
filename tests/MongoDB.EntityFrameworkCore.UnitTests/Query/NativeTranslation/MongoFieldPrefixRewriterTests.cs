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
using MongoDB.EntityFrameworkCore.Query.Expressions;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;
using MongoDB.EntityFrameworkCore.UnitTests.TestUtilities;
using Xunit;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query.NativeTranslation;

public class MongoFieldPrefixRewriterTests
{
    // --- Entity model used across tests ---

    private class Owner
    {
        public MongoDB.Bson.ObjectId Id { get; set; }
        public int Total { get; set; }
        public string Name { get; set; } = null!;
    }

    private static IProperty GetProperty(string propertyName)
    {
        using var db = SingleEntityDbContext.Create<Owner>();
        return db.Model.FindEntityType(typeof(Owner))!.FindProperty(propertyName)!;
    }

    private static MongoFieldExpression Field(string name)
        => new(GetProperty(name), name);

    // MongoFieldPrefixRewriter's public entry point is TryRewrite (it DECLINES rather than throwing for a node
    // kind with no prefixing rule). Every case below is a success case, so this asserts the success and unwraps.
    private static MongoExpression Rewrite(MongoExpression expr, string prefix)
    {
        Assert.True(MongoFieldPrefixRewriter.TryRewrite(expr, prefix, out var rewritten));
        return rewritten;
    }

    [Fact]
    public void Rewrites_a_bare_field_element_name_with_the_prefix()
    {
        var rewritten = (MongoFieldExpression)Rewrite(Field("Total"), "_lookup_Refs");
        Assert.Equal("_lookup_Refs.Total", rewritten.ElementName);
    }

    [Fact]
    public void Rewrites_fields_nested_in_binary_and_unary_nodes()
    {
        var expr = new MongoBinaryExpression(
            MongoBinaryOperator.AndAlso,
            new MongoBinaryExpression(MongoBinaryOperator.GreaterThan, Field("Total"),
                new MongoConstantExpression(100, forSerialization: null)),
            new MongoUnaryExpression(MongoUnaryOperator.Not,
                new MongoBinaryExpression(MongoBinaryOperator.Equal, Field("Name"),
                    new MongoConstantExpression("x", forSerialization: null))));

        var rewritten = (MongoBinaryExpression)Rewrite(expr, "_lookup_Refs");
        var left = (MongoBinaryExpression)rewritten.Left;
        var right = (MongoBinaryExpression)((MongoUnaryExpression)rewritten.Right).Operand;
        Assert.Equal("_lookup_Refs.Total", ((MongoFieldExpression)left.Left).ElementName);
        Assert.Equal("_lookup_Refs.Name", ((MongoFieldExpression)right.Left).ElementName);
    }

    [Fact]
    public void Prefixes_the_array_path_of_a_size_node()
    {
        var rewritten = Rewrite(
            new MongoSizeExpression("Comments", typeof(int), nullSafe: true), "Posts");

        var size = Assert.IsType<MongoSizeExpression>(rewritten);
        Assert.Equal("Posts.Comments", size.FieldName);
        Assert.True(size.NullSafe);
    }

    [Fact]
    public void Prefixes_a_size_node_inside_a_comparison()
    {
        var comparison = new MongoBinaryExpression(
            MongoBinaryOperator.GreaterThan,
            new MongoSizeExpression("Comments", typeof(int), nullSafe: true),
            new MongoConstantExpression(2, null));

        var rewritten = Assert.IsType<MongoBinaryExpression>(
            Rewrite(comparison, "Posts"));

        Assert.Equal("Posts.Comments", Assert.IsType<MongoSizeExpression>(rewritten.Left).FieldName);
    }

    [Fact]
    public void Prefixes_the_elem_match_array_path_and_leaves_the_element_predicate_alone()
    {
        var child = new MongoBinaryExpression(
            MongoBinaryOperator.Equal, Field("Name"),
            new MongoConstantExpression("x", forSerialization: null));
        var expr = new MongoElemMatchExpression("Posts", child, negated: false);

        var rewritten = (MongoElemMatchExpression)Rewrite(expr, "_lookup_Refs");

        Assert.Equal("_lookup_Refs.Posts", rewritten.ArrayPath);
        // The child is element-relative and must be untouched — NOT "_lookup_Refs.Name".
        var childField = (MongoFieldExpression)((MongoBinaryExpression)rewritten.ElementPredicate!).Left;
        Assert.Equal("Name", childField.ElementName);
        Assert.False(rewritten.Negated);
    }

    [Fact]
    public void Filtered_size_prefixes_the_array_path_only()
    {
        var node = new MongoFilteredSizeExpression(
            "Comments",
            new MongoBinaryExpression(
                MongoBinaryOperator.GreaterThan,
                new MongoElementRefExpression("Age", typeof(int)),
                new MongoConstantExpression(0, forSerialization: null)),
            typeof(int));

        var rewritten = Assert.IsType<MongoFilteredSizeExpression>(Rewrite(node, "Posts"));

        Assert.Equal("Posts.Comments", rewritten.ArrayPath);
        Assert.Same(node.ElementPredicate, rewritten.ElementPredicate);
    }

    // EF-382: MongoArrayContainsExpression must be rewritable — reachable when an owned/reference SelectMany's
    // inner filter contains an arrayField.Contains(constant) predicate; without this arm, Rewrite's exhaustive
    // switch would hit its catch-all throw for a shape that now translates successfully upstream (a
    // translate-time success turning into an unexpected later throw, rather than either working end-to-end or
    // declining up front).
    [Fact]
    public void Prefixes_the_array_contains_field_and_leaves_the_value_alone()
    {
        var expr = new MongoArrayContainsExpression(
            Field("Name"), new MongoConstantExpression("keep", forSerialization: null), negated: false);

        var rewritten = (MongoArrayContainsExpression)Rewrite(expr, "_lookup_Refs");

        Assert.Equal("_lookup_Refs.Name", rewritten.Field.ElementName);
        Assert.Equal("keep", Assert.IsType<MongoConstantExpression>(rewritten.Value).Value);
        Assert.False(rewritten.Negated);
    }

    // REGRESSION: this arm used to rebuild the node with the two-argument constructor, silently defaulting
    // NullSafe back to false. NullSafe is what makes the aggregation renderer wrap the reference in $ifNull so a
    // MISSING element compares equal to null the way $expr's own $eq does not — so dropping it turned an
    // owned-nav null check inside a prefixed scope (`SelectMany(o => o.Details.Where(d => d.Ship == null), …)`)
    // into one that matched only EXPLICIT nulls and quietly lost every row whose sub-document was absent.
    [Fact]
    public void Prefixes_an_element_ref_and_preserves_its_null_safety()
    {
        var rewritten = Assert.IsType<MongoElementRefExpression>(
            Rewrite(new MongoElementRefExpression("Ship", typeof(object), nullSafe: true), "_lookup_Refs"));

        Assert.Equal("_lookup_Refs.Ship", rewritten.Path);
        Assert.True(rewritten.NullSafe);
    }

    // An outer-scoped field is root-anchored by definition (it renders with elementVariable: null regardless of
    // any enclosing element scope), so it must pass through UNPREFIXED. Before this arm existed the node hit the
    // exhaustive switch's throwing default, converting a clean driver-LINQ fallback into a hard failure in every
    // MongoQueryMode.
    [Fact]
    public void Leaves_an_outer_scoped_field_unprefixed()
    {
        var outerField = new MongoOuterFieldExpression(Field("Name").Property, "Name");

        var rewritten = Assert.IsType<MongoOuterFieldExpression>(Rewrite(outerField, "_lookup_Refs"));

        Assert.Same(outerField, rewritten);
        Assert.Equal("Name", rewritten.ElementName);
    }

    // The disposition for a node kind with no prefixing rule is a DECLINE, not a throw — the caller falls back
    // to driver-LINQ. MongoExpressionNodeCoverageTests pins which kinds currently land here.
    [Fact]
    public void Declines_rather_than_throwing_for_a_node_kind_with_no_prefixing_rule()
    {
        var unprefixable = new MongoQuantifierExpression(
            new MongoElementRefExpression("Posts", typeof(object)),
            Field("Flag"),
            MongoExpressionTranslator.MongoQuantifierKind.Any);

        Assert.False(MongoFieldPrefixRewriter.TryRewrite(unprefixable, "_lookup_Refs", out var rewritten));
        Assert.Null(rewritten);
    }
}
