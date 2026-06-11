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
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Extensions;

namespace MongoDB.EntityFrameworkCore.UnitTests.Metadata.Conventions;

public static class MongoRelationshipDiscoveryConventionTests
{
    [Fact]
    public static void Navigation_to_type_without_DbSet_is_owned()
    {
        using var db = SingleEntityDbContext.Create<OrderWithAddress>();

        var orderType = db.Model.FindEntityType(typeof(OrderWithAddress));
        Assert.NotNull(orderType);

        var addressType = db.Model.FindEntityType(typeof(Address));
        Assert.NotNull(addressType);
        Assert.NotNull(addressType.FindOwnership());
    }

    [Fact]
    public static void Navigation_to_type_with_DbSet_is_not_owned()
    {
        using var db = new OrderCustomerDbContext();

        var orderType = db.Model.FindEntityType(typeof(Order));
        Assert.NotNull(orderType);

        var customerType = db.Model.FindEntityType(typeof(Customer));
        Assert.NotNull(customerType);
        Assert.Null(customerType.FindOwnership());
        Assert.True(customerType.IsDocumentRoot());
    }

    [Fact]
    public static void Navigation_to_type_with_DbSet_creates_foreign_key()
    {
        using var db = new OrderCustomerDbContext();

        var orderType = db.Model.FindEntityType(typeof(Order));
        Assert.NotNull(orderType);

        var navigation = orderType.FindNavigation(nameof(Order.Customer));
        Assert.NotNull(navigation);
        Assert.NotNull(navigation.ForeignKey);
    }

    [Fact]
    public static void Collection_navigation_to_type_with_DbSet_is_not_owned()
    {
        using var db = new OrderCustomerDbContext();

        var customerType = db.Model.FindEntityType(typeof(Customer));
        Assert.NotNull(customerType);

        var orderType = db.Model.FindEntityType(typeof(Order));
        Assert.NotNull(orderType);
        Assert.Null(orderType.FindOwnership());

        var navigation = customerType.FindNavigation(nameof(Customer.Orders));
        Assert.NotNull(navigation);
    }

    [Fact]
    public static void Embedded_type_without_DbSet_remains_owned()
    {
        using var db = new OrderCustomerDbContext();

        var orderType = db.Model.FindEntityType(typeof(Order));
        Assert.NotNull(orderType);

        var shippingAddressType = db.Model.FindEntityType(typeof(Address));
        Assert.NotNull(shippingAddressType);
        Assert.NotNull(shippingAddressType.FindOwnership());
    }

    class Order
    {
        public ObjectId _id { get; set; }
        public string Description { get; set; }
        public Customer Customer { get; set; }
        public Address ShippingAddress { get; set; }
    }

    class Customer
    {
        public ObjectId _id { get; set; }
        public string Name { get; set; }
        public List<Order> Orders { get; set; }
    }

    class Address
    {
        public string Street { get; set; }
        public string City { get; set; }
    }

    class OrderWithAddress
    {
        public ObjectId _id { get; set; }
        public Address ShippingAddress { get; set; }
    }

    class OrderCustomerDbContext : DbContext
    {
        public DbSet<Order> Orders { get; set; }
        public DbSet<Customer> Customers { get; set; }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
            => optionsBuilder
                .UseMongoDB("mongodb://localhost:27017", "UnitTests")
                .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
    }
}
