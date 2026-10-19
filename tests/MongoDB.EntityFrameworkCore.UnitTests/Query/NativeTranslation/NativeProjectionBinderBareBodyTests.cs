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
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using MongoDB.EntityFrameworkCore;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Query.Expressions;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query.NativeTranslation;

/// <summary>
/// EF-322 step 3a: <see cref="NativeProjectionBinder.TryPopulateNativeProjection"/>'s BARE-selector-body arm.
/// Asserts the DERIVED ALIAS and its TIER, not just the admit/decline boolean — the alias IS the mechanism this
/// slice turns on, and a test that only checked the boolean would stay green if the alias were wrong.
/// </summary>
public class NativeProjectionBinderBareBodyTests
{
    private class Order
    {
        public ObjectId Id { get; set; }
        public string Country { get; set; } = "";
        public int Amount { get; set; }
        public double Weight { get; set; }
        public List<string> Tags { get; set; } = null!;
        public Address Address { get; set; } = null!;
        public List<Line> Lines { get; set; } = null!;
    }

    private class Address
    {
        public string City { get; set; } = "";

        // The hop's own collection — the ONLY thing in this fixture that produces a DOTTED array path
        // ("Address.Notes"), which is what IsFallbackSafeBareSizeLeaf declines. Without it no test here could
        // discriminate that guard at all.
        public List<Note> Notes { get; set; } = null!;
    }

    private class Note
    {
        public string Text { get; set; } = "";
    }

    private class Line
    {
        public int Quantity { get; set; }
    }

    /// <summary>
    /// The same shape as <see cref="Order"/>, but the owned collection navigation is declared as
    /// <see cref="ISet{T}"/> — an ordinary, EF-supported collection type that <c>List&lt;Line&gt;</c> is NOT
    /// assignable to, and therefore one <c>TryCreateEmptyCollection</c> declines.
    /// </summary>
    /// <remarks>
    /// This fixture exists because nothing else in the tree could discriminate the CONSTRUCTIBILITY dimension of
    /// the A4-0 rewrite's reach: every interface-typed navigation modelled anywhere else is one <c>List&lt;T&gt;</c>
    /// IS assignable to, i.e. the success side of the branch. See the final-review F1 finding.
    /// </remarks>
    private class SetOrder
    {
        public ObjectId Id { get; set; }
        public string Country { get; set; } = "";
        public ISet<Line> Lines { get; set; } = new HashSet<Line>();
    }

    private static readonly Action<ModelBuilder> Model = mb =>
    {
        mb.Entity<Order>().OwnsOne(o => o.Address, a => a.OwnsMany(x => x.Notes));
        mb.Entity<Order>().OwnsMany(o => o.Lines);
    };

    private static readonly Action<ModelBuilder> SetModel = mb => mb.Entity<SetOrder>().OwnsMany(o => o.Lines);

    private static MongoQueryExpression TestQuery()
    {
        using var db = SingleEntityDbContext.Create<Order>(Model);
        return new MongoQueryExpression(db.Model.FindEntityType(typeof(Order))!);
    }

    private static MongoQueryExpression TestSetQuery()
    {
        using var db = SingleEntityDbContext.Create<SetOrder>(SetModel);
        return new MongoQueryExpression(db.Model.FindEntityType(typeof(SetOrder))!);
    }

    [Fact]
    public void Bare_top_level_scalar_is_admitted_with_the_element_name_as_the_alias()
    {
        var mongoQ = TestQuery();
        Expression<Func<Order, string>> selector = o => o.Country;

        Assert.True(NativeProjectionBinder.TryPopulateNativeProjection(mongoQ, selector));

        var projection = Assert.Single(mongoQ.Select.Projection);
        Assert.Equal("Country", projection.Alias);
        Assert.Equal("Country", Assert.IsType<MongoFieldExpression>(projection.Expression).ElementName);

        // The alias is registered on the carrier under the BARE sentinel key, which is what every alias-reading
        // site consults — a bare body's own ProjectionMember has no last member to derive a name from.
        Assert.True(mongoQ.Select.IsBareProjection);
        Assert.True(mongoQ.Select.TryGetProjectionAlias(null, out var alias));
        Assert.Equal("Country", alias);
        Assert.Equal(ProjectionAliasTier.DocumentPath, mongoQ.Select.BareProjectionTier);
        Assert.Equal(NativeRoute.Projection, mongoQ.Select.Route);
    }

    [Fact]
    public void Bare_single_property_primary_key_is_admitted_with_the_underscore_id_alias()
    {
        var mongoQ = TestQuery();
        Expression<Func<Order, ObjectId>> selector = o => o.Id;

        Assert.True(NativeProjectionBinder.TryPopulateNativeProjection(mongoQ, selector));

        // A document root's PK is stored at "_id" (PrimaryKeyDiscoveryConvention rewrites it), so tier 1's
        // alias-IS-the-path rule makes the alias "_id" rather than the CLR member name. That is what lets
        // RenderProject suppress its default `_id : 0` exclusion instead of emitting a malformed mix.
        var projection = Assert.Single(mongoQ.Select.Projection);
        Assert.Equal("_id", projection.Alias);
        Assert.True(mongoQ.Select.TryGetProjectionAlias(null, out var alias));
        Assert.Equal("_id", alias);
    }

    [Fact]
    public void Bare_primitive_collection_property_is_admitted_with_the_element_name_as_the_alias()
    {
        var mongoQ = TestQuery();
        Expression<Func<Order, List<string>>> selector = o => o.Tags;

        Assert.True(NativeProjectionBinder.TryPopulateNativeProjection(mongoQ, selector));

        // A primitive collection is a mapped PROPERTY, so it arrives as a plain member access and resolves to a
        // MongoFieldExpression — it never reaches the owned-array branch at all.
        var projection = Assert.Single(mongoQ.Select.Projection);
        Assert.Equal("Tags", projection.Alias);
        Assert.IsType<MongoFieldExpression>(projection.Expression);
        Assert.Equal(ProjectionAliasTier.DocumentPath, mongoQ.Select.BareProjectionTier);
    }

