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

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.EntityFrameworkCore.Extensions;

namespace MongoDB.EntityFrameworkCore.UnitTests.Infrastructure;

public static class MongoModelValidatorTests
{
    class EntityWithTwoUnderscoreIds
    {
        [Column("_id")]
        public int key1 { get; set; }

        [Column("_id")]
        public string key2 { get; set; }
    }

    class DoubleNamedEntity
    {
        public int _id { get; set; }

        public string name1 { get; set; }
        public string name2 { get; set; }
    }

    class ConfiguredIdNamedEntity
    {
        public string ThisWillBePrimaryKey { get; set; }
        public string SomethingElse { get; set; }
    }

    class WithTwoOwnedEntities
    {
        public int _id { get; set; }
        public Location First { get; set; }
        public Location Second { get; set; }
        public string Different { get; set; }
    }

    class Location
    {
        public Decimal Longitude { get; set; }
        public Decimal Latitude { get; set; }
    }

    [Fact]
    public static void Validate_throws_when_multiple_properties_attributed_to_same_element_name()
    {
        using var db = SingleEntityDbContext.Create<EntityWithTwoUnderscoreIds>();

        var ex = Assert.Throws<InvalidOperationException>(() => db.Model);
        Assert.Contains($"'{nameof(EntityWithTwoUnderscoreIds)}'", ex.Message);
        Assert.Contains("'_id'", ex.Message);
        Assert.Contains($"'{nameof(EntityWithTwoUnderscoreIds.key1)}'", ex.Message);
        Assert.Contains($"'{nameof(EntityWithTwoUnderscoreIds.key2)}'", ex.Message);
    }

    [Fact]
    public static void Validate_throws_when_multiple_properties_configured_to_same_element_name()
    {
        using var db = SingleEntityDbContext.Create<DoubleNamedEntity>(mb =>
        {
            var dneBuilder = mb.Entity<DoubleNamedEntity>();
            dneBuilder.Property(p => p.name1).HasElementName("name");
            dneBuilder.Property(p => p.name2).HasElementName("name");
        });

        var ex = Assert.Throws<InvalidOperationException>(() => db.Model);
        Assert.Contains($"'{nameof(DoubleNamedEntity)}'", ex.Message);
        Assert.Contains("'name'", ex.Message);
        Assert.Contains($"'{nameof(DoubleNamedEntity.name1)}'", ex.Message);
        Assert.Contains($"'{nameof(DoubleNamedEntity.name2)}'", ex.Message);
    }

    [Fact]
    public static void Validate_throws_when_property_element_name_starts_with_dollar_sign()
    {
        using var db = SingleEntityDbContext.Create<DoubleNamedEntity>(mb =>
        {
            var dneBuilder = mb.Entity<DoubleNamedEntity>();
            dneBuilder.Property(p => p.name1).HasElementName("$something");
        });

        var ex = Assert.Throws<InvalidOperationException>(() => db.Model);
        Assert.Contains($"'{nameof(DoubleNamedEntity)}'", ex.Message);
        Assert.Contains($"'{nameof(DoubleNamedEntity.name1)}'", ex.Message);
        Assert.Contains("'$something'", ex.Message);
    }

    [Fact]
    public static void Validate_succeeds_if_property_element_name_ends_with_dollar_sign()
    {
        using var db = SingleEntityDbContext.Create<DoubleNamedEntity>(mb =>
        {
            var dneBuilder = mb.Entity<DoubleNamedEntity>();
            dneBuilder.Property(p => p.name1).HasElementName("something$");
        });

        Assert.NotNull(db.Model);
    }

    [Fact]
    public static void Validate_succeeds_if_property_element_name_contains_dollar_sign_not_at_the_start()
    {
        using var db = SingleEntityDbContext.Create<DoubleNamedEntity>(mb =>
        {
            var dneBuilder = mb.Entity<DoubleNamedEntity>();
            dneBuilder.Property(p => p.name1).HasElementName("some$thing");
        });

        Assert.NotNull(db.Model);
    }

