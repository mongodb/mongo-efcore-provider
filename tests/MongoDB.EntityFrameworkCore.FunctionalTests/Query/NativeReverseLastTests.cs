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

using System;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Linq;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.Infrastructure;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// Native <c>Reverse</c>/<c>Last</c>/<c>LastOrDefault</c>. MQL has no "reverse row order" stage, so
/// <c>Reverse</c> is native only by flipping an explicit trailing sort; otherwise it hard-fails in every mode
/// because the driver's LINQ provider doesn't translate <c>Reverse</c> either. <c>Last</c> over a trailing sort
/// flips it; otherwise it uses <c>$group</c>/<c>$last</c>. <see cref="MongoQueryMode.NativeOnly"/> is the
/// "went native" signal, since the MQL can't tell the paths apart.
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeReverseLastTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    private class ValueEntity
    {
        public ObjectId Id { get; set; }
        public int Value { get; set; }
        public string Name { get; set; } = "";
    }

    private IMongoCollection<ValueEntity> Seed(int[] values, string name)
    {
        var collectionName = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + Guid.NewGuid().ToString("N")[..8];
        var collection = database.MongoDatabase.GetCollection<ValueEntity>(collectionName);
        if (values.Length > 0)
            collection.InsertMany(values.Select(v => new ValueEntity { Id = ObjectId.GenerateNewId(), Value = v, Name = $"n{v}" }));
        return collection;
    }

    private static SingleEntityDbContext<ValueEntity> CreateContext(IMongoCollection<ValueEntity> collection, MongoQueryMode mode)
        => SingleEntityDbContext.Create(
            collection,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });

    // ── Reverse ────────────────────────────────────────────────────────────────

    [Fact]
    public void Reverse_over_explicit_order_goes_native_and_reverses_the_rows()
    {
        var collection = Seed([1, 2, 3], nameof(Reverse_over_explicit_order_goes_native_and_reverses_the_rows));
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly);

        var result = db.Entities.OrderBy(e => e.Value).Reverse().Select(e => e.Value).ToList();

        Assert.Equal([3, 2, 1], result);
    }

    [Fact]
    public void Reverse_over_descending_order_flips_to_ascending_in_the_emitted_sort()
    {
        var collection = Seed([1, 2, 3], nameof(Reverse_over_descending_order_flips_to_ascending_in_the_emitted_sort));
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly);

        // A Reverse that failed to flip the $sort would return [3,2,1].
        var result = db.Entities.OrderByDescending(e => e.Value).Reverse().Select(e => e.Value).ToList();

        Assert.Equal([1, 2, 3], result);
    }

    [Fact]
    public void Reverse_over_a_ThenBy_chain_flips_every_ordering()
    {
        var collection = Seed([1, 1, 2], nameof(Reverse_over_a_ThenBy_chain_flips_every_ordering));
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly);

        var result = db.Entities.OrderBy(e => e.Value).ThenBy(e => e.Name).Reverse().Select(e => e.Name).ToList();

        Assert.Equal(["n2", "n1", "n1"], result);
    }

    // The driver's LINQ provider doesn't support Queryable.Reverse(), so a Reverse that can't go native has no
    // fallback and fails in every mode.

    [Fact]
    public void Reverse_without_an_explicit_order_still_hard_fails_in_every_mode()
    {
        var collection = Seed([1, 2, 3], nameof(Reverse_without_an_explicit_order_still_hard_fails_in_every_mode));

        using var nativeDb = CreateContext(collection, MongoQueryMode.Native);
        Assert.Throws<ExpressionNotSupportedException>(() => nativeDb.Entities.Reverse().ToList());

        using var driverDb = CreateContext(collection, MongoQueryMode.DriverLinq);
        Assert.Throws<ExpressionNotSupportedException>(() => driverDb.Entities.Reverse().ToList());

        using var nativeOnlyDb = CreateContext(collection, MongoQueryMode.NativeOnly);
        Assert.Throws<NativeTranslationNotSupportedException>(() => nativeOnlyDb.Entities.Reverse().ToList());
    }

    [Fact]
    public void Reverse_after_a_composed_operator_following_the_sort_still_hard_fails_in_every_mode()
    {
        var collection = Seed([1, 2, 3], nameof(Reverse_after_a_composed_operator_following_the_sort_still_hard_fails_in_every_mode));

        // The trailing op is a $match, not a $sort, so there is nothing to flip.
        using var nativeDb = CreateContext(collection, MongoQueryMode.Native);
        Assert.Throws<ExpressionNotSupportedException>(
            () => nativeDb.Entities.OrderBy(e => e.Value).Where(e => e.Value > 0).Reverse().ToList());

        using var nativeOnlyDb = CreateContext(collection, MongoQueryMode.NativeOnly);
        Assert.Throws<NativeTranslationNotSupportedException>(
            () => nativeOnlyDb.Entities.OrderBy(e => e.Value).Where(e => e.Value > 0).Reverse().ToList());
    }

    // ── Last / LastOrDefault ──────────────────────────────────────────────────

    [Fact]
    public void Last_over_ordered_source_goes_native_and_returns_the_max()
    {
        var collection = Seed([1, 3, 2], nameof(Last_over_ordered_source_goes_native_and_returns_the_max));
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly);

        var last = db.Entities.OrderBy(e => e.Value).Last();

        Assert.Equal(3, last.Value);
    }

    [Fact]
    public void LastOrDefault_over_ordered_source_goes_native_and_returns_the_max()
    {
        var collection = Seed([1, 3, 2], nameof(LastOrDefault_over_ordered_source_goes_native_and_returns_the_max));
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly);

        var last = db.Entities.OrderBy(e => e.Value).LastOrDefault();

        Assert.NotNull(last);
        Assert.Equal(3, last!.Value);
    }

    [Fact]
    public void LastOrDefault_over_an_empty_ordered_source_returns_null_and_goes_native()
    {
        var collection = Seed([], nameof(LastOrDefault_over_an_empty_ordered_source_returns_null_and_goes_native));
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly);

        var last = db.Entities.OrderBy(e => e.Value).LastOrDefault();

        Assert.Null(last);
    }

    [Fact]
    public void Last_over_an_empty_ordered_source_throws_the_BCL_empty_sequence_contract()
    {
        var collection = Seed([], nameof(Last_over_an_empty_ordered_source_throws_the_BCL_empty_sequence_contract));
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly);

        Assert.Throws<InvalidOperationException>(() => db.Entities.OrderBy(e => e.Value).Last());
    }

    [Fact]
    public void Last_over_a_descending_order_flips_to_ascending_and_returns_the_minimum()
    {
        var collection = Seed([1, 3, 2], nameof(Last_over_a_descending_order_flips_to_ascending_and_returns_the_minimum));
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly);

        var last = db.Entities.OrderByDescending(e => e.Value).Last();

        Assert.Equal(1, last.Value); // the LAST row of a descending order is the minimum
    }

    [Fact]
    public void Last_over_a_ThenBy_chain_flips_every_ordering()
    {
        var collection = Seed([2, 1, 3], nameof(Last_over_a_ThenBy_chain_flips_every_ordering));
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly);

        // Every ordering in the ThenBy chain must be flipped together, or ties could return a different row.
        var last = db.Entities.OrderBy(e => e.Value).ThenBy(e => e.Name).Last();

        Assert.Equal(3, last.Value);
    }

    // Without a trailing sort to flip, Last/LastOrDefault lower to $group{_id:null,_last:{$last:"$$ROOT"}} +
    // $replaceRoot, the same MQL driver LINQ emits.
    [Fact]
    public void Last_without_an_explicit_order_goes_native_with_driver_linq_parity()
    {
        var collection = Seed([1, 2, 3], nameof(Last_without_an_explicit_order_goes_native_with_driver_linq_parity));

        using var nativeOnlyDb = CreateContext(collection, MongoQueryMode.NativeOnly);
        var nativeValue = nativeOnlyDb.Entities.Last().Value;

        using var driverDb = CreateContext(collection, MongoQueryMode.DriverLinq);
        var driverValue = driverDb.Entities.Last().Value;

        // Row order is undefined for an unordered source, so driver LINQ is the oracle.
        Assert.Equal(driverValue, nativeValue);
    }

    [Fact]
    public void LastOrDefault_without_an_explicit_order_goes_native_and_returns_null_when_empty()
    {
        var collection = Seed([], nameof(LastOrDefault_without_an_explicit_order_goes_native_and_returns_null_when_empty));

        // $group{_id:null} over no input emits no document at all; the reducer must still yield null.
        using var nativeOnlyDb = CreateContext(collection, MongoQueryMode.NativeOnly);
        Assert.Null(nativeOnlyDb.Entities.LastOrDefault());
    }

    [Fact]
    public void Last_after_Take_over_an_ordered_source_goes_native_and_respects_the_Take()
    {
        var collection = Seed([1, 2, 3], nameof(Last_after_Take_over_an_ordered_source_goes_native_and_respects_the_Take));
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly);

        // The $group must run after $sort/$limit (answer 2). Flipping the sort instead, or dropping the Take,
        // would both answer 3.
        Assert.Equal(2, db.Entities.OrderBy(e => e.Value).Take(2).Last().Value);
    }
}
