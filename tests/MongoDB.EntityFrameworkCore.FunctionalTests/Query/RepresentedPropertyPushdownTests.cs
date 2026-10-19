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
using Microsoft.EntityFrameworkCore.ChangeTracking;
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
/// EF-337: a property stored in a form whose server ordering differs from its CLR ordering (a string
/// <c>BsonRepresentation</c>, or any value converter) must never be sorted, relationally compared or aggregated on
/// its stored form. Every such query returns the LINQ-to-objects answer or throws; equality keeps working; and
/// representations the server orders exactly like the CLR value (numeric-to-numeric, string/ObjectId) stay
/// supported.
/// </summary>
/// <remarks>
/// Seeded 9 / 10 / 100: stored as strings, "9" &gt; "100" &gt; "10", <c>$sum</c> ignores strings (0), and
/// <c>{$gt: "50"}</c> matches only "9". Each case is run under <see cref="MongoQueryMode.Native"/>,
/// <see cref="MongoQueryMode.NativeOnly"/> and <see cref="MongoQueryMode.DriverLinq"/>, against an oracle computed
/// in memory over the materialized entities (which apply the representation/converter).
/// </remarks>
[XUnitCollection("QueryTests")]
public class RepresentedPropertyPushdownTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    public class Item
    {
        public ObjectId Id { get; set; }
        public int Seq { get; set; }
        public int Group { get; set; }
        public int Price { get; set; }

        // Same value and storage as Price; for ?? shapes.
        public int? NPrice { get; set; }
    }

    public enum Storage
    {
        /// <summary><c>HasBsonRepresentation(BsonType.String)</c>: stored "9", "10", "100".</summary>
        StringRepresentation,

        /// <summary><c>HasConversion&lt;string&gt;()</c>: stored "9", "10", "100".</summary>
        StringConverter,

        /// <summary><c>HasConversion(v =&gt; v * 2, v =&gt; v / 2)</c>: order-preserving, but not provably so.</summary>
        ScalingConverter,

        /// <summary><c>HasConversion(v =&gt; -v, v =&gt; -v)</c>: reverses the order.</summary>
        NegatingConverter,

        /// <summary><c>HasBsonRepresentation(BsonType.Int64)</c>: exact, numerically ordered. Allow-listed.</summary>
        Int64Representation,

        /// <summary><c>HasBsonRepresentation(BsonType.Double)</c>: exact for int. Allow-listed.</summary>
        DoubleRepresentation,

        /// <summary><c>HasBsonRepresentation(BsonType.Decimal128)</c>: exact for int. Allow-listed.</summary>
        Decimal128Representation,

        /// <summary><c>HasConversion&lt;long&gt;()</c>: EF's built-in CastingConverter, exact. Allow-listed.</summary>
        CastingConverterToLong,

        /// <summary><c>HasConversion&lt;double&gt;()</c>: EF's built-in CastingConverter, exact for int. Allow-listed.</summary>
        CastingConverterToDouble
    }

    private static readonly int[] Prices = [9, 10, 100];

    private static BsonValue Stored(Storage storage, int price)
        => storage switch
        {
            Storage.StringRepresentation or Storage.StringConverter => price.ToString(CultureInfo.InvariantCulture),
            Storage.ScalingConverter => price * 2,
            Storage.NegatingConverter => -price,
            Storage.Int64Representation or Storage.CastingConverterToLong => (long)price,
            Storage.DoubleRepresentation or Storage.CastingConverterToDouble => (double)price,
            Storage.Decimal128Representation => new Decimal128(price),
            _ => throw new ArgumentOutOfRangeException(nameof(storage))
        };

    private static void Configure(ModelBuilder mb, Storage storage)
    {
        var price = mb.Entity<Item>().Property(e => e.Price);
        var nPrice = mb.Entity<Item>().Property(e => e.NPrice);
        switch (storage)
        {
            case Storage.StringRepresentation:
                price.HasBsonRepresentation(BsonType.String);
                nPrice.HasBsonRepresentation(BsonType.String);
                break;
            case Storage.StringConverter:
                price.HasConversion<string>();
                nPrice.HasConversion<string>();
                break;
            case Storage.ScalingConverter:
                price.HasConversion(v => v * 2, v => v / 2);
                nPrice.HasConversion(new ValueConverter<int, int>(v => v * 2, v => v / 2));
                break;
            case Storage.NegatingConverter:
                price.HasConversion(v => -v, v => -v);
                nPrice.HasConversion(new ValueConverter<int, int>(v => -v, v => -v));
                break;
            case Storage.Int64Representation:
                price.HasBsonRepresentation(BsonType.Int64);
                nPrice.HasBsonRepresentation(BsonType.Int64);
                break;
            case Storage.DoubleRepresentation:
                price.HasBsonRepresentation(BsonType.Double);
                nPrice.HasBsonRepresentation(BsonType.Double);
                break;
            case Storage.Decimal128Representation:
                price.HasBsonRepresentation(BsonType.Decimal128);
                nPrice.HasBsonRepresentation(BsonType.Decimal128);
                break;
            case Storage.CastingConverterToLong:
                price.HasConversion<long>();
                nPrice.HasConversion<long?>();
                break;
            case Storage.CastingConverterToDouble:
                price.HasConversion<double>();
                nPrice.HasConversion<double?>();
                break;
        }
    }

    private static bool IsAllowListed(Storage storage)
        => storage is Storage.Int64Representation or Storage.DoubleRepresentation or Storage.Decimal128Representation
            or Storage.CastingConverterToLong or Storage.CastingConverterToDouble;

    // Queries whose server-side answer depends on the stored ordering or arithmetic.
    private static readonly Dictionary<string, Func<IQueryable<Item>, object>> OrderSensitiveQueries = new()
    {
        ["Sum"] = q => q.Sum(e => e.Price),
        ["Sum_selectorless"] = q => q.Select(e => e.Price).Sum(),
        ["Min"] = q => q.Min(e => e.Price),
        ["Max"] = q => q.Max(e => e.Price),
        ["Max_selectorless"] = q => q.Select(e => e.Price).Max(),
        ["Average"] = q => q.Average(e => e.Price),
        ["Where_greater_than"] = q => q.Where(e => e.Price > 50).OrderBy(e => e.Seq).Select(e => e.Seq).ToList(),
        ["Where_constant_on_left"] = q => q.Where(e => 50 >= e.Price).OrderBy(e => e.Seq).Select(e => e.Seq).ToList(),
        ["Where_less_than_parameter"] = q =>
        {
            var limit = 50;
            return q.Where(e => e.Price < limit).OrderBy(e => e.Seq).Select(e => e.Seq).ToList();
        },
        ["OrderBy"] = q => q.OrderBy(e => e.Price).Select(e => e.Seq).ToList(),
        ["OrderByDescending"] = q => q.OrderByDescending(e => e.Price).Select(e => e.Seq).ToList(),
        ["ThenBy"] = q => q.OrderBy(e => e.Group).ThenBy(e => e.Price).Select(e => e.Seq).ToList(),
        ["OrderBy_First"] = q => q.OrderBy(e => e.Price).Select(e => e.Seq).First(),
        ["GroupBy_Sum"] = q => q.GroupBy(e => e.Group).Select(g => g.Sum(e => e.Price)).ToList(),
        ["GroupBy_Max"] = q => q.GroupBy(e => e.Group).Select(g => g.Max(e => e.Price)).ToList(),
        ["GroupBy_distinct_Max"] = q => q.GroupBy(e => e.Group).Select(g => g.Select(e => e.Price).Distinct().Max()).ToList(),

        // Operators composed after a projection: the key/operand is a projected member or the bare projected value.
        ["Select_anonymous_then_OrderBy"] = q => q.Select(e => new { e.Seq, e.Price }).OrderBy(a => a.Price).Select(a => a.Seq).ToList(),
        ["Select_anonymous_then_Where"] = q => q.Select(e => new { e.Seq, e.Price }).Where(a => a.Price > 50).Select(a => a.Seq).ToList(),
        ["Select_scalar_then_OrderBy"] = q => q.Select(e => e.Price).OrderBy(p => p).ToList(),
        ["Select_scalar_then_Where"] = q => q.Select(e => e.Price).Where(p => p > 50).ToList(),
        ["Distinct_then_OrderBy"] = q => q.Select(e => e.Price).Distinct().OrderBy(p => p).ToList(),
        ["Select_scalar_then_Max"] = q => q.Select(e => new { e.Price }).Max(a => a.Price),

        // Computed keys/operands (fix round 1, I-1): the property is reached through ??, ?:, negation, a projected
        // computed member, g.Key, or a set operation.
        ["OrderBy_coalesce"] = q => q.OrderBy(e => e.NPrice ?? 0).Select(e => e.Seq).ToList(),
        ["Where_coalesce_gt"] = q => q.Where(e => (e.NPrice ?? 0) > 50).OrderBy(e => e.Seq).Select(e => e.Seq).ToList(),
        ["Max_coalesce"] = q => q.Max(e => e.NPrice ?? 0),
        ["OrderBy_conditional"] = q => q.OrderBy(e => e.Seq > 0 ? e.Price : 0).Select(e => e.Seq).ToList(),
        ["Where_conditional_gt"] = q => q.Where(e => (e.Seq > 0 ? e.Price : 0) > 50).OrderBy(e => e.Seq).Select(e => e.Seq).ToList(),
        ["OrderBy_negated"] = q => q.OrderBy(e => -e.Price).Select(e => e.Seq).ToList(),
        ["Select_anon_computed_OrderBy"] = q => q.Select(e => new { e.Seq, P = e.Seq > 0 ? e.Price : 0 }).OrderBy(a => a.P).Select(a => a.Seq).ToList(),
        ["GroupBy_key_Max"] = q => q.GroupBy(e => e.Price).Select(g => g.Key).Max(),
        ["GroupBy_key_OrderBy"] = q => q.GroupBy(e => e.Price).Select(g => g.Key).OrderBy(k => k).ToList(),
        ["Concat_then_Max"] = q => q.Select(e => e.Price).Concat(q.Select(e => e.Price)).Max(),
        ["Distinct_then_Where"] = q => q.Select(e => e.Price).Distinct().Where(p => p > 50).ToList(),
        ["Concat_second_source_Max"] = q => q.Select(e => e.Seq).Concat(q.Select(e => e.Price)).Max(),
        // An array element (EF rewrites a[0] to ElementAt): the walk resolves the array's elements.
        ["Array_element_Max"] = q => q.Select(e => new[] { e.Price }).Select(a => a[0]).Max(),
        // A GroupBy result selector: `k` resolves to the key selector.
        ["GroupBy_result_selector_Max"] = q => q.GroupBy(e => e.Price, (k, g) => k).Max(),
        ["GroupBy_result_selector_group_Max"] = q => q.GroupBy(e => e.Seq, (k, g) => g.Max(e => e.Price)).Max(),

        // Fix round 2 adversarial: method calls, arithmetic and nullable accessors over the property.
        ["Where_Math_Abs_gt"] = q => q.Where(e => Math.Abs(e.Price) > 50).OrderBy(e => e.Seq).Select(e => e.Seq).ToList(),
        ["Where_divided_gt"] = q => q.Where(e => e.Price / 2.0 > 25).OrderBy(e => e.Seq).Select(e => e.Seq).ToList(),
        ["OrderBy_ToString"] = q => q.OrderBy(e => e.Price.ToString()).ThenBy(e => e.Seq).Select(e => e.Seq).ToList(),
        ["OrderBy_nullable_Value"] = q => q.OrderBy(e => e.NPrice!.Value).Select(e => e.Seq).ToList(),
        ["Max_GetValueOrDefault"] = q => q.Max(e => e.NPrice.GetValueOrDefault())
    };

    // Equality only: stored-form comparison is exact, so these must keep working for every storage.
    private static readonly Dictionary<string, Func<IQueryable<Item>, object>> EqualityQueries = new()
    {
        ["Where_equal"] = q => q.Where(e => e.Price == 10).Select(e => e.Seq).ToList(),
        ["Where_not_equal"] = q => q.Where(e => e.Price != 10).OrderBy(e => e.Seq).Select(e => e.Seq).ToList(),
        ["Where_contains"] = q =>
        {
            var wanted = new[] { 10, 100 };
            return q.Where(e => wanted.Contains(e.Price)).OrderBy(e => e.Seq).Select(e => e.Seq).ToList();
        },
        ["Count_equal"] = q => q.Count(e => e.Price == 100),

        // Keys/operands that resolve to a default-serialized member of a projection or grouping key that ALSO reads
        // Price: the walk must resolve them precisely rather than refuse because the query mentions Price.
        ["GroupBy_composite_key_safe_member_Max"] = q => q.GroupBy(e => new { e.Seq, e.Price }).Select(g => g.Key.Seq).Max()
    };

    private static readonly MongoQueryMode[] Modes = [MongoQueryMode.Native, MongoQueryMode.NativeOnly, MongoQueryMode.DriverLinq];

    public static IEnumerable<object[]> RefusedCases()
        => from query in OrderSensitiveQueries.Keys
           from storage in new[] { Storage.StringRepresentation, Storage.StringConverter, Storage.ScalingConverter, Storage.NegatingConverter }
           from mode in Modes
           select new object[] { query, storage, mode };

    public static IEnumerable<object[]> AllowListedCases()
        => from query in OrderSensitiveQueries.Keys
           from storage in Enum.GetValues<Storage>().Where(IsAllowListed)
           from mode in Modes
           select new object[] { query, storage, mode };

    public static IEnumerable<object[]> EqualityCases()
        => from query in EqualityQueries.Keys
           from storage in Enum.GetValues<Storage>()
           from mode in Modes
           select new object[] { query, storage, mode };

    /// <summary>
    /// The guard: correct or loud, never a stored-form answer. Pinned to the decline (the answer can't reveal a
    /// guard that happens to get a right answer by accident, e.g. the order-preserving scaling converter).
    /// </summary>
    [Theory]
    [MemberData(nameof(RefusedCases))]
    public void Order_sensitive_query_over_non_order_preserving_storage_is_refused(string query, Storage storage, MongoQueryMode mode)
    {
        var (oracle, run) = Arrange(query, OrderSensitiveQueries[query], storage, mode);

        var ex = Record.Exception(run);
        if (ex is null)
        {
            // Correct or loud: a silent wrong answer is the bug.
            Assert.Equal(oracle, Normalize(run()));
        }

        // Pinned decline. NativeOnly refuses at the native gate; Native falls back and DriverLinq goes straight to the
        // driver-LINQ bridge, which refuses naming the property.
        Assert.NotNull(ex);
        if (mode == MongoQueryMode.NativeOnly)
        {
            Assert.IsType<NativeTranslationNotSupportedException>(ex);
        }
        else if (IsGroupByAggregate(query))
        {
            // Pre-existing and loud: a fallback GroupBy whose aggregate the native binder left unbound fails at compile
            // time (EF's GroupByShaperExpression reaches VerifyNoClientConstant), before the bridge runs.
            Assert.IsType<InvalidOperationException>(ex);
        }
        else
        {
            var notSupported = Assert.IsType<NotSupportedException>(ex);
            Assert.Matches(@"Item\.N?Price'", notSupported.Message);
        }
    }

    [Theory]
    [MemberData(nameof(AllowListedCases))]
    public void Order_sensitive_query_over_order_preserving_representation_is_correct(string query, Storage storage, MongoQueryMode mode)
    {
        var (oracle, run) = Arrange(query, OrderSensitiveQueries[query], storage, mode);

        // Answers, and answers right; or fails loudly in one of the pre-existing ways listed in
        // IsPreExistingLoudFailure. Never the EF-337 refusal: these storages are allow-listed.
        var ex = Record.Exception(run);
        if (ex is not null)
        {
            Assert.DoesNotContain("is stored through a value converter or a BsonRepresentation", ex.Message);
            Assert.True(
                IsPreExistingLoudFailure(query, storage, mode, ex),
                $"'{query}' over {storage} threw under {mode}: {ex}");
            return;
        }

        Assert.Equal(oracle, Normalize(run()));
    }

    // Loud failures that predate EF-337, for an operand that isn't default-serialized:
    // - native aggregates reduce only default-serialized operands (their result is read back through a generic
    //   serializer), so NativeOnly declines them;
    // - a fallback GroupBy with an unbound aggregate fails at compile time (see the refused-case test);
    // - Average over a Decimal128-represented int reads the fractional $avg through the property's Int32 serializer.
    // - a computed key/operand (??, ?:, negation) is native only over default-serialized fields, so NativeOnly declines;
    // - the driver can't negate a converted value ("uses a non-numeric representation").
    // - the round-2 adversarial method/arithmetic/nullable shapes hit assorted native declines and driver-LINQ
    //   limitations over non-default storage (ExpressionNotSupportedException, a NullReferenceException in the driver's
    //   converter serializer); any loud failure other than the EF-337 refusal is accepted for them.
    private static bool IsPreExistingLoudFailure(string query, Storage storage, MongoQueryMode mode, Exception ex)
        => query is "Where_Math_Abs_gt" or "Where_divided_gt" or "OrderBy_ToString" or "OrderBy_nullable_Value"
               or "Max_GetValueOrDefault"
           || (mode == MongoQueryMode.NativeOnly && ex is NativeTranslationNotSupportedException
               && (IsAggregate(query) || IsComputed(query)))
           || (query == "OrderBy_negated" && ex.GetType().Name == "ExpressionNotSupportedException"
               && storage is Storage.CastingConverterToLong or Storage.CastingConverterToDouble)
           // A native Distinct key is likewise default-serialized only (NativeGroupByBinder.HasDefaultKeySerialization).
           || (mode == MongoQueryMode.NativeOnly && query.StartsWith("Distinct", StringComparison.Ordinal)
               && ex is NativeTranslationNotSupportedException)
           || (mode != MongoQueryMode.NativeOnly && IsGroupByAggregate(query) && ex is InvalidOperationException)
           || (query == "Average" && storage == Storage.Decimal128Representation && ex is TruncationException);

    // Fallback GroupBy shapes that fail at compile time before the bridge runs (pre-existing; see the refused test).
    private static bool IsGroupByAggregate(string query)
        => query is "GroupBy_Sum" or "GroupBy_Max" or "GroupBy_distinct_Max" or "GroupBy_key_OrderBy";

    private static bool IsComputed(string query)
        => query.Contains("coalesce", StringComparison.Ordinal) || query.Contains("conditional", StringComparison.Ordinal)
           || query.Contains("negated", StringComparison.Ordinal) || query.Contains("computed", StringComparison.Ordinal)
           || query.StartsWith("Distinct", StringComparison.Ordinal);

    [Theory]
    [MemberData(nameof(EqualityCases))]
    public void Equality_over_any_storage_keeps_working(string query, Storage storage, MongoQueryMode mode)
    {
        var (oracle, run) = Arrange(query, EqualityQueries[query], storage, mode);

        // A native Distinct / GroupBy key over a non-default-serialized part declines (pre-existing,
        // HasDefaultKeySerialization); the fallback must then answer, not refuse.
        if (mode == MongoQueryMode.NativeOnly && query.Contains("safe_member", StringComparison.Ordinal)
            && Record.Exception(run) is NativeTranslationNotSupportedException)
        {
            return;
        }

        Assert.Equal(oracle, Normalize(run()));
    }

    private static bool IsAggregate(string query)
        => query.StartsWith("Sum", StringComparison.Ordinal) || query.StartsWith("Min", StringComparison.Ordinal)
           || query.StartsWith("Max", StringComparison.Ordinal) || query.StartsWith("Average", StringComparison.Ordinal)
           || query.StartsWith("GroupBy", StringComparison.Ordinal) || query.EndsWith("_Max", StringComparison.Ordinal);

    private (string Oracle, Func<object> Run) Arrange(
        string query, Func<IQueryable<Item>, object> shape, Storage storage, MongoQueryMode mode)
    {
        var name = TemporaryDatabaseFixtureBase.CreateCollectionName(nameof(RepresentedPropertyPushdownTests), query, storage, mode)
                   + Guid.NewGuid().ToString("N")[..8];

        // Seeded as BsonDocument: the driver's POCO serializer ignores EF's representation/converter.
        database.MongoDatabase.GetCollection<BsonDocument>(name).InsertMany(
            Prices.Select((price, i) => new BsonDocument
            {
                { "_id", ObjectId.GenerateNewId() },
                { "Seq", i + 1 },
                { "Group", 1 },
                { "Price", Stored(storage, price) },
                { "NPrice", Stored(storage, price) }
            }));

        var collection = database.MongoDatabase.GetCollection<Item>(name);

        // Oracle: materialize through the model (applying the representation/converter), then LINQ-to-objects.
        using (var materializing = CreateContext(collection, storage, MongoQueryMode.Native))
        {
            var entities = materializing.Entities.AsNoTracking().ToList();
            Assert.Equal(Prices, entities.OrderBy(e => e.Seq).Select(e => e.Price));
            var oracle = Normalize(shape(entities.AsQueryable()));

            return (oracle, () =>
            {
                using var db = CreateContext(collection, storage, mode);
                return Normalize(shape(db.Entities.AsNoTracking()));
            });
        }
    }

    private static SingleEntityDbContext<Item> CreateContext(IMongoCollection<Item> collection, Storage storage, MongoQueryMode mode)
        => SingleEntityDbContext.Create(
            collection,
            modelBuilderAction: mb => Configure(mb, storage),
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });

    // ── string <-> ObjectId and enum representations ─────────────────────────────────────────────────────────
    //
    // A string stored as an ObjectId (the canonical [BsonRepresentation(BsonType.ObjectId)] string Id) and an ObjectId
    // stored as its hex string are allow-listed: EF writes lowercase fixed-width hex, which sorts (ordinally and under
    // the default culture comparer) exactly as the 12 big-endian ObjectId bytes do. An enum is compared by its
    // underlying value in C#: allowed when stored as a number, refused when stored as its name.

    public enum Level
    {
        Low = 1,
        Mid = 2,
        High = 10
    }

    public class Tagged
    {
        public ObjectId Id { get; set; }
        public int Seq { get; set; }
        public string Ref { get; set; } = "";
        public ObjectId Oid { get; set; }
        public Level Level { get; set; }
    }

    // Hex strings where digit/letter positions and leading bytes vary, plus random ids.
    private static readonly string[] Hexes =
    [
        "000000000000000000000009", "00000000000000000000000a", "000000000000000000000010",
        "0000000000000000000000f0", "a00000000000000000000000", "100000000000000000000000",
        "9fffffffffffffffffffffff", "ffffffffffffffffffffffff", "0a0000000000000000000000",
        .. Enumerable.Range(0, 16).Select(_ => ObjectId.GenerateNewId().ToString())
    ];

    private static readonly Level[] Levels = [Level.High, Level.Low, Level.Mid];

    private const string MidHex = "0a0000000000000000000000";

    private static readonly Dictionary<string, Func<IQueryable<Tagged>, object>> TaggedOrderQueries = new()
    {
        ["OrderBy_string_as_ObjectId"] = q => q.OrderBy(e => e.Ref).Select(e => e.Seq).ToList(),
        ["OrderByDescending_string_as_ObjectId"] = q => q.OrderByDescending(e => e.Ref).Select(e => e.Seq).ToList(),
        ["CompareTo_string_as_ObjectId"] = q => q.Where(e => e.Ref.CompareTo(MidHex) > 0).OrderBy(e => e.Seq).Select(e => e.Seq).ToList(),
        ["string_Compare_string_as_ObjectId"] = q => q.Where(e => string.Compare(e.Ref, MidHex) <= 0).OrderBy(e => e.Seq).Select(e => e.Seq).ToList(),
        ["Max_string_as_ObjectId"] = q => q.Max(e => e.Ref)!,
        ["OrderBy_ObjectId_as_string"] = q => q.OrderBy(e => e.Oid).Select(e => e.Seq).ToList(),
        ["Greater_ObjectId_as_string"] = q =>
        {
            var mid = ObjectId.Parse(MidHex);
            return q.Where(e => e.Oid > mid).OrderBy(e => e.Seq).Select(e => e.Seq).ToList();
        },
        ["OrderBy_enum"] = q => q.OrderBy(e => e.Level).ThenBy(e => e.Seq).Select(e => e.Seq).ToList(),
        ["Greater_enum"] = q => q.Where(e => e.Level > Level.Low).OrderBy(e => e.Seq).Select(e => e.Seq).ToList(),
        ["Max_enum"] = q => q.Max(e => e.Level)
    };

    public enum EnumStorage
    {
        /// <summary><c>HasBsonRepresentation(BsonType.Int64)</c>: allow-listed.</summary>
        EnumAsInt64,

        /// <summary><c>HasBsonRepresentation(BsonType.String)</c>: stored "High"/"Low"/"Mid"; refused.</summary>
        EnumAsString,

        /// <summary><c>HasConversion&lt;long&gt;()</c>: EF's built-in EnumToNumberConverter; allow-listed.</summary>
        EnumAsNumberConverter
    }

    public static IEnumerable<object[]> TaggedCases()
        => from query in TaggedOrderQueries.Keys
           from storage in Enum.GetValues<EnumStorage>()
           from mode in Modes
           select new object[] { query, storage, mode };

    [Theory]
    [MemberData(nameof(TaggedCases))]
    public void Order_sensitive_query_over_ObjectId_or_enum_representation(string query, EnumStorage storage, MongoQueryMode mode)
    {
        var name = TemporaryDatabaseFixtureBase.CreateCollectionName(nameof(Order_sensitive_query_over_ObjectId_or_enum_representation), query, storage, mode)
                   + Guid.NewGuid().ToString("N")[..8];

        database.MongoDatabase.GetCollection<BsonDocument>(name).InsertMany(
            Hexes.Select((hex, i) => new BsonDocument
            {
                { "_id", ObjectId.GenerateNewId() },
                { "Seq", i + 1 },
                { "Ref", ObjectId.Parse(hex) },
                { "Oid", hex },
                {
                    "Level", storage == EnumStorage.EnumAsString
                        ? (BsonValue)Levels[i % Levels.Length].ToString()
                        : (long)Levels[i % Levels.Length]
                }
            }));

        var collection = database.MongoDatabase.GetCollection<Tagged>(name);
        var shape = TaggedOrderQueries[query];

        string oracle;
        using (var materializing = CreateTaggedContext(collection, storage, MongoQueryMode.Native))
        {
            var entities = materializing.Entities.AsNoTracking().ToList();
            Assert.Equal(Hexes, entities.OrderBy(e => e.Seq).Select(e => e.Ref));
            oracle = Normalize(shape(entities.AsQueryable()));
        }

        string Run()
        {
            using var db = CreateTaggedContext(collection, storage, mode);
            return Normalize(shape(db.Entities.AsNoTracking()));
        }

        var ex = Record.Exception(Run);
        var refused = storage == EnumStorage.EnumAsString && query.Contains("enum", StringComparison.Ordinal);
        if (refused)
        {
            // Correct or loud, pinned to the decline.
            if (ex is null)
            {
                Assert.Equal(oracle, Run());
            }

            Assert.NotNull(ex);
            if (mode == MongoQueryMode.NativeOnly)
            {
                Assert.IsType<NativeTranslationNotSupportedException>(ex);
            }
            else
            {
                Assert.Contains("Tagged.Level'", Assert.IsType<NotSupportedException>(ex).Message);
            }

            return;
        }

        // Allow-listed: answers, and answers right. Two native declines predate EF-337 and stay strict (default
        // serialization only): aggregates, and string.Compare/CompareTo (IsFoldSafe); under NativeOnly those may
        // decline cleanly instead, and the driver-LINQ fallback answers them correctly (the other modes).
        if (ex is not null)
        {
            Assert.True(
                mode == MongoQueryMode.NativeOnly
                && (query.StartsWith("Max", StringComparison.Ordinal) || query.Contains("Compare", StringComparison.Ordinal))
                && ex is NativeTranslationNotSupportedException,
                $"'{query}' over {storage} threw under {mode}: {ex}");
            return;
        }

        Assert.Equal(oracle, Run());
    }

    private static SingleEntityDbContext<Tagged> CreateTaggedContext(
        IMongoCollection<Tagged> collection, EnumStorage storage, MongoQueryMode mode)
        => SingleEntityDbContext.Create(
            collection,
            modelBuilderAction: mb =>
            {
                var entity = mb.Entity<Tagged>();
                entity.Property(e => e.Ref).HasBsonRepresentation(BsonType.ObjectId);
                entity.Property(e => e.Oid).HasBsonRepresentation(BsonType.String);
                if (storage == EnumStorage.EnumAsNumberConverter)
                {
                    entity.Property(e => e.Level).HasConversion<long>();
                }
                else
                {
                    entity.Property(e => e.Level).HasBsonRepresentation(
                        storage == EnumStorage.EnumAsString ? BsonType.String : BsonType.Int64);
                }
            },
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });

    // ── Owned collection element property ───────────────────────────────────────────────────────────────────
    //
    // An element-scoped comparison (`o.Lines.Any(l => l.Price > 50)`) runs on the stored element field too.

    public class Line
    {
        public int Price { get; set; }
    }

    public class Order
    {
        public ObjectId Id { get; set; }
        public int Seq { get; set; }
        public List<Line> Lines { get; set; } = [];
    }

    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.NativeOnly)]
    [InlineData(MongoQueryMode.DriverLinq)]
    public void Owned_element_comparison_over_string_represented_property_is_refused(MongoQueryMode mode)
    {
        var name = TemporaryDatabaseFixtureBase.CreateCollectionName(nameof(Owned_element_comparison_over_string_represented_property_is_refused), mode)
                   + Guid.NewGuid().ToString("N")[..8];
        database.MongoDatabase.GetCollection<BsonDocument>(name).InsertMany(
            Prices.Select((price, i) => new BsonDocument
            {
                { "_id", ObjectId.GenerateNewId() },
                { "Seq", i + 1 },
                { "Lines", new BsonArray { new BsonDocument("Price", price.ToString(CultureInfo.InvariantCulture)) } }
            }));

        var collection = database.MongoDatabase.GetCollection<Order>(name);
        SingleEntityDbContext<Order> Create(MongoQueryMode m)
            => SingleEntityDbContext.Create(
                collection,
                modelBuilderAction: mb => mb.Entity<Order>().OwnsMany(o => o.Lines, l => l.Property(x => x.Price).HasBsonRepresentation(BsonType.String)),
                optionsBuilderAction: b =>
                {
                    b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                    new MongoDbContextOptionsBuilder(b).UseQueryMode(m);
                });

        using (var oracleDb = Create(MongoQueryMode.Native))
        {
            Assert.Equal([3], oracleDb.Entities.AsNoTracking().ToList()
                .Where(o => o.Lines.Any(l => l.Price > 50)).Select(o => o.Seq));
        }

        using var db = Create(mode);
        List<int>? actual = null;
        var ex = Record.Exception(() => actual = db.Entities.AsNoTracking()
            .Where(o => o.Lines.Any(l => l.Price > 50)).OrderBy(o => o.Seq).Select(o => o.Seq).ToList());
        if (ex is null)
        {
            Assert.Equal([3], actual);
        }

        Assert.NotNull(ex);
        if (mode == MongoQueryMode.NativeOnly)
        {
            Assert.IsType<NativeTranslationNotSupportedException>(ex);
        }
        else
        {
            Assert.Contains("Line.Price'", Assert.IsType<NotSupportedException>(ex).Message);
        }

        // Equality over the same element property keeps working.
        Assert.Equal([2], db.Entities.AsNoTracking().Where(o => o.Lines.Any(l => l.Price == 10)).Select(o => o.Seq).ToList());
    }

    // ── Narrowing casting converters (fix round 1, I-2) ─────────────────────────────────────────────────────
    //
    // `long` stored as `int` (integral narrowing): sort and aggregates read stored values exactly and stay supported,
    // but a relational comparison against a constant outside the int range would wrap the constant (3_000_000_000L ->
    // a negative int), so it is refused. `decimal` stored as `int` (fractional -> integral) truncates constants and
    // values alike (1.5m -> 1), so every order-sensitive use is refused.

    public class Narrow
    {
        public ObjectId Id { get; set; }
        public int Seq { get; set; }
        public long Big { get; set; }
        public decimal Dec { get; set; }
    }

    private static readonly Dictionary<string, Func<IQueryable<Narrow>, object>> NarrowQueries = new()
    {
        // Refused.
        ["Big_gt_3e9"] = q => q.Where(e => e.Big > 3_000_000_000L).OrderBy(e => e.Seq).Select(e => e.Seq).ToList(),
        ["Big_lt_5e9"] = q => q.Where(e => e.Big < 5_000_000_000L).OrderBy(e => e.Seq).Select(e => e.Seq).ToList(),
        ["Dec_ge_1_5"] = q => q.Where(e => e.Dec >= 1.5m).OrderBy(e => e.Seq).Select(e => e.Seq).ToList(),
        ["Dec_lt_2_5"] = q => q.Where(e => e.Dec < 2.5m).OrderBy(e => e.Seq).Select(e => e.Seq).ToList(),
        ["Dec_OrderBy"] = q => q.OrderByDescending(e => e.Dec).Select(e => e.Seq).ToList(),
        ["Dec_Max"] = q => q.Max(e => e.Dec),

        // Supported (integral narrowing, sort/aggregate only).
        ["Big_OrderByDescending"] = q => q.OrderByDescending(e => e.Big).Select(e => e.Seq).ToList(),
        ["Big_Sum"] = q => q.Sum(e => e.Big),
        ["Big_Max"] = q => q.Max(e => e.Big),
        ["Big_Average"] = q => q.Average(e => e.Big),

        // Equality keeps working for the integral case.
        ["Big_equal"] = q => q.Where(e => e.Big == 2L).Select(e => e.Seq).ToList()
    };

    public static IEnumerable<object[]> NarrowCases()
        => from query in NarrowQueries.Keys
           from mode in Modes
           select new object[] { query, mode };

    [Theory]
    [MemberData(nameof(NarrowCases))]
    public void Narrowing_casting_converter(string query, MongoQueryMode mode)
    {
        var name = TemporaryDatabaseFixtureBase.CreateCollectionName(nameof(Narrowing_casting_converter), query, mode)
                   + Guid.NewGuid().ToString("N")[..8];
        database.MongoDatabase.GetCollection<BsonDocument>(name).InsertMany(
            new[] { 1, 2, 3 }.Select(v => new BsonDocument
            {
                { "_id", ObjectId.GenerateNewId() }, { "Seq", v }, { "Big", v }, { "Dec", v }
            }));
        var collection = database.MongoDatabase.GetCollection<Narrow>(name);

        SingleEntityDbContext<Narrow> Create(MongoQueryMode m)
            => SingleEntityDbContext.Create(
                collection,
                modelBuilderAction: mb =>
                {
                    mb.Entity<Narrow>().Property(e => e.Big).HasConversion<int>();
                    mb.Entity<Narrow>().Property(e => e.Dec).HasConversion<int>();
                },
                optionsBuilderAction: b =>
                {
                    b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                    new MongoDbContextOptionsBuilder(b).UseQueryMode(m);
                });

        var shape = NarrowQueries[query];
        string oracle;
        using (var materializing = Create(MongoQueryMode.Native))
        {
            oracle = Normalize(shape(materializing.Entities.AsNoTracking().ToList().AsQueryable()));
        }

        string Run()
        {
            using var db = Create(mode);
            return Normalize(shape(db.Entities.AsNoTracking()));
        }

        var ex = Record.Exception(Run);
        var refused = query.StartsWith("Dec", StringComparison.Ordinal) || query is "Big_gt_3e9" or "Big_lt_5e9";
        if (!refused)
        {
            if (ex is not null)
            {
                // Native aggregates read back through a generic serializer, so they decline cleanly under NativeOnly.
                Assert.True(
                    mode == MongoQueryMode.NativeOnly && ex is NativeTranslationNotSupportedException
                    && query is "Big_Sum" or "Big_Max" or "Big_Average",
                    $"'{query}' threw under {mode}: {ex}");
                return;
            }

            Assert.Equal(oracle, Run());
            return;
        }

        if (ex is null)
        {
            Assert.Equal(oracle, Run()); // correct or loud
        }

        Assert.NotNull(ex);
        if (mode == MongoQueryMode.NativeOnly)
        {
            Assert.IsType<NativeTranslationNotSupportedException>(ex);
        }
        else
        {
            Assert.Matches(@"Narrow\.(Big|Dec)'", Assert.IsType<NotSupportedException>(ex).Message);
        }
    }

    // ── Identity representations (fix round 1, M-2) ────────────────────────────────────────────────────────
    //
    // A BsonRepresentation equal to the type's default BSON type stores the default form, so it isn't refused.

    public class Plain
    {
        public ObjectId Id { get; set; }
        public int Seq { get; set; }
        public string Name { get; set; } = "";
        public DateTime When { get; set; }
        public int Count { get; set; }
    }

    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.NativeOnly)]
    [InlineData(MongoQueryMode.DriverLinq)]
    public void Identity_representation_is_not_refused(MongoQueryMode mode)
    {
        var name = TemporaryDatabaseFixtureBase.CreateCollectionName(nameof(Identity_representation_is_not_refused), mode)
                   + Guid.NewGuid().ToString("N")[..8];
        var baseDate = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        database.MongoDatabase.GetCollection<BsonDocument>(name).InsertMany(
            new[] { (1, "b", 3, 9), (2, "c", 1, 100), (3, "a", 2, 10) }.Select(r => new BsonDocument
            {
                { "_id", ObjectId.GenerateNewId() }, { "Seq", r.Item1 }, { "Name", r.Item2 },
                { "When", baseDate.AddDays(r.Item3) }, { "Count", r.Item4 }
            }));
        var collection = database.MongoDatabase.GetCollection<Plain>(name);
        using var db = SingleEntityDbContext.Create(
            collection,
            modelBuilderAction: mb =>
            {
                mb.Entity<Plain>().Property(e => e.Name).HasBsonRepresentation(BsonType.String);
                mb.Entity<Plain>().Property(e => e.When).HasBsonRepresentation(BsonType.DateTime);
                mb.Entity<Plain>().Property(e => e.Count).HasBsonRepresentation(BsonType.Int32);
            },
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });

        Assert.Equal([3, 1, 2], db.Entities.AsNoTracking().OrderBy(e => e.Name).Select(e => e.Seq).ToList());
        Assert.Equal([2, 3, 1], db.Entities.AsNoTracking().OrderBy(e => e.When).Select(e => e.Seq).ToList());
        Assert.Equal([1, 3, 2], db.Entities.AsNoTracking().OrderBy(e => e.Count).Select(e => e.Seq).ToList());
        Assert.Equal([2], db.Entities.AsNoTracking().Where(e => e.Count > 50).Select(e => e.Seq).ToList());
        Assert.Equal([2], db.Entities.AsNoTracking().Where(e => e.When < baseDate.AddDays(1.5)).Select(e => e.Seq).ToList());
    }

    // ── Fix round 2: members read off a stored value, converted-bool truthiness, primitive collections ────────

    public enum St
    {
        Open,
        Closed
    }

    public class Rich
    {
        public ObjectId Id { get; set; }
        public int Seq { get; set; }
        public string Name { get; set; } = "";
        public DateTime When { get; set; }
        public TimeSpan Dur { get; set; }
        public bool Flag { get; set; }
        public bool Plain { get; set; }
        public St Status { get; set; }
        public List<int> Scores { get; set; } = [];
        public List<int> Codes { get; set; } = [];
    }

    public enum FlagStorage
    {
        /// <summary><c>HasConversion&lt;string&gt;()</c>: stored "1"/"0", both truthy on the server.</summary>
        FlagAsString,

        /// <summary><c>HasConversion(v =&gt; !v, v =&gt; !v)</c>: stored inverted.</summary>
        FlagInverted
    }

    private static readonly Dictionary<string, Func<IQueryable<Rich>, object>> RichRefusedQueries = new()
    {
        // A: a member computed from a converted/represented value.
        ["Name_length_gt_4"] = q => q.Where(x => x.Name.Length > 4).OrderBy(x => x.Seq).Select(x => x.Seq).ToList(),
        ["OrderBy_Name_length"] = q => q.OrderBy(x => x.Name.Length).ThenBy(x => x.Seq).Select(x => x.Seq).ToList(),
        ["When_year_gt_2020"] = q => q.Where(x => x.When.Year > 2020).OrderBy(x => x.Seq).Select(x => x.Seq).ToList(),
        ["Dur_TotalMinutes_gt_60"] = q => q.Where(x => x.Dur.TotalMinutes > 60).OrderBy(x => x.Seq).Select(x => x.Seq).ToList(),
        ["OrderBy_Name_StartsWith"] = q => q.OrderBy(x => x.Name.StartsWith("ab")).ThenBy(x => x.Seq).Select(x => x.Seq).ToList(),

        // B: a converted bool used for its truth in a key.
        ["OrderBy_not_Flag"] = q => q.OrderBy(x => !x.Flag).ThenBy(x => x.Seq).Select(x => x.Seq).ToList(),
        ["OrderBy_Flag_conditional"] = q => q.OrderBy(x => x.Flag ? 1 : 0).ThenBy(x => x.Seq).Select(x => x.Seq).ToList(),
        ["OrderBy_Flag_and_Plain"] = q => q.OrderBy(x => x.Flag && x.Plain).ThenBy(x => x.Seq).Select(x => x.Seq).ToList(),

        // C: a primitive collection whose elements are stored as strings is still refused.
        ["Codes_any_gt_5"] = q => q.Where(x => x.Codes.Any(c => c > 5)).OrderBy(x => x.Seq).Select(x => x.Seq).ToList(),
        ["OrderBy_Codes_max"] = q => q.OrderBy(x => x.Codes.Max()).Select(x => x.Seq).ToList()
    };

    private static readonly Dictionary<string, Func<IQueryable<Rich>, object>> RichWorkingQueries = new()
    {
        // C: a default-serialized primitive collection next to an equality-only or projection-only converted property.
        ["Status_eq_and_Scores_any_gt"] = q => q.Where(x => x.Status == St.Closed && x.Scores.Any(s => s > 5)).OrderBy(x => x.Seq).Select(x => x.Seq).ToList(),
        ["Status_eq_and_Scores_max_gt"] = q => q.Where(x => x.Status == St.Closed && x.Scores.Max() > 5).OrderBy(x => x.Seq).Select(x => x.Seq).ToList(),
        ["OrderBy_Scores_max_select_Status"] = q => q.OrderBy(x => x.Scores.Max()).Select(x => new { x.Seq, x.Status }).ToList(),
        ["Scores_any_gt_only"] = q => q.Where(x => x.Scores.Any(s => s > 5)).OrderBy(x => x.Seq).Select(x => x.Seq).ToList(),

        // B: filters over the converted bool (the native truthiness guards handle these) and a default bool key.
        ["Where_not_Flag"] = q => q.Where(x => !x.Flag).OrderBy(x => x.Seq).Select(x => x.Seq).ToList(),
        ["Where_Flag"] = q => q.Where(x => x.Flag).OrderBy(x => x.Seq).Select(x => x.Seq).ToList(),
        ["Where_Flag_eq_false"] = q => q.Where(x => x.Flag == false).OrderBy(x => x.Seq).Select(x => x.Seq).ToList(),
        ["OrderBy_not_Plain"] = q => q.OrderBy(x => !x.Plain).ThenBy(x => x.Seq).Select(x => x.Seq).ToList(),

        // A: equality over the converted string still works.
        ["Name_equal"] = q => q.Where(x => x.Name == "ab").Select(x => x.Seq).ToList(),

        // C: an unmodelled operator (GroupBy result selector) over a source filtered by an equality on a converted enum:
        // the refusal scope is what flows into the elements (Seq), not the filter.
        ["Status_filter_then_GroupBy_result_selector_Max"] = q => q.Where(x => x.Status == St.Closed).GroupBy(x => x.Seq, (k, g) => k).Max(),
        // A converted property projected alongside, but not into, the resolved key.
        ["Status_projected_GroupBy_result_selector_Max"] = q => q.Select(x => new { x.Seq, x.Status }).Distinct().GroupBy(a => a.Seq, (k, g) => k).Max()
    };

    public static IEnumerable<object[]> RichCases()
        => from refused in new[] { true, false }
           from query in (refused ? RichRefusedQueries : RichWorkingQueries).Keys
           from flag in Enum.GetValues<FlagStorage>()
           from mode in Modes
           select new object[] { query, refused, flag, mode };

    [Theory]
    [MemberData(nameof(RichCases))]
    public void Stored_value_members_truthiness_and_primitive_collections(
        string query, bool refused, FlagStorage flag, MongoQueryMode mode)
    {
        var collection = database.MongoDatabase.GetCollection<Rich>(
            TemporaryDatabaseFixtureBase.CreateCollectionName(nameof(Stored_value_members_truthiness_and_primitive_collections), query, flag, mode)
            + Guid.NewGuid().ToString("N")[..8]);

        SingleEntityDbContext<Rich> Create(MongoQueryMode m)
            => SingleEntityDbContext.Create(
                collection,
                modelBuilderAction: mb =>
                {
                    var entity = mb.Entity<Rich>();
                    entity.Property(x => x.Name).HasConversion(v => "pfx-" + v, v => v.Substring(4));
                    entity.Property(x => x.When).HasBsonRepresentation(BsonType.String);
                    entity.Property(x => x.Dur).HasConversion<string>();
                    if (flag == FlagStorage.FlagAsString)
                    {
                        entity.Property(x => x.Flag).HasConversion<string>();
                    }
                    else
                    {
                        entity.Property(x => x.Flag).HasConversion(v => !v, v => !v);
                    }

                    entity.Property(x => x.Status).HasConversion<string>();
                    // The collection stored through a whole-collection converter (as "1,20").
                    entity.Property(x => x.Codes).HasConversion(
                        v => string.Join(",", v.Select(i => i.ToString(CultureInfo.InvariantCulture))),
                        v => v.Split(',', StringSplitOptions.None).Select(t => int.Parse(t, CultureInfo.InvariantCulture)).ToList(),
                        new ValueComparer<List<int>>(
                            (a, b) => a!.SequenceEqual(b!), v => v.Aggregate(0, (h, i) => HashCode.Combine(h, i)), v => v.ToList()));
                },
                optionsBuilderAction: b =>
                {
                    b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                    new MongoDbContextOptionsBuilder(b).UseQueryMode(m);
                });

        using (var seed = Create(MongoQueryMode.Native))
        {
            seed.Entities.AddRange(
                new Rich
                {
                    Seq = 1, Name = "abcdef", When = new DateTime(2019, 6, 1, 0, 0, 0, DateTimeKind.Utc),
                    Dur = TimeSpan.FromMinutes(50), Flag = true, Plain = true, Status = St.Open, Scores = [1, 2], Codes = [1, 20]
                },
                new Rich
                {
                    Seq = 2, Name = "ab", When = new DateTime(2021, 6, 1, 0, 0, 0, DateTimeKind.Utc),
                    Dur = TimeSpan.FromMinutes(100), Flag = false, Plain = false, Status = St.Closed, Scores = [6, 7], Codes = [3]
                },
                new Rich
                {
                    Seq = 3, Name = "abc", When = new DateTime(2022, 6, 1, 0, 0, 0, DateTimeKind.Utc),
                    Dur = TimeSpan.FromMinutes(150), Flag = true, Plain = false, Status = St.Closed, Scores = [3], Codes = [100]
                });
            seed.SaveChanges();
        }

        // Premise: the stored forms really differ from the CLR values.
        var raw = collection.Database.GetCollection<BsonDocument>(collection.CollectionNamespace.CollectionName)
            .Find(Builders<BsonDocument>.Filter.Eq("Seq", 1)).Single();
        Assert.Equal("pfx-abcdef", raw["Name"].AsString);
        Assert.Equal(BsonType.String, raw["When"].BsonType);
        Assert.Equal(BsonType.String, raw["Dur"].BsonType);
        Assert.Equal(flag == FlagStorage.FlagAsString ? BsonType.String : BsonType.Boolean, raw["Flag"].BsonType);
        Assert.Equal("1,20", raw["Codes"].AsString);
        Assert.Equal(BsonType.Int32, raw["Scores"].AsBsonArray[0].BsonType);

        var shape = (refused ? RichRefusedQueries : RichWorkingQueries)[query];
        string oracle;
        using (var materializing = Create(MongoQueryMode.Native))
        {
            var entities = materializing.Entities.AsNoTracking().ToList();
            Assert.Equal(["abcdef", "ab", "abc"], entities.OrderBy(e => e.Seq).Select(e => e.Name));
            oracle = Normalize(shape(entities.AsQueryable()));
        }

        string Run()
        {
            using var db = Create(mode);
            return Normalize(shape(db.Entities.AsNoTracking()));
        }

        var ex = Record.Exception(Run);
        if (!refused)
        {
            // Must answer, and answer right; NativeOnly may decline cleanly (pre-existing native declines over
            // non-default storage).
            if (ex is not null)
            {
                Assert.True(
                    mode == MongoQueryMode.NativeOnly && ex is NativeTranslationNotSupportedException,
                    $"'{query}' ({flag}) threw under {mode}: {ex}");
                return;
            }

            Assert.Equal(oracle, Run());
            return;
        }

        if (ex is null)
        {
            Assert.Equal(oracle, Run()); // correct or loud
        }

        Assert.NotNull(ex);
        if (mode == MongoQueryMode.NativeOnly)
        {
            Assert.IsType<NativeTranslationNotSupportedException>(ex);
        }
        else
        {
            Assert.Matches(@"Rich\.(Name|When|Dur|Flag|Codes)'", Assert.IsType<NotSupportedException>(ex).Message);
        }
    }

    private static string Normalize(object? value)
        => value switch
        {
            null => "null",
            string s => s,
            double d => d.ToString("0.#########", CultureInfo.InvariantCulture),
            IEnumerable e => "[" + string.Join(",", e.Cast<object?>().Select(Normalize)) + "]",
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? ""
        };
}