    [Fact]
    public static void Validate_throws_when_property_element_name_starts_with_dot()
    {
        using var db = SingleEntityDbContext.Create<DoubleNamedEntity>(mb =>
        {
            var dneBuilder = mb.Entity<DoubleNamedEntity>();
            dneBuilder.Property(p => p.name1).HasElementName(".something");
        });

        var ex = Assert.Throws<InvalidOperationException>(() => db.Model);
        Assert.Contains($"'{nameof(DoubleNamedEntity)}'", ex.Message);
        Assert.Contains($"'{nameof(DoubleNamedEntity.name1)}'", ex.Message);
        Assert.Contains("'.something'", ex.Message);
    }

    [Fact]
    public static void Validate_throws_when_property_element_name_ends_with_dot()
    {
        using var db = SingleEntityDbContext.Create<DoubleNamedEntity>(mb =>
        {
            var dneBuilder = mb.Entity<DoubleNamedEntity>();
            dneBuilder.Property(p => p.name1).HasElementName("something.");
        });

        var ex = Assert.Throws<InvalidOperationException>(() => db.Model);
        Assert.Contains($"'{nameof(DoubleNamedEntity)}'", ex.Message);
        Assert.Contains($"'{nameof(DoubleNamedEntity.name1)}'", ex.Message);
        Assert.Contains("'something.'", ex.Message);
    }

    [Fact]
    public static void Validate_throws_when_property_element_name_contains_dot()
    {
        using var db = SingleEntityDbContext.Create<DoubleNamedEntity>(mb =>
        {
            var dneBuilder = mb.Entity<DoubleNamedEntity>();
            dneBuilder.Property(p => p.name1).HasElementName("some.thing");
        });

        var ex = Assert.Throws<InvalidOperationException>(() => db.Model);
        Assert.Contains($"'{nameof(DoubleNamedEntity)}'", ex.Message);
        Assert.Contains($"'{nameof(DoubleNamedEntity.name1)}'", ex.Message);
        Assert.Contains("'some.thing'", ex.Message);
    }

    [Fact]
    public static void Validate_throws_when_multiple_navigations_configured_to_same_element_name()
    {
        using var db = SingleEntityDbContext.Create<WithTwoOwnedEntities>(mb =>
        {
            var dneBuilder = mb.Entity<WithTwoOwnedEntities>();
            dneBuilder.OwnsOne(p => p.First, r => r.HasElementName("location"));
            dneBuilder.OwnsOne(p => p.Second, r => r.HasElementName("location"));
        });

        var ex = Assert.Throws<InvalidOperationException>(() => db.Model);
        Assert.Contains($"'{nameof(WithTwoOwnedEntities)}'", ex.Message);
        Assert.Contains($"'{nameof(WithTwoOwnedEntities.First)}'", ex.Message);
        Assert.Contains($"'{nameof(WithTwoOwnedEntities.Second)}'", ex.Message);
        Assert.Contains("'location'", ex.Message);
    }

    [Fact]
    public static void Validate_throws_when_navigation_element_name_starts_with_dollar_sign()
    {
        using var db = SingleEntityDbContext.Create<WithTwoOwnedEntities>(mb =>
        {
            var dneBuilder = mb.Entity<WithTwoOwnedEntities>();
            dneBuilder.OwnsOne(p => p.First).HasElementName("$something");
        });

        var ex = Assert.Throws<InvalidOperationException>(() => db.Model);
        Assert.Contains($"'{nameof(WithTwoOwnedEntities)}'", ex.Message);
        Assert.Contains($"'{nameof(WithTwoOwnedEntities.First)}'", ex.Message);
        Assert.Contains("'$something'", ex.Message);
    }

    [Fact]
    public static void Validate_succeeds_if_navigation_element_name_ends_with_dollar_sign()
    {
        using var db = SingleEntityDbContext.Create<WithTwoOwnedEntities>(mb =>
        {
            var dneBuilder = mb.Entity<WithTwoOwnedEntities>();
            dneBuilder.OwnsOne(p => p.First).HasElementName("something$");
        });

        Assert.NotNull(db.Model);
    }

    [Fact]
    public static void Validate_succeeds_if_navigation_element_name_contains_dollar_sign_not_at_the_start()
    {
        using var db = SingleEntityDbContext.Create<WithTwoOwnedEntities>(mb =>
        {
            var dneBuilder = mb.Entity<WithTwoOwnedEntities>();
            dneBuilder.OwnsOne(p => p.First).HasElementName("some$thing");
        });

        Assert.NotNull(db.Model);
    }

