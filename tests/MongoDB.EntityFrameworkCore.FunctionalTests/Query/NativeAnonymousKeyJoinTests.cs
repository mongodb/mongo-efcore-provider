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
using System.Threading;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Diagnostics;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Infrastructure;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// EF-436: a navigation-less <c>Join</c>/left join whose key is an anonymous type of simple properties
/// (<c>new { s.Region, s.Zone } equals new { r.Region, r.Zone }</c>) goes native as a <c>$lookup</c> matching every
/// member pair (<c>let</c> + <c>$expr</c> <c>$and</c> for two or more, <c>localField</c>/<c>foreignField</c> for one).
/// Before this, every mode threw (the driver can't translate an anonymous join key).
/// </summary>
/// <remarks>
/// Anonymous-type equality is member-wise, so a null part equals a null part (EF Core relational compensates to
/// match), and a missing element materializes as null. The seed mixes stored nulls and missing elements on both
/// sides; the oracle is LINQ-to-objects over the same rows as materialized. Non-simple keys keep declining.
/// </remarks>
[XUnitCollection("QueryTests")]
public class NativeAnonymousKeyJoinTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    private class Shipment
    {
        public int Id { get; set; }
        public string? Region { get; set; }
        public int Zone { get; set; }
        public int? Bay { get; set; }
    }

    private class Rate
    {
        public int Id { get; set; }
        public string? Region { get; set; }
        public int Zone { get; set; }
        public int? Bay { get; set; }
        public int Price { get; set; }
    }

    // Composite primary key: both components live under `_id`.
    private class Slot
    {
        public int Zone { get; set; }
        public int Bay { get; set; }
        public string Label { get; set; } = "";
    }

    private class KeyHolder
    {
        public string? Region { get; set; }
    }

    private class TagP
    {
        public int Id { get; set; }
        public int Zone { get; set; }
        public List<int> Tags { get; set; } = [];
    }

    private class TagQ
    {
        public int Id { get; set; }
        public int Zone { get; set; }
        public List<int> Tags { get; set; } = [];
    }

    private enum Kind
    {
        A,
        B
    }

    // K stored as a string (HasConversion<string>) and Code as a string (BsonRepresentation) - on this side only.
    private class KindP
    {
        public int Id { get; set; }
        public int Zone { get; set; }
        public Kind K { get; set; }
        public int Code { get; set; }
    }

    // Default storage: K and Code as ints.
    private class KindQ
    {
        public int Id { get; set; }
        public int Zone { get; set; }
        public Kind K { get; set; }
        public int Code { get; set; }
    }

    // K stored as a string, like KindP.
    private class KindR
    {
        public int Id { get; set; }
        public int Zone { get; set; }
        public Kind K { get; set; }
    }

    // Code: lambda converter to string ("7") on ConvP, a different lambda converter (x10, "70") on ConvQ. Flag:
    // BoolToStringConverter("N", "Y") on ConvP, ("F", "T") on ConvQ. Same converter types, different encodings.
    private class ConvP
    {
        public int Id { get; set; }
        public int Zone { get; set; }
        public int Code { get; set; }
        public bool Flag { get; set; }
        public int Code2 { get; set; }
    }

    private class ConvQ
    {
        public int Id { get; set; }
        public int Zone { get; set; }
        public int Code { get; set; }
        public bool Flag { get; set; }
        public int Code2 { get; set; }
    }

    // Configured by converter type (HasConversion<TConverter>()): two different encodings with the same provider type.
    private sealed class TimesOneToStringConverter() : ValueConverter<int, string>(v => v.ToString(), v => int.Parse(v));

    private sealed class TimesTenToStringConverter()
        : ValueConverter<int, string>(v => (v * 10).ToString(), v => int.Parse(v) / 10);

    // A string key stored as an ObjectId (HasConversion<ObjectId>()) on both sides: the provider's converter is a new
    // instance per property but stateless, so the pair stays native.
    private class OidP
    {
        public int Id { get; set; }
        public int Zone { get; set; }
        public string Ref { get; set; } = "";
        public string Ref2 { get; set; } = "";
    }

    private class OidQ
    {
        public int Id { get; set; }
        public int Zone { get; set; }
        public string Ref { get; set; } = "";
        public string Ref2 { get; set; } = "";
    }

    // Stored nulls AND missing elements on both sides: shipment 4 is (missing, 1, missing), rates 14/15 are
    // (missing, 1, missing) and (null, 1, null), so null-vs-missing pairs must match as they do once materialized.
    private static readonly BsonDocument[] ShipmentDocuments =
    [
        new() { { "_id", 1 }, { "Region", "N" }, { "Zone", 1 }, { "Bay", 1 } },
        new() { { "_id", 2 }, { "Region", "N" }, { "Zone", 2 }, { "Bay", BsonNull.Value } },
        new() { { "_id", 3 }, { "Region", BsonNull.Value }, { "Zone", 1 }, { "Bay", 1 } },
        new() { { "_id", 4 }, { "Zone", 1 } },
        new() { { "_id", 5 }, { "Region", "S" }, { "Zone", 9 }, { "Bay", 3 } },
    ];

    private static readonly BsonDocument[] RateDocuments =
    [
        new() { { "_id", 10 }, { "Region", "N" }, { "Zone", 1 }, { "Bay", 1 }, { "Price", 100 } },
        new() { { "_id", 11 }, { "Region", "N" }, { "Zone", 1 }, { "Bay", 1 }, { "Price", 101 } },
        new() { { "_id", 12 }, { "Region", "N" }, { "Zone", 2 }, { "Bay", BsonNull.Value }, { "Price", 200 } },
        new() { { "_id", 13 }, { "Region", BsonNull.Value }, { "Zone", 1 }, { "Bay", 1 }, { "Price", 300 } },
        new() { { "_id", 14 }, { "Zone", 1 }, { "Price", 400 } },
        new() { { "_id", 15 }, { "Region", BsonNull.Value }, { "Zone", 1 }, { "Bay", BsonNull.Value }, { "Price", 500 } },
        new() { { "_id", 16 }, { "Region", "N" }, { "Zone", 1 }, { "Bay", 2 }, { "Price", 600 } },
    ];

    private static readonly BsonDocument[] SlotDocuments =
    [
        new() { { "_id", new BsonDocument { { "Zone", 1 }, { "Bay", 1 } } }, { "Label", "A1" } },
        new() { { "_id", new BsonDocument { { "Zone", 2 }, { "Bay", 1 } } }, { "Label", "B1" } },
    ];

    private static readonly BsonDocument[] TagPDocuments =
    [
        new() { { "_id", 1 }, { "Zone", 1 }, { "Tags", new BsonArray { 1, 2 } } },
    ];

    private static readonly BsonDocument[] TagQDocuments =
    [
        new() { { "_id", 10 }, { "Zone", 1 }, { "Tags", new BsonArray { 1, 2 } } },
        new() { { "_id", 11 }, { "Zone", 1 }, { "Tags", new BsonArray { 2, 9 } } },
    ];

    private static readonly BsonDocument[] KindPDocuments =
    [
        new() { { "_id", 1 }, { "Zone", 1 }, { "K", "B" }, { "Code", "7" } },
    ];

    private static readonly BsonDocument[] KindQDocuments =
    [
        new() { { "_id", 10 }, { "Zone", 1 }, { "K", 1 }, { "Code", 7 } },
        new() { { "_id", 11 }, { "Zone", 1 }, { "K", 0 }, { "Code", 8 } },
    ];

    private static readonly BsonDocument[] KindRDocuments =
    [
        new() { { "_id", 20 }, { "Zone", 1 }, { "K", "B" } },
        new() { { "_id", 21 }, { "Zone", 1 }, { "K", "A" } },
    ];

    private static readonly BsonDocument[] ConvPDocuments =
    [
        new() { { "_id", 1 }, { "Zone", 1 }, { "Code", "7" }, { "Flag", "Y" }, { "Code2", "7" } },
    ];

    private static readonly BsonDocument[] ConvQDocuments =
    [
        new() { { "_id", 10 }, { "Zone", 1 }, { "Code", "70" }, { "Flag", "T" }, { "Code2", "70" } },
        new() { { "_id", 11 }, { "Zone", 1 }, { "Code", "80" }, { "Flag", "F" }, { "Code2", "80" } },
    ];

    private static readonly ObjectId OidA = ObjectId.Parse("65f000000000000000000001");
    private static readonly ObjectId OidB = ObjectId.Parse("65f000000000000000000002");

    private static readonly BsonDocument[] OidPDocuments =
    [
        new() { { "_id", 1 }, { "Zone", 1 }, { "Ref", OidA }, { "Ref2", OidA } },
        new() { { "_id", 2 }, { "Zone", 1 }, { "Ref", OidB }, { "Ref2", OidB } },
    ];

    private static readonly BsonDocument[] OidQDocuments =
    [
        new() { { "_id", 10 }, { "Zone", 1 }, { "Ref", OidA }, { "Ref2", OidA } },
        new() { { "_id", 11 }, { "Zone", 2 }, { "Ref", OidB }, { "Ref2", OidB } },
    ];

    public static IEnumerable<object[]> AllModes()
        => [[MongoQueryMode.NativeOnly], [MongoQueryMode.Native], [MongoQueryMode.DriverLinq]];

    [Theory]
    [MemberData(nameof(AllModes))]
    public void Inner_join_on_three_member_anonymous_key_matches_oracle_including_null_and_missing_parts(MongoQueryMode mode)
    {
        using var db = CreateContext(mode, out var spy);
        var (shipments, rates, _) = Materialize();

        var actual = db.Shipments
            .Join(db.Rates, s => new { s.Region, s.Zone, s.Bay }, r => new { r.Region, r.Zone, r.Bay },
                (s, r) => new { S = s.Id, R = r.Id })
            .AsEnumerable().Select(x => (x.S, x.R)).OrderBy(x => x).ToList();
        var expected = shipments
            .Join(rates, s => new { s.Region, s.Zone, s.Bay }, r => new { r.Region, r.Zone, r.Bay },
                (s, r) => (S: s.Id, R: r.Id))
            .OrderBy(x => x).ToList();

        // Pins that the oracle actually exercises null == null and null == missing.
        Assert.Equal([(1, 10), (1, 11), (2, 12), (3, 13), (4, 14), (4, 15)], expected);
        Assert.Equal(expected, actual);

        // Every mode reads the registered composite $lookup (a confirmed join registers it at translation time).
        spy.AssertExecutedMqlContains("\"$and\"");
        // A nullable pair (Region) is null-normalized on both sides so null and missing compare equal.
        spy.AssertExecutedMqlContains("\"k0\" : { \"$ifNull\" : [\"$Region\", null] }");
        spy.AssertExecutedMqlContains("\"$eq\" : [{ \"$ifNull\" : [\"$Region\", null] }, \"$$k0\"]");
        // A non-nullable pair (Zone) is compared bare so an index on it stays usable.
        spy.AssertExecutedMqlContains("\"$eq\" : [\"$Zone\", \"$$k1\"]");
    }

    [Theory]
    [MemberData(nameof(AllModes))]
    public void Left_join_on_anonymous_key_matches_oracle(MongoQueryMode mode)
    {
        using var db = CreateContext(mode, out _);
        var (shipments, rates, _) = Materialize();

        var actual = (from s in db.Shipments
                      join r in db.Rates on new { s.Region, s.Bay } equals new { r.Region, r.Bay } into g
                      from r in g.DefaultIfEmpty()
                      select new { S = s.Id, R = r == null ? (int?)null : r.Id })
            .AsEnumerable().Select(x => (x.S, x.R)).OrderBy(x => x).ToList();
        var expected = (from s in shipments
                        join r in rates on new { s.Region, s.Bay } equals new { r.Region, r.Bay } into g
                        from r in g.DefaultIfEmpty()
                        select (S: s.Id, R: r == null ? (int?)null : r.Id))
            .OrderBy(x => x).ToList();

        Assert.Contains((5, null), expected);
        Assert.Contains((4, 15), expected);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [MemberData(nameof(AllModes))]
    public void Single_member_anonymous_key_matches_null_to_null_and_missing(MongoQueryMode mode)
    {
        // Unlike a bare `s.Region equals r.Region` (where C# never matches null keys), a one-member anonymous key
        // matches null to null; the localField/foreignField form treats null and missing alike, which is exactly that.
        using var db = CreateContext(mode, out _);
        var (shipments, rates, _) = Materialize();

        var actual = db.Shipments
            .Join(db.Rates, s => new { s.Region }, r => new { r.Region }, (s, r) => new { S = s.Id, R = r.Id })
            .AsEnumerable().Select(x => (x.S, x.R)).OrderBy(x => x).ToList();
        var expected = shipments
            .Join(rates, s => new { s.Region }, r => new { r.Region }, (s, r) => (S: s.Id, R: r.Id))
            .OrderBy(x => x).ToList();

        Assert.Contains((4, 13), expected);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [MemberData(nameof(AllModes))]
    public void EF_Property_members_are_simple_properties(MongoQueryMode mode)
    {
        using var db = CreateContext(mode, out _);
        var (shipments, rates, _) = Materialize();

        var actual = db.Shipments
            .Join(db.Rates,
                s => new { Region = EF.Property<string?>(s, nameof(Shipment.Region)), s.Zone },
                r => new { Region = EF.Property<string?>(r, nameof(Rate.Region)), r.Zone },
                (s, r) => new { S = s.Id, R = r.Id })
            .AsEnumerable().Select(x => (x.S, x.R)).OrderBy(x => x).ToList();
        var expected = shipments
            .Join(rates, s => new { s.Region, s.Zone }, r => new { r.Region, r.Zone }, (s, r) => (S: s.Id, R: r.Id))
            .OrderBy(x => x).ToList();

        Assert.Contains((4, 15), expected);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [MemberData(nameof(AllModes))]
    public void Anonymous_key_onto_composite_primary_key_components_matches_oracle(MongoQueryMode mode)
    {
        using var db = CreateContext(mode, out var spy);
        var (_, rates, slots) = Materialize();

        var actual = db.Rates
            .Join(db.Slots, r => new { r.Zone, r.Bay }, sl => new { sl.Zone, Bay = (int?)sl.Bay },
                (r, sl) => new { R = r.Id, sl.Label })
            .AsEnumerable().Select(x => (x.R, x.Label)).OrderBy(x => x).ToList();
        var expected = rates
            .Join(slots, r => new { r.Zone, r.Bay }, sl => new { sl.Zone, Bay = (int?)sl.Bay },
                (r, sl) => (R: r.Id, sl.Label))
            .OrderBy(x => x).ToList();

        Assert.Equal([(10, "A1"), (11, "A1"), (13, "A1")], expected);
        Assert.Equal(expected, actual);
        spy.AssertExecutedMqlContains("\"$_id.Zone\"");
    }

    [Theory]
    [MemberData(nameof(AllModes))]
    public void Chained_join_with_anonymous_key_off_the_prior_hop_matches_oracle(MongoQueryMode mode)
    {
        using var db = CreateContext(mode, out _);
        var (shipments, rates, slots) = Materialize();

        var actual = db.Shipments
            .Join(db.Rates, s => new { s.Region, s.Zone, s.Bay }, r => new { r.Region, r.Zone, r.Bay },
                (s, r) => new { s, r })
            .Join(db.Slots, x => new { x.r.Zone, x.r.Bay }, sl => new { sl.Zone, Bay = (int?)sl.Bay },
                (x, sl) => new { S = x.s.Id, R = x.r.Id, sl.Label })
            .AsEnumerable().Select(x => (x.S, x.R, x.Label)).OrderBy(x => x).ToList();
        var expected = shipments
            .Join(rates, s => new { s.Region, s.Zone, s.Bay }, r => new { r.Region, r.Zone, r.Bay },
                (s, r) => new { s, r })
            .Join(slots, x => new { x.r.Zone, x.r.Bay }, sl => new { sl.Zone, Bay = (int?)sl.Bay },
                (x, sl) => (S: x.s.Id, R: x.r.Id, sl.Label))
            .OrderBy(x => x).ToList();

        Assert.Equal([(1, 10, "A1"), (1, 11, "A1"), (3, 13, "A1")], expected);
        Assert.Equal(expected, actual);
    }

    // Non-simple keys keep declining, loudly: NativeOnly refuses with NativeTranslationNotSupportedException, and the
    // driver-LINQ fallback (Native, DriverLinq) throws ExpressionNotSupportedException as before EF-436 (the driver
    // can't translate an anonymous join key). The oracle pins what a wrong native answer would have differed from.
    [Theory]
    [InlineData("ConstantMember")]
    [InlineData("MixedHops")]
    [InlineData("NonAnonymousKey")]
    [InlineData("NestedAnonymous")]
    [InlineData("PrimitiveCollection")]
    [InlineData("PrimitiveCollectionWithScalar")]
    [InlineData("OneSideConverted")]
    [InlineData("RepresentationMismatch")]
    [InlineData("DifferentLambdaConverters")]
    [InlineData("DifferentBoolToStringConverters")]
    [InlineData("DifferentConverterTypes")]
    public void Non_simple_anonymous_keys_decline_loudly(string shape)
    {
        var (shipments, rates, slots) = Materialize();
        var (tagPs, tagQs, kindPs, kindQs, _) = MaterializeStorageShapes();
        List<ConvP> convPs;
        List<ConvQ> convQs;
        using (var seed = CreateContext(MongoQueryMode.DriverLinq, out _))
        {
            convPs = seed.ConvPs.ToList();
            convQs = seed.ConvQs.ToList();
        }

        List<string> Run(NativeAnonymousKeyJoinTestsContext db) => (shape switch
        {
            "ConstantMember" => db.Shipments
                .Join(db.Rates, s => new { s.Region, Zone = 1 }, r => new { r.Region, r.Zone }, (s, r) => new { s.Id, R = r.Id })
                .AsEnumerable().Select(x => $"{x.Id}:{x.R}"),
            "MixedHops" => db.Shipments
                .Join(db.Rates, s => new { s.Region, s.Zone, s.Bay }, r => new { r.Region, r.Zone, r.Bay }, (s, r) => new { s, r })
                .Join(db.Slots, x => new { x.s.Zone, x.r.Bay }, sl => new { sl.Zone, Bay = (int?)sl.Bay },
                    (x, sl) => new { x.s.Id, R = x.r.Id, sl.Label })
                .AsEnumerable().Select(x => $"{x.Id}:{x.R}:{x.Label}"),
            "NonAnonymousKey" => db.Shipments
                .Join(db.Rates, s => new KeyHolder { Region = s.Region }, r => new KeyHolder { Region = r.Region },
                    (s, r) => new { s.Id, R = r.Id })
                .AsEnumerable().Select(x => $"{x.Id}:{x.R}"),
            "NestedAnonymous" => db.Shipments
                .Join(db.Rates, s => new { s.Zone, N = new { s.Region } }, r => new { r.Zone, N = new { r.Region } },
                    (s, r) => new { s.Id, R = r.Id })
                .AsEnumerable().Select(x => $"{x.Id}:{x.R}"),
            "PrimitiveCollection" => db.TagPs
                .Join(db.TagQs, p => new { p.Tags }, q => new { q.Tags }, (p, q) => new { p.Id, Q = q.Id })
                .AsEnumerable().Select(x => $"{x.Id}:{x.Q}"),
            "PrimitiveCollectionWithScalar" => db.TagPs
                .Join(db.TagQs, p => new { p.Zone, p.Tags }, q => new { q.Zone, q.Tags }, (p, q) => new { p.Id, Q = q.Id })
                .AsEnumerable().Select(x => $"{x.Id}:{x.Q}"),
            "OneSideConverted" => db.KindPs
                .Join(db.KindQs, p => new { p.Zone, p.K }, q => new { q.Zone, q.K }, (p, q) => new { p.Id, Q = q.Id })
                .AsEnumerable().Select(x => $"{x.Id}:{x.Q}"),
            "RepresentationMismatch" => db.KindPs
                .Join(db.KindQs, p => new { p.Zone, p.Code }, q => new { q.Zone, q.Code }, (p, q) => new { p.Id, Q = q.Id })
                .AsEnumerable().Select(x => $"{x.Id}:{x.Q}"),
            "DifferentLambdaConverters" => db.ConvPs
                .Join(db.ConvQs, p => new { p.Zone, p.Code }, q => new { q.Zone, q.Code }, (p, q) => new { p.Id, Q = q.Id })
                .AsEnumerable().Select(x => $"{x.Id}:{x.Q}"),
            "DifferentBoolToStringConverters" => db.ConvPs
                .Join(db.ConvQs, p => new { p.Zone, p.Flag }, q => new { q.Zone, q.Flag }, (p, q) => new { p.Id, Q = q.Id })
                .AsEnumerable().Select(x => $"{x.Id}:{x.Q}"),
            _ => db.ConvPs
                .Join(db.ConvQs, p => new { p.Zone, p.Code2 }, q => new { q.Zone, q.Code2 }, (p, q) => new { p.Id, Q = q.Id })
                .AsEnumerable().Select(x => $"{x.Id}:{x.Q}"),
        }).OrderBy(x => x).ToList();

        var expected = (shape switch
        {
            "ConstantMember" => shipments
                .Join(rates, s => new { s.Region, Zone = 1 }, r => new { r.Region, r.Zone }, (s, r) => $"{s.Id}:{r.Id}"),
            "MixedHops" => shipments
                .Join(rates, s => new { s.Region, s.Zone, s.Bay }, r => new { r.Region, r.Zone, r.Bay }, (s, r) => new { s, r })
                .Join(slots, x => new { x.s.Zone, x.r.Bay }, sl => new { sl.Zone, Bay = (int?)sl.Bay },
                    (x, sl) => $"{x.s.Id}:{x.r.Id}:{sl.Label}"),
            // Reference equality: a plain class key never matches in LINQ-to-objects.
            "NonAnonymousKey" => shipments
                .Join(rates, s => new KeyHolder { Region = s.Region }, r => new KeyHolder { Region = r.Region },
                    (s, r) => $"{s.Id}:{r.Id}"),
            "NestedAnonymous" => shipments
                .Join(rates, s => new { s.Zone, N = new { s.Region } }, r => new { r.Zone, N = new { r.Region } },
                    (s, r) => $"{s.Id}:{r.Id}"),
            // List<int> compares by reference: no match, although the stored arrays overlap or are equal.
            "PrimitiveCollection" => tagPs
                .Join(tagQs, p => new { p.Tags }, q => new { q.Tags }, (p, q) => $"{p.Id}:{q.Id}"),
            "PrimitiveCollectionWithScalar" => tagPs
                .Join(tagQs, p => new { p.Zone, p.Tags }, q => new { q.Zone, q.Tags }, (p, q) => $"{p.Id}:{q.Id}"),
            // K is "B" (string) on one side and 1 (int) on the other: equal in C#, never equal as stored.
            "OneSideConverted" => kindPs
                .Join(kindQs, p => new { p.Zone, p.K }, q => new { q.Zone, q.K }, (p, q) => $"{p.Id}:{q.Id}"),
            // Code is "7" on one side and 7 on the other.
            "RepresentationMismatch" => kindPs
                .Join(kindQs, p => new { p.Zone, p.Code }, q => new { q.Zone, q.Code }, (p, q) => $"{p.Id}:{q.Id}"),
            // Code 7 is stored "7" on one side and "70" on the other; Flag true is "Y" vs "T".
            "DifferentLambdaConverters" => convPs
                .Join(convQs, p => new { p.Zone, p.Code }, q => new { q.Zone, q.Code }, (p, q) => $"{p.Id}:{q.Id}"),
            "DifferentBoolToStringConverters" => convPs
                .Join(convQs, p => new { p.Zone, p.Flag }, q => new { q.Zone, q.Flag }, (p, q) => $"{p.Id}:{q.Id}"),
            _ => convPs
                .Join(convQs, p => new { p.Zone, p.Code2 }, q => new { q.Zone, q.Code2 }, (p, q) => $"{p.Id}:{q.Id}"),
        }).OrderBy(x => x).ToList();

        switch (shape)
        {
            case "PrimitiveCollection" or "PrimitiveCollectionWithScalar":
                Assert.Empty(expected);
                break;
            case "OneSideConverted" or "RepresentationMismatch" or "DifferentLambdaConverters" or "DifferentBoolToStringConverters"
                or "DifferentConverterTypes":
                Assert.Equal(["1:10"], expected);
                break;
        }

        using (var nativeOnly = CreateContext(MongoQueryMode.NativeOnly, out _))
        {
            Assert.Throws<NativeTranslationNotSupportedException>(() => Run(nativeOnly));
        }

        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq })
        {
            using var db = CreateContext(mode, out _);
            var ex = Assert.Throws<MongoDB.Driver.Linq.ExpressionNotSupportedException>(() => Run(db));
            Assert.Contains("Expression not supported", ex.Message);
        }
    }

    [Theory]
    [MemberData(nameof(AllModes))]
    public void Both_sides_with_the_same_converter_stay_native_and_match_oracle(MongoQueryMode mode)
    {
        using var db = CreateContext(mode, out var spy);
        var (_, _, kindPs, _, kindRs) = MaterializeStorageShapes();

        var actual = db.KindPs
            .Join(db.KindRs, p => new { p.Zone, p.K }, r => new { r.Zone, r.K }, (p, r) => new { p.Id, R = r.Id })
            .AsEnumerable().Select(x => $"{x.Id}:{x.R}").OrderBy(x => x).ToList();
        var expected = kindPs
            .Join(kindRs, p => new { p.Zone, p.K }, r => new { r.Zone, r.K }, (p, r) => $"{p.Id}:{r.Id}")
            .OrderBy(x => x).ToList();

        Assert.Equal(["1:20"], expected);
        Assert.Equal(expected, actual);
        spy.AssertExecutedMqlContains("\"$and\"");
    }

    // The genuine blocker of the EF-436 spec tests GroupJoin_aggregate_anonymous_key_selectors(2/_one_argument) is the
    // g.Sum(...) over the GroupJoin grouping (a correlated reducer), not the key: it fails in every mode even with a
    // simple key. Pinned so a future fix of that gap is noticed.
    [Theory]
    [MemberData(nameof(AllModes))]
    public void GroupJoin_with_Sum_over_the_grouping_still_fails_in_every_mode_regardless_of_key(MongoQueryMode mode)
    {
        using var db = CreateContext(mode, out _);

        Assert.Throws<InvalidOperationException>(() => db.Shipments
            .GroupJoin(db.Rates, s => s.Id, r => r.Zone, (s, g) => new { s.Id, Sum = g.Sum(r => r.Price) })
            .ToList());
        Assert.Throws<InvalidOperationException>(() => db.Shipments
            .GroupJoin(db.Rates, s => new { s.Region, s.Zone }, r => new { r.Region, r.Zone },
                (s, g) => new { s.Id, Sum = g.Sum(r => r.Price) })
            .ToList());
    }

    private (List<Shipment> Shipments, List<Rate> Rates, List<Slot> Slots) Materialize()
    {
        using var db = CreateContext(MongoQueryMode.DriverLinq, out _);
        return (db.Shipments.ToList(), db.Rates.ToList(), db.Slots.ToList());
    }

    public static IEnumerable<object[]> AllModesByObjectIdConfiguration()
        => from mode in new[] { MongoQueryMode.NativeOnly, MongoQueryMode.Native, MongoQueryMode.DriverLinq }
           from explicitInstances in new[] { false, true }
           select new object[] { mode, explicitInstances };

    [Theory]
    [MemberData(nameof(AllModesByObjectIdConfiguration))]
    public void String_key_stored_as_ObjectId_on_both_sides_stays_native_and_matches_oracle(
        MongoQueryMode mode, bool explicitInstances)
    {
        List<OidP> oidPs;
        List<OidQ> oidQs;
        using (var seed = CreateContext(MongoQueryMode.DriverLinq, out _))
        {
            oidPs = seed.OidPs.ToList();
            oidQs = seed.OidQs.ToList();
        }

        using var db = CreateContext(mode, out var spy);
        // HasConversion<ObjectId>() shares one cached converter instance; explicit `new StringToObjectIdConverter()`
        // per side does not.
        var joined = explicitInstances
            ? db.OidPs.Join(db.OidQs, p => new { p.Zone, p.Ref2 }, q => new { q.Zone, q.Ref2 }, (p, q) => new { p.Id, Q = q.Id })
            : db.OidPs.Join(db.OidQs, p => new { p.Zone, p.Ref }, q => new { q.Zone, q.Ref }, (p, q) => new { p.Id, Q = q.Id });
        var actual = joined.AsEnumerable().Select(x => $"{x.Id}:{x.Q}").OrderBy(x => x).ToList();
        var expected = oidPs
            .Join(oidQs, p => new { p.Zone, p.Ref }, q => new { q.Zone, q.Ref }, (p, q) => $"{p.Id}:{q.Id}")
            .OrderBy(x => x).ToList();

        Assert.Equal(["1:10"], expected);
        Assert.Equal(expected, actual);
        spy.AssertExecutedMqlContains("\"$and\"");
    }

    private (List<TagP>, List<TagQ>, List<KindP>, List<KindQ>, List<KindR>) MaterializeStorageShapes()
    {
        using var db = CreateContext(MongoQueryMode.DriverLinq, out _);
        return (db.TagPs.ToList(), db.TagQs.ToList(), db.KindPs.ToList(), db.KindQs.ToList(), db.KindRs.ToList());
    }

    private string? _collectionSuffix;

    private NativeAnonymousKeyJoinTestsContext CreateContext(MongoQueryMode mode, out SpyLoggerProvider spy)
    {
        if (_collectionSuffix == null)
        {
            _collectionSuffix = Guid.NewGuid().ToString("N")[..8];
            database.MongoDatabase.GetCollection<BsonDocument>("Shipments" + _collectionSuffix).InsertMany(ShipmentDocuments);
            database.MongoDatabase.GetCollection<BsonDocument>("Rates" + _collectionSuffix).InsertMany(RateDocuments);
            database.MongoDatabase.GetCollection<BsonDocument>("Slots" + _collectionSuffix).InsertMany(SlotDocuments);
            database.MongoDatabase.GetCollection<BsonDocument>("TagPs" + _collectionSuffix).InsertMany(TagPDocuments);
            database.MongoDatabase.GetCollection<BsonDocument>("TagQs" + _collectionSuffix).InsertMany(TagQDocuments);
            database.MongoDatabase.GetCollection<BsonDocument>("KindPs" + _collectionSuffix).InsertMany(KindPDocuments);
            database.MongoDatabase.GetCollection<BsonDocument>("KindQs" + _collectionSuffix).InsertMany(KindQDocuments);
            database.MongoDatabase.GetCollection<BsonDocument>("KindRs" + _collectionSuffix).InsertMany(KindRDocuments);
            database.MongoDatabase.GetCollection<BsonDocument>("ConvPs" + _collectionSuffix).InsertMany(ConvPDocuments);
            database.MongoDatabase.GetCollection<BsonDocument>("ConvQs" + _collectionSuffix).InsertMany(ConvQDocuments);
            database.MongoDatabase.GetCollection<BsonDocument>("OidPs" + _collectionSuffix).InsertMany(OidPDocuments);
            database.MongoDatabase.GetCollection<BsonDocument>("OidQs" + _collectionSuffix).InsertMany(OidQDocuments);
        }

        var (loggerFactory, provider) = SpyLoggerProvider.Create();
        spy = provider;
        return new NativeAnonymousKeyJoinTestsContext(database, _collectionSuffix, mode, loggerFactory);
    }

    private sealed class NativeAnonymousKeyJoinTestsContext(
        TemporaryDatabaseFixture database, string suffix, MongoQueryMode mode, ILoggerFactory loggerFactory)
        : DbContext(new DbContextOptionsBuilder<NativeAnonymousKeyJoinTestsContext>()
            .UseMongoDB(database.Client, database.MongoDatabase.DatabaseNamespace.DatabaseName, b => b.UseQueryMode(mode))
            .UseLoggerFactory(loggerFactory)
            .EnableSensitiveDataLogging()
            .ReplaceService<IModelCacheKeyFactory, IgnoreCacheKeyFactory>()
            .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
            .Options)
    {
        public DbSet<Shipment> Shipments => Set<Shipment>();
        public DbSet<Rate> Rates => Set<Rate>();
        public DbSet<Slot> Slots => Set<Slot>();
        public DbSet<TagP> TagPs => Set<TagP>();
        public DbSet<TagQ> TagQs => Set<TagQ>();
        public DbSet<KindP> KindPs => Set<KindP>();
        public DbSet<KindQ> KindQs => Set<KindQ>();
        public DbSet<KindR> KindRs => Set<KindR>();
        public DbSet<ConvP> ConvPs => Set<ConvP>();
        public DbSet<ConvQ> ConvQs => Set<ConvQ>();
        public DbSet<OidP> OidPs => Set<OidP>();
        public DbSet<OidQ> OidQs => Set<OidQ>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<Shipment>().ToCollection("Shipments" + suffix);
            modelBuilder.Entity<Rate>().ToCollection("Rates" + suffix);
            modelBuilder.Entity<Slot>(b =>
            {
                b.ToCollection("Slots" + suffix);
                b.HasKey(s => new { s.Zone, s.Bay });
            });
            modelBuilder.Entity<TagP>().ToCollection("TagPs" + suffix);
            modelBuilder.Entity<TagQ>().ToCollection("TagQs" + suffix);
            modelBuilder.Entity<KindP>(b =>
            {
                b.ToCollection("KindPs" + suffix);
                b.Property(p => p.K).HasConversion<string>();
                b.Property(p => p.Code).HasBsonRepresentation(BsonType.String);
            });
            modelBuilder.Entity<KindQ>().ToCollection("KindQs" + suffix);
            modelBuilder.Entity<KindR>(b =>
            {
                b.ToCollection("KindRs" + suffix);
                b.Property(r => r.K).HasConversion<string>();
            });
            modelBuilder.Entity<ConvP>(b =>
            {
                b.ToCollection("ConvPs" + suffix);
                b.Property(p => p.Code).HasConversion(v => v.ToString(), v => int.Parse(v));
                b.Property(p => p.Flag).HasConversion(new BoolToStringConverter("N", "Y"));
                b.Property(p => p.Code2).HasConversion<TimesOneToStringConverter>();
            });
            modelBuilder.Entity<ConvQ>(b =>
            {
                b.ToCollection("ConvQs" + suffix);
                b.Property(q => q.Code).HasConversion(v => (v * 10).ToString(), v => int.Parse(v) / 10);
                b.Property(q => q.Flag).HasConversion(new BoolToStringConverter("F", "T"));
                b.Property(q => q.Code2).HasConversion<TimesTenToStringConverter>();
            });
            modelBuilder.Entity<OidP>(b =>
            {
                b.ToCollection("OidPs" + suffix);
                b.Property(p => p.Ref).HasConversion<ObjectId>();
                // An explicit instance per side, so equivalence rests on the stateless allow-list, not instance identity.
                b.Property(p => p.Ref2).HasConversion(new MongoDB.EntityFrameworkCore.Storage.ValueConversion.StringToObjectIdConverter());
            });
            modelBuilder.Entity<OidQ>(b =>
            {
                b.ToCollection("OidQs" + suffix);
                b.Property(q => q.Ref).HasConversion<ObjectId>();
                b.Property(q => q.Ref2).HasConversion(new MongoDB.EntityFrameworkCore.Storage.ValueConversion.StringToObjectIdConverter());
            });
        }
    }

    private sealed class IgnoreCacheKeyFactory : IModelCacheKeyFactory
    {
        private static int _count;
        public object Create(DbContext context, bool designTime) => Interlocked.Increment(ref _count);
    }
}
