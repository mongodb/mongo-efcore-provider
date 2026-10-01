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
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Infrastructure;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// A bare projection of a stored required scalar whose element is MISSING reads <c>default(T)</c> (as driver-LINQ's
/// <c>$project</c> push-down did); an explicit BSON null still throws, and whole-entity materialization stays strict.
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeMissingRequiredScalarProjectionTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    public class Item
    {
        public ObjectId Id { get; set; }
        public string Title { get; set; } = "";
        public int Rank { get; set; }
    }

    [Fact]
    public void Bare_scalar_projection_of_a_missing_required_element_reads_default()
    {
        var collection = Seed(nameof(Bare_scalar_projection_of_a_missing_required_element_reads_default), nullRow: false);

        Assert.Equal(
            [1, 2, 0],
            NativeModeAssert.NativeAndExpected(
                mode =>
                {
                    using var db = CreateContext(collection, mode);
                    return db.Entities.AsNoTracking().OrderBy(x => x.Title).Select(x => x.Rank).ToList();
                },
                [1, 2, 0]));
    }

    [Fact]
    public void Anonymous_projection_of_a_missing_required_element_reads_default()
    {
        var collection = Seed(nameof(Anonymous_projection_of_a_missing_required_element_reads_default), nullRow: false);

        NativeModeAssert.NativeAndExpected(
            mode =>
            {
                using var db = CreateContext(collection, mode);
                return db.Entities.AsNoTracking().OrderBy(x => x.Title).Select(x => new { x.Rank }).ToList()
                    .Select(a => a.Rank).ToList();
            },
            [1, 2, 0]);

        NativeModeAssert.NativeAndExpected(
            mode =>
            {
                using var db = CreateContext(collection, mode);
                return db.Entities.AsNoTracking().OrderBy(x => x.Title).Select(x => new { x.Title, x.Rank }).ToList()
                    .Select(a => $"{a.Title}:{a.Rank}").ToList();
            },
            ["a:1", "b:2", "c:0"]);
    }

    [Fact]
    public void Bare_scalar_projection_of_an_explicit_null_is_unchanged_by_the_missing_element_rule()
    {
        var collection = Seed(
            nameof(Bare_scalar_projection_of_an_explicit_null_is_unchanged_by_the_missing_element_rule), nullRow: true);

        // Observed behavior, pinned so the missing-element rule can't silently widen to explicit null. Driver-LINQ
        // throws FormatException. Native reads default(int): the "null on a required property" check in
        // BsonBinding compares an unboxed int to null (never true), so it doesn't fire for a value-typed T.
        // That native leniency predates this change and is a separate parity gap (see the Task 1.10 report).
        using (var db = CreateContext(collection, MongoQueryMode.DriverLinq))
        {
            Assert.Throws<FormatException>(
                () => db.Entities.AsNoTracking().Where(x => x.Title == "d").Select(x => x.Rank).ToList());
        }

        foreach (var mode in new[] { MongoQueryMode.NativeOnly, MongoQueryMode.Native })
        {
            using var db = CreateContext(collection, mode);
            Assert.Equal(
                [0], db.Entities.AsNoTracking().Where(x => x.Title == "d").Select(x => x.Rank).ToList());
        }
    }

    [Fact]
    public void Whole_entity_read_of_a_missing_required_element_still_throws()
    {
        var collection = Seed(nameof(Whole_entity_read_of_a_missing_required_element_still_throws), nullRow: false);

        foreach (var mode in new[] { MongoQueryMode.NativeOnly, MongoQueryMode.Native, MongoQueryMode.DriverLinq })
        {
            using var db = CreateContext(collection, mode);
            var ex = Assert.Throws<InvalidOperationException>(
                () => db.Entities.AsNoTracking().Where(x => x.Title == "c").ToList());
            Assert.Contains("missing for required non-nullable property", ex.Message);
        }
    }

    private IMongoCollection<Item> Seed(string name, bool nullRow)
    {
        var raw = database.MongoDatabase.GetCollection<BsonDocument>(UniqueCollectionName(name));
        var docs = new List<BsonDocument>
        {
            new() {{"_id", ObjectId.GenerateNewId()}, {"Title", "a"}, {"Rank", 1}},
            new() {{"_id", ObjectId.GenerateNewId()}, {"Title", "b"}, {"Rank", 2}},
            new() {{"_id", ObjectId.GenerateNewId()}, {"Title", "c"}},
        };
        if (nullRow)
        {
            docs.Add(new BsonDocument {{"_id", ObjectId.GenerateNewId()}, {"Title", "d"}, {"Rank", BsonNull.Value}});
        }

        raw.InsertMany(docs);
        return database.MongoDatabase.GetCollection<Item>(raw.CollectionNamespace.CollectionName);
    }

    private static SingleEntityDbContext<Item> CreateContext(IMongoCollection<Item> collection, MongoQueryMode mode)
        => SingleEntityDbContext.Create(
            collection,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });

    private string UniqueCollectionName(string name)
        => TemporaryDatabaseFixtureBase.CreateCollectionName(name) + Guid.NewGuid().ToString("N")[..8];
}
