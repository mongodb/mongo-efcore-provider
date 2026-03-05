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
using Xunit.Sdk;

namespace MongoDB.EntityFrameworkCore.SpecificationTests.Query;

public class NorthwindStringIncludeQueryMongoTest : NorthwindStringIncludeQueryTestBase<
    NorthwindQueryMongoFixture<NoopModelCustomizer>>
{
    public NorthwindStringIncludeQueryMongoTest(NorthwindQueryMongoFixture<NoopModelCustomizer> fixture)
        : base(fixture)
        => Fixture.TestMqlLoggerFactory.Clear();

    [ConditionalFact]
    public virtual void Check_all_tests_overridden()
        => TestHelpers.AssertAllMethodsOverridden(GetType());

#if !EF8 && !EF9

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Include_collection_with_right_join_clause_with_filter(bool _)
        => Task.CompletedTask;

#endif

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Include_collection_with_last_no_orderby(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Include_collection_with_filter_reordered(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Include_collection_order_by_non_key_with_first_or_default(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Include_with_cycle_does_not_throw_when_AsTracking_NoTrackingWithIdentityResolution(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Include_collection_with_filter(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Include_references_then_include_multi_level(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Include_collection_order_by_collection_column(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Include_collection_alias_generation(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Include_collection_skip_take_no_order_by(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Include_collection_with_cross_join_clause_with_filter(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Join_Include_reference_GroupBy_Select(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Include_multi_level_reference_and_collection_predicate(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Include_references_then_include_collection(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Include_collection_on_additional_from_clause_with_filter(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Include_duplicate_reference3(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Include_collection_order_by_non_key_with_take(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Include_collection_then_include_collection_predicate(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Include_collection_take_no_order_by(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Include_collection_principal_already_tracked(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Include_collection_OrderBy_object(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Include_duplicate_collection_result_operator2(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Repro9735(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Include_collection_single_or_default_no_result(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Include_collection_with_cross_apply_with_filter(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Include_collection_with_left_join_clause_with_filter(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Include_duplicate_collection(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Include_collection(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Include_collection_then_include_collection_then_include_reference(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Include_reference_GroupBy_Select(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Include_multiple_references_multi_level_reverse(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Include_collection_with_join_clause_with_filter(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Include_collection_OrderBy_list_does_not_contains(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Include_reference_dependent_already_tracked(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Include_reference_with_filter(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Include_duplicate_reference(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Include_with_complex_projection(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Include_collection_order_by_non_key_with_skip(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Include_collection_on_join_clause_with_order_by_and_filter(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Multi_level_includes_are_applied_with_take(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Include_multiple_references_then_include_collection_multi_level_reverse(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Include_collection_then_reference(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Include_collection_order_by_key(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Include_collection_with_outer_apply_with_filter(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Include_collection_on_additional_from_clause2(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Include_collection_dependent_already_tracked(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Include_with_complex_projection_does_not_change_ordering_of_projection(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Include_multi_level_collection_and_then_include_reference_predicate(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Multi_level_includes_are_applied_with_skip_take(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Include_collection_OrderBy_empty_list_contains(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Include_references_and_collection_multi_level(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Include_collection_force_alias_uniquefication(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Include_collection_with_outer_apply_with_filter_non_equality(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Include_in_let_followed_by_FirstOrDefault(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Include_references_multi_level(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Include_collection_then_include_collection(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Include_collection_with_multiple_conditional_order_by(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Include_reference_when_entity_in_projection(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Include_reference_single_or_default_when_no_result(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Include_reference_alias_generation(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Include_with_cycle_does_not_throw_when_AsNoTrackingWithIdentityResolution(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Include_references_then_include_collection_multi_level(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Include_reference_Join_GroupBy_Select(bool _)
        => Task.CompletedTask;

    public override async Task Include_collection_when_projection(bool async)
    {
        await base.Include_collection_when_projection(async);

        AssertMql(
            """
            Customers.{ "$project" : { "_v" : "$_id", "_id" : 0 } }
            """);
    }

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Include_reference_SelectMany_GroupBy_Select(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Include_multiple_references_then_include_collection_multi_level(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Outer_identifier_correctly_determined_when_doing_include_on_right_side_of_left_join(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task SelectMany_Include_reference_GroupBy_Select(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Include_collection_SelectMany_GroupBy_Select(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Include_collection_OrderBy_list_contains(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Multi_level_includes_are_applied_with_skip(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Include_collection_on_additional_from_clause(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Include_reference_distinct_is_server_evaluated(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Include_collection_distinct_is_server_evaluated(bool _)
        => Task.CompletedTask;

    public override async Task Include_reference_when_projection(bool async)
    {
        await base.Include_reference_when_projection(async);

        AssertMql(
            """
            Orders.{ "$project" : { "_v" : "$CustomerID", "_id" : 0 } }
            """);
    }

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Include_duplicate_collection_result_operator(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Include_reference(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Include_multiple_references_and_collection_multi_level_reverse(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Include_closes_reader(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Include_with_skip(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Include_collection_Join_GroupBy_Select(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Include_collection_GroupBy_Select(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Include_collection_orderby_take(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Join_Include_collection_GroupBy_Select(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Include_collection_order_by_non_key(bool _)
        => Task.CompletedTask;

    public override async Task Include_when_result_operator(bool async)
    {
        await base.Include_when_result_operator(async);

        AssertMql(
            """
            Customers.{ "$limit" : 1 }, { "$project" : { "_id" : 0, "_v" : null } }
            """);
    }

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Include_duplicate_reference2(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Include_collection_and_reference(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Include_multiple_references_multi_level(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Include_references_and_collection_multi_level_predicate(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task SelectMany_Include_collection_GroupBy_Select(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Include_collection_with_last(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Include_collection_OrderBy_empty_list_does_not_contains(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Include_multiple_references_then_include_multi_level_reverse(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Include_reference_and_collection(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Include_is_not_ignored_when_projection_contains_client_method_and_complex_expression(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Include_reference_with_filter_reordered(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Include_collection_order_by_subquery(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Include_reference_and_collection_order_by(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Then_include_collection_order_by_collection_column(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Include_multiple_references_then_include_multi_level(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Include_collection_skip_no_order_by(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Include_multi_level_reference_then_include_collection_predicate(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Include_multiple_references_and_collection_multi_level(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Include_where_skip_take_projection(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Include_with_take(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Include_multiple_references(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Include_list(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Include_empty_reference_sets_IsLoaded(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Include_references_then_include_collection_multi_level_predicate(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Include_collection_with_conditional_order_by(bool _)
        => Task.CompletedTask;

    public override async Task Include_non_existing_navigation(bool async)
    {
        await base.Include_non_existing_navigation(async);

        AssertMql();
    }

    public override async Task Include_property(bool async)
    {
        await base.Include_property(async);

        AssertMql();
    }

    public override async Task Include_property_after_navigation(bool async)
    {
        await base.Include_property_after_navigation(async);

        AssertMql();
    }

    public override async Task Include_property_expression_invalid(bool async)
    {
        await base.Include_property_expression_invalid(async);

        AssertMql();
    }

    public override async Task Then_include_property_expression_invalid(bool async)
    {
        await base.Then_include_property_expression_invalid(async);

        AssertMql();
    }

    public override async Task Filtered_include_with_multiple_ordering(bool async)
    {
        await base.Filtered_include_with_multiple_ordering(async);

        AssertMql(
        );
    }

    public override async Task Include_specified_on_non_entity_not_supported(bool async)
    {
        await base.Include_specified_on_non_entity_not_supported(async);

        AssertMql();
    }

    public override async Task Include_collection_with_client_filter(bool async)
    {
        // Throws with Mongo-specific message rather than the generic EF message.
        Assert.Contains(
            "Including navigation 'Navigation' is not",
            (await Assert.ThrowsAsync<ContainsException>(() => base.Include_collection_with_client_filter(async))).Message);

        AssertMql();
    }

    private void AssertMql(params string[] expected)
        => Fixture.TestMqlLoggerFactory.AssertBaseline(expected);
}
