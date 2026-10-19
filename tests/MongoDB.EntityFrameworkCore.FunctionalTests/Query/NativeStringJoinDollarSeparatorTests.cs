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
/// A <c>string.Join</c> separator starting with <c>$</c> must be treated as text. The separator is wrapped in
/// <c>$ifNull</c>, whose constant/parameter branches must be <c>$literal</c>-wrapped; otherwise <c>"$A"</c> is read
/// as a path to field <c>A</c> and the join silently uses that field's value.
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeStringJoinDollarSeparatorTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    public class Row
    {
        public ObjectId Id { get; set; }
        public string Label { get; set; } = "";
        public string A { get; set; } = "";
        public string B { get; set; } = "";
    }

    [Fact]
    public void Join_with_constant_dollar_separator_matches_dotnet_oracle()
    {
        using var context = CreateSeededContext(nameof(Join_with_constant_dollar_separator_matches_dotnet_oracle));

        var matches = context.Entities.AsNoTracking()
            .Where(x => string.Join("$A", new[] { x.A, x.B }) == "foo$Abar")
            .Select(x => x.Label)
            .ToList();

        Assert.Equal(["one"], matches);
    }

    [Fact]
    public void Join_with_parameterized_dollar_separator_matches_dotnet_oracle()
    {
        using var context = CreateSeededContext(nameof(Join_with_parameterized_dollar_separator_matches_dotnet_oracle));

        var separator = "$A";
        var expected = string.Join(separator, "foo", "bar");

        var matches = context.Entities.AsNoTracking()
            .Where(x => string.Join(separator, new[] { x.A, x.B }) == expected)
            .Select(x => x.Label)
            .ToList();

        Assert.Equal(["one"], matches);
    }

    private SingleEntityDbContext<Row> CreateSeededContext(string testName)
    {
        var collection = database.MongoDatabase.GetCollection<Row>(
            TemporaryDatabaseFixtureBase.CreateCollectionName(testName) + Guid.NewGuid().ToString("N")[..8]);
        collection.InsertMany(
        [
            new Row { Label = "one", A = "foo", B = "bar" },
            new Row { Label = "two", A = "baz", B = "qux" }
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
