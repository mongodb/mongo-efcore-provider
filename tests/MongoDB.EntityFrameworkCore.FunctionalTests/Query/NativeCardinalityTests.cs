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
/// Native reducers and scalar aggregates. Reducers synthesize a native <c>$limit</c> and EF Core's cardinality
/// reduction runs over the result (empty ⇒ throw/null, more-than-one ⇒ throw for Single*).
/// <see cref="MongoQueryMode.NativeOnly"/> is the "went native" signal, since the MQL matches the fallback's.
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
        public float FloatValue { get; set; }
        public float? NullableFloatValue { get; set; }
        public decimal? NullableDecimalValue { get; set; }
        public bool IsActive { get; set; }
        public string Name { get; set; } = "";

        // Used only by the field-to-field All() fallback test; defaults to 0 elsewhere.
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

    // Reducer composed after Take/Skip: the reducer appends its $limit to the tail, and consecutive $limit
    // stages narrow monotonically. Tests are chosen so the order of the two limits is observable.

    [Fact]
    public void First_after_Take_now_goes_native()
    {
        using var db = CreateContext([1, 2, 3, 4, 5], MongoQueryMode.NativeOnly, nameof(First_after_Take_now_goes_native));
        // Take(3) then First => [$limit 3, $limit 1] => the first of the first three.
        Assert.Equal(1, db.Entities.OrderBy(e => e.Value).Take(3).First().Value); // NativeOnly succeeds => native
    }

    [Fact]
    public void First_after_Skip_then_Take_composes_in_recorded_order()
    {
        // Correct emission is [$sort, $skip 2, $limit 2, $limit 1] => row 3. Hoisting the reducer's $limit
        // ahead of the paging would yield no rows and throw.
        using var nativeOnly = CreateContext(
            [1, 2, 3, 4, 5], MongoQueryMode.NativeOnly, nameof(First_after_Skip_then_Take_composes_in_recorded_order) + "only");
        Assert.Equal(3, nativeOnly.Entities.OrderBy(e => e.Value).Skip(2).Take(2).First().Value);

        using var driver = CreateContext(
            [1, 2, 3, 4, 5], MongoQueryMode.DriverLinq, nameof(First_after_Skip_then_Take_composes_in_recorded_order) + "driver");
        Assert.Equal(3, driver.Entities.OrderBy(e => e.Value).Skip(2).Take(2).First().Value); // driver-LINQ oracle agrees
    }

    [Fact]
    public void FirstOrDefault_after_Take_that_selects_nothing_is_null()
    {
        // Take(2) keeps rows 1,2; the Where then rejects both. The reducer's $limit composes onto an empty
        // stream and FirstOrDefault must answer null (not throw, and not see row 5, which Take excluded).
        using var db = CreateContext(
            [1, 2, 5], MongoQueryMode.NativeOnly, nameof(FirstOrDefault_after_Take_that_selects_nothing_is_null));
        Assert.Null(db.Entities.OrderBy(e => e.Value).Take(2).Where(e => e.Value == 5).FirstOrDefault());
    }

    [Fact]
    public void Single_after_Take_of_two_still_throws_more_than_one()
    {
        // Single appends $limit 2: [$limit 2, $limit 2] => two rows => throws. Replacing the Take's limit with
        // 1 would wrongly return a value.
        using var db = CreateContext(
            [1, 2, 3], MongoQueryMode.NativeOnly, nameof(Single_after_Take_of_two_still_throws_more_than_one));
        var ex = Assert.Throws<InvalidOperationException>(() => db.Entities.OrderBy(e => e.Value).Take(2).Single());
        Assert.Contains("more than one", ex.Message);
    }

    [Fact]
    public void Single_after_Take_of_one_returns_that_element()
    {
        // [$limit 1, $limit 2] => one row. With the test above, proves the limits take the minimum rather than
        // one clobbering the other.
        using var db = CreateContext(
            [7, 8, 9], MongoQueryMode.NativeOnly, nameof(Single_after_Take_of_one_returns_that_element));
        Assert.Equal(7, db.Entities.OrderBy(e => e.Value).Take(1).Single().Value);
    }

    [Fact]
    public void First_after_Take_zero_throws_no_elements()
    {
        // Take(0) is normalized to an always-false $match, so the reducer sees an empty stream and throws
        // "no elements", not the ArgumentOutOfRangeException a raw $limit:0 would raise.
        using var db = CreateContext([1, 2, 3], MongoQueryMode.NativeOnly, nameof(First_after_Take_zero_throws_no_elements));
        var ex = Assert.Throws<InvalidOperationException>(() => db.Entities.Take(0).First());
        Assert.Contains("no elements", ex.Message);
    }

    [Fact]
    public void First_after_Skip_only_is_unaffected()
    {
        // Skip alone, as a baseline for the Take cases above.
        using var db = CreateContext([1, 2, 3], MongoQueryMode.NativeOnly, nameof(First_after_Skip_only_is_unaffected));
        Assert.Equal(2, db.Entities.OrderBy(e => e.Value).Skip(1).First().Value);
    }

    // Scalar aggregates.

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

    // Comparison-predicate All(...) goes native: the binder negates the translated tree via
    // MongoExpressionNegator ($not-wrapped comparison).
    [Fact]
    public void All_over_empty_is_true()
    {
        using var db = CreateContext([], MongoQueryMode.NativeOnly, nameof(All_over_empty_is_true));
        Assert.True(db.Entities.All(e => e.Value > 0)); // vacuously true over an empty set — succeeds under NativeOnly
    }

    [Fact]
    public void All_with_failing_element_is_false()
    {
        // Asserting the value, not just no exception, under NativeOnly.
        using var db = CreateContext([1, -1, 2], MongoQueryMode.NativeOnly, nameof(All_with_failing_element_is_false));
        Assert.False(db.Entities.All(e => e.Value > 0)); // -1 fails the predicate => All is false
    }

    [Fact]
    public void All_over_bare_bool_goes_native()
    {
        // A bare-bool predicate has no comparison to negate.
        using var active = CreateBoolContext([true, true], MongoQueryMode.NativeOnly, nameof(All_over_bare_bool_goes_native));
        Assert.True(active.Entities.All(e => e.IsActive));
    }

    // Top-level All() negates via MongoExpressionNegator. Helpers run NativeOnly and DriverLinq (the oracle),
    // assert they agree, and return the value. Seeds make the answer depend on the negation being exact.

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
        // Renders { Value: { $not: { $gt: 3 } } }. Two rows fail Value > 3 and one passes, so a wrong-direction
        // or inverted-instead-of-$not-wrapped negation would flip the result.
        var result = AssertAggregateNativeAndParity([1, 2, 5], NumericProject, q => q.All(e => e.Value > 3));
        Assert.False(result); // rows 1 and 2 fail the predicate
    }

    [Fact]
    public void All_with_a_conjunctive_predicate_goes_native_via_de_morgan()
    {
        // De Morgan: All(p1 && p2) negates to NOT(p1) OR NOT(p2). Two rows each fail exactly one conjunct, so
        // a buggy AND-negation would miss both and wrongly answer true.
        var result = AssertAggregateNativeAndParity(
            [(5, "A"), (5, "B"), (1, "A")],
            t => new ValueEntity { Id = ObjectId.GenerateNewId(), Value = t.Item1, Name = t.Item2 },
            q => q.All(e => e.Value > 2 && e.Name == "A"));
        Assert.False(result);
    }

    [Fact]
    public void All_with_a_disjunctive_predicate_goes_native_via_de_morgan()
    {
        // De Morgan: All(p1 || p2) negates to NOT(p1) AND NOT(p2). Each row satisfies a different single
        // disjunct, so a buggy OR-negation would wrongly answer false.
        var result = AssertAggregateNativeAndParity(
            [(5, "B"), (1, "A"), (5, "A")],
            t => new ValueEntity { Id = ObjectId.GenerateNewId(), Value = t.Item1, Name = t.Item2 },
            q => q.All(e => e.Value > 2 || e.Name == "A"));
        Assert.True(result);
    }

    [Fact]
    public void All_returning_false_is_still_correct_when_one_row_fails_the_predicate()
    {
        // Any row surviving the negated $match means false; pins the single-violator edge.
        var result = AssertAggregateNativeAndParity([5, 5, 1], NumericProject, q => q.All(e => e.Value > 2));
        Assert.False(result);
    }

    [Fact]
    public void All_with_a_field_to_field_predicate_still_falls_back()
    {
        // A field-to-field comparison has no exact complement, so TryNegate declines and the query falls back
        // gracefully. Values make the predicate neither trivially true nor false.
        var result = AssertAggregateFallsBackGracefully(
            [(5, 3), (2, 2), (1, 4)],
            t => new ValueEntity { Id = ObjectId.GenerateNewId(), Value = t.Item1, OtherValue = t.Item2 },
            q => q.All(e => e.Value > e.OtherValue));
        Assert.False(result); // (2,2) and (1,4) both fail the predicate
    }

    [Fact]
    public void All_with_a_regex_predicate_is_unchanged_by_the_negator_swap()
    {
        // Regex negation flips MongoRegexExpression.Negated. "Banana" doesn't start with "A", so the predicate
        // isn't vacuously true.
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
    public void Computed_selector_sum_goes_native()
    {
        using var db = CreateContext([1, 2], MongoQueryMode.NativeOnly, nameof(Computed_selector_sum_goes_native));
        Assert.Equal(6, db.Entities.Sum(e => e.Value * 2));
    }

    // Non-int scalar coverage for DeserializeScalar<TResult>.

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

    // EF-228: Average over float / float? / decimal? / int?. The oracle is LINQ-to-objects, which accumulates in
    // double and narrows to float (so 1.6 and 1.7 average to 1.6500001f, not the float-arithmetic 1.65f).

    private static readonly float[] FloatSeed = [1.6f, 1.7f];

    private SingleEntityDbContext<ValueEntity> CreateFloatContext(float?[] seed, MongoQueryMode mode, string name)
        => CreateContext(
            seed,
            v => new ValueEntity { Id = ObjectId.GenerateNewId(), FloatValue = v ?? 0f, NullableFloatValue = v, Name = "g" },
            mode, name);

    [Fact]
    public void Average_over_float_goes_native_and_matches_linq_to_objects()
    {
        using var db = CreateFloatContext([1.6f, 1.7f], MongoQueryMode.NativeOnly, nameof(Average_over_float_goes_native_and_matches_linq_to_objects));
        var expected = FloatSeed.Average();
        Assert.Equal(1.6500001f, expected);
        Assert.Equal(expected, db.Entities.Average(e => e.FloatValue));
    }

    [Fact]
    public void Average_over_nullable_float_goes_native_and_matches_linq_to_objects()
    {
        using var db = CreateFloatContext([1.6f, null, 1.7f], MongoQueryMode.NativeOnly, nameof(Average_over_nullable_float_goes_native_and_matches_linq_to_objects));
        Assert.Equal(1.6500001f, db.Entities.Average(e => e.NullableFloatValue));
    }

    [Fact]
    public void Average_over_nullable_float_with_no_values_is_null()
    {
        using var db = CreateFloatContext([null, null], MongoQueryMode.NativeOnly, nameof(Average_over_nullable_float_with_no_values_is_null));
        Assert.Null(db.Entities.Average(e => e.NullableFloatValue));
    }

    [Fact]
    public void Grouped_average_over_float_goes_native_and_matches_linq_to_objects()
    {
        using var db = CreateFloatContext([1.6f, 1.7f], MongoQueryMode.NativeOnly, nameof(Grouped_average_over_float_goes_native_and_matches_linq_to_objects));
        var result = db.Entities.GroupBy(e => e.Name).Select(g => new { g.Key, Avg = g.Average(x => x.FloatValue) }).ToList();
        var row = Assert.Single(result);
        Assert.Equal("g", row.Key);
        Assert.Equal(1.6500001f, row.Avg);
    }

    [Fact]
    public void Average_over_nullable_decimal_goes_native()
    {
        using var db = CreateContext(
            new decimal?[] { 1.0m, null, 2.0m },
            v => new ValueEntity { Id = ObjectId.GenerateNewId(), NullableDecimalValue = v },
            MongoQueryMode.NativeOnly, nameof(Average_over_nullable_decimal_goes_native));
        Assert.Equal(1.5m, db.Entities.Average(e => e.NullableDecimalValue));
    }

    [Fact]
    public void Average_over_nullable_int_ignores_nulls()
    {
        // Nulls are skipped, not counted as zero: (2 + 6) / 2 = 4, not 8 / 3.
        using var db = CreateNullableContext([2, null, 6], MongoQueryMode.NativeOnly, nameof(Average_over_nullable_int_ignores_nulls));
        Assert.Equal(4.0, db.Entities.Average(e => e.NullableValue));
    }

    [Fact]
    public void Average_over_float_under_explicit_driver_linq_throws_truncation()
    {
        // EF-228 DriverLinq leg, NOT fixed: the driver's float Average reads the double result through a
        // float serializer and throws. Pinned so a change is noticed; driver-LINQ is being retired.
        using var db = CreateFloatContext([1.6f, 1.7f], MongoQueryMode.DriverLinq, nameof(Average_over_float_under_explicit_driver_linq_throws_truncation));
        Assert.Throws<TruncationException>(() => db.Entities.Average(e => e.FloatValue));
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
        // Every NullableValue is null: $group yields one document with v: null (not the empty-input path).
        using var db = CreateNullableContext(
            [null, null, null],
            MongoQueryMode.NativeOnly,
            nameof(Min_max_average_over_nullable_all_null_rows_return_null));

        Assert.Null(db.Entities.Min(e => e.NullableValue));
        Assert.Null(db.Entities.Max(e => e.NullableValue));
        Assert.Null(db.Entities.Average(e => e.NullableValue));
    }

    // Aggregate-with-predicate after paging: AddPredicateConjunct targets the tail of the op list, so the
    // predicate can't hoist ahead of the paging.

    [Fact]
    public void All_after_Take_goes_native_and_is_correct()
    {
        // The first two rows are active and the third (excluded by Take(2)) isn't, so ignoring the Take would
        // answer false.
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
        // No injected predicate: $limit -> $count.
        using var db = CreateContext([1, 2, 3], MongoQueryMode.NativeOnly, nameof(Count_after_Take_stays_native));
        Assert.Equal(2, db.Entities.Take(2).Count()); // succeeds under NativeOnly => went native
    }

    [Fact]
    public void All_bare_bool_with_failing_row_is_false_native()
    {
        // The false-result branch of a native bare-bool All.
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

    // A bare-scalar projection pushes down a $project, so a reducer after it goes native. The lowerer emits
    // $sort, $limit, $project: the reducer runs before the (row-preserving) projection.
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
