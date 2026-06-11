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

// ExecuteUpdate / ExecuteDelete bulk operations are EF9+ only.
#if !EF8

using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.BulkUpdates;
using Microsoft.EntityFrameworkCore.TestUtilities;
using Xunit;
using Xunit.Abstractions;

namespace MongoDB.EntityFrameworkCore.SpecificationTests.Query;

// Conformance coverage for EF Core's bulk-update spec suite, scoped to the subset the MongoDB
// provider supports: a single collection, scoped by Where, with constant / parameter /
// self-referencing scalar setters (see README "What is supported" and EF-107).
//
// Supported cases call base, which asserts the affected-document count and the before/after
// document state. Bulk operations emit Executing/ExecutedBulkUpdate|Delete diagnostics events
// rather than ExecutedMqlQuery, so there is no MQL baseline to pin via AssertMql here (unlike the
// Northwind *query* suites). Ordering/paging/Distinct-scoped bulk operations are supported via the
// two-phase (_id-projection) execution path and call base. Per the suite convention (see
// docs/failing-spec-tests.md) the remaining out-of-subset cases (joins, set operations, GroupBy,
// SelectMany, navigations, non-entity projections, multiple-collection updates) are not skipped:
// they run and assert the current failure mode (translation failure, cross-DbSet rejection, or a
// non-translation exception), tagged with a // Fails: <reason> <ticket> comment. Behavioral
// coverage for the supported subset also lives in FunctionalTests/Query/ExecuteUpdateTests.cs and
// ExecuteDeleteTests.cs.
public class NorthwindBulkUpdatesMongoTest : NorthwindBulkUpdatesTestBase<NorthwindBulkUpdatesMongoFixture<NoopModelCustomizer>>
{
    public NorthwindBulkUpdatesMongoTest(
        NorthwindBulkUpdatesMongoFixture<NoopModelCustomizer> fixture,
        ITestOutputHelper testOutputHelper)
        : base(fixture)
    {
    }

    [ConditionalFact]
    public virtual void Check_all_tests_overridden()
        => TestHelpers.AssertAllMethodsOverridden(GetType());

    // ---- Supported: single-collection, Where-scoped scalar setters ----
    public override Task Delete_Where(bool async)
        => base.Delete_Where(async);

    public override Task Delete_Where_TagWith(bool async)
        => base.Delete_Where_TagWith(async);

    public override Task Delete_Where_parameter(bool async)
        => base.Delete_Where_parameter(async);

    public override Task Update_Where_multiple_set(bool async)
        => base.Update_Where_multiple_set(async);

    public override Task Update_Where_parameter_set_constant(bool async)
        => base.Update_Where_parameter_set_constant(async);

    public override Task Update_Where_set_constant(bool async)
        => base.Update_Where_set_constant(async);

    public override Task Update_Where_set_constant_TagWith(bool async)
        => base.Update_Where_set_constant_TagWith(async);

#if EF10
    public override Task Update_Where_set_constant_via_lambda(bool async)
        => base.Update_Where_set_constant_via_lambda(async);
#endif

    public override Task Update_Where_set_null(bool async)
        => base.Update_Where_set_null(async);

    public override Task Update_Where_set_parameter(bool async)
        => base.Update_Where_set_parameter(async);

    public override Task Update_Where_set_parameter_from_closure_array(bool async)
        => base.Update_Where_set_parameter_from_closure_array(async);

    public override Task Update_Where_set_parameter_from_inline_list(bool async)
        => base.Update_Where_set_parameter_from_inline_list(async);

    public override Task Update_Where_set_parameter_from_multilevel_property_access(bool async)
        => base.Update_Where_set_parameter_from_multilevel_property_access(async);

    public override Task Update_Where_set_property_plus_constant(bool async)
        => base.Update_Where_set_property_plus_constant(async);

    public override Task Update_Where_set_property_plus_parameter(bool async)
        => base.Update_Where_set_property_plus_parameter(async);

    public override Task Update_Where_set_property_plus_property(bool async)
        => base.Update_Where_set_property_plus_property(async);

