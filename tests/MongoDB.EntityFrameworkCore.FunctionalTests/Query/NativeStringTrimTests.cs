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
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Infrastructure;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// Zero-arg <c>Trim()</c>/<c>TrimStart()</c>/<c>TrimEnd()</c> must render an explicit <c>chars</c> option built from
/// <c>char.IsWhiteSpace</c>: MongoDB's default whitespace set also includes U+0000 (NUL), so omitting it trims
/// differently from .NET and the driver-LINQ fallback.
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeStringTrimTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    public class Row
    {
        public ObjectId Id { get; set; }
        public string Label { get; set; } = "";
        public string S { get; set; } = "";
    }

    [Fact]
    public void Trim_with_embedded_NUL_matches_dotnet_oracle_not_mongo_default()
    {
        var collection = Seed(
            nameof(Trim_with_embedded_NUL_matches_dotnet_oracle_not_mongo_default),
            ("nul-padded", "\0Boston\0"),
            ("space-padded", "  Boston  "),
            ("mixed-padded", " \0Boston\0 "));

        AssertSelectMatchesOracle(collection, x => x.S.Trim());
    }

    [Fact]
    public void TrimStart_with_embedded_NUL_matches_dotnet_oracle_not_mongo_default()
    {
        var collection = Seed(
            nameof(TrimStart_with_embedded_NUL_matches_dotnet_oracle_not_mongo_default),
            ("nul-padded", "\0Boston\0"),
            ("space-padded", "  Boston  "),
            ("mixed-padded", " \0Boston\0 "));

        AssertSelectMatchesOracle(collection, x => x.S.TrimStart());
    }

    [Fact]
    public void TrimEnd_with_embedded_NUL_matches_dotnet_oracle_not_mongo_default()
    {
        var collection = Seed(
            nameof(TrimEnd_with_embedded_NUL_matches_dotnet_oracle_not_mongo_default),
            ("nul-padded", "\0Boston\0"),
            ("space-padded", "  Boston  "),
            ("mixed-padded", " \0Boston\0 "));

        AssertSelectMatchesOracle(collection, x => x.S.TrimEnd());
    }

    // `selector` must be an Expression, not a Func (see NativeStringConcatTests.AssertConcatMatchesOracle).
    private static void AssertSelectMatchesOracle(IMongoCollection<Row> collection, Expression<Func<Row, string>> selector)
    {
        using var oracleDb = CreateContext(collection, MongoQueryMode.Native);
        var oracle = oracleDb.Entities.AsNoTracking().ToList().Select(selector.Compile())
            .OrderBy(x => x, StringComparer.Ordinal).ToList();

        using var nativeOnly = CreateContext(collection, MongoQueryMode.NativeOnly);
        var nativeOnlyResult = nativeOnly.Entities.AsNoTracking().Select(selector).ToList()
            .OrderBy(x => x, StringComparer.Ordinal).ToList();
        Assert.Equal(oracle, nativeOnlyResult);

        using var native = CreateContext(collection, MongoQueryMode.Native);
        var nativeResult = native.Entities.AsNoTracking().Select(selector).ToList()
            .OrderBy(x => x, StringComparer.Ordinal).ToList();
        Assert.Equal(oracle, nativeResult);
    }

    private IMongoCollection<Row> Seed(string name, params (string Label, string S)[] rows)
    {
        var collection = database.MongoDatabase.GetCollection<Row>(UniqueCollectionName(name));
        collection.InsertMany(rows.Select(r => new Row { Label = r.Label, S = r.S }));
        return collection;
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
