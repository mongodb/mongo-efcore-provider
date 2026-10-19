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
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Infrastructure;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// A <c>char</c> argument to StartsWith/EndsWith/Contains is a one-character literal, escaped per execution when
/// parameterized, so a regex metacharacter like <c>'.'</c> matches only itself.
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeStringCharArgumentTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    public class Row
    {
        public ObjectId Id { get; set; }
        public string Label { get; set; } = "";
        public string Text { get; set; } = "";
    }

    [Fact]
    public void Parameterized_metacharacter_char_matches_literally()
    {
        using var context = CreateSeededContext(nameof(Parameterized_metacharacter_char_matches_literally));

        var dot = '.';
        var endsWithDot = context.Entities.AsNoTracking().Where(x => x.Text.EndsWith(dot)).Select(x => x.Label).ToList();
        var containsDollar = context.Entities.AsNoTracking().Where(x => x.Text.Contains('$')).Select(x => x.Label).ToList();

        Assert.Equal(["dot"], endsWithDot);
        Assert.Equal(["dollar"], containsDollar);
    }

    private SingleEntityDbContext<Row> CreateSeededContext(string testName)
    {
        var collection = database.MongoDatabase.GetCollection<Row>(
            TemporaryDatabaseFixtureBase.CreateCollectionName(testName) + Guid.NewGuid().ToString("N")[..8]);
        collection.InsertMany(
        [
            new Row { Label = "dot", Text = "end." },
            new Row { Label = "any", Text = "endX" },
            new Row { Label = "dollar", Text = "a$b" }
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
