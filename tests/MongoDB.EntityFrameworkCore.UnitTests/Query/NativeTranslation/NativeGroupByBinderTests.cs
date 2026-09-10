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
using Microsoft.EntityFrameworkCore.Metadata;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Query.Expressions;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query.NativeTranslation;

/// <summary>
/// Unit tests for <see cref="NativeGroupByBinder"/>, which parses a LINQ <c>GroupBy(key).Select(aggregate)</c>
/// into the <see cref="MongoGrouping"/> IR on <see cref="MongoSelectDefinition"/>.
/// </summary>
public class NativeGroupByBinderTests
{
    private class Order
    {
        public ObjectId Id { get; set; }
        public string Country { get; set; } = "";
        public string Region { get; set; } = "";
        public int Amount { get; set; }
        public int Quantity { get; set; }
        public DateTime OrderDate { get; set; }
    }

    private class OrderGroup
    {
        public string Key { get; set; } = "";
        public int Count { get; set; }
        public int Total { get; set; }
    }

    private static MongoQueryExpression TestQuery()
    {
        using var db = SingleEntityDbContext.Create<Order>();
        var entityType = db.Model.FindEntityType(typeof(Order))!;
        return new MongoQueryExpression(entityType);
    }

    // ── TryBindGroupKey ──────────────────────────────────────────────────────────

    [Fact]
    public void Scalar_key_binds_single_part_with_null_name()
    {
        var mongoQ = TestQuery();
        Expression<Func<Order, string>> key = x => x.Country;

        Assert.True(NativeGroupByBinder.TryBindGroupKey(mongoQ, key));

        var parts = mongoQ.Select.PendingGroupKey!;
        Assert.Single(parts);
        Assert.Null(parts[0].Name);
        var field = Assert.IsType<MongoFieldExpression>(parts[0].FieldRef);
        Assert.Equal("Country", field.ElementName);
        // Not finalized into Grouping until the projection is bound.
        Assert.Null(mongoQ.Select.Grouping);
    }

    [Fact]
    public void Composite_key_binds_two_named_parts()
    {
        var mongoQ = TestQuery();
        Expression<Func<Order, object>> key = x => new { x.Country, x.Region };

        Assert.True(NativeGroupByBinder.TryBindGroupKey(mongoQ, key));

        var parts = mongoQ.Select.PendingGroupKey!;
        Assert.Equal(2, parts.Count);
        Assert.Equal("Country", parts[0].Name);
        Assert.Equal("Region", parts[1].Name);
        Assert.Equal("Country", Assert.IsType<MongoFieldExpression>(parts[0].FieldRef).ElementName);
        Assert.Equal("Region", Assert.IsType<MongoFieldExpression>(parts[1].FieldRef).ElementName);
    }

    [Fact]
    public void Computed_key_returns_false_and_leaves_state_unset()
    {
        var mongoQ = TestQuery();
        Expression<Func<Order, int>> key = x => x.OrderDate.Year;

        Assert.False(NativeGroupByBinder.TryBindGroupKey(mongoQ, key));
        Assert.Null(mongoQ.Select.PendingGroupKey);
        Assert.Null(mongoQ.Select.Grouping);
    }

    [Fact]
    public void Composite_key_with_computed_part_returns_false()
    {
        var mongoQ = TestQuery();
        Expression<Func<Order, object>> key = x => new { x.Country, Yr = x.OrderDate.Year };

        Assert.False(NativeGroupByBinder.TryBindGroupKey(mongoQ, key));
        Assert.Null(mongoQ.Select.PendingGroupKey);
    }

    [Fact]
    public void Paging_present_returns_false()
    {
        var mongoQ = TestQuery();
        mongoQ.Select.AppendLimit(new MongoConstantExpression(10, null));
        Expression<Func<Order, string>> key = x => x.Country;

        Assert.False(NativeGroupByBinder.TryBindGroupKey(mongoQ, key));
        Assert.Null(mongoQ.Select.PendingGroupKey);
    }

