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

using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Extensions;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

[XUnitCollection(nameof(SampleGuidesFixture))]
public class OwnedEntityTests : IClassFixture<TemporaryDatabaseFixture>
{
    private readonly TemporaryDatabaseFixture _tempDatabase;

    public OwnedEntityTests(TemporaryDatabaseFixture tempDatabase)
    {
        _tempDatabase = tempDatabase;
    }

    [Fact]
    public void OwnedEntity_nested_one_level_materializes_single()
    {
        var collection = _tempDatabase.CreateTemporaryCollection<PersonWithLocation>();
        collection.WriteTestDocs(__personWithLocation);
        var db = SingleEntityDbContext.Create(collection);

        var actual = db.Entitites.Single();

        Assert.Equal("Carmen", actual.name);
        Assert.Equal(__location1.latitude, actual.location.latitude);
        Assert.Equal(__location1.longitude, actual.location.longitude);
    }

    [Fact]
    public void OwnedEntity_nested_one_level_allows_nested_where()
    {
        var collection = _tempDatabase.CreateTemporaryCollection<PersonWithLocation>();
        collection.WriteTestDocs(__personWithLocation);
        var db = SingleEntityDbContext.Create(collection);

        var actual = db.Entitites.First(e => e.location.latitude > 0.00m);

        Assert.Equal("Carmen", actual.name);
        Assert.Equal(__location1.latitude, actual.location.latitude);
        Assert.Equal(__location1.longitude, actual.location.longitude);
    }

    [Fact]
    public void OwnedEntity_nested_two_levels_allows_nested_where()
    {
        var collection = _tempDatabase.CreateTemporaryCollection<PersonWithCity>();
        collection.WriteTestDocs(__personWithCity);
        var db = SingleEntityDbContext.Create(collection);

        var actual = db.Entitites.First(e => e.location.city.name == "San Diego");

        Assert.Equal("Carmen", actual.name);
        Assert.Equal(__location1.latitude, actual.location.latitude);
        Assert.Equal(__location1.longitude, actual.location.longitude);
    }

    [Fact]
    public void OwnedEntity_nested_one_level_materializes_many()
    {
        var collection = _tempDatabase.CreateTemporaryCollection<PersonWithLocation>();
        collection.WriteTestDocs(__personWithLocation);
        var db = SingleEntityDbContext.Create(collection);

        var actual = db.Entitites.Where(p => p.name != "bob").ToList();

        Assert.NotEmpty(actual);

        Assert.Equal("Carmen", actual[0].name);
        Assert.Equal(__location1.latitude, actual[0].location.latitude);
        Assert.Equal(__location1.longitude, actual[0].location.longitude);
    }

    [Fact]
    public void OwnedEntity_nested_one_level_creates()
    {
        var collection = _tempDatabase.CreateTemporaryCollection<PersonWithLocation>();
        var expected = new PersonWithLocation {name = "Charlie", location = new() {latitude = 1.234m, longitude = 1.567m}};

        {
            var db = SingleEntityDbContext.Create(collection);
            db.Entitites.Add(expected);
            db.SaveChanges();
        }

        {
            var db = SingleEntityDbContext.Create(collection);
            var actual = db.Entitites.First(p => p.name == "Charlie");

            Assert.Equal(expected.name, actual.name);
            Assert.Equal(expected.location.latitude, actual.location.latitude);
            Assert.Equal(expected.location.longitude, actual.location.longitude);
        }
    }

    [Fact]
    public void OwnedEntity_collection_creates()
    {
        var collection = _tempDatabase.CreateTemporaryCollection<PersonWithMultipleLocations>();

        var expected = new PersonWithMultipleLocations
        {
            _id = ObjectId.GenerateNewId(),
            name = "Alfred",
            locations = new List<Location>
            {
                new() {latitude = 1.234m, longitude = 1.567m}, new() {latitude = 5.1m, longitude = 3.9m}
            }
        };

        {
            var db = SingleEntityDbContext.Create(collection);
            db.Entitites.Add(expected);
            db.SaveChanges();
        }

        {
            var db = SingleEntityDbContext.Create(collection);
            var actual = db.Entitites.First(p => p.name == "Alfred");

            Assert.Equal(expected.name, actual.name);
            Assert.Equal(expected.locations[0].latitude, actual.locations[0].latitude);
            Assert.Equal(expected.locations[0].longitude, actual.locations[0].longitude);
            Assert.Equal(expected.locations[1].latitude, actual.locations[1].latitude);
            Assert.Equal(expected.locations[1].longitude, actual.locations[1].longitude);
        }
    }

