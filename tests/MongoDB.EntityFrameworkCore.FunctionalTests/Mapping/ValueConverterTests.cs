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

    private static readonly Action<ModelBuilder>? ObjectIdToMongoString = mb =>
        mb.Entity<IdIsObjectId>().Property(e => e._id).HasConversion(v => v.ToString(), v => ObjectId.Parse(v));

    private static readonly Action<ModelBuilder>? DefaultObjectIdToMongoString = mb =>
        mb.Entity<IdIsObjectId>().Property(e => e._id).HasConversion<string>();

    [Theory]
    [InlineData(["507f1f77bcf86cd799439011", true])]
    [InlineData(["507f191e810c19729de860ea", true])]
    [InlineData(["507f1f77bcf86cd799439011", false])]
    [InlineData(["507f191e810c19729de860ea", false])]
    public void ObjectId_can_deserialize_and_query_from_string(string id, bool defaultConverter)
    {
        var expectedId = ObjectId.Parse(id);
        var expected = new IdIsString {_id = expectedId.ToString()};
        var docs = database.CreateTemporaryCollection<IdIsString>(values: [id, defaultConverter]);
        docs.InsertOne(expected);

        var collection = database.GetExistingTemporaryCollection<IdIsObjectId>(docs.CollectionNamespace);
        using var db = SingleEntityDbContext.Create(collection,
            defaultConverter ? DefaultObjectIdToMongoString : ObjectIdToMongoString);

        var found = db.Entities.First(e => e._id == expectedId);
        Assert.Equal(expectedId, found._id);
    }

    [Theory]
    [InlineData(["507f1f77bcf86cd799439011", true])]
    [InlineData(["507f191e810c19729de860ea", true])]
    [InlineData(["507f1f77bcf86cd799439011", false])]
    [InlineData(["507f191e810c19729de860ea", false])]
    public void ObjectId_can_serialize_to_string(string id, bool defaultConverter)
    {
        var collection = database.CreateTemporaryCollection<IdIsObjectId>(values: [id, defaultConverter]);
        using var db = SingleEntityDbContext.Create(collection,
            defaultConverter ? DefaultObjectIdToMongoString : ObjectIdToMongoString);

        var original = new IdIsObjectId {_id = ObjectId.Parse(id)};
        db.Entities.Add(original);
        db.SaveChanges();

        var found = database.GetExistingTemporaryCollection<IdIsString>(collection.CollectionNamespace).AsQueryable().First();
        Assert.Equal(original._id.ToString(), found._id);
    }

    private static readonly Action<ModelBuilder>? StringToMongoObjectId = mb =>
        mb.Entity<IdIsString>().Property(e => e._id).HasConversion(v => ObjectId.Parse(v), v => v.ToString());

    private static readonly Action<ModelBuilder>? DefaultStringToMongoObjectId = mb =>
        mb.Entity<IdIsString>().Property(e => e._id).HasConversion<ObjectId>();

    [Theory]
    [InlineData(["507f1f77bcf86cd799439011", true])]
    [InlineData(["507f191e810c19729de860ea", true])]
    [InlineData(["507f1f77bcf86cd799439011", false])]
    [InlineData(["507f191e810c19729de860ea", false])]
    public void String_can_deserialize_and_query_from_ObjectId(string id, bool defaultConverter)
    {
        var expected = new IdIsObjectId {_id = ObjectId.Parse(id)};
        var docs = database.CreateTemporaryCollection<IdIsObjectId>(values: [id, defaultConverter]);
        docs.InsertOne(expected);

        var collection = database.GetExistingTemporaryCollection<IdIsString>(docs.CollectionNamespace);
        using var db = SingleEntityDbContext.Create(collection,
            defaultConverter ? DefaultStringToMongoObjectId : StringToMongoObjectId);

        var found = db.Entities.First(e => e._id == expected._id.ToString());
        Assert.Equal(expected._id.ToString(), found._id);
    }

    [Theory]
    [InlineData(["507f1f77bcf86cd799439011", true])]
    [InlineData(["507f191e810c19729de860ea", true])]
    [InlineData(["507f1f77bcf86cd799439011", false])]
    [InlineData(["507f191e810c19729de860ea", false])]
    public void String_can_serialize_to_ObjectId(string id, bool defaultConverter)
    {
        var collection = database.CreateTemporaryCollection<IdIsString>(values: [id, defaultConverter]);
        using var db = SingleEntityDbContext.Create(collection,
            defaultConverter ? DefaultStringToMongoObjectId : StringToMongoObjectId);

        var original = new IdIsString {_id = id};
        db.Entities.Add(original);
        db.SaveChanges();

        var found = database.GetExistingTemporaryCollection<IdIsObjectId>(collection.CollectionNamespace).AsQueryable().First();
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

    private static readonly Action<ModelBuilder>? BoolToMongoString = mb =>
        mb.Entity<ActiveIsBool>()
            .Property(e => e.active).HasConversion(v => v.ToString(), v => bool.Parse(v));

    private static readonly Action<ModelBuilder>? DefaultBoolToMongoString = mb =>
        mb.Entity<ActiveIsBool>()
            .Property(e => e.active).HasConversion<string>();

    [Theory]
    [InlineData([true, true])]
    [InlineData([true, false])]
    [InlineData([false, true])]
    [InlineData([false, false])]
    public void Bool_can_deserialize_and_query_from_string(bool active, bool defaultConverter)
    {
        var expectedActive =
            defaultConverter ? active ? "1" : "0" : active.ToString(); // EF default uses "0" and "1" not "false" and "true"
        var expected = new ActiveIsString {active = expectedActive, _id = ObjectId.GenerateNewId()};
        var docs = database.CreateTemporaryCollection<ActiveIsString>(values: [active, defaultConverter]);
        docs.InsertOne(expected);

        var collection = database.GetExistingTemporaryCollection<ActiveIsBool>(docs.CollectionNamespace);
        using var db = SingleEntityDbContext.Create(collection, defaultConverter ? DefaultBoolToMongoString : BoolToMongoString);

        var found = db.Entities.First(e => e.active == active);
        Assert.Equal(expected._id, found._id);
        Assert.Equal(active, found.active);
    }

    [Theory]
    [InlineData([true, true])]
    [InlineData([true, false])]
    [InlineData([false, true])]
    [InlineData([false, false])]
    public void Bool_can_serialize_to_string(bool active, bool defaultConverter)
    {
        var collection = database.CreateTemporaryCollection<ActiveIsBool>(values: [active, defaultConverter]);
        using var db = SingleEntityDbContext.Create(collection, defaultConverter ? DefaultBoolToMongoString : BoolToMongoString);

        var original = new ActiveIsBool {active = active};
        db.Entities.Add(original);
        db.SaveChanges();

        var found = database.GetExistingTemporaryCollection<ActiveIsString>(collection.CollectionNamespace)
            .AsQueryable().First();
        Assert.Equal(original._id, found._id);

        var expectedActive =
            defaultConverter ? active ? "1" : "0" : active.ToString(); // EF default uses "0" and "1" not "false" and "true"
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

    private static readonly Action<ModelBuilder>? IntToMongoString = mb =>
        mb.Entity<DaysIsInt>()
            .Property(e => e.days).HasConversion(v => v.ToString(), v => int.Parse(v));

    private static readonly Action<ModelBuilder>? DefaultIntToMongoString = mb =>
        mb.Entity<DaysIsInt>()
            .Property(e => e.days).HasConversion<string>();

    [Theory]
    [InlineData([1, true])]
    [InlineData([1, false])]
    [InlineData([-123, true])]
    [InlineData([-123, false])]
    [InlineData([0, true])]
    [InlineData([0, false])]
    public void Int_can_deserialize_and_query_from_string(int days, bool defaultConverter)
    {
        var expected = new DaysIsString {days = days.ToString(), _id = ObjectId.GenerateNewId()};
        var docs = database.CreateTemporaryCollection<DaysIsString>(values: [days, defaultConverter]);
        docs.InsertOne(expected);

        var collection = database.GetExistingTemporaryCollection<DaysIsInt>(docs.CollectionNamespace);
        using var db = SingleEntityDbContext.Create(collection, defaultConverter ? DefaultIntToMongoString : IntToMongoString);

        var found = db.Entities.First(e => e.days == days);
        Assert.Equal(expected._id, found._id);
        Assert.Equal(days, found.days);
    }

    [Theory]
    [InlineData([1, true])]
    [InlineData([1, false])]
    [InlineData([-123, true])]
    [InlineData([-123, false])]
    [InlineData([0, true])]
    [InlineData([0, false])]
    public void Int_can_serialize_to_string(int days, bool defaultConverter)
    {
        var collection = database.CreateTemporaryCollection<DaysIsInt>(values: [days, defaultConverter]);
        using var db = SingleEntityDbContext.Create(collection, defaultConverter ? DefaultIntToMongoString : IntToMongoString);

        var original = new DaysIsInt {days = days};
        db.Entities.Add(original);
        db.SaveChanges();

        var found = database.GetExistingTemporaryCollection<DaysIsString>(collection.CollectionNamespace)
            .AsQueryable().First();
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

    private static readonly Action<ModelBuilder>? EnumToMongoString = mb =>
        mb.Entity<DayIsEnum>().Property(e => e.day).HasConversion<string>(e => e.ToString(), s => Enum.Parse<DayOfWeek>(s));

    private static readonly Action<ModelBuilder>? DefaultEnumToMongoString = mb =>
        mb.Entity<DayIsEnum>().Property(e => e.day).HasConversion<string>();

    [Theory]
    [InlineData([DayOfWeek.Wednesday, true])]
    [InlineData([DayOfWeek.Wednesday, false])]
    [InlineData([DayOfWeek.Saturday, true])]
    [InlineData([DayOfWeek.Saturday, false])]
    [InlineData([DayOfWeek.Sunday, true])]
    [InlineData([DayOfWeek.Sunday, false])]
    public void Enum_can_deserialize_and_query_from_string(DayOfWeek day, bool defaultConverter)
    {
        var expected = new DayIsString {day = day.ToString(), _id = ObjectId.GenerateNewId()};
        var docs = database.CreateTemporaryCollection<DayIsString>(values: [day, defaultConverter]);
        docs.InsertOne(expected);

        var collection = database.GetExistingTemporaryCollection<DayIsEnum>(docs.CollectionNamespace);
        using var db = SingleEntityDbContext.Create(collection, defaultConverter ? DefaultEnumToMongoString : EnumToMongoString);

        var found = db.Entities.First(e => e.day == day);
        Assert.Equal(expected._id, found._id);
        Assert.Equal(day, found.day);
    }

    [Theory]
    [InlineData([DayOfWeek.Wednesday, true])]
    [InlineData([DayOfWeek.Wednesday, false])]
    [InlineData([DayOfWeek.Saturday, true])]
    [InlineData([DayOfWeek.Saturday, false])]
    [InlineData([DayOfWeek.Sunday, true])]
    [InlineData([DayOfWeek.Sunday, false])]
    public void Enum_can_serialize_to_string(DayOfWeek day, bool defaultConverter)
    {
        var collection = database.CreateTemporaryCollection<DayIsString>(values: [day, defaultConverter]);
        using var db = SingleEntityDbContext.Create(collection, defaultConverter ? DefaultEnumToMongoString : EnumToMongoString);

        var original = new DayIsString {day = day.ToString()};
        db.Entities.Add(original);
        db.SaveChanges();

        var found = database.GetExistingTemporaryCollection<DayIsString>(collection.CollectionNamespace)
            .AsQueryable().First();
        Assert.Equal(original._id, found._id);
        Assert.Equal(day.ToString(), found.day);
    }

    class DayIsInt : IdIsObjectId
    {
        public int day { get; set; }
    }

    private static readonly Action<ModelBuilder>? EnumToMongoInt = mb =>
        mb.Entity<DayIsEnum>().Property(e => e.day).HasConversion(e => (int)e, i => (DayOfWeek)i);

    private static readonly Action<ModelBuilder>? DefaultEnumToMongoInt = mb =>
        mb.Entity<DayIsEnum>().Property(e => e.day).HasConversion<int>();

    [Theory]
    [InlineData([DayOfWeek.Wednesday, true])]
    [InlineData([DayOfWeek.Wednesday, false])]
    [InlineData([DayOfWeek.Saturday, true])]
    [InlineData([DayOfWeek.Saturday, false])]
    [InlineData([DayOfWeek.Sunday, true])]
    [InlineData([DayOfWeek.Sunday, false])]
    public void Enum_can_deserialize_and_query_from_int(DayOfWeek day, bool defaultConverter)
    {
        var expected = new DayIsInt {day = (int)day, _id = ObjectId.GenerateNewId()};
        var docs = database.CreateTemporaryCollection<DayIsInt>(values: [day, defaultConverter]);
        docs.InsertOne(expected);

        var collection = database.GetExistingTemporaryCollection<DayIsEnum>(docs.CollectionNamespace);
        using var db = SingleEntityDbContext.Create(collection, defaultConverter ? DefaultEnumToMongoInt : EnumToMongoInt);

        var found = db.Entities.First(e => e.day == day);
        Assert.Equal(expected._id, found._id);
        Assert.Equal(day, found.day);
    }

    [Theory]
    [InlineData([DayOfWeek.Wednesday, true])]
    [InlineData([DayOfWeek.Wednesday, false])]
    [InlineData([DayOfWeek.Saturday, true])]
    [InlineData([DayOfWeek.Saturday, false])]
    [InlineData([DayOfWeek.Sunday, true])]
    [InlineData([DayOfWeek.Sunday, false])]
    public void Enum_can_serialize_to_int(DayOfWeek day, bool defaultConverter)
    {
        var collection = database.CreateTemporaryCollection<DayIsInt>(values: [day, defaultConverter]);
        using var db = SingleEntityDbContext.Create(collection, defaultConverter ? DefaultEnumToMongoInt : EnumToMongoInt);

        var original = new DayIsInt {day = (int)day};
        db.Entities.Add(original);
        db.SaveChanges();

        var found = database.GetExistingTemporaryCollection<DayIsInt>(collection.CollectionNamespace)
            .AsQueryable().First();
        Assert.Equal(original._id, found._id);
        Assert.Equal((int)day, found.day);
    }

    class DaysIsTimeSpan : IdIsObjectId
    {
        public TimeSpan days { get; set; }
    }

    private static readonly Action<ModelBuilder>? TimeSpanToMongoInt = mb =>
        mb.Entity<DaysIsTimeSpan>()
            .Property(e => e.days).HasConversion(v => v.TotalDays, v => TimeSpan.FromDays(v));

    // There is no default TimeSpan to Int in EF

    [Theory]
    [InlineData([1633])]
    [InlineData([-123])]
    [InlineData([0])]
    public void TimeSpan_can_deserialize_and_query_from_int(int days)
    {
        var expected = new DaysIsInt {days = days, _id = ObjectId.GenerateNewId()};
        var docs = database.CreateTemporaryCollection<DaysIsInt>(values: days);
        docs.InsertOne(expected);

        var collection = database.GetExistingTemporaryCollection<DaysIsTimeSpan>(docs.CollectionNamespace);
        using var db = SingleEntityDbContext.Create(collection, TimeSpanToMongoInt);

        var found = db.Entities.First(e => e.days == TimeSpan.FromDays(days));
        Assert.Equal(expected._id, found._id);
        Assert.Equal(days, found.days.TotalDays);
    }

    [Theory]
    [InlineData([1024])]
    [InlineData([-123])]
    [InlineData([0])]
    public void TimeSpan_can_serialize_to_int(int days)
    {
        var collection = database.CreateTemporaryCollection<DaysIsTimeSpan>(values: days);
        using var db = SingleEntityDbContext.Create(collection, TimeSpanToMongoInt);

        var original = new DaysIsTimeSpan {days = TimeSpan.FromDays(days)};
        db.Entities.Add(original);
        db.SaveChanges();

        var found = database.GetExistingTemporaryCollection<DaysIsInt>(collection.CollectionNamespace)
            .AsQueryable().First();
        Assert.Equal(original._id, found._id);
        Assert.Equal(days, found.days);
    }

    private static readonly Action<ModelBuilder>? StringToMongoInt = mb =>
        mb.Entity<DaysIsString>()
            .Property(e => e.days).HasConversion(v => int.Parse(v), v => v.ToString());

    private static readonly Action<ModelBuilder>? DefaultStringToMongoInt = mb =>
        mb.Entity<DaysIsString>()
            .Property(e => e.days).HasConversion<int>();

    [Theory]
    [InlineData([1, true])]
    [InlineData([-123, true])]
    [InlineData([0, true])]
    [InlineData([1, false])]
    [InlineData([-123, false])]
    [InlineData([0, false])]
    public void String_can_deserialize_and_query_from_int(int days, bool defaultConverter)
    {
        var expected = new DaysIsInt {days = days, _id = ObjectId.GenerateNewId()};
        var docs = database.CreateTemporaryCollection<DaysIsInt>(values: [days, defaultConverter]);
        docs.InsertOne(expected);

        var collection = database.GetExistingTemporaryCollection<DaysIsString>(docs.CollectionNamespace);
        using var db =
            SingleEntityDbContext.Create(collection, defaultConverter ? DefaultStringToMongoInt : StringToMongoInt);

        var found = db.Entities.First(e => e.days == days.ToString());
        Assert.Equal(expected._id, found._id);
        Assert.Equal(days.ToString(), found.days);
    }

    [Theory]
    [InlineData([1, true])]
    [InlineData([-123, true])]
    [InlineData([0, true])]
    [InlineData([1, false])]
    [InlineData([-123, false])]
    [InlineData([0, false])]
    public void String_can_serialize_to_int(int days, bool defaultConverter)
    {
        var collection =
            database.CreateTemporaryCollection<DaysIsString>(values: [days, defaultConverter]);
        using var db =
            SingleEntityDbContext.Create(collection, defaultConverter ? DefaultStringToMongoInt : StringToMongoInt);

        var original = new DaysIsString {days = days.ToString()};
        db.Entities.Add(original);
        db.SaveChanges();

        var found = database.GetExistingTemporaryCollection<DaysIsInt>(collection.CollectionNamespace)
            .AsQueryable().First();
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

    private static readonly Action<ModelBuilder>? DefaultDecimalToMongoDecimal128 = mb =>
        mb.Entity<AmountIsDecimal>()
            .Property(e => e.amount).HasConversion<Decimal128>();

    private static readonly Action<ModelBuilder>? DecimalToMongoDecimal128 = mb =>
        mb.Entity<AmountIsDecimal>()
            .Property(e => e.amount).HasConversion(v => new Decimal128(v), v => Decimal128.ToDecimal(v));

    [Theory]
    [InlineData(["1.1234", true])]
    [InlineData(["-123.213", true])]
    [InlineData(["0", true])]
    [InlineData(["1.1234", false])]
    [InlineData(["-123.213", false])]
    [InlineData(["0", false])]
    public void Decimal_can_deserialize_and_query_from_Decimal128(string amountString, bool defaultConverter)
    {
        var amount = decimal.Parse(amountString);
        var expected = new AmountIsDecimal128 {amount = new Decimal128(amount), _id = ObjectId.GenerateNewId()};
        var docs = database.CreateTemporaryCollection<AmountIsDecimal128>(values: [amount, defaultConverter]);
        docs.InsertOne(expected);

        var collection = database.GetExistingTemporaryCollection<AmountIsDecimal>(docs.CollectionNamespace);
        using var db = SingleEntityDbContext.Create(collection,
            defaultConverter ? DefaultDecimalToMongoDecimal128 : DecimalToMongoDecimal128);

        var found = db.Entities.First(e => e.amount == amount);
        Assert.Equal(expected._id, found._id);
        Assert.Equal(amount, found.amount);
    }

    [Theory]
    [InlineData(["1.1234", true])]
    [InlineData(["-123.213", true])]
    [InlineData(["0", true])]
    [InlineData(["1.1234", false])]
    [InlineData(["-123.213", false])]
    [InlineData(["0", false])]
    public void Decimal_can_serialize_to_Decimal128(string amountString, bool defaultConverter)
    {
        var amount = decimal.Parse(amountString);
        var collection =
            database.CreateTemporaryCollection<AmountIsDecimal>(values: [amount, defaultConverter]);
        using var db = SingleEntityDbContext.Create(collection,
            defaultConverter ? DefaultDecimalToMongoDecimal128 : DecimalToMongoDecimal128);

        var original = new AmountIsDecimal {amount = amount};
        db.Entities.Add(original);
        db.SaveChanges();

        var found = database.GetExistingTemporaryCollection<AmountIsDecimal128>(collection.CollectionNamespace)
            .AsQueryable().First();
        Assert.Equal(original._id, found._id);
        Assert.Equal(amount, found.amount);
    }

    private static readonly Action<ModelBuilder>? Decimal128ToMongoDecimal = mb =>
        mb.Entity<AmountIsDecimal128>()
            .Property(e => e.amount).HasConversion(v => Decimal128.ToDecimal(v), v => new Decimal128(v));

    private static readonly Action<ModelBuilder>? DefaultDecimal128ToMongoDecimal = mb =>
        mb.Entity<AmountIsDecimal128>()
            .Property(e => e.amount).HasConversion<decimal>();

    [Theory]
    [InlineData(["1.1234", true])]
    [InlineData(["-123.213", true])]
    [InlineData(["0", true])]
    [InlineData(["1.1234", false])]
    [InlineData(["-123.213", false])]
    [InlineData(["0", false])]
    public void Decimal128_can_deserialize_and_query_from_Decimal(string amountString, bool defaultConverter)
    {
        var amount = decimal.Parse(amountString);
        var expected = new AmountIsDecimal {amount = amount, _id = ObjectId.GenerateNewId()};
        var docs = database.CreateTemporaryCollection<AmountIsDecimal>(values: [amount, defaultConverter]);
        docs.InsertOne(expected);

        var collection = database.GetExistingTemporaryCollection<AmountIsDecimal>(docs.CollectionNamespace);
        using var db = SingleEntityDbContext.Create(collection,
            defaultConverter ? DefaultDecimal128ToMongoDecimal : Decimal128ToMongoDecimal);

        var found = db.Entities.First(e => e.amount == amount);
        Assert.Equal(expected._id, found._id);
        Assert.Equal(amount, found.amount);
    }

    [Theory]
    [InlineData(["1.1234", true])]
    [InlineData(["-123.213", true])]
    [InlineData(["0", true])]
    [InlineData(["1.1234", false])]
    [InlineData(["-123.213", false])]
    [InlineData(["0", false])]
    public void Decimal128_can_serialize_to_Decimal(string amountString, bool defaultConverter)
    {
        var amount = decimal.Parse(amountString);
        var collection =
            database.CreateTemporaryCollection<AmountIsDecimal128>(values: [amount, defaultConverter]);
        using var db = SingleEntityDbContext.Create(collection,
            defaultConverter ? DefaultDecimal128ToMongoDecimal : Decimal128ToMongoDecimal);

        var original = new AmountIsDecimal128 {amount = amount};
        db.Entities.Add(original);
        db.SaveChanges();

        var found = database.GetExistingTemporaryCollection<AmountIsDecimal>(collection.CollectionNamespace)
            .AsQueryable().First();
        Assert.Equal(original._id, found._id);
        Assert.Equal(amount, found.amount);
    }

    class AmountIsString : IdIsObjectId
    {
        public string amount { get; set; }
    }

    private static readonly Action<ModelBuilder> DecimalToMongoString = mb =>
        mb.Entity<AmountIsString>()
            .Property(e => e.amount).HasConversion(v => decimal.Parse(v), v => v.ToString());

    private static readonly Action<ModelBuilder> DefaultDecimalToMongoString = mb =>
        mb.Entity<AmountIsString>()
            .Property(e => e.amount).HasConversion<string>();

    [Theory]
    [InlineData(["1.1234", true])]
    [InlineData(["-123.213", true])]
    [InlineData(["0", true])]
    [InlineData(["1.1234", false])]
    [InlineData(["-123.213", false])]
    [InlineData(["0", false])]
    public void Decimal_can_deserialize_and_query_from_string(string amount, bool defaultConverter)
    {
        var expected = new AmountIsString {amount = amount, _id = ObjectId.GenerateNewId()};
        var docs = database.CreateTemporaryCollection<AmountIsString>(values: [amount, defaultConverter]);
        docs.InsertOne(expected);

        var collection = database.GetExistingTemporaryCollection<AmountIsDecimal>(docs.CollectionNamespace);
        using var db =
            SingleEntityDbContext.Create(collection, defaultConverter ? DefaultDecimalToMongoString : DecimalToMongoString);

        var found = db.Entities.First(e => e.amount == decimal.Parse(amount));
        Assert.Equal(expected._id, found._id);
        Assert.Equal(amount, found.amount.ToString());
    }

    [Theory]
    [InlineData(["1.1234", true])]
    [InlineData(["-123.213", true])]
    [InlineData(["0", true])]
    [InlineData(["1.1234", false])]
    [InlineData(["-123.213", false])]
    [InlineData(["0", false])]
    public void Decimal_can_serialize_to_string(string amount, bool defaultConverter)
    {
        var collection =
            database.CreateTemporaryCollection<AmountIsDecimal>(values: [amount, defaultConverter]);
        using var db =
            SingleEntityDbContext.Create(collection, defaultConverter ? DefaultDecimalToMongoString : DecimalToMongoString);

        var original = new AmountIsDecimal {amount = decimal.Parse(amount)};
        db.Entities.Add(original);
        db.SaveChanges();

        var found = database.GetExistingTemporaryCollection<AmountIsString>(collection.CollectionNamespace)
            .AsQueryable().First();
        Assert.Equal(original._id, found._id);
        Assert.Equal(amount, found.amount);
    }

    class AmountIsDouble : IdIsObjectId
    {
        public double amount { get; set; }
    }

    [Theory]
    [InlineData(["1.1234"])]
    [InlineData(["-123.213"])]
    [InlineData(["0"])]
    public void Double_can_deserialize_and_query_from_string_default(string amount)
    {
        var expected = new AmountIsString {amount = amount, _id = ObjectId.GenerateNewId()};
        var docs = database.CreateTemporaryCollection<AmountIsString>(values: amount);
        docs.InsertOne(expected);

        var collection = database.GetExistingTemporaryCollection<AmountIsDouble>(docs.CollectionNamespace);
        using var db = SingleEntityDbContext.Create(collection, mb =>
        {
            mb.Entity<AmountIsDouble>().Property(e => e.amount).HasConversion<string>();
        });

        var found = db.Entities.First(e => e.amount.ToString() == amount);
        Assert.Equal(expected._id, found._id);
        Assert.Equal(amount, found.amount.ToString());
    }

    [Theory]
    [InlineData([1.1234f])]
    [InlineData([-123.213f])]
    [InlineData([0f])]
    public void Double_can_serialize_to_string_default(double amount)
    {
        var collection =
            database.CreateTemporaryCollection<AmountIsDouble>(values: amount);
        using var db = SingleEntityDbContext.Create(collection, mb =>
        {
            mb.Entity<AmountIsDouble>().Property(e => e.amount).HasConversion<string>();
        });

        var original = new AmountIsDouble {amount = amount};
        db.Entities.Add(original);
        db.SaveChanges();

        var found = database.GetExistingTemporaryCollection<AmountIsString>(collection.CollectionNamespace)
            .AsQueryable().First();
        Assert.Equal(original._id, found._id);
        Assert.Equal(amount.ToString(), found.amount);
    }

    class AmountIsGuid : IdIsObjectId
    {
        public Guid amount { get; set; }
    }

    [Theory]
    [InlineData(["6ec635e0-06e0-11ef-93e0-325096b39f47"])]
    [InlineData(["380bb5de-fb71-4f6d-a349-2b83908ab43b"])]
    [InlineData(["018f2ea7-a7a7-7c33-bd63-c6b1b1d5ecff"])]
    [InlineData(["00000000-0000-0000-0000-000000000000"])]
    public void Guid_can_deserialize_and_query_from_string_default(string amount)
    {
        var expected = new AmountIsString {amount = amount, _id = ObjectId.GenerateNewId()};
        var docs = database.CreateTemporaryCollection<AmountIsString>(values: amount.Substring(6));
        docs.InsertOne(expected);

        var collection = database.GetExistingTemporaryCollection<AmountIsGuid>(docs.CollectionNamespace);
        using var db = SingleEntityDbContext.Create(collection, mb =>
        {
            mb.Entity<AmountIsGuid>().Property(e => e.amount).HasConversion<string>();
        });

        var found = db.Entities.First(e => e.amount.ToString() == amount);
        Assert.Equal(expected._id, found._id);
        Assert.Equal(amount, found.amount.ToString());
    }

    [Theory]
    [InlineData(["6ec635e0-06e0-11ef-93e0-325096b39f47"])]
    [InlineData(["380bb5de-fb71-4f6d-a349-2b83908ab43b"])]
    [InlineData(["018f2ea7-a7a7-7c33-bd63-c6b1b1d5ecff"])]
    [InlineData(["00000000-0000-0000-0000-000000000000"])]
    public void Guid_can_serialize_to_string_default(string amountString)
    {
        var amount = Guid.Parse(amountString);
        var collection =
            database.CreateTemporaryCollection<AmountIsGuid>(values: amountString.Substring(6));
        using var db = SingleEntityDbContext.Create(collection, mb =>
        {
            mb.Entity<AmountIsGuid>().Property(e => e.amount).HasConversion<string>();
        });

        var original = new AmountIsGuid {amount = amount};
        db.Entities.Add(original);
        db.SaveChanges();

        var found = database.GetExistingTemporaryCollection<AmountIsString>(collection.CollectionNamespace)
            .AsQueryable().First();
        Assert.Equal(original._id, found._id);
        Assert.Equal(amount.ToString(), found.amount);
    }

    [Theory]
    [InlineData(["507f1f77bcf86cd799439011"])]
    [InlineData(["507f191e810c19729de860ea"])]
    public void String_can_deserialize_and_query_from_ObjectId_default(string id)
    {
        var docs = database.CreateTemporaryCollection<IdIsObjectId>(values: id.Substring(6));
        docs.InsertOne(new IdIsObjectId {_id = ObjectId.Parse(id)});

        var collection = database.GetExistingTemporaryCollection<IdIsString>(docs.CollectionNamespace);
        using var db = SingleEntityDbContext.Create(collection, mb =>
        {
            mb.Entity<IdIsString>().Property(e => e._id).HasConversion<ObjectId>();
        });

        var found = db.Entities.First(e => e._id == id);
        Assert.Equal(id, found._id);
    }

    [Theory]
    [InlineData(["507f1f77bcf86cd799439011"])]
    [InlineData(["507f191e810c19729de860ea"])]
    public void String_can_serialize_to_ObjectId_default(string id)
    {
        var docs = database.CreateTemporaryCollection<IdIsString>(values: id.Substring(6));
        using var db = SingleEntityDbContext.Create(docs, mb =>
        {
            mb.Entity<IdIsString>().Property(e => e._id).HasConversion<ObjectId>();
        });
        var original = new IdIsString {_id = id};
        db.Entities.Add(original);
        db.SaveChanges();

        var found = database.GetExistingTemporaryCollection<IdIsObjectId>(docs.CollectionNamespace).AsQueryable()
            .First();
        Assert.Equal(original._id, found._id.ToString());
    }

    [Theory]
    [InlineData(["507f1f77bcf86cd799439011"])]
    [InlineData(["507f191e810c19729de860ea"])]
    public void ObjectId_can_deserialize_and_query_from_string_default(string id)
    {
        var docs = database.CreateTemporaryCollection<IdIsString>(values: id.Substring(6));
        docs.InsertOne(new IdIsString {_id = id});

        var collection = database.GetExistingTemporaryCollection<IdIsObjectId>(docs.CollectionNamespace);
        using var db = SingleEntityDbContext.Create(collection, mb =>
        {
            mb.Entity<IdIsObjectId>().Property(e => e._id).HasConversion<string>();
        });

        var found = db.Entities.First(e => e._id == ObjectId.Parse(id));
        Assert.Equal(id, found._id.ToString());
    }
}
