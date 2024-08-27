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
using MongoDB.EntityFrameworkCore.Metadata.Conventions;
using MongoDB.EntityFrameworkCore.Storage;

namespace MongoDB.EntityFrameworkCore.UnitTests.Storage;

public class BsonBindingTests
{
    [Fact]
    public void Read_element_returns_value()
    {
        var document = BsonDocument.Parse("{ property: 12 }");

        var value = BsonBinding.GetElementValue<int>(document, "property");

        Assert.Equal(12, value);
    }

    [Fact]
    public void Read_missing_element_throws()
    {
        var document = BsonDocument.Parse("{ property: 12 }");

        var ex = Assert.Throws<InvalidOperationException>(() => BsonBinding.GetElementValue<int>(document, "missedElementName"));

        Assert.Contains("missedElementName", ex.Message);
    }

    [Fact]
    public void Read_missing_nullable_element_returns_default()
    {
        var document = BsonDocument.Parse("{ property: 12 }");

        var value = BsonBinding.GetElementValue<int?>(document, "missedElementName");

        Assert.Null(value);
    }

    [Fact]
    public void Read_property_value()
    {
        var conventions = MongoConventionSetBuilder.Build();
        var modelBuilder = new ModelBuilder(conventions);
        modelBuilder.Entity<TestEntity>();

        var entity = modelBuilder.Model.FindEntityType(typeof(TestEntity));
        Assert.NotNull(entity);
        var property = entity.GetProperty(nameof(TestEntity.IntProperty));
        var document = BsonDocument.Parse("{ IntProperty: 12 }");

        var value = BsonBinding.GetPropertyValue<int>(document, property);

        Assert.Equal(12, value);
    }

    [Fact]
    public void Read_missing_property_throws()
    {
        var conventions = MongoConventionSetBuilder.Build();
        var modelBuilder = new ModelBuilder(conventions);
        modelBuilder.Entity<TestEntity>();

        var entity = modelBuilder.Model.FindEntityType(typeof(TestEntity));
        Assert.NotNull(entity);
        var property = entity.GetProperty(nameof(TestEntity.IntProperty));
        var document = BsonDocument.Parse("{ property: 12 }");

        var ex = Assert.Throws<InvalidOperationException>(() => BsonBinding.GetPropertyValue<int>(document, property));
        Assert.Contains("IntProperty", ex.Message);
    }

    [Fact]
    public void Read_missing_nullable_property_returns_default()
    {
        var conventions = MongoConventionSetBuilder.Build();
        var modelBuilder = new ModelBuilder(conventions);
        modelBuilder.Entity<TestEntity>();

        var entity = modelBuilder.Model.FindEntityType(typeof(TestEntity));
        Assert.NotNull(entity);
        var property = entity.GetProperty(nameof(TestEntity.NullableProperty));

        var document = BsonDocument.Parse("{ somevalue: 12 }");
        var value = BsonBinding.GetPropertyValue<int?>(document, property);

        Assert.Null(value);
    }

    public class TestEntity
    {
        public int IntProperty { get; set; }

        public int? NullableProperty { get; set; }
    }
}
