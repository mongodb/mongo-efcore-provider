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

using System.Reflection;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.TestUtilities;
using MongoDB.Driver.Linq;
using Xunit.Abstractions;

namespace MongoDB.EntityFrameworkCore.SpecificationTests.Query;

public class NorthwindDbFunctionsQueryMongoTest
    : NorthwindDbFunctionsQueryTestBase<NorthwindQueryMongoFixture<NoopModelCustomizer>>
{
    public NorthwindDbFunctionsQueryMongoTest(
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

    public override async Task Like_literal(bool async)
    {
        // Fails: translation of Like issue EF-222
        Assert.Contains(
            "Expression not supported: value(Microsoft.EntityFrameworkCore.DbFunctions).Like",
            (await Assert.ThrowsAsync<ExpressionNotSupportedException>(() => base.Like_literal(async))).Message);

        AssertMql(
            """
Customers.
""");
    }

    public override async Task Like_identity(bool async)
    {
        // Fails: translation of Like issue EF-222
        Assert.Contains(
            "Expression not supported: value(Microsoft.EntityFrameworkCore.DbFunctions).Like",
            (await Assert.ThrowsAsync<ExpressionNotSupportedException>(() => base.Like_identity(async))).Message);

        AssertMql(
            """
Customers.
""");
    }

    public override async Task Like_literal_with_escape(bool async)
    {
        // Fails: translation of Like issue EF-222
        Assert.Contains(
            "Expression not supported: value(Microsoft.EntityFrameworkCore.DbFunctions).Like",
            (await Assert.ThrowsAsync<ExpressionNotSupportedException>(() => base.Like_literal_with_escape(async))).Message);

        AssertMql(
            """
Customers.
""");
    }

    public override async Task Like_all_literals(bool async)
    {
        // Fails: translation of Like issue EF-222
        Assert.Contains(
            "The 'Like' method is not supported because the query has switched to client-evaluation.",
            (await Assert.ThrowsAsync<TargetInvocationException>(() => base.Like_all_literals(async))).InnerException.Message);

        AssertMql(
            """
Customers.
""");
    }

    public override async Task Like_all_literals_with_escape(bool async)
    {
        // Fails: translation of Like issue EF-222
        Assert.Contains(
            "The 'Like' method is not supported because the query has switched to client-evaluation.",
            (await Assert.ThrowsAsync<TargetInvocationException>(() => base.Like_all_literals_with_escape(async))).InnerException.Message);

        AssertMql(
            """
Customers.
""");
    }

#if EF8 || EF9

    public override async Task Random_return_less_than_1(bool async)
    {
        // Fails: translation of Random issue EF-234
        Assert.Contains(
            "The 'Random' method is not supported because the query has switched to client-evaluation.",
            (await Assert.ThrowsAsync<TargetInvocationException>(() => base.Random_return_less_than_1(async))).InnerException.Message);

        AssertMql(
            """
Orders.
""");
    }

    public override async Task Random_return_greater_than_0(bool async)
    {
        // Fails: translation of Random issue EF-234
        Assert.Contains(
            "The 'Random' method is not supported because the query has switched to client-evaluation.",
            (await Assert.ThrowsAsync<TargetInvocationException>(() => base.Random_return_greater_than_0(async))).InnerException.Message);

        AssertMql(
            """
Orders.
""");
    }

#endif

    private void AssertMql(params string[] expected)
        => Fixture.TestMqlLoggerFactory.AssertBaseline(expected);
}
