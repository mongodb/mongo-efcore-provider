// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.EntityFrameworkCore;
using MongoDB.Bson;

namespace MongoDB.EntityFrameworkCore.Tests.Metadata.Conventions;

public class IdPrimaryKeyConventionTests
{
    [Fact]
    public virtual void Id_fields_are_identified_as_primary_keys_when_strings()
    {
        using var context = new MyDbContext();

        var keys = context.Model.FindEntityType(typeof(Vendor)).GetKeys().ToArray();

        var expectedProperty = Utilities.GetPropertyInfo((Vendor v) => v._id);

        Assert.Single(keys);
        Assert.Single(keys[0].Properties, p => p.PropertyInfo.Equals(expectedProperty));
    }

    [Fact]
    public virtual void Id_field_are_identified_as_primary_keys_when_objectids()
    {
        using var context = new MyDbContext();

        var keys = context.Model.FindEntityType(typeof(Customer)).GetKeys().ToArray();

        var expectedProperty = Utilities.GetPropertyInfo((Customer c) => c._id);

        Assert.Single(keys);
        Assert.Single(keys[0].Properties, p => p.PropertyInfo.Equals(expectedProperty));
    }

    class Vendor
    {
        public string _id { get; set; }
        public string name { get; set; }
    }

    class Customer
    {
        public ObjectId _id { get; set; }
        public string name { get; set; }
    }

    abstract class BaseDbContext : DbContext
    {
        public DbSet<Vendor> Vendors { get; set; }
        public DbSet<Customer> Customers { get; set; }
    }

    class MyDbContext : BaseDbContext
    {
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
            => optionsBuilder
                .UseMongoDB("mongodb://localhost:27017", "UnitTests");
    }
}
