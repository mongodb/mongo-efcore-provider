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
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Linq;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace MongoDB.EntityFrameworkCore.SpecificationTests.Query;

public class NorthwindGroupByQueryMongoTest : NorthwindGroupByQueryTestBase<
    NorthwindQueryMongoFixture<NoopModelCustomizer>>
{
    public NorthwindGroupByQueryMongoTest(
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

#if !EF8 && !EF9

    public override async Task Final_GroupBy_TagWith(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.Final_GroupBy_TagWith(async));
    }

#endif

    public override async Task GroupBy_Property_Select_Average(bool async)
    {
        await base.GroupBy_Property_Select_Average(async);

        AssertMql(
            """
Orders.{ "$group" : { "_id" : "$CustomerID", "_v" : { "$avg" : "$_id" } } }, { "$project" : { "_v" : "$_v", "_id" : 0 } }
""");

        // Validating that we don't generate warning when translating GroupBy. See Issue#11157
        Assert.DoesNotContain(
            "The LINQ expression 'GroupBy([o].CustomerID, [o])' could not be translated and will be evaluated locally.",
            Fixture.TestMqlLoggerFactory.Log.Select(l => l.Message));
    }

    public override async Task GroupBy_Property_Select_Average_with_group_enumerable_projected(bool async)
    {
        await Assert.ThrowsAnyAsync<Exception>(() => base.GroupBy_Property_Select_Average_with_group_enumerable_projected(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_Property_Select_Count(bool async)
    {
        await base.GroupBy_Property_Select_Count(async);

        AssertMql(
            """
Orders.{ "$group" : { "_id" : "$CustomerID", "_v" : { "$sum" : 1 } } }, { "$project" : { "_v" : "$_v", "_id" : 0 } }
""");
    }

    public override async Task GroupBy_Property_Select_LongCount(bool async)
    {
        await base.GroupBy_Property_Select_LongCount(async);

        AssertMql(
            """
Orders.{ "$group" : { "_id" : "$CustomerID", "_v" : { "$sum" : 1 } } }, { "$project" : { "_v" : "$_v", "_id" : 0 } }
""");
    }

    public override async Task GroupBy_Property_Select_Count_with_nulls(bool async)
    {
        await base.GroupBy_Property_Select_Count_with_nulls(async);
        AssertMql(
            """
            Customers.{ "$group" : { "_id" : "$City", "Faxes" : { "$sum" : 1 } } }, { "$project" : { "City" : "$_id", "Faxes" : "$Faxes", "_id" : 0 } }
            """);
    }

    public override async Task GroupBy_Property_Select_LongCount_with_nulls(bool async)
    {
        await base.GroupBy_Property_Select_LongCount_with_nulls(async);

        AssertMql(
            """
            Customers.{ "$group" : { "_id" : "$City", "Faxes" : { "$sum" : 1 } } }, { "$project" : { "City" : "$_id", "Faxes" : "$Faxes", "_id" : 0 } }
            """);
    }

    public override async Task GroupBy_Property_Select_Max(bool async)
    {
        await base.GroupBy_Property_Select_Max(async);

        AssertMql(
            """
Orders.{ "$group" : { "_id" : "$CustomerID", "_v" : { "$max" : "$_id" } } }, { "$project" : { "_v" : "$_v", "_id" : 0 } }
""");
    }

    public override async Task GroupBy_Property_Select_Min(bool async)
    {
        await base.GroupBy_Property_Select_Min(async);

        AssertMql(
            """
Orders.{ "$group" : { "_id" : "$CustomerID", "_v" : { "$min" : "$_id" } } }, { "$project" : { "_v" : "$_v", "_id" : 0 } }
""");
    }

    public override async Task GroupBy_Property_Select_Sum(bool async)
    {
        await base.GroupBy_Property_Select_Sum(async);

        AssertMql(
            """
Orders.{ "$group" : { "_id" : "$CustomerID", "_v" : { "$sum" : "$_id" } } }, { "$project" : { "_v" : "$_v", "_id" : 0 } }
""");
    }

    public override async Task GroupBy_Property_Select_Sum_Min_Max_Avg(bool async)
    {
        await base.GroupBy_Property_Select_Sum_Min_Max_Avg(async);

        AssertMql(
            """
            Orders.{ "$group" : { "_id" : "$CustomerID", "Sum" : { "$sum" : "$_id" }, "Min" : { "$min" : "$_id" }, "Max" : { "$max" : "$_id" }, "Avg" : { "$avg" : "$_id" } } }, { "$project" : { "Sum" : "$Sum", "Min" : "$Min", "Max" : "$Max", "Avg" : "$Avg", "_id" : 0 } }
            """);
    }

    public override async Task GroupBy_Property_Select_Key_Average(bool async)
    {
        await base.GroupBy_Property_Select_Key_Average(async);

        AssertMql(
            """
            Orders.{ "$group" : { "_id" : "$CustomerID", "Average" : { "$avg" : "$_id" } } }, { "$project" : { "Key" : "$_id", "Average" : "$Average", "_id" : 0 } }
            """);
    }

    public override async Task GroupBy_Property_Select_Key_Count(bool async)
    {
        await base.GroupBy_Property_Select_Key_Count(async);

        AssertMql(
            """
Orders.{ "$group" : { "_id" : "$CustomerID", "Count" : { "$sum" : 1 } } }, { "$project" : { "Key" : "$_id", "Count" : "$Count", "_id" : 0 } }
""");
    }

    public override async Task GroupBy_Property_Select_Key_LongCount(bool async)
    {
        await base.GroupBy_Property_Select_Key_LongCount(async);

        AssertMql(
            """
            Orders.{ "$group" : { "_id" : "$CustomerID", "LongCount" : { "$sum" : 1 } } }, { "$project" : { "Key" : "$_id", "LongCount" : "$LongCount", "_id" : 0 } }
            """);
    }

    public override async Task GroupBy_Property_Select_Key_Max(bool async)
    {
        await base.GroupBy_Property_Select_Key_Max(async);

        AssertMql(
            """
            Orders.{ "$group" : { "_id" : "$CustomerID", "Max" : { "$max" : "$_id" } } }, { "$project" : { "Key" : "$_id", "Max" : "$Max", "_id" : 0 } }
            """);
    }

    public override async Task GroupBy_Property_Select_Key_Min(bool async)
    {
        await base.GroupBy_Property_Select_Key_Min(async);

        AssertMql(
            """
            Orders.{ "$group" : { "_id" : "$CustomerID", "Min" : { "$min" : "$_id" } } }, { "$project" : { "Key" : "$_id", "Min" : "$Min", "_id" : 0 } }
            """);
    }

    public override async Task GroupBy_Property_Select_Key_Sum(bool async)
    {
        await base.GroupBy_Property_Select_Key_Sum(async);

        AssertMql(
            """
            Orders.{ "$group" : { "_id" : "$CustomerID", "Sum" : { "$sum" : "$_id" } } }, { "$project" : { "Key" : "$_id", "Sum" : "$Sum", "_id" : 0 } }
            """);
    }

    public override async Task GroupBy_Property_Select_Key_Sum_Min_Max_Avg(bool async)
    {
        await base.GroupBy_Property_Select_Key_Sum_Min_Max_Avg(async);

        AssertMql(
            """
            Orders.{ "$group" : { "_id" : "$CustomerID", "Sum" : { "$sum" : "$_id" }, "Min" : { "$min" : "$_id" }, "Max" : { "$max" : "$_id" }, "Avg" : { "$avg" : "$_id" } } }, { "$project" : { "Key" : "$_id", "Sum" : "$Sum", "Min" : "$Min", "Max" : "$Max", "Avg" : "$Avg", "_id" : 0 } }
            """);
    }

    public override async Task GroupBy_Property_Select_Sum_Min_Key_Max_Avg(bool async)
    {
        await base.GroupBy_Property_Select_Sum_Min_Key_Max_Avg(async);
        AssertMql(
            """
Orders.{ "$group" : { "_id" : "$CustomerID", "Sum" : { "$sum" : "$_id" }, "Min" : { "$min" : "$_id" }, "Max" : { "$max" : "$_id" }, "Avg" : { "$avg" : "$_id" } } }, { "$project" : { "Sum" : "$Sum", "Min" : "$Min", "Key" : "$_id", "Max" : "$Max", "Avg" : "$Avg", "_id" : 0 } }
""");
    }

    public override async Task GroupBy_Property_Select_key_multiple_times_and_aggregate(bool async)
    {
        await base.GroupBy_Property_Select_key_multiple_times_and_aggregate(async);

        AssertMql(
            """
            Orders.{ "$group" : { "_id" : "$CustomerID", "Sum" : { "$sum" : "$_id" } } }, { "$project" : { "Key1" : "$_id", "Key2" : "$_id", "Sum" : "$Sum", "_id" : 0 } }
            """);
    }

    public override async Task GroupBy_Property_Select_Key_with_constant(bool async)
    {
        await base.GroupBy_Property_Select_Key_with_constant(async);

        AssertMql(
            """
Orders.{ "$group" : { "_id" : { "Name" : { "$literal" : "CustomerID" }, "Value" : "$CustomerID" }, "Count" : { "$sum" : 1 } } }, { "$project" : { "Key" : "$_id", "Count" : "$Count", "_id" : 0 } }
""");
    }

    public override async Task GroupBy_aggregate_projecting_conditional_expression(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_aggregate_projecting_conditional_expression(async));

        if (MongoSpecTestHelpers.IsNativeOnly)
        {
            AssertMql();
        }
        else
        {
            AssertMql(
    """
            Orders.{ "$group" : { "_id" : "$OrderDate", "__agg0" : { "$sum" : 1 }, "__agg1" : { "$sum" : { "$cond" : { "if" : { "$eq" : [{ "$mod" : ["$_id", 2] }, 0] }, "then" : 1, "else" : 0 } } } } }, { "$project" : { "Key" : "$_id", "SomeValue" : { "$cond" : { "if" : { "$eq" : ["$__agg0", 0] }, "then" : 1, "else" : { "$divide" : ["$__agg1", "$__agg0"] } } }, "_id" : 0 } }
            """);
        }
    }

    public override async Task GroupBy_aggregate_projecting_conditional_expression_based_on_group_key(bool async)
    {
        await base.GroupBy_aggregate_projecting_conditional_expression_based_on_group_key(async);

        AssertMql(
            """
Orders.{ "$group" : { "_id" : "$OrderDate", "Sum" : { "$sum" : "$_id" } } }, { "$project" : { "Key" : { "$cond" : { "if" : { "$eq" : [{ "$ifNull" : ["$_id", null] }, null] }, "then" : { "$literal" : "is null" }, "else" : { "$literal" : "is not null" } } }, "Sum" : "$Sum", "_id" : 0 } }
""");
    }

    public override async Task GroupBy_anonymous_Select_Average(bool async)
    {
        await base.GroupBy_anonymous_Select_Average(async);

        AssertMql(
            """
Orders.{ "$group" : { "_id" : { "CustomerID" : "$CustomerID" }, "_v" : { "$avg" : "$_id" } } }, { "$project" : { "_v" : "$_v", "_id" : 0 } }
""");
    }

    public override async Task GroupBy_anonymous_Select_Count(bool async)
    {
        await base.GroupBy_anonymous_Select_Count(async);

        AssertMql(
            """
Orders.{ "$group" : { "_id" : { "CustomerID" : "$CustomerID" }, "_v" : { "$sum" : 1 } } }, { "$project" : { "_v" : "$_v", "_id" : 0 } }
""");
    }

    public override async Task GroupBy_anonymous_Select_LongCount(bool async)
    {
        await base.GroupBy_anonymous_Select_LongCount(async);

        AssertMql(
            """
Orders.{ "$group" : { "_id" : { "CustomerID" : "$CustomerID" }, "_v" : { "$sum" : 1 } } }, { "$project" : { "_v" : "$_v", "_id" : 0 } }
""");
    }

    public override async Task GroupBy_anonymous_Select_Max(bool async)
    {
        await base.GroupBy_anonymous_Select_Max(async);

        AssertMql(
            """
Orders.{ "$group" : { "_id" : { "CustomerID" : "$CustomerID" }, "_v" : { "$max" : "$_id" } } }, { "$project" : { "_v" : "$_v", "_id" : 0 } }
""");
    }

    public override async Task GroupBy_anonymous_Select_Min(bool async)
    {
        await base.GroupBy_anonymous_Select_Min(async);

        AssertMql(
            """
Orders.{ "$group" : { "_id" : { "CustomerID" : "$CustomerID" }, "_v" : { "$min" : "$_id" } } }, { "$project" : { "_v" : "$_v", "_id" : 0 } }
""");
    }

    public override async Task GroupBy_anonymous_Select_Sum(bool async)
    {
        await base.GroupBy_anonymous_Select_Sum(async);

        AssertMql(
            """
Orders.{ "$group" : { "_id" : { "CustomerID" : "$CustomerID" }, "_v" : { "$sum" : "$_id" } } }, { "$project" : { "_v" : "$_v", "_id" : 0 } }
""");
    }

    public override async Task GroupBy_anonymous_Select_Sum_Min_Max_Avg(bool async)
    {
        await base.GroupBy_anonymous_Select_Sum_Min_Max_Avg(async);

        AssertMql(
            """
            Orders.{ "$group" : { "_id" : { "CustomerID" : "$CustomerID" }, "Sum" : { "$sum" : "$_id" }, "Min" : { "$min" : "$_id" }, "Max" : { "$max" : "$_id" }, "Avg" : { "$avg" : "$_id" } } }, { "$project" : { "Sum" : "$Sum", "Min" : "$Min", "Max" : "$Max", "Avg" : "$Avg", "_id" : 0 } }
            """);
    }

    public override async Task GroupBy_anonymous_with_alias_Select_Key_Sum(bool async)
    {
        await base.GroupBy_anonymous_with_alias_Select_Key_Sum(async);

        AssertMql(
            """
            Orders.{ "$group" : { "_id" : { "Id" : "$CustomerID" }, "Sum" : { "$sum" : "$_id" } } }, { "$project" : { "Key" : "$_id.Id", "Sum" : "$Sum", "_id" : 0 } }
            """);
    }

    public override async Task GroupBy_Composite_Select_Average(bool async)
    {
        await base.GroupBy_Composite_Select_Average(async);

        AssertMql(
            """
Orders.{ "$group" : { "_id" : { "CustomerID" : "$CustomerID", "EmployeeID" : "$EmployeeID" }, "_v" : { "$avg" : "$_id" } } }, { "$project" : { "_v" : "$_v", "_id" : 0 } }
""");
    }

    public override async Task GroupBy_Composite_Select_Count(bool async)
    {
        await base.GroupBy_Composite_Select_Count(async);

        AssertMql(
            """
Orders.{ "$group" : { "_id" : { "CustomerID" : "$CustomerID", "EmployeeID" : "$EmployeeID" }, "_v" : { "$sum" : 1 } } }, { "$project" : { "_v" : "$_v", "_id" : 0 } }
""");
    }

    public override async Task GroupBy_Composite_Select_LongCount(bool async)
    {
        await base.GroupBy_Composite_Select_LongCount(async);

        AssertMql(
            """
Orders.{ "$group" : { "_id" : { "CustomerID" : "$CustomerID", "EmployeeID" : "$EmployeeID" }, "_v" : { "$sum" : 1 } } }, { "$project" : { "_v" : "$_v", "_id" : 0 } }
""");
    }

    public override async Task GroupBy_Composite_Select_Max(bool async)
    {
        await base.GroupBy_Composite_Select_Max(async);

        AssertMql(
            """
Orders.{ "$group" : { "_id" : { "CustomerID" : "$CustomerID", "EmployeeID" : "$EmployeeID" }, "_v" : { "$max" : "$_id" } } }, { "$project" : { "_v" : "$_v", "_id" : 0 } }
""");
    }

    public override async Task GroupBy_Composite_Select_Min(bool async)
    {
        await base.GroupBy_Composite_Select_Min(async);

        AssertMql(
            """
Orders.{ "$group" : { "_id" : { "CustomerID" : "$CustomerID", "EmployeeID" : "$EmployeeID" }, "_v" : { "$min" : "$_id" } } }, { "$project" : { "_v" : "$_v", "_id" : 0 } }
""");
    }

    public override async Task GroupBy_Composite_Select_Sum(bool async)
    {
        await base.GroupBy_Composite_Select_Sum(async);

        AssertMql(
            """
Orders.{ "$group" : { "_id" : { "CustomerID" : "$CustomerID", "EmployeeID" : "$EmployeeID" }, "_v" : { "$sum" : "$_id" } } }, { "$project" : { "_v" : "$_v", "_id" : 0 } }
""");
    }

    public override async Task GroupBy_Composite_Select_Sum_Min_Max_Avg(bool async)
    {
        await base.GroupBy_Composite_Select_Sum_Min_Max_Avg(async);

        AssertMql(
            """
            Orders.{ "$group" : { "_id" : { "CustomerID" : "$CustomerID", "EmployeeID" : "$EmployeeID" }, "Sum" : { "$sum" : "$_id" }, "Min" : { "$min" : "$_id" }, "Max" : { "$max" : "$_id" }, "Avg" : { "$avg" : "$_id" } } }, { "$project" : { "Sum" : "$Sum", "Min" : "$Min", "Max" : "$Max", "Avg" : "$Avg", "_id" : 0 } }
            """);
    }

    public override async Task GroupBy_Composite_Select_Key_Average(bool async)
    {
        await base.GroupBy_Composite_Select_Key_Average(async);

        AssertMql(
            """
            Orders.{ "$group" : { "_id" : { "CustomerID" : "$CustomerID", "EmployeeID" : "$EmployeeID" }, "Average" : { "$avg" : "$_id" } } }, { "$project" : { "Key" : "$_id", "Average" : "$Average", "_id" : 0 } }
            """);
    }

    public override async Task GroupBy_Composite_Select_Key_Count(bool async)
    {
        await base.GroupBy_Composite_Select_Key_Count(async);

        AssertMql(
            """
            Orders.{ "$group" : { "_id" : { "CustomerID" : "$CustomerID", "EmployeeID" : "$EmployeeID" }, "Count" : { "$sum" : 1 } } }, { "$project" : { "Key" : "$_id", "Count" : "$Count", "_id" : 0 } }
            """);
    }

    public override async Task GroupBy_Composite_Select_Key_LongCount(bool async)
    {
        await base.GroupBy_Composite_Select_Key_LongCount(async);

        AssertMql(
            """
            Orders.{ "$group" : { "_id" : { "CustomerID" : "$CustomerID", "EmployeeID" : "$EmployeeID" }, "LongCount" : { "$sum" : 1 } } }, { "$project" : { "Key" : "$_id", "LongCount" : "$LongCount", "_id" : 0 } }
            """);
    }

    public override async Task GroupBy_Composite_Select_Key_Max(bool async)
    {
        await base.GroupBy_Composite_Select_Key_Max(async);

        AssertMql(
            """
            Orders.{ "$group" : { "_id" : { "CustomerID" : "$CustomerID", "EmployeeID" : "$EmployeeID" }, "Max" : { "$max" : "$_id" } } }, { "$project" : { "Key" : "$_id", "Max" : "$Max", "_id" : 0 } }
            """);
    }

    public override async Task GroupBy_Composite_Select_Key_Min(bool async)
    {
        await base.GroupBy_Composite_Select_Key_Min(async);

        AssertMql(
            """
            Orders.{ "$group" : { "_id" : { "CustomerID" : "$CustomerID", "EmployeeID" : "$EmployeeID" }, "Min" : { "$min" : "$_id" } } }, { "$project" : { "Key" : "$_id", "Min" : "$Min", "_id" : 0 } }
            """);
    }

    public override async Task GroupBy_Composite_Select_Key_Sum(bool async)
    {
        await base.GroupBy_Composite_Select_Key_Sum(async);

        AssertMql(
            """
            Orders.{ "$group" : { "_id" : { "CustomerID" : "$CustomerID", "EmployeeID" : "$EmployeeID" }, "Sum" : { "$sum" : "$_id" } } }, { "$project" : { "Key" : "$_id", "Sum" : "$Sum", "_id" : 0 } }
            """);
    }

    public override async Task GroupBy_Composite_Select_Key_Sum_Min_Max_Avg(bool async)
    {
        await base.GroupBy_Composite_Select_Key_Sum_Min_Max_Avg(async);

        AssertMql(
            """
            Orders.{ "$group" : { "_id" : { "CustomerID" : "$CustomerID", "EmployeeID" : "$EmployeeID" }, "Sum" : { "$sum" : "$_id" }, "Min" : { "$min" : "$_id" }, "Max" : { "$max" : "$_id" }, "Avg" : { "$avg" : "$_id" } } }, { "$project" : { "Key" : "$_id", "Sum" : "$Sum", "Min" : "$Min", "Max" : "$Max", "Avg" : "$Avg", "_id" : 0 } }
            """);
    }

    public override async Task GroupBy_Composite_Select_Sum_Min_Key_Max_Avg(bool async)
    {
        await base.GroupBy_Composite_Select_Sum_Min_Key_Max_Avg(async);

        AssertMql(
            """
            Orders.{ "$group" : { "_id" : { "CustomerID" : "$CustomerID", "EmployeeID" : "$EmployeeID" }, "Sum" : { "$sum" : "$_id" }, "Min" : { "$min" : "$_id" }, "Max" : { "$max" : "$_id" }, "Avg" : { "$avg" : "$_id" } } }, { "$project" : { "Sum" : "$Sum", "Min" : "$Min", "Key" : "$_id", "Max" : "$Max", "Avg" : "$Avg", "_id" : 0 } }
            """);
    }

    public override async Task GroupBy_Composite_Select_Sum_Min_Key_flattened_Max_Avg(bool async)
    {
        await base.GroupBy_Composite_Select_Sum_Min_Key_flattened_Max_Avg(async);

        AssertMql(
            """
            Orders.{ "$group" : { "_id" : { "CustomerID" : "$CustomerID", "EmployeeID" : "$EmployeeID" }, "Sum" : { "$sum" : "$_id" }, "Min" : { "$min" : "$_id" }, "Max" : { "$max" : "$_id" }, "Avg" : { "$avg" : "$_id" } } }, { "$project" : { "Sum" : "$Sum", "Min" : "$Min", "CustomerID" : "$_id.CustomerID", "EmployeeID" : "$_id.EmployeeID", "Max" : "$Max", "Avg" : "$Avg", "_id" : 0 } }
            """);
    }

    public override async Task GroupBy_Dto_as_key_Select_Sum(bool async)
    {
        await base.GroupBy_Dto_as_key_Select_Sum(async);

        AssertMql(
            """
Orders.{ "$group" : { "_id" : { "CustomerID" : "$CustomerID", "EmployeeID" : "$EmployeeID" }, "Sum" : { "$sum" : "$_id" } } }, { "$project" : { "Sum" : "$Sum", "Key" : "$_id", "_id" : 0 } }
""");
    }

    public override async Task GroupBy_Dto_as_element_selector_Select_Sum(bool async)
    {
        await base.GroupBy_Dto_as_element_selector_Select_Sum(async);

        AssertMql(
            """
Orders.{ "$group" : { "_id" : "$CustomerID", "Sum" : { "$sum" : "$EmployeeID" } } }, { "$project" : { "Sum" : "$Sum", "Key" : "$_id", "_id" : 0 } }
""");
    }

    public override async Task GroupBy_Composite_Select_Dto_Sum_Min_Key_flattened_Max_Avg(bool async)
    {
        await base.GroupBy_Composite_Select_Dto_Sum_Min_Key_flattened_Max_Avg(async);

        AssertMql(
            """
            Orders.{ "$group" : { "_id" : { "CustomerID" : "$CustomerID", "EmployeeID" : "$EmployeeID" }, "Sum" : { "$sum" : "$_id" }, "Min" : { "$min" : "$_id" }, "Max" : { "$max" : "$_id" }, "Avg" : { "$avg" : "$_id" } } }, { "$project" : { "Sum" : "$Sum", "Min" : "$Min", "CustomerId" : "$_id.CustomerID", "EmployeeId" : "$_id.EmployeeID", "Max" : "$Max", "Avg" : "$Avg", "_id" : 0 } }
            """);
    }

    public override async Task GroupBy_Composite_Select_Sum_Min_part_Key_flattened_Max_Avg(bool async)
    {
        await base.GroupBy_Composite_Select_Sum_Min_part_Key_flattened_Max_Avg(async);

        AssertMql(
            """
            Orders.{ "$group" : { "_id" : { "CustomerID" : "$CustomerID", "EmployeeID" : "$EmployeeID" }, "Sum" : { "$sum" : "$_id" }, "Min" : { "$min" : "$_id" }, "Max" : { "$max" : "$_id" }, "Avg" : { "$avg" : "$_id" } } }, { "$project" : { "Sum" : "$Sum", "Min" : "$Min", "CustomerID" : "$_id.CustomerID", "Max" : "$Max", "Avg" : "$Avg", "_id" : 0 } }
            """);
    }

    public override async Task GroupBy_Constant_Select_Sum_Min_Key_Max_Avg(bool async)
    {
        await base.GroupBy_Constant_Select_Sum_Min_Key_Max_Avg(async);

        AssertMql(
            """
Orders.{ "$group" : { "_id" : { "$literal" : 2 }, "Sum" : { "$sum" : "$_id" }, "Min" : { "$min" : "$_id" }, "Max" : { "$max" : "$_id" }, "Avg" : { "$avg" : "$_id" } } }, { "$project" : { "Sum" : "$Sum", "Min" : "$Min", "Key" : "$_id", "Max" : "$Max", "Avg" : "$Avg", "_id" : 0 } }
""");
    }

    public override async Task GroupBy_Constant_with_element_selector_Select_Sum(bool async)
    {
        await base.GroupBy_Constant_with_element_selector_Select_Sum(async);

        AssertMql(
            """
Orders.{ "$group" : { "_id" : { "$literal" : 2 }, "Sum" : { "$sum" : "$_id" } } }, { "$project" : { "Sum" : "$Sum", "_id" : 0 } }
""");
    }

    public override async Task GroupBy_Constant_with_element_selector_Select_Sum2(bool async)
    {
        await base.GroupBy_Constant_with_element_selector_Select_Sum2(async);

        AssertMql(
            """
Orders.{ "$group" : { "_id" : { "$literal" : 2 }, "Sum" : { "$sum" : "$_id" } } }, { "$project" : { "Sum" : "$Sum", "_id" : 0 } }
""");
    }

    public override async Task GroupBy_Constant_with_element_selector_Select_Sum3(bool async)
    {
        await base.GroupBy_Constant_with_element_selector_Select_Sum3(async);

        AssertMql(
            """
Orders.{ "$group" : { "_id" : { "$literal" : 2 }, "Sum" : { "$sum" : "$_id" } } }, { "$project" : { "Sum" : "$Sum", "_id" : 0 } }
""");
    }

    public override async Task GroupBy_after_predicate_Constant_Select_Sum_Min_Key_Max_Avg(bool async)
    {
        await base.GroupBy_after_predicate_Constant_Select_Sum_Min_Key_Max_Avg(async);

        AssertMql(
            """
Orders.{ "$match" : { "_id" : { "$gt" : 10500 } } }, { "$group" : { "_id" : { "$literal" : 2 }, "Sum" : { "$sum" : "$_id" }, "Min" : { "$min" : "$_id" }, "Max" : { "$max" : "$_id" }, "Avg" : { "$avg" : "$_id" } } }, { "$project" : { "Sum" : "$Sum", "Min" : "$Min", "Random" : "$_id", "Max" : "$Max", "Avg" : "$Avg", "_id" : 0 } }
""");
    }

    public override async Task GroupBy_Constant_with_element_selector_Select_Sum_Min_Key_Max_Avg(bool async)
    {
        await base.GroupBy_Constant_with_element_selector_Select_Sum_Min_Key_Max_Avg(async);

        AssertMql(
            """
Orders.{ "$group" : { "_id" : { "$literal" : 2 }, "Sum" : { "$sum" : "$_id" } } }, { "$project" : { "Sum" : "$Sum", "Key" : "$_id", "_id" : 0 } }
""");
    }

    public override async Task GroupBy_constant_with_where_on_grouping_with_aggregate_operators(bool async)
    {
        await base.GroupBy_constant_with_where_on_grouping_with_aggregate_operators(async);

        AssertMql(
            """
Orders.{ "$group" : { "_id" : { "$literal" : 1 }, "Min" : { "$min" : { "$cond" : { "if" : { "$eq" : [1, 1] }, "then" : "$OrderDate", "else" : "$$REMOVE" } } }, "Max" : { "$max" : { "$cond" : { "if" : { "$eq" : [1, 1] }, "then" : "$OrderDate", "else" : "$$REMOVE" } } }, "Sum" : { "$sum" : { "$cond" : { "if" : { "$eq" : [1, 1] }, "then" : "$_id", "else" : "$$REMOVE" } } }, "Average" : { "$avg" : { "$cond" : { "if" : { "$eq" : [1, 1] }, "then" : "$_id", "else" : "$$REMOVE" } } } } }, { "$sort" : { "_id" : 1 } }, { "$project" : { "Min" : "$Min", "Max" : "$Max", "Sum" : "$Sum", "Average" : "$Average", "_id" : 0 } }
""");
    }

    public override async Task GroupBy_param_Select_Sum_Min_Key_Max_Avg(bool async)
    {
        await base.GroupBy_param_Select_Sum_Min_Key_Max_Avg(async);

        AssertMql(
            """
Orders.{ "$group" : { "_id" : { "$literal" : 2 }, "Sum" : { "$sum" : "$_id" }, "Min" : { "$min" : "$_id" }, "Max" : { "$max" : "$_id" }, "Avg" : { "$avg" : "$_id" } } }, { "$project" : { "Sum" : "$Sum", "Min" : "$Min", "Key" : "$_id", "Max" : "$Max", "Avg" : "$Avg", "_id" : 0 } }
""");
    }

    public override async Task GroupBy_param_with_element_selector_Select_Sum(bool async)
    {
        await base.GroupBy_param_with_element_selector_Select_Sum(async);

        AssertMql(
            """
Orders.{ "$group" : { "_id" : { "$literal" : 2 }, "Sum" : { "$sum" : "$_id" } } }, { "$project" : { "Sum" : "$Sum", "_id" : 0 } }
""");
    }

    public override async Task GroupBy_param_with_element_selector_Select_Sum2(bool async)
    {
        await base.GroupBy_param_with_element_selector_Select_Sum2(async);

        AssertMql(
            """
Orders.{ "$group" : { "_id" : { "$literal" : 2 }, "Sum" : { "$sum" : "$_id" } } }, { "$project" : { "Sum" : "$Sum", "_id" : 0 } }
""");
    }

    public override async Task GroupBy_param_with_element_selector_Select_Sum3(bool async)
    {
        await base.GroupBy_param_with_element_selector_Select_Sum3(async);

        AssertMql(
            """
Orders.{ "$group" : { "_id" : { "$literal" : 2 }, "Sum" : { "$sum" : "$_id" } } }, { "$project" : { "Sum" : "$Sum", "_id" : 0 } }
""");
    }

    public override async Task GroupBy_param_with_element_selector_Select_Sum_Min_Key_Max_Avg(bool async)
    {
        await base.GroupBy_param_with_element_selector_Select_Sum_Min_Key_Max_Avg(async);

        AssertMql(
            """
Orders.{ "$group" : { "_id" : { "$literal" : 2 }, "Sum" : { "$sum" : "$_id" } } }, { "$project" : { "Sum" : "$Sum", "Key" : "$_id", "_id" : 0 } }
""");
    }

    public override async Task GroupBy_anonymous_key_type_mismatch_with_aggregate(bool async)
    {
        await base.GroupBy_anonymous_key_type_mismatch_with_aggregate(async);

        AssertMql(
            """
Orders.{ "$group" : { "_id" : { "I0" : { "$year" : "$OrderDate" } }, "I0" : { "$sum" : 1 } } }, { "$sort" : { "_id.I0" : 1 } }, { "$project" : { "I0" : "$I0", "I1" : "$_id.I0", "_id" : 0 } }
""");
    }

    public override async Task GroupBy_Property_scalar_element_selector_Average(bool async)
    {
        await base.GroupBy_Property_scalar_element_selector_Average(async);

        AssertMql(
            """
Orders.{ "$group" : { "_id" : "$CustomerID", "_v" : { "$avg" : "$_id" } } }, { "$project" : { "_v" : "$_v", "_id" : 0 } }
""");
    }

    public override async Task GroupBy_Property_scalar_element_selector_Count(bool async)
    {
        await base.GroupBy_Property_scalar_element_selector_Count(async);

        AssertMql(
            """
Orders.{ "$group" : { "_id" : "$CustomerID", "_v" : { "$sum" : 1 } } }, { "$project" : { "_v" : "$_v", "_id" : 0 } }
""");
    }

    public override async Task GroupBy_Property_scalar_element_selector_LongCount(bool async)
    {
        await base.GroupBy_Property_scalar_element_selector_LongCount(async);

        AssertMql(
            """
Orders.{ "$group" : { "_id" : "$CustomerID", "_v" : { "$sum" : 1 } } }, { "$project" : { "_v" : "$_v", "_id" : 0 } }
""");
    }

    public override async Task GroupBy_Property_scalar_element_selector_Max(bool async)
    {
        await base.GroupBy_Property_scalar_element_selector_Max(async);

        AssertMql(
            """
Orders.{ "$group" : { "_id" : "$CustomerID", "_v" : { "$max" : "$_id" } } }, { "$project" : { "_v" : "$_v", "_id" : 0 } }
""");
    }

    public override async Task GroupBy_Property_scalar_element_selector_Min(bool async)
    {
        await base.GroupBy_Property_scalar_element_selector_Min(async);

        AssertMql(
            """
Orders.{ "$group" : { "_id" : "$CustomerID", "_v" : { "$min" : "$_id" } } }, { "$project" : { "_v" : "$_v", "_id" : 0 } }
""");
    }

    public override async Task GroupBy_Property_scalar_element_selector_Sum(bool async)
    {
        await base.GroupBy_Property_scalar_element_selector_Sum(async);

        AssertMql(
            """
Orders.{ "$group" : { "_id" : "$CustomerID", "_v" : { "$sum" : "$_id" } } }, { "$project" : { "_v" : "$_v", "_id" : 0 } }
""");
    }

    public override async Task GroupBy_Property_scalar_element_selector_Sum_Min_Max_Avg(bool async)
    {
        await base.GroupBy_Property_scalar_element_selector_Sum_Min_Max_Avg(async);

        AssertMql(
            """
Orders.{ "$group" : { "_id" : "$CustomerID", "Sum" : { "$sum" : "$_id" }, "Min" : { "$min" : "$_id" }, "Max" : { "$max" : "$_id" }, "Avg" : { "$avg" : "$_id" } } }, { "$project" : { "Sum" : "$Sum", "Min" : "$Min", "Max" : "$Max", "Avg" : "$Avg", "_id" : 0 } }
""");
    }

    public override async Task GroupBy_Property_anonymous_element_selector_Average(bool async)
    {
        await base.GroupBy_Property_anonymous_element_selector_Average(async);

        AssertMql(
            """
Orders.{ "$group" : { "_id" : "$CustomerID", "_v" : { "$avg" : "$_id" } } }, { "$project" : { "_v" : "$_v", "_id" : 0 } }
""");
    }

    public override async Task GroupBy_Property_anonymous_element_selector_Count(bool async)
    {
        await base.GroupBy_Property_anonymous_element_selector_Count(async);

        AssertMql(
            """
Orders.{ "$group" : { "_id" : "$CustomerID", "_v" : { "$sum" : 1 } } }, { "$project" : { "_v" : "$_v", "_id" : 0 } }
""");
    }

    public override async Task GroupBy_Property_anonymous_element_selector_LongCount(bool async)
    {
        await base.GroupBy_Property_anonymous_element_selector_LongCount(async);

        AssertMql(
            """
Orders.{ "$group" : { "_id" : "$CustomerID", "_v" : { "$sum" : 1 } } }, { "$project" : { "_v" : "$_v", "_id" : 0 } }
""");
    }

    public override async Task GroupBy_Property_anonymous_element_selector_Max(bool async)
    {
        await base.GroupBy_Property_anonymous_element_selector_Max(async);

        AssertMql(
            """
Orders.{ "$group" : { "_id" : "$CustomerID", "_v" : { "$max" : "$_id" } } }, { "$project" : { "_v" : "$_v", "_id" : 0 } }
""");
    }

    public override async Task GroupBy_Property_anonymous_element_selector_Min(bool async)
    {
        await base.GroupBy_Property_anonymous_element_selector_Min(async);

        AssertMql(
            """
Orders.{ "$group" : { "_id" : "$CustomerID", "_v" : { "$min" : "$_id" } } }, { "$project" : { "_v" : "$_v", "_id" : 0 } }
""");
    }

    public override async Task GroupBy_Property_anonymous_element_selector_Sum(bool async)
    {
        await base.GroupBy_Property_anonymous_element_selector_Sum(async);

        AssertMql(
            """
Orders.{ "$group" : { "_id" : "$CustomerID", "_v" : { "$sum" : "$_id" } } }, { "$project" : { "_v" : "$_v", "_id" : 0 } }
""");
    }

    public override async Task GroupBy_Property_anonymous_element_selector_Sum_Min_Max_Avg(bool async)
    {
        await base.GroupBy_Property_anonymous_element_selector_Sum_Min_Max_Avg(async);

        AssertMql(
            """
            Orders.{ "$group" : { "_id" : "$CustomerID", "Sum" : { "$sum" : "$_id" }, "Min" : { "$min" : "$EmployeeID" }, "Max" : { "$max" : "$EmployeeID" }, "Avg" : { "$avg" : "$_id" } } }, { "$project" : { "Sum" : "$Sum", "Min" : "$Min", "Max" : "$Max", "Avg" : "$Avg", "_id" : 0 } }
            """);
    }

    public override async Task GroupBy_element_selector_complex_aggregate(bool async)
    {
        await base.GroupBy_element_selector_complex_aggregate(async);

        AssertMql(
            """
Orders.{ "$group" : { "_id" : "$CustomerID", "_v" : { "$sum" : { "$add" : ["$_id", 1] } } } }, { "$project" : { "_v" : "$_v", "_id" : 0 } }
""");
    }

    public override async Task GroupBy_element_selector_complex_aggregate2(bool async)
    {
        await base.GroupBy_element_selector_complex_aggregate2(async);

        AssertMql(
            """
Orders.{ "$group" : { "_id" : "$CustomerID", "_v" : { "$sum" : { "$add" : ["$_id", 1] } } } }, { "$project" : { "_v" : "$_v", "_id" : 0 } }
""");
    }

    public override async Task GroupBy_element_selector_complex_aggregate3(bool async)
    {
        await base.GroupBy_element_selector_complex_aggregate3(async);

        AssertMql(
            """
Orders.{ "$group" : { "_id" : "$CustomerID", "_v" : { "$sum" : { "$add" : ["$_id", 1] } } } }, { "$project" : { "_v" : "$_v", "_id" : 0 } }
""");
    }

    public override async Task GroupBy_element_selector_complex_aggregate4(bool async)
    {
        await base.GroupBy_element_selector_complex_aggregate4(async);

        AssertMql(
            """
Orders.{ "$group" : { "_id" : "$CustomerID", "_v" : { "$sum" : { "$add" : ["$_id", 1] } } } }, { "$project" : { "_v" : "$_v", "_id" : 0 } }
""");
    }

    public override async Task Element_selector_with_case_block_repeated_inside_another_case_block_in_projection(bool async)
    {
        await base.Element_selector_with_case_block_repeated_inside_another_case_block_in_projection(async);

        AssertMql(
            """
            Orders.{ "$group" : { "_id" : { "OrderID" : "$_id" }, "Aggregate" : { "$sum" : { "$cond" : { "if" : { "$eq" : ["$CustomerID", { "$literal" : "ALFKI" }] }, "then" : { "$cond" : { "if" : { "$gt" : ["$_id", 1000] }, "then" : "$_id", "else" : { "$subtract" : [0, "$_id"] } } }, "else" : { "$subtract" : [0, { "$cond" : { "if" : { "$gt" : ["$_id", 1000] }, "then" : "$_id", "else" : { "$subtract" : [0, "$_id"] } } }] } } } } } }, { "$project" : { "OrderID" : "$_id.OrderID", "Aggregate" : "$Aggregate", "_id" : 0 } }
            """);
    }

    public override async Task GroupBy_conditional_properties(bool async)
    {
        // Fails: GroupBy issue EF-149
        await MongoSpecTestHelpers.AssertNativeTranslationFailedAsync(
            () => base.GroupBy_conditional_properties(async), typeof(ArgumentException));

        AssertMql(
        );
    }

    public override async Task GroupBy_empty_key_Aggregate(bool async)
    {
        await base.GroupBy_empty_key_Aggregate(async);

        AssertMql(
            """
            Orders.{ "$group" : { "_id" : { }, "_v" : { "$sum" : "$_id" } } }, { "$project" : { "_v" : "$_v", "_id" : 0 } }
            """);
    }

    public override async Task GroupBy_empty_key_Aggregate_Key(bool async)
    {
        await base.GroupBy_empty_key_Aggregate_Key(async);

        AssertMql(
            """
            Orders.{ "$group" : { "_id" : { }, "Sum" : { "$sum" : "$_id" } } }, { "$project" : { "Key" : "$_id", "Sum" : "$Sum", "_id" : 0 } }
            """);
    }

    public override async Task OrderBy_GroupBy_Aggregate(bool async)
    {
        await base.OrderBy_GroupBy_Aggregate(async);

        AssertMql(
            """
            Orders.{ "$sort" : { "_id" : 1 } }, { "$group" : { "_id" : "$CustomerID", "_v" : { "$sum" : "$_id" } } }, { "$project" : { "_v" : "$_v", "_id" : 0 } }
            """);
    }

    public override async Task OrderBy_Skip_GroupBy_Aggregate(bool async)
    {
        await base.OrderBy_Skip_GroupBy_Aggregate(async);

        AssertMql(
            """
            Orders.{ "$sort" : { "_id" : 1 } }, { "$skip" : 80 }, { "$group" : { "_id" : "$CustomerID", "_v" : { "$avg" : "$_id" } } }, { "$project" : { "_v" : "$_v", "_id" : 0 } }
            """);
    }

    public override async Task OrderBy_Take_GroupBy_Aggregate(bool async)
    {
        await base.OrderBy_Take_GroupBy_Aggregate(async);

        AssertMql(
            """
            Orders.{ "$sort" : { "_id" : 1 } }, { "$limit" : 500 }, { "$group" : { "_id" : "$CustomerID", "_v" : { "$min" : "$_id" } } }, { "$project" : { "_v" : "$_v", "_id" : 0 } }
            """);
    }

    public override async Task OrderBy_Skip_Take_GroupBy_Aggregate(bool async)
    {
        await base.OrderBy_Skip_Take_GroupBy_Aggregate(async);

        AssertMql(
            """
            Orders.{ "$sort" : { "_id" : 1 } }, { "$skip" : 80 }, { "$limit" : 500 }, { "$group" : { "_id" : "$CustomerID", "_v" : { "$max" : "$_id" } } }, { "$project" : { "_v" : "$_v", "_id" : 0 } }
            """);
    }

    public override async Task Distinct_GroupBy_Aggregate(bool async)
    {
        await base.Distinct_GroupBy_Aggregate(async);

        AssertMql(
            """
            Orders.{ "$group" : { "_id" : "$$ROOT" } }, { "$replaceRoot" : { "newRoot" : "$_id" } }, { "$group" : { "_id" : "$CustomerID", "c" : { "$sum" : 1 } } }, { "$project" : { "Key" : "$_id", "c" : "$c", "_id" : 0 } }
            """);
    }

    public override async Task Anonymous_projection_Distinct_GroupBy_Aggregate(bool async)
    {
        await base.Anonymous_projection_Distinct_GroupBy_Aggregate(async);

        AssertMql(
            """
            Orders.{ "$group" : { "_id" : { "OrderID" : "$_id", "EmployeeID" : "$EmployeeID" } } }, { "$project" : { "OrderID" : "$_id.OrderID", "EmployeeID" : "$_id.EmployeeID", "_id" : 0 } }, { "$group" : { "_id" : "$EmployeeID", "c" : { "$sum" : 1 } } }, { "$project" : { "Key" : "$_id", "c" : "$c", "_id" : 0 } }
            """);
    }

    public override async Task SelectMany_GroupBy_Aggregate(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.SelectMany_GroupBy_Aggregate(async));

        AssertMql(
        );
    }

    public override async Task Join_GroupBy_Aggregate(bool async)
    {
        await base.Join_GroupBy_Aggregate(async);

        AssertMql(
            """
            Orders.{ "$project" : { "_outer" : "$$ROOT", "_id" : 0 } }, { "$lookup" : { "from" : "Customers", "localField" : "_outer.CustomerID", "foreignField" : "_id", "as" : "_inner" } }, { "$unwind" : "$_inner" }, { "$project" : { "Outer" : "$_outer", "Inner" : "$_inner", "_id" : 0 } }, { "$group" : { "_id" : "$Inner._id", "__agg0" : { "$avg" : "$Outer._id" } } }, { "$project" : { "Key" : "$_id", "Count" : "$__agg0", "_id" : 0 } }
            """);
    }

    public override async Task GroupBy_required_navigation_member_Aggregate(bool async)
    {
        await base.GroupBy_required_navigation_member_Aggregate(async);

        AssertMql(
            """
            OrderDetails.{ "$project" : { "_outer" : "$$ROOT", "_id" : 0 } }, { "$lookup" : { "from" : "Orders", "localField" : "_outer._id.OrderID", "foreignField" : "_id", "as" : "_inner" } }, { "$unwind" : "$_inner" }, { "$project" : { "Outer" : "$_outer", "Inner" : "$_inner", "_id" : 0 } }, { "$group" : { "_id" : "$Inner.CustomerID", "__agg0" : { "$sum" : 1 } } }, { "$project" : { "CustomerId" : "$_id", "Count" : "$__agg0", "_id" : 0 } }
            """);
    }

    public override async Task Join_complex_GroupBy_Aggregate(bool async)
    {
        // Fails: Join/GroupJoin inner sub-query (filtered/ordered) not supported EF-X022. The inner
        // (`Customers.Where(…).OrderBy(City).Skip(10).Take(50)`) is rejected by the driver rather than mis-folded
        // into the correlated $lookup (which returned wrong rows on 3.10). See docs/failing-spec-tests.md.
        // The rejection happens after the outer collection is logged, so a partial pipeline is captured.
        await AssertTranslationFailed(() => base.Join_complex_GroupBy_Aggregate(async));

        if (MongoSpecTestHelpers.IsNativeOnly)
        {
            AssertMql();
        }
        else
        {
            AssertMql(
    """
            Orders.
            """);
        }
    }

    public override async Task GroupJoin_GroupBy_Aggregate(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupJoin_GroupBy_Aggregate(async));

        AssertMql(
        );
    }

    public override async Task GroupJoin_GroupBy_Aggregate_2(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupJoin_GroupBy_Aggregate_2(async));

        AssertMql(
        );
    }

    public override async Task GroupJoin_GroupBy_Aggregate_3(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupJoin_GroupBy_Aggregate_3(async));

        AssertMql(
        );
    }

    public override async Task GroupJoin_GroupBy_Aggregate_4(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupJoin_GroupBy_Aggregate_4(async));

        AssertMql(
        );
    }

    public override async Task GroupJoin_GroupBy_Aggregate_5(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupJoin_GroupBy_Aggregate_5(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_optional_navigation_member_Aggregate(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_optional_navigation_member_Aggregate(async));

        AssertMql(
        );
    }

    public override async Task GroupJoin_complex_GroupBy_Aggregate(bool async)
    {
        // Fails: Join/GroupJoin inner sub-query (filtered/ordered) not supported EF-X022. The inner
        // (`Orders.Where(< 10400).OrderBy(OrderDate).Take(100)`) is rejected by the driver rather than mis-folded
        // into the correlated $lookup (which returned wrong rows on 3.10). See docs/failing-spec-tests.md.
        // The rejection happens after the outer collection is logged, so a partial pipeline is captured.
        await AssertTranslationFailed(() => base.GroupJoin_complex_GroupBy_Aggregate(async));

        if (MongoSpecTestHelpers.IsNativeOnly)
        {
            AssertMql();
        }
        else
        {
            AssertMql(
    """
            Customers.
            """);
        }
    }

    public override async Task Self_join_GroupBy_Aggregate(bool async)
    {
        await base.Self_join_GroupBy_Aggregate(async);

        AssertMql(
            """
            Orders.{ "$match" : { "_id" : { "$lt" : 10400 } } }, { "$project" : { "_outer" : "$$ROOT", "_id" : 0 } }, { "$lookup" : { "from" : "Orders", "localField" : "_outer._id", "foreignField" : "_id", "as" : "_inner" } }, { "$unwind" : "$_inner" }, { "$project" : { "Outer" : "$_outer", "Inner" : "$_inner", "_id" : 0 } }, { "$group" : { "_id" : "$Outer.CustomerID", "__agg0" : { "$avg" : "$Inner._id" } } }, { "$project" : { "Key" : "$_id", "Count" : "$__agg0", "_id" : 0 } }
            """);
    }

    public override async Task GroupBy_multi_navigation_members_Aggregate(bool async)
    {
        await base.GroupBy_multi_navigation_members_Aggregate(async);

        AssertMql(
            """
            OrderDetails.{ "$project" : { "_outer" : "$$ROOT", "_id" : 0 } }, { "$lookup" : { "from" : "Orders", "localField" : "_outer._id.OrderID", "foreignField" : "_id", "as" : "_inner" } }, { "$unwind" : "$_inner" }, { "$project" : { "Outer" : "$_outer", "Inner" : "$_inner", "_id" : 0 } }, { "$project" : { "_outer" : "$$ROOT", "_id" : 0 } }, { "$lookup" : { "from" : "Products", "localField" : "_outer.Outer._id.ProductID", "foreignField" : "_id", "as" : "_inner" } }, { "$unwind" : "$_inner" }, { "$project" : { "Outer" : "$_outer", "Inner" : "$_inner", "_id" : 0 } }, { "$group" : { "_id" : { "CustomerID" : "$Outer.Inner.CustomerID", "ProductName" : "$Inner.ProductName" }, "__agg0" : { "$sum" : 1 } } }, { "$project" : { "CompositeKey" : "$_id", "Count" : "$__agg0", "_id" : 0 } }
            """);
    }

    public override async Task Union_simple_groupby(bool async)
    {
        await base.Union_simple_groupby(async);

        AssertMql(
            """
Customers.{ "$match" : { "ContactTitle" : "Owner" } }, { "$unionWith" : { "coll" : "Customers", "pipeline" : [{ "$match" : { "City" : "México D.F." } }] } }, { "$group" : { "_id" : "$$ROOT" } }, { "$replaceRoot" : { "newRoot" : "$_id" } }, { "$group" : { "_id" : "$City", "Total" : { "$sum" : 1 } } }, { "$project" : { "Key" : "$_id", "Total" : "$Total", "_id" : 0 } }
""");
    }

    public override async Task Select_anonymous_GroupBy_Aggregate(bool async)
    {
        await base.Select_anonymous_GroupBy_Aggregate(async);

        AssertMql(
            """
            Orders.{ "$match" : { "_id" : { "$lt" : 10300 } } }, { "$group" : { "_id" : "$CustomerID", "Min" : { "$min" : "$OrderDate" }, "Max" : { "$max" : "$OrderDate" }, "Sum" : { "$sum" : "$_id" }, "Avg" : { "$avg" : "$_id" } } }, { "$project" : { "Min" : "$Min", "Max" : "$Max", "Sum" : "$Sum", "Avg" : "$Avg", "_id" : 0 } }
            """);
    }

    public override async Task GroupBy_principal_key_property_optimization(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_principal_key_property_optimization(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_after_anonymous_projection_and_distinct_followed_by_another_anonymous_projection(bool async)
    {
        await base.GroupBy_after_anonymous_projection_and_distinct_followed_by_another_anonymous_projection(async);

        AssertMql(
            """
            Orders.{ "$group" : { "_id" : { "CustomerID" : "$CustomerID", "OrderID" : "$_id" } } }, { "$project" : { "CustomerID" : "$_id.CustomerID", "OrderID" : "$_id.OrderID", "_id" : 0 } }, { "$group" : { "_id" : { "CustomerID" : "$CustomerID" }, "Count" : { "$sum" : 1 } } }, { "$project" : { "Key" : "$_id.CustomerID", "Count" : "$Count", "_id" : 0 } }
            """);
    }

    public override async Task GroupBy_complex_key_aggregate(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_complex_key_aggregate(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_complex_key_aggregate_2(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_complex_key_aggregate_2(async));

        AssertMql(
        );
    }

    public override async Task Select_collection_of_scalar_before_GroupBy_aggregate(bool async)
    {
        await base.Select_collection_of_scalar_before_GroupBy_aggregate(async);

        AssertMql(
            """
            Customers.{ "$group" : { "_id" : "$City", "Count" : { "$sum" : 1 } } }, { "$project" : { "Key" : "$_id", "Count" : "$Count", "_id" : 0 } }
            """);
    }

    public override async Task GroupBy_OrderBy_key(bool async)
    {
        await base.GroupBy_OrderBy_key(async);

        AssertMql(
            """
            Orders.{ "$group" : { "_id" : "$CustomerID", "c" : { "$sum" : 1 } } }, { "$sort" : { "_id" : 1 } }, { "$project" : { "Key" : "$_id", "c" : "$c", "_id" : 0 } }
            """);
    }

    public override async Task GroupBy_OrderBy_count(bool async)
    {
        await base.GroupBy_OrderBy_count(async);

        AssertMql(
            """
            Orders.{ "$group" : { "_id" : "$CustomerID", "_orderAgg0" : { "$sum" : 1 }, "Count" : { "$sum" : 1 } } }, { "$sort" : { "_orderAgg0" : 1, "_id" : 1 } }, { "$project" : { "Key" : "$_id", "Count" : "$Count", "_id" : 0 } }
            """);
    }

    public override async Task GroupBy_OrderBy_count_Select_sum(bool async)
    {
        await base.GroupBy_OrderBy_count_Select_sum(async);

        AssertMql(
            """
            Orders.{ "$group" : { "_id" : "$CustomerID", "_orderAgg0" : { "$sum" : 1 }, "Sum" : { "$sum" : "$_id" } } }, { "$sort" : { "_orderAgg0" : 1, "_id" : 1 } }, { "$project" : { "Key" : "$_id", "Sum" : "$Sum", "_id" : 0 } }
            """);
    }

    public override async Task GroupBy_aggregate_Contains(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() =>
            base.GroupBy_aggregate_Contains(async));

        if (MongoSpecTestHelpers.IsNativeOnly)
        {
            AssertMql();
        }
        else
        {
            AssertMql(
    """
            Orders.
            """);
        }
    }

    public override async Task GroupBy_aggregate_Pushdown(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_aggregate_Pushdown(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_aggregate_using_grouping_key_Pushdown(bool async)
    {
        if (MongoSpecTestHelpers.IsNativeOnly)
        {
            // Fails: GroupBy issue EF-149
            await AssertTranslationFailed(() => base.GroupBy_aggregate_using_grouping_key_Pushdown(async));

            AssertMql();
        }
        else
        {
            // Max(e => g.Key) inside the accumulator reads the per-document key field (EF-457).
            await base.GroupBy_aggregate_using_grouping_key_Pushdown(async);

            AssertMql(
    """
            Orders.{ "$group" : { "_id" : "$CustomerID", "__agg0" : { "$sum" : 1 }, "__agg1" : { "$max" : "$CustomerID" } } }, { "$match" : { "$expr" : { "$gt" : ["$__agg0", 10] } } }, { "$project" : { "Key" : "$_id", "Max" : "$__agg1", "_id" : 0 } }, { "$sort" : { "Key" : 1 } }, { "$limit" : 20 }, { "$skip" : 4 }
            """);
        }
    }

    public override async Task GroupBy_aggregate_Pushdown_followed_by_projecting_Length(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_aggregate_Pushdown_followed_by_projecting_Length(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_aggregate_Pushdown_followed_by_projecting_constant(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_aggregate_Pushdown_followed_by_projecting_constant(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_filter_key(bool async)
    {
        await base.GroupBy_filter_key(async);

        AssertMql(
            """
Orders.{ "$group" : { "_id" : "$CustomerID", "c" : { "$sum" : 1 } } }, { "$match" : { "$expr" : { "$eq" : ["$_id", { "$literal" : "ALFKI" }] } } }, { "$project" : { "Key" : "$_id", "c" : "$c", "_id" : 0 } }
""");
    }

    public override async Task GroupBy_filter_count(bool async)
    {
        await base.GroupBy_filter_count(async);

        AssertMql(
            """
Orders.{ "$group" : { "_id" : "$CustomerID", "__agg0" : { "$sum" : 1 }, "Count" : { "$sum" : 1 } } }, { "$match" : { "$expr" : { "$gt" : ["$__agg0", 4] } } }, { "$project" : { "Key" : "$_id", "Count" : "$Count", "_id" : 0 } }
""");
    }

    public override async Task GroupBy_count_filter(bool async)
    {
        await base.GroupBy_count_filter(async);

        AssertMql(
            """
            Orders.{ "$group" : { "_id" : "Order", "__agg0" : { "$sum" : 1 } } }, { "$project" : { "Name" : "$_id", "Count" : "$__agg0", "_id" : 0 } }, { "$match" : { "Count" : { "$gt" : 0 } } }
            """);
    }

    public override async Task GroupBy_filter_count_OrderBy_count_Select_sum(bool async)
    {
        await base.GroupBy_filter_count_OrderBy_count_Select_sum(async);

        AssertMql(
            """
Orders.{ "$group" : { "_id" : "$CustomerID", "_orderAgg0" : { "$sum" : 1 }, "__agg0" : { "$sum" : 1 }, "Count" : { "$sum" : 1 }, "Sum" : { "$sum" : "$_id" } } }, { "$match" : { "$expr" : { "$gt" : ["$__agg0", 4] } } }, { "$sort" : { "_orderAgg0" : 1, "_id" : 1 } }, { "$project" : { "Key" : "$_id", "Count" : "$Count", "Sum" : "$Sum", "_id" : 0 } }
""");
    }

    public override async Task GroupBy_Aggregate_Join(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_Aggregate_Join(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_Aggregate_Join_converted_from_SelectMany(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_Aggregate_Join_converted_from_SelectMany(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_Aggregate_LeftJoin_converted_from_SelectMany(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_Aggregate_LeftJoin_converted_from_SelectMany(async));

        AssertMql(
        );
    }

    public override async Task Join_GroupBy_Aggregate_multijoins(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.Join_GroupBy_Aggregate_multijoins(async));

        AssertMql(
        );
    }

    public override async Task Join_GroupBy_Aggregate_single_join(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.Join_GroupBy_Aggregate_single_join(async));

        AssertMql(
        );
    }

    public override async Task Join_GroupBy_Aggregate_with_another_join(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.Join_GroupBy_Aggregate_with_another_join(async));

        AssertMql(
        );
    }

    public override async Task Join_GroupBy_Aggregate_distinct_single_join(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.Join_GroupBy_Aggregate_distinct_single_join(async));

        AssertMql(
        );
    }

    public override async Task Join_GroupBy_Aggregate_with_left_join(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.Join_GroupBy_Aggregate_with_left_join(async));

        AssertMql(
        );
    }

    public override async Task Join_GroupBy_Aggregate_in_subquery(bool async)
    {
        // Declines: the inner subquery joins a grouped source; the wrong-data verdict is propagated from the
        // intermediate query (MongoSelectDefinition.PropagateFallbackWrongDataFrom). Without it the driver-LINQ
        // fallback returns 0 rows instead of 133.
        await AssertTranslationFailed(() => base.Join_GroupBy_Aggregate_in_subquery(async));

        AssertMql();
    }

    public override async Task Join_GroupBy_Aggregate_on_key(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.Join_GroupBy_Aggregate_on_key(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_with_result_selector(bool async)
    {
        // Fails: GroupBy issue EF-149
        await MongoSpecTestHelpers.AssertNativeTranslationFailedAsync(
            () => base.GroupBy_with_result_selector(async), typeof(ArgumentException));

        AssertMql(
        );
    }

    public override async Task GroupBy_Sum_constant(bool async)
    {
        await base.GroupBy_Sum_constant(async);

        AssertMql(
            """
Orders.{ "$group" : { "_id" : "$CustomerID", "_v" : { "$sum" : { "$literal" : 1 } } } }, { "$project" : { "_v" : "$_v", "_id" : 0 } }
""");
    }

    public override async Task GroupBy_Sum_constant_cast(bool async)
    {
        await base.GroupBy_Sum_constant_cast(async);

        AssertMql(
            """
Orders.{ "$group" : { "_id" : "$CustomerID", "_v" : { "$sum" : { "$literal" : 1 } } } }, { "$project" : { "_v" : "$_v", "_id" : 0 } }
""");
    }

    public override async Task Distinct_GroupBy_OrderBy_key(bool async)
    {
        await base.Distinct_GroupBy_OrderBy_key(async);

        AssertMql(
            """
            Orders.{ "$group" : { "_id" : "$$ROOT" } }, { "$replaceRoot" : { "newRoot" : "$_id" } }, { "$group" : { "_id" : "$CustomerID", "c" : { "$sum" : 1 } } }, { "$sort" : { "_id" : 1 } }, { "$project" : { "Key" : "$_id", "c" : "$c", "_id" : 0 } }
            """);
    }

    public override async Task Select_nested_collection_with_groupby(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.Select_nested_collection_with_groupby(async));

        AssertMql(
        );
    }

    public override async Task Select_uncorrelated_collection_with_groupby_works(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.Select_uncorrelated_collection_with_groupby_works(async));

        AssertMql(
        );
    }

    public override async Task Select_uncorrelated_collection_with_groupby_multiple_collections_work(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.Select_uncorrelated_collection_with_groupby_multiple_collections_work(async));

        AssertMql(
        );
    }

    public override async Task Select_GroupBy_All(bool async)
    {
        await base.Select_GroupBy_All(async);

        AssertMql(
            """
Orders.{ "$group" : { "_id" : "$CustomerID" } }, { "$match" : { "$expr" : { "$ne" : ["$_id", { "$literal" : "ALFKI" }] } } }, { "$limit" : 1 }
""");
    }

    public override async Task GroupBy_Where_Average(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_Where_Average(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_Where_Count(bool async)
    {
        await base.GroupBy_Where_Count(async);

        AssertMql(
            """
Orders.{ "$group" : { "_id" : { "CustomerID" : "$CustomerID" }, "_v" : { "$sum" : { "$cond" : { "if" : { "$lt" : ["$_id", 10300] }, "then" : { "$literal" : 1 }, "else" : { "$literal" : 0 } } } } } }, { "$project" : { "_v" : "$_v", "_id" : 0 } }
""");
    }

    public override async Task GroupBy_Where_LongCount(bool async)
    {
        await base.GroupBy_Where_LongCount(async);

        AssertMql(
            """
Orders.{ "$group" : { "_id" : { "CustomerID" : "$CustomerID" }, "_v" : { "$sum" : { "$cond" : { "if" : { "$lt" : ["$_id", 10300] }, "then" : { "$literal" : 1 }, "else" : { "$literal" : 0 } } } } } }, { "$project" : { "_v" : "$_v", "_id" : 0 } }
""");
    }

    public override async Task GroupBy_Where_Max(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_Where_Max(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_Where_Min(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_Where_Min(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_Where_Sum(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_Where_Sum(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_Where_Count_with_predicate(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_Where_Count_with_predicate(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_Where_Where_Count(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_Where_Where_Count(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_Where_Select_Where_Count(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_Where_Select_Where_Count(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_Where_Select_Where_Select_Min(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_Where_Select_Where_Select_Min(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_multiple_Count_with_predicate(bool async)
    {
        await base.GroupBy_multiple_Count_with_predicate(async);

        AssertMql(
            """
Orders.{ "$group" : { "_id" : { "CustomerID" : "$CustomerID" }, "All" : { "$sum" : 1 }, "TenK" : { "$sum" : { "$cond" : { "if" : { "$lt" : ["$_id", 11000] }, "then" : { "$literal" : 1 }, "else" : { "$literal" : 0 } } } }, "EleventK" : { "$sum" : { "$cond" : { "if" : { "$lt" : ["$_id", 12000] }, "then" : { "$literal" : 1 }, "else" : { "$literal" : 0 } } } } } }, { "$project" : { "Key" : "$_id", "All" : "$All", "TenK" : "$TenK", "EleventK" : "$EleventK", "_id" : 0 } }
""");
    }

    public override async Task GroupBy_multiple_Sum_with_conditional_projection(bool async)
    {
        await base.GroupBy_multiple_Sum_with_conditional_projection(async);

        AssertMql(
            """
Orders.{ "$group" : { "_id" : { "CustomerID" : "$CustomerID" }, "TenK" : { "$sum" : { "$cond" : { "if" : { "$lt" : ["$_id", 11000] }, "then" : "$_id", "else" : { "$literal" : 0 } } } }, "EleventK" : { "$sum" : { "$cond" : { "if" : { "$gte" : ["$_id", 11000] }, "then" : "$_id", "else" : { "$literal" : 0 } } } } } }, { "$project" : { "Key" : "$_id", "TenK" : "$TenK", "EleventK" : "$EleventK", "_id" : 0 } }
""");
    }

    public override async Task GroupBy_multiple_Sum_with_Select_conditional_projection(bool async)
    {
        await base.GroupBy_multiple_Sum_with_Select_conditional_projection(async);

        AssertMql(
            """
Orders.{ "$group" : { "_id" : { "CustomerID" : "$CustomerID" }, "TenK" : { "$sum" : { "$cond" : { "if" : { "$lt" : ["$_id", 11000] }, "then" : "$_id", "else" : { "$literal" : 0 } } } }, "EleventK" : { "$sum" : { "$cond" : { "if" : { "$gte" : ["$_id", 11000] }, "then" : "$_id", "else" : { "$literal" : 0 } } } } } }, { "$project" : { "Key" : "$_id", "TenK" : "$TenK", "EleventK" : "$EleventK", "_id" : 0 } }
""");
    }

    public override async Task GroupBy_Key_as_part_of_element_selector(bool async)
    {
        await base.GroupBy_Key_as_part_of_element_selector(async);

        AssertMql(
            """
            Orders.{ "$group" : { "_id" : "$_id", "Avg" : { "$avg" : "$_id" }, "Max" : { "$max" : "$OrderDate" } } }, { "$project" : { "Key" : "$_id", "Avg" : "$Avg", "Max" : "$Max", "_id" : 0 } }
            """);
    }

    public override async Task GroupBy_composite_Key_as_part_of_element_selector(bool async)
    {
        await base.GroupBy_composite_Key_as_part_of_element_selector(async);

        AssertMql(
            """
            Orders.{ "$group" : { "_id" : { "OrderID" : "$_id", "CustomerID" : "$CustomerID" }, "Avg" : { "$avg" : "$_id" }, "Max" : { "$max" : "$OrderDate" } } }, { "$project" : { "Key" : "$_id", "Avg" : "$Avg", "Max" : "$Max", "_id" : 0 } }
            """);
    }

    public override async Task GroupBy_with_aggregate_through_navigation_property(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_with_aggregate_through_navigation_property(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_with_aggregate_containing_complex_where(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_with_aggregate_containing_complex_where(async));

        if (MongoSpecTestHelpers.IsNativeOnly)
        {
            AssertMql();
        }
        else
        {
            AssertMql(
    """
            Orders.
            """);
        }
    }

    public override async Task GroupBy_Shadow(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_Shadow(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_Shadow2(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_Shadow2(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_Shadow3(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_Shadow3(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_select_grouping_list(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_select_grouping_list(async));

        if (MongoSpecTestHelpers.IsNativeOnly)
        {
            AssertMql();
        }
        else
        {
            AssertMql(
    """
            Customers.{ "$group" : { "_id" : "$City", "_elements" : { "$push" : "$$ROOT" } } }, { "$project" : { "Key" : "$_id", "List" : "$_elements", "_id" : 0 } }
            """);
        }
    }

    public override async Task GroupBy_select_grouping_array(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_select_grouping_array(async));

        if (MongoSpecTestHelpers.IsNativeOnly)
        {
            AssertMql();
        }
        else
        {
            AssertMql(
    """
            Customers.{ "$group" : { "_id" : "$City", "_elements" : { "$push" : "$$ROOT" } } }, { "$project" : { "Key" : "$_id", "List" : "$_elements", "_id" : 0 } }
            """);
        }
    }

    public override async Task GroupBy_select_grouping_composed_list(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_select_grouping_composed_list(async));

        if (MongoSpecTestHelpers.IsNativeOnly)
        {
            AssertMql();
        }
        else
        {
            AssertMql(
    """
            Customers.{ "$group" : { "_id" : "$City", "_elements" : { "$push" : "$$ROOT" } } }, { "$project" : { "Key" : "$_id", "List" : { "$filter" : { "input" : "$_elements", "as" : "e", "cond" : { "$eq" : [{ "$indexOfCP" : ["$$e._id", "A"] }, 0] } } }, "_id" : 0 } }
            """);
        }
    }

    public override async Task GroupBy_select_grouping_composed_list_2(bool async)
    {
        // Fails: GroupBy issue EF-149
        // The driver-LINQ fallback pipeline uses $sortArray (server 5.2+); on older servers the
        // aggregate command itself is rejected ("Unknown expression $sortArray") rather than
        // failing at translation time, so that failure mode needs to be accepted too.
        if (Fixture.TestServer.SupportsSortArray)
        {
            await AssertTranslationFailed(() => base.GroupBy_select_grouping_composed_list_2(async));
        }
        else
        {
            await MongoSpecTestHelpers.AssertNativeTranslationFailedAsync(
                () => base.GroupBy_select_grouping_composed_list_2(async),
                typeof(ArgumentException), typeof(FormatException), typeof(MongoCommandException));
        }

        if (MongoSpecTestHelpers.IsNativeOnly)
        {
            AssertMql();
        }
        else
        {
            AssertMql(
    """
            Customers.{ "$group" : { "_id" : "$City", "_elements" : { "$push" : "$$ROOT" } } }, { "$project" : { "Key" : "$_id", "List" : { "$sortArray" : { "input" : "$_elements", "sortBy" : { "_id" : 1 } } }, "_id" : 0 } }
            """);
        }
    }

    public override async Task Select_GroupBy_SelectMany(bool async)
    {
        await base.Select_GroupBy_SelectMany(async);

        AssertMql();
    }

    public override async Task Count_after_GroupBy_aggregate(bool async)
    {
        await base.Count_after_GroupBy_aggregate(async);

        AssertMql(
            """
Orders.{ "$group" : { "_id" : "$CustomerID", "_v" : { "$sum" : "$_id" } } }, { "$project" : { "_v" : "$_v", "_id" : 0 } }, { "$count" : "v" }
""");
    }

    public override async Task LongCount_after_GroupBy_aggregate(bool async)
    {
        await base.LongCount_after_GroupBy_aggregate(async);

        AssertMql(
            """
Orders.{ "$group" : { "_id" : { "CustomerID" : "$CustomerID" }, "_v" : { "$sum" : { "$cond" : { "if" : { "$lt" : ["$_id", 10300] }, "then" : { "$literal" : 1 }, "else" : { "$literal" : 0 } } } } } }, { "$project" : { "_v" : "$_v", "_id" : 0 } }, { "$count" : "v" }
""");
    }

    public override async Task GroupBy_Select_Distinct_aggregate(bool async)
    {
        await base.GroupBy_Select_Distinct_aggregate(async);

        AssertMql(
            """
            Orders.{ "$group" : { "_id" : "$CustomerID", "Average" : { "$addToSet" : "$_id" }, "Count" : { "$addToSet" : "$EmployeeID" }, "LongCount" : { "$addToSet" : "$EmployeeID" }, "Max" : { "$addToSet" : "$OrderDate" }, "Min" : { "$addToSet" : "$OrderDate" }, "Sum" : { "$addToSet" : "$_id" } } }, { "$project" : { "Key" : "$_id", "Average" : { "$avg" : "$Average" }, "Count" : { "$size" : "$Count" }, "LongCount" : { "$size" : "$LongCount" }, "Max" : { "$max" : "$Max" }, "Min" : { "$min" : "$Min" }, "Sum" : { "$sum" : "$Sum" }, "_id" : 0 } }
            """);
    }

    public override async Task GroupBy_group_Distinct_Select_Distinct_aggregate(bool async)
    {
        await base.GroupBy_group_Distinct_Select_Distinct_aggregate(async);

        AssertMql(
            """
Orders.{ "$group" : { "_id" : "$CustomerID", "Max" : { "$addToSet" : "$OrderDate" } } }, { "$project" : { "Key" : "$_id", "Max" : { "$max" : "$Max" }, "_id" : 0 } }
""");
    }

    public override async Task GroupBy_group_Where_Select_Distinct_aggregate(bool async)
    {
        await base.GroupBy_group_Where_Select_Distinct_aggregate(async);

        AssertMql(
            """
Orders.{ "$group" : { "_id" : "$CustomerID", "Max" : { "$addToSet" : { "$cond" : { "if" : { "$ne" : [{ "$ifNull" : ["$OrderDate", null] }, null] }, "then" : "$OrderDate", "else" : "$$REMOVE" } } } } }, { "$project" : { "Key" : "$_id", "Max" : { "$max" : "$Max" }, "_id" : 0 } }
""");
    }

    public override async Task MinMax_after_GroupBy_aggregate(bool async)
    {
        await base.MinMax_after_GroupBy_aggregate(async);

        AssertMql(
            """
Orders.{ "$group" : { "_id" : "$CustomerID", "_v" : { "$sum" : "$_id" } } }, { "$project" : { "_v" : "$_v", "_id" : 0 } }, { "$group" : { "_id" : null, "v" : { "$min" : "$_v" } } }
""",
            //
            """
Orders.{ "$group" : { "_id" : "$CustomerID", "_v" : { "$sum" : "$_id" } } }, { "$project" : { "_v" : "$_v", "_id" : 0 } }, { "$group" : { "_id" : null, "v" : { "$max" : "$_v" } } }
""");
    }

    public override async Task All_after_GroupBy_aggregate(bool async)
    {
        await base.All_after_GroupBy_aggregate(async);

        AssertMql(
            """
            Orders.{ "$group" : { "_id" : "$CustomerID", "_v" : { "$sum" : "$_id" } } }, { "$project" : { "_v" : "$_v", "_id" : 0 } }, { "$match" : { "_id" : { "$type" : -1 } } }, { "$limit" : 1 }
            """);
    }

    public override async Task All_after_GroupBy_aggregate2(bool async)
    {
        await base.All_after_GroupBy_aggregate2(async);

        AssertMql(
            """
            Orders.{ "$group" : { "_id" : "$CustomerID", "_v" : { "$sum" : "$_id" } } }, { "$project" : { "_v" : "$_v", "_id" : 0 } }, { "$match" : { "$expr" : { "$not" : [{ "$gte" : ["$_v", 0] }] } } }, { "$limit" : 1 }
            """);
    }

    public override async Task Any_after_GroupBy_aggregate(bool async)
    {
        await base.Any_after_GroupBy_aggregate(async);

        AssertMql(
            """
            Orders.{ "$group" : { "_id" : "$CustomerID", "_v" : { "$sum" : "$_id" } } }, { "$project" : { "_v" : "$_v", "_id" : 0 } }, { "$limit" : 1 }
            """);
    }

    public override async Task Count_after_GroupBy_without_aggregate(bool async)
    {
        await base.Count_after_GroupBy_without_aggregate(async);

        AssertMql(
            """
Orders.{ "$group" : { "_id" : "$CustomerID" } }, { "$count" : "v" }
""");
    }

    public override async Task Count_with_predicate_after_GroupBy_without_aggregate(bool async)
    {
        await base.Count_with_predicate_after_GroupBy_without_aggregate(async);

        AssertMql(
            """
Orders.{ "$group" : { "_id" : "$CustomerID", "__agg0" : { "$sum" : 1 } } }, { "$match" : { "$expr" : { "$gt" : ["$__agg0", 1] } } }, { "$count" : "v" }
""");
    }

    public override async Task LongCount_after_GroupBy_without_aggregate(bool async)
    {
        await base.LongCount_after_GroupBy_without_aggregate(async);

        AssertMql(
            """
Orders.{ "$group" : { "_id" : "$CustomerID" } }, { "$count" : "v" }
""");
    }

    public override async Task LongCount_with_predicate_after_GroupBy_without_aggregate(bool async)
    {
        await base.LongCount_with_predicate_after_GroupBy_without_aggregate(async);

        AssertMql(
            """
Orders.{ "$group" : { "_id" : "$CustomerID", "__agg0" : { "$sum" : 1 } } }, { "$match" : { "$expr" : { "$gt" : ["$__agg0", 1] } } }, { "$count" : "v" }
""");
    }

    public override async Task Any_after_GroupBy_without_aggregate(bool async)
    {
        await base.Any_after_GroupBy_without_aggregate(async);

        AssertMql(
            """
Orders.{ "$group" : { "_id" : "$CustomerID" } }, { "$limit" : 1 }
""");
    }

    public override async Task Any_with_predicate_after_GroupBy_without_aggregate(bool async)
    {
        await base.Any_with_predicate_after_GroupBy_without_aggregate(async);

        AssertMql(
            """
Orders.{ "$group" : { "_id" : "$CustomerID", "__agg0" : { "$sum" : 1 } } }, { "$match" : { "$expr" : { "$gt" : ["$__agg0", 1] } } }, { "$limit" : 1 }
""");
    }

    public override async Task All_with_predicate_after_GroupBy_without_aggregate(bool async)
    {
        await base.All_with_predicate_after_GroupBy_without_aggregate(async);

        AssertMql(
            """
Orders.{ "$group" : { "_id" : "$CustomerID", "__agg0" : { "$sum" : 1 } } }, { "$match" : { "$expr" : { "$not" : [{ "$gt" : ["$__agg0", 1] }] } } }, { "$limit" : 1 }
""");
    }

    public override async Task GroupBy_aggregate_followed_by_another_GroupBy_aggregate(bool async)
    {
        await base.GroupBy_aggregate_followed_by_another_GroupBy_aggregate(async);

        AssertMql(
            """
Orders.{ "$group" : { "_id" : "$CustomerID", "Count" : { "$sum" : 1 }, "LastOrder" : { "$max" : "$_id" } } }, { "$project" : { "Key" : "$_id", "Count" : "$Count", "LastOrder" : "$LastOrder", "_id" : 0 } }, { "$group" : { "_id" : { "$literal" : 1 }, "Count" : { "$sum" : "$Count" } } }, { "$project" : { "Key" : "$_id", "Count" : "$Count", "_id" : 0 } }
""");
    }

    public override async Task GroupBy_Count_in_projection(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_Count_in_projection(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_nominal_type_count(bool async)
    {
        await base.GroupBy_nominal_type_count(async);

        AssertMql(
            """
Orders.{ "$group" : { "_id" : "$CustomerID" } }, { "$project" : { "_ctorArg0" : "$_id", "_id" : 0 } }, { "$count" : "v" }
""");
    }

    public override async Task GroupBy_based_on_renamed_property_simple(bool async)
    {
        await base.GroupBy_based_on_renamed_property_simple(async);

        AssertMql(
            """
            Customers.{ "$group" : { "_id" : { "Renamed" : "$City" }, "Count" : { "$sum" : 1 } } }, { "$project" : { "Key" : "$_id", "Count" : "$Count", "_id" : 0 } }
            """);
    }

    public override async Task GroupBy_based_on_renamed_property_complex(bool async)
    {
        await base.GroupBy_based_on_renamed_property_complex(async);

        AssertMql(
            """
            Customers.{ "$group" : { "_id" : { "Renamed" : "$City", "CustomerID" : "$_id" } } }, { "$project" : { "Renamed" : "$_id.Renamed", "CustomerID" : "$_id.CustomerID", "_id" : 0 } }, { "$group" : { "_id" : "$Renamed", "Count" : { "$sum" : 1 } } }, { "$project" : { "Key" : "$_id", "Count" : "$Count", "_id" : 0 } }
            """);
    }

    public override async Task Join_groupby_anonymous_orderby_anonymous_projection(bool async)
    {
        await base.Join_groupby_anonymous_orderby_anonymous_projection(async);

        AssertMql(
            """
            Customers.{ "$project" : { "_outer" : "$$ROOT", "_id" : 0 } }, { "$lookup" : { "from" : "Orders", "localField" : "_outer._id", "foreignField" : "CustomerID", "as" : "_inner" } }, { "$unwind" : "$_inner" }, { "$project" : { "Outer" : "$_outer", "Inner" : "$_inner", "_id" : 0 } }, { "$group" : { "_id" : { "CustomerID" : "$Outer._id", "OrderDate" : "$Inner.OrderDate" } } }, { "$sort" : { "_id.OrderDate" : 1 } }, { "$project" : { "CustomerID" : "$_id.CustomerID", "OrderDate" : "$_id.OrderDate", "_id" : 0 } }
            """);
    }

    public override async Task Odata_groupby_empty_key(bool async)
    {
        await base.Odata_groupby_empty_key(async);

        AssertMql(
            """
            Orders.{ "$group" : { "_id" : { }, "_nestedAgg1" : { "$sum" : "$_id" } } }, { "$project" : { "Container" : { "Name" : { "$literal" : "TotalAmount" }, "Value" : "$_nestedAgg1" }, "_id" : 0 } }
            """);
    }

    public override async Task GroupBy_with_group_key_access_thru_navigation(bool async)
    {
        await base.GroupBy_with_group_key_access_thru_navigation(async);

        AssertMql(
            """
            OrderDetails.{ "$project" : { "_outer" : "$$ROOT", "_id" : 0 } }, { "$lookup" : { "from" : "Orders", "localField" : "_outer._id.OrderID", "foreignField" : "_id", "as" : "_inner" } }, { "$unwind" : "$_inner" }, { "$project" : { "Outer" : "$_outer", "Inner" : "$_inner", "_id" : 0 } }, { "$group" : { "_id" : "$Inner.CustomerID", "__agg0" : { "$sum" : "$Outer._id.OrderID" } } }, { "$project" : { "Key" : "$_id", "Aggregate" : "$__agg0", "_id" : 0 } }
            """);
    }

    public override async Task GroupBy_with_group_key_access_thru_nested_navigation(bool async)
    {
        // Fails: GroupBy issue EF-149. The two-hop join chain declines at GuardAgainstUnstrippableMultiJoin
        // before any pipeline runs, on every EF version.
        await AssertTranslationFailed(() => base.GroupBy_with_group_key_access_thru_nested_navigation(async));

        AssertMql();
    }

    public override async Task GroupBy_with_group_key_being_navigation(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_with_group_key_being_navigation(async));

        if (MongoSpecTestHelpers.IsNativeOnly)
        {
            AssertMql();
        }
        else
        {
            AssertMql(
    """
            OrderDetails.{ "$project" : { "_outer" : "$$ROOT", "_id" : 0 } }, { "$lookup" : { "from" : "Orders", "localField" : "_outer._id.OrderID", "foreignField" : "_id", "as" : "_inner" } }, { "$unwind" : "$_inner" }, { "$project" : { "Outer" : "$_outer", "Inner" : "$_inner", "_id" : 0 } }, { "$group" : { "_id" : "$Inner", "__agg0" : { "$sum" : "$Outer._id.OrderID" } } }, { "$project" : { "Key" : "$_id", "Aggregate" : "$__agg0", "_id" : 0 } }
            """);
        }
    }

    public override async Task GroupBy_with_group_key_being_nested_navigation(bool async)
    {
        // Fails: GroupBy issue EF-149. The two-hop join chain declines during translation, so nothing is logged
        // on any EF version.
        await AssertTranslationFailed(() => base.GroupBy_with_group_key_being_nested_navigation(async));

        AssertMql();
    }

    public override async Task GroupBy_with_group_key_being_navigation_with_entity_key_projection(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_with_group_key_being_navigation_with_entity_key_projection(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_with_group_key_being_navigation_with_complex_projection(bool async)
    {
        await base.GroupBy_with_group_key_being_navigation_with_complex_projection(async);

        AssertMql();
    }

    public override async Task GroupBy_with_order_by_skip_and_another_order_by(bool async)
    {
        await base.GroupBy_with_order_by_skip_and_another_order_by(async);

        AssertMql(
            """
            Orders.{ "$sort" : { "CustomerID" : 1, "_id" : 1 } }, { "$skip" : 80 }, { "$sort" : { "CustomerID" : 1, "_id" : 1 } }, { "$group" : { "_id" : "$CustomerID", "_v" : { "$sum" : "$_id" } } }, { "$project" : { "_v" : "$_v", "_id" : 0 } }
            """);
    }

    public override async Task GroupBy_Property_Select_Count_with_predicate(bool async)
    {
        await base.GroupBy_Property_Select_Count_with_predicate(async);

        AssertMql(
            """
Orders.{ "$group" : { "_id" : "$CustomerID", "_v" : { "$sum" : { "$cond" : { "if" : { "$lt" : ["$_id", 10300] }, "then" : { "$literal" : 1 }, "else" : { "$literal" : 0 } } } } } }, { "$project" : { "_v" : "$_v", "_id" : 0 } }
""");
    }

    public override async Task GroupBy_Property_Select_LongCount_with_predicate(bool async)
    {
        await base.GroupBy_Property_Select_LongCount_with_predicate(async);

        AssertMql(
            """
Orders.{ "$group" : { "_id" : "$CustomerID", "_v" : { "$sum" : { "$cond" : { "if" : { "$lt" : ["$_id", 10300] }, "then" : { "$literal" : 1 }, "else" : { "$literal" : 0 } } } } } }, { "$project" : { "_v" : "$_v", "_id" : 0 } }
""");
    }

    public override async Task GroupBy_orderby_projection_with_coalesce_operation(bool async)
    {
        await base.GroupBy_orderby_projection_with_coalesce_operation(async);

        AssertMql(
            """
Customers.{ "$group" : { "_id" : "$City", "_orderAgg0" : { "$sum" : 1 }, "Count" : { "$sum" : 1 } } }, { "$sort" : { "_orderAgg0" : -1, "_id" : 1 } }, { "$project" : { "Locality" : { "$ifNull" : ["$_id", { "$literal" : "Unknown" }] }, "Count" : "$Count", "_id" : 0 } }
""");
    }

    public override async Task GroupBy_let_orderby_projection_with_coalesce_operation(bool async)
    {
        await base.GroupBy_let_orderby_projection_with_coalesce_operation(async);

        AssertMql();
    }

    public override async Task GroupBy_Min_Where_optional_relationship(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_Min_Where_optional_relationship(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_Min_Where_optional_relationship_2(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_Min_Where_optional_relationship_2(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_aggregate_over_a_subquery(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_aggregate_over_a_subquery(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_aggregate_join_with_grouping_key(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_aggregate_join_with_grouping_key(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_aggregate_join_with_group_result(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_aggregate_join_with_group_result(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_aggregate_from_right_side_of_join(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_aggregate_from_right_side_of_join(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_aggregate_join_another_GroupBy_aggregate(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_aggregate_join_another_GroupBy_aggregate(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_aggregate_after_skip_0_take_0(bool async)
    {
        await base.GroupBy_aggregate_after_skip_0_take_0(async);

        AssertMql(
            """
            Orders.{ "$skip" : 0 }, { "$match" : { "_id" : { "$type" : -1 } } }, { "$group" : { "_id" : "$CustomerID", "Total" : { "$sum" : 1 } } }, { "$project" : { "Key" : "$_id", "Total" : "$Total", "_id" : 0 } }
            """);
    }

    public override async Task GroupBy_skip_0_take_0_aggregate(bool async)
    {
        await base.GroupBy_skip_0_take_0_aggregate(async);

        AssertMql(
            """
Orders.{ "$match" : { "_id" : { "$gt" : 10500 } } }, { "$group" : { "_id" : "$CustomerID", "Total" : { "$sum" : 1 } } }, { "$skip" : 0 }, { "$match" : { "_id" : { "$type" : -1 } } }, { "$project" : { "Key" : "$_id", "Total" : "$Total", "_id" : 0 } }
""");
    }

    public override async Task GroupBy_aggregate_followed_another_GroupBy_aggregate(bool async)
    {
        await base.GroupBy_aggregate_followed_another_GroupBy_aggregate(async);

        AssertMql(
            """
Orders.{ "$group" : { "_id" : { "CustomerID" : "$CustomerID", "Year" : { "$year" : "$OrderDate" } } } }, { "$project" : { "CustomerID" : "$_id.CustomerID", "Year" : "$_id.Year", "_id" : 0 } }, { "$group" : { "_id" : "$CustomerID", "Count" : { "$sum" : 1 } } }, { "$project" : { "Key" : "$_id", "Count" : "$Count", "_id" : 0 } }
""");
    }

    public override async Task GroupBy_aggregate_without_selectMany_selecting_first(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_aggregate_without_selectMany_selecting_first(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_aggregate_left_join_GroupBy_aggregate_left_join(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_aggregate_left_join_GroupBy_aggregate_left_join(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_selecting_grouping_key_list(bool async)
    {
        await base.GroupBy_selecting_grouping_key_list(async);

        AssertMql(
            """
            Orders.{ "$group" : { "_id" : "$CustomerID", "__agg0" : { "$push" : "$CustomerID" } } }, { "$project" : { "Key" : "$_id", "Data" : "$__agg0", "_id" : 0 } }
            """);
    }

    public override async Task GroupBy_with_grouping_key_using_Like(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_with_grouping_key_using_Like(async));

        if (MongoSpecTestHelpers.IsNativeOnly)
        {
            AssertMql();
        }
        else
        {
            AssertMql(
    """
            Orders.
            """);
        }
    }

    public override async Task GroupBy_with_grouping_key_DateTime_Day(bool async)
    {
        await base.GroupBy_with_grouping_key_DateTime_Day(async);

        AssertMql(
            """
Orders.{ "$group" : { "_id" : { "$dayOfMonth" : "$OrderDate" }, "Count" : { "$sum" : 1 } } }, { "$project" : { "Key" : "$_id", "Count" : "$Count", "_id" : 0 } }
""");
    }

    public override async Task GroupBy_with_cast_inside_grouping_aggregate(bool async)
    {
        await base.GroupBy_with_cast_inside_grouping_aggregate(async);

        AssertMql(
            """
Orders.{ "$group" : { "_id" : "$CustomerID", "Count" : { "$sum" : 1 }, "Sum" : { "$sum" : "$_id" } } }, { "$project" : { "Key" : "$_id", "Count" : "$Count", "Sum" : "$Sum", "_id" : 0 } }
""");
    }

    public override async Task Complex_query_with_groupBy_in_subquery1(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.Complex_query_with_groupBy_in_subquery1(async));

        AssertMql(
        );
    }

    public override async Task Complex_query_with_groupBy_in_subquery2(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.Complex_query_with_groupBy_in_subquery2(async));

        AssertMql(
        );
    }

    public override async Task Complex_query_with_groupBy_in_subquery3(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.Complex_query_with_groupBy_in_subquery3(async));

        AssertMql(
        );
    }

    public override async Task Group_by_with_projection_into_DTO(bool async)
    {
        await base.Group_by_with_projection_into_DTO(async);

        AssertMql(
            """
            Orders.{ "$group" : { "_id" : "$_id", "Count" : { "$sum" : 1 } } }, { "$project" : { "Id" : "$_id", "Count" : "$Count", "_id" : 0 } }
            """);
    }

    public override async Task Where_select_function_groupby_followed_by_another_select_with_aggregates(bool async)
    {
        await base.Where_select_function_groupby_followed_by_another_select_with_aggregates(async);

        AssertMql(
            """
Orders.{ "$match" : { "CustomerID" : { "$regularExpression" : { "pattern" : "^A", "options" : "s" } } } }, { "$group" : { "_id" : "$CustomerID", "Sum1" : { "$sum" : { "$cond" : { "if" : { "$lte" : [{ "$subtract" : [2020, { "$year" : "$OrderDate" }] }, 30] }, "then" : "$_id", "else" : { "$literal" : 0 } } } }, "Sum2" : { "$sum" : { "$cond" : { "if" : { "$and" : [{ "$gt" : [{ "$subtract" : [2020, { "$year" : "$OrderDate" }] }, 30] }, { "$lte" : [{ "$subtract" : [2020, { "$year" : "$OrderDate" }] }, 60] }] }, "then" : "$_id", "else" : { "$literal" : 0 } } } } } }, { "$project" : { "Key" : "$_id", "Sum1" : "$Sum1", "Sum2" : "$Sum2", "_id" : 0 } }
""");
    }

    public override async Task Group_by_column_project_constant(bool async)
    {
        // Native via two pieces: the bare-body decline applies only to bare key members (isBareBodyKeyMember),
        // and TryTranslateGroupProjectionExpression falls through to TryTranslateValue to accept the constant.
        await base.Group_by_column_project_constant(async);

        AssertMql(
            """
            Orders.{ "$group" : { "_id" : "$CustomerID" } }, { "$sort" : { "_id" : 1 } }, { "$project" : { "_v" : { "$literal" : 42 }, "_id" : 0 } }
            """);
    }

    public override async Task Key_plus_key_in_projection(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.Key_plus_key_in_projection(async));

        AssertMql(
        );
    }

    public override async Task Group_by_with_arithmetic_operation_inside_aggregate(bool async)
    {
        await base.Group_by_with_arithmetic_operation_inside_aggregate(async);

        AssertMql(
            """
Orders.{ "$group" : { "_id" : "$CustomerID", "Sum" : { "$sum" : { "$add" : ["$_id", { "$strLenCP" : "$CustomerID" }] } } } }, { "$project" : { "Key" : "$_id", "Sum" : "$Sum", "_id" : 0 } }
""");
    }

    public override async Task GroupBy_scalar_subquery(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_scalar_subquery(async));

        AssertMql(
        );
    }

    public override async Task AsEnumerable_in_subquery_for_GroupBy(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.AsEnumerable_in_subquery_for_GroupBy(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_aggregate_from_multiple_query_in_same_projection(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_aggregate_from_multiple_query_in_same_projection(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_aggregate_from_multiple_query_in_same_projection_2(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_aggregate_from_multiple_query_in_same_projection_2(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_aggregate_from_multiple_query_in_same_projection_3(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.GroupBy_aggregate_from_multiple_query_in_same_projection_3(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_scalar_aggregate_in_set_operation(bool async)
    {
        await AssertNoMultiCollectionQuerySupport(() => base.GroupBy_scalar_aggregate_in_set_operation(async));
    }

    public override async Task Select_uncorrelated_collection_with_groupby_when_outer_is_distinct(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.Select_uncorrelated_collection_with_groupby_when_outer_is_distinct(async));

        AssertMql(
        );
    }

    public override async Task Select_correlated_collection_after_GroupBy_aggregate_when_identifier_does_not_change(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() =>
            base.Select_correlated_collection_after_GroupBy_aggregate_when_identifier_does_not_change(async));

        AssertMql(
        );
    }

    public override async Task Select_correlated_collection_after_GroupBy_aggregate_when_identifier_changes(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() =>
            base.Select_correlated_collection_after_GroupBy_aggregate_when_identifier_changes(async));

        if (MongoSpecTestHelpers.IsNativeOnly)
        {
            AssertMql();
        }
        else
        {
            AssertMql(
    """
            Orders.
            """);
        }
    }

    public override async Task Select_correlated_collection_after_GroupBy_aggregate_when_identifier_changes_to_complex(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() =>
            base.Select_correlated_collection_after_GroupBy_aggregate_when_identifier_changes_to_complex(async));

        if (MongoSpecTestHelpers.IsNativeOnly)
        {
            AssertMql();
        }
        else
        {
            AssertMql(
    """
            Orders.
            """);
        }
    }

    //AssertMql(" ");
    public override async Task Complex_query_with_group_by_in_subquery5(bool async)
    {
        // Fails: GroupBy issue EF-149. The accepted types stay widened here: the driver tries to BSON-serialize
        // the un-inlined MongoQuery<Customer,Customer> subquery constant and fails in BsonClassMap.Freeze(), so
        // a translation error surfaces as a serialization one. The exception type of an unsupported shape isn't
        // contract.
        await MongoSpecTestHelpers.AssertNativeTranslationFailedAsync(
            () => base.Complex_query_with_group_by_in_subquery5(async),
            typeof(ArgumentException), typeof(FormatException), typeof(BsonSerializationException));

        AssertMql();
    }

    public override async Task Complex_query_with_groupBy_in_subquery4(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.Complex_query_with_groupBy_in_subquery4(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_aggregate_SelectMany(bool async)
    {
        await base.GroupBy_aggregate_SelectMany(async);

        AssertMql();
    }

    public override async Task Final_GroupBy_property_entity(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.Final_GroupBy_property_entity(async));

        AssertMql(
        );
    }

    public override async Task Final_GroupBy_entity(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.Final_GroupBy_entity(async));

        AssertMql(
        );
    }

    public override async Task Final_GroupBy_property_entity_non_nullable(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.Final_GroupBy_property_entity_non_nullable(async));

        AssertMql(
        );
    }

    public override async Task Final_GroupBy_property_anonymous_type(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.Final_GroupBy_property_anonymous_type(async));

        AssertMql(
        );
    }

    public override async Task Final_GroupBy_multiple_properties_entity(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.Final_GroupBy_multiple_properties_entity(async));

        AssertMql(
        );
    }

    public override async Task Final_GroupBy_complex_key_entity(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.Final_GroupBy_complex_key_entity(async));

        AssertMql(
        );
    }

    public override async Task Final_GroupBy_nominal_type_entity(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.Final_GroupBy_nominal_type_entity(async));

        AssertMql(
        );
    }

    public override async Task Final_GroupBy_property_anonymous_type_element_selector(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.Final_GroupBy_property_anonymous_type_element_selector(async));

        AssertMql(
        );
    }

    public override async Task Final_GroupBy_property_entity_Include_collection(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.Final_GroupBy_property_entity_Include_collection(async));

        AssertMql(
        );
    }

    public override async Task Final_GroupBy_property_entity_projecting_collection(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.Final_GroupBy_property_entity_projecting_collection(async));

        AssertMql(
        );
    }

    public override async Task Final_GroupBy_property_entity_projecting_collection_composed(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.Final_GroupBy_property_entity_projecting_collection_composed(async));

        AssertMql(
        );
    }

    public override async Task Final_GroupBy_property_entity_projecting_collection_and_single_result(bool async)
    {
        // Fails: GroupBy issue EF-149
        await AssertTranslationFailed(() => base.Final_GroupBy_property_entity_projecting_collection_and_single_result(async));

        AssertMql(
        );
    }

    public override async Task GroupBy_Where_with_grouping_result(bool async)
    {
        await base.GroupBy_Where_with_grouping_result(async);

        AssertMql();
    }

    public override async Task GroupBy_OrderBy_with_grouping_result(bool async)
    {
        await base.GroupBy_OrderBy_with_grouping_result(async);

        AssertMql();
    }

    public override async Task GroupBy_SelectMany(bool async)
    {
        await base.GroupBy_SelectMany(async);

        AssertMql();
    }

    public override async Task OrderBy_GroupBy_SelectMany(bool async)
    {
        await base.OrderBy_GroupBy_SelectMany(async);

        AssertMql();
    }

    public override async Task OrderBy_GroupBy_SelectMany_shadow(bool async)
    {
        await base.OrderBy_GroupBy_SelectMany_shadow(async);

        AssertMql();
    }

    public override async Task GroupBy_with_orderby_take_skip_distinct_followed_by_group_key_projection(bool async)
    {
        await base.GroupBy_with_orderby_take_skip_distinct_followed_by_group_key_projection(async);

        AssertMql();
    }

    public override async Task GroupBy_Distinct(bool async)
    {
        await base.GroupBy_Distinct(async);

        AssertMql();
    }

    public override async Task GroupBy_complex_key_without_aggregate(bool async)
    {
        await AssertGroupByUnsupported(() => base.GroupBy_complex_key_without_aggregate(async));
    }

    private void AssertMql(params string[] expected)
        => Fixture.TestMqlLoggerFactory.AssertBaseline(expected);

    protected override void ClearLog()
        => Fixture.TestMqlLoggerFactory.Clear();

    // Fails: Cross-document navigation access issue EF-216
    private static Task AssertNoMultiCollectionQuerySupport(Func<Task> query)
        => MongoSpecTestHelpers.AssertNoMultiCollectionQuerySupportAsync(query);

    // Fails: GroupBy issue EF-149
    private static async Task AssertGroupByUnsupported(Func<Task> query)
        => await AssertTranslationFailed(query);

    // Shadows the base helper: an unsupported GroupBy shape must fail as a translation failure.
    // NativeOnly throws NativeTranslationNotSupportedException; under Native the driver-LINQ fallback throws an
    // EF InvalidOperationException or a driver ExpressionNotSupportedException / ArgumentException /
    // FormatException. ArgumentException (e.g. GroupJoin_GroupBy_Aggregate*, GroupBy_skip_0_take_0_aggregate)
    // and FormatException (fallback deserialization in several GroupBy_* shapes) are needed — narrowing the set
    // fails ~21 tests. Xunit assertion exceptions are not accepted, so a fallback wrong-data regression still
    // fails.
    protected new static Task AssertTranslationFailed(Func<Task> query)
        => MongoSpecTestHelpers.AssertNativeTranslationFailedAsync(
            query, typeof(ArgumentException), typeof(FormatException));
}
