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
/// A null <c>string.Join</c> separator must behave like <c>""</c> (as in .NET); un-coalesced, it would null the
/// whole <c>$concat</c> on every row. The separator gets the same <c>$ifNull</c> the elements use.
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeStringJoinNullSeparatorTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    public class Row
    {
        public ObjectId Id { get; set; }
        public string Label { get; set; } = "";
        public string A { get; set; } = "";
        public string B { get; set; } = "";
    }

    [Fact]
    public void Join_with_parameterized_null_separator_matches_dotnet_oracle()
    {
        var collection = database.MongoDatabase.GetCollection<Row>(
            UniqueCollectionName(nameof(Join_with_parameterized_null_separator_matches_dotnet_oracle)));
        collection.InsertMany(
        [
            new Row { Label = "one", A = "foo", B = "bar" },
            new Row { Label = "two", A = "baz", B = "qux" }
        ]);

        string? separator = null;

        // Asserted via a Where predicate because a bare Select of a MongoConcatExpression isn't native.
        var expectedOneJoined = string.Join(separator!, "foo", "bar");
        var expectedTwoJoined = string.Join(separator!, "baz", "qux");

        using var nativeOnly = CreateContext(collection, MongoQueryMode.NativeOnly);
        var oneMatches = nativeOnly.Entities.AsNoTracking()
            .Where(x => string.Join(separator!, new[] { x.A, x.B }) == expectedOneJoined)
            .Select(x => x.Label)
            .ToList();
        Assert.Equal(["one"], oneMatches);

        var twoMatches = nativeOnly.Entities.AsNoTracking()
            .Where(x => string.Join(separator!, new[] { x.A, x.B }) == expectedTwoJoined)
            .Select(x => x.Label)
            .ToList();
        Assert.Equal(["two"], twoMatches);

        // An un-coalesced null separator would make every row's join null.
        var noneMatchNull = nativeOnly.Entities.AsNoTracking()
            .Where(x => string.Join(separator!, new[] { x.A, x.B }) == null)
            .Select(x => x.Label)
            .ToList();
        Assert.Empty(noneMatchNull);
    }

    private string UniqueCollectionName(string name)
        => TemporaryDatabaseFixtureBase.CreateCollectionName(name) + Guid.NewGuid().ToString("N")[..8];

    private static SingleEntityDbContext<Row> CreateContext(IMongoCollection<Row> collection, MongoQueryMode mode)
        => SingleEntityDbContext.Create(
            collection,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });
}
