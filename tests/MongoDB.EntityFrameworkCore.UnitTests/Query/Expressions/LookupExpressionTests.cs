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
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.Query.Expressions;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query.Expressions;

public static class LookupExpressionTests
{
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

    class OrderDetail
    {
        public int OrderId { get; set; }
        public int ProductId { get; set; }
        public int Quantity { get; set; }
    }

    class LineItem
    {
        public ObjectId Id { get; set; }
        public int OrderId { get; set; }
        public OrderDetail OrderDetail { get; set; }
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

    class CompositeKeyDbContext : DbContext
    {
        public DbSet<OrderDetail> OrderDetails { get; set; }
        public DbSet<LineItem> LineItems { get; set; }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
            => optionsBuilder
                .UseMongoDB("mongodb://localhost:27017", "UnitTests")
                .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<OrderDetail>().HasKey(x => new { x.OrderId, x.ProductId });
            modelBuilder.Entity<LineItem>()
                .HasOne(x => x.OrderDetail)
                .WithMany()
                .HasForeignKey(x => x.OrderId)
                .HasPrincipalKey(x => x.OrderId);
        }
    }

    [Fact]
    public static void Dependent_side_reference_navigation_derives_fields()
    {
        using var db = new LookupDbContext();
        var orderType = db.Model.FindEntityType(typeof(Order))!;
        var customerType = db.Model.FindEntityType(typeof(Customer))!;
        var navigation = orderType.FindNavigation(nameof(Order.Customer))!;

        var lookup = new LookupExpression(navigation);

        Assert.Equal(customerType.GetCollectionName(), lookup.From);
        Assert.Equal("_lookup_Customer", lookup.As);
        Assert.True(lookup.IsReference);
        Assert.True(lookup.ShouldUnwind);

        var fk = navigation.ForeignKey;
        Assert.Equal(fk.Properties[0].GetElementName(), lookup.LocalField);
        Assert.Equal(fk.PrincipalKey.Properties[0].GetElementName(), lookup.ForeignField);
    }

    [Fact]
    public static void Principal_side_collection_navigation_derives_fields()
    {
        using var db = new LookupDbContext();
        var customerType = db.Model.FindEntityType(typeof(Customer))!;
        var navigation = customerType.FindNavigation(nameof(Customer.Orders))!;

        var lookup = new LookupExpression(navigation);

        Assert.Equal("_lookup_Orders", lookup.As);
        Assert.False(lookup.IsReference);

        var fk = navigation.ForeignKey;
        // Roles reversed vs the reference case.
        Assert.Equal(fk.PrincipalKey.Properties[0].GetElementName(), lookup.LocalField);
        Assert.Equal(fk.Properties[0].GetElementName(), lookup.ForeignField);
    }

    [Fact]
    public static void Composite_key_target_produces_id_dotted_path()
    {
        using var db = new CompositeKeyDbContext();
        var lineItemType = db.Model.FindEntityType(typeof(LineItem))!;
        var navigation = lineItemType.FindNavigation(nameof(LineItem.OrderDetail))!;

        var lookup = new LookupExpression(navigation);

        var fk = navigation.ForeignKey;
        var principalProperty = fk.PrincipalKey.Properties[0];

        Assert.True(principalProperty.IsPrimaryKey());
        Assert.True(((IEntityType)principalProperty.DeclaringType).FindPrimaryKey()!.Properties.Count > 1);

        // LineItem.OrderDetail is a dependent-side reference; ForeignField is the principal-key
        // property, which is part of OrderDetail's composite PK stored under _id.
        Assert.Equal($"_id.{principalProperty.GetElementName()}", lookup.ForeignField);
    }
}
