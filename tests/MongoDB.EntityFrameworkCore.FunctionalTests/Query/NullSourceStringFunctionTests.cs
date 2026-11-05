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

using Microsoft.EntityFrameworkCore;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Infrastructure;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// EF-469 (silent-wrong-result part only): MongoDB's $toUpper, $toLower and $substrCP return "" for a null or
/// missing operand, so a null (or absent) string column run through ToUpper/ToLower/Substring came back as ""
/// instead of null wherever the value surfaces as data (projections, grouping keys, aggregates).
/// </summary>
public class NullSourceStringFunctionTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    class Row
    {
        public ObjectId _id { get; set; }
        public string? S { get; set; }
    }

    // "ab", explicit null, and a document with no S element at all (as written by other clients or older models).
    private SingleEntityDbContext<Row> Seed([System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        var db = SingleEntityDbContext.Create<Row>(database, collectionName: name);
        db.Set<Row>().AddRange(new Row { S = "ab" }, new Row { S = null });
        db.SaveChanges();
        db.ChangeTracker.Clear();
        database.GetCollection<BsonDocument>(db.CollectionNamespace)
            .InsertOne(new BsonDocument("_id", ObjectId.GenerateNewId()));
        return db;
    }

    private static void AssertNullAndMissingAreNull(string?[] results, string? nonNull)
    {
        Assert.Equal(3, results.Length);
        Assert.Equal(1, results.Count(r => r == nonNull));
        Assert.Equal(2, results.Count(r => r == null));
    }

    [Fact]
    public void ToUpper_of_null_or_missing_projects_null()
    {
        using var db = Seed();

        AssertNullAndMissingAreNull(db.Set<Row>().Select(x => x.S!.ToUpper()).ToArray(), "AB");
    }

    [Fact]
    public void ToLower_of_null_or_missing_projects_null()
    {
        using var db = Seed();

        AssertNullAndMissingAreNull(db.Set<Row>().Select(x => x.S!.ToLower()).ToArray(), "ab");
    }

    [Fact]
    public void ToUpperInvariant_of_null_or_missing_projects_null()
    {
        using var db = Seed();

        AssertNullAndMissingAreNull(db.Set<Row>().Select(x => x.S!.ToUpperInvariant()).ToArray(), "AB");
    }

    [Fact]
    public void ToLowerInvariant_of_null_or_missing_projects_null()
    {
        using var db = Seed();

        AssertNullAndMissingAreNull(db.Set<Row>().Select(x => x.S!.ToLowerInvariant()).ToArray(), "ab");
    }

    [Fact]
    public void Substring_of_null_or_missing_projects_null()
    {
        using var db = Seed();

        AssertNullAndMissingAreNull(db.Set<Row>().Select(x => x.S!.Substring(0, 1)).ToArray(), "a");
    }

    [Fact]
    public void Chained_calls_of_null_or_missing_project_null()
    {
        using var db = Seed();

        AssertNullAndMissingAreNull(db.Set<Row>().Select(x => x.S!.ToUpper().Substring(0, 1).ToLower()).ToArray(), "a");
    }

    [Fact]
    public void Null_guard_composes_with_other_operators()
    {
        using var db = Seed();

        var results = db.Set<Row>().Select(x => x.S!.ToUpper() + "x").ToArray();

        Assert.Equal(3, results.Length);
        Assert.Equal(1, results.Count(r => r == "ABx"));
        Assert.Equal(2, results.Count(r => r == null));
    }

    [Fact]
    public void Non_null_values_are_unchanged_by_the_null_guard()
    {
        using var db = Seed();

        var results = db.Set<Row>().Where(x => x.S != null).Select(x => new { U = x.S!.ToUpper(), L = x.S.ToLower() }).ToList();

        Assert.Equal("AB", Assert.Single(results).U);
    }

    [Fact]
    public void ToUpper_of_null_or_missing_projects_null_under_Distinct()
    {
        using var db = Seed();

        var results = db.Set<Row>().Select(x => new { V = x.S!.ToUpper() }).Distinct().ToList();

        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r.V == null);
        Assert.Contains(results, r => r.V == "AB");
    }

    [Fact]
    public void ToUpper_of_null_or_missing_projects_null_through_FirstOrDefault()
    {
        using var db = Seed();

        var results = db.Set<Row>().Where(x => x.S == null).Select(x => x.S!.ToUpper()).FirstOrDefault();

        Assert.Null(results);
    }

    [Fact]
    public void Scalar_projected_Contains_does_not_match_null_or_missing_rows_against_empty_string()
    {
        using var db = Seed();

        Assert.False(db.Set<Row>().Select(x => x.S!.ToUpper()).Contains(""));
        Assert.True(db.Set<Row>().Select(x => x.S!.ToUpper()).Contains("AB"));
    }

    [Fact]
    public void Scalar_projected_Contains_leaves_predicates_in_its_source_unguarded()
    {
        using var db = Seed();

        // Substring(0, 0) is "" for every row when unguarded, null and missing included, so a Where on it keeps all three rows and the guarded
        // projection then yields a null: Contains(null) is true. The native translator guards that predicate itself (a null source is not "", as in
        // SQL), so it filters the null and missing rows out before Contains looks, and Contains(null) is false. In neither mode does Contains add a
        // guard of its own to the predicate.
        var predicateKeepsNullRows = TestQueryMode.Current == MongoQueryMode.DriverLinq;
        Assert.Equal(predicateKeepsNullRows, db.Set<Row>().Where(x => x.S!.Substring(0, 0) == "").Select(x => x.S!.ToUpper()).Contains(null));
    }

    [Fact]
    public void Steps_and_literal_concatenation_above_a_case_mapping_project_null_for_null_or_missing()
    {
        using var db = Seed();

        AssertNullAndMissingAreNull(db.Set<Row>().Select(x => x.S!.ToUpper().Trim() + "!").ToArray(), "AB!");
        AssertNullAndMissingAreNull(db.Set<Row>().Select(x => "<" + x.S!.ToLower()).ToArray(), "<ab");
        AssertNullAndMissingAreNull(db.Set<Row>().Select(x => x.S!.ToUpper().Replace("A", "_").PadRight(3, '.')).ToArray(), "_B.");
    }

    [Fact]
    public void Distinct_after_a_case_mapping_dedups_the_mapped_values()
    {
        using var db = Seed();
        db.Set<Row>().AddRange(new Row { S = "AB" }, new Row { S = "Ab" }, new Row { S = "cd" });
        db.SaveChanges();

        // The server holds the raw values ("ab", "AB", "Ab", ...), which differ; once mapped, three of them are "AB".
        var results = db.Set<Row>().Select(x => x.S!.ToUpper()).Distinct().ToList();

        Assert.Equal(3, results.Count);
        Assert.Contains("AB", results);
        Assert.Contains("CD", results);
        Assert.Contains(null, results);
    }

    [Fact]
    public async Task Contains_after_a_case_mapping_looks_among_the_mapped_values_asynchronously()
    {
        using var db = Seed();

        Assert.True(await db.Set<Row>().Select(x => x.S!.ToUpper()).ContainsAsync("AB"));
        Assert.False(await db.Set<Row>().Select(x => x.S!.ToUpper()).ContainsAsync("ab"));
        Assert.False(await db.Set<Row>().Select(x => x.S!.ToUpper()).ContainsAsync(""));
        Assert.True(await db.Set<Row>().Select(x => x.S!.ToUpper()).ContainsAsync(null));

        var wanted = "AB";
        Assert.True(await db.Set<Row>().Select(x => x.S!.ToUpper()).ContainsAsync(wanted));
    }
}
