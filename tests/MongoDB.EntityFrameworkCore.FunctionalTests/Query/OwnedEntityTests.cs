﻿/* Copyright 2023-present MongoDB Inc.
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

using Microsoft.EntityFrameworkCore.Infrastructure;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Extensions;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

[XUnitCollection(nameof(SampleGuidesFixture))]
public class OwnedEntityTests : IClassFixture<TemporaryDatabaseFixture>
{
    private readonly TemporaryDatabaseFixture _tempDatabase;

    public OwnedEntityTests(TemporaryDatabaseFixture tempDatabase) => _tempDatabase = tempDatabase;

    [Fact]
    public void OwnedEntity_nested_one_level_materializes_single()
    {
        var collection = _tempDatabase.CreateTemporaryCollection<PersonWithLocation>();
        collection.WriteTestDocs(PersonWithLocation1);
        SingleEntityDbContext<PersonWithLocation> db = SingleEntityDbContext.Create(collection);

        var actual = db.Entitites.Single();

        Assert.Equal("Carmen", actual.name);
        Assert.Equal(Location1.latitude, actual.location.latitude);
        Assert.Equal(Location1.longitude, actual.location.longitude);
    }

    [Fact]
    public void OwnedEntity_missing_document_element_does_not_throw()
    {
        _tempDatabase.CreateTemporaryCollection<Person>("personNoLocation").WriteTestDocs([
            new Person
            {
                name = "Bill"
            }
        ]);

        var collection = _tempDatabase.MongoDatabase.GetCollection<PersonWithOptionalLocation>("personNoLocation");
        var db = SingleEntityDbContext.Create(collection);

        var person = db.Entitites.First();
        Assert.NotNull(person);
        Assert.Equal("Bill", person.name);
        Assert.Null(person.location);
    }

    [Fact]
    public void OwnedEntity_nested_one_level_allows_nested_where()
    {
        var collection = _tempDatabase.CreateTemporaryCollection<PersonWithLocation>();
        collection.WriteTestDocs(PersonWithLocation1);
        SingleEntityDbContext<PersonWithLocation> db = SingleEntityDbContext.Create(collection);

        var actual = db.Entitites.First(e => e.location.latitude > 0.00m);

        Assert.Equal("Carmen", actual.name);
        Assert.Equal(Location1.latitude, actual.location.latitude);
        Assert.Equal(Location1.longitude, actual.location.longitude);
    }

    [Fact]
    public void OwnedEntity_nested_two_levels_allows_nested_where()
    {
        var collection = _tempDatabase.CreateTemporaryCollection<PersonWithCity>();
        collection.WriteTestDocs(PersonWithCity1);
        SingleEntityDbContext<PersonWithCity> db = SingleEntityDbContext.Create(collection);

        var actual = db.Entitites.First(e => e.location.city.name == "San Diego");

        Assert.Equal("Carmen", actual.name);
        Assert.Equal(Location1.latitude, actual.location.latitude);
        Assert.Equal(Location1.longitude, actual.location.longitude);
    }

    [Fact]
    public void OwnedEntity_materializes_when_missing_non_required_owned_entity()
    {
        var collection = _tempDatabase.CreateTemporaryCollection<PersonWithLocation>();
        collection.WriteTestDocs(PersonWithMissingLocation1);
        SingleEntityDbContext<PersonWithLocation> db = SingleEntityDbContext.Create(collection,
            mb => { mb.Entity<PersonWithLocation>().Navigation(p => p.location).IsRequired(false); });

        List<PersonWithLocation> actual = db.Entitites.Where(p => p.name == "Elizabeth").ToList();

        Assert.NotEmpty(actual);
        Assert.Equal("Elizabeth", actual[0].name);
        Assert.Null(actual[0].location);
    }

    [Fact]
    public void OwnedEntity_throws_when_missing_required_owned_entity()
    {
        var collection = _tempDatabase.CreateTemporaryCollection<PersonWithLocation>();
        collection.WriteTestDocs(PersonWithMissingLocation1);
        SingleEntityDbContext<PersonWithLocation> db = SingleEntityDbContext.Create(collection,
            mb => { mb.Entity<PersonWithLocation>().Navigation(p => p.location).IsRequired(); });

        var ex = Assert.Throws<InvalidOperationException>(() => db.Entitites.Where(p => p.name != "bob").ToList());
        Assert.Contains(nameof(PersonWithLocation), ex.Message);
        Assert.Contains(nameof(PersonWithLocation.location), ex.Message);
    }

    [Fact]
    public void OwnedEntity_nested_one_level_materializes_many()
    {
        var collection = _tempDatabase.CreateTemporaryCollection<PersonWithLocation>();
        collection.WriteTestDocs(PersonWithLocation1);
        SingleEntityDbContext<PersonWithLocation> db = SingleEntityDbContext.Create(collection);

        List<PersonWithLocation> actual = db.Entitites.Where(p => p.name != "bob").ToList();

        Assert.NotEmpty(actual);

        Assert.Equal("Carmen", actual[0].name);
        Assert.Equal(Location1.latitude, actual[0].location.latitude);
        Assert.Equal(Location1.longitude, actual[0].location.longitude);
    }

    [Fact]
    public void OwnedEntity_nested_one_level_creates()
    {
        var collection = _tempDatabase.CreateTemporaryCollection<PersonWithLocation>();
        var expected =
            new PersonWithLocation
            {
                name = "Charlie",
                location = new Location
                {
                    latitude = 1.234m, longitude = 1.567m
                }
            };

        {
            SingleEntityDbContext<PersonWithLocation> db = SingleEntityDbContext.Create(collection);
            db.Entitites.Add(expected);
            db.SaveChanges();
        }

        {
            SingleEntityDbContext<PersonWithLocation> db = SingleEntityDbContext.Create(collection);
            var actual = db.Entitites.First(p => p.name == "Charlie");

            Assert.Equal(expected.name, actual.name);
            Assert.Equal(expected.location.latitude, actual.location.latitude);
            Assert.Equal(expected.location.longitude, actual.location.longitude);
        }
    }

    [Fact]
    public void OwnedEntity_collection_creates()
    {
        var collection =
            _tempDatabase.CreateTemporaryCollection<PersonWithMultipleLocations>();

        PersonWithMultipleLocations expected = new()
        {
            _id = ObjectId.GenerateNewId(),
            name = "Alfred",
            locations =
            [
                new()
                {
                    latitude = 1.234m, longitude = 1.567m
                },

                new()
                {
                    latitude = 5.1m, longitude = 3.9m
                }
            ]
        };

        {
            SingleEntityDbContext<PersonWithMultipleLocations> db = SingleEntityDbContext.Create(collection);
            db.Entitites.Add(expected);
            db.SaveChanges();
        }

        {
            SingleEntityDbContext<PersonWithMultipleLocations> db = SingleEntityDbContext.Create(collection);
            var actual = db.Entitites.First(p => p.name == "Alfred");

            Assert.Equal(expected.name, actual.name);
            Assert.Equal(expected.locations[0].latitude, actual.locations[0].latitude);
            Assert.Equal(expected.locations[0].longitude, actual.locations[0].longitude);
            Assert.Equal(expected.locations[1].latitude, actual.locations[1].latitude);
            Assert.Equal(expected.locations[1].longitude, actual.locations[1].longitude);
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

    [Fact]
    public void OwnedEntity_non_nullable_collection_is_empty_when_empty()
    {
        var collection = _tempDatabase.CreateTemporaryCollection<SimpleNonNullableCollection>();
        collection.WriteTestDocs([
            new SimpleNonNullableCollection
            {
                children = []
            }
        ]);
        var db = SingleEntityDbContext.Create(collection);

        var actual = db.Entitites.First();
        Assert.Empty(actual.children);
    }

    [Fact]
    public void OwnedEntity_nullable_collection_is_empty_when_empty()
    {
        var collection = _tempDatabase.CreateTemporaryCollection<SimpleNullableCollection>();
        collection.WriteTestDocs([
            new SimpleNullableCollection
            {
                children = []
            }
        ]);
        var db = SingleEntityDbContext.Create(collection);

        var actual = db.Entitites.First();
        Assert.NotNull(actual.children);
        Assert.Empty(actual.children);
    }

    [Fact]
    public void OwnedEntity_non_nullable_collection_is_null_when_null()
    {
        var collection = _tempDatabase.CreateTemporaryCollection<SimpleNonNullableCollection>();
        collection.WriteTestDocs([
            new SimpleNonNullableCollection
            {
                children = null
            }
        ]);
        var db = SingleEntityDbContext.Create(collection);

        var actual = db.Entitites.First();
        Assert.Null(actual.children);
    }

    [Fact]
    public void OwnedEntity_nullable_collection_is_null_when_null()
    {
        var collection = _tempDatabase.CreateTemporaryCollection<SimpleNullableCollection>();
        collection.WriteTestDocs([
            new SimpleNullableCollection
            {
                children = null
            }
        ]);
        var db = SingleEntityDbContext.Create(collection);

        var actual = db.Entitites.First();
        Assert.Null(actual.children);
    }

    [Fact]
    public void OwnedEntity_non_nullable_collection_throws_when_missing()
    {
        var collection = _tempDatabase.CreateTemporaryCollection<MissingNullableCollection>();
        collection.WriteTestDocs([new MissingNullableCollection()]);
        var db = SingleEntityDbContext.Create<MissingNullableCollection, SimpleNullableCollection>(collection);

        var ex = Assert.Throws<InvalidOperationException>(() => db.Entitites.First());
        Assert.Contains(nameof(SimpleNullableCollection.children), ex.Message);
    }

    [Fact]
    public void OwnedEntity_nullable_collection_throws_when_missing()
    {
        var collection = _tempDatabase.CreateTemporaryCollection<MissingNullableCollection>();
        collection.WriteTestDocs([new MissingNullableCollection()]);
        var db = SingleEntityDbContext.Create<MissingNullableCollection, SimpleNullableCollection>(collection);

        var ex = Assert.Throws<InvalidOperationException>(() => db.Entitites.First());
        Assert.Contains(nameof(SimpleNullableCollection.children), ex.Message);
    }

    [Fact]
    public void OwnedEntity_nested_two_levels_materializes_single()
    {
        var collection = _tempDatabase.CreateTemporaryCollection<PersonWithCity>();
        collection.WriteTestDocs(PersonWithCity1);
        SingleEntityDbContext<PersonWithCity> db = SingleEntityDbContext.Create(collection);

        var actual = db.Entitites.Single();

        Assert.Equal("Carmen", actual.name);
        Assert.Equal(Location1.latitude, actual.location.latitude);
        Assert.Equal(Location1.longitude, actual.location.longitude);
        Assert.Equal(City1.name, actual.location.city.name);
    }

    [Fact]
    public void OwnedEntity_nested_two_levels_materializes_many()
    {
        var collection = _tempDatabase.CreateTemporaryCollection<PersonWithCity>();
        collection.WriteTestDocs(PersonWithCity1);
        SingleEntityDbContext<PersonWithCity> db = SingleEntityDbContext.Create(collection);

        List<PersonWithCity> actual = db.Entitites.Where(p => p.name != "bob").ToList();

        Assert.NotEmpty(actual);

        Assert.Equal("Carmen", actual[0].name);
        Assert.Equal(Location1.latitude, actual[0].location.latitude);
        Assert.Equal(Location1.longitude, actual[0].location.longitude);
        Assert.Equal(City1.name, actual[0].location.city.name);
    }

    [Fact]
    public void OwnedEntity_with_collection_materializes_many()
    {
        var collection =
            _tempDatabase.CreateTemporaryCollection<PersonWithMultipleLocations>();
        collection.WriteTestDocs(PersonWithLocations1);
        SingleEntityDbContext<PersonWithMultipleLocations> db = SingleEntityDbContext.Create(collection);

        List<PersonWithMultipleLocations> actual = db.Entitites.Where(p => p.name != "bob").ToList();

        Assert.NotEmpty(actual);

        Assert.Equal("Damien", actual[0].name);
        Assert.Equal(2, actual[0].locations.Count);

        Assert.Single(actual[0].locations, s => Location1.latitude == s.latitude && Location1.longitude == s.longitude);
        Assert.Single(actual[0].locations, s => Location2.latitude == s.latitude && Location2.longitude == s.longitude);
    }

    [Fact]
    public void OwnedEntity_with_ienumerable_collection_materializes_many()
    {
        var expectedLocation = new Location
        {
            latitude = 1.01m, longitude = 1.02m
        };
        var collection = _tempDatabase.CreateTemporaryCollection<PersonWithIEnumerableLocations>();
        collection.WriteTestDocs(new PersonWithIEnumerableLocations[]
        {
            new()
            {
                _id = ObjectId.GenerateNewId(),
                name = "IEnumerableRound1",
                locations = new List<Location>
                {
                    expectedLocation
                }
            },
            new()
            {
                _id = ObjectId.GenerateNewId(),
                name = "IEnumerableRound2",
                locations = new List<Location>
                {
                    new()
                    {
                        latitude = 1.03m, longitude = 1.04m
                    }
                }
            }
        });

        var actual = SingleEntityDbContext.Create(collection).Entitites.ToList();

        Assert.NotEmpty(actual);
        Assert.Equal("IEnumerableRound1", actual[0].name);
        Assert.Equal(2, actual.Count);
        var actualLocation = Assert.Single(actual[0].locations);
        Assert.Equal(expectedLocation, actualLocation);
    }

    [Fact]
    public void OwnedEntity_with_ienumerable_list_serializes()
    {
        var collection = _tempDatabase.CreateTemporaryCollection<PersonWithIEnumerableLocations>();
        var entity = new PersonWithIEnumerableLocations
        {
            _id = ObjectId.GenerateNewId(),
            name = "IEnumerableSerialize",
            locations = new List<Location>
            {
                Location1, Location2
            }
        };

        {
            var db = SingleEntityDbContext.Create(collection);
            db.Entitites.Add(entity);
            db.SaveChanges();
        }

        {
            var db = SingleEntityDbContext.Create(collection);
            var actual = db.Entitites.FirstOrDefault();

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
        var collection = _tempDatabase.CreateTemporaryCollection<PersonWithIEnumerableLocations>();
        var db = SingleEntityDbContext.Create(collection);

        var entity = new PersonWithIEnumerableLocations
        {
            _id = ObjectId.GenerateNewId(),
            name = "IEnumerableSerialize",
            locations = EnumerableOnlyWrapper.Wrap(new List<Location>
            {
                Location1, Location2
            })
        };

        var ex = Assert.Throws<InvalidOperationException>(() => db.Entitites.Add(entity));
        Assert.Contains(nameof(PersonWithIEnumerableLocations.locations), ex.Message);
        Assert.Contains(entity.locations.GetType().ShortDisplayName(), ex.Message);
    }

    [Fact]
    public void OwnedEntity_with_collection_adjusted_correctly()
    {
        var collection = _tempDatabase.CreateTemporaryCollection<PersonWithMultipleLocations>();

        {
            var db = SingleEntityDbContext.Create(collection);

            var original = new PersonWithMultipleLocations
            {
                _id = ObjectId.GenerateNewId(),
                name = "Many updates",
                locations =
                [
                    new Location
                    {
                        latitude = 1.1m, longitude = 2.2m
                    }
                ]
            };

            db.Add(original);
            db.SaveChanges();
            Assert.Single(original.locations, l => l.latitude == 1.1m);

            original.locations.Add(new()
            {
                latitude = 3.3m, longitude = 4.4m
            });
            db.SaveChanges();

            Assert.Equal(2, original.locations.Count);
        }

        {
            var db = SingleEntityDbContext.Create(collection);

            var found = db.Entitites.Single();
            Assert.Equal(2, found.locations.Count);

            found.locations.RemoveAt(0);
            db.SaveChanges();

            Assert.Single(found.locations, l => l.longitude == 4.4m);

            found.locations.Clear();
            db.SaveChanges();
        }

        {
            var db = SingleEntityDbContext.Create(collection);
            var found = db.Entitites.Single();

            Assert.Empty(found.locations);
        }
    }

    [Fact]
    public void OwnedEntity_with_two_owned_entities_materializes()
    {
        var collection = _tempDatabase.CreateTemporaryCollection<PersonWithTwoLocations>();
        var expected = PersonWithTwoLocations1[0];
        collection.WriteTestDocs(PersonWithTwoLocations1);
        SingleEntityDbContext<PersonWithTwoLocations> db = SingleEntityDbContext.Create(collection);

        var actual = db.Entitites.FirstOrDefault();

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
        var collection = _tempDatabase.CreateTemporaryCollection<PersonWithTwoLocations>();
        PersonWithTwoLocations expected = new()
        {
            name = "Elizabeth", first = Location2, second = Location1
        };

        {
            SingleEntityDbContext<PersonWithTwoLocations> db = SingleEntityDbContext.Create(collection);
            db.Entitites.Add(expected);
            db.SaveChanges();
        }

        {
            SingleEntityDbContext<PersonWithTwoLocations> db = SingleEntityDbContext.Create(collection);
            var actual = db.Entitites.FirstOrDefault();

            Assert.NotNull(actual);
            Assert.Equal(expected.name, actual.name);
            Assert.Equal(expected.first.latitude, actual.first.latitude);
            Assert.Equal(expected.first.longitude, actual.first.longitude);
            Assert.Equal(expected.second.latitude, actual.second.latitude);
            Assert.Equal(expected.second.longitude, actual.second.longitude);
        }
    }

    [Fact]
    public void OwnedEntity_can_have_element_name_set()
    {
        var collection = _tempDatabase.CreateTemporaryCollection<PersonWithLocation>();
        var expected = PersonWithLocation1[0];
        collection.WriteTestDocs(PersonWithLocation1);
        SingleEntityDbContext<PersonWithLocation> db = SingleEntityDbContext.Create(collection,
            mb => { mb.Entity<PersonWithLocation>().OwnsOne(p => p.location, r => r.HasElementName("location")); });

        var actual = db.Entitites.FirstOrDefault();

        Assert.NotNull(actual);
        Assert.Equal(expected.name, actual.name);
        Assert.Equal(expected.location.latitude, actual.location.latitude);
        Assert.Equal(expected.location.longitude, actual.location.longitude);
    }

    private class Person
    {
        public ObjectId _id { get; set; }
        public string name { get; set; }
    }

    private class PersonWithOptionalLocation : Person
    {
        public Location? location { get; set; }
    }

    private class PersonWithLocation : Person
    {
        public Location location { get; set; }
    }

    private class PersonWithCity : Person
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

    private class City
    {
        public string name { get; set; }
    }

    private class PersonWithMultipleLocations : Person
    {
        public List<Location> locations { get; set; }
    }

    private class PersonWithIEnumerableLocations : Person
    {
        public IEnumerable<Location> locations { get; set; }
    }

    private class PersonWithTwoLocations : Person
    {
        public Location first { get; set; }
        public Location second { get; set; }
    }

    private static readonly City City1 = new()
    {
        name = "San Diego"
    };

    private static readonly LocationWithCity LocationWithCity1 =
        new()
        {
            latitude = 32.715736m, longitude = -117.161087m, city = City1
        };

    private static readonly PersonWithCity[] PersonWithCity1 =
    [
        new()
        {
            name = "Carmen", location = LocationWithCity1
        }
    ];

    private static readonly Location Location1 = new()
    {
        latitude = 32.715736m, longitude = -117.161087m
    };

    private static readonly PersonWithLocation[] PersonWithLocation1 =
    [
        new()
        {
            name = "Carmen", location = Location1
        }
    ];

    private static readonly PersonWithLocation[] PersonWithMissingLocation1 =
    [
        new()
        {
            name = "Elizabeth"
        }
    ];

    private static readonly Location Location2 = new()
    {
        latitude = 49.45981m, longitude = -2.53527m
    };

    private static readonly PersonWithMultipleLocations[] PersonWithLocations1 =
    [
        new()
        {
            name = "Damien", locations = [Location2, Location1]
        }
    ];

    private static readonly PersonWithTwoLocations[] PersonWithTwoLocations1 =
    [
        new()
        {
            name = "Henry", first = Location1, second = Location2
        }
    ];
}
