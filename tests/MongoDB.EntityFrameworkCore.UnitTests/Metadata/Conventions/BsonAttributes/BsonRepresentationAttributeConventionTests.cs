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
using MongoDB.Bson.Serialization.Attributes;

namespace MongoDB.EntityFrameworkCore.UnitTests.Metadata.Conventions.BsonAttributes;

public static class BsonRepresentationAttributeConventionTests
{
    [Fact]
    public static void BsonRepresentation_specified_properties_are_set()
    {
        using var db = SingleEntityDbContext.Create<RepresentedEntity>();

        var intProperty = db.GetProperty((RepresentedEntity r) => r.anInt);
        Assert.NotNull(intProperty);
        Assert.Equal(BsonType.String, intProperty.GetBsonRepresentation()?.BsonType);

        var longProperty = db.GetProperty((RepresentedEntity r) => r.aLong);
        Assert.NotNull(longProperty);

        var longRepresentation = longProperty.GetBsonRepresentation();
        Assert.NotNull(longRepresentation);

        Assert.Equal(BsonType.Int32, longRepresentation.BsonType);
        Assert.True(longRepresentation.AllowOverflow);
        Assert.True(longRepresentation.AllowTruncation);
    }

    [Fact]
    public static void ModelBuilder_specified_null_unsets_attribute()
    {
        using var db = SingleEntityDbContext.Create<RepresentedEntity>(
            mb => mb.Entity<RepresentedEntity>().Property(r => r.anInt).HasBsonRepresentation(null));

        var property = db.GetProperty((RepresentedEntity r) => r.anInt);
        Assert.NotNull(property);

        Assert.Null(property.GetBsonRepresentation());
    }

    [Fact]
    public static void ModelBuilder_specified_option_overrides_attribute_entirely()
    {
        using var db = SingleEntityDbContext.Create<RepresentedEntity>(
            mb => mb.Entity<RepresentedEntity>().Property(r => r.aLong).HasBsonRepresentation(BsonType.Double));

        var property = db.GetProperty((RepresentedEntity r) => r.aLong);
        Assert.NotNull(property);

        var representation = property.GetBsonRepresentation();
        Assert.NotNull(representation);

        Assert.Equal(BsonType.Double, representation.BsonType);
        Assert.Null(representation.AllowOverflow);
        Assert.Null(representation.AllowTruncation);
    }

    class RepresentedEntity
    {
        public ObjectId _id { get; set; }

        [BsonRepresentation(BsonType.String)]
        public int anInt { get; set; }

        [BsonRepresentation(BsonType.Int32, AllowOverflow = true, AllowTruncation = true)]
        public long aLong { get; set; }
    }
}
