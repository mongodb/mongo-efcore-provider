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
using MongoDB.Driver;
using MongoDB.Driver.Linq;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace MongoDB.EntityFrameworkCore.SpecificationTests.Query;

public class NorthwindMiscellaneousQueryMongoTest
    : NorthwindMiscellaneousQueryTestBase<NorthwindQueryMongoFixture<NoopModelCustomizer>>
{
    public NorthwindMiscellaneousQueryMongoTest(
        NorthwindQueryMongoFixture<NoopModelCustomizer> fixture,
        ITestOutputHelper testOutputHelper)
        : base(fixture)
    {
        ClearLog();
        // Fixture.TestMqlLoggerFactory.SetTestOutputHelper(testOutputHelper);
    }

    [ConditionalFact]
    public virtual void Check_all_tests_overridden()
        => TestHelpers.AssertAllMethodsOverridden(GetType());

#if EF8 || EF9

    [ConditionalTheory(Skip = "DefaultIfEmpty not supported"), MemberData(nameof(IsAsyncData))]
    public override Task DefaultIfEmpty_over_empty_collection_followed_by_projecting_constant(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "DefaultIfEmpty not supported"), MemberData(nameof(IsAsyncData))]
    public override Task DefaultIfEmpty_without_group_join(bool _)
        => Task.CompletedTask;

