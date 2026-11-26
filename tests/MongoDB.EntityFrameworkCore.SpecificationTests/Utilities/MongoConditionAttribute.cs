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

#if EF11
using System.Reflection;
using Xunit.v3;
#else
using Microsoft.EntityFrameworkCore.TestUtilities.Xunit;
#endif
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;

namespace MongoDB.EntityFrameworkCore.SpecificationTests.Utilities;

#if EF11
// EF11 dropped ITestCondition, so a condition that is not met skips each test dynamically as it starts. Unlike the
// EF8-EF10 behaviour, class fixtures have already been created by then.
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class MongoConditionAttribute(MongoCondition conditions) : BeforeAfterTestAttribute
{
    public override void Before(MethodInfo methodUnderTest, IXunitTest test)
    {
        if (conditions.HasFlag(MongoCondition.IsAtlas))
        {
            Assert.SkipUnless(TestServer.SupportsAtlas, "Mongo tests skipped because: 'IsAtlas'");
        }
    }
}
#else
public sealed class MongoConditionAttribute(MongoCondition conditions) : Attribute, ITestCondition
{
    public ValueTask<bool> IsMetAsync()
    {
        var isMet = true;

        if (conditions.HasFlag(MongoCondition.IsAtlas))
        {
            isMet &= TestServer.SupportsAtlas;
        }

        return ValueTask.FromResult(isMet);
    }

    public string SkipReason
        => $"Mongo tests skipped because: '{string.Join(
            ", ", Enum.GetValues(typeof(MongoCondition))
                .Cast<Enum>()
                .Where(conditions.HasFlag)
                .Select(f => Enum.GetName(typeof(MongoCondition), f)))}'";
}
#endif
