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
using Microsoft.EntityFrameworkCore.Metadata;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.Query.Expressions;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query.Expressions;

/// <summary>
/// Truth-table tests for the predicates that gate native-versus-fallback decisions on <see cref="LookupExpression"/>,
/// <see cref="JoinInfo"/> and <see cref="MongoQueryExpression"/>'s lookup ordering and registration.
/// </summary>
public class LookupExpressionGateTests
{
    private class Customer
    {
        public ObjectId Id { get; set; }
        public ICollection<Order> Orders { get; set; } = [];
        public Profile? Profile { get; set; }
    }

    private class Order
    {
        public ObjectId Id { get; set; }
        public ObjectId CustomerId { get; set; }
        public Customer? Customer { get; set; }
        public ObjectId? CouponId { get; set; }
        public Coupon? Coupon { get; set; }
    }

    private class Coupon
    {
        public ObjectId Id { get; set; }
    }

    private class Profile
    {
        public ObjectId Id { get; set; }
        public ObjectId CustomerId { get; set; }
        public Customer? Customer { get; set; }
    }

    private class GateDbContext : DbContext
    {
        public DbSet<Customer> Customers { get; set; } = null!;
        public DbSet<Order> Orders { get; set; } = null!;
        public DbSet<Coupon> Coupons { get; set; } = null!;
        public DbSet<Profile> Profiles { get; set; } = null!;

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
            => optionsBuilder
                .UseMongoDB("mongodb://localhost:27017", "UnitTests")
                .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
    }

    private static IEntityType EntityType<T>(GateDbContext db) => db.Model.FindEntityType(typeof(T))!;

    private static INavigation Nav<T>(GateDbContext db, string name)
        => EntityType<T>(db).GetNavigations().Single(n => n.Name == name);

    private static LookupExpression KeyedLookup(
        IEntityType type, string alias, string localField = "k", string foreignField = "_id", bool correlatedArray = false)
        => new(type, "C", localField, foreignField, alias, forceUnwind: false) { IsCorrelatedArraySource = correlatedArray };

    private static MongoConstantExpression Const(object? value) => new(value, null);

    private static MongoParameterExpression Param(string name) => new(name, null);

    // ---- IsNativeOnlyArray / IsBridgeReadableArray / IsBridgeIgnorable ----

    [Fact]
    public void IsNativeOnlyArray_is_false_for_a_plain_lookup()
    {
        using var db = new GateDbContext();
        var lookup = KeyedLookup(EntityType<Order>(db), "_lookup_a", correlatedArray: true);

        Assert.False(lookup.IsNativeOnlyArray);
        Assert.True(lookup.IsBridgeReadableArray);
    }

    [Fact]
    public void IsNativeOnlyArray_is_true_for_each_native_only_feature()
    {
        using var db = new GateDbContext();
        var type = EntityType<Order>(db);
        var ordering = new List<(string, int)> { ("Id", 1) };

        var lookups = new[]
        {
            new LookupExpression(type, "C", "k", "_id", "a", false) { InnerPredicate = Const(true) },
            new LookupExpression(type, "C", "k", "_id", "b", false) { ElementSort = ordering },
            new LookupExpression(type, "C", "k", "_id", "c", false) { ElementLimit = Const(1) },
            new LookupExpression(type, "C", "k", "_id", "d", false) { ElementSkip = Const(1) },
            new LookupExpression(type, "C", "k", "_id", "e", false) { OuterConditions = [new LookupOuterCondition("x", Const(1), false)] },
        };

        foreach (var lookup in lookups)
        {
            Assert.True(lookup.IsNativeOnlyArray, lookup.As);
        }

        var withLeaf = KeyedLookup(type, "f");
        withLeaf.ElementLeafAliases.Add("Os");
        Assert.True(withLeaf.IsNativeOnlyArray);
    }

    [Fact]
    public void IsBridgeReadableArray_requires_a_correlated_array_source()
    {
        using var db = new GateDbContext();
        var type = EntityType<Order>(db);

        Assert.False(KeyedLookup(type, "a").IsBridgeReadableArray);
        Assert.True(KeyedLookup(type, "b", correlatedArray: true).IsBridgeReadableArray);

        var native = KeyedLookup(type, "c", correlatedArray: true);
        native.InnerPredicate = Const(true);
        Assert.False(native.IsBridgeReadableArray);
    }

