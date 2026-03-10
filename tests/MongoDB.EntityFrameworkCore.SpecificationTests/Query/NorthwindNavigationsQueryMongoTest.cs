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

using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.TestModels.Northwind;
using Microsoft.EntityFrameworkCore.TestUtilities;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace MongoDB.EntityFrameworkCore.SpecificationTests.Query;

public class NorthwindNavigationsQueryMongoTest : NorthwindNavigationsQueryTestBase<NorthwindQueryMongoFixture<NoopModelCustomizer>>
{
    public NorthwindNavigationsQueryMongoTest(
        NorthwindQueryMongoFixture<NoopModelCustomizer> fixture,
        ITestOutputHelper testOutputHelper)
        : base(fixture)
    {
        fixture.TestMqlLoggerFactory.Clear();
        //fixture.TestMqlLoggerFactory.SetTestOutputHelper(testOutputHelper);
    }

    [ConditionalFact]
    public virtual void Check_all_tests_overridden()
        => TestHelpers.AssertAllMethodsOverridden(GetType());

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Select_Where_Navigation(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Select_Where_Navigation_Contains(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Select_Where_Navigation_Deep(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Take_Select_Navigation(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Select_collection_FirstOrDefault_project_single_column1(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Select_collection_FirstOrDefault_project_single_column2(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Select_collection_FirstOrDefault_project_anonymous_type(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Select_collection_FirstOrDefault_project_anonymous_type_client_eval(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Select_collection_FirstOrDefault_project_entity(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Skip_Select_Navigation(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Select_Where_Navigation_Included(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Include_with_multiple_optional_navigations(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Select_Navigation(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Select_Navigations(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Select_Where_Navigation_Multiple_Access(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Select_Navigations_Where_Navigations(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Select_Singleton_Navigation_With_Member_Access(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Select_count_plus_sum(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Singleton_Navigation_With_Member_Access(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Select_Where_Navigation_Scalar_Equals_Navigation_Scalar_Projected(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Select_Where_Navigation_Equals_Navigation(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Select_Where_Navigation_Null(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Select_Where_Navigation_Null_Deep(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Select_Where_Navigation_Null_Reverse(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Select_collection_navigation_simple(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Select_collection_navigation_simple2(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Select_collection_navigation_simple_followed_by_ordering_by_scalar(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Select_collection_navigation_multi_part(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Select_collection_navigation_multi_part2(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Collection_select_nav_prop_any(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Collection_select_nav_prop_predicate(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "No multi-collection query support"), MemberData(nameof(IsAsyncData))]
    public override Task Collection_where_nav_prop_any(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "No multi-collection query support"), MemberData(nameof(IsAsyncData))]
    public override Task Collection_where_nav_prop_any_predicate(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Collection_select_nav_prop_all(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "No multi-collection query support"), MemberData(nameof(IsAsyncData))]
    public override Task Collection_where_nav_prop_all(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Collection_select_nav_prop_count(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "No multi-collection query support"), MemberData(nameof(IsAsyncData))]
    public override Task Collection_where_nav_prop_count(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "No multi-collection query support"), MemberData(nameof(IsAsyncData))]
    public override Task Collection_where_nav_prop_count_reverse(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "No multi-collection query support"), MemberData(nameof(IsAsyncData))]
    public override Task Collection_orderby_nav_prop_count(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Collection_select_nav_prop_long_count(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Select_multiple_complex_projections(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Collection_select_nav_prop_sum(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Collection_select_nav_prop_sum_plus_one(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "No multi-collection query support"), MemberData(nameof(IsAsyncData))]
    public override Task Collection_where_nav_prop_sum(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Collection_select_nav_prop_first_or_default(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Collection_select_nav_prop_first_or_default_then_nav_prop(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Collection_select_nav_prop_first_or_default_then_nav_prop_nested(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Collection_select_nav_prop_single_or_default_then_nav_prop_nested(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Collection_select_nav_prop_first_or_default_then_nav_prop_nested_using_property_method(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Collection_select_nav_prop_first_or_default_then_nav_prop_nested_with_orderby(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Navigation_fk_based_inside_contains(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Navigation_inside_contains(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Navigation_inside_contains_nested(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Navigation_from_join_clause_inside_contains(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "No multi-collection query support"), MemberData(nameof(IsAsyncData))]
    public override Task Where_subquery_on_navigation(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "No multi-collection query support"), MemberData(nameof(IsAsyncData))]
    public override Task Where_subquery_on_navigation2(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Navigation_in_subquery_referencing_outer_query(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Project_single_scalar_value_subquery_is_properly_inlined(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Project_single_entity_value_subquery_works(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Project_single_scalar_value_subquery_in_query_with_optional_navigation_works(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task GroupJoin_with_complex_subquery_and_LOJ_gets_flattened(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task GroupJoin_with_complex_subquery_and_LOJ_gets_flattened2(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Navigation_with_collection_with_nullable_type_key(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Multiple_include_with_multiple_optional_navigations(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Navigation_in_subquery_referencing_outer_query_with_client_side_result_operator_and_count(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Select_Where_Navigation_Scalar_Equals_Navigation_Scalar(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "No multi-collection query support"), MemberData(nameof(IsAsyncData))]
    public override Task Where_subquery_on_navigation_client_eval(bool _)
        => Task.CompletedTask;

    public override async Task Join_with_nav_projected_in_subquery_when_client_eval(bool async)
    {
        await base.Join_with_nav_projected_in_subquery_when_client_eval(async);

        AssertMql();
    }

    public override async Task Join_with_nav_in_predicate_in_subquery_when_client_eval(bool async)
    {
        await base.Join_with_nav_in_predicate_in_subquery_when_client_eval(async);

        AssertMql();
    }

    public override async Task Join_with_nav_in_orderby_in_subquery_when_client_eval(bool async)
    {
        await base.Join_with_nav_in_orderby_in_subquery_when_client_eval(async);

        AssertMql();
    }

    [ConditionalTheory(Skip = "Expression not supported"), MemberData(nameof(IsAsyncData))]
    public override Task Select_Where_Navigation_Client(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Expression not supported"), MemberData(nameof(IsAsyncData))]
    public override Task Collection_select_nav_prop_all_client(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "No multi-collection query support"), MemberData(nameof(IsAsyncData))]
    public override Task Collection_where_nav_prop_all_client(bool _)
        => Task.CompletedTask;

    private void AssertMql(params string[] expected)
        => Fixture.TestMqlLoggerFactory.AssertBaseline(expected);

    protected override void ClearLog()
        => Fixture.TestMqlLoggerFactory.Clear();
}
