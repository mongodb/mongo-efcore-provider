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

#if EF8 || EF9

using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.TestModels.Northwind;
using Microsoft.EntityFrameworkCore.TestUtilities;
using MongoDB.Driver.Linq;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace MongoDB.EntityFrameworkCore.SpecificationTests.Query;

public class NorthwindFunctionsQueryMongoTest : NorthwindFunctionsQueryTestBase<NorthwindQueryMongoFixture<NoopModelCustomizer>>
{
    public NorthwindFunctionsQueryMongoTest(
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

    public override async Task TimeSpan_Compare_to_simple_zero(bool async, bool compareTo)
    {
        // Copied from base because of Mongo Local date handling.
        var myDatetime = new DateTime(1998, 5, 4, 0, 0, 0, DateTimeKind.Utc);

        if (compareTo)
        {
            await AssertQuery(async, ss => ss.Set<Order>().Where(c => c.OrderDate!.Value.CompareTo(myDatetime) == 0));
            await AssertQuery(async, ss => ss.Set<Order>().Where(c => 0 != c.OrderDate!.Value.CompareTo(myDatetime)));
            await AssertQuery(async, ss => ss.Set<Order>().Where(c => c.OrderDate!.Value.CompareTo(myDatetime) > 0));
            await AssertQuery(async, ss => ss.Set<Order>().Where(c => 0 >= c.OrderDate!.Value.CompareTo(myDatetime)));
            await AssertQuery(async, ss => ss.Set<Order>().Where(c => 0 < c.OrderDate!.Value.CompareTo(myDatetime)));
            await AssertQuery(async, ss => ss.Set<Order>().Where(c => c.OrderDate!.Value.CompareTo(myDatetime) <= 0));

            AssertMql(
                """
                Orders.{ "$match" : { "OrderDate" : { "$date" : "1998-05-04T00:00:00Z" } } }
                """,
                //
                """
                Orders.{ "$match" : { "OrderDate" : { "$ne" : { "$date" : "1998-05-04T00:00:00Z" } } } }
                """,
                //
                """
                Orders.{ "$match" : { "OrderDate" : { "$gt" : { "$date" : "1998-05-04T00:00:00Z" } } } }
                """,
                //
                """
                Orders.{ "$match" : { "OrderDate" : { "$lte" : { "$date" : "1998-05-04T00:00:00Z" } } } }
                """,
                //
                """
                Orders.{ "$match" : { "OrderDate" : { "$gt" : { "$date" : "1998-05-04T00:00:00Z" } } } }
                """,
                //
                """
                Orders.{ "$match" : { "OrderDate" : { "$lte" : { "$date" : "1998-05-04T00:00:00Z" } } } }
                """);
        }
        else
        {
            await AssertQuery(async, ss => ss.Set<Order>().Where(c => DateTime.Compare(c.OrderDate!.Value, myDatetime) == 0));
            await AssertQuery(async, ss => ss.Set<Order>().Where(c => 0 != DateTime.Compare(c.OrderDate!.Value, myDatetime)));
            await AssertQuery(async, ss => ss.Set<Order>().Where(c => DateTime.Compare(c.OrderDate!.Value, myDatetime) > 0));
            await AssertQuery(async, ss => ss.Set<Order>().Where(c => 0 >= DateTime.Compare(c.OrderDate!.Value, myDatetime)));
            await AssertQuery(async, ss => ss.Set<Order>().Where(c => 0 < DateTime.Compare(c.OrderDate!.Value, myDatetime)));
            await AssertQuery(async, ss => ss.Set<Order>().Where(c => DateTime.Compare(c.OrderDate!.Value, myDatetime) <= 0));

            AssertMql(
                """
                Orders.{ "$match" : { "OrderDate" : { "$date" : "1998-05-04T00:00:00Z" } } }
                """,
                //
                """
                Orders.{ "$match" : { "OrderDate" : { "$ne" : { "$date" : "1998-05-04T00:00:00Z" } } } }
                """,
                //
                """
                Orders.{ "$match" : { "OrderDate" : { "$gt" : { "$date" : "1998-05-04T00:00:00Z" } } } }
                """,
                //
                """
                Orders.{ "$match" : { "OrderDate" : { "$lte" : { "$date" : "1998-05-04T00:00:00Z" } } } }
                """,
                //
                """
                Orders.{ "$match" : { "OrderDate" : { "$gt" : { "$date" : "1998-05-04T00:00:00Z" } } } }
                """,
                //
                """
                Orders.{ "$match" : { "OrderDate" : { "$lte" : { "$date" : "1998-05-04T00:00:00Z" } } } }
                """);
        }
    }

    public override async Task String_StartsWith_Literal(bool async)
    {
        await base.String_StartsWith_Literal(async);

        AssertMql(
            """
            Customers.{ "$match" : { "ContactName" : { "$regularExpression" : { "pattern" : "^M", "options" : "s" } } } }
            """);
    }

    public override async Task String_StartsWith_Parameter(bool async)
    {
        await base.String_StartsWith_Parameter(async);

        AssertMql(
            """
            Customers.{ "$match" : { "ContactName" : { "$regularExpression" : { "pattern" : "^M", "options" : "s" } } } }
            """);
    }

    public override async Task String_StartsWith_Identity(bool async)
    {
        await base.String_StartsWith_Identity(async);

        AssertMql(
            """
            Customers.{ "$match" : { "$expr" : { "$eq" : [{ "$indexOfCP" : ["$ContactName", "$ContactName"] }, 0] } } }
            """);
    }

    public override async Task String_StartsWith_Column(bool async)
    {
        await base.String_StartsWith_Column(async);

        AssertMql(
            """
            Customers.{ "$match" : { "$expr" : { "$eq" : [{ "$indexOfCP" : ["$ContactName", "$ContactName"] }, 0] } } }
            """);
    }

    public override async Task String_StartsWith_MethodCall(bool async)
    {
        await base.String_StartsWith_MethodCall(async);

        AssertMql(
            """
            Customers.{ "$match" : { "ContactName" : { "$regularExpression" : { "pattern" : "^M", "options" : "s" } } } }
            """);
    }

#if EF9
    [ConditionalTheory(Skip = "StartsWith/Contains/EndsWith Ordinal/OrdinalIgnoreCase issue EF-243"), MemberData(nameof(IsAsyncData))]
    public override Task String_StartsWith_with_StringComparison_Ordinal(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "StartsWith/Contains/EndsWith Ordinal/OrdinalIgnoreCase issue EF-243"), MemberData(nameof(IsAsyncData))]
    public override Task String_StartsWith_with_StringComparison_OrdinalIgnoreCase(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "StartsWith/Contains/EndsWith Ordinal/OrdinalIgnoreCase issue EF-243"), MemberData(nameof(IsAsyncData))]
    public override Task String_StartsWith_with_StringComparison_unsupported(bool _)
        => Task.CompletedTask;