    [Fact]
    public void Bare_owned_hop_scalar_is_declined_and_leaves_no_override()
    {
        var mongoQ = TestQuery();
        Expression<Func<Order, string>> selector = o => o.Address.City;

        // The EF-362 tripwire. The leaf resolves fine — to the DOTTED path "Address.City" — but a dotted alias
        // is read back as a literal key while MongoDB renders `$project: {"Address.City": …}` as NESTED output,
        // so tier 1 requires a non-dotted path.
        Assert.False(NativeProjectionBinder.TryPopulateNativeProjection(mongoQ, selector));

        Assert.Empty(mongoQ.Select.Projection);
        Assert.False(mongoQ.Select.IsBareProjection);
        Assert.Null(mongoQ.Select.BareProjectionTier);
        Assert.False(mongoQ.Select.TryGetProjectionAlias(null, out _));
    }

    // ── EF-322 slice A4 (A4-1): TIER 2 — a COMPUTED bare leaf under the reserved `_v` alias ───────────────
    //
    // These four flip and pin the tier-2 admission. The tier is asserted, not just the alias string: the
    // late-fallback strip is TIER-conditional (do NOT strip for Synthetic, whose `_v` is exactly what the
    // driver's own bare push-down writes), so a test that checked only the alias would stay green if the tier
    // regressed to DocumentPath and would then be asserting a shape that silently reads a missing element.

    [Fact]
    public void Bare_arithmetic_leaf_is_admitted_under_the_reserved_alias_and_the_synthetic_tier()
    {
        var mongoQ = TestQuery();
        Expression<Func<Order, int>> selector = o => o.Amount * 2;

        // Was a DECLINE through step 3a (tier 1 requires a root-relative document path and an arithmetic leaf is
        // backed by no document element at all). A4-1 admits it instead, under a SYNTHETIC alias — which is a
        // different answer to the same question, not a relaxation of tier 1: `_v` is still not whole-document
        // readable, which is precisely why the strip must not fire for it.
        Assert.True(NativeProjectionBinder.TryPopulateNativeProjection(mongoQ, selector));

        var projection = Assert.Single(mongoQ.Select.Projection);
        Assert.Equal("_v", projection.Alias);
        Assert.Equal(
            MongoBinaryOperator.Multiply,
            Assert.IsType<MongoBinaryExpression>(projection.Expression).Operator);

        Assert.True(mongoQ.Select.IsBareProjection);
        Assert.True(mongoQ.Select.TryGetProjectionAlias(null, out var alias));
        Assert.Equal("_v", alias);
        Assert.Equal(ProjectionAliasTier.Synthetic, mongoQ.Select.BareProjectionTier);
        Assert.False(mongoQ.Select.HasDocumentPathAliasOverride);
        Assert.Equal(NativeRoute.Projection, mongoQ.Select.Route);
    }

    [Fact]
    public void Bare_cast_leaf_is_admitted_under_the_reserved_alias_and_the_synthetic_tier()
    {
        var mongoQ = TestQuery();
        Expression<Func<Order, int>> selector = o => (int)o.Weight;

        Assert.True(NativeProjectionBinder.TryPopulateNativeProjection(mongoQ, selector));

        var projection = Assert.Single(mongoQ.Select.Projection);
        Assert.Equal("_v", projection.Alias);
        Assert.IsType<MongoConvertExpression>(projection.Expression);
        Assert.Equal(ProjectionAliasTier.Synthetic, mongoQ.Select.BareProjectionTier);
        Assert.False(mongoQ.Select.HasDocumentPathAliasOverride);
    }

    [Fact]
    public void Bare_widening_cast_leaf_is_admitted_as_a_document_path_leaf()
    {
        var mongoQ = TestQuery();
        Expression<Func<Order, long>> selector = o => (long)o.Amount;

        // EF-410: a widening cast leaf is now admitted here too. MongoExpressionTranslator.TranslateOperand
        // UNWRAPS a widening conversion instead of wrapping it in a MongoConvertExpression, so the translated
        // value is a bare MongoFieldExpression — indistinguishable by node kind alone from a leaf that was never
        // cast. The tier-2 gate now re-derives "was this leaf syntactically a cast" from the ORIGINAL selector
        // body (a `UnaryExpression { NodeType: Convert }`) rather than from the already-lossy translated value,
        // so this now admits — but the RESULT is a plain MongoFieldExpression, exactly as an uncast field leaf
        // produces, so the downstream alias/tier derivation treats it identically: DocumentPath tier, aliased to
        // the field's own name ("Amount"), not the reserved "_v" a computed (arithmetic/count/MongoConvertExpression)
        // leaf gets. The shaper reads the raw stored value back and the driver/native BSON readers widen it to the
        // requested CLR type (long) same as any other numeric read. See
        // NativeCastTests.Widening_cast_bare_projection_leaf_goes_native /
        // NativeCastTests.Widening_cast_projection_leaf_now_goes_native for the end-to-end functional pin.
        Assert.True(NativeProjectionBinder.TryPopulateNativeProjection(mongoQ, selector));

        var projection = Assert.Single(mongoQ.Select.Projection);
        Assert.Equal("Amount", projection.Alias);
        Assert.IsType<MongoFieldExpression>(projection.Expression);
        Assert.Equal(ProjectionAliasTier.DocumentPath, mongoQ.Select.BareProjectionTier);
    }

