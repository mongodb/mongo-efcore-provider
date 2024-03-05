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
using MongoDB.Bson.Serialization.Attributes;

namespace MongoDB.EntityFrameworkCore.UnitTests.Metadata.Conventions.BsonAttributes;

public static class BsonIdAttributeConventionTests
{
    [Fact]
    public static void BsonId_specified_properties_sets_property_to_key()
    {
        using var context = SingleEntityDbContext.Create<EntityWithBsonId>();

        var attributedProperty = context.GetProperty((EntityWithBsonId e) => e.AttributedKey);

        Assert.NotNull(attributedProperty);
        Assert.Equal("_id", attributedProperty.GetElementName());
        Assert.True(attributedProperty.IsKey());

        var unattributedProperty = context.GetProperty((EntityWithBsonId e) => e.UnattributedKey);
        Assert.NotNull(unattributedProperty);
        Assert.Equal("UnattributedKey", unattributedProperty.GetElementName());
        Assert.False(unattributedProperty.IsKey());
    }

    [Fact]
    public static void ModelBuilder_specified_names_override_BsonId_key()
    {
        using var context = SingleEntityDbContext.Create<EntityWithBsonId>(mb => mb.Entity<EntityWithBsonId>(e =>
        {
            e.Property(p => p.UnattributedKey).HasElementName("_id");
            e.HasKey(p => p.UnattributedKey);
            e.Property(p => p.AttributedKey).HasElementName("notId");
        }));

        var attributedProperty = context.GetProperty((EntityWithBsonId e) => e.AttributedKey);

        Assert.NotNull(attributedProperty);
        Assert.Equal("notId", attributedProperty.GetElementName());
        Assert.False(attributedProperty.IsKey());

        var unattributedProperty = context.GetProperty((EntityWithBsonId e) => e.UnattributedKey);
        Assert.NotNull(unattributedProperty);
        Assert.Equal("_id", unattributedProperty.GetElementName());
        Assert.True(unattributedProperty.IsKey());
    }

    class EntityWithBsonId
    {
        [BsonId]
        public int AttributedKey { get; set; }

        public int UnattributedKey { get; set; }

        public string Name { get; set; }
    }
}
