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
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.Extensions;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Update;

[XUnitCollection("UpdateTests")]
public class SaveChangesTransactionTests(TemporaryDatabaseFixture tempDatabase)
    : IClassFixture<TemporaryDatabaseFixture>
{
    class TextEntity
    {
        public ObjectId _id { get; set; }
        public string text { get; set; }
    }

    [Theory]
    [InlineData(AutoTransactionBehavior.WhenNeeded)]
    [InlineData(AutoTransactionBehavior.Never)]
    public void SaveChanges_behavior_on_driver_exception(AutoTransactionBehavior transactionBehavior)
    {
        var collection =
            tempDatabase.CreateCollection<TextEntity>("SaveChanges_DriverException" + transactionBehavior);
        var idToDuplicate = ObjectId.GenerateNewId();
        var idToDelete = ObjectId.GenerateNewId();

        {
            // Setup two entities
            var db = SingleEntityDbContext.Create(collection);
            db.AddRange(
                new TextEntity {_id = idToDuplicate, text = "Original"},
                new TextEntity {_id = idToDelete, text = "Delete"});
            db.SaveChanges();
        }

        {
            // Attempt to delete one and duplicate the other
            var db = SingleEntityDbContext.Create(collection);
            db.Database.AutoTransactionBehavior = transactionBehavior;
            var toDelete = db.Entities.First(e => e._id == idToDelete);
            db.Entities.AddRange(
                new TextEntity {_id = ObjectId.GenerateNewId(), text = "Insert"},
                new TextEntity {_id = idToDuplicate, text = "Duplicate"});
            db.Entities.Remove(toDelete);
            Assert.Throws<MongoBulkWriteException<BsonDocument>>(() => db.SaveChanges());
        }

        {
            // Ensure database is in original state
            var db = SingleEntityDbContext.Create(collection);
            if (transactionBehavior == AutoTransactionBehavior.Never)
            {
                Assert.Equal(3, db.Entities.Count());
            }
            else
            {
                Assert.Equal(2, db.Entities.Count());
                Assert.Equal("Original", db.Entities.First(e => e._id == idToDuplicate).text);
                Assert.Equal("Delete", db.Entities.First(e => e._id == idToDelete).text);
            }
        }
    }

    [Theory]
    [InlineData(AutoTransactionBehavior.WhenNeeded)]
    [InlineData(AutoTransactionBehavior.Never)]
    public async Task SaveChangesAsync_behavior_on_driver_exception(AutoTransactionBehavior transactionBehavior)
    {
        var collection =
            tempDatabase.CreateCollection<TextEntity>("SaveChangesAsync_DriverException" + transactionBehavior);
        var idToDuplicate = ObjectId.GenerateNewId();
        var idToDelete = ObjectId.GenerateNewId();

        {
            // Setup two entities
            var db = SingleEntityDbContext.Create(collection);
            await db.AddRangeAsync(
                new TextEntity {_id = idToDuplicate, text = "Original"},
                new TextEntity {_id = idToDelete, text = "Delete"});
            await db.SaveChangesAsync();
        }

        {
            // Attempt to delete one and duplicate the other
            var db = SingleEntityDbContext.Create(collection);
            db.Database.AutoTransactionBehavior = transactionBehavior;
            var toDelete = await db.Entities.FirstAsync(e => e._id == idToDelete);
            await db.Entities.AddRangeAsync(
                new TextEntity {_id = ObjectId.GenerateNewId(), text = "Insert"},
                new TextEntity {_id = idToDuplicate, text = "Duplicate"});
            db.Entities.Remove(toDelete);
            await Assert.ThrowsAsync<MongoBulkWriteException<BsonDocument>>(() => db.SaveChangesAsync());
        }

        {
            // Ensure database is in original state
            var db = SingleEntityDbContext.Create(collection);

            if (transactionBehavior == AutoTransactionBehavior.Never)
            {
                Assert.Equal(3, db.Entities.Count());
            }
            else
            {
                Assert.Equal(2, db.Entities.Count());
                Assert.Equal("Original", db.Entities.First(e => e._id == idToDuplicate).text);
                Assert.Equal("Delete", db.Entities.First(e => e._id == idToDelete).text);
            }
        }
    }

    [Theory]
    [InlineData(AutoTransactionBehavior.WhenNeeded)]
    [InlineData(AutoTransactionBehavior.Never)]
    public void SaveChanges_behavior_on_DbUpdateConcurrencyException(AutoTransactionBehavior transactionBehavior)
    {
        var collection =
            tempDatabase.CreateCollection<TextEntity>("SaveChanges_DbConcurrencyException" + transactionBehavior);
        var idToDelete = ObjectId.GenerateNewId();

        {
            var db = SingleEntityDbContext.Create(collection);
            db.AddRange(new TextEntity {_id = idToDelete, text = "Initial"});
            db.SaveChanges();
        }

        {
            var db1 = SingleEntityDbContext.Create(collection);
            var copy1 = db1.Entities.First(e => e._id == idToDelete);

            var db2 = SingleEntityDbContext.Create(collection);
            db2.Database.AutoTransactionBehavior = transactionBehavior;
            db2.Entities.Add(new TextEntity {text = "Should I rollback?"});
            var copy2 = db2.Entities.First(e => e._id == idToDelete);

            db1.Entities.Remove(copy1);
            db1.SaveChanges();

            copy2.text = "Change on 2";
            Assert.Throws<DbUpdateConcurrencyException>(() => db2.SaveChanges());
        }

        {
            var db = SingleEntityDbContext.Create(collection);
            Assert.Equal(transactionBehavior == AutoTransactionBehavior.Never ? 1 : 0, db.Entities.Count());
        }
    }

    [Theory]
    [InlineData(AutoTransactionBehavior.WhenNeeded)]
    [InlineData(AutoTransactionBehavior.Never)]
    public async Task SaveChangesAsync_behavior_on_DbUpdateConcurrencyException(AutoTransactionBehavior transactionBehavior)
    {
        var collection =
            tempDatabase.CreateCollection<TextEntity>("SaveChangesAsync_DbConcurrencyException" + transactionBehavior);
        var idToDelete = ObjectId.GenerateNewId();

        {
            var db = SingleEntityDbContext.Create(collection);
            await db.AddRangeAsync(new TextEntity {_id = idToDelete, text = "Initial"});
            await db.SaveChangesAsync();
        }

        {
            var db1 = SingleEntityDbContext.Create(collection);
            var copy1 = await db1.Entities.FirstAsync(e => e._id == idToDelete);

            var db2 = SingleEntityDbContext.Create(collection);
            db2.Database.AutoTransactionBehavior = transactionBehavior;
            db2.Entities.Add(new TextEntity {text = "Should I rollback?"});
            var copy2 = await db2.Entities.FirstAsync(e => e._id == idToDelete);

            db1.Entities.Remove(copy1);
            await db1.SaveChangesAsync();

            copy2.text = "Change on 2";
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => db2.SaveChangesAsync());
        }

        {
            var db = SingleEntityDbContext.Create(collection);
            Assert.Equal(transactionBehavior == AutoTransactionBehavior.Never ? 1 : 0, db.Entities.Count());
        }
    }

    class NamedEntity
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public int RowVersion { get; set; }
    }

    class Customer : NamedEntity
    {
        public string SalesRegion { get; set; }
    }

    class Supplier : NamedEntity
    {
        public string Address { get; set; }
    }

    class Employee : NamedEntity
    {
        public string JobTitle { get; set; }
    }

    class MultiEntityDbContext(IMongoDatabase database) : DbContext
    {
        public DbSet<Customer> Customers { get; set; }
        public DbSet<Supplier> Suppliers { get; set; }
        public DbSet<Employee> Employees { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Customer>().ToCollection("customers");
            modelBuilder.Entity<Supplier>().ToCollection("suppliers");
            modelBuilder.Entity<Employee>().ToCollection("employees");
        }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            optionsBuilder
                .UseMongoDB(database.Client, database.DatabaseNamespace.DatabaseName)
                .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
        }
    }

    class ConcurrentMultiEntityDbContext(IMongoDatabase database) : MultiEntityDbContext(database)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<Customer>().Property(p => p.RowVersion).IsRowVersion();
            modelBuilder.Entity<Employee>().Property(p => p.RowVersion).IsRowVersion();
            modelBuilder.Entity<Supplier>().Property(p => p.RowVersion).IsRowVersion();
        }
    }

    [Fact]
    public void SaveChanges_multi_collection_rolls_back_on_driver_exception()
    {
        using var tempDatabaseFixture = new TemporaryDatabaseFixture();
        var e1 = new Employee {Id = "E1", Name = "Roy Williams", JobTitle = "Office Manager"};

        {
            using var db = new MultiEntityDbContext(tempDatabaseFixture.MongoDatabase);
            db.Database.EnsureCreated();
            db.Add(new Supplier {Id = "S1", Name = "Friendly Corp.", Address = "123 Main St."});
            db.Add(e1);
            db.SaveChanges();
        }

        {
            using var db = new MultiEntityDbContext(tempDatabaseFixture.MongoDatabase);
            db.Add(new Employee {Id = "E2", Name = "Jay Smith", JobTitle = "Procurement Manager"});
            db.Add(new Customer {Id = "C1", Name = "A Friend", SalesRegion = "EMEA"});
            db.Add(new Supplier {Id = "S2", Name = "Friendly Services", Address = "345 Main St."});
            db.Add(new Supplier {Id = "S1", Name = "Friendly Industries", Address = "234 Main St."});
            db.RemoveRange(e1);
            Assert.Throws<MongoBulkWriteException<BsonDocument>>(() => db.SaveChanges());
        }

        {
            using var db = new MultiEntityDbContext(tempDatabaseFixture.MongoDatabase);
            Assert.Empty(db.Customers);
            Assert.Equal(1, db.Employees.Count());
            Assert.Equal(1, db.Suppliers.Count());
        }
    }

    [Fact]
    public async Task SaveChangesAsync_multi_collection_rolls_back_on_driver_exception()
    {
        await using var tempDatabaseFixture = new TemporaryDatabaseFixture();
        var e1 = new Employee {Id = "E1", Name = "Roy Williams", JobTitle = "Office Manager"};

        {
            await using var db = new MultiEntityDbContext(tempDatabaseFixture.MongoDatabase);
            await db.Database.EnsureCreatedAsync();
            await db.AddAsync(new Supplier {Id = "S1", Name = "Friendly Corp.", Address = "123 Main St."});
            await db.AddAsync(e1);
            await db.SaveChangesAsync();
        }

        {
            await using var db = new MultiEntityDbContext(tempDatabaseFixture.MongoDatabase);
            await db.AddAsync(new Employee {Id = "E2", Name = "Jay Smith", JobTitle = "Procurement Manager"});
            await db.AddAsync(new Customer {Id = "C1", Name = "A Friend", SalesRegion = "EMEA"});
            await db.AddAsync(new Supplier {Id = "S2", Name = "Friendly Services", Address = "345 Main St."});
            await db.AddAsync(new Supplier {Id = "S1", Name = "Friendly Industries", Address = "234 Main St."});
            db.RemoveRange(e1);
            await Assert.ThrowsAsync<MongoBulkWriteException<BsonDocument>>(() => db.SaveChangesAsync());
        }

        {
            await using var db = new MultiEntityDbContext(tempDatabaseFixture.MongoDatabase);
            Assert.Empty(db.Customers);
            Assert.Equal(1, db.Employees.Count());
            Assert.Equal(1, db.Suppliers.Count());
        }
    }

    [Fact]
    public void SaveChanges_multi_collection_rolls_back_on_DbUpdateConcurrencyException()
    {
        using var tempDatabaseFixture = new TemporaryDatabaseFixture();
        var e1 = new Employee {Id = "E1", Name = "Roy Williams", JobTitle = "Office Manager"};
        var s1 = new Supplier {Id = "S1", Name = "Friendly Corp.", Address = "123 Main St."};

        {
            using var db = new ConcurrentMultiEntityDbContext(tempDatabaseFixture.MongoDatabase);
            db.Database.EnsureCreated();
            db.AddRange(s1, e1);
            db.SaveChanges();
        }

        {
            using var db = new ConcurrentMultiEntityDbContext(tempDatabaseFixture.MongoDatabase);
            db.Suppliers.Single(s => s.Id == "S1").Address = "100 Main St.";
            db.Employees.Single(e => e.Id == "E1").JobTitle = "Senior Office Manager";
            db.SaveChanges();
        }

        {
            using var db = new ConcurrentMultiEntityDbContext(tempDatabaseFixture.MongoDatabase);
            db.Customers.Add(new Customer { Id = "C1", Name = "A Friend", SalesRegion = "EMEA" });
            db.RemoveRange(s1);
            e1.Name = "Roy Williams Jr.";
            Assert.Throws<DbUpdateConcurrencyException>(() => db.SaveChanges());
        }

        {
            using var db = new ConcurrentMultiEntityDbContext(tempDatabaseFixture.MongoDatabase);
            Assert.Empty(db.Customers);
            Assert.Single(db.Employees, e => e is {Id: "E1", Name: "Roy Williams", JobTitle: "Senior Office Manager", RowVersion: 2});
            Assert.Single(db.Suppliers, e => e is {Id: "S1", Name: "Friendly Corp.", Address: "100 Main St.", RowVersion: 2});
        }
    }

    [Fact]
    public async Task SaveChangesAsync_multi_collection_rolls_back_on_DbUpdateConcurrencyException()
    {
        await using var tempDatabaseFixture = new TemporaryDatabaseFixture();
        var e1 = new Employee {Id = "E1", Name = "Roy Williams", JobTitle = "Office Manager"};
        var s1 = new Supplier {Id = "S1", Name = "Friendly Corp.", Address = "123 Main St."};

        {
            await using var db = new ConcurrentMultiEntityDbContext(tempDatabaseFixture.MongoDatabase);
            await db.Database.EnsureCreatedAsync();
            await db.AddRangeAsync(s1, e1);
            await db.SaveChangesAsync();
        }

        {
            await using var db = new ConcurrentMultiEntityDbContext(tempDatabaseFixture.MongoDatabase);
            (await db.Suppliers.SingleAsync(s => s.Id == "S1")).Address = "100 Main St.";
            (await db.Employees.SingleAsync(e => e.Id == "E1")).JobTitle = "Senior Office Manager";
            await db.SaveChangesAsync();
        }

        {
            await using var db = new ConcurrentMultiEntityDbContext(tempDatabaseFixture.MongoDatabase);
            await db.Customers.AddAsync(new Customer { Id = "C1", Name = "A Friend", SalesRegion = "EMEA" });
            db.RemoveRange(s1);
            e1.Name = "Roy Williams Jr.";
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => db.SaveChangesAsync());
        }

        {
            await using var db = new ConcurrentMultiEntityDbContext(tempDatabaseFixture.MongoDatabase);
            Assert.Empty(db.Customers);
            Assert.Single(db.Employees, e => e is {Id: "E1", Name: "Roy Williams", JobTitle: "Senior Office Manager", RowVersion: 2});
            Assert.Single(db.Suppliers, e => e is {Id: "S1", Name: "Friendly Corp.", Address: "100 Main St.", RowVersion: 2});
        }
    }
}

