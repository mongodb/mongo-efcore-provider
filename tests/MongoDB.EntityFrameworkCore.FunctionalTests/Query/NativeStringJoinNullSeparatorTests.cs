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
/// Final whole-branch review finding 4 (MINOR, real bug): <c>string.Join</c>'s elements were each wrapped in
/// <c>$ifNull</c> against <c>""</c> so a null element degrades gracefully rather than nulling the whole
/// <c>$concat</c> — but the SEPARATOR itself was left un-coalesced, so a null/parameterized-null separator
/// nulled the entire result (every row), diverging from .NET's own <c>string.Join</c> semantics (a null
/// separator behaves like an empty one). Fixed by wrapping the separator in the same
/// <c>MongoCoalesceExpression</c>/<c>$ifNull</c> the elements already use.
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

        // .NET's own oracle: string.Join(null, ...) behaves exactly like string.Join("", ...) — the null
        // separator contributes nothing, it does NOT null out the result. Asserted via a WHERE predicate
        // (matching this suite's own Join_non_aggregate spec-test precedent, a $expr equality comparison)
        // rather than a bare Select projection — a bare Select of a MongoConcatExpression is a separate,
        // pre-existing native-projection gap outside this finding's scope (only Trim/FirstOrLast were in
        // finding 5's allow-list fix).
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

        // The critical regression check: with the null separator un-coalesced, $concat would null out EVERY
        // row's Joined value, so BOTH predicates above would match zero rows instead of exactly one each.
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
