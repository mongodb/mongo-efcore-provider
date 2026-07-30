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

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Query.Expressions;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query.Expressions;

public class ArrayAliasProjectionExpressionTests
{
    // A minimal model with one owned collection, built through a real ModelBuilder (via the established
    // SingleEntityDbContext unit-test helper — see NativeSelectManyBinderTests for the same pattern) so the
    // INavigation and IEntityType are genuine rather than mocked.
    private static INavigation GetPostsNavigation()
    {
        using var db = SingleEntityDbContext.Create<Blog>(mb => mb.Entity<Blog>().OwnsMany(b => b.Posts));
        return db.Model.FindEntityType(typeof(Blog))!.FindNavigation(nameof(Blog.Posts))!;
    }

    [Fact]
    public void Type_is_the_enumerable_of_the_element_clr_type()
    {
        var navigation = GetPostsNavigation();
        var access = new RootReferenceExpression(navigation.DeclaringEntityType);

        var sut = new ArrayAliasProjectionExpression(navigation, access);

        Assert.Equal(typeof(IEnumerable<Post>), sut.Type);
    }

    // The node is alias-addressed, so it deliberately has NO document-path field name. This is what
    // tells MongoProjectionBindingRemovingExpressionVisitor.VisitBinary to keep the alias it already
    // resolved from the ProjectionExpression rather than substituting a navigation element name.
    [Fact]
    public void ArrayFieldName_is_null_because_the_array_is_addressed_by_projection_alias()
    {
        var navigation = GetPostsNavigation();
        var sut = new ArrayAliasProjectionExpression(navigation, new RootReferenceExpression(navigation.DeclaringEntityType));

        Assert.Null(((IArrayProjectionExpression)sut).ArrayFieldName);
    }

    [Fact]
    public void Inner_projection_defaults_to_an_entity_projection_over_the_element_type()
    {
        var navigation = GetPostsNavigation();
        var sut = new ArrayAliasProjectionExpression(navigation, new RootReferenceExpression(navigation.DeclaringEntityType));

        Assert.Same(navigation.TargetEntityType, sut.InnerProjection.EntityType);
    }

    [Fact]
    public void Equal_nodes_are_equal_and_hash_alike()
    {
        var navigation = GetPostsNavigation();
        var access = new RootReferenceExpression(navigation.DeclaringEntityType);

        var a = new ArrayAliasProjectionExpression(navigation, access);
        var b = new ArrayAliasProjectionExpression(navigation, access);

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    // Equality must NOT collapse with the navigation-driven sibling: the two nodes address the same
    // array by different mechanisms, and _projectionBindings is keyed on the node, so conflating them
    // would let a document-path read satisfy an alias lookup.
    [Fact]
    public void Is_not_equal_to_an_object_array_projection_for_the_same_navigation()
    {
        var navigation = GetPostsNavigation();
        var access = new RootReferenceExpression(navigation.DeclaringEntityType);

        var alias = new ArrayAliasProjectionExpression(navigation, access);
        var byPath = new ObjectArrayProjectionExpression(navigation, access);

        Assert.NotEqual<object>(alias, byPath);
    }

    [Fact]
    public void Update_returns_the_same_instance_when_nothing_changed()
    {
        var navigation = GetPostsNavigation();
        var access = new RootReferenceExpression(navigation.DeclaringEntityType);
        var sut = new ArrayAliasProjectionExpression(navigation, access);

        Assert.Same(sut, sut.Update(access, sut.InnerProjection));
    }

    private class Blog
    {
        public ObjectId Id { get; set; }
        public string Title { get; set; } = "";
        public List<Post> Posts { get; set; } = null!;
    }

    private class Post
    {
        public string? Heading { get; set; }
    }
}
