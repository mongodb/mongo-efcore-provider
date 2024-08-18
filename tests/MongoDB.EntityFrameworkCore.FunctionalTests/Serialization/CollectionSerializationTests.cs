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
    [InlineData]
    [InlineData(2, 4, 8, 16, 32, 64)]
    public void Int_array_round_trips(params int[] expected)
    {
        var collection = TempDatabase.CreateCollection<IntArrayEntity>(nameof(Int_array_round_trips) + expected.Length);

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entities.Add(new IntArrayEntity
            {
                anIntArray = expected
            });
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var result = db.Entities.FirstOrDefault();
            Assert.NotNull(result);
            Assert.Equal(expected, result.anIntArray);
        }
    }

    [Fact]
    public void Missing_int_array_throws()
    {
        var collection = SetupIdOnlyCollection<IntArrayEntity>();
        using var db = SingleEntityDbContext.Create(collection);

        Assert.Throws<InvalidOperationException>(() => db.Entities.FirstOrDefault());
    }

    class IntArrayEntity : BaseIdEntity
    {
        public int[] anIntArray { get; set; }
    }


    [Theory]
    [InlineData]
    [InlineData(2, 4, 8, 16, 32, 64)]
    public void Nullable_int_array_round_trips(params int[] expected)
    {
        var collection =
            TempDatabase.CreateCollection<NullableIntArrayEntity>(nameof(Nullable_int_array_round_trips)
                                                                           + expected.Length);

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entities.Add(new NullableIntArrayEntity
            {
                anIntArray = expected
            });
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var result = db.Entities.FirstOrDefault();
            Assert.NotNull(result);
            Assert.Equal(expected, result.anIntArray);
        }
    }

    [Fact]
    public void Missing_nullable_int_array_defaults_to_null()
    {
        var collection = SetupIdOnlyCollection<NullableIntArrayEntity>();
        using var db = SingleEntityDbContext.Create(collection);

        var result = db.Entities.FirstOrDefault();
        Assert.NotNull(result);
        Assert.Null(result.anIntArray);
    }

    class NullableIntArrayEntity : BaseIdEntity
    {
        public int[]? anIntArray { get; set; }
    }


    [Theory]
    [InlineData]
    [InlineData("abc", "def", "ghi", "and the rest")]
    public void String_array_round_trips(params string[] expected)
    {
        var collection =
            TempDatabase.CreateCollection<StringArrayEntity>(nameof(String_array_round_trips) + expected.Length);

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entities.Add(new StringArrayEntity
            {
                aStringArray = expected
            });
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var result = db.Entities.FirstOrDefault();
            Assert.NotNull(result);
            Assert.Equal(expected, result.aStringArray);
        }
    }

    [Fact]
    public void Missing_string_array_throws()
    {
        var collection = SetupIdOnlyCollection<StringArrayEntity>();
        using var db = SingleEntityDbContext.Create(collection);

        Assert.Throws<InvalidOperationException>(() => db.Entities.FirstOrDefault());
    }

    class StringArrayEntity : BaseIdEntity
    {
        public string[] aStringArray { get; set; }
    }


    public static readonly TheoryData<string[][]> ArrayOfArraysData =
    [
        [],
        [
            new [] { "a", "b", "c" },
            new [] { "d" }
        ]
    ];

    [Theory]
    [MemberData(nameof(ArrayOfArraysData))]
    public void String_array_of_arrays_round_trips(string[][] expected)
    {
        var collection =
            TempDatabase.CreateCollection<StringArrayOfArraysEntity>(nameof(String_array_of_arrays_round_trips)
                                                                              + expected.Length);

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entities.Add(new StringArrayOfArraysEntity
            {
                arrayOfStringArray = expected
            });
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var result = db.Entities.FirstOrDefault();
            Assert.NotNull(result);
            Assert.Equal(expected, result.arrayOfStringArray);
        }
    }

    class StringArrayOfArraysEntity : BaseIdEntity
    {
        public string[][] arrayOfStringArray { get; set; }
    }

    [Theory]
    [InlineData]
    [InlineData("abc", "def", "ghi", "and the rest")]
    public void Nullable_string_array_round_trips(params string[] expected)
    {
        var collection =
            TempDatabase.CreateCollection<NullableStringArrayEntity>(nameof(Nullable_string_array_round_trips)
                                                                              + expected.Length);

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entities.Add(new NullableStringArrayEntity
            {
                aStringArray = expected
            });
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var result = db.Entities.FirstOrDefault();
            Assert.NotNull(result);
            Assert.Equal(expected, result.aStringArray);
        }
    }

    [Fact]
    public void Missing_nullable_string_array_defaults_to_null()
    {
        var collection = SetupIdOnlyCollection<NullableStringArrayEntity>();
        using var db = SingleEntityDbContext.Create(collection);

        var result = db.Entities.FirstOrDefault();
        Assert.NotNull(result);
        Assert.Null(result.aStringArray);
    }

    class NullableStringArrayEntity : BaseIdEntity
    {
        public string[]? aStringArray { get; set; }
    }

    [Theory]
    [InlineData]
    [InlineData(1, 2, 3, 4)]
    public void List_round_trips(params int[] expected)
    {
        var collection =
            TempDatabase.CreateCollection<ListEntity>(nameof(List_round_trips) + expected.Length);

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entities.Add(new ListEntity
            {
                aList = [..expected]
            });
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var result = db.Entities.FirstOrDefault();
            Assert.NotNull(result);
            Assert.Equivalent(expected, result.aList);
        }
    }

    [Fact]
    public void Missing_list_throws()
    {
        var collection = SetupIdOnlyCollection<ListEntity>();
        using var db = SingleEntityDbContext.Create(collection);

        Assert.Throws<InvalidOperationException>(() => db.Entities.FirstOrDefault());
    }

    class ListEntity : BaseIdEntity
    {
        public List<int> aList { get; set; }
    }

    [Theory]
    [InlineData]
    [InlineData(1, 2, 3, 4)]
    public void Nullable_list_round_trips(params int[] expected)
    {
        var collection =
            TempDatabase.CreateCollection<NullableListEntity>(nameof(Nullable_list_round_trips) + expected.Length);

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entities.Add(new NullableListEntity
            {
                aList = new List<int>(expected)
            });
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var result = db.Entities.FirstOrDefault();
            Assert.NotNull(result);
            Assert.Equivalent(expected, result.aList);
        }
    }

    [Fact]
    public void Missing_nullable_list_default_to_null()
    {
        var collection = SetupIdOnlyCollection<NullableListEntity>();
        using var db = SingleEntityDbContext.Create(collection);

        var result = db.Entities.FirstOrDefault();
        Assert.NotNull(result);
        Assert.Null(result.aList);
    }

    class NullableListEntity : BaseIdEntity
    {
        public List<int>? aList { get; set; }
    }

    [Theory]
    [InlineData]
    [InlineData(1, 2, 3, 4)]
    public void IEnumerable_exposed_list_round_trips(params int[] expected)
    {
        var collection =
            TempDatabase.CreateCollection<IEnumerableEntity>(
                nameof(IEnumerable_exposed_list_round_trips) + expected.Length);

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entities.Add(new IEnumerableEntity
            {
                anEnumerable = new List<int>(expected)
            });
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var result = db.Entities.FirstOrDefault();
            Assert.NotNull(result);
            Assert.Equivalent(expected, result.anEnumerable);
        }
    }

    class IEnumerableEntity : BaseIdEntity
    {
        public IEnumerable<int>? anEnumerable { get; set; }
    }

    [Theory]
    [InlineData]
    [InlineData(1, 2, 3, 4)]
    public void Nullable_ienumerable_exposed_list_round_trips(params int[] expected)
    {
        var collection =
            TempDatabase.CreateCollection<NullableIEnumerableEntity>(nameof(Nullable_ienumerable_exposed_list_round_trips)
                                                                              + expected.Length);

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entities.Add(new NullableIEnumerableEntity
            {
                anEnumerable = new List<int>(expected)
            });
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var result = db.Entities.FirstOrDefault();
            Assert.NotNull(result);
            Assert.Equivalent(expected, result.anEnumerable);
        }
    }

    class NullableIEnumerableEntity : BaseIdEntity
    {
        public IEnumerable<int>? anEnumerable { get; set; }
    }

    [Fact]
    public void IEnumerable_exposed_ienumerable_throws()
    {
        var collection = TempDatabase.CreateCollection<IEnumerableEntity>();

        using var db = SingleEntityDbContext.Create(collection);

        var ex = Assert.Throws<InvalidOperationException>(() => db.Entities.Add(new IEnumerableEntity
        {
            anEnumerable = EnumerableOnlyWrapper.Wrap(new[]
            {
                1, 2, 3
            })
        }));

        Assert.Contains(nameof(EnumerableOnlyWrapper<int>), ex.Message);
    }
}
