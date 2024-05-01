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

using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson.Serialization.Options;

namespace MongoDB.EntityFrameworkCore.UnitTests.Metadata.Conventions.BsonAttributes;

public static class BsonUnsupportedAttributeConventionTests
{
    [Fact]
    public static void BsonSerializer_is_not_supported_on_property()
    {
        using var db = SingleEntityDbContext.Create<EntityWithBsonSerializerProperty>();

        var ex = Assert.Throws<NotSupportedException>(()
            => db.GetProperty((EntityWithBsonSerializerProperty e) => e.AttributedSerializer));
        Assert.Contains(
            $"'{nameof(EntityWithBsonSerializerProperty)}.{nameof(EntityWithBsonSerializerProperty.AttributedSerializer)}'",
            ex.Message);
        Assert.Contains($"'{nameof(BsonSerializerAttribute)}'", ex.Message);
    }

    class EntityWithBsonSerializerProperty
    {
        public int _id { get; set; }

        [BsonSerializer]
        public string AttributedSerializer { get; set; }
    }

    [Fact]
    public static void BsonExtraElements_is_not_supported_on_property()
    {
        using var db = SingleEntityDbContext.Create<EntityWithBsonExtraElementsProperty>();

        var ex = Assert.Throws<NotSupportedException>(()
            => db.GetProperty((EntityWithBsonExtraElementsProperty e) => e.AttributedSerializer));
        Assert.Contains(
            $"'{nameof(EntityWithBsonExtraElementsProperty)}.{nameof(EntityWithBsonExtraElementsProperty.AttributedSerializer)}'",
            ex.Message);
        Assert.Contains($"'{nameof(BsonExtraElementsAttribute)}'", ex.Message);
    }

    class EntityWithBsonExtraElementsProperty
    {
        public int _id { get; set; }

        [BsonExtraElements]
        public string AttributedSerializer { get; set; }
    }

    [Fact]
    public static void BsonRepresentation_is_not_supported_on_property()
    {
        using var db = SingleEntityDbContext.Create<EntityWithBsonRepresentationProperty>();

        var ex = Assert.Throws<NotSupportedException>(()
            => db.GetProperty((EntityWithBsonRepresentationProperty e) => e.AttributedSerializer));
        Assert.Contains(
            $"'{nameof(EntityWithBsonRepresentationProperty)}.{nameof(EntityWithBsonRepresentationProperty.AttributedSerializer)}'",
            ex.Message);
        Assert.Contains($"'{nameof(BsonRepresentationAttribute)}'", ex.Message);
    }

    class EntityWithBsonRepresentationProperty
    {
        public int _id { get; set; }

        [BsonRepresentation(BsonType.Binary)]
        public string AttributedSerializer { get; set; }
    }

    [Fact]
    public static void BsonDefaultValue_is_not_supported_on_property()
    {
        using var db = SingleEntityDbContext.Create<EntityWithBsonDefaultValueProperty>();

        var ex = Assert.Throws<NotSupportedException>(()
            => db.GetProperty((EntityWithBsonDefaultValueProperty e) => e.AttributedSerializer));
        Assert.Contains(
            $"'{nameof(EntityWithBsonDefaultValueProperty)}.{nameof(EntityWithBsonDefaultValueProperty.AttributedSerializer)}'",
            ex.Message);
        Assert.Contains($"'{nameof(BsonDefaultValueAttribute)}'", ex.Message);
    }

    class EntityWithBsonDefaultValueProperty
    {
        public int _id { get; set; }

        [BsonDefaultValue("Test")]
        public string AttributedSerializer { get; set; }
    }

    [Fact]
    public static void BsonGuidRepresentation_is_not_supported_on_property()
    {
        using var db = SingleEntityDbContext.Create<EntityWithBsonGuidRepresentationProperty>();

        var ex = Assert.Throws<NotSupportedException>(()
            => db.GetProperty((EntityWithBsonGuidRepresentationProperty e) => e.AttributedSerializer));
        Assert.Contains(
            $"'{nameof(EntityWithBsonGuidRepresentationProperty)}.{
                nameof(EntityWithBsonGuidRepresentationProperty.AttributedSerializer)}'", ex.Message);
        Assert.Contains($"'{nameof(BsonGuidRepresentationAttribute)}'", ex.Message);
    }

