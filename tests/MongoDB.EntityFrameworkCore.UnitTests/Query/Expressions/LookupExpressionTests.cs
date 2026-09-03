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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.Query.Expressions;
using Xunit;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query.Expressions;

/// <summary>
/// Unit tests for <see cref="LookupExpression"/>, including the <see cref="LookupPipelineKind"/> discriminator.
/// </summary>
public class LookupExpressionTests
{
    [Fact]
    public void New_lookup_defaults_to_PipelineKind_None()
    {
        var navigation = GetCustomerOrdersNavigation();
        var lookup = new LookupExpression(navigation);

        Assert.Equal(LookupPipelineKind.None, lookup.PipelineKind);
    }

    [Fact]
    public void Lookup_for_navigation_targeting_collection_in_separate_entity_is_marked_FallbackOnly()
    {
        // When a lookup targets a collection navigation to a TPH-derived type, it should be marked FallbackOnly
        // because the native translator cannot narrow the collection scope to just the derived discriminator value.
        var dogsNavigation = GetOwnerDogsNavigation();
        var lookup = new LookupExpression(dogsNavigation);

        // The lookup should be marked FallbackOnly because it targets a TPH-derived type collection.
        Assert.Equal(LookupPipelineKind.FallbackOnly, lookup.PipelineKind);
        Assert.True(lookup.HasPipeline);
        // The pipeline should contain a $match stage to filter by the discriminator
        Assert.Single(lookup.PipelineStages);
        Assert.NotNull(lookup.PipelineStages[0]);
        Assert.True(lookup.PipelineStages[0].Contains("$match"));
    }

    private static Microsoft.EntityFrameworkCore.Metadata.INavigation GetCustomerOrdersNavigation()
    {
        using var db = new SimpleDbContext();
        var customerType = db.Model.FindEntityType(typeof(Customer))!;
        return customerType.GetNavigations().Single(n => n.IsCollection && n.Name == nameof(Customer.Orders));
    }

    private static Microsoft.EntityFrameworkCore.Metadata.INavigation GetOwnerDogsNavigation()
    {
        using var db = new TphDbContext();
        var ownerType = db.Model.FindEntityType(typeof(Owner))!;
        return ownerType.GetNavigations().Single(n => n.IsCollection && n.Name == nameof(Owner.Dogs));
    }


    private class Customer
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
        public ICollection<Order> Orders { get; set; } = [];
    }

    private class Order
    {
        public ObjectId Id { get; set; }
        public ObjectId CustomerId { get; set; }
        public string Product { get; set; } = "";
    }

    // Owner with a collection navigation to a TPH-derived animal type (Dog)
    private class Owner
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
        public List<Dog> Dogs { get; set; } = [];
    }

    // TPH hierarchy types for testing discriminator-narrowed navigation
    private abstract class AnimalBase
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
        public ObjectId? OwnerId { get; set; }
    }

    private class Dog : AnimalBase
    {
        public string Breed { get; set; } = "";
    }

    private class Cat : AnimalBase
    {
        public string Color { get; set; } = "";
    }

    private class SimpleDbContext : DbContext
    {
        public DbSet<Customer> Customers => Set<Customer>();
        public DbSet<Order> Orders => Set<Order>();

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
            => optionsBuilder
                .UseMongoDB("mongodb://localhost:27017", "UnitTests")
                .ReplaceService<IModelCacheKeyFactory, IgnoreCacheKeyFactory>()
                .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<Customer>()
                .HasMany(c => c.Orders)
                .WithOne()
                .HasForeignKey(o => o.CustomerId);
        }
    }

    private class TphDbContext : DbContext
    {
        public DbSet<Owner> Owners => Set<Owner>();
        public DbSet<AnimalBase> Animals => Set<AnimalBase>();

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
            => optionsBuilder
                .UseMongoDB("mongodb://localhost:27017", "UnitTests")
                .ReplaceService<IModelCacheKeyFactory, IgnoreCacheKeyFactory>()
                .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // Configure Owner with a collection navigation to Dog (a TPH-derived type)
            modelBuilder.Entity<Owner>(b =>
            {
                b.HasMany(o => o.Dogs).WithOne().HasForeignKey(d => d.OwnerId);
            });

            // Register the TPH hierarchy with AnimalBase as root
            modelBuilder.Entity<AnimalBase>(b =>
            {
                b.HasDiscriminator<string>("AnimalType")
                    .HasValue<Dog>("Dog")
                    .HasValue<Cat>("Cat");
            });
            // Explicitly touch the derived types to ensure proper registration
            modelBuilder.Entity<Dog>();
            modelBuilder.Entity<Cat>();
        }
    }

    private sealed class IgnoreCacheKeyFactory : IModelCacheKeyFactory
    {
        private static int _count;
        public object Create(DbContext context, bool designTime) => Interlocked.Increment(ref _count);
    }
}
