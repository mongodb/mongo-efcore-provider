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
/// <c>string.FirstOrDefault()</c>/<c>LastOrDefault()</c> against a real server. An empty string and one whose
/// first/last character is an embedded <c>'\0'</c> must both equal <c>'\0'</c>, which requires both
/// <c>$cond</c> branches to produce the same BSON type (a number never equals a string). See
/// <c>MongoStringFirstOrLastExpression</c>.
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
        var collection = Seed(
            nameof(FirstOrDefault_equals_ordinary_char_still_matches_oracle),
            ("empty", ""),
            ("matches", "abc"),
            ("no-match", "xyz"));

        AssertWhereMatchesOracle(collection, x => x.S.FirstOrDefault() == 'a');
    }

    public class NullableRow
    {
        public ObjectId Id { get; set; }
        public string Label { get; set; } = "";
        public string? S { get; set; }
    }

    // $strLenCP is a server error for a null/missing argument, so the query must not abort; and, as C#/EF compare a
    // null with a char, a null field does not equal '\0' (only the empty string does). No client-side oracle
    // (FirstOrDefault on a null string throws), so expected rows are hand-written.
    [Fact]
    public void FirstOrDefault_equals_null_char_on_null_field_does_not_throw_and_does_not_match()
    {
        var collection = database.MongoDatabase.GetCollection<NullableRow>(UniqueCollectionName(
            nameof(FirstOrDefault_equals_null_char_on_null_field_does_not_throw_and_does_not_match)));
        collection.InsertMany(
        [
            new NullableRow { Label = "null-field", S = null },
            new NullableRow { Label = "empty", S = "" },
            new NullableRow { Label = "ordinary", S = "abc" }
        ]);

        AssertNullableFieldMatchesExpected(
            collection, x => x.S!.FirstOrDefault() == '\0', ["empty"]);
    }

    [Fact]
    public void LastOrDefault_equals_null_char_on_null_field_does_not_throw_and_does_not_match()
    {
        var collection = database.MongoDatabase.GetCollection<NullableRow>(UniqueCollectionName(
            nameof(LastOrDefault_equals_null_char_on_null_field_does_not_throw_and_does_not_match)));
        collection.InsertMany(
        [
            new NullableRow { Label = "null-field", S = null },
            new NullableRow { Label = "empty", S = "" },
            new NullableRow { Label = "ordinary", S = "abc" }
        ]);

        AssertNullableFieldMatchesExpected(
            collection, x => x.S!.LastOrDefault() == '\0', ["empty"]);
    }

    private static void AssertNullableFieldMatchesExpected(
        IMongoCollection<NullableRow> collection, Expression<Func<NullableRow, bool>> predicate, string[] expectedLabels)
    {
        var expected = expectedLabels.OrderBy(x => x, StringComparer.Ordinal).ToList();

        using var nativeOnly = CreateNullableContext(collection, MongoQueryMode.NativeOnly);
        var nativeOnlyResult = nativeOnly.Entities.AsNoTracking().Where(predicate).Select(x => x.Label).ToList()
            .OrderBy(x => x, StringComparer.Ordinal).ToList();
        Assert.Equal(expected, nativeOnlyResult);

        using var native = CreateNullableContext(collection, MongoQueryMode.Native);
        var nativeResult = native.Entities.AsNoTracking().Where(predicate).Select(x => x.Label).ToList()
            .OrderBy(x => x, StringComparer.Ordinal).ToList();
        Assert.Equal(expected, nativeResult);
    }

    private static SingleEntityDbContext<NullableRow> CreateNullableContext(IMongoCollection<NullableRow> collection, MongoQueryMode mode)
        => SingleEntityDbContext.Create(
            collection,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });

    // `predicate` must be an Expression; a Func would silently filter client-side and bypass translation.
    private static void AssertWhereMatchesOracle(IMongoCollection<Row> collection, Expression<Func<Row, bool>> predicate)
    {
        using var oracleDb = CreateContext(collection, MongoQueryMode.Native);
        var oracle = oracleDb.Entities.AsNoTracking().ToList().Where(predicate.Compile()).Select(x => x.Label)
            .OrderBy(x => x, StringComparer.Ordinal).ToList();

        // OrderBy(comparer) runs after ToList(); a custom IComparer has no MQL translation.
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
