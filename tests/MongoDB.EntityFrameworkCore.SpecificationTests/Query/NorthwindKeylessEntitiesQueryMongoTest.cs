using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.TestUtilities;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace MongoDB.EntityFrameworkCore.SpecificationTests.Query;

public class NorthwindKeylessEntitiesQueryMongoTest : NorthwindKeylessEntitiesQueryTestBase<
    NorthwindQueryMongoFixture<NoopModelCustomizer>>
{
    public NorthwindKeylessEntitiesQueryMongoTest(
        NorthwindQueryMongoFixture<NoopModelCustomizer> fixture,
        ITestOutputHelper testOutputHelper)
        : base(fixture)
    {
        ClearLog();
        //Fixture.TestMqlLoggerFactory.SetTestOutputHelper(testOutputHelper);
    }

    [ConditionalFact]
    public virtual void Check_all_tests_overridden()
        => TestHelpers.AssertAllMethodsOverridden(GetType());

    public override async Task KeylessEntity_simple(bool async)
    {
        await base.KeylessEntity_simple(async);

        AssertMql(
            """
Customers.
""");
    }

    public override async Task KeylessEntity_where_simple(bool async)
    {
        await base.KeylessEntity_where_simple(async);

        AssertMql(
            """
Customers.{ "$match" : { "City" : "London" } }
""");
    }

    [ConditionalTheory(Skip = "Views are not supported, so this returns all entities from mapped collection."), MemberData(nameof(IsAsyncData))]
    public override Task KeylessEntity_by_database_view(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task Entity_mapped_to_view_on_right_side_of_join(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Defining queries are not supported."), MemberData(nameof(IsAsyncData))]
    public override Task KeylessEntity_with_nav_defining_query(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Multiple query roots issue EF-220"), MemberData(nameof(IsAsyncData))]
    public override Task KeylessEntity_with_mixed_tracking(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task KeylessEntity_with_included_nav(bool _)
        => Task.CompletedTask;

    public override async Task KeylessEntity_with_defining_query(bool async)
    {
        await base.KeylessEntity_with_defining_query(async);

        AssertMql(
            """
Orders.{ "$match" : { "CustomerID" : "ALFKI" } }
""");
    }

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task KeylessEntity_with_defining_query_and_correlated_collection(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task KeylessEntity_select_where_navigation(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Cross-document navigation access issue EF-216"), MemberData(nameof(IsAsyncData))]
    public override Task KeylessEntity_select_where_navigation_multi_level(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "Include issue EF-117"), MemberData(nameof(IsAsyncData))]
    public override Task KeylessEntity_with_included_navs_multi_level(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "GroupBy issue EF-149"), MemberData(nameof(IsAsyncData))]
    public override Task KeylessEntity_groupby(bool _)
        => Task.CompletedTask;

    [ConditionalTheory(Skip = "No multi-collection query support"), MemberData(nameof(IsAsyncData))]
    public override Task Collection_correlated_with_keyless_entity_in_predicate_works(bool _)
        => Task.CompletedTask;

    public override async Task Auto_initialized_view_set(bool async)
    {
        await base.Auto_initialized_view_set(async);

        AssertMql(
            """
Customers.
""");
    }

    public override async Task Count_over_keyless_entity(bool async)
    {
        await base.Count_over_keyless_entity(async);

        AssertMql(
            """
            Customers.{ "$count" : "_v" }
            """);
    }

    public override async Task Count_over_keyless_entity_with_pushdown(bool async)
    {
        await base.Count_over_keyless_entity_with_pushdown(async);

        AssertMql(
            """
Customers.{ "$sort" : { "ContactTitle" : 1 } }, { "$limit" : 10 }, { "$count" : "_v" }
""");
    }

    public override async Task Count_over_keyless_entity_with_pushdown_empty_projection(bool async)
    {
        await base.Count_over_keyless_entity_with_pushdown_empty_projection(async);

        AssertMql(
            """
Customers.{ "$limit" : 10 }, { "$count" : "_v" }
""");
    }

    private void AssertMql(params string[] expected)
        => Fixture.TestMqlLoggerFactory.AssertBaseline(expected);

    protected override void ClearLog()
        => Fixture.TestMqlLoggerFactory.Clear();
}
