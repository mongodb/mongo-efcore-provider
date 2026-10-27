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
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Infrastructure;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

public class StoredValueConversionTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    public class Item
    {
        public ObjectId Id { get; set; }
        public int Seq { get; set; }
        public int Group { get; set; }
        public int Price { get; set; }
        public long Big { get; set; }
        public double Dbl { get; set; }
        public decimal Dec { get; set; }
        public int? NPrice { get; set; }
    }

    public enum Storage
    {
        Representation,
        Converter
    }

    // These tests exercise the driver-LINQ bridge's $convert read, which NativeOnly forbids: the native translator declines these shapes.
    private static void PinDefaultQueryMode(DbContextOptionsBuilder optionsBuilder)
        => new MongoDbContextOptionsBuilder(optionsBuilder).UseQueryMode(MongoQueryMode.Native);

    private SingleEntityDbContext<Item> CreateDb(Storage storage, string name)
    {
        var db = SingleEntityDbContext.Create(
            database.CreateCollection<Item>(TemporaryDatabaseFixtureBase.CreateCollectionName($"{name}_{storage}")),
            mb =>
            {
                var e = mb.Entity<Item>();
                if (storage == Storage.Representation)
                {
                    e.Property(i => i.Price).HasBsonRepresentation(BsonType.String);
                    e.Property(i => i.Big).HasBsonRepresentation(BsonType.String);
                    e.Property(i => i.Dbl).HasBsonRepresentation(BsonType.String);
                    e.Property(i => i.Dec).HasBsonRepresentation(BsonType.String);
                    e.Property(i => i.NPrice).HasBsonRepresentation(BsonType.String);
                }
                else
                {
                    e.Property(i => i.Price).HasConversion<string>();
                    e.Property(i => i.Big).HasConversion<string>();
                    e.Property(i => i.Dbl).HasConversion<string>();
                    e.Property(i => i.Dec).HasConversion<string>();
                    e.Property(i => i.NPrice).HasConversion<string>();
                }
            },
            optionsBuilderAction: PinDefaultQueryMode);
        db.Entities.AddRange(
            new Item { Seq = 1, Group = 1, Price = 9, Big = 9, Dbl = 9, Dec = 9, NPrice = 9 },
            new Item { Seq = 2, Group = 1, Price = 10, Big = 10, Dbl = 10, Dec = 10, NPrice = null },
            new Item { Seq = 3, Group = 2, Price = 100, Big = 100, Dbl = 100, Dec = 100, NPrice = 100 });
        db.SaveChanges();
        return db;
    }

    private IMongoCollection<BsonDocument> RawCollection(Storage storage, string name)
        => database.GetCollection<BsonDocument>(TemporaryDatabaseFixtureBase.CreateCollectionName($"{name}_{storage}"));

    public static TheoryData<Storage> Storages => new() { Storage.Representation, Storage.Converter };

    [Theory, MemberData(nameof(Storages))]
    public void Sum(Storage s)
    {
        var db = CreateDb(s, nameof(Sum));
        Assert.Equal(119, db.Entities.Sum(e => e.Price));
        Assert.Equal(119L, db.Entities.Sum(e => e.Big));
        Assert.Equal(119.0, db.Entities.Sum(e => e.Dbl));
        Assert.Equal(119m, db.Entities.Sum(e => e.Dec));
        Assert.Equal(109, db.Entities.Sum(e => e.NPrice));
    }

    [Theory, MemberData(nameof(Storages))]
    public void Min_Max(Storage s)
    {
        var db = CreateDb(s, nameof(Min_Max));
        Assert.Equal(100, db.Entities.Max(e => e.Price));
        Assert.Equal(9, db.Entities.Min(e => e.Price));
        Assert.Equal(100L, db.Entities.Max(e => e.Big));
        Assert.Equal(9.0, db.Entities.Min(e => e.Dbl));
        Assert.Equal(100m, db.Entities.Max(e => e.Dec));
        Assert.Equal(100, db.Entities.Max(e => e.NPrice));
    }

    [Theory, MemberData(nameof(Storages))]
    public void Average(Storage s)
    {
        var db = CreateDb(s, nameof(Average));
        Assert.Equal(119.0 / 3, db.Entities.Average(e => e.Price), 6);
        Assert.Equal(54.5, db.Entities.Average(e => e.NPrice)!.Value, 6);
    }

    [Theory, MemberData(nameof(Storages))]
    public void Where_relational(Storage s)
    {
        var db = CreateDb(s, nameof(Where_relational));
        Assert.Equal([3], db.Entities.Where(e => e.Price > 50).Select(e => e.Seq).ToList());
        Assert.Equal([3], db.Entities.Where(e => e.Big > 50).Select(e => e.Seq).ToList());
        Assert.Equal([3], db.Entities.Where(e => e.Dbl >= 50).Select(e => e.Seq).ToList());
        Assert.Equal([3], db.Entities.Where(e => e.Dec > 50).Select(e => e.Seq).ToList());
        Assert.Equal([3], db.Entities.Where(e => e.NPrice > 50).Select(e => e.Seq).ToList());
        var limit = 50;
        Assert.Equal([1, 2], db.Entities.Where(e => e.Price < limit).OrderBy(e => e.Seq).Select(e => e.Seq).ToList());
    }

    [Theory, MemberData(nameof(Storages))]
    public void OrderBy(Storage s)
    {
        var db = CreateDb(s, nameof(OrderBy));
        Assert.Equal([1, 2, 3], db.Entities.OrderBy(e => e.Price).Select(e => e.Seq).ToList());
        Assert.Equal([3, 2, 1], db.Entities.OrderByDescending(e => e.Dec).Select(e => e.Seq).ToList());
        Assert.Equal([1, 2, 3], db.Entities.OrderBy(e => e.Group).ThenBy(e => e.Price).Select(e => e.Seq).ToList());
    }

    [Theory, MemberData(nameof(Storages))]
    public void Equality_and_projection_still_work(Storage s)
    {
        var db = CreateDb(s, nameof(Equality_and_projection_still_work));
        Assert.Equal([2], db.Entities.Where(e => e.Price == 10).Select(e => e.Seq).ToList());
        var captured = 10m;
        Assert.Equal([2], db.Entities.Where(e => e.Dec == captured).Select(e => e.Seq).ToList());
        Assert.Equal([2], db.Entities.Where(e => e.Dec.Equals(captured)).Select(e => e.Seq).ToList());
        Assert.Equal(2, db.Entities.First(e => e.Dec.Equals(captured)).Seq);
        object boxed = 10m;
        Assert.Equal([2], db.Entities.Where(e => e.Dec.Equals(boxed)).Select(e => e.Seq).ToList());
        Assert.Equal([2], db.Entities.Where(e => e.Dec == 10m).Select(e => e.Seq).ToList());
        Assert.Equal([2], db.Entities.Where(e => e.Dec.Equals(10m)).Select(e => e.Seq).ToList());
        Assert.Equal(2, db.Entities.First(e => e.Dec.Equals(10m)).Seq);
        Assert.Equal([2], db.Entities.Where(e => e.Big == 10L).Select(e => e.Seq).ToList());
        Assert.Equal([2], db.Entities.Where(e => e.Dbl == 10.0).Select(e => e.Seq).ToList());
        Assert.Equal([1, 3], db.Entities.Where(e => e.Price != 10).OrderBy(e => e.Seq).Select(e => e.Seq).ToList());
        Assert.Equal([2], db.Entities.Where(e => e.NPrice == null).Select(e => e.Seq).ToList());
        Assert.Equal([9, 10, 100], db.Entities.OrderBy(e => e.Seq).Select(e => e.Price).ToList());
        Assert.Equal([9, null, 100], db.Entities.OrderBy(e => e.Seq).Select(e => e.NPrice).ToList());
        Assert.Equal(3, db.Entities.Count());
        Assert.Equal(9, db.Entities.OrderBy(e => e.Seq).First().Price);
    }

    [Theory, MemberData(nameof(Storages))]
    public void Equality_and_range_mixed_in_one_predicate(Storage s)
    {
        var db = CreateDb(s, nameof(Equality_and_range_mixed_in_one_predicate));
        Assert.Equal([3], db.Entities.Where(e => e.Price != 10 && e.Price > 50).Select(e => e.Seq).ToList());
        Assert.Equal([2], db.Entities.Where(e => e.Price == 10 && e.Price < 50).Select(e => e.Seq).ToList());
    }

    [Theory, MemberData(nameof(Storages))]
    public void Decimal_extremes_equality(Storage s)
    {
        var db = CreateDb(s, nameof(Decimal_extremes_equality));
        db.Entities.Add(new Item { Seq = 4, Group = 3, Dec = decimal.MaxValue });
        db.SaveChanges();
        var max = decimal.MaxValue;
        Assert.Equal([4], db.Entities.Where(e => e.Dec == max).Select(e => e.Seq).ToList());
        Assert.Equal([4], db.Entities.Where(e => e.Dec.Equals(max)).Select(e => e.Seq).ToList());
    }

    [Theory, MemberData(nameof(Storages))]
    public void Contains_keeps_working(Storage s)
    {
        var db = CreateDb(s, nameof(Contains_keeps_working));
        var wanted = new[] { 10, 100 };
        Assert.Equal([2, 3], db.Entities.Where(e => wanted.Contains(e.Price)).OrderBy(e => e.Seq).Select(e => e.Seq).ToList());
    }

    [Theory, MemberData(nameof(Storages))]
    public void Join_on_converted_key_keeps_working(Storage s)
    {
        var db = CreateDb(s, nameof(Join_on_converted_key_keeps_working));
        var pairs = db.Entities.Join(db.Entities, a => a.Price, b => b.Price, (a, b) => new { A = a.Seq, B = b.Seq })
            .OrderBy(p => p.A).ToList();
        Assert.Equal([1, 2, 3], pairs.Select(p => p.A));
        Assert.Equal([1, 2, 3], pairs.Select(p => p.B));
    }

    [Theory, MemberData(nameof(Storages))]
    public void Equality_and_aggregate_over_the_same_property(Storage s)
    {
        var db = CreateDb(s, nameof(Equality_and_aggregate_over_the_same_property));
        Assert.Equal(10, db.Entities.Where(e => e.Price == 10).Sum(e => e.Price));
        Assert.Equal(100, db.Entities.Where(e => e.Price != 9).OrderBy(e => e.Price).Select(e => e.Price).Last());
        Assert.Equal(2, db.Entities.Where(e => e.Price > 5 && e.Price != 100).Count());
    }

    [Theory, MemberData(nameof(Storages))]
    public void Key_free_Find_by_value_and_FirstOrDefault(Storage s)
    {
        var db = CreateDb(s, nameof(Key_free_Find_by_value_and_FirstOrDefault));
        Assert.Equal(3, db.Entities.FirstOrDefault(e => e.Price > 50)!.Seq);
        Assert.Equal(1, db.Entities.OrderBy(e => e.Price).First().Seq);
    }

    [Fact]
    public void Non_numeric_stored_string_fails_the_query_loudly()
    {
        var collection = database.CreateCollection<Item>(nameof(Non_numeric_stored_string_fails_the_query_loudly));
        collection.InsertMany(
        [
            new Item { Seq = 1, Price = 9 },
        ]);
        var raw = database.GetCollection<BsonDocument>(nameof(Non_numeric_stored_string_fails_the_query_loudly));
        raw.InsertOne(new BsonDocument { { "Seq", 2 }, { "Group", 1 }, { "Price", "abc" } });

        using var db = SingleEntityDbContext.Create(
            collection, mb => mb.Entity<Item>().Property(e => e.Price).HasBsonRepresentation(BsonType.String),
            optionsBuilderAction: PinDefaultQueryMode);

        // Documented behaviour (BREAKING-CHANGES.md): a stored value that is not a number fails range/sort/aggregate
        // queries rather than being silently misordered or ignored. Equality compares the stored form and is unaffected.
        Assert.ThrowsAny<Exception>(() => db.Entities.Where(e => e.Price > 5).ToList());
        Assert.ThrowsAny<Exception>(() => db.Entities.Max(e => e.Price));
        Assert.Equal([2], db.Entities.Where(e => e.Price == 9 || e.Seq == 2).Select(e => e.Seq).OrderBy(x => x).Skip(0).Take(1).ToList());
    }

    [Fact]
    public void Converter_with_string_representation_is_converted()
    {
        var db = SingleEntityDbContext.Create(
            database.CreateCollection<Item>(TemporaryDatabaseFixtureBase.CreateCollectionName(nameof(Converter_with_string_representation_is_converted))),
            mb => mb.Entity<Item>().Property(e => e.Price).HasConversion<long>().HasBsonRepresentation(BsonType.String),
            optionsBuilderAction: PinDefaultQueryMode);
        db.Entities.AddRange(
            new Item { Seq = 1, Price = 9 }, new Item { Seq = 2, Price = 10 }, new Item { Seq = 3, Price = 100 });
        db.SaveChanges();

        Assert.Equal(100, db.Entities.Max(e => e.Price));
        Assert.Equal(119, db.Entities.Sum(e => e.Price));
        Assert.Equal([3], db.Entities.Where(e => e.Price > 50).Select(e => e.Seq).ToList());
        Assert.Equal([1, 2, 3], db.Entities.OrderBy(e => e.Price).Select(e => e.Seq).ToList());
    }

