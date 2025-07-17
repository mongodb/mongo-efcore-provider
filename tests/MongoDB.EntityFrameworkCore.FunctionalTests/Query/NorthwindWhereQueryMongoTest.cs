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

using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.TestUtilities;
using MongoDB.Driver.Linq;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

public class NorthwindWhereQueryMongoTest : NorthwindWhereQueryTestBase<NorthwindQueryMongoFixture<NoopModelCustomizer>>
{
    public NorthwindWhereQueryMongoTest(
        NorthwindQueryMongoFixture<NoopModelCustomizer> fixture,
        ITestOutputHelper testOutputHelper)
        : base(fixture)
    {
        ClearLog();
        Fixture.TestMqlLoggerFactory.SetTestOutputHelper(testOutputHelper);
    }

    [ConditionalFact]
    public virtual void Check_all_tests_overridden()
        => TestHelpers.AssertAllMethodsOverridden(GetType());

    public override async Task Where_simple(bool async)
    {
        await base.Where_simple(async);

        AssertMql(
            """
            Customers.{ "$match" : { "City" : "London" } }
            """);
    }

    public override async Task Where_as_queryable_expression(bool async)
    {
        // Fails: AV007
        Assert.Contains(
            "cannot be used for parameter",
            (await Assert.ThrowsAsync<ArgumentException>(async () =>
                await base.Where_as_queryable_expression(async))).Message);

        AssertMql();
    }

    public override async Task<string> Where_simple_closure(bool async)
    {
        var queryString = await base.Where_simple_closure(async);

        AssertMql(
            """
            Customers.{ "$match" : { "City" : "London" } }
            """);

        return queryString;
    }

    public override async Task Where_indexer_closure(bool async)
    {
        await base.Where_indexer_closure(async);

        AssertMql(
            """
            Customers.{ "$match" : { "City" : "London" } }
            """);
    }

    public override async Task Where_dictionary_key_access_closure(bool async)
    {
        await base.Where_dictionary_key_access_closure(async);

        AssertMql(
            """
            Customers.{ "$match" : { "City" : "London" } }
            """);
    }

    public override async Task Where_tuple_item_closure(bool async)
    {
        await base.Where_tuple_item_closure(async);

        AssertMql(
            """
            Customers.{ "$match" : { "City" : "London" } }
            """);
    }

    public override async Task Where_named_tuple_item_closure(bool async)
    {
        await base.Where_named_tuple_item_closure(async);

        AssertMql(
            """
            Customers.{ "$match" : { "City" : "London" } }
            """);
    }

    public override async Task Where_simple_closure_constant(bool async)
    {
        await base.Where_simple_closure_constant(async);

        AssertMql(
            """
            Customers.
            """);
    }

    public override async Task Where_simple_closure_via_query_cache(bool async)
    {
        await base.Where_simple_closure_via_query_cache(async);

        AssertMql(
            """
            Customers.{ "$match" : { "City" : "London" } }
            """,
            //
            """
            Customers.{ "$match" : { "City" : "Seattle" } }
            """);
    }

    public override async Task Where_method_call_nullable_type_closure_via_query_cache(bool async)
    {
        await base.Where_method_call_nullable_type_closure_via_query_cache(async);

        AssertMql(
            """
            Employees.{ "$match" : { "$expr" : { "$eq" : [{ "$toLong" : "$ReportsTo" }, 2] } } }
            """,
            //
            """
            Employees.{ "$match" : { "$expr" : { "$eq" : [{ "$toLong" : "$ReportsTo" }, 5] } } }
            """);
    }

    public override async Task Where_method_call_nullable_type_reverse_closure_via_query_cache(bool async)
    {
        // Fails: AV000
        Assert.Contains(
            "Values differ", // (Expected 8, got 0)
            (await Assert.ThrowsAsync<EqualException>(async () =>
                await base.Where_method_call_nullable_type_reverse_closure_via_query_cache(async))).Message);

        AssertMql(
            """
            Employees.{ "$match" : { "_id" : { "$gt" : 1 } } }
            """);
    }

    public override async Task Where_method_call_closure_via_query_cache(bool async)
    {
        await base.Where_method_call_closure_via_query_cache(async);

        AssertMql(
            """
            Customers.{ "$match" : { "City" : "London" } }
            """,
            //
            """
            Customers.{ "$match" : { "City" : "Seattle" } }
            """);
    }

    public override async Task Where_field_access_closure_via_query_cache(bool async)
    {
        await base.Where_field_access_closure_via_query_cache(async);

        AssertMql(
            """
            Customers.{ "$match" : { "City" : "London" } }
            """,
            //
            """
            Customers.{ "$match" : { "City" : "Seattle" } }
            """);
    }

    public override async Task Where_property_access_closure_via_query_cache(bool async)
    {
        await base.Where_property_access_closure_via_query_cache(async);

        AssertMql(
            """
            Customers.{ "$match" : { "City" : "London" } }
            """,
            //
            """
            Customers.{ "$match" : { "City" : "Seattle" } }
            """);
    }

    public override async Task Where_static_field_access_closure_via_query_cache(bool async)
    {
        await base.Where_static_field_access_closure_via_query_cache(async);

        AssertMql(
            """
            Customers.{ "$match" : { "City" : "London" } }
            """,
            //
            """
            Customers.{ "$match" : { "City" : "Seattle" } }
            """);
    }

    public override async Task Where_static_property_access_closure_via_query_cache(bool async)
    {
        await base.Where_static_property_access_closure_via_query_cache(async);

        AssertMql(
            """
            Customers.{ "$match" : { "City" : "London" } }
            """,
            //
            """
            Customers.{ "$match" : { "City" : "Seattle" } }
            """);
    }

    public override async Task Where_nested_field_access_closure_via_query_cache(bool async)
    {
        await base.Where_nested_field_access_closure_via_query_cache(async);

        AssertMql(
            """
            Customers.{ "$match" : { "City" : "London" } }
            """,
            //
            """
            Customers.{ "$match" : { "City" : "Seattle" } }
            """);
    }

    public override async Task Where_nested_property_access_closure_via_query_cache(bool async)
    {
        await base.Where_nested_property_access_closure_via_query_cache(async);

        AssertMql(
            """
            Customers.{ "$match" : { "City" : "London" } }
            """,
            //
            """
            Customers.{ "$match" : { "City" : "Seattle" } }
            """);
    }

    public override async Task Where_new_instance_field_access_query_cache(bool async)
    {
        await base.Where_new_instance_field_access_query_cache(async);

        AssertMql(
            """
            Customers.{ "$match" : { "City" : "London" } }
            """,
            //
            """
            Customers.{ "$match" : { "City" : "Seattle" } }
            """);
    }

    public override async Task Where_new_instance_field_access_closure_via_query_cache(bool async)
    {
        await base.Where_new_instance_field_access_closure_via_query_cache(async);

        AssertMql(
            """
            Customers.{ "$match" : { "City" : "London" } }
            """,
            //
            """
            Customers.{ "$match" : { "City" : "Seattle" } }
            """);
    }

    public override async Task Where_simple_closure_via_query_cache_nullable_type(bool async)
    {
        await base.Where_simple_closure_via_query_cache_nullable_type(async);

        AssertMql(
            """
            Employees.{ "$match" : { "$expr" : { "$eq" : [{ "$toLong" : "$ReportsTo" }, 2] } } }
            """,
            //
            """
            Employees.{ "$match" : { "$expr" : { "$eq" : [{ "$toLong" : "$ReportsTo" }, 5] } } }
            """,
            //
            """
            Employees.{ "$match" : { "$expr" : { "$eq" : [{ "$toLong" : "$ReportsTo" }, null] } } }
            """);
    }

    public override async Task Where_simple_closure_via_query_cache_nullable_type_reverse(bool async)
    {
        await base.Where_simple_closure_via_query_cache_nullable_type_reverse(async);

        AssertMql(
            """
            Employees.{ "$match" : { "$expr" : { "$eq" : [{ "$toLong" : "$ReportsTo" }, null] } } }
            """,
            //
            """
            Employees.{ "$match" : { "$expr" : { "$eq" : [{ "$toLong" : "$ReportsTo" }, 5] } } }
            """,
            //
            """
            Employees.{ "$match" : { "$expr" : { "$eq" : [{ "$toLong" : "$ReportsTo" }, 2] } } }
            """);
    }

    public override async Task Where_subquery_closure_via_query_cache(bool async)
    {
        // Fails: AV007
        Assert.Contains(
            "cannot be used for parameter",
            (await Assert.ThrowsAsync<ArgumentException>(async () =>
                await base.Where_subquery_closure_via_query_cache(async))).Message);

        AssertMql();
    }

    public override async Task Where_simple_shadow(bool async)
    {
        await base.Where_simple_shadow(async);

        AssertMql(
            """
            Employees.{ "$match" : { "Title" : "Sales Representative" } }
            """);
    }

    public override async Task Where_simple_shadow_projection(bool async)
    {
        // Fails: AV000
        await Assert.ThrowsAsync<NullReferenceException>(async () => await base.Where_simple_shadow_projection(async));

        AssertMql(
        );
    }

    public override async Task Where_simple_shadow_subquery(bool async)
    {
        await base.Where_simple_shadow_subquery(async);

        AssertMql(
            """
            Employees.{ "$sort" : { "_id" : 1 } }, { "$limit" : 5 }, { "$match" : { "Title" : "Sales Representative" } }
            """);
    }

    public override async Task Where_shadow_subquery_FirstOrDefault(bool async)
    {
        // Fails: AV000
        Assert.Contains(
            "Expression not supported: Northwind.Employees.Aggregate(",
            (await Assert.ThrowsAsync<ExpressionNotSupportedException>(async () =>
                await base.Where_shadow_subquery_FirstOrDefault(async))).Message);

        AssertMql(
            """
            Employees.
            """);
    }

    public override async Task Where_client(bool async)
    {
        // Fails: AV013 (Not throwing expected translation failed exception)
        Assert.Contains(
            "Serializer for Microsoft.",
            (await Assert.ThrowsAsync<ContainsException>(async () => await base.Where_client(async))).Message);

        AssertMql(
            """
            Customers.
            """);
    }

    public override async Task Where_subquery_correlated(bool async)
    {
        // Fails: AV013 (Not throwing expected translation failed exception)
        Assert.Contains(
            "Expression not supported: Northwind.Custo",
            (await Assert.ThrowsAsync<ExpressionNotSupportedException>(async () => await base.Where_subquery_correlated(async)))
            .Message);

        AssertMql(
            """
            Customers.
            """);
    }

