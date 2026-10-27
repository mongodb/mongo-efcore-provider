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
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Infrastructure;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// <c>entities.Contains(e)</c> over a composite primary key renders a per-entity conjunction of key paths under
/// <c>$or</c>/<c>$nor</c> (<c>MongoCompositeKeyInExpression</c>), built per execution so one compiled plan serves lists
/// of any length. Order-agnostic: it never relies on the stored key's field order.
/// </summary>
public class NativeCompositeKeyContainsTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    public enum Tier { Low = 1, High = 2 }

    public class Doc
    {
        public string Key1 { get; set; } = "";
        public int Key2 { get; set; }
        public string Label { get; set; } = "";
    }

    public class WideDoc
    {
        public Guid Key1 { get; set; }
        public Tier Key2 { get; set; }
        public string Label { get; set; } = "";
    }

    private static readonly Doc One = new() { Key1 = "one", Key2 = 1 };
    private static readonly Doc Two2 = new() { Key1 = "two", Key2 = 2 };
    private static readonly Doc Two3 = new() { Key1 = "two", Key2 = 3 };

    [Theory]
    [InlineData("array", new[] { "one", "two3" })]
    [InlineData("list", new[] { "one", "two3" })]
    [InlineData("inline", new[] { "one" })]
    [InlineData("empty", new string[0])]
    [InlineData("not_empty", new[] { "one", "two2", "two3" })]
    [InlineData("not_non_empty", new[] { "two2" })]
    [InlineData("with_null_element", new[] { "one" })]
    [InlineData("with_duplicates", new[] { "one" })]
    [InlineData("and_other_predicate", new[] { "two3" })]
    public void Composite_key_Contains_goes_native_and_matches(string shape, string[] expected)
    {
        var collection = Seed();
        NativeModeAssert.NativeAndExpected(
            mode =>
            {
                using var db = Create(collection, mode);
                var asList = new List<Doc> { One, Two3 };
                var q = db.Entities.AsNoTracking();
                q = shape switch
                {
                    "array" => q.Where(e => new[] { One, Two3 }.Contains(e)),
                    "list" => q.Where(e => asList.Contains(e)),
                    "inline" => q.Where(e => new[] { One }.Contains(e)),
                    "empty" => q.Where(e => new Doc[0].Contains(e)),
                    "not_empty" => q.Where(e => !new Doc[0].Contains(e)),
                    "not_non_empty" => q.Where(e => !asList.Contains(e)),
                    "with_null_element" => q.Where(e => new Doc?[] { One, null }.Contains(e)),
                    "with_duplicates" => q.Where(e => new[] { One, One }.Contains(e)),
                    _ => q.Where(e => asList.Contains(e) && e.Key2 > 1)
                };
                return q.OrderBy(e => e.Label).Select(e => e.Label).ToList();
            },
            [.. expected]);
    }

    [Fact]
    public void One_compiled_plan_serves_lists_of_different_lengths()
    {
        var collection = Seed();
        foreach (var mode in new[] { MongoQueryMode.NativeOnly, MongoQueryMode.Native, MongoQueryMode.DriverLinq })
        {
            // ONE context: both runs must hit the same compiled query for the clause array to be built per execution.
            using var db = Create(collection, mode);
            var wanted = new List<Doc> { One, Two2, Two3 };

            List<string> Run() => db.Entities.AsNoTracking().Where(e => wanted.Contains(e)).OrderBy(e => e.Label)
                .Select(e => e.Label).ToList();

            NativeModeAssert.TwiceWithDifferentValues(
                _ => Run(),
                null, ["one", "two2", "two3"],
                null, ["one", "two2", "two3"]);

            wanted = new List<Doc> { Two2 };
            Assert.Equal(["two2"], Run());

            wanted = [];
            Assert.Empty(Run());
        }
    }

    [Fact]
    public void Guid_and_enum_key_components_are_serialized_through_their_key_serializers()
    {
        var name = TemporaryDatabaseFixtureBase.CreateCollectionName("widekeys") + Guid.NewGuid().ToString("N")[..8];
        var typed = database.MongoDatabase.GetCollection<WideDoc>(name);
        var g1 = Guid.NewGuid();
        var g2 = Guid.NewGuid();
        using (var seed = CreateWide(typed, MongoQueryMode.DriverLinq))
        {
            seed.Entities.AddRange(
                new WideDoc { Key1 = g1, Key2 = Tier.Low, Label = "g1-low" },
                new WideDoc { Key1 = g1, Key2 = Tier.High, Label = "g1-high" },
                new WideDoc { Key1 = g2, Key2 = Tier.Low, Label = "g2-low" });
            seed.SaveChanges();
        }

        NativeModeAssert.NativeAndExpected(
            mode =>
            {
                using var db = CreateWide(typed, mode);
                var wanted = new List<WideDoc>
                {
                    new() { Key1 = g1, Key2 = Tier.High }, new() { Key1 = g2, Key2 = Tier.Low }
                };
                return db.Entities.AsNoTracking().Where(e => wanted.Contains(e)).OrderBy(e => e.Label)
                    .Select(e => e.Label).ToList();
            },
            ["g1-high", "g2-low"]);
    }

    private static SingleEntityDbContext<Doc> Create(IMongoCollection<Doc> collection, MongoQueryMode mode)
        => SingleEntityDbContext.Create(
            collection,
            mb => mb.Entity<Doc>().HasKey(e => new { e.Key1, e.Key2 }),
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });

    private static SingleEntityDbContext<WideDoc> CreateWide(IMongoCollection<WideDoc> collection, MongoQueryMode mode)
        => SingleEntityDbContext.Create(
            collection,
            mb => mb.Entity<WideDoc>().HasKey(e => new { e.Key1, e.Key2 }),
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });

    private IMongoCollection<Doc> Seed()
    {
        var name = TemporaryDatabaseFixtureBase.CreateCollectionName("compkeys") + Guid.NewGuid().ToString("N")[..8];
        var typed = database.MongoDatabase.GetCollection<Doc>(name);
        using var db = Create(typed, MongoQueryMode.DriverLinq);
        db.Entities.AddRange(
            new Doc { Key1 = "one", Key2 = 1, Label = "one" },
            new Doc { Key1 = "two", Key2 = 2, Label = "two2" },
            new Doc { Key1 = "two", Key2 = 3, Label = "two3" });
        db.SaveChanges();
        return typed;
    }
}