    [Fact]
    public void Bare_constant_leaf_is_declined()
    {
        var mongoQ = TestQuery();
        Expression<Func<Order, int>> selector = _ => 0;

        // Deliberate, and it is the tier-2 gate's own mutation target. A falsy constant renders as a BARE VALUE,
        // which $project reads as an inclusion/exclusion FLAG rather than a literal. Gating tier 2 on the
        // resulting NODE KIND — an arithmetic MongoBinaryExpression or a MongoConvertExpression, both of which
        // render as DOCUMENTS — is what keeps this out; gating on "the leaf translated" would not.
        //
        // WHAT THE MUTATION PRODUCES FOR A *BARE* BODY IS NOT THE WRAPPED MEASUREMENT, AND THIS COMMENT USED TO
        // QUOTE THE WRAPPED ONE. A wrapped `new { b.Title, X = 0 }` hard-fails ("Cannot do exclusion on field X
        // in inclusion projection") because the falsy flag sits beside an inclusion. A bare `0` has no sibling,
        // so nothing is mixed: MEASURED, the relaxed gate emits a legal pure-exclusion
        // {"$project": {"_v": 0, "_id": 0}} and the VALUES still come back correct, because the shaper folds a
        // constant client-side. The damage is a junk pipeline, not an abort — which is why the functional pin,
        // NativeComputedBareProjectionTests.Bare_constant_leaf_is_not_admitted_by_the_tier_2_node_kind_gate,
        // asserts the emitted MQL rather than values.
        Assert.False(NativeProjectionBinder.TryPopulateNativeProjection(mongoQ, selector));
        Assert.Empty(mongoQ.Select.Projection);
        Assert.False(mongoQ.Select.IsBareProjection);
    }

    [Fact]
    public void Bare_comparison_leaf_is_declined_although_it_is_a_MongoBinaryExpression()
    {
        var mongoQ = TestQuery();
        Expression<Func<Order, bool>> selector = o => o.Amount > 2;

        // The reason the tier-2 gate matches on the OPERATOR rather than on the node type alone. A comparison
        // also translates to a MongoBinaryExpression, but it renders as a boolean predicate rather than an
        // arithmetic value, so `{_v: <false>}` is the same BARE-VALUE-as-a-flag hazard the constant case above
        // pins — and, as that case records, for a BARE body the measured consequence is a junk pure-exclusion
        // projection rather than the abort the WRAPPED spelling produces. (This shape is declined one gate
        // earlier today — TryTranslateLeaf's own node-kind check never admits a comparison — so this is a second
        // net, deliberately: two gates, one shape, neither relying on the other.)
        Assert.False(NativeProjectionBinder.TryPopulateNativeProjection(mongoQ, selector));
        Assert.Empty(mongoQ.Select.Projection);
        Assert.False(mongoQ.Select.IsBareProjection);
    }

    [Fact]
    public void Bare_collection_count_leaf_is_admitted_under_the_reserved_alias_and_the_synthetic_tier()
    {
        var mongoQ = TestQuery();

        // THE LOWERED SPELLING, not the source one — and the difference is now load-bearing rather than
        // cosmetic. Since the final fix round, arm 1a asks the A4-0 rewrite's OWN matcher whether it can reach
        // this body, and that matcher recognizes the post-nav-expansion form EF actually produces
        // (Queryable.Count over Queryable.AsQueryable over EF.Property). A source-spelled `o => o.Lines.Count`
        // still TRANSLATES to a MongoSizeExpression — the translator matches `.Count` by name — but it is not a
        // shape the rewrite could ever be handed at runtime, so testing the gate with it would assert the gate
        // against an input the production path never produces.
        Expression<Func<Order, int>> selector = o => EF.Property<List<Line>>(o, "Lines").AsQueryable().Count();

        // A4-2 — THE ADMISSION THIS WHOLE SLICE EXISTS FOR, and the one that makes the A4-0 prerequisite live.
        // A count leaf is the shape whose bare `$size` over a MISSING or explicitly-null array aborts the whole
        // aggregate under the DEFAULT Native mode on a late fallback — the exact defect that got tier 2 reverted
        // at step 3a. It is admitted here only because NullCoalesceSyntheticBareCountBody now rewrites the
        // pushed-down body to its `$ifNull` form, so the driver's un-stripped push-down renders the SAME MQL
        // native does. Pinned end-to-end over a ragged fixture, in all three modes plus the late-decline route,
        // by NativeComputedBareProjectionTests.
        //
        // GATE 1a admits it (the top node IS a MongoSizeExpression); the subtree check deliberately does NOT
        // run for this arm, because "the body IS the count" is exactly the reach the A4-0 rewrite has. The three
        // cases below — a count NESTED under arithmetic or a cast — stay declined by GATE 2 for the mirror-image
        // reason: the rewrite matches only a body that IS the count, never one that merely contains one.
        Assert.True(NativeProjectionBinder.TryPopulateNativeProjection(mongoQ, selector));

        var projection = Assert.Single(mongoQ.Select.Projection);
        Assert.Equal("_v", projection.Alias);
        Assert.IsType<MongoSizeExpression>(projection.Expression);

        Assert.True(mongoQ.Select.IsBareProjection);
        Assert.Equal(ProjectionAliasTier.Synthetic, mongoQ.Select.BareProjectionTier);
        // The tier, not just the alias: the late-fallback strip is TIER-conditional and must NOT fire here, or
        // the shaper is handed whole documents while still reading `_v`.
        Assert.False(mongoQ.Select.HasDocumentPathAliasOverride);
    }

    [Fact]
    public void Bare_FILTERED_collection_count_leaf_is_admitted_too()
    {
        var mongoQ = TestQuery();
        Expression<Func<Order, int>> selector = o => o.Lines.Count(l => l.Quantity > 0);

        // MongoFilteredSizeExpression is a SEPARATE node kind from MongoSizeExpression (the "sibling, not a
        // flag" decision recorded in Query/AGENTS.md), so gate 1a has to name it explicitly and a test that
        // covered only the unfiltered one would leave that arm unpinned.
        //
        // It is admitted WITHOUT any rewrite of its own, and that is MEASURED rather than assumed: the driver
        // renders a filtered count as `{$sum: {$map: {input: "$Lines", …}}}`, and `$map` over a MISSING or
        // explicitly-null array yields missing rather than aborting the aggregate the way `$size` does — so the
        // un-stripped fallback answers 0 for the ragged rows on its own. Measured over all four array states in
        // every mode by NativeComputedBareProjectionTests.Bare_filtered_count_leaf_goes_native_for_every_array_state.
        Assert.True(NativeProjectionBinder.TryPopulateNativeProjection(mongoQ, selector));

        var projection = Assert.Single(mongoQ.Select.Projection);
        Assert.Equal("_v", projection.Alias);
        Assert.IsType<MongoFilteredSizeExpression>(projection.Expression);
        Assert.Equal(ProjectionAliasTier.Synthetic, mongoQ.Select.BareProjectionTier);
        Assert.False(mongoQ.Select.HasDocumentPathAliasOverride);
    }