#if !EF8
    [Theory, MemberData(nameof(Storages))]
    public void ExecuteUpdate_with_computed_value_fails_rather_than_corrupt_the_stored_form(Storage s)
    {
        var db = CreateDb(s, nameof(ExecuteUpdate_with_computed_value_fails_rather_than_corrupt_the_stored_form));
        var raw = RawCollection(s, nameof(ExecuteUpdate_with_computed_value_fails_rather_than_corrupt_the_stored_form));

        // Not translatable (as before EF-337). It must not write a number into a field stored as a string.
        Assert.Throws<InvalidOperationException>(
            () => db.Entities.Where(e => e.Seq == 3).ExecuteUpdate(u => u.SetProperty(e => e.Price, e => e.Price + 1)));

        Assert.Equal(BsonType.String, raw.Find(new BsonDocument("Seq", 3)).First()["Price"].BsonType);
    }

    [Theory, MemberData(nameof(Storages))]
    public void ExecuteUpdate_with_constant_value_keeps_the_stored_form(Storage s)
    {
        var db = CreateDb(s, nameof(ExecuteUpdate_with_constant_value_keeps_the_stored_form));
        var raw = RawCollection(s, nameof(ExecuteUpdate_with_constant_value_keeps_the_stored_form));

        db.Entities.Where(e => e.Price > 50).ExecuteUpdate(u => u.SetProperty(e => e.Price, 7));

        Assert.Equal("7", raw.Find(new BsonDocument("Seq", 3)).First()["Price"].AsString);
    }

    [Theory, MemberData(nameof(Storages))]
    public void ExecuteDelete_with_range_filter(Storage s)
    {
        var db = CreateDb(s, nameof(ExecuteDelete_with_range_filter));
        Assert.Equal(1, db.Entities.Where(e => e.Price > 50).ExecuteDelete());
        Assert.Equal(2, db.Entities.Count());
    }
#endif
}
