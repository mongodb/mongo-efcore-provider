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
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.EntityFrameworkCore.ValueGeneration;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.ValueGeneration;

[XUnitCollection("StorageTests")]
public class ValueGenerationTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    [Fact]
    public void ObjectId_keys_are_generated_for_add_by_default()
    {
        var expected = new ObjectIdEntity {Name = "Test"};
        var collection = database.CreateCollection<ObjectIdEntity>();

        using var db = SingleEntityDbContext.Create(collection);
        db.Entities.Add(expected);
        db.SaveChanges();

        Assert.NotEqual(ObjectId.Empty, expected.Id);
    }

    [Fact]
    public void Guid_keys_are_generated_for_add_by_default()
    {
        var expected = new GuidIdEntity {Name = "Test"};
        var collection = database.CreateCollection<GuidIdEntity>();

        using var db = SingleEntityDbContext.Create(collection);
        db.Entities.Add(expected);
        db.SaveChanges();

        Assert.NotEqual(Guid.Empty, expected.Id);
    }

    [Fact]
    public void String_keys_are_not_generated_for_add_by_default()
    {
        var expected = new StringIdEntity {Name = "Test"};
        var collection = database.CreateCollection<StringIdEntity>();

        var db = SingleEntityDbContext.Create(collection);
        Assert.Throws<InvalidOperationException>(() => db.Entities.Add(expected));
    }

    [Fact]
    public void String_keys_use_guid_generator_by_default_when_add_specified()
    {
        var expected = new StringIdEntity {Name = "Test"};
        var collection = database.CreateCollection<StringIdEntity>();

        var db = SingleEntityDbContext.Create(collection,
            mb => mb.Entity<StringIdEntity>()
                .Property(p => p.Id)
                .ValueGeneratedOnAdd());

        db.Entities.Add(expected);
        db.SaveChanges();

        Assert.NotEmpty(expected.Id);
        Assert.True(Guid.TryParse(expected.Id, out _));
    }

    [Fact]
    public void String_keys_use_objectId_generator_when_specified_for_add()
    {
        var expected = new StringIdEntity {Name = "Test"};
        var collection = database.CreateCollection<StringIdEntity>();

        var db = SingleEntityDbContext.Create(collection,
            mb => mb.Entity<StringIdEntity>()
                .Property(p => p.Id)
                .ValueGeneratedOnAdd()
                .HasValueGenerator(typeof(StringObjectIdValueGenerator)));

        db.Entities.Add(expected);
        db.SaveChanges();

        Assert.NotEmpty(expected.Id);
        Assert.True(ObjectId.TryParse(expected.Id, out _));
    }

    [Fact]
    public void String_keys_can_use_StringObjectIdValueGenerator_for_add_with_HasConversion()
    {
        var expected = new StringIdEntity {Name = "Test"};
        var collection = database.CreateCollection<StringIdEntity>();

        var db = SingleEntityDbContext.Create(collection,
            mb => mb.Entity<StringIdEntity>()
                .Property(p => p.Id)
                .HasConversion<ObjectId>()
                .ValueGeneratedOnAdd()
                .HasValueGenerator(typeof(StringObjectIdValueGenerator)));

        db.Entities.Add(expected);
        db.SaveChanges();

        Assert.NotEmpty(expected.Id);
        Assert.True(ObjectId.TryParse(expected.Id, out _));
    }

    [Fact]
    public void String_keys_can_use_StringObjectIdValueGenerator_for_add_with_BsonRepresentation()
    {
        var expected = new StringIdEntity {Name = "Test"};
        var collection = database.CreateCollection<StringIdEntity>();

        var db = SingleEntityDbContext.Create(collection,
            mb => mb.Entity<StringIdEntity>()
                .Property(p => p.Id)
                .HasBsonRepresentation(BsonType.ObjectId)
                .ValueGeneratedOnAdd());

        db.Entities.Add(expected);
        db.SaveChanges();

        Assert.NotEmpty(expected.Id);
        Assert.True(ObjectId.TryParse(expected.Id, out _));
    }

    [Fact]
    public void String_keys_can_use_StringObjectIdValueGenerator_for_add_with_attributes()
    {
        var expected = new StringIdEntityAttributed {Name = "Test"};
        var collection = database.CreateCollection<StringIdEntityAttributed>();

        var db = SingleEntityDbContext.Create(collection);

        db.Entities.Add(expected);
        db.SaveChanges();

        Assert.NotEmpty(expected.Id);
        Assert.True(ObjectId.TryParse(expected.Id, out _));
    }

    public class StringIdEntity
    {
        public string Id { get; set; }

        public string Name { get; set; }
    }

    public class ObjectIdEntity
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; }
    }

    public class GuidIdEntity
    {
        public Guid Id { get; set; }
        public string Name { get; set; }
    }

    public class StringIdEntityAttributed
    {
        [BsonRepresentation(BsonType.ObjectId)]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public string Id { get; set; }

        public string Name { get; set; }
    }
}
