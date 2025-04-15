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

using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using MongoDB.Bson;

// ReSharper disable UseCollectionExpression - XUnit.NET TheoryData does not support collection initializers

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Mapping;

[XUnitCollection("MappingTests")]
public class BsonTypeTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    [Fact]
    public void String_clr_id_with_ObjectId_storage()
        => Exerciser.TestConvertedIdRoundTrip(database,
            ObjectId.GenerateNewId().ToString(),
            a => new ObjectId(a),
            mb => mb.Entity<EntityWithId<string>>().Property(e => e._id).HasBsonRepresentation(BsonType.ObjectId));

    [Fact]
    public void ObjectId_clr_id_with_String_storage()
        => Exerciser.TestConvertedIdRoundTrip(database,
            ObjectId.GenerateNewId(),
            a => a,
            mb => mb.Entity<EntityWithId<ObjectId>>().Property(e => e._id).HasBsonRepresentation(BsonType.String));

    public static readonly TheoryData<int> IntData
        = new()
        {
            0,
            1,
            -1,
            int.MaxValue,
            int.MinValue,
        };

    [Theory]
    [MemberData(nameof(IntData))]
    public void Int_clr_with_String_storage(int expectedValue)
        => TestValueRoundTripToString(expectedValue);

    public static readonly TheoryData<int?> NullableIntData
        = new()
        {
            0,
            1,
            -1,
            int.MaxValue,
            int.MinValue,
            null
        };

    [Theory]
    [MemberData(nameof(NullableIntData))]
    public void Nullable_Int_clr_with_String_storage(int? expectedValue)
        => TestValueRoundTripToString(expectedValue);

    public static readonly TheoryData<double> DoubleData
        = new()
        {
            0d,
            1d,
            -1d,
            1.2d,
            -1.2d,
            double.MaxValue,
            double.MinValue
        };

    [Theory]
    [MemberData(nameof(DoubleData))]
    public void Double_clr_with_String_storage(double expectedValue)
        => TestValueRoundTripToString(expectedValue);

    public static readonly TheoryData<double?> NullableDoubleData
        = new()
        {
            0d,
            1d,
            -1d,
            1.2d,
            -1.2d,
            double.MaxValue,
            double.MinValue,
            null
        };

    [Theory]
    [MemberData(nameof(NullableDoubleData))]
    public void Nullable_Double_clr_with_String_storage(double? expectedValue)
        => TestValueRoundTripToString(expectedValue);

    public static readonly TheoryData<float> FloatData
        = new()
        {
            0f,
            1f,
            -1f,
            1.1f,
            -1.1f,
            float.MaxValue,
            float.MinValue
        };

    [Theory]
    [MemberData(nameof(FloatData))]
    public void Float_clr_with_String_storage(float expectedValue)
        => TestValueRoundTripToString(expectedValue);

    public static readonly TheoryData<float?> NullableFloatData
        = new()
        {
            0f,
            1f,
            -1f,
            1.1f,
            -1.1f,
            float.MaxValue,
            float.MinValue,
            null
        };

    [Theory]
    [MemberData(nameof(NullableFloatData))]
    public void Nullable_Float_clr_with_String_storage(float? expectedValue)
        => TestValueRoundTripToString(expectedValue);

    public static readonly TheoryData<decimal> DecimalData
        = new()
        {
            0m,
            1m,
            -1m,
            1.1m,
            -1.1m,
            decimal.MaxValue,
            decimal.MinValue
        };

    [Theory]
    [MemberData(nameof(DecimalData))]
    public void Decimal_clr_with_String_storage(decimal expectedValue)
        => TestValueRoundTripToString(expectedValue);

    public static readonly TheoryData<decimal?> NullableDecimalData
        = new()
        {
            0m,
            1m,
            -1m,
            1.1m,
            -1.1m,
            decimal.MaxValue,
            decimal.MinValue,
            null
        };

    [Theory]
    [MemberData(nameof(NullableDecimalData))]
    public void Nullable_Decimal_clr_with_String_storage(decimal? expectedValue)
        => TestValueRoundTripToString(expectedValue);

    public static readonly TheoryData<Guid> GuidData
        = new()
        {
            Guid.Empty,
            Guid.NewGuid(),
            Guid.Parse("f81d4fae-7dec-11d0-a765-00a0c91e6bf6"),
            Guid.Parse("6ba7b810-9dad-11d1-80b4-00c04fd430c8"),
            Guid.Parse("27701bfc-78d0-4e2b-92ca-193cea53fa30"),
            Guid.Parse("1c8b3375-a836-5722-9e30-570df2fe82e5"),
            Guid.Parse("9c115909-ac69-48ee-9583-b972a69fbc51")
        };

    [Theory]
    [MemberData(nameof(GuidData))]
    public void Guid_clr_with_String_storage(Guid expectedValue)
        => TestValueRoundTripToString(expectedValue);

    public static readonly TheoryData<Guid?> NullableGuidData
        = new()
        {
            Guid.Empty,
            Guid.NewGuid(),
            Guid.Parse("f81d4fae-7dec-11d0-a765-00a0c91e6bf6"),
            Guid.Parse("6ba7b810-9dad-11d1-80b4-00c04fd430c8"),
            Guid.Parse("27701bfc-78d0-4e2b-92ca-193cea53fa30"),
            Guid.Parse("1c8b3375-a836-5722-9e30-570df2fe82e5"),
            Guid.Parse("9c115909-ac69-48ee-9583-b972a69fbc51"),
            null
        };

    [Theory]
    [MemberData(nameof(NullableGuidData))]
    public void Nullable_Guid_clr_with_String_storage(Guid? expectedValue)
        => TestValueRoundTripToString(expectedValue);

    public static readonly TheoryData<TimeSpan> TimeSpanData
        = new()
        {
            TimeSpan.Zero,
            TimeSpan.MaxValue,
            TimeSpan.MinValue,
            TimeSpan.FromDays(2),
            TimeSpan.FromHours(-1)
        };

    [Theory]
    [MemberData(nameof(TimeSpanData))]
    public void TimeSpan_clr_with_String_storage(TimeSpan expectedValue)
        => TestValueRoundTripToString(expectedValue);

    public static readonly TheoryData<TimeSpan?> NullableTimeSpanData
        = new()
        {
            TimeSpan.Zero,
            TimeSpan.MaxValue,
            TimeSpan.MinValue,
            TimeSpan.FromDays(2),
            TimeSpan.FromHours(-1),
            null
        };

    [Theory]
    [MemberData(nameof(NullableTimeSpanData))]
    public void Nullable_TimeSpan_clr_with_String_storage(TimeSpan? expectedValue)
        => TestValueRoundTripToString(expectedValue);

    public static readonly TheoryData<DateTime> DateTimeData
        = new() {DateTime.Now, DateTime.Now.AddYears(500), DateTime.Now.AddYears(-500)};

    [Theory]
    [MemberData(nameof(DateTimeData))]
    public void DateTime_clr_with_String_storage(DateTime expectedValue)
        => Exerciser.TestConvertedValueRoundTrip(database,
            expectedValue.ToBsonPrecision(),
            a => a.ToBsonPrecision().ToString("yyyy-MM-ddTHH:mm:ss.FFFK", CultureInfo.InvariantCulture),
            mb => mb.Entity<EntityWithValue<DateTime>>().Property(e => e.value)
                .HasDateTimeKind(DateTimeKind.Local)
                .HasBsonRepresentation(BsonType.String));

    public static readonly TheoryData<DateTime?> NullableDateTimeData
        = new() {DateTime.Now, DateTime.Now.AddYears(500), DateTime.Now.AddYears(-500), null};

    [Theory]
    [MemberData(nameof(NullableDateTimeData))]
    public void Nullable_DateTime_clr_with_String_storage(DateTime? expectedValue)
        => Exerciser.TestConvertedValueRoundTrip(database,
            expectedValue?.ToBsonPrecision(),
            a => a?.ToBsonPrecision().ToString("yyyy-MM-ddTHH:mm:ss.FFFK", CultureInfo.InvariantCulture),
            mb => mb.Entity<EntityWithValue<DateTime?>>().Property(e => e.value)
                .HasDateTimeKind(DateTimeKind.Local)
                .HasBsonRepresentation(BsonType.String));

    public static readonly TheoryData<DateTime> UtcDateTimeData
        = new() {DateTime.UtcNow, DateTime.UtcNow.AddYears(500), DateTime.UtcNow.AddYears(-500)};

    [Theory]
    [MemberData(nameof(UtcDateTimeData))]
    public void DateTime_clr_with_DateTime_storage(DateTime expectedValue)
        => Exerciser.TestConvertedValueRoundTrip(database,
            expectedValue,
            a => a.ToBsonPrecision(),
            mb => mb.Entity<EntityWithValue<DateTime>>().Property(e => e.value)
                .HasDateTimeKind(DateTimeKind.Utc)
                .HasBsonRepresentation(BsonType.DateTime),
            d => d.ToBsonPrecision());

    public static readonly TheoryData<DateTime?> NullableUtcDateTimeData
        = new() {DateTime.UtcNow, DateTime.UtcNow.AddYears(500), DateTime.UtcNow.AddYears(-500), null};

    [Theory]
    [MemberData(nameof(NullableUtcDateTimeData))]
    public void Nullable_DateTime_clr_with_DateTime_storage(DateTime? expectedValue)
        => Exerciser.TestConvertedValueRoundTrip(database,
            expectedValue,
            a => a?.ToBsonPrecision(),
            mb => mb.Entity<EntityWithValue<DateTime>>().Property(e => e.value)
                .HasDateTimeKind(DateTimeKind.Utc)
                .HasBsonRepresentation(BsonType.DateTime),
            d => d?.ToBsonPrecision());

    public static readonly TheoryData<DateOnly> DateOnlyData
        = new() {DateOnly.MaxValue, DateOnly.MinValue, new DateOnly(2024, 10, 05)};

    [Theory]
    [MemberData(nameof(DateOnlyData))]
    public void DateOnly_clr_with_String_storage(DateOnly expectedValue)
        => Exerciser.TestConvertedValueRoundTrip(database,
            expectedValue,
            a => a.ToString("yyyy-MM-dd"),
            mb => mb.Entity<EntityWithValue<DateOnly>>().Property(e => e.value).HasBsonRepresentation(BsonType.String));

    public static readonly TheoryData<DateOnly?> NullableDateOnlyData
        = new() {DateOnly.MaxValue, DateOnly.MinValue, new DateOnly(2024, 10, 05), null};

    [Theory]
    [MemberData(nameof(NullableDateOnlyData))]
    public void Nullable_DateOnly_clr_with_String_storage(DateOnly? expectedValue)
        => Exerciser.TestConvertedValueRoundTrip(database,
            expectedValue,
            a => a?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            mb => mb.Entity<EntityWithValue<DateOnly?>>().Property(e => e.value)
                .HasBsonRepresentation(BsonType.String));

    public static readonly TheoryData<TimeOnly> TimeOnlyData
        = new() {TimeOnly.MaxValue, TimeOnly.MinValue, new TimeOnly(13, 10, 05)};

    [Theory]
    [MemberData(nameof(TimeOnlyData))]
    public void TimeOnly_clr_with_String_storage(TimeOnly expectedValue)
        => Exerciser.TestConvertedValueRoundTrip(database,
            expectedValue,
            a => a.ToString("HH:mm:ss.fffffff"),
            mb => mb.Entity<EntityWithValue<TimeOnly>>().Property(e => e.value).HasBsonRepresentation(BsonType.String));

    public static readonly TheoryData<TimeOnly?> NullableTimeOnlyData
        = new() {TimeOnly.MaxValue, TimeOnly.MinValue, new TimeOnly(13, 10, 05), null};

    [Theory]
    [MemberData(nameof(NullableTimeOnlyData))]
    public void Nullable_TimeOnly_clr_with_String_storage(TimeOnly? expectedValue)
        => Exerciser.TestConvertedValueRoundTrip(database,
            expectedValue,
            a => a?.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture),
            mb => mb.Entity<EntityWithValue<TimeOnly?>>().Property(e => e.value)
                .HasBsonRepresentation(BsonType.String));

    public static readonly TheoryData<decimal> ConstrainedDecimalData
        = new()
        {
            0m,
            1m,
            -1m,
            1.1m,
            -1.1m
        };

    [Theory]
    [MemberData(nameof(ConstrainedDecimalData))]
    public void Decimal_clr_with_Decimal128_storage(decimal expectedValue)
        => Exerciser.TestConvertedValueRoundTrip(database,
            expectedValue,
            a => new Decimal128(a),
            mb => mb.Entity<EntityWithValue<ObjectId>>().Property(e => e.value).HasBsonRepresentation(BsonType.Decimal128));

    public static readonly TheoryData<decimal?> ConstrainedNullableDecimalData
        = new()
        {
            0m,
            1m,
            -1m,
            1.1m,
            -1.1m,
            null
        };

    [Theory]
    [MemberData(nameof(ConstrainedNullableDecimalData))]
    public void Nullable_Decimal_clr_with_Decimal128_storage(decimal? expectedValue)
        => Exerciser.TestConvertedValueRoundTrip(database,
            expectedValue,
            a => a,
            mb => mb.Entity<EntityWithValue<ObjectId>>().Property(e => e.value).HasBsonRepresentation(BsonType.Decimal128));

    private void TestValueRoundTripToString<T>(
        T expectedValue,
        [CallerMemberName] string? caller = default)
    {
        Exerciser.TestConvertedValueRoundTrip(database,
            expectedValue,
            a => a == null ? null : string.Format(CultureInfo.InvariantCulture, "{0}", a),
            mb => mb.Entity<EntityWithValue<T>>().Property(e => e.value).HasBsonRepresentation(BsonType.String),
            caller: caller);
    }
}
