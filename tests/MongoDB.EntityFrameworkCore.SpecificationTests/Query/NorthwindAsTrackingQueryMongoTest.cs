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

    public override void Applied_to_body_clause()
    {
        base.Applied_to_body_clause();

        AssertMql(
"""
Customers.{ "$project" : { "_outer" : "$$ROOT", "_id" : 0 } }, { "$lookup" : { "from" : "Orders", "localField" : "_outer._id", "foreignField" : "CustomerID", "as" : "_inner" } }, { "$unwind" : "$_inner" }, { "$project" : { "_outer" : "$_outer", "_inner" : "$_inner", "_id" : 0 } }, { "$match" : { "_outer._id" : "ALFKI" } }
""");
    }

    public override void Entity_added_to_state_manager(bool useParam)
    {
        base.Entity_added_to_state_manager(useParam);

        AssertMql(
            """
Customers.
""");
    }

    public override void Applied_to_projection()
    {
        base.Applied_to_projection();

        AssertMql(
"""
Customers.{ "$project" : { "_outer" : "$$ROOT", "_id" : 0 } }, { "$lookup" : { "from" : "Orders", "localField" : "_outer._id", "foreignField" : "CustomerID", "as" : "_inner" } }, { "$unwind" : "$_inner" }, { "$project" : { "_outer" : "$_outer", "_inner" : "$_inner", "_id" : 0 } }, { "$match" : { "_outer._id" : "ALFKI" } }
""");
    }

    public override void Applied_to_multiple_body_clauses()
    {
        // Fails: Cross-document navigation access issue EF-216
        AssertTranslationFailed(() => base.Applied_to_multiple_body_clauses());

        AssertMql(
);
    }

    public override void Applied_to_body_clause_with_projection()
    {
        base.Applied_to_body_clause_with_projection();

        AssertMql(
"""
Customers.{ "$project" : { "_outer" : "$$ROOT", "_id" : 0 } }, { "$lookup" : { "from" : "Orders", "localField" : "_outer._id", "foreignField" : "CustomerID", "as" : "_inner" } }, { "$unwind" : "$_inner" }, { "$project" : { "_outer" : "$_outer", "_inner" : "$_inner", "_id" : 0 } }, { "$match" : { "_outer._id" : "ALFKI" } }
""");
    }

    private static void AssertTranslationFailed(Action query)
        => Assert.Contains(
            CoreStrings.TranslationFailed("")[48..],
            Assert.Throws<InvalidOperationException>(query).Message);

    private void AssertMql(params string[] expected)
        => Fixture.TestMqlLoggerFactory.AssertBaseline(expected);
}
