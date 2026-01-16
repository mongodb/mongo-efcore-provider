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

    public override async Task String_StartsWith_with_StringComparison_Ordinal(bool async)
    {
        // Fails: StartsWith/Contains/EndsWith Ordinal/OrdinalIgnoreCase issue EF-243
        Assert.Contains(
            "Expression not supported",
            (await Assert.ThrowsAsync<ExpressionNotSupportedException>(() =>
                base.String_StartsWith_with_StringComparison_Ordinal(async))).Message);

        AssertMql(
            """
Customers.
""");
    }

    public override async Task String_StartsWith_with_StringComparison_OrdinalIgnoreCase(bool async)
    {
        // Fails: StartsWith/Contains/EndsWith Ordinal/OrdinalIgnoreCase issue EF-243
        Assert.Contains(
            "Expression not supported",
            (await Assert.ThrowsAsync<ExpressionNotSupportedException>(() =>
                base.String_StartsWith_with_StringComparison_OrdinalIgnoreCase(async))).Message);

        AssertMql(
            """
Customers.
""");
    }

    public override async Task String_StartsWith_with_StringComparison_unsupported(bool async)
    {
        // These are translated by the Mongo provider. See EF-243.
        await AssertQuery(async, ss => ss.Set<Customer>().Where(c => c.CompanyName.StartsWith("Qu", StringComparison.CurrentCulture)));
        await AssertQuery(async, ss => ss.Set<Customer>().Where(c => c.ContactName.StartsWith("m", StringComparison.CurrentCultureIgnoreCase)));

        // Fails: StartsWith/Contains/EndsWith Ordinal/OrdinalIgnoreCase issue EF-243
        Assert.Contains(
            "Expression not supported",
            (await Assert.ThrowsAsync<ExpressionNotSupportedException>(() =>
                AssertQuery(async, ss => ss.Set<Customer>().Where(c => c.ContactName.StartsWith("M", StringComparison.InvariantCulture))))).Message);

        Assert.Contains(
            "Expression not supported",
            (await Assert.ThrowsAsync<ExpressionNotSupportedException>(() =>
                AssertQuery(async, ss => ss.Set<Customer>().Where(c => c.ContactName.StartsWith("M", StringComparison.InvariantCultureIgnoreCase))))).Message);

        AssertMql(
            """
Customers.{ "$match" : { "CompanyName" : { "$regularExpression" : { "pattern" : "^Qu", "options" : "s" } } } }
""",
            //
            """
Customers.{ "$match" : { "ContactName" : { "$regularExpression" : { "pattern" : "^m", "options" : "is" } } } }
""",
            //
            """
Customers.
""",
            //
            """
Customers.
""");
    }

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

    public override async Task String_EndsWith_with_StringComparison_Ordinal(bool async)
    {
        // Fails: StartsWith/Contains/EndsWith Ordinal/OrdinalIgnoreCase issue EF-243
        Assert.Contains(
            "Expression not supported",
            (await Assert.ThrowsAsync<ExpressionNotSupportedException>(() =>
                base.String_EndsWith_with_StringComparison_Ordinal(async))).Message);

        AssertMql(
            """
Customers.
""");
    }

    public override async Task String_EndsWith_with_StringComparison_OrdinalIgnoreCase(bool async)
    {
        // Fails: StartsWith/Contains/EndsWith Ordinal/OrdinalIgnoreCase issue EF-243
        Assert.Contains(
            "Expression not supported",
            (await Assert.ThrowsAsync<ExpressionNotSupportedException>(() =>
                base.String_EndsWith_with_StringComparison_OrdinalIgnoreCase(async))).Message);

        AssertMql(
            """
Customers.
""");
    }

    public override async Task String_EndsWith_with_StringComparison_unsupported(bool async)
    {
        // These are translated by the Mongo provider. See EF-243.
        await AssertQuery(async, ss => ss.Set<Customer>().Where(c => c.ContactName.EndsWith("e", StringComparison.CurrentCulture)));
        await AssertQuery(async, ss => ss.Set<Customer>().Where(c => c.ContactName.EndsWith("m", StringComparison.CurrentCultureIgnoreCase)));

        // Fails: StartsWith/Contains/EndsWith Ordinal/OrdinalIgnoreCase issue EF-243
        Assert.Contains(
            "Expression not supported",
            (await Assert.ThrowsAsync<ExpressionNotSupportedException>(() =>
                AssertQuery(async, ss => ss.Set<Customer>().Where(c => c.ContactName.EndsWith("M", StringComparison.InvariantCulture))))).Message);

        Assert.Contains(
            "Expression not supported",
            (await Assert.ThrowsAsync<ExpressionNotSupportedException>(() =>
                AssertQuery(async, ss => ss.Set<Customer>().Where(c => c.ContactName.EndsWith("M", StringComparison.InvariantCultureIgnoreCase))))).Message);

        AssertMql(
            """
Customers.{ "$match" : { "ContactName" : { "$regularExpression" : { "pattern" : "e$", "options" : "s" } } } }
""",
            //
            """
Customers.{ "$match" : { "ContactName" : { "$regularExpression" : { "pattern" : "m$", "options" : "is" } } } }
""",
            //
            """
Customers.
""",
            //
            """
Customers.
""");
    }

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

    public override async Task String_FirstOrDefault_MethodCall(bool async)
    {
        // Fails: Translate String.FirstOrDefault and String.LastOrDefault issue EF-248
        Assert.Contains(
            "StringSerializer must implement IBsonArraySerializer",
            (await Assert.ThrowsAsync<InvalidOperationException>(() =>
                base.String_FirstOrDefault_MethodCall(async))).Message);

        AssertMql(
            """
Customers.
""");
    }

    public override async Task String_LastOrDefault_MethodCall(bool async)
    {
        // Fails: Translate String.FirstOrDefault and String.LastOrDefault issue EF-248
        Assert.Contains(
            "StringSerializer must implement IBsonArraySerializer",
            (await Assert.ThrowsAsync<InvalidOperationException>(() =>
                base.String_LastOrDefault_MethodCall(async))).Message);

        AssertMql(
            """
Customers.
""");
    }

    public override async Task String_Contains_MethodCall(bool async)
    {
        await base.String_Contains_MethodCall(async);

        AssertMql(
            """
Customers.{ "$match" : { "ContactName" : { "$regularExpression" : { "pattern" : "M", "options" : "s" } } } }
""");
    }

    public override async Task String_Join_over_non_nullable_column(bool async)
    {
        // Fails: String.Join issue EF-245
        await AssertTranslationFailed(() => base.String_Join_over_non_nullable_column(async));

        AssertMql(
);
    }

    public override async Task String_Join_over_nullable_column(bool async)
    {
        // Fails: String.Join issue EF-245
        await AssertTranslationFailed(() => base.String_Join_over_nullable_column(async));

        AssertMql(
);
    }

    public override async Task String_Join_with_predicate(bool async)
    {
        // Fails: String.Join issue EF-245
        await AssertTranslationFailed(() => base.String_Join_with_predicate(async));

        AssertMql(
);
    }

    public override async Task String_Join_with_ordering(bool async)
    {
        // Fails: String.Join issue EF-245
        await AssertTranslationFailed(() => base.String_Join_with_ordering(async));

        AssertMql(
);
    }

    #if EF9

    public override async Task String_Join_non_aggregate(bool async)
    {
        // Fails: String.Join issue EF-245
        Assert.Contains(
            "Expression not supported",
            (await Assert.ThrowsAsync<ExpressionNotSupportedException>(() =>
                base.String_Join_non_aggregate(async))).Message);

        AssertMql(
            """
Customers.
""");
    }

    #endif

    public override async Task String_Concat(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.String_Concat(async));

        AssertMql(
);
    }

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

    public override async Task String_Compare_nested(bool async)
    {
        // Fails: String.Compare issue EF-244
        Assert.Contains(
            "Expression not supported",
            (await Assert.ThrowsAsync<ExpressionNotSupportedException>(() =>
                base.String_Compare_nested(async))).Message);

        AssertMql(
            """
            Customers.{ "$match" : { "$expr" : { "$eq" : [{ "$cmp" : ["$_id", { "$concat" : ["M", "$_id"] }] }, 0] } } }
            """,
            //
            """
            Customers.{ "$match" : { "$expr" : { "$ne" : [0, { "$cmp" : ["$_id", { "$toUpper" : "$_id" }] }] } } }
            """,
            //
            """
            Customers.
            """);
    }

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

    public override async Task String_Compare_to_nested(bool async)
    {
        // Fails: String.Replace issue EF-223
        Assert.Contains(
            "Expression not supported: \"AROUT\".Replace",
            (await Assert.ThrowsAsync<ExpressionNotSupportedException>(() => base.String_Compare_to_nested(async))).Message);

        AssertMql(
            """
Customers.{ "$match" : { "$expr" : { "$ne" : [{ "$cmp" : ["$_id", { "$concat" : ["M", "$_id"] }] }, 0] } } }
""",
            //
            """
Customers.{ "$match" : { "$expr" : { "$eq" : [0, { "$cmp" : ["$_id", { "$toUpper" : "$_id" }] }] } } }
""",
            //
            """
Customers.
""");
    }

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

    public override async Task Sum_over_round_works_correctly_in_projection(bool async)
    {
        // Fails: Projections issue EF-76
        await AssertTranslationFailed(() => base.Sum_over_round_works_correctly_in_projection(async));

        AssertMql(
);
    }

    public override async Task Sum_over_round_works_correctly_in_projection_2(bool async)
    {
        // Fails: Projections issue EF-76
        await AssertTranslationFailed(() => base.Sum_over_round_works_correctly_in_projection_2(async));

        AssertMql(
);
    }

    public override async Task Sum_over_truncate_works_correctly_in_projection(bool async)
    {
        // Fails: Projections issue EF-76
        await AssertTranslationFailed(() => base.Sum_over_truncate_works_correctly_in_projection(async));

        AssertMql(
);
    }

    public override async Task Sum_over_truncate_works_correctly_in_projection_2(bool async)
    {
        // Fails: Projections issue EF-76
        await AssertTranslationFailed(() => base.Sum_over_truncate_works_correctly_in_projection_2(async));

        AssertMql(
);
    }

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

    public override async Task Where_math_sign(bool async)
    {
        // Fails: Math.Sign mapping issue EF-239
        Assert.Contains(
            "Expression not supported",
            (await Assert.ThrowsAsync<ExpressionNotSupportedException>(() =>
                base.Where_math_sign(async))).Message);

        AssertMql(
            """
OrderDetails.
""");
    }

    public override async Task Where_math_min(bool async)
    {
        // Fails: Math.Min/Math.Max mapping issue EF-238
        Assert.Contains(
            "Expression not supported",
            (await Assert.ThrowsAsync<ExpressionNotSupportedException>(() =>
                base.Where_math_min(async))).Message);

        AssertMql(
            """
OrderDetails.
""");
    }

    #if EF9

    public override async Task Where_math_min_nested(bool async)
    {
        // Fails: Math.Min/Math.Max mapping issue EF-238
        Assert.Contains(
            "Expression not supported",
            (await Assert.ThrowsAsync<ExpressionNotSupportedException>(() =>
                base.Where_math_min_nested(async))).Message);

        AssertMql(
            """
OrderDetails.
""");
    }

    public override async Task Where_math_min_nested_twice(bool async)
    {
        // Fails: Math.Min/Math.Max mapping issue EF-238
        Assert.Contains(
            "Expression not supported",
            (await Assert.ThrowsAsync<ExpressionNotSupportedException>(() =>
                base.Where_math_min_nested_twice(async))).Message);

        AssertMql(
            """
OrderDetails.
""");
    }

    #endif

    public override async Task Where_math_max(bool async)
    {
        // Fails: Math.Min/Math.Max mapping issue EF-238
        Assert.Contains(
            "Expression not supported",
            (await Assert.ThrowsAsync<ExpressionNotSupportedException>(() =>
                base.Where_math_max(async))).Message);

        AssertMql(
            """
OrderDetails.
""");
    }

    #if EF9

    public override async Task Where_math_max_nested(bool async)
    {
        // Fails: Math.Min/Math.Max mapping issue EF-238
        Assert.Contains(
            "Expression not supported",
            (await Assert.ThrowsAsync<ExpressionNotSupportedException>(() =>
                base.Where_math_max_nested(async))).Message);

        AssertMql(
            """
OrderDetails.
""");
    }

    public override async Task Where_math_max_nested_twice(bool async)
    {
        // Fails: Math.Min/Math.Max mapping issue EF-238
        Assert.Contains(
            "Expression not supported",
            (await Assert.ThrowsAsync<ExpressionNotSupportedException>(() =>
                base.Where_math_max_nested_twice(async))).Message);

        AssertMql(
            """
OrderDetails.
""");
    }

    #endif

    public override async Task Where_math_degrees(bool async)
    {
        // Fails: Double.RadiansToDegrees and Double.DegreesToRadians mapping issue EF-240
        Assert.Contains(
            "Expression not supported",
            (await Assert.ThrowsAsync<ExpressionNotSupportedException>(() =>
                base.Where_math_degrees(async))).Message);

        AssertMql(
            """
OrderDetails.
""");
    }

    public override async Task Where_math_radians(bool async)
    {
        // Fails: Double.RadiansToDegrees and Double.DegreesToRadians mapping issue EF-240
        Assert.Contains(
            "Expression not supported",
            (await Assert.ThrowsAsync<ExpressionNotSupportedException>(() =>
                base.Where_math_radians(async))).Message);

        AssertMql(
            """
OrderDetails.
""");
    }

    public override async Task Where_mathf_abs1(bool async)
    {
        // Fails: MathF mapping issue EF-237
        Assert.Contains(
            "Expression not supported",
            (await Assert.ThrowsAsync<ExpressionNotSupportedException>(() =>
                base.Where_mathf_abs1(async))).Message);

        AssertMql(
            """
Products.
""");
    }

    public override async Task Where_mathf_ceiling1(bool async)
    {
        // Fails: MathF mapping issue EF-237
        Assert.Contains(
            "Expression not supported",
            (await Assert.ThrowsAsync<ExpressionNotSupportedException>(() =>
                base.Where_mathf_ceiling1(async))).Message);

        AssertMql(
            """
OrderDetails.
""");
    }

    public override async Task Where_mathf_floor(bool async)
    {
        // Fails: MathF mapping issue EF-237
        Assert.Contains(
            "Expression not supported",
            (await Assert.ThrowsAsync<ExpressionNotSupportedException>(() =>
                base.Where_mathf_floor(async))).Message);

        AssertMql(
            """
OrderDetails.
""");
    }

    public override async Task Where_mathf_power(bool async)
    {
        // Fails: MathF mapping issue EF-237
        Assert.Contains(
            "Expression not supported",
            (await Assert.ThrowsAsync<ExpressionNotSupportedException>(() =>
                base.Where_mathf_power(async))).Message);

        AssertMql(
            """
OrderDetails.
""");
    }

    public override async Task Where_mathf_square(bool async)
    {
        // Fails: MathF mapping issue EF-237
        Assert.Contains(
            "Expression not supported",
            (await Assert.ThrowsAsync<ExpressionNotSupportedException>(() =>
                base.Where_mathf_square(async))).Message);

        AssertMql(
            """
OrderDetails.
""");
    }

    public override async Task Where_mathf_round2(bool async)
    {
        // Fails: MathF mapping issue EF-237
        Assert.Contains(
            "Expression not supported",
            (await Assert.ThrowsAsync<ExpressionNotSupportedException>(() =>
                base.Where_mathf_round2(async))).Message);

        AssertMql(
            """
OrderDetails.
""");
    }

    public override async Task Select_mathf_round(bool async)
    {
        // Fails: MathF mapping issue EF-237
        Assert.Contains(
            "Expression not supported",
            (await Assert.ThrowsAsync<ExpressionNotSupportedException>(() =>
                base.Select_mathf_round(async))).Message);

        AssertMql(
            """
Orders.
""");
    }

    public override async Task Select_mathf_round2(bool async)
    {
        // Fails: MathF mapping issue EF-237
        Assert.Contains(
            "Expression not supported",
            (await Assert.ThrowsAsync<ExpressionNotSupportedException>(() =>
                base.Select_mathf_round2(async))).Message);

        AssertMql(
            """
OrderDetails.
""");
    }

    public override async Task Where_mathf_truncate(bool async)
    {
        // Fails: MathF mapping issue EF-237
        Assert.Contains(
            "Expression not supported",
            (await Assert.ThrowsAsync<ExpressionNotSupportedException>(() =>
                base.Where_mathf_truncate(async))).Message);

        AssertMql(
            """
OrderDetails.
""");
    }

    public override async Task Select_mathf_truncate(bool async)
    {
        // Fails: MathF mapping issue EF-237
        Assert.Contains(
            "Expression not supported",
            (await Assert.ThrowsAsync<ExpressionNotSupportedException>(() =>
                base.Select_mathf_truncate(async))).Message);

        AssertMql(
            """
OrderDetails.
""");
    }

    public override async Task Where_mathf_exp(bool async)
    {
        // Fails: MathF mapping issue EF-237
        Assert.Contains(
            "Expression not supported",
            (await Assert.ThrowsAsync<ExpressionNotSupportedException>(() =>
                base.Where_mathf_exp(async))).Message);

        AssertMql(
            """
OrderDetails.
""");
    }

    public override async Task Where_mathf_log10(bool async)
    {
        // Fails: MathF mapping issue EF-237
        Assert.Contains(
            "Expression not supported",
            (await Assert.ThrowsAsync<ExpressionNotSupportedException>(() =>
                base.Where_mathf_log10(async))).Message);

        AssertMql(
            """
OrderDetails.
""");
    }

    public override async Task Where_mathf_log(bool async)
    {
        // Fails: MathF mapping issue EF-237
        Assert.Contains(
            "Expression not supported",
            (await Assert.ThrowsAsync<ExpressionNotSupportedException>(() =>
                base.Where_mathf_log(async))).Message);

        AssertMql(
            """
OrderDetails.
""");
    }

    public override async Task Where_mathf_log_new_base(bool async)
    {
        // Fails: MathF mapping issue EF-237
        Assert.Contains(
            "Expression not supported",
            (await Assert.ThrowsAsync<ExpressionNotSupportedException>(() =>
                base.Where_mathf_log_new_base(async))).Message);

        AssertMql(
            """
OrderDetails.
""");
    }

    public override async Task Where_mathf_sqrt(bool async)
    {
        // Fails: MathF mapping issue EF-237
        Assert.Contains(
            "Expression not supported",
            (await Assert.ThrowsAsync<ExpressionNotSupportedException>(() =>
                base.Where_mathf_sqrt(async))).Message);

        AssertMql(
            """
OrderDetails.
""");
    }

    public override async Task Where_mathf_acos(bool async)
    {
        // Fails: MathF mapping issue EF-237
        Assert.Contains(
            "Expression not supported",
            (await Assert.ThrowsAsync<ExpressionNotSupportedException>(() =>
                base.Where_mathf_acos(async))).Message);

        AssertMql(
            """
OrderDetails.
""");
    }

    public override async Task Where_mathf_asin(bool async)
    {
        // Fails: MathF mapping issue EF-237
        Assert.Contains(
            "Expression not supported",
            (await Assert.ThrowsAsync<ExpressionNotSupportedException>(() =>
                base.Where_mathf_asin(async))).Message);

        AssertMql(
            """
OrderDetails.
""");
    }

    public override async Task Where_mathf_atan(bool async)
    {
        // Fails: MathF mapping issue EF-237
        Assert.Contains(
            "Expression not supported",
            (await Assert.ThrowsAsync<ExpressionNotSupportedException>(() =>
                base.Where_mathf_atan(async))).Message);

        AssertMql(
            """
OrderDetails.
""");
    }

    public override async Task Where_mathf_atan2(bool async)
    {
        // Fails: MathF mapping issue EF-237
        Assert.Contains(
            "Expression not supported",
            (await Assert.ThrowsAsync<ExpressionNotSupportedException>(() =>
                base.Where_mathf_atan2(async))).Message);

        AssertMql(
            """
OrderDetails.
""");
    }

    public override async Task Where_mathf_cos(bool async)
    {
        // Fails: MathF mapping issue EF-237
        Assert.Contains(
            "Expression not supported",
            (await Assert.ThrowsAsync<ExpressionNotSupportedException>(() =>
                base.Where_mathf_cos(async))).Message);

        AssertMql(
            """
OrderDetails.
""");
    }

    public override async Task Where_mathf_sin(bool async)
    {
        // Fails: MathF mapping issue EF-237
        Assert.Contains(
            "Expression not supported",
            (await Assert.ThrowsAsync<ExpressionNotSupportedException>(() =>
                base.Where_mathf_sin(async))).Message);

        AssertMql(
            """
OrderDetails.
""");
    }

    public override async Task Where_mathf_tan(bool async)
    {
        // Fails: MathF mapping issue EF-237
        Assert.Contains(
            "Expression not supported",
            (await Assert.ThrowsAsync<ExpressionNotSupportedException>(() =>
                base.Where_mathf_tan(async))).Message);

        AssertMql(
            """
OrderDetails.
""");
    }

    public override async Task Where_mathf_sign(bool async)
    {
        // Fails: MathF mapping issue EF-237
        Assert.Contains(
            "Expression not supported",
            (await Assert.ThrowsAsync<ExpressionNotSupportedException>(() =>
                base.Where_mathf_sign(async))).Message);

        AssertMql(
            """
OrderDetails.
""");
    }

    public override async Task Where_mathf_degrees(bool async)
    {
        // Fails: MathF mapping issue EF-237
        Assert.Contains(
            "Expression not supported",
            (await Assert.ThrowsAsync<ExpressionNotSupportedException>(() =>
                base.Where_mathf_degrees(async))).Message);

        AssertMql(
            """
OrderDetails.
""");
    }

    public override async Task Where_mathf_radians(bool async)
    {
        // Fails: MathF mapping issue EF-237
        Assert.Contains(
            "Expression not supported",
            (await Assert.ThrowsAsync<ExpressionNotSupportedException>(() =>
                base.Where_mathf_radians(async))).Message);

        AssertMql(
            """
OrderDetails.
""");
    }

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

    public override async Task Convert_ToBoolean(bool async)
    {
        // Fails: Translate Convert methods issue EF-235
        Assert.Contains(
            "Expression not supported: ToBoolean(",
            (await Assert.ThrowsAsync<ExpressionNotSupportedException>(() => base.Convert_ToBoolean(async))).Message);

        AssertMql(
            """
Orders.
""");
    }

    public override async Task Convert_ToByte(bool async)
    {
        // Fails: Translate Convert methods issue EF-235
        Assert.Contains(
            "Expression not supported: ToByte(",
            (await Assert.ThrowsAsync<ExpressionNotSupportedException>(() => base.Convert_ToByte(async))).Message);

        AssertMql(
            """
Orders.
""");
    }

    public override async Task Convert_ToDecimal(bool async)
    {
        // Fails: Translate Convert methods issue EF-235
        Assert.Contains(
            "Expression not supported: ToDecimal(",
            (await Assert.ThrowsAsync<ExpressionNotSupportedException>(() => base.Convert_ToDecimal(async))).Message);

        AssertMql(
            """
Orders.
""");
    }

    public override async Task Convert_ToDouble(bool async)
    {
        // Fails: Translate Convert methods issue EF-235
        Assert.Contains(
            "Expression not supported: ToDouble(",
            (await Assert.ThrowsAsync<ExpressionNotSupportedException>(() => base.Convert_ToDouble(async))).Message);

        AssertMql(
            """
Orders.
""");
    }

    public override async Task Convert_ToInt16(bool async)
    {
        // Fails: Translate Convert methods issue EF-235
        Assert.Contains(
            "Expression not supported: ToInt16(",
            (await Assert.ThrowsAsync<ExpressionNotSupportedException>(() => base.Convert_ToInt16(async))).Message);

        AssertMql(
            """
Orders.
""");
    }

    public override async Task Convert_ToInt32(bool async)
    {
        // Fails: Translate Convert methods issue EF-235
        Assert.Contains(
            "Expression not supported: ToInt32(",
            (await Assert.ThrowsAsync<ExpressionNotSupportedException>(() => base.Convert_ToInt32(async))).Message);

        AssertMql(
            """
Orders.
""");
    }

    public override async Task Convert_ToInt64(bool async)
    {
        // Fails: Translate Convert methods issue EF-235
        Assert.Contains(
            "Expression not supported: ToInt64(",
            (await Assert.ThrowsAsync<ExpressionNotSupportedException>(() => base.Convert_ToInt64(async))).Message);

        AssertMql(
            """
Orders.
""");
    }

    public override async Task Convert_ToString(bool async)
    {
        // Fails: Translate Convert methods issue EF-235
        Assert.Contains(
            "Expression not supported: ToString(",
            (await Assert.ThrowsAsync<ExpressionNotSupportedException>(() => base.Convert_ToString(async))).Message);

        AssertMql(
            """
Orders.
""");
    }

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

    public override async Task Replace_with_emptystring(bool async)
    {
        // Fails: Translate string.Replace methods issue EF-223
        Assert.Contains(
            "Expression not supported",
            (await Assert.ThrowsAsync<ExpressionNotSupportedException>(
                () => base.Replace_with_emptystring(async))).Message);

        AssertMql(
            """
Customers.
""");
    }

    public override async Task Replace_using_property_arguments(bool async)
    {
        // Fails: Translate string.Replace methods issue EF-223
        Assert.Contains(
            "Expression not supported",
            (await Assert.ThrowsAsync<ExpressionNotSupportedException>(
                () => base.Replace_using_property_arguments(async))).Message);

        AssertMql(
            """
Customers.
""");
    }

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

    public override async Task TrimStart_without_arguments_in_predicate(bool async)
    {
        // Fails: Translate string.Trim methods issue EF-241
        Assert.Contains(
            "Expression not supported",
            (await Assert.ThrowsAsync<ExpressionNotSupportedException>(
                () => base.TrimStart_without_arguments_in_predicate(async))).Message);

        AssertMql(
            """
Customers.
""");
    }

    public override async Task TrimStart_with_char_argument_in_predicate(bool async)
    {
        // Fails: Translate string.Trim methods issue EF-241
        Assert.Contains(
            "Expression not supported",
            (await Assert.ThrowsAsync<ExpressionNotSupportedException>(
                () => base.TrimStart_without_arguments_in_predicate(async))).Message);

        AssertMql(
            """
Customers.
""");
    }

    public override async Task TrimStart_with_char_array_argument_in_predicate(bool async)
    {
        await base.TrimStart_with_char_array_argument_in_predicate(async);

        AssertMql(
            """
Customers.{ "$match" : { "ContactTitle" : { "$regularExpression" : { "pattern" : "^[Ow]*(?=[^Ow])ner$", "options" : "s" } } } }
""");
    }

    public override async Task TrimEnd_without_arguments_in_predicate(bool async)
    {
        // Fails: Translate string.Trim methods issue EF-241
        Assert.Contains(
            "Expression not supported",
            (await Assert.ThrowsAsync<ExpressionNotSupportedException>(
                () => base.TrimEnd_without_arguments_in_predicate(async))).Message);

        AssertMql(
            """
Customers.
""");
    }

    public override async Task TrimEnd_with_char_argument_in_predicate(bool async)
    {
        // Fails: Translate string.Trim methods issue EF-241
        Assert.Contains(
            "Expression not supported",
            (await Assert.ThrowsAsync<ExpressionNotSupportedException>(
                () => base.TrimEnd_with_char_argument_in_predicate(async))).Message);

        AssertMql(
            """
Customers.
""");
    }

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

    public override async Task Trim_with_char_argument_in_predicate(bool async)
    {
        // Fails: Translate string.Trim methods issue EF-241
        Assert.Contains(
            "Expression not supported",
            (await Assert.ThrowsAsync<ExpressionNotSupportedException>(
                () => base.Trim_with_char_argument_in_predicate(async))).Message);

        AssertMql(
            """
Customers.
""");
    }

    public override async Task Trim_with_char_array_argument_in_predicate(bool async)
    {
        // Fails: Translate string.Trim methods issue EF-241
        Assert.Contains(
            "Expression not supported",
            (await Assert.ThrowsAsync<ExpressionNotSupportedException>(
                () => base.Trim_with_char_argument_in_predicate(async))).Message);

        AssertMql(
            """
Customers.
""");
    }

    public override async Task Order_by_length_twice(bool async)
    {
        await base.Order_by_length_twice(async);

        AssertMql(
            """
Customers.{ "$project" : { "_id" : 0, "_document" : "$$ROOT", "_key1" : { "$strLenCP" : "$_id" }, "_key2" : { "$strLenCP" : "$_id" } } }, { "$sort" : { "_key1" : 1, "_key2" : 1, "_document._id" : 1 } }, { "$replaceRoot" : { "newRoot" : "$_document" } }
""");
    }

    public override async Task Order_by_length_twice_followed_by_projection_of_naked_collection_navigation(bool async)
    {
        // Fails: Projections issue EF-76
        await AssertTranslationFailed(() => base.Order_by_length_twice_followed_by_projection_of_naked_collection_navigation(async));

        AssertMql(
);
    }

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
        // Fails: MongoDB DateTimeKind handling
        var arg = new DateTime(1996, 7, 4, 0, 0, 0, DateTimeKind.Utc);

        await AssertQuery(
            async,
            ss => ss.Set<Order>().Where(o => Equals(o.OrderDate, arg)));

        AssertMql(
            """
Orders.{ "$match" : { "OrderDate" : { "$date" : "1996-07-04T00:00:00Z" } } }
""");
    }

    public override async Task Static_equals_int_compared_to_long(bool async)
    {
        // Fails: Equals with different types issue EF-221
        Assert.Contains(
            "Unable to cast object of type 'System.Int",
            (await Assert.ThrowsAsync<InvalidCastException>(() => base.Static_equals_int_compared_to_long(async)))
            .Message);

        AssertMql(
            """
Orders.
""");
    }

    public override async Task Where_DateOnly_FromDateTime(bool async)
    {
        // Fails: DateOnly support issue EF-242
        Assert.Contains(
            "Expression not supported: FromDateTime",
            (await Assert.ThrowsAsync<ExpressionNotSupportedException>(
                () => base.Where_DateOnly_FromDateTime(async))).Message);

        AssertMql(
            """
Orders.
""");
    }

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

    public override async Task Regex_IsMatch_MethodCall_constant_input(bool async)
    {
        // Fails Regex with non-constant pattern issue EF-247
        Assert.Contains(
            "Expression not supported: IsMatch",
            (await Assert.ThrowsAsync<ExpressionNotSupportedException>(() =>
                base.Regex_IsMatch_MethodCall_constant_input(async))).Message);

        AssertMql(
            """
Customers.
""");
    }

    public override async Task Datetime_subtraction_TotalDays(bool async)
    {
        // Fails: DateTime subtraction issue EF-246
        Assert.Contains(
            "Expression not supported: (o.OrderDate.Value",
            (await Assert.ThrowsAsync<ExpressionNotSupportedException>(() =>
                base.Datetime_subtraction_TotalDays(async))).Message);

        AssertMql(
            """
Orders.
""");
    }

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

    public override async Task String_Contains_with_StringComparison_Ordinal(bool async)
    {
        // Fails: StartsWith/Contains/EndsWith Ordinal/OrdinalIgnoreCase issue EF-243
        Assert.Contains(
            "Expression not supported",
            (await Assert.ThrowsAsync<ExpressionNotSupportedException>(() =>
                base.String_Contains_with_StringComparison_Ordinal(async))).Message);

        AssertMql(
            """
Customers.
""");
    }

    public override async Task String_Contains_with_StringComparison_OrdinalIgnoreCase(bool async)
    {
        // Fails: StartsWith/Contains/EndsWith Ordinal/OrdinalIgnoreCase issue EF-243
        Assert.Contains(
            "Expression not supported",
            (await Assert.ThrowsAsync<ExpressionNotSupportedException>(() =>
                base.String_Contains_with_StringComparison_OrdinalIgnoreCase(async))).Message);

        AssertMql(
            """
Customers.
""");
    }

    public override async Task String_Contains_with_StringComparison_unsupported(bool async)
    {
        // These are translated by the Mongo provider. See EF-243.
        await AssertQuery(async, ss => ss.Set<Customer>().Where(c => c.ContactName.Contains("M", StringComparison.CurrentCulture)));
        await AssertQuery(async, ss => ss.Set<Customer>().Where(c => c.ContactName.Contains("m", StringComparison.CurrentCultureIgnoreCase)));

        // Fails: StartsWith/Contains/EndsWith Ordinal/OrdinalIgnoreCase issue EF-243
        Assert.Contains(
            "Expression not supported",
            (await Assert.ThrowsAsync<ExpressionNotSupportedException>(() =>
                AssertQuery(async, ss => ss.Set<Customer>().Where(c => c.ContactName.Contains("M", StringComparison.InvariantCulture))))).Message);

        Assert.Contains(
            "Expression not supported",
            (await Assert.ThrowsAsync<ExpressionNotSupportedException>(() =>
                AssertQuery(async, ss => ss.Set<Customer>().Where(c => c.ContactName.Contains("M", StringComparison.InvariantCultureIgnoreCase))))).Message);

        AssertMql(
            """
Customers.{ "$match" : { "ContactName" : { "$regularExpression" : { "pattern" : "M", "options" : "s" } } } }
""",
            //
            """
Customers.{ "$match" : { "ContactName" : { "$regularExpression" : { "pattern" : "m", "options" : "is" } } } }
""",
            //
            """
Customers.
""",
            //
            """
Customers.
""");
    }

    #endif

    private void AssertMql(params string[] expected)
        => Fixture.TestMqlLoggerFactory.AssertBaseline(expected);

    protected override void ClearLog()
        => Fixture.TestMqlLoggerFactory.Clear();
}
