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
using Microsoft.EntityFrameworkCore.TestUtilities;

namespace MongoDB.EntityFrameworkCore.SpecificationTests.Query;

public class NorthwindAsTrackingQueryMongoTest : NorthwindAsTrackingQueryTestBase<NorthwindQueryMongoFixture<NoopModelCustomizer>>
{
    public NorthwindAsTrackingQueryMongoTest(NorthwindQueryMongoFixture<NoopModelCustomizer> fixture)
        : base(fixture)
        => Fixture.TestMqlLoggerFactory.Clear();

    [ConditionalFact]
    public virtual void Check_all_tests_overridden()
        => TestHelpers.AssertAllMethodsOverridden(GetType());

    [ConditionalFact(Skip = "Cross-document navigation access issue EF-216")]
    public override void Applied_to_body_clause()
    {
    }

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), InlineData(false), InlineData(true)]
    public override void Entity_added_to_state_manager(bool _)
    {
    }

    [ConditionalFact(Skip = "Cross-document navigation access issue EF-216")]
    public override void Applied_to_projection()
    {
    }

    [ConditionalFact(Skip = "Cross-document navigation access issue EF-216")]
    public override void Applied_to_multiple_body_clauses()
    {
    }

    [ConditionalFact(Skip = "Cross-document navigation access issue EF-216")]
    public override void Applied_to_body_clause_with_projection()
    {
    }
}
