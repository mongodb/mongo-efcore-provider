// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using MongoDB.EntityFrameworkCore.Extensions;

namespace MongoDB.EntityFrameworkCore.Tests.Metadata.Conventions;

public class TableAttributeConventionTests
{
    [Fact]
    public virtual void Table_attribute_specified_names_are_used_as_Table_names()
    {
        using var context = new BaseDbContext();
        Assert.Equal("attributedTable", GetCollectionName<Customer>(context));
    }

    [Fact]
    public virtual void Model_builder_specified_names_override_Table_attribute_names()
    {
        using var context = new ModelBuilderSpecifiedDbContext();
        Assert.Equal("fluentCollection", GetCollectionName<Customer>(context));
    }

    static string GetCollectionName<TEntity>(DbContext context) =>
        context.Model.FindEntityType(typeof(TEntity)).GetCollectionName();

    [Table("attributedTable")]
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
            modelBuilder.Entity<Customer>().ToCollection("fluentCollection");
        }
    }
}
