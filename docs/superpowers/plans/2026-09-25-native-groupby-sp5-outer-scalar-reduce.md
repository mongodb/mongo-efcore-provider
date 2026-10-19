# Native GroupBy SP5: Outer Scalar Reduce Over a Per-Group Projection Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Move `MinMax_after_GroupBy_aggregate` and `LongCount_after_GroupBy_aggregate` off the driver-LINQ
fallback — 2 `NorthwindGroupByQueryMongoTest` methods, dropping the `NativeOnly` GroupBy-class failure count
from 30 (SP4's ending count) to 28.

**Architecture:** The design doc (see Spec) bucketed both tests under one "outer reduce" gap, but re-checking
each test's actual EF Core base-class source (`test/EFCore.Specification.Tests/Query/
NorthwindGroupByQueryTestBase.cs` at tag `v10.0.11`) plus a scratch probe against this branch's own binder
shows they need **two independent, narrower fixes**:

1. **`LongCount_after_GroupBy_aggregate`**'s actual shape is `GroupBy(key).Select(g =>
   g.Where(pred).Count()).LongCount()` — a **filtered COUNT with no selector** as the bare Select body. Probed
   directly: `NativeGroupByBinder.TryBindFilteredAccumulator` only recognizes `g.Where(pred).Sum/Min/Max/
   Average(selector)` (`call.Arguments.Count == 2`, a selector-bearing op) — `g.Where(pred).Count()`/
   `.LongCount()` has `call.Arguments.Count == 1` and falls straight through to the ordinary bare-`g.Count()`
   arm below, which requires the call's OWN source to be `g` directly, not `g.Where(...)`, so it declines. Once
   this Select binds, the outer `.LongCount()` needs **no new plumbing at all** —
   `NativeCardinalityBinder.TryBindAggregate`'s existing `isPostGroupBySelectAggregate` bucket already accepts
   a selector-less Count/LongCount/Any/All composed after `GroupBy(key).Select(aggregate)`.
2. **`MinMax_after_GroupBy_aggregate`**'s shape (`GroupBy(key).Select(g => g.Sum(gg => gg.OrderID)).Min()`/
   `.Max()`) matches the design doc's own bucket exactly, but `TryBindAggregate`'s Sum/Min/Max/Average branch
   requires a non-null `selector` (`if (selector is null || !translator.TryTranslateValue(...)) return false;`)
   — a bare, no-selector `.Min()`/`.Max()` (the ONLY shape reachable here; C#'s `IComparable` constraint on the
   parameterless overloads forbids anything else) is deliberately excluded today. The fix reuses the SAME
   `MongoGroupAccumulatorStage` (`{_id: null, <field>: {<op>: <operand>}}`) the WITH-selector case already
   lowers to (`MongoSelectLowerer.cs` lines ~358-365) — no new pipeline stage, no second Grouping IR — by
   setting `operand` to a `MongoElementRefExpression` over the Select's own single flattened output alias
   (`select.Projection.Single().Alias`, e.g. `"_v"`) instead of translating a (nonexistent) selector lambda.

**Tech Stack:** C#, EF Core 8/9/10 provider internals, xUnit (plain `Assert.*`), MongoDB aggregation pipeline
(`$group`, `$project`, `$count`).

**Spec:** `docs/superpowers/specs/2026-09-24-native-groupby-fallback-reduction-design.md` (SP5 section) — Task
1 and Task 2's own "Architecture" text above supersedes that section's "Approach" paragraph for the reasons
given there; the spec's test list and priority ordering still apply.

## Global Constraints

- **`Native == DriverLinq` invariant** — every target test's `AssertQuery`/`AssertMql`/`AssertMin`/`AssertMax`/
  `AssertSingleResult` must keep passing under `MongoQueryMode.Native` exactly as it does today (via
  fallback), in addition to newly passing under `MongoQueryMode.NativeOnly`.
- **No new `MongoExpression` or pipeline-stage subtype.** Task 1 reuses `MongoConditionalExpression` +
  `MongoElementRefExpression.RemoveSentinelPath`-style `$sum`/`$cond` shape already used by the existing
  filtered-Sum/Min/Max/Average arm. Task 2 reuses `MongoGroupAccumulatorStage` exactly as already wired.
- **A predicate/shape this plan doesn't recognize must DECLINE, never silently mis-resolve** — same discipline
  SP2/SP3/SP4 established.
- `src/` is nullable-enabled — no new warnings.
- Unit tests use plain xUnit `Assert.*` — FluentAssertions is not referenced in this repo.

## Review Focus

- **A `g.Where(pred).Count()` whose `pred` mixes a per-element reference with `g.Key`** (e.g. `g.Where(e =>
  e.Amount > 5 && g.Key == "ALFKI").Count()`) must decline cleanly via `TryTranslateAccumulatorCondition`'s
  existing mixed-reference guard, exactly like the already-tested `Sum`/`Min`/`Max` filtered-accumulator arms —
  Task 1's own test proves this, since the new arm is easy to wire past that guard by accident.
- **The direct `g.Count(pred)`/`g.LongCount(pred)` form (SP3, already shipped) must keep working unmodified**
  once Task 1 adds the `g.Where(pred).Count()` form beside it in the same method — they are structurally
  disjoint (`call.Arguments.Count == 2` with the predicate as `call.Arguments[1]` directly on `g`, vs.
  `call.Arguments.Count == 1` with `g.Where(...)` as `call.Arguments[0]`), but Task 1's own regression test
  pins both still bind, not just the new one.
- **`Min()`/`Max()` over an EMPTY result** (every group filtered out, or the source itself has zero rows) must
  keep the existing `BuildEmptyBehavior` contract — `ReturnNull` for a nullable result type, `Throw`
  (`InvalidOperationException`) otherwise, matching in-memory LINQ. Task 2's own change reuses the unmodified
  `BuildEmptyBehavior` call already in `TryBindAggregate`, but its functional test must still assert this on an
  empty seed rather than assuming the plumbing "just works" untested.
- **A selector-less `Sum()`/`Average()` after `GroupBy(key).Select(aggregate)`** (as opposed to `Min`/`Max`) is
  a shape NEITHER target test needs — Task 2 deliberately scopes its new binder condition to `Min`/`Max` only
  and must decline `Sum()`/`Average()` in the same position (falls back to driver-LINQ), not silently admit
  them untested.
- **A selector-less `Min()`/`Max()` after a `Distinct()` (not a `GroupBy`) `.Select(aggregate)`** — e.g.
  `Select(o => new {...}).Distinct().Select(g => g.Sum(...)).Min()` — is also out of scope: Task 2 only widens
  the `isPostGroupBySelectAggregate` (`select.IsGroupBy`) bucket, not `isPostDistinctAggregate`. No target test
  needs the Distinct variant; leave it declined.

---

### Task 1: Filtered Count/LongCount accumulator (`g.Where(pred).Count()`/`.LongCount()`, no selector)

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeGroupByBinder.cs` —
  `TryBindFilteredAccumulator` (currently starting at line 1021)
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/NativeGroupByBinderTests.cs`

**Interfaces:**
- Consumes: `IsGroupingSource(Expression, ParameterExpression) : bool` (existing, ~line 1090),
  `TryTranslateAccumulatorCondition(Expression, ParameterExpression, IReadOnlyList<MongoGroupingKeyPart>, bool,
  MongoExpressionTranslator, out bool isKeyOnlyCondition, out MongoExpression? condition) : bool` (existing),
  `UnwrapLambdaFromQuote()` (existing extension method), `EnumerableMethods.CountWithoutPredicate` /
  `LongCountWithoutPredicate`, `QueryableMethods.CountWithoutPredicate` / `LongCountWithoutPredicate` (existing
  EF Core constants, already used at line ~600-604 in `TryBindAccumulator`).
- Produces: nothing new for later tasks — Task 3 verifies the resulting Select binds and that the
  ALREADY-EXISTING `NativeCardinalityBinder.TryBindAggregate` `isPostGroupBySelectAggregate` bucket picks up
  the outer `LongCount()` unmodified.

- [ ] **Step 1: Write the failing tests**

Open `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/NativeGroupByBinderTests.cs`. Find
`Where_predicate_mixing_element_and_key_reference_declines` (search for that method name) and add these four
tests immediately after it:

```csharp
    [Fact]
    public void Where_then_Count_binds_conditional_sum_accumulator()
    {
        // LongCount_after_GroupBy_aggregate's inner Select shape (as a NAMED member here; the bare-body
        // variant, matching the real test exactly, is proven by the next test).
        var mongoQ = BoundScalarKeyQuery();
        Expression<Func<IGrouping<string, Order>, object>> proj =
            g => new { Filtered = g.Where(e => e.Amount < 250).Count() };

        Assert.True(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out _));

        var acc = Assert.Single(mongoQ.Select.Grouping!.Accumulators);
        Assert.Equal("Filtered", acc.OutputField);
        Assert.Equal("$sum", acc.Operator);
        var cond = Assert.IsType<MongoConditionalExpression>(acc.Operand);
        Assert.IsType<MongoBinaryExpression>(cond.Test);
        // Unlike the Sum/Min/Max filtered arm's $$REMOVE else-branch, a filtered COUNT sums 0/1 either way —
        // an unmatched element must contribute 0, not $$REMOVE (which would be wrong for $sum: it silently
        // drops the summand, but 0 is exactly what an unmatched element should contribute here anyway; this
        // assertion pins that the constant 0 is actually emitted, not the $$REMOVE sentinel some OTHER
        // accumulator kind uses).
        Assert.Equal(1, Assert.IsType<MongoConstantExpression>(cond.IfTrue).Value);
        Assert.Equal(0, Assert.IsType<MongoConstantExpression>(cond.IfFalse).Value);
    }

    [Fact]
    public void Bare_body_Where_then_Count_binds_under_synthetic_alias()
    {
        // LongCount_after_GroupBy_aggregate's EXACT shape: a bare (non-`new{}`) Select body.
        var mongoQ = BoundScalarKeyQuery();
        Expression<Func<IGrouping<string, Order>, int>> proj =
            g => g.Where(e => e.Amount < 250).Count();

        Assert.True(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out var bareAlias));

        Assert.Equal(NativeProjectionBinder.SyntheticBareProjectionAlias, bareAlias);
        var acc = Assert.Single(mongoQ.Select.Grouping!.Accumulators);
        Assert.Equal(bareAlias, acc.OutputField);
        Assert.Equal("$sum", acc.Operator);
    }

    [Fact]
    public void Where_then_LongCount_binds_conditional_sum_accumulator()
    {
        var mongoQ = BoundScalarKeyQuery();
        Expression<Func<IGrouping<string, Order>, object>> proj =
            g => new { Filtered = g.Where(e => e.Amount < 250).LongCount() };

        Assert.True(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out _));

        var acc = Assert.Single(mongoQ.Select.Grouping!.Accumulators);
        Assert.Equal("$sum", acc.Operator);
        Assert.IsType<MongoConditionalExpression>(acc.Operand);
    }

    [Fact]
    public void Where_then_Count_mixing_element_and_key_reference_declines()
    {
        // Review Focus: the SAME mixed-reference guard the Sum/Min/Max filtered arm already relies on must
        // also gate the new Count/LongCount arm — proven separately, since it is easy to wire the new arm
        // past TryTranslateAccumulatorCondition's guard by accident (e.g. by not routing through it at all).
        var mongoQ = BoundScalarKeyQuery();
        Expression<Func<IGrouping<string, Order>, object>> proj =
            g => new { Filtered = g.Where(e => e.Amount > 5 && g.Key == "ALFKI").Count() };

        Assert.False(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out _));
    }