    [Fact]
    public void IsBridgeIgnorable_truth_table()
    {
        using var db = new GateDbContext();
        var type = EntityType<Order>(db);
        var outer = new[] { new LookupOuterCondition("x", Const(1), false) };

        // Not a correlated array: never ignorable.
        var notArray = new LookupExpression(type, "C", "k", "_id", "a", false) { InnerPredicate = Const(true) };
        Assert.False(notArray.IsBridgeIgnorable);

        // Correlated array with nothing the bridge can't apply itself: not ignorable (it is read).
        Assert.False(KeyedLookup(type, "b", correlatedArray: true).IsBridgeIgnorable);

        // Element filter only / outer conditions only: ignorable.
        var filtered = KeyedLookup(type, "c", correlatedArray: true);
        filtered.InnerPredicate = Const(true);
        Assert.True(filtered.IsBridgeIgnorable);

        var gated = new LookupExpression(type, "C", "k", "_id", "d", false) { IsCorrelatedArraySource = true, OuterConditions = outer };
        Assert.True(gated.IsBridgeIgnorable);

        // Read by an element leaf: not ignorable.
        filtered.ElementLeafAliases.Add("Os");
        Assert.False(filtered.IsBridgeIgnorable);

        // Element stages without a filter or gate: not ignorable.
        var sorted = new LookupExpression(type, "C", "k", "_id", "e", false)
        {
            IsCorrelatedArraySource = true,
            ElementSort = new List<(string, int)> { ("Id", 1) }
        };
        Assert.False(sorted.IsBridgeIgnorable);
    }

    // ---- SameElementCount / SameOuterConditions ----

    [Fact]
    public void SameElementCount_truth_table()
    {
        Assert.True(LookupExpression.SameElementCount(null, null));
        Assert.True(LookupExpression.SameElementCount(Const(2), Const(2)));
        Assert.False(LookupExpression.SameElementCount(Const(2), Const(3)));
        Assert.True(LookupExpression.SameElementCount(Param("p"), Param("p")));
        Assert.False(LookupExpression.SameElementCount(Param("p"), Param("q")));
        Assert.False(LookupExpression.SameElementCount(Const(2), Param("p")));
        Assert.False(LookupExpression.SameElementCount(Param("p"), Const(2)));
        Assert.False(LookupExpression.SameElementCount(null, Const(2)));
        Assert.False(LookupExpression.SameElementCount(Const(2), null));
    }

    [Fact]
    public void SameOuterConditions_truth_table()
    {
        var a = new LookupOuterCondition("city", Const("London"), Guarded: false);

        Assert.True(LookupExpression.SameOuterConditions([], []));
        Assert.True(LookupExpression.SameOuterConditions([a], [new LookupOuterCondition("city", Const("London"), false)]));
        Assert.True(LookupExpression.SameOuterConditions(
            [new LookupOuterCondition("city", Param("p"), true)], [new LookupOuterCondition("city", Param("p"), true)]));

        Assert.False(LookupExpression.SameOuterConditions([a], []));
        Assert.False(LookupExpression.SameOuterConditions([a], [a, a]));
        Assert.False(LookupExpression.SameOuterConditions([a], [new LookupOuterCondition("town", Const("London"), false)]));
        Assert.False(LookupExpression.SameOuterConditions([a], [new LookupOuterCondition("city", Const("London"), true)]));
        Assert.False(LookupExpression.SameOuterConditions([a], [new LookupOuterCondition("city", Const("Paris"), false)]));
        Assert.False(LookupExpression.SameOuterConditions([a], [new LookupOuterCondition("city", Param("p"), false)]));
    }

    // ---- MatchesAtMostOneDocument ----

    [Fact]
    public void MatchesAtMostOneDocument_truth_table()
    {
        using var db = new GateDbContext();
        var type = EntityType<Order>(db);

        Assert.True(KeyedLookup(type, "_lookup_a", foreignField: "_id").MatchesAtMostOneDocument);
        Assert.False(KeyedLookup(type, "_lookup_b", foreignField: "Other").MatchesAtMostOneDocument);
        Assert.False(KeyedLookup(type, "_lookup_c", foreignField: "_id.Part").MatchesAtMostOneDocument);
        Assert.False(KeyedLookup(type, "_lookup_d.nested", foreignField: "_id").MatchesAtMostOneDocument);

        var composite = new LookupExpression(
            type, "C", [new LookupKeyPair("a", "_id", false), new LookupKeyPair("b", "_id", false)], "_lookup_e", forceUnwind: false);
        Assert.False(composite.MatchesAtMostOneDocument);
    }

