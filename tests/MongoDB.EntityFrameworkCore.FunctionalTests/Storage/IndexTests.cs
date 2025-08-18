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
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.Metadata;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Storage;

[XUnitCollection("StorageTests")]
public class IndexTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    class SimpleEntity
    {
        public Guid Id { get; set; }
        public float[] Floats { get; set; }

        [NotMapped]
        public float[] MoreFloats { get; set; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Create_vector_index(bool async)
    {
        var collection = database.CreateCollection<SimpleEntity>(values: async);
        using var db = SingleEntityDbContext.Create(collection,
            b => { b.Entity<SimpleEntity>().HasIndex(e => e.Floats)
                .IsVectorIndex(VectorSimilarity.Cosine, 2); });

        _ = async ? await db.Database.EnsureCreatedAsync() : db.Database.EnsureCreated();

        ValidateIndex("FloatsVectorIndex", collection, "Floats", 2, "cosine");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Create_vector_index_with_name(bool async)
    {
        var collection = database.CreateCollection<SimpleEntity>(values: async);
        using var db = SingleEntityDbContext.Create(collection,
            b => { b.Entity<SimpleEntity>().HasIndex(e => e.Floats, "MyVI")
                .IsVectorIndex(VectorSimilarity.Euclidean, 8); });

        _ = async ? await db.Database.EnsureCreatedAsync() : db.Database.EnsureCreated();

        ValidateIndex("MyVI", collection, "Floats", 8, "euclidean");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Create_vector_index_with_options(bool async)
    {
        var collection = database.CreateCollection<SimpleEntity>(values: async);
        using var db = SingleEntityDbContext.Create(collection,
            b =>
            {
                b.Entity<SimpleEntity>().HasIndex(e => e.Floats)
                    .IsVectorIndex(new(VectorSimilarity.DotProduct, 4, VectorQuantization.Scalar, 32, 1600));
            });

        _ = async ? await db.Database.EnsureCreatedAsync() : db.Database.EnsureCreated();

        var index = ValidateIndex("FloatsVectorIndex", collection, "Floats", 4, "dotProduct");

        var field = index["latestDefinition"]["fields"][0];
        Assert.Equal("scalar", field["quantization"].AsString);
        Assert.Equal(32, field["hnswOptions"]["maxEdges"].AsInt32);
        Assert.Equal(1600, field["hnswOptions"]["numEdgeCandidates"].AsInt32);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Create_vector_index_with_options_and_name(bool async)
    {
        var collection = database.CreateCollection<SimpleEntity>(values: async);
        using var db = SingleEntityDbContext.Create(collection,
            b =>
            {
                b.Entity<SimpleEntity>().HasIndex(e => e.Floats, "Shadowfell")
                    .IsVectorIndex(new(VectorSimilarity.DotProduct, 8, VectorQuantization.Binary, 32, 200));
            });

        _ = async ? await db.Database.EnsureCreatedAsync() : db.Database.EnsureCreated();

        var index = ValidateIndex("Shadowfell", collection, "Floats", 8, "dotProduct");

        var field = index["latestDefinition"]["fields"][0];
        Assert.Equal("binary", field["quantization"].AsString);
        Assert.Equal(32, field["hnswOptions"]["maxEdges"].AsInt32);
        Assert.Equal(200, field["hnswOptions"]["numEdgeCandidates"].AsInt32);
    }

    private BsonDocument ValidateIndex(string indexName, IMongoCollection<SimpleEntity> collection, string? expectedPath,
        int expectedDimensions, string? expectedSimilarity)
    {
        var index = database.GetCollection<BsonDocument>(collection.CollectionNamespace)
            .SearchIndexes.List().ToList().Single(i => i["name"].AsString == indexName);

        Assert.Equal("vectorSearch", index["type"].AsString);
        Assert.Equal("READY", index["status"].AsString);
        Assert.True(index["queryable"].AsBoolean);
        Assert.Equal(0, index["latestVersion"].AsInt32);
        Assert.Single(index["latestDefinition"]["fields"].AsBsonArray);

        var field = index["latestDefinition"]["fields"][0];
        Assert.Equal("vector", field["type"].AsString);
        Assert.Equal(expectedPath, field["path"].AsString);
        Assert.Equal(expectedDimensions, field["numDimensions"].AsInt32);
        Assert.Equal(expectedSimilarity, field["similarity"].AsString);

        return index;
    }

    [Fact]
    public void Query_throws_for_vector_index_specified_but_missing()
    {
        var collection = database.CreateCollection<SimpleEntity>();
        using var db = SingleEntityDbContext.Create(collection);

        db.Database.EnsureCreated();

        Assert.Contains(
            "A vector query for 'SimpleEntity.Floats' could not be executed because vector index 'MissingIndex' was not defined in the EF Core model. " +
            "Use 'HasIndex' on the EF model builder to specify the index, or disable this warning if you have created your MongoDB indexes outside of EF Core.",
            Assert.Throws<InvalidOperationException>(
                () => db.Set<SimpleEntity>().VectorSearch(e => e.Floats, new[] { 0.33f, -0.52f }, 2, new() { IndexName = "MissingIndex" })).Message);
    }

    [Fact]
    public void Query_throws_for_vector_index_specified_but_different()
    {
        var collection = database.CreateCollection<SimpleEntity>();
        using var db = SingleEntityDbContext.Create(collection,
            b =>
            {
                b.Entity<SimpleEntity>().HasIndex(e => e.Floats, "Slarti")
                    .IsVectorIndex(new(VectorSimilarity.DotProduct, 8, VectorQuantization.Binary, 32, 200));
            });

        db.Database.EnsureCreated();

        Assert.Contains(
            "A vector query for 'SimpleEntity.Floats' could not be executed because vector index 'MissingIndex' was not defined in the EF Core model. " +
            "Vector query searches must use one of the indexes defined on the EF model.",
            Assert.Throws<InvalidOperationException>(
                () => db.Set<SimpleEntity>().VectorSearch(e => e.Floats, new[] { 0.33f, -0.52f }, 2, new() { IndexName = "MissingIndex" })).Message);
    }

    [Fact]
    public void Query_throws_for_no_vector_index_when_not_specified()
    {
        var collection = database.CreateCollection<SimpleEntity>();
        using var db = SingleEntityDbContext.Create(collection);

        db.Database.EnsureCreated();

        Assert.Contains(
            "A vector query for 'SimpleEntity.Floats' could not be executed because there are no vector indexes defined",
            Assert.Throws<InvalidOperationException>(
                () => db.Set<SimpleEntity>().VectorSearch(e => e.Floats, new[] { 0.33f, -0.52f }, 2)).Message);
    }

    [Fact]
    public void Query_throws_for_multiple_vector_indexes_when_not_specified()
    {
        var collection = database.CreateCollection<SimpleEntity>();
        using var db = SingleEntityDbContext.Create(collection,
            b =>
            {
                b.Entity<SimpleEntity>(b =>
                {
                    b.HasIndex(e => e.Floats, "Slarti")
                        .IsVectorIndex(new(VectorSimilarity.DotProduct, 8, VectorQuantization.Binary, 32, 200));
                    b.HasIndex(e => e.Floats, "Bartfast")
                        .IsVectorIndex(new(VectorSimilarity.Euclidean, 4, VectorQuantization.Scalar));
                });
            });

        db.Database.EnsureCreated();

        Assert.Contains(
            "A vector query for 'SimpleEntity.Floats' could not be executed because multiple vector indexes are defined",
            Assert.Throws<InvalidOperationException>(
                () => db.Set<SimpleEntity>().VectorSearch(e => e.Floats, new[] { 0.33f, -0.52f }, 2)).Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Query_does_not_throw_when_multiple_vector_indexes_but_one_specified(bool async)
    {
        var collection = database.CreateCollection<SimpleEntity>(values: async);
        using var db = SingleEntityDbContext.Create(collection,
            b =>
            {
                b.Entity<SimpleEntity>(b =>
                {
                    b.HasIndex(e => e.Floats, "Slarti").IsVectorIndex(new(VectorSimilarity.DotProduct, 2));
                    b.HasIndex(e => e.Floats, "Bartfast").IsVectorIndex(new(VectorSimilarity.Euclidean, 2));
                });
            });

        _ = async ? await db.Database.EnsureCreatedAsync() : db.Database.EnsureCreated();

        db.AddRange(new SimpleEntity { Floats = [0.36f, -0.57f] }, new SimpleEntity { Floats = [0.31f, -0.54f] });
        _ = async ? await db.SaveChangesAsync() : db.SaveChanges();

        var query = db.Set<SimpleEntity>().VectorSearch(e => e.Floats, new[] { 0.33f, -0.52f }, 4, new() {  IndexName = "Bartfast" });

        // TODO: This often fails because adding date to the index makes it not ready.
        // Assert.Equal(2, (async ? await query.ToListAsync() : query.ToList()).Count);
    }

    [Fact]
    public void Query_throws_for_unmapped_member()
    {
        var collection = database.CreateCollection<SimpleEntity>();
        using var db = SingleEntityDbContext.Create(collection);

        db.Database.EnsureCreated();

        Assert.Contains(
            "Could not create a vector query for 'SimpleEntity.MoreFloats'.",
            Assert.Throws<InvalidOperationException>(
                () =>db.Set<SimpleEntity>().VectorSearch(e => e.MoreFloats, new[] { 0.33f, -0.52f }, 2)).Message);
    }
}
