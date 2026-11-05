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
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Infrastructure;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// An element <c>Select</c> over an owned collection (<c>Weights = e.Kids.Select(k =&gt; k.Weight).ToList()</c>) stages
/// the whole owned array and re-applies the <c>Select</c> over the materialized owned entities, so each element member
/// is read through its owned property's serializer (<c>NativeProjectionBinder.TryMatchOwnedArraySelectLeaf</c>).
/// </summary>
public class NativeOwnedArraySelectLeafTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    public class Kid
    {
        public string Label { get; set; } = "";

        [BsonRepresentation(BsonType.String)]
        public decimal Weight { get; set; }
    }

    public class Owner
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public List<Kid> Kids { get; set; } = [];
    }

    [Fact]
    public void Select_leaf_beside_the_whole_array_reads_through_the_owned_serializer()
        => NativeModeAssert.NativeAndExpected(
            mode =>
            {
                using var db = Create(Seed(), mode);
                var row = db.Entities.AsNoTracking().OrderBy(e => e.Id)
                    .Select(e => new { e.Kids, Weights = e.Kids.Select(k => k.Weight).ToList() }).First();
                return row.Weights.Select(w => $"{w}").Concat(row.Kids.Select(k => k.Label)).ToList();
            },
            ["10.5", "20.25", "a", "b"]);

    [Fact]
    public void Select_leaf_before_the_whole_array_shares_it()
        => NativeModeAssert.NativeAndExpected(
            mode =>
            {
                using var db = Create(Seed(), mode);
                var row = db.Entities.AsNoTracking().OrderBy(e => e.Id)
                    .Select(e => new { Labels = e.Kids.Select(k => k.Label).ToList(), e.Kids }).First();
                return row.Labels.Concat(row.Kids.Select(k => $"{k.Weight}")).ToList();
            },
            ["a", "b", "10.5", "20.25"]);

    [Fact]
    public void Select_leaf_alone_goes_native_and_reads_every_row()
        => NativeModeAssert.NativeAndExpected(
            mode =>
            {
                using var db = Create(Seed(), mode);
                return db.Entities.AsNoTracking().OrderBy(e => e.Id)
                    .Select(e => new { e.Name, Pairs = e.Kids.Select(k => new { k.Label, k.Weight }).ToList() })
                    .AsEnumerable().Select(r => $"{r.Name}:{string.Join(",", r.Pairs.Select(p => $"{p.Label}={p.Weight}"))}").ToList();
            },
            ["x:a=10.5,b=20.25", "y:"]);


    private static SingleEntityDbContext<Owner> Create(IMongoCollection<Owner> collection, MongoQueryMode mode)
        => SingleEntityDbContext.Create(
            collection,
            mb => mb.Entity<Owner>().OwnsMany(e => e.Kids, o => { o.HasElementName("Kids"); }),
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });

    private IMongoCollection<Owner> Seed()
    {
        var name = TemporaryDatabaseFixtureBase.CreateCollectionName("kids") + Guid.NewGuid().ToString("N")[..8];
        var collection = database.MongoDatabase.GetCollection<Owner>(name);
        using var db = Create(collection, MongoQueryMode.DriverLinq);
        db.Entities.AddRange(
            new Owner { Id = 1, Name = "x", Kids = [new Kid { Label = "a", Weight = 10.5m }, new Kid { Label = "b", Weight = 20.25m }] },
            new Owner { Id = 2, Name = "y", Kids = [] });
        db.SaveChanges();
        return collection;
    }
}
