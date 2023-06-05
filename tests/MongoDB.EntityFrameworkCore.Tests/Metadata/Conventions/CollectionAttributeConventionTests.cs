// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.EntityFrameworkCore;
using MongoDB.EntityFrameworkCore.Extensions;

namespace MongoDB.EntityFrameworkCore.Tests.Metadata.Conventions;

public class CollectionAttributeConventionTests
{
    [Fact]
    public virtual void Collection_attribute_specified_names_are_used_as_collection_names()
    {
        using var context = new BaseDbContext();
        Assert.Equal("attributedCollection", context.GetCollectionName<Customer>());
    }

    [Fact]
    public virtual void Model_builder_specified_names_override_collection_attribute_names()
    {
        using var context = new ModelBuilderSpecifiedDbContext();
        Assert.Equal("namedCollection", context.GetCollectionName<Customer>());
    }

    [Collection("attributedCollection")]
    class Customer
    {
        public int Id { get; set; }
        public string Name { get; set; }
    }

    class BaseDbContext : DbContext
    {
        public DbSet<Customer> Customers { get; set; }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
            => optionsBuilder
                .UseMongoDB("mongodb://localhost:27017", "UnitTests");
    }

    class ModelBuilderSpecifiedDbContext : BaseDbContext
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<Customer>().ToCollection("namedCollection");
        }
    }
}
