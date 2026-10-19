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
/// <c>string.Replace</c>'s find/replacement operands are <c>$literal</c>-wrapped so a <c>"$"</c>-prefixed value
/// isn't read as a field path, and a <see langword="null"/> replacement is treated as "" (.NET semantics) via
/// <c>$ifNull</c> rather than nulling out the whole result (server semantics).
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeStringReplaceTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    public class Row
    {
        public ObjectId Id { get; set; }
        public string Label { get; set; } = "";
        public string Text { get; set; } = "";
    }

    [Fact]
    public void Replace_dollar_prefixed_find_is_literal_not_a_field_path()
    {
        using var context = CreateSeededContext(nameof(Replace_dollar_prefixed_find_is_literal_not_a_field_path));

        var matches = context.Entities.AsNoTracking()
            .Where(x => x.Text.Replace("$Text", "hit") == "hit")
            .Select(x => x.Label)
            .ToList();

        Assert.Equal(["dollar-text"], matches);
    }

    [Fact]
    public void Replace_null_replacement_treated_as_empty_string()
    {
        using var context = CreateSeededContext(nameof(Replace_null_replacement_treated_as_empty_string));

        var matches = context.Entities.AsNoTracking()
            .Where(x => x.Text.Replace("Sea", null!) == "ttle")
            .Select(x => x.Label)
            .ToList();

        Assert.Equal(["seattle"], matches);
    }

    private SingleEntityDbContext<Row> CreateSeededContext(string testName)
    {
        var collection = database.MongoDatabase.GetCollection<Row>(
            TemporaryDatabaseFixtureBase.CreateCollectionName(testName) + Guid.NewGuid().ToString("N")[..8]);
        collection.InsertMany(
        [
            new Row { Label = "seattle", Text = "Seattle" },
            new Row { Label = "dollar-text", Text = "$Text" }
        ]);

        return SingleEntityDbContext.Create(
            collection,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(MongoQueryMode.NativeOnly);
            });
    }
}
