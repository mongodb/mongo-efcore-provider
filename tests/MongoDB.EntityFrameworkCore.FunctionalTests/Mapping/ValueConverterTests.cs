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

using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using MongoDB.Bson;
using MongoDB.Driver;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Mapping;

[XUnitCollection("MappingTests")]
public class ValueConverterTests(TemporaryDatabaseFixture tempDatabase)
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

    private static readonly Action<ModelBuilder>? ClrObjectIdToMongoString = mb =>
    {
        mb.Entity<IdIsObjectId>()
            .Property(e => e._id).HasConversion(v => v.ToString(), v => ObjectId.Parse(v));
    };

    [Fact]
    public void ObjectId_can_deserialize_from_string()
    {
        var expectedId = ObjectId.GenerateNewId();
        var expected = new IdIsString
        {
            _id = expectedId.ToString()
        };
        tempDatabase.CreateTemporaryCollection<IdIsString>().InsertOne(expected);

        var collection = GetCollection<IdIsObjectId>();
        var db = SingleEntityDbContext.Create(collection, ClrObjectIdToMongoString);

        var found = db.Entities.First();
        Assert.Equal(expectedId, found._id);
    }

    [Fact]
    public void ObjectId_can_query_against_string()
    {
        var expectedId = ObjectId.GenerateNewId();
        var expected = new IdIsString
        {
            _id = expectedId.ToString()
        };
        tempDatabase.CreateTemporaryCollection<IdIsString>().InsertOne(expected);

        var collection = GetCollection<IdIsObjectId>();
        var db = SingleEntityDbContext.Create(collection, ClrObjectIdToMongoString);

        var found = db.Entities.First(e => e._id == expectedId);
        Assert.Equal(expectedId, found._id);
    }

    [Fact]
    public void ObjectId_can_serialize_to_string()
    {
        var collection = tempDatabase.CreateTemporaryCollection<IdIsObjectId>();
        var db = SingleEntityDbContext.Create(collection, ClrObjectIdToMongoString);

        var original = new IdIsObjectId();
        db.Entities.Add(original);
        db.SaveChanges();

        var found = GetCollection<IdIsString>().AsQueryable().First();
        Assert.Equal(original._id.ToString(), found._id);
    }

    private static readonly Action<ModelBuilder>? ClrStringToMongoObjectId = mb =>
    {
        mb.Entity<IdIsString>()
            .Property(e => e._id).HasConversion(v => ObjectId.Parse(v), v => v.ToString());
    };

    [Fact]
    public void String_can_deserialize_from_ObjectId()
    {
        var expected = new IdIsObjectId
        {
            _id = ObjectId.GenerateNewId()
        };
        tempDatabase.CreateTemporaryCollection<IdIsObjectId>().InsertOne(expected);

        var collection = GetCollection<IdIsString>();
        var db = SingleEntityDbContext.Create(collection, ClrStringToMongoObjectId);

        var found = db.Entities.First();
        Assert.Equal(expected._id.ToString(), found._id);
    }

    [Fact]
    public void String_can_query_against_ObjectId()
    {
        var expected = new IdIsObjectId
        {
            _id = ObjectId.GenerateNewId()
        };
        tempDatabase.CreateTemporaryCollection<IdIsObjectId>().InsertOne(expected);

        var collection = GetCollection<IdIsString>();
        var db = SingleEntityDbContext.Create(collection, ClrStringToMongoObjectId);

        var found = db.Entities.First(e => e._id == expected._id.ToString());
        Assert.Equal(expected._id.ToString(), found._id);
    }

    [Fact]
    public void String_can_serialize_to_ObjectId()
    {
        var collection = tempDatabase.CreateTemporaryCollection<IdIsString>();
        var db = SingleEntityDbContext.Create(collection, ClrStringToMongoObjectId);

        var original = new IdIsString
        {
            _id = ObjectId.GenerateNewId().ToString()
        };
        db.Entities.Add(original);
        db.SaveChanges();

        var found = GetCollection<IdIsObjectId>().AsQueryable().First();
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

    private static readonly Action<ModelBuilder>? ClrBoolToMongoString = mb =>
    {
        mb.Entity<ActiveIsBool>()
            .Property(e => e.active).HasConversion(v => v.ToString(), v => bool.Parse(v));
    };

    [Theory]
    [InlineData([true])]
    [InlineData([false])]
    public void Bool_can_deserialize_from_string(bool active)
    {
        var expected = new ActiveIsString
        {
            active = active.ToString(), _id = ObjectId.GenerateNewId()
        };
        var docs = tempDatabase.CreateTemporaryCollection<ActiveIsString>(nameof(Bool_can_deserialize_from_string) + "_" + active);
        docs.InsertOne(expected);

        var collection = GetCollection<ActiveIsBool>(docs.CollectionNamespace.CollectionName);
        var db = SingleEntityDbContext.Create(collection, ClrBoolToMongoString);

        var found = db.Entities.First();
        Assert.Equal(expected._id, found._id);
        Assert.Equal(active, found.active);
    }

    [Theory(Skip = "Currently not able to map CLR bool in C# Driver to non-bool types when querying")]
    [InlineData([true])]
    [InlineData([false])]
    public void Bool_can_query_against_string(bool active)
    {
        var expected = new ActiveIsString
        {
            active = active.ToString(), _id = ObjectId.GenerateNewId()
        };
        var docs = tempDatabase.CreateTemporaryCollection<ActiveIsString>(nameof(Bool_can_query_against_string) + "_" + active);
        docs.InsertOne(expected);

        var collection = GetCollection<ActiveIsBool>(docs.CollectionNamespace.CollectionName);
        var db = SingleEntityDbContext.Create(collection, ClrBoolToMongoString);

        var found = db.Entities.First(e => e.active == active);
        Assert.Equal(expected._id, found._id);
        Assert.Equal(active, found.active);
    }

    [Theory]
    [InlineData([true])]
    [InlineData([false])]
    public void Bool_can_serialize_to_string(bool active)
    {
        var collection = tempDatabase.CreateTemporaryCollection<ActiveIsBool>(nameof(Bool_can_serialize_to_string) + "_" + active);
        var db = SingleEntityDbContext.Create(collection, ClrBoolToMongoString);

        var original = new ActiveIsBool
        {
            active = active
        };
        db.Entities.Add(original);
        db.SaveChanges();

        var found = GetCollection<ActiveIsString>(collection.CollectionNamespace.CollectionName).AsQueryable().First();
        Assert.Equal(original._id, found._id);
        Assert.Equal(active.ToString(), found.active);
    }

    class DaysIsInt : IdIsObjectId
    {
        public int days { get; set; }
    }

    class DaysIsString : IdIsObjectId
    {
        public string days { get; set; }
    }

    private static readonly Action<ModelBuilder>? ClrIntToMongoString = mb =>
    {
        mb.Entity<DaysIsInt>()
            .Property(e => e.days).HasConversion(v => v.ToString(), v => int.Parse(v));
    };

    [Theory]
    [InlineData([1])]
    [InlineData([-123])]
    [InlineData([0])]
    public void Int_can_deserialize_from_string(int days)
    {
        var expected = new DaysIsString
        {
            days = days.ToString(), _id = ObjectId.GenerateNewId()
        };
        var docs = tempDatabase.CreateTemporaryCollection<DaysIsString>(nameof(Int_can_deserialize_from_string) + "_" + days);
        docs.InsertOne(expected);

        var collection = GetCollection<DaysIsInt>(docs.CollectionNamespace.CollectionName);
        var db = SingleEntityDbContext.Create(collection, ClrIntToMongoString);

        var found = db.Entities.First();
        Assert.Equal(expected._id, found._id);
        Assert.Equal(days, found.days);
    }

    [Theory]
    [InlineData([1])]
    [InlineData([-123])]
    [InlineData([0])]
    public void Int_can_query_against_string(int days)
    {
        var expected = new DaysIsString
        {
            days = days.ToString(), _id = ObjectId.GenerateNewId()
        };
        var docs = tempDatabase.CreateTemporaryCollection<DaysIsString>(nameof(Int_can_query_against_string) + "_" + days);
        docs.InsertOne(expected);

        var collection = GetCollection<DaysIsInt>(docs.CollectionNamespace.CollectionName);
        var db = SingleEntityDbContext.Create(collection, ClrIntToMongoString);

        var found = db.Entities.First(e => e.days == days);
        Assert.Equal(expected._id, found._id);
        Assert.Equal(days, found.days);
    }

    [Theory]
    [InlineData([1])]
    [InlineData([-123])]
    [InlineData([0])]
    public void Int_can_serialize_to_string(int days)
    {
        var collection = tempDatabase.CreateTemporaryCollection<DaysIsInt>(nameof(Int_can_serialize_to_string) + "_" + days);
        var db = SingleEntityDbContext.Create(collection, ClrIntToMongoString);

        var original = new DaysIsInt
        {
            days = days
        };
        db.Entities.Add(original);
        db.SaveChanges();

        var found = GetCollection<DaysIsString>(collection.CollectionNamespace.CollectionName).AsQueryable().First();
        Assert.Equal(original._id, found._id);
        Assert.Equal(days.ToString(), found.days);
    }

    class DaysIsTimeSpan : IdIsObjectId
    {
        public TimeSpan days { get; set; }
    }

    private static readonly Action<ModelBuilder>? ClrTimeSpanToMongoInt = mb =>
    {
        mb.Entity<DaysIsTimeSpan>()
            .Property(e => e.days).HasConversion(v => v.TotalDays, v => TimeSpan.FromDays(v));
    };

    [Theory]
    [InlineData([1633])]
    [InlineData([-123])]
    [InlineData([0])]
    public void TimeSpan_can_deserialize_from_int(int days)
    {
        var expected = new DaysIsInt
        {
            days = days, _id = ObjectId.GenerateNewId()
        };
        var docs = tempDatabase.CreateTemporaryCollection<DaysIsInt>(nameof(TimeSpan_can_deserialize_from_int) + "_" + days);
        docs.InsertOne(expected);

        var collection = GetCollection<DaysIsTimeSpan>(docs.CollectionNamespace.CollectionName);
        var db = SingleEntityDbContext.Create(collection, ClrTimeSpanToMongoInt);

        var found = db.Entities.First();
        Assert.Equal(expected._id, found._id);
        Assert.Equal(days, found.days.TotalDays);
    }

    [Theory]
    [InlineData([1634])]
    [InlineData([-123])]
    [InlineData([0])]
    public void TimeSpan_can_query_against_int(int days)
    {
        var expected = new DaysIsInt
        {
            days = days, _id = ObjectId.GenerateNewId()
        };
        var docs = tempDatabase.CreateTemporaryCollection<DaysIsInt>(nameof(TimeSpan_can_query_against_int) + "_" + days);
        docs.InsertOne(expected);

        var collection = GetCollection<DaysIsInt>(docs.CollectionNamespace.CollectionName);
        var db = SingleEntityDbContext.Create(collection, ClrTimeSpanToMongoInt);

        var found = db.Entities.First(e => e.days == days);
        Assert.Equal(expected._id, found._id);
        Assert.Equal(days, found.days);
    }

    [Theory]
    [InlineData([1024])]
    [InlineData([-123])]
    [InlineData([0])]
    public void TimeSpan_can_serialize_to_int(int days)
    {
        var collection = tempDatabase.CreateTemporaryCollection<DaysIsTimeSpan>(nameof(TimeSpan_can_serialize_to_int) + "_" + days);
        var db = SingleEntityDbContext.Create(collection, ClrTimeSpanToMongoInt);

        var original = new DaysIsTimeSpan
        {
            days = TimeSpan.FromDays(days)
        };
        db.Entities.Add(original);
        db.SaveChanges();

        var found = GetCollection<DaysIsInt>(collection.CollectionNamespace.CollectionName).AsQueryable().First();
        Assert.Equal(original._id, found._id);
        Assert.Equal(days, found.days);
    }

    private static readonly Action<ModelBuilder>? ClrStringToMongoInt = mb =>
    {
        mb.Entity<DaysIsString>()
            .Property(e => e.days).HasConversion(v => int.Parse(v), v => v.ToString());
    };

    [Theory]
    [InlineData([1])]
    [InlineData([-123])]
    [InlineData([0])]
    public void String_can_deserialize_from_int(int days)
    {
        var expected = new DaysIsInt
        {
            days = days, _id = ObjectId.GenerateNewId()
        };
        var docs = tempDatabase.CreateTemporaryCollection<DaysIsInt>(nameof(String_can_deserialize_from_int) + "_" + days);
        docs.InsertOne(expected);

        var collection = GetCollection<DaysIsString>(docs.CollectionNamespace.CollectionName);
        var db = SingleEntityDbContext.Create(collection, ClrStringToMongoInt);

        var found = db.Entities.First();
        Assert.Equal(expected._id, found._id);
        Assert.Equal(days.ToString(), found.days);
    }

    [Theory]
    [InlineData([1])]
    [InlineData([-123])]
    [InlineData([0])]
    public void String_can_deserialize_from_int_default(int days)
    {
        var expected = new DaysIsInt
        {
            days = days, _id = ObjectId.GenerateNewId()
        };
        var docs = tempDatabase.CreateTemporaryCollection<DaysIsInt>(nameof(String_can_deserialize_from_int_default)
                                                                     + "_" + days);
        docs.InsertOne(expected);

        var collection = GetCollection<DaysIsString>(docs.CollectionNamespace.CollectionName);
        var db = SingleEntityDbContext.Create(collection, mb =>
        {
            mb.Entity<DaysIsString>().Property(d => d.days).HasConversion<int>();
        });

        var found = db.Entities.First();
        Assert.Equal(expected._id, found._id);
        Assert.Equal(days.ToString(), found.days);
    }

    [Theory]
    [InlineData([1])]
    [InlineData([-123])]
    [InlineData([0])]
    public void String_can_query_against_int(int days)
    {
        var expected = new DaysIsInt
        {
            days = days, _id = ObjectId.GenerateNewId()
        };
        var docs = tempDatabase.CreateTemporaryCollection<DaysIsInt>(nameof(String_can_query_against_int) + "_" + days);
        docs.InsertOne(expected);

        var collection = GetCollection<DaysIsString>(docs.CollectionNamespace.CollectionName);
        var db = SingleEntityDbContext.Create(collection, ClrStringToMongoInt);

        var found = db.Entities.First(e => e.days == days.ToString());
        Assert.Equal(expected._id, found._id);
        Assert.Equal(days.ToString(), found.days);
    }

    [Theory]
    [InlineData([1])]
    [InlineData([-123])]
    [InlineData([0])]
    public void String_can_serialize_to_int(int days)
    {
        var collection = tempDatabase.CreateTemporaryCollection<DaysIsString>(nameof(String_can_serialize_to_int) + "_" + days);
        var db = SingleEntityDbContext.Create(collection, ClrStringToMongoInt);

        var original = new DaysIsString
        {
            days = days.ToString()
        };
        db.Entities.Add(original);
        db.SaveChanges();

        var found = GetCollection<DaysIsInt>(collection.CollectionNamespace.CollectionName).AsQueryable().First();
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

    private static readonly Action<ModelBuilder>? ClrDecimalToMongoDecimal128 = mb =>
    {
        mb.Entity<AmountIsDecimal>()
            .Property(e => e.amount).HasConversion(v => new Decimal128(v), v => Decimal128.ToDecimal(v));
    };

    [Theory]
    [InlineData(["1.1234"])]
    [InlineData(["-123.213"])]
    [InlineData(["0"])]
    public void Decimal_can_deserialize_from_Decimal128(string amountString)
    {
        var amount = decimal.Parse(amountString);
        var expected = new AmountIsDecimal128
        {
            amount = new Decimal128(amount), _id = ObjectId.GenerateNewId()
        };
        var docs = tempDatabase.CreateTemporaryCollection<AmountIsDecimal128>(nameof(Decimal_can_deserialize_from_Decimal128) + "_"
            + amount);
        docs.InsertOne(expected);

        var collection = GetCollection<AmountIsDecimal>(docs.CollectionNamespace.CollectionName);
        var db = SingleEntityDbContext.Create(collection, ClrDecimalToMongoDecimal128);

        var found = db.Entities.First();
        Assert.Equal(expected._id, found._id);
        Assert.Equal(amount, found.amount);
    }

    [Theory]
    [InlineData(["1.1234"])]
    [InlineData(["-123.213"])]
    [InlineData(["0"])]
    public void Decimal_can_query_against_Decimal128(string amountString)
    {
        var amount = decimal.Parse(amountString);
        var expected = new AmountIsDecimal128
        {
            amount = new Decimal128(amount), _id = ObjectId.GenerateNewId()
        };
        var docs = tempDatabase.CreateTemporaryCollection<AmountIsDecimal128>(nameof(Decimal_can_query_against_Decimal128) + "_"
            + amount);
        docs.InsertOne(expected);

        var collection = GetCollection<AmountIsDecimal>(docs.CollectionNamespace.CollectionName);
        var db = SingleEntityDbContext.Create(collection, ClrDecimalToMongoDecimal128);

        var found = db.Entities.First(e => e.amount == amount);
        Assert.Equal(expected._id, found._id);
        Assert.Equal(amount, found.amount);
    }

    [Theory]
    [InlineData(["1.1234"])]
    [InlineData(["-123.213"])]
    [InlineData(["0"])]
    public void Decimal_can_serialize_to_Decimal128(string amountString)
    {
        var amount = decimal.Parse(amountString);
        var collection =
            tempDatabase.CreateTemporaryCollection<AmountIsDecimal>(nameof(Decimal_can_serialize_to_Decimal128) + "_" + amount);
        var db = SingleEntityDbContext.Create(collection, ClrDecimalToMongoDecimal128);

        var original = new AmountIsDecimal
        {
            amount = amount
        };
        db.Entities.Add(original);
        db.SaveChanges();

        var found = GetCollection<AmountIsDecimal128>(collection.CollectionNamespace.CollectionName).AsQueryable().First();
        Assert.Equal(original._id, found._id);
        Assert.Equal(amount, found.amount);
    }

    class AmountIsString : IdIsObjectId
    {
        public string amount { get; set; }
    }

    private static readonly Action<ModelBuilder> ClrDecimalToString = mb =>
    {
        mb.Entity<AmountIsString>()
            .Property(e => e.amount).HasConversion(v => decimal.Parse(v), v => v.ToString());
    };

    [Theory]
    [InlineData(["1.1234"])]
    [InlineData(["-123.213"])]
    [InlineData(["0"])]
    public void Decimal_can_deserialize_from_string(string amount)
    {
        var expected = new AmountIsString
        {
            amount = amount, _id = ObjectId.GenerateNewId()
        };
        var docs = tempDatabase.CreateTemporaryCollection<AmountIsString>(
            nameof(Decimal_can_deserialize_from_string) + "_" + amount);
        docs.InsertOne(expected);

        var collection = GetCollection<AmountIsDecimal>(docs.CollectionNamespace.CollectionName);
        var db = SingleEntityDbContext.Create(collection, ClrDecimalToString);

        var found = db.Entities.First();
        Assert.Equal(expected._id, found._id);
        Assert.Equal(amount, found.amount.ToString());
    }

    [Theory]
    [InlineData(["1.1234"])]
    [InlineData(["-123.213"])]
    [InlineData(["0"])]
    public void Decimal_can_query_against_string(string amount)
    {
        var expected = new AmountIsString
        {
            amount = amount, _id = ObjectId.GenerateNewId()
        };
        var docs = tempDatabase.CreateTemporaryCollection<AmountIsString>(nameof(Decimal_can_query_against_string) + "_" + amount);
        docs.InsertOne(expected);

        var collection = GetCollection<AmountIsDecimal>(docs.CollectionNamespace.CollectionName);
        var db = SingleEntityDbContext.Create(collection, ClrDecimalToString);

        var found = db.Entities.First(e => e.amount == decimal.Parse(amount));
        Assert.Equal(expected._id, found._id);
        Assert.Equal(amount, found.amount.ToString());
    }

    [Theory]
    [InlineData(["1.1234"])]
    [InlineData(["-123.213"])]
    [InlineData(["0"])]
    public void Decimal_can_serialize_to_string(string amount)
    {
        var collection =
            tempDatabase.CreateTemporaryCollection<AmountIsDecimal>(nameof(Decimal_can_serialize_to_string) + "_" + amount);
        var db = SingleEntityDbContext.Create(collection, ClrDecimalToString);

        var original = new AmountIsDecimal
        {
            amount = decimal.Parse(amount)
        };
        db.Entities.Add(original);
        db.SaveChanges();

        var found = GetCollection<AmountIsString>(collection.CollectionNamespace.CollectionName).AsQueryable().First();
        Assert.Equal(original._id, found._id);
        Assert.Equal(amount, found.amount);
    }

    IMongoCollection<T> GetCollection<T>([CallerMemberName] string? name = null)
        => tempDatabase.MongoDatabase.GetCollection<T>(name);

    class AmountIsDouble : IdIsObjectId
    {
        public double amount { get; set; }
    }

    [Theory]
    [InlineData(["1.1234"])]
    [InlineData(["-123.213"])]
    [InlineData(["0"])]
    public void Double_can_deserialize_from_string_default(string amount)
    {
        var expected = new AmountIsString
        {
            amount = amount, _id = ObjectId.GenerateNewId()
        };
        var docs = tempDatabase.CreateTemporaryCollection<AmountIsString>(
            nameof(Double_can_deserialize_from_string_default) + "_" + amount);
        docs.InsertOne(expected);

        var collection = GetCollection<AmountIsDouble>(docs.CollectionNamespace.CollectionName);
        var db = SingleEntityDbContext.Create(collection, mb =>
        {
            mb.Entity<AmountIsDouble>().Property(e => e.amount).HasConversion<string>();
        });

        var found = db.Entities.First();
        Assert.Equal(expected._id, found._id);
        Assert.Equal(amount, found.amount.ToString());
    }

    [Theory]
    [InlineData(["1.1234"])]
    [InlineData(["-123.213"])]
    [InlineData(["0"])]
    public void Double_can_query_against_string_default(string amount)
    {
        var expected = new AmountIsString
        {
            amount = amount, _id = ObjectId.GenerateNewId()
        };
        var docs = tempDatabase.CreateTemporaryCollection<AmountIsString>(
            nameof(Double_can_query_against_string_default) + "_" + amount);
        docs.InsertOne(expected);

        var collection = GetCollection<AmountIsDouble>(docs.CollectionNamespace.CollectionName);
        var db = SingleEntityDbContext.Create(collection, mb =>
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
            tempDatabase.CreateTemporaryCollection<AmountIsDouble>(nameof(Double_can_serialize_to_string_default)
                                                                   + "_" + amount);
        var db = SingleEntityDbContext.Create(collection, mb =>
        {
            mb.Entity<AmountIsDouble>().Property(e => e.amount).HasConversion<string>();
        });

        var original = new AmountIsDouble
        {
            amount = amount
        };
        db.Entities.Add(original);
        db.SaveChanges();

        var found = GetCollection<AmountIsString>(collection.CollectionNamespace.CollectionName).AsQueryable().First();
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
    public void Guid_can_deserialize_from_string_default(string amount)
    {
        var expected = new AmountIsString
        {
            amount = amount, _id = ObjectId.GenerateNewId()
        };
        var docs = tempDatabase.CreateTemporaryCollection<AmountIsString>(
            nameof(Guid_can_deserialize_from_string_default) + "_" + amount.Substring(6));
        docs.InsertOne(expected);

        var collection = GetCollection<AmountIsGuid>(docs.CollectionNamespace.CollectionName);
        var db = SingleEntityDbContext.Create(collection, mb =>
        {
            mb.Entity<AmountIsGuid>().Property(e => e.amount).HasConversion<string>();
        });

        var found = db.Entities.First();
        Assert.Equal(expected._id, found._id);
        Assert.Equal(amount, found.amount.ToString());
    }

    [Theory]
    [InlineData(["6ec635e0-06e0-11ef-93e0-325096b39f47"])]
    [InlineData(["380bb5de-fb71-4f6d-a349-2b83908ab43b"])]
    [InlineData(["018f2ea7-a7a7-7c33-bd63-c6b1b1d5ecff"])]
    [InlineData(["00000000-0000-0000-0000-000000000000"])]
    public void Guid_can_query_against_string_default(string amount)
    {
        var expected = new AmountIsString
        {
            amount = amount, _id = ObjectId.GenerateNewId()
        };
        var docs = tempDatabase.CreateTemporaryCollection<AmountIsString>(
            nameof(Guid_can_query_against_string_default) + "_" + amount.Substring(6));
        docs.InsertOne(expected);

        var collection = GetCollection<AmountIsGuid>(docs.CollectionNamespace.CollectionName);
        var db = SingleEntityDbContext.Create(collection, mb =>
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
            tempDatabase.CreateTemporaryCollection<AmountIsGuid>(nameof(Guid_can_serialize_to_string_default) + "_"
                + amountString.Substring(6));
        var db = SingleEntityDbContext.Create(collection, mb =>
        {
            mb.Entity<AmountIsGuid>().Property(e => e.amount).HasConversion<string>();
        });

        var original = new AmountIsGuid
        {
            amount = amount
        };
        db.Entities.Add(original);
        db.SaveChanges();

        var found = GetCollection<AmountIsString>(collection.CollectionNamespace.CollectionName).AsQueryable().First();
        Assert.Equal(original._id, found._id);
        Assert.Equal(amount.ToString(), found.amount);
    }

    [Theory]
    [InlineData(["507f1f77bcf86cd799439011"])]
    [InlineData(["507f191e810c19729de860ea"])]
    public void String_can_deserialize_from_ObjectId_default(string id)
    {
        var docs = tempDatabase.CreateTemporaryCollection<IdIsObjectId>(
            nameof(String_can_deserialize_from_ObjectId_default) + "_" + id.Substring(6));
        docs.InsertOne(new IdIsObjectId
        {
            _id = ObjectId.Parse(id)
        });

        var collection = GetCollection<IdIsString>(docs.CollectionNamespace.CollectionName);
        var db = SingleEntityDbContext.Create(collection, mb =>
        {
            mb.Entity<IdIsString>().Property(e => e._id).HasConversion<ObjectId>();
        });

        var found = db.Entities.First();
        Assert.Equal(id, found._id);
    }

    [Theory]
    [InlineData(["507f1f77bcf86cd799439011"])]
    [InlineData(["507f191e810c19729de860ea"])]
    public void String_can_query_against_ObjectId_default(string id)
    {
        var docs = tempDatabase.CreateTemporaryCollection<IdIsObjectId>(
            nameof(String_can_query_against_ObjectId_default) + "_" + id.Substring(6));
        docs.InsertOne(new IdIsObjectId
        {
            _id = ObjectId.Parse(id)
        });

        var collection = GetCollection<IdIsString>(docs.CollectionNamespace.CollectionName);
        var db = SingleEntityDbContext.Create(collection, mb =>
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
        var docs = tempDatabase.CreateTemporaryCollection<IdIsString>(
            nameof(String_can_serialize_to_ObjectId_default) + "_" + id.Substring(6));
        var db = SingleEntityDbContext.Create(docs, mb =>
        {
            mb.Entity<IdIsString>().Property(e => e._id).HasConversion<ObjectId>();
        });
        var original = new IdIsString
        {
            _id = id
        };
        db.Entities.Add(original);
        db.SaveChanges();

        var found = GetCollection<IdIsObjectId>(docs.CollectionNamespace.CollectionName).AsQueryable().First();
        Assert.Equal(original._id, found._id.ToString());
    }

    [Theory]
    [InlineData(["507f1f77bcf86cd799439011"])]
    [InlineData(["507f191e810c19729de860ea"])]
    public void ObjectId_can_deserialize_from_string_default(string id)
    {
        var docs = tempDatabase.CreateTemporaryCollection<IdIsString>(
            nameof(ObjectId_can_deserialize_from_string_default) + "_" + id.Substring(6));
        docs.InsertOne(new IdIsString
        {
            _id = id
        });

        var collection = GetCollection<IdIsObjectId>(docs.CollectionNamespace.CollectionName);
        var db = SingleEntityDbContext.Create(collection, mb =>
        {
            mb.Entity<IdIsObjectId>().Property(e => e._id).HasConversion<string>();
        });

        var found = db.Entities.First();
        Assert.Equal(id, found._id.ToString());
    }
}