    // ---- Out-of-subset shapes: run and assert the current failure mode (see // Fails: tags) ----

    // Fails: ExecuteUpdate/ExecuteDelete source restricted to a Where predicate; set operations unsupported EF-X016
    public override Task Delete_Concat(bool async)
        => AssertTranslationFailed(() => base.Delete_Concat(async));

    // Fails: ExecuteUpdate/ExecuteDelete source restricted to a Where predicate; set operations unsupported EF-X016
    public override Task Delete_Except(bool async)
        => AssertTranslationFailed(() => base.Delete_Except(async));

    // Fails: ExecuteUpdate/ExecuteDelete source restricted to a Where predicate; GroupBy unsupported EF-X016
    // Re-declares [ConditionalTheory] (without skip) to run past the upstream EF skip ("Issue#28525") so
    // the provider's rejection is still asserted (the upstream result-correctness bug never applies — we
    // throw first). The inherited [MemberData] still supplies the async data; re-declaring it here would
    // duplicate the test-case IDs, so xUnit1003 (which only sees method-level data) is suppressed.
#pragma warning disable xUnit1003
    [ConditionalTheory]
    public override Task Delete_GroupBy_Where_Select(bool async)
        => AssertTranslationFailed(() => base.Delete_GroupBy_Where_Select(async));
#pragma warning restore xUnit1003

    // Fails: ExecuteUpdate/ExecuteDelete source restricted to a Where predicate; GroupBy unsupported EF-X016
    // Re-declares [ConditionalTheory] (without skip) to run past the upstream EF skip ("Issue#26753");
    // the inherited [MemberData] supplies the async data, so xUnit1003 is suppressed (see above).
#pragma warning disable xUnit1003
    [ConditionalTheory]
    public override Task Delete_GroupBy_Where_Select_2(bool async)
        => AssertTranslationFailed(() => base.Delete_GroupBy_Where_Select_2(async));
#pragma warning restore xUnit1003

    // Fails: ExecuteUpdate/ExecuteDelete source restricted to a Where predicate; set operations unsupported EF-X016
    public override Task Delete_Intersect(bool async)
        => AssertTranslationFailed(() => base.Delete_Intersect(async));

    // Fails: ExecuteUpdate/ExecuteDelete source restricted to a Where predicate; SelectMany unsupported EF-X016
    public override Task Delete_SelectMany(bool async)
        => AssertTranslationFailed(() => base.Delete_SelectMany(async));

    // Fails: ExecuteUpdate/ExecuteDelete source restricted to a Where predicate; SelectMany unsupported EF-X016
    public override Task Delete_SelectMany_subquery(bool async)
        => AssertTranslationFailed(() => base.Delete_SelectMany_subquery(async));

    // Fails: ExecuteUpdate/ExecuteDelete source restricted to a Where predicate; set operations unsupported EF-X016
    public override Task Delete_Union(bool async)
        => AssertTranslationFailed(() => base.Delete_Union(async));

    public override Task Delete_Where_Distinct(bool async)
        => base.Delete_Where_Distinct(async);

    public override Task Delete_Where_OrderBy(bool async)
        => base.Delete_Where_OrderBy(async);

    public override Task Delete_Where_OrderBy_Skip(bool async)
        => base.Delete_Where_OrderBy_Skip(async);

    public override Task Delete_Where_OrderBy_Skip_Take(bool async)
        => base.Delete_Where_OrderBy_Skip_Take(async);

    public override Task Delete_Where_OrderBy_Take(bool async)
        => base.Delete_Where_OrderBy_Take(async);

    public override Task Delete_Where_Skip(bool async)
        => base.Delete_Where_Skip(async);

    public override Task Delete_Where_Skip_Take(bool async)
        => base.Delete_Where_Skip_Take(async);

    public override Task Delete_Where_Skip_Take_Skip_Take_causing_subquery(bool async)
        => base.Delete_Where_Skip_Take_Skip_Take_causing_subquery(async);

    public override Task Delete_Where_Take(bool async)
        => base.Delete_Where_Take(async);

