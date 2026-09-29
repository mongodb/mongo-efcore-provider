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
/// <see cref="NativeProjectionBinder.TryPopulateNativeProjection"/>'s bare-selector-body arm. Asserts the derived
/// alias and its tier, not just the admit/decline boolean — a boolean-only check stays green with a wrong alias.
/// </summary>
public class NativeProjectionBinderBareBodyTests
{
    private class Order
    {
        public ObjectId Id { get; set; }
        public string Country { get; set; } = "";
        public int Amount { get; set; }
        public double Weight { get; set; }
        public int Age { get; set; }
        public double Score { get; set; }
        public int? Rank { get; set; }
        public List<string> Tags { get; set; } = null!;
        public Address Address { get; set; } = null!;
        public List<Line> Lines { get; set; } = null!;
    }

    private class Address
    {
        public string City { get; set; } = "";

        // The only dotted array path in this fixture ("Address.Notes"), which IsFallbackSafeBareSizeLeaf declines.
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
    /// Like <see cref="Order"/>, but the owned collection is an <see cref="ISet{T}"/>, which <c>List&lt;Line&gt;</c>
    /// is not assignable to, so <c>TryCreateEmptyCollection</c> declines it. The only fixture that exercises that
    /// decline side.
    /// </summary>
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

        // Registered under the bare sentinel key, which every alias-reading site consults: a bare body's
        // ProjectionMember has no last member to derive a name from.
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

        // A root PK is stored at "_id", and tier 1's alias is the path, so the alias is "_id" — which lets
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

        // A primitive collection is a mapped property, so it resolves to a MongoFieldExpression, not the
        // owned-array branch.
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

        // The leaf resolves to the dotted path "Address.City", but a dotted alias is read back as a literal key
        // while `$project: {"Address.City": …}` renders nested output, so tier 1 requires a non-dotted path.
        Assert.False(NativeProjectionBinder.TryPopulateNativeProjection(mongoQ, selector));

        Assert.Empty(mongoQ.Select.Projection);
        Assert.False(mongoQ.Select.IsBareProjection);
        Assert.Null(mongoQ.Select.BareProjectionTier);
        Assert.False(mongoQ.Select.TryGetProjectionAlias(null, out _));
    }

    // ── Tier 2: a computed bare leaf under the reserved `_v` alias ───────────────────────────────────────
    //
    // The tier is asserted, not just the alias: the late-fallback strip is tier-conditional (it must not fire for
    // Synthetic, whose `_v` is what the driver's bare push-down writes), so a DocumentPath regression would
    // silently read a missing element.

    [Fact]
    public void Bare_arithmetic_leaf_is_admitted_under_the_reserved_alias_and_the_synthetic_tier()
    {
        var mongoQ = TestQuery();
        Expression<Func<Order, int>> selector = o => o.Amount * 2;

        // Not a relaxation of tier 1: `_v` is still not whole-document readable, which is why the strip must not
        // fire for it.
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

        // TranslateOperand unwraps a widening conversion, so the leaf is a plain MongoFieldExpression and gets
        // DocumentPath tier with the field's own alias ("Amount"), not "_v". The BSON readers widen the stored
        // value to long. End-to-end: NativeCastTests.Widening_cast_bare_projection_leaf_goes_native.
        Assert.True(NativeProjectionBinder.TryPopulateNativeProjection(mongoQ, selector));

        var projection = Assert.Single(mongoQ.Select.Projection);
        Assert.Equal("Amount", projection.Alias);
        Assert.IsType<MongoFieldExpression>(projection.Expression);
        Assert.Equal(ProjectionAliasTier.DocumentPath, mongoQ.Select.BareProjectionTier);
    }

    [Fact]
    public void Bare_constant_leaf_is_admitted_under_the_reserved_synthetic_alias()
    {
        var mongoQ = TestQuery();
        Expression<Func<Order, int>> selector = _ => 0;

        // The constant/parameter arm admits unconditionally. A bare falsy constant in $project would read as an
        // exclusion flag, so RenderProject $literal-wraps it. End-to-end:
        // NativeComputedBareProjectionTests.Bare_constant_leaf_now_goes_native_via_the_project_literal_wrap.
        Assert.True(NativeProjectionBinder.TryPopulateNativeProjection(mongoQ, selector));
        var projection = Assert.Single(mongoQ.Select.Projection);
        Assert.IsType<MongoConstantExpression>(projection.Expression);
        Assert.Equal(ProjectionAliasTier.Synthetic, mongoQ.Select.BareProjectionTier);
    }

