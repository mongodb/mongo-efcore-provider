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
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.Diagnostics;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Infrastructure;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// EF-247: native <c>Regex.IsMatch(input, pattern)</c> where the pattern is not a constant: a string field of the
/// entity or a query parameter. Both render via <c>$expr</c>/<c>$regexMatch</c> (<c>$regularExpression</c> needs a
/// literal). Explicit <c>DriverLinq</c> still throws for a non-constant pattern (the driver has no translation), which
/// is pinned here so a driver change is noticed.
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeRegexIsMatchNonConstantPatternTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    public class Row
    {
        public ObjectId Id { get; set; }
        public string? Foo { get; set; }
        public string? Bar { get; set; }
    }

    private static readonly Row[] ValidRows =
    [
        new() { Foo = "Seattle", Bar = "^S" },
        new() { Foo = "Seattle", Bar = "^T" },
        new() { Foo = "seattle", Bar = "^S" },
        new() { Foo = "Portland", Bar = "land$" },
        new() { Foo = "Portland", Bar = "^Port.*d$" },
        new() { Foo = "", Bar = "^$" },
        new() { Foo = "abc", Bar = "" },
    ];

    [Fact]
    public void Field_pattern_matches_oracle()
        => AssertMatchesOracle(ValidRows, r => Regex.IsMatch(r.Foo!, r.Bar!));

    [Fact]
    public void Negated_field_pattern_matches_oracle()
        => AssertMatchesOracle(ValidRows, r => !Regex.IsMatch(r.Foo!, r.Bar!));

    [Fact]
    public void Field_pattern_with_constant_options_matches_oracle()
        => AssertMatchesOracle(ValidRows, r => Regex.IsMatch(r.Foo!, r.Bar!, RegexOptions.IgnoreCase));

    [Fact]
    public void Field_pattern_with_Multiline_matches_oracle()
        => AssertMatchesOracle(
            [new Row { Foo = "a\nb", Bar = "^b" }, new Row { Foo = "a\nb", Bar = "^c" }],
            r => Regex.IsMatch(r.Foo!, r.Bar!, RegexOptions.Multiline));

    [Fact]
    public void Field_pattern_combined_with_other_predicates()
        => AssertMatchesOracle(ValidRows, r => r.Foo != "" && Regex.IsMatch(r.Foo!, r.Bar!) || r.Bar == "");

    [Fact]
    public void Parameter_pattern_matches_oracle()
    {
        var pattern = "^S";
        AssertMatchesOracle(ValidRows, r => Regex.IsMatch(r.Foo!, pattern));
    }

    [Fact]
    public void Negated_parameter_pattern_matches_oracle()
    {
        var pattern = "land$";
        AssertMatchesOracle(ValidRows, r => !Regex.IsMatch(r.Foo!, pattern));
    }

    [Fact]
    public void Constant_pattern_still_uses_query_dialect()
    {
        var collection = Seed(ValidRows);
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly, out var spy);
        var ids = db.Entities.AsNoTracking().Where(r => Regex.IsMatch(r.Foo!, "^S")).Select(r => r.Id).ToList();
        Assert.Equal(2, ids.Count);

        var mql = spy.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery);
        Assert.Contains("$regularExpression", mql);
        Assert.DoesNotContain("$expr", mql);
    }

    [Fact]
    public void Field_pattern_renders_via_expr_regexMatch_not_regularExpression()
    {
        var collection = Seed(ValidRows);
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly, out var spy);
        db.Entities.AsNoTracking().Where(r => Regex.IsMatch(r.Foo!, r.Bar!)).Select(r => r.Id).ToList();

        var mql = spy.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery);
        Assert.Contains("$expr", mql);
        Assert.Contains("$regexMatch", mql);
        Assert.DoesNotContain("$regularExpression", mql);
    }

    [Fact]
    public void Parameter_pattern_renders_via_expr_regexMatch_not_regularExpression()
    {
        var collection = Seed(ValidRows);
        var pattern = "^S";
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly, out var spy);
        db.Entities.AsNoTracking().Where(r => Regex.IsMatch(r.Foo!, pattern)).Select(r => r.Id).ToList();

        var mql = spy.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery);
        Assert.Contains("$expr", mql);
        Assert.Contains("$regexMatch", mql);
        Assert.DoesNotContain("$regularExpression", mql);
    }

    // Null/missing input or pattern: .NET throws ArgumentNullException, so there is no CLR oracle; native follows the
    // existing constant-pattern behavior and yields "no match" (false), never an error. Negation is the complement.
    [Fact]
    public void Null_or_missing_field_or_pattern_does_not_match()
    {
        var collection = SeedWithMissing();
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly, out _);

        var matched = db.Entities.AsNoTracking().Where(r => Regex.IsMatch(r.Foo!, r.Bar!)).Select(r => r.Id).ToList();
        Assert.Single(matched);
        Assert.Equal(_matchingId, matched[0]);

        var notMatched = db.Entities.AsNoTracking().Where(r => !Regex.IsMatch(r.Foo!, r.Bar!)).Select(r => r.Id).ToList();
        Assert.Equal(4, notMatched.Count);
        Assert.DoesNotContain(_matchingId, notMatched);
    }

    [Fact]
    public void Null_parameter_pattern_does_not_match()
    {
        var collection = Seed(ValidRows);
        string? pattern = null;
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly, out _);

        Assert.Empty(db.Entities.AsNoTracking().Where(r => Regex.IsMatch(r.Foo!, pattern!)).ToList());
    }

    // An invalid regex stored in the data makes the server reject the aggregation (parity with .NET, which throws
    // RegexParseException): a loud error, never a wrong answer.
    [Fact]
    public void Invalid_pattern_in_data_throws_server_error()
    {
        var collection = Seed([new Row { Foo = "abc", Bar = "(" }]);
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly, out _);

        Assert.Throws<MongoCommandException>(
            () => db.Entities.AsNoTracking().Where(r => Regex.IsMatch(r.Foo!, r.Bar!)).ToList());
    }

    // A non-constant RegexOptions can't be mapped to the constant $regexMatch options string: NativeOnly declines.
    [Fact]
    public void Non_constant_options_decline_under_NativeOnly()
    {
        var collection = Seed(ValidRows);
        var options = RegexOptions.IgnoreCase;
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly, out _);

        Assert.ThrowsAny<Exception>(
            () => db.Entities.AsNoTracking().Where(r => Regex.IsMatch(r.Foo!, r.Bar!, options)).ToList());
    }

    // Pins the driver leg: DriverLinq cannot translate a non-constant pattern. If this starts passing, the driver
    // gained support and the ticket's DriverLinq leg can be unblocked.
    [Fact]
    public void DriverLinq_field_pattern_throws()
    {
        var collection = Seed(ValidRows);
        using var db = CreateContext(collection, MongoQueryMode.DriverLinq, out _);

        var ex = Assert.ThrowsAny<Exception>(
            () => db.Entities.AsNoTracking().Where(r => Regex.IsMatch(r.Foo!, r.Bar!)).ToList());
        Assert.Contains("IsMatch", ex.ToString());
    }

    // Unlike a field pattern, a parameter pattern is captured by the driver as a constant, so DriverLinq already
    // works; native must agree with it.
    [Fact]
    public void DriverLinq_parameter_pattern_matches_oracle()
    {
        var collection = Seed(ValidRows);
        var pattern = "^S";
        Expression<Func<Row, bool>> predicate = r => Regex.IsMatch(r.Foo!, pattern);
        var expected = ValidRows.AsQueryable().Where(predicate).Count();
        using var db = CreateContext(collection, MongoQueryMode.DriverLinq, out _);

        Assert.Equal(expected, db.Entities.AsNoTracking().Where(predicate).Count());
    }

    private ObjectId _matchingId;

    private IMongoCollection<Row> SeedWithMissing()
    {
        var name = TemporaryDatabaseFixtureBase.CreateCollectionName(Guid.NewGuid().ToString("N")[..8]);
        var collection = database.MongoDatabase.GetCollection<BsonDocument>(name);
        _matchingId = ObjectId.GenerateNewId();
        collection.InsertMany(
        [
            new BsonDocument { { "_id", _matchingId }, { "Foo", "Seattle" }, { "Bar", "^S" } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Foo", "Seattle" }, { "Bar", BsonNull.Value } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Foo", "Seattle" } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Foo", BsonNull.Value }, { "Bar", "^S" } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Bar", "^S" } },
        ]);
        return database.MongoDatabase.GetCollection<Row>(name);
    }

    // `predicate` must be an Expression, or it binds to Enumerable.Where and never runs natively.
    private void AssertMatchesOracle(Row[] rows, Expression<Func<Row, bool>> predicate)
    {
        var collection = Seed(rows);

        using var oracleDb = CreateContext(collection, MongoQueryMode.Native, out _);
        var oracle = oracleDb.Entities.AsNoTracking().ToList().AsQueryable().Where(predicate).Select(r => r.Id).ToList();
        Assert.NotEmpty(oracle);
        Assert.NotEqual(rows.Length, oracle.Count); // the predicate must discriminate

        using var nativeOnly = CreateContext(collection, MongoQueryMode.NativeOnly, out var spy);
        var nativeOnlyResult = nativeOnly.Entities.AsNoTracking().Where(predicate).Select(r => r.Id).ToList();
        Assert.Equal(oracle.OrderBy(x => x), nativeOnlyResult.OrderBy(x => x));
        Assert.Contains("$regexMatch", spy.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery));

        using var native = CreateContext(collection, MongoQueryMode.Native, out _);
        var nativeResult = native.Entities.AsNoTracking().Where(predicate).Select(r => r.Id).ToList();
        Assert.Equal(oracle.OrderBy(x => x), nativeResult.OrderBy(x => x));
    }

    private IMongoCollection<Row> Seed(Row[] rows)
    {
        var collection = database.MongoDatabase.GetCollection<Row>(
            TemporaryDatabaseFixtureBase.CreateCollectionName(Guid.NewGuid().ToString("N")[..8]));
        collection.InsertMany(rows);
        return collection;
    }

    private static SingleEntityDbContext<Row> CreateContext(
        IMongoCollection<Row> collection, MongoQueryMode mode, out SpyLoggerProvider spy)
    {
        var (loggerFactory, provider) = SpyLoggerProvider.Create();
        spy = provider;
        return SingleEntityDbContext.Create(
            collection,
            loggerFactory,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                b.EnableSensitiveDataLogging();
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });
    }
}
