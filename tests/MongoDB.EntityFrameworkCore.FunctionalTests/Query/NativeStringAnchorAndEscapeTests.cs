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
/// Pins the anchors and escaping of the regexes behind <c>StartsWith</c>/<c>EndsWith</c>/<c>Contains</c> and
/// <c>EF.Functions.Like</c> against a real server: a PCRE "$" also matches before a trailing "\n", and the term must
/// reach the server as a literal whatever characters it holds.
/// </summary>
public class NativeStringAnchorAndEscapeTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    public class Row
    {
        public ObjectId Id { get; set; }
        public string Label { get; set; } = "";
        public string S { get; set; } = "";
    }

    [Fact]
    public void EndsWith_does_not_match_before_a_trailing_newline()
    {
        var collection = Seed(
            nameof(EndsWith_does_not_match_before_a_trailing_newline),
            ("match", "report.pdf"), ("lf", "report.pdf\n"), ("crlf", "report.pdf\r\n"), ("other", "report.txt"));
        var suffix = ".pdf";

        AssertBothModes(collection, x => x.S.EndsWith(".pdf"), "match");
        AssertBothModes(collection, x => x.S.EndsWith(suffix), "match");
        AssertBothModes(collection, x => !x.S.EndsWith(suffix), "lf", "crlf", "other");
    }

    [Fact]
    public void Like_does_not_match_before_a_trailing_newline_and_percent_spans_newlines()
    {
        var collection = Seed(
            nameof(Like_does_not_match_before_a_trailing_newline_and_percent_spans_newlines),
            ("match", "ABC"), ("lf", "ABC\n"), ("crlf", "ABC\r\n"), ("short", "AB"), ("multiline", "A\nB\nC"));

        AssertBothModes(collection, x => EF.Functions.Like(x.S, "AB_"), "match");
        AssertBothModes(collection, x => EF.Functions.Like(x.S, "A%C"), "match", "multiline");
    }

    [Theory]
    [InlineData(@"\")]
    [InlineData("a.b")]
    [InlineData("a*b")]
    [InlineData("a+b")]
    [InlineData("a?b")]
    [InlineData("a|b")]
    [InlineData("a{1}b")]
    [InlineData("a[b]c")]
    [InlineData("a(b)c")]
    [InlineData("^a")]
    [InlineData("a$")]
    [InlineData("a#b c")]
    [InlineData("a\tb")]
    [InlineData("a\nb")]
    [InlineData("a\0b")]
    [InlineData("é😀")]
    public void Special_characters_in_a_term_match_only_themselves(string term)
    {
        // "decoy" is what the term would match if any of it were read as regex syntax.
        var collection = Seed(
            nameof(Special_characters_in_a_term_match_only_themselves) + Guid.NewGuid().ToString("N")[..6],
            ("prefix", term + "tail"), ("suffix", "head" + term), ("middle", "head" + term + "tail"), ("exact", term),
            ("decoy", "aab"), ("decoy2", "ab"), ("decoy3", "a"), ("decoy4", "abc"), ("decoy5", "headtail"));

        AssertBothModes(collection, x => x.S.StartsWith(term), "prefix", "exact");
        AssertBothModes(collection, x => x.S.EndsWith(term), "suffix", "exact");
        AssertBothModes(collection, x => x.S.Contains(term), "prefix", "suffix", "middle", "exact");
        AssertBothModes(collection, x => x.S == term, "exact");
    }

    [Fact]
    public void Like_treats_regex_metacharacters_as_literals()
    {
        var collection = Seed(
            nameof(Like_treats_regex_metacharacters_as_literals),
            ("match", "a.zzz[x]Q(\0"), ("dot", "aXzzz[x]Q(\0"), ("class", "a.zzzxQ(\0"));

        AssertBothModes(collection, x => EF.Functions.Like(x.S, "a.%[x]_(\0"), "match");
    }

    private static void AssertBothModes(
        IMongoCollection<Row> collection,
        System.Linq.Expressions.Expression<Func<Row, bool>> predicate,
        params string[] expectedLabels)
    {
        foreach (var mode in new[] { MongoQueryProvider.NativeOnly, MongoQueryProvider.Native })
        {
            using var context = SingleEntityDbContext.Create(
                collection,
                optionsBuilderAction: b =>
                {
                    b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                    new MongoDbContextOptionsBuilder(b).UseQueryProvider(mode);
                });

            var actual = context.Entities.AsNoTracking().Where(predicate).Select(x => x.Label).ToList()
                .OrderBy(x => x, StringComparer.Ordinal).ToList();
            Assert.Equal(expectedLabels.OrderBy(x => x, StringComparer.Ordinal).ToList(), actual);
        }
    }

    private IMongoCollection<Row> Seed(string name, params (string Label, string S)[] rows)
    {
        var collection = database.MongoDatabase.GetCollection<Row>(
            TemporaryDatabaseFixtureBase.CreateCollectionName(name) + Guid.NewGuid().ToString("N")[..8]);
        collection.InsertMany(rows.Select(r => new Row { Label = r.Label, S = r.S }));
        return collection;
    }
}
