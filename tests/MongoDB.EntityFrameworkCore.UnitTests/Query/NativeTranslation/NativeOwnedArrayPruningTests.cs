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

using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;
using MongoDB.EntityFrameworkCore.UnitTests.TestUtilities;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query.NativeTranslation;

/// <summary>
/// What an element <c>Select</c> over an owned collection reads of each element (<c>NativeProjectionBinder.TryCollectAccessedNavigations</c>), and the
/// element names a pruned owned array keeps (<c>TryGetPrunedElementFields</c>).
/// </summary>
public class NativeOwnedArrayPruningTests
{
    private class Blog
    {
        public ObjectId Id { get; set; }
        public List<Post> Posts { get; set; } = [];
    }

    private class Post
    {
        public string Title { get; set; } = "";
        public int Rank { get; set; }
        [BsonElement("renamed")]
        public string Slug { get; set; } = "";
        public Meta? Meta { get; set; }
        public List<Comment> Comments { get; set; } = [];
        public string Summary => Title + Comments.Count;
    }

    private class Meta
    {
        public string Tag { get; set; } = "";
    }

    private class Comment
    {
        public string Text { get; set; } = "";
    }

    private static INavigation Posts(bool requiredMeta = false)
    {
        using var db = SingleEntityDbContext.Create<Blog>(mb => mb.Entity<Blog>().OwnsMany(
            b => b.Posts,
            p =>
            {
                p.Ignore(x => x.Summary);
                p.OwnsOne(x => x.Meta);
                p.OwnsMany(x => x.Comments);
                if (requiredMeta)
                {
                    p.Navigation(x => x.Meta).IsRequired();
                }
            }));
        return db.Model.FindEntityType(typeof(Blog))!.FindNavigation(nameof(Blog.Posts))!;
    }

    private static bool Collect(Expression<Func<Post, object?>> selector, out HashSet<INavigation> accessed)
    {
        accessed = [];
        return NativeProjectionBinder.TryCollectAccessedNavigations(selector, Posts().TargetEntityType, accessed);
    }

    private static string Helper(Post post) => post.Title;

    [Fact]
    public void A_scalar_member_touches_no_navigation()
    {
        Assert.True(Collect(p => p.Title, out var accessed));
        Assert.Empty(accessed);
    }

    [Fact]
    public void A_member_of_an_owned_reference_or_a_count_of_an_owned_collection_touches_that_navigation()
    {
        Assert.True(Collect(p => p.Meta!.Tag, out var viaReference));
        Assert.Equal(["Meta"], viaReference.Select(n => n.Name));

        Assert.True(Collect(p => p.Comments.Count, out var viaCollection));
        Assert.Equal(["Comments"], viaCollection.Select(n => n.Name));

        Assert.True(Collect(p => new { p.Title, p.Meta, p.Comments }, out var both));
        Assert.Equal(["Comments", "Meta"], both.Select(n => n.Name).Order());
    }

    [Fact]
    public void An_element_used_any_other_way_needs_the_whole_element()
    {
        Assert.False(Collect(p => p, out _));
        Assert.False(Collect(p => Helper(p), out _));
        Assert.False(Collect(p => new { p, p.Title }, out _));
    }

    [Fact]
    public void An_unmapped_member_may_read_anything_and_needs_the_whole_element()
        => Assert.False(Collect(p => p.Summary, out _));

    [Fact]
    public void A_pruned_array_keeps_every_stored_scalar_and_drops_untouched_owned_navigations()
    {
        var posts = Posts();

        Assert.True(NativeProjectionBinder.TryGetPrunedElementFields(posts, new HashSet<INavigation>(), out var fields));

        // The owner key an owned element is keyed by is not stored in it; the renamed property keeps its stored name.
        Assert.Equal(["Rank", "Title", "renamed"], fields.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_touched_owned_navigation_is_kept_under_its_stored_name()
    {
        var posts = Posts();
        var meta = posts.TargetEntityType.FindNavigation(nameof(Post.Meta))!;

        Assert.True(NativeProjectionBinder.TryGetPrunedElementFields(posts, new HashSet<INavigation> { meta }, out var fields));

        Assert.Equal(["Meta", "Rank", "Title", "renamed"], fields.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_required_owned_reference_is_never_dropped()
    {
        var posts = Posts(requiredMeta: true);

        Assert.True(NativeProjectionBinder.TryGetPrunedElementFields(posts, new HashSet<INavigation>(), out var fields));

        Assert.Equal(["Meta", "Rank", "Title", "renamed"], fields.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void An_array_with_nothing_to_leave_out_is_not_pruned()
    {
        var posts = Posts();
        var touched = posts.TargetEntityType.GetNavigations().Where(n => n.IsCollection || n.TargetEntityType.ClrType == typeof(Meta)).ToHashSet();

        Assert.False(NativeProjectionBinder.TryGetPrunedElementFields(posts, touched, out _));
    }
}
