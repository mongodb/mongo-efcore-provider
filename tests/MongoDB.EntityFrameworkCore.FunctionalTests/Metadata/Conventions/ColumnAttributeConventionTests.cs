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

using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.Diagnostics;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Metadata.Conventions;

[XUnitCollection("ConventionsTests")]
public class ColumnAttributeConventionTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    class IntendedStorageEntity
    {
        public ObjectId _id { get; set; }

        public string name { get; set; }
    }

    class NonKeyRemappingEntity
    {
        public ObjectId _id { get; set; }

        [Column("name")] public string RemapThisToName { get; set; }
    }

    class KeyRemappingEntity
    {
        [Column("_id")] public ObjectId _id { get; set; }

        public string name { get; set; }
    }

    class TypeNameSpecifyingEntity
    {
        public ObjectId _id { get; set; }

        [Column("name", TypeName = "varchar(255)")]
        public string TypeNameNotPermitted { get; set; }
    }

    class OwnedEntityRemappingEntity
    {
        public ObjectId _id { get; set; }

        [Column("otherLocation")] public Geolocation Location { get; set; }
    }

    class IntendedOwnedEntityRemappingEntity
    {
        public ObjectId _id { get; set; }

        public Geolocation otherLocation { get; set; }
    }

    record Geolocation(double latitude, double longitude);

    [Fact]
    public void ColumnAttribute_redefines_element_name_for_owned_entity()
    {
        var collection = database.CreateCollection<OwnedEntityRemappingEntity>();

        var id = ObjectId.GenerateNewId();
        var location = new Geolocation(1.1, 2.2);

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entities.Add(new OwnedEntityRemappingEntity {_id = id, Location = location});
            db.SaveChanges();
        }

        {
            var actual = collection.Database.GetCollection<IntendedOwnedEntityRemappingEntity>(collection.CollectionNamespace
                .CollectionName);
            var directFound = actual.Find(f => f._id == id).Single();
            Assert.Equal(location, directFound.otherLocation);
        }
    }

    [Fact]
    public void ColumnAttribute_redefines_element_name_for_insert_and_query()
    {
        var collection = database.CreateCollection<NonKeyRemappingEntity>();

        var id = ObjectId.GenerateNewId();
        var name = "The quick brown fox";

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entities.Add(new NonKeyRemappingEntity {_id = id, RemapThisToName = name});
            db.SaveChanges();
        }

        {
            var actual = collection.Database.GetCollection<IntendedStorageEntity>(collection.CollectionNamespace.CollectionName);
            var directFound = actual.Find(f => f._id == id).Single();
            Assert.Equal(name, directFound.name);
        }
    }

    [Fact]
    public void ColumnAttribute_redefines_key_name_for_insert_and_query()
    {
        var collection = database.CreateCollection<KeyRemappingEntity>();

        var id = ObjectId.GenerateNewId();
        var name = "The quick brown fox";

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entities.Add(new KeyRemappingEntity {_id = id, name = name});
            db.SaveChanges();
        }

        {
            var actual = collection.Database.GetCollection<IntendedStorageEntity>(collection.CollectionNamespace.CollectionName);
            var directFound = actual.Find(f => f._id == id).Single();
            Assert.Equal(name, directFound.name);
        }
    }

    [Fact]
    public void ColumnAttribute_redefines_key_name_for_delete()
    {
        var collection = database.CreateCollection<KeyRemappingEntity>();

        var id = ObjectId.GenerateNewId();
        var name = "The quick brown fox";

        {
            using var db = SingleEntityDbContext.Create(collection);
            var entity = new KeyRemappingEntity {_id = id, name = name};
            db.Entities.Add(entity);
            db.SaveChanges();

            db.Entities.Remove(entity);
            db.SaveChanges();
        }

        {
            var actual = collection.Database.GetCollection<IntendedStorageEntity>(collection.CollectionNamespace.CollectionName);
            Assert.Equal(0, actual.AsQueryable().Count());
        }
    }

    [Fact]
    public void ColumnAttribute_throws_if_type_name_specified()
    {
        var collection = database.CreateCollection<TypeNameSpecifyingEntity>();

        using var db = SingleEntityDbContext.Create(collection);

        var ex = Assert.Throws<InvalidOperationException>(() => db.Model);

        Assert.Equal(
            "An error was generated for warning 'Microsoft.EntityFrameworkCore.Model.ColumnAttributeWithTypeUsed': " +
            "Property 'TypeNameSpecifyingEntity.TypeNameNotPermitted' specifies a 'ColumnAttribute.TypeName' which is not supported by MongoDB. " +
            "Use MongoDB-specific attributes or the model building API to configure your model for MongoDB. " +
            "The 'TypeName' will be ignored if this event is suppressed. " +
            "This exception can be suppressed or logged by passing event ID 'MongoEventId.ColumnAttributeWithTypeUsed' to the 'ConfigureWarnings' method in 'DbContext.OnConfiguring' or 'AddDbContext'.",
            ex.Message);
    }

    [Fact]
    public void ColumnAttribute_warning_can_be_suppressed()
    {
        using var db = new NoThrowDbContext();

        var model = db.Model;
        Assert.NotNull(model
            .FindEntityType(typeof(TypeNameSpecifyingEntity))!
            .FindProperty(nameof(TypeNameSpecifyingEntity.TypeNameNotPermitted)));
    }

    private class NoThrowDbContext : DbContext
    {
        public DbSet<TypeNameSpecifyingEntity> Entities { get; set; }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
            => optionsBuilder
                .UseMongoDB("mongodb://localhost:27017", nameof(TypeNameSpecifyingEntity))
                .ConfigureWarnings(x => x.Ignore(MongoEventId.ColumnAttributeWithTypeUsed));
    }
}