    [Fact]
    public void Bare_comparison_leaf_is_admitted_under_the_reserved_synthetic_alias()
    {
        var mongoQ = TestQuery();
        Expression<Func<Order, bool>> selector = o => o.Amount > 2;

        // Renders as an operator document ({ $gt: [...] }), never a bare flag value.
        Assert.True(NativeProjectionBinder.TryPopulateNativeProjection(mongoQ, selector));

        var projection = Assert.Single(mongoQ.Select.Projection);
        Assert.Equal("_v", projection.Alias);
        Assert.Equal(
            MongoBinaryOperator.GreaterThan,
            Assert.IsType<MongoBinaryExpression>(projection.Expression).Operator);
        Assert.Equal(ProjectionAliasTier.Synthetic, mongoQ.Select.BareProjectionTier);
        Assert.Equal(NativeRoute.Projection, mongoQ.Select.Route);
    }

    [Fact]
    public void Bare_lifted_relational_comparison_leaf_is_declined()
    {
        var mongoQ = TestQuery();
        Expression<Func<Order, bool>> selector = o => o.Rank < 2;

        // .NET answers false for null < 2; aggregation $lt orders null/missing below every number and answers true.
        Assert.False(NativeProjectionBinder.TryPopulateNativeProjection(mongoQ, selector));
        Assert.Empty(mongoQ.Select.Projection);
        Assert.False(mongoQ.Select.IsBareProjection);
    }

    [Fact]
    public void Bare_string_predicate_leaf_is_admitted_under_the_reserved_synthetic_alias()
    {
        var mongoQ = TestQuery();
        Expression<Func<Order, bool>> selector = o => o.Country.StartsWith("a");

        Assert.True(NativeProjectionBinder.TryPopulateNativeProjection(mongoQ, selector));

        var projection = Assert.Single(mongoQ.Select.Projection);
        Assert.Equal("_v", projection.Alias);
        Assert.Equal(MongoRegexKind.StartsWith, Assert.IsType<MongoRegexExpression>(projection.Expression).Kind);
        Assert.True(mongoQ.Select.TryGetProjectionAlias(null, out var alias));
        Assert.Equal("_v", alias);
        Assert.Equal(ProjectionAliasTier.Synthetic, mongoQ.Select.BareProjectionTier);
        Assert.Equal(NativeRoute.Projection, mongoQ.Select.Route);
    }

    [Fact]
    public void Bare_negated_string_predicate_leaf_is_admitted_as_a_self_negated_regex()
    {
        var mongoQ = TestQuery();
        Expression<Func<Order, bool>> selector = o => !o.Country.Contains("a");

        Assert.True(NativeProjectionBinder.TryPopulateNativeProjection(mongoQ, selector));

        var projection = Assert.Single(mongoQ.Select.Projection);
        Assert.Equal("_v", projection.Alias);
        Assert.True(Assert.IsType<MongoRegexExpression>(projection.Expression).Negated);
    }

    [Fact]
    public void Bare_string_Length_leaf_is_admitted_under_the_reserved_synthetic_alias()
    {
        var mongoQ = TestQuery();
        // Nullable: a non-nullable Length would read a null string's length as 0 (see the next test).
        Expression<Func<Order, int?>> selector = o => o.Country.Length;

        Assert.True(NativeProjectionBinder.TryPopulateNativeProjection(mongoQ, selector));

        var projection = Assert.Single(mongoQ.Select.Projection);
        Assert.Equal("_v", projection.Alias);
        Assert.IsType<MongoStringLengthExpression>(projection.Expression);
        Assert.True(mongoQ.Select.TryGetProjectionAlias(null, out var alias));
        Assert.Equal("_v", alias);
        Assert.Equal(ProjectionAliasTier.Synthetic, mongoQ.Select.BareProjectionTier);
        Assert.Equal(NativeRoute.Projection, mongoQ.Select.Route);
    }

