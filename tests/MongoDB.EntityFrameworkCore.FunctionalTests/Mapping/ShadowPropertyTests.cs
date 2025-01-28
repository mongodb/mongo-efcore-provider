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
using Microsoft.EntityFrameworkCore.Diagnostics;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.Metadata.Conventions;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Mapping;

[XUnitCollection("MappingTests")]
public class ShadowPropertyTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    [Theory]
    [InlineData(1)]
    [InlineData("Testing")]
    [InlineData(1.1)]
    public void Can_retrieve_doc_element_into_shadow_property<T>(T value)
    {
        var expected = new KeyedDocWithActualProperty<T> {StoredValue = value};
        var collection = database.CreateCollection<KeyedEntity>(values: value);
        collection.InsertOne(expected);

        var db = SingleEntityDbContext.Create(collection, mb => mb.Entity<KeyedEntity>().Property<T>("storedValue"));
        var actual = db.Entities.First(e => e.Id == expected.Id);

        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(value, db.Entry(actual).Property<T>("storedValue").CurrentValue);
    }

    [Theory]
    [InlineData(1)]
    [InlineData("Testing")]
    [InlineData(1.1)]
    public void Shadow_properties_use_element_names_from_configuration<T>(T value)
    {
        var expected = new KeyedDocWithActualProperty<T> {StoredValue = value};
        var collection = database.CreateCollection<KeyedEntity>(values: value);
        collection.InsertOne(expected);

        var db = SingleEntityDbContext.Create(collection,
            mb => mb.Entity<KeyedEntity>().Property<T>("Prop").HasElementName("storedValue"));
        var actual = db.Entities.First(e => e.Id == expected.Id);

        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(value, db.Entry(actual).Property<T>("Prop").CurrentValue);
    }

    [Theory]
    [InlineData(1)]
    [InlineData("Testing")]
    [InlineData(1.1)]
    public void Shadow_properties_use_element_names_from_convention<T>(T value)
    {
        var expected = new KeyedDocWithActualProperty<T> {StoredValue = value};
        var collection = database.CreateCollection<KeyedEntity>(values: value);
        collection.InsertOne(expected);

        var db = SingleEntityDbContext.Create(collection,
            mb => mb.Entity<KeyedEntity>().Property<T>("StoredValue"),
            ob => ob.Conventions.Add(_ => new CamelCaseElementNameConvention()));
        var actual = db.Entities.First(e => e.Id == expected.Id);

        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(value, db.Entry(actual).Property<T>("StoredValue").CurrentValue);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(false)]
    [InlineData(1.1)]
    public void Shadow_properties_use_has_conversion<T>(T value)
    {
        var expected = new KeyedDocWithActualProperty<string> {StoredValue = value.ToString()!};
        var collection = database.CreateCollection<KeyedEntity>(values: value);
        collection.InsertOne(expected);

        var db = SingleEntityDbContext.Create(collection,
            mb => mb.Entity<KeyedEntity>().Property<T>("storedValue").HasConversion<string>());
        var actual = db.Entities.First(e => e.Id == expected.Id);

        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(value, db.Entry(actual).Property<T>("storedValue").CurrentValue);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(false)]
    [InlineData(1.1)]
    public void Shadow_properties_use_bson_type_representation<T>(T value)
    {
        var expected = new KeyedDocWithActualProperty<string> {StoredValue = value.ToString()!};
        var collection = database.CreateCollection<KeyedEntity>(values: value);
        collection.InsertOne(expected);

        var db = SingleEntityDbContext.Create(collection,
            mb => mb.Entity<KeyedEntity>().Property<T>("storedValue").HasBsonRepresentation(BsonType.String));
        var actual = db.Entities.First(e => e.Id == expected.Id);

        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(value, db.Entry(actual).Property<T>("storedValue").CurrentValue);
    }

    [Theory]
    [InlineData(0)]
    [InlineData("Something Interesting")]
    [InlineData(234.567d)]
    public void Can_store_shadow_property_into_doc_element<T>(T value)
    {
        var expected = new KeyedEntity();
        var collection = database.CreateCollection<KeyedEntity>(values: value);

        {
            var db = SingleEntityDbContext.Create(collection,
                mb => mb.Entity<KeyedEntity>().Property<T>("storedValue"));
            db.Entities.Add(expected);
            db.Entry(expected).Property<T>("storedValue").CurrentValue = value;
            db.SaveChanges();
        }

        {
            var docCollection = database.GetCollection<KeyedDocWithActualProperty<T>>(collection.CollectionNamespace);
            var actual = docCollection.AsQueryable().First(e => e.Id == expected.Id);
            Assert.Equal(expected.Id, actual.Id);
            Assert.Equal(value, actual.StoredValue);
        }
    }

    [Theory]
    [InlineData(1011)]
    [InlineData("Something Interesting 33")]
    [InlineData(234.5671d)]
    public void Can_query_on_shadow_property<T>(T value)
    {
        var expected = new KeyedEntity();
        var collection = database.CreateCollection<KeyedEntity>(values: value);

        {
            var db = SingleEntityDbContext.Create(collection, ConfigureModel);
            db.Entities.AddRange(expected, new KeyedEntity(), new KeyedEntity());
            db.Entry(expected).Property<T>("storedValue").CurrentValue = value;
            db.SaveChanges();
        }

        {
            var db = SingleEntityDbContext.Create(collection, ConfigureModel);
            var actual = db.Entities.First(e => EF.Property<T>(e, "storedValue").Equals(value));
            Assert.Equal(expected.Id, actual.Id);
            Assert.Equal(value, db.Entry(actual).Property<T>("storedValue").CurrentValue);
        }

        void ConfigureModel(ModelBuilder mb) => mb.Entity<KeyedEntity>().Property<T>("storedValue");
    }

    [Theory]
    [InlineData(0)]
    [InlineData("Something Interesting")]
    [InlineData(234.567d)]
    public void Can_query_on_shadow_property_declared_on_parent<T>(T value)
    {
        var expected = new KeyedEntity();
        var collection = database.CreateCollection<KeyedEntity>(values: value);

        {
            var db = SingleEntityDbContext.Create(collection, ConfigureModel);
            db.Entities.AddRange(expected, new KeyedEntity(), new KeyedEntity());
            db.Entry(expected).Property<T>("storedValue").CurrentValue = value;
            db.SaveChanges();
        }

        {
            var db = SingleEntityDbContext.Create(collection, ConfigureModel);
            var actual = db.Entities.First(e => EF.Property<T>(e, "storedValue").Equals(value));
            Assert.Equal(expected.Id, actual.Id);
            Assert.Equal(value, db.Entry(actual).Property<T>("storedValue").CurrentValue);
        }

        void ConfigureModel(ModelBuilder mb) => mb.Entity<KeyedEntity>(e =>
        {
            e.Property<T>("storedValue");
            e.HasDiscriminator(f => f.Type)
                .HasValue<KeyedEntity>("main")
                .HasValue<SubKeyedEntity>("sub");
        });
    }

    [Theory]
    [InlineData(3.1415926f)]
    [InlineData("MongoDB")]
    [InlineData('a')]
    public void Can_query_on_shadow_property_both_sides<T>(T value)
    {
        var expected = new KeyedEntity();
        var collection = database.CreateCollection<KeyedEntity>(values: value);

        {
            var db = SingleEntityDbContext.Create(collection, ConfigureModel);
            db.Entities.AddRange(expected, new KeyedEntity(), new KeyedEntity());
            db.Entry(expected).Property<T>("valueOne").CurrentValue = value;
            db.Entry(expected).Property<T>("valueTwo").CurrentValue = value;
            db.SaveChanges();
        }

        {
            var db = SingleEntityDbContext.Create(collection, ConfigureModel);
            var actual = db.Entities.First(e => EF.Property<T>(e, "valueOne").Equals(EF.Property<T>(e, "valueTwo")));
            Assert.Equal(expected.Id, actual.Id);
            Assert.Equal(value, db.Entry(actual).Property<T>("valueTwo").CurrentValue);
        }

        void ConfigureModel(ModelBuilder mb)
        {
            mb.Entity<KeyedEntity>(e =>
            {
                e.Property<T>("valueOne");
                e.Property<T>("valueTwo");
            });
        }
    }

    [Theory]
    [InlineData(3.14159f, "3.14159")]
    [InlineData(true, "True")]
    [InlineData("False", false)]
    public void Can_query_on_shadow_property_with_conversion<TDoc, TClr>(TDoc docValue, TClr clrValue)
    {
        var expected = new KeyedDocWithActualProperty<TDoc> {StoredValue = docValue};
        var collection = database.CreateCollection<KeyedEntity>(values: docValue);
        collection.InsertOne(expected);

        var db = SingleEntityDbContext.Create(collection, ConfigureModel);
        var actual = db.Entities.First(e => e.Id == expected.Id);
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(clrValue, db.Entry(actual).Property<TClr>("storedValue").CurrentValue);

        void ConfigureModel(ModelBuilder mb)
        {
            mb.Entity<KeyedEntity>(e =>
            {
                e.Property<TClr>("storedValue").HasConversion<TDoc>();
            });
        }
    }

    public class KeyedEntity
    {
        [BsonElement("_id")]
        public ObjectId Id { get; set; } = ObjectId.GenerateNewId();

        public string? Type { get; set; }
    }

    public class KeyedDocWithActualProperty<T> : KeyedEntity
    {
        [BsonElement("storedValue")]
        public T StoredValue { get; set; }
    }

    public class SubKeyedEntity : KeyedEntity
    {
        public string SomeValue { get; set; }
    }

    [Fact]
    public void Shadow_property_can_be_set_as_a_foreign_key()
    {
        var originalAuthor = new Author {Name = "Damien"};
        var originalPost = new Post {Title = "Foreign key", Author = originalAuthor};

        {
            var db = new BloggingContext(For(database.MongoDatabase).Options);
            db.Authors.Add(originalAuthor);
            db.Posts.Add(originalPost);
            db.SaveChanges();
        }

        {
            var db = new BloggingContext(For(database.MongoDatabase).Options);
            var actualPost = db.Posts.First(p => p.Id == originalPost.Id);
            Assert.Equal(originalAuthor.Id, db.Entry(actualPost).Property<ObjectId>("AuthorId").CurrentValue);
        }
    }

    [Fact]
    public void Shadow_property_can_be_used_by_navigation_proxy()
    {
        var originalAuthor = new Author {Name = "Damien"};
        var originalPost = new Post {Title = "Navigation proxy", Author = originalAuthor};

        {
            var db = new BloggingContext(For(database.MongoDatabase).UseLazyLoadingProxies().Options);
            db.Authors.Add(originalAuthor);
            db.Posts.Add(originalPost);
            db.SaveChanges();
        }

        {
            var db = new BloggingContext(For(database.MongoDatabase).UseLazyLoadingProxies().Options);
            var foundPost = db.Posts.First(p => p.Id == originalPost.Id);
            Assert.Equal(originalAuthor.Name, foundPost.Author.Name);
        }
    }

    [Fact]
    public void Shadow_property_can_be_used_for_rowversion()
    {
        var staleAuthor = new Author {Name = "Damien"};

        var staleDb = new BloggingContext(For(database.MongoDatabase).Options, ModelConfiguration);
        staleDb.Authors.Add(staleAuthor);
        staleDb.SaveChanges();

        {
            var freshDb = new BloggingContext(For(database.MongoDatabase).Options, ModelConfiguration);
            var freshAuthor = freshDb.Authors.First(a => a.Id == staleAuthor.Id);
            freshAuthor.Name = "Damien Modified Fresh";
            freshDb.SaveChanges();
        }

        staleAuthor.Name = "Damien Modified Stale";
        Assert.Throws<DbUpdateConcurrencyException>(() => staleDb.SaveChanges());

        void ModelConfiguration(ModelBuilder mb) => mb.Entity<Author>().Property<long>("RowVersion").IsRowVersion();
    }

    public class BloggingContext(DbContextOptions options, Action<ModelBuilder>? mb = null)
        : DbContext(options)
    {
        public DbSet<Author> Authors { get; set; }
        public DbSet<Post> Posts { get; set; }

        protected override void ConfigureConventions(ModelConfigurationBuilder cb)
        {
            base.ConfigureConventions(cb);
            cb.Conventions.Add(_ => new CamelCaseElementNameConvention());
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            mb?.Invoke(modelBuilder);
        }
    }

    public static DbContextOptionsBuilder<BloggingContext> For(IMongoDatabase mongoDatabase) =>
        new DbContextOptionsBuilder<BloggingContext>()
            .UseMongoDB(mongoDatabase.Client, mongoDatabase.DatabaseNamespace.DatabaseName)
            .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));

    public class Post
    {
        public ObjectId Id { get; set; }
        public string Title { get; set; }
        public virtual Author Author { get; set; }
    }

    public class Author
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; }
    }
}
