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
using MongoDB.Driver;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Metadata.Conventions.BsonAttributes;

[XUnitCollection("ConventionsTests")]
public class BsonIgnoreAttributeConventionTests(TemporaryDatabaseFixture tempDatabase)
    : IClassFixture<TemporaryDatabaseFixture>
{
    class IgnoredPropertiesEntity
    {
        public ObjectId _id { get; set; }

        public string? KeepMe { get; set; }

        [BsonIgnore]
        public string? IgnoreMe { get; set; }

        [BsonIgnore]
        public bool AndMe { get; set; }
    }

    class PropertiesEntity
    {
        public ObjectId _id { get; set; }

        public string? KeepMe { get; set; }

        public string? IgnoreMe { get; set; }

        public bool AndMe { get; set; }
    }

    [Fact]
    public void BsonElementAttribute_redefines_element_name_for_insert_and_query()
    {
        var collection = tempDatabase.CreateTemporaryCollection<IgnoredPropertiesEntity>();

        var id = ObjectId.GenerateNewId();
        const string name = "The quick brown fox";

        {
            var dbContext = SingleEntityDbContext.Create(collection);
            dbContext.Entitites.Add(new IgnoredPropertiesEntity
            {
                _id = id, KeepMe = name, IgnoreMe = "a", AndMe = true
            });
            dbContext.SaveChanges();
        }

        {
            var actual = collection.Database.GetCollection<PropertiesEntity>(collection.CollectionNamespace.CollectionName);
            var directFound = actual.Find(f => f._id == id).Single();
            Assert.Equal(name, directFound.KeepMe);
            Assert.Null(directFound.IgnoreMe);
            Assert.False(directFound.AndMe);
        }
    }
}
