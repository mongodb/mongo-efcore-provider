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
/// Final whole-branch review finding 1 (CRITICAL): <c>StringComparison.OrdinalIgnoreCase</c> was silently
/// dropped — rendered as if case-sensitive, with no exception and wrong rows — for two term shapes that no
/// pre-existing test on this branch covered (every existing <c>OrdinalIgnoreCase</c> test used a constant
/// term only): (a) a PARAMETERIZED term (<c>PlaceholderTable.CreateRegexPlaceholder</c> had no
/// case-insensitive input, so <c>MongoPipelineFactory.Build</c> always emitted <c>BsonRegularExpression</c>
/// options <c>"s"</c>), and (b) a field-to-field <c>$expr</c> term
/// (<c>MongoAggregationExpressionRenderer.RenderRegexAsExpr</c> never read <c>CaseInsensitive</c> at all, so
/// <c>$indexOfCP</c>/<c>$strLenCP</c> always compared case-sensitively). Both are fixed: the placeholder now
/// carries the flag through to <c>MongoPipelineFactory</c> (emitting options <c>"is"</c>), and the <c>$expr</c>
/// renderer now folds both operands through <c>$toLower</c> before comparing when the term is case-insensitive.
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeStringCaseInsensitiveMatchTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    public class Row
    {
        public ObjectId Id { get; set; }
        public string Label { get; set; } = "";
        public string S { get; set; } = "";
        public string T { get; set; } = "";
    }

    [Theory]
    [InlineData("SEA")]
    [InlineData("sea")]
    [InlineData("SeA")]
    public void StartsWith_parameterized_term_OrdinalIgnoreCase_matches_oracle(string term)
    {
        var collection = Seed(
            nameof(StartsWith_parameterized_term_OrdinalIgnoreCase_matches_oracle) + term,
            ("upper", "SEATTLE", ""),
            ("lower", "seattle", ""),
            ("mixed", "Seattle", ""),
            ("no-match", "Portland", ""));

        AssertWhereMatchesOracle(
            collection, x => x.S.StartsWith(term, StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("SEA")]
    [InlineData("sea")]
    public void Contains_parameterized_term_OrdinalIgnoreCase_matches_oracle(string term)
    {
        var collection = Seed(
            nameof(Contains_parameterized_term_OrdinalIgnoreCase_matches_oracle) + term,
            ("upper", "USEATTLEX", ""),
            ("lower", "useattlex", ""),
            ("no-match", "Portland", ""));

        AssertWhereMatchesOracle(
            collection, x => x.S.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("TLE")]
    [InlineData("tle")]
    public void EndsWith_parameterized_term_OrdinalIgnoreCase_matches_oracle(string term)
    {
        var collection = Seed(
            nameof(EndsWith_parameterized_term_OrdinalIgnoreCase_matches_oracle) + term,
            ("upper", "SEATTLE", ""),
            ("lower", "seattle", ""),
            ("no-match", "Portland", ""));

        AssertWhereMatchesOracle(
            collection, x => x.S.EndsWith(term, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void StartsWith_field_to_field_term_OrdinalIgnoreCase_matches_oracle()
    {
        var collection = Seed(
            nameof(StartsWith_field_to_field_term_OrdinalIgnoreCase_matches_oracle),
            ("match-same-case", "Seattle", "Sea"),
            ("match-different-case", "SEATTLE", "sea"),
            ("no-match", "Portland", "Sea"));

        AssertWhereMatchesOracle(
            collection, x => x.S.StartsWith(x.T, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Contains_field_to_field_term_OrdinalIgnoreCase_matches_oracle()
    {
        var collection = Seed(
            nameof(Contains_field_to_field_term_OrdinalIgnoreCase_matches_oracle),
            ("match-same-case", "USEATTLEX", "SEA"),
            ("match-different-case", "useattlex", "SEA"),
            ("no-match", "Portland", "SEA"));

        AssertWhereMatchesOracle(
            collection, x => x.S.Contains(x.T, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EndsWith_field_to_field_term_OrdinalIgnoreCase_matches_oracle()
    {
        var collection = Seed(
            nameof(EndsWith_field_to_field_term_OrdinalIgnoreCase_matches_oracle),
            ("match-same-case", "SEATTLE", "TLE"),
            ("match-different-case", "seattle", "TLE"),
            ("no-match", "Portland", "TLE"));

        AssertWhereMatchesOracle(
            collection, x => x.S.EndsWith(x.T, StringComparison.OrdinalIgnoreCase));
    }

    // `predicate` MUST be Expression<Func<...>>, never a plain Func delegate — see NativeStringConcatTests'
    // own remarks on why a Func parameter here would silently pull every row into memory instead of
    // exercising the native translation at all.
    private static void AssertWhereMatchesOracle(IMongoCollection<Row> collection, Expression<Func<Row, bool>> predicate)
    {
        using var oracleDb = CreateContext(collection, MongoQueryMode.Native);
        var oracle = oracleDb.Entities.AsNoTracking().ToList().Where(predicate.Compile()).Select(x => x.Label)
            .OrderBy(x => x, StringComparer.Ordinal).ToList();

        // OrderBy(comparer) is applied AFTER ToList() (client-side, LINQ-to-Objects) — a server-translated
        // IQueryable.OrderBy with a custom IComparer has no MQL translation and throws, so the comparer-based
        // ordering must happen only once the rows are already materialized, exactly like the oracle leg above.
        using var nativeOnly = CreateContext(collection, MongoQueryMode.NativeOnly);
        var nativeOnlyResult = nativeOnly.Entities.AsNoTracking().Where(predicate).Select(x => x.Label).ToList()
            .OrderBy(x => x, StringComparer.Ordinal).ToList();
        Assert.Equal(oracle, nativeOnlyResult);

        using var native = CreateContext(collection, MongoQueryMode.Native);
        var nativeResult = native.Entities.AsNoTracking().Where(predicate).Select(x => x.Label).ToList()
            .OrderBy(x => x, StringComparer.Ordinal).ToList();
        Assert.Equal(oracle, nativeResult);
    }

    private IMongoCollection<Row> Seed(string name, params (string Label, string S, string T)[] rows)
    {
        var collection = database.MongoDatabase.GetCollection<Row>(UniqueCollectionName(name));
        collection.InsertMany(rows.Select(r => new Row { Label = r.Label, S = r.S, T = r.T }));
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
