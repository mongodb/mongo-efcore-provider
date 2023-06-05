// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.EntityFrameworkCore;
using MongoDB.EntityFrameworkCore.Extensions;

namespace MongoDB.EntityFrameworkCore.Tests.Metadata.Conventions;

public class CollectionNameFromDbSetConventionTests
{
    [Fact]
    public virtual void DbSet_names_are_used_as_collection_names()
    {
        using var context = new UnnamedCollectionsDbContext();
        Assert.Equal("Customers", context.GetCollectionName<Customer>());
    }

    [Fact]
    public virtual void Explicit_collection_names_can_be_set()
    {
        using var context = new NamedCollectionsDbContext();
        Assert.Equal("customersCollection", context.GetCollectionName<Customer>());
    }

    class Customer
    {
        public int Id { get; set; }
        public string Name { get; set; }
    }

    abstract class BaseDbContext : DbContext
    {
        public DbSet<Customer> Customers { get; set; }
    }

    class UnnamedCollectionsDbContext : BaseDbContext
    {
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
            => optionsBuilder
                .UseMongoDB("mongodb://localhost:27017", "UnitTests");
    }

    class NamedCollectionsDbContext : BaseDbContext
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<Customer>().ToCollection("customersCollection");
        }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
            => optionsBuilder
                .UseMongoDB("mongodb://localhost:27017", "UnitTests");
    }
}
