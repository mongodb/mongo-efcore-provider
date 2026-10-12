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
using Microsoft.EntityFrameworkCore.Metadata.Builders;
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
    // K2: HasConversion<string>() here, an explicit EnumToStringConverter<Kind> instance on KindR.
    private class KindP
    {
        public int Id { get; set; }
        public int Zone { get; set; }
        public Kind K { get; set; }
        public int Code { get; set; }
        public Kind K2 { get; set; }
    }

    private class KindQ
    {
        public int Id { get; set; }
        public int Zone { get; set; }
        public Kind K { get; set; }
        public int Code { get; set; }
    }

    private class KindR
    {
        public int Id { get; set; }
        public int Zone { get; set; }
        public Kind K { get; set; }
        public Kind K2 { get; set; }
    }

    // Code: lambda converter to string ("7") on ConvP, a different lambda converter (x10, "70") on ConvQ. Flag:
    // BoolToStringConverter("N", "Y") on ConvP, ("F", "T") on ConvQ. Same converter types, different encodings.
    // Code3: the same non-capturing lambda converter (+100, "107") written out on both sides. Code4: a lambda converter
    // capturing a prefix ("c7") on both sides, through one helper: the same encoding, but each side's closure is its
    // own object.
    private class ConvP
    {
        public int Id { get; set; }
        public int Zone { get; set; }
        public int Code { get; set; }
        public bool Flag { get; set; }
        public int Code2 { get; set; }
        public int Code3 { get; set; }
        public int Code4 { get; set; }
    }

    private class ConvQ
    {
        public int Id { get; set; }
        public int Zone { get; set; }
        public int Code { get; set; }
        public bool Flag { get; set; }
        public int Code2 { get; set; }
        public int Code3 { get; set; }
        public int Code4 { get; set; }
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
        new() { { "_id", 1 }, { "Zone", 1 }, { "K", "B" }, { "Code", "7" }, { "K2", "B" } },
    ];

    private static readonly BsonDocument[] KindQDocuments =
    [
        new() { { "_id", 10 }, { "Zone", 1 }, { "K", 1 }, { "Code", 7 } },
        new() { { "_id", 11 }, { "Zone", 1 }, { "K", 0 }, { "Code", 8 } },
    ];

    private static readonly BsonDocument[] KindRDocuments =
    [
        new() { { "_id", 20 }, { "Zone", 1 }, { "K", "B" }, { "K2", "B" } },
        new() { { "_id", 21 }, { "Zone", 1 }, { "K", "A" }, { "K2", "A" } },
    ];

    private static readonly BsonDocument[] ConvPDocuments =
    [
        new() { { "_id", 1 }, { "Zone", 1 }, { "Code", "7" }, { "Flag", "Y" }, { "Code2", "7" }, { "Code3", "107" }, { "Code4", "c7" } },
    ];

    private static readonly BsonDocument[] ConvQDocuments =
    [
        new() { { "_id", 10 }, { "Zone", 1 }, { "Code", "70" }, { "Flag", "T" }, { "Code2", "70" }, { "Code3", "107" }, { "Code4", "c7" } },
        new() { { "_id", 11 }, { "Zone", 1 }, { "Code", "80" }, { "Flag", "F" }, { "Code2", "80" }, { "Code3", "108" }, { "Code4", "c8" } },
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

        Assert.Equal([(1, 10), (1, 11), (2, 12), (3, 13), (4, 14), (4, 15)], expected);
        Assert.Equal(expected, actual);

        spy.AssertExecutedMqlContains("\"$and\"");
        spy.AssertExecutedMqlContains("\"k0\" : { \"$ifNull\" : [\"$Region\", null] }");
        spy.AssertExecutedMqlContains("\"$eq\" : [{ \"$ifNull\" : [\"$Region\", null] }, \"$$k0\"]");
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
    [InlineData("CapturingLambdaConverters")]
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
            "CapturingLambdaConverters" => db.ConvPs
                .Join(db.ConvQs, p => new { p.Zone, p.Code4 }, q => new { q.Zone, q.Code4 }, (p, q) => new { p.Id, Q = q.Id })
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
            "NonAnonymousKey" => shipments
                .Join(rates, s => new KeyHolder { Region = s.Region }, r => new KeyHolder { Region = r.Region },
                    (s, r) => $"{s.Id}:{r.Id}"),
            "NestedAnonymous" => shipments
                .Join(rates, s => new { s.Zone, N = new { s.Region } }, r => new { r.Zone, N = new { r.Region } },
                    (s, r) => $"{s.Id}:{r.Id}"),
            "PrimitiveCollection" => tagPs
                .Join(tagQs, p => new { p.Tags }, q => new { q.Tags }, (p, q) => $"{p.Id}:{q.Id}"),
            "PrimitiveCollectionWithScalar" => tagPs
                .Join(tagQs, p => new { p.Zone, p.Tags }, q => new { q.Zone, q.Tags }, (p, q) => $"{p.Id}:{q.Id}"),
            "OneSideConverted" => kindPs
                .Join(kindQs, p => new { p.Zone, p.K }, q => new { q.Zone, q.K }, (p, q) => $"{p.Id}:{q.Id}"),
            "RepresentationMismatch" => kindPs
                .Join(kindQs, p => new { p.Zone, p.Code }, q => new { q.Zone, q.Code }, (p, q) => $"{p.Id}:{q.Id}"),
            // Code 7 is stored "7" on one side and "70" on the other; Flag true is "Y" vs "T".
            "DifferentLambdaConverters" => convPs
                .Join(convQs, p => new { p.Zone, p.Code }, q => new { q.Zone, q.Code }, (p, q) => $"{p.Id}:{q.Id}"),
            "DifferentBoolToStringConverters" => convPs
                .Join(convQs, p => new { p.Zone, p.Flag }, q => new { q.Zone, q.Flag }, (p, q) => $"{p.Id}:{q.Id}"),
            // Code4 7 is stored "c7" on both sides, but the converters' expressions each capture their own closure
            // object, so they aren't structurally equal and the pair declines (conservatively).
            "CapturingLambdaConverters" => convPs
                .Join(convQs, p => new { p.Zone, p.Code4 }, q => new { q.Zone, q.Code4 }, (p, q) => $"{p.Id}:{q.Id}"),
            _ => convPs
                .Join(convQs, p => new { p.Zone, p.Code2 }, q => new { q.Zone, q.Code2 }, (p, q) => $"{p.Id}:{q.Id}"),
        }).OrderBy(x => x).ToList();

        switch (shape)
        {
            case "PrimitiveCollection" or "PrimitiveCollectionWithScalar":
                Assert.Empty(expected);
                break;
            case "OneSideConverted" or "RepresentationMismatch" or "DifferentLambdaConverters" or "DifferentBoolToStringConverters"
                or "DifferentConverterTypes" or "CapturingLambdaConverters":
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

    // K2 is HasConversion<string>() on KindP (no configured converter instance, only a provider-type annotation) and
    // an explicit `new EnumToStringConverter<Kind>()` on KindR (a converter, no annotation): the same effective converter
    // type with structurally equal expressions, so both store "B" and the pair stays native.
    [Theory]
    [MemberData(nameof(AllModes))]
    public void Provider_type_conversion_and_explicit_equivalent_converter_stay_native_and_match_oracle(MongoQueryMode mode)
    {
        using var db = CreateContext(mode, out var spy);
        var (_, _, kindPs, _, kindRs) = MaterializeStorageShapes();

        var actual = db.KindPs
            .Join(db.KindRs, p => new { p.Zone, p.K2 }, r => new { r.Zone, r.K2 }, (p, r) => new { p.Id, R = r.Id })
            .AsEnumerable().Select(x => $"{x.Id}:{x.R}").OrderBy(x => x).ToList();
        var expected = kindPs
            .Join(kindRs, p => new { p.Zone, p.K2 }, r => new { r.Zone, r.K2 }, (p, r) => $"{p.Id}:{r.Id}")
            .OrderBy(x => x).ToList();

        Assert.Equal(["1:20"], expected);
        Assert.Equal(expected, actual);
        spy.AssertExecutedMqlContains("\"$and\"");
    }

    // Code3 carries the same lambda converter (v => (v + 100).ToString()) written separately on each entity type: two
    // converter instances whose to/from-provider expressions are structurally equal, so the pair stays native.
    [Theory]
    [MemberData(nameof(AllModes))]
    public void Identical_lambda_converters_on_both_sides_stay_native_and_match_oracle(MongoQueryMode mode)
    {
        List<ConvP> convPs;
        List<ConvQ> convQs;
        using (var seed = CreateContext(MongoQueryMode.DriverLinq, out _))
        {
            convPs = seed.ConvPs.ToList();
            convQs = seed.ConvQs.ToList();
        }

        using var db = CreateContext(mode, out var spy);
        var actual = db.ConvPs
            .Join(db.ConvQs, p => new { p.Zone, p.Code3 }, q => new { q.Zone, q.Code3 }, (p, q) => new { p.Id, Q = q.Id })
            .AsEnumerable().Select(x => $"{x.Id}:{x.Q}").OrderBy(x => x).ToList();
        var expected = convPs
            .Join(convQs, p => new { p.Zone, p.Code3 }, q => new { q.Zone, q.Code3 }, (p, q) => $"{p.Id}:{q.Id}")
            .OrderBy(x => x).ToList();

        Assert.Equal(["1:10"], expected);
        Assert.Equal(expected, actual);
        spy.AssertExecutedMqlContains("\"$and\"");
    }

    // g.Sum(...) over a GroupJoin grouping is an aggregate over the keyed array lookup (native and bridge); a missing/null outer key
    // matches nothing, as the rewrite's `outerKey != null` guard says.
    [Theory]
    [InlineData(MongoQueryMode.NativeOnly)]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.DriverLinq)]
    public void GroupJoin_with_Sum_over_the_grouping_matches_the_in_memory_oracle(MongoQueryMode mode)
    {
        var (shipments, rates, _) = Materialize();
        using var db = CreateContext(mode, out _);

        var actual = db.Shipments
            .GroupJoin(db.Rates, s => s.Id, r => r.Zone, (s, g) => new { s.Id, Sum = g.Sum(r => r.Price) })
            .OrderBy(x => x.Id).ToList();
        var expected = shipments
            .GroupJoin(rates, s => s.Id, r => r.Zone, (s, g) => new { s.Id, Sum = g.Sum(r => r.Price) })
            .OrderBy(x => x.Id).ToList();

        Assert.Equal(expected, actual);
        Assert.Contains(actual, x => x.Sum > 0);
    }

    // ---- an aggregate over a GroupJoin group (or an explicit subquery) correlated on SEVERAL properties ----
    // EF10 rewrites a GroupJoin with an aggregate into a correlated subquery that guards EVERY outer part with `!= null`, so a shipment
    // with a null or missing part has an empty group (unlike the same key in a Join, where null equals null, and unlike LINQ-to-objects).
    // EF8 and EF9 keep the anonymous key whole in the rewrite (`Equals(new { s.A, s.B }, new { r.A, r.B })`), which is member-wise equality:
    // null equals null, as in LINQ-to-objects. The provider follows the tree each version hands it. An explicit subquery carries no guard on
    // any version, so C# semantics apply there: a null part equals a null part and a missing element reads as null.
    // Oracles are LINQ-to-objects over the same rows as materialized, written to those rules.

    private static bool GroupJoinGuardsNullKeyParts
    {
        get
        {
#if EF8 || EF9
            return false;
#else
            return true;
#endif
        }
    }

    [Theory]
    [MemberData(nameof(AllModes))]
    public void GroupJoin_Count_on_a_three_member_anonymous_key_matches_the_oracle(MongoQueryMode mode)
    {
        var (shipments, rates, _) = Materialize();
        using var db = CreateContext(mode, out var spy);

        var actual = db.Shipments
            .GroupJoin(db.Rates, s => new { s.Region, s.Zone, s.Bay }, r => new { r.Region, r.Zone, r.Bay }, (s, g) => new { s.Id, N = g.Count() })
            .OrderBy(x => x.Id).ToList();
        var expected = shipments
            .Select(s => new
            {
                s.Id,
                N = !GroupJoinGuardsNullKeyParts || (s.Region != null && s.Bay != null)
                    ? rates.Count(r => r.Region == s.Region && r.Zone == s.Zone && r.Bay == s.Bay)
                    : 0
            })
            .OrderBy(x => x.Id).ToList();

        // Guarded (EF10): only shipment 1 has no null part; shipments 2 (Bay), 3 and 4 (Region) match nothing although rates 12, 13, 14 and 15
        // carry the same nulls. Member-wise (EF8, EF9): those rates match, a missing element reading as null.
        Assert.Equal(GroupJoinGuardsNullKeyParts ? [2, 0, 0, 0, 0] : [2, 1, 1, 2, 0], expected.Select(x => x.N));
        Assert.Equal(expected, actual);

        // The composite lookup excludes a null outer value for the nullable guarded parts (Region = k0, Bay = k2), not for Zone (k1), whose
        // property cannot be null, and for none of them where the key is compared member-wise.
        var mql = spy.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery);
        Assert.DoesNotContain("{ \"$ne\" : [\"$$k1\", null] }", mql);
        if (GroupJoinGuardsNullKeyParts)
        {
            spy.AssertExecutedMqlContains("{ \"$ne\" : [\"$$k0\", null] }");
            spy.AssertExecutedMqlContains("{ \"$ne\" : [\"$$k2\", null] }");
        }
        else
        {
            Assert.DoesNotContain("{ \"$ne\" : [\"$$k0\", null] }", mql);
        }
    }

    [Theory]
    [MemberData(nameof(AllModes))]
    public void GroupJoin_Sum_and_Any_on_a_two_member_anonymous_key_match_the_oracle(MongoQueryMode mode)
    {
        var (shipments, rates, _) = Materialize();
        using var db = CreateContext(mode, out _);

        var actual = db.Shipments
            .GroupJoin(db.Rates, s => new { s.Region, s.Zone }, r => new { r.Region, r.Zone },
                (s, g) => new { s.Id, Total = g.Sum(r => r.Price), Any = g.Any(r => r.Price > 450) })
            .OrderBy(x => x.Id).ToList();
        var expected = shipments
            .Select(s => new
            {
                s.Id,
                Total = !GroupJoinGuardsNullKeyParts || s.Region != null
                    ? rates.Where(r => r.Region == s.Region && r.Zone == s.Zone).Sum(r => r.Price)
                    : 0,
                Any = (!GroupJoinGuardsNullKeyParts || s.Region != null)
                      && rates.Where(r => r.Region == s.Region && r.Zone == s.Zone).Any(r => r.Price > 450)
            })
            .OrderBy(x => x.Id).ToList();

        // Shipment 1 (N, 1): rates 10, 11, 16; shipment 2 (N, 2): rate 12. Shipments 3 and 4 have a null Region: no group where it is guarded
        // (EF10), rates 13 to 15 (1200) where it is not.
        Assert.Equal(GroupJoinGuardsNullKeyParts ? [801, 200, 0, 0, 0] : [801, 200, 1200, 1200, 0], expected.Select(x => x.Total));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [MemberData(nameof(AllModes))]
    public void Explicit_subquery_on_two_equalities_counts_by_both(MongoQueryMode mode)
    {
        var (shipments, rates, _) = Materialize();
        using var db = CreateContext(mode, out _);

        // No `!= null` guard here, so C# semantics: a null Region equals a null Region.
        var actual = db.Shipments
            .Select(s => new { s.Id, N = db.Rates.Where(r => r.Region == s.Region && r.Zone == s.Zone).Count() })
            .OrderBy(x => x.Id).ToList();
        var expected = shipments
            .Select(s => new { s.Id, N = rates.Where(r => r.Region == s.Region && r.Zone == s.Zone).Count() })
            .OrderBy(x => x.Id).ToList();

        Assert.Equal(expected, actual);
        Assert.Contains(expected, x => x.N > 1);
    }

    [Theory]
    [MemberData(nameof(AllModes))]
    public void Where_and_OrderBy_over_a_composite_correlated_aggregate(MongoQueryMode mode)
    {
        var (shipments, rates, _) = Materialize();
        using var db = CreateContext(mode, out _);

        var actual = db.Shipments
            .Where(s => db.Rates.Where(r => r.Region == s.Region && r.Zone == s.Zone && r.Bay == s.Bay).Any(r => r.Price > 100))
            .OrderBy(s => db.Rates.Where(r => r.Region == s.Region && r.Zone == s.Zone && r.Bay == s.Bay).Sum(r => r.Price)).ThenBy(s => s.Id)
            .Select(s => s.Id).ToList();
        var expected = shipments
            .Where(s => rates.Where(r => r.Region == s.Region && r.Zone == s.Zone && r.Bay == s.Bay).Any(r => r.Price > 100))
            .OrderBy(s => rates.Where(r => r.Region == s.Region && r.Zone == s.Zone && r.Bay == s.Bay).Sum(r => r.Price)).ThenBy(s => s.Id)
            .Select(s => s.Id).ToList();

        Assert.NotEmpty(expected);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [MemberData(nameof(AllModes))]
    public void A_filtered_group_on_a_composite_key_reads_its_parameter_per_execution(MongoQueryMode mode)
    {
        var (shipments, rates, _) = Materialize();
        using var db = CreateContext(mode, out _);

        // One context and one lambda shape, so the second run hits the compiled-query cache: a filter value baked into the plan would
        // repeat the first run's answer.
        foreach (var cutoff in new[] { 150, 450 })
        {
            var actual = db.Shipments
                .GroupJoin(db.Rates.Where(r => r.Price > cutoff), s => new { s.Region, s.Zone }, r => new { r.Region, r.Zone },
                    (s, g) => new { s.Id, N = g.Count() })
                .OrderBy(x => x.Id).ToList();
            var expected = shipments
                .Select(s => new
                {
                    s.Id,
                    N = !GroupJoinGuardsNullKeyParts || s.Region != null
                        ? rates.Where(r => r.Price > cutoff).Count(r => r.Region == s.Region && r.Zone == s.Zone)
                        : 0
                })
                .OrderBy(x => x.Id).ToList();

            Assert.Equal(expected, actual);
        }
    }

    [Theory]
    [MemberData(nameof(AllModes))]
    public void A_guard_on_one_part_of_an_explicit_composite_correlation_excludes_only_that_part(MongoQueryMode mode)
    {
        var (shipments, rates, _) = Materialize();
        using var db = CreateContext(mode, out _);

        // Region is guarded (a null Region matches nothing); Bay is not (a null or missing Bay equals a null or missing Bay).
        var actual = db.Shipments
            .Select(s => new
            {
                s.Id,
                N = db.Rates.Where(r => s.Region != null && r.Region == s.Region && r.Zone == s.Zone && r.Bay == s.Bay).Count()
            })
            .OrderBy(x => x.Id).ToList();
        var expected = shipments
            .Select(s => new
            {
                s.Id,
                N = rates.Where(r => s.Region != null && r.Region == s.Region && r.Zone == s.Zone && r.Bay == s.Bay).Count()
            })
            .OrderBy(x => x.Id).ToList();

        // Shipment 2 (N, 2, null Bay) matches rate 12 (null Bay): the unguarded part equates null with null.
        Assert.Equal([2, 1, 0, 0, 0], expected.Select(x => x.N));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(MongoQueryMode.NativeOnly)]
    [InlineData(MongoQueryMode.Native)]
    public void Element_leaves_over_a_composite_correlation_match_the_oracle(MongoQueryMode mode)
    {
        var (shipments, rates, _) = Materialize();
        using var db = CreateContext(mode, out _);

        var actual = db.Shipments
            .Select(s => new
            {
                s.Id,
                All = db.Rates.Where(r => r.Region == s.Region && r.Zone == s.Zone).OrderByDescending(r => r.Id).ToList(),
                Cheapest = db.Rates.Where(r => r.Region == s.Region && r.Zone == s.Zone).OrderBy(r => r.Price).FirstOrDefault()
            })
            .AsEnumerable().OrderBy(x => x.Id)
            .Select(x => (x.Id, string.Join(",", x.All.Select(r => r.Id)), x.Cheapest?.Id)).ToList();
        var expected = shipments
            .Select(s => (
                s.Id,
                string.Join(",", rates.Where(r => r.Region == s.Region && r.Zone == s.Zone).OrderByDescending(r => r.Id).Select(r => r.Id)),
                rates.Where(r => r.Region == s.Region && r.Zone == s.Zone).OrderBy(r => r.Price).FirstOrDefault()?.Id))
            .OrderBy(x => x.Item1).ToList();

        Assert.Contains(expected, x => x.Item2.Contains(','));
        Assert.Equal(expected, actual);
    }

    // A correlation that is not a clean conjunction of one equality and at most one null guard per compared outer property declines
    // (NativeOnly refuses; the fallback finds nothing to read), never answering by the part it did understand.
    [Theory]
    [InlineData("GuardOnAPropertyThatIsNotCompared")]
    [InlineData("TheSameOuterPropertyTwice")]
    [InlineData("ComparedToAConstant")]
    [InlineData("AnOrInsteadOfAnAnd")]
    public void Malformed_composite_correlations_decline_loudly(string shape)
    {
        using var db = CreateContext(MongoQueryMode.NativeOnly, out _);

        Assert.ThrowsAny<Exception>(() => (shape switch
        {
            "GuardOnAPropertyThatIsNotCompared" => db.Shipments.Select(s => new
            {
                s.Id, N = db.Rates.Where(r => r.Region == s.Region && r.Zone == s.Zone && s.Bay != null).Count()
            }).AsEnumerable().Select(x => x.N),
            "TheSameOuterPropertyTwice" => db.Shipments.Select(s => new
            {
                s.Id, N = db.Rates.Where(r => r.Region == s.Region && r.Region == s.Region && r.Zone == s.Zone).Count()
            }).AsEnumerable().Select(x => x.N),
            "ComparedToAConstant" => db.Shipments.Select(s => new
            {
                s.Id, N = db.Rates.Where(r => r.Region == s.Region && r.Zone == 1).Count()
            }).AsEnumerable().Select(x => x.N),
            _ => db.Shipments.Select(s => new
            {
                s.Id, N = db.Rates.Where(r => r.Region == s.Region || r.Zone == s.Zone).Count()
            }).AsEnumerable().Select(x => x.N)
        }).ToList());
    }

    [Theory]
    [MemberData(nameof(AllModes))]
    public void A_further_conjunct_in_the_correlation_is_never_dropped(MongoQueryMode mode)
    {
        using var db = CreateContext(mode, out _);

        // `r.Price > 100` is not part of the key, so the correlation is not recognized: every mode refuses rather than count by the keys alone.
        Assert.ThrowsAny<Exception>(() => db.Shipments
            .Select(s => new { s.Id, N = db.Rates.Where(r => r.Region == s.Region && r.Zone == s.Zone && r.Price > 100).Count() })
            .AsEnumerable().ToList());
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
                b.Property(p => p.K2).HasConversion<string>();
            });
            modelBuilder.Entity<KindQ>().ToCollection("KindQs" + suffix);
            modelBuilder.Entity<KindR>(b =>
            {
                b.ToCollection("KindRs" + suffix);
                b.Property(r => r.K).HasConversion<string>();
                b.Property(r => r.K2).HasConversion(new EnumToStringConverter<Kind>());
            });
            modelBuilder.Entity<ConvP>(b =>
            {
                b.ToCollection("ConvPs" + suffix);
                b.Property(p => p.Code).HasConversion(v => v.ToString(), v => int.Parse(v));
                b.Property(p => p.Flag).HasConversion(new BoolToStringConverter("N", "Y"));
                b.Property(p => p.Code2).HasConversion<TimesOneToStringConverter>();
                b.Property(p => p.Code3).HasConversion(v => (v + 100).ToString(), v => int.Parse(v) - 100);
                HasPrefixedConversion(b.Property(p => p.Code4), "c");
            });
            modelBuilder.Entity<ConvQ>(b =>
            {
                b.ToCollection("ConvQs" + suffix);
                b.Property(q => q.Code).HasConversion(v => (v * 10).ToString(), v => int.Parse(v) / 10);
                b.Property(q => q.Flag).HasConversion(new BoolToStringConverter("F", "T"));
                b.Property(q => q.Code2).HasConversion<TimesTenToStringConverter>();
                b.Property(q => q.Code3).HasConversion(v => (v + 100).ToString(), v => int.Parse(v) - 100);
                HasPrefixedConversion(b.Property(q => q.Code4), "c");
            });
            modelBuilder.Entity<OidP>(b =>
            {
                b.ToCollection("OidPs" + suffix);
                b.Property(p => p.Ref).HasConversion<ObjectId>();
                // An explicit instance per side, so equivalence rests on structural converter equality, not instance identity.
                b.Property(p => p.Ref2).HasConversion(new MongoDB.EntityFrameworkCore.Storage.ValueConversion.StringToObjectIdConverter());
            });
            modelBuilder.Entity<OidQ>(b =>
            {
                b.ToCollection("OidQs" + suffix);
                b.Property(q => q.Ref).HasConversion<ObjectId>();
                b.Property(q => q.Ref2).HasConversion(new MongoDB.EntityFrameworkCore.Storage.ValueConversion.StringToObjectIdConverter());
            });
        }

        private static void HasPrefixedConversion(PropertyBuilder<int> property, string prefix)
            => property.HasConversion(v => prefix + v, v => int.Parse(v.Substring(prefix.Length)));
    }

    private sealed class IgnoreCacheKeyFactory : IModelCacheKeyFactory
    {
        private static int _count;
        public object Create(DbContext context, bool designTime) => Interlocked.Increment(ref _count);
    }
}
