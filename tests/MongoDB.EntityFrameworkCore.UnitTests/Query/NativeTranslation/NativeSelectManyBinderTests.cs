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
using MongoDB.EntityFrameworkCore.Query.Expressions;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query.NativeTranslation;

/// <summary>
/// Unit tests for <see cref="NativeSelectManyBinder"/>, which binds the inner-<c>Select</c> form of an
/// owned-collection <c>SelectMany</c> — <c>o => o.Items.AsQueryable().Select(i => new {...})</c> — to a native
/// <c>$unwind</c> + <c>$project</c>, plus the transparent-identifier and reference-navigation forms.
/// </summary>
public class NativeSelectManyBinderTests
{
    private class Item
    {
        public string Name { get; set; } = "";
        public decimal Price { get; set; }

        // A nested owned collection on the unwound element, so a computed leaf can contain a MongoSizeExpression
        // (i.Notes.Count) — the node kind the bare `_v` tier must refuse.
        public List<Note> Notes { get; set; } = [];
    }

    private class Note
    {
        public string Text { get; set; } = "";
    }

    private class Tag
    {
        public int Id { get; set; }
        public int OwnerId { get; set; }
        public string Label { get; set; } = "";
    }

    private class Owner
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public decimal Rank { get; set; }
        public List<Item> Items { get; set; } = [];
        public List<Tag> Tags { get; set; } = [];
    }

    private static MongoQueryExpression TestQuery()
    {
        using var db = SingleEntityDbContext.Create<Owner>(mb =>
        {
            mb.Entity<Owner>().OwnsMany(o => o.Items, ib => ib.OwnsMany(i => i.Notes));
            // MongoRelationshipDiscoveryConvention makes any unregistered navigation target owned. Registering
            // Tag with its own key and FK makes Owner.Tags a genuine reference collection navigation.
            mb.Entity<Tag>();
            mb.Entity<Owner>().HasMany(o => o.Tags).WithOne().HasForeignKey(t => t.OwnerId);
        });
        var entityType = db.Model.FindEntityType(typeof(Owner))!;
        return new MongoQueryExpression(entityType);
    }

    // Builds the collectionSelector lambda with an inferred anonymous projection type, in EF's shape:
    // Queryable.Select(Queryable.AsQueryable(o.Nav), innerLambda).
    private static LambdaExpression Build<TResult>(Expression<Func<Owner, IQueryable<TResult>>> expr) => expr;

    [Fact]
    public void Nested_select_binds_unwind_and_two_scope_projection()
    {
        var mongoQ = TestQuery();
        var collectionSelector = Build((Owner o) => o.Items.AsQueryable().Select(i => new { o.Name, i.Price }));

        Assert.True(NativeSelectManyBinder.TryBind(mongoQ, collectionSelector));

        Assert.Equal("Items", mongoQ.Select.UnwindSource!.InnerScopePath);
        Assert.Null(mongoQ.Select.UnwindSource!.Filter);
        Assert.Collection(mongoQ.Select.Projection,
            p =>
            {
                Assert.Equal("Name", p.Alias);
                var field = Assert.IsType<MongoFieldExpression>(p.Expression);
                Assert.Equal("Name", field.ElementName);
            },
            p =>
            {
                Assert.Equal("Price", p.Alias);
                var field = Assert.IsType<MongoFieldExpression>(p.Expression);
                Assert.Equal("Items.Price", field.ElementName);
            });
    }

    [Fact]
    public void Shared_member_name_resolves_by_scope_not_name()
    {
        var mongoQ = TestQuery();
        var collectionSelector =
            Build((Owner o) => o.Items.AsQueryable().Select(i => new { OuterName = o.Name, InnerName = i.Name }));

        Assert.True(NativeSelectManyBinder.TryBind(mongoQ, collectionSelector));

        Assert.Equal("Items", mongoQ.Select.UnwindSource!.InnerScopePath);
        var outer = mongoQ.Select.Projection.Single(p => p.Alias == "OuterName");
        Assert.Equal("Name", Assert.IsType<MongoFieldExpression>(outer.Expression).ElementName);
        var inner = mongoQ.Select.Projection.Single(p => p.Alias == "InnerName");
        Assert.Equal("Items.Name", Assert.IsType<MongoFieldExpression>(inner.Expression).ElementName);
    }

    [Fact]
    public void Reference_collection_navigation_returns_false()
    {
        var mongoQ = TestQuery();
        var collectionSelector = Build((Owner o) => o.Tags.AsQueryable().Select(t => new { o.Name, t.Label }));

        Assert.False(NativeSelectManyBinder.TryBind(mongoQ, collectionSelector));
        Assert.Null(mongoQ.Select.UnwindSource);
        Assert.Empty(mongoQ.Select.Projection);
    }

    [Fact]
    public void Entity_valued_leaf_returns_false()
    {
        var mongoQ = TestQuery();
        var collectionSelector = Build((Owner o) => o.Items.AsQueryable().Select(i => new { X = i }));

        Assert.False(NativeSelectManyBinder.TryBind(mongoQ, collectionSelector));
        Assert.Null(mongoQ.Select.UnwindSource);
        Assert.Empty(mongoQ.Select.Projection);
    }

    [Fact]
    public void Bare_element_leaf_returns_false()
    {
        var mongoQ = TestQuery();
        var collectionSelector = Build((Owner o) => o.Items.AsQueryable().Select(i => i));

        Assert.False(NativeSelectManyBinder.TryBind(mongoQ, collectionSelector));
        Assert.Null(mongoQ.Select.UnwindSource);
    }

    [Fact]
    public void Computed_leaf_returns_false()
    {
        var mongoQ = TestQuery();
        var collectionSelector = Build((Owner o) => o.Items.AsQueryable().Select(i => new { X = i.Price * 2 }));

        Assert.False(NativeSelectManyBinder.TryBind(mongoQ, collectionSelector));
        Assert.Null(mongoQ.Select.UnwindSource);
    }

    [Fact]
    public void Body_not_nested_select_returns_false()
    {
        // Explicit-result-selector form: the body is the bare AsQueryable() call and the projection lives in a
        // separate trailing Select, which this entry point doesn't handle.
        var mongoQ = TestQuery();
        Expression<Func<Owner, IQueryable<Item>>> collectionSelector = o => o.Items.AsQueryable();

        Assert.False(NativeSelectManyBinder.TryBind(mongoQ, collectionSelector));
        Assert.Null(mongoQ.Select.UnwindSource);
    }

    [Fact]
    public void Inner_where_before_select_binds_with_filter()
    {
        var mongoQ = TestQuery();
        var collectionSelector = Build((Owner o) =>
            o.Items.AsQueryable().Where(i => i.Price > 0).Select(i => new { o.Name, i.Price }));

        Assert.True(NativeSelectManyBinder.TryBind(mongoQ, collectionSelector));

        Assert.Equal("Items", mongoQ.Select.UnwindSource!.InnerScopePath);
        // Filter field ref is prefixed with the owned unwind path.
        var binary = Assert.IsType<MongoBinaryExpression>(mongoQ.Select.UnwindSource!.Filter);
        Assert.Equal("Items.Price", Assert.IsType<MongoFieldExpression>(binary.Left).ElementName);
        // The projection still binds.
        Assert.Equal(2, mongoQ.Select.Projection.Count);
    }

    [Fact]
    public void Inner_stacked_where_ands_together_binds_with_filter()
    {
        var mongoQ = TestQuery();
        var collectionSelector = Build((Owner o) =>
            o.Items.AsQueryable().Where(i => i.Price > 0).Where(i => i.Name != "x").Select(i => new { o.Name, i.Price }));

        Assert.True(NativeSelectManyBinder.TryBind(mongoQ, collectionSelector));

        var filter = Assert.IsType<MongoBinaryExpression>(mongoQ.Select.UnwindSource!.Filter);
        Assert.Equal(MongoBinaryOperator.AndAlso, filter.Operator);
    }

    [Fact]
    public void Inner_where_correlated_beyond_outer_binds_with_expr_filter()
    {
        // A filter referencing the outer owner (i.Name == o.Name) goes native as a field-to-field $expr: inner
        // field prefixed with the unwind path, outer at root. Item.Name shadows Owner.Name, so scopes must be
        // resolved by parameter identity, not name.
        var mongoQ = TestQuery();
        var collectionSelector = Build((Owner o) =>
            o.Items.AsQueryable().Where(i => i.Name == o.Name).Select(i => new { o.Name, i.Price }));

        Assert.True(NativeSelectManyBinder.TryBind(mongoQ, collectionSelector));

        var bin = Assert.IsType<MongoBinaryExpression>(mongoQ.Select.UnwindSource!.Filter);
        Assert.Equal(MongoBinaryOperator.Equal, bin.Operator);
        Assert.Equal("Items.Name", Assert.IsType<MongoFieldExpression>(bin.Left).ElementName);   // inner, prefixed
        Assert.Equal("Name", Assert.IsType<MongoOuterFieldExpression>(bin.Right).ElementName);     // outer, root
    }

    [Fact]
    public void Inner_where_mixed_inner_and_correlated_conjunct_binds()
    {
        // One .Where whose body ANDs an inner-only conjunct with a correlated one.
        var mongoQ = TestQuery();
        var collectionSelector = Build((Owner o) =>
            o.Items.AsQueryable().Where(i => i.Name != "x" && i.Name == o.Name).Select(i => new { o.Name, i.Price }));

        Assert.True(NativeSelectManyBinder.TryBind(mongoQ, collectionSelector));
        var and = Assert.IsType<MongoBinaryExpression>(mongoQ.Select.UnwindSource!.Filter);
        Assert.Equal(MongoBinaryOperator.AndAlso, and.Operator);
        // Inner-only conjunct: inner field prefixed.
        var left = Assert.IsType<MongoBinaryExpression>(and.Left);
        Assert.Equal("Items.Name", Assert.IsType<MongoFieldExpression>(left.Left).ElementName);
        // Correlated conjunct: inner prefixed vs. outer at root.
        var right = Assert.IsType<MongoBinaryExpression>(and.Right);
        Assert.Equal("Items.Name", Assert.IsType<MongoFieldExpression>(right.Left).ElementName);
        Assert.Equal("Name", Assert.IsType<MongoOuterFieldExpression>(right.Right).ElementName);
    }

    [Fact]
    public void Inner_where_unsupported_correlated_operator_returns_false_without_mutation()
    {
        // i.Name.ToUpper() == o.Name — the two-scope translation rejects the operator, so the bind declines with no
        // partial mutation.
        var mongoQ = TestQuery();
        var collectionSelector = Build((Owner o) =>
            o.Items.AsQueryable().Where(i => i.Name.ToUpper() == o.Name).Select(i => new { o.Name, i.Price }));

        Assert.False(NativeSelectManyBinder.TryBind(mongoQ, collectionSelector));
        Assert.Null(mongoQ.Select.UnwindSource);
    }

    [Fact]
    public void Bare_nav_with_inner_where_binds_with_filter()
    {
        var mongoQ = TestQuery();
        var collectionSelector = Build((Owner o) => o.Items.AsQueryable().Where(i => i.Price > 0));

        Assert.True(NativeSelectManyBinder.TryBindBareNavUnwind(mongoQ, collectionSelector));

        Assert.Equal("Items", mongoQ.Select.UnwindSource!.InnerScopePath);
        var binary = Assert.IsType<MongoBinaryExpression>(mongoQ.Select.UnwindSource!.Filter);
        Assert.Equal("Items.Price", Assert.IsType<MongoFieldExpression>(binary.Left).ElementName);
    }

    [Fact]
    public void Bare_nav_without_where_binds_with_null_filter()
    {
        var mongoQ = TestQuery();
        Expression<Func<Owner, IQueryable<Item>>> collectionSelector = o => o.Items.AsQueryable();

        Assert.True(NativeSelectManyBinder.TryBindBareNavUnwind(mongoQ, collectionSelector));
        Assert.Equal("Items", mongoQ.Select.UnwindSource!.InnerScopePath);
        Assert.Null(mongoQ.Select.UnwindSource!.Filter);
    }

    [Fact]
    public void Bare_nav_with_correlated_beyond_outer_where_binds_with_expr_filter()
    {
        var mongoQ = TestQuery();
        var collectionSelector = Build((Owner o) => o.Items.AsQueryable().Where(i => i.Name == o.Name));

        Assert.True(NativeSelectManyBinder.TryBindBareNavUnwind(mongoQ, collectionSelector));
        var bin = Assert.IsType<MongoBinaryExpression>(mongoQ.Select.UnwindSource!.Filter);
        Assert.Equal(MongoBinaryOperator.Equal, bin.Operator);
        Assert.Equal("Items.Name", Assert.IsType<MongoFieldExpression>(bin.Left).ElementName);
        Assert.Equal("Name", Assert.IsType<MongoOuterFieldExpression>(bin.Right).ElementName);
    }

    // TryBindTransparentIdentifierProjection (explicit-result-selector / query-syntax form): nav-expansion produces a
    // trailing Select over a TransparentIdentifier(Outer, Inner) with leaves ti.Outer.<m> / ti.Inner.<m>. Synthesized
    // here, since real nav-expansion output isn't reachable from a unit test.

    private class TransparentIdentifier
    {
        public Owner Outer { get; set; } = null!;
        public Item Inner { get; set; } = null!;
        public Item Other { get; set; } = null!;
    }

    private class Projected
    {
        public string Name { get; set; } = "";
        public decimal Price { get; set; }
    }

    private class NamedProjected
    {
        public string OuterName { get; set; } = "";
        public string InnerName { get; set; } = "";
    }

    private class EntityLeafProjected
    {
        public Owner X { get; set; } = null!;
    }

    private class ComputedLeafProjected
    {
        public decimal X { get; set; }
    }

    private class OtherScopeProjected
    {
        public string X { get; set; } = "";
    }

    private class EntityValuedProjected
    {
        public List<Item> X { get; set; } = [];
    }

    private static IEntityType ItemEntityType(MongoQueryExpression mongoQ)
        => mongoQ.CollectionExpression.EntityType.FindNavigation(nameof(Owner.Items))!.TargetEntityType;

    private static (ParameterExpression Ti, MemberExpression Outer, MemberExpression Inner) TiScopes()
    {
        var ti = Expression.Parameter(typeof(TransparentIdentifier), "ti");
        var outer = Expression.Property(ti, nameof(TransparentIdentifier.Outer));
        var inner = Expression.Property(ti, nameof(TransparentIdentifier.Inner));
        return (ti, outer, inner);
    }

    private static LambdaExpression NameAndPriceSelector()
    {
        var (ti, outer, inner) = TiScopes();
        var body = Expression.MemberInit(Expression.New(typeof(Projected)),
            Expression.Bind(typeof(Projected).GetProperty(nameof(Projected.Name))!,
                Expression.Property(outer, nameof(Owner.Name))),
            Expression.Bind(typeof(Projected).GetProperty(nameof(Projected.Price))!,
                Expression.Property(inner, nameof(Item.Price))));
        return Expression.Lambda(body, ti);
    }

    [Fact]
    public void TryBindTransparentIdentifierProjection_binds_two_scope_projection()
    {
        var mongoQ = TestQuery();
        mongoQ.Select.AddUnwindSource(MongoUnwindSource.Owned("Items", ItemEntityType(mongoQ)));

        Assert.True(NativeSelectManyBinder.TryBindTransparentIdentifierProjection(mongoQ, NameAndPriceSelector(), out _));

        Assert.Collection(mongoQ.Select.Projection,
            p =>
            {
                Assert.Equal("Name", p.Alias);
                var field = Assert.IsType<MongoFieldExpression>(p.Expression);
                Assert.Equal("Name", field.ElementName);
            },
            p =>
            {
                Assert.Equal("Price", p.Alias);
                var field = Assert.IsType<MongoFieldExpression>(p.Expression);
                Assert.Equal("Items.Price", field.ElementName);
            });
    }

    [Fact]
    public void TryBindTransparentIdentifierProjection_shared_member_name_resolves_by_scope_not_name()
    {
        var mongoQ = TestQuery();
        mongoQ.Select.AddUnwindSource(MongoUnwindSource.Owned("Items", ItemEntityType(mongoQ)));

        var (ti, outer, inner) = TiScopes();
        var body = Expression.MemberInit(Expression.New(typeof(NamedProjected)),
            Expression.Bind(typeof(NamedProjected).GetProperty(nameof(NamedProjected.OuterName))!,
                Expression.Property(outer, nameof(Owner.Name))),
            Expression.Bind(typeof(NamedProjected).GetProperty(nameof(NamedProjected.InnerName))!,
                Expression.Property(inner, nameof(Item.Name))));
        var selector = Expression.Lambda(body, ti);

        Assert.True(NativeSelectManyBinder.TryBindTransparentIdentifierProjection(mongoQ, selector, out _));

        var outerP = mongoQ.Select.Projection.Single(p => p.Alias == "OuterName");
        Assert.Equal("Name", Assert.IsType<MongoFieldExpression>(outerP.Expression).ElementName);
        var innerP = mongoQ.Select.Projection.Single(p => p.Alias == "InnerName");
        Assert.Equal("Items.Name", Assert.IsType<MongoFieldExpression>(innerP.Expression).ElementName);
    }

    private class DoublyNestedTi
    {
        public TransparentIdentifier Outer { get; set; } = null!; // TI(Outer=Owner, Inner=Item) — level 1
        public Tag Inner { get; set; } = null!;                   // level 2's own unwound element
    }

    private class TripleProjected
    {
        public string OwnerName { get; set; } = "";
        public string ItemName { get; set; } = "";
        public string TagLabel { get; set; } = "";
    }

    [Fact]
    public void TryBindTransparentIdentifierProjection_binds_three_scope_projection()
    {
        var mongoQ = TestQuery();
        mongoQ.Select.AddUnwindSource(MongoUnwindSource.Owned("Items", ItemEntityType(mongoQ)));
        var tagsNav = mongoQ.CollectionExpression.EntityType.FindNavigation(nameof(Owner.Tags))!;
        var tagLookup = new LookupExpression(tagsNav, forceUnwind: true);
        mongoQ.Select.AddUnwindSource(
            MongoUnwindSource.Reference(LookupExpression.GetLookupAlias(tagsNav), tagsNav.TargetEntityType, tagLookup));

        var ti = Expression.Parameter(typeof(DoublyNestedTi), "ti");
        var outerOuter = Expression.Property(Expression.Property(ti, nameof(DoublyNestedTi.Outer)), nameof(TransparentIdentifier.Outer));
        var outerInner = Expression.Property(Expression.Property(ti, nameof(DoublyNestedTi.Outer)), nameof(TransparentIdentifier.Inner));
        var inner = Expression.Property(ti, nameof(DoublyNestedTi.Inner));

        var body = Expression.MemberInit(Expression.New(typeof(TripleProjected)),
            Expression.Bind(typeof(TripleProjected).GetProperty(nameof(TripleProjected.OwnerName))!,
                Expression.Property(outerOuter, nameof(Owner.Name))),
            Expression.Bind(typeof(TripleProjected).GetProperty(nameof(TripleProjected.ItemName))!,
                Expression.Property(outerInner, nameof(Item.Name))),
            Expression.Bind(typeof(TripleProjected).GetProperty(nameof(TripleProjected.TagLabel))!,
                Expression.Property(inner, nameof(Tag.Label))));
        var selector = Expression.Lambda(body, ti);

        Assert.True(NativeSelectManyBinder.TryBindTransparentIdentifierProjection(mongoQ, selector, out _));

        var ownerP = mongoQ.Select.Projection.Single(p => p.Alias == "OwnerName");
        Assert.Equal("Name", Assert.IsType<MongoFieldExpression>(ownerP.Expression).ElementName);
        var itemP = mongoQ.Select.Projection.Single(p => p.Alias == "ItemName");
        Assert.Equal("Items.Name", Assert.IsType<MongoFieldExpression>(itemP.Expression).ElementName);
        var tagP = mongoQ.Select.Projection.Single(p => p.Alias == "TagLabel");
        Assert.Equal("_lookup_Tags.Label", Assert.IsType<MongoFieldExpression>(tagP.Expression).ElementName);
    }

    // Wraps DoublyNestedTi one level further, so a chain 1 hop deeper than any valid 2-source shape can be
    // constructed with real static types (ti3.Outer.Outer.Outer.Name — 3 "Outer" hops under a 2-source chain).
    private class TripleNestedTi
    {
        public DoublyNestedTi Outer { get; set; } = null!;
    }

    [Fact]
    public void TryBindTransparentIdentifierProjection_chain_deeper_than_source_count_returns_false()
    {
        // ti3.Outer.Outer.Outer.Name — 3 "Outer" hops under a 2-source chain. TryResolveScopeDepth must reject
        // path.Count > sourceCount before checking the hop pattern. End-to-end, the QMTEV's
        // IsSingleReferenceUnwindTerminalOnly check declines a third SelectMany before this binder is reached;
        // this isolates the binder half of that boundary.
        var mongoQ = TestQuery();
        mongoQ.Select.AddUnwindSource(MongoUnwindSource.Owned("Items", ItemEntityType(mongoQ)));
        var tagsNav = mongoQ.CollectionExpression.EntityType.FindNavigation(nameof(Owner.Tags))!;
        var tagLookup = new LookupExpression(tagsNav, forceUnwind: true);
        mongoQ.Select.AddUnwindSource(
            MongoUnwindSource.Reference(LookupExpression.GetLookupAlias(tagsNav), tagsNav.TargetEntityType, tagLookup));

        var ti3 = Expression.Parameter(typeof(TripleNestedTi), "ti3");
        var threeHops = Expression.Property(
            Expression.Property(
                Expression.Property(Expression.Property(ti3, nameof(TripleNestedTi.Outer)), nameof(DoublyNestedTi.Outer)),
                nameof(TransparentIdentifier.Outer)),
            nameof(Owner.Name));
        var body = Expression.MemberInit(Expression.New(typeof(OtherScopeProjected)),
            Expression.Bind(typeof(OtherScopeProjected).GetProperty(nameof(OtherScopeProjected.X))!, threeHops));
        var selector = Expression.Lambda(body, ti3);

        Assert.False(NativeSelectManyBinder.TryBindTransparentIdentifierProjection(mongoQ, selector, out _));
        Assert.Empty(mongoQ.Select.Projection);
    }

    [Fact]
    public void TryBindTransparentIdentifierProjection_no_unwind_source_returns_false()
    {
        var mongoQ = TestQuery();

        Assert.False(NativeSelectManyBinder.TryBindTransparentIdentifierProjection(mongoQ, NameAndPriceSelector(), out _));
        Assert.Empty(mongoQ.Select.Projection);
    }

    [Fact]
    public void TryBindTransparentIdentifierProjection_bare_scope_leaf_returns_false()
    {
        var mongoQ = TestQuery();
        mongoQ.Select.AddUnwindSource(MongoUnwindSource.Owned("Items", ItemEntityType(mongoQ)));

        var (ti, outer, _) = TiScopes();
        var body = Expression.MemberInit(Expression.New(typeof(EntityLeafProjected)),
            Expression.Bind(typeof(EntityLeafProjected).GetProperty(nameof(EntityLeafProjected.X))!, outer));
        var selector = Expression.Lambda(body, ti);

        Assert.False(NativeSelectManyBinder.TryBindTransparentIdentifierProjection(mongoQ, selector, out _));
        Assert.Empty(mongoQ.Select.Projection);
    }

    [Fact]
    public void TryBindTransparentIdentifierProjection_single_scope_arithmetic_leaf_returns_true()
    {
        var mongoQ = TestQuery();
        mongoQ.Select.AddUnwindSource(MongoUnwindSource.Owned("Items", ItemEntityType(mongoQ)));

        var (ti, _, inner) = TiScopes();
        var body = Expression.MemberInit(Expression.New(typeof(ComputedLeafProjected)),
            Expression.Bind(typeof(ComputedLeafProjected).GetProperty(nameof(ComputedLeafProjected.X))!,
                Expression.Multiply(Expression.Property(inner, nameof(Item.Price)), Expression.Constant(2m))));
        var selector = Expression.Lambda(body, ti);

        Assert.True(NativeSelectManyBinder.TryBindTransparentIdentifierProjection(mongoQ, selector, out _));

        var projection = Assert.Single(mongoQ.Select.Projection);
        Assert.Equal("X", projection.Alias);
        var binary = Assert.IsType<MongoBinaryExpression>(projection.Expression);
        Assert.Equal(MongoBinaryOperator.Multiply, binary.Operator);
        Assert.Equal("Items.Price", Assert.IsType<MongoFieldExpression>(binary.Left).ElementName);
        Assert.Equal(2m, Assert.IsType<MongoConstantExpression>(binary.Right).Value);
    }

    [Fact]
    public void TryBindTransparentIdentifierProjection_cross_scope_computed_leaf_binds_each_operand_in_its_own_scope()
    {
        // An arithmetic leaf mixing outer (o.Rank) and inner (i.Price) scopes: the single-scope
        // ScopeRerootingVisitor sets CrossScope and declines, and the cross-scope fallback translates each operand
        // against its own scope. Both element names are asserted because a mis-scoped side would still yield a
        // $multiply of two fields.
        var mongoQ = TestQuery();
        mongoQ.Select.AddUnwindSource(MongoUnwindSource.Owned("Items", ItemEntityType(mongoQ)));

        var (ti, outer, inner) = TiScopes();
        var body = Expression.MemberInit(Expression.New(typeof(ComputedLeafProjected)),
            Expression.Bind(typeof(ComputedLeafProjected).GetProperty(nameof(ComputedLeafProjected.X))!,
                Expression.Multiply(Expression.Property(outer, nameof(Owner.Rank)), Expression.Property(inner, nameof(Item.Price)))));
        var selector = Expression.Lambda(body, ti);

        Assert.True(NativeSelectManyBinder.TryBindTransparentIdentifierProjection(mongoQ, selector, out var bareAlias));
        Assert.Null(bareAlias); // a WRAPPED body: no bare `_v` alias.

        var projection = Assert.Single(mongoQ.Select.Projection);
        Assert.Equal("X", projection.Alias);
        var binary = Assert.IsType<MongoBinaryExpression>(projection.Expression);
        Assert.Equal(MongoBinaryOperator.Multiply, binary.Operator);
        Assert.Equal("Rank", Assert.IsType<MongoFieldExpression>(binary.Left).ElementName);
        Assert.Equal("Items.Price", Assert.IsType<MongoFieldExpression>(binary.Right).ElementName);
    }

    [Fact]
    public void TryBindTransparentIdentifierProjection_cross_scope_leaf_with_a_constant_operand_binds()
    {
        // `(o.Rank * i.Price) + 1`: the cross-scope subtree is an operand, exercising TryTranslateScopedOperand's
        // recursion, and `1` its scope-free arm (which the single-scope binder still refuses as a whole leaf —
        // see the constant-only leaf test).
        var mongoQ = TestQuery();
        mongoQ.Select.AddUnwindSource(MongoUnwindSource.Owned("Items", ItemEntityType(mongoQ)));

        var (ti, outer, inner) = TiScopes();
        var crossScope = Expression.Multiply(
            Expression.Property(outer, nameof(Owner.Rank)), Expression.Property(inner, nameof(Item.Price)));
        var body = Expression.MemberInit(Expression.New(typeof(ComputedLeafProjected)),
            Expression.Bind(typeof(ComputedLeafProjected).GetProperty(nameof(ComputedLeafProjected.X))!,
                Expression.Add(crossScope, Expression.Constant(1m))));
        var selector = Expression.Lambda(body, ti);

        Assert.True(NativeSelectManyBinder.TryBindTransparentIdentifierProjection(mongoQ, selector, out _));

        var projection = Assert.Single(mongoQ.Select.Projection);
        var add = Assert.IsType<MongoBinaryExpression>(projection.Expression);
        Assert.Equal(MongoBinaryOperator.Add, add.Operator);
        Assert.Equal(1m, Assert.IsType<MongoConstantExpression>(add.Right).Value);
        var multiply = Assert.IsType<MongoBinaryExpression>(add.Left);
        Assert.Equal("Rank", Assert.IsType<MongoFieldExpression>(multiply.Left).ElementName);
        Assert.Equal("Items.Price", Assert.IsType<MongoFieldExpression>(multiply.Right).ElementName);
    }

    [Fact]
    public void TryBindTransparentIdentifierProjection_constant_only_leaf_still_returns_false()
    {
        // The single-scope entry point must pass `requireScopeRooted: true`, so a leaf with no scope-rooted member
        // (2 * 3) declines rather than pushing a constant-only $project leaf down.
        var mongoQ = TestQuery();
        mongoQ.Select.AddUnwindSource(MongoUnwindSource.Owned("Items", ItemEntityType(mongoQ)));

        var (ti, _, _) = TiScopes();
        var body = Expression.MemberInit(Expression.New(typeof(ComputedLeafProjected)),
            Expression.Bind(typeof(ComputedLeafProjected).GetProperty(nameof(ComputedLeafProjected.X))!,
                Expression.Multiply(Expression.Constant(2m), Expression.Constant(3m))));
        var selector = Expression.Lambda(body, ti);

        Assert.False(NativeSelectManyBinder.TryBindTransparentIdentifierProjection(mongoQ, selector, out _));
        Assert.Empty(mongoQ.Select.Projection);
    }

    [Fact]
    public void TryBindTransparentIdentifierProjection_bare_computed_body_binds_under_the_synthetic_alias()
    {
        // One-arg `SelectMany(o => o.Items).Select(i => i.Price * 2)` folds to a bare computed body,
        // `ti => ti.Inner.Price * 2`. With no member name it binds under the reserved `_v` alias and reports it
        // back so the caller can build the by-alias shaper.
        var mongoQ = TestQuery();
        mongoQ.Select.AddUnwindSource(MongoUnwindSource.Owned("Items", ItemEntityType(mongoQ)));

        var (ti, _, inner) = TiScopes();
        var selector = Expression.Lambda(
            Expression.Multiply(Expression.Property(inner, nameof(Item.Price)), Expression.Constant(2m)), ti);

        Assert.True(NativeSelectManyBinder.TryBindTransparentIdentifierProjection(mongoQ, selector, out var bareAlias));
        Assert.Equal("_v", bareAlias);

        var projection = Assert.Single(mongoQ.Select.Projection);
        Assert.Equal("_v", projection.Alias);
        var binary = Assert.IsType<MongoBinaryExpression>(projection.Expression);
        Assert.Equal("Items.Price", Assert.IsType<MongoFieldExpression>(binary.Left).ElementName);
    }

    [Fact]
    public void TryBindTransparentIdentifierProjection_bare_cross_scope_computed_body_binds()
    {
        // A bare body that is also cross-scope.
        var mongoQ = TestQuery();
        mongoQ.Select.AddUnwindSource(MongoUnwindSource.Owned("Items", ItemEntityType(mongoQ)));

        var (ti, outer, inner) = TiScopes();
        var selector = Expression.Lambda(
            Expression.Multiply(
                Expression.Property(outer, nameof(Owner.Rank)), Expression.Property(inner, nameof(Item.Price))), ti);

        Assert.True(NativeSelectManyBinder.TryBindTransparentIdentifierProjection(mongoQ, selector, out var bareAlias));
        Assert.Equal("_v", bareAlias);
        var binary = Assert.IsType<MongoBinaryExpression>(Assert.Single(mongoQ.Select.Projection).Expression);
        Assert.Equal("Rank", Assert.IsType<MongoFieldExpression>(binary.Left).ElementName);
        Assert.Equal("Items.Price", Assert.IsType<MongoFieldExpression>(binary.Right).ElementName);
    }

    [Fact]
    public void TryBindTransparentIdentifierProjection_bare_computed_body_containing_a_count_returns_false()
    {
        // Mirrors NativeProjectionBinder's tier-2 subtree rule: a `$size` anywhere under the body renders as a
        // bare `$size` on the un-stripped driver fallback, a server error on a missing or null array.
        // `ti.Inner.Notes.Count * 2` has an arithmetic top node, so only the subtree check declines it.
        var mongoQ = TestQuery();
        mongoQ.Select.AddUnwindSource(MongoUnwindSource.Owned("Items", ItemEntityType(mongoQ)));

        var (ti, _, inner) = TiScopes();
        var count = Expression.Property(Expression.Property(inner, nameof(Item.Notes)), nameof(List<Note>.Count));
        var selector = Expression.Lambda(Expression.Multiply(count, Expression.Constant(2)), ti);

        Assert.False(NativeSelectManyBinder.TryBindTransparentIdentifierProjection(mongoQ, selector, out var bareAlias));
        Assert.Null(bareAlias);
        Assert.Empty(mongoQ.Select.Projection);
    }

    [Fact]
    public void TryBindTransparentIdentifierProjection_bare_member_access_body_still_returns_false()
    {
        // Only an arithmetic bare body is admitted. A bare member access (`ti.Inner.Name`) is path-addressable,
        // so its alias would have to be its own document path for a late fallback (tier 1), not `_v`. Without
        // IsArithmeticComputedLeaf's gate the member loop's first branch would bind it under `_v`.
        var mongoQ = TestQuery();
        mongoQ.Select.AddUnwindSource(MongoUnwindSource.Owned("Items", ItemEntityType(mongoQ)));

        var (ti, _, inner) = TiScopes();
        var selector = Expression.Lambda(Expression.Property(inner, nameof(Item.Name)), ti);

        Assert.False(NativeSelectManyBinder.TryBindTransparentIdentifierProjection(mongoQ, selector, out var bareAlias));
        Assert.Null(bareAlias);
        Assert.Empty(mongoQ.Select.Projection);
    }

    [Fact]
    public void TryBindTransparentIdentifierProjection_root_scope_arithmetic_leaf_returns_true()
    {
        // An all-outer (scope 0) arithmetic leaf takes the no-prefix path, unlike the inner-scope "Items.Price".
        var mongoQ = TestQuery();
        mongoQ.Select.AddUnwindSource(MongoUnwindSource.Owned("Items", ItemEntityType(mongoQ)));

        var (ti, outer, _) = TiScopes();
        var body = Expression.MemberInit(Expression.New(typeof(ComputedLeafProjected)),
            Expression.Bind(typeof(ComputedLeafProjected).GetProperty(nameof(ComputedLeafProjected.X))!,
                Expression.Multiply(Expression.Property(outer, nameof(Owner.Rank)), Expression.Constant(2m))));
        var selector = Expression.Lambda(body, ti);

        Assert.True(NativeSelectManyBinder.TryBindTransparentIdentifierProjection(mongoQ, selector, out _));

        var projection = Assert.Single(mongoQ.Select.Projection);
        Assert.Equal("X", projection.Alias);
        var binary = Assert.IsType<MongoBinaryExpression>(projection.Expression);
        Assert.Equal(MongoBinaryOperator.Multiply, binary.Operator);
        Assert.Equal("Rank", Assert.IsType<MongoFieldExpression>(binary.Left).ElementName);
        Assert.Equal(2m, Assert.IsType<MongoConstantExpression>(binary.Right).Value);
    }

    [Fact]
    public void TryBindTransparentIdentifierProjection_method_call_computed_leaf_returns_false()
    {
        // A plain method call like .ToUpper() has no translation here (TranslateOperand admits only
        // Contains/Not), so this declines.
        var mongoQ = TestQuery();
        mongoQ.Select.AddUnwindSource(MongoUnwindSource.Owned("Items", ItemEntityType(mongoQ)));

        var (ti, _, inner) = TiScopes();
        var toUpper = Expression.Call(
            Expression.Property(inner, nameof(Item.Name)), typeof(string).GetMethod(nameof(string.ToUpper), [])!);
        var body = Expression.MemberInit(Expression.New(typeof(OtherScopeProjected)),
            Expression.Bind(typeof(OtherScopeProjected).GetProperty(nameof(OtherScopeProjected.X))!, toUpper));
        var selector = Expression.Lambda(body, ti);

        Assert.False(NativeSelectManyBinder.TryBindTransparentIdentifierProjection(mongoQ, selector, out _));
        Assert.Empty(mongoQ.Select.Projection);
    }

    [Fact]
    public void TryBindTransparentIdentifierProjection_cross_scope_string_concat_leaf_translates_to_concat()
    {
        // String concatenation (Add, Type == string) translates via MongoConcatExpression — the string-typed
        // counterpart of the arithmetic leaf tests above.
        var mongoQ = TestQuery();
        mongoQ.Select.AddUnwindSource(MongoUnwindSource.Owned("Items", ItemEntityType(mongoQ)));

        var (ti, _, inner) = TiScopes();
        var concat = Expression.Add(
            Expression.Property(inner, nameof(Item.Name)),
            Expression.Constant("x"),
            typeof(string).GetMethod(nameof(string.Concat), [typeof(string), typeof(string)]));
        var body = Expression.MemberInit(Expression.New(typeof(OtherScopeProjected)),
            Expression.Bind(typeof(OtherScopeProjected).GetProperty(nameof(OtherScopeProjected.X))!, concat));
        var selector = Expression.Lambda(body, ti);

        Assert.True(NativeSelectManyBinder.TryBindTransparentIdentifierProjection(mongoQ, selector, out _));

        var projection = Assert.Single(mongoQ.Select.Projection);
        Assert.Equal("X", projection.Alias);
        var concatExpr = Assert.IsType<MongoConcatExpression>(projection.Expression);
        Assert.Equal(2, concatExpr.Operands.Count);
        Assert.Equal("Items.Name", Assert.IsType<MongoFieldExpression>(concatExpr.Operands[0]).ElementName);
        Assert.Equal("x", Assert.IsType<MongoConstantExpression>(concatExpr.Operands[1]).Value);
    }

    [Fact]
    public void TryBindTransparentIdentifierProjection_member_off_neither_scope_returns_false()
    {
        var mongoQ = TestQuery();
        mongoQ.Select.AddUnwindSource(MongoUnwindSource.Owned("Items", ItemEntityType(mongoQ)));

        var (ti, _, _) = TiScopes();
        var other = Expression.Property(ti, nameof(TransparentIdentifier.Other));
        var body = Expression.MemberInit(Expression.New(typeof(OtherScopeProjected)),
            Expression.Bind(typeof(OtherScopeProjected).GetProperty(nameof(OtherScopeProjected.X))!,
                Expression.Property(other, nameof(Item.Name))));
        var selector = Expression.Lambda(body, ti);

        Assert.False(NativeSelectManyBinder.TryBindTransparentIdentifierProjection(mongoQ, selector, out _));
        Assert.Empty(mongoQ.Select.Projection);
    }

    [Fact]
    public void TryBindTransparentIdentifierProjection_entity_valued_leaf_returns_false()
    {
        var mongoQ = TestQuery();
        mongoQ.Select.AddUnwindSource(MongoUnwindSource.Owned("Items", ItemEntityType(mongoQ)));

        var (ti, outer, _) = TiScopes();
        var body = Expression.MemberInit(Expression.New(typeof(EntityValuedProjected)),
            Expression.Bind(typeof(EntityValuedProjected).GetProperty(nameof(EntityValuedProjected.X))!,
                Expression.Property(outer, nameof(Owner.Items))));
        var selector = Expression.Lambda(body, ti);

        Assert.False(NativeSelectManyBinder.TryBindTransparentIdentifierProjection(mongoQ, selector, out _));
        Assert.Empty(mongoQ.Select.Projection);
    }

    // TryBindReferenceNavUnwind: the reference collectionSelector is a correlated subquery,
    // Queryable.Where(EntityQueryRootExpression<Target>, o => c.pk == o.fk), not a bare nav. Fixture: Owner.Tags (FK Tag.OwnerId).

    private static readonly System.Reflection.MethodInfo EfPropertyOfInt =
        typeof(EF).GetMethod(nameof(EF.Property))!.MakeGenericMethod(typeof(int));

    private static Expression ShadowProperty(ParameterExpression param, string name)
        => Expression.Call(EfPropertyOfInt, param, Expression.Constant(name));

    private static INavigation TagsNavigation(MongoQueryExpression mongoQ)
        => mongoQ.CollectionExpression.EntityType.FindNavigation(nameof(Owner.Tags))!;

    // Queryable.Where(EntityQueryRootExpression<Tag>, predicate) — the correlated-subquery shape a reference-nav
    // SelectMany's collectionSelector normalizes to.
    private static LambdaExpression ReferenceCollectionSelector(
        IEntityType targetEntityType, ParameterExpression outerParam, LambdaExpression predicate)
    {
        var whereCall = Expression.Call(
            typeof(Queryable), nameof(Queryable.Where), [predicate.Parameters[0].Type],
            new EntityQueryRootExpression(targetEntityType), Expression.Quote(predicate));
        return Expression.Lambda(whereCall, outerParam);
    }

    private static LambdaExpression TagsCorrelatedSelector(IEntityType tagEntityType, ParameterExpression outerParam)
    {
        var tParam = Expression.Parameter(typeof(Tag), "t");
        var predicate = Expression.Lambda(
            Expression.Equal(Expression.Property(outerParam, nameof(Owner.Id)), Expression.Property(tParam, nameof(Tag.OwnerId))),
            tParam);
        return ReferenceCollectionSelector(tagEntityType, outerParam, predicate);
    }

    // Queryable.Where(Queryable.Where(EntityQueryRootExpression<Tag>, fkPred), userPred) — a filtered inner
    // (c.Tags.Where(userPred)) nested form.
    private static LambdaExpression ReferenceCollectionSelectorFiltered(
        IEntityType targetEntityType, ParameterExpression outerParam, LambdaExpression fkPredicate,
        params LambdaExpression[] userPredicates)
    {
        Expression source = Expression.Call(
            typeof(Queryable), nameof(Queryable.Where), [fkPredicate.Parameters[0].Type],
            new EntityQueryRootExpression(targetEntityType), Expression.Quote(fkPredicate));
        foreach (var userPredicate in userPredicates)
            source = Expression.Call(
                typeof(Queryable), nameof(Queryable.Where), [userPredicate.Parameters[0].Type],
                source, Expression.Quote(userPredicate));
        return Expression.Lambda(source, outerParam);
    }

    [Fact]
    public void TryBindReferenceNavUnwind_binds_reference_collection_to_lookup_and_unwind_source()
    {
        var mongoQ = TestQuery();
        var tagNav = TagsNavigation(mongoQ);
        var outerParam = Expression.Parameter(typeof(Owner), "o");
        var collectionSelector = TagsCorrelatedSelector(tagNav.TargetEntityType, outerParam);

        Assert.True(NativeSelectManyBinder.TryBindReferenceNavUnwind(mongoQ, collectionSelector));

        var unwind = mongoQ.Select.UnwindSource;
        Assert.NotNull(unwind);
        Assert.Equal(MongoUnwindSourceKind.Reference, unwind!.Kind);
        Assert.Equal("_lookup_Tags", unwind.InnerScopePath);
        Assert.Same(tagNav.TargetEntityType, unwind.InnerEntityType);
        Assert.NotNull(unwind.Lookup);
        Assert.True(unwind.Lookup!.ForceUnwind);
        Assert.Contains(unwind.Lookup, mongoQ.Lookups);
        Assert.Null(unwind.Filter);
    }

    [Fact]
    public void TryBindReferenceNavUnwind_owned_collection_returns_false()
    {
        var mongoQ = TestQuery();
        var itemEntityType = ItemEntityType(mongoQ);
        var outerParam = Expression.Parameter(typeof(Owner), "o");
        var iParam = Expression.Parameter(typeof(Item), "i");
        var predicate = Expression.Lambda(
            Expression.Equal(Expression.Property(outerParam, nameof(Owner.Id)), ShadowProperty(iParam, "OwnerId")),
            iParam);
        var collectionSelector = ReferenceCollectionSelector(itemEntityType, outerParam, predicate);

        Assert.False(NativeSelectManyBinder.TryBindReferenceNavUnwind(mongoQ, collectionSelector));
        Assert.Null(mongoQ.Select.UnwindSource);
        Assert.Empty(mongoQ.Lookups); // no partial mutation: the lookup is registered only AFTER a confirmed match
    }

    [Fact]
    public void TryBindReferenceNavUnwind_nested_filtered_inner_binds_with_filter()
    {
        var mongoQ = TestQuery();
        var tagNav = TagsNavigation(mongoQ);
        var outerParam = Expression.Parameter(typeof(Owner), "o");
        var tParam = Expression.Parameter(typeof(Tag), "t");
        var fk = Expression.Lambda(
            Expression.Equal(Expression.Property(outerParam, nameof(Owner.Id)), Expression.Property(tParam, nameof(Tag.OwnerId))),
            tParam);
        var user = Expression.Lambda(
            Expression.NotEqual(Expression.Property(tParam, nameof(Tag.Label)), Expression.Constant("x")),
            tParam);
        var collectionSelector = ReferenceCollectionSelectorFiltered(tagNav.TargetEntityType, outerParam, fk, user);

        Assert.True(NativeSelectManyBinder.TryBindReferenceNavUnwind(mongoQ, collectionSelector));

        var unwind = mongoQ.Select.UnwindSource!;
        Assert.Equal(MongoUnwindSourceKind.Reference, unwind.Kind);
        Assert.NotNull(unwind.Filter);
        // The user filter's field ref is prefixed with the lookup scope.
        var binary = Assert.IsType<MongoBinaryExpression>(unwind.Filter);
        var field = Assert.IsType<MongoFieldExpression>(binary.Left);
        Assert.Equal("_lookup_Tags.Label", field.ElementName);
    }

    [Fact]
    public void TryBindReferenceNavUnwind_stacked_filters_bind_and_and_together()
    {
        var mongoQ = TestQuery();
        var tagNav = TagsNavigation(mongoQ);
        var outerParam = Expression.Parameter(typeof(Owner), "o");
        var tParam = Expression.Parameter(typeof(Tag), "t");
        var fk = Expression.Lambda(
            Expression.Equal(Expression.Property(outerParam, nameof(Owner.Id)), Expression.Property(tParam, nameof(Tag.OwnerId))),
            tParam);
        var user1 = Expression.Lambda(Expression.NotEqual(Expression.Property(tParam, nameof(Tag.Label)), Expression.Constant("x")), tParam);
        var user2 = Expression.Lambda(Expression.NotEqual(Expression.Property(tParam, nameof(Tag.Label)), Expression.Constant("y")), tParam);
        var collectionSelector = ReferenceCollectionSelectorFiltered(tagNav.TargetEntityType, outerParam, fk, user1, user2);

        Assert.True(NativeSelectManyBinder.TryBindReferenceNavUnwind(mongoQ, collectionSelector));
        Assert.IsType<MongoBinaryExpression>(mongoQ.Select.UnwindSource!.Filter);
        Assert.Equal(MongoBinaryOperator.AndAlso, ((MongoBinaryExpression)mongoQ.Select.UnwindSource!.Filter!).Operator);
    }

    [Fact]
    public void TryBindReferenceNavUnwind_folded_filtered_inner_binds_with_filter()
    {
        // The synthetic folded shape Where(root, fkPred && userPred). Handled by TrySplitCorrelation.
        var mongoQ = TestQuery();
        var tagNav = TagsNavigation(mongoQ);
        var outerParam = Expression.Parameter(typeof(Owner), "o");
        var tParam = Expression.Parameter(typeof(Tag), "t");
        var correlation = Expression.Equal(Expression.Property(outerParam, nameof(Owner.Id)), Expression.Property(tParam, nameof(Tag.OwnerId)));
        var extra = Expression.NotEqual(Expression.Property(tParam, nameof(Tag.Label)), Expression.Constant("x"));
        var predicate = Expression.Lambda(Expression.AndAlso(correlation, extra), tParam);
        var collectionSelector = ReferenceCollectionSelector(tagNav.TargetEntityType, outerParam, predicate);

        Assert.True(NativeSelectManyBinder.TryBindReferenceNavUnwind(mongoQ, collectionSelector));
        // The folded user conjunct must be CAPTURED, not swallowed by the correlation match: the filter is the
        // user predicate, scope-prefixed.
        var filter = Assert.IsType<MongoBinaryExpression>(mongoQ.Select.UnwindSource!.Filter);
        Assert.Equal(MongoBinaryOperator.NotEqual, filter.Operator);
        Assert.Equal("_lookup_Tags.Label", Assert.IsType<MongoFieldExpression>(filter.Left).ElementName);
    }

    [Fact]
    public void TryBindReferenceNavUnwind_folded_inner_not_null_user_filter_is_not_silently_dropped()
    {
        // Where(root, fkPred && (t.Label != null)) looks like a null-guarded FK correlation except for which key
        // is null-checked. If NativeCorrelationMatcher treated the user conjunct as the FK's guard, it would bind
        // with Filter == null and silently return all children. The user filter must survive or the bind decline.
        var mongoQ = TestQuery();
        var tagNav = TagsNavigation(mongoQ);
        var outerParam = Expression.Parameter(typeof(Owner), "o");
        var tParam = Expression.Parameter(typeof(Tag), "t");
        var correlation = Expression.Equal(
            Expression.Property(outerParam, nameof(Owner.Id)), Expression.Property(tParam, nameof(Tag.OwnerId)));
        var userNotNull = Expression.NotEqual(
            Expression.Property(tParam, nameof(Tag.Label)), Expression.Constant(null, typeof(string)));
        var predicate = Expression.Lambda(Expression.AndAlso(correlation, userNotNull), tParam);
        var collectionSelector = ReferenceCollectionSelector(tagNav.TargetEntityType, outerParam, predicate);

        Assert.True(NativeSelectManyBinder.TryBindReferenceNavUnwind(mongoQ, collectionSelector));

        var filter = Assert.IsType<MongoBinaryExpression>(mongoQ.Select.UnwindSource!.Filter);
        Assert.Equal(MongoBinaryOperator.NotEqual, filter.Operator);
        Assert.Equal("_lookup_Tags.Label", Assert.IsType<MongoFieldExpression>(filter.Left).ElementName);
    }

    [Fact]
    public void TryBindReferenceNavUnwind_folded_inner_not_null_user_filter_is_not_dropped_when_written_first()
    {
        // Reversed conjunct order must not change whether the user filter survives.
        var mongoQ = TestQuery();
        var tagNav = TagsNavigation(mongoQ);
        var outerParam = Expression.Parameter(typeof(Owner), "o");
        var tParam = Expression.Parameter(typeof(Tag), "t");
        var correlation = Expression.Equal(
            Expression.Property(outerParam, nameof(Owner.Id)), Expression.Property(tParam, nameof(Tag.OwnerId)));
        var userNotNull = Expression.NotEqual(
            Expression.Property(tParam, nameof(Tag.Label)), Expression.Constant(null, typeof(string)));
        var predicate = Expression.Lambda(Expression.AndAlso(userNotNull, correlation), tParam);
        var collectionSelector = ReferenceCollectionSelector(tagNav.TargetEntityType, outerParam, predicate);

        Assert.True(NativeSelectManyBinder.TryBindReferenceNavUnwind(mongoQ, collectionSelector));

        var filter = Assert.IsType<MongoBinaryExpression>(mongoQ.Select.UnwindSource!.Filter);
        Assert.Equal(MongoBinaryOperator.NotEqual, filter.Operator);
        Assert.Equal("_lookup_Tags.Label", Assert.IsType<MongoFieldExpression>(filter.Left).ElementName);
    }

    [Fact]
    public void TryBindReferenceNavUnwind_null_guarded_fk_correlation_still_binds_unfiltered()
    {
        // EF emits `(int?)o.Id != null && (int?)o.Id == t.OwnerId` for a nullable outer key. That guard is on the
        // FK's own key, so the bind must still go native with no element filter.
        var mongoQ = TestQuery();
        var tagNav = TagsNavigation(mongoQ);
        var outerParam = Expression.Parameter(typeof(Owner), "o");
        var tParam = Expression.Parameter(typeof(Tag), "t");
        var outerKey = Expression.Convert(Expression.Property(outerParam, nameof(Owner.Id)), typeof(int?));
        var guard = Expression.NotEqual(outerKey, Expression.Constant(null, typeof(int?)));
        var correlation = Expression.Equal(
            outerKey, Expression.Convert(Expression.Property(tParam, nameof(Tag.OwnerId)), typeof(int?)));
        var predicate = Expression.Lambda(Expression.AndAlso(guard, correlation), tParam);
        var collectionSelector = ReferenceCollectionSelector(tagNav.TargetEntityType, outerParam, predicate);

        Assert.True(NativeSelectManyBinder.TryBindReferenceNavUnwind(mongoQ, collectionSelector));

        var unwind = mongoQ.Select.UnwindSource!;
        Assert.Equal(MongoUnwindSourceKind.Reference, unwind.Kind);
        Assert.Same(tagNav.TargetEntityType, unwind.InnerEntityType);
        Assert.Null(unwind.Filter); // the FK's own null-guard is not a user filter
    }

    [Fact]
    public void TryBindReferenceNavUnwind_null_guarded_fk_correlation_with_user_filter_keeps_only_user_filter()
    {
        // The two cases combined: the FK's own null-guard is dropped, the user's `!= null` on a DIFFERENT
        // member is kept.
        var mongoQ = TestQuery();
        var tagNav = TagsNavigation(mongoQ);
        var outerParam = Expression.Parameter(typeof(Owner), "o");
        var tParam = Expression.Parameter(typeof(Tag), "t");
        var outerKey = Expression.Convert(Expression.Property(outerParam, nameof(Owner.Id)), typeof(int?));
        var guard = Expression.NotEqual(outerKey, Expression.Constant(null, typeof(int?)));
        var correlation = Expression.Equal(
            outerKey, Expression.Convert(Expression.Property(tParam, nameof(Tag.OwnerId)), typeof(int?)));
        var userNotNull = Expression.NotEqual(
            Expression.Property(tParam, nameof(Tag.Label)), Expression.Constant(null, typeof(string)));
        var predicate = Expression.Lambda(
            Expression.AndAlso(Expression.AndAlso(guard, correlation), userNotNull), tParam);
        var collectionSelector = ReferenceCollectionSelector(tagNav.TargetEntityType, outerParam, predicate);

        Assert.True(NativeSelectManyBinder.TryBindReferenceNavUnwind(mongoQ, collectionSelector));

        var filter = Assert.IsType<MongoBinaryExpression>(mongoQ.Select.UnwindSource!.Filter);
        Assert.Equal(MongoBinaryOperator.NotEqual, filter.Operator);
        Assert.Equal("_lookup_Tags.Label", Assert.IsType<MongoFieldExpression>(filter.Left).ElementName);
    }

    [Fact]
    public void TryBindReferenceNavUnwind_folded_untranslatable_user_filter_declines_without_mutation()
    {
        // A folded user conjunct the translator rejects (t.Label.ToUpper() != null) must decline the whole bind
        // — never bind while dropping the conjunct.
        var mongoQ = TestQuery();
        var tagNav = TagsNavigation(mongoQ);
        var outerParam = Expression.Parameter(typeof(Owner), "o");
        var tParam = Expression.Parameter(typeof(Tag), "t");
        var correlation = Expression.Equal(
            Expression.Property(outerParam, nameof(Owner.Id)), Expression.Property(tParam, nameof(Tag.OwnerId)));
        var userNotNull = Expression.NotEqual(
            Expression.Call(
                Expression.Property(tParam, nameof(Tag.Label)),
                typeof(string).GetMethod(nameof(string.ToUpper), System.Type.EmptyTypes)!),
            Expression.Constant(null, typeof(string)));
        var predicate = Expression.Lambda(Expression.AndAlso(correlation, userNotNull), tParam);
        var collectionSelector = ReferenceCollectionSelector(tagNav.TargetEntityType, outerParam, predicate);

        Assert.False(NativeSelectManyBinder.TryBindReferenceNavUnwind(mongoQ, collectionSelector));
        Assert.Null(mongoQ.Select.UnwindSource);
        Assert.Empty(mongoQ.Lookups); // no partial mutation
    }

    [Fact]
    public void TryBindReferenceNavUnwind_correlated_beyond_fk_filter_binds_with_expr_filter()
    {
        // A filter referencing the outer entity beyond the FK (t.Label == o.Name) goes native as a field-to-field
        // $expr: inner field prefixed, outer at root.
        var mongoQ = TestQuery();
        var tagNav = TagsNavigation(mongoQ);
        var outerParam = Expression.Parameter(typeof(Owner), "o");
        var tParam = Expression.Parameter(typeof(Tag), "t");
        var fk = Expression.Lambda(
            Expression.Equal(Expression.Property(outerParam, nameof(Owner.Id)), Expression.Property(tParam, nameof(Tag.OwnerId))),
            tParam);
        // t.Label == o.Name — correlated beyond the FK.
        var user = Expression.Lambda(
            Expression.Equal(Expression.Property(tParam, nameof(Tag.Label)), Expression.Property(outerParam, nameof(Owner.Name))),
            tParam);
        var collectionSelector = ReferenceCollectionSelectorFiltered(tagNav.TargetEntityType, outerParam, fk, user);

        Assert.True(NativeSelectManyBinder.TryBindReferenceNavUnwind(mongoQ, collectionSelector));

        var unwind = mongoQ.Select.UnwindSource!;
        Assert.Equal(MongoUnwindSourceKind.Reference, unwind.Kind);
        var bin = Assert.IsType<MongoBinaryExpression>(unwind.Filter);
        Assert.Equal(MongoBinaryOperator.Equal, bin.Operator);
        // Inner field prefixed with the lookup scope, outer at root — resolved by parameter identity.
        Assert.Equal("_lookup_Tags.Label", Assert.IsType<MongoFieldExpression>(bin.Left).ElementName);
        Assert.Equal("Name", Assert.IsType<MongoOuterFieldExpression>(bin.Right).ElementName);
    }

    [Fact]
    public void TryBindReferenceNavUnwind_mixed_inner_and_correlated_conjunct_binds()
    {
        // One .Where layer whose body ANDs an inner-only conjunct with a correlated one:
        // t.Label != "x" && t.Label == o.Name. The whole layer routes through the two-scope translator.
        var mongoQ = TestQuery();
        var tagNav = TagsNavigation(mongoQ);
        var outerParam = Expression.Parameter(typeof(Owner), "o");
        var tParam = Expression.Parameter(typeof(Tag), "t");
        var fk = Expression.Lambda(
            Expression.Equal(Expression.Property(outerParam, nameof(Owner.Id)), Expression.Property(tParam, nameof(Tag.OwnerId))),
            tParam);
        var innerOnly = Expression.NotEqual(Expression.Property(tParam, nameof(Tag.Label)), Expression.Constant("x"));
        var correlated = Expression.Equal(Expression.Property(tParam, nameof(Tag.Label)), Expression.Property(outerParam, nameof(Owner.Name)));
        var user = Expression.Lambda(Expression.AndAlso(innerOnly, correlated), tParam);
        var collectionSelector = ReferenceCollectionSelectorFiltered(tagNav.TargetEntityType, outerParam, fk, user);

        Assert.True(NativeSelectManyBinder.TryBindReferenceNavUnwind(mongoQ, collectionSelector));
        var and = Assert.IsType<MongoBinaryExpression>(mongoQ.Select.UnwindSource!.Filter);
        Assert.Equal(MongoBinaryOperator.AndAlso, and.Operator);
        // Both conjuncts' inner fields are prefixed; the correlated conjunct's outer field is at root.
        var left = Assert.IsType<MongoBinaryExpression>(and.Left);
        Assert.Equal("_lookup_Tags.Label", Assert.IsType<MongoFieldExpression>(left.Left).ElementName);
    }

    [Fact]
    public void TryBindReferenceNavUnwind_unsupported_correlated_operator_returns_false_without_mutation()
    {
        // t.Label.ToUpper() == o.Name — the two-scope translation rejects the operator, so the bind declines with
        // no partial mutation.
        var mongoQ = TestQuery();
        var tagNav = TagsNavigation(mongoQ);
        var outerParam = Expression.Parameter(typeof(Owner), "o");
        var tParam = Expression.Parameter(typeof(Tag), "t");
        var fk = Expression.Lambda(
            Expression.Equal(Expression.Property(outerParam, nameof(Owner.Id)), Expression.Property(tParam, nameof(Tag.OwnerId))),
            tParam);
        var user = Expression.Lambda(
            Expression.Equal(
                Expression.Call(Expression.Property(tParam, nameof(Tag.Label)), typeof(string).GetMethod(nameof(string.ToUpper), System.Type.EmptyTypes)!),
                Expression.Property(outerParam, nameof(Owner.Name))),
            tParam);
        var collectionSelector = ReferenceCollectionSelectorFiltered(tagNav.TargetEntityType, outerParam, fk, user);

        Assert.False(NativeSelectManyBinder.TryBindReferenceNavUnwind(mongoQ, collectionSelector));
        Assert.Null(mongoQ.Select.UnwindSource);
        Assert.Empty(mongoQ.Lookups); // no partial mutation
    }

    [Fact]
    public void TryBindReferenceNavUnwind_non_where_body_returns_false()
    {
        var mongoQ = TestQuery();
        var tagNav = TagsNavigation(mongoQ);
        var outerParam = Expression.Parameter(typeof(Owner), "o");
        var collectionSelector = Expression.Lambda(new EntityQueryRootExpression(tagNav.TargetEntityType), outerParam);

        Assert.False(NativeSelectManyBinder.TryBindReferenceNavUnwind(mongoQ, collectionSelector));
        Assert.Null(mongoQ.Select.UnwindSource);
        Assert.Empty(mongoQ.Lookups); // no partial mutation: the lookup is registered only AFTER a confirmed match
    }

    private class TagTransparentIdentifier
    {
        public Owner Outer { get; set; } = null!;
        public Tag Inner { get; set; } = null!;
    }

    private class NameAndLabelProjected
    {
        public string Name { get; set; } = "";
        public string Label { get; set; } = "";
    }

    [Fact]
    public void TryBindReferenceNavUnwind_reference_source_flows_through_two_scope_projection_binder()
    {
        // The lookup InnerScopePath ("_lookup_Tags") flows through TryBindTransparentIdentifierProjection: ti.Inner
        // resolves with the lookup-alias prefix and ti.Outer at root, as for an owned UnwindSource.
        var mongoQ = TestQuery();
        var tagNav = TagsNavigation(mongoQ);
        var lookup = new LookupExpression(tagNav, forceUnwind: true);
        mongoQ.Select.AddUnwindSource(
            MongoUnwindSource.Reference(LookupExpression.GetLookupAlias(tagNav), tagNav.TargetEntityType, lookup));

        var ti = Expression.Parameter(typeof(TagTransparentIdentifier), "ti");
        var outer = Expression.Property(ti, nameof(TagTransparentIdentifier.Outer));
        var inner = Expression.Property(ti, nameof(TagTransparentIdentifier.Inner));
        var body = Expression.MemberInit(Expression.New(typeof(NameAndLabelProjected)),
            Expression.Bind(typeof(NameAndLabelProjected).GetProperty(nameof(NameAndLabelProjected.Name))!,
                Expression.Property(outer, nameof(Owner.Name))),
            Expression.Bind(typeof(NameAndLabelProjected).GetProperty(nameof(NameAndLabelProjected.Label))!,
                Expression.Property(inner, nameof(Tag.Label))));
        var selector = Expression.Lambda(body, ti);

        Assert.True(NativeSelectManyBinder.TryBindTransparentIdentifierProjection(mongoQ, selector, out _));

        var nameP = mongoQ.Select.Projection.Single(p => p.Alias == "Name");
        Assert.Equal("Name", Assert.IsType<MongoFieldExpression>(nameP.Expression).ElementName);
        var labelP = mongoQ.Select.Projection.Single(p => p.Alias == "Label");
        Assert.Equal("_lookup_Tags.Label", Assert.IsType<MongoFieldExpression>(labelP.Expression).ElementName);
    }

    // TryBindNestedReferenceNavUnwind (2-level chained reference SelectMany): the second collectionSelector is
    // Queryable.Where(EntityQueryRootExpression<Leaf>, l => ti.Inner.Id == l.MidId), as TryBindReferenceNavUnwind
    // parses, except the outer-key side is a transparent-identifier member chain (ti.Inner.<pk>).

    private class NestedOwner
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public List<NestedMid> Mids { get; set; } = [];
    }

    private class NestedMid
    {
        public int Id { get; set; }
        public string Tag { get; set; } = "";
        public int OwnerId { get; set; }
        public List<NestedLeaf> Leaves { get; set; } = [];
    }

    private class NestedLeaf
    {
        public int Id { get; set; }
        public string Label { get; set; } = "";
        public int MidId { get; set; }
    }

    private class OwnerMidTi
    {
        public NestedOwner Outer { get; set; } = null!;
        public NestedMid Inner { get; set; } = null!;
    }

    private static MongoQueryExpression NestedTestQuery()
    {
        using var db = SingleEntityDbContext.Create<NestedOwner>(mb =>
        {
            mb.Entity<NestedMid>();
            mb.Entity<NestedOwner>().HasMany(o => o.Mids).WithOne().HasForeignKey(m => m.OwnerId);
            mb.Entity<NestedLeaf>();
            mb.Entity<NestedMid>().HasMany(m => m.Leaves).WithOne().HasForeignKey(l => l.MidId);
        });
        var entityType = db.Model.FindEntityType(typeof(NestedOwner))!;
        return new MongoQueryExpression(entityType);
    }

    // Simulates level 1 already bound (TryBindReferenceNavUnwind run against o.Mids).
    private static void BindLevel1(MongoQueryExpression mongoQ)
    {
        var midsNav = mongoQ.CollectionExpression.EntityType.FindNavigation(nameof(NestedOwner.Mids))!;
        var lookup = new LookupExpression(midsNav, forceUnwind: true);
        mongoQ.AddLookup(lookup);
        mongoQ.Select.AddUnwindSource(
            MongoUnwindSource.Reference(LookupExpression.GetLookupAlias(midsNav), midsNav.TargetEntityType, lookup));
    }

    private static IEntityType LeafEntityType(MongoQueryExpression mongoQ)
        => mongoQ.CollectionExpression.EntityType.FindNavigation(nameof(NestedOwner.Mids))!.TargetEntityType
            .FindNavigation(nameof(NestedMid.Leaves))!.TargetEntityType;

    // Queryable.Where(EntityQueryRootExpression<Leaf>, l => ti.Inner.<pk> == l.<fk>) — the level-2 shape.
    private static LambdaExpression NestedLeavesCorrelatedSelector(IEntityType leafEntityType, ParameterExpression ti)
    {
        var lParam = Expression.Parameter(typeof(NestedLeaf), "l");
        var predicate = Expression.Lambda(
            Expression.Equal(
                Expression.Property(Expression.Property(ti, nameof(OwnerMidTi.Inner)), nameof(NestedMid.Id)),
                Expression.Property(lParam, nameof(NestedLeaf.MidId))),
            lParam);
        var whereCall = Expression.Call(
            typeof(Queryable), nameof(Queryable.Where), [typeof(NestedLeaf)],
            new EntityQueryRootExpression(leafEntityType), Expression.Quote(predicate));
        return Expression.Lambda(whereCall, ti);
    }

    [Fact]
    public void TryBindNestedReferenceNavUnwind_binds_second_lookup_scoped_under_first()
    {
        var mongoQ = NestedTestQuery();
        BindLevel1(mongoQ);
        var leafEntityType = LeafEntityType(mongoQ);
        var ti = Expression.Parameter(typeof(OwnerMidTi), "ti");
        var collectionSelector = NestedLeavesCorrelatedSelector(leafEntityType, ti);

        Assert.True(NativeSelectManyBinder.TryBindNestedReferenceNavUnwind(mongoQ, collectionSelector));

        Assert.Equal(2, mongoQ.Select.UnwindSources.Count);
        var level2 = mongoQ.Select.UnwindSources[1];
        Assert.Equal(MongoUnwindSourceKind.Reference, level2.Kind);
        Assert.Equal("_lookup_Leaves", level2.InnerScopePath);
        Assert.Same(leafEntityType, level2.InnerEntityType);
        Assert.NotNull(level2.Lookup);
        Assert.True(level2.Lookup!.ForceUnwind);
        Assert.Equal("_lookup_Mids._id", level2.Lookup.LocalField);
        Assert.Null(level2.Filter);

        // MongoQueryExpression.GetPendingLookups must order the level-1 lookup before level-2's.
        var lookups = mongoQ.Lookups;
        Assert.Equal(2, lookups.Count);
        Assert.Equal("_lookup_Mids", lookups[0].As);
        Assert.Equal("_lookup_Leaves", lookups[1].As);
    }

    [Fact]
    public void TryBindNestedReferenceNavUnwind_returns_false_without_a_prior_reference_source()
    {
        var mongoQ = NestedTestQuery(); // no level-1 bind at all
        var ti = Expression.Parameter(typeof(OwnerMidTi), "ti");
        var collectionSelector = NestedLeavesCorrelatedSelector(LeafEntityType(mongoQ), ti);

        Assert.False(NativeSelectManyBinder.TryBindNestedReferenceNavUnwind(mongoQ, collectionSelector));
        Assert.Empty(mongoQ.Select.UnwindSources);
        Assert.Empty(mongoQ.Lookups);
    }

    [Fact]
    public void TryBindNestedReferenceNavUnwind_returns_false_when_prior_source_is_owned()
    {
        var mongoQ = NestedTestQuery();
        mongoQ.Select.AddUnwindSource(MongoUnwindSource.Owned("Mids", LeafEntityType(mongoQ))); // wrong Kind
        var ti = Expression.Parameter(typeof(OwnerMidTi), "ti");
        var collectionSelector = NestedLeavesCorrelatedSelector(LeafEntityType(mongoQ), ti);

        Assert.False(NativeSelectManyBinder.TryBindNestedReferenceNavUnwind(mongoQ, collectionSelector));
        Assert.Single(mongoQ.Select.UnwindSources); // untouched — no partial mutation
        Assert.Empty(mongoQ.Lookups);
    }

    [Fact]
    public void TryBindNestedReferenceNavUnwind_returns_false_for_non_where_body()
    {
        var mongoQ = NestedTestQuery();
        BindLevel1(mongoQ);
        var ti = Expression.Parameter(typeof(OwnerMidTi), "ti");
        var collectionSelector = Expression.Lambda(new EntityQueryRootExpression(LeafEntityType(mongoQ)), ti);

        Assert.False(NativeSelectManyBinder.TryBindNestedReferenceNavUnwind(mongoQ, collectionSelector));
        Assert.Single(mongoQ.Select.UnwindSources); // untouched
        Assert.Single(mongoQ.Lookups); // only BindLevel1's own lookup — no second one added on decline
    }

    [Fact]
    public void TryBindNestedReferenceNavUnwind_returns_false_when_correlation_is_not_ti_inner_rooted()
    {
        // l.MidId == l.MidId (a self-comparison, not ti.Inner.<pk>) resolves no navigation off the level-1 target;
        // must decline, not crash or mis-bind.
        var mongoQ = NestedTestQuery();
        BindLevel1(mongoQ);
        var ti = Expression.Parameter(typeof(OwnerMidTi), "ti");
        var lParam = Expression.Parameter(typeof(NestedLeaf), "l");
        var predicate = Expression.Lambda(
            Expression.Equal(Expression.Property(lParam, nameof(NestedLeaf.MidId)), Expression.Property(lParam, nameof(NestedLeaf.MidId))),
            lParam);
        var whereCall = Expression.Call(
            typeof(Queryable), nameof(Queryable.Where), [typeof(NestedLeaf)],
            new EntityQueryRootExpression(LeafEntityType(mongoQ)), Expression.Quote(predicate));
        var collectionSelector = Expression.Lambda(whereCall, ti);

        Assert.False(NativeSelectManyBinder.TryBindNestedReferenceNavUnwind(mongoQ, collectionSelector));
        Assert.Single(mongoQ.Select.UnwindSources);
        Assert.Single(mongoQ.Lookups); // only BindLevel1's own lookup — no second one added on decline
    }
}
