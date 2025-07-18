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
using MongoDB.EntityFrameworkCore.SpecificationTests.Utilities;

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
        => MongoNorthwindTestStoreFactory.Instance;

    protected override bool UsePooling
        => false;

    public TestMqlLoggerFactory TestMqlLoggerFactory
        => (TestMqlLoggerFactory)ServiceProvider.GetRequiredService<ILoggerFactory>();

    protected override bool ShouldLogCategory(string logCategory)
        => logCategory == DbLoggerCategory.Query.Name;

    protected override void OnModelCreating(ModelBuilder modelBuilder, DbContext context)
    {
        base.OnModelCreating(modelBuilder, context);

        modelBuilder.Entity<Customer>().ToCollection("Customers");
        modelBuilder.Entity<Employee>().ToCollection("Employees");
        modelBuilder.Entity<Order>().ToCollection("Orders");
        modelBuilder.Entity<OrderDetail>().ToCollection("OrderDetails");
        modelBuilder.Entity<Product>().ToCollection("Products");

        // TODO: File an issue to not throw for keyless entity types
        modelBuilder.Ignore<OrderQuery>();
        modelBuilder.Ignore<ProductQuery>();
        modelBuilder.Ignore<ProductView>();
        modelBuilder.Ignore<CustomerQueryWithQueryFilter>();
        modelBuilder.Ignore<CustomerQuery>();
    }

    public override DbContextOptionsBuilder AddOptions(DbContextOptionsBuilder builder)
        => base.AddOptions(builder).ConfigureWarnings(
            c => c.Log(CoreEventId.MappedEntityTypeIgnoredWarning)); // Needed because we ignore keyless types above
}
