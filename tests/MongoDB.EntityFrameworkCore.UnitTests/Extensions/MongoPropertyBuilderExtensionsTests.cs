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

namespace MongoDB.EntityFrameworkCore.UnitTests.Extensions;

public class MongoPropertyBuilderExtensionsTests
{
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
        public DateTime DateTimeProperty { get; set; }

        public DateTime? NullableDateTimeProperty { get; set; }

        public string StringProperty { get; set; }
    }
}
