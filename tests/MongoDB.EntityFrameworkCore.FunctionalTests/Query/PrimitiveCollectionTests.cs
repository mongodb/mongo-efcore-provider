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

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

[XUnitCollection(nameof(ReadOnlySampleGuidesFixture))]
public class PrimitiveCollectionTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    class MissingPrimitiveList
    {
        public ObjectId _id { get; set; }
    }

    class NonNullablePrimitiveList
    {
        public ObjectId _id { get; set; }
        public List<int> items { get; set; }
    }

    class NullablePrimitiveList
    {
        public ObjectId _id { get; set; }
        public List<int>? items { get; set; }
    }

    class NonNullablePrimitiveArray
    {
        public ObjectId _id { get; set; }
        public int[] items { get; set; }
    }

    class NullablePrimitiveArray
    {
        public ObjectId _id { get; set; }
        public int[]? items { get; set; }
    }

    [Fact]
    public void Non_nullable_primitive_list_is_empty_when_bson_empty()
    {
        var collection = database.CreateCollection<NonNullablePrimitiveList>();
        collection.WriteTestDocs([
            new NonNullablePrimitiveList
            {
                items = []
            }
        ]);
        using var db = SingleEntityDbContext.Create(collection);

        var actual = db.Entities.First();
        Assert.Empty(actual.items);
    }

    [Fact]
    public void Non_nullable_primitive_list_throws_when_bson_missing()
    {
        var collection = database.CreateCollection<MissingPrimitiveList>();
        collection.WriteTestDocs([new MissingPrimitiveList()]);
        using var db = SingleEntityDbContext.Create<MissingPrimitiveList, NonNullablePrimitiveArray>(collection);

        var ex = Assert.Throws<InvalidOperationException>(() => db.Entities.First());
        Assert.Contains("null", ex.Message);
        Assert.Contains("non-nullable", ex.Message);
        Assert.Contains(nameof(NonNullablePrimitiveList.items), ex.Message);
    }

    [Fact]
    public void Non_nullable_primitive_list_throws_when_bson_null()
    {
        var collection = database.CreateCollection<NonNullablePrimitiveList>();
        collection.WriteTestDocs([
            new NonNullablePrimitiveList
            {
                items = null!
            }
        ]);
        using var db = SingleEntityDbContext.Create(collection);

        var ex = Assert.Throws<InvalidOperationException>(() => db.Entities.First());
        Assert.Contains("null", ex.Message);
        Assert.Contains("non-nullable", ex.Message);
        Assert.Contains(nameof(NonNullablePrimitiveList.items), ex.Message);
    }

    [Fact]
    public void Nullable_primitive_list_is_null_when_bson_missing()
    {
        var collection = database.CreateCollection<MissingPrimitiveList>();
        collection.WriteTestDocs([new MissingPrimitiveList()]);
        var db = SingleEntityDbContext.Create<MissingPrimitiveList, NullablePrimitiveArray>(collection);

        var actual = db.Entities.First();
        Assert.Null(actual.items);
    }

    [Fact]
    public void Nullable_primitive_list_is_empty_when_bson_empty()
    {
        var collection = database.CreateCollection<NullablePrimitiveList>();
        collection.WriteTestDocs([
            new NullablePrimitiveList
            {
                items = []
            }
        ]);
        var db = SingleEntityDbContext.Create(collection);

        var actual = db.Entities.First();
        Assert.NotNull(actual.items);
        Assert.Empty(actual.items);
    }

    [Fact]
    public void Nullable_primitive_list_is_null_when_bson_null()
    {
        var collection = database.CreateCollection<NullablePrimitiveList>();
        collection.WriteTestDocs([
            new NullablePrimitiveList
            {
                items = null
            }
        ]);
        var db = SingleEntityDbContext.Create(collection);

        var actual = db.Entities.First();
        Assert.Null(actual.items);
    }
}
