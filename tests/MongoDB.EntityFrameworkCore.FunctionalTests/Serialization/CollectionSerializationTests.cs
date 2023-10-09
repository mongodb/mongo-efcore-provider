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

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Serialization;

public class CollectionSerializationTests : BaseSerializationTests
{
    public CollectionSerializationTests(TemporaryDatabaseFixture tempDatabase)
        : base(tempDatabase)
    {
    }

    [Theory]
    [InlineData(2, 4, 8, 16, 32, 64)]
    [InlineData]
    public void Int_array_round_trips(params int[] expected)
    {
        var collection = TempDatabase.CreateTemporaryCollection<IntArrayEntity>(nameof(Int_array_round_trips) + expected.Length);

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entitites.Add(new IntArrayEntity {anIntArray = expected});
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var result = db.Entitites.FirstOrDefault();
            Assert.NotNull(result);
            Assert.Equal(expected, result.anIntArray);
        }
    }

    [Fact]
    public void Missing_int_array_defaults_to_null()
    {
        var collection = SetupIdOnlyCollection<IntArrayEntity>();
        using var db = SingleEntityDbContext.Create(collection);

        var result = db.Entitites.FirstOrDefault();
        Assert.NotNull(result);
        Assert.Null(result.anIntArray);
    }

    class IntArrayEntity : BaseIdEntity
    {
        public int[] anIntArray { get; set; }
    }

    [Theory]
    [InlineData("abc", "def", "ghi", "and the rest")]
    [InlineData]
    public void String_array_round_trips(params string[] expected)
    {
        var collection =
            TempDatabase.CreateTemporaryCollection<StringArrayEntity>(nameof(String_array_round_trips) + expected.Length);

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entitites.Add(new StringArrayEntity {aStringArray = expected});
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var result = db.Entitites.FirstOrDefault();
            Assert.NotNull(result);
            Assert.Equal(expected, result.aStringArray);
        }
    }

    [Fact]
    public void Missing_string_array_defaults_to_null()
    {
        var collection = SetupIdOnlyCollection<IntArrayEntity>();
        using var db = SingleEntityDbContext.Create(collection);

        var result = db.Entitites.FirstOrDefault();
        Assert.NotNull(result);
        Assert.Null(result.anIntArray);
    }

    class StringArrayEntity : BaseIdEntity
    {
        public string[] aStringArray { get; set; }
    }

    [Fact]
    public void Missing_list_defaults_to_null()
    {
        var collection = SetupIdOnlyCollection<ListEntity>();
        using var db = SingleEntityDbContext.Create(collection);

        var result = db.Entitites.FirstOrDefault();
        Assert.NotNull(result);
        Assert.Null(result.aList);
    }

    class ListEntity : BaseIdEntity
    {
        public List<decimal> aList { get; set; }
    }
}
