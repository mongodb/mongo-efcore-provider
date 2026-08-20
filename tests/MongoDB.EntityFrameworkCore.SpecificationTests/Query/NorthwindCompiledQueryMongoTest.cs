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
using Xunit.Abstractions;

namespace MongoDB.EntityFrameworkCore.SpecificationTests.Query;

public class NorthwindCompiledQueryMongoTest : NorthwindCompiledQueryTestBase<NorthwindQueryMongoFixture<NoopModelCustomizer>>
{
    public NorthwindCompiledQueryMongoTest(
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

    public override void DbSet_query()
    {
        base.DbSet_query();

        AssertMql(
            """
Customers.
""",
            //
            """
Customers.
""");
    }

    public override void DbSet_query_first()
    {
        base.DbSet_query_first();

        AssertMql(
            """
Customers.{ "$sort" : { "_id" : 1 } }, { "$limit" : 1 }
""");
    }

    public override void Query_ending_with_include()
    {
        base.Query_ending_with_include();
        AssertMql(
            """
Customers.{ "$lookup" : { "from" : "Orders", "localField" : "_id", "foreignField" : "CustomerID", "as" : "_lookup_Orders" } }
""",
            //
            """
Customers.{ "$lookup" : { "from" : "Orders", "localField" : "_id", "foreignField" : "CustomerID", "as" : "_lookup_Orders" } }
""");
    }

    public override void Untyped_context()
    {
        base.Untyped_context();

        AssertMql(
            """
Customers.
""",
            //
            """
Customers.
""");
    }

    public override void Query_with_single_parameter()
    {
        base.Query_with_single_parameter();

        AssertMql(
            """
Customers.{ "$match" : { "_id" : "ALFKI" } }
""",
            //
            """
Customers.{ "$match" : { "_id" : "ANATR" } }
""");
    }

    public override void First_query_with_single_parameter()
    {
        base.First_query_with_single_parameter();

        AssertMql(
            """
Customers.{ "$match" : { "_id" : "ALFKI" } }, { "$limit" : 1 }
""",
            //
            """
Customers.{ "$match" : { "_id" : "ANATR" } }, { "$limit" : 1 }
""");
    }

    public override void Query_with_two_parameters()
    {
        base.Query_with_two_parameters();

        AssertMql(
            """
Customers.{ "$match" : { "_id" : "ALFKI" } }
""",
            //
            """
Customers.{ "$match" : { "_id" : "ANATR" } }
""");
    }

    public override void Query_with_three_parameters()
    {
        base.Query_with_three_parameters();

        AssertMql(
            """
Customers.{ "$match" : { "_id" : "ALFKI" } }
""",
            //
            """
Customers.{ "$match" : { "_id" : "ANATR" } }
""");
    }

    public override void Query_with_contains()
    {
        base.Query_with_contains();

        AssertMql(
            """
Customers.{ "$match" : { "_id" : { "$in" : ["ALFKI"] } } }
""",
            //
            """
Customers.{ "$match" : { "_id" : { "$in" : ["ANATR"] } } }
""");
    }

    public override void Query_with_closure()
    {
        base.Query_with_closure();

        AssertMql(
            """
Customers.{ "$match" : { "_id" : "ALFKI" } }
""",
            //
            """
Customers.{ "$match" : { "_id" : "ALFKI" } }
""");
    }

    public override void Compiled_query_when_does_not_end_in_query_operator()
    {
        base.Compiled_query_when_does_not_end_in_query_operator();

        AssertMql(
            """
Customers.{ "$match" : { "_id" : "ALFKI" } }, { "$count" : "v" }
""");
    }

    public override async Task Compiled_query_with_max_parameters()
    {
        await base.Compiled_query_with_max_parameters();
        AssertMql(
            """
Customers.{ "$match" : { "$or" : [{ "_id" : "ALFKI" }, { "_id" : "ANATR" }, { "_id" : "ANTON" }, { "_id" : "AROUT" }, { "_id" : "BERGS" }, { "_id" : "BLAUS" }, { "_id" : "BLONP" }, { "_id" : "BOLID" }, { "_id" : "BONAP" }, { "_id" : "BSBEV" }, { "_id" : "CACTU" }, { "_id" : "CENTC" }, { "_id" : "CHOPS" }, { "_id" : "CONSH" }, { "_id" : "RANDM" }] } }
""",
            //
            """
Customers.{ "$match" : { "$or" : [{ "_id" : "ALFKI" }, { "_id" : "ANATR" }, { "_id" : "ANTON" }, { "_id" : "AROUT" }, { "_id" : "BERGS" }, { "_id" : "BLAUS" }, { "_id" : "BLONP" }, { "_id" : "BOLID" }, { "_id" : "BONAP" }, { "_id" : "BSBEV" }, { "_id" : "CACTU" }, { "_id" : "CENTC" }, { "_id" : "CHOPS" }, { "_id" : "CONSH" }, { "_id" : "RANDM" }] } }, { "$lookup" : { "from" : "Orders", "localField" : "_id", "foreignField" : "CustomerID", "as" : "_lookup_Orders" } }
""",
            //
            """
Customers.{ "$match" : { "$or" : [{ "_id" : "ALFKI" }, { "_id" : "ANATR" }, { "_id" : "ANTON" }, { "_id" : "AROUT" }, { "_id" : "BERGS" }, { "_id" : "BLAUS" }, { "_id" : "BLONP" }, { "_id" : "BOLID" }, { "_id" : "BONAP" }, { "_id" : "BSBEV" }, { "_id" : "CACTU" }, { "_id" : "CENTC" }, { "_id" : "CHOPS" }, { "_id" : "CONSH" }, { "_id" : "RANDM" }] } }, { "$count" : "v" }
""",
            //
            """
Customers.{ "$match" : { "$or" : [{ "_id" : "ALFKI" }, { "_id" : "ANATR" }, { "_id" : "ANTON" }, { "_id" : "AROUT" }, { "_id" : "BERGS" }, { "_id" : "BLAUS" }, { "_id" : "BLONP" }, { "_id" : "BOLID" }, { "_id" : "BONAP" }, { "_id" : "BSBEV" }, { "_id" : "CACTU" }, { "_id" : "CENTC" }, { "_id" : "CHOPS" }, { "_id" : "CONSH" }, { "_id" : "RANDM" }] } }
""",
            //
            """
Customers.{ "$match" : { "$or" : [{ "_id" : "ALFKI" }, { "_id" : "ANATR" }, { "_id" : "ANTON" }, { "_id" : "AROUT" }, { "_id" : "BERGS" }, { "_id" : "BLAUS" }, { "_id" : "BLONP" }, { "_id" : "BOLID" }, { "_id" : "BONAP" }, { "_id" : "BSBEV" }, { "_id" : "CACTU" }, { "_id" : "CENTC" }, { "_id" : "CHOPS" }, { "_id" : "CONSH" }, { "_id" : "RANDM" }] } }, { "$lookup" : { "from" : "Orders", "localField" : "_id", "foreignField" : "CustomerID", "as" : "_lookup_Orders" } }
""",
            //
            """
Customers.{ "$match" : { "$or" : [{ "_id" : "ALFKI" }, { "_id" : "ANATR" }, { "_id" : "ANTON" }, { "_id" : "AROUT" }, { "_id" : "BERGS" }, { "_id" : "BLAUS" }, { "_id" : "BLONP" }, { "_id" : "BOLID" }, { "_id" : "BONAP" }, { "_id" : "BSBEV" }, { "_id" : "CACTU" }, { "_id" : "CENTC" }, { "_id" : "CHOPS" }, { "_id" : "CONSH" }, { "_id" : "RANDM" }] } }, { "$count" : "v" }
""",
            //
            """
Customers.{ "$match" : { "$or" : [{ "_id" : "ALFKI" }, { "_id" : "ANATR" }, { "_id" : "ANTON" }, { "_id" : "AROUT" }, { "_id" : "BERGS" }, { "_id" : "BLAUS" }, { "_id" : "BLONP" }, { "_id" : "BOLID" }, { "_id" : "BONAP" }, { "_id" : "BSBEV" }, { "_id" : "CACTU" }, { "_id" : "CENTC" }, { "_id" : "CHOPS" }, { "_id" : "CONSH" }] } }, { "$count" : "v" }
""");
    }

    public override void Query_with_array_parameter()
    {
        base.Query_with_array_parameter();

        AssertMql(
            """
Customers.{ "$match" : { "_id" : "ALFKI" } }
""",
            //
            """
Customers.{ "$match" : { "_id" : "ANATR" } }
""");
    }

    public override async Task Query_with_array_parameter_async()
    {
        await base.Query_with_array_parameter_async();

        AssertMql(
            """
Customers.{ "$match" : { "_id" : "ALFKI" } }
""",
            //
            """
Customers.{ "$match" : { "_id" : "ANATR" } }
""");
    }

    // Fails: Cross-document navigation access issue EF-216
    //
    // EF-322 step 3a re-baseline. This is a MESSAGE change on a shape that is unsupported either way — the
    // exception TYPE is still InvalidOperationException and the query still produces no data in any
    // MongoQueryMode — so what follows is where it is now raised, traced rather than guessed.
    //
    // `Multiple_queries` compiles `<subquery1> + <subquery2>`, so the top-level expression handed to
    // QueryCompilationContext is a BinaryExpression, NOT a ShapedQueryExpression. MongoQueryTranslationPostprocessor
    // .Process calls ApplyProjection() only when the top-level expression IS a ShapedQueryExpression, so neither
    // sub-query's ProjectionMember mapping is ever rewritten to its Constant(index) form. Before step 3a a bare
    // scalar projection populated no native Projection, Route was Fallback, that missing mapping was never read,
    // and the driver-LINQ bridge got far enough to raise its own explanatory "Unsupported cross-DbSet query
    // between ..." error. With step 3a the bare projection IS pushed down, Route is Projection, and
    // MongoProjectionBindingRemovingExpressionVisitor.GetProjectionIndex reads the un-rewritten mapping first —
    // throwing the PARAMETERLESS InvalidOperationException from ExpressionExtensionMethods.GetConstantValue<int>
    // ("Operation is not valid due to the current state of the object") before the bridge is reached.
    //
    // So the postprocessor's single-ShapedQueryExpression assumption is a PRE-EXISTING gap that step 3a merely
    // makes reachable. Widening it is deliberately NOT done here: applying the projection to every shaped query
    // in the tree would very likely make this query SUCCEED (the two sub-queries are independent
    // single-collection natives with no cross-DbSet bridging left to do), which is a different test and a
    // separate, measured change. Until then this asserts the type and the shape of the failure, not the message,
    // so it cannot be quietly satisfied by some third unrelated InvalidOperationException.
    public override void Multiple_queries()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => base.Multiple_queries());

