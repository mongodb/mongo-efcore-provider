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

using System.Collections.Generic;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Query.Expressions;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query.Expressions;

public static class MongoQueryExpressionTests
{
    class Product
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; }
        public decimal Price { get; set; }
    }

    class Customer
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; }
        public List<Order> Orders { get; set; }
    }

    class Order
    {
        public ObjectId Id { get; set; }
        public ObjectId CustomerId { get; set; }
        public Customer Customer { get; set; }
    }

    class QueryDbContext : DbContext
    {
        public DbSet<Product> Products { get; set; }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
            => optionsBuilder
                .UseMongoDB("mongodb://localhost:27017", "UnitTests")
                .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
    }

    class LookupDbContext : DbContext
    {
        public DbSet<Customer> Customers { get; set; }
        public DbSet<Order> Orders { get; set; }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
            => optionsBuilder
                .UseMongoDB("mongodb://localhost:27017", "UnitTests")
                .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
    }

    [Fact]
    public static void Set_collection_to_entity_passed_in_constructor()
    {
        using var db = new QueryDbContext();
        var expectedEntityType = db.Model.GetEntityTypes().First();

        var actual = new MongoQueryExpression(expectedEntityType);

        Assert.Equal(expectedEntityType, actual.CollectionExpression.EntityType);
    }

    [Fact]
    public static void PendingLookups_is_empty_on_fresh_MongoQueryExpression()
    {
        using var db = new LookupDbContext();
        var customerType = db.Model.FindEntityType(typeof(Customer))!;

        var queryExpression = new MongoQueryExpression(customerType);

        Assert.Empty(queryExpression.PendingLookups);
    }

    [Fact]
    public static void AddLookup_adds_lookup_visible_in_PendingLookups()
    {
        using var db = new LookupDbContext();
        var customerType = db.Model.FindEntityType(typeof(Customer))!;
        var navigation = customerType.FindNavigation(nameof(Customer.Orders))!;
        var lookup = new LookupExpression(navigation);

        var queryExpression = new MongoQueryExpression(customerType);
        queryExpression.AddLookup(lookup);

        Assert.Single(queryExpression.PendingLookups);
        Assert.Same(lookup, queryExpression.PendingLookups[0]);
    }

    [Fact]
    public static void AddLookup_deduplicates_by_output_alias()
    {
        using var db = new LookupDbContext();
        var customerType = db.Model.FindEntityType(typeof(Customer))!;
        var navigation = customerType.FindNavigation(nameof(Customer.Orders))!;
        var lookup1 = new LookupExpression(navigation);
        var lookup2 = new LookupExpression(navigation); // same As value: "_lookup_Orders"

        var queryExpression = new MongoQueryExpression(customerType);
        queryExpression.AddLookup(lookup1);
        queryExpression.AddLookup(lookup2);

        Assert.Single(queryExpression.PendingLookups);
        Assert.Same(lookup1, queryExpression.PendingLookups[0]);
    }

    [Fact]
    public static void AddLookup_with_different_aliases_adds_both()
    {
        using var db = new LookupDbContext();
        var customerType = db.Model.FindEntityType(typeof(Customer))!;
        var orderType = db.Model.FindEntityType(typeof(Order))!;
        var ordersNavigation = customerType.FindNavigation(nameof(Customer.Orders))!;
        var customerNavigation = orderType.FindNavigation(nameof(Order.Customer))!;
        var lookupOrders = new LookupExpression(ordersNavigation);   // As = "_lookup_Orders"
        var lookupCustomer = new LookupExpression(customerNavigation); // As = "_lookup_Customer"

        var queryExpression = new MongoQueryExpression(customerType);
        queryExpression.AddLookup(lookupOrders);
        queryExpression.AddLookup(lookupCustomer);

        Assert.Equal(2, queryExpression.PendingLookups.Count);
    }

    [Fact]
    public static void UsesDriverJoinFields_defaults_to_false()
    {
        using var db = new LookupDbContext();
        var customerType = db.Model.FindEntityType(typeof(Customer))!;

        var queryExpression = new MongoQueryExpression(customerType);

        Assert.False(queryExpression.UsesDriverJoinFields);
    }

    [Fact]
    public static void UsesDriverJoinFields_can_be_set_to_true()
    {
        using var db = new LookupDbContext();
        var customerType = db.Model.FindEntityType(typeof(Customer))!;

        var queryExpression = new MongoQueryExpression(customerType);
        queryExpression.UsesDriverJoinFields = true;

        Assert.True(queryExpression.UsesDriverJoinFields);
    }
}
