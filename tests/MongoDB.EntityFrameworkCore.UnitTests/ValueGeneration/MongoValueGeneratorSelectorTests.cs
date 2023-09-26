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
using Microsoft.EntityFrameworkCore.ValueGeneration;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.ValueGeneration;

namespace MongoDB.EntityFrameworkCore.UnitTests.ValueGeneration;

public class MongoValueGeneratorSelectorTests
{
    private readonly IModel __model;
    private readonly IValueGeneratorSelector __valueGeneratorSelector;
    private readonly IEntityType __entityType;

    public MongoValueGeneratorSelectorTests()
    {
        var instance = MongoTestHelpers.Instance;
        var builder = instance.CreateConventionBuilder();
        var e = builder.Entity<RootEntity>();

        builder.FinalizeModel();
        __model = (IModel)builder.Model;
        var serviceProvider = instance.CreateServiceProvider();
        __valueGeneratorSelector = (IValueGeneratorSelector)serviceProvider.GetService(typeof(IValueGeneratorSelector))!;
        __entityType = __model.FindEntityType(typeof(EntityWithDifferentTypes))!;
    }

    [Fact]
    public void Create_returns_OwnedEntityIndexValueGenerator_for_mapped_int()
    {
        var generator = __valueGeneratorSelector.Select(__entityType.FindProperty("_unique")!, __entityType);
        Assert.IsType<OwnedEntityIndexValueGenerator>(generator);
    }

    [Fact]
    public void Create_throws_NotSupportedException_for_int()
    {
        Assert.Throws<NotSupportedException>(() =>
            __valueGeneratorSelector.Select(__entityType.FindProperty("someInt")!, __entityType));
    }

    [Fact]
    public void Create_throws_NotSupportedException_for_guid()
    {
        var generator = __valueGeneratorSelector.Select(__entityType.FindProperty("someGuid")!, __entityType);
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
