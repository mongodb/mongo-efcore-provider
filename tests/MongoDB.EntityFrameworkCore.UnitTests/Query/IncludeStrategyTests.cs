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
    public static void ChooseStrategy_returns_ServerLookup_for_top_level_principal_collection_nav()
    {
        using var db = new TwoCollectionDbContext();
        var navigation = db.Model
            .FindEntityType(typeof(Customer))!
            .FindNavigation(nameof(Customer.Orders))!;

        Assert.True(MongoIncludeCompiler.IsCrossCollection(navigation));
        Assert.True(navigation.IsCollection);
        Assert.False(navigation.IsOnDependent);

        // EF-117 Task 2.2: a top-level principal→dependent collection Include (with no nested
        // ThenInclude chain) now routes to a server-side $lookup. A null includeExpression means
        // "no nested include", so the routing reduces to the navigation shape.
        var strategy = MongoIncludeCompiler.ChooseStrategy(null, navigation);

        Assert.Equal(IncludeStrategy.ServerLookup, strategy);
    }

    [Fact]
    public static void ChooseStrategy_returns_ServerLookup_for_single_level_single_key_dependent_reference_nav()
    {
        using var db = new TwoCollectionDbContext();
        var navigation = db.Model
            .FindEntityType(typeof(Order))!
            .FindNavigation(nameof(Order.Customer))!;

        Assert.True(MongoIncludeCompiler.IsCrossCollection(navigation));
        Assert.False(navigation.IsCollection);
        Assert.True(navigation.IsOnDependent);
        Assert.Equal(1, navigation.ForeignKey.Properties.Count);

        // EF-117 Stage 3: a single-level dependent→principal reference Include with a
        // single-column foreign key (and no nested ThenInclude chain) now routes to a
        // server-side $lookup + $unwind. A null includeExpression means "no nested include".
        var strategy = MongoIncludeCompiler.ChooseStrategy(null, navigation);

        Assert.Equal(IncludeStrategy.ServerLookup, strategy);
    }
}
