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
using static MongoDB.EntityFrameworkCore.SpecificationTests.Utilities.MongoAssert;

namespace MongoDB.EntityFrameworkCore.SpecificationTests.Query;

public class NorthwindEFPropertyIncludeQueryMongoTest : NorthwindEFPropertyIncludeQueryTestBase<
    NorthwindQueryMongoFixture<NoopModelCustomizer>>
{
    public NorthwindEFPropertyIncludeQueryMongoTest(NorthwindQueryMongoFixture<NoopModelCustomizer> fixture)
        : base(fixture)
        => Fixture.TestMqlLoggerFactory.Clear();

    [ConditionalFact]
    public virtual void Check_all_tests_overridden()
        => TestHelpers.AssertAllMethodsOverridden(GetType());

    #if !EF8 && !EF9

    public override async Task Include_collection_with_right_join_clause_with_filter(bool async)
    {
        await base.Include_collection_with_right_join_clause_with_filter(async);

        AssertMql();
    }

    #endif

    public override async Task Include_collection_with_last_no_orderby(bool async)
    {
        await base.Include_collection_with_last_no_orderby(async);
        AssertMql(
            """
Customers.{ "$lookup" : { "from" : "Orders", "localField" : "_id", "foreignField" : "CustomerID", "as" : "_lookup_Orders" } }, { "$group" : { "_id" : null, "_last" : { "$last" : "$$ROOT" } } }, { "$replaceRoot" : { "newRoot" : "$_last" } }
""");
    }

    public override async Task Include_collection_with_filter_reordered(bool async)
    {
        await base.Include_collection_with_filter_reordered(async);
        AssertMql(
            """
Customers.{ "$match" : { "_id" : "ALFKI" } }, { "$lookup" : { "from" : "Orders", "localField" : "_id", "foreignField" : "CustomerID", "as" : "_lookup_Orders" } }
""");
    }

    public override async Task Include_collection_order_by_non_key_with_first_or_default(bool async)
    {
        await base.Include_collection_order_by_non_key_with_first_or_default(async);
        AssertMql(
            """
Customers.{ "$sort" : { "CompanyName" : -1 } }, { "$lookup" : { "from" : "Orders", "localField" : "_id", "foreignField" : "CustomerID", "as" : "_lookup_Orders" } }, { "$limit" : 1 }
""");
    }

    public override async Task Include_with_cycle_does_not_throw_when_AsTracking_NoTrackingWithIdentityResolution(bool async)
    {
        await base.Include_with_cycle_does_not_throw_when_AsTracking_NoTrackingWithIdentityResolution(async);
        AssertMql(
            """
Orders.{ "$match" : { "_id" : { "$lt" : 10800 } } }, { "$project" : { "_outer" : "$$ROOT", "_id" : 0 } }, { "$lookup" : { "from" : "Customers", "localField" : "_outer.CustomerID", "foreignField" : "_id", "as" : "_inner" } }, { "$unwind" : { "path" : "$_inner", "preserveNullAndEmptyArrays" : true } }, { "$project" : { "_outer" : "$_outer", "_inner" : "$_inner", "_id" : 0 } }, { "$lookup" : { "from" : "Orders", "localField" : "_inner._id", "foreignField" : "CustomerID", "as" : "_inner._lookup_Orders" } }
""");
    }

    public override async Task Include_collection_with_filter(bool async)
    {
        await base.Include_collection_with_filter(async);
        AssertMql(
            """
Customers.{ "$match" : { "_id" : "ALFKI" } }, { "$lookup" : { "from" : "Orders", "localField" : "_id", "foreignField" : "CustomerID", "as" : "_lookup_Orders" } }
""");
    }

    public override async Task Include_references_then_include_multi_level(bool async)
    {
        await base.Include_references_then_include_multi_level(async);

        AssertMql(
);
    }

    public override async Task Include_collection_order_by_collection_column(bool async)
    {
        await AssertUnsupportedCrossDbSetQuery(() => base.Include_collection_order_by_collection_column(async));

        AssertMql(
);
    }

    public override async Task Include_collection_alias_generation(bool async)
    {
        await base.Include_collection_alias_generation(async);

        AssertMql(
            """
Orders.{ "$match" : { "CustomerID" : { "$regularExpression" : { "pattern" : "^F", "options" : "s" } } } }, { "$lookup" : { "from" : "OrderDetails", "localField" : "_id", "foreignField" : "_id.OrderID", "as" : "_lookup_OrderDetails" } }
""");
    }

    public override async Task Include_collection_skip_take_no_order_by(bool async)
    {
        await base.Include_collection_skip_take_no_order_by(async);
        AssertMql(
            """
Customers.{ "$skip" : 10 }, { "$limit" : 5 }, { "$lookup" : { "from" : "Orders", "localField" : "_id", "foreignField" : "CustomerID", "as" : "_lookup_Orders" } }
""");
    }

    public override async Task Include_collection_with_cross_join_clause_with_filter(bool async)
    {
        await base.Include_collection_with_cross_join_clause_with_filter(async);

        AssertMql(
);
    }

    public override async Task Join_Include_reference_GroupBy_Select(bool async)
    {
        await base.Join_Include_reference_GroupBy_Select(async);

        AssertMql(
);
    }

    public override async Task Include_multi_level_reference_and_collection_predicate(bool async)
    {
        await base.Include_multi_level_reference_and_collection_predicate(async);

        AssertMql(
            """
Orders.{ "$match" : { "_id" : 10248 } }, { "$project" : { "_outer" : "$$ROOT", "_id" : 0 } }, { "$lookup" : { "from" : "Customers", "localField" : "_outer.CustomerID", "foreignField" : "_id", "as" : "_inner" } }, { "$unwind" : { "path" : "$_inner", "preserveNullAndEmptyArrays" : true } }, { "$project" : { "_outer" : "$_outer", "_inner" : "$_inner", "_id" : 0 } }, { "$lookup" : { "from" : "Orders", "localField" : "_inner._id", "foreignField" : "CustomerID", "as" : "_inner._lookup_Orders" } }, { "$limit" : 2 }
""");
    }

    public override async Task Include_references_then_include_collection(bool async)
    {
        await base.Include_references_then_include_collection(async);
        AssertMql(
            """
Orders.{ "$match" : { "CustomerID" : { "$regularExpression" : { "pattern" : "^F", "options" : "s" } } } }, { "$project" : { "_outer" : "$$ROOT", "_id" : 0 } }, { "$lookup" : { "from" : "Customers", "localField" : "_outer.CustomerID", "foreignField" : "_id", "as" : "_inner" } }, { "$unwind" : { "path" : "$_inner", "preserveNullAndEmptyArrays" : true } }, { "$project" : { "_outer" : "$_outer", "_inner" : "$_inner", "_id" : 0 } }, { "$lookup" : { "from" : "Orders", "localField" : "_inner._id", "foreignField" : "CustomerID", "as" : "_inner._lookup_Orders" } }
""");
    }

    public override async Task Include_collection_on_additional_from_clause_with_filter(bool async)
    {
        await base.Include_collection_on_additional_from_clause_with_filter(async);

        AssertMql(
);
    }

    public override async Task Include_duplicate_reference3(bool async)
    {
        await base.Include_duplicate_reference3(async);

        AssertMql(
);
    }

    public override async Task Include_collection_order_by_non_key_with_take(bool async)
    {
        await base.Include_collection_order_by_non_key_with_take(async);
        AssertMql(
            """
Customers.{ "$sort" : { "ContactTitle" : 1 } }, { "$limit" : 10 }, { "$lookup" : { "from" : "Orders", "localField" : "_id", "foreignField" : "CustomerID", "as" : "_lookup_Orders" } }
""");
    }

    public override async Task Include_collection_then_include_collection_predicate(bool async)
    {
        await base.Include_collection_then_include_collection_predicate(async);

        AssertMql(
            """
Customers.{ "$match" : { "_id" : "ALFKI" } }, { "$lookup" : { "from" : "Orders", "let" : { "localField" : "$_id" }, "pipeline" : [{ "$match" : { "$expr" : { "$eq" : ["$CustomerID", "$$localField"] } } }, { "$lookup" : { "from" : "OrderDetails", "localField" : "_id", "foreignField" : "_id.OrderID", "as" : "_lookup_OrderDetails" } }], "as" : "_lookup_Orders" } }, { "$limit" : 2 }
""");
    }

    public override async Task Include_collection_take_no_order_by(bool async)
    {
        await base.Include_collection_take_no_order_by(async);
        AssertMql(
            """
Customers.{ "$limit" : 10 }, { "$lookup" : { "from" : "Orders", "localField" : "_id", "foreignField" : "CustomerID", "as" : "_lookup_Orders" } }
""");
    }

    public override async Task Include_collection_principal_already_tracked(bool async)
    {
        await base.Include_collection_principal_already_tracked(async);
        AssertMql(
            """
Customers.{ "$match" : { "_id" : "ALFKI" } }, { "$limit" : 2 }
""",
            //
            """
Customers.{ "$match" : { "_id" : "ALFKI" } }, { "$lookup" : { "from" : "Orders", "localField" : "_id", "foreignField" : "CustomerID", "as" : "_lookup_Orders" } }, { "$limit" : 2 }
""");
    }

    public override async Task Include_collection_OrderBy_object(bool async)
    {
        await base.Include_collection_OrderBy_object(async);

        AssertMql(
            """
Orders.{ "$match" : { "_id" : { "$lt" : 10250 } } }, { "$sort" : { "_id" : 1 } }, { "$lookup" : { "from" : "OrderDetails", "localField" : "_id", "foreignField" : "_id.OrderID", "as" : "_lookup_OrderDetails" } }
""");
    }

    public override async Task Include_duplicate_collection_result_operator2(bool async)
    {
        await base.Include_duplicate_collection_result_operator2(async);

        AssertMql(
);
    }

    public override async Task Repro9735(bool async)
    {
        await base.Repro9735(async);

        AssertMql(
            """
Orders.{ "$project" : { "_outer" : "$$ROOT", "_id" : 0 } }, { "$lookup" : { "from" : "Customers", "localField" : "_outer.CustomerID", "foreignField" : "_id", "as" : "_inner" } }, { "$unwind" : { "path" : "$_inner", "preserveNullAndEmptyArrays" : true } }, { "$project" : { "Outer" : "$_outer", "Inner" : "$_inner", "_id" : 0 } }, { "$project" : { "_id" : 0, "_document" : "$$ROOT", "_key1" : { "$ne" : ["$Inner._id", null] }, "_key2" : { "$cond" : { "if" : { "$ne" : ["$Inner", null] }, "then" : "$Inner._id", "else" : "" } } } }, { "$sort" : { "_key1" : 1, "_key2" : 1 } }, { "$replaceRoot" : { "newRoot" : "$_document" } }, { "$limit" : 2 }, { "$lookup" : { "from" : "OrderDetails", "localField" : "_id", "foreignField" : "OrderID", "as" : "_lookup_OrderDetails" } }
""");
    }

    public override async Task Include_collection_single_or_default_no_result(bool async)
    {
        await base.Include_collection_single_or_default_no_result(async);
        AssertMql(
            """
Customers.{ "$match" : { "_id" : "ALFKI ?" } }, { "$lookup" : { "from" : "Orders", "localField" : "_id", "foreignField" : "CustomerID", "as" : "_lookup_Orders" } }, { "$limit" : 2 }
""");
    }

    public override async Task Include_collection_with_cross_apply_with_filter(bool async)
    {
        await base.Include_collection_with_cross_apply_with_filter(async);

        AssertMql(
);
    }

    public override async Task Include_collection_with_left_join_clause_with_filter(bool async)
    {
        await base.Include_collection_with_left_join_clause_with_filter(async);

        AssertMql(
            """
Customers.{ "$project" : { "_outer" : "$$ROOT", "_id" : 0 } }, { "$lookup" : { "from" : "Orders", "localField" : "_outer._id", "foreignField" : "CustomerID", "as" : "_inner" } }, { "$unwind" : { "path" : "$_inner", "preserveNullAndEmptyArrays" : true } }, { "$project" : { "_outer" : "$_outer", "_inner" : "$_inner", "_id" : 0 } }, { "$match" : { "_outer._id" : { "$regularExpression" : { "pattern" : "^F", "options" : "s" } } } }, { "$lookup" : { "from" : "Orders", "localField" : "_outer._id", "foreignField" : "CustomerID", "as" : "_outer._lookup_Orders" } }
""");
    }

    public override async Task Include_duplicate_collection(bool async)
    {
        await base.Include_duplicate_collection(async);

        AssertMql(
);
    }

    public override async Task Include_collection(bool async)
    {
        await base.Include_collection(async);
        AssertMql(
            """
Customers.{ "$match" : { "_id" : { "$regularExpression" : { "pattern" : "^F", "options" : "s" } } } }, { "$lookup" : { "from" : "Orders", "localField" : "_id", "foreignField" : "CustomerID", "as" : "_lookup_Orders" } }
""");
    }

    public override async Task Include_collection_then_include_collection_then_include_reference(bool async)
    {
        await base.Include_collection_then_include_collection_then_include_reference(async);

        AssertMql(
            """
Customers.{ "$match" : { "_id" : { "$regularExpression" : { "pattern" : "^F", "options" : "s" } } } }, { "$lookup" : { "from" : "Orders", "localField" : "_id", "foreignField" : "CustomerID", "as" : "_lookup_Orders" } }
""");
    }

    public override async Task Include_reference_GroupBy_Select(bool async)
    {
        await base.Include_reference_GroupBy_Select(async);

        AssertMql(
);
    }

    public override async Task Include_multiple_references_multi_level_reverse(bool async)
    {
        await base.Include_multiple_references_multi_level_reverse(async);

        AssertMql(
);
    }

    public override async Task Include_collection_with_join_clause_with_filter(bool async)
    {
        await base.Include_collection_with_join_clause_with_filter(async);

        AssertMql(
            """
Customers.{ "$project" : { "_outer" : "$$ROOT", "_id" : 0 } }, { "$lookup" : { "from" : "Orders", "localField" : "_outer._id", "foreignField" : "CustomerID", "as" : "_inner" } }, { "$unwind" : "$_inner" }, { "$project" : { "_outer" : "$_outer", "_inner" : "$_inner", "_id" : 0 } }, { "$match" : { "_outer._id" : { "$regularExpression" : { "pattern" : "^F", "options" : "s" } } } }, { "$lookup" : { "from" : "Orders", "localField" : "_outer._id", "foreignField" : "CustomerID", "as" : "_outer._lookup_Orders" } }
""");
    }

    public override async Task Include_collection_OrderBy_list_does_not_contains(bool async)
    {
        await base.Include_collection_OrderBy_list_does_not_contains(async);
        AssertMql(
            """
Customers.{ "$match" : { "_id" : { "$regularExpression" : { "pattern" : "^A", "options" : "s" } } } }, { "$project" : { "_id" : 0, "_document" : "$$ROOT", "_key1" : { "$not" : { "$in" : ["$_id", ["ALFKI"]] } } } }, { "$sort" : { "_key1" : 1 } }, { "$replaceRoot" : { "newRoot" : "$_document" } }, { "$skip" : 1 }, { "$lookup" : { "from" : "Orders", "localField" : "_id", "foreignField" : "CustomerID", "as" : "_lookup_Orders" } }
""");
    }

    public override async Task Include_reference_dependent_already_tracked(bool async)
    {
        await base.Include_reference_dependent_already_tracked(async);
        AssertMql(
            """
Customers.{ "$match" : { "_id" : "ALFKI" } }, { "$limit" : 2 }
""",
            //
            """
Orders.{ "$match" : { "CustomerID" : "ALFKI" } }, { "$project" : { "_outer" : "$$ROOT", "_id" : 0 } }, { "$lookup" : { "from" : "Customers", "localField" : "_outer.CustomerID", "foreignField" : "_id", "as" : "_inner" } }, { "$unwind" : { "path" : "$_inner", "preserveNullAndEmptyArrays" : true } }, { "$project" : { "_outer" : "$_outer", "_inner" : "$_inner", "_id" : 0 } }
""");
    }

    public override async Task Include_reference_with_filter(bool async)
    {
        await base.Include_reference_with_filter(async);
        AssertMql(
            """
Orders.{ "$match" : { "CustomerID" : "ALFKI" } }, { "$project" : { "_outer" : "$$ROOT", "_id" : 0 } }, { "$lookup" : { "from" : "Customers", "localField" : "_outer.CustomerID", "foreignField" : "_id", "as" : "_inner" } }, { "$unwind" : { "path" : "$_inner", "preserveNullAndEmptyArrays" : true } }, { "$project" : { "_outer" : "$_outer", "_inner" : "$_inner", "_id" : 0 } }
""");
    }

    public override async Task Include_duplicate_reference(bool async)
    {
        await base.Include_duplicate_reference(async);

        AssertMql(
);
    }

    public override async Task Include_with_complex_projection(bool async)
    {
        await base.Include_with_complex_projection(async);
        AssertMql(
            """
Orders.{ "$project" : { "_outer" : "$$ROOT", "_id" : 0 } }, { "$lookup" : { "from" : "Customers", "localField" : "_outer.CustomerID", "foreignField" : "_id", "as" : "_inner" } }, { "$unwind" : { "path" : "$_inner", "preserveNullAndEmptyArrays" : true } }, { "$project" : { "Outer" : "$_outer", "Inner" : "$_inner", "_id" : 0 } }, { "$project" : { "CustomerId" : { "_id" : "$Inner._id" }, "_id" : 0 } }
""");
    }

    public override async Task Include_collection_order_by_non_key_with_skip(bool async)
    {
        await base.Include_collection_order_by_non_key_with_skip(async);
        AssertMql(
            """
Customers.{ "$match" : { "_id" : { "$regularExpression" : { "pattern" : "^F", "options" : "s" } } } }, { "$sort" : { "ContactTitle" : 1 } }, { "$skip" : 2 }, { "$lookup" : { "from" : "Orders", "localField" : "_id", "foreignField" : "CustomerID", "as" : "_lookup_Orders" } }
""");
    }

    public override async Task Include_collection_on_join_clause_with_order_by_and_filter(bool async)
    {
        await base.Include_collection_on_join_clause_with_order_by_and_filter(async);

        AssertMql(
            """
Customers.{ "$project" : { "_outer" : "$$ROOT", "_id" : 0 } }, { "$lookup" : { "from" : "Orders", "localField" : "_outer._id", "foreignField" : "CustomerID", "as" : "_inner" } }, { "$unwind" : "$_inner" }, { "$project" : { "_outer" : "$_outer", "_inner" : "$_inner", "_id" : 0 } }, { "$match" : { "_outer._id" : "ALFKI" } }, { "$sort" : { "_outer.City" : 1 } }, { "$lookup" : { "from" : "Orders", "localField" : "_outer._id", "foreignField" : "CustomerID", "as" : "_outer._lookup_Orders" } }
""");
    }

    public override async Task Multi_level_includes_are_applied_with_take(bool async)
    {
        await base.Multi_level_includes_are_applied_with_take(async);

        AssertMql(
);
    }

    public override async Task Include_multiple_references_then_include_collection_multi_level_reverse(bool async)
    {
        await base.Include_multiple_references_then_include_collection_multi_level_reverse(async);

        AssertMql(
);
    }

    public override async Task Include_collection_then_reference(bool async)
    {
        await base.Include_collection_then_reference(async);

        AssertMql(
            """
Products.{ "$match" : { "_id" : { "$mod" : [17, 5] } } }, { "$lookup" : { "from" : "OrderDetails", "localField" : "_id", "foreignField" : "ProductID", "as" : "_lookup_OrderDetails" } }
""");
    }

    public override async Task Include_collection_order_by_key(bool async)
    {
        await base.Include_collection_order_by_key(async);
        AssertMql(
            """
Customers.{ "$match" : { "_id" : { "$regularExpression" : { "pattern" : "^F", "options" : "s" } } } }, { "$sort" : { "_id" : 1 } }, { "$lookup" : { "from" : "Orders", "localField" : "_id", "foreignField" : "CustomerID", "as" : "_lookup_Orders" } }
""");
    }

    public override async Task Include_collection_with_outer_apply_with_filter(bool async)
    {
        await base.Include_collection_with_outer_apply_with_filter(async);

        AssertMql(
);
    }

    public override async Task Include_collection_on_additional_from_clause2(bool async)
    {
        await base.Include_collection_on_additional_from_clause2(async);

        AssertMql(
);
    }

    public override async Task Include_collection_dependent_already_tracked(bool async)
    {
        await base.Include_collection_dependent_already_tracked(async);
        AssertMql(
            """
Orders.{ "$match" : { "CustomerID" : "ALFKI" } }
""",
            //
            """
Customers.{ "$match" : { "_id" : "ALFKI" } }, { "$lookup" : { "from" : "Orders", "localField" : "_id", "foreignField" : "CustomerID", "as" : "_lookup_Orders" } }, { "$limit" : 2 }
""");
    }

    public override async Task Include_with_complex_projection_does_not_change_ordering_of_projection(bool async)
    {
        await base.Include_with_complex_projection_does_not_change_ordering_of_projection(async);

        AssertMql(
);
    }

    public override async Task Include_multi_level_collection_and_then_include_reference_predicate(bool async)
    {
        await base.Include_multi_level_collection_and_then_include_reference_predicate(async);

        AssertMql(
            """
Orders.{ "$match" : { "_id" : 10248 } }, { "$lookup" : { "from" : "OrderDetails", "localField" : "_id", "foreignField" : "OrderID", "as" : "_lookup_OrderDetails" } }, { "$limit" : 2 }
""");
    }

    public override async Task Multi_level_includes_are_applied_with_skip_take(bool async)
    {
        await base.Multi_level_includes_are_applied_with_skip_take(async);

        AssertMql(
);
    }

    public override async Task Include_collection_OrderBy_empty_list_contains(bool async)
    {
        await base.Include_collection_OrderBy_empty_list_contains(async);
        AssertMql(
            """
Customers.{ "$match" : { "_id" : { "$regularExpression" : { "pattern" : "^A", "options" : "s" } } } }, { "$project" : { "_id" : 0, "_document" : "$$ROOT", "_key1" : { "$in" : ["$_id", []] } } }, { "$sort" : { "_key1" : 1 } }, { "$replaceRoot" : { "newRoot" : "$_document" } }, { "$skip" : 1 }, { "$lookup" : { "from" : "Orders", "localField" : "_id", "foreignField" : "CustomerID", "as" : "_lookup_Orders" } }
""");
    }

    public override async Task Include_references_and_collection_multi_level(bool async)
    {
        await base.Include_references_and_collection_multi_level(async);

        AssertMql(
);
    }

    public override async Task Include_collection_force_alias_uniquefication(bool async)
    {
        await base.Include_collection_force_alias_uniquefication(async);
        AssertMql(
            """
Orders.{ "$match" : { "CustomerID" : "ALFKI" } }, { "$lookup" : { "from" : "OrderDetails", "localField" : "_id", "foreignField" : "_id.OrderID", "as" : "_lookup_OrderDetails" } }
""");
    }

    public override async Task Include_collection_with_outer_apply_with_filter_non_equality(bool async)
    {
        await base.Include_collection_with_outer_apply_with_filter_non_equality(async);

        AssertMql(
);
    }

    public override async Task Include_in_let_followed_by_FirstOrDefault(bool async)
    {
        await base.Include_in_let_followed_by_FirstOrDefault(async);

        AssertMql(
);
    }

    public override async Task Include_references_multi_level(bool async)
    {
        await base.Include_references_multi_level(async);

        AssertMql(
);
    }

    public override async Task Include_collection_then_include_collection(bool async)
    {
        await base.Include_collection_then_include_collection(async);

        AssertMql(
            """
Customers.{ "$match" : { "_id" : { "$regularExpression" : { "pattern" : "^F", "options" : "s" } } } }, { "$lookup" : { "from" : "Orders", "let" : { "localField" : "$_id" }, "pipeline" : [{ "$match" : { "$expr" : { "$eq" : ["$CustomerID", "$$localField"] } } }, { "$lookup" : { "from" : "OrderDetails", "localField" : "_id", "foreignField" : "_id.OrderID", "as" : "_lookup_OrderDetails" } }], "as" : "_lookup_Orders" } }
""");
    }

    public override async Task Include_collection_with_multiple_conditional_order_by(bool async)
    {
        await base.Include_collection_with_multiple_conditional_order_by(async);

        AssertMql(
            """
Orders.{ "$project" : { "_outer" : "$$ROOT", "_id" : 0 } }, { "$lookup" : { "from" : "Customers", "localField" : "_outer.CustomerID", "foreignField" : "_id", "as" : "_inner" } }, { "$unwind" : { "path" : "$_inner", "preserveNullAndEmptyArrays" : true } }, { "$project" : { "Outer" : "$_outer", "Inner" : "$_inner", "_id" : 0 } }, { "$project" : { "_id" : 0, "_document" : "$$ROOT", "_key1" : { "$gt" : ["$Outer._id", 0] }, "_key2" : { "$cond" : { "if" : { "$ne" : ["$Inner", null] }, "then" : "$Inner.City", "else" : "" } } } }, { "$sort" : { "_key1" : 1, "_key2" : 1 } }, { "$replaceRoot" : { "newRoot" : "$_document" } }, { "$limit" : 5 }, { "$lookup" : { "from" : "OrderDetails", "localField" : "_id", "foreignField" : "OrderID", "as" : "_lookup_OrderDetails" } }
""");
    }

    public override async Task Include_reference_when_entity_in_projection(bool async)
    {
        await base.Include_reference_when_entity_in_projection(async);
        AssertMql(
            """
Orders.{ "$match" : { "CustomerID" : { "$regularExpression" : { "pattern" : "^F", "options" : "s" } } } }, { "$project" : { "_outer" : "$$ROOT", "_id" : 0 } }, { "$lookup" : { "from" : "Customers", "localField" : "_outer.CustomerID", "foreignField" : "_id", "as" : "_inner" } }, { "$unwind" : { "path" : "$_inner", "preserveNullAndEmptyArrays" : true } }, { "$project" : { "_outer" : "$_outer", "_inner" : "$_inner", "_id" : 0 } }
""");
    }

    public override async Task Include_reference_single_or_default_when_no_result(bool async)
    {
        await base.Include_reference_single_or_default_when_no_result(async);
        AssertMql(
            """
Orders.{ "$match" : { "_id" : -1 } }, { "$project" : { "_outer" : "$$ROOT", "_id" : 0 } }, { "$lookup" : { "from" : "Customers", "localField" : "_outer.CustomerID", "foreignField" : "_id", "as" : "_inner" } }, { "$unwind" : { "path" : "$_inner", "preserveNullAndEmptyArrays" : true } }, { "$project" : { "_outer" : "$_outer", "_inner" : "$_inner", "_id" : 0 } }, { "$limit" : 2 }
""");
    }

    public override async Task Include_reference_alias_generation(bool async)
    {
        await base.Include_reference_alias_generation(async);

        AssertMql(
            """
OrderDetails.{ "$match" : { "_id.OrderID" : { "$mod" : [23, 13] } } }, { "$project" : { "_outer" : "$$ROOT", "_id" : 0 } }, { "$lookup" : { "from" : "Orders", "localField" : "_outer._id.OrderID", "foreignField" : "_id", "as" : "_inner" } }, { "$unwind" : "$_inner" }, { "$project" : { "_outer" : "$_outer", "_inner" : "$_inner", "_id" : 0 } }
""");
    }

    public override async Task Include_with_cycle_does_not_throw_when_AsNoTrackingWithIdentityResolution(bool async)
    {
        await base.Include_with_cycle_does_not_throw_when_AsNoTrackingWithIdentityResolution(async);
        AssertMql(
            """
Orders.{ "$match" : { "_id" : { "$lt" : 10800 } } }, { "$project" : { "_outer" : "$$ROOT", "_id" : 0 } }, { "$lookup" : { "from" : "Customers", "localField" : "_outer.CustomerID", "foreignField" : "_id", "as" : "_inner" } }, { "$unwind" : { "path" : "$_inner", "preserveNullAndEmptyArrays" : true } }, { "$project" : { "_outer" : "$_outer", "_inner" : "$_inner", "_id" : 0 } }, { "$lookup" : { "from" : "Orders", "localField" : "_inner._id", "foreignField" : "CustomerID", "as" : "_inner._lookup_Orders" } }
""");
    }

    public override async Task Include_references_then_include_collection_multi_level(bool async)
    {
        await base.Include_references_then_include_collection_multi_level(async);

        AssertMql(
);
    }

    public override async Task Include_reference_Join_GroupBy_Select(bool async)
    {
        await base.Include_reference_Join_GroupBy_Select(async);

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
        await base.Include_reference_SelectMany_GroupBy_Select(async);

        AssertMql(
);
    }

    public override async Task Include_multiple_references_then_include_collection_multi_level(bool async)
    {
        await base.Include_multiple_references_then_include_collection_multi_level(async);

        AssertMql(
);
    }

    public override async Task Outer_identifier_correctly_determined_when_doing_include_on_right_side_of_left_join(bool async)
    {
        await base.Outer_identifier_correctly_determined_when_doing_include_on_right_side_of_left_join(async);

        AssertMql(
            """
Customers.{ "$project" : { "_outer" : "$$ROOT", "_id" : 0 } }, { "$lookup" : { "from" : "Orders", "localField" : "_outer._id", "foreignField" : "CustomerID", "as" : "_inner" } }, { "$unwind" : { "path" : "$_inner", "preserveNullAndEmptyArrays" : true } }, { "$project" : { "_outer" : "$_outer", "_inner" : "$_inner", "_id" : 0 } }, { "$match" : { "_outer.City" : "Seattle" } }, { "$lookup" : { "from" : "OrderDetails", "localField" : "_inner._id", "foreignField" : "_id.OrderID", "as" : "_inner._lookup_OrderDetails" } }
""");
    }

    public override async Task SelectMany_Include_reference_GroupBy_Select(bool async)
    {
        await base.SelectMany_Include_reference_GroupBy_Select(async);

        AssertMql(
);
    }

    public override async Task Include_collection_SelectMany_GroupBy_Select(bool async)
    {
        await base.Include_collection_SelectMany_GroupBy_Select(async);

        AssertMql(
);
    }

    public override async Task Include_collection_OrderBy_list_contains(bool async)
    {
        await base.Include_collection_OrderBy_list_contains(async);
        AssertMql(
            """
Customers.{ "$match" : { "_id" : { "$regularExpression" : { "pattern" : "^A", "options" : "s" } } } }, { "$project" : { "_id" : 0, "_document" : "$$ROOT", "_key1" : { "$in" : ["$_id", ["ALFKI"]] } } }, { "$sort" : { "_key1" : 1 } }, { "$replaceRoot" : { "newRoot" : "$_document" } }, { "$skip" : 1 }, { "$lookup" : { "from" : "Orders", "localField" : "_id", "foreignField" : "CustomerID", "as" : "_lookup_Orders" } }
""");
    }

    public override async Task Multi_level_includes_are_applied_with_skip(bool async)
    {
        await base.Multi_level_includes_are_applied_with_skip(async);

        AssertMql(
);
    }

    public override async Task Include_collection_on_additional_from_clause(bool async)
    {
        await base.Include_collection_on_additional_from_clause(async);

        AssertMql(
);
    }

    public override async Task Include_reference_distinct_is_server_evaluated(bool async)
    {
        await base.Include_reference_distinct_is_server_evaluated(async);
        AssertMql(
            """
Orders.{ "$match" : { "_id" : { "$lt" : 10250 } } }, { "$group" : { "_id" : "$$ROOT" } }, { "$replaceRoot" : { "newRoot" : "$_id" } }, { "$project" : { "_outer" : "$$ROOT", "_id" : 0 } }, { "$lookup" : { "from" : "Customers", "localField" : "_outer.CustomerID", "foreignField" : "_id", "as" : "_inner" } }, { "$unwind" : { "path" : "$_inner", "preserveNullAndEmptyArrays" : true } }, { "$project" : { "_outer" : "$_outer", "_inner" : "$_inner", "_id" : 0 } }
""");
    }

    public override async Task Include_collection_distinct_is_server_evaluated(bool async)
    {
        await base.Include_collection_distinct_is_server_evaluated(async);
        AssertMql(
            """
Customers.{ "$match" : { "_id" : { "$regularExpression" : { "pattern" : "^A", "options" : "s" } } } }, { "$group" : { "_id" : "$$ROOT" } }, { "$replaceRoot" : { "newRoot" : "$_id" } }, { "$lookup" : { "from" : "Orders", "localField" : "_id", "foreignField" : "CustomerID", "as" : "_lookup_Orders" } }
""");
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
        await base.Include_duplicate_collection_result_operator(async);

        AssertMql(
);
    }

    public override async Task Include_reference(bool async)
    {
        await base.Include_reference(async);
        AssertMql(
            """
Orders.{ "$match" : { "CustomerID" : { "$regularExpression" : { "pattern" : "^F", "options" : "s" } } } }, { "$project" : { "_outer" : "$$ROOT", "_id" : 0 } }, { "$lookup" : { "from" : "Customers", "localField" : "_outer.CustomerID", "foreignField" : "_id", "as" : "_inner" } }, { "$unwind" : { "path" : "$_inner", "preserveNullAndEmptyArrays" : true } }, { "$project" : { "_outer" : "$_outer", "_inner" : "$_inner", "_id" : 0 } }
""");
    }

    public override async Task Include_multiple_references_and_collection_multi_level_reverse(bool async)
    {
        await base.Include_multiple_references_and_collection_multi_level_reverse(async);

        AssertMql(
);
    }

    public override async Task Include_closes_reader(bool async)
    {
        await base.Include_closes_reader(async);
        AssertMql(
            """
Customers.{ "$lookup" : { "from" : "Orders", "localField" : "_id", "foreignField" : "CustomerID", "as" : "_lookup_Orders" } }, { "$limit" : 1 }
""",
            //
            """
Products.
""");
    }

    public override async Task Include_with_skip(bool async)
    {
        await base.Include_with_skip(async);
        AssertMql(
            """
Customers.{ "$sort" : { "ContactName" : 1 } }, { "$skip" : 80 }, { "$lookup" : { "from" : "Orders", "localField" : "_id", "foreignField" : "CustomerID", "as" : "_lookup_Orders" } }
""");
    }

    public override async Task Include_collection_Join_GroupBy_Select(bool async)
    {
        await base.Include_collection_Join_GroupBy_Select(async);

        AssertMql(
);
    }

    public override async Task Include_collection_GroupBy_Select(bool async)
    {
        await base.Include_collection_GroupBy_Select(async);

        AssertMql(
);
    }

    public override async Task Include_collection_orderby_take(bool async)
    {
        await base.Include_collection_orderby_take(async);
        AssertMql(
            """
Customers.{ "$sort" : { "_id" : 1 } }, { "$limit" : 5 }, { "$lookup" : { "from" : "Orders", "localField" : "_id", "foreignField" : "CustomerID", "as" : "_lookup_Orders" } }
""");
    }

    public override async Task Join_Include_collection_GroupBy_Select(bool async)
    {
        await base.Join_Include_collection_GroupBy_Select(async);

        AssertMql(
);
    }

    public override async Task Include_collection_order_by_non_key(bool async)
    {
        await base.Include_collection_order_by_non_key(async);
        AssertMql(
            """
Customers.{ "$match" : { "_id" : { "$regularExpression" : { "pattern" : "^F", "options" : "s" } } } }, { "$sort" : { "PostalCode" : 1 } }, { "$lookup" : { "from" : "Orders", "localField" : "_id", "foreignField" : "CustomerID", "as" : "_lookup_Orders" } }
""");
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
        await base.Include_duplicate_reference2(async);

        AssertMql(
);
    }

    public override async Task Include_collection_and_reference(bool async)
    {
        await base.Include_collection_and_reference(async);

        AssertMql(
            """
Orders.{ "$match" : { "CustomerID" : { "$regularExpression" : { "pattern" : "^F", "options" : "s" } } } }, { "$project" : { "_outer" : "$$ROOT", "_id" : 0 } }, { "$lookup" : { "from" : "Customers", "localField" : "_outer.CustomerID", "foreignField" : "_id", "as" : "_inner" } }, { "$unwind" : { "path" : "$_inner", "preserveNullAndEmptyArrays" : true } }, { "$project" : { "_outer" : "$_outer", "_inner" : "$_inner", "_id" : 0 } }, { "$lookup" : { "from" : "OrderDetails", "localField" : "_outer._id", "foreignField" : "_id.OrderID", "as" : "_outer._lookup_OrderDetails" } }
""");
    }

    public override async Task Include_multiple_references_multi_level(bool async)
    {
        await base.Include_multiple_references_multi_level(async);

        AssertMql(
);
    }

    public override async Task Include_references_and_collection_multi_level_predicate(bool async)
    {
        await base.Include_references_and_collection_multi_level_predicate(async);

        AssertMql(
);
    }

    public override async Task SelectMany_Include_collection_GroupBy_Select(bool async)
    {
        await base.SelectMany_Include_collection_GroupBy_Select(async);

        AssertMql(
);
    }

    public override async Task Include_collection_with_last(bool async)
    {
        await base.Include_collection_with_last(async);
        AssertMql(
            """
Customers.{ "$sort" : { "CompanyName" : 1 } }, { "$lookup" : { "from" : "Orders", "localField" : "_id", "foreignField" : "CustomerID", "as" : "_lookup_Orders" } }, { "$group" : { "_id" : null, "_last" : { "$last" : "$$ROOT" } } }, { "$replaceRoot" : { "newRoot" : "$_last" } }
""");
    }

    public override async Task Include_collection_OrderBy_empty_list_does_not_contains(bool async)
    {
        await base.Include_collection_OrderBy_empty_list_does_not_contains(async);
        AssertMql(
            """
Customers.{ "$match" : { "_id" : { "$regularExpression" : { "pattern" : "^A", "options" : "s" } } } }, { "$project" : { "_id" : 0, "_document" : "$$ROOT", "_key1" : { "$not" : { "$in" : ["$_id", []] } } } }, { "$sort" : { "_key1" : 1 } }, { "$replaceRoot" : { "newRoot" : "$_document" } }, { "$skip" : 1 }, { "$lookup" : { "from" : "Orders", "localField" : "_id", "foreignField" : "CustomerID", "as" : "_lookup_Orders" } }
""");
    }

    public override async Task Include_multiple_references_then_include_multi_level_reverse(bool async)
    {
        await base.Include_multiple_references_then_include_multi_level_reverse(async);

        AssertMql(
);
    }

    public override async Task Include_reference_and_collection(bool async)
    {
        await base.Include_reference_and_collection(async);

        AssertMql(
);
    }

    public override async Task Include_is_not_ignored_when_projection_contains_client_method_and_complex_expression(bool async)
    {
        await base.Include_is_not_ignored_when_projection_contains_client_method_and_complex_expression(async);
        AssertMql(
            """
Employees.{ "$match" : { "$or" : [{ "_id" : 1 }, { "_id" : 2 }] } }, { "$sort" : { "_id" : 1 } }, { "$project" : { "_outer" : "$$ROOT", "_id" : 0 } }, { "$lookup" : { "from" : "Employees", "localField" : "_outer.ReportsTo", "foreignField" : "_id", "as" : "_inner" } }, { "$unwind" : { "path" : "$_inner", "preserveNullAndEmptyArrays" : true } }, { "$project" : { "_outer" : "$_outer", "_inner" : "$_inner", "_id" : 0 } }
""");
    }

    public override async Task Include_reference_with_filter_reordered(bool async)
    {
        await base.Include_reference_with_filter_reordered(async);
        AssertMql(
            """
Orders.{ "$match" : { "CustomerID" : "ALFKI" } }, { "$project" : { "_outer" : "$$ROOT", "_id" : 0 } }, { "$lookup" : { "from" : "Customers", "localField" : "_outer.CustomerID", "foreignField" : "_id", "as" : "_inner" } }, { "$unwind" : { "path" : "$_inner", "preserveNullAndEmptyArrays" : true } }, { "$project" : { "_outer" : "$_outer", "_inner" : "$_inner", "_id" : 0 } }
""");
    }

    public override async Task Include_collection_order_by_subquery(bool async)
    {
        await AssertUnsupportedCrossDbSetQuery(() => base.Include_collection_order_by_subquery(async));

        AssertMql(
);
    }

    public override async Task Include_reference_and_collection_order_by(bool async)
    {
        await base.Include_reference_and_collection_order_by(async);
        AssertMql(
            """
Orders.{ "$match" : { "CustomerID" : { "$regularExpression" : { "pattern" : "^F", "options" : "s" } } } }, { "$sort" : { "_id" : 1 } }, { "$project" : { "_outer" : "$$ROOT", "_id" : 0 } }, { "$lookup" : { "from" : "Customers", "localField" : "_outer.CustomerID", "foreignField" : "_id", "as" : "_inner" } }, { "$unwind" : { "path" : "$_inner", "preserveNullAndEmptyArrays" : true } }, { "$project" : { "_outer" : "$_outer", "_inner" : "$_inner", "_id" : 0 } }, { "$lookup" : { "from" : "Orders", "localField" : "_inner._id", "foreignField" : "CustomerID", "as" : "_inner._lookup_Orders" } }
""");
    }

    public override async Task Then_include_collection_order_by_collection_column(bool async)
    {
        await AssertUnsupportedCrossDbSetQuery(() => base.Then_include_collection_order_by_collection_column(async));

        AssertMql(
);
    }

    public override async Task Include_multiple_references_then_include_multi_level(bool async)
    {
        await base.Include_multiple_references_then_include_multi_level(async);

        AssertMql(
);
    }

    public override async Task Include_collection_skip_no_order_by(bool async)
    {
        await base.Include_collection_skip_no_order_by(async);
        AssertMql(
            """
Customers.{ "$skip" : 10 }, { "$lookup" : { "from" : "Orders", "localField" : "_id", "foreignField" : "CustomerID", "as" : "_lookup_Orders" } }
""");
    }

    public override async Task Include_multi_level_reference_then_include_collection_predicate(bool async)
    {
        await base.Include_multi_level_reference_then_include_collection_predicate(async);

        AssertMql(
            """
Orders.{ "$match" : { "_id" : 10248 } }, { "$project" : { "_outer" : "$$ROOT", "_id" : 0 } }, { "$lookup" : { "from" : "Customers", "localField" : "_outer.CustomerID", "foreignField" : "_id", "as" : "_inner" } }, { "$unwind" : { "path" : "$_inner", "preserveNullAndEmptyArrays" : true } }, { "$project" : { "_outer" : "$_outer", "_inner" : "$_inner", "_id" : 0 } }, { "$lookup" : { "from" : "Orders", "localField" : "_inner._id", "foreignField" : "CustomerID", "as" : "_inner._lookup_Orders" } }, { "$limit" : 2 }
""");
    }

    public override async Task Include_multiple_references_and_collection_multi_level(bool async)
    {
        await base.Include_multiple_references_and_collection_multi_level(async);

        AssertMql(
);
    }

    public override async Task Include_where_skip_take_projection(bool async)
    {
        await base.Include_where_skip_take_projection(async);
        AssertMql(
            """
OrderDetails.{ "$match" : { "Quantity" : 10 } }, { "$sort" : { "_id.OrderID" : 1, "_id.ProductID" : 1 } }, { "$skip" : 1 }, { "$limit" : 2 }, { "$project" : { "_outer" : "$$ROOT", "_id" : 0 } }, { "$lookup" : { "from" : "Orders", "localField" : "_outer._id.OrderID", "foreignField" : "_id", "as" : "_inner" } }, { "$unwind" : "$_inner" }, { "$project" : { "Outer" : "$_outer", "Inner" : "$_inner", "_id" : 0 } }, { "$project" : { "CustomerID" : "$Inner.CustomerID", "_id" : 0 } }
""");
    }

    public override async Task Include_with_take(bool async)
    {
        await base.Include_with_take(async);
        AssertMql(
            """
Customers.{ "$sort" : { "ContactName" : -1 } }, { "$limit" : 10 }, { "$lookup" : { "from" : "Orders", "localField" : "_id", "foreignField" : "CustomerID", "as" : "_lookup_Orders" } }
""");
    }

    public override async Task Include_multiple_references(bool async)
    {
        await base.Include_multiple_references(async);

        AssertMql(
);
    }

    public override async Task Include_list(bool async)
    {
        await base.Include_list(async);

        AssertMql(
            """
Products.{ "$match" : { "_id" : { "$mod" : [17, 5] }, "UnitPrice" : { "$lt" : { "$numberDecimal" : "20" } } } }, { "$lookup" : { "from" : "OrderDetails", "localField" : "_id", "foreignField" : "ProductID", "as" : "_lookup_OrderDetails" } }
""");
    }

    public override async Task Include_empty_reference_sets_IsLoaded(bool async)
    {
        await base.Include_empty_reference_sets_IsLoaded(async);

        AssertMql(
            """
Employees.{ "$project" : { "_outer" : "$$ROOT", "_id" : 0 } }, { "$lookup" : { "from" : "Employees", "localField" : "_outer.ReportsTo", "foreignField" : "_id", "as" : "_inner" } }, { "$unwind" : { "path" : "$_inner", "preserveNullAndEmptyArrays" : true } }, { "$project" : { "_outer" : "$_outer", "_inner" : "$_inner", "_id" : 0 } }, { "$match" : { "_inner" : null } }, { "$limit" : 1 }
""");
    }

    public override async Task Include_references_then_include_collection_multi_level_predicate(bool async)
    {
        await base.Include_references_then_include_collection_multi_level_predicate(async);

        AssertMql(
);
    }

    public override async Task Include_collection_with_conditional_order_by(bool async)
    {
        await base.Include_collection_with_conditional_order_by(async);
        AssertMql(
            """
Customers.{ "$match" : { "_id" : { "$regularExpression" : { "pattern" : "^F", "options" : "s" } } } }, { "$project" : { "_id" : 0, "_document" : "$$ROOT", "_key1" : { "$cond" : { "if" : { "$eq" : [{ "$indexOfCP" : ["$_id", "S"] }, 0] }, "then" : 1, "else" : 2 } } } }, { "$sort" : { "_key1" : 1 } }, { "$replaceRoot" : { "newRoot" : "$_document" } }, { "$lookup" : { "from" : "Orders", "localField" : "_id", "foreignField" : "CustomerID", "as" : "_lookup_Orders" } }
""");
    }

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
            """
Customers.{ "$match" : { "_id" : { "$regularExpression" : { "pattern" : "^F", "options" : "s" } } } }, { "$lookup" : { "from" : "Orders", "let" : { "localField" : "$_id" }, "pipeline" : [{ "$match" : { "$expr" : { "$eq" : ["$CustomerID", "$$localField"] } } }, { "$sort" : { "_id" : 1 } }, { "$skip" : 1 }, { "$sort" : { "OrderDate" : -1 } }], "as" : "_lookup_Orders" } }
""");
    }

    public override async Task Include_specified_on_non_entity_not_supported(bool async)
    {
        await base.Include_specified_on_non_entity_not_supported(async);

        AssertMql();
    }

    public override async Task Include_collection_with_client_filter(bool async)
    {
        await base.Include_collection_with_client_filter(async);

        AssertMql();
    }

    protected new static async Task AssertTranslationFailed(Func<Task> query)
    {
        try
        {
            await query();
        }
        catch
        {
            return;
        }

        throw new Xunit.Sdk.XunitException("Expected query to fail but it succeeded.");
    }

    private void AssertMql(params string[] expected)
        => Fixture.TestMqlLoggerFactory.AssertBaseline(expected);
}