    [Fact]
    public void Bare_collection_count_leaf_through_an_owned_reference_HOP_is_declined()
    {
        var mongoQ = TestQuery();

        // The lowered spelling again, for the reason the root-declared case above states — and here it matters
        // twice over: the hop is now declined by the rewrite's OWN IsNavigationOnParameter check (the receiver
        // is `o.Address`, not the selector parameter), so a source-spelled body would decline at the matcher's
        // very FIRST test instead and this case would stop exercising the hop dimension at all.
        Expression<Func<Order, int>> selector =
            o => EF.Property<List<Note>>(o.Address, "Notes").AsQueryable().Count();

        // THE C1 REGRESSION PIN, at unit level. This translates to a MongoSizeExpression exactly like the
        // root-declared `o.Lines.Count` above, so gate 1a's NODE KIND test admits it — what declines it is
        // IsFallbackSafeBareSizeLeaf consulting the A4-0 rewrite's own matcher, whose IsNavigationOnParameter
        // accepts only a navigation whose receiver IS the selector parameter. Admitting a hop would commit a
        // projection the rewrite cannot coalesce — and the Synthetic tier suppresses the strip, so the driver's
        // un-stripped push-down renders a bare $size and aborts on a missing or explicitly-null array. Measured
        // end-to-end over a ragged hop fixture by
        // NativeComputedBareProjectionTests.Bare_count_leaf_through_an_owned_reference_HOP_is_declined_and_answers_correctly.
        Assert.False(NativeProjectionBinder.TryPopulateNativeProjection(mongoQ, selector));
        Assert.Empty(mongoQ.Select.Projection);
        Assert.False(mongoQ.Select.IsBareProjection);
        Assert.Null(mongoQ.Select.BareProjectionTier);
    }

    [Fact]
    public void Bare_FILTERED_collection_count_leaf_through_an_owned_reference_HOP_is_declined_too()
    {
        var mongoQ = TestQuery();
        Expression<Func<Order, int>> selector = o => o.Address.Notes.Count(n => n.Text == "x");

        // The filtered kind is held to the SAME dotted-path rule, and that is a deliberate uniformity rather
        // than a necessity — a filtered count is structurally protected without any rewrite (the driver renders
        // it {$sum: {$map: …}}, and $map tolerates a missing array where $size does not), so this shape COULD
        // have been admitted. One rule over both size kinds is checkable by reading one method; two rules would
        // have to be kept matched against two different protection mechanisms. Pinned so that widening it later
        // is a deliberate choice.
        Assert.False(NativeProjectionBinder.TryPopulateNativeProjection(mongoQ, selector));
        Assert.Empty(mongoQ.Select.Projection);
        Assert.False(mongoQ.Select.IsBareProjection);
    }

    [Fact]
    public void Bare_collection_count_leaf_over_a_navigation_type_the_rewrite_cannot_build_an_empty_for_is_declined()
    {
        // THE THIRD INSTANCE OF THE RECURRING DEFECT CLASS, pinned at the gate. An ISet<Line> navigation carries
        // a perfectly NON-DOTTED array path ("Lines"), so the gate's previous ARRAY-PATH-only test admitted it —
        // but the A4-0 rewrite declines it on a different dimension entirely: List<Line> is not assignable to
        // ISet<Line>, so TryCreateEmptyCollection cannot build the `??` substitute and the body is left
        // un-coalesced. The Synthetic tier then suppresses the strip and the driver's un-stripped push-down
        // renders a bare {"$size": "$Lines"}, which aborts on a missing or explicitly-null array. MEASURED
        // end-to-end, base vs. that version, on three routes — see
        // NativeComputedBareProjectionTests.Set_typed_collection_navigation_bare_count_is_declined_and_answers_correctly.
        //
        // What declines it now is that IsFallbackSafeBareSizeLeaf CALLS the rewrite's own matcher, so the gate
        // cannot know about one dimension and not another: it knows exactly what the rewrite knows.
        var mongoQ = TestSetQuery();
        Expression<Func<SetOrder, int>> selector =
            o => EF.Property<ISet<Line>>(o, "Lines").AsQueryable().Count();

        Assert.False(NativeProjectionBinder.TryPopulateNativeProjection(mongoQ, selector));
        Assert.Empty(mongoQ.Select.Projection);
        Assert.False(mongoQ.Select.IsBareProjection);
        Assert.Null(mongoQ.Select.BareProjectionTier);

        // CONTROL, on the SAME model: an ordinary bare leaf still binds, so the decline above cannot be blamed
        // on a fixture EF failed to build. Without this a broken SetOrder mapping would make the test pass for
        // the wrong reason.
        var control = TestSetQuery();
        Expression<Func<SetOrder, string>> plain = o => o.Country;
        Assert.True(NativeProjectionBinder.TryPopulateNativeProjection(control, plain));
        Assert.Equal("Country", Assert.Single(control.Select.Projection).Alias);
    }

    [Fact]
    public void Bare_arithmetic_OVER_a_collection_count_is_declined_by_the_subtree_check()
    {
        var mongoQ = TestQuery();
        Expression<Func<Order, int>> selector = o => o.Lines.Count * 2;

        // THE CASE THE TOP-NODE GATE ALONE LETS THROUGH, and it is not a hypothetical: A4-1 shipped without the
        // subtree check and this shape re-opened the tier-2 revert's own defect. The top node here IS an
        // arithmetic MongoBinaryExpression, so gate 1 admits it; its LEFT operand is a MongoSizeExpression, so
        // the driver's un-stripped push-down renders a bare `$size` and aborts on a missing or explicitly-null
        // array — under the DEFAULT Native mode on the late-decline route, and under explicit DriverLinq with no
        // decline at all. Pinned end-to-end over a ragged fixture by
        // NativeComputedBareProjectionTests.Bare_arithmetic_over_a_collection_count_is_declined_and_answers_correctly.
        Assert.False(NativeProjectionBinder.TryPopulateNativeProjection(mongoQ, selector));
        Assert.Empty(mongoQ.Select.Projection);
        Assert.False(mongoQ.Select.IsBareProjection);
    }

