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
/// Unit tests for <see cref="NativeSelectManyBinder"/>, which recognizes the INNER-<c>Select</c> form of an
/// owned-collection <c>SelectMany</c> — <c>o => o.Items.AsQueryable().Select(i => new {...})</c> — and binds
/// it to a native <c>$unwind</c> + <c>$project</c> (EF-347 slice 3, Task 2).
/// </summary>
public class NativeSelectManyBinderTests
{
    private class Item
    {
        public string Name { get; set; } = "";
        public decimal Price { get; set; }
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
        public List<Item> Items { get; set; } = [];
        public List<Tag> Tags { get; set; } = [];
    }

    private static MongoQueryExpression TestQuery()
    {
        using var db = SingleEntityDbContext.Create<Owner>(mb =>
        {
            mb.Entity<Owner>().OwnsMany(o => o.Items);
            // MongoRelationshipDiscoveryConvention defaults any navigation target not already registered as
            // its own independent entity type to OWNED (embedded) — the document-DB norm. Registering Tag
            // explicitly (with its own key + FK-based relationship) is what makes Owner.Tags a genuine
            // reference (non-owned) collection navigation, for the "reference nav is rejected" test below.
            mb.Entity<Tag>();
            mb.Entity<Owner>().HasMany(o => o.Tags).WithOne().HasForeignKey(t => t.OwnerId);
        });
        var entityType = db.Model.FindEntityType(typeof(Owner))!;
        return new MongoQueryExpression(entityType);
    }

    // Builds the collectionSelector lambda while letting the compiler infer the anonymous projection type —
    // mirrors the probe's confirmed shape: Queryable.Select(Queryable.AsQueryable(o.Nav), innerLambda).
    private static LambdaExpression Build<TResult>(Expression<Func<Owner, IQueryable<TResult>>> expr) => expr;

    // ── Success cases ────────────────────────────────────────────────────────────

    [Fact]
    public void Nested_select_binds_unwind_and_two_scope_projection()
    {
        var mongoQ = TestQuery();
        var collectionSelector = Build((Owner o) => o.Items.AsQueryable().Select(i => new { o.Name, i.Price }));

        Assert.True(NativeSelectManyBinder.TryBind(mongoQ, collectionSelector));

        Assert.Equal("Items", mongoQ.Select.UnwindSource!.InnerScopePath);
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

    // ── Rejection cases ──────────────────────────────────────────────────────────

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
        // The deferred explicit-result-selector form: collectionSelector's body is just the bare
        // AsQueryable() call, with the real projection (if any) living in a SEPARATE subsequent Select —
        // out of scope for this binder (Task 3+), so it must be rejected here.
        var mongoQ = TestQuery();
        Expression<Func<Owner, IQueryable<Item>>> collectionSelector = o => o.Items.AsQueryable();

        Assert.False(NativeSelectManyBinder.TryBind(mongoQ, collectionSelector));
        Assert.Null(mongoQ.Select.UnwindSource);
    }

    [Fact]
    public void Inner_where_before_select_returns_false()
    {
        var mongoQ = TestQuery();
        var collectionSelector = Build((Owner o) =>
            o.Items.AsQueryable().Where(i => i.Price > 0).Select(i => new { o.Name, i.Price }));

        Assert.False(NativeSelectManyBinder.TryBind(mongoQ, collectionSelector));
        Assert.Null(mongoQ.Select.UnwindSource);
    }

    // ── TryBindTransparentIdentifierProjection: explicit-result-selector / query-syntax form ───────
    // (EF-347 slice 4, Task 1). EF's nav-expansion for THIS form produces a SEPARATE trailing Select
    // over a TransparentIdentifier(Outer, Inner) source — the projection leaves are nested member
    // accesses ti.Outer.<m> / ti.Inner.<m> on a SINGLE ti parameter (not pre-folded), synthesized here
    // explicitly since the real EF nav-expansion output isn't reachable from a unit test.

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
        mongoQ.Select.UnwindSource = MongoUnwindSource.Owned("Items", ItemEntityType(mongoQ));

        Assert.True(NativeSelectManyBinder.TryBindTransparentIdentifierProjection(mongoQ, NameAndPriceSelector()));

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
        mongoQ.Select.UnwindSource = MongoUnwindSource.Owned("Items", ItemEntityType(mongoQ));

