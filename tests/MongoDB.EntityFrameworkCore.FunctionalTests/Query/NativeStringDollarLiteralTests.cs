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
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Infrastructure;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// A <c>"$"</c>-prefixed string constant or parameter rendered in the aggregation dialect is <c>$literal</c>-wrapped,
/// so it is compared as a value rather than read as a field path (reachable with user input: a search for
/// <c>"$Text"</c> would otherwise compare against the <c>Text</c> field itself).
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeStringDollarLiteralTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    public class Row
    {
        public ObjectId Id { get; set; }
        public int Order { get; set; }
        public string Label { get; set; } = "";
        public string Text { get; set; } = "";
    }

    private static readonly Row[] Rows =
    [
        new() { Order = 0, Label = "dollar-text", Text = "$Text" },
        new() { Order = 1, Label = "dollar-order", Text = "$Order" },
        new() { Order = 2, Label = "plain", Text = "abc" },
        new() { Order = 3, Label = "or", Text = "Or" },
        new() { Order = 4, Label = "dollar-edges", Text = "$ab$" }
    ];

    [Fact]
    public void Computed_comparison_against_dollar_prefixed_constant_is_literal()
    {
        using var context = CreateContext(nameof(Computed_comparison_against_dollar_prefixed_constant_is_literal));

        Assert.Equal(["dollar-text"],
            context.Entities.Where(x => x.Text.Replace("zz", "yy") == "$Text").Select(x => x.Label).ToList());
        Assert.Equal(Rows.Where(r => r.Text != "$Text").Select(r => r.Label),
            context.Entities.Where(x => x.Text.Replace("zz", "yy") != "$Text").OrderBy(x => x.Order)
                .Select(x => x.Label).ToList());
        Assert.Equal(Rows.Where(r => string.CompareOrdinal(r.Text, "$Order") > 0).Select(r => r.Label),
            context.Entities.Where(x => x.Text.Replace("zz", "yy").CompareTo("$Order") > 0).OrderBy(x => x.Order)
                .Select(x => x.Label).ToList());
    }

    [Fact]
    public void Computed_comparison_against_dollar_prefixed_parameter_is_literal()
    {
        using var context = CreateContext(nameof(Computed_comparison_against_dollar_prefixed_parameter_is_literal));

        var term = "$Text";
        Assert.Equal(["dollar-text"],
            context.Entities.Where(x => x.Text.Replace("zz", "yy") == term).Select(x => x.Label).ToList());
        term = "$Order";
        Assert.Equal(["dollar-order"],
            context.Entities.Where(x => x.Text.Replace("zz", "yy") == term).Select(x => x.Label).ToList());
    }

    [Fact]
    public void Computed_in_against_dollar_prefixed_values_is_literal()
    {
        using var context = CreateContext(nameof(Computed_in_against_dollar_prefixed_values_is_literal));

        var terms = new[] { "$Text", "$Label" };
        Assert.Equal(["dollar-text"],
            context.Entities.Where(x => terms.Contains(x.Text.Replace("zz", "yy"))).Select(x => x.Label).ToList());
    }

    [Fact]
    public void Concat_with_dollar_prefixed_constant_and_parameter_is_literal()
    {
        using var context = CreateContext(nameof(Concat_with_dollar_prefixed_constant_and_parameter_is_literal));

        Assert.Equal(Rows.Select(r => r.Text + "$Order"),
            context.Entities.OrderBy(x => x.Order).Select(x => x.Text + "$Order").ToList());

        var suffix = "$Label";
        Assert.Equal(Rows.Select(r => r.Text + suffix),
            context.Entities.OrderBy(x => x.Order).Select(x => x.Text + suffix).ToList());
    }

    [Fact]
    public void IndexOf_over_dollar_prefixed_constant_haystack_is_literal()
    {
        using var context = CreateContext(nameof(IndexOf_over_dollar_prefixed_constant_haystack_is_literal));

        // (int?): a non-nullable IndexOf over a possibly-null needle declines (see NativeStringNullPropagationTests).
        Assert.Equal(Rows.Select(r => (int?)"$Order".IndexOf(r.Text, StringComparison.Ordinal)),
            context.Entities.OrderBy(x => x.Order).Select(x => (int?)"$Order".IndexOf(x.Text)).ToList());
    }

    [Fact]
    public void Trim_of_dollar_constant_chars_is_literal()
    {
        using var context = CreateContext(nameof(Trim_of_dollar_constant_chars_is_literal));

        Assert.Equal(Rows.Select(r => r.Text.Trim('$')),
            context.Entities.OrderBy(x => x.Order).Select(x => x.Text.Trim('$')).ToList());

        // Parameterized chars aren't native (no placeholder path for "chars"), so only constant forms.
        Assert.Equal(Rows.Select(r => r.Text.TrimEnd('$', 'b')),
            context.Entities.OrderBy(x => x.Order).Select(x => x.Text.TrimEnd('$', 'b')).ToList());
    }

    private SingleEntityDbContext<Row> CreateContext(string testName)
    {
        var collection = database.MongoDatabase.GetCollection<Row>(
            TemporaryDatabaseFixtureBase.CreateCollectionName(testName) + Guid.NewGuid().ToString("N")[..8]);
        collection.InsertMany(Rows.Select(r => new Row { Order = r.Order, Label = r.Label, Text = r.Text }));

        return SingleEntityDbContext.Create(
            collection,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(MongoQueryMode.NativeOnly);
            });
    }
}
