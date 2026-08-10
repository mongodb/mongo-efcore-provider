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
using System.Collections.Generic;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query.NativeTranslation;

/// <summary>
/// EF-362: the array-leaf admissibility rule after its root-declared conjunct was replaced by a ROOT-RELATIVE
/// DOCUMENT PATH. <see cref="NativeProjectionBinder.IsNativeArrayProjectionLeaf"/> is the ONE predicate the
/// emit side and the shaper side share, so the whole widening is decided here.
/// <para>
/// These cases exist at unit level rather than functionally because two of them are not reachable through
/// ordinary LINQ at all: a collection nested inside a collection cannot be written as a projection leaf
/// (<c>b.Posts.Comments</c> is not a member access — <c>Posts</c> is a sequence), and neither can a two-hop
/// <c>OwnsOne</c> chain be exercised for its path SHAPE independently of its values. The functional surface is
/// <c>Ef362OwnedHopArrayProjectionTests</c>.
/// </para>
/// </summary>
public class Ef362ArrayLeafPathTests
{
    private class Blog
    {
        public ObjectId Id { get; set; }
        public string Title { get; set; } = "";
        public List<Post> Posts { get; set; } = null!;
        public List<Note> RootNotes { get; set; } = null!;
        public Home Home { get; set; } = null!;
    }

    private class Post
    {
        public int PostId { get; set; }
        public string? Heading { get; set; }
        public List<Comment> Comments { get; set; } = null!;
    }

    private class Comment
    {
        public int CommentId { get; set; }
    }

    private class Home
    {
        public string? City { get; set; }
        public List<Note> Notes { get; set; } = null!;
        public Wing Wing { get; set; } = null!;
    }

    private class Wing
    {
        public List<Note> Notes { get; set; } = null!;
    }

    private class Note
    {
        public int NoteId { get; set; }
    }

    private static readonly Action<ModelBuilder> Model = mb =>
    {
        mb.Entity<Blog>().OwnsMany(b => b.Posts, p =>
        {
            p.HasKey(x => x.PostId);
            p.OwnsMany(x => x.Comments, c => c.HasKey(y => y.CommentId));
        });
        mb.Entity<Blog>().OwnsMany(b => b.RootNotes, n => n.HasKey(x => x.NoteId));
        mb.Entity<Blog>().OwnsOne(b => b.Home, h =>
        {
            h.OwnsMany(x => x.Notes, n => n.HasKey(y => y.NoteId));
            h.OwnsOne(x => x.Wing, w => w.OwnsMany(y => y.Notes, n => n.HasKey(z => z.NoteId)));
        });
    };

    private static (IEntityType Root, IModel FullModel) BuildModel()
    {
        using var db = SingleEntityDbContext.Create<Blog>(Model);
        return (db.Model.FindEntityType(typeof(Blog))!, db.Model);
    }

    private static INavigation Navigation(IEntityType declaring, string name)
        => declaring.FindNavigation(name)!;

    [Fact]
    public void A_root_declared_array_is_admitted_under_its_own_element_name_exactly_as_before()
    {
        // The pre-EF-362 case, unchanged: the derived path for a root-declared navigation IS the containing
        // element name, so nothing about the existing shapes moves.
        var (root, _) = BuildModel();
        var rootNotes = Navigation(root, nameof(Blog.RootNotes));

        Assert.True(NativeProjectionBinder.IsNativeArrayProjectionLeaf(rootNotes, root, "RootNotes"));
        Assert.False(NativeProjectionBinder.IsNativeArrayProjectionLeaf(rootNotes, root, "Home.RootNotes"));
        // The renamed-alias narrowing, which EF-362 does not touch.
        Assert.False(NativeProjectionBinder.IsNativeArrayProjectionLeaf(rootNotes, root, "P"));
    }

    [Fact]
    public void An_owned_reference_hop_is_admitted_only_under_its_full_dotted_path()
    {
        // THE widening. The alias must be the full path, not the last segment — the last segment is what the
        // anonymous type's member name would be, and reading by it against either a projected or an
        // un-projected document misses.
        var (root, _) = BuildModel();
        var home = root.FindNavigation(nameof(Blog.Home))!.TargetEntityType;
        var notes = Navigation(home, nameof(Home.Notes));

        Assert.True(NativeProjectionBinder.IsNativeArrayProjectionLeaf(notes, root, "Home.Notes"));
        Assert.False(NativeProjectionBinder.IsNativeArrayProjectionLeaf(notes, root, "Notes"));
    }

    [Fact]
    public void Two_owned_reference_hops_are_admitted_under_the_whole_chain()
    {
        // The walk is not special-cased to one hop; each additional single embedded reference adds a segment.
        var (root, _) = BuildModel();
        var home = root.FindNavigation(nameof(Blog.Home))!.TargetEntityType;
        var wing = home.FindNavigation(nameof(Home.Wing))!.TargetEntityType;
        var notes = Navigation(wing, nameof(Wing.Notes));

        Assert.True(NativeProjectionBinder.IsNativeArrayProjectionLeaf(notes, root, "Home.Wing.Notes"));
        Assert.False(NativeProjectionBinder.IsNativeArrayProjectionLeaf(notes, root, "Wing.Notes"));
        Assert.False(NativeProjectionBinder.IsNativeArrayProjectionLeaf(notes, root, "Notes"));
    }

    [Fact]
    public void An_array_under_a_COLLECTION_hop_is_declined_at_every_spelling()
    {
        // The intermediate-hop constraint, and the reason it is not merely tidiness: `Posts` is an ARRAY, so
        // "Posts.Comments" has no dotted read — a segment walk hits a BsonArray where it needs a BsonDocument.
        // Not reachable through ordinary LINQ (a collection is not a member access), which is exactly why it is
        // pinned here.
        //
        // MUTATION: drop TryGetRootRelativeArrayPath's `owner.IsCollection` check and the first assertion goes
        // green — i.e. the emit side would start aliasing an unreadable path.
        var (root, _) = BuildModel();
        var post = root.FindNavigation(nameof(Blog.Posts))!.TargetEntityType;
        var comments = Navigation(post, nameof(Post.Comments));

        Assert.False(NativeProjectionBinder.IsNativeArrayProjectionLeaf(comments, root, "Posts.Comments"));
        Assert.False(NativeProjectionBinder.IsNativeArrayProjectionLeaf(comments, root, "Comments"));
    }

    [Fact]
    public void An_element_with_its_own_eager_navigation_is_still_declined_under_a_hop_too()
    {
        // The EF-360 conjunct is orthogonal to the path and must keep applying after the widening: `Post` owns
        // `Comments`, so even at its own (admissible) root-declared path it is declined.
        var (root, _) = BuildModel();
        var posts = Navigation(root, nameof(Blog.Posts));

        Assert.Contains(posts.TargetEntityType.GetNavigations(), n => n.IsEagerLoaded);
        Assert.False(NativeProjectionBinder.IsNativeArrayProjectionLeaf(posts, root, "Posts"));
    }
}
