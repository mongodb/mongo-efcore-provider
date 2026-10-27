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
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Infrastructure;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// <c>x is T</c>, <c>x.GetType() ==/!= typeof(T)</c> and derived-only members after <c>OfType&lt;T&gt;()</c> over a TPH
/// hierarchy: one shared discriminator test (<see cref="MongoDiscriminatorPredicate"/>), where an abstract type has no
/// discriminator value and contributes nothing.
/// </summary>
public class NativeTphTypeTestTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    // Base <- Party (abstract) <- Customer <- SubCustomer ; Party <- Supplier ; Base <- Order <- OrderWithLines
    public class Base
    {
        public ObjectId Id { get; set; }
        public string Label { get; set; } = "";
        public string? Kind { get; set; }
    }

    public abstract class Party : Base;

    public class Customer : Party
    {
        public string CustomerName { get; set; } = "";
    }

    public class SubCustomer : Customer;

    public class Supplier : Party
    {
        public string CustomerName { get; set; } = "";
    }

    public class Order : Base;

    public class OrderWithLines : Order;

    public class Unrelated;

    private static void MapReal(ModelBuilder mb)
    {
        mb.Entity<Party>(); // abstract: mapped, but it has no discriminator value
        mb.Entity<Base>().HasDiscriminator(e => e.Kind)
            .HasValue<Base>("base").HasValue<Customer>("cust").HasValue<SubCustomer>("sub").HasValue<Supplier>("sup")
            .HasValue<Order>("ord").HasValue<OrderWithLines>("ordx"); // Party deliberately has no value
    }

    private static void MapShadow(ModelBuilder mb)
    {
        mb.Entity<Base>().HasDiscriminator();
        mb.Entity<Party>();
        mb.Entity<Customer>();
        mb.Entity<SubCustomer>();
        mb.Entity<Supplier>().Property(s => s.CustomerName).HasElementName("sname");
        mb.Entity<Order>();
        mb.Entity<OrderWithLines>();
    }

    private static Action<ModelBuilder> Map(string mapping) => mapping == "real" ? MapReal : MapShadow;

    public static IEnumerable<object[]> Shapes()
    {
        foreach (var mapping in new[] { "real", "shadow" })
        {
            yield return [mapping, "is_Customer", new[] { "c1", "c2", "sc" }];
            yield return [mapping, "is_abstract_Party", new[] { "c1", "c2", "s1", "sc" }];
            yield return [mapping, "is_leaf_SubCustomer", new[] { "sc" }];
            yield return [mapping, "is_Base_always_true", new[] { "b", "c1", "c2", "o", "ox", "s1", "sc" }];
            yield return [mapping, "not_is_Customer", new[] { "b", "o", "ox", "s1" }];
            yield return [mapping, "is_or_is", new[] { "o", "ox", "s1" }];
            yield return [mapping, "is_and_base_member", new[] { "c2", "sc" }];
            yield return [mapping, "GetType_eq", new[] { "c1", "c2" }];
            yield return [mapping, "GetType_ne", new[] { "b", "o", "ox", "s1", "sc" }];
            yield return [mapping, "reversed_GetType", new[] { "o" }];
            yield return [mapping, "OfType_derived_member", new[] { "c1", "sc" }];
            yield return [mapping, "OfType_is_sub", new[] { "sc" }];
            yield return [mapping, "OfType_sibling_same_name", new[] { "s1" }];
        }
    }

    private static IQueryable<Base> Shape(IQueryable<Base> q, string name)
        => name switch
        {
            "is_Customer" => q.Where(e => e is Customer),
            "is_abstract_Party" => q.Where(e => e is Party),
            "is_leaf_SubCustomer" => q.Where(e => e is SubCustomer),
            "is_Base_always_true" => q.Where(e => e is Base),
            "not_is_Customer" => q.Where(e => !(e is Customer)),
            "is_or_is" => q.Where(e => e is Supplier || e is Order),
            "is_and_base_member" => q.Where(e => e is Customer && e.Label != "c1"),
            "GetType_eq" => q.Where(e => e.GetType() == typeof(Customer)),
            "GetType_ne" => q.Where(e => e.GetType() != typeof(Customer)),
            "reversed_GetType" => q.Where(e => typeof(Order) == e.GetType()),
            "OfType_derived_member" => q.OfType<Customer>().Where(c => c.CustomerName == "x"),
            "OfType_is_sub" => q.OfType<Customer>().Where(c => c is SubCustomer),
            "OfType_sibling_same_name" => q.OfType<Supplier>().Where(s => s.CustomerName == "x"),
            _ => throw new ArgumentOutOfRangeException(nameof(name))
        };

    [Theory]
    [MemberData(nameof(Shapes))]
    public void Type_tests_go_native_and_match(string mapping, string shape, string[] expected)
    {
        var collection = Seed(mapping);
        NativeModeAssert.NativeAndExpected(
            mode =>
            {
                using var db = Create(collection, mode, mapping);
                return Shape(db.Entities.AsNoTracking(), shape).OrderBy(e => e.Label).Select(e => e.Label).ToList();
            },
            [.. expected]);
    }

    [Fact]
    public void OfType_an_abstract_type_does_not_match_a_document_without_a_discriminator()
    {
        var collection = Seed("real");
        database.MongoDatabase.GetCollection<BsonDocument>(collection.CollectionNamespace.CollectionName)
            .InsertOne(new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Label", "nodisc" } });

        using var db = Create(collection, MongoQueryMode.NativeOnly, "real");
        var labels = db.Entities.AsNoTracking().OfType<Party>().OrderBy(e => e.Label).Select(e => e.Label).ToList();

        // $in: [null, ...] would also have matched the document that has no discriminator element.
        Assert.Equal(["c1", "c2", "s1", "sc"], labels);
    }

    [Fact]
    public void GetType_against_a_type_outside_the_hierarchy_matches_nothing_natively()
    {
        var collection = Seed("real");
        using var db = Create(collection, MongoQueryMode.NativeOnly, "real");

        // EF-202: driver-LINQ narrows on discriminator presence rather than the comparison type, so it would answer
        // every row.
        Assert.Empty(db.Entities.Where(e => e.GetType() == typeof(Unrelated)).ToList());
        Assert.Equal(7, db.Entities.Count(e => e.GetType() != typeof(Unrelated)));
    }

    [Fact]
    public void A_derived_member_through_a_cast_declines_cleanly()
    {
        var collection = Seed("real");
        using var db = Create(collection, MongoQueryMode.NativeOnly, "real");

        // The rows are not narrowed by a cast, so a sibling's same-named element must not be read natively.
        Assert.Throws<NativeTranslationNotSupportedException>(
            () => db.Entities.Where(e => ((Customer)e).CustomerName == "x").ToList());
    }

    private static SingleEntityDbContext<Base> Create(IMongoCollection<Base> collection, MongoQueryMode mode, string mapping)
        => SingleEntityDbContext.Create(
            collection,
            Map(mapping),
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });

    private IMongoCollection<Base> Seed(string mapping)
    {
        var name = TemporaryDatabaseFixtureBase.CreateCollectionName("tph" + mapping) + Guid.NewGuid().ToString("N")[..8];
        var collection = database.MongoDatabase.GetCollection<Base>(name);
        using var db = Create(collection, MongoQueryMode.DriverLinq, mapping);
        db.AddRange(
            new Base { Label = "b" },
            new Customer { Label = "c1", CustomerName = "x" },
            new Customer { Label = "c2", CustomerName = "y" },
            new SubCustomer { Label = "sc", CustomerName = "x" },
            new Supplier { Label = "s1", CustomerName = "x" },
            new Order { Label = "o" },
            new OrderWithLines { Label = "ox" });
        db.SaveChanges();
        return collection;
    }
}