    [Fact]
    public void MatchesAtMostOneDocument_is_true_for_a_navigation_matching_the_target_id()
    {
        using var db = new GateDbContext();

        // Order.Customer: the FK targets Customer's primary key, which is "_id".
        Assert.True(new LookupExpression(Nav<Order>(db, nameof(Order.Customer))).MatchesAtMostOneDocument);

        // Customer.Orders: matches on Order.CustomerId, which can repeat.
        Assert.False(new LookupExpression(Nav<Customer>(db, nameof(Customer.Orders))).MatchesAtMostOneDocument);
    }

    // ---- CompositeKeyLetAndMatch ----

    [Fact]
    public void CompositeKeyLetAndMatch_renders_let_and_match_with_null_normalization_and_guards()
    {
        var (let, match) = LookupExpression.CompositeKeyLetAndMatch(
        [
            new LookupKeyPair("a", "b", MayBeNull: false),
            new LookupKeyPair("c", "d", MayBeNull: true, ExcludesNullLocal: true)
        ]);

        Assert.Equal(
            new BsonDocument
            {
                { "k0", "$a" },
                { "k1", new BsonDocument("$ifNull", new BsonArray { "$c", BsonNull.Value }) }
            },
            let);

        Assert.Equal(
            new BsonDocument("$and", new BsonArray
            {
                new BsonDocument("$eq", new BsonArray { "$b", "$$k0" }),
                new BsonDocument("$ne", new BsonArray { "$$k1", BsonNull.Value }),
                new BsonDocument("$eq", new BsonArray
                {
                    new BsonDocument("$ifNull", new BsonArray { "$d", BsonNull.Value }), "$$k1"
                })
            }),
            match);
    }

    [Fact]
    public void CompositeKeyLetAndMatch_unguarded_nullable_pair_equates_null_with_null()
    {
        var (_, match) = LookupExpression.CompositeKeyLetAndMatch([new LookupKeyPair("a", "b", MayBeNull: true)]);

        var conditions = match["$and"].AsBsonArray;
        Assert.Single(conditions);
        Assert.Equal("$eq", conditions[0].AsBsonDocument.GetElement(0).Name);
    }

    // ---- LocalField setter ----

    [Fact]
    public void LocalField_setter_throws_for_a_composite_key_lookup()
    {
        using var db = new GateDbContext();
        var composite = new LookupExpression(
            EntityType<Order>(db), "C",
            [new LookupKeyPair("a", "x", false), new LookupKeyPair("b", "y", false)], "_lookup_c", forceUnwind: false);

        Assert.Throws<InvalidOperationException>(() => composite.LocalField = "other.a");
        Assert.Equal("a", composite.LocalField);
    }

    [Fact]
    public void LocalField_setter_retargets_a_single_key_lookup()
    {
        using var db = new GateDbContext();
        var lookup = KeyedLookup(EntityType<Order>(db), "_lookup_a");

        lookup.LocalField = "_lookup_p.k";

        Assert.Equal("_lookup_p.k", lookup.LocalField);
    }

    // ---- JoinInfo.PagingBeforeItMustStayAhead ----