#endif

    public override async Task String_EndsWith_Literal(bool async)
    {
        await base.String_EndsWith_Literal(async);

        AssertMql(
            """
            Customers.{ "$match" : { "ContactName" : { "$regularExpression" : { "pattern" : "b$", "options" : "s" } } } }
            """);
    }

    public override async Task String_EndsWith_Parameter(bool async)
    {
        await base.String_EndsWith_Parameter(async);

        AssertMql(
            """
            Customers.{ "$match" : { "ContactName" : { "$regularExpression" : { "pattern" : "b$", "options" : "s" } } } }
            """);
    }

    public override async Task String_EndsWith_Identity(bool async)
    {
        await base.String_EndsWith_Identity(async);

        AssertMql(
            """
            Customers.{ "$match" : { "$expr" : { "$let" : { "vars" : { "start" : { "$subtract" : [{ "$strLenCP" : "$ContactName" }, { "$strLenCP" : "$ContactName" }] } }, "in" : { "$and" : [{ "$gte" : ["$$start", 0] }, { "$eq" : [{ "$indexOfCP" : ["$ContactName", "$ContactName", "$$start"] }, "$$start"] }] } } } } }
            """);
    }

    public override async Task String_EndsWith_Column(bool async)
    {
        await base.String_EndsWith_Column(async);

        AssertMql(
            """
            Customers.{ "$match" : { "$expr" : { "$let" : { "vars" : { "start" : { "$subtract" : [{ "$strLenCP" : "$ContactName" }, { "$strLenCP" : "$ContactName" }] } }, "in" : { "$and" : [{ "$gte" : ["$$start", 0] }, { "$eq" : [{ "$indexOfCP" : ["$ContactName", "$ContactName", "$$start"] }, "$$start"] }] } } } } }
            """);
    }

    public override async Task String_EndsWith_MethodCall(bool async)
    {
        await base.String_EndsWith_MethodCall(async);

        AssertMql(
            """
            Customers.{ "$match" : { "ContactName" : { "$regularExpression" : { "pattern" : "m$", "options" : "s" } } } }
            """);
    }

#if EF9
    [ConditionalTheory(Skip = "StartsWith/Contains/EndsWith Ordinal/OrdinalIgnoreCase issue EF-243"), MemberData(nameof(IsAsyncData))]
    public override Task String_EndsWith_with_StringComparison_Ordinal(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "StartsWith/Contains/EndsWith Ordinal/OrdinalIgnoreCase issue EF-243"), MemberData(nameof(IsAsyncData))]
    public override Task String_EndsWith_with_StringComparison_OrdinalIgnoreCase(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "StartsWith/Contains/EndsWith Ordinal/OrdinalIgnoreCase issue EF-243"), MemberData(nameof(IsAsyncData))]
    public override Task String_EndsWith_with_StringComparison_unsupported(bool _)
        => Task.CompletedTask;

#endif

    public override async Task String_Contains_Literal(bool async)
    {
        await base.String_Contains_Literal(async);

        AssertMql(
            """
            Customers.{ "$match" : { "ContactName" : { "$regularExpression" : { "pattern" : "M", "options" : "s" } } } }
            """);
    }

    public override async Task String_Contains_Identity(bool async)
    {
        await base.String_Contains_Identity(async);

        AssertMql(
            """
            Customers.{ "$match" : { "$expr" : { "$gte" : [{ "$indexOfCP" : ["$ContactName", "$ContactName"] }, 0] } } }
            """);
    }

    public override async Task String_Contains_Column(bool async)
    {
        await base.String_Contains_Column(async);

#if EF9
        AssertMql(
            """
Customers.{ "$match" : { "$expr" : { "$gte" : [{ "$indexOfCP" : ["$CompanyName", "$ContactName"] }, 0] } } }
""");
#else
        AssertMql(
            """
            Customers.{ "$match" : { "$expr" : { "$gte" : [{ "$indexOfCP" : ["$ContactName", "$ContactName"] }, 0] } } }
            """);
#endif
    }

    public override async Task String_Contains_constant_with_whitespace(bool async)
    {
        await base.String_Contains_constant_with_whitespace(async);

        AssertMql(
            """
            Customers.{ "$match" : { "ContactName" : { "$regularExpression" : { "pattern" : "\\ \\ \\ \\ \\ ", "options" : "s" } } } }
            """);
    }

    public override async Task String_Contains_parameter_with_whitespace(bool async)
    {
        await base.String_Contains_parameter_with_whitespace(async);

        AssertMql(
            """
            Customers.{ "$match" : { "ContactName" : { "$regularExpression" : { "pattern" : "\\ \\ \\ \\ \\ ", "options" : "s" } } } }
            """);
    }

    [ConditionalTheory(Skip = "Translate String.FirstOrDefault and String.LastOrDefault issue EF-248"), MemberData(nameof(IsAsyncData))]
    public override Task String_FirstOrDefault_MethodCall(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Translate String.FirstOrDefault and String.LastOrDefault issue EF-248"), MemberData(nameof(IsAsyncData))]
    public override Task String_LastOrDefault_MethodCall(bool _)
        => Task.CompletedTask;

    public override async Task String_Contains_MethodCall(bool async)
    {
        await base.String_Contains_MethodCall(async);

        AssertMql(
            """
            Customers.{ "$match" : { "ContactName" : { "$regularExpression" : { "pattern" : "M", "options" : "s" } } } }
            """);
    }

    [ConditionalTheory(Skip = "String.Join issue EF-245"), MemberData(nameof(IsAsyncData))]
    public override Task String_Join_over_non_nullable_column(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "String.Join issue EF-245"), MemberData(nameof(IsAsyncData))]
    public override Task String_Join_over_nullable_column(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "String.Join issue EF-245"), MemberData(nameof(IsAsyncData))]
    public override Task String_Join_with_predicate(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "String.Join issue EF-245"), MemberData(nameof(IsAsyncData))]
    public override Task String_Join_with_ordering(bool _)
        => Task.CompletedTask;

#if EF9
    [ConditionalTheory(Skip = "String.Join issue EF-245"), MemberData(nameof(IsAsyncData))]
    public override Task String_Join_non_aggregate(bool _)
        => Task.CompletedTask;