    [Fact]
    public void Bare_cast_OVER_a_collection_count_is_declined_by_the_subtree_check()
    {
        var mongoQ = TestQuery();
        Expression<Func<Order, long>> selector = o => (long)(o.Lines.Count / 2.0);

        // The same hole through the CAST arm rather than the arithmetic one — a separate case because gate 1 has
        // two admitting shapes and a subtree check wired into only one of them would be silently half-applied.
        // The `/ 2.0` is what forces a genuine narrowing MongoConvertExpression at the top (a widening cast is
        // unwrapped by TranslateOperand and declines a gate earlier, for unrelated reasons — see
        // Bare_widening_cast_leaf_is_still_declined_and_never_reaches_the_alias_derivation).
        Assert.False(NativeProjectionBinder.TryPopulateNativeProjection(mongoQ, selector));
        Assert.Empty(mongoQ.Select.Projection);
        Assert.False(mongoQ.Select.IsBareProjection);
    }

    [Fact]
    public void Bare_arithmetic_over_a_FILTERED_collection_count_is_declined_by_the_subtree_check()
    {
        var mongoQ = TestQuery();
        Expression<Func<Order, int>> selector = o => o.Lines.Count(l => l.Quantity > 0) * 2;

        // MongoFilteredSizeExpression is a separate node kind from MongoSizeExpression (the "sibling, not a
        // flag" decision recorded in Query/AGENTS.md), so a subtree check that named only the unfiltered one
        // would admit this. It does not: IsArrayFreeComputedSubtree is an ALLOW-LIST, and neither size kind is
        // on it. That is also why this case cannot be folded into the one above — it is the arm that proves the
        // exclusion comes from the catch-all rather than from an enumeration someone has to keep complete.
        Assert.False(NativeProjectionBinder.TryPopulateNativeProjection(mongoQ, selector));
        Assert.Empty(mongoQ.Select.Projection);
        Assert.False(mongoQ.Select.IsBareProjection);
    }

    [Fact]
    public void Bare_whole_entity_parameter_is_declined_by_the_arm()
    {
        var mongoQ = TestQuery();
        Expression<Func<Order, Order>> selector = o => o;

        // The positive control at unit level. TranslateSelect returns a `x => x` Select unchanged before the
        // binder is ever called, so this shape does not reach here in practice — but the arm must not be able to
        // match a bare ParameterExpression even if it did, or whole-entity queries would start emitting a
        // $project keyed by nothing.
        Assert.False(NativeProjectionBinder.TryPopulateNativeProjection(mongoQ, selector));
        Assert.Empty(mongoQ.Select.Projection);
        Assert.False(mongoQ.Select.IsBareProjection);
    }

    [Fact]
    public void A_bare_body_on_an_already_populated_projection_is_declined()
    {
        var mongoQ = TestQuery();
        Expression<Func<Order, string>> first = o => o.Country;
        Assert.True(NativeProjectionBinder.TryPopulateNativeProjection(mongoQ, first));

        Expression<Func<Order, int>> second = o => o.Amount;

        // This is what makes the alias carrier provably WRITE-ONCE, and therefore what makes
        // AddProjectionAliasOverride safe to use Dictionary.Add: the one writer cannot commit a second bare
        // override on the same select. Without the guard the second call would append a second projection AND
        // attempt a second override write for the one bare sentinel key.
        Assert.False(NativeProjectionBinder.TryPopulateNativeProjection(mongoQ, second));

        // And it declines cleanly — the first projection and the first override are untouched.
        var projection = Assert.Single(mongoQ.Select.Projection);
        Assert.Equal("Country", projection.Alias);
        Assert.True(mongoQ.Select.TryGetProjectionAlias(null, out var alias));
        Assert.Equal("Country", alias);
    }

    // ── EF-322 slice A4 (A4-0): the CapturedExpression null-coalescing rewrite ───────────────────────────
    //
    // These exercise NativeProjectionBinder.NullCoalesceSyntheticBareCountBody rather than
    // TryPopulateNativeProjection, because the rewrite CANNOT live inside the binder's commit block:
    // MongoQueryableMethodTranslatingExpressionVisitor.VisitMethodCall re-assigns
    // CapturedExpression = _finalExpression immediately after every translated Queryable call — including the
    // Select whose translation runs the binder — so a write from inside the binder is overwritten before
    // anything reads it. MEASURED with a marker expression: the marker never reached the driver-LINQ bridge.
    // The rewrite is applied at that assignment instead, and its input is the Synthetic-tier override the
    // binder registers, so these tests set that override up by hand — the tier is not yet REACHABLE (no
    // producer writes Synthetic until the tier-2 admission lands), which is exactly why the rewrite ships
    // first and is inert.
    //
    // The captured spelling used below is the one EF's nav-expansion actually produces, MEASURED on a running
    // query rather than assumed: Select(b => b.Posts.Count) is captured as
    // Select(b => Queryable.Count(Queryable.AsQueryable(EF.Property<List<Post>>(b, "Posts")))).

    private static MongoQueryExpression SyntheticBareQuery(Expression captured)
    {
        var mongoQ = TestQuery();
        mongoQ.Select.AddProjectionAliasOverride(
            MongoSelectDefinition.BareProjectionMemberKey, "_v", ProjectionAliasTier.Synthetic);
        mongoQ.CapturedExpression = captured;
        return mongoQ;
    }

    private static Expression CapturedSelect<T>(Expression<Func<Order, T>> selector)
        => Array.Empty<Order>().AsQueryable().Select(selector).Expression;

    private static LambdaExpression SelectorOf(Expression captured)
        => ((MethodCallExpression)captured).Arguments[1].UnwrapLambdaFromQuote();

