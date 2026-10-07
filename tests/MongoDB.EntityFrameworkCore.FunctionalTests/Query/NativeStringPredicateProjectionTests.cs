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
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// Bare boolean/integer projections of string predicates (<c>Select(x =&gt; x.Text.Contains("a"))</c>) run
/// natively and agree with LINQ-to-objects, including for a document whose field is missing.
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeStringPredicateProjectionTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    public class Row
    {
        public ObjectId Id { get; set; }
        public int Order { get; set; }
        public string? Text { get; set; }
        public int? Rank { get; set; }
    }

    [Fact]
    public void Contains_and_negated_contains_projections_match_oracle()
    {
        var (context, rows) = Seed(nameof(Contains_and_negated_contains_projections_match_oracle));
        using (context)
        {
            Assert.Equal(rows.Select(r => r.Text != null && r.Text.Contains("eat")),
                context.Entities.OrderBy(x => x.Order).Select(x => x.Text!.Contains("eat")).ToList());
            Assert.Equal(rows.Select(r => !(r.Text != null && r.Text.Contains("eat"))),
                context.Entities.OrderBy(x => x.Order).Select(x => !x.Text!.Contains("eat")).ToList());
        }
    }

    [Fact]
    public void Parameterized_starts_with_projection_matches_oracle()
    {
        var (context, rows) = Seed(nameof(Parameterized_starts_with_projection_matches_oracle));
        using (context)
        {
            // A regex placeholder inside $project, substituted per execution; "." must match literally.
            foreach (var term in new[] { "Sea", ".", "éc" })
            {
                Assert.Equal(rows.Select(r => r.Text != null && r.Text.StartsWith(term)),
                    context.Entities.OrderBy(x => x.Order).Select(x => x.Text!.StartsWith(term)).ToList());
            }
        }
    }

    [Fact]
    public void Case_insensitive_contains_projection_folds_non_ascii()
    {
        var (context, _) = Seed(nameof(Case_insensitive_contains_projection_folds_non_ascii));
        using (context)
        {
            var result = context.Entities.OrderBy(x => x.Order)
                .Select(x => x.Text!.Contains("ÉCO", StringComparison.OrdinalIgnoreCase)).ToList();
            Assert.Equal([false, true, false, false], result);
        }
    }

    [Fact]
    public void IsNullOrEmpty_projection_treats_missing_as_null()
    {
        var (context, _) = Seed(nameof(IsNullOrEmpty_projection_treats_missing_as_null));
        using (context)
        {
            var result = context.Entities.OrderBy(x => x.Order).Select(x => string.IsNullOrEmpty(x.Text)).ToList();
            Assert.Equal([false, false, true, true], result);

            var negated = context.Entities.OrderBy(x => x.Order).Select(x => !string.IsNullOrEmpty(x.Text)).ToList();
            Assert.Equal([true, true, false, false], negated);
        }
    }

    [Fact]
    public void Null_comparison_projection_treats_missing_as_null()
    {
        var (context, _) = Seed(nameof(Null_comparison_projection_treats_missing_as_null));
        using (context)
        {
            Assert.Equal([false, false, false, true],
                context.Entities.OrderBy(x => x.Order).Select(x => x.Text == null).ToList());
            Assert.Equal([true, true, true, false],
                context.Entities.OrderBy(x => x.Order).Select(x => x.Text != null).ToList());
        }
    }

    [Fact]
    public void Null_parameter_comparison_projection_treats_missing_as_null()
    {
        var (context, _) = Seed(nameof(Null_parameter_comparison_projection_treats_missing_as_null));
        using (context)
        {
            string? text = null;
            Assert.Equal([false, false, false, true],
                context.Entities.OrderBy(x => x.Order).Select(x => x.Text == text).ToList());
            text = "école";
            Assert.Equal([false, true, false, false],
                context.Entities.OrderBy(x => x.Order).Select(x => x.Text == text).ToList());
        }
    }

    [Fact]
    public void Lifted_relational_comparison_projection_declines()
    {
        // .NET answers false for null < 5, but $lt orders null/missing below every number.
        var (context, _) = Seed(nameof(Lifted_relational_comparison_projection_declines));
        using (context)
        {
            Assert.Throws<NativeTranslationNotSupportedException>(
                () => context.Entities.Select(x => x.Rank < 5).ToList());
        }
    }

    [Fact]
    public void Wrapped_predicate_members_are_read_whole()
    {
        var (context, rows) = Seed(nameof(Wrapped_predicate_members_are_read_whole));
        using (context)
        {
            var result = context.Entities.OrderBy(x => x.Order)
                .Select(x => new { C = x.Text!.Contains("eat"), N = !x.Text!.Contains("eat"), E = string.IsNullOrEmpty(x.Text) })
                .ToList();
            Assert.Equal(rows.Select(r => (r.Text != null && r.Text.Contains("eat"), !(r.Text != null && r.Text.Contains("eat")), string.IsNullOrEmpty(r.Text))),
                result.Select(r => (r.C, r.N, r.E)));
        }
    }

    [Fact]
    public void Non_idempotent_math_projection_is_applied_once()
    {
        var (context, _) = Seed(nameof(Non_idempotent_math_projection_is_applied_once));
        using (context)
        {
            Assert.Equal([0.0, 1.0, 4.0, 9.0],
                context.Entities.OrderBy(x => x.Order).Select(x => Math.Pow(x.Order, 2)).ToList());
        }
    }

    [Fact]
    public void Length_projection_goes_native()
    {
        var (context, _) = Seed(nameof(Length_projection_goes_native));
        using (context)
        {
            // (int?): a non-nullable Length over a possibly-null string declines (see NativeStringNullPropagationTests).
            Assert.Equal([7, 5], context.Entities.Where(x => x.Order < 2).OrderBy(x => x.Order)
                .Select(x => (int?)x.Text!.Length).ToList());
        }
    }

    [Fact]
    public void Substring_projection_goes_native()
    {
        var (context, rows) = Seed(nameof(Substring_projection_goes_native));
        using (context)
        {
            Assert.Equal(rows.Where(r => r.Order < 2).Select(r => r.Text!.Substring(1, 2)),
                context.Entities.Where(x => x.Order < 2).OrderBy(x => x.Order)
                    .Select(x => x.Text!.Substring(1, 2)).ToList());
        }
    }

    // The synthesized length ($strLenCP of the source) is a server error over a null/missing field, which would
    // abort the whole query rather than just not match that row. (A start past the end of the measured string is
    // still a server error, as .NET throws for it.)
    [Fact]
    public void One_arg_Substring_over_a_null_or_missing_field_does_not_abort_the_query()
    {
        var (context, rows) = Seed(nameof(One_arg_Substring_over_a_null_or_missing_field_does_not_abort_the_query));
        using (context)
        {
            Assert.Equal(rows.Where(r => r.Text != null && r.Text.Substring(0) == "Seattle").Select(r => r.Order),
                context.Entities.Where(x => x.Text!.Substring(0) == "Seattle").Select(x => x.Order).ToList());
        }
    }

    [Fact]
    public void Replace_projection_goes_native()
    {
        var (context, rows) = Seed(nameof(Replace_projection_goes_native));
        using (context)
        {
            Assert.Equal(rows.Where(r => r.Order < 3).Select(r => r.Text!.Replace("a", "b")),
                context.Entities.Where(x => x.Order < 3).OrderBy(x => x.Order)
                    .Select(x => x.Text!.Replace("a", "b")).ToList());
        }
    }

    [Fact]
    public void IndexOf_projection_goes_native()
    {
        var (context, rows) = Seed(nameof(IndexOf_projection_goes_native));
        using (context)
        {
            Assert.Equal(rows.Where(r => r.Order < 3).Select(r => (int?)r.Text!.IndexOf('e')),
                context.Entities.Where(x => x.Order < 3).OrderBy(x => x.Order)
                    .Select(x => (int?)x.Text!.IndexOf('e')).ToList());
        }
    }

    // The binder stages native leaves in every query mode (including DriverLinq), and a late decline can hand the
    // native projection to the mixed shaper, so each leaf's client-side form must stay correct off the native
    // alias reader: whole-entity mixed projections, driver-LINQ push-down, and Enumerable-over-string analysis.
    public static TheoryData<string> AllModes
        => [nameof(MongoQueryMode.DriverLinq), nameof(MongoQueryMode.Native), nameof(MongoQueryMode.NativeOnly)];

    [Theory]
    [MemberData(nameof(AllModes))]
    public void Mixed_whole_entity_projection_with_a_Substring_member_is_correct_in_every_mode(string mode)
    {
        var (context, rows) = Seed(nameof(Mixed_whole_entity_projection_with_a_Substring_member_is_correct_in_every_mode) + mode, mode);
        using (context)
        {
            var result = context.Entities.Where(x => x.Order < 2).OrderBy(x => x.Order)
                .Select(x => new { x, S = x.Text!.Substring(1, 2) }).ToList();
            Assert.Equal(rows.Where(r => r.Order < 2).Select(r => (r.Order, r.Text!.Substring(1, 2))),
                result.Select(r => (r.x.Order, r.S)));
        }
    }

    [Theory]
    [MemberData(nameof(AllModes))]
    public void Mixed_whole_entity_projection_with_a_predicate_member_is_correct_in_every_mode(string mode)
    {
        var (context, rows) = Seed(nameof(Mixed_whole_entity_projection_with_a_predicate_member_is_correct_in_every_mode) + mode, mode);
        using (context)
        {
            var result = context.Entities.Where(x => x.Order < 3).OrderBy(x => x.Order)
                .Select(x => new { x, B = x.Text!.Contains("eat"), N = !x.Text!.Contains("eat") }).ToList();
            Assert.Equal(rows.Where(r => r.Order < 3).Select(r => (r.Order, r.Text!.Contains("eat"), !r.Text!.Contains("eat"))),
                result.Select(r => (r.x.Order, r.B, r.N)));
        }
    }

    [Theory]
    [MemberData(nameof(AllModes))]
    public void Bare_string_FirstOrDefault_is_correct_in_every_mode(string mode)
    {
        var (context, rows) = Seed(nameof(Bare_string_FirstOrDefault_is_correct_in_every_mode) + mode, mode);
        using (context)
        {
            Assert.Equal(rows.Where(r => r.Order < 3).Select(r => r.Text!.FirstOrDefault()),
                context.Entities.Where(x => x.Order < 3).OrderBy(x => x.Order).Select(x => x.Text!.FirstOrDefault()).ToList());
        }
    }

    [Theory]
    [MemberData(nameof(AllModes))]
    public void Bare_and_wrapped_computed_leaves_are_correct_in_every_mode(string mode)
    {
        var (context, rows) = Seed(nameof(Bare_and_wrapped_computed_leaves_are_correct_in_every_mode) + mode, mode);
        using (context)
        {
            Assert.Equal(rows.Where(r => r.Order < 2).Select(r => r.Text!.Substring(1, 2)),
                context.Entities.Where(x => x.Order < 2).OrderBy(x => x.Order).Select(x => x.Text!.Substring(1, 2)).ToList());
            Assert.Equal(rows.Select(r => !(r.Text != null && r.Text.Contains("eat"))),
                context.Entities.OrderBy(x => x.Order).Select(x => !x.Text!.Contains("eat")).ToList());
            Assert.Equal(rows.Where(r => r.Order < 2).Select(r => (r.Text!.Substring(1, 2), r.Text!.Contains("eat"))),
                context.Entities.Where(x => x.Order < 2).OrderBy(x => x.Order)
                    .Select(x => new { S = x.Text!.Substring(1, 2), C = x.Text!.Contains("eat") }).ToList()
                    .Select(r => (r.S, r.C)));
        }
    }

    // The outer and nested members share the name "B" and type string; neither may be mistaken for the other.
    // (A nested anonymous type isn't native today, so NativeOnly isn't exercised; the structural match itself is
    // pinned by SlotPopulationTests.Only_the_exact_staged_subtree_is_wrapped.)
    [Theory]
    [InlineData(nameof(MongoQueryMode.DriverLinq))]
    [InlineData(nameof(MongoQueryMode.Native))]
    public void Nested_members_sharing_a_name_each_keep_their_own_computation(string mode)
    {
        var (context, rows) = Seed(nameof(Nested_members_sharing_a_name_each_keep_their_own_computation) + mode, mode);
        using (context)
        {
            var result = context.Entities.Where(x => x.Order < 2).OrderBy(x => x.Order)
                .Select(x => new { B = x.Text!.Replace("e", "E"), A = new { B = x.Text!.Substring(1) } }).ToList();
            Assert.Equal(rows.Where(r => r.Order < 2).Select(r => (r.Text!.Replace("e", "E"), r.Text!.Substring(1))),
                result.Select(r => (r.B, r.A.B)));
        }
    }

    // Order 0: "Seattle", 1: "école", 2: "" , 3: field missing (inserted as a raw BsonDocument).
    private (SingleEntityDbContext<Row>, Row[]) Seed(string testName, string mode = nameof(MongoQueryMode.NativeOnly))
    {
        var name = TemporaryDatabaseFixtureBase.CreateCollectionName(testName) + Guid.NewGuid().ToString("N")[..8];
        var collection = database.MongoDatabase.GetCollection<Row>(name);
        var rows = new[] { new Row { Order = 0, Text = "Seattle" }, new Row { Order = 1, Text = "école" }, new Row { Order = 2, Text = "" } };
        collection.InsertMany(rows);
        database.MongoDatabase.GetCollection<BsonDocument>(name)
            .InsertOne(new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Order", 3 } });

        var context = SingleEntityDbContext.Create(
            collection,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(Enum.Parse<MongoQueryMode>(mode));
            });
        return (context, [.. rows, new Row { Order = 3, Text = null }]);
    }
}
