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
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// A client-side case mapping (<c>ToUpper</c>/<c>ToLower</c> and the Invariant forms) projected from a grouped result
/// (<c>GroupBy(x =&gt; x.S).Select(g =&gt; new { K = g.Key.ToUpper(), C = g.Count() })</c>) is applied to the grouped
/// value client-side, never by the server's <c>$toUpper</c>/<c>$toLower</c> (ASCII-only, null as <c>""</c>).
/// </summary>
/// <remarks>
/// <para>
/// Hand oracle: C# over the stored values, except that a case mapping over a null key answers null rather than
/// throwing (EF's null propagation, as for <c>new { U = x.S.ToUpper() }</c>), and a null and a missing S share one
/// group. Rows: value (S = "Abc", N = 1), null (S null, N = 2), missing (no S, N = 2), accent (S = "école", N = 1).
/// Groups are compared as sorted "key:value" strings.
/// </para>
/// <para>
/// The native <c>$group</c> projects the raw grouped value and the shaper re-applies the call. Driver-LINQ (explicit,
/// or a Native fallback) would compute it with <c>$toUpper</c>/<c>$toLower</c>, so it throws instead; NativeOnly throws
/// its own decline for a shape that isn't native.
/// </para>
/// </remarks>
[XUnitCollection("QueryTests")]
public class NativeGroupByKeyCaseMappingTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    public class Row
    {
        public ObjectId Id { get; set; }
        public string Label { get; set; } = "";
        public string? S { get; set; }
        public string? T { get; set; }
        public int N { get; set; }
    }

    public class GroupDto
    {
        public string? K { get; set; }
        public int C { get; set; }
    }

    public static TheoryData<string> AllModes
        => [nameof(MongoQueryMode.DriverLinq), nameof(MongoQueryMode.Native), nameof(MongoQueryMode.NativeOnly)];

    private static readonly string[] UpperKeyCounts = ["<null>:2", "ABC:1", "ÉCOLE:1"];

    [Theory]
    [MemberData(nameof(AllModes))]
    public void KeyValuePair_over_a_case_mapped_key(string mode)
        => AssertGroups(mode, q => q.GroupBy(x => x.S).Select(g => new KeyValuePair<string, int>(g.Key!.ToUpper(), g.Count()))
                .AsEnumerable().Select(x => Show(x.Key) + ":" + x.Value),
            UpperKeyCounts, Native);

    [Theory]
    [MemberData(nameof(AllModes))]
    public void Anonymous_type_and_member_init_dto_over_a_case_mapped_key(string mode)
    {
        AssertGroups(mode, q => q.GroupBy(x => x.S).Select(g => new { K = g.Key!.ToUpper(), C = g.Count() })
                .AsEnumerable().Select(x => Show(x.K) + ":" + x.C),
            UpperKeyCounts, Native);
        AssertGroups(mode, q => q.GroupBy(x => x.S).Select(g => new GroupDto { K = g.Key!.ToUpper(), C = g.Count() })
                .AsEnumerable().Select(x => Show(x.K) + ":" + x.C),
            UpperKeyCounts, Native);
    }

    // Only nullable members: read off anything but the $group output (whole documents), each would silently be null.
    [Theory]
    [MemberData(nameof(AllModes))]
    public void Only_nullable_members_beside_a_case_mapped_key(string mode)
        => AssertGroups(mode, q => q.GroupBy(x => x.S).Select(g => new { K = g.Key!.ToUpper(), M = g.Max(x => x.S) })
                .AsEnumerable().Select(x => Show(x.K) + ":" + Show(x.M)),
            ["<null>:<null>", "ABC:Abc", "ÉCOLE:école"], Native);

    [Theory]
    [MemberData(nameof(AllModes))]
    public void Other_case_mappings_over_a_key(string mode)
    {
        AssertGroups(mode, q => q.GroupBy(x => x.S).Select(g => new { K = g.Key!.ToLower(), C = g.Count() })
                .AsEnumerable().Select(x => Show(x.K) + ":" + x.C),
            ["<null>:2", "abc:1", "école:1"], Native);
        AssertGroups(mode, q => q.GroupBy(x => x.S).Select(g => new { K = g.Key!.ToUpperInvariant(), C = g.Count() })
                .AsEnumerable().Select(x => Show(x.K) + ":" + x.C),
            UpperKeyCounts, Native);
        AssertGroups(mode, q => q.GroupBy(x => x.S).Select(g => new { K = g.Key!.ToLowerInvariant(), C = g.Count() })
                .AsEnumerable().Select(x => Show(x.K) + ":" + x.C),
            ["<null>:2", "abc:1", "école:1"], Native);
        AssertGroups(mode, q => q.GroupBy(x => x.S).Select(g => new { K = g.Key!.ToLower().ToUpper(), R = g.Key, C = g.Count() })
                .AsEnumerable().Select(x => Show(x.K) + ":" + Show(x.R) + ":" + x.C),
            ["<null>:<null>:2", "ABC:Abc:1", "ÉCOLE:école:1"], Native);
    }

    [Theory]
    [MemberData(nameof(AllModes))]
    public void Case_mapped_composite_key_member(string mode)
        => AssertGroups(mode, q => q.GroupBy(x => new { A = x.S, x.N }).Select(g => new { K = g.Key.A!.ToUpper(), g.Key.N, C = g.Count() })
                .AsEnumerable().Select(x => Show(x.K) + ":" + x.N + ":" + x.C),
            ["<null>:2:2", "ABC:1:1", "ÉCOLE:1:1"], Native);

    [Theory]
    [MemberData(nameof(AllModes))]
    public void Case_mapped_aggregate_result(string mode)
    {
        AssertGroups(mode, q => q.GroupBy(x => x.N).Select(g => new { g.Key, M = g.Max(x => x.S)!.ToUpper() })
                .AsEnumerable().Select(x => x.Key + ":" + Show(x.M)),
            ["1:ÉCOLE", "2:<null>"], Native);
        AssertGroups(mode, q => q.GroupBy(x => x.N).Select(g => g.Min(x => x.S)!.ToLower())
                .AsEnumerable().Select(Show),
            ["<null>", "abc"], Native);
    }

    [Theory]
    [MemberData(nameof(AllModes))]
    public void Element_selector_overload(string mode)
        => AssertGroups(mode, q => q.GroupBy(x => x.S, x => x.N).Select(g => new { K = g.Key!.ToUpper(), T = g.Sum() })
                .AsEnumerable().Select(x => Show(x.K) + ":" + x.T),
            ["<null>:4", "ABC:1", "ÉCOLE:1"], Native);

    // Not native (TranslateGroupBy declines a result selector), so loud; without a case mapping this overload fails
    // translation in every mode anyway.
    [Theory]
    [MemberData(nameof(AllModes))]
    public void Result_selector_overload(string mode)
        => AssertGroups(mode, q => q.GroupBy(x => x.S, (k, g) => new { K = k!.ToUpper(), C = g.Count() })
                .AsEnumerable().Select(x => Show(x.K) + ":" + x.C),
            UpperKeyCounts, Loud);

    [Theory]
    [MemberData(nameof(AllModes))]
    public void After_a_Where(string mode)
        => AssertGroups(mode, q => q.Where(x => x.Label != "value").GroupBy(x => x.S).Select(g => new { K = g.Key!.ToUpper(), C = g.Count() })
                .AsEnumerable().Select(x => Show(x.K) + ":" + x.C),
            ["<null>:2", "ÉCOLE:1"], Native);

    // Loud in every mode: nothing can read the mapped value server-side, and the driver would compute it with $toUpper.
    [Theory]
    [MemberData(nameof(AllModes))]
    public void Shapes_with_no_native_case_mapping_throw(string mode)
    {
        // A bare key is a plain Distinct, which the native $group declines.
        AssertGroups(mode, q => q.GroupBy(x => x.S).Select(g => g.Key!.ToUpper()).AsEnumerable().Select(Show),
            ["<null>", "ABC", "ÉCOLE"], Loud);
        // The Where reads the mapped value.
        AssertGroups(mode, q => q.GroupBy(x => x.S).Select(g => new { K = g.Key!.ToUpper(), C = g.Count() })
                .Where(x => x.K == "ÉCOLE").AsEnumerable().Select(x => Show(x.K) + ":" + x.C),
            ["ÉCOLE:1"], Loud);
        // A case mapping inside a nested construction.
        AssertGroups(mode, q => q.GroupBy(x => x.S).Select(g => new { I = new { K = g.Key!.ToUpper() }, C = g.Count() })
                .AsEnumerable().Select(x => Show(x.I.K) + ":" + x.C),
            UpperKeyCounts, Loud);
    }

    // A case mapping that isn't the whole projected member: native declines (it never emits $toUpper/$toLower) and
    // driver-LINQ would compute it on the server, so every mode throws. The oracle is what C# answers.
    [Theory]
    [MemberData(nameof(AllModes))]
    public void Case_mapping_elsewhere_in_a_grouped_query_throws(string mode)
    {
        // Consumed by a concatenation.
        AssertGroups(mode, q => q.GroupBy(x => x.S).Select(g => new { K = g.Key!.ToUpper() + "!", C = g.Count() })
                .AsEnumerable().Select(x => Show(x.K) + ":" + x.C),
            ["!:2", "ABC!:1", "ÉCOLE!:1"], Loud);
        // In the key selector.
        AssertGroups(mode, q => q.GroupBy(x => x.S!.ToUpper()).Select(g => new { g.Key, C = g.Count() })
                .AsEnumerable().Select(x => Show(x.Key) + ":" + x.C),
            ["<null>:2", "ABC:1", "ÉCOLE:1"], Loud);
        // In the element selector.
        AssertGroups(mode, q => q.GroupBy(x => x.N, x => x.S!.ToUpper()).Select(g => new { g.Key, M = g.Max() })
                .AsEnumerable().Select(x => x.Key + ":" + Show(x.M)),
            ["1:ÉCOLE", "2:<null>"], Loud);
        // In an accumulator operand.
        AssertGroups(mode, q => q.GroupBy(x => x.N).Select(g => new { g.Key, M = g.Max(x => x.S!.ToUpper()) })
                .AsEnumerable().Select(x => x.Key + ":" + Show(x.M)),
            ["1:ÉCOLE", "2:<null>"], Loud);
        // In a conditional.
        AssertGroups(mode, q => q.GroupBy(x => x.S).Select(g => new { K = g.Key == null ? "none" : g.Key.ToUpper(), C = g.Count() })
                .AsEnumerable().Select(x => Show(x.K) + ":" + x.C),
            ["ABC:1", "none:2", "ÉCOLE:1"], Loud);
        // In a Select feeding the GroupBy.
        AssertGroups(mode, q => q.Select(x => new { U = x.S!.ToUpper(), x.N }).GroupBy(x => x.U).Select(g => new { g.Key, C = g.Count() })
                .AsEnumerable().Select(x => Show(x.Key) + ":" + x.C),
            ["<null>:2", "ABC:1", "ÉCOLE:1"], Loud);
        // In a Select that stays below the GroupBy (a Distinct materializes it; the one above is folded into the key).
        AssertGroups(mode, q => q.Select(x => new { U = x.S!.ToUpper(), x.N }).Distinct().GroupBy(x => x.U).Select(g => new { g.Key, C = g.Count() })
                .AsEnumerable().Select(x => Show(x.Key) + ":" + x.C),
            ["<null>:1", "ABC:1", "ÉCOLE:1"], Loud);
        // In a Select on the other side of a set op the GroupBy groups.
        AssertGroups(mode, q => q.Select(x => new { U = x.S }).Concat(q.Select(x => new { U = (string?)x.S!.ToUpper() }))
                .GroupBy(x => x.U).Select(g => new { g.Key, C = g.Count() })
                .AsEnumerable().Select(x => Show(x.Key) + ":" + x.C),
            ["<null>:4", "ABC:1", "Abc:1", "ÉCOLE:1", "école:1"], Loud);
        AssertGroups(mode, q => q.Select(x => new { U = x.S }).Union(q.Select(x => new { U = (string?)x.S!.ToUpper() }))
                .GroupBy(x => x.U).Select(g => new { g.Key, C = g.Count() })
                .AsEnumerable().Select(x => Show(x.Key) + ":" + x.C),
            ["<null>:1", "ABC:1", "Abc:1", "ÉCOLE:1", "école:1"], Loud);
        // In an ungrouped Select a grouped result is combined with.
        AssertGroups(mode, q => q.GroupBy(x => x.S).Select(g => new { K = g.Key }).Concat(q.Select(x => new { K = (string?)x.S!.ToUpper() }))
                .AsEnumerable().Select(x => Show(x.K)),
            ["<null>", "<null>", "<null>", "ABC", "Abc", "ÉCOLE", "école"], Loud);
        // In a Where before the GroupBy, against another field (the driver's $expr + ASCII-only $toUpper drops école)...
        AssertGroups(mode, q => q.Where(x => x.S!.ToUpper() == x.T).GroupBy(x => x.S).Select(g => new { g.Key, C = g.Count() })
                .AsEnumerable().Select(x => Show(x.Key) + ":" + x.C),
            ["Abc:1", "école:1"], Loud);
        // ...or a constant: native (a case-insensitive regex); driver-LINQ would answer it correctly too, but is declined
        // all the same.
        AssertGroups(mode, q => q.Where(x => x.S!.ToUpper() == "ÉCOLE").GroupBy(x => x.S).Select(g => new { g.Key, C = g.Count() })
                .AsEnumerable().Select(x => Show(x.Key) + ":" + x.C),
            ["école:1"], Native);
        // In a Where on the ungrouped side a grouped result is combined with.
        AssertGroups(mode, q => q.GroupBy(x => x.S).Select(g => new { K = g.Key })
                .Concat(q.Where(x => x.S!.ToUpper() == x.T).Select(x => new { K = x.S }))
                .AsEnumerable().Select(x => Show(x.K)),
            ["<null>", "Abc", "Abc", "école", "école"], Loud);
        // In an OrderBy before the GroupBy.
        AssertGroups(mode, q => q.OrderBy(x => x.S!.ToUpper()).GroupBy(x => x.N).Select(g => new { g.Key, F = g.First().Label })
                .AsEnumerable().Select(x => x.Key + ":" + x.F),
            ["1:value", "2:null"], Loud);
        // Consumed by Length (C# throws for the null key; the server answers 0).
        AssertGroups(mode, q => q.GroupBy(x => x.S).Select(g => new { L = g.Key!.ToUpper().Length, C = g.Count() })
                .AsEnumerable().Select(x => x.L + ":" + x.C),
            ["<throws>"], Loud);
    }

    // Ungrouped case mappings keep their existing answers in every mode.
    [Theory]
    [MemberData(nameof(AllModes))]
    public void Ungrouped_case_mapping_is_unaffected(string mode)
    {
        AssertGroups(mode, q => q.Select(x => x.S!.ToUpper()).AsEnumerable().Select(Show),
            ["<null>", "<null>", "ABC", "ÉCOLE"], Everywhere);
        AssertGroups(mode, q => q.Select(x => new { U = x.S!.ToLower(), x.N }).AsEnumerable().Select(x => Show(x.U) + ":" + x.N),
            ["<null>:2", "<null>:2", "abc:1", "école:1"], Everywhere);
        AssertGroups(mode, q => q.Where(x => x.S!.ToUpper() == "ÉCOLE").AsEnumerable().Select(x => x.Label),
            ["accent"], Everywhere);

        // An ungrouped set op over a case-mapped Select isn't declined by the grouped-query gate (a known gap: on
        // driver-LINQ it still runs $toUpper).
        using var db = CreateContext(Seed(nameof(Ungrouped_case_mapping_is_unaffected) + mode + "SetOp"), Enum.Parse<MongoQueryMode>(mode));
        var exception = Record.Exception(() => db.Entities.AsNoTracking().Select(x => new { K = x.S })
            .Concat(db.Entities.AsNoTracking().Select(x => new { K = (string?)x.S!.ToUpper() })).ToList());
        Assert.False(exception?.Message.Contains("$toUpper/$toLower") == true, exception?.Message);
    }

    // Controls: no case mapping, every mode answers.
    [Theory]
    [MemberData(nameof(AllModes))]
    public void Unmapped_key_control(string mode)
        => AssertGroups(mode, q => q.GroupBy(x => x.S).Select(g => new { K = g.Key, C = g.Count() })
                .AsEnumerable().Select(x => Show(x.K) + ":" + x.C),
            ["<null>:2", "Abc:1", "école:1"], Everywhere);

    // Everywhere: every mode answers. Native: native modes answer; DriverLinq throws the case-mapping decline. Loud:
    // DriverLinq and Native throw the case-mapping decline, NativeOnly its own.
    private const int Everywhere = 0;
    private const int Native = 1;
    private const int Loud = 2;

    private static string Show(string? value) => value ?? "<null>";

    private void AssertGroups(
        string mode,
        Func<IQueryable<Row>, IEnumerable<string>> query,
        string[] expected,
        int reach,
        [System.Runtime.CompilerServices.CallerMemberName] string name = "")
    {
        var collection = Seed(name + mode);
        using var db = CreateContext(collection, Enum.Parse<MongoQueryMode>(mode));
        var source = db.Entities.AsNoTracking();

        if (reach == Loud && mode == nameof(MongoQueryMode.NativeOnly))
        {
            Assert.Throws<NativeTranslationNotSupportedException>(() => query(source).ToList());
            return;
        }

        if ((reach == Native && mode == nameof(MongoQueryMode.DriverLinq)) || reach == Loud)
        {
            var exception = Assert.Throws<InvalidOperationException>(() => query(source).ToList());
            Assert.Contains("$toUpper/$toLower", exception.Message);
            return;
        }

        Assert.Equal(expected, query(source).OrderBy(x => x, StringComparer.Ordinal).ToArray());
    }

    private IMongoCollection<Row> Seed(string name)
    {
        var collectionName = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + Guid.NewGuid().ToString("N")[..8];
        database.MongoDatabase.GetCollection<BsonDocument>(collectionName).InsertMany(
        [
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Label", "value" }, { "S", "Abc" }, { "T", "ABC" }, { "N", 1 } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Label", "null" }, { "S", BsonNull.Value }, { "N", 2 } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Label", "missing" }, { "N", 2 } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Label", "accent" }, { "S", "école" }, { "T", "ÉCOLE" }, { "N", 1 } }
        ]);
        return database.MongoDatabase.GetCollection<Row>(collectionName);
    }

    private static SingleEntityDbContext<Row> CreateContext(IMongoCollection<Row> collection, MongoQueryMode mode)
        => SingleEntityDbContext.Create(
            collection,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });
}
