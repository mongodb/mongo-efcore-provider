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
using MongoDB.EntityFrameworkCore.Query;
using MongoDB.EntityFrameworkCore.Query.Visitors;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query;

public static class IncludeStrategyTests
{
    class Customer
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
        public List<Order> Orders { get; set; } = [];
    }

    class Order
    {
        public ObjectId Id { get; set; }
        public ObjectId CustomerId { get; set; }
        public Customer? Customer { get; set; }
    }

    class TwoCollectionDbContext : DbContext
    {
        public DbSet<Customer> Customers { get; set; }
        public DbSet<Order> Orders { get; set; }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
            => optionsBuilder
                .UseMongoDB("mongodb://localhost:27017", "UnitTests")
                .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Customer>()
                .HasMany(c => c.Orders)
                .WithOne(o => o.Customer)
                .HasForeignKey(o => o.CustomerId);
        }
    }

    [Fact]
    public static void ChooseStrategy_returns_ClientFanOut_for_cross_collection_nav()
    {
        using var db = new TwoCollectionDbContext();
        var navigation = db.Model
            .FindEntityType(typeof(Customer))!
            .FindNavigation(nameof(Customer.Orders))!;

        Assert.True(MongoIncludeCompiler.IsCrossCollection(navigation));

        // Stage 0 ignores includeExpression
        var strategy = MongoIncludeCompiler.ChooseStrategy(null!, navigation);

        Assert.Equal(IncludeStrategy.ClientFanOut, strategy);
    }
}