    [Fact]
    public void PagingBeforeItMustStayAhead_truth_table()
    {
        using var db = new GateDbContext();
        var order = EntityType<Order>(db);

        // No lookup resolved: fail closed.
        Assert.True(new JoinInfo(order, isLeftOuter: true).PagingBeforeItMustStayAhead);

        // A navigation-less lookup can multiply rows.
        Assert.True(new JoinInfo(order, true) { Lookup = KeyedLookup(order, "_lookup_a") }.PagingBeforeItMustStayAhead);

        // A filtered inner drops rows however the FK is declared (even a left-outer required dependent reference).
        var requiredDependent = Nav<Order>(db, nameof(Order.Customer));
        Assert.True(new JoinInfo(order, true)
        {
            Lookup = new LookupExpression(requiredDependent) { InnerPredicate = Const(true) }
        }.PagingBeforeItMustStayAhead);

        // Collection navigation: multiplies rows, left-outer or not.
        var collection = new LookupExpression(Nav<Customer>(db, nameof(Customer.Orders)));
        Assert.True(new JoinInfo(order, true) { Lookup = collection }.PagingBeforeItMustStayAhead);
        Assert.True(new JoinInfo(order, false) { Lookup = collection }.PagingBeforeItMustStayAhead);

        // Left-outer reference: row-count preserving, either side.
        Assert.False(new JoinInfo(order, true) { Lookup = new LookupExpression(requiredDependent) }.PagingBeforeItMustStayAhead);
        Assert.False(new JoinInfo(order, true)
        {
            Lookup = new LookupExpression(Nav<Order>(db, nameof(Order.Coupon)))
        }.PagingBeforeItMustStayAhead);
        Assert.False(new JoinInfo(order, true)
        {
            Lookup = new LookupExpression(Nav<Customer>(db, nameof(Customer.Profile)))
        }.PagingBeforeItMustStayAhead);

        // Inner join: only a required dependent-side reference may defer.
        Assert.False(new JoinInfo(order, false) { Lookup = new LookupExpression(requiredDependent) }.PagingBeforeItMustStayAhead);
        Assert.True(new JoinInfo(order, false)
        {
            Lookup = new LookupExpression(Nav<Order>(db, nameof(Order.Coupon)))
        }.PagingBeforeItMustStayAhead);
        Assert.True(new JoinInfo(order, false)
        {
            Lookup = new LookupExpression(Nav<Customer>(db, nameof(Customer.Profile)))
        }.PagingBeforeItMustStayAhead);
    }

    // ---- OrderLookupsByDependency (via GetPendingLookups) ----

    [Fact]
    public void GetPendingLookups_orders_a_dependent_lookup_after_the_lookup_it_reads()
    {
        using var db = new GateDbContext();
        var type = EntityType<Order>(db);
        var query = new MongoQueryExpression(type);
        var dependent = KeyedLookup(type, "_lookup_Leaf", localField: "_lookup_Mid.k");
        var middle = KeyedLookup(type, "_lookup_Mid");

        query.AddLookup(dependent);
        query.AddLookup(middle);

        Assert.Equal(["_lookup_Mid", "_lookup_Leaf"], query.GetPendingLookups().Select(l => l.As));
    }

    [Fact]
    public void GetPendingLookups_orders_a_three_deep_chain_regardless_of_registration_order()
    {
        using var db = new GateDbContext();
        var type = EntityType<Order>(db);
        var query = new MongoQueryExpression(type);

        query.AddLookup(KeyedLookup(type, "_lookup_C", localField: "_lookup_B.k"));
        query.AddLookup(KeyedLookup(type, "_lookup_B", localField: "_lookup_A.k"));
        query.AddLookup(KeyedLookup(type, "_lookup_A"));

        Assert.Equal(["_lookup_A", "_lookup_B", "_lookup_C"], query.GetPendingLookups().Select(l => l.As));
    }

    [Fact]
    public void GetPendingLookups_keeps_registration_order_for_independent_lookups()
    {
        using var db = new GateDbContext();
        var type = EntityType<Order>(db);
        var query = new MongoQueryExpression(type);

        query.AddLookup(KeyedLookup(type, "_lookup_Z"));
        query.AddLookup(KeyedLookup(type, "_lookup_A"));
        query.AddLookup(KeyedLookup(type, "_lookup_M"));

        Assert.Equal(["_lookup_Z", "_lookup_A", "_lookup_M"], query.GetPendingLookups().Select(l => l.As));
    }

    [Fact]
    public void GetPendingLookups_keeps_registration_order_on_a_dependency_cycle_rather_than_hanging()
    {
        using var db = new GateDbContext();
        var type = EntityType<Order>(db);
        var query = new MongoQueryExpression(type);

        query.AddLookup(KeyedLookup(type, "_lookup_A", localField: "_lookup_B.k"));
        query.AddLookup(KeyedLookup(type, "_lookup_B", localField: "_lookup_A.k"));

        Assert.Equal(["_lookup_A", "_lookup_B"], query.GetPendingLookups().Select(l => l.As));
    }

    // ---- AddLookup (alias dedup) ----

    private static LookupExpression Pipelined(IEntityType type, string alias, LookupPipelineKind kind)
    {
        var lookup = KeyedLookup(type, alias);
        lookup.PipelineStages.Add(new BsonDocument("$limit", 2));
        lookup.PipelineKind = kind;
        return lookup;
    }

