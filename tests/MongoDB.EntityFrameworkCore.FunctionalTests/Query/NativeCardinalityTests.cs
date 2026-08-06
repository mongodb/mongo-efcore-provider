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
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.Infrastructure;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// EF-SP4 native entity reducers (First/FirstOrDefault/Single/SingleOrDefault). Proves that the reducer
/// synthesizes a native <c>$limit</c> and that EF Core's base cardinality reduction runs correctly over
/// the resulting <see cref="System.Collections.Generic.IEnumerable{T}"/> (empty ⇒ throw for
/// First/Single, empty ⇒ null for *OrDefault, more-than-one ⇒ throw for Single*).
/// <see cref="MongoQueryMode.NativeOnly"/> is used as the "went native" signal (succeeds ⇒ native;
/// throws <see cref="NativeTranslationNotSupportedException"/> ⇒ fell back to driver-LINQ) since the
/// emitted MQL is not otherwise distinguishable from the driver-LINQ fallback.
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeCardinalityTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    private class ValueEntity
    {
        public ObjectId Id { get; set; }
        public int Value { get; set; }
        public decimal DecimalValue { get; set; }
        public int? NullableValue { get; set; }
        public bool IsActive { get; set; }
        public string Name { get; set; } = "";

        // EF-335: a SECOND plain int property, used only by the field-to-field All() fallback test below —
        // a field-to-field comparison has no exact query-dialect complement (MongoExpressionNegator declines
        // it), so All(e => e.Value > e.OtherValue) must still fall back to driver-LINQ. Defaults to 0, so
        // every pre-existing seed (which never sets it) is unaffected.
        public int OtherValue { get; set; }
    }

    /// <summary>
    /// Shared seed/context builder for the int/decimal/nullable-int/bool <see cref="ValueEntity"/> shapes below
    /// — they differ only in which property the seed values project onto.
    /// </summary>
    private SingleEntityDbContext<ValueEntity> CreateContext<TSeed>(
        TSeed[] seed, Func<TSeed, ValueEntity> project, MongoQueryMode mode, string name)
    {
        var collectionName = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + Guid.NewGuid().ToString("N")[..8];
        var collection = database.MongoDatabase.GetCollection<ValueEntity>(collectionName);
        if (seed.Length > 0)
            collection.InsertMany(seed.Select(project));

        return SingleEntityDbContext.Create(
            collection,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });
    }

    private SingleEntityDbContext<ValueEntity> CreateContext(int[] seed, MongoQueryMode mode, string name)
        => CreateContext(seed, v => new ValueEntity { Id = ObjectId.GenerateNewId(), Value = v }, mode, name);

    private SingleEntityDbContext<ValueEntity> CreateDecimalContext(decimal[] seed, MongoQueryMode mode, string name)
        => CreateContext(seed, v => new ValueEntity { Id = ObjectId.GenerateNewId(), DecimalValue = v }, mode, name);

    private SingleEntityDbContext<ValueEntity> CreateNullableContext(int?[] seed, MongoQueryMode mode, string name)
        => CreateContext(seed, v => new ValueEntity { Id = ObjectId.GenerateNewId(), NullableValue = v }, mode, name);

    private SingleEntityDbContext<ValueEntity> CreateBoolContext(bool[] seed, MongoQueryMode mode, string name)
        => CreateContext(seed, v => new ValueEntity { Id = ObjectId.GenerateNewId(), IsActive = v }, mode, name);

    private SingleEntityDbContext<ValueEntity> CreateStringContext(string[] seed, MongoQueryMode mode, string name)
    {
        var collectionName = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + Guid.NewGuid().ToString("N")[..8];
        var collection = database.MongoDatabase.GetCollection<ValueEntity>(collectionName);
        if (seed.Length > 0)
            collection.InsertMany(seed.Select(v => new ValueEntity { Id = ObjectId.GenerateNewId(), Name = v }));

        return SingleEntityDbContext.Create(
            collection,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });
    }

    [Fact]
    public void First_returns_first_and_goes_native()
    {
        using var db = CreateContext([1, 2, 3], MongoQueryMode.NativeOnly, nameof(First_returns_first_and_goes_native));
        var first = db.Entities.OrderBy(e => e.Value).First();
        Assert.Equal(1, first.Value); // succeeds under NativeOnly => went native
    }

    [Fact]
    public void FirstOrDefault_returns_first_and_goes_native()
    {
        using var db = CreateContext([1, 2, 3], MongoQueryMode.NativeOnly, nameof(FirstOrDefault_returns_first_and_goes_native));
        var first = db.Entities.OrderBy(e => e.Value).FirstOrDefault();
        Assert.NotNull(first);
        Assert.Equal(1, first!.Value);
    }

    [Fact]
    public void Single_returns_match_and_goes_native()
    {
        using var db = CreateContext([1, 2, 3], MongoQueryMode.NativeOnly, nameof(Single_returns_match_and_goes_native));
        var single = db.Entities.Where(e => e.Value == 2).Single();
        Assert.Equal(2, single.Value);
    }

    [Fact]
    public void SingleOrDefault_returns_match_and_goes_native()
    {
        using var db = CreateContext([1, 2, 3], MongoQueryMode.NativeOnly, nameof(SingleOrDefault_returns_match_and_goes_native));
        var single = db.Entities.Where(e => e.Value == 2).SingleOrDefault();
        Assert.NotNull(single);
        Assert.Equal(2, single!.Value);
    }

    [Fact]
    public void First_on_empty_throws_sequence_contains_no_elements()
    {
        using var db = CreateContext([], MongoQueryMode.Native, nameof(First_on_empty_throws_sequence_contains_no_elements));
        var ex = Assert.Throws<InvalidOperationException>(() => db.Entities.First());
        Assert.Contains("no elements", ex.Message);
    }

    [Fact]
    public void FirstOrDefault_on_empty_returns_null()
    {
        using var db = CreateContext([], MongoQueryMode.Native, nameof(FirstOrDefault_on_empty_returns_null));
        Assert.Null(db.Entities.FirstOrDefault());
    }

    [Fact]
    public void Single_on_empty_throws_sequence_contains_no_elements()
    {
        using var db = CreateContext([], MongoQueryMode.Native, nameof(Single_on_empty_throws_sequence_contains_no_elements));
        var ex = Assert.Throws<InvalidOperationException>(() => db.Entities.Single());
        Assert.Contains("no elements", ex.Message);
    }

    [Fact]
    public void SingleOrDefault_on_empty_returns_null()
    {
        using var db = CreateContext([], MongoQueryMode.Native, nameof(SingleOrDefault_on_empty_returns_null));
        Assert.Null(db.Entities.SingleOrDefault());
    }

    [Fact]
    public void Single_with_two_matches_throws_more_than_one()
    {
        using var db = CreateContext([5, 5], MongoQueryMode.Native, nameof(Single_with_two_matches_throws_more_than_one));
        var ex = Assert.Throws<InvalidOperationException>(() => db.Entities.Where(e => e.Value == 5).Single());
        Assert.Contains("more than one", ex.Message);
    }

    [Fact]
    public void SingleOrDefault_with_two_matches_throws_more_than_one()
    {
        using var db = CreateContext([5, 5], MongoQueryMode.Native, nameof(SingleOrDefault_with_two_matches_throws_more_than_one));
        var ex = Assert.Throws<InvalidOperationException>(() => db.Entities.Where(e => e.Value == 5).SingleOrDefault());
        Assert.Contains("more than one", ex.Message);
    }

    [Fact]
    public void First_after_Take_falls_back()
    {
        using var db = CreateContext([1, 2, 3], MongoQueryMode.NativeOnly, nameof(First_after_Take_falls_back));
        // Limit already populated by Take => reducer not representable => NativeOnly throws.
        Assert.Throws<NativeTranslationNotSupportedException>(() => db.Entities.Take(2).First());
    }

    // ── Scalar-aggregate native path (EF-SP4 Task 5) ────────────────────────────────────────────

    [Fact]
    public void Count_goes_native_and_counts()
    {
        using var db = CreateContext([1, 2, 3], MongoQueryMode.NativeOnly, nameof(Count_goes_native_and_counts));
        Assert.Equal(3, db.Entities.Count()); // succeeds under NativeOnly => went native
    }

    [Fact]
    public void LongCount_goes_native_and_counts()
    {
        using var db = CreateContext([1, 2, 3], MongoQueryMode.NativeOnly, nameof(LongCount_goes_native_and_counts));
        Assert.Equal(3L, db.Entities.LongCount());
    }

    [Fact]
    public void Count_on_empty_is_zero()
    {
        using var db = CreateContext([], MongoQueryMode.Native, nameof(Count_on_empty_is_zero));
        Assert.Equal(0, db.Entities.Count());
    }

    [Fact]
    public void Sum_on_empty_is_zero()
    {
        using var db = CreateContext([], MongoQueryMode.Native, nameof(Sum_on_empty_is_zero));
        Assert.Equal(0, db.Entities.Sum(e => e.Value));
    }

    [Fact]
    public void Any_true_and_false()
    {
        using var db = CreateContext([1], MongoQueryMode.NativeOnly, nameof(Any_true_and_false) + "1");
        Assert.True(db.Entities.Any());
        using var empty = CreateContext([], MongoQueryMode.Native, nameof(Any_true_and_false) + "2");
        Assert.False(empty.Entities.Any());
    }

    // FIXED (EF-335, see task-3-report.md): comparison-predicate All(...) now goes native. Previously
    // NativeCardinalityBinder.TryBindAggregate negated the predicate at the LINQ level via
    // Expression.Not(predicate.Body), producing a MongoUnaryExpression{Not} over a MongoBinaryExpression
    // comparison — a node MongoQueryLanguageRenderer.RenderUnary had no case for pre-EF-322-Task-1, so it
    // threw NativeTranslationNotSupportedException at RENDER time and the gate silently fell back. The
    // binder now negates the TRANSLATED tree via MongoExpressionNegator, whose $not-wrapped-comparison
    // rendering (added by EF-322 Task 1) makes this natively representable.
    [Fact]
    public void All_over_empty_is_true()
    {
        using var db = CreateContext([], MongoQueryMode.NativeOnly, nameof(All_over_empty_is_true));
        Assert.True(db.Entities.All(e => e.Value > 0)); // vacuously true over an empty set — succeeds under NativeOnly
    }

    [Fact]
    public void All_with_failing_element_is_false()
    {
        // EF-335 flip: this used to lock in the documented fallback gap (asserted NativeOnly threw). The
        // comparison predicate now goes native, so NativeOnly succeeds — asserting the correct value here
        // proves the native path (not just "no exception"), per the task-3 instruction to invert a flipped
        // fallback assertion rather than delete it.
        using var db = CreateContext([1, -1, 2], MongoQueryMode.NativeOnly, nameof(All_with_failing_element_is_false));
        Assert.False(db.Entities.All(e => e.Value > 0)); // -1 fails the predicate => All is false
    }

    [Fact]
    public void All_over_bare_bool_goes_native()
    {
        // A bare-bool predicate (no comparison to negate) does not hit the Not-over-comparison gap, so it
        // should go native under NativeOnly.
        using var active = CreateBoolContext([true, true], MongoQueryMode.NativeOnly, nameof(All_over_bare_bool_goes_native));
        Assert.True(active.Entities.All(e => e.IsActive));
    }

    // ── Top-level All() negates via MongoExpressionNegator (EF-335) ─────────────────────────────────
    //
    // Local helpers mirroring NativeOwnedCollectionPredicateTests.AssertNativeAndParity: run the SAME
    // aggregate under NativeOnly (the only reliable "went native" signal — see the Query AGENTS.md pitfall)
    // and under DriverLinq (the pre-existing, trusted oracle for a top-level All over a flat entity), assert
    // the two agree, and return the value so each test can additionally pin the expected answer — a
    // predicate that is trivially true/false for every seeded row would pass even against a broken negation,
    // so every seed below is chosen to make the All() answer depend on the negation being EXACT.

    private bool AssertAggregateNativeAndParity<TSeed>(
        TSeed[] seed, Func<TSeed, ValueEntity> project, Func<IQueryable<ValueEntity>, bool> query,
        [CallerMemberName] string testName = "")
    {
        using var nativeOnly = CreateContext(seed, project, MongoQueryMode.NativeOnly, testName + "native");
        var nativeResult = query(nativeOnly.Entities); // succeeds only if the aggregate went native

        using var driver = CreateContext(seed, project, MongoQueryMode.DriverLinq, testName + "driver");
        var driverResult = query(driver.Entities);

        Assert.Equal(driverResult, nativeResult);
        return nativeResult;
    }

    private bool AssertAggregateFallsBackGracefully<TSeed>(
        TSeed[] seed, Func<TSeed, ValueEntity> project, Func<IQueryable<ValueEntity>, bool> query,
        [CallerMemberName] string testName = "")
    {
        using var nativeOnly = CreateContext(seed, project, MongoQueryMode.NativeOnly, testName + "no");
        Assert.Throws<NativeTranslationNotSupportedException>(() => query(nativeOnly.Entities));

        using var native = CreateContext(seed, project, MongoQueryMode.Native, testName + "native");
        using var driver = CreateContext(seed, project, MongoQueryMode.DriverLinq, testName + "driver");
        var nativeResult = query(native.Entities);
        Assert.Equal(query(driver.Entities), nativeResult);
        return nativeResult;
    }

    private static ValueEntity NumericProject(int v) => new() { Id = ObjectId.GenerateNewId(), Value = v };

    [Fact]
    public void All_with_a_relational_predicate_goes_native()
    {
        // EF-335: previously the negation translated but RenderUnary threw, so the gate fell back to
        // driver-LINQ (throwing under NativeOnly). The negator now renders { Value: { $not: { $gt: 3 } } }.
        // Seed has TWO rows failing Value > 3 (1 and 2) and one passing (5) — discriminating because a
        // negation that is merely close (e.g. wrong direction, or an inverted-instead-of-$not-wrapped
        // relational operator) would flip the surviving-row count and the final All boolean.
        var result = AssertAggregateNativeAndParity([1, 2, 5], NumericProject, q => q.All(e => e.Value > 3));
        Assert.False(result); // rows 1 and 2 fail the predicate
    }

    [Fact]
    public void All_with_a_conjunctive_predicate_goes_native_via_de_morgan()
    {
        // De Morgan: All(p1 && p2) negates to NOT(p1) OR NOT(p2). Rows: (5,"A") satisfies both; (5,"B") and
        // (1,"A") each satisfy exactly ONE of the two conjuncts. A correct OR-negation flags both violators
        // (2 survivors => All=false); a buggy AND-negation would require BOTH conjuncts to fail
        // simultaneously — true for neither row — silently missing both violators (0 survivors => All=true,
        // wrong). This is the discriminating property: AND vs. OR flips the final boolean, not just a count.
        var result = AssertAggregateNativeAndParity(
            [(5, "A"), (5, "B"), (1, "A")],
            t => new ValueEntity { Id = ObjectId.GenerateNewId(), Value = t.Item1, Name = t.Item2 },
            q => q.All(e => e.Value > 2 && e.Name == "A"));
        Assert.False(result);
    }

    [Fact]
    public void All_with_a_disjunctive_predicate_goes_native_via_de_morgan()
    {
        // De Morgan: All(p1 || p2) negates to NOT(p1) AND NOT(p2). Every row satisfies the disjunction, but
        // each via a DIFFERENT single disjunct: (5,"B") only via Value>2, (1,"A") only via Name=="A". A
        // correct AND-negation requires BOTH negated conjuncts to hold for a row to survive — never true
        // here — so 0 survivors => All=true (correct). A buggy OR-negation would let either negated conjunct
        // alone qualify a row as a survivor — true for both rows — silently reporting All=false (wrong).
        // Same discriminating property as the conjunctive case, mirrored: AND vs. OR flips the boolean.
        var result = AssertAggregateNativeAndParity(
            [(5, "B"), (1, "A"), (5, "A")],
            t => new ValueEntity { Id = ObjectId.GenerateNewId(), Value = t.Item1, Name = t.Item2 },
            q => q.All(e => e.Value > 2 || e.Name == "A"));
        Assert.True(result);
    }

    [Fact]
    public void All_returning_false_is_still_correct_when_one_row_fails_the_predicate()
    {
        // The presence-only contract: the negated predicate is pushed as a $match, and ANY surviving row
        // means All is false. Two rows (5, 5) pass Value > 2 and exactly ONE (1) fails — discriminating for
        // the "exactly one violator" edge, distinct from the multi-violator case in the relational test
        // above: it proves the $match/$count presence check correctly detects a SINGLE surviving row rather
        // than over/under-counting.
        var result = AssertAggregateNativeAndParity([5, 5, 1], NumericProject, q => q.All(e => e.Value > 2));
        Assert.False(result);
    }

    [Fact]
    public void All_with_a_field_to_field_predicate_still_falls_back()
    {
        // No exact complement (a field-to-field comparison is not query-dialect-renderable, so
        // MongoExpressionNegator.TryNegate declines) ⇒ the binder declines ⇒ graceful fallback: correct
        // under Native/DriverLinq, throws only under NativeOnly. This is the boundary of what the negator
        // admits. Values chosen so the predicate is neither trivially true nor trivially false: (5,3)
        // satisfies Value > OtherValue, (2,2) and (1,4) do not.
        var result = AssertAggregateFallsBackGracefully(
            [(5, 3), (2, 2), (1, 4)],
            t => new ValueEntity { Id = ObjectId.GenerateNewId(), Value = t.Item1, OtherValue = t.Item2 },
            q => q.All(e => e.Value > e.OtherValue));
        Assert.False(result); // (2,2) and (1,4) both fail the predicate
    }

    [Fact]
    public void All_with_a_regex_predicate_is_unchanged_by_the_negator_swap()
    {
        // Already native BEFORE this slice (MongoExpressionTranslator's Not arm flips MongoRegexExpression.
        // Negated directly, independent of NativeCardinalityBinder). Pinned so the swap from Expression.Not
        // to TryNegate is proven behavior-preserving, not just additive: "Banana" does not start with "A", so
        // the predicate is not vacuously true, and the fixed Value=0 default (unused here) does not affect it.
        var result = AssertAggregateNativeAndParity(
            ["Apple", "Avocado", "Banana"],
            s => new ValueEntity { Id = ObjectId.GenerateNewId(), Name = s },
            q => q.All(e => e.Name.StartsWith("A")));
        Assert.False(result); // "Banana" fails the predicate
    }

    [Fact]
    public void Min_on_empty_nonnullable_throws()
    {
        using var db = CreateContext([], MongoQueryMode.Native, nameof(Min_on_empty_nonnullable_throws));
        var ex = Assert.Throws<InvalidOperationException>(() => db.Entities.Min(e => e.Value));
        Assert.Contains("no elements", ex.Message);
    }

    [Fact]
    public void Max_and_average_go_native()
    {
        using var db = CreateContext([2, 4, 6], MongoQueryMode.NativeOnly, nameof(Max_and_average_go_native));
        Assert.Equal(6, db.Entities.Max(e => e.Value));
        Assert.Equal(4.0, db.Entities.Average(e => e.Value));
    }

    [Fact]
    public void Computed_selector_sum_falls_back()
    {
        using var db = CreateContext([1, 2], MongoQueryMode.NativeOnly, nameof(Computed_selector_sum_falls_back));
        Assert.Throws<NativeTranslationNotSupportedException>(() => db.Entities.Sum(e => e.Value * 2));
    }

    // ── Non-int scalar coverage for DeserializeScalar<TResult> (EF-SP4 Task 5 review fix M1) ───────

    [Fact]
    public void Sum_over_decimal_goes_native()
    {
        using var db = CreateDecimalContext([1.5m, 2.25m, 3.0m], MongoQueryMode.NativeOnly, nameof(Sum_over_decimal_goes_native));
        Assert.Equal(6.75m, db.Entities.Sum(e => e.DecimalValue)); // succeeds under NativeOnly => went native
    }

    [Fact]
    public void Average_over_decimal_goes_native()
    {
        using var db = CreateDecimalContext([1.0m, 2.0m, 3.0m], MongoQueryMode.NativeOnly, nameof(Average_over_decimal_goes_native));
        Assert.Equal(2.0m, db.Entities.Average(e => e.DecimalValue));
    }

    [Fact]
    public void Min_max_average_over_nullable_on_empty_return_null()
    {
        using var db = CreateNullableContext([], MongoQueryMode.Native, nameof(Min_max_average_over_nullable_on_empty_return_null));
        Assert.Null(db.Entities.Min(e => e.NullableValue));
        Assert.Null(db.Entities.Max(e => e.NullableValue));
        Assert.Null(db.Entities.Average(e => e.NullableValue));
    }

    [Fact]
    public void Min_max_average_over_nullable_with_values_go_native()
    {
        using var db = CreateNullableContext([2, 4, 6], MongoQueryMode.NativeOnly, nameof(Min_max_average_over_nullable_with_values_go_native));
        Assert.Equal(2, db.Entities.Min(e => e.NullableValue));
        Assert.Equal(6, db.Entities.Max(e => e.NullableValue));
        Assert.Equal(4.0, db.Entities.Average(e => e.NullableValue));
    }

    [Fact]
    public void Min_max_average_over_nullable_all_null_rows_return_null()
    {
        // Rows exist, but every row's NullableValue is null: $group{v:{$min/$max/$avg:"$NullableValue"}}
        // yields ONE document with v: null (the empty-input path is NOT taken), so this exercises
        // DeserializeScalar's handling of a non-empty aggregate whose accumulator result is BSON null.
        using var db = CreateNullableContext(
            [null, null, null],
            MongoQueryMode.NativeOnly,
            nameof(Min_max_average_over_nullable_all_null_rows_return_null));

        Assert.Null(db.Entities.Min(e => e.NullableValue));
        Assert.Null(db.Entities.Max(e => e.NullableValue));
        Assert.Null(db.Entities.Average(e => e.NullableValue));
    }

    // ── Aggregate-with-predicate after paging now goes native (EF-347 Task 3) ──────────────────────
    // The paging guard in NativeCardinalityBinder.TryBindAggregate (originally added by EF-SP4 Task 6 to
    // force fallback here) is gone: AddPredicateConjunct always ANDs the injected predicate into — or
    // appends it after — the TAIL of the ordered op list, i.e. AFTER any $skip/$limit already recorded,
    // so it can never hoist ahead of the paging. NativeOnly succeeding (rather than throwing) is the proof.

    [Fact]
    public void All_after_Take_goes_native_and_is_correct()
    {
        // Bare-bool predicate: goes native as a standalone All, and Take(2).All(...) now goes native too.
        // First two elements are both active; the third (excluded by Take(2)) is not, so the whole-set
        // answer would be false while the correct first-two-only answer is true.
        using var db = CreateBoolContext(
            [true, true, false], MongoQueryMode.Native, nameof(All_after_Take_goes_native_and_is_correct) + "native");
        Assert.True(db.Entities.Take(2).All(e => e.IsActive));

        using var nativeOnly = CreateBoolContext(
            [true, true, false], MongoQueryMode.NativeOnly, nameof(All_after_Take_goes_native_and_is_correct) + "only");
        Assert.True(nativeOnly.Entities.Take(2).All(e => e.IsActive)); // succeeds under NativeOnly => went native
    }

    [Fact]
    public void Count_after_Take_stays_native()
    {
        // A plain aggregate that injects no predicate ($limit -> $count) is unaffected by the guard and
        // must remain native even after a preceding Take.
        using var db = CreateContext([1, 2, 3], MongoQueryMode.NativeOnly, nameof(Count_after_Take_stays_native));
        Assert.Equal(2, db.Entities.Take(2).Count()); // succeeds under NativeOnly => went native
    }

    // ── Review-response coverage (EF-336) ───────────────────────────────────────────────────────

    [Fact]
    public void All_bare_bool_with_failing_row_is_false_native()
    {
        // A bare-bool predicate has no comparison to negate, so it goes native under NativeOnly even
        // when a ¬pred row survives (the false-result branch of native All).
        using var db = CreateBoolContext(
            [true, false], MongoQueryMode.NativeOnly, nameof(All_bare_bool_with_failing_row_is_false_native));
        Assert.False(db.Entities.All(e => e.IsActive));
    }

    [Fact]
    public void All_bare_bool_all_true_is_true_native()
    {
        using var db = CreateBoolContext(
            [true, true], MongoQueryMode.NativeOnly, nameof(All_bare_bool_all_true_is_true_native));
        Assert.True(db.Entities.All(e => e.IsActive));
    }

    [Fact]
    public void Select_projection_First_goes_native()
    {
        using var db = CreateContext([1, 2, 3], MongoQueryMode.NativeOnly, nameof(Select_projection_First_goes_native));
        var first = db.Entities.OrderBy(e => e.Value).Select(e => new { e.Value }).First();
        Assert.Equal(1, first.Value); // succeeds under NativeOnly => went native
    }

    [Fact]
    public void Select_projection_FirstOrDefault_over_empty_is_null()
    {
        using var db = CreateContext([], MongoQueryMode.Native, nameof(Select_projection_FirstOrDefault_over_empty_is_null));
        var first = db.Entities.Select(e => new { e.Value }).FirstOrDefault();
        Assert.Null(first);
    }

    // FLIPPED by EF-322 step 3a: a bare-scalar projection now pushes a $project down, so a cardinality operator
    // composed after one goes native too. Neither of that slice's two deliberate narrowings (the projected set-op
    // operand and Distinct) sits on the cardinality path, so this is an INCIDENTAL widening of the boundary
    // slice rather than the composition work — worth its own pin for exactly that reason. Note the lowerer
    // appends Projection LAST, so the emitted order is $sort, $limit, $project: the reducer runs before the
    // projection, which is 1:1 with respect to rows and is the ordering a wrapped projection already had.
    [Fact]
    public void Select_bare_scalar_First_goes_native()
    {
        using var nativeOnly = CreateContext(
            [1, 2, 3], MongoQueryMode.NativeOnly, nameof(Select_bare_scalar_First_goes_native) + "only");
        Assert.Equal(1, nativeOnly.Entities.OrderBy(e => e.Value).Select(e => e.Value).First());

        using var native = CreateContext(
            [1, 2, 3], MongoQueryMode.Native, nameof(Select_bare_scalar_First_goes_native) + "native");
        Assert.Equal(1, native.Entities.OrderBy(e => e.Value).Select(e => e.Value).First());
    }

    [Fact]
    public void Min_max_over_string_go_native()
    {
        using var db = CreateStringContext(["banana", "apple", "cherry"], MongoQueryMode.NativeOnly, nameof(Min_max_over_string_go_native));
        Assert.Equal("apple", db.Entities.Min(e => e.Name));
        Assert.Equal("cherry", db.Entities.Max(e => e.Name));
    }

    [Fact]
    public void Filtered_sum_and_count_go_native()
    {
        using var db = CreateContext([1, 2, 3], MongoQueryMode.NativeOnly, nameof(Filtered_sum_and_count_go_native));
        Assert.Equal(5, db.Entities.Where(e => e.Value > 1).Sum(e => e.Value));
        Assert.Equal(2, db.Entities.Where(e => e.Value > 1).Count());
    }
}
