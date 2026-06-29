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
using Microsoft.EntityFrameworkCore.Metadata;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query.NativeTranslation;

/// <summary>
/// Unit tests for <see cref="StreamingEligibility.IsEligible"/>, a pure function that decides
/// whether an entity type can be materialized via the forward-only streaming reader.
/// </summary>
public class StreamingEligibilityTests
{
    // --- Simple flat entity with int PK ---

    private class FlatEntityIntId
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public int Age { get; set; }
    }

    [Fact]
    public void FlatEntity_WithIntId_IsEligible()
    {
        var entityType = GetEntityType<FlatEntityIntId>();
        Assert.True(StreamingEligibility.IsEligible(entityType));
    }

    // --- Simple flat entity with ObjectId PK ---

    private class FlatEntityObjectId
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
        public double Score { get; set; }
    }

    [Fact]
    public void FlatEntity_WithObjectIdId_IsEligible()
    {
        var entityType = GetEntityType<FlatEntityObjectId>();
        Assert.True(StreamingEligibility.IsEligible(entityType));
    }

    // --- Composite primary key (non-owned) makes entity ineligible ---

    private class CompositeKeyEntity
    {
        public int KeyA { get; set; }
        public int KeyB { get; set; }
        public string Name { get; set; } = "";
    }

    [Fact]
    public void Entity_WithCompositePrimaryKey_IsNotEligible()
    {
        var entityType = GetEntityType<CompositeKeyEntity>(mb =>
        {
            mb.Entity<CompositeKeyEntity>().HasKey(e => new { e.KeyA, e.KeyB });
        });
        Assert.False(StreamingEligibility.IsEligible(entityType));
    }

    // --- Owned collection navigation makes entity ineligible ---

    private class OwnedCollectionItem
    {
        public string Value { get; set; } = "";
    }

    private class EntityWithOwnedCollection
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public IList<OwnedCollectionItem> Items { get; set; } = [];
    }

    [Fact]
    public void Entity_WithOwnedFlatCollectionNavigation_IsEligible()
    {
        // A flat owned collection (element type has no sub-collections) is allowed by the spike's
        // streaming rules. The rewriter emits an array loop for it. Only "collection-of-collection"
        // nesting (element type itself owns a collection) is rejected.
        var entityType = GetEntityType<EntityWithOwnedCollection>(mb =>
        {
            mb.Entity<EntityWithOwnedCollection>().OwnsMany(e => e.Items);
        });
        Assert.True(StreamingEligibility.IsEligible(entityType));
    }

    // --- Owned reference navigation to an eligible owned type: entity is eligible ---

    private class OwnedAddress
    {
        public string Street { get; set; } = "";
        public string City { get; set; } = "";
    }

    private class EntityWithOwnedReference
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public OwnedAddress? Address { get; set; }
    }

    [Fact]
    public void Entity_WithSingleOwnedReferenceNavigation_IsEligible()
    {
        var entityType = GetEntityType<EntityWithOwnedReference>(mb =>
        {
            mb.Entity<EntityWithOwnedReference>().OwnsOne(e => e.Address);
        });
        Assert.True(StreamingEligibility.IsEligible(entityType));
    }

    // --- TPH hierarchy (has base type) makes entity ineligible ---

    private abstract class AnimalBase
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
    }

    private class Dog : AnimalBase
    {
        public string Breed { get; set; } = "";
    }

    [Fact]
    public void Entity_InTPHHierarchy_WithBaseType_IsNotEligible()
    {
        var entityType = GetEntityType<Dog>(mb =>
        {
            mb.Entity<AnimalBase>().HasDiscriminator<string>("AnimalType");
            mb.Entity<Dog>();
        });
        Assert.False(StreamingEligibility.IsEligible(entityType));
    }

    // --- No primary key makes entity ineligible ---

    private class NoPrimaryKey
    {
        public string Name { get; set; } = "";
    }

    [Fact]
    public void Entity_WithNoPrimaryKey_IsNotEligible()
    {
        var entityType = GetEntityType<NoPrimaryKey>(mb =>
        {
            mb.Entity<NoPrimaryKey>().HasNoKey();
        });
        Assert.False(StreamingEligibility.IsEligible(entityType));
    }

    // --- Owned collection whose element type also owns a collection: root is ineligible (collection-of-collection) ---

    private class InnerWithCollection
    {
        public string Value { get; set; } = "";
        public IList<OwnedCollectionItem> Nested { get; set; } = [];
    }

    private class EntityWithNestedOwnedCollection
    {
        public int Id { get; set; }
        public IList<InnerWithCollection> Inner { get; set; } = [];
    }

    [Fact]
    public void Entity_WithOwnedCollectionNavigation_WhoseElementHasOwnedCollection_IsNotEligible()
    {
        // A collection element type that itself owns a collection ("collection-of-collection") makes the
        // root entity ineligible. The spike rejects this case explicitly (see the navigation.IsCollection &&
        // target.GetNavigations().Any(n => n.IsCollection) check in StreamingEligibility).
        var entityType = GetEntityType<EntityWithNestedOwnedCollection>(mb =>
        {
            mb.Entity<EntityWithNestedOwnedCollection>().OwnsMany(e => e.Inner, inner =>
            {
                inner.OwnsMany(i => i.Nested);
            });
        });
        Assert.False(StreamingEligibility.IsEligible(entityType));
    }

    // --- Helper ---

    private static IEntityType GetEntityType<T>(Action<ModelBuilder>? configure = null) where T : class
    {
        using var db = SingleEntityDbContext.Create<T>(configure);
        return db.Model.FindEntityType(typeof(T))!;
    }
}
