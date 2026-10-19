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

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.Infrastructure;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// Native <c>OfType&lt;TDerived&gt;()</c> as a discriminator predicate: <c>$eq</c> for a leaf type, <c>$in</c>
/// over the subtree for an intermediate type, across real and shadow discriminator mappings.
/// <see cref="MongoQueryMode.NativeOnly"/> is the "went native" signal, since the MQL matches driver-LINQ's.
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeOfTypeTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    public enum MappingMode
    {
        RealProperty,
        ShadowPropertyWithValues,
        ShadowPropertyDefaults
    }

    [Theory]
    [InlineData(MappingMode.RealProperty)]
    [InlineData(MappingMode.ShadowPropertyWithValues)]
    [InlineData(MappingMode.ShadowPropertyDefaults)]
    public void OfType_intermediate_type_goes_native_and_returns_subtree(MappingMode mode)
    {
        var mapping = GetMapping(mode);
        var collection = database.CreateCollection<BaseEntity>(values: [mode]);
        SetupTestData(Make(collection, MongoQueryMode.Native, mapping));

        using var db = Make(collection, MongoQueryMode.NativeOnly, mapping);

        // Intermediate type: the predicate is an $in over the {Customer, SubCustomer} subtree.
        var result = db.Entities.OfType<Customer>().ToList();

        Assert.Equal(4, result.Count); // 3 Customer rows + 1 SubCustomer row from SetupTestData.
        Assert.All(result, e => Assert.IsAssignableFrom<Customer>(e));
        Assert.Single(result, e => e is SubCustomer);
    }

    [Theory]
    [InlineData(MappingMode.RealProperty)]
    [InlineData(MappingMode.ShadowPropertyWithValues)]
    [InlineData(MappingMode.ShadowPropertyDefaults)]
    public void OfType_leaf_type_goes_native(MappingMode mode)
    {
        var mapping = GetMapping(mode);
        var collection = database.CreateCollection<BaseEntity>(values: [mode]);
        SetupTestData(Make(collection, MongoQueryMode.Native, mapping));

        using var db = Make(collection, MongoQueryMode.NativeOnly, mapping);

        // Leaf type: the predicate is a single-value $eq.
        var result = db.Entities.OfType<SubCustomer>().ToList();

        Assert.Single(result);
        Assert.All(result, e => Assert.IsType<SubCustomer>(e));
    }

    [Theory]
    [InlineData(MappingMode.RealProperty)]
    [InlineData(MappingMode.ShadowPropertyWithValues)]
    [InlineData(MappingMode.ShadowPropertyDefaults)]
    public void OfType_matches_driver_linq_for_intermediate_type(MappingMode mode)
    {
        var mapping = GetMapping(mode);
        var collection = database.CreateCollection<BaseEntity>(values: [mode]);
        SetupTestData(Make(collection, MongoQueryMode.Native, mapping));

        using var nativeDb = Make(collection, MongoQueryMode.Native, mapping);
        using var driverDb = Make(collection, MongoQueryMode.DriverLinq, mapping);

        var nativeIds = nativeDb.Entities.OfType<Customer>().OrderBy(e => e._id).Select(e => e._id).ToList();
        var driverIds = driverDb.Entities.OfType<Customer>().OrderBy(e => e._id).Select(e => e._id).ToList();

        Assert.Equal(driverIds, nativeIds);
        Assert.Equal(4, nativeIds.Count);
    }

    [Theory]
    [InlineData(MappingMode.RealProperty)]
    [InlineData(MappingMode.ShadowPropertyWithValues)]
    [InlineData(MappingMode.ShadowPropertyDefaults)]
    public void OfType_matches_driver_linq_for_leaf_type(MappingMode mode)
    {
        var mapping = GetMapping(mode);
        var collection = database.CreateCollection<BaseEntity>(values: [mode]);
        SetupTestData(Make(collection, MongoQueryMode.Native, mapping));

        using var nativeDb = Make(collection, MongoQueryMode.Native, mapping);
        using var driverDb = Make(collection, MongoQueryMode.DriverLinq, mapping);

        var nativeIds = nativeDb.Entities.OfType<SubCustomer>().OrderBy(e => e._id).Select(e => e._id).ToList();
        var driverIds = driverDb.Entities.OfType<SubCustomer>().OrderBy(e => e._id).Select(e => e._id).ToList();

        Assert.Equal(driverIds, nativeIds);
        Assert.Single(nativeIds);
    }

    [Fact]
    public void OfType_composed_with_where_goes_native()
    {
        var mapping = GetMapping(MappingMode.RealProperty);
        var collection = database.CreateCollection<BaseEntity>();
        SetupTestData(Make(collection, MongoQueryMode.Native, mapping));

        using var db = Make(collection, MongoQueryMode.NativeOnly, mapping);

        // The discriminator conjunct must AND with the Where predicate, not replace it: Sequence <= 3 excludes
        // SubCustomer (Sequence 5), which the bare $in would include. Filters on a BaseEntity-declared member
        // because derived-only members resolve against the root entity type only (a known gap), and enum
        // equality arrives as Convert(prop, int) == constant, which the numeric-cast guard rejects.
        var result = db.Entities.OfType<Customer>().Where(c => c.Sequence <= 3).ToList();

        Assert.Equal(3, result.Count);
        Assert.All(result, e => Assert.IsType<Customer>(e));
    }

    [Theory]
    [InlineData(false)] // value converter on the discriminator
    [InlineData(true)]  // non-default BsonRepresentation on the discriminator
    public void OfType_value_converted_or_represented_discriminator_goes_native(bool useBsonRepresentation)
    {
        // Both paths must serialize discriminator values through the property's serializer (as the write path
        // does) so the filter matches the stored converted/represented value rather than the raw model value.
        Action<ModelBuilder> model = useBsonRepresentation
            ? IntDiscriminatorStringRepresentationModel
            : ConvertedDiscriminatorModel;
        var collection = database.CreateCollection<BaseEntity>(values: [useBsonRepresentation]);
        SetupTestData(Make(collection, MongoQueryMode.Native, model));

        using (var nativeDb = Make(collection, MongoQueryMode.Native, model))
        using (var driverDb = Make(collection, MongoQueryMode.DriverLinq, model))
        {
            var nativeIds = nativeDb.Entities.OfType<Customer>().OrderBy(e => e._id).Select(e => e._id).ToList();
            var driverIds = driverDb.Entities.OfType<Customer>().OrderBy(e => e._id).Select(e => e._id).ToList();

            Assert.Equal(driverIds, nativeIds);
            Assert.Equal(4, nativeIds.Count);
        }

        // Succeeding under NativeOnly (rather than throwing) is the "went native" signal.
        using var nativeOnlyDb = Make(collection, MongoQueryMode.NativeOnly, model);
        var result = nativeOnlyDb.Entities.OfType<Customer>().ToList();
        Assert.Equal(4, result.Count);
        Assert.All(result, e => Assert.IsAssignableFrom<Customer>(e));
    }

    [Fact]
    public void OfType_composed_with_Distinct_on_derived_members_falls_back_under_native_only()
    {
        var mapping = GetMapping(MappingMode.RealProperty);
        var collection = database.CreateCollection<BaseEntity>();
        SetupTestData(Make(collection, MongoQueryMode.Native, mapping));

        // Sequence 1 and 2 share ("Customer 1", "123 Main St"), so Distinct is observable (3 pairs overall).
        // Falls back because Name/ShippingAddress are Customer-only and the translator resolves members against
        // the root entity type (BaseEntity), not the OfType-narrowed type.
        using (var db = Make(collection, MongoQueryMode.NativeOnly, mapping))
        {
            Assert.Throws<NativeTranslationNotSupportedException>(() =>
                db.Entities.OfType<Customer>().Select(x => new { x.Name, x.ShippingAddress }).Distinct().ToList());
        }

        // Native == DriverLinq: with the fallback, results are still correct and deduped.
        using var nativeDb = Make(collection, MongoQueryMode.Native, mapping);
        var result = nativeDb.Entities.OfType<Customer>()
            .Select(x => new { x.Name, x.ShippingAddress })
            .Distinct()
            .AsEnumerable()
            .OrderBy(r => r.Name)
            .ToList();

        Assert.Equal(
            new[] { ("Customer 1", "123 Main St"), ("Customer 2", "123 Main St"), ("SubCustomer 1", "3.5 Inch Dr.") },
            result.Select(r => (r.Name, r.ShippingAddress)).ToArray());
    }

    [Fact]
    public void OfType_composed_with_Distinct_on_base_declared_member_goes_native()
    {
        // Companion to the test above: a BaseEntity-declared member through the same composition goes native,
        // so the fallback there is the derived-member gap, not OfType + Distinct itself.
        var mapping = GetMapping(MappingMode.RealProperty);
        var collection = database.CreateCollection<BaseEntity>();
        SetupTestData(Make(collection, MongoQueryMode.Native, mapping));

        using var db = Make(collection, MongoQueryMode.NativeOnly, mapping);

        var result = db.Entities.OfType<Customer>()
            .Select(x => new { x.Status })
            .Distinct()
            .AsEnumerable()
            .OrderBy(r => r.Status)
            .ToList();

        Assert.Equal(new[] { Status.Active, Status.Inactive }, result.Select(r => r.Status).ToArray());
    }

    [Fact]
    public void OfType_composed_with_Distinct_matches_driver_linq()
    {
        var mapping = GetMapping(MappingMode.RealProperty);
        var collection = database.CreateCollection<BaseEntity>();
        SetupTestData(Make(collection, MongoQueryMode.Native, mapping));

        using var nativeDb = Make(collection, MongoQueryMode.Native, mapping);
        using var driverDb = Make(collection, MongoQueryMode.DriverLinq, mapping);

        (string Name, string ShippingAddress)[] Run(SingleEntityDbContext<BaseEntity> db) =>
            db.Entities.OfType<Customer>()
                .Select(x => new { x.Name, x.ShippingAddress })
                .Distinct()
                .AsEnumerable()
                .OrderBy(r => r.Name)
                .Select(r => (r.Name, r.ShippingAddress))
                .ToArray();

        var native = Run(nativeDb);
        Assert.Equal(3, native.Length);
        Assert.Equal(Run(driverDb), native);
    }

    [Fact]
    public void Non_TPH_OfType_falls_back_gracefully_and_works_across_modes()
    {
        // No discriminator property, so TryBuildDiscriminatorPredicate declines; pins that OfType still returns
        // correct results in every query mode.
        var collection = database.CreateCollection<Animal>(nameof(Non_TPH_OfType_falls_back_gracefully_and_works_across_modes));

        using (var db = MakeAnimalContext(collection, MongoQueryMode.Native, NoDiscriminatorModel))
        {
            db.Add(new Animal {Name = "Animal 1"});
            db.Add(new Animal {Name = "Animal 2"});
            db.Add(new Cat {Name = "Cat 1", Purrs = true});
            db.SaveChanges();
        }

        List<Cat> RunOfTypeQuery(AnimalDbContext db) =>
            db.Animals.AsNoTracking().OfType<Cat>().ToList();

        using (var nativeDb = MakeAnimalContext(collection, MongoQueryMode.Native, NoDiscriminatorModel))
        {
            var cats = RunOfTypeQuery(nativeDb);
            Assert.Single(cats);
            Assert.All(cats, c => Assert.IsType<Cat>(c));
            Assert.Equal("Cat 1", cats[0].Name);
        }

        using (var driverDb = MakeAnimalContext(collection, MongoQueryMode.DriverLinq, NoDiscriminatorModel))
        {
            var cats = RunOfTypeQuery(driverDb);
            Assert.Single(cats);
            Assert.Equal("Cat 1", cats[0].Name);
        }

        // NativeOnly also succeeds for this shape.
        using (var nativeOnlyDb = MakeAnimalContext(collection, MongoQueryMode.NativeOnly, NoDiscriminatorModel))
        {
            var cats = RunOfTypeQuery(nativeOnlyDb);
            Assert.Single(cats);
            Assert.Equal("Cat 1", cats[0].Name);
        }
    }

    [Fact]
    public void OfType_with_orderby_skip_take_goes_native_and_returns_correct_rows()
    {
        var mapping = GetMapping(MappingMode.RealProperty);
        var collection = database.CreateCollection<BaseEntity>();
        SetupTestData(Make(collection, MongoQueryMode.Native, mapping));

        using var nativeDb = Make(collection, MongoQueryMode.Native, mapping);
        using var driverDb = Make(collection, MongoQueryMode.DriverLinq, mapping);

        // Subtree by Sequence is {1, 2, 3, 5}; Skip(1).Take(2) must return {2, 3}. Result-correctness
        // complement to NativeGateRoutingTests.C_tph_oftype_derived_routing.
        List<int> Run(SingleEntityDbContext<BaseEntity> db) =>
            db.Entities.OfType<Customer>().OrderBy(e => e.Sequence).Skip(1).Take(2)
                .Select(e => e.Sequence).ToList();

        var native = Run(nativeDb);
        Assert.Equal(new[] { 2, 3 }, native);
        Assert.Equal(Run(driverDb), native);

        using var nativeOnlyDb = Make(collection, MongoQueryMode.NativeOnly, mapping);
        var nativeOnlyResult = nativeOnlyDb.Entities.OfType<Customer>().OrderBy(e => e.Sequence).Skip(1).Take(2).ToList();
        Assert.Equal(new[] { 2, 3 }, nativeOnlyResult.Select(e => e.Sequence).ToArray());
    }

    private static Action<ModelBuilder> GetMapping(MappingMode mappingMode)
        => mappingMode switch
        {
            MappingMode.RealProperty => RealPropertyConfiguredModel,
            MappingMode.ShadowPropertyDefaults => ShadowPropertyConfiguredModel,
            MappingMode.ShadowPropertyWithValues => ShadowPropertyNoValuesConfiguredModel,
            _ => throw new ArgumentOutOfRangeException(nameof(mappingMode), mappingMode, null)
        };

    private static SingleEntityDbContext<BaseEntity> Make(
        IMongoCollection<BaseEntity> collection, MongoQueryMode mode, Action<ModelBuilder> mapping)
        => SingleEntityDbContext.Create(
            collection,
            modelBuilderAction: mapping,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });

    private static AnimalDbContext MakeAnimalContext(
        IMongoCollection<Animal> collection, MongoQueryMode mode, Action<ModelBuilder> mapping)
        => AnimalDbContext.Create(
            collection,
            modelBuilderAction: mapping,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });

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
    }

    private static void ShadowPropertyNoValuesConfiguredModel(ModelBuilder mb)
    {
        mb.Entity<BaseEntity>()
            .HasDiscriminator();
        // There is no HasValue without a value, this is the required syntax.
        mb.Entity<Customer>();
        mb.Entity<Order>();
        mb.Entity<SubCustomer>();
        mb.Entity<Supplier>();
        mb.Entity<OrderWithProducts>();
        mb.Entity<Contact>();
    }

    // A shadow int discriminator whose element carries a non-default (String) BsonRepresentation.
    private static void IntDiscriminatorStringRepresentationModel(ModelBuilder mb)
    {
        mb.Entity<BaseEntity>()
            .HasDiscriminator<int>("IntType")
            .HasValue<BaseEntity>(0)
            .HasValue<Customer>(1)
            .HasValue<SubCustomer>(2)
            .HasValue<Supplier>(3)
            .HasValue<Order>(4)
            .HasValue<OrderWithProducts>(5)
            .HasValue<Contact>(6);
        mb.Entity<BaseEntity>().Property<int>("IntType").HasBsonRepresentation(BsonType.String);
    }

    // A string discriminator with a value converter: the write stores a prefixed form ("d:Client").
    private static void ConvertedDiscriminatorModel(ModelBuilder mb)
    {
        mb.Entity<BaseEntity>()
            .HasDiscriminator(e => e.EntityType)
            .HasValue<Customer>("Client")
            .HasValue<SubCustomer>("SubClient")
            .HasValue<Supplier>("Supplier")
            .HasValue<Order>("Order")
            .HasValue<OrderWithProducts>("OrderEx")
            .HasValue<Contact>("Contact");
        mb.Entity<BaseEntity>().Property(e => e.EntityType)
            .HasConversion(v => "d:" + v, s => s!.Substring(2));
    }

    // Animal and Cat share a collection with no discriminator property, so OfType<Cat>() can't go native.
    private static void NoDiscriminatorModel(ModelBuilder mb)
    {
        mb.Entity<Animal>().ToCollection("animals");
        mb.Entity<Cat>().ToCollection("animals");
    }

    private static void SetupTestData(DbContext db)
    {
        // Sequence is declared on BaseEntity so native predicates can use it after OfType (derived-only
        // members resolve against the root entity type only).
        db.Add(new Customer {Sequence = 1, Name = "Customer 1", ShippingAddress = "123 Main St", Status = Status.Active});
        db.Add(new Customer {Sequence = 2, Name = "Customer 1", ShippingAddress = "123 Main St", Status = Status.Inactive});
        db.Add(new Customer {Sequence = 3, Name = "Customer 2", ShippingAddress = "123 Main St", Status = Status.Active});
        db.Add(new Supplier {Sequence = 4, Name = "Supplier 1", Products = ["Product 1", "Product 2"]});
        db.Add(new SubCustomer {Sequence = 5, Name = "SubCustomer 1", ShippingAddress = "3.5 Inch Dr.", AccountingCode = 123});
        db.Add(new Order {Sequence = 6, OrderReference = "Order 1"});
        db.Add(new OrderWithProducts {Sequence = 7, OrderReference = "Order 2", Products = ["abc", "123"]});
        db.Add(new Contact {Sequence = 8, Name = "Contact 1"});
        db.Add(new BaseEntity {Sequence = 9});
        db.SaveChanges();
        db.Dispose();
    }

    public enum Status
    {
        Active,
        Inactive,
        Unused
    }

    public class BaseEntity
    {
        public ObjectId _id { get; set; }
        public string? EntityType { get; set; }
        public Status Status { get; set; } = Status.Inactive;
        public int Sequence { get; set; }
    }

    public class Customer : BaseEntity
    {
        public string Name { get; set; }
        public string ShippingAddress { get; set; }
    }

    public class SubCustomer : Customer
    {
        public int AccountingCode { get; set; }
    }

    public class Supplier : BaseEntity
    {
        public string Name { get; set; }
        public List<string> Products { get; set; }
    }

    public class Order : BaseEntity
    {
        public string OrderReference { get; set; }
    }

    public class OrderWithProducts : Order
    {
        public List<string> Products { get; set; }
    }

    public class Contact : BaseEntity
    {
        public string Name { get; set; }
    }

    public class Animal
    {
        public ObjectId _id { get; set; }
        public string Name { get; set; }
    }

    public class Cat : Animal
    {
        public bool Purrs { get; set; }
    }

    private class AnimalDbContext : DbContext
    {
        private readonly IMongoCollection<Animal> _animals;
        private readonly Action<ModelBuilder>? _modelBuilderAction;

        public DbSet<Animal> Animals => Set<Animal>();

        public AnimalDbContext(DbContextOptions options, IMongoCollection<Animal> animals, Action<ModelBuilder>? modelBuilderAction)
            : base(options)
        {
            _animals = animals;
            _modelBuilderAction = modelBuilderAction;
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<Animal>().HasKey("_id");
            modelBuilder.Entity<Animal>().ToCollection("animals");
            modelBuilder.Entity<Cat>().ToCollection("animals");
            _modelBuilderAction?.Invoke(modelBuilder);
        }

        public static AnimalDbContext Create(
            IMongoCollection<Animal> collection,
            Action<ModelBuilder>? modelBuilderAction = null,
            Action<DbContextOptionsBuilder>? optionsBuilderAction = null)
        {
            var options = new DbContextOptionsBuilder<AnimalDbContext>();
            var mongoClient = collection.Database.Client;
            options.UseMongoDB(mongoClient, collection.Database.DatabaseNamespace.DatabaseName);
            optionsBuilderAction?.Invoke(options);
            return new AnimalDbContext(options.Options, collection, modelBuilderAction);
        }
    }
}
