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
/// <c>NativeJoinScopeProjectionBinder</c>'s nested wrapped-leaf arm
/// (<c>CustomerId = new { Id = o.Customer!.CustomerID }</c>), driven through the real EF Core pipeline with the
/// same Owner/Order fixture as <see cref="NativeJoinScopeProjectionBinderTests"/> (see its remarks for why).
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

    // Separate types for the depth-2 decline test: adding a collection nav to the depth-1 Order would flip the
    // convention for its unregistered target to owned, breaking the depth-1 tests (see
    // NativeJoinScopeProjectionBinderTests' ChainOwner/ChainOrder/ChainOrderLine).
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
        // `new { CustomerId = new { Id = r.OwnerId } }` (EF's Include_with_complex_projection): a nested `new {...}`
        // whose one member is a plain Inner-side scalar.
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
        // The shared read side (ReadDocumentConstructionMemberTyped) hard-casts each member to MongoFieldExpression,
        // and an outer-rooted access is a MongoOuterFieldExpression (a sibling, not a subtype); accepting would throw
        // InvalidCastException at compile time. Declining falls back.
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
        // A computed member is a MongoBinaryExpression, which fails the same hard cast; declines.
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
        // One inadmissible member declines the whole outer leaf (no partial commit), even beside an admissible one.
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
        // `new { A = new { B = new { C = r.OwnerId } } }`: two levels of nesting are out of scope.
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
        // `new { A = new { Whole = r } }`: a whole-entity nested member isn't translatable by TryTranslateValue, and
        // the whole-entity arm doesn't recurse into nested bodies, so the whole leaf declines.
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
        // A two-level join chain: the nested arm is depth-1-only, so this hits the ordinary arm's
        // `Levels.Count > 1` decline.
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
        // `new { o.Name, CustomerId = new { Id = r.OwnerId } }`: a nested leaf beside a sibling scalar leaf goes
        // native.
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
        // `new { o, CustomerId = new { Id = r.OwnerId } }`: MongoDocumentConstructionExpression isn't on the
        // whole-entity-forces-sibling-readability allow-list, so this declines.
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
        // `new { Wrap = new { Id = r.OwnerId, id = r.Id } }`: names differing only by case collide under the
        // case-insensitive dedup; the whole leaf declines rather than silently dropping one.
        var mongoQ = TranslateJoinQuery((owners, orders) =>
            owners.Join(orders, o => o.Id, r => r.OwnerId,
                (o, r) => new { Wrap = new { Id = r.OwnerId, id = r.Id } }));

        Assert.NotNull(mongoQ.Select.JoinScope);
        Assert.Empty(mongoQ.Select.Projection);
        Assert.Empty(mongoQ.Lookups);
        Assert.Equal(NativeRoute.Fallback, mongoQ.Select.Route);
    }
}
