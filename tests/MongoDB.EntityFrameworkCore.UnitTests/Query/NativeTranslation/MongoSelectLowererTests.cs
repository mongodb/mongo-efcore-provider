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

using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Query.Expressions;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation.Stages;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query.NativeTranslation;

/// <summary>
/// Tests for <see cref="MongoSelectLowerer"/>, which turns the native-translation slots
/// on <see cref="MongoQueryExpression"/> into a typed <see cref="MongoPipelineStage"/> list
/// in canonical pipeline order ($match → $sort → $skip → $limit → $lookup/$unwind).
/// </summary>
public class MongoSelectLowererTests
{
    private class StubEntity
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
    }

    private static MongoQueryExpression TestSelect()
    {
        using var db = SingleEntityDbContext.Create<StubEntity>();
        var entityType = db.Model.GetEntityTypes().First();
        return new MongoQueryExpression(entityType);
    }

    // ── Test 1: Empty slots → no stages ─────────────────────────────────────────

    [Fact]
    public void Empty_slots_lower_to_no_stages()
    {
        var select = TestSelect();
        var stages = new MongoSelectLowerer().Lower(select);
        Assert.Empty(stages);
    }

    // ── Test 2: All slots populated → canonical order ────────────────────────────

    [Fact]
    public void Predicate_ordering_offset_limit_lower_in_canonical_order()
    {
        var select = TestSelect();
        select.AddPredicateConjunct(new MongoConstantExpression(true, null));
        select.AppendOrdering(new MongoOrdering(new MongoConstantExpression(0, null), true));
        select.Offset = new MongoConstantExpression(5, null);
        select.Limit = new MongoConstantExpression(10, null);

        var stages = new MongoSelectLowerer().Lower(select);

        Assert.Equal(4, stages.Count);
        Assert.IsType<MongoMatchStage>(stages[0]);
        Assert.IsType<MongoSortStage>(stages[1]);
        Assert.IsType<MongoSkipStage>(stages[2]);
        Assert.IsType<MongoLimitStage>(stages[3]);
    }

    // ── Test 3: Only a predicate → exactly one MongoMatchStage ──────────────────

    [Fact]
    public void Only_predicate_lowers_to_single_match_stage()
    {
        var select = TestSelect();
        select.AddPredicateConjunct(new MongoConstantExpression(true, null));

        var stages = new MongoSelectLowerer().Lower(select);

        Assert.Single(stages);
        Assert.IsType<MongoMatchStage>(stages[0]);
    }

    // ── Test 4: Match stage carries the predicate expression ────────────────────

    [Fact]
    public void Match_stage_carries_the_predicate_expression()
    {
        var select = TestSelect();
        var predicate = new MongoConstantExpression(42, null);
        select.AddPredicateConjunct(predicate);

        var stages = new MongoSelectLowerer().Lower(select);

        var matchStage = Assert.IsType<MongoMatchStage>(stages[0]);
        Assert.Same(predicate, matchStage.Predicate);
    }

    // ── Test 5: Only orderings → exactly one MongoSortStage ─────────────────────

    [Fact]
    public void Only_orderings_lower_to_single_sort_stage()
    {
        var select = TestSelect();
        select.AppendOrdering(new MongoOrdering(new MongoConstantExpression(0, null), true));
        select.AppendOrdering(new MongoOrdering(new MongoConstantExpression(1, null), false));

        var stages = new MongoSelectLowerer().Lower(select);

        Assert.Single(stages);
        var sortStage = Assert.IsType<MongoSortStage>(stages[0]);
        Assert.Equal(2, sortStage.Orderings.Count);
    }

    // ── Test 6: Only Offset → exactly one MongoSkipStage ────────────────────────

    [Fact]
    public void Only_offset_lowers_to_single_skip_stage()
    {
        var select = TestSelect();
        var offset = new MongoConstantExpression(10, null);
        select.Offset = offset;

        var stages = new MongoSelectLowerer().Lower(select);

        Assert.Single(stages);
        var skipStage = Assert.IsType<MongoSkipStage>(stages[0]);
        Assert.Same(offset, skipStage.Offset);
    }

    // ── Test 7: Only Limit → exactly one MongoLimitStage ────────────────────────

    [Fact]
    public void Only_limit_lowers_to_single_limit_stage()
    {
        var select = TestSelect();
        var limit = new MongoConstantExpression(5, null);
        select.Limit = limit;

        var stages = new MongoSelectLowerer().Lower(select);

        Assert.Single(stages);
        var limitStage = Assert.IsType<MongoLimitStage>(stages[0]);
        Assert.Same(limit, limitStage.Limit);
    }

    // ── Test 8: Sort stage carries orderings from the slot ───────────────────────

    [Fact]
    public void Sort_stage_carries_orderings_from_the_slot()
    {
        var select = TestSelect();
        var keyExpr = new MongoConstantExpression(0, null);
        select.AppendOrdering(new MongoOrdering(keyExpr, Ascending: true));

        var stages = new MongoSelectLowerer().Lower(select);

        var sortStage = Assert.IsType<MongoSortStage>(stages[0]);
        var ordering = Assert.Single(sortStage.Orderings);
        Assert.Same(keyExpr, ordering.KeySelector);
        Assert.True(ordering.Ascending);
    }
}
