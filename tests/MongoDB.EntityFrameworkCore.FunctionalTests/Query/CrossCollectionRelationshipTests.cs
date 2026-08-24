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
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.Extensions;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// Characterization / lock-in tests for the EF-117 cross-collection relationship feature:
/// an entity that has its own <see cref="DbSet{TEntity}"/> and is reached by a navigation into a
/// SEPARATE collection via a foreign key (not embedded). These tests cover the write/lifecycle path
/// (Group A — version-agnostic where it does not execute an Include query) and serialization /
/// change-tracking behavior through the <c>$lookup</c>-based Include (Groups B and C — EF10-only,
/// because cross-collection Include QUERY translation only works on EF10; see EF-X020).
/// C# property names intentionally differ from BSON element names to verify element-name mapping.
/// </summary>
[XUnitCollection("QueryTests")]
public class CrossCollectionRelationshipTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    // ---------------------------------------------------------------------------------------------
    // Group A — Write / lifecycle. Read back via the RAW driver (no Include query) so these are
    // version-agnostic and run on EF8/EF9/EF10.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Write_insert_principal_and_dependent_stores_fk_with_element_name()
    {
        var (ordersName, customersName) = NewCollectionNames();

        ObjectId customerId;
        ObjectId orderId;
        using (var db = new OrderCustomerDbContext(database, ordersName, customersName))
        {
            var customer = new Customer { FullName = "Alice" };
            var order = new Order { OrderDescription = "Order 1", Customer = customer };
            db.Customers.Add(customer);
            db.Orders.Add(order);
            db.SaveChanges();
            customerId = customer._id;
            orderId = order._id;
        }

        // FK is stored on the dependent under its configured element name "cust_id".
        var rawOrder = database.MongoDatabase.GetCollection<BsonDocument>(ordersName)
            .Find(Builders<BsonDocument>.Filter.Eq("_id", orderId)).Single();
        Assert.Equal(customerId, rawOrder["cust_id"].AsObjectId);
        Assert.Equal("Order 1", rawOrder["desc"].AsString);

        // Round-trips through a fresh context (no Include needed to read the FK back).
        using (var db = new OrderCustomerDbContext(database, ordersName, customersName))
        {
            var order = db.Orders.Single(o => o._id == orderId);
            Assert.Equal(customerId, order.CustomerId);
        }
    }

    [Fact]
    public void Write_reassign_reference_updates_stored_fk()
    {
        var (ordersName, customersName) = NewCollectionNames();

        ObjectId aliceId, bobId, orderId;
        using (var db = new OrderCustomerDbContext(database, ordersName, customersName))
        {
            var alice = new Customer { FullName = "Alice" };
            var bob = new Customer { FullName = "Bob" };
            var order = new Order { OrderDescription = "Order 1", Customer = alice };
            db.Customers.AddRange(alice, bob);
            db.Orders.Add(order);
            db.SaveChanges();
            aliceId = alice._id;
            bobId = bob._id;
            orderId = order._id;
        }

        using (var db = new OrderCustomerDbContext(database, ordersName, customersName))
        {
            var order = db.Orders.Single(o => o._id == orderId);
            var bob = db.Customers.Single(c => c._id == bobId);
            order.Customer = bob;
            db.SaveChanges();
        }

        var rawOrder = database.MongoDatabase.GetCollection<BsonDocument>(ordersName)
            .Find(Builders<BsonDocument>.Filter.Eq("_id", orderId)).Single();
        Assert.Equal(bobId, rawOrder["cust_id"].AsObjectId);
        Assert.NotEqual(aliceId, rawOrder["cust_id"].AsObjectId);
    }

    [Fact]
    public void Write_cascade_delete_required_relationship_deletes_dependents()
    {
        var (ordersName, customersName) = NewCollectionNames();

        ObjectId customerId;
        using (var db = new RequiredOrderCustomerDbContext(database, ordersName, customersName))
        {
            var customer = new RequiredCustomer { FullName = "Alice" };
            db.Customers.Add(customer);
            db.Orders.AddRange(
                new RequiredOrder { OrderDescription = "Order 1", Customer = customer },
                new RequiredOrder { OrderDescription = "Order 2", Customer = customer });
            db.SaveChanges();
            customerId = customer._id;
        }

        using (var db = new RequiredOrderCustomerDbContext(database, ordersName, customersName))
        {
            // Load the principal and its dependents, then delete the principal -> cascade.
            var customer = db.Customers.Single(c => c._id == customerId);
            db.Orders.Where(o => o.CustomerId == customerId).Load();
            db.Customers.Remove(customer);
            db.SaveChanges();
        }

        Assert.Equal(0, database.MongoDatabase.GetCollection<BsonDocument>(ordersName).CountDocuments(FilterDefinition<BsonDocument>.Empty));
        Assert.Equal(0, database.MongoDatabase.GetCollection<BsonDocument>(customersName).CountDocuments(FilterDefinition<BsonDocument>.Empty));
    }

    [Fact]
    public void Write_client_set_null_optional_relationship_nulls_dependent_fk()
    {
        var (ordersName, customersName) = NewCollectionNames();

        ObjectId customerId, orderId;
        using (var db = new OrderCustomerDbContext(database, ordersName, customersName))
        {
            var customer = new Customer { FullName = "Alice" };
            var order = new Order { OrderDescription = "Order 1", Customer = customer };
            db.Customers.Add(customer);
            db.Orders.Add(order);
            db.SaveChanges();
            customerId = customer._id;
            orderId = order._id;
        }

        using (var db = new OrderCustomerDbContext(database, ordersName, customersName))
        {
            var customer = db.Customers.Single(c => c._id == customerId);
            db.Orders.Where(o => o.CustomerId == customerId).Load();
            db.Customers.Remove(customer);
            db.SaveChanges();
        }

        // ClientSetNull (optional, nullable FK): the dependent survives with a null FK.
        var rawOrder = database.MongoDatabase.GetCollection<BsonDocument>(ordersName)
            .Find(Builders<BsonDocument>.Filter.Eq("_id", orderId)).Single();
        Assert.True(!rawOrder.Contains("cust_id") || rawOrder["cust_id"].IsBsonNull);
        Assert.Equal(0, database.MongoDatabase.GetCollection<BsonDocument>(customersName).CountDocuments(FilterDefinition<BsonDocument>.Empty));
    }

    [Fact]
    public void Write_multi_collection_atomicity_rolls_back_principal_on_dependent_failure()
    {
        var (ordersName, customersName) = NewCollectionNames();

        // Pre-seed an order with a fixed _id so a second insert of the same _id fails (duplicate key)
        // mid-SaveChanges, which under the default AutoTransactionBehavior must roll back the
        // principal customer write too. Requires a replica set (transactions).
        var fixedOrderId = ObjectId.GenerateNewId();
        database.MongoDatabase.GetCollection<BsonDocument>(ordersName)
            .InsertOne(new BsonDocument { { "_id", fixedOrderId }, { "desc", "pre-existing" } });

        using (var db = new OrderCustomerDbContext(database, ordersName, customersName))
        {
            var customer = new Customer { FullName = "Alice" };
            var order = new Order { _id = fixedOrderId, OrderDescription = "dup", Customer = customer };
            db.Customers.Add(customer);
            db.Orders.Add(order);

            Assert.ThrowsAny<Exception>(() => db.SaveChanges());
        }

        // The principal customer write must have rolled back: no customer persisted.
        Assert.Equal(0, database.MongoDatabase.GetCollection<BsonDocument>(customersName).CountDocuments(FilterDefinition<BsonDocument>.Empty));
        // Only the original pre-existing order remains.
        Assert.Equal(1, database.MongoDatabase.GetCollection<BsonDocument>(ordersName).CountDocuments(FilterDefinition<BsonDocument>.Empty));
    }

