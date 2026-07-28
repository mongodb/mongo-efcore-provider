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

using MongoDB.EntityFrameworkCore.Query.Expressions;
using Xunit;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query.NativeTranslation;

public class MongoSelectDefinitionTrailingOpsTests
{
    private static MongoSelectDefinition WithSetOp()
    {
        var select = new MongoSelectDefinition();
        // Record a pre-set-op op first (source1's own), then attach the set op.
        select.AddPredicateConjunct(new MongoConstantExpression(true, forSerialization: null));
        select.SetOperation = new MongoSetOperation(
            MongoSetOperationKind.Union, new MongoSelectDefinition(), "customers");
        select.IsSetOp = true;
        return select;
    }

    [Fact]
    public void Ops_before_set_op_go_to_PipelineOps()
    {
        var select = new MongoSelectDefinition();
        select.AddPredicateConjunct(new MongoConstantExpression(true, forSerialization: null));
        Assert.Single(select.PipelineOps);
        Assert.Empty(select.TrailingOps);
    }

    [Fact]
    public void Ops_after_set_op_go_to_TrailingOps()
    {
        var select = WithSetOp();
        // The pre-set-op predicate stayed in PipelineOps; a new predicate now lands in TrailingOps.
        select.AddPredicateConjunct(new MongoConstantExpression(false, forSerialization: null));
        Assert.Single(select.PipelineOps);
        Assert.Single(select.TrailingOps);
        Assert.IsType<MongoMatchOp>(select.TrailingOps[0]);
    }

    [Fact]
    public void Trailing_sort_skip_limit_route_to_TrailingOps()
    {
        var select = WithSetOp();
        select.StartOrReplaceSort(new MongoOrdering(new MongoConstantExpression(0, forSerialization: null), true));
        select.AppendSkip(new MongoConstantExpression(1, forSerialization: null));
        select.AppendLimit(new MongoConstantExpression(2, forSerialization: null));
        Assert.Collection(select.TrailingOps,
            op => Assert.IsType<MongoSortOp>(op),
            op => Assert.IsType<MongoSkipOp>(op),
            op => Assert.IsType<MongoLimitOp>(op));
    }

    [Fact]
    public void IsSetOpTerminalOnly_true_for_a_plain_set_op()
        => Assert.True(WithSetOp().IsSetOpTerminalOnly);

    [Fact]
    public void IsSetOpTerminalOnly_false_when_no_set_op()
        => Assert.False(new MongoSelectDefinition().IsSetOpTerminalOnly);

    [Fact]
    public void IsSetOpTerminalOnly_false_when_also_grouped()
    {
        var select = WithSetOp();
        select.IsGroupBy = true; // defensive: a mixed terminal must not count as set-op-only
        Assert.False(select.IsSetOpTerminalOnly);
    }

    [Fact]
    public void IsSetOpTerminalOnly_false_when_a_projection_is_populated()
    {
        var select = WithSetOp();
        // A trailing projection was pushed down: the set op is no longer the ONLY thing done, so a
        // subsequent operator must NOT be treated as set-op-terminal-only (it would resolve against the
        // entity type and mis-place / mis-bind — the composition-after-projection seam this closes).
        select.AddProjection(new MongoProjection("N", new MongoConstantExpression(0, forSerialization: null)));
        Assert.False(select.IsSetOpTerminalOnly);
    }
}
