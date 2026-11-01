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
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query.NativeTranslation;

/// <summary>
/// <see cref="MongoPipelineFactory.SingleOutputField"/>: the cursor reads a one-slot projection without matching element names only when the
/// pipeline guarantees every document is that one field or empty.
/// </summary>
public class MongoPipelineFactorySingleOutputFieldTests
{
    private static string? Field(params string[] stages)
        => new MongoPipelineFactory(
            stages.Select(s => MongoPipelineFactory.StageSlot.Rendered(BsonDocument.Parse(s))).ToList(),
            new PlaceholderTable()).SingleOutputField;

    private const string Project = "{ $project: { Count: '$Count', _id: 0 } }";

    [Fact]
    public void A_project_of_one_field_with_id_suppressed_names_the_field()
        => Assert.Equal("Count", Field("{ $match: { Rank: 1 } }", Project));

    [Fact]
    public void Stages_that_only_filter_sort_or_page_may_follow_the_project()
        => Assert.Equal(
            "Count",
            Field(Project, "{ $match: { Count: 1 } }", "{ $sort: { Count: 1 } }", "{ $skip: 5 }", "{ $limit: 10 }"));

    [Fact]
    public void Two_fields_are_not_a_single_output_field()
        => Assert.Null(Field("{ $project: { Count: '$Count', Name: '$Name', _id: 0 } }"));

    [Fact]
    public void An_emitted_id_is_a_second_field()
        => Assert.Null(Field("{ $project: { Count: '$Count', _id: 1 } }"));

    [Fact]
    public void A_dotted_or_operator_field_name_is_not_a_plain_field()
    {
        Assert.Null(Field("{ $project: { 'a.b': '$X', _id: 0 } }"));
        Assert.Null(Field("{ $project: { '$weird': '$X', _id: 0 } }"));
    }

    [Fact]
    public void A_stage_that_can_add_or_replace_fields_after_the_project_declines()
    {
        Assert.Null(Field(Project, "{ $set: { Extra: 1 } }"));
        Assert.Null(Field(Project, "{ $lookup: { from: 'x', as: 'y', localField: 'a', foreignField: 'b' } }"));
        Assert.Null(Field(Project, "{ $unionWith: 'other' }"));
        Assert.Null(Field(Project, "{ $group: { _id: '$Count' } }"));
    }

    [Fact]
    public void A_pipeline_without_a_project_declines()
        => Assert.Null(Field("{ $match: { Rank: 1 } }"));

    [Fact]
    public void A_deferred_slot_after_the_project_declines()
        => Assert.Null(
            new MongoPipelineFactory(
                [
                    MongoPipelineFactory.StageSlot.Rendered(BsonDocument.Parse(Project)),
                    MongoPipelineFactory.StageSlot.Deferred(_ => new BsonDocument())
                ],
                new PlaceholderTable()).SingleOutputField);
}
