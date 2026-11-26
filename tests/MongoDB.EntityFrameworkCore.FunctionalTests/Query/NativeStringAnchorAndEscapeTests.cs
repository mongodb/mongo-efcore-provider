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

#if !EF8 // The provider has no ExecuteDelete/ExecuteUpdate translation on EF8.
    // ExecuteDelete/ExecuteUpdate translate their filter through the driver-LINQ bridge, not the native translator.
    [Fact]
    public void ExecuteDelete_EndsWith_does_not_match_before_a_trailing_newline()
    {
        var collection = Seed(
            nameof(ExecuteDelete_EndsWith_does_not_match_before_a_trailing_newline),
            ("match", "report.pdf"), ("lf", "report.pdf\n"), ("crlf", "report.pdf\r\n"), ("other", "report.txt"));
        var suffix = ".pdf";

        using var context = CreateContext(collection, MongoQueryProvider.Native);
        var deleted = context.Entities.Where(x => x.S.EndsWith(suffix)).ExecuteDelete();

        Assert.Equal(1, deleted);
        Assert.Equal(["crlf", "lf", "other"], Labels(collection));
    }

    [Fact]
    public void ExecuteUpdate_EndsWith_does_not_match_before_a_trailing_newline()
    {
        var collection = Seed(
            nameof(ExecuteUpdate_EndsWith_does_not_match_before_a_trailing_newline),
            ("match", "report.pdf"), ("lf", "report.pdf\n"), ("other", "report.txt"));

        using var context = CreateContext(collection, MongoQueryProvider.Native);
        var updated = context.Entities.Where(x => x.S.EndsWith(".pdf")).ExecuteUpdate(s => s.SetProperty(x => x.Label, "hit"));

        Assert.Equal(1, updated);
        Assert.Equal(["hit", "lf", "other"], Labels(collection));
    }

    [Fact]
    public void ExecuteDelete_EndsWith_OrdinalIgnoreCase_matches_case_insensitively_but_not_before_a_trailing_newline()
    {
        var collection = Seed(
            nameof(ExecuteDelete_EndsWith_OrdinalIgnoreCase_matches_case_insensitively_but_not_before_a_trailing_newline),
            ("upper", "REPORT.PDF"), ("lower", "report.pdf"), ("lf", "report.PDF\n"), ("other", "report.txt"));
        var suffix = ".pdf";

        using var context = CreateContext(collection, MongoQueryProvider.Native);
        context.Entities.Where(x => x.S.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)).ExecuteDelete();

        Assert.Equal(["lf", "other"], Labels(collection));
    }

    [Fact]
    public void ExecuteDelete_EndsWith_special_characters_match_only_themselves()
    {
        var collection = Seed(
            nameof(ExecuteDelete_EndsWith_special_characters_match_only_themselves),
            ("match", "head.*[x]\0"), ("decoy", "headQQQQQ\0"), ("decoy2", "head.*[x]"));
        var suffix = ".*[x]\0";

        using var context = CreateContext(collection, MongoQueryProvider.Native);
        context.Entities.Where(x => x.S.EndsWith(suffix)).ExecuteDelete();

        Assert.Equal(["decoy", "decoy2"], Labels(collection));
    }
#endif

    // "||" and projections render as $expr/$regexMatch rather than a plain regex filter.
    [Fact]
    public void Anchors_hold_in_a_projection_and_under_or()
    {
        var collection = Seed(
            nameof(Anchors_hold_in_a_projection_and_under_or),
            ("match", "report.pdf"), ("lf", "report.pdf\n"), ("other", "report.txt"));

        AssertBothModes(collection, x => x.S.EndsWith(".pdf") || x.Label == "other", "match", "other");
        AssertBothModes(collection, x => EF.Functions.Like(x.S, "report.pd_") || x.Label == "other", "match", "other");
        AssertBothModes(collection, x => !(x.S.EndsWith(".pdf") || x.Label == "other"), "lf");

        // NativeOnly refuses a non-entity projection, so Native it is (these go through the driver bridge).
        {
            using var context = CreateContext(collection, MongoQueryProvider.Native);
            var ends = context.Entities.AsNoTracking().OrderBy(x => x.Label).Select(x => x.S.EndsWith(".pdf")).ToList();
            Assert.Equal([false, true, false], ends); // lf, match, other
        }
    }

    [Theory]
    [InlineData("startsWith")]
    [InlineData("endsWith")]
    [InlineData("contains")]
    public void OrdinalIgnoreCase_overloads_match_case_insensitively_and_treat_the_term_literally(string method)
    {
        var collection = Seed(
            nameof(OrdinalIgnoreCase_overloads_match_case_insensitively_and_treat_the_term_literally) + method,
            ("exact", "A.b*"), ("upper", "A.B*"), ("lower", "a.b*"), ("decoy", "aXbbb"), ("lf", "xA.b*\n"), ("lfstart", "a.b*\nz"),
            ("pre", "A.b*tail"), ("suf", "headA.B*"), ("mid", "headA.b*tail"));
        var term = "a.B*";

        switch (method)
        {
            case "startsWith":
                AssertBothModes(collection, x => x.S.StartsWith(term, StringComparison.OrdinalIgnoreCase),
                    "exact", "upper", "lower", "pre", "lfstart");
                AssertBothModes(collection, x => x.S.StartsWith(term, StringComparison.OrdinalIgnoreCase) || x.Label == "decoy",
                    "exact", "upper", "lower", "pre", "lfstart", "decoy");
                break;
            case "endsWith":
                AssertBothModes(collection, x => x.S.EndsWith(term, StringComparison.OrdinalIgnoreCase),
                    "exact", "upper", "lower", "suf");
                AssertBothModes(collection, x => x.S.EndsWith(term, StringComparison.OrdinalIgnoreCase) || x.Label == "decoy",
                    "exact", "upper", "lower", "suf", "decoy");
                break;
            default:
                AssertBothModes(collection, x => x.S.Contains(term, StringComparison.OrdinalIgnoreCase),
                    "exact", "upper", "lower", "lf", "lfstart", "pre", "suf", "mid");
                AssertBothModes(collection, x => x.S.Contains(term, StringComparison.OrdinalIgnoreCase) || x.Label == "decoy",
                    "exact", "upper", "lower", "lf", "lfstart", "pre", "suf", "mid", "decoy");
                break;
        }

        // NativeOnly refuses a non-entity projection, so Native it is (these go through the driver bridge).
        {
            using var context = CreateContext(collection, MongoQueryProvider.Native);
            var projected = context.Entities.AsNoTracking().Where(x => x.Label == "lf" || x.Label == "exact").OrderBy(x => x.Label)
                .Select(x => x.S.EndsWith(term, StringComparison.OrdinalIgnoreCase)).ToList();
            Assert.Equal([true, false], projected); // "exact" ends with the term; "lf" has a trailing newline
        }
    }

    private static SingleEntityDbContext<Row> CreateContext(IMongoCollection<Row> collection, MongoQueryProvider provider)
        => SingleEntityDbContext.Create(
            collection,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryProvider(provider);
            });

#if !EF8
    private static string[] Labels(IMongoCollection<Row> collection)
        => collection.Find(FilterDefinition<Row>.Empty).ToList().Select(x => x.Label).OrderBy(x => x, StringComparer.Ordinal).ToArray();
#endif

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
