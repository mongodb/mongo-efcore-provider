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
using MongoDB.Driver;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

public class OwnedEntityTests : IClassFixture<TemporaryDatabaseFixture>
{
    private static readonly City __city = new() {name = "San Diego"};

    private static readonly LocationWithCity __locationWithCity =
        new() {latitude = 32.715736m, longitude = -117.161087m, city = __city};

    private static readonly PersonWithCity[] __peopleWithCity = {new() {name = "Carmen", location = __locationWithCity}};

    private static readonly Location __location1 = new() {latitude = 32.715736m, longitude = -117.161087m};
    private static readonly PersonWithLocation[] __peopleWithLocation = {new() {name = "Carmen", location = __location1}};

    private static readonly Location __location2 = new() {latitude = 49.45981m, longitude =  -2.53527m};
    private static readonly PersonWithMultipleLocations[] __peopleWithLocations = {new() {name = "Damien", locations = new() {__location2, __location1}}};

    private readonly TemporaryDatabaseFixture _tempDatabase;

    public OwnedEntityTests(TemporaryDatabaseFixture tempDatabase)
    {
        _tempDatabase = tempDatabase;
    }

    [Fact]
    public void OwnedEntity_nested_one_level_materializes_single()
    {
        var collection = _tempDatabase.CreateTemporaryCollection<PersonWithLocation>();
        collection.BulkWrite(__peopleWithLocation.Select(p => new InsertOneModel<PersonWithLocation>(p)));
        var db = SingleEntityDbContext.Create(collection);

        var actual = db.Entitites.Single();

        Assert.Equal("Carmen", actual.name);
        Assert.Equal(__location1.latitude, actual.location.latitude);
        Assert.Equal(__location1.longitude, actual.location.longitude);
    }

    [Fact]
    public void OwnedEntity_nested_one_level_materializes_many()
    {
        var collection = _tempDatabase.CreateTemporaryCollection<PersonWithLocation>();
        collection.BulkWrite(__peopleWithLocation.Select(p => new InsertOneModel<PersonWithLocation>(p)));
        var db = SingleEntityDbContext.Create(collection);

        var actual = db.Entitites.Where(p => p.name != "bob").ToList();

        Assert.NotEmpty(actual);

        Assert.Equal("Carmen", actual[0].name);
        Assert.Equal(__location1.latitude, actual[0].location.latitude);
        Assert.Equal(__location1.longitude, actual[0].location.longitude);
    }

    [Fact]
    public void OwnedEntity_nested_two_levels_materializes_single()
    {
        var collection = _tempDatabase.CreateTemporaryCollection<PersonWithCity>();
        collection.BulkWrite(__peopleWithCity.Select(p => new InsertOneModel<PersonWithCity>(p)));
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
        collection.BulkWrite(__peopleWithCity.Select(p => new InsertOneModel<PersonWithCity>(p)));
        var db = SingleEntityDbContext.Create(collection);

        var actual = db.Entitites.Where(p => p.name != "bob").ToList();

        Assert.NotEmpty(actual);

        Assert.Equal("Carmen", actual[0].name);
        Assert.Equal(__location1.latitude, actual[0].location.latitude);
        Assert.Equal(__location1.longitude, actual[0].location.longitude);
        Assert.Equal(__city.name, actual[0].location.city.name);
    }

    [Fact(Skip = "Collection navigations not yet supported")]
    public void OwnedEntity_with_collection_materializes_many()
    {
        var collection = _tempDatabase.CreateTemporaryCollection<PersonWithMultipleLocations>();
        collection.BulkWrite(__peopleWithLocations.Select(p => new InsertOneModel<PersonWithMultipleLocations>(p)));
        var db = SingleEntityDbContext.Create(collection);

        var actual = db.Entitites.Where(p => p.name != "bob").ToList();

        Assert.NotEmpty(actual);

        Assert.Equal("Damien", actual[0].name);
        Assert.Equal(2, actual[0].locations.Count);

        Assert.Single(actual[0].locations, s => __location1.latitude == s.latitude && __location1.longitude == s.longitude);
        Assert.Single(actual[0].locations, s => __location2.latitude == s.latitude && __location2.longitude == s.longitude);
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
}
