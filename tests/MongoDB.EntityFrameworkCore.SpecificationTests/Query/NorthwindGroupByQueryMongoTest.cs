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

using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.TestUtilities;
using MongoDB.Driver.Linq;
using Xunit.Abstractions;

namespace MongoDB.EntityFrameworkCore.SpecificationTests.Query;

public class NorthwindGroupByQueryMongoTest : NorthwindGroupByQueryTestBase<
    NorthwindQueryMongoFixture<NoopModelCustomizer>>
{
    public NorthwindGroupByQueryMongoTest(
        NorthwindQueryMongoFixture<NoopModelCustomizer> fixture,
        ITestOutputHelper testOutputHelper)
        : base(fixture)
    {
        Fixture.TestMqlLoggerFactory.Clear();
        //Fixture.TestMqlLoggerFactory.SetTestOutputHelper(testOutputHelper);
    }

    [ConditionalFact]
    public virtual void Check_all_tests_overridden()
        => TestHelpers.AssertAllMethodsOverridden(GetType());

#if !EF8 && !EF9

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task Final_GroupBy_TagWith(bool _)
        => Task.CompletedTask;

#endif

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_Property_Select_Average(bool _)
        => Task.CompletedTask;

    public override async Task GroupBy_Property_Select_Average_with_group_enumerable_projected(bool async)
    {
        await base.GroupBy_Property_Select_Average_with_group_enumerable_projected(async);

        AssertMql();
    }

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_Property_Select_Count(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_Property_Select_LongCount(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_Property_Select_Count_with_nulls(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_Property_Select_LongCount_with_nulls(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_Property_Select_Max(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_Property_Select_Min(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_Property_Select_Sum(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_Property_Select_Sum_Min_Max_Avg(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_Property_Select_Key_Average(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_Property_Select_Key_Count(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_Property_Select_Key_LongCount(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_Property_Select_Key_Max(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_Property_Select_Key_Min(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_Property_Select_Key_Sum(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_Property_Select_Key_Sum_Min_Max_Avg(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_Property_Select_Sum_Min_Key_Max_Avg(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_Property_Select_key_multiple_times_and_aggregate(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_Property_Select_Key_with_constant(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_aggregate_projecting_conditional_expression(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_aggregate_projecting_conditional_expression_based_on_group_key(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_anonymous_Select_Average(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_anonymous_Select_Count(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_anonymous_Select_LongCount(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_anonymous_Select_Max(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_anonymous_Select_Min(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_anonymous_Select_Sum(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_anonymous_Select_Sum_Min_Max_Avg(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_anonymous_with_alias_Select_Key_Sum(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_Composite_Select_Average(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_Composite_Select_Count(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_Composite_Select_LongCount(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_Composite_Select_Max(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_Composite_Select_Min(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_Composite_Select_Sum(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_Composite_Select_Sum_Min_Max_Avg(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_Composite_Select_Key_Average(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_Composite_Select_Key_Count(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_Composite_Select_Key_LongCount(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_Composite_Select_Key_Max(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_Composite_Select_Key_Min(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_Composite_Select_Key_Sum(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_Composite_Select_Key_Sum_Min_Max_Avg(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_Composite_Select_Sum_Min_Key_Max_Avg(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_Composite_Select_Sum_Min_Key_flattened_Max_Avg(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_Dto_as_key_Select_Sum(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_Dto_as_element_selector_Select_Sum(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_Composite_Select_Dto_Sum_Min_Key_flattened_Max_Avg(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_Composite_Select_Sum_Min_part_Key_flattened_Max_Avg(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_Constant_Select_Sum_Min_Key_Max_Avg(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_Constant_with_element_selector_Select_Sum(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_Constant_with_element_selector_Select_Sum2(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_Constant_with_element_selector_Select_Sum3(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_after_predicate_Constant_Select_Sum_Min_Key_Max_Avg(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_Constant_with_element_selector_Select_Sum_Min_Key_Max_Avg(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_constant_with_where_on_grouping_with_aggregate_operators(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_param_Select_Sum_Min_Key_Max_Avg(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_param_with_element_selector_Select_Sum(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_param_with_element_selector_Select_Sum2(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_param_with_element_selector_Select_Sum3(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_param_with_element_selector_Select_Sum_Min_Key_Max_Avg(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_anonymous_key_type_mismatch_with_aggregate(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_Property_scalar_element_selector_Average(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_Property_scalar_element_selector_Count(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_Property_scalar_element_selector_LongCount(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_Property_scalar_element_selector_Max(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_Property_scalar_element_selector_Min(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_Property_scalar_element_selector_Sum(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_Property_scalar_element_selector_Sum_Min_Max_Avg(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_Property_anonymous_element_selector_Average(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_Property_anonymous_element_selector_Count(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_Property_anonymous_element_selector_LongCount(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_Property_anonymous_element_selector_Max(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_Property_anonymous_element_selector_Min(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_Property_anonymous_element_selector_Sum(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_Property_anonymous_element_selector_Sum_Min_Max_Avg(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_element_selector_complex_aggregate(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_element_selector_complex_aggregate2(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_element_selector_complex_aggregate3(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_element_selector_complex_aggregate4(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task Element_selector_with_case_block_repeated_inside_another_case_block_in_projection(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_conditional_properties(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_empty_key_Aggregate(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_empty_key_Aggregate_Key(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task OrderBy_GroupBy_Aggregate(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task OrderBy_Skip_GroupBy_Aggregate(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task OrderBy_Take_GroupBy_Aggregate(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task OrderBy_Skip_Take_GroupBy_Aggregate(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task Distinct_GroupBy_Aggregate(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task Anonymous_projection_Distinct_GroupBy_Aggregate(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task SelectMany_GroupBy_Aggregate(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task Join_GroupBy_Aggregate(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_required_navigation_member_Aggregate(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task Join_complex_GroupBy_Aggregate(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupJoin_GroupBy_Aggregate(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupJoin_GroupBy_Aggregate_2(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupJoin_GroupBy_Aggregate_3(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupJoin_GroupBy_Aggregate_4(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupJoin_GroupBy_Aggregate_5(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_optional_navigation_member_Aggregate(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupJoin_complex_GroupBy_Aggregate(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task Self_join_GroupBy_Aggregate(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_multi_navigation_members_Aggregate(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task Union_simple_groupby(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task Select_anonymous_GroupBy_Aggregate(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_principal_key_property_optimization(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_after_anonymous_projection_and_distinct_followed_by_another_anonymous_projection(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_complex_key_aggregate(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_complex_key_aggregate_2(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task Select_collection_of_scalar_before_GroupBy_aggregate(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_OrderBy_key(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_OrderBy_count(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_OrderBy_count_Select_sum(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_aggregate_Contains(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_aggregate_Pushdown(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_aggregate_using_grouping_key_Pushdown(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_aggregate_Pushdown_followed_by_projecting_Length(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_aggregate_Pushdown_followed_by_projecting_constant(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_filter_key(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_filter_count(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_count_filter(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_filter_count_OrderBy_count_Select_sum(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_Aggregate_Join(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_Aggregate_Join_converted_from_SelectMany(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_Aggregate_LeftJoin_converted_from_SelectMany(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task Join_GroupBy_Aggregate_multijoins(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task Join_GroupBy_Aggregate_single_join(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task Join_GroupBy_Aggregate_with_another_join(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task Join_GroupBy_Aggregate_distinct_single_join(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task Join_GroupBy_Aggregate_with_left_join(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task Join_GroupBy_Aggregate_in_subquery(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task Join_GroupBy_Aggregate_on_key(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_with_result_selector(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_Sum_constant(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_Sum_constant_cast(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task Distinct_GroupBy_OrderBy_key(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task Select_nested_collection_with_groupby(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task Select_uncorrelated_collection_with_groupby_works(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task Select_uncorrelated_collection_with_groupby_multiple_collections_work(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task Select_GroupBy_All(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_Where_Average(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_Where_Count(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_Where_LongCount(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_Where_Max(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_Where_Min(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_Where_Sum(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_Where_Count_with_predicate(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_Where_Where_Count(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_Where_Select_Where_Count(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_Where_Select_Where_Select_Min(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_multiple_Count_with_predicate(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_multiple_Sum_with_conditional_projection(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_multiple_Sum_with_Select_conditional_projection(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_Key_as_part_of_element_selector(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_composite_Key_as_part_of_element_selector(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_with_aggregate_through_navigation_property(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_with_aggregate_containing_complex_where(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_Shadow(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_Shadow2(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_Shadow3(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_select_grouping_list(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_select_grouping_array(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_select_grouping_composed_list(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_select_grouping_composed_list_2(bool _)
        => Task.CompletedTask;

    public override async Task Select_GroupBy_SelectMany(bool async)
    {
        await base.Select_GroupBy_SelectMany(async);

        AssertMql();
    }

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task Count_after_GroupBy_aggregate(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task LongCount_after_GroupBy_aggregate(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_Select_Distinct_aggregate(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_group_Distinct_Select_Distinct_aggregate(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_group_Where_Select_Distinct_aggregate(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task MinMax_after_GroupBy_aggregate(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task All_after_GroupBy_aggregate(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task All_after_GroupBy_aggregate2(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task Any_after_GroupBy_aggregate(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task Count_after_GroupBy_without_aggregate(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task Count_with_predicate_after_GroupBy_without_aggregate(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task LongCount_after_GroupBy_without_aggregate(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task LongCount_with_predicate_after_GroupBy_without_aggregate(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task Any_after_GroupBy_without_aggregate(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task Any_with_predicate_after_GroupBy_without_aggregate(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task All_with_predicate_after_GroupBy_without_aggregate(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_aggregate_followed_by_another_GroupBy_aggregate(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_Count_in_projection(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_nominal_type_count(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_based_on_renamed_property_simple(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_based_on_renamed_property_complex(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task Join_groupby_anonymous_orderby_anonymous_projection(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task Odata_groupby_empty_key(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_with_group_key_access_thru_navigation(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_with_group_key_access_thru_nested_navigation(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_with_group_key_being_navigation(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_with_group_key_being_nested_navigation(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_with_group_key_being_navigation_with_entity_key_projection(bool _)
        => Task.CompletedTask;

    public override async Task GroupBy_with_group_key_being_navigation_with_complex_projection(bool async)
    {
        await base.GroupBy_with_group_key_being_navigation_with_complex_projection(async);

        AssertMql();
    }

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_with_order_by_skip_and_another_order_by(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_Property_Select_Count_with_predicate(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_Property_Select_LongCount_with_predicate(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_orderby_projection_with_coalesce_operation(bool _)
        => Task.CompletedTask;

    public override async Task GroupBy_let_orderby_projection_with_coalesce_operation(bool async)
    {
        await base.GroupBy_let_orderby_projection_with_coalesce_operation(async);

        AssertMql();
    }

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_Min_Where_optional_relationship(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_Min_Where_optional_relationship_2(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_aggregate_over_a_subquery(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_aggregate_join_with_grouping_key(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_aggregate_join_with_group_result(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_aggregate_from_right_side_of_join(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_aggregate_join_another_GroupBy_aggregate(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_aggregate_after_skip_0_take_0(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_skip_0_take_0_aggregate(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_aggregate_followed_another_GroupBy_aggregate(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_aggregate_without_selectMany_selecting_first(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_aggregate_left_join_GroupBy_aggregate_left_join(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_selecting_grouping_key_list(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_with_grouping_key_using_Like(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_with_grouping_key_DateTime_Day(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_with_cast_inside_grouping_aggregate(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task Complex_query_with_groupBy_in_subquery1(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task Complex_query_with_groupBy_in_subquery2(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task Complex_query_with_groupBy_in_subquery3(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task Group_by_with_projection_into_DTO(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task Where_select_function_groupby_followed_by_another_select_with_aggregates(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task Group_by_column_project_constant(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task Key_plus_key_in_projection(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task Group_by_with_arithmetic_operation_inside_aggregate(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_scalar_subquery(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task AsEnumerable_in_subquery_for_GroupBy(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_aggregate_from_multiple_query_in_same_projection(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_aggregate_from_multiple_query_in_same_projection_2(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_aggregate_from_multiple_query_in_same_projection_3(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_scalar_aggregate_in_set_operation(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task Select_uncorrelated_collection_with_groupby_when_outer_is_distinct(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task Select_correlated_collection_after_GroupBy_aggregate_when_identifier_does_not_change(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task Select_correlated_collection_after_GroupBy_aggregate_when_identifier_changes(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task Select_correlated_collection_after_GroupBy_aggregate_when_identifier_changes_to_complex(bool _)
        => Task.CompletedTask;

    //AssertMql(" ");
    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task Complex_query_with_group_by_in_subquery5(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task Complex_query_with_groupBy_in_subquery4(bool _)
        => Task.CompletedTask;

    public override async Task GroupBy_aggregate_SelectMany(bool async)
    {
        await base.GroupBy_aggregate_SelectMany(async);

        AssertMql();
    }

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task Final_GroupBy_property_entity(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task Final_GroupBy_entity(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task Final_GroupBy_property_entity_non_nullable(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task Final_GroupBy_property_anonymous_type(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task Final_GroupBy_multiple_properties_entity(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task Final_GroupBy_complex_key_entity(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task Final_GroupBy_nominal_type_entity(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task Final_GroupBy_property_anonymous_type_element_selector(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task Final_GroupBy_property_entity_Include_collection(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task Final_GroupBy_property_entity_projecting_collection(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task Final_GroupBy_property_entity_projecting_collection_composed(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task Final_GroupBy_property_entity_projecting_collection_and_single_result(bool _)
        => Task.CompletedTask;

    public override async Task GroupBy_Where_with_grouping_result(bool async)
    {
        await base.GroupBy_Where_with_grouping_result(async);

        AssertMql();
    }

    public override async Task GroupBy_OrderBy_with_grouping_result(bool async)
    {
        await base.GroupBy_OrderBy_with_grouping_result(async);

        AssertMql();
    }

    public override async Task GroupBy_SelectMany(bool async)
    {
        await base.GroupBy_SelectMany(async);

        AssertMql();
    }

    public override async Task OrderBy_GroupBy_SelectMany(bool async)
    {
        await base.OrderBy_GroupBy_SelectMany(async);

        AssertMql();
    }

    public override async Task OrderBy_GroupBy_SelectMany_shadow(bool async)
    {
        await base.OrderBy_GroupBy_SelectMany_shadow(async);

        AssertMql();
    }

    public override async Task GroupBy_with_orderby_take_skip_distinct_followed_by_group_key_projection(bool async)
    {
        await base.GroupBy_with_orderby_take_skip_distinct_followed_by_group_key_projection(async);

        AssertMql();
    }

    public override async Task GroupBy_Distinct(bool async)
    {
        await base.GroupBy_Distinct(async);

        AssertMql();
    }

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_complex_key_without_aggregate(bool _)
        => Task.CompletedTask;

    private void AssertMql(params string[] expected)
        => Fixture.TestMqlLoggerFactory.AssertBaseline(expected);

    protected override void ClearLog()
        => Fixture.TestMqlLoggerFactory.Clear();
}
