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

    public override async Task LeftJoin(bool async)
    {
        await AssertTranslationFailed(() => base.LeftJoin(async));

        AssertMql(
        );
    }

    public override async Task RightJoin(bool async)
    {
        await AssertTranslationFailed(() => base.RightJoin(async));

        AssertMql(
        );
    }

    public override async Task GroupJoin_aggregate_anonymous_key_selectors(bool async)
    {
        await AssertTranslationFailed(() => base.GroupJoin_aggregate_anonymous_key_selectors(async));

        AssertMql(
        );
    }

    public override async Task GroupJoin_aggregate_anonymous_key_selectors2(bool async)
    {
        await AssertTranslationFailed(() => base.GroupJoin_aggregate_anonymous_key_selectors2(async));

        AssertMql(
        );
    }

    public override async Task GroupJoin_aggregate_anonymous_key_selectors_one_argument(bool async)
    {
        await AssertTranslationFailed(() => base.GroupJoin_aggregate_anonymous_key_selectors_one_argument(async));

        AssertMql(
        );
    }

    public override async Task GroupJoin_aggregate_nested_anonymous_key_selectors(bool async)
    {
        await AssertTranslationFailed(() => base.GroupJoin_aggregate_nested_anonymous_key_selectors(async));

        AssertMql(
        );
    }

    public override async Task Join_with_key_selectors_being_nested_anonymous_objects(bool async)
    {
        await AssertTranslationFailed(() => base.Join_with_key_selectors_being_nested_anonymous_objects(async));

        AssertMql(
        );
    }

