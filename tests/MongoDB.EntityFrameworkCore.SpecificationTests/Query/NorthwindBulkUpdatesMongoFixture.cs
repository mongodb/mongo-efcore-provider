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

// ExecuteUpdate / ExecuteDelete bulk operations are EF9+ only.
#if !EF8

using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.BulkUpdates;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.TestModels.Northwind;
using Microsoft.EntityFrameworkCore.TestUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Storage;

namespace MongoDB.EntityFrameworkCore.SpecificationTests.Query;

public class NorthwindBulkUpdatesMongoFixture<TModelCustomizer> : NorthwindBulkUpdatesFixture<TModelCustomizer>
    where TModelCustomizer : ITestModelCustomizer, new()
{
    protected override string StoreName { get; } = TestDatabaseNamer.GetUniqueDatabaseName("NorthwindBulkUpdates");

    private ITestStoreFactory? _testStoreFactory;

    protected override ITestStoreFactory TestStoreFactory
        => _testStoreFactory!;

    public TestServer TestServer { get; private set; }

    public override async Task InitializeAsync()
    {
        TestServer = await TestServer.GetOrInitializeTestServerAsync(MongoCondition.None);
        _testStoreFactory = new MongoTestStoreFactory(TestServer);

        await base.InitializeAsync();
    }

    protected override bool UsePooling
        => false;

    public TestMqlLoggerFactory TestMqlLoggerFactory
        => (TestMqlLoggerFactory)ServiceProvider.GetRequiredService<ILoggerFactory>();

    protected override bool ShouldLogCategory(string logCategory)
        => logCategory == DbLoggerCategory.Query.Name;

    // EF's BulkUpdatesAsserter wraps every AssertDelete/AssertUpdate in a transaction that is rolled
    // back, so the shared Northwind seed is left unchanged across the repeated (sync + async) runs.
    // The transaction is begun on one context and the bulk operation is executed on a *second*
    // context; this hook enlists that second context in the same MongoDB session so DeleteMany /
    // UpdateMany participate in — and are rolled back with — the outer transaction. Both contexts
    // are built against the shared TestServer.Client, so the session handle is valid across them
    // (CurrentTransaction's internal setter is reachable here via InternalsVisibleTo). The bulk
    // executor reads the session from Database.CurrentTransaction (see PrepareBulk in
    // MongoShapedQueryCompilingExpressionVisitor).
    //
    // NOTE: this requires a transaction-capable deployment (replica set / mongos). Against a
    // standalone mongod the BeginTransactionAsync in the asserter will throw, and these tests fail
    // at setup rather than asserting anything meaningful.
    public override void UseTransaction(DatabaseFacade facade, IDbContextTransaction transaction)
        => ((MongoTransactionManager)facade.GetService<IDbContextTransactionManager>()).CurrentTransaction = transaction;

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

#endif
