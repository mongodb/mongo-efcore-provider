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
/// Accept/decline matrix for <c>NativeProjectionBinder.TryGetCorrelatedReducerLeaf</c>: a reference-collection
/// navigation reduced by <c>First</c>/<c>FirstOrDefault</c> to a scalar member in a projection
/// (<c>a.IdentificationMethods.FirstOrDefault().Method</c>).
/// </summary>
/// <remarks>
/// Queries go through EF Core's real nav-expansion first, because it rewrites the navigation access into a shape
/// unlike the written LINQ; skipping it would only produce false declines. Assertions are on the staged
/// <c>$lookup</c> sub-pipeline, the <see cref="MongoCorrelatedReducerLeaf"/> and the projection path, not just
/// the accept/decline boolean.
/// </remarks>
public class NativeCorrelatedReducerLeafTests
{
    // ── Model: a cross-collection (non-owned) one-to-many, plus the decline fixtures ────────────────────────

    private class Animal
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";

        // Same name as IdentificationMethod.Rank on purpose: a single-scope translator would render
        // `m.Rank > a.Rank` as "$Rank" twice, comparing the element to itself. See
        // Correlated_field_to_field_predicate_declines.
        public int Rank { get; set; }

        public List<IdentificationMethod> IdentificationMethods { get; set; } = null!;
        public List<Sighting> Sightings { get; set; } = null!;
        public List<OwnedTag> Tags { get; set; } = null!;
    }

    /// <summary>
    /// Enum member, as in <c>BuiltInDataTypesMongoTest.Can_read_back_mapped_enum_from_collection_first_or_default</c>.
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
        /// For a user-written narrowing cast (<c>(int)nav.FirstOrDefault()!.NullableRank</c>); see
        /// <c>User_written_narrowing_cast_over_an_already_nullable_member_declines</c>.
        /// </summary>
        public int? NullableRank { get; set; }

        public IdentificationKind Kind { get; set; }

        /// <summary>
        /// Same enum stored as a string, so stored order and value diverge from the CLR ones; see
        /// <c>Non_default_represented_reduced_member_declines</c> / <c>Non_default_represented_sort_key_declines</c>.
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

    /// <summary>A second reference collection nav, so the two-leaf accept case uses two distinct lookups.</summary>
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

    // ── TPH model: the target of the reduced navigation is a derived type ───────────────────────────────────

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
        // TphDerivedChild is a TPH-derived (non-root) target, which the recognizer must decline.
        mb.Entity<TphChildBase>().HasDiscriminator<string>("_t")
            .HasValue<TphDerivedChild>(nameof(TphDerivedChild));
        mb.Entity<TphOwner>().HasMany(o => o.Children).WithOne(c => c.Owner).HasForeignKey(c => c.OwnerId);
    };

    // ── Harness ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs <paramref name="buildQuery"/> through the real preprocessor and hands the terminal <c>Select</c>'s
    /// selector straight to <see cref="NativeProjectionBinder.TryPopulateNativeProjection"/>, stopping short of
    /// the shaper/bind side.
    /// </summary>
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

        // Emulates QueryCompiler's funcletization of captured variables into query parameters; otherwise a
        // captured local arrives as a closure read and declines for an unrelated reason. See
        // Harness_really_turns_a_captured_local_into_an_EF_query_parameter.
        var preprocessed = ClosureCaptureParameterizer.Parameterize(
            preprocessor.Process(buildQuery(root).Expression));

        // Expect `DbSet<TRoot>().Select(<selector>)`; asserted so an EF reshaping fails loudly.
        var select = Assert.IsAssignableFrom<MethodCallExpression>(preprocessed);
        Assert.Equal(nameof(Queryable.Select), select.Method.Name);
        var selector = Assert.IsAssignableFrom<LambdaExpression>(select.Arguments[1].UnwrapLambdaFromQuote());

        var mongoQ = new MongoQueryExpression(entityType);
        return (NativeProjectionBinder.TryPopulateNativeProjection(mongoQ, selector), mongoQ, selector);
    }

    private static (bool Accepted, MongoQueryExpression Query) BindAnimals(Func<IQueryable<Animal>, IQueryable> buildQuery)
        => Bind(buildQuery, Model);

    /// <summary>Binds and asserts the projection was admitted.</summary>
    private static MongoQueryExpression BindAccepted(Func<IQueryable<Animal>, IQueryable> buildQuery)
    {
        var (accepted, mongoQ) = BindAnimals(buildQuery);
        Assert.True(accepted);
        return mongoQ;
    }

    /// <summary>The <c>$lookup</c> sub-pipeline this leaf staged.</summary>
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

        // A bare reduction stages only the $limit.
        Assert.Equal([new BsonDocument("$limit", 1)], SubPipelineOf(leaf));

        // The $lookup is registered, and the projection reads the member off the unwound field.
        Assert.Contains(leaf.Lookup, mongoQ.GetPendingLookups());
        var projection = Assert.Single(mongoQ.Select.Projection, p => p.Alias == "M");
        Assert.Equal(
            "_lookup_IdentificationMethods.Method",
            Assert.IsType<MongoElementRefExpression>(projection.Expression).Path);
    }

    /// <summary>
    /// Reduces to non-nullable <c>Rank</c>; <c>First()</c> over a nullable member declines (see
    /// <see cref="First_over_nullable_typed_member_declines"/>).
    /// </summary>
    [Fact]
    public void First_sets_ThrowOnEmpty()
    {
        var mongoQ = BindAccepted(q =>
            q.Select(a => new { a.Id, M = a.IdentificationMethods.First().Rank }));

        var leaf = Assert.Single(mongoQ.CorrelatedReducerLeaves);
        Assert.True(leaf.ThrowOnEmpty);

        // Same join as FirstOrDefault; the distinction is read-side only.
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
    /// Sort plus constant predicate: stages must be $match, $sort, $limit, even though the nav-expanded tree has
    /// the <c>Where</c> outside the <c>OrderBy</c>.
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
    /// Two leaves over different navigations each get their own <c>$lookup</c>; the same-navigation collision
    /// guard must not catch this.
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
    // Every decline also asserts nothing was staged (no leaf, lookup or projection), catching a recognizer that
    // half-registers before declining.

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
    /// Guards the harness: without real parameterization, <see cref="Parameterized_predicate_declines"/> would
    /// decline for an unrelated reason.
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
    /// Control for <see cref="Parameterized_predicate_declines"/>: differing only in constant vs. captured value
    /// proves the decline is due to the parameter, not the predicate.
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
    /// <c>First()</c> over a nullable member (<c>string Method</c>) declines: the read side's alias-only check
    /// can't tell "no related row" from "a row whose member is null".
    /// </summary>
    [Fact]
    public void First_over_nullable_typed_member_declines()
        => AssertDeclined(q => q.Select(a => new { a.Id, M = a.IdentificationMethods.First().Method }));

    /// <summary>
    /// Control for <see cref="First_over_nullable_typed_member_declines"/>: <c>FirstOrDefault()</c> never throws
    /// on empty, so reading a missing alias as default is always correct.
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
    /// <c>nav.FirstOrDefault()</c> with no member read reduces to a whole entity, which this leaf doesn't shape.
    /// </summary>
    [Fact]
    public void Whole_element_reduction_with_no_member_read_declines()
        => AssertDeclined(q => q.Select(a => new { a.Id, M = a.IdentificationMethods.FirstOrDefault() }));

    /// <summary>
    /// Owned collections are handled elsewhere: nav-expansion leaves an <c>EF.Property(...).AsQueryable()</c>
    /// with no <see cref="EntityQueryRootExpression"/> or FK correlation.
    /// </summary>
    [Fact]
    public void Owned_collection_navigation_declines()
        => AssertDeclined(q => q.Select(a => new { a.Id, M = a.Tags.FirstOrDefault()!.Label }));

    /// <summary>
    /// A TPH-derived target declines at the recognizer's metadata gate, before the <see cref="LookupExpression"/>
    /// is built. A structural backstop (empty <see cref="LookupExpression.PipelineStages"/> after construction,
    /// since the constructor would prepend a discriminator <c>$match</c> that <see cref="LookupPipelineKind.CorrelatedReducer"/>
    /// would silently overwrite) is unreachable while that gate stands.
    /// </summary>
    [Fact]
    public void TPH_derived_target_declines()
        => AssertDeclined(
            Bind<TphOwner>(q => q.Select(o => new { o.Id, L = o.Children.FirstOrDefault()!.Label }), TphModel));

    /// <summary>
    /// A bare selector body declines: its alias is derived from the translated leaf, and no derivation can honour
    /// a <c>_lookup_&lt;Nav&gt;.&lt;Member&gt;</c> path. Pins behavior; doesn't isolate the
    /// <c>allowWholeRootEntityLeaf</c> conjunct, since alias derivation also refuses.
    /// </summary>
    [Fact]
    public void Bare_selector_body_declines()
        => AssertDeclined(q => q.Select(a => a.IdentificationMethods.FirstOrDefault()!.Method));

    /// <summary>
    /// A predicate referencing the outer animal: the sub-pipeline runs in the foreign collection's scope, and a
    /// single-scope translator could resolve the outer member against a same-named target member (wrong data).
    /// </summary>
    [Fact]
    public void Predicate_correlated_to_the_outer_entity_declines()
        => AssertDeclined(q => q.Select(a => new
        {
            a.Id, M = a.IdentificationMethods.FirstOrDefault(m => m.Method == a.Name)!.Method
        }));

    /// <summary>
    /// <c>m.Rank &gt; a.Rank</c>: both types have <c>Rank</c>, so a single-scope translator would render
    /// <c>{$gt: ["$Rank", "$Rank"]}</c> (silently wrong). Only the outer-parameter identity guard catches this;
    /// <see cref="Predicate_correlated_to_the_outer_entity_declines"/> is also caught by the translator.
    /// </summary>
    [Fact]
    public void Correlated_field_to_field_predicate_declines()
        => AssertDeclined(q => q.Select(a => new
        {
            a.Id, M = a.IdentificationMethods.FirstOrDefault(m => m.Rank > a.Rank)!.Method
        }));

    /// <summary>
    /// Two leaves over the same navigation with different sub-pipelines: <c>AddLookup</c> dedupes by alias, so
    /// the second would silently read the first's row. Declines the projection.
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
    /// Reducer then <c>Count</c> over the same navigation: both want the same alias with incompatible shapes
    /// (unwound single document vs. array for <c>$size</c>), and <c>AddLookup</c> keeps the first — a server
    /// error or a silent empty read. Declines the projection.
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
    /// Mirror of <see cref="Reducer_leaf_then_count_leaf_over_the_same_navigation_decline"/>: a different
    /// registration site sees the collision.
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
    /// Control for the collision tests: reducer and count leaves over different navigations are admitted.
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

    // ── Serialization gates: the reduced member and the sort key must both be default-serialized ────────────
    //
    // Both use NativeGroupByBinder.HasDefaultKeySerialization. The member is read off the alias with no
    // IProperty, so it would materialize the raw stored value; the sort key orders by stored representation
    // (enum-as-string sorts 1 < 0 < 2), so the wrong element would be "first".

    /// <summary>
    /// Reduced member stored as a string: the alias read would yield the raw stored string.
    /// </summary>
    [Fact]
    public void Non_default_represented_reduced_member_declines()
        => AssertDeclined(q => q.Select(a => new
        {
            a.Id, K = a.IdentificationMethods.FirstOrDefault()!.StoredKind
        }));

    /// <summary>
    /// Sort key stored as a string: <c>$sort</c> would order by the string rather than the CLR value and pick the
    /// wrong element. The reduced member is default-serialized, so only the sort-key gate can fire.
    /// </summary>
    [Fact]
    public void Non_default_represented_sort_key_declines()
        => AssertDeclined(q => q.Select(a => new
        {
            a.Id, M = a.IdentificationMethods.OrderBy(m => m.StoredKind).FirstOrDefault()!.Method
        }));

    /// <summary>Descending spelling of the sort-key gate.</summary>
    [Fact]
    public void Non_default_represented_descending_sort_key_declines()
        => AssertDeclined(q => q.Select(a => new
        {
            a.Id, M = a.IdentificationMethods.OrderByDescending(m => m.StoredKind).FirstOrDefault()!.Method
        }));

    /// <summary>
    /// Control for both gates: the default-serialized <c>Kind</c> is admitted as member and as sort key.
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

    // ── Scope boundary: one sort key only, no paging inside the nav chain ───────────────────────────────────
    //
    // Scope is a single OrderBy/OrderByDescending key; ThenBy/Skip/Take decline. Pinned as a scope decision.

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

    // ── The nullable-widened FirstOrDefault() shape ─────────────────────────────────────────────────────────
    //
    // For FirstOrDefault() over a non-nullable value-type member, nav-expansion widens the member to Nullable<T>
    // inside the Select and converts back at the end:
    //
    //   Convert(DbSet<M>().Where(fk)[.OrderBy(k)][.Where(pred)]
    //             .Select(m => Convert(m.Rank, int?)).FirstOrDefault(), int)
    //
    // Already-nullable members and every First() arrive unwrapped, so the peel is scoped to this idiom.

    /// <summary>
    /// Guards this section: pins the outer and inner <c>Convert</c>s EF emits (and that <c>First()</c> isn't
    /// wrapped), so the tests below can't silently exercise the unwrapped path instead.
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

        // First() over the same member is not wrapped.
        var (_, unwrapped) = BindAndCapture(q =>
            q.Select(a => new { a.Id, M = a.IdentificationMethods.First().Rank }));
        Assert.IsAssignableFrom<MethodCallExpression>(
            Assert.IsAssignableFrom<NewExpression>(Assert.IsAssignableFrom<LambdaExpression>(unwrapped).Body)
                .Arguments[1]);
    }

    /// <summary>
    /// <c>FirstOrDefault()</c> reduced to a non-nullable <c>int</c>: the leaf is a <see cref="UnaryExpression"/>
    /// that must be peeled to reach the reducer call.
    /// </summary>
    [Fact]
    public void Nullable_widened_FirstOrDefault_over_a_non_nullable_int_member_is_recognized()
    {
        var mongoQ = BindAccepted(q =>
            q.Select(a => new { a.Id, M = a.IdentificationMethods.FirstOrDefault()!.Rank }));

        var leaf = Assert.Single(mongoQ.CorrelatedReducerLeaves);
        Assert.Equal("M", leaf.Alias);
        // The real member, resolved through the peeled inner Convert.
        Assert.Equal("Rank", leaf.Member);
        Assert.False(leaf.ThrowOnEmpty);
        Assert.Equal([new BsonDocument("$limit", 1)], SubPipelineOf(leaf));

        var projection = Assert.Single(mongoQ.Select.Projection, p => p.Alias == "M");
        var elementRef = Assert.IsType<MongoElementRefExpression>(projection.Expression);
        Assert.Equal("_lookup_IdentificationMethods.Rank", elementRef.Path);
        // Typed as the outer Convert (what the shaper expects), not the widened Nullable<int>.
        Assert.Equal(typeof(int), elementRef.Type);
    }

    /// <summary>
    /// An enum member (a non-nullable value type, so also widened).
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
    /// Sort and predicate nest inside the widened reducer as in the unwrapped shape, so the chain walk handles
    /// them once the wrappers are peeled.
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
    /// The peel feeds the existing gates rather than bypassing them: the parameterized-predicate decline still
    /// fires, while the constant twin is admitted.
    /// </summary>
    [Fact]
    public void Nullable_widened_FirstOrDefault_with_a_parameterized_predicate_declines()
    {
        var threshold = 3;

        AssertDeclined(q => q.Select(a => new
        {
            a.Id, M = a.IdentificationMethods.FirstOrDefault(m => m.Rank > threshold)!.Rank
        }));

        var constant = BindAnimals(q => q.Select(a => new
        {
            a.Id, M = a.IdentificationMethods.FirstOrDefault(m => m.Rank > 3)!.Rank
        }));
        Assert.True(constant.Accepted);
        Assert.NotEqual(0, threshold);
    }

    /// <summary>
    /// The peel isn't "strip any Convert": a user widening cast arrives as <c>Convert(Convert(reducer, int), long)</c>,
    /// so it declines rather than being typed as something <c>$project</c> doesn't produce.
    /// </summary>
    [Fact]
    public void Widening_cast_over_a_nullable_widened_reduction_declines()
        => AssertDeclined(q => q.Select(a => new
        {
            a.Id, M = (long)a.IdentificationMethods.FirstOrDefault()!.Rank
        }));

    /// <summary>
    /// Other gates still apply through the peel: a two-hop chain declines.
    /// </summary>
    [Fact]
    public void Nullable_widened_two_hop_navigation_chain_declines()
        => AssertDeclined(q => q.Select(a => new
        {
            a.Id, M = a.IdentificationMethods.FirstOrDefault()!.Detail.Ordinal
        }));

    /// <summary>An owned collection reduced to a non-nullable value-type member still declines.</summary>
    [Fact]
    public void Nullable_widened_owned_collection_navigation_declines()
        => AssertDeclined(q => q.Select(a => new { a.Id, M = a.Tags.FirstOrDefault()!.Weight }));

    /// <summary>A TPH-derived target still declines for the widened shape.</summary>
    [Fact]
    public void Nullable_widened_TPH_derived_target_declines()
        => AssertDeclined(
            Bind<TphOwner>(q => q.Select(o => new { o.Id, N = o.Children.FirstOrDefault()!.Ordinal }), TphModel));

    /// <summary>
    /// A user narrowing cast over an already-nullable member (<c>(int)…FirstOrDefault().NullableRank</c>) has the
    /// same outer shape as EF's widening idiom but no inner <c>Convert</c>, so both peels are required. Admitting
    /// it would return <c>0</c> for a matched row with null <c>NullableRank</c>, where LINQ throws
    /// <see cref="InvalidOperationException"/>.
    /// </summary>
    [Fact]
    public void User_written_narrowing_cast_over_an_already_nullable_member_declines()
        => AssertDeclined(q => q.Select(a => new
        {
            a.Id, M = (int)a.IdentificationMethods.FirstOrDefault()!.NullableRank!
        }));

    /// <summary>
    /// Guards the decline above: pins that the shape has the widening idiom's outer shape and differs only in the
    /// bare inner member access.
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

        // The difference from EF's idiom: a bare Nullable<int> member access, no inner Convert.
        var innerSelect = Assert.IsAssignableFrom<MethodCallExpression>(reducer.Arguments[0]);
        Assert.Equal(nameof(Queryable.Select), innerSelect.Method.Name);
        var memberBody = innerSelect.Arguments[1].UnwrapLambdaFromQuote().Body;
        Assert.IsAssignableFrom<MemberExpression>(memberBody);
        Assert.Equal(typeof(int?), memberBody.Type);
    }

    /// <summary>
    /// Control: the non-nullable member (EF's widening idiom, both peels fire) is admitted, so the decline
    /// isn't "any cast over a reduction declines".
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
    /// Stands in for EF Core's funcletization: rewrites each closure capture into an EF query parameter node (a
    /// prefix-named <see cref="ParameterExpression"/> on EF8/EF9, a <c>QueryParameterExpression</c> on EF10).
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
                // EF10 dropped QueryCompilationContext.QueryParameterPrefix.
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
    /// Queryable stub rooted in an <see cref="EntityQueryRootExpression"/>, so LINQ operators build the chain EF's
    /// preprocessor receives.
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
