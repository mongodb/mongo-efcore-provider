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

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.TestModels.Northwind;
using Microsoft.EntityFrameworkCore.TestUtilities;

namespace MongoDB.EntityFrameworkCore.SpecificationTests.Query;

public class NorthwindChangeTrackingQueryMongoTest : NorthwindChangeTrackingQueryTestBase<
    NorthwindQueryMongoFixture<NoopModelCustomizer>>
{
    public NorthwindChangeTrackingQueryMongoTest(NorthwindQueryMongoFixture<NoopModelCustomizer> fixture)
        : base(fixture)
        => Fixture.TestMqlLoggerFactory.Clear();

    [ConditionalFact]
    public virtual void Check_all_tests_overridden()
        => TestHelpers.AssertAllMethodsOverridden(GetType());

    public override void Entity_reverts_when_state_set_to_unchanged()
    {
        base.Entity_reverts_when_state_set_to_unchanged();

        AssertMql(
            """
Customers.{ "$limit" : 1 }
""");
    }

    public override void Entity_does_not_revert_when_attached_on_DbSet()
    {
        base.Entity_does_not_revert_when_attached_on_DbSet();

        AssertMql(
            """
Customers.{ "$limit" : 1 }
""");
    }

    public override void AsTracking_switches_tracking_on_when_off_in_options()
    {
        base.AsTracking_switches_tracking_on_when_off_in_options();

        AssertMql(
            """
Employees.
""");
    }

    public override void Can_disable_and_reenable_query_result_tracking_query_caching_using_options()
    {
        base.Can_disable_and_reenable_query_result_tracking_query_caching_using_options();

        AssertMql(
            """
Employees.
""",
            //
            """
Employees.
""");
    }

    public override void Can_disable_and_reenable_query_result_tracking()
    {
        base.Can_disable_and_reenable_query_result_tracking();

        AssertMql(
            """
Employees.{ "$sort" : { "_id" : 1 } }, { "$limit" : 1 }
""",
            //
            """
Employees.{ "$sort" : { "_id" : 1 } }, { "$skip" : 1 }, { "$limit" : 1 }
""",
            //
            """
Employees.{ "$sort" : { "_id" : 1 } }
""");
    }

    public override void Entity_range_does_not_revert_when_attached_dbSet()
    {
        base.Entity_range_does_not_revert_when_attached_dbSet();

        AssertMql(
            """
Customers.{ "$sort" : { "_id" : 1 } }, { "$limit" : 2 }, { "$limit" : 1 }
""",
            //
            """
Customers.{ "$sort" : { "_id" : 1 } }, { "$limit" : 2 }, { "$skip" : 1 }, { "$limit" : 1 }
""",
            //
            """
Customers.{ "$sort" : { "_id" : 1 } }, { "$limit" : 2 }
""");
    }

    public override void Precedence_of_tracking_modifiers5()
    {
        // Fails: Cross-document navigation access issue EF-216
        AssertTranslationFailed(() => base.Precedence_of_tracking_modifiers5());

        AssertMql(
);
    }

    public override void Precedence_of_tracking_modifiers2()
    {
        base.Precedence_of_tracking_modifiers2();

        AssertMql(
            """
Employees.
""");
    }

    public override void Can_disable_and_reenable_query_result_tracking_query_caching()
    {
        base.Can_disable_and_reenable_query_result_tracking_query_caching();

        AssertMql(
            """
Employees.
""",
            //
            """
Employees.
""");
    }

    public override void Entity_range_does_not_revert_when_attached_dbContext()
    {
        base.Entity_range_does_not_revert_when_attached_dbContext();

        AssertMql(
            """
Customers.{ "$sort" : { "_id" : 1 } }, { "$limit" : 2 }, { "$limit" : 1 }
""",
            //
            """
Customers.{ "$sort" : { "_id" : 1 } }, { "$limit" : 2 }, { "$skip" : 1 }, { "$limit" : 1 }
""",
            //
            """
Customers.{ "$sort" : { "_id" : 1 } }, { "$limit" : 2 }
""");
    }

    public override void Precedence_of_tracking_modifiers()
    {
        base.Precedence_of_tracking_modifiers();

        AssertMql(
            """
Employees.
""");
    }

    public override void Precedence_of_tracking_modifiers3()
    {
        // Fails: Cross-document navigation access issue EF-216
        AssertTranslationFailed(() => base.Precedence_of_tracking_modifiers3());

        AssertMql(
);
    }

    public override void Can_disable_and_reenable_query_result_tracking_starting_with_NoTracking()
    {
        base.Can_disable_and_reenable_query_result_tracking_starting_with_NoTracking();

        AssertMql(
            """
Employees.{ "$sort" : { "_id" : 1 } }, { "$limit" : 1 }
""",
            //
            """
Employees.{ "$sort" : { "_id" : 1 } }, { "$skip" : 1 }, { "$limit" : 1 }
""");
    }

    public override void Entity_does_not_revert_when_attached_on_DbContext()
    {
        base.Entity_does_not_revert_when_attached_on_DbContext();

        AssertMql(
            """
Customers.{ "$limit" : 1 }
""");
    }

    public override void Can_disable_and_reenable_query_result_tracking_query_caching_single_context()
    {
        base.Can_disable_and_reenable_query_result_tracking_query_caching_single_context();

        AssertMql(
            """
Employees.
""",
            //
            """
Employees.
""");
    }

    public override void Multiple_entities_can_revert()
    {
        base.Multiple_entities_can_revert();

        AssertMql(
            """
Customers.{ "$project" : { "_v" : "$PostalCode", "_id" : 0 } }
""",
            //
            """
Customers.{ "$project" : { "_v" : "$Region", "_id" : 0 } }
""",
            //
            """
Customers.
""",
            //
            """
Customers.{ "$limit" : 1 }
""",
            //
            """
Customers.{ "$limit" : 1 }
""",
            //
            """
Customers.{ "$project" : { "_v" : "$PostalCode", "_id" : 0 } }
""",
            //
            """
Customers.{ "$project" : { "_v" : "$Region", "_id" : 0 } }
""");
    }

    public override void Precedence_of_tracking_modifiers4()
    {
        // Fails: Cross-document navigation access issue EF-216
        AssertTranslationFailed(() => base.Precedence_of_tracking_modifiers4());

        AssertMql(
);
    }

    private static void AssertTranslationFailed(Action query)
        => Assert.Contains(
            CoreStrings.TranslationFailed("")[48..],
            Assert.Throws<InvalidOperationException>(query).Message);

    private void AssertMql(params string[] expected)
        => Fixture.TestMqlLoggerFactory.AssertBaseline(expected);

    protected override NorthwindContext CreateNoTrackingContext()
        => new(new DbContextOptionsBuilder(Fixture.CreateOptions())
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking).Options);
}
