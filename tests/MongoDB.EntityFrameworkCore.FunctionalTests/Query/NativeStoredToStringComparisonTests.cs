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
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Infrastructure;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// <c>x.P.ToString() == value</c> over a property stored as the string its <c>ToString()</c> produces (EF's built-in
/// Guid/number/enum to-string converters) compares the stored string directly
/// (<c>MongoExpressionTranslator.TryTranslateStoredToStringComparison</c>).
/// </summary>
public class NativeStoredToStringComparisonTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    public enum Color { Red = 1, Green = 2 }

    public class Row
    {
        public int Id { get; set; }
        public Guid G { get; set; }
        public double D { get; set; }
        public Color E { get; set; }
        public Guid Plain { get; set; }
    }

    private static readonly Guid FirstGuid = Guid.Parse("380bb5de-fb71-4f6d-a349-2b83908ab43b");
    private static readonly Guid SecondGuid = Guid.Parse("6ec635e0-06e0-11ef-93e0-325096b39f47");

    [Theory]
    [InlineData("guid_eq", new[] { 1 })]
    [InlineData("guid_ne", new[] { 2 })]
    [InlineData("guid_mirrored", new[] { 1 })]
    [InlineData("double_eq", new[] { 2 })]
    [InlineData("enum_eq", new[] { 2 })]
    public void ToString_over_a_stored_string_goes_native(string shape, int[] expected)
    {
        var collection = Seed();
        NativeModeAssert.NativeAndExpected(
            mode =>
            {
                using var db = Create(collection, mode);
                var guid = FirstGuid.ToString();
                var number = "2.5";
                var color = "Green";
                var q = db.Entities.AsNoTracking();
                q = shape switch
                {
                    "guid_eq" => q.Where(x => x.G.ToString() == guid),
                    "guid_ne" => q.Where(x => x.G.ToString() != guid),
                    "guid_mirrored" => q.Where(x => guid == x.G.ToString()),
                    "double_eq" => q.Where(x => x.D.ToString() == number),
                    _ => q.Where(x => x.E.ToString() == color)
                };
                return q.OrderBy(x => x.Id).Select(x => x.Id).ToList();
            },
            [.. expected]);
    }

    [Fact]
    public void ToString_with_a_format_declines()
    {
        var collection = Seed();
        using var db = Create(collection, MongoQueryMode.NativeOnly);
        Assert.Throws<NativeTranslationNotSupportedException>(
            () => db.Entities.Where(x => x.G.ToString("N") == "380bb5defb714f6da3492b83908ab43b").Select(x => x.Id).ToList());
    }

    [Fact]
    public void ToString_over_a_default_stored_guid_is_not_the_stored_string_comparison()
    {
        var collection = Seed();
        using var db = Create(collection, MongoQueryMode.NativeOnly);
        var guid = FirstGuid.ToString();

        // The default Guid representation is binary: ToString() cannot be the stored value, so this must not match.
        Assert.ThrowsAny<Exception>(() => db.Entities.Where(x => x.Plain.ToString() == guid).Select(x => x.Id).ToList());
    }

    private static SingleEntityDbContext<Row> Create(IMongoCollection<Row> collection, MongoQueryMode mode)
        => SingleEntityDbContext.Create(
            collection,
            mb =>
            {
                mb.Entity<Row>().Property(e => e.G).HasConversion<string>();
                mb.Entity<Row>().Property(e => e.D).HasConversion<string>();
                mb.Entity<Row>().Property(e => e.E).HasConversion<string>();
            },
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });

    private IMongoCollection<Row> Seed()
    {
        var name = TemporaryDatabaseFixtureBase.CreateCollectionName("tostring") + Guid.NewGuid().ToString("N")[..8];
        var collection = database.MongoDatabase.GetCollection<Row>(name);
        using var db = Create(collection, MongoQueryMode.DriverLinq);
        db.Entities.AddRange(
            new Row { Id = 1, G = FirstGuid, D = 1.5, E = Color.Red, Plain = FirstGuid },
            new Row { Id = 2, G = SecondGuid, D = 2.5, E = Color.Green, Plain = SecondGuid });
        db.SaveChanges();
        return collection;
    }
}
