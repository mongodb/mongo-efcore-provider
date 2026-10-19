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
/// A constructed (non-navigation) sub-entity leaf in a projection —
/// <c>Select(b => new { Copy = new BookCopy { Id = b.Id, Title = b.Title }, b.Rank })</c> — mixed with a
/// sibling leaf, across NativeOnly, Native and DriverLinq. See also <c>NativeOwnedReferenceWholeEntityTests</c>.
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

    private string UniqueCollectionName(string name)
        => TemporaryDatabaseFixtureBase.CreateCollectionName(name) + Guid.NewGuid().ToString("N")[..8];

    private class Book
    {
        public ObjectId Id { get; set; }
        public string Title { get; set; } = "";
        public string Author { get; set; } = "";
        public int Rank { get; set; }
    }

    // Unmapped DTO built from the root's own scalar fields (unlike an owned-nav leaf, which aliases a stored
    // sub-document).
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

        spyLogger.AssertExecutedMqlContains(
            "{ \"$project\" : { \"Copy\" : { \"Id\" : \"$_id\", \"Title\" : \"$Title\", \"Author\" : \"$Author\" }, "
            + "\"Rank\" : \"$Rank\", \"_id\" : 0 } }");
    }

    // A computed sibling is safe here, unlike with an owned-nav leaf: this leaf's members are readable off a whole
    // document by their natural paths (see NativeProjectionBinder.TryGetDocumentConstructionLeaf).
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

    // The DriverLinq leg hands the shaper whole, un-projected documents, so each member must be read at its
    // natural root-relative path, not the $project alias (see
    // MongoMixedProjectionBindingRemovingExpressionVisitor.ReadDocumentConstructionMember).
    [Fact]
    public void Document_construction_leaf_behind_a_parameterized_where_reads_correct_values()
    {
        var collection = SeedBooks(nameof(Document_construction_leaf_behind_a_parameterized_where_reads_correct_values));
        var titlePrefix = "A"; // a captured local, not a constant — a genuine query parameter

        using (var nativeOnly = CreateContext(collection, MongoQueryMode.NativeOnly))
        {
            var nativeOnlyResults = nativeOnly.Entities.AsNoTracking()
                .Where(b => b.Title.StartsWith(titlePrefix))
                .Select(b => new { Copy = new BookCopy { Id = b.Id, Title = b.Title, Author = b.Author }, b.Rank })
                .ToList();

            var nativeOnlyRow = Assert.Single(nativeOnlyResults);
            Assert.Equal("Alpha", nativeOnlyRow.Copy.Title);
            Assert.Equal("Ada", nativeOnlyRow.Copy.Author);
            Assert.Equal(3, nativeOnlyRow.Rank);
        }

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

    // A computed member inside the construction declines the whole leaf: NativeOnly throws, Native silently
    // falls back and still reads correct values.
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

    // ════════════════════════════════════════════════════════════════════════════════════════════
    //  A ternary whose branches are client-only constructions: only the test is projected (a bool leaf) and the
    //  conditional is evaluated client-side. Server-side document construction would be wrong here: IdName.Id maps
    //  to "_id" in the driver's class map, while a native sub-document would carry "Id".
    // ════════════════════════════════════════════════════════════════════════════════════════════

    private class Place
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
        public string? City { get; set; }
        public string? Country { get; set; }
    }

    private class IdName
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
    }

    // City is a match, a non-match, BSON null and missing: the last two must take the false branch (C# semantics of
    // `null == "Seattle"`).
    private IMongoCollection<Place> SeedPlaces(string name)
    {
        var coll = database.MongoDatabase.GetCollection<BsonDocument>(UniqueCollectionName(name));
        coll.InsertMany([
            new BsonDocument
            {
                { "_id", ObjectId.GenerateNewId() }, { "Name", "Alpha" }, { "City", "Seattle" }, { "Country", "USA" }
            },
            new BsonDocument
            {
                { "_id", ObjectId.GenerateNewId() }, { "Name", "Beta" }, { "City", "London" }, { "Country", "UK" }
            },
            new BsonDocument
            {
                { "_id", ObjectId.GenerateNewId() }, { "Name", "Gamma" }, { "City", BsonNull.Value }, { "Country", "FR" }
            },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Name", "Delta" }, { "Country", "DE" } },
        ]);
        return database.MongoDatabase.GetCollection<Place>(coll.CollectionNamespace.CollectionName);
    }

    private static string Show(IdName value)
        => $"{value.Id}|{value.Name}";

    [Fact]
    public void Bare_ternary_over_constant_constructions_goes_native_with_parity()
    {
        var collection = SeedPlaces(nameof(Bare_ternary_over_constant_constructions_goes_native_with_parity));

        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateContext(collection, mode);
            return db.Entities.AsNoTracking()
                .OrderBy(p => p.Name)
                .Select(p => p.City == "Seattle"
                    ? new IdName { Id = "PAY", Name = "Pay" }
                    : new IdName { Id = "REC", Name = "Receive" })
                .AsEnumerable()
                .Select(Show)
                .ToList();
        });

        // Alpha, Beta, Delta (missing City), Gamma (null City).
        Assert.Equal(["PAY|Pay", "REC|Receive", "REC|Receive", "REC|Receive"], results);
    }

    [Fact]
    public void Bare_ternary_over_constant_constructions_projects_only_the_test()
    {
        var collection = SeedPlaces(nameof(Bare_ternary_over_constant_constructions_projects_only_the_test));
        using var db = CreateContextWithLogging(collection, MongoQueryMode.NativeOnly, null, out var spyLogger);

        _ = db.Entities.AsNoTracking()
            .Select(p => p.City == "Seattle"
                ? new IdName { Id = "PAY", Name = "Pay" }
                : new IdName { Id = "REC", Name = "Receive" })
            .ToList();

        spyLogger.AssertExecutedMqlContains(
            "{ \"$project\" : { \"_v\" : { \"$eq\" : [\"$City\", { \"$literal\" : \"Seattle\" }] }, \"_id\" : 0 } }");
    }

    [Fact]
    public void Bare_ternary_with_a_branch_reading_a_row_value_goes_native_with_parity()
    {
        var collection = SeedPlaces(nameof(Bare_ternary_with_a_branch_reading_a_row_value_goes_native_with_parity));

        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateContext(collection, mode);
            return db.Entities.AsNoTracking()
                .OrderBy(p => p.Name)
                .Select(p => p.City == "Seattle"
                    ? new IdName { Id = "PAY", Name = p.Name }
                    : new IdName { Id = "REC", Name = "Receive" })
                .AsEnumerable()
                .Select(Show)
                .ToList();
        });

        Assert.Equal(["PAY|Alpha", "REC|Receive", "REC|Receive", "REC|Receive"], results);
    }

    [Fact]
    public void Bare_ternary_with_both_branches_reading_row_values_goes_native_with_parity()
    {
        var collection = SeedPlaces(nameof(Bare_ternary_with_both_branches_reading_row_values_goes_native_with_parity));

        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateContext(collection, mode);
            return db.Entities.AsNoTracking()
                .OrderBy(p => p.Name)
                .Select(p => p.City == "Seattle"
                    ? new IdName { Id = p.Country!, Name = p.Name }
                    : new IdName { Id = "REC", Name = p.Name })
                .AsEnumerable()
                .Select(Show)
                .ToList();
        });

        Assert.Equal(["USA|Alpha", "REC|Beta", "REC|Delta", "REC|Gamma"], results);
    }

    // One field read under two members (across branches, or within one branch): staged once and shared by both.
    [Fact]
    public void Bare_ternary_reading_one_field_under_two_members_goes_native_with_parity()
    {
        var collection = SeedPlaces(nameof(Bare_ternary_reading_one_field_under_two_members_goes_native_with_parity));

        var acrossBranches = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateContext(collection, mode);
            return db.Entities.AsNoTracking()
                .OrderBy(p => p.Name)
                .Select(p => p.City == "Seattle"
                    ? new IdName { Id = "PAY", Name = p.Name }
                    : new IdName { Id = p.Name, Name = "Rec" })
                .AsEnumerable()
                .Select(Show)
                .ToList();
        });
        Assert.Equal(["PAY|Alpha", "Beta|Rec", "Delta|Rec", "Gamma|Rec"], acrossBranches);

        var withinBranch = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateContext(collection, mode);
            return db.Entities.AsNoTracking()
                .OrderBy(p => p.Name)
                .Select(p => p.City == "Seattle"
                    ? new IdName { Id = p.Name, Name = p.Name }
                    : new IdName { Id = "REC", Name = "Rec" })
                .AsEnumerable()
                .Select(Show)
                .ToList();
        });
        Assert.Equal(["Alpha|Alpha", "REC|Rec", "REC|Rec", "REC|Rec"], withinBranch);

        // The shared read is staged once.
        using var logged = CreateContextWithLogging(collection, MongoQueryMode.NativeOnly, null, out var spyLogger);
        _ = logged.Entities.AsNoTracking()
            .Select(p => p.City == "Seattle"
                ? new IdName { Id = p.Name, Name = p.Name }
                : new IdName { Id = "REC", Name = "Rec" })
            .ToList();
        spyLogger.AssertExecutedMqlContains(
            "{ \"$project\" : { \"_v\" : { \"$eq\" : [\"$City\", { \"$literal\" : \"Seattle\" }] }, \"_cr0\" : \"$Name\", \"_id\" : 0 } }");
    }

    // A captured value (a query parameter) in a branch is evaluated client-side with this execution's value, and a
    // captured value in the test is rendered server-side.
    [Fact]
    public void Bare_ternary_with_captured_values_goes_native_with_parity()
    {
        var collection = SeedPlaces(nameof(Bare_ternary_with_captured_values_goes_native_with_parity));

        List<string> Run(string city, string label)
            => NativeModeAssert.NativeAndParity(mode =>
            {
                using var db = CreateContext(collection, mode);
                return db.Entities.AsNoTracking()
                    .OrderBy(p => p.Name)
                    .Select(p => p.City == city
                        ? new IdName { Id = "PAY", Name = label }
                        : new IdName { Id = "REC", Name = "Receive" })
                    .AsEnumerable()
                    .Select(Show)
                    .ToList();
            });

        Assert.Equal(["PAY|Pay", "REC|Receive", "REC|Receive", "REC|Receive"], Run("Seattle", "Pay"));
        Assert.Equal(["REC|Receive", "PAY|Paid", "REC|Receive", "REC|Receive"], Run("London", "Paid"));
    }

    // Both branches assign Name from different fields: bound under the same projection member, one would read as the
    // other. Must decline rather than answer with the wrong field.
    [Fact]
    public void Bare_ternary_whose_branches_read_different_fields_into_one_member_declines()
    {
        var collection = SeedPlaces(nameof(Bare_ternary_whose_branches_read_different_fields_into_one_member_declines));

        var results = NativeModeAssert.DeclinesCleanly(mode =>
        {
            using var db = CreateContext(collection, mode);
            return db.Entities.AsNoTracking()
                .OrderBy(p => p.Name)
                .Select(p => p.City == "Seattle"
                    ? new IdName { Id = "PAY", Name = p.Name }
                    : new IdName { Id = "REC", Name = p.Country! })
                .AsEnumerable()
                .Select(Show)
                .ToList();
        });

        Assert.Equal(["PAY|Alpha", "REC|UK", "REC|DE", "REC|FR"], results);
    }

    // The latent crash: the wrapped form was admitted as a server-side $cond over sub-documents carrying "Id", and the
    // read back through IdName's class map (which expects "_id") threw FormatException.
    [Fact]
    public void Wrapped_ternary_over_constant_constructions_goes_native_with_parity()
    {
        var collection = SeedPlaces(nameof(Wrapped_ternary_over_constant_constructions_goes_native_with_parity));

        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateContext(collection, mode);
            return db.Entities.AsNoTracking()
                .OrderBy(p => p.Name)
                .Select(p => new
                {
                    p.Name,
                    X = p.City == "Seattle"
                        ? new IdName { Id = "PAY", Name = "Pay" }
                        : new IdName { Id = "REC", Name = "Receive" }
                })
                .AsEnumerable()
                .Select(r => $"{r.Name}:{Show(r.X)}")
                .ToList();
        });

        Assert.Equal(["Alpha:PAY|Pay", "Beta:REC|Receive", "Delta:REC|Receive", "Gamma:REC|Receive"], results);
    }

    [Fact]
    public void Wrapped_ternary_with_a_branch_reading_a_row_value_goes_native_with_parity()
    {
        var collection = SeedPlaces(nameof(Wrapped_ternary_with_a_branch_reading_a_row_value_goes_native_with_parity));

        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateContext(collection, mode);
            return db.Entities.AsNoTracking()
                .OrderBy(p => p.Name)
                .Select(p => new
                {
                    p.Country,
                    X = p.City == "Seattle"
                        ? new IdName { Id = "PAY", Name = p.Name }
                        : new IdName { Id = "REC", Name = "Receive" }
                })
                .AsEnumerable()
                .Select(r => $"{r.Country}:{Show(r.X)}")
                .ToList();
        });

        Assert.Equal(["USA:PAY|Alpha", "UK:REC|Receive", "DE:REC|Receive", "FR:REC|Receive"], results);
    }

    // A branch member named like a sibling member (`Name`) would read the sibling's alias; declines.
    [Fact]
    public void Wrapped_ternary_whose_branch_member_collides_with_a_sibling_alias_declines()
    {
        var collection = SeedPlaces(nameof(Wrapped_ternary_whose_branch_member_collides_with_a_sibling_alias_declines));

        var results = NativeModeAssert.DeclinesCleanly(mode =>
        {
            using var db = CreateContext(collection, mode);
            return db.Entities.AsNoTracking()
                .OrderBy(p => p.Name)
                .Select(p => new
                {
                    Name = p.Country,
                    X = p.City == "Seattle"
                        ? new IdName { Id = "PAY", Name = p.Name }
                        : new IdName { Id = "REC", Name = "Receive" }
                })
                .AsEnumerable()
                .Select(r => $"{r.Name}:{Show(r.X)}")
                .ToList();
        });

        Assert.Equal(["USA:PAY|Alpha", "UK:REC|Receive", "DE:REC|Receive", "FR:REC|Receive"], results);
    }

    // A whole-entity sibling sends explicit DriverLinq to the mixed shaper over whole documents, which reads this
    // shaper too: the test re-evaluated client-side, the branch member read at its natural path.
    [Fact]
    public void Wrapped_ternary_beside_the_whole_entity_goes_native_with_parity()
    {
        var collection = SeedPlaces(nameof(Wrapped_ternary_beside_the_whole_entity_goes_native_with_parity));

        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateContext(collection, mode);
            return db.Entities.AsNoTracking()
                .OrderBy(p => p.Name)
                .Select(p => new
                {
                    P = p,
                    X = p.City == "Seattle"
                        ? new IdName { Id = "PAY", Name = p.Name }
                        : new IdName { Id = "REC", Name = "Receive" }
                })
                .AsEnumerable()
                .Select(r => $"{r.P.Name}:{Show(r.X)}")
                .ToList();
        });

        Assert.Equal(["Alpha:PAY|Alpha", "Beta:REC|Receive", "Delta:REC|Receive", "Gamma:REC|Receive"], results);
    }

    private class Label
    {
        public string Text { get; set; } = "";
    }

    // A construction whose class map reads every member from the element of its own name is read back whole
    // correctly, so the ternary stays a server-side $cond over sub-documents (only the Id-to-_id kind is moved to
    // the client).
    [Fact]
    public void Wrapped_ternary_over_constructions_the_class_map_reads_by_name_stays_a_server_side_cond()
    {
        var collection = SeedPlaces(nameof(Wrapped_ternary_over_constructions_the_class_map_reads_by_name_stays_a_server_side_cond));

        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateContext(collection, mode);
            return db.Entities.AsNoTracking()
                .OrderBy(p => p.Name)
                .Select(p => new
                {
                    p.Country,
                    X = p.City == "Seattle" ? new Label { Text = p.Name } : new Label { Text = p.Country! }
                })
                .AsEnumerable()
                .Select(r => $"{r.Country}:{r.X.Text}")
                .ToList();
        });

        Assert.Equal(["USA:Alpha", "UK:UK", "DE:DE", "FR:FR"], results);

        using var logged = CreateContextWithLogging(collection, MongoQueryMode.NativeOnly, null, out var spyLogger);
        _ = logged.Entities.AsNoTracking()
            .Select(p => new
            {
                p.Country,
                X = p.City == "Seattle" ? new Label { Text = p.Name } : new Label { Text = p.Country! }
            })
            .ToList();
        spyLogger.AssertExecutedMqlContains("\"then\" : { \"Text\" : \"$Name\" }, \"else\" : { \"Text\" : \"$Country\" }");
    }

    // Over a join scope the ternary is staged by NativeJoinScopeProjectionBinder, whose computed leaf is read back whole
    // through IdName's class map (Id -> _id): it used to throw FormatException in NativeOnly and Native. Declines now.
    [Fact]
    public void Join_scope_ternary_over_constructions_the_class_map_misreads_declines()
    {
        var collection = SeedPlaces(nameof(Join_scope_ternary_over_constructions_the_class_map_misreads_declines));

        var results = NativeModeAssert.DeclinesCleanly(mode =>
        {
            using var db = CreateContext(collection, mode);
            return (from a in db.Entities.AsNoTracking()
                    join b in db.Entities.AsNoTracking() on a.Name equals b.Name
                    select new
                    {
                        a.Name,
                        X = b.City == "Seattle"
                            ? new IdName { Id = "PAY", Name = "Pay" }
                            : new IdName { Id = "REC", Name = "Receive" }
                    })
                .AsEnumerable()
                .Select(r => $"{r.Name}:{Show(r.X)}")
                .OrderBy(s => s)
                .ToList();
        });

        Assert.Equal(["Alpha:PAY|Pay", "Beta:REC|Receive", "Delta:REC|Receive", "Gamma:REC|Receive"], results);
    }

    // A bare (boxed) ternary over a join scope goes through TranslateSelect's bare join-scope value leaf, which reads
    // the ternary back whole: the same misread, so it declines.
    [Fact]
    public void Bare_boxed_join_scope_ternary_over_constructions_the_class_map_misreads_declines()
    {
        var collection = SeedPlaces(nameof(Bare_boxed_join_scope_ternary_over_constructions_the_class_map_misreads_declines));

        var results = NativeModeAssert.DeclinesCleanly(mode =>
        {
            using var db = CreateContext(collection, mode);
            return (from a in db.Entities.AsNoTracking()
                    join b in db.Entities.AsNoTracking() on a.Name equals b.Name
                    select (object)(b.Name == "Alpha"
                        ? new IdName { Id = "PAY", Name = "Pay" }
                        : new IdName { Id = "REC", Name = "Receive" }))
                .AsEnumerable()
                .Select(r => Show((IdName)r))
                .OrderBy(s => s)
                .ToList();
        });

        Assert.Equal(["PAY|Pay", "REC|Receive", "REC|Receive", "REC|Receive"], results);
    }

    // A construction its class map reads by member name stays a native $cond over the join scope.
    [Fact]
    public void Join_scope_ternary_over_constructions_the_class_map_reads_by_name_goes_native_with_parity()
    {
        var collection = SeedPlaces(nameof(Join_scope_ternary_over_constructions_the_class_map_reads_by_name_goes_native_with_parity));

        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateContext(collection, mode);
            return (from a in db.Entities.AsNoTracking()
                    join b in db.Entities.AsNoTracking() on a.Name equals b.Name
                    select new
                    {
                        a.Name,
                        X = b.City == "Seattle" ? new Label { Text = "Pay" } : new Label { Text = "Receive" }
                    })
                .AsEnumerable()
                .Select(r => $"{r.Name}:{r.X.Text}")
                .OrderBy(s => s)
                .ToList();
        });

        Assert.Equal(["Alpha:Pay", "Beta:Receive", "Delta:Receive", "Gamma:Receive"], results);
    }

    // A GroupBy result member ternary over constructions is read back whole by alias too (NativeGroupByBinder).
    [Fact]
    public void Grouped_ternary_over_constructions_the_class_map_misreads_declines()
    {
        var collection = SeedPlaces(nameof(Grouped_ternary_over_constructions_the_class_map_misreads_declines));

        var results = NativeModeAssert.DeclinesCleanly(mode =>
        {
            using var db = CreateContext(collection, mode);
            return db.Entities.AsNoTracking()
                .GroupBy(p => p.Country)
                .Select(g => new
                {
                    g.Key,
                    X = g.Key == "USA"
                        ? new IdName { Id = "PAY", Name = "Pay" }
                        : new IdName { Id = "REC", Name = "Receive" }
                })
                .AsEnumerable()
                .Select(r => $"{r.Key}:{Show(r.X)}")
                .OrderBy(s => s)
                .ToList();
        });

        Assert.Equal(["DE:REC|Receive", "FR:REC|Receive", "UK:REC|Receive", "USA:PAY|Pay"], results);
    }

    // A later operator reading the ternary's value (it is not in the server document) declines.
    [Fact]
    public void OrderBy_over_a_client_evaluated_ternary_declines()
    {
        var collection = SeedPlaces(nameof(OrderBy_over_a_client_evaluated_ternary_declines));

        NativeModeAssert.DeclinesCleanly(mode =>
        {
            using var db = CreateContext(collection, mode);
            return db.Entities.AsNoTracking()
                .Select(p => p.City == "Seattle"
                    ? new IdName { Id = "PAY", Name = p.Name }
                    : new IdName { Id = "REC", Name = "Receive" })
                .OrderBy(x => x.Name)
                .AsEnumerable()
                .Select(Show)
                .ToList();
        });
    }

    [Fact]
    public void Where_over_a_client_evaluated_ternary_declines()
    {
        var collection = SeedPlaces(nameof(Where_over_a_client_evaluated_ternary_declines));

        NativeModeAssert.DeclinesCleanly(mode =>
        {
            using var db = CreateContext(collection, mode);
            return db.Entities.AsNoTracking()
                .Select(p => p.City == "Seattle"
                    ? new IdName { Id = "PAY", Name = p.Name }
                    : new IdName { Id = "REC", Name = "Receive" })
                .Where(x => x.Id == "PAY")
                .AsEnumerable()
                .Select(Show)
                .ToList();
        });
    }

    // The conditional is not in the server document, so a set op (whose dedup/compare runs server-side) declines.
    [Fact]
    public void Union_over_a_client_evaluated_ternary_declines()
    {
        var collection = SeedPlaces(nameof(Union_over_a_client_evaluated_ternary_declines));

        NativeModeAssert.DeclinesCleanly(mode =>
        {
            using var db = CreateContext(collection, mode);
            return db.Entities.AsNoTracking()
                .Where(p => p.Name == "Alpha")
                .Select(p => p.City == "Seattle"
                    ? new IdName { Id = "PAY", Name = "Pay" }
                    : new IdName { Id = "REC", Name = "Receive" })
                .Union(db.Entities.AsNoTracking()
                    .Where(p => p.Name == "Beta")
                    .Select(p => p.City == "Seattle"
                        ? new IdName { Id = "PAY", Name = "Pay" }
                        : new IdName { Id = "REC", Name = "Receive" }))
                .AsEnumerable()
                .Select(Show)
                .OrderBy(s => s)
                .ToList();
        });
    }

    // Distinct would dedup server-side by the projected test only; not proven equivalent to deduplicating the
    // constructed values, so it declines.
    [Fact]
    public void Distinct_over_a_client_evaluated_ternary_declines()
    {
        var collection = SeedPlaces(nameof(Distinct_over_a_client_evaluated_ternary_declines));

        NativeModeAssert.DeclinesCleanly(mode =>
        {
            using var db = CreateContext(collection, mode);
            return db.Entities.AsNoTracking()
                .Select(p => p.City == "Seattle"
                    ? new IdName { Id = "PAY", Name = "Pay" }
                    : new IdName { Id = "REC", Name = "Receive" })
                .Distinct()
                .AsEnumerable()
                .Select(Show)
                .OrderBy(s => s)
                .ToList();
        });
    }
}
