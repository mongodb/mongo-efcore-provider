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
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Query.Expressions;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query.NativeTranslation;

/// <summary>
/// Array-leaf admissibility by root-relative document path. <see cref="NativeProjectionBinder.IsNativeArrayProjectionLeaf"/>
/// is the one predicate the emit and shaper sides share. Unit-level because some shapes (a collection inside a
/// collection) aren't expressible in LINQ; the functional surface is <c>Ef362OwnedHopArrayProjectionTests</c>.
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
        // Root-declared navigation: the derived path is the containing element name.
        var (root, _) = BuildModel();
        var rootNotes = Navigation(root, nameof(Blog.RootNotes));

        Assert.True(NativeProjectionBinder.IsNativeArrayProjectionLeaf(rootNotes, root, "RootNotes"));
        Assert.False(NativeProjectionBinder.IsNativeArrayProjectionLeaf(rootNotes, root, "Home.RootNotes"));
        // Renamed-alias narrowing still applies.
        Assert.False(NativeProjectionBinder.IsNativeArrayProjectionLeaf(rootNotes, root, "P"));
    }

    [Fact]
    public void An_owned_reference_hop_is_admitted_only_under_its_full_dotted_path()
    {
        // Alias must be the full path, not the last segment (the anonymous member name), which would miss on both
        // projected and un-projected documents.
        var (root, _) = BuildModel();
        var home = root.FindNavigation(nameof(Blog.Home))!.TargetEntityType;
        var notes = Navigation(home, nameof(Home.Notes));

        Assert.True(NativeProjectionBinder.IsNativeArrayProjectionLeaf(notes, root, "Home.Notes"));
        Assert.False(NativeProjectionBinder.IsNativeArrayProjectionLeaf(notes, root, "Notes"));
    }

    [Fact]
    public void Two_owned_reference_hops_are_admitted_under_the_whole_chain()
    {
        // Each additional single embedded reference adds a segment.
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
        // `Posts` is an array, so "Posts.Comments" has no dotted read (the walk hits a BsonArray). Dropping
        // TryGetRootRelativeArrayPath's `owner.IsCollection` check makes this fail.
        var (root, _) = BuildModel();
        var post = root.FindNavigation(nameof(Blog.Posts))!.TargetEntityType;
        var comments = Navigation(post, nameof(Post.Comments));

        Assert.False(NativeProjectionBinder.IsNativeArrayProjectionLeaf(comments, root, "Posts.Comments"));
        Assert.False(NativeProjectionBinder.IsNativeArrayProjectionLeaf(comments, root, "Comments"));
    }

    [Fact]
    public void An_element_with_its_own_eager_navigation_is_still_declined_under_a_hop_too()
    {
        // The owns-a-collection conjunct still applies: `Post` owns `Comments`, so it is declined even at its
        // root-declared path.
        var (root, _) = BuildModel();
        var posts = Navigation(root, nameof(Blog.Posts));

        Assert.Contains(posts.TargetEntityType.GetNavigations(), n => n.IsEagerLoaded);
        Assert.False(NativeProjectionBinder.IsNativeArrayProjectionLeaf(posts, root, "Posts"));
    }

    // ── The array leaf's sibling sweep keeps a "$$ROOT" leaf out of the strip path ────────

    /// <summary>DTO for the whole-root-entity + owned-hop-array selector under test.</summary>
    private class RootAndNotes
    {
        public Blog Root { get; set; } = null!;
        public List<Note> Notes { get; set; } = null!;
    }

    /// <summary>Positive-control DTO: a whole-document-readable scalar sibling instead of the root leaf.</summary>
    private class TitleAndNotes
    {
        public string Title { get; set; } = "";
        public List<Note> Notes { get; set; } = null!;
    }

    /// <summary>
    /// Builds `b => new TDto { &lt;first leaf&gt;, Notes = b.Home.Notes }` in the nav-expanded shape
    /// (<see cref="MaterializeCollectionNavigationExpression"/> over `EF.Property(...).AsQueryable()`), the only form
    /// <see cref="NativeProjectionBinder.TryTranslateLeaf"/>'s array branch recognizes.
    /// </summary>
    private static (MongoQueryExpression Query, LambdaExpression Selector) OwnedHopArraySelector(bool wholeRootLeaf)
    {
        using var db = SingleEntityDbContext.Create<Blog>(Model);
        var root = db.Model.FindEntityType(typeof(Blog))!;
        var home = root.FindNavigation(nameof(Blog.Home))!.TargetEntityType;
        var notes = Navigation(home, nameof(Home.Notes));

        Expression<Func<Blog, IQueryable<Note>>> subquery =
            b => EF.Property<List<Note>>(b.Home, "Notes").AsQueryable();
        var parameter = subquery.Parameters[0];
        var arrayLeaf = new MaterializeCollectionNavigationExpression(subquery.Body, notes);

        var body = wholeRootLeaf
            ? Expression.MemberInit(
                Expression.New(typeof(RootAndNotes)),
                Expression.Bind(typeof(RootAndNotes).GetProperty(nameof(RootAndNotes.Root))!, parameter),
                Expression.Bind(typeof(RootAndNotes).GetProperty(nameof(RootAndNotes.Notes))!, arrayLeaf))
            : Expression.MemberInit(
                Expression.New(typeof(TitleAndNotes)),
                Expression.Bind(
                    typeof(TitleAndNotes).GetProperty(nameof(TitleAndNotes.Title))!,
                    Expression.Property(parameter, nameof(Blog.Title))),
                Expression.Bind(typeof(TitleAndNotes).GetProperty(nameof(TitleAndNotes.Notes))!, arrayLeaf));

        return (new MongoQueryExpression(root), Expression.Lambda(body, parameter));
    }

    [Fact]
    public void A_whole_root_entity_leaf_beside_an_owned_hop_array_leaf_declines_the_whole_projection()
    {
        // A whole-root leaf ("$ROOT" element ref) must never coexist with a document-path alias override: that
        // override triggers ShouldStripBareProjectionOnFallback, handing whole documents to a visitor that can't
        // read a "$$ROOT" alias. Admitting an array leaf requires every sibling to pass IsWholeDocumentReadableLeaf,
        // which rejects the element ref, so the whole projection must decline with nothing partially committed.
        // Widening IsWholeDocumentReadableLeaf to admit MongoElementRefExpression makes this fail.
        var (query, selector) = OwnedHopArraySelector(wholeRootLeaf: true);

        Assert.False(NativeProjectionBinder.TryPopulateNativeProjection(query, selector));

        // Nothing committed: no projections, no alias override (hence no strip), no array-leaf provenance.
        Assert.Empty(query.Select.Projection);
        Assert.False(query.Select.HasDocumentPathAliasOverride);
        Assert.False(query.Select.HasArrayProjectionLeaf);
    }

    [Fact]
    public void The_same_owned_hop_array_leaf_beside_a_whole_document_readable_scalar_is_admitted()
    {
        // Positive control: swapping only the sibling (to `b.Title`) admits the same array leaf at its full dotted
        // path, so the decline above is caused by the $$ROOT sibling and not a harness problem.
        var (query, selector) = OwnedHopArraySelector(wholeRootLeaf: false);

        Assert.True(NativeProjectionBinder.TryPopulateNativeProjection(query, selector));

        Assert.Equal(["Title", "Home.Notes", "_id"], query.Select.Projection.Select(p => p.Alias).ToArray());
        Assert.True(query.Select.HasDocumentPathAliasOverride);
        Assert.True(query.Select.HasArrayProjectionLeaf);
    }
}