        // The frame, not the message: the parameterless InvalidOperationException carries no text of its own
        // ("Operation is not valid due to the current state of the object"), so naming the throw site is the
        // only way to distinguish this from an unrelated InvalidOperationException — in particular from the
        // driver-LINQ bridge's own "Unsupported cross-DbSet query between ..." guard, which is what this
        // query used to reach and what it would reach again if the projection stopped being pushed down.
        Assert.Contains("GetConstantValue", exception.StackTrace);
        Assert.DoesNotContain("Unsupported cross-DbSet query", exception.Message);
    }

    public override void Compiled_query_when_using_member_on_context()
    {
        #if EF9 // XUnit assembly loading issue

        base.Compiled_query_when_using_member_on_context();

        AssertMql(
            """
Customers.{ "$match" : { "_id" : { "$regularExpression" : { "pattern" : "^A", "options" : "s" } } } }
""",
            //
            """
Customers.{ "$match" : { "_id" : { "$regularExpression" : { "pattern" : "^A", "options" : "s" } } } }
""");

        #endif
    }

    public override async Task First_query_with_cancellation_async()
    {
        await base.First_query_with_cancellation_async();

        AssertMql(
            """
Customers.{ "$match" : { "_id" : "ALFKI" } }, { "$limit" : 1 }
""",
            //
            """
Customers.{ "$match" : { "_id" : "ANATR" } }, { "$limit" : 1 }
""");
    }

    public override async Task DbSet_query_first_async()
    {
        await base.DbSet_query_first_async();

        AssertMql(
            """
Customers.{ "$sort" : { "_id" : 1 } }, { "$limit" : 1 }
""");
    }

    public override async Task First_query_with_single_parameter_async()
    {
        await base.First_query_with_single_parameter_async();

        AssertMql(
            """
Customers.{ "$match" : { "_id" : "ALFKI" } }, { "$limit" : 1 }
""",
            //
            """
Customers.{ "$match" : { "_id" : "ANATR" } }, { "$limit" : 1 }
""");
    }

    public override async Task Keyless_query_first_async()
    {
        await base.Keyless_query_first_async();

        AssertMql(
            """
Customers.{ "$sort" : { "CompanyName" : 1 } }, { "$limit" : 1 }
""");
    }

    public override async Task Query_with_closure_async_null()
    {
        await base.Query_with_closure_async_null();

        AssertMql(
            """
Customers.{ "$match" : { "_id" : null } }
""");
    }

    public override async Task Query_with_three_parameters_async()
    {
        await base.Query_with_three_parameters_async();

        AssertMql(
            """
Customers.{ "$match" : { "_id" : "ALFKI" } }
""",
            //
            """
Customers.{ "$match" : { "_id" : "ANATR" } }
""");
    }

    public override async Task Query_with_two_parameters_async()
    {
        await base.Query_with_two_parameters_async();

        AssertMql(
            """
Customers.{ "$match" : { "_id" : "ALFKI" } }
""",
            //
            """
Customers.{ "$match" : { "_id" : "ANATR" } }
""");
    }

    public override async Task Keyless_query_async()
    {
        await base.Keyless_query_async();

        AssertMql(
            """
Customers.
""",
            //
            """
Customers.
""");
    }

    public override async Task Query_with_single_parameter_async()
    {
        await base.Query_with_single_parameter_async();

        AssertMql(
            """
Customers.{ "$match" : { "_id" : "ALFKI" } }
""",
            //
            """
Customers.{ "$match" : { "_id" : "ANATR" } }
""");
    }

    public override void Keyless_query_first()
    {
        base.Keyless_query_first();

        AssertMql(
            """
Customers.{ "$sort" : { "CompanyName" : 1 } }, { "$limit" : 1 }
""");
    }

    public override void Query_with_closure_null()
    {
        base.Query_with_closure_null();

        AssertMql(
            """
Customers.{ "$match" : { "_id" : null } }
""");
    }

    public override async Task Query_with_closure_async()
    {
        await base.Query_with_closure_async();

        AssertMql(
            """
Customers.{ "$match" : { "_id" : "ALFKI" } }
""",
            //
            """
Customers.{ "$match" : { "_id" : "ALFKI" } }
""");
    }

    public override async Task Untyped_context_async()
    {
        await base.Untyped_context_async();

        AssertMql(
            """
Customers.
""",
            //
            """
Customers.
""");
    }

    public override async Task DbSet_query_async()
    {
        await base.DbSet_query_async();

        AssertMql(
            """
Customers.
""",
            //
            """
Customers.
""");
    }

    public override void Keyless_query()
    {
        base.Keyless_query();

        AssertMql(
            """
Customers.
""",
            //
            """
Customers.
""");
    }

    public override void Query_with_single_parameter_with_include()
    {
        base.Query_with_single_parameter_with_include();
        AssertMql(
            """
Customers.{ "$match" : { "_id" : "ALFKI" } }, { "$lookup" : { "from" : "Orders", "localField" : "_id", "foreignField" : "CustomerID", "as" : "_lookup_Orders" } }
""",
            //
            """
Customers.{ "$match" : { "_id" : "ANATR" } }, { "$lookup" : { "from" : "Orders", "localField" : "_id", "foreignField" : "CustomerID", "as" : "_lookup_Orders" } }
""");
    }

    private void AssertMql(params string[] expected)
        => Fixture.TestMqlLoggerFactory.AssertBaseline(expected);

}
