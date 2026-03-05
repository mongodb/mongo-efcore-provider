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
using Microsoft.EntityFrameworkCore.TestModels.Northwind;
using Microsoft.EntityFrameworkCore.TestUtilities;
using MongoDB.Driver;
using MongoDB.Driver.Core.Misc;
using MongoDB.Driver.Linq;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace MongoDB.EntityFrameworkCore.SpecificationTests.Query;

public class NorthwindSelectQueryMongoTest : NorthwindSelectQueryTestBase<NorthwindQueryMongoFixture<NoopModelCustomizer>>
{
    public NorthwindSelectQueryMongoTest(
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

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task SelectMany_with_nested_DefaultIfEmpty(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task SelectMany_with_multiple_Take(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Project_single_element_from_collection_with_OrderBy_Take_OrderBy_and_FirstOrDefault(bool _)
        => Task.CompletedTask;

    public override async Task Select_conditional_drops_false(bool async)
    {
        await base.Select_conditional_drops_false(async);

        AssertMql(
            """
            Orders.{ "$project" : { "_v" : { "$cond" : { "if" : { "$eq" : [{ "$mod" : ["$_id", 2] }, 0] }, "then" : "$_id", "else" : { "$subtract" : [0, "$_id"] } } }, "_id" : 0 } }
            """);
    }

    public override async Task Select_conditional_terminates_at_true(bool async)
    {
        await base.Select_conditional_terminates_at_true(async);

        AssertMql(
            """
            Orders.{ "$project" : { "_v" : { "$cond" : { "if" : { "$eq" : [{ "$mod" : ["$_id", 2] }, 0] }, "then" : "$_id", "else" : 0 } }, "_id" : 0 } }
            """);
    }

    public override async Task Select_conditional_flatten_nested_results(bool async)
    {
        await base.Select_conditional_flatten_nested_results(async);

        AssertMql(
            """
            Orders.{ "$project" : { "_v" : { "$cond" : { "if" : { "$eq" : [{ "$mod" : ["$_id", 2] }, 0] }, "then" : { "$cond" : { "if" : { "$eq" : [{ "$mod" : ["$_id", 5] }, 0] }, "then" : { "$subtract" : [0, "$_id"] }, "else" : "$_id" } }, "else" : "$_id" } }, "_id" : 0 } }
            """);
    }

    public override async Task Select_conditional_flatten_nested_tests(bool async)
    {
        await base.Select_conditional_flatten_nested_tests(async);

        AssertMql(
            """
Orders.{ "$project" : { "_v" : { "$cond" : { "if" : { "$ne" : [{ "$mod" : ["$_id", 2] }, 0] }, "then" : "$_id", "else" : { "$subtract" : [0, "$_id"] } } }, "_id" : 0 } }
""");
    }

    public override async Task Select_with_multiple_Take(bool async)
    {
        await base.Select_with_multiple_Take(async);

        AssertMql(
            """
            Customers.{ "$sort" : { "_id" : 1 } }, { "$limit" : 5 }, { "$limit" : 3 }
            """);
    }

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Entity_passed_to_DTO_constructor_works(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Set_operation_in_pending_collection(bool _)
        => Task.CompletedTask;

#endif

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Projection_when_arithmetic_expression_precedence(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Projection_when_arithmetic_expressions(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Projection_when_arithmetic_mixed(bool _)
        => Task.CompletedTask;

    public override async Task Projection_when_null_value(bool async)
    {
        await base.Projection_when_null_value(async);

        AssertMql(
            """
            Customers.{ "$project" : { "_v" : "$Region", "_id" : 0 } }
            """);
    }

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Projection_when_client_evald_subquery(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Project_to_object_array(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Projection_of_entity_type_into_object_array(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Projection_of_multiple_entity_types_into_object_array(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Projection_of_entity_type_into_object_list(bool _)
        => Task.CompletedTask;

    public override async Task Project_to_int_array(bool async)
    {
        await base.Project_to_int_array(async);

        AssertMql(
            """
            Employees.{ "$match" : { "_id" : 1 } }, { "$project" : { "_v" : ["$_id", "$ReportsTo"], "_id" : 0 } }
            """);
    }

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Select_bool_closure_with_order_parameter_with_cast_to_nullable(bool _)
        => Task.CompletedTask;

    public override async Task Select_scalar(bool async)
    {
        await base.Select_scalar(async);

        AssertMql(
            """
            Customers.{ "$project" : { "_v" : "$City", "_id" : 0 } }
            """);
    }

    public override async Task Select_anonymous_one(bool async)
    {
        await base.Select_anonymous_one(async);

        AssertMql(
            """
            Customers.{ "$project" : { "City" : "$City", "_id" : 0 } }
            """);
    }

    public override async Task Select_anonymous_two(bool async)
    {
        await base.Select_anonymous_two(async);

        AssertMql(
            """
            Customers.{ "$project" : { "City" : "$City", "Phone" : "$Phone", "_id" : 0 } }
            """);
    }

    public override async Task Select_anonymous_three(bool async)
    {
        await base.Select_anonymous_three(async);

        AssertMql(
            """
            Customers.{ "$project" : { "City" : "$City", "Phone" : "$Phone", "Country" : "$Country", "_id" : 0 } }
            """);
    }

    public override async Task Select_anonymous_bool_constant_true(bool async)
    {
        await base.Select_anonymous_bool_constant_true(async);

        AssertMql(
            """
            Customers.{ "$project" : { "CustomerID" : "$_id", "ConstantTrue" : { "$literal" : true }, "_id" : 0 } }
            """);
    }

    public override async Task Select_anonymous_constant_in_expression(bool async)
    {
        await base.Select_anonymous_constant_in_expression(async);

        AssertMql(
            """
            Customers.{ "$project" : { "CustomerID" : "$_id", "Expression" : { "$add" : [{ "$strLenCP" : "$_id" }, 5] }, "_id" : 0 } }
            """);
    }

    public override async Task Select_anonymous_conditional_expression(bool async)
    {
        await base.Select_anonymous_conditional_expression(async);

        AssertMql(
            """
            Products.{ "$project" : { "ProductID" : "$_id", "IsAvailable" : { "$gt" : [{ "$toInt" : "$UnitsInStock" }, 0] }, "_id" : 0 } }
            """);
    }

    public override async Task Select_constant_int(bool async)
    {
        await base.Select_constant_int(async);

        AssertMql(
            """
            Customers.{ "$project" : { "_v" : { "$literal" : 0 }, "_id" : 0 } }
            """);
    }

    public override async Task Select_constant_null_string(bool async)
    {
        await base.Select_constant_null_string(async);

        AssertMql(
            """
            Customers.{ "$project" : { "_v" : null, "_id" : 0 } }
            """);
    }

    public override async Task Select_local(bool async)
    {
        await base.Select_local(async);

        AssertMql(
            """
            Customers.{ "$project" : { "_v" : { "$literal" : 10 }, "_id" : 0 } }
            """);
    }

    public override async Task Select_scalar_primitive_after_take(bool async)
    {
        await base.Select_scalar_primitive_after_take(async);

        AssertMql(
            """
            Employees.{ "$limit" : 9 }, { "$project" : { "_v" : "$_id", "_id" : 0 } }
            """);
    }

    public override async Task Select_project_filter(bool async)
    {
        await base.Select_project_filter(async);

        AssertMql(
            """
            Customers.{ "$match" : { "City" : "London" } }, { "$project" : { "_v" : "$CompanyName", "_id" : 0 } }
            """);
    }

    public override async Task Select_project_filter2(bool async)
    {
        await base.Select_project_filter2(async);

        AssertMql(
            """
            Customers.{ "$match" : { "City" : "London" } }, { "$project" : { "_v" : "$City", "_id" : 0 } }
            """);
    }

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Select_nested_collection(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Select_nested_collection_multi_level(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Select_nested_collection_multi_level2(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Select_nested_collection_multi_level3(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Select_nested_collection_multi_level4(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Select_nested_collection_multi_level5(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Select_nested_collection_multi_level6(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Select_nested_collection_count_using_anonymous_type(bool _)
        => Task.CompletedTask;

    public override async Task New_date_time_in_anonymous_type_works(bool async)
    {
        await base.New_date_time_in_anonymous_type_works(async);

        AssertMql(
            """
            Customers.{ "$match" : { "_id" : { "$regularExpression" : { "pattern" : "^A", "options" : "s" } } } }, { "$project" : { "_v" : { "$literal" : { "A" : { "$date" : { "$numberLong" : "-62135596800000" } } } }, "_id" : 0 } }
            """);
    }

    public override async Task Select_non_matching_value_types_int_to_long_introduces_explicit_cast(bool async)
    {
        await base.Select_non_matching_value_types_int_to_long_introduces_explicit_cast(async);

        AssertMql(
            """
            Orders.{ "$match" : { "CustomerID" : "ALFKI" } }, { "$sort" : { "_id" : 1 } }, { "$project" : { "_v" : { "$toLong" : "$_id" }, "_id" : 0 } }
            """);
    }

    public override async Task Select_non_matching_value_types_nullable_int_to_long_introduces_explicit_cast(bool async)
    {
        await base.Select_non_matching_value_types_nullable_int_to_long_introduces_explicit_cast(async);

        AssertMql(
            """
            Orders.{ "$match" : { "CustomerID" : "ALFKI" } }, { "$sort" : { "_id" : 1 } }, { "$project" : { "_v" : { "$toLong" : "$EmployeeID" }, "_id" : 0 } }
            """);
    }

    public override async Task Select_non_matching_value_types_nullable_int_to_int_doesnt_introduce_explicit_cast(bool async)
    {
        await base.Select_non_matching_value_types_nullable_int_to_int_doesnt_introduce_explicit_cast(async);

        AssertMql(
            """
            Orders.{ "$match" : { "CustomerID" : "ALFKI" } }, { "$sort" : { "_id" : 1 } }, { "$project" : { "_v" : "$EmployeeID", "_id" : 0 } }
            """);
    }

    public override async Task Select_non_matching_value_types_int_to_nullable_int_doesnt_introduce_explicit_cast(bool async)
    {
        await base.Select_non_matching_value_types_int_to_nullable_int_doesnt_introduce_explicit_cast(async);

        AssertMql(
            """
            Orders.{ "$match" : { "CustomerID" : "ALFKI" } }, { "$sort" : { "_id" : 1 } }, { "$project" : { "_v" : "$_id", "_id" : 0 } }
            """);
    }

    public override async Task Select_non_matching_value_types_from_binary_expression_introduces_explicit_cast(bool async)
    {
        await base.Select_non_matching_value_types_from_binary_expression_introduces_explicit_cast(async);

        AssertMql(
            """
            Orders.{ "$match" : { "CustomerID" : "ALFKI" } }, { "$sort" : { "_id" : 1 } }, { "$project" : { "_v" : { "$toLong" : { "$add" : ["$_id", "$_id"] } }, "_id" : 0 } }
            """);
    }

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Select_non_matching_value_types_from_binary_expression_nested_introduces_top_level_explicit_cast(bool _)
        => Task.CompletedTask;

    public override async Task Select_non_matching_value_types_from_unary_expression_introduces_explicit_cast1(bool async)
    {
        await base.Select_non_matching_value_types_from_unary_expression_introduces_explicit_cast1(async);

        AssertMql(
            """
            Orders.{ "$match" : { "CustomerID" : "ALFKI" } }, { "$sort" : { "_id" : 1 } }, { "$project" : { "_v" : { "$toLong" : { "$subtract" : [0, "$_id"] } }, "_id" : 0 } }
            """);
    }

    public override async Task Select_non_matching_value_types_from_unary_expression_introduces_explicit_cast2(bool async)
    {
        await base.Select_non_matching_value_types_from_unary_expression_introduces_explicit_cast2(async);

        AssertMql(
            """
            Orders.{ "$match" : { "CustomerID" : "ALFKI" } }, { "$sort" : { "_id" : 1 } }, { "$project" : { "_v" : { "$subtract" : [0, { "$toLong" : "$_id" }] }, "_id" : 0 } }
            """);
    }

    public override async Task Select_non_matching_value_types_from_length_introduces_explicit_cast(bool async)
    {
        await base.Select_non_matching_value_types_from_length_introduces_explicit_cast(async);

        AssertMql(
            """
            Orders.{ "$match" : { "CustomerID" : "ALFKI" } }, { "$sort" : { "_id" : 1 } }, { "$project" : { "_v" : { "$toLong" : { "$strLenCP" : "$CustomerID" } }, "_id" : 0 } }
            """);
    }

    public override async Task Select_non_matching_value_types_from_method_call_introduces_explicit_cast(bool async)
    {
        await base.Select_non_matching_value_types_from_method_call_introduces_explicit_cast(async);

        AssertMql(
            """
            Orders.{ "$match" : { "CustomerID" : "ALFKI" } }, { "$sort" : { "_id" : 1 } }, { "$project" : { "_v" : { "$toLong" : { "$abs" : "$_id" } }, "_id" : 0 } }
            """);
    }

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Select_non_matching_value_types_from_anonymous_type_introduces_explicit_cast(bool _)
        => Task.CompletedTask;

    public override async Task Select_conditional_with_null_comparison_in_test(bool async)
    {
        await base.Select_conditional_with_null_comparison_in_test(async);

        AssertMql(
            """
Orders.{ "$match" : { "CustomerID" : "ALFKI" } }, { "$project" : { "_v" : { "$or" : [{ "$eq" : ["$CustomerID", null] }, { "$lt" : ["$_id", 100] }] }, "_id" : 0 } }
""");
    }

    public override async Task Select_over_10_nested_ternary_condition(bool isAsync)
    {
        await base.Select_over_10_nested_ternary_condition(isAsync);

        AssertMql(
            """
            Customers.{ "$project" : { "_v" : { "$cond" : { "if" : { "$eq" : ["$_id", "1"] }, "then" : "01", "else" : { "$cond" : { "if" : { "$eq" : ["$_id", "2"] }, "then" : "02", "else" : { "$cond" : { "if" : { "$eq" : ["$_id", "3"] }, "then" : "03", "else" : { "$cond" : { "if" : { "$eq" : ["$_id", "4"] }, "then" : "04", "else" : { "$cond" : { "if" : { "$eq" : ["$_id", "5"] }, "then" : "05", "else" : { "$cond" : { "if" : { "$eq" : ["$_id", "6"] }, "then" : "06", "else" : { "$cond" : { "if" : { "$eq" : ["$_id", "7"] }, "then" : "07", "else" : { "$cond" : { "if" : { "$eq" : ["$_id", "8"] }, "then" : "08", "else" : { "$cond" : { "if" : { "$eq" : ["$_id", "9"] }, "then" : "09", "else" : { "$cond" : { "if" : { "$eq" : ["$_id", "10"] }, "then" : "10", "else" : { "$cond" : { "if" : { "$eq" : ["$_id", "11"] }, "then" : "11", "else" : null } } } } } } } } } } } } } } } } } } } } } }, "_id" : 0 } }
            """);
    }

#if EF9
    public override async Task Select_conditional_drops_false(bool isAsync)
    {
        await base.Select_conditional_drops_false(isAsync);

        AssertMql(
            """
Orders.{ "$project" : { "_v" : { "$cond" : { "if" : { "$eq" : [{ "$mod" : ["$_id", 2] }, 0] }, "then" : "$_id", "else" : { "$subtract" : [0, "$_id"] } } }, "_id" : 0 } }
""");
    }

    public override async Task Select_conditional_terminates_at_true(bool isAsync)
    {
        await base.Select_conditional_terminates_at_true(isAsync);

        AssertMql(
            """
Orders.{ "$project" : { "_v" : { "$cond" : { "if" : { "$eq" : [{ "$mod" : ["$_id", 2] }, 0] }, "then" : "$_id", "else" : 0 } }, "_id" : 0 } }
""");
    }

    public override async Task Select_conditional_flatten_nested_results(bool isAsync)
    {
        await base.Select_conditional_flatten_nested_results(isAsync);

        AssertMql(
            """
Orders.{ "$project" : { "_v" : { "$cond" : { "if" : { "$eq" : [{ "$mod" : ["$_id", 2] }, 0] }, "then" : { "$cond" : { "if" : { "$eq" : [{ "$mod" : ["$_id", 5] }, 0] }, "then" : { "$subtract" : [0, "$_id"] }, "else" : "$_id" } }, "else" : "$_id" } }, "_id" : 0 } }
""");
    }

    public override async Task Select_conditional_flatten_nested_tests(bool isAsync)
    {
        await base.Select_conditional_flatten_nested_tests(isAsync);

        AssertMql(
            """
Orders.{ "$project" : { "_v" : { "$cond" : { "if" : { "$ne" : [{ "$mod" : ["$_id", 2] }, 0] }, "then" : "$_id", "else" : { "$subtract" : [0, "$_id"] } } }, "_id" : 0 } }
""");
    }

#endif

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Projection_in_a_subquery_should_be_liftable(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Projection_containing_DateTime_subtraction(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Project_single_element_from_collection_with_OrderBy_Take_and_FirstOrDefault(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Project_single_element_from_collection_with_OrderBy_Skip_and_FirstOrDefault(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Project_single_element_from_collection_with_OrderBy_Distinct_and_FirstOrDefault(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Project_single_element_from_collection_with_OrderBy_Distinct_and_FirstOrDefault_followed_by_projecting_length(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Project_single_element_from_collection_with_OrderBy_Take_and_SingleOrDefault(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Project_single_element_from_collection_with_OrderBy_Take_and_FirstOrDefault_with_parameter(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Project_single_element_from_collection_with_multiple_OrderBys_Take_and_FirstOrDefault(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Project_single_element_from_collection_with_multiple_OrderBys_Take_and_FirstOrDefault_followed_by_projection_of_length_property(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Project_single_element_from_collection_with_multiple_OrderBys_Take_and_FirstOrDefault_2(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Project_single_element_from_collection_with_OrderBy_over_navigation_Take_and_FirstOrDefault(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Project_single_element_from_collection_with_OrderBy_over_navigation_Take_and_FirstOrDefault_2(bool _)
        => Task.CompletedTask;

    public override async Task Select_datetime_year_component(bool async)
    {
        await base.Select_datetime_year_component(async);

        AssertMql(
            """
            Orders.{ "$project" : { "_v" : { "$year" : "$OrderDate" }, "_id" : 0 } }
            """);
    }

    public override async Task Select_datetime_month_component(bool async)
    {
        await base.Select_datetime_month_component(async);

        AssertMql(
            """
            Orders.{ "$project" : { "_v" : { "$month" : "$OrderDate" }, "_id" : 0 } }
            """);
    }

    public override async Task Select_datetime_day_of_year_component(bool async)
    {
        await base.Select_datetime_day_of_year_component(async);

        AssertMql(
            """
            Orders.{ "$project" : { "_v" : { "$dayOfYear" : "$OrderDate" }, "_id" : 0 } }
            """);
    }

    public override async Task Select_datetime_day_component(bool async)
    {
        await base.Select_datetime_day_component(async);

        AssertMql(
            """
            Orders.{ "$project" : { "_v" : { "$dayOfMonth" : "$OrderDate" }, "_id" : 0 } }
            """);
    }

    public override async Task Select_datetime_hour_component(bool async)
    {
        await base.Select_datetime_hour_component(async);

        AssertMql(
            """
            Orders.{ "$project" : { "_v" : { "$hour" : "$OrderDate" }, "_id" : 0 } }
            """);
    }

    public override async Task Select_datetime_minute_component(bool async)
    {
        await base.Select_datetime_minute_component(async);

        AssertMql(
            """
            Orders.{ "$project" : { "_v" : { "$minute" : "$OrderDate" }, "_id" : 0 } }
            """);
    }

    public override async Task Select_datetime_second_component(bool async)
    {
        await base.Select_datetime_second_component(async);

        AssertMql(
            """
            Orders.{ "$project" : { "_v" : { "$second" : "$OrderDate" }, "_id" : 0 } }
            """);
    }

    public override async Task Select_datetime_millisecond_component(bool async)
    {
        await base.Select_datetime_millisecond_component(async);

        AssertMql(
            """
            Orders.{ "$project" : { "_v" : { "$millisecond" : "$OrderDate" }, "_id" : 0 } }
            """);
    }

    public override async Task Select_byte_constant(bool async)
    {
        await base.Select_byte_constant(async);

        AssertMql(
            """
            Customers.{ "$project" : { "_v" : { "$cond" : { "if" : { "$eq" : ["$_id", "ALFKI"] }, "then" : 1, "else" : 2 } }, "_id" : 0 } }
            """);
    }

    public override async Task Select_short_constant(bool async)
    {
        await base.Select_short_constant(async);

        AssertMql(
            """
            Customers.{ "$project" : { "_v" : { "$cond" : { "if" : { "$eq" : ["$_id", "ALFKI"] }, "then" : 1, "else" : 2 } }, "_id" : 0 } }
            """);
    }

    public override async Task Select_bool_constant(bool async)
    {
        await base.Select_bool_constant(async);

        AssertMql(
            """
Customers.{ "$project" : { "_v" : { "$eq" : ["$_id", "ALFKI"] }, "_id" : 0 } }
""");
    }

    public override async Task Anonymous_projection_AsNoTracking_Selector(bool async)
    {
        await base.Anonymous_projection_AsNoTracking_Selector(async);

        AssertMql(
            """
            Orders.{ "$project" : { "_v" : "$OrderDate", "_id" : 0 } }
            """);
    }

    public override async Task Anonymous_projection_with_repeated_property_being_ordered(bool async)
    {
        await base.Anonymous_projection_with_repeated_property_being_ordered(async);

        AssertMql(
            """
            Customers.{ "$sort" : { "_id" : 1 } }, { "$project" : { "A" : "$_id", "B" : "$_id", "_id" : 0 } }
            """);
    }

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Anonymous_projection_with_repeated_property_being_ordered_2(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Select_GetValueOrDefault_on_DateTime(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Select_GetValueOrDefault_on_DateTime_with_null_values(bool _)
        => Task.CompletedTask;

    public override async Task Cast_on_top_level_projection_brings_explicit_Cast(bool async)
    {
        await base.Cast_on_top_level_projection_brings_explicit_Cast(async);

        AssertMql(
            """
            Orders.{ "$project" : { "_v" : { "$toDouble" : "$_id" }, "_id" : 0 } }
            """);
    }

    public override async Task Projecting_nullable_struct(bool async)
    {
        await base.Projecting_nullable_struct(async);

        AssertMql(
            """
            Orders.{ "$project" : { "One" : "$CustomerID", "Two" : { "$cond" : { "if" : { "$eq" : ["$CustomerID", "ALFKI"] }, "then" : { "X" : "$_id", "Y" : { "$strLenCP" : "$CustomerID" } }, "else" : null } }, "_id" : 0 } }
            """);
    }

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Multiple_select_many_with_predicate(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task SelectMany_without_result_selector_naked_collection_navigation(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task SelectMany_without_result_selector_collection_navigation_composed(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task SelectMany_correlated_with_outer_1(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task SelectMany_correlated_with_outer_2(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task SelectMany_correlated_with_outer_3(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task SelectMany_correlated_with_outer_4(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task SelectMany_correlated_with_outer_5(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task SelectMany_correlated_with_outer_6(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task SelectMany_correlated_with_outer_7(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task FirstOrDefault_over_empty_collection_of_value_type_returns_correct_results(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Project_non_nullable_value_after_FirstOrDefault_on_empty_collection(bool _)
        => Task.CompletedTask;

    public override async Task Member_binding_after_ctor_arguments_fails_with_client_eval(bool async)
    {
        // Default ordering on Mongo is different from .NET, so just compare the first three items.
        await AssertQuery(
            async,
            ss => ss.Set<Customer>().Select(c => new CustomerListItem(c.CustomerID, c.City)).OrderBy(c => c.City).Take(3),
            assertOrder: true);

        AssertMql(
            """
            Customers.{ "$project" : { "_id" : 0, "_document" : "$$ROOT", "_key1" : { "$let" : { "vars" : { "this" : { "_id" : "$_id", "City" : "$City" } }, "in" : "$$this.City" } } } }, { "$sort" : { "_key1" : 1 } }, { "$replaceRoot" : { "newRoot" : "$_document" } }, { "$limit" : 3 }, { "$project" : { "_id" : "$_id", "City" : "$City" } }
            """);
    }

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Filtered_collection_projection_is_tracked(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Filtered_collection_projection_with_to_list_is_tracked(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task SelectMany_with_collection_being_correlated_subquery_which_references_inner_and_outer_entity(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task SelectMany_with_collection_being_correlated_subquery_which_references_non_mapped_properties_from_inner_and_outer_entity(bool _)
        => Task.CompletedTask;

    public override async Task Select_with_complex_expression_that_can_be_funcletized(bool async)
    {
        await base.Select_with_complex_expression_that_can_be_funcletized(async);

        // Test changed between EF8 and EF9
#if EF8
        AssertMql(
            """
            Customers.{ "$match" : { "_id" : "ALFKI" } }, { "$project" : { "_v" : { "$indexOfCP" : ["$ContactName", ""] }, "_id" : 0 } }
            """);
#else
        AssertMql(
            """
Customers.{ "$match" : { "_id" : "ALFKI" } }, { "$project" : { "_v" : { "$indexOfCP" : ["$Region", ""] }, "_id" : 0 } }
""");
#endif
    }

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Select_chained_entity_navigation_doesnt_materialize_intermittent_entities(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Select_entity_compared_to_null(bool _)
        => Task.CompletedTask;

    public override async Task Explicit_cast_in_arithmetic_operation_is_preserved(bool async)
    {
        await base.Explicit_cast_in_arithmetic_operation_is_preserved(async);

        AssertMql(
            """
            Orders.{ "$match" : { "_id" : 10250 } }, { "$project" : { "_v" : { "$divide" : ["$_id", { "$add" : ["$_id", 1000] }] }, "_id" : 0 } }
            """);
    }

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task SelectMany_whose_selector_references_outer_source(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Collection_FirstOrDefault_with_entity_equality_check_in_projection(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Collection_FirstOrDefault_with_nullable_unsigned_int_column(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task ToList_Count_in_projection_works(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task LastOrDefault_member_access_in_projection_translates_to_server(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Projection_with_parameterized_constructor(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Projection_with_parameterized_constructor_with_member_assignment(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Collection_projection_AsNoTracking_OrderBy(bool _)
        => Task.CompletedTask;

    public override async Task Coalesce_over_nullable_uint(bool async)
    {
        await base.Coalesce_over_nullable_uint(async);

        AssertMql(
            """
            Orders.{ "$project" : { "_v" : { "$ifNull" : ["$EmployeeID", 0] }, "_id" : 0 } }
            """);
    }

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Project_uint_through_collection_FirstOrDefault(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Project_keyless_entity_FirstOrDefault_without_orderby(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Reverse_changes_asc_order_to_desc(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Reverse_changes_desc_order_to_asc(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Reverse_after_multiple_orderbys(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Reverse_after_orderby_thenby(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Reverse_in_subquery_via_pushdown(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Reverse_after_orderBy_and_take(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Reverse_in_join_outer(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Reverse_in_join_outer_with_take(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Reverse_in_join_inner(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Reverse_in_join_inner_with_skip(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Reverse_in_SelectMany(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Reverse_in_SelectMany_with_Take(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Reverse_in_projection_subquery(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Reverse_in_projection_subquery_single_result(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Reverse_in_projection_scalar_subquery(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Projection_AsEnumerable_projection(bool _)
        => Task.CompletedTask;

    public override async Task Projection_custom_type_in_both_sides_of_ternary(bool async)
    {
        await base.Projection_custom_type_in_both_sides_of_ternary(async);

        AssertMql(
            """
            Customers.{ "$sort" : { "_id" : 1 } }, { "$project" : { "_v" : { "$cond" : { "if" : { "$eq" : ["$City", "Seattle"] }, "then" : { "_id" : "PAY", "Name" : "Pay" }, "else" : { "_id" : "REC", "Name" : "Receive" } } }, "_id" : 0 } }
            """);
    }

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Projecting_multiple_collection_with_same_constant_works(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Custom_projection_reference_navigation_PK_to_FK_optimization(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Projecting_Length_of_a_string_property_after_FirstOrDefault_on_correlated_collection(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Projecting_count_of_navigation_which_is_generic_list(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Projecting_count_of_navigation_which_is_generic_collection(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Projecting_count_of_navigation_which_is_generic_collection_using_convert(bool _)
        => Task.CompletedTask;

    public override async Task Projection_take_projection_doesnt_project_intermittent_column(bool async)
    {
        await base.Projection_take_projection_doesnt_project_intermittent_column(async);

        AssertMql(
            """
            Customers.{ "$sort" : { "_id" : 1 } }, { "$limit" : 10 }, { "$project" : { "Aggregate" : { "$concat" : ["$_id", " ", "$City"] }, "_id" : 0 } }
            """);
    }

    public override async Task Projection_skip_projection_doesnt_project_intermittent_column(bool async)
    {
        await base.Projection_skip_projection_doesnt_project_intermittent_column(async);

        AssertMql(
            """
            Customers.{ "$sort" : { "_id" : 1 } }, { "$skip" : 7 }, { "$project" : { "Aggregate" : { "$concat" : ["$_id", " ", "$City"] }, "_id" : 0 } }
            """);
    }

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Projection_Distinct_projection_preserves_columns_used_for_distinct_in_subquery(bool _)
        => Task.CompletedTask;

    public override async Task Projection_take_predicate_projection(bool async)
    {
        await base.Projection_take_predicate_projection(async);

        AssertMql(
            """
            Customers.{ "$sort" : { "_id" : 1 } }, { "$limit" : 10 }, { "$match" : { "_id" : { "$regularExpression" : { "pattern" : "^A", "options" : "s" } } } }, { "$project" : { "Aggregate" : { "$concat" : ["$_id", " ", "$City"] }, "_id" : 0 } }
            """);
    }

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Do_not_erase_projection_mapping_when_adding_single_projection(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Ternary_in_client_eval_assigns_correct_types(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Projecting_after_navigation_and_distinct(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Correlated_collection_after_distinct_with_complex_projection_containing_original_identifier(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Correlated_collection_after_distinct_not_containing_original_identifier(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Correlated_collection_after_distinct_with_complex_projection_not_containing_original_identifier(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Correlated_collection_after_groupby_with_complex_projection_containing_original_identifier(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Select_nested_collection_deep(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Select_nested_collection_deep_distinct_no_identifiers(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Collection_include_over_result_of_single_non_scalar(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Collection_projection_selecting_outer_element_followed_by_take(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Take_on_top_level_and_on_collection_projection_with_outer_apply(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Take_on_correlated_collection_in_first(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Client_projection_via_ctor_arguments(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Client_projection_with_string_initialization_with_scalar_subquery(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task MemberInit_in_projection_without_arguments(bool _)
        => Task.CompletedTask;

    public override async Task VisitLambda_should_not_be_visited_trivially(bool async)
    {
        await base.VisitLambda_should_not_be_visited_trivially(async);

        AssertMql(
            """
            Orders.{ "$match" : { "CustomerID" : { "$regularExpression" : { "pattern" : "^A", "options" : "s" } } } }
            """);
    }

    public override async Task Select_anonymous_literal(bool async)
    {
        await base.Select_anonymous_literal(async);

        AssertMql(
            """
            Customers.{ "$project" : { "_v" : { "$literal" : { "X" : 10 } }, "_id" : 0 } }
            """);
    }

    public override async Task Select_anonymous_nested(bool async)
    {
        await base.Select_anonymous_nested(async);

        AssertMql(
            """
            Customers.{ "$project" : { "City" : "$City", "Country" : { "Country" : "$Country" }, "_id" : 0 } }
            """);
    }

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Projection_when_arithmetic_mixed_subqueries(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Select_datetime_Ticks_component(bool _)
        => Task.CompletedTask;

    public override async Task Select_datetime_TimeOfDay_component(bool async)
    {
        await base.Select_datetime_TimeOfDay_component(async);

        AssertMql(
            """
            Orders.{ "$project" : { "_v" : { "$dateDiff" : { "startDate" : { "$dateTrunc" : { "date" : "$OrderDate", "unit" : "day" } }, "endDate" : "$OrderDate", "unit" : "millisecond" } }, "_id" : 0 } }
            """);
    }

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Select_anonymous_with_object(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Failing sometimes on latest server."), MemberData(nameof(IsAsyncData))]
    public override Task Client_method_in_projection_requiring_materialization_1(bool _)
        => Task.CompletedTask;

    public override async Task Select_datetime_DayOfWeek_component(bool async)
    {
        await base.Select_datetime_DayOfWeek_component(async);

        AssertMql(
            """
            Orders.{ "$project" : { "_v" : { "$subtract" : [{ "$dayOfWeek" : "$OrderDate" }, 1] }, "_id" : 0 } }
            """);
    }

    public override async Task Select_scalar_primitive(bool async)
    {
        await base.Select_scalar_primitive(async);

        AssertMql(
            """
            Employees.{ "$project" : { "_v" : "$_id", "_id" : 0 } }
            """);
    }

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Client_method_in_projection_requiring_materialization_2(bool _)
        => Task.CompletedTask;

    public override async Task Select_anonymous_empty(bool async)
    {
        await base.Select_anonymous_empty(async);

        AssertMql(
            """
            Customers.{ "$project" : { "_v" : { "$literal" : { } }, "_id" : 0 } }
            """);
    }

    public override async Task Select_customer_table(bool async)
    {
        await base.Select_customer_table(async);

        AssertMql(
            """
            Customers.
            """);
    }

    public override async Task Select_into(bool async)
    {
        await base.Select_into(async);

        AssertMql(
            """
            Customers.{ "$match" : { "_id" : "ALFKI" } }, { "$project" : { "_v" : "$_id", "_id" : 0 } }
            """);
    }

    public override async Task Select_bool_closure(bool async)
    {
        await base.Select_bool_closure(async);

        AssertMql(
            """
            Customers.{ "$project" : { "_v" : { "$literal" : { "f" : false } }, "_id" : 0 } }
            """,
            //
            """
            Customers.{ "$project" : { "_v" : { "$literal" : { "f" : true } }, "_id" : 0 } }
            """);
    }

    public override async Task Select_customer_identity(bool async)
    {
        await base.Select_customer_identity(async);

        AssertMql(
            """
            Customers.
            """);
    }

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Correlated_collection_after_groupby_with_complex_projection_not_containing_original_identifier(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Select_bool_closure_with_order_by_property_with_cast_to_nullable(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Reverse_without_explicit_ordering(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task List_of_list_of_anonymous_type(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task List_from_result_of_single_result(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task List_from_result_of_single_result_2(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task List_from_result_of_single_result_3(bool _)
        => Task.CompletedTask;

    public override async Task Using_enumerable_parameter_in_projection(bool async)
    {
        await base.Using_enumerable_parameter_in_projection(async);

        AssertMql(
            """
            Customers.{ "$match" : { "_id" : { "$regularExpression" : { "pattern" : "^F", "options" : "s" } } } }, { "$project" : { "CustomerID" : "$_id", "Orders" : [], "_id" : 0 } }
            """);
    }

#if EF9
    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Entity_passed_to_DTO_constructor_works(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Set_operation_in_pending_collection(bool _)
        => Task.CompletedTask;

#endif

    private void AssertMql(params string[] expected)
        => Fixture.TestMqlLoggerFactory.AssertBaseline(expected);

    protected override void ClearLog()
        => Fixture.TestMqlLoggerFactory.Clear();
}
