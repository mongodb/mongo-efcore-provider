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
using Xunit.Sdk;

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

    public override async Task Final_GroupBy_TagWith(bool async)
    {
        await AssertTranslationFailed(() => base.Final_GroupBy_TagWith(async));
    }

#endif

    public override async Task GroupBy_Property_Select_Average(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_Property_Select_Average(async));

        AssertMql(
        );

        // Validating that we don't generate warning when translating GroupBy. See Issue#11157
        Assert.DoesNotContain(
            "The LINQ expression 'GroupBy([o].CustomerID, [o])' could not be translated and will be evaluated locally.",
            Fixture.TestMqlLoggerFactory.Log.Select(l => l.Message));
    }

    public override async Task GroupBy_Property_Select_Average_with_group_enumerable_projected(bool async)
    {
        await base.GroupBy_Property_Select_Average_with_group_enumerable_projected(async);

        AssertMql();
    }

    public override async Task GroupBy_Property_Select_Count(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_Property_Select_Count(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_Property_Select_LongCount(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_Property_Select_LongCount(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_Property_Select_Count_with_nulls(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_Property_Select_Count_with_nulls(async));
        AssertMql(
        );
    }

    public override async Task GroupBy_Property_Select_LongCount_with_nulls(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_Property_Select_LongCount_with_nulls(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_Property_Select_Max(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_Property_Select_Max(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_Property_Select_Min(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_Property_Select_Min(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_Property_Select_Sum(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_Property_Select_Sum(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_Property_Select_Sum_Min_Max_Avg(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_Property_Select_Sum_Min_Max_Avg(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_Property_Select_Key_Average(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_Property_Select_Key_Average(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_Property_Select_Key_Count(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_Property_Select_Key_Count(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_Property_Select_Key_LongCount(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_Property_Select_Key_LongCount(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_Property_Select_Key_Max(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_Property_Select_Key_Max(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_Property_Select_Key_Min(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_Property_Select_Key_Min(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_Property_Select_Key_Sum(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_Property_Select_Key_Sum(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_Property_Select_Key_Sum_Min_Max_Avg(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_Property_Select_Key_Sum_Min_Max_Avg(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_Property_Select_Sum_Min_Key_Max_Avg(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_Property_Select_Sum_Min_Key_Max_Avg(async));
        AssertMql(
        );
    }

    public override async Task GroupBy_Property_Select_key_multiple_times_and_aggregate(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_Property_Select_key_multiple_times_and_aggregate(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_Property_Select_Key_with_constant(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_Property_Select_Key_with_constant(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_aggregate_projecting_conditional_expression(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_aggregate_projecting_conditional_expression(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_aggregate_projecting_conditional_expression_based_on_group_key(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_aggregate_projecting_conditional_expression_based_on_group_key(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_anonymous_Select_Average(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_anonymous_Select_Average(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_anonymous_Select_Count(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_anonymous_Select_Count(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_anonymous_Select_LongCount(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_anonymous_Select_LongCount(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_anonymous_Select_Max(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_anonymous_Select_Max(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_anonymous_Select_Min(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_anonymous_Select_Min(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_anonymous_Select_Sum(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_anonymous_Select_Sum(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_anonymous_Select_Sum_Min_Max_Avg(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_anonymous_Select_Sum_Min_Max_Avg(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_anonymous_with_alias_Select_Key_Sum(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_anonymous_with_alias_Select_Key_Sum(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_Composite_Select_Average(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_Composite_Select_Average(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_Composite_Select_Count(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_Composite_Select_Count(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_Composite_Select_LongCount(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_Composite_Select_LongCount(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_Composite_Select_Max(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_Composite_Select_Max(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_Composite_Select_Min(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_Composite_Select_Min(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_Composite_Select_Sum(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_Composite_Select_Sum(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_Composite_Select_Sum_Min_Max_Avg(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_Composite_Select_Sum_Min_Max_Avg(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_Composite_Select_Key_Average(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_Composite_Select_Key_Average(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_Composite_Select_Key_Count(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_Composite_Select_Key_Count(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_Composite_Select_Key_LongCount(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_Composite_Select_Key_LongCount(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_Composite_Select_Key_Max(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_Composite_Select_Key_Max(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_Composite_Select_Key_Min(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_Composite_Select_Key_Min(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_Composite_Select_Key_Sum(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_Composite_Select_Key_Sum(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_Composite_Select_Key_Sum_Min_Max_Avg(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_Composite_Select_Key_Sum_Min_Max_Avg(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_Composite_Select_Sum_Min_Key_Max_Avg(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_Composite_Select_Sum_Min_Key_Max_Avg(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_Composite_Select_Sum_Min_Key_flattened_Max_Avg(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_Composite_Select_Sum_Min_Key_flattened_Max_Avg(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_Dto_as_key_Select_Sum(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_Dto_as_key_Select_Sum(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_Dto_as_element_selector_Select_Sum(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_Dto_as_element_selector_Select_Sum(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_Composite_Select_Dto_Sum_Min_Key_flattened_Max_Avg(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_Composite_Select_Dto_Sum_Min_Key_flattened_Max_Avg(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_Composite_Select_Sum_Min_part_Key_flattened_Max_Avg(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_Composite_Select_Sum_Min_part_Key_flattened_Max_Avg(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_Constant_Select_Sum_Min_Key_Max_Avg(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_Constant_Select_Sum_Min_Key_Max_Avg(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_Constant_with_element_selector_Select_Sum(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_Constant_with_element_selector_Select_Sum(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_Constant_with_element_selector_Select_Sum2(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_Constant_with_element_selector_Select_Sum2(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_Constant_with_element_selector_Select_Sum3(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_Constant_with_element_selector_Select_Sum3(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_after_predicate_Constant_Select_Sum_Min_Key_Max_Avg(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_after_predicate_Constant_Select_Sum_Min_Key_Max_Avg(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_Constant_with_element_selector_Select_Sum_Min_Key_Max_Avg(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_Constant_with_element_selector_Select_Sum_Min_Key_Max_Avg(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_constant_with_where_on_grouping_with_aggregate_operators(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_constant_with_where_on_grouping_with_aggregate_operators(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_param_Select_Sum_Min_Key_Max_Avg(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_param_Select_Sum_Min_Key_Max_Avg(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_param_with_element_selector_Select_Sum(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_param_with_element_selector_Select_Sum(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_param_with_element_selector_Select_Sum2(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_param_with_element_selector_Select_Sum2(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_param_with_element_selector_Select_Sum3(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_param_with_element_selector_Select_Sum3(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_param_with_element_selector_Select_Sum_Min_Key_Max_Avg(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_param_with_element_selector_Select_Sum_Min_Key_Max_Avg(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_anonymous_key_type_mismatch_with_aggregate(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_anonymous_key_type_mismatch_with_aggregate(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_Property_scalar_element_selector_Average(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_Property_scalar_element_selector_Average(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_Property_scalar_element_selector_Count(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_Property_scalar_element_selector_Count(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_Property_scalar_element_selector_LongCount(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_Property_scalar_element_selector_LongCount(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_Property_scalar_element_selector_Max(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_Property_scalar_element_selector_Max(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_Property_scalar_element_selector_Min(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_Property_scalar_element_selector_Min(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_Property_scalar_element_selector_Sum(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_Property_scalar_element_selector_Sum(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_Property_scalar_element_selector_Sum_Min_Max_Avg(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_Property_scalar_element_selector_Sum_Min_Max_Avg(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_Property_anonymous_element_selector_Average(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_Property_anonymous_element_selector_Average(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_Property_anonymous_element_selector_Count(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_Property_anonymous_element_selector_Count(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_Property_anonymous_element_selector_LongCount(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_Property_anonymous_element_selector_LongCount(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_Property_anonymous_element_selector_Max(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_Property_anonymous_element_selector_Max(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_Property_anonymous_element_selector_Min(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_Property_anonymous_element_selector_Min(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_Property_anonymous_element_selector_Sum(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_Property_anonymous_element_selector_Sum(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_Property_anonymous_element_selector_Sum_Min_Max_Avg(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_Property_anonymous_element_selector_Sum_Min_Max_Avg(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_element_selector_complex_aggregate(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_element_selector_complex_aggregate(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_element_selector_complex_aggregate2(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_element_selector_complex_aggregate2(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_element_selector_complex_aggregate3(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_element_selector_complex_aggregate3(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_element_selector_complex_aggregate4(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_element_selector_complex_aggregate4(async));

        AssertMql(
        );
    }

    public override async Task Element_selector_with_case_block_repeated_inside_another_case_block_in_projection(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() =>
            base.Element_selector_with_case_block_repeated_inside_another_case_block_in_projection(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_conditional_properties(bool async)
    {
        // Fails: GroupBy issue EF-149
        Assert.Contains(
            "Expression of type 'MongoDB.EntityFrameworkCore.Query.QueryingEnumerable",
            (await Assert.ThrowsAsync<ArgumentException>(() =>
                base.GroupBy_conditional_properties(async))).Message);

        AssertMql(
        );
    }

    public override async Task GroupBy_empty_key_Aggregate(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_empty_key_Aggregate(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_empty_key_Aggregate_Key(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_empty_key_Aggregate_Key(async));

        AssertMql(
        );
    }

    public override async Task OrderBy_GroupBy_Aggregate(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.OrderBy_GroupBy_Aggregate(async));

        AssertMql(
        );
    }

    public override async Task OrderBy_Skip_GroupBy_Aggregate(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.OrderBy_Skip_GroupBy_Aggregate(async));

        AssertMql(
        );
    }

    public override async Task OrderBy_Take_GroupBy_Aggregate(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.OrderBy_Take_GroupBy_Aggregate(async));

        AssertMql(
        );
    }

    public override async Task OrderBy_Skip_Take_GroupBy_Aggregate(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.OrderBy_Skip_Take_GroupBy_Aggregate(async));

        AssertMql(
        );
    }

    public override async Task Distinct_GroupBy_Aggregate(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.Distinct_GroupBy_Aggregate(async));

        AssertMql(
        );
    }

    public override async Task Anonymous_projection_Distinct_GroupBy_Aggregate(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.Anonymous_projection_Distinct_GroupBy_Aggregate(async));

        AssertMql(
        );
    }

    public override async Task SelectMany_GroupBy_Aggregate(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.SelectMany_GroupBy_Aggregate(async));

        AssertMql(
        );
    }

    public override async Task Join_GroupBy_Aggregate(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.Join_GroupBy_Aggregate(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_required_navigation_member_Aggregate(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_required_navigation_member_Aggregate(async));

        AssertMql(
        );
    }

    public override async Task Join_complex_GroupBy_Aggregate(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.Join_complex_GroupBy_Aggregate(async));

        AssertMql(
        );
    }

    public override async Task GroupJoin_GroupBy_Aggregate(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupJoin_GroupBy_Aggregate(async));

        AssertMql(
        );
    }

    public override async Task GroupJoin_GroupBy_Aggregate_2(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupJoin_GroupBy_Aggregate_2(async));

        AssertMql(
        );
    }

    public override async Task GroupJoin_GroupBy_Aggregate_3(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupJoin_GroupBy_Aggregate_3(async));

        AssertMql(
        );
    }

    public override async Task GroupJoin_GroupBy_Aggregate_4(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupJoin_GroupBy_Aggregate_4(async));

        AssertMql(
        );
    }

    public override async Task GroupJoin_GroupBy_Aggregate_5(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupJoin_GroupBy_Aggregate_5(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_optional_navigation_member_Aggregate(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_optional_navigation_member_Aggregate(async));

        AssertMql(
        );
    }

    public override async Task GroupJoin_complex_GroupBy_Aggregate(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupJoin_complex_GroupBy_Aggregate(async));

        AssertMql(
        );
    }

    public override async Task Self_join_GroupBy_Aggregate(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.Self_join_GroupBy_Aggregate(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_multi_navigation_members_Aggregate(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_multi_navigation_members_Aggregate(async));

        AssertMql(
        );
    }

    public override async Task Union_simple_groupby(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.Union_simple_groupby(async));

        AssertMql(
        );
    }

    public override async Task Select_anonymous_GroupBy_Aggregate(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.Select_anonymous_GroupBy_Aggregate(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_principal_key_property_optimization(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_principal_key_property_optimization(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_after_anonymous_projection_and_distinct_followed_by_another_anonymous_projection(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() =>
            base.GroupBy_after_anonymous_projection_and_distinct_followed_by_another_anonymous_projection(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_complex_key_aggregate(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_complex_key_aggregate(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_complex_key_aggregate_2(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_complex_key_aggregate_2(async));

        AssertMql(
        );
    }

    public override async Task Select_collection_of_scalar_before_GroupBy_aggregate(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.Select_collection_of_scalar_before_GroupBy_aggregate(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_OrderBy_key(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_OrderBy_key(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_OrderBy_count(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_OrderBy_count(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_OrderBy_count_Select_sum(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_OrderBy_count_Select_sum(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_aggregate_Contains(bool async)
    {
        // Fails: GroupBy issue EF-149
        Assert.Contains(
            "Expression not supported",
            (await Assert.ThrowsAsync<ExpressionNotSupportedException>(() =>
                base.GroupBy_aggregate_Contains(async))).Message);

        AssertMql(
            """
            Orders.
            """);
    }

    public override async Task GroupBy_aggregate_Pushdown(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_aggregate_Pushdown(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_aggregate_using_grouping_key_Pushdown(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_aggregate_using_grouping_key_Pushdown(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_aggregate_Pushdown_followed_by_projecting_Length(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_aggregate_Pushdown_followed_by_projecting_Length(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_aggregate_Pushdown_followed_by_projecting_constant(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_aggregate_Pushdown_followed_by_projecting_constant(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_filter_key(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_filter_key(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_filter_count(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_filter_count(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_count_filter(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_count_filter(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_filter_count_OrderBy_count_Select_sum(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_filter_count_OrderBy_count_Select_sum(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_Aggregate_Join(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_Aggregate_Join(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_Aggregate_Join_converted_from_SelectMany(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_Aggregate_Join_converted_from_SelectMany(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_Aggregate_LeftJoin_converted_from_SelectMany(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_Aggregate_LeftJoin_converted_from_SelectMany(async));

        AssertMql(
        );
    }

    public override async Task Join_GroupBy_Aggregate_multijoins(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.Join_GroupBy_Aggregate_multijoins(async));

        AssertMql(
        );
    }

    public override async Task Join_GroupBy_Aggregate_single_join(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.Join_GroupBy_Aggregate_single_join(async));

        AssertMql(
        );
    }

    public override async Task Join_GroupBy_Aggregate_with_another_join(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.Join_GroupBy_Aggregate_with_another_join(async));

        AssertMql(
        );
    }

    public override async Task Join_GroupBy_Aggregate_distinct_single_join(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.Join_GroupBy_Aggregate_distinct_single_join(async));

        AssertMql(
        );
    }

    public override async Task Join_GroupBy_Aggregate_with_left_join(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.Join_GroupBy_Aggregate_with_left_join(async));

        AssertMql(
        );
    }

    public override async Task Join_GroupBy_Aggregate_in_subquery(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.Join_GroupBy_Aggregate_in_subquery(async));

        AssertMql(
        );
    }

    public override async Task Join_GroupBy_Aggregate_on_key(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.Join_GroupBy_Aggregate_on_key(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_with_result_selector(bool async)
    {
        // Fails: GroupBy issue EF-149
        Assert.Contains(
            "Expression of type 'MongoDB.EntityFrameworkCore.Query.QueryingEnumerable",
            (await Assert.ThrowsAsync<ArgumentException>(() =>
                base.GroupBy_with_result_selector(async))).Message);

        AssertMql(
        );
    }

    public override async Task GroupBy_Sum_constant(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_Sum_constant(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_Sum_constant_cast(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_Sum_constant_cast(async));

        AssertMql(
        );
    }

    public override async Task Distinct_GroupBy_OrderBy_key(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.Distinct_GroupBy_OrderBy_key(async));

        AssertMql(
        );
    }

    public override async Task Select_nested_collection_with_groupby(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.Select_nested_collection_with_groupby(async));

        AssertMql(
        );
    }

    public override async Task Select_uncorrelated_collection_with_groupby_works(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.Select_uncorrelated_collection_with_groupby_works(async));

        AssertMql(
        );
    }

    public override async Task Select_uncorrelated_collection_with_groupby_multiple_collections_work(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.Select_uncorrelated_collection_with_groupby_multiple_collections_work(async));

        AssertMql(
        );
    }

    public override async Task Select_GroupBy_All(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.Select_GroupBy_All(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_Where_Average(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_Where_Average(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_Where_Count(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_Where_Count(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_Where_LongCount(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_Where_LongCount(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_Where_Max(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_Where_Max(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_Where_Min(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_Where_Min(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_Where_Sum(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_Where_Sum(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_Where_Count_with_predicate(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_Where_Count_with_predicate(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_Where_Where_Count(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_Where_Where_Count(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_Where_Select_Where_Count(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_Where_Select_Where_Count(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_Where_Select_Where_Select_Min(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_Where_Select_Where_Select_Min(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_multiple_Count_with_predicate(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_multiple_Count_with_predicate(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_multiple_Sum_with_conditional_projection(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_multiple_Sum_with_conditional_projection(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_multiple_Sum_with_Select_conditional_projection(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_multiple_Sum_with_Select_conditional_projection(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_Key_as_part_of_element_selector(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_Key_as_part_of_element_selector(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_composite_Key_as_part_of_element_selector(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_composite_Key_as_part_of_element_selector(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_with_aggregate_through_navigation_property(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_with_aggregate_through_navigation_property(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_with_aggregate_containing_complex_where(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_with_aggregate_containing_complex_where(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_Shadow(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_Shadow(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_Shadow2(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_Shadow2(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_Shadow3(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_Shadow3(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_select_grouping_list(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_select_grouping_list(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_select_grouping_array(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_select_grouping_array(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_select_grouping_composed_list(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_select_grouping_composed_list(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_select_grouping_composed_list_2(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_select_grouping_composed_list_2(async));

        AssertMql(
        );
    }

    public override async Task Select_GroupBy_SelectMany(bool async)
    {
        await base.Select_GroupBy_SelectMany(async);

        AssertMql();
    }

    public override async Task Count_after_GroupBy_aggregate(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.Count_after_GroupBy_aggregate(async));

        AssertMql(
        );
    }

    public override async Task LongCount_after_GroupBy_aggregate(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.LongCount_after_GroupBy_aggregate(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_Select_Distinct_aggregate(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_Select_Distinct_aggregate(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_group_Distinct_Select_Distinct_aggregate(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_group_Distinct_Select_Distinct_aggregate(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_group_Where_Select_Distinct_aggregate(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_group_Where_Select_Distinct_aggregate(async));

        AssertMql(
        );
    }

    public override async Task MinMax_after_GroupBy_aggregate(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.MinMax_after_GroupBy_aggregate(async));

        AssertMql(
        );
    }

    public override async Task All_after_GroupBy_aggregate(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.All_after_GroupBy_aggregate(async));

        AssertMql(
        );
    }

    public override async Task All_after_GroupBy_aggregate2(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.All_after_GroupBy_aggregate2(async));

        AssertMql(
        );
    }

    public override async Task Any_after_GroupBy_aggregate(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.Any_after_GroupBy_aggregate(async));

        AssertMql(
        );
    }

    public override async Task Count_after_GroupBy_without_aggregate(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.Count_after_GroupBy_without_aggregate(async));

        AssertMql(
        );
    }

    public override async Task Count_with_predicate_after_GroupBy_without_aggregate(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.Count_with_predicate_after_GroupBy_without_aggregate(async));

        AssertMql(
        );
    }

    public override async Task LongCount_after_GroupBy_without_aggregate(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.LongCount_after_GroupBy_without_aggregate(async));

        AssertMql(
        );
    }

    public override async Task LongCount_with_predicate_after_GroupBy_without_aggregate(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.LongCount_with_predicate_after_GroupBy_without_aggregate(async));

        AssertMql(
        );
    }

    public override async Task Any_after_GroupBy_without_aggregate(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.Any_after_GroupBy_without_aggregate(async));

        AssertMql(
        );
    }

    public override async Task Any_with_predicate_after_GroupBy_without_aggregate(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.Any_with_predicate_after_GroupBy_without_aggregate(async));

        AssertMql(
        );
    }

    public override async Task All_with_predicate_after_GroupBy_without_aggregate(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.All_with_predicate_after_GroupBy_without_aggregate(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_aggregate_followed_by_another_GroupBy_aggregate(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_aggregate_followed_by_another_GroupBy_aggregate(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_Count_in_projection(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_Count_in_projection(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_nominal_type_count(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_nominal_type_count(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_based_on_renamed_property_simple(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_based_on_renamed_property_simple(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_based_on_renamed_property_complex(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_based_on_renamed_property_complex(async));

        AssertMql(
        );
    }

    public override async Task Join_groupby_anonymous_orderby_anonymous_projection(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.Join_groupby_anonymous_orderby_anonymous_projection(async));

        AssertMql(
        );
    }

    public override async Task Odata_groupby_empty_key(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.Odata_groupby_empty_key(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_with_group_key_access_thru_navigation(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_with_group_key_access_thru_navigation(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_with_group_key_access_thru_nested_navigation(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_with_group_key_access_thru_nested_navigation(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_with_group_key_being_navigation(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_with_group_key_being_navigation(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_with_group_key_being_nested_navigation(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_with_group_key_being_nested_navigation(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_with_group_key_being_navigation_with_entity_key_projection(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_with_group_key_being_navigation_with_entity_key_projection(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_with_group_key_being_navigation_with_complex_projection(bool async)
    {
        await base.GroupBy_with_group_key_being_navigation_with_complex_projection(async);

        AssertMql();
    }

    public override async Task GroupBy_with_order_by_skip_and_another_order_by(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_with_order_by_skip_and_another_order_by(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_Property_Select_Count_with_predicate(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_Property_Select_Count_with_predicate(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_Property_Select_LongCount_with_predicate(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_Property_Select_LongCount_with_predicate(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_orderby_projection_with_coalesce_operation(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_orderby_projection_with_coalesce_operation(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_let_orderby_projection_with_coalesce_operation(bool async)
    {
        await base.GroupBy_let_orderby_projection_with_coalesce_operation(async);

        AssertMql();
    }

    public override async Task GroupBy_Min_Where_optional_relationship(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_Min_Where_optional_relationship(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_Min_Where_optional_relationship_2(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_Min_Where_optional_relationship_2(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_aggregate_over_a_subquery(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_aggregate_over_a_subquery(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_aggregate_join_with_grouping_key(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_aggregate_join_with_grouping_key(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_aggregate_join_with_group_result(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_aggregate_join_with_group_result(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_aggregate_from_right_side_of_join(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_aggregate_from_right_side_of_join(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_aggregate_join_another_GroupBy_aggregate(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_aggregate_join_another_GroupBy_aggregate(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_aggregate_after_skip_0_take_0(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_aggregate_after_skip_0_take_0(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_skip_0_take_0_aggregate(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_skip_0_take_0_aggregate(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_aggregate_followed_another_GroupBy_aggregate(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_aggregate_followed_another_GroupBy_aggregate(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_aggregate_without_selectMany_selecting_first(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_aggregate_without_selectMany_selecting_first(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_aggregate_left_join_GroupBy_aggregate_left_join(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_aggregate_left_join_GroupBy_aggregate_left_join(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_selecting_grouping_key_list(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_selecting_grouping_key_list(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_with_grouping_key_using_Like(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_with_grouping_key_using_Like(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_with_grouping_key_DateTime_Day(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_with_grouping_key_DateTime_Day(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_with_cast_inside_grouping_aggregate(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_with_cast_inside_grouping_aggregate(async));

        AssertMql(
        );
    }

    public override async Task Complex_query_with_groupBy_in_subquery1(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.Complex_query_with_groupBy_in_subquery1(async));

        AssertMql(
        );
    }

    public override async Task Complex_query_with_groupBy_in_subquery2(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.Complex_query_with_groupBy_in_subquery2(async));

        AssertMql(
        );
    }

    public override async Task Complex_query_with_groupBy_in_subquery3(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.Complex_query_with_groupBy_in_subquery3(async));

        AssertMql(
        );
    }

    public override async Task Group_by_with_projection_into_DTO(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.Group_by_with_projection_into_DTO(async));

        AssertMql(
        );
    }

    public override async Task Where_select_function_groupby_followed_by_another_select_with_aggregates(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.Where_select_function_groupby_followed_by_another_select_with_aggregates(async));

        AssertMql(
        );
    }

    public override async Task Group_by_column_project_constant(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.Group_by_column_project_constant(async));

        AssertMql(
        );
    }

    public override async Task Key_plus_key_in_projection(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.Key_plus_key_in_projection(async));

        AssertMql(
        );
    }

    public override async Task Group_by_with_arithmetic_operation_inside_aggregate(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.Group_by_with_arithmetic_operation_inside_aggregate(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_scalar_subquery(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_scalar_subquery(async));

        AssertMql(
        );
    }

    public override async Task AsEnumerable_in_subquery_for_GroupBy(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.AsEnumerable_in_subquery_for_GroupBy(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_aggregate_from_multiple_query_in_same_projection(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_aggregate_from_multiple_query_in_same_projection(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_aggregate_from_multiple_query_in_same_projection_2(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_aggregate_from_multiple_query_in_same_projection_2(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_aggregate_from_multiple_query_in_same_projection_3(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_aggregate_from_multiple_query_in_same_projection_3(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_scalar_aggregate_in_set_operation(bool async)
    {
        await AssertNoMultiCollectionQuerySupport(() => base.GroupBy_scalar_aggregate_in_set_operation(async));
    }

    public override async Task Select_uncorrelated_collection_with_groupby_when_outer_is_distinct(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.Select_uncorrelated_collection_with_groupby_when_outer_is_distinct(async));

        AssertMql(
        );
    }

    public override async Task Select_correlated_collection_after_GroupBy_aggregate_when_identifier_does_not_change(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() =>
            base.Select_correlated_collection_after_GroupBy_aggregate_when_identifier_does_not_change(async));

        AssertMql(
        );
    }

    public override async Task Select_correlated_collection_after_GroupBy_aggregate_when_identifier_changes(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() =>
            base.Select_correlated_collection_after_GroupBy_aggregate_when_identifier_changes(async));

        AssertMql(
        );
    }

    public override async Task Select_correlated_collection_after_GroupBy_aggregate_when_identifier_changes_to_complex(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() =>
            base.Select_correlated_collection_after_GroupBy_aggregate_when_identifier_changes_to_complex(async));

        AssertMql(
        );
    }

    //AssertMql(" ");
    public override async Task Complex_query_with_group_by_in_subquery5(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.Complex_query_with_group_by_in_subquery5(async));

        AssertMql(
        );
    }

    public override async Task Complex_query_with_groupBy_in_subquery4(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.Complex_query_with_groupBy_in_subquery4(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_aggregate_SelectMany(bool async)
    {
        await base.GroupBy_aggregate_SelectMany(async);

        AssertMql();
    }

    public override async Task Final_GroupBy_property_entity(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.Final_GroupBy_property_entity(async));

        AssertMql(
        );
    }

    public override async Task Final_GroupBy_entity(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.Final_GroupBy_entity(async));

        AssertMql(
        );
    }

    public override async Task Final_GroupBy_property_entity_non_nullable(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.Final_GroupBy_property_entity_non_nullable(async));

        AssertMql(
        );
    }

    public override async Task Final_GroupBy_property_anonymous_type(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.Final_GroupBy_property_anonymous_type(async));

        AssertMql(
        );
    }

    public override async Task Final_GroupBy_multiple_properties_entity(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.Final_GroupBy_multiple_properties_entity(async));

        AssertMql(
        );
    }

    public override async Task Final_GroupBy_complex_key_entity(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.Final_GroupBy_complex_key_entity(async));

        AssertMql(
        );
    }

    public override async Task Final_GroupBy_nominal_type_entity(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.Final_GroupBy_nominal_type_entity(async));

        AssertMql(
        );
    }

    public override async Task Final_GroupBy_property_anonymous_type_element_selector(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.Final_GroupBy_property_anonymous_type_element_selector(async));

        AssertMql(
        );
    }

    public override async Task Final_GroupBy_property_entity_Include_collection(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.Final_GroupBy_property_entity_Include_collection(async));

        AssertMql(
        );
    }

    public override async Task Final_GroupBy_property_entity_projecting_collection(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.Final_GroupBy_property_entity_projecting_collection(async));

        AssertMql(
        );
    }

    public override async Task Final_GroupBy_property_entity_projecting_collection_composed(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.Final_GroupBy_property_entity_projecting_collection_composed(async));

        AssertMql(
        );
    }

    public override async Task Final_GroupBy_property_entity_projecting_collection_and_single_result(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.Final_GroupBy_property_entity_projecting_collection_and_single_result(async));

        AssertMql(
        );
    }

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

    public override async Task GroupBy_complex_key_without_aggregate(bool async)
    {
        await AssertGroupByUnsupported(() => base.GroupBy_complex_key_without_aggregate(async));
    }

    private void AssertMql(params string[] expected)
        => Fixture.TestMqlLoggerFactory.AssertBaseline(expected);

    protected override void ClearLog()
        => Fixture.TestMqlLoggerFactory.Clear();

    private static async Task AssertNoMultiCollectionQuerySupport(Func<Task> query)
        =>  Assert.Contains("Unsupported cross-DbSet query between",
            (await Assert.ThrowsAsync<InvalidOperationException>(query)).Message);

    // Fails: GroupBy issue EF-149
    private static async Task AssertGroupByUnsupported(Func<Task> query)
        => await AssertTranslationFailed(query);
}
