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
using MongoDB.EntityFrameworkCore.Metadata.Conventions;

namespace MongoDB.EntityFrameworkCore.UnitTests.Metadata.Conventions;

public class DateTimeKindConventionTests
{
    [Theory]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Utc)]
    [InlineData(DateTimeKind.Unspecified)]
    public static void Can_build_a_model_with_DateTimeKind(DateTimeKind value)
    {
        var conventions = MongoConventionSetBuilder.Build();
        conventions.Add(new DateTimeKindConvention(value));
        var modelBuilder = new ModelBuilder(conventions);
        modelBuilder.Entity<TestEntity>();

        var entity = modelBuilder.Model.FindEntityType(typeof(TestEntity));

        Assert.Equal(value, entity.GetProperty(nameof(TestEntity.DateTimeProperty)).GetDateTimeKind());
        Assert.Equal(value, entity.GetProperty(nameof(TestEntity.NullableDateTimeProperty)).GetDateTimeKind());
        Assert.Equal(DateTimeKind.Unspecified, entity.GetProperty(nameof(TestEntity.StringProperty)).GetDateTimeKind());
    }

    private class TestEntity
    {
        public DateTime DateTimeProperty { get; set; }

        public DateTime? NullableDateTimeProperty { get; set; }

        public string StringProperty { get; set; }
    }
}
