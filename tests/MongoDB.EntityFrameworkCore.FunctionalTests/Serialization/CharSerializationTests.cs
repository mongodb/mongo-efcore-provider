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

public class CharSerializationTests(TemporaryDatabaseFixture database)
    : BaseSerializationTests(database)
{
    [Theory]
    [InlineData('A')]
    [InlineData('Z')]
    [InlineData(' ')]
    public void Char_round_trips(char value)
    {
        var collection = Database.CreateCollection<CharEntity>(values: value);
        using var db = SingleEntityDbContext.Create(collection);
        db.Entities.Add(new CharEntity { aChar = value });
        db.SaveChanges();

        using var readDb = SingleEntityDbContext.Create(collection);
        var result = readDb.Entities.First();
        Assert.Equal(value, result.aChar);
    }

    [Fact]
    public void Nullable_char_round_trips_null()
    {
        var collection = Database.CreateCollection<NullableCharEntity>();
        using var db = SingleEntityDbContext.Create(collection);
        db.Entities.Add(new NullableCharEntity { aChar = null });
        db.SaveChanges();

        using var readDb = SingleEntityDbContext.Create(collection);
        var result = readDb.Entities.First();
        Assert.Null(result.aChar);
    }

    [Fact]
    public void Nullable_char_round_trips_value()
    {
        var collection = Database.CreateCollection<NullableCharEntity>();
        using var db = SingleEntityDbContext.Create(collection);
        db.Entities.Add(new NullableCharEntity { aChar = 'X' });
        db.SaveChanges();

        using var readDb = SingleEntityDbContext.Create(collection);
        var result = readDb.Entities.First();
        Assert.Equal('X', result.aChar);
    }

    class CharEntity : BaseIdEntity
    {
        public char aChar { get; set; }
    }

    class NullableCharEntity : BaseIdEntity
    {
        public char? aChar { get; set; }
    }
}