    [Fact]
    public void Orderings_present_returns_false()
    {
        var mongoQ = TestQuery();
        mongoQ.Select.StartOrReplaceSort(new MongoOrdering(new MongoConstantExpression(0, null), true));
        Expression<Func<Order, string>> key = x => x.Country;

        Assert.False(NativeGroupByBinder.TryBindGroupKey(mongoQ, key));
    }

    // ── TryBindGroupProjection ─────────────────────────────────────────────────────

    private static MongoQueryExpression BoundScalarKeyQuery()
    {
        var mongoQ = TestQuery();
        Expression<Func<Order, string>> key = x => x.Country;
        Assert.True(NativeGroupByBinder.TryBindGroupKey(mongoQ, key));
        return mongoQ;
    }

    [Fact]
    public void Scalar_key_and_count_binds_group()
    {
        var mongoQ = BoundScalarKeyQuery();
        Expression<Func<IGrouping<string, Order>, object>> proj =
            g => new { Count = g.Count() };

        Assert.True(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out _));

        var grouping = mongoQ.Select.Grouping!;
        Assert.Single(grouping.Key);
        Assert.Null(grouping.Key[0].Name);
        Assert.Collection(grouping.Accumulators,
            a =>
            {
                Assert.Equal("Count", a.OutputField);
                Assert.Equal("$sum", a.Operator);
                Assert.Null(a.Operand);
            });
    }

    [Fact]
    public void Sum_with_member_selector_binds_sum_operand()
    {
        var mongoQ = BoundScalarKeyQuery();
        Expression<Func<IGrouping<string, Order>, object>> proj =
            g => new { Total = g.Sum(x => x.Amount) };

        Assert.True(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out _));

        var acc = Assert.Single(mongoQ.Select.Grouping!.Accumulators);
        Assert.Equal("Total", acc.OutputField);
        Assert.Equal("$sum", acc.Operator);
        Assert.Equal("Amount", Assert.IsType<MongoFieldExpression>(acc.Operand).ElementName);
    }

    [Fact]
    public void Min_max_average_map_to_operators()
    {
        var mongoQ = BoundScalarKeyQuery();
        Expression<Func<IGrouping<string, Order>, object>> proj =
            g => new { Lo = g.Min(x => x.Amount), Hi = g.Max(x => x.Amount), Av = g.Average(x => x.Amount) };

        Assert.True(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out _));

        var accs = mongoQ.Select.Grouping!.Accumulators;
        Assert.Equal("$min", accs.Single(a => a.OutputField == "Lo").Operator);
        Assert.Equal("$max", accs.Single(a => a.OutputField == "Hi").Operator);
        Assert.Equal("$avg", accs.Single(a => a.OutputField == "Av").Operator);
    }

    [Fact]
    public void Key_access_is_not_an_accumulator()
    {
        var mongoQ = BoundScalarKeyQuery();
        Expression<Func<IGrouping<string, Order>, object>> proj =
            g => new { g.Key, Count = g.Count() };

        Assert.True(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out _));

        // Only the Count accumulator; the key member is not an accumulator.
        var acc = Assert.Single(mongoQ.Select.Grouping!.Accumulators);
        Assert.Equal("Count", acc.OutputField);
    }

    [Fact]
    public void MemberInit_dto_projection_binds()
    {
        var mongoQ = BoundScalarKeyQuery();
        Expression<Func<IGrouping<string, Order>, OrderGroup>> proj =
            g => new OrderGroup { Key = g.Key, Count = g.Count(), Total = g.Sum(x => x.Amount) };

        Assert.True(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out _));

        var accs = mongoQ.Select.Grouping!.Accumulators;
        Assert.Equal(2, accs.Count);
        Assert.Contains(accs, a => a.OutputField == "Count" && a.Operator == "$sum" && a.Operand == null);
        Assert.Contains(accs, a => a.OutputField == "Total" && a.Operator == "$sum" && a.Operand != null);
    }

    [Fact]
    public void Computed_operand_returns_false()
    {
        var mongoQ = BoundScalarKeyQuery();
        Expression<Func<IGrouping<string, Order>, object>> proj =
            g => new { Total = g.Sum(x => x.Amount * x.Quantity) };

        Assert.False(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out _));
        Assert.Null(mongoQ.Select.Grouping);
    }

    [Fact]
    public void Projection_with_no_accumulators_returns_false()
    {
        var mongoQ = BoundScalarKeyQuery();
        Expression<Func<IGrouping<string, Order>, object>> proj =
            g => new { g.Key };

        Assert.False(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out _));
        Assert.Null(mongoQ.Select.Grouping);
    }

    [Fact]
    public void Aggregate_over_non_grouping_source_returns_false()
    {
        // The accumulator's SOURCE is a DIFFERENT sequence (an in-scope array), not the grouping parameter g.
        // Binding it to a $group accumulator would silently drop the real computation and return the group's
        // row count instead, diverging from driver-LINQ. It must NOT bind → the projection falls back.
        var mongoQ = BoundScalarKeyQuery();
        var others = new[] { 1, 2, 3, 4, 5, 6 };
        Expression<Func<IGrouping<string, Order>, object>> proj =
            g => new { Count = others.Count() };

        Assert.False(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out _));
        Assert.Null(mongoQ.Select.Grouping);
    }

    [Fact]
    public void Sum_over_non_grouping_source_returns_false()
    {
        var mongoQ = BoundScalarKeyQuery();
        var others = new[] { new Order { Amount = 1 } };
        Expression<Func<IGrouping<string, Order>, object>> proj =
            g => new { Total = others.Sum(x => x.Amount) };

        Assert.False(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out _));
        Assert.Null(mongoQ.Select.Grouping);
    }

    [Fact]
    public void Accumulator_output_field_named_id_returns_false()
    {
        // An accumulator whose result member is literally "_id" would emit a second "_id" element into the
        // $group document (which already carries the grouping key under "_id"), throwing a BsonDocument
        // duplicate-key exception at pipeline build rather than falling back cleanly. Reject it here so the
        // shape falls back to driver-LINQ (and throws only under NativeOnly).
        var mongoQ = BoundScalarKeyQuery();
        Expression<Func<IGrouping<string, Order>, object>> proj =
            g => new { _id = g.Count() };

        Assert.False(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out _));
        Assert.Null(mongoQ.Select.Grouping);
    }

    [Fact]
    public void Key_member_projected_to_id_alias_still_binds()
    {
        // A KEY member projected to an "_id" alias reads the group's own "_id" back and does NOT collide with
        // the reserved field, so it must remain natively representable — the guard is scoped to accumulators.
        var mongoQ = BoundScalarKeyQuery();
        Expression<Func<IGrouping<string, Order>, object>> proj =
            g => new { _id = g.Key, Count = g.Count() };

        Assert.True(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out _));

        var acc = Assert.Single(mongoQ.Select.Grouping!.Accumulators);
        Assert.Equal("Count", acc.OutputField);
    }

    [Fact]
    public void Projection_without_bound_key_returns_false()
    {
        var mongoQ = TestQuery(); // TryBindGroupKey never called
        Expression<Func<IGrouping<string, Order>, object>> proj =
            g => new { Count = g.Count() };

        Assert.False(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out _));
        Assert.Null(mongoQ.Select.Grouping);
    }

    // ── TryBindGroupProjection: pending OrderBy/ThenBy (composed before the Select) ─────────────

    [Fact]
    public void Pending_order_by_key_resolves_to_id_reference()
    {
        var mongoQ = BoundScalarKeyQuery();
        Expression<Func<IGrouping<string, Order>, string>> orderKey = g => g.Key;
        mongoQ.Select.PendingGroupOrderings = [(true, orderKey)];

        Expression<Func<IGrouping<string, Order>, object>> proj =
            g => new { g.Key, Count = g.Count() };
        Assert.True(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out _));

        var order = Assert.Single(mongoQ.Select.GroupOrderOp!.Orderings);
        Assert.True(order.Ascending);
        Assert.Equal("_id", Assert.IsType<MongoElementRefExpression>(order.KeySelector).Path);
        Assert.Null(mongoQ.Select.PendingGroupOrderings);
    }

    [Fact]
    public void Pending_order_by_aggregate_adds_its_own_accumulator_alongside_the_projected_one()
    {
        var mongoQ = BoundScalarKeyQuery();
        Expression<Func<IGrouping<string, Order>, int>> orderKey = g => g.Count();
        mongoQ.Select.PendingGroupOrderings = [(true, orderKey)];

        Expression<Func<IGrouping<string, Order>, object>> proj =
            g => new { g.Key, Count = g.Count() };
        Assert.True(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out _));

        // Two SEPARATE $sum:1 accumulators — one for the ordering, one for the projection. Deliberately not
        // de-duplicated (see this task's Architecture note).
        Assert.Equal(2, mongoQ.Select.Grouping!.Accumulators.Count);
        Assert.Contains(mongoQ.Select.Grouping.Accumulators, a => a.OutputField == "Count" && a.Operator == "$sum" && a.Operand == null);
        Assert.Contains(mongoQ.Select.Grouping.Accumulators, a => a.OutputField == "_orderAgg0" && a.Operator == "$sum" && a.Operand == null);

        var order = Assert.Single(mongoQ.Select.GroupOrderOp!.Orderings);
        Assert.Equal("_orderAgg0", Assert.IsType<MongoElementRefExpression>(order.KeySelector).Path);
    }

    [Fact]
    public void Pending_order_by_aggregate_not_projected_still_adds_accumulator_but_not_flattened()
    {
        var mongoQ = BoundScalarKeyQuery();
        Expression<Func<IGrouping<string, Order>, int>> orderKey = g => g.Count();
        mongoQ.Select.PendingGroupOrderings = [(true, orderKey)];

        // Projects Sum, not Count — but the ordering still needs Count computed in the SAME $group.
        Expression<Func<IGrouping<string, Order>, object>> proj =
            g => new { g.Key, Total = g.Sum(x => x.Amount) };
        Assert.True(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out _));

        Assert.Equal(2, mongoQ.Select.Grouping!.Accumulators.Count);
        Assert.Contains(mongoQ.Select.Grouping.Accumulators, a => a.OutputField == "_orderAgg0" && a.Operator == "$sum" && a.Operand == null);
        Assert.Contains(mongoQ.Select.Grouping.Accumulators, a => a.OutputField == "Total" && a.Operator == "$sum");

        // The flatten projection (what the final $project keeps) only has the user-requested members.
        Assert.DoesNotContain(mongoQ.Select.Projection, p => p.Alias == "_orderAgg0");
        Assert.Contains(mongoQ.Select.Projection, p => p.Alias == "Total");
    }

    [Fact]
    public void Pending_order_by_key_then_by_aggregate_resolves_both_in_order()
    {
        var mongoQ = BoundScalarKeyQuery();
        Expression<Func<IGrouping<string, Order>, string>> byKey = g => g.Key;
        Expression<Func<IGrouping<string, Order>, int>> byCount = g => g.Count();
        mongoQ.Select.PendingGroupOrderings = [(true, byKey), (false, byCount)];

        Expression<Func<IGrouping<string, Order>, object>> proj =
            g => new { g.Key, Count = g.Count() };
        Assert.True(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out _));

        Assert.Collection(mongoQ.Select.GroupOrderOp!.Orderings,
            o =>
            {
                Assert.True(o.Ascending);
                Assert.Equal("_id", Assert.IsType<MongoElementRefExpression>(o.KeySelector).Path);
            },
            o =>
            {
                Assert.False(o.Ascending);
                Assert.Equal("_orderAgg0", Assert.IsType<MongoElementRefExpression>(o.KeySelector).Path);
            });
    }

    [Fact]
    public void Pending_order_by_computed_expression_declines()
    {
        var mongoQ = BoundScalarKeyQuery();
        Expression<Func<IGrouping<string, Order>, int>> orderKey = g => g.Count() * 2;
        mongoQ.Select.PendingGroupOrderings = [(true, orderKey)];

        Expression<Func<IGrouping<string, Order>, object>> proj =
            g => new { g.Key, Count = g.Count() };

        Assert.False(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out _));
    }

    [Fact]
    public void Pending_order_by_bare_key_over_composite_key_declines()
    {
        var mongoQ = TestQuery();
        Expression<Func<Order, object>> key = x => new { x.Country, x.Region };
        Assert.True(NativeGroupByBinder.TryBindGroupKey(mongoQ, key));

        Expression<Func<IGrouping<object, Order>, object>> orderKey = g => g.Key;
        mongoQ.Select.PendingGroupOrderings = [(true, orderKey)];

        Expression<Func<IGrouping<object, Order>, object>> proj =
            g => new { Count = g.Count() };

        Assert.False(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out _));
    }

    // ── TryBindGroupTerminalAggregate ──────────────────────────────────────────────
    // GroupBy(key).{Count()|LongCount()|Any()|Any(pred)|All(pred)|Count(pred)|LongCount(pred)} with NO
    // intervening Select — the "GroupBy_without_aggregate" family (EF-449).

    [Fact]
    public void Bare_Count_with_no_predicate_binds_zero_accumulator_grouping()
    {
        var mongoQ = BoundScalarKeyQuery();

        Assert.True(NativeGroupByBinder.TryBindGroupTerminalAggregate(
            mongoQ, MongoAggregateOperator.Count, null, typeof(int)));

        Assert.NotNull(mongoQ.Select.Grouping);
        Assert.Empty(mongoQ.Select.Grouping!.Accumulators);
        Assert.Null(mongoQ.Select.PostGroupPredicate);
        Assert.Equal(MongoAggregateOperator.Count, mongoQ.Select.Cardinality!.Aggregate);
    }

    [Fact]
    public void Bare_Any_with_no_predicate_binds_zero_accumulator_grouping()
    {
        var mongoQ = BoundScalarKeyQuery();

        Assert.True(NativeGroupByBinder.TryBindGroupTerminalAggregate(
            mongoQ, MongoAggregateOperator.Any, null, typeof(bool)));

        Assert.NotNull(mongoQ.Select.Grouping);
        Assert.Empty(mongoQ.Select.Grouping!.Accumulators);
        Assert.Null(mongoQ.Select.PostGroupPredicate);
        Assert.True(mongoQ.Select.Cardinality!.PresenceOnly);
    }

    [Fact]
    public void Count_with_count_predicate_binds_accumulator_and_match()
    {
        var mongoQ = BoundScalarKeyQuery();
        Expression<Func<IGrouping<string, Order>, bool>> pred = g => g.Count() > 1;

        Assert.True(NativeGroupByBinder.TryBindGroupTerminalAggregate(
            mongoQ, MongoAggregateOperator.Count, pred, typeof(int)));

        var acc = Assert.Single(mongoQ.Select.Grouping!.Accumulators);
        Assert.Equal("$sum", acc.Operator);
        Assert.Null(acc.Operand);

        var match = Assert.IsType<MongoBinaryExpression>(mongoQ.Select.PostGroupPredicate);
        Assert.Equal(MongoBinaryOperator.GreaterThan, match.Operator);
        var left = Assert.IsType<MongoElementRefExpression>(match.Left);
        Assert.Equal(acc.OutputField, left.Path);
        var right = Assert.IsType<MongoConstantExpression>(match.Right);
        Assert.Equal(1, right.Value);
    }

    [Fact]
    public void Any_with_count_predicate_binds_direct_comparison()
    {
        var mongoQ = BoundScalarKeyQuery();
        Expression<Func<IGrouping<string, Order>, bool>> pred = g => g.Count() > 1;

        Assert.True(NativeGroupByBinder.TryBindGroupTerminalAggregate(
            mongoQ, MongoAggregateOperator.Any, pred, typeof(bool)));

        var match = Assert.IsType<MongoBinaryExpression>(mongoQ.Select.PostGroupPredicate);
        Assert.Equal(MongoBinaryOperator.GreaterThan, match.Operator);
    }

    [Fact]
    public void All_with_count_predicate_negates_comparison()
    {
        var mongoQ = BoundScalarKeyQuery();
        Expression<Func<IGrouping<string, Order>, bool>> pred = g => g.Count() > 1;

        Assert.True(NativeGroupByBinder.TryBindGroupTerminalAggregate(
            mongoQ, MongoAggregateOperator.All, pred, typeof(bool)));

        // All(pred) matches the COMPLEMENT: presence of a group failing pred means All is false. Relational
        // operators are $not-wrapped, never inverted (mirrors MongoExpressionNegator's own rule).
        var wrapped = Assert.IsType<MongoUnaryExpression>(mongoQ.Select.PostGroupPredicate);
        Assert.Equal(MongoUnaryOperator.Not, wrapped.Operator);
        var inner = Assert.IsType<MongoBinaryExpression>(wrapped.Operand);
        Assert.Equal(MongoBinaryOperator.GreaterThan, inner.Operator);
        Assert.False(mongoQ.Select.Cardinality!.PresentValue as bool?);
    }

    [Fact]
    public void Reversed_operand_order_binds_flipped_operator()
    {
        var mongoQ = BoundScalarKeyQuery();
        Expression<Func<IGrouping<string, Order>, bool>> pred = g => 1 < g.Count();

        Assert.True(NativeGroupByBinder.TryBindGroupTerminalAggregate(
            mongoQ, MongoAggregateOperator.Any, pred, typeof(bool)));

        var match = Assert.IsType<MongoBinaryExpression>(mongoQ.Select.PostGroupPredicate);
        Assert.Equal(MongoBinaryOperator.GreaterThan, match.Operator); // flipped from < to >
        Assert.IsType<MongoElementRefExpression>(match.Left);
        Assert.IsType<MongoConstantExpression>(match.Right);
    }

    [Fact]
    public void Compound_predicate_returns_false()
    {
        var mongoQ = BoundScalarKeyQuery();
        Expression<Func<IGrouping<string, Order>, bool>> pred = g => g.Count() > 1 && g.Count() < 10;

        Assert.False(NativeGroupByBinder.TryBindGroupTerminalAggregate(
            mongoQ, MongoAggregateOperator.Any, pred, typeof(bool)));
        Assert.Null(mongoQ.Select.Grouping);
    }

    [Fact]
    public void Non_bare_group_declines()
    {
        var mongoQ = TestQuery(); // no PendingGroupKey at all

        Assert.False(NativeGroupByBinder.TryBindGroupTerminalAggregate(
            mongoQ, MongoAggregateOperator.Count, null, typeof(int)));
    }

    [Fact]
    public void Already_finalized_grouping_declines()
    {
        var mongoQ = BoundScalarKeyQuery();
        Expression<Func<IGrouping<string, Order>, object>> proj =
            g => new { Count = g.Count() };
        Assert.True(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out _));

        Assert.False(NativeGroupByBinder.TryBindGroupTerminalAggregate(
            mongoQ, MongoAggregateOperator.Count, null, typeof(int)));
    }
}
