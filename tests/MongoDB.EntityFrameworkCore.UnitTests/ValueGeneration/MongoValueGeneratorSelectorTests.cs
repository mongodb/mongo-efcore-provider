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

using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.ValueGeneration;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.ValueGeneration;

namespace MongoDB.EntityFrameworkCore.UnitTests.ValueGeneration;

public class MongoValueGeneratorSelectorTests
{
    private readonly IValueGeneratorSelector _valueGeneratorSelector;
    private readonly IEntityType _entityType;

    public MongoValueGeneratorSelectorTests()
    {
        var instance = MongoTestHelpers.Instance;
        var builder = instance.CreateConventionBuilder();
        builder.Entity<RootEntity>();

        builder.FinalizeModel();
        IModel model = (IModel)builder.Model;
        var serviceProvider = instance.CreateServiceProvider();
        _valueGeneratorSelector = (IValueGeneratorSelector)serviceProvider.GetService(typeof(IValueGeneratorSelector))!;
        _entityType = model.FindEntityType(typeof(EntityWithDifferentTypes))!;
    }

    [Fact]
    public void Create_returns_OwnedEntityIndexValueGenerator_for_mapped_int()
    {
        var generator = _valueGeneratorSelector.Select(_entityType.FindProperty("_unique")!, _entityType);
        Assert.IsType<OwnedEntityIndexValueGenerator>(generator);
    }

    [Fact]
    public void Create_returns_ObjectIdValueGenerator_for_mapped_ObjectId()
    {
        var generator = _valueGeneratorSelector.Select(_entityType.FindProperty("_id")!, _entityType);
        Assert.IsType<ObjectIdValueGenerator>(generator);
    }

    [Fact]
    public void Create_throws_NotSupportedException_for_mapped_int()
    {
        Assert.Throws<NotSupportedException>(() =>
            _valueGeneratorSelector.Select(_entityType.FindProperty("someInt")!, _entityType));
    }

    [Fact]
    public void Create_returns_GuidValueGenerator_for_guid()
    {
        var generator = _valueGeneratorSelector.Select(_entityType.FindProperty("someGuid")!, _entityType);
        Assert.IsType<GuidValueGenerator>(generator);
    }

    class RootEntity
    {
        public ObjectId _id { get; set; }
        public List<EntityWithDifferentTypes> OwnedEntityList { get; set; }
    }

    class EntityWithDifferentTypes
    {
        public ObjectId _id { get; set; }
        public int someInt { get; set; }
        public Guid someGuid { get; set; }
    }
}
