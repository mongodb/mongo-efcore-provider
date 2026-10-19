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
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Query;
using MongoDB.EntityFrameworkCore.Query.Expressions;
using MongoDB.EntityFrameworkCore.UnitTests.TestUtilities;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query.NativeTranslation;

/// <summary>
/// native-join-scope-nested-projection ticket, Task 1: <c>NativeJoinScopeProjectionBinder</c>'s new NESTED
/// wrapped-leaf recognizer arm (<c>CustomerId = new { Id = o.Customer!.CustomerID }</c>), driven through the
/// REAL EF Core translation pipeline rather than a hand-built fixture — same rationale, and the same
/// Owner/Order fixture shape, as <see cref="NativeJoinScopeProjectionBinderTests"/> (see that class's own
/// remarks for why a hand-declared "Outer"/"Inner" fixture would leave the production guard under test dead
/// code).
/// </summary>
public class NativeJoinScopeNestedProjectionTests
{
    private class Owner
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public List<Order> Orders { get; set; } = [];
    }

    private class Order
    {
        public int Id { get; set; }
        public int OwnerId { get; set; }
        public Owner? Owner { get; set; }
        public decimal Total { get; set; }
    }

    private static MongoQueryExpression TranslateJoinQuery(
        Func<IQueryable<Owner>, IQueryable<Order>, IQueryable> buildQuery)
    {
        using var db = SingleEntityDbContext.Create<Owner>(mb => mb.Entity<Order>());

        var query = buildQuery(db.Set<Owner>(), db.Set<Order>());

        var ccFactory = db.GetService<IQueryCompilationContextFactory>();
        var compilationContext = ccFactory.Create(async: false);

        var preprocessor = db.GetService<IQueryTranslationPreprocessorFactory>().Create(compilationContext);
        var preprocessed = preprocessor.Process(query.Expression);

        var visitor = db.GetService<IQueryableMethodTranslatingExpressionVisitorFactory>().Create(compilationContext);
        var result = visitor.Visit(preprocessed);

        Assert.NotNull(result);
        var shaped = Assert.IsAssignableFrom<ShapedQueryExpression>(result);
        return Assert.IsType<MongoQueryExpression>(shaped.QueryExpression);
    }

    // Separate fixture types for the chained (depth-2) decline test, for the exact reason recorded on
    // NativeJoinScopeProjectionBinderTests' own ChainOwner/ChainOrder/ChainOrderLine: sharing the depth-1
    // Order type above with an added collection navigation flips the MongoDB provider's convention for an
    // unregistered-target collection nav from cross-collection reference to OWNED, which breaks every
    // depth-1 test above even though none of them reference the new nav.
    private class ChainOwner
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public List<ChainOrder> Orders { get; set; } = [];
    }

    private class ChainOrder
    {
        public int Id { get; set; }
        public int OwnerId { get; set; }
        public ChainOwner? Owner { get; set; }
        public decimal Total { get; set; }
        public List<ChainOrderLine> Lines { get; set; } = [];
    }

    private class ChainOrderLine
    {
        public int Id { get; set; }
        public int OrderId { get; set; }
        public ChainOrder? Order { get; set; }
        public string Sku { get; set; } = "";
    }

    private static MongoQueryExpression TranslateThreeSourceJoinQuery(
        Func<IQueryable<ChainOwner>, IQueryable<ChainOrder>, IQueryable<ChainOrderLine>, IQueryable> buildQuery)
    {
        using var db = SingleEntityDbContext.Create<ChainOwner>(mb =>
        {
            mb.Entity<ChainOrder>();
            mb.Entity<ChainOrderLine>();
        });

        var query = buildQuery(db.Set<ChainOwner>(), db.Set<ChainOrder>(), db.Set<ChainOrderLine>());

        var ccFactory = db.GetService<IQueryCompilationContextFactory>();
        var compilationContext = ccFactory.Create(async: false);

        var preprocessor = db.GetService<IQueryTranslationPreprocessorFactory>().Create(compilationContext);
        var preprocessed = preprocessor.Process(query.Expression);

        var visitor = db.GetService<IQueryableMethodTranslatingExpressionVisitorFactory>().Create(compilationContext);
        var result = visitor.Visit(preprocessed);

        Assert.NotNull(result);
        var shaped = Assert.IsAssignableFrom<ShapedQueryExpression>(result);
        return Assert.IsType<MongoQueryExpression>(shaped.QueryExpression);
    }

    [Fact]
    public void Nested_nav_scalar_leaf_translates_to_document_construction_expression()
    {
        // `new { CustomerId = new { Id = r.OwnerId } }` — the motivating shape (EF's own
        // `Include_with_complex_projection`, modulo naming): the outer member's value is itself a nested
        // `new {...}` whose one member is a plain Inner-side scalar.
        var mongoQ = TranslateJoinQuery((owners, orders) =>
            owners.Join(orders, o => o.Id, r => r.OwnerId, (o, r) => new { CustomerId = new { Id = r.OwnerId } }));

        Assert.NotNull(mongoQ.Select.JoinScope);
        var projection = Assert.Single(mongoQ.Select.Projection, p => p.Alias == "CustomerId");
        var construction = Assert.IsType<MongoDocumentConstructionExpression>(projection.Expression);
        var member = Assert.Single(construction.Members);
        Assert.Equal("Id", member.MemberName);
        var field = Assert.IsType<MongoFieldExpression>(member.Value);
        Assert.Contains('.', field.ElementName);
        Assert.StartsWith(mongoQ.Select.JoinScope!.Levels[0].InnerPrefix + ".", field.ElementName);

        Assert.Single(mongoQ.Lookups);
        Assert.False(mongoQ.Select.HasUnconfirmedCandidateJoin);
        Assert.Equal(NativeRoute.Projection, mongoQ.Select.Route);
    }

    [Fact]
    public void Nested_leaf_with_outer_sourced_member_declines_whole_projection()
    {
        // An OUTER-sourced nested member DECLINES (final-review Critical 1). NativeJoinScopeTranslator's
        // two-scope translator resolves an outer-rooted access to a MongoOuterFieldExpression, which is a
        // SEALED SIBLING of MongoFieldExpression, not a subtype — and the shared read side
        // (MongoProjectionBindingRemovingExpressionVisitor.ReadDocumentConstructionMemberTyped) hard-casts
        // each staged member value to MongoFieldExpression. Accepting this shape (as this arm originally did)
        // therefore threw InvalidCastException at query-COMPILE time in the default Native mode, for a query
        // that works under MongoQueryMode.DriverLinq. Declining falls back instead — the exact behavior this
        // shape had before the arm existed.
        var mongoQ = TranslateJoinQuery((owners, orders) =>
            owners.Join(orders, o => o.Id, r => r.OwnerId, (o, r) => new { Wrap = new { OwnerName = o.Name } }));

        Assert.NotNull(mongoQ.Select.JoinScope);
        Assert.Empty(mongoQ.Select.Projection);
        Assert.Empty(mongoQ.Lookups);
        Assert.Equal(NativeRoute.Fallback, mongoQ.Select.Route);
    }

    [Fact]
    public void Nested_leaf_with_computed_outer_and_inner_member_declines_whole_projection()
    {
        // A COMPUTED nested member DECLINES, for the same reason as the outer-sourced case above: it
        // translates to a MongoBinaryExpression, which the shared read side's hard cast to
        // MongoFieldExpression cannot accept. This is the exact shape the final review reproduced against a
        // real database ("Unable to cast object of type 'MongoBinaryExpression' to type
        // 'MongoFieldExpression'"), reduced to the binder level here.
        var mongoQ = TranslateJoinQuery((owners, orders) =>
            owners.Join(orders, o => o.Id, r => r.OwnerId, (o, r) => new { Wrap = new { Combo = o.Id + r.OwnerId } }));

        Assert.NotNull(mongoQ.Select.JoinScope);
        Assert.Empty(mongoQ.Select.Projection);
        Assert.Empty(mongoQ.Lookups);
        Assert.Equal(NativeRoute.Fallback, mongoQ.Select.Route);
    }

    [Fact]
    public void Nested_leaf_with_inner_sourced_member_alongside_outer_sourced_member_declines()
    {
        // The accept set is per-MEMBER, and one inadmissible member declines the WHOLE outer leaf (no partial
        // commit): an Inner-sourced member that WOULD be accepted on its own does not rescue an
        // outer-sourced sibling inside the same nested body.
        var mongoQ = TranslateJoinQuery((owners, orders) =>
            owners.Join(orders, o => o.Id, r => r.OwnerId,
                (o, r) => new { Wrap = new { Inner = r.OwnerId, Outer = o.Name } }));

        Assert.NotNull(mongoQ.Select.JoinScope);
        Assert.Empty(mongoQ.Select.Projection);
        Assert.Empty(mongoQ.Lookups);
        Assert.Equal(NativeRoute.Fallback, mongoQ.Select.Route);
    }

    [Fact]
    public void Double_nested_leaf_declines_whole_projection()
    {
        // `new { A = new { B = new { C = r.OwnerId } } }` — two levels of nesting is out of scope (v1).
        var mongoQ = TranslateJoinQuery((owners, orders) =>
            owners.Join(orders, o => o.Id, r => r.OwnerId,
                (o, r) => new { A = new { B = new { C = r.OwnerId } } }));

        Assert.NotNull(mongoQ.Select.JoinScope);
        Assert.Empty(mongoQ.Select.Projection);
        Assert.Empty(mongoQ.Lookups);
        Assert.Equal(NativeRoute.Fallback, mongoQ.Select.Route);
    }

    [Fact]
    public void Nested_leaf_containing_whole_entity_member_declines_whole_projection()
    {
        // `new { A = new { Whole = r } }` — a nested member that is itself a whole-entity scope leaf is not a
        // scalar/computed value `NativeJoinScopeTranslator.TryTranslateValue` can translate, so the WHOLE
        // outer leaf declines (no partial commit) rather than being handled by the whole-entity arm one level
        // up (which never recurses into a nested body).
        var mongoQ = TranslateJoinQuery((owners, orders) =>
            owners.Join(orders, o => o.Id, r => r.OwnerId, (o, r) => new { A = new { Whole = r } }));

        Assert.NotNull(mongoQ.Select.JoinScope);
        Assert.Empty(mongoQ.Select.Projection);
        Assert.Empty(mongoQ.Lookups);
        Assert.Equal(NativeRoute.Fallback, mongoQ.Select.Route);
    }

    [Fact]
    public void Chained_join_scope_declines_nested_leaf()
    {
        // A genuine two-level join chain (scope.Levels.Count == 2) whose trailing selector contains a nested
        // wrapped leaf — the new arm is depth-1-only (`scope.Levels.Count == 1`), so this falls through to the
        // existing ordinary-leaf arm's own `Levels.Count > 1` decline, unchanged.
        var mongoQ = TranslateThreeSourceJoinQuery((owners, orders, lines) =>
            owners.Join(orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
                .Join(lines, e => e.r.Id, l => l.OrderId, (e, l) => new { Nested = new { Value = l.Sku } }));

        Assert.NotNull(mongoQ.Select.JoinScope);
        Assert.Equal(2, mongoQ.Select.JoinScope!.Levels.Count);
        Assert.Empty(mongoQ.Select.Projection);
        Assert.Equal(NativeRoute.Fallback, mongoQ.Select.Route);
    }

    [Fact]
    public void Nested_leaf_alongside_sibling_scalar_leaf_both_stage()
    {
        // `new { o.Name, CustomerId = new { Id = r.OwnerId } }` — a nested leaf may sit alongside an ordinary
        // sibling scalar leaf; both stage and the whole projection goes native.
        var mongoQ = TranslateJoinQuery((owners, orders) =>
            owners.Join(orders, o => o.Id, r => r.OwnerId,
                (o, r) => new { o.Name, CustomerId = new { Id = r.OwnerId } }));

        Assert.NotNull(mongoQ.Select.JoinScope);
        Assert.Equal(2, mongoQ.Select.Projection.Count);
        Assert.Equal(["Name", "CustomerId"], mongoQ.Select.Projection.Select(p => p.Alias).ToArray());
        Assert.IsType<MongoOuterFieldExpression>(mongoQ.Select.Projection[0].Expression);
        Assert.IsType<MongoDocumentConstructionExpression>(mongoQ.Select.Projection[1].Expression);

        Assert.Single(mongoQ.Lookups);
        Assert.False(mongoQ.Select.HasUnconfirmedCandidateJoin);
        Assert.Equal(NativeRoute.Projection, mongoQ.Select.Route);
    }

    [Fact]
    public void Nested_leaf_alongside_whole_entity_sibling_leaf_declines()
    {
        // `new { o, CustomerId = new { Id = r.OwnerId } }` — out-of-scope boundary: mixing a nested-document
        // leaf alongside a WHOLE-ENTITY sibling leaf declines, because MongoDocumentConstructionExpression is
        // deliberately not added to the whole-entity-forces-sibling-readability allow-list in this ticket.
        var mongoQ = TranslateJoinQuery((owners, orders) =>
            owners.Join(orders, o => o.Id, r => r.OwnerId,
                (o, r) => new { o, CustomerId = new { Id = r.OwnerId } }));

        Assert.NotNull(mongoQ.Select.JoinScope);
        Assert.Empty(mongoQ.Select.Projection);
        Assert.Empty(mongoQ.Lookups);
        Assert.Equal(NativeRoute.Fallback, mongoQ.Select.Route);
    }

    [Fact]
    public void Nested_leaf_with_case_insensitively_colliding_member_names_declines()
    {
        // `new { Wrap = new { Id = r.OwnerId, id = r.Id } }` — two nested members whose names differ only by
        // case collide under the same case-insensitive dedup discipline this file's outer loop already
        // applies to top-level aliases; the whole outer leaf declines rather than silently dropping one.
        var mongoQ = TranslateJoinQuery((owners, orders) =>
            owners.Join(orders, o => o.Id, r => r.OwnerId,
                (o, r) => new { Wrap = new { Id = r.OwnerId, id = r.Id } }));

        Assert.NotNull(mongoQ.Select.JoinScope);
        Assert.Empty(mongoQ.Select.Projection);
        Assert.Empty(mongoQ.Lookups);
        Assert.Equal(NativeRoute.Fallback, mongoQ.Select.Route);
    }
}