#else

    [ConditionalTheory(Skip = "DefaultIfEmpty not supported"), MemberData(nameof(IsAsyncData))]
    public override Task DefaultIfEmpty_top_level(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "DefaultIfEmpty not supported"), MemberData(nameof(IsAsyncData))]
    public override Task DefaultIfEmpty_without_group_join(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Navigations issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task DefaultIfEmpty_over_empty_collection_followed_by_projecting_constant(bool _)
        => Task.CompletedTask;

    public override async Task Coalesce_Correct_TypeMapping_Double(bool async)
    {
        await base.Coalesce_Correct_TypeMapping_Double(async);

        AssertMql();
    }

    public override async Task Coalesce_Correct_Multiple_Same_TypeMapping(bool async)
    {
        await base.Coalesce_Correct_Multiple_Same_TypeMapping(async);

        AssertMql(
            """
            Employees.{ "$sort" : { "_id" : 1 } }, { "$project" : { "_v" : { "$ifNull" : [{ "$add" : [{ "$toLong" : "$ReportsTo" }, 1] }, { "$ifNull" : [{ "$add" : [{ "$toLong" : "$ReportsTo" }, 2] }, { "$add" : [{ "$toLong" : "$ReportsTo" }, 3] }] }] }, "_id" : 0 } }
            """);
    }

    [ConditionalTheory(Skip = "DefaultIfEmpty not supported"), MemberData(nameof(IsAsyncData))]
    public override Task DefaultIfEmpty_top_level_followed_by_constant_Select(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "DefaultIfEmpty not supported"), MemberData(nameof(IsAsyncData))]
    public override Task DefaultIfEmpty_top_level_preceded_by_constant_Select(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "DefaultIfEmpty not supported"), MemberData(nameof(IsAsyncData))]
    public override Task SelectMany_correlated_with_Select_value_type_and_DefaultIfEmpty_in_selector(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "DefaultIfEmpty not supported"), MemberData(nameof(IsAsyncData))]
    public override Task Late_subquery_pushdown(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "DefaultIfEmpty not supported"), MemberData(nameof(IsAsyncData))]
    public override Task Where_nanosecond_and_microsecond_component(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "DefaultIfEmpty not supported"), MemberData(nameof(IsAsyncData))]
    public override Task Coalesce_Correct_TypeMapping_String(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Any_on_distinct(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Contains_on_distinct(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task All_on_distinct(bool _)
        => Task.CompletedTask;

    public override async Task SelectMany_correlated_with_DefaultIfEmpty_and_Select_value_type_in_selector_throws(bool async)
    {
        await base.SelectMany_correlated_with_DefaultIfEmpty_and_Select_value_type_in_selector_throws(async);

        AssertMql();
    }

    public override async Task Select_Order(bool async)
    {
        await base.Select_Order(async);

        AssertMql(
            """
            Customers.{ "$sort" : { "_id" : 1 } }, { "$project" : { "_v" : "$_id", "_id" : 0 } }
            """);
    }

    public override async Task Select_OrderDescending(bool async)
    {
        await base.Select_OrderDescending(async);

        AssertMql(
            """
            Customers.{ "$sort" : { "_id" : -1 } }, { "$project" : { "_v" : "$_id", "_id" : 0 } }
            """);
    }

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Where_Order_First(bool _)
        => Task.CompletedTask;

    [ConditionalFact(Skip = "Cross-document navigation access issue EF-216")]
    public override Task IQueryable_captured_variable()
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Skip_1_Take_0_works_when_constant(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Take_0_works_when_constant(bool _)
        => Task.CompletedTask;

    public override async Task Compiler_generated_local_closure_produces_valid_parameter_name(bool async)
    {
        await base.Compiler_generated_local_closure_produces_valid_parameter_name(async);

        AssertMql(
            """
            Customers.{ "$match" : { "_id" : "ALFKI", "City" : "Berlin" } }
            """);
    }

    public override async Task Static_member_access_gets_parameterized_within_larger_evaluatable(bool async)
    {
        await base.Static_member_access_gets_parameterized_within_larger_evaluatable(async);

        AssertMql(
            """
            Customers.{ "$match" : { "_id" : "ALFKI" } }
            """);
    }

    public override async Task Ternary_Not_Null_Contains(bool async)
    {
        await base.Ternary_Not_Null_Contains(async);

        AssertMql(
            """
            Orders.{ "$sort" : { "_id" : 1 } }, { "$match" : { "$expr" : { "$gte" : [{ "$indexOfCP" : [{ "$cond" : { "if" : { "$ne" : ["$$ROOT", null] }, "then" : { "$concat" : [{ "$toString" : "$_id" }, ""] }, "else" : null } }, "1"] }, 0] } } }, { "$project" : { "_v" : { "$cond" : { "if" : { "$ne" : ["$$ROOT", null] }, "then" : { "$concat" : [{ "$toString" : "$_id" }, ""] }, "else" : null } }, "_id" : 0 } }, { "$limit" : 1 }
            """);
    }

    public override async Task Ternary_Not_Null_endsWith_Non_Numeric_First_Part(bool async)
    {
        await base.Ternary_Not_Null_endsWith_Non_Numeric_First_Part(async);

        AssertMql(
            """
            Orders.{ "$sort" : { "_id" : 1 } }, { "$match" : { "$expr" : { "$let" : { "vars" : { "string" : { "$cond" : { "if" : { "$ne" : ["$$ROOT", null] }, "then" : { "$concat" : ["", { "$toString" : "$_id" }, ""] }, "else" : null } } }, "in" : { "$let" : { "vars" : { "start" : { "$subtract" : [{ "$strLenCP" : "$$string" }, 1] } }, "in" : { "$and" : [{ "$gte" : ["$$start", 0] }, { "$eq" : [{ "$indexOfCP" : ["$$string", "1", "$$start"] }, "$$start"] }] } } } } } } }, { "$project" : { "_v" : { "$cond" : { "if" : { "$ne" : ["$$ROOT", null] }, "then" : { "$concat" : ["", { "$toString" : "$_id" }, ""] }, "else" : null } }, "_id" : 0 } }, { "$limit" : 1 }
            """);
    }

    public override async Task Ternary_Null_Equals_Non_Numeric_First_Part(bool async)
    {
        await base.Ternary_Null_Equals_Non_Numeric_First_Part(async);

        AssertMql(
            """
            Orders.{ "$sort" : { "_id" : 1 } }, { "$match" : { "$expr" : { "$eq" : [{ "$cond" : { "if" : { "$eq" : ["$$ROOT", null] }, "then" : null, "else" : { "$concat" : ["", { "$toString" : "$_id" }, ""] } } }, "1"] } } }, { "$project" : { "_v" : { "$cond" : { "if" : { "$eq" : ["$$ROOT", null] }, "then" : null, "else" : { "$concat" : ["", { "$toString" : "$_id" }, ""] } } }, "_id" : 0 } }, { "$limit" : 1 }
            """);
    }

    public override async Task Ternary_Null_StartsWith(bool async)
    {
        await base.Ternary_Null_StartsWith(async);

        AssertMql(
            """
            Orders.{ "$sort" : { "_id" : 1 } }, { "$match" : { "$expr" : { "$eq" : [{ "$indexOfCP" : [{ "$cond" : { "if" : { "$eq" : ["$$ROOT", null] }, "then" : null, "else" : { "$concat" : [{ "$toString" : "$_id" }, ""] } } }, "1"] }, 0] } } }, { "$project" : { "_v" : { "$cond" : { "if" : { "$eq" : ["$$ROOT", null] }, "then" : null, "else" : { "$concat" : [{ "$toString" : "$_id" }, ""] } } }, "_id" : 0 } }, { "$limit" : 1 }
            """);
    }

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Column_access_inside_subquery_predicate(bool _)
        => Task.CompletedTask;

    public override async Task Cast_to_object_over_parameter_directly_in_lambda(bool async)
    {
        await base.Cast_to_object_over_parameter_directly_in_lambda(async);

        AssertMql(
            """
            Orders.{ "$project" : { "_id" : 0, "_document" : "$$ROOT", "_key1" : 8 } }, { "$sort" : { "_key1" : 1 } }, { "$replaceRoot" : { "newRoot" : "$_document" } }
            """);
    }

#endif

    [ConditionalTheory(Skip = "No subquery support"), MemberData(nameof(IsAsyncData))]
    public override Task Subquery_DefaultIfEmpty_Any(bool _)
        => Task.CompletedTask;

    public override async Task Shaper_command_caching_when_parameter_names_different(bool async)
    {
        await base.Shaper_command_caching_when_parameter_names_different(async);

        AssertMql(
            """
            Customers.{ "$match" : { "_id" : "ALFKI" } }, { "$count" : "_v" }
            """,
            //
            """
            Customers.{ "$match" : { "_id" : "ALFKI" } }, { "$count" : "_v" }
            """);
    }

    public override async Task Can_convert_manually_build_expression_with_default(bool async)
    {
        await base.Can_convert_manually_build_expression_with_default(async);

        AssertMql(
            """
            Customers.{ "$match" : { "City" : { "$ne" : null } } }, { "$count" : "_v" }
            """,
            //
            """
            Customers.{ "$match" : { "City" : { "$ne" : null } } }, { "$count" : "_v" }
            """);
    }

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Lifting_when_subquery_nested_order_by_anonymous(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Lifting_when_subquery_nested_order_by_simple(bool _)
        => Task.CompletedTask;

    public override async Task Local_dictionary(bool async)
    {
        await base.Local_dictionary(async);

        AssertMql(
            """
            Customers.{ "$match" : { "_id" : "ALFKI" } }, { "$limit" : 2 }
            """);
    }

    public override async Task Entity_equality_self(bool async)
    {
        await base.Entity_equality_self(async);

        AssertMql(
            """
            Customers.{ "$match" : { "$expr" : { "$eq" : ["$$ROOT", "$$ROOT"] } } }, { "$project" : { "_v" : "$_id", "_id" : 0 } }
            """);
    }

    [ConditionalTheory(Skip = "Entity equality issue EF-202"), MemberData(nameof(IsAsyncData))]
    public override Task Entity_equality_local(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Entity equality issue EF-202"), MemberData(nameof(IsAsyncData))]
    public override Task Entity_equality_local_composite_key(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Entity equality issue EF-202"), MemberData(nameof(IsAsyncData))]
    public override Task Entity_equality_local_double_check(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Join_with_entity_equality_local_on_both_sources(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Entity equality issue EF-202"), MemberData(nameof(IsAsyncData))]
    public override Task Entity_equality_local_inline(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Entity equality issue EF-202"), MemberData(nameof(IsAsyncData))]
    public override Task Entity_equality_local_inline_composite_key(bool _)
        => Task.CompletedTask;

    public override async Task Entity_equality_null(bool async)
    {
        await base.Entity_equality_null(async);

        AssertMql(
            """
            Customers.{ "$match" : { "$expr" : { "$eq" : ["$$ROOT", null] } } }, { "$project" : { "_v" : "$_id", "_id" : 0 } }
            """);
    }

    public override async Task Entity_equality_null_composite_key(bool async)
    {
        await base.Entity_equality_null_composite_key(async);

        AssertMql(
            """
            OrderDetails.{ "$match" : { "$expr" : { "$eq" : ["$$ROOT", null] } } }
            """);
    }

    public override async Task Entity_equality_not_null(bool async)
    {
        await base.Entity_equality_not_null(async);

        AssertMql(
            """
            Customers.{ "$match" : { "$expr" : { "$ne" : ["$$ROOT", null] } } }, { "$project" : { "_v" : "$_id", "_id" : 0 } }
            """);
    }

    public override async Task Entity_equality_not_null_composite_key(bool async)
    {
        await base.Entity_equality_not_null_composite_key(async);

        AssertMql(
            """
            OrderDetails.{ "$match" : { "$expr" : { "$ne" : ["$$ROOT", null] } } }
            """);
    }

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Entity_equality_through_nested_anonymous_type_projection(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Entity_equality_through_DTO_projection(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "No multi-collection query support"), MemberData(nameof(IsAsyncData))]
    public override Task Entity_equality_through_subquery(bool _)
        => Task.CompletedTask;

    public override async Task Entity_equality_through_include(bool async)
    {
        await base.Entity_equality_through_include(async);

        AssertMql(
            """
            Customers.{ "$match" : { "$expr" : { "$eq" : ["$$ROOT", null] } } }, { "$project" : { "_v" : "$_id", "_id" : 0 } }
            """);
    }

    public override async Task Entity_equality_orderby(bool async)
    {
        await base.Entity_equality_orderby(async);

        AssertMql(
            """
            Customers.{ "$sort" : { "_id" : 1 } }
            """);
    }

    public override async Task Entity_equality_orderby_descending_composite_key(bool async)
    {
        await base.Entity_equality_orderby_descending_composite_key(async);

        AssertMql(
            """
            OrderDetails.{ "$sort" : { "_id.OrderID" : -1, "_id.ProductID" : -1 } }
            """);
    }

    [ConditionalTheory(Skip = "No multi-collection query support"), MemberData(nameof(IsAsyncData))]
    public override Task Entity_equality_orderby_subquery(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "No multi-collection query support"), MemberData(nameof(IsAsyncData))]
    public override Task Entity_equality_orderby_descending_subquery_composite_key(bool _)
        => Task.CompletedTask;

#if EF8 || EF9
    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Default_if_empty_top_level(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "DefaultIfEmpty not supported"), MemberData(nameof(IsAsyncData))]
    public override Task Join_with_default_if_empty_on_both_sources(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "DefaultIfEmpty not supported"), MemberData(nameof(IsAsyncData))]
    public override Task Default_if_empty_top_level_followed_by_projecting_constant(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "DefaultIfEmpty not supported"), MemberData(nameof(IsAsyncData))]
    public override Task Default_if_empty_top_level_positive(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "DefaultIfEmpty not supported"), MemberData(nameof(IsAsyncData))]
    public override Task Default_if_empty_top_level_projection(bool _)
        => Task.CompletedTask;

#else

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Join_with_DefaultIfEmpty_on_both_sources(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Navigations issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task DefaultIfEmpty_top_level_positive(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Navigations issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task DefaultIfEmpty_top_level_projection(bool _)
        => Task.CompletedTask;

#endif

    [ConditionalTheory(Skip = "Navigations issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Where_query_composition(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Navigations issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Where_query_composition_is_null(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Navigations issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Where_query_composition_is_not_null(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Navigations issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Where_query_composition_entity_equality_one_element_SingleOrDefault(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Navigations issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Where_query_composition_entity_equality_one_element_Single(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Navigations issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Where_query_composition_entity_equality_one_element_FirstOrDefault(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Navigations issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Where_query_composition_entity_equality_one_element_First(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Navigations issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Where_query_composition_entity_equality_no_elements_SingleOrDefault(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Navigations issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Where_query_composition_entity_equality_no_elements_Single(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Navigations issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Where_query_composition_entity_equality_no_elements_FirstOrDefault(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Navigations issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Where_query_composition_entity_equality_no_elements_First(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Navigations issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Where_query_composition_entity_equality_multiple_elements_SingleOrDefault(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Navigations issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Where_query_composition_entity_equality_multiple_elements_Single(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Navigations issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Where_query_composition_entity_equality_multiple_elements_FirstOrDefault(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Navigations issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Where_query_composition_entity_equality_multiple_elements_First(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Navigations issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Where_query_composition2(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Navigations issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Where_query_composition2_FirstOrDefault(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Navigations issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Where_query_composition2_FirstOrDefault_with_anonymous(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Select_Subquery_Single(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "No multi-collection query support"), MemberData(nameof(IsAsyncData))]
    public override Task Select_Where_Subquery_Deep_Single(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "No multi-collection query support"), MemberData(nameof(IsAsyncData))]
    public override Task Select_Where_Subquery_Deep_First(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "No multi-collection query support"), MemberData(nameof(IsAsyncData))]
    public override Task Select_Where_Subquery_Equality(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Where_subquery_anon(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Where_subquery_anon_nested(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task OrderBy_SelectMany(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Let_any_subquery_anonymous(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task OrderBy_arithmetic(bool _)
        => Task.CompletedTask;

    public override async Task OrderBy_condition_comparison(bool async)
    {
        await base.OrderBy_condition_comparison(async);

        AssertMql(
            """
            Products.{ "$project" : { "_id" : 0, "_document" : "$$ROOT", "_key1" : { "$gt" : [{ "$toInt" : "$UnitsInStock" }, 0] } } }, { "$sort" : { "_key1" : 1, "_document._id" : 1 } }, { "$replaceRoot" : { "newRoot" : "$_document" } }
            """);
    }

    public override async Task OrderBy_ternary_conditions(bool async)
    {
        await base.OrderBy_ternary_conditions(async);

        AssertMql(
            """
            Products.{ "$project" : { "_id" : 0, "_document" : "$$ROOT", "_key1" : { "$cond" : { "if" : { "$gt" : [{ "$toInt" : "$UnitsInStock" }, 10] }, "then" : { "$gt" : ["$_id", 40] }, "else" : { "$lte" : ["$_id", 40] } } } } }, { "$sort" : { "_key1" : 1, "_document._id" : 1 } }, { "$replaceRoot" : { "newRoot" : "$_document" } }
            """);
    }

    [ConditionalTheory(Skip = "No multi-collection query support"), MemberData(nameof(IsAsyncData))]
    public override Task OrderBy_any(bool _)
        => Task.CompletedTask;

    public override async Task Skip(bool async)
    {
        await base.Skip(async);

        AssertMql(
            """
            Customers.{ "$sort" : { "_id" : 1 } }, { "$skip" : 5 }
            """);
    }

    public override async Task Skip_no_orderby(bool async)
    {
        await base.Skip_no_orderby(async);

        AssertMql(
            """
            Customers.{ "$skip" : 5 }
            """);
    }

    public override async Task Skip_orderby_const(bool async)
    {
        await base.Skip_orderby_const(async);

        AssertMql(
            """
            Customers.{ "$project" : { "_id" : 0, "_document" : "$$ROOT", "_key1" : true } }, { "$sort" : { "_key1" : 1 } }, { "$replaceRoot" : { "newRoot" : "$_document" } }, { "$skip" : 5 }
            """);
    }

    public override async Task Skip_Take(bool async)
    {
        await base.Skip_Take(async);

        AssertMql(
            """
            Customers.{ "$sort" : { "ContactName" : 1 } }, { "$skip" : 5 }, { "$limit" : 10 }
            """);
    }

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Join_Customers_Orders_Skip_Take(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Join_Customers_Orders_Skip_Take_followed_by_constant_projection(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Join_Customers_Orders_Projection_With_String_Concat_Skip_Take(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Join_Customers_Orders_Orders_Skip_Take_Same_Properties(bool _)
        => Task.CompletedTask;

    public override async Task Ternary_should_not_evaluate_both_sides(bool async)
    {
        await base.Ternary_should_not_evaluate_both_sides(async);

        AssertMql(
            """
            Customers.{ "$project" : { "CustomerID" : "$_id", "Data1" : "none", "Data2" : "none", "Data3" : "none", "_id" : 0 } }
            """);
    }

    public override async Task Ternary_should_not_evaluate_both_sides_with_parameter(bool async)
    {
        await base.Ternary_should_not_evaluate_both_sides_with_parameter(async);

        AssertMql(
            """
            Orders.{ "$project" : { "_v" : { "$literal" : { "Data1" : true, "Data2" : true } }, "_id" : 0 } }
            """);
    }

    public override async Task Take_Skip(bool async)
    {
        await base.Take_Skip(async);

        AssertMql(
            """
            Customers.{ "$sort" : { "ContactName" : 1 } }, { "$limit" : 10 }, { "$skip" : 5 }
            """);
    }

    public override async Task Take_Skip_Distinct(bool async)
    {
        await base.Take_Skip_Distinct(async);

        AssertMql(
            """
            Customers.{ "$sort" : { "ContactName" : 1 } }, { "$limit" : 10 }, { "$skip" : 5 }, { "$group" : { "_id" : "$$ROOT" } }, { "$replaceRoot" : { "newRoot" : "$_id" } }
            """);
    }

    public override async Task Take_Skip_Distinct_Caching(bool async)
    {
        await base.Take_Skip_Distinct_Caching(async);

        AssertMql(
            """
            Customers.{ "$sort" : { "ContactName" : 1 } }, { "$limit" : 10 }, { "$skip" : 5 }, { "$group" : { "_id" : "$$ROOT" } }, { "$replaceRoot" : { "newRoot" : "$_id" } }
            """,
            //
            """
            Customers.{ "$sort" : { "ContactName" : 1 } }, { "$limit" : 15 }, { "$skip" : 10 }, { "$group" : { "_id" : "$$ROOT" } }, { "$replaceRoot" : { "newRoot" : "$_id" } }
            """);
    }

    public override async Task Take_Distinct_Count(bool async)
    {
        await base.Take_Distinct_Count(async);

        AssertMql(
            """
            Orders.{ "$limit" : 5 }, { "$group" : { "_id" : "$$ROOT" } }, { "$replaceRoot" : { "newRoot" : "$_id" } }, { "$count" : "_v" }
            """);
    }

    public override async Task Take_Where_Distinct_Count(bool async)
    {
        await base.Take_Where_Distinct_Count(async);

        AssertMql(
            """
            Orders.{ "$match" : { "CustomerID" : "FRANK" } }, { "$limit" : 5 }, { "$group" : { "_id" : "$$ROOT" } }, { "$replaceRoot" : { "newRoot" : "$_id" } }, { "$count" : "_v" }
            """);
    }

    public override async Task Queryable_simple(bool async)
    {
        await base.Queryable_simple(async);

        AssertMql(
            """
            Customers.
            """);
    }

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Queryable_simple_anonymous(bool _)
        => Task.CompletedTask;

    public override async Task Queryable_nested_simple(bool async)
    {
        await base.Queryable_nested_simple(async);

        AssertMql(
            """
            Customers.
            """);
    }

    public override async Task Queryable_simple_anonymous_projection_subquery(bool async)
    {
        await base.Queryable_simple_anonymous_projection_subquery(async);

        AssertMql(
            """
            Customers.{ "$limit" : 91 }, { "$project" : { "_v" : "$City", "_id" : 0 } }
            """);
    }

    public override async Task Queryable_simple_anonymous_subquery(bool async)
    {
        await base.Queryable_simple_anonymous_subquery(async);

        AssertMql(
            """
            Customers.{ "$limit" : 91 }
            """);
    }

    public override async Task Take_simple(bool async)
    {
        await base.Take_simple(async);

        AssertMql(
            """
            Customers.{ "$sort" : { "_id" : 1 } }, { "$limit" : 10 }
            """);
    }

    public override async Task Take_simple_parameterized(bool async)
    {
        await base.Take_simple_parameterized(async);

        AssertMql(
            """
            Customers.{ "$sort" : { "_id" : 1 } }, { "$limit" : 10 }
            """);
    }

    public override async Task Take_simple_projection(bool async)
    {
        await base.Take_simple_projection(async);

        AssertMql(
            """
            Customers.{ "$sort" : { "_id" : 1 } }, { "$limit" : 10 }, { "$project" : { "_v" : "$City", "_id" : 0 } }
            """);
    }

    public override async Task Take_subquery_projection(bool async)
    {
        await base.Take_subquery_projection(async);

        AssertMql(
            """
            Customers.{ "$sort" : { "_id" : 1 } }, { "$limit" : 2 }, { "$project" : { "_v" : "$City", "_id" : 0 } }
            """);
    }

    public override async Task OrderBy_Take_Count(bool async)
    {
        await base.OrderBy_Take_Count(async);

        AssertMql(
            """
            Orders.{ "$sort" : { "_id" : 1 } }, { "$limit" : 5 }, { "$count" : "_v" }
            """);
    }

    public override async Task Take_OrderBy_Count(bool async)
    {
        await base.Take_OrderBy_Count(async);

        AssertMql(
            """
            Orders.{ "$limit" : 5 }, { "$sort" : { "_id" : 1 } }, { "$count" : "_v" }
            """);
    }

    public override async Task Any_simple(bool async)
    {
        await base.Any_simple(async);

        AssertMql(
            """
            Customers.{ "$limit" : 1 }, { "$project" : { "_id" : 0, "_v" : null } }
            """);
    }

    public override async Task Any_predicate(bool async)
    {
        await base.Any_predicate(async);

        AssertMql(
            """
            Customers.{ "$match" : { "ContactName" : { "$regularExpression" : { "pattern" : "^A", "options" : "s" } } } }, { "$limit" : 1 }, { "$project" : { "_id" : 0, "_v" : null } }
            """);
    }

    [ConditionalTheory(Skip = "No multi-collection query support"), MemberData(nameof(IsAsyncData))]
    public override Task Any_nested_negated(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "No multi-collection query support"), MemberData(nameof(IsAsyncData))]
    public override Task Any_nested_negated2(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "No multi-collection query support"), MemberData(nameof(IsAsyncData))]
    public override Task Any_nested_negated3(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "No multi-collection query support"), MemberData(nameof(IsAsyncData))]
    public override Task Any_nested(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "No multi-collection query support"), MemberData(nameof(IsAsyncData))]
    public override Task Any_nested2(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "No multi-collection query support"), MemberData(nameof(IsAsyncData))]
    public override Task Any_nested3(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "No multi-collection query support"), MemberData(nameof(IsAsyncData))]
    public override Task Any_with_multiple_conditions_still_uses_exists(bool _)
        => Task.CompletedTask;

#if EF9
    [ConditionalTheory(Skip = "No multi-collection query support"), MemberData(nameof(IsAsyncData))]
    public override Task Any_on_distinct(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "No multi-collection query support"), MemberData(nameof(IsAsyncData))]
    public override Task Contains_on_distinct(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "No multi-collection query support"), MemberData(nameof(IsAsyncData))]
    public override Task All_on_distinct(bool _)
        => Task.CompletedTask;

#endif

    public override async Task All_top_level(bool async)
    {
        await base.All_top_level(async);

        AssertMql(
            """
            Customers.{ "$match" : { "ContactName" : { "$not" : { "$regularExpression" : { "pattern" : "^A", "options" : "s" } } } } }, { "$limit" : 1 }, { "$project" : { "_id" : 0, "_v" : null } }
            """);
    }

    public override async Task All_top_level_column(bool async)
    {
        await base.All_top_level_column(async);

        AssertMql(
            """
            Customers.{ "$match" : { "$nor" : [{ "$expr" : { "$eq" : [{ "$indexOfCP" : ["$ContactName", "$ContactName"] }, 0] } }] } }, { "$limit" : 1 }, { "$project" : { "_id" : 0, "_v" : null } }
            """);
    }

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task All_top_level_subquery(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task All_top_level_subquery_ef_property(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "SelectMany not supported"), MemberData(nameof(IsAsyncData))]
    public override Task Where_select_many_or(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "SelectMany not supported"), MemberData(nameof(IsAsyncData))]
    public override Task Where_select_many_or2(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "SelectMany not supported"), MemberData(nameof(IsAsyncData))]
    public override Task Where_select_many_or3(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "SelectMany not supported"), MemberData(nameof(IsAsyncData))]
    public override Task Where_select_many_or4(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "SelectMany not supported"), MemberData(nameof(IsAsyncData))]
    public override Task Where_select_many_or_with_parameter(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "SelectMany not supported"), MemberData(nameof(IsAsyncData))]
    public override Task SelectMany_simple_subquery(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "SelectMany not supported"), MemberData(nameof(IsAsyncData))]
    public override Task SelectMany_simple1(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "SelectMany not supported"), MemberData(nameof(IsAsyncData))]
    public override Task SelectMany_simple2(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "SelectMany not supported"), MemberData(nameof(IsAsyncData))]
    public override Task SelectMany_entity_deep(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "SelectMany not supported"), MemberData(nameof(IsAsyncData))]
    public override Task SelectMany_projection1(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "SelectMany not supported"), MemberData(nameof(IsAsyncData))]
    public override Task SelectMany_projection2(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "SelectMany not supported"), MemberData(nameof(IsAsyncData))]
    public override Task SelectMany_Count(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "SelectMany not supported"), MemberData(nameof(IsAsyncData))]
    public override Task SelectMany_LongCount(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "SelectMany not supported"), MemberData(nameof(IsAsyncData))]
    public override Task SelectMany_OrderBy_ThenBy_Any(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Join not supported"), MemberData(nameof(IsAsyncData))]
    public override Task Join_Where_Count(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "No multi-collection query support"), MemberData(nameof(IsAsyncData))]
    public override Task Where_Join_Any(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "No multi-collection query support"), MemberData(nameof(IsAsyncData))]
    public override Task Where_Join_Exists(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "No multi-collection query support"), MemberData(nameof(IsAsyncData))]
    public override Task Where_Join_Exists_Inequality(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "No multi-collection query support"), MemberData(nameof(IsAsyncData))]
    public override Task Where_Join_Exists_Constant(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "No multi-collection query support"), MemberData(nameof(IsAsyncData))]
    public override Task Where_Join_Not_Exists(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Join_OrderBy_Count(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Multiple_joins_Where_Order_Any(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Where_join_select(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Where_orderby_join_select(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Where_join_orderby_join_select(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Where_select_many(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Where_orderby_select_many(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task SelectMany_cartesian_product_with_ordering(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task SelectMany_Joined_DefaultIfEmpty(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task SelectMany_Joined_DefaultIfEmpty2(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task SelectMany_Joined_DefaultIfEmpty3(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task SelectMany_Joined_Take(bool _)
        => Task.CompletedTask;

    public override async Task Take_with_single(bool async)
    {
        await base.Take_with_single(async);

        AssertMql(
            """
            Customers.{ "$sort" : { "_id" : 1 } }, { "$limit" : 1 }, { "$limit" : 2 }
            """);
    }

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Take_with_single_select_many(bool _)
        => Task.CompletedTask;

    public override async Task Distinct_Skip(bool async)
    {
        await base.Distinct_Skip(async);

        AssertMql(
            """
            Customers.{ "$group" : { "_id" : "$$ROOT" } }, { "$replaceRoot" : { "newRoot" : "$_id" } }, { "$sort" : { "_id" : 1 } }, { "$skip" : 5 }
            """);
    }

    public override async Task Distinct_Skip_Take(bool async)
    {
        await base.Distinct_Skip_Take(async);

        AssertMql(
            """
            Customers.{ "$group" : { "_id" : "$$ROOT" } }, { "$replaceRoot" : { "newRoot" : "$_id" } }, { "$sort" : { "ContactName" : 1 } }, { "$skip" : 5 }, { "$limit" : 10 }
            """);
    }

    public override async Task Skip_Distinct(bool async)
    {
        await base.Skip_Distinct(async);

        AssertMql(
            """
            Customers.{ "$sort" : { "ContactName" : 1 } }, { "$skip" : 5 }, { "$group" : { "_id" : "$$ROOT" } }, { "$replaceRoot" : { "newRoot" : "$_id" } }
            """);
    }

    public override async Task Skip_Take_Distinct(bool async)
    {
        await base.Skip_Take_Distinct(async);

        AssertMql(
            """
            Customers.{ "$sort" : { "ContactName" : 1 } }, { "$skip" : 5 }, { "$limit" : 10 }, { "$group" : { "_id" : "$$ROOT" } }, { "$replaceRoot" : { "newRoot" : "$_id" } }
            """);
    }

    public override async Task Skip_Take_Any(bool async)
    {
        await base.Skip_Take_Any(async);

        AssertMql(
            """
            Customers.{ "$sort" : { "ContactName" : 1 } }, { "$skip" : 5 }, { "$limit" : 10 }, { "$limit" : 1 }, { "$project" : { "_id" : 0, "_v" : null } }
            """);
    }

    public override async Task Skip_Take_All(bool async)
    {
        await base.Skip_Take_All(async);

        AssertMql(
            """
            Customers.{ "$sort" : { "_id" : 1 } }, { "$skip" : 4 }, { "$limit" : 7 }, { "$match" : { "_id" : { "$not" : { "$regularExpression" : { "pattern" : "^B", "options" : "s" } } } } }, { "$limit" : 1 }, { "$project" : { "_id" : 0, "_v" : null } }
            """);
    }

    public override async Task Take_All(bool async)
    {
        await base.Take_All(async);

        AssertMql(
            """
            Customers.{ "$sort" : { "_id" : 1 } }, { "$limit" : 4 }, { "$match" : { "_id" : { "$not" : { "$regularExpression" : { "pattern" : "^A", "options" : "s" } } } } }, { "$limit" : 1 }, { "$project" : { "_id" : 0, "_v" : null } }
            """);
    }

    public override async Task Skip_Take_Any_with_predicate(bool async)
    {
        await base.Skip_Take_Any_with_predicate(async);

        AssertMql(
            """
            Customers.{ "$sort" : { "_id" : 1 } }, { "$skip" : 5 }, { "$limit" : 7 }, { "$match" : { "_id" : { "$regularExpression" : { "pattern" : "^C", "options" : "s" } } } }, { "$limit" : 1 }, { "$project" : { "_id" : 0, "_v" : null } }
            """);
    }

    public override async Task Take_Any_with_predicate(bool async)
    {
        await base.Take_Any_with_predicate(async);

        AssertMql(
            """
            Customers.{ "$sort" : { "_id" : 1 } }, { "$limit" : 5 }, { "$match" : { "_id" : { "$regularExpression" : { "pattern" : "^B", "options" : "s" } } } }, { "$limit" : 1 }, { "$project" : { "_id" : 0, "_v" : null } }
            """);
    }

    public override async Task OrderBy(bool async)
    {
        await base.OrderBy(async);

        AssertMql(
            """
            Customers.{ "$sort" : { "_id" : 1 } }
            """);
    }

    public override async Task OrderBy_true(bool async)
    {
        await base.OrderBy_true(async);

        AssertMql(
            """
            Customers.{ "$project" : { "_id" : 0, "_document" : "$$ROOT", "_key1" : true } }, { "$sort" : { "_key1" : 1 } }, { "$replaceRoot" : { "newRoot" : "$_document" } }
            """);
    }

    public override async Task OrderBy_integer(bool async)
    {
        await base.OrderBy_integer(async);

        AssertMql(
            """
            Customers.{ "$project" : { "_id" : 0, "_document" : "$$ROOT", "_key1" : 3 } }, { "$sort" : { "_key1" : 1 } }, { "$replaceRoot" : { "newRoot" : "$_document" } }
            """);
    }

    public override async Task OrderBy_parameter(bool async)
    {
        await base.OrderBy_parameter(async);

        AssertMql(
            """
            Customers.{ "$project" : { "_id" : 0, "_document" : "$$ROOT", "_key1" : 5 } }, { "$sort" : { "_key1" : 1 } }, { "$replaceRoot" : { "newRoot" : "$_document" } }
            """);
    }

    public override async Task OrderBy_anon(bool async)
    {
        await base.OrderBy_anon(async);

        AssertMql(
            """
            Customers.{ "$sort" : { "_id" : 1 } }, { "$project" : { "CustomerID" : "$_id", "_id" : 0 } }
            """);
    }

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task OrderBy_anon2(bool _)
        => Task.CompletedTask;

    public override async Task Distinct_Take(bool async)
    {
        await base.Distinct_Take(async);

        AssertMql(
            """
            Orders.{ "$group" : { "_id" : "$$ROOT" } }, { "$replaceRoot" : { "newRoot" : "$_id" } }, { "$sort" : { "_id" : 1 } }, { "$limit" : 5 }
            """);
    }

    public override async Task Distinct_Take_Count(bool async)
    {
        await base.Distinct_Take_Count(async);

        AssertMql(
            """
            Orders.{ "$group" : { "_id" : "$$ROOT" } }, { "$replaceRoot" : { "newRoot" : "$_id" } }, { "$limit" : 5 }, { "$count" : "_v" }
            """);
    }

    public override async Task OrderBy_shadow(bool async)
    {
        await base.OrderBy_shadow(async);

        AssertMql(
            """
            Employees.{ "$sort" : { "Title" : 1, "_id" : 1 } }
            """);
    }

    public override async Task OrderBy_multiple(bool async)
    {
        await base.OrderBy_multiple(async);

        AssertMql(
            """
            Customers.{ "$match" : { "_id" : { "$regularExpression" : { "pattern" : "^A", "options" : "s" } } } }, { "$sort" : { "_id" : 1 } }, { "$sort" : { "Country" : 1, "City" : 1 } }, { "$project" : { "_v" : "$City", "_id" : 0 } }
            """);
    }

    public override async Task OrderBy_ThenBy_Any(bool async)
    {
        await base.OrderBy_ThenBy_Any(async);

        AssertMql(
            """
            Customers.{ "$sort" : { "_id" : 1, "ContactName" : 1 } }, { "$limit" : 1 }, { "$project" : { "_id" : 0, "_v" : null } }
            """);
    }

    [ConditionalTheory(Skip = "Expression not supported"), MemberData(nameof(IsAsyncData))]
    public override Task OrderBy_correlated_subquery1(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "No multi-collection query support"), MemberData(nameof(IsAsyncData))]
    public override Task OrderBy_correlated_subquery2(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Navigations issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Where_subquery_recursive_trivial(bool _)
        => Task.CompletedTask;

    public override async Task Select_DTO_distinct_translated_to_server(bool async)
    {
        await base.Select_DTO_distinct_translated_to_server(async);

        AssertMql(
            """
            Orders.{ "$match" : { "_id" : { "$lt" : 10300 } } }, { "$project" : { "_v" : { "$literal" : { "_id" : null, "Count" : 0 } }, "_id" : 0 } }, { "$group" : { "_id" : "$$ROOT" } }, { "$replaceRoot" : { "newRoot" : "$_id" } }
            """);
    }

    public override async Task Select_DTO_constructor_distinct_translated_to_server(bool async)
    {
        await base.Select_DTO_constructor_distinct_translated_to_server(async);

        AssertMql(
            """
            Orders.{ "$match" : { "_id" : { "$lt" : 10300 } } }, { "$project" : { "_id" : "$CustomerID" } }, { "$group" : { "_id" : "$$ROOT" } }, { "$replaceRoot" : { "newRoot" : "$_id" } }
            """);
    }

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Select_DTO_constructor_distinct_with_navigation_translated_to_server(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Select_DTO_constructor_distinct_with_collection_projection_translated_to_server(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task
        Select_DTO_constructor_distinct_with_collection_projection_translated_to_server_with_binding_after_client_eval(bool _)
        => Task.CompletedTask;

    public override async Task Select_DTO_with_member_init_distinct_translated_to_server(bool async)
    {
        await base.Select_DTO_with_member_init_distinct_translated_to_server(async);

        AssertMql(
            """
            Orders.{ "$match" : { "_id" : { "$lt" : 10300 } } }, { "$project" : { "_id" : "$CustomerID", "Count" : "$_id" } }, { "$group" : { "_id" : "$$ROOT" } }, { "$replaceRoot" : { "newRoot" : "$_id" } }
            """);
    }

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Select_nested_collection_count_using_DTO(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Select_DTO_with_member_init_distinct_in_subquery_translated_to_server(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Select_DTO_with_member_init_distinct_in_subquery_translated_to_server_2(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Select_DTO_with_member_init_distinct_in_subquery_used_in_projection_translated_to_server(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Select_correlated_subquery_filtered(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Select_correlated_subquery_ordered(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Navigations issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Where_subquery_on_bool(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "No multi-collection query support"), MemberData(nameof(IsAsyncData))]
    public override Task Where_subquery_on_collection(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Select_many_cross_join_same_collection(bool _)
        => Task.CompletedTask;

    public override async Task OrderBy_null_coalesce_operator(bool async)
    {
        await base.OrderBy_null_coalesce_operator(async);

        AssertMql(
            """
            Customers.{ "$project" : { "_id" : 0, "_document" : "$$ROOT", "_key1" : { "$ifNull" : ["$Region", "ZZ"] } } }, { "$sort" : { "_key1" : 1, "_document._id" : 1 } }, { "$replaceRoot" : { "newRoot" : "$_document" } }
            """);
    }

    public override async Task Select_null_coalesce_operator(bool async)
    {
        await base.Select_null_coalesce_operator(async);

        AssertMql(
            """
            Customers.{ "$project" : { "_id" : 0, "_document" : "$$ROOT", "_key1" : { "$ifNull" : ["$Region", "ZZ"] } } }, { "$sort" : { "_key1" : 1, "_document._id" : 1 } }, { "$replaceRoot" : { "newRoot" : "$_document" } }, { "$project" : { "CustomerID" : "$_id", "CompanyName" : "$CompanyName", "Region" : { "$ifNull" : ["$Region", "ZZ"] }, "_id" : 0 } }
            """);
    }

    // issue #16038
    //            AssertMql(
    //                @"SELECT [c].[CustomerID], [c].[CompanyName], COALESCE([c].[Region], N'ZZ') AS [Region]
    //FROM [Customers] AS [c]
    //ORDER BY [Region], [c].[CustomerID]");
    public override async Task OrderBy_conditional_operator(bool async)
    {
        await base.OrderBy_conditional_operator(async);

        AssertMql(
            """
            Customers.{ "$project" : { "_id" : 0, "_document" : "$$ROOT", "_key1" : { "$cond" : { "if" : { "$eq" : ["$Region", null] }, "then" : "ZZ", "else" : "$Region" } } } }, { "$sort" : { "_key1" : 1, "_document._id" : 1 } }, { "$replaceRoot" : { "newRoot" : "$_document" } }
            """);
    }

    [ConditionalTheory(Skip = "Client eval in final projection EF-250"), MemberData(nameof(IsAsyncData))]
    public override Task Null_Coalesce_Short_Circuit(bool _)
        => Task.CompletedTask;

    public override async Task Null_Coalesce_Short_Circuit_with_server_correlated_leftover(bool async)
    {
        await base.Null_Coalesce_Short_Circuit_with_server_correlated_leftover(async);

        AssertMql(
            """
            Customers.{ "$project" : { "_v" : { "$literal" : { "Result" : false } }, "_id" : 0 } }
            """);
    }

    public override async Task OrderBy_conditional_operator_where_condition_false(bool async)
    {
        await base.OrderBy_conditional_operator_where_condition_false(async);

        AssertMql(
            """
            Customers.{ "$sort" : { "City" : 1 } }
            """);
    }

    public override async Task OrderBy_comparison_operator(bool async)
    {
        await base.OrderBy_comparison_operator(async);

        AssertMql(
            """
            Customers.{ "$project" : { "_id" : 0, "_document" : "$$ROOT", "_key1" : { "$eq" : ["$Region", "ASK"] } } }, { "$sort" : { "_key1" : 1 } }, { "$replaceRoot" : { "newRoot" : "$_document" } }
            """);
    }

    public override async Task Projection_null_coalesce_operator(bool async)
    {
        await base.Projection_null_coalesce_operator(async);

        AssertMql(
            """
            Customers.{ "$project" : { "CustomerID" : "$_id", "CompanyName" : "$CompanyName", "Region" : { "$ifNull" : ["$Region", "ZZ"] }, "_id" : 0 } }
            """);
    }

    public override async Task Filter_coalesce_operator(bool async)
    {
        await base.Filter_coalesce_operator(async);

#if EF8
        AssertMql(
            """
            Customers.{ "$match" : { "$expr" : { "$eq" : [{ "$ifNull" : ["$CompanyName", "$ContactName"] }, "The Big Cheese"] } } }
            """);
#else
        AssertMql(
            """
            Customers.{ "$match" : { "$expr" : { "$eq" : [{ "$ifNull" : ["$ContactName", "$CompanyName"] }, "Liz Nixon"] } } }
            """);
#endif
    }

    public override async Task Take_skip_null_coalesce_operator(bool async)
    {
        await base.Take_skip_null_coalesce_operator(async);

        AssertMql(
            """
            Customers.{ "$project" : { "_id" : 0, "_document" : "$$ROOT", "_key1" : { "$ifNull" : ["$Region", "ZZ"] } } }, { "$sort" : { "_key1" : 1 } }, { "$replaceRoot" : { "newRoot" : "$_document" } }, { "$limit" : 10 }, { "$skip" : 5 }, { "$group" : { "_id" : "$$ROOT" } }, { "$replaceRoot" : { "newRoot" : "$_id" } }
            """);
    }

    public override async Task Select_take_null_coalesce_operator(bool async)
    {
        await base.Select_take_null_coalesce_operator(async);

        AssertMql(
            """
            Customers.{ "$project" : { "_id" : 0, "_document" : "$$ROOT", "_key1" : { "$ifNull" : ["$Region", "ZZ"] } } }, { "$sort" : { "_key1" : 1 } }, { "$replaceRoot" : { "newRoot" : "$_document" } }, { "$limit" : 5 }, { "$project" : { "CustomerID" : "$_id", "CompanyName" : "$CompanyName", "Region" : { "$ifNull" : ["$Region", "ZZ"] }, "_id" : 0 } }
            """);
    }

    // issue #16038
    //            AssertMql(
    //                @"@__p_0='5'
    //SELECT TOP(@__p_0) [c].[CustomerID], [c].[CompanyName], COALESCE([c].[Region], N'ZZ') AS [Region]
    //FROM [Customers] AS [c]
    //ORDER BY [Region]");
    public override async Task Select_take_skip_null_coalesce_operator(bool async)
    {
        await base.Select_take_skip_null_coalesce_operator(async);

        AssertMql(
            """
            Customers.{ "$project" : { "_id" : 0, "_document" : "$$ROOT", "_key1" : { "$ifNull" : ["$Region", "ZZ"] } } }, { "$sort" : { "_key1" : 1 } }, { "$replaceRoot" : { "newRoot" : "$_document" } }, { "$limit" : 10 }, { "$skip" : 5 }, { "$project" : { "CustomerID" : "$_id", "CompanyName" : "$CompanyName", "Region" : { "$ifNull" : ["$Region", "ZZ"] }, "_id" : 0 } }
            """);
    }

    public override async Task Select_take_skip_null_coalesce_operator2(bool async)
    {
        await base.Select_take_skip_null_coalesce_operator2(async);

        AssertMql(
            """
            Customers.{ "$project" : { "_id" : 0, "_document" : "$$ROOT", "_key1" : { "$ifNull" : ["$Region", "ZZ"] } } }, { "$sort" : { "_key1" : 1 } }, { "$replaceRoot" : { "newRoot" : "$_document" } }, { "$limit" : 10 }, { "$skip" : 5 }, { "$project" : { "CustomerID" : "$_id", "CompanyName" : "$CompanyName", "Region" : "$Region", "_id" : 0 } }
            """);
    }

    public override async Task Select_take_skip_null_coalesce_operator3(bool async)
    {
        await base.Select_take_skip_null_coalesce_operator3(async);

        AssertMql(
            """
            Customers.{ "$project" : { "_id" : 0, "_document" : "$$ROOT", "_key1" : { "$ifNull" : ["$Region", "ZZ"] } } }, { "$sort" : { "_key1" : 1 } }, { "$replaceRoot" : { "newRoot" : "$_document" } }, { "$limit" : 10 }, { "$skip" : 5 }
            """);
    }

    public override async Task Selected_column_can_coalesce(bool async)
    {
        await base.Selected_column_can_coalesce(async);

        AssertMql(
            """
            Customers.{ "$project" : { "_id" : 0, "_document" : "$$ROOT", "_key1" : { "$ifNull" : ["$Region", "ZZ"] } } }, { "$sort" : { "_key1" : 1 } }, { "$replaceRoot" : { "newRoot" : "$_document" } }
            """);
    }

#if EF8 || EF9
    public override async Task DateTime_parse_is_inlined(bool async)
    {
        await base.DateTime_parse_is_inlined(async);

        AssertMql(
            """
            Orders.{ "$match" : { "OrderDate" : { "$gt" : { "$date" : "1998-01-01T12:00:00Z" } } } }
            """);
    }

    public override async Task DateTime_parse_is_parameterized_when_from_closure(bool async)
    {
        await base.DateTime_parse_is_parameterized_when_from_closure(async);

        AssertMql(
            """
            Orders.{ "$match" : { "OrderDate" : { "$gt" : { "$date" : "1998-01-01T12:00:00Z" } } } }
            """);
    }

    public override async Task New_DateTime_is_inlined(bool async)
    {
        await base.New_DateTime_is_inlined(async);

        AssertMql(
            """
            Orders.{ "$match" : { "OrderDate" : { "$gt" : { "$date" : "1998-01-01T12:00:00Z" } } } }
            """);
    }

    public override async Task New_DateTime_is_parameterized_when_from_closure(bool async)
    {
        await base.New_DateTime_is_parameterized_when_from_closure(async);

        AssertMql(
            """
            Orders.{ "$match" : { "OrderDate" : { "$gt" : { "$date" : "1998-01-01T12:00:00Z" } } } }
            """,
            //
            """
            Orders.{ "$match" : { "OrderDate" : { "$gt" : { "$date" : "1998-01-01T11:00:00Z" } } } }
            """);
    }

#endif

    public override async Task Environment_newline_is_funcletized(bool async)
    {
        await base.Environment_newline_is_funcletized(async);

        // Not asserting baseline since the newline character is different on different platforms.
//         AssertMql(
//             """
// Customers.{ "$match" : { "_id" : { "$regularExpression" : { "pattern" : "\\n", "options" : "s" } } } }
// """);
    }

    public override async Task Concat_string_int(bool async)
    {
        await base.Concat_string_int(async);

        AssertMql(
            """
            Orders.{ "$project" : { "_v" : { "$concat" : [{ "$toString" : "$_id" }, "$CustomerID"] }, "_id" : 0 } }
            """);
    }

    public override async Task Concat_int_string(bool async)
    {
        await base.Concat_int_string(async);

        AssertMql(
            """
            Orders.{ "$project" : { "_v" : { "$concat" : ["$CustomerID", { "$toString" : "$_id" }] }, "_id" : 0 } }
            """);
    }

    public override async Task Concat_parameter_string_int(bool async)
    {
        await base.Concat_parameter_string_int(async);

        AssertMql(
            """
            Orders.{ "$project" : { "_v" : { "$concat" : ["-", { "$toString" : "$_id" }] }, "_id" : 0 } }
            """);
    }

    public override async Task Concat_constant_string_int(bool async)
    {
        await base.Concat_constant_string_int(async);

        AssertMql(
            """
            Orders.{ "$project" : { "_v" : { "$concat" : ["-", { "$toString" : "$_id" }] }, "_id" : 0 } }
            """);
    }

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task String_concat_with_navigation1(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task String_concat_with_navigation2(bool _)
        => Task.CompletedTask;

#if EF8 || EF9
    public override async Task Select_bitwise_or(bool async)
    {
        await base.Select_bitwise_or(async);

        AssertMql(
            """
            Customers.{ "$sort" : { "_id" : 1 } }, { "$project" : { "CustomerID" : "$_id", "Value" : { "$or" : [{ "$eq" : ["$_id", "ALFKI"] }, { "$eq" : ["$_id", "ANATR"] }] }, "_id" : 0 } }
            """);
    }

    public override async Task Select_bitwise_or_multiple(bool async)
    {
        await base.Select_bitwise_or_multiple(async);

        AssertMql(
            """
            Customers.{ "$sort" : { "_id" : 1 } }, { "$project" : { "CustomerID" : "$_id", "Value" : { "$or" : [{ "$eq" : ["$_id", "ALFKI"] }, { "$eq" : ["$_id", "ANATR"] }, { "$eq" : ["$_id", "ANTON"] }] }, "_id" : 0 } }
            """);
    }

    public override async Task Select_bitwise_and(bool async)
    {
        await base.Select_bitwise_and(async);

        AssertMql(
            """
            Customers.{ "$sort" : { "_id" : 1 } }, { "$project" : { "CustomerID" : "$_id", "Value" : { "$and" : [{ "$eq" : ["$_id", "ALFKI"] }, { "$eq" : ["$_id", "ANATR"] }] }, "_id" : 0 } }
            """);
    }

    public override async Task Select_bitwise_and_or(bool async)
    {
        await base.Select_bitwise_and_or(async);

        AssertMql(
            """
            Customers.{ "$sort" : { "_id" : 1 } }, { "$project" : { "CustomerID" : "$_id", "Value" : { "$or" : [{ "$and" : [{ "$eq" : ["$_id", "ALFKI"] }, { "$eq" : ["$_id", "ANATR"] }] }, { "$eq" : ["$_id", "ANTON"] }] }, "_id" : 0 } }
            """);
    }

    public override async Task Where_bitwise_or_with_logical_or(bool async)
    {
        await base.Where_bitwise_or_with_logical_or(async);

        AssertMql(
            """
            Customers.{ "$match" : { "$or" : [{ "_id" : "ALFKI" }, { "_id" : "ANATR" }, { "_id" : "ANTON" }] } }
            """);
    }

    public override async Task Where_bitwise_and_with_logical_and(bool async)
    {
        await base.Where_bitwise_and_with_logical_and(async);

        AssertMql(
            """
            Customers.{ "$match" : { "$and" : [{ "_id" : "ALFKI" }, { "_id" : "ANATR" }, { "_id" : "ANTON" }] } }
            """);
    }

    public override async Task Where_bitwise_or_with_logical_and(bool async)
    {
        await base.Where_bitwise_or_with_logical_and(async);

        AssertMql(
            """
            Customers.{ "$match" : { "$and" : [{ "$or" : [{ "_id" : "ALFKI" }, { "_id" : "ANATR" }] }, { "Country" : "Germany" }] } }
            """);
    }

    public override async Task Where_bitwise_and_with_logical_or(bool async)
    {
        await base.Where_bitwise_and_with_logical_or(async);

        AssertMql(
            """
            Customers.{ "$match" : { "$or" : [{ "$and" : [{ "_id" : "ALFKI" }, { "_id" : "ANATR" }] }, { "_id" : "ANTON" }] } }
            """);
    }

    public override async Task Where_bitwise_binary_not(bool async)
    {
        if (!TestServer.SupportsBitwiseOperators)
        {
            return;
        }

        await base.Where_bitwise_binary_not(async);

        AssertMql(
            """
            Orders.{ "$match" : { "$expr" : { "$eq" : [{ "$bitNot" : "$_id" }, -10249] } } }
            """);
    }

    public override async Task Where_bitwise_binary_and(bool async)
    {
        await base.Where_bitwise_binary_and(async);

        AssertMql(
            """
            Orders.{ "$match" : { "_id" : { "$bitsAllSet" : 10248 } } }
            """);
    }

    public override async Task Where_bitwise_binary_or(bool async)
    {
        if (!TestServer.SupportsBitwiseOperators)
        {
            return;
        }

        await base.Where_bitwise_binary_or(async);

        AssertMql(
            """
            Orders.{ "$match" : { "$expr" : { "$eq" : [{ "$bitOr" : ["$_id", 10248] }, 10248] } } }
            """);
    }

#if EF9
    public override async Task Where_bitwise_binary_xor(bool async)
    {
        if (!TestServer.SupportsBitwiseOperators)
        {
            return;
        }

        await base.Where_bitwise_binary_xor(async);

        AssertMql(
            """
Orders.{ "$match" : { "$expr" : { "$eq" : [{ "$bitXor" : ["$_id", 1] }, 10249] } } }
""");
    }

#endif

    public override async Task Select_bitwise_or_with_logical_or(bool async)
    {
        await base.Select_bitwise_or_with_logical_or(async);

        AssertMql(
            """
            Customers.{ "$sort" : { "_id" : 1 } }, { "$project" : { "CustomerID" : "$_id", "Value" : { "$or" : [{ "$eq" : ["$_id", "ALFKI"] }, { "$eq" : ["$_id", "ANATR"] }, { "$eq" : ["$_id", "ANTON"] }] }, "_id" : 0 } }
            """);
    }

    public override async Task Select_bitwise_and_with_logical_and(bool async)
    {
        await base.Select_bitwise_and_with_logical_and(async);

        AssertMql(
            """
            Customers.{ "$sort" : { "_id" : 1 } }, { "$project" : { "CustomerID" : "$_id", "Value" : { "$and" : [{ "$eq" : ["$_id", "ALFKI"] }, { "$eq" : ["$_id", "ANATR"] }, { "$eq" : ["$_id", "ANTON"] }] }, "_id" : 0 } }
            """);
    }

#endif

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Handle_materialization_properly_when_more_than_two_query_sources_are_involved(bool _)
        => Task.CompletedTask;

    public override async Task Parameter_extraction_short_circuits_1(bool async)
    {
        await base.Parameter_extraction_short_circuits_1(async);

        AssertMql(
            """
            Orders.{ "$match" : { "$and" : [{ "_id" : { "$lt" : 10400 } }, { "OrderDate" : { "$ne" : null } }, { "$expr" : { "$eq" : [{ "$month" : "$OrderDate" }, 7] } }, { "$expr" : { "$eq" : [{ "$year" : "$OrderDate" }, 1996] } }] } }
            """,
            //
            """
            Orders.{ "$match" : { "_id" : { "$lt" : 10400 } } }
            """);
    }

    public override async Task Parameter_extraction_short_circuits_2(bool async)
    {
        await base.Parameter_extraction_short_circuits_2(async);

        AssertMql(
            """
            Orders.{ "$match" : { "$and" : [{ "_id" : { "$lt" : 10400 } }, { "OrderDate" : { "$ne" : null } }, { "$expr" : { "$eq" : [{ "$month" : "$OrderDate" }, 7] } }, { "$expr" : { "$eq" : [{ "$year" : "$OrderDate" }, 1996] } }] } }
            """,
            //
            """
            Orders.{ "$match" : { "_id" : { "$type" : -1 } } }
            """);
    }

    public override async Task Parameter_extraction_short_circuits_3(bool async)
    {
        await base.Parameter_extraction_short_circuits_3(async);

        AssertMql(
            """
            Orders.{ "$match" : { "$or" : [{ "_id" : { "$lt" : 10400 } }, { "$and" : [{ "OrderDate" : { "$ne" : null } }, { "$expr" : { "$eq" : [{ "$month" : "$OrderDate" }, 7] } }, { "$expr" : { "$eq" : [{ "$year" : "$OrderDate" }, 1996] } }] }] } }
            """,
            //
            """
            Orders.
            """);
    }

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Subquery_member_pushdown_does_not_change_original_subquery_model(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Subquery_member_pushdown_does_not_change_original_subquery_model2(bool _)
        => Task.CompletedTask;

    public override async Task Query_expression_with_to_string_and_contains(bool async)
    {
        await base.Query_expression_with_to_string_and_contains(async);

        AssertMql(
            """
            Orders.{ "$match" : { "$and" : [{ "OrderDate" : { "$ne" : null } }, { "$expr" : { "$gte" : [{ "$indexOfCP" : [{ "$toString" : "$EmployeeID" }, "7"] }, 0] } }] } }, { "$project" : { "CustomerID" : "$CustomerID", "_id" : 0 } }
            """);
    }

    [ConditionalTheory(Skip = "Client eval in final projection EF-250"), MemberData(nameof(IsAsyncData))]
    public override Task Select_expression_long_to_string(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Client eval in final projection EF-250"), MemberData(nameof(IsAsyncData))]
    public override Task Select_expression_int_to_string(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Client eval in final projection EF-250"), MemberData(nameof(IsAsyncData))]
    public override Task ToString_with_formatter_is_evaluated_on_the_client(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Client eval in final projection EF-250"), MemberData(nameof(IsAsyncData))]
    public override Task Select_expression_other_to_string(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Select_expression_date_add_year(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Select_expression_datetime_add_month(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Select_expression_datetime_add_hour(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Select_expression_datetime_add_minute(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Select_expression_datetime_add_second(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Select_expression_date_add_milliseconds_above_the_range(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Select_expression_date_add_milliseconds_below_the_range(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Select_expression_date_add_milliseconds_large_number_divided(bool _)
        => Task.CompletedTask;

    public override async Task Add_minutes_on_constant_value(bool async)
    {
        await base.Add_minutes_on_constant_value(async);

        AssertMql(
            """
            Orders.{ "$match" : { "_id" : { "$lt" : 10500 } } }, { "$sort" : { "_id" : 1 } }, { "$project" : { "Test" : { "$dateAdd" : { "startDate" : { "$date" : { "$numberLong" : "-2208988800000" } }, "unit" : "minute", "amount" : { "$mod" : ["$_id", 25] } } }, "_id" : 0 } }
            """);
    }

    public override async Task Select_expression_references_are_updated_correctly_with_subquery(bool async)
    {
        await base.Select_expression_references_are_updated_correctly_with_subquery(async);

        AssertMql(
            """
            Orders.{ "$match" : { "OrderDate" : { "$ne" : null } } }, { "$project" : { "_v" : { "$year" : "$OrderDate" }, "_id" : 0 } }, { "$group" : { "_id" : "$$ROOT" } }, { "$replaceRoot" : { "newRoot" : "$_id" } }, { "$match" : { "_v" : { "$lt" : 2017 } } }
            """);
    }

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task DefaultIfEmpty_in_subquery(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task DefaultIfEmpty_in_subquery_not_correlated(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task DefaultIfEmpty_in_subquery_nested(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task DefaultIfEmpty_in_subquery_nested_filter_order_comparison(bool _)
        => Task.CompletedTask;

    public override async Task OrderBy_skip_take(bool async)
    {
        await base.OrderBy_skip_take(async);

        AssertMql(
            """
            Customers.{ "$sort" : { "ContactTitle" : 1, "ContactName" : 1 } }, { "$skip" : 5 }, { "$limit" : 8 }
            """);
    }

    public override async Task OrderBy_skip_skip_take(bool async)
    {
        await base.OrderBy_skip_skip_take(async);

        AssertMql(
            """
            Customers.{ "$sort" : { "ContactTitle" : 1, "ContactName" : 1 } }, { "$skip" : 5 }, { "$skip" : 8 }, { "$limit" : 3 }
            """);
    }

    public override async Task OrderBy_skip_take_take(bool async)
    {
        await base.OrderBy_skip_take_take(async);

        AssertMql(
            """
            Customers.{ "$sort" : { "ContactTitle" : 1, "ContactName" : 1 } }, { "$skip" : 5 }, { "$limit" : 8 }, { "$limit" : 3 }
            """);
    }

    public override async Task OrderBy_skip_take_take_take_take(bool async)
    {
        await base.OrderBy_skip_take_take_take_take(async);

        AssertMql(
            """
            Customers.{ "$sort" : { "ContactTitle" : 1, "ContactName" : 1 } }, { "$skip" : 5 }, { "$limit" : 15 }, { "$limit" : 10 }, { "$limit" : 8 }, { "$limit" : 5 }
            """);
    }

    public override async Task OrderBy_skip_take_skip_take_skip(bool async)
    {
        await base.OrderBy_skip_take_skip_take_skip(async);

        AssertMql(
            """
            Customers.{ "$sort" : { "ContactTitle" : 1, "ContactName" : 1 } }, { "$skip" : 5 }, { "$limit" : 15 }, { "$skip" : 2 }, { "$limit" : 8 }, { "$skip" : 5 }
            """);
    }

    public override async Task OrderBy_skip_take_distinct(bool async)
    {
        await base.OrderBy_skip_take_distinct(async);

        AssertMql(
            """
            Customers.{ "$sort" : { "ContactTitle" : 1, "ContactName" : 1 } }, { "$skip" : 5 }, { "$limit" : 15 }, { "$group" : { "_id" : "$$ROOT" } }, { "$replaceRoot" : { "newRoot" : "$_id" } }
            """);
    }

    public override async Task OrderBy_coalesce_take_distinct(bool async)
    {
        await base.OrderBy_coalesce_take_distinct(async);

        AssertMql(
            """
            Products.{ "$project" : { "_id" : 0, "_document" : "$$ROOT", "_key1" : { "$ifNull" : ["$UnitPrice", { "$numberDecimal" : "0" }] } } }, { "$sort" : { "_key1" : 1 } }, { "$replaceRoot" : { "newRoot" : "$_document" } }, { "$limit" : 15 }, { "$group" : { "_id" : "$$ROOT" } }, { "$replaceRoot" : { "newRoot" : "$_id" } }
            """);
    }

    public override async Task OrderBy_coalesce_skip_take_distinct(bool async)
    {
        await base.OrderBy_coalesce_skip_take_distinct(async);

        AssertMql(
            """
            Products.{ "$project" : { "_id" : 0, "_document" : "$$ROOT", "_key1" : { "$ifNull" : ["$UnitPrice", { "$numberDecimal" : "0" }] } } }, { "$sort" : { "_key1" : 1 } }, { "$replaceRoot" : { "newRoot" : "$_document" } }, { "$skip" : 5 }, { "$limit" : 15 }, { "$group" : { "_id" : "$$ROOT" } }, { "$replaceRoot" : { "newRoot" : "$_id" } }
            """);
    }

    public override async Task OrderBy_coalesce_skip_take_distinct_take(bool async)
    {
        await base.OrderBy_coalesce_skip_take_distinct_take(async);

        AssertMql(
            """
            Products.{ "$project" : { "_id" : 0, "_document" : "$$ROOT", "_key1" : { "$ifNull" : ["$UnitPrice", { "$numberDecimal" : "0" }] } } }, { "$sort" : { "_key1" : 1 } }, { "$replaceRoot" : { "newRoot" : "$_document" } }, { "$skip" : 5 }, { "$limit" : 15 }, { "$group" : { "_id" : "$$ROOT" } }, { "$replaceRoot" : { "newRoot" : "$_id" } }, { "$limit" : 5 }
            """);
    }

    public override async Task OrderBy_skip_take_distinct_orderby_take(bool async)
    {
        await base.OrderBy_skip_take_distinct_orderby_take(async);

        AssertMql(
            """
            Customers.{ "$sort" : { "ContactTitle" : 1, "ContactName" : 1 } }, { "$skip" : 5 }, { "$limit" : 15 }, { "$group" : { "_id" : "$$ROOT" } }, { "$replaceRoot" : { "newRoot" : "$_id" } }, { "$sort" : { "ContactTitle" : 1 } }, { "$limit" : 8 }
            """);
    }

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task No_orderby_added_for_fully_translated_manually_constructed_LOJ(bool _)
        => Task.CompletedTask;

    public override async Task No_orderby_added_for_client_side_GroupJoin_dependent_to_principal_LOJ(bool async)
    {
        await base.No_orderby_added_for_client_side_GroupJoin_dependent_to_principal_LOJ(async);

        AssertMql();
    }

    public override async Task
        No_orderby_added_for_client_side_GroupJoin_dependent_to_principal_LOJ_with_additional_join_condition1(
            bool async)
    {
        await base.No_orderby_added_for_client_side_GroupJoin_dependent_to_principal_LOJ_with_additional_join_condition1(async);

        AssertMql();
    }

    public override async Task
        No_orderby_added_for_client_side_GroupJoin_dependent_to_principal_LOJ_with_additional_join_condition2(
            bool async)
    {
        await base.No_orderby_added_for_client_side_GroupJoin_dependent_to_principal_LOJ_with_additional_join_condition2(async);

        AssertMql();
    }

    public override async Task Orderby_added_for_client_side_GroupJoin_principal_to_dependent_LOJ(bool async)
    {
        await base.Orderby_added_for_client_side_GroupJoin_principal_to_dependent_LOJ(async);

        AssertMql();
    }

    public override async Task Contains_with_DateTime_Date(bool async)
    {
        // Force the DateTime to be UTC since Mongo will otherwise convert from local to UTC on insertion
        var dates = new[]
        {
            new DateTime(1996, 07, 04, 0, 0, 0, DateTimeKind.Utc), new DateTime(1996, 07, 16, 0, 0, 0, DateTimeKind.Utc)
        };

        await AssertQuery(
            async,
            ss => ss.Set<Order>().Where(e => dates.Contains(e.OrderDate!.Value.Date)));

        dates = [new DateTime(1996, 07, 04, 0, 0, 0, DateTimeKind.Utc)];

        await AssertQuery(
            async,
            ss => ss.Set<Order>().Where(e => dates.Contains(e.OrderDate!.Value.Date)));

        //await base.Contains_with_DateTime_Date(async);

        AssertMql(
            """
            Orders.{ "$match" : { "$expr" : { "$in" : [{ "$dateTrunc" : { "date" : "$OrderDate", "unit" : "day" } }, [{ "$date" : "1996-07-04T00:00:00Z" }, { "$date" : "1996-07-16T00:00:00Z" }]] } } }
            """,
            //
            """
            Orders.{ "$match" : { "$expr" : { "$in" : [{ "$dateTrunc" : { "date" : "$OrderDate", "unit" : "day" } }, [{ "$date" : "1996-07-04T00:00:00Z" }]] } } }
            """);
    }

    [ConditionalTheory(Skip = "No multi-collection query support"), MemberData(nameof(IsAsyncData))]
    public override Task Contains_with_subquery_involving_join_binds_to_correct_table(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Expression not supported"), MemberData(nameof(IsAsyncData))]
    public override Task Complex_query_with_repeated_query_model_compiles_correctly(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Expression not supported"), MemberData(nameof(IsAsyncData))]
    public override Task Complex_query_with_repeated_nested_query_model_compiles_correctly(bool _)
        => Task.CompletedTask;

    public override async Task Anonymous_member_distinct_where(bool async)
    {
        await base.Anonymous_member_distinct_where(async);

        AssertMql(
            """
            Customers.{ "$project" : { "CustomerID" : "$_id", "_id" : 0 } }, { "$group" : { "_id" : "$$ROOT" } }, { "$replaceRoot" : { "newRoot" : "$_id" } }, { "$match" : { "CustomerID" : "ALFKI" } }
            """);
    }

    public override async Task Anonymous_member_distinct_orderby(bool async)
    {
        await base.Anonymous_member_distinct_orderby(async);

        AssertMql(
            """
            Customers.{ "$project" : { "CustomerID" : "$_id", "_id" : 0 } }, { "$group" : { "_id" : "$$ROOT" } }, { "$replaceRoot" : { "newRoot" : "$_id" } }, { "$sort" : { "CustomerID" : 1 } }
            """);
    }

    public override async Task Anonymous_member_distinct_result(bool async)
    {
        await base.Anonymous_member_distinct_result(async);

        AssertMql(
            """
            Customers.{ "$project" : { "CustomerID" : "$_id", "_id" : 0 } }, { "$group" : { "_id" : "$$ROOT" } }, { "$replaceRoot" : { "newRoot" : "$_id" } }, { "$match" : { "CustomerID" : { "$regularExpression" : { "pattern" : "^A", "options" : "s" } } } }, { "$count" : "_v" }
            """);
    }

    public override async Task Anonymous_complex_distinct_where(bool async)
    {
        await base.Anonymous_complex_distinct_where(async);

        AssertMql(
            """
            Customers.{ "$project" : { "A" : { "$concat" : ["$_id", "$City"] }, "_id" : 0 } }, { "$group" : { "_id" : "$$ROOT" } }, { "$replaceRoot" : { "newRoot" : "$_id" } }, { "$match" : { "A" : "ALFKIBerlin" } }
            """);
    }

    public override async Task Anonymous_complex_distinct_orderby(bool async)
    {
        await base.Anonymous_complex_distinct_orderby(async);

        AssertMql(
            """
            Customers.{ "$project" : { "A" : { "$concat" : ["$_id", "$City"] }, "_id" : 0 } }, { "$group" : { "_id" : "$$ROOT" } }, { "$replaceRoot" : { "newRoot" : "$_id" } }, { "$sort" : { "A" : 1 } }
            """);
    }

    public override async Task Anonymous_complex_distinct_result(bool async)
    {
        await base.Anonymous_complex_distinct_result(async);

        AssertMql(
            """
            Customers.{ "$project" : { "A" : { "$concat" : ["$_id", "$City"] }, "_id" : 0 } }, { "$group" : { "_id" : "$$ROOT" } }, { "$replaceRoot" : { "newRoot" : "$_id" } }, { "$match" : { "A" : { "$regularExpression" : { "pattern" : "^A", "options" : "s" } } } }, { "$count" : "_v" }
            """);
    }

    public override async Task Anonymous_complex_orderby(bool async)
    {
        await base.Anonymous_complex_orderby(async);

        AssertMql(
            """
            Customers.{ "$project" : { "_id" : 0, "_document" : "$$ROOT", "_key1" : { "$concat" : ["$_id", "$City"] } } }, { "$sort" : { "_key1" : 1 } }, { "$replaceRoot" : { "newRoot" : "$_document" } }, { "$project" : { "A" : { "$concat" : ["$_id", "$City"] }, "_id" : 0 } }
            """);
    }

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Anonymous_subquery_orderby(bool _)
        => Task.CompletedTask;

    public override async Task DTO_member_distinct_where(bool async)
    {
        await base.DTO_member_distinct_where(async);

        AssertMql(
            """
            Customers.{ "$project" : { "Property" : "$_id", "_id" : 0 } }, { "$group" : { "_id" : "$$ROOT" } }, { "$replaceRoot" : { "newRoot" : "$_id" } }, { "$match" : { "Property" : "ALFKI" } }
            """);
    }

    public override async Task DTO_member_distinct_orderby(bool async)
    {
        await base.DTO_member_distinct_orderby(async);

        AssertMql(
            """
            Customers.{ "$project" : { "Property" : "$_id", "_id" : 0 } }, { "$group" : { "_id" : "$$ROOT" } }, { "$replaceRoot" : { "newRoot" : "$_id" } }, { "$sort" : { "Property" : 1 } }
            """);
    }

    public override async Task DTO_member_distinct_result(bool async)
    {
        await base.DTO_member_distinct_result(async);

        AssertMql(
            """
            Customers.{ "$project" : { "Property" : "$_id", "_id" : 0 } }, { "$group" : { "_id" : "$$ROOT" } }, { "$replaceRoot" : { "newRoot" : "$_id" } }, { "$match" : { "Property" : { "$regularExpression" : { "pattern" : "^A", "options" : "s" } } } }, { "$count" : "_v" }
            """);
    }

    public override async Task DTO_complex_distinct_where(bool async)
    {
        await base.DTO_complex_distinct_where(async);

        AssertMql(
            """
            Customers.{ "$project" : { "Property" : { "$concat" : ["$_id", "$City"] }, "_id" : 0 } }, { "$group" : { "_id" : "$$ROOT" } }, { "$replaceRoot" : { "newRoot" : "$_id" } }, { "$match" : { "Property" : "ALFKIBerlin" } }
            """);
    }

    public override async Task DTO_complex_distinct_orderby(bool async)
    {
        await base.DTO_complex_distinct_orderby(async);

        AssertMql(
            """
            Customers.{ "$project" : { "Property" : { "$concat" : ["$_id", "$City"] }, "_id" : 0 } }, { "$group" : { "_id" : "$$ROOT" } }, { "$replaceRoot" : { "newRoot" : "$_id" } }, { "$sort" : { "Property" : 1 } }
            """);
    }

    public override async Task DTO_complex_distinct_result(bool async)
    {
        await base.DTO_complex_distinct_result(async);

        AssertMql(
            """
            Customers.{ "$project" : { "Property" : { "$concat" : ["$_id", "$City"] }, "_id" : 0 } }, { "$group" : { "_id" : "$$ROOT" } }, { "$replaceRoot" : { "newRoot" : "$_id" } }, { "$match" : { "Property" : { "$regularExpression" : { "pattern" : "^A", "options" : "s" } } } }, { "$count" : "_v" }
            """);
    }

    public override async Task DTO_complex_orderby(bool async)
    {
        await base.DTO_complex_orderby(async);

        AssertMql(
            """
            Customers.{ "$project" : { "_id" : 0, "_document" : "$$ROOT", "_key1" : { "$concat" : ["$_id", "$City"] } } }, { "$sort" : { "_key1" : 1 } }, { "$replaceRoot" : { "newRoot" : "$_document" } }, { "$project" : { "Property" : { "$concat" : ["$_id", "$City"] }, "_id" : 0 } }
            """);
    }

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task DTO_subquery_orderby(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Include_with_orderby_skip_preserves_ordering(bool _)
        => Task.CompletedTask;

    public override async Task Int16_parameter_can_be_used_for_int_column(bool async)
    {
        await base.Int16_parameter_can_be_used_for_int_column(async);

        AssertMql(
            """
            Orders.{ "$match" : { "_id" : 10300 } }
            """);
    }

    [ConditionalTheory(Skip = "No multi-collection query support"), MemberData(nameof(IsAsyncData))]
    public override Task Subquery_is_null_translated_correctly(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "No multi-collection query support"), MemberData(nameof(IsAsyncData))]
    public override Task Subquery_is_not_null_translated_correctly(bool _)
        => Task.CompletedTask;

    public override async Task Select_take_average(bool async)
    {
        await base.Select_take_average(async);

        AssertMql(
            """
            Orders.{ "$sort" : { "_id" : 1 } }, { "$limit" : 10 }, { "$project" : { "_v" : "$_id", "_id" : 0 } }, { "$group" : { "_id" : null, "_v" : { "$avg" : "$_v" } } }, { "$project" : { "_id" : 0 } }
            """);
    }

    public override async Task Select_take_count(bool async)
    {
        await base.Select_take_count(async);

        AssertMql(
            """
            Customers.{ "$limit" : 7 }, { "$count" : "_v" }
            """);
    }

    public override async Task Select_orderBy_take_count(bool async)
    {
        await base.Select_orderBy_take_count(async);

        AssertMql(
            """
            Customers.{ "$sort" : { "Country" : 1 } }, { "$limit" : 7 }, { "$count" : "_v" }
            """);
    }

    public override async Task Select_take_long_count(bool async)
    {
        await base.Select_take_long_count(async);

        AssertMql(
            """
            Customers.{ "$limit" : 7 }, { "$count" : "_v" }
            """);
    }

    public override async Task Select_orderBy_take_long_count(bool async)
    {
        await base.Select_orderBy_take_long_count(async);

        AssertMql(
            """
            Customers.{ "$sort" : { "Country" : 1 } }, { "$limit" : 7 }, { "$count" : "_v" }
            """);
    }

    public override async Task Select_take_max(bool async)
    {
        await base.Select_take_max(async);

        AssertMql(
            """
            Orders.{ "$sort" : { "_id" : 1 } }, { "$limit" : 10 }, { "$project" : { "_v" : "$_id", "_id" : 0 } }, { "$group" : { "_id" : null, "_max" : { "$max" : "$$ROOT" } } }, { "$replaceRoot" : { "newRoot" : "$_max" } }
            """);
    }

    public override async Task Select_take_min(bool async)
    {
        await base.Select_take_min(async);

        AssertMql(
            """
            Orders.{ "$sort" : { "_id" : 1 } }, { "$limit" : 10 }, { "$project" : { "_v" : "$_id", "_id" : 0 } }, { "$group" : { "_id" : null, "_min" : { "$min" : "$$ROOT" } } }, { "$replaceRoot" : { "newRoot" : "$_min" } }
            """);
    }

    public override async Task Select_take_sum(bool async)
    {
        await base.Select_take_sum(async);

        AssertMql(
            """
            Orders.{ "$sort" : { "_id" : 1 } }, { "$limit" : 10 }, { "$project" : { "_v" : "$_id", "_id" : 0 } }, { "$group" : { "_id" : null, "_v" : { "$sum" : "$_v" } } }, { "$project" : { "_id" : 0 } }
            """);
    }

    public override async Task Select_skip_average(bool async)
    {
        await base.Select_skip_average(async);

        AssertMql(
            """
            Orders.{ "$sort" : { "_id" : 1 } }, { "$skip" : 10 }, { "$project" : { "_v" : "$_id", "_id" : 0 } }, { "$group" : { "_id" : null, "_v" : { "$avg" : "$_v" } } }, { "$project" : { "_id" : 0 } }
            """);
    }

    public override async Task Select_skip_count(bool async)
    {
        await base.Select_skip_count(async);

        AssertMql(
            """
            Customers.{ "$skip" : 7 }, { "$count" : "_v" }
            """);
    }

    public override async Task Select_orderBy_skip_count(bool async)
    {
        await base.Select_orderBy_skip_count(async);

        AssertMql(
            """
            Customers.{ "$sort" : { "Country" : 1 } }, { "$skip" : 7 }, { "$count" : "_v" }
            """);
    }

    public override async Task Select_skip_long_count(bool async)
    {
        await base.Select_skip_long_count(async);

        AssertMql(
            """
            Customers.{ "$skip" : 7 }, { "$count" : "_v" }
            """);
    }

    public override async Task Select_orderBy_skip_long_count(bool async)
    {
        await base.Select_orderBy_skip_long_count(async);

        AssertMql(
            """
            Customers.{ "$sort" : { "Country" : 1 } }, { "$skip" : 7 }, { "$count" : "_v" }
            """);
    }

    public override async Task Select_skip_max(bool async)
    {
        await base.Select_skip_max(async);

        AssertMql(
            """
            Orders.{ "$sort" : { "_id" : 1 } }, { "$skip" : 10 }, { "$project" : { "_v" : "$_id", "_id" : 0 } }, { "$group" : { "_id" : null, "_max" : { "$max" : "$$ROOT" } } }, { "$replaceRoot" : { "newRoot" : "$_max" } }
            """);
    }

    public override async Task Select_skip_min(bool async)
    {
        await base.Select_skip_min(async);

        AssertMql(
            """
            Orders.{ "$sort" : { "_id" : 1 } }, { "$skip" : 10 }, { "$project" : { "_v" : "$_id", "_id" : 0 } }, { "$group" : { "_id" : null, "_min" : { "$min" : "$$ROOT" } } }, { "$replaceRoot" : { "newRoot" : "$_min" } }
            """);
    }

    public override async Task Select_skip_sum(bool async)
    {
        await base.Select_skip_sum(async);

        AssertMql(
            """
            Orders.{ "$sort" : { "_id" : 1 } }, { "$skip" : 10 }, { "$project" : { "_v" : "$_id", "_id" : 0 } }, { "$group" : { "_id" : null, "_v" : { "$sum" : "$_v" } } }, { "$project" : { "_id" : 0 } }
            """);
    }

    public override async Task Select_distinct_average(bool async)
    {
        await base.Select_distinct_average(async);

        AssertMql(
            """
            Orders.{ "$project" : { "_v" : "$_id", "_id" : 0 } }, { "$group" : { "_id" : "$$ROOT" } }, { "$replaceRoot" : { "newRoot" : "$_id" } }, { "$group" : { "_id" : null, "_v" : { "$avg" : "$_v" } } }, { "$project" : { "_id" : 0 } }
            """);
    }

    public override async Task Select_distinct_count(bool async)
    {
        await base.Select_distinct_count(async);

        AssertMql(
            """
            Customers.{ "$group" : { "_id" : "$$ROOT" } }, { "$replaceRoot" : { "newRoot" : "$_id" } }, { "$count" : "_v" }
            """);
    }

    public override async Task Select_distinct_long_count(bool async)
    {
        await base.Select_distinct_long_count(async);

        AssertMql(
            """
            Customers.{ "$group" : { "_id" : "$$ROOT" } }, { "$replaceRoot" : { "newRoot" : "$_id" } }, { "$count" : "_v" }
            """);
    }

    public override async Task Select_distinct_max(bool async)
    {
        await base.Select_distinct_max(async);

        AssertMql(
            """
            Orders.{ "$project" : { "_v" : "$_id", "_id" : 0 } }, { "$group" : { "_id" : "$$ROOT" } }, { "$replaceRoot" : { "newRoot" : "$_id" } }, { "$group" : { "_id" : null, "_max" : { "$max" : "$$ROOT" } } }, { "$replaceRoot" : { "newRoot" : "$_max" } }
            """);
    }

    public override async Task Select_distinct_min(bool async)
    {
        await base.Select_distinct_min(async);

        AssertMql(
            """
            Orders.{ "$project" : { "_v" : "$_id", "_id" : 0 } }, { "$group" : { "_id" : "$$ROOT" } }, { "$replaceRoot" : { "newRoot" : "$_id" } }, { "$group" : { "_id" : null, "_min" : { "$min" : "$$ROOT" } } }, { "$replaceRoot" : { "newRoot" : "$_min" } }
            """);
    }

    public override async Task Select_distinct_sum(bool async)
    {
        await base.Select_distinct_sum(async);

        AssertMql(
            """
            Orders.{ "$project" : { "_v" : "$_id", "_id" : 0 } }, { "$group" : { "_id" : "$$ROOT" } }, { "$replaceRoot" : { "newRoot" : "$_id" } }, { "$group" : { "_id" : null, "_v" : { "$sum" : "$_v" } } }, { "$project" : { "_id" : 0 } }
            """);
    }

    public override async Task Comparing_to_fixed_string_parameter(bool async)
    {
        await base.Comparing_to_fixed_string_parameter(async);

        AssertMql(
            """
            Customers.{ "$match" : { "_id" : { "$regularExpression" : { "pattern" : "^A", "options" : "s" } } } }, { "$project" : { "_v" : "$_id", "_id" : 0 } }
            """);
    }

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Comparing_entities_using_Equals(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Comparing_different_entity_types_using_Equals(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Comparing_entity_to_null_using_Equals(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Comparing_navigations_using_Equals(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Comparing_navigations_using_static_Equals(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Comparing_non_matching_entities_using_Equals(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Comparing_non_matching_collection_navigations_using_Equals(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "No multi-collection query support"), MemberData(nameof(IsAsyncData))]
    public override Task Comparing_collection_navigation_to_null(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Comparing_collection_navigation_to_null_complex(bool _)
        => Task.CompletedTask;

    public override async Task Compare_collection_navigation_with_itself(bool async)
    {
        await base.Compare_collection_navigation_with_itself(async);

        AssertMql(
            """
            Customers.{ "$match" : { "_id" : { "$regularExpression" : { "pattern" : "^A", "options" : "s" } } } }, { "$match" : { "$expr" : { "$eq" : ["$$ROOT", "$$ROOT"] } } }, { "$project" : { "_v" : "$_id", "_id" : 0 } }
            """);
    }

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Compare_two_collection_navigations_with_different_query_sources(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Compare_two_collection_navigations_using_equals(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Compare_two_collection_navigations_with_different_property_chains(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Multiple ordering issue EF-253"), MemberData(nameof(IsAsyncData))]
    public override Task OrderBy_ThenBy_same_column_different_direction(bool _)
        => Task.CompletedTask;

    public override async Task OrderBy_OrderBy_same_column_different_direction(bool async)
    {
        await base.OrderBy_OrderBy_same_column_different_direction(async);

        AssertMql(
            """
            Customers.{ "$match" : { "_id" : { "$regularExpression" : { "pattern" : "^A", "options" : "s" } } } }, { "$sort" : { "_id" : 1 } }, { "$sort" : { "_id" : -1 } }, { "$project" : { "_v" : "$_id", "_id" : 0 } }
            """);
    }

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Complex_nested_query_doesnt_try_binding_to_grandparent_when_parent_returns_complex_result(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Complex_nested_query_properly_binds_to_grandparent_when_parent_returns_scalar_result(bool _)
        => Task.CompletedTask;

    public override async Task OrderBy_Dto_projection_skip_take(bool async)
    {
        await base.OrderBy_Dto_projection_skip_take(async);

        AssertMql(
            """
            Customers.{ "$sort" : { "_id" : 1 } }, { "$skip" : 5 }, { "$limit" : 10 }, { "$project" : { "_id" : "$_id" } }
            """);
    }

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Join_take_count_works(bool _)
        => Task.CompletedTask;

    public override async Task OrderBy_empty_list_contains(bool async)
    {
        await base.OrderBy_empty_list_contains(async);

        AssertMql(
            """
            Customers.{ "$project" : { "_id" : 0, "_document" : "$$ROOT", "_key1" : { "$in" : ["$_id", []] } } }, { "$sort" : { "_key1" : 1 } }, { "$replaceRoot" : { "newRoot" : "$_document" } }
            """);
    }

    public override async Task OrderBy_empty_list_does_not_contains(bool async)
    {
        await base.OrderBy_empty_list_does_not_contains(async);

        AssertMql(
            """
            Customers.{ "$project" : { "_id" : 0, "_document" : "$$ROOT", "_key1" : { "$not" : { "$in" : ["$_id", []] } } } }, { "$sort" : { "_key1" : 1 } }, { "$replaceRoot" : { "newRoot" : "$_document" } }
            """);
    }

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Manual_expression_tree_typed_null_equality(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Let_subquery_with_multiple_occurrences(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Let_entity_equality_to_null(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Let_entity_equality_to_other_entity(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "No multi-collection query support"), MemberData(nameof(IsAsyncData))]
    public override Task Collection_navigation_equal_to_null_for_subquery(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "No multi-collection query support"), MemberData(nameof(IsAsyncData))]
    public override Task Dependent_to_principal_navigation_equal_to_null_for_subquery(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "No multi-collection query support"), MemberData(nameof(IsAsyncData))]
    public override Task Collection_navigation_equality_rewrite_for_subquery(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "No multi-collection query support"), MemberData(nameof(IsAsyncData))]
    public override Task Inner_parameter_in_nested_lambdas_gets_preserved(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Rewriting child expression"), MemberData(nameof(IsAsyncData))]
    public override Task Convert_to_nullable_on_nullable_value_is_ignored(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Navigation_inside_interpolated_string_is_expanded(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task OrderBy_object_type_server_evals(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task AsQueryable_in_query_server_evals(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Projection_skip_collection_projection(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Projection_take_collection_projection(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Projection_skip_take_collection_projection(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Projection_skip_projection(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Projection_take_projection(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Projection_skip_take_projection(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Collection_projection_skip(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Collection_projection_take(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Collection_projection_skip_take(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Anonymous_projection_skip_empty_collection_FirstOrDefault(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Anonymous_projection_take_empty_collection_FirstOrDefault(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Anonymous_projection_skip_take_empty_collection_FirstOrDefault(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "checked issue EF-249"), MemberData(nameof(IsAsyncData))]
    public override Task Checked_context_with_arithmetic_does_not_fail(bool _)
        => Task.CompletedTask;

    public override async Task Checked_context_with_case_to_same_nullable_type_does_not_fail(bool isAsync)
    {
        await base.Checked_context_with_case_to_same_nullable_type_does_not_fail(isAsync);

        AssertMql(
            """
            OrderDetails.{ "$group" : { "_id" : null, "_max" : { "$max" : { "_v" : "$Quantity" } } } }, { "$replaceRoot" : { "newRoot" : "$_max" } }
            """);
    }

    [ConditionalTheory(Skip = "Entity equality issue EF-202"), MemberData(nameof(IsAsyncData))]
    public override Task Entity_equality_with_null_coalesce_client_side(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Entity equality issue EF-202"), MemberData(nameof(IsAsyncData))]
    public override Task Entity_equality_contains_with_list_of_null(bool _)
        => Task.CompletedTask;

    public override async Task MemberInitExpression_NewExpression_is_funcletized_even_when_bindings_are_not_evaluatable(bool async)
    {
        await base.MemberInitExpression_NewExpression_is_funcletized_even_when_bindings_are_not_evaluatable(async);

        AssertMql(
            """
            Customers.{ "$match" : { "_id" : { "$regularExpression" : { "pattern" : "^A", "options" : "s" } } } }, { "$project" : { "Value" : "random", "CustomerID" : "$_id", "NestedDto" : { "$literal" : { "CustomerID" : null, "NestedDto" : null } }, "_id" : 0 } }
            """);
    }

#if !EF8
    public override async Task Funcletize_conditional_with_evaluatable_test(bool async)
    {
        await base.Funcletize_conditional_with_evaluatable_test(async);

        AssertMql(
            """
            Customers.
            """);
    }

#endif

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Single_non_scalar_projection_after_skip_uses_join(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Select_distinct_Select_with_client_bindings(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Client eval in final projection EF-250"), MemberData(nameof(IsAsyncData))]
    public override Task ToList_over_string(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Client eval in final projection EF-250"), MemberData(nameof(IsAsyncData))]
    public override Task ToArray_over_string(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Client eval in final projection EF-250"), MemberData(nameof(IsAsyncData))]
    public override Task AsEnumerable_over_string(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task
        Pending_selector_in_cardinality_reducing_method_is_applied_before_expanding_collection_navigation_member(bool _)
        => Task.CompletedTask;

    public override async Task Distinct_followed_by_ordering_on_condition(bool async)
    {
        await base.Distinct_followed_by_ordering_on_condition(async);

        AssertMql(
            """
Customers.{ "$match" : { "$and" : [{ "_id" : { "$ne" : "VAFFE" } }, { "_id" : { "$ne" : "DRACD" } }] } }, { "$project" : { "_v" : "$City", "_id" : 0 } }, { "$group" : { "_id" : "$$ROOT" } }, { "$replaceRoot" : { "newRoot" : "$_id" } }, { "$project" : { "_id" : 0, "_document" : "$$ROOT", "_key1" : { "$indexOfCP" : ["$_v", "c"] } } }, { "$sort" : { "_key1" : 1, "_document._v" : 1 } }, { "$replaceRoot" : { "newRoot" : "$_document" } }, { "$limit" : 5 }
""");
    }

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task DefaultIfEmpty_Sum_over_collection_navigation(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Entity_equality_on_subquery_with_null_check(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task FirstOrDefault_with_predicate_nested(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task First_on_collection_in_projection(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task SelectMany_correlated_subquery_hard(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Take zero EF-254"), MemberData(nameof(IsAsyncData))]
    public override Task Skip_0_Take_0_works_when_parameter(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Skip_0_Take_0_works_when_constant(bool _)
        => Task.CompletedTask;

#if EF9
    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Skip_1_Take_0_works_when_constant(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Take_0_works_when_constant(bool _)
        => Task.CompletedTask;

#endif

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Correlated_collection_with_distinct_without_default_identifiers_projecting_columns(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task
        Correlated_collection_with_distinct_without_default_identifiers_projecting_columns_with_navigation(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Select_nested_collection_with_distinct(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task SelectMany_primitive_select_subquery(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Concurrency detector tests broken EF-252"), MemberData(nameof(IsAsyncData))]
    public override Task Throws_on_concurrent_query_first(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Expression not supported"), MemberData(nameof(IsAsyncData))]
    public override Task Non_nullable_property_through_optional_navigation(bool _)
        => Task.CompletedTask;

    public override async Task OrderByDescending(bool async)
    {
        await base.OrderByDescending(async);

        AssertMql(
            """
            Customers.{ "$sort" : { "_id" : -1 } }, { "$project" : { "_v" : "$City", "_id" : 0 } }
            """);
    }

    public override async Task Take_Distinct(bool async)
    {
        await base.Take_Distinct(async);

        AssertMql(
            """
            Orders.{ "$sort" : { "_id" : 1 } }, { "$limit" : 5 }, { "$group" : { "_id" : "$$ROOT" } }, { "$replaceRoot" : { "newRoot" : "$_id" } }
            """);
    }

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Perform_identity_resolution_reuses_same_instances(bool _, bool useMe)
    {
        Assert.True(useMe || !useMe); // Use the parameter to avoid stupid xUnit warning.
        return Task.CompletedTask;
    }

    [ConditionalTheory(Skip = "Navigations issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Context_based_client_method(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Select_nested_collection_in_anonymous_type(bool _)
        => Task.CompletedTask;

    public override async Task OrderBy_Select(bool async)
    {
        await base.OrderBy_Select(async);

        AssertMql(
            """
            Customers.{ "$sort" : { "_id" : 1 } }, { "$project" : { "_v" : "$ContactName", "_id" : 0 } }
            """);
    }

    public override async Task OrderBy_ThenBy_predicate(bool async)
    {
        await base.OrderBy_ThenBy_predicate(async);

        AssertMql(
            """
            Customers.{ "$match" : { "City" : "London" } }, { "$sort" : { "City" : 1, "_id" : 1 } }
            """);
    }

    [ConditionalTheory(Skip = "No multi-collection query support"), MemberData(nameof(IsAsyncData))]
    public override Task Query_when_evaluatable_queryable_method_call_with_repository(bool _)
        => Task.CompletedTask;

    public override async Task Max_on_empty_sequence_throws(bool async)
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => base.Max_on_empty_sequence_throws(async));

        AssertMql(
        );
    }

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task OrderBy_Join(bool _)
        => Task.CompletedTask;

    public override async Task Where_Property_shadow_closure(bool async)
    {
        await base.Where_Property_shadow_closure(async);

        AssertMql(
            """
            Employees.{ "$match" : { "Title" : "Sales Representative" } }
            """,
            //
            """
            Employees.{ "$match" : { "FirstName" : "Steven" } }
            """);
    }

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task SelectMany_customer_orders(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Concurrency detector tests broken EF-252"), MemberData(nameof(IsAsyncData))]
    public override Task Throws_on_concurrent_query_list(bool _)
        => Task.CompletedTask;

    public override async Task Select_Property_when_shadow(bool async)
    {
        await base.Select_Property_when_shadow(async);

        AssertMql(
            """
            Employees.{ "$project" : { "_v" : "$Title", "_id" : 0 } }
            """);
    }

    public override async Task Select_Property_when_non_shadow(bool async)
    {
        await base.Select_Property_when_non_shadow(async);

        AssertMql(
            """
            Orders.{ "$project" : { "_v" : "$_id", "_id" : 0 } }
            """);
    }

    public override async Task OrderByDescending_ThenBy(bool async)
    {
        await base.OrderByDescending_ThenBy(async);

        AssertMql(
            """
            Customers.{ "$sort" : { "_id" : -1, "Country" : 1 } }, { "$project" : { "_v" : "$City", "_id" : 0 } }
            """);
    }

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task SelectMany_correlated_subquery_simple(bool _)
        => Task.CompletedTask;

    public override async Task Select_Property_when_shadow_unconstrained_generic_method(bool async)
    {
        await base.Select_Property_when_shadow_unconstrained_generic_method(async);

        AssertMql(
            """
            Employees.{ "$project" : { "_v" : "$Title", "_id" : 0 } }
            """);
    }

    public override async Task Where_Property_when_shadow(bool async)
    {
        await base.Where_Property_when_shadow(async);

        AssertMql(
            """
            Employees.{ "$match" : { "Title" : "Sales Representative" } }
            """);
    }

    public override async Task Where_Property_when_shadow_unconstrained_generic_method(bool async)
    {
        await base.Where_Property_when_shadow_unconstrained_generic_method(async);

        AssertMql(
            """
            Employees.{ "$match" : { "Title" : "Sales Representative" } }
            """);
    }

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Perform_identity_resolution_reuses_same_instances_across_joins(bool _, bool useMe)
    {
        Assert.True(useMe || !useMe); // Use the parameter to avoid stupid xUnit warning.
        return Task.CompletedTask;
    }

    public override async Task OrderBy_scalar_primitive(bool async)
    {
        await base.OrderBy_scalar_primitive(async);

        AssertMql(
            """
            Employees.{ "$sort" : { "_id" : 1 } }, { "$project" : { "_v" : "$_id", "_id" : 0 } }
            """);
    }

    public override async Task Where_Property_when_non_shadow(bool async)
    {
        await base.Where_Property_when_non_shadow(async);

        AssertMql(
            """
            Orders.{ "$match" : { "_id" : 10248 } }
            """);
    }

    public override async Task OrderByDescending_ThenByDescending(bool async)
    {
        await base.OrderByDescending_ThenByDescending(async);

        AssertMql(
            """
            Customers.{ "$sort" : { "_id" : -1, "Country" : -1 } }, { "$project" : { "_v" : "$City", "_id" : 0 } }
            """);
    }

    public override async Task Load_should_track_results(bool async)
    {
        await base.Load_should_track_results(async);

        AssertMql(
            """
            Customers.
            """);
    }

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task SelectMany_nested_simple(bool _)
        => Task.CompletedTask;

    public override async Task Null_parameter_name_works(bool async)
    {
        await base.Null_parameter_name_works(async);

        AssertMql(
            """
            Customers.{ "$match" : { "$expr" : { "$eq" : ["$$ROOT", null] } } }
            """);
    }

    public override async Task Where_subquery_expression(bool async)
    {
        await base.Where_subquery_expression(async);

        AssertMql(
            """
            Orders.{ "$limit" : 1 }
            """,
            //
            """
            Orders.{ "$match" : { "_id" : 10248 } }, { "$limit" : 1 }, { "$project" : { "_id" : 0, "_v" : null } }
            """,
            //
            """
            Orders.
            """);
    }

    public override async Task Mixed_sync_async_in_query_cache()
    {
        await base.Mixed_sync_async_in_query_cache();

        AssertMql(
            """
            Customers.
            """,
            //
            """
            Customers.
            """);
    }

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Select_expression_datetime_add_ticks(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Navigations issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Where_subquery_expression_same_parametername(bool _)
        => Task.CompletedTask;

    public override async Task Cast_results_to_object(bool async)
    {
        await base.Cast_results_to_object(async);

        AssertMql(
            """
            Customers.
            """);
    }

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Select_subquery_recursive_trivial(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task SelectMany_primitive(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task SelectMany_Joined(bool _)
        => Task.CompletedTask;

    // ReSharper disable once RedundantOverriddenMember
    public override async Task ToListAsync_can_be_canceled()
        // May or may not generate Mql depending on when cancellation happens.
        => await base.ToListAsync_can_be_canceled();

    public override async Task OrderBy_ThenBy(bool async)
    {
        await base.OrderBy_ThenBy(async);

        AssertMql(
            """
            Customers.{ "$sort" : { "_id" : 1, "Country" : 1 } }, { "$project" : { "_v" : "$City", "_id" : 0 } }
            """);
    }

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Collection_projection_after_DefaultIfEmpty(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task SelectMany_correlated_simple(bool _)
        => Task.CompletedTask;

    public override void Query_composition_against_ienumerable_set()
    {
        base.Query_composition_against_ienumerable_set();

        AssertMql(
            """
            Orders.
            """);
    }

    [ConditionalTheory(Skip = "Expression not supported"), MemberData(nameof(IsAsyncData))]
    public override Task Using_static_string_Equals_with_StringComparison_throws_informative_error(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Expression not supported"), MemberData(nameof(IsAsyncData))]
    public override Task Using_string_Equals_with_StringComparison_throws_informative_error(bool _)
        => Task.CompletedTask;

#if EF8 || EF9
    public override async Task Random_next_is_not_funcletized_1(bool async)
    {
        try
        {
            await base.Random_next_is_not_funcletized_1(async);
        }
        catch
        {
            Assert.Fail("Exception is expected here, so this bug may have been fixed.");
        }

        // No MQL assertion since the query contains randomness.
        // AssertMql();
    }

    public override async Task Random_next_is_not_funcletized_2(bool async)
    {
        try
        {
            await base.Random_next_is_not_funcletized_2(async);
        }
        catch
        {
            Assert.Fail("Exception is expected here, so this bug may have been fixed.");
        }

        // No MQL assertion since the query contains randomness.
        // AssertMql();
    }

    public override async Task Random_next_is_not_funcletized_3(bool async)
    {
        try
        {
            await base.Random_next_is_not_funcletized_3(async);
        }
        catch
        {
            Assert.Fail("Exception is expected here, so this bug may have been fixed.");
        }

        // No MQL assertion since the query contains randomness.
        // AssertMql();
    }

    public override async Task Random_next_is_not_funcletized_4(bool async)
    {
        try
        {
            await base.Random_next_is_not_funcletized_4(async);
        }
        catch
        {
            Assert.Fail("Exception is expected here, so this bug may have been fixed.");
        }

        // No MQL assertion since the query contains randomness.
        // AssertMql();
    }

    public override async Task Random_next_is_not_funcletized_5(bool async)
    {
        await base.Random_next_is_not_funcletized_5(async);

        AssertMql(
            """
            Orders.{ "$match" : { "_id" : { "$gt" : 2 } } }
            """);
    }

    public override async Task Random_next_is_not_funcletized_6(bool async)
    {
        await base.Random_next_is_not_funcletized_6(async);

        AssertMql(
            """
            Orders.{ "$match" : { "_id" : { "$gt" : 5 } } }
            """);
    }

#endif

    public override async Task SelectMany_after_client_method(bool async)
    {
        await base.SelectMany_after_client_method(async);

        AssertMql(
        );
    }

    public override async Task Client_OrderBy_GroupBy_Group_ordering_works(bool async)
    {
        await base.Client_OrderBy_GroupBy_Group_ordering_works(async);

        AssertMql();
    }

    [ConditionalTheory(Skip = "Expression not supported"), MemberData(nameof(IsAsyncData))]
    public override Task Client_code_using_instance_method_throws(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Expression not supported"), MemberData(nameof(IsAsyncData))]
    public override Task Client_code_using_instance_in_static_method(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Client_code_using_instance_in_anonymous_type(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Expression not supported"), MemberData(nameof(IsAsyncData))]
    public override Task Client_code_unknown_method(bool _)
        => Task.CompletedTask;

    public override async Task String_include_on_incorrect_property_throws(bool async)
    {
        await base.String_include_on_incorrect_property_throws(async);

        AssertMql();
    }

    public override async Task SkipWhile_throws_meaningful_exception(bool async)
    {
        await base.SkipWhile_throws_meaningful_exception(async);

        AssertMql(
        );
    }

    public override async Task ToListAsync_with_canceled_token()
    {
        await base.ToListAsync_with_canceled_token();

        AssertMql();
    }

    public override async Task Mixed_sync_async_query()
    {
        await base.Mixed_sync_async_query();

        AssertMql(
        );
    }

    public override async Task Parameter_extraction_can_throw_exception_from_user_code(bool async)
    {
        await base.Parameter_extraction_can_throw_exception_from_user_code(async);

        AssertMql();
    }

    public override async Task Parameter_extraction_can_throw_exception_from_user_code_2(bool async)
    {
        await base.Parameter_extraction_can_throw_exception_from_user_code_2(async);

        AssertMql();
    }

    [ConditionalTheory(Skip = "Expression not supported"), MemberData(nameof(IsAsyncData))]
    public override Task Where_query_composition3(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Expression not supported"), MemberData(nameof(IsAsyncData))]
    public override Task Where_query_composition4(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Expression not supported"), MemberData(nameof(IsAsyncData))]
    public override Task Where_query_composition5(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Expression not supported"), MemberData(nameof(IsAsyncData))]
    public override Task Where_query_composition6(bool _)
        => Task.CompletedTask;

    public override async Task SelectMany_mixed(bool async)
    {
        await base.SelectMany_mixed(async);

        AssertMql();
    }

#if !EF8 && !EF9
    public override async Task DefaultIfEmpty_top_level_arg(bool async)
    {
        await base.DefaultIfEmpty_top_level_arg(async);

        AssertMql();
    }

    public override async Task DefaultIfEmpty_top_level_arg_followed_by_projecting_constant(bool async)
    {
        await base.DefaultIfEmpty_top_level_arg_followed_by_projecting_constant(async);

        AssertMql();
    }

#else
    public override async Task Default_if_empty_top_level_arg(bool async)
    {
        await base.Default_if_empty_top_level_arg(async);

        AssertMql();
    }

    public override async Task Default_if_empty_top_level_arg_followed_by_projecting_constant(bool async)
    {
        await base.Default_if_empty_top_level_arg_followed_by_projecting_constant(async);

        AssertMql();
    }

#endif

    [ConditionalTheory(Skip = "Expression not supported"), MemberData(nameof(IsAsyncData))]
    public override Task OrderBy_client_mixed(bool _)
        => Task.CompletedTask;

    public override async Task OrderBy_multiple_queries(bool async)
    {
        await base.OrderBy_multiple_queries(async);

        AssertMql();
    }

    public override void Can_cast_CreateQuery_result_to_IQueryable_T_bug_1730()
    {
        base.Can_cast_CreateQuery_result_to_IQueryable_T_bug_1730();

        AssertMql();
    }

#if EF9
    [ConditionalFact(Skip = "No multi-collection query support")]
    public override Task IQueryable_captured_variable()
        => Task.CompletedTask;

#endif

    public override async Task Multiple_context_instances(bool async)
    {
        await base.Multiple_context_instances(async);

        AssertMql();
    }

    public override async Task Multiple_context_instances_2(bool async)
    {
        await base.Multiple_context_instances_2(async);

        AssertMql();
    }

    public override async Task Multiple_context_instances_set(bool async)
    {
        await base.Multiple_context_instances_set(async);
        AssertMql(
        );
    }

    public override async Task Multiple_context_instances_parameter(bool async)
    {
        await base.Multiple_context_instances_parameter(async);

        AssertMql();
    }

    public override async Task Entity_equality_through_subquery_composite_key(bool async)
    {
        await base.Multiple_context_instances_parameter(async);

        AssertMql();
    }

    [ConditionalTheory(Skip = "Expression not supported"), MemberData(nameof(IsAsyncData))]
    public override Task Queryable_reprojection(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Expression not supported"), MemberData(nameof(IsAsyncData))]
    public override Task All_client(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Expression not supported"), MemberData(nameof(IsAsyncData))]
    public override Task All_client_and_server_top_level(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Expression not supported"), MemberData(nameof(IsAsyncData))]
    public override Task All_client_or_server_top_level(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Expression not supported"), MemberData(nameof(IsAsyncData))]
    public override Task First_client_predicate(bool _)
        => Task.CompletedTask;

    public override async Task Select_correlated_subquery_filtered_returning_queryable_throws(bool async)
    {
        await base.Select_correlated_subquery_filtered_returning_queryable_throws(async);

        AssertMql();
    }

    public override async Task Select_correlated_subquery_ordered_returning_queryable_throws(bool async)
    {
        await base.Select_correlated_subquery_ordered_returning_queryable_throws(async);

        AssertMql();
    }

    public override async Task Select_correlated_subquery_ordered_returning_queryable_in_DTO_throws(bool async)
    {
        await base.Select_correlated_subquery_ordered_returning_queryable_in_DTO_throws(async);

        AssertMql();
    }

    public override async Task Select_nested_collection_in_anonymous_type_returning_ordered_queryable(bool async)
    {
        await base.Select_nested_collection_in_anonymous_type_returning_ordered_queryable(async);

        AssertMql();
    }

    public override async Task Select_subquery_recursive_trivial_returning_queryable(bool async)
    {
        await base.Select_subquery_recursive_trivial_returning_queryable(async);

        AssertMql();
    }

    public override async Task EF_Property_include_on_incorrect_property_throws(bool async)
    {
        await base.EF_Property_include_on_incorrect_property_throws(async);

        AssertMql();
    }

    [ConditionalTheory(Skip = "No multi-collection query support"), MemberData(nameof(IsAsyncData))]
    public override Task Collection_navigation_equal_to_null_for_subquery_using_ElementAtOrDefault_constant_zero(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "No multi-collection query support"), MemberData(nameof(IsAsyncData))]
    public override Task Collection_navigation_equal_to_null_for_subquery_using_ElementAtOrDefault_constant_one(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "No multi-collection query support"), MemberData(nameof(IsAsyncData))]
    public override Task Collection_navigation_equal_to_null_for_subquery_using_ElementAtOrDefault_parameter(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "No multi-collection query support"), MemberData(nameof(IsAsyncData))]
    public override Task Subquery_with_navigation_inside_inline_collection(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Parameter_collection_Contains_with_projection_and_ordering(bool _)
        => Task.CompletedTask;

    public override async Task Contains_over_concatenated_columns_with_different_sizes(bool async)
    {
        await base.Contains_over_concatenated_columns_with_different_sizes(async);

        AssertMql(
            """
            Customers.{ "$match" : { "$expr" : { "$in" : [{ "$concat" : ["$_id", "$CompanyName"] }, ["ALFKIAlfreds Futterkiste", "ANATRAna Trujillo Emparedados y helados"]] } } }
            """);
    }

    public override async Task Contains_over_concatenated_column_and_constant(bool async)
    {
        await base.Contains_over_concatenated_column_and_constant(async);

        AssertMql(
            """
            Customers.{ "$match" : { "$expr" : { "$in" : [{ "$concat" : ["$_id", "SomeConstant"] }, ["ALFKISomeConstant", "ANATRSomeConstant", "ALFKIX"]] } } }
            """);
    }

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Contains_over_concatenated_columns_both_fixed_length(bool _)
        => Task.CompletedTask;

    public override async Task Contains_over_concatenated_column_and_parameter(bool async)
    {
        await base.Contains_over_concatenated_column_and_parameter(async);

        AssertMql(
            """
            Customers.{ "$match" : { "$expr" : { "$in" : [{ "$concat" : ["$_id", "SomeVariable"] }, ["ALFKISomeVariable", "ANATRSomeVariable", "ALFKIX"]] } } }
            """);
    }

    public override async Task Contains_over_concatenated_parameter_and_constant(bool async)
    {
        await base.Contains_over_concatenated_parameter_and_constant(async);

        AssertMql(
            """
            Customers.
            """);
    }

#if EF9
    public override async Task Compiler_generated_local_closure_produces_valid_parameter_name(bool async)
    {
        await base.Compiler_generated_local_closure_produces_valid_parameter_name(async);

        AssertMql(
            """
Customers.{ "$match" : { "_id" : "ALFKI", "City" : "Berlin" } }
""");
    }

    public override async Task Static_member_access_gets_parameterized_within_larger_evaluatable(bool async)
    {
        await base.Static_member_access_gets_parameterized_within_larger_evaluatable(async);

        AssertMql(
            """
Customers.{ "$match" : { "_id" : "ALFKI" } }
""");
    }

    public override async Task Select_Order(bool async)
    {
        await base.Select_Order(async);

        AssertMql(
            """
Customers.{ "$sort" : { "_id" : 1 } }, { "$project" : { "_v" : "$_id", "_id" : 0 } }
""");
    }

    public override async Task Select_OrderDescending(bool async)
    {
        await base.Select_OrderDescending(async);

        AssertMql(
            """
Customers.{ "$sort" : { "_id" : -1 } }, { "$project" : { "_v" : "$_id", "_id" : 0 } }
""");
    }

    [ConditionalTheory(Skip = "No multi-collection query support"), MemberData(nameof(IsAsyncData))]
    public override Task Where_Order_First(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "No multi-collection query support"), MemberData(nameof(IsAsyncData))]
    public override Task Column_access_inside_subquery_predicate(bool _)
        => Task.CompletedTask;

    public override async Task Cast_to_object_over_parameter_directly_in_lambda(bool async)
    {
        await base.Cast_to_object_over_parameter_directly_in_lambda(async);

        AssertMql(
            """
Orders.{ "$project" : { "_id" : 0, "_document" : "$$ROOT", "_key1" : 8 } }, { "$sort" : { "_key1" : 1 } }, { "$replaceRoot" : { "newRoot" : "$_document" } }
""");
    }

#endif

    private TestServer TestServer
        => ((MongoTestStore)Fixture.TestStore).TestServer;

    private void AssertMql(params string[] expected)
        => Fixture.TestMqlLoggerFactory.AssertBaseline(expected);

    protected override void ClearLog()
        => Fixture.TestMqlLoggerFactory.Clear();
}