    public override async Task Where_subquery_correlated_client_eval(bool async)
    {
        // Fails: AV013 (Not throwing expected translation failed exception)
        await Assert.ThrowsAsync<ThrowsException>(async () => await base.Where_subquery_correlated_client_eval(async));

        AssertMql(
            """
            Customers.
            """);
    }

    public override async Task Where_client_and_server_top_level(bool async)
    {
        // Fails: AV013 (Not throwing expected translation failed exception)
        Assert.Contains(
            "Serializer for Microsoft.",
            (await Assert.ThrowsAsync<ContainsException>(async () => await base.Where_client_and_server_top_level(async))).Message);

        AssertMql(
            """
            Customers.
            """);
    }

    public override async Task Where_client_or_server_top_level(bool async)
    {
        // Fails: AV013 (Not throwing expected translation failed exception)
        Assert.Contains(
            "Serializer for Microsoft.",
            (await Assert.ThrowsAsync<ContainsException>(async () => await base.Where_client_or_server_top_level(async))).Message);

        AssertMql(
            """
            Customers.
            """);
    }

    public override async Task Where_client_and_server_non_top_level(bool async)
    {
        // Fails: AV013 (Not throwing expected translation failed exception)
        Assert.Contains(
            "Serializer for Microsoft.",
            (await Assert.ThrowsAsync<ContainsException>(async () => await base.Where_client_and_server_non_top_level(async)))
            .Message);

        AssertMql(
            """
            Customers.
            """);
    }

    public override async Task Where_client_deep_inside_predicate_and_server_top_level(bool async)
    {
        // Fails: AV013 (Not throwing expected translation failed exception)
        Assert.Contains(
            "Serializer for Microsoft.",
            (await Assert.ThrowsAsync<ContainsException>(async () =>
                await base.Where_client_deep_inside_predicate_and_server_top_level(async))).Message);

        AssertMql(
            """
            Customers.
            """);
    }

    public override async Task Where_equals_method_int(bool async)
    {
        // Fails: AV000
        Assert.Contains(
            "Values differ", // (Expected 1, got 0)
            (await Assert.ThrowsAsync<EqualException>(async () =>
                await base.Where_equals_method_int(async))).Message);

        AssertMql(
            """
            Employees.{ "$match" : { "_id" : 1 } }
            """);
    }

    public override async Task Where_equals_using_object_overload_on_mismatched_types(bool async)
    {
        // Fails: AV000
        Assert.Contains(
            "Unable to cast object of type 'System.UInt64' to type 'System.UInt32'.",
            (await Assert.ThrowsAsync<InvalidCastException>(async () =>
                await base.Where_equals_using_object_overload_on_mismatched_types(async))).Message);

        AssertMql(
            """
            Employees.
            """);
    }

    public override async Task Where_equals_using_int_overload_on_mismatched_types(bool async)
    {
        // Fails: AV000
        Assert.Contains(
            "Values differ",
            (await Assert.ThrowsAsync<EqualException>(async () =>
                await base.Where_equals_using_int_overload_on_mismatched_types(async))).Message);

        AssertMql(
            """
            Employees.{ "$match" : { "_id" : 1 } }
            """);
    }

    public override async Task Where_equals_on_mismatched_types_nullable_int_long(bool async)
    {
        // Fails: AV000
        Assert.Contains(
            "Unable to cast object of type 'System.UInt64' to type 'System.Nullable`1[System.UInt32]'.",
            (await Assert.ThrowsAsync<InvalidCastException>(async () =>
                await base.Where_equals_on_mismatched_types_nullable_int_long(async))).Message);

        AssertMql(
            """
            Employees.
            """);
    }

    public override async Task Where_equals_on_mismatched_types_nullable_long_nullable_int(bool async)
    {
        // Fails: AV000
        Assert.Contains(
            "Unable to cast object of type 'System.UInt64' to type 'System.Nullable`1[System.UInt32]'.",
            (await Assert.ThrowsAsync<InvalidCastException>(async () =>
                await base.Where_equals_on_mismatched_types_nullable_long_nullable_int(async))).Message);

        AssertMql(
            """
            Employees.
            """);
    }

    public override async Task Where_equals_on_mismatched_types_int_nullable_int(bool async)
    {
        await base.Where_equals_on_mismatched_types_int_nullable_int(async);

        AssertMql(
            """
            Employees.{ "$match" : { "ReportsTo" : 2 } }
            """,
            //
            """
            Employees.{ "$match" : { "ReportsTo" : 2 } }
            """);
    }

    public override async Task Where_equals_on_matched_nullable_int_types(bool async)
    {
        await base.Where_equals_on_matched_nullable_int_types(async);

        AssertMql(
            """
            Employees.{ "$match" : { "ReportsTo" : 2 } }
            """,
            //
            """
            Employees.{ "$match" : { "ReportsTo" : 2 } }
            """);
    }

    public override async Task Where_equals_on_null_nullable_int_types(bool async)
    {
        await base.Where_equals_on_null_nullable_int_types(async);

        AssertMql(
            """
            Employees.{ "$match" : { "ReportsTo" : null } }
            """,
            //
            """
            Employees.{ "$match" : { "ReportsTo" : null } }
            """);
    }

    public override async Task Where_comparison_nullable_type_not_null(bool async)
    {
        await base.Where_comparison_nullable_type_not_null(async);

        AssertMql(
            """
            Employees.{ "$match" : { "ReportsTo" : 2 } }
            """);
    }

    public override async Task Where_comparison_nullable_type_null(bool async)
    {
        await base.Where_comparison_nullable_type_null(async);

        AssertMql(
            """
            Employees.{ "$match" : { "ReportsTo" : null } }
            """);
    }

    public override async Task Where_simple_reversed(bool async)
    {
        await base.Where_simple_reversed(async);

        AssertMql(
            """
            Customers.{ "$match" : { "City" : "London" } }
            """);
    }

    public override async Task Where_is_null(bool async)
    {
        await base.Where_is_null(async);

        AssertMql(
            """
            Customers.{ "$match" : { "Region" : null } }
            """);
    }

    public override async Task Where_null_is_null(bool async)
    {
        await base.Where_null_is_null(async);

        AssertMql(
            """
            Customers.
            """);
    }

    public override async Task Where_constant_is_null(bool async)
    {
        await base.Where_constant_is_null(async);

        AssertMql(
            """
            Customers.{ "$match" : { "_id" : { "$type" : -1 } } }
            """);
    }

    public override async Task Where_is_not_null(bool async)
    {
        await base.Where_is_not_null(async);

        AssertMql(
            """
            Customers.{ "$match" : { "City" : { "$ne" : null } } }
            """);
    }

    public override async Task Where_null_is_not_null(bool async)
    {
        await base.Where_null_is_not_null(async);

        AssertMql(
            """
            Customers.{ "$match" : { "_id" : { "$type" : -1 } } }
            """);
    }

    public override async Task Where_constant_is_not_null(bool async)
    {
        await base.Where_constant_is_not_null(async);

        AssertMql(
            """
            Customers.
            """);
    }

    public override async Task Where_identity_comparison(bool async)
    {
        await base.Where_identity_comparison(async);

        AssertMql(
            """
            Customers.{ "$match" : { "$expr" : { "$eq" : ["$City", "$City"] } } }
            """);
    }

    public override async Task Where_in_optimization_multiple(bool async)
    {
        // Fails: AV000
        await AssertTranslationFailed(() => base.Where_in_optimization_multiple(async));

        AssertMql();
    }

    public override async Task Where_not_in_optimization1(bool async)
    {
        // Fails: AV000
        await AssertTranslationFailed(() => base.Where_not_in_optimization1(async));

        AssertMql();
    }

    public override async Task Where_not_in_optimization2(bool async)
    {
        // Fails: AV000
        await AssertTranslationFailed(() => base.Where_not_in_optimization2(async));

        AssertMql();
    }

    public override async Task Where_not_in_optimization3(bool async)
    {
        // Fails: AV000
        await AssertTranslationFailed(() => base.Where_not_in_optimization3(async));

        AssertMql();
    }

    public override async Task Where_not_in_optimization4(bool async)
    {
        // Fails: AV000
        await AssertTranslationFailed(() => base.Where_not_in_optimization4(async));

        AssertMql();
    }

    public override async Task Where_select_many_and(bool async)
    {
        // Fails: AV000
        await AssertTranslationFailed(() => base.Where_select_many_and(async));

        AssertMql();
    }

    public override async Task Where_primitive(bool async)
    {
        // Fails: AV000
        Assert.Contains(
            "Values differ", // (Expected 1, got 0)
            (await Assert.ThrowsAsync<EqualException>(async () =>
                await base.Where_primitive(async))).Message);

        AssertMql(
            """
            Employees.{ "$limit" : 9 }, { "$match" : { "_id" : 5 } }, { "$project" : { "_v" : "$_id", "_id" : 0 } }
            """);
    }

    public override async Task Where_bool_member(bool async)
    {
        await base.Where_bool_member(async);

        AssertMql(
            """
            Products.{ "$match" : { "Discontinued" : true } }
            """);
    }

    public override async Task Where_bool_member_false(bool async)
    {
        await base.Where_bool_member_false(async);

        AssertMql(
            """
            Products.{ "$match" : { "Discontinued" : { "$ne" : true } } }
            """);
    }

    public override async Task Where_bool_client_side_negated(bool async)
    {
        // Fails: AV011
        Assert.Contains(
            "Expression not supported: ClientFunc(p.ProductID)",
            (await Assert.ThrowsAsync<ExpressionNotSupportedException>(async () =>
                await base.Where_bool_client_side_negated(async))).Message);

        AssertMql(
            """
            Products.
            """);
    }

    public override async Task Where_bool_member_negated_twice(bool async)
    {
        await base.Where_bool_member_negated_twice(async);

        AssertMql(
            """
            Products.{ "$match" : { "Discontinued" : true } }
            """);
    }

    public override async Task Where_bool_member_shadow(bool async)
    {
        await base.Where_bool_member_shadow(async);

        AssertMql(
            """
            Products.{ "$match" : { "$expr" : "$Discontinued" } }
            """);
    }

    public override async Task Where_bool_member_false_shadow(bool async)
    {
        await base.Where_bool_member_false_shadow(async);
        AssertMql(
            """
            Products.{ "$match" : { "$nor" : [{ "$expr" : "$Discontinued" }] } }
            """);
    }

