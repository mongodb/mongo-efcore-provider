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
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.Metadata.Conventions;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Storage;

[XUnitCollection("StorageTests")]
public class SearchIndexNegativeTests(AtlasTemporaryDatabaseFixture database)
    : IClassFixture<AtlasTemporaryDatabaseFixture>
{
    [AtlasFact]
    public void Named_property_for_stored_source_does_not_exist()
    {
        var modelBuilder = MongoConventionSetBuilder.CreateModelBuilder();

        var builder = modelBuilder.Entity<Car>().HasSearchIndex();

        Assert.Equal(
            "Member 'DoesNotExist' was not found or not mapped to a property or navigation in entity type 'Car'. Make sure all properties in search indexes are mapped in the entity framework model.",
            Assert.Throws<InvalidOperationException>(() => builder.StoreSourceFor("DoesNotExist")).Message);
    }

    [AtlasFact]
    public void Named_property_for_stored_source_is_a_navigation()
    {
        var modelBuilder = MongoConventionSetBuilder.CreateModelBuilder();

        var builder = modelBuilder.Entity<Car>().HasSearchIndex();

        Assert.Equal(
            "Member 'Spare' was not found or not mapped to a property in entity type 'Car'. Make sure all properties in search indexes are mapped in the entity framework model.",
            Assert.Throws<InvalidOperationException>(() => builder.StoreSourceFor("Spare")).Message);
    }

    [AtlasFact]
    public void Named_property_for_embedded_is_not_a_navigation()
    {
        var modelBuilder = MongoConventionSetBuilder.CreateModelBuilder();

        var builder = modelBuilder.Entity<Car>().HasSearchIndex();

        Assert.Equal(
            "The member 'Car.Model' does not point to a nested owned entity. Make sure that the referenced type is mapped as a nested/embedded document.",
            Assert.Throws<InvalidOperationException>(() => builder.IndexAsEmbedded("Model")).Message);
    }

    [AtlasFact]
    public void Named_property_for_embedded_is_a_collection_navigation()
    {
        var modelBuilder = MongoConventionSetBuilder.CreateModelBuilder();

        var builder = modelBuilder.Entity<Car>().HasSearchIndex();

        Assert.Equal(
            "The member 'Car.Wheels' cannot be indexed as an embedded object because it is a collection of objects. References to a single object can be indexed as 'embedded', while references to a collection of objects can be indexed as 'embedded array'.",
            Assert.Throws<InvalidOperationException>(() => builder.IndexAsEmbedded("Wheels")).Message);
    }

    [AtlasFact]
    public void Named_property_for_embedded_array_is_not_a_navigation()
    {
        var modelBuilder = MongoConventionSetBuilder.CreateModelBuilder();

        var builder = modelBuilder.Entity<Car>().HasSearchIndex();

        Assert.Equal(
            "The member 'Car.Model' does not point to a nested owned entity. Make sure that the referenced type is mapped as a nested/embedded document.",
            Assert.Throws<InvalidOperationException>(() => builder.IndexAsEmbeddedArray("Model")).Message);
    }

    [AtlasFact]
    public void Named_property_for_embedded_array_is_not_a_collection_navigation()
    {
        var modelBuilder = MongoConventionSetBuilder.CreateModelBuilder();

        var builder = modelBuilder.Entity<Car>().HasSearchIndex();

        Assert.Equal(
            "The member 'Car.Spare' cannot be indexed as an embedded array because it is not a collection of embedded objects. References to a single object can be indexed as 'embedded', while references to a collection of objects can be indexed as 'embedded array'.",
            Assert.Throws<InvalidOperationException>(() => builder.IndexAsEmbeddedArray("Spare")).Message);
    }

    [AtlasFact]
    public async Task Custom_analyzer_does_not_have_a_tokenizer()
    {
        await using var db = SingleEntityDbContext.Create(database.CreateCollection<Car>(), modelBuilder =>
        {
            modelBuilder.Entity<Car>().HasSearchIndex().AddCustomAnalyzer("MyAnalyzer");
        });

        Assert.Equal(
            "The MongoDB search index custom analyzer 'MyAnalyzer' does not specify a tokenizer. All custom analyzers must configure a tokenizer.",
            (await Assert.ThrowsAsync<InvalidOperationException>(() => db.Database.CreateMissingSearchIndexesAsync())).Message);
    }

    [AtlasFact]
    public async Task Stored_source_has_both_included_and_excluded_field_names()
    {
        await using var db = SingleEntityDbContext.Create(database.CreateCollection<Car>(), modelBuilder =>
        {
            modelBuilder.Entity<Car>().HasSearchIndex("MyIndex", b =>
            {
                b.StoreSourceFor(e => e.Model);
                b.StoreSourceFor(e => e.Make, store: false);
            });
        });

        Assert.Equal(
            "Stored source for 'MyIndex' has both excluded and included field names. Stored source can be configured to exclude or include field names, but not both.",
            (await Assert.ThrowsAsync<InvalidOperationException>(() => db.Database.CreateMissingSearchIndexesAsync())).Message);
    }

    private Task<IMongoCollection<BsonDocument>> PrepareDatabase<TEntity>(
        SingleEntityDbContext<TEntity> db, Func<Task> seed)
        where TEntity : class
        => SearchIndexExamplesTests.PrepareDatabase(database, db, seed);

    public class Car
    {
        public int Id { get; set; }
        public string Model { get; set; }
        public string Make { get; set; }
        public List<EmbeddedWheel> Wheels { get; } = new();
        public EmbeddedWheel Spare { get; set; }

        public class EmbeddedWheel
        {
            public string Type { get; set; }
        }
    }
}
