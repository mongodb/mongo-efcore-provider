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
using Microsoft.EntityFrameworkCore.Metadata;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.Query.Expressions;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query.NativeTranslation;

/// <summary>
/// Tests for <see cref="MongoQueryExpression.GetStreamingReferenceLookups"/>, which
/// reconstructs the list of <see cref="LookupExpression"/>s the native streaming path must
/// emit as $lookup + $unwind stages for single-level reference Includes.
/// </summary>
public class StreamingReferenceLookupsTests
{
    // ── Entity model ─────────────────────────────────────────────────────────────

    private class Customer
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
    }

    private class Order
    {
        public ObjectId Id { get; set; }
        public ObjectId CustomerId { get; set; }
        public Customer? Customer { get; set; }
    }

    // ── Two-entity DbContext ──────────────────────────────────────────────────────

    private class TwoEntityDbContext : DbContext
    {
        public DbSet<Order> Orders => Set<Order>();
        public DbSet<Customer> Customers => Set<Customer>();

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
            => optionsBuilder
                .UseMongoDB("mongodb://localhost:27017", "UnitTests")
                .ReplaceService<IModelCacheKeyFactory, IgnoreCacheKeyFactory>()
                .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<Order>()
                .HasOne(o => o.Customer)
                .WithMany()
                .HasForeignKey(o => o.CustomerId);
        }

        private sealed class IgnoreCacheKeyFactory : IModelCacheKeyFactory
        {
            private static int _count;
            public object Create(DbContext context, bool designTime) => Interlocked.Increment(ref _count);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────

    private static (IEntityType orderEntityType, IEntityType customerEntityType) GetEntityTypes()
    {
        using var db = new TwoEntityDbContext();
        return (db.Model.FindEntityType(typeof(Order))!, db.Model.FindEntityType(typeof(Customer))!);
    }

    // ── Test 1: Flat query — no inner collections → GetStreamingReferenceLookups is empty ──

    [Fact]
    public void No_inner_collections_returns_empty_lookups()
    {
        var (orderEntityType, _) = GetEntityTypes();
        var query = new MongoQueryExpression(orderEntityType);

        // No AddInnerCollection called → UsesDriverJoinFields is false, no pending lookups
        var result = query.GetStreamingReferenceLookups();

        Assert.Empty(result);
    }

    // ── Test 2: Single reference Include in driver-join state → synthesizes one LookupExpression ──

    [Fact]
    public void Single_reference_include_in_driver_join_state_returns_one_lookup()
    {
        var (orderEntityType, customerEntityType) = GetEntityTypes();
        var query = new MongoQueryExpression(orderEntityType);

        // Simulate the driver-native LeftJoin: one inner collection, no forced-unwind pending lookup.
        // UsesDriverJoinFields will be true after this.
        query.AddInnerCollection(customerEntityType);

        Assert.True(query.UsesDriverJoinFields);
        Assert.Empty(query.GetPendingLookups());

        var result = query.GetStreamingReferenceLookups();

        Assert.Single(result);

        var lookup = result[0];
        // The navigation on Order → Customer is a single reference (not a collection).
        Assert.False(lookup.Navigation.IsCollection);
        Assert.Equal("Customer", lookup.Navigation.Name);
        // As = "_lookup_Customer"
        Assert.Equal("_lookup_Customer", lookup.As);
        // From = the Customer collection name
        var expectedFrom = customerEntityType.GetCollectionName();
        Assert.Equal(expectedFrom, lookup.From);
        // LocalField = FK property element name on Order (CustomerId → customerId by convention)
        Assert.False(string.IsNullOrEmpty(lookup.LocalField));
        // ForeignField = PK property element name on Customer (_id)
        Assert.False(string.IsNullOrEmpty(lookup.ForeignField));
    }

    // ── Test 3: Query with pending lookups (forced-unwind) → returns pending lookups directly ──

    [Fact]
    public void Pending_lookups_registered_returns_them_directly()
    {
        var (orderEntityType, customerEntityType) = GetEntityTypes();
        var query = new MongoQueryExpression(orderEntityType);

        // Register a lookup via AddLookup (forced-unwind mode).
        var nav = orderEntityType.GetNavigations()
            .Single(n => n.TargetEntityType == customerEntityType && !n.IsCollection);
        var explicitLookup = new LookupExpression(nav, forceUnwind: true);
        query.AddLookup(explicitLookup);

        // With a ForceUnwind lookup registered, UsesDriverJoinFields should be false.
        Assert.False(query.UsesDriverJoinFields);

        var result = query.GetStreamingReferenceLookups();

        Assert.Single(result);
        Assert.Same(explicitLookup, result[0]);
    }

    // ── Test 4: Lookups slot on MongoQueryExpression delegates to GetStreamingReferenceLookups ──

    [Fact]
    public void Lookups_property_delegates_to_GetStreamingReferenceLookups()
    {
        var (orderEntityType, customerEntityType) = GetEntityTypes();
        var query = new MongoQueryExpression(orderEntityType);

        query.AddInnerCollection(customerEntityType);

        // The Lookups slot and GetStreamingReferenceLookups() must return the same result.
        var fromSlot = query.Lookups;
        var fromMethod = query.GetStreamingReferenceLookups();

        Assert.Equal(fromMethod.Count, fromSlot.Count);
        if (fromMethod.Count > 0)
        {
            Assert.Equal(fromMethod[0].Navigation, fromSlot[0].Navigation);
        }
    }
}
