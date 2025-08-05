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
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.TestModels.Northwind;
using Microsoft.EntityFrameworkCore.TestUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MongoDB.EntityFrameworkCore.Extensions;

namespace MongoDB.EntityFrameworkCore.SpecificationTests.Query;

public class NorthwindQueryMongoFixture<TModelCustomizer> : NorthwindQueryFixtureBase<TModelCustomizer>
    where TModelCustomizer :
#if EF9
    ITestModelCustomizer,
#else
    IModelCustomizer,
#endif
    new()
{
    protected override ITestStoreFactory TestStoreFactory
        => MongoTestStoreFactory.Instance;

    protected override bool UsePooling
        => false;

    public TestMqlLoggerFactory TestMqlLoggerFactory
        => (TestMqlLoggerFactory)ServiceProvider.GetRequiredService<ILoggerFactory>();

    protected override bool ShouldLogCategory(string logCategory)
        => logCategory == DbLoggerCategory.Query.Name;

#if !EF9
    protected override void Seed(NorthwindContext context)
    {
        AddEntities(context);

        context.SaveChanges();
    }
#endif

    protected override Task SeedAsync(NorthwindContext context)
    {
        AddEntities(context);

        return context.SaveChangesAsync();
    }

    private static void AddEntities(NorthwindContext context)
    {
        context.Set<Customer>().AddRange(NorthwindData.CreateCustomers());
        context.Set<Employee>().AddRange(NorthwindData.CreateEmployees());

        var orders = NorthwindData.CreateOrders();
        foreach (var order in orders)
        {
            if (order.OrderDate != null)
            {
                // Force the DateTime to be UTC since Mongo will otherwise convert from local to UTC on insertion
                var o = order.OrderDate.Value;
                order.OrderDate = new DateTime(o.Year, o.Month, o.Day, o.Hour, o.Minute, o.Second, o.Millisecond, DateTimeKind.Utc);
            }
        }

        context.Set<Order>().AddRange(orders);

        context.Set<Product>().AddRange(NorthwindData.CreateProducts());
        context.Set<OrderDetail>().AddRange(NorthwindData.CreateOrderDetails());
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder, DbContext context)
    {
        base.OnModelCreating(modelBuilder, context);

        modelBuilder.Entity<Customer>().ToCollection("Customers");
        modelBuilder.Entity<Employee>().ToCollection("Employees");
        modelBuilder.Entity<Order>().ToCollection("Orders");
        modelBuilder.Entity<OrderDetail>().ToCollection("OrderDetails");
        modelBuilder.Entity<Product>().ToCollection("Products");

        modelBuilder.Entity<CustomerQuery>().ToCollection("Customers");
        modelBuilder.Entity<OrderQuery>().ToCollection("Orders");
        modelBuilder.Entity<ProductQuery>().ToCollection("Products");
        modelBuilder.Entity<ProductView>(b =>
        {
            b.Property(e => e.ProductID).HasElementName("_id");
            b.ToCollection("Products");
        });
    }
}