    public override async Task Where_bool_member_equals_constant(bool async)
    {
        await base.Where_bool_member_equals_constant(async);

        AssertMql(
            """
            Products.{ "$match" : { "Discontinued" : true } }
            """);
    }

    public override async Task Where_bool_member_in_complex_predicate(bool async)
    {
        await base.Where_bool_member_in_complex_predicate(async);

        AssertMql(
            """
            Products.{ "$match" : { "$or" : [{ "_id" : { "$gt" : 100 }, "Discontinued" : true }, { "Discontinued" : true }] } }
            """);
    }

    public override async Task Where_bool_member_compared_to_binary_expression(bool async)
    {
        // Fails: AV000
        Assert.Contains(
            "Values differ", // (Expected 44, got 8)
            (await Assert.ThrowsAsync<EqualException>(async () =>
                await base.Where_bool_member_compared_to_binary_expression(async))).Message);

        AssertMql(
            """
            Products.{ "$match" : { "$expr" : { "$eq" : ["$Discontinued", { "$gt" : ["$_id", 50] }] } } }
            """);
    }

    public override async Task Where_not_bool_member_compared_to_not_bool_member(bool async)
    {
        await base.Where_not_bool_member_compared_to_not_bool_member(async);

        AssertMql(
            """
            Products.{ "$match" : { "$expr" : { "$eq" : [{ "$not" : "$Discontinued" }, { "$not" : "$Discontinued" }] } } }
            """);
    }

    public override async Task Where_negated_boolean_expression_compared_to_another_negated_boolean_expression(bool async)
    {
        // Fails: AV000
        Assert.Contains(
            "Values differ", // (Expected 47, got 77)
            (await Assert.ThrowsAsync<EqualException>(async () =>
                await base.Where_negated_boolean_expression_compared_to_another_negated_boolean_expression(async))).Message);

        AssertMql(
            """
            Products.{ "$match" : { "$expr" : { "$eq" : [{ "$not" : { "$gt" : ["$_id", 50] } }, { "$not" : { "$gt" : ["$_id", 20] } }] } } }
            """);
    }

    public override async Task Where_not_bool_member_compared_to_binary_expression(bool async)
    {
        // Fails: AV000
        Assert.Contains(
            "Values differ", // (Expected 33, got 69)
            (await Assert.ThrowsAsync<EqualException>(async () =>
                await base.Where_not_bool_member_compared_to_binary_expression(async))).Message);

        AssertMql(
            """
            Products.{ "$match" : { "$expr" : { "$eq" : [{ "$not" : "$Discontinued" }, { "$gt" : ["$_id", 50] }] } } }
            """);
    }

    public override async Task Where_bool_parameter(bool async)
    {
        await base.Where_bool_parameter(async);

        AssertMql(
            """
            Products.
            """);
    }

    public override async Task Where_bool_parameter_compared_to_binary_expression(bool async)
    {
        // Fails: AV000
        Assert.Contains(
            "Values differ", // (Expected 50, got 77)
            (await Assert.ThrowsAsync<EqualException>(async () =>
                await base.Where_bool_parameter_compared_to_binary_expression(async))).Message);

        AssertMql(
            """
            Products.{ "$match" : { "_id" : { "$not" : { "$gt" : 50 } } } }
            """);
    }

    public override async Task Where_bool_member_and_parameter_compared_to_binary_expression_nested(bool async)
    {
        // Fails: AV000
        Assert.Contains(
            "Values differ", // (Expected 33, got 69)
            (await Assert.ThrowsAsync<EqualException>(async () =>
                await base.Where_bool_member_and_parameter_compared_to_binary_expression_nested(async))).Message);

        AssertMql(
            """
            Products.{ "$match" : { "$expr" : { "$eq" : ["$Discontinued", { "$ne" : [{ "$gt" : ["$_id", 50] }, true] }] } } }
            """);
    }

    public override async Task Where_de_morgan_or_optimized(bool async)
    {
        // Fails: AV000
        Assert.Contains(
            "Values differ", // (Expected 53, got 69)
            (await Assert.ThrowsAsync<EqualException>(async () =>
                await base.Where_de_morgan_or_optimized(async))).Message);

        AssertMql(
            """
            Products.{ "$match" : { "$nor" : [{ "$or" : [{ "Discontinued" : true }, { "_id" : { "$lt" : 20 } }] }] } }
            """);
    }

    public override async Task Where_de_morgan_and_optimized(bool async)
    {
        // Fails: AV000
        Assert.Contains(
            "Values differ", // (Expected 74, got 77)
            (await Assert.ThrowsAsync<EqualException>(async () =>
                await base.Where_de_morgan_and_optimized(async))).Message);

        AssertMql(
            """
            Products.{ "$match" : { "$nor" : [{ "Discontinued" : true, "_id" : { "$lt" : 20 } }] } }
            """);
    }

    public override async Task Where_complex_negated_expression_optimized(bool async)
    {
        // Fails: AV000
        Assert.Contains(
            "Values differ", // (Expected 27, got 0)
            (await Assert.ThrowsAsync<EqualException>(async () =>
                await base.Where_complex_negated_expression_optimized(async))).Message);

        AssertMql(
            """
            Products.{ "$match" : { "$nor" : [{ "$or" : [{ "$nor" : [{ "Discontinued" : { "$ne" : true }, "_id" : { "$lt" : 60 } }] }, { "_id" : { "$not" : { "$gt" : 30 } } }] }] } }
            """);
    }

    public override async Task Where_short_member_comparison(bool async)
    {
        await base.Where_short_member_comparison(async);

        AssertMql(
            """
            Products.{ "$match" : { "UnitsInStock" : { "$gt" : 10 } } }
            """);
    }

    public override async Task Where_comparison_to_nullable_bool(bool async)
    {
        await base.Where_comparison_to_nullable_bool(async);
        AssertMql(
            """
            Customers.{ "$match" : { "$expr" : { "$eq" : [{ "$let" : { "vars" : { "start" : { "$subtract" : [{ "$strLenCP" : "$_id" }, 2] } }, "in" : { "$and" : [{ "$gte" : ["$$start", 0] }, { "$eq" : [{ "$indexOfCP" : ["$_id", "KI", "$$start"] }, "$$start"] }] } } }, true] } } }
            """);
    }

    public override async Task Where_true(bool async)
    {
        await base.Where_true(async);
        AssertMql(
            """
            Customers.
            """);
    }

    public override async Task Where_false(bool async)
    {
        await base.Where_false(async);

        AssertMql(
            """
            Customers.{ "$match" : { "_id" : { "$type" : -1 } } }
            """);
    }

    public override async Task Where_bool_closure(bool async)
    {
        await base.Where_bool_closure(async);

        #if EF9
        AssertMql(
            """
            Customers.{ "$match" : { "_id" : { "$type" : -1 } } }
            """,
            //
            """
            Customers.{ "$match" : { "_id" : "ALFKI" } }
            """,
            //
            """
            Customers.{ "$match" : { "_id" : "ALFKI" } }
            """,
            //
            """
            Customers.
            """);
        #else
        AssertMql(
            """
            Customers.{ "$match" : { "_id" : { "$type" : -1 } } }
            """,
            //
            """
            Customers.{ "$match" : { "_id" : "ALFKI" } }
            """);
        #endif
    }

    public override async Task Where_default(bool async)
    {
        await base.Where_default(async);

        AssertMql(
            """
            Customers.{ "$match" : { "Fax" : null } }
            """);
    }

    public override async Task Where_expression_invoke_1(bool async)
    {
        await base.Where_expression_invoke_1(async);

        AssertMql(
            """
            Customers.{ "$match" : { "_id" : "ALFKI" } }
            """);
    }

    public override async Task Where_expression_invoke_2(bool async)
    {
        // Fails: AV000
        await AssertTranslationFailed(() => base.Where_expression_invoke_2(async));

        AssertMql();
    }

    public override async Task Where_expression_invoke_3(bool async)
    {
        await base.Where_expression_invoke_3(async);

        AssertMql(
            """
            Customers.{ "$match" : { "_id" : "ALFKI" } }
            """);
    }

    public override async Task Where_ternary_boolean_condition_true(bool async)
    {
        await base.Where_ternary_boolean_condition_true(async);

        AssertMql(
            """
            Products.{ "$match" : { "UnitsInStock" : { "$gte" : 20 } } }
            """);
    }

    public override async Task Where_ternary_boolean_condition_false(bool async)
    {
        await base.Where_ternary_boolean_condition_false(async);

        AssertMql(
            """
            Products.{ "$match" : { "UnitsInStock" : { "$lt" : 20 } } }
            """);
    }

    public override async Task Where_ternary_boolean_condition_with_another_condition(bool async)
    {
        // Fails: AV000
        Assert.Contains(
            "Values differ", // (Expected 9, got 0)
            (await Assert.ThrowsAsync<EqualException>(async () =>
                await base.Where_ternary_boolean_condition_with_another_condition(async))).Message);

        AssertMql(
            """
            Products.{ "$match" : { "_id" : { "$lt" : 15 }, "UnitsInStock" : { "$gte" : 20 } } }
            """);
    }

    public override async Task Where_ternary_boolean_condition_with_false_as_result_true(bool async)
    {
        await base.Where_ternary_boolean_condition_with_false_as_result_true(async);

        AssertMql(
            """
            Products.{ "$match" : { "UnitsInStock" : { "$gte" : 20 } } }
            """);
    }

    public override async Task Where_ternary_boolean_condition_with_false_as_result_false(bool async)
    {
        await base.Where_ternary_boolean_condition_with_false_as_result_false(async);

        AssertMql(
            """
            Products.{ "$match" : { "_id" : { "$type" : -1 } } }
            """);
    }

    #if EF9
    public override async Task Where_ternary_boolean_condition_negated(bool async)
    {
        await base.Where_ternary_boolean_condition_negated(async);

        AssertMql(
            """
            Products.{ "$match" : { "$nor" : [{ "$expr" : { "$cond" : { "if" : { "$gte" : [{ "$toInt" : "$UnitsInStock" }, 20] }, "then" : false, "else" : true } } }] } }
            """);
    }
    #endif

    public override async Task Where_compare_constructed_equal(bool async)
    {
        await base.Where_compare_constructed_equal(async);

        AssertMql(
            """
            Customers.{ "$match" : { "$expr" : { "$eq" : [{ "x" : "$City" }, { "x" : "London" }] } } }
            """);
    }

