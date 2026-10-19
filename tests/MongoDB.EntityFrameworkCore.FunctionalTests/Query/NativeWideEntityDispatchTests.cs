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
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Infrastructure;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// Element-name dispatch of the streaming materializer's fill loop on a wide entity: elements arrive in any order,
/// some are absent, unknown ones are skipped, and owned references/collections are dispatched by the same names.
/// Pins behavior that must hold whatever the dispatch strategy (linear chain or switch).
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeWideEntityDispatchTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    public class WideRow
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
    public int? P01 { get; set; }
    public int? P02 { get; set; }
    public int? P03 { get; set; }
    public int? P04 { get; set; }
    public int? P05 { get; set; }
    public int? P06 { get; set; }
    public int? P07 { get; set; }
    public int? P08 { get; set; }
    public int? P09 { get; set; }
    public int? P10 { get; set; }
    public int? P11 { get; set; }
    public int? P12 { get; set; }
    public int? P13 { get; set; }
    public int? P14 { get; set; }
    public int? P15 { get; set; }
    public int? P16 { get; set; }
    public int? P17 { get; set; }
    public int? P18 { get; set; }
    public int? P19 { get; set; }
    public int? P20 { get; set; }
    public int? P21 { get; set; }
    public int? P22 { get; set; }
    public int? P23 { get; set; }
    public int? P24 { get; set; }
    public int? P25 { get; set; }
    public int? P26 { get; set; }
    public int? P27 { get; set; }
    public int? P28 { get; set; }
    public int? P29 { get; set; }
    public int? P30 { get; set; }
    public int? P31 { get; set; }
    public int? P32 { get; set; }
    public int? P33 { get; set; }
    public int? P34 { get; set; }
    public int? P35 { get; set; }
    public int? P36 { get; set; }
    public int? P37 { get; set; }
    public int? P38 { get; set; }
    public int? P39 { get; set; }
    public int? P40 { get; set; }
    public int? P41 { get; set; }
    public int? P42 { get; set; }
    public int? P43 { get; set; }
    public int? P44 { get; set; }
    public int? P45 { get; set; }
    public int? P46 { get; set; }
    public int? P47 { get; set; }
    public int? P48 { get; set; }
    public int? P49 { get; set; }
    public int? P50 { get; set; }
    public int? P51 { get; set; }
    public int? P52 { get; set; }
    public int? P53 { get; set; }
    public int? P54 { get; set; }
    public int? P55 { get; set; }
    public int? P56 { get; set; }
    public int? P57 { get; set; }
    public int? P58 { get; set; }
    public int? P59 { get; set; }
    public int? P60 { get; set; }
        public WideDetail Detail { get; set; }
        public List<WideTag> Tags { get; set; }
    }

    public class WideDetail
    {
        public int? D1 { get; set; }
        public string D2 { get; set; }
    }

    public class WideTag
    {
        public string Label { get; set; }
        public int? Weight { get; set; }
    }

    private const int PropertyCount = 60;

    private static readonly int[] GapProperties = [1, 17, 30, 60];

    private static readonly Action<ModelBuilder> WideModel = mb =>
    {
        var row = mb.Entity<WideRow>();
        row.OwnsOne(r => r.Detail);
        row.OwnsMany(r => r.Tags);
    };

    private static SingleEntityDbContext<WideRow> CreateContext(IMongoCollection<WideRow> collection, MongoQueryMode mode)
        => SingleEntityDbContext.Create(
            collection,
            modelBuilderAction: WideModel,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });

    private string UniqueCollectionName(string name)
        => TemporaryDatabaseFixtureBase.CreateCollectionName(name) + Guid.NewGuid().ToString("N")[..8];

    private static string PropName(int i) => $"P{i:00}";

    // Property i of row `seed` holds seed * 1000 + i.
    private static int ValueOf(int seed, int i) => seed * 1000 + i;

    // Elements are added in a seed-dependent shuffled order, absent ones are left out, and unknown elements
    // (a scalar and a sub-document) are interleaved. `missing` lists the property numbers to omit.
    private static BsonDocument Row(int seed, string name, int[] missing, bool shuffle)
    {
        var elements = new List<BsonElement>();
        for (var i = 1; i <= PropertyCount; i++)
        {
            if (!missing.Contains(i))
            {
                elements.Add(new BsonElement(PropName(i), ValueOf(seed, i)));
            }
        }

        elements.Add(new BsonElement("Name", name));
        elements.Add(new BsonElement("Detail", new BsonDocument { { "D2", "d-" + name }, { "Unknown", 1 }, { "D1", seed } }));
        elements.Add(new BsonElement("Tags", new BsonArray
        {
            new BsonDocument { { "Weight", 2 }, { "Extra", "x" }, { "Label", "b-" + name } },
            new BsonDocument { { "Label", "a-" + name }, { "Weight", 1 } }
        }));
        elements.Add(new BsonElement("NotMapped", 42));
        elements.Add(new BsonElement("AlsoNotMapped", new BsonDocument("Z", 1)));

        if (shuffle)
        {
            var random = new Random(seed);
            elements = elements.OrderBy(_ => random.Next()).ToList();
        }

        var doc = new BsonDocument("_id", ObjectId.GenerateNewId());
        doc.AddRange(elements);
        return doc;
    }

    private IMongoCollection<WideRow> Seed(string name)
    {
        var coll = database.MongoDatabase.GetCollection<BsonDocument>(UniqueCollectionName(name));
        coll.InsertMany([
            Row(1, "ordered", missing: [], shuffle: false),
            Row(2, "shuffled", missing: [], shuffle: true),
            Row(3, "gaps", GapProperties, shuffle: true)
        ]);
        return database.MongoDatabase.GetCollection<WideRow>(coll.CollectionNamespace.CollectionName);
    }

    // Guards against the model drifting and silently routing these queries to the DOM shaper, which would make
    // every NativeOnly leg vacuous.
    private static void AssertStreamingEligible(DbContext db)
        => Assert.True(
            StreamingEligibility.IsEligible(db.Model.FindEntityType(typeof(WideRow))!),
            "WideRow must be streaming-eligible or these tests do not exercise the streaming materializer.");

    private static int? Read(WideRow row, int i)
        => (int?)typeof(WideRow).GetProperty(PropName(i))!.GetValue(row);

    private static void AssertRows(List<WideRow> rows)
    {
        Assert.Equal(["gaps", "ordered", "shuffled"], rows.Select(r => r.Name).ToArray());
        var seeds = new Dictionary<string, int> { ["ordered"] = 1, ["shuffled"] = 2, ["gaps"] = 3 };
        foreach (var row in rows)
        {
            var seed = seeds[row.Name];
            var missing = row.Name == "gaps" ? GapProperties : [];
            for (var i = 1; i <= PropertyCount; i++)
            {
                Assert.Equal(missing.Contains(i) ? null : ValueOf(seed, i), Read(row, i));
            }

            Assert.Equal(seed, row.Detail.D1);
            Assert.Equal("d-" + row.Name, row.Detail.D2);
            Assert.Equal(["b-" + row.Name, "a-" + row.Name], row.Tags.Select(t => t.Label).ToArray());
            Assert.Equal([2, 1], row.Tags.Select(t => t.Weight).ToArray());
        }
    }

    [Theory]
    [InlineData(MongoQueryMode.NativeOnly)]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.DriverLinq)]
    public void Wide_entity_reads_every_property_whatever_the_element_order(MongoQueryMode mode)
    {
        var collection = Seed(nameof(Wide_entity_reads_every_property_whatever_the_element_order) + mode);

        using var db = CreateContext(collection, mode);
        AssertStreamingEligible(db);

        AssertRows(db.Entities.AsNoTracking().OrderBy(r => r.Name).ToList());
    }

    [Fact]
    public void Wide_entity_tracked_reads_every_property_whatever_the_element_order()
    {
        var collection = Seed(nameof(Wide_entity_tracked_reads_every_property_whatever_the_element_order));

        using var db = CreateContext(collection, MongoQueryMode.NativeOnly);

        AssertRows(db.Entities.OrderBy(r => r.Name).ToList());
    }

    public class CollidingRow
    {
        public ObjectId Id { get; set; }
        public string Scalar { get; set; } = "";
        public WideDetail Detail { get; set; }
    }

    [Fact]
    public void Two_members_with_one_element_name_are_rejected_before_materialization()
    {
        // The switch dispatch cannot hold duplicate cases and BuildDispatch keeps the last entry for a name, so a duplicate
        // element name must never reach the rewriter. Pin that the model rejects it.
        var collection = database.MongoDatabase.GetCollection<CollidingRow>(
            UniqueCollectionName(nameof(Two_members_with_one_element_name_are_rejected_before_materialization)));

        using var db = SingleEntityDbContext.Create(
            collection,
            modelBuilderAction: mb =>
            {
                var row = mb.Entity<CollidingRow>();
                row.Property(r => r.Scalar).HasElementName("Dup");
                row.OwnsOne(r => r.Detail).HasElementName("Dup");
            });

        var ex = Record.Exception(() => db.Entities.AsNoTracking().ToList());
        Assert.NotNull(ex);
        Assert.Contains("Dup", ex.ToString());
    }
}
