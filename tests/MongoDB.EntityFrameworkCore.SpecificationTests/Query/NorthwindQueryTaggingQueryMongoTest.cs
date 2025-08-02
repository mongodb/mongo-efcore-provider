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

    public override void Single_query_tag()
    {
        base.Single_query_tag();

        // Fails: TagWith EF-153
        Assert.DoesNotContain("Yanni", string.Join('|', Fixture.TestMqlLoggerFactory.MqlStatements));

        AssertMql(
            """
Customers.{ "$sort" : { "_id" : 1 } }, { "$limit" : 1 }
""");
    }

    public override void Single_query_multiple_tags()
    {
        base.Single_query_multiple_tags();

        // Fails: TagWith EF-153
        Assert.DoesNotContain("Yanni", string.Join('|', Fixture.TestMqlLoggerFactory.MqlStatements));
        Assert.DoesNotContain("Enya", string.Join('|', Fixture.TestMqlLoggerFactory.MqlStatements));

        AssertMql(
            """
Customers.{ "$sort" : { "_id" : 1 } }, { "$limit" : 1 }
""");
    }

    public override void Tags_on_subquery()
    {
        // Fails: Include issue EF-117
        Assert.Contains(
            "The LINQ expression",
            Assert.Throws<InvalidOperationException>(() => base.Tags_on_subquery()).Message);

        // Fails: TagWith EF-153
        Assert.DoesNotContain("Yanni", string.Join('|', Fixture.TestMqlLoggerFactory.MqlStatements));
        Assert.DoesNotContain("Laurel", string.Join('|', Fixture.TestMqlLoggerFactory.MqlStatements));

        AssertMql(
);
    }

    public override void Duplicate_tags()
    {
        base.Duplicate_tags();

        // Fails: TagWith EF-153
        Assert.DoesNotContain("Yanni", string.Join('|', Fixture.TestMqlLoggerFactory.MqlStatements));

        AssertMql(
            """
Customers.{ "$sort" : { "_id" : 1 } }, { "$limit" : 1 }
""");
    }

    public override void Tag_on_include_query()
    {
        // Fails: Include issue EF-117
        Assert.Contains(
            "Including navigation 'Navigation' is not supported",
            Assert.Throws<InvalidOperationException>(() => base.Tag_on_include_query()).Message);

        // Fails: TagWith EF-153
        Assert.DoesNotContain("Yanni", string.Join('|', Fixture.TestMqlLoggerFactory.MqlStatements));

        AssertMql(
);
    }

    public override void Tag_on_scalar_query()
    {
        base.Tag_on_scalar_query();

        // Fails: TagWith EF-153
        Assert.DoesNotContain("Yanni", string.Join('|', Fixture.TestMqlLoggerFactory.MqlStatements));

        AssertMql(
            """
Orders.{ "$sort" : { "_id" : 1 } }, { "$project" : { "_v" : "$OrderDate", "_id" : 0 } }, { "$limit" : 1 }
""");
    }

    public override void Single_query_multiline_tag()
    {
        base.Single_query_multiline_tag();

        // Fails: TagWith EF-153
        Assert.DoesNotContain("Yanni", string.Join('|', Fixture.TestMqlLoggerFactory.MqlStatements));
        Assert.DoesNotContain("AND", string.Join('|', Fixture.TestMqlLoggerFactory.MqlStatements));
        Assert.DoesNotContain("Laurel", string.Join('|', Fixture.TestMqlLoggerFactory.MqlStatements));

        AssertMql(
            """
Customers.{ "$sort" : { "_id" : 1 } }, { "$limit" : 1 }
""");
    }

    public override void Single_query_multiple_multiline_tag()
    {
        base.Single_query_multiple_multiline_tag();

        // Fails: TagWith EF-153
        Assert.DoesNotContain("Yanni", string.Join('|', Fixture.TestMqlLoggerFactory.MqlStatements));
        Assert.DoesNotContain("AND", string.Join('|', Fixture.TestMqlLoggerFactory.MqlStatements));
        Assert.DoesNotContain("Laurel", string.Join('|', Fixture.TestMqlLoggerFactory.MqlStatements));
        Assert.DoesNotContain("Yet", string.Join('|', Fixture.TestMqlLoggerFactory.MqlStatements));
        Assert.DoesNotContain("Another", string.Join('|', Fixture.TestMqlLoggerFactory.MqlStatements));
        Assert.DoesNotContain("Multiline", string.Join('|', Fixture.TestMqlLoggerFactory.MqlStatements));
        Assert.DoesNotContain("Tag", string.Join('|', Fixture.TestMqlLoggerFactory.MqlStatements));

        AssertMql(
            """
Customers.{ "$sort" : { "_id" : 1 } }, { "$limit" : 1 }
""");
    }

    public override void Single_query_multiline_tag_with_empty_lines()
    {
        base.Single_query_multiline_tag_with_empty_lines();

        // Fails: TagWith EF-153
        Assert.DoesNotContain("Yanni", string.Join('|', Fixture.TestMqlLoggerFactory.MqlStatements));
        Assert.DoesNotContain("AND", string.Join('|', Fixture.TestMqlLoggerFactory.MqlStatements));
        Assert.DoesNotContain("Laurel", string.Join('|', Fixture.TestMqlLoggerFactory.MqlStatements));

        AssertMql(
            """
Customers.{ "$sort" : { "_id" : 1 } }, { "$limit" : 1 }
""");
    }

    private void AssertMql(params string[] expected)
        => Fixture.TestMqlLoggerFactory.AssertBaseline(expected);
}