    [Fact]
    public void Bare_non_nullable_string_Length_leaf_over_a_possibly_null_string_declines()
    {
        var mongoQ = TestQuery();
        Expression<Func<Order, int>> selector = o => o.Country.Length;

        Assert.False(NativeProjectionBinder.TryPopulateNativeProjection(mongoQ, selector));
    }

    [Fact]
    public void Bare_collection_count_leaf_is_admitted_under_the_reserved_alias_and_the_synthetic_tier()
    {
        var mongoQ = TestQuery();

        // The lowered spelling EF's nav-expansion produces (Queryable.Count over AsQueryable over EF.Property):
        // arm 1a asks the null-coalescing rewrite's own matcher, which only recognizes this form. A source-spelled
        // `o => o.Lines.Count` still translates to MongoSizeExpression but never reaches the rewrite at runtime.
        Expression<Func<Order, int>> selector = o => EF.Property<List<Line>>(o, "Lines").AsQueryable().Count();

        // A bare `$size` over a missing/null array aborts the aggregate on a late fallback under Native. It is
        // admitted only because NullCoalesceSyntheticBareCountBody rewrites the pushed-down body to its `$ifNull`
        // form, so the driver's push-down renders the same MQL as native (pinned end-to-end by
        // NativeComputedBareProjectionTests). Gate 1a skips the subtree check because the rewrite reaches exactly
        // a body that is the count; a count nested under arithmetic or a cast stays declined (tests below).
        Assert.True(NativeProjectionBinder.TryPopulateNativeProjection(mongoQ, selector));

        var projection = Assert.Single(mongoQ.Select.Projection);
        Assert.Equal("_v", projection.Alias);
        Assert.IsType<MongoSizeExpression>(projection.Expression);

        Assert.True(mongoQ.Select.IsBareProjection);
        Assert.Equal(ProjectionAliasTier.Synthetic, mongoQ.Select.BareProjectionTier);
        // The late-fallback strip is tier-conditional and must not fire here, or the shaper gets whole documents
        // while reading `_v`.
        Assert.False(mongoQ.Select.HasDocumentPathAliasOverride);
    }

    [Fact]
    public void Bare_FILTERED_collection_count_leaf_is_admitted_too()
    {
        var mongoQ = TestQuery();
        Expression<Func<Order, int>> selector = o => o.Lines.Count(l => l.Quantity > 0);

        // MongoFilteredSizeExpression is a separate node kind, so gate 1a must name it. No rewrite needed: the
        // driver renders a filtered count as `{$sum: {$map: …}}`, and $map over a missing/null array yields
        // missing instead of aborting. Pinned by
        // NativeComputedBareProjectionTests.Bare_filtered_count_leaf_goes_native_for_every_array_state.
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

        // Lowered spelling so the hop is declined by the rewrite matcher's IsNavigationOnParameter check (receiver
        // is `o.Address`) rather than at its first test.
        Expression<Func<Order, int>> selector =
            o => EF.Property<List<Note>>(o.Address, "Notes").AsQueryable().Count();

        // Gate 1a's node-kind test admits this MongoSizeExpression; IsFallbackSafeBareSizeLeaf declines it via the
        // rewrite's matcher, which accepts only a navigation on the selector parameter. Admitting it would commit
        // a projection the rewrite can't coalesce, and the driver's un-stripped push-down would abort on a
        // missing/null array. End-to-end:
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

        // Held to the same dotted-path rule for uniformity, although $map would tolerate a missing array: one rule
        // over both size kinds is easier to verify. Pinned so widening it is deliberate.
        Assert.False(NativeProjectionBinder.TryPopulateNativeProjection(mongoQ, selector));
        Assert.Empty(mongoQ.Select.Projection);
        Assert.False(mongoQ.Select.IsBareProjection);
    }

