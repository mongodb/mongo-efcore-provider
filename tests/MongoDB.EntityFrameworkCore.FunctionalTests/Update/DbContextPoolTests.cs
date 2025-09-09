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
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Extensions;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Update;

[XUnitCollection("UpdateTests")]
public class DbContextPoolTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    [Fact]
    public async Task DbContextPool_honors_AutoTransactionBehavior_set_in_constructor()
    {
        var fixture = new TemporaryDatabaseFixture();
        await fixture.InitializeAsync();

        var serviceProvider = new ServiceCollection()
            .AddEntityFrameworkMongoDB()
            .AddDbContextPool<CustomerContext>(
                (p, o) =>
                    o.UseMongoDB(
                            fixture.TestServer.ConnectionString,
                            database.MongoDatabase.DatabaseNamespace.DatabaseName)
                        .UseInternalServiceProvider(p)
            )
            .BuildServiceProvider();

        const int loopCount = 20;
        var contexts = new HashSet<CustomerContext>();
        var outerScope = serviceProvider.CreateScope();

        for (var i = 0; i < loopCount; i++)
        {
            var innerScope = serviceProvider.CreateScope();
            var isOdd = i % 2 == 1;
            var dbContext = (isOdd ? innerScope : outerScope).ServiceProvider.GetRequiredService<CustomerContext>();
            contexts.Add(dbContext);

            Assert.Equal(AutoTransactionBehavior.Never, dbContext.Database.AutoTransactionBehavior);
            var customer1 = new Customer(Guid.NewGuid().ToString());
            var customer2 = new Customer(Guid.NewGuid().ToString());
            dbContext.AddRange(customer1, customer2);
            dbContext.SaveChanges();
        }

        const int expectedCount = loopCount / 2 + 1; // Half in the inner scope, one for the outer scope
        Assert.Equal(expectedCount, contexts.Count);

        var context = outerScope.ServiceProvider.GetRequiredService<CustomerContext>();
        Assert.Equal(loopCount * 2, context.Customers.Count());
    }

    class CustomerContext : DbContext
    {
        public CustomerContext(DbContextOptions options) : base(options)
        {
            Database.AutoTransactionBehavior = AutoTransactionBehavior.Never;
        }

        public DbSet<Customer> Customers { get; init; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<Customer>().ToCollection("Customers");
        }
    }

    class Customer(string name)
    {
        public ObjectId _id { get; set; } = ObjectId.GenerateNewId();
        public string name { get; set; } = name;
    }
}
