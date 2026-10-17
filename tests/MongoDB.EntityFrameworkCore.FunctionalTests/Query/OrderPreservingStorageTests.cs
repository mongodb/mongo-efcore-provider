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
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Infrastructure;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// EF-472: a value-converted property is refused for relational comparisons, sorting and <c>Min</c>/<c>Max</c> (EF-337) unless the model
/// declares its stored form order-preserving (<c>HasOrderPreservingStorage()</c>, <c>HaveOrderPreservingStorage()</c>) or the converter
/// is one of EF's stateless ticks / zero-one converters. The declaration covers a bare read of the property against a value; a sum, a
/// computation, or a key that mixes the property with another value is still refused.
/// </summary>
/// <remarks>
/// Stored forms are seeded as <see cref="BsonDocument"/> (the driver's POCO serializer ignores the converters). Each case runs under
/// <see cref="MongoQueryMode.Native"/>, <see cref="MongoQueryMode.NativeOnly"/> and <see cref="MongoQueryMode.DriverLinq"/> against an
/// oracle computed in memory over the materialized entities.
/// </remarks>
[XUnitCollection("QueryTests")]
public class OrderPreservingStorageTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    public readonly record struct Slot(long Value) : IComparable<Slot>
    {
        public int CompareTo(Slot other) => Value.CompareTo(other.Value);
    }

    public class SlotConverter() : ValueConverter<Slot, long>(s => s.Value, v => new Slot(v));

    public class Doc
    {
        public ObjectId Id { get; set; }
        public int Seq { get; set; }

        /// <summary>Stored as v * 2; declared order-preserving on the property.</summary>
        public int Declared { get; set; }

        /// <summary>Stored as v * 2; not declared.</summary>
        public int Undeclared { get; set; }

        /// <summary>A strongly-typed id stored as its long; declared for the type in ConfigureConventions.</summary>
        public Slot Strong { get; set; }

        /// <summary>EF's DateTimeToTicksConverter.</summary>
        public DateTime When { get; set; }

        /// <summary>EF's TimeSpanToTicksConverter.</summary>
        public TimeSpan Span { get; set; }

        /// <summary>EF's BoolToZeroOneConverter.</summary>
        public bool Flag { get; set; }
    }

    private static readonly int[] Values = [9, 10, 100, -5, 7];
    private static readonly long[] Slots = [30, 5, 100, 7, 1];
    private static readonly DateTime Epoch = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
    private static readonly bool[] Flags = [true, false, true, false, false];

    private static DateTime When(int i) => Epoch.AddDays(new[] { 40, 3, 400, 17, 90 }[i]);
    private static TimeSpan Span(int i) => TimeSpan.FromMinutes(new[] { 90, 5, 700, 33, 1 }[i]);

    private static readonly Dictionary<string, Func<IQueryable<Doc>, object>> Supported = new()
    {
        ["Declared_Where_gt"] = q => q.Where(e => e.Declared > 8).OrderBy(e => e.Seq).Select(e => e.Seq).ToList(),
        ["Declared_Where_lte"] = q => q.Where(e => e.Declared <= 9).OrderBy(e => e.Seq).Select(e => e.Seq).ToList(),
        ["Declared_Where_constant_on_left"] = q => q.Where(e => 9 > e.Declared).OrderBy(e => e.Seq).Select(e => e.Seq).ToList(),
        ["Declared_Where_parameter"] = q =>
        {
            var limit = 10;
            return q.Where(e => e.Declared >= limit).OrderBy(e => e.Seq).Select(e => e.Seq).ToList();
        },
        ["Declared_OrderBy"] = q => q.OrderBy(e => e.Declared).Select(e => e.Seq).ToList(),
        ["Declared_OrderByDescending"] = q => q.OrderByDescending(e => e.Declared).Select(e => e.Seq).ToList(),
        ["Declared_ThenBy"] = q => q.OrderBy(e => e.Flag).ThenBy(e => e.Declared).Select(e => e.Seq).ToList(),
        ["Declared_Min"] = q => q.Min(e => e.Declared),
        ["Declared_Max"] = q => q.Max(e => e.Declared),
        ["Declared_Select_then_OrderBy"] = q => q.Select(e => e.Declared).OrderBy(d => d).ToList(),
        ["Declared_Select_anonymous_then_Where"] = q => q.Select(e => new { e.Seq, e.Declared }).Where(a => a.Declared > 8).Select(a => a.Seq).OrderBy(s => s).ToList(),
        ["Declared_equality"] = q => q.Where(e => e.Declared == 10).Select(e => e.Seq).ToList(),
        ["Strong_OrderBy"] = q => q.OrderBy(e => e.Strong).Select(e => e.Seq).ToList(),
        ["Strong_OrderByDescending"] = q => q.OrderByDescending(e => e.Strong).Select(e => e.Seq).ToList(),
        ["Strong_Max"] = q => q.Max(e => e.Strong),
        ["Strong_Min"] = q => q.Min(e => e.Strong),
        ["When_Where_gt"] = q => q.Where(e => e.When > Epoch.AddDays(30)).OrderBy(e => e.Seq).Select(e => e.Seq).ToList(),
        ["When_Where_parameter"] = q =>
        {
            var cutoff = Epoch.AddDays(100);
            return q.Where(e => e.When <= cutoff).OrderBy(e => e.Seq).Select(e => e.Seq).ToList();
        },
        ["When_OrderBy"] = q => q.OrderBy(e => e.When).Select(e => e.Seq).ToList(),
        ["When_Max"] = q => q.Max(e => e.When),
        ["When_Min"] = q => q.Min(e => e.When),
        ["Span_Where_gt"] = q => q.Where(e => e.Span > TimeSpan.FromMinutes(40)).OrderBy(e => e.Seq).Select(e => e.Seq).ToList(),
        ["Span_OrderByDescending"] = q => q.OrderByDescending(e => e.Span).Select(e => e.Seq).ToList(),
        ["Flag_OrderBy"] = q => q.OrderBy(e => e.Flag).ThenBy(e => e.Seq).Select(e => e.Seq).ToList(),
        ["Flag_OrderByDescending"] = q => q.OrderByDescending(e => e.Flag).ThenBy(e => e.Seq).Select(e => e.Seq).ToList()
    };

    // Declared, but not honored: the stored values are added, computed from, or mixed with another value.
    private static readonly Dictionary<string, Func<IQueryable<Doc>, object>> StillRefused = new()
    {
        ["Declared_Sum"] = q => q.Sum(e => e.Declared),
        ["Declared_Average"] = q => q.Average(e => e.Declared),
        ["Declared_Select_then_Sum"] = q => q.Select(e => e.Declared).Sum(),
        ["Declared_conditional_key"] = q => q.OrderBy(e => e.Seq > 0 ? e.Declared : 0).Select(e => e.Seq).ToList(),
        ["Declared_conditional_Max"] = q => q.Max(e => e.Seq > 0 ? e.Declared : 0),
        ["Declared_against_property"] = q => q.Where(e => e.Declared > e.Seq).Select(e => e.Seq).ToList(),
        ["Declared_against_computation"] = q => q.Where(e => e.Declared > e.Seq * 2).Select(e => e.Seq).ToList(),
        ["Declared_arithmetic_comparison"] = q => q.Where(e => e.Declared + 1 > 8).Select(e => e.Seq).ToList(),
        ["Declared_arithmetic_key"] = q => q.OrderBy(e => e.Declared * 1).Select(e => e.Seq).ToList(),
        ["Declared_modulo"] = q => q.Where(e => e.Declared % 2 == 0).Select(e => e.Seq).ToList(),
        ["Declared_projected_conditional_then_OrderBy"] = q
            => q.Select(e => new { e.Seq, V = e.Seq > 0 ? e.Declared : 0 }).OrderBy(a => a.V).Select(a => a.Seq).ToList(),
        ["Declared_ToString_key"] = q => q.OrderBy(e => e.Declared.ToString()).Select(e => e.Seq).ToList()
    };

    // The same shapes over the undeclared twin: refused as in EF-337, and the message names the opt-out.
    private static readonly Dictionary<string, Func<IQueryable<Doc>, object>> Undeclared = new()
    {
        ["Undeclared_Where_gt"] = q => q.Where(e => e.Undeclared > 8).Select(e => e.Seq).ToList(),
        ["Undeclared_OrderBy"] = q => q.OrderBy(e => e.Undeclared).Select(e => e.Seq).ToList(),
        ["Undeclared_Max"] = q => q.Max(e => e.Undeclared)
    };

    private static readonly MongoQueryMode[] Modes = [MongoQueryMode.Native, MongoQueryMode.NativeOnly, MongoQueryMode.DriverLinq];

    public static IEnumerable<object[]> SupportedCases()
        => from query in Supported.Keys from mode in Modes select new object[] { query, mode };

    public static IEnumerable<object[]> StillRefusedCases()
        => from query in StillRefused.Keys from mode in Modes select new object[] { query, mode };

    public static IEnumerable<object[]> UndeclaredCases()
        => from query in Undeclared.Keys from mode in Modes select new object[] { query, mode };

    [Theory]
    [MemberData(nameof(SupportedCases))]
    public void Declared_or_built_in_order_preserving_storage_runs_on_the_server_and_is_correct(string query, MongoQueryMode mode)
    {
        var (oracle, run) = Arrange(query, Supported[query], mode);

        Assert.Equal(oracle, run());
    }

    [Theory]
    [MemberData(nameof(StillRefusedCases))]
    public void Declaration_does_not_cover_sums_computations_or_mixed_keys(string query, MongoQueryMode mode)
    {
        var (_, run) = Arrange(query, StillRefused[query], mode);

        var ex = Record.Exception(run);
        Assert.NotNull(ex);
        if (mode == MongoQueryMode.NativeOnly)
        {
            Assert.IsType<NativeTranslationNotSupportedException>(ex);
        }
        else
        {
            Assert.Contains("Doc.Declared'", Assert.IsType<NotSupportedException>(ex).Message);
        }
    }

    [Theory]
    [MemberData(nameof(UndeclaredCases))]
    public void Undeclared_converter_is_refused_and_the_message_names_the_opt_out(string query, MongoQueryMode mode)
    {
        var (_, run) = Arrange(query, Undeclared[query], mode);

        var ex = Record.Exception(run);
        Assert.NotNull(ex);
        if (mode == MongoQueryMode.NativeOnly)
        {
            Assert.IsType<NativeTranslationNotSupportedException>(ex);
        }
        else
        {
            var message = Assert.IsType<NotSupportedException>(ex).Message;
            Assert.Contains("Doc.Undeclared'", message);
            Assert.Contains("HasOrderPreservingStorage()", message);
            Assert.Contains("HaveOrderPreservingStorage()", message);
        }
    }

    [Fact]
    public void Declaration_can_be_set_and_removed_through_the_model()
    {
        var collection = database.MongoDatabase.GetCollection<Doc>(
            TemporaryDatabaseFixtureBase.CreateCollectionName(nameof(Declaration_can_be_set_and_removed_through_the_model)));
        using var db = CreateContext(collection, MongoQueryMode.Native);

        var entity = db.Model.FindEntityType(typeof(Doc))!;
        Assert.True(entity.FindProperty(nameof(Doc.Declared))!.IsOrderPreservingStorage());
        Assert.True(entity.FindProperty(nameof(Doc.Strong))!.IsOrderPreservingStorage());
        Assert.False(entity.FindProperty(nameof(Doc.Undeclared))!.IsOrderPreservingStorage());
        Assert.False(entity.FindProperty(nameof(Doc.Seq))!.IsOrderPreservingStorage());

        var builder = new ModelBuilder();
        var property = builder.Entity<Doc>().Property(e => e.Declared);
        property.HasOrderPreservingStorage();
        Assert.True(property.Metadata.IsOrderPreservingStorage());
        property.HasOrderPreservingStorage(false);
        Assert.False(property.Metadata.IsOrderPreservingStorage());
    }

    private (string Oracle, Func<string> Run) Arrange(string query, Func<IQueryable<Doc>, object> shape, MongoQueryMode mode)
    {
        var name = TemporaryDatabaseFixtureBase.CreateCollectionName(nameof(OrderPreservingStorageTests), query, mode)
                   + Guid.NewGuid().ToString("N")[..8];

        database.MongoDatabase.GetCollection<BsonDocument>(name).InsertMany(
            Values.Select((value, i) => new BsonDocument
            {
                { "_id", ObjectId.GenerateNewId() },
                { "Seq", i + 1 },
                { "Declared", value * 2 },
                { "Undeclared", value * 2 },
                { "Strong", Slots[i] },
                { "When", When(i).Ticks },
                { "Span", Span(i).Ticks },
                { "Flag", Flags[i] ? 1 : 0 }
            }));

        var collection = database.MongoDatabase.GetCollection<Doc>(name);

        using var materializing = CreateContext(collection, MongoQueryMode.Native);
        var entities = materializing.Entities.AsNoTracking().OrderBy(e => e.Seq).ToList();
        Assert.Equal(Values, entities.Select(e => e.Declared));
        Assert.Equal(Slots, entities.Select(e => e.Strong.Value));
        Assert.Equal(Enumerable.Range(0, Values.Length).Select(When), entities.Select(e => e.When));
        Assert.Equal(Flags, entities.Select(e => e.Flag));
        var oracle = Normalize(shape(entities.AsQueryable()));

        return (oracle, () =>
        {
            using var db = CreateContext(collection, mode);
            return Normalize(shape(db.Entities.AsNoTracking()));
        });
    }

    private static SingleEntityDbContext<Doc> CreateContext(IMongoCollection<Doc> collection, MongoQueryMode mode)
        => SingleEntityDbContext.Create(
            collection,
            modelBuilderAction: mb =>
            {
                var doc = mb.Entity<Doc>();
                doc.Property(e => e.Declared).HasConversion(v => v * 2, v => v / 2).HasOrderPreservingStorage();
                doc.Property(e => e.Undeclared).HasConversion(v => v * 2, v => v / 2);
                doc.Property(e => e.When).HasConversion<DateTimeToTicksConverter>();
                doc.Property(e => e.Span).HasConversion<TimeSpanToTicksConverter>();
                doc.Property(e => e.Flag).HasConversion<BoolToZeroOneConverter<int>>();
            },
            configBuilderAction: cb => cb.Properties<Slot>().HaveConversion<SlotConverter>().HaveOrderPreservingStorage(),
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
            Slot slot => slot.Value.ToString(CultureInfo.InvariantCulture),
            double d => d.ToString("0.#########", CultureInfo.InvariantCulture),
            IEnumerable e => "[" + string.Join(",", e.Cast<object?>().Select(Normalize)) + "]",
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? ""
        };
}