    // Fails: ExecuteUpdate/ExecuteDelete source restricted to a Where predicate; cross-document navigation predicates unsupported EF-X016
    public override Task Delete_Where_optional_navigation_predicate(bool async)
        => AssertTranslationFailed(() => base.Delete_Where_optional_navigation_predicate(async));

    // Fails: ExecuteUpdate/ExecuteDelete source restricted to a Where predicate; GroupBy unsupported EF-X016
    public override Task Delete_Where_predicate_with_GroupBy_aggregate(bool async)
        => AssertTranslationFailed(() => base.Delete_Where_predicate_with_GroupBy_aggregate(async));

    // Fails: ExecuteUpdate/ExecuteDelete source restricted to a Where predicate; GroupBy unsupported EF-X016
    public override Task Delete_Where_predicate_with_GroupBy_aggregate_2(bool async)
        => AssertTranslationFailed(() => base.Delete_Where_predicate_with_GroupBy_aggregate_2(async));

    // Fails: ExecuteUpdate/ExecuteDelete source restricted to a Where predicate; cross-document navigation predicates unsupported EF-X016
    public override Task Delete_Where_using_navigation(bool async)
        => AssertTranslationFailed(() => base.Delete_Where_using_navigation(async));

    // Fails: ExecuteUpdate/ExecuteDelete source restricted to a Where predicate; cross-document navigation predicates unsupported EF-X016
    public override Task Delete_Where_using_navigation_2(bool async)
        => AssertTranslationFailed(() => base.Delete_Where_using_navigation_2(async));

    // Fails: ExecuteUpdate/ExecuteDelete source restricted to a Where predicate; non-entity projection sources unsupported EF-X016
    public override Task Delete_non_entity_projection(bool async)
        => AssertTranslationFailed(() => base.Delete_non_entity_projection(async));

    // Fails: ExecuteUpdate/ExecuteDelete source restricted to a Where predicate; non-entity projection sources unsupported EF-X016
    public override Task Delete_non_entity_projection_2(bool async)
        => AssertTranslationFailed(() => base.Delete_non_entity_projection_2(async));

    // Fails: ExecuteUpdate/ExecuteDelete source restricted to a Where predicate; non-entity projection sources unsupported EF-X016
    public override Task Delete_non_entity_projection_3(bool async)
        => AssertTranslationFailed(() => base.Delete_non_entity_projection_3(async));

#if EF10
    // Fails: ExecuteUpdate/ExecuteDelete source restricted to a Where predicate; joins / correlated subqueries unsupported EF-X016
    public override Task Delete_with_LeftJoin(bool async)
        => AssertTranslationFailed(() => base.Delete_with_LeftJoin(async));
#endif

#if EF10
    // Fails: ExecuteUpdate/ExecuteDelete source restricted to a Where predicate; joins / correlated subqueries unsupported EF-X016
    public override Task Delete_with_LeftJoin_via_flattened_GroupJoin(bool async)
        => AssertTranslationFailed(() => base.Delete_with_LeftJoin_via_flattened_GroupJoin(async));
#endif

#if EF10
    // Fails: ExecuteUpdate/ExecuteDelete source restricted to a Where predicate; joins / correlated subqueries unsupported EF-X016
    public override Task Delete_with_RightJoin(bool async)
        => AssertTranslationFailed(() => base.Delete_with_RightJoin(async));
#endif

    // Fails: ExecuteUpdate/ExecuteDelete source restricted to a Where predicate; joins / correlated subqueries unsupported EF-X016
    public override Task Delete_with_cross_apply(bool async)
        => AssertTranslationFailed(() => base.Delete_with_cross_apply(async));

    // Fails: ExecuteUpdate/ExecuteDelete source restricted to a Where predicate; joins / correlated subqueries unsupported EF-X016
    public override Task Delete_with_cross_join(bool async)
        => AssertTranslationFailed(() => base.Delete_with_cross_join(async));

