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

using System.Collections;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Infrastructure;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// A scalar projection is read straight off the cursor into typed slots instead of a <see cref="BsonDocument"/> per row. The slots must
/// read exactly as the document did: the same values for missing, null and converted elements, the same errors on the same rows, and the
/// projection's client-side code running when the enumerator reaches each row, not when the driver reads the batch.
/// </summary>
public class NativeStreamingProjectionTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    private static SingleEntityDbContext<T> CreateContext<T>(IMongoCollection<T> collection, MongoQueryMode mode)
        where T : class
        => SingleEntityDbContext.Create(
            collection,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });

    private class Row
    {
        public int Id { get; set; }
        public int Count { get; set; }
        public string? Name { get; set; }
        public int? Opt { get; set; }
        public double Rate { get; set; }
    }

    // Ragged on purpose: a present row, explicit nulls, missing elements, and values stored as another numeric type.
    private IMongoCollection<Row> SeedRows([CallerMemberName] string? name = null)
    {
        var collection = database.CreateCollection<Row>(name);
        var raw = collection.Database.GetCollection<BsonDocument>(collection.CollectionNamespace.CollectionName);
        raw.InsertMany([
            new BsonDocument { { "_id", 1 }, { "Count", 1 }, { "Name", "a" }, { "Opt", 10 }, { "Rate", 1.5 } },
            new BsonDocument { { "_id", 2 }, { "Count", 2 }, { "Name", BsonNull.Value }, { "Opt", BsonNull.Value }, { "Rate", 2.5 } },
            new BsonDocument { { "_id", 3 }, { "Count", 3 }, { "Rate", 3.5 } },
            new BsonDocument { { "_id", 4 }, { "Count", 4L }, { "Name", "d" }, { "Opt", 40L }, { "Rate", 4 } },
        ]);
        return collection;
    }

    // The cursor's row type: a streaming projection yields MongoProjectedRow<>, a DOM projection yields BsonDocument.
    private static Type CursorRowType<T>(IQueryable<T> query)
    {
        var enumerator = ((IEnumerable)query).GetEnumerator();
        try
        {
            return enumerator.GetType().GetGenericArguments()[0];
        }
        finally
        {
            (enumerator as IDisposable)?.Dispose();
        }
    }

    private static void AssertStreams<T>(IQueryable<T> query)
    {
        var rowType = CursorRowType(query);
        Assert.True(
            rowType.IsGenericType && rowType.GetGenericTypeDefinition() == typeof(MongoProjectedRow<>),
            $"Expected the streaming projection reader, but the cursor yields {rowType}.");
    }

    // ---- Values ----------------------------------------------------------------------------------------

    [Fact]
    public void Scalar_projection_streams_and_matches_driver_linq()
    {
        var collection = SeedRows();

        using (var db = CreateContext(collection, MongoQueryMode.NativeOnly))
        {
            AssertStreams(db.Entities.AsNoTracking().Select(r => r.Count));
        }

        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateContext(collection, mode);
            return db.Entities.AsNoTracking().OrderBy(r => r.Id).Select(r => r.Count).ToList();
        });

        Assert.Equal([1, 2, 3, 4], results);
    }

    [Fact]
    public void Multi_member_projection_with_null_and_missing_elements_matches_driver_linq()
    {
        var collection = SeedRows();

        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateContext(collection, mode);
            return db.Entities.AsNoTracking().OrderBy(r => r.Id)
                .Select(r => new { r.Count, r.Name, r.Opt, r.Rate })
                .AsEnumerable()
                .Select(r => $"{r.Count}|{r.Name ?? "<null>"}|{r.Opt?.ToString() ?? "<null>"}|{r.Rate}")
                .ToList();
        });

        Assert.Equal(["1|a|10|1.5", "2|<null>|<null>|2.5", "3|<null>|<null>|3.5", "4|d|40|4"], results);
    }

    [Fact]
    public void Computed_projection_and_a_repeated_alias_match_driver_linq()
    {
        var collection = SeedRows();

        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateContext(collection, mode);
            return db.Entities.AsNoTracking().OrderBy(r => r.Id)
                .Select(r => new { A = r.Count, B = r.Count * 2 + 1, C = r.Count + r.Count })
                .AsEnumerable()
                .Select(r => $"{r.A}|{r.B}|{r.C}")
                .ToList();
        });

        Assert.Equal(["1|3|2", "2|5|4", "3|7|6", "4|9|8"], results);
    }

    private class Described
    {
        private int _count;

        public int Count
        {
            get => _count;
            set
            {
                _count = value;
                lock (Calls)
                {
                    Calls.Add(value);
                }
            }
        }

        public string? Name { get; set; }
    }

    [Fact]
    public void Member_init_projection_matches_driver_linq()
    {
        var collection = SeedRows();

        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateContext(collection, mode);
            return db.Entities.AsNoTracking().OrderBy(r => r.Id)
                .Select(r => new Described { Count = r.Count, Name = r.Name })
                .AsEnumerable()
                .Select(d => $"{d.Count}:{d.Name ?? "-"}")
                .ToList();
        });

        Assert.Equal(["1:a", "2:-", "3:-", "4:d"], results);
    }

    [Fact]
    public async Task First_single_and_async_reads_match_driver_linq()
    {
        var collection = SeedRows();

        foreach (var mode in new[] { MongoQueryMode.NativeOnly, MongoQueryMode.DriverLinq })
        {
            using var db = CreateContext(collection, mode);
            Assert.Equal(1, db.Entities.AsNoTracking().OrderBy(r => r.Id).Select(r => r.Count).First());
            Assert.Equal(3, db.Entities.AsNoTracking().Where(r => r.Id == 3).Select(r => r.Count).Single());
            Assert.Equal(0, db.Entities.AsNoTracking().Where(r => r.Id == 99).Select(r => r.Count).FirstOrDefault());
            Assert.Equal(
                [1, 2, 3, 4],
                await db.Entities.AsNoTracking().OrderBy(r => r.Id).Select(r => r.Count).ToListAsync());
        }
    }

    // ---- A projection wider than one ValueTuple (7 items nest through Rest) ----------------------------

    private class Wide
    {
        public int Id { get; set; }
        public int P1 { get; set; }
        public int P2 { get; set; }
        public long P3 { get; set; }
        public string P4 { get; set; } = "";
        public bool P5 { get; set; }
        public double P6 { get; set; }
        public int P7 { get; set; }
        public int P8 { get; set; }
        public string P9 { get; set; } = "";
        public int? P10 { get; set; }
    }

    [Fact]
    public void Projection_wider_than_one_value_tuple_streams_and_matches_driver_linq()
    {
        var collection = database.CreateCollection<Wide>();
        collection.InsertMany(Enumerable.Range(1, 5).Select(i => new Wide
        {
            Id = i, P1 = i, P2 = i * 2, P3 = i * 3L, P4 = "four" + i, P5 = i % 2 == 0, P6 = i / 2.0, P7 = i * 7, P8 = i * 8, P9 = "nine" + i,
            P10 = i % 2 == 0 ? null : i * 10,
        }));

        using (var db = CreateContext(collection, MongoQueryMode.NativeOnly))
        {
            AssertStreams(db.Entities.AsNoTracking().Select(w => new { w.P1, w.P2, w.P3, w.P4, w.P5, w.P6, w.P7, w.P8, w.P9, w.P10 }));
        }

        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateContext(collection, mode);
            return db.Entities.AsNoTracking().OrderBy(w => w.Id)
                .Select(w => new { w.P1, w.P2, w.P3, w.P4, w.P5, w.P6, w.P7, w.P8, w.P9, w.P10 })
                .AsEnumerable()
                .Select(w => $"{w.P1}|{w.P2}|{w.P3}|{w.P4}|{w.P5}|{w.P6}|{w.P7}|{w.P8}|{w.P9}|{w.P10?.ToString() ?? "<null>"}")
                .ToList();
        });

        Assert.Equal("1|2|3|four1|False|0.5|7|8|nine1|10", results[0]);
        Assert.Equal("2|4|6|four2|True|1|14|16|nine2|<null>", results[1]);
    }

    // ---- Types that take the serializer rather than the direct read ----------------------------------

    private enum Kind { None, Small, Large }

    private class Typed
    {
        public int Id { get; set; }
        public Kind Kind { get; set; }
        public DateTime When { get; set; }
        public Guid Token { get; set; }
        public decimal Amount { get; set; }
        public Kind? MaybeKind { get; set; }
        public TimeSpan Span { get; set; }
    }

    [Fact]
    public void Enum_datetime_guid_decimal_and_timespan_leaves_match_driver_linq()
    {
        var collection = database.CreateCollection<Typed>();
        collection.InsertMany([
            new Typed { Id = 1, Kind = Kind.Small, When = new DateTime(2024, 3, 4, 5, 6, 7, DateTimeKind.Utc), Token = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e"), Amount = 12.34m, MaybeKind = Kind.Large, Span = TimeSpan.FromMinutes(90) },
            new Typed { Id = 2, Kind = Kind.Large, When = new DateTime(1999, 12, 31, 23, 59, 59, DateTimeKind.Utc), Token = Guid.Empty, Amount = -0.5m, MaybeKind = null, Span = TimeSpan.Zero },
        ]);

        using (var db = CreateContext(collection, MongoQueryMode.NativeOnly))
        {
            AssertStreams(db.Entities.AsNoTracking().Select(t => new { t.Kind, t.When, t.Token, t.Amount, t.MaybeKind, t.Span }));
        }

        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateContext(collection, mode);
            return db.Entities.AsNoTracking().OrderBy(t => t.Id)
                .Select(t => new { t.Kind, t.When, t.Token, t.Amount, t.MaybeKind, t.Span })
                .AsEnumerable()
                .Select(t => $"{t.Kind}|{t.When:O}|{t.Token}|{t.Amount}|{t.MaybeKind?.ToString() ?? "<null>"}|{t.Span}")
                .ToList();
        });

        Assert.Equal(2, results.Count);
    }

    // ---- Required elements that are null, missing or malformed -----------------------------------------

    [Fact]
    public void Missing_required_element_reads_default_and_null_required_element_throws_on_its_own_row()
    {
        var collection = database.CreateCollection<Row>();
        var raw = collection.Database.GetCollection<BsonDocument>(collection.CollectionNamespace.CollectionName);
        raw.InsertMany([
            new BsonDocument { { "_id", 1 }, { "Count", 1 } },
            new BsonDocument { { "_id", 2 } }, // Count missing: reads default, as the driver's $project push-down did
            new BsonDocument { { "_id", 3 }, { "Count", BsonNull.Value } }, // explicit null of a required property throws
            new BsonDocument { { "_id", 4 }, { "Count", 4 } },
        ]);

        // (Driver-LINQ throws the driver's FormatException for the null, from inside its batch read; only the provider's message is asserted.)
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly);
        using var enumerator = db.Entities.AsNoTracking().OrderBy(r => r.Id).Select(r => r.Count).AsEnumerable().GetEnumerator();

        Assert.True(enumerator.MoveNext());
        Assert.Equal(1, enumerator.Current);
        Assert.True(enumerator.MoveNext());
        Assert.Equal(0, enumerator.Current);

        var exception = Assert.Throws<InvalidOperationException>(() => enumerator.MoveNext());
        Assert.Contains("'Count'", exception.Message);
        Assert.Contains("null", exception.Message);
    }

    [Fact]
    public void Malformed_element_fails_only_its_own_row_with_the_same_exception_type_as_driver_linq()
    {
        var collection = database.CreateCollection<Row>();
        var raw = collection.Database.GetCollection<BsonDocument>(collection.CollectionNamespace.CollectionName);
        raw.InsertMany([
            new BsonDocument { { "_id", 1 }, { "Count", 1 } },
            new BsonDocument { { "_id", 2 }, { "Count", 2 } },
            new BsonDocument { { "_id", 3 }, { "Count", "not an int" } },
            new BsonDocument { { "_id", 4 }, { "Count", 4 } },
        ]);

        // Driver-LINQ deserializes the whole batch inside the driver, so it fails before returning row 1; only its exception type is compared.
        using var driverDb = CreateContext(collection, MongoQueryMode.DriverLinq);
        var driverType = Assert.ThrowsAny<Exception>(
            () => driverDb.Entities.AsNoTracking().OrderBy(r => r.Id).Select(r => r.Count).ToList()).GetType();

        using var db = CreateContext(collection, MongoQueryMode.NativeOnly);
        using var enumerator = db.Entities.AsNoTracking().OrderBy(r => r.Id).Select(r => r.Count).AsEnumerable().GetEnumerator();

        // The two valid rows before the malformed one come back; the failure is raised only when the enumerator reaches row 3.
        Assert.True(enumerator.MoveNext());
        Assert.Equal(1, enumerator.Current);
        Assert.True(enumerator.MoveNext());
        Assert.Equal(2, enumerator.Current);

        Assert.Equal(driverType, Assert.ThrowsAny<Exception>(() => enumerator.MoveNext()).GetType());
    }

    [Fact]
    public void Malformed_row_after_the_rows_read_does_not_throw()
    {
        var collection = database.CreateCollection<Row>();
        var raw = collection.Database.GetCollection<BsonDocument>(collection.CollectionNamespace.CollectionName);
        raw.InsertMany([
            new BsonDocument { { "_id", 1 }, { "Count", 1 } },
            new BsonDocument { { "_id", 2 }, { "Count", 2 } },
            new BsonDocument { { "_id", 3 }, { "Count", "not an int" } },
        ]);

        using var db = CreateContext(collection, MongoQueryMode.NativeOnly);
        var firstTwo = db.Entities.AsNoTracking().OrderBy(r => r.Id).Select(r => r.Count).AsEnumerable().Take(2).ToList();

        Assert.Equal([1, 2], firstTwo);
    }

    // ---- Client-side code runs when the enumerator reaches a row ---------------------------------------

    private static readonly List<int> Calls = [];

    [Fact]
    public void Client_side_projection_code_runs_per_row_as_the_enumerator_reaches_it()
    {
        var collection = SeedRows();
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly);

        lock (Calls)
        {
            Calls.Clear();
        }

        using var enumerator = db.Entities.AsNoTracking().OrderBy(r => r.Id)
            .Select(r => new Described { Count = r.Count, Name = r.Name }).AsEnumerable().GetEnumerator();

        Assert.True(enumerator.MoveNext());

        // The driver has read the whole batch by now, but only the row the caller reached has been shaped.
        lock (Calls)
        {
            Assert.Equal([1], Calls);
        }

        Assert.True(enumerator.MoveNext());
        lock (Calls)
        {
            Assert.Equal([1, 2], Calls);
        }
    }

    // ---- Shapes that keep the DOM shaper ---------------------------------------------------------------

    [Fact]
    public void Projection_with_no_document_reads_keeps_the_dom_shaper()
    {
        var collection = SeedRows();
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly);

        // A whole entity is the entity materializer's, not a projection's, and is never a MongoProjectedRow.
        var rowType = CursorRowType(db.Entities.AsNoTracking());
        Assert.False(rowType.IsGenericType && rowType.GetGenericTypeDefinition() == typeof(MongoProjectedRow<>));
    }
}