    private static readonly MethodInfo EfPropertyMethod =
        typeof(EF).GetMethod(nameof(EF.Property), BindingFlags.Public | BindingFlags.Static)!;

    private static MethodInfo QueryableMethod(string name, int parameterCount, Type elementType)
        => typeof(Queryable).GetMethods()
            .Single(m => m.Name == name && m.IsGenericMethod && m.GetParameters().Length == parameterCount)
            .MakeGenericMethod(elementType);

    // Hand-built rather than compiler-emitted, because C# cannot spell EF.Property<T> with a `Type` variable —
    // and the navigation's DECLARED CLR type is precisely the axis under test.
    private static Expression CapturedSelectOfType(Type navigationType)
    {
        var parameter = Expression.Parameter(typeof(Order), "o");
        var navigation = Expression.Call(
            EfPropertyMethod.MakeGenericMethod(navigationType), parameter, Expression.Constant("Lines"));
        var body = Expression.Call(
            QueryableMethod(nameof(Queryable.Count), 1, typeof(Line)),
            Expression.Call(QueryableMethod(nameof(Queryable.AsQueryable), 1, typeof(Line)), navigation));

        return Array.Empty<Order>().AsQueryable()
            .Select(Expression.Lambda<Func<Order, int>>(body, parameter)).Expression;
    }

    private static Expression UnderTerminator(Expression select, string terminator)
        => Expression.Call(QueryableMethod(terminator, 1, typeof(int)), select);

    [Fact]
    public void Bare_collection_navigation_Count_body_is_rewritten_to_its_null_coalesced_form()
    {
        // THE PREREQUISITE. The driver renders a bare {"$size": "$Lines"} for this body, and $size against a
        // MISSING or explicitly-null array aborts the whole aggregate — so a Synthetic-tier bare count that
        // ever reaches the driver-LINQ bridge (a late native-factory decline under the DEFAULT Native mode, or
        // an explicit DriverLinq) is a hard failure on ragged data. (b.Lines ?? new List<Line>()).Count is
        // MEASURED to render {"$size": {"$ifNull": ["$Lines", []]}} — byte-identical to native's own rendering.
        var captured = CapturedSelect(o => EF.Property<List<Line>>(o, "Lines").AsQueryable().Count());
        var mongoQ = SyntheticBareQuery(captured);

        var rewritten = NativeProjectionBinder.NullCoalesceSyntheticBareCountBody(
            mongoQ.CapturedExpression, mongoQ.Select);

        Assert.NotSame(captured, rewritten);

        // Assert the SHAPE of the rewritten body, not merely that something changed: Count over AsQueryable
        // over a Coalesce whose left operand is the untouched navigation access and whose right operand is a
        // freshly constructed empty collection of the navigation's own CLR type.
        var count = Assert.IsAssignableFrom<MethodCallExpression>(SelectorOf(rewritten!).Body);
        Assert.Equal(nameof(Queryable.Count), count.Method.Name);
        var asQueryable = Assert.IsAssignableFrom<MethodCallExpression>(count.Arguments[0]);
        Assert.Equal(nameof(Queryable.AsQueryable), asQueryable.Method.Name);
        var coalesce = Assert.IsAssignableFrom<BinaryExpression>(asQueryable.Arguments[0]);
        Assert.Equal(ExpressionType.Coalesce, coalesce.NodeType);

        var originalNavigation =
            ((MethodCallExpression)((MethodCallExpression)SelectorOf(captured).Body).Arguments[0]).Arguments[0];
        Assert.Same(originalNavigation, coalesce.Left);
        Assert.Equal(typeof(List<Line>), Assert.IsAssignableFrom<NewExpression>(coalesce.Right).Type);

        // The lambda parameter is REUSED, not re-created — a rebuilt parameter would leave the rewritten body
        // referring to a parameter the Select no longer binds, which is not something a shape assertion on the
        // Coalesce alone would catch.
        Assert.Same(SelectorOf(captured).Parameters[0], SelectorOf(rewritten!).Parameters[0]);
    }

    [Fact]
    public void Bare_LongCount_body_is_rewritten_too()
    {
        // LongCount is admitted by the same gate as Count and was untested when this rewrite first shipped.
        // It is not a spelling nobody writes: EF lowers `b.Lines.LongCount()` to exactly this shape, and a
        // rewrite that silently covered only Count would leave the LongCount spelling aborting on ragged data.
        var captured = CapturedSelect(o => EF.Property<List<Line>>(o, "Lines").AsQueryable().LongCount());
        var mongoQ = SyntheticBareQuery(captured);

        var rewritten = NativeProjectionBinder.NullCoalesceSyntheticBareCountBody(
            mongoQ.CapturedExpression, mongoQ.Select);

        Assert.NotSame(captured, rewritten);
        var count = Assert.IsAssignableFrom<MethodCallExpression>(SelectorOf(rewritten!).Body);
        Assert.Equal(nameof(Queryable.LongCount), count.Method.Name);
        var asQueryable = Assert.IsAssignableFrom<MethodCallExpression>(count.Arguments[0]);
        Assert.Equal(ExpressionType.Coalesce,
            Assert.IsAssignableFrom<BinaryExpression>(asQueryable.Arguments[0]).NodeType);
    }

