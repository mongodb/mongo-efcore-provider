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
using MongoDB.Bson;

namespace MongoDB.EntityFrameworkCore.UnitTests.Metadata.Conventions;

public static class PrimaryKeyDiscoveryConventionTests
{
    [Fact]
    public static void Id_fields_are_identified_as_primary_keys_when_strings()
    {
        using var db = new MyDbContext();

        var entityType = db.Model.FindEntityType(typeof(Vendor));
        Assert.NotNull(entityType);
        var keys = entityType.GetKeys().ToArray();
        var expectedProperty = Utilities.GetPropertyInfo((Vendor v) => v._id);

        Assert.Single(keys);
        Assert.Single(keys[0].Properties, p => p.PropertyInfo.Equals(expectedProperty));
    }

    [Fact]
    public static void Id_field_are_identified_as_primary_keys_when_objectids()
    {
        using var db = new MyDbContext();

        var entityType = db.Model.FindEntityType(typeof(Customer));
        Assert.NotNull(entityType);
        var keys = entityType.GetKeys().ToArray();
        var expectedProperty = Utilities.GetPropertyInfo((Customer c) => c._id);

        Assert.Single(keys);
        Assert.Single(keys[0].Properties, p => p.PropertyInfo.Equals(expectedProperty));
    }

    [Fact]
    public static void Many_to_many_join_entity_gets_composite_primary_key_without_extra_shadow_property()
    {
        using var db = new ManyToManyDbContext();

        var joinEntityType = db.Model.GetEntityTypes().Single(e => e.ClrType == typeof(Dictionary<string, object>));

        var primaryKey = joinEntityType.FindPrimaryKey();
        Assert.NotNull(primaryKey);
        Assert.Equal(["FruitsId", "JamsId"], primaryKey.Properties.Select(p => p.Name).OrderBy(n => n));

        Assert.Equal(["FruitsId", "JamsId"], joinEntityType.GetProperties().Select(p => p.Name).OrderBy(n => n));
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

    class Fruit
    {
        public Guid Id { get; set; }
        public string Name { get; set; }
        public List<Jam> Jams { get; } = new();
    }

    class Jam
    {
        public Guid Id { get; set; }
        public string Name { get; set; }
        public List<Fruit> Fruits { get; } = new();
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
                .UseMongoDB("mongodb://localhost:27017", "UnitTests")
                .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
    }

    class ManyToManyDbContext : DbContext
    {
        public DbSet<Fruit> Fruits { get; set; }
        public DbSet<Jam> Jams { get; set; }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
            => optionsBuilder
                .UseMongoDB("mongodb://localhost:27017", "UnitTests")
                .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));

        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<Fruit>().HasMany(f => f.Jams).WithMany(j => j.Fruits);
    }
}