#if !EF8 && !EF9
    // ---------------------------------------------------------------------------------------------
    // Group B — Serialization through Include. EF10-only (these execute cross-collection Includes,
    // which only translate on EF10; EF8/EF9 fail to translate — see EF-X020).
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Include_bson_representation_on_included_entity_round_trips()
    {
        var ordersName = TemporaryDatabaseFixtureBase.CreateCollectionName("BsonRepOrders") + Guid.NewGuid().ToString("N")[..8];
        var customersName = TemporaryDatabaseFixtureBase.CreateCollectionName("BsonRepCustomers") + Guid.NewGuid().ToString("N")[..8];

        var customerId = ObjectId.GenerateNewId();
        var publicGuid = Guid.NewGuid();
        // Customer.PublicId is a Guid stored as a string (HasBsonRepresentation(String)).
        database.MongoDatabase.GetCollection<BsonDocument>(customersName).InsertOne(
            new BsonDocument { { "_id", customerId }, { "name", "Alice" }, { "pub", publicGuid.ToString() } });
        database.MongoDatabase.GetCollection<BsonDocument>(ordersName).InsertOne(
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "desc", "Order 1" }, { "cust_id", customerId } });

        using var db = new BsonRepDbContext(database, ordersName, customersName);
        var order = db.Orders.Include(o => o.Customer).First();

        Assert.NotNull(order.Customer);
        Assert.Equal(publicGuid, order.Customer.PublicId);
    }

    [Fact]
    public void Include_value_converter_on_included_entity_round_trips()
    {
        var ordersName = TemporaryDatabaseFixtureBase.CreateCollectionName("ConvOrders") + Guid.NewGuid().ToString("N")[..8];
        var customersName = TemporaryDatabaseFixtureBase.CreateCollectionName("ConvCustomers") + Guid.NewGuid().ToString("N")[..8];

        var customerId = ObjectId.GenerateNewId();
        // Customer.Tier is an enum persisted via a value converter as its string name.
        database.MongoDatabase.GetCollection<BsonDocument>(customersName).InsertOne(
            new BsonDocument { { "_id", customerId }, { "name", "Alice" }, { "tier", "Gold" } });
        database.MongoDatabase.GetCollection<BsonDocument>(ordersName).InsertOne(
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "desc", "Order 1" }, { "cust_id", customerId } });

        using var db = new ConverterDbContext(database, ordersName, customersName);
        var order = db.Orders.Include(o => o.Customer).First();

        Assert.NotNull(order.Customer);
        Assert.Equal(CustomerTier.Gold, order.Customer.Tier);
    }

    [Fact]
    public void Include_owned_type_nested_under_included_entity_materializes()
    {
        var ordersName = TemporaryDatabaseFixtureBase.CreateCollectionName("OwnedOrders") + Guid.NewGuid().ToString("N")[..8];
        var customersName = TemporaryDatabaseFixtureBase.CreateCollectionName("OwnedCustomers") + Guid.NewGuid().ToString("N")[..8];

        var customerId = ObjectId.GenerateNewId();
        database.MongoDatabase.GetCollection<BsonDocument>(customersName).InsertOne(
            new BsonDocument
            {
                { "_id", customerId },
                { "name", "Alice" },
                { "Address", new BsonDocument { { "City", "London" }, { "Zip", "EC1" } } }
            });
        database.MongoDatabase.GetCollection<BsonDocument>(ordersName).InsertOne(
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "desc", "Order 1" }, { "cust_id", customerId } });

        using var db = new OwnedDbContext(database, ordersName, customersName);
        var order = db.Orders.Include(o => o.Customer).First();

        Assert.NotNull(order.Customer);
        Assert.NotNull(order.Customer.Address);
        Assert.Equal("London", order.Customer.Address.City);
        Assert.Equal("EC1", order.Customer.Address.Zip);
    }

    [Fact]
    public void Include_tph_hierarchy_on_included_entity_resolves_derived_type()
    {
        var ordersName = TemporaryDatabaseFixtureBase.CreateCollectionName("TphOrders") + Guid.NewGuid().ToString("N")[..8];
        var customersName = TemporaryDatabaseFixtureBase.CreateCollectionName("TphCustomers") + Guid.NewGuid().ToString("N")[..8];

        var customerId = ObjectId.GenerateNewId();
        database.MongoDatabase.GetCollection<BsonDocument>(customersName).InsertOne(
            new BsonDocument
            {
                { "_id", customerId },
                { "name", "Alice" },
                { "_t", "VipCustomer" },
                { "discount", 42 }
            });
        database.MongoDatabase.GetCollection<BsonDocument>(ordersName).InsertOne(
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "desc", "Order 1" }, { "cust_id", customerId } });

        using var db = new TphDbContext(database, ordersName, customersName);
        var order = db.Orders.Include(o => o.Customer).First();

        Assert.NotNull(order.Customer);
        var vip = Assert.IsType<VipCustomer>(order.Customer);
        Assert.Equal(42, vip.Discount);
    }

    [Fact]
    public void Include_tph_derived_target_collection_nav_excludes_sibling_type_rows()
    {
        var ordersName = TemporaryDatabaseFixtureBase.CreateCollectionName("PriorityOrders") + Guid.NewGuid().ToString("N")[..8];
        var customersName = TemporaryDatabaseFixtureBase.CreateCollectionName("PriorityCustomers") + Guid.NewGuid().ToString("N")[..8];

        var customerId = ObjectId.GenerateNewId();
        database.MongoDatabase.GetCollection<BsonDocument>(customersName).InsertOne(
            new BsonDocument { { "_id", customerId }, { "name", "Alice" } });
        database.MongoDatabase.GetCollection<BsonDocument>(ordersName).InsertMany([
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "desc", "Regular" }, { "cust_id", customerId }, { "_t", "Order" } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "desc", "Rush" }, { "cust_id", customerId }, { "_t", "Priority" }, { "priority", 5 } }
        ]);

        using var db = new DerivedCollectionNavDbContext(database, ordersName, customersName);
        var customer = db.Customers.Include(c => c.PriorityOrders).First();

        // The FK (cust_id) matches both sibling-type documents; only the discriminator narrows the
        // $lookup to the derived PriorityOrder rows the navigation actually targets. See EF-374.
        Assert.Single(customer.PriorityOrders);
        Assert.All(customer.PriorityOrders, o => Assert.IsType<PriorityOrder>(o));
    }

    [Theory]
    [InlineData("string")]
    [InlineData("int")]
    [InlineData("guid")]
    public void Include_non_objectid_join_keys_match_both_directions(string keyType)
    {
        var ordersName = TemporaryDatabaseFixtureBase.CreateCollectionName("KeyOrders") + keyType + Guid.NewGuid().ToString("N")[..8];
        var customersName = TemporaryDatabaseFixtureBase.CreateCollectionName("KeyCustomers") + keyType + Guid.NewGuid().ToString("N")[..8];

        BsonValue key = keyType switch
        {
            "string" => "CUST-1",
            "int" => 7,
            "guid" => new BsonBinaryData(Guid.NewGuid(), GuidRepresentation.Standard),
            _ => throw new ArgumentOutOfRangeException(nameof(keyType))
        };

        database.MongoDatabase.GetCollection<BsonDocument>(customersName).InsertOne(
            new BsonDocument { { "_id", key }, { "name", "Alice" } });
        database.MongoDatabase.GetCollection<BsonDocument>(ordersName).InsertMany([
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "desc", "Order 1" }, { "cust_id", key } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "desc", "Order 2" }, { "cust_id", key } }
        ]);

        switch (keyType)
        {
            case "string":
                RunNonObjectIdKeyAssertions<string>(ordersName, customersName);
                break;
            case "int":
                RunNonObjectIdKeyAssertions<int>(ordersName, customersName);
                break;
            case "guid":
                RunNonObjectIdKeyAssertions<Guid>(ordersName, customersName);
                break;
        }
    }

    private void RunNonObjectIdKeyAssertions<TKey>(string ordersName, string customersName)
    {
        using var db = new TypedKeyDbContext<TKey>(database, ordersName, customersName);

        // Reference direction: order -> customer.
        var order = db.Orders.Include(o => o.Customer).First();
        Assert.NotNull(order.Customer);
        Assert.Equal("Alice", order.Customer.FullName);

        // Collection direction: customer -> orders.
        var customer = db.Customers.Include(c => c.Orders).First();
        Assert.Equal(2, customer.Orders.Count);
    }

    // ---------------------------------------------------------------------------------------------
    // Group C — Change tracking through Include. EF10-only.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Include_identity_resolution_tracking_shares_principal_and_fixes_up_inverse()
    {
        var (ordersName, customersName) = SetupThreeOrdersTwoCustomers();

        using var db = new OrderCustomerDbContext(database, ordersName, customersName);
        var aliceOrders = db.Orders.Include(o => o.Customer)
            .Where(o => o.Customer.FullName == "Alice").ToList();

        Assert.Equal(2, aliceOrders.Count);
        // Both of Alice's orders share the SAME Customer instance.
        Assert.Same(aliceOrders[0].Customer, aliceOrders[1].Customer);
        // Inverse navigation is fixed up to contain both orders.
        Assert.Equal(2, aliceOrders[0].Customer.Orders.Count);
    }

    [Fact]
    public void Include_no_tracking_with_identity_resolution_vs_plain_no_tracking()
    {
        var (ordersName, customersName) = SetupThreeOrdersTwoCustomers();

        using (var db = new OrderCustomerDbContext(database, ordersName, customersName))
        {
            var withId = db.Orders.AsNoTrackingWithIdentityResolution()
                .Include(o => o.Customer)
                .Where(o => o.Customer.FullName == "Alice").ToList();
            Assert.Equal(2, withId.Count);
            Assert.Same(withId[0].Customer, withId[1].Customer);
        }

        using (var db = new OrderCustomerDbContext(database, ordersName, customersName))
        {
            var plain = db.Orders.AsNoTracking()
                .Include(o => o.Customer)
                .Where(o => o.Customer.FullName == "Alice").ToList();
            Assert.Equal(2, plain.Count);
            Assert.NotSame(plain[0].Customer, plain[1].Customer);
        }
    }

    [Fact]
    public void Include_pagination_orderby_skip_take_with_collection_include()
    {
        var (ordersName, customersName) = SetupManyCustomers(5);

        using var db = new OrderCustomerDbContext(database, ordersName, customersName);
        var page = db.Customers
            .Include(c => c.Orders)
            .OrderBy(c => c.FullName)
            .Skip(1)
            .Take(2)
            .ToList();

        Assert.Equal(2, page.Count);
        Assert.Equal(["Customer 1", "Customer 2"], page.Select(c => c.FullName).ToArray());
        // Each customer has exactly its own two orders (no cross-contamination).
        Assert.All(page, c => Assert.Equal(2, c.Orders.Count));
    }

    [Fact]
    public void Include_multi_level_then_include_does_not_duplicate()
    {
        // Real three-collection chain: Customer -(collection)-> Order -(collection)-> OrderItem.
        // A multi-level ThenInclude across two separate $lookup joins must not cartesian-multiply
        // the intermediate collection (Alice keeps exactly 2 orders, each with its own items).
        var customersName = TemporaryDatabaseFixtureBase.CreateCollectionName("ChainCustomers") + Guid.NewGuid().ToString("N")[..8];
        var ordersName = TemporaryDatabaseFixtureBase.CreateCollectionName("ChainOrders") + Guid.NewGuid().ToString("N")[..8];
        var itemsName = TemporaryDatabaseFixtureBase.CreateCollectionName("ChainItems") + Guid.NewGuid().ToString("N")[..8];

        var aliceId = ObjectId.GenerateNewId();
        var order1Id = ObjectId.GenerateNewId();
        var order2Id = ObjectId.GenerateNewId();

        database.MongoDatabase.GetCollection<BsonDocument>(customersName).InsertOne(
            new BsonDocument { { "_id", aliceId }, { "name", "Alice" } });
        database.MongoDatabase.GetCollection<BsonDocument>(ordersName).InsertMany([
            new BsonDocument { { "_id", order1Id }, { "desc", "Order 1" }, { "cust_id", aliceId } },
            new BsonDocument { { "_id", order2Id }, { "desc", "Order 2" }, { "cust_id", aliceId } }
        ]);
        database.MongoDatabase.GetCollection<BsonDocument>(itemsName).InsertMany([
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "sku", "A" }, { "ord_id", order1Id } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "sku", "B" }, { "ord_id", order1Id } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "sku", "C" }, { "ord_id", order2Id } }
        ]);

        using var db = new ChainDbContext(database, customersName, ordersName, itemsName);
        var customers = db.Customers
            .Include(c => c.Orders)
                .ThenInclude(o => o.Items)
            .ToList();

        var alice = Assert.Single(customers);
        // Alice has exactly 2 orders (no cartesian blow-up from the deeper ThenInclude).
        Assert.Equal(2, alice.Orders.Count);
        var order1 = alice.Orders.Single(o => o.OrderDescription == "Order 1");
        var order2 = alice.Orders.Single(o => o.OrderDescription == "Order 2");
        Assert.Equal(2, order1.Items.Count);
        Assert.Single(order2.Items);
        Assert.Equal(["A", "B"], order1.Items.Select(i => i.Sku).OrderBy(s => s).ToArray());
    }

    [Fact]
    public void Include_then_include_tph_derived_target_excludes_sibling_type_rows()
    {
        // Same sibling-FK-collision shape as Include_tph_derived_target_collection_nav_excludes_sibling_type_rows,
        // but one level deeper (ThenInclude): the nested $lookup construction sites must not silently drop
        // the discriminator-narrowing pipeline stage when flattening a nested LookupExpression. See EF-374.
        var customersName = TemporaryDatabaseFixtureBase.CreateCollectionName("NestedTphCustomers") + Guid.NewGuid().ToString("N")[..8];
        var ordersName = TemporaryDatabaseFixtureBase.CreateCollectionName("NestedTphOrders") + Guid.NewGuid().ToString("N")[..8];
        var itemsName = TemporaryDatabaseFixtureBase.CreateCollectionName("NestedTphItems") + Guid.NewGuid().ToString("N")[..8];

        var aliceId = ObjectId.GenerateNewId();
        var orderId = ObjectId.GenerateNewId();

        database.MongoDatabase.GetCollection<BsonDocument>(customersName).InsertOne(
            new BsonDocument { { "_id", aliceId }, { "name", "Alice" } });
        database.MongoDatabase.GetCollection<BsonDocument>(ordersName).InsertOne(
            new BsonDocument { { "_id", orderId }, { "desc", "Order 1" }, { "cust_id", aliceId } });
        database.MongoDatabase.GetCollection<BsonDocument>(itemsName).InsertMany([
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "sku", "Regular" }, { "ord_id", orderId }, { "_t", "Item" } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "sku", "Rush" }, { "ord_id", orderId }, { "_t", "Priority" }, { "priority", 5 } }
        ]);

        using var db = new NestedTphDbContext(database, customersName, ordersName, itemsName);
        var customer = db.Customers.Include(c => c.Orders).ThenInclude(o => o.PriorityItems).First();

        var order = Assert.Single(customer.Orders);
        Assert.Single(order.PriorityItems);
        Assert.All(order.PriorityItems, i => Assert.IsType<PriorityChainItem>(i));
    }

    // EF-373. An operator composed BETWEEN two cross-collection joins is SUPPORTED: the driver-LINQ
    // bridge's StripInterleavedJoinChain splits the join-replacing $lookup stages along the join order and
    // emits each at its own reattachment boundary, so the interleaved operator lands between the two
    // $lookup stages rather than above both of them. Before that, the operator was hoisted above both and
    // the paging ran before the second join had filtered anything - a silently wrong page.
    //
    // Each of these asserts the ROW IDENTITIES, never just the count: hoisting the operator returns the
    // right NUMBER of rows from the wrong window, which a count assertion cannot distinguish. Seed is
    // L1(O1), L2(O2), L3(O1), L4(O1), every line pointing at the one product.
    [Fact]
    public void Take_between_two_joins_returns_the_correct_page()
    {
        var (linesName, ordersName, productsName) = SetupLinesOrdersProducts();

        using var db = new TwoJoinDbContext(database, linesName, ordersName, productsName);

        var lines = db.Lines
            .OrderBy(l => l.LineName)
            .Where(l => l.Order.OrderName != "O2")
            .Take(3)
            .Include(l => l.Product)
            .ToList();

        // Order by name gives L1..L4; the Order join drops L2; Take(3) then takes all three survivors.
        // Were the $lookup/$unwind for the Product join hoisted above the Take, the Take would have seen
        // L1,L2,L3 and returned L1,L3 instead.
        Assert.Equal(["L1", "L3", "L4"], lines.Select(l => l.LineName).ToArray());
        Assert.All(lines, l => Assert.Equal("Widget", l.Product.ProductName));
    }

    [Fact]
    public void Skip_between_two_joins_returns_the_correct_page()
    {
        var (linesName, ordersName, productsName) = SetupLinesOrdersProducts();

        using var db = new TwoJoinDbContext(database, linesName, ordersName, productsName);

        var lines = db.Lines
            .OrderBy(l => l.LineName)
            .Where(l => l.Order.OrderName != "O2")
            .Skip(1)
            .Include(l => l.Product)
            .ToList();

        // Survivors of the Order join, in name order, are L1,L3,L4; Skip(1) drops L1. Hoisting the Product
        // join's $unwind above the Skip would have skipped L1 out of L1,L2,L3,L4 and yielded L3,L4 by luck
        // here, so this test is paired with the Take one above, which does discriminate.
        Assert.Equal(["L3", "L4"], lines.Select(l => l.LineName).ToArray());
        Assert.All(lines, l => Assert.Equal("Widget", l.Product.ProductName));
    }

    [Fact]
    public void Distinct_between_two_joins_returns_the_correct_rows()
    {
        var (linesName, ordersName, productsName) = SetupLinesOrdersProducts();

        using var db = new TwoJoinDbContext(database, linesName, ordersName, productsName);

        var lines = db.Lines
            .Where(l => l.Order.OrderName != "O2")
            .Distinct()
            .Include(l => l.Product)
            .ToList();

        // Distinct is applied to the rows the Order join left (L1,L3,L4 - all distinct entities), so it
        // removes nothing; the point is that the Product join does not run before it. Order is not
        // guaranteed after a $group-based Distinct, hence the sort here rather than an ordered assertion.
        Assert.Equal(["L1", "L3", "L4"], lines.Select(l => l.LineName).OrderBy(n => n).ToArray());
        Assert.All(lines, l => Assert.Equal("Widget", l.Product.ProductName));
    }

    // EF-373 (review follow-up): InnerCollections is keyed by IEntityType, so two navigations that join
    // to the SAME target entity type (e.g. a self-join) collapse to one entry there - the split must not
    // rely on that count, or this shape would be mispositioned even though the two-different-types one is
    // handled. Both navigations here target Order.
    [Fact]
    public void Take_between_two_joins_to_same_target_entity_type_returns_the_correct_page()
    {
        var (linesName, ordersName) = SetupSameTypeJoinLinesAndOrders();

        using var db = new SameTypeJoinDbContext(database, linesName, ordersName);

        var lines = db.Lines
            .OrderBy(l => l.LineName)
            .Where(l => l.PrimaryOrder.OrderName != "O2")
            .Take(3)
            .Include(l => l.SecondaryOrder)
            .ToList();

        // L2's PrimaryOrder is O2, so the first join drops it; Take(3) then takes all three survivors.
        Assert.Equal(["L1", "L3", "L4"], lines.Select(l => l.LineName).ToArray());
        Assert.All(lines, l => Assert.Equal("O1", l.SecondaryOrder.OrderName));
    }

    [Fact]
    public void Include_sibling_then_includes_through_same_target_type_do_not_collapse()
    {
        // EF-376 repro: two sibling reference navigations (PrimaryMid, SecondaryMid) targeting the SAME
        // entity type, each further ThenInclude'd through the SAME navigation name (Leaf). Both branches
        // must produce their own $lookup rather than colliding on a shared "_lookup_Leaf" alias.
        var rootsName = TemporaryDatabaseFixtureBase.CreateCollectionName("SibRoots") + Guid.NewGuid().ToString("N")[..8];
        var midsName = TemporaryDatabaseFixtureBase.CreateCollectionName("SibMids") + Guid.NewGuid().ToString("N")[..8];
        var leavesName = TemporaryDatabaseFixtureBase.CreateCollectionName("SibLeaves") + Guid.NewGuid().ToString("N")[..8];

        var primaryLeafId = ObjectId.GenerateNewId();
        var secondaryLeafId = ObjectId.GenerateNewId();
        var primaryMidId = ObjectId.GenerateNewId();
        var secondaryMidId = ObjectId.GenerateNewId();
        var rootId = ObjectId.GenerateNewId();

        database.MongoDatabase.GetCollection<BsonDocument>(leavesName).InsertMany([
            new BsonDocument { { "_id", primaryLeafId }, { "value", "PrimaryLeaf" } },
            new BsonDocument { { "_id", secondaryLeafId }, { "value", "SecondaryLeaf" } }
        ]);
        database.MongoDatabase.GetCollection<BsonDocument>(midsName).InsertMany([
            new BsonDocument { { "_id", primaryMidId }, { "leaf_id", primaryLeafId } },
            new BsonDocument { { "_id", secondaryMidId }, { "leaf_id", secondaryLeafId } }
        ]);
        database.MongoDatabase.GetCollection<BsonDocument>(rootsName).InsertOne(
            new BsonDocument { { "_id", rootId }, { "pmid_id", primaryMidId }, { "smid_id", secondaryMidId } });

        using var db = new SiblingDbContext(database, rootsName, midsName, leavesName);
        var root = db.Roots
            .Include(r => r.PrimaryMid).ThenInclude(m => m.Leaf)
            .Include(r => r.SecondaryMid).ThenInclude(m => m.Leaf)
            .Single();

        Assert.NotNull(root.PrimaryMid);
        Assert.NotNull(root.PrimaryMid.Leaf);
        Assert.NotNull(root.SecondaryMid);
        Assert.NotNull(root.SecondaryMid.Leaf);
        Assert.Equal("PrimaryLeaf", root.PrimaryMid.Leaf.Value);
        Assert.Equal("SecondaryLeaf", root.SecondaryMid.Leaf.Value);
    }