    [Fact]
    public static void Validate_throws_when_navigation_element_name_starts_with_dot()
    {
        using var db = SingleEntityDbContext.Create<WithTwoOwnedEntities>(mb =>
        {
            var dneBuilder = mb.Entity<WithTwoOwnedEntities>();
            dneBuilder.OwnsOne(p => p.First).HasElementName(".why");
        });

        var ex = Assert.Throws<InvalidOperationException>(() => db.Model);
        Assert.Contains($"'{nameof(WithTwoOwnedEntities)}'", ex.Message);
        Assert.Contains($"'{nameof(WithTwoOwnedEntities.First)}'", ex.Message);
        Assert.Contains("'.why'", ex.Message);
    }

    [Fact]
    public static void Validate_throws_when_navigation_element_name_ends_with_dot()
    {
        using var db = SingleEntityDbContext.Create<WithTwoOwnedEntities>(mb =>
        {
            var dneBuilder = mb.Entity<WithTwoOwnedEntities>();
            dneBuilder.OwnsOne(p => p.First).HasElementName("notokay.");
        });

        var ex = Assert.Throws<InvalidOperationException>(() => db.Model);
        Assert.Contains($"'{nameof(WithTwoOwnedEntities)}'", ex.Message);
        Assert.Contains($"'{nameof(WithTwoOwnedEntities.First)}'", ex.Message);
        Assert.Contains("'notokay.'", ex.Message);
    }

    [Fact]
    public static void Validate_throws_when_navigation_element_name_contains_dot()
    {
        using var db = SingleEntityDbContext.Create<WithTwoOwnedEntities>(mb =>
        {
            var dneBuilder = mb.Entity<WithTwoOwnedEntities>();
            dneBuilder.OwnsOne(p => p.First).HasElementName("one.dot.is.too.many");
        });

        var ex = Assert.Throws<InvalidOperationException>(() => db.Model);
        Assert.Contains($"'{nameof(WithTwoOwnedEntities)}'", ex.Message);
        Assert.Contains($"'{nameof(WithTwoOwnedEntities.First)}'", ex.Message);
        Assert.Contains("'one.dot.is.too.many'", ex.Message);
    }

    [Fact]
    public static void Validate_throws_when_navigation_and_property_configured_to_same_element_name()
    {
        using var db = SingleEntityDbContext.Create<WithTwoOwnedEntities>(mb =>
        {
            var dneBuilder = mb.Entity<WithTwoOwnedEntities>();
            dneBuilder.OwnsOne(p => p.First, r => r.HasElementName("someTarget"));
            dneBuilder.Property(p => p.Different).HasElementName("someTarget");
        });

        var ex = Assert.Throws<InvalidOperationException>(() => db.Model);
        Assert.Contains($"'{nameof(WithTwoOwnedEntities)}'", ex.Message);
        Assert.Contains($"'{nameof(WithTwoOwnedEntities.First)}'", ex.Message);
        Assert.Contains($"'{nameof(WithTwoOwnedEntities.Different)}'", ex.Message);
        Assert.Contains("'someTarget'", ex.Message);
    }

    [Fact]
    public static void Validate_succeeds_when_primary_key_configured_correctly()
    {
        using var db = SingleEntityDbContext.Create<ConfiguredIdNamedEntity>(mb =>
        {
            var dneBuilder = mb.Entity<ConfiguredIdNamedEntity>();
            dneBuilder.HasKey(e => e.ThisWillBePrimaryKey);
            dneBuilder.Property(p => p.ThisWillBePrimaryKey).HasElementName("_id");
        });

        Assert.NotNull(db.Model);
    }

    [Fact]
    public static void Validate_throws_when_primary_key_conflicts_with_different_id_mapped_property()
    {
        using var db = SingleEntityDbContext.Create<ConfiguredIdNamedEntity>(mb =>
        {
            var dneBuilder = mb.Entity<ConfiguredIdNamedEntity>();
            dneBuilder.HasKey(e => e.ThisWillBePrimaryKey);
            dneBuilder.Property(p => p.SomethingElse).HasElementName("_id");
        });

        var ex = Assert.Throws<InvalidOperationException>(() => db.Model);
        Assert.Contains($"'{nameof(ConfiguredIdNamedEntity)}'", ex.Message);
        Assert.Contains($"'{nameof(ConfiguredIdNamedEntity.ThisWillBePrimaryKey)}'", ex.Message);
        Assert.Contains("'_id'", ex.Message);
    }