    // Fails: ExecuteUpdate/ExecuteDelete source restricted to a Where predicate; joins / correlated subqueries unsupported EF-X016
    public override Task Delete_with_join(bool async)
        => AssertTranslationFailed(() => base.Delete_with_join(async));

#if EF9
    // Fails: ExecuteUpdate/ExecuteDelete source restricted to a Where predicate; joins / correlated subqueries unsupported EF-X016
    public override Task Delete_with_left_join(bool async)
        => AssertTranslationFailed(() => base.Delete_with_left_join(async));
#endif

    // Fails: ExecuteUpdate/ExecuteDelete source restricted to a Where predicate; joins / correlated subqueries unsupported EF-X016
    public override Task Delete_with_outer_apply(bool async)
        => AssertTranslationFailed(() => base.Delete_with_outer_apply(async));

    // Fails: Throws a non-translation exception, but still throws EF-X002
    public override Task Update_Concat_set_constant(bool async)
        => Assert.ThrowsAnyAsync<Exception>(() => base.Update_Concat_set_constant(async));

    // Fails: ExecuteUpdate/ExecuteDelete source restricted to a Where predicate; set operations unsupported EF-X016
    public override Task Update_Except_set_constant(bool async)
        => AssertTranslationFailed(() => base.Update_Except_set_constant(async));

    // Fails: ExecuteUpdate/ExecuteDelete source restricted to a Where predicate; set operations unsupported EF-X016
    public override Task Update_Intersect_set_constant(bool async)
        => AssertTranslationFailed(() => base.Update_Intersect_set_constant(async));

    // Fails: Throws a non-translation exception, but still throws EF-X002
    public override Task Update_Union_set_constant(bool async)
        => Assert.ThrowsAnyAsync<Exception>(() => base.Update_Union_set_constant(async));

    public override Task Update_Where_Distinct_set_constant(bool async)
        => base.Update_Where_Distinct_set_constant(async);

    public override Task Update_Where_GroupBy_First_set_constant(bool async)
        => AssertNoMultiCollectionQuerySupport(() => base.Update_Where_GroupBy_First_set_constant(async));

    // Re-declares [ConditionalTheory] (without skip) to run past the upstream EF skip ("Issue#26753");
    // the inherited [MemberData] supplies the async data, so xUnit1003 is suppressed (see above).
#pragma warning disable xUnit1003
    [ConditionalTheory]
    public override Task Update_Where_GroupBy_First_set_constant_2(bool async)
        => AssertNoMultiCollectionQuerySupport(() => base.Update_Where_GroupBy_First_set_constant_2(async));
#pragma warning restore xUnit1003

    public override Task Update_Where_GroupBy_First_set_constant_3(bool async)
        => AssertNoMultiCollectionQuerySupport(() => base.Update_Where_GroupBy_First_set_constant_3(async));

    public override Task Update_Where_GroupBy_aggregate_set_constant(bool async)
        => AssertNoMultiCollectionQuerySupport(() => base.Update_Where_GroupBy_aggregate_set_constant(async));

    // Fails: ExecuteUpdate/ExecuteDelete source restricted to a Where predicate; joins / correlated subqueries unsupported EF-X016
    public override Task Update_Where_Join_set_property_from_joined_single_result_scalar(bool async)
        => AssertTranslationFailed(() => base.Update_Where_Join_set_property_from_joined_single_result_scalar(async));

    // Fails: ExecuteUpdate/ExecuteDelete source restricted to a Where predicate; joins / correlated subqueries unsupported EF-X016
    public override Task Update_Where_Join_set_property_from_joined_single_result_table(bool async)
        => AssertTranslationFailed(() => base.Update_Where_Join_set_property_from_joined_single_result_table(async));

    // Fails: ExecuteUpdate/ExecuteDelete source restricted to a Where predicate; joins / correlated subqueries unsupported EF-X016
    public override Task Update_Where_Join_set_property_from_joined_table(bool async)
        => AssertTranslationFailed(() => base.Update_Where_Join_set_property_from_joined_table(async));

    public override Task Update_Where_OrderBy_Skip_Take_Skip_Take_set_constant(bool async)
        => base.Update_Where_OrderBy_Skip_Take_Skip_Take_set_constant(async);

