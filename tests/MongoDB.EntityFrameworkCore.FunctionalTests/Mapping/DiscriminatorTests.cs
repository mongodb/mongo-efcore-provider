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
using MongoDB.EntityFrameworkCore.Extensions;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Mapping;

[XUnitCollection("MappingTests")]
public class DiscriminatorTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    [Fact]
    public void Uses_real_property_type_discriminator_property_for_read_and_write()
    {
        var collection = database.CreateCollection<BaseEntity>();
        SetupTestData(SingleEntityDbContext.Create(collection, RealPropertyConfiguredModel));

        using var db = SingleEntityDbContext.Create(collection, RealPropertyConfiguredModel);
        var entities = db.Entities.ToList();
        Assert.Single(entities, e => e.EntityType == "Client" && e.GetType() == typeof(Customer) && e.Status == Status.Active
                                     && e is Customer
                                     {
                                         Name: "Customer 1"
                                     });
        Assert.Single(entities, e => e.EntityType == "Client" && e.GetType() == typeof(Customer) && e.Status == Status.Active
                                     && e is Customer
                                     {
                                         Name: "Customer 2"
                                     });
        Assert.Single(entities, e => e.EntityType == "Client" && e.GetType() == typeof(Customer) && e.Status == Status.Inactive
                                     && e is Customer
                                     {
                                         Name: "Customer 1"
                                     });

        Assert.Single(entities, e => e.EntityType == "SubClient" && e.GetType() == typeof(SubCustomer));
        Assert.Single(entities, e => e.EntityType == "Order" && e.GetType() == typeof(Order));
        Assert.Single(entities, e => e.EntityType == "Supplier" && e.GetType() == typeof(Supplier));
        Assert.Single(entities, e => e.EntityType == "Contact" && e.GetType() == typeof(Contact));
        Assert.Single(entities, e => e.EntityType == "BaseEntity" && e.GetType() == typeof(BaseEntity));
    }

    [Fact]
    public void Uses_shadow_property_type_discriminator_for_read_and_write()
    {
        var collection = database.CreateCollection<BaseEntity>();
        SetupTestData(SingleEntityDbContext.Create(collection, ShadowPropertyConfiguredModel));

        using var db = SingleEntityDbContext.Create(collection, ShadowPropertyConfiguredModel);
        var entities = db.Entities.ToList();
        Assert.Single(entities, e => e.Status == Status.Active && e is Customer {Name: "Customer 1"});
        Assert.Single(entities, e => e.Status == Status.Active && e is Customer {Name: "Customer 2"});
        Assert.Single(entities, e => e.Status == Status.Inactive && e is Customer {Name: "Customer 1"});
        Assert.Single(entities, e => e is SubCustomer {Name: "SubCustomer 1"});
        Assert.Single(entities, e => e is Order {OrderReference: "Order 1"});
        Assert.Single(entities, e => e is Supplier {Name: "Supplier 1"});
        Assert.Single(entities, e => e is Contact {Name: "Contact 1"});
        Assert.Single(entities, e => e.GetType() == typeof(BaseEntity));
        Assert.All(entities, e => Assert.Null(e.EntityType));
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
            RealPropertyConfiguredModel(mb);
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
        SetupTestData(SingleEntityDbContext.Create(collection, RealPropertyConfiguredModel));

        using var db = SingleEntityDbContext.Create(collection, RealPropertyConfiguredModel);
        var entities = db.Entities.Where(e => e is Supplier || e is Contact).ToList();

        Assert.Single(entities, e => e is Supplier {Name: "Supplier 1"});
        Assert.Single(entities, e => e is Contact {Name: "Contact 1"});
        Assert.Equal(2, entities.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Returns_correct_entity_where_is_type_query(bool useShadowProperty)
    {
        Action<ModelBuilder> configuration = useShadowProperty
            ? ShadowPropertyConfiguredModel
            : RealPropertyConfiguredModel;

        var collection = database.CreateCollection<BaseEntity>(null, useShadowProperty);
        SetupTestData(SingleEntityDbContext.Create(collection, configuration));

        using var db = SingleEntityDbContext.Create(collection, configuration);
        var firstSupplier = db.Entities.First(e => e is Supplier);
        Assert.Equal("Supplier 1", Assert.IsType<Supplier>(firstSupplier).Name);

        var orders = db.Entities.Where(e => e is Order).ToList();
        Assert.Single(orders, o => o is Order {OrderReference: "Order 1"});
        Assert.Single(orders, o => o is OrderWithProducts {OrderReference: "Order 2"});
        Assert.Equal(2, orders.Count);

        Action<object> assertion = useShadowProperty ? Assert.Null : Assert.NotNull;
        Assert.All(db.Entities, e => assertion(e.EntityType!));
    }

    [Fact]
    public void Returns_correct_entity_where_GetType_query()
    {
        var collection = database.CreateCollection<BaseEntity>();
        SetupTestData(SingleEntityDbContext.Create(collection, RealPropertyConfiguredModel));

        using var db = SingleEntityDbContext.Create(collection, RealPropertyConfiguredModel);
        var entities = db.Entities.Where(e => e.GetType() == typeof(Order)).ToList();
        Assert.Single(entities, e => e is Order {OrderReference: "Order 1"});
        Assert.Single(entities);
    }

    [Fact]
    public void Returns_correct_values_when_navigation_shared_between_entities()
    {
        var collection = database.CreateCollection<BaseEntity>();
        var expectedProducts = new List<string> {"Product 3", "Product 4", "Product 5"};

        {
            using var db = SingleEntityDbContext.Create(collection, RealPropertyConfiguredModel);
            db.Add(new Customer {Name = "Customer 1", ShippingAddress = "123 Main St"});
            db.Add(new Supplier {Name = "Supplier 1", Products = ["Product 1", "Product 2"]});
            db.Add(new Order {OrderReference = "Order 1"});
            db.Add(new OrderWithProducts {OrderReference = "Order 2", Products = expectedProducts});
            db.Add(new BaseEntity());
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection, RealPropertyConfiguredModel);
            var entities = db.Entities.Where(e => e is Order || e is OrderWithProducts).ToList();

            Assert.Single(entities, e => e is Order {OrderReference: "Order 1"});
            var order = Assert.IsType<OrderWithProducts>(Assert.Single(entities,
                e => e is OrderWithProducts {OrderReference: "Order 2"}));
            Assert.Equal(expectedProducts, order.Products);
            Assert.Equal(2, entities.Count);
        }
    }

    [Fact]
    public void Enables_multiple_independent_hierarchies_with_different_collections()
    {
        var options = new DbContextOptionsBuilder<MultiEntityContext>()
            .UseMongoDB(database.Client, database.MongoDatabase.DatabaseNamespace.DatabaseName)
            .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
            .Options;

        {
            using var db = new MultiEntityContext(options);
            db.Add(new Customer {Name = "Customer 1", ShippingAddress = "123 Main St"});
            db.Add(new Supplier {Name = "Supplier 1", Products = ["Product 1", "Product 2"]});
            db.Add(new SubCustomer {Name = "SubCustomer 1", ShippingAddress = "3.5 Inch Dr.", AccountingCode = 123});
            db.Add(new Order {OrderReference = "Order 1"});
            db.Add(new OrderWithProducts {OrderReference = "Order 2", Products = ["abc", "123"]});
            db.SaveChanges();
        }

        {
            using var db = new MultiEntityContext(options);
            var customers = db.Customers.ToList();
            Assert.Single(customers, c => c.Name == "Customer 1");
            Assert.Single(customers, c => c is SubCustomer {Name: "SubCustomer 1"});
            Assert.Equal(2, customers.Count);

            var suppliers = db.Suppliers.ToList();
            Assert.Single(suppliers, s => s.Name == "Supplier 1");
            Assert.Single(suppliers);

            var orders = db.Orders.ToList();
            Assert.Single(orders, o => o.OrderReference == "Order 1");
            Assert.Single(orders, o => o is OrderWithProducts {OrderReference: "Order 2"});
            Assert.Equal(2, orders.Count);
        }
    }

    [Fact]
    public void Returns_correct_entity_with_OfType_query()
    {
        var collection = database.CreateCollection<BaseEntity>();
        SetupTestData(SingleEntityDbContext.Create(collection, RealPropertyConfiguredModel));

        using var db = SingleEntityDbContext.Create(collection, RealPropertyConfiguredModel);
        var entities = db.Entities.OfType<Customer>().ToList();
        Assert.Single(entities, e => e is {Name: "Customer 1", Status: Status.Active});
        Assert.Single(entities, e => e is {Name: "Customer 2", Status: Status.Active});
        Assert.Single(entities, e => e is {Name: "Customer 1", Status: Status.Inactive});
        Assert.Single(entities, e => e.Name == "SubCustomer 1");
        Assert.Equal(4, entities.Count);
    }

    [Fact]
    public void Returns_correct_entities_with_mixed_query()
    {
        var collection = database.CreateCollection<BaseEntity>();
        SetupTestData(SingleEntityDbContext.Create(collection, RealPropertyConfiguredModel));

        using var db = SingleEntityDbContext.Create(collection, RealPropertyConfiguredModel);
        var entities = db.Entities.OfType<BaseEntity>().Where(e => e is Customer || e.GetType() == typeof(Order)).ToList();
        Assert.Equal(5, entities.Count);
        Assert.Single(entities, e => e is Customer {Name: "Customer 1", Status: Status.Active});
        Assert.Single(entities, e => e is Customer {Name: "Customer 1", Status: Status.Inactive});
        Assert.Single(entities, e => e is Customer {Name: "Customer 2", Status: Status.Active});
        Assert.Single(entities, e => e is SubCustomer {Name: "SubCustomer 1"});
        Assert.Single(entities, e => e is Order {OrderReference: "Order 1"});
    }

    [Fact]
    public void OfType_does_not_break_entity_serializer_association()
    {
        var collection = database.CreateCollection<BaseEntity>();
        SetupTestData(SingleEntityDbContext.Create(collection, RealPropertyConfiguredModel));

        using var db = SingleEntityDbContext.Create(collection, RealPropertyConfiguredModel);

        var allActiveCustomers = db.Entities.OfType<Customer>().Where(e => e.Status == Status.Active);
        Assert.All(allActiveCustomers, f => Assert.Equal(Status.Active, f.Status));

        var activeCustomer1 = db.Entities.Where(e => e.Status == Status.Active).OfType<Customer>()
            .Single(c => c.Name == "Customer 1");
        Assert.Equal("Customer 1", activeCustomer1.Name);
        Assert.Equal(Status.Active, activeCustomer1.Status);
    }

#if EF8 // TPC/TPT methods were moved to relational in EF9

    [Fact]
    public void TablePerType_throws_NotSupportedException()
    {
        var collection = database.CreateCollection<BaseEntity>();

        using var db = SingleEntityDbContext.Create(collection, mb =>
        {
            RealPropertyConfiguredModel(mb);
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
            RealPropertyConfiguredModel(mb);
            mb.Entity<BaseEntity>().UseTpcMappingStrategy();
        });

        Assert.Throws<NotSupportedException>(() => SetupTestData(db));
    }

#endif

    private static void RealPropertyConfiguredModel(ModelBuilder mb)
    {
        mb.Entity<BaseEntity>()
            .HasDiscriminator(e => e.EntityType)
            .HasValue<Customer>("Client")
            .HasValue<SubCustomer>("SubClient")
            .HasValue<Supplier>("Supplier")
            .HasValue<Order>("Order")
            .HasValue<OrderWithProducts>("OrderEx")
            .HasValue<Contact>("Contact");
        mb.Entity<BaseEntity>().Property(e => e.Status).HasConversion<string>(e => e.ToString(), s => Enum.Parse<Status>(s));
    }

    private static void ShadowPropertyConfiguredModel(ModelBuilder mb)
    {
        mb.Entity<BaseEntity>()
            .HasDiscriminator()
            .HasValue<Customer>("Client")
            .HasValue<SubCustomer>("SubClient")
            .HasValue<Supplier>("Supplier")
            .HasValue<Order>("Order")
            .HasValue<OrderWithProducts>("OrderEx")
            .HasValue<Contact>("Contact");
        mb.Entity<BaseEntity>().Property(e => e.Status).HasConversion<string>(e => e.ToString(), s => Enum.Parse<Status>(s));
    }

    private static void SetupTestData(DbContext db)
    {
        db.Add(new Customer {Name = "Customer 1", ShippingAddress = "123 Main St", Status = Status.Active});
        db.Add(new Customer {Name = "Customer 1", ShippingAddress = "123 Main St", Status = Status.Inactive});
        db.Add(new Customer {Name = "Customer 2", ShippingAddress = "123 Main St", Status = Status.Active});
        db.Add(new Supplier {Name = "Supplier 1", Products = ["Product 1", "Product 2"]});
        db.Add(new SubCustomer {Name = "SubCustomer 1", ShippingAddress = "3.5 Inch Dr.", AccountingCode = 123});
        db.Add(new Order {OrderReference = "Order 1"});
        db.Add(new OrderWithProducts {OrderReference = "Order 2", Products = ["abc", "123"]});
        db.Add(new Contact {Name = "Contact 1"});
        db.Add(new BaseEntity());
        db.SaveChanges();
        db.Dispose();
    }

    enum Status
    {
        Active,
        Inactive,
        Unused
    }

    class BaseEntity
    {
        public ObjectId _id { get; set; }
        public string? EntityType { get; set; }
        public Status Status { get; set; } = Status.Inactive;
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

    class MultiEntityContext(DbContextOptions options)
        : DbContext(options)
    {
        public DbSet<Order> Orders { get; set; }
        public DbSet<Customer> Customers { get; set; }
        public DbSet<Supplier> Suppliers { get; set; }

        protected override void OnModelCreating(ModelBuilder mb)
        {
            base.OnModelCreating(mb);
            mb.Entity<Customer>()
                .ToCollection("customer-docs")
                .HasDiscriminator(o => o.EntityType)
                .HasValue<Customer>("c")
                .HasValue<SubCustomer>("s");
            mb.Entity<Order>()
                .ToCollection("order-docs")
                .HasDiscriminator(o => o.EntityType)
                .HasValue<Order>("order")
                .HasValue<OrderWithProducts>("product-order");
            mb.Entity<Supplier>()
                .Ignore(s => s.EntityType)
                .ToCollection("supplier-docs");
        }
    }
}