    [Fact]
    public void Bare_collection_count_leaf_over_a_navigation_type_the_rewrite_cannot_build_an_empty_for_is_declined()
    {
        // A non-dotted path ("Lines"), but List<Line> is not assignable to ISet<Line>, so the rewrite can't build
        // the `??` substitute and the driver's push-down would render a bare {"$size": "$Lines"} that aborts on a
        // missing/null array. IsFallbackSafeBareSizeLeaf calls the rewrite's own matcher, so the gate knows every
        // dimension the rewrite does. End-to-end:
        // NativeComputedBareProjectionTests.Set_typed_collection_navigation_bare_count_is_declined_and_answers_correctly.
        var mongoQ = TestSetQuery();
        Expression<Func<SetOrder, int>> selector =
            o => EF.Property<ISet<Line>>(o, "Lines").AsQueryable().Count();

        Assert.False(NativeProjectionBinder.TryPopulateNativeProjection(mongoQ, selector));
        Assert.Empty(mongoQ.Select.Projection);
        Assert.False(mongoQ.Select.IsBareProjection);
        Assert.Null(mongoQ.Select.BareProjectionTier);

        // Control on the same model: a plain bare leaf still binds, so the decline isn't a broken fixture.
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

        // The top node is arithmetic, so gate 1 admits it, but its operand is a MongoSizeExpression: the driver's
        // push-down renders a bare `$size` that aborts on a missing/null array (late decline under Native, or
        // DriverLinq). End-to-end:
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

        // Same hole through the cast arm; gate 1 has two admitting shapes and both need the subtree check. The
        // `/ 2.0` forces a narrowing MongoConvertExpression (a widening cast is unwrapped to a plain field).
        Assert.False(NativeProjectionBinder.TryPopulateNativeProjection(mongoQ, selector));
        Assert.Empty(mongoQ.Select.Projection);
        Assert.False(mongoQ.Select.IsBareProjection);
    }

    [Fact]
    public void Bare_arithmetic_over_a_FILTERED_collection_count_is_declined_by_the_subtree_check()
    {
        var mongoQ = TestQuery();
        Expression<Func<Order, int>> selector = o => o.Lines.Count(l => l.Quantity > 0) * 2;

        // IsArrayFreeComputedSubtree is an allow-list containing neither size kind; this proves the filtered kind
        // is excluded by the catch-all, not by an enumeration someone must keep complete.
        Assert.False(NativeProjectionBinder.TryPopulateNativeProjection(mongoQ, selector));
        Assert.Empty(mongoQ.Select.Projection);
        Assert.False(mongoQ.Select.IsBareProjection);
    }

    [Fact]
    public void Bare_whole_entity_parameter_is_declined_by_the_arm()
    {
        var mongoQ = TestQuery();
        Expression<Func<Order, Order>> selector = o => o;

        // TranslateSelect returns `x => x` unchanged before the binder runs, but the arm must still never match a
        // bare ParameterExpression, or whole-entity queries would emit a $project keyed by nothing.
        Assert.False(NativeProjectionBinder.TryPopulateNativeProjection(mongoQ, selector));
        Assert.Empty(mongoQ.Select.Projection);
        Assert.False(mongoQ.Select.IsBareProjection);
    }

    // ── Wrapped whole-entity leaf recognition ───────────────────────────────────────────────────────────
    //
    // A whole-entity leaf inside a wrapped projection (NewExpression/MemberInitExpression) is emitted as
    // `$$ROOT`. A bare-body parameter must not be admitted by this arm.

    [Fact]
    public void Wrapped_whole_entity_leaf_is_admitted_as_ROOT()
    {
        var mongoQ = TestQuery();
        Expression<Func<Order, object>> selector = o => new { o, Total = o.Age * o.Score };

        Assert.True(NativeProjectionBinder.TryPopulateNativeProjection(mongoQ, selector));

        var projections = mongoQ.Select.Projection;
        Assert.Equal(2, projections.Count);

        var firstProjection = projections[0];
        Assert.Equal("o", firstProjection.Alias);
        var rootRef = Assert.IsType<MongoElementRefExpression>(firstProjection.Expression);
        Assert.Equal("$ROOT", rootRef.Path);
        Assert.Equal(typeof(Order), rootRef.Type);

        var secondProjection = projections[1];
        Assert.Equal("Total", secondProjection.Alias);
        Assert.IsType<MongoBinaryExpression>(secondProjection.Expression);

        Assert.False(mongoQ.Select.IsBareProjection);
        Assert.False(mongoQ.Select.TryGetProjectionAlias(null, out _));
    }