#endif

    public override async Task Join_customers_orders_projection(bool async)
    {
        // Fails: Include (joins) issue EF-117
        await AssertTranslationFailed(() => base.Join_customers_orders_projection(async));

        AssertMql(
        );
    }

    public override async Task Join_customers_orders_entities(bool async)
    {
        // Fails: Include (joins) issue EF-117
        await AssertTranslationFailed(() => base.Join_customers_orders_entities(async));

        AssertMql(
        );
    }

    public override async Task Join_select_many(bool async)
    {
        // Fails: Include (joins) issue EF-117
        await AssertTranslationFailed(() => base.Join_select_many(async));

        AssertMql(
        );
    }

    public override async Task Client_Join_select_many(bool async)
    {
        await base.Client_Join_select_many(async);

        AssertMql();
    }

    public override async Task Join_customers_orders_select(bool async)
    {
        // Fails: Include (joins) issue EF-117
        await AssertTranslationFailed(() => base.Join_customers_orders_select(async));

        AssertMql(
        );
    }

    public override async Task Join_customers_orders_with_subquery(bool async)
    {
        // Fails: Include (joins) issue EF-117
        await AssertTranslationFailed(() => base.Join_customers_orders_with_subquery(async));

        AssertMql(
        );
    }

    public override async Task Join_customers_orders_with_subquery_with_take(bool async)
    {
        // Fails: Include (joins) issue EF-117
        await AssertTranslationFailed(() => base.Join_customers_orders_with_subquery_with_take(async));

        AssertMql(
        );
    }

    public override async Task Join_customers_orders_with_subquery_anonymous_property_method(bool async)
    {
        // Fails: Include (joins) issue EF-117
        await AssertTranslationFailed(() => base.Join_customers_orders_with_subquery_anonymous_property_method(async));

        AssertMql(
        );
    }

    public override async Task Join_customers_orders_with_subquery_anonymous_property_method_with_take(bool async)
    {
        // Fails: Include (joins) issue EF-117
        await AssertTranslationFailed(() => base.Join_customers_orders_with_subquery_anonymous_property_method_with_take(async));

        AssertMql(
        );
    }

    public override async Task Join_customers_orders_with_subquery_predicate(bool async)
    {
        // Fails: Include (joins) issue EF-117
        await AssertTranslationFailed(() => base.Join_customers_orders_with_subquery_predicate(async));

        AssertMql(
        );
    }

    public override async Task Join_customers_orders_with_subquery_predicate_with_take(bool async)
    {
        // Fails: Include (joins) issue EF-117
        await AssertTranslationFailed(() => base.Join_customers_orders_with_subquery_predicate_with_take(async));

        AssertMql(
        );
    }

    public override async Task Join_composite_key(bool async)
    {
        // Fails: Include (joins) issue EF-117
        await AssertTranslationFailed(() => base.Join_composite_key(async));

        AssertMql(
        );
    }

    public override async Task Join_complex_condition(bool async)
    {
        // Fails: Include (joins) issue EF-117
        await AssertTranslationFailed(() => base.Join_complex_condition(async));

        AssertMql(
        );
    }

    public override async Task Join_same_collection_multiple(bool async)
    {
        // Fails: Include (joins) issue EF-117
        await AssertTranslationFailed(() => base.Join_same_collection_multiple(async));

        AssertMql(
        );
    }

    public override async Task Join_same_collection_force_alias_uniquefication(bool async)
    {
        // Fails: Include (joins) issue EF-117
        await AssertTranslationFailed(() => base.Join_same_collection_force_alias_uniquefication(async));

        AssertMql(
        );
    }

    public override async Task GroupJoin_simple(bool async)
    {
        // Fails: Include (joins) issue EF-117
        await AssertTranslationFailed(() => base.GroupJoin_simple(async));

        AssertMql(
        );
    }

    public override async Task GroupJoin_simple2(bool async)
    {
        // Fails: Include (joins) issue EF-117
        await AssertTranslationFailed(() => base.GroupJoin_simple2(async));

        AssertMql(
        );
    }

    public override async Task GroupJoin_simple3(bool async)
    {
        // Fails: Include (joins) issue EF-117
        await AssertTranslationFailed(() => base.GroupJoin_simple3(async));

        AssertMql(
        );
    }

    public override async Task GroupJoin_simple_ordering(bool async)
    {
        // Fails: Include (joins) issue EF-117
        await AssertTranslationFailed(() => base.GroupJoin_simple_ordering(async));

        AssertMql(
        );
    }

    public override async Task GroupJoin_simple_subquery(bool async)
    {
        // Fails: Include (joins) issue EF-117
        await AssertTranslationFailed(() => base.GroupJoin_simple_subquery(async));

        AssertMql(
        );
    }

    public override async Task GroupJoin_as_final_operator(bool async)
    {
        // Fails: Include (joins) issue EF-117
        await AssertTranslationFailed(() => base.GroupJoin_as_final_operator(async));

        AssertMql(
        );
    }

    public override async Task Unflattened_GroupJoin_composed(bool async)
    {
        // Fails: Include (joins) issue EF-117
        await AssertTranslationFailed(() => base.Unflattened_GroupJoin_composed(async));

        AssertMql(
        );
    }

    public override async Task Unflattened_GroupJoin_composed_2(bool async)
    {
        // Fails: Include (joins) issue EF-117
        await AssertTranslationFailed(() => base.Unflattened_GroupJoin_composed_2(async));

        AssertMql(
        );
    }

    public override async Task GroupJoin_DefaultIfEmpty(bool async)
    {
        // Fails: Include (joins) issue EF-117
        await AssertTranslationFailed(() => base.GroupJoin_DefaultIfEmpty(async));

        AssertMql(
        );
    }

    public override async Task GroupJoin_DefaultIfEmpty_multiple(bool async)
    {
        // Fails: Include (joins) issue EF-117
        await AssertTranslationFailed(() => base.GroupJoin_DefaultIfEmpty_multiple(async));

        AssertMql(
        );
    }

    public override async Task GroupJoin_DefaultIfEmpty2(bool async)
    {
        // Fails: Include (joins) issue EF-117
        await AssertTranslationFailed(() => base.GroupJoin_DefaultIfEmpty2(async));

        AssertMql(
        );
    }

    public override async Task GroupJoin_DefaultIfEmpty3(bool async)
    {
        // Fails: Include (joins) issue EF-117
        await AssertTranslationFailed(() => base.GroupJoin_DefaultIfEmpty3(async));

        AssertMql(
        );
    }

    public override async Task GroupJoin_Where(bool async)
    {
        // Fails: Include (joins) issue EF-117
        await AssertTranslationFailed(() => base.GroupJoin_Where(async));

        AssertMql(
        );
    }

    public override async Task GroupJoin_Where_OrderBy(bool async)
    {
        // Fails: Include (joins) issue EF-117
        await AssertTranslationFailed(() => base.GroupJoin_Where_OrderBy(async));

        AssertMql(
        );
    }

    public override async Task GroupJoin_DefaultIfEmpty_Where(bool async)
    {
        // Fails: Include (joins) issue EF-117
        await AssertTranslationFailed(() => base.GroupJoin_DefaultIfEmpty_Where(async));

        AssertMql(
        );
    }

    public override async Task Join_GroupJoin_DefaultIfEmpty_Where(bool async)
    {
        // Fails: Include (joins) issue EF-117
        await AssertTranslationFailed(() => base.Join_GroupJoin_DefaultIfEmpty_Where(async));

        AssertMql(
        );
    }

    public override async Task GroupJoin_DefaultIfEmpty_Project(bool async)
    {
        // Fails: Include (joins) issue EF-117
        await AssertTranslationFailed(() => base.GroupJoin_DefaultIfEmpty_Project(async));

        AssertMql(
        );
    }

    public override async Task GroupJoin_SelectMany_subquery_with_filter(bool async)
    {
        // Fails: Include (joins) issue EF-117
        await AssertTranslationFailed(() => base.GroupJoin_SelectMany_subquery_with_filter(async));

        AssertMql(
        );
    }

    public override async Task GroupJoin_SelectMany_subquery_with_filter_orderby(bool async)
    {
        // Fails: Include (joins) issue EF-117
        await AssertTranslationFailed(() => base.GroupJoin_SelectMany_subquery_with_filter_orderby(async));

        AssertMql(
        );
    }

    public override async Task GroupJoin_SelectMany_subquery_with_filter_and_DefaultIfEmpty(bool async)
    {
        // Fails: Include (joins) issue EF-117
        await AssertTranslationFailed(() => base.GroupJoin_SelectMany_subquery_with_filter_and_DefaultIfEmpty(async));

        AssertMql(
        );
    }

    public override async Task GroupJoin_SelectMany_subquery_with_filter_orderby_and_DefaultIfEmpty(bool async)
    {
        // Fails: Include (joins) issue EF-117
        await AssertTranslationFailed(() => base.GroupJoin_SelectMany_subquery_with_filter_orderby_and_DefaultIfEmpty(async));

        AssertMql(
        );
    }

    public override async Task GroupJoin_Subquery_with_Take_Then_SelectMany_Where(bool async)
    {
        // Fails: Include (joins) issue EF-117
        await AssertTranslationFailed(() => base.GroupJoin_Subquery_with_Take_Then_SelectMany_Where(async));

        AssertMql(
        );
    }

    public override async Task Inner_join_with_tautology_predicate_converts_to_cross_join(bool async)
    {
        // Fails: Include (joins) issue EF-117
        await AssertTranslationFailed(() => base.Inner_join_with_tautology_predicate_converts_to_cross_join(async));

        AssertMql(
        );
    }

    public override async Task Left_join_with_tautology_predicate_doesnt_convert_to_cross_join(bool async)
    {
        // Fails: Include (joins) issue EF-117
        await AssertTranslationFailed(() => base.Left_join_with_tautology_predicate_doesnt_convert_to_cross_join(async));

        AssertMql(
        );
    }

    public override async Task SelectMany_with_client_eval(bool async)
    {
        // Fails: Include (joins) issue EF-117
        await AssertTranslationFailed(() => base.SelectMany_with_client_eval(async));

        AssertMql(
        );
    }

    public override async Task SelectMany_with_client_eval_with_collection_shaper(bool async)
    {
        // Fails: Include (joins) issue EF-117
        await AssertTranslationFailed(() => base.SelectMany_with_client_eval_with_collection_shaper(async));

        AssertMql(
        );
    }

    public override async Task SelectMany_with_client_eval_with_collection_shaper_ignored(bool async)
    {
        // Fails: Include (joins) issue EF-117
        await AssertTranslationFailed(() => base.SelectMany_with_client_eval_with_collection_shaper_ignored(async));

        AssertMql(
        );
    }

    public override async Task SelectMany_with_client_eval_with_constructor(bool async)
    {
        // Fails: Include (joins) issue EF-117
        await AssertTranslationFailed(() => base.SelectMany_with_client_eval_with_constructor(async));

        AssertMql(
        );
    }

    public override async Task SelectMany_with_selecting_outer_entity(bool async)
    {
        // Fails: Include (joins) issue EF-117
        await AssertTranslationFailed(() => base.SelectMany_with_selecting_outer_entity(async));

        AssertMql(
        );
    }

    public override async Task SelectMany_with_selecting_outer_element(bool async)
    {
        // Fails: Include (joins) issue EF-117
        await AssertTranslationFailed(() => base.SelectMany_with_selecting_outer_element(async));

        AssertMql(
        );
    }

    public override async Task SelectMany_with_selecting_outer_entity_column_and_inner_column(bool async)
    {
        // Fails: Include (joins) issue EF-117
        await AssertTranslationFailed(() => base.SelectMany_with_selecting_outer_entity_column_and_inner_column(async));

        AssertMql(
        );
    }

    public override async Task SelectMany_correlated_subquery_take(bool async)
    {
        // Fails: Include (joins) issue EF-117
        await AssertTranslationFailed(() => base.SelectMany_correlated_subquery_take(async));

        AssertMql(
        );
    }

    public override async Task Distinct_SelectMany_correlated_subquery_take(bool async)
    {
        // Fails: Include (joins) issue EF-117
        await AssertTranslationFailed(() => base.Distinct_SelectMany_correlated_subquery_take(async));

        AssertMql(
        );
    }

    public override async Task Distinct_SelectMany_correlated_subquery_take_2(bool async)
    {
        // Fails: Include (joins) issue EF-117
        await AssertTranslationFailed(() => base.Distinct_SelectMany_correlated_subquery_take_2(async));

        AssertMql(
        );
    }

    public override async Task Take_SelectMany_correlated_subquery_take(bool async)
    {
        // Fails: Include (joins) issue EF-117
        await AssertTranslationFailed(() => base.Take_SelectMany_correlated_subquery_take(async));

        AssertMql(
        );
    }

    public override async Task Take_in_collection_projection_with_FirstOrDefault_on_top_level(bool async)
    {
        // Fails: Include (joins) issue EF-117
        await AssertTranslationFailed(() => base.Take_in_collection_projection_with_FirstOrDefault_on_top_level(async));

        AssertMql(
        );
    }

    public override async Task Condition_on_entity_with_include(bool async)
    {
        // Fails: Include (joins) issue EF-117
        await AssertTranslationFailed(() => base.Condition_on_entity_with_include(async));

        AssertMql(
        );
    }

    public override async Task Join_customers_orders_entities_same_entity_twice(bool async)
    {
        // Fails: Include (joins) issue EF-117
        await AssertTranslationFailed(() => base.Join_customers_orders_entities_same_entity_twice(async));

        AssertMql(
        );
    }

    public override async Task Join_local_collection_int_closure_is_cached_correctly(bool async)
    {
        // Fails: Include (joins) issue EF-117
        await AssertTranslationFailed(() => base.Join_local_collection_int_closure_is_cached_correctly(async));

        AssertMql(
        );
    }

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

    public override async Task GroupJoin_customers_employees_shadow(bool async)
    {
        // Fails: Include (joins) issue EF-117
        await AssertTranslationFailed(() => base.GroupJoin_customers_employees_shadow(async));

        AssertMql(
        );
    }

    public override async Task GroupJoin_customers_employees_subquery_shadow(bool async)
    {
        // Fails: Include (joins) issue EF-117
        await AssertTranslationFailed(() => base.GroupJoin_customers_employees_subquery_shadow(async));

        AssertMql(
        );
    }

    public override async Task GroupJoin_customers_employees_subquery_shadow_take(bool async)
    {
        // Fails: Include (joins) issue EF-117
        await AssertTranslationFailed(() => base.GroupJoin_customers_employees_subquery_shadow_take(async));

        AssertMql(
        );
    }

    public override async Task GroupJoin_projection(bool async)
    {
        // Fails: Include (joins) issue EF-117
        await AssertTranslationFailed(() => base.GroupJoin_projection(async));

        AssertMql(
        );
    }

    public override async Task GroupJoin_subquery_projection_outer_mixed(bool async)
    {
        // Fails: Include (joins) issue EF-117
        await AssertTranslationFailed(() => base.GroupJoin_subquery_projection_outer_mixed(async));

        AssertMql(
        );
    }

#if EF9
    public override async Task GroupJoin_on_true_equal_true(bool async)
    {
        // Fails: Include (joins) issue EF-117
        await AssertTranslationFailed(() => base.GroupJoin_on_true_equal_true(async));

        AssertMql(
);
    }

#endif

    private void AssertMql(params string[] expected)
        => Fixture.TestMqlLoggerFactory.AssertBaseline(expected);

    protected override void ClearLog()
        => Fixture.TestMqlLoggerFactory.Clear();
}
