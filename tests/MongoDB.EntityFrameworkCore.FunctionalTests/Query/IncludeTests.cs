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
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.Extensions;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

[XUnitCollection("QueryTests")]
public class IncludeTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    private IMongoDatabase MongoDatabase => database.MongoDatabase;

    [Fact]
    public void Include_reference_dependent_to_principal_materializes()
    {
        const string testName = nameof(Include_reference_dependent_to_principal_materializes);
        // Stage 2: dependent → principal reference Include. EF nav-expansion
        // rewrites Orders.Include(o => o.Customer) into a Queryable.Join + Select
        // wrapping an IncludeExpression; MongoQueryTranslationPreprocessor's
        // IncludeJoinUnwrapper lifts that back to a plain
        // Select(p => IncludeExpression(p, default(TInner), nav)), after which
        // the Stage 1 loader infrastructure (now extended with a reference
        // branch) materializes the related principal via a per-dependent
        // sub-query.
        using var seed = new CustomerOrderContext(MongoDatabase, testName);
        seed.Database.EnsureCreated();
        seed.Customers.AddRange(
            new Customer { Id = "alfki", Name = "Alfreds" },
            new Customer { Id = "anatr", Name = "Ana Trujillo" });
        seed.Orders.AddRange(
            new Order { Id = "o1", CustomerId = "alfki" },
            new Order { Id = "o2", CustomerId = "alfki" },
            new Order { Id = "o3", CustomerId = "anatr" });
        seed.SaveChanges();

        using var db = new CustomerOrderContext(MongoDatabase, testName);
        var orders = db.Orders
            .OrderBy(o => o.Id)
            .Include(o => o.Customer)
            .ToList();

        Assert.Equal(3, orders.Count);
        Assert.All(orders, o => Assert.NotNull(o.Customer));
        Assert.Equal("Alfreds", orders[0].Customer.Name);
        Assert.Equal("Alfreds", orders[1].Customer.Name);
        Assert.Equal("Ana Trujillo", orders[2].Customer.Name);
        // Identity resolution: orders 0 and 1 share the same Customer instance.
        Assert.Same(orders[0].Customer, orders[1].Customer);
    }

    [Fact]
    public void Include_collection_principal_to_dependents_materializes()
    {
        const string testName = nameof(Include_collection_principal_to_dependents_materializes);
        // Stage 1: cross-collection collection Include is implemented via a
        // fan-out sub-query against the related collection.
        using var seed = new CustomerOrderContext(MongoDatabase, testName);
        seed.Database.EnsureCreated();
        seed.Customers.AddRange(
            new Customer { Id = "alfki", Name = "Alfreds" },
            new Customer { Id = "anatr", Name = "Ana Trujillo" });
        seed.Orders.AddRange(
            new Order { Id = "o1", CustomerId = "alfki" },
            new Order { Id = "o2", CustomerId = "alfki" },
            new Order { Id = "o3", CustomerId = "anatr" });
        seed.SaveChanges();

        using var db = new CustomerOrderContext(MongoDatabase, testName);
        var customers = db.Customers
            .OrderBy(c => c.Id)
            .Include(c => c.Orders)
            .ToList();

        Assert.Equal(2, customers.Count);
        var alfki = customers.Single(c => c.Id == "alfki");
        var anatr = customers.Single(c => c.Id == "anatr");
        Assert.Equal(2, alfki.Orders.Count);
        Assert.Single(anatr.Orders);
        Assert.All(alfki.Orders, o => Assert.Same(alfki, o.Customer));
        Assert.Same(anatr, anatr.Orders.Single().Customer);
    }

    [Fact]
    public void Collection_include_materializes_regardless_of_strategy()
    {
        const string testName = nameof(Collection_include_materializes_regardless_of_strategy);
        // Strategy-invariance anchor for EF-117 hybrid work: this test must
        // keep passing when the collection Include is later routed through
        // server-side $lookup instead of the current fan-out sub-query loader.
        using var seed = new CustomerOrderContext(MongoDatabase, testName);
        seed.Database.EnsureCreated();
        seed.Customers.AddRange(
            new Customer { Id = "alfki", Name = "Alfreds" },
            new Customer { Id = "anatr", Name = "Ana Trujillo" });
        seed.Orders.AddRange(
            new Order { Id = "o1", CustomerId = "alfki" },
            new Order { Id = "o2", CustomerId = "alfki" },
            new Order { Id = "o3", CustomerId = "anatr" });
        seed.SaveChanges();

        using var db = new CustomerOrderContext(MongoDatabase, testName);
        var customers = db.Customers
            .OrderBy(c => c.Id)
            .Include(c => c.Orders)
            .ToList();

        Assert.NotEmpty(customers);
        Assert.All(customers, c => Assert.NotNull(c.Orders));
        var alfki = customers.Single(c => c.Id == "alfki");
        var anatr = customers.Single(c => c.Id == "anatr");
        Assert.Equal(2, alfki.Orders.Count);
        Assert.Single(anatr.Orders);
    }

    [Fact]
    public void Include_collection_emits_single_lookup_query_and_materializes()
    {
        const string testName = nameof(Include_collection_emits_single_lookup_query_and_materializes);
        // EF-117 Task 2.2: a top-level principal→dependent collection Include must
        // run as a SINGLE server-side $lookup (into the `_lookup_<Nav>` field) rather
        // than the client-side fan-out (one sub-query per principal).
        using var seed = new CustomerOrderContext(MongoDatabase, testName);
        seed.Database.EnsureCreated();
        seed.Customers.AddRange(
            new Customer { Id = "alfki", Name = "Alfreds" },
            new Customer { Id = "anatr", Name = "Ana Trujillo" });
        seed.Orders.AddRange(
            new Order { Id = "o1", CustomerId = "alfki" },
            new Order { Id = "o2", CustomerId = "alfki" },
            new Order { Id = "o3", CustomerId = "anatr" });
        seed.SaveChanges();

        List<string> logs = [];
        using var db = new CustomerOrderContext(MongoDatabase, testName, logs.Add);
        var customers = db.Customers
            .OrderBy(c => c.Id)
            .Include(c => c.Orders)
            .ToList();

        // Materialization: each principal's dependents come back from the $lookup array.
        Assert.Equal(2, customers.Count);
        var alfki = customers.Single(c => c.Id == "alfki");
        var anatr = customers.Single(c => c.Id == "anatr");
        Assert.Equal(2, alfki.Orders.Count);
        Assert.Single(anatr.Orders);

        // Exactly one MQL query was executed, and it carries a $lookup into _lookup_Orders.
        var mqlQueries = logs.Where(l => l.Contains("Executed MQL query")).ToList();
        Assert.Single(mqlQueries);
        var lookupQuery = mqlQueries[0];
        Assert.Contains("$lookup", lookupQuery);
        Assert.Contains("\"as\" : \"_lookup_Orders\"", lookupQuery);
        // No fan-out sub-queries against the Orders collection (would be a second query).
        Assert.DoesNotContain(logs, l => l.Contains("Executed MQL query") && l != lookupQuery);
    }

    [Fact]
    public void Filtered_collection_Include_emits_pipeline_lookup_and_materializes_filtered_ordered_paged()
    {
        const string testName =
            nameof(Filtered_collection_Include_emits_pipeline_lookup_and_materializes_filtered_ordered_paged);
        // EF-117: a FILTERED collection Include (ordering + paging inside the include lambda)
        // runs as a SINGLE server-side $lookup using the PIPELINE form — the OrderBy / Skip / Take
        // become element-name-aware $sort / $skip / $limit stages applied after the correlation
        // $match, so the included collection is ordered and paged on the server.
        using var seed = new CustomerOrderContext(MongoDatabase, testName);
        seed.Database.EnsureCreated();
        seed.Customers.AddRange(
            new Customer { Id = "alfki", Name = "Alfreds" },
            new Customer { Id = "anatr", Name = "Ana Trujillo" });
        seed.Orders.AddRange(
            new Order { Id = "o1", CustomerId = "alfki" },
            new Order { Id = "o2", CustomerId = "alfki" },
            new Order { Id = "o3", CustomerId = "alfki" },
            new Order { Id = "o4", CustomerId = "alfki" },
            new Order { Id = "o5", CustomerId = "anatr" });
        seed.SaveChanges();

        List<string> logs = [];
        using var db = new CustomerOrderContext(MongoDatabase, testName, logs.Add);
        var customers = db.Customers
            .OrderBy(c => c.Id)
            .Include(c => c.Orders.OrderByDescending(o => o.Id).Skip(1).Take(2))
            .ToList();

        // Materialization: alfki has 4 orders; ordered by Id desc => o4,o3,o2,o1, skip 1 => o3,o2,o1,
        // take 2 => o3,o2. anatr has a single order which Skip(1) drops entirely.
        Assert.Equal(2, customers.Count);
        var alfki = customers.Single(c => c.Id == "alfki");
        var anatr = customers.Single(c => c.Id == "anatr");
        Assert.Equal(["o3", "o2"], alfki.Orders.Select(o => o.Id).ToArray());
        Assert.Empty(anatr.Orders);

        // Exactly one MQL query, carrying the PIPELINE form of $lookup (let/pipeline) with the
        // $sort / $skip / $limit stages — not a simple localField/foreignField $lookup.
        var mqlQueries = logs.Where(l => l.Contains("Executed MQL query")).ToList();
        Assert.Single(mqlQueries);
        var lookupQuery = mqlQueries[0];
        Assert.Contains("$lookup", lookupQuery);
        Assert.Contains("\"as\" : \"_lookup_Orders\"", lookupQuery);
        Assert.Contains("\"let\"", lookupQuery);
        Assert.Contains("\"pipeline\"", lookupQuery);
        Assert.Contains("\"$sort\" : { \"_id\" : -1 }", lookupQuery);
        Assert.Contains("\"$skip\" : 1", lookupQuery);
        Assert.Contains("\"$limit\" : 2", lookupQuery);
        // It is the pipeline form, so no simple foreignField on the $lookup.
        Assert.DoesNotContain("\"foreignField\"", lookupQuery);
        // No fan-out sub-queries against the Orders collection (would be a second query).
        Assert.DoesNotContain(logs, l => l.Contains("Executed MQL query") && l != lookupQuery);
    }

    [Fact]
    public void Filtered_collection_Include_with_Where_predicate_fans_out_and_materializes_only_matching()
    {
        const string testName =
            nameof(Filtered_collection_Include_with_Where_predicate_fans_out_and_materializes_only_matching);
        // EF-117: a filtered collection Include carrying a USER Where predicate
        // (Include(c => c.Orders.Where(o => o.Total > N))) cannot be rendered to a server-side
        // $match, so it routes to the CLIENT-SIDE FAN-OUT loader, which re-runs the sub-query
        // through DbContext.Set<Order>() where the driver translates the predicate. Assert the
        // included collection contains ONLY the matching dependents, and that NO $lookup for the
        // Orders nav is emitted (it is fan-out, not the simple/pipeline $lookup).
        using var seed = new CustomerOrderContext(MongoDatabase, testName);
        seed.Database.EnsureCreated();
        seed.Customers.AddRange(
            new Customer { Id = "alfki", Name = "Alfreds" },
            new Customer { Id = "anatr", Name = "Ana Trujillo" });
        seed.Orders.AddRange(
            new Order { Id = "o1", CustomerId = "alfki", Total = 5 },
            new Order { Id = "o2", CustomerId = "alfki", Total = 15 },
            new Order { Id = "o3", CustomerId = "alfki", Total = 25 },
            new Order { Id = "o4", CustomerId = "anatr", Total = 8 });
        seed.SaveChanges();

        List<string> logs = [];
        using var db = new CustomerOrderContext(MongoDatabase, testName, logs.Add);
        var customers = db.Customers
            .OrderBy(c => c.Id)
            .Include(c => c.Orders.Where(o => o.Total > 10))
            .ToList();

        // Filtering correctness: only orders with Total > 10 are materialized.
        Assert.Equal(2, customers.Count);
        var alfki = customers.Single(c => c.Id == "alfki");
        var anatr = customers.Single(c => c.Id == "anatr");
        Assert.Equal(["o2", "o3"], alfki.Orders.OrderBy(o => o.Id).Select(o => o.Id).ToArray());
        Assert.Empty(anatr.Orders); // anatr's only order (Total 8) is filtered out

        // Fan-out: no $lookup for the Orders nav anywhere; the dependents come from a separate
        // sub-query against the Orders collection.
        var mqlQueries = logs.Where(l => l.Contains("Executed MQL query")).ToList();
        Assert.DoesNotContain(mqlQueries, q => q.Contains("_lookup_Orders"));
    }

    [Fact]
    public void Filtered_collection_Include_with_Where_and_ordering_paging_fans_out_filtered_ordered_paged()
    {
        const string testName =
            nameof(Filtered_collection_Include_with_Where_and_ordering_paging_fans_out_filtered_ordered_paged);
        // EF-117: a filtered collection Include combining a USER Where predicate WITH ordering/paging
        // runs end-to-end on the fan-out path so the predicate is honored. The composition applies
        // Where → OrderBy/ThenBy → Skip → Take in EF semantic order.
        using var seed = new CustomerOrderContext(MongoDatabase, testName);
        seed.Database.EnsureCreated();
        seed.Customers.AddRange(new Customer { Id = "alfki", Name = "Alfreds" });
        seed.Orders.AddRange(
            new Order { Id = "o1", CustomerId = "alfki", Total = 5 },
            new Order { Id = "o2", CustomerId = "alfki", Total = 15 },
            new Order { Id = "o3", CustomerId = "alfki", Total = 25 },
            new Order { Id = "o4", CustomerId = "alfki", Total = 35 });
        seed.SaveChanges();

        List<string> logs = [];
        using var db = new CustomerOrderContext(MongoDatabase, testName, logs.Add);
        var customers = db.Customers
            .Include(c => c.Orders.Where(o => o.Total > 10).OrderByDescending(o => o.Id).Skip(1).Take(1))
            .ToList();

        // Total > 10 => o2,o3,o4; order by Id desc => o4,o3,o2; skip 1 => o3,o2; take 1 => o3.
        var alfki = Assert.Single(customers);
        Assert.Equal(["o3"], alfki.Orders.Select(o => o.Id).ToArray());

        var mqlQueries = logs.Where(l => l.Contains("Executed MQL query")).ToList();
        Assert.DoesNotContain(mqlQueries, q => q.Contains("_lookup_Orders"));
    }

    [Fact]
    public void ThenInclude_chain_materializes()
    {
        const string testName = nameof(ThenInclude_chain_materializes);
        // Stage 3: a ThenInclude chain. The outer collection Include
        // (Customer.Orders) is materialized via the fan-out loader; the loader
        // extracts the chained ThenInclude path (Items) from the outer's
        // NavigationExpression and applies it as a recursive .Include(path) on
        // the sub-query, so the inner collection is loaded too.
        using var seed = new ThenIncludeContext(MongoDatabase, testName);
        seed.Database.EnsureCreated();
        seed.Customers.AddRange(new ThenIncludeCustomer { Id = "alfki", Name = "Alfreds" });
        seed.Orders.AddRange(new ThenIncludeOrder { Id = "o1", CustomerId = "alfki" });
        seed.Items.AddRange(
            new ThenIncludeItem { Id = "i1", OrderId = "o1" },
            new ThenIncludeItem { Id = "i2", OrderId = "o1" });
        seed.SaveChanges();

        using var db = new ThenIncludeContext(MongoDatabase, testName);
        var customers = db.Customers
            .Include(c => c.Orders)
            .ThenInclude(o => o.Items)
            .ToList();

        var alfki = Assert.Single(customers);
        var order = Assert.Single(alfki.Orders);
        Assert.Equal(2, order.Items.Count);
        Assert.All(order.Items, i => Assert.Same(order, i.Order));
    }

    [Fact]
    public void Include_reference_and_collection_emits_two_lookups_in_single_query_and_materializes()
    {
        const string testName = nameof(Include_reference_and_collection_emits_two_lookups_in_single_query_and_materializes);
        // EF-117 Task 4.1: a root that carries BOTH a dependent→principal reference Include
        // (Order.Customer) AND a principal→dependent collection Include (Order.Items) must run
        // as ONE query emitting two independent top-level lookups — a $lookup + $unwind into
        // _lookup_Customer and a $lookup into _lookup_Items — with both navigations materialized.
        using var seed = new ThenIncludeContext(MongoDatabase, testName);
        seed.Database.EnsureCreated();
        seed.Customers.AddRange(new ThenIncludeCustomer { Id = "alfki", Name = "Alfreds" });
        seed.Orders.AddRange(
            new ThenIncludeOrder { Id = "o1", CustomerId = "alfki" },
            new ThenIncludeOrder { Id = "o2", CustomerId = "alfki" });
        seed.Items.AddRange(
            new ThenIncludeItem { Id = "i1", OrderId = "o1" },
            new ThenIncludeItem { Id = "i2", OrderId = "o1" },
            new ThenIncludeItem { Id = "i3", OrderId = "o2" });
        seed.SaveChanges();

        List<string> logs = [];
        using var db = new ThenIncludeContext(MongoDatabase, testName, logs.Add);
        var orders = db.Orders
            .OrderBy(o => o.Id)
            .Include(o => o.Customer)
            .Include(o => o.Items)
            .ToList();

        // Materialization: both the reference and the collection come back populated.
        Assert.Equal(2, orders.Count);
        Assert.All(orders, o => Assert.NotNull(o.Customer));
        Assert.All(orders, o => Assert.Equal("Alfreds", o.Customer.Name));
        // Identity resolution: both orders share the single Customer instance.
        Assert.Same(orders[0].Customer, orders[1].Customer);
        Assert.Equal(2, orders[0].Items.Count);
        Assert.Single(orders[1].Items);

        // Exactly ONE MQL query was executed, carrying both lookups (no client-side fan-out).
        var mqlQueries = logs.Where(l => l.Contains("Executed MQL query")).ToList();
        var query = Assert.Single(mqlQueries);
        Assert.Contains("\"as\" : \"_lookup_Customer\"", query);
        Assert.Contains("$unwind", query);
        Assert.Contains("\"as\" : \"_lookup_Items\"", query);
    }

    [Fact]
    public void Include_reference_then_include_collection_emits_nested_lookup_in_single_query_and_materializes()
    {
        const string testName = nameof(Include_reference_then_include_collection_emits_nested_lookup_in_single_query_and_materializes);
        // EF-117 Task 4.2: a reference-ROOTED chain Order.Customer.ThenInclude(c => c.Orders)
        // must run as ONE query: a $lookup + $unwind into _lookup_Customer, then a NESTED
        // $lookup whose `as` (and localField) are prefixed with the parent's `as`
        // (_lookup_Customer._lookup_Orders / _lookup_Customer._id), rooting the child array
        // inside the unwound parent object.
        using var seed = new ThenIncludeContext(MongoDatabase, testName);
        seed.Database.EnsureCreated();
        seed.Customers.AddRange(new ThenIncludeCustomer { Id = "alfki", Name = "Alfreds" });
        seed.Orders.AddRange(
            new ThenIncludeOrder { Id = "o1", CustomerId = "alfki" },
            new ThenIncludeOrder { Id = "o2", CustomerId = "alfki" });
        seed.SaveChanges();

        List<string> logs = [];
        using var db = new ThenIncludeContext(MongoDatabase, testName, logs.Add);
        var orders = db.Orders
            .OrderBy(o => o.Id)
            .Include(o => o.Customer)
            .ThenInclude(c => c.Orders)
            .ToList();

        Assert.Equal(2, orders.Count);
        Assert.All(orders, o => Assert.NotNull(o.Customer));
        // Identity resolution: both orders share the single Customer instance.
        Assert.Same(orders[0].Customer, orders[1].Customer);
        // The included Customer.Orders collection is materialized (both orders).
        Assert.Equal(2, orders[0].Customer.Orders.Count);
        Assert.Equal(new[] { "o1", "o2" }, orders[0].Customer.Orders.Select(o => o.Id).OrderBy(i => i).ToArray());

        // ONE query, with a nested $lookup whose `as` is dotted under the parent lookup.
        var mqlQueries = logs.Where(l => l.Contains("Executed MQL query")).ToList();
        var query = Assert.Single(mqlQueries);
        Assert.Contains("\"as\" : \"_lookup_Customer\"", query);
        Assert.Contains("$unwind", query);
        Assert.Contains("\"as\" : \"_lookup_Customer._lookup_Orders\"", query);
        Assert.Contains("\"localField\" : \"_lookup_Customer._id\"", query);
    }

    [Fact]
    public void Include_reference_then_include_reference_emits_nested_lookup_in_single_query_and_materializes()
    {
        const string testName = nameof(Include_reference_then_include_reference_emits_nested_lookup_in_single_query_and_materializes);
        // EF-117 Task 4.2: an all-reference chain Item.Order.ThenInclude(o => o.Customer).
        // Both levels are references → $lookup + $unwind at each level, the second nested
        // (and unwound) under the first via a dotted `as`/localField.
        using var seed = new ThenIncludeContext(MongoDatabase, testName);
        seed.Database.EnsureCreated();
        seed.Customers.AddRange(new ThenIncludeCustomer { Id = "alfki", Name = "Alfreds" });
        seed.Orders.AddRange(new ThenIncludeOrder { Id = "o1", CustomerId = "alfki" });
        seed.Items.AddRange(
            new ThenIncludeItem { Id = "i1", OrderId = "o1" },
            new ThenIncludeItem { Id = "i2", OrderId = "o1" });
        seed.SaveChanges();

        List<string> logs = [];
        using var db = new ThenIncludeContext(MongoDatabase, testName, logs.Add);
        var items = db.Items
            .OrderBy(i => i.Id)
            .Include(i => i.Order)
            .ThenInclude(o => o.Customer)
            .ToList();

        Assert.Equal(2, items.Count);
        Assert.All(items, i => Assert.NotNull(i.Order));
        Assert.All(items, i => Assert.NotNull(i.Order.Customer));
        Assert.All(items, i => Assert.Equal("Alfreds", i.Order.Customer.Name));
        // Identity resolution across the two items.
        Assert.Same(items[0].Order, items[1].Order);
        Assert.Same(items[0].Order.Customer, items[1].Order.Customer);

        var mqlQueries = logs.Where(l => l.Contains("Executed MQL query")).ToList();
        var query = Assert.Single(mqlQueries);
        Assert.Contains("\"as\" : \"_lookup_Order\"", query);
        Assert.Contains("\"as\" : \"_lookup_Order._lookup_Customer\"", query);
        Assert.Contains("\"localField\" : \"_lookup_Order.CustomerId\"", query);
    }

    [Fact]
    public void Include_reference_then_reference_then_collection_three_level_chain_emits_nested_lookups_and_materializes()
    {
        const string testName = nameof(Include_reference_then_reference_then_collection_three_level_chain_emits_nested_lookups_and_materializes);
        // EF-117 Task 4.2: a 3-level reference-rooted chain ending in a collection:
        // Item.Order.Customer.Orders (ref -> ref -> collection). Verifies the dotted-alias
        // nesting generalizes past two levels: _lookup_Order, _lookup_Order._lookup_Customer,
        // _lookup_Order._lookup_Customer._lookup_Orders.
        using var seed = new ThenIncludeContext(MongoDatabase, testName);
        seed.Database.EnsureCreated();
        seed.Customers.AddRange(new ThenIncludeCustomer { Id = "alfki", Name = "Alfreds" });
        seed.Orders.AddRange(
            new ThenIncludeOrder { Id = "o1", CustomerId = "alfki" },
            new ThenIncludeOrder { Id = "o2", CustomerId = "alfki" });
        seed.Items.AddRange(new ThenIncludeItem { Id = "i1", OrderId = "o1" });
        seed.SaveChanges();

        List<string> logs = [];
        using var db = new ThenIncludeContext(MongoDatabase, testName, logs.Add);
        var items = db.Items
            .Include(i => i.Order)
            .ThenInclude(o => o.Customer)
            .ThenInclude(c => c.Orders)
            .ToList();

        var item = Assert.Single(items);
        Assert.NotNull(item.Order);
        Assert.NotNull(item.Order.Customer);
        Assert.Equal("Alfreds", item.Order.Customer.Name);
        Assert.Equal(2, item.Order.Customer.Orders.Count);
        Assert.Equal(new[] { "o1", "o2" }, item.Order.Customer.Orders.Select(o => o.Id).OrderBy(i => i).ToArray());

        var mqlQueries = logs.Where(l => l.Contains("Executed MQL query")).ToList();
        var query = Assert.Single(mqlQueries);
        Assert.Contains("\"as\" : \"_lookup_Order\"", query);
        Assert.Contains("\"as\" : \"_lookup_Order._lookup_Customer\"", query);
        Assert.Contains("\"as\" : \"_lookup_Order._lookup_Customer._lookup_Orders\"", query);
        Assert.Contains("\"localField\" : \"_lookup_Order._lookup_Customer._id\"", query);
    }

    [Fact]
    public void Include_collection_as_no_tracking_materializes()
    {
        const string testName = nameof(Include_collection_as_no_tracking_materializes);
        // Stage 4: AsNoTracking on the outer query should still load related
        // collections — the loader has to propagate the outer's per-query
        // tracking behavior to its sub-query, otherwise the related entities
        // get attached to the DbContext anyway.
        using var seed = new CustomerOrderContext(MongoDatabase, testName);
        seed.Database.EnsureCreated();
        seed.Customers.AddRange(new Customer { Id = "alfki", Name = "Alfreds" });
        seed.Orders.AddRange(
            new Order { Id = "o1", CustomerId = "alfki" },
            new Order { Id = "o2", CustomerId = "alfki" });
        seed.SaveChanges();

        using var db = new CustomerOrderContext(MongoDatabase, testName);
        var customers = db.Customers
            .AsNoTracking()
            .Include(c => c.Orders)
            .ToList();

        var alfki = Assert.Single(customers);
        Assert.Equal(2, alfki.Orders.Count);
        Assert.All(alfki.Orders, o => Assert.Same(alfki, o.Customer));
        // Nothing should be tracked.
        Assert.Empty(db.ChangeTracker.Entries());
    }

    [Fact]
    public void Include_reference_as_no_tracking_materializes()
    {
        const string testName = nameof(Include_reference_as_no_tracking_materializes);
        using var seed = new CustomerOrderContext(MongoDatabase, testName);
        seed.Database.EnsureCreated();
        seed.Customers.AddRange(new Customer { Id = "alfki", Name = "Alfreds" });
        seed.Orders.AddRange(
            new Order { Id = "o1", CustomerId = "alfki" },
            new Order { Id = "o2", CustomerId = "alfki" });
        seed.SaveChanges();

        using var db = new CustomerOrderContext(MongoDatabase, testName);
        var orders = db.Orders
            .AsNoTracking()
            .Include(o => o.Customer)
            .ToList();

        Assert.Equal(2, orders.Count);
        Assert.All(orders, o => Assert.NotNull(o.Customer));
        Assert.All(orders, o => Assert.Equal("Alfreds", o.Customer.Name));
        Assert.Empty(db.ChangeTracker.Entries());
    }

    [Fact]
    public void Include_collection_no_tracking_with_identity_resolution_materializes_without_tracking()
    {
        const string testName = nameof(Include_collection_no_tracking_with_identity_resolution_materializes_without_tracking);
        // Stage 4: AsNoTrackingWithIdentityResolution propagates to the
        // include sub-query so no entities get tracked. Cross-query identity
        // resolution (two Orders pointing to the same Customer resolve to a
        // single Customer instance) is a known limitation of the fan-out
        // implementation: each sub-query has its own materialization scope.
        // Tracking-mode TrackAll DOES dedupe via the DbContext state manager
        // — see Include_reference_dependent_to_principal_materializes.
        using var seed = new CustomerOrderContext(MongoDatabase, testName);
        seed.Database.EnsureCreated();
        seed.Customers.AddRange(new Customer { Id = "alfki", Name = "Alfreds" });
        seed.Orders.AddRange(
            new Order { Id = "o1", CustomerId = "alfki" },
            new Order { Id = "o2", CustomerId = "alfki" });
        seed.SaveChanges();

        using var db = new CustomerOrderContext(MongoDatabase, testName);
        var orders = db.Orders
            .AsNoTrackingWithIdentityResolution()
            .Include(o => o.Customer)
            .ToList();

        Assert.Equal(2, orders.Count);
        Assert.All(orders, o => Assert.NotNull(o.Customer));
        Assert.All(orders, o => Assert.Equal("Alfreds", o.Customer.Name));
        Assert.Empty(db.ChangeTracker.Entries());
    }

    [Fact]
    public void Include_collection_with_no_matching_dependents_returns_empty_collection()
    {
        const string testName = nameof(Include_collection_with_no_matching_dependents_returns_empty_collection);
        using var seed = new CustomerOrderContext(MongoDatabase, testName);
        seed.Database.EnsureCreated();
        seed.Customers.AddRange(new Customer { Id = "lonely", Name = "No Orders" });
        // No Orders for this customer.
        seed.SaveChanges();

        using var db = new CustomerOrderContext(MongoDatabase, testName);
        var customers = db.Customers
            .Include(c => c.Orders)
            .ToList();

        var lonely = Assert.Single(customers);
        Assert.NotNull(lonely.Orders);
        Assert.Empty(lonely.Orders);
    }

    [Fact]
    public void Include_reference_with_missing_principal_leaves_navigation_null()
    {
        const string testName = nameof(Include_reference_with_missing_principal_leaves_navigation_null);
        using var seed = new CustomerOrderContext(MongoDatabase, testName);
        seed.Database.EnsureCreated();
        // Order with a CustomerId pointing at a Customer that doesn't exist —
        // dangling FK. The Include should leave Customer null rather than throw.
        seed.Orders.AddRange(new Order { Id = "o-orphan", CustomerId = "ghost" });
        seed.SaveChanges();

        using var db = new CustomerOrderContext(MongoDatabase, testName);
        var orders = db.Orders
            .Include(o => o.Customer)
            .ToList();

        var orphan = Assert.Single(orders);
        Assert.Null(orphan.Customer);
    }

    [Fact]
    public void Include_self_referencing_reference_materializes_manager()
    {
        const string testName = nameof(Include_self_referencing_reference_materializes_manager);
        // Stage 3.3 (review R8): a self-referencing dependent → principal reference
        // navigation (Staff.Manager, FK ManagerId on the same Staff type). The
        // server-side $lookup is on the SAME collection with localField (ManagerId)
        // != foreignField (_id); it should "just work" via the ported
        // LookupExpression with no special-casing.
        using var seed = new StaffContext(MongoDatabase, testName);
        seed.Database.EnsureCreated();
        seed.Staff.AddRange(
            new Staff { Id = "boss", Name = "Big Boss", ManagerId = null },
            new Staff { Id = "alice", Name = "Alice", ManagerId = "boss" },
            new Staff { Id = "bob", Name = "Bob", ManagerId = "boss" });
        seed.SaveChanges();

        using var db = new StaffContext(MongoDatabase, testName);
        var staff = db.Staff
            .OrderBy(s => s.Id)
            .Include(s => s.Manager)
            .ToList();

        Assert.Equal(3, staff.Count);
        // alice -> boss, bob -> boss, boss -> null (no manager; dangling/absent FK).
        var alice = staff.Single(s => s.Id == "alice");
        var bob = staff.Single(s => s.Id == "bob");
        var boss = staff.Single(s => s.Id == "boss");
        Assert.NotNull(alice.Manager);
        Assert.Equal("Big Boss", alice.Manager!.Name);
        Assert.NotNull(bob.Manager);
        Assert.Equal("Big Boss", bob.Manager!.Name);
        Assert.Null(boss.Manager);
        // Identity resolution: alice and bob share the same Manager instance (TrackAll).
        Assert.Same(alice.Manager, bob.Manager);
    }

    [Fact]
    public void Include_collection_then_include_collection_then_include_reference_materializes()
    {
        const string testName = nameof(Include_collection_then_include_collection_then_include_reference_materializes);
        // Stage 3 regression check — Customer.Orders.Items.Product is a 3-level
        // chain ending in a reference, mirroring the Northwind spec test
        // Include_collection_then_include_collection_then_include_reference
        // (Customer.Orders.OrderDetails.Product). Verifies our recursive
        // Include(path) handles reference-at-end-of-chain correctly.
        using var seed = new ThenIncludeContext(MongoDatabase, testName);
        seed.Database.EnsureCreated();
        seed.Products.AddRange(
            new ThenIncludeProduct { Id = "p1", Name = "Chai" },
            new ThenIncludeProduct { Id = "p2", Name = "Chang" });
        seed.Customers.AddRange(new ThenIncludeCustomer { Id = "alfki", Name = "Alfreds" });
        seed.Orders.AddRange(new ThenIncludeOrder { Id = "o1", CustomerId = "alfki" });
        seed.Items.AddRange(
            new ThenIncludeItem { Id = "i1", OrderId = "o1", ProductId = "p1" },
            new ThenIncludeItem { Id = "i2", OrderId = "o1", ProductId = "p2" });
        seed.SaveChanges();

        using var db = new ThenIncludeContext(MongoDatabase, testName);
        var customers = db.Customers
            .Include(c => c.Orders)
            .ThenInclude(o => o.Items)
            .ThenInclude(i => i.Product)
            .ToList();

        var alfki = Assert.Single(customers);
        var order = Assert.Single(alfki.Orders);
        Assert.Equal(2, order.Items.Count);
        Assert.All(order.Items, i => Assert.NotNull(i.Product));
        Assert.Equal(new[] { "Chai", "Chang" }, order.Items.Select(i => i.Product!.Name).OrderBy(n => n).ToArray());
    }

    [Fact]
    public void Include_skip_navigation_throws_not_supported()
    {
        // EF-117's scope explicitly excludes many-to-many (skip navigations).
        // This exception message ships as the final behavior for the M2M case.
        using var db = new PostTagContext(MongoDatabase, nameof(Include_skip_navigation_throws_not_supported));

        var ex = Assert.Throws<InvalidOperationException>(
            () => db.Posts.Include(p => p.Tags).ToList());

        Assert.Contains("many-to-many", ex.Message);
        Assert.Contains("not yet supported", ex.Message);
    }

    [Fact]
    public void Include_collection_to_composite_primary_key_member_emits_id_dotted_lookup_and_materializes()
    {
        const string testName = nameof(Include_collection_to_composite_primary_key_member_emits_id_dotted_lookup_and_materializes);
        // EF-117 composite-PK-member regression guard (mirrors the Northwind Order.OrderDetails
        // shape). OrderDetail has a composite primary key {OrderId, ProductId} stored under _id,
        // and OrderId is ALSO the single-column FK back to Order. The principal collection nav
        // Order.OrderDetails therefore derives its $lookup foreignField from a composite-PK
        // member, which LookupExpression.GetFieldPath must nest as "_id.OrderId" (not bare
        // "OrderId"). This is a single-column FK, so it stays on the ServerLookup path and is
        // unaffected by the new composite-FK collection-routing guard — it must keep working.
        using var seed = new CompositeKeyContext(MongoDatabase, testName);
        seed.Database.EnsureCreated();
        seed.Orders.AddRange(
            new CompositeOrder { Id = "10248", OrderNo = 10248 },
            new CompositeOrder { Id = "10249", OrderNo = 10249 });
        seed.OrderDetails.AddRange(
            new OrderDetail { OrderId = 10248, ProductId = 11, Quantity = 12 },
            new OrderDetail { OrderId = 10248, ProductId = 42, Quantity = 10 },
            new OrderDetail { OrderId = 10249, ProductId = 14, Quantity = 9 });
        seed.SaveChanges();

        List<string> logs = [];
        using var db = new CompositeKeyContext(MongoDatabase, testName, logs.Add);
        var orders = db.Orders
            .OrderBy(o => o.OrderNo)
            .Include(o => o.OrderDetails)
            .ToList();

        // Materialization: each Order resolves its dependents via the composite-PK member.
        Assert.Equal(2, orders.Count);
        Assert.Equal(2, orders[0].OrderDetails.Count);
        Assert.Single(orders[1].OrderDetails);
        Assert.Equal(new[] { 10, 12 }, orders[0].OrderDetails.Select(d => d.Quantity).OrderBy(q => q).ToArray());

        // The single query carries a $lookup whose foreignField is the _id-dotted path into the
        // composite key — proving GetFieldPath nested the composite-PK member under _id.
        var mqlQueries = logs.Where(l => l.Contains("Executed MQL query")).ToList();
        Assert.Single(mqlQueries);
        var lookupQuery = mqlQueries[0];
        Assert.Contains("$lookup", lookupQuery);
        Assert.Contains("\"as\" : \"_lookup_OrderDetails\"", lookupQuery);
        Assert.Contains("\"foreignField\" : \"_id.OrderId\"", lookupQuery);
    }

    private class Customer
    {
        public string Id { get; set; } = null!;
        public string Name { get; set; } = null!;
        public List<Order> Orders { get; set; } = [];
    }

    private class Order
    {
        public string Id { get; set; } = null!;
        public string CustomerId { get; set; } = null!;
        public int Total { get; set; }
        public Customer Customer { get; set; } = null!;
    }

    private class CustomerOrderContext(IMongoDatabase mongoDatabase, string suffix, Action<string>? log = null) : DbContext
    {
        public DbSet<Customer> Customers { get; set; } = null!;
        public DbSet<Order> Orders { get; set; } = null!;

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            base.OnConfiguring(optionsBuilder
                .UseMongoDB(mongoDatabase.Client, mongoDatabase.DatabaseNamespace.DatabaseName)
                .ReplaceService<Microsoft.EntityFrameworkCore.Infrastructure.IModelCacheKeyFactory, IgnoreCacheKeyFactory>()
                .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning)));
            if (log != null)
            {
                optionsBuilder.LogTo(log).EnableSensitiveDataLogging();
            }
        }

        protected override void OnModelCreating(ModelBuilder mb)
        {
            base.OnModelCreating(mb);
            mb.Entity<Customer>().ToCollection($"ef117_{suffix}_customers");
            mb.Entity<Order>().ToCollection($"ef117_{suffix}_orders");
            mb.Entity<Customer>()
                .HasMany(c => c.Orders)
                .WithOne(o => o.Customer)
                .HasForeignKey(o => o.CustomerId);
        }
    }

    private class ThenIncludeCustomer
    {
        public string Id { get; set; } = null!;
        public string Name { get; set; } = null!;
        public List<ThenIncludeOrder> Orders { get; set; } = [];
    }

    private class ThenIncludeOrder
    {
        public string Id { get; set; } = null!;
        public string CustomerId { get; set; } = null!;
        public ThenIncludeCustomer Customer { get; set; } = null!;
        public List<ThenIncludeItem> Items { get; set; } = [];
    }

    private class ThenIncludeItem
    {
        public string Id { get; set; } = null!;
        public string OrderId { get; set; } = null!;
        public ThenIncludeOrder Order { get; set; } = null!;
        public string? ProductId { get; set; }
        public ThenIncludeProduct? Product { get; set; }
    }

    private class ThenIncludeProduct
    {
        public string Id { get; set; } = null!;
        public string Name { get; set; } = null!;
    }

    private class ThenIncludeContext(IMongoDatabase mongoDatabase, string suffix, Action<string>? log = null) : DbContext
    {
        public DbSet<ThenIncludeCustomer> Customers { get; set; } = null!;
        public DbSet<ThenIncludeOrder> Orders { get; set; } = null!;
        public DbSet<ThenIncludeItem> Items { get; set; } = null!;
        public DbSet<ThenIncludeProduct> Products { get; set; } = null!;

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            base.OnConfiguring(optionsBuilder
                .UseMongoDB(mongoDatabase.Client, mongoDatabase.DatabaseNamespace.DatabaseName)
                .ReplaceService<Microsoft.EntityFrameworkCore.Infrastructure.IModelCacheKeyFactory, IgnoreCacheKeyFactory>()
                .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning)));
            if (log != null)
            {
                optionsBuilder.LogTo(log).EnableSensitiveDataLogging();
            }
        }

        protected override void OnModelCreating(ModelBuilder mb)
        {
            base.OnModelCreating(mb);
            mb.Entity<ThenIncludeCustomer>().ToCollection($"ef117_{suffix}_customers");
            mb.Entity<ThenIncludeOrder>().ToCollection($"ef117_{suffix}_orders");
            mb.Entity<ThenIncludeItem>().ToCollection($"ef117_{suffix}_items");
            mb.Entity<ThenIncludeProduct>().ToCollection($"ef117_{suffix}_products");
            mb.Entity<ThenIncludeCustomer>()
                .HasMany(c => c.Orders)
                .WithOne(o => o.Customer)
                .HasForeignKey(o => o.CustomerId);
            mb.Entity<ThenIncludeOrder>()
                .HasMany(o => o.Items)
                .WithOne(i => i.Order)
                .HasForeignKey(i => i.OrderId);
            mb.Entity<ThenIncludeItem>()
                .HasOne(i => i.Product)
                .WithMany()
                .HasForeignKey(i => i.ProductId);
        }
    }

    private class Post
    {
        public string Id { get; set; } = null!;
        public string Title { get; set; } = null!;
        public List<Tag> Tags { get; set; } = [];
    }

    private class Tag
    {
        public string Id { get; set; } = null!;
        public string Name { get; set; } = null!;
        public List<Post> Posts { get; set; } = [];
    }

    private class PostTagContext(IMongoDatabase mongoDatabase, string suffix) : DbContext
    {
        public DbSet<Post> Posts { get; set; } = null!;
        public DbSet<Tag> Tags { get; set; } = null!;

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
            => base.OnConfiguring(optionsBuilder
                .UseMongoDB(mongoDatabase.Client, mongoDatabase.DatabaseNamespace.DatabaseName)
                .ReplaceService<Microsoft.EntityFrameworkCore.Infrastructure.IModelCacheKeyFactory, IgnoreCacheKeyFactory>()
                .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning)));

        protected override void OnModelCreating(ModelBuilder mb)
        {
            base.OnModelCreating(mb);
            mb.Entity<Post>().ToCollection($"ef117_{suffix}_posts");
            mb.Entity<Tag>().ToCollection($"ef117_{suffix}_tags");
            mb.Entity<Post>().HasMany(p => p.Tags).WithMany(t => t.Posts);
        }
    }

    private class Staff
    {
        public string Id { get; set; } = null!;
        public string Name { get; set; } = null!;
        public string? ManagerId { get; set; }
        public Staff? Manager { get; set; }
    }

    private class StaffContext(IMongoDatabase mongoDatabase, string suffix) : DbContext
    {
        public DbSet<Staff> Staff { get; set; } = null!;

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
            => base.OnConfiguring(optionsBuilder
                .UseMongoDB(mongoDatabase.Client, mongoDatabase.DatabaseNamespace.DatabaseName)
                .ReplaceService<Microsoft.EntityFrameworkCore.Infrastructure.IModelCacheKeyFactory, IgnoreCacheKeyFactory>()
                .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning)));

        protected override void OnModelCreating(ModelBuilder mb)
        {
            base.OnModelCreating(mb);
            mb.Entity<Staff>().ToCollection($"ef117_{suffix}_staff");
            mb.Entity<Staff>()
                .HasOne(s => s.Manager)
                .WithMany()
                .HasForeignKey(s => s.ManagerId);
        }
    }

    private class CompositeOrder
    {
        public string Id { get; set; } = null!;
        public int OrderNo { get; set; }
        public List<OrderDetail> OrderDetails { get; set; } = [];
    }

    private class OrderDetail
    {
        public int OrderId { get; set; }
        public int ProductId { get; set; }
        public int Quantity { get; set; }
        public CompositeOrder Order { get; set; } = null!;
    }

    private class CompositeKeyContext(IMongoDatabase mongoDatabase, string suffix, Action<string>? log = null) : DbContext
    {
        public DbSet<CompositeOrder> Orders { get; set; } = null!;
        public DbSet<OrderDetail> OrderDetails { get; set; } = null!;

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            base.OnConfiguring(optionsBuilder
                .UseMongoDB(mongoDatabase.Client, mongoDatabase.DatabaseNamespace.DatabaseName)
                .ReplaceService<Microsoft.EntityFrameworkCore.Infrastructure.IModelCacheKeyFactory, IgnoreCacheKeyFactory>()
                .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning)));
            if (log != null)
            {
                optionsBuilder.LogTo(log).EnableSensitiveDataLogging();
            }
        }

        protected override void OnModelCreating(ModelBuilder mb)
        {
            base.OnModelCreating(mb);
            mb.Entity<CompositeOrder>().ToCollection($"ef117_{suffix}_orders");
            mb.Entity<OrderDetail>().ToCollection($"ef117_{suffix}_orderdetails");
            // OrderDetail's primary key is composite {OrderId, ProductId} (stored under _id), and
            // OrderId is also the FK to CompositeOrder.OrderNo. The collection nav therefore looks
            // up dependents by a composite-PK member → foreignField "_id.OrderId".
            mb.Entity<OrderDetail>().HasKey(x => new { x.OrderId, x.ProductId });
            mb.Entity<CompositeOrder>()
                .HasMany(o => o.OrderDetails)
                .WithOne(d => d.Order)
                .HasForeignKey(d => d.OrderId)
                .HasPrincipalKey(o => o.OrderNo);
        }
    }
}
