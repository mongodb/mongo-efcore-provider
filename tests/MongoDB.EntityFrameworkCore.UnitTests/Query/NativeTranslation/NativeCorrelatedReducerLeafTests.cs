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

using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Query;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.Query.Expressions;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query.NativeTranslation;

/// <summary>
/// EF-449: <c>NativeProjectionBinder.TryGetCorrelatedReducerLeaf</c>'s accept/decline matrix — a
/// reference-collection navigation reduced by <c>First</c>/<c>FirstOrDefault</c> to a scalar member inside a
/// projection (<c>a.IdentificationMethods.FirstOrDefault().Method</c>).
/// </summary>
/// <remarks>
/// <para>
/// These tests drive the query through the REAL <see cref="IQueryTranslationPreprocessor"/> (EF Core's
/// nav-expansion) before handing it to the QMTEV, which is essential rather than incidental: nav-expansion
/// ERASES the navigation member access and hoists the reduced member into an inner <c>Select</c>, so the tree the
/// recognizer actually sees bears no resemblance to the LINQ written here. A harness that skipped preprocessing
/// (as <c>SlotPopulationTests.TranslateToMongoQuery</c> deliberately does) would feed the recognizer a shape it
/// is not written for and could only ever produce false declines.
/// </para>
/// <para>
/// Every assertion is on the STAGED ARTIFACTS — the <c>$lookup</c>'s rendered sub-pipeline, the
/// <see cref="MongoCorrelatedReducerLeaf"/> record, and the projection's own
/// <see cref="MongoElementRefExpression"/> path — not merely on a native/fallback boolean, so a recognizer that
/// admitted the shape but built the wrong pipeline would fail here.
/// </para>
/// </remarks>
public class NativeCorrelatedReducerLeafTests
{
    // ── Model: a genuine cross-collection (NON-owned) one-to-many, plus the decline fixtures ────────────────

    private class Animal
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";

        // Deliberately shares its name with IdentificationMethod.Rank. This is what makes the
        // outer-parameter guard load-bearing rather than redundant: `m.Rank > a.Rank` is a comparison a
        // single-scope translator over the TARGET type happily renders as $expr over "$Rank" TWICE, silently
        // comparing the looked-up element to itself. See Correlated_field_to_field_predicate_declines.
        public int Rank { get; set; }

