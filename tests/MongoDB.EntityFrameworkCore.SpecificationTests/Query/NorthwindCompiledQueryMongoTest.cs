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

    [ConditionalFact(Skip = "Include issue EF-117")]
    public override void Query_ending_with_include()
    {
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

    [ConditionalFact(Skip = "Compiled query with non-query operator issue EF-232")]
    public override void Compiled_query_when_does_not_end_in_query_operator()
    {
    }

    [ConditionalFact(Skip = "Include issue EF-117")]
    public override Task Compiled_query_with_max_parameters()
        => Task.CompletedTask;

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

    [ConditionalFact(Skip = "Cross-document navigation access issue EF-216")]
    public override void Multiple_queries()
    {
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

    [ConditionalFact(Skip = "Include issue EF-117")]
    public override void Query_with_single_parameter_with_include()
    {
    }

    private void AssertMql(params string[] expected)
        => Fixture.TestMqlLoggerFactory.AssertBaseline(expected);
}
