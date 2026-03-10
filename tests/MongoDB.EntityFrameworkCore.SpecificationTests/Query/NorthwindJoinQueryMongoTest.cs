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
using Xunit.Abstractions;

namespace MongoDB.EntityFrameworkCore.SpecificationTests.Query;

public class NorthwindJoinQueryMongoTest : NorthwindJoinQueryTestBase<NorthwindQueryMongoFixture<NoopModelCustomizer>>
{
    public NorthwindJoinQueryMongoTest(
        NorthwindQueryMongoFixture<NoopModelCustomizer> fixture,
        ITestOutputHelper testOutputHelper)
        : base(fixture)
    {
        ClearLog();
        //Fixture.TestMqlLoggerFactory.SetTestOutputHelper(testOutputHelper);
    }

    [ConditionalFact]
    public virtual void Check_all_tests_overridden()
        => TestHelpers.AssertAllMethodsOverridden(GetType());

#if !EF8 && !EF9

    [ConditionalTheory(Skip = "Include (joins) issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task LeftJoin(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include (joins) issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task RightJoin(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include (joins) issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task GroupJoin_aggregate_anonymous_key_selectors(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include (joins) issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task GroupJoin_aggregate_anonymous_key_selectors2(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include (joins) issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task GroupJoin_aggregate_anonymous_key_selectors_one_argument(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include (joins) issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task GroupJoin_aggregate_nested_anonymous_key_selectors(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include (joins) issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Join_with_key_selectors_being_nested_anonymous_objects(bool _)
        => Task.CompletedTask;

#endif

    [ConditionalTheory(Skip = "Include (joins) issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Join_customers_orders_projection(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include (joins) issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Join_customers_orders_entities(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include (joins) issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Join_select_many(bool _)
        => Task.CompletedTask;

    public override async Task Client_Join_select_many(bool async)
    {
        await base.Client_Join_select_many(async);

        AssertMql();
    }

    [ConditionalTheory(Skip = "Include (joins) issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Join_customers_orders_select(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include (joins) issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Join_customers_orders_with_subquery(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include (joins) issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Join_customers_orders_with_subquery_with_take(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include (joins) issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Join_customers_orders_with_subquery_anonymous_property_method(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include (joins) issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Join_customers_orders_with_subquery_anonymous_property_method_with_take(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include (joins) issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Join_customers_orders_with_subquery_predicate(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include (joins) issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Join_customers_orders_with_subquery_predicate_with_take(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include (joins) issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Join_composite_key(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include (joins) issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Join_complex_condition(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include (joins) issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Join_same_collection_multiple(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include (joins) issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Join_same_collection_force_alias_uniquefication(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include (joins) issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task GroupJoin_simple(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include (joins) issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task GroupJoin_simple2(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include (joins) issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task GroupJoin_simple3(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include (joins) issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task GroupJoin_simple_ordering(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include (joins) issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task GroupJoin_simple_subquery(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include (joins) issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task GroupJoin_as_final_operator(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include (joins) issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Unflattened_GroupJoin_composed(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include (joins) issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Unflattened_GroupJoin_composed_2(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include (joins) issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task GroupJoin_DefaultIfEmpty(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include (joins) issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task GroupJoin_DefaultIfEmpty_multiple(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include (joins) issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task GroupJoin_DefaultIfEmpty2(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include (joins) issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task GroupJoin_DefaultIfEmpty3(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include (joins) issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task GroupJoin_Where(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include (joins) issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task GroupJoin_Where_OrderBy(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include (joins) issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task GroupJoin_DefaultIfEmpty_Where(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include (joins) issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Join_GroupJoin_DefaultIfEmpty_Where(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include (joins) issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task GroupJoin_DefaultIfEmpty_Project(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include (joins) issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task GroupJoin_SelectMany_subquery_with_filter(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include (joins) issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task GroupJoin_SelectMany_subquery_with_filter_orderby(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include (joins) issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task GroupJoin_SelectMany_subquery_with_filter_and_DefaultIfEmpty(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include (joins) issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task GroupJoin_SelectMany_subquery_with_filter_orderby_and_DefaultIfEmpty(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include (joins) issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task GroupJoin_Subquery_with_Take_Then_SelectMany_Where(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include (joins) issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Inner_join_with_tautology_predicate_converts_to_cross_join(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include (joins) issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Left_join_with_tautology_predicate_doesnt_convert_to_cross_join(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include (joins) issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task SelectMany_with_client_eval(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include (joins) issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task SelectMany_with_client_eval_with_collection_shaper(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include (joins) issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task SelectMany_with_client_eval_with_collection_shaper_ignored(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include (joins) issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task SelectMany_with_client_eval_with_constructor(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include (joins) issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task SelectMany_with_selecting_outer_entity(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include (joins) issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task SelectMany_with_selecting_outer_element(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include (joins) issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task SelectMany_with_selecting_outer_entity_column_and_inner_column(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include (joins) issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task SelectMany_correlated_subquery_take(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include (joins) issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Distinct_SelectMany_correlated_subquery_take(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include (joins) issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Distinct_SelectMany_correlated_subquery_take_2(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include (joins) issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Take_SelectMany_correlated_subquery_take(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include (joins) issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Take_in_collection_projection_with_FirstOrDefault_on_top_level(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include (joins) issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Condition_on_entity_with_include(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include (joins) issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Join_customers_orders_entities_same_entity_twice(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include (joins) issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Join_local_collection_int_closure_is_cached_correctly(bool _)
        => Task.CompletedTask;

    public override async Task Join_local_string_closure_is_cached_correctly(bool async)
    {
        await base.Join_local_string_closure_is_cached_correctly(async);

        AssertMql();
    }

    public override async Task Join_local_bytes_closure_is_cached_correctly(bool async)
    {
        await base.Join_local_bytes_closure_is_cached_correctly(async);

        AssertMql();
    }

    [ConditionalTheory(Skip = "Include (joins) issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task GroupJoin_customers_employees_shadow(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include (joins) issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task GroupJoin_customers_employees_subquery_shadow(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include (joins) issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task GroupJoin_customers_employees_subquery_shadow_take(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include (joins) issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task GroupJoin_projection(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include (joins) issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task GroupJoin_subquery_projection_outer_mixed(bool _)
        => Task.CompletedTask;

#if EF9
    [ConditionalTheory(Skip = "Include (joins) issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task GroupJoin_on_true_equal_true(bool _)
        => Task.CompletedTask;

#endif

    private void AssertMql(params string[] expected)
        => Fixture.TestMqlLoggerFactory.AssertBaseline(expected);

    protected override void ClearLog()
        => Fixture.TestMqlLoggerFactory.Clear();
}