    [Fact]
    public void Bare_whole_entity_parameter_is_still_declined_by_the_new_arm()
    {
        // A bare `o => o` must not be admitted by the wrapped-entity arm, or it would break the WholeEntity route.
        var mongoQ = TestQuery();
        Expression<Func<Order, Order>> selector = o => o;

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

        // The guard makes the alias carrier write-once, which is what lets AddProjectionAliasOverride use
        // Dictionary.Add.
        Assert.False(NativeProjectionBinder.TryPopulateNativeProjection(mongoQ, second));

        // Declines cleanly: the first projection and override are untouched.
        var projection = Assert.Single(mongoQ.Select.Projection);
        Assert.Equal("Country", projection.Alias);
        Assert.True(mongoQ.Select.TryGetProjectionAlias(null, out var alias));
        Assert.Equal("Country", alias);
    }

    // ── NullCoalesceSyntheticBareCountBody: the CapturedExpression null-coalescing rewrite ───────────────
    //
    // Tested directly rather than through TryPopulateNativeProjection: the rewrite can't live in the binder's
    // commit block, because MongoQueryableMethodTranslatingExpressionVisitor.VisitMethodCall reassigns
    // CapturedExpression = _finalExpression after every translated Queryable call. It is applied at that
    // assignment, keyed on the Synthetic-tier override, which these tests register by hand.
    //
    // EF's nav-expansion captures Select(b => b.Posts.Count) as
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

    // Hand-built: C# can't spell EF.Property<T> with a `Type` variable, and the navigation's declared CLR type is
    // the axis under test.
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
        // The driver renders a bare {"$size": "$Lines"}, which aborts on a missing/null array whenever a
        // Synthetic bare count reaches the driver-LINQ bridge. (b.Lines ?? new List<Line>()).Count renders
        // {"$size": {"$ifNull": ["$Lines", []]}}, identical to native.
        var captured = CapturedSelect(o => EF.Property<List<Line>>(o, "Lines").AsQueryable().Count());
        var mongoQ = SyntheticBareQuery(captured);

        var rewritten = NativeProjectionBinder.NullCoalesceSyntheticBareCountBody(
            mongoQ.CapturedExpression, mongoQ.Select);

        Assert.NotSame(captured, rewritten);

        // Shape: Count over AsQueryable over a Coalesce of the untouched navigation and a new empty collection of
        // the navigation's CLR type.
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

