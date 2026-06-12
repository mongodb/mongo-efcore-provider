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

    public override async Task Select_Where_Navigation(bool async)
    {
#if EF8 || EF9
        // Fails: Cross-collection Include/join not translated on EF8/EF9 EF-X020
        await AssertTranslationFailed(() => base.Select_Where_Navigation(async));
        AssertMql();
#else
        await base.Select_Where_Navigation(async);
        AssertMql(
            """
Orders.{ "$project" : { "_outer" : "$$ROOT", "_id" : 0 } }, { "$lookup" : { "from" : "Customers", "localField" : "_outer.CustomerID", "foreignField" : "_id", "as" : "_inner" } }, { "$unwind" : { "path" : "$_inner", "preserveNullAndEmptyArrays" : true } }, { "$project" : { "_outer" : "$_outer", "_inner" : "$_inner", "_id" : 0 } }, { "$match" : { "_inner.City" : "Seattle" } }
""");
#endif
    }

    public override async Task Select_Where_Navigation_Contains(bool async)
    {
#if EF8 || EF9
        // Fails: Cross-collection Include/join not translated on EF8/EF9 EF-X020
        await AssertTranslationFailed(() => base.Select_Where_Navigation_Contains(async));
        AssertMql();
#else
        await base.Select_Where_Navigation_Contains(async);
        AssertMql(
            """
Orders.{ "$project" : { "_outer" : "$$ROOT", "_id" : 0 } }, { "$lookup" : { "from" : "Customers", "localField" : "_outer.CustomerID", "foreignField" : "_id", "as" : "_inner" } }, { "$unwind" : { "path" : "$_inner", "preserveNullAndEmptyArrays" : true } }, { "$project" : { "_outer" : "$_outer", "_inner" : "$_inner", "_id" : 0 } }, { "$match" : { "_inner.City" : { "$regularExpression" : { "pattern" : "Sea", "options" : "s" } } } }
""");
#endif
    }

    public override async Task Select_Where_Navigation_Deep(bool async)
    {
#if EF8 || EF9
        // Fails: Cross-document navigation access issue EF-216
        await AssertTranslationFailed(() => base.Select_Where_Navigation_Deep(async));

        AssertMql(
        );
#else
        Assert.Contains(
            "Expression not supported",
            (await Assert.ThrowsAsync<MongoDB.Driver.Linq.ExpressionNotSupportedException>(() =>
                base.Select_Where_Navigation_Deep(async))).Message);

        AssertMql(
            """
OrderDetails.
""");
#endif
    }

    public override async Task Take_Select_Navigation(bool async)
    {
        // Fails: Cross-document navigation access issue EF-216
        await AssertTranslationFailed(() => base.Take_Select_Navigation(async));

        AssertMql(
        );
    }

    public override async Task Select_collection_FirstOrDefault_project_single_column1(bool async)
    {
        // Fails: Cross-document navigation access issue EF-216
        await AssertTranslationFailed(() => base.Select_collection_FirstOrDefault_project_single_column1(async));

        AssertMql(
        );
    }

    public override async Task Select_collection_FirstOrDefault_project_single_column2(bool async)
    {
        // Fails: Cross-document navigation access issue EF-216
        await AssertTranslationFailed(() => base.Select_collection_FirstOrDefault_project_single_column2(async));

        AssertMql(
        );
    }

    public override async Task Select_collection_FirstOrDefault_project_anonymous_type(bool async)
    {
        // Fails: Cross-document navigation access issue EF-216
        await AssertTranslationFailed(() => base.Select_collection_FirstOrDefault_project_anonymous_type(async));

        AssertMql(
        );
    }

    public override async Task Select_collection_FirstOrDefault_project_anonymous_type_client_eval(bool async)
    {
        // Fails: Cross-document navigation access issue EF-216
        await AssertTranslationFailed(() => base.Select_collection_FirstOrDefault_project_anonymous_type_client_eval(async));

        AssertMql(
        );
    }

    public override async Task Select_collection_FirstOrDefault_project_entity(bool async)
    {
        // Fails: Cross-document navigation access issue EF-216
        await AssertTranslationFailed(() => base.Select_collection_FirstOrDefault_project_entity(async));

        AssertMql(
        );
    }

    public override async Task Skip_Select_Navigation(bool async)
    {
        // Fails: Cross-document navigation access issue EF-216
        await AssertTranslationFailed(() => base.Skip_Select_Navigation(async));

        AssertMql(
        );
    }

    public override async Task Select_Where_Navigation_Included(bool async)
    {
#if EF8 || EF9
        // Fails: Cross-collection Include/join not translated on EF8/EF9 EF-X020
        await AssertTranslationFailed(() => base.Select_Where_Navigation_Included(async));
        AssertMql();
#else
        await base.Select_Where_Navigation_Included(async);
        AssertMql(
            """
Orders.{ "$project" : { "_outer" : "$$ROOT", "_id" : 0 } }, { "$lookup" : { "from" : "Customers", "localField" : "_outer.CustomerID", "foreignField" : "_id", "as" : "_inner" } }, { "$unwind" : { "path" : "$_inner", "preserveNullAndEmptyArrays" : true } }, { "$project" : { "_outer" : "$_outer", "_inner" : "$_inner", "_id" : 0 } }, { "$match" : { "_inner.City" : "Seattle" } }
""");
#endif
    }

    [ConditionalTheory(Skip = "EF-216: multi-hop cross-collection navigation returns wrong data")]
    [MemberData(nameof(IsAsyncData))]
    // Fails: returns wrong data (multi-hop cross-collection navigation) EF-216
    public override async Task Include_with_multiple_optional_navigations(bool async)
        => await base.Include_with_multiple_optional_navigations(async);

    public override async Task Select_Navigation(bool async)
    {
#if EF8 || EF9
        // Fails: Cross-collection Include/join not translated on EF8/EF9 EF-X020
        await AssertTranslationFailed(() => base.Select_Navigation(async));
        AssertMql();
#else
        await base.Select_Navigation(async);
        AssertMql(
            """
Orders.{ "$project" : { "_outer" : "$$ROOT", "_id" : 0 } }, { "$lookup" : { "from" : "Customers", "localField" : "_outer.CustomerID", "foreignField" : "_id", "as" : "_inner" } }, { "$unwind" : { "path" : "$_inner", "preserveNullAndEmptyArrays" : true } }, { "$project" : { "_outer" : "$_outer", "_inner" : "$_inner", "_id" : 0 } }
""");
#endif
    }

    public override async Task Select_Navigations(bool async)
    {
#if EF8 || EF9
        // Fails: Cross-collection Include/join not translated on EF8/EF9 EF-X020
        await AssertTranslationFailed(() => base.Select_Navigations(async));
        AssertMql();
#else
        await base.Select_Navigations(async);
        AssertMql(
            """
Orders.{ "$project" : { "_outer" : "$$ROOT", "_id" : 0 } }, { "$lookup" : { "from" : "Customers", "localField" : "_outer.CustomerID", "foreignField" : "_id", "as" : "_inner" } }, { "$unwind" : { "path" : "$_inner", "preserveNullAndEmptyArrays" : true } }, { "$project" : { "_outer" : "$_outer", "_inner" : "$_inner", "_id" : 0 } }
""");
#endif
    }

    public override async Task Select_Where_Navigation_Multiple_Access(bool async)
    {
#if EF8 || EF9
        // Fails: Cross-collection Include/join not translated on EF8/EF9 EF-X020
        await AssertTranslationFailed(() => base.Select_Where_Navigation_Multiple_Access(async));
        AssertMql();
#else
        await base.Select_Where_Navigation_Multiple_Access(async);
        AssertMql(
            """
Orders.{ "$project" : { "_outer" : "$$ROOT", "_id" : 0 } }, { "$lookup" : { "from" : "Customers", "localField" : "_outer.CustomerID", "foreignField" : "_id", "as" : "_inner" } }, { "$unwind" : { "path" : "$_inner", "preserveNullAndEmptyArrays" : true } }, { "$project" : { "_outer" : "$_outer", "_inner" : "$_inner", "_id" : 0 } }, { "$match" : { "_inner.City" : "Seattle", "_inner.Phone" : { "$ne" : "555 555 5555" } } }
""");
#endif
    }

    public override async Task Select_Navigations_Where_Navigations(bool async)
    {
#if EF8 || EF9
        // Fails: Cross-collection Include/join not translated on EF8/EF9 EF-X020
        await AssertTranslationFailed(() => base.Select_Navigations_Where_Navigations(async));
        AssertMql();
#else
        await base.Select_Navigations_Where_Navigations(async);
        AssertMql(
            """
Orders.{ "$project" : { "_outer" : "$$ROOT", "_id" : 0 } }, { "$lookup" : { "from" : "Customers", "localField" : "_outer.CustomerID", "foreignField" : "_id", "as" : "_inner" } }, { "$unwind" : { "path" : "$_inner", "preserveNullAndEmptyArrays" : true } }, { "$project" : { "_outer" : "$_outer", "_inner" : "$_inner", "_id" : 0 } }, { "$match" : { "_inner.City" : "Seattle" } }, { "$match" : { "_inner.Phone" : { "$ne" : "555 555 5555" } } }
""");
#endif
    }

    public override async Task Select_Singleton_Navigation_With_Member_Access(bool async)
    {
#if EF8 || EF9
        // Fails: Cross-collection Include/join not translated on EF8/EF9 EF-X020
        await AssertTranslationFailed(() => base.Select_Singleton_Navigation_With_Member_Access(async));
        AssertMql();
#else
        await base.Select_Singleton_Navigation_With_Member_Access(async);

        AssertMql(
            """
Orders.{ "$project" : { "_outer" : "$$ROOT", "_id" : 0 } }, { "$lookup" : { "from" : "Customers", "localField" : "_outer.CustomerID", "foreignField" : "_id", "as" : "_inner" } }, { "$unwind" : { "path" : "$_inner", "preserveNullAndEmptyArrays" : true } }, { "$project" : { "_outer" : "$_outer", "_inner" : "$_inner", "_id" : 0 } }, { "$match" : { "_inner.City" : "Seattle" } }, { "$match" : { "_inner.Phone" : { "$ne" : "555 555 5555" } } }
""");
#endif
    }

    public override async Task Select_count_plus_sum(bool async)
    {
        // Fails: Cross-document navigation access issue EF-216
        await AssertTranslationFailed(() => base.Select_count_plus_sum(async));

        AssertMql(
        );
    }

    public override async Task Singleton_Navigation_With_Member_Access(bool async)
    {
#if EF8 || EF9
        // Fails: Cross-collection Include/join not translated on EF8/EF9 EF-X020
        await AssertTranslationFailed(() => base.Singleton_Navigation_With_Member_Access(async));
        AssertMql();
#else
        await base.Singleton_Navigation_With_Member_Access(async);
        AssertMql(
            """
Orders.{ "$project" : { "_outer" : "$$ROOT", "_id" : 0 } }, { "$lookup" : { "from" : "Customers", "localField" : "_outer.CustomerID", "foreignField" : "_id", "as" : "_inner" } }, { "$project" : { "_outer" : "$_outer", "_inner" : "$_inner", "_id" : 0 } }, { "$project" : { "_v" : { "$map" : { "input" : { "$cond" : { "if" : { "$eq" : [{ "$size" : "$_inner" }, 0] }, "then" : [null], "else" : "$_inner" } }, "as" : "i", "in" : { "_outer" : "$_outer", "_inner" : "$$i" } } }, "_id" : 0 } }, { "$unwind" : "$_v" }, { "$match" : { "_v._inner.City" : "Seattle" } }, { "$match" : { "_v._inner.Phone" : { "$ne" : "555 555 5555" } } }, { "$project" : { "B" : "$_v._inner.City", "_id" : 0 } }
""");
#endif
    }

    public override async Task Select_Where_Navigation_Scalar_Equals_Navigation_Scalar_Projected(bool async)
    {
        // Fails: Cross-document navigation access issue EF-216
        await AssertTranslationFailed(() => base.Select_Where_Navigation_Scalar_Equals_Navigation_Scalar_Projected(async));

        AssertMql(
        );
    }

    public override async Task Select_Where_Navigation_Equals_Navigation(bool async)
    {
        // Fails: Cross-document navigation access issue EF-216
        await AssertTranslationFailed(() => base.Select_Where_Navigation_Equals_Navigation(async));

        AssertMql(
        );
    }

    public override async Task Select_Where_Navigation_Null(bool async)
    {
#if EF8 || EF9
        // Fails: Cross-collection Include/join not translated on EF8/EF9 EF-X020
        await AssertTranslationFailed(() => base.Select_Where_Navigation_Null(async));
        AssertMql();
#else
        await base.Select_Where_Navigation_Null(async);
        AssertMql(
            """
Employees.{ "$project" : { "_outer" : "$$ROOT", "_id" : 0 } }, { "$lookup" : { "from" : "Employees", "localField" : "_outer.ReportsTo", "foreignField" : "_id", "as" : "_inner" } }, { "$unwind" : { "path" : "$_inner", "preserveNullAndEmptyArrays" : true } }, { "$project" : { "_outer" : "$_outer", "_inner" : "$_inner", "_id" : 0 } }, { "$match" : { "_inner" : null } }
""");
#endif
    }

    [ConditionalTheory(Skip = "EF-216: multi-hop cross-collection navigation returns wrong data")]
    [MemberData(nameof(IsAsyncData))]
    // Fails: returns wrong data (multi-hop cross-collection navigation) EF-216
    public override async Task Select_Where_Navigation_Null_Deep(bool async)
        => await base.Select_Where_Navigation_Null_Deep(async);

    public override async Task Select_Where_Navigation_Null_Reverse(bool async)
    {
#if EF8 || EF9
        // Fails: Cross-collection Include/join not translated on EF8/EF9 EF-X020
        await AssertTranslationFailed(() => base.Select_Where_Navigation_Null_Reverse(async));
        AssertMql();
#else
        await base.Select_Where_Navigation_Null_Reverse(async);
        AssertMql(
            """
Employees.{ "$project" : { "_outer" : "$$ROOT", "_id" : 0 } }, { "$lookup" : { "from" : "Employees", "localField" : "_outer.ReportsTo", "foreignField" : "_id", "as" : "_inner" } }, { "$unwind" : { "path" : "$_inner", "preserveNullAndEmptyArrays" : true } }, { "$project" : { "_outer" : "$_outer", "_inner" : "$_inner", "_id" : 0 } }, { "$match" : { "_inner" : null } }
""");
#endif
    }

    public override async Task Select_collection_navigation_simple(bool async)
    {
        // Fails: Cross-document navigation access issue EF-216
        await AssertTranslationFailed(() => base.Select_collection_navigation_simple(async));

        AssertMql(
        );
    }

    public override async Task Select_collection_navigation_simple2(bool async)
    {
        await base.Select_collection_navigation_simple2(async);

        AssertMql(
            """
Customers.{ "$lookup" : { "from" : "Orders", "localField" : "_id", "foreignField" : "CustomerID", "as" : "_lookup_Orders" } }, { "$match" : { "_id" : { "$regularExpression" : { "pattern" : "^A", "options" : "s" } } } }, { "$sort" : { "_id" : 1 } }, { "$project" : { "CustomerID" : "$_id", "Count" : { "$size" : "$_lookup_Orders" }, "_id" : 0 } }
""");
    }

    public override async Task Select_collection_navigation_simple_followed_by_ordering_by_scalar(bool async)
    {
        // Fails: Cross-document navigation access issue EF-216
        await AssertTranslationFailed(() => base.Select_collection_navigation_simple_followed_by_ordering_by_scalar(async));

        AssertMql(
        );
    }

    public override async Task Select_collection_navigation_multi_part(bool async)
    {
        // Fails: Cross-document navigation access issue EF-216
        await AssertTranslationFailed(() => base.Select_collection_navigation_multi_part(async));

        AssertMql(
        );
    }

    public override async Task Select_collection_navigation_multi_part2(bool async)
    {
        // Fails: Cross-document navigation access issue EF-216
        await AssertTranslationFailed(() => base.Select_collection_navigation_multi_part2(async));

        AssertMql(
        );
    }

    public override async Task Collection_select_nav_prop_any(bool async)
    {
        // Fails: Cross-document navigation access issue EF-216
        await AssertTranslationFailed(() => base.Collection_select_nav_prop_any(async));

        AssertMql(
        );
    }

    public override async Task Collection_select_nav_prop_predicate(bool async)
    {
#if EF8
        await base.Collection_select_nav_prop_predicate(async);
        AssertMql(
            """
Customers.{ "$lookup" : { "from" : "Orders", "localField" : "_id", "foreignField" : "CustomerID", "as" : "_lookup_Orders" } }, { "$project" : { "_v" : { "$gt" : [{ "$size" : "$_lookup_Orders" }, 0] }, "_id" : 0 } }
""");
#else
        // Fails: Cross-document navigation access issue EF-216
        await AssertTranslationFailed(() => base.Collection_select_nav_prop_predicate(async));

        AssertMql(
        );
#endif
    }

    public override async Task Collection_where_nav_prop_any(bool async)
    {
        await AssertNoMultiCollectionQuerySupport(() => base.Collection_where_nav_prop_any(async));
    }

    public override async Task Collection_where_nav_prop_any_predicate(bool async)
    {
        await AssertNoMultiCollectionQuerySupport(() => base.Collection_where_nav_prop_any_predicate(async));
    }

    public override async Task Collection_select_nav_prop_all(bool async)
    {
        // Fails: Cross-document navigation access issue EF-216
        await AssertTranslationFailed(() => base.Collection_select_nav_prop_all(async));
    }

    public override async Task Collection_where_nav_prop_all(bool async)
    {
        await AssertNoMultiCollectionQuerySupport(() => base.Collection_where_nav_prop_all(async));
    }

    public override async Task Collection_select_nav_prop_count(bool async)
    {
        await base.Collection_select_nav_prop_count(async);

        AssertMql(
            """
Customers.{ "$lookup" : { "from" : "Orders", "localField" : "_id", "foreignField" : "CustomerID", "as" : "_lookup_Orders" } }, { "$project" : { "Count" : { "$size" : "$_lookup_Orders" }, "_id" : 0 } }
""");
    }

    public override async Task Collection_where_nav_prop_count(bool async)
    {
        await AssertNoMultiCollectionQuerySupport(() => base.Collection_where_nav_prop_count(async));
    }

    public override async Task Collection_where_nav_prop_count_reverse(bool async)
    {
        await AssertNoMultiCollectionQuerySupport(() => base.Collection_where_nav_prop_count_reverse(async));
    }

    public override async Task Collection_orderby_nav_prop_count(bool async)
    {
        await AssertNoMultiCollectionQuerySupport(() => base.Collection_orderby_nav_prop_count(async));
    }

    public override async Task Collection_select_nav_prop_long_count(bool async)
    {
        await base.Collection_select_nav_prop_long_count(async);

        AssertMql(
            """
Customers.{ "$lookup" : { "from" : "Orders", "localField" : "_id", "foreignField" : "CustomerID", "as" : "_lookup_Orders" } }, { "$project" : { "C" : { "$size" : "$_lookup_Orders" }, "_id" : 0 } }
""");
    }

    public override async Task Select_multiple_complex_projections(bool async)
    {
        // Fails: Cross-document navigation access issue EF-216
        await AssertTranslationFailed(() => base.Select_multiple_complex_projections(async));

        AssertMql(
        );
    }

    public override async Task Collection_select_nav_prop_sum(bool async)
    {
        // Fails: Cross-document navigation access issue EF-216
        await AssertTranslationFailed(() => base.Collection_select_nav_prop_sum(async));

        AssertMql(
        );
    }

    public override async Task Collection_select_nav_prop_sum_plus_one(bool async)
    {
        // Fails: Cross-document navigation access issue EF-216
        await AssertTranslationFailed(() => base.Collection_select_nav_prop_sum_plus_one(async));

        AssertMql(
        );
    }

    public override async Task Collection_where_nav_prop_sum(bool async)
    {
        await AssertNoMultiCollectionQuerySupport(() => base.Collection_where_nav_prop_sum(async));
    }

    public override async Task Collection_select_nav_prop_first_or_default(bool async)
    {
        // Fails: Cross-document navigation access issue EF-216
        await AssertTranslationFailed(() => base.Collection_select_nav_prop_first_or_default(async));

        AssertMql(
        );
    }

    public override async Task Collection_select_nav_prop_first_or_default_then_nav_prop(bool async)
    {
        // Fails: Cross-document navigation access issue EF-216
        await AssertTranslationFailed(() => base.Collection_select_nav_prop_first_or_default_then_nav_prop(async));

        AssertMql(
        );
    }

    public override async Task Collection_select_nav_prop_first_or_default_then_nav_prop_nested(bool async)
    {
        // Fails: Cross-document navigation access issue EF-216
        await AssertTranslationFailed(() => base.Collection_select_nav_prop_first_or_default_then_nav_prop_nested(async));

        AssertMql(
        );
    }

    public override async Task Collection_select_nav_prop_single_or_default_then_nav_prop_nested(bool async)
    {
        // Fails: Cross-document navigation access issue EF-216
        await AssertTranslationFailed(() => base.Collection_select_nav_prop_single_or_default_then_nav_prop_nested(async));

        AssertMql(
        );
    }

    public override async Task Collection_select_nav_prop_first_or_default_then_nav_prop_nested_using_property_method(bool async)
    {
        // Fails: Cross-document navigation access issue EF-216
        await AssertTranslationFailed(() =>
            base.Collection_select_nav_prop_first_or_default_then_nav_prop_nested_using_property_method(async));

        AssertMql(
        );
    }

    public override async Task Collection_select_nav_prop_first_or_default_then_nav_prop_nested_with_orderby(bool async)
    {
        // Fails: Cross-document navigation access issue EF-216
        await AssertTranslationFailed(() =>
            base.Collection_select_nav_prop_first_or_default_then_nav_prop_nested_with_orderby(async));

        AssertMql(
        );
    }

    public override async Task Navigation_fk_based_inside_contains(bool async)
    {
#if EF8 || EF9
        // Fails: Cross-collection Include/join not translated on EF8/EF9 EF-X020
        await AssertTranslationFailed(() => base.Navigation_fk_based_inside_contains(async));
        AssertMql();
#else
        // Failed: Throws ExpressionNotSupportedException (query not translated)
        await base.Navigation_fk_based_inside_contains(async);

        AssertMql(
            """
Orders.{ "$project" : { "_outer" : "$$ROOT", "_id" : 0 } }, { "$lookup" : { "from" : "Customers", "localField" : "_outer.CustomerID", "foreignField" : "_id", "as" : "_inner" } }, { "$unwind" : { "path" : "$_inner", "preserveNullAndEmptyArrays" : true } }, { "$project" : { "_outer" : "$_outer", "_inner" : "$_inner", "_id" : 0 } }, { "$match" : { "_inner._id" : { "$in" : ["ALFKI"] } } }
""");
#endif
    }

    public override async Task Navigation_inside_contains(bool async)
    {
#if EF8 || EF9
        // Fails: Cross-collection Include/join not translated on EF8/EF9 EF-X020
        await AssertTranslationFailed(() => base.Navigation_inside_contains(async));
        AssertMql();
#else
        await base.Navigation_inside_contains(async);
        AssertMql(
            """
Orders.{ "$project" : { "_outer" : "$$ROOT", "_id" : 0 } }, { "$lookup" : { "from" : "Customers", "localField" : "_outer.CustomerID", "foreignField" : "_id", "as" : "_inner" } }, { "$unwind" : { "path" : "$_inner", "preserveNullAndEmptyArrays" : true } }, { "$project" : { "_outer" : "$_outer", "_inner" : "$_inner", "_id" : 0 } }, { "$match" : { "_inner.City" : { "$in" : ["Novigrad", "Seattle"] } } }
""");
#endif
    }

    [ConditionalTheory(Skip = "EF-216: multi-hop cross-collection navigation returns wrong data")]
    [MemberData(nameof(IsAsyncData))]
    // Fails: returns wrong data (multi-hop cross-collection navigation) EF-216
    public override async Task Navigation_inside_contains_nested(bool async)
        => await base.Navigation_inside_contains_nested(async);

    [ConditionalTheory(Skip = "EF-216: multi-hop cross-collection navigation returns wrong data")]
    [MemberData(nameof(IsAsyncData))]
    // Fails: returns wrong data (multi-hop cross-collection navigation) EF-216
    public override async Task Navigation_from_join_clause_inside_contains(bool async)
        => await base.Navigation_from_join_clause_inside_contains(async);

    public override async Task Where_subquery_on_navigation(bool async)
    {
        // Fails: Does not throw expected unable to translate exception EF-X002
        await AssertNoMultiCollectionQuerySupport(() => AssertQuery(
            async,
            ss => from p in ss.Set<Product>()
                where p.OrderDetails.Contains(
                    ss.Set<OrderDetail>().OrderByDescending(o => o.OrderID).ThenBy(o => o.ProductID)
                        .FirstOrDefault(orderDetail => orderDetail.Quantity == 1))
                select p));
    }

    public override async Task Where_subquery_on_navigation2(bool async)
    {
        // Fails: Does not throw expected unable to translate exception EF-X002
        await AssertNoMultiCollectionQuerySupport(() => AssertQuery(
            async,
            ss => from p in ss.Set<Product>()
                where p.OrderDetails.Contains(
                    ss.Set<OrderDetail>().OrderByDescending(o => o.OrderID).ThenBy(o => o.ProductID).FirstOrDefault())
                select p));
    }

    public override async Task Navigation_in_subquery_referencing_outer_query(bool async)
    {
#if EF8 || EF9
        // Fails: Cross-document navigation access issue EF-216
        await AssertTranslationFailed(() => base.Navigation_in_subquery_referencing_outer_query(async));

        AssertMql(
        );
#else
        Assert.Contains(
            "is not defined for type",
            (await Assert.ThrowsAsync<ArgumentException>(() =>
                base.Navigation_in_subquery_referencing_outer_query(async))).Message);

        AssertMql(
        );
#endif
    }

    public override async Task Project_single_scalar_value_subquery_is_properly_inlined(bool async)
    {
        // Fails: Cross-document navigation access issue EF-216
        await AssertTranslationFailed(() => base.Project_single_scalar_value_subquery_is_properly_inlined(async));

        AssertMql(
        );
    }

    public override async Task Project_single_entity_value_subquery_works(bool async)
    {
        // Fails: Cross-document navigation access issue EF-216
        await AssertTranslationFailed(() => base.Project_single_entity_value_subquery_works(async));

        AssertMql(
        );
    }

    public override async Task Project_single_scalar_value_subquery_in_query_with_optional_navigation_works(bool async)
    {
        // Fails: Cross-document navigation access issue EF-216
        await AssertTranslationFailed(() =>
            base.Project_single_scalar_value_subquery_in_query_with_optional_navigation_works(async));

        AssertMql(
        );
    }

    public override async Task GroupJoin_with_complex_subquery_and_LOJ_gets_flattened(bool async)
    {
#if EF8 || EF9
        // Fails: Cross-document navigation access issue EF-216
        await AssertTranslationFailed(() => base.GroupJoin_with_complex_subquery_and_LOJ_gets_flattened(async));

        AssertMql(
        );
#else
        Assert.Contains(
            "Unsupported cross-DbSet query",
            (await Assert.ThrowsAsync<InvalidOperationException>(() =>
                base.GroupJoin_with_complex_subquery_and_LOJ_gets_flattened(async))).Message);

        AssertMql(
        );
#endif
    }

    public override async Task GroupJoin_with_complex_subquery_and_LOJ_gets_flattened2(bool async)
    {
#if EF8 || EF9
        // Fails: Cross-document navigation access issue EF-216
        await AssertTranslationFailed(() => base.GroupJoin_with_complex_subquery_and_LOJ_gets_flattened2(async));

        AssertMql(
        );
#else
        Assert.Contains(
            "Unsupported cross-DbSet query",
            (await Assert.ThrowsAsync<InvalidOperationException>(() =>
                base.GroupJoin_with_complex_subquery_and_LOJ_gets_flattened2(async))).Message);

        AssertMql(
        );
#endif
    }

    public override async Task Navigation_with_collection_with_nullable_type_key(bool async)
    {
#if EF8 || EF9
        // Fails: Cross-document navigation access issue EF-216
        await AssertTranslationFailed(() => base.Navigation_with_collection_with_nullable_type_key(async));

        AssertMql(
        );
#else
        Assert.Contains(
            "Expression not supported",
            (await Assert.ThrowsAsync<MongoDB.Driver.Linq.ExpressionNotSupportedException>(() =>
                base.Navigation_with_collection_with_nullable_type_key(async))).Message);

        AssertMql(
            """
Orders.
""");
#endif
    }

    [ConditionalTheory(Skip = "EF-216: multi-hop cross-collection navigation returns wrong data")]
    [MemberData(nameof(IsAsyncData))]
    // Fails: returns wrong data (multi-hop cross-collection navigation) EF-216
    public override async Task Multiple_include_with_multiple_optional_navigations(bool async)
        => await base.Multiple_include_with_multiple_optional_navigations(async);

    public override async Task Navigation_in_subquery_referencing_outer_query_with_client_side_result_operator_and_count(bool async)
    {
#if EF8 || EF9
        // Fails: Cross-document navigation access issue EF-216
        await AssertTranslationFailed(() =>
            base.Navigation_in_subquery_referencing_outer_query_with_client_side_result_operator_and_count(async));

        AssertMql(
        );
#else
        Assert.Contains(
            "is not defined for type",
            (await Assert.ThrowsAsync<ArgumentException>(() =>
                base.Navigation_in_subquery_referencing_outer_query_with_client_side_result_operator_and_count(async))).Message);

        AssertMql(
        );
#endif
    }

    public override async Task Select_Where_Navigation_Scalar_Equals_Navigation_Scalar(bool async)
    {
        // Fails: Cross-document navigation access issue EF-216
        await AssertTranslationFailed(() => base.Select_Where_Navigation_Scalar_Equals_Navigation_Scalar(async));

        AssertMql(
        );
    }

    public override async Task Where_subquery_on_navigation_client_eval(bool async)
    {
        await AssertNoMultiCollectionQuerySupport(() => base.Where_subquery_on_navigation_client_eval(async));
    }

    public override async Task Join_with_nav_projected_in_subquery_when_client_eval(bool async)
    {
#if EF8 || EF9
        await base.Join_with_nav_projected_in_subquery_when_client_eval(async);

        AssertMql();
#else
        await Assert.ThrowsAnyAsync<Exception>(() => base.Join_with_nav_projected_in_subquery_when_client_eval(async));

        AssertMql(
        );
#endif
    }

    public override async Task Join_with_nav_in_predicate_in_subquery_when_client_eval(bool async)
    {
#if EF8 || EF9
        await base.Join_with_nav_in_predicate_in_subquery_when_client_eval(async);

        AssertMql();
#else
        await Assert.ThrowsAnyAsync<Exception>(() => base.Join_with_nav_in_predicate_in_subquery_when_client_eval(async));

        AssertMql(
        );
#endif
    }

    public override async Task Join_with_nav_in_orderby_in_subquery_when_client_eval(bool async)
    {
#if EF8 || EF9
        await base.Join_with_nav_in_orderby_in_subquery_when_client_eval(async);

        AssertMql();
#else
        await Assert.ThrowsAnyAsync<Exception>(() => base.Join_with_nav_in_orderby_in_subquery_when_client_eval(async));

        AssertMql(
        );
#endif
    }

    public override async Task Select_Where_Navigation_Client(bool async)
    {
#if EF8 || EF9
        // Fails: Not throwing expected translation failed exception from EF. EF-X002
        Assert.Contains(
            "The LINQ expression",
            (await Assert.ThrowsAsync<ContainsException>(() => base.Select_Where_Navigation_Client(async))).Message);

        AssertMql();
#else
        var exception = await Assert.ThrowsAnyAsync<Exception>(() => base.Select_Where_Navigation_Client(async));

        AssertMql(
            """
Orders.
""");
#endif
    }

    public override async Task Collection_select_nav_prop_all_client(bool async)
    {
        // Fails: Not throwing expected translation failed exception from EF. EF-X002
        Assert.Contains(
            "The LINQ expression",
            (await Assert.ThrowsAsync<ContainsException>(() => base.Collection_select_nav_prop_all_client(async))).Message);

        AssertMql();
    }

    public override async Task Collection_where_nav_prop_all_client(bool async)
    {
        await AssertNoMultiCollectionQuerySupport(() => AssertQuery(
            async,
            ss => from c in ss.Set<Customer>()
                orderby c.CustomerID
                where c.Orders.All(o => o.ShipCity == "London")
                select c));
    }

    private void AssertMql(params string[] expected)
        => Fixture.TestMqlLoggerFactory.AssertBaseline(expected);

    protected override void ClearLog()
        => Fixture.TestMqlLoggerFactory.Clear();

    // Fails: Cross-document navigation access issue EF-216
    private static async Task AssertNoMultiCollectionQuerySupport(Func<Task> query)
        => Assert.Contains("Unsupported cross-DbSet query between",
            (await Assert.ThrowsAsync<InvalidOperationException>(query)).Message);
}
