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
/// EF-322 owned-data slice 8: an owned entity-COLLECTION leaf inside a terminal anonymous-type
/// projection — <c>Select(b =&gt; new { b.Title, b.Posts })</c> — goes native, emitting a server-side
/// <c>$project</c> that includes the array instead of fetching whole documents and folding the projection
/// client-side. Routing is proven by <see cref="MongoQueryMode.NativeOnly"/>, never by MQL shape.
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeArrayProjectionTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    // NOTE the deliberate absence of `= []` on Posts. A field initializer masks exactly the class of bug
    // this slice touches (a null-vs-empty read-back is invisible when the POCO pre-populates the
    // navigation); see the design doc's verification section and EF-358's own un-masked fixtures.
    public class Blog
    {
        public ObjectId Id { get; set; }
        public string Title { get; set; } = "";
        public List<Post> Posts { get; set; } = null!;

        // Task 5 item 5 (arithmetic leaf alongside an array leaf) and item 8 (primitive-collection leaf
        // alongside an array leaf). Plain scalar/primitive-collection properties, unrelated to the array-leaf
        // admissibility rule; safe additions to the shared model because no EXISTING test in this file selects
        // a whole Blog entity or projects Rank/Tags, so their absence from other tests' seeded documents is
        // never observed. Tags deliberately has NO `= []` initializer either, for the same un-masking reason
        // Posts does not.
        public int Rank { get; set; }
        public List<string> Tags { get; set; } = null!;
    }

    // No navigations of its own, and an EXPLICIT key: this is the walking-skeleton model. An element
    // carrying a navigation is the deferred EF-360 case (declined in a later task); a shadow-key element
    // needs the owner _id emitted alongside the array (also a later task).
    public class Post
    {
        public int PostId { get; set; }
        public string? Heading { get; set; }
    }

    private static readonly Action<ModelBuilder> KeyedModel = mb =>
        mb.Entity<Blog>().OwnsMany(b => b.Posts, p => p.HasKey(x => x.PostId));

    // Same Blog/Post CLR shape as KeyedModel, but with NO explicit HasKey call on Post. This is the model
    // shape almost every real user has: EF's key-discovery convention does not treat Post's own "PostId"
    // property as its primary key for an OWNED COLLECTION element (see the PostDoc comment below on why the
    // stored element name differs for the keyed model) — instead EF builds a SHADOW composite key (owner FK +
    // ordinal), and the element shaper reads the OWNER's key out of the document it is given via
    // _ownerMappings, not anything stored on the element itself. See NativeProjectionBinder.
    // TryPopulateNativeProjection's owner-key-emission comment for the mechanism this model exercises.
    private static readonly Action<ModelBuilder> ShadowKeyModel = mb =>
        mb.Entity<Blog>().OwnsMany(b => b.Posts);

    // Same walking-skeleton element shape as Post (explicit key, no navigations of its own), but the collection
    // is reached THROUGH an owned single reference — so the navigation is declared on Home, not on the query
    // root. That one difference is what the shared admissibility predicate declines.
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

    // Element-with-its-own-navigation model (EF-360). NestedPost, the OwnsMany element type declared directly
    // on the query root, itself owns a further collection (Comments) — the shape the shared admissibility rule
    // (NativeProjectionBinder.IsNativeArrayProjectionLeaf) now structurally declines, per its slice-8 Task 4
    // remarks. Deliberately un-masked: no `= []` on either collection navigation.
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

    // Final-review finding 1. The element carries EXACTLY ONE navigation, and it is the LAZY INVERSE
    // back-reference to its own owner, configured by OwnsMany(..., p => p.WithOwner(x => x.Owner)) — an
    // entirely ordinary model, not an exotic one. It is NOT a nested owned navigation, so EF Core never
    // auto-includes it and it therefore never produces the inner Queryable.Select that crashes shaper build
    // (that crash is EF-360, pinned by NestedModel above). The admissibility conjunct must distinguish the two,
    // which is why it tests IsEagerLoaded rather than mere presence — mirroring the sibling reference-kind
    // guard, MongoQueryableMethodTranslatingExpressionVisitor.IsWholeElementRepresentable.
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

    // Task 5 item 1: a named DTO / MemberInit spelling, as opposed to every other test's anonymous-type
    // NewExpression spelling. Exercises NativeProjectionBinder.TryPopulateNativeProjection's MemberInitExpression
    // branch. Alias "Posts" (the property being ASSIGNED) equals the navigation's containing element name, so
    // this is admissible under the shared rule exactly like the anonymous-type spelling.
    public class TitlePosts
    {
        public string Title { get; set; } = "";
        public List<Post> Posts { get; set; } = null!;
    }

    // Task 5 item 3: two owned-collection array leaves in ONE projection. A dedicated model (rather than adding
    // a second navigation to the shared Blog) keeps this shape isolated from every other test in this file.
    // Post is reused as the CLR element type for both navigations (a shared-CLR-type owned collection, already
    // a supported shape per the EF-322 owned-collection whole-entity note) with distinct explicit keys per
    // navigation, so there is no key collision between the two owned types.
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

    // Task 5 item 6: a HashSet-backed collection navigation, to prove the array-leaf branch reads the element
    // back through the navigation's own IClrCollectionAccessor rather than a hand-built List<T>.
    public class HashSetBlog
    {
        public ObjectId Id { get; set; }
        public string Title { get; set; } = "";
        public HashSet<Post> Posts { get; set; } = null!;
    }

    private static readonly Action<ModelBuilder> HashSetModel = mb =>
        mb.Entity<HashSetBlog>().OwnsMany(b => b.Posts, p => p.HasKey(x => x.PostId));

    // Task 6: the converter-through-the-alias model. The ELEMENT carries a Guid with a non-default
    // BsonRepresentation and a value-converted enum. The enum's converter is deliberately TRANSFORMING ("g:"
    // prefix) rather than a bare ToString — see SeedConverters for the measurement that forced that, and for why
    // only the enum half of this model can discriminate a converted read from a raw one.
    // Deliberately un-masked (no `= []` on Posts), like every other model in this file.
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

    // No explicit HasKey, so this element gets a SHADOW composite key exactly like ShadowKeyModel's Post — the
    // route that needs the owner _id emitted alongside the array (Task 4). Combining the two concerns in one
    // model is deliberate: the converter question is about the per-ELEMENT inner shaper, and the shadow key is
    // what makes the element shaper read the owner's key too, so this exercises both at once.
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
        // Observed: aggregate([{ "$project" : { "Title" : "$Title", "Posts" : "$Posts", "_id" : "$_id" } }])
        //
        // CORRECTED IN PLACE (slice-8 doc sweep): this line used to record `"_id" : 0`, which was the Task 3
        // emission. Task 4 made the binder emit the owner key alongside any array leaf, so the `_id : 0`
        // exclusion is suppressed and `"_id" : "$_id"` is emitted instead. Owned_array_leaf_projection_emits_the_owner_key
        // below pins that change, though only as "_id present, `"_id" : 0` absent" rather than as the literal
        // field-ref form; the literal form is quoted in the C1 dedup test's comment. This test asserts neither
        // `_id` form, so it stayed green through the change and the stale observation survived unnoticed; the
        // assertions below are unchanged.
        //
        // The LOAD-BEARING fragment is `"Posts" : "$Posts"` — the array projected by alias to a field ref. A bare
        // Assert.Contains("Posts") would pass on any pipeline that merely MENTIONS Posts anywhere (a $match, a
        // $lookup, a client-side-folded whole-document fetch), so it would not distinguish this from the old
        // aggregate([]) behaviour at all.
        Assert.Contains("$project", mql);
        Assert.Contains("\"Posts\" : \"$Posts\"", mql);
        Assert.DoesNotContain("aggregate([])", mql);
    }

    [Fact]
    public void Owned_array_leaf_projection_matches_driver_linq()
    {
        var collection = SeedKeyed(nameof(Owned_array_leaf_projection_matches_driver_linq));

        // Headings are compared as a JOINED STRING, not as the string?[] the plan first specified: a
        // ValueTuple's Equals uses EqualityComparer<string?[]>.Default, i.e. REFERENCE equality for an array,
        // so the array form can never compare equal across two separately-materialized result sets — it failed
        // with two identically-PRINTED collections before this was changed.
        //
        // A null heading is projected as the sentinel "<null>" rather than joined as "": string.Join would
        // conflate a null element with an empty one, which weakens the oracle exactly where a later slice adds
        // ragged/absent states.
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

    // A RENAMED alias (`P = b.Posts`, alias "P" vs. document element "Posts") is DELIBERATELY declined — see
    // NativeProjectionBinder.IsNativeArrayProjectionLeaf's remarks. The shaper is alias-addressed from
    // translation time onward, but whether a $project is emitted is decided LATER by the gate, so an explicit
    // DriverLinq (aggregate([]), whole documents) hands that same shaper an un-projected row.
    //
    // This test is a REGRESSION test, not a scope statement: before the alias conjunct existed this exact query
    // returned 1 element under Native/NativeOnly and 0 elements, SILENTLY, under DriverLinq. The plain
    // `new { b.Title, b.Posts }` spelling masked it, because an anonymous type's implicit member name is the
    // property name and so happened to satisfy the invariant.
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

        // BOTH fallback-capable modes must agree with the seeded truth — the DriverLinq leg is the one that
        // silently returned zeros before the fix.
        foreach (var mode in new[] {MongoQueryMode.Native, MongoQueryMode.DriverLinq})
        {
            using var db = CreateContext(collection, KeyedModel, mode);
            Assert.Equal(new[] {0, 1, 2}, Run(db).Select(r => r.Count));
        }

        using var nativeOnly = CreateContext(collection, KeyedModel, MongoQueryMode.NativeOnly);
        Assert.Throws<NativeTranslationNotSupportedException>(() => Run(nativeOnly));
    }

    // FLIPPED BY EF-362. This used to be the TRIPWIRE for the nested-owner narrowing
    // (`Array_leaf_under_a_nested_owner_is_declined_but_still_returns_correct_data`, asserting a NativeOnly
    // throw); the narrowing has now been widened deliberately, which is exactly the flip that test existed to
    // force. Its parity leg is kept verbatim, and it is the oracle the widening was checked against: the
    // values below are what the fallback returned before EF-362.
    //
    // The full EF-362 surface — the ragged array-state matrix, the parameterized-Where late-fallback leg, the
    // dotted `$project` alias, and the shadow-key element — lives in Ef362OwnedHopArrayProjectionTests. This
    // one stays here as the flipped tripwire, in the file whose predicate was widened.
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

    // Measured (spike Q1a): with a shadow-key owned collection the element shaper reads the OWNER's key out
    // of the document it is given. A $project that emits only { Title, Posts } has no _id, and materialization
    // then fails PER ROW with InvalidOperationException "Document element is missing for required
    // non-nullable property 'Id'" at Storage/BsonBinding.cs:229. So the array-leaf branch must emit the
    // owner key alongside. An explicit-HasKey element never emits that read at all (Q1b), which is why the
    // walking-skeleton test in Task 3 passed without this.
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

    // An element type carrying an EAGER-LOADED navigation of its own — a nested owned collection, or a nested
    // owned single reference, behaving identically — is still DECLINED by the native array-leaf branch, but the
    // fallback it declines into now WORKS. It used to throw ArgumentException in every mode, before
    // MongoQueryMode was even read: EF's nav-expansion emits the element's auto-include as an inner
    // Queryable.Select, MongoProjectionBindingExpressionVisitor rebuilt that as an IEnumerable<T> rather than
    // the navigation's declared List<T>, and Expression.New's member-type validation rejected the mismatch
    // (MatchTypes deliberately skips collection-typed targets, so nothing coerced it first). That was EF-360,
    // fixed on the main-bound line by converting the visited subquery to the navigation's declared type — a
    // runtime no-op, since MongoProjectionBindingRemovingExpressionVisitor discards this exact
    // Select-over-IncludeExpression shape later for the correctly-typed CollectionShaperExpression.
    //
    // So the routing claim is unchanged and the DATA claim is new: Native and DriverLinq return correct rows,
    // NativeOnly still declines — cleanly now, rather than crashing.
    //
    // Note the admissibility conjunct is `!GetNavigations().Any(n => n.IsEagerLoaded)`, not mere presence: a
    // LAZY navigation (the inverse owner back-reference — see
    // Array_leaf_whose_element_has_only_a_lazy_inverse_owner_navigation_goes_native below) IS admitted and does
    // not decline. Only an eager-loaded one declines, because that is what triggers EF's auto-include.
    [Fact]
    public void Element_with_its_own_navigation_is_declined_but_the_fallback_returns_correct_rows()
    {
        var collection = SeedNested(nameof(Element_with_its_own_navigation_is_declined_but_the_fallback_returns_correct_rows));

        foreach (var mode in new[] {MongoQueryMode.Native, MongoQueryMode.DriverLinq, MongoQueryMode.NativeOnly})
        {
            using var db = CreateContext(collection, NestedModel, mode);

            // The premise the admissibility conjunct rests on, asserted rather than assumed: a NESTED OWNED
            // navigation IS eager-loaded (EF Core convention), which is what keeps this shape declined now that
            // the conjunct tests IsEagerLoaded rather than mere presence. Contrast
            // Array_leaf_whose_element_has_only_a_lazy_inverse_owner_navigation_goes_native, whose element's only
            // navigation is NOT eager-loaded and is therefore admitted.
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
                // The array leaf is not in the admitted set, so the whole projection declines - and NativeOnly
                // has no fallback to land on.
                Assert.Throws<NativeTranslationNotSupportedException>(() => query());
                continue;
            }

            var rows = query();

            Assert.Equal(["a_empty", "b_two"], rows.Select(r => r.Title).ToArray());
            Assert.Empty(rows[0].Posts);
            Assert.Equal(["h1", "h2"], rows[1].Posts.Select(p => p.Heading).ToArray());
        }
    }

    // Final-review finding 1: the element-navigation conjunct must NOT decline an element whose only navigation
    // is the LAZY INVERSE back-reference to its owner (OwnsMany(..., p => p.WithOwner(x => x.Owner))). Before the
    // conjunct was narrowed from "no navigations at all" to "no EAGER-LOADED navigation", this entirely ordinary
    // model silently never went native — NativeOnly threw NativeTranslationNotSupportedException.
    //
    // The eager-loaded assertion is not decoration: it is the PREMISE the narrowing rests on. Should EF Core ever
    // start auto-including an owner back-reference, this assertion fails first and names the reason, instead of
    // the shape mysteriously regressing into EF-360's shaper-build ArgumentException.
    [Fact]
    public void Array_leaf_whose_element_has_only_a_lazy_inverse_owner_navigation_goes_native()
    {
        var collection = SeedOwnerRef(nameof(Array_leaf_whose_element_has_only_a_lazy_inverse_owner_navigation_goes_native));

        using var db = CreateOwnerRefContext(collection, MongoQueryMode.NativeOnly);

        // The premise: exactly one navigation on the element, and it is NOT eager-loaded — so EF emits no
        // auto-include, hence no inner Queryable.Select, hence none of EF-360's crash mechanism applies.
        var elementNavigations = db.Model.FindEntityType(typeof(OwnerRefBlog))!
            .FindNavigation(nameof(OwnerRefBlog.Posts))!.TargetEntityType.GetNavigations().ToList();
        Assert.Equal([nameof(OwnerRefPost.Owner)], elementNavigations.Select(n => n.Name));
        Assert.All(elementNavigations, n => Assert.False(n.IsEagerLoaded));

        // NativeOnly succeeding IS the routing proof — the emitted MQL cannot distinguish the two paths.
        var results = db.Entities.AsNoTracking()
            .OrderBy(b => b.Title)
            .Select(b => new {b.Title, b.Posts})
            .ToList();

        Assert.Equal(new[] {"a_empty", "b_one", "c_two"}, results.Select(r => r.Title));
        Assert.Equal(new[] {0, 1, 2}, results.Select(r => r.Posts.Count));
        Assert.Equal(new[] {"h1"}, results[1].Posts.Select(p => p.Heading));
        Assert.Equal(new[] {"h2", "h3"}, results[2].Posts.Select(p => p.Heading));
    }

    // The values half of the same finding, on the oracle the rest of this file uses for admissible shapes: the
    // newly-native route must agree with driver-LINQ, not merely route differently.
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

    // The bare spelling on the SAME nested model works and must keep working — it takes the mixed path and
    // never reaches the native projection binder at all.
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

    // ---- Task 5: breadth — the in-scope spellings and shapes ----

    // Item 1: a named DTO / MemberInit spelling (as opposed to every test above's anonymous-type NewExpression
    // spelling) exercises NativeProjectionBinder.TryPopulateNativeProjection's MemberInitExpression branch.
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

    // Item 2a: the EF.Property spelling with an alias that EQUALS the element name — admissible under the
    // shared rule for the same reason the plain `b.Posts` spelling is, and pins that EF.Property normalizes to
    // the identical tree the plain member-access spelling does.
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

    // Item 2b: the EF.Property spelling with a RENAMED alias — the same alias-agreement decline as the plain
    // `Mine = b.Posts` spelling (already pinned by Renamed_array_alias_is_declined_and_returns_correct_data_in_
    // every_mode, not duplicated here), proven for the EF.Property form specifically: the shared admissibility
    // rule operates on the ALIAS regardless of how the leaf expression itself spells the member access.
    [Fact]
    public void EF_Property_spelling_with_a_renamed_alias_declines_and_returns_correct_data_in_every_mode()
    {
        var collection = SeedKeyed(nameof(EF_Property_spelling_with_a_renamed_alias_declines_and_returns_correct_data_in_every_mode));

        // Contents (not just counts), on BOTH the Native and DriverLinq legs — review fix round 1: a
        // counts-only oracle would not catch a decline that returned the right COUNT via the wrong ELEMENTS.
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

    // Item 3: two owned-collection array leaves in ONE projection. Contents (not just counts) of BOTH arrays
    // are asserted with DISTINCT values per array, so a swapped-alias bug (Posts values landing under the
    // Drafts alias or vice versa) would be caught.
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

    // Item 4: an array leaf alongside a count leaf over the SAME collection. DECLINES — review fix round 1.
    //
    // A count leaf's alias (e.g. "N") names no document element at all, so it fails the sibling-readability
    // requirement IsNativeArrayProjectionLeaf's own invariant extends to (NativeProjectionBinder.
    // IsWholeDocumentReadableLeaf): an array leaf's presence forces EF's own client-side "mixed" shaper the
    // instant the query ever executes via fallback (ANY entity/collection-typed leaf makes
    // ProjectionAnalyzer.CanPushDown refuse hand-off to the driver's own LINQ v3 provider — see
    // Query/AGENTS.md's arithmetic-leaf note on the identical "mixed-whole-entity-plus-computed" hazard for a
    // whole-entity leaf), and that mixed shaper reads every alias against a WHOLE, un-projected document. "N"
    // names no element there, so admitting this shape would let Native (which — unlike NativeOnly — degrades
    // to the SAME mixed path once declined, rather than throwing) crash exactly like DriverLinq. Declining at
    // the binder means the shape never even reaches native $project construction, so ALL THREE modes agree:
    // NativeOnly throws cleanly at translation time; Native and DriverLinq both fall through the ordinary
    // EF-357-documented fallback (aggregate([]), client-side fold) and return correct values.
    //
    // KNOWN COVERAGE GAP, recorded here because this is where a reader will look for it (slice-8 Task 8 mutation
    // pass, doc sweep). This test is currently the ONLY defender of the shaper-side `Route == Projection` guard
    // on the array-leaf registration.
    //
    // CORRECTED (final residuals pass, EF-322 slice 8): this comment used to say the test defends the guard only
    // "incidentally", that `Select(b => new { b, b.Posts })` (a whole-entity leaf alongside an array leaf) "is
    // the shape that guard most directly exists for", and that adding it "would make the protection direct
    // rather than incidental". MEASURED FALSE: that shape never reaches the guard at all. The whole-entity leaf
    // is a bare `ParameterExpression`, not a member access, so it fails `TryTranslateLeaf`; `TryPopulateNativeProjection`
    // returns false and `MarkNotNativelyRepresentable()` has already driven `Route` to `Fallback` by the time the
    // shaper visitor runs, so the guard is never consulted. What the shape actually does instead is raise a
    // PRE-EXISTING `NullReferenceException`, identically at the branch base `7c199e4` and at HEAD, under both
    // `Native` and `DriverLinq` (declining cleanly under `NativeOnly`) — see `Query/AGENTS.md`'s array-valued-
    // projections note for the measurement. So adding that case could NOT have made the protection "direct" — it
    // never touches the guard either way. This test remains the ONLY defender of the shaper-side
    // `Route == Projection` guard.
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

    // Item 5: an array leaf alongside an arithmetic leaf (Rank * 2) over an UNRELATED scalar field. DECLINES —
    // review fix round 1. Same sibling-readability gap as item 4 above: an arithmetic leaf's alias ("X") also
    // names no document element, so it fails IsWholeDocumentReadableLeaf identically to a count leaf.
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

    // Colliding-alias variant of item 5 (review fix round 1 — "add a test for the colliding-alias case"): the
    // computed leaf's alias, "Rank", collides with a REAL stored element name ("Rank" is itself a mapped
    // scalar property of Blog). Had the sibling-readability check been skipped instead of declining, the mixed
    // shaper's alias-addressed read of "Rank" against a whole document would have returned the RAW stored
    // Rank value, not Rank * 2 — silent wrong data, no exception, in whichever mode falls back. The
    // sibling-readability check declines this identically to the non-colliding case, so no mode can reach that
    // silent-collision outcome: it is a translate-time decline in every mode, not merely a lucky non-collision.
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

            // The seeded Rank values are 1, 2, 3 (SeedKeyedWithRank) — doubled, NOT the raw stored value, in
            // every mode. A silent-collision bug would instead read back the raw 1, 2, 3.
            Assert.Equal(new[] {2, 4, 6}, actual.Select(r => r.Rank));
            Assert.Equal(new[] {0, 1, 2}, actual.Select(r => r.PostsCount));
        }

        using var nativeOnly = CreateContext(collection, KeyedModel, MongoQueryMode.NativeOnly);
        Assert.Throws<NativeTranslationNotSupportedException>(() => Run(nativeOnly.Entities.AsNoTracking()));
    }

    // Item 6: a HashSet<Post>-backed navigation. Asserts the runtime type really IS a HashSet<Post> (proving
    // the array leaf is materialized through the navigation's own IClrCollectionAccessor rather than a
    // hand-made List<T> that merely happens to satisfy the compile-time HashSet<Post> property type).
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

        // Review fix round 1: bring contents AND the runtime-type assertion into the DriverLinq leg too — the
        // original version compared only (Title, Count) here despite the Native leg checking heading contents
        // and Assert.IsType<HashSet<Post>>, so a bug that preserved counts but lost contents or collection
        // identity on the fallback leg would have gone uncaught.
        using var driver = CreateHashSetContext(collection, MongoQueryMode.DriverLinq);
        var driverResults = driver.Entities.AsNoTracking().OrderBy(b => b.Title)
            .Select(b => new {b.Title, b.Posts})
            .ToList();
        Assert.Equal(new[] {"a_empty", "b_two"}, driverResults.Select(r => r.Title));
        Assert.Equal(new[] {0, 2}, driverResults.Select(r => r.Posts.Count));
        Assert.IsType<HashSet<Post>>(driverResults[1].Posts);
        Assert.Equal(new[] {"h1", "h2"}, driverResults[1].Posts.Select(p => p.Heading).OrderBy(h => h));
    }

    // Item 7: a TRACKING query (no AsNoTracking) with an array leaf.
    //
    // The task brief expected the exception type to differ by mode — NativeTranslationNotSupportedException
    // under NativeOnly, on the theory that the provider's own decline would fire before EF Core's owned-
    // tracking guard. MEASURED HERE, DIRECTLY, and it does NOT differ: this shape's array leaf (`b.Posts`,
    // alias "Posts" matching the element name) is fully admissible under the shared rule, so translation
    // SUCCEEDS in every mode including NativeOnly. EF Core's own owned-tracking guard
    // (ShapedQueryCompilingExpressionVisitor.StructuralTypeMaterializerInjector.Inject, invoked from
    // CompileShapedQuery identically regardless of native-vs-fallback routing) is what throws — the SAME
    // InvalidOperationException, in Native, DriverLinq, AND NativeOnly alike. The brief's premise is corrected
    // here rather than assumed; see the task report for the measurement.
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

    // Item 8: a primitive-collection leaf (Tags, a mapped primitive-collection PROPERTY — already native via
    // the plain-field branch) alongside an owned-collection array leaf, pinning that the two leaf kinds compose.
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

    // Item 9 (Task 4 review finding): the shadow-key model currently has only Native-mode MQL coverage
    // (Owned_array_leaf_projection_emits_the_owner_key) and NativeOnly materialization coverage
    // (Owned_array_leaf_with_a_shadow_key_element_goes_native) — no test asserts the DriverLinq fallback leg,
    // unlike the keyed model's own Owned_array_leaf_projection_matches_driver_linq. Added here to close that gap.
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

        // Review fix round 1: the parity assertion alone (Run(driver) == Run(native)) is vacuously
        // satisfiable — both legs run the SAME alias-addressed shaper, so it would pass even if both
        // returned the same WRONG thing. Anchor to the seeded ground truth too.
        Assert.Equal(new[] {"a_empty", "b_one", "c_two"}, nativeResult.Select(r => r.Title));
        Assert.Equal(new[] {0, 1, 2}, nativeResult.Select(r => r.Count));
        Assert.Equal(new[] {"", "h1", "h2|h3"}, nativeResult.Select(r => r.Headings));

        Assert.Equal(Run(driver), nativeResult);
    }

    // ---- Task 6: the correctness gate — ragged arrays, the differential oracle, converters ----

    // EF-358's contract, on the NEW native route: a MISSING or explicitly-BSON-null stored array materializes as
    // an EMPTY collection, never null, uniformly with every other path. The coalesce that guarantees it lives at
    // the POINT OF USE in MongoProjectionBindingRemovingExpressionVisitor's CollectionShaperExpression case
    // (`Expression.Coalesce(bsonArrayExpression, Expression.New(typeof(BsonArray)))`), and it is load-bearing on
    // THIS route specifically: the alias read resolves to null for BOTH of those stored states (an absent element
    // and an explicit BSON null both come back null through the TypeAs the injector emits — the same reason
    // BsonBinding.CreateGetBsonArray returns null for both on the document-path route).
    //
    // Two properties of this file make the assertion real rather than decorative: Blog.Posts has NO `= []`
    // initializer (a field initializer would pre-populate the navigation and hide a null read-back entirely), and
    // the five states are seeded as RAW BSON with a read-back self-check in SeedFiveStates — "missing" and
    // "present but empty" are indistinguishable in this test's RESULTS, so without that self-check a seed bug
    // could make the matrix pass while testing three states instead of five.
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

        // Contents, not just counts: a normalization that produced an empty collection for EVERY state would
        // satisfy the NotNull/count assertions for the three empty states while silently losing the populated ones.
        Assert.Equal(new[] {"h1"}, results[3].Posts.Select(p => p.Heading));
        Assert.Equal(new[] {"h2", "h3"}, results[4].Posts.Select(p => p.Heading));
    }

    // The differential oracle. The expected leg materializes WHOLE Blog entities and applies the same summariser
    // client-side; the leg under test projects `new { Title, Posts }` server-side and reads the array back through
    // the native $project ALIAS. The two legs share no array-read code at all — the oracle reads Posts by
    // DOCUMENT PATH out of a whole document (the pre-existing whole-entity route), the leg under test reads it by
    // PROJECTION ALIAS out of a reduced row (the route this slice adds) — which is precisely why the expected leg
    // must NOT be built from a second projection query: a bug in the alias read, or in the shared normalization
    // both projection modes go through, would then appear on both sides and the test would pass regardless.
    //
    // Ground truth is asserted as well as parity, so the comparison cannot be satisfied vacuously by two legs
    // that are wrong in the same way (the lesson recorded on
    // Owned_array_leaf_with_a_shadow_key_element_matches_driver_linq above).
    [Fact]
    public void Native_array_projection_equals_the_whole_entity_oracle_for_every_array_state()
    {
        var collection = SeedFiveStates(nameof(Native_array_projection_equals_the_whole_entity_oracle_for_every_array_state));

        // One summariser, applied to both legs, so the only difference between them is where the collection came
        // from. Count() (not .Count) keeps it usable for any IEnumerable<Post>.
        //
        // The "<null>" sentinel is DEFENSIVE, not load-bearing — softened here by the slice-8 doc sweep, which
        // previously presented it as doing real work ("stop a null heading being conflated with an empty one").
        // No seed in this class writes a null (or absent) Heading — PostDoc always writes one and takes a
        // non-nullable string — so the ?? branch never executes and no assertion depends on it. It is kept
        // because Post.Heading IS nullable and a future ragged seed would otherwise silently conflate null with
        // "", but it currently proves nothing. Seed a null Heading if you want it to.
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

    // The slice's own obligation, not routine coverage. A pre-implementation spike proved element converters
    // round-trip on every path that existed BEFORE this slice (whole-entity, Include, and the bare array
    // projection through the mixed shaper) but could not prove the ALIAS path, because no alias read existed yet.
    // The argument that it holds is that the new arm changes only WHERE the BsonArray comes from and leaves the
    // per-element inner shaper untouched — sound, but an inference; this test is the measurement. It therefore
    // has to run through the native alias route (NativeOnly) and not reuse a pre-existing path.
    //
    // Both stored forms are genuine BSON strings (asserted in SeedConverters). The DISCRIMINATING assertion is
    // Code's: its converter is transforming ("g:High" is not a Grade member name), so no lenient/default
    // deserialization can produce Grade.High from the stored value — only the configured converter can. Ref's is a
    // round-trip pin rather than a discriminating one, because the driver's Guid serializer reads a BSON string
    // regardless of configured representation. SeedConverters records that measurement in full; read it before
    // strengthening or weakening either assertion.
    //
    // Task 8 (mutation pass) fix: the second element's Ref used to be asserted as Guid.Empty, which is ALSO the
    // CLR default for Guid — so that line passed even if Ref was never read at all. Both elements now carry a
    // distinct NON-EMPTY Guid, so each assertion can only be satisfied by an actual read of that element's own
    // stored value. Two of them, not one, so a bug that read element 0's Ref for every element would show up too.
    [Fact]
    public void Element_converters_round_trip_through_the_alias_read()
    {
        var (collection, expectedRefA, expectedRefB) =
            SeedConverters(nameof(Element_converters_round_trip_through_the_alias_read));

        using var db = CreateConverterContext(collection, MongoQueryMode.NativeOnly);

        // ToList() before Single(), deliberately: a server-side Single() would additionally set a reducer
        // Cardinality on the same select — an unrelated axis whose interaction with the array leaf is not what
        // this test is measuring. Reducing client-side keeps the projection the only thing under test.
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

    // ---- Task 7: the set-op positions TryTranslateLeaf is also the entry point for ----
    //
    // NativeProjectionBinder.TryTranslateLeaf is the ONE shared per-leaf entry point for every site that
    // populates Select.Projection, so admitting an array leaf made it admissible in two further positions
    // nobody designed for: a projected set-op OPERAND (A.Select(p).Union(B.Select(p)) — EF-347 slice C1) and a
    // trailing projection AFTER a whole-entity set op (Union(A,B).Select(p) — slice C2). Task 7 probed both.
    // The two came out DIFFERENTLY, and the reason is WHERE the set operation's value comparison sits relative
    // to the $project:
    //
    //   * OPERAND position (C1) — DECLINED, see NativeProjectionBinder / IsPlainProjectedSelect's
    //     HasArrayProjectionLeaf conjunct. C1 emits each operand's $project BEFORE the combine, so Union's
    //     dedup ($group{_id:"$$ROOT"}) and Intersect/Except's source-tagging ($group{_id:"$_doc"}) compare the
    //     PROJECTED document by value. An array leaf forces the owner key into that document (Task 4), and the
    //     leaked _id then joins the comparison key — turning C1's established "dedup over the projected values"
    //     contract into dedup by document IDENTITY. Measured below.
    //   * TRAILING position (C2) — measured SOUND, stays native. The combine runs over WHOLE documents and the
    //     trailing $project is the last stage, so neither the array nor the owner key ever reaches the value
    //     comparison. Nothing about it is array-specific.
    //
    // KNOWN COVERAGE GAP in the C1 decline below, recorded by the slice-8 doc sweep because this is where a reader
    // will look for it: the decline is exercised on the KEYED and SHADOW-KEY element models only. It is NOT
    // exercised for the DTO/MemberInit operand spelling (even though the binder sets HasArrayProjectionLeaf on
    // that branch too), nor for the HashSet-navigation or value-converter element models. The gate itself is
    // element-model-agnostic — it reads one provenance flag — so the risk is low, but no test says so.

    // The C1 decline, ordinary shape. Native/DriverLinq return the correct rows through the fallback; NativeOnly
    // is the routing proof that the shape is genuinely declined rather than quietly going native.
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

    // THE measurement that decided Task 7, recorded as numbers rather than as an argument.
    //
    // The seed is two DISTINCT documents whose PROJECTED values are identical (same Title, same single Post),
    // differing only in Rank — which is NOT projected. So the whole question "what does this set operation
    // compare?" is decidable from the row count alone.
    //
    // Measured on a build that admitted the array leaf in the operand position (i.e. before the
    // HasArrayProjectionLeaf conjunct existed), against an otherwise-identical `new { b.Title }` control that
    // has no array leaf, under Native and NativeOnly alike — the emitted $project was
    // { Title: "$Title", Posts: "$Posts", _id: "$_id" } instead of the control's { Title: "$Title", _id: 0 }:
    //
    //     operation   control (no array leaf)   WITH array leaf admitted
    //     Union       1 row                     2 rows
    //     Intersect   1 row                     0 rows      <-- answer flipped
    //     Except      0 rows                    1 row       <-- answer flipped
    //
    // The Union row count is a visible semantic change; the Intersect/Except numbers are worse, because those
    // two have NO driver-LINQ oracle at all (the driver's LINQ v3 provider throws for a cross-view
    // Intersect/Except), so the flipped answer would have been the ONLY answer obtainable in any mode.
    //
    // On the CURRENT build the shape declines, so the fallback answers, and it answers 1 — C1's documented
    // dedup-over-projected-values semantics (NativeSetOpsTests.
    // Projected_operand_union_dedups_over_projected_values_not_whole_entities), restored by declining.
    //
    // Note what is NOT claimed: in-memory BCL LINQ answers 2 for this query and 0 for the Intersect, because an
    // anonymous type carrying a List<Post> compares that member by REFERENCE, so no two separately-materialized
    // rows are ever equal. BCL is therefore not the oracle here — it would never dedup anything — and the
    // admitted-array numbers agreeing with it is a coincidence of _id being unique, not evidence of correctness.
    // The authority used is the provider's own C1 contract, which the control column exhibits.
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
            // ONE row: the two documents' projected values are equal, so a projected-value dedup collapses them.
            Assert.Equal([("same", "h1")], Run(db));
        }

        using var nativeOnly = CreateContext(collection, KeyedModel, MongoQueryMode.NativeOnly);
        Assert.Throws<NativeTranslationNotSupportedException>(() => Run(nativeOnly));
    }

    // Concat shares the C1 operand gate with Union and declines identically, but has no dedup stage at all — so
    // this pins that the decline does not silently turn Concat into Union (or drop a row) on the fallback: both
    // twins survive.
    //
    // WHY Concat DECLINES AT ALL IS A CONSIDERED TRADE, NOT A FORCED OUTCOME — stated explicitly here by the
    // slice-8 doc sweep, because the paragraph above reads as if the mechanism required it. It does not: Concat
    // was MEASURED harmless (no dedup stage, so the leaked owner `_id` never joins any comparison key and is
    // inert for an alias-reading shaper). It is declined for UNIFORMITY, because it shares the single
    // IsPlainProjectedSelect predicate with Union, whose dedup the `_id` genuinely corrupts. Splitting that
    // predicate per set-op kind to admit Concat alone is available and was deliberately not taken — one
    // predicate, one rule, at the cost of one shape that would have been safe.
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

    // The decline's OTHER disposition, and the asymmetry is pre-existing and documented, not something this
    // slice chose: Intersect/Except have no driver-LINQ baseline, so TryTranslateSetOperation returns null for an
    // out-of-scope shape instead of marking it non-native — EF Core's own translation-failure path, i.e. the SAME
    // InvalidOperationException in Native, DriverLinq and NativeOnly alike, with no mode returning data. Per the
    // versioning rubric the exception TYPE of an unsupported shape is not contract, so a type change here is a
    // prompt to re-measure rather than a regression.
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

    // The honest full picture of what the C1 decline lands on for a SHADOW-KEY element, so a later reader does
    // not discover it as a surprise: the fallback CRASHES for this shape, in both fallback-capable modes.
    //
    // Cause, and it is not the decline: unlike a plain terminal array-leaf Select (whose fallback fetches whole
    // documents and folds client-side — hence Owned_array_leaf_with_a_shadow_key_element_matches_driver_linq
    // passing), a projected-operand set op is a shape the driver's own LINQ provider pushes SERVER-side, and it
    // emits `_id: 0`. That strips the owner key the shadow-key element shaper reads out of the row, so
    // materialization fails per row.
    //
    // UNPINNED RATIONALE (flagged by the slice-8 doc sweep, not corrected — it is believed right, just not
    // proven here): the `_id: 0` half of that explanation is NOT asserted by this test, which checks only the
    // exception message. Nothing here captures the driver's emitted MQL, so a future change to what the driver
    // pushes down would leave this paragraph stale without turning the test red. Treat it as the mechanism we
    // believe explains the observed message, and re-measure before relying on it.
    //
    // The DriverLinq leg is the evidence this is not the gate's doing: DriverLinq
    // never consults the native binder at all, so it behaved identically before this slice, when the array leaf
    // was not admissible anywhere and this shape already fell back. Declining therefore restores exactly the
    // pre-slice behaviour — crash included — rather than introducing one; the alternative (admitting the leaf)
    // returns data, but with the silently-changed set semantics measured two tests above.
    [Fact]
    public void Array_leaf_in_a_projected_union_operand_with_a_shadow_key_element_lands_on_the_pre_existing_fallback_crash()
    {
        var collection = SeedShadow(
            nameof(Array_leaf_in_a_projected_union_operand_with_a_shadow_key_element_lands_on_the_pre_existing_fallback_crash));

        static List<object> Run(SingleEntityDbContext<Blog> db)
            => db.Set<Blog>().AsNoTracking().Where(b => b.Title == "b_one")
                .Select(b => new {b.Title, b.Posts})
                .Union(db.Set<Blog>().AsNoTracking().Where(b => b.Title == "c_two")
                    .Select(b => new {b.Title, b.Posts}))
                .ToList<object>();

        foreach (var mode in new[] {MongoQueryMode.Native, MongoQueryMode.DriverLinq})
        {
            using var db = CreateContext(collection, ShadowKeyModel, mode);
            var ex = Assert.Throws<InvalidOperationException>(() => Run(db));
            Assert.Contains("Document element is missing for required non-nullable property 'Id'", ex.Message);
        }

        using var nativeOnly = CreateContext(collection, ShadowKeyModel, MongoQueryMode.NativeOnly);
        Assert.Throws<NativeTranslationNotSupportedException>(() => Run(nativeOnly));
    }

    // The C2 trailing position, which the probe found SOUND — so it keeps the array leaf, and the name states the
    // measured routing (NativeOnly succeeding) rather than merely "returns correct results". Both element-key
    // kinds are exercised: the shadow-key model is the one that needs the owner key emitted alongside the array,
    // and here it round-trips on both legs (unlike the operand position two tests above).
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
            // NativeOnly succeeding IS the routing proof — the emitted MQL cannot distinguish the two paths.
            using var nativeOnly = CreateContext(collection, model, MongoQueryMode.NativeOnly);
            var actual = Run(nativeOnly);
            Assert.Equal([("b_one", "h1"), ("c_two", "h2|h3")], actual);

            using var driver = CreateContext(collection, model, MongoQueryMode.DriverLinq);
            Assert.Equal(Run(driver), actual);
        }
    }

    // What the trailing position's dedup actually compares, measured rather than assumed — the question Task 7
    // was asked to settle, answered for the form that stayed native.
    //
    // A whole-entity Union dedups over $$ROOT of the WHOLE document, which always carries a distinct _id, so two
    // distinct documents can never collapse no matter what their arrays hold, and the array plays no part in the
    // comparison at all. Two consequences, both asserted here:
    //   * The SAME document reached from both operands DOES collapse (the b_one row below, matched by both
    //     operand filters, yields one row, not two).
    //   * Two documents differing ONLY in array state — one with NO stored Posts element, one with Posts: [] —
    //     both survive, even though after EF-358 normalization their materialized results are indistinguishable
    //     (both empty). Dedup sees the STORED representation, upstream of that normalization. This is inherent to
    //     whole-document dedup and predates this slice; it is recorded so the next reader does not have to
    //     re-derive whether an array leaf could have caused it. It cannot: the $project runs after the $group.
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
            // Both twins match BOTH operands, so each is deduped against its own duplicate — two rows out, not
            // four (dedup happened) and not one (the missing-array twin and the empty-array twin are distinct
            // documents and are NOT merged, despite materializing identically).
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

    // The trailing position over the OTHER set-op rendering — Intersect/Except lower to a source-tagging
    // pipeline rather than a plain $unionWith, and its two $group stages likewise run over whole documents
    // ahead of the trailing $project, so the array leaf composes with it too. Proven by NativeOnly succeeding
    // plus an expected result set: unlike Union/Concat, this shape has no driver-LINQ oracle to compare against
    // (the driver throws for a cross-view Intersect), which is exactly why the operand-position flip two tests
    // above would have been unobservable.
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

    // ---- Final-review finding 2: shapes this slice flipped fallback → native without recording it ----
    //
    // All three were MEASURED as declining at this branch's base (7c199e4) and succeeding at HEAD, so they are
    // genuinely part of this slice's newly-native surface rather than pre-existing capability. They are pinned
    // here because nothing else in this file covers them, and each is a single keystroke away from a shape that
    // IS covered — so a future narrowing of admissibility could drop them silently.

    // The array-only projection: no sibling leaf at all, so the sibling-readability rule is trivially satisfied
    // and the array leaf's own alias-agreement conjunct is the only thing gating it. One keystroke from
    // Owned_array_leaf_in_an_anonymous_projection_goes_native.
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

        // NativeOnly succeeding IS the routing proof — the emitted MQL cannot distinguish the two paths.
        using var nativeOnly = CreateContext(collection, KeyedModel, MongoQueryMode.NativeOnly);
        var actual = Run(nativeOnly);
        Assert.Equal(["", "h1", "h2|h3"], actual);

        using var driver = CreateContext(collection, KeyedModel, MongoQueryMode.DriverLinq);
        Assert.Equal(Run(driver), actual);
    }

    // A reducer after an array projection. The array leaf populates Projection (Route == Projection) and the
    // reducer then appends its own $limit, so both survive — this is the scalar-cardinality machinery composing
    // with an array leaf, which nothing else here exercises.
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

    // Paging after an array projection. Skip/Take reach NativeSlotPopulator's ordinary arms and append to the
    // recorded op list; the array leaf's $project is emitted last regardless, so the two compose.
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

    // ---- Final-review finding 3: a PRIMARY-KEY sibling can never satisfy the sibling-readability rule ----
    //
    // IsWholeDocumentReadableLeaf compares `alias == field.ElementName`. A document ROOT's primary key always has
    // element name "_id" (PrimaryKeyDiscoveryConvention rewrites it), while its alias is the CLR member name
    // ("Id") — so Select(b => new { b.Id, b.Title, b.Posts }) can NEVER satisfy the rule and the whole projection
    // declines, falling back. This is materially DIFFERENT from the renamed-alias declines it sits alongside:
    // those are a naming choice the user could make differently, whereas here no ORDINARY naming choice fixes it.
    //
    // CORRECTED (final residuals pass, EF-322 slice 8): the "NO naming choice fixes it" phrasing above was
    // overstated. Select(b => new { _id = b.Id, b.Title, b.Posts }) aliases the member "_id" directly, which
    // DOES satisfy `alias == field.ElementName` and is therefore admitted — NativeProjectionBinder.cs's
    // `hasArrayLeaf && seenAliases.Add("_id")` check (around line 144) prevents a duplicate owner-key projection
    // for this case, so nothing collides. This was derived by READING the code, not executed as a test — it is
    // an untested admitted case, worth covering in whichever future slice takes the principled sibling-rule fix
    // described below. The substantive point this test defends is unaffected: no ORDINARY spelling of a PK
    // sibling satisfies the rule, which is what makes it materially unlike the renamed-alias cases it sits
    // alongside.
    //
    // Deliberately NOT widened here, and this test is the tripwire for that decision. A decline returns correct
    // values via fallback, and this branch found silent-wrong-data bugs TWICE while widening admissibility in
    // exactly this area, so widening it in a final fix wave with a single re-review is the wrong risk.
    //
    // The principled fix, when it is taken: narrow the sibling rule to reject only a leaf resolving to NO property
    // (a computed leaf — a count or arithmetic expression), because a PROPERTY-BACKED leaf is resolved by the
    // mixed shaper through its IProperty rather than by alias, so the alias never has to match the element name
    // for it. That is deferred, not overlooked.
    [Fact]
    public void Primary_key_sibling_declines_and_returns_correct_data_in_every_mode()
    {
        var collection = SeedKeyed(nameof(Primary_key_sibling_declines_and_returns_correct_data_in_every_mode));

        static List<(string Title, int Count)> Run(SingleEntityDbContext<Blog> db)
            => db.Entities.AsNoTracking().OrderBy(b => b.Title)
                .Select(b => new {b.Id, b.Title, b.Posts})
                .ToList()
                .Select(r => (r.Title, r.Posts.Count))
                .ToList();

        // The premise, asserted rather than assumed: the root key's stored element name is "_id", never "Id".
        using (var probe = CreateContext(collection, KeyedModel, MongoQueryMode.Native))
        {
            var key = probe.Model.FindEntityType(typeof(Blog))!.FindPrimaryKey()!.Properties[0];
            Assert.Equal(nameof(Blog.Id), key.Name);
            Assert.Equal("_id", key.GetElementName());
        }

        using (var native = CreateContext(collection, KeyedModel, MongoQueryMode.Native))
        using (var driver = CreateContext(collection, KeyedModel, MongoQueryMode.DriverLinq))
        {
            var expected = new List<(string, int)> {("a_empty", 0), ("b_one", 1), ("c_two", 2)};
            Assert.Equal(expected, Run(native));
            Assert.Equal(expected, Run(driver));
        }

        // The decline itself. Flipping this to a success is a deliberate act, not a quiet one.
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

    // MQL-capture idiom mirrored from NativeOwnedCollectionCountTests.cs: FunctionalTests has no
    // TestMqlLoggerFactory/AssertMql (those live in the SpecificationTests project), so MQL is captured
    // through SpyLoggerProvider instead.
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

    // Seeded as RAW BSON, not through the change tracker, so the stored array state is exactly what the
    // test intends rather than whatever a round-trip would normalize it to. Ragged states (a MISSING or
    // explicitly-null Posts element) are a later task's subject; these three rows are all well-formed
    // arrays of length 0, 1 and 2.
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

    // "PostId", not "_id": PrimaryKeyDiscoveryConvention returns early for an OWNED type that already has an
    // explicit primary key, so it never rewrites the key property's element name to "_id" the way it does for
    // a document root. The stored element name is therefore just the property name.
    private static BsonDocument PostDoc(int postId, string heading)
        => new() {{"PostId", postId}, {"Heading", heading}};

    // Same document shape as SeedKeyed — a shadow key changes only how the ELEMENT'S OWN key is materialized
    // (via _ownerMappings, off the owner's _id), not what gets stored for the element itself. The stray
    // "PostId" field is harmless: with no explicit HasKey it is just an ordinary scalar property on Post,
    // never consulted as identity.
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

    // Home is seeded present on every row: it is a required owned reference, so a document missing it fails
    // materialization for reasons unrelated to the array leaf under test.
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

    // Two rows only (not three, unlike SeedKeyed/SeedNestedOwner) — the decline test needs no particular
    // array shape (it never reaches materialization), and the bare-spelling regression test only needs an
    // empty-vs-populated pair to prove ordering and counts. Comments are seeded present-but-empty on both
    // NestedPosts, not omitted: this is EF-360 territory (the element carries its own navigation), which is
    // deliberately out of scope for this slice's array-state breadth (Task 6).
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

    // ---- Final-review finding 1 helpers (lazy inverse owner navigation) ----

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

    // Same raw-BSON three-row empty/one/two shape as SeedKeyed, and the same "PostId" element name for the same
    // reason (PrimaryKeyDiscoveryConvention returns early for an owned type with an explicit key). The Owner
    // back-reference is a navigation, not a stored field, so nothing is seeded for it.
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

    // ---- Task 5 helpers ----

    // Same three-row shape as SeedKeyed, plus a seeded "Rank" field for the arithmetic-leaf item.
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

    // Same three-row shape as SeedKeyed, plus a seeded "Tags" primitive-array field for the primitive-collection
    // composition item. Deliberately varies tag-array length (1, 2, 0) independently of the Posts array length,
    // so a swapped-alias bug between Tags and Posts would show up as mismatched counts/contents.
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

    // Reuses the generic Row/PostDoc helpers (they build plain BsonDocuments, unattached to any particular
    // entity CLR type) to seed a HashSetBlog collection with the same two-row empty/populated shape as SeedNested.
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

    // ---- Task 6 helpers ----

    // The five-state matrix, seeded as RAW BSON so each stored state is EXACTLY what the test intends rather than
    // whatever a change-tracker round-trip would normalize it to (an insert through the change tracker can never
    // produce a MISSING array element at all — it always writes the navigation, so the two most interesting
    // states here are unreachable that way):
    //   a_missing — no "Posts" element in the document at all
    //   b_null    — "Posts": null (explicit BSON null)
    //   c_empty   — "Posts": []
    //   d_single  — one element
    //   e_multi   — two elements
    //
    // "Rank" and "Tags" are seeded on every row even though no Task 6 test projects them: the differential oracle
    // materializes WHOLE Blog entities, which reads every mapped property of Blog, and a missing required
    // non-nullable property fails materialization per row (BsonBinding) for reasons unrelated to the array leaf
    // under test. Their values are irrelevant and deliberately uniform.
    //
    // The read-back self-check is load-bearing, not belt-and-braces: "seeded as MISSING" and "seeded as
    // present-but-empty" are indistinguishable in the RESULTS of both Task 6 data tests (each yields an empty
    // collection), so a seed that silently wrote `Posts: []` for a_missing would leave both tests passing while
    // covering three states instead of five. Asserting against the raw document is the only way to know.
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

    // A null `posts` means OMIT the element entirely — distinct from passing BsonNull.Value, which writes an
    // explicit BSON null. Those are the two ragged states the array-leaf read has to normalize.
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

    // ---- Task 7 helpers ----

    // Two DISTINCT documents whose PROJECTED { Title, Posts } values are IDENTICAL — same Title, one element with
    // the same PostId/Heading — differing only in Rank, which no Task 7 test projects. Rank is what each operand's
    // own Where selects on, so "which document came from which operand" is expressible without projecting the
    // discriminator; the row COUNT of a set operation then reveals exactly what it compared.
    //
    // CORRECTED IN PLACE (slice-8 doc sweep): this comment used to claim "Tags is seeded (empty) for uniformity
    // with the other seeds; nothing here reads it." It is NOT seeded at all — RowWithRank writes no Tags element
    // (contrast SeedArrayStateTwins, from which the sentence was copied). Nothing here reads Tags either way, so
    // no test changes; the claim was simply false.
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

    // Two documents identical in every mapped field EXCEPT the stored array state: one has NO "Posts" element at
    // all, the other has "Posts": []. Both materialize as an empty collection (EF-358), so they are
    // indistinguishable in a result — which is precisely what makes them the right probe for "does a set
    // operation's value comparison see the stored representation or the materialized one?". Seeded as RAW BSON with
    // a read-back self-check, because "missing" and "present but empty" are otherwise unverifiable from results
    // alone (the same reasoning as SeedFiveStates').
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

    // Seeds ONE ConvBlog with two elements whose Ref and Code are stored as genuine BSON STRINGS (asserted below),
    // the intent being that a RAW read would visibly differ from a CONVERTED one.
    //
    // MEASURED, and it changes what "visibly differ" can mean for each of the two properties — recorded because
    // the obvious spelling of this test proves nothing. The driver's serializers are LENIENT ON READ for a BSON
    // STRING: GuidSerializer deserializes a String as happily as its configured Binary form, and EnumSerializer
    // accepts a member-NAME string as happily as an int. So a stored value of just `expectedRef.ToString()` /
    // `"High"` reads back correctly even with HasBsonRepresentation and a plain `v.ToString()` HasConversion
    // BOTH DELETED — verified by exactly that mutation, which left this test green. Hence:
    //   * Code's converter is TRANSFORMING ("g:" prefix), not a bare ToString. `"g:High"` is not a Grade member
    //     name, so no default/lenient path can produce Grade.High from it — only the configured converter can.
    //     This is the discriminating half, and it is mutation-verified in both directions.
    //   * Ref keeps HasBsonRepresentation(BsonType.String) per the slice's obligation, and its assertion is an
    //     honest round-trip pin (a non-default-representation element property survives the alias read intact),
    //     NOT a discriminating one — NO SEEDED FORM MAKES THE ROUND-TRIP ASSERTION DISCRIMINATE.
    //     CORRECTED IN PLACE (slice-8 doc sweep): the claim here used to be that reads are "lenient in BOTH
    //     DIRECTIONS". Right conclusion, WRONG REASON, and the wrong reason mattered because the sentence that
    //     followed it froze the reason in place. Leniency is ONE-DIRECTIONAL. GuidSerializer(BsonType.String)
    //     sets _guidRepresentation = Unspecified and THROWS on a BsonType.Binary value, while the provider
    //     default (GuidSerializer.StandardInstance) reads Binary fine — so a BINARY-seeded value DOES
    //     distinguish configured from unconfigured, as a THROW rather than as a differing value. What holds is
    //     the narrower claim: seeding String makes both configurations agree, and seeding Binary turns the
    //     difference into an exception rather than into something a round-trip equality assertion can express.
    //     So this leg remains a round-trip pin, not a proof that the representation was applied — but do not
    //     re-derive that from "lenient in both directions", which is false.
    //
    // BOTH Guids are non-empty and DISTINCT (Task 8's mutation pass). The second element originally carried
    // Guid.Empty, which is also the CLR default for Guid, so the assertion on it was vacuous — it would have
    // held even if the element's Ref had never been read. Two distinct non-default values make each assertion
    // discriminating on its own, and make a "read element 0's value for every element" bug visible too.
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
        // Asserted against the STORED documents, not just the local constants: what makes the test's Ref
        // assertions discriminating is that no element stores the default Guid, so an assertion satisfied by a
        // never-read Ref (whose CLR value would be Guid.Empty) is impossible. Checking the constants alone would
        // not catch a seed that wrote Guid.Empty into the document while returning a non-empty expectation.
        Assert.All(stored, e => Assert.NotEqual(Guid.Empty.ToString(), e.AsBsonDocument["Ref"].AsString));
        Assert.Equal(2, stored.Select(e => e.AsBsonDocument["Ref"].AsString).Distinct().Count());

        return (database.MongoDatabase.GetCollection<ConvBlog>(raw.CollectionNamespace.CollectionName),
            expectedRefA, expectedRefB);
    }
}
