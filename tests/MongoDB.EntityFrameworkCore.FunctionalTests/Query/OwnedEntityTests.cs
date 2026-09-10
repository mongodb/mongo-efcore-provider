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
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Diagnostics;
using MongoDB.EntityFrameworkCore.Extensions;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

[XUnitCollection("QueryTests")]
public class OwnedEntityTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    [Fact]
    public void OwnedEntity_nested_one_level_materializes_single()
    {
        var collection = database.CreateCollection<PersonWithLocation>();
        collection.WriteTestDocs(PersonWithLocation1);
        using var db = SingleEntityDbContext.Create(collection);

        var actual = db.Entities.Single();

        Assert.Equal("Carmen", actual.name);
        Assert.Equal(Location1.latitude, actual.location.latitude);
        Assert.Equal(Location1.longitude, actual.location.longitude);
    }

    [Fact]
    public void OwnedEntity_nested_one_level_materializes_single_get_only()
    {
        database.CreateCollection<PersonWithLocation>().WriteTestDocs(PersonWithLocation1);
        using var db = SingleEntityDbContext.Create(database.GetCollection<PersonWithGetOnlyLocation>(),
            mb => { mb.Entity<PersonWithGetOnlyLocation>().OwnsOne(p => p.location); });

        var actual = db.Entities.Single();

        Assert.Equal("Carmen", actual.name);
        Assert.Equal(Location1.latitude, actual.location.latitude);
        Assert.Equal(Location1.longitude, actual.location.longitude);
    }

    [Fact]
    public void OwnedEntity_nested_one_level_where_not_null()
    {
        var collection = database.CreateCollection<PersonWithLocation>();
        collection.WriteTestDocs(PersonWithLocation1);
        using var db = SingleEntityDbContext.Create(collection);

        var actual = db.Entities.Where(e => e.location != null).First();

        Assert.Equal("Carmen", actual.name);
        Assert.Equal(Location1.latitude, actual.location.latitude);
        Assert.Equal(Location1.longitude, actual.location.longitude);
    }

    [Fact]
    public void OwnedEntity_nested_one_level_where_null()
    {
        var collection = database.CreateCollection<PersonWithOptionalLocation>();
        collection.WriteTestDocs([new PersonWithOptionalLocation { _id = ObjectId.GenerateNewId(), name = "Milton" }]);
        using var db = SingleEntityDbContext.Create(collection);

        var actual = db.Entities.Where(e => e.location == null).First();

        Assert.Equal("Milton", actual.name);
        Assert.Null(actual.location);
    }

    [Fact]
    public void OwnedEntity_nested_one_level_first_matching_location()
    {
        var collection = database.CreateCollection<PersonWithLocation>();
        collection.WriteTestDocs(PersonWithLocation1);
        collection.WriteTestDocs(Person2WithLocation1);
        using var db = SingleEntityDbContext.Create(collection);

        var location = db.Entities.First(p => p.name == "Carmen").location;
        var actual = db.Entities.First(p => p.location == location && p.name != "Carmen");

        Assert.Equal("Milton", actual.name);
        Assert.Equal(location.latitude, actual.location.latitude);
        Assert.Equal(location.longitude, actual.location.longitude);
    }

    [Fact]
    public void OwnedEntity_nested_one_level_where_no_matching_location()
    {
        var collection = database.CreateCollection<PersonWithLocation>();
        collection.WriteTestDocs(PersonWithLocation1);
        collection.WriteTestDocs(Person2WithLocation1);
        using var db = SingleEntityDbContext.Create(collection);

        var location = db.Entities.First(p => p.name == "Carmen").location;
        var found = db.Entities.Where(p => p.location != location).ToList();

        Assert.Empty(found);
    }

    [Fact]
    public void OwnedEntity_nested_one_level_first_match_location_property()
    {
        var collection = database.CreateCollection<PersonWithLocation>();
        collection.WriteTestDocs(PersonWithLocation1);
        collection.WriteTestDocs(Person2WithLocation1);
        using var db = SingleEntityDbContext.Create(collection);

        var location = db.Entities.First(p => p.name == "Carmen").location;
        var actual = db.Entities.FirstOrDefault(p => p.location.latitude == location.latitude && p.name != "Carmen");

        Assert.Equal("Milton", actual.name);
    }

    [Fact]
    public void OwnedEntity_nested_one_level_collection_match()
    {
        var collection = database.CreateCollection<PersonWithMultipleLocations>();
        collection.WriteTestDocs(PersonWithLocations1);
        using var db = SingleEntityDbContext.Create(collection);

        var location = db.Entities.First().locations[1];
        var actual = db.Entities.FirstOrDefault(p => p.locations.Contains(location));

        Assert.Equal("Damien", actual.name);
    }

    [Fact]
    public void OwnedEntity_nested_one_level_collection_not_match()
    {
        var collection = database.CreateCollection<PersonWithMultipleLocations>();
        collection.WriteTestDocs(PersonWithLocations1);
        using var db = SingleEntityDbContext.Create(collection);

        var location = db.Entities.First().locations[1];
        var actual = db.Entities.FirstOrDefault(p => !p.locations.Contains(location));

        Assert.Equal("Carmen", actual.name);
    }

    [Fact]
    public void OwnedEntity_nested_one_level_collection_any_match()
    {
        var collection = database.CreateCollection<PersonWithMultipleLocations>();
        collection.WriteTestDocs(PersonWithLocations1);
        using var db = SingleEntityDbContext.Create(collection);

        var location = db.Entities.First().locations[1];
        var actual = db.Entities.FirstOrDefault(p => p.locations.Any(l => l == location));

        Assert.Equal("Damien", actual.name);
    }

    [Fact]
    public void OwnedEntity_nested_one_level_collection_any_not_match()
    {
        var collection = database.CreateCollection<PersonWithMultipleLocations>();
        collection.WriteTestDocs(PersonWithLocations1);
        using var db = SingleEntityDbContext.Create(collection);

        var location = db.Entities.First().locations[1];
        var actual = db.Entities.FirstOrDefault(p => !p.locations.Any(l => l == location));

        Assert.Equal("Carmen", actual.name);
    }

    [Fact]
    public void OwnedEntity_missing_document_element_does_not_throw()
    {
        database.CreateCollection<Person>().WriteTestDocs([
            new Person { name = "Bill" }
        ]);

        var collection = database.GetCollection<PersonWithOptionalLocation>();
        using var db = SingleEntityDbContext.Create(collection);

        var person = db.Entities.First();
        Assert.NotNull(person);
        Assert.Equal("Bill", person.name);
        Assert.Null(person.location);
    }

    [Fact]
    public void OwnedEntity_nested_one_level_allows_nested_where()
    {
        var collection = database.CreateCollection<PersonWithLocation>();
        collection.WriteTestDocs(PersonWithLocation1);
        using var db = SingleEntityDbContext.Create(collection);

        var actual = db.Entities.First(e => e.location.latitude > 0.00m);

        Assert.Equal("Carmen", actual.name);
        Assert.Equal(Location1.latitude, actual.location.latitude);
        Assert.Equal(Location1.longitude, actual.location.longitude);
    }

    [Fact]
    public void OwnedEntity_nested_two_levels_allows_nested_where()
    {
        var collection = database.CreateCollection<PersonWithCity>();
        collection.WriteTestDocs(PersonWithCity1);
        using var db = SingleEntityDbContext.Create(collection);

        var actual = db.Entities.First(e => e.location.city.name == "San Diego");

        Assert.Equal("Carmen", actual.name);
        Assert.Equal(Location1.latitude, actual.location.latitude);
        Assert.Equal(Location1.longitude, actual.location.longitude);
    }

    [Fact]
    public void OwnedEntity_materializes_when_null_non_required_owned_entity()
    {
        var collection = database.CreateCollection<PersonWithLocation>();
        collection.WriteTestDocs(PersonWithMissingLocation1);
        using var db = SingleEntityDbContext.Create(collection,
            mb => { mb.Entity<PersonWithLocation>().Navigation(p => p.location).IsRequired(false); });

        var actual = db.Entities.Where(p => p.name == "Elizabeth").ToList();

        Assert.NotEmpty(actual);
        Assert.Equal("Elizabeth", actual[0].name);
        Assert.Null(actual[0].location);
    }

    [Fact]
    public void OwnedEntity_materializes_when_missing_non_required_owned_entity()
    {
        var collection = database.CreateCollection<Person>();
        collection.WriteTestDocs([new Person { name = "Henry" }]);
        using var db = SingleEntityDbContext.Create(database.GetCollection<PersonWithLocation>(),
            mb => { mb.Entity<PersonWithLocation>().Navigation(p => p.location).IsRequired(false); });

        var actual = db.Entities.Where(p => p.name == "Henry").ToList();

        Assert.NotEmpty(actual);
        Assert.Equal("Henry", actual[0].name);
        Assert.Null(actual[0].location);
    }

    [Fact]
    public void OwnedEntity_throws_when_missing_required_owned_entity()
    {
        var collection = database.CreateCollection<PersonWithLocation>();
        collection.WriteTestDocs(PersonWithMissingLocation1);
        using var db = SingleEntityDbContext.Create(collection,
            mb => { mb.Entity<PersonWithLocation>().Navigation(p => p.location).IsRequired(); });

        var ex = Assert.Throws<InvalidOperationException>(() => db.Entities.Where(p => p.name != "bob").ToList());
        Assert.Contains(nameof(PersonWithLocation), ex.Message);
        Assert.Contains(nameof(PersonWithLocation.location), ex.Message);
    }

    [Fact]
    public void OwnedEntity_nested_one_level_materializes_many()
    {
        var collection = database.CreateCollection<PersonWithLocation>();
        collection.WriteTestDocs(PersonWithLocation1);
        using var db = SingleEntityDbContext.Create(collection);

        var actual = db.Entities.Where(p => p.name != "bob").ToList();

        Assert.NotEmpty(actual);
        Assert.Equal("Carmen", actual[0].name);
        Assert.Equal(Location1.latitude, actual[0].location.latitude);
        Assert.Equal(Location1.longitude, actual[0].location.longitude);
    }

    [Fact]
    public void OwnedEntity_nested_one_level_creates()
    {
        var collection = database.CreateCollection<PersonWithLocation>();
        var expected =
            new PersonWithLocation { name = "Charlie", location = new Location { latitude = 1.234m, longitude = 1.567m } };

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entities.Add(expected);
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var actual = db.Entities.First(p => p.name == "Charlie");

            Assert.Equal(expected.name, actual.name);
            Assert.Equal(expected.location.latitude, actual.location.latitude);
            Assert.Equal(expected.location.longitude, actual.location.longitude);
        }
    }

    [Fact]
    public void OwnedEntity_collection_creates()
    {
        var collection =
            database.CreateCollection<PersonWithMultipleLocations>();

        PersonWithMultipleLocations expected = new()
        {
            _id = ObjectId.GenerateNewId(),
            name = "Alfred",
            locations =
            [
                new() { latitude = 1.234m, longitude = 1.567m },

                new() { latitude = 5.1m, longitude = 3.9m }
            ]
        };

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entities.Add(expected);
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var actual = db.Entities.First(p => p.name == "Alfred");

            Assert.Equal(expected.name, actual.name);
            Assert.Equal(expected.locations[0].latitude, actual.locations[0].latitude);
            Assert.Equal(expected.locations[0].longitude, actual.locations[0].longitude);
            Assert.Equal(expected.locations[1].latitude, actual.locations[1].latitude);
            Assert.Equal(expected.locations[1].longitude, actual.locations[1].longitude);
        }
    }

    [Fact]
    public void OwnedEntity_can_set_single_owned_entity_element_name()
    {
        var collection = database.CreateCollection<PersonWithLocation>();

        var id = ObjectId.GenerateNewId();
        var expectedName = Guid.NewGuid().ToString();
        var expectedLocation = new Location { latitude = 1.234m, longitude = 1.567m };

        var modelBuilder = (ModelBuilder mb) =>
        {
            mb.Entity<PersonWithLocation>(p =>
            {
                p.OwnsOne(e => e.location, f =>
                {
                    f.HasElementName("Location");
                    f.Property(g => g.longitude)
                        .HasElementName("Longitude")
                        .HasBsonRepresentation(BsonType.String);
                });
            });
        };

        {
            using var dbContext = SingleEntityDbContext.Create(collection, modelBuilder);
            dbContext.Entities.Add(new PersonWithLocation { _id = id, name = expectedName, location = expectedLocation });
            dbContext.SaveChanges();
        }

        {
            using var dbContext = SingleEntityDbContext.Create(collection, modelBuilder);
            var found = dbContext.Entities.Single(f => f._id == id);
            Assert.Equal(expectedName, found.name);
            Assert.Equal(expectedLocation.latitude, found.location.latitude);
            Assert.Equal(expectedLocation.longitude, found.location.longitude);
        }
    }

    [Fact]
    public void OwnedEntity_can_set_meta_on_owned_entity_element_name()
    {
        var collection = database.CreateCollection<PersonWithMultipleLocations>();

        var id = ObjectId.GenerateNewId();
        var expectedName = Guid.NewGuid().ToString();
        var expectedLocation = new Location { latitude = 1.234m, longitude = 1.567m };

        var modelBuilder = (ModelBuilder mb) =>
        {
            mb.Entity<PersonWithMultipleLocations>(p =>
            {
                p.OwnsMany(e => e.locations, f =>
                {
                    f.HasElementName("Locations");
                    f.Property(g => g.longitude)
                        .HasElementName("Longitude")
                        .HasBsonRepresentation(BsonType.String);
                });
            });
        };

        {
            using var dbContext = SingleEntityDbContext.Create(collection, modelBuilder);
            dbContext.Entities.Add(
                new PersonWithMultipleLocations { _id = id, name = expectedName, locations = [expectedLocation] });
            dbContext.SaveChanges();
        }

        {
            using var dbContext = SingleEntityDbContext.Create(collection, modelBuilder);
            var found = dbContext.Entities.Single(f => f._id == id);
            Assert.Equal(expectedName, found.name);
            var foundLocation = Assert.Single(found.locations);
            Assert.Equal(expectedLocation.latitude, foundLocation.latitude);
            Assert.Equal(expectedLocation.longitude, foundLocation.longitude);
        }
    }

    [Fact]
    public void OwnedEntity_projection_alias_with_bson_representation_uses_owned_property_serializer()
    {
        var collection = database.CreateCollection<PersonWithLocation>();

        var id = ObjectId.GenerateNewId();
        var expectedLocation = new Location { latitude = 1.234m, longitude = 1.567m };

        var modelBuilder = (ModelBuilder mb) =>
        {
            mb.Entity<PersonWithLocation>(p =>
            {
                p.OwnsOne(e => e.location, f =>
                {
                    f.HasElementName("Location");
                    f.Property(g => g.longitude)
                        .HasElementName("Longitude")
                        .HasBsonRepresentation(BsonType.String);
                });
            });
        };

        {
            using var dbContext = SingleEntityDbContext.Create(collection, modelBuilder);
            dbContext.Entities.Add(new PersonWithLocation
            {
                _id = id,
                name = Guid.NewGuid().ToString(),
                location = expectedLocation
            });
            dbContext.SaveChanges();
        }

        {
            using var dbContext = SingleEntityDbContext.Create(collection, modelBuilder);
            var found = dbContext.Entities.AsNoTracking()
                .Where(e => e._id == id)
                .Select(e => new { Alias = e.location.longitude })
                .Single();

            Assert.Equal(expectedLocation.longitude, found.Alias);
        }
    }

    [Fact]
    public void OwnedEntity_dotted_scalar_leaf_projects_alongside_array_leaf()
    {
        var collection = database.CreateCollection<BlogWithHome>();

        var id = ObjectId.GenerateNewId();

        var modelBuilder = (ModelBuilder mb) =>
        {
            mb.Entity<BlogWithHome>().OwnsOne(b => b.Home, h => h.OwnsMany(x => x.Notes, n => n.HasKey(x => x.NoteId)));
        };

        {
            using var dbContext = SingleEntityDbContext.Create(collection, modelBuilder);
            dbContext.Entities.Add(new BlogWithHome
            {
                _id = id,
                Home = new Home { City = "Springfield", Notes = [new Note { NoteId = 1, Text = "a" }, new Note { NoteId = 2, Text = "b" }] }
            });
            dbContext.SaveChanges();
        }

        {
            using var dbContext = SingleEntityDbContext.Create(collection, modelBuilder);
            var found = dbContext.Entities.AsNoTracking()
                .Where(e => e._id == id)
                .Select(e => new { e.Home.City, e.Home.Notes })
                .Single();

            Assert.Equal(["a", "b"], found.Notes.Select(n => n.Text));
            Assert.Equal("Springfield", found.City);
        }
    }

    private record BlogWithHome
    {
        public ObjectId _id { get; set; }
        public Home Home { get; set; }
    }

    private record Home
    {
        public string City { get; set; }
        public List<Note> Notes { get; set; }
    }

    private record Note
    {
        public int NoteId { get; set; }
        public string Text { get; set; }
    }

    [Fact]
    public void OwnedEntity_two_level_dotted_scalar_leaf_projects_alongside_array_leaf()
    {
        var collection = database.CreateCollection<BlogWithNestedHome>();

        var id = ObjectId.GenerateNewId();

        var modelBuilder = (ModelBuilder mb) =>
        {
            mb.Entity<BlogWithNestedHome>().OwnsOne(b => b.Home, h =>
            {
                h.OwnsMany(x => x.Notes, n => n.HasKey(x => x.NoteId));
                h.OwnsOne(x => x.Inner);
            });
        };

        {
            using var dbContext = SingleEntityDbContext.Create(collection, modelBuilder);
            dbContext.Entities.Add(new BlogWithNestedHome
            {
                _id = id,
                Home = new NestedHome
                {
                    Inner = new InnerHome { City = "Springfield" },
                    Notes = [new Note { NoteId = 1, Text = "a" }, new Note { NoteId = 2, Text = "b" }]
                }
            });
            dbContext.SaveChanges();
        }

        {
            using var dbContext = SingleEntityDbContext.Create(collection, modelBuilder);
            var found = dbContext.Entities.AsNoTracking()
                .Where(e => e._id == id)
                .Select(e => new { e.Home.Inner.City, e.Home.Notes })
                .Single();

            Assert.Equal(["a", "b"], found.Notes.Select(n => n.Text));
            Assert.Equal("Springfield", found.City);
        }
    }

    private record BlogWithNestedHome
    {
        public ObjectId _id { get; set; }
        public NestedHome Home { get; set; }
    }

    private record NestedHome
    {
        public InnerHome Inner { get; set; }
        public List<Note> Notes { get; set; }
    }

    private record InnerHome
    {
        public string City { get; set; }
    }

    [Fact]
    public void OwnedEntity_collection_projection_alias_with_bson_representation_uses_owned_property_serializer()
    {
        var collection = database.CreateCollection<PersonWithMultipleLocations>();

        var id = ObjectId.GenerateNewId();
        var expectedLocations = new[]
        {
            new Location { latitude = 1.234m, longitude = 1.567m },
            new Location { latitude = 2.345m, longitude = 2.678m }
        };

        var modelBuilder = (ModelBuilder mb) =>
        {
            mb.Entity<PersonWithMultipleLocations>(p =>
            {
                p.OwnsMany(e => e.locations, f =>
                {
                    f.HasElementName("Locations");
                    f.Property(g => g.longitude)
                        .HasElementName("Longitude")
                        .HasBsonRepresentation(BsonType.String);
                });
            });
        };

        {
            using var dbContext = SingleEntityDbContext.Create(collection, modelBuilder);
            dbContext.Entities.Add(new PersonWithMultipleLocations
            {
                _id = id,
                name = "A",
                locations = [.. expectedLocations]
            });
            dbContext.SaveChanges();
        }

        {
            using var dbContext = SingleEntityDbContext.Create(collection, modelBuilder);
            var actual = dbContext.Entities
                .AsNoTracking()
                .Where(e => e._id == id)
                .Select(e => new
                {
                    e.name,
                    e.locations,
                    Longitudes = e.locations
                        .Select(l => new { Alias = l.longitude })
                        .ToList()
                })
                .Single();

            Assert.Equal("A", actual.name);
            Assert.Equal(expectedLocations.Length, actual.locations.Count);
            Assert.Equal(expectedLocations.Select(l => l.longitude), actual.Longitudes.Select(l => l.Alias));
        }
    }

    class SimpleNonNullableCollection
    {
        public ObjectId _id { get; set; }
        public List<SimpleChild> children { get; set; }
    }

    class SimpleNullableCollection
    {
        public ObjectId _id { get; set; }
        public List<SimpleChild>? children { get; set; }
    }

    class MissingNullableCollection
    {
        public ObjectId _id { get; set; }
    }

    class SimpleChild
    {
        public string name { get; set; }
    }

    [Theory]
    [InlineData(QueryTrackingBehavior.TrackAll)]
    [InlineData(QueryTrackingBehavior.NoTracking)]
    [InlineData(QueryTrackingBehavior.NoTrackingWithIdentityResolution)]
    public void OwnedEntity_non_nullable_collection_is_empty_when_empty(QueryTrackingBehavior queryTrackingBehavior)
    {
        var collection = database.CreateCollection<SimpleNonNullableCollection>(values: queryTrackingBehavior);
        collection.WriteTestDocs([
            new SimpleNonNullableCollection { children = [] }
        ]);
        using var db = SingleEntityDbContext.Create(
            collection,
            optionsBuilderAction: x => x.UseQueryTrackingBehavior(queryTrackingBehavior));

        var actual = db.Entities.First();
        Assert.Empty(actual.children);
    }

    [Theory]
    [InlineData(QueryTrackingBehavior.TrackAll)]
    [InlineData(QueryTrackingBehavior.NoTracking)]
    [InlineData(QueryTrackingBehavior.NoTrackingWithIdentityResolution)]
    public void OwnedEntity_nullable_collection_is_empty_when_empty(QueryTrackingBehavior queryTrackingBehavior)
    {
        var collection = database.CreateCollection<SimpleNullableCollection>(values: queryTrackingBehavior);
        collection.WriteTestDocs([
            new SimpleNullableCollection { children = [] }
        ]);
        using var db = SingleEntityDbContext.Create(
            collection,
            optionsBuilderAction: x => x.UseQueryTrackingBehavior(queryTrackingBehavior));

        var actual = db.Entities.First();
        Assert.NotNull(actual.children);
        Assert.Empty(actual.children);
    }

    // RENAMED (EF-358) from "..._is_null_when_null" / "..._is_null_when_missing". The old names asserted a
    // provider contract that never existed: the old "null" was produced by EF Core's IncludeExpression fixup
    // (in MongoProjectionBindingRemovingExpressionVisitor.IncludeCollection) being skipped because the
    // provider's computed value was null, leaving `children` at the POCO's own default — not by any provider
    // guarantee. EF-358 always materializes a real (possibly empty) collection, so the fixup always runs and
    // every class reads back the same regardless of its field initializer.
    [Theory]
    [InlineData(QueryTrackingBehavior.TrackAll)]
    [InlineData(QueryTrackingBehavior.NoTracking)]
    [InlineData(QueryTrackingBehavior.NoTrackingWithIdentityResolution)]
    public void OwnedEntity_non_nullable_collection_is_empty_when_null(QueryTrackingBehavior queryTrackingBehavior)
    {
        var collection = database.CreateCollection<SimpleNonNullableCollection>(values: queryTrackingBehavior);
        collection.WriteTestDocs([
            new SimpleNonNullableCollection { children = null! }
        ]);
        using var db = SingleEntityDbContext.Create(
            collection,
            optionsBuilderAction: x => x.UseQueryTrackingBehavior(queryTrackingBehavior));

        var actual = db.Entities.First();
        Assert.Empty(actual.children);
    }

    [Theory]
    [InlineData(QueryTrackingBehavior.TrackAll)]
    [InlineData(QueryTrackingBehavior.NoTracking)]
    [InlineData(QueryTrackingBehavior.NoTrackingWithIdentityResolution)]
    public void OwnedEntity_nullable_collection_is_empty_when_null(QueryTrackingBehavior queryTrackingBehavior)
    {
        var collection = database.CreateCollection<SimpleNullableCollection>(values: queryTrackingBehavior);
        collection.WriteTestDocs([
            new SimpleNullableCollection { children = null }
        ]);
        using var db = SingleEntityDbContext.Create(
            collection,
            optionsBuilderAction: x => x.UseQueryTrackingBehavior(queryTrackingBehavior));

        var actual = db.Entities.First();
        Assert.NotNull(actual.children);
        Assert.Empty(actual.children);
    }

    [Theory]
    [InlineData(QueryTrackingBehavior.TrackAll)]
    [InlineData(QueryTrackingBehavior.NoTracking)]
    [InlineData(QueryTrackingBehavior.NoTrackingWithIdentityResolution)]
    public void OwnedEntity_non_nullable_collection_is_empty_when_missing(QueryTrackingBehavior queryTrackingBehavior)
    {
        var collection = database.CreateCollection<MissingNullableCollection>(values: queryTrackingBehavior);
        collection.WriteTestDocs([new MissingNullableCollection()]);
        using var db = SingleEntityDbContext.Create<MissingNullableCollection, SimpleNonNullableCollection>(
            collection,
            optionsBuilderAction: x => x.UseQueryTrackingBehavior(queryTrackingBehavior));

        var actual = db.Entities.First();
        Assert.Empty(actual.children);
    }

    [Theory]
    [InlineData(QueryTrackingBehavior.TrackAll)]
    [InlineData(QueryTrackingBehavior.NoTracking)]
    [InlineData(QueryTrackingBehavior.NoTrackingWithIdentityResolution)]
    public void OwnedEntity_nullable_collection_is_empty_when_missing(QueryTrackingBehavior queryTrackingBehavior)
    {
        var collection = database.CreateCollection<MissingNullableCollection>(values: queryTrackingBehavior);
        collection.WriteTestDocs([new MissingNullableCollection()]);
        using var db = SingleEntityDbContext.Create<MissingNullableCollection, SimpleNullableCollection>(
            collection,
            optionsBuilderAction: x => x.UseQueryTrackingBehavior(queryTrackingBehavior));

        var actual = db.Entities.First();
        Assert.NotNull(actual.children);
        Assert.Empty(actual.children);
    }

    // EF-358: the projection path (Select over a CollectionShaperExpression) used to disagree with whole-entity
    // materialization for a missing or explicitly-null stored array — it produced a null CLR collection instead
    // of an empty one. Owned-collection projections require AsNoTracking (EF Core cannot track an owned entity
    // without its owner in the result), so this covers the same "missing"/"null" states as the whole-entity
    // theories above, but through Select rather than whole-entity materialization.
    [Fact]
    public void OwnedEntity_collection_projection_is_empty_when_missing()
    {
        var collection = database.CreateCollection<MissingNullableCollection>();
        collection.WriteTestDocs([new MissingNullableCollection()]);
        using var db = SingleEntityDbContext.Create<MissingNullableCollection, SimpleNonNullableCollection>(collection);

        var actual = db.Entities.AsNoTracking().Select(e => e.children).First();
        Assert.NotNull(actual);
        Assert.Empty(actual);
    }

    [Fact]
    public void OwnedEntity_collection_projection_is_empty_when_null()
    {
        var collection = database.CreateCollection<SimpleNonNullableCollection>();
        collection.WriteTestDocs([new SimpleNonNullableCollection { children = null! }]);
        using var db = SingleEntityDbContext.Create(collection);

        var actual = db.Entities.AsNoTracking().Select(e => e.children).First();
        Assert.NotNull(actual);
        Assert.Empty(actual);
    }

    // EF-358: a collection shaper nested inside another owned collection's item (FirstLevel.children[i].children)
    // never gets a bound bsonArray variable — BsonDocumentInjectingExpressionVisitor doesn't recurse into a
    // CollectionShaperExpression's InnerShaper — so it always takes the "else" branch that reads the BsonArray
    // straight off the parent element via CreateGetBsonArray. That's a different code path from the root-level
    // case above, so it needs its own missing/null coverage.
    [Fact]
    public void OwnedEntity_nested_collection_is_empty_when_grandchild_array_missing()
    {
        var collection = database.CreateCollection<FirstLevelWithMissingGrandchildren>();
        collection.WriteTestDocs([
            new FirstLevelWithMissingGrandchildren
            {
                _id = Guid.NewGuid(),
                day = DayOfWeek.Monday,
                reference = new() { name = "ref", day = DayOfWeek.Friday },
                children = [new SecondLevelMissingChildren { day = DayOfWeek.Tuesday }]
            }
        ]);
        using var db = SingleEntityDbContext.Create<FirstLevelWithMissingGrandchildren, FirstLevel>(collection);

        var actual = db.Entities.First();
        var secondLevel = Assert.Single(actual.children);
        Assert.NotNull(secondLevel.children);
        Assert.Empty(secondLevel.children);
    }

    [Fact]
    public void OwnedEntity_nested_collection_is_empty_when_grandchild_array_null()
    {
        var collection = database.CreateCollection<FirstLevelWithNullGrandchildren>();
        collection.WriteTestDocs([
            new FirstLevelWithNullGrandchildren
            {
                _id = Guid.NewGuid(),
                day = DayOfWeek.Monday,
                reference = new() { name = "ref", day = DayOfWeek.Friday },
                children = [new SecondLevelNullChildren { day = DayOfWeek.Tuesday, children = null! }]
            }
        ]);
        using var db = SingleEntityDbContext.Create<FirstLevelWithNullGrandchildren, FirstLevel>(collection);

        var actual = db.Entities.First();
        var secondLevel = Assert.Single(actual.children);
        Assert.NotNull(secondLevel.children);
        Assert.Empty(secondLevel.children);
    }

    [Fact]
    public void OwnedEntity_nested_two_levels_materializes_single()
    {
        var collection = database.CreateCollection<PersonWithCity>();
        collection.WriteTestDocs(PersonWithCity1);
        using var db = SingleEntityDbContext.Create(collection);

        var actual = db.Entities.Single();

        Assert.Equal("Carmen", actual.name);
        Assert.Equal(Location1.latitude, actual.location.latitude);
        Assert.Equal(Location1.longitude, actual.location.longitude);
        Assert.Equal(City1.name, actual.location.city.name);
    }

    [Fact]
    public void OwnedEntity_nested_two_levels_materializes_many()
    {
        var collection = database.CreateCollection<PersonWithCity>();
        collection.WriteTestDocs(PersonWithCity1);
        using var db = SingleEntityDbContext.Create(collection);

        var actual = db.Entities.Where(p => p.name != "bob").ToList();

        Assert.NotEmpty(actual);

        Assert.Equal("Carmen", actual[0].name);
        Assert.Equal(Location1.latitude, actual[0].location.latitude);
        Assert.Equal(Location1.longitude, actual[0].location.longitude);
        Assert.Equal(City1.name, actual[0].location.city.name);
    }

    [Fact]
    public void OwnedEntity_with_collection_materializes_many()
    {
        var collection = database.CreateCollection<PersonWithMultipleLocations>();
        collection.WriteTestDocs(PersonWithLocations1);
        using var db = SingleEntityDbContext.Create(collection);

        var actual = db.Entities.Where(p => p.name != "bob").ToList();

        Assert.NotEmpty(actual);

        Assert.Equal("Damien", actual[0].name);
        Assert.Equal(2, actual[0].locations.Count);

        Assert.Single(actual[0].locations, s => Location1.latitude == s.latitude && Location1.longitude == s.longitude);
        Assert.Single(actual[0].locations, s => Location2.latitude == s.latitude && Location2.longitude == s.longitude);
    }

    [Fact]
    public void OwnedEntity_with_ienumerable_collection_materializes_many()
    {
        var expectedLocation = new Location { latitude = 1.01m, longitude = 1.02m };
        var collection = database.CreateCollection<PersonWithIEnumerableLocations>();
        collection.WriteTestDocs([
            new()
            {
                _id = ObjectId.GenerateNewId(), name = "IEnumerableRound1", locations = new List<Location> { expectedLocation }
            },
            new()
            {
                _id = ObjectId.GenerateNewId(),
                name = "IEnumerableRound2",
                locations = new List<Location> { new() { latitude = 1.03m, longitude = 1.04m } }
            }
        ]);

        var actual = SingleEntityDbContext.Create(collection).Entities.ToList();

        Assert.NotEmpty(actual);
        Assert.Equal("IEnumerableRound1", actual[0].name);
        Assert.Equal(2, actual.Count);
        var actualLocation = Assert.Single(actual[0].locations);
        Assert.Equal(expectedLocation, actualLocation);
    }

    [Fact]
    public void OwnedEntity_with_ienumerable_list_serializes()
    {
        var collection = database.CreateCollection<PersonWithIEnumerableLocations>();
        var entity = new PersonWithIEnumerableLocations
        {
            _id = ObjectId.GenerateNewId(),
            name = "IEnumerableSerialize",
            locations = new List<Location> { Location1, Location2 }
        };

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entities.Add(entity);
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var actual = db.Entities.FirstOrDefault();

            Assert.NotNull(actual);
            Assert.Equal("IEnumerableSerialize", actual.name);
            Assert.Equal(2, actual.locations.Count());
            Assert.Equal(Location1, actual.locations.First());
            Assert.Equal(Location2, actual.locations.Last());
        }
    }

    [Fact]
    public void OwnedEntity_with_ienumerable_non_list_or_array_throws()
    {
        var collection = database.CreateCollection<PersonWithIEnumerableLocations>();
        using var db = SingleEntityDbContext.Create(collection);

        var entity = new PersonWithIEnumerableLocations
        {
            _id = ObjectId.GenerateNewId(),
            name = "IEnumerableSerialize",
            locations = EnumerableOnlyWrapper.Wrap(new List<Location> { Location1, Location2 })
        };

        var ex = Assert.Throws<InvalidOperationException>(() => db.Entities.Add(entity));
        Assert.Contains(nameof(PersonWithIEnumerableLocations.locations), ex.Message);
        Assert.Contains(entity.locations.GetType().ShortDisplayName(), ex.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnedEntity_with_collection_adjusted_correctly(bool async)
    {
        var collection = database.CreateCollection<PersonWithMultipleLocations>(values: [async]);

        {
            await using var db = SingleEntityDbContext.Create(collection);

            var original = new PersonWithMultipleLocations
            {
                _id = ObjectId.GenerateNewId(),
                name = "Many updates",
                locations =
                [
                    new Location { latitude = 1.1m, longitude = 2.2m }
                ]
            };

            db.Add(original);
            await SaveChanges(db, async);
            Assert.Single(original.locations, l => l.latitude == 1.1m);

            original.locations.Add(new() { latitude = 3.3m, longitude = 4.4m });
            await SaveChanges(db, async);

            Assert.Equal(2, original.locations.Count);
        }

        {
            await using var db = SingleEntityDbContext.Create(collection);

            var found = db.Entities.Single();
            Assert.Equal(2, found.locations.Count);

            found.locations.RemoveAt(0);
            await SaveChanges(db, async);
            AssertAllEntriesAreUnchanged(db);
            Assert.Single(found.locations, l => l.longitude == 4.4m);

            await SaveChanges(db, async);
            AssertAllEntriesAreUnchanged(db);

            found.locations.Clear();
            await SaveChanges(db, async);
        }

        {
            await using var db = SingleEntityDbContext.Create(collection);
            var found = db.Entities.Single();

            Assert.Empty(found.locations);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnedEntity_collection_modification_does_not_write_unmodified_root_properties(bool async)
    {
        var collection = database.CreateCollection<PersonWithMultipleLocations>(values: [async]);

        await using var originalDb = SingleEntityDbContext.Create(collection);
        var originalEntity = new PersonWithMultipleLocations
        {
            _id = ObjectId.GenerateNewId(),
            name = "Original",
            locations =
            [
                new Location { latitude = 1.1m, longitude = 2.2m },
                new Location { latitude = 3.3m, longitude = 4.4m }
            ]
        };
        originalDb.Entities.Add(originalEntity);
        await SaveChanges(originalDb, async);

        // Use a second context to modify only the name field independently
        {
            await using var modificationDb = SingleEntityDbContext.Create(collection);
            var found = modificationDb.Entities.Single();
            found.name = "Externally modified";
            await SaveChanges(modificationDb, async);
        }

        // Trigger the owned entity update pipeline
        originalEntity.locations.RemoveAt(0);
        await SaveChanges(originalDb, async);

        // Validate that the root entity was not written to
        {
            await using var validationDb = SingleEntityDbContext.Create(collection);
            var found = validationDb.Entities.Single();
            Assert.Equal("Externally modified", found.name);
            Assert.Single(found.locations);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnedEntity_with_nested_collection_adjusted_correctly(bool async)
    {
        var collection = database.CreateCollection<FirstLevel>(values: [async]);

        {
            await using var db = SingleEntityDbContext.Create(collection);

            var original = new FirstLevel
            {
                _id = Guid.NewGuid(),
                day = DayOfWeek.Monday,
                reference = new() { name = "ref", day = DayOfWeek.Friday },
                children =
                [
                    new SecondLevel
                    {
                        day = DayOfWeek.Tuesday,
                        children =
                        [
                            new ThirdLevel
                            {
                                name = "A",
                                day = DayOfWeek.Wednesday,
                                reference = new() { name = "refA", day = DayOfWeek.Thursday }
                            },
                            new ThirdLevel
                            {
                                name = "B",
                                day = DayOfWeek.Thursday,
                                reference = new() { name = "refB", day = DayOfWeek.Friday }
                            }
                        ]
                    }
                ]
            };

            db.Entities.Add(original);
            await SaveChanges(db, async);
            AssertAllEntriesAreUnchanged(db);

            // Add a second SecondLevel with its own children
            original.children.Add(new SecondLevel
            {
                day = DayOfWeek.Saturday,
                children =
                [
                    new ThirdLevel
                    {
                        name = "C", day = DayOfWeek.Sunday, reference = new() { name = "refC", day = DayOfWeek.Monday }
                    }
                ]
            });
            await SaveChanges(db, async);
            AssertAllEntriesAreUnchanged(db);

            Assert.Equal(2, original.children.Count);
        }

        {
            await using var db = SingleEntityDbContext.Create(collection);

            var found = db.Entities.Single();
            Assert.Equal(2, found.children.Count);
            Assert.Equal(2, found.children[0].children.Count);
            Assert.Single(found.children[1].children);

            // Remove first child from nested collection
            found.children[0].children.RemoveAt(0);
            await SaveChanges(db, async);
            AssertAllEntriesAreUnchanged(db);
            Assert.Single(found.children[0].children, c => c.name == "B");

            // Remove first SecondLevel entirely
            found.children.RemoveAt(0);
            await SaveChanges(db, async);
            AssertAllEntriesAreUnchanged(db);
            Assert.Single(found.children, c => c.day == DayOfWeek.Saturday);

            // Verify no spurious changes on subsequent save
            await SaveChanges(db, async);
            AssertAllEntriesAreUnchanged(db);
        }

        {
            await using var db = SingleEntityDbContext.Create(collection);
            var found = db.Entities.Single();

            Assert.Single(found.children);
            Assert.Single(found.children[0].children, c => c.name == "C");
        }
    }

    private static async Task SaveChanges(DbContext db, bool async)
    {
        if (async)
            await db.SaveChangesAsync();
        else
            db.SaveChanges();
    }

    private static void AssertAllEntriesAreUnchanged<T>(SingleEntityDbContext<T> db) where T : class
        => Assert.All(db.ChangeTracker.Entries(), e => Assert.Equal(EntityState.Unchanged, e.State));

    [Fact]
    public void OwnedEntity_with_two_owned_entities_materializes()
    {
        var collection = database.CreateCollection<PersonWithTwoLocations>();
        var expected = PersonWithTwoLocations1[0];
        collection.WriteTestDocs(PersonWithTwoLocations1);
        using var db = SingleEntityDbContext.Create(collection);

        var actual = db.Entities.FirstOrDefault();

        Assert.NotNull(actual);
        Assert.Equal(expected.name, actual.name);
        Assert.Equal(expected.first.latitude, actual.first.latitude);
        Assert.Equal(expected.first.longitude, actual.first.longitude);
        Assert.Equal(expected.second.latitude, actual.second.latitude);
        Assert.Equal(expected.second.longitude, actual.second.longitude);
    }

    [Fact]
    public void OwnedEntity_with_two_owned_entities_creates()
    {
        var collection = database.CreateCollection<PersonWithTwoLocations>();
        PersonWithTwoLocations expected = new() { name = "Elizabeth", first = Location2, second = Location1 };

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entities.Add(expected);
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var actual = db.Entities.FirstOrDefault();

            Assert.NotNull(actual);
            Assert.Equal(expected.name, actual.name);
            Assert.Equal(expected.first.latitude, actual.first.latitude);
            Assert.Equal(expected.first.longitude, actual.first.longitude);
            Assert.Equal(expected.second.latitude, actual.second.latitude);
            Assert.Equal(expected.second.longitude, actual.second.longitude);
        }
    }

    [Fact]
    public void OwnedEntity_can_be_queried_on()
    {
        var collection = database.CreateCollection<PersonWithLocation>();
        var expected = PersonWithLocation1[0];
        collection.WriteTestDocs(PersonWithLocation1);
        using var db = SingleEntityDbContext.Create(collection,
            mb => { mb.Entity<PersonWithLocation>().OwnsOne(p => p.location, r => r.HasElementName("location")); });

        var actual = db.Entities.First(e => e.location.latitude == expected.location.latitude);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void OwnedEntity_collection_can_be_queried_on()
    {
        var collection = database.CreateCollection<PersonWithMultipleLocations>();
        var expected = PersonWithLocations1[0];
        collection.WriteTestDocs(PersonWithLocations1);

        using var db = SingleEntityDbContext.Create(collection);

        var actual = db.Entities.First(e => e.locations.Any(l => l.latitude == expected.locations[0].latitude));

        Assert.Equal(expected._id, actual._id);
    }

    [Fact]
    public void OwnedEntity_nested_one_level_allows_list_nested_where()
    {
        var collection = database.CreateCollection<PersonWithMultipleLocations>();
        collection.WriteTestDocs(PersonWithLocations1);
        using var db = SingleEntityDbContext.Create(collection);

        var actual = db.Entities.First(e => e.locations.Any(l => l.latitude == 40.1m && l.longitude != 0m));

        Assert.Equal("Carmen", actual.name);
        Assert.Equal(Location3.latitude, actual.locations[0].latitude);
        Assert.Equal(Location3.longitude, actual.locations[0].longitude);
    }


    [Fact]
    public void OwnedEntity_can_have_element_name_set()
    {
        var collection = database.CreateCollection<PersonWithLocation>();
        var expected = PersonWithLocation1[0];
        collection.WriteTestDocs(PersonWithLocation1);
        using var db = SingleEntityDbContext.Create(collection,
            mb => { mb.Entity<PersonWithLocation>().OwnsOne(p => p.location, r => r.HasElementName("location")); });

        var actual = db.Entities.FirstOrDefault();

        Assert.NotNull(actual);
        Assert.Equal(expected.name, actual.name);
        Assert.Equal(expected.location.latitude, actual.location.latitude);
        Assert.Equal(expected.location.longitude, actual.location.longitude);
    }

    [Fact]
    public void OwnedEntity_can_have_element_name_set_for_same_types()
    {
        var expected = new PersonWithTwoLocationsRemapped { name = "Elizabeth", locationOne = Location1, locationTwo = Location2 };

        database.CreateCollection<PersonWithTwoLocationsRemapped>().WriteTestDocs([expected]);

        var collection = database.GetCollection<PersonWithTwoLocations>();
        using var db = SingleEntityDbContext.Create(collection,
            mb =>
            {
                mb.Entity<PersonWithTwoLocations>().OwnsOne(p => p.first, r => r.HasElementName("locationOne"));
                mb.Entity<PersonWithTwoLocations>().OwnsOne(p => p.second, r => r.HasElementName("locationTwo"));
            });

        var actual = db.Entities.FirstOrDefault();

        Assert.NotNull(actual);
        Assert.Equal(expected.name, actual.name);
        Assert.Equal(expected.locationOne.latitude, actual.first.latitude);
        Assert.Equal(expected.locationTwo.longitude, actual.second.longitude);
    }

    [Fact]
    public void OwnedEntity_can_go_multiple_levels_deep_serializing()
    {
        var expectedName = FirstLevel1.children[0].children[0].name;
        var collection = database.CreateCollection<FirstLevel>();

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entities.Add(FirstLevel1);
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var actual = db.Entities.FirstOrDefault();

            Assert.NotNull(actual);
            Assert.Equal(FirstLevel1._id, actual._id);
            var secondLevel = Assert.Single(actual.children);
            var thirdLevel = Assert.Single(secondLevel.children);
            Assert.Equal(expectedName, thirdLevel.name);
        }
    }

    [Fact]
    public void OwnedEntity_can_go_multiple_levels_deep_querying_collection()
    {
        var expectedName = FirstLevel1.children[0].children[0].name;
        var collection = database.CreateCollection<FirstLevel>();

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entities.Add(FirstLevel1);
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var actual = db.Entities.FirstOrDefault(e => e.children.Any(f => f.children.Any(g => g.name == expectedName)));

            Assert.NotNull(actual);
            Assert.Equal(FirstLevel1._id, actual._id);
            var secondLevel = Assert.Single(actual.children);
            var thirdLevel = Assert.Single(secondLevel.children);
            Assert.Equal(expectedName, thirdLevel.name);
        }
    }

    public class Blog
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public List<Post> Posts { get; set; }
    }

    public class Post
    {
        public string Content { get; set; }
        public List<Comment> Comments { get; set; }
    }

    public class Comment
    {
        public string Text { get; set; }
    }

    [Fact]
    public void OwnedEntity_collection_leaf_projection_with_nested_collection_element()
    {
        // Regression test: Post's own Comments navigation used to make this leaf mistyped as
        // IEnumerable<Post>, throwing ArgumentException from Expression.New's member-type check.
        var collection = database.CreateCollection<Blog>();
        var expected = new Blog
        {
            Id = "1",
            Title = "t",
            Posts =
            [
                new Post { Content = "c1", Comments = [new Comment { Text = "x" }] },
                new Post { Content = "c2", Comments = [] }
            ]
        };

        using var db = SingleEntityDbContext.Create(collection, mb =>
        {
            mb.Entity<Blog>().OwnsMany(b => b.Posts, p => p.OwnsMany(x => x.Comments));
        });
        db.Entities.Add(expected);
        db.SaveChanges();

        var result = db.Entities.AsNoTracking().Select(b => new { b.Title, b.Posts }).ToList();
        var single = Assert.Single(result);
        Assert.Equal("t", single.Title);
        Assert.Equal(2, single.Posts.Count);
        Assert.Equal("c1", single.Posts[0].Content);
        Assert.Single(single.Posts[0].Comments);
        Assert.Equal("x", single.Posts[0].Comments[0].Text);
        Assert.Equal("c2", single.Posts[1].Content);
        Assert.Empty(single.Posts[1].Comments);
    }

    [Fact]
    public void OwnedEntity_collection_can_be_tested_for_not_null()
    {
        var collection = database.CreateCollection<A>();
        var expected = new A { _id = "1", children = [new B { name = "child1" }, new B { name = "child2" }] };

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entities.AddRange(expected, new A { _id = "2", children = null! });
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            Assert.Equivalent(expected, db.Entities.FirstOrDefault(e => e.children != null && e.children.Count > 0));
            Assert.Equivalent(expected, db.Entities.FirstOrDefault(e => null != e.children && e.children.Count > 0));
        }
    }

    [Fact]
    public void OwnedEntity_collection_field_can_be_tested_for_not_null()
    {
        var collection = database.CreateCollection<AField>();
        var expected = new AField { _id = "1", children = [new B { name = "child1" }, new B { name = "child2" }] };

        {
            using var db = SingleEntityDbContext.Create(collection, mb => mb.Entity<AField>().OwnsMany(f => f.children));
            db.Entities.AddRange(expected, new AField { _id = "2", children = null! });
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection, mb => mb.Entity<AField>().OwnsMany(f => f.children));
            Assert.Equivalent(expected, db.Entities.FirstOrDefault(e => e.children != null && e.children.Count > 0));
            Assert.Equivalent(expected, db.Entities.FirstOrDefault(e => null != e.children && e.children.Count > 0));
        }
    }

    [Fact]
    public void OwnedEntity_collection_can_be_tested_for_null()
    {
        // The predicate `e.children == null` is unaffected by EF-358 — only the materialized value of the
        // matched row flips from `null` to `[]`. `inserted` and `expected` must stay separate objects: setting
        // `children = []` on `inserted` would write an empty array to the document instead of leaving the
        // field unset, changing what gets seeded rather than just what gets asserted.
        var collection = database.CreateCollection<A>();
        var inserted = new A { _id = "1" };
        var expected = new A { _id = "1", children = [] };

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entities.AddRange(inserted, new A { _id = "2", children = [new B { name = "child1" }, new B { name = "child2" }] });
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            Assert.Equivalent(expected, db.Entities.FirstOrDefault(e => e.children == null));
            Assert.Equivalent(expected, db.Entities.FirstOrDefault(e => null == e.children));
        }
    }

    [Fact]
    public void OwnedEntity_collection_field_can_be_tested_for_null()
    {
        // Same reasoning as OwnedEntity_collection_can_be_tested_for_null immediately above: the predicate
        // `e.children == null` is unaffected (still matches row "1" by its missing stored field); only the
        // MATERIALIZED value flips from `null` to `[]` post-EF-358. `inserted` (children left unset) is what
        // gets written, unchanged; `expected` (children = []) is the separate comparison value.
        var collection = database.CreateCollection<AField>();
        var inserted = new AField { _id = "1" };
        var expected = new AField { _id = "1", children = [] };

        {
            using var db = SingleEntityDbContext.Create(collection, mb => mb.Entity<AField>().OwnsMany(f => f.children));
            db.Entities.AddRange(inserted,
                new AField { _id = "2", children = [new B { name = "child1" }, new B { name = "child2" }] });
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection, mb => mb.Entity<AField>().OwnsMany(f => f.children));
            Assert.Equivalent(expected, db.Entities.FirstOrDefault(e => e.children == null));
            Assert.Equivalent(expected, db.Entities.FirstOrDefault(e => null == e.children));
        }
    }

    [Theory]
    [InlineData(typeof(int))]
    [InlineData(typeof(string))]
    [InlineData(null)]
    public void OwnedEntity_can_go_multiple_levels_deep_querying_enum(Type? storageType)
    {
        var configBuilderAction = (ModelConfigurationBuilder cb) => { };
        if (storageType != null)
        {
            configBuilderAction = cb => cb.Properties<DayOfWeek>().HaveConversion(storageType);
        }

        var collection = database.CreateCollection<FirstLevel>(values: [storageType]);

        {
            using var db = SingleEntityDbContext.Create(collection, configBuilderAction: configBuilderAction);
            db.Entities.Add(FirstLevel1);
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection, configBuilderAction: configBuilderAction);

            var level1 = db.Entities.First(e => e.day == DayOfWeek.Monday);
            Assert.Equal(FirstLevel1._id, level1._id);

            var level2Ref = db.Entities.First(e => e.reference.day == DayOfWeek.Friday);
            Assert.Equal(FirstLevel1._id, level2Ref._id);

            var level2 = db.Entities.First(e => e.children.Any(f => f.day == DayOfWeek.Tuesday));
            Assert.Equal(FirstLevel1._id, level2._id);

            var level3 = db.Entities.First(e => e.children.Any(f => f.children.Any(g => g.day == DayOfWeek.Wednesday)));
            Assert.Equal(FirstLevel1._id, level3._id);

            var level4 = db.Entities.First(e => e.children.Any(f => f.children.Any(g => g.reference.day == DayOfWeek.Thursday)));
            Assert.Equal(FirstLevel1._id, level4._id);
        }
    }

    [Fact]
    public void OwnedEntity_can_query_owned_entity_collection_with_remapped_name()
    {
        var docs = database.CreateCollection<BsonDocument>();
        var id = ObjectId.GenerateNewId();

        {
            docs.InsertOne(new BsonDocument("_id", id)
            {
                ["children"] = new BsonArray { new BsonDocument("name", "child1"), new BsonDocument("name", "child2") }
            });
        }

        {
            var collection = database.GetCollection<SimpleOwner>();
            using var db = SingleEntityDbContext.Create(collection, mb =>
            {
                var owned = mb.Entity<SimpleOwner>().OwnsMany(o => o.Children);
                owned.HasElementName("children");
                owned.Property(o => o.Name).HasElementName("name");
            });

            var actual = db.Entities.FirstOrDefault(e => e.Children.Any(c => c.Name == "child1"));
            Assert.NotNull(actual);
            Assert.Equal(id, actual.Id);
        }
    }

    [Fact]
    public void OwnedEntity_can_go_multiple_levels_deep_querying_reference()
    {
        var expectedReference = FirstLevel1.children[0].children[0].reference;
        var collection = database.CreateCollection<FirstLevel>();

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entities.Add(FirstLevel1);
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var actual = db.Entities.FirstOrDefault(e
                => e.children.Any(f => f.children.Any(g => g.reference.name == expectedReference.name)));

            Assert.NotNull(actual);
            Assert.Equal(FirstLevel1._id, actual._id);
            var secondLevel = Assert.Single(actual.children);
            var thirdLevel = Assert.Single(secondLevel.children);
            Assert.Equal(expectedReference.name, thirdLevel.reference.name);
        }
    }

    [Fact]
    public void OwnedEntity_filtered_count_in_projection_translates()
    {
        var collection = database.CreateCollection<CountBlog>();

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entities.Add(new CountBlog
            {
                _id = "1",
                Title = "Blog1",
                Posts = [new CountPost { Rank = 1 }, new CountPost { Rank = 0 }, new CountPost { Rank = 2 }]
            });
            db.SaveChanges();
        }

        {
            // Assert the count is evaluated server-side (a $map/$sum over the array, equivalent to
            // filter+size) rather than by materializing the owned CountPost entities and counting client-side.
            var (loggerFactory, spyLogger) = SpyLoggerProvider.Create();
            using var db = SingleEntityDbContext.Create(collection, loggerFactory,
                optionsBuilderAction: o => o.EnableSensitiveDataLogging());
            var result = Assert.Single(db.Entities.Select(b => new { b.Title, N = b.Posts.Count(p => p.Rank > 0) }));

            Assert.Equal("Blog1", result.Title);
            Assert.Equal(2, result.N);

            var message = spyLogger.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery);
            Assert.Contains("$map", message);
            Assert.Contains("$sum", message);
        }
    }

    [Fact]
    public void OwnedEntity_unfiltered_count_in_projection_translates()
    {
        var collection = database.CreateCollection<CountBlog>();

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entities.Add(new CountBlog
            {
                _id = "1",
                Title = "Blog1",
                Posts = [new CountPost { Rank = 1 }, new CountPost { Rank = 0 }]
            });
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var result = Assert.Single(db.Entities.Select(b => new { b.Title, N = b.Posts.Count() }));

            Assert.Equal("Blog1", result.Title);
            Assert.Equal(2, result.N);
        }
    }

    [Fact]
    public void OwnedEntity_collection_bare_count_projection_returns_element_count()
    {
        var collection = database.CreateCollection<A>();

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entities.AddRange(
                new A { _id = "1", children = [new B { name = "child1" }, new B { name = "child2" }] },
                new A { _id = "2", children = [] },
                new A { _id = "3", children = null! });
            db.SaveChanges();
        }

        // Deliberately a default *tracking* query (unlike EF-357's own tests, which required
        // AsNoTracking): the bare Count must stay a server-side scalar, not a materialized owned-entity
        // shaper, or EF Core's tracking-materializer rejects it ("owned entity without a corresponding
        // owner").
        var (loggerFactory, spyLogger) = SpyLoggerProvider.Create();
        using var db2 = SingleEntityDbContext.Create(collection, loggerFactory,
            optionsBuilderAction: o => o.EnableSensitiveDataLogging());

        var counts = db2.Entities.OrderBy(e => e._id).Select(e => e.children.Count).ToList();
        Assert.Equal([2, 0, 0], counts);

        // A null stored array must report 0, not throw: the driver renders a bare Count as a server-side
        // $size, which rejects a null array, so this needs an $ifNull normalization first.
        var message = spyLogger.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery);
        Assert.Contains("$ifNull", message);
        Assert.Contains("$size", message);

        var longCounts = db2.Entities.OrderBy(e => e._id).Select(e => e.children.LongCount()).ToList();
        Assert.Equal([2L, 0L, 0L], longCounts);

        var filteredLongCounts = db2.Entities.OrderBy(e => e._id)
            .Select(e => e.children.LongCount(c => c.name == "child1"))
            .ToList();
        Assert.Equal([1L, 0L, 0L], filteredLongCounts);
    }

    [Fact]
    public void OwnedEntity_collection_bare_count_projection_over_missing_element_returns_zero()
    {
        // A document where the array element is OMITTED entirely (not stored as BSON null) is a distinct
        // case from an explicit null: in an aggregation expression a missing field is not equal to BSON
        // null, so a plain null-equality guard does not catch it and $size still throws on "missing". Only
        // $ifNull (which normalizes missing the same as null) handles both.
        var collection = database.CreateCollection<A>();
        database.GetCollection<BsonDocument>(collection.CollectionNamespace).InsertOne(new BsonDocument("_id", "1"));

        using var db = SingleEntityDbContext.Create(collection);
        var counts = db.Entities.Select(e => e.children.Count).ToList();

        Assert.Equal([0], counts);
    }

    [Fact]
    public void OwnedEntity_collection_count_projection_wrapped_in_arithmetic_returns_element_count()
    {
        var collection = database.CreateCollection<A>();

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entities.AddRange(
                new A { _id = "1", children = [new B { name = "child1" }, new B { name = "child2" }] },
                new A { _id = "2", children = null! });
            db.SaveChanges();
        }

        using var db2 = SingleEntityDbContext.Create(collection);
        var doubled = db2.Entities.OrderBy(e => e._id)
            .Select(e => new { e._id, N = e.children.Count * 2 })
            .ToList();

        Assert.Equal([4, 0], doubled.Select(r => r.N).ToArray());
    }

    [Fact]
    public void OwnedEntity_collection_filtered_count_projection_with_null_children_returns_zero()
    {
        var collection = database.CreateCollection<A>();

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entities.AddRange(
                new A { _id = "1", children = [new B { name = "child1" }, new B { name = "child2" }] },
                new A { _id = "2", children = null! });
            db.SaveChanges();
        }

        using var db2 = SingleEntityDbContext.Create(collection);
        var counts = db2.Entities.OrderBy(e => e._id)
            .Select(e => e.children.Count(c => c.name == "child1"))
            .ToList();

        Assert.Equal([1, 0], counts);
    }

    record CountBlog
    {
        public string _id { get; set; }
        public string Title { get; set; }
        public List<CountPost> Posts { get; set; }
    }

    record CountPost
    {
        public int Rank { get; set; }
    }

    record A
    {
        public string _id { get; set; }
        public List<B> children { get; set; }
    }

    record AField
    {
        public string _id { get; set; }
        public List<B> children;
    }

    record B
    {
        public string name { get; set; }
    }

    private record Person
    {
        public ObjectId _id { get; set; }
        public string name { get; set; }
    }

    private record PersonWithOptionalLocation : Person
    {
        public Location? location { get; set; }
    }

    private record PersonWithLocation : Person
    {
        public Location location { get; set; }
    }

    private record PersonWithGetOnlyLocation : Person
    {
        public PersonWithGetOnlyLocation()
        {
        }

        public PersonWithGetOnlyLocation(string name, Location location)
        {
            this.name = name;
            this.location = location;
        }

        public Location location { get; }
    }

    private record PersonWithCity : Person
    {
        public LocationWithCity location { get; set; }
    }

    private record Location
    {
        public decimal latitude { get; set; }
        public decimal longitude { get; set; }
    }

    private record LocationWithCity : Location
    {
        public City city { get; set; }
    }

    private record City
    {
        public string name { get; set; }
    }

    private record PersonWithMultipleLocations : Person
    {
        public List<Location> locations { get; set; }
    }


    private record PersonWithIEnumerableLocations : Person
    {
        public IEnumerable<Location> locations { get; set; }
    }

    private record PersonWithTwoLocations : Person
    {
        public Location first { get; set; }
        public Location second { get; set; }
    }

    private record PersonWithTwoLocationsRemapped : Person
    {
        public Location locationOne { get; set; }
        public Location locationTwo { get; set; }
    }

    private record FirstLevel
    {
        public Guid _id { get; set; }
        public List<SecondLevel> children { get; set; }
        public DayOfWeek day { get; set; }
        public Reference reference { get; set; }
    }

    private record SecondLevel
    {
        public List<ThirdLevel> children { get; set; }
        public DayOfWeek day { get; set; }
    }

    private record ThirdLevel
    {
        public string name { get; set; }
        public Reference reference { get; set; }
        public DayOfWeek day { get; set; }
    }

    private record Reference
    {
        public string name { get; set; }
        public DayOfWeek day { get; set; }
    }

    // Write-side shapes for OwnedEntity_nested_collection_is_empty_when_grandchild_array_missing/null: mirror
    // FirstLevel/SecondLevel but the SecondLevel-equivalent either omits its `children` element entirely
    // (SecondLevelMissingChildren) or is written with it explicitly null (SecondLevelNullChildren), so the
    // stored document's grandchild array is absent/null exactly as MissingNullableCollection does for the
    // root-level case above. Read back through FirstLevel/SecondLevel/ThirdLevel, unchanged.
    private record FirstLevelWithMissingGrandchildren
    {
        public Guid _id { get; set; }
        public List<SecondLevelMissingChildren> children { get; set; }
        public DayOfWeek day { get; set; }
        public Reference reference { get; set; }
    }

    private record SecondLevelMissingChildren
    {
        public DayOfWeek day { get; set; }
    }

    private record FirstLevelWithNullGrandchildren
    {
        public Guid _id { get; set; }
        public List<SecondLevelNullChildren> children { get; set; }
        public DayOfWeek day { get; set; }
        public Reference reference { get; set; }
    }

    private record SecondLevelNullChildren
    {
        public List<ThirdLevel> children { get; set; }
        public DayOfWeek day { get; set; }
    }

    private record SimpleOwner
    {
        public ObjectId Id { get; set; }
        public List<SimpleOwned> Children { get; set; }
    }

    private record SimpleOwned
    {
        public string Name { get; set; }
    }

    private static readonly City City1 = new() { name = "San Diego" };

    private static readonly LocationWithCity LocationWithCity1 =
        new() { latitude = 32.715736m, longitude = -117.161087m, city = City1 };

    private static readonly PersonWithCity[] PersonWithCity1 =
    [
        new() { name = "Carmen", location = LocationWithCity1 }
    ];

    private static readonly Location Location1 = new() { latitude = 32.715736m, longitude = -117.161087m };

    private static readonly PersonWithLocation[] PersonWithLocation1 =
    [
        new() { name = "Carmen", location = Location1 }
    ];

    private static readonly PersonWithLocation[] Person2WithLocation1 =
    [
        new() { name = "Milton", location = Location1 }
    ];

    private static readonly PersonWithLocation[] PersonWithMissingLocation1 =
    [
        new() { name = "Elizabeth" }
    ];

    private static readonly Location Location2 = new() { latitude = 49.45981m, longitude = -2.53527m };

    private static readonly Location Location3 = new() { latitude = 40.1m, longitude = -1.1m };

    private static readonly PersonWithMultipleLocations[] PersonWithLocations1 =
    [
        new() { name = "Damien", locations = [Location2, Location1] },
        new() { name = "Carmen", locations = [Location3] },
    ];

    private static readonly PersonWithTwoLocations[] PersonWithTwoLocations1 =
    [
        new() { name = "Henry", first = Location1, second = Location2 }
    ];

    private static readonly FirstLevel FirstLevel1 = new()
    {
        _id = Guid.NewGuid(),
        day = DayOfWeek.Monday,
        reference = new() { day = DayOfWeek.Friday, name = "This is the first level name" },
        children =
        [
            new SecondLevel
            {
                day = DayOfWeek.Tuesday,
                children =
                [
                    new()
                    {
                        name = "This is the third level name",
                        day = DayOfWeek.Wednesday,
                        reference = new() { name = "This is the item reference name", day = DayOfWeek.Thursday }
                    }
                ]
            }
        ]
    };
}
