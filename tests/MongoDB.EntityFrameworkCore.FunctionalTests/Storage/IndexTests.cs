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
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.Metadata;
using Xunit.Abstractions;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Storage;

[XUnitCollection("StorageTests")]
public class IndexTests(AtlasTemporaryDatabaseFixture database, ITestOutputHelper  testOutputHelper)
    : IClassFixture<AtlasTemporaryDatabaseFixture>
{
    class SimpleEntity
    {
        public Guid Id { get; set; }
        public float[] Floats { get; set; }

        [NotMapped] public float[] MoreFloats { get; set; }

        public string Filter1 { get; set; }
        public int Filter2 { get; set; }
        public bool Filter3 { get; set; }

        public NestedEntity1 Nested { get; set; }
    }

    class NestedEntity1
    {
        public NestedEntity2 Nested { get; set; }
        public decimal FilterN1 { get; set; }
        public float[] Floats { get; set; }
    }

    class NestedEntity2
    {
        public long FilterN2 { get; set; }
        public double[] Doubles { get; set; }
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, true, true)]
    [InlineData(false, false, true)]
    [InlineData(true, true, false)]
    public async Task Create_vector_index_in_EnsureCreated(bool async, bool useStrings, bool useOptions)
    {
        if (SkipForAtlas())
            return;

        var collection = database.CreateCollection<SimpleEntity>(values: async);
        using var db = SingleEntityDbContext.Create(collection,
            b =>
            {
                _ = useStrings
                    ? b.Entity(typeof(SimpleEntity).FullName!)
                        .HasIndex("Floats")
                        .IsVectorIndex(VectorSimilarity.Cosine, 2)
                    : b.Entity<SimpleEntity>()
                        .HasIndex(e => e.Floats)
                        .IsVectorIndex(VectorSimilarity.Cosine, 2);
            });

        if (useOptions)
        {
            var options = new MongoDatabaseCreationOptions();
            _ = async ? await db.Database.EnsureCreatedAsync(options) : db.Database.EnsureCreated(options);
        }
        else
        {
            _ = async ? await db.Database.EnsureCreatedAsync() : db.Database.EnsureCreated();
        }

        ValidateIndex("FloatsVectorIndex", collection, "Floats", 2, "cosine", expectedFilterPaths: null);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Create_vector_index_after_EnsureCreated(bool async)
    {
        if (SkipForAtlas())
            return;

        var collection = database.CreateCollection<SimpleEntity>(values: async);
        using var db = SingleEntityDbContext.Create(collection,
            b => b.Entity<SimpleEntity>().HasIndex(e => e.Floats).IsVectorIndex(VectorSimilarity.Cosine, 2));

        var options = new MongoDatabaseCreationOptions(CreateMissingVectorIndexes: false, WaitForVectorIndexes: false);

        if (async)
        {
            await db.Database.EnsureCreatedAsync(options);

            Assert.Equal(0, GetVectorIndexCount(collection));

            await db.Database.CreateMissingVectorIndexesAsync();
            await db.Database.WaitForVectorIndexesAsync();
        }
        else
        {
            db.Database.EnsureCreated(options);

            Assert.Equal(0, GetVectorIndexCount(collection));

            await db.Database.CreateMissingVectorIndexesAsync();
            await db.Database.WaitForVectorIndexesAsync();
        }

        ValidateIndex("FloatsVectorIndex", collection, "Floats", 2, "cosine", expectedFilterPaths: null);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, true, true)]
    [InlineData(false, false, true)]
    [InlineData(true, true, false)]
    public async Task Create_vector_index_on_nested_entity_in_EnsureCreated(bool async, bool useStrings, bool useOptions)
    {
        if (SkipForAtlas())
            return;

        var collection = database.CreateCollection<SimpleEntity>(values: async);
        using var db = SingleEntityDbContext.Create(collection,
            b =>
            {
                _ = useStrings
                    ? b.Entity(typeof(SimpleEntity).FullName!)
                        .OwnsOne(typeof(NestedEntity1), "Nested")
                        .OwnsOne(typeof(NestedEntity2), "Nested")
                        .HasIndex("Doubles")
                        .IsVectorIndex(VectorSimilarity.Cosine, 2)
                    : b.Entity<SimpleEntity>()
                        .OwnsOne(e => e.Nested)
                        .OwnsOne(e => e.Nested)
                        .HasIndex(e => e.Doubles)
                        .IsVectorIndex(VectorSimilarity.Cosine, 2);
            });

        if (useOptions)
        {
            var options = new MongoDatabaseCreationOptions();
            _ = async ? await db.Database.EnsureCreatedAsync(options) : db.Database.EnsureCreated(options);
        }
        else
        {
            _ = async ? await db.Database.EnsureCreatedAsync() : db.Database.EnsureCreated();
        }

        ValidateIndex("DoublesVectorIndex", collection, "Nested.Nested.Doubles", 2, "cosine", expectedFilterPaths: null);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Create_vector_index_on_nested_entity_after_EnsureCreated(bool async)
    {
        if (SkipForAtlas())
            return;

        var collection = database.CreateCollection<SimpleEntity>(values: async);
        using var db = SingleEntityDbContext.Create(
            collection,
            b => b.Entity<SimpleEntity>()
                .OwnsOne(e => e.Nested)
                .OwnsOne(e => e.Nested)
                .HasIndex(e => e.Doubles)
                .IsVectorIndex(VectorSimilarity.Cosine, 2));

        var options = new MongoDatabaseCreationOptions(CreateMissingVectorIndexes: false, WaitForVectorIndexes: false);

        if (async)
        {
            await db.Database.EnsureCreatedAsync(options);

            Assert.Equal(0, GetVectorIndexCount(collection));

            await db.Database.CreateMissingVectorIndexesAsync();
            await db.Database.WaitForVectorIndexesAsync();
        }
        else
        {
            db.Database.EnsureCreated(options);

            Assert.Equal(0, GetVectorIndexCount(collection));

            await db.Database.CreateMissingVectorIndexesAsync();
            await db.Database.WaitForVectorIndexesAsync();
        }

        ValidateIndex("DoublesVectorIndex", collection, "Nested.Nested.Doubles", 2, "cosine", expectedFilterPaths: null);
    }

#if EF9 // HasIndex with name on owned types was accidentally missing in EF8

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Indexes_can_be_created_individually(bool async)
    {
        if (SkipForAtlas())
            return;

        var collection = database.CreateCollection<SimpleEntity>(values: async);
        using var db = SingleEntityDbContext.Create(
            collection,
            b => b.Entity<SimpleEntity>(b =>
            {
                b.HasIndex(e => e.Filter1, "I1");
                b.HasIndex(e => e.Floats, "VI1").IsVectorIndex(VectorSimilarity.DotProduct, 4);
                b.OwnsOne(e => e.Nested, b =>
                {
                    b.HasIndex(e => e.FilterN1, "I2");
                    b.OwnsOne(e => e.Nested, b =>
                    {
                        b.HasIndex(e => e.FilterN2, "I3");
                        b.HasIndex(e => e.Doubles, "VI2").IsVectorIndex(VectorSimilarity.Cosine, 2);
                    });
                });
            }));

        var options = new MongoDatabaseCreationOptions(CreateMissingVectorIndexes: false, CreateMissingIndexes: false);
        var model = db.GetService<IDesignTimeModel>().Model;
        var root = model.FindEntityType(typeof(SimpleEntity))!;
        var nested1 = model.FindEntityType(typeof(NestedEntity1))!;
        var nested2 = model.FindEntityType(typeof(NestedEntity2))!;

        if (async)
        {
            await db.Database.EnsureCreatedAsync(options);
            Assert.Equal(1, GetIndexCount(collection));
            Assert.Equal(0, GetVectorIndexCount(collection));

            await db.Database.CreateIndexAsync(IndexWithName(root, "I1"));
            Assert.Equal(2, GetIndexCount(collection));
            Assert.Equal(0, GetVectorIndexCount(collection));

            await db.Database.CreateIndexAsync(IndexWithName(root, "VI1"));
            Assert.Equal(2, GetIndexCount(collection));
            Assert.Equal(1, GetVectorIndexCount(collection));

            await db.Database.CreateIndexAsync(IndexWithName(nested1, "I2"));
            Assert.Equal(3, GetIndexCount(collection));
            Assert.Equal(1, GetVectorIndexCount(collection));

            await db.Database.CreateIndexAsync(IndexWithName(nested2, "I3"));
            Assert.Equal(4, GetIndexCount(collection));
            Assert.Equal(1, GetVectorIndexCount(collection));

            await db.Database.CreateIndexAsync(IndexWithName(nested2, "VI2"));
            Assert.Equal(4, GetIndexCount(collection));
            Assert.Equal(2, GetVectorIndexCount(collection));
        }
        else
        {
            db.Database.EnsureCreated(options);
            Assert.Equal(1, GetIndexCount(collection));
            Assert.Equal(0, GetVectorIndexCount(collection));

            db.Database.CreateIndex(IndexWithName(root, "I1"));
            Assert.Equal(2, GetIndexCount(collection));
            Assert.Equal(0, GetVectorIndexCount(collection));

            db.Database.CreateIndex(IndexWithName(root, "VI1"));
            Assert.Equal(2, GetIndexCount(collection));
            Assert.Equal(1, GetVectorIndexCount(collection));

            db.Database.CreateIndex(IndexWithName(nested1, "I2"));
            Assert.Equal(3, GetIndexCount(collection));
            Assert.Equal(1, GetVectorIndexCount(collection));

            db.Database.CreateIndex(IndexWithName(nested2, "I3"));
            Assert.Equal(4, GetIndexCount(collection));
            Assert.Equal(1, GetVectorIndexCount(collection));

            db.Database.CreateIndex(IndexWithName(nested2, "VI2"));
            Assert.Equal(4, GetIndexCount(collection));
            Assert.Equal(2, GetVectorIndexCount(collection));
        }

        static IIndex IndexWithName(IEntityType entityType, string name)
        {
            return entityType.GetIndexes().Single(i => i.Name == name);
        }
    }

#endif

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task Create_vector_index_with_name(bool async, bool useStrings)
    {
        if (SkipForAtlas())
            return;

        var collection = database.CreateCollection<SimpleEntity>(values: async);
        using var db = SingleEntityDbContext.Create(collection,
            b =>
            {
                _ = useStrings
                    ? b.Entity(typeof(SimpleEntity).FullName!)
                        .HasIndex(["Floats"], "MyVI")
                        .IsVectorIndex(VectorSimilarity.Euclidean, 8)
                    : b.Entity<SimpleEntity>()
                        .HasIndex(e => e.Floats, "MyVI")
                        .IsVectorIndex(VectorSimilarity.Euclidean, 8);
            });

        _ = async ? await db.Database.EnsureCreatedAsync() : db.Database.EnsureCreated();

        ValidateIndex("MyVI", collection, "Floats", 8, "euclidean", expectedFilterPaths: null);
    }

#if EF9 // HasIndex with name on owned types was accidentally missing in EF8

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Create_vector_index_with_name_on_nested_entity(bool async, bool useStrings)
    {
        if (SkipForAtlas())
            return;

        var collection = database.CreateCollection<SimpleEntity>(values: async);
        using var db = SingleEntityDbContext.Create(collection,
            b =>
            {
                _ = useStrings
                    ? b.Entity(typeof(SimpleEntity).FullName!)
                        .OwnsOne(typeof(NestedEntity1), "Nested")
                        .OwnsOne(typeof(NestedEntity2), "Nested")
                        .HasIndex(["Doubles"], "AfterEight")
                        .IsVectorIndex(VectorSimilarity.Euclidean, 8)
                    : b.Entity<SimpleEntity>()
                        .OwnsOne(e => e.Nested)
                        .OwnsOne(e => e.Nested)
                        .HasIndex(e => e.Doubles, "AfterEight")
                        .IsVectorIndex(VectorSimilarity.Euclidean, 8);
            });

        _ = async ? await db.Database.EnsureCreatedAsync() : db.Database.EnsureCreated();

        ValidateIndex("AfterEight", collection, "Nested.Nested.Doubles", 8, "euclidean", expectedFilterPaths: null);
    }

#endif

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task Create_vector_index_with_options(bool async, bool useStrings)
    {
        if (SkipForAtlas())
            return;

        var collection = database.CreateCollection<SimpleEntity>(values: async);
        using var db = SingleEntityDbContext.Create(collection,
            b =>
            {
                _ = useStrings
                    ? b.Entity(typeof(SimpleEntity).FullName!)
                        .HasIndex("Floats")
                        .IsVectorIndex(VectorSimilarity.DotProduct, 4)
                        .HasQuantization(VectorQuantization.Scalar)
                        .HasEdgeOptions(32, 1600)
                        .AllowsFiltersOn("Filter1")
                        .AllowsFiltersOn("Filter2")
                        .AllowsFiltersOn("Filter3")
                    : b.Entity<SimpleEntity>()
                        .HasIndex(e => e.Floats)
                        .IsVectorIndex(VectorSimilarity.DotProduct, 4)
                        .HasQuantization(VectorQuantization.Scalar)
                        .HasEdgeOptions(32, 1600)
                        .AllowsFiltersOn(e => e.Filter1)
                        .AllowsFiltersOn(e => e.Filter2)
                        .AllowsFiltersOn(e => e.Filter3);
            });

        _ = async ? await db.Database.EnsureCreatedAsync() : db.Database.EnsureCreated();

        var index = ValidateIndex("FloatsVectorIndex", collection, "Floats", 4, "dotProduct", ["Filter1", "Filter2", "Filter3"]);

        var field = index["latestDefinition"]["fields"][0];
        Assert.Equal("scalar", field["quantization"].AsString);
        Assert.Equal(32, field["hnswOptions"]["maxEdges"].AsInt32);
        Assert.Equal(1600, field["hnswOptions"]["numEdgeCandidates"].AsInt32);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Create_vector_index_with_options_on_nested_entity(bool async, bool useStrings)
    {
        if (SkipForAtlas())
            return;

        var collection = database.CreateCollection<SimpleEntity>(values: async);
        using var db = SingleEntityDbContext.Create(collection,
            b =>
            {
                _ = useStrings
                    ? b.Entity(typeof(SimpleEntity).FullName!)
                        .OwnsOne(typeof(NestedEntity1), "Nested")
                        .HasIndex("Floats")
                        .IsVectorIndex(VectorSimilarity.DotProduct, 4)
                        .HasQuantization(VectorQuantization.Scalar)
                        .HasEdgeOptions(32, 1600)
                        .AllowsFiltersOn("FilterN1")
                        .AllowsFiltersOn("Nested.FilterN2")
                    : b.Entity<SimpleEntity>()
                        .OwnsOne(e => e.Nested)
                        .HasIndex(e => e.Floats)
                        .IsVectorIndex(VectorSimilarity.DotProduct, 4)
                        .HasQuantization(VectorQuantization.Scalar)
                        .HasEdgeOptions(32, 1600)
                        .AllowsFiltersOn(e => e.FilterN1)
                        .AllowsFiltersOn(e => e.Nested.FilterN2);
            });

        _ = async ? await db.Database.EnsureCreatedAsync() : db.Database.EnsureCreated();

        var index = ValidateIndex("FloatsVectorIndex", collection, "Nested.Floats", 4, "dotProduct",
            ["Nested.FilterN1", "Nested.Nested.FilterN2"]);

        var field = index["latestDefinition"]["fields"][0];
        Assert.Equal("scalar", field["quantization"].AsString);
        Assert.Equal(32, field["hnswOptions"]["maxEdges"].AsInt32);
        Assert.Equal(1600, field["hnswOptions"]["numEdgeCandidates"].AsInt32);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task Create_vector_index_with_options_and_name(bool async, bool useStrings)
    {
        if (SkipForAtlas())
            return;

        var collection = database.CreateCollection<SimpleEntity>(values: async);
        using var db = SingleEntityDbContext.Create(collection,
            b =>
            {
                _ = useStrings
                    ? b.Entity(typeof(SimpleEntity).FullName!)
                        .HasIndex(["Floats"], "Shadowfell")
                        .IsVectorIndex(VectorSimilarity.DotProduct, 8)
                        .AllowsFiltersOn("Filter1")
                        .AllowsFiltersOn("Filter3")
                        .HasQuantization(VectorQuantization.Binary)
                        .HasEdgeOptions(32, 200)
                    : b.Entity<SimpleEntity>()
                        .HasIndex(e => e.Floats, "Shadowfell")
                        .IsVectorIndex(VectorSimilarity.DotProduct, 8)
                        .AllowsFiltersOn(e => e.Filter1)
                        .AllowsFiltersOn(e => e.Filter3)
                        .HasQuantization(VectorQuantization.Binary)
                        .HasEdgeOptions(32, 200);
            });

        _ = async ? await db.Database.EnsureCreatedAsync() : db.Database.EnsureCreated();

        var index = ValidateIndex("Shadowfell", collection, "Floats", 8, "dotProduct", ["Filter1", "Filter3"]);

        var field = index["latestDefinition"]["fields"][0];
        Assert.Equal("binary", field["quantization"].AsString);
        Assert.Equal(32, field["hnswOptions"]["maxEdges"].AsInt32);
        Assert.Equal(200, field["hnswOptions"]["numEdgeCandidates"].AsInt32);
    }

#if EF9 // HasIndex with name on owned types was accidentally missing in EF8

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Create_vector_index_with_options_and_name_on_nested_entity(bool async, bool useStrings)
    {
        if (SkipForAtlas())
            return;

        var collection = database.CreateCollection<SimpleEntity>(values: async);
        using var db = SingleEntityDbContext.Create(collection,
            b =>
            {
                _ = useStrings
                    ? b.Entity(typeof(SimpleEntity).FullName!)
                        .OwnsOne(typeof(NestedEntity1), "Nested")
                        .OwnsOne(typeof(NestedEntity2), "Nested")
                        .HasIndex(["Doubles"], "Shadowfell")
                        .IsVectorIndex(VectorSimilarity.DotProduct, 4)
                        .HasQuantization(VectorQuantization.Scalar)
                        .HasEdgeOptions(32, 1600)
                        .AllowsFiltersOn("FilterN2")
                    : b.Entity<SimpleEntity>()
                        .OwnsOne(e => e.Nested)
                        .OwnsOne(e => e.Nested)
                        .HasIndex(e => e.Doubles, "Shadowfell")
                        .IsVectorIndex(VectorSimilarity.DotProduct, 4)
                        .HasQuantization(VectorQuantization.Scalar)
                        .HasEdgeOptions(32, 1600)
                        .AllowsFiltersOn(e => e.FilterN2);
            });

        _ = async ? await db.Database.EnsureCreatedAsync() : db.Database.EnsureCreated();

        var index = ValidateIndex("Shadowfell", collection, "Nested.Nested.Doubles", 4, "dotProduct", ["Nested.Nested.FilterN2"]);

        var field = index["latestDefinition"]["fields"][0];
        Assert.Equal("scalar", field["quantization"].AsString);
        Assert.Equal(32, field["hnswOptions"]["maxEdges"].AsInt32);
        Assert.Equal(1600, field["hnswOptions"]["numEdgeCandidates"].AsInt32);
    }

#endif

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task Create_vector_index_with_options_and_name_using_nested_builder(bool async, bool useStrings)
    {
        if (SkipForAtlas())
            return;

        var collection = database.CreateCollection<SimpleEntity>(values: async);
        using var db = SingleEntityDbContext.Create(collection,
            b =>
            {
                _ = useStrings
                    ? b.Entity(typeof(SimpleEntity).FullName!)
                        .HasIndex(["Floats"], "Shadowfell")
                        .IsVectorIndex(VectorSimilarity.DotProduct, 8,
                            b =>
                            {
                                b.AllowsFiltersOn("Filter1");
                                b.AllowsFiltersOn("Filter3");
                                b.HasQuantization(VectorQuantization.Binary);
                                b.HasEdgeOptions(32, 200);
                            })
                    : b.Entity<SimpleEntity>()
                        .HasIndex(e => e.Floats, "Shadowfell")
                        .IsVectorIndex(VectorSimilarity.DotProduct, 8,
                            b =>
                            {
                                b.AllowsFiltersOn(e => e.Filter1);
                                b.AllowsFiltersOn(e => e.Filter3);
                                b.HasQuantization(VectorQuantization.Binary);
                                b.HasEdgeOptions(32, 200);

                            });
            });

        _ = async ? await db.Database.EnsureCreatedAsync() : db.Database.EnsureCreated();

        var index = ValidateIndex("Shadowfell", collection, "Floats", 8, "dotProduct", ["Filter1", "Filter3"]);

        var field = index["latestDefinition"]["fields"][0];
        Assert.Equal("binary", field["quantization"].AsString);
        Assert.Equal(32, field["hnswOptions"]["maxEdges"].AsInt32);
        Assert.Equal(200, field["hnswOptions"]["numEdgeCandidates"].AsInt32);
    }

#if EF9 // HasIndex with name on owned types was accidentally missing in EF8

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Create_vector_index_with_options_and_name_on_nested_entity_using_nested_builder(bool async, bool useStrings)
    {
        if (SkipForAtlas())
            return;

        var collection = database.CreateCollection<SimpleEntity>(values: async);
        using var db = SingleEntityDbContext.Create(collection,
            b =>
            {
                _ = useStrings
                    ? b.Entity(typeof(SimpleEntity).FullName!)
                        .OwnsOne(typeof(NestedEntity1), "Nested")
                        .OwnsOne(typeof(NestedEntity2), "Nested")
                        .HasIndex(["Doubles"], "Shadowfell")
                        .IsVectorIndex(VectorSimilarity.DotProduct, 4, b =>
                        {
                            b.HasQuantization(VectorQuantization.Scalar);
                            b.HasEdgeOptions(32, 1600);
                            b.AllowsFiltersOn("FilterN2");

                        })
                    : b.Entity<SimpleEntity>()
                        .OwnsOne(e => e.Nested)
                        .OwnsOne(e => e.Nested)
                        .HasIndex(e => e.Doubles, "Shadowfell")
                        .IsVectorIndex(VectorSimilarity.DotProduct, 4,
                            b =>
                            {
                                b.HasQuantization(VectorQuantization.Scalar);
                                b.HasEdgeOptions(32, 1600);
                                b.AllowsFiltersOn(e => e.FilterN2);
                            });
            });

        _ = async ? await db.Database.EnsureCreatedAsync() : db.Database.EnsureCreated();

        var index = ValidateIndex("Shadowfell", collection, "Nested.Nested.Doubles", 4, "dotProduct", ["Nested.Nested.FilterN2"]);

        var field = index["latestDefinition"]["fields"][0];
        Assert.Equal("scalar", field["quantization"].AsString);
        Assert.Equal(32, field["hnswOptions"]["maxEdges"].AsInt32);
        Assert.Equal(1600, field["hnswOptions"]["numEdgeCandidates"].AsInt32);
    }

#endif

    private int GetIndexCount(IMongoCollection<SimpleEntity> collection)
        => database.GetCollection<BsonDocument>(collection.CollectionNamespace).Indexes.List().ToList().Count;

    private int GetVectorIndexCount(IMongoCollection<SimpleEntity> collection)
        => database.GetCollection<BsonDocument>(collection.CollectionNamespace).SearchIndexes.List().ToList().Count;

    private BsonDocument ValidateIndex(string indexName, IMongoCollection<SimpleEntity> collection, string? expectedPath,
        int expectedDimensions, string? expectedSimilarity, List<string>? expectedFilterPaths)
    {
        var indexes = database.GetCollection<BsonDocument>(collection.CollectionNamespace).SearchIndexes.List().ToList();
        var index = indexes.Single(i => i["name"].AsString == indexName);

        var fields = index["latestDefinition"]["fields"].AsBsonArray;

        Assert.Equal("vectorSearch", index["type"].AsString);
        Assert.Equal("READY", index["status"].AsString);
        Assert.True(index["queryable"].AsBoolean);
        Assert.Equal(0, index["latestVersion"].AsInt32);

        var field = fields[0];
        Assert.Equal("vector", field["type"].AsString);
        Assert.Equal(expectedPath, field["path"].AsString);
        Assert.Equal(expectedDimensions, field["numDimensions"].AsInt32);
        Assert.Equal(expectedSimilarity, field["similarity"].AsString);

        if (expectedFilterPaths != null)
        {
            Assert.Equal(expectedFilterPaths.Count, fields.Count - 1);
            for (var i = 1; i < expectedFilterPaths.Count; i++)
            {
                Assert.Equal(expectedFilterPaths[i - 1], fields[i]["path"].AsString);
            }
        }
        else
        {
            Assert.Single(fields);
        }

        return index;
    }

    [Fact]
    public void Query_throws_for_vector_index_specified_but_missing()
    {
        if (SkipForAtlas())
            return;

        var collection = database.CreateCollection<SimpleEntity>();
        using var db = SingleEntityDbContext.Create(collection);

        db.Database.EnsureCreated();

        Assert.Contains(
            "A vector query for 'SimpleEntity.Floats' could not be executed because vector index 'MissingIndex' was not defined in the EF Core model. " +
            "Use 'HasIndex' on the EF model builder to specify the index, or disable this warning if you have created your MongoDB indexes outside of EF Core.",
            Assert.Throws<InvalidOperationException>(() =>
                db.Set<SimpleEntity>()
                    .VectorSearch(e => e.Floats, new[] { 0.33f, -0.52f }, 2, new() { IndexName = "MissingIndex" })).Message);
    }

    [Fact]
    public void Query_throws_for_vector_index_specified_but_different()
    {
        if (SkipForAtlas())
            return;

        var collection = database.CreateCollection<SimpleEntity>();
        using var db = SingleEntityDbContext.Create(collection,
            b =>
            {
                b.Entity<SimpleEntity>().HasIndex(e => e.Floats, "Slarti")
                    .IsVectorIndex(VectorSimilarity.DotProduct, 8)
                    .HasQuantization(VectorQuantization.Binary)
                    .HasEdgeOptions(32, 200);
            });

        db.Database.EnsureCreated();

        Assert.Contains(
            "A vector query for 'SimpleEntity.Floats' could not be executed because vector index 'MissingIndex' was not defined in the EF Core model. " +
            "Vector query searches must use one of the indexes defined on the EF Core model.",
            Assert.Throws<InvalidOperationException>(() =>
                db.Set<SimpleEntity>()
                    .VectorSearch(e => e.Floats, new[] { 0.33f, -0.52f }, 2, new() { IndexName = "MissingIndex" })).Message);
    }

    [Fact]
    public void Query_throws_for_no_vector_index_when_not_specified()
    {
        if (SkipForAtlas())
            return;

        var collection = database.CreateCollection<SimpleEntity>();
        using var db = SingleEntityDbContext.Create(collection);

        db.Database.EnsureCreated();

        Assert.Contains(
            "A vector query for 'SimpleEntity.Floats' could not be executed because there are no vector indexes defined",
            Assert.Throws<InvalidOperationException>(() =>
                db.Set<SimpleEntity>().VectorSearch(e => e.Floats, new[] { 0.33f, -0.52f }, 2)).Message);
    }

    [Fact]
    public void Query_throws_for_multiple_vector_indexes_when_not_specified()
    {
        if (SkipForAtlas())
            return;

        var collection = database.CreateCollection<SimpleEntity>();
        using var db = SingleEntityDbContext.Create(collection,
            b =>
            {
                b.Entity<SimpleEntity>(b =>
                {
                    b.HasIndex(e => e.Floats, "Slarti")
                        .IsVectorIndex(VectorSimilarity.DotProduct, 8)
                        .HasQuantization(VectorQuantization.Binary)
                        .HasEdgeOptions(32, 200);

                    b.HasIndex(e => e.Floats, "Bartfast")
                        .IsVectorIndex(VectorSimilarity.Euclidean, 4)
                        .HasQuantization(VectorQuantization.Scalar);
                });
            });

        db.Database.EnsureCreated();

        Assert.Contains(
            "A vector query for 'SimpleEntity.Floats' could not be executed because multiple vector indexes are defined",
            Assert.Throws<InvalidOperationException>(() =>
                db.Set<SimpleEntity>().VectorSearch(e => e.Floats, new[] { 0.33f, -0.52f }, 2)).Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Query_does_not_throw_when_multiple_vector_indexes_but_one_specified(bool async)
    {
        if (SkipForAtlas())
            return;

        var collection = database.CreateCollection<SimpleEntity>(values: async);
        using var db = SingleEntityDbContext.Create(collection,
            b =>
            {
                b.Entity<SimpleEntity>(b =>
                {
                    b.HasIndex(e => e.Floats, "Slarti").IsVectorIndex(VectorSimilarity.DotProduct, 2);
                    b.HasIndex(e => e.Floats, "Bartfast").IsVectorIndex(VectorSimilarity.Euclidean, 2);
                    b.Ignore(e => e.Nested);
                });
            });

        var creationOptions = new MongoDatabaseCreationOptions(CreateMissingVectorIndexes: false, WaitForVectorIndexes: false);
        _ = async ? await db.Database.EnsureCreatedAsync(creationOptions) : db.Database.EnsureCreated(creationOptions);

        db.AddRange(new SimpleEntity { Floats = [0.36f, -0.57f] }, new SimpleEntity { Floats = [0.31f, -0.54f] });

        if (async)
        {
            await db.SaveChangesAsync();
            await db.Database.CreateMissingVectorIndexesAsync();
            await db.Database.WaitForVectorIndexesAsync();
        }
        else
        {
            db.SaveChanges();
            db.Database.CreateMissingVectorIndexes();
            db.Database.WaitForVectorIndexes();
        }

        var query = db.Set<SimpleEntity>()
            .VectorSearch(e => e.Floats, new[] { 0.33f, -0.52f }, 4, new() { IndexName = "Bartfast" });

        Assert.Equal(2, (async ? await query.ToListAsync() : query.ToList()).Count);
    }

    [Fact]
    public void Query_throws_for_unmapped_member()
    {
        if (SkipForAtlas())
            return;

        var collection = database.CreateCollection<SimpleEntity>();
        using var db = SingleEntityDbContext.Create(collection);

        db.Database.EnsureCreated();

        Assert.Contains(
            "Could not create a vector query for 'SimpleEntity.MoreFloats'.",
            Assert.Throws<InvalidOperationException>(() =>
                db.Set<SimpleEntity>().VectorSearch(e => e.MoreFloats, new[] { 0.33f, -0.52f }, 2)).Message);
    }

    private bool SkipForAtlas([CallerMemberName] string? caller = default)
        => TestServer.SkipForAtlas(testOutputHelper, caller!);

}