#endif

    // ---------------------------------------------------------------------------------------------
    // Helpers / seeding (raw-BSON, mirroring CrossCollectionIncludeTests conventions).
    // BSON uses: desc, cust_id for Orders; name for Customers.
    // ---------------------------------------------------------------------------------------------

    private static (string ordersName, string customersName) NewCollectionNames()
    {
        var customersName = TemporaryDatabaseFixtureBase.CreateCollectionName("RelCustomers") + Guid.NewGuid().ToString("N")[..8];
        var ordersName = TemporaryDatabaseFixtureBase.CreateCollectionName("RelOrders") + Guid.NewGuid().ToString("N")[..8];
        return (ordersName, customersName);
    }

    private (string linesName, string ordersName, string productsName) SetupLinesOrdersProducts()
    {
        var linesName = TemporaryDatabaseFixtureBase.CreateCollectionName("TwoJoinLines") + Guid.NewGuid().ToString("N")[..8];
        var ordersName = TemporaryDatabaseFixtureBase.CreateCollectionName("TwoJoinOrders") + Guid.NewGuid().ToString("N")[..8];
        var productsName = TemporaryDatabaseFixtureBase.CreateCollectionName("TwoJoinProducts") + Guid.NewGuid().ToString("N")[..8];

        var order1Id = ObjectId.GenerateNewId();
        var order2Id = ObjectId.GenerateNewId();
        var productId = ObjectId.GenerateNewId();

        database.MongoDatabase.GetCollection<BsonDocument>(ordersName).InsertMany([
            new BsonDocument { { "_id", order1Id }, { "name", "O1" } },
            new BsonDocument { { "_id", order2Id }, { "name", "O2" } }
        ]);
        database.MongoDatabase.GetCollection<BsonDocument>(productsName).InsertOne(
            new BsonDocument { { "_id", productId }, { "name", "Widget" } });
        database.MongoDatabase.GetCollection<BsonDocument>(linesName).InsertMany([
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "name", "L1" }, { "ord_id", order1Id }, { "prod_id", productId } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "name", "L2" }, { "ord_id", order2Id }, { "prod_id", productId } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "name", "L3" }, { "ord_id", order1Id }, { "prod_id", productId } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "name", "L4" }, { "ord_id", order1Id }, { "prod_id", productId } }
        ]);

        return (linesName, ordersName, productsName);
    }

    private (string linesName, string ordersName) SetupSameTypeJoinLinesAndOrders()
    {
        var linesName = TemporaryDatabaseFixtureBase.CreateCollectionName("SameTypeJoinLines") + Guid.NewGuid().ToString("N")[..8];
        var ordersName = TemporaryDatabaseFixtureBase.CreateCollectionName("SameTypeJoinOrders") + Guid.NewGuid().ToString("N")[..8];

        var order1Id = ObjectId.GenerateNewId();
        var order2Id = ObjectId.GenerateNewId();

        database.MongoDatabase.GetCollection<BsonDocument>(ordersName).InsertMany([
            new BsonDocument { { "_id", order1Id }, { "name", "O1" } },
            new BsonDocument { { "_id", order2Id }, { "name", "O2" } }
        ]);
        database.MongoDatabase.GetCollection<BsonDocument>(linesName).InsertMany([
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "name", "L1" }, { "primary_ord_id", order1Id }, { "secondary_ord_id", order1Id } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "name", "L2" }, { "primary_ord_id", order2Id }, { "secondary_ord_id", order1Id } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "name", "L3" }, { "primary_ord_id", order1Id }, { "secondary_ord_id", order1Id } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "name", "L4" }, { "primary_ord_id", order1Id }, { "secondary_ord_id", order1Id } }
        ]);

        return (linesName, ordersName);
    }

    private (string ordersName, string customersName) SetupThreeOrdersTwoCustomers()
    {
        var (ordersName, customersName) = NewCollectionNames();

        var customerId1 = ObjectId.GenerateNewId();
        var customerId2 = ObjectId.GenerateNewId();

        database.MongoDatabase.GetCollection<BsonDocument>(customersName).InsertMany([
            new BsonDocument { { "_id", customerId1 }, { "name", "Alice" } },
            new BsonDocument { { "_id", customerId2 }, { "name", "Bob" } }
        ]);
        database.MongoDatabase.GetCollection<BsonDocument>(ordersName).InsertMany([
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "desc", "Order 1" }, { "cust_id", customerId1 } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "desc", "Order 2" }, { "cust_id", customerId1 } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "desc", "Order 3" }, { "cust_id", customerId2 } }
        ]);

        return (ordersName, customersName);
    }

    private (string ordersName, string customersName) SetupManyCustomers(int count)
    {
        var (ordersName, customersName) = NewCollectionNames();

        var customers = new List<BsonDocument>();
        var orders = new List<BsonDocument>();
        for (var i = 0; i < count; i++)
        {
            var cid = ObjectId.GenerateNewId();
            customers.Add(new BsonDocument { { "_id", cid }, { "name", $"Customer {i}" } });
            orders.Add(new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "desc", $"O{i}-a" }, { "cust_id", cid } });
            orders.Add(new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "desc", $"O{i}-b" }, { "cust_id", cid } });
        }

        database.MongoDatabase.GetCollection<BsonDocument>(customersName).InsertMany(customers);
        database.MongoDatabase.GetCollection<BsonDocument>(ordersName).InsertMany(orders);
        return (ordersName, customersName);
    }

    // ---------------------------------------------------------------------------------------------
    // Models + contexts.
    // ---------------------------------------------------------------------------------------------

    class Order
    {
        public ObjectId _id { get; set; }
        public string OrderDescription { get; set; }
        public ObjectId? CustomerId { get; set; }
        public Customer Customer { get; set; }
    }

    class Customer
    {
        public ObjectId _id { get; set; }
        public string FullName { get; set; }
        public List<Order> Orders { get; set; }
    }

    abstract class CrossCollectionDbContextBase<TSelf> : DbContext
        where TSelf : DbContext
    {
        protected CrossCollectionDbContextBase(TemporaryDatabaseFixture database)
            : base(new DbContextOptionsBuilder<TSelf>()
                .UseMongoDB(database.Client, database.MongoDatabase.DatabaseNamespace.DatabaseName)
                .ReplaceService<IModelCacheKeyFactory, IgnoreCacheKeyFactory>()
                .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
                .Options)
        {
        }

        protected sealed class IgnoreCacheKeyFactory : IModelCacheKeyFactory
        {
            private static int _count;
            public object Create(DbContext context, bool designTime)
                => Interlocked.Increment(ref _count);
        }
    }

    class OrderCustomerDbContext(TemporaryDatabaseFixture database, string ordersCollection, string customersCollection)
        : CrossCollectionDbContextBase<OrderCustomerDbContext>(database)
    {
        public DbSet<Order> Orders { get; set; }
        public DbSet<Customer> Customers { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<Customer>(b =>
            {
                b.ToCollection(customersCollection);
                b.Property(c => c.FullName).HasElementName("name");
                b.HasMany(c => c.Orders).WithOne(o => o.Customer).HasForeignKey(o => o.CustomerId);
            });

            modelBuilder.Entity<Order>(b =>
            {
                b.ToCollection(ordersCollection);
                b.Property(o => o.OrderDescription).HasElementName("desc");
                b.Property(o => o.CustomerId).HasElementName("cust_id");
            });
        }
    }

    // Required (non-nullable FK) relationship -> DeleteBehavior.Cascade by default.
    class RequiredOrder
    {
        public ObjectId _id { get; set; }
        public string OrderDescription { get; set; }
        public ObjectId CustomerId { get; set; }
        public RequiredCustomer Customer { get; set; }
    }

    class RequiredCustomer
    {
        public ObjectId _id { get; set; }
        public string FullName { get; set; }
        public List<RequiredOrder> Orders { get; set; }
    }

    class RequiredOrderCustomerDbContext(TemporaryDatabaseFixture database, string ordersCollection, string customersCollection)
        : CrossCollectionDbContextBase<RequiredOrderCustomerDbContext>(database)
    {
        public DbSet<RequiredOrder> Orders { get; set; }
        public DbSet<RequiredCustomer> Customers { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<RequiredCustomer>(b =>
            {
                b.ToCollection(customersCollection);
                b.Property(c => c.FullName).HasElementName("name");
                b.HasMany(c => c.Orders).WithOne(o => o.Customer)
                    .HasForeignKey(o => o.CustomerId)
                    .OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<RequiredOrder>(b =>
            {
                b.ToCollection(ordersCollection);
                b.Property(o => o.OrderDescription).HasElementName("desc");
                b.Property(o => o.CustomerId).HasElementName("cust_id");
            });
        }
    }

#if !EF8 && !EF9
    enum CustomerTier
    {
        None,
        Silver,
        Gold
    }

    class BsonRepCustomer
    {
        public ObjectId _id { get; set; }
        public string FullName { get; set; }
        public Guid PublicId { get; set; }
        public List<BsonRepOrder> Orders { get; set; }
    }

    class BsonRepOrder
    {
        public ObjectId _id { get; set; }
        public string OrderDescription { get; set; }
        public ObjectId? CustomerId { get; set; }
        public BsonRepCustomer Customer { get; set; }
    }

    class BsonRepDbContext(TemporaryDatabaseFixture database, string ordersCollection, string customersCollection)
        : CrossCollectionDbContextBase<BsonRepDbContext>(database)
    {
        public DbSet<BsonRepOrder> Orders { get; set; }
        public DbSet<BsonRepCustomer> Customers { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<BsonRepCustomer>(b =>
            {
                b.ToCollection(customersCollection);
                b.Property(c => c.FullName).HasElementName("name");
                b.Property(c => c.PublicId).HasElementName("pub").HasBsonRepresentation(BsonType.String);
                b.HasMany(c => c.Orders).WithOne(o => o.Customer).HasForeignKey(o => o.CustomerId);
            });

            modelBuilder.Entity<BsonRepOrder>(b =>
            {
                b.ToCollection(ordersCollection);
                b.Property(o => o.OrderDescription).HasElementName("desc");
                b.Property(o => o.CustomerId).HasElementName("cust_id");
            });
        }
    }

    class ConverterCustomer
    {
        public ObjectId _id { get; set; }
        public string FullName { get; set; }
        public CustomerTier Tier { get; set; }
        public List<ConverterOrder> Orders { get; set; }
    }

    class ConverterOrder
    {
        public ObjectId _id { get; set; }
        public string OrderDescription { get; set; }
        public ObjectId? CustomerId { get; set; }
        public ConverterCustomer Customer { get; set; }
    }

    class ConverterDbContext(TemporaryDatabaseFixture database, string ordersCollection, string customersCollection)
        : CrossCollectionDbContextBase<ConverterDbContext>(database)
    {
        public DbSet<ConverterOrder> Orders { get; set; }
        public DbSet<ConverterCustomer> Customers { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<ConverterCustomer>(b =>
            {
                b.ToCollection(customersCollection);
                b.Property(c => c.FullName).HasElementName("name");
                b.Property(c => c.Tier).HasElementName("tier")
                    .HasConversion(new ValueConverter<CustomerTier, string>(
                        v => v.ToString(),
                        v => (CustomerTier)Enum.Parse(typeof(CustomerTier), v)));
                b.HasMany(c => c.Orders).WithOne(o => o.Customer).HasForeignKey(o => o.CustomerId);
            });

            modelBuilder.Entity<ConverterOrder>(b =>
            {
                b.ToCollection(ordersCollection);
                b.Property(o => o.OrderDescription).HasElementName("desc");
                b.Property(o => o.CustomerId).HasElementName("cust_id");
            });
        }
    }

    class OwnedCustomer
    {
        public ObjectId _id { get; set; }
        public string FullName { get; set; }
        public OwnedAddress Address { get; set; }
        public List<OwnedOrder> Orders { get; set; }
    }

    class OwnedAddress
    {
        public string City { get; set; }
        public string Zip { get; set; }
    }

    class OwnedOrder
    {
        public ObjectId _id { get; set; }
        public string OrderDescription { get; set; }
        public ObjectId? CustomerId { get; set; }
        public OwnedCustomer Customer { get; set; }
    }

    class OwnedDbContext(TemporaryDatabaseFixture database, string ordersCollection, string customersCollection)
        : CrossCollectionDbContextBase<OwnedDbContext>(database)
    {
        public DbSet<OwnedOrder> Orders { get; set; }
        public DbSet<OwnedCustomer> Customers { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<OwnedCustomer>(b =>
            {
                b.ToCollection(customersCollection);
                b.Property(c => c.FullName).HasElementName("name");
                b.OwnsOne(c => c.Address);
                b.HasMany(c => c.Orders).WithOne(o => o.Customer).HasForeignKey(o => o.CustomerId);
            });

            modelBuilder.Entity<OwnedOrder>(b =>
            {
                b.ToCollection(ordersCollection);
                b.Property(o => o.OrderDescription).HasElementName("desc");
                b.Property(o => o.CustomerId).HasElementName("cust_id");
            });
        }
    }

    class TphCustomer
    {
        public ObjectId _id { get; set; }
        public string FullName { get; set; }
        public List<TphOrder> Orders { get; set; }
    }

    class VipCustomer : TphCustomer
    {
        public int Discount { get; set; }
    }

    class TphOrder
    {
        public ObjectId _id { get; set; }
        public string OrderDescription { get; set; }
        public ObjectId? CustomerId { get; set; }
        public TphCustomer Customer { get; set; }
    }

    class TphDbContext(TemporaryDatabaseFixture database, string ordersCollection, string customersCollection)
        : CrossCollectionDbContextBase<TphDbContext>(database)
    {
        public DbSet<TphOrder> Orders { get; set; }
        public DbSet<TphCustomer> Customers { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<TphCustomer>(b =>
            {
                b.ToCollection(customersCollection);
                b.Property(c => c.FullName).HasElementName("name");
                b.HasDiscriminator<string>("_t")
                    .HasValue<TphCustomer>("Customer")
                    .HasValue<VipCustomer>("VipCustomer");
                b.HasMany(c => c.Orders).WithOne(o => o.Customer).HasForeignKey(o => o.CustomerId);
            });
            modelBuilder.Entity<VipCustomer>().Property(c => c.Discount).HasElementName("discount");

            modelBuilder.Entity<TphOrder>(b =>
            {
                b.ToCollection(ordersCollection);
                b.Property(o => o.OrderDescription).HasElementName("desc");
                b.Property(o => o.CustomerId).HasElementName("cust_id");
            });
        }
    }

    class PriorityOrder : TphOrder
    {
        public int PriorityLevel { get; set; }
    }

    class DerivedNavCustomer
    {
        public ObjectId _id { get; set; }
        public string FullName { get; set; }
        public List<PriorityOrder> PriorityOrders { get; set; }
    }

    class DerivedCollectionNavDbContext(TemporaryDatabaseFixture database, string ordersCollection, string customersCollection)
        : CrossCollectionDbContextBase<DerivedCollectionNavDbContext>(database)
    {
        public DbSet<TphOrder> Orders { get; set; }
        public DbSet<DerivedNavCustomer> Customers { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<DerivedNavCustomer>(b =>
            {
                b.ToCollection(customersCollection);
                b.Property(c => c.FullName).HasElementName("name");
                b.HasMany(c => c.PriorityOrders).WithOne().HasForeignKey("CustomerId");
            });

            modelBuilder.Entity<TphOrder>(b =>
            {
                b.ToCollection(ordersCollection);
                b.Property(o => o.OrderDescription).HasElementName("desc");
                b.Property(o => o.CustomerId).HasElementName("cust_id");
                b.Ignore(o => o.Customer);
                b.HasDiscriminator<string>("_t")
                    .HasValue<TphOrder>("Order")
                    .HasValue<PriorityOrder>("Priority");
            });
            modelBuilder.Entity<PriorityOrder>().Property(o => o.PriorityLevel).HasElementName("priority");
        }
    }

    class TypedKeyCustomer<TKey>
    {
        public TKey _id { get; set; }
        public string FullName { get; set; }
        public List<TypedKeyOrder<TKey>> Orders { get; set; }
    }

    class TypedKeyOrder<TKey>
    {
        public ObjectId _id { get; set; }
        public string OrderDescription { get; set; }
        public TKey CustomerId { get; set; }
        public TypedKeyCustomer<TKey> Customer { get; set; }
    }

    class TypedKeyDbContext<TKey>(TemporaryDatabaseFixture database, string ordersCollection, string customersCollection)
        : CrossCollectionDbContextBase<TypedKeyDbContext<TKey>>(database)
    {
        public DbSet<TypedKeyOrder<TKey>> Orders { get; set; }
        public DbSet<TypedKeyCustomer<TKey>> Customers { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<TypedKeyCustomer<TKey>>(b =>
            {
                b.ToCollection(customersCollection);
                b.Property(c => c.FullName).HasElementName("name");
                b.HasMany(c => c.Orders).WithOne(o => o.Customer).HasForeignKey(o => o.CustomerId);
            });

            modelBuilder.Entity<TypedKeyOrder<TKey>>(b =>
            {
                b.ToCollection(ordersCollection);
                b.Property(o => o.OrderDescription).HasElementName("desc");
                b.Property(o => o.CustomerId).HasElementName("cust_id");
            });
        }
    }

    // Three-collection chain for deep cross-collection ThenInclude (Customer -> Order -> OrderItem).
    class ChainCustomer
    {
        public ObjectId _id { get; set; }
        public string FullName { get; set; }
        public List<ChainOrder> Orders { get; set; }
    }

    class ChainOrder
    {
        public ObjectId _id { get; set; }
        public string OrderDescription { get; set; }
        public ObjectId? CustomerId { get; set; }
        public ChainCustomer Customer { get; set; }
        public List<ChainItem> Items { get; set; }
    }

    class ChainItem
    {
        public ObjectId _id { get; set; }
        public string Sku { get; set; }
        public ObjectId? OrderId { get; set; }
        public ChainOrder Order { get; set; }
    }

    class ChainDbContext(TemporaryDatabaseFixture database, string customersCollection, string ordersCollection, string itemsCollection)
        : CrossCollectionDbContextBase<ChainDbContext>(database)
    {
        public DbSet<ChainCustomer> Customers { get; set; }
        public DbSet<ChainOrder> Orders { get; set; }
        public DbSet<ChainItem> Items { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<ChainCustomer>(b =>
            {
                b.ToCollection(customersCollection);
                b.Property(c => c.FullName).HasElementName("name");
                b.HasMany(c => c.Orders).WithOne(o => o.Customer).HasForeignKey(o => o.CustomerId);
            });

            modelBuilder.Entity<ChainOrder>(b =>
            {
                b.ToCollection(ordersCollection);
                b.Property(o => o.OrderDescription).HasElementName("desc");
                b.Property(o => o.CustomerId).HasElementName("cust_id");
                b.HasMany(o => o.Items).WithOne(i => i.Order).HasForeignKey(i => i.OrderId);
            });

            modelBuilder.Entity<ChainItem>(b =>
            {
                b.ToCollection(itemsCollection);
                b.Property(i => i.Sku).HasElementName("sku");
                b.Property(i => i.OrderId).HasElementName("ord_id");
            });
        }
    }

    // Three-collection chain (Customer -> Order -> Item) where the deepest ThenInclude collection nav
    // targets a derived TPH type, exercising the nested $lookup construction sites (not just the
    // top-level Include site).
    class NestedTphCustomer
    {
        public ObjectId _id { get; set; }
        public string FullName { get; set; }
        public List<NestedTphOrder> Orders { get; set; }
    }

    class NestedTphOrder
    {
        public ObjectId _id { get; set; }
        public string OrderDescription { get; set; }
        public ObjectId? CustomerId { get; set; }
        public List<PriorityChainItem> PriorityItems { get; set; }
    }

    class TphChainItem
    {
        public ObjectId _id { get; set; }
        public string Sku { get; set; }
        public ObjectId? OrderId { get; set; }
    }

    class PriorityChainItem : TphChainItem
    {
        public int PriorityLevel { get; set; }
    }

    class NestedTphDbContext(TemporaryDatabaseFixture database, string customersCollection, string ordersCollection, string itemsCollection)
        : CrossCollectionDbContextBase<NestedTphDbContext>(database)
    {
        public DbSet<NestedTphCustomer> Customers { get; set; }
        public DbSet<TphChainItem> Items { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<NestedTphCustomer>(b =>
            {
                b.ToCollection(customersCollection);
                b.Property(c => c.FullName).HasElementName("name");
                b.HasMany(c => c.Orders).WithOne().HasForeignKey(o => o.CustomerId);
            });

            modelBuilder.Entity<NestedTphOrder>(b =>
            {
                b.ToCollection(ordersCollection);
                b.Property(o => o.OrderDescription).HasElementName("desc");
                b.Property(o => o.CustomerId).HasElementName("cust_id");
                b.HasMany(o => o.PriorityItems).WithOne().HasForeignKey("OrderId");
            });

            modelBuilder.Entity<TphChainItem>(b =>
            {
                b.ToCollection(itemsCollection);
                b.Property(i => i.Sku).HasElementName("sku");
                b.Property(i => i.OrderId).HasElementName("ord_id");
                b.HasDiscriminator<string>("_t")
                    .HasValue<TphChainItem>("Item")
                    .HasValue<PriorityChainItem>("Priority");
            });
            modelBuilder.Entity<PriorityChainItem>().Property(i => i.PriorityLevel).HasElementName("priority");
        }
    }

    // Two SIBLING cross-collection joins off a common root (as opposed to ChainCustomer/Order/Item's
    // transitive chain): a Line references both an Order (used in a Where filter) and a Product (via
    // Include) - the shape EF-373 covers.
    class TwoJoinLine
    {
        public ObjectId _id { get; set; }
        public string LineName { get; set; }
        public ObjectId? OrderId { get; set; }
        public TwoJoinOrder Order { get; set; }
        public ObjectId? ProductId { get; set; }
        public TwoJoinProduct Product { get; set; }
    }

    class TwoJoinOrder
    {
        public ObjectId _id { get; set; }
        public string OrderName { get; set; }
    }

    class TwoJoinProduct
    {
        public ObjectId _id { get; set; }
        public string ProductName { get; set; }
    }

    class TwoJoinDbContext(TemporaryDatabaseFixture database, string linesCollection, string ordersCollection, string productsCollection)
        : CrossCollectionDbContextBase<TwoJoinDbContext>(database)
    {
        public DbSet<TwoJoinLine> Lines { get; set; }
        public DbSet<TwoJoinOrder> Orders { get; set; }
        public DbSet<TwoJoinProduct> Products { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<TwoJoinLine>(b =>
            {
                b.ToCollection(linesCollection);
                b.Property(l => l.LineName).HasElementName("name");
                b.Property(l => l.OrderId).HasElementName("ord_id");
                b.Property(l => l.ProductId).HasElementName("prod_id");
                b.HasOne(l => l.Order).WithMany().HasForeignKey(l => l.OrderId);
                b.HasOne(l => l.Product).WithMany().HasForeignKey(l => l.ProductId);
            });

            modelBuilder.Entity<TwoJoinOrder>(b =>
            {
                b.ToCollection(ordersCollection);
                b.Property(o => o.OrderName).HasElementName("name");
            });

            modelBuilder.Entity<TwoJoinProduct>(b =>
            {
                b.ToCollection(productsCollection);
                b.Property(p => p.ProductName).HasElementName("name");
            });
        }
    }

    // Two navigations from the same root that join to the SAME target entity type (self-join shape) -
    // as opposed to TwoJoinLine's two navigations to two DIFFERENT target types.
    class SameTypeJoinLine
    {
        public ObjectId _id { get; set; }
        public string LineName { get; set; }
        public ObjectId? PrimaryOrderId { get; set; }
        public SameTypeJoinOrder PrimaryOrder { get; set; }
        public ObjectId? SecondaryOrderId { get; set; }
        public SameTypeJoinOrder SecondaryOrder { get; set; }
    }

    class SameTypeJoinOrder
    {
        public ObjectId _id { get; set; }
        public string OrderName { get; set; }
    }

    class SameTypeJoinDbContext(TemporaryDatabaseFixture database, string linesCollection, string ordersCollection)
        : CrossCollectionDbContextBase<SameTypeJoinDbContext>(database)
    {
        public DbSet<SameTypeJoinLine> Lines { get; set; }
        public DbSet<SameTypeJoinOrder> Orders { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<SameTypeJoinLine>(b =>
            {
                b.ToCollection(linesCollection);
                b.Property(l => l.LineName).HasElementName("name");
                b.Property(l => l.PrimaryOrderId).HasElementName("primary_ord_id");
                b.Property(l => l.SecondaryOrderId).HasElementName("secondary_ord_id");
                b.HasOne(l => l.PrimaryOrder).WithMany().HasForeignKey(l => l.PrimaryOrderId);
                b.HasOne(l => l.SecondaryOrder).WithMany().HasForeignKey(l => l.SecondaryOrderId);
            });

            modelBuilder.Entity<SameTypeJoinOrder>(b =>
            {
                b.ToCollection(ordersCollection);
                b.Property(o => o.OrderName).HasElementName("name");
            });
        }
    }

    // Sibling reference navigations targeting the SAME entity type, each ThenInclude'd through the
    // same navigation name (EF-376 repro): Root -(ref)-> PrimaryMid / SecondaryMid -(ref)-> Leaf.
    class SiblingRoot
    {
        public ObjectId _id { get; set; }
        public ObjectId? PrimaryMidId { get; set; }
        public SiblingMid PrimaryMid { get; set; }
        public ObjectId? SecondaryMidId { get; set; }
        public SiblingMid SecondaryMid { get; set; }
    }

    class SiblingMid
    {
        public ObjectId _id { get; set; }
        public ObjectId? LeafId { get; set; }
        public SiblingLeaf Leaf { get; set; }
    }

    class SiblingLeaf
    {
        public ObjectId _id { get; set; }
        public string Value { get; set; }
    }

    class SiblingDbContext(TemporaryDatabaseFixture database, string rootsCollection, string midsCollection, string leavesCollection)
        : CrossCollectionDbContextBase<SiblingDbContext>(database)
    {
        public DbSet<SiblingRoot> Roots { get; set; }
        public DbSet<SiblingMid> Mids { get; set; }
        public DbSet<SiblingLeaf> Leaves { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<SiblingRoot>(b =>
            {
                b.ToCollection(rootsCollection);
                b.Property(r => r.PrimaryMidId).HasElementName("pmid_id");
                b.Property(r => r.SecondaryMidId).HasElementName("smid_id");
                b.HasOne(r => r.PrimaryMid).WithMany().HasForeignKey(r => r.PrimaryMidId);
                b.HasOne(r => r.SecondaryMid).WithMany().HasForeignKey(r => r.SecondaryMidId);
            });

            modelBuilder.Entity<SiblingMid>(b =>
            {
                b.ToCollection(midsCollection);
                b.Property(m => m.LeafId).HasElementName("leaf_id");
                b.HasOne(m => m.Leaf).WithMany().HasForeignKey(m => m.LeafId);
            });

            modelBuilder.Entity<SiblingLeaf>(b =>
            {
                b.ToCollection(leavesCollection);
                b.Property(l => l.Value).HasElementName("value");
            });
        }
    }
#endif
}