    class EntityWithBsonGuidRepresentationProperty
    {
        public int _id { get; set; }

        [BsonGuidRepresentation(GuidRepresentation.CSharpLegacy)]
        public string AttributedSerializer { get; set; }
    }

    [Fact]
    public static void BsonTimeSpanOptions_is_not_supported_on_property()
    {
        using var db = SingleEntityDbContext.Create<EntityWithBsonTimeSpanOptionsProperty>();

        var ex = Assert.Throws<NotSupportedException>(()
            => db.GetProperty((EntityWithBsonTimeSpanOptionsProperty e) => e.AttributedSerializer));
        Assert.Contains(
            $"'{nameof(EntityWithBsonTimeSpanOptionsProperty)}.{nameof(EntityWithBsonTimeSpanOptionsProperty.AttributedSerializer)
            }'", ex.Message);
        Assert.Contains($"'{nameof(BsonTimeSpanOptionsAttribute)}'", ex.Message);
    }

    class EntityWithBsonTimeSpanOptionsProperty
    {
        public int _id { get; set; }

        [BsonTimeSpanOptions(BsonType.Int32, TimeSpanUnits.Seconds)]
        public string AttributedSerializer { get; set; }
    }

    [Fact]
    public static void BsonDictionaryOptions_is_not_supported_on_property()
    {
        using var db = SingleEntityDbContext.Create<EntityWithBsonDictionaryOptionsProperty>();

        var ex = Assert.Throws<NotSupportedException>(()
            => db.GetProperty((EntityWithBsonDictionaryOptionsProperty e) => e.AttributedSerializer));
        Assert.Contains(
            $"'{nameof(EntityWithBsonDictionaryOptionsProperty)}.{
                nameof(EntityWithBsonDictionaryOptionsProperty.AttributedSerializer)}'", ex.Message);
        Assert.Contains($"'{nameof(BsonDictionaryOptionsAttribute)}'", ex.Message);
    }

    class EntityWithBsonDictionaryOptionsProperty
    {
        public int _id { get; set; }

        [BsonDictionaryOptions]
        public string AttributedSerializer { get; set; }
    }

    [Fact]
    public static void BsonIgnoreIfDefault_is_not_supported_on_property()
    {
        using var db = SingleEntityDbContext.Create<EntityWithBsonIgnoreIfDefaultProperty>();

        var ex = Assert.Throws<NotSupportedException>(()
            => db.GetProperty((EntityWithBsonIgnoreIfDefaultProperty e) => e.AttributedSerializer));
        Assert.Contains(
            $"'{nameof(EntityWithBsonIgnoreIfDefaultProperty)}.{nameof(EntityWithBsonIgnoreIfDefaultProperty.AttributedSerializer)
            }'", ex.Message);
        Assert.Contains($"'{nameof(BsonIgnoreIfDefaultAttribute)}'", ex.Message);
    }

    class EntityWithBsonIgnoreIfDefaultProperty
    {
        public int _id { get; set; }

        [BsonIgnoreIfDefault]
        public string AttributedSerializer { get; set; }
    }

    [Fact]
    public static void BsonIgnoreIfNull_is_not_supported_on_property()
    {
        using var db = SingleEntityDbContext.Create<EntityWithBsonIgnoreIfNullProperty>();

        var ex = Assert.Throws<NotSupportedException>(()
            => db.GetProperty((EntityWithBsonIgnoreIfNullProperty e) => e.AttributedSerializer));
        Assert.Contains(
            $"'{nameof(EntityWithBsonIgnoreIfNullProperty)}.{nameof(EntityWithBsonIgnoreIfNullProperty.AttributedSerializer)}'",
            ex.Message);
        Assert.Contains($"'{nameof(BsonIgnoreIfNullAttribute)}'", ex.Message);
    }

    class EntityWithBsonIgnoreIfNullProperty
    {
        public int _id { get; set; }

        [BsonIgnoreIfNull]
        public string AttributedSerializer { get; set; }
    }
}
