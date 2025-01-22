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

using System.Globalization;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using MongoDB.Bson;
using MongoDB.Driver;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Mapping;

[XUnitCollection("MappingTests")]
public class ValueConverterTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    class IdIsObjectId
    {
        public ObjectId _id { get; set; }
    }

    class IdIsString
    {
        public string _id { get; set; }
    }

    private static readonly Action<ModelBuilder>? ObjectIdToString = mb =>
        mb.Entity<IdIsObjectId>().Property(e => e._id).HasConversion(v => v.ToString(), v => ObjectId.Parse(v));

    private static readonly Action<ModelBuilder>? DefaultObjectIdToString = mb =>
        mb.Entity<IdIsObjectId>().Property(e => e._id).HasConversion<string>();

    [Theory]
    [InlineData("507f1f77bcf86cd799439011", true)]
    [InlineData("507f191e810c19729de860ea", true)]
    [InlineData("507f1f77bcf86cd799439011", false)]
    [InlineData("507f191e810c19729de860ea", false)]
    public void ObjectId_can_deserialize_and_query_from_string(string id, bool defaultConverter)
    {
        var expectedId = ObjectId.Parse(id);
        var expected = new IdIsString {_id = expectedId.ToString()};
        var docs = database.CreateCollection<IdIsString>(values: [id, defaultConverter]);
        docs.InsertOne(expected);

        var collection = database.GetCollection<IdIsObjectId>(docs.CollectionNamespace);
        using var db = SingleEntityDbContext.Create(collection, defaultConverter ? DefaultObjectIdToString : ObjectIdToString);

        var found = db.Entities.First(e => e._id == expectedId);
        Assert.Equal(expectedId, found._id);
    }

    [Theory]
    [InlineData("507f1f77bcf86cd799439011", true)]
    [InlineData("507f191e810c19729de860ea", true)]
    [InlineData("507f1f77bcf86cd799439011", false)]
    [InlineData("507f191e810c19729de860ea", false)]
    public void ObjectId_can_serialize_to_string(string id, bool defaultConverter)
    {
        var collection = database.CreateCollection<IdIsObjectId>(values: [id, defaultConverter]);
        using var db = SingleEntityDbContext.Create(collection, defaultConverter ? DefaultObjectIdToString : ObjectIdToString);

        var original = new IdIsObjectId {_id = ObjectId.Parse(id)};
        db.Entities.Add(original);
        db.SaveChanges();

        var found = database.GetCollection<IdIsString>(collection.CollectionNamespace).AsQueryable().First();
        Assert.Equal(original._id.ToString(), found._id);
    }

    private static readonly Action<ModelBuilder>? StringToObjectId = mb =>
        mb.Entity<IdIsString>().Property(e => e._id).HasConversion(v => ObjectId.Parse(v), v => v.ToString());

    private static readonly Action<ModelBuilder>? DefaultStringToObjectId = mb =>
        mb.Entity<IdIsString>().Property(e => e._id).HasConversion<ObjectId>();

    [Theory]
    [InlineData("507f1f77bcf86cd799439011", true)]
    [InlineData("507f191e810c19729de860ea", true)]
    [InlineData("507f1f77bcf86cd799439011", false)]
    [InlineData("507f191e810c19729de860ea", false)]
    public void String_can_deserialize_and_query_from_ObjectId(string id, bool defaultConverter)
    {
        var expected = new IdIsObjectId {_id = ObjectId.Parse(id)};
        var docs = database.CreateCollection<IdIsObjectId>(values: [id, defaultConverter]);
        docs.InsertOne(expected);

        var collection = database.GetCollection<IdIsString>(docs.CollectionNamespace);
        using var db = SingleEntityDbContext.Create(collection, defaultConverter ? DefaultStringToObjectId : StringToObjectId);

        var found = db.Entities.First(e => e._id == expected._id.ToString());
        Assert.Equal(expected._id.ToString(), found._id);
    }

    [Theory]
    [InlineData("507f1f77bcf86cd799439011", true)]
    [InlineData("507f191e810c19729de860ea", true)]
    [InlineData("507f1f77bcf86cd799439011", false)]
    [InlineData("507f191e810c19729de860ea", false)]
    public void String_can_serialize_to_ObjectId(string id, bool defaultConverter)
    {
        var collection = database.CreateCollection<IdIsString>(values: [id, defaultConverter]);
        using var db = SingleEntityDbContext.Create(collection,
            defaultConverter ? DefaultStringToObjectId : StringToObjectId);

        var original = new IdIsString {_id = id};
        db.Entities.Add(original);
        db.SaveChanges();

        var found = database.GetCollection<IdIsObjectId>(collection.CollectionNamespace).AsQueryable().First();
        Assert.Equal(original._id, found._id.ToString());
    }

    class ActiveIsBool : IdIsObjectId
    {
        public bool active { get; set; }
    }

    class ActiveIsString : IdIsObjectId
    {
        public string active { get; set; }
    }

    private static readonly Action<ModelBuilder>? BoolToString = mb =>
        mb.Entity<ActiveIsBool>().Property(e => e.active).HasConversion(v => v.ToString(), v => bool.Parse(v));

    private static readonly Action<ModelBuilder>? DefaultBoolToString = mb =>
        mb.Entity<ActiveIsBool>().Property(e => e.active).HasConversion<string>();

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void Bool_can_deserialize_and_query_from_string(bool active, bool defaultConverter)
    {
        // EF default uses "0" and "1" not "false" and "true"
        var expectedActive = defaultConverter ? active ? "1" : "0" : active.ToString();
        var expected = new ActiveIsString {active = expectedActive, _id = ObjectId.GenerateNewId()};
        var docs = database.CreateCollection<ActiveIsString>(values: [active, defaultConverter]);
        docs.InsertOne(expected);

        var collection = database.GetCollection<ActiveIsBool>(docs.CollectionNamespace);
        using var db = SingleEntityDbContext.Create(collection, defaultConverter ? DefaultBoolToString : BoolToString);

        var found = db.Entities.First(e => e.active == active);
        Assert.Equal(expected._id, found._id);
        Assert.Equal(active, found.active);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void Bool_can_serialize_to_string(bool active, bool defaultConverter)
    {
        var collection = database.CreateCollection<ActiveIsBool>(values: [active, defaultConverter]);
        using var db = SingleEntityDbContext.Create(collection, defaultConverter ? DefaultBoolToString : BoolToString);

        var original = new ActiveIsBool {active = active};
        db.Entities.Add(original);
        db.SaveChanges();

        var found = database.GetCollection<ActiveIsString>(collection.CollectionNamespace).AsQueryable().First();
        Assert.Equal(original._id, found._id);

        // EF default uses "0" and "1" not "false" and "true"
        var expectedActive = defaultConverter ? active ? "1" : "0" : active.ToString();
        Assert.Equal(expectedActive, found.active);
    }

    class DaysIsInt : IdIsObjectId
    {
        public int days { get; set; }
    }

    class DaysIsString : IdIsObjectId
    {
        public string days { get; set; }
    }

    private static readonly Action<ModelBuilder>? IntToString = mb =>
        mb.Entity<DaysIsInt>().Property(e => e.days).HasConversion(v => v.ToString(), v => int.Parse(v));

    private static readonly Action<ModelBuilder>? DefaultIntToString = mb =>
        mb.Entity<DaysIsInt>().Property(e => e.days).HasConversion<string>();

    [Theory]
    [InlineData(1, true)]
    [InlineData(1, false)]
    [InlineData(-123, true)]
    [InlineData(-123, false)]
    [InlineData(0, true)]
    [InlineData(0, false)]
    public void Int_can_deserialize_and_query_from_string(int days, bool defaultConverter)
    {
        var expected = new DaysIsString {days = days.ToString(), _id = ObjectId.GenerateNewId()};
        var docs = database.CreateCollection<DaysIsString>(values: [days, defaultConverter]);
        docs.InsertOne(expected);

        var collection = database.GetCollection<DaysIsInt>(docs.CollectionNamespace);
        using var db = SingleEntityDbContext.Create(collection, defaultConverter ? DefaultIntToString : IntToString);

        var found = db.Entities.First(e => e.days == days);
        Assert.Equal(expected._id, found._id);
        Assert.Equal(days, found.days);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(1, false)]
    [InlineData(-123, true)]
    [InlineData(-123, false)]
    [InlineData(0, true)]
    [InlineData(0, false)]
    public void Int_can_serialize_to_string(int days, bool defaultConverter)
    {
        var collection = database.CreateCollection<DaysIsInt>(values: [days, defaultConverter]);
        using var db = SingleEntityDbContext.Create(collection, defaultConverter ? DefaultIntToString : IntToString);

        var original = new DaysIsInt {days = days};
        db.Entities.Add(original);
        db.SaveChanges();

        var found = database.GetCollection<DaysIsString>(collection.CollectionNamespace).AsQueryable().First();
        Assert.Equal(original._id, found._id);
        Assert.Equal(days.ToString(), found.days);
    }

    class DayIsEnum : IdIsObjectId
    {
        public DayOfWeek day { get; set; }
    }

    class DayIsString : IdIsObjectId
    {
        public string day { get; set; }
    }

    private static readonly Action<ModelBuilder>? EnumToString = mb =>
        mb.Entity<DayIsEnum>().Property(e => e.day).HasConversion(e => e.ToString(), s => Enum.Parse<DayOfWeek>(s));

    private static readonly Action<ModelBuilder>? DefaultEnumToString = mb =>
        mb.Entity<DayIsEnum>().Property(e => e.day).HasConversion<string>();

    [Theory]
    [InlineData(2, true)]
    [InlineData(2, false)]
    [InlineData(-1234, true)]
    [InlineData(-1234, false)]
    [InlineData(0, true)]
    [InlineData(0, false)]
    [InlineData(null, true)]
    [InlineData(null, false)]
    public void Nullable_int_can_deserialize_and_query_from_string(int? days, bool defaultConverter)
    {
        var expected = new DaysIsString {days = days?.ToString()!, _id = ObjectId.GenerateNewId()};
        var docs = database.CreateCollection<DaysIsString>(values: [days, defaultConverter]);
        docs.InsertOne(expected);

        var collection = database.GetCollection<DaysIsNullableInt>(docs.CollectionNamespace);
        using var db =
            SingleEntityDbContext.Create(collection, defaultConverter ? DefaultNullableIntToString : NullableIntToString);

        var found = db.Entities.First(e => e.days == days);
        Assert.Equal(expected._id, found._id);
        Assert.Equal(days, found.days);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(1, false)]
    [InlineData(-123, true)]
    [InlineData(-123, false)]
    [InlineData(0, true)]
    [InlineData(0, false)]
    [InlineData(null, true)]
    [InlineData(null, false)]
    public void Nullable_int_can_serialize_to_string(int? days, bool defaultConverter)
    {
        var collection = database.CreateCollection<DaysIsNullableInt>(values: [days, defaultConverter]);
        using var db =
            SingleEntityDbContext.Create(collection, defaultConverter ? DefaultNullableIntToString : NullableIntToString);

        var original = new DaysIsNullableInt {days = days};
        db.Entities.Add(original);
        db.SaveChanges();

        var found = database.GetCollection<DaysIsString>(collection.CollectionNamespace).AsQueryable().First();
        Assert.Equal(original._id, found._id);
        Assert.Equal(days?.ToString(), found.days);
    }

    class DaysIsNullableInt : IdIsObjectId
    {
        public int? days { get; set; }
    }

    private static readonly Action<ModelBuilder>? NullableIntToString = mb =>
        mb.Entity<DaysIsNullableInt>().Property(e => e.days)
            .HasConversion(new ValueConverter<int, string>(v => v.ToString(), v => int.Parse(v)));

    private static readonly Action<ModelBuilder>? DefaultNullableIntToString = mb =>
        mb.Entity<DaysIsNullableInt>().Property(e => e.days).HasConversion<string>();

    [Theory]
    [InlineData(DayOfWeek.Wednesday, true)]
    [InlineData(DayOfWeek.Wednesday, false)]
    [InlineData(DayOfWeek.Saturday, true)]
    [InlineData(DayOfWeek.Saturday, false)]
    [InlineData(DayOfWeek.Sunday, true)]
    [InlineData(DayOfWeek.Sunday, false)]
    public void Enum_can_deserialize_and_query_from_string(DayOfWeek day, bool defaultConverter)
    {
        var expected = new DayIsString {day = day.ToString(), _id = ObjectId.GenerateNewId()};
        var docs = database.CreateCollection<DayIsString>(values: [day, defaultConverter]);
        docs.InsertOne(expected);

        var collection = database.GetCollection<DayIsEnum>(docs.CollectionNamespace);
        using var db = SingleEntityDbContext.Create(collection, defaultConverter ? DefaultEnumToString : EnumToString);

        var found = db.Entities.First(e => e.day == day);
        Assert.Equal(expected._id, found._id);
        Assert.Equal(day, found.day);
    }

    [Theory]
    [InlineData(DayOfWeek.Wednesday, true)]
    [InlineData(DayOfWeek.Wednesday, false)]
    [InlineData(DayOfWeek.Saturday, true)]
    [InlineData(DayOfWeek.Saturday, false)]
    [InlineData(DayOfWeek.Sunday, true)]
    [InlineData(DayOfWeek.Sunday, false)]
    public void Enum_can_serialize_to_string(DayOfWeek day, bool defaultConverter)
    {
        var collection = database.CreateCollection<DayIsString>(values: [day, defaultConverter]);
        using var db = SingleEntityDbContext.Create(collection, defaultConverter ? DefaultEnumToString : EnumToString);

        var original = new DayIsString {day = day.ToString()};
        db.Entities.Add(original);
        db.SaveChanges();

        var found = database.GetCollection<DayIsString>(collection.CollectionNamespace).AsQueryable().First();
        Assert.Equal(original._id, found._id);
        Assert.Equal(day.ToString(), found.day);
    }

    private static readonly Action<ModelBuilder>? UnsupportedNullableConverter = mb =>
        mb.Entity<DaysIsNullableInt>().Property(e => e.days)
            .HasConversion(v => v == null ? null : v.ToString(), v => v == null ? null : int.Parse(v));

    [Theory]
    [InlineData(DayOfWeek.Wednesday, true)]
    [InlineData(DayOfWeek.Wednesday, false)]
    [InlineData(DayOfWeek.Saturday, true)]
    [InlineData(DayOfWeek.Saturday, false)]
    [InlineData(DayOfWeek.Sunday, true)]
    [InlineData(DayOfWeek.Sunday, false)]
    [InlineData(null, true)]
    [InlineData(null, false)]
    public void Nullable_enum_can_deserialize_and_query_from_string(DayOfWeek? day, bool defaultConverter)
    {
        var expected = new DayIsString {day = day == null ? null! : day.ToString()!, _id = ObjectId.GenerateNewId()};
        var docs = database.CreateCollection<DayIsString>(values: [day, defaultConverter]);
        docs.InsertOne(expected);

        var collection = database.GetCollection<DayIsNullableEnum>(docs.CollectionNamespace);
        using var db =
            SingleEntityDbContext.Create(collection, defaultConverter ? DefaultNullableEnumToString : NullableEnumToString);

        var found = db.Entities.First(e => e.day == day);
        Assert.Equal(expected._id, found._id);
        Assert.Equal(day, found.day);
    }

    [Theory]
    [InlineData(DayOfWeek.Wednesday, true)]
    [InlineData(DayOfWeek.Wednesday, false)]
    [InlineData(DayOfWeek.Saturday, true)]
    [InlineData(DayOfWeek.Saturday, false)]
    [InlineData(DayOfWeek.Sunday, true)]
    [InlineData(DayOfWeek.Sunday, false)]
    [InlineData(null, true)]
    [InlineData(null, false)]
    public void Nullable_enum_can_serialize_to_string(DayOfWeek? day, bool defaultConverter)
    {
        var collection = database.CreateCollection<DayIsNullableEnum>(values: [day, defaultConverter]);
        using var db =
            SingleEntityDbContext.Create(collection, defaultConverter ? DefaultNullableEnumToString : NullableEnumToString);

        var original = new DayIsNullableEnum {day = day};
        db.Entities.Add(original);
        db.SaveChanges();

        var found = database.GetCollection<DayIsString>(collection.CollectionNamespace).AsQueryable().First();
        Assert.Equal(original._id, found._id);
        Assert.Equal(day?.ToString(), found.day);
    }

    class DayIsNullableInt : IdIsObjectId
    {
        public int? day { get; set; }
    }

    private static readonly Action<ModelBuilder>? NullableEnumToNullableInt = mb =>
        mb.Entity<DayIsNullableEnum>().Property(e => e.day)
            .HasConversion(new ValueConverter<DayOfWeek, int>(e => (int)e, i => (DayOfWeek)i));

    private static readonly Action<ModelBuilder>? DefaultNullableEnumToNullableInt = mb =>
        mb.Entity<DayIsNullableEnum>().Property(e => e.day).HasConversion<int>();

    [Theory]
    [InlineData(DayOfWeek.Wednesday)]
    [InlineData(DayOfWeek.Saturday)]
    [InlineData(DayOfWeek.Sunday)]
    public void Enum_can_deserialize_and_query_from_string_global(DayOfWeek day)
    {
        var expected = new DayIsString {day = day.ToString(), _id = ObjectId.GenerateNewId()};
        var docs = database.CreateCollection<DayIsString>(values: [day]);
        docs.InsertOne(expected);

        var collection = database.GetCollection<DayIsEnum>(docs.CollectionNamespace);
        using var db = SingleEntityDbContext.Create(collection, null, ConfigDefaultEnumToString);

        var found = db.Entities.First(e => e.day == day);
        Assert.Equal(expected._id, found._id);
        Assert.Equal(day, found.day);
    }

    [Theory]
    [InlineData(DayOfWeek.Wednesday)]
    [InlineData(DayOfWeek.Saturday)]
    [InlineData(DayOfWeek.Sunday)]
    public void Enum_can_serialize_to_string_global(DayOfWeek day)
    {
        var collection = database.CreateCollection<DayIsNullableEnum>(values: [day]);
        using var db = SingleEntityDbContext.Create(collection, null, ConfigDefaultEnumToString);

        var original = new DayIsNullableEnum {day = day};
        db.Entities.Add(original);
        db.SaveChanges();

        var found = database.GetCollection<DayIsString>(collection.CollectionNamespace).AsQueryable().First();
        Assert.Equal(original._id, found._id);
        Assert.Equal(day.ToString(), found.day);
    }

    private static readonly Action<ModelConfigurationBuilder>? ConfigDefaultEnumToString = cb =>
        cb.Properties<DayOfWeek>().HaveConversion<string>();

    [Theory]
    [InlineData(DayOfWeek.Wednesday)]
    [InlineData(DayOfWeek.Saturday)]
    [InlineData(DayOfWeek.Sunday)]
    [InlineData(null)]
    public void Nullable_enum_can_deserialize_and_query_from_string_global(DayOfWeek? day)
    {
        var expected = new DayIsString {day = day == null ? null! : day.ToString()!, _id = ObjectId.GenerateNewId()};
        var docs = database.CreateCollection<DayIsString>(values: [day]);
        docs.InsertOne(expected);

        var collection = database.GetCollection<DayIsNullableEnum>(docs.CollectionNamespace);
        using var db = SingleEntityDbContext.Create(collection, null, ConfigDefaultNullableEnumToString);

        var found = db.Entities.First(e => e.day == day);
        Assert.Equal(expected._id, found._id);
        Assert.Equal(day, found.day);
    }

    [Theory]
    [InlineData(DayOfWeek.Wednesday)]
    [InlineData(DayOfWeek.Saturday)]
    [InlineData(DayOfWeek.Sunday)]
    [InlineData(null)]
    public void Nullable_enum_can_serialize_to_string_global(DayOfWeek? day)
    {
        var collection = database.CreateCollection<DayIsNullableEnum>(values: [day]);
        using var db = SingleEntityDbContext.Create(collection, null, ConfigDefaultNullableEnumToString);

        var original = new DayIsNullableEnum {day = day};
        db.Entities.Add(original);
        db.SaveChanges();

        var found = database.GetCollection<DayIsString>(collection.CollectionNamespace).AsQueryable().First();
        Assert.Equal(original._id, found._id);
        Assert.Equal(day?.ToString(), found.day);
    }

    private static readonly Action<ModelConfigurationBuilder>? ConfigDefaultNullableEnumToString = cb =>
        cb.Properties<DayOfWeek>().HaveConversion<string>();

    [Theory]
    [InlineData(DayOfWeek.Wednesday, true)]
    [InlineData(DayOfWeek.Wednesday, false)]
    [InlineData(DayOfWeek.Saturday, true)]
    [InlineData(DayOfWeek.Saturday, false)]
    [InlineData(DayOfWeek.Sunday, true)]
    [InlineData(DayOfWeek.Sunday, false)]
    [InlineData(null, true)]
    [InlineData(null, false)]
    public void Nullable_enum_can_deserialize_and_query_from_nullable_int(DayOfWeek? day, bool defaultConverter)
    {
        var expected = new DayIsNullableInt {day = day == null ? null : (int)day, _id = ObjectId.GenerateNewId()};
        var docs = database.CreateCollection<DayIsNullableInt>(values: [day, defaultConverter]);
        docs.InsertOne(expected);

        var collection = database.GetCollection<DayIsNullableEnum>(docs.CollectionNamespace);
        using var db = SingleEntityDbContext.Create(collection,
            defaultConverter ? DefaultNullableEnumToNullableInt : NullableEnumToNullableInt);

        var found = db.Entities.First(e => e.day == day);
        Assert.Equal(expected._id, found._id);
        Assert.Equal(day, found.day);
    }

    [Theory]
    [InlineData(DayOfWeek.Wednesday, true)]
    [InlineData(DayOfWeek.Wednesday, false)]
    [InlineData(DayOfWeek.Saturday, true)]
    [InlineData(DayOfWeek.Saturday, false)]
    [InlineData(DayOfWeek.Sunday, true)]
    [InlineData(DayOfWeek.Sunday, false)]
    [InlineData(null, true)]
    [InlineData(null, false)]
    public void Nullable_enum_can_serialize_to_nullable_int(DayOfWeek? day, bool defaultConverter)
    {
        var collection = database.CreateCollection<DayIsNullableEnum>(values: [day, defaultConverter]);
        using var db = SingleEntityDbContext.Create(collection,
            defaultConverter ? DefaultNullableEnumToNullableInt : NullableEnumToNullableInt);

        var original = new DayIsNullableEnum {day = day};
        db.Entities.Add(original);
        db.SaveChanges();

        var found = database.GetCollection<DayIsNullableInt>(collection.CollectionNamespace)
            .AsQueryable().First();
        Assert.Equal(original._id, found._id);
        Assert.Equal(day, (DayOfWeek?)found.day);
    }

    class DayIsInt : IdIsObjectId
    {
        public int day { get; set; }
    }

    private static readonly Action<ModelBuilder>? EnumToInt = mb =>
        mb.Entity<DayIsEnum>().Property(e => e.day).HasConversion(e => (int)e, i => (DayOfWeek)i);

    private static readonly Action<ModelBuilder>? DefaultEnumToInt = mb =>
        mb.Entity<DayIsEnum>().Property(e => e.day).HasConversion<int>();

    [Theory]
    [InlineData(DayOfWeek.Wednesday, true)]
    [InlineData(DayOfWeek.Wednesday, false)]
    [InlineData(DayOfWeek.Saturday, true)]
    [InlineData(DayOfWeek.Saturday, false)]
    [InlineData(DayOfWeek.Sunday, true)]
    [InlineData(DayOfWeek.Sunday, false)]
    public void Enum_can_deserialize_and_query_from_int(DayOfWeek day, bool defaultConverter)
    {
        var expected = new DayIsInt {day = (int)day, _id = ObjectId.GenerateNewId()};
        var docs = database.CreateCollection<DayIsInt>(values: [day, defaultConverter]);
        docs.InsertOne(expected);

        var collection = database.GetCollection<DayIsEnum>(docs.CollectionNamespace);
        using var db = SingleEntityDbContext.Create(collection, defaultConverter ? DefaultEnumToInt : EnumToInt);

        var found = db.Entities.First(e => e.day == day);
        Assert.Equal(expected._id, found._id);
        Assert.Equal(day, found.day);
    }

    [Theory]
    [InlineData(DayOfWeek.Wednesday, true)]
    [InlineData(DayOfWeek.Wednesday, false)]
    [InlineData(DayOfWeek.Saturday, true)]
    [InlineData(DayOfWeek.Saturday, false)]
    [InlineData(DayOfWeek.Sunday, true)]
    [InlineData(DayOfWeek.Sunday, false)]
    public void Enum_can_serialize_to_int(DayOfWeek day, bool defaultConverter)
    {
        var collection = database.CreateCollection<DayIsInt>(values: [day, defaultConverter]);
        using var db = SingleEntityDbContext.Create(collection, defaultConverter ? DefaultEnumToInt : EnumToInt);

        var original = new DayIsInt {day = (int)day};
        db.Entities.Add(original);
        db.SaveChanges();

        var found = database.GetCollection<DayIsInt>(collection.CollectionNamespace).AsQueryable().First();
        Assert.Equal(original._id, found._id);
        Assert.Equal((int)day, found.day);
    }

    class DaysIsTimeSpan : IdIsObjectId
    {
        public TimeSpan days { get; set; }
    }

    private static readonly Action<ModelBuilder>? TimeSpanToInt = mb =>
        mb.Entity<DaysIsTimeSpan>().Property(e => e.days).HasConversion(v => v.TotalDays, v => TimeSpan.FromDays(v));

    [Theory]
    [InlineData(1633)]
    [InlineData(-123)]
    [InlineData(0)]
    public void TimeSpan_can_deserialize_and_query_from_int(int days)
    {
        var expected = new DaysIsInt {days = days, _id = ObjectId.GenerateNewId()};
        var docs = database.CreateCollection<DaysIsInt>(values: days);
        docs.InsertOne(expected);

        var collection = database.GetCollection<DaysIsTimeSpan>(docs.CollectionNamespace);
        using var db = SingleEntityDbContext.Create(collection, TimeSpanToInt);

        var found = db.Entities.First(e => e.days == TimeSpan.FromDays(days));
        Assert.Equal(expected._id, found._id);
        Assert.Equal(days, found.days.TotalDays);
    }

    [Theory]
    [InlineData(1024)]
    [InlineData(-123)]
    [InlineData(0)]
    public void TimeSpan_can_serialize_to_int(int days)
    {
        var collection = database.CreateCollection<DaysIsTimeSpan>(values: days);
        using var db = SingleEntityDbContext.Create(collection, TimeSpanToInt);

        var original = new DaysIsTimeSpan {days = TimeSpan.FromDays(days)};
        db.Entities.Add(original);
        db.SaveChanges();

        var found = database.GetCollection<DaysIsInt>(collection.CollectionNamespace).AsQueryable().First();
        Assert.Equal(original._id, found._id);
        Assert.Equal(days, found.days);
    }

    private static readonly Action<ModelBuilder>? StringToInt = mb =>
        mb.Entity<DaysIsString>().Property(e => e.days).HasConversion(v => int.Parse(v), v => v.ToString());

    private static readonly Action<ModelBuilder>? DefaultStringToInt = mb =>
        mb.Entity<DaysIsString>().Property(e => e.days).HasConversion<int>();

    [Theory]
    [InlineData(1, true)]
    [InlineData(-123, true)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(-123, false)]
    [InlineData(0, false)]
    public void String_can_deserialize_and_query_from_int(int days, bool defaultConverter)
    {
        var expected = new DaysIsInt {days = days, _id = ObjectId.GenerateNewId()};
        var docs = database.CreateCollection<DaysIsInt>(values: [days, defaultConverter]);
        docs.InsertOne(expected);

        var collection = database.GetCollection<DaysIsString>(docs.CollectionNamespace);
        using var db = SingleEntityDbContext.Create(collection, defaultConverter ? DefaultStringToInt : StringToInt);

        var found = db.Entities.First(e => e.days == days.ToString());
        Assert.Equal(expected._id, found._id);
        Assert.Equal(days.ToString(), found.days);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(-123, true)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(-123, false)]
    [InlineData(0, false)]
    public void String_can_serialize_to_int(int days, bool defaultConverter)
    {
        var collection = database.CreateCollection<DaysIsString>(values: [days, defaultConverter]);
        using var db = SingleEntityDbContext.Create(collection, defaultConverter ? DefaultStringToInt : StringToInt);

        var original = new DaysIsString {days = days.ToString()};
        db.Entities.Add(original);
        db.SaveChanges();

        var found = database.GetCollection<DaysIsInt>(collection.CollectionNamespace).AsQueryable().First();
        Assert.Equal(original._id, found._id);
        Assert.Equal(days, found.days);
    }

    class AmountIsDecimal : IdIsObjectId
    {
        public decimal amount { get; set; }
    }

    class AmountIsDecimal128 : IdIsObjectId
    {
        public Decimal128 amount { get; set; }
    }

    private static readonly Action<ModelBuilder>? DefaultDecimalToDecimal128 = mb =>
        mb.Entity<AmountIsDecimal>().Property(e => e.amount).HasConversion<Decimal128>();

    private static readonly Action<ModelBuilder>? DecimalToDecimal128 = mb =>
        mb.Entity<AmountIsDecimal>().Property(e => e.amount).HasConversion(v => new Decimal128(v), v => Decimal128.ToDecimal(v));

    [Theory]
    [InlineData("1.1234", true)]
    [InlineData("-123.213", true)]
    [InlineData("0", true)]
    [InlineData("1.1234", false)]
    [InlineData("-123.213", false)]
    [InlineData("0", false)]
    public void Decimal_can_deserialize_and_query_from_Decimal128(string amountString, bool defaultConverter)
    {
        var amount = decimal.Parse(amountString);
        var expected = new AmountIsDecimal128 {amount = new Decimal128(amount), _id = ObjectId.GenerateNewId()};
        var docs = database.CreateCollection<AmountIsDecimal128>(values: [amount, defaultConverter]);
        docs.InsertOne(expected);

        var collection = database.GetCollection<AmountIsDecimal>(docs.CollectionNamespace);
        using var db =
            SingleEntityDbContext.Create(collection, defaultConverter ? DefaultDecimalToDecimal128 : DecimalToDecimal128);

        var found = db.Entities.First(e => e.amount == amount);
        Assert.Equal(expected._id, found._id);
        Assert.Equal(amount, found.amount);
    }

    [Theory]
    [InlineData("1.1234", true)]
    [InlineData("-123.213", true)]
    [InlineData("0", true)]
    [InlineData("1.1234", false)]
    [InlineData("-123.213", false)]
    [InlineData("0", false)]
    public void Decimal_can_serialize_to_Decimal128(string amountString, bool defaultConverter)
    {
        var amount = decimal.Parse(amountString);
        var collection = database.CreateCollection<AmountIsDecimal>(values: [amount, defaultConverter]);
        using var db =
            SingleEntityDbContext.Create(collection, defaultConverter ? DefaultDecimalToDecimal128 : DecimalToDecimal128);

        var original = new AmountIsDecimal {amount = amount};
        db.Entities.Add(original);
        db.SaveChanges();

        var found = database.GetCollection<AmountIsDecimal128>(collection.CollectionNamespace).AsQueryable().First();
        Assert.Equal(original._id, found._id);
        Assert.Equal(amount, found.amount);
    }

    private static readonly Action<ModelBuilder>? Decimal128ToDecimal = mb =>
        mb.Entity<AmountIsDecimal128>().Property(e => e.amount).HasConversion(v => Decimal128.ToDecimal(v), v => new Decimal128(v));

    private static readonly Action<ModelBuilder>? DefaultDecimal128ToDecimal = mb =>
        mb.Entity<AmountIsDecimal128>().Property(e => e.amount).HasConversion<decimal>();

    [Theory]
    [InlineData("1.1234", true)]
    [InlineData("-123.213", true)]
    [InlineData("0", true)]
    [InlineData("1.1234", false)]
    [InlineData("-123.213", false)]
    [InlineData("0", false)]
    public void Decimal128_can_deserialize_and_query_from_Decimal(string amountString, bool defaultConverter)
    {
        var amount = decimal.Parse(amountString);
        var expected = new AmountIsDecimal {amount = amount, _id = ObjectId.GenerateNewId()};
        var docs = database.CreateCollection<AmountIsDecimal>(values: [amount, defaultConverter]);
        docs.InsertOne(expected);

        var collection = database.GetCollection<AmountIsDecimal>(docs.CollectionNamespace);
        using var db =
            SingleEntityDbContext.Create(collection, defaultConverter ? DefaultDecimal128ToDecimal : Decimal128ToDecimal);

        var found = db.Entities.First(e => e.amount == amount);
        Assert.Equal(expected._id, found._id);
        Assert.Equal(amount, found.amount);
    }

    [Theory]
    [InlineData("1.1234", true)]
    [InlineData("-123.213", true)]
    [InlineData("0", true)]
    [InlineData("1.1234", false)]
    [InlineData("-123.213", false)]
    [InlineData("0", false)]
    public void Decimal128_can_serialize_to_Decimal(string amountString, bool defaultConverter)
    {
        var amount = decimal.Parse(amountString);
        var collection =
            database.CreateCollection<AmountIsDecimal128>(values: [amount, defaultConverter]);
        using var db =
            SingleEntityDbContext.Create(collection, defaultConverter ? DefaultDecimal128ToDecimal : Decimal128ToDecimal);

        var original = new AmountIsDecimal128 {amount = amount};
        db.Entities.Add(original);
        db.SaveChanges();

        var found = database.GetCollection<AmountIsDecimal>(collection.CollectionNamespace).AsQueryable().First();
        Assert.Equal(original._id, found._id);
        Assert.Equal(amount, found.amount);
    }

    class AmountIsString : IdIsObjectId
    {
        public string amount { get; set; }
    }

    private static readonly Action<ModelBuilder> DecimalToString = mb =>
        mb.Entity<AmountIsDecimal>().Property(e => e.amount)
            .HasConversion(v => v.ToString(CultureInfo.InvariantCulture), v => decimal.Parse(v, CultureInfo.InvariantCulture));

    private static readonly Action<ModelBuilder> DefaultDecimalToString = mb =>
        mb.Entity<AmountIsDecimal>().Property(e => e.amount).HasConversion<string>();

    [Theory]
    [InlineData("1.1234", true)]
    [InlineData("-123.213", true)]
    [InlineData("0", true)]
    [InlineData("1.1234", false)]
    [InlineData("-123.213", false)]
    [InlineData("0", false)]
    public void Decimal_can_deserialize_and_query_from_string(string amount, bool defaultConverter)
    {
        var expected = new AmountIsString {amount = amount, _id = ObjectId.GenerateNewId()};
        var docs = database.CreateCollection<AmountIsString>(values: [amount, defaultConverter]);
        docs.InsertOne(expected);

        var collection = database.GetCollection<AmountIsDecimal>(docs.CollectionNamespace);
        using var db = SingleEntityDbContext.Create(collection, defaultConverter ? DefaultDecimalToString : DecimalToString);

        var found = db.Entities.First(e => e.amount == decimal.Parse(amount, CultureInfo.InvariantCulture));
        Assert.Equal(expected._id, found._id);
        Assert.Equal(amount, found.amount.ToString(CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("1.1234", true)]
    [InlineData("-123.213", true)]
    [InlineData("0", true)]
    [InlineData("1.1234", false)]
    [InlineData("-123.213", false)]
    [InlineData("0", false)]
    public void Decimal_can_serialize_to_string(string amount, bool defaultConverter)
    {
        var collection = database.CreateCollection<AmountIsDecimal>(values: [amount, defaultConverter]);
        using var db = SingleEntityDbContext.Create(collection, defaultConverter ? DefaultDecimalToString : DecimalToString);

        var original = new AmountIsDecimal {amount = decimal.Parse(amount, CultureInfo.InvariantCulture)};
        db.Entities.Add(original);
        db.SaveChanges();

        var found = database.GetCollection<AmountIsString>(collection.CollectionNamespace).AsQueryable().First();
        Assert.Equal(original._id, found._id);
        Assert.Equal(amount, found.amount);
    }

    class AmountIsDouble : IdIsObjectId
    {
        public double amount { get; set; }
    }

    [Theory]
    [InlineData("1.1234")]
    [InlineData("-123.213")]
    [InlineData("0")]
    public void Double_can_deserialize_and_query_from_string_default(string amount)
    {
        var expected = new AmountIsString {amount = amount, _id = ObjectId.GenerateNewId()};
        var docs = database.CreateCollection<AmountIsString>(values: amount);
        docs.InsertOne(expected);

        var collection = database.GetCollection<AmountIsDouble>(docs.CollectionNamespace);
        using var db = SingleEntityDbContext.Create(collection,
            mb => mb.Entity<AmountIsDouble>().Property(e => e.amount).HasConversion<string>());

        var found = db.Entities.First(e => e.amount.ToString() == amount);
        Assert.Equal(expected._id, found._id);
        Assert.Equal(amount, found.amount.ToString(CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData(1.1234f)]
    [InlineData(-123.213f)]
    [InlineData(0f)]
    public void Double_can_serialize_to_string_default(double amount)
    {
        var collection = database.CreateCollection<AmountIsDouble>(values: amount);
        using var db = SingleEntityDbContext.Create(collection,
            mb => mb.Entity<AmountIsDouble>().Property(e => e.amount).HasConversion<string>());

        var original = new AmountIsDouble {amount = amount};
        db.Entities.Add(original);
        db.SaveChanges();

        var found = database.GetCollection<AmountIsString>(collection.CollectionNamespace).AsQueryable().First();
        Assert.Equal(original._id, found._id);
        Assert.Equal(amount.ToString(CultureInfo.InvariantCulture), found.amount);
    }

    class AmountIsGuid : IdIsObjectId
    {
        public Guid amount { get; set; }
    }

    [Theory]
    [InlineData("6ec635e0-06e0-11ef-93e0-325096b39f47")]
    [InlineData("380bb5de-fb71-4f6d-a349-2b83908ab43b")]
    [InlineData("018f2ea7-a7a7-7c33-bd63-c6b1b1d5ecff")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void Guid_can_deserialize_and_query_from_string_default(string amount)
    {
        var expected = new AmountIsString {amount = amount, _id = ObjectId.GenerateNewId()};
        var docs = database.CreateCollection<AmountIsString>(values: amount.Substring(6));
        docs.InsertOne(expected);

        var collection = database.GetCollection<AmountIsGuid>(docs.CollectionNamespace);
        using var db = SingleEntityDbContext.Create(collection,
            mb => mb.Entity<AmountIsGuid>().Property(e => e.amount).HasConversion<string>());

        var found = db.Entities.First(e => e.amount.ToString() == amount);
        Assert.Equal(expected._id, found._id);
        Assert.Equal(amount, found.amount.ToString());
    }

    [Theory]
    [InlineData("6ec635e0-06e0-11ef-93e0-325096b39f47")]
    [InlineData("380bb5de-fb71-4f6d-a349-2b83908ab43b")]
    [InlineData("018f2ea7-a7a7-7c33-bd63-c6b1b1d5ecff")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void Guid_can_serialize_to_string_default(string amountString)
    {
        var amount = Guid.Parse(amountString);
        var collection = database.CreateCollection<AmountIsGuid>(values: amountString.Substring(6));
        using var db = SingleEntityDbContext.Create(collection,
            mb => mb.Entity<AmountIsGuid>().Property(e => e.amount).HasConversion<string>());

        var original = new AmountIsGuid {amount = amount};
        db.Entities.Add(original);
        db.SaveChanges();

        var found = database.GetCollection<AmountIsString>(collection.CollectionNamespace).AsQueryable().First();
        Assert.Equal(original._id, found._id);
        Assert.Equal(amount.ToString(), found.amount);
    }

    [Theory]
    [InlineData("507f1f77bcf86cd799439011")]
    [InlineData("507f191e810c19729de860ea")]
    public void String_can_deserialize_and_query_from_ObjectId_default(string id)
    {
        var docs = database.CreateCollection<IdIsObjectId>(values: id.Substring(6));
        docs.InsertOne(new IdIsObjectId {_id = ObjectId.Parse(id)});

        var collection = database.GetCollection<IdIsString>(docs.CollectionNamespace);
        using var db = SingleEntityDbContext.Create(collection,
            mb => mb.Entity<IdIsString>().Property(e => e._id).HasConversion<ObjectId>());

        var found = db.Entities.First(e => e._id == id);
        Assert.Equal(id, found._id);
    }

    [Theory]
    [InlineData("507f1f77bcf86cd799439011")]
    [InlineData("507f191e810c19729de860ea")]
    public void String_can_serialize_to_ObjectId_default(string id)
    {
        var docs = database.CreateCollection<IdIsString>(values: id.Substring(6));
        using var db =
            SingleEntityDbContext.Create(docs, mb => mb.Entity<IdIsString>().Property(e => e._id).HasConversion<ObjectId>());
        var original = new IdIsString {_id = id};
        db.Entities.Add(original);
        db.SaveChanges();

        var found = database.GetCollection<IdIsObjectId>(docs.CollectionNamespace).AsQueryable().First();
        Assert.Equal(original._id, found._id.ToString());
    }

    [Theory]
    [InlineData("507f1f77bcf86cd799439011")]
    [InlineData("507f191e810c19729de860ea")]
    public void ObjectId_can_deserialize_and_query_from_string_default(string id)
    {
        var docs = database.CreateCollection<IdIsString>(values: id.Substring(6));
        docs.InsertOne(new IdIsString {_id = id});

        var collection = database.GetCollection<IdIsObjectId>(docs.CollectionNamespace);
        using var db = SingleEntityDbContext.Create(collection,
            mb => mb.Entity<IdIsObjectId>().Property(e => e._id).HasConversion<string>());

        var found = db.Entities.First(e => e._id == ObjectId.Parse(id));
        Assert.Equal(id, found._id.ToString());
    }

    [Fact]
    public void Custom_struct_can_be_used_as_key()
    {
        var idValue = HexStruct.Create();
        const string expectedName = "Any Name Works";
        var docs = database.CreateCollection<IdIsStringWithName>();
        docs.InsertOne(new IdIsStringWithName {_id = idValue.ToString(), name = expectedName});

        var collection = database.GetCollection<IdGenericEntity<HexStruct>>(docs.CollectionNamespace);
        using var db = SingleEntityDbContext.Create(collection, mb =>
        {
            mb.Entity<IdGenericEntity<HexStruct>>(e =>
            {
                e.HasKey(f => f.Id);
                e.Property(f => f.Id).HasElementName("_id").HasConversion(k => k.ToString(), s => new HexStruct(s));
                e.Property(f => f.Name).HasElementName("name");
            });
        });

        var found = db.Entities.First(e => e.Id.Equals(idValue));
        Assert.Equal(idValue, found.Id);
        Assert.Equal(expectedName, found.Name);

        found.Name = "Test";
        db.SaveChanges();
    }

    [Fact]
    public void Custom_class_can_be_used_as_key()
    {
        var idValue = HexClass.Create();
        const string expectedName = "Any Name Works";
        var docs = database.CreateCollection<IdIsStringWithName>();
        docs.InsertOne(new IdIsStringWithName {_id = idValue.ToString(), name = expectedName});

        var collection = database.GetCollection<IdGenericEntity<HexClass>>(docs.CollectionNamespace);
        using var db = SingleEntityDbContext.Create(collection, mb =>
        {
            mb.Entity<IdGenericEntity<HexClass>>(e =>
            {
                e.HasKey(f => f.Id);
                e.Property(f => f.Id).HasElementName("_id").HasConversion(k => k.ToString(), s => new HexClass(s));
                e.Property(f => f.Name).HasElementName("name");
            });
        });

        var found = db.Entities.First(e => e.Id.Equals(idValue));
        Assert.Equal(idValue, found.Id);
        Assert.Equal(expectedName, found.Name);

        found.Name = "Test";
        db.SaveChanges();
    }

    class IdIsStringWithName
    {
        public string _id { get; set; }
        public string name { get; set; }
    }

    public class IdGenericEntity<T>
    {
        public T Id { get; set; }
        public string Name { get; set; }
    }

    [Fact]
    public void Custom_struct_can_be_used_as_property()
    {
        var propertyValue = HexStruct.Create();
        var docs = database.CreateCollection<GenericPropertyEntity<string>>();
        docs.InsertOne(new GenericPropertyEntity<string> {value = propertyValue.ToString()});

        var collection = database.GetCollection<GenericPropertyEntity<HexStruct>>(docs.CollectionNamespace);
        using var db = SingleEntityDbContext.Create(collection, mb =>
        {
            mb.Entity<GenericPropertyEntity<HexStruct>>(e =>
            {
                e.Property(f => f.value).HasConversion(k => k.ToString(), s => new HexStruct(s));
            });
        });

        var found = db.Entities.First(e => e.value.Equals(propertyValue));
        Assert.Equal(propertyValue, found.value);

        found.value = HexStruct.Create();
        db.SaveChanges();
    }

    [Fact]
    public void Custom_class_can_be_used_as_property()
    {
        var propertyValue = HexClass.Create();
        var docs = database.CreateCollection<GenericPropertyEntity<string>>();
        docs.InsertOne(new GenericPropertyEntity<string> {value = propertyValue.ToString()});

        var collection = database.GetCollection<GenericPropertyEntity<HexClass>>(docs.CollectionNamespace);
        using var db = SingleEntityDbContext.Create(collection, mb =>
        {
            mb.Entity<GenericPropertyEntity<HexClass>>(e =>
            {
                e.Property(f => f.value).HasConversion(k => k.ToString(), s => new HexClass(s));
            });
        });

        var found = db.Entities.First(e => e.value.Equals(propertyValue));
        Assert.Equal(propertyValue, found.value);

        found.value = HexClass.Create();
        db.SaveChanges();
    }

    class GenericPropertyEntity<T>
    {
        public ObjectId _id { get; set; }
        public T value { get; set; }
    }

    [Fact]
    public void Unsupported_nullable_value_conversion_throws()
    {
        var collection = database.GetCollection<DaysIsNullableInt>();
        using var db = SingleEntityDbContext.Create(collection, UnsupportedNullableConverter);

        var ex = Assert.Throws<NotSupportedException>(() => db.Entities.First(e => e.days == null));
        Assert.Contains(nameof(DaysIsNullableInt), ex.Message);
        Assert.Contains(nameof(DaysIsNullableInt.days), ex.Message);
        Assert.Contains("HasConversion", ex.Message);
    }

    class DayIsNullableEnum : IdIsObjectId
    {
        public DayOfWeek? day { get; set; }
    }

    private static readonly Action<ModelBuilder>? NullableEnumToString = mb =>
        mb.Entity<DayIsNullableEnum>().Property(e => e.day)
            .HasConversion(new ValueConverter<DayOfWeek, string>(e => e.ToString(), f => Enum.Parse<DayOfWeek>(f)));

    private static readonly Action<ModelBuilder>? DefaultNullableEnumToString = mb =>
        mb.Entity<DayIsNullableEnum>().Property(e => e.day).HasConversion<string>();

    [Fact(Skip = "Not currently supported, see EF-169")]
    public void Custom_struct_can_be_used_in_an_array()
    {
        var arrayValues = Enumerable.Range(0, 5).Select(_ => HexStruct.Create()).ToArray();
        var collection = database.CreateCollection<ValueArrayGenericEntity<HexStruct>>();

        {
            using var db = SingleEntityDbContext.Create(collection, ConfigureModel);
            db.Add(new ValueArrayGenericEntity<HexStruct> {values = arrayValues});
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection, ConfigureModel);
            var found = db.Entities.First();
            Assert.Equal(arrayValues, found.values);
        }

        void ConfigureModel(ModelBuilder mb)
        {
            mb.Entity<ValueArrayGenericEntity<HexStruct>>(e =>
            {
                e.Property(f => f.values)
                    .HasConversion(
                        k => k.Select(r => r.ToString()).ToArray(),
                        s => s.Select(r => new HexStruct(r)).ToArray());
            });
        }
    }

    [Fact(Skip = "Not currently supported, see EF-169")]
    public void Custom_class_can_be_used_in_an_array()
    {
        var arrayValues = Enumerable.Range(0, 5).Select(_ => HexClass.Create()).ToArray();
        var collection = database.CreateCollection<ValueArrayGenericEntity<HexClass>>();

        {
            using var db = SingleEntityDbContext.Create(collection, ConfigureModel);
            db.Add(new ValueArrayGenericEntity<HexClass> {values = arrayValues});
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection, ConfigureModel);
            var found = db.Entities.First();
            Assert.Equal(arrayValues, found.values);
        }

        void ConfigureModel(ModelBuilder mb)
        {
            mb.Entity<ValueArrayGenericEntity<HexClass>>(e =>
            {
                e.Property(f => f.values)
                    .HasConversion(
                        k => k.Select(r => r.ToString()).ToArray(),
                        s => s.Select(r => new HexClass(r)).ToArray());
            });
        }
    }

    class ValueArrayGenericEntity<T>
    {
        public ObjectId _id { get; set; }
        public T[] values { get; set; }
    }

    public readonly struct HexStruct : IEquatable<HexStruct>
    {
        private readonly byte[] _keyBytes;

        public HexStruct(string hex)
        {
            _keyBytes = Convert.FromHexString(hex);
        }

        public HexStruct(byte[] keyBytes)
        {
            _keyBytes = keyBytes;
        }

        public override string ToString() => Convert.ToHexString(_keyBytes);

        public static HexStruct Create()
            => new(BitConverter.GetBytes(DateTime.Now.Ticks).Reverse().Concat(RandomNumberGenerator.GetBytes(8)).ToArray());

        public bool Equals(HexStruct other)
            => _keyBytes.SequenceEqual(other._keyBytes);

        public override bool Equals(object? obj)
            => obj is HexStruct other && Equals(other);

        public override int GetHashCode()
            => _keyBytes.GetHashCode();

        public static bool operator ==(HexStruct left, HexStruct right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(HexStruct left, HexStruct right)
        {
            return !(left == right);
        }
    }

    public class HexClass : IEquatable<HexClass>
    {
        private readonly byte[] _keyBytes;

        public HexClass(string hex)
        {
            _keyBytes = Convert.FromHexString(hex);
        }

        public HexClass(byte[] keyBytes)
        {
            _keyBytes = keyBytes;
        }

        public override string ToString() => Convert.ToHexString(_keyBytes);

        public static HexClass Create()
            => new(BitConverter.GetBytes(DateTime.Now.Ticks).Reverse().Concat(RandomNumberGenerator.GetBytes(8)).ToArray());

        public bool Equals(HexClass? other)
            => _keyBytes.SequenceEqual(other._keyBytes);

        public override bool Equals(object? obj)
            => obj is HexStruct other && Equals(other);

        public override int GetHashCode()
            => _keyBytes.GetHashCode();
    }
}
