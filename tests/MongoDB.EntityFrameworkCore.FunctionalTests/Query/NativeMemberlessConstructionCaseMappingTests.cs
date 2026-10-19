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
using MongoDB.Driver.Linq;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Infrastructure;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// A client-side case mapping (<c>ToUpper</c>/<c>ToLower</c> and the Invariant forms) inside a member-less
/// construction (<c>KeyValuePair</c>, <c>Tuple</c>, a positional record, a constructor-only DTO) is applied to the
/// projected receiver, exactly as in an anonymous type or a member-init DTO; the server's <c>$toUpper</c>/<c>$toLower</c>
/// (ASCII-only, null as <c>""</c>) never answers it.
/// </summary>
/// <remarks>
/// <para>
/// Hand oracle: C# over the stored values, except that a case mapping over a null (or missing) S answers null rather
/// than throwing: EF's null propagation, as for <c>new { U = x.S.ToUpper() }</c>. Rows: value (S = "Abc", T = "b"),
/// null (S and T null), missing (no S or T element) and accent (S = "école", T = "é"); N = 3 in every row. Each row is
/// queried on its own.
/// </para>
/// <para>
/// A construction whose constructor parameters each name a member of the type (<c>KeyValuePair</c>, <c>Tuple</c>, a
/// positional record) is read from whole documents in every mode, argument by argument, so it isn't native and
/// NativeOnly throws. A constructor-only DTO whose parameters name no member stays native, re-applying the call over
/// the projected receiver; on driver-LINQ it is pushed down, where the driver rejects it.
/// </para>
/// </remarks>
[XUnitCollection("QueryTests")]
public class NativeMemberlessConstructionCaseMappingTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    private const string Throws = "<throws>";

    public class Row
    {
        public ObjectId Id { get; set; }
        public string Label { get; set; } = "";
        public string? S { get; set; }
        public string? T { get; set; }
        public int N { get; set; }
    }

    public class CDto(string? s, int n)
    {
        public string? Upper { get; } = s;
        public int Doubled { get; } = n;
    }

    public class CDto2(string? s, string? t)
    {
        public string? A { get; } = s;
        public string? B { get; } = t;
    }

    public class CDto1(string? s)
    {
        public string? Upper { get; } = s;
    }

    public record PRec(string? S, string? T);

    public class MDto
    {
        public string? U { get; set; }
        public string? T { get; set; }
    }

    public static TheoryData<string> AllModes
        => [nameof(MongoQueryMode.DriverLinq), nameof(MongoQueryMode.Native), nameof(MongoQueryMode.NativeOnly)];

    [Theory]
    [MemberData(nameof(AllModes))]
    public void KeyValuePair_with_a_case_mapped_argument(string mode)
    {
        AssertRows(mode, q => q.Select(x => new KeyValuePair<string, string?>(x.S!.ToUpper(), x.T))
                .AsEnumerable().Select(x => Show(x.Key) + ":" + Show(x.Value)),
            ["ABC:b", "<null>:<null>", "<null>:<null>", "ÉCOLE:é"], nativeOnlyThrows: true);
        AssertRows(mode, q => q.Select(x => new KeyValuePair<string?, string>(x.T, x.S!.ToLowerInvariant()))
                .AsEnumerable().Select(x => Show(x.Key) + ":" + Show(x.Value)),
            ["b:abc", "<null>:<null>", "<null>:<null>", "é:école"], nativeOnlyThrows: true);
    }

    [Theory]
    [MemberData(nameof(AllModes))]
    public void Tuple_with_a_case_mapped_argument(string mode)
        => AssertRows(mode, q => q.Select(x => new Tuple<string, string?>(x.S!.ToUpperInvariant(), x.T))
                .AsEnumerable().Select(x => Show(x.Item1) + ":" + Show(x.Item2)),
            ["ABC:b", "<null>:<null>", "<null>:<null>", "ÉCOLE:é"], nativeOnlyThrows: true);

    [Theory]
    [MemberData(nameof(AllModes))]
    public void Positional_record_with_a_case_mapped_argument(string mode)
        => AssertRows(mode, q => q.Select(x => new PRec(x.S!.ToUpper(), x.T))
                .AsEnumerable().Select(x => Show(x.S) + ":" + Show(x.T)),
            ["ABC:b", "<null>:<null>", "<null>:<null>", "ÉCOLE:é"], nativeOnlyThrows: true);

    // The whole case-mapping chain is re-applied, over a receiver that may itself be server-computed (Trim).
    [Theory]
    [MemberData(nameof(AllModes))]
    public void Repeated_and_computed_receiver_case_mappings_in_a_member_less_construction(string mode)
    {
        AssertRows(mode, q => q.Select(x => new KeyValuePair<string, string>(x.S!.ToLower().ToUpper(), x.S!.ToLower()))
                .AsEnumerable().Select(x => Show(x.Key) + ":" + Show(x.Value)),
            ["ABC:abc", "<null>:<null>", "<null>:<null>", "ÉCOLE:école"], nativeOnlyThrows: true);
        AssertRows(mode, q => q.Select(x => new KeyValuePair<string, string?>(x.S!.Trim().ToUpper(), x.T))
                .AsEnumerable().Select(x => Show(x.Key) + ":" + Show(x.Value)),
            ["ABC:b", "<null>:<null>", "<null>:<null>", "ÉCOLE:é"], nativeOnlyThrows: true);
    }

    // Parameters that name no member: native in Native/NativeOnly; driver-LINQ pushes it down and the driver rejects
    // it.
    [Theory]
    [MemberData(nameof(AllModes))]
    public void Constructor_only_dto_with_a_case_mapped_argument(string mode)
        => AssertRows(mode, q => q.Select(x => new CDto(x.S!.ToUpper(), x.N * 2))
                .AsEnumerable().Select(x => Show(x.Upper) + ":" + x.Doubled),
            ["ABC:6", "<null>:6", "<null>:6", "ÉCOLE:6"], driverLinqThrows: true);

    // As above, with only nullable arguments: read off a whole document by their synthetic aliases, they would all
    // silently answer null.
    [Theory]
    [MemberData(nameof(AllModes))]
    public void Constructor_only_dto_with_nullable_arguments_and_a_case_mapped_argument(string mode)
    {
        AssertRows(mode, q => q.Select(x => new CDto2(x.S!.ToUpper(), x.T))
                .AsEnumerable().Select(x => Show(x.A) + ":" + Show(x.B)),
            ["ABC:b", "<null>:<null>", "<null>:<null>", "ÉCOLE:é"], driverLinqThrows: true);
        AssertRows(mode, q => q.Select(x => new CDto2(x.T, x.S!.ToLower().ToUpperInvariant()))
                .AsEnumerable().Select(x => Show(x.A) + ":" + Show(x.B)),
            ["b:ABC", "<null>:<null>", "<null>:<null>", "é:ÉCOLE"], driverLinqThrows: true);
    }

    [Theory]
    [MemberData(nameof(AllModes))]
    public void Single_argument_constructor_only_dto_with_a_case_mapped_argument(string mode)
        => AssertRows(mode, q => q.Select(x => new CDto1(x.S!.ToUpper()))
                .AsEnumerable().Select(x => Show(x.Upper)),
            ["ABC", "<null>", "<null>", "ÉCOLE"]);

    // Controls: an anonymous type and a member-init DTO.
    [Theory]
    [MemberData(nameof(AllModes))]
    public void Anonymous_type_and_member_init_dto_with_a_case_mapped_member(string mode)
    {
        AssertRows(mode, q => q.Select(x => new { U = x.S!.ToUpper(), x.T })
                .AsEnumerable().Select(x => Show(x.U) + ":" + Show(x.T)),
            ["ABC:b", "<null>:<null>", "<null>:<null>", "ÉCOLE:é"]);
        AssertRows(mode, q => q.Select(x => new MDto { U = x.S!.ToUpper(), T = x.T })
                .AsEnumerable().Select(x => Show(x.U) + ":" + Show(x.T)),
            ["ABC:b", "<null>:<null>", "<null>:<null>", "ÉCOLE:é"]);
    }

    // A member-less construction nested in an anonymous type is not a native leaf; it reads from whole documents.
    [Theory]
    [MemberData(nameof(AllModes))]
    public void Member_less_construction_nested_in_an_anonymous_type(string mode)
    {
        string[] expected = ["ABC:b:3", "<null>:<null>:3", "<null>:<null>:3", "ÉCOLE:é:3"];
        AssertRows(mode, q => q.Select(x => new { K = new KeyValuePair<string, string?>(x.S!.ToUpper(), x.T), x.N })
                .AsEnumerable().Select(x => Show(x.K.Key) + ":" + Show(x.K.Value) + ":" + x.N),
            expected, nativeOnlyThrows: true);
        AssertRows(mode, q => q.Select(x => new { R = new PRec(x.S!.ToUpper(), x.T), x.N })
                .AsEnumerable().Select(x => Show(x.R.S) + ":" + Show(x.R.T) + ":" + x.N),
            expected, nativeOnlyThrows: true);
    }

    private static string Show(string? value) => value ?? "<null>";

    // Each row answers the oracle, or, where the mode is expected to throw, every row throws that mode's exception:
    // NativeOnly's decline, or the driver's rejection of a constructor it can't match to properties.
    private void AssertRows(
        string mode,
        Func<IQueryable<Row>, IEnumerable<string>> query,
        string[] expected,
        bool nativeOnlyThrows = false,
        bool driverLinqThrows = false,
        [System.Runtime.CompilerServices.CallerMemberName] string name = "")
    {
        var collection = Seed(name + mode);
        var labels = new[] { "value", "null", "missing", "accent" };
        var actual = new List<string>();
        foreach (var label in labels)
        {
            using var db = CreateContext(collection, Enum.Parse<MongoQueryMode>(mode));
            try
            {
                actual.Add(string.Join(",", query(db.Entities.AsNoTracking().Where(x => x.Label == label)).ToList()));
            }
            catch (NativeTranslationNotSupportedException) when (mode == nameof(MongoQueryMode.NativeOnly))
            {
                actual.Add(Throws);
            }
            catch (ExpressionNotSupportedException exception)
                when (mode == nameof(MongoQueryMode.DriverLinq)
                      && exception.Message.Contains("couldn't find matching properties for constructor parameters"))
            {
                actual.Add(Throws);
            }
        }

        var throws = (nativeOnlyThrows && mode == nameof(MongoQueryMode.NativeOnly))
                     || (driverLinqThrows && mode == nameof(MongoQueryMode.DriverLinq));
        Assert.Equal(throws ? labels.Select(_ => Throws) : expected, actual);
    }

    private IMongoCollection<Row> Seed(string name)
    {
        var collectionName = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + Guid.NewGuid().ToString("N")[..8];
        database.MongoDatabase.GetCollection<BsonDocument>(collectionName).InsertMany(
        [
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Label", "value" }, { "S", "Abc" }, { "T", "b" }, { "N", 3 } },
            new BsonDocument
            {
                { "_id", ObjectId.GenerateNewId() }, { "Label", "null" }, { "S", BsonNull.Value }, { "T", BsonNull.Value }, { "N", 3 }
            },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Label", "missing" }, { "N", 3 } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Label", "accent" }, { "S", "école" }, { "T", "é" }, { "N", 3 } }
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
