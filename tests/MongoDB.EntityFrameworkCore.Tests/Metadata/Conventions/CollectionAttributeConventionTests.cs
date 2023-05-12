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
        Assert.Equal("attributedCollection", GetCollectionName<Customer>(context));
    }

    [Fact]
    public virtual void Model_builder_specified_names_override_collection_attribute_names()
    {
        using var context = new ModelBuilderSpecifiedDbContext();
        Assert.Equal("namedCollection", GetCollectionName<Customer>(context));
    }

    static string GetCollectionName<TEntity>(DbContext context) =>
        context.Model.FindEntityType(typeof(TEntity)).GetCollectionName();

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
                .UseMongo("mongodb://localhost:27017", "UnitTests");
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
