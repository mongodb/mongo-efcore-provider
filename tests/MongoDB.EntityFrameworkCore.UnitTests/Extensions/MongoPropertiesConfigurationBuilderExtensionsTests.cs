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

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MongoDB.Bson;

namespace MongoDB.EntityFrameworkCore.UnitTests.Extensions;

public class MongoPropertiesConfigurationBuilderExtensionsTests
{
    [Theory]
    [InlineData(BsonType.String)]
    [InlineData(BsonType.Double)]
    [InlineData(BsonType.Decimal128)]
    [InlineData(BsonType.Int64)]
    public void HaveBsonRepresentation_can_set_type_on_property(BsonType bsonType)
    {
        using var db = new TestDbContext(bsonType);

        var property = db.GetProperty((TestEntity t) => t.DateTimeOffsetProperty);
        Assert.NotNull(property);

        var representation = property.GetBsonRepresentation();
        Assert.NotNull(representation);
        Assert.Equal(bsonType, representation.BsonType);
    }

    [Theory]
    [InlineData(BsonType.String, false, true)]
    [InlineData(BsonType.Double, false, false)]
    [InlineData(BsonType.Decimal128, null, false)]
    [InlineData(BsonType.Int64, true, true)]
    public void HaveBsonRepresentation_can_set_type_overflow_and_truncation_on_property(BsonType bsonType, bool? allowOverflow,
        bool? allowTruncation)
    {
        using var db = new TestDbContext(bsonType, allowOverflow, allowTruncation);

        var property = db.GetProperty((TestEntity t) => t.DateTimeOffsetProperty);
        Assert.NotNull(property);

        var representation = property.GetBsonRepresentation();
        Assert.NotNull(representation);
        Assert.Equal(bsonType, representation.BsonType);
        Assert.Equal(allowOverflow, representation.AllowOverflow);
        Assert.Equal(allowTruncation, representation.AllowTruncation);
    }

    [Theory]
    [InlineData(DateTimeKind.Unspecified)]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Utc)]
    public void HaveDateTimeKind_can_set_DateTimeKind_on_property(DateTimeKind dateTimeKind)
    {
        using var db = new TestDbContext(dateTimeKind: dateTimeKind);

        var property = db.GetProperty((TestEntity t) => t.DateTimeOffsetProperty);
        Assert.NotNull(property);

        var foundDateTimeKind = property.GetDateTimeKind();
        Assert.Equal(dateTimeKind, foundDateTimeKind);
    }

    private class TestEntity
    {
        public int Id { get; set; }

        public DateTimeOffset DateTimeOffsetProperty { get; set; }

        public DateTimeOffset? NullableDateTimeOffsetProperty { get; set; }

        public string StringProperty { get; set; }
    }

    private class TestDbContext(
        BsonType? bsonType = null,
        bool? allowOverflow = null,
        bool? allowTruncation = null,
        DateTimeKind? dateTimeKind = null) : DbContext
    {
        public DbSet<TestEntity> Tests { get; set; }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
            => optionsBuilder
                .UseMongoDB("mongodb://localhost:27017", $"UnitTests{Guid.NewGuid()}")
                .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));

        protected override void ConfigureConventions(ModelConfigurationBuilder cb)
        {
            base.ConfigureConventions(cb);

            if (bsonType != null)
            {
                cb.Properties<DateTimeOffset>().HaveBsonRepresentation(bsonType.Value, allowOverflow, allowTruncation);
            }

            if (dateTimeKind != null)
            {
                cb.Properties<DateTimeOffset>().HaveDateTimeKind(dateTimeKind.Value);
            }
        }
    }
}
