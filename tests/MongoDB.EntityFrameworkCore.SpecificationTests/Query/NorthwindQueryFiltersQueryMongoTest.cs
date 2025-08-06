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
using Xunit.Sdk;

namespace MongoDB.EntityFrameworkCore.SpecificationTests.Query;

public class NorthwindQueryFiltersQueryMongoTest
    : NorthwindQueryFiltersQueryTestBase<NorthwindQueryMongoFixture<NorthwindQueryFiltersCustomizer>>
{
    public NorthwindQueryFiltersQueryMongoTest(
        NorthwindQueryMongoFixture<NorthwindQueryFiltersCustomizer> fixture,
        ITestOutputHelper testOutputHelper)
        : base(fixture)
    {
        fixture.TestMqlLoggerFactory.Clear();
        //fixture.TestMqlLoggerFactory.SetTestOutputHelper(testOutputHelper);
    }

    [ConditionalFact]
    public virtual void Check_all_tests_overridden()
        => TestHelpers.AssertAllMethodsOverridden(GetType());

    public override async Task Count_query(bool async)
    {
        await base.Count_query(async);

        AssertMql(
            """
Customers.{ "$match" : { "CompanyName" : { "$regularExpression" : { "pattern" : "^B", "options" : "s" } } } }, { "$count" : "_v" }
""");
    }

    public override async Task Materialized_query(bool async)
    {
        await base.Materialized_query(async);

        AssertMql(
            """
Customers.{ "$match" : { "CompanyName" : { "$regularExpression" : { "pattern" : "^B", "options" : "s" } } } }
""");
    }

    public override async Task Find(bool async)
    {
        await base.Find(async);

        AssertMql(
            """
Customers.{ "$match" : { "CompanyName" : { "$regularExpression" : { "pattern" : "^B", "options" : "s" } } } }, { "$match" : { "_id" : "ALFKI" } }, { "$limit" : 1 }
""");
    }

    public override async Task Materialized_query_parameter(bool async)
    {
        await base.Materialized_query_parameter(async);

        AssertMql(
            """
Customers.{ "$match" : { "CompanyName" : { "$regularExpression" : { "pattern" : "^F", "options" : "s" } } } }
""");
    }

    public override async Task Materialized_query_parameter_new_context(bool async)
    {
        await base.Materialized_query_parameter_new_context(async);

        AssertMql(
            """
Customers.{ "$match" : { "CompanyName" : { "$regularExpression" : { "pattern" : "^B", "options" : "s" } } } }
""",
            //
            """
Customers.{ "$match" : { "CompanyName" : { "$regularExpression" : { "pattern" : "^T", "options" : "s" } } } }
""");
    }

    public override async Task Projection_query_parameter(bool async)
    {
        await base.Projection_query_parameter(async);

        AssertMql(
            """
Customers.{ "$match" : { "CompanyName" : { "$regularExpression" : { "pattern" : "^F", "options" : "s" } } } }, { "$project" : { "_v" : "$_id", "_id" : 0 } }
""");
    }

    public override async Task Projection_query(bool async)
    {
        await base.Projection_query(async);

        AssertMql(
            """
Customers.{ "$match" : { "CompanyName" : { "$regularExpression" : { "pattern" : "^B", "options" : "s" } } } }, { "$project" : { "_v" : "$_id", "_id" : 0 } }
""");
    }

    public override async Task Include_query(bool async)
    {
        // Fails: Include issue EF-117
        Assert.Contains(
            "Including navigation 'Navigation' is not supported",
            (await Assert.ThrowsAsync<InvalidOperationException>(() => base.Include_query(async))).Message);

        AssertMql(
);
    }

    public override async Task Include_query_opt_out(bool async)
    {
        // Fails: Include issue EF-117
        Assert.Contains(
            "Including navigation 'Navigation' is not supported",
            (await Assert.ThrowsAsync<InvalidOperationException>(() => base.Include_query_opt_out(async))).Message);

        AssertMql(
);
    }

    public override async Task Included_many_to_one_query(bool async)
    {
        // Fails: Include issue EF-117
        await AssertTranslationFailed(() => base.Included_many_to_one_query(async));

        AssertMql(
);
    }

    public override async Task Project_reference_that_itself_has_query_filter_with_another_reference(bool async)
    {
        // Fails: Include issue EF-117
        await AssertTranslationFailed(() => base.Project_reference_that_itself_has_query_filter_with_another_reference(async));

        AssertMql(
);
    }

    public override async Task Navs_query(bool async)
    {
        // Fails: Include issue EF-117
        await AssertTranslationFailed(() => base.Navs_query(async));

        AssertMql(
);
    }

    public override void Compiled_query()
    {
        base.Compiled_query();

        AssertMql(
            """
Customers.{ "$match" : { "CompanyName" : { "$regularExpression" : { "pattern" : "^B", "options" : "s" } } } }, { "$match" : { "_id" : "BERGS" } }
""",
            //
            """
Customers.{ "$match" : { "CompanyName" : { "$regularExpression" : { "pattern" : "^B", "options" : "s" } } } }, { "$match" : { "_id" : "BLAUS" } }
""");
    }

    public override async Task Entity_Equality(bool async)
    {
        // Fails: Entity equality issue EF-202
        await AssertTranslationFailed(() => base.Entity_Equality(async));

        AssertMql(
);
    }

    public override async Task Client_eval(bool async)
    {
        // Fails: Does not throw expected unable to translate exception
        Assert.Contains(
            "Actual:   typeof(MongoDB.Driver.Linq.ExpressionNotSupportedException)",
            (await Assert.ThrowsAsync<ThrowsException>(() => base.Client_eval(async))).Message);

        AssertMql(
            """
Products.
""");
    }

    public override async Task Included_many_to_one_query2(bool async)
    {
        // Fails: Include issue EF-117
        await AssertTranslationFailed(() => base.Included_many_to_one_query2(async));

        AssertMql(
);
    }

    public override async Task Included_one_to_many_query_with_client_eval(bool async)
    {
        // Fails: Include issue EF-117
        Assert.Contains(
            "Actual:   \"Including navigation 'Navigation' is not ",
            (await Assert.ThrowsAsync<EqualException>(() => base.Included_one_to_many_query_with_client_eval(async))).Message);

        AssertMql();
    }

    private void AssertMql(params string[] expected)
        => Fixture.TestMqlLoggerFactory.AssertBaseline(expected);
}
