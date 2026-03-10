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

public class NorthwindQueryTaggingQueryMongoTest : NorthwindQueryTaggingQueryTestBase<NorthwindQueryMongoFixture<NoopModelCustomizer>>
{
    public NorthwindQueryTaggingQueryMongoTest(
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

    [ConditionalFact(Skip = "TagWith EF-153")]
    public override void Single_query_tag()
    {
    }

    [ConditionalFact(Skip = "TagWith EF-153")]
    public override void Single_query_multiple_tags()
    {
    }

    [ConditionalFact(Skip = "TagWith EF-153")]
    public override void Tags_on_subquery()
    {
    }

    [ConditionalFact(Skip = "TagWith EF-153")]
    public override void Duplicate_tags()
    {
    }

    [ConditionalFact(Skip = "TagWith EF-153")]
    public override void Tag_on_include_query()
    {
    }

    [ConditionalFact(Skip = "TagWith EF-153")]
    public override void Tag_on_scalar_query()
    {
    }

    [ConditionalFact(Skip = "TagWith EF-153")]
    public override void Single_query_multiline_tag()
    {
    }

    [ConditionalFact(Skip = "TagWith EF-153")]
    public override void Single_query_multiple_multiline_tag()
    {
    }

    [ConditionalFact(Skip = "TagWith EF-153")]
    public override void Single_query_multiline_tag_with_empty_lines()
    {
    }
}
