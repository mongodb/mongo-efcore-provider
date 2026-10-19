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

using System.Collections.Generic;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Query.Expressions;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation.Stages;
using Xunit;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query.NativeTranslation;

public class AggregateStageRenderingTests
{
    private static BsonDocument[] Render(params MongoPipelineStage[] stages)
        => MongoPipelineFactory.Create(stages, new MongoQueryLanguageRenderer())
            .Build(new Dictionary<string, object?>());

    [Fact]
    public void Count_renders_count_stage()
    {
        var result = Render(new MongoCountStage("v"));
        Assert.Equal(BsonDocument.Parse("{ $count: 'v' }"), result[0]);
    }

    [Fact]
    public void Sum_renders_group_stage()
    {
        var result = Render(new MongoGroupAccumulatorStage("$sum",
            new MongoFieldExpression(property: null!, elementName: "price"), "v"));
        Assert.Equal(BsonDocument.Parse("{ $group: { _id: null, v: { $sum: '$price' } } }"), result[0]);
    }

    [Fact]
    public void Keyed_group_scalar_key_renders()
    {
        var grouping = new MongoGrouping(
            new[] { new MongoGroupingKeyPart(null, new MongoFieldExpression(property: null!, elementName: "country")) },
            new[]
            {
                new MongoGroupAccumulator("Count", "$sum", null),
                new MongoGroupAccumulator("Total", "$sum", new MongoFieldExpression(property: null!, elementName: "amount")),
            });

        var result = Render(new MongoGroupStage(grouping));

        Assert.Equal(
            BsonDocument.Parse("{ $group: { _id: '$country', Count: { $sum: 1 }, Total: { $sum: '$amount' } } }"),
            result[0]);
    }

    [Fact]
    public void Keyed_group_composite_key_renders()
    {
        var grouping = new MongoGrouping(
            new[]
            {
                new MongoGroupingKeyPart("Country", new MongoFieldExpression(property: null!, elementName: "country")),
                new MongoGroupingKeyPart("Year", new MongoFieldExpression(property: null!, elementName: "year")),
            },
            new[] { new MongoGroupAccumulator("Count", "$sum", null) });

        var result = Render(new MongoGroupStage(grouping));

        Assert.Equal(
            BsonDocument.Parse("{ $group: { _id: { Country: '$country', Year: '$year' }, Count: { $sum: 1 } } }"),
            result[0]);
    }
}
