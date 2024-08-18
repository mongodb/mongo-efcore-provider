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
        var collection = _tempDatabase.CreateCollection<PersonWithCity>();

        {
            using var db = SingleEntityDbContext.Create(collection);
            var person = new PersonWithCity
            {
                Id = 1, Name = "John"
            };

            db.Entities.Add(person);
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
        var collection = _tempDatabase.CreateCollection<PersonWithCity>();

        {
            using var db = SingleEntityDbContext.Create(collection);
            var person = new PersonWithCity
            {
                Id = 1,
                Name = "John",
                City = new City
                {
                    Id = 1, Name = "New York"
                }
            };

            db.Entities.Add(person);
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
        var collection = _tempDatabase.CreateCollection<PersonWithCity>();

        {
            using var db = SingleEntityDbContext.Create(collection);
            var person = new PersonWithCity
            {
                Id = 1,
                Name = "John",
                City = new City
                {
                    Id = 1, Name = "New York"
                }
            };

            db.Entities.Add(person);
            db.SaveChanges();

            person.City = null!;
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
        var collection = _tempDatabase.CreateCollection<PersonWithCity>();

        {
            using var db = SingleEntityDbContext.Create(collection);
            var person = new PersonWithCity
            {
                Id = 1,
                Name = "John",
                City = new City
                {
                    Id = 1, Name = "New York"
                }
            };

            db.Entities.Add(person);
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
        var collection = _tempDatabase.CreateCollection<PersonWithCity>();

        {
            using var db = SingleEntityDbContext.Create(collection);
            var person = new PersonWithCity
            {
                Id = 1,
                Name = "John",
                City = new City
                {
                    Id = 1, Name = "New York"
                }
            };

            db.Entities.Add(person);
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
        var collection = _tempDatabase.CreateCollection<PersonWithCities>();

        {
            using var db = SingleEntityDbContext.Create(collection);
            var person = new PersonWithCities
            {
                Id = 1, Name = "John"
            };

            db.Entities.Add(person);
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
        var collection = _tempDatabase.CreateCollection<PersonWithCities>();

        {
            using var db = SingleEntityDbContext.Create(collection);
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

            db.Entities.Add(person);
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
        var collection = _tempDatabase.CreateCollection<PersonWithCities>();

        {
            using var db = SingleEntityDbContext.Create(collection);
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


            db.Entities.Add(person);
            db.SaveChanges();

            person.Cities = null!;
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
        var collection = _tempDatabase.CreateCollection<PersonWithCities>();

        {
            using var db = SingleEntityDbContext.Create(collection);
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

            db.Entities.Add(person);
            db.SaveChanges();

            person.Cities = person.Cities.Concat([
                new City
                {
                    Id = 3, Name = "Denver"
                }
            ]).ToList();
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
        var collection = _tempDatabase.CreateCollection<PersonWithCities>();

        {
            using var db = SingleEntityDbContext.Create(collection);
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

            db.Entities.Add(person);
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
    public void Should_reload_changed_values_correctly()
    {
        var collection = _tempDatabase.CreateCollection<PersonWithCountries>();

        using var db = SingleEntityDbContext.Create(collection);
        var person = new PersonWithCountries()
        {
            Id = 1,
            Name = "John",
            Countries =
            [
                new Country()
                {
                    Name = "New York"
                },
                new Country()
                {
                    Name = "Washington"
                }
            ]
        };

        db.Entities.Add(person);
        db.SaveChanges();

        var newPerson = db.Entities.First(e => e.Id == person.Id);
        Assert.Equal(2, newPerson.Countries.Count);
    }

    [Fact]
    public void Should_update_owned_navigation_collection_update_value()
    {
        var collection = _tempDatabase.CreateCollection<PersonWithCities>();

        {
            using var db = SingleEntityDbContext.Create(collection);
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

            db.Entities.Add(person);
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


    [Fact]
    public void Should_reorder_owned_navigation_collection_ordinals()
    {
        var collection = _tempDatabase.CreateCollection<PersonWithCountries>();
        var unitedKingdom = new Country { Name = "United Kingdom" };
        var newZealand = new Country { Name = "New Zealand" };
        var france = new Country { Name = "France" };

        {
            using var db = SingleEntityDbContext.Create(collection);
            var person = new PersonWithCountries
            {
                Id = 1,
                Name = "Sally",
                Countries = [ unitedKingdom, newZealand ]
            };

            db.Entities.Add(person);
            db.SaveChanges();

            Assert.Equal(unitedKingdom.Name, person.Countries[0].Name);

            person.Countries.Remove(newZealand);
            person.Countries.Add(france);
            person.Countries.Add(newZealand);
            db.SaveChanges();

            // Test in-memory state on existing context
            Assert.Equal(unitedKingdom.Name, person.Countries[0].Name);
            Assert.Equal(france.Name, person.Countries[1].Name);
            Assert.Equal(newZealand.Name, person.Countries[2].Name);
        }

        {
            // Test on-disk state via new context
            using var db = SingleEntityDbContext.Create(collection);
            var person = db.Entities.First();

            Assert.Equal(unitedKingdom.Name, person.Countries[0].Name);
            Assert.Equal(france.Name, person.Countries[1].Name);
            Assert.Equal(newZealand.Name, person.Countries[2].Name);
        }
    }

    [Fact]
    public void Should_reorder_owned_navigation_collection_ordinals_nested()
    {
        var collection = _tempDatabase.CreateCollection<PersonWithPhoneNumbers>();

        var ukWorkPhone = new Phone
        {
            Description = "Work", Number = "123"
        };

        var ukHomePhone = new Phone
        {
            Description = "Home", Number = "789"
        };

        var ukCellPhone = new Phone
        {
            Description = "Cell", Number = "555"
        };

        var unitedKingdom = new CountryPhones
        {
            Name = "United Kingdom",
            Phones =
            [
                ukWorkPhone, ukHomePhone
            ]
        };
        var newZealand = new CountryPhones
        {
            Name = "New Zealand",
            Phones =
            [
                new Phone
                {
                    Description = "Cell", Number = "456"
                }
            ]
        };
        var france = new CountryPhones
        {
            Name = "France",
            Phones =
            [
                new Phone
                {
                    Description = "Work", Number = "456"
                }
            ]
        };

        {
            using var db = SingleEntityDbContext.Create(collection);
            var person = new PersonWithPhoneNumbers
            {
                Id = 1, Name = "Simon", PhonesByCountry = [unitedKingdom, newZealand]
            };

            db.Entities.Add(person);
            db.SaveChanges();

            Assert.Equal(unitedKingdom.Name, person.PhonesByCountry[0].Name);

            person.PhonesByCountry.Remove(newZealand);
            person.PhonesByCountry.Add(france);
            person.PhonesByCountry.Add(newZealand);

            unitedKingdom.Phones.Remove(ukWorkPhone);
            unitedKingdom.Phones.Add(ukCellPhone);
            unitedKingdom.Phones.Add(ukWorkPhone);

            db.SaveChanges();

            // Test in-memory state on existing context
            Assert.Equal(ukHomePhone.Description, person.PhonesByCountry[0].Phones[0].Description);
            Assert.Equal(ukCellPhone.Description, person.PhonesByCountry[0].Phones[1].Description);
            Assert.Equal(ukWorkPhone.Description, person.PhonesByCountry[0].Phones[2].Description);
        }

        {
            // Test on-disk state via new context
            using var db = SingleEntityDbContext.Create(collection);
            var person = db.Entities.First();

            Assert.Equal(ukHomePhone.Description, person.PhonesByCountry[0].Phones[0].Description);
            Assert.Equal(ukCellPhone.Description, person.PhonesByCountry[0].Phones[1].Description);
            Assert.Equal(ukWorkPhone.Description, person.PhonesByCountry[0].Phones[2].Description);
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

    private class Country
    {
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

    private class PersonWithCountries : Person
    {
        public List<Country> Countries { get; set; }
    }

    private class PersonWithPhoneNumbers : Person
    {
        public List<CountryPhones> PhonesByCountry { get; set; }
    }

    private class CountryPhones
    {
        public string Name { get; set; }
        public List<Phone> Phones { get; set; }
    }

    private class Phone
    {
        public string Description { get; set; }
        public string Number { get; set; }
    }
}