    [Fact]
    public static void Validate_throws_when_entity_has_shadow_properties()
    {
        using var db = SingleEntityDbContext.Create<DoubleNamedEntity>(mb =>
        {
            var dneBuilder = mb.Entity<DoubleNamedEntity>();
            dneBuilder.Property<DateTime>("ShadowDateTime");
        });

        var ex = Assert.Throws<NotSupportedException>(() => db.Model);
        Assert.Contains($"'{nameof(DoubleNamedEntity)}'", ex.Message);
        Assert.Contains("'ShadowDateTime'", ex.Message);
    }

    [Fact]
    public static void Validate_throws_when_entity_constructor_has_BsonConstructor_attribute()
    {
        using var context = SingleEntityDbContext.Create<EntityWithBsonConstructor>();

        var ex = Assert.Throws<NotSupportedException>(() => context.GetProperty((EntityWithBsonConstructor e) => e.name));
        Assert.Contains($"'{nameof(EntityWithBsonConstructor)}'", ex.Message);
        Assert.Contains($"'{nameof(BsonConstructorAttribute)}'", ex.Message);
    }

    class EntityWithBsonConstructor
    {
        public int _id { get; set; }

        public string name { get; set; }

        [BsonConstructor]
        public EntityWithBsonConstructor(string name)
        {
            this.name = name;
        }
    }

    [Fact]
    public static void Validate_throws_when_entity_class_has_BsonDiscriminator_attribute()
    {
        using var context = SingleEntityDbContext.Create<EntityWithBsonDiscriminator>();

        var ex = Assert.Throws<NotSupportedException>(() => context.GetProperty((EntityWithBsonDiscriminator e) => e.name));
        Assert.Contains($"'{nameof(EntityWithBsonDiscriminator)}'", ex.Message);
        Assert.Contains($"'{nameof(BsonDiscriminatorAttribute)}'", ex.Message);
    }

    [BsonDiscriminator]
    class EntityWithBsonDiscriminator
    {
        public int _id { get; set; }

        public string name { get; set; }
    }

    [Fact]
    public static void Validate_throws_when_entity_class_has_BsonKnownTypes_attribute()
    {
        using var context = SingleEntityDbContext.Create<EntityWithBsonKnownTypes>();

        var ex = Assert.Throws<NotSupportedException>(() => context.GetProperty((EntityWithBsonKnownTypes e) => e.name));
        Assert.Contains($"'{nameof(EntityWithBsonKnownTypes)}'", ex.Message);
        Assert.Contains($"'{nameof(BsonKnownTypesAttribute)}'", ex.Message);
    }

    [BsonKnownTypes]
    class EntityWithBsonKnownTypes
    {
        public int _id { get; set; }

        public string name { get; set; }
    }

    [Fact]
    public static void Validate_throws_when_entity_class_has_BsonMemberMapAttributeUsage_attribute()
    {
        using var context = SingleEntityDbContext.Create<EntityWithBsonMemberMapAttributeUsage>();

        var ex = Assert.Throws<NotSupportedException>(
            () => context.GetProperty((EntityWithBsonMemberMapAttributeUsage e) => e.name));
        Assert.Contains($"'{nameof(EntityWithBsonMemberMapAttributeUsage)}'", ex.Message);
        Assert.Contains($"'{nameof(BsonMemberMapAttributeUsageAttribute)}'", ex.Message);
    }

    [BsonMemberMapAttributeUsage]
    class EntityWithBsonMemberMapAttributeUsage
    {
        public int _id { get; set; }

        public string name { get; set; }
    }

    [Fact]
    public static void Validate_throws_when_entity_class_has_EntityWithBsonNoId_attribute()
    {
        using var context = SingleEntityDbContext.Create<EntityWithBsonNoId>();

        var ex = Assert.Throws<NotSupportedException>(() => context.GetProperty((EntityWithBsonNoId e) => e.name));
        Assert.Contains($"'{nameof(EntityWithBsonNoId)}'", ex.Message);
        Assert.Contains($"'{nameof(BsonNoIdAttribute)}'", ex.Message);
    }

