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

using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using MongoDB.Bson;

// ReSharper disable UseCollectionExpression - XUnit.NET TheoryData does not support collection initializers

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Mapping;

[XUnitCollection("MappingTests")]
public class BsonTypeTests(TemporaryDatabaseFixture tempDatabase)
    : IClassFixture<TemporaryDatabaseFixture>
{
    [Fact]
    public void String_clr_with_ObjectId_storage()
        => Exerciser.TestConvertedIdRoundTrip(tempDatabase,
            ObjectId.GenerateNewId().ToString(),
            a => new ObjectId(a),
            mb => mb.Entity<EntityWithId<string>>().Property(e => e._id).HasBsonRepresentation(BsonType.ObjectId));

    public static readonly TheoryData<decimal> ConstrainedDecimalData
        = new() {0m, 1m, -1m, 1.1m, -1.1m};

    [Theory]
    [MemberData(nameof(ConstrainedDecimalData))]
    public void Decimal_clr_with_Decimal128_storage(decimal expectedValue)
        => Exerciser.TestConvertedValueRoundTrip(tempDatabase,
            expectedValue,
            a => new Decimal128(a),
            mb => mb.Entity<EntityWithId<ObjectId>>().Property(e => e._id).HasBsonRepresentation(BsonType.Decimal128));

    public static readonly TheoryData<int> IntData
        = new() {0, 1, -1, int.MaxValue, int.MinValue};

    [Theory]
    [MemberData(nameof(IntData))]
    public void Int_clr_with_String_storage(int expectedValue)
        => TestValueRoundTripToString(expectedValue);

    public static readonly TheoryData<double> DoubleData
        = new() {0d, 1d, -1d, 1.2d, -1.2d, double.MaxValue, double.MinValue};

    [Theory]
    [MemberData(nameof(DoubleData))]
    public void Double_clr_with_String_storage(double expectedValue)
        => TestValueRoundTripToString(expectedValue);

    public static readonly TheoryData<float> FloatData
        = new() {0f, 1f, -1f, 1.1f, -1.1f, float.MaxValue, float.MinValue};

    [Theory]
    [MemberData(nameof(FloatData))]
    public void Float_clr_with_String_storage(float expectedValue)
        => TestValueRoundTripToString(expectedValue);

    public static readonly TheoryData<decimal> DecimalData
        = new() {0m, 1m, -1m, 1.1m, -1.1m, decimal.MaxValue, decimal.MinValue};

    [Theory]
    [MemberData(nameof(DecimalData))]
    public void Decimal_clr_with_String_storage(decimal expectedValue)
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

    public static readonly TheoryData<TimeSpan> TimeSpanData
        = new() {TimeSpan.Zero, TimeSpan.MaxValue, TimeSpan.MinValue, TimeSpan.FromDays(2), TimeSpan.FromHours(-1)};

    [Theory]
    [MemberData(nameof(TimeSpanData))]
    public void TimeSpan_clr_with_String_storage(TimeSpan expectedValue)
        => TestValueRoundTripToString(expectedValue);

    public static readonly TheoryData<DateTime> DateTimeData
        = new() { DateTime.Now, DateTime.Now.AddYears(500), DateTime.Now.AddYears(-500) };

    [Theory]
    [MemberData(nameof(DateTimeData))]
    public void DateTime_clr_with_String_storage(DateTime expectedValue)
        => Exerciser.TestConvertedValueRoundTrip(tempDatabase,
            expectedValue.ToBsonPrecision(),
            converter: a => a.ToBsonPrecision().ToUniversalTime(),
            mb => mb.Entity<EntityWithValue<DateTime>>().Property(e => e.value)
                .HasDateTimeKind(DateTimeKind.Local)
                .HasBsonRepresentation(BsonType.String));

    public static readonly TheoryData<DateTime> UtcDateTimeData
        = new() { DateTime.UtcNow, DateTime.UtcNow.AddYears(500), DateTime.UtcNow.AddYears(-500) };

    [Theory]
    [MemberData(nameof(UtcDateTimeData))]
    public void DateTime_clr_with_DateTime_storage(DateTime expectedValue)
        => Exerciser.TestConvertedValueRoundTrip(tempDatabase,
            expectedValue,
            a => a.ToBsonPrecision(),
            mb => mb.Entity<EntityWithValue<DateTime>>().Property(e => e.value)
                .HasDateTimeKind(DateTimeKind.Utc)
                .HasBsonRepresentation(BsonType.DateTime),
            d => d.ToBsonPrecision());

    private void TestValueRoundTripToString<T>(
        T expectedValue,
        [CallerMemberName] string? caller = default)
    {
        Exerciser.TestConvertedValueRoundTrip(tempDatabase,
            expectedValue,
            a => a?.ToString(),
            mb => mb.Entity<EntityWithValue<T>>().Property(e => e.value).HasBsonRepresentation(BsonType.String),
            caller: caller);
    }
}