    public override async Task Where_compare_constructed_multi_value_equal(bool async)
    {
        await base.Where_compare_constructed_multi_value_equal(async);

        AssertMql(
            """
            Customers.{ "$match" : { "$expr" : { "$eq" : [{ "x" : "$City", "y" : "$Country" }, { "x" : "London", "y" : "UK" }] } } }
            """);
    }

    public override async Task Where_compare_constructed_multi_value_not_equal(bool async)
    {
        // Fails: AV000
        Assert.Contains(
            "Values differ", // (Expected 91, got 85)
            (await Assert.ThrowsAsync<EqualException>(async () =>
                await base.Where_compare_constructed_multi_value_not_equal(async))).Message);

        AssertMql(
            """
            Customers.{ "$match" : { "$expr" : { "$ne" : [{ "x" : "$City", "y" : "$Country" }, { "x" : "London", "y" : "UK" }] } } }
            """);
    }

    public override async Task Where_compare_tuple_constructed_equal(bool async)
    {
        await base.Where_compare_tuple_constructed_equal(async);

        AssertMql(
            """
            Customers.{ "$match" : { "$expr" : { "$eq" : [["$City"], ["London"]] } } }
            """);
    }

    public override async Task Where_compare_tuple_constructed_multi_value_equal(bool async)
    {
        await base.Where_compare_tuple_constructed_multi_value_equal(async);

        AssertMql(
            """
            Customers.{ "$match" : { "$expr" : { "$eq" : [["$City", "$Country"], ["London", "UK"]] } } }
            """);
    }

    public override async Task Where_compare_tuple_constructed_multi_value_not_equal(bool async)
    {
        await base.Where_compare_tuple_constructed_multi_value_not_equal(async);

        AssertMql(
            """
            Customers.{ "$match" : { "$expr" : { "$ne" : [["$City", "$Country"], ["London", "UK"]] } } }
            """);
    }

    public override async Task Where_compare_tuple_create_constructed_equal(bool async)
    {
        await base.Where_compare_tuple_create_constructed_equal(async);

        AssertMql(
            """
            Customers.{ "$match" : { "$expr" : { "$eq" : [["$City"], ["London"]] } } }
            """);
    }

    public override async Task Where_compare_tuple_create_constructed_multi_value_equal(bool async)
    {
        await base.Where_compare_tuple_create_constructed_multi_value_equal(async);

        AssertMql(
            """
            Customers.{ "$match" : { "$expr" : { "$eq" : [["$City", "$Country"], ["London", "UK"]] } } }
            """);
    }

    public override async Task Where_compare_tuple_create_constructed_multi_value_not_equal(bool async)
    {
        await base.Where_compare_tuple_create_constructed_multi_value_not_equal(async);

        AssertMql(
            """
            Customers.{ "$match" : { "$expr" : { "$ne" : [["$City", "$Country"], ["London", "UK"]] } } }
            """);
    }

    public override async Task Where_compare_null(bool async)
    {
        await base.Where_compare_null(async);

        AssertMql(
            """
            Customers.{ "$match" : { "Region" : null, "Country" : "UK" } }
            """);
    }

    public override async Task Where_Is_on_same_type(bool async)
    {
        await base.Where_Is_on_same_type(async);

        AssertMql(
            """
            Customers.
            """);
    }

    public override async Task Where_chain(bool async)
    {
        // Fails: AV000
        Assert.Contains(
            "Values differ", // (Expected 8, got 0)
            (await Assert.ThrowsAsync<EqualException>(async () =>
                await base.Where_chain(async))).Message);

        AssertMql(
            """
            Products.{ "$match" : { "CustomerID" : "QUICK" } }, { "$match" : { "OrderDate" : { "$gt" : { "$date" : "1998-01-01T00:00:00Z" } } } }
            """);
    }

    public override async Task Where_navigation_contains(bool async)
    {
        // Fails: AV000
        Assert.Contains(
            "Including navigation 'Navigation' is not supported",
            (await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await base.Where_navigation_contains(async))).Message);