    [Fact]
    public void AddLookup_keeps_the_first_of_two_bare_lookups_with_one_alias()
    {
        using var db = new GateDbContext();
        var type = EntityType<Order>(db);
        var query = new MongoQueryExpression(type);
        var first = KeyedLookup(type, "_lookup_A");

        query.AddLookup(first);
        query.AddLookup(KeyedLookup(type, "_lookup_A"));

        Assert.Same(first, Assert.Single(query.GetPendingLookups()));
    }

    [Fact]
    public void AddLookup_folds_a_pipelined_lookup_into_an_existing_bare_one()
    {
        using var db = new GateDbContext();
        var type = EntityType<Order>(db);
        var query = new MongoQueryExpression(type);
        var bare = KeyedLookup(type, "_lookup_A");

        query.AddLookup(bare);
        query.AddLookup(Pipelined(type, "_lookup_A", LookupPipelineKind.FilteredInclude));

        var merged = Assert.Single(query.GetPendingLookups());
        Assert.Same(bare, merged);
        Assert.True(merged.HasPipeline);
        Assert.Equal(LookupPipelineKind.FilteredInclude, merged.PipelineKind);
    }

    [Fact]
    public void AddLookup_drops_a_bare_lookup_registered_over_a_pipelined_one()
    {
        using var db = new GateDbContext();
        var type = EntityType<Order>(db);
        var query = new MongoQueryExpression(type);
        var pipelined = Pipelined(type, "_lookup_A", LookupPipelineKind.NestedInclude);

        query.AddLookup(pipelined);
        query.AddLookup(KeyedLookup(type, "_lookup_A"));

        var kept = Assert.Single(query.GetPendingLookups());
        Assert.Same(pipelined, kept);
        Assert.Equal(LookupPipelineKind.NestedInclude, kept.PipelineKind);
    }

    [Fact]
    public void AddLookup_re_registering_the_same_instance_keeps_its_filter_state()
    {
        // A join's lookup carrying an InnerPredicate can be registered again by the Include path; that must neither duplicate nor disturb it.
        using var db = new GateDbContext();
        var type = EntityType<Order>(db);
        var query = new MongoQueryExpression(type);
        var lookup = KeyedLookup(type, "_lookup_A");
        lookup.InnerPredicate = Const(true);
        lookup.PipelineKind = LookupPipelineKind.FilteredJoinInner;

        query.AddLookup(lookup);
        query.AddLookup(lookup);

        var kept = Assert.Single(query.GetPendingLookups());
        Assert.Same(lookup, kept);
        Assert.NotNull(kept.InnerPredicate);
        Assert.Equal(LookupPipelineKind.FilteredJoinInner, kept.PipelineKind);
    }

    [Fact]
    public void AddLookup_dedupes_identical_correlated_array_lookups_by_alias_and_keeps_the_first_state()
    {
        // A correlated array's alias encodes its target, key paths, null exclusion and filter suffix, so one alias is one lookup:
        // dropping the second is correct, and no state (InnerPredicate, element filters, ...) needs carrying over.
        using var db = new GateDbContext();
        var type = EntityType<Order>(db);
        var query = new MongoQueryExpression(type);
        var predicate = Const(true);
        var first = KeyedLookup(type, "_lookup_Order_k_k_f1", correlatedArray: true);
        first.InnerPredicate = predicate;
        var second = KeyedLookup(type, "_lookup_Order_k_k_f1", correlatedArray: true);
        second.InnerPredicate = predicate;

        query.AddLookup(first);
        query.AddLookup(second);

        var kept = Assert.Single(query.GetPendingLookups());
        Assert.Same(first, kept);
        Assert.Same(predicate, kept.InnerPredicate);
    }

    [Fact]
    public void GetUnusedLookupAlias_gives_a_colliding_lookup_its_own_alias_so_AddLookup_keeps_both()
    {
        using var db = new GateDbContext();
        var type = EntityType<Order>(db);
        var query = new MongoQueryExpression(type);
        query.AddLookup(KeyedLookup(type, "_lookup_A"));

        var renamed = KeyedLookup(type, query.GetUnusedLookupAlias("_lookup_A"));
        query.AddLookup(renamed);

        Assert.Equal(["_lookup_A", "_lookup_A_1"], query.GetPendingLookups().Select(l => l.As));
    }
}
