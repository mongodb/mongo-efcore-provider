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

    [ConditionalTheory(Skip = "Translation of Like issue EF-222"), MemberData(nameof(IsAsyncData))]
    public override Task Like_literal(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Translation of Like issue EF-222"), MemberData(nameof(IsAsyncData))]
    public override Task Like_identity(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Translation of Like issue EF-222"), MemberData(nameof(IsAsyncData))]
    public override Task Like_literal_with_escape(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Translation of Like issue EF-222"), MemberData(nameof(IsAsyncData))]
    public override Task Like_all_literals(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Translation of Like issue EF-222"), MemberData(nameof(IsAsyncData))]
    public override Task Like_all_literals_with_escape(bool _)
        => Task.CompletedTask;

#if EF8 || EF9

    [ConditionalTheory(Skip = "Translation of Random issue EF-234"), MemberData(nameof(IsAsyncData))]
    public override Task Random_return_less_than_1(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Translation of Random issue EF-234"), MemberData(nameof(IsAsyncData))]
    public override Task Random_return_greater_than_0(bool _)
        => Task.CompletedTask;

#endif

    private void AssertMql(params string[] expected)
        => Fixture.TestMqlLoggerFactory.AssertBaseline(expected);
}
