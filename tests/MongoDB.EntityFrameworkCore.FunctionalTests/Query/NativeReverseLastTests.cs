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
/// EF-411: native <c>Reverse</c>/<c>Last</c>/<c>LastOrDefault</c> for the ORDERED-source case — MQL has no
/// "reverse row order" stage, so the only sound native form is flipping an explicit trailing sort's
/// direction (the exact complement of the original order) and, for Last/LastOrDefault, reusing the ordinary
/// First/FirstOrDefault <c>$limit:1</c> reducer machinery on top of the flipped sort. An unordered source has
/// no defined LINQ row order to complement, so all three decline rather than inventing an unreliable
/// natural-order sort. <c>Last</c>/<c>LastOrDefault</c> have a working driver-LINQ oracle either way (MEASURED
/// live), so their unordered decline falls back gracefully — correct results, throwing only under
/// <see cref="MongoQueryMode.NativeOnly"/>. <c>Reverse</c> does NOT: the C# driver's own LINQ v3 provider
/// does not translate <c>Queryable.Reverse()</c> at all, ordered or not (MEASURED —
/// <see cref="MongoDB.Driver.Linq.ExpressionNotSupportedException"/>), so its decline hard-fails in EVERY
/// mode, exactly like reference SelectMany/Intersect/Except elsewhere in this codebase — this is pre-existing
/// (every <c>Reverse()</c> call failed in every mode before this slice too), not a regression it introduces.
/// <see cref="MongoQueryMode.NativeOnly"/> is the "went native" signal throughout, since the emitted MQL for
/// the ordered case is not otherwise distinguishable from a fallback that flips the sort itself.
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

        Assert.Equal([3, 2, 1], result); // succeeds under NativeOnly => went native
    }

    [Fact]
    public void Reverse_over_descending_order_flips_to_ascending_in_the_emitted_sort()
    {
        var collection = Seed([1, 2, 3], nameof(Reverse_over_descending_order_flips_to_ascending_in_the_emitted_sort));
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly);

        // OrderByDescending puts [3,2,1]; Reverse() must flip the emitted $sort back to ascending — the row
        // order proves the flip (a no-op Reverse, or one that failed to flip, would return [3,2,1] instead).
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

    // MEASURED: the C# driver's own LINQ v3 provider does not support Queryable.Reverse() AT ALL — ordered
    // or not (MongoDB.Driver.Linq.ExpressionNotSupportedException, confirmed via a live probe against both
    // shapes before writing the two tests below). So a Reverse() this slice cannot route natively has NO
    // driver-LINQ oracle to land on, same family as reference SelectMany/Intersect/Except elsewhere in this
    // file: a decline hard-fails in EVERY mode (Native's own fallback attempt included), not just NativeOnly.
    // This is NOT a regression from this slice — every Reverse() call hard-failed in every mode before it
    // too, since native had no coverage for it at all and the driver never did either; this slice only adds
    // the ordered case as a genuinely new, previously-unreachable capability.

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

        // The tail op is a $match (Where), not a $sort, so TryFlipTrailingSortDirection has nothing to flip
        // and this shape declines exactly like the unordered case above — same no-oracle disposition.
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

        Assert.Equal(3, last.Value); // succeeds under NativeOnly => went native
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

        // OrderBy(Value).ThenBy(Name) ascending puts Value==3 last; Last() must return that row, which only
        // holds if BOTH orderings in the ThenBy chain were flipped together (flipping just one would break
        // the tie-break and could return a different row on a model with real ties).
        var last = db.Entities.OrderBy(e => e.Value).ThenBy(e => e.Name).Last();

        Assert.Equal(3, last.Value);
    }

    // EF-322 RE-BASELINE (the three tests below). They pinned Last/LastOrDefault DECLINING when there is no
    // explicit prior sort to flip. That shape now goes native via $group{_id:null,_last:{$last:"$$ROOT"}} +
    // $replaceRoot — the exact MQL the driver-LINQ fallback already emitted for it, so going native did not
    // invent a new notion of "the last row". Re-pinned as results rather than deleted.
    [Fact]
    public void Last_without_an_explicit_order_goes_native_with_driver_linq_parity()
    {
        var collection = Seed([1, 2, 3], nameof(Last_without_an_explicit_order_goes_native_with_driver_linq_parity));

        using var nativeOnlyDb = CreateContext(collection, MongoQueryMode.NativeOnly);
        var nativeValue = nativeOnlyDb.Entities.Last().Value;

        using var driverDb = CreateContext(collection, MongoQueryMode.DriverLinq);
        var driverValue = driverDb.Entities.Last().Value;

        // LINQ leaves row order undefined for an unordered source, so the DRIVER is the oracle here, not a
        // hard-coded value: the contract this pins is that going native did not change which row comes back.
        Assert.Equal(driverValue, nativeValue);
    }

    [Fact]
    public void LastOrDefault_without_an_explicit_order_goes_native_and_returns_null_when_empty()
    {
        var collection = Seed([], nameof(LastOrDefault_without_an_explicit_order_goes_native_and_returns_null_when_empty));

        // The empty case is the one with a DEFINED answer regardless of row order, and it is the case the
        // $group pattern could plausibly get wrong: $group{_id:null} over no input emits no document at all
        // (rather than one with a null _last), so the reducer must still yield null and not throw.
        using var nativeOnlyDb = CreateContext(collection, MongoQueryMode.NativeOnly);
        Assert.Null(nativeOnlyDb.Entities.LastOrDefault());
    }

    [Fact]
    public void Last_after_Take_over_an_ordered_source_goes_native_and_respects_the_Take()
    {
        var collection = Seed([1, 2, 3], nameof(Last_after_Take_over_an_ordered_source_goes_native_and_respects_the_Take));
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly);

        // This used to decline on the grounds that Take(2) had already consumed the limit slot that the
        // sort-flip + $limit:1 lowering needed. The $group lowering needs no limit slot at all, so the
        // conflict is gone — but that makes the ORDERING load-bearing, which is what this asserts: the
        // $group must run AFTER the $sort/$limit ($sort asc, $limit 2, $last => 2), not before or instead
        // of them. Flipping the sort here (the old lowering) would answer 3, and dropping the Take would
        // also answer 3 — so the correct answer distinguishes this from both ways of getting it wrong.
        Assert.Equal(2, db.Entities.OrderBy(e => e.Value).Take(2).Last().Value);
    }
}