#endif

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task String_Concat(bool _)
        => Task.CompletedTask;

    public override async Task String_Compare_simple_zero(bool async)
    {
        await base.String_Compare_simple_zero(async);

        AssertMql(
            """
            Customers.{ "$match" : { "_id" : "AROUT" } }
            """,
            //
            """
            Customers.{ "$match" : { "_id" : { "$ne" : "AROUT" } } }
            """,
            //
            """
            Customers.{ "$match" : { "_id" : { "$gt" : "AROUT" } } }
            """,
            //
            """
            Customers.{ "$match" : { "_id" : { "$lte" : "AROUT" } } }
            """,
            //
            """
            Customers.{ "$match" : { "_id" : { "$gt" : "AROUT" } } }
            """,
            //
            """
            Customers.{ "$match" : { "_id" : { "$lte" : "AROUT" } } }
            """);
    }

    public override async Task String_Compare_simple_one(bool async)
    {
       await base.String_Compare_simple_one(async);

       AssertMql(
           """
           Customers.{ "$match" : { "_id" : { "$gt" : "AROUT" } } }
           """,
           //
           """
           Customers.{ "$match" : { "_id" : { "$lt" : "AROUT" } } }
           """,
           //
           """
           Customers.{ "$match" : { "_id" : { "$lte" : "AROUT" } } }
           """,
           //
           """
           Customers.{ "$match" : { "_id" : { "$lte" : "AROUT" } } }
           """,
           //
           """
           Customers.{ "$match" : { "_id" : { "$gte" : "AROUT" } } }
           """,
           //
           """
           Customers.{ "$match" : { "_id" : { "$gte" : "AROUT" } } }
           """);
    }

    public override async Task String_compare_with_parameter(bool async)
    {
        await base.String_compare_with_parameter(async);

        AssertMql(
            """
            Customers.{ "$match" : { "_id" : { "$gt" : "AROUT" } } }
            """,
            //
            """
            Customers.{ "$match" : { "_id" : { "$lt" : "AROUT" } } }
            """,
            //
            """
            Customers.{ "$match" : { "_id" : { "$lte" : "AROUT" } } }
            """,
            //
            """
            Customers.{ "$match" : { "_id" : { "$lte" : "AROUT" } } }
            """,
            //
            """
            Customers.{ "$match" : { "_id" : { "$gte" : "AROUT" } } }
            """,
            //
            """
            Customers.{ "$match" : { "_id" : { "$gte" : "AROUT" } } }
            """);
    }

    public override async Task String_Compare_simple_more_than_one(bool async)
    {
        await base.String_Compare_simple_more_than_one(async);

        AssertMql(
            """
            Customers.{ "$match" : { "$expr" : { "$eq" : [{ "$cmp" : ["$_id", "ALFKI"] }, 42] } } }
            """,
            //
            """
            Customers.{ "$match" : { "$expr" : { "$gt" : [{ "$cmp" : ["$_id", "ALFKI"] }, 42] } } }
            """,
            //
            """
            Customers.{ "$match" : { "$expr" : { "$gt" : [42, { "$cmp" : ["$_id", "ALFKI"] }] } } }
            """);
    }

    [ConditionalTheory(Skip = "String.Compare issue EF-244"), MemberData(nameof(IsAsyncData))]
    public override Task String_Compare_nested(bool _)
        => Task.CompletedTask;

    public override async Task String_Compare_multi_predicate(bool async)
    {
        await base.String_Compare_multi_predicate(async);

        AssertMql(
            """
            Customers.{ "$match" : { "_id" : { "$gte" : "ALFKI" } } }, { "$match" : { "_id" : { "$lt" : "CACTU" } } }
            """,
            //
            """
            Customers.{ "$match" : { "ContactTitle" : "Owner" } }, { "$match" : { "Country" : { "$ne" : "USA" } } }
            """);
    }

    public override async Task String_Compare_to_simple_zero(bool async)
    {
        await base.String_Compare_to_simple_zero(async);

        AssertMql(
            """
            Customers.{ "$match" : { "_id" : "AROUT" } }
            """,
            //
            """
            Customers.{ "$match" : { "_id" : { "$ne" : "AROUT" } } }
            """,
            //
            """
            Customers.{ "$match" : { "_id" : { "$gt" : "AROUT" } } }
            """,
            //
            """
            Customers.{ "$match" : { "_id" : { "$lte" : "AROUT" } } }
            """,
            //
            """
            Customers.{ "$match" : { "_id" : { "$gt" : "AROUT" } } }
            """,
            //
            """
            Customers.{ "$match" : { "_id" : { "$lte" : "AROUT" } } }
            """);
    }

    public override async Task String_Compare_to_simple_one(bool async)
    {
        await base.String_Compare_to_simple_one(async);

        AssertMql(
            """
Customers.{ "$match" : { "_id" : { "$gt" : "AROUT" } } }
""",
            //
            """
Customers.{ "$match" : { "_id" : { "$lt" : "AROUT" } } }
""",
            //
            """
Customers.{ "$match" : { "_id" : { "$lte" : "AROUT" } } }
""",
            //
            """
Customers.{ "$match" : { "_id" : { "$lte" : "AROUT" } } }
""",
            //
            """
Customers.{ "$match" : { "_id" : { "$gte" : "AROUT" } } }
""",
            //
            """
Customers.{ "$match" : { "_id" : { "$gte" : "AROUT" } } }
""");
    }

    public override async Task String_compare_to_with_parameter(bool async)
    {
        await base.String_compare_to_with_parameter(async);

        AssertMql(
            """
Customers.{ "$match" : { "_id" : { "$gt" : "AROUT" } } }
""",
            //
            """
Customers.{ "$match" : { "_id" : { "$lt" : "AROUT" } } }
""",
            //
            """
Customers.{ "$match" : { "_id" : { "$lte" : "AROUT" } } }
""",
            //
            """
Customers.{ "$match" : { "_id" : { "$lte" : "AROUT" } } }
""",
            //
            """
Customers.{ "$match" : { "_id" : { "$gte" : "AROUT" } } }
""",
            //
            """
Customers.{ "$match" : { "_id" : { "$gte" : "AROUT" } } }
""");
    }

    public override async Task String_Compare_to_simple_more_than_one(bool async)
    {
        await base.String_Compare_to_simple_more_than_one(async);

        AssertMql(
            """
            Customers.{ "$match" : { "$expr" : { "$eq" : [{ "$cmp" : ["$_id", "ALFKI"] }, 42] } } }
            """,
            //
            """
            Customers.{ "$match" : { "$expr" : { "$gt" : [{ "$cmp" : ["$_id", "ALFKI"] }, 42] } } }
            """,
            //
            """
            Customers.{ "$match" : { "$expr" : { "$gt" : [42, { "$cmp" : ["$_id", "ALFKI"] }] } } }
            """);
    }

    [ConditionalTheory(Skip = "String.Replace issue EF-223"), MemberData(nameof(IsAsyncData))]
    public override Task String_Compare_to_nested(bool _)
        => Task.CompletedTask;

    public override async Task String_Compare_to_multi_predicate(bool async)
    {
        await base.String_Compare_to_multi_predicate(async);

        AssertMql(
            """
Customers.{ "$match" : { "_id" : { "$gte" : "ALFKI" } } }, { "$match" : { "_id" : { "$lt" : "CACTU" } } }
""",
            //
            """
            Customers.{ "$match" : { "ContactTitle" : "Owner" } }, { "$match" : { "Country" : { "$ne" : "USA" } } }
            """);
    }

    public override async Task DateTime_Compare_to_simple_zero(bool async, bool compareTo)
    {
        // Copied from base because of Mongo Local date handling.
        var myDatetime = new DateTime(1998, 5, 4, 0, 0, 0, DateTimeKind.Utc);

        if (compareTo)
        {
            await AssertQuery(
                async,
                ss => ss.Set<Order>().Where(c => c.OrderDate!.Value.CompareTo(myDatetime) == 0));

            await AssertQuery(
                async,
                ss => ss.Set<Order>().Where(c => 0 != c.OrderDate!.Value.CompareTo(myDatetime)));

            await AssertQuery(
                async,
                ss => ss.Set<Order>().Where(c => c.OrderDate!.Value.CompareTo(myDatetime) > 0));

            await AssertQuery(
                async,
                ss => ss.Set<Order>().Where(c => 0 >= c.OrderDate!.Value.CompareTo(myDatetime)));

            await AssertQuery(
                async,
                ss => ss.Set<Order>().Where(c => 0 < c.OrderDate!.Value.CompareTo(myDatetime)));

            await AssertQuery(
                async,
                ss => ss.Set<Order>().Where(c => c.OrderDate!.Value.CompareTo(myDatetime) <= 0));

            AssertMql(
                """
                Orders.{ "$match" : { "OrderDate" : { "$date" : "1998-05-04T00:00:00Z" } } }
                """,
                //
                """
                Orders.{ "$match" : { "OrderDate" : { "$ne" : { "$date" : "1998-05-04T00:00:00Z" } } } }
                """,
                //
                """
                Orders.{ "$match" : { "OrderDate" : { "$gt" : { "$date" : "1998-05-04T00:00:00Z" } } } }
                """,
                //
                """
                Orders.{ "$match" : { "OrderDate" : { "$lte" : { "$date" : "1998-05-04T00:00:00Z" } } } }
                """,
                //
                """
                Orders.{ "$match" : { "OrderDate" : { "$gt" : { "$date" : "1998-05-04T00:00:00Z" } } } }
                """,
                //
                """
                Orders.{ "$match" : { "OrderDate" : { "$lte" : { "$date" : "1998-05-04T00:00:00Z" } } } }
                """);
        }
        else
        {
            await AssertQuery(async, ss => ss.Set<Order>().Where(c => DateTime.Compare(c.OrderDate!.Value, myDatetime) == 0));
            await AssertQuery(async, ss => ss.Set<Order>().Where(c => 0 != DateTime.Compare(c.OrderDate!.Value, myDatetime)));
            await AssertQuery(async, ss => ss.Set<Order>().Where(c => DateTime.Compare(c.OrderDate!.Value, myDatetime) > 0));
            await AssertQuery(async, ss => ss.Set<Order>().Where(c => 0 >= DateTime.Compare(c.OrderDate!.Value, myDatetime)));
            await AssertQuery(async, ss => ss.Set<Order>().Where(c => 0 < DateTime.Compare(c.OrderDate!.Value, myDatetime)));
            await AssertQuery(async, ss => ss.Set<Order>().Where(c => DateTime.Compare(c.OrderDate!.Value, myDatetime) <= 0));

            AssertMql(
                """
Orders.{ "$match" : { "OrderDate" : { "$date" : "1998-05-04T00:00:00Z" } } }
""",
                //
                """
Orders.{ "$match" : { "OrderDate" : { "$ne" : { "$date" : "1998-05-04T00:00:00Z" } } } }
""",
                //
                """
Orders.{ "$match" : { "OrderDate" : { "$gt" : { "$date" : "1998-05-04T00:00:00Z" } } } }
""",
                //
                """
Orders.{ "$match" : { "OrderDate" : { "$lte" : { "$date" : "1998-05-04T00:00:00Z" } } } }
""",
                //
                """
Orders.{ "$match" : { "OrderDate" : { "$gt" : { "$date" : "1998-05-04T00:00:00Z" } } } }
""",
                //
                """
Orders.{ "$match" : { "OrderDate" : { "$lte" : { "$date" : "1998-05-04T00:00:00Z" } } } }
""");
        }
    }

    public override async Task Int_Compare_to_simple_zero(bool async)
    {
        await base.Int_Compare_to_simple_zero(async);

        AssertMql(
            """
            Orders.{ "$match" : { "_id" : 10250 } }
            """,
            //
            """
            Orders.{ "$match" : { "_id" : { "$ne" : 10250 } } }
            """,
            //
            """
            Orders.{ "$match" : { "_id" : { "$gt" : 10250 } } }
            """,
            //
            """
            Orders.{ "$match" : { "_id" : { "$lte" : 10250 } } }
            """,
            //
            """
            Orders.{ "$match" : { "_id" : { "$gt" : 10250 } } }
            """,
            //
            """
            Orders.{ "$match" : { "_id" : { "$lte" : 10250 } } }
            """);
    }

    public override async Task Where_math_abs1(bool async)
    {
        await base.Where_math_abs1(async);

        AssertMql(
            """
            Products.{ "$match" : { "$expr" : { "$gt" : [{ "$abs" : "$_id" }, 10] } } }
            """);
    }

    public override async Task Where_math_abs2(bool async)
    {
        await base.Where_math_abs2(async);

        AssertMql(
            """
            OrderDetails.{ "$match" : { "UnitPrice" : { "$lt" : { "$numberDecimal" : "7" } } } }, { "$match" : { "$expr" : { "$gt" : [{ "$toInt" : { "$abs" : "$Quantity" } }, 10] } } }
            """);
    }

    public override async Task Where_math_abs3(bool async)
    {
        await base.Where_math_abs3(async);

        AssertMql(
            """
            OrderDetails.{ "$match" : { "Quantity" : { "$lt" : 5 } } }, { "$match" : { "$expr" : { "$gt" : [{ "$abs" : "$UnitPrice" }, { "$numberDecimal" : "10" }] } } }
            """);
    }

    public override async Task Where_math_abs_uncorrelated(bool async)
    {
        await base.Where_math_abs_uncorrelated(async);

        AssertMql(
            """
            OrderDetails.{ "$match" : { "UnitPrice" : { "$lt" : { "$numberDecimal" : "7" } } } }, { "$match" : { "_id.ProductID" : { "$gt" : 10 } } }
            """);
    }

    public override async Task Where_math_ceiling1(bool async)
    {
        await base.Where_math_ceiling1(async);

        AssertMql(
            """
            OrderDetails.{ "$match" : { "UnitPrice" : { "$lt" : { "$numberDecimal" : "7" } } } }, { "$match" : { "$expr" : { "$gt" : [{ "$ceil" : "$Discount" }, 0.0] } } }
            """);
    }

    public override async Task Where_math_ceiling2(bool async)
    {
        await base.Where_math_ceiling2(async);

        AssertMql(
            """
            OrderDetails.{ "$match" : { "Quantity" : { "$lt" : 5 } } }, { "$match" : { "$expr" : { "$gt" : [{ "$ceil" : "$UnitPrice" }, { "$numberDecimal" : "10" }] } } }
            """);
    }

    public override async Task Where_math_floor(bool async)
    {
        await base.Where_math_floor(async);

        AssertMql(
            """
            OrderDetails.{ "$match" : { "Quantity" : { "$lt" : 5 } } }, { "$match" : { "$expr" : { "$gt" : [{ "$floor" : "$UnitPrice" }, { "$numberDecimal" : "10" }] } } }
            """);
    }

    public override async Task Where_math_power(bool async)
    {
        await base.Where_math_power(async);

        AssertMql(
            """
            OrderDetails.{ "$match" : { "$expr" : { "$gt" : [{ "$pow" : ["$Discount", 3.0] }, 0.004999999888241291] } } }
            """);
    }

    public override async Task Where_math_square(bool async)
    {
        await base.Where_math_square(async);

        AssertMql(
            """
            OrderDetails.{ "$match" : { "$expr" : { "$gt" : [{ "$pow" : ["$Discount", 2.0] }, 0.05000000074505806] } } }
            """);
    }

    public override async Task Where_math_round(bool async)
    {
        await base.Where_math_round(async);

        AssertMql(
            """
            OrderDetails.{ "$match" : { "Quantity" : { "$lt" : 5 } } }, { "$match" : { "$expr" : { "$gt" : [{ "$round" : "$UnitPrice" }, { "$numberDecimal" : "10" }] } } }
            """);
    }

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Sum_over_round_works_correctly_in_projection(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Sum_over_round_works_correctly_in_projection_2(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Sum_over_truncate_works_correctly_in_projection(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Sum_over_truncate_works_correctly_in_projection_2(bool _)
        => Task.CompletedTask;

    public override async Task Select_math_round_int(bool async)
    {
        await base.Select_math_round_int(async);

        AssertMql(
            """
            Orders.{ "$match" : { "_id" : { "$lt" : 10250 } } }, { "$project" : { "A" : { "$round" : "$_id" }, "_id" : 0 } }
            """);
    }

    public override async Task Select_math_truncate_int(bool async)
    {
        await base.Select_math_truncate_int(async);

        AssertMql(
            """
            Orders.{ "$match" : { "_id" : { "$lt" : 10250 } } }, { "$project" : { "A" : { "$trunc" : "$_id" }, "_id" : 0 } }
            """);
    }

    public override async Task Where_math_round2(bool async)
    {
        await base.Where_math_round2(async);

        AssertMql(
            """
            OrderDetails.{ "$match" : { "$expr" : { "$gt" : [{ "$round" : ["$UnitPrice", 2] }, { "$numberDecimal" : "100" }] } } }
            """);
    }

    public override async Task Where_math_truncate(bool async)
    {
        await base.Where_math_truncate(async);

        AssertMql(
            """
            OrderDetails.{ "$match" : { "Quantity" : { "$lt" : 5 } } }, { "$match" : { "$expr" : { "$gt" : [{ "$trunc" : "$UnitPrice" }, { "$numberDecimal" : "10" }] } } }
            """);
    }

    public override async Task Where_math_exp(bool async)
    {
        await base.Where_math_exp(async);

        AssertMql(
            """
            OrderDetails.{ "$match" : { "_id.OrderID" : 11077 } }, { "$match" : { "$expr" : { "$gt" : [{ "$exp" : "$Discount" }, 1.0] } } }
            """);
    }

    public override async Task Where_math_log10(bool async)
    {
        await base.Where_math_log10(async);

        AssertMql(
            """
            OrderDetails.{ "$match" : { "_id.OrderID" : 11077, "Discount" : { "$gt" : 0.0 } } }, { "$match" : { "$expr" : { "$lt" : [{ "$log10" : "$Discount" }, 0.0] } } }
            """);
    }

    public override async Task Where_math_log(bool async)
    {
        await base.Where_math_log(async);

        AssertMql(
            """
            OrderDetails.{ "$match" : { "_id.OrderID" : 11077, "Discount" : { "$gt" : 0.0 } } }, { "$match" : { "$expr" : { "$lt" : [{ "$ln" : "$Discount" }, 0.0] } } }
            """);
    }

    public override async Task Where_math_log_new_base(bool async)
    {
        await base.Where_math_log_new_base(async);

#if EF9
        AssertMql(
            """
OrderDetails.{ "$match" : { "_id.OrderID" : 11077, "Discount" : { "$gt" : 0.0 } } }, { "$match" : { "$expr" : { "$lt" : [{ "$log" : ["$Discount", 7.0] }, -1.0] } } }
""");
#else
        AssertMql(
            """
            OrderDetails.{ "$match" : { "_id.OrderID" : 11077, "Discount" : { "$gt" : 0.0 } } }, { "$match" : { "$expr" : { "$lt" : [{ "$log" : ["$Discount", 7.0] }, 0.0] } } }
            """);
#endif
    }

    public override async Task Where_math_sqrt(bool async)
    {
        await base.Where_math_sqrt(async);

        AssertMql(
            """
            OrderDetails.{ "$match" : { "_id.OrderID" : 11077 } }, { "$match" : { "$expr" : { "$gt" : [{ "$sqrt" : "$Discount" }, 0.0] } } }
            """);
    }

    public override async Task Where_math_acos(bool async)
    {
        await base.Where_math_acos(async);

        AssertMql(
            """
            OrderDetails.{ "$match" : { "_id.OrderID" : 11077 } }, { "$match" : { "$expr" : { "$gt" : [{ "$acos" : { "$toDouble" : "$Discount" } }, 1.0] } } }
            """);
    }

    public override async Task Where_math_asin(bool async)
    {
        await base.Where_math_asin(async);

        AssertMql(
            """
            OrderDetails.{ "$match" : { "_id.OrderID" : 11077 } }, { "$match" : { "$expr" : { "$gt" : [{ "$asin" : { "$toDouble" : "$Discount" } }, 0.0] } } }
            """);
    }

    public override async Task Where_math_atan(bool async)
    {
        await base.Where_math_atan(async);

        AssertMql(
            """
            OrderDetails.{ "$match" : { "_id.OrderID" : 11077 } }, { "$match" : { "$expr" : { "$gt" : [{ "$atan" : { "$toDouble" : "$Discount" } }, 0.0] } } }
            """);
    }

    public override async Task Where_math_atan2(bool async)
    {
        await base.Where_math_atan2(async);

        AssertMql(
            """
            OrderDetails.{ "$match" : { "_id.OrderID" : 11077 } }, { "$match" : { "$expr" : { "$gt" : [{ "$atan2" : [{ "$toDouble" : "$Discount" }, 1.0] }, 0.0] } } }
            """);
    }

    public override async Task Where_math_cos(bool async)
    {
        await base.Where_math_cos(async);

        AssertMql(
            """
            OrderDetails.{ "$match" : { "_id.OrderID" : 11077 } }, { "$match" : { "$expr" : { "$gt" : [{ "$cos" : { "$toDouble" : "$Discount" } }, 0.0] } } }
            """);
    }

    public override async Task Where_math_sin(bool async)
    {
        await base.Where_math_sin(async);

        AssertMql(
            """
            OrderDetails.{ "$match" : { "_id.OrderID" : 11077 } }, { "$match" : { "$expr" : { "$gt" : [{ "$sin" : { "$toDouble" : "$Discount" } }, 0.0] } } }
            """);
    }

    public override async Task Where_math_tan(bool async)
    {
        await base.Where_math_tan(async);

        AssertMql(
            """
            OrderDetails.{ "$match" : { "_id.OrderID" : 11077 } }, { "$match" : { "$expr" : { "$gt" : [{ "$tan" : { "$toDouble" : "$Discount" } }, 0.0] } } }
            """);
    }

    [ConditionalTheory(Skip = "Math.Sign mapping issue EF-239"), MemberData(nameof(IsAsyncData))]
    public override Task Where_math_sign(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Math.Min/Math.Max mapping issue EF-238"), MemberData(nameof(IsAsyncData))]
    public override Task Where_math_min(bool _)
        => Task.CompletedTask;

#if EF9
    [ConditionalTheory(Skip = "Math.Min/Math.Max mapping issue EF-238"), MemberData(nameof(IsAsyncData))]
    public override Task Where_math_min_nested(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Math.Min/Math.Max mapping issue EF-238"), MemberData(nameof(IsAsyncData))]
    public override Task Where_math_min_nested_twice(bool _)
        => Task.CompletedTask;

#endif

    [ConditionalTheory(Skip = "Math.Min/Math.Max mapping issue EF-238"), MemberData(nameof(IsAsyncData))]
    public override Task Where_math_max(bool _)
        => Task.CompletedTask;

#if EF9
    [ConditionalTheory(Skip = "Math.Min/Math.Max mapping issue EF-238"), MemberData(nameof(IsAsyncData))]
    public override Task Where_math_max_nested(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Math.Min/Math.Max mapping issue EF-238"), MemberData(nameof(IsAsyncData))]
    public override Task Where_math_max_nested_twice(bool _)
        => Task.CompletedTask;

#endif

    [ConditionalTheory(Skip = "Double.RadiansToDegrees and Double.DegreesToRadians mapping issue EF-240"), MemberData(nameof(IsAsyncData))]
    public override Task Where_math_degrees(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Double.RadiansToDegrees and Double.DegreesToRadians mapping issue EF-240"), MemberData(nameof(IsAsyncData))]
    public override Task Where_math_radians(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "MathF mapping issue EF-237"), MemberData(nameof(IsAsyncData))]
    public override Task Where_mathf_abs1(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "MathF mapping issue EF-237"), MemberData(nameof(IsAsyncData))]
    public override Task Where_mathf_ceiling1(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "MathF mapping issue EF-237"), MemberData(nameof(IsAsyncData))]
    public override Task Where_mathf_floor(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "MathF mapping issue EF-237"), MemberData(nameof(IsAsyncData))]
    public override Task Where_mathf_power(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "MathF mapping issue EF-237"), MemberData(nameof(IsAsyncData))]
    public override Task Where_mathf_square(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "MathF mapping issue EF-237"), MemberData(nameof(IsAsyncData))]
    public override Task Where_mathf_round2(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "MathF mapping issue EF-237"), MemberData(nameof(IsAsyncData))]
    public override Task Select_mathf_round(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "MathF mapping issue EF-237"), MemberData(nameof(IsAsyncData))]
    public override Task Select_mathf_round2(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "MathF mapping issue EF-237"), MemberData(nameof(IsAsyncData))]
    public override Task Where_mathf_truncate(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "MathF mapping issue EF-237"), MemberData(nameof(IsAsyncData))]
    public override Task Select_mathf_truncate(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "MathF mapping issue EF-237"), MemberData(nameof(IsAsyncData))]
    public override Task Where_mathf_exp(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "MathF mapping issue EF-237"), MemberData(nameof(IsAsyncData))]
    public override Task Where_mathf_log10(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "MathF mapping issue EF-237"), MemberData(nameof(IsAsyncData))]
    public override Task Where_mathf_log(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "MathF mapping issue EF-237"), MemberData(nameof(IsAsyncData))]
    public override Task Where_mathf_log_new_base(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "MathF mapping issue EF-237"), MemberData(nameof(IsAsyncData))]
    public override Task Where_mathf_sqrt(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "MathF mapping issue EF-237"), MemberData(nameof(IsAsyncData))]
    public override Task Where_mathf_acos(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "MathF mapping issue EF-237"), MemberData(nameof(IsAsyncData))]
    public override Task Where_mathf_asin(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "MathF mapping issue EF-237"), MemberData(nameof(IsAsyncData))]
    public override Task Where_mathf_atan(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "MathF mapping issue EF-237"), MemberData(nameof(IsAsyncData))]
    public override Task Where_mathf_atan2(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "MathF mapping issue EF-237"), MemberData(nameof(IsAsyncData))]
    public override Task Where_mathf_cos(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "MathF mapping issue EF-237"), MemberData(nameof(IsAsyncData))]
    public override Task Where_mathf_sin(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "MathF mapping issue EF-237"), MemberData(nameof(IsAsyncData))]
    public override Task Where_mathf_tan(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "MathF mapping issue EF-237"), MemberData(nameof(IsAsyncData))]
    public override Task Where_mathf_sign(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "MathF mapping issue EF-237"), MemberData(nameof(IsAsyncData))]
    public override Task Where_mathf_degrees(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "MathF mapping issue EF-237"), MemberData(nameof(IsAsyncData))]
    public override Task Where_mathf_radians(bool _)
        => Task.CompletedTask;

    public override async Task Where_guid_newguid(bool async)
    {
        await base.Where_guid_newguid(async);

        AssertMql(
            """
            Customers.
            """);
    }

    public override async Task Where_string_to_upper(bool async)
    {
        await base.Where_string_to_upper(async);

        AssertMql(
            """
            Customers.{ "$match" : { "_id" : { "$regularExpression" : { "pattern" : "^ALFKI$", "options" : "is" } } } }
            """);
    }

    public override async Task Where_string_to_lower(bool async)
    {
        await base.Where_string_to_lower(async);

        AssertMql(
            """
            Customers.{ "$match" : { "_id" : { "$regularExpression" : { "pattern" : "^alfki$", "options" : "is" } } } }
            """);
    }

    public override async Task Where_functions_nested(bool async)
    {
        await base.Where_functions_nested(async);

        AssertMql(
            """
            Customers.{ "$match" : { "$expr" : { "$eq" : [{ "$pow" : [{ "$strLenCP" : "$_id" }, 2.0] }, 25.0] } } }
            """);
    }

    [ConditionalTheory(Skip = "Translate Convert methods issue EF-235"), MemberData(nameof(IsAsyncData))]
    public override Task Convert_ToBoolean(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Translate Convert methods issue EF-235"), MemberData(nameof(IsAsyncData))]
    public override Task Convert_ToByte(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Translate Convert methods issue EF-235"), MemberData(nameof(IsAsyncData))]
    public override Task Convert_ToDecimal(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Translate Convert methods issue EF-235"), MemberData(nameof(IsAsyncData))]
    public override Task Convert_ToDouble(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Translate Convert methods issue EF-235"), MemberData(nameof(IsAsyncData))]
    public override Task Convert_ToInt16(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Translate Convert methods issue EF-235"), MemberData(nameof(IsAsyncData))]
    public override Task Convert_ToInt32(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Translate Convert methods issue EF-235"), MemberData(nameof(IsAsyncData))]
    public override Task Convert_ToInt64(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Translate Convert methods issue EF-235"), MemberData(nameof(IsAsyncData))]
    public override Task Convert_ToString(bool _)
        => Task.CompletedTask;

    public override async Task Indexof_with_emptystring(bool async)
    {
        await base.Indexof_with_emptystring(async);

#if EF9
        AssertMql(
            """
Customers.{ "$match" : { "Region" : { "$regularExpression" : { "pattern" : "$", "options" : "s" } } } }
""");
#else
        AssertMql(
            """
            Customers.{ "$match" : { "ContactName" : { "$regularExpression" : { "pattern" : "$", "options" : "s" } } } }
            """);
#endif
    }

    public override async Task Indexof_with_one_constant_arg(bool async)
    {
        await base.Indexof_with_one_constant_arg(async);

        AssertMql(
            """
            Customers.{ "$match" : { "ContactName" : { "$regularExpression" : { "pattern" : "^.{1}a", "options" : "s" } } } }
            """);
    }

    public override async Task Indexof_with_one_parameter_arg(bool async)
    {
        await base.Indexof_with_one_parameter_arg(async);

        AssertMql(
            """
            Customers.{ "$match" : { "ContactName" : { "$regularExpression" : { "pattern" : "^.{1}a", "options" : "s" } } } }
            """);
    }

    public override async Task Indexof_with_constant_starting_position(bool async)
    {
        await base.Indexof_with_constant_starting_position(async);

        AssertMql(
            """
            Customers.{ "$match" : { "ContactName" : { "$regularExpression" : { "pattern" : "^.{2}(?!.{0,1}a).{2}a", "options" : "s" } } } }
            """);
    }

    public override async Task Indexof_with_parameter_starting_position(bool async)
    {
        await base.Indexof_with_parameter_starting_position(async);

        AssertMql(
            """
            Customers.{ "$match" : { "ContactName" : { "$regularExpression" : { "pattern" : "^.{2}(?!.{0,1}a).{2}a", "options" : "s" } } } }
            """);
    }

    [ConditionalTheory(Skip = "Translate string.Replace methods issue EF-223"), MemberData(nameof(IsAsyncData))]
    public override Task Replace_with_emptystring(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Translate string.Replace methods issue EF-223"), MemberData(nameof(IsAsyncData))]
    public override Task Replace_using_property_arguments(bool _)
        => Task.CompletedTask;

    public override async Task Substring_with_one_arg_with_zero_startindex(bool async)
    {
        await base.Substring_with_one_arg_with_zero_startindex(async);

        AssertMql(
            """
            Customers.{ "$match" : { "$expr" : { "$eq" : [{ "$substrCP" : ["$_id", 0, { "$strLenCP" : "$_id" }] }, "ALFKI"] } } }, { "$project" : { "_v" : "$ContactName", "_id" : 0 } }
            """);
    }

    public override async Task Substring_with_one_arg_with_constant(bool async)
    {
        await base.Substring_with_one_arg_with_constant(async);

        AssertMql(
            """
            Customers.{ "$match" : { "$expr" : { "$eq" : [{ "$substrCP" : ["$_id", 1, { "$subtract" : [{ "$strLenCP" : "$_id" }, 1] }] }, "LFKI"] } } }, { "$project" : { "_v" : "$ContactName", "_id" : 0 } }
            """);
    }

    public override async Task Substring_with_one_arg_with_closure(bool async)
    {
        await base.Substring_with_one_arg_with_closure(async);

        AssertMql(
            """
            Customers.{ "$match" : { "$expr" : { "$eq" : [{ "$substrCP" : ["$_id", 2, { "$subtract" : [{ "$strLenCP" : "$_id" }, 2] }] }, "FKI"] } } }, { "$project" : { "_v" : "$ContactName", "_id" : 0 } }
            """);
    }

    public override async Task Substring_with_two_args_with_zero_startindex(bool async)
    {
        await base.Substring_with_two_args_with_zero_startindex(async);

        AssertMql(
            """
            Customers.{ "$match" : { "_id" : "ALFKI" } }, { "$project" : { "_v" : { "$substrCP" : ["$ContactName", 0, 3] }, "_id" : 0 } }
            """);
    }

    public override async Task Substring_with_two_args_with_zero_length(bool async)
    {
        await base.Substring_with_two_args_with_zero_length(async);

        AssertMql(
            """
            Customers.{ "$match" : { "_id" : "ALFKI" } }, { "$project" : { "_v" : { "$substrCP" : ["$ContactName", 2, 0] }, "_id" : 0 } }
            """);
    }

    public override async Task Substring_with_two_args_with_constant(bool async)
    {
        await base.Substring_with_two_args_with_constant(async);

        AssertMql(
            """
            Customers.{ "$match" : { "_id" : "ALFKI" } }, { "$project" : { "_v" : { "$substrCP" : ["$ContactName", 1, 3] }, "_id" : 0 } }
            """);
    }

    public override async Task Substring_with_two_args_with_closure(bool async)
    {
        await base.Substring_with_two_args_with_closure(async);

        AssertMql(
            """
            Customers.{ "$match" : { "_id" : "ALFKI" } }, { "$project" : { "_v" : { "$substrCP" : ["$ContactName", 2, 3] }, "_id" : 0 } }
            """);
    }

    public override async Task Substring_with_two_args_with_Index_of(bool async)
    {
        await base.Substring_with_two_args_with_Index_of(async);

        AssertMql(
            """
            Customers.{ "$match" : { "_id" : "ALFKI" } }, { "$project" : { "_v" : { "$substrCP" : ["$ContactName", { "$indexOfCP" : ["$ContactName", "a"] }, 3] }, "_id" : 0 } }
            """);
    }

    public override async Task IsNullOrEmpty_in_predicate(bool async)
    {
        await base.IsNullOrEmpty_in_predicate(async);

        AssertMql(
            """
            Customers.{ "$match" : { "Region" : { "$in" : [null, ""] } } }
            """);
    }

    public override async Task IsNullOrEmpty_in_projection(bool async)
    {
        await base.IsNullOrEmpty_in_projection(async);

        AssertMql(
            """
            Customers.{ "$project" : { "_id" : "$_id", "Value" : { "$in" : ["$Region", [null, ""]] } } }
            """);
    }

    public override async Task IsNullOrEmpty_negated_in_predicate(bool async)
    {
        await base.IsNullOrEmpty_negated_in_predicate(async);

        AssertMql(
            """
            Customers.{ "$match" : { "Region" : { "$nin" : [null, ""] } } }
            """);
    }

    public override async Task IsNullOrEmpty_negated_in_projection(bool async)
    {
        await base.IsNullOrEmpty_negated_in_projection(async);

        AssertMql(
            """
            Customers.{ "$project" : { "_id" : "$_id", "Value" : { "$not" : { "$in" : ["$Region", [null, ""]] } } } }
            """);
    }

    public override async Task IsNullOrWhiteSpace_in_predicate(bool async)
    {
        await base.IsNullOrWhiteSpace_in_predicate(async);

        AssertMql(
            """
            Customers.{ "$match" : { "Region" : { "$in" : [null, { "$regularExpression" : { "pattern" : "^\\s*$", "options" : "" } }] } } }
            """);
    }

    public override async Task IsNullOrWhiteSpace_in_predicate_on_non_nullable_column(bool async)
    {
        await base.IsNullOrWhiteSpace_in_predicate_on_non_nullable_column(async);

        AssertMql(
            """
            Customers.{ "$match" : { "_id" : { "$in" : [null, { "$regularExpression" : { "pattern" : "^\\s*$", "options" : "" } }] } } }
            """);
    }

    [ConditionalTheory(Skip = "Translate string.Trim methods issue EF-241"), MemberData(nameof(IsAsyncData))]
    public override Task TrimStart_without_arguments_in_predicate(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Translate string.Trim methods issue EF-241"), MemberData(nameof(IsAsyncData))]
    public override Task TrimStart_with_char_argument_in_predicate(bool _)
        => Task.CompletedTask;

    public override async Task TrimStart_with_char_array_argument_in_predicate(bool async)
    {
        await base.TrimStart_with_char_array_argument_in_predicate(async);

        AssertMql(
            """
            Customers.{ "$match" : { "ContactTitle" : { "$regularExpression" : { "pattern" : "^[Ow]*(?=[^Ow])ner$", "options" : "s" } } } }
            """);
    }

    [ConditionalTheory(Skip = "Translate string.Trim methods issue EF-241"), MemberData(nameof(IsAsyncData))]
    public override Task TrimEnd_without_arguments_in_predicate(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Translate string.Trim methods issue EF-241"), MemberData(nameof(IsAsyncData))]
    public override Task TrimEnd_with_char_argument_in_predicate(bool _)
        => Task.CompletedTask;

    public override async Task TrimEnd_with_char_array_argument_in_predicate(bool async)
    {
        await base.TrimEnd_with_char_array_argument_in_predicate(async);

        AssertMql(
            """
            Customers.{ "$match" : { "ContactTitle" : { "$regularExpression" : { "pattern" : "^Own(?<=[^er])[er]*$", "options" : "s" } } } }
            """);
    }

    public override async Task Trim_without_argument_in_predicate(bool async)
    {
        await base.Trim_without_argument_in_predicate(async);

        AssertMql(
            """
            Customers.{ "$match" : { "ContactTitle" : { "$regularExpression" : { "pattern" : "^\\s*(?!\\s)Owner(?<!\\s)\\s*$", "options" : "s" } } } }
            """);
    }

    [ConditionalTheory(Skip = "Translate string.Trim methods issue EF-241"), MemberData(nameof(IsAsyncData))]
    public override Task Trim_with_char_argument_in_predicate(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Translate string.Trim methods issue EF-241"), MemberData(nameof(IsAsyncData))]
    public override Task Trim_with_char_array_argument_in_predicate(bool _)
        => Task.CompletedTask;

    public override async Task Order_by_length_twice(bool async)
    {
        await base.Order_by_length_twice(async);

        AssertMql(
            """
            Customers.{ "$project" : { "_id" : 0, "_document" : "$$ROOT", "_key1" : { "$strLenCP" : "$_id" }, "_key2" : { "$strLenCP" : "$_id" } } }, { "$sort" : { "_key1" : 1, "_key2" : 1, "_document._id" : 1 } }, { "$replaceRoot" : { "newRoot" : "$_document" } }
            """);
    }

    [ConditionalTheory(Skip = "Projections issue EF-76"), MemberData(nameof(IsAsyncData))]
    public override Task Order_by_length_twice_followed_by_projection_of_naked_collection_navigation(bool _)
        => Task.CompletedTask;

    public override async Task Static_string_equals_in_predicate(bool async)
    {
        await base.Static_string_equals_in_predicate(async);

        AssertMql(
            """
            Customers.{ "$match" : { "_id" : "ANATR" } }
            """);
    }

    public override async Task Static_equals_nullable_datetime_compared_to_non_nullable(bool async)
    {
        var arg = new DateTime(1996, 7, 4, 0, 0, 0, DateTimeKind.Utc);

        await AssertQuery(
            async,
            ss => ss.Set<Order>().Where(o => Equals(o.OrderDate, arg)));

        AssertMql(
            """
            Orders.{ "$match" : { "OrderDate" : { "$date" : "1996-07-04T00:00:00Z" } } }
            """);
    }

    [ConditionalTheory(Skip = "Equals with different types issue EF-221"), MemberData(nameof(IsAsyncData))]
    public override Task Static_equals_int_compared_to_long(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "DateOnly support issue EF-242"), MemberData(nameof(IsAsyncData))]
    public override Task Where_DateOnly_FromDateTime(bool _)
        => Task.CompletedTask;

    public override async Task Projecting_Math_Truncate_and_ordering_by_it_twice(bool async)
    {
        await base.Projecting_Math_Truncate_and_ordering_by_it_twice(async);

        AssertMql(
            """
            Orders.{ "$match" : { "_id" : { "$lt" : 10250 } } }, { "$project" : { "_id" : 0, "_document" : "$$ROOT", "_key1" : { "$trunc" : "$_id" } } }, { "$sort" : { "_key1" : 1 } }, { "$replaceRoot" : { "newRoot" : "$_document" } }, { "$project" : { "_id" : 0, "_document" : "$$ROOT", "_key1" : { "$trunc" : "$_id" } } }, { "$sort" : { "_key1" : 1 } }, { "$replaceRoot" : { "newRoot" : "$_document" } }, { "$project" : { "A" : { "$trunc" : "$_id" }, "_id" : 0 } }
            """);
    }

    public override async Task Projecting_Math_Truncate_and_ordering_by_it_twice2(bool async)
    {
        await base.Projecting_Math_Truncate_and_ordering_by_it_twice2(async);

        AssertMql(
            """
            Orders.{ "$match" : { "_id" : { "$lt" : 10250 } } }, { "$project" : { "_id" : 0, "_document" : "$$ROOT", "_key1" : { "$trunc" : "$_id" } } }, { "$sort" : { "_key1" : 1 } }, { "$replaceRoot" : { "newRoot" : "$_document" } }, { "$project" : { "_id" : 0, "_document" : "$$ROOT", "_key1" : { "$trunc" : "$_id" } } }, { "$sort" : { "_key1" : -1 } }, { "$replaceRoot" : { "newRoot" : "$_document" } }, { "$project" : { "A" : { "$trunc" : "$_id" }, "_id" : 0 } }
            """);
    }

    public override async Task Projecting_Math_Truncate_and_ordering_by_it_twice3(bool async)
    {
        await base.Projecting_Math_Truncate_and_ordering_by_it_twice3(async);

        AssertMql(
            """
            Orders.{ "$match" : { "_id" : { "$lt" : 10250 } } }, { "$project" : { "_id" : 0, "_document" : "$$ROOT", "_key1" : { "$trunc" : "$_id" }, "_key2" : { "$trunc" : "$_id" } } }, { "$sort" : { "_key1" : -1, "_key2" : 1 } }, { "$replaceRoot" : { "newRoot" : "$_document" } }, { "$project" : { "A" : { "$trunc" : "$_id" }, "_id" : 0 } }
            """);
    }

    public override async Task Regex_IsMatch_MethodCall(bool async)
    {
        await base.Regex_IsMatch_MethodCall(async);

        AssertMql(
            """
            Customers.{ "$match" : { "_id" : { "$regularExpression" : { "pattern" : "^T", "options" : "" } } } }
            """);
    }

    [ConditionalTheory(Skip = "Regex with non-constant pattern issue EF-247"), MemberData(nameof(IsAsyncData))]
    public override Task Regex_IsMatch_MethodCall_constant_input(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "DateTime subtraction issue EF-246"), MemberData(nameof(IsAsyncData))]
    public override Task Datetime_subtraction_TotalDays(bool _)
        => Task.CompletedTask;

#if EF9
    public override async Task Select_ToString_IndexOf(bool async)
    {
        await base.Select_ToString_IndexOf(async);

        AssertMql(
            """
Orders.{ "$match" : { "$expr" : { "$eq" : [{ "$indexOfCP" : [{ "$toString" : "$_id" }, "123"] }, -1] } } }
""");
    }

    public override async Task Select_IndexOf_ToString(bool async)
    {
        await base.Select_IndexOf_ToString(async);

        AssertMql(
            """
Orders.{ "$match" : { "$expr" : { "$eq" : [{ "$indexOfCP" : ["123", { "$toString" : "$_id" }] }, -1] } } }
""");
    }

    public override async Task String_Contains_in_projection(bool async)
    {
        await base.String_Contains_in_projection(async);

        AssertMql(
            """
Customers.{ "$project" : { "_id" : "$_id", "Value" : { "$gte" : [{ "$indexOfCP" : ["$CompanyName", "$ContactName"] }, 0] } } }
""");
    }

    public override async Task String_Contains_negated_in_predicate(bool async)
    {
        await base.String_Contains_negated_in_predicate(async);

        AssertMql(
            """
Customers.{ "$match" : { "$nor" : [{ "$expr" : { "$gte" : [{ "$indexOfCP" : ["$CompanyName", "$ContactName"] }, 0] } }] } }
""");
    }

    public override async Task String_Contains_negated_in_projection(bool async)
    {
        await base.String_Contains_negated_in_projection(async);

        AssertMql(
            """
Customers.{ "$project" : { "_id" : "$_id", "Value" : { "$not" : { "$gte" : [{ "$indexOfCP" : ["$CompanyName", "$ContactName"] }, 0] } } } }
""");
    }

    [ConditionalTheory(Skip = "StartsWith/Contains/EndsWith Ordinal/OrdinalIgnoreCase issue EF-243"), MemberData(nameof(IsAsyncData))]
    public override Task String_Contains_with_StringComparison_Ordinal(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "StartsWith/Contains/EndsWith Ordinal/OrdinalIgnoreCase issue EF-243"), MemberData(nameof(IsAsyncData))]
    public override Task String_Contains_with_StringComparison_OrdinalIgnoreCase(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "StartsWith/Contains/EndsWith Ordinal/OrdinalIgnoreCase issue EF-243"), MemberData(nameof(IsAsyncData))]
    public override Task String_Contains_with_StringComparison_unsupported(bool _)
        => Task.CompletedTask;

#endif

    private void AssertMql(params string[] expected)
        => Fixture.TestMqlLoggerFactory.AssertBaseline(expected);

    protected override void ClearLog()
        => Fixture.TestMqlLoggerFactory.Clear();
}

#endif
