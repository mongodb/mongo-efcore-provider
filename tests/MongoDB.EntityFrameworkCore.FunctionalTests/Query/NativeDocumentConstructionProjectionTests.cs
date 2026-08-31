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

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.Diagnostics;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Infrastructure;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// EF-447: a CONSTRUCTED (non-navigation) sub-entity leaf in a projection —
/// <c>Select(b => new { Copy = new BookCopy { Id = b.Id, Title = b.Title }, b.Rank })</c> — mixed with a
/// sibling leaf, goes native on all four legs (NativeOnly, default Native, explicit DriverLinq, and the
/// late-fallback leg). Mirrors <c>NativeOwnedReferenceWholeEntityTests</c>' EF-441 coverage pattern.
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeDocumentConstructionProjectionTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    private static SingleEntityDbContext<T> CreateContext<T>(
        IMongoCollection<T> collection, MongoQueryMode mode, Action<ModelBuilder>? modelBuilderAction = null)
        where T : class
        => SingleEntityDbContext.Create(
            collection,
            modelBuilderAction: modelBuilderAction,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });

    private static SingleEntityDbContext<T> CreateContextWithLogging<T>(
        IMongoCollection<T> collection, MongoQueryMode mode, Action<ModelBuilder>? modelBuilderAction,
        out SpyLoggerProvider spyLogger)
        where T : class
    {
        var (loggerFactory, provider) = SpyLoggerProvider.Create();
        spyLogger = provider;

        return SingleEntityDbContext.Create(
            collection,
            loggerFactory,
            modelBuilderAction: modelBuilderAction,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                b.EnableSensitiveDataLogging();
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });
    }

    private static void AssertMql(SpyLoggerProvider spyLogger, string expected)
        => Assert.Contains(expected, spyLogger.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery));

    private string UniqueCollectionName(string name)
        => TemporaryDatabaseFixtureBase.CreateCollectionName(name) + Guid.NewGuid().ToString("N")[..8];

    private class Book
    {
        public ObjectId Id { get; set; }
        public string Title { get; set; } = "";
        public string Author { get; set; } = "";
        public int Rank { get; set; }
    }

    // A plain, unmapped DTO reconstructed from the root entity's own scalar fields — NOT a navigation, and not
    // itself part of the model. Distinguishes this leaf from EF-441's owned-nav-entity leaf, which aliases an
    // ALREADY-STORED owned sub-document.
    private class BookCopy
    {
        public ObjectId Id { get; set; }
        public string Title { get; set; } = "";
        public string Author { get; set; } = "";
    }

    private IMongoCollection<Book> SeedBooks(string name)
    {
        var coll = database.MongoDatabase.GetCollection<BsonDocument>(UniqueCollectionName(name));
        coll.InsertMany([
            new BsonDocument
            {
                { "_id", ObjectId.GenerateNewId() }, { "Title", "Alpha" }, { "Author", "Ada" }, { "Rank", 3 }
            },
            new BsonDocument
            {
                { "_id", ObjectId.GenerateNewId() }, { "Title", "Beta" }, { "Author", "Bea" }, { "Rank", 5 }
            },
        ]);
        return database.MongoDatabase.GetCollection<Book>(coll.CollectionNamespace.CollectionName);
    }

    // ════════════════════════════════════════════════════════════════════════════════════════════
    //  Goes native: a constructed sub-entity leaf mixed with a plain field sibling
    // ════════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Document_construction_leaf_goes_native_and_reads_correct_values()
    {
        var collection = SeedBooks(nameof(Document_construction_leaf_goes_native_and_reads_correct_values));
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly);

        // NativeOnly: success here (rather than NativeTranslationNotSupportedException) is the routing proof.
        var results = db.Entities.AsNoTracking()
            .Select(b => new { Copy = new BookCopy { Id = b.Id, Title = b.Title, Author = b.Author }, b.Rank })
            .OrderBy(r => r.Copy.Title)
            .ToList();

        Assert.Equal(2, results.Count);
        Assert.Equal("Alpha", results[0].Copy.Title);
        Assert.Equal("Ada", results[0].Copy.Author);
        Assert.Equal(3, results[0].Rank);
        Assert.Equal("Beta", results[1].Copy.Title);
        Assert.Equal("Bea", results[1].Copy.Author);
        Assert.Equal(5, results[1].Rank);
    }

    [Fact]
    public void Document_construction_leaf_emits_expected_nested_project_stage()
    {
        var collection = SeedBooks(nameof(Document_construction_leaf_emits_expected_nested_project_stage));
        using var db = CreateContextWithLogging(collection, MongoQueryMode.NativeOnly, null, out var spyLogger);

        _ = db.Entities.AsNoTracking()
            .Select(b => new { Copy = new BookCopy { Id = b.Id, Title = b.Title, Author = b.Author }, b.Rank })
            .ToList();

        AssertMql(
            spyLogger,
            "{ \"$project\" : { \"Copy\" : { \"Id\" : \"$_id\", \"Title\" : \"$Title\", \"Author\" : \"$Author\" }, "
            + "\"Rank\" : \"$Rank\", \"_id\" : 0 } }");
    }

    // Mixed with a COMPUTED sibling too — unlike EF-441's owned-nav-entity leaf, this leaf's own members are
    // independently readable off a whole document by their own natural paths, so a computed sibling's lack of
    // a document path is not a hazard here (see NativeProjectionBinder.TryGetDocumentConstructionLeaf remarks).
    [Fact]
    public void Document_construction_leaf_mixed_with_computed_sibling_goes_native()
    {
        var collection = SeedBooks(nameof(Document_construction_leaf_mixed_with_computed_sibling_goes_native));
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly);

        var results = db.Entities.AsNoTracking()
            .Select(b => new
            {
                Copy = new BookCopy { Id = b.Id, Title = b.Title, Author = b.Author },
                Total = b.Rank * b.Rank
            })
            .OrderBy(r => r.Copy.Title)
            .ToList();

        Assert.Equal(2, results.Count);
        Assert.Equal("Alpha", results[0].Copy.Title);
        Assert.Equal(9, results[0].Total);
        Assert.Equal("Beta", results[1].Copy.Title);
        Assert.Equal(25, results[1].Total);
    }

    [Fact]
    public void Document_construction_leaf_parity_between_native_and_driver_linq()
    {
        var collection = SeedBooks(nameof(Document_construction_leaf_parity_between_native_and_driver_linq));

        List<(string Title, string Author, int Rank)> driver;
        using (var db = CreateContext(collection, MongoQueryMode.DriverLinq))
        {
            driver = db.Entities.AsNoTracking()
                .Select(b => new { Copy = new BookCopy { Id = b.Id, Title = b.Title, Author = b.Author }, b.Rank })
                .OrderBy(r => r.Copy.Title)
                .ToList()
                .Select(r => (r.Copy.Title, r.Copy.Author, r.Rank)).ToList();
        }

        List<(string Title, string Author, int Rank)> native;
        using (var db = CreateContext(collection, MongoQueryMode.NativeOnly))
        {
            native = db.Entities.AsNoTracking()
                .Select(b => new { Copy = new BookCopy { Id = b.Id, Title = b.Title, Author = b.Author }, b.Rank })
                .OrderBy(r => r.Copy.Title)
                .ToList()
                .Select(r => (r.Copy.Title, r.Copy.Author, r.Rank)).ToList();
        }

        Assert.Equal(driver, native);
    }

    // The LATE-FALLBACK leg: a captured-local `StartsWith` is the trigger MongoQueryLanguageRenderer.RenderRegex
    // declines (MQL has no parameterized-regex form), so NativeOnly proves the query translates natively at
    // compile time (Route == Projection) but then genuinely DECLINES mid-compile (TryBuildNativeFactory) — the
    // exact leg that hands the shaper WHOLE, un-projected documents (see
    // MongoMixedProjectionBindingRemovingExpressionVisitor.ReadDocumentConstructionMember's override, which reads
    // each member at its own NATURAL root-relative path instead of the native $project's nested alias). Mirrors
    // NativeOwnedReferenceWholeEntityTests.Field_sibling_projection_behind_a_parameterized_where_reads_correct_values.
    [Fact]
    public void Document_construction_leaf_behind_a_parameterized_where_reads_correct_values()
    {
        var collection = SeedBooks(nameof(Document_construction_leaf_behind_a_parameterized_where_reads_correct_values));
        var titlePrefix = "A";

        // HALF THE DISCRIMINATOR: NativeOnly forbids the fallback, so this throw is the proof that
        // TryBuildNativeFactory declines MID-COMPILE for this exact query.
        using (var nativeOnly = CreateContext(collection, MongoQueryMode.NativeOnly))
        {
            var declined = nativeOnly.Entities.AsNoTracking()
                .Where(b => b.Title.StartsWith(titlePrefix))
                .Select(b => new { Copy = new BookCopy { Id = b.Id, Title = b.Title, Author = b.Author }, b.Rank });
            Assert.Throws<NativeTranslationNotSupportedException>(() => declined.ToList());
        }

        // The OTHER half: default Native mode must still read back CORRECT values on the late-fallback leg.
        using (var native = CreateContext(collection, MongoQueryMode.Native))
        {
            var results = native.Entities.AsNoTracking()
                .Where(b => b.Title.StartsWith(titlePrefix))
                .Select(b => new { Copy = new BookCopy { Id = b.Id, Title = b.Title, Author = b.Author }, b.Rank })
                .ToList();

            var row = Assert.Single(results);
            Assert.Equal("Alpha", row.Copy.Title);
            Assert.Equal("Ada", row.Copy.Author);
            Assert.Equal(3, row.Rank);
        }

        // And explicit DriverLinq — the OTHER leg that needs the same shape to read correctly.
        using (var driver = CreateContext(collection, MongoQueryMode.DriverLinq))
        {
            var results = driver.Entities.AsNoTracking()
                .Where(b => b.Title.StartsWith(titlePrefix))
                .Select(b => new { Copy = new BookCopy { Id = b.Id, Title = b.Title, Author = b.Author }, b.Rank })
                .ToList();

            var row = Assert.Single(results);
            Assert.Equal("Alpha", row.Copy.Title);
            Assert.Equal("Ada", row.Copy.Author);
            Assert.Equal(3, row.Rank);
        }
    }

    // A computed MEMBER inside the construction (not a plain root-relative scalar field) declines the WHOLE
    // leaf — a strict, minimal widening rather than a general nested-projection engine — so this shape must
    // keep behaving exactly as it did before this ticket: silent fallback under Native, and correct values in
    // every mode (there is a working driver-LINQ oracle for this shape).
    [Fact]
    public void Document_construction_leaf_with_computed_member_falls_back_but_still_reads_correct_values()
    {
        var collection = SeedBooks(nameof(Document_construction_leaf_with_computed_member_falls_back_but_still_reads_correct_values));

        using (var nativeOnly = CreateContext(collection, MongoQueryMode.NativeOnly))
        {
            Assert.Throws<NativeTranslationNotSupportedException>(() => nativeOnly.Entities.AsNoTracking()
                .Select(b => new { Copy = new BookCopy { Id = b.Id, Title = b.Title, Author = b.Title.Length.ToString() } })
                .ToList());
        }

        using var db = CreateContext(collection, MongoQueryMode.Native);
        var results = db.Entities.AsNoTracking()
            .Select(b => new { Copy = new BookCopy { Id = b.Id, Title = b.Title, Author = b.Title.Length.ToString() } })
            .OrderBy(r => r.Copy.Title)
            .ToList();

        Assert.Equal(2, results.Count);
        Assert.Equal("Alpha", results[0].Copy.Title);
        Assert.Equal("5", results[0].Copy.Author);
        Assert.Equal("Beta", results[1].Copy.Title);
        Assert.Equal("4", results[1].Copy.Author);
    }
}
