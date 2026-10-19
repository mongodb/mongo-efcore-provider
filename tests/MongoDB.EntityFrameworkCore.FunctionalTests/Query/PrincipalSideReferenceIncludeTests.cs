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
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Infrastructure;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// A reference <c>Include</c> from the principal side of a 1:1 (required FK on the dependent) is always a LeftJoin in
/// EF's navigation expansion: a principal without a dependent must survive. The Include admission used to guess the
/// <c>$unwind</c>'s <c>preserveNullAndEmptyArrays</c> from <c>ForeignKey.IsRequired</c> and dropped them in every mode.
/// Rows are raw <see cref="BsonDocument"/> seeds so a missing dependent is real data.
/// </summary>
[XUnitCollection("QueryTests")]
public class PrincipalSideReferenceIncludeTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.NativeOnly)]
    [InlineData(MongoQueryMode.DriverLinq)]
    public void Include_principal_side_reference_with_required_dependent_fk_keeps_principals_without_a_dependent(MongoQueryMode mode)
    {
        using var db = Setup(mode);
        Assert.Equal(["with:Paris", "without:<none>"],
            db.Customers.Include(c => c.Address).OrderBy(c => c.Id).ToList()
                .Select(c => $"{c.Name}:{c.Address?.City ?? "<none>"}").ToList());
    }

    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.NativeOnly)]
    [InlineData(MongoQueryMode.DriverLinq)]
    public void Include_principal_side_reference_AsNoTracking_keeps_principals_without_a_dependent(MongoQueryMode mode)
    {
        using var db = Setup(mode);
        Assert.Equal(["with:Paris", "without:<none>"],
            db.Customers.AsNoTracking().Include(c => c.Address).OrderBy(c => c.Id).ToList()
                .Select(c => $"{c.Name}:{c.Address?.City ?? "<none>"}").ToList());
    }

    // Dependent-side control: EF emits an inner Join for a required FK, so the dangling row drops.
    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.NativeOnly)]
    [InlineData(MongoQueryMode.DriverLinq)]
    public void Include_dependent_side_reference_with_required_fk_drops_dangling_dependent(MongoQueryMode mode)
    {
        using var db = Setup(mode);
        Assert.Equal(["Paris:with"],
            db.Addresses.Include(a => a.Customer).OrderBy(a => a.Id).ToList()
                .Select(a => $"{a.City}:{a.Customer?.Name}").ToList());
    }

    // Dependent-side control with an optional FK: a LeftJoin, so the dangling row stays with a null navigation.
    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.NativeOnly)]
    [InlineData(MongoQueryMode.DriverLinq)]
    public void Include_dependent_side_reference_with_optional_fk_keeps_dangling_dependent(MongoQueryMode mode)
    {
        using var db = Setup(mode);
        Assert.Equal(["111:Ann", "222:<none>"],
            db.Phones.Include(p => p.Person).OrderBy(p => p.Id).ToList()
                .Select(p => $"{p.Number}:{p.Person?.Name ?? "<none>"}").ToList());
    }

    // Regression pins for neighbouring shapes that were already right.
    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.NativeOnly)]
    [InlineData(MongoQueryMode.DriverLinq)]
    public void Neighbouring_navigation_shapes_keep_principals_without_a_dependent(MongoQueryMode mode)
    {
        using var db = Setup(mode);

        Assert.Equal(["with:Paris", "without:<null>"],
            db.Customers.OrderBy(c => c.Id).Select(c => new { c.Name, c.Address!.City }).ToList()
                .Select(x => $"{x.Name}:{x.City ?? "<null>"}").ToList());
        Assert.Equal(["without"],
            db.Customers.Where(c => c.Address == null).OrderBy(c => c.Id).Select(c => c.Name).ToList());
        Assert.Equal(["with"],
            db.Customers.Where(c => c.Address != null).OrderBy(c => c.Id).Select(c => c.Name).ToList());
    }

    private PrincipalDbContext Setup(MongoQueryMode mode)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var customers = TemporaryDatabaseFixtureBase.CreateCollectionName("PsriCustomers") + suffix;
        var addresses = TemporaryDatabaseFixtureBase.CreateCollectionName("PsriAddresses") + suffix;
        var people = TemporaryDatabaseFixtureBase.CreateCollectionName("PsriPeople") + suffix;
        var phones = TemporaryDatabaseFixtureBase.CreateCollectionName("PsriPhones") + suffix;

        database.MongoDatabase.GetCollection<BsonDocument>(customers).InsertMany([
            new BsonDocument { { "_id", 1 }, { "Name", "with" } },
            new BsonDocument { { "_id", 2 }, { "Name", "without" } }
        ]);
        database.MongoDatabase.GetCollection<BsonDocument>(addresses).InsertMany([
            new BsonDocument { { "_id", 10 }, { "CustomerId", 1 }, { "City", "Paris" } },
            new BsonDocument { { "_id", 11 }, { "CustomerId", 99 }, { "City", "Nowhere" } }
        ]);
        database.MongoDatabase.GetCollection<BsonDocument>(people).InsertMany([
            new BsonDocument { { "_id", 1 }, { "Name", "Ann" } }
        ]);
        database.MongoDatabase.GetCollection<BsonDocument>(phones).InsertMany([
            new BsonDocument { { "_id", 1 }, { "PersonId", 1 }, { "Number", "111" } },
            new BsonDocument { { "_id", 2 }, { "PersonId", 99 }, { "Number", "222" } }
        ]);

        return new PrincipalDbContext(database, customers, addresses, people, phones, mode);
    }

    public class Customer
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public Address? Address { get; set; }
    }

    public class Address
    {
        public int Id { get; set; }
        public int CustomerId { get; set; }
        public string City { get; set; } = "";
        public Customer? Customer { get; set; }
    }

    public class Person
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public List<Phone> Phones { get; set; } = [];
    }

    public class Phone
    {
        public int Id { get; set; }
        public int? PersonId { get; set; }
        public string Number { get; set; } = "";
        public Person? Person { get; set; }
    }

    public class PrincipalDbContext : DbContext
    {
        private readonly string _customers;
        private readonly string _addresses;
        private readonly string _people;
        private readonly string _phones;

        public DbSet<Customer> Customers { get; set; } = null!;
        public DbSet<Address> Addresses { get; set; } = null!;
        public DbSet<Person> People { get; set; } = null!;
        public DbSet<Phone> Phones { get; set; } = null!;

        public PrincipalDbContext(
            TemporaryDatabaseFixture db, string customers, string addresses, string people, string phones,
            MongoQueryMode mode)
            : base(BuildOptions(db, mode))
        {
            _customers = customers;
            _addresses = addresses;
            _people = people;
            _phones = phones;
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Customer>().ToCollection(_customers);
            modelBuilder.Entity<Address>().ToCollection(_addresses);
            modelBuilder.Entity<Customer>()
                .HasOne(c => c.Address).WithOne(a => a.Customer).HasForeignKey<Address>(a => a.CustomerId);

            modelBuilder.Entity<Person>().ToCollection(_people);
            modelBuilder.Entity<Phone>().ToCollection(_phones);
            modelBuilder.Entity<Phone>()
                .HasOne(p => p.Person).WithMany(p => p.Phones).HasForeignKey(p => p.PersonId).IsRequired(false);
        }

        private static DbContextOptions<PrincipalDbContext> BuildOptions(TemporaryDatabaseFixture db, MongoQueryMode mode)
            => new DbContextOptionsBuilder<PrincipalDbContext>()
                .UseMongoDB(db.Client, db.MongoDatabase.DatabaseNamespace.DatabaseName, o => o.UseQueryMode(mode))
                .ReplaceService<IModelCacheKeyFactory, IgnoreCacheKeyFactory>()
                .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
                .Options;

        sealed class IgnoreCacheKeyFactory : IModelCacheKeyFactory
        {
            private static int _count;
            public object Create(DbContext context, bool designTime) => Interlocked.Increment(ref _count);
        }
    }
}
