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
/// <c>string.FirstOrDefault()</c>/<c>LastOrDefault()</c> against a real server — specifically the review-fix
/// regression this pins (EF-322 Task 4): a genuinely EMPTY source and a genuinely NON-EMPTY source whose real
/// first/last character IS <c>'\0'</c> (a legal embedded-NUL string, e.g. <c>"\0abc"</c>) must BOTH compare
/// equal to the literal <c>'\0'</c> — an earlier revision rendered the empty-source branch as a BSON Int32
/// zero while the non-empty <c>$substrCP</c> branch produced a one-character BSON STRING, so a row with a
/// genuine embedded-NUL first/last character silently compared UNEQUAL to <c>'\0'</c> (crossing BSON type
/// brackets: MongoDB never considers a number equal to a string). See
/// <c>MongoStringFirstOrLastExpression</c>'s own remarks for the fix.
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeStringFirstOrLastTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    public class Row
    {
        public ObjectId Id { get; set; }
        public string Label { get; set; } = "";
        public string S { get; set; } = "";
    }

    [Fact]
    public void FirstOrDefault_equals_null_char_matches_oracle_for_both_empty_and_embedded_NUL_rows()
    {
        var collection = Seed(
            nameof(FirstOrDefault_equals_null_char_matches_oracle_for_both_empty_and_embedded_NUL_rows),
            ("empty", ""),
            ("embedded-nul-first", "\0abc"),
            ("ordinary", "abc"));

        AssertWhereMatchesOracle(collection, x => x.S.FirstOrDefault() == '\0');
    }

    [Fact]
    public void LastOrDefault_equals_null_char_matches_oracle_for_both_empty_and_embedded_NUL_rows()
    {
        var collection = Seed(
            nameof(LastOrDefault_equals_null_char_matches_oracle_for_both_empty_and_embedded_NUL_rows),
            ("empty", ""),
            ("embedded-nul-last", "abc\0"),
            ("ordinary", "abc"));

        AssertWhereMatchesOracle(collection, x => x.S.LastOrDefault() == '\0');
    }

    [Fact]
    public void FirstOrDefault_equals_ordinary_char_still_matches_oracle()
    {
        // Regression guard alongside the embedded-NUL fix above: an ordinary (non-NUL) literal comparison
        // must keep working exactly as it did before the fix (both $cond branches now agree on the SAME BSON
        // type — a one-character string — so this shape's rendering hasn't otherwise changed).
        var collection = Seed(
            nameof(FirstOrDefault_equals_ordinary_char_still_matches_oracle),
            ("empty", ""),
            ("matches", "abc"),
            ("no-match", "xyz"));

        AssertWhereMatchesOracle(collection, x => x.S.FirstOrDefault() == 'a');
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
