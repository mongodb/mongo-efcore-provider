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
/// <c>$indexOfCP</c>/<c>$strLenCP</c> always compared case-sensitively).
/// <para>
/// (a) is fixed: the placeholder now carries the flag through to <c>MongoPipelineFactory</c> (emitting
/// options <c>"is"</c>), which uses a genuine PCRE/ICU-backed case-insensitive regex match — correct for all
/// Unicode, not just ASCII (see the parameterized-term tests below).
/// </para>
/// <para>
/// (b) was FIRST "fixed" by folding both operands through <c>$toLower</c>, but a later whole-branch review
/// caught that this only folds ASCII case: <c>$toLower</c> (and <c>$strcasecmp</c>) are, empirically (verified
/// directly against a live mongod 8.2.7 — see the reasoning on
/// <see cref="Query.NativeTranslation.MongoExpressionTranslatorTests.Field_to_field_term_with_OrdinalIgnoreCase_reports_not_translatable"/>
/// in the unit tests, mirrored here), genuinely ASCII-only: <c>É</c>/<c>Б</c>/<c>Ω</c> pass through untouched,
/// so <c>x.S.StartsWith(x.T, StringComparison.OrdinalIgnoreCase)</c> silently answered NO-match for
/// <c>S="ÉCOLE"</c>/<c>T="é"</c> where .NET says it should match. There is no other <c>$expr</c>-scoped
/// operator that does genuine Unicode-aware, locale-independent case folding (<c>collation</c> is a whole
/// command/collection option, not attachable to one operator inside a larger <c>$expr</c>), so rather than
/// leave this narrow-but-real silent-wrong-data gap live, the translator now DECLINES the whole
/// CaseInsensitive-field-to-field-term shape (see <c>MongoExpressionTranslator.TranslateNode</c>'s
/// <c>caseInsensitive</c> check just after constructing the field-to-field <c>termNode</c>), falling back to
/// driver-LINQ — which throws a clean <c>ExpressionNotSupportedException</c> for this shape (verified below),
/// never silently wrong data. This is a real narrowing versus the interim ASCII-only fix (a previously-working
/// ASCII field-to-field case now throws instead of succeeding), traded deliberately for correctness: no
/// non-ASCII input can ever reach this shape and get a silently wrong answer.
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
        // Replaces the old "_matches_oracle" ASCII-only field-to-field tests: that ASCII case used to succeed
        // natively via $toLower folding, but $toLower is genuinely ASCII-only (see class remarks), so the
        // shape as a WHOLE (not just its non-ASCII inputs) now declines — including this ASCII row — falling
        // back to driver-LINQ, which throws a clean ExpressionNotSupportedException. Never silent wrong data.
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

        // NativeOnly: the native translator declines at compile time, so NativeOnly's own coverage guard
        // throws (fallback is forbidden under this mode) rather than ever reaching driver-LINQ.
        using var nativeOnly = CreateContext(collection, MongoQueryMode.NativeOnly);
        Assert.Throws<MongoDB.EntityFrameworkCore.Query.NativeTranslation.NativeTranslationNotSupportedException>(
            () => nativeOnly.Entities.AsNoTracking().Where(predicate).Select(x => x.Label).ToList());

        // Native (default): falls back to driver-LINQ at runtime, which itself throws for this shape — this
        // is the exact pre-existing "loud failure" driver-LINQ has always had for OrdinalIgnoreCase (verified
        // by DriverLinq_field_to_field_OrdinalIgnoreCase_throws_for_every_case below), not a new exception
        // type this fix introduces.
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
    // Broad non-ASCII sample: Latin-1 (the finding's own É/é repro), Cyrillic, Greek, Latin-with-diacritics.
    // Every one of these previously (under the ASCII-only $toLower "fix") silently answered NO-match for a
    // case that .NET's OrdinalIgnoreCase says SHOULD match — proving this is not a one-character fluke.
    [InlineData("ÉCOLE", "é")] // Latin-1 (the finding's exact repro)
    [InlineData("МОСКВА", "москва")] // Cyrillic
    [InlineData("ΑΘΗΝΑ", "αθηνα")] // Greek
    [InlineData("GARÇON", "garçon")] // Latin, cedilla
    public void StartsWith_field_to_field_OrdinalIgnoreCase_non_ASCII_declines_instead_of_answering_wrong(string s, string t)
    {
        var collection = Seed(
            nameof(StartsWith_field_to_field_OrdinalIgnoreCase_non_ASCII_declines_instead_of_answering_wrong) + s,
            ("row", s, t));

        // Confirm the .NET oracle really would match (i.e. this IS a real gap, not a bad test fixture).
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
        // Confirms the safety net this decline relies on is still real: MongoQueryMode.DriverLinq has never
        // supported OrdinalIgnoreCase for a field-to-field StartsWith/Contains/EndsWith — the driver's own
        // LINQ v3 provider throws ExpressionNotSupportedException for the comparisonType argument itself,
        // regardless of ASCII/non-ASCII data (this is a compile-time-shape rejection, not a data-dependent
        // one). Verified directly against a live mongod 8.2.7.
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
        // Scope check: the decline above is CaseInsensitive-only. The plain case-SENSITIVE field-to-field
        // shape (StringComparison.Ordinal, or the parameterless overload) never went through $toLower at
        // all, so it is completely unaffected and still goes native.
        var collection = Seed(
            nameof(Field_to_field_term_Ordinal_case_sensitive_still_translates_natively),
            ("match", "Seattle", "Sea"),
            ("no-match-case", "seattle", "Sea"),
            ("no-match", "Portland", "Sea"));

        AssertWhereMatchesOracle(
            collection, x => x.S.StartsWith(x.T, StringComparison.Ordinal));
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