        AssertMql();
    }

    public override async Task Where_array_index(bool async)
    {
        await base.Where_array_index(async);

        AssertMql(
            """
            Customers.{ "$match" : { "_id" : "ALFKI" } }
            """);
    }

    public override async Task Where_multiple_contains_in_subquery_with_or(bool async)
    {
        // Fails: AV007
        Assert.Contains(
            "cannot be used for parameter",
            (await Assert.ThrowsAsync<ArgumentException>(async () =>
                await base.Where_multiple_contains_in_subquery_with_or(async))).Message);

        AssertMql(
        );
    }

    public override async Task Where_multiple_contains_in_subquery_with_and(bool async)
    {
        // Fails: AV007
        Assert.Contains(
            "cannot be used for parameter",
            (await Assert.ThrowsAsync<ArgumentException>(async () =>
                await base.Where_multiple_contains_in_subquery_with_and(async))).Message);

        AssertMql();
    }

    public override async Task Where_contains_on_navigation(bool async)
    {
        // Fails: AV007
        Assert.Contains(
            "cannot be used for parameter",
            (await Assert.ThrowsAsync<ArgumentException>(async () =>
                await base.Where_contains_on_navigation(async))).Message);

        AssertMql();
    }

    public override async Task Where_subquery_FirstOrDefault_is_null(bool async)
    {
        // Fails: AV007
        Assert.Contains(
            "cannot be used for parameter",
            (await Assert.ThrowsAsync<ArgumentException>(async () =>
                await base.Where_subquery_FirstOrDefault_is_null(async))).Message);

        AssertMql();
    }

    public override async Task Where_subquery_FirstOrDefault_compared_to_entity(bool async)
    {
        // Fails: AV007
        Assert.Contains(
            "cannot be used for parameter",
            (await Assert.ThrowsAsync<ArgumentException>(async () =>
                await base.Where_subquery_FirstOrDefault_compared_to_entity(async))).Message);

        AssertMql();
    }

    public override async Task TypeBinary_short_circuit(bool async)
    {
        await base.TypeBinary_short_circuit(async);

        AssertMql(
            """
            Products.{ "$match" : { "_id" : { "$type" : -1 } } }
            """);
    }

    public override async Task Decimal_cast_to_double_works(bool async)
    {
        await base.Decimal_cast_to_double_works(async);

        AssertMql(
            """
            Products.{ "$match" : { "$expr" : { "$gt" : [{ "$toDouble" : "$UnitPrice" }, 100.0] } } }
            """);
    }

    public override async Task Where_is_conditional(bool async)
    {
        await base.Where_is_conditional(async);

        AssertMql(
            """
            Products.{ "$match" : { "$expr" : { "$cond" : { "if" : true, "then" : false, "else" : true } } } }
            """);
    }

    public override async Task Filter_non_nullable_value_after_FirstOrDefault_on_empty_collection(bool async)
    {
        // Fails: AV007
        Assert.Contains(
            "cannot be used for parameter",
            (await Assert.ThrowsAsync<ArgumentException>(async () =>
                await base.Filter_non_nullable_value_after_FirstOrDefault_on_empty_collection(async))).Message);

        AssertMql();
    }

    public override async Task Using_same_parameter_twice_in_query_generates_one_sql_parameter(bool async)
    {
        await base.Using_same_parameter_twice_in_query_generates_one_sql_parameter(async);

        AssertMql(
            """
            Customers.{ "$match" : { "$expr" : { "$eq" : [{ "$concat" : ["10", "$_id", "10"] }, "10ALFKI10"] } } }, { "$project" : { "_v" : "$_id", "_id" : 0 } }
            """);
    }

    public override async Task Where_Queryable_ToList_Count(bool async)
    {
        // Fails: AV000
        await AssertTranslationFailed(() => base.Where_Queryable_ToList_Count(async));

        AssertMql();
    }

    public override async Task Where_Queryable_ToList_Contains(bool async)
    {
        // Fails: AV000
        await AssertTranslationFailed(() => base.Where_Queryable_ToList_Contains(async));

        AssertMql();
    }

    public override async Task Where_Queryable_ToArray_Count(bool async)
    {
        // Fails: AV000
        await AssertTranslationFailed(() => base.Where_Queryable_ToArray_Count(async));

        AssertMql();
    }

    public override async Task Where_Queryable_ToArray_Contains(bool async)
    {
        // Fails: AV000
        await AssertTranslationFailed(() => base.Where_Queryable_ToArray_Contains(async));

        AssertMql();
    }

    public override async Task Where_Queryable_AsEnumerable_Count(bool async)
    {
        // Fails: AV000
        await AssertTranslationFailed(() => base.Where_Queryable_AsEnumerable_Count(async));

        AssertMql();
    }

    public override async Task Where_Queryable_AsEnumerable_Contains(bool async)
    {
        // Fails: AV000
        await AssertTranslationFailed(() => base.Where_Queryable_AsEnumerable_Contains(async));

        AssertMql();
    }

    public override async Task Where_Queryable_ToList_Count_member(bool async)
    {
        // Fails: AV000
        await AssertTranslationFailed(() => base.Where_Queryable_ToList_Count_member(async));

        AssertMql();
    }

    public override async Task Where_Queryable_ToArray_Length_member(bool async)
    {
        // Fails: AV000
        await AssertTranslationFailed(() => base.Where_Queryable_ToArray_Length_member(async));

        AssertMql();
    }

    public override async Task Where_collection_navigation_ToList_Count(bool async)
    {
        // Fails: AV000
        await AssertTranslationFailed(() => base.Where_collection_navigation_ToList_Count(async));

        AssertMql();
    }

    public override async Task Where_collection_navigation_ToList_Contains(bool async)
    {
        // Fails: AV000
        await AssertTranslationFailed(() => base.Where_collection_navigation_ToList_Contains(async));

        AssertMql();
    }

    public override async Task Where_collection_navigation_ToArray_Count(bool async)
    {
        // Fails: AV000
        await AssertTranslationFailed(() => base.Where_collection_navigation_ToArray_Count(async));

        AssertMql();
    }

    public override async Task Where_collection_navigation_ToArray_Contains(bool async)
    {
        // Fails: AV000
        await AssertTranslationFailed(() => base.Where_collection_navigation_ToArray_Contains(async));

        AssertMql();
    }

    public override async Task Where_collection_navigation_AsEnumerable_Count(bool async)
    {
        // Fails: AV000
        await AssertTranslationFailed(() => base.Where_collection_navigation_AsEnumerable_Count(async));

        AssertMql();
    }

    public override async Task Where_collection_navigation_AsEnumerable_Contains(bool async)
    {
        // Fails: AV000
        await AssertTranslationFailed(() => base.Where_collection_navigation_AsEnumerable_Contains(async));

        AssertMql();
    }

    public override async Task Where_collection_navigation_ToList_Count_member(bool async)
    {
        // Fails: AV000
        await AssertTranslationFailed(() => base.Where_collection_navigation_ToList_Count_member(async));

        AssertMql();
    }

    public override async Task Where_collection_navigation_ToArray_Length_member(bool async)
    {
        // Fails: AV000
        await AssertTranslationFailed(() => base.Where_collection_navigation_ToArray_Length_member(async));

        AssertMql();
    }

    public override async Task Where_Queryable_AsEnumerable_Contains_negated(bool async)
    {
        // Fails: AV000
        await AssertTranslationFailed(() => base.Where_Queryable_AsEnumerable_Contains_negated(async));

        AssertMql();
    }

    public override async Task Where_list_object_contains_over_value_type(bool async)
    {
        // Fails: AV000
        Assert.Contains(
            "Values differ", // (Expected 2, got 0)
            (await Assert.ThrowsAsync<EqualException>(async () =>
                await base.Where_list_object_contains_over_value_type(async))).Message);

        AssertMql(
            """
            Products.{ "$match" : { "_id" : { "$in" : [10248, 10249] } } }
            """);
    }

    public override async Task Where_array_of_object_contains_over_value_type(bool async)
    {
        // Fails: AV000
        Assert.Contains(
            "Values differ", // (Expected 2, got 0)
            (await Assert.ThrowsAsync<EqualException>(async () =>
                await base.Where_array_of_object_contains_over_value_type(async))).Message);

        AssertMql(
            """
            Products.{ "$match" : { "_id" : { "$in" : [10248, 10249] } } }
            """);
    }

    public override async Task Filter_with_EF_Property_using_closure_for_property_name(bool async)
    {
        await base.Filter_with_EF_Property_using_closure_for_property_name(async);

        AssertMql(
            """
            Customers.{ "$match" : { "_id" : "ALFKI" } }
            """);
    }

    public override async Task Filter_with_EF_Property_using_function_for_property_name(bool async)
    {
        await base.Filter_with_EF_Property_using_function_for_property_name(async);

        AssertMql(
            """
            Customers.{ "$match" : { "_id" : "ALFKI" } }
            """);
    }

    public override async Task FirstOrDefault_over_scalar_projection_compared_to_null(bool async)
    {
        // Fails: AV007
        Assert.Contains(
            "cannot be used for parameter",
            (await Assert.ThrowsAsync<ArgumentException>(async () =>
                await base.FirstOrDefault_over_scalar_projection_compared_to_null(async))).Message);

        AssertMql();
    }

    public override async Task FirstOrDefault_over_scalar_projection_compared_to_not_null(bool async)
    {
        // Fails: AV007
        Assert.Contains(
            "cannot be used for parameter",
            (await Assert.ThrowsAsync<ArgumentException>(async () =>
                await base.FirstOrDefault_over_scalar_projection_compared_to_not_null(async))).Message);

        AssertMql();
    }

    public override async Task FirstOrDefault_over_custom_projection_compared_to_null(bool async)
    {
        // Fails: AV007
        Assert.Contains(
            "cannot be used for parameter",
            (await Assert.ThrowsAsync<ArgumentException>(async () =>
                await base.FirstOrDefault_over_custom_projection_compared_to_null(async))).Message);

        AssertMql();
    }

    public override async Task FirstOrDefault_over_custom_projection_compared_to_not_null(bool async)
    {
        // Fails: AV007
        Assert.Contains(
            "cannot be used for parameter",
            (await Assert.ThrowsAsync<ArgumentException>(async () =>
                await base.FirstOrDefault_over_custom_projection_compared_to_not_null(async))).Message);

        AssertMql();
    }

    public override async Task SingleOrDefault_over_custom_projection_compared_to_null(bool async)
    {
        // Fails: AV007
        Assert.Contains(
            "cannot be used for parameter",
            (await Assert.ThrowsAsync<ArgumentException>(async () =>
                await base.SingleOrDefault_over_custom_projection_compared_to_null(async))).Message);

        AssertMql();
    }

    public override async Task SingleOrDefault_over_custom_projection_compared_to_not_null(bool async)
    {
        // Fails: AV007
        Assert.Contains(
            "cannot be used for parameter",
            (await Assert.ThrowsAsync<ArgumentException>(async () =>
                await base.SingleOrDefault_over_custom_projection_compared_to_not_null(async))).Message);

        AssertMql();
    }

    public override async Task LastOrDefault_over_custom_projection_compared_to_null(bool async)
    {
        // Fails: AV007
        Assert.Contains(
            "cannot be used for parameter",
            (await Assert.ThrowsAsync<ArgumentException>(async () =>
                await base.LastOrDefault_over_custom_projection_compared_to_null(async))).Message);

        AssertMql();
    }

    public override async Task LastOrDefault_over_custom_projection_compared_to_not_null(bool async)
    {
        // Fails: AV007
        Assert.Contains(
            "cannot be used for parameter",
            (await Assert.ThrowsAsync<ArgumentException>(async () =>
                await base.LastOrDefault_over_custom_projection_compared_to_not_null(async))).Message);

        AssertMql();
    }

    public override async Task First_over_custom_projection_compared_to_null(bool async)
    {
        // Fails: AV007
        Assert.Contains(
            "cannot be used for parameter",
            (await Assert.ThrowsAsync<ArgumentException>(async () =>
                await base.First_over_custom_projection_compared_to_null(async))).Message);

        AssertMql();
    }

    public override async Task First_over_custom_projection_compared_to_not_null(bool async)
    {
        // Fails: AV007
        Assert.Contains(
            "cannot be used for parameter",
            (await Assert.ThrowsAsync<ArgumentException>(async () =>
                await base.First_over_custom_projection_compared_to_not_null(async))).Message);

        AssertMql();
    }

    public override async Task ElementAt_over_custom_projection_compared_to_not_null(bool async)
    {
        // Fails: AV007
        Assert.Contains(
            "cannot be used for parameter",
            (await Assert.ThrowsAsync<ArgumentException>(async () =>
                await base.ElementAt_over_custom_projection_compared_to_not_null(async))).Message);

        AssertMql();
    }

    public override async Task ElementAtOrDefault_over_custom_projection_compared_to_null(bool async)
    {
        // Fails: AV007
        Assert.Contains(
            "cannot be used for parameter",
            (await Assert.ThrowsAsync<ArgumentException>(async () =>
                await base.ElementAtOrDefault_over_custom_projection_compared_to_null(async))).Message);

        AssertMql();
    }

    public override async Task Single_over_custom_projection_compared_to_null(bool async)
    {
        // Fails: AV007
        Assert.Contains(
            "cannot be used for parameter",
            (await Assert.ThrowsAsync<ArgumentException>(async () =>
                await base.Single_over_custom_projection_compared_to_null(async))).Message);

        AssertMql();
    }

    public override async Task Single_over_custom_projection_compared_to_not_null(bool async)
    {
        // Fails: AV007
        Assert.Contains(
            "cannot be used for parameter",
            (await Assert.ThrowsAsync<ArgumentException>(async () =>
                await base.Single_over_custom_projection_compared_to_not_null(async))).Message);

        AssertMql();
    }

    public override async Task Last_over_custom_projection_compared_to_null(bool async)
    {
        // Fails: AV007
        Assert.Contains(
            "cannot be used for parameter",
            (await Assert.ThrowsAsync<ArgumentException>(async () =>
                await base.Last_over_custom_projection_compared_to_null(async))).Message);

        AssertMql();
    }

    public override async Task Last_over_custom_projection_compared_to_not_null(bool async)
    {
        // Fails: AV007
        Assert.Contains(
            "cannot be used for parameter",
            (await Assert.ThrowsAsync<ArgumentException>(async () =>
                await base.Last_over_custom_projection_compared_to_not_null(async))).Message);

        AssertMql();
    }

    public override async Task Where_Contains_and_comparison(bool async)
    {
        await base.Where_Contains_and_comparison(async);

        AssertMql(
            """
            Customers.{ "$match" : { "_id" : { "$in" : ["ALFKI", "FISSA", "WHITC"] }, "City" : "Seattle" } }
            """);
    }

    public override async Task Where_Contains_or_comparison(bool async)
    {
        await base.Where_Contains_or_comparison(async);

        AssertMql(
            """
            Customers.{ "$match" : { "$or" : [{ "_id" : { "$in" : ["ALFKI", "FISSA"] } }, { "City" : "Seattle" }] } }
            """);
    }

    public override async Task GetType_on_non_hierarchy1(bool async)
    {
        await base.GetType_on_non_hierarchy1(async);

        AssertMql(
            """
            Customers.{ "$match" : { "_t" : null } }
            """);
    }

    public override async Task GetType_on_non_hierarchy2(bool async)
    {
        await base.GetType_on_non_hierarchy2(async);

        AssertMql(
            """
            Customers.{ "$match" : { "_t" : { "$ne" : null } } }
            """);
    }

    public override async Task GetType_on_non_hierarchy3(bool async)
    {
        // Fails: AV000
        Assert.Contains(
            "Values differ", // (Expected 0 got 91)
            (await Assert.ThrowsAsync<EqualException>(async () =>
                await base.GetType_on_non_hierarchy3(async))).Message);

        AssertMql(
            """
            Customers.{ "$match" : { "_t" : null } }
            """);
    }

    public override async Task GetType_on_non_hierarchy4(bool async)
    {
        // Fails: AV000
        Assert.Contains(
            "Values differ", // (Expected 0 got 91)
            (await Assert.ThrowsAsync<EqualException>(async () =>
                await base.GetType_on_non_hierarchy4(async))).Message);

        AssertMql(
            """
            Customers.{ "$match" : { "_t" : { "$ne" : null } } }
            """);
    }

    public override async Task Case_block_simplification_works_correctly(bool async)
    {
        await base.Case_block_simplification_works_correctly(async);
        AssertMql(
            """
            Customers.{ "$match" : { "$expr" : { "$eq" : [{ "$cond" : { "if" : { "$eq" : ["$Region", null] }, "then" : "OR", "else" : "$Region" } }, "OR"] } } }
            """);
    }

    public override async Task Where_compare_null_with_cast_to_object(bool async)
    {
        await base.Where_compare_null_with_cast_to_object(async);

        AssertMql(
            """
            Customers.{ "$match" : { "Region" : null } }
            """);
    }

    public override async Task Where_compare_with_both_cast_to_object(bool async)
    {
        await base.Where_compare_with_both_cast_to_object(async);

        AssertMql(
            """
            Customers.{ "$match" : { "City" : "London" } }
            """);
    }

    public override async Task Where_projection(bool async)
    {
        await base.Where_projection(async);

        AssertMql(
            """
            Customers.{ "$match" : { "City" : "London" } }, { "$project" : { "_v" : "$CompanyName", "_id" : 0 } }
            """);
    }

    public override async Task Enclosing_class_settable_member_generates_parameter(bool async)
    {
        // Fails: AV000
        Assert.Contains(
            "Values differ", // (Expected 1, got 0)
            (await Assert.ThrowsAsync<EqualException>(async () =>
                await base.Enclosing_class_settable_member_generates_parameter(async))).Message);

        AssertMql(
            """
            Products.{ "$match" : { "_id" : 10274 } }
            """);
    }

    public override async Task Enclosing_class_readonly_member_generates_parameter(bool async)
    {
        // Fails: AV000
        Assert.Contains(
            "Values differ", // (Expected 1, got 0)
            (await Assert.ThrowsAsync<EqualException>(async () =>
                await base.Enclosing_class_readonly_member_generates_parameter(async))).Message);

        AssertMql(
            """
            Products.{ "$match" : { "_id" : 10275 } }
            """);
    }

    public override async Task Enclosing_class_const_member_does_not_generate_parameter(bool async)
    {
        // Fails: AV000
        Assert.Contains(
            "Values differ", // (Expected 1, got 0)
            (await Assert.ThrowsAsync<EqualException>(async () =>
                await base.Enclosing_class_const_member_does_not_generate_parameter(async))).Message);

        AssertMql(
            """
            Products.{ "$match" : { "_id" : 10274 } }
            """);
    }

    public override async Task Generic_Ilist_contains_translates_to_server(bool async)
    {
        await base.Generic_Ilist_contains_translates_to_server(async);
        AssertMql(
            """
            Customers.{ "$match" : { "City" : { "$in" : ["Seattle"] } } }
            """);
    }

    public override async Task Multiple_OrElse_on_same_column_converted_to_in_with_overlap(bool async)
    {
        await base.Multiple_OrElse_on_same_column_converted_to_in_with_overlap(async);

        AssertMql(
            """
            Customers.{ "$match" : { "$or" : [{ "_id" : "ALFKI" }, { "_id" : "ANATR" }, { "_id" : "ANTON" }, { "_id" : "ANATR" }] } }
            """);
    }

    public override async Task Multiple_OrElse_on_same_column_with_null_constant_comparison_converted_to_in(bool async)
    {
        await base.Multiple_OrElse_on_same_column_with_null_constant_comparison_converted_to_in(async);
        AssertMql(
            """
            Customers.{ "$match" : { "$or" : [{ "Region" : "WA" }, { "Region" : "OR" }, { "Region" : null }, { "Region" : "BC" }] } }
            """);
    }

    public override async Task Constant_array_Contains_OrElse_comparison_with_constant_gets_combined_to_one_in(bool async)
    {
        await base.Constant_array_Contains_OrElse_comparison_with_constant_gets_combined_to_one_in(async);
        AssertMql(
            """
            Customers.{ "$match" : { "$or" : [{ "_id" : { "$in" : ["ALFKI", "ANATR"] } }, { "_id" : "ANTON" }] } }
            """);
    }

    public override async Task
        Constant_array_Contains_OrElse_comparison_with_constant_gets_combined_to_one_in_with_overlap(bool async)
    {
        await base.Constant_array_Contains_OrElse_comparison_with_constant_gets_combined_to_one_in_with_overlap(async);

        AssertMql(
            """
            Customers.{ "$match" : { "$or" : [{ "_id" : "ANTON" }, { "_id" : { "$in" : ["ALFKI", "ANATR"] } }, { "_id" : "ALFKI" }] } }
            """);
    }

    public override async Task Constant_array_Contains_OrElse_another_Contains_gets_combined_to_one_in_with_overlap(bool async)
    {
        await base.Constant_array_Contains_OrElse_another_Contains_gets_combined_to_one_in_with_overlap(async);

        AssertMql(
            """
            Customers.{ "$match" : { "$or" : [{ "_id" : { "$in" : ["ALFKI", "ANATR"] } }, { "_id" : { "$in" : ["ALFKI", "ANTON"] } }] } }
            """);
    }

    public override async Task Constant_array_Contains_AndAlso_another_Contains_gets_combined_to_one_in_with_overlap(bool async)
    {
        await base.Constant_array_Contains_AndAlso_another_Contains_gets_combined_to_one_in_with_overlap(async);

        AssertMql(
            """
            Customers.{ "$match" : { "$and" : [{ "_id" : { "$nin" : ["ALFKI", "ANATR"] } }, { "_id" : { "$nin" : ["ALFKI", "ANTON"] } }] } }
            """);
    }

    public override async Task Multiple_AndAlso_on_same_column_converted_to_in_using_parameters(bool async)
    {
        await base.Multiple_AndAlso_on_same_column_converted_to_in_using_parameters(async);

        AssertMql(
            """
            Customers.{ "$match" : { "$and" : [{ "_id" : { "$ne" : "ALFKI" } }, { "_id" : { "$ne" : "ANATR" } }, { "_id" : { "$ne" : "ANTON" } }] } }
            """);
    }

    public override async Task Array_of_parameters_Contains_OrElse_comparison_with_constant_gets_combined_to_one_in(bool async)
    {
        await base.Array_of_parameters_Contains_OrElse_comparison_with_constant_gets_combined_to_one_in(async);

        AssertMql(
            """
            Customers.{ "$match" : { "$or" : [{ "_id" : { "$in" : ["ALFKI", "ANATR"] } }, { "_id" : "ANTON" }] } }
            """);
    }

    public override async Task Multiple_OrElse_on_same_column_with_null_parameter_comparison_converted_to_in(bool async)
    {
        await base.Multiple_OrElse_on_same_column_with_null_parameter_comparison_converted_to_in(async);

        AssertMql(
            """
            Customers.{ "$match" : { "$or" : [{ "Region" : "WA" }, { "Region" : "OR" }, { "Region" : null }, { "Region" : "BC" }] } }
            """);
    }

    public override async Task Parameter_array_Contains_OrElse_comparison_with_constant(bool async)
    {
        await base.Parameter_array_Contains_OrElse_comparison_with_constant(async);

        AssertMql(
            """
            Customers.{ "$match" : { "$or" : [{ "_id" : { "$in" : ["ALFKI", "ANATR"] } }, { "_id" : "ANTON" }] } }
            """);
    }

    public override async Task Parameter_array_Contains_OrElse_comparison_with_parameter_with_overlap(bool async)
    {
        await base.Parameter_array_Contains_OrElse_comparison_with_parameter_with_overlap(async);

        AssertMql(
            """
            Customers.{ "$match" : { "$or" : [{ "_id" : "ANTON" }, { "_id" : { "$in" : ["ALFKI", "ANATR"] } }, { "_id" : "ALFKI" }] } }
            """);
    }

    public override async Task Two_sets_of_comparison_combine_correctly(bool async)
    {
        await base.Two_sets_of_comparison_combine_correctly(async);

        AssertMql(
            """
            Customers.{ "$match" : { "$and" : [{ "_id" : { "$in" : ["ALFKI", "ANATR"] } }, { "$or" : [{ "_id" : "ANATR" }, { "_id" : "ANTON" }] }] } }
            """);
    }

    public override async Task Two_sets_of_comparison_combine_correctly2(bool async)
    {
        await base.Two_sets_of_comparison_combine_correctly2(async);

        AssertMql(
            """
            Customers.{ "$match" : { "$or" : [{ "$and" : [{ "Region" : { "$ne" : "WA" } }, { "Region" : { "$ne" : "OR" } }, { "Region" : { "$ne" : null } }] }, { "$and" : [{ "Region" : { "$ne" : "WA" } }, { "Region" : { "$ne" : null } }] }] } }
            """);
    }

    public override async Task Filter_with_property_compared_to_null_wrapped_in_explicit_convert_to_object(bool async)
    {
        await base.Filter_with_property_compared_to_null_wrapped_in_explicit_convert_to_object(async);

        AssertMql(
            """
            Customers.{ "$match" : { "Region" : null } }
            """);
    }

    public override async Task Where_nested_field_access_closure_via_query_cache_error_null(bool async)
    {
        await base.Where_nested_field_access_closure_via_query_cache_error_null(async);

        AssertMql();
    }

    public override async Task Where_nested_field_access_closure_via_query_cache_error_method_null(bool async)
    {
        await base.Where_nested_field_access_closure_via_query_cache_error_method_null(async);

        AssertMql();
    }

    public override async Task Where_simple_shadow_projection_mixed(bool async)
    {
        // Fails: AV012
        await Assert.ThrowsAsync<NullReferenceException>(async () => await base.Where_simple_shadow_projection_mixed(async));

        AssertMql();
    }

    public override async Task Where_primitive_tracked(bool async)
    {
        // Fails: AV000
        Assert.Contains(
            "Values differ", // (Expected 1, got 0)
            (await Assert.ThrowsAsync<EqualException>(async () =>
                await base.Where_primitive_tracked(async))).Message);

        AssertMql(
            """
            Employees.{ "$limit" : 9 }, { "$match" : { "_id" : 5 } }
            """);
    }

    public override async Task Where_primitive_tracked2(bool async)
    {
        // Fails: AV000
        Assert.Contains(
            "Values differ", // (Expected 1, got 0)
            (await Assert.ThrowsAsync<EqualException>(async () =>
                await base.Where_primitive_tracked2(async))).Message);

        AssertMql(
            """
            Employees.{ "$limit" : 9 }, { "$match" : { "_id" : 5 } }, { "$project" : { "e" : "$$ROOT", "_id" : 0 } }
            """);
    }

    public override async Task Where_poco_closure(bool async)
    {
        // Fails: AV000
        Assert.Contains(
            "Entity to entity comparison is not supported.",
            (await Assert.ThrowsAsync<NotSupportedException>(async () =>
                await base.Where_poco_closure(async))).Message);

        AssertMql(
            """
            Customers.
            """);
    }

    #if EF9
    public override async Task EF_Constant(bool async)
    {
        // Fails: AV000 (EF.Constant not supported on Mongo)
        Assert.Equal(
            CoreStrings.EFConstantNotSupported,
            (await Assert.ThrowsAsync<InvalidOperationException>(() => base.EF_Constant(async))).Message);

        AssertMql();
    }

    public override async Task EF_Constant_with_subtree(bool async)
    {
        // Fails: AV000 (EF.Constant not supported on Mongo)
        Assert.Equal(
            CoreStrings.EFConstantNotSupported,
            (await Assert.ThrowsAsync<InvalidOperationException>(() => base.EF_Constant_with_subtree(async))).Message);

        AssertMql();
    }

    public override async Task EF_Constant_does_not_parameterized_as_part_of_bigger_subtree(bool async)
    {
        // Fails: AV000 (EF.Constant not supported on Mongo)
        Assert.Equal(
            CoreStrings.EFConstantNotSupported,
            (await Assert.ThrowsAsync<InvalidOperationException>(() => base.EF_Constant_does_not_parameterized_as_part_of_bigger_subtree(async))).Message);

        AssertMql();
    }

    public override async Task EF_Constant_with_non_evaluatable_argument_throws(bool async)
    {
        await base.EF_Constant_with_non_evaluatable_argument_throws(async);

        AssertMql();
    }
    #else
    public override async Task EF_Constant(bool async)
    {
        await base.EF_Constant(async);

        AssertMql(
            """
Customers.{ "$match" : { "_id" : "ALFKI" } }
""");
    }

    public override async Task EF_Constant_with_subtree(bool async)
    {
        await base.EF_Constant_with_subtree(async);

        AssertMql(
            """
Customers.{ "$match" : { "_id" : "ALFKI" } }
""");
    }

    public override async Task EF_Constant_does_not_parameterized_as_part_of_bigger_subtree(bool async)
    {
        await base.EF_Constant_does_not_parameterized_as_part_of_bigger_subtree(async);

        AssertMql(
            """
Customers.{ "$match" : { "_id" : "ALFKI" } }
""");
    }

    public override async Task EF_Constant_with_non_evaluatable_argument_throws(bool async)
    {
        await base.EF_Constant_with_non_evaluatable_argument_throws(async);

        AssertMql();
    }
    #endif

    #if EF9

    public override async Task EF_Parameter(bool async)
    {
        await base.EF_Parameter(async);

        AssertMql(
            """
            Customers.{ "$match" : { "_id" : "ALFKI" } }
            """);
    }

    public override async Task EF_Parameter_with_subtree(bool async)
    {
        await base.EF_Parameter_with_subtree(async);

        AssertMql(
            """
            Customers.{ "$match" : { "_id" : "ALFKI" } }
            """);
    }

    public override async Task EF_Parameter_does_not_parameterized_as_part_of_bigger_subtree(bool async)
    {
        await base.EF_Parameter_does_not_parameterized_as_part_of_bigger_subtree(async);
        AssertMql(
            """
            Customers.{ "$match" : { "_id" : "ALFKI" } }
            """);
    }

    public override async Task EF_Parameter_with_non_evaluatable_argument_throws(bool async)
    {
        await base.EF_Parameter_with_non_evaluatable_argument_throws(async);

        AssertMql();
    }

    public override async Task Implicit_cast_in_predicate(bool async)
    {
        await base.Implicit_cast_in_predicate(async);

        AssertMql(
            """
            Products.{ "$match" : { "CustomerID" : "1337" } }
            """,
            //
            """
            Products.{ "$match" : { "CustomerID" : "1337" } }
            """,
            //
            """
            Products.{ "$match" : { "CustomerID" : "1337" } }
            """,
            //
            """
            Products.{ "$match" : { "CustomerID" : "1337" } }
            """,
            //
            """
            Products.{ "$match" : { "CustomerID" : "1337" } }
            """);
    }

    public override async Task Interface_casting_though_generic_method(bool async)
    {
        // Fails: AV000
        Assert.Contains(
            "Values differ", // (Expected 1, got 0)
            (await Assert.ThrowsAsync<EqualException>(async () =>
                await base.Interface_casting_though_generic_method(async))).Message);

        AssertMql(
            """
            Products.{ "$match" : { "_id" : 10252 } }, { "$project" : { "_id" : "$_id" } }
            """);
    }

    public override async Task Simplifiable_coalesce_over_nullable(bool async)
    {
        // Fails: AV000
        Assert.Contains(
            "Values differ", // (Expected 1, got 0)
            (await Assert.ThrowsAsync<EqualException>(async () =>
                await base.Simplifiable_coalesce_over_nullable(async))).Message);

        AssertMql(
            """
            Products.{ "$match" : { "_id" : 10248 } }
            """);
    }

    public override async Task Take_and_Where_evaluation_order(bool async)
    {
        // Fails: AV000
        Assert.Contains(
            "Values differ", // (Expected 3, got 0)
            (await Assert.ThrowsAsync<EqualException>(async () =>
                await base.Take_and_Where_evaluation_order(async))).Message);

        AssertMql(
            """
            Employees.{ "$sort" : { "_id" : 1 } }, { "$limit" : 3 }, { "$match" : { "_id" : { "$mod" : [2, 0] } } }
            """);
    }

    public override async Task Skip_and_Where_evaluation_order(bool async)
    {
        // Fails: AV000
        Assert.Contains(
            "Values differ", // (Expected 3, got 0)
            (await Assert.ThrowsAsync<EqualException>(async () =>
                await base.Skip_and_Where_evaluation_order(async))).Message);

        AssertMql(
            """
            Employees.{ "$sort" : { "_id" : 1 } }, { "$skip" : 3 }, { "$match" : { "_id" : { "$mod" : [2, 0] } } }
            """);
    }

    public override async Task Take_and_Distinct_evaluation_order(bool async)
    {
        await base.Take_and_Distinct_evaluation_order(async);

        AssertMql(
            """
            Customers.{ "$sort" : { "ContactTitle" : 1 } }, { "$limit" : 3 }, { "$project" : { "_v" : "$ContactTitle", "_id" : 0 } }, { "$group" : { "_id" : "$$ROOT" } }, { "$replaceRoot" : { "newRoot" : "$_id" } }
            """);
    }
    #endif

    public override async Task Where_bitwise_or(bool async)
    {
        await base.Where_bitwise_or(async);

        AssertMql(
            """
            Customers.{ "$match" : { "$or" : [{ "_id" : "ALFKI" }, { "_id" : "ANATR" }] } }
            """);
    }

    public override async Task Where_bitwise_and(bool async)
    {
        await base.Where_bitwise_and(async);

        AssertMql(
            """
            Customers.{ "$match" : { "$and" : [{ "_id" : "ALFKI" }, { "_id" : "ANATR" }] } }
            """);
    }

    public override async Task Where_bitwise_xor(bool async)
    {
        // Fails: AV000
        Assert.Contains(
            "because MongoDB does not have a boolean $xor operator",
            (await Assert.ThrowsAsync<ExpressionNotSupportedException>(async () =>
                await base.Where_bitwise_xor(async))).Message);

        AssertMql(
            """
            Customers.
            """);
    }

    public override async Task Where_equals_method_string(bool async)
    {
        await base.Where_equals_method_string(async);

        AssertMql(
            """
            Customers.{ "$match" : { "City" : "London" } }
            """);
    }

    public override async Task Where_equals_method_string_with_ignore_case(bool async)
    {
        await base.Where_equals_method_string_with_ignore_case(async);

        AssertMql(
            """
            Customers.{ "$match" : { "$expr" : { "$eq" : [{ "$strcasecmp" : ["$City", "London"] }, 0] } } }
            """);
    }

    public override async Task Where_string_length(bool async)
    {
        await base.Where_string_length(async);

        AssertMql(
            """
            Customers.{ "$match" : { "City" : { "$regularExpression" : { "pattern" : "^.{6}$", "options" : "s" } } } }
            """);
    }

    public override async Task Where_string_indexof(bool async)
    {
        // Fails: AV000
        Assert.Contains(
            "Values differ", // (Expected 1, got 90)
            (await Assert.ThrowsAsync<EqualException>(async () =>
                await base.Where_string_indexof(async))).Message);

        AssertMql(
            """
            Customers.{ "$match" : { "City" : { "$not" : { "$regularExpression" : { "pattern" : "^Sea", "options" : "s" } } } } }
            """);
    }

    public override async Task Where_string_replace(bool async)
    {
        // Fails: AV000
        Assert.Contains(
            "Expression not supported: c.City.Replace(\"Sea\", \"Rea\").",
            (await Assert.ThrowsAsync<ExpressionNotSupportedException>(async () =>
                await base.Where_string_replace(async))).Message);

        AssertMql(
            """
            Customers.
            """);
    }

    public override async Task Where_string_substring(bool async)
    {
        await base.Where_string_substring(async);

        AssertMql(
            """
            Customers.{ "$match" : { "$expr" : { "$eq" : [{ "$substrCP" : ["$City", 1, 2] }, "ea"] } } }
            """);
    }

    public override async Task Where_datetime_now(bool async)
    {
        await base.Where_datetime_now(async);

        AssertMql(
            """
            Customers.
            """);
    }

    public override async Task Where_datetime_utcnow(bool async)
    {
        await base.Where_datetime_utcnow(async);

        AssertMql(
            """
            Customers.
            """);
    }

    public override async Task Where_datetimeoffset_utcnow(bool async)
    {
        await base.Where_datetimeoffset_utcnow(async);

        AssertMql(
            """
            Customers.
            """);
    }

    public override async Task Where_datetime_today(bool async)
    {
        await base.Where_datetime_today(async);

        AssertMql(
            """
            Employees.
            """);
    }

    public override async Task Where_datetime_date_component(bool async)
    {
        // Fails: AV000
        Assert.Contains(
            "Values differ", // (Expected 3, got 0)
            (await Assert.ThrowsAsync<EqualException>(async () =>
                await base.Where_datetime_date_component(async))).Message);

        AssertMql(
            """
            Products.{ "$match" : { "$expr" : { "$eq" : [{ "$dateTrunc" : { "date" : "$OrderDate", "unit" : "day" } }, { "$date" : "1998-05-03T23:00:00Z" }] } } }
            """);
    }

    public override async Task Where_date_add_year_constant_component(bool async)
    {
        // Fails: AV000
        Assert.Contains(
            "Values differ", // (Expected 270, got 0)
            (await Assert.ThrowsAsync<EqualException>(async () =>
                await base.Where_date_add_year_constant_component(async))).Message);

        AssertMql(
            """
            Products.{ "$match" : { "$expr" : { "$eq" : [{ "$year" : { "$dateAdd" : { "startDate" : "$OrderDate", "unit" : "year", "amount" : -1 } } }, 1997] } } }
            """);
    }

    public override async Task Where_datetime_year_component(bool async)
    {
        // Fails: AV000
        Assert.Contains(
            "Values differ", // (Expected 270, got 0)
            (await Assert.ThrowsAsync<EqualException>(async () =>
                await base.Where_datetime_year_component(async))).Message);

        AssertMql(
            """
            Products.{ "$match" : { "$expr" : { "$eq" : [{ "$year" : "$OrderDate" }, 1998] } } }
            """);
    }

    public override async Task Where_datetime_month_component(bool async)
    {
        // Fails: AV000
        Assert.Contains(
            "Values differ",
            (await Assert.ThrowsAsync<EqualException>(async () =>
                await base.Where_datetime_month_component(async))).Message);

        AssertMql(
            """
            Products.{ "$match" : { "$expr" : { "$eq" : [{ "$month" : "$OrderDate" }, 4] } } }
            """);
    }

    public override async Task Where_datetime_dayOfYear_component(bool async)
    {
        // Fails: AV000
        Assert.Contains(
            "Values differ",
            (await Assert.ThrowsAsync<EqualException>(async () =>
                await base.Where_datetime_dayOfYear_component(async))).Message);

        AssertMql(
            """
            Products.{ "$match" : { "$expr" : { "$eq" : [{ "$dayOfYear" : "$OrderDate" }, 68] } } }
            """);
    }

    public override async Task Where_datetime_day_component(bool async)
    {
        // Fails: AV000
        Assert.Contains(
            "Values differ",
            (await Assert.ThrowsAsync<EqualException>(async () =>
                await base.Where_datetime_day_component(async))).Message);

        AssertMql(
            """
            Products.{ "$match" : { "$expr" : { "$eq" : [{ "$dayOfMonth" : "$OrderDate" }, 4] } } }
            """);
    }

    public override async Task Where_datetime_hour_component(bool async)
    {
        // Fails: AV000
        Assert.Contains(
            "Values differ",
            (await Assert.ThrowsAsync<EqualException>(async () =>
                await base.Where_datetime_hour_component(async))).Message);

        AssertMql(
            """
            Products.{ "$match" : { "$expr" : { "$eq" : [{ "$hour" : "$OrderDate" }, 0] } } }
            """);
    }

    public override async Task Where_datetime_minute_component(bool async)
    {
        // Fails: AV000
        Assert.Contains(
            "Values differ",
            (await Assert.ThrowsAsync<EqualException>(async () =>
                await base.Where_datetime_minute_component(async))).Message);

        AssertMql(
            """
            Products.{ "$match" : { "$expr" : { "$eq" : [{ "$minute" : "$OrderDate" }, 0] } } }
            """);
    }

    public override async Task Where_datetime_second_component(bool async)
    {
        // Fails: AV000
        Assert.Contains(
            "Values differ",
            (await Assert.ThrowsAsync<EqualException>(async () =>
                await base.Where_datetime_second_component(async))).Message);

        AssertMql(
            """
            Products.{ "$match" : { "$expr" : { "$eq" : [{ "$second" : "$OrderDate" }, 0] } } }
            """);
    }

    public override async Task Where_datetime_millisecond_component(bool async)
    {
        // Fails: AV000
        Assert.Contains(
            "Values differ",
            (await Assert.ThrowsAsync<EqualException>(async () =>
                await base.Where_datetime_millisecond_component(async))).Message);

        AssertMql(
            """
            Products.{ "$match" : { "$expr" : { "$eq" : [{ "$millisecond" : "$OrderDate" }, 0] } } }
            """);
    }

    public override async Task Where_datetimeoffset_now_component(bool async)
    {
        // Fails: AV000
        Assert.Contains(
            "Expression not supported: Convert(",
            (await Assert.ThrowsAsync<ExpressionNotSupportedException>(async () =>
                await base.Where_datetimeoffset_now_component(async))).Message);

        AssertMql(
            """
            Products.
            """);
    }

    public override async Task Where_datetimeoffset_utcnow_component(bool async)
    {
        // Fails: AV000
        Assert.Contains(
            "Expression not supported: Convert(",
            (await Assert.ThrowsAsync<ExpressionNotSupportedException>(async () =>
                await base.Where_datetimeoffset_utcnow_component(async))).Message);

        AssertMql(
            """
            Products.
            """);
    }

    public override async Task Where_concat_string_int_comparison1(bool async)
    {
        await base.Where_concat_string_int_comparison1(async);

        AssertMql(
            """
            Customers.{ "$match" : { "$expr" : { "$eq" : [{ "$concat" : ["$_id", "10"] }, "$CompanyName"] } } }, { "$project" : { "_v" : "$_id", "_id" : 0 } }
            """);
    }

    public override async Task Where_concat_string_int_comparison2(bool async)
    {
        await base.Where_concat_string_int_comparison2(async);

        AssertMql(
            """
            Customers.{ "$match" : { "$expr" : { "$eq" : [{ "$concat" : ["10", "$_id"] }, "$CompanyName"] } } }, { "$project" : { "_v" : "$_id", "_id" : 0 } }
            """);
    }

    public override async Task Where_concat_string_int_comparison3(bool async)
    {
        await base.Where_concat_string_int_comparison3(async);

        AssertMql(
            """
            Customers.{ "$match" : { "$expr" : { "$eq" : [{ "$concat" : ["30", "$_id", "21", "42"] }, "$CompanyName"] } } }, { "$project" : { "_v" : "$_id", "_id" : 0 } }
            """);
    }

    public override async Task Where_concat_string_int_comparison4(bool async)
    {
        await base.Where_concat_string_int_comparison4(async);

        AssertMql(
            """
            Products.{ "$match" : { "$expr" : { "$eq" : [{ "$concat" : [{ "$toString" : "$_id" }, "$CustomerID"] }, "$CustomerID"] } } }, { "$project" : { "_v" : "$CustomerID", "_id" : 0 } }
            """);
    }

    public override async Task Where_concat_string_string_comparison(bool async)
    {
        await base.Where_concat_string_string_comparison(async);

        AssertMql(
            """
            Customers.{ "$match" : { "$expr" : { "$eq" : [{ "$concat" : ["A", "$_id"] }, "AALFKI"] } } }, { "$project" : { "_v" : "$_id", "_id" : 0 } }
            """);
    }

    public override async Task Where_string_concat_method_comparison(bool async)
    {
        await base.Where_string_concat_method_comparison(async);

        AssertMql(
            """
            Customers.{ "$match" : { "$expr" : { "$eq" : [{ "$concat" : ["A", "$_id"] }, "AAROUT"] } } }, { "$project" : { "_v" : "$_id", "_id" : 0 } }
            """);
    }

    public override async Task Where_string_concat_method_comparison_2(bool async)
    {
        await base.Where_string_concat_method_comparison_2(async);

        AssertMql(
            """
            Customers.{ "$match" : { "$expr" : { "$eq" : [{ "$concat" : ["A", "B", "$_id"] }, "ABANATR"] } } }, { "$project" : { "_v" : "$_id", "_id" : 0 } }
            """);
    }

    public override async Task Where_string_concat_method_comparison_3(bool async)
    {
        await base.Where_string_concat_method_comparison_3(async);

        AssertMql(
            """
            Customers.{ "$match" : { "$expr" : { "$eq" : [{ "$concat" : ["A", "B", "C", "$_id"] }, "ABCANTON"] } } }, { "$project" : { "_v" : "$_id", "_id" : 0 } }
            """);
    }

    public override async Task Time_of_day_datetime(bool async)
    {
        // Fails: AV013
        Assert.Contains(
            "Cannot deserialize a 'TimeSpan' from BsonType 'Null'.",
            (await Assert.ThrowsAsync<FormatException>(async () =>
                await base.Time_of_day_datetime(async))).Message);

        AssertMql(
            """
            Products.{ "$project" : { "_v" : { "$dateDiff" : { "startDate" : { "$dateTrunc" : { "date" : "$OrderDate", "unit" : "day" } }, "endDate" : "$OrderDate", "unit" : "millisecond" } }, "_id" : 0 } }
            """);
    }

    public override async Task Like_with_non_string_column_using_ToString(bool async)
    {
        // Fails: AV014
        Assert.Contains(
            "value(Microsoft.EntityFrameworkCore.DbFunctions).Like(",
            (await Assert.ThrowsAsync<ExpressionNotSupportedException>(async () =>
                await base.Like_with_non_string_column_using_ToString(async))).Message);

        AssertMql(
            """
            Products.
            """);
    }

    public override async Task Like_with_non_string_column_using_double_cast(bool async)
    {
        // Fails: AV014 (Translation of Like)
        Assert.Contains(
            "value(Microsoft.EntityFrameworkCore.DbFunctions).Like(",
            (await Assert.ThrowsAsync<ExpressionNotSupportedException>(async () =>
                await base.Like_with_non_string_column_using_double_cast(async))).Message);

        AssertMql(
            """
            Products.
            """);
    }

    public override async Task Where_Like_and_comparison(bool async)
    {
        // Fails: AV014 (Translation of Like)
        Assert.Contains(
            "value(Microsoft.EntityFrameworkCore.DbFunctions).Like(",
            (await Assert.ThrowsAsync<ExpressionNotSupportedException>(async () =>
                await base.Where_Like_and_comparison(async))).Message);

        AssertMql(
            """
            Customers.
            """);
    }

    public override async Task Where_Like_or_comparison(bool async)
    {
        // Fails: AV014 (Translation of Like)
        Assert.Contains(
            "value(Microsoft.EntityFrameworkCore.DbFunctions).Like(",
            (await Assert.ThrowsAsync<ExpressionNotSupportedException>(async () =>
                await base.Where_Like_or_comparison(async))).Message);

        AssertMql(
            """
            Customers.
            """);
    }

    private void AssertMql(params string[] expected)
        => Fixture.TestMqlLoggerFactory.AssertBaseline(expected);

    protected override void ClearLog()
        => Fixture.TestMqlLoggerFactory.Clear();
}
