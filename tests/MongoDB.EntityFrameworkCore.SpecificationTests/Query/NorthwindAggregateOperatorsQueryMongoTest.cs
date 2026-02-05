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
using MongoDB.Bson;
using MongoDB.Driver.Linq;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace MongoDB.EntityFrameworkCore.SpecificationTests.Query;

public class NorthwindAggregateOperatorsQueryMongoTest
    : NorthwindAggregateOperatorsQueryTestBase<NorthwindQueryMongoFixture<NoopModelCustomizer>>
{
    public NorthwindAggregateOperatorsQueryMongoTest(
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

    public override async Task Last_when_no_order_by(bool async)
    {
        await base.Last_when_no_order_by(async);

        AssertMql(
            """
            Customers.{ "$match" : { "_id" : "ALFKI" } }, { "$group" : { "_id" : null, "_last" : { "$last" : "$$ROOT" } } }, { "$replaceRoot" : { "newRoot" : "$_last" } }
            """);
    }

    public override async Task LastOrDefault_when_no_order_by(bool async)
    {
        await base.LastOrDefault_when_no_order_by(async);

        AssertMql(
            """
            Customers.{ "$match" : { "_id" : "ALFKI" } }, { "$group" : { "_id" : null, "_last" : { "$last" : "$$ROOT" } } }, { "$replaceRoot" : { "newRoot" : "$_last" } }
            """);
    }

    public override async Task Contains_with_local_tuple_array_closure(bool async)
    {
        await base.Contains_with_local_tuple_array_closure(async);

        AssertMql(
            """
            OrderDetails.{ "$match" : { "$expr" : { "$in" : [["$_id.OrderID", "$_id.ProductID"], [[1, 2], [10248, 11]]] } } }
            """);
    }

    public override async Task Array_cast_to_IEnumerable_Contains_with_constant(bool async)
    {
        await base.Array_cast_to_IEnumerable_Contains_with_constant(async);

        AssertMql(
            """
            Customers.{ "$match" : { "_id" : { "$in" : ["ALFKI", "WRONG"] } } }
            """);
    }

    public override async Task Contains_over_keyless_entity_throws(bool async)
    {
        // Fails: Entity equality issue EF-202
        Assert.Contains(
            "Entity to entity comparison is not supported.",
            (await Assert.ThrowsAsync<NotSupportedException>(() => base.Contains_over_keyless_entity_throws(async))).Message);

        AssertMql(
            """
            Customers.{ "$limit" : 1 }
            """,
            //
            """
            Customers.
            """);
    }

    public override async Task Enumerable_min_is_mapped_to_Queryable_1(bool async)
    {
        // Fails: Projections issue EF-76
        await AssertTranslationFailed(() => base.Enumerable_min_is_mapped_to_Queryable_1(async));

        AssertMql(
        );
    }

    public override async Task Enumerable_min_is_mapped_to_Queryable_2(bool async)
    {
        // Fails: Projections issue EF-76
        await AssertTranslationFailed(() => base.Enumerable_min_is_mapped_to_Queryable_2(async));

        AssertMql(
        );
    }

    public override async Task Average_with_unmapped_property_access_throws_meaningful_exception(bool async)
    {
        // Fails: Does not use translation failed message
        Assert.Contains(
            "Serializer for Microsoft.EntityFramework",
            (await Assert.ThrowsAsync<ContainsException>(() =>
                base.Average_with_unmapped_property_access_throws_meaningful_exception(async))).Message);

        AssertMql(
            """
            Orders.
            """);
    }

    public override async Task Sum_over_empty_returns_zero(bool async)
    {
        await base.Sum_over_empty_returns_zero(async);

        AssertMql(
            """
            Orders.{ "$match" : { "_id" : 42 } }, { "$group" : { "_id" : null, "_v" : { "$sum" : "$_id" } } }, { "$project" : { "_id" : 0 } }
            """);
    }

    public override async Task Average_over_default_returns_default(bool async)
    {
        await base.Average_over_default_returns_default(async);

        AssertMql(
            """
            Orders.{ "$match" : { "_id" : 10248 } }, { "$group" : { "_id" : null, "_v" : { "$avg" : { "$subtract" : ["$_id", 10248] } } } }, { "$project" : { "_id" : 0 } }
            """);
    }

    public override async Task Max_over_default_returns_default(bool async)
    {
        await base.Max_over_default_returns_default(async);

        AssertMql(
            """
            Orders.{ "$match" : { "_id" : 10248 } }, { "$group" : { "_id" : null, "_max" : { "$max" : { "_v" : { "$subtract" : ["$_id", 10248] } } } } }, { "$replaceRoot" : { "newRoot" : "$_max" } }
            """);
    }

    public override async Task Min_over_default_returns_default(bool async)
    {
        await base.Min_over_default_returns_default(async);

        AssertMql(
            """
            Orders.{ "$match" : { "_id" : 10248 } }, { "$group" : { "_id" : null, "_min" : { "$min" : { "_v" : { "$subtract" : ["$_id", 10248] } } } } }, { "$replaceRoot" : { "newRoot" : "$_min" } }
            """);
    }

#if EF8 || EF9

    public override async Task Average_after_default_if_empty_does_not_throw(bool async)
    {
        await AssertTranslationFailed(() => base.Average_after_default_if_empty_does_not_throw(async));
    }

    public override async Task Max_after_default_if_empty_does_not_throw(bool async)
    {
        await AssertNoProjectionSupport(() => base.Max_after_default_if_empty_does_not_throw(async));
    }

    public override async Task Min_after_default_if_empty_does_not_throw(bool async)
    {
        await AssertNoProjectionSupport(() => base.Min_after_default_if_empty_does_not_throw(async));
    }

#endif

    public override async Task Sum_with_no_data_cast_to_nullable(bool async)
    {
        // Fails: Sum of empty set cast to nullable issue EF-232
        Assert.Contains(
            "Expected: 0",
            (await Assert.ThrowsAsync<EqualException>(() =>
                base.Sum_with_no_data_cast_to_nullable(async))).Message);

        AssertMql(
            """
            Orders.{ "$match" : { "_id" : { "$lt" : 0 } } }, { "$project" : { "_v" : "$_id", "_id" : 0 } }, { "$group" : { "_id" : null, "_v" : { "$sum" : "$_v" } } }, { "$project" : { "_id" : 0 } }
            """);
    }

    public override async Task Sum_with_no_data_nullable(bool async)
    {
        await base.Sum_with_no_data_nullable(async);

        AssertMql(
            """
            Products.{ "$group" : { "_id" : null, "_v" : { "$sum" : "$SupplierID" } } }, { "$project" : { "_id" : 0 } }
            """);
    }

    public override async Task Sum_with_no_arg_empty(bool async)
    {
        await base.Sum_with_no_arg_empty(async);

        AssertMql(
            """
            Orders.{ "$match" : { "_id" : 42 } }, { "$project" : { "_v" : "$_id", "_id" : 0 } }, { "$group" : { "_id" : null, "_v" : { "$sum" : "$_v" } } }, { "$project" : { "_id" : 0 } }
            """);
    }

    public override async Task Min_no_data(bool async)
    {
        await base.Min_no_data(async);

        AssertMql(
            """
            Orders.{ "$match" : { "_id" : -1 } }, { "$group" : { "_id" : null, "_min" : { "$min" : { "_v" : "$_id" } } } }, { "$replaceRoot" : { "newRoot" : "$_min" } }
            """);
    }

    public override async Task Min_no_data_nullable(bool async)
    {
        // Fails: Max over empty nullables issue EF-227
        Assert.Contains(
            "Sequence contains no elements",
            (await Assert.ThrowsAsync<InvalidOperationException>(() =>
                base.Min_no_data_nullable(async))).Message);

        AssertMql(
            """
            Products.{ "$match" : { "SupplierID" : -1 } }, { "$group" : { "_id" : null, "_min" : { "$min" : { "_v" : "$SupplierID" } } } }, { "$replaceRoot" : { "newRoot" : "$_min" } }
            """);
    }

    public override async Task Min_no_data_cast_to_nullable(bool async)
    {
        // Fails: Max over empty nullables issue EF-227
        Assert.Contains(
            "Sequence contains no elements",
            (await Assert.ThrowsAsync<InvalidOperationException>(() =>
                base.Min_no_data_cast_to_nullable(async))).Message);

        AssertMql(
            """
            Orders.{ "$match" : { "_id" : -1 } }, { "$group" : { "_id" : null, "_min" : { "$min" : { "_v" : "$_id" } } } }, { "$replaceRoot" : { "newRoot" : "$_min" } }
            """);
    }

    public override async Task Min_no_data_subquery(bool async)
    {
        // Fails: Projections issue EF-76
        await AssertTranslationFailed(() => base.Min_no_data_subquery(async));

        AssertMql(
        );
    }

    public override async Task Max_no_data(bool async)
    {
        await base.Max_no_data(async);

        AssertMql(
            """
            Orders.{ "$match" : { "_id" : -1 } }, { "$group" : { "_id" : null, "_max" : { "$max" : { "_v" : "$_id" } } } }, { "$replaceRoot" : { "newRoot" : "$_max" } }
            """);
    }

    public override async Task Max_no_data_nullable(bool async)
    {
        // Fails: Max over empty nullables issue EF-227
        Assert.Contains(
            "Sequence contains no elements",
            (await Assert.ThrowsAsync<InvalidOperationException>(() =>
                base.Max_no_data_nullable(async))).Message);

        AssertMql(
            """
            Products.{ "$match" : { "SupplierID" : -1 } }, { "$group" : { "_id" : null, "_max" : { "$max" : { "_v" : "$SupplierID" } } } }, { "$replaceRoot" : { "newRoot" : "$_max" } }
            """);
    }

    public override async Task Max_no_data_cast_to_nullable(bool async)
    {
        // Fails: Max over empty nullables issue EF-227
        Assert.Contains(
            "Sequence contains no elements",
            (await Assert.ThrowsAsync<InvalidOperationException>(() =>
                base.Max_no_data_cast_to_nullable(async))).Message);

        AssertMql(
            """
            Orders.{ "$match" : { "_id" : -1 } }, { "$group" : { "_id" : null, "_max" : { "$max" : { "_v" : "$_id" } } } }, { "$replaceRoot" : { "newRoot" : "$_max" } }
            """);
    }

    public override async Task Max_no_data_subquery(bool async)
    {
        // Fails: Projections issue EF-76
        await AssertTranslationFailed(() => base.Max_no_data_subquery(async));

        AssertMql(
        );
    }

    public override async Task Average_no_data(bool async)
    {
        await base.Average_no_data(async);

        AssertMql(
            """
            Orders.{ "$match" : { "_id" : -1 } }, { "$group" : { "_id" : null, "_v" : { "$avg" : "$_id" } } }, { "$project" : { "_id" : 0 } }
            """);
    }

    public override async Task Average_no_data_nullable(bool async)
    {
        await base.Average_no_data_nullable(async);

        AssertMql(
            """
            Products.{ "$match" : { "SupplierID" : -1 } }, { "$group" : { "_id" : null, "_v" : { "$avg" : "$SupplierID" } } }, { "$project" : { "_id" : 0 } }
            """);
    }

    public override async Task Average_no_data_cast_to_nullable(bool async)
    {
        await base.Average_no_data_cast_to_nullable(async);

        AssertMql(
            """
            Orders.{ "$match" : { "_id" : -1 } }, { "$group" : { "_id" : null, "_v" : { "$avg" : "$_id" } } }, { "$project" : { "_id" : 0 } }
            """);
    }

    public override async Task Average_no_data_subquery(bool async)
    {
        // Fails: Projections issue EF-76
        await AssertTranslationFailed(() => base.Average_no_data_subquery(async));

        AssertMql(
        );
    }

    public override async Task Count_with_no_predicate(bool async)
    {
        await base.Count_with_no_predicate(async);

        AssertMql(
            """
            Orders.{ "$count" : "_v" }
            """);
    }

    public override async Task Count_with_order_by(bool async)
    {
        await base.Count_with_order_by(async);

        AssertMql(
            """
            Orders.{ "$sort" : { "CustomerID" : 1 } }, { "$count" : "_v" }
            """);
    }

    public override async Task Where_OrderBy_Count_client_eval(bool async)
    {
        // Fails: Does not throw expected unable to translate exception
        Assert.Contains(
            "Actual:   typeof(MongoDB.Driver.Linq.ExpressionNotSupportedException)",
            (await Assert.ThrowsAsync<ThrowsException>(() =>
                base.Where_OrderBy_Count_client_eval(async))).Message);

        AssertMql(
            """
            Orders.
            """);
    }

    public override async Task OrderBy_Where_Count_client_eval(bool async)
    {
        // Fails: Does not throw expected unable to translate exception
        Assert.Contains(
            "Actual:   typeof(MongoDB.Driver.Linq.ExpressionNotSupportedException)",
            (await Assert.ThrowsAsync<ThrowsException>(() =>
                base.OrderBy_Where_Count_client_eval(async))).Message);

        AssertMql(
            """
            Orders.
            """);
    }

    public override async Task OrderBy_Where_Count_client_eval_mixed(bool async)
    {
        // Fails: Does not throw expected unable to translate exception
        Assert.Contains(
            "Actual:   typeof(MongoDB.Driver.Linq.ExpressionNotSupportedException)",
            (await Assert.ThrowsAsync<ThrowsException>(() =>
                base.OrderBy_Where_Count_client_eval_mixed(async))).Message);

        AssertMql(
            """
            Orders.
            """);
    }

    public override async Task OrderBy_Count_with_predicate_client_eval(bool async)
    {
        // Fails: Does not throw expected unable to translate exception
        Assert.Contains(
            "Actual:   typeof(MongoDB.Driver.Linq.ExpressionNotSupportedException)",
            (await Assert.ThrowsAsync<ThrowsException>(() =>
                base.OrderBy_Count_with_predicate_client_eval(async))).Message);

        AssertMql(
            """
            Orders.
            """);
    }

    public override async Task OrderBy_Count_with_predicate_client_eval_mixed(bool async)
    {
        // Fails: Does not throw expected unable to translate exception
        Assert.Contains(
            "Actual:   typeof(MongoDB.Driver.Linq.ExpressionNotSupportedException)",
            (await Assert.ThrowsAsync<ThrowsException>(() =>
                base.OrderBy_Count_with_predicate_client_eval_mixed(async))).Message);

        AssertMql(
            """
            Orders.
            """);
    }

    public override async Task OrderBy_Where_Count_with_predicate_client_eval(bool async)
    {
        // Fails: Does not throw expected unable to translate exception
        Assert.Contains(
            "Actual:   typeof(MongoDB.Driver.Linq.ExpressionNotSupportedException)",
            (await Assert.ThrowsAsync<ThrowsException>(() =>
                base.OrderBy_Where_Count_with_predicate_client_eval(async))).Message);

        AssertMql(
            """
            Orders.
            """);
    }

    public override async Task OrderBy_Where_Count_with_predicate_client_eval_mixed(bool async)
    {
        // Fails: Does not throw expected unable to translate exception
        Assert.Contains(
            "Actual:   typeof(MongoDB.Driver.Linq.ExpressionNotSupportedException)",
            (await Assert.ThrowsAsync<ThrowsException>(() =>
                base.OrderBy_Where_Count_with_predicate_client_eval_mixed(async))).Message);

        AssertMql(
            """
            Orders.
            """);
    }

    public override async Task OrderBy_client_Take(bool async)
    {
        await base.OrderBy_client_Take(async);

        AssertMql(
            """
            Employees.{ "$project" : { "_id" : 0, "_document" : "$$ROOT", "_key1" : 42 } }, { "$sort" : { "_key1" : 1 } }, { "$replaceRoot" : { "newRoot" : "$_document" } }, { "$limit" : 10 }
            """);
    }

    public override async Task Single_Throws(bool async)
    {
        await base.Single_Throws(async);

        AssertMql(
            """
            Customers.{ "$limit" : 2 }
            """);
    }

    public override async Task Where_Single(bool async)
    {
        await base.Where_Single(async);

        AssertMql(
            """
            Customers.{ "$match" : { "_id" : "ALFKI" } }, { "$limit" : 2 }
            """);
    }

    public override async Task SingleOrDefault_Throws(bool async)
    {
        await base.SingleOrDefault_Throws(async);

        AssertMql(
            """
            Customers.{ "$limit" : 2 }
            """);
    }

    public override async Task SingleOrDefault_Predicate(bool async)
    {
        await base.SingleOrDefault_Predicate(async);

        AssertMql(
            """
            Customers.{ "$match" : { "_id" : "ALFKI" } }, { "$limit" : 2 }
            """);
    }

    public override async Task Where_SingleOrDefault(bool async)
    {
        await base.Where_SingleOrDefault(async);

        AssertMql(
            """
            Customers.{ "$match" : { "_id" : "ALFKI" } }, { "$limit" : 2 }
            """);
    }

    public override async Task First(bool async)
    {
        await base.First(async);

        AssertMql(
            """
            Customers.{ "$sort" : { "ContactName" : 1 } }, { "$limit" : 1 }
            """);
    }

    public override async Task First_Predicate(bool async)
    {
        await base.First_Predicate(async);

        AssertMql(
            """
            Customers.{ "$sort" : { "ContactName" : 1 } }, { "$match" : { "City" : "London" } }, { "$limit" : 1 }
            """);
    }

    public override async Task Where_First(bool async)
    {
        await base.Where_First(async);

        AssertMql(
            """
            Customers.{ "$sort" : { "ContactName" : 1 } }, { "$match" : { "City" : "London" } }, { "$limit" : 1 }
            """);
    }

    public override async Task FirstOrDefault(bool async)
    {
        await base.FirstOrDefault(async);

        AssertMql(
            """
            Customers.{ "$sort" : { "ContactName" : 1 } }, { "$limit" : 1 }
            """);
    }

    public override async Task FirstOrDefault_Predicate(bool async)
    {
        await base.FirstOrDefault_Predicate(async);

        AssertMql(
            """
            Customers.{ "$sort" : { "ContactName" : 1 } }, { "$match" : { "City" : "London" } }, { "$limit" : 1 }
            """);
    }

    public override async Task Where_FirstOrDefault(bool async)
    {
        await base.Where_FirstOrDefault(async);

        AssertMql(
            """
            Customers.{ "$sort" : { "ContactName" : 1 } }, { "$match" : { "City" : "London" } }, { "$limit" : 1 }
            """);
    }

    public override async Task Select_All(bool async)
    {
        await base.Select_All(async);

        AssertMql(
            """
            Orders.{ "$match" : { "CustomerID" : { "$ne" : "ALFKI" } } }, { "$limit" : 1 }, { "$project" : { "_id" : 0, "_v" : null } }
            """);
    }

    public override async Task Sum_with_no_arg(bool async)
    {
        await base.Sum_with_no_arg(async);

        AssertMql(
            """
            Orders.{ "$project" : { "_v" : "$_id", "_id" : 0 } }, { "$group" : { "_id" : null, "_v" : { "$sum" : "$_v" } } }, { "$project" : { "_id" : 0 } }
            """);
    }

    public override async Task Sum_with_binary_expression(bool async)
    {
        await base.Sum_with_binary_expression(async);

        AssertMql(
            """
            Orders.{ "$project" : { "_v" : { "$multiply" : ["$_id", 2] }, "_id" : 0 } }, { "$group" : { "_id" : null, "_v" : { "$sum" : "$_v" } } }, { "$project" : { "_id" : 0 } }
            """);
    }

    public override async Task Sum_with_arg(bool async)
    {
        await base.Sum_with_arg(async);

        AssertMql(
            """
            Orders.{ "$group" : { "_id" : null, "_v" : { "$sum" : "$_id" } } }, { "$project" : { "_id" : 0 } }
            """);
    }

    public override async Task Sum_with_arg_expression(bool async)
    {
        await base.Sum_with_arg_expression(async);

        AssertMql(
            """
            Orders.{ "$group" : { "_id" : null, "_v" : { "$sum" : { "$add" : ["$_id", "$_id"] } } } }, { "$project" : { "_id" : 0 } }
            """);
    }

    public override async Task Sum_with_division_on_decimal(bool async)
    {
        await base.Sum_with_division_on_decimal(async);

        AssertMql(
            """
            OrderDetails.{ "$group" : { "_id" : null, "_v" : { "$sum" : { "$divide" : ["$Quantity", { "$numberDecimal" : "2.09" }] } } } }, { "$project" : { "_id" : 0 } }
            """);
    }

    public override async Task Sum_with_division_on_decimal_no_significant_digits(bool async)
    {
        await base.Sum_with_division_on_decimal_no_significant_digits(async);

        AssertMql(
            """
            OrderDetails.{ "$group" : { "_id" : null, "_v" : { "$sum" : { "$divide" : ["$Quantity", { "$numberDecimal" : "2" }] } } } }, { "$project" : { "_id" : 0 } }
            """);
    }

    public override async Task Sum_with_coalesce(bool async)
    {
        await base.Sum_with_coalesce(async);

        AssertMql(
            """
            Products.{ "$match" : { "_id" : { "$lt" : 40 } } }, { "$group" : { "_id" : null, "_v" : { "$sum" : { "$ifNull" : ["$UnitPrice", { "$numberDecimal" : "0" }] } } } }, { "$project" : { "_id" : 0 } }
            """);
    }

#if !EF8

    public override async Task Sum_over_subquery(bool async)
    {
        await AssertNoMultiCollectionQuerySupport(() => base.Sum_over_subquery(async));
    }

    public override async Task Sum_over_nested_subquery(bool async)
    {
        await AssertNoMultiCollectionQuerySupport(() => base.Sum_over_nested_subquery(async));
    }

    public override async Task Sum_over_min_subquery(bool async)
    {
        await AssertNoMultiCollectionQuerySupport(() => base.Sum_over_min_subquery(async));
    }

    public override async Task Sum_over_scalar_returning_subquery(bool async)
    {
        await AssertNoMultiCollectionQuerySupport(() => base.Sum_over_scalar_returning_subquery(async));
    }

    public override async Task Sum_over_Any_subquery(bool async)
    {
        await AssertNoMultiCollectionQuerySupport(() => base.Sum_over_Any_subquery(async));
    }

    public override async Task Sum_over_uncorrelated_subquery(bool async)
    {
        await AssertNoMultiCollectionQuerySupport(() => base.Sum_over_uncorrelated_subquery(async));
    }

#else
    public override async Task Sum_over_subquery_is_client_eval(bool async)
    {
        await AssertNoMultiCollectionQuerySupport(() => base.Sum_over_subquery_is_client_eval(async));
    }

    public override async Task Sum_over_nested_subquery_is_client_eval(bool async)
    {
        await AssertNoMultiCollectionQuerySupport(() => base.Sum_over_nested_subquery_is_client_eval(async));
    }

    public override async Task Sum_over_min_subquery_is_client_eval(bool async)
    {
        await AssertNoMultiCollectionQuerySupport(() => base.Sum_over_min_subquery_is_client_eval(async));
    }

#endif

    public override async Task Sum_on_float_column(bool async)
    {
        // Fails: Truncation data loss issue EF-228
        Assert.Contains(
            "Truncation resulted in data loss.",
            (await Assert.ThrowsAsync<TruncationException>(() => base.Sum_on_float_column(async))).Message);

        AssertMql(
            """
            OrderDetails.{ "$match" : { "_id.ProductID" : 1 } }, { "$group" : { "_id" : null, "_v" : { "$sum" : "$Discount" } } }, { "$project" : { "_id" : 0 } }
            """);
    }

    public override async Task Sum_on_float_column_in_subquery(bool async)
    {
        // Fails: Projections issue EF-76
        await AssertTranslationFailed(() => base.Sum_on_float_column_in_subquery(async));

        AssertMql(
        );
    }

    public override async Task Average_with_no_arg(bool async)
    {
        await base.Average_with_no_arg(async);

        AssertMql(
            """
            Orders.{ "$project" : { "_v" : "$_id", "_id" : 0 } }, { "$group" : { "_id" : null, "_v" : { "$avg" : "$_v" } } }, { "$project" : { "_id" : 0 } }
            """);
    }

    public override async Task Average_with_binary_expression(bool async)
    {
        await base.Average_with_binary_expression(async);

        AssertMql(
            """
            Orders.{ "$project" : { "_v" : { "$multiply" : ["$_id", 2] }, "_id" : 0 } }, { "$group" : { "_id" : null, "_v" : { "$avg" : "$_v" } } }, { "$project" : { "_id" : 0 } }
            """);
    }

    public override async Task Average_with_arg(bool async)
    {
        await base.Average_with_arg(async);

        AssertMql(
            """
            Orders.{ "$group" : { "_id" : null, "_v" : { "$avg" : "$_id" } } }, { "$project" : { "_id" : 0 } }
            """);
    }

    public override async Task Average_with_arg_expression(bool async)
    {
        await base.Average_with_arg_expression(async);

        AssertMql(
            """
            Orders.{ "$group" : { "_id" : null, "_v" : { "$avg" : { "$add" : ["$_id", "$_id"] } } } }, { "$project" : { "_id" : 0 } }
            """);
    }

    public override async Task Average_with_division_on_decimal(bool async)
    {
        await base.Average_with_division_on_decimal(async);

        AssertMql(
            """
            OrderDetails.{ "$group" : { "_id" : null, "_v" : { "$avg" : { "$divide" : ["$Quantity", { "$numberDecimal" : "2.09" }] } } } }, { "$project" : { "_id" : 0 } }
            """);
    }

    public override async Task Average_with_division_on_decimal_no_significant_digits(bool async)
    {
        await base.Average_with_division_on_decimal_no_significant_digits(async);

        AssertMql(
            """
            OrderDetails.{ "$group" : { "_id" : null, "_v" : { "$avg" : { "$divide" : ["$Quantity", { "$numberDecimal" : "2" }] } } } }, { "$project" : { "_id" : 0 } }
            """);
    }

    public override async Task Average_with_coalesce(bool async)
    {
        await base.Average_with_coalesce(async);

        AssertMql(
            """
            Products.{ "$match" : { "_id" : { "$lt" : 40 } } }, { "$group" : { "_id" : null, "_v" : { "$avg" : { "$ifNull" : ["$UnitPrice", { "$numberDecimal" : "0" }] } } } }, { "$project" : { "_id" : 0 } }
            """);
    }

#if !EF8

    public override async Task Average_over_subquery(bool async)
    {
        await AssertNoMultiCollectionQuerySupport(() => base.Average_over_subquery(async));
    }

    public override async Task Average_over_nested_subquery(bool async)
    {
        await AssertNoMultiCollectionQuerySupport(() => base.Average_over_nested_subquery(async));
    }

    public override async Task Average_over_max_subquery(bool async)
    {
        await AssertNoMultiCollectionQuerySupport(() => base.Average_over_max_subquery(async));
    }

#else
    public override async Task Average_over_subquery_is_client_eval(bool async)
    {
        await AssertNoMultiCollectionQuerySupport(() => base.Average_over_subquery_is_client_eval(async));
    }

    public override async Task Average_over_nested_subquery_is_client_eval(bool async)
    {
        await AssertNoMultiCollectionQuerySupport(() => base.Average_over_nested_subquery_is_client_eval(async));
    }

    public override async Task Average_over_max_subquery_is_client_eval(bool async)
    {
        await AssertNoMultiCollectionQuerySupport(() => base.Average_over_max_subquery_is_client_eval(async));
    }

#endif

    public override async Task Average_on_float_column(bool async)
    {
        // Fails: Truncation data loss issue EF-228
        Assert.Contains(
            "Truncation resulted in data loss.",
            (await Assert.ThrowsAsync<TruncationException>(() => base.Average_on_float_column(async))).Message);

        AssertMql(
            """
            OrderDetails.{ "$match" : { "_id.ProductID" : 1 } }, { "$group" : { "_id" : null, "_v" : { "$avg" : "$Discount" } } }, { "$project" : { "_id" : 0 } }
            """);
    }

    public override async Task Average_on_float_column_in_subquery(bool async)
    {
        // Fails: Projections issue EF-76
        await AssertTranslationFailed(() => base.Average_on_float_column_in_subquery(async));

        AssertMql(
        );
    }

    public override async Task Average_on_float_column_in_subquery_with_cast(bool async)
    {
        // Fails: Projections issue EF-76
        await AssertTranslationFailed(() => base.Average_on_float_column_in_subquery_with_cast(async));

        AssertMql(
        );
    }

    public override async Task Min_with_no_arg(bool async)
    {
        await base.Min_with_no_arg(async);

        AssertMql(
            """
            Orders.{ "$project" : { "_v" : "$_id", "_id" : 0 } }, { "$group" : { "_id" : null, "_min" : { "$min" : "$$ROOT" } } }, { "$replaceRoot" : { "newRoot" : "$_min" } }
            """);
    }

    public override async Task Min_with_arg(bool async)
    {
        await base.Min_with_arg(async);

        AssertMql(
            """
            Orders.{ "$group" : { "_id" : null, "_min" : { "$min" : { "_v" : "$_id" } } } }, { "$replaceRoot" : { "newRoot" : "$_min" } }
            """);
    }

    public override async Task Min_with_coalesce(bool async)
    {
        await base.Min_with_coalesce(async);

        AssertMql(
            """
            Products.{ "$match" : { "_id" : { "$lt" : 40 } } }, { "$group" : { "_id" : null, "_min" : { "$min" : { "_v" : { "$ifNull" : ["$UnitPrice", { "$numberDecimal" : "0" }] } } } } }, { "$replaceRoot" : { "newRoot" : "$_min" } }
            """);
    }

#if !EF8

    public override async Task Min_over_subquery(bool async)
    {
        await AssertNoMultiCollectionQuerySupport(() => base.Min_over_subquery(async));
    }

    public override async Task Min_over_nested_subquery(bool async)
    {
        await AssertNoMultiCollectionQuerySupport(() => base.Min_over_nested_subquery(async));
    }

    public override async Task Min_over_max_subquery(bool async)
    {
        await AssertNoMultiCollectionQuerySupport(() => base.Min_over_max_subquery(async));
    }

#else
    public override async Task Min_over_subquery_is_client_eval(bool async)
    {
        await AssertNoMultiCollectionQuerySupport(() => base.Min_over_subquery_is_client_eval(async));
    }

    public override async Task Min_over_nested_subquery_is_client_eval(bool async)
    {
        await AssertNoMultiCollectionQuerySupport(() => base.Min_over_nested_subquery_is_client_eval(async));
    }

    public override async Task Min_over_max_subquery_is_client_eval(bool async)
    {
        await AssertNoMultiCollectionQuerySupport(() => base.Min_over_max_subquery_is_client_eval(async));
    }

#endif

    public override async Task Max_with_no_arg(bool async)
    {
        await base.Max_with_no_arg(async);

        AssertMql(
            """
            Orders.{ "$project" : { "_v" : "$_id", "_id" : 0 } }, { "$group" : { "_id" : null, "_max" : { "$max" : "$$ROOT" } } }, { "$replaceRoot" : { "newRoot" : "$_max" } }
            """);
    }

    public override async Task Max_with_arg(bool async)
    {
        await base.Max_with_arg(async);

        AssertMql(
            """
            Orders.{ "$group" : { "_id" : null, "_max" : { "$max" : { "_v" : "$_id" } } } }, { "$replaceRoot" : { "newRoot" : "$_max" } }
            """);
    }

    public override async Task Max_with_coalesce(bool async)
    {
        await base.Max_with_coalesce(async);

        AssertMql(
            """
            Products.{ "$match" : { "_id" : { "$lt" : 40 } } }, { "$group" : { "_id" : null, "_max" : { "$max" : { "_v" : { "$ifNull" : ["$UnitPrice", { "$numberDecimal" : "0" }] } } } } }, { "$replaceRoot" : { "newRoot" : "$_max" } }
            """);
    }

#if !EF8

    public override async Task Max_over_subquery(bool async)
    {
        await AssertNoMultiCollectionQuerySupport(() => base.Max_over_subquery(async));
    }

    public override async Task Max_over_nested_subquery(bool async)
    {
        await AssertNoMultiCollectionQuerySupport(() => base.Max_over_nested_subquery(async));
    }

    public override async Task Max_over_sum_subquery(bool async)
    {
        await AssertNoMultiCollectionQuerySupport(() => base.Max_over_sum_subquery(async));
    }

#else
    public override async Task Max_over_subquery_is_client_eval(bool async)
    {
        await AssertNoMultiCollectionQuerySupport(() => base.Max_over_subquery_is_client_eval(async));
    }

    public override async Task Max_over_nested_subquery_is_client_eval(bool async)
    {
        await AssertNoMultiCollectionQuerySupport(() => base.Max_over_nested_subquery_is_client_eval(async));
    }

    public override async Task Max_over_sum_subquery_is_client_eval(bool async)
    {
        await AssertNoMultiCollectionQuerySupport(() => base.Max_over_sum_subquery_is_client_eval(async));
    }

#endif

    public override async Task Count_with_predicate(bool async)
    {
        await base.Count_with_predicate(async);

        AssertMql(
            """
            Orders.{ "$match" : { "CustomerID" : "ALFKI" } }, { "$count" : "_v" }
            """);
    }

    public override async Task Where_OrderBy_Count(bool async)
    {
        await base.Where_OrderBy_Count(async);

        AssertMql(
            """
            Orders.{ "$match" : { "CustomerID" : "ALFKI" } }, { "$sort" : { "_id" : 1 } }, { "$count" : "_v" }
            """);
    }

    public override async Task OrderBy_Where_Count(bool async)
    {
        await base.OrderBy_Where_Count(async);

        AssertMql(
            """
            Orders.{ "$sort" : { "_id" : 1 } }, { "$match" : { "CustomerID" : "ALFKI" } }, { "$count" : "_v" }
            """);
    }

    public override async Task OrderBy_Count_with_predicate(bool async)
    {
        await base.OrderBy_Count_with_predicate(async);

        AssertMql(
            """
            Orders.{ "$sort" : { "_id" : 1 } }, { "$match" : { "CustomerID" : "ALFKI" } }, { "$count" : "_v" }
            """);
    }

    public override async Task OrderBy_Where_Count_with_predicate(bool async)
    {
        await base.OrderBy_Where_Count_with_predicate(async);

        AssertMql(
            """
            Orders.{ "$sort" : { "_id" : 1 } }, { "$match" : { "_id" : { "$gt" : 10 } } }, { "$match" : { "CustomerID" : { "$ne" : "ALFKI" } } }, { "$count" : "_v" }
            """);
    }

    public override async Task Distinct(bool async)
    {
        await base.Distinct(async);

        AssertMql(
            """
            Customers.{ "$group" : { "_id" : "$$ROOT" } }, { "$replaceRoot" : { "newRoot" : "$_id" } }
            """);
    }

    public override async Task Distinct_Scalar(bool async)
    {
        await base.Distinct_Scalar(async);

        AssertMql(
            """
            Customers.{ "$project" : { "_v" : "$City", "_id" : 0 } }, { "$group" : { "_id" : "$$ROOT" } }, { "$replaceRoot" : { "newRoot" : "$_id" } }
            """);
    }

    public override async Task OrderBy_Distinct(bool async)
    {
        await base.OrderBy_Distinct(async);

        // Ordering not preserved by distinct when ordering columns not projected.
        AssertMql(
            """
            Customers.{ "$sort" : { "_id" : 1 } }, { "$project" : { "_v" : "$City", "_id" : 0 } }, { "$group" : { "_id" : "$$ROOT" } }, { "$replaceRoot" : { "newRoot" : "$_id" } }
            """);
    }

    public override async Task Distinct_OrderBy(bool async)
    {
        await base.Distinct_OrderBy(async);

        AssertMql(
            """
            Customers.{ "$project" : { "_v" : "$Country", "_id" : 0 } }, { "$group" : { "_id" : "$$ROOT" } }, { "$replaceRoot" : { "newRoot" : "$_id" } }, { "$sort" : { "_v" : 1 } }
            """);
    }

    public override async Task Distinct_OrderBy2(bool async)
    {
        await base.Distinct_OrderBy2(async);

        AssertMql(
            """
            Customers.{ "$group" : { "_id" : "$$ROOT" } }, { "$replaceRoot" : { "newRoot" : "$_id" } }, { "$sort" : { "_id" : 1 } }
            """);
    }

    public override async Task Distinct_OrderBy3(bool async)
    {
        await base.Distinct_OrderBy3(async);

        AssertMql(
            """
            Customers.{ "$project" : { "CustomerID" : "$_id", "_id" : 0 } }, { "$group" : { "_id" : "$$ROOT" } }, { "$replaceRoot" : { "newRoot" : "$_id" } }, { "$sort" : { "CustomerID" : 1 } }
            """);
    }

    public override async Task Distinct_Count(bool async)
    {
        await base.Distinct_Count(async);

        AssertMql(
            """
            Customers.{ "$group" : { "_id" : "$$ROOT" } }, { "$replaceRoot" : { "newRoot" : "$_id" } }, { "$count" : "_v" }
            """);
    }

    public override async Task Select_Select_Distinct_Count(bool async)
    {
        await base.Select_Select_Distinct_Count(async);

        AssertMql(
            """
            Customers.{ "$project" : { "_v" : "$City", "_id" : 0 } }, { "$group" : { "_id" : "$$ROOT" } }, { "$replaceRoot" : { "newRoot" : "$_id" } }, { "$count" : "_v" }
            """);
    }

    public override async Task Single_Predicate(bool async)
    {
        await base.Single_Predicate(async);

        AssertMql(
            """
            Customers.{ "$match" : { "_id" : "ALFKI" } }, { "$limit" : 2 }
            """);
    }

    public override async Task FirstOrDefault_inside_subquery_gets_server_evaluated(bool async)
    {
        await AssertNoMultiCollectionQuerySupport(() => base.FirstOrDefault_inside_subquery_gets_server_evaluated(async));
    }

    public override async Task Multiple_collection_navigation_with_FirstOrDefault_chained(bool async)
    {
        await AssertNoProjectionSupport(() => base.Multiple_collection_navigation_with_FirstOrDefault_chained(async));
    }

    public override async Task Multiple_collection_navigation_with_FirstOrDefault_chained_projecting_scalar(bool async)
    {
        await AssertNoProjectionSupport(() =>
            base.Multiple_collection_navigation_with_FirstOrDefault_chained_projecting_scalar(async));
    }

    public override async Task First_inside_subquery_gets_client_evaluated(bool async)
    {
        await AssertNoMultiCollectionQuerySupport(() => base.First_inside_subquery_gets_client_evaluated(async));
    }

    public override async Task Last(bool async)
    {
        await base.Last(async);

        AssertMql(
            """
            Customers.{ "$sort" : { "ContactName" : 1 } }, { "$group" : { "_id" : null, "_last" : { "$last" : "$$ROOT" } } }, { "$replaceRoot" : { "newRoot" : "$_last" } }
            """);
    }

    public override async Task Last_Predicate(bool async)
    {
        await base.Last_Predicate(async);

        AssertMql(
            """
            Customers.{ "$sort" : { "ContactName" : 1 } }, { "$match" : { "City" : "London" } }, { "$group" : { "_id" : null, "_last" : { "$last" : "$$ROOT" } } }, { "$replaceRoot" : { "newRoot" : "$_last" } }
            """);
    }

    public override async Task Where_Last(bool async)
    {
        await base.Where_Last(async);

        AssertMql(
            """
            Customers.{ "$sort" : { "ContactName" : 1 } }, { "$match" : { "City" : "London" } }, { "$group" : { "_id" : null, "_last" : { "$last" : "$$ROOT" } } }, { "$replaceRoot" : { "newRoot" : "$_last" } }
            """);
    }

    public override async Task LastOrDefault(bool async)
    {
        await base.LastOrDefault(async);

        AssertMql(
            """
            Customers.{ "$sort" : { "ContactName" : 1 } }, { "$group" : { "_id" : null, "_last" : { "$last" : "$$ROOT" } } }, { "$replaceRoot" : { "newRoot" : "$_last" } }
            """);
    }

    public override async Task LastOrDefault_Predicate(bool async)
    {
        await base.LastOrDefault_Predicate(async);

        AssertMql(
            """
            Customers.{ "$sort" : { "ContactName" : 1 } }, { "$match" : { "City" : "London" } }, { "$group" : { "_id" : null, "_last" : { "$last" : "$$ROOT" } } }, { "$replaceRoot" : { "newRoot" : "$_last" } }
            """);
    }

    public override async Task Where_LastOrDefault(bool async)
    {
        await base.Where_LastOrDefault(async);

        AssertMql(
            """
            Customers.{ "$sort" : { "ContactName" : 1 } }, { "$match" : { "City" : "London" } }, { "$group" : { "_id" : null, "_last" : { "$last" : "$$ROOT" } } }, { "$replaceRoot" : { "newRoot" : "$_last" } }
            """);
    }

    public override async Task Contains_with_subquery(bool async)
    {
        await AssertNoProjectionSupport(() => base.Contains_with_subquery(async));
    }

    public override async Task Contains_with_local_array_closure(bool async)
    {
        await base.Contains_with_local_array_closure(async);

        AssertMql(
            """
            Customers.{ "$match" : { "_id" : { "$in" : ["ABCDE", "ALFKI"] } } }
            """,
            //
            """
            Customers.{ "$match" : { "_id" : { "$in" : ["ABCDE"] } } }
            """);
    }

    public override async Task Contains_with_subquery_and_local_array_closure(bool async)
    {
        // Fails: Projections issue EF-76
        Assert.Contains(
            "Expression not supported",
            (await Assert.ThrowsAsync<ExpressionNotSupportedException>(() =>
                base.Contains_with_subquery_and_local_array_closure(async))).Message);

        AssertMql(
            """
            Customers.
            """);
    }

    public override async Task Contains_with_local_uint_array_closure(bool async)
    {
        await base.Contains_with_local_uint_array_closure(async);

        AssertMql(
            """
            Employees.{ "$match" : { "_id" : { "$in" : [0, 1] } } }
            """,
            //
            """
            Employees.{ "$match" : { "_id" : { "$in" : [0] } } }
            """);
    }

    public override async Task Contains_with_local_nullable_uint_array_closure(bool async)
    {
        await base.Contains_with_local_nullable_uint_array_closure(async);

        AssertMql(
            """
            Employees.{ "$match" : { "_id" : { "$in" : [0, 1] } } }
            """,
            //
            """
            Employees.{ "$match" : { "_id" : { "$in" : [0] } } }
            """);
    }

    public override async Task Contains_with_local_array_inline(bool async)
    {
        await base.Contains_with_local_array_inline(async);

        AssertMql(
            """
            Customers.{ "$match" : { "_id" : { "$in" : ["ABCDE", "ALFKI"] } } }
            """);
    }

    public override async Task Contains_with_local_list_closure(bool async)
    {
        await base.Contains_with_local_list_closure(async);

        AssertMql(
            """
            Customers.{ "$match" : { "_id" : { "$in" : ["ABCDE", "ALFKI"] } } }
            """);
    }

    public override async Task Contains_with_local_object_list_closure(bool async)
    {
        await base.Contains_with_local_object_list_closure(async);

        AssertMql(
            """
            Customers.{ "$match" : { "_id" : { "$in" : ["ABCDE", "ALFKI"] } } }
            """);
    }

    public override async Task Contains_with_local_list_closure_all_null(bool async)
    {
        await base.Contains_with_local_list_closure_all_null(async);
        AssertMql(
            """
            Customers.{ "$match" : { "_id" : { "$in" : [null, null] } } }
            """);
    }

    public override async Task Contains_with_local_list_inline(bool async)
    {
        await base.Contains_with_local_list_inline(async);

        AssertMql(
            """
            Customers.{ "$match" : { "_id" : { "$in" : ["ABCDE", "ALFKI"] } } }
            """);
    }

    public override async Task Contains_with_local_list_inline_closure_mix(bool async)
    {
        await base.Contains_with_local_list_inline_closure_mix(async);

        AssertMql(
            """
            Customers.{ "$match" : { "_id" : { "$in" : ["ABCDE", "ALFKI"] } } }
            """,
            //
            """
            Customers.{ "$match" : { "_id" : { "$in" : ["ABCDE", "ANATR"] } } }
            """);
    }

    public override async Task Contains_with_local_non_primitive_list_inline_closure_mix(bool async)
    {
        await base.Contains_with_local_non_primitive_list_inline_closure_mix(async);

        AssertMql(
            """
            Customers.{ "$match" : { "_id" : { "$in" : ["ABCDE", "ALFKI"] } } }
            """,
            //
            """
            Customers.{ "$match" : { "_id" : { "$in" : ["ABCDE", "ANATR"] } } }
            """);
    }

    public override async Task Contains_with_local_enumerable_closure(bool async)
    {
        await base.Contains_with_local_enumerable_closure(async);

        AssertMql(
            """
            Customers.{ "$match" : { "_id" : { "$in" : ["ABCDE", "ALFKI"] } } }
            """,
            //
            """
            Customers.{ "$match" : { "_id" : { "$in" : ["ABCDE"] } } }
            """);
    }

    public override async Task Contains_with_local_object_enumerable_closure(bool async)
    {
        await base.Contains_with_local_object_enumerable_closure(async);

        AssertMql(
            """
            Customers.{ "$match" : { "_id" : { "$in" : ["ABCDE", "ALFKI"] } } }
            """);
    }

    public override async Task Contains_with_local_enumerable_closure_all_null(bool async)
    {
        await base.Contains_with_local_enumerable_closure_all_null(async);

        AssertMql(
            """
            Customers.{ "$match" : { "_id" : { "$in" : [] } } }
            """);
    }

    public override async Task Contains_with_local_enumerable_inline(bool async)
    {
        await base.Contains_with_local_enumerable_inline(async);

        AssertMql(
            """
            Customers.{ "$match" : { "$expr" : { "$in" : ["$_id", { "$filter" : { "input" : ["ABCDE", "ALFKI"], "as" : "e", "cond" : { "$ne" : ["$$e", null] } } }] } } }
            """);
    }

    public override async Task Contains_with_local_enumerable_inline_closure_mix(bool async)
    {
        await base.Contains_with_local_enumerable_inline_closure_mix(async);

        AssertMql(
            """
            Customers.{ "$match" : { "$expr" : { "$in" : ["$_id", { "$filter" : { "input" : ["ABCDE", "ALFKI"], "as" : "e", "cond" : { "$ne" : ["$$e", null] } } }] } } }
            """,
            //
            """
            Customers.{ "$match" : { "$expr" : { "$in" : ["$_id", { "$filter" : { "input" : ["ABCDE", "ANATR"], "as" : "e", "cond" : { "$ne" : ["$$e", null] } } }] } } }
            """);
    }

    public override async Task Contains_with_local_ordered_enumerable_closure(bool async)
    {
        await base.Contains_with_local_ordered_enumerable_closure(async);

        AssertMql(
            """
            Customers.{ "$match" : { "_id" : { "$in" : ["ABCDE", "ALFKI"] } } }
            """,
            //
            """
            Customers.{ "$match" : { "_id" : { "$in" : ["ABCDE"] } } }
            """);
    }

    public override async Task Contains_with_local_object_ordered_enumerable_closure(bool async)
    {
        await base.Contains_with_local_object_ordered_enumerable_closure(async);

        AssertMql(
            """
            Customers.{ "$match" : { "_id" : { "$in" : ["ABCDE", "ALFKI"] } } }
            """);
    }

    public override async Task Contains_with_local_ordered_enumerable_closure_all_null(bool async)
    {
        await base.Contains_with_local_ordered_enumerable_closure_all_null(async);

        AssertMql(
            """
            Customers.{ "$match" : { "_id" : { "$in" : [null, null] } } }
            """);
    }

    public override async Task Contains_with_local_ordered_enumerable_inline(bool async)
    {
        await base.Contains_with_local_ordered_enumerable_inline(async);

        AssertMql(
            """
            Customers.{ "$match" : { "_id" : { "$in" : ["ABCDE", "ALFKI"] } } }
            """);
    }

    public override async Task Contains_with_local_ordered_enumerable_inline_closure_mix(bool async)
    {
        await base.Contains_with_local_ordered_enumerable_inline_closure_mix(async);

        AssertMql(
            """
            Customers.{ "$match" : { "_id" : { "$in" : ["ABCDE", "ALFKI"] } } }
            """,
            //
            """
            Customers.{ "$match" : { "_id" : { "$in" : ["ABCDE", "ANATR"] } } }
            """);
    }

    public override async Task Contains_with_local_read_only_collection_closure(bool async)
    {
        await base.Contains_with_local_read_only_collection_closure(async);

        AssertMql(
            """
            Customers.{ "$match" : { "_id" : { "$in" : ["ABCDE", "ALFKI"] } } }
            """,
            //
            """
            Customers.{ "$match" : { "_id" : { "$in" : ["ABCDE"] } } }
            """);
    }

    public override async Task Contains_with_local_object_read_only_collection_closure(bool async)
    {
        await base.Contains_with_local_object_read_only_collection_closure(async);

        AssertMql(
            """
            Customers.{ "$match" : { "_id" : { "$in" : ["ABCDE", "ALFKI"] } } }
            """);
    }

    public override async Task Contains_with_local_ordered_read_only_collection_all_null(bool async)
    {
        await base.Contains_with_local_ordered_read_only_collection_all_null(async);

        AssertMql(
            """
            Customers.{ "$match" : { "_id" : { "$in" : [null, null] } } }
            """);
    }

    public override async Task Contains_with_local_read_only_collection_inline(bool async)
    {
        await base.Contains_with_local_read_only_collection_inline(async);

        AssertMql(
            """
            Customers.{ "$match" : { "_id" : { "$in" : ["ABCDE", "ALFKI"] } } }
            """);
    }

    public override async Task Contains_with_local_read_only_collection_inline_closure_mix(bool async)
    {
        await base.Contains_with_local_read_only_collection_inline_closure_mix(async);

        AssertMql(
            """
            Customers.{ "$match" : { "_id" : { "$in" : ["ABCDE", "ALFKI"] } } }
            """,
            //
            """
            Customers.{ "$match" : { "_id" : { "$in" : ["ABCDE", "ANATR"] } } }
            """);
    }

    public override async Task Contains_with_local_non_primitive_list_closure_mix(bool async)
    {
        await base.Contains_with_local_non_primitive_list_closure_mix(async);

        AssertMql(
            """
            Customers.{ "$match" : { "_id" : { "$in" : ["ABCDE", "ALFKI"] } } }
            """);
    }

    public override async Task Contains_with_local_collection_false(bool async)
    {
        await base.Contains_with_local_collection_false(async);

        AssertMql(
            """
            Customers.{ "$match" : { "_id" : { "$nin" : ["ABCDE", "ALFKI"] } } }
            """);
    }

    public override async Task Contains_with_local_collection_complex_predicate_and(bool async)
    {
        await base.Contains_with_local_collection_complex_predicate_and(async);

        AssertMql(
            """
            Customers.{ "$match" : { "$and" : [{ "$or" : [{ "_id" : "ALFKI" }, { "_id" : "ABCDE" }] }, { "_id" : { "$in" : ["ABCDE", "ALFKI"] } }] } }
            """);
    }

    public override async Task Contains_with_local_collection_complex_predicate_or(bool async)
        => await base.Contains_with_local_collection_complex_predicate_or(async);

    // issue #18791
    //            AssertMql(
    //                @"SELECT [c].[CustomerID], [c].[Address], [c].[City], [c].[CompanyName], [c].[ContactName], [c].[ContactTitle], [c].[Country], [c].[Fax], [c].[Phone], [c].[PostalCode], [c].[Region]
    //FROM [Customers] AS [c]
    //WHERE [c].[CustomerID] IN (N'ABCDE', N'ALFKI', N'ALFKI', N'ABCDE')");
    public override async Task Contains_with_local_collection_complex_predicate_not_matching_ins1(bool async)
        => await base.Contains_with_local_collection_complex_predicate_not_matching_ins1(async);

    // issue #18791
    //            AssertMql(
    //                @"SELECT [c].[CustomerID], [c].[Address], [c].[City], [c].[CompanyName], [c].[ContactName], [c].[ContactTitle], [c].[Country], [c].[Fax], [c].[Phone], [c].[PostalCode], [c].[Region]
    //FROM [Customers] AS [c]
    //WHERE [c].[CustomerID] IN (N'ALFKI', N'ABCDE') OR [c].[CustomerID] NOT IN (N'ABCDE', N'ALFKI')");
    public override async Task Contains_with_local_collection_complex_predicate_not_matching_ins2(bool async)
        => await base.Contains_with_local_collection_complex_predicate_not_matching_ins2(async);

    // issue #18791
    //            AssertMql(
    //                @"SELECT [c].[CustomerID], [c].[Address], [c].[City], [c].[CompanyName], [c].[ContactName], [c].[ContactTitle], [c].[Country], [c].[Fax], [c].[Phone], [c].[PostalCode], [c].[Region]
    //FROM [Customers] AS [c]
    //WHERE [c].[CustomerID] IN (N'ABCDE', N'ALFKI') AND [c].[CustomerID] NOT IN (N'ALFKI', N'ABCDE')");
    public override async Task Contains_with_local_collection_sql_injection(bool async)
    {
        await base.Contains_with_local_collection_sql_injection(async);

        AssertMql(
            """
            Customers.{ "$match" : { "$or" : [{ "_id" : { "$in" : ["ALFKI", "ABC')); GO; DROP TABLE Orders; GO; --"] } }, { "_id" : "ALFKI" }, { "_id" : "ABCDE" }] } }
            """);
    }

    public override async Task Contains_with_local_collection_empty_closure(bool async)
    {
        await base.Contains_with_local_collection_empty_closure(async);

        AssertMql(
            """
            Customers.{ "$match" : { "_id" : { "$in" : [] } } }
            """);
    }

    public override async Task Contains_with_local_collection_empty_inline(bool async)
    {
        await base.Contains_with_local_collection_empty_inline(async);

        AssertMql(
            """
            Customers.{ "$match" : { "_id" : { "$nin" : [] } } }
            """);
    }

    public override async Task Contains_top_level(bool async)
    {
        // Fails: Incorrect results issue EF-229
        Assert.Contains(
            "Expected: True",
            (await Assert.ThrowsAsync<EqualException>(() => base.Contains_top_level(async))).Message);

        AssertMql(
            """
            Customers.{ "$project" : { "_v" : "$_id", "_id" : 0 } }, { "$match" : { "_v" : { "_v" : "ALFKI" } } }, { "$limit" : 1 }, { "$project" : { "_id" : 0, "_v" : null } }
            """);
    }

    public override async Task Contains_with_local_anonymous_type_array_closure(bool async)
    {
        await base.Contains_with_local_anonymous_type_array_closure(async);

        AssertMql(
            """
            OrderDetails.{ "$match" : { "$expr" : { "$in" : [{ "Id1" : "$_id.OrderID", "Id2" : "$_id.ProductID" }, [{ "Id1" : 1, "Id2" : 2 }, { "Id1" : 10248, "Id2" : 11 }]] } } }
            """);
    }

    public override async Task OfType_Select(bool async)
    {
        // Fails: Projections issue EF-76
        await AssertTranslationFailed(() => base.OfType_Select(async));

        AssertMql(
        );
    }

    public override async Task OfType_Select_OfType_Select(bool async)
    {
        // Fails: Projections issue EF-76
        await AssertTranslationFailed(() => base.OfType_Select_OfType_Select(async));

        AssertMql(
        );
    }

    public override async Task Average_with_non_matching_types_in_projection_doesnt_produce_second_explicit_cast(bool async)
    {
        await base.Average_with_non_matching_types_in_projection_doesnt_produce_second_explicit_cast(async);

        AssertMql(
            """
            Orders.{ "$match" : { "CustomerID" : { "$regularExpression" : { "pattern" : "^A", "options" : "s" } } } }, { "$sort" : { "_id" : 1 } }, { "$project" : { "_v" : { "$toLong" : "$_id" }, "_id" : 0 } }, { "$group" : { "_id" : null, "_v" : { "$avg" : "$_v" } } }, { "$project" : { "_id" : 0 } }
            """);
    }

    public override async Task Max_with_non_matching_types_in_projection_introduces_explicit_cast(bool async)
    {
        await base.Max_with_non_matching_types_in_projection_introduces_explicit_cast(async);

        AssertMql(
            """
            Orders.{ "$match" : { "CustomerID" : { "$regularExpression" : { "pattern" : "^A", "options" : "s" } } } }, { "$sort" : { "_id" : 1 } }, { "$project" : { "_v" : { "$toLong" : "$_id" }, "_id" : 0 } }, { "$group" : { "_id" : null, "_max" : { "$max" : "$$ROOT" } } }, { "$replaceRoot" : { "newRoot" : "$_max" } }
            """);
    }

    public override async Task Min_with_non_matching_types_in_projection_introduces_explicit_cast(bool async)
    {
        await base.Min_with_non_matching_types_in_projection_introduces_explicit_cast(async);

        AssertMql(
            """
            Orders.{ "$match" : { "CustomerID" : { "$regularExpression" : { "pattern" : "^A", "options" : "s" } } } }, { "$project" : { "_v" : { "$toLong" : "$_id" }, "_id" : 0 } }, { "$group" : { "_id" : null, "_min" : { "$min" : "$$ROOT" } } }, { "$replaceRoot" : { "newRoot" : "$_min" } }
            """);
    }

    public override async Task OrderBy_Take_Last_gives_correct_result(bool async)
    {
        await base.OrderBy_Take_Last_gives_correct_result(async);

        AssertMql(
            """
            Customers.{ "$sort" : { "_id" : 1 } }, { "$limit" : 20 }, { "$group" : { "_id" : null, "_last" : { "$last" : "$$ROOT" } } }, { "$replaceRoot" : { "newRoot" : "$_last" } }
            """);
    }

    public override async Task OrderBy_Skip_Last_gives_correct_result(bool async)
    {
        await base.OrderBy_Skip_Last_gives_correct_result(async);

        AssertMql(
            """
            Customers.{ "$sort" : { "_id" : 1 } }, { "$skip" : 20 }, { "$group" : { "_id" : null, "_last" : { "$last" : "$$ROOT" } } }, { "$replaceRoot" : { "newRoot" : "$_last" } }
            """);
    }

    public override async Task Contains_over_entityType_should_rewrite_to_identity_equality(bool async)
    {
        // Fails: Entity equality issue EF-202
        Assert.Contains(
            "Entity to entity comparison is not supported.",
            (await Assert.ThrowsAsync<NotSupportedException>(() =>
                base.Contains_over_entityType_should_rewrite_to_identity_equality(async))).Message);

        AssertMql(
            """
            Orders.{ "$match" : { "_id" : 10248 } }, { "$limit" : 2 }
            """,
            //
            """
            Orders.
            """);
    }

    public override async Task List_Contains_over_entityType_should_rewrite_to_identity_equality(bool async)
    {
        await AssertNoMultiCollectionQuerySupport(() =>
            base.List_Contains_over_entityType_should_rewrite_to_identity_equality(async));
    }

    public override async Task List_Contains_with_constant_list(bool async)
    {
        // Fails: Entity equality issue EF-202
        Assert.Contains(
            "Entity to entity comparison is not supported.",
            (await Assert.ThrowsAsync<NotSupportedException>(() => base.List_Contains_with_constant_list(async))).Message);
        AssertMql(
            """
            Customers.
            """);
    }

    public override async Task List_Contains_with_parameter_list(bool async)
    {
        // Fails: Entity equality issue EF-202
        Assert.Contains(
            "Entity to entity comparison is not supported.",
            (await Assert.ThrowsAsync<NotSupportedException>(() => base.List_Contains_with_parameter_list(async))).Message);

        AssertMql(
            """
            Customers.
            """);
    }

    public override async Task Contains_with_parameter_list_value_type_id(bool async)
    {
        // Fails: Entity equality issue EF-202
        Assert.Contains(
            "Entity to entity comparison is not supported.",
            (await Assert.ThrowsAsync<NotSupportedException>(() => base.Contains_with_parameter_list_value_type_id(async)))
            .Message);

        AssertMql(
            """
            Orders.
            """);
    }

    public override async Task Contains_with_constant_list_value_type_id(bool async)
    {
        // Fails: Entity equality issue EF-202
        Assert.Contains(
            "Entity to entity comparison is not supported.",
            (await Assert.ThrowsAsync<NotSupportedException>(() => base.Contains_over_keyless_entity_throws(async))).Message);

        AssertMql(
            """
            Customers.{ "$limit" : 1 }
            """,
            //
            """
            Customers.
            """);
    }

    public override async Task IImmutableSet_Contains_with_parameter(bool async)
    {
        await base.IImmutableSet_Contains_with_parameter(async);

        AssertMql(
            """
            Customers.{ "$match" : { "_id" : { "$in" : ["ALFKI"] } } }
            """);
    }

    public override async Task IReadOnlySet_Contains_with_parameter(bool async)
    {
        await base.IReadOnlySet_Contains_with_parameter(async);

        AssertMql(
            """
            Customers.{ "$match" : { "_id" : { "$in" : ["ALFKI"] } } }
            """);
    }

    public override async Task HashSet_Contains_with_parameter(bool async)
    {
        await base.HashSet_Contains_with_parameter(async);

        AssertMql(
            """
            Customers.{ "$match" : { "_id" : { "$in" : ["ALFKI"] } } }
            """);
    }

    public override async Task ImmutableHashSet_Contains_with_parameter(bool async)
    {
        await base.ImmutableHashSet_Contains_with_parameter(async);

        AssertMql(
            """
            Customers.{ "$match" : { "_id" : { "$in" : ["ALFKI"] } } }
            """);
    }

    public override async Task Contains_over_entityType_with_null_should_rewrite_to_false(bool async)
    {
        await base.Contains_over_entityType_with_null_should_rewrite_to_false(async);

        AssertMql(
            """
            Orders.{ "$match" : { "CustomerID" : "VINET" } }, { "$project" : { "_id" : 0, "_v" : "$$ROOT" } }, { "$match" : { "_v" : { "_v" : null } } }, { "$limit" : 1 }, { "$project" : { "_id" : 0, "_v" : null } }
            """);
    }

    public override async Task Contains_over_entityType_with_null_should_rewrite_to_identity_equality_subquery(bool async)
    {
        // Fails: Projections issue EF-76
        Assert.Contains(
            "Expression not supported",
            (await Assert.ThrowsAsync<ExpressionNotSupportedException>(() =>
                base.Contains_over_entityType_with_null_should_rewrite_to_identity_equality_subquery(async))).Message);

        AssertMql(
            """
            Orders.
            """);
    }

    public override async Task Contains_over_entityType_with_null_in_projection(bool async)
    {
        // Fails: Projections issue EF-76
        Assert.Contains(
            "Expression not supported",
            (await Assert.ThrowsAsync<ExpressionNotSupportedException>(() =>
                base.Contains_over_entityType_with_null_in_projection(async))).Message);
        AssertMql(
            """
            Orders.
            """);
    }

    public override async Task Contains_over_scalar_with_null_should_rewrite_to_identity_equality_subquery(bool async)
    {
        // Fails: Projections issue EF-76
        Assert.Contains(
            "Expression not supported",
            (await Assert.ThrowsAsync<ExpressionNotSupportedException>(() =>
                base.Contains_over_scalar_with_null_should_rewrite_to_identity_equality_subquery(async))).Message);

        AssertMql(
            """
            Orders.
            """);
    }

    public override async Task Contains_over_entityType_with_null_should_rewrite_to_identity_equality_subquery_negated(bool async)
    {
        // Fails: Projections issue EF-76
        Assert.Contains(
            "Expression not supported",
            (await Assert.ThrowsAsync<ExpressionNotSupportedException>(() =>
                base.Contains_over_entityType_with_null_should_rewrite_to_identity_equality_subquery_negated(async))).Message);

        AssertMql(
            """
            Orders.
            """);
    }

    public override async Task Contains_over_entityType_with_null_should_rewrite_to_identity_equality_subquery_complex(bool async)
    {
        // Fails: Projections issue EF-76
        Assert.Contains(
            "Expression not supported",
            (await Assert.ThrowsAsync<ExpressionNotSupportedException>(() =>
                base.Contains_over_entityType_with_null_should_rewrite_to_identity_equality_subquery_complex(async))).Message);

        AssertMql(
            """
            Orders.
            """);
    }

    public override async Task Contains_over_nullable_scalar_with_null_in_subquery_translated_correctly(bool async)
    {
        // Fails: Projections issue EF-76
        await AssertTranslationFailed(() => base.Contains_over_nullable_scalar_with_null_in_subquery_translated_correctly(async));

        AssertMql(
        );
    }

    public override async Task Contains_over_non_nullable_scalar_with_null_in_subquery_simplifies_to_false(bool async)
    {
        // Fails: Projections issue EF-76
        await AssertTranslationFailed(() =>
            base.Contains_over_non_nullable_scalar_with_null_in_subquery_simplifies_to_false(async));

        AssertMql(
        );
    }

    public override async Task Contains_over_entityType_should_materialize_when_composite(bool async)
    {
        // Fails: Projections issue EF-76
        Assert.Contains(
            "Expression not supported",
            (await Assert.ThrowsAsync<ExpressionNotSupportedException>(() =>
                base.Contains_over_entityType_should_materialize_when_composite(async))).Message);

        AssertMql(
            """
            OrderDetails.
            """);
    }

    public override async Task Contains_over_entityType_should_materialize_when_composite2(bool async)
    {
        // Fails: Projections issue EF-76
        Assert.Contains(
            "Expression not supported",
            (await Assert.ThrowsAsync<ExpressionNotSupportedException>(() =>
                base.Contains_over_entityType_should_materialize_when_composite2(async))).Message);

        AssertMql(
            """
            OrderDetails.
            """);
    }

    public override async Task String_FirstOrDefault_in_projection_does_not_do_client_eval(bool async)
    {
        // Fails: String.FirstOrDefault issue EF-231
        Assert.Contains(
            "StringSerializer must implement IBsonArraySerializer",
            (await Assert.ThrowsAsync<InvalidOperationException>(() =>
                base.String_FirstOrDefault_in_projection_does_not_do_client_eval(async))).Message);

        AssertMql(
            """
            Customers.
            """);
    }

    public override async Task Project_constant_Sum(bool async)
    {
        await base.Project_constant_Sum(async);

        AssertMql(
            """
            Employees.{ "$group" : { "_id" : null, "_v" : { "$sum" : 1 } } }, { "$project" : { "_id" : 0 } }
            """);
    }

    public override async Task Where_subquery_any_equals_operator(bool async)
    {
        await base.Where_subquery_any_equals_operator(async);

        AssertMql(
            """
            Customers.{ "$match" : { "_id" : { "$in" : ["ABCDE", "ALFKI", "ANATR"] } } }
            """);
    }

    public override async Task Where_subquery_any_equals(bool async)
    {
        await base.Where_subquery_any_equals(async);

        AssertMql(
            """
            Customers.{ "$match" : { "_id" : { "$in" : ["ABCDE", "ALFKI", "ANATR"] } } }
            """);
    }

    public override async Task Where_subquery_any_equals_static(bool async)
    {
        await base.Where_subquery_any_equals_static(async);

        AssertMql(
            """
            Customers.{ "$match" : { "_id" : { "$in" : ["ABCDE", "ALFKI", "ANATR"] } } }
            """);
    }

    public override async Task Where_subquery_where_any(bool async)
    {
        await base.Where_subquery_where_any(async);

        AssertMql(
            """
            Customers.{ "$match" : { "City" : "México D.F." } }, { "$match" : { "_id" : { "$in" : ["ABCDE", "ALFKI", "ANATR"] } } }
            """,
            //
            """
            Customers.{ "$match" : { "City" : "México D.F." } }, { "$match" : { "_id" : { "$in" : ["ABCDE", "ALFKI", "ANATR"] } } }
            """);
    }

    public override async Task Where_subquery_all_not_equals_operator(bool async)
    {
        await base.Where_subquery_all_not_equals_operator(async);

        AssertMql(
            """
            Customers.{ "$match" : { "_id" : { "$nin" : ["ABCDE", "ALFKI", "ANATR"] } } }
            """);
    }

    public override async Task Where_subquery_all_not_equals(bool async)
    {
        await base.Where_subquery_all_not_equals(async);

        AssertMql(
            """
            Customers.{ "$match" : { "_id" : { "$nin" : ["ABCDE", "ALFKI", "ANATR"] } } }
            """);
    }

    public override async Task Where_subquery_all_not_equals_static(bool async)
    {
        await base.Where_subquery_all_not_equals_static(async);

        AssertMql(
            """
            Customers.{ "$match" : { "_id" : { "$nin" : ["ABCDE", "ALFKI", "ANATR"] } } }
            """);
    }

    public override async Task Where_subquery_where_all(bool async)
    {
        await base.Where_subquery_where_all(async);

        AssertMql(
            """
            Customers.{ "$match" : { "City" : "México D.F." } }, { "$match" : { "_id" : { "$nin" : ["ABCDE", "ALFKI", "ANATR"] } } }
            """,
            //
            """
            Customers.{ "$match" : { "City" : "México D.F." } }, { "$match" : { "_id" : { "$nin" : ["ABCDE", "ALFKI", "ANATR"] } } }
            """);
    }

    public override async Task Cast_to_same_Type_Count_works(bool async)
    {
        await base.Cast_to_same_Type_Count_works(async);

        AssertMql(
            """
            Customers.{ "$count" : "_v" }
            """);
    }

    public override async Task Cast_before_aggregate_is_preserved(bool async)
    {
        // Fails: Projections issue EF-76
        await AssertTranslationFailed(() => base.Cast_before_aggregate_is_preserved(async));

        AssertMql(
        );
    }

    public override async Task Collection_Last_member_access_in_projection_translated(bool async)
    {
        await AssertNoMultiCollectionQuerySupport(() => base.Collection_Last_member_access_in_projection_translated(async));
    }

    public override async Task Collection_LastOrDefault_member_access_in_projection_translated(bool async)
    {
        await AssertNoMultiCollectionQuerySupport(() =>
            base.Collection_LastOrDefault_member_access_in_projection_translated(async));
    }

    public override async Task Sum_over_explicit_cast_over_column(bool async)
    {
        await base.Sum_over_explicit_cast_over_column(async);

        AssertMql(
            """
            Orders.{ "$group" : { "_id" : null, "_v" : { "$sum" : { "$toLong" : "$_id" } } } }, { "$project" : { "_id" : 0 } }
            """);
    }

    public override async Task Count_on_projection_with_client_eval(bool async)
    {
        await base.Count_on_projection_with_client_eval(async);

        AssertMql(
            """
            Orders.{ "$count" : "_v" }
            """,
            //
            """
            Orders.{ "$count" : "_v" }
            """,
            //
            """
            Orders.{ "$count" : "_v" }
            """);
    }

    public override async Task Average_on_nav_subquery_in_projection(bool async)
    {
        // Fails: Projections issue EF-76
        await AssertTranslationFailed(() => base.Average_on_nav_subquery_in_projection(async));

        AssertMql(
        );
    }

    public override async Task Count_after_client_projection(bool async)
    {
        await base.Count_after_client_projection(async);

        AssertMql(
            """
            Orders.{ "$limit" : 1 }, { "$count" : "_v" }
            """);
    }

    public override async Task All_true(bool async)
    {
        await base.All_true(async);

        AssertMql(
            """
            Customers.{ "$match" : { "_id" : { "$type" : -1 } } }, { "$limit" : 1 }, { "$project" : { "_id" : 0, "_v" : null } }
            """);
    }

    public override async Task Not_Any_false(bool async)
    {
        await AssertNoMultiCollectionQuerySupport(() => base.Not_Any_false(async));
    }

    public override async Task Contains_inside_aggregate_function_with_GroupBy(bool async)
    {
        await AssertTranslationFailed(() => base.Contains_inside_aggregate_function_with_GroupBy(async));
    }

    public override async Task Contains_inside_Average_without_GroupBy(bool async)
    {
        await base.Contains_inside_Average_without_GroupBy(async);

        AssertMql(
            """
            Customers.{ "$group" : { "_id" : null, "_v" : { "$avg" : { "$cond" : { "if" : { "$in" : ["$City", ["London", "Berlin"]] }, "then" : 1.0, "else" : 0.0 } } } } }, { "$project" : { "_id" : 0 } }
            """);
    }

    public override async Task Contains_inside_Sum_without_GroupBy(bool async)
    {
        await base.Contains_inside_Sum_without_GroupBy(async);

        AssertMql(
            """
            Customers.{ "$group" : { "_id" : null, "_v" : { "$sum" : { "$cond" : { "if" : { "$in" : ["$City", ["London", "Berlin"]] }, "then" : 1, "else" : 0 } } } } }, { "$project" : { "_id" : 0 } }
            """);
    }

    public override async Task Contains_inside_Count_without_GroupBy(bool async)
    {
        await base.Contains_inside_Count_without_GroupBy(async);

        AssertMql(
            """
            Customers.{ "$match" : { "City" : { "$in" : ["London", "Berlin"] } } }, { "$count" : "_v" }
            """);
    }

    public override async Task Contains_inside_LongCount_without_GroupBy(bool async)
    {
        await base.Contains_inside_LongCount_without_GroupBy(async);

        AssertMql(
            """
            Customers.{ "$match" : { "City" : { "$in" : ["London", "Berlin"] } } }, { "$count" : "_v" }
            """);
    }

    public override async Task Contains_inside_Max_without_GroupBy(bool async)
    {
        await base.Contains_inside_Max_without_GroupBy(async);

        AssertMql(
            """
            Customers.{ "$group" : { "_id" : null, "_max" : { "$max" : { "_v" : { "$cond" : { "if" : { "$in" : ["$City", ["London", "Berlin"]] }, "then" : 1, "else" : 0 } } } } } }, { "$replaceRoot" : { "newRoot" : "$_max" } }
            """);
    }

    public override async Task Contains_inside_Min_without_GroupBy(bool async)
    {
        await base.Contains_inside_Min_without_GroupBy(async);

        AssertMql(
            """
            Customers.{ "$group" : { "_id" : null, "_min" : { "$min" : { "_v" : { "$cond" : { "if" : { "$in" : ["$City", ["London", "Berlin"]] }, "then" : 1, "else" : 0 } } } } } }, { "$replaceRoot" : { "newRoot" : "$_min" } }
            """);
    }

#if EF8 || EF9
    public override async Task DefaultIfEmpty_selects_only_required_columns(bool async)
    {
        await AssertNoProjectionSupport(() => base.DefaultIfEmpty_selects_only_required_columns(async));
    }

#else

    public override async Task Average_after_DefaultIfEmpty_does_not_throw(bool async)
    {
        await AssertTranslationFailed(() => base.Average_after_DefaultIfEmpty_does_not_throw(async));
    }

    public override async Task Max_after_DefaultIfEmpty_does_not_throw(bool async)
    {
        await AssertTranslationFailed(() => base.Max_after_DefaultIfEmpty_does_not_throw(async));
    }

    public override async Task Min_after_DefaultIfEmpty_does_not_throw(bool async)
    {
        await AssertTranslationFailed(() => base.Min_after_DefaultIfEmpty_does_not_throw(async));
    }

    public override async Task DefaultIfEmpty_selects_only_required_columns(bool async)
    {
        // Fails: Projections issue EF-76
        await AssertTranslationFailed(() => base.DefaultIfEmpty_selects_only_required_columns(async));

        AssertMql(
        );
    }

#endif

#if !EF8

    public override async Task Return_type_of_singular_operator_is_preserved(bool async)
    {
        await base.Return_type_of_singular_operator_is_preserved(async);

        AssertMql(
            """
            Customers.{ "$match" : { "_id" : "ALFKI" } }, { "$project" : { "CustomerId" : "$_id", "City" : "$City", "_id" : 0 } }, { "$limit" : 1 }
            """,
            //
            """
            Customers.{ "$match" : { "_id" : "ALFKI" } }, { "$project" : { "CustomerId" : "$_id", "City" : "$City", "_id" : 0 } }, { "$limit" : 1 }
            """,
            //
            """
            Customers.{ "$match" : { "_id" : "ALFKI" } }, { "$project" : { "CustomerId" : "$_id", "City" : "$City", "_id" : 0 } }, { "$limit" : 2 }
            """,
            //
            """
            Customers.{ "$match" : { "_id" : "ALFKI" } }, { "$project" : { "CustomerId" : "$_id", "City" : "$City", "_id" : 0 } }, { "$limit" : 2 }
            """,
            //
            """
            Customers.{ "$match" : { "_id" : { "$regularExpression" : { "pattern" : "^A", "options" : "s" } } } }, { "$sort" : { "_id" : 1 } }, { "$project" : { "CustomerId" : "$_id", "City" : "$City", "_id" : 0 } }, { "$group" : { "_id" : null, "_last" : { "$last" : "$$ROOT" } } }, { "$replaceRoot" : { "newRoot" : "$_last" } }
            """,
            //
            """
            Customers.{ "$match" : { "_id" : { "$regularExpression" : { "pattern" : "^A", "options" : "s" } } } }, { "$sort" : { "_id" : 1 } }, { "$project" : { "CustomerId" : "$_id", "City" : "$City", "_id" : 0 } }, { "$group" : { "_id" : null, "_last" : { "$last" : "$$ROOT" } } }, { "$replaceRoot" : { "newRoot" : "$_last" } }
            """);
    }

    public override async Task Type_casting_inside_sum(bool async)
    {
        // Returns 121.04000180587159838 instead of 121.040 because of conversion errors.
        Assert.Contains(
            "Actual:   121.04000180587159838",
            (await Assert.ThrowsAsync<EqualException>(() =>
                base.Type_casting_inside_sum(async))).Message);

        AssertMql(
            """
            OrderDetails.{ "$group" : { "_id" : null, "_v" : { "$sum" : { "$toDecimal" : "$Discount" } } } }, { "$project" : { "_id" : 0 } }
            """);
    }

#endif

    private void AssertMql(params string[] expected)
        => Fixture.TestMqlLoggerFactory.AssertBaseline(expected);

    protected override void ClearLog()
        => Fixture.TestMqlLoggerFactory.Clear();

    // Fails: Projections issue EF-76
    private static Task AssertNoProjectionSupport(Func<Task> query)
        => Assert.ThrowsAsync<InvalidOperationException>(query);

    // Fails: Cross-document navigation access issue EF-216
    private static async Task AssertNoMultiCollectionQuerySupport(Func<Task> query)
        => Assert.Contains("Unsupported cross-DbSet query between",
            (await Assert.ThrowsAsync<InvalidOperationException>(query)).Message);
}
