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

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Update;

[XUnitCollection("UpdateTests")]
public class OwnedNavigationPropertyCrudTests : IClassFixture<TemporaryDatabaseFixture>
{
    private readonly TemporaryDatabaseFixture _tempDatabase;

    public OwnedNavigationPropertyCrudTests(TemporaryDatabaseFixture tempDatabase)
    {
        _tempDatabase = tempDatabase;
    }

    [Fact]
    public void Should_insert_empty_owned_navigation_property()
    {
        var collection = _tempDatabase.CreateTemporaryCollection<PersonWithCity>();

        {
            var db = SingleEntityDbContext.Create(collection);
            var person = new PersonWithCity
            {
                Id = 1, Name = "John"
            };

            db.Entitites.Add(person);
            db.SaveChanges();
        }

        {
            var actual = collection
                .Find(Builders<PersonWithCity>.Filter.Empty)
                .Project(Builders<PersonWithCity>.Projection.As<BsonDocument>())
                .Single();

            var expected = BsonDocument.Parse("{ _id : 1, Name: 'John', City: null }");
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void Should_insert_owned_navigation_property()
    {
        var collection = _tempDatabase.CreateTemporaryCollection<PersonWithCity>();

        {
            var db = SingleEntityDbContext.Create(collection);
            var person = new PersonWithCity
            {
                Id = 1,
                Name = "John",
                City = new City
                {
                    Id = 1, Name = "New York"
                }
            };

            db.Entitites.Add(person);
            db.SaveChanges();
        }

        {
            var actual = collection
                .Find(Builders<PersonWithCity>.Filter.Empty)
                .Project(Builders<PersonWithCity>.Projection.As<BsonDocument>())
                .Single();

            var expected = BsonDocument.Parse("{ _id : 1, Name: 'John', City: { Id: 1, Name: 'New York' } }");
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void Should_update_owned_navigation_property_with_null()
    {
        var collection = _tempDatabase.CreateTemporaryCollection<PersonWithCity>();

        {
            var db = SingleEntityDbContext.Create(collection);
            var person = new PersonWithCity
            {
                Id = 1,
                Name = "John",
                City = new City
                {
                    Id = 1, Name = "New York"
                }
            };

            db.Entitites.Add(person);
            db.SaveChanges();

            person.City = null;
            db.SaveChanges();
        }

        {
            var actual = collection
                .Find(Builders<PersonWithCity>.Filter.Empty)
                .Project(Builders<PersonWithCity>.Projection.As<BsonDocument>())
                .Single();

            var expected = BsonDocument.Parse("{ _id : 1, Name: 'John', City: null }");
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void Should_update_owned_navigation_property_with_new_value()
    {
        var collection = _tempDatabase.CreateTemporaryCollection<PersonWithCity>();

        {
            var db = SingleEntityDbContext.Create(collection);
            var person = new PersonWithCity
            {
                Id = 1,
                Name = "John",
                City = new City
                {
                    Id = 1, Name = "New York"
                }
            };

            db.Entitites.Add(person);
            db.SaveChanges();

            person.City = new City
            {
                Id = 2, Name = "Washington"
            };
            db.SaveChanges();
        }

        {
            var actual = collection
                .Find(Builders<PersonWithCity>.Filter.Empty)
                .Project(Builders<PersonWithCity>.Projection.As<BsonDocument>())
                .Single();

            var expected = BsonDocument.Parse("{ _id : 1, Name: 'John',  City: { Id: 2, Name: 'Washington' } }");
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void Should_update_owned_navigation_property_fields()
    {
        var collection = _tempDatabase.CreateTemporaryCollection<PersonWithCity>();

        {
            var db = SingleEntityDbContext.Create(collection);
            var person = new PersonWithCity
            {
                Id = 1,
                Name = "John",
                City = new City
                {
                    Id = 1, Name = "New York"
                }
            };

            db.Entitites.Add(person);
            db.SaveChanges();

            person.City.Name = "Washington";
            db.SaveChanges();
        }

        {
            var actual = collection
                .Find(Builders<PersonWithCity>.Filter.Empty)
                .Project(Builders<PersonWithCity>.Projection.As<BsonDocument>())
                .Single();

            var expected = BsonDocument.Parse("{ _id : 1, Name: 'John',  City: { Id: 1, Name: 'Washington' } }");
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void Should_insert_empty_owned_navigation_collection()
    {
        var collection = _tempDatabase.CreateTemporaryCollection<PersonWithCities>();

        {
            var db = SingleEntityDbContext.Create(collection);
            var person = new PersonWithCities
            {
                Id = 1, Name = "John"
            };

            db.Entitites.Add(person);
            db.SaveChanges();
        }

        {
            var actual = collection
                .Find(Builders<PersonWithCities>.Filter.Empty)
                .Project(Builders<PersonWithCities>.Projection.As<BsonDocument>())
                .Single();

            var expected = BsonDocument.Parse("{ _id : 1, Name: 'John', Cities: null }");
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void Should_insert_owned_navigation_collection()
    {
        var collection = _tempDatabase.CreateTemporaryCollection<PersonWithCities>();

        {
            var db = SingleEntityDbContext.Create(collection);
            var person = new PersonWithCities
            {
                Id = 1,
                Name = "John",
                Cities =
                [
                    new City
                    {
                        Id = 1, Name = "New York"
                    },
                    new City
                    {
                        Id = 2, Name = "Washington"
                    }
                ]
            };

            db.Entitites.Add(person);
            db.SaveChanges();
        }

        {
            var actual = collection
                .Find(Builders<PersonWithCities>.Filter.Empty)
                .Project(Builders<PersonWithCities>.Projection.As<BsonDocument>())
                .Single();

            var expected =
                BsonDocument.Parse(
                    "{ _id : 1, Name: 'John', Cities: [{ Id: 1, Name: 'New York' }, { Id: 2, Name: 'Washington' }] }");
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void Should_update_owned_navigation_collection_with_null()
    {
        var collection = _tempDatabase.CreateTemporaryCollection<PersonWithCities>();

        {
            var db = SingleEntityDbContext.Create(collection);
            var person = new PersonWithCities
            {
                Id = 1,
                Name = "John",
                Cities =
                [
                    new City
                    {
                        Id = 1, Name = "New York"
                    },
                    new City
                    {
                        Id = 2, Name = "Washington"
                    }
                ]
            };


            db.Entitites.Add(person);
            db.SaveChanges();

            person.Cities = null;
            db.SaveChanges();
        }

        {
            var actual = collection
                .Find(Builders<PersonWithCities>.Filter.Empty)
                .Project(Builders<PersonWithCities>.Projection.As<BsonDocument>())
                .Single();

            var expected = BsonDocument.Parse("{ _id : 1, Name: 'John', Cities: null }");
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void Should_update_owned_navigation_collection_adding_new_value()
    {
        var collection = _tempDatabase.CreateTemporaryCollection<PersonWithCities>();

        {
            var db = SingleEntityDbContext.Create(collection);
            var person = new PersonWithCities
            {
                Id = 1,
                Name = "John",
                Cities =
                [
                    new City
                    {
                        Id = 1, Name = "New York"
                    },
                    new City
                    {
                        Id = 2, Name = "Washington"
                    }
                ]
            };

            db.Entitites.Add(person);
            db.SaveChanges();

            person.Cities = person.Cities.Concat(new List<City>
            {
                new City
                {
                    Id = 3, Name = "Denver"
                }
            }).ToList();
            db.SaveChanges();
        }

        {
            var actual = collection
                .Find(Builders<PersonWithCities>.Filter.Empty)
                .Project(Builders<PersonWithCities>.Projection.As<BsonDocument>())
                .Single();

            var expected =
                BsonDocument.Parse(
                    "{ _id : 1, Name: 'John',  Cities: [{ Id: 1, Name: 'New York' }, { Id: 2, Name: 'Washington' }, { Id: 3, Name: 'Denver' }] }");
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void Should_update_owned_navigation_collection_remove_value()
    {
        var collection = _tempDatabase.CreateTemporaryCollection<PersonWithCities>();

        {
            var db = SingleEntityDbContext.Create(collection);
            var person = new PersonWithCities
            {
                Id = 1,
                Name = "John",
                Cities =
                [
                    new City
                    {
                        Id = 1, Name = "New York"
                    },
                    new City
                    {
                        Id = 2, Name = "Washington"
                    }
                ]
            };

            db.Entitites.Add(person);
            db.SaveChanges();

            person.Cities = person.Cities.Where(c => c.Name != "Washington").ToList();
            db.SaveChanges();
        }

        {
            var actual = collection
                .Find(Builders<PersonWithCities>.Filter.Empty)
                .Project(Builders<PersonWithCities>.Projection.As<BsonDocument>())
                .Single();

            var expected = BsonDocument.Parse("{ _id : 1, Name: 'John',  Cities: [{ Id: 1, Name: 'New York' }] }");
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void Should_update_owned_navigation_collection_update_value()
    {
        var collection = _tempDatabase.CreateTemporaryCollection<PersonWithCities>();

        {
            var db = SingleEntityDbContext.Create(collection);
            var person = new PersonWithCities
            {
                Id = 1,
                Name = "John",
                Cities =
                [
                    new City
                    {
                        Id = 1, Name = "New York"
                    },
                    new City
                    {
                        Id = 2, Name = "Washington"
                    }
                ]
            };

            db.Entitites.Add(person);
            db.SaveChanges();

            person.Cities[0].Name = "Denver";
            db.SaveChanges();
        }

        {
            var actual = collection
                .Find(Builders<PersonWithCities>.Filter.Empty)
                .Project(Builders<PersonWithCities>.Projection.As<BsonDocument>())
                .Single();

            var expected =
                BsonDocument.Parse(
                    "{ _id : 1, Name: 'John',  Cities: [{ Id: 1, Name: 'Denver' }, { Id: 2, Name: 'Washington' }] }");
            Assert.Equal(expected, actual);
        }
    }

    private class Person
    {
        public int Id { get; set; }

        public string Name { get; set; }
    }

    private class City
    {
        public int Id { get; set; }
        public string Name { get; set; }
    }

    private class PersonWithCity : Person
    {
        public City City { get; set; }
    }

    private class PersonWithCities : Person
    {
        public List<City> Cities { get; set; }
    }
}
