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

public class NorthwindJoinQueryMongoTest : NorthwindJoinQueryTestBase<NorthwindQueryMongoFixture<NoopModelCustomizer>>
{
    public NorthwindJoinQueryMongoTest(
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

#if !EF8 && !EF9

    public override async Task LeftJoin(bool async)
    {
        // Failed: Throws ExpressionNotSupportedException (query not translated)
        await base.LeftJoin(async);

        AssertMql(
            """
Customers.{ "$project" : { "_outer" : "$$ROOT", "_id" : 0 } }, { "$lookup" : { "from" : "Orders", "localField" : "_outer._id", "foreignField" : "CustomerID", "as" : "_inner" } }, { "$unwind" : { "path" : "$_inner", "preserveNullAndEmptyArrays" : true } }, { "$project" : { "_outer" : "$_outer", "_inner" : "$_inner", "_id" : 0 } }
""");
    }

    public override async Task RightJoin(bool async)
    {
        // Fails: RightJoin not supported EF-X018
        await AssertTranslationFailed(() => base.RightJoin(async));

        AssertMql(
        );
    }

    public override async Task GroupJoin_aggregate_anonymous_key_selectors(bool async)
    {
        // Fails: GroupJoin shape not translated EF-X016
        await AssertTranslationFailed(() => base.GroupJoin_aggregate_anonymous_key_selectors(async));

        AssertMql(
        );
    }

    public override async Task GroupJoin_aggregate_anonymous_key_selectors2(bool async)
    {
        // Fails: GroupJoin shape not translated EF-X016
        await AssertTranslationFailed(() => base.GroupJoin_aggregate_anonymous_key_selectors2(async));

        AssertMql(
        );
    }

    public override async Task GroupJoin_aggregate_anonymous_key_selectors_one_argument(bool async)
    {
        // Fails: GroupJoin shape not translated EF-X016
        await AssertTranslationFailed(() => base.GroupJoin_aggregate_anonymous_key_selectors_one_argument(async));

        AssertMql(
        );
    }

    public override async Task GroupJoin_aggregate_nested_anonymous_key_selectors(bool async)
    {
        // Fails: GroupJoin shape not translated EF-X016
        await AssertTranslationFailed(() => base.GroupJoin_aggregate_nested_anonymous_key_selectors(async));

        AssertMql(
        );
    }

    public override async Task Join_with_key_selectors_being_nested_anonymous_objects(bool async)
    {
        // Fails: Join shape not translated EF-X017
        await AssertTranslationFailed(() => base.Join_with_key_selectors_being_nested_anonymous_objects(async));

        AssertMql(
        );
    }

#endif

    public override async Task Join_customers_orders_projection(bool async)
    {
        await base.Join_customers_orders_projection(async);
        AssertMql(
            """
Customers.{ "$project" : { "_outer" : "$$ROOT", "_id" : 0 } }, { "$lookup" : { "from" : "Orders", "localField" : "_outer._id", "foreignField" : "CustomerID", "as" : "_inner" } }, { "$unwind" : "$_inner" }, { "$project" : { "Outer" : "$_outer", "Inner" : "$_inner", "_id" : 0 } }, { "$project" : { "ContactName" : "$Outer.ContactName", "OrderID" : "$Inner._id", "_id" : 0 } }
""");
    }

    public override async Task Join_customers_orders_entities(bool async)
    {
        await base.Join_customers_orders_entities(async);

        AssertMql(
            """
Customers.{ "$match" : { "_id" : { "$regularExpression" : { "pattern" : "^F", "options" : "s" } } } }, { "$project" : { "_outer" : "$$ROOT", "_id" : 0 } }, { "$lookup" : { "from" : "Orders", "localField" : "_outer._id", "foreignField" : "CustomerID", "as" : "_inner" } }, { "$unwind" : "$_inner" }, { "$project" : { "_outer" : "$_outer", "_inner" : "$_inner", "_id" : 0 } }
""");
    }

    public override async Task Join_select_many(bool async)
    {
        await AssertTranslationFailed(() => base.Join_select_many(async));

        AssertMql(
        );
    }

    public override async Task Client_Join_select_many(bool async)
    {
        await base.Client_Join_select_many(async);

        AssertMql();
    }

    public override async Task Join_customers_orders_select(bool async)
    {
        await base.Join_customers_orders_select(async);
        AssertMql(
            """
Customers.{ "$project" : { "_outer" : "$$ROOT", "_id" : 0 } }, { "$lookup" : { "from" : "Orders", "localField" : "_outer._id", "foreignField" : "CustomerID", "as" : "_inner" } }, { "$unwind" : "$_inner" }, { "$project" : { "Outer" : "$_outer", "Inner" : "$_inner", "_id" : 0 } }, { "$project" : { "ContactName" : "$Outer.ContactName", "OrderID" : "$Inner._id", "_id" : 0 } }
""");
    }

    public override async Task Join_customers_orders_with_subquery(bool async)
    {
        // Fails: Join/GroupJoin inner sub-query (filtered/ordered) not supported EF-X022
        Assert.Contains(
            "Expression not supported",
            (await Assert.ThrowsAsync<MongoDB.Driver.Linq.ExpressionNotSupportedException>(() =>
                base.Join_customers_orders_with_subquery(async))).Message);

        AssertMql(
            """
Customers.
""");
    }

    public override async Task Join_customers_orders_with_subquery_with_take(bool async)
    {
        // Fails: Join/GroupJoin inner sub-query (filtered/ordered) not supported EF-X022
        Assert.Contains(
            "Expression not supported",
            (await Assert.ThrowsAsync<MongoDB.Driver.Linq.ExpressionNotSupportedException>(() =>
                base.Join_customers_orders_with_subquery_with_take(async))).Message);

        AssertMql(
            """
Customers.
""");
    }

    public override async Task Join_customers_orders_with_subquery_anonymous_property_method(bool async)
    {
        // Fails: Join/GroupJoin inner sub-query (filtered/ordered) not supported EF-X022
        Assert.Contains(
            "Expression not supported",
            (await Assert.ThrowsAsync<MongoDB.Driver.Linq.ExpressionNotSupportedException>(() =>
                base.Join_customers_orders_with_subquery_anonymous_property_method(async))).Message);

        AssertMql(
            """
Customers.
""");
    }

    public override async Task Join_customers_orders_with_subquery_anonymous_property_method_with_take(bool async)
    {
        // Fails: Join/GroupJoin inner sub-query (filtered/ordered) not supported EF-X022
        Assert.Contains(
            "Expression not supported",
            (await Assert.ThrowsAsync<MongoDB.Driver.Linq.ExpressionNotSupportedException>(() =>
                base.Join_customers_orders_with_subquery_anonymous_property_method_with_take(async))).Message);

        AssertMql(
            """
Customers.
""");
    }

    public override async Task Join_customers_orders_with_subquery_predicate(bool async)
    {
        // Fails: Join/GroupJoin inner sub-query (filtered/ordered) not supported EF-X022
        Assert.Contains(
            "Expression not supported",
            (await Assert.ThrowsAsync<MongoDB.Driver.Linq.ExpressionNotSupportedException>(() =>
                base.Join_customers_orders_with_subquery_predicate(async))).Message);

        AssertMql(
            """
Customers.
""");
    }

    public override async Task Join_customers_orders_with_subquery_predicate_with_take(bool async)
    {
        // Fails: Join/GroupJoin inner sub-query (filtered/ordered) not supported EF-X022
        Assert.Contains(
            "Expression not supported",
            (await Assert.ThrowsAsync<MongoDB.Driver.Linq.ExpressionNotSupportedException>(() =>
                base.Join_customers_orders_with_subquery_predicate_with_take(async))).Message);

        AssertMql(
            """
Customers.
""");
    }

    public override async Task Join_composite_key(bool async)
    {
        // Fails: Join shape not translated EF-X017
        Assert.Contains(
            "Expression not supported",
            (await Assert.ThrowsAsync<MongoDB.Driver.Linq.ExpressionNotSupportedException>(() =>
                base.Join_composite_key(async))).Message);

        AssertMql(
            """
Customers.
""");
    }

    public override async Task Join_complex_condition(bool async)
    {
        // Fails: Join shape not translated EF-X017
        Assert.Contains(
            "Expression not supported",
            (await Assert.ThrowsAsync<MongoDB.Driver.Linq.ExpressionNotSupportedException>(() =>
                base.Join_complex_condition(async))).Message);

        AssertMql(
            """
Customers.
""");
    }

    public override async Task Join_same_collection_multiple(bool async)
    {
        // Fails: two navigation-less same-target-type joins chained off root, both plain inner Join;
        // StripJoinForLookup declines the shape (bare key-equality hop has no navigation for the strip
        // to key on) and the entity-shaped result can't fall back to driver-native rendering the way a
        // scalar projection can (see GuardAgainstUnstrippableMultiJoin) - EF-X024
        await AssertTranslationFailed(() => base.Join_same_collection_multiple(async));

        AssertMql(
        );
    }

    public override async Task Join_same_collection_force_alias_uniquefication(bool async)
    {
        await base.Join_same_collection_force_alias_uniquefication(async);

        AssertMql(
            """
Orders.{ "$match" : { "CustomerID" : { "$regularExpression" : { "pattern" : "^F", "options" : "s" } } } }, { "$project" : { "_outer" : "$$ROOT", "_id" : 0 } }, { "$lookup" : { "from" : "Orders", "localField" : "_outer.CustomerID", "foreignField" : "CustomerID", "as" : "_inner" } }, { "$unwind" : "$_inner" }, { "$project" : { "_outer" : "$_outer", "_inner" : "$_inner", "_id" : 0 } }
""");
    }

    public override async Task GroupJoin_simple(bool async)
    {
        await base.GroupJoin_simple(async);

        AssertMql(
            """
Customers.{ "$match" : { "_id" : { "$regularExpression" : { "pattern" : "^F", "options" : "s" } } } }, { "$project" : { "_outer" : "$$ROOT", "_id" : 0 } }, { "$lookup" : { "from" : "Orders", "localField" : "_outer._id", "foreignField" : "CustomerID", "as" : "_inner" } }, { "$unwind" : "$_inner" }, { "$project" : { "_outer" : "$_outer", "_inner" : "$_inner", "_id" : 0 } }
""");
    }

    public override async Task GroupJoin_simple2(bool async)
    {
        await base.GroupJoin_simple2(async);

        AssertMql(
            """
Customers.{ "$project" : { "_outer" : "$$ROOT", "_id" : 0 } }, { "$lookup" : { "from" : "Orders", "localField" : "_outer._id", "foreignField" : "CustomerID", "as" : "_inner" } }, { "$unwind" : "$_inner" }, { "$project" : { "_outer" : "$_outer", "_inner" : "$_inner", "_id" : 0 } }
""");
    }

    public override async Task GroupJoin_simple3(bool async)
    {
        await base.GroupJoin_simple3(async);
        AssertMql(
            """
Customers.{ "$project" : { "_outer" : "$$ROOT", "_id" : 0 } }, { "$lookup" : { "from" : "Orders", "localField" : "_outer._id", "foreignField" : "CustomerID", "as" : "_inner" } }, { "$unwind" : "$_inner" }, { "$project" : { "Outer" : "$_outer", "Inner" : "$_inner", "_id" : 0 } }, { "$project" : { "OrderID" : "$Inner._id", "_id" : 0 } }
""");
    }

    public override async Task GroupJoin_simple_ordering(bool async)
    {
        await base.GroupJoin_simple_ordering(async);

        AssertMql(
            """
Customers.{ "$match" : { "_id" : { "$regularExpression" : { "pattern" : "^F", "options" : "s" } } } }, { "$sort" : { "City" : 1 } }, { "$project" : { "_outer" : "$$ROOT", "_id" : 0 } }, { "$lookup" : { "from" : "Orders", "localField" : "_outer._id", "foreignField" : "CustomerID", "as" : "_inner" } }, { "$unwind" : "$_inner" }, { "$project" : { "_outer" : "$_outer", "_inner" : "$_inner", "_id" : 0 } }
""");
    }

    public override async Task GroupJoin_simple_subquery(bool async)
    {
        // Fails: Join/GroupJoin inner sub-query (filtered/ordered) not supported EF-X022
        Assert.Contains(
            "Expression not supported",
            (await Assert.ThrowsAsync<MongoDB.Driver.Linq.ExpressionNotSupportedException>(() =>
                base.GroupJoin_simple_subquery(async))).Message);

        AssertMql(
            """
Customers.
""");
    }

    public override async Task GroupJoin_as_final_operator(bool async)
    {
        await AssertTranslationFailed(() => base.GroupJoin_as_final_operator(async));

        AssertMql(
        );
    }

    public override async Task Unflattened_GroupJoin_composed(bool async)
    {
        await AssertTranslationFailed(() => base.Unflattened_GroupJoin_composed(async));

        AssertMql(
        );
    }

    public override async Task Unflattened_GroupJoin_composed_2(bool async)
    {
        // Fails: GroupJoin shape not translated EF-X016
        await AssertTranslationFailed(() => base.Unflattened_GroupJoin_composed_2(async));

        AssertMql(
        );
    }

    public override async Task GroupJoin_DefaultIfEmpty(bool async)
    {
#if EF8 || EF9
        // Fails: Cross-collection Include/join not translated on EF8/EF9 EF-X020
        await AssertTranslationFailed(() => base.GroupJoin_DefaultIfEmpty(async));
        AssertMql();
#else
        // Failed: Throws ExpressionNotSupportedException (query not translated)
        await base.GroupJoin_DefaultIfEmpty(async);

        AssertMql(
            """
Customers.{ "$match" : { "_id" : { "$regularExpression" : { "pattern" : "^F", "options" : "s" } } } }, { "$project" : { "_outer" : "$$ROOT", "_id" : 0 } }, { "$lookup" : { "from" : "Orders", "localField" : "_outer._id", "foreignField" : "CustomerID", "as" : "_inner" } }, { "$unwind" : { "path" : "$_inner", "preserveNullAndEmptyArrays" : true } }, { "$project" : { "_outer" : "$_outer", "_inner" : "$_inner", "_id" : 0 } }
""");
#endif
    }

    public override async Task GroupJoin_DefaultIfEmpty_multiple(bool async)
    {
#if EF8 || EF9
        // Fails: GroupJoin shape not translated EF-X016
        await AssertTranslationFailed(() => base.GroupJoin_DefaultIfEmpty_multiple(async));

        AssertMql(
        );
#else
        // EF-375: two joins onto the same target type now flatten to one $lookup per join instead of
        // leaving the driver to nest the document twice (which threw at shaper time).
        await base.GroupJoin_DefaultIfEmpty_multiple(async);

        AssertMql(
            """
Customers.{ "$match" : { "_id" : { "$regularExpression" : { "pattern" : "^F", "options" : "s" } } } }, { "$lookup" : { "from" : "Orders", "localField" : "_id", "foreignField" : "CustomerID", "as" : "_lookup_Orders_1" } }, { "$unwind" : { "path" : "$_lookup_Orders_1", "preserveNullAndEmptyArrays" : true } }, { "$lookup" : { "from" : "Orders", "localField" : "_id", "foreignField" : "CustomerID", "as" : "_lookup_Orders" } }, { "$unwind" : { "path" : "$_lookup_Orders", "preserveNullAndEmptyArrays" : true } }
""");
#endif
    }

    public override async Task GroupJoin_DefaultIfEmpty2(bool async)
    {
        // Fails: GroupJoin shape not translated on EF8/EF9 EF-X016; on EF10 the flattened
        // GroupJoin's inner is a filtered sub-query, which is not supported EF-X022
        await Assert.ThrowsAnyAsync<Exception>(() => base.GroupJoin_DefaultIfEmpty2(async));

#if EF8 || EF9
        AssertMql(
        );
#else
        AssertMql(
            """
Employees.
""");
#endif
    }

    public override async Task GroupJoin_DefaultIfEmpty3(bool async)
    {
#if EF8 || EF9
        // Fails: Cross-collection Include/join not translated on EF8/EF9 EF-X020
        await AssertTranslationFailed(() => base.GroupJoin_DefaultIfEmpty3(async));
        AssertMql();
#else
        await base.GroupJoin_DefaultIfEmpty3(async);

        AssertMql(
            """
Customers.{ "$sort" : { "_id" : 1 } }, { "$limit" : 1 }, { "$project" : { "_outer" : "$$ROOT", "_id" : 0 } }, { "$lookup" : { "from" : "Orders", "localField" : "_outer._id", "foreignField" : "CustomerID", "as" : "_inner" } }, { "$unwind" : { "path" : "$_inner", "preserveNullAndEmptyArrays" : true } }, { "$project" : { "_outer" : "$_outer", "_inner" : "$_inner", "_id" : 0 } }
""");
#endif
    }

    public override async Task GroupJoin_Where(bool async)
    {
        await base.GroupJoin_Where(async);

        AssertMql(
            """
Customers.{ "$project" : { "_outer" : "$$ROOT", "_id" : 0 } }, { "$lookup" : { "from" : "Orders", "localField" : "_outer._id", "foreignField" : "CustomerID", "as" : "_inner" } }, { "$unwind" : "$_inner" }, { "$project" : { "_outer" : "$_outer", "_inner" : "$_inner", "_id" : 0 } }, { "$match" : { "_inner.CustomerID" : "ALFKI" } }
""");
    }

    public override async Task GroupJoin_Where_OrderBy(bool async)
    {
        await base.GroupJoin_Where_OrderBy(async);

        AssertMql(
            """
Customers.{ "$project" : { "_outer" : "$$ROOT", "_id" : 0 } }, { "$lookup" : { "from" : "Orders", "localField" : "_outer._id", "foreignField" : "CustomerID", "as" : "_inner" } }, { "$unwind" : "$_inner" }, { "$project" : { "_outer" : "$_outer", "_inner" : "$_inner", "_id" : 0 } }, { "$match" : { "$or" : [{ "_inner.CustomerID" : "ALFKI" }, { "_outer._id" : "ANATR" }] } }, { "$sort" : { "_outer.City" : 1 } }
""");
    }

    public override async Task GroupJoin_DefaultIfEmpty_Where(bool async)
    {
#if EF8 || EF9
        // Fails: Cross-collection Include/join not translated on EF8/EF9 EF-X020
        await AssertTranslationFailed(() => base.GroupJoin_DefaultIfEmpty_Where(async));
        AssertMql();
#else
        await base.GroupJoin_DefaultIfEmpty_Where(async);

        AssertMql(
            """
Customers.{ "$project" : { "_outer" : "$$ROOT", "_id" : 0 } }, { "$lookup" : { "from" : "Orders", "localField" : "_outer._id", "foreignField" : "CustomerID", "as" : "_inner" } }, { "$unwind" : { "path" : "$_inner", "preserveNullAndEmptyArrays" : true } }, { "$project" : { "_outer" : "$_outer", "_inner" : "$_inner", "_id" : 0 } }, { "$match" : { "_inner" : { "$ne" : null }, "_inner.CustomerID" : "ALFKI" } }
""");
#endif
    }

    public override async Task Join_GroupJoin_DefaultIfEmpty_Where(bool async)
    {
#if EF8 || EF9
        // Fails: Cross-collection Include/join not translated on EF8/EF9 EF-X020
        await AssertTranslationFailed(() => base.Join_GroupJoin_DefaultIfEmpty_Where(async));
        AssertMql();
#else
        // Fails: Where over a flattened multi-join chain is not translated EF-X024.
        // This shape has two INDEPENDENT joins onto the same target type (Orders), each with its own
        // forced-unwind $lookup (EF-375). The composed Where can't be reattached to one of them
        // unambiguously - both lookups resolve the same navigation with no chaining/prefix relationship
        // to disambiguate by (see EF-369's TransparentIdentifierToLookupFieldRewriter.ResolveLookup,
        // designed for self-referencing CHAINS, not independent siblings) - so the strip declines and,
        // since native rendering can't represent two forced-unwind lookups either, translation is
        // rejected rather than risk falling back to a native pipeline that would silently double-nest.
        await AssertTranslationFailed(() => base.Join_GroupJoin_DefaultIfEmpty_Where(async));
        AssertMql();
#endif
    }

    public override async Task GroupJoin_DefaultIfEmpty_Project(bool async)
    {
#if EF8 || EF9
        // Fails: Cross-collection Include/join not translated on EF8/EF9 EF-X020
        await AssertTranslationFailed(() => base.GroupJoin_DefaultIfEmpty_Project(async));
        AssertMql();
#else
        // Failed: Throws ExpressionNotSupportedException (query not translated)
        await base.GroupJoin_DefaultIfEmpty_Project(async);
        AssertMql(
            """
Customers.{ "$project" : { "_outer" : "$$ROOT", "_id" : 0 } }, { "$lookup" : { "from" : "Orders", "localField" : "_outer._id", "foreignField" : "CustomerID", "as" : "_inner" } }, { "$project" : { "_outer" : "$_outer", "_inner" : "$_inner", "_id" : 0 } }, { "$project" : { "_v" : { "$map" : { "input" : { "$cond" : { "if" : { "$eq" : [{ "$size" : "$_inner" }, 0] }, "then" : [null], "else" : "$_inner" } }, "as" : "i", "in" : { "_outer" : "$_outer", "_inner" : "$$i" } } }, "_id" : 0 } }, { "$unwind" : "$_v" }, { "$project" : { "_v" : "$_v._inner._id", "_id" : 0 } }
""");
#endif
    }

    public override async Task GroupJoin_SelectMany_subquery_with_filter(bool async)
    {
        // Fails: Join/GroupJoin inner sub-query (filtered/ordered) not supported EF-X022
        Assert.Contains(
            "Expression not supported",
            (await Assert.ThrowsAsync<MongoDB.Driver.Linq.ExpressionNotSupportedException>(() =>
                base.GroupJoin_SelectMany_subquery_with_filter(async))).Message);

        AssertMql(
            """
Customers.
""");
    }

    public override async Task GroupJoin_SelectMany_subquery_with_filter_orderby(bool async)
    {
        // Fails: Subquery selection EF-X001
        await AssertTranslationFailed(() => base.GroupJoin_SelectMany_subquery_with_filter_orderby(async));

        AssertMql(
        );
    }

    public override async Task GroupJoin_SelectMany_subquery_with_filter_and_DefaultIfEmpty(bool async)
    {
        // Fails: Subquery selection EF-X001; on EF10 the join inner is a filtered sub-query EF-X022
        await Assert.ThrowsAnyAsync<Exception>(() => base.GroupJoin_SelectMany_subquery_with_filter_and_DefaultIfEmpty(async));

#if EF8 || EF9
        AssertMql(
        );
#else
        AssertMql(
            """
Customers.
""");
#endif
    }

    public override async Task GroupJoin_SelectMany_subquery_with_filter_orderby_and_DefaultIfEmpty(bool async)
    {
        await AssertTranslationFailed(() => base.GroupJoin_SelectMany_subquery_with_filter_orderby_and_DefaultIfEmpty(async));

        AssertMql(
        );
    }

    public override async Task GroupJoin_Subquery_with_Take_Then_SelectMany_Where(bool async)
    {
        // Fails: Join/GroupJoin inner sub-query (filtered/ordered) not supported EF-X022
        Assert.Contains(
            "Expression not supported",
            (await Assert.ThrowsAsync<MongoDB.Driver.Linq.ExpressionNotSupportedException>(() =>
                base.GroupJoin_Subquery_with_Take_Then_SelectMany_Where(async))).Message);

        AssertMql(
            """
Customers.
""");
    }

    public override async Task Inner_join_with_tautology_predicate_converts_to_cross_join(bool async)
    {
        // Fails: Multiple query roots issue EF-220, and Join/GroupJoin inner sub-query (filtered/ordered) not
        // supported EF-X022. Upstream's body is
        // `from c in Customers.OrderBy(c => c.CustomerID).Take(10) join o in Orders.OrderBy(o => o.OrderID).Take(10) ...`
        // — BOTH sides are self-paging. Only the INNER (`Orders.OrderBy(OrderID).Take(10)`) matters: the outer's
        // own paging is emitted at pipeline top level and is correct. The inner is a sorted+paged sub-query, so
        // driver 3.11 rejects the whole expression with ExpressionNotSupportedException ("expression must be a
        // MongoDB IQueryable against a collection") rather than folding it into the correlated $lookup
        // sub-pipeline the way 3.10 silently did. See docs/failing-spec-tests.md § EF-X022.
        // This spelling reaches TranslateJoin on ALL THREE EF versions (an ordinary inner join needs no
        // DefaultIfEmpty normalization, unlike the Left_join_... sibling below).
        // The driver rejects the expression at translation time, but only AFTER the outer collection is logged,
        // so a partial ("Customers.") pipeline is captured on all three EF versions.
        await MongoSpecTestHelpers.AssertNativeTranslationFailedAsync(
            () => base.Inner_join_with_tautology_predicate_converts_to_cross_join(async));

        AssertMql(
            """
            Customers.
            """);
    }

    public override async Task Left_join_with_tautology_predicate_doesnt_convert_to_cross_join(bool async)
    {
        // Fails: Multiple query roots issue EF-220, and Join/GroupJoin inner sub-query (filtered/ordered) not
        // supported EF-X022. Upstream's body is
        // `from c in Customers.OrderBy(c => c.CustomerID).Take(10) join o in Orders.OrderBy(o => o.OrderID).Take(10)
        //  on ... into grouping from o in grouping.DefaultIfEmpty() ...` — BOTH sides are self-paging. Only the
        // INNER (`Orders.OrderBy(OrderID).Take(10)`) matters; the outer's own paging is emitted at pipeline top
        // level and is correct. The inner is a sorted+paged sub-query, which driver 3.11 rejects outright
        // (see docs/failing-spec-tests.md § EF-X022).
        //
        // WHICH MECHANISM ACTUALLY MAKES THIS TEST GREEN DIFFERS BY EF VERSION — measured, not assumed, and the
        // test is green either way only because AssertNativeTranslationFailedAsync accepts both
        // ExpressionNotSupportedException and InvalidOperationException:
        //   EF10: the DefaultIfEmpty spelling reaches TranslateLeftJoin, the query routes to driver-LINQ, and the
        //         driver rejects the ordered/paged inner with ExpressionNotSupportedException.
        //   EF8/EF9: the same spelling normalizes to GroupJoin(...).SelectMany(DefaultIfEmpty), TranslateSelectMany
        //         returns null, and EF throws CoreStrings.TranslationFailed (InvalidOperationException) from
        //         INSIDE the QMTEV — the query never reaches the driver at all. This is a pre-existing,
        //         unrelated SelectMany-over-a-GroupJoin-grouping gap on those versions.
        // That split is why the MQL baseline is version-conditional: reaching the driver logs the outer
        // collection before the rejection; failing inside the QMTEV logs nothing.
        await MongoSpecTestHelpers.AssertNativeTranslationFailedAsync(
            () => base.Left_join_with_tautology_predicate_doesnt_convert_to_cross_join(async));

#if EF8 || EF9
        AssertMql();
#else
        AssertMql(
            """
            Customers.
            """);
#endif
    }

    public override async Task SelectMany_with_client_eval(bool async)
    {
        // Fails: Multiple query roots issue EF-220
        await AssertTranslationFailed(() => base.SelectMany_with_client_eval(async));

        AssertMql(
        );
    }

    public override async Task SelectMany_with_client_eval_with_collection_shaper(bool async)
    {
        // Fails: Multiple query roots issue EF-220
        await AssertTranslationFailed(() => base.SelectMany_with_client_eval_with_collection_shaper(async));

        AssertMql(
        );
    }

    public override async Task SelectMany_with_client_eval_with_collection_shaper_ignored(bool async)
    {
        await AssertTranslationFailed(() => base.SelectMany_with_client_eval_with_collection_shaper_ignored(async));

        AssertMql(
        );
    }

    public override async Task SelectMany_with_client_eval_with_constructor(bool async)
    {
        // Fails: Multiple query roots issue EF-220
        await AssertTranslationFailed(() => base.SelectMany_with_client_eval_with_constructor(async));

        AssertMql(
        );
    }

    public override async Task SelectMany_with_selecting_outer_entity(bool async)
    {
        // Fails: Multiple query roots issue EF-220
        await AssertTranslationFailed(() => base.SelectMany_with_selecting_outer_entity(async));

        AssertMql(
        );
    }

    public override async Task SelectMany_with_selecting_outer_element(bool async)
    {
        // Fails: Multiple query roots issue EF-220
        await AssertTranslationFailed(() => base.SelectMany_with_selecting_outer_element(async));

        AssertMql(
        );
    }

    public override async Task SelectMany_with_selecting_outer_entity_column_and_inner_column(bool async)
    {
        // Fails: Multiple query roots issue EF-220
        await AssertTranslationFailed(() => base.SelectMany_with_selecting_outer_entity_column_and_inner_column(async));

        AssertMql(
        );
    }

    public override async Task SelectMany_correlated_subquery_take(bool async)
    {
        await AssertTranslationFailed(() => base.SelectMany_correlated_subquery_take(async));

        AssertMql(
        );
    }

    public override async Task Distinct_SelectMany_correlated_subquery_take(bool async)
    {
        await AssertTranslationFailed(() => base.Distinct_SelectMany_correlated_subquery_take(async));

        AssertMql(
        );
    }

    public override async Task Distinct_SelectMany_correlated_subquery_take_2(bool async)
    {
        // Fails: Subquery selection EF-X001
        await AssertTranslationFailed(() => base.Distinct_SelectMany_correlated_subquery_take_2(async));

        AssertMql(
        );
    }

    public override async Task Take_SelectMany_correlated_subquery_take(bool async)
    {
        await AssertTranslationFailed(() => base.Take_SelectMany_correlated_subquery_take(async));

        AssertMql(
        );
    }

    public override async Task Take_in_collection_projection_with_FirstOrDefault_on_top_level(bool async)
    {
        // Fails: Subquery selection EF-X001
        await AssertTranslationFailed(() => base.Take_in_collection_projection_with_FirstOrDefault_on_top_level(async));

        AssertMql(
        );
    }

    public override async Task Condition_on_entity_with_include(bool async)
    {
#if EF8 || EF9
        // Fails: Cross-collection Include/join not translated on EF8/EF9 EF-X020
        await AssertTranslationFailed(() => base.Condition_on_entity_with_include(async));
        AssertMql();
#else
        // Failed: Throws ExpressionNotSupportedException (query not translated)
        await base.Condition_on_entity_with_include(async);
        AssertMql(
            """
Customers.{ "$match" : { "_id" : { "$regularExpression" : { "pattern" : "^F", "options" : "s" } } } }, { "$project" : { "_outer" : "$$ROOT", "_id" : 0 } }, { "$lookup" : { "from" : "Orders", "localField" : "_outer._id", "foreignField" : "CustomerID", "as" : "_inner" } }, { "$project" : { "_outer" : "$_outer", "_inner" : "$_inner", "_id" : 0 } }, { "$project" : { "_v" : { "$map" : { "input" : { "$cond" : { "if" : { "$eq" : [{ "$size" : "$_inner" }, 0] }, "then" : [null], "else" : "$_inner" } }, "as" : "i", "in" : { "_outer" : "$_outer", "_inner" : "$$i" } } }, "_id" : 0 } }, { "$unwind" : "$_v" }, { "$project" : { "a" : { "$cond" : { "if" : { "$ne" : ["$_v._inner", null] }, "then" : "$_v._inner._id", "else" : -1 } }, "_id" : 0 } }
""");
#endif
    }

    public override async Task Join_customers_orders_entities_same_entity_twice(bool async)
    {
        await base.Join_customers_orders_entities_same_entity_twice(async);

        AssertMql(
            """
Customers.{ "$project" : { "_outer" : "$$ROOT", "_id" : 0 } }, { "$lookup" : { "from" : "Orders", "localField" : "_outer._id", "foreignField" : "CustomerID", "as" : "_inner" } }, { "$unwind" : "$_inner" }, { "$project" : { "_outer" : "$_outer", "_inner" : "$_inner", "_id" : 0 } }
""");
    }

    public override async Task Join_local_collection_int_closure_is_cached_correctly(bool async)
    {
        // Fails: Join shape not translated EF-X017
        await AssertTranslationFailed(() => base.Join_local_collection_int_closure_is_cached_correctly(async));

        AssertMql(
        );
    }

    public override async Task Join_local_string_closure_is_cached_correctly(bool async)
    {
        await base.Join_local_string_closure_is_cached_correctly(async);

        AssertMql();
    }

    public override async Task Join_local_bytes_closure_is_cached_correctly(bool async)
    {
        await base.Join_local_bytes_closure_is_cached_correctly(async);

        AssertMql();
    }

    public override async Task GroupJoin_customers_employees_shadow(bool async)
    {
        await base.GroupJoin_customers_employees_shadow(async);
        AssertMql(
            """
Customers.{ "$project" : { "_outer" : "$$ROOT", "_id" : 0 } }, { "$lookup" : { "from" : "Employees", "localField" : "_outer.City", "foreignField" : "City", "as" : "_inner" } }, { "$unwind" : "$_inner" }, { "$project" : { "Outer" : "$_outer", "Inner" : "$_inner", "_id" : 0 } }, { "$project" : { "Title" : "$Inner.Title", "_id" : "$Inner._id" } }
""");
    }

    public override async Task GroupJoin_customers_employees_subquery_shadow(bool async)
    {
        // Fails: Join/GroupJoin inner sub-query (filtered/ordered) not supported EF-X022
        Assert.Contains(
            "Expression not supported",
            (await Assert.ThrowsAsync<MongoDB.Driver.Linq.ExpressionNotSupportedException>(() =>
                base.GroupJoin_customers_employees_subquery_shadow(async))).Message);

        AssertMql(
            """
Customers.
""");
    }

    public override async Task GroupJoin_customers_employees_subquery_shadow_take(bool async)
    {
        // Fails: Join/GroupJoin inner sub-query (filtered/ordered) not supported EF-X022
        Assert.Contains(
            "Expression not supported",
            (await Assert.ThrowsAsync<MongoDB.Driver.Linq.ExpressionNotSupportedException>(() =>
                base.GroupJoin_customers_employees_subquery_shadow_take(async))).Message);

        AssertMql(
            """
Customers.
""");
    }

    public override async Task GroupJoin_projection(bool async)
    {
        await base.GroupJoin_projection(async);

        AssertMql(
            """
Customers.{ "$match" : { "_id" : { "$regularExpression" : { "pattern" : "^F", "options" : "s" } } } }, { "$project" : { "_outer" : "$$ROOT", "_id" : 0 } }, { "$lookup" : { "from" : "Orders", "localField" : "_outer._id", "foreignField" : "CustomerID", "as" : "_inner" } }, { "$unwind" : "$_inner" }, { "$project" : { "_outer" : "$_outer", "_inner" : "$_inner", "_id" : 0 } }
""");
    }

    public override async Task GroupJoin_subquery_projection_outer_mixed(bool async)
    {
        // Fails: GroupJoin shape not translated EF-X016
        await AssertTranslationFailed(() => base.GroupJoin_subquery_projection_outer_mixed(async));

        AssertMql(
        );
    }

#if EF9
    public override async Task GroupJoin_on_true_equal_true(bool async)
    {
        // Fails: GroupJoin shape not translated EF-X016
        await AssertTranslationFailed(() => base.GroupJoin_on_true_equal_true(async));

        AssertMql(
);
    }

#endif

    private void AssertMql(params string[] expected)
        => Fixture.TestMqlLoggerFactory.AssertBaseline(expected);

    protected override void ClearLog()
        => Fixture.TestMqlLoggerFactory.Clear();
}
