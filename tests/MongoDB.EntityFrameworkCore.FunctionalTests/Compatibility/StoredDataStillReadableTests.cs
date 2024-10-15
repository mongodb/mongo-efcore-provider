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

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Compatibility;

[XUnitCollection("CompatibilityTests")]
public class StoredDataStillReadableTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    [Fact]
    public void Can_read_default_clr_types_from_provider_8_1_written_doc()
    {
        var nonNullableDoc = BsonDocument.Parse(
            """{"_id":{"$oid":"670d7d952112a60d7fa17d98"},"Array":["A","B","C"],"Bool":true,"Byte":201,"Char":99,"DateTimeLocal":{"$date":"2023-10-14T19:22:45.815Z"},"DateTimeUnspecified":{"$date":"2023-10-14T19:22:45.815Z"},"DateTimeUtc":{"$date":"2024-10-14T20:22:45.815Z"},"Decimal":"123123123","Decimal128":"123456.789","Dictionary":{"A":"B","C":"D"},"Double":123123123.123123,"Enum":5,"EnumAsByte":4,"EnumAsString":"Saturday","Float":-134334.234375,"Guid":{"$binary":{"base64":"G5217JknTWKqQ4qYNSszaw==","subType":"04"}},"Int":-10001,"List":["A","B","C"],"Long":{"$numberLong":"-100001"},"Sbyte":-101,"Short":-1001,"String":"A string","TimeSpan":"1.02:03:04.0050060","Uint":1000001,"Ulong":{"$numberLong":"10000001"},"Ushort":1001,"OwnedMany":[{"Name":"Owned 1"},{"Name":"Owned 2"}],"OwnedSingle":{"Name":"Owned"}}""");
        database.CreateCollection<BsonDocument>().InsertOne(nonNullableDoc);

        var collection = database.CreateCollection<NonNullables>();
        using var db = SingleEntityDbContext.Create(collection, ConfigureDefaults);

        var read = db.Entities.First();

        Assert.Equal(_nonNullables.id, read.id);
        Assert.Equal(_nonNullables.Array, read.Array);
        Assert.Equal(_nonNullables.Bool, read.Bool);
        Assert.Equal(_nonNullables.Byte, read.Byte);
        Assert.Equal(_nonNullables.Char, read.Char);
        Assert.Equal(_nonNullables.DateTimeUtc, read.DateTimeUtc);
        Assert.Equal(_nonNullables.Decimal, read.Decimal);
        Assert.Equal(_nonNullables.Decimal128, read.Decimal128);
        Assert.Equal(_nonNullables.Dictionary, read.Dictionary);
        Assert.Equal(_nonNullables.Double, read.Double);
        Assert.Equal(_nonNullables.Enum, read.Enum);
        Assert.Equal(_nonNullables.Float, read.Float);
        Assert.Equal(_nonNullables.Guid, read.Guid);
        Assert.Equal(_nonNullables.Int, read.Int);
        Assert.Equal(_nonNullables.List, read.List);
        Assert.Equal(_nonNullables.Long, read.Long);
        Assert.Equal(_nonNullables.Sbyte, read.Sbyte);
        Assert.Equal(_nonNullables.Short, read.Short);
        Assert.Equal(_nonNullables.String, read.String);
        Assert.Equal(_nonNullables.Uint, read.Uint);
        Assert.Equal(_nonNullables.Ulong, read.Ulong);
        Assert.Equal(_nonNullables.Ushort, read.Ushort);
        Assert.Equal(_nonNullables.OwnedSingle.Name, read.OwnedSingle.Name);
        Assert.Equal(_nonNullables.OwnedMany, read.OwnedMany);
    }

    [Fact]
    public void Can_read_nullable_clr_types_from_provider_8_1_written_doc()
    {
        var nullCompleteDoc = BsonDocument.Parse(
            """{"_id":{"$oid":"670d7d952112a60d7fa17d98"},"Array":["A","B","C"],"Bool":true,"Byte":201,"Char":99,"DateTimeLocal":{"$date":"2023-10-14T19:22:45.815Z"},"DateTimeUnspecified":{"$date":"2023-10-14T19:22:45.815Z"},"DateTimeUtc":{"$date":"2024-10-14T20:22:45.815Z"},"Decimal":{"$numberDecimal":"123123123"},"Decimal128":{"$numberDecimal":"123456.789"},"Dictionary":{"A":"B","C":"D"},"Double":123123123.123123,"Enum":null,"Float":-134334.234375,"Guid":{"$binary":{"base64":"G5217JknTWKqQ4qYNSszaw==","subType":"04"}},"Int":-10001,"List":["A","B","C"],"Long":{"$numberLong":"-100001"},"ObjectId":null,"Sbyte":-101,"Short":-1001,"String":"A string","TimeSpan":"1.02:03:04.0050060","Uint":1000001,"Ulong":{"$numberLong":"10000001"},"Ushort":1001,"OwnedMany":[{"Name":"Owned 1"},{"Name":"Owned 2"}],"OwnedSingle":{"Name":"Owned"}}""");
        var nullDefaultDoc = BsonDocument.Parse(
            """{"_id":{"$oid":"670d7d952112a60d7fa17d99"},"Array":null,"Bool":null,"Byte":null,"Char":null,"DateTimeLocal":null,"DateTimeUnspecified":null,"DateTimeUtc":null,"Decimal":null,"Decimal128":null,"Dictionary":null,"Double":null,"Enum":null,"Float":null,"Guid":null,"Int":null,"List":null,"Long":null,"ObjectId":null,"Sbyte":null,"Short":null,"String":null,"TimeSpan":null,"Uint":null,"Ulong":null,"Ushort":null,"OwnedMany":null,"OwnedSingle":null}""");
        database.CreateCollection<BsonDocument>().InsertMany([nullCompleteDoc, nullDefaultDoc]);

        var collection = database.CreateCollection<Nullables>();
        using var db = SingleEntityDbContext.Create(collection);

        CheckNullables(_nullableSet, db.Entities.First(e => e.id == _nullableSet.id));
        CheckNullables(_nullableDefault, db.Entities.First(e => e.id == _nullableDefault.id));
    }

    private static void CheckNullables(Nullables expected, Nullables actual)
    {
        Assert.Equal(expected.id, actual.id);
        Assert.Equal(expected.Array, actual.Array);
        Assert.Equal(expected.Bool, actual.Bool);
        Assert.Equal(expected.Byte, actual.Byte);
        Assert.Equal(expected.Char, actual.Char);
        Assert.Equal(expected.DateTimeUtc, actual.DateTimeUtc);
        Assert.Equal(expected.Decimal, actual.Decimal);
        Assert.Equal(expected.Decimal128, actual.Decimal128);
        Assert.Equal(expected.Dictionary, actual.Dictionary);
        Assert.Equal(expected.Double, actual.Double);
        Assert.Equal(expected.Enum, actual.Enum);
        Assert.Equal(expected.Float, actual.Float);
        Assert.Equal(expected.Guid, actual.Guid);
        Assert.Equal(expected.Int, actual.Int);
        Assert.Equal(expected.List, actual.List);
        Assert.Equal(expected.Long, actual.Long);
        Assert.Equal(expected.ObjectId, actual.ObjectId);
        Assert.Equal(expected.Sbyte, actual.Sbyte);
        Assert.Equal(expected.Short, actual.Short);
        Assert.Equal(expected.String, actual.String);
        Assert.Equal(expected.Uint, actual.Uint);
        Assert.Equal(expected.Ulong, actual.Ulong);
        Assert.Equal(expected.Ushort, actual.Ushort);
        Assert.Equal(expected.OwnedSingle, actual.OwnedSingle);
        Assert.Equal(expected.OwnedMany, actual.OwnedMany);
    }

    [Fact]
    public void Can_write_default_clr_types()
    {
        var collection = database.CreateCollection<NonNullables>();
        using var db = SingleEntityDbContext.Create(collection, ConfigureDefaults);
        db.Entities.Add(_nonNullables);
        db.SaveChanges();
    }

    [Fact]
    public void Can_write_nullable_clr_types()
    {
        var collection = database.CreateCollection<Nullables>();
        using var db = SingleEntityDbContext.Create(collection);
        db.Entities.Add(_nullableSet);
        db.Entities.Add(_nullableDefault);
        db.SaveChanges();
    }

    private static void ConfigureDefaults(ModelBuilder mb)
        => mb.Entity<NonNullables>(e =>
        {
            e.Property(f => f.EnumAsByte).HasConversion<byte>();
            e.Property(f => f.EnumAsString).HasConversion<string>();
        });

    public class NonNullables
    {
        public ObjectId id { get; set; }

        public string String { get; set; }
        public char Char { get; set; }

        public byte Byte { get; set; }
        public sbyte Sbyte { get; set; }

        public short Short { get; set; }
        public int Int { get; set; }
        public long Long { get; set; }

        public uint Uint { get; set; }
        public ulong Ulong { get; set; }
        public ushort Ushort { get; set; }

        public decimal Decimal { get; set; }
        public double Double { get; set; }
        public float Float { get; set; }
        public Decimal128 Decimal128 { get; set; }

        public DayOfWeek Enum { get; set; }
        public DayOfWeek EnumAsString { get; set; }
        public DayOfWeek EnumAsByte { get; set; }

        public DateTime DateTimeUtc { get; set; }
        public DateTime DateTimeLocal { get; set; }
        public DateTime DateTimeUnspecified { get; set; }
        public TimeSpan TimeSpan { get; set; }

        public bool Bool { get; set; }
        public Guid Guid { get; set; }

        public List<string> List { get; set; }
        public string[] Array { get; set; }
        public Dictionary<string, string> Dictionary { get; set; }

        public Owned OwnedSingle { get; set; }
        public List<Owned> OwnedMany { get; set; }
    }

    private readonly NonNullables _nonNullables = new()
    {
        id = ObjectId.Parse("670d7d952112a60d7fa17d98"),

        String = "A string",
        Char = 'c',

        Byte = 201,
        Sbyte = -101,

        Short = -1001,
        Int = -10001,
        Long = -100001,

        Uint = 1000001,
        Ulong = 10000001,
        Ushort = 1001,

        Decimal = 123123123m,
        Double = 123123123.123123,
        Float = -134334.234f,
        Decimal128 = new Decimal128(123456.789),

        Enum = DayOfWeek.Friday,
        EnumAsByte = DayOfWeek.Thursday,
        EnumAsString = DayOfWeek.Saturday,

        DateTimeUtc = new DateTime(2024, 10, 14, 20, 22, 45, 815, DateTimeKind.Utc),
        DateTimeLocal = new DateTime(2023, 10, 14, 20, 22, 45, 815, DateTimeKind.Local),
        DateTimeUnspecified = new DateTime(2023, 10, 14, 20, 22, 45, 815, DateTimeKind.Unspecified),
        TimeSpan = new TimeSpan(1, 2, 3, 4, 5, 6),

        Bool = true,
        Guid = Guid.Parse("1b9db5ec-9927-4d62-aa43-8a98352b336b"),

        List = ["A", "B", "C"],
        Array = ["A", "B", "C"],
        Dictionary = new Dictionary<string, string> {{"A", "B"}, {"C", "D"}},

        OwnedSingle = new Owned {Name = "Owned"},
        OwnedMany =
        [
            new() {Name = "Owned 1"},
            new() {Name = "Owned 2"}
        ]
    };

    public class Nullables
    {
        public ObjectId id { get; set; }

        public string? String { get; set; }
        public char? Char { get; set; }

        public byte? Byte { get; set; }
        public sbyte? Sbyte { get; set; }

        public short? Short { get; set; }
        public int? Int { get; set; }
        public long? Long { get; set; }

        public uint? Uint { get; set; }
        public ulong? Ulong { get; set; }
        public ushort? Ushort { get; set; }

        public decimal? Decimal { get; set; }
        public double? Double { get; set; }
        public float? Float { get; set; }
        public Decimal128? Decimal128 { get; set; }

        public DayOfWeek? Enum { get; set; }
        public DayOfWeek? EnumAsString { get; set; }
        public DayOfWeek? EnumAsByte { get; set; }

        public DateTime? DateTimeUtc { get; set; }
        public DateTime? DateTimeLocal { get; set; }
        public DateTime? DateTimeUnspecified { get; set; }
        public TimeSpan? TimeSpan { get; set; }

        public bool? Bool { get; set; }

        public Guid? Guid { get; set; }
        public ObjectId? ObjectId { get; set; }

        public List<string>? List { get; set; }
        public string[]? Array { get; set; }
        public Dictionary<string, string>? Dictionary { get; set; }

        public Owned? OwnedSingle { get; set; }
        public List<Owned>? OwnedMany { get; set; }
    }

    private readonly Nullables _nullableSet = new()
    {
        id = ObjectId.Parse("670d7d952112a60d7fa17d98"),

        String = "A string",
        Char = 'c',

        Byte = 201,
        Sbyte = -101,

        Short = -1001,
        Int = -10001,
        Long = -100001,

        Uint = 1000001,
        Ulong = 10000001,
        Ushort = 1001,

        Decimal = 123123123m,
        Double = 123123123.123123,
        Float = -134334.234f,
        Decimal128 = new Decimal128(123456.789),

        DateTimeUtc = new DateTime(2024, 10, 14, 20, 22, 45, 815, DateTimeKind.Utc),
        DateTimeLocal = new DateTime(2023, 10, 14, 20, 22, 45, 815, DateTimeKind.Local),
        DateTimeUnspecified = new DateTime(2023, 10, 14, 20, 22, 45, 815, DateTimeKind.Unspecified),
        TimeSpan = new TimeSpan(1, 2, 3, 4, 5, 6),

        Bool = true,
        Guid = Guid.Parse("1b9db5ec-9927-4d62-aa43-8a98352b336b"),
        ObjectId = ObjectId.Parse("670d7d952112a60d7fa17d9f"),

        List = ["A", "B", "C"],
        Array = ["A", "B", "C"],
        Dictionary = new Dictionary<string, string> {{"A", "B"}, {"C", "D"}},

        OwnedSingle = new Owned {Name = "Owned"},
        OwnedMany =
        [
            new() {Name = "Owned 1"},
            new() {Name = "Owned 2"}
        ]
    };

    private readonly Nullables _nullableDefault = new() {id = ObjectId.Parse("670d7d952112a60d7fa17d99")};

    public record Owned
    {
        public string Name { get; set; }
    }
}
