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
using MongoDB.Bson;

namespace MongoDB.EntityFrameworkCore.SpecificationTests.Metadata.Conventions;

public class MongoValueGenerationConventionTests
{
    [Theory]
    [InlineData(typeof(int))]
    [InlineData(typeof(long))]
    [InlineData(typeof(uint))]
    [InlineData(typeof(ulong))]
    [InlineData(typeof(float))]
    [InlineData(typeof(double))]
    [InlineData(typeof(decimal))]
    [InlineData(typeof(string))]
    public void Primary_keys_without_mongodb_value_generators_wont_be_generated(Type clrKeyType)
    {
        var idProperty = GetBasicEntityIdProperty(clrKeyType);
        Assert.Equal(ValueGenerated.Never, idProperty.ValueGenerated);
    }

    [Theory]
    [InlineData(typeof(ObjectId))]
    [InlineData(typeof(Guid))]
    public void Primary_keys_with_mongodb_value_generators_will_be_generated_on_add(Type clrKeyType)
    {
        var idProperty = GetBasicEntityIdProperty(clrKeyType);
        Assert.Equal(ValueGenerated.OnAdd, idProperty.ValueGenerated);
    }

    private static IMutableProperty GetBasicEntityIdProperty(Type idPropertyType)
    {
        var entityClrType = typeof(BasicEntity<>).MakeGenericType(idPropertyType);
        var entityType = BuildEntityType(entityClrType);
        return entityType.GetProperty("_id");
    }

    private static IMutableEntityType BuildEntityType(Type clrType)
    {
        var builder = MongoTestHelpers.Instance.CreateConventionBuilder();
        builder.Entity(clrType);
        builder.FinalizeModel();
        var model = builder.Model;
        return model.FindEntityType(clrType)
            ?? throw new InvalidOperationException($"Could not find entity type '{clrType.FullName}'");
    }

    class BasicEntity<T>
    {
        public T _id { get; set; }
        public string name { get; set; }
    }

    class OwningEntity
    {
        public ObjectId _id { get; set; }
        public List<OwnedEntity> owned { get; set; }
    }

    class OwnedEntity
    {
        public string name { get; set; }

    }
}
