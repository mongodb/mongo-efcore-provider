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
using Microsoft.EntityFrameworkCore.TestUtilities;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.Metadata;

namespace MongoDB.EntityFrameworkCore.SpecificationTests.Query;

public class VectorSearchMongoTest : IClassFixture<VectorSearchMongoTest.VectorSearchFixture>
{
    public VectorSearchMongoTest(VectorSearchFixture fixture)
    {
        Fixture = fixture;
        fixture.TestMqlLoggerFactory.Clear();
    }

    private VectorSearchFixture Fixture { get; }

    [ConditionalTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public virtual async Task VectorSearch_floats(bool async, bool specifyIndex)
    {
        await using var context = Fixture.CreateContext();
        var inputVector = new[] { 0.33f, -0.52f };

        var queryable = specifyIndex
            ? context.Set<Book>().VectorSearch(e => e.Floats, inputVector, limit: 4, new() { IndexName = "FloatsIndex" })
            : context.Set<Book>().VectorSearch(e => e.Floats, inputVector, limit: 4);

        var booksFromStore = async ? await queryable.ToListAsync() : queryable.ToList();

        Assert.Equal(4, booksFromStore.Count);
        Assert.Equal(
        [
            "Programming Entity Framework: Code First",
            "Programming Entity Framework",
            "Entity Framework Core in Action",
            "Programming Entity Framework: DbContext"
        ], booksFromStore.Select(e => e.Title));

        AssertMql(
            """
Books.{ "$vectorSearch" : { "queryVector" : [0.33000001311302185, -0.51999998092651367], "path" : "Floats", "limit" : 4, "numCandidates" : 40, "index" : "FloatsIndex" } }
""");
    }

    [ConditionalTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public virtual async Task VectorSearch_doubles(bool async, bool specifyIndex)
    {
        await using var context = Fixture.CreateContext();
        var inputVector = new[] { 0.33, -0.52 };

        var queryable = specifyIndex
            ? context.Set<Book>().VectorSearch(e => e.Doubles, inputVector, limit: 4, new() { IndexName = "DoublesIndex" })
            : context.Set<Book>().VectorSearch(e => e.Doubles, inputVector, limit: 4);

        var booksFromStore = async ? await queryable.ToListAsync() : queryable.ToList();

        Assert.Equal(4, booksFromStore.Count);
        Assert.Equal(
        [
            "Entity Framework Core in Action",
            "Programming Entity Framework",
            "Programming Entity Framework: Code First",
            "Programming Entity Framework: DbContext"
        ], booksFromStore.Select(e => e.Title));

        AssertMql(
            """
Books.{ "$vectorSearch" : { "queryVector" : [0.33000000000000002, -0.52000000000000002], "path" : "Doubles", "limit" : 4, "numCandidates" : 40, "index" : "DoublesIndex" } }
""");
    }

    [ConditionalTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public virtual async Task VectorSearch_Memory_floats(bool async, bool specifyIndex)
    {
        await using var context = Fixture.CreateContext();
        var inputVector = new[] { 0.33f, -0.52f };

        var queryable = specifyIndex
            ? context.Set<Book>().VectorSearch(e => e.MemoryFloats, inputVector, limit: 4, new() { IndexName = "MemoryFloatsIndex" })
            : context.Set<Book>().VectorSearch(e => e.MemoryFloats, inputVector, limit: 4);

        var booksFromStore = async ? await queryable.ToListAsync() : queryable.ToList();

        Assert.Equal(4, booksFromStore.Count);
        Assert.Equal(
        [
            "Programming Entity Framework: Code First",
            "Programming Entity Framework",
            "Entity Framework Core in Action",
            "Programming Entity Framework: DbContext"
        ], booksFromStore.Select(e => e.Title));

        AssertMql(
            """
Books.{ "$vectorSearch" : { "queryVector" : [0.33000001311302185, -0.51999998092651367], "path" : "MemoryFloats", "limit" : 4, "numCandidates" : 40, "index" : "MemoryFloatsIndex" } }
""");
    }

    [ConditionalTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public virtual async Task VectorSearch_Memory_doubles(bool async, bool specifyIndex)
    {
        await using var context = Fixture.CreateContext();
        var inputVector = new[] { 0.33, -0.52 };

        var queryable = specifyIndex
            ? context.Set<Book>().VectorSearch(e => e.MemoryDoubles, inputVector, limit: 4, new() { IndexName = "MemoryDoublesIndex" })
            : context.Set<Book>().VectorSearch(e => e.MemoryDoubles, inputVector, limit: 4);

        var booksFromStore = async ? await queryable.ToListAsync() : queryable.ToList();

        Assert.Equal(4, booksFromStore.Count);
        Assert.Equal(
        [
            "Entity Framework Core in Action",
            "Programming Entity Framework",
            "Programming Entity Framework: Code First",
            "Programming Entity Framework: DbContext"
        ], booksFromStore.Select(e => e.Title));

        AssertMql(
            """
Books.{ "$vectorSearch" : { "queryVector" : [0.33000000000000002, -0.52000000000000002], "path" : "MemoryDoubles", "limit" : 4, "numCandidates" : 40, "index" : "MemoryDoublesIndex" } }
""");
    }

    [ConditionalTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public virtual async Task VectorSearch_ReadOnlyMemory_floats(bool async, bool specifyIndex)
    {
        await using var context = Fixture.CreateContext();
        var inputVector = new[] { 0.33f, -0.52f };

        var queryable = specifyIndex
            ? context.Set<Book>().VectorSearch(e => e.ReadOnlyMemoryFloats, inputVector, limit: 4, new() { IndexName = "ReadOnlyMemoryFloatsIndex" })
            : context.Set<Book>().VectorSearch(e => e.ReadOnlyMemoryFloats, inputVector, limit: 4);

        var booksFromStore = async ? await queryable.ToListAsync() : queryable.ToList();

        Assert.Equal(4, booksFromStore.Count);
        Assert.Equal(
        [
            "Programming Entity Framework: Code First",
            "Programming Entity Framework",
            "Entity Framework Core in Action",
            "Programming Entity Framework: DbContext"
        ], booksFromStore.Select(e => e.Title));

        AssertMql(
            """
Books.{ "$vectorSearch" : { "queryVector" : [0.33000001311302185, -0.51999998092651367], "path" : "ReadOnlyMemoryFloats", "limit" : 4, "numCandidates" : 40, "index" : "ReadOnlyMemoryFloatsIndex" } }
""");
    }

    [ConditionalTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public virtual async Task VectorSearch_ReadOnlyMemory_doubles(bool async, bool specifyIndex)
    {
        await using var context = Fixture.CreateContext();
        var inputVector = new[] { 0.33, -0.52 };

        var queryable = specifyIndex
            ? context.Set<Book>().VectorSearch(e => e.ReadOnlyMemoryDoubles, inputVector, limit: 4, new() { IndexName = "ReadOnlyMemoryDoublesIndex" })
            : context.Set<Book>().VectorSearch(e => e.ReadOnlyMemoryDoubles, inputVector, limit: 4);

        var booksFromStore = async ? await queryable.ToListAsync() : queryable.ToList();

        Assert.Equal(4, booksFromStore.Count);
        Assert.Equal(
        [
            "Entity Framework Core in Action",
            "Programming Entity Framework",
            "Programming Entity Framework: Code First",
            "Programming Entity Framework: DbContext"
        ], booksFromStore.Select(e => e.Title));

        AssertMql(
            """
Books.{ "$vectorSearch" : { "queryVector" : [0.33000000000000002, -0.52000000000000002], "path" : "ReadOnlyMemoryDoubles", "limit" : 4, "numCandidates" : 40, "index" : "ReadOnlyMemoryDoublesIndex" } }
""");
    }

    [ConditionalTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public virtual async Task VectorSearch_binary_floats(bool async, bool specifyIndex)
    {
        await using var context = Fixture.CreateContext();
        var inputVector = new BinaryVectorFloat32(new[] { 0.33f, -0.52f });

        var queryable = specifyIndex
            ? context.Set<Book>().VectorSearch(e => e.BinaryFloats, inputVector, limit: 4, new() { IndexName = "BinaryFloatsIndex" })
            : context.Set<Book>().VectorSearch(e => e.BinaryFloats, inputVector, limit: 4);

        var booksFromStore = async ? await queryable.ToListAsync() : queryable.ToList();

        Assert.Equal(4, booksFromStore.Count);
        Assert.Equal(
        [
            "Programming Entity Framework",
            "Programming Entity Framework: Code First",
            "Programming Entity Framework: DbContext",
            "Entity Framework Core in Action"
        ], booksFromStore.Select(e => e.Title));

        AssertMql(
            """
Books.{ "$vectorSearch" : { "queryVector" : { "$binary" : { "base64" : "JwDD9ag+uB4Fvw==", "subType" : "09" } }, "path" : "BinaryFloats", "limit" : 4, "numCandidates" : 40, "index" : "BinaryFloatsIndex" } }
""");
    }

    [ConditionalTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public virtual async Task VectorSearch_binary_bytes(bool async, bool specifyIndex)
    {
        await using var context = Fixture.CreateContext();
        var inputVector = new BinaryVectorPackedBit(new byte[] { 0x3A, 0x17 }, 0);

        var queryable = specifyIndex
            ? context.Set<Book>().VectorSearch(e => e.BinaryBytes, inputVector, limit: 4, new() { IndexName = "BinaryBytesIndex" })
            : context.Set<Book>().VectorSearch(e => e.BinaryBytes, inputVector, limit: 4);

        var booksFromStore = async ? await queryable.ToListAsync() : queryable.ToList();

        Assert.Equal(4, booksFromStore.Count);
        Assert.Equal(
        [
            "Programming Entity Framework: Code First",
            "Programming Entity Framework",
            "Entity Framework Core in Action",
            "Programming Entity Framework: DbContext"
        ], booksFromStore.Select(e => e.Title));

        AssertMql(
            """
Books.{ "$vectorSearch" : { "queryVector" : { "$binary" : { "base64" : "EAA6Fw==", "subType" : "09" } }, "path" : "BinaryBytes", "limit" : 4, "numCandidates" : 40, "index" : "BinaryBytesIndex" } }
""");
    }

    [ConditionalTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public virtual async Task VectorSearch_binary_sbytes(bool async, bool specifyIndex)
    {
        await using var context = Fixture.CreateContext();
        var inputVector = new BinaryVectorInt8(new sbyte[] { 1, -2 });

        var queryable = specifyIndex
            ? context.Set<Book>().VectorSearch(e => e.BinarySBytes, inputVector, limit: 4, new() { IndexName = "BinarySBytesIndex" })
            : context.Set<Book>().VectorSearch(e => e.BinarySBytes, inputVector, limit: 4);

        var booksFromStore = async ? await queryable.ToListAsync() : queryable.ToList();

        Assert.Equal(4, booksFromStore.Count);
        Assert.Equal(
        [
            "Programming Entity Framework",
            "Programming Entity Framework: Code First",
            "Programming Entity Framework: DbContext",
            "Entity Framework Core in Action"
        ], booksFromStore.Select(e => e.Title));

        AssertMql(
            """
Books.{ "$vectorSearch" : { "queryVector" : { "$binary" : { "base64" : "AwAB/g==", "subType" : "09" } }, "path" : "BinarySBytes", "limit" : 4, "numCandidates" : 40, "index" : "BinarySBytesIndex" } }
""");
    }

    [ConditionalTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public virtual async Task VectorSearch_binary_floats_as_Memory(bool async, bool specifyIndex)
    {
        await using var context = Fixture.CreateContext();
        var inputVector = new BinaryVectorFloat32(new[] { 0.33f, -0.52f });

        var queryable = specifyIndex
            ? context.Set<Book>().VectorSearch(e => e.BinaryMemoryFloats, inputVector, limit: 4, new() { IndexName = "BinaryMemoryFloatsIndex" })
            : context.Set<Book>().VectorSearch(e => e.BinaryMemoryFloats, inputVector, limit: 4);

        var booksFromStore = async ? await queryable.ToListAsync() : queryable.ToList();

        Assert.Equal(4, booksFromStore.Count);
        Assert.Equal(
        [
            "Programming Entity Framework",
            "Programming Entity Framework: Code First",
            "Programming Entity Framework: DbContext",
            "Entity Framework Core in Action"
        ], booksFromStore.Select(e => e.Title));

        AssertMql(
            """
Books.{ "$vectorSearch" : { "queryVector" : { "$binary" : { "base64" : "JwDD9ag+uB4Fvw==", "subType" : "09" } }, "path" : "BinaryMemoryFloats", "limit" : 4, "numCandidates" : 40, "index" : "BinaryMemoryFloatsIndex" } }
""");
    }

    [ConditionalTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public virtual async Task VectorSearch_binary_bytes_as_Memory(bool async, bool specifyIndex)
    {
        await using var context = Fixture.CreateContext();
        var inputVector = new BinaryVectorPackedBit(new byte[] { 0x3A, 0x17 }, 0);

        var queryable = specifyIndex
            ? context.Set<Book>().VectorSearch(e => e.BinaryMemoryBytes, inputVector, limit: 4, new() { IndexName = "BinaryMemoryBytesIndex" })
            : context.Set<Book>().VectorSearch(e => e.BinaryMemoryBytes, inputVector, limit: 4);

        var booksFromStore = async ? await queryable.ToListAsync() : queryable.ToList();

        Assert.Equal(4, booksFromStore.Count);
        Assert.Equal(
        [
            "Programming Entity Framework: Code First",
            "Programming Entity Framework",
            "Entity Framework Core in Action",
            "Programming Entity Framework: DbContext"
        ], booksFromStore.Select(e => e.Title));

        AssertMql(
            """
Books.{ "$vectorSearch" : { "queryVector" : { "$binary" : { "base64" : "EAA6Fw==", "subType" : "09" } }, "path" : "BinaryMemoryBytes", "limit" : 4, "numCandidates" : 40, "index" : "BinaryMemoryBytesIndex" } }
""");
    }

    [ConditionalTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public virtual async Task VectorSearch_binary_sbytes_as_Memory(bool async, bool specifyIndex)
    {
        await using var context = Fixture.CreateContext();
        var inputVector = new BinaryVectorInt8(new sbyte[] { 1, -2 });

        var queryable = specifyIndex
            ? context.Set<Book>().VectorSearch(e => e.BinaryMemorySBytes, inputVector, limit: 4, new() { IndexName = "BinaryMemorySBytesIndex" })
            : context.Set<Book>().VectorSearch(e => e.BinaryMemorySBytes, inputVector, limit: 4);

        var booksFromStore = async ? await queryable.ToListAsync() : queryable.ToList();

        Assert.Equal(4, booksFromStore.Count);
        Assert.Equal(
        [
            "Programming Entity Framework",
            "Programming Entity Framework: Code First",
            "Programming Entity Framework: DbContext",
            "Entity Framework Core in Action"
        ], booksFromStore.Select(e => e.Title));

        AssertMql(
            """
Books.{ "$vectorSearch" : { "queryVector" : { "$binary" : { "base64" : "AwAB/g==", "subType" : "09" } }, "path" : "BinaryMemorySBytes", "limit" : 4, "numCandidates" : 40, "index" : "BinaryMemorySBytesIndex" } }
""");
    }

    [ConditionalTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public virtual async Task VectorSearch_binary_floats_as_ReadOnlyMemory(bool async, bool specifyIndex)
    {
        await using var context = Fixture.CreateContext();
        var inputVector = new BinaryVectorFloat32(new[] { 0.33f, -0.52f });

        var queryable = specifyIndex
            ? context.Set<Book>().VectorSearch(e => e.BinaryReadOnlyMemoryFloats, inputVector, limit: 4, new() { IndexName = "BinaryReadOnlyMemoryFloatsIndex" })
            : context.Set<Book>().VectorSearch(e => e.BinaryReadOnlyMemoryFloats, inputVector, limit: 4);

        var booksFromStore = async ? await queryable.ToListAsync() : queryable.ToList();

        Assert.Equal(4, booksFromStore.Count);
        Assert.Equal(
        [
            "Programming Entity Framework",
            "Programming Entity Framework: Code First",
            "Programming Entity Framework: DbContext",
            "Entity Framework Core in Action"
        ], booksFromStore.Select(e => e.Title));

        AssertMql(
            """
Books.{ "$vectorSearch" : { "queryVector" : { "$binary" : { "base64" : "JwDD9ag+uB4Fvw==", "subType" : "09" } }, "path" : "BinaryReadOnlyMemoryFloats", "limit" : 4, "numCandidates" : 40, "index" : "BinaryReadOnlyMemoryFloatsIndex" } }
""");
    }

    [ConditionalTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public virtual async Task VectorSearch_binary_bytes_as_ReadOnlyMemory(bool async, bool specifyIndex)
    {
        await using var context = Fixture.CreateContext();
        var inputVector = new BinaryVectorPackedBit(new byte[] { 0x3A, 0x17 }, 0);

        var queryable = specifyIndex
            ? context.Set<Book>().VectorSearch(e => e.BinaryReadOnlyMemoryBytes, inputVector, limit: 4, new() { IndexName = "BinaryReadOnlyMemoryBytesIndex" })
            : context.Set<Book>().VectorSearch(e => e.BinaryReadOnlyMemoryBytes, inputVector, limit: 4);

        var booksFromStore = async ? await queryable.ToListAsync() : queryable.ToList();

        Assert.Equal(4, booksFromStore.Count);
        Assert.Equal(
        [
            "Programming Entity Framework: Code First",
            "Programming Entity Framework",
            "Entity Framework Core in Action",
            "Programming Entity Framework: DbContext"
        ], booksFromStore.Select(e => e.Title));

        AssertMql(
            """
Books.{ "$vectorSearch" : { "queryVector" : { "$binary" : { "base64" : "EAA6Fw==", "subType" : "09" } }, "path" : "BinaryReadOnlyMemoryBytes", "limit" : 4, "numCandidates" : 40, "index" : "BinaryReadOnlyMemoryBytesIndex" } }
""");
    }

    [ConditionalTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public virtual async Task VectorSearch_binary_sbytes_as_ReadOnlyMemory(bool async, bool specifyIndex)
    {
        await using var context = Fixture.CreateContext();
        var inputVector = new BinaryVectorInt8(new sbyte[] { 1, -2 });

        var queryable = specifyIndex
            ? context.Set<Book>().VectorSearch(e => e.BinaryReadOnlyMemorySBytes, inputVector, limit: 4, new() { IndexName = "BinaryReadOnlyMemorySBytesIndex" })
            : context.Set<Book>().VectorSearch(e => e.BinaryReadOnlyMemorySBytes, inputVector, limit: 4);

        var booksFromStore = async ? await queryable.ToListAsync() : queryable.ToList();

        Assert.Equal(4, booksFromStore.Count);
        Assert.Equal(
        [
            "Programming Entity Framework",
            "Programming Entity Framework: Code First",
            "Programming Entity Framework: DbContext",
            "Entity Framework Core in Action"
        ], booksFromStore.Select(e => e.Title));

        AssertMql(
            """
Books.{ "$vectorSearch" : { "queryVector" : { "$binary" : { "base64" : "AwAB/g==", "subType" : "09" } }, "path" : "BinaryReadOnlyMemorySBytes", "limit" : 4, "numCandidates" : 40, "index" : "BinaryReadOnlyMemorySBytesIndex" } }
""");
    }

    [ConditionalTheory]
    [InlineData(false)]
    [InlineData(true)]
    public virtual async Task VectorSearch_floats_after_filter(bool async)
    {
        await using var context = Fixture.CreateContext();
        var inputVector = new[] { 0.33f, -0.52f };

        var queryable = context
            .Set<Book>()
            .Where(e => e.IsPublished)
            .VectorSearch(e => e.Floats, inputVector, limit: 4, new() { IndexName = "FloatsIndex" });

        // Fails: Vector search must be the first operator
        Assert.Contains(
            "$vectorSearch is only valid as the first stage in a pipeline.",
            (await Assert.ThrowsAsync<MongoCommandException>(async () =>
            {
                var booksFromStore = async ? await queryable.ToListAsync() : queryable.ToList();

                Assert.Equal(4, booksFromStore.Count);
                Assert.Equal(
                [
                    "Programming Entity Framework: DbContext",
                    "Entity Framework Core in Action",
                    "Programming Entity Framework",
                    "Programming Entity Framework: Code First"
                ], booksFromStore.Select(e => e.Title));
            })).Message);

        AssertMql(
            """
Books.{ "$match" : { "IsPublished" : true } }, { "$vectorSearch" : { "queryVector" : [0.33000001311302185, -0.51999998092651367], "path" : "Floats", "limit" : 4, "numCandidates" : 40, "index" : "FloatsIndex" } }
""");
    }

    [ConditionalTheory]
    [InlineData(false)]
    [InlineData(true)]
    public virtual async Task VectorSearch_floats_before_filter(bool async)
    {
        await using var context = Fixture.CreateContext();
        var inputVector = new[] { 0.33f, -0.52f };

        var queryable = context
            .Set<Book>()
            .VectorSearch(e => e.Floats, inputVector, limit: 4, new() { IndexName = "FloatsIndex" })
            .Where(e => e.IsPublished);

        // Fails: Cannot compose after vector search
        Assert.Contains(
            "could not be translated",
            (await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                var booksFromStore = async ? await queryable.ToListAsync() : queryable.ToList();

                Assert.Equal(4, booksFromStore.Count);
                Assert.Equal(
                [
                    "Programming Entity Framework: DbContext",
                    "Entity Framework Core in Action",
                    "Programming Entity Framework",
                    "Programming Entity Framework: Code First"
                ], booksFromStore.Select(e => e.Title));
            })).Message);

        AssertMql(
);
    }

    [ConditionalTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public virtual async Task VectorSearch_floats_on_nested_reference(bool async, bool specifyIndex)
    {
        await using var context = Fixture.CreateContext();
        var inputVector = new[] { 0.33f, -0.52f };

        var queryable = specifyIndex
            ? context.Set<Book>().VectorSearch(e => e.Preface.Floats, inputVector, limit: 4, new() { IndexName = "PrefaceFloatsIndex" })
            : context.Set<Book>().VectorSearch(e => e.Preface.Floats, inputVector, limit: 4);

        var booksFromStore = async ? await queryable.ToListAsync() : queryable.ToList();

        Assert.Equal(4, booksFromStore.Count);
        Assert.Equal(
        [
            "Programming Entity Framework",
            "Entity Framework Core in Action",
            "Programming Entity Framework: DbContext",
            "Programming Entity Framework: Code First"
        ], booksFromStore.Select(e => e.Title));

        AssertMql(
            """
Books.{ "$vectorSearch" : { "queryVector" : [0.33000001311302185, -0.51999998092651367], "path" : "Preface.Floats", "limit" : 4, "numCandidates" : 40, "index" : "PrefaceFloatsIndex" } }
""");
    }

    private class Book
    {
        public ObjectId Id { get; set; }
        public string Title { get; set; } = null!;
        public string Author { get; set; } = null!;
        public byte[] Isbn { get; set; } = null!;
        public bool IsPublished { get; set; }

        public float[] Floats { get; set; }
        public double[] Doubles { get; set; }

        public Memory<float> MemoryFloats { get; set; }

        public Memory<double> MemoryDoubles { get; set; }

        public ReadOnlyMemory<float> ReadOnlyMemoryFloats { get; set; }

        public ReadOnlyMemory<double> ReadOnlyMemoryDoubles { get; set; }

        [BinaryVector(BinaryVectorDataType.Float32)]
        public float[]? BinaryFloats { get; set; }

        [BinaryVector(BinaryVectorDataType.PackedBit)]
        public byte[]? BinaryBytes { get; set; }

        [BinaryVector(BinaryVectorDataType.Int8)]
        public sbyte[]? BinarySBytes { get; set; }

        [BinaryVector(BinaryVectorDataType.Float32)]
        public Memory<float> BinaryMemoryFloats { get; set; }

        [BinaryVector(BinaryVectorDataType.PackedBit)]
        public Memory<byte> BinaryMemoryBytes { get; set; }

        [BinaryVector(BinaryVectorDataType.Int8)]
        public Memory<sbyte> BinaryMemorySBytes { get; set; }

        [BinaryVector(BinaryVectorDataType.Float32)]
        public ReadOnlyMemory<float> BinaryReadOnlyMemoryFloats { get; set; }

        [BinaryVector(BinaryVectorDataType.PackedBit)]
        public ReadOnlyMemory<byte> BinaryReadOnlyMemoryBytes { get; set; }

        [BinaryVector(BinaryVectorDataType.Int8)]
        public ReadOnlyMemory<sbyte> BinaryReadOnlyMemorySBytes { get; set; }

        //public BinaryVectorFloat32 BinaryNativeFloats { get; set; }
        //public BinaryVectorInt8 BinaryNativeBytes { get; set; }
        //public BinaryVectorPackedBit BinaryNativePackedBits { get; set; }

        public Preface? Preface { get; set; }
    }

    private class Preface
    {
        public string Author { get; set; } = null!;
        public float[]? Floats { get; set; }
        public double[]? Doubles { get; set; }
    }

    private void AssertMql(params string[] expected)
        => Fixture.TestMqlLoggerFactory.AssertBaseline(expected);

    public class VectorSearchFixture : SharedStoreFixtureBase<PoolableDbContext>
    {
        private static readonly string DatabaseName = TestServer.GetUniqueDatabaseName("VectorSearchTest");

        protected override string StoreName
            => DatabaseName;

        protected override void OnModelCreating(ModelBuilder modelBuilder, DbContext context)
            => modelBuilder.Entity<Book>(b =>
            {
                b.ToCollection("Books");
                b.OwnsOne(x => x.Preface, b =>
                {
                    b.HasIndex(e => e.Floats,"PrefaceFloatsIndex").IsVectorIndex(VectorSimilarity.Cosine, 2);
                });

                b.HasIndex(e => e.Floats, "FloatsIndex").IsVectorIndex(VectorSimilarity.Cosine, 2);
                b.HasIndex(e => e.Doubles, "DoublesIndex").IsVectorIndex(VectorSimilarity.Euclidean, 2);
                b.HasIndex(e => e.MemoryFloats, "MemoryFloatsIndex").IsVectorIndex(VectorSimilarity.Cosine, 2);
                b.HasIndex(e => e.MemoryDoubles, "MemoryDoublesIndex").IsVectorIndex(VectorSimilarity.Euclidean, 2);
                b.HasIndex(e => e.ReadOnlyMemoryFloats, "ReadOnlyMemoryFloatsIndex").IsVectorIndex(VectorSimilarity.Cosine, 2);
                b.HasIndex(e => e.ReadOnlyMemoryDoubles, "ReadOnlyMemoryDoublesIndex").IsVectorIndex(VectorSimilarity.Euclidean, 2);
                b.HasIndex(e => e.BinaryFloats, "BinaryFloatsIndex").IsVectorIndex(VectorSimilarity.DotProduct, 2);
                b.HasIndex(e => e.BinaryBytes, "BinaryBytesIndex").IsVectorIndex(VectorSimilarity.Euclidean, 16);
                b.HasIndex(e => e.BinarySBytes, "BinarySBytesIndex").IsVectorIndex(VectorSimilarity.Cosine, 2);
                b.HasIndex(e => e.BinaryMemoryFloats, "BinaryMemoryFloatsIndex").IsVectorIndex(VectorSimilarity.DotProduct, 2);
                b.HasIndex(e => e.BinaryMemoryBytes, "BinaryMemoryBytesIndex").IsVectorIndex(VectorSimilarity.Euclidean, 16);
                b.HasIndex(e => e.BinaryMemorySBytes, "BinaryMemorySBytesIndex").IsVectorIndex(VectorSimilarity.Cosine, 2);
                b.HasIndex(e => e.BinaryReadOnlyMemoryFloats, "BinaryReadOnlyMemoryFloatsIndex").IsVectorIndex(VectorSimilarity.DotProduct, 2);
                b.HasIndex(e => e.BinaryReadOnlyMemoryBytes, "BinaryReadOnlyMemoryBytesIndex").IsVectorIndex(VectorSimilarity.Euclidean, 16);
                b.HasIndex(e => e.BinaryReadOnlyMemorySBytes, "BinaryReadOnlyMemorySBytesIndex").IsVectorIndex(VectorSimilarity.Cosine, 2);
            });

        #if EF9
        protected override Task SeedAsync(PoolableDbContext context)
            => TrackSeedEntities(context).SaveChangesAsync();
        #else
        protected override void Seed(PoolableDbContext context)
            => TrackSeedEntities(context).SaveChanges();
        #endif

        private static PoolableDbContext TrackSeedEntities(PoolableDbContext context)
        {
            var book1 = new Book
            {
                Author = "Jon P Smith",
                Isbn = [1, 1, 1, 1],
                Title = "Entity Framework Core in Action",
                Floats = [0.332f, -0.562f],
                Doubles = [0.332, -0.532],
                MemoryFloats = new([0.332f, -0.562f]),
                MemoryDoubles = new([0.332, -0.532]),
                ReadOnlyMemoryFloats = new([0.332f, -0.562f]),
                ReadOnlyMemoryDoubles = new([0.332, -0.532]),
                BinaryFloats = [0.733f, -0.323f],
                BinaryBytes = [0x71, 0xA7],
                BinarySBytes = [3, 4],
                BinaryMemoryFloats = new([0.733f, -0.323f]),
                BinaryMemoryBytes = new([0x71, 0xA7]),
                BinaryMemorySBytes = new([3, 4]),
                BinaryReadOnlyMemoryFloats = new([0.733f, -0.323f]),
                BinaryReadOnlyMemoryBytes = new([0x71, 0xA7]),
                BinaryReadOnlyMemorySBytes = new([3, 4]),
                Preface = new Preface { Author = "Judge Dread", Floats = [0.363f, -0.562f], },
            };

            var book2 = new Book
            {
                Author = "Julie Lerman",
                Title = "Programming Entity Framework: DbContext",
                Isbn = [1, 1, 1, 2],
                Floats = [0.338f, -0.582f],
                Doubles = [0.833, -0.582],
                MemoryFloats = new([0.338f, -0.582f]),
                MemoryDoubles = new([0.833, -0.582]),
                ReadOnlyMemoryFloats = new([0.338f, -0.582f]),
                ReadOnlyMemoryDoubles = new([0.833, -0.582]),
                BinaryFloats = [0.738f, -0.326f],
                BinaryBytes = [0x31, 0xF2],
                BinarySBytes = [7, 8],
                BinaryMemoryFloats = new([0.738f, -0.326f]),
                BinaryMemoryBytes = new([0x31, 0xF2]),
                BinaryMemorySBytes = new([7, 8]),
                BinaryReadOnlyMemoryFloats = new([0.738f, -0.326f]),
                BinaryReadOnlyMemoryBytes = new([0x31, 0xF2]),
                BinaryReadOnlyMemorySBytes = new([7, 8]),
                Preface = new Preface { Author = "Diego Vega", Floats = [0.533f, -0.526f], Doubles = [0.337, -0.523], },
            };

            var book3 = new Book
            {
                Author = "Julie Lerman",
                Title = "Programming Entity Framework",
                Isbn = [1, 1, 1, 3],
                Floats = [0.334f, -0.542f],
                Doubles = [0.373, -0.562],
                MemoryFloats = new([0.334f, -0.542f]),
                MemoryDoubles = new([0.373, -0.562]),
                ReadOnlyMemoryFloats = new([0.334f, -0.542f]),
                ReadOnlyMemoryDoubles = new([0.373, -0.562]),
                BinaryFloats = [0.783f, -0.392f],
                BinaryBytes = [0x35, 0x27],
                BinarySBytes = [9, -10],
                BinaryMemoryFloats = new([0.783f, -0.392f]),
                BinaryMemoryBytes = new([0x35, 0x27]),
                BinaryMemorySBytes = new([9, -10]),
                BinaryReadOnlyMemoryFloats = new([0.783f, -0.392f]),
                BinaryReadOnlyMemoryBytes = new([0x35, 0x27]),
                BinaryReadOnlyMemorySBytes = new([9, -10]),
                Preface = new Preface { Author = "Danny Simmons", Floats = [0.333f, -0.521f], Doubles = [0.333, -0.522], },
            };

            var book4 = new Book
            {
                Author = "Julie Lerman",
                Title = "Programming Entity Framework: Code First",
                Isbn = [1, 1, 1, 3],
                Floats = [0.333f, -0.526f],
                Doubles = [0.333, -0.452],
                MemoryFloats = new([0.333f, -0.526f]),
                MemoryDoubles = new([0.333, -0.452]),
                ReadOnlyMemoryFloats = new([0.333f, -0.526f]),
                ReadOnlyMemoryDoubles = new([0.333, -0.452]),
                BinaryFloats = [0.773f, -0.327f],
                BinaryBytes = [0x23, 0x55],
                BinarySBytes = [11, 12],
                BinaryMemoryFloats = new([0.773f, -0.327f]),
                BinaryMemoryBytes = new([0x23, 0x55]),
                BinaryMemorySBytes = new([11, 12]),
                BinaryReadOnlyMemoryFloats = new([0.773f, -0.327f]),
                BinaryReadOnlyMemoryBytes = new([0x23, 0x55]),
                BinaryReadOnlyMemorySBytes = new([11, 12]),
                Preface = new Preface { Author = "Ford Prefect", Floats = [0.733f, -0.527f], Doubles = [0.337, -0.752], },
            };

            context.AddRange(book1, book2, book3, book4);

            return context;
        }

        public TestMqlLoggerFactory TestMqlLoggerFactory
            => (TestMqlLoggerFactory)ListLoggerFactory;

        protected override ITestStoreFactory TestStoreFactory
            => MongoTestStoreFactory.Instance;
    }
}
