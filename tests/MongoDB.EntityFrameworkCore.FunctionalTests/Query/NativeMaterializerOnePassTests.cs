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
using MongoDB.EntityFrameworkCore.Infrastructure;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// One-pass "deserialize is materialize" streaming: for a streaming-eligible whole-entity query the Aggregate output
/// serializer is a custom <c>IBsonSerializer&lt;TEntity&gt;</c>, so the cursor yields finished (and, when tracked,
/// tracked) entities in a single reader pass.
/// <para>
/// Covers no-track parity with the DOM path, tracked round-trips, owned reference + collection values, wide
/// mixed-type rows, IDisposable entities, and the required-but-missing exception.
/// </para>
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeMaterializerOnePassTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    private class SimpleItem
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
        public int Count { get; set; }
        public bool Active { get; set; }
    }

    private class RequiredScalar
    {
        public ObjectId Id { get; set; }
        public int Score { get; set; } // required non-nullable scalar
    }

    private class OrderRoot
    {
        public ObjectId Id { get; set; }
        public string Customer { get; set; } = "";
        public Address ShipTo { get; set; } = null!;     // owned reference sub-document
        public List<LineItem> Lines { get; set; } = new(); // owned collection
    }

    private class Address
    {
        public string City { get; set; } = "";
        public string Zip { get; set; } = "";
    }

    private class LineItem
    {
        public string Sku { get; set; } = "";
        public int Qty { get; set; }
    }

    private static SingleEntityDbContext<T> CreateContext<T>(
        IMongoCollection<T> collection, MongoQueryMode mode, Action<ModelBuilder>? model = null)
        where T : class
        => SingleEntityDbContext.Create(
            collection,
            modelBuilderAction: model,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });

    // ── (a) whole-entity no-track: streamed set == DOM AsNoTracking set, and native ──────

    [Fact]
    public void Whole_entity_no_track_streamed_equals_DOM_and_succeeds_under_NativeOnly()
    {
        var collection = database.CreateCollection<SimpleItem>();
        collection.InsertMany(Enumerable.Range(0, 25).Select(i => new SimpleItem
        {
            Id = ObjectId.GenerateNewId(),
            Name = "item-" + i,
            Count = i,
            Active = i % 2 == 0
        }));

        List<SimpleItem> driver;
        using (var ctx = CreateContext(collection, MongoQueryMode.DriverLinq))
        {
            driver = ctx.Entities.AsNoTracking().OrderBy(e => e.Count).ToList();
        }

        List<SimpleItem> native;
        using (var ctx = CreateContext(collection, MongoQueryMode.Native))
        {
            native = ctx.Entities.AsNoTracking().OrderBy(e => e.Count).ToList();
        }

        Assert.Equal(driver.Count, native.Count);
        foreach (var (d, n) in driver.Zip(native))
        {
            Assert.Equal(d.Id, n.Id);
            Assert.Equal(d.Name, n.Name);
            Assert.Equal(d.Count, n.Count);
            Assert.Equal(d.Active, n.Active);
        }

        // Genuinely native (streaming one-pass); a fallback shape would throw under NativeOnly.
        using (var ctx = CreateContext(collection, MongoQueryMode.NativeOnly))
        {
            Assert.Equal(25, ctx.Entities.AsNoTracking().ToList().Count);
        }
    }

    // ── (b) whole-entity tracked: entities are tracked; mutate + SaveChanges round-trips ───────────
    // The driver materializes (and tracks) cursor batch 1 inside collection.Aggregate(...), so the state manager
    // must be initialized before that call or this NREs.

    [Fact]
    public void Whole_entity_tracked_streams_tracked_entities_and_round_trips()
    {
        var collection = database.CreateCollection<SimpleItem>();
        collection.InsertMany(Enumerable.Range(0, 10).Select(i => new SimpleItem
        {
            Id = ObjectId.GenerateNewId(),
            Name = "row-" + i,
            Count = i,
            Active = true
        }));

        ObjectId targetId;
        using (var ctx = CreateContext(collection, MongoQueryMode.Native))
        {
            var loaded = ctx.Entities.OrderBy(e => e.Count).ToList();
            Assert.Equal(10, loaded.Count);

            // Tracked: the change tracker holds an entry per row.
            Assert.Equal(10, ctx.ChangeTracker.Entries<SimpleItem>().Count());

            var first = loaded[0];
            targetId = first.Id;
            first.Name = "mutated";
            ctx.SaveChanges();
        }

        // Re-read (no tracking) and confirm the mutation persisted.
        using (var ctx = CreateContext(collection, MongoQueryMode.Native))
        {
            var reread = ctx.Entities.AsNoTracking().Single(e => e.Id == targetId);
            Assert.Equal("mutated", reread.Name);
        }
    }

    // ── (c) owned reference AND owned collection materialize correct nested values ─────────────────
    // Mixes an owned reference (ShipTo) with an owned collection (Lines); asserts Native == DriverLinq nested values
    // and that the shape routes native.

    [Fact]
    public void Owned_reference_and_owned_collection_materialize_correct_nested_values()
    {
        var collection = database.CreateCollection<OrderRoot>();

        Action<ModelBuilder> model = b =>
        {
            b.Entity<OrderRoot>().OwnsOne(o => o.ShipTo);
            b.Entity<OrderRoot>().OwnsMany(o => o.Lines);
        };

        var id1 = ObjectId.GenerateNewId();
        var id2 = ObjectId.GenerateNewId();

        // Seed via a DbContext so the owned nested shapes are written with the EF-configured element names.
        using (var seed = CreateContext(collection, MongoQueryMode.DriverLinq, model))
        {
            seed.Entities.AddRange(
                new OrderRoot
                {
                    Id = id1,
                    Customer = "Acme",
                    ShipTo = new Address { City = "London", Zip = "EC1" },
                    Lines = new List<LineItem>
                    {
                        new() { Sku = "A1", Qty = 2 },
                        new() { Sku = "B2", Qty = 5 }
                    }
                },
                new OrderRoot
                {
                    Id = id2,
                    Customer = "Globex",
                    ShipTo = new Address { City = "Paris", Zip = "75001" },
                    Lines = new List<LineItem> { new() { Sku = "C3", Qty = 1 } }
                });
            seed.SaveChanges();
        }

        static void AssertNesting(List<OrderRoot> rows, ObjectId acmeId)
        {
            Assert.Equal(2, rows.Count);

            var acme = rows.Single(o => o.Customer == "Acme");
            Assert.Equal(acmeId, acme.Id);
            Assert.NotNull(acme.ShipTo);
            Assert.Equal("London", acme.ShipTo.City);
            Assert.Equal("EC1", acme.ShipTo.Zip);
            Assert.Equal(2, acme.Lines.Count);
            Assert.Equal(new[] { "A1", "B2" }, acme.Lines.Select(l => l.Sku).OrderBy(s => s).ToArray());
            Assert.Equal(7, acme.Lines.Sum(l => l.Qty));

            var globex = rows.Single(o => o.Customer == "Globex");
            Assert.Equal("Paris", globex.ShipTo.City);
            Assert.Single(globex.Lines);
            Assert.Equal("C3", globex.Lines[0].Sku);
            Assert.Equal(1, globex.Lines[0].Qty);
        }

        using (var ctx = CreateContext(collection, MongoQueryMode.DriverLinq, model))
        {
            AssertNesting(ctx.Entities.AsNoTracking().OrderBy(o => o.Customer).ToList(), id1);
        }

        using (var ctx = CreateContext(collection, MongoQueryMode.Native, model))
        {
            AssertNesting(ctx.Entities.AsNoTracking().OrderBy(o => o.Customer).ToList(), id1);
        }

        // Proves the mixed owned shape routes native.
        using (var nativeOnly = CreateContext(collection, MongoQueryMode.NativeOnly, model))
        {
            AssertNesting(nativeOnly.Entities.AsNoTracking().ToList(), id1);
        }
    }

    // ── (e) wide mixed-type entity: every typed read (typed-generic + boxed-fallback) is byte-identical ──
    // Guards against a mis-positioned reader or wrong typed cast silently corrupting a value. Covers every primitive
    // and its nullable, a non-default BsonRepresentation (int stored as string), and a value converter.

    private class WideEntity
    {
        public ObjectId Id { get; set; }
        public int IntVal { get; set; }
        public long LongVal { get; set; }
        public string StrVal { get; set; } = "";
        public bool BoolVal { get; set; }
        public double DblVal { get; set; }
        public decimal DecVal { get; set; }
        public int? NIntVal { get; set; }
        public long? NLongVal { get; set; }
        public bool? NBoolVal { get; set; }
        public double? NDblVal { get; set; }
        public decimal? NDecVal { get; set; }
        public string? NStrVal { get; set; }
        public int IntAsString { get; set; }      // non-default BsonRepresentation (stored as string)
        public string Converted { get; set; } = ""; // value-converter backed
    }

    [Fact]
    public void Wide_mixed_type_entity_streams_byte_identical_values_and_is_native()
    {
        var collection = database.CreateCollection<WideEntity>();

        Action<ModelBuilder> model = b =>
        {
            b.Entity<WideEntity>().Property(e => e.IntAsString).HasBsonRepresentation(BsonType.String);
            b.Entity<WideEntity>().Property(e => e.Converted)
                .HasConversion(v => "d:" + v, s => s.Substring(2));
        };

        var rows = new List<WideEntity>
        {
            new()
            {
                Id = ObjectId.GenerateNewId(),
                IntVal = -2147483648, LongVal = 9223372036854775807L, StrVal = "alpha",
                BoolVal = true, DblVal = 3.5, DecVal = 123.456m,
                NIntVal = 42, NLongVal = -1L, NBoolVal = false, NDblVal = -0.25, NDecVal = 9.99m,
                NStrVal = "present", IntAsString = 777, Converted = "hello"
            },
            new()
            {
                Id = ObjectId.GenerateNewId(),
                IntVal = 2147483647, LongVal = -9223372036854775808L, StrVal = "beta",
                BoolVal = false, DblVal = 0.0, DecVal = 0m,
                NIntVal = null, NLongVal = null, NBoolVal = null, NDblVal = null, NDecVal = null,
                NStrVal = null, IntAsString = 0, Converted = "world"
            }
        };

        using (var seed = CreateContext(collection, MongoQueryMode.DriverLinq, model))
        {
            seed.Entities.AddRange(rows);
            seed.SaveChanges();
        }

        static void AssertEqualRow(WideEntity e, WideEntity a)
        {
            Assert.Equal(e.Id, a.Id);
            Assert.Equal(e.IntVal, a.IntVal);
            Assert.Equal(e.LongVal, a.LongVal);
            Assert.Equal(e.StrVal, a.StrVal);
            Assert.Equal(e.BoolVal, a.BoolVal);
            Assert.Equal(e.DblVal, a.DblVal);
            Assert.Equal(e.DecVal, a.DecVal);
            Assert.Equal(e.NIntVal, a.NIntVal);
            Assert.Equal(e.NLongVal, a.NLongVal);
            Assert.Equal(e.NBoolVal, a.NBoolVal);
            Assert.Equal(e.NDblVal, a.NDblVal);
            Assert.Equal(e.NDecVal, a.NDecVal);
            Assert.Equal(e.NStrVal, a.NStrVal);
            Assert.Equal(e.IntAsString, a.IntAsString);
            Assert.Equal(e.Converted, a.Converted);
        }

        // Native (streaming one-pass) values must be byte-identical to the seed and to the DOM path.
        List<WideEntity> native;
        using (var ctx = CreateContext(collection, MongoQueryMode.Native, model))
        {
            native = ctx.Entities.AsNoTracking().OrderBy(e => e.StrVal).ToList();
        }

        List<WideEntity> driver;
        using (var ctx = CreateContext(collection, MongoQueryMode.DriverLinq, model))
        {
            driver = ctx.Entities.AsNoTracking().OrderBy(e => e.StrVal).ToList();
        }

        Assert.Equal(2, native.Count);
        var expected = rows.OrderBy(e => e.StrVal).ToList();
        for (var i = 0; i < expected.Count; i++)
        {
            AssertEqualRow(expected[i], native[i]);
            AssertEqualRow(driver[i], native[i]);
        }

        // Genuinely native (streaming one-pass); a fallback shape would throw under NativeOnly.
        using (var ctx = CreateContext(collection, MongoQueryMode.NativeOnly, model))
        {
            Assert.Equal(2, ctx.Entities.AsNoTracking().ToList().Count);
        }
    }

    // ── (f) an IDisposable entity type must NOT be disposed by ReleaseCurrentRow ─────────────────────
    // With one-pass streaming the shaper is identity, so _currentRow and Current are the same reference;
    // ReleaseCurrentRow must not dispose a mapped entity (possibly now owned by the state manager). Only the
    // RawBsonDocument fallback row type may be released.

    private class DisposableItem : IDisposable
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
        public int Value { get; set; }

        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    [Fact]
    public void IDisposable_entity_is_not_disposed_by_one_pass_streaming_no_tracking()
    {
        var collection = database.CreateCollection<DisposableItem>();
        collection.InsertMany(Enumerable.Range(0, 5).Select(i => new DisposableItem
        {
            Id = ObjectId.GenerateNewId(),
            Name = "item-" + i,
            Value = i
        }));

        List<DisposableItem> native;
        using (var ctx = CreateContext(collection, MongoQueryMode.Native))
        {
            native = ctx.Entities.AsNoTracking().OrderBy(e => e.Value).ToList();
        }

        Assert.Equal(5, native.Count);
        for (var i = 0; i < native.Count; i++)
        {
            Assert.Equal("item-" + i, native[i].Name);
            Assert.Equal(i, native[i].Value);
            Assert.False(native[i].Disposed);
        }

        // Native one-pass (where _currentRow is the returned entity); a fallback shape would throw under NativeOnly.
        using (var ctx = CreateContext(collection, MongoQueryMode.NativeOnly))
        {
            var nativeOnly = ctx.Entities.AsNoTracking().OrderBy(e => e.Value).ToList();
            Assert.Equal(5, nativeOnly.Count);
            Assert.All(nativeOnly, e => Assert.False(e.Disposed));
        }
    }

    [Fact]
    public void IDisposable_entity_is_not_disposed_by_one_pass_streaming_tracked()
    {
        var collection = database.CreateCollection<DisposableItem>();
        collection.InsertMany(Enumerable.Range(0, 5).Select(i => new DisposableItem
        {
            Id = ObjectId.GenerateNewId(),
            Name = "row-" + i,
            Value = i
        }));

        using var ctx = CreateContext(collection, MongoQueryMode.Native);
        var tracked = ctx.Entities.OrderBy(e => e.Value).ToList();

        Assert.Equal(5, tracked.Count);
        Assert.Equal(5, ctx.ChangeTracker.Entries<DisposableItem>().Count());
        Assert.All(tracked, e => Assert.False(e.Disposed));
    }

    // ── (d) required-but-missing field throws the same exception the DOM path throws ───────────────

    [Fact]
    public void Missing_required_field_throws_same_as_DOM()
    {
        var collection = database.CreateCollection<RequiredScalar>();
        var raw = database.GetCollection<BsonDocument>(collection.CollectionNamespace);
        // Document is MISSING the required "Score" element entirely.
        raw.InsertOne(new BsonDocument { { "_id", ObjectId.GenerateNewId() } });

        InvalidOperationException driverEx;
        using (var driver = CreateContext(collection, MongoQueryMode.DriverLinq))
        {
            driverEx = Assert.Throws<InvalidOperationException>(() => driver.Entities.AsNoTracking().ToList());
        }

        InvalidOperationException nativeEx;
        using (var native = CreateContext(collection, MongoQueryMode.Native))
        {
            nativeEx = Assert.Throws<InvalidOperationException>(() => native.Entities.AsNoTracking().ToList());
        }

        Assert.Equal(driverEx.Message, nativeEx.Message);

        using (var nativeOnly = CreateContext(collection, MongoQueryMode.NativeOnly))
        {
            Assert.Throws<InvalidOperationException>(() => nativeOnly.Entities.AsNoTracking().ToList());
        }
    }
}