    [BsonNoId]
    class EntityWithBsonNoId
    {
        public int _id { get; set; }

        public string name { get; set; }
    }

    [Fact]
    public static void Validate_throws_when_entity_class_has_EntityWithBsonSerializer_attribute()
    {
        using var context = SingleEntityDbContext.Create<EntityWithBsonSerializer>();

        var ex = Assert.Throws<NotSupportedException>(() => context.GetProperty((EntityWithBsonSerializer e) => e.name));
        Assert.Contains($"'{nameof(EntityWithBsonSerializer)}'", ex.Message);
        Assert.Contains($"'{nameof(BsonSerializerAttribute)}'", ex.Message);
    }

    [BsonSerializer]
    class EntityWithBsonSerializer
    {
        public int _id { get; set; }

        public string name { get; set; }
    }

    [Fact]
    public static void Validate_throws_when_entity_method_has_BsonFactoryMethod_attribute()
    {
        using var context = SingleEntityDbContext.Create<EntityWithBsonFactoryMethod>();

        var ex = Assert.Throws<NotSupportedException>(() => context.GetProperty((EntityWithBsonFactoryMethod e) => e.name));
        Assert.Contains($"'{nameof(EntityWithBsonFactoryMethod)}.{nameof(EntityWithBsonFactoryMethod.Create)}'", ex.Message);
        Assert.Contains($"'{nameof(BsonFactoryMethodAttribute)}'", ex.Message);
    }

    class EntityWithBsonFactoryMethod
    {
        public int _id { get; set; }

        public string name { get; set; }

        [BsonFactoryMethod]
        public static EntityWithBsonFactoryMethod Create() => new();
    }

    [Fact]
    public static void Validate_throws_when_entity_has_multiple_timestamp_attributes()
    {
        using var context = SingleEntityDbContext.Create<DoubleTimestampedEntity>();

        var ex = Assert.Throws<NotSupportedException>(() => context.GetProperty((DoubleTimestampedEntity e) => e.name));
        Assert.Contains($"{nameof(DoubleTimestampedEntity.Version1)}", ex.Message);
        Assert.Contains($"{nameof(DoubleTimestampedEntity.Version2)}", ex.Message);
    }

    class DoubleTimestampedEntity
    {
        public int _id { get; set; }

        public string name { get; set; }

        [Timestamp] public int Version1 { get; set; }
        [Timestamp] public int Version2 { get; set; }
    }

    [Fact]
    public static void Validate_throws_when_entity_has_multiple_IsRowVersion_configurations()
    {
        using var context = SingleEntityDbContext.Create<VersionableEntity>(mb =>
            mb.Entity<VersionableEntity>(e =>
                {
                    e.Property(v => v.VersionA).IsRowVersion();
                    e.Property(v => v.VersionB).IsRowVersion();
                }));

        var ex = Assert.Throws<NotSupportedException>(() => context.GetProperty((VersionableEntity e) => e.name));
        Assert.Contains($"{nameof(VersionableEntity.VersionA)}", ex.Message);
        Assert.Contains($"{nameof(VersionableEntity.VersionB)}", ex.Message);
    }

    class VersionableEntity
    {
        public int _id { get; set; }

        public string name { get; set; }

        public int VersionA { get; set; }
        public int VersionB { get; set; }
    }

    [Fact]
    public static void Validate_throws_when_entity_has_mutiple_timestamp_rowversions()
    {
        using var context = SingleEntityDbContext.Create<VersionableTimestampedEntity>(mb =>
            mb.Entity<VersionableTimestampedEntity>(e =>
            {
                e.Property(v => v.VersionB2).IsRowVersion();
            }));

        var ex = Assert.Throws<NotSupportedException>(() => context.GetProperty((VersionableTimestampedEntity e) => e.name));
        Assert.Contains($"{nameof(VersionableTimestampedEntity.VersionA1)}", ex.Message);
        Assert.Contains($"{nameof(VersionableTimestampedEntity.VersionB2)}", ex.Message);
    }

    class VersionableTimestampedEntity
    {
        public int _id { get; set; }

        public string name { get; set; }

        [Timestamp] public int VersionA1 { get; set; }
        public int VersionB2 { get; set; }
    }
}
