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

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Mapping;

[XUnitCollection("MappingTests")]
public class DiscriminatorTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    [Fact]
    public void Sets_type_discriminator_property_and_type_for_read_and_write()
    {
        var collection = database.CreateCollection<BaseEntity>();
        SetupTestData(SingleEntityDbContext.Create(collection, ConfigureModel));

        using var db = SingleEntityDbContext.Create(collection, ConfigureModel);
        var entities = db.Entities.ToList();
        Assert.Single(entities, e => e.EntityType == "Client" && e.GetType() == typeof(Customer));
        Assert.Single(entities, e => e.EntityType == "SubClient" && e.GetType() == typeof(SubCustomer));
        Assert.Single(entities, e => e.EntityType == "Order" && e.GetType() == typeof(Order));
        Assert.Single(entities, e => e.EntityType == "Supplier" && e.GetType() == typeof(Supplier));
        Assert.Single(entities, e => e.EntityType == "Contact" && e.GetType() == typeof(Contact));
        Assert.Single(entities, e => e.EntityType == "BaseEntity" && e.GetType() == typeof(BaseEntity));
    }

    [Fact]
    public void Can_configure_type_discriminator_with_int()
    {
        var collection = database.CreateCollection<GuidKeyedEntity>();
        var configuration = (ModelBuilder mb) =>
        {
            mb.Entity<GuidKeyedEntity>(e =>
            {
                e.HasDiscriminator(g => g.SubType)
                    .HasValue<KeyedCustomer>(1)
                    .HasValue<KeyedOrder>(2);
            });
        };

        {
            using var db = SingleEntityDbContext.Create(collection, configuration);
            db.Add(new KeyedCustomer {Name = "Customer 1"});
            db.Add(new KeyedOrder {OrderReference = "Order 1"});
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection, configuration);
            var customer = db.Entities.First(e => e is KeyedCustomer);
            Assert.Equal("Customer 1", Assert.IsType<KeyedCustomer>(customer).Name);
        }
    }

    [Fact]
    public void Can_configure_type_discriminator_element_name()
    {
        var configuration = (ModelBuilder mb) =>
        {
            ConfigureModel(mb);
            mb.Entity<BaseEntity>().Property(e => e.EntityType).HasElementName("_entityType");
        };

        var collection = database.CreateCollection<BaseEntity>();
        SetupTestData(SingleEntityDbContext.Create(collection, configuration));

        using var db = SingleEntityDbContext.Create(collection, configuration);
        var customer = db.Entities.First(e => e is Customer);
        Assert.Equal("Customer 1", Assert.IsType<Customer>(customer).Name);
    }

    [Fact]
    public void Returns_correct_values_when_property_name_shared_between_entities()
    {
        var collection = database.CreateCollection<BaseEntity>();
        SetupTestData(SingleEntityDbContext.Create(collection, ConfigureModel));

        using var db = SingleEntityDbContext.Create(collection, ConfigureModel);
        var entities = db.Entities.Where(e => e is Supplier || e is Contact).ToList();

        Assert.Single(entities, e => e is Supplier {Name: "Supplier 1"});
        Assert.Single(entities, e => e is Contact {Name: "Contact 1"});
        Assert.Equal(2, entities.Count);
    }

    [Fact]
    public void Returns_correct_entity_where_is_type_query()
    {
        var collection = database.CreateCollection<BaseEntity>();
        SetupTestData(SingleEntityDbContext.Create(collection, ConfigureModel));

        using var db = SingleEntityDbContext.Create(collection, ConfigureModel);
        var entities = db.Entities.Where(e => e is Supplier).ToList();
        Assert.Single(entities, e => e is Supplier {Name: "Supplier 1"});

        var firstOrder = db.Entities.First(e => e is Order);
        Assert.Equal("Order 1", Assert.IsType<Order>(firstOrder).OrderReference);
    }


    [Fact]
    public void Returns_correct_entity_where_GetType_query()
    {
        var collection = database.CreateCollection<BaseEntity>();
        SetupTestData(SingleEntityDbContext.Create(collection, ConfigureModel));

        using var db = SingleEntityDbContext.Create(collection, ConfigureModel);
        var entities = db.Entities.Where(e => e.GetType() == typeof(Order)).ToList();
        Assert.Single(entities, e => e is Order {OrderReference: "Order 1"});
    }

    [Fact]
    public void Returns_correct_values_when_navigation_shared_between_entities()
    {
        var collection = database.CreateCollection<BaseEntity>();
        var expectedProducts = new List<string> {"Product 3", "Product 4", "Product 5"};

        {
            using var db = SingleEntityDbContext.Create(collection, ConfigureModel);
            db.Add(new Customer {Name = "Customer 1", ShippingAddress = "123 Main St"});
            db.Add(new Supplier {Name = "Supplier 1", Products = ["Product 1", "Product 2"]});
            db.Add(new Order {OrderReference = "Order 1"});
            db.Add(new OrderWithProducts {OrderReference = "Order 2", Products = expectedProducts});
            db.Add(new BaseEntity());
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection, ConfigureModel);
            var entities = db.Entities.Where(e => e is Order || e is OrderWithProducts).ToList();

            Assert.Single(entities, e => e is Order {OrderReference: "Order 1"});
            var order = Assert.IsType<OrderWithProducts>(Assert.Single(entities,
                e => e is OrderWithProducts {OrderReference: "Order 2"}));
            Assert.Equal(expectedProducts, order.Products);
            Assert.Equal(2, entities.Count);
        }
    }

    [Fact]
    public void Returns_correct_entity_with_OfType_query()
    {
        var collection = database.CreateCollection<BaseEntity>();
        SetupTestData(SingleEntityDbContext.Create(collection, ConfigureModel));

        using var db = SingleEntityDbContext.Create(collection, ConfigureModel);
        var entities = db.Entities.OfType<Customer>().ToList();
        Assert.Single(entities, e => e.Name == "Customer 1");
    }

    [Fact]
    public void Returns_correct_entities_with_mixed_query()
    {
        var collection = database.CreateCollection<BaseEntity>();
        SetupTestData(SingleEntityDbContext.Create(collection, ConfigureModel));

        using var db = SingleEntityDbContext.Create(collection, ConfigureModel);
        var entities = db.Entities.OfType<BaseEntity>().Where(e => e is Customer || e.GetType() == typeof(Order)).ToList();
        Assert.Equal(3, entities.Count);
        Assert.Single(entities, e => e is Customer {Name: "Customer 1"});
        Assert.Single(entities, e => e is SubCustomer {Name: "SubCustomer 1"});
        Assert.Single(entities, e => e is Order {OrderReference: "Order 1"});
    }

    [Fact]
    public void TablePerType_throws_NotSupportedException()
    {
        var collection = database.CreateCollection<BaseEntity>();

        using var db = SingleEntityDbContext.Create(collection, mb =>
        {
            ConfigureModel(mb);
            mb.Entity<BaseEntity>().UseTptMappingStrategy();
        });

        Assert.Throws<NotSupportedException>(() => SetupTestData(db));
    }

    [Fact]
    public void TablePerConcrete_throws_NotSupportedException()
    {
        var collection = database.CreateCollection<BaseEntity>();

        using var db = SingleEntityDbContext.Create(collection, mb =>
        {
            ConfigureModel(mb);
            mb.Entity<BaseEntity>().UseTpcMappingStrategy();
        });

        Assert.Throws<NotSupportedException>(() => SetupTestData(db));
    }

    private static void ConfigureModel(ModelBuilder mb)
    {
        mb.Entity<BaseEntity>()
            .HasDiscriminator(e => e.EntityType)
            .HasValue<Customer>("Client")
            .HasValue<SubCustomer>("SubClient")
            .HasValue<Supplier>("Supplier")
            .HasValue<Order>("Order")
            .HasValue<OrderWithProducts>("OrderEx")
            .HasValue<Contact>("Contact");
    }

    private static void SetupTestData(DbContext db)
    {
        db.Add(new Customer {Name = "Customer 1", ShippingAddress = "123 Main St"});
        db.Add(new Supplier {Name = "Supplier 1", Products = ["Product 1", "Product 2"]});
        db.Add(new SubCustomer {Name = "SubCustomer 1", ShippingAddress = "3.5 Inch Dr.", AccountingCode = 123});
        db.Add(new Order {OrderReference = "Order 1"});
        db.Add(new Contact {Name = "Contact 1"});
        db.Add(new BaseEntity());
        db.SaveChanges();
        db.Dispose();
    }

    class BaseEntity
    {
        public ObjectId _id { get; set; }
        public string EntityType { get; set; }
    }

    class Customer : BaseEntity
    {
        public string Name { get; set; }
        public string ShippingAddress { get; set; }
    }

    class SubCustomer : Customer
    {
        public int AccountingCode { get; set; }
    }

    class Supplier : BaseEntity
    {
        public string Name { get; set; }
        public List<string> Products { get; set; }
    }

    class Order : BaseEntity
    {
        public string OrderReference { get; set; }
    }

    class OrderWithProducts : Order
    {
        public List<string> Products { get; set; }
    }

    class Contact : BaseEntity
    {
        public string Name { get; set; }
    }

    abstract class GuidKeyedEntity
    {
        public Guid Id { get; set; }
        public int SubType { get; set; }
    }

    class KeyedCustomer : GuidKeyedEntity
    {
        public string Name { get; set; }
    }

    class KeyedOrder : GuidKeyedEntity
    {
        public string OrderReference { get; set; }
    }
}