    public override Task Update_Where_OrderBy_Skip_Take_set_constant(bool async)
        => base.Update_Where_OrderBy_Skip_Take_set_constant(async);

    public override Task Update_Where_OrderBy_Skip_set_constant(bool async)
        => base.Update_Where_OrderBy_Skip_set_constant(async);

    public override Task Update_Where_OrderBy_Take_set_constant(bool async)
        => base.Update_Where_OrderBy_Take_set_constant(async);

    public override Task Update_Where_OrderBy_set_constant(bool async)
        => base.Update_Where_OrderBy_set_constant(async);

    // Fails: ExecuteUpdate/ExecuteDelete source restricted to a Where predicate; SelectMany unsupported EF-X016
    public override Task Update_Where_SelectMany_set_null(bool async)
        => AssertTranslationFailed(() => base.Update_Where_SelectMany_set_null(async));

    // Fails: ExecuteUpdate/ExecuteDelete source restricted to a Where predicate; SelectMany unsupported EF-X016
    public override Task Update_Where_SelectMany_subquery_set_null(bool async)
        => AssertTranslationFailed(() => base.Update_Where_SelectMany_subquery_set_null(async));

    public override Task Update_Where_Skip_Take_set_constant(bool async)
        => base.Update_Where_Skip_Take_set_constant(async);

    public override Task Update_Where_Skip_set_constant(bool async)
        => base.Update_Where_Skip_set_constant(async);

    public override Task Update_Where_Take_set_constant(bool async)
        => base.Update_Where_Take_set_constant(async);

    public override Task Update_Where_set_constant_using_ef_property(bool async)
        => base.Update_Where_set_constant_using_ef_property(async);

#if EF10
    public override Task Update_Where_set_nullable_int_constant_via_discard_lambda(bool async)
        => base.Update_Where_set_nullable_int_constant_via_discard_lambda(async);
#endif

    // Fails: ExecuteUpdate/ExecuteDelete source restricted to a Where predicate; cross-document navigation predicates unsupported EF-X016
    public override Task Update_Where_using_navigation_2_set_constant(bool async)
        => AssertTranslationFailed(() => base.Update_Where_using_navigation_2_set_constant(async));

    // Fails: ExecuteUpdate/ExecuteDelete source restricted to a Where predicate; cross-document navigation predicates unsupported EF-X016
    public override Task Update_Where_using_navigation_set_null(bool async)
        => AssertTranslationFailed(() => base.Update_Where_using_navigation_set_null(async));

    // Fails: ExecuteUpdate/ExecuteDelete source restricted to a Where predicate; multiple-collection updates unsupported EF-X016
    public override Task Update_multiple_tables_throws(bool async)
        => AssertTranslationFailed(() => base.Update_multiple_tables_throws(async));

    public override Task Update_unmapped_property_throws(bool async)
        => AssertTranslationFailed(() => base.Update_unmapped_property_throws(async));

#if EF10
    // Fails: ExecuteUpdate/ExecuteDelete source restricted to a Where predicate; joins / correlated subqueries unsupported EF-X016
    public override Task Update_with_LeftJoin(bool async)
        => AssertTranslationFailed(() => base.Update_with_LeftJoin(async));
#endif

#if EF10
    // Fails: ExecuteUpdate/ExecuteDelete source restricted to a Where predicate; joins / correlated subqueries unsupported EF-X016
    public override Task Update_with_LeftJoin_via_flattened_GroupJoin(bool async)
        => AssertTranslationFailed(() => base.Update_with_LeftJoin_via_flattened_GroupJoin(async));
#endif

#if EF10
    // Fails: ExecuteUpdate/ExecuteDelete source restricted to a Where predicate; joins / correlated subqueries unsupported EF-X016
    public override Task Update_with_PK_pushdown_and_join_and_multiple_setters(bool async)
        => AssertTranslationFailed(() => base.Update_with_PK_pushdown_and_join_and_multiple_setters(async));
#endif

#if EF10
    // Fails: ExecuteUpdate/ExecuteDelete source restricted to a Where predicate; joins / correlated subqueries unsupported EF-X016
    public override Task Update_with_RightJoin(bool async)
        => AssertTranslationFailed(() => base.Update_with_RightJoin(async));
#endif

