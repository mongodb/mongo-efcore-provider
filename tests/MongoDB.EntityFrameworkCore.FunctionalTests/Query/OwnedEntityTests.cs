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
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Infrastructure;
using MongoDB.Bson;
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
        using var db = SingleEntityDbContext.Create(database.GetCollection<PersonWithGetOnlyLocation>(), mb =>
        {
            mb.Entity<PersonWithGetOnlyLocation>().OwnsOne(p => p.location);
        });

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
        collection.WriteTestDocs([new PersonWithOptionalLocation {_id = ObjectId.GenerateNewId(), name = "Milton"}]);
        using var db = SingleEntityDbContext.Create(collection);

        var actual = db.Entities.Where(e => e.location == null).First();

        Assert.Equal("Milton", actual.name);
        Assert.Null(actual.location);
    }

    [Fact]
    public void OwnedEntity_nested_one_level_first_matching_location_throws()
    {
        var collection = database.CreateCollection<PersonWithLocation>();
        collection.WriteTestDocs(PersonWithLocation1);
        collection.WriteTestDocs(Person2WithLocation1);
        using var db = SingleEntityDbContext.Create(collection);

        var location = db.Entities.First(p => p.name == "Carmen").location;

        var ex = Assert.Throws<NotSupportedException>(() => db.Entities.First(p => p.location == location && p.name != "Carmen"));
        Assert.Contains(nameof(Location), ex.Message);
        Assert.Contains("unique fields", ex.Message);
    }

    [Fact]
    public void OwnedEntity_nested_one_level_first_no_matching_location_throws()
    {
        var collection = database.CreateCollection<PersonWithLocation>();
        collection.WriteTestDocs(PersonWithLocation1);
        collection.WriteTestDocs(Person2WithLocation1);
        using var db = SingleEntityDbContext.Create(collection);

        var location = db.Entities.First(p => p.name == "Carmen").location;

        var ex = Assert.Throws<NotSupportedException>(() => db.Entities.FirstOrDefault(p => p.location != location));
        Assert.Contains(nameof(Location), ex.Message);
        Assert.Contains("unique fields", ex.Message);
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
    public void OwnedEntity_missing_document_element_does_not_throw()
    {
        database.CreateCollection<Person>().WriteTestDocs([
            new Person {name = "Bill"}
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
        collection.WriteTestDocs([new Person {name = "Henry"}]);
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
            new PersonWithLocation {name = "Charlie", location = new Location {latitude = 1.234m, longitude = 1.567m}};

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
                new() {latitude = 1.234m, longitude = 1.567m},

                new() {latitude = 5.1m, longitude = 3.9m}
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
        var expectedLocation = new Location {latitude = 1.234m, longitude = 1.567m};

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
            dbContext.Entities.Add(new PersonWithLocation {_id = id, name = expectedName, location = expectedLocation});
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
        var expectedLocation = new Location {latitude = 1.234m, longitude = 1.567m};

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
            dbContext.Entities.Add(new PersonWithMultipleLocations {_id = id, name = expectedName, locations = [expectedLocation]});
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
            new SimpleNonNullableCollection {children = []}
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
            new SimpleNullableCollection {children = []}
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
    public void OwnedEntity_non_nullable_collection_is_null_when_null(QueryTrackingBehavior queryTrackingBehavior)
    {
        var collection = database.CreateCollection<SimpleNonNullableCollection>(values: queryTrackingBehavior);
        collection.WriteTestDocs([
            new SimpleNonNullableCollection {children = null!}
        ]);
        using var db = SingleEntityDbContext.Create(
            collection,
            optionsBuilderAction: x => x.UseQueryTrackingBehavior(queryTrackingBehavior));

        var actual = db.Entities.First();
        Assert.Null(actual.children);
    }

    [Theory]
    [InlineData(QueryTrackingBehavior.TrackAll)]
    [InlineData(QueryTrackingBehavior.NoTracking)]
    [InlineData(QueryTrackingBehavior.NoTrackingWithIdentityResolution)]
    public void OwnedEntity_nullable_collection_is_null_when_null(QueryTrackingBehavior queryTrackingBehavior)
    {
        var collection = database.CreateCollection<SimpleNullableCollection>(values: queryTrackingBehavior);
        collection.WriteTestDocs([
            new SimpleNullableCollection {children = null}
        ]);
        using var db = SingleEntityDbContext.Create(
            collection,
            optionsBuilderAction: x => x.UseQueryTrackingBehavior(queryTrackingBehavior));

        var actual = db.Entities.First();
        Assert.Null(actual.children);
    }

    [Theory]
    [InlineData(QueryTrackingBehavior.TrackAll)]
    [InlineData(QueryTrackingBehavior.NoTracking)]
    [InlineData(QueryTrackingBehavior.NoTrackingWithIdentityResolution)]
    public void OwnedEntity_non_nullable_collection_is_null_when_missing(QueryTrackingBehavior queryTrackingBehavior)
    {
        var collection = database.CreateCollection<MissingNullableCollection>(values: queryTrackingBehavior);
        collection.WriteTestDocs([new MissingNullableCollection()]);
        using var db = SingleEntityDbContext.Create<MissingNullableCollection, SimpleNonNullableCollection>(
            collection,
            optionsBuilderAction: x => x.UseQueryTrackingBehavior(queryTrackingBehavior));

        var actual = db.Entities.First();
        Assert.Null(actual.children);
    }

    [Theory]
    [InlineData(QueryTrackingBehavior.TrackAll)]
    [InlineData(QueryTrackingBehavior.NoTracking)]
    [InlineData(QueryTrackingBehavior.NoTrackingWithIdentityResolution)]
    public void OwnedEntity_nullable_collection_is_null_when_missing(QueryTrackingBehavior queryTrackingBehavior)
    {
        var collection = database.CreateCollection<MissingNullableCollection>(values: queryTrackingBehavior);
        collection.WriteTestDocs([new MissingNullableCollection()]);
        using var db = SingleEntityDbContext.Create<MissingNullableCollection, SimpleNullableCollection>(
            collection,
            optionsBuilderAction: x => x.UseQueryTrackingBehavior(queryTrackingBehavior));

        var actual = db.Entities.First();
        Assert.Null(actual.children);
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
        var expectedLocation = new Location {latitude = 1.01m, longitude = 1.02m};
        var collection = database.CreateCollection<PersonWithIEnumerableLocations>();
        collection.WriteTestDocs([
            new()
            {
                _id = ObjectId.GenerateNewId(),
                name = "IEnumerableRound1",
                locations = new List<Location> {expectedLocation}
            },
            new()
            {
                _id = ObjectId.GenerateNewId(),
                name = "IEnumerableRound2",
                locations = new List<Location> {new() {latitude = 1.03m, longitude = 1.04m}}
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
            _id = ObjectId.GenerateNewId(), name = "IEnumerableSerialize", locations = new List<Location> {Location1, Location2}
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
            locations = EnumerableOnlyWrapper.Wrap(new List<Location> {Location1, Location2})
        };

        var ex = Assert.Throws<InvalidOperationException>(() => db.Entities.Add(entity));
        Assert.Contains(nameof(PersonWithIEnumerableLocations.locations), ex.Message);
        Assert.Contains(entity.locations.GetType().ShortDisplayName(), ex.Message);
    }

    [Fact]
    public void OwnedEntity_with_collection_adjusted_correctly()
    {
        var collection = database.CreateCollection<PersonWithMultipleLocations>();

        {
            using var db = SingleEntityDbContext.Create(collection);

            var original = new PersonWithMultipleLocations
            {
                _id = ObjectId.GenerateNewId(),
                name = "Many updates",
                locations =
                [
                    new Location {latitude = 1.1m, longitude = 2.2m}
                ]
            };

            db.Add(original);
            db.SaveChanges();
            Assert.Single(original.locations, l => l.latitude == 1.1m);

            original.locations.Add(new() {latitude = 3.3m, longitude = 4.4m});
            db.SaveChanges();
            Assert.False(db.ChangeTracker.HasChanges());

            Assert.Equal(2, original.locations.Count);
        }

        {
            using var db = SingleEntityDbContext.Create(collection);

            var found = db.Entities.Single();
            Assert.Equal(2, found.locations.Count);

            found.locations.RemoveAt(0);
            db.SaveChanges();
            Assert.False(db.ChangeTracker.HasChanges());

            Assert.Single(found.locations, l => l.longitude == 4.4m);

            found.locations.Clear();
            db.SaveChanges();
            Assert.False(db.ChangeTracker.HasChanges());
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var found = db.Entities.Single();
        
            Assert.Empty(found.locations);
        }
    }

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
        PersonWithTwoLocations expected = new() {name = "Elizabeth", first = Location2, second = Location1};

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
        var expected = new PersonWithTwoLocationsRemapped {name = "Elizabeth", locationOne = Location1, locationTwo = Location2};

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

    [Fact]
    public void OwnedEntity_collection_can_be_tested_for_not_null()
    {
        var collection = database.CreateCollection<A>();
        var expected = new A {_id = "1", children = [new B {name = "child1"}, new B {name = "child2"}]};

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entities.AddRange(expected, new A {_id = "2", children = null!});
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
        var expected = new AField {_id = "1", children = [new B {name = "child1"}, new B {name = "child2"}]};

        {
            using var db = SingleEntityDbContext.Create(collection, mb => mb.Entity<AField>().OwnsMany(f => f.children));
            db.Entities.AddRange(expected, new AField {_id = "2", children = null!});
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
        var collection = database.CreateCollection<A>();
        var expected = new A {_id = "1"};

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entities.AddRange(expected, new A {_id = "2", children = [new B {name = "child1"}, new B {name = "child2"}]});
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
        var collection = database.CreateCollection<AField>();
        var expected = new AField {_id = "1"};

        {
            using var db = SingleEntityDbContext.Create(collection, mb => mb.Entity<AField>().OwnsMany(f => f.children));
            db.Entities.AddRange(expected, new AField {_id = "2", children = [new B {name = "child1"}, new B {name = "child2"}]});
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
                ["children"] = new BsonArray {new BsonDocument("name", "child1"), new BsonDocument("name", "child2")}
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

    private record SimpleOwner
    {
        public ObjectId Id { get; set; }
        public List<SimpleOwned> Children { get; set; }
    }

    private record SimpleOwned
    {
        public string Name { get; set; }
    }

    private static readonly City City1 = new() {name = "San Diego"};

    private static readonly LocationWithCity LocationWithCity1 =
        new() {latitude = 32.715736m, longitude = -117.161087m, city = City1};

    private static readonly PersonWithCity[] PersonWithCity1 =
    [
        new() {name = "Carmen", location = LocationWithCity1}
    ];

    private static readonly Location Location1 = new() {latitude = 32.715736m, longitude = -117.161087m};

    private static readonly PersonWithLocation[] PersonWithLocation1 =
    [
        new() {name = "Carmen", location = Location1}
    ];

    private static readonly PersonWithLocation[] Person2WithLocation1 =
    [
        new() {name = "Milton", location = Location1}
    ];

    private static readonly PersonWithLocation[] PersonWithMissingLocation1 =
    [
        new() {name = "Elizabeth"}
    ];

    private static readonly Location Location2 = new() {latitude = 49.45981m, longitude = -2.53527m};

    private static readonly Location Location3 = new() {latitude = 40.1m, longitude = -1.1m};

    private static readonly PersonWithMultipleLocations[] PersonWithLocations1 =
    [
        new() {name = "Damien", locations = [Location2, Location1]},
        new() {name = "Carmen", locations = [Location3]},
    ];

    private static readonly PersonWithTwoLocations[] PersonWithTwoLocations1 =
    [
        new() {name = "Henry", first = Location1, second = Location2}
    ];

    private static readonly FirstLevel FirstLevel1 = new()
    {
        _id = Guid.NewGuid(),
        day = DayOfWeek.Monday,
        reference = new() {day = DayOfWeek.Friday, name = "This is the first level name"},
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
                        reference = new() {name = "This is the item reference name", day = DayOfWeek.Thursday}
                    }
                ]
            }
        ]
    };
}
