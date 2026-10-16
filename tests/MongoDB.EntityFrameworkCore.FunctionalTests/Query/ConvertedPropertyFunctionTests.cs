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
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Infrastructure;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// EF-465: a server-side string, member or arithmetic function computed over a value-converted or represented property
/// runs against the stored value, not the CLR value (<c>Name.Length</c> over a converter that prefixes the text, <c>Num % 2</c>
/// over a converter that doubles). Every such query throws; reading, projecting and equality-comparing the property itself
/// keeps working (the stored value goes through the property's serializer, which is injective).
/// </summary>
/// <remarks>
/// <c>Name</c> converts <c>v =&gt; "pfx-" + v</c> and <c>Num</c> converts <c>v =&gt; v * 2</c>; each case is run under
/// <see cref="MongoQueryMode.Native"/>, <see cref="MongoQueryMode.NativeOnly"/> and <see cref="MongoQueryMode.DriverLinq"/>
/// against an oracle computed in memory over the materialized entities.
/// </remarks>
[XUnitCollection("QueryTests")]
public class ConvertedPropertyFunctionTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    public class Item
    {
        public ObjectId Id { get; set; }
        public int Seq { get; set; }
        public string Name { get; set; } = "";
        public int Num { get; set; }
        public Color Color { get; set; }
        public int Wide { get; set; }
        public bool Flag { get; set; }
        public List<int> Scores { get; set; } = [];
    }

    public enum Color
    {
        Red = 1,
        Green = 2
    }

    private static readonly (int Seq, string Name, int Num)[] Rows = [(1, "abcdef", 1), (2, "ab", 2), (3, "abc", 3)];

    private static Color ColorOf(int seq) => seq == 2 ? Color.Green : Color.Red;

    // Computed from the converted property: refused.
    private static readonly Dictionary<string, Func<IQueryable<Item>, object>> RefusedQueries = new()
    {
        ["Where_Contains"] = q => q.Where(x => x.Name.Contains("x")).Select(x => x.Seq).ToList(),
        ["Where_StartsWith"] = q => q.Where(x => x.Name.StartsWith("ab")).OrderBy(x => x.Seq).Select(x => x.Seq).ToList(),
        ["Where_EndsWith"] = q => q.Where(x => x.Name.EndsWith("c")).OrderBy(x => x.Seq).Select(x => x.Seq).ToList(),
        ["Where_Length"] = q => q.Where(x => x.Name.Length == 2).Select(x => x.Seq).ToList(),
        ["Where_ToUpper"] = q => q.Where(x => x.Name.ToUpper() == "AB").Select(x => x.Seq).ToList(),
        ["Where_Substring"] = q => q.Where(x => x.Name.Substring(0, 2) == "ab").OrderBy(x => x.Seq).Select(x => x.Seq).ToList(),
        ["Where_IndexOf"] = q => q.Where(x => x.Name.IndexOf("a") == 0).OrderBy(x => x.Seq).Select(x => x.Seq).ToList(),
        ["Where_modulo"] = q => q.Where(x => x.Num % 2 == 1).OrderBy(x => x.Seq).Select(x => x.Seq).ToList(),
        ["Where_plus_equal"] = q => q.Where(x => x.Num + 1 == 3).Select(x => x.Seq).ToList(),
        ["Where_Math_Abs_equal"] = q => q.Where(x => Math.Abs(x.Num) == 2).Select(x => x.Seq).ToList(),
        // A converted bool tested by truthiness: the stored "True"/"False" are both truthy (EF-466).
        ["Where_conditional_truth"] = q => q.Where(x => x.Flag ? x.Seq > 1 : x.Seq > 2).OrderBy(x => x.Seq).Select(x => x.Seq).ToList(),
        ["Where_conditional_value"] = q => q.Where(x => (x.Flag ? 1 : 0) == 0).Select(x => x.Seq).ToList(),
        ["Count_conditional_value"] = q => q.Count(x => (x.Flag ? 1 : 0) == 1),
        // Two operands stored differently: a default-stored list against a converted property compares the stored forms (EF-467).
        ["Where_stored_list_Contains_converted"] = q => q.Where(x => x.Scores.Contains(x.Num)).Select(x => x.Seq).ToList(),
        ["Where_stored_list_Any_equal_converted"] = q => q.Where(x => x.Scores.Any(s => s == x.Num)).Select(x => x.Seq).ToList(),
        ["Select_Length"] = q => q.OrderBy(x => x.Seq).Select(x => x.Name.Length).ToList(),
        ["Select_plus"] = q => q.OrderBy(x => x.Seq).Select(x => x.Num + 1).ToList(),
        ["Select_concat"] = q => q.OrderBy(x => x.Seq).Select(x => x.Name + "!").ToList(),
        ["Select_anonymous_Length"] = q => q.OrderBy(x => x.Seq).Select(x => new { x.Seq, L = x.Name.Length }).ToList(),
        ["GroupBy_Length"] = q => q.GroupBy(x => x.Name.Length).Select(g => g.Key).OrderBy(k => k).ToList(),
        ["GroupBy_modulo_Count"] = q => q.GroupBy(x => x.Num % 2).Select(g => new { g.Key, N = g.Count() }).OrderBy(a => a.Key).ToList()
    };

    // Reading the property itself, or comparing it for equality, is exact whatever the storage.
    private static readonly Dictionary<string, Func<IQueryable<Item>, object>> AllowedQueries = new()
    {
        ["Where_equal"] = q => q.Where(x => x.Name == "ab").Select(x => x.Seq).ToList(),
        ["Where_Flag"] = q => q.Where(x => x.Flag).OrderBy(x => x.Seq).Select(x => x.Seq).ToList(),
        ["Where_not_Flag"] = q => q.Where(x => !x.Flag).Select(x => x.Seq).ToList(),
        ["Where_Flag_equal_false"] = q => q.Where(x => x.Flag == false).Select(x => x.Seq).ToList(),
        ["Where_not_equal"] = q => q.Where(x => x.Num != 2).OrderBy(x => x.Seq).Select(x => x.Seq).ToList(),
        ["Where_in_list"] = q =>
        {
            var wanted = new[] { "ab", "abc" };
            return q.Where(x => wanted.Contains(x.Name)).OrderBy(x => x.Seq).Select(x => x.Seq).ToList();
        },
        ["Where_equal_Seq_computed"] = q => q.Where(x => x.Seq + 1 == 3).Select(x => x.Name).ToList(),
        ["Select_Name"] = q => q.OrderBy(x => x.Seq).Select(x => x.Name).ToList(),
        ["Select_Num"] = q => q.OrderBy(x => x.Seq).Select(x => x.Num).ToList(),
        ["Select_anonymous"] = q => q.OrderBy(x => x.Seq).Select(x => new { x.Name, x.Num }).ToList(),
        ["Select_Seq_computed"] = q => q.OrderBy(x => x.Seq).Select(x => x.Seq + 1).ToList(),

        // A top-level projected string function is applied client-side to the materialized value (the case mapping is never pushed down).
        ["Select_ToUpper"] = q => q.OrderBy(x => x.Seq).Select(x => x.Name.ToUpper()).ToList(),
        ["Select_Name_Length_of_unconverted"] = q => q.OrderBy(x => x.Seq).Select(x => x.Seq.ToString().Length).ToList(),        ["Count_equal"] = q => q.Count(x => x.Name == "abc"),
        ["Whole_entity_Where_equal"] = q => q.Where(x => x.Num == 3).AsEnumerable().Select(x => x.Name).ToList()
    };

    // Shapes the stored-form guard must not refuse: a function of a property stored as its own ToString() (EF built-in enum-to-string
    // converter), and arithmetic over EF built-in casting converter to a wider type (int stored as long). The native translator or the
    // driver may still decline them loudly for other reasons; the answer, when there is one, is right.
    private static readonly Dictionary<string, Func<IQueryable<Item>, object>> NotRefusedQueries = new()
    {
        ["Where_ToString_StartsWith"] = q => q.Where(x => x.Color.ToString().StartsWith("R")).OrderBy(x => x.Seq).Select(x => x.Seq).ToList(),
        ["Where_ToString_Length"] = q => q.Where(x => x.Color.ToString().Length == 3).OrderBy(x => x.Seq).Select(x => x.Seq).ToList(),
        ["Where_stored_list_Contains_alike"] = q => q.Where(x => x.Scores.Contains(x.Seq)).Select(x => x.Seq).ToList(),
        ["Where_numeric_cast_modulo"] = q => q.Where(x => x.Wide % 2 == 1).OrderBy(x => x.Seq).Select(x => x.Seq).ToList(),
        ["Select_numeric_cast_plus"] = q => q.OrderBy(x => x.Seq).Select(x => x.Wide + 1).ToList()
    };

    private static readonly MongoQueryMode[] Modes = [MongoQueryMode.Native, MongoQueryMode.NativeOnly, MongoQueryMode.DriverLinq];

    public static IEnumerable<object[]> RefusedCases()
        => from query in RefusedQueries.Keys from mode in Modes select new object[] { query, mode };

    public static IEnumerable<object[]> NotRefusedCases()
        => from query in NotRefusedQueries.Keys from mode in Modes select new object[] { query, mode };

    public static IEnumerable<object[]> AllowedCases()
        => from query in AllowedQueries.Keys from mode in Modes select new object[] { query, mode };

    [Theory]
    [MemberData(nameof(RefusedCases))]
    public void Function_over_converted_property_is_refused(string query, MongoQueryMode mode)
    {
        var (oracle, run) = Arrange(query, RefusedQueries[query], mode);

        var ex = Record.Exception(run);
        if (ex is null)
        {
            Assert.Equal(oracle, run());
        }

        Assert.NotNull(ex);
        if (mode == MongoQueryMode.NativeOnly)
        {
            Assert.IsType<NativeTranslationNotSupportedException>(ex);
        }
        else if (query.StartsWith("GroupBy", StringComparison.Ordinal))
        {
            // A fallback GroupBy whose key the native binder left unbound fails at compile time, before the bridge runs.
            Assert.True(ex is NotSupportedException or InvalidOperationException, ex.ToString());
        }
        else
        {
            Assert.Matches(@"Item.(Name|Num|Flag|Scores)'", Assert.IsType<NotSupportedException>(ex).Message);
        }
    }

    [Theory]
    [MemberData(nameof(AllowedCases))]
    public void Reading_or_comparing_converted_property_keeps_working(string query, MongoQueryMode mode)
    {
        var (oracle, run) = Arrange(query, AllowedQueries[query], mode);

        Assert.Equal(oracle, run());
    }

    [Theory]
    [MemberData(nameof(NotRefusedCases))]
    public void Function_over_exactly_stored_property_is_not_refused(string query, MongoQueryMode mode)
    {
        var (oracle, run) = Arrange(query, NotRefusedQueries[query], mode);

        var ex = Record.Exception(run);
        if (ex is null)
        {
            Assert.Equal(oracle, run());
            return;
        }

        Assert.DoesNotContain("value converter or a BsonRepresentation", ex.Message);
    }

    private (string Oracle, Func<string> Run) Arrange(string query, Func<IQueryable<Item>, object> shape, MongoQueryMode mode)
    {
        var name = TemporaryDatabaseFixtureBase.CreateCollectionName(nameof(ConvertedPropertyFunctionTests), query, mode)
                   + Guid.NewGuid().ToString("N")[..8];

        // Seeded as BsonDocument: the driver's POCO serializer ignores EF's converter.
        database.MongoDatabase.GetCollection<BsonDocument>(name).InsertMany(
            Rows.Select(r => new BsonDocument
            {
                { "_id", ObjectId.GenerateNewId() },
                { "Seq", r.Seq },
                { "Name", "pfx-" + r.Name },
                { "Num", r.Num * 2 },
                { "Color", ColorOf(r.Seq).ToString() },
                { "Flag", r.Seq != 2 ? "1" : "0" },
                { "Scores", new BsonArray { r.Seq == 1 ? 9 : r.Seq == 2 ? 4 : 3 } },
                { "Wide", (long)r.Seq }
            }));

        var collection = database.MongoDatabase.GetCollection<Item>(name);

        using var materializing = CreateContext(collection, MongoQueryMode.Native);
        var entities = materializing.Entities.AsNoTracking().ToList();
        Assert.Equal(Rows.Select(r => r.Name), entities.OrderBy(e => e.Seq).Select(e => e.Name));
        Assert.Equal(Rows.Select(r => r.Num), entities.OrderBy(e => e.Seq).Select(e => e.Num));
        var oracle = Normalize(shape(entities.AsQueryable()));

        return (oracle, () =>
        {
            using var db = CreateContext(collection, mode);
            return Normalize(shape(db.Entities.AsNoTracking()));
        });
    }

    private static SingleEntityDbContext<Item> CreateContext(IMongoCollection<Item> collection, MongoQueryMode mode)
        => SingleEntityDbContext.Create(
            collection,
            modelBuilderAction: mb =>
            {
                mb.Entity<Item>().Property(e => e.Name).HasConversion(v => "pfx-" + v, v => v.Substring(4));
                mb.Entity<Item>().Property(e => e.Num).HasConversion(v => v * 2, v => v / 2);
                mb.Entity<Item>().Property(e => e.Color).HasConversion<string>();
                mb.Entity<Item>().Property(e => e.Wide).HasConversion<long>();
                mb.Entity<Item>().Property(e => e.Flag).HasConversion<string>();
            },
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });

    private static string Normalize(object? value)
        => value switch
        {
            null => "null",
            string s => s,
            IEnumerable e => "[" + string.Join(",", e.Cast<object?>().Select(Normalize)) + "]",
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? ""
        };
}
