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
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.Diagnostics;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Infrastructure;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// Generalizations of the Entity_equality_null native translation (spec test) to two closely-related
/// whole-entity-typed shapes that reuse the SAME <c>MongoElementRefExpression</c>-vs-null mechanism:
/// an owned single-reference navigation compared to null (<c>b.Address == null</c>), and the root entity
/// compared to itself (<c>c == c</c>), which is trivially true regardless of document content. Also covers
/// the remaining whole-entity shape, a non-null, non-self entity-typed operand (<c>Entity_equality_local</c>'s
/// shape, <c>c == other</c>) — this one goes native via genuinely different, key-based machinery instead
/// (<c>MongoExpressionTranslator.EntityEquality.cs</c>), since comparing against an arbitrary OTHER entity
/// is EF's key-based equality semantics, not document equality.
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeEntityEqualityTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
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

    // ════════════════════════════════════════════════════════════════════════════════════════════
    //  Root entity vs. itself — `c == c` / `c != c`, trivially true/false regardless of content
    // ════════════════════════════════════════════════════════════════════════════════════════════

    private class Customer
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
    }

    private IMongoCollection<Customer> SeedCustomers(string name)
    {
        var coll = database.MongoDatabase.GetCollection<BsonDocument>(UniqueCollectionName(name));
        coll.InsertMany([
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Name", "Alpha" } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Name", "Beta" } },
        ]);
        return database.MongoDatabase.GetCollection<Customer>(coll.CollectionNamespace.CollectionName);
    }

    [Fact]
    public void Entity_equality_self_goes_native()
    {
        var collection = SeedCustomers(nameof(Entity_equality_self_goes_native));
        using var db = CreateContextWithLogging(collection, MongoQueryMode.NativeOnly, null, out var spyLogger);

        // Under NativeOnly a shape that falls back throws NativeTranslationNotSupportedException; success
        // here proves `c == c` went through the native $$ROOT-vs-itself path rather than driver-LINQ.
#pragma warning disable CS1718 // Comparison made to same variable — deliberate: this is the shape under test.
        var results = db.Entities.AsNoTracking().Where(c => c == c).ToList();
#pragma warning restore CS1718

        Assert.Equal(2, results.Count);
        spyLogger.AssertExecutedMqlContains("\"$eq\" : [\"$$ROOT\", \"$$ROOT\"]");
    }

    [Fact]
    public void Entity_equality_self_negated_returns_no_rows()
    {
        var collection = SeedCustomers(nameof(Entity_equality_self_negated_returns_no_rows));
        using var db = CreateContextWithLogging(collection, MongoQueryMode.NativeOnly, null, out var spyLogger);

#pragma warning disable CS1718 // Comparison made to same variable — deliberate: this is the shape under test.
        var results = db.Entities.AsNoTracking().Where(c => c != c).ToList();
#pragma warning restore CS1718

        Assert.Empty(results);
        spyLogger.AssertExecutedMqlContains("\"$ne\" : [\"$$ROOT\", \"$$ROOT\"]");
    }

    // ════════════════════════════════════════════════════════════════════════════════════════════
    //  Owned single-reference navigation vs. null — `b.Address == null` / `b.Address != null`
    // ════════════════════════════════════════════════════════════════════════════════════════════

    private class Blog
    {
        public ObjectId Id { get; set; }
        public string Title { get; set; } = "";
        public Address? Address { get; set; }
    }

    private class Address
    {
        public string City { get; set; } = "";
    }

    private static readonly Action<ModelBuilder> BlogModel = mb => mb.Entity<Blog>().OwnsOne(b => b.Address);

    private IMongoCollection<Blog> SeedBlogs(string name)
    {
        var coll = database.MongoDatabase.GetCollection<BsonDocument>(UniqueCollectionName(name));
        coll.InsertMany([
            new BsonDocument
            {
                { "_id", ObjectId.GenerateNewId() }, { "Title", "HasAddress" },
                { "Address", new BsonDocument { { "City", "NYC" } } }
            },
            new BsonDocument
            {
                { "_id", ObjectId.GenerateNewId() }, { "Title", "NoAddress" }
                // "Address" deliberately omitted — the owned single-reference nav is missing.
            },
        ]);
        return database.MongoDatabase.GetCollection<Blog>(coll.CollectionNamespace.CollectionName);
    }

    [Fact]
    public void Owned_reference_navigation_null_check_goes_native()
    {
        var collection = SeedBlogs(nameof(Owned_reference_navigation_null_check_goes_native));
        using var db = CreateContextWithLogging(collection, MongoQueryMode.NativeOnly, BlogModel, out var spyLogger);

        var results = db.Entities.AsNoTracking().Where(b => b.Address == null).ToList();

        var blog = Assert.Single(results);
        Assert.Equal("NoAddress", blog.Title);
        spyLogger.AssertExecutedMqlContains("\"$eq\" : [{ \"$ifNull\" : [\"$Address\", null] }, null]");
    }

    [Fact]
    public void Owned_reference_navigation_not_null_check_goes_native()
    {
        var collection = SeedBlogs(nameof(Owned_reference_navigation_not_null_check_goes_native));
        using var db = CreateContextWithLogging(collection, MongoQueryMode.NativeOnly, BlogModel, out var spyLogger);

        var results = db.Entities.AsNoTracking().Where(b => b.Address != null).ToList();

        var blog = Assert.Single(results);
        Assert.Equal("HasAddress", blog.Title);
        spyLogger.AssertExecutedMqlContains("\"$ne\" : [{ \"$ifNull\" : [\"$Address\", null] }, null]");
    }

    // ════════════════════════════════════════════════════════════════════════════════════════════
    //  Entity vs. a DIFFERENT, non-null captured entity (`Entity_equality_local`'s shape) — goes native
    //  via a primary-key comparison (MongoExpressionTranslator.EntityEquality.cs), not the $$ROOT/self
    //  mechanism above: comparing a whole entity to an arbitrary OTHER entity value is EF's key-based
    //  equality semantics, not document equality, and has no serializer path for `$$ROOT`/a sub-document
    //  against an unrelated CLR object — so this needed genuinely different machinery.
    // ════════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Entity_equality_against_a_captured_local_goes_native()
    {
        var collection = SeedCustomers(nameof(Entity_equality_against_a_captured_local_goes_native));

        // Fetched via the raw driver, not EF, so the logger captured below sees only the query under test.
        var existingId = collection.Find(FilterDefinition<Customer>.Empty)
            .ToList().Single(c => c.Name == "Alpha").Id;

        using var db = CreateContextWithLogging(collection, MongoQueryMode.NativeOnly, null, out var spyLogger);
        var other = new Customer { Id = existingId, Name = "Ignored — only the key is compared" };

        // Under NativeOnly a shape that falls back throws NativeTranslationNotSupportedException; success
        // here proves `c == other` went through the native key-based path rather than driver-LINQ.
        var results = db.Entities.AsNoTracking().Where(c => c == other).ToList();

        var found = Assert.Single(results);
        Assert.Equal("Alpha", found.Name);
        spyLogger.AssertExecutedMqlContains("\"_id\" :");
        Assert.DoesNotContain("$$ROOT", spyLogger.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery));
    }

    [Fact]
    public void Entity_equality_against_a_captured_local_with_no_match_returns_empty()
    {
        var collection = SeedCustomers(nameof(Entity_equality_against_a_captured_local_with_no_match_returns_empty));
        using var db = CreateContextWithLogging(collection, MongoQueryMode.NativeOnly, null, out _);

        var other = new Customer { Id = ObjectId.GenerateNewId(), Name = "Other" };

        var results = db.Entities.AsNoTracking().Where(c => c == other).ToList();

        Assert.Empty(results);
    }
}
