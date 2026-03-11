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

public class EnumSerializationTests(TemporaryDatabaseFixture database)
    : BaseSerializationTests(database)
{
    [Theory]
    [InlineData(IntBasedEnum.None)]
    [InlineData(IntBasedEnum.First)]
    [InlineData(IntBasedEnum.Second)]
    public void Int_backed_enum_round_trips(IntBasedEnum value)
    {
        var collection = Database.CreateCollection<IntEnumEntity>(values: value);
        using var db = SingleEntityDbContext.Create(collection);
        db.Entities.Add(new IntEnumEntity { anEnum = value });
        db.SaveChanges();

        using var readDb = SingleEntityDbContext.Create(collection);
        var result = readDb.Entities.First();
        Assert.Equal(value, result.anEnum);
    }

    [Theory]
    [InlineData(LongBasedEnum.None)]
    [InlineData(LongBasedEnum.First)]
    [InlineData(LongBasedEnum.Large)]
    public void Long_backed_enum_round_trips(LongBasedEnum value)
    {
        var collection = Database.CreateCollection<LongEnumEntity>(values: value);
        using var db = SingleEntityDbContext.Create(collection);
        db.Entities.Add(new LongEnumEntity { anEnum = value });
        db.SaveChanges();

        using var readDb = SingleEntityDbContext.Create(collection);
        var result = readDb.Entities.First();
        Assert.Equal(value, result.anEnum);
    }

    [Fact]
    public void Nullable_enum_round_trips_null()
    {
        var collection = Database.CreateCollection<NullableEnumEntity>();
        using var db = SingleEntityDbContext.Create(collection);
        db.Entities.Add(new NullableEnumEntity { anEnum = null });
        db.SaveChanges();

        using var readDb = SingleEntityDbContext.Create(collection);
        var result = readDb.Entities.First();
        Assert.Null(result.anEnum);
    }

    [Fact]
    public void Nullable_enum_round_trips_value()
    {
        var collection = Database.CreateCollection<NullableEnumEntity>();
        using var db = SingleEntityDbContext.Create(collection);
        db.Entities.Add(new NullableEnumEntity { anEnum = IntBasedEnum.Second });
        db.SaveChanges();

        using var readDb = SingleEntityDbContext.Create(collection);
        var result = readDb.Entities.First();
        Assert.Equal(IntBasedEnum.Second, result.anEnum);
    }

    public enum IntBasedEnum { None = 0, First = 1, Second = 2 }
    public enum LongBasedEnum : long { None = 0, First = 1, Large = long.MaxValue }

    class IntEnumEntity : BaseIdEntity
    {
        public IntBasedEnum anEnum { get; set; }
    }

    class LongEnumEntity : BaseIdEntity
    {
        public LongBasedEnum anEnum { get; set; }
    }

    class NullableEnumEntity : BaseIdEntity
    {
        public IntBasedEnum? anEnum { get; set; }
    }
}