    [Fact]
    public void OwnedEntity_nested_two_levels_materializes_single()
    {
        var collection = _tempDatabase.CreateTemporaryCollection<PersonWithCity>();
        collection.WriteTestDocs(__personWithCity);
        var db = SingleEntityDbContext.Create(collection);

        var actual = db.Entitites.Single();

        Assert.Equal("Carmen", actual.name);
        Assert.Equal(__location1.latitude, actual.location.latitude);
        Assert.Equal(__location1.longitude, actual.location.longitude);
        Assert.Equal(__city.name, actual.location.city.name);
    }

    [Fact]
    public void OwnedEntity_nested_two_levels_materializes_many()
    {
        var collection = _tempDatabase.CreateTemporaryCollection<PersonWithCity>();
        collection.WriteTestDocs(__personWithCity);
        var db = SingleEntityDbContext.Create(collection);

        var actual = db.Entitites.Where(p => p.name != "bob").ToList();

        Assert.NotEmpty(actual);

        Assert.Equal("Carmen", actual[0].name);
        Assert.Equal(__location1.latitude, actual[0].location.latitude);
        Assert.Equal(__location1.longitude, actual[0].location.longitude);
        Assert.Equal(__city.name, actual[0].location.city.name);
    }

    [Fact]
    public void OwnedEntity_with_collection_materializes_many()
    {
        var collection = _tempDatabase.CreateTemporaryCollection<PersonWithMultipleLocations>();
        collection.WriteTestDocs(__personWithLocations);
        var db = SingleEntityDbContext.Create(collection);

        var actual = db.Entitites.Where(p => p.name != "bob").ToList();

        Assert.NotEmpty(actual);

        Assert.Equal("Damien", actual[0].name);
        Assert.Equal(2, actual[0].locations.Count);

        Assert.Single(actual[0].locations, s => __location1.latitude == s.latitude && __location1.longitude == s.longitude);
        Assert.Single(actual[0].locations, s => __location2.latitude == s.latitude && __location2.longitude == s.longitude);
    }

    [Fact]
    public void OwnedEntity_with_two_owned_entities_materializes()
    {
        var collection = _tempDatabase.CreateTemporaryCollection<PersonWithTwoLocations>();
        var expected = __personWithTwoLocations[0];
        collection.WriteTestDocs(__personWithTwoLocations);
        var db = SingleEntityDbContext.Create(collection);

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
        var expected = new PersonWithTwoLocations {name = "Elizabeth", first = __location2, second = __location1};

        {
            var db = SingleEntityDbContext.Create(collection);
            db.Entitites.Add(expected);
            db.SaveChanges();
        }

        {
            var db = SingleEntityDbContext.Create(collection);
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
        var expected = __personWithLocation[0];
        collection.WriteTestDocs(__personWithLocation);
        var db = SingleEntityDbContext.Create(collection,
            mb => { mb.Entity<PersonWithLocation>().OwnsOne(p => p.location, r => r.HasElementName("location")); });

        var actual = db.Entitites.FirstOrDefault();

        Assert.NotNull(actual);
        Assert.Equal(expected.name, actual.name);
        Assert.Equal(expected.location.latitude, actual.location.latitude);
        Assert.Equal(expected.location.longitude, actual.location.longitude);
    }

    class Person
    {
        public ObjectId _id { get; set; }
        public string name { get; set; }
    }

    class PersonWithLocation : Person
    {
        public Location location { get; set; }
    }

    class PersonWithCity : Person
    {
        public LocationWithCity location { get; set; }
    }

    class Location
    {
        public decimal latitude { get; set; }
        public decimal longitude { get; set; }
    }

    class LocationWithCity : Location
    {
        public City city { get; set; }
    }

    class City
    {
        public string name { get; set; }
    }

    class PersonWithMultipleLocations : Person
    {
        public List<Location> locations { get; set; }
    }

    class PersonWithTwoLocations : Person
    {
        public Location first { get; set; }
        public Location second { get; set; }
    }

    private static readonly City __city = new() {name = "San Diego"};

    private static readonly LocationWithCity __locationWithCity =
        new() {latitude = 32.715736m, longitude = -117.161087m, city = __city};

    private static readonly PersonWithCity[] __personWithCity = {new() {name = "Carmen", location = __locationWithCity}};

    private static readonly Location __location1 = new() {latitude = 32.715736m, longitude = -117.161087m};
    private static readonly PersonWithLocation[] __personWithLocation = {new() {name = "Carmen", location = __location1}};

    private static readonly Location __location2 = new() {latitude = 49.45981m, longitude = -2.53527m};

    private static readonly PersonWithMultipleLocations[] __personWithLocations =
    {
        new() {name = "Damien", locations = new List<Location> {__location2, __location1}}
    };

    private static readonly PersonWithTwoLocations[] __personWithTwoLocations =
    {
        new() {name = "Henry", first = __location1, second = __location2}
    };
}
