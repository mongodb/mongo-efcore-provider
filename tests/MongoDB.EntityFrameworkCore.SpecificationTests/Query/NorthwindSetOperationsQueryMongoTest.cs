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
using Microsoft.EntityFrameworkCore.TestModels.Northwind;
using Microsoft.EntityFrameworkCore.TestUtilities;
using MongoDB.Driver.Linq;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace MongoDB.EntityFrameworkCore.SpecificationTests.Query;

public class NorthwindSetOperationsQueryMongoTest : NorthwindSetOperationsQueryTestBase<
    NorthwindQueryMongoFixture<NoopModelCustomizer>>
{
    public NorthwindSetOperationsQueryMongoTest(
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

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Union_Intersect(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Intersect_non_entity(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Intersect_nested(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Intersect(bool _)
        => Task.CompletedTask;


    public override async Task Union(bool async)
    {
        await base.Union(async);

        AssertMql(
            """
            Customers.{ "$match" : { "City" : "Berlin" } }, { "$unionWith" : { "coll" : "Customers", "pipeline" : [{ "$match" : { "City" : "London" } }] } }, { "$group" : { "_id" : "$$ROOT" } }, { "$replaceRoot" : { "newRoot" : "$_id" } }
            """);
    }

    public override async Task Concat(bool async)
    {
        await base.Concat(async);

        AssertMql(
            """
            Customers.{ "$match" : { "City" : "Berlin" } }, { "$unionWith" : { "coll" : "Customers", "pipeline" : [{ "$match" : { "City" : "London" } }] } }
            """);
    }

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Except(bool _)
        => Task.CompletedTask;

    public override async Task Union_OrderBy_Skip_Take(bool async)
    {
        await base.Union_OrderBy_Skip_Take(async);

        AssertMql(
            """
            Customers.{ "$match" : { "City" : "Berlin" } }, { "$unionWith" : { "coll" : "Customers", "pipeline" : [{ "$match" : { "City" : "London" } }] } }, { "$group" : { "_id" : "$$ROOT" } }, { "$replaceRoot" : { "newRoot" : "$_id" } }, { "$sort" : { "ContactName" : 1 } }, { "$skip" : 1 }, { "$limit" : 1 }
            """);
    }

    public override async Task Union_Where(bool async)
    {
        await base.Union_Where(async);

        AssertMql(
            """
            Customers.{ "$match" : { "City" : "Berlin" } }, { "$unionWith" : { "coll" : "Customers", "pipeline" : [{ "$match" : { "City" : "London" } }] } }, { "$group" : { "_id" : "$$ROOT" } }, { "$replaceRoot" : { "newRoot" : "$_id" } }, { "$match" : { "ContactName" : { "$regularExpression" : { "pattern" : "Thomas", "options" : "s" } } } }
            """);
    }

    public override async Task Union_Skip_Take_OrderBy_ThenBy_Where(bool async)
    {
        await base.Union_Skip_Take_OrderBy_ThenBy_Where(async);

        AssertMql(
            """
            Customers.{ "$match" : { "City" : "Berlin" } }, { "$unionWith" : { "coll" : "Customers", "pipeline" : [{ "$match" : { "City" : "London" } }] } }, { "$group" : { "_id" : "$$ROOT" } }, { "$replaceRoot" : { "newRoot" : "$_id" } }, { "$sort" : { "Region" : 1, "City" : 1 } }, { "$skip" : 0 }, { "$match" : { "ContactName" : { "$regularExpression" : { "pattern" : "Thomas", "options" : "s" } } } }
            """);
    }

    public override async Task Union_Union(bool async)
    {
        await base.Union_Union(async);

        AssertMql(
            """
            Customers.{ "$match" : { "City" : "Berlin" } }, { "$unionWith" : { "coll" : "Customers", "pipeline" : [{ "$match" : { "City" : "London" } }] } }, { "$group" : { "_id" : "$$ROOT" } }, { "$replaceRoot" : { "newRoot" : "$_id" } }, { "$unionWith" : { "coll" : "Customers", "pipeline" : [{ "$match" : { "City" : "Mannheim" } }] } }, { "$group" : { "_id" : "$$ROOT" } }, { "$replaceRoot" : { "newRoot" : "$_id" } }
            """);
    }


    public override async Task Union_inside_Concat(bool async)
    {
        await base.Union_inside_Concat(async);

        AssertMql(
            """
            Customers.{ "$match" : { "City" : "Berlin" } }, { "$unionWith" : { "coll" : "Customers", "pipeline" : [{ "$match" : { "City" : "London" } }, { "$unionWith" : { "coll" : "Customers", "pipeline" : [{ "$match" : { "City" : "Berlin" } }] } }, { "$group" : { "_id" : "$$ROOT" } }, { "$replaceRoot" : { "newRoot" : "$_id" } }] } }
            """);
    }

    public override async Task Union_Take_Union_Take(bool async)
    {
        // Copied because the upstream test has a bug--see https://github.com/dotnet/efcore/issues/36488
        await AssertQuery(
            async,
            ss => ss.Set<Customer>()
                .Where(c => c.City == "Berlin")
                .Union(ss.Set<Customer>().Where(c => c.City == "London"))
                .OrderBy(c => c.CustomerID)
                .Take(1)
                .Union(ss.Set<Customer>().Where(c => c.City == "Mannheim"))
                .OrderBy(o => o.CustomerID)
                .Take(1)
                .OrderBy(c => c.CustomerID),
            assertOrder: true);

        AssertMql(
            """
            Customers.{ "$match" : { "City" : "Berlin" } }, { "$unionWith" : { "coll" : "Customers", "pipeline" : [{ "$match" : { "City" : "London" } }] } }, { "$group" : { "_id" : "$$ROOT" } }, { "$replaceRoot" : { "newRoot" : "$_id" } }, { "$sort" : { "_id" : 1 } }, { "$limit" : 1 }, { "$unionWith" : { "coll" : "Customers", "pipeline" : [{ "$match" : { "City" : "Mannheim" } }] } }, { "$group" : { "_id" : "$$ROOT" } }, { "$replaceRoot" : { "newRoot" : "$_id" } }, { "$sort" : { "_id" : 1 } }, { "$limit" : 1 }, { "$sort" : { "_id" : 1 } }
            """);
    }

    public override async Task Select_Union(bool async)
    {
        await base.Select_Union(async);

        AssertMql(
            """
            Customers.{ "$match" : { "City" : "Berlin" } }, { "$project" : { "_v" : "$Address", "_id" : 0 } }, { "$unionWith" : { "coll" : "Customers", "pipeline" : [{ "$match" : { "City" : "London" } }, { "$project" : { "_v" : "$Address", "_id" : 0 } }] } }, { "$group" : { "_id" : "$$ROOT" } }, { "$replaceRoot" : { "newRoot" : "$_id" } }
            """);
    }

    public override async Task Union_Select(bool async)
    {
        await base.Union_Select(async);

        AssertMql(
            """
            Customers.{ "$match" : { "City" : "Berlin" } }, { "$unionWith" : { "coll" : "Customers", "pipeline" : [{ "$match" : { "City" : "London" } }] } }, { "$group" : { "_id" : "$$ROOT" } }, { "$replaceRoot" : { "newRoot" : "$_id" } }, { "$match" : { "Address" : { "$regularExpression" : { "pattern" : "Hanover", "options" : "s" } } } }, { "$project" : { "_v" : "$Address", "_id" : 0 } }
            """);
    }

    public override async Task Union_Select_scalar(bool async)
    {
        await base.Union_Select_scalar(async);

        AssertMql(
            """
            Customers.{ "$unionWith" : "Customers" }, { "$group" : { "_id" : "$$ROOT" } }, { "$replaceRoot" : { "newRoot" : "$_id" } }, { "$project" : { "_v" : { "$literal" : 1 }, "_id" : 0 } }
            """);
    }

    public override async Task Union_with_anonymous_type_projection(bool async)
    {
        await base.Union_with_anonymous_type_projection(async);

        AssertMql(
            """
            Customers.{ "$match" : { "CompanyName" : { "$regularExpression" : { "pattern" : "^A", "options" : "s" } } } }, { "$unionWith" : { "coll" : "Customers", "pipeline" : [{ "$match" : { "CompanyName" : { "$regularExpression" : { "pattern" : "^B", "options" : "s" } } } }] } }, { "$group" : { "_id" : "$$ROOT" } }, { "$replaceRoot" : { "newRoot" : "$_id" } }, { "$project" : { "_id" : "$_id" } }
            """);
    }

    [ConditionalTheory(Skip = "No multi-collection query support"), MemberData(nameof(IsAsyncData))]
    public override Task Select_Union_unrelated(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Select_Union_different_fields_in_anonymous_with_subquery(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Union_Include(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Include_Union(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Select_Except_reference_projection(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task SubSelect_Union(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task GroupBy_Select_Union(bool _)
        => Task.CompletedTask;

    public override async Task Union_over_columns_with_different_nullability(bool async)
    {
        await base.Union_over_columns_with_different_nullability(async);

        AssertMql(
            """
            Customers.{ "$project" : { "_v" : "NonNullableConstant", "_id" : 0 } }, { "$unionWith" : { "coll" : "Customers", "pipeline" : [{ "$project" : { "_v" : null, "_id" : 0 } }] } }
            """);
    }

    public override async Task Union_over_column_column(bool async)
    {
        await base.Union_over_column_column(async);

        AssertMql(
            """
            Orders.{ "$project" : { "_v" : "$_id", "_id" : 0 } }, { "$unionWith" : { "coll" : "Orders", "pipeline" : [{ "$project" : { "_v" : "$_id", "_id" : 0 } }] } }, { "$group" : { "_id" : "$$ROOT" } }, { "$replaceRoot" : { "newRoot" : "$_id" } }
            """);
    }

    public override async Task Union_over_column_function(bool async)
    {
        await base.Union_over_column_function(async);

        AssertMql(
            """
            Orders.{ "$project" : { "_v" : "$_id", "_id" : 0 } }, { "$unionWith" : { "coll" : "Orders", "pipeline" : [{ "$group" : { "_id" : "$_id", "_elements" : { "$push" : "$$ROOT" } } }, { "$project" : { "_v" : { "$size" : "$_elements" }, "_id" : 0 } }] } }, { "$group" : { "_id" : "$$ROOT" } }, { "$replaceRoot" : { "newRoot" : "$_id" } }
            """);
    }

    public override async Task Union_over_column_constant(bool async)
    {
        await base.Union_over_column_constant(async);

        AssertMql(
            """
            Orders.{ "$project" : { "_v" : "$_id", "_id" : 0 } }, { "$unionWith" : { "coll" : "Orders", "pipeline" : [{ "$project" : { "_v" : { "$literal" : 8 }, "_id" : 0 } }] } }, { "$group" : { "_id" : "$$ROOT" } }, { "$replaceRoot" : { "newRoot" : "$_id" } }
            """);
    }

    public override async Task Union_over_column_unary(bool async)
    {
        await base.Union_over_column_unary(async);

        AssertMql(
            """
            Orders.{ "$project" : { "_v" : "$_id", "_id" : 0 } }, { "$unionWith" : { "coll" : "Orders", "pipeline" : [{ "$project" : { "_v" : { "$subtract" : [0, "$_id"] }, "_id" : 0 } }] } }, { "$group" : { "_id" : "$$ROOT" } }, { "$replaceRoot" : { "newRoot" : "$_id" } }
            """);
    }

    public override async Task Union_over_column_binary(bool async)
    {
        await base.Union_over_column_binary(async);

        AssertMql(
            """
            Orders.{ "$project" : { "_v" : "$_id", "_id" : 0 } }, { "$unionWith" : { "coll" : "Orders", "pipeline" : [{ "$project" : { "_v" : { "$add" : ["$_id", 1] }, "_id" : 0 } }] } }, { "$group" : { "_id" : "$$ROOT" } }, { "$replaceRoot" : { "newRoot" : "$_id" } }
            """);
    }

    [ConditionalTheory(Skip = "No multi-collection query support"), MemberData(nameof(IsAsyncData))]
    public override Task Union_over_column_scalarsubquery(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Union_over_function_column(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Union_over_function_function(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Union_over_function_constant(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Union_over_function_unary(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Union_over_function_binary(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Union_over_function_scalarsubquery(bool _)
        => Task.CompletedTask;

    public override async Task Union_over_constant_column(bool async)
    {
        await base.Union_over_constant_column(async);

        AssertMql(
            """
            Orders.{ "$project" : { "_v" : { "$literal" : 8 }, "_id" : 0 } }, { "$unionWith" : { "coll" : "Orders", "pipeline" : [{ "$project" : { "_v" : "$_id", "_id" : 0 } }] } }, { "$group" : { "_id" : "$$ROOT" } }, { "$replaceRoot" : { "newRoot" : "$_id" } }
            """);
    }

    public override async Task Union_over_constant_function(bool async)
    {
        await base.Union_over_constant_function(async);

        AssertMql(
            """
            Orders.{ "$project" : { "_v" : { "$literal" : 8 }, "_id" : 0 } }, { "$unionWith" : { "coll" : "Orders", "pipeline" : [{ "$group" : { "_id" : "$_id", "_elements" : { "$push" : "$$ROOT" } } }, { "$project" : { "_v" : { "$size" : "$_elements" }, "_id" : 0 } }] } }, { "$group" : { "_id" : "$$ROOT" } }, { "$replaceRoot" : { "newRoot" : "$_id" } }
            """);
    }

    public override async Task Union_over_constant_constant(bool async)
    {
        await base.Union_over_constant_constant(async);
        AssertMql(
            """
            Orders.{ "$project" : { "_v" : { "$literal" : 8 }, "_id" : 0 } }, { "$unionWith" : { "coll" : "Orders", "pipeline" : [{ "$project" : { "_v" : { "$literal" : 8 }, "_id" : 0 } }] } }, { "$group" : { "_id" : "$$ROOT" } }, { "$replaceRoot" : { "newRoot" : "$_id" } }
            """);
    }

    public override async Task Union_over_constant_unary(bool async)
    {
        await base.Union_over_constant_unary(async);

        AssertMql(
            """
            Orders.{ "$project" : { "_v" : { "$literal" : 8 }, "_id" : 0 } }, { "$unionWith" : { "coll" : "Orders", "pipeline" : [{ "$project" : { "_v" : { "$subtract" : [0, "$_id"] }, "_id" : 0 } }] } }, { "$group" : { "_id" : "$$ROOT" } }, { "$replaceRoot" : { "newRoot" : "$_id" } }
            """);
    }

    public override async Task Union_over_constant_binary(bool async)
    {
        await base.Union_over_constant_binary(async);

        AssertMql(
            """
            Orders.{ "$project" : { "_v" : { "$literal" : 8 }, "_id" : 0 } }, { "$unionWith" : { "coll" : "Orders", "pipeline" : [{ "$project" : { "_v" : { "$add" : ["$_id", 1] }, "_id" : 0 } }] } }, { "$group" : { "_id" : "$$ROOT" } }, { "$replaceRoot" : { "newRoot" : "$_id" } }
            """);
    }

    [ConditionalTheory(Skip = "No multi-collection query support"), MemberData(nameof(IsAsyncData))]
    public override Task Union_over_constant_scalarsubquery(bool _)
        => Task.CompletedTask;

    public override async Task Union_over_unary_column(bool async)
    {
        await base.Union_over_unary_column(async);

        AssertMql(
            """
            Orders.{ "$project" : { "_v" : { "$subtract" : [0, "$_id"] }, "_id" : 0 } }, { "$unionWith" : { "coll" : "Orders", "pipeline" : [{ "$project" : { "_v" : "$_id", "_id" : 0 } }] } }, { "$group" : { "_id" : "$$ROOT" } }, { "$replaceRoot" : { "newRoot" : "$_id" } }
            """);
    }

    public override async Task Union_over_unary_function(bool async)
    {
        await base.Union_over_unary_function(async);

        AssertMql(
            """
            Orders.{ "$project" : { "_v" : { "$subtract" : [0, "$_id"] }, "_id" : 0 } }, { "$unionWith" : { "coll" : "Orders", "pipeline" : [{ "$group" : { "_id" : "$_id", "_elements" : { "$push" : "$$ROOT" } } }, { "$project" : { "_v" : { "$size" : "$_elements" }, "_id" : 0 } }] } }, { "$group" : { "_id" : "$$ROOT" } }, { "$replaceRoot" : { "newRoot" : "$_id" } }
            """);
    }

    public override async Task Union_over_unary_constant(bool async)
    {
        await base.Union_over_unary_constant(async);

        AssertMql(
            """
            Orders.{ "$project" : { "_v" : { "$subtract" : [0, "$_id"] }, "_id" : 0 } }, { "$unionWith" : { "coll" : "Orders", "pipeline" : [{ "$project" : { "_v" : { "$literal" : 8 }, "_id" : 0 } }] } }, { "$group" : { "_id" : "$$ROOT" } }, { "$replaceRoot" : { "newRoot" : "$_id" } }
            """);
    }

    public override async Task Union_over_unary_unary(bool async)
    {
        await base.Union_over_unary_unary(async);

        AssertMql(
            """
            Orders.{ "$project" : { "_v" : { "$subtract" : [0, "$_id"] }, "_id" : 0 } }, { "$unionWith" : { "coll" : "Orders", "pipeline" : [{ "$project" : { "_v" : { "$subtract" : [0, "$_id"] }, "_id" : 0 } }] } }, { "$group" : { "_id" : "$$ROOT" } }, { "$replaceRoot" : { "newRoot" : "$_id" } }
            """);
    }

    public override async Task Union_over_unary_binary(bool async)
    {
        await base.Union_over_unary_binary(async);

        AssertMql(
            """
            Orders.{ "$project" : { "_v" : { "$subtract" : [0, "$_id"] }, "_id" : 0 } }, { "$unionWith" : { "coll" : "Orders", "pipeline" : [{ "$project" : { "_v" : { "$add" : ["$_id", 1] }, "_id" : 0 } }] } }, { "$group" : { "_id" : "$$ROOT" } }, { "$replaceRoot" : { "newRoot" : "$_id" } }
            """);
    }

    [ConditionalTheory(Skip = "No multi-collection query support"), MemberData(nameof(IsAsyncData))]
    public override Task Union_over_unary_scalarsubquery(bool _)
        => Task.CompletedTask;

    public override async Task Union_over_binary_column(bool async)
    {
        await base.Union_over_binary_column(async);

        AssertMql(
            """
            Orders.{ "$project" : { "_v" : { "$add" : ["$_id", 1] }, "_id" : 0 } }, { "$unionWith" : { "coll" : "Orders", "pipeline" : [{ "$project" : { "_v" : "$_id", "_id" : 0 } }] } }, { "$group" : { "_id" : "$$ROOT" } }, { "$replaceRoot" : { "newRoot" : "$_id" } }
            """);
    }

    public override async Task Union_over_binary_function(bool async)
    {
        await base.Union_over_binary_function(async);

        AssertMql(
            """
            Orders.{ "$project" : { "_v" : { "$add" : ["$_id", 1] }, "_id" : 0 } }, { "$unionWith" : { "coll" : "Orders", "pipeline" : [{ "$group" : { "_id" : "$_id", "_elements" : { "$push" : "$$ROOT" } } }, { "$project" : { "_v" : { "$size" : "$_elements" }, "_id" : 0 } }] } }, { "$group" : { "_id" : "$$ROOT" } }, { "$replaceRoot" : { "newRoot" : "$_id" } }
            """);
    }

    public override async Task Union_over_binary_constant(bool async)
    {
        await base.Union_over_binary_constant(async);

        AssertMql(
            """
            Orders.{ "$project" : { "_v" : { "$add" : ["$_id", 1] }, "_id" : 0 } }, { "$unionWith" : { "coll" : "Orders", "pipeline" : [{ "$project" : { "_v" : { "$literal" : 8 }, "_id" : 0 } }] } }, { "$group" : { "_id" : "$$ROOT" } }, { "$replaceRoot" : { "newRoot" : "$_id" } }
            """);
    }

    public override async Task Union_over_binary_unary(bool async)
    {
        await base.Union_over_binary_unary(async);

        AssertMql(
            """
            Orders.{ "$project" : { "_v" : { "$add" : ["$_id", 1] }, "_id" : 0 } }, { "$unionWith" : { "coll" : "Orders", "pipeline" : [{ "$project" : { "_v" : { "$subtract" : [0, "$_id"] }, "_id" : 0 } }] } }, { "$group" : { "_id" : "$$ROOT" } }, { "$replaceRoot" : { "newRoot" : "$_id" } }
            """);
    }

    public override async Task Union_over_binary_binary(bool async)
    {
        await base.Union_over_binary_binary(async);

        AssertMql(
            """
            Orders.{ "$project" : { "_v" : { "$add" : ["$_id", 1] }, "_id" : 0 } }, { "$unionWith" : { "coll" : "Orders", "pipeline" : [{ "$project" : { "_v" : { "$add" : ["$_id", 1] }, "_id" : 0 } }] } }, { "$group" : { "_id" : "$$ROOT" } }, { "$replaceRoot" : { "newRoot" : "$_id" } }
            """);
    }

    [ConditionalTheory(Skip = "No multi-collection query support"), MemberData(nameof(IsAsyncData))]
    public override Task Union_over_binary_scalarsubquery(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Union_over_scalarsubquery_column(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Union_over_scalarsubquery_function(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Union_over_scalarsubquery_constant(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Union_over_scalarsubquery_unary(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Union_over_scalarsubquery_binary(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Union_over_scalarsubquery_scalarsubquery(bool _)
        => Task.CompletedTask;

    public override async Task Union_over_OrderBy_Take1(bool async)
    {
        await base.Union_over_OrderBy_Take1(async);

        AssertMql(
            """
            Orders.{ "$sort" : { "OrderDate" : 1 } }, { "$limit" : 5 }, { "$project" : { "_v" : "$_id", "_id" : 0 } }, { "$unionWith" : { "coll" : "Orders", "pipeline" : [{ "$project" : { "_v" : "$_id", "_id" : 0 } }] } }, { "$group" : { "_id" : "$$ROOT" } }, { "$replaceRoot" : { "newRoot" : "$_id" } }
            """);
    }

    public override async Task Union_over_OrderBy_without_Skip_Take1(bool async)
    {
        await base.Union_over_OrderBy_without_Skip_Take1(async);

        AssertMql(
            """
            Orders.{ "$sort" : { "OrderDate" : 1 } }, { "$project" : { "_v" : "$_id", "_id" : 0 } }, { "$unionWith" : { "coll" : "Orders", "pipeline" : [{ "$project" : { "_v" : "$_id", "_id" : 0 } }] } }, { "$group" : { "_id" : "$$ROOT" } }, { "$replaceRoot" : { "newRoot" : "$_id" } }
            """);
    }

    public override async Task Union_over_OrderBy_Take2(bool async)
    {
        await base.Union_over_OrderBy_Take2(async);

        AssertMql(
            """
            Orders.{ "$project" : { "_v" : "$_id", "_id" : 0 } }, { "$unionWith" : { "coll" : "Orders", "pipeline" : [{ "$sort" : { "OrderDate" : 1 } }, { "$limit" : 5 }, { "$project" : { "_v" : "$_id", "_id" : 0 } }] } }, { "$group" : { "_id" : "$$ROOT" } }, { "$replaceRoot" : { "newRoot" : "$_id" } }
            """);
    }

    public override async Task Union_over_OrderBy_without_Skip_Take2(bool async)
    {
        await base.Union_over_OrderBy_without_Skip_Take2(async);

        AssertMql(
            """
            Orders.{ "$project" : { "_v" : "$_id", "_id" : 0 } }, { "$unionWith" : { "coll" : "Orders", "pipeline" : [{ "$sort" : { "OrderDate" : 1 } }, { "$project" : { "_v" : "$_id", "_id" : 0 } }] } }, { "$group" : { "_id" : "$$ROOT" } }, { "$replaceRoot" : { "newRoot" : "$_id" } }
            """);
    }

    public override async Task OrderBy_Take_Union(bool async)
    {
        await base.OrderBy_Take_Union(async);

        AssertMql(
            """
            Customers.{ "$sort" : { "ContactName" : 1 } }, { "$limit" : 1 }, { "$unionWith" : { "coll" : "Customers", "pipeline" : [{ "$sort" : { "ContactName" : 1 } }, { "$limit" : 1 }] } }, { "$group" : { "_id" : "$$ROOT" } }, { "$replaceRoot" : { "newRoot" : "$_id" } }
            """);
    }

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Collection_projection_after_set_operation(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Concat_with_one_side_being_GroupBy_aggregate(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Union_on_entity_with_correlated_collection(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Union_on_entity_plus_other_column_with_correlated_collection(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Except_non_entity(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Except_simple_followed_by_projecting_constant(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Except_nested(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Except_nested2(bool _)
        => Task.CompletedTask;

    public override async Task Concat_nested(bool async)
    {
        await base.Concat_nested(async);

        AssertMql(
            """
            Customers.{ "$match" : { "City" : "México D.F." } }, { "$unionWith" : { "coll" : "Customers", "pipeline" : [{ "$match" : { "City" : "Berlin" } }] } }, { "$unionWith" : { "coll" : "Customers", "pipeline" : [{ "$match" : { "City" : "London" } }] } }
            """);
    }

    public override async Task Union_nested(bool async)
    {
        await base.Union_nested(async);
        AssertMql(
            """
            Customers.{ "$match" : { "ContactTitle" : "Owner" } }, { "$unionWith" : { "coll" : "Customers", "pipeline" : [{ "$match" : { "City" : "México D.F." } }] } }, { "$group" : { "_id" : "$$ROOT" } }, { "$replaceRoot" : { "newRoot" : "$_id" } }, { "$unionWith" : { "coll" : "Customers", "pipeline" : [{ "$match" : { "City" : "London" } }] } }, { "$group" : { "_id" : "$$ROOT" } }, { "$replaceRoot" : { "newRoot" : "$_id" } }
            """);
    }

    public override async Task Union_non_entity(bool async)
    {
        await base.Union_non_entity(async);

        AssertMql(
            """
            Customers.{ "$match" : { "ContactTitle" : "Owner" } }, { "$project" : { "_v" : "$_id", "_id" : 0 } }, { "$unionWith" : { "coll" : "Customers", "pipeline" : [{ "$match" : { "City" : "México D.F." } }, { "$project" : { "_v" : "$_id", "_id" : 0 } }] } }, { "$group" : { "_id" : "$$ROOT" } }, { "$replaceRoot" : { "newRoot" : "$_id" } }
            """);
    }

    public override async Task Concat_non_entity(bool async)
    {
        await base.Concat_non_entity(async);
        AssertMql(
            """
            Customers.{ "$match" : { "City" : "México D.F." } }, { "$project" : { "_v" : "$_id", "_id" : 0 } }, { "$unionWith" : { "coll" : "Customers", "pipeline" : [{ "$match" : { "ContactTitle" : "Owner" } }, { "$project" : { "_v" : "$_id", "_id" : 0 } }] } }
            """);
    }

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Collection_projection_after_set_operation_fails_if_distinct(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Collection_projection_before_set_operation_fails(bool _)
        => Task.CompletedTask;

#if !EF8

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Intersect_on_distinct(bool _)
        => Task.CompletedTask;

    public override async Task Union_on_distinct(bool async)
    {
        await base.Union_on_distinct(async);

        AssertMql(
            """
            Customers.{ "$match" : { "City" : "México D.F." } }, { "$project" : { "_v" : "$CompanyName", "_id" : 0 } }, { "$group" : { "_id" : "$$ROOT" } }, { "$replaceRoot" : { "newRoot" : "$_id" } }, { "$unionWith" : { "coll" : "Customers", "pipeline" : [{ "$match" : { "ContactTitle" : "Owner" } }, { "$project" : { "_v" : "$CompanyName", "_id" : 0 } }] } }, { "$group" : { "_id" : "$$ROOT" } }, { "$replaceRoot" : { "newRoot" : "$_id" } }
            """);
    }

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task Except_on_distinct(bool _)
        => Task.CompletedTask;

#endif

    public override async Task Include_Union_only_on_one_side_throws(bool async)
    {
        await base.Include_Union_only_on_one_side_throws(async);

        AssertMql();
    }

    public override async Task Include_Union_different_includes_throws(bool async)
    {
        await base.Include_Union_different_includes_throws(async);

        AssertMql();
    }

    public override async Task Concat_with_pruning(bool async)
    {
        await base.Concat_with_pruning(async);

        AssertMql(
            """
            Customers.{ "$match" : { "_id" : { "$regularExpression" : { "pattern" : "^A", "options" : "s" } } } }, { "$unionWith" : { "coll" : "Customers", "pipeline" : [{ "$match" : { "_id" : { "$regularExpression" : { "pattern" : "^B", "options" : "s" } } } }] } }, { "$project" : { "_v" : "$City", "_id" : 0 } }
            """);
    }

    public override async Task Concat_with_distinct_on_one_source_and_pruning(bool async)
    {
        await base.Concat_with_distinct_on_one_source_and_pruning(async);

        AssertMql(
            """
            Customers.{ "$match" : { "_id" : { "$regularExpression" : { "pattern" : "^A", "options" : "s" } } } }, { "$unionWith" : { "coll" : "Customers", "pipeline" : [{ "$match" : { "_id" : { "$regularExpression" : { "pattern" : "^B", "options" : "s" } } } }, { "$group" : { "_id" : "$$ROOT" } }, { "$replaceRoot" : { "newRoot" : "$_id" } }] } }, { "$project" : { "_v" : "$City", "_id" : 0 } }
            """);
    }

    public override async Task Concat_with_distinct_on_both_source_and_pruning(bool async)
    {
        await base.Concat_with_distinct_on_both_source_and_pruning(async);

        AssertMql(
            """
            Customers.{ "$match" : { "_id" : { "$regularExpression" : { "pattern" : "^A", "options" : "s" } } } }, { "$group" : { "_id" : "$$ROOT" } }, { "$replaceRoot" : { "newRoot" : "$_id" } }, { "$unionWith" : { "coll" : "Customers", "pipeline" : [{ "$match" : { "_id" : { "$regularExpression" : { "pattern" : "^B", "options" : "s" } } } }, { "$group" : { "_id" : "$$ROOT" } }, { "$replaceRoot" : { "newRoot" : "$_id" } }] } }, { "$project" : { "_v" : "$City", "_id" : 0 } }
            """);
    }

    public override async Task Nested_concat_with_pruning(bool async)
    {
        await base.Nested_concat_with_pruning(async);

        AssertMql(
            """
            Customers.{ "$match" : { "_id" : { "$regularExpression" : { "pattern" : "^A", "options" : "s" } } } }, { "$unionWith" : { "coll" : "Customers", "pipeline" : [{ "$match" : { "_id" : { "$regularExpression" : { "pattern" : "^B", "options" : "s" } } } }] } }, { "$unionWith" : { "coll" : "Customers", "pipeline" : [{ "$match" : { "_id" : { "$regularExpression" : { "pattern" : "^A", "options" : "s" } } } }] } }, { "$project" : { "_v" : "$City", "_id" : 0 } }
            """);
    }

    public override async Task Nested_concat_with_distinct_in_the_middle_and_pruning(bool async)
    {
        await base.Nested_concat_with_distinct_in_the_middle_and_pruning(async);

        AssertMql(
            """
            Customers.{ "$match" : { "_id" : { "$regularExpression" : { "pattern" : "^A", "options" : "s" } } } }, { "$unionWith" : { "coll" : "Customers", "pipeline" : [{ "$match" : { "_id" : { "$regularExpression" : { "pattern" : "^B", "options" : "s" } } } }, { "$group" : { "_id" : "$$ROOT" } }, { "$replaceRoot" : { "newRoot" : "$_id" } }] } }, { "$unionWith" : { "coll" : "Customers", "pipeline" : [{ "$match" : { "_id" : { "$regularExpression" : { "pattern" : "^A", "options" : "s" } } } }] } }, { "$project" : { "_v" : "$City", "_id" : 0 } }
            """);
    }

    [ConditionalTheory(Skip = "Not throwing expected translation failed exception from EF, but still throws."), MemberData(nameof(IsAsyncData))]
    public override Task Client_eval_Union_FirstOrDefault(bool _)
        => Task.CompletedTask;

    private void AssertMql(params string[] expected)
        => Fixture.TestMqlLoggerFactory.AssertBaseline(expected);

    protected override void ClearLog()
        => Fixture.TestMqlLoggerFactory.Clear();
}
