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
using Microsoft.EntityFrameworkCore.Diagnostics;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.Diagnostics;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Infrastructure;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// An owned entity-collection leaf inside a terminal anonymous-type projection —
/// <c>Select(b =&gt; new { b.Title, b.Posts })</c> — goes native as a server-side <c>$project</c> including the array.
/// Routing is proven by <see cref="MongoQueryMode.NativeOnly"/>, never by MQL shape.
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeArrayProjectionTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    // No `= []` on Posts: a field initializer would mask a null-vs-empty read-back bug.
    public class Blog
    {
        public ObjectId Id { get; set; }
        public string Title { get; set; } = "";
        public List<Post> Posts { get; set; } = null!;

        // Rank and Tags back the arithmetic-leaf and primitive-collection-leaf tests. No other test here selects a
        // whole Blog or projects them, so their absence from other seeds is never observed. No `= []` on Tags.
        public int Rank { get; set; }
        public List<string> Tags { get; set; } = null!;
    }

    // No navigations and an explicit key: the simplest admissible element shape.
    public class Post
    {
        public int PostId { get; set; }
        public string? Heading { get; set; }
    }

    private static readonly Action<ModelBuilder> KeyedModel = mb =>
        mb.Entity<Blog>().OwnsMany(b => b.Posts, p => p.HasKey(x => x.PostId));

    // Same shape as KeyedModel but with no explicit HasKey on Post — the common real-world model. EF gives an
    // owned-collection element a shadow composite key (owner FK + ordinal), so the element shaper reads the
    // owner's key out of the row; see NativeProjectionBinder.TryPopulateNativeProjection's owner-key emission.
    private static readonly Action<ModelBuilder> ShadowKeyModel = mb =>
        mb.Entity<Blog>().OwnsMany(b => b.Posts);

    // Same element shape as Post, but the collection is reached through an owned single reference (declared on
    // Home, not the query root).
    public class NestedOwnerBlog
    {
        public ObjectId Id { get; set; }
        public string Title { get; set; } = "";
        public Home Home { get; set; } = null!;
    }

    public class Home
    {
        public List<Note> Notes { get; set; } = null!;
    }

    public class Note
    {
        public int NoteId { get; set; }
        public string? Text { get; set; }
    }

    private static readonly Action<ModelBuilder> NestedOwnerModel = mb =>
        mb.Entity<NestedOwnerBlog>().OwnsOne(b => b.Home, h => h.OwnsMany(x => x.Notes, n => n.HasKey(x => x.NoteId)));

    // The element (NestedPost) owns a further collection (Comments) — an eager-loaded element navigation, which
    // NativeProjectionBinder.IsNativeArrayProjectionLeaf declines. No `= []` on either collection.
    public class NestedBlog
    {
        public ObjectId Id { get; set; }
        public string Title { get; set; } = "";
        public List<NestedPost> Posts { get; set; } = null!;
    }

    public class NestedPost
    {
        public string? Heading { get; set; }
        public List<Comment> Comments { get; set; } = null!;
    }

    public class Comment
    {
        public string? Text { get; set; }
    }

    private static readonly Action<ModelBuilder> NestedModel = mb =>
        mb.Entity<NestedBlog>().OwnsMany(b => b.Posts, p => p.OwnsMany(x => x.Comments));

    // The element's only navigation is the lazy inverse back-reference to its owner
    // (OwnsMany(..., p => p.WithOwner(x => x.Owner))). EF never auto-includes it, so it is admissible; the
    // admissibility conjunct tests IsEagerLoaded rather than mere presence, mirroring
    // MongoQueryableMethodTranslatingExpressionVisitor.IsWholeElementRepresentable.
    public class OwnerRefBlog
    {
        public ObjectId Id { get; set; }
        public string Title { get; set; } = "";
        public List<OwnerRefPost> Posts { get; set; } = null!;
    }

    public class OwnerRefPost
    {
        public int PostId { get; set; }
        public string? Heading { get; set; }
        public OwnerRefBlog Owner { get; set; } = null!;
    }

    private static readonly Action<ModelBuilder> OwnerRefModel = mb =>
        mb.Entity<OwnerRefBlog>().OwnsMany(b => b.Posts, p =>
        {
            p.WithOwner(x => x.Owner);
            p.HasKey(x => x.PostId);
        });

    // Named DTO (MemberInit) spelling; exercises TryPopulateNativeProjection's MemberInitExpression branch. The
    // assigned member "Posts" matches the element name, so it is admissible.
    public class TitlePosts
    {
        public string Title { get; set; } = "";
        public List<Post> Posts { get; set; } = null!;
    }

    // Two owned-collection array leaves in one projection. Post is reused as the CLR element type for both
    // navigations, with distinct explicit keys so the two owned types don't collide.
    public class TwoArrayBlog
    {
        public ObjectId Id { get; set; }
        public string Title { get; set; } = "";
        public List<Post> Posts { get; set; } = null!;
        public List<Post> Drafts { get; set; } = null!;
    }

    private static readonly Action<ModelBuilder> TwoArrayModel = mb =>
        mb.Entity<TwoArrayBlog>(b =>
        {
            b.OwnsMany(x => x.Posts, p => p.HasKey(y => y.PostId));
            b.OwnsMany(x => x.Drafts, p => p.HasKey(y => y.PostId));
        });

    // HashSet-backed navigation: proves the array leaf is read back through the navigation's own
    // IClrCollectionAccessor rather than a hand-built List<T>.
    public class HashSetBlog
    {
        public ObjectId Id { get; set; }
        public string Title { get; set; } = "";
        public HashSet<Post> Posts { get; set; } = null!;
    }

    private static readonly Action<ModelBuilder> HashSetModel = mb =>
        mb.Entity<HashSetBlog>().OwnsMany(b => b.Posts, p => p.HasKey(x => x.PostId));

    // Element with a Guid stored with a non-default BsonRepresentation and a value-converted enum. The enum
    // converter is deliberately transforming ("g:" prefix) — see SeedConverters for why only the enum can
    // discriminate a converted read from a raw one. No `= []` on Posts.
    public enum Grade
    {
        Low,
        High
    }

    public class ConvBlog
    {
        public ObjectId Id { get; set; }
        public string Title { get; set; } = "";
        public List<ConvPost> Posts { get; set; } = null!;
    }

    // No explicit HasKey, so the element gets a shadow key (like ShadowKeyModel) and its shaper also reads the
    // owner key — exercising converters and owner-key emission together.
    public class ConvPost
    {
        public string? Heading { get; set; }
        public Guid Ref { get; set; }
        public Grade Code { get; set; }
    }

    private static readonly Action<ModelBuilder> ConverterModel = mb =>
        mb.Entity<ConvBlog>().OwnsMany(b => b.Posts, p =>
        {
            p.Property(x => x.Ref).HasBsonRepresentation(BsonType.String);
            p.Property(x => x.Code).HasConversion(v => "g:" + v.ToString(), v => Enum.Parse<Grade>(v.Substring(2)));
        });

    [Fact]
    public void Owned_array_leaf_in_an_anonymous_projection_goes_native()
    {
        var collection = SeedKeyed(nameof(Owned_array_leaf_in_an_anonymous_projection_goes_native));

        using var db = CreateContext(collection, KeyedModel, MongoQueryMode.NativeOnly);

        var results = db.Entities.AsNoTracking()
            .OrderBy(b => b.Title)
            .Select(b => new {b.Title, b.Posts})
            .ToList();

        Assert.Equal(new[] {"a_empty", "b_one", "c_two"}, results.Select(r => r.Title));
        Assert.Equal(new[] {0, 1, 2}, results.Select(r => r.Posts.Count));
        Assert.Equal(new[] {"h1"}, results[1].Posts.Select(p => p.Heading));
        Assert.Equal(new[] {"h2", "h3"}, results[2].Posts.Select(p => p.Heading));
    }

    [Fact]
    public void Owned_array_leaf_projection_emits_a_project_stage_and_no_longer_fetches_whole_documents()
    {
        var collection = SeedKeyed(nameof(Owned_array_leaf_projection_emits_a_project_stage_and_no_longer_fetches_whole_documents));

        using var db = CreateContextWithLogging(collection, KeyedModel, MongoQueryMode.Native, out var spy);
        _ = db.Entities.AsNoTracking().Select(b => new {b.Title, b.Posts}).ToList();

        var mql = spy.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery)!;
        // Expected: aggregate([{ "$project" : { "Title" : "$Title", "Posts" : "$Posts", "_id" : "$_id" } }])
        // `"Posts" : "$Posts"` is the load-bearing fragment; a bare Contains("Posts") would also match a $match,
        // a $lookup, or a whole-document fetch.
        Assert.Contains("$project", mql);
        Assert.Contains("\"Posts\" : \"$Posts\"", mql);
        Assert.DoesNotContain("aggregate([])", mql);
    }

    [Fact]
    public void Owned_array_leaf_projection_matches_driver_linq()
    {
        var collection = SeedKeyed(nameof(Owned_array_leaf_projection_matches_driver_linq));

        // Headings are compared as a joined string: a ValueTuple compares a string?[] member by reference, so
        // arrays never compare equal across two result sets. "<null>" keeps a null heading distinct from "".
        static List<(string Title, int Count, string Headings)> Run(SingleEntityDbContext<Blog> db)
            => db.Entities.AsNoTracking().OrderBy(b => b.Title)
                .Select(b => new {b.Title, b.Posts})
                .ToList()
                .Select(r => (r.Title, r.Posts.Count, string.Join("|", r.Posts.Select(p => p.Heading ?? "<null>"))))
                .ToList();

        using var native = CreateContext(collection, KeyedModel, MongoQueryMode.Native);
        using var driver = CreateContext(collection, KeyedModel, MongoQueryMode.DriverLinq);

        Assert.Equal(Run(driver), Run(native));
    }

    // A renamed alias (`P = b.Posts`, alias "P" vs. element "Posts") is declined — see
    // NativeProjectionBinder.IsNativeArrayProjectionLeaf. The shaper is alias-addressed, but the fallback hands it
    // an un-projected whole document, so admitting this shape made DriverLinq silently return zero elements.
    [Fact]
    public void Renamed_array_alias_is_declined_and_returns_correct_data_in_every_mode()
    {
        var collection = SeedKeyed(nameof(Renamed_array_alias_is_declined_and_returns_correct_data_in_every_mode));

        static List<(string Title, int Count)> Run(SingleEntityDbContext<Blog> db)
            => db.Entities.AsNoTracking().OrderBy(b => b.Title)
                .Select(b => new {b.Title, P = b.Posts})
                .ToList()
                .Select(r => (r.Title, r.P.Count))
                .ToList();

        // Both fallback-capable modes must agree with the seeded truth; DriverLinq is the leg that silently
        // returned zeros.
        foreach (var mode in new[] {MongoQueryMode.Native, MongoQueryMode.DriverLinq})
        {
            using var db = CreateContext(collection, KeyedModel, mode);
            Assert.Equal(new[] {0, 1, 2}, Run(db).Select(r => r.Count));
        }

        using var nativeOnly = CreateContext(collection, KeyedModel, MongoQueryMode.NativeOnly);
        Assert.Throws<NativeTranslationNotSupportedException>(() => Run(nativeOnly));
    }

    // An array leaf under a nested owner (reached through an owned reference) goes native. The full surface
    // (ragged arrays, parameterized Where, dotted alias, shadow-key element) is in
    // Ef362OwnedHopArrayProjectionTests; the expected values are the fallback's results.
    [Fact]
    public void Array_leaf_under_a_nested_owner_now_goes_native_and_still_returns_correct_data()
    {
        var collection = SeedNestedOwner(nameof(Array_leaf_under_a_nested_owner_now_goes_native_and_still_returns_correct_data));

        static List<(string Title, int Count, string Texts)> Run(SingleEntityDbContext<NestedOwnerBlog> db)
            => db.Entities.AsNoTracking().OrderBy(b => b.Title)
                .Select(b => new {b.Title, b.Home.Notes})
                .ToList()
                .Select(r => (r.Title, r.Notes.Count, string.Join("|", r.Notes.Select(n => n.Text ?? "<null>"))))
                .ToList();

        using (var native = CreateNestedOwnerContext(collection, MongoQueryMode.Native))
        using (var driver = CreateNestedOwnerContext(collection, MongoQueryMode.DriverLinq))
        {
            var actual = Run(native);

            Assert.Equal(new[] {"a_empty", "b_one", "c_two"}, actual.Select(r => r.Title));
            Assert.Equal(new[] {0, 1, 2}, actual.Select(r => r.Count));
            Assert.Equal(new[] {"", "n1", "n2|n3"}, actual.Select(r => r.Texts));
            Assert.Equal(Run(driver), actual);
        }

        // The routing proof, and the only reliable one: MQL shape cannot distinguish the two paths.
        using var nativeOnly = CreateNestedOwnerContext(collection, MongoQueryMode.NativeOnly);
        Assert.Equal(new[] {"", "n1", "n2|n3"}, Run(nativeOnly).Select(r => r.Texts));
    }

    // With a shadow-key owned collection the element shaper reads the owner's key out of the row. A $project
    // without _id fails per row ("Document element is missing for required non-nullable property 'Id'"), so the
    // array-leaf branch must emit the owner key. An explicit-HasKey element never reads it.
    [Fact]
    public void Owned_array_leaf_with_a_shadow_key_element_goes_native()
    {
        var collection = SeedShadow(nameof(Owned_array_leaf_with_a_shadow_key_element_goes_native));

        using var db = CreateContext(collection, ShadowKeyModel, MongoQueryMode.NativeOnly);

        var results = db.Set<Blog>().AsNoTracking().OrderBy(b => b.Title)
            .Select(b => new {b.Title, b.Posts})
            .ToList();

        Assert.Equal(new[] {0, 1, 2}, results.Select(r => r.Posts.Count));
        Assert.Equal(new[] {"h2", "h3"}, results[2].Posts.Select(p => p.Heading));
    }

    [Fact]
    public void Owned_array_leaf_projection_emits_the_owner_key()
    {
        var collection = SeedShadow(nameof(Owned_array_leaf_projection_emits_the_owner_key));

        using var db = CreateContextWithLogging(collection, ShadowKeyModel, MongoQueryMode.Native, out var spy);
        _ = db.Set<Blog>().AsNoTracking().Select(b => new {b.Title, b.Posts}).ToList();

        var mql = spy.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery)!;
        Assert.Contains("_id", mql);
        // RenderProject emits an explicit `_id : 0` suppression UNLESS the projection itself emits _id.
        Assert.DoesNotContain("\"_id\" : 0", mql);
    }

    // An element with an eager-loaded navigation of its own (nested owned collection or reference) is declined by
    // the native array-leaf branch; Native and DriverLinq return correct rows via the fallback, NativeOnly throws.
    // (The fallback used to throw ArgumentException in every mode — EF-360 — because the element's auto-include
    // subquery was rebuilt as IEnumerable<T> rather than the navigation's declared List<T>.)
    // Only eager-loaded navigations decline; a lazy inverse owner navigation is admitted — see
    // Array_leaf_whose_element_has_only_a_lazy_inverse_owner_navigation_goes_native.
    [Fact]
    public void Element_with_its_own_navigation_is_declined_but_the_fallback_returns_correct_rows()
    {
        var collection = SeedNested(nameof(Element_with_its_own_navigation_is_declined_but_the_fallback_returns_correct_rows));

        foreach (var mode in new[] {MongoQueryMode.Native, MongoQueryMode.DriverLinq, MongoQueryMode.NativeOnly})
        {
            using var db = CreateContext(collection, NestedModel, mode);

            // Premise: a nested owned navigation is eager-loaded, which is what keeps this shape declined.
            var elementNavigations = db.Model.FindEntityType(typeof(NestedBlog))!
                .FindNavigation(nameof(NestedBlog.Posts))!.TargetEntityType.GetNavigations().ToList();
            Assert.Equal([nameof(NestedPost.Comments)], elementNavigations.Select(n => n.Name));
            Assert.All(elementNavigations, n => Assert.True(n.IsEagerLoaded));

            var query = () => db.Set<NestedBlog>().AsNoTracking()
                .OrderBy(b => b.Title)
                .Select(b => new {b.Title, b.Posts})
                .ToList();

            if (mode == MongoQueryMode.NativeOnly)
            {
                // The array leaf is not admitted, so the whole projection declines; NativeOnly has no fallback.
                Assert.Throws<NativeTranslationNotSupportedException>(() => query());
                continue;
            }

            var rows = query();

            Assert.Equal(["a_empty", "b_two"], rows.Select(r => r.Title).ToArray());
            Assert.Empty(rows[0].Posts);
            Assert.Equal(new[] { "h1", "h2" }, rows[1].Posts.Select(p => p.Heading).ToArray());
        }
    }

    // An element whose only navigation is the lazy inverse owner back-reference must go native. The
    // not-eager-loaded assertion is the premise: if EF ever auto-includes owner back-references, it fails first
    // and names the reason instead of regressing into EF-360's shaper-build ArgumentException.
    [Fact]
    public void Array_leaf_whose_element_has_only_a_lazy_inverse_owner_navigation_goes_native()
    {
        var collection = SeedOwnerRef(nameof(Array_leaf_whose_element_has_only_a_lazy_inverse_owner_navigation_goes_native));

        using var db = CreateOwnerRefContext(collection, MongoQueryMode.NativeOnly);

        // Premise: exactly one navigation, not eager-loaded, so EF emits no auto-include subquery.
        var elementNavigations = db.Model.FindEntityType(typeof(OwnerRefBlog))!
            .FindNavigation(nameof(OwnerRefBlog.Posts))!.TargetEntityType.GetNavigations().ToList();
        Assert.Equal([nameof(OwnerRefPost.Owner)], elementNavigations.Select(n => n.Name));
        Assert.All(elementNavigations, n => Assert.False(n.IsEagerLoaded));

        // NativeOnly succeeding is the routing proof; MQL cannot distinguish the two paths.
        var results = db.Entities.AsNoTracking()
            .OrderBy(b => b.Title)
            .Select(b => new {b.Title, b.Posts})
            .ToList();

        Assert.Equal(new[] {"a_empty", "b_one", "c_two"}, results.Select(r => r.Title));
        Assert.Equal(new[] {0, 1, 2}, results.Select(r => r.Posts.Count));
        Assert.Equal(new[] {"h1"}, results[1].Posts.Select(p => p.Heading));
        Assert.Equal(new[] {"h2", "h3"}, results[2].Posts.Select(p => p.Heading));
    }

    // Values for the same shape: the native route must agree with driver-LINQ, not merely route differently.
    [Fact]
    public void Array_leaf_with_a_lazy_inverse_owner_navigation_matches_driver_linq()
    {
        var collection = SeedOwnerRef(nameof(Array_leaf_with_a_lazy_inverse_owner_navigation_matches_driver_linq));

        static List<(string Title, int Count, string Headings)> Run(SingleEntityDbContext<OwnerRefBlog> db)
            => db.Entities.AsNoTracking().OrderBy(b => b.Title)
                .Select(b => new {b.Title, b.Posts})
                .ToList()
                .Select(r => (r.Title, r.Posts.Count, string.Join("|", r.Posts.Select(p => p.Heading ?? "<null>"))))
                .ToList();

        using var native = CreateOwnerRefContext(collection, MongoQueryMode.Native);
        using var driver = CreateOwnerRefContext(collection, MongoQueryMode.DriverLinq);

        Assert.Equal(Run(driver), Run(native));
    }

    // The bare spelling on the same nested model takes the mixed path and never reaches the native projection
    // binder; it must keep working.
    [Fact]
    public void Bare_array_projection_of_an_element_with_its_own_navigation_still_works()
    {
        var collection = SeedNested(nameof(Bare_array_projection_of_an_element_with_its_own_navigation_still_works));

        using var db = CreateContext(collection, NestedModel, MongoQueryMode.Native);

        var results = db.Set<NestedBlog>().AsNoTracking().OrderBy(b => b.Title)
            .Select(b => b.Posts)
            .ToList();

        Assert.Equal(new[] {0, 2}, results.Select(r => r.Count));
    }

    // ---- Breadth: in-scope spellings and shapes ----

    // Named DTO / MemberInit spelling; exercises TryPopulateNativeProjection's MemberInitExpression branch.
    [Fact]
    public void Named_dto_projection_via_MemberInit_goes_native()
    {
        var collection = SeedKeyed(nameof(Named_dto_projection_via_MemberInit_goes_native));

        static List<(string Title, int Count, string Headings)> Run(IQueryable<Blog> query)
            => query.OrderBy(b => b.Title)
                .Select(b => new TitlePosts {Title = b.Title, Posts = b.Posts})
                .ToList()
                .Select(r => (r.Title, r.Posts.Count, string.Join("|", r.Posts.Select(p => p.Heading ?? "<null>"))))
                .ToList();

        using var nativeOnly = CreateContext(collection, KeyedModel, MongoQueryMode.NativeOnly);
        var actual = Run(nativeOnly.Entities.AsNoTracking());

        Assert.Equal(new[] {"a_empty", "b_one", "c_two"}, actual.Select(r => r.Title));
        Assert.Equal(new[] {0, 1, 2}, actual.Select(r => r.Count));
        Assert.Equal(new[] {"", "h1", "h2|h3"}, actual.Select(r => r.Headings));

        using var driver = CreateContext(collection, KeyedModel, MongoQueryMode.DriverLinq);
        Assert.Equal(Run(driver.Entities.AsNoTracking()), actual);
    }

    // EF.Property with an alias equal to the element name: admissible, and normalizes to the same tree as the
    // plain member-access spelling.
    [Fact]
    public void EF_Property_spelling_with_a_matching_alias_goes_native()
    {
        var collection = SeedKeyed(nameof(EF_Property_spelling_with_a_matching_alias_goes_native));

        static List<(string Title, int Count, string Headings)> Run(IQueryable<Blog> query)
            => query.OrderBy(b => b.Title)
                .Select(b => new {b.Title, Posts = EF.Property<List<Post>>(b, "Posts")})
                .ToList()
                .Select(r => (r.Title, r.Posts.Count, string.Join("|", r.Posts.Select(p => p.Heading ?? "<null>"))))
                .ToList();

        using var nativeOnly = CreateContext(collection, KeyedModel, MongoQueryMode.NativeOnly);
        var actual = Run(nativeOnly.Entities.AsNoTracking());

        Assert.Equal(new[] {0, 1, 2}, actual.Select(r => r.Count));
        Assert.Equal(new[] {"", "h1", "h2|h3"}, actual.Select(r => r.Headings));

        using var driver = CreateContext(collection, KeyedModel, MongoQueryMode.DriverLinq);
        Assert.Equal(Run(driver.Entities.AsNoTracking()), actual);
    }

    // EF.Property with a renamed alias is declined like `Mine = b.Posts`: the admissibility rule checks the alias
    // regardless of how the member access is spelled.
    [Fact]
    public void EF_Property_spelling_with_a_renamed_alias_declines_and_returns_correct_data_in_every_mode()
    {
        var collection = SeedKeyed(nameof(EF_Property_spelling_with_a_renamed_alias_declines_and_returns_correct_data_in_every_mode));

        // Contents, not just counts, on both legs: a decline could return the right count with the wrong elements.
        static List<(string Title, int Count, string Headings)> Run(SingleEntityDbContext<Blog> db)
            => db.Entities.AsNoTracking().OrderBy(b => b.Title)
                .Select(b => new {b.Title, P = EF.Property<List<Post>>(b, "Posts")})
                .ToList()
                .Select(r => (r.Title, r.P.Count, string.Join("|", r.P.Select(p => p.Heading ?? "<null>"))))
                .ToList();

        foreach (var mode in new[] {MongoQueryMode.Native, MongoQueryMode.DriverLinq})
        {
            using var db = CreateContext(collection, KeyedModel, mode);
            var actual = Run(db);
            Assert.Equal(new[] {0, 1, 2}, actual.Select(r => r.Count));
            Assert.Equal(new[] {"", "h1", "h2|h3"}, actual.Select(r => r.Headings));
        }

        using var nativeOnly = CreateContext(collection, KeyedModel, MongoQueryMode.NativeOnly);
        Assert.Throws<NativeTranslationNotSupportedException>(() => Run(nativeOnly));
    }

    // Two array leaves in one projection, with distinct values per array so a swapped-alias bug is caught.
    [Fact]
    public void Two_array_leaves_in_one_projection_go_native()
    {
        var collection = SeedTwoArrays(nameof(Two_array_leaves_in_one_projection_go_native));

        static List<(string Title, string PostHeadings, string DraftHeadings)> Run(IQueryable<TwoArrayBlog> query)
            => query.OrderBy(b => b.Title)
                .Select(b => new {b.Posts, b.Drafts, b.Title})
                .ToList()
                .Select(r => (r.Title,
                    string.Join("|", r.Posts.Select(p => p.Heading ?? "<null>")),
                    string.Join("|", r.Drafts.Select(p => p.Heading ?? "<null>"))))
                .ToList();

        using var nativeOnly = CreateTwoArrayContext(collection, MongoQueryMode.NativeOnly);
        var actual = Run(nativeOnly.Entities.AsNoTracking());

        Assert.Equal(new[] {"a_empty", "b_mixed"}, actual.Select(r => r.Title));
        Assert.Equal(new[] {"", "p1"}, actual.Select(r => r.PostHeadings));
        Assert.Equal(new[] {"", "d1|d2"}, actual.Select(r => r.DraftHeadings));

        using var driver = CreateTwoArrayContext(collection, MongoQueryMode.DriverLinq);
        Assert.Equal(Run(driver.Entities.AsNoTracking()), actual);
    }

    // An array leaf alongside a count leaf over the same collection declines. Any entity/collection-typed leaf
    // forces EF's client-side mixed shaper on fallback, which reads every alias against a whole un-projected
    // document; the count alias "N" names no element there, so Native would crash like DriverLinq if admitted
    // (see NativeProjectionBinder.IsWholeDocumentReadableLeaf). Declining makes all modes agree: NativeOnly
    // throws at translation, Native and DriverLinq fall back and return correct values.
    //
    // Known coverage gap: this test is the only defender of the shaper-side `Route == Projection` guard on the
    // array-leaf registration. `new { b, b.Posts }` does not exercise it — the whole-entity leaf fails
    // TryTranslateLeaf, so Route is already Fallback — and instead hits a pre-existing NullReferenceException
    // under Native and DriverLinq (see Query/AGENTS.md's array-valued-projections note).
    [Fact]
    public void Array_leaf_alongside_a_count_leaf_declines_and_returns_correct_data_in_every_mode()
    {
        var collection = SeedKeyed(nameof(Array_leaf_alongside_a_count_leaf_declines_and_returns_correct_data_in_every_mode));

        static List<(string Title, int N, int PostsCount)> Run(IQueryable<Blog> query)
            => query.OrderBy(b => b.Title)
                .Select(b => new {b.Title, N = b.Posts.Count, b.Posts})
                .ToList()
                .Select(r => (r.Title, r.N, r.Posts.Count))
                .ToList();

        foreach (var mode in new[] {MongoQueryMode.Native, MongoQueryMode.DriverLinq})
        {
            using var db = CreateContext(collection, KeyedModel, mode);
            var actual = Run(db.Entities.AsNoTracking());

            Assert.Equal(new[] {"a_empty", "b_one", "c_two"}, actual.Select(r => r.Title));
            Assert.All(actual, r => Assert.Equal(r.PostsCount, r.N));
            Assert.Equal(new[] {0, 1, 2}, actual.Select(r => r.N));
        }

        using var nativeOnly = CreateContext(collection, KeyedModel, MongoQueryMode.NativeOnly);
        Assert.Throws<NativeTranslationNotSupportedException>(() => Run(nativeOnly.Entities.AsNoTracking()));
    }

    // An array leaf alongside an arithmetic leaf (Rank * 2) declines for the same reason as the count leaf above:
    // the alias "X" names no document element.
    [Fact]
    public void Array_leaf_alongside_an_arithmetic_leaf_declines_and_returns_correct_data_in_every_mode()
    {
        var collection = SeedKeyedWithRank(nameof(Array_leaf_alongside_an_arithmetic_leaf_declines_and_returns_correct_data_in_every_mode));

        static List<(string Title, int X, int PostsCount)> Run(IQueryable<Blog> query)
            => query.OrderBy(b => b.Title)
                .Select(b => new {X = b.Rank * 2, b.Posts, b.Title})
                .ToList()
                .Select(r => (r.Title, r.X, r.Posts.Count))
                .ToList();

        foreach (var mode in new[] {MongoQueryMode.Native, MongoQueryMode.DriverLinq})
        {
            using var db = CreateContext(collection, KeyedModel, mode);
            var actual = Run(db.Entities.AsNoTracking());

            Assert.Equal(new[] {2, 4, 6}, actual.Select(r => r.X));
            Assert.Equal(new[] {0, 1, 2}, actual.Select(r => r.PostsCount));
        }

        using var nativeOnly = CreateContext(collection, KeyedModel, MongoQueryMode.NativeOnly);
        Assert.Throws<NativeTranslationNotSupportedException>(() => Run(nativeOnly.Entities.AsNoTracking()));
    }

    // Computed-leaf alias "Rank" collides with a real stored element. If the sibling-readability check were
    // skipped, the mixed shaper would read the raw stored Rank instead of Rank * 2 — silent wrong data. It
    // declines at translation instead, in every mode.
    [Fact]
    public void Array_leaf_alongside_an_arithmetic_leaf_whose_alias_collides_with_a_real_field_declines_and_returns_correct_data_in_every_mode()
    {
        var collection = SeedKeyedWithRank(nameof(Array_leaf_alongside_an_arithmetic_leaf_whose_alias_collides_with_a_real_field_declines_and_returns_correct_data_in_every_mode));

        static List<(string Title, int Rank, int PostsCount)> Run(IQueryable<Blog> query)
            => query.OrderBy(b => b.Title)
                .Select(b => new {Rank = b.Rank * 2, b.Posts, b.Title})
                .ToList()
                .Select(r => (r.Title, r.Rank, r.Posts.Count))
                .ToList();

        foreach (var mode in new[] {MongoQueryMode.Native, MongoQueryMode.DriverLinq})
        {
            using var db = CreateContext(collection, KeyedModel, mode);
            var actual = Run(db.Entities.AsNoTracking());

            // Seeded Rank is 1, 2, 3 (SeedKeyedWithRank); doubled here. A collision bug would read back 1, 2, 3.
            Assert.Equal(new[] {2, 4, 6}, actual.Select(r => r.Rank));
            Assert.Equal(new[] {0, 1, 2}, actual.Select(r => r.PostsCount));
        }

        using var nativeOnly = CreateContext(collection, KeyedModel, MongoQueryMode.NativeOnly);
        Assert.Throws<NativeTranslationNotSupportedException>(() => Run(nativeOnly.Entities.AsNoTracking()));
    }

    // HashSet<Post>-backed navigation: asserts the runtime type is HashSet<Post>, proving materialization goes
    // through the navigation's IClrCollectionAccessor rather than a List<T>.
    [Fact]
    public void HashSet_navigation_goes_native()
    {
        var collection = SeedHashSet(nameof(HashSet_navigation_goes_native));

        using var nativeOnly = CreateHashSetContext(collection, MongoQueryMode.NativeOnly);
        var results = nativeOnly.Entities.AsNoTracking().OrderBy(b => b.Title)
            .Select(b => new {b.Title, b.Posts})
            .ToList();

        Assert.Equal(new[] {"a_empty", "b_two"}, results.Select(r => r.Title));
        Assert.Equal(new[] {0, 2}, results.Select(r => r.Posts.Count));
        Assert.IsType<HashSet<Post>>(results[1].Posts);
        Assert.Equal(new[] {"h1", "h2"}, results[1].Posts.Select(p => p.Heading).OrderBy(h => h));

        // Contents and runtime type on the DriverLinq leg too, so a fallback that keeps counts but loses contents
        // or collection type is caught.
        using var driver = CreateHashSetContext(collection, MongoQueryMode.DriverLinq);
        var driverResults = driver.Entities.AsNoTracking().OrderBy(b => b.Title)
            .Select(b => new {b.Title, b.Posts})
            .ToList();
        Assert.Equal(new[] {"a_empty", "b_two"}, driverResults.Select(r => r.Title));
        Assert.Equal(new[] {0, 2}, driverResults.Select(r => r.Posts.Count));
        Assert.IsType<HashSet<Post>>(driverResults[1].Posts);
        Assert.Equal(new[] {"h1", "h2"}, driverResults[1].Posts.Select(p => p.Heading).OrderBy(h => h));
    }

    // A tracking query with an array leaf. The leaf is admissible, so translation succeeds in every mode;
    // EF Core's own owned-tracking guard (StructuralTypeMaterializerInjector.Inject, run from CompileShapedQuery
    // regardless of routing) throws the same InvalidOperationException in Native, DriverLinq and NativeOnly.
    [Fact]
    public void Tracking_query_with_an_array_leaf_throws_EF_Cores_owned_tracking_guard_in_every_mode()
    {
        var collection = SeedKeyed(nameof(Tracking_query_with_an_array_leaf_throws_EF_Cores_owned_tracking_guard_in_every_mode));

        foreach (var mode in new[] {MongoQueryMode.Native, MongoQueryMode.DriverLinq, MongoQueryMode.NativeOnly})
        {
            using var db = CreateContext(collection, KeyedModel, mode);
            var ex = Assert.Throws<InvalidOperationException>(
                () => db.Entities.Select(b => new {b.Title, b.Posts}).ToList());
            Assert.Contains("owned entities cannot be tracked without their owner", ex.Message);
        }
    }

    // A primitive-collection property leaf (Tags, already native via the plain-field branch) alongside an
    // owned-collection array leaf: the two leaf kinds compose.
    [Fact]
    public void Primitive_collection_leaf_alongside_an_owned_array_leaf_goes_native()
    {
        var collection = SeedKeyedWithTags(nameof(Primitive_collection_leaf_alongside_an_owned_array_leaf_goes_native));

        static List<(string Title, string Tags, int PostsCount)> Run(IQueryable<Blog> query)
            => query.OrderBy(b => b.Title)
                .Select(b => new {b.Tags, b.Posts, b.Title})
                .ToList()
                .Select(r => (r.Title, string.Join(",", r.Tags), r.Posts.Count))
                .ToList();

        using var nativeOnly = CreateContext(collection, KeyedModel, MongoQueryMode.NativeOnly);
        var actual = Run(nativeOnly.Entities.AsNoTracking());

        Assert.Equal(new[] {"x", "y,z", ""}, actual.Select(r => r.Tags));
        Assert.Equal(new[] {0, 1, 2}, actual.Select(r => r.PostsCount));

        using var driver = CreateContext(collection, KeyedModel, MongoQueryMode.DriverLinq);
        Assert.Equal(Run(driver.Entities.AsNoTracking()), actual);
    }

    // DriverLinq fallback leg for the shadow-key model (the other legs are covered by
    // Owned_array_leaf_projection_emits_the_owner_key and Owned_array_leaf_with_a_shadow_key_element_goes_native).
    [Fact]
    public void Owned_array_leaf_with_a_shadow_key_element_matches_driver_linq()
    {
        var collection = SeedShadow(nameof(Owned_array_leaf_with_a_shadow_key_element_matches_driver_linq));

        static List<(string Title, int Count, string Headings)> Run(SingleEntityDbContext<Blog> db)
            => db.Set<Blog>().AsNoTracking().OrderBy(b => b.Title)
                .Select(b => new {b.Title, b.Posts})
                .ToList()
                .Select(r => (r.Title, r.Posts.Count, string.Join("|", r.Posts.Select(p => p.Heading ?? "<null>"))))
                .ToList();

        using var native = CreateContext(collection, ShadowKeyModel, MongoQueryMode.Native);
        using var driver = CreateContext(collection, ShadowKeyModel, MongoQueryMode.DriverLinq);

        var nativeResult = Run(native);

        // Parity alone is vacuous — both legs run the same alias-addressed shaper — so anchor to seeded truth too.
        Assert.Equal(new[] {"a_empty", "b_one", "c_two"}, nativeResult.Select(r => r.Title));
        Assert.Equal(new[] {0, 1, 2}, nativeResult.Select(r => r.Count));
        Assert.Equal(new[] {"", "h1", "h2|h3"}, nativeResult.Select(r => r.Headings));

        Assert.Equal(Run(driver), nativeResult);
    }

    // ---- Correctness: ragged arrays, differential oracle, converters ----

    // A missing or explicit-BSON-null stored array materializes as an empty collection, never null (EF-358). The
    // alias read yields null for both, so the coalesce in MongoProjectionBindingRemovingExpressionVisitor's
    // CollectionShaperExpression case is load-bearing on this route. Blog.Posts has no initializer, and
    // SeedFiveStates self-checks the raw seed, because "missing" and "empty" are indistinguishable in results.
    [Fact]
    public void Native_array_projection_normalizes_missing_and_null_arrays_to_empty()
    {
        var collection = SeedFiveStates(nameof(Native_array_projection_normalizes_missing_and_null_arrays_to_empty));

        using var db = CreateContext(collection, ShadowKeyModel, MongoQueryMode.NativeOnly);

        var results = db.Set<Blog>().AsNoTracking().OrderBy(b => b.Title)
            .Select(b => new {b.Title, b.Posts})
            .ToList();

        Assert.Equal(new[] {"a_missing", "b_null", "c_empty", "d_single", "e_multi"}, results.Select(r => r.Title));
        Assert.All(results, r => Assert.NotNull(r.Posts));
        Assert.Equal(new[] {0, 0, 0, 1, 2}, results.Select(r => r.Posts.Count));

        // Contents too: a normalization producing empty for every state would pass the count checks.
        Assert.Equal(new[] {"h1"}, results[3].Posts.Select(p => p.Heading));
        Assert.Equal(new[] {"h2", "h3"}, results[4].Posts.Select(p => p.Heading));
    }

    // Differential oracle: the expected leg materializes whole Blog entities (reading Posts by document path);
    // the leg under test projects `new { Title, Posts }` and reads by $project alias. The expected leg must not be
    // another projection query, or a bug in the shared alias read would appear on both sides. Ground truth is
    // asserted too, so two legs wrong the same way can't pass.
    [Fact]
    public void Native_array_projection_equals_the_whole_entity_oracle_for_every_array_state()
    {
        var collection = SeedFiveStates(nameof(Native_array_projection_equals_the_whole_entity_oracle_for_every_array_state));

        // One summariser for both legs, so the only difference is where the collection came from. The "<null>"
        // sentinel is defensive only: no seed here writes a null Heading.
        static (string Title, int Count, string Headings) Summarize(string title, IEnumerable<Post> posts)
            => (title, posts.Count(), string.Join("|", posts.Select(p => p.Heading ?? "<null>")));

        using var oracleDb = CreateContext(collection, ShadowKeyModel, MongoQueryMode.Native);
        var expected = oracleDb.Set<Blog>().AsNoTracking().OrderBy(b => b.Title)
            .ToList()
            .Select(b => Summarize(b.Title, b.Posts))
            .ToList();

        using var db = CreateContext(collection, ShadowKeyModel, MongoQueryMode.NativeOnly);
        var actual = db.Set<Blog>().AsNoTracking().OrderBy(b => b.Title)
            .Select(b => new {b.Title, b.Posts})
            .ToList()
            .Select(r => Summarize(r.Title, r.Posts))
            .ToList();

        Assert.Equal(
            new[]
            {
                ("a_missing", 0, ""), ("b_null", 0, ""), ("c_empty", 0, ""), ("d_single", 1, "h1"),
                ("e_multi", 2, "h2|h3")
            },
            expected);
        Assert.Equal(expected, actual);
    }

    // Element converters must survive the native alias read (NativeOnly), not just the whole-entity/Include/mixed
    // paths. Code's converter is transforming ("g:High" isn't a Grade name), so only the configured converter can
    // produce Grade.High — that is the discriminating assertion. Ref is a round-trip pin only; see SeedConverters.
    // Both elements carry distinct non-empty Guids, so each assertion requires a real read of its own element.
    [Fact]
    public void Element_converters_round_trip_through_the_alias_read()
    {
        var (collection, expectedRefA, expectedRefB) =
            SeedConverters(nameof(Element_converters_round_trip_through_the_alias_read));

        using var db = CreateConverterContext(collection, MongoQueryMode.NativeOnly);

        // ToList() before Single(): a server-side Single() would add a reducer Cardinality, an unrelated axis.
        var posts = db.Entities.AsNoTracking()
            .Select(b => new {b.Title, b.Posts})
            .ToList()
            .Single().Posts.OrderBy(p => p.Heading).ToList();

        Assert.Equal(2, posts.Count);
        Assert.Equal(expectedRefA, posts[0].Ref);
        Assert.Equal(expectedRefB, posts[1].Ref);
        Assert.Equal(Grade.High, posts[0].Code);
        Assert.Equal(Grade.Low, posts[1].Code);
    }

    // ---- Set-op positions (TryTranslateLeaf is shared by every site that populates Select.Projection) ----
    //
    // Operand position (A.Select(p).Union(B.Select(p))) declines — see IsPlainProjectedSelect's
    // HasArrayProjectionLeaf conjunct. Each operand's $project runs before the combine, so dedup/source-tagging
    // compares the projected document; the owner _id an array leaf forces into it would turn dedup-by-value into
    // dedup-by-identity. Trailing position (Union(A,B).Select(p)) stays native: the combine runs over whole
    // documents and the $project is last.
    //
    // Coverage gap: the operand decline is tested only for the keyed and shadow-key element models, not the
    // DTO/MemberInit spelling or the HashSet/converter models. The gate reads one flag, so the risk is low.

    // Operand-position decline: Native/DriverLinq return correct rows via fallback; NativeOnly proves the decline.
    [Fact]
    public void Array_leaf_in_a_projected_union_operand_is_declined_and_falls_back_with_correct_data()
    {
        var collection = SeedKeyed(nameof(Array_leaf_in_a_projected_union_operand_is_declined_and_falls_back_with_correct_data));

        static List<(string Title, string Headings)> Run(SingleEntityDbContext<Blog> db)
            => db.Set<Blog>().AsNoTracking().Where(b => b.Title == "b_one")
                .Select(b => new {b.Title, b.Posts})
                .Union(db.Set<Blog>().AsNoTracking().Where(b => b.Title == "c_two")
                    .Select(b => new {b.Title, b.Posts}))
                .ToList()
                .Select(r => (r.Title, string.Join("|", r.Posts.Select(p => p.Heading ?? "<null>"))))
                .OrderBy(r => r.Title)
                .ToList();

        foreach (var mode in new[] {MongoQueryMode.Native, MongoQueryMode.DriverLinq})
        {
            using var db = CreateContext(collection, KeyedModel, mode);
            Assert.Equal([("b_one", "h1"), ("c_two", "h2|h3")], Run(db));
        }

        using var nativeOnly = CreateContext(collection, KeyedModel, MongoQueryMode.NativeOnly);
        Assert.Throws<NativeTranslationNotSupportedException>(() => Run(nativeOnly));
    }

    // Two distinct documents with identical projected values (differing only in unprojected Rank), so the row
    // count shows what the set op compares. With the array leaf admitted in operand position, the emitted
    // { Title, Posts, _id: "$_id" } changed the answers:
    //
    //     operation   control (no array leaf)   WITH array leaf admitted
    //     Union       1 row                     2 rows
    //     Intersect   1 row                     0 rows      <-- answer flipped
    //     Except      0 rows                    1 row       <-- answer flipped
    //
    // Intersect/Except have no driver-LINQ oracle, so the flipped answer would have been the only one available.
    // Declining restores the projected-value dedup contract (NativeSetOpsTests.
    // Projected_operand_union_dedups_over_projected_values_not_whole_entities). BCL LINQ is not the oracle here:
    // it compares the List<Post> member by reference and never dedups.
    [Fact]
    public void Array_leaf_in_a_projected_union_operand_declines_so_dedup_stays_over_the_projected_values()
    {
        var collection = SeedProjectionValueTwins(
            nameof(Array_leaf_in_a_projected_union_operand_declines_so_dedup_stays_over_the_projected_values));

        static List<(string Title, string Headings)> Run(SingleEntityDbContext<Blog> db)
            => db.Set<Blog>().AsNoTracking().Where(b => b.Rank == 1)
                .Select(b => new {b.Title, b.Posts})
                .Union(db.Set<Blog>().AsNoTracking().Where(b => b.Rank == 2)
                    .Select(b => new {b.Title, b.Posts}))
                .ToList()
                .Select(r => (r.Title, string.Join("|", r.Posts.Select(p => p.Heading ?? "<null>"))))
                .ToList();

        foreach (var mode in new[] {MongoQueryMode.Native, MongoQueryMode.DriverLinq})
        {
            using var db = CreateContext(collection, KeyedModel, mode);
            // One row: the projected values are equal, so projected-value dedup collapses them.
            Assert.Equal([("same", "h1")], Run(db));
        }

        using var nativeOnly = CreateContext(collection, KeyedModel, MongoQueryMode.NativeOnly);
        Assert.Throws<NativeTranslationNotSupportedException>(() => Run(nativeOnly));
    }

    // Concat has no dedup stage, so this pins that the decline doesn't turn it into Union or drop a row. Concat
    // would be safe natively; it declines only because it shares the IsPlainProjectedSelect predicate with Union
    // (one predicate, one rule).
    [Fact]
    public void Array_leaf_in_a_projected_concat_operand_is_declined_and_falls_back_with_correct_data()
    {
        var collection = SeedProjectionValueTwins(
            nameof(Array_leaf_in_a_projected_concat_operand_is_declined_and_falls_back_with_correct_data));

        static List<(string Title, string Headings)> Run(SingleEntityDbContext<Blog> db)
            => db.Set<Blog>().AsNoTracking().Where(b => b.Rank == 1)
                .Select(b => new {b.Title, b.Posts})
                .Concat(db.Set<Blog>().AsNoTracking().Where(b => b.Rank == 2)
                    .Select(b => new {b.Title, b.Posts}))
                .ToList()
                .Select(r => (r.Title, string.Join("|", r.Posts.Select(p => p.Heading ?? "<null>"))))
                .ToList();

        foreach (var mode in new[] {MongoQueryMode.Native, MongoQueryMode.DriverLinq})
        {
            using var db = CreateContext(collection, KeyedModel, mode);
            Assert.Equal([("same", "h1"), ("same", "h1")], Run(db));
        }

        using var nativeOnly = CreateContext(collection, KeyedModel, MongoQueryMode.NativeOnly);
        Assert.Throws<NativeTranslationNotSupportedException>(() => Run(nativeOnly));
    }

    // Intersect/Except have no driver-LINQ baseline, so TryTranslateSetOperation returns null for an out-of-scope
    // shape rather than marking it non-native: EF Core's translation failure, the same InvalidOperationException
    // in every mode. The exception type of an unsupported shape isn't contract; re-measure if it changes.
    [Fact]
    public void Array_leaf_in_a_projected_intersect_or_except_operand_hard_fails_in_every_mode()
    {
        var collection = SeedProjectionValueTwins(
            nameof(Array_leaf_in_a_projected_intersect_or_except_operand_hard_fails_in_every_mode));

        static IQueryable<Blog> Left(SingleEntityDbContext<Blog> db)
            => db.Set<Blog>().AsNoTracking().Where(b => b.Rank == 1);

        static IQueryable<Blog> Right(SingleEntityDbContext<Blog> db)
            => db.Set<Blog>().AsNoTracking().Where(b => b.Rank == 2);

        foreach (var mode in new[] {MongoQueryMode.Native, MongoQueryMode.DriverLinq, MongoQueryMode.NativeOnly})
        {
            using var db = CreateContext(collection, KeyedModel, mode);

            var intersect = Assert.Throws<InvalidOperationException>(
                () => Left(db).Select(b => new {b.Title, b.Posts})
                    .Intersect(Right(db).Select(b => new {b.Title, b.Posts}))
                    .ToList());
            Assert.Contains("could not be translated", intersect.Message);

            var except = Assert.Throws<InvalidOperationException>(
                () => Left(db).Select(b => new {b.Title, b.Posts})
                    .Except(Right(db).Select(b => new {b.Title, b.Posts}))
                    .ToList());
            Assert.Contains("could not be translated", except.Message);
        }
    }

    // For a shadow-key element the operand-position fallback pushes the projected-operand set op server-side with
    // `_id: 0`, stripping the owner key the element shaper reads. Under NoTracking that key is unobservable, so the
    // mixed shaper reads it as a placeholder and the fallback returns the value-deduped rows. Natively the leaf is
    // still declined: admitting it would leak the owner _id into the dedup key and change set semantics.
    [Fact]
    public void Array_leaf_in_a_projected_union_operand_with_a_shadow_key_element_falls_back_correctly()
    {
        var collection = SeedShadow(
            nameof(Array_leaf_in_a_projected_union_operand_with_a_shadow_key_element_falls_back_correctly));

        static List<(string Title, string Headings)> Run(SingleEntityDbContext<Blog> db)
            => db.Set<Blog>().AsNoTracking().Where(b => b.Title == "b_one")
                .Select(b => new {b.Title, b.Posts})
                .Union(db.Set<Blog>().AsNoTracking().Where(b => b.Title == "c_two")
                    .Select(b => new {b.Title, b.Posts}))
                .ToList()
                .Select(r => (r.Title, string.Join(",", r.Posts.Select(p => p.Heading))))
                .OrderBy(r => r.Title)
                .ToList();

        foreach (var mode in new[] {MongoQueryMode.Native, MongoQueryMode.DriverLinq})
        {
            using var db = CreateContext(collection, ShadowKeyModel, mode);
            Assert.Equal([("b_one", "h1"), ("c_two", "h2,h3")], Run(db));
        }

        using var nativeOnly = CreateContext(collection, ShadowKeyModel, MongoQueryMode.NativeOnly);
        Assert.Throws<NativeTranslationNotSupportedException>(() => Run(nativeOnly));
    }

    // Trailing position stays native (NativeOnly succeeds), for both element-key kinds; the shadow-key model
    // round-trips here, unlike in operand position.
    [Fact]
    public void Array_leaf_in_a_trailing_projection_after_a_union_goes_native()
    {
        var collection = SeedKeyed(nameof(Array_leaf_in_a_trailing_projection_after_a_union_goes_native));

        static List<(string Title, string Headings)> Run(SingleEntityDbContext<Blog> db)
            => db.Set<Blog>().AsNoTracking().Where(b => b.Title == "b_one")
                .Union(db.Set<Blog>().AsNoTracking().Where(b => b.Title == "c_two"))
                .Select(b => new {b.Title, b.Posts})
                .ToList()
                .Select(r => (r.Title, string.Join("|", r.Posts.Select(p => p.Heading ?? "<null>"))))
                .OrderBy(r => r.Title)
                .ToList();

        foreach (var model in new[] {KeyedModel, ShadowKeyModel})
        {
            // NativeOnly succeeding is the routing proof; MQL cannot distinguish the two paths.
            using var nativeOnly = CreateContext(collection, model, MongoQueryMode.NativeOnly);
            var actual = Run(nativeOnly);
            Assert.Equal([("b_one", "h1"), ("c_two", "h2|h3")], actual);

            using var driver = CreateContext(collection, model, MongoQueryMode.DriverLinq);
            Assert.Equal(Run(driver), actual);
        }
    }

    // A trailing-position Union dedups over the whole document ($$ROOT, with a distinct _id), so the array plays no
    // part in the comparison:
    //   * The same document matched by both operands collapses to one row.
    //   * Two documents differing only in stored array state (missing vs []) both survive even though both
    //     materialize empty (EF-358) — dedup sees the stored form. Inherent to whole-document dedup, not the leaf.
    [Fact]
    public void Trailing_projection_after_a_union_dedups_whole_documents_and_never_compares_the_array()
    {
        var overlapping = SeedKeyedWithRank(
            nameof(Trailing_projection_after_a_union_dedups_whole_documents_and_never_compares_the_array));

        using (var db = CreateContext(overlapping, KeyedModel, MongoQueryMode.NativeOnly))
        {
            // Rank <= 2 => {a_empty, b_one}; Rank == 2 => {b_one}. b_one is the same DOCUMENT on both sides.
            var rows = db.Set<Blog>().AsNoTracking().Where(b => b.Rank <= 2)
                .Union(db.Set<Blog>().AsNoTracking().Where(b => b.Rank == 2))
                .Select(b => new {b.Title, b.Posts})
                .ToList()
                .Select(r => (r.Title, r.Posts.Count))
                .OrderBy(r => r.Title)
                .ToList();

            Assert.Equal([("a_empty", 0), ("b_one", 1)], rows);
        }

        var twins = SeedArrayStateTwins(
            nameof(Trailing_projection_after_a_union_dedups_whole_documents_and_never_compares_the_array));

        using (var db = CreateContext(twins, KeyedModel, MongoQueryMode.NativeOnly))
        {
            // Each twin matches both operands and dedups against itself: two rows, not four (dedup happened) and not
            // one (missing-array and empty-array twins are distinct documents).
            var rows = db.Set<Blog>().AsNoTracking().Where(b => b.Rank == 1)
                .Union(db.Set<Blog>().AsNoTracking().Where(b => b.Rank == 1))
                .Select(b => new {b.Title, b.Posts})
                .ToList();

            Assert.Equal(2, rows.Count);
            Assert.All(rows, r => Assert.Equal("t", r.Title));
            Assert.All(rows, r => Assert.NotNull(r.Posts));
            Assert.All(rows, r => Assert.Empty(r.Posts));
        }
    }

    // Trailing position over Intersect (source-tagging pipeline, whose $group stages also run over whole
    // documents before the $project). No driver-LINQ oracle exists for this shape, so it asserts expected rows.
    [Fact]
    public void Array_leaf_in_a_trailing_projection_after_an_intersect_goes_native()
    {
        var collection = SeedKeyedWithRank(nameof(Array_leaf_in_a_trailing_projection_after_an_intersect_goes_native));

        using var db = CreateContext(collection, KeyedModel, MongoQueryMode.NativeOnly);

        // Rank <= 2 => {a_empty, b_one}; Rank == 2 => {b_one}. Intersection: b_one, whose array survives intact.
        var rows = db.Set<Blog>().AsNoTracking().Where(b => b.Rank <= 2)
            .Intersect(db.Set<Blog>().AsNoTracking().Where(b => b.Rank == 2))
            .Select(b => new {b.Title, b.Posts})
            .ToList()
            .Select(r => (r.Title, string.Join("|", r.Posts.Select(p => p.Heading ?? "<null>"))))
            .ToList();

        Assert.Equal([("b_one", "h1")], rows);
    }

    // ---- Shapes one keystroke from covered ones, pinned so a future admissibility narrowing can't drop them ----

    // Array-only projection: no sibling leaf, so only the alias-agreement conjunct gates it.
    [Fact]
    public void Array_only_anonymous_projection_goes_native()
    {
        var collection = SeedKeyed(nameof(Array_only_anonymous_projection_goes_native));

        static List<string> Run(SingleEntityDbContext<Blog> db)
            => db.Entities.AsNoTracking().OrderBy(b => b.Title)
                .Select(b => new {b.Posts})
                .ToList()
                .Select(r => string.Join("|", r.Posts.Select(p => p.Heading ?? "<null>")))
                .ToList();

        // NativeOnly succeeding is the routing proof; MQL cannot distinguish the two paths.
        using var nativeOnly = CreateContext(collection, KeyedModel, MongoQueryMode.NativeOnly);
        var actual = Run(nativeOnly);
        Assert.Equal(["", "h1", "h2|h3"], actual);

        using var driver = CreateContext(collection, KeyedModel, MongoQueryMode.DriverLinq);
        Assert.Equal(Run(driver), actual);
    }

    // A reducer after an array projection: the array $project and the reducer's $limit both survive.
    [Fact]
    public void First_after_an_array_projection_goes_native()
    {
        var collection = SeedKeyed(nameof(First_after_an_array_projection_goes_native));

        static (string Title, string Headings) Run(SingleEntityDbContext<Blog> db)
        {
            var r = db.Entities.AsNoTracking().OrderBy(b => b.Title)
                .Select(b => new {b.Title, b.Posts})
                .First();
            return (r.Title, string.Join("|", r.Posts.Select(p => p.Heading ?? "<null>")));
        }

        using var nativeOnly = CreateContext(collection, KeyedModel, MongoQueryMode.NativeOnly);
        var actual = Run(nativeOnly);
        Assert.Equal(("a_empty", ""), actual);

        using var driver = CreateContext(collection, KeyedModel, MongoQueryMode.DriverLinq);
        Assert.Equal(Run(driver), actual);
    }

    // Paging after an array projection: Skip/Take append to the op list and the array $project is emitted last.
    [Fact]
    public void Paging_after_an_array_projection_goes_native()
    {
        var collection = SeedKeyed(nameof(Paging_after_an_array_projection_goes_native));

        static List<(string Title, string Headings)> Run(SingleEntityDbContext<Blog> db)
            => db.Entities.AsNoTracking().OrderBy(b => b.Title)
                .Select(b => new {b.Title, b.Posts})
                .Skip(1).Take(1)
                .ToList()
                .Select(r => (r.Title, string.Join("|", r.Posts.Select(p => p.Heading ?? "<null>"))))
                .ToList();

        using var nativeOnly = CreateContext(collection, KeyedModel, MongoQueryMode.NativeOnly);
        var actual = Run(nativeOnly);
        Assert.Equal([("b_one", "h1")], actual);

        using var driver = CreateContext(collection, KeyedModel, MongoQueryMode.DriverLinq);
        Assert.Equal(Run(driver), actual);
    }

    // ---- A primary-key sibling goes native ----
    //
    // A root's key has element name "_id" but alias "Id". IsWholeDocumentReadableLeaf admits any property-backed
    // leaf regardless of alias, because the mixed/fallback path resolves it through its IProperty
    // (MongoMixedProjectionBindingRemovingExpressionVisitor), not by alias. Only a computed leaf (no backing
    // IProperty) still needs alias == element name — see Computed_sibling_still_declines_alongside_an_array_leaf.
    // Earlier widenings of this rule caused silent wrong data; keep it this narrow.
    [Fact]
    public void Primary_key_sibling_now_goes_native_and_returns_correct_data_in_every_mode()
    {
        var collection = SeedKeyed(nameof(Primary_key_sibling_now_goes_native_and_returns_correct_data_in_every_mode));

        static List<(string Title, int Count)> Run(SingleEntityDbContext<Blog> db)
            => db.Entities.AsNoTracking().OrderBy(b => b.Title)
                .Select(b => new {b.Id, b.Title, b.Posts})
                .ToList()
                .Select(r => (r.Title, r.Posts.Count))
                .ToList();

        // Premise: the root key's stored element name is "_id", not "Id".
        using (var probe = CreateContext(collection, KeyedModel, MongoQueryMode.Native))
        {
            var key = probe.Model.FindEntityType(typeof(Blog))!.FindPrimaryKey()!.Properties[0];
            Assert.Equal(nameof(Blog.Id), key.Name);
            Assert.Equal("_id", key.GetElementName());
        }

        var expected = new List<(string, int)> {("a_empty", 0), ("b_one", 1), ("c_two", 2)};

        using (var native = CreateContext(collection, KeyedModel, MongoQueryMode.Native))
        using (var driver = CreateContext(collection, KeyedModel, MongoQueryMode.DriverLinq))
        {
            Assert.Equal(expected, Run(native));
            Assert.Equal(expected, Run(driver));
        }

        // Succeeds under NativeOnly.
        using var nativeOnly = CreateContext(collection, KeyedModel, MongoQueryMode.NativeOnly);
        Assert.Equal(expected, Run(nativeOnly));
    }

    [Fact]
    public void Computed_sibling_still_declines_alongside_an_array_leaf()
    {
        var collection = SeedKeyed(nameof(Computed_sibling_still_declines_alongside_an_array_leaf));

        static List<(string Title, int PostCountPlusOne)> Run(SingleEntityDbContext<Blog> db)
            => db.Entities.AsNoTracking().OrderBy(b => b.Title)
                .Select(b => new {b.Title, b.Posts, PostCountPlusOne = b.Posts.Count + 1})
                .ToList()
                .Select(r => (r.Title, r.PostCountPlusOne))
                .ToList();

        var expected = new List<(string, int)> {("a_empty", 1), ("b_one", 2), ("c_two", 3)};

        using (var native = CreateContext(collection, KeyedModel, MongoQueryMode.Native))
        using (var driver = CreateContext(collection, KeyedModel, MongoQueryMode.DriverLinq))
        {
            Assert.Equal(expected, Run(native));
            Assert.Equal(expected, Run(driver));
        }

        // A computed sibling (no backing IProperty) must still decline; if this flips, the rule went too far.
        using var nativeOnly = CreateContext(collection, KeyedModel, MongoQueryMode.NativeOnly);
        Assert.Throws<NativeTranslationNotSupportedException>(() => Run(nativeOnly));
    }

    private static SingleEntityDbContext<Blog> CreateContext(
        IMongoCollection<Blog> collection, Action<ModelBuilder> modelBuilderAction, MongoQueryMode mode)
        => SingleEntityDbContext.Create(
            collection,
            modelBuilderAction: modelBuilderAction,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });

    // FunctionalTests has no AssertMql (that's in SpecificationTests), so MQL is captured via SpyLoggerProvider.
    private static SingleEntityDbContext<Blog> CreateContextWithLogging(
        IMongoCollection<Blog> collection, Action<ModelBuilder> modelBuilderAction, MongoQueryMode mode,
        out SpyLoggerProvider spyLogger)
    {
        var (loggerFactory, provider) = SpyLoggerProvider.Create();
        spyLogger = provider;

        return SingleEntityDbContext.Create(
            collection,
            loggerFactory,
            modelBuilderAction: modelBuilderAction,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                b.EnableSensitiveDataLogging();
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });
    }

    private string UniqueCollectionName(string name)
        => TemporaryDatabaseFixtureBase.CreateCollectionName(name) + Guid.NewGuid().ToString("N")[..8];

    // Seeded as raw BSON so the stored array state is exactly as intended: arrays of length 0, 1 and 2.
    private IMongoCollection<Blog> SeedKeyed(string name)
    {
        var coll = database.MongoDatabase.GetCollection<BsonDocument>(UniqueCollectionName(name));
        coll.InsertMany(
        [
            Row("a_empty"),
            Row("b_one", PostDoc(1, "h1")),
            Row("c_two", PostDoc(2, "h2"), PostDoc(3, "h3"))
        ]);
        return database.MongoDatabase.GetCollection<Blog>(coll.CollectionNamespace.CollectionName);
    }

    private static BsonDocument Row(string title, params BsonDocument[] posts)
        => new()
        {
            {"_id", ObjectId.GenerateNewId()},
            {"Title", title},
            {"Posts", new BsonArray(posts)}
        };

    // "PostId", not "_id": PrimaryKeyDiscoveryConvention doesn't rewrite the element name of an owned type's
    // explicit key.
    private static BsonDocument PostDoc(int postId, string heading)
        => new() {{"PostId", postId}, {"Heading", heading}};

    // Same documents as SeedKeyed; a shadow key only changes how the element's key is materialized (from the
    // owner's _id). The stored "PostId" is just an ordinary scalar here.
    private IMongoCollection<Blog> SeedShadow(string name)
        => SeedKeyed(name);

    private static SingleEntityDbContext<NestedOwnerBlog> CreateNestedOwnerContext(
        IMongoCollection<NestedOwnerBlog> collection, MongoQueryMode mode)
        => SingleEntityDbContext.Create(
            collection,
            modelBuilderAction: NestedOwnerModel,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });

    // Home is required, so it is seeded on every row to avoid unrelated materialization failures.
    private IMongoCollection<NestedOwnerBlog> SeedNestedOwner(string name)
    {
        var coll = database.MongoDatabase.GetCollection<BsonDocument>(UniqueCollectionName(name));
        coll.InsertMany(
        [
            NestedRow("a_empty"),
            NestedRow("b_one", NoteDoc(1, "n1")),
            NestedRow("c_two", NoteDoc(2, "n2"), NoteDoc(3, "n3"))
        ]);
        return database.MongoDatabase.GetCollection<NestedOwnerBlog>(coll.CollectionNamespace.CollectionName);
    }

    private static BsonDocument NestedRow(string title, params BsonDocument[] notes)
        => new()
        {
            {"_id", ObjectId.GenerateNewId()},
            {"Title", title},
            {"Home", new BsonDocument {{"Notes", new BsonArray(notes)}}}
        };

    private static BsonDocument NoteDoc(int noteId, string text)
        => new() {{"NoteId", noteId}, {"Text", text}};

    private static SingleEntityDbContext<NestedBlog> CreateContext(
        IMongoCollection<NestedBlog> collection, Action<ModelBuilder> modelBuilderAction, MongoQueryMode mode)
        => SingleEntityDbContext.Create(
            collection,
            modelBuilderAction: modelBuilderAction,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });

    // Two rows (empty and populated) suffice: the decline test never materializes, and the bare-spelling test
    // only checks ordering and counts. Comments are seeded present-but-empty.
    private IMongoCollection<NestedBlog> SeedNested(string name)
    {
        var coll = database.MongoDatabase.GetCollection<BsonDocument>(UniqueCollectionName(name));
        coll.InsertMany(
        [
            NestedBlogRow("a_empty"),
            NestedBlogRow("b_two", NestedPostDoc("h1"), NestedPostDoc("h2"))
        ]);
        return database.MongoDatabase.GetCollection<NestedBlog>(coll.CollectionNamespace.CollectionName);
    }

    private static BsonDocument NestedBlogRow(string title, params BsonDocument[] posts)
        => new()
        {
            {"_id", ObjectId.GenerateNewId()},
            {"Title", title},
            {"Posts", new BsonArray(posts)}
        };

    private static BsonDocument NestedPostDoc(string heading)
        => new() {{"Heading", heading}, {"Comments", new BsonArray()}};

    // ---- Lazy inverse owner navigation helpers ----

    private static SingleEntityDbContext<OwnerRefBlog> CreateOwnerRefContext(
        IMongoCollection<OwnerRefBlog> collection, MongoQueryMode mode)
        => SingleEntityDbContext.Create(
            collection,
            modelBuilderAction: OwnerRefModel,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });

    // Same shape as SeedKeyed. The Owner back-reference is a navigation, not a stored field.
    private IMongoCollection<OwnerRefBlog> SeedOwnerRef(string name)
    {
        var coll = database.MongoDatabase.GetCollection<BsonDocument>(UniqueCollectionName(name));
        coll.InsertMany(
        [
            Row("a_empty"),
            Row("b_one", PostDoc(1, "h1")),
            Row("c_two", PostDoc(2, "h2"), PostDoc(3, "h3"))
        ]);
        return database.MongoDatabase.GetCollection<OwnerRefBlog>(coll.CollectionNamespace.CollectionName);
    }

    // ---- Breadth helpers ----

    // Same as SeedKeyed plus a "Rank" field.
    private static BsonDocument RowWithRank(string title, int rank, params BsonDocument[] posts)
        => new()
        {
            {"_id", ObjectId.GenerateNewId()},
            {"Title", title},
            {"Rank", rank},
            {"Posts", new BsonArray(posts)}
        };

    private IMongoCollection<Blog> SeedKeyedWithRank(string name)
    {
        var coll = database.MongoDatabase.GetCollection<BsonDocument>(UniqueCollectionName(name));
        coll.InsertMany(
        [
            RowWithRank("a_empty", 1),
            RowWithRank("b_one", 2, PostDoc(1, "h1")),
            RowWithRank("c_two", 3, PostDoc(2, "h2"), PostDoc(3, "h3"))
        ]);
        return database.MongoDatabase.GetCollection<Blog>(coll.CollectionNamespace.CollectionName);
    }

    // Same as SeedKeyed plus a "Tags" array whose lengths (1, 2, 0) vary independently of Posts, so a
    // swapped-alias bug between Tags and Posts shows up.
    private static BsonDocument RowWithTags(string title, string[] tags, params BsonDocument[] posts)
        => new()
        {
            {"_id", ObjectId.GenerateNewId()},
            {"Title", title},
            {"Tags", new BsonArray(tags)},
            {"Posts", new BsonArray(posts)}
        };

    private IMongoCollection<Blog> SeedKeyedWithTags(string name)
    {
        var coll = database.MongoDatabase.GetCollection<BsonDocument>(UniqueCollectionName(name));
        coll.InsertMany(
        [
            RowWithTags("a_empty", ["x"]),
            RowWithTags("b_one", ["y", "z"], PostDoc(1, "h1")),
            RowWithTags("c_two", [], PostDoc(2, "h2"), PostDoc(3, "h3"))
        ]);
        return database.MongoDatabase.GetCollection<Blog>(coll.CollectionNamespace.CollectionName);
    }

    private static SingleEntityDbContext<TwoArrayBlog> CreateTwoArrayContext(
        IMongoCollection<TwoArrayBlog> collection, MongoQueryMode mode)
        => SingleEntityDbContext.Create(
            collection,
            modelBuilderAction: TwoArrayModel,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });

    private static BsonDocument TwoArrayRow(string title, BsonDocument[]? posts = null, BsonDocument[]? drafts = null)
        => new()
        {
            {"_id", ObjectId.GenerateNewId()},
            {"Title", title},
            {"Posts", new BsonArray(posts ?? [])},
            {"Drafts", new BsonArray(drafts ?? [])}
        };

    private IMongoCollection<TwoArrayBlog> SeedTwoArrays(string name)
    {
        var coll = database.MongoDatabase.GetCollection<BsonDocument>(UniqueCollectionName(name));
        coll.InsertMany(
        [
            TwoArrayRow("a_empty"),
            TwoArrayRow("b_mixed", [PostDoc(1, "p1")], [PostDoc(11, "d1"), PostDoc(12, "d2")])
        ]);
        return database.MongoDatabase.GetCollection<TwoArrayBlog>(coll.CollectionNamespace.CollectionName);
    }

    private static SingleEntityDbContext<HashSetBlog> CreateHashSetContext(
        IMongoCollection<HashSetBlog> collection, MongoQueryMode mode)
        => SingleEntityDbContext.Create(
            collection,
            modelBuilderAction: HashSetModel,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });

    // Seeds HashSetBlog with the same empty/populated pair as SeedNested.
    private IMongoCollection<HashSetBlog> SeedHashSet(string name)
    {
        var coll = database.MongoDatabase.GetCollection<BsonDocument>(UniqueCollectionName(name));
        coll.InsertMany(
        [
            Row("a_empty"),
            Row("b_two", PostDoc(1, "h1"), PostDoc(2, "h2"))
        ]);
        return database.MongoDatabase.GetCollection<HashSetBlog>(coll.CollectionNamespace.CollectionName);
    }

    // ---- Correctness helpers ----

    // The five-state matrix, seeded as raw BSON (the change tracker can never write a missing array):
    //   a_missing — no "Posts" element in the document at all
    //   b_null    — "Posts": null (explicit BSON null)
    //   c_empty   — "Posts": []
    //   d_single  — one element
    //   e_multi   — two elements
    // Rank and Tags are seeded uniformly because the oracle materializes whole Blogs. The read-back self-check is
    // load-bearing: "missing" and "empty" give identical results, so a bad seed would silently test fewer states.
    private IMongoCollection<Blog> SeedFiveStates(string name)
    {
        var raw = database.MongoDatabase.GetCollection<BsonDocument>(UniqueCollectionName(name));
        raw.InsertMany(
        [
            FiveStateRow("a_missing", null),
            FiveStateRow("b_null", BsonNull.Value),
            FiveStateRow("c_empty", PostsArray()),
            FiveStateRow("d_single", PostsArray(PostDoc(1, "h1"))),
            FiveStateRow("e_multi", PostsArray(PostDoc(2, "h2"), PostDoc(3, "h3")))
        ]);

        var stored = raw.Find(FilterDefinition<BsonDocument>.Empty).ToList().ToDictionary(d => d["Title"].AsString);
        Assert.False(stored["a_missing"].Contains("Posts"));
        Assert.True(stored["b_null"]["Posts"].IsBsonNull);
        Assert.Empty(stored["c_empty"]["Posts"].AsBsonArray);
        Assert.Single(stored["d_single"]["Posts"].AsBsonArray);
        Assert.Equal(2, stored["e_multi"]["Posts"].AsBsonArray.Count);

        return database.MongoDatabase.GetCollection<Blog>(raw.CollectionNamespace.CollectionName);
    }

    // A null `posts` omits the element; BsonNull.Value writes an explicit BSON null.
    private static BsonDocument FiveStateRow(string title, BsonValue? posts)
    {
        var doc = new BsonDocument
        {
            {"_id", ObjectId.GenerateNewId()},
            {"Title", title},
            {"Rank", 1},
            {"Tags", new BsonArray()}
        };

        if (posts is not null)
        {
            doc.Add("Posts", posts);
        }

        return doc;
    }

    private static BsonArray PostsArray(params BsonDocument[] posts)
        => new(posts);

    // ---- Set-op helpers ----

    // Two distinct documents with identical projected { Title, Posts } values, differing only in Rank (which the
    // operands filter on), so a set op's row count reveals what it compared. No Tags element is written.
    private IMongoCollection<Blog> SeedProjectionValueTwins(string name)
    {
        var coll = database.MongoDatabase.GetCollection<BsonDocument>(UniqueCollectionName(name));
        coll.InsertMany(
        [
            RowWithRank("same", 1, PostDoc(1, "h1")),
            RowWithRank("same", 2, PostDoc(1, "h1"))
        ]);
        return database.MongoDatabase.GetCollection<Blog>(coll.CollectionNamespace.CollectionName);
    }

    // Two documents identical except for stored array state (missing vs []); both materialize empty, so they
    // probe whether a set op compares stored or materialized values. Self-checked like SeedFiveStates.
    private IMongoCollection<Blog> SeedArrayStateTwins(string name)
    {
        var raw = database.MongoDatabase.GetCollection<BsonDocument>(UniqueCollectionName(name));

        var missing = new BsonDocument
        {
            {"_id", ObjectId.GenerateNewId()}, {"Title", "t"}, {"Rank", 1}, {"Tags", new BsonArray()}
        };
        var empty = new BsonDocument
        {
            {"_id", ObjectId.GenerateNewId()}, {"Title", "t"}, {"Rank", 1}, {"Tags", new BsonArray()},
            {"Posts", new BsonArray()}
        };
        raw.InsertMany([missing, empty]);

        var stored = raw.Find(FilterDefinition<BsonDocument>.Empty).ToList();
        Assert.Equal(2, stored.Count);
        Assert.Single(stored, d => !d.Contains("Posts"));
        Assert.Single(stored, d => d.Contains("Posts") && d["Posts"].AsBsonArray.Count == 0);

        return database.MongoDatabase.GetCollection<Blog>(raw.CollectionNamespace.CollectionName);
    }

    private static SingleEntityDbContext<ConvBlog> CreateConverterContext(
        IMongoCollection<ConvBlog> collection, MongoQueryMode mode)
        => SingleEntityDbContext.Create(
            collection,
            modelBuilderAction: ConverterModel,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });

    // One ConvBlog whose element Ref and Code are stored as BSON strings. The driver reads a string leniently
    // (GuidSerializer and EnumSerializer both accept one), so a plain value would pass even with the converter
    // and representation removed. Hence:
    //   * Code's converter is transforming ("g:" prefix): only the configured converter can yield Grade.High.
    //     This is the discriminating half.
    //   * Ref's assertion is a round-trip pin only. A Binary seed would distinguish configured from unconfigured
    //     (GuidSerializer(BsonType.String) throws on Binary), but as an exception, not a differing value.
    // Both Guids are non-empty and distinct, so neither assertion passes on a never-read default or a
    // read-element-0-for-all bug.
    private (IMongoCollection<ConvBlog> Collection, Guid ExpectedRefA, Guid ExpectedRefB) SeedConverters(string name)
    {
        var expectedRefA = Guid.Parse("27701bfc-78d0-4e2b-92ca-193cea53fa30");
        var expectedRefB = Guid.Parse("5c4d9f10-8a3e-4b77-b1d2-6e0f43a9c581");

        var raw = database.MongoDatabase.GetCollection<BsonDocument>(UniqueCollectionName(name));
        raw.InsertOne(
            new BsonDocument
            {
                {"_id", ObjectId.GenerateNewId()},
                {"Title", "t"},
                {
                    "Posts", new BsonArray(
                        new[]
                        {
                            new BsonDocument
                                {{"Heading", "a"}, {"Ref", expectedRefA.ToString()}, {"Code", "g:" + Grade.High}},
                            new BsonDocument
                                {{"Heading", "b"}, {"Ref", expectedRefB.ToString()}, {"Code", "g:" + Grade.Low}}
                        })
                }
            });

        var stored = raw.Find(FilterDefinition<BsonDocument>.Empty).Single()["Posts"].AsBsonArray;
        Assert.All(stored, e => Assert.Equal(BsonType.String, e.AsBsonDocument["Ref"].BsonType));
        Assert.All(stored, e => Assert.Equal(BsonType.String, e.AsBsonDocument["Code"].BsonType));
        // Check the stored documents, not just the constants: no element may store Guid.Empty, or a never-read
        // Ref would pass.
        Assert.All(stored, e => Assert.NotEqual(Guid.Empty.ToString(), e.AsBsonDocument["Ref"].AsString));
        Assert.Equal(2, stored.Select(e => e.AsBsonDocument["Ref"].AsString).Distinct().Count());

        return (database.MongoDatabase.GetCollection<ConvBlog>(raw.CollectionNamespace.CollectionName),
            expectedRefA, expectedRefB);
    }

    // ════════════════════════════════════════════════════════════════════════════════════════════
    //  Positional containers of scalar elements — `new[] { x.I, x.NullableI }`, `new object[] { ... }` — each
    //  element projected under its own `_ctorArg<N>` alias and read with its operand's own type (so a boxed element
    //  keeps its runtime type). A scalar `new List<object> { ... }` is not a native container and stays declined (see
    //  below). Whole-entity elements/arguments are never admitted on this scalar path.
    // ════════════════════════════════════════════════════════════════════════════════════════════

    public class ScalarRow
    {
        public ObjectId Id { get; set; }
        public string Label { get; set; } = "";
        public int I { get; set; }
        public int? NullableI { get; set; }
        public string? S { get; set; }
        public long L { get; set; }

        // Stored as Decimal128: an object-typed read of the raw value would answer Decimal128, not decimal.
        public decimal D { get; set; }
    }

    // Three rows with non-default, pairwise-distinct values, so a swapped/shared alias or a default read shows up:
    //   a: NullableI null, S "abc"; b: NullableI 5, S null; c: NullableI 7, S missing.
    private IMongoCollection<ScalarRow> SeedScalarRows(string name)
    {
        var coll = database.MongoDatabase.GetCollection<BsonDocument>(UniqueCollectionName(name));
        coll.InsertMany(
        [
            new BsonDocument
            {
                {"_id", ObjectId.GenerateNewId()}, {"Label", "a"}, {"I", 1}, {"NullableI", BsonNull.Value},
                {"S", "abc"}, {"L", 10L}, {"D", new Decimal128(1.5m)}
            },
            new BsonDocument
            {
                {"_id", ObjectId.GenerateNewId()}, {"Label", "b"}, {"I", 2}, {"NullableI", 5}, {"S", BsonNull.Value},
                {"L", 20L}, {"D", new Decimal128(2.5m)}
            },
            new BsonDocument
            {
                {"_id", ObjectId.GenerateNewId()}, {"Label", "c"}, {"I", 3}, {"NullableI", 7}, {"L", 30L}, {"D", new Decimal128(3.5m)}
            }
        ]);
        return database.MongoDatabase.GetCollection<ScalarRow>(coll.CollectionNamespace.CollectionName);
    }

    private static SingleEntityDbContext<ScalarRow> CreateScalarContext(IMongoCollection<ScalarRow> collection, MongoQueryMode mode)
        => SingleEntityDbContext.Create(
            collection,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });

    // Renders one element with its runtime type, so equal values of different CLR types (int vs long) differ.
    private static string DescribeElement(object? element)
        => element is null ? "<null>" : $"{element.GetType().Name}:{element}";

    [Fact]
    public void Scalar_array_container_goes_native_with_exact_values()
    {
        var collection = SeedScalarRows(nameof(Scalar_array_container_goes_native_with_exact_values));

        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateScalarContext(collection, mode);
            return db.Entities.OrderBy(x => x.Label).Select(x => new[] { x.I, x.NullableI }).ToList()
                .Select(row => string.Join(",", row.Select(e => e?.ToString() ?? "<null>")))
                .ToList();
        });

        Assert.Equal(["1,<null>", "2,5", "3,7"], results);
    }

    // AssertArrays in the spec suite checks each element's GetType(): a boxed element must be read with its operand's
    // own type (int stays Int32, long stays Int64), not as whatever an object-typed read of the BSON value yields.
    [Fact]
    public void Boxed_object_array_container_keeps_each_elements_runtime_type()
    {
        var collection = SeedScalarRows(nameof(Boxed_object_array_container_keeps_each_elements_runtime_type));

        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateScalarContext(collection, mode);
            return db.Entities.OrderBy(x => x.Label).Select(x => new object?[] { x.I, x.S, x.L, x.D }).ToList()
                .Select(row => string.Join(",", row.Select(DescribeElement)))
                .ToList();
        });

        Assert.Equal(
            [
                "Int32:1,String:abc,Int64:10,Decimal:1.5",
                "Int32:2,<null>,Int64:20,Decimal:2.5",
                "Int32:3,<null>,Int64:30,Decimal:3.5"
            ],
            results);
    }

    // A boxed nullable element holding null. No driver-LINQ oracle: the driver's push-down deserializes the boxed int?
    // with the non-nullable Int32 serializer and throws FormatException ("Cannot deserialize a 'Int32' from BsonType
    // 'Null'"), as it did before this shape went native. Hand-computed oracle instead.
    [Theory]
    [InlineData(MongoQueryMode.NativeOnly)]
    [InlineData(MongoQueryMode.Native)]
    public void Boxed_nullable_object_array_element_reads_null_and_keeps_runtime_types(MongoQueryMode mode)
    {
        var collection = SeedScalarRows(nameof(Boxed_nullable_object_array_element_reads_null_and_keeps_runtime_types) + mode);
        using var db = CreateScalarContext(collection, mode);

        var results = db.Entities.OrderBy(x => x.Label).Select(x => new object?[] { x.I, x.NullableI, x.S, x.L }).ToList()
            .Select(row => string.Join(",", row.Select(DescribeElement)))
            .ToList();

        Assert.Equal(
            [
                "Int32:1,<null>,String:abc,Int64:10",
                "Int32:2,Int32:5,<null>,Int64:20",
                "Int32:3,Int32:7,<null>,Int64:30"
            ],
            results);
    }

    // A case-mapped container element over a null/missing string reads null natively: the raw string is projected and
    // the mapping re-applied client-side with EF's null propagation (the relational answer, UPPER(NULL) = NULL).
    // Explicit DriverLinq pushes $toUpper down instead, which maps null/missing to "" (server semantics), so it is
    // not an oracle here; the native null is the EF-faithful answer and is kept. Hand-computed oracle.
    [Theory]
    [InlineData(MongoQueryMode.NativeOnly)]
    [InlineData(MongoQueryMode.Native)]
    public void Case_mapped_container_element_over_a_null_string_reads_null(MongoQueryMode mode)
    {
        var collection = SeedScalarRows(nameof(Case_mapped_container_element_over_a_null_string_reads_null) + mode);
        using var db = CreateScalarContext(collection, mode);

        var results = db.Entities.OrderBy(x => x.Label).Select(x => new[] { x.S!.ToUpper(), x.Label }).ToList()
            .Select(row => string.Join(",", row.Select(DescribeElement)))
            .ToList();

        Assert.Equal(["String:ABC,String:a", "<null>,String:b", "<null>,String:c"], results);
    }

    // A string-sequence element (`x.S.ToList()`, a List<char>) in a container. No driver-LINQ oracle: explicit
    // DriverLinq throws ArgumentNullException materializing it even for a non-null string. So the native answer is
    // pinned against a hand-computed oracle: the characters for a non-null string, and C#'s own ArgumentNullException
    // (`((string)null).ToList()`) for a null/missing one.
    [Theory]
    [InlineData(MongoQueryMode.NativeOnly)]
    [InlineData(MongoQueryMode.Native)]
    public void String_sequence_container_element_matches_the_hand_oracle(MongoQueryMode mode)
    {
        var collection = SeedScalarRows(nameof(String_sequence_container_element_matches_the_hand_oracle) + mode);
        using var db = CreateScalarContext(collection, mode);

        var row = Assert.Single(db.Entities.Where(x => x.Label == "a").Select(x => new object?[] { x.S!.ToList(), x.I }).ToList());
        Assert.Equal(['a', 'b', 'c'], Assert.IsType<List<char>>(row[0]));
        Assert.Equal(1, Assert.IsType<int>(row[1]));

        foreach (var label in new[] { "b", "c" })
        {
            Assert.Throws<ArgumentNullException>(
                () => db.Entities.Where(x => x.Label == label).Select(x => new object?[] { x.S!.ToList(), x.I }).ToList());
        }
    }

    // `new List<object> { ... }` is not a native container: the driver can't push a list initializer down, so the
    // index-based shaper would be unreadable under explicit DriverLinq / a late fallback (see
    // TryGetProjectionMembers' allowContainerElements). It keeps declining to the client-side construction.
    [Fact]
    public void Object_list_container_declines_cleanly()
    {
        var collection = SeedScalarRows(nameof(Object_list_container_declines_cleanly));

        var results = NativeModeAssert.DeclinesCleanly(mode =>
        {
            using var db = CreateScalarContext(collection, mode);
            return db.Entities.OrderBy(x => x.Label).Select(x => new List<object?> { x.S }).ToList()
                .Select(row => $"{row.Count}|{string.Join(",", row.Select(DescribeElement))}")
                .ToList();
        });

        Assert.Equal(["1|String:abc", "1|<null>", "1|<null>"], results);
    }

    // A non-nullable Length over a possibly-null string, as a container element, goes through the same null-propagation
    // classifier as a named member (ClassifyNonNullableValueRead): EF's "Nullable object must have a value." on the
    // null/missing rows, never a silent 0. No driver-LINQ oracle: its unguarded $strLenCP fails server-side on null.
    [Fact]
    public void Non_nullable_Length_container_element_throws_on_null_instead_of_reading_zero()
    {
        var collection = SeedScalarRows(nameof(Non_nullable_Length_container_element_throws_on_null_instead_of_reading_zero));
        using var db = CreateScalarContext(collection, MongoQueryMode.NativeOnly);

        foreach (var label in new[] { "b", "c" })
        {
            var exception = Assert.Throws<InvalidOperationException>(
                () => db.Entities.Where(x => x.Label == label).Select(x => new[] { x.I, x.S!.Length }).ToList());
            Assert.Equal("Nullable object must have a value.", exception.Message);
        }

        var row = Assert.Single(db.Entities.Where(x => x.Label == "a").Select(x => new[] { x.I, x.S!.Length }).ToList());
        Assert.Equal([1, 3], row);
    }

    // Keyed by a string property that is not named Id, so it is stored as `_id` under a different CLR name: a driver
    // class-map read of the raw document (what a `$$ROOT` scalar leaf gets) cannot map `_id` and throws, where the EF
    // entity materializer maps it.
    public class CodedRow
    {
        public string Code { get; set; } = "";
        public string? City { get; set; }
    }

    private IMongoCollection<CodedRow> SeedCodedRows(string name)
    {
        var coll = database.MongoDatabase.GetCollection<BsonDocument>(UniqueCollectionName(name));
        coll.InsertMany(
        [
            new BsonDocument { {"_id", "A"}, {"City", "Oslo"} },
            new BsonDocument { {"_id", "B"}, {"City", BsonNull.Value} }
        ]);
        return database.MongoDatabase.GetCollection<CodedRow>(coll.CollectionNamespace.CollectionName);
    }

    // Before this change the multi-argument positional-ctor arm admitted the whole entity as a `$$ROOT` leaf and the
    // read side threw FormatException ("Element '_id' does not match any field or property of class CodedRow"). A
    // whole-entity argument is now excluded from the scalar positional path, so the construction is the whole-entity
    // client construction instead (NativeClientWholeEntityShape): whole documents fetched, the pair built client-side
    // over the materialized entity. Oracle is hand-computed: driver-LINQ throws NotImplementedException for this shape.
    [Theory]
    [InlineData(MongoQueryMode.NativeOnly)]
    [InlineData(MongoQueryMode.Native)]
    public void Whole_entity_positional_ctor_argument_is_not_read_as_a_scalar(MongoQueryMode mode)
    {
        var collection = SeedCodedRows(nameof(Whole_entity_positional_ctor_argument_is_not_read_as_a_scalar) + mode);
        using var db = SingleEntityDbContext.Create(
            collection,
            modelBuilderAction: mb => mb.Entity<CodedRow>(e =>
            {
                e.HasKey(x => x.Code);
                e.Property(x => x.Code).HasElementName("_id");
            }),
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });

        var results = db.Entities.OrderBy(x => x.Code).Select(x => new KeyValuePair<CodedRow, string?>(x, x.City)).ToList();

        Assert.Equal(["A:Oslo=Oslo", "B:<null>=<null>"],
            results.Select(p => $"{p.Key.Code}:{p.Key.City ?? "<null>"}={p.Value ?? "<null>"}").ToList());
        Assert.All(results, p => Assert.Same(p.Key, db.Entities.Local.Single(l => l.Code == p.Key.Code)));
    }

    // A whole-entity argument beside a leaf the whole-entity client construction does not admit (a translatable
    // Substring call) must decline, not take the scalar positional path as a `$$ROOT` leaf: this is the case the
    // argument-level whole-entity exclusion alone decides (the client construction would otherwise claim the body
    // first). Only NativeOnly is asserted: the fallback's driver-LINQ throws NotImplementedException for this shape.
    [Fact]
    public void Whole_entity_positional_ctor_argument_beside_a_computed_argument_declines()
    {
        var collection = SeedCodedRows(nameof(Whole_entity_positional_ctor_argument_beside_a_computed_argument_declines));
        using var db = SingleEntityDbContext.Create(
            collection,
            modelBuilderAction: mb => mb.Entity<CodedRow>(e =>
            {
                e.HasKey(x => x.Code);
                e.Property(x => x.Code).HasElementName("_id");
            }),
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(MongoQueryMode.NativeOnly);
            });

        Assert.Throws<NativeTranslationNotSupportedException>(
            () => db.Entities.Select(x => new KeyValuePair<CodedRow, string>(x, x.City!.Substring(1))).ToList());
    }

    private static string ClientDescribe(ScalarRow row) => $"<{row.Label}>";

    // An opaque client call on the whole entity as an element is the whole-entity client construction's, so the scalar
    // container arm steps aside (IsScalarPositionalConstruction) instead of declining on the untranslatable call.
    [Fact]
    public void Container_with_a_client_call_on_the_whole_entity_goes_native_with_parity()
    {
        var collection = SeedScalarRows(nameof(Container_with_a_client_call_on_the_whole_entity_goes_native_with_parity));

        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateScalarContext(collection, mode);
            return db.Entities.OrderBy(x => x.Label).Select(x => new object?[] { ClientDescribe(x), x.I }).ToList()
                .Select(row => string.Join(",", row.Select(DescribeElement)))
                .ToList();
        });

        Assert.Equal(["String:<a>,Int32:1", "String:<b>,Int32:2", "String:<c>,Int32:3"], results);
    }

    // The container counterpart: a whole-entity element beside scalars is the whole-entity client construction, never a
    // scalar container element; each scalar (boxed, computed or not) is read off the materialized entity. The boxed and
    // computed elements used to read null there (a projection binding read by its member name off the raw document).
    [Fact]
    public void Whole_entity_container_element_is_not_read_as_a_scalar()
    {
        var collection = SeedScalarRows(nameof(Whole_entity_container_element_is_not_read_as_a_scalar));

        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateScalarContext(collection, mode);
            return db.Entities.OrderBy(x => x.Label).Select(x => new object?[] { x, x.I, x.I + 10, x.S }).ToList()
                .Select(row => $"{((ScalarRow)row[0]!).Label}|{string.Join(",", row.Skip(1).Select(DescribeElement))}")
                .ToList();
        });

        Assert.Equal(["a|Int32:1,Int32:11,String:abc", "b|Int32:2,Int32:12,<null>", "c|Int32:3,Int32:13,<null>"], results);
    }
}
