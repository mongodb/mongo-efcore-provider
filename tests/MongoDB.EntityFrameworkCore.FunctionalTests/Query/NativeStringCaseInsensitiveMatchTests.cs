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
/// Pins <c>StringComparison.OrdinalIgnoreCase</c> for non-constant terms, where it is easy to drop silently
/// (case-sensitive match, wrong rows).
/// <para>
/// Parameterized term: the placeholder carries the flag so the regex gets options <c>"is"</c>, a
/// Unicode-correct case-insensitive match.
/// </para>
/// <para>
/// Field-to-field (<c>$expr</c>) term: the native translator declines, because <c>$toLower</c>/<c>$strcasecmp</c>
/// fold ASCII only (<c>"ÉCOLE"</c> would not start with <c>"é"</c>) and collation can't be scoped to one
/// operator. Driver-LINQ then throws <c>ExpressionNotSupportedException</c>, which is loud rather than wrong.
/// See <c>MongoExpressionTranslatorTests.Field_to_field_term_with_OrdinalIgnoreCase_reports_not_translatable</c>.
/// </para>
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

    [Theory]
    [InlineData(RegexKindUnderTest.StartsWith)]
    [InlineData(RegexKindUnderTest.Contains)]
    [InlineData(RegexKindUnderTest.EndsWith)]
    public void Field_to_field_term_OrdinalIgnoreCase_declines_cleanly_rather_than_answer_wrong(RegexKindUnderTest kind)
    {
        // The whole shape declines, even for ASCII data (see class remarks).
        var collection = Seed(
            nameof(Field_to_field_term_OrdinalIgnoreCase_declines_cleanly_rather_than_answer_wrong) + kind,
            ("match-same-case", "Seattle", "Sea"));

        Expression<Func<Row, bool>> predicate = kind switch
        {
            RegexKindUnderTest.StartsWith => x => x.S.StartsWith(x.T, StringComparison.OrdinalIgnoreCase),
            RegexKindUnderTest.Contains => x => x.S.Contains(x.T, StringComparison.OrdinalIgnoreCase),
            RegexKindUnderTest.EndsWith => x => x.S.EndsWith(x.T, StringComparison.OrdinalIgnoreCase),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

        // NativeOnly forbids the fallback, so the decline surfaces as its own exception.
        using var nativeOnly = CreateContext(collection, MongoQueryMode.NativeOnly);
        Assert.Throws<MongoDB.EntityFrameworkCore.Query.NativeTranslation.NativeTranslationNotSupportedException>(
            () => nativeOnly.Entities.AsNoTracking().Where(predicate).Select(x => x.Label).ToList());

        // Native falls back to driver-LINQ, which throws for this shape
        // (see DriverLinq_field_to_field_OrdinalIgnoreCase_throws_for_every_case).
        using var native = CreateContext(collection, MongoQueryMode.Native);
        Assert.Throws<MongoDB.Driver.Linq.ExpressionNotSupportedException>(
            () => native.Entities.AsNoTracking().Where(predicate).Select(x => x.Label).ToList());
    }

    public enum RegexKindUnderTest
    {
        StartsWith,
        Contains,
        EndsWith
    }

    [Theory]
    // Non-ASCII cases that .NET matches but $toLower-based folding would silently miss.
    [InlineData("ÉCOLE", "é")] // Latin-1
    [InlineData("МОСКВА", "москва")] // Cyrillic
    [InlineData("ΑΘΗΝΑ", "αθηνα")] // Greek
    [InlineData("GARÇON", "garçon")] // Latin, cedilla
    public void StartsWith_field_to_field_OrdinalIgnoreCase_non_ASCII_declines_instead_of_answering_wrong(string s, string t)
    {
        var collection = Seed(
            nameof(StartsWith_field_to_field_OrdinalIgnoreCase_non_ASCII_declines_instead_of_answering_wrong) + s,
            ("row", s, t));

        // Guards the fixture: .NET does match.
        Assert.StartsWith(t, s, StringComparison.OrdinalIgnoreCase);

        using var native = CreateContext(collection, MongoQueryMode.Native);
        Assert.Throws<MongoDB.Driver.Linq.ExpressionNotSupportedException>(
            () => native.Entities.AsNoTracking()
                .Where(x => x.S.StartsWith(x.T, StringComparison.OrdinalIgnoreCase))
                .Select(x => x.Label).ToList());
    }

    [Fact]
    public void DriverLinq_field_to_field_OrdinalIgnoreCase_throws_for_every_case()
    {
        // The native decline relies on driver-LINQ rejecting this shape (independent of data) rather than
        // answering wrong.
        var collection = Seed(
            nameof(DriverLinq_field_to_field_OrdinalIgnoreCase_throws_for_every_case),
            ("row", "Seattle", "Sea"));

        using var driverLinq = CreateContext(collection, MongoQueryMode.DriverLinq);
        Assert.Throws<MongoDB.Driver.Linq.ExpressionNotSupportedException>(
            () => driverLinq.Entities.AsNoTracking()
                .Where(x => x.S.StartsWith(x.T, StringComparison.OrdinalIgnoreCase))
                .Select(x => x.Label).ToList());
    }

    [Fact]
    public void Field_to_field_term_Ordinal_case_sensitive_still_translates_natively()
    {
        // The decline is case-insensitive-only; the case-sensitive field-to-field shape still goes native.
        var collection = Seed(
            nameof(Field_to_field_term_Ordinal_case_sensitive_still_translates_natively),
            ("match", "Seattle", "Sea"),
            ("no-match-case", "seattle", "Sea"),
            ("no-match", "Portland", "Sea"));

        AssertWhereMatchesOracle(
            collection, x => x.S.StartsWith(x.T, StringComparison.Ordinal));
    }

    // `predicate` must be an Expression, not a Func: a Func would run client-side over every row and never
    // exercise the translator.
    private static void AssertWhereMatchesOracle(IMongoCollection<Row> collection, Expression<Func<Row, bool>> predicate)
    {
        using var oracleDb = CreateContext(collection, MongoQueryMode.Native);
        var oracle = oracleDb.Entities.AsNoTracking().ToList().Where(predicate.Compile()).Select(x => x.Label)
            .OrderBy(x => x, StringComparer.Ordinal).ToList();

        // OrderBy(comparer) runs after ToList(): a server-side OrderBy with a custom IComparer can't translate.
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