    [Theory]
    [InlineData(typeof(ICollection<Line>))]
    [InlineData(typeof(IEnumerable<Line>))]
    [InlineData(typeof(IList<Line>))]
    [InlineData(typeof(HashSet<Line>))]
    public void A_non_List_navigation_CLR_type_is_rewritten_against_an_assignable_empty_collection(Type navigationType)
    {
        // THE CORRECTNESS CASE THIS REWRITE SHIPPED WITHOUT. EF's nav-expansion spells the navigation
        // EF.Property<TNavClrType>(b, "…") using the DECLARED property type, so a model declaring
        // ICollection<T> / IList<T> / IEnumerable<T> reaches this rewrite with an INTERFACE-typed navigation —
        // and this provider's own suite already models one (OwnedEntityTests.PersonWithIEnumerableLocations).
        // The first version of this rewrite declined every interface type, which is not a coverage gap but the
        // exact bare-$size abort this whole change exists to close, left open for a whole family of ordinary
        // models. HashSet<Line> is the control on the other side of the branch: constructible, so it must be
        // coalesced against ITSELF rather than against a List.
        var captured = CapturedSelectOfType(navigationType);
        var mongoQ = SyntheticBareQuery(captured);

        var rewritten = NativeProjectionBinder.NullCoalesceSyntheticBareCountBody(
            mongoQ.CapturedExpression, mongoQ.Select);

        Assert.NotSame(captured, rewritten);
        var count = Assert.IsAssignableFrom<MethodCallExpression>(SelectorOf(rewritten!).Body);
        var asQueryable = Assert.IsAssignableFrom<MethodCallExpression>(count.Arguments[0]);
        var coalesce = Assert.IsAssignableFrom<BinaryExpression>(asQueryable.Arguments[0]);

        // The Coalesce's own type must stay the NAVIGATION's declared type — that is what keeps the rebuilt
        // AsQueryable call valid — while the substitute is a constructible collection assignable to it.
        Assert.Equal(navigationType, coalesce.Type);
        var substitute = Assert.IsAssignableFrom<NewExpression>(coalesce.Right).Type;
        Assert.True(navigationType.IsAssignableFrom(substitute));
        Assert.Equal(navigationType.IsInterface ? typeof(List<Line>) : navigationType, substitute);
    }

    [Theory]
    [InlineData(typeof(ISet<Line>))]
    [InlineData(typeof(IReadOnlySet<Line>))]
    public void A_navigation_CLR_type_no_substitute_is_assignable_to_is_left_untouched(Type navigationType)
    {
        // THE DECLINE SIDE OF THE SAME BRANCH, which had NO ROW until the final review. The theory above covers
        // ICollection<T> / IEnumerable<T> / IList<T> — precisely the three interfaces List<T> IS assignable to,
        // i.e. the three for which TryCreateEmptyCollection SUCCEEDS — so the mutation recorded against it
        // ("the interface-typed-navigation fallback removed") could only ever have measured the success path.
        //
        // ISet<T> and IReadOnlySet<T> are ordinary EF-supported collection types List<T> is NOT assignable to.
        // They are the boundary the gate now shares with this rewrite: a decline here is also a decline at
        // IsFallbackSafeBareSizeLeaf, which is what stops the un-rewritten bare $size from ever being committed
        // (see Bare_collection_count_leaf_over_a_navigation_type_the_rewrite_cannot_build_an_empty_for_is_declined).
        var captured = CapturedSelectOfType(navigationType);
        var mongoQ = SyntheticBareQuery(captured);

        Assert.Same(captured,
            NativeProjectionBinder.NullCoalesceSyntheticBareCountBody(mongoQ.CapturedExpression, mongoQ.Select));
    }

    [Theory]
    [InlineData(nameof(Queryable.First))]
    [InlineData(nameof(Queryable.SingleOrDefault))]
    [InlineData(nameof(Queryable.Last))]
    public void A_bare_count_under_a_cardinality_terminator_is_rewritten_through_the_terminator(string terminator)
    {
        // THE SECOND NAVIGATION SHAPE, which shipped with no coverage at all: `Select(b => b.Posts.Count).First()`
        // captures as First(Select(…)), so the pushed-down Select is not the outermost node. Deleting the whole
        // branch left every other test green, which is exactly why this one exists.
        var select = CapturedSelect(o => EF.Property<List<Line>>(o, "Lines").AsQueryable().Count());
        var captured = UnderTerminator(select, terminator);
        var mongoQ = SyntheticBareQuery(captured);

        var rewritten = NativeProjectionBinder.NullCoalesceSyntheticBareCountBody(
            mongoQ.CapturedExpression, mongoQ.Select);

        Assert.NotSame(captured, rewritten);
        var rebuiltTerminator = Assert.IsAssignableFrom<MethodCallExpression>(rewritten);

        // The terminator's METHOD — generic argument included — must be UNCHANGED. StripPushedDownSelect
        // retargets its terminator because it REMOVES the Select and the element type therefore changes; this
        // rewrite keeps the Select and touches only its body, whose type is unchanged, so retargeting here
        // would be wrong rather than merely redundant. Asserting the MethodInfo pins that distinction.
        Assert.Same(((MethodCallExpression)captured).Method, rebuiltTerminator.Method);

        var innerSelect = Assert.IsAssignableFrom<MethodCallExpression>(rebuiltTerminator.Arguments[0]);
        var count = Assert.IsAssignableFrom<MethodCallExpression>(SelectorOf(innerSelect).Body);
        var asQueryable = Assert.IsAssignableFrom<MethodCallExpression>(count.Arguments[0]);
        Assert.Equal(ExpressionType.Coalesce,
            Assert.IsAssignableFrom<BinaryExpression>(asQueryable.Arguments[0]).NodeType);
    }

    [Fact]
    public void A_non_count_body_under_a_cardinality_terminator_is_left_untouched()
    {
        // The terminator branch inherits the SAME body gate as the outermost-Select branch, rather than having a
        // second, independently-spelled copy of it — which is what this pins.
        var captured = UnderTerminator(CapturedSelect(o => o.Amount * 2), nameof(Queryable.First));
        var mongoQ = SyntheticBareQuery(captured);

        Assert.Same(captured,
            NativeProjectionBinder.NullCoalesceSyntheticBareCountBody(mongoQ.CapturedExpression, mongoQ.Select));
    }

    [Fact]
    public void Bare_arithmetic_body_is_left_untouched()
    {
        // Scope pin, and it is a MEASURED boundary rather than a conservative choice: the driver renders
        // arithmetic as $multiply, which never touches an array and so never aborts on a missing or
        // explicitly-null one. Rewriting it would be unrequested scope — and the tier-2 admission that lands
        // later admits arithmetic under the SAME Synthetic tier, so this test is what keeps the rewrite keyed
        // on the body SHAPE rather than on the tier alone.
        var captured = CapturedSelect(o => o.Amount * 2);
        var mongoQ = SyntheticBareQuery(captured);

        Assert.Same(captured,
            NativeProjectionBinder.NullCoalesceSyntheticBareCountBody(mongoQ.CapturedExpression, mongoQ.Select));
    }