    // Fails: ExecuteUpdate/ExecuteDelete source restricted to a Where predicate; joins / correlated subqueries unsupported EF-X016
    public override Task Update_with_cross_apply_set_constant(bool async)
        => AssertTranslationFailed(() => base.Update_with_cross_apply_set_constant(async));

    // Fails: ExecuteUpdate/ExecuteDelete source restricted to a Where predicate; joins / correlated subqueries unsupported EF-X016
    public override Task Update_with_cross_join_cross_apply_set_constant(bool async)
        => AssertTranslationFailed(() => base.Update_with_cross_join_cross_apply_set_constant(async));

    // Fails: ExecuteUpdate/ExecuteDelete source restricted to a Where predicate; joins / correlated subqueries unsupported EF-X016
    public override Task Update_with_cross_join_left_join_set_constant(bool async)
        => AssertTranslationFailed(() => base.Update_with_cross_join_left_join_set_constant(async));

    // Fails: ExecuteUpdate/ExecuteDelete source restricted to a Where predicate; joins / correlated subqueries unsupported EF-X016
    public override Task Update_with_cross_join_outer_apply_set_constant(bool async)
        => AssertTranslationFailed(() => base.Update_with_cross_join_outer_apply_set_constant(async));

    // Fails: ExecuteUpdate/ExecuteDelete source restricted to a Where predicate; joins / correlated subqueries unsupported EF-X016
    public override Task Update_with_cross_join_set_constant(bool async)
        => AssertTranslationFailed(() => base.Update_with_cross_join_set_constant(async));

    public override Task Update_with_invalid_lambda_in_set_property_throws(bool async)
        => AssertTranslationFailed(() => base.Update_with_invalid_lambda_in_set_property_throws(async));

#if EF9
    public override Task Update_with_invalid_lambda_throws(bool async)
        => AssertTranslationFailed(() => base.Update_with_invalid_lambda_throws(async));
#endif

    // Fails: ExecuteUpdate/ExecuteDelete source restricted to a Where predicate; joins / correlated subqueries unsupported EF-X016
    public override Task Update_with_join_set_constant(bool async)
        => AssertTranslationFailed(() => base.Update_with_join_set_constant(async));

#if EF9
    // Fails: ExecuteUpdate/ExecuteDelete source restricted to a Where predicate; joins / correlated subqueries unsupported EF-X016
    public override Task Update_with_left_join_set_constant(bool async)
        => AssertTranslationFailed(() => base.Update_with_left_join_set_constant(async));
#endif

    // Fails: ExecuteUpdate/ExecuteDelete source restricted to a Where predicate; joins / correlated subqueries unsupported EF-X016
    public override Task Update_with_outer_apply_set_constant(bool async)
        => AssertTranslationFailed(() => base.Update_with_outer_apply_set_constant(async));

    // Fails: ExecuteUpdate/ExecuteDelete source restricted to a Where predicate; joins / correlated subqueries unsupported EF-X016
    public override Task Update_with_two_inner_joins(bool async)
        => AssertTranslationFailed(() => base.Update_with_two_inner_joins(async));

    public override Task Update_without_property_to_set_throws(bool async)
        => AssertTranslationFailed(() => base.Update_without_property_to_set_throws(async));

    // Out-of-subset bulk shapes (and the base *_throws cases) are rejected during translation: the
    // MongoDB provider surfaces them as an InvalidOperationException whose message reports the LINQ
    // expression could not be translated.
    private static async Task AssertTranslationFailed(Func<Task> query)
        => Assert.Contains(
            "could not be translated",
            (await Assert.ThrowsAsync<InvalidOperationException>(query)).Message);

    // Fails: Cross-document navigation access issue EF-216
    private static async Task AssertNoMultiCollectionQuerySupport(Func<Task> query)
        => Assert.Contains(
            "Unsupported cross-DbSet query between",
            (await Assert.ThrowsAsync<InvalidOperationException>(query)).Message);
}

#endif
