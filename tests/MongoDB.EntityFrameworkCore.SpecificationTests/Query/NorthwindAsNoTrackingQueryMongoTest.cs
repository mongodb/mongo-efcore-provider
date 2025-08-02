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

public class NorthwindAsNoTrackingQueryMongoTest : NorthwindAsNoTrackingQueryTestBase<
    NorthwindQueryMongoFixture<NoopModelCustomizer>>
{
    public NorthwindAsNoTrackingQueryMongoTest(
        NorthwindQueryMongoFixture<NoopModelCustomizer> fixture,
        ITestOutputHelper testOutputHelper)
        : base(fixture)
    {
        Fixture.TestMqlLoggerFactory.Clear();
        //Fixture.TestMqlLoggerFactory.SetTestOutputHelper(testOutputHelper);
    }

    [ConditionalFact]
    public virtual void Check_all_tests_overridden()
        => TestHelpers.AssertAllMethodsOverridden(GetType());

    public override async Task Can_get_current_values(bool async)
    {
        await base.Can_get_current_values(async);

        AssertMql(
            """
Customers.{ "$limit" : 1 }
""",
            //
            """
Customers.{ "$limit" : 1 }
""");
    }

    public override async Task Where_simple_shadow(bool async)
    {
        await base.Where_simple_shadow(async);

        AssertMql(
            """
Employees.{ "$match" : { "Title" : "Sales Representative" } }
""");
    }

    public override async Task Entity_not_added_to_state_manager(bool useParam, bool async)
    {
        await base.Entity_not_added_to_state_manager(useParam, async);

        AssertMql(
            """
Customers.
""");
    }

    public override async Task Query_fast_path_when_ctor_binding(bool async)
    {
        await base.Query_fast_path_when_ctor_binding(async);

        AssertMql(
            """
Customers.
""");
    }

    public override async Task Applied_to_multiple_body_clauses(bool async)
    {
        // Fails: Cross-document navigation access issue EF-216
        await AssertTranslationFailed(() => base.Applied_to_multiple_body_clauses(async));

        AssertMql(
);
    }

    public override async Task SelectMany_simple(bool async)
    {
        // Fails: Cross-document navigation access issue EF-216
        await AssertTranslationFailed(() => base.SelectMany_simple(async));

        AssertMql(
);
    }

    public override async Task Applied_after_navigation_expansion(bool async)
    {
        // Fails: Cross-document navigation access issue EF-216
        await AssertTranslationFailed(() => base.Applied_after_navigation_expansion(async));

        AssertMql(
);
    }

    public override async Task Include_reference_and_collection(bool async)
    {
        // Fails: Cross-document navigation access issue EF-216
        await AssertTranslationFailed(() => base.Include_reference_and_collection(async));

        AssertMql(
);
    }

    public override async Task Applied_to_body_clause(bool async)
    {
        // Fails: Cross-document navigation access issue EF-216
        await AssertTranslationFailed(() => base.Applied_to_body_clause(async));

        AssertMql(
);
    }

    public override async Task Applied_to_projection(bool async)
    {
        // Fails: Cross-document navigation access issue EF-216
        await AssertTranslationFailed(() => base.Applied_to_projection(async));

        AssertMql(
);
    }

    public override async Task Applied_to_body_clause_with_projection(bool async)
    {
        // Fails: Cross-document navigation access issue EF-216
        await AssertTranslationFailed(() => base.Applied_to_body_clause_with_projection(async));

        AssertMql(
);
    }

    private void AssertMql(params string[] expected)
        => Fixture.TestMqlLoggerFactory.AssertBaseline(expected);
}