    [Fact]
    public void Bare_cast_body_is_left_untouched()
    {
        // Same boundary, the other measured-safe computed kind: a narrowing cast renders $toInt.
        var captured = CapturedSelect(o => (int)o.Weight);
        var mongoQ = SyntheticBareQuery(captured);

        Assert.Same(captured,
            NativeProjectionBinder.NullCoalesceSyntheticBareCountBody(mongoQ.CapturedExpression, mongoQ.Select));
    }

    [Fact]
    public void A_count_body_with_no_synthetic_override_is_left_untouched()
    {
        // THE INERTNESS CONTROL, and the reason this change is safe to ship ahead of the capability it exists
        // for: nothing in the tree registers a Synthetic-tier override yet, so on today's code path the
        // rewrite is unreachable and every shipped shape keeps its exact current disposition. It also pins the
        // gate as TIER data rather than a body-shape sniff — a rewrite keyed only on the body would fire here.
        var captured = CapturedSelect(o => EF.Property<List<Line>>(o, "Lines").AsQueryable().Count());
        var mongoQ = TestQuery();
        mongoQ.CapturedExpression = captured;

        Assert.Same(captured,
            NativeProjectionBinder.NullCoalesceSyntheticBareCountBody(mongoQ.CapturedExpression, mongoQ.Select));

        // A DocumentPath-tier bare override — tier 1, everything step 3a ships — is equally untouched.
        var tier1 = TestQuery();
        tier1.Select.AddProjectionAliasOverride(
            MongoSelectDefinition.BareProjectionMemberKey, "Lines", ProjectionAliasTier.DocumentPath);
        tier1.CapturedExpression = captured;

        Assert.Same(captured,
            NativeProjectionBinder.NullCoalesceSyntheticBareCountBody(tier1.CapturedExpression, tier1.Select));
    }

    [Fact]
    public void A_wrapped_count_body_is_left_untouched()
    {
        // A WRAPPED count leaf's end-to-end behaviour on a ragged array under DriverLinq is measured by
        // NativeOwnedCollectionCountTests.Wrapped_count_projection_under_DriverLinq_works_for_present_and_ragged_arrays_alike
        // — it no longer aborts, but via a different mechanism (MongoEFToLinqTranslatingExpressionVisitor's
        // embedded-collection-count coalesce), not this one. This method's own scope stays narrow: the rewrite
        // navigates to the pushed-down bare Select's own body — the same navigation
        // StripPushedDownSelect uses — precisely so a free-form tree walk cannot reach a wrapped leaf and flip
        // that pin silently.
        var captured = CapturedSelect(o => new {o.Country, N = EF.Property<List<Line>>(o, "Lines").AsQueryable().Count()});
        var mongoQ = SyntheticBareQuery(captured);

        Assert.Same(captured,
            NativeProjectionBinder.NullCoalesceSyntheticBareCountBody(mongoQ.CapturedExpression, mongoQ.Select));
    }

    [Fact]
    public void A_count_over_something_other_than_the_selector_parameter_is_left_untouched()
    {
        // A REFERENCE-collection count is captured as an EntityQueryRoot subquery, not as a navigation access
        // on the selector's own parameter, and it needs no rewrite: it reads a $lookup output, and a $lookup
        // always writes an array (never absent, never explicit null). The parameter-rooted requirement is what
        // separates the two; this stands in for it with a count over a captured local.
        //
        // The body deliberately keeps the FULL Count(AsQueryable(x)) spelling and varies only what `x` is
        // rooted at. An earlier version of this test used `other.Count()`, which declined one gate EARLIER (no
        // AsQueryable call at all) and so was VACUOUS — removing the parameter-rooted check left it green.
        // Caught by mutation, and the corrected body fails when that check is removed.
        var other = new List<Line>();
        var captured = CapturedSelect(o => other.AsQueryable().Count());
        var mongoQ = SyntheticBareQuery(captured);

        Assert.Same(captured,
            NativeProjectionBinder.NullCoalesceSyntheticBareCountBody(mongoQ.CapturedExpression, mongoQ.Select));
    }

    [Fact]
    public void A_null_captured_expression_is_returned_unchanged()
    {
        var mongoQ = TestQuery();
        mongoQ.Select.AddProjectionAliasOverride(
            MongoSelectDefinition.BareProjectionMemberKey, "_v", ProjectionAliasTier.Synthetic);

        Assert.Null(NativeProjectionBinder.NullCoalesceSyntheticBareCountBody(null, mongoQ.Select));
    }

    [Fact]
    public void A_wrapped_body_still_registers_no_alias_override()
    {
        var mongoQ = TestQuery();
        Expression<Func<Order, object>> selector = o => new {o.Country, o.Amount};

        // The inertness control for the wrapped path: a wrapped projection's alias comes from its member name,
        // so it must register nothing — an override here would make IsBareProjection/BareProjectionTier answer
        // for a projection that has no bare body, and wrongly trip the late-fallback strip
        // (NativeProjectionBinder.NullCoalesceSyntheticBareCountBody, which reads BareProjectionTier). NOTE:
        // as of EF-395 NEITHER of the two former narrowings consults IsBareProjection any more — both were
        // removed (see MongoQueryableMethodTranslatingExpressionVisitor's and NativeGroupByBinder's own
        // comments recording that) — so the strip is now the only consumer this control protects.
        Assert.True(NativeProjectionBinder.TryPopulateNativeProjection(mongoQ, selector));

        Assert.Equal(2, mongoQ.Select.Projection.Count);
        Assert.False(mongoQ.Select.IsBareProjection);
        Assert.Null(mongoQ.Select.BareProjectionTier);
        Assert.False(mongoQ.Select.TryGetProjectionAlias(null, out _));
    }
}
