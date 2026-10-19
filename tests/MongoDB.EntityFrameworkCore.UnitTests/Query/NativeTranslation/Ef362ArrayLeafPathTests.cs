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

    // ── EF-412: the array leaf's sibling sweep is what keeps a "$$ROOT" leaf out of the strip path ────────

    /// <summary>DTO for the whole-root-entity + owned-hop-array selector under test.</summary>
    private class RootAndNotes
    {
        public Blog Root { get; set; } = null!;
        public List<Note> Notes { get; set; } = null!;
    }

    /// <summary>The positive CONTROL's DTO: a whole-document-readable scalar sibling instead of the root leaf.</summary>
    private class TitleAndNotes
    {
        public string Title { get; set; } = "";
        public List<Note> Notes { get; set; } = null!;
    }

    /// <summary>
    /// Builds `b => new TDto { &lt;first leaf&gt;, Notes = b.Home.Notes }` in the shape EF's own nav-expansion
    /// produces — the owned collection wrapped in a <see cref="MaterializeCollectionNavigationExpression"/> over
    /// `EF.Property(...).AsQueryable()`, which is the ONLY spelling
    /// <see cref="NativeProjectionBinder.TryTranslateLeaf"/>'s array branch recognizes. Built by hand because
    /// these tests call the binder directly, without the preprocessing that would synthesize it; a source-spelled
    /// `b.Home.Notes` member access would decline for the unrelated reason that it is not that node kind, and the
    /// test would then prove nothing about the sibling sweep.
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
        // WHY THIS EXISTS (final-review finding F3): EF-412 makes a whole-ROOT-entity leaf translate to a
        // MongoElementRefExpression("$ROOT"), and the safety property nobody had tested is that such a leaf can
        // never coexist with a DOCUMENT-PATH alias override — the one thing that makes
        // MongoShapedQueryCompilingExpressionVisitor.ShouldStripBareProjectionOnFallback strip the $project and
        // hand WHOLE documents to a visitor whose ReadsUnprojectedDocuments is false, which cannot read a
        // "$$ROOT" alias. For a wrapped body that override can only come from an owned-ARRAY leaf, and admitting
        // an array leaf forces IsWholeDocumentReadableLeaf over every sibling — which requires a
        // MongoFieldExpression and therefore rejects the "$ROOT" element ref. So the WHOLE projection must
        // decline, and nothing may be committed to the select on the way out (a PARTIAL admission would be the
        // actual hazard: an array leaf registered with its "Home.Notes" override, and a $$ROOT leaf beside it).
        //
        // MUTATION, MEASURED: widening IsWholeDocumentReadableLeaf to also admit a MongoElementRefExpression
        // (`leaf is MongoElementRefExpression || leaf is MongoFieldExpression field`) flips the first assertion
        // below to True — the projection is then admitted with a "$$ROOT" leaf beside a "Home.Notes" override.
        // So this test discriminates the predicate, it does not merely observe a decline that has other causes.
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
        // THE POSITIVE CONTROL, and it is not decoration: without it the decline above could equally be caused
        // by this harness failing to build a recognizable array leaf at all, and the test would be vacuous.
        // Swapping ONLY the sibling leaf (root parameter → `b.Title`, a top-level field whose alias equals its
        // own element name) admits the identical array leaf, under its full dotted document path — so the
        // decline above is attributable to the $$ROOT sibling and nothing else.
        var (query, selector) = OwnedHopArraySelector(wholeRootLeaf: false);

        Assert.True(NativeProjectionBinder.TryPopulateNativeProjection(query, selector));

        Assert.Equal(["Title", "Home.Notes", "_id"], query.Select.Projection.Select(p => p.Alias).ToArray());
        Assert.True(query.Select.HasDocumentPathAliasOverride);
        Assert.True(query.Select.HasArrayProjectionLeaf);
    }
}
