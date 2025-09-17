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
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.TestUtilities;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Metadata;
using MongoDB.EntityFrameworkCore.Storage;

namespace MongoDB.EntityFrameworkCore.SpecificationTests.Query;

public abstract class VectorSearchMongoTestBase
{
    protected VectorSearchMongoTestBase(VectorSearchFixtureBase fixture)
    {
        Fixture = fixture;
        fixture.TestMqlLoggerFactory.Clear();
    }

    protected VectorSearchFixtureBase Fixture { get; }

    [ConditionalTheory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public virtual async Task VectorSearch_floats(bool async, bool specifyIndex)
    {
        await using var context = Fixture.CreateContext();
        var inputVector = new[] { 0.33f, -0.52f };

        var queryable = context.Set<Book>()
            .VectorSearch(e => e.Floats, inputVector, limit: 4, CreateQueryOptions(specifyIndex ? "FloatsIndex" : null));

        var booksFromStore = async ? await queryable.ToListAsync() : queryable.ToList();

        Assert.Equal(4, booksFromStore.Count);
        Assert.Equal(
        [
            "Programming Entity Framework: Code First",
            "Programming Entity Framework",
            "Entity Framework Core in Action",
            "Programming Entity Framework: DbContext"
        ], booksFromStore.Select(e => e.Title));
    }

    [ConditionalTheory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public virtual async Task VectorSearch_doubles(bool async, bool specifyIndex)
    {
        await using var context = Fixture.CreateContext();
        var inputVector = new[] { 0.33, -0.52 };

        var queryable = context.Set<Book>()
            .VectorSearch(e => e.Doubles, inputVector, limit: 4, CreateQueryOptions(specifyIndex ? "DoublesIndex" : null));

        var booksFromStore = async ? await queryable.ToListAsync() : queryable.ToList();

        Assert.Equal(4, booksFromStore.Count);
        Assert.Equal(
        [
            "Entity Framework Core in Action",
            "Programming Entity Framework",
            "Programming Entity Framework: Code First",
            "Programming Entity Framework: DbContext"
        ], booksFromStore.Select(e => e.Title));
    }

    [ConditionalTheory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public virtual async Task VectorSearch_Memory_floats(bool async, bool specifyIndex)
    {
        await using var context = Fixture.CreateContext();
        var inputVector = new[] { 0.33f, -0.52f };

        var queryable = context.Set<Book>()
            .VectorSearch(e => e.MemoryFloats, inputVector, limit: 4, CreateQueryOptions(specifyIndex ? "MemoryFloatsIndex" : null));

        var booksFromStore = async ? await queryable.ToListAsync() : queryable.ToList();

        Assert.Equal(4, booksFromStore.Count);
        Assert.Equal(
        [
            "Programming Entity Framework: Code First",
            "Programming Entity Framework",
            "Entity Framework Core in Action",
            "Programming Entity Framework: DbContext"
        ], booksFromStore.Select(e => e.Title));
    }

    [ConditionalTheory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public virtual async Task VectorSearch_Memory_doubles(bool async, bool specifyIndex)
    {
        await using var context = Fixture.CreateContext();
        var inputVector = new[] { 0.33, -0.52 };

        var queryable = context.Set<Book>()
            .VectorSearch(e => e.MemoryDoubles, inputVector, limit: 4, CreateQueryOptions(specifyIndex ? "MemoryDoublesIndex" : null));

        var booksFromStore = async ? await queryable.ToListAsync() : queryable.ToList();

        Assert.Equal(4, booksFromStore.Count);
        Assert.Equal(
        [
            "Entity Framework Core in Action",
            "Programming Entity Framework",
            "Programming Entity Framework: Code First",
            "Programming Entity Framework: DbContext"
        ], booksFromStore.Select(e => e.Title));
    }

    [ConditionalTheory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public virtual async Task VectorSearch_ReadOnlyMemory_floats(bool async, bool specifyIndex)
    {
        await using var context = Fixture.CreateContext();
        var inputVector = new[] { 0.33f, -0.52f };

        var queryable = context.Set<Book>()
            .VectorSearch(e => e.ReadOnlyMemoryFloats, inputVector, limit: 4, CreateQueryOptions(specifyIndex ? "ReadOnlyMemoryFloatsIndex" : null));

        var booksFromStore = async ? await queryable.ToListAsync() : queryable.ToList();

        Assert.Equal(4, booksFromStore.Count);
        Assert.Equal(
        [
            "Programming Entity Framework: Code First",
            "Programming Entity Framework",
            "Entity Framework Core in Action",
            "Programming Entity Framework: DbContext"
        ], booksFromStore.Select(e => e.Title));
    }

    [ConditionalTheory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public virtual async Task VectorSearch_ReadOnlyMemory_doubles(bool async, bool specifyIndex)
    {
        await using var context = Fixture.CreateContext();
        var inputVector = new[] { 0.33, -0.52 };

        var queryable = context.Set<Book>()
            .VectorSearch(e => e.ReadOnlyMemoryDoubles, inputVector, limit: 4, CreateQueryOptions(specifyIndex ? "ReadOnlyMemoryDoublesIndex" : null));

        var booksFromStore = async ? await queryable.ToListAsync() : queryable.ToList();

        Assert.Equal(4, booksFromStore.Count);
        Assert.Equal(
        [
            "Entity Framework Core in Action",
            "Programming Entity Framework",
            "Programming Entity Framework: Code First",
            "Programming Entity Framework: DbContext"
        ], booksFromStore.Select(e => e.Title));
    }

    [ConditionalTheory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public virtual async Task VectorSearch_binary_floats(bool async, bool specifyIndex)
    {
        await using var context = Fixture.CreateContext();
        var inputVector = new BinaryVectorFloat32(new[] { 0.33f, -0.52f });

        var queryable = context.Set<Book>()
            .VectorSearch(e => e.BinaryFloats, inputVector, limit: 4, CreateQueryOptions(specifyIndex ? "BinaryFloatsIndex" : null));

        var booksFromStore = async ? await queryable.ToListAsync() : queryable.ToList();

        Assert.Equal(4, booksFromStore.Count);
        Assert.Equal(
        [
            "Programming Entity Framework",
            "Programming Entity Framework: Code First",
            "Programming Entity Framework: DbContext",
            "Entity Framework Core in Action"
        ], booksFromStore.Select(e => e.Title));
    }

    [ConditionalTheory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public virtual async Task VectorSearch_binary_bytes(bool async, bool specifyIndex)
    {
        await using var context = Fixture.CreateContext();
        var inputVector = new BinaryVectorPackedBit(new byte[] { 0x3A, 0x17 }, 0);

        var queryable = context.Set<Book>()
            .VectorSearch(e => e.BinaryBytes, inputVector, limit: 4, CreateQueryOptions(specifyIndex ? "BinaryBytesIndex" : null));

        var booksFromStore = async ? await queryable.ToListAsync() : queryable.ToList();

        Assert.Equal(4, booksFromStore.Count);
        Assert.Equal(
        [
            "Programming Entity Framework: Code First",
            "Programming Entity Framework",
            "Entity Framework Core in Action",
            "Programming Entity Framework: DbContext"
        ], booksFromStore.Select(e => e.Title));
    }

    [ConditionalTheory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public virtual async Task VectorSearch_binary_sbytes(bool async, bool specifyIndex)
    {
        await using var context = Fixture.CreateContext();
        var inputVector = new BinaryVectorInt8(new sbyte[] { 1, -2 });

        var queryable = context.Set<Book>()
            .VectorSearch(e => e.BinarySBytes, inputVector, limit: 4, CreateQueryOptions(specifyIndex ? "BinarySBytesIndex" : null));

        var booksFromStore = async ? await queryable.ToListAsync() : queryable.ToList();

        Assert.Equal(4, booksFromStore.Count);
        Assert.Equal(
        [
            "Programming Entity Framework",
            "Programming Entity Framework: Code First",
            "Programming Entity Framework: DbContext",
            "Entity Framework Core in Action"
        ], booksFromStore.Select(e => e.Title));
    }

    [ConditionalTheory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public virtual async Task VectorSearch_binary_floats_as_Memory(bool async, bool specifyIndex)
    {
        await using var context = Fixture.CreateContext();
        var inputVector = new BinaryVectorFloat32(new[] { 0.33f, -0.52f });

        var queryable = context.Set<Book>()
            .VectorSearch(e => e.BinaryMemoryFloats, inputVector, limit: 4, CreateQueryOptions(specifyIndex ? "BinaryMemoryFloatsIndex" : null));

        var booksFromStore = async ? await queryable.ToListAsync() : queryable.ToList();

        Assert.Equal(4, booksFromStore.Count);
        Assert.Equal(
        [
            "Programming Entity Framework",
            "Programming Entity Framework: Code First",
            "Programming Entity Framework: DbContext",
            "Entity Framework Core in Action"
        ], booksFromStore.Select(e => e.Title));
    }

    [ConditionalTheory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public virtual async Task VectorSearch_binary_bytes_as_Memory(bool async, bool specifyIndex)
    {
        await using var context = Fixture.CreateContext();
        var inputVector = new BinaryVectorPackedBit(new byte[] { 0x3A, 0x17 }, 0);

        var queryable = context.Set<Book>()
            .VectorSearch(e => e.BinaryMemoryBytes, inputVector, limit: 4, CreateQueryOptions(specifyIndex ? "BinaryMemoryBytesIndex" : null));

        var booksFromStore = async ? await queryable.ToListAsync() : queryable.ToList();

        Assert.Equal(4, booksFromStore.Count);
        Assert.Equal(
        [
            "Programming Entity Framework: Code First",
            "Programming Entity Framework",
            "Entity Framework Core in Action",
            "Programming Entity Framework: DbContext"
        ], booksFromStore.Select(e => e.Title));
    }

    [ConditionalTheory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public virtual async Task VectorSearch_binary_sbytes_as_Memory(bool async, bool specifyIndex)
    {
        await using var context = Fixture.CreateContext();
        var inputVector = new BinaryVectorInt8(new sbyte[] { 1, -2 });

        var queryable = context.Set<Book>()
            .VectorSearch(e => e.BinaryMemorySBytes, inputVector, limit: 4, CreateQueryOptions(specifyIndex ? "BinaryMemorySBytesIndex" : null));

        var booksFromStore = async ? await queryable.ToListAsync() : queryable.ToList();

        Assert.Equal(4, booksFromStore.Count);
        Assert.Equal(
        [
            "Programming Entity Framework",
            "Programming Entity Framework: Code First",
            "Programming Entity Framework: DbContext",
            "Entity Framework Core in Action"
        ], booksFromStore.Select(e => e.Title));
    }

    [ConditionalTheory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public virtual async Task VectorSearch_binary_floats_as_ReadOnlyMemory(bool async, bool specifyIndex)
    {
        await using var context = Fixture.CreateContext();
        var inputVector = new BinaryVectorFloat32(new[] { 0.33f, -0.52f });

        var queryable = context.Set<Book>()
            .VectorSearch(e => e.BinaryReadOnlyMemoryFloats, inputVector, limit: 4, CreateQueryOptions(specifyIndex ? "BinaryReadOnlyMemoryFloatsIndex" : null));

        var booksFromStore = async ? await queryable.ToListAsync() : queryable.ToList();

        Assert.Equal(4, booksFromStore.Count);
        Assert.Equal(
        [
            "Programming Entity Framework",
            "Programming Entity Framework: Code First",
            "Programming Entity Framework: DbContext",
            "Entity Framework Core in Action"
        ], booksFromStore.Select(e => e.Title));
    }

    [ConditionalTheory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public virtual async Task VectorSearch_binary_bytes_as_ReadOnlyMemory(bool async, bool specifyIndex)
    {
        await using var context = Fixture.CreateContext();
        var inputVector = new BinaryVectorPackedBit(new byte[] { 0x3A, 0x17 }, 0);

        var queryable = context.Set<Book>()
            .VectorSearch(e => e.BinaryReadOnlyMemoryBytes, inputVector, limit: 4, CreateQueryOptions(specifyIndex ? "BinaryReadOnlyMemoryBytesIndex" : null));

        var booksFromStore = async ? await queryable.ToListAsync() : queryable.ToList();

        Assert.Equal(4, booksFromStore.Count);
        Assert.Equal(
        [
            "Programming Entity Framework: Code First",
            "Programming Entity Framework",
            "Entity Framework Core in Action",
            "Programming Entity Framework: DbContext"
        ], booksFromStore.Select(e => e.Title));
    }

    [ConditionalTheory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public virtual async Task VectorSearch_binary_sbytes_as_ReadOnlyMemory(bool async, bool specifyIndex)
    {
        await using var context = Fixture.CreateContext();
        var inputVector = new BinaryVectorInt8(new sbyte[] { 1, -2 });

        var queryable = context.Set<Book>()
            .VectorSearch(e => e.BinaryReadOnlyMemorySBytes, inputVector, limit: 4, CreateQueryOptions(specifyIndex ? "BinaryReadOnlyMemorySBytesIndex" : null));

        var booksFromStore = async ? await queryable.ToListAsync() : queryable.ToList();

        Assert.Equal(4, booksFromStore.Count);
        Assert.Equal(
        [
            "Programming Entity Framework",
            "Programming Entity Framework: Code First",
            "Programming Entity Framework: DbContext",
            "Entity Framework Core in Action"
        ], booksFromStore.Select(e => e.Title));
    }

    [ConditionalTheory]
    [InlineData(false)]
    [InlineData(true)]
    public virtual async Task VectorSearch_floats_before_where(bool async)
    {
        await using var context = Fixture.CreateContext();
        var inputVector = new[] { 0.33f, -0.52f };

        var queryable = context.Set<Book>()
            .VectorSearch(e => e.Floats, inputVector, limit: 4, CreateQueryOptions("FloatsIndex"))
            .Where(e => e.IsPublished);

        var booksFromStore = async ? await queryable.ToListAsync() : queryable.ToList();

        Assert.Equal(2, booksFromStore.Count);
        Assert.Equal(
        [
            "Programming Entity Framework",
            "Entity Framework Core in Action",
        ], booksFromStore.Select(e => e.Title));
    }

    [ConditionalTheory]
    [InlineData(false)]
    [InlineData(true)]
    public virtual async Task VectorSearch_with_projection(bool async)
    {
        await using var context = Fixture.CreateContext();
        var inputVector = new[] { 0.33f, -0.52f };

        var queryable = context.Set<Book>()
            .VectorSearch(e => e.Floats, inputVector, limit: 4, CreateQueryOptions("FloatsIndex"))
            .Where(e => e.Title.Contains("Action") || e.Title.Contains("DbContext"))
            .Select(e => e.Author);

        var authors = async ? await queryable.ToListAsync() : queryable.ToList();

        Assert.Equal(2, authors.Count);
        Assert.Equal(
        [
            "Jon P Smith",
            "Julie Lerman",
        ], authors);
    }

    [ConditionalTheory]
    [InlineData(false)]
    [InlineData(true)]
    public virtual async Task VectorSearch_with_projection_of_score(bool async)
    {
        await using var context = Fixture.CreateContext();
        var inputVector = new[] { 0.33f, -0.52f };

        var queryable = context.Set<Book>()
            .VectorSearch(e => e.Floats, inputVector, limit: 4, CreateQueryOptions("FloatsIndex"))
            .Where(e => e.Title.Contains("Action") || e.Title.Contains("DbContext"))
            .Select(e => new { e.Author, Score = Mql.Field<Book, double>(e, "__score", null) });

        var results = async ? await queryable.ToListAsync() : queryable.ToList();

        Assert.Equal(2, results.Count);
        Assert.Equal("Jon P Smith", results[0].Author);
        Assert.Equal("Julie Lerman", results[1].Author);
        Assert.Equal(0.99974566698074341, results[0].Score, 5);
        Assert.Equal(0.99961328506469727, results[1].Score, 5);
    }

    [ConditionalTheory]
    [InlineData(false)]
    [InlineData(true)]
    public virtual async Task VectorSearch_with_projection_of_score_using_EF_Property(bool async)
    {
        await using var context = Fixture.CreateContext();
        var inputVector = new[] { 0.33f, -0.52f };

        var queryable = context.Set<Book>()
            .VectorSearch(e => e.Floats, inputVector, limit: 4, CreateQueryOptions("FloatsIndex"))
            .Where(e => e.Title.Contains("Action") || e.Title.Contains("DbContext"))
            .Select(e => new { e.Author, Score = EF.Property<double>(e, "__score") });

        var results = async ? await queryable.ToListAsync() : queryable.ToList();

        Assert.Equal(2, results.Count);
        Assert.Equal("Jon P Smith", results[0].Author);
        Assert.Equal("Julie Lerman", results[1].Author);
        Assert.Equal(0.99974566698074341, results[0].Score, 5);
        Assert.Equal(0.99961328506469727, results[1].Score, 5);
    }

    [ConditionalTheory]
    [InlineData(false)]
    [InlineData(true)]
    public virtual async Task VectorSearch_with_projection_of_entity_and_score(bool async)
    {
        await using var context = Fixture.CreateContext();
        var inputVector = new[] { 0.33f, -0.52f };

        var queryable = context.Set<Book>()
            .VectorSearch(e => e.Floats, inputVector, limit: 4, CreateQueryOptions("FloatsIndex"))
            .Where(e => e.Title.Contains("Action") || e.Title.Contains("DbContext"))
            .Select(e => new { Book = e, Score = Mql.Field<Book, double>(e, "__score", null) });

        // Fails: Projections issue EF-76
        Assert.Contains(
            "An error occurred while deserializing the Book ",
            (await Assert.ThrowsAsync<FormatException>(async () =>
                _ = async ? await queryable.ToListAsync() : queryable.ToList()))
            .Message);
    }

    [ConditionalTheory]
    [InlineData(false)]
    [InlineData(true)]
    public virtual async Task VectorSearch_with_projection_of_constructed_entity_and_score(bool async)
    {
        await using var context = Fixture.CreateContext();
        var inputVector = new[] { 0.33f, -0.52f };

        var queryable = context.Set<Book>()
            .VectorSearch(e => e.Floats, inputVector, limit: 4, CreateQueryOptions("FloatsIndex"))
            .Where(e => e.Title.Contains("Action") || e.Title.Contains("DbContext"))
            .Select(e => new
            {
                Book = new Book
                {
                    Id = e.Id,
                    Title = e.Title,
                    Author = e.Author,
                    Isbn = e.Isbn,
                    Comments = e.Comments,
                    Pages = e.Pages,
                    IsPublished = e.IsPublished
                },
                Score = Mql.Field<Book, double>(e, "__score", null)
            });

        var results = async ? await queryable.ToListAsync() : queryable.ToList();
        Assert.Equal(2, results.Count);

        Assert.Equal("Entity Framework Core in Action", results[0].Book.Title);
        Assert.Equal("Jon P Smith", results[0].Book.Author);
        Assert.Equal([1, 1, 1, 1], results[0].Book.Isbn);
        Assert.Equal(["Fab", "Froody"], results[0].Book.Comments);
        Assert.Equal(500, results[0].Book.Pages);

        Assert.Equal("Programming Entity Framework: DbContext", results[1].Book.Title);
        Assert.Equal("Julie Lerman", results[1].Book.Author);
        Assert.Equal([1, 1, 1, 2], results[1].Book.Isbn);
        Assert.Equal(["Fab", "Froody"], results[1].Book.Comments);
        Assert.Equal(600, results[1].Book.Pages);
        Assert.False(results[1].Book.IsPublished);
    }

    [ConditionalTheory]
    [InlineData(false)]
    [InlineData(true)]
    public virtual async Task VectorSearch_with_bool_pre_filter(bool async)
    {
        await using var context = Fixture.CreateContext();
        var inputVector = new[] { 0.33f, -0.52f };

        var queryable = context.Set<Book>()
            .VectorSearch(e => e.Floats, e => e.IsPublished, inputVector, limit: 4, CreateQueryOptions("FloatsIndex"));

        var booksFromStore = async ? await queryable.ToListAsync() : queryable.ToList();

        Assert.Equal(2, booksFromStore.Count);
        Assert.Equal(
        [
            "Programming Entity Framework",
            "Entity Framework Core in Action"
        ], booksFromStore.Select(e => e.Title));
    }

    [ConditionalTheory]
    [InlineData(false)]
    [InlineData(true)]
    public virtual async Task VectorSearch_with_complex_pre_filter(bool async)
    {
        await using var context = Fixture.CreateContext();
        var inputVector = new[] { 0.33f, -0.52f };

        var queryable = context.Set<Book>()
            .VectorSearch(
                e => e.Doubles,
                e => e.Comments.Contains("Froody") && (e.Pages > 500 || e.IsPublished),
                inputVector,
                limit: 4,
                CreateQueryOptions("DoublesIndex"));

        var booksFromStore = async ? await queryable.ToListAsync() : queryable.ToList();

        Assert.Equal(2, booksFromStore.Count);
        Assert.Equal(
        [
            "Entity Framework Core in Action",
            "Programming Entity Framework: DbContext"
        ], booksFromStore.Select(e => e.Title));
    }

    [ConditionalTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public virtual async Task VectorSearch_on_nested_reference(bool async, bool specifyIndex)
    {
        await using var context = Fixture.CreateContext();
        var inputVector = new[] { 0.33f, -0.52f };

        var queryable = specifyIndex
            ? context.Set<Book>().VectorSearch(e => e.Preface.Floats, inputVector, limit: 4,
                new() { IndexName = "FloatsVectorIndex" })
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
    }

    [ConditionalTheory]
    [InlineData(false)]
    [InlineData(true)]
    public virtual async Task VectorSearch_with_bool_pre_filter_on_nested_reference(bool async)
    {
        await using var context = Fixture.CreateContext();
        var inputVector = new[] { 0.33f, -0.52f };

        var queryable = context.Set<Book>()
            .VectorSearch(e => e.Preface.Floats, e => e.Preface.IsPublished, inputVector, limit: 4,
                CreateQueryOptions("FloatsVectorIndex"));

        var booksFromStore = async ? await queryable.ToListAsync() : queryable.ToList();

        Assert.Equal(2, booksFromStore.Count);
        Assert.Equal(
        [
            "Programming Entity Framework: DbContext",
            "Programming Entity Framework: Code First"
        ], booksFromStore.Select(e => e.Title));
    }

    [ConditionalTheory]
    [InlineData(false)]
    [InlineData(true)]
    public virtual async Task VectorSearch_with_complex_pre_filter_on_nested_reference(bool async)
    {
        await using var context = Fixture.CreateContext();
        var inputVector = new[] { 0.33f, -0.52f };

        var queryable = context.Set<Book>()
            .VectorSearch(
                e => e.Preface.Floats,
                e => e.Preface.Comments.Contains("Froody") && (e.Preface.Pages > 500 || e.Preface.IsPublished),
                inputVector, limit: 4,
                CreateQueryOptions("FloatsVectorIndex"));

        var booksFromStore = async ? await queryable.ToListAsync() : queryable.ToList();

        Assert.Equal(2, booksFromStore.Count);
        Assert.Equal(
        [
            "Programming Entity Framework",
            "Programming Entity Framework: Code First"
        ], booksFromStore.Select(e => e.Title));
    }

    [ConditionalTheory]
    [InlineData(false)]
    [InlineData(true)]
    public abstract Task VectorSearch_logs_for_zero_results(bool async);

    protected async Task VectorSearchZeroResults(bool async, string expectedQuery)
    {
        await using var context = Fixture.CreateContext();
        var inputVector = new[] { 0.33f, -0.52f };

        var queryable = context.Set<Book>()
            .VectorSearch(e => e.FloatsWithNoData, inputVector, limit: 4);

        // This throws by default because warnings as errors is on in the spec tests.
        Assert.Contains(
            $"An error was generated for warning 'Microsoft.EntityFrameworkCore.Query.VectorSearchReturnedZeroResults': The vector " +
            $"query '{expectedQuery}' " +
            "returned zero results. This could be because either there is no vector index defined for query property, or because " +
            "vector data (embeddings) have recently been inserted. Consider disabling index creation in " +
            "'DbContext.Database.EnsureCreated' and performing initial ingestion before calling " +
            "'DbContext.Database.CreateMissingVectorIndexes' and 'DbContext.Database.WaitForVectorIndexes'. " +
            "This exception can be suppressed or logged by passing event ID 'MongoEventId.VectorSearchReturnedZeroResults' " +
            "to the 'ConfigureWarnings' method in 'DbContext.OnConfiguring' or 'AddDbContext'.",
            (await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                _ = async ? await queryable.ToListAsync() : queryable.ToList();
            })).Message);
    }

    private VectorQueryOptions? CreateQueryOptions(string? indexName)
    {
        VectorQueryOptions? options = null;
        if (indexName != null)
        {
            options = new VectorQueryOptions { IndexName = indexName };
        }

        if (Fixture.Exact)
        {
            options = (options ?? new VectorQueryOptions()) with { Exact = true };
        }

        return options;
    }

    protected class Book
    {
        public ObjectId Id { get; set; }
        public string Title { get; set; } = null!;
        public string Author { get; set; } = null!;
        public byte[] Isbn { get; set; } = null!;
        public string[] Comments { get; set; } = [];
        public int Pages { get; set; }
        public bool IsPublished { get; set; }

        public float[] Floats { get; set; }
        public double[] Doubles { get; set; }

        public float[] FloatsWithNoData { get; set; }

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

        public Preface? Preface { get; set; }
    }

    protected class Preface
    {
        public string Author { get; set; } = null!;
        public float[]? Floats { get; set; }
        public double[]? Doubles { get; set; }
        public string[] Comments { get; set; } = [];
        public int Pages { get; set; }
        public bool IsPublished { get; set; }
    }

    protected void AssertMql(params string[] expected)
        => Fixture.TestMqlLoggerFactory.AssertBaseline(expected);

    public abstract class VectorSearchFixtureBase : SharedStoreFixtureBase<PoolableDbContext>
    {
        public abstract bool Exact { get; }

        protected override void OnModelCreating(ModelBuilder modelBuilder, DbContext context)
            => modelBuilder.Entity<Book>(b =>
            {
                b.ToCollection("Books");
                b.OwnsOne(x => x.Preface, b =>
                {
                    b.HasIndex(e => e.Floats).IsVectorIndex(VectorSimilarity.Cosine, 2, b =>
                    {
                        b.AllowsFiltersOn(e => e.Comments);
                        b.AllowsFiltersOn(e => e.Pages);
                        b.AllowsFiltersOn(e => e.IsPublished);
                    });
                });

                b.HasIndex(e => e.Floats, "FloatsIndex").IsVectorIndex(VectorSimilarity.Cosine, 2)
                    .AllowsFiltersOn(e => e.IsPublished);

                b.HasIndex(e => e.FloatsWithNoData, "FloatsWithNoDataIndex").IsVectorIndex(VectorSimilarity.Cosine, 2);

                b.HasIndex(e => e.Doubles, "DoublesIndex").IsVectorIndex(VectorSimilarity.Euclidean, 2, b =>
                {
                    b.AllowsFiltersOn(e => e.Comments);
                    b.AllowsFiltersOn(e => e.Pages);
                    b.AllowsFiltersOn(e => e.IsPublished);
                });

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
                b.HasIndex(e => e.BinaryReadOnlyMemoryFloats, "BinaryReadOnlyMemoryFloatsIndex")
                    .IsVectorIndex(VectorSimilarity.DotProduct, 2);
                b.HasIndex(e => e.BinaryReadOnlyMemoryBytes, "BinaryReadOnlyMemoryBytesIndex")
                    .IsVectorIndex(VectorSimilarity.Euclidean, 16);
                b.HasIndex(e => e.BinaryReadOnlyMemorySBytes, "BinaryReadOnlyMemorySBytesIndex")
                    .IsVectorIndex(VectorSimilarity.Cosine, 2);
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
                Comments = ["Fab", "Froody"],
                Pages = 500,
                IsPublished = true,
                Floats = [0.332f, -0.562f],
                Doubles = [0.332, -0.532],
                FloatsWithNoData = [],
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
                Preface = new Preface
                {
                    Author = "Judge Dread",
                    Floats = [0.363f, -0.562f],
                    Comments = ["Fab"],
                    Pages = 800,
                },
            };

            var book2 = new Book
            {
                Author = "Julie Lerman",
                Title = "Programming Entity Framework: DbContext",
                Isbn = [1, 1, 1, 2],
                Comments = ["Fab", "Froody"],
                Pages = 600,
                Floats = [0.338f, -0.582f],
                Doubles = [0.833, -0.582],
                FloatsWithNoData = [],
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
                Preface = new Preface
                {
                    Author = "Diego Vega",
                    Floats = [0.533f, -0.526f],
                    Doubles = [0.337, -0.523],
                    Comments = ["Fab"],
                    Pages = 700,
                    IsPublished = true,
                },
            };

            var book3 = new Book
            {
                Author = "Julie Lerman",
                Title = "Programming Entity Framework",
                Isbn = [1, 1, 1, 3],
                Comments = ["Fab"],
                Pages = 700,
                IsPublished = true,
                Floats = [0.334f, -0.542f],
                FloatsWithNoData = [],
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
                Preface = new Preface
                {
                    Author = "Danny Simmons",
                    Floats = [0.333f, -0.521f],
                    Doubles = [0.333, -0.522],
                    Comments = ["Fab", "Froody"],
                    Pages = 600,
                },
            };

            var book4 = new Book
            {
                Author = "Julie Lerman",
                Title = "Programming Entity Framework: Code First",
                Isbn = [1, 1, 1, 3],
                Comments = ["Fab"],
                Pages = 800,
                Floats = [0.333f, -0.526f],
                FloatsWithNoData = [],
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
                Preface = new Preface
                {
                    Author = "Ford Prefect",
                    Floats = [0.733f, -0.527f],
                    Doubles = [0.337, -0.752],
                    Comments = ["Fab", "Froody"],
                    Pages = 500,
                    IsPublished = true,
                },
            };

            context.AddRange(book1, book2, book3, book4);

            return context;
        }

        private ITestStoreFactory? _testStoreFactory;

        protected override ITestStoreFactory TestStoreFactory
            => _testStoreFactory!;

        public override async Task InitializeAsync()
        {
            var server = await TestServer.GetOrInitializeTestServerAsync(MongoCondition.IsAtlas);
            _testStoreFactory = new MongoTestStoreFactory(server);

            await base.InitializeAsync();
        }

        public TestMqlLoggerFactory TestMqlLoggerFactory
            => (TestMqlLoggerFactory)ListLoggerFactory;
    }
}
