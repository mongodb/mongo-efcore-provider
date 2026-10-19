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
using System.Collections.Generic;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Infrastructure;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// <c>ToLower</c>/<c>ToUpper</c>: server <c>$toLower</c>/<c>$toUpper</c> are ASCII-only, so a projection re-applies
/// the .NET call client-side over the (correctly computed) receiver in every query mode, and <c>Where</c> uses an
/// exact case-insensitive regex. Neither ever emits <c>$toLower</c>/<c>$toUpper</c>.
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeStringCaseMappingTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    public class Row
    {
        public ObjectId Id { get; set; }
        public int Order { get; set; }
        public string? Text { get; set; }
    }

    public static TheoryData<string> AllModes
        => [nameof(MongoQueryMode.DriverLinq), nameof(MongoQueryMode.Native), nameof(MongoQueryMode.NativeOnly)];

    [Theory]
    [MemberData(nameof(AllModes))]
    public void Bare_ToUpper_projection_is_applied_client_side(string mode)
    {
        var (context, rows, logs) = Seed(nameof(Bare_ToUpper_projection_is_applied_client_side) + mode, mode);
        using (context)
        {
            Assert.Equal(rows.Select(r => r.Text?.ToUpper()),
                context.Entities.OrderBy(x => x.Order).Select(x => x.Text!.ToUpper()).ToList());
            AssertNoServerCaseMapping(logs);
        }
    }

    [Theory]
    [MemberData(nameof(AllModes))]
    public void Invariant_projections_are_applied_client_side(string mode)
    {
        var (context, rows, logs) = Seed(nameof(Invariant_projections_are_applied_client_side) + mode, mode);
        using (context)
        {
            Assert.Equal(rows.Select(r => r.Text?.ToLowerInvariant()),
                context.Entities.OrderBy(x => x.Order).Select(x => x.Text!.ToLowerInvariant()).ToList());
            Assert.Equal(rows.Select(r => r.Text?.ToUpperInvariant()),
                context.Entities.OrderBy(x => x.Order).Select(x => x.Text!.ToUpperInvariant()).ToList());
            AssertNoServerCaseMapping(logs);
        }
    }

    [Theory]
    [MemberData(nameof(AllModes))]
    public void ToUpper_over_Trim_is_applied_client_side_over_the_trimmed_value(string mode)
    {
        var (context, rows, logs) = Seed(nameof(ToUpper_over_Trim_is_applied_client_side_over_the_trimmed_value) + mode, mode);
        using (context)
        {
            Assert.Equal(rows.Select(r => r.Text?.Trim().ToUpper()),
                context.Entities.OrderBy(x => x.Order).Select(x => x.Text!.Trim().ToUpper()).ToList());
            AssertNoServerCaseMapping(logs);
        }
    }

    [Theory]
    [MemberData(nameof(AllModes))]
    public void Wrapped_ToLower_members_are_applied_client_side(string mode)
    {
        var (context, rows, logs) = Seed(nameof(Wrapped_ToLower_members_are_applied_client_side) + mode, mode);
        using (context)
        {
            var result = context.Entities.OrderBy(x => x.Order)
                .Select(x => new { x.Text, L = x.Text!.ToLower(), U = x.Text!.Trim().ToUpper() }).ToList();
            Assert.Equal(rows.Select(r => (r.Text, r.Text?.ToLower(), r.Text?.Trim().ToUpper())),
                result.Select(r => (r.Text, (string?)r.L, (string?)r.U)));
            AssertNoServerCaseMapping(logs);
        }
    }

    [Theory]
    [MemberData(nameof(AllModes))]
    public void Mixed_whole_entity_projection_with_a_ToLower_member_is_applied_client_side(string mode)
    {
        var (context, rows, logs) = Seed(
            nameof(Mixed_whole_entity_projection_with_a_ToLower_member_is_applied_client_side) + mode, mode);
        using (context)
        {
            var result = context.Entities.OrderBy(x => x.Order).Select(x => new { x, U = x.Text!.ToLower() }).ToList();
            Assert.Equal(rows.Select(r => (r.Order, r.Text?.ToLower())), result.Select(r => (r.x.Order, (string?)r.U)));
            AssertNoServerCaseMapping(logs);
        }
    }

    [Fact]
    public void ToLower_equals_lowercase_constant_matches_non_ascii_case_insensitively()
    {
        var (context, _, logs) = Seed(nameof(ToLower_equals_lowercase_constant_matches_non_ascii_case_insensitively));
        using (context)
        {
            Assert.Equal([3], context.Entities.Where(x => x.Text!.ToLower() == "école").Select(x => x.Order).ToList());
            Assert.Equal([0, 1, 2], context.Entities.Where(x => x.Text!.ToLower() != "école").OrderBy(x => x.Order)
                .Select(x => x.Order).ToList());
            Assert.Equal([3], context.Entities.Where(x => "ÉCOLE" == x.Text!.ToUpperInvariant()).Select(x => x.Order).ToList());
            AssertNoServerCaseMapping(logs);
        }
    }

    [Fact]
    public void ToUpper_equals_constant_not_in_upper_case_matches_nothing()
    {
        var (context, _, logs) = Seed(nameof(ToUpper_equals_constant_not_in_upper_case_matches_nothing));
        using (context)
        {
            Assert.Empty(context.Entities.Where(x => x.Text!.ToUpper() == "Seattle").ToList());
            Assert.Equal(4, context.Entities.Where(x => x.Text!.ToUpper() != "Seattle").Count());
            AssertNoServerCaseMapping(logs);
        }
    }

    // A projection that folds at translate time ($literal false/true) must read the folded value, not the receiver.
    [Theory]
    [MemberData(nameof(AllModes))]
    public void Folded_case_mapping_comparison_projections_read_the_folded_value(string mode)
    {
        var (context, rows, _) = Seed(nameof(Folded_case_mapping_comparison_projections_read_the_folded_value) + mode, mode);
        using (context)
        {
            Assert.Equal(rows.Select(r => r.Text?.ToLower() == "Seattle"),
                context.Entities.OrderBy(x => x.Order).Select(x => x.Text!.ToLower() == "Seattle").ToList());
            Assert.Equal(rows.Select(r => r.Text?.ToLower() != "Seattle"),
                context.Entities.OrderBy(x => x.Order).Select(x => x.Text!.ToLower() != "Seattle").ToList());
            Assert.Equal(rows.Select(r => (r.Order, r.Text?.ToUpper() == "Seattle")),
                context.Entities.OrderBy(x => x.Order).Select(x => new { x.Order, C = x.Text!.ToUpper() == "Seattle" })
                    .ToList().Select(r => (r.Order, r.C)));
        }
    }

    // The native projection holds the raw receiver, so operators that read projected values decline (driver-LINQ
    // fallback, and a throw under NativeOnly). ASCII case variants only: the fallback's $toUpper/$toLower is
    // ASCII-only, and null rows are excluded because it maps null to "".
    [Theory]
    [MemberData(nameof(AllModes))]
    public void Value_reading_operators_over_a_projected_case_mapping_decline(string mode)
    {
        var (context, rows) = SeedCaseVariants(nameof(Value_reading_operators_over_a_projected_case_mapping_decline) + mode, mode);
        using (context)
        {
            var source = rows.Where(r => r.Text != null).ToList();
            var queries = new (object Expected, Func<object> Actual)[]
            {
                (source.Select(r => r.Text!.ToUpper()).Distinct().OrderBy(s => s, StringComparer.Ordinal).ToList(),
                    () => context.Entities.Where(x => x.Text != null).Select(x => x.Text!.ToUpper()).Distinct().ToList()
                        .OrderBy(s => s, StringComparer.Ordinal).ToList()),
                (source.Select(r => r.Text!.ToUpper()).Distinct().Count(),
                    () => context.Entities.Where(x => x.Text != null).Select(x => x.Text!.ToUpper()).Distinct().Count()),
                (source.Select(r => r.Text!.ToLower()).Distinct().OrderBy(s => s, StringComparer.Ordinal).ToList(),
                    () => context.Entities.Where(x => x.Text != null).Select(x => new { U = x.Text!.ToLower() }).Distinct()
                        .ToList().Select(r => r.U).OrderBy(s => s, StringComparer.Ordinal).ToList()),
                (source.Select(r => r.Text!.ToLower()).OrderBy(s => s, StringComparer.Ordinal).ToList(),
                    () => context.Entities.Where(x => x.Text != null).Select(x => x.Text!.ToLower()).OrderBy(s => s).ToList()),
                (source.Select(r => r.Text!.ToLower()).Max(StringComparer.Ordinal)!,
                    () => context.Entities.Where(x => x.Text != null).Select(x => x.Text!.ToLower()).Max()!),
            };

            for (var i = 0; i < queries.Length; i++)
            {
                var (expected, actual) = queries[i];
                if (mode == nameof(MongoQueryMode.NativeOnly))
                {
                    var thrown = Record.Exception(actual);
                    Assert.True(thrown is NativeTranslationNotSupportedException, $"Query {i}: {thrown?.GetType().Name ?? "no exception"}");
                }
                else
                {
                    Assert.Equal(expected, actual());
                }
            }
        }
    }

    // Paging and predicate-free cardinality don't read the projected values, so they stay native.
    [Theory]
    [MemberData(nameof(AllModes))]
    public void Value_free_operators_over_a_projected_case_mapping_stay_native(string mode)
    {
        var (context, rows) = SeedCaseVariants(nameof(Value_free_operators_over_a_projected_case_mapping_stay_native) + mode, mode);
        using (context)
        {
            Assert.Equal(rows.OrderBy(r => r.Order).Select(r => r.Text?.ToUpper()).Take(3),
                context.Entities.OrderBy(x => x.Order).Select(x => x.Text!.ToUpper()).Take(3).ToList());
            Assert.Equal(rows.OrderBy(r => r.Order).Select(r => r.Text?.ToLower()).Skip(1).First(),
                context.Entities.OrderBy(x => x.Order).Select(x => x.Text!.ToLower()).Skip(1).First());
            Assert.Equal(rows.Length, context.Entities.Select(x => x.Text!.ToLower()).Count());

            // EF moves a Where over the projected member ahead of the Select, where it is the exact regex.
            Assert.Equal([0, 1, 2],
                context.Entities.Select(x => new { U = x.Text!.ToUpper(), x.Order }).Where(a => a.U == "SEATTLE")
                    .ToList().Select(a => a.Order).OrderBy(o => o));
        }
    }

    private (SingleEntityDbContext<Row>, Row[]) SeedCaseVariants(string testName, string mode)
    {
        var name = TemporaryDatabaseFixtureBase.CreateCollectionName(testName) + Guid.NewGuid().ToString("N")[..8];
        var collection = database.MongoDatabase.GetCollection<Row>(name);
        Row[] rows =
        [
            new() { Order = 0, Text = "Seattle" }, new() { Order = 1, Text = "seattle" }, new() { Order = 2, Text = "SEATTLE" },
            new() { Order = 3, Text = "London" }, new() { Order = 4, Text = "london" }, new() { Order = 5, Text = null }
        ];
        collection.InsertMany(rows);

        var context = SingleEntityDbContext.Create(
            collection,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(Enum.Parse<MongoQueryMode>(mode));
            });
        return (context, rows);
    }

    private static void AssertNoServerCaseMapping(List<string> logs)
    {
        var mql = logs.Where(l => l.Contains("Executed MQL query")).ToList();
        Assert.NotEmpty(mql);
        Assert.All(mql, m =>
        {
            Assert.DoesNotContain("$toLower", m);
            Assert.DoesNotContain("$toUpper", m);
        });
    }

    // Order 0: "Seattle", 1: " École ", 2: null, 3: "École".
    private (SingleEntityDbContext<Row>, Row[], List<string>) Seed(
        string testName, string mode = nameof(MongoQueryMode.NativeOnly))
    {
        var name = TemporaryDatabaseFixtureBase.CreateCollectionName(testName) + Guid.NewGuid().ToString("N")[..8];
        var collection = database.MongoDatabase.GetCollection<Row>(name);
        Row[] rows =
        [
            new() { Order = 0, Text = "Seattle" }, new() { Order = 1, Text = " École " }, new() { Order = 2, Text = null },
            new() { Order = 3, Text = "École" }
        ];
        collection.InsertMany(rows);

        List<string> logs = [];
        var context = SingleEntityDbContext.Create(
            collection,
            optionsBuilderAction: b =>
            {
                b.LogTo(logs.Add)
                    .EnableSensitiveDataLogging()
                    .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(Enum.Parse<MongoQueryMode>(mode));
            });
        return (context, rows, logs);
    }
}
