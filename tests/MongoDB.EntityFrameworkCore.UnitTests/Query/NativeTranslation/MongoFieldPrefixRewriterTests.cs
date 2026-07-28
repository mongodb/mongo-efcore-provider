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

    [Fact]
    public void Rewrites_a_bare_field_element_name_with_the_prefix()
    {
        var rewritten = (MongoFieldExpression)MongoFieldPrefixRewriter.Rewrite(Field("Total"), "_lookup_Refs");
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

        var rewritten = (MongoBinaryExpression)MongoFieldPrefixRewriter.Rewrite(expr, "_lookup_Refs");
        var left = (MongoBinaryExpression)rewritten.Left;
        var right = (MongoBinaryExpression)((MongoUnaryExpression)rewritten.Right).Operand;
        Assert.Equal("_lookup_Refs.Total", ((MongoFieldExpression)left.Left).ElementName);
        Assert.Equal("_lookup_Refs.Name", ((MongoFieldExpression)right.Left).ElementName);
    }

    [Fact]
    public void Prefixes_the_array_path_of_a_size_node()
    {
        var rewritten = MongoFieldPrefixRewriter.Rewrite(
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
            MongoFieldPrefixRewriter.Rewrite(comparison, "Posts"));

        Assert.Equal("Posts.Comments", Assert.IsType<MongoSizeExpression>(rewritten.Left).FieldName);
    }

    [Fact]
    public void Prefixes_the_elem_match_array_path_and_leaves_the_element_predicate_alone()
    {
        var child = new MongoBinaryExpression(
            MongoBinaryOperator.Equal, Field("Name"),
            new MongoConstantExpression("x", forSerialization: null));
        var expr = new MongoElemMatchExpression("Posts", child, negated: false);

        var rewritten = (MongoElemMatchExpression)MongoFieldPrefixRewriter.Rewrite(expr, "_lookup_Refs");

        Assert.Equal("_lookup_Refs.Posts", rewritten.ArrayPath);
        // The child is element-relative and must be untouched — NOT "_lookup_Refs.Name".
        var childField = (MongoFieldExpression)((MongoBinaryExpression)rewritten.ElementPredicate!).Left;
        Assert.Equal("Name", childField.ElementName);
        Assert.False(rewritten.Negated);
    }
}
