using Microsoft.EntityFrameworkCore;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Metadata.Conventions;
using MongoDB.EntityFrameworkCore.Serializers;

namespace MongoDB.EntityFrameworkCore.UnitTests.Serializers;

public class SerializationHelperTests
{
    [Fact]
    public void Read_element_returns_value()
    {
        var document = BsonDocument.Parse("{ property: 12 }");

        var value = SerializationHelper.GetElementValue<int>(document, "property");

        Assert.Equal(12, value);
    }

    [Fact]
    public void Read_missing_element_throws()
    {
        var document = BsonDocument.Parse("{ property: 12 }");

        var ex = Assert.Throws<InvalidOperationException>(() => SerializationHelper.GetElementValue<int>(document, "missedElementName"));

        Assert.Contains("missedElementName", ex.Message);
    }

    [Fact]
    public void Read_missing_nullable_element_returns_default()
    {
        var document = BsonDocument.Parse("{ property: 12 }");

        var value = SerializationHelper.GetElementValue<int?>(document, "missedElementName");

        Assert.Null(value);
    }

    [Fact]
    public void Read_property_value()
    {
        var conventions = MongoConventionSetBuilder.Build();
        var modelBuilder = new ModelBuilder(conventions);
        modelBuilder.Entity<TestEntity>();

        var entity = modelBuilder.Model.FindEntityType(typeof(TestEntity));
        var property = entity.GetProperty(nameof(TestEntity.IntProperty));
        var document = BsonDocument.Parse("{ IntProperty: 12 }");

        var value = SerializationHelper.GetPropertyValue<int>(document, property);

        Assert.Equal(12, value);
    }

    [Fact]
    public void Read_missing_property_throws()
    {
        var conventions = MongoConventionSetBuilder.Build();
        var modelBuilder = new ModelBuilder(conventions);
        modelBuilder.Entity<TestEntity>();

        var entity = modelBuilder.Model.FindEntityType(typeof(TestEntity));
        var property = entity.GetProperty(nameof(TestEntity.IntProperty));
        var document = BsonDocument.Parse("{ property: 12 }");

        var ex = Assert.Throws<InvalidOperationException>(() => SerializationHelper.GetPropertyValue<int>(document, property));
        Assert.Contains("IntProperty", ex.Message);
    }

    [Fact]
    public void Read_missing_nullable_property_returns_default()
    {
        var conventions = MongoConventionSetBuilder.Build();
        var modelBuilder = new ModelBuilder(conventions);
        modelBuilder.Entity<TestEntity>();

        var entity = modelBuilder.Model.FindEntityType(typeof(TestEntity));
        var property = entity.GetProperty(nameof(TestEntity.NullableProperty));
        var document = BsonDocument.Parse("{ somevalue: 12 }");

        var value = SerializationHelper.GetPropertyValue<int?>(document, property);

        Assert.Null(value);
    }

    public class TestEntity
    {
        public int IntProperty { get; set; }

        public int? NullableProperty { get; set; }
    }
}
