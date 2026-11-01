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

using System.Collections;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Infrastructure;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// An entity of a discriminated hierarchy (table-per-hierarchy) is read by the cursor straight into the concrete type its document names,
/// from one plan holding the properties of every concrete type. Each type must come back with exactly its own values, a required property
/// of a derived type must still be required for that type only, and a hierarchy the plan cannot tell apart keeps the DOM shaper.
/// </summary>
public class NativeStreamingHierarchyTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    private static SingleEntityDbContext<T> CreateContext<T>(
        IMongoCollection<T> collection, MongoQueryMode mode, Action<ModelBuilder>? model = null)
        where T : class
        => SingleEntityDbContext.Create(
            collection,
            model,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });

    private abstract class Animal
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
    }

    private class Dog : Animal
    {
        public string Breed { get; set; } = "";
        public int? Tricks { get; set; }
    }

    private class Puppy : Dog
    {
        public string? Toy { get; set; }
    }

    // Shares the element 'Lives' with Bird: same type, so one read answers for both.
    private class Cat : Animal
    {
        public string Color { get; set; } = "";
        public int Lives { get; set; }
    }

    private class Bird : Animal
    {
        public int Lives { get; set; }
        public double Wingspan { get; set; }
    }

    private static readonly Action<ModelBuilder> AnimalModel = mb =>
    {
        mb.Entity<Animal>();
        mb.Entity<Dog>();
        mb.Entity<Puppy>();
        mb.Entity<Cat>();
        mb.Entity<Bird>();
    };

    private IMongoCollection<Animal> SeedAnimals([CallerMemberName] string? name = null)
    {
        var collection = database.CreateCollection<Animal>(name);
        using var seed = CreateContext(collection, MongoQueryMode.Native, AnimalModel);
        seed.Entities.AddRange(
            new Dog { Id = 1, Name = "rex", Breed = "lab", Tricks = 3 },
            new Cat { Id = 2, Name = "tom", Color = "grey", Lives = 9 },
            new Bird { Id = 3, Name = "tweety", Lives = 1, Wingspan = 0.25 },
            new Puppy { Id = 4, Name = "bit", Breed = "pug", Tricks = null, Toy = "ball" },
            new Dog { Id = 5, Name = "fido", Breed = "mutt" });
        seed.SaveChanges();
        return collection;
    }

    private static string Describe(Animal a)
        => a switch
        {
            Puppy p => $"Puppy:{p.Id}:{p.Name}:{p.Breed}:{p.Tricks?.ToString() ?? "-"}:{p.Toy ?? "-"}",
            Dog d => $"Dog:{d.Id}:{d.Name}:{d.Breed}:{d.Tricks?.ToString() ?? "-"}",
            Cat c => $"Cat:{c.Id}:{c.Name}:{c.Color}:{c.Lives}",
            Bird b => $"Bird:{b.Id}:{b.Name}:{b.Lives}:{b.Wingspan}",
            _ => "?"
        };

    private static Type CursorRowType<T>(IQueryable<T> query)
    {
        var enumerator = ((IEnumerable)query).GetEnumerator();
        try
        {
            return enumerator.GetType().GetGenericArguments()[0];
        }
        finally
        {
            (enumerator as IDisposable)?.Dispose();
        }
    }

    [Fact]
    public void Hierarchy_root_query_streams()
    {
        var collection = SeedAnimals();
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly, AnimalModel);

        Assert.NotEqual(typeof(BsonDocument), CursorRowType(db.Entities.AsNoTracking()));
        Assert.NotEqual(typeof(BsonDocument), CursorRowType(db.Entities));
    }

    [Fact]
    public void Every_concrete_type_comes_back_with_its_own_values()
    {
        var collection = SeedAnimals();

        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateContext(collection, mode, AnimalModel);
            return db.Entities.AsNoTracking().OrderBy(a => a.Id).AsEnumerable().Select(Describe).ToList();
        });

        Assert.Equal(
            [
                "Dog:1:rex:lab:3", "Cat:2:tom:grey:9", "Bird:3:tweety:1:0.25", "Puppy:4:bit:pug:-:ball", "Dog:5:fido:mutt:-"
            ],
            results);
    }

    [Fact]
    public void Derived_root_and_filtered_hierarchy_queries_match_driver_linq()
    {
        var collection = SeedAnimals();

        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateContext(collection, mode, AnimalModel);
            var dogs = db.Entities.AsNoTracking().OfType<Dog>().OrderBy(a => a.Id).AsEnumerable().Select(Describe).ToList();
            var puppies = db.Set<Puppy>().AsNoTracking().OrderBy(a => a.Id).AsEnumerable().Select(Describe).ToList();
            var lives = db.Entities.AsNoTracking().Where(a => a.Id > 1 && a.Id < 4).OrderBy(a => a.Id).AsEnumerable().Select(Describe).ToList();
            return dogs.Concat(puppies).Concat(lives).ToList();
        });

        Assert.Equal(
            [
                "Dog:1:rex:lab:3", "Puppy:4:bit:pug:-:ball", "Dog:5:fido:mutt:-",
                "Puppy:4:bit:pug:-:ball",
                "Cat:2:tom:grey:9", "Bird:3:tweety:1:0.25"
            ],
            results);
    }

    [Fact]
    public void Tracked_hierarchy_query_tracks_each_concrete_type()
    {
        var collection = SeedAnimals();

        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateContext(collection, mode, AnimalModel);
            var animals = db.Entities.OrderBy(a => a.Id).ToList();
            var first = db.Entities.First(a => a.Id == 4);
            return new List<string>
            {
                $"entries={db.ChangeTracker.Entries<Animal>().Count()}",
                $"same={ReferenceEquals(first, animals[3])}",
                string.Join(",", animals.Select(a => a.GetType().Name)),
            };
        });

        Assert.Equal("entries=5", results[0]);
        Assert.Equal("same=True", results[1]);
        Assert.Equal("Dog,Cat,Bird,Puppy,Dog", results[2]);
    }

    [Fact]
    public void A_required_property_of_a_derived_type_is_required_for_that_type_only()
    {
        var collection = SeedAnimals();
        var raw = collection.Database.GetCollection<BsonDocument>(collection.CollectionNamespace.CollectionName);
        raw.UpdateOne(Builders<BsonDocument>.Filter.Eq("_id", 5), Builders<BsonDocument>.Update.Unset("Breed"));

        foreach (var mode in new[] { MongoQueryMode.DriverLinq, MongoQueryMode.Native })
        {
            using var db = CreateContext(collection, mode, AnimalModel);

            // The dog without its Breed fails; the cat, the bird and the other dogs, which are not asked for a Breed they do not have, do not.
            Assert.Throws<InvalidOperationException>(() => db.Entities.AsNoTracking().OrderBy(a => a.Id).ToList());
            Assert.Equal(
                ["Cat:2:tom:grey:9", "Bird:3:tweety:1:0.25"],
                db.Entities.AsNoTracking().Where(a => a.Id == 2 || a.Id == 3).OrderBy(a => a.Id).AsEnumerable().Select(Describe).ToList());
        }
    }

    // ---- a hierarchy the plan cannot tell apart ----------------------------------------------------------

    private abstract class Shape
    {
        public int Id { get; set; }
    }

    private class Circle : Shape
    {
        public double Size { get; set; }
    }

    // Shares the element 'Size' with Circle but stores it as a string: one read cannot answer for both, so the DOM shaper reads it.
    private class Label : Shape
    {
        public string? Size { get; set; }
    }

    private static readonly Action<ModelBuilder> ShapeModel = mb =>
    {
        mb.Entity<Shape>();
        mb.Entity<Circle>();
        mb.Entity<Label>();
    };

    [Fact]
    public void Siblings_that_store_a_shared_element_differently_still_read_correctly()
    {
        var collection = database.CreateCollection<Shape>();
        using (var seed = CreateContext(collection, MongoQueryMode.Native, ShapeModel))
        {
            seed.Entities.AddRange(new Circle { Id = 1, Size = 2.5 }, new Label { Id = 2, Size = "big" });
            seed.SaveChanges();
        }

        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateContext(collection, mode, ShapeModel);
            return db.Entities.AsNoTracking().OrderBy(s => s.Id).AsEnumerable()
                .Select(s => s switch { Circle c => $"Circle:{c.Size}", Label l => $"Label:{l.Size}", _ => "?" })
                .ToList();
        });

        Assert.Equal(["Circle:2.5", "Label:big"], results);
    }
}