        // The lambda parameter must be reused; a rebuilt one would leave the body referencing an unbound parameter.
        Assert.Same(SelectorOf(captured).Parameters[0], SelectorOf(rewritten!).Parameters[0]);
    }

    [Fact]
    public void Bare_LongCount_body_is_rewritten_too()
    {
        // EF lowers `b.Lines.LongCount()` to this shape; it must be rewritten like Count.
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
        // Nav-expansion uses the declared property type, so interface-typed navigations (ICollection<T>,
        // IList<T>, IEnumerable<T>; see OwnedEntityTests.PersonWithIEnumerableLocations) reach this rewrite and
        // must be coalesced against List<T>. HashSet<Line> is constructible, so it's coalesced against itself.
        var captured = CapturedSelectOfType(navigationType);
        var mongoQ = SyntheticBareQuery(captured);

        var rewritten = NativeProjectionBinder.NullCoalesceSyntheticBareCountBody(
            mongoQ.CapturedExpression, mongoQ.Select);

        Assert.NotSame(captured, rewritten);
        var count = Assert.IsAssignableFrom<MethodCallExpression>(SelectorOf(rewritten!).Body);
        var asQueryable = Assert.IsAssignableFrom<MethodCallExpression>(count.Arguments[0]);
        var coalesce = Assert.IsAssignableFrom<BinaryExpression>(asQueryable.Arguments[0]);

        // The Coalesce keeps the navigation's declared type (keeping AsQueryable valid); the substitute is a
        // constructible type assignable to it.
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
        // ISet<T>/IReadOnlySet<T> are EF-supported types List<T> isn't assignable to, so the rewrite declines —
        // and so does IsFallbackSafeBareSizeLeaf, which keeps the un-rewritten bare $size from being committed
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
        // `Select(b => b.Posts.Count).First()` captures as First(Select(…)), so the Select isn't outermost.
        var select = CapturedSelect(o => EF.Property<List<Line>>(o, "Lines").AsQueryable().Count());
        var captured = UnderTerminator(select, terminator);
        var mongoQ = SyntheticBareQuery(captured);

        var rewritten = NativeProjectionBinder.NullCoalesceSyntheticBareCountBody(
            mongoQ.CapturedExpression, mongoQ.Select);

        Assert.NotSame(captured, rewritten);
        var rebuiltTerminator = Assert.IsAssignableFrom<MethodCallExpression>(rewritten);

        // The terminator's MethodInfo must be unchanged: unlike StripPushedDownSelect (which removes the Select
        // and so must retarget), this rewrite keeps the Select's element type.
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
        // The terminator branch shares the outermost-Select branch's body gate rather than a second copy.
        var captured = UnderTerminator(CapturedSelect(o => o.Amount * 2), nameof(Queryable.First));
        var mongoQ = SyntheticBareQuery(captured);

        Assert.Same(captured,
            NativeProjectionBinder.NullCoalesceSyntheticBareCountBody(mongoQ.CapturedExpression, mongoQ.Select));
    }

    [Fact]
    public void Bare_arithmetic_body_is_left_untouched()
    {
        // $multiply never touches an array, so arithmetic needs no rewrite. Arithmetic is also Synthetic-tier, so
        // this keeps the rewrite keyed on body shape as well as tier.
        var captured = CapturedSelect(o => o.Amount * 2);
        var mongoQ = SyntheticBareQuery(captured);

        Assert.Same(captured,
            NativeProjectionBinder.NullCoalesceSyntheticBareCountBody(mongoQ.CapturedExpression, mongoQ.Select));
    }

    [Fact]
    public void Bare_cast_body_is_left_untouched()
    {
        // Same boundary: a narrowing cast renders $toInt.
        var captured = CapturedSelect(o => (int)o.Weight);
        var mongoQ = SyntheticBareQuery(captured);

        Assert.Same(captured,
            NativeProjectionBinder.NullCoalesceSyntheticBareCountBody(mongoQ.CapturedExpression, mongoQ.Select));
    }

    [Fact]
    public void A_count_body_with_no_synthetic_override_is_left_untouched()
    {
        // Without a Synthetic-tier override the rewrite must not fire: the gate is tier data, not a body-shape
        // sniff.
        var captured = CapturedSelect(o => EF.Property<List<Line>>(o, "Lines").AsQueryable().Count());
        var mongoQ = TestQuery();
        mongoQ.CapturedExpression = captured;

        Assert.Same(captured,
            NativeProjectionBinder.NullCoalesceSyntheticBareCountBody(mongoQ.CapturedExpression, mongoQ.Select));

        // A DocumentPath-tier bare override is equally untouched.
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
        // The rewrite navigates only to the pushed-down bare Select's body (the same navigation
        // StripPushedDownSelect uses), so it never reaches a wrapped leaf. Wrapped counts are coalesced elsewhere
        // (MongoEFToLinqTranslatingExpressionVisitor); see
        // NativeOwnedCollectionCountTests.Wrapped_count_projection_under_DriverLinq_works_for_present_and_ragged_arrays_alike.
        var captured = CapturedSelect(o => new {o.Country, N = EF.Property<List<Line>>(o, "Lines").AsQueryable().Count()});
        var mongoQ = SyntheticBareQuery(captured);

        Assert.Same(captured,
            NativeProjectionBinder.NullCoalesceSyntheticBareCountBody(mongoQ.CapturedExpression, mongoQ.Select));
    }

    [Fact]
    public void A_count_over_something_other_than_the_selector_parameter_is_left_untouched()
    {
        // A reference-collection count is an EntityQueryRoot subquery over a $lookup output (always an array), so
        // it needs no rewrite; the parameter-rooted requirement separates the two. Stood in for by a captured
        // local. The full Count(AsQueryable(x)) spelling is kept so only the root varies — `other.Count()` would
        // decline earlier and never exercise the parameter-rooted check.
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

        // A wrapped projection's aliases come from member names, so it must register no override; otherwise
        // IsBareProjection/BareProjectionTier would answer for a non-bare projection and wrongly trip the
        // late-fallback strip.
        Assert.True(NativeProjectionBinder.TryPopulateNativeProjection(mongoQ, selector));

        Assert.Equal(2, mongoQ.Select.Projection.Count);
        Assert.False(mongoQ.Select.IsBareProjection);
        Assert.Null(mongoQ.Select.BareProjectionTier);
        Assert.False(mongoQ.Select.TryGetProjectionAlias(null, out _));
    }
}