        var (ti, outer, inner) = TiScopes();
        var body = Expression.MemberInit(Expression.New(typeof(NamedProjected)),
            Expression.Bind(typeof(NamedProjected).GetProperty(nameof(NamedProjected.OuterName))!,
                Expression.Property(outer, nameof(Owner.Name))),
            Expression.Bind(typeof(NamedProjected).GetProperty(nameof(NamedProjected.InnerName))!,
                Expression.Property(inner, nameof(Item.Name))));
        var selector = Expression.Lambda(body, ti);

        Assert.True(NativeSelectManyBinder.TryBindTransparentIdentifierProjection(mongoQ, selector));

        var outerP = mongoQ.Select.Projection.Single(p => p.Alias == "OuterName");
        Assert.Equal("Name", Assert.IsType<MongoFieldExpression>(outerP.Expression).ElementName);
        var innerP = mongoQ.Select.Projection.Single(p => p.Alias == "InnerName");
        Assert.Equal("Items.Name", Assert.IsType<MongoFieldExpression>(innerP.Expression).ElementName);
    }

    [Fact]
    public void TryBindTransparentIdentifierProjection_no_unwind_source_returns_false()
    {
        var mongoQ = TestQuery();

        Assert.False(NativeSelectManyBinder.TryBindTransparentIdentifierProjection(mongoQ, NameAndPriceSelector()));
        Assert.Empty(mongoQ.Select.Projection);
    }

    [Fact]
    public void TryBindTransparentIdentifierProjection_bare_scope_leaf_returns_false()
    {
        var mongoQ = TestQuery();
        mongoQ.Select.UnwindSource = MongoUnwindSource.Owned("Items", ItemEntityType(mongoQ));

        var (ti, outer, _) = TiScopes();
        var body = Expression.MemberInit(Expression.New(typeof(EntityLeafProjected)),
            Expression.Bind(typeof(EntityLeafProjected).GetProperty(nameof(EntityLeafProjected.X))!, outer));
        var selector = Expression.Lambda(body, ti);

        Assert.False(NativeSelectManyBinder.TryBindTransparentIdentifierProjection(mongoQ, selector));
        Assert.Empty(mongoQ.Select.Projection);
    }

    [Fact]
    public void TryBindTransparentIdentifierProjection_computed_leaf_returns_false()
    {
        var mongoQ = TestQuery();
        mongoQ.Select.UnwindSource = MongoUnwindSource.Owned("Items", ItemEntityType(mongoQ));

        var (ti, _, inner) = TiScopes();
        var body = Expression.MemberInit(Expression.New(typeof(ComputedLeafProjected)),
            Expression.Bind(typeof(ComputedLeafProjected).GetProperty(nameof(ComputedLeafProjected.X))!,
                Expression.Multiply(Expression.Property(inner, nameof(Item.Price)), Expression.Constant(2m))));
        var selector = Expression.Lambda(body, ti);

        Assert.False(NativeSelectManyBinder.TryBindTransparentIdentifierProjection(mongoQ, selector));
        Assert.Empty(mongoQ.Select.Projection);
    }

    [Fact]
    public void TryBindTransparentIdentifierProjection_member_off_neither_scope_returns_false()
    {
        var mongoQ = TestQuery();
        mongoQ.Select.UnwindSource = MongoUnwindSource.Owned("Items", ItemEntityType(mongoQ));

        var (ti, _, _) = TiScopes();
        var other = Expression.Property(ti, nameof(TransparentIdentifier.Other));
        var body = Expression.MemberInit(Expression.New(typeof(OtherScopeProjected)),
            Expression.Bind(typeof(OtherScopeProjected).GetProperty(nameof(OtherScopeProjected.X))!,
                Expression.Property(other, nameof(Item.Name))));
        var selector = Expression.Lambda(body, ti);

        Assert.False(NativeSelectManyBinder.TryBindTransparentIdentifierProjection(mongoQ, selector));
        Assert.Empty(mongoQ.Select.Projection);
    }

    [Fact]
    public void TryBindTransparentIdentifierProjection_entity_valued_leaf_returns_false()
    {
        var mongoQ = TestQuery();
        mongoQ.Select.UnwindSource = MongoUnwindSource.Owned("Items", ItemEntityType(mongoQ));

        var (ti, outer, _) = TiScopes();
        var body = Expression.MemberInit(Expression.New(typeof(EntityValuedProjected)),
            Expression.Bind(typeof(EntityValuedProjected).GetProperty(nameof(EntityValuedProjected.X))!,
                Expression.Property(outer, nameof(Owner.Items))));
        var selector = Expression.Lambda(body, ti);

        Assert.False(NativeSelectManyBinder.TryBindTransparentIdentifierProjection(mongoQ, selector));
        Assert.Empty(mongoQ.Select.Projection);
    }

    // ── TryBindReferenceNavUnwind: cross-collection reference SelectMany (EF-347 slice 5, Task 2) ────
    // The reference collectionSelector is a correlated subquery — Queryable.Where(EntityQueryRootExpression
    // <Target>, o => c.pk == o.fk) — NOT a bare nav, per the spike (.superpowers/sdd/explicit-selectmany-spike.md)
    // and the design doc. Owner.Tags (a genuine reference collection nav, FK Tag.OwnerId) is reused as the
    // reference-nav fixture here.

    private static readonly System.Reflection.MethodInfo EfPropertyOfInt =
        typeof(EF).GetMethod(nameof(EF.Property))!.MakeGenericMethod(typeof(int));

    private static Expression ShadowProperty(ParameterExpression param, string name)
        => Expression.Call(EfPropertyOfInt, param, Expression.Constant(name));

    private static INavigation TagsNavigation(MongoQueryExpression mongoQ)
        => mongoQ.CollectionExpression.EntityType.FindNavigation(nameof(Owner.Tags))!;

    // Queryable.Where(EntityQueryRootExpression<Tag>, predicate) — the spike-confirmed correlated-subquery
    // shape a reference-nav SelectMany's collectionSelector normalizes to.
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
    public void TryBindReferenceNavUnwind_filtered_inner_returns_false()
    {
        var mongoQ = TestQuery();
        var tagNav = TagsNavigation(mongoQ);
        var outerParam = Expression.Parameter(typeof(Owner), "o");
        var tParam = Expression.Parameter(typeof(Tag), "t");
        var correlation = Expression.Equal(Expression.Property(outerParam, nameof(Owner.Id)), Expression.Property(tParam, nameof(Tag.OwnerId)));
        var extra = Expression.NotEqual(Expression.Property(tParam, nameof(Tag.Label)), Expression.Constant("x"));
        var predicate = Expression.Lambda(Expression.AndAlso(correlation, extra), tParam);
        var collectionSelector = ReferenceCollectionSelector(tagNav.TargetEntityType, outerParam, predicate);

        Assert.False(NativeSelectManyBinder.TryBindReferenceNavUnwind(mongoQ, collectionSelector));
        Assert.Null(mongoQ.Select.UnwindSource);
        Assert.Empty(mongoQ.Lookups); // no partial mutation: the lookup is registered only AFTER a confirmed match
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
        // Proves the generalized InnerScopePath ("_lookup_Tags") flows unchanged through slice 4's
        // TryBindTransparentIdentifierProjection — ti.Inner.<m> resolves against the inner scope with the
        // lookup-alias prefix, ti.Outer.<m> resolves against the outer (root) scope, exactly as for an owned
        // UnwindSource, just with a different (lookup-alias) InnerScopePath.
        var mongoQ = TestQuery();
        var tagNav = TagsNavigation(mongoQ);
        var lookup = new LookupExpression(tagNav, forceUnwind: true);
        mongoQ.Select.UnwindSource =
            MongoUnwindSource.Reference(LookupExpression.GetLookupAlias(tagNav), tagNav.TargetEntityType, lookup);

        var ti = Expression.Parameter(typeof(TagTransparentIdentifier), "ti");
        var outer = Expression.Property(ti, nameof(TagTransparentIdentifier.Outer));
        var inner = Expression.Property(ti, nameof(TagTransparentIdentifier.Inner));
        var body = Expression.MemberInit(Expression.New(typeof(NameAndLabelProjected)),
            Expression.Bind(typeof(NameAndLabelProjected).GetProperty(nameof(NameAndLabelProjected.Name))!,
                Expression.Property(outer, nameof(Owner.Name))),
            Expression.Bind(typeof(NameAndLabelProjected).GetProperty(nameof(NameAndLabelProjected.Label))!,
                Expression.Property(inner, nameof(Tag.Label))));
        var selector = Expression.Lambda(body, ti);

        Assert.True(NativeSelectManyBinder.TryBindTransparentIdentifierProjection(mongoQ, selector));

        var nameP = mongoQ.Select.Projection.Single(p => p.Alias == "Name");
        Assert.Equal("Name", Assert.IsType<MongoFieldExpression>(nameP.Expression).ElementName);
        var labelP = mongoQ.Select.Projection.Single(p => p.Alias == "Label");
        Assert.Equal("_lookup_Tags.Label", Assert.IsType<MongoFieldExpression>(labelP.Expression).ElementName);
    }
}
