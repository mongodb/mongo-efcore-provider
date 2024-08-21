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

public class StringSerializationTests(TemporaryDatabaseFixture database)
    : BaseSerializationTests(database)
{
    private static long CollectionCounter;

    [Theory]
    [InlineData("A sample string")]
    [InlineData("")]
    [InlineData("\nWith\nNewlines")]
    [InlineData("With unicode \ud83d\ude0d")]
    [InlineData(null)]
    public void String_round_trips(string? expected)
    {
        var counter = Interlocked.Increment(ref CollectionCounter);
        var collection = Database.CreateCollection<StringEntity>(values: counter);

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entities.Add(new StringEntity {aString = expected});
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var result = db.Entities.FirstOrDefault();
            Assert.NotNull(result);
            Assert.Equal(expected, result.aString);
        }
    }

    [Fact]
    public void Missing_string_defaults_to_null()
    {
        var collection = SetupIdOnlyCollection<StringEntity>();
        using var db = SingleEntityDbContext.Create(collection);

        var result = db.Entities.FirstOrDefault();
        Assert.NotNull(result);
        Assert.Null(result.aString);
    }

    class StringEntity : BaseIdEntity
    {
        public string? aString { get; set; }
    }

    [Theory]
    [InlineData(' ')]
    [InlineData('A')]
    [InlineData('<')]
    [InlineData('{')]
    [InlineData('\n')]
    [InlineData('\x0169')]
    public void Char_round_trips(char expected)
    {
        var collection = Database.CreateCollection<CharEntity>(values: expected);

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entities.Add(new CharEntity {aChar = expected});
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var result = db.Entities.FirstOrDefault();
            Assert.NotNull(result);
            Assert.Equal(expected, result.aChar);
        }
    }

    [Fact]
    public void Missing_char_throws()
    {
        var collection = SetupIdOnlyCollection<CharEntity>();
        using var db = SingleEntityDbContext.Create(collection);

        Assert.Throws<InvalidOperationException>(() => db.Entities.FirstOrDefault());
    }

    class CharEntity : BaseIdEntity
    {
        public char aChar { get; set; }
    }

    [Theory]
    [InlineData(' ')]
    [InlineData('A')]
    [InlineData('<')]
    [InlineData('{')]
    [InlineData('\n')]
    [InlineData('\x0169')]
    [InlineData(null)]
    public void Nullable_char_round_trips(char? expected)
    {
        var collection =
            Database.CreateCollection<NullableCharEntity>(values: expected?.ToString().Replace(' ', '_'));

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entities.Add(new NullableCharEntity {aNullableChar = expected});
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var result = db.Entities.FirstOrDefault();
            Assert.NotNull(result);
            Assert.Equal(expected, result.aNullableChar);
        }
    }

    [Fact]
    public void Missing_nullable_char_defaults_to_null()
    {
        var collection = SetupIdOnlyCollection<NullableCharEntity>();
        using var db = SingleEntityDbContext.Create(collection);

        var result = db.Entities.FirstOrDefault();
        Assert.NotNull(result);
        Assert.Null(result.aNullableChar);
    }

    class NullableCharEntity : BaseIdEntity
    {
        public char? aNullableChar { get; set; }
    }
}
