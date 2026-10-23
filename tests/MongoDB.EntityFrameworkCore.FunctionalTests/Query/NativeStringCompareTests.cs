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
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Infrastructure;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// <c>a.CompareTo(b)</c>/<c>string.Compare(a, b)</c> against a nullable receiver renders <c>$cmp</c>, whose BSON
/// total order puts null/missing before every string, matching .NET's null-first <c>string.Compare</c> — with no
/// <c>$ifNull</c> wrapping needed (see <c>MongoExpressionTranslator.StringCompare.cs</c>).
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeStringCompareTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    public class Row
    {
        public ObjectId Id { get; set; }
        public string Label { get; set; } = "";
        public string? Maybe { get; set; }
    }

    [Fact]
    public void Compare_less_than_minus_one_over_nullable_field_matches_null_and_lesser_strings()
    {
        using var context = CreateSeededContext(nameof(Compare_less_than_minus_one_over_nullable_field_matches_null_and_lesser_strings));

        var matches = context.Entities.AsNoTracking()
            .Where(x => string.Compare(x.Maybe, "b") == -1)
            .OrderBy(x => x.Label)
            .Select(x => x.Label)
            .ToList();

        Assert.Equal(["a", "missing", "null"], matches);
    }

    private SingleEntityDbContext<Row> CreateSeededContext(string testName)
    {
        var raw = database.MongoDatabase.GetCollection<BsonDocument>(
            TemporaryDatabaseFixtureBase.CreateCollectionName(testName) + Guid.NewGuid().ToString("N")[..8]);

        raw.InsertMany(
        [
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Label", "a" }, { "Maybe", "a" } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Label", "b" }, { "Maybe", "b" } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Label", "null" }, { "Maybe", BsonNull.Value } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Label", "missing" } }
        ]);

        var typed = database.MongoDatabase.GetCollection<Row>(raw.CollectionNamespace.CollectionName);

        return SingleEntityDbContext.Create(
            typed,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(MongoQueryMode.NativeOnly);
            });
    }
}
