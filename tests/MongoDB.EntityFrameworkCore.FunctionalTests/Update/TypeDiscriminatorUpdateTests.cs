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
using MongoDB.Bson;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Update;

[XUnitCollection("UpdateTests")]
public class TypeDiscriminatorUpdateTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    [Fact]
    public void Entity_type_sets_type_discriminator_property_read_write()
    {
        var collection = database.CreateCollection<Entity>();

        {
            var db = SingleEntityDbContext.Create(collection, SetupDiscriminators);
            db.Entities.Add(new Customer {Name = "Customer 1", ShippingAddress = "123 Main St"});
            db.Entities.Add(new Supplier {Name = "Supplier 1", Products = ["Product 1", "Product 2"]});
            db.Entities.Add(new Order {OrderReference = "Order 1"});
            db.Entities.Add(new Entity());
            db.SaveChanges();
        }

        {
            var db = SingleEntityDbContext.Create(collection, SetupDiscriminators);
            var entities = db.Entities.ToList();
            Assert.Equal(4, entities.Count);
        }
    }

    [Fact]
    public void Entity_type_is_type_query()
    {
        var collection = database.CreateCollection<Entity>();

        {
            var db = SingleEntityDbContext.Create(collection, SetupDiscriminators);
            db.Entities.Add(new Customer {Name = "Customer 1", ShippingAddress = "123 Main St"});
            db.Entities.Add(new Supplier {Name = "Supplier 1", Products = ["Product 1", "Product 2"]});
            db.Entities.Add(new Order {OrderReference = "Order 1"});
            db.Entities.Add(new Entity());
            db.SaveChanges();
        }

        {
            var db = SingleEntityDbContext.Create(collection, SetupDiscriminators);
            var entities = db.Entities.Where(e => e is Supplier).ToList();
            Assert.Single(entities, e => ((Supplier)e).Name == "Supplier 1");
        }
    }

    [Fact]
    public void Entity_type_of_type_query()
    {
        var collection = database.CreateCollection<Entity>();

        {
            var db = SingleEntityDbContext.Create(collection, SetupDiscriminators);
            db.Entities.Add(new Customer {Name = "Customer 1", ShippingAddress = "123 Main St"});
            db.Entities.Add(new Supplier {Name = "Supplier 1", Products = ["Product 1", "Product 2"]});
            db.Entities.Add(new Order {OrderReference = "Order 1"});
            db.Entities.Add(new Entity());
            db.SaveChanges();
        }

        {
            var db = SingleEntityDbContext.Create(collection, SetupDiscriminators);
            var entities = db.Entities.OfType<Customer>().ToList();
            Assert.Single(entities, e => e.Name == "Customer 1");
        }
    }


    private static void SetupDiscriminators(ModelBuilder mb)
    {
        var discriminator = mb.Entity<Entity>().HasDiscriminator(e => e.EntityType);
        discriminator.HasValue<Customer>("Client");
        discriminator.HasValue<Supplier>("Supplier");
        discriminator.HasValue<Order>("Order");
    }

    class Entity
    {
        public ObjectId _id { get; set; }
        public string EntityType { get; set; }
    }

    class Customer : Entity
    {
        public string Name { get; set; }
        public string ShippingAddress { get; set; }
    }

    class Supplier : Entity
    {
        public string Name { get; set; }
        public List<string> Products { get; set; }
    }

    class Order : Entity
    {
        public string OrderReference { get; set; }
    }
}