```

- [ ] **Step 2: Run the test file to confirm the new tests fail**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10" -v quiet
dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~NativeGroupByBinderTests"
```

Expected: `Where_then_Count_binds_conditional_sum_accumulator`, `Bare_body_Where_then_Count_binds_under_
synthetic_alias`, and `Where_then_LongCount_binds_conditional_sum_accumulator` FAIL with `Assert.True()
Failure` (`TryBindGroupProjection` returns `false` — `TryBindFilteredAccumulator`'s `call.Arguments.Count != 2`
gate rejects a selector-less `Count()`/`LongCount()` immediately, and the bare-`g.Count()` arm below also
declines because the call's source is `g.Where(...)`, not `g` directly).
`Where_then_Count_mixing_element_and_key_reference_declines` PASSES already (the shape already declines
today, for the OLD blanket reason).

- [ ] **Step 3: Implement the production fix**

In `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeGroupByBinder.cs`, find
`TryBindFilteredAccumulator` (currently line 1021) and add a new arm at the very top of its body, before the
existing `if (call.Arguments.Count != 2) return false;` line:

```csharp
        // EF-322 SP5: g.Where(pred).Count() / g.Where(pred).LongCount() — a filtered COUNT with NO selector.
        // Tried before the call.Arguments.Count != 2 gate below (which the Sum/Average/Min/Max-with-selector
        // arm requires): Count/LongCount without a predicate argument have call.Arguments.Count == 1 (just
        // the Where-wrapped source), never 2. Reduces to $sum: {$cond: [translatedPredicate, 1, 0]} — the
        // SAME shape SP3's direct g.Count(pred) arm (further down in TryBindAccumulator) already emits; the
        // only difference is where the predicate comes from (the Where call's own lambda here, vs. Count's
        // own argument there). 0, not $$REMOVE, matches a filtered COUNT's own correct semantics: an
        // unmatched element contributes 0 to the sum-of-1s either way.
        var countDefinition = call.Method.IsGenericMethod ? call.Method.GetGenericMethodDefinition() : null;
        if (call.Arguments.Count == 1
            && (countDefinition == EnumerableMethods.CountWithoutPredicate
                || countDefinition == EnumerableMethods.LongCountWithoutPredicate
                || countDefinition == QueryableMethods.CountWithoutPredicate
                || countDefinition == QueryableMethods.LongCountWithoutPredicate)
            && Unwrap(call.Arguments[0]) is MethodCallExpression
                {
                    Method: { Name: nameof(Queryable.Where), DeclaringType: var whereDeclaring0 },
                    Arguments.Count: 2
                } whereCall0
            && (whereDeclaring0 == typeof(Queryable) || whereDeclaring0 == typeof(Enumerable))
            && IsGroupingSource(whereCall0.Arguments[0], groupingParameter)
            && whereCall0.Arguments[1].UnwrapLambdaFromQuote() is { } wherePred0
            && TryTranslateAccumulatorCondition(
                wherePred0.Body, groupingParameter, keyParts, isComposite, translator, out _, out var countCond0))
        {
            accumulator = new MongoGroupAccumulator(outputField, "$sum",
                new MongoConditionalExpression(
                    countCond0, new MongoConstantExpression(1, null), new MongoConstantExpression(0, null)));
            flattenRead = new MongoElementRefExpression(outputField, call.Method.ReturnType);
            return true;
        }

        if (call.Arguments.Count != 2)
            return false;
```

Leave everything after that unchanged (the existing Sum/Average/Min/Max-with-selector logic).

- [ ] **Step 4: Run the test file again to confirm everything passes**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10" -v quiet
dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~NativeGroupByBinderTests"
```

Expected: all tests PASS, including every pre-existing test and the four new ones from Step 1.

- [ ] **Step 5: Run the whole Unit suite**

```bash
dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj \
  -c "Debug EF10" --no-build
```

Expected: PASS, 0 failed.

- [ ] **Step 6: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeGroupByBinder.cs \
        tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/NativeGroupByBinderTests.cs
git commit -m "EF-322: translate a filtered Count/LongCount GroupBy accumulator (g.Where(pred).Count())"
```

---

### Task 2: Selector-less Min/Max after `GroupBy(key).Select(aggregate)`

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeCardinalityBinder.cs` —
  `TryBindAggregate` (currently lines 122-335, specifically the `isPostGroupBySelectAggregate` condition at
  lines 194-199 and the Sum/Min/Max/Average operand block at lines 219-232)
- Test: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeGroupByTests.cs` (no dedicated unit-test
  harness exists for `NativeCardinalityBinder.TryBindAggregate` — `MongoCardinalityRouteTests.cs` only tests
  `MongoSelectDefinition.Route` classification from a hand-built `MongoCardinality`, never the binder itself —
  so this task is verified end-to-end via a functional test, matching how `NativeDistinctTests.cs`'s
  `Bare_scalar_Distinct_then_Max_goes_native` already verifies the sibling `TryBindDistinctTerminalAggregate`
  path)

**Interfaces:**
- Consumes: `MongoSelectDefinition.Projection : List<MongoProjection>` (existing —
  `readonly record struct MongoProjection(string Alias, MongoExpression Expression)`),
  `MongoElementRefExpression(string path, Type type)` (existing constructor).
- Produces: nothing new for later tasks — this is SP5's second and final capability.

- [ ] **Step 1: Write the failing functional tests**

Open `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeGroupByTests.cs`. Find its LAST test
method (search for the final `[Fact]` before the file's closing `}`) and add these three tests immediately
after it, before the closing brace:

```csharp
    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.NativeOnly)]
    public void Bare_accumulator_then_outer_Min_goes_native(MongoQueryMode mode)
    {
        // MinMax_after_GroupBy_aggregate's exact shape: GroupBy(key).Select(g => g.Sum(...)).Min() — a
        // bare, no-selector Min() reducing the ALREADY-flattened per-group Sum. Per-country sums: US=300,
        // UK=75, FR=300 — Min is 75.
        var seed = SeedOrders();
        using var db = CreateContext(seed, mode, nameof(Bare_accumulator_then_outer_Min_goes_native) + mode);

        var result = db.Entities
            .GroupBy(o => o.Country)
            .Select(g => g.Sum(o => o.Amount))
            .Min();
        Assert.Equal(75m, result);
    }

    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.NativeOnly)]
    public void Bare_accumulator_then_outer_Max_goes_native(MongoQueryMode mode)
    {
        var seed = SeedOrders();
        using var db = CreateContext(seed, mode, nameof(Bare_accumulator_then_outer_Max_goes_native) + mode);

        var result = db.Entities
            .GroupBy(o => o.Country)
            .Select(g => g.Sum(o => o.Amount))
            .Max();
        Assert.Equal(300m, result);
    }

    [Fact]
    public void Bare_accumulator_then_outer_Min_over_empty_source_throws()
    {
        // Review Focus: Min()/Max() over an empty result must keep BuildEmptyBehavior's existing contract —
        // decimal is non-nullable, so this throws InvalidOperationException, matching in-memory LINQ and the
        // driver-LINQ fallback, not silently returning default(decimal).
        using var db = CreateContext([], MongoQueryMode.NativeOnly, nameof(Bare_accumulator_then_outer_Min_over_empty_source_throws));

        Assert.Throws<InvalidOperationException>(() =>
            db.Entities.GroupBy(o => o.Country).Select(g => g.Sum(o => o.Amount)).Min());
    }

    [Fact]
    public void Bare_accumulator_then_outer_Sum_still_declines_under_NativeOnly()
    {
        // Review Focus: Task 2 deliberately scopes its new binder condition to Min/Max only — a selector-less
        // Sum()/Average() in the SAME position must keep declining (falling back), not silently be admitted
        // as an untested side effect of widening isPostGroupTerminalAggregate.
        using var db = CreateContext(SeedOrders(), MongoQueryMode.NativeOnly,
            nameof(Bare_accumulator_then_outer_Sum_still_declines_under_NativeOnly));

        Assert.Throws<MongoDB.EntityFrameworkCore.Query.NativeTranslation.NativeTranslationNotSupportedException>(() =>
            db.Entities.GroupBy(o => o.Country).Select(g => g.Sum(o => o.Amount)).Sum());
    }
```

No separate functional test covers the "Distinct variant stays out of scope" Review Focus line: there is no
reachable `Distinct().Select(aggregate).Min()` LINQ shape to begin with (`Distinct()`'s element is a
projected DTO, not an `IGrouping`, so there is no per-key accumulator Select to flatten before a bare
`Min()`) — the scoping is enforced purely structurally, by `isPostGroupBySelectlessMinMax`'s own
`select.IsGroupBy` check (never `select.IsDistinct`) in Step 3's implementation. Confirm this reading holds
before writing Step 3: if it turns out there IS a reachable Distinct-based shape reaching
`isPostDistinctAggregate` with `select.Projection.Count == 1` and a null selector, add a decline test for it
using the SAME `Assert.Throws<NativeTranslationNotSupportedException>` pattern as
`Bare_accumulator_then_outer_Sum_still_declines_under_NativeOnly` above before proceeding to Step 3.

- [ ] **Step 2: Run the test file to confirm the new tests fail**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10" -v quiet
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~NativeGroupByTests&FullyQualifiedName~outer_Min|FullyQualifiedName~NativeGroupByTests&FullyQualifiedName~outer_Max" \
  --logger "console;verbosity=normal"
```

Expected: `Bare_accumulator_then_outer_Min_goes_native(NativeOnly)` and `Bare_accumulator_then_outer_Max_goes_
native(NativeOnly)` FAIL with `NativeTranslationNotSupportedException` (`TryBindAggregate`'s Sum/Min/Max/
Average operand block requires a non-null selector). The `Native`-mode variants and
`Bare_accumulator_then_outer_Min_over_empty_source_throws` PASS already (fallback already gives the right
answer/exception; only `NativeOnly` proves the capability is missing).

- [ ] **Step 3: Implement the production fix**

In `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeCardinalityBinder.cs`, find the
`isPostGroupBySelectAggregate` declaration (currently lines 194-199):

```csharp
        var isPostGroupBySelectAggregate = select.IsGroupBy && !select.IsDistinct && select.Grouping != null
            && select.Cardinality == null
            && (op is MongoAggregateOperator.Count or MongoAggregateOperator.LongCount
                or MongoAggregateOperator.Any or MongoAggregateOperator.All
                || (op is MongoAggregateOperator.Sum or MongoAggregateOperator.Min
                    or MongoAggregateOperator.Max or MongoAggregateOperator.Average && selector != null));
```

Add a new local just below it — a selector-less Min/Max is a DIFFERENT admission rule (single flattened
projection, no selector translation at all) from the selector-bearing one above, so keep them as two named
booleans rather than folding a THIRD condition into the expression above:

```csharp
        // EF-322 SP5: a selector-less Min()/Max() (the ONLY shape reachable — C#'s IComparable constraint on
        // the parameterless overloads forbids anything else) reducing the ALREADY-flattened single scalar a
        // preceding GroupBy(key).Select(aggregate) projected — e.g. GroupBy(o => o.CustomerID)
        // .Select(g => g.Sum(o => o.OrderID)).Min(). Requires exactly one flattened projection member (the
        // bare-body case always has exactly one, aliased NativeProjectionBinder.SyntheticBareProjectionAlias —
        // a `new{...}` multi-member projection could never reach a parameterless Min()/Max() call in the
        // first place, since an anonymous type has no IComparable). Deliberately scoped to Min/Max only, and
        // to IsGroupBy (never IsDistinct) — see this plan's own Review Focus for why Sum/Average and the
        // Distinct variant stay out of scope.
        var isPostGroupBySelectlessMinMax = select.IsGroupBy && !select.IsDistinct && select.Grouping != null
            && select.Cardinality == null && selector == null
            && op is MongoAggregateOperator.Min or MongoAggregateOperator.Max
            && select.Projection.Count == 1;
```

Now find `isPostGroupTerminalAggregate` just below (currently line 201) and widen it:

```csharp
        var isPostGroupTerminalAggregate =
            isPostDistinctAggregate || isPostGroupBySelectAggregate || isPostGroupBySelectlessMinMax;
```

Find the `HasTerminalOperator` guard just below that (currently line 203) — it already reads
`isPostGroupTerminalAggregate`, so no change needed there.

Now find the Sum/Min/Max/Average operand block (currently lines 219-232):

```csharp
        MongoExpression? operand = null;
        if (op is MongoAggregateOperator.Sum or MongoAggregateOperator.Min
               or MongoAggregateOperator.Max or MongoAggregateOperator.Average)
        {
            // Selector may be a plain member access, a widening/nullable-preserving Convert over one (e.g.
            // `(short?)detail.Quantity`, EF-322's "cast to same nullable type"), or a numeric arithmetic
            // expression (e.g. `detail.Quantity / 2.09m`) — anything TryTranslateValue accepts. ...
            if (selector is null || !translator.TryTranslateValue(selector.Body, out operand))
                return false;
        }
```

Replace it with:

```csharp
        MongoExpression? operand = null;
        if (op is MongoAggregateOperator.Sum or MongoAggregateOperator.Min
               or MongoAggregateOperator.Max or MongoAggregateOperator.Average)
        {
            if (isPostGroupBySelectlessMinMax)
            {
                // Reduce the preceding Select's own single flattened output field directly — there is no
                // selector lambda to translate at all (see isPostGroupBySelectlessMinMax's own remarks).
                var flattened = select.Projection[0];
                operand = new MongoElementRefExpression(flattened.Alias, flattened.Expression.Type);
            }
            // Selector may be a plain member access, a widening/nullable-preserving Convert over one (e.g.
            // `(short?)detail.Quantity`, EF-322's "cast to same nullable type"), or a numeric arithmetic
            // expression (e.g. `detail.Quantity / 2.09m`) — anything TryTranslateValue accepts. That helper
            // already enforces exact value preservation (it rejects narrowing casts and non-default-serialized
            // operands), which Sum/Average need; Min/Max need only order preservation, which value preservation
            // trivially satisfies, so both share the same call rather than Min/Max keeping a separately
            // maintained, looser cast check.
            else if (selector is null || !translator.TryTranslateValue(selector.Body, out operand))
                return false; // untranslatable selector shape (e.g. a correlated method call) — fall back
        }
```

- [ ] **Step 4: Run the test file again to confirm everything passes**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10" -v quiet
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~NativeGroupByTests" --logger "console;verbosity=normal"
```

Expected: all tests in the file PASS, including the three new ones from Step 1 (both `Native` and
`NativeOnly` variants of the first two).

- [ ] **Step 5: Run the whole Functional suite**

```bash
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --no-build
```

Expected: PASS, 0 failed.

- [ ] **Step 6: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeCardinalityBinder.cs \
        tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeGroupByTests.cs
git commit -m "EF-322: reduce a selector-less Min/Max over an already-grouped Select projection"
```

---

### Task 3: Verify the target spec-test methods, regenerate baselines, run every suite

**Files:**
- Test (read-only unless a diff is found): `tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/
  NorthwindGroupByQueryMongoTest.cs`

**Interfaces:**
- Consumes: Task 1 + Task 2's production changes — this task is pure verification, no new production code.

- [ ] **Step 1: Confirm Docker (or `MONGODB_URI`) is available**

```bash
docker info >/dev/null 2>&1 && echo "docker OK" || echo "need MONGODB_URI or Docker"
```

- [ ] **Step 2: Run the 2 target methods under default (`Native`) mode; regenerate baselines that changed**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10" -v quiet
dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~NorthwindGroupByQueryMongoTest&(FullyQualifiedName~MinMax_after_GroupBy_aggregate|FullyQualifiedName~LongCount_after_GroupBy_aggregate)" \
  --logger "console;verbosity=normal" > /tmp/sp5-native.log 2>&1
tail -60 /tmp/sp5-native.log
```

Both methods' current baselines (`LongCount_after_GroupBy_aggregate`'s `$push`/`$filter`/`$size`/`$count`
shape and `MinMax_after_GroupBy_aggregate`'s two-baseline `$group`/`$project`/`$group`/`$replaceRoot` shape,
both visible in the file today) were captured from the OLD driver-LINQ fallback — the native pipeline this
plan produces is very unlikely to match either byte-for-byte. For any method whose `AssertMql` fails with
`Assert.Equal() Failure: Strings differ` (a baseline mismatch, NOT a data/behavior failure), regenerate:

```bash
EF_TEST_REWRITE_BASELINES=1 dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~NorthwindGroupByQueryMongoTest.<MethodName>"
git diff tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/NorthwindGroupByQueryMongoTest.cs
```

Confirm each diff shows a `$group`/`$project`/`$count` (LongCount) or `$group`/`$project`/`$group`/
`$replaceRoot` (MinMax) shape consistent with this plan's design, then rebuild and rerun WITHOUT the env var
to confirm green.

- [ ] **Step 3: Run the same 2 methods under `MONGODB_EF_NATIVE_ONLY=1`**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10" -v quiet
MONGODB_EF_NATIVE_ONLY=1 dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~NorthwindGroupByQueryMongoTest&(FullyQualifiedName~MinMax_after_GroupBy_aggregate|FullyQualifiedName~LongCount_after_GroupBy_aggregate)" \
  --logger "console;verbosity=normal" > /tmp/sp5-nativeonly.log 2>&1
tail -20 /tmp/sp5-nativeonly.log
```

Expected: both methods (4 with async) now PASS under `NativeOnly`.

- [ ] **Step 4: Re-measure the whole class's `NativeOnly` failure count, checking CONTENT not just the number**

```bash
MONGODB_EF_NATIVE_ONLY=1 dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~NorthwindGroupByQueryMongoTest" \
  --logger "console;verbosity=normal" > /tmp/sp5-full-nativeonly.log 2>&1
tail -8 /tmp/sp5-full-nativeonly.log
grep -E "^\s*Failed MongoDB" /tmp/sp5-full-nativeonly.log | sed -E 's/^\s*Failed //; s/\(async:.*//' | sed 's/ *$//' | sort -u
```

Expected: failure count drops from 30 (SP4's ending count) to 28, and the method-NAME list is exactly SP4's
15 remaining methods minus these 2 (i.e. 13 names): `GroupBy_Dto_as_key_Select_Sum`, `GroupBy_count_filter`,
`GroupBy_empty_key_Aggregate_Key`, `GroupBy_multi_navigation_members_Aggregate`,
`GroupBy_required_navigation_member_Aggregate`, `GroupBy_selecting_grouping_key_list`,
`GroupBy_skip_0_take_0_aggregate`, `GroupBy_with_group_key_access_thru_navigation`, `Join_GroupBy_Aggregate`,
`Join_groupby_anonymous_orderby_anonymous_projection`, `Odata_groupby_empty_key`, `Self_join_GroupBy_Aggregate`,
`Union_simple_groupby`. Do not just check the NUMBER — diff the actual name list against this expected 13
before proceeding. If the list differs, investigate whether a name's absence is a genuine bonus fix (a
baseline-only fix, confirm via `AssertMql` mismatch not a data/behavior failure) or a sign this plan's own
change touched something unintended, before assuming either way.

- [ ] **Step 5: Run the full Specification suite (not just the GroupBy class)**

```bash
dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --logger "console;verbosity=normal" > /tmp/sp5-spec-full.log 2>&1
tail -8 /tmp/sp5-spec-full.log
```

Expected: PASS, 0 failed. If anything OUTSIDE the 2 target methods fails, investigate whether it's a genuine
regression or another "secretly-already-fixed, baseline-only" bonus (a recurring pattern in prior slices)
before assuming either way.

- [ ] **Step 6: Run the full Functional suite (not just NativeGroupByTests)**

```bash
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --no-build --logger "console;verbosity=normal" > /tmp/sp5-functional-full.log 2>&1
tail -8 /tmp/sp5-functional-full.log
```

Expected: PASS, 0 failed.

- [ ] **Step 7: Run all three suites on EF8 and EF9**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF8" -v quiet
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF9" -v quiet

for CFG in EF8 EF9; do
  echo "=== $CFG unit ==="
  dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj -c "Debug $CFG" --no-build 2>&1 | tail -5
  echo "=== $CFG spec (GroupBy class) ==="
  dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj -c "Debug $CFG" --no-build --filter "FullyQualifiedName~NorthwindGroupByQueryMongoTest" 2>&1 | tail -5
  echo "=== $CFG functional (GroupBy) ==="
  dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj -c "Debug $CFG" --no-build --filter "FullyQualifiedName~NativeGroupByTests" 2>&1 | tail -5
done
```

Expected: PASS, 0 failed, on both EF8 and EF9, for all three commands.

- [ ] **Step 8: Commit any baseline changes from Step 2**

```bash
git add tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/NorthwindGroupByQueryMongoTest.cs
git commit -m "EF-322: verify SP5 outer-scalar-reduce slice — baselines, full suites"
```

---

## After this plan

This plan covers SP5 only (2 tests). SP6–SP9 each get their own plan when their turn comes, using this
slice's post-landing `NativeOnly` failure count (28, confirmed in Task 3 Step 4) as their new baseline. Before
starting SP6's plan, re-verify SP6's own target tests' shapes against the actual EF Core base-test source the
same way this plan's own investigation (and SP2's/SP4's own corrections) did — don't assume the design doc's
original bucketing is exact without a quick re-check. SP6's two tests (`Union_simple_groupby`,
`GroupBy_skip_0_take_0_aggregate`) are structurally unrelated to each other (a `Union` mutual-exclusion guard
vs. a new `PendingGroupPaging` mechanism) — confirm during SP6 planning whether they should split into two
plans rather than one.
