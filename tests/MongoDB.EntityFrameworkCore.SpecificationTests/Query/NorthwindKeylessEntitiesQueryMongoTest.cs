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

    public override async Task KeylessEntity_by_database_view(bool async)
    {
        // Fails: Views are not supported, so this returns all entities from mapped collection.
        await Assert.ThrowsAsync<EqualException>(() => base.KeylessEntity_by_database_view(async));

        AssertMql(
            """
            Products.
            """);
    }

    public override async Task Entity_mapped_to_view_on_right_side_of_join(bool async)
    {
        // Fails: Include issue EF-117
        await AssertTranslationFailed(() => base.Entity_mapped_to_view_on_right_side_of_join(async));

        AssertMql();
    }

    public override async Task KeylessEntity_with_nav_defining_query(bool async)
    {
        // Fails: Defining queries are not supported.
        await Assert.ThrowsAsync<EqualException>(() => base.KeylessEntity_with_nav_defining_query(async));

        AssertMql(
            """
CustomerQueryWithQueryFilter.{ "$match" : { "OrderCount" : { "$gt" : 0 } } }
""");
    }

    public override async Task KeylessEntity_with_mixed_tracking(bool async)
    {
        // Fails: Multiple query roots issue EF-220
        await AssertTranslationFailed(() => base.KeylessEntity_with_mixed_tracking(async));

        AssertMql();
    }

    public override async Task KeylessEntity_with_included_nav(bool async)
    {
        // Fails: Include issue EF-117
        await AssertTranslationFailed(() => base.KeylessEntity_with_included_nav(async));

        AssertMql();
    }

    public override async Task KeylessEntity_with_defining_query(bool async)
    {
        await base.KeylessEntity_with_defining_query(async);

        AssertMql(
            """
Orders.{ "$match" : { "CustomerID" : "ALFKI" } }
""");
    }

    public override async Task KeylessEntity_with_defining_query_and_correlated_collection(bool async)
    {
        // Fails: Cross-document navigation access issue EF-216
        await AssertTranslationFailed(() => base.KeylessEntity_with_defining_query_and_correlated_collection(async));

        AssertMql();
    }

    public override async Task KeylessEntity_select_where_navigation(bool async)
    {
        // Fails: Cross-document navigation access issue EF-216
        await AssertTranslationFailed(() => base.KeylessEntity_select_where_navigation(async));

        AssertMql();
    }

    public override async Task KeylessEntity_select_where_navigation_multi_level(bool async)
    {
        // Fails: Cross-document navigation access issue EF-216
        await AssertTranslationFailed(() => base.KeylessEntity_select_where_navigation_multi_level(async));

        AssertMql();
    }

    public override async Task KeylessEntity_with_included_navs_multi_level(bool async)
    {
        // Fails: Include issue EF-117
        await AssertTranslationFailed(() => base.KeylessEntity_with_included_navs_multi_level(async));

        AssertMql();
    }

    public override async Task KeylessEntity_groupby(bool async)
    {
        // Fails: GroupBy issue EF-225
        await AssertTranslationFailed(() => base.KeylessEntity_groupby(async));

        AssertMql();
    }

    public override async Task Collection_correlated_with_keyless_entity_in_predicate_works(bool async)
    {
        // Fails: Cross-document navigation access issue EF-216
        Assert.Contains(
            "cannot be used for parameter",
            (await Assert.ThrowsAsync<ArgumentException>(async () =>
                await base.Collection_correlated_with_keyless_entity_in_predicate_works(async))).Message);

        AssertMql();
    }

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
