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
using MongoDB.EntityFrameworkCore.Query.NativeTranslation.Stages;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query.NativeTranslation;

public class MongoPipelineStageTests
{
    [Fact]
    public void Match_stage_carries_its_predicate()
    {
        var predicate = new MongoConstantExpression(true, null);
        var stage = new MongoMatchStage(predicate);
        Assert.Same(predicate, stage.Predicate);
    }

    [Fact]
    public void Sort_stage_carries_its_orderings()
    {
        var keySelector = new MongoConstantExpression(0, null);
        var orderings = new[] { new MongoOrdering(keySelector, Ascending: true) };
        var stage = new MongoSortStage(orderings);
        Assert.Same(orderings, stage.Orderings);
    }

    [Fact]
    public void Skip_stage_carries_its_offset()
    {
        var offset = new MongoConstantExpression(10, null);
        var stage = new MongoSkipStage(offset);
        Assert.Same(offset, stage.Offset);
    }

    [Fact]
    public void Limit_stage_carries_its_limit()
    {
        var limit = new MongoConstantExpression(5, null);
        var stage = new MongoLimitStage(limit);
        Assert.Same(limit, stage.Limit);
    }
}