        public List<IdentificationMethod> IdentificationMethods { get; set; } = null!;
        public List<Sighting> Sightings { get; set; } = null!;
        public List<OwnedTag> Tags { get; set; } = null!;
    }

    /// <summary>
    /// The reduced element type's enum member — the exact member KIND the motivating spec test
    /// (<c>BuiltInDataTypesMongoTest.Can_read_back_mapped_enum_from_collection_first_or_default</c>) reduces to.
    /// </summary>
    private enum IdentificationKind
    {
        Unknown = 0,
        Implant = 1,
        Visual = 2
    }

    /// <summary>The reduced element type: a separate collection, correlated by <c>AnimalId</c>.</summary>
    private class IdentificationMethod
    {
        public ObjectId Id { get; set; }
        public string Method { get; set; } = "";
        public int Rank { get; set; }

        /// <summary>
        /// The ALREADY-NULLABLE value-type member. Exists so a user-WRITTEN narrowing cast
        /// (<c>(int)nav.FirstOrDefault()!.NullableRank</c>) can be built — see
        /// <c>User_written_narrowing_cast_over_an_already_nullable_member_declines</c>.
        /// </summary>
        public int? NullableRank { get; set; }

        public IdentificationKind Kind { get; set; }

        /// <summary>
        /// The SAME enum, but mapped with a NON-DEFAULT <c>BsonRepresentation</c> (stored as a string). Exists so
        /// the serialization gates on the reduced MEMBER and on the SORT KEY can each be exercised against a
        /// property whose STORED order and STORED value both diverge from the CLR ones — see
        /// <c>Non_default_represented_reduced_member_declines</c> /
        /// <c>Non_default_represented_sort_key_declines</c>.
        /// </summary>
        public IdentificationKind StoredKind { get; set; }

        public ObjectId AnimalId { get; set; }
        public Animal Animal { get; set; } = null!;

        // The two-hop / non-scalar-member decline fixture.
        public Detail Detail { get; set; } = null!;
        public ObjectId DetailId { get; set; }
    }

    private class Detail
    {
        public ObjectId Id { get; set; }
        public string Note { get; set; } = "";
        public int Ordinal { get; set; }
    }

    /// <summary>A SECOND reference collection nav, so the two-leaf accept case uses two distinct lookups.</summary>
    private class Sighting
    {
        public ObjectId Id { get; set; }
        public string Place { get; set; } = "";
        public ObjectId AnimalId { get; set; }
        public Animal Animal { get; set; } = null!;
    }

    /// <summary>The owned/embedded-collection decline fixture.</summary>
    private class OwnedTag
    {
        public string Label { get; set; } = "";
        public int Weight { get; set; }
    }

    private static readonly Action<ModelBuilder> Model = mb =>
    {
        mb.Entity<Animal>().HasMany(a => a.IdentificationMethods).WithOne(m => m.Animal)
            .HasForeignKey(m => m.AnimalId);
        mb.Entity<Animal>().HasMany(a => a.Sightings).WithOne(s => s.Animal).HasForeignKey(s => s.AnimalId);
        mb.Entity<Animal>().OwnsMany(a => a.Tags);
        mb.Entity<IdentificationMethod>().HasOne(m => m.Detail).WithMany().HasForeignKey(m => m.DetailId);
        mb.Entity<IdentificationMethod>().Property(m => m.StoredKind).HasBsonRepresentation(BsonType.String);
        mb.Entity<Detail>();
    };

    // ── TPH model: the target of the reduced navigation is a DERIVED type ───────────────────────────────────

    private class TphOwner
    {
        public ObjectId Id { get; set; }
        public List<TphDerivedChild> Children { get; set; } = null!;
    }

    private abstract class TphChildBase
    {
        public ObjectId Id { get; set; }
        public string Label { get; set; } = "";
        public int Ordinal { get; set; }
    }

    private class TphDerivedChild : TphChildBase
    {
        public ObjectId OwnerId { get; set; }
        public TphOwner Owner { get; set; } = null!;
    }

    private static readonly Action<ModelBuilder> TphModel = mb =>
    {
        // Both base and derived share one collection with a discriminator, so TphDerivedChild is a
        // TPH-derived (non-root) target — which is exactly what the recognizer must decline.
        mb.Entity<TphChildBase>().HasDiscriminator<string>("_t")
            .HasValue<TphDerivedChild>(nameof(TphDerivedChild));
        mb.Entity<TphOwner>().HasMany(o => o.Children).WithOne(c => c.Owner).HasForeignKey(c => c.OwnerId);
    };

    // ── Harness ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs <paramref name="buildQuery"/> through the real preprocessor (EF Core's nav-expansion), pulls the
    /// resulting terminal <c>Select</c>'s selector lambda out of the preprocessed tree, and hands that — the
    /// genuine nav-expanded shape the recognizer sees in production — straight to
    /// <see cref="NativeProjectionBinder.TryPopulateNativeProjection"/>.
    /// </summary>
    /// <remarks>
    /// Stopping at the projection binder rather than driving the whole QMTEV is deliberate and necessary: this
    /// leaf's READ side (the shaper/bind side, <c>MongoProjectionBindingExpressionVisitor</c>) is a LATER task in
    /// this plan and today throws "The LINQ expression 'DbSet&lt;IdentificationMethod&gt;()' could not be
    /// translated" for every one of these shapes, accepted or declined — so a full-QMTEV harness could not
    /// distinguish the two at all. Everything below the bind side (nav-expansion, the recognizer, the staged
    /// <c>$lookup</c> and its sub-pipeline) is exercised for real.
    /// </remarks>
    private static (bool Accepted, MongoQueryExpression Query) Bind<TRoot>(
        Func<IQueryable<TRoot>, IQueryable> buildQuery, Action<ModelBuilder> model)
        where TRoot : class
    {
        var (accepted, query, _) = BindCore(buildQuery, model);
        return (accepted, query);
    }

    private static (bool Accepted, Expression Selector) BindAndCapture(Func<IQueryable<Animal>, IQueryable> buildQuery)
    {
        var (accepted, _, selector) = BindCore(buildQuery, Model);
        return (accepted, selector);
    }

    private static (bool Accepted, MongoQueryExpression Query, Expression Selector) BindCore<TRoot>(
        Func<IQueryable<TRoot>, IQueryable> buildQuery, Action<ModelBuilder> model)
        where TRoot : class
    {
        using var db = new TestDbContext<TRoot>(model);

        var compilationContext = db.GetService<IQueryCompilationContextFactory>().Create(async: false);
        var preprocessor = db.GetService<IQueryTranslationPreprocessorFactory>().Create(compilationContext);

        var entityType = db.Model.FindEntityType(typeof(TRoot))!;
        var root = new RootExpressionQueryable<TRoot>(new EntityQueryRootExpression(entityType));

        // EF's query compiler funcletizes captured variables into query PARAMETERS before the preprocessor
        // ever runs; that step lives in QueryCompiler, not in any service this harness can resolve, so it is
        // emulated here. Without it a captured `threshold` would reach the recognizer as a raw closure-field
        // access, which the translator declines for an unrelated reason — and the parameterized-predicate
        // decline test would pass while testing nothing. See Parameterized_predicate_declines, which asserts
        // the substitution actually happened.
        var preprocessed = ClosureCaptureParameterizer.Parameterize(
            preprocessor.Process(buildQuery(root).Expression));

        // The preprocessed tree is `DbSet<TRoot>().Select(<selector>)`; the selector is what the QMTEV hands to
        // NativeProjectionBinder. Asserted rather than pattern-matched leniently, so a future EF version that
        // reshapes the outer call fails loudly here instead of silently testing nothing.
        var select = Assert.IsAssignableFrom<MethodCallExpression>(preprocessed);
        Assert.Equal(nameof(Queryable.Select), select.Method.Name);
        var selector = Assert.IsAssignableFrom<LambdaExpression>(select.Arguments[1].UnwrapLambdaFromQuote());

        var mongoQ = new MongoQueryExpression(entityType);
        return (NativeProjectionBinder.TryPopulateNativeProjection(mongoQ, selector), mongoQ, selector);
    }

    private static (bool Accepted, MongoQueryExpression Query) BindAnimals(Func<IQueryable<Animal>, IQueryable> buildQuery)
        => Bind(buildQuery, Model);

    /// <summary>Binds and asserts the projection was ADMITTED, returning the query for further assertions.</summary>
    private static MongoQueryExpression BindAccepted(Func<IQueryable<Animal>, IQueryable> buildQuery)
    {
        var (accepted, mongoQ) = BindAnimals(buildQuery);
        Assert.True(accepted);
        return mongoQ;
    }

    /// <summary>The <c>$lookup</c> sub-pipeline this leaf staged, rendered to BSON exactly as it will be emitted.</summary>
    private static List<BsonDocument> SubPipelineOf(MongoCorrelatedReducerLeaf leaf)
        => leaf.Lookup.PipelineStages;

    // ── ACCEPT cases ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Bare_FirstOrDefault_member_is_recognized()
    {
        var mongoQ = BindAccepted(q =>
            q.Select(a => new { a.Id, M = a.IdentificationMethods.FirstOrDefault()!.Method }));

        var leaf = Assert.Single(mongoQ.CorrelatedReducerLeaves);
        Assert.Equal("M", leaf.Alias);
        Assert.Equal("Method", leaf.Member);
        Assert.False(leaf.ThrowOnEmpty);
        Assert.Equal(LookupPipelineKind.CorrelatedReducer, leaf.Lookup.PipelineKind);
        Assert.Equal("_lookup_IdentificationMethods", leaf.Lookup.As);
        Assert.Equal("_id", leaf.Lookup.LocalField);
        Assert.Equal("AnimalId", leaf.Lookup.ForeignField);

        // A bare reduction stages ONLY the $limit — no $match, no $sort.
        Assert.Equal([new BsonDocument("$limit", 1)], SubPipelineOf(leaf));

        // The $lookup is registered for emission, and the projected value reads the member off the unwound field.
        Assert.Contains(leaf.Lookup, mongoQ.GetPendingLookups());
        var projection = Assert.Single(mongoQ.Select.Projection, p => p.Alias == "M");
        Assert.Equal(
            "_lookup_IdentificationMethods.Method",
            Assert.IsType<MongoElementRefExpression>(projection.Expression).Path);
    }

    /// <summary>
    /// Reduces to <c>Rank</c> (a non-nullable <c>int</c>), not <c>Method</c> (a <c>string</c>) — see
    /// <see cref="First_over_nullable_typed_member_declines"/> for why a <c>First()</c> reducing to a NULLABLE-
    /// typed member (value-type <c>Nullable&lt;T&gt;</c> or any reference type) must decline instead of being
    /// admitted with <c>ThrowOnEmpty</c> set (EF-449 fix): the read side's throw-on-empty check can only tell
    /// "this leaf's alias is missing/null", which is genuinely ambiguous between "no related row" and "a related
    /// row was found whose own member is null/absent" for a nullable member type.
    /// </summary>
    [Fact]
    public void First_sets_ThrowOnEmpty()
    {
        var mongoQ = BindAccepted(q =>
            q.Select(a => new { a.Id, M = a.IdentificationMethods.First().Rank }));

        var leaf = Assert.Single(mongoQ.CorrelatedReducerLeaves);
        Assert.True(leaf.ThrowOnEmpty);

        // The join shape is IDENTICAL to FirstOrDefault's — the distinction is read-side only.
        Assert.Equal([new BsonDocument("$limit", 1)], SubPipelineOf(leaf));
    }

    [Fact]
    public void Constant_predicate_is_staged_as_a_match_before_the_limit()
    {
        var mongoQ = BindAccepted(q =>
            q.Select(a => new { a.Id, M = a.IdentificationMethods.FirstOrDefault(m => m.Rank > 3)!.Method }));

        var leaf = Assert.Single(mongoQ.CorrelatedReducerLeaves);
        Assert.Equal(
        [
            new BsonDocument("$match", new BsonDocument("Rank", new BsonDocument("$gt", 3))),
            new BsonDocument("$limit", 1)
        ], SubPipelineOf(leaf));
    }

    [Fact]
    public void OrderBy_is_staged_as_a_sort_before_the_limit()
    {
        var mongoQ = BindAccepted(q =>
            q.Select(a => new
            {
                a.Id, M = a.IdentificationMethods.OrderBy(m => m.Rank).FirstOrDefault()!.Method
            }));

        var leaf = Assert.Single(mongoQ.CorrelatedReducerLeaves);
        Assert.Equal(
        [
            new BsonDocument("$sort", new BsonDocument("Rank", 1)),
            new BsonDocument("$limit", 1)
        ], SubPipelineOf(leaf));
    }

    [Fact]
    public void OrderByDescending_sorts_descending()
    {
        var mongoQ = BindAccepted(q =>
            q.Select(a => new
            {
                a.Id, M = a.IdentificationMethods.OrderByDescending(m => m.Rank).FirstOrDefault()!.Method
            }));

        var leaf = Assert.Single(mongoQ.CorrelatedReducerLeaves);
        Assert.Equal(
        [
            new BsonDocument("$sort", new BsonDocument("Rank", -1)),
            new BsonDocument("$limit", 1)
        ], SubPipelineOf(leaf));
    }

    /// <summary>
    /// The full V1 shape: sort AND a constant predicate. Pins the stage ORDER ($match, then $sort, then $limit) —
    /// note this deliberately differs from the order the two layers appear in the nav-expanded tree, where the
    /// predicate <c>Where</c> sits OUTSIDE the <c>OrderBy</c>.
    /// </summary>
    [Fact]
    public void OrderBy_plus_constant_predicate_stages_match_then_sort_then_limit()
    {
        var mongoQ = BindAccepted(q =>
            q.Select(a => new
            {
                a.Id,
                M = a.IdentificationMethods.OrderByDescending(m => m.Rank).FirstOrDefault(m => m.Rank > 3)!.Method
            }));

        var leaf = Assert.Single(mongoQ.CorrelatedReducerLeaves);
        Assert.Equal(
        [
            new BsonDocument("$match", new BsonDocument("Rank", new BsonDocument("$gt", 3))),
            new BsonDocument("$sort", new BsonDocument("Rank", -1)),
            new BsonDocument("$limit", 1)
        ], SubPipelineOf(leaf));
    }

    /// <summary>
    /// Two reducer leaves over DIFFERENT navigations both go native, each with its own <c>$lookup</c> alias — the
    /// case the same-navigation collision guard must NOT be catching.
    /// </summary>
    [Fact]
    public void Two_leaves_over_different_navigations_are_both_recognized()
    {
        var mongoQ = BindAccepted(q =>
            q.Select(a => new
            {
                a.Id,
                M = a.IdentificationMethods.FirstOrDefault()!.Method,
                P = a.Sightings.FirstOrDefault()!.Place
            }));

        Assert.Equal(2, mongoQ.CorrelatedReducerLeaves.Count);
        Assert.Equal(
            ["_lookup_IdentificationMethods", "_lookup_Sightings"],
            mongoQ.CorrelatedReducerLeaves.Select(l => l.Lookup.As).Order().ToArray());
        Assert.Equal(["M", "P"], mongoQ.CorrelatedReducerLeaves.Select(l => l.Alias).Order().ToArray());
    }

    // ── DECLINE cases ───────────────────────────────────────────────────────────────────────────────────────
    //
    // Every decline asserts BOTH halves: TryPopulateNativeProjection returned false AND nothing was staged —
    // no reducer leaf, no CorrelatedReducer $lookup, no projection entries. Asserting only the return value
    // would stay green for a recognizer that half-registered a lookup on its way out.

    private static void AssertDeclined(Func<IQueryable<Animal>, IQueryable> buildQuery)
        => AssertDeclined(BindAnimals(buildQuery));

    private static void AssertDeclined((bool Accepted, MongoQueryExpression Query) bound)
    {
        Assert.False(bound.Accepted);
        Assert.Empty(bound.Query.CorrelatedReducerLeaves);
        Assert.DoesNotContain(
            bound.Query.GetPendingLookups(), l => l.PipelineKind == LookupPipelineKind.CorrelatedReducer);
        Assert.Empty(bound.Query.Select.Projection);
    }

    [Fact]
    public void Parameterized_predicate_declines()
    {
        var threshold = 3;

        AssertDeclined(q => q.Select(a => new
        {
            a.Id, M = a.IdentificationMethods.FirstOrDefault(m => m.Rank > threshold)!.Method
        }));
    }

    /// <summary>
    /// Guards the harness itself: if <see cref="ClosureCaptureParameterizer"/> ever stopped substituting, the
    /// captured value would arrive as a raw closure read that the translator declines for an unrelated reason,
    /// and <see cref="Parameterized_predicate_declines"/> would silently stop testing the parameter gate.
    /// </summary>
    [Fact]
    public void Harness_really_turns_a_captured_local_into_an_EF_query_parameter()
    {
        var threshold = 3;

        var (_, tree) = BindAndCapture(q => q.Select(a => new
        {
            a.Id, M = a.IdentificationMethods.FirstOrDefault(m => m.Rank > threshold)!.Method
        }));

        Assert.True(ClosureCaptureParameterizer.ContainsQueryParameter(tree));
    }

    /// <summary>
    /// The discriminating control for <see cref="Parameterized_predicate_declines"/>: the two queries here differ
    /// in NOTHING but constant-vs-captured, and they land on opposite sides of the gate. Without this pairing,
    /// the decline test alone could not distinguish "declined because of the parameter" from "declined because a
    /// predicate layer is present at all".
    /// </summary>
    [Fact]
    public void Parameterized_and_constant_predicates_differ_only_in_the_captured_value()
    {
        var threshold = 3;

        var parameterized = BindAnimals(q => q.Select(a => new
        {
            a.Id, M = a.IdentificationMethods.FirstOrDefault(m => m.Rank > threshold)!.Method
        }));
        var constant = BindAnimals(q => q.Select(a => new
        {
            a.Id, M = a.IdentificationMethods.FirstOrDefault(m => m.Rank > 3)!.Method
        }));

        Assert.False(parameterized.Accepted);
        Assert.True(constant.Accepted);
    }

    /// <summary>
    /// EF-449 fix: <c>First()</c> reducing to a NULLABLE-typed member (<c>Method</c> is a <c>string</c>) declines
    /// entirely, rather than being admitted with <c>ThrowOnEmpty</c> set — the read side's alias-only
    /// presence/null check cannot distinguish "no related row" from "a related row was found whose own Method
    /// happens to be null/absent" once the member itself can legitimately be null.
    /// </summary>
    [Fact]
    public void First_over_nullable_typed_member_declines()
        => AssertDeclined(q => q.Select(a => new { a.Id, M = a.IdentificationMethods.First().Method }));

    /// <summary>
    /// The discriminating control for <see cref="First_over_nullable_typed_member_declines"/>:
    /// <c>FirstOrDefault()</c> over the exact same nullable member is unaffected — it never throws on empty, so a
    /// missing/null alias reading back as a CLR default is always correct for it, nullable member or not.
    /// </summary>
    [Fact]
    public void FirstOrDefault_over_nullable_typed_member_is_still_admitted()
    {
        var mongoQ = BindAccepted(q =>
            q.Select(a => new { a.Id, M = a.IdentificationMethods.FirstOrDefault()!.Method }));

        var leaf = Assert.Single(mongoQ.CorrelatedReducerLeaves);
        Assert.False(leaf.ThrowOnEmpty);
    }

    [Fact]
    public void Two_hop_navigation_chain_declines()
        => AssertDeclined(q => q.Select(a => new { a.Id, M = a.IdentificationMethods.FirstOrDefault()!.Detail.Note }));

    [Fact]
    public void Non_scalar_reduced_member_declines()
        => AssertDeclined(q => q.Select(a => new { a.Id, M = a.IdentificationMethods.FirstOrDefault()!.Detail }));

    /// <summary>
    /// No member read at all — <c>nav.FirstOrDefault()</c> reduces to a whole ENTITY, which this leaf kind does
    /// not shape (the mandatory inner member <c>Select</c> is absent from the nav-expanded tree).
    /// </summary>
    [Fact]
    public void Whole_element_reduction_with_no_member_read_declines()
        => AssertDeclined(q => q.Select(a => new { a.Id, M = a.IdentificationMethods.FirstOrDefault() }));

    /// <summary>
    /// An OWNED (embedded) collection navigation. Handled by the pre-existing owned-collection machinery, not
    /// this recognizer — nav-expansion leaves it as an <c>EF.Property(...).AsQueryable()</c> subquery with no
    /// <see cref="EntityQueryRootExpression"/> and no FK correlation at all.
    /// </summary>
    [Fact]
    public void Owned_collection_navigation_declines()
        => AssertDeclined(q => q.Select(a => new { a.Id, M = a.Tags.FirstOrDefault()!.Label }));

    /// <summary>
    /// A TPH-DERIVED target navigation. Declined by the recognizer's own metadata gate, which runs BEFORE the
    /// <see cref="LookupExpression"/> is ever constructed.
    /// <para>
    /// There is a SECOND, structural guard behind it (EF-449 final review, I2): immediately after construction the
    /// recognizer asserts the constructor left <see cref="LookupExpression.PipelineStages"/> EMPTY, because for a
    /// TPH-derived target the constructor prepends a discriminator <c>$match</c> and claims
    /// <see cref="LookupPipelineKind.FallbackOnly"/>, which the object initializer then silently overwrites back
    /// to <see cref="LookupPipelineKind.CorrelatedReducer"/>. The two agree rather than one making the other
    /// moot — the metadata gate keeps the decline cheap and legible, the structural check makes the exclusion a
    /// property of the code rather than of statement ordering. This test exercises the EARLIER (metadata) gate;
    /// the structural check is deliberately unreachable while that gate stands, so it has no test of its own — it
    /// is a fail-closed backstop for a future edit that widens or reorders the gate.
    /// </para>
    /// </summary>
    [Fact]
    public void TPH_derived_target_declines()
        => AssertDeclined(
            Bind<TphOwner>(q => q.Select(o => new { o.Id, L = o.Children.FirstOrDefault()!.Label }), TphModel));

    /// <summary>
    /// A BARE selector body. Declined deliberately (the arm is gated to wrapped bodies), because a bare body's
    /// alias is DERIVED from the translated leaf and neither derivation tier can honour a
    /// <c>_lookup_&lt;Nav&gt;.&lt;Member&gt;</c> path. Honest note, from mutation testing: this test does NOT
    /// discriminate the arm's own <c>allowWholeRootEntityLeaf</c> conjunct — removing it leaves the decline
    /// intact, because the alias derivation refuses the leaf anyway. It pins the BEHAVIOUR, which is what a
    /// future widening of either alias tier would break.
    /// </summary>
    [Fact]
    public void Bare_selector_body_declines()
        => AssertDeclined(q => q.Select(a => a.IdentificationMethods.FirstOrDefault()!.Method));

    /// <summary>
    /// A predicate correlated back OUT to the enclosing animal. The sub-pipeline runs in the FOREIGN collection's
    /// scope with no access to the local document, and a single-scope translator over the target type would
    /// silently resolve the outer <c>Name</c> against a same-named member of the TARGET — wrong data, not a
    /// decline — so the outer-parameter guard must catch it first.
    /// </summary>
    [Fact]
    public void Predicate_correlated_to_the_outer_entity_declines()
        => AssertDeclined(q => q.Select(a => new
        {
            a.Id, M = a.IdentificationMethods.FirstOrDefault(m => m.Method == a.Name)!.Method
        }));

    /// <summary>
    /// The correlated case that actually has TEETH: <c>m.Rank &gt; a.Rank</c> compares two members that exist,
    /// with the same name, on BOTH entity types. A single-scope translator over the target type resolves both
    /// sides to the TARGET's own <c>Rank</c> and renders <c>$expr: {$gt: ["$Rank", "$Rank"]}</c> — silently wrong
    /// rows, not a decline — so only the outer-parameter identity guard stops it. (The
    /// <see cref="Predicate_correlated_to_the_outer_entity_declines"/> case above happens to be caught by the
    /// translator too, because <c>Animal.Name</c> has no counterpart on the target; this one is not.)
    /// </summary>
    [Fact]
    public void Correlated_field_to_field_predicate_declines()
        => AssertDeclined(q => q.Select(a => new
        {
            a.Id, M = a.IdentificationMethods.FirstOrDefault(m => m.Rank > a.Rank)!.Method
        }));

    /// <summary>
    /// Two reducer leaves over the SAME navigation with DIFFERENT sub-pipelines. Both want
    /// <c>_lookup_IdentificationMethods</c>, and <c>AddLookup</c> dedupes by that alias, so admitting the pair
    /// would silently drop the second's <c>$sort</c> and make it read the first's row. Declines the whole
    /// projection instead.
    /// </summary>
    [Fact]
    public void Two_leaves_over_the_same_navigation_decline()
        => AssertDeclined(q => q.Select(a => new
        {
            a.Id,
            First = a.IdentificationMethods.FirstOrDefault()!.Method,
            Best = a.IdentificationMethods.OrderByDescending(m => m.Rank).FirstOrDefault()!.Method
        }));

    /// <summary>
    /// A reducer leaf and a projected-<c>Count</c> leaf over the SAME navigation, reducer FIRST. Both stage a
    /// <c>$lookup</c> aliased <c>_lookup_IdentificationMethods</c>, but with INCOMPATIBLE shapes: the reducer's
    /// carries a <c>$limit: 1</c> sub-pipeline and is unwound to a single DOCUMENT, while the count's is a plain
    /// whole-ARRAY lookup its <c>{$size: ...}</c> reads. <c>AddLookup</c> dedupes by alias and keeps whichever
    /// was registered first, so admitting the pair would leave one of the two leaves reading the wrong shape
    /// (a <c>$size</c> over a single document is a server error; a <c>_lookup_Nav.Member</c> path over an array
    /// silently reads nothing). Declines the whole projection instead.
    /// </summary>
    [Fact]
    public void Reducer_leaf_then_count_leaf_over_the_same_navigation_decline()
        => AssertDeclined(q => q.Select(a => new
        {
            a.Id,
            M = a.IdentificationMethods.FirstOrDefault()!.Method,
            N = a.IdentificationMethods.Count
        }));

    /// <summary>
    /// The MIRROR ordering of <see cref="Reducer_leaf_then_count_leaf_over_the_same_navigation_decline"/> —
    /// count leaf FIRST. Left-to-right member processing means a different one of the two registration sites
    /// sees the collision, so both orderings must be covered for the guard to be genuinely symmetric.
    /// </summary>
    [Fact]
    public void Count_leaf_then_reducer_leaf_over_the_same_navigation_decline()
        => AssertDeclined(q => q.Select(a => new
        {
            a.Id,
            N = a.IdentificationMethods.Count,
            M = a.IdentificationMethods.FirstOrDefault()!.Method
        }));

    /// <summary>
    /// The discriminating control for the two collision tests above: a reducer leaf and a count leaf over
    /// DIFFERENT navigations stage two distinct aliases and are admitted together. Without this pairing the
    /// collision tests could not distinguish "declined because the aliases collide" from "declined because a
    /// reducer leaf and a count leaf can never coexist".
    /// </summary>
    [Fact]
    public void Reducer_leaf_and_count_leaf_over_different_navigations_are_admitted()
    {
        var mongoQ = BindAccepted(q => q.Select(a => new
        {
            a.Id,
            M = a.IdentificationMethods.FirstOrDefault()!.Method,
            N = a.Sightings.Count
        }));

        var leaf = Assert.Single(mongoQ.CorrelatedReducerLeaves);
        Assert.Equal("M", leaf.Alias);
        Assert.Equal("_lookup_IdentificationMethods", leaf.Lookup.As);
        Assert.Contains(mongoQ.GetPendingLookups(), l => l.As == "_lookup_Sightings");
    }

    // ── Serialization gates: the reduced MEMBER and the SORT KEY must both be default-serialized ────────────
    //
    // Both gates call NativeGroupByBinder.HasDefaultKeySerialization, for two DIFFERENT failure modes:
    //
    //   * the reduced MEMBER is read back off the $project alias with no backing IProperty to route a value
    //     converter / non-default BsonRepresentation through, so it would materialize the RAW STORED value; and
    //   * the SORT KEY orders by the STORED representation, which need not agree with the CLR order real LINQ
    //     sorts by (this enum stored as a string sorts "Implant" < "Unknown" < "Visual", i.e. 1 < 0 < 2), so a
    //     DIFFERENT element would be picked as "first" and the returned scalar would be silently wrong.
    //
    // Each is paired with the same-shaped default-serialized twin, so neither decline can be confused with
    // "any reduction over an enum declines" / "any sort key declines".

    /// <summary>
    /// The reduced MEMBER carries a non-default <c>BsonRepresentation</c> (enum-as-string) — declined, because the
    /// alias read has no <c>IProperty</c> to apply the representation through and would yield the raw stored
    /// string.
    /// </summary>
    [Fact]
    public void Non_default_represented_reduced_member_declines()
        => AssertDeclined(q => q.Select(a => new
        {
            a.Id, K = a.IdentificationMethods.FirstOrDefault()!.StoredKind
        }));

    /// <summary>
    /// The SORT KEY carries a non-default <c>BsonRepresentation</c> — declined (EF-449 final review, I1). Before
    /// this gate the leaf was admitted and emitted <c>$sort: { StoredKind: 1 }</c>, ordering by the stored STRING
    /// while real LINQ orders by the enum's CLR value, so a different element could be picked as "first" and the
    /// returned scalar was silently wrong. Note the reduced member here (<c>Method</c>) is perfectly
    /// default-serialized, so the pre-existing member gate cannot be what fires.
    /// </summary>
    [Fact]
    public void Non_default_represented_sort_key_declines()
        => AssertDeclined(q => q.Select(a => new
        {
            a.Id, M = a.IdentificationMethods.OrderBy(m => m.StoredKind).FirstOrDefault()!.Method
        }));

    /// <summary>The descending spelling of the sort-key gate — same decline, since the gate is order-agnostic.</summary>
    [Fact]
    public void Non_default_represented_descending_sort_key_declines()
        => AssertDeclined(q => q.Select(a => new
        {
            a.Id, M = a.IdentificationMethods.OrderByDescending(m => m.StoredKind).FirstOrDefault()!.Method
        }));

    /// <summary>
    /// The discriminating control for BOTH gates above: the DEFAULT-serialized twin of the very same enum
    /// (<c>Kind</c>) is admitted in both positions — reduced member and sort key — with the ordinary
    /// <c>$sort</c>/<c>$limit</c> sub-pipeline. The pairs differ in nothing but which enum property is named.
    /// </summary>
    [Fact]
    public void Default_serialized_enum_is_admitted_as_both_the_reduced_member_and_the_sort_key()
    {
        var asMember = BindAccepted(q => q.Select(a => new
        {
            a.Id, K = a.IdentificationMethods.FirstOrDefault()!.Kind
        }));
        Assert.Equal("Kind", Assert.Single(asMember.CorrelatedReducerLeaves).Member);

        var asSortKey = BindAccepted(q => q.Select(a => new
        {
            a.Id, M = a.IdentificationMethods.OrderBy(m => m.Kind).FirstOrDefault()!.Method
        }));
        Assert.Equal(
        [
            new BsonDocument("$sort", new BsonDocument("Kind", 1)),
            new BsonDocument("$limit", 1)
        ], SubPipelineOf(Assert.Single(asSortKey.CorrelatedReducerLeaves)));
    }

    // ── Scope boundary: ONE sort key only, no paging inside the nav chain ───────────────────────────────────
    //
    // V1 scope is a single OrderBy/OrderByDescending key. These shapes decline BY CONSTRUCTION (a ThenBy /
    // Skip / Take node matches neither the OrderBy name check nor the FK-Where match the chain walk requires),
    // but the boundary is scope, not an accident of the walk, so it is pinned here.

    [Fact]
    public void Chained_ThenBy_sort_key_declines()
        => AssertDeclined(q => q.Select(a => new
        {
            a.Id,
            M = a.IdentificationMethods.OrderBy(m => m.Rank).ThenBy(m => m.Method).FirstOrDefault()!.Method
        }));

    [Fact]
    public void Skip_inside_the_navigation_chain_declines()
        => AssertDeclined(q => q.Select(a => new
        {
            a.Id,
            M = a.IdentificationMethods.OrderBy(m => m.Rank).Skip(1).FirstOrDefault()!.Method
        }));

    [Fact]
    public void Take_inside_the_navigation_chain_declines()
        => AssertDeclined(q => q.Select(a => new
        {
            a.Id,
            M = a.IdentificationMethods.OrderBy(m => m.Rank).Take(2).FirstOrDefault()!.Method
        }));

    // ── The NULLABLE-WIDENED FirstOrDefault() shape (EF-449 fix 2) ──────────────────────────────────────────
    //
    // For a FirstOrDefault() reducing to a NON-NULLABLE VALUE-TYPE member (an enum, int, bool, DateTime …) EF's
    // nav-expansion widens the reduced member to Nullable<T> INSIDE the inner Select — so the "no row" sentinel
    // can be null — and converts the whole reducer result back to the non-nullable T at the very end. MEASURED
    // (see Nullable_widened_shape_really_arrives_wrapped_in_a_Convert, the guard for these tests):
    //
    //   Convert(DbSet<M>().Where(fk)[.OrderBy(k)][.Where(pred)]
    //             .Select(m => Convert(m.Rank, int?)).FirstOrDefault(), int)
    //
    // A `string` (already-nullable) member, and EVERY First() — value-type member or not — arrive UNWRAPPED,
    // which is why the recognizer's peel is scoped to FirstOrDefault and to this exact widening idiom.

    /// <summary>
    /// The guard for every test in this section: if EF ever stopped wrapping this shape, the peel under test
    /// would become dead code and the accept tests below would silently start exercising the ORDINARY
    /// unwrapped path instead. Pins the wrapping — an outer <c>Convert</c> to the non-nullable member type over
    /// the <c>FirstOrDefault</c> call, and an inner <c>Convert</c> to <c>Nullable&lt;T&gt;</c> inside the
    /// <c>Select</c> — as a fact about the tree, and pins that <c>First()</c> is NOT wrapped.
    /// </summary>
    [Fact]
    public void Nullable_widened_shape_really_arrives_wrapped_in_a_Convert()
    {
        var (_, wrapped) = BindAndCapture(q =>
            q.Select(a => new { a.Id, M = a.IdentificationMethods.FirstOrDefault()!.Rank }));

        var leaf = Assert.IsAssignableFrom<NewExpression>(Assert.IsAssignableFrom<LambdaExpression>(wrapped).Body)
            .Arguments[1];
        var outerConvert = Assert.IsType<UnaryExpression>(leaf);
        Assert.Equal(ExpressionType.Convert, outerConvert.NodeType);
        Assert.Equal(typeof(int), outerConvert.Type);

        var reducer = Assert.IsAssignableFrom<MethodCallExpression>(outerConvert.Operand);
        Assert.Equal(nameof(Queryable.FirstOrDefault), reducer.Method.Name);
        Assert.Equal(typeof(int?), reducer.Type);

        var innerSelect = Assert.IsAssignableFrom<MethodCallExpression>(reducer.Arguments[0]);
        Assert.Equal(nameof(Queryable.Select), innerSelect.Method.Name);
        var memberBody = innerSelect.Arguments[1].UnwrapLambdaFromQuote().Body;
        Assert.Equal(typeof(int?), Assert.IsType<UnaryExpression>(memberBody).Type);

        // First() over the SAME non-nullable value-type member is not wrapped at all.
        var (_, unwrapped) = BindAndCapture(q =>
            q.Select(a => new { a.Id, M = a.IdentificationMethods.First().Rank }));
        Assert.IsAssignableFrom<MethodCallExpression>(
            Assert.IsAssignableFrom<NewExpression>(Assert.IsAssignableFrom<LambdaExpression>(unwrapped).Body)
                .Arguments[1]);
    }

    /// <summary>
    /// The motivating shape of EF-449's second fix — reduced to <c>Rank</c>, a non-nullable <c>int</c>, via
    /// <c>FirstOrDefault()</c>. Before the peel this declined at the recognizer's very first structural check
    /// (the leaf is a <see cref="UnaryExpression"/>, not the <see cref="MethodCallExpression"/> that check
    /// required), which is what kept the
    /// <c>BuiltInDataTypesMongoTest.Can_read_back_mapped_enum_from_collection_first_or_default</c> spec test
    /// failing to translate.
    /// </summary>
    [Fact]
    public void Nullable_widened_FirstOrDefault_over_a_non_nullable_int_member_is_recognized()
    {
        var mongoQ = BindAccepted(q =>
            q.Select(a => new { a.Id, M = a.IdentificationMethods.FirstOrDefault()!.Rank }));

        var leaf = Assert.Single(mongoQ.CorrelatedReducerLeaves);
        Assert.Equal("M", leaf.Alias);
        // The REAL, non-nullable member — resolved through the peeled inner Convert, not the widened Nullable<T>.
        Assert.Equal("Rank", leaf.Member);
        Assert.False(leaf.ThrowOnEmpty);
        Assert.Equal([new BsonDocument("$limit", 1)], SubPipelineOf(leaf));

        var projection = Assert.Single(mongoQ.Select.Projection, p => p.Alias == "M");
        var elementRef = Assert.IsType<MongoElementRefExpression>(projection.Expression);
        Assert.Equal("_lookup_IdentificationMethods.Rank", elementRef.Path);
        // The leaf's CLR type is the OUTER convert's — the real member type the shaper expects — never the
        // widened Nullable<int> the inner tree carries.
        Assert.Equal(typeof(int), elementRef.Type);
    }

    /// <summary>
    /// The spec test's own member kind: an ENUM (a non-nullable value type, so identically widened).
    /// </summary>
    [Fact]
    public void Nullable_widened_FirstOrDefault_over_an_enum_member_is_recognized()
    {
        var mongoQ = BindAccepted(q =>
            q.Select(a => new { a.Id, K = a.IdentificationMethods.FirstOrDefault()!.Kind }));

        var leaf = Assert.Single(mongoQ.CorrelatedReducerLeaves);
        Assert.Equal("Kind", leaf.Member);
        Assert.False(leaf.ThrowOnEmpty);
        Assert.Equal(
            typeof(IdentificationKind),
            Assert.IsType<MongoElementRefExpression>(
                Assert.Single(mongoQ.Select.Projection, p => p.Alias == "K").Expression).Type);
    }

    /// <summary>
    /// <c>OrderBy</c>/<c>OrderByDescending</c> and a constant predicate nest INSIDE the widened reducer exactly
    /// as they do in the unwrapped shape (MEASURED: <c>Where(fk).OrderByDescending(k).Where(pred).Select(...)
    /// .FirstOrDefault()</c>), so the existing chain walk handles them unchanged once the wrappers are peeled.
    /// </summary>
    [Fact]
    public void Nullable_widened_FirstOrDefault_with_OrderBy_and_predicate_stages_match_then_sort_then_limit()
    {
        var mongoQ = BindAccepted(q => q.Select(a => new
        {
            a.Id,
            M = a.IdentificationMethods.OrderByDescending(m => m.Rank).FirstOrDefault(m => m.Rank > 3)!.Rank
        }));

        var leaf = Assert.Single(mongoQ.CorrelatedReducerLeaves);
        Assert.Equal(
        [
            new BsonDocument("$match", new BsonDocument("Rank", new BsonDocument("$gt", 3))),
            new BsonDocument("$sort", new BsonDocument("Rank", -1)),
            new BsonDocument("$limit", 1)
        ], SubPipelineOf(leaf));
    }

    /// <summary>
    /// The peel is a NORMALIZATION step feeding the existing logic, not a parallel path that skips gates: the
    /// parameterized-predicate decline fires for the widened shape too. Paired with the constant-predicate
    /// accept above, this discriminates "declined because of the parameter" from "the widened shape declines".
    /// </summary>
    [Fact]
    public void Nullable_widened_FirstOrDefault_with_a_parameterized_predicate_declines()
    {
        var threshold = 3;

        AssertDeclined(q => q.Select(a => new
        {
            a.Id, M = a.IdentificationMethods.FirstOrDefault(m => m.Rank > threshold)!.Rank
        }));

        // The constant-valued twin is admitted, so the decline above is the parameter gate, not the shape.
        var constant = BindAnimals(q => q.Select(a => new
        {
            a.Id, M = a.IdentificationMethods.FirstOrDefault(m => m.Rank > 3)!.Rank
        }));
        Assert.True(constant.Accepted);
        Assert.NotEqual(0, threshold);
    }

    /// <summary>
    /// The peel is NOT a general "strip any Convert around a reducer" rule. A user-written widening cast
    /// (<c>(long)…FirstOrDefault().Rank</c>) arrives as a DOUBLE convert
    /// (<c>Convert(Convert(reducer, int), long)</c>, MEASURED) whose outer operand is not the reducer call, so
    /// the peel does not fire and the leaf declines — rather than being admitted under a CLR type the emitted
    /// <c>$project</c> never produces.
    /// </summary>
    [Fact]
    public void Widening_cast_over_a_nullable_widened_reduction_declines()
        => AssertDeclined(q => q.Select(a => new
        {
            a.Id, M = (long)a.IdentificationMethods.FirstOrDefault()!.Rank
        }));

    /// <summary>
    /// The other gates reached only through the peel: a two-hop chain and a non-scalar reduced member still
    /// decline when the reduction is over a non-nullable value type. (Their unwrapped twins are covered above.)
    /// </summary>
    [Fact]
    public void Nullable_widened_two_hop_navigation_chain_declines()
        => AssertDeclined(q => q.Select(a => new
        {
            a.Id, M = a.IdentificationMethods.FirstOrDefault()!.Detail.Ordinal
        }));

    /// <summary>An OWNED collection reduced to a non-nullable value-type member still declines.</summary>
    [Fact]
    public void Nullable_widened_owned_collection_navigation_declines()
        => AssertDeclined(q => q.Select(a => new { a.Id, M = a.Tags.FirstOrDefault()!.Weight }));

    /// <summary>A TPH-derived target still declines for the widened shape.</summary>
    [Fact]
    public void Nullable_widened_TPH_derived_target_declines()
        => AssertDeclined(
            Bind<TphOwner>(q => q.Select(o => new { o.Id, N = o.Children.FirstOrDefault()!.Ordinal }), TphModel));

    /// <summary>
    /// The one shape the outer peel's TYPE-ONLY recognition cannot tell apart from EF's widening idiom, so it
    /// is separated by requiring BOTH halves of the idiom to fire (EF-449 fix 2, review round 2). A user-WRITTEN
    /// narrowing cast over an ALREADY-NULLABLE member arrives with the IDENTICAL outer shape — MEASURED, and
    /// pinned structurally by <see cref="User_written_narrowing_cast_really_arrives_with_the_same_outer_shape"/>:
    /// <c>Convert(nav.Where(fk).Select(m =&gt; m.NullableRank).FirstOrDefault(), int)</c>, an <c>int?</c>-typed
    /// reducer narrowed to <c>int</c>, but with a BARE <see cref="MemberExpression"/> as the inner
    /// <c>Select</c>'s body (no inner widening <c>Convert</c> to peel, because the member is already
    /// <c>int?</c>). Nothing downstream catches it: the member's own <c>ClrType</c> IS <c>int?</c>, so the
    /// nullable-member gate is satisfied, and that gate is <c>First()</c>-only anyway. Admitting it would type
    /// the leaf <c>int</c> and let the read side's default-on-empty branch return <c>0</c> both for an empty
    /// collection AND for a MATCHED row whose <c>NullableRank</c> is null — where real LINQ-to-objects throws
    /// <see cref="InvalidOperationException"/> ("Nullable object must not have a value").
    /// </summary>
    [Fact]
    public void User_written_narrowing_cast_over_an_already_nullable_member_declines()
        => AssertDeclined(q => q.Select(a => new
        {
            a.Id, M = (int)a.IdentificationMethods.FirstOrDefault()!.NullableRank!
        }));

    /// <summary>
    /// The structural guard for the decline above: pins that this shape really does reach the recognizer with
    /// the same OUTER shape EF's widening idiom has (a <c>Convert</c> to the non-nullable type over an
    /// <c>int?</c>-typed <c>FirstOrDefault</c>) while differing ONLY in the inner <c>Select</c> body being a bare
    /// member access. If EF ever reshaped this, the decline above would still pass but would no longer be
    /// exercising the both-peels-must-fire cross-check it exists for.
    /// </summary>
    [Fact]
    public void User_written_narrowing_cast_really_arrives_with_the_same_outer_shape()
    {
        var (_, tree) = BindAndCapture(q => q.Select(a => new
        {
            a.Id, M = (int)a.IdentificationMethods.FirstOrDefault()!.NullableRank!
        }));

        var leaf = Assert.IsAssignableFrom<NewExpression>(Assert.IsAssignableFrom<LambdaExpression>(tree).Body)
            .Arguments[1];
        var outerConvert = Assert.IsType<UnaryExpression>(leaf);
        Assert.Equal(ExpressionType.Convert, outerConvert.NodeType);
        Assert.Equal(typeof(int), outerConvert.Type);

        var reducer = Assert.IsAssignableFrom<MethodCallExpression>(outerConvert.Operand);
        Assert.Equal(nameof(Queryable.FirstOrDefault), reducer.Method.Name);
        Assert.Equal(typeof(int?), reducer.Type);

        // The DISCRIMINATING difference from EF's own widening idiom: the inner Select's body is a BARE member
        // access, already Nullable<int>, with no widening Convert wrapped around it.
        var innerSelect = Assert.IsAssignableFrom<MethodCallExpression>(reducer.Arguments[0]);
        Assert.Equal(nameof(Queryable.Select), innerSelect.Method.Name);
        var memberBody = innerSelect.Arguments[1].UnwrapLambdaFromQuote().Body;
        Assert.IsAssignableFrom<MemberExpression>(memberBody);
        Assert.Equal(typeof(int?), memberBody.Type);
    }

    /// <summary>
    /// The discriminating control for the pair above — the SAME cast spelling over the SAME navigation, differing
    /// only in the reduced member being non-nullable (<c>Rank</c>, so EF's own widening idiom applies and both
    /// peels fire), is ADMITTED. Without this pairing the decline could not be distinguished from "any cast over
    /// a reduction declines".
    /// </summary>
    [Fact]
    public void The_widening_idiom_and_the_user_cast_differ_only_in_the_members_nullability()
    {
        var userCast = BindAnimals(q => q.Select(a => new
        {
            a.Id, M = (int)a.IdentificationMethods.FirstOrDefault()!.NullableRank!
        }));
        var efWidened = BindAnimals(q => q.Select(a => new
        {
            a.Id, M = a.IdentificationMethods.FirstOrDefault()!.Rank
        }));

        Assert.False(userCast.Accepted);
        Assert.True(efWidened.Accepted);
    }

    // ── Test infrastructure ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Stands in for EF Core's own funcletization step: rewrites every CLOSURE capture (a member read off the
    /// compiler-generated display-class constant the C# compiler emits for a captured local) into the EF query
    /// parameter node that step would produce — a prefix-named <see cref="ParameterExpression"/> on EF8/EF9, a
    /// <c>QueryParameterExpression</c> on EF10 (the same version split
    /// <c>MongoExpressionTranslatorTests</c> already encodes by hand).
    /// </summary>
    private sealed class ClosureCaptureParameterizer : ExpressionVisitor
    {
        private int _index;

        public static Expression Parameterize(Expression expression)
            => new ClosureCaptureParameterizer().Visit(expression);

        /// <summary>Whether <paramref name="expression"/> contains at least one EF query parameter.</summary>
        public static bool ContainsQueryParameter(Expression expression)
        {
            var found = false;
            new AnonymousVisitor(node =>
            {
                if (NativeQueryParameter.TryGetQueryParameterName(node, out _))
                {
                    found = true;
                }
            }).Visit(expression);
            return found;
        }

        protected override Expression VisitMember(MemberExpression node)
        {
            if (node.Expression is ConstantExpression { Value: { } closure }
                && Attribute.IsDefined(closure.GetType(), typeof(CompilerGeneratedAttribute)))
            {
#if EF8 || EF9
                return Expression.Parameter(
                    node.Type,
                    QueryCompilationContext.QueryParameterPrefix + node.Member.Name + "_" + _index++);
#else
                // EF10 dropped QueryCompilationContext.QueryParameterPrefix; the "__" spelling is what
                // MongoExpressionTranslatorTests already hard-codes for the same reason.
                return new QueryParameterExpression("__" + node.Member.Name + "_" + _index++, node.Type);
#endif
            }

            return base.VisitMember(node);
        }

        private sealed class AnonymousVisitor(Action<Expression> onNode) : ExpressionVisitor
        {
            [return: NotNullIfNotNull(nameof(node))]
            public override Expression? Visit(Expression? node)
            {
                if (node is not null)
                {
                    onNode(node);
                }

                return base.Visit(node);
            }
        }
    }

    private sealed class TestDbContext<TRoot>(Action<ModelBuilder> model) : DbContext(BuildOptions())
        where TRoot : class
    {
        private static DbContextOptions BuildOptions()
            => new DbContextOptionsBuilder()
                .UseMongoDB("mongodb://localhost:27017", "UnitTests")
                .ReplaceService<IModelCacheKeyFactory, IgnoreCacheKeyFactory>()
                .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
                .Options;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            model(modelBuilder);
        }

        private sealed class IgnoreCacheKeyFactory : IModelCacheKeyFactory
        {
            private static int Count;

            public object Create(DbContext context, bool designTime) => Interlocked.Increment(ref Count);
        }
    }

    /// <summary>
    /// A minimal queryable stub rooted in an <see cref="EntityQueryRootExpression"/>, so applied LINQ operators
    /// build the same method-call chain EF's own preprocessing phase receives. Mirrors
    /// <c>SlotPopulationTests.RootExpressionQueryable</c>.
    /// </summary>
    private sealed class RootExpressionQueryable<T>(Expression expression) : IOrderedQueryable<T>
    {
        public Type ElementType => typeof(T);
        public Expression Expression => expression;
        public IQueryProvider Provider => new ThrowingProvider();
        public IEnumerator<T> GetEnumerator() => throw new NotSupportedException("Test stub only.");
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

        private sealed class ThrowingProvider : IQueryProvider
        {
            public IQueryable CreateQuery(Expression e) => throw new NotSupportedException("Test stub only.");
            public IQueryable<TElement> CreateQuery<TElement>(Expression e) => new RootExpressionQueryable<TElement>(e);
            public object Execute(Expression e) => throw new NotSupportedException("Test stub only.");
            public TResult Execute<TResult>(Expression e) => throw new NotSupportedException("Test stub only.");
        }
    }
}
