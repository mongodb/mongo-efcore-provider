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
using MongoDB.Bson;

namespace MongoDB.EntityFrameworkCore.UnitTests.Extensions;

public class MongoPropertyBuilderExtensionsTests
{
    [Theory]
    [InlineData("AnotherName")]
    [InlineData("another_name")]
    [InlineData("anotherName")]
    public void HasElementName_can_set_name_on_property(string name)
    {
        var model = new ModelBuilder();
        var entity = model.Entity<TestEntity>();

        entity.Property(e => e.StringProperty).HasElementName(name);

        var property = entity.Metadata.GetProperty(nameof(TestEntity.StringProperty));
        Assert.Equal(name, property.GetElementName());
    }

    [Fact]
    public void HasElementName_can_unset_name_on_property_with_null()
    {
        var model = new ModelBuilder();
        var entity = model.Entity<TestEntity>();

        entity.Property(e => e.StringProperty)
            .HasElementName("Something")
            .HasElementName(null);

        var property = entity.Metadata.GetProperty(nameof(TestEntity.StringProperty));
        Assert.Equal(nameof(TestEntity.StringProperty), property.GetElementName());
    }

    [Fact]
    public void GetElementName_defaults_to_property_name()
    {
        var model = new ModelBuilder();
        var entity = model.Entity<TestEntity>();
        entity.Property(p => p.StringProperty);

        var property = entity.Metadata.GetProperty(nameof(TestEntity.StringProperty));
        Assert.Equal(nameof(TestEntity.StringProperty), property.GetElementName());
    }

    [Fact]
    public void GetElementName_defaults_to_underscore_version_for_RowVersion()
    {
        var model = new ModelBuilder();
        var entity = model.Entity<TestEntity>();
        entity.Property(p => p.IntegerProperty)
            .IsRowVersion();

        var property = entity.Metadata.GetProperty(nameof(TestEntity.IntegerProperty));
        Assert.Equal("_version", property.GetElementName());
    }

    [Fact]
    public void GetElementName_can_set_name_on_RowVersion()
    {
        var model = new ModelBuilder();
        var entity = model.Entity<TestEntity>();
        entity.Property(p => p.IntegerProperty)
            .IsRowVersion()
            .HasElementName("MyCustomVersion");

        var property = entity.Metadata.GetProperty(nameof(TestEntity.IntegerProperty));
        Assert.Equal("MyCustomVersion", property.GetElementName());
    }

    [Fact]
    public void GetElementName_can_unset_name_on_RowVersion()
    {
        var model = new ModelBuilder();
        var entity = model.Entity<TestEntity>();
        entity.Property(p => p.IntegerProperty).IsRowVersion()
            .HasElementName("MyCustomVersion")
            .HasElementName(null);

        var property = entity.Metadata.GetProperty(nameof(TestEntity.IntegerProperty));
        Assert.Equal("_version", property.GetElementName());
    }

    [Theory]
    [InlineData(BsonType.String)]
    [InlineData(BsonType.Double)]
    [InlineData(BsonType.Decimal128)]
    [InlineData(BsonType.Int64)]
    [InlineData(null)]
    public void HasBsonRepresentation_can_set_type_on_property(BsonType? bsonType)
    {
        var model = new ModelBuilder();
        var entity = model.Entity<TestEntity>();

        entity.Property(e => e.DateTimeProperty).HasBsonRepresentation(bsonType);

        var property = entity.Metadata.GetProperty(nameof(TestEntity.DateTimeProperty));
        var representation = property.GetBsonRepresentation();
        if (bsonType == null)
        {
            Assert.Null(representation);
        }
        else
        {
            Assert.NotNull(representation);
            Assert.Equal(bsonType, representation.BsonType);
        }
    }

    [Theory]
    [InlineData(BsonType.String, false, true)]
    [InlineData(BsonType.Double, false, false)]
    [InlineData(BsonType.Decimal128, null, false)]
    [InlineData(BsonType.Int64, true, true)]
    public void HasBsonRepresentation_can_set_type_overflow_and_truncation_on_property(BsonType? bsonType, bool? allowOverflow, bool? allowTruncation)
    {
        var model = new ModelBuilder();
        var entity = model.Entity<TestEntity>();

        entity.Property(e => e.DateTimeProperty).HasBsonRepresentation(bsonType, allowOverflow, allowTruncation);

        var property = entity.Metadata.GetProperty(nameof(TestEntity.DateTimeProperty));
        var representation = property.GetBsonRepresentation();

        Assert.NotNull(representation);
        Assert.Equal(bsonType, representation.BsonType);
        Assert.Equal(allowOverflow, representation.AllowOverflow);
        Assert.Equal(allowTruncation, representation.AllowTruncation);
    }

    [Theory]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Utc)]
    [InlineData(DateTimeKind.Unspecified)]
    public void HasDateTimeKind_can_set_value_on_DateTime_property(DateTimeKind value)
    {
        var model = new ModelBuilder();
        var entity = model.Entity<TestEntity>();

        entity.Property(e => e.DateTimeProperty).HasDateTimeKind(value);

        var property = entity.Metadata.GetProperty(nameof(TestEntity.DateTimeProperty));
        Assert.Equal(value, property.GetDateTimeKind());
    }

    [Theory]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Utc)]
    [InlineData(DateTimeKind.Unspecified)]
    public void HasDateTimeKind_can_set_value_on_NullableDateTime_property(DateTimeKind value)
    {
        var model = new ModelBuilder();
        var entity = model.Entity<TestEntity>();

        entity.Property(e => e.NullableDateTimeProperty).HasDateTimeKind(value);

        var property = entity.Metadata.GetProperty(nameof(TestEntity.NullableDateTimeProperty));
        Assert.Equal(value, property.GetDateTimeKind());
    }

    [Fact]
    public void HasDateTimeKind_throws_on_string_property()
    {
        var model = new ModelBuilder();
        var entity = model.Entity<TestEntity>();

        Assert.Throws<InvalidOperationException>(() =>
        {
            entity.Property(e => e.StringProperty).HasDateTimeKind(DateTimeKind.Local);
        });
    }


    private class TestEntity
    {
        public string StringProperty { get; set; }

        public DateTime DateTimeProperty { get; set; }

        public DateTime? NullableDateTimeProperty { get; set; }

        public int IntegerProperty { get; set; }
    }
}
