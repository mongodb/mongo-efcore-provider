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

public class NorthwindIncludeQueryMongoTest : NorthwindIncludeQueryTestBase<
    NorthwindQueryMongoFixture<NoopModelCustomizer>>
{
    public NorthwindIncludeQueryMongoTest(NorthwindQueryMongoFixture<NoopModelCustomizer> fixture)
        : base(fixture)
        => Fixture.TestMqlLoggerFactory.Clear();

    [ConditionalFact]
    public virtual void Check_all_tests_overridden()
        => TestHelpers.AssertAllMethodsOverridden(GetType());

#if !EF8 && !EF9

    public override async Task Include_collection_with_right_join_clause_with_filter(bool async)
    {
        await AssertTranslationFailed(() => base.Include_collection_with_right_join_clause_with_filter(async));
    }

#endif

    public override async Task Include_collection_with_last_no_orderby(bool async)
    {
        // Fails: Include issue EF-117
        Assert.Contains(
            "Including navigation 'Navigation' is not supported",
            (await Assert.ThrowsAsync<InvalidOperationException>(() => base.Include_collection_with_last_no_orderby(async)))
            .Message);

        AssertMql();
    }

    public override async Task Include_collection_with_filter_reordered(bool async)
    {
        // Fails: Include issue EF-117
        Assert.Contains(
            "Including navigation 'Navigation' is not supported",
            (await Assert.ThrowsAsync<InvalidOperationException>(() => base.Include_collection_with_filter_reordered(async)))
            .Message);

        AssertMql(
        );
    }

    public override async Task Include_collection_order_by_non_key_with_first_or_default(bool async)
    {
        // Fails: Include issue EF-117
        Assert.Contains(
            "Including navigation 'Navigation' is not supported",
            (await Assert.ThrowsAsync<InvalidOperationException>(() =>
                base.Include_collection_order_by_non_key_with_first_or_default(async))).Message);

        AssertMql(
        );
    }

    public override async Task Include_with_cycle_does_not_throw_when_AsTracking_NoTrackingWithIdentityResolution(bool async)
    {
        // Fails: Include issue EF-117
        await AssertTranslationFailed(() =>
            base.Include_with_cycle_does_not_throw_when_AsTracking_NoTrackingWithIdentityResolution(async));

        AssertMql(
        );
    }

    public override async Task Include_collection_with_filter(bool async)
    {
        // Fails: Include issue EF-117
        Assert.Contains(
            "Including navigation 'Navigation' is not supported",
            (await Assert.ThrowsAsync<InvalidOperationException>(() => base.Include_collection_with_filter(async))).Message);

        AssertMql(
        );
    }

    public override async Task Include_references_then_include_multi_level(bool async)
    {
        // Fails: Include issue EF-117
        await AssertTranslationFailed(() => base.Include_references_then_include_multi_level(async));

        AssertMql(
        );
    }

    public override async Task Include_collection_order_by_collection_column(bool async)
    {
        // Fails: Include issue EF-117
        Assert.Contains(
            "Including navigation 'Navigation' is not supported",
            (await Assert.ThrowsAsync<InvalidOperationException>(() => base.Include_collection_order_by_collection_column(async)))
            .Message);

        AssertMql(
        );
    }

    public override async Task Include_collection_alias_generation(bool async)
    {
        // Fails: Include issue EF-117
        Assert.Contains(
            "Including navigation 'Navigation' is not supported",
            (await Assert.ThrowsAsync<InvalidOperationException>(() => base.Include_collection_alias_generation(async))).Message);

        AssertMql(
        );
    }

    public override async Task Include_collection_skip_take_no_order_by(bool async)
    {
        // Fails: Include issue EF-117
        Assert.Contains(
            "Including navigation 'Navigation' is not supported",
            (await Assert.ThrowsAsync<InvalidOperationException>(() => base.Include_collection_skip_take_no_order_by(async)))
            .Message);
        AssertMql(
        );
    }

    public override async Task Include_collection_with_cross_join_clause_with_filter(bool async)
    {
        // Fails: Include issue EF-117
        await AssertTranslationFailed(() => base.Include_collection_with_cross_join_clause_with_filter(async));

        AssertMql(
        );
    }

    public override async Task Join_Include_reference_GroupBy_Select(bool async)
    {
        // Fails: Include issue EF-117
        await AssertTranslationFailed(() => base.Join_Include_reference_GroupBy_Select(async));

        AssertMql(
        );
    }

    public override async Task Include_multi_level_reference_and_collection_predicate(bool async)
    {
        // Fails: Include issue EF-117
        await AssertTranslationFailed(() => base.Include_multi_level_reference_and_collection_predicate(async));

        AssertMql(
        );
    }

    public override async Task Include_references_then_include_collection(bool async)
    {
        // Fails: Include issue EF-117
        await AssertTranslationFailed(() => base.Include_references_then_include_collection(async));

        AssertMql(
        );
    }

    public override async Task Include_collection_on_additional_from_clause_with_filter(bool async)
    {
        // Fails: Include issue EF-117
        await AssertTranslationFailed(() => base.Include_collection_on_additional_from_clause_with_filter(async));

        AssertMql(
        );
    }

    public override async Task Include_duplicate_reference3(bool async)
    {
        // Fails: Include issue EF-117
        await AssertTranslationFailed(() => base.Include_duplicate_reference3(async));

        AssertMql(
        );
    }

    public override async Task Include_collection_order_by_non_key_with_take(bool async)
    {
        // Fails: Include issue EF-117
        Assert.Contains(
            "Including navigation 'Navigation' is not supported",
            (await Assert.ThrowsAsync<InvalidOperationException>(() => base.Include_collection_order_by_non_key_with_take(async)))
            .Message);

        AssertMql(
        );
    }

    public override async Task Include_collection_then_include_collection_predicate(bool async)
    {
        // Fails: Include issue EF-117
        Assert.Contains(
            "Including navigation 'Navigation' is not supported",
            (await Assert.ThrowsAsync<InvalidOperationException>(() =>
                base.Include_collection_then_include_collection_predicate(async))).Message);

        AssertMql(
        );
    }

    public override async Task Include_collection_take_no_order_by(bool async)
    {
        // Fails: Include issue EF-117
        Assert.Contains(
            "Including navigation 'Navigation' is not supported",
            (await Assert.ThrowsAsync<InvalidOperationException>(() => base.Include_collection_take_no_order_by(async))).Message);

        AssertMql(
        );
    }

    public override async Task Include_collection_principal_already_tracked(bool async)
    {
        // Fails: Include issue EF-117
        Assert.Contains(
            "Including navigation 'Navigation' is not supported",
            (await Assert.ThrowsAsync<InvalidOperationException>(() => base.Include_collection_principal_already_tracked(async)))
            .Message);

        AssertMql(
            """
            Customers.{ "$match" : { "_id" : "ALFKI" } }, { "$limit" : 2 }
            """);
    }

    public override async Task Include_collection_OrderBy_object(bool async)
    {
        // Fails: Include issue EF-117
        Assert.Contains(
            "Including navigation 'Navigation' is not supported",
            (await Assert.ThrowsAsync<InvalidOperationException>(() => base.Include_collection_OrderBy_object(async))).Message);

        AssertMql(
        );
    }

    public override async Task Include_duplicate_collection_result_operator2(bool async)
    {
        // Fails: Include issue EF-117
        await AssertTranslationFailed(() => base.Include_duplicate_collection_result_operator2(async));

        AssertMql(
        );
    }

    public override async Task Repro9735(bool async)
    {
        // Fails: Include issue EF-117
        await AssertTranslationFailed(() => base.Repro9735(async));

        AssertMql(
        );
    }

    public override async Task Include_collection_single_or_default_no_result(bool async)
    {
        // Fails: Include issue EF-117
        Assert.Contains(
            "Including navigation 'Navigation' is not supported",
            (await Assert.ThrowsAsync<InvalidOperationException>(() => base.Include_collection_single_or_default_no_result(async)))
            .Message);

        AssertMql(
        );
    }

    public override async Task Include_collection_with_cross_apply_with_filter(bool async)
    {
        // Fails: Include issue EF-117
        await AssertTranslationFailed(() => base.Include_collection_with_cross_apply_with_filter(async));

        AssertMql(
        );
    }

    public override async Task Include_collection_with_left_join_clause_with_filter(bool async)
    {
        // Fails: Include issue EF-117
        await AssertTranslationFailed(() => base.Include_collection_with_left_join_clause_with_filter(async));

        AssertMql(
        );
    }

    public override async Task Include_duplicate_collection(bool async)
    {
        // Fails: Include issue EF-117
        await AssertTranslationFailed(() => base.Include_duplicate_collection(async));

        AssertMql(
        );
    }

    public override async Task Include_collection(bool async)
    {
        // Fails: Include issue EF-117
        Assert.Contains(
            "Including navigation 'Navigation' is not supported",
            (await Assert.ThrowsAsync<InvalidOperationException>(() => base.Include_collection(async))).Message);

        AssertMql(
        );
    }

    public override async Task Include_collection_then_include_collection_then_include_reference(bool async)
    {
        // Fails: Include issue EF-117
        Assert.Contains(
            "Including navigation 'Navigation' is not supported",
            (await Assert.ThrowsAsync<InvalidOperationException>(() =>
                base.Include_collection_then_include_collection_then_include_reference(async))).Message);

        AssertMql(
        );
    }

    public override async Task Include_reference_GroupBy_Select(bool async)
    {
        // Fails: Include issue EF-117
        await AssertTranslationFailed(() => base.Include_reference_GroupBy_Select(async));

        AssertMql(
        );
    }

    public override async Task Include_multiple_references_multi_level_reverse(bool async)
    {
        // Fails: Include issue EF-117
        await AssertTranslationFailed(() => base.Include_multiple_references_multi_level_reverse(async));

        AssertMql(
        );
    }

    public override async Task Include_collection_with_join_clause_with_filter(bool async)
    {
        // Fails: Include issue EF-117
        await AssertTranslationFailed(() => base.Include_collection_with_join_clause_with_filter(async));

        AssertMql(
        );
    }

    public override async Task Include_collection_OrderBy_list_does_not_contains(bool async)
    {
        // Fails: Include issue EF-117
        Assert.Contains(
            "Including navigation 'Navigation' is not supported",
            (await Assert.ThrowsAsync<InvalidOperationException>(() =>
                base.Include_collection_OrderBy_list_does_not_contains(async))).Message);

        AssertMql(
        );
    }

    public override async Task Include_reference_dependent_already_tracked(bool async)
    {
        // Fails: Include issue EF-117
        await AssertTranslationFailed(() => base.Include_reference_dependent_already_tracked(async));

        AssertMql(
            """
            Customers.{ "$match" : { "_id" : "ALFKI" } }, { "$limit" : 2 }
            """);
    }

    public override async Task Include_reference_with_filter(bool async)
    {
        // Fails: Include issue EF-117
        await AssertTranslationFailed(() => base.Include_reference_with_filter(async));

        AssertMql(
        );
    }

    public override async Task Include_duplicate_reference(bool async)
    {
        // Fails: Include issue EF-117
        await AssertTranslationFailed(() => base.Include_duplicate_reference(async));

        AssertMql(
        );
    }

    public override async Task Include_with_complex_projection(bool async)
    {
        // Fails: Include issue EF-117
        await AssertTranslationFailed(() => base.Include_with_complex_projection(async));

        AssertMql(
        );
    }

    public override async Task Include_collection_order_by_non_key_with_skip(bool async)
    {
        // Fails: Include issue EF-117
        Assert.Contains(
            "Including navigation 'Navigation' is not supported",
            (await Assert.ThrowsAsync<InvalidOperationException>(() => base.Include_collection_order_by_non_key_with_skip(async)))
            .Message);

        AssertMql(
        );
    }

    public override async Task Include_collection_on_join_clause_with_order_by_and_filter(bool async)
    {
        // Fails: Include issue EF-117
        await AssertTranslationFailed(() => base.Include_collection_on_join_clause_with_order_by_and_filter(async));

        AssertMql(
        );
    }

    public override async Task Multi_level_includes_are_applied_with_take(bool async)
    {
        // Fails: Include issue EF-117
        await AssertTranslationFailed(() => base.Multi_level_includes_are_applied_with_take(async));

        AssertMql(
        );
    }

    public override async Task Include_multiple_references_then_include_collection_multi_level_reverse(bool async)
    {
        // Fails: Include issue EF-117
        await AssertTranslationFailed(() => base.Include_multiple_references_then_include_collection_multi_level_reverse(async));

        AssertMql(
        );
    }

    public override async Task Include_collection_then_reference(bool async)
    {
        // Fails: Include issue EF-117
        Assert.Contains(
            "Including navigation 'Navigation' is not supported",
            (await Assert.ThrowsAsync<InvalidOperationException>(() => base.Include_collection_then_reference(async))).Message);

        AssertMql(
        );
    }

    public override async Task Include_collection_order_by_key(bool async)
    {
        // Fails: Include issue EF-117
        Assert.Contains(
            "Including navigation 'Navigation' is not supported",
            (await Assert.ThrowsAsync<InvalidOperationException>(() => base.Include_collection_order_by_key(async))).Message);

        AssertMql(
        );
    }

    public override async Task Include_collection_with_outer_apply_with_filter(bool async)
    {
        // Fails: Include issue EF-117
        await AssertTranslationFailed(() => base.Include_collection_with_outer_apply_with_filter(async));

        AssertMql(
        );
    }

    public override async Task Include_collection_on_additional_from_clause2(bool async)
    {
        // Fails: Include issue EF-117
        await AssertTranslationFailed(() => base.Include_collection_on_additional_from_clause2(async));

        AssertMql(
        );
    }

    public override async Task Include_collection_dependent_already_tracked(bool async)
    {
        // Fails: Include issue EF-117
        Assert.Contains(
            "Including navigation 'Navigation' is not supported",
            (await Assert.ThrowsAsync<InvalidOperationException>(() => base.Include_collection_dependent_already_tracked(async)))
            .Message);

        AssertMql(
            """
            Orders.{ "$match" : { "CustomerID" : "ALFKI" } }
            """);
    }

    public override async Task Include_with_complex_projection_does_not_change_ordering_of_projection(bool async)
    {
        // Fails: Include issue EF-117
        await AssertTranslationFailed(() => base.Include_with_complex_projection_does_not_change_ordering_of_projection(async));

        AssertMql(
        );
    }

    public override async Task Include_multi_level_collection_and_then_include_reference_predicate(bool async)
    {
        // Fails: Include issue EF-117
        Assert.Contains(
            "Including navigation 'Navigation' is not supported",
            (await Assert.ThrowsAsync<InvalidOperationException>(() =>
                base.Include_multi_level_collection_and_then_include_reference_predicate(async))).Message);

        AssertMql(
        );
    }

    public override async Task Multi_level_includes_are_applied_with_skip_take(bool async)
    {
        // Fails: Include issue EF-117
        await AssertTranslationFailed(() => base.Multi_level_includes_are_applied_with_skip_take(async));

        AssertMql(
        );
    }

    public override async Task Include_collection_OrderBy_empty_list_contains(bool async)
    {
        // Fails: Include issue EF-117
        Assert.Contains(
            "Including navigation 'Navigation' is not supported",
            (await Assert.ThrowsAsync<InvalidOperationException>(() => base.Include_collection_OrderBy_empty_list_contains(async)))
            .Message);

        AssertMql(
        );
    }

    public override async Task Include_references_and_collection_multi_level(bool async)
    {
        // Fails: Include issue EF-117
        await AssertTranslationFailed(() => base.Include_references_and_collection_multi_level(async));

        AssertMql(
        );
    }

    public override async Task Include_collection_force_alias_uniquefication(bool async)
    {
        // Fails: Include issue EF-117
        Assert.Contains(
            "Including navigation 'Navigation' is not supported",
            (await Assert.ThrowsAsync<InvalidOperationException>(() => base.Include_collection_force_alias_uniquefication(async)))
            .Message);

        AssertMql(
        );
    }

    public override async Task Include_collection_with_outer_apply_with_filter_non_equality(bool async)
    {
        // Fails: Include issue EF-117
        await AssertTranslationFailed(() => base.Include_collection_with_outer_apply_with_filter_non_equality(async));

        AssertMql(
        );
    }

    public override async Task Include_in_let_followed_by_FirstOrDefault(bool async)
    {
        // Fails: Include issue EF-117
        await AssertTranslationFailed(() => base.Include_in_let_followed_by_FirstOrDefault(async));

        AssertMql(
        );
    }

    public override async Task Include_references_multi_level(bool async)
    {
        // Fails: Include issue EF-117
        await AssertTranslationFailed(() => base.Include_references_multi_level(async));

        AssertMql(
        );
    }

    public override async Task Include_collection_then_include_collection(bool async)
    {
        // Fails: Include issue EF-117
        Assert.Contains(
            "Including navigation 'Navigation' is not supported",
            (await Assert.ThrowsAsync<InvalidOperationException>(() => base.Include_collection_then_include_collection(async)))
            .Message);

        AssertMql(
        );
    }

    public override async Task Include_collection_with_multiple_conditional_order_by(bool async)
    {
        // Fails: Include issue EF-117
        await AssertTranslationFailed(() => base.Include_collection_with_multiple_conditional_order_by(async));

        AssertMql(
        );
    }

    public override async Task Include_reference_when_entity_in_projection(bool async)
    {
        // Fails: Include issue EF-117
        await AssertTranslationFailed(() => base.Include_reference_when_entity_in_projection(async));

        AssertMql(
        );
    }

    public override async Task Include_reference_single_or_default_when_no_result(bool async)
    {
        // Fails: Include issue EF-117
        await AssertTranslationFailed(() => base.Include_reference_single_or_default_when_no_result(async));

        AssertMql(
        );
    }

    public override async Task Include_reference_alias_generation(bool async)
    {
        // Fails: Include issue EF-117
        await AssertTranslationFailed(() => base.Include_reference_alias_generation(async));

        AssertMql(
        );
    }

    public override async Task Include_with_cycle_does_not_throw_when_AsNoTrackingWithIdentityResolution(bool async)
    {
        // Fails: Include issue EF-117
        await AssertTranslationFailed(() => base.Include_with_cycle_does_not_throw_when_AsNoTrackingWithIdentityResolution(async));

        AssertMql(
        );
    }

    public override async Task Include_references_then_include_collection_multi_level(bool async)
    {
        // Fails: Include issue EF-117
        await AssertTranslationFailed(() => base.Include_references_then_include_collection_multi_level(async));

        AssertMql(
        );
    }

    public override async Task Include_reference_Join_GroupBy_Select(bool async)
    {
        // Fails: Include issue EF-117
        await AssertTranslationFailed(() => base.Include_reference_Join_GroupBy_Select(async));

        AssertMql(
        );
    }

    public override async Task Include_collection_when_projection(bool async)
    {
        await base.Include_collection_when_projection(async);

        AssertMql(
            """
            Customers.{ "$project" : { "_v" : "$_id", "_id" : 0 } }
            """);
    }

    public override async Task Include_reference_SelectMany_GroupBy_Select(bool async)
    {
        // Fails: Include issue EF-117
        await AssertTranslationFailed(() => base.Include_reference_SelectMany_GroupBy_Select(async));

        AssertMql(
        );
    }

    public override async Task Include_multiple_references_then_include_collection_multi_level(bool async)
    {
        // Fails: Include issue EF-117
        await AssertTranslationFailed(() => base.Include_multiple_references_then_include_collection_multi_level(async));

        AssertMql(
        );
    }

    public override async Task Outer_identifier_correctly_determined_when_doing_include_on_right_side_of_left_join(bool async)
    {
        // Fails: Include issue EF-117
        await AssertTranslationFailed(() =>
            base.Outer_identifier_correctly_determined_when_doing_include_on_right_side_of_left_join(async));

        AssertMql(
        );
    }

    public override async Task SelectMany_Include_reference_GroupBy_Select(bool async)
    {
        // Fails: Include issue EF-117
        await AssertTranslationFailed(() => base.SelectMany_Include_reference_GroupBy_Select(async));

        AssertMql(
        );
    }

    public override async Task Include_collection_SelectMany_GroupBy_Select(bool async)
    {
        // Fails: Include issue EF-117
        await AssertTranslationFailed(() => base.Include_collection_SelectMany_GroupBy_Select(async));

        AssertMql(
        );
    }

    public override async Task Include_collection_OrderBy_list_contains(bool async)
    {
        // Fails: Include issue EF-117
        Assert.Contains(
            "Including navigation 'Navigation' is not supported",
            (await Assert.ThrowsAsync<InvalidOperationException>(() => base.Include_collection_OrderBy_list_contains(async)))
            .Message);

        AssertMql(
        );
    }

    public override async Task Multi_level_includes_are_applied_with_skip(bool async)
    {
        // Fails: Include issue EF-117
        await AssertTranslationFailed(() => base.Multi_level_includes_are_applied_with_skip(async));

        AssertMql(
        );
    }

    public override async Task Include_collection_on_additional_from_clause(bool async)
    {
        // Fails: Include issue EF-117
        await AssertTranslationFailed(() => base.Include_collection_on_additional_from_clause(async));

        AssertMql(
        );
    }

    public override async Task Include_reference_distinct_is_server_evaluated(bool async)
    {
        // Fails: Include issue EF-117
        await AssertTranslationFailed(() => base.Include_reference_distinct_is_server_evaluated(async));

        AssertMql(
        );
    }

    public override async Task Include_collection_distinct_is_server_evaluated(bool async)
    {
        // Fails: Include issue EF-117
        Assert.Contains(
            "Including navigation 'Navigation' is not supported",
            (await Assert.ThrowsAsync<InvalidOperationException>(() => base.Include_collection_distinct_is_server_evaluated(async)))
            .Message);

        AssertMql(
        );
    }

    public override async Task Include_reference_when_projection(bool async)
    {
        await base.Include_reference_when_projection(async);

        AssertMql(
            """
            Orders.{ "$project" : { "_v" : "$CustomerID", "_id" : 0 } }
            """);
    }

    public override async Task Include_duplicate_collection_result_operator(bool async)
    {
        // Fails: Include issue EF-117
        await AssertTranslationFailed(() => base.Include_duplicate_collection_result_operator(async));

        AssertMql(
        );
    }

    public override async Task Include_reference(bool async)
    {
        // Fails: Include issue EF-117
        await AssertTranslationFailed(() => base.Include_reference(async));

        AssertMql(
        );
    }

    public override async Task Include_multiple_references_and_collection_multi_level_reverse(bool async)
    {
        // Fails: Include issue EF-117
        await AssertTranslationFailed(() => base.Include_multiple_references_and_collection_multi_level_reverse(async));

        AssertMql(
        );
    }

    public override async Task Include_closes_reader(bool async)
    {
        // Fails: Include issue EF-117
        Assert.Contains(
            "Including navigation 'Navigation' is not supported",
            (await Assert.ThrowsAsync<InvalidOperationException>(() => base.Include_closes_reader(async))).Message);

        AssertMql(
        );
    }

    public override async Task Include_with_skip(bool async)
    {
        // Fails: Include issue EF-117
        Assert.Contains(
            "Including navigation 'Navigation' is not supported",
            (await Assert.ThrowsAsync<InvalidOperationException>(() => base.Include_with_skip(async))).Message);

        AssertMql(
        );
    }

    public override async Task Include_collection_Join_GroupBy_Select(bool async)
    {
        // Fails: Include issue EF-117
        await AssertTranslationFailed(() => base.Include_collection_Join_GroupBy_Select(async));

        AssertMql(
        );
    }

    public override async Task Include_collection_GroupBy_Select(bool async)
    {
        // Fails: Include issue EF-117
        await AssertTranslationFailed(() => base.Include_collection_GroupBy_Select(async));

        AssertMql(
        );
    }

    public override async Task Include_collection_orderby_take(bool async)
    {
        // Fails: Include issue EF-117
        Assert.Contains(
            "Including navigation 'Navigation' is not supported",
            (await Assert.ThrowsAsync<InvalidOperationException>(() => base.Include_collection_orderby_take(async))).Message);
        AssertMql(
        );
    }

    public override async Task Join_Include_collection_GroupBy_Select(bool async)
    {
        // Fails: Include issue EF-117
        await AssertTranslationFailed(() => base.Join_Include_collection_GroupBy_Select(async));

        AssertMql(
        );
    }

    public override async Task Include_collection_order_by_non_key(bool async)
    {
        // Fails: Include issue EF-117
        Assert.Contains(
            "Including navigation 'Navigation' is not supported",
            (await Assert.ThrowsAsync<InvalidOperationException>(() => base.Include_collection_order_by_non_key(async))).Message);

        AssertMql(
        );
    }

    public override async Task Include_when_result_operator(bool async)
    {
        await base.Include_when_result_operator(async);

        AssertMql(
            """
            Customers.{ "$limit" : 1 }, { "$project" : { "_id" : 0, "_v" : null } }
            """);
    }

    public override async Task Include_duplicate_reference2(bool async)
    {
        // Fails: Include issue EF-117
        await AssertTranslationFailed(() => base.Include_duplicate_reference2(async));

        AssertMql(
        );
    }

    public override async Task Include_collection_and_reference(bool async)
    {
        // Fails: Include issue EF-117
        await AssertTranslationFailed(() => base.Include_collection_and_reference(async));

        AssertMql(
        );
    }

    public override async Task Include_multiple_references_multi_level(bool async)
    {
        // Fails: Include issue EF-117
        await AssertTranslationFailed(() => base.Include_multiple_references_multi_level(async));

        AssertMql(
        );
    }

    public override async Task Include_references_and_collection_multi_level_predicate(bool async)
    {
        // Fails: Include issue EF-117
        await AssertTranslationFailed(() => base.Include_references_and_collection_multi_level_predicate(async));

        AssertMql(
        );
    }

    public override async Task SelectMany_Include_collection_GroupBy_Select(bool async)
    {
        // Fails: Include issue EF-117
        await AssertTranslationFailed(() => base.SelectMany_Include_collection_GroupBy_Select(async));

        AssertMql(
        );
    }

    public override async Task Include_collection_with_last(bool async)
    {
        // Fails: Include issue EF-117
        Assert.Contains(
            "Including navigation 'Navigation' is not supported",
            (await Assert.ThrowsAsync<InvalidOperationException>(() => base.Include_collection_with_last(async))).Message);

        AssertMql(
        );
    }

    public override async Task Include_collection_OrderBy_empty_list_does_not_contains(bool async)
    {
        // Fails: Include issue EF-117
        Assert.Contains(
            "Including navigation 'Navigation' is not supported",
            (await Assert.ThrowsAsync<InvalidOperationException>(() =>
                base.Include_collection_OrderBy_empty_list_does_not_contains(async))).Message);

        AssertMql(
        );
    }

    public override async Task Include_multiple_references_then_include_multi_level_reverse(bool async)
    {
        // Fails: Include issue EF-117
        await AssertTranslationFailed(() => base.Include_multiple_references_then_include_multi_level_reverse(async));

        AssertMql(
        );
    }

    public override async Task Include_reference_and_collection(bool async)
    {
        // Fails: Include issue EF-117
        await AssertTranslationFailed(() => base.Include_reference_and_collection(async));

        AssertMql(
        );
    }

    public override async Task Include_is_not_ignored_when_projection_contains_client_method_and_complex_expression(bool async)
    {
        // Fails: Include issue EF-117
        await AssertTranslationFailed(() =>
            base.Include_is_not_ignored_when_projection_contains_client_method_and_complex_expression(async));

        AssertMql(
        );
    }

    public override async Task Include_reference_with_filter_reordered(bool async)
    {
        // Fails: Include issue EF-117
        await AssertTranslationFailed(() => base.Include_reference_with_filter_reordered(async));

        AssertMql(
        );
    }

    public override async Task Include_collection_order_by_subquery(bool async)
    {
        // Fails: Include issue EF-117
        Assert.Contains(
            "Including navigation 'Navigation' is not supported",
            (await Assert.ThrowsAsync<InvalidOperationException>(() => base.Include_collection_order_by_subquery(async))).Message);

        AssertMql(
        );
    }

    public override async Task Include_reference_and_collection_order_by(bool async)
    {
        // Fails: Include issue EF-117
        await AssertTranslationFailed(() => base.Include_reference_and_collection_order_by(async));

        AssertMql(
        );
    }

    public override async Task Then_include_collection_order_by_collection_column(bool async)
    {
        // Fails: Include issue EF-117
        Assert.Contains(
            "Including navigation 'Navigation' is not supported",
            (await Assert.ThrowsAsync<InvalidOperationException>(() =>
                base.Then_include_collection_order_by_collection_column(async))).Message);

        AssertMql(
        );
    }

    public override async Task Include_multiple_references_then_include_multi_level(bool async)
    {
        // Fails: Include issue EF-117
        await AssertTranslationFailed(() => base.Include_multiple_references_then_include_multi_level(async));

        AssertMql(
        );
    }

    public override async Task Include_collection_skip_no_order_by(bool async)
    {
        // Fails: Include issue EF-117
        Assert.Contains(
            "Including navigation 'Navigation' is not supported",
            (await Assert.ThrowsAsync<InvalidOperationException>(() => base.Include_collection_skip_no_order_by(async))).Message);

        AssertMql(
        );
    }

    public override async Task Include_multi_level_reference_then_include_collection_predicate(bool async)
    {
        // Fails: Include issue EF-117
        await AssertTranslationFailed(() => base.Include_multi_level_reference_then_include_collection_predicate(async));

        AssertMql(
        );
    }

    public override async Task Include_multiple_references_and_collection_multi_level(bool async)
    {
        // Fails: Include issue EF-117
        await AssertTranslationFailed(() => base.Include_multiple_references_and_collection_multi_level(async));

        AssertMql(
        );
    }

    public override async Task Include_where_skip_take_projection(bool async)
    {
        // Fails: Include issue EF-117
        await AssertTranslationFailed(() => base.Include_where_skip_take_projection(async));

        AssertMql(
        );
    }

    public override async Task Include_with_take(bool async)
    {
        // Fails: Include issue EF-117
        Assert.Contains(
            "Including navigation 'Navigation' is not supported",
            (await Assert.ThrowsAsync<InvalidOperationException>(() => base.Include_with_take(async))).Message);

        AssertMql(
        );
    }

    public override async Task Include_multiple_references(bool async)
    {
        // Fails: Include issue EF-117
        await AssertTranslationFailed(() => base.Include_multiple_references(async));

        AssertMql(
        );
    }

    public override async Task Include_list(bool async)
    {
        // Fails: Include issue EF-117
        Assert.Contains(
            "Including navigation 'Navigation' is not supported",
            (await Assert.ThrowsAsync<InvalidOperationException>(() => base.Include_list(async))).Message);

        AssertMql(
        );
    }

    public override async Task Include_empty_reference_sets_IsLoaded(bool async)
    {
        // Fails: Include issue EF-117
        await AssertTranslationFailed(() => base.Include_empty_reference_sets_IsLoaded(async));

        AssertMql(
        );
    }

    public override async Task Include_references_then_include_collection_multi_level_predicate(bool async)
    {
        // Fails: Include issue EF-117
        await AssertTranslationFailed(() => base.Include_references_then_include_collection_multi_level_predicate(async));

        AssertMql(
        );
    }

    public override async Task Include_collection_with_conditional_order_by(bool async)
    {
        // Fails: Include issue EF-117
        Assert.Contains(
            "Including navigation 'Navigation' is not supported",
            (await Assert.ThrowsAsync<InvalidOperationException>(() => base.Include_collection_with_conditional_order_by(async)))
            .Message);

        AssertMql(
        );
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
        // Fails: Include issue EF-117
        Assert.Contains(
            "Including navigation 'Navigation' is not supported",
            (await Assert.ThrowsAsync<InvalidOperationException>(() => base.Filtered_include_with_multiple_ordering(async)))
            .Message);

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
