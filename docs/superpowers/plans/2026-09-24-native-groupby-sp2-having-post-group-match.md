# Native GroupBy SP2: HAVING (post-`$group` `$match`) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make a `Where` composed directly between `GroupBy(key)` and its terminal `Select(aggregate)` — a
HAVING clause — translate natively as a `$match` emitted right after `$group` and before the flattening
`$project`, instead of declining to the driver-LINQ fallback. Moves 3 `NorthwindGroupByQueryMongoTest` methods
off the fallback: `GroupBy_filter_count`, `GroupBy_filter_count_OrderBy_count_Select_sum`, `GroupBy_filter_key`.

**Architecture:** `NativeGroupByBinder.TryBindGroupWherePredicate` already recognizes this Where shape and
stashes the bound comparison on `MongoSelectDefinition.PendingGroupPredicate` — but today `TryBindGroupProjection`
just declines whenever it sees that stashed state (a documented, deliberate EF-449 gap). This plan makes
`TryBindGroupProjection` consume it instead: fold its accumulator (if any) into the `$group`'s own accumulators,
and emit its comparison as a new `MongoSelectDefinition.GroupHavingPredicate`, which `MongoSelectLowerer` renders
as a `MongoMatchStage` right after the `$group` stage. Separately, `GroupBy_filter_key`'s predicate
(`o.Key == "ALFKI"`) is a KEY comparison, not an accumulator comparison — `TryBindGroupPredicateComparison`
(shared by the Where path and by a bare-GroupBy terminal aggregate's own direct-predicate arm) grows a key-access
arm alongside its existing accumulator arm. That widens what a successful bind can look like — a key-only match
has no accumulator at all — so `TryBindGroupTerminalAggregate`'s own consumption of the shared result must be
fixed in the same change, or it would flip from "safely declines" to "silently drops the filter."

**Tech Stack:** C#, EF Core 8/9/10 provider internals, xUnit (plain `Assert.*`), MongoDB aggregation pipeline
(`$group`/`$match`).

**Spec:** `docs/superpowers/specs/2026-09-24-native-groupby-fallback-reduction-design.md` (SP2 section — read
the whole section including its "Correction" and "Trap to avoid" notes before starting; both were added after
a pre-implementation re-check found the spec's original 4-test scope included a test that doesn't belong here)

## Global Constraints

- **`Native == DriverLinq` invariant** — this change must alter only the execution path, never query results.
  Every target test's `AssertQuery`/`AssertMql` must keep passing under `MongoQueryMode.Native` exactly as it
  does today (via fallback), in addition to newly passing under `MongoQueryMode.NativeOnly`.
- **Do not add `GroupBy_count_filter` to this slice's scope.** Its `Where` filters an already-flattened Select
  output (not an `IGrouping` HAVING predicate) and its `GroupBy` key must resolve against a prior projected
  `Select`'s own alias, not the entity — a different, not-yet-built capability. Confirmed via the design doc's
  "Correction" note and this plan's own investigation (baseline for that test already shows `$match` running
  AFTER the flattening `$project`, on the flattened `"Count"` alias — see Task 3, Step 1).
- **The `[NotNullWhen(true)]` fix on `TryBindGroupPredicateComparison`'s `accumulator` parameter and
  `TryBindGroupTerminalAggregate`'s consumption-gating fix must land in Task 1, together** — not deferred, not
  split across tasks. Task 1's own tests prove this explicitly (see its Review Focus item below).
- `src/` is nullable-enabled — no new warnings.
- Unit tests use plain xUnit `Assert.*` — FluentAssertions is not referenced in this repo.
- **Run the Functional test suite, not just Unit and Specification** — SP1's final review found 3 stale
  pinned Functional tests that neither the original plan nor its execution had ever run. Task 3 runs all three
  test projects explicitly; do not skip Functional "to save time."

## Review Focus

- **A bare `GroupBy(key).Where(g => g.Key == "X").Any()`-shaped query (no Select at all) must not silently
  drop its filter** once the key-access arm exists — `TryBindGroupTerminalAggregate` currently gates emitting
  its `$match` on `accumulator != null`, which a key-only comparison never sets. Task 1 adds
  `Where_key_comparison_then_bare_Any_applies_filter_with_no_accumulator`, which fails loudly (asserts the
  predicate WAS applied) rather than silently passing with an unfiltered result.
- **A HAVING accumulator's own output field ("__agg0") must not collide with a same-named field the Select
  ALSO projects.** `GroupBy_filter_count_OrderBy_count_Select_sum`'s Select projects its OWN `Count =
  g.Count()` under alias `"Count"`, distinct from the HAVING's `"__agg0"` — Task 2's own unit test
  (`HAVING_where_then_select_binds_grouping_with_having_predicate`) asserts there are TWO accumulators, not
  one deduplicated one, matching this file's existing "harmless redundant field" precedent for
  `orderAccumulators`.
- **`GroupHavingPredicate` must run BEFORE `GroupOrderOp`'s sort, not after** — HAVING filters which groups
  exist; a sort emitted first would order groups that HAVING would have excluded, and (for
  `GroupBy_filter_count_OrderBy_count_Select_sum`) a sort key that happens to be the SAME accumulator HAVING
  filtered on would still be internally consistent either way, so this ordering mistake could pass that one
  test's data assertion by coincidence — Task 3 must diff the actual `AssertMql` baseline, not just check the
  test passes, to catch stage-order mistakes MQL string comparison would otherwise reveal but a looser
  data-only check might miss.
- **A `PendingGroupPredicate` left un-consumed on a query that later declines for an unrelated reason** would
  leak stale binder state into whatever fallback path runs next for the same `MongoSelectDefinition` — Task 2
  clears `select.PendingGroupPredicate = null` unconditionally right after staging it into a local, mirroring
  `TryBindGroupTerminalAggregate`'s own existing one-shot-consume discipline, not only on the success path.
  Task 2's own tests assert `Assert.Null(mongoQ.Select.PendingGroupPredicate)` after a successful bind.

---

### Task 1: Add the key-access arm to `TryBindGroupPredicateComparison`, and fix the accumulator-nullability trap it opens

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeGroupByBinder.cs`
  - `TryBindGroupPredicateComparison` (currently lines 771–815)
  - `TryBindGroupWherePredicate` (currently lines 754–765)
  - `TryBindGroupTerminalAggregate` (currently lines 658–740)
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/NativeGroupByBinderTests.cs`

**Interfaces:**
- Consumes: `TryGetKeyMemberPath(Expression, ParameterExpression, IReadOnlyList<MongoGroupingKeyPart>, bool,
  out string?, bool allowWholeKeyRead = true)` (existing, private, same file — reused unchanged) and
  `TryBindAccumulator` (existing, private, same file — reused unchanged).
- Produces: `TryBindGroupPredicateComparison`'s new signature —
  `(Expression body, ParameterExpression groupingParameter, MongoExpressionTranslator translator,
  IReadOnlyList<MongoGroupingKeyPart> keyParts, bool isComposite, out MongoGroupAccumulator? accumulator,
  [NotNullWhen(true)] out MongoExpression? comparisonNode) : bool` — Task 2 does NOT call this method directly
  (it only reads the already-bound `PendingGroupPredicate` tuple), so Task 2 only needs to know the TUPLE
  shape changed: `MongoSelectDefinition.PendingGroupPredicate` is now
  `(MongoGroupAccumulator? Accumulator, MongoExpression Comparison)?` — `Accumulator` can be `null`.

- [ ] **Step 1: Write the failing tests**

Open `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/NativeGroupByBinderTests.cs`. Find the
`// ── TryBindGroupTerminalAggregate ──` section (around line 886) and add these three tests immediately after
`Already_finalized_grouping_declines` (the last test in that section, around line 1015), before the
`// ── NativeCardinalityBinder...` comment that follows it:

```csharp
    [Fact]
    public void Any_with_key_comparison_predicate_binds_direct_comparison_with_no_accumulator()
    {
        var mongoQ = BoundScalarKeyQuery();
        Expression<Func<IGrouping<string, Order>, bool>> pred = g => g.Key == "ALFKI";

        Assert.True(NativeGroupByBinder.TryBindGroupTerminalAggregate(
            mongoQ, MongoAggregateOperator.Any, pred, typeof(bool)));

        Assert.Empty(mongoQ.Select.Grouping!.Accumulators);
        var match = Assert.IsType<MongoBinaryExpression>(mongoQ.Select.PostGroupPredicate);
        Assert.Equal(MongoBinaryOperator.Equal, match.Operator);
        var left = Assert.IsType<MongoElementRefExpression>(match.Left);
        Assert.Equal("_id", left.Path);
        var right = Assert.IsType<MongoConstantExpression>(match.Right);
        Assert.Equal("ALFKI", right.Value);
    }

    [Fact]
    public void Where_key_comparison_then_bare_Any_applies_filter_with_no_accumulator()
    {
        // Regression for the accumulator-nullability trap: TryBindGroupWherePredicate's key-access arm
        // produces a PendingGroupPredicate with a NULL accumulator (a key comparison needs no $group
        // accumulator of its own); TryBindGroupTerminalAggregate must still emit it as the $match predicate,
        // not silently discard it because accumulator is null — that would return every group instead of
        // the filtered subset, a correctness regression, not just a missed capability.
        var mongoQ = BoundScalarKeyQuery();
        Expression<Func<IGrouping<string, Order>, bool>> wherePred = g => g.Key == "ALFKI";

        Assert.True(NativeGroupByBinder.TryBindGroupWherePredicate(mongoQ, wherePred));
        Assert.NotNull(mongoQ.Select.PendingGroupPredicate);
        Assert.Null(mongoQ.Select.PendingGroupPredicate!.Value.Accumulator);

        Assert.True(NativeGroupByBinder.TryBindGroupTerminalAggregate(
            mongoQ, MongoAggregateOperator.Any, null, typeof(bool)));

        Assert.Empty(mongoQ.Select.Grouping!.Accumulators);
        var match = Assert.IsType<MongoBinaryExpression>(mongoQ.Select.PostGroupPredicate);
        Assert.Equal(MongoBinaryOperator.Equal, match.Operator);
    }

    [Fact]
    public void Where_key_comparison_over_composite_key_still_declines()
    {
        // A bare g.Key over a COMPOSITE key has no single field to compare against a scalar constant — same
        // "no single field" reasoning every other key-flatten call site in this file applies. Must still
        // decline (fall back), not crash or silently compare the wrong thing.
        var mongoQ = TestQuery();
        Expression<Func<Order, object>> key = x => new { x.Country, x.Region };
        Assert.True(NativeGroupByBinder.TryBindGroupKey(mongoQ, key));

        Expression<Func<IGrouping<object, Order>, bool>> wherePred = g => g.Key == new { Country = "US", Region = "West" };

        Assert.False(NativeGroupByBinder.TryBindGroupWherePredicate(mongoQ, wherePred));
        Assert.Null(mongoQ.Select.PendingGroupPredicate);
    }
```

- [ ] **Step 2: Run the test file to confirm the new tests fail against today's code**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10" -v quiet
dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~NativeGroupByBinderTests"
```

Expected: `Any_with_key_comparison_predicate_binds_direct_comparison_with_no_accumulator` and
`Where_key_comparison_then_bare_Any_applies_filter_with_no_accumulator` FAIL (both currently return `false`
from `TryBindGroupTerminalAggregate`/`TryBindGroupWherePredicate` — a bare key comparison isn't recognized
yet). `Where_key_comparison_over_composite_key_still_declines` PASSES already (both old and new code decline
this shape) — that's expected; it's here to catch a regression in Step 4, not to be red now.

- [ ] **Step 3: Implement the production fix**

In `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeGroupByBinder.cs`, replace
`TryBindGroupPredicateComparison` (currently lines 771–815) with:

```csharp
    // Recognizes `body` as a single comparison of one group-level operand — a KEY access (g.Key / g.Key.Sub,
    // no accumulator needed) or a group-level aggregate (bound via the SAME TryBindAccumulator shapes the
    // Select-projection path uses, output field "__agg0") — against a constant/parameter, in EITHER operand
    // order. Returns the bound accumulator (null for a key-access comparison — EF-322 SP2) and the translated
    // comparison node (the group-level operand's field-ref on the left, in normalized — not necessarily
    // source — operator direction).
    private static bool TryBindGroupPredicateComparison(
        Expression body,
        ParameterExpression groupingParameter,
        MongoExpressionTranslator translator,
        IReadOnlyList<MongoGroupingKeyPart> keyParts,
        bool isComposite,
        out MongoGroupAccumulator? accumulator,
        [NotNullWhen(true)] out MongoExpression? comparisonNode)
    {
        accumulator = null;
        comparisonNode = null;

        const string outputField = "__agg0";

        if (Unwrap(body) is not BinaryExpression
            {
                NodeType: ExpressionType.Equal or ExpressionType.NotEqual or ExpressionType.GreaterThan
                or ExpressionType.GreaterThanOrEqual or ExpressionType.LessThan or ExpressionType.LessThanOrEqual
            } bin)
        {
            return false;
        }

        if (TryBindGroupSideOperand(bin.Left, groupingParameter, keyParts, isComposite, translator, outputField,
                out accumulator, out var leftRef)
            && TryTranslateComparisonConstant(bin.Right, out var rightNode))
        {
            comparisonNode = new MongoBinaryExpression(MapComparisonOperator(bin.NodeType), leftRef, rightNode);
            return true;
        }

        if (TryBindGroupSideOperand(bin.Right, groupingParameter, keyParts, isComposite, translator, outputField,
                out accumulator, out var rightRef)
            && TryTranslateComparisonConstant(bin.Left, out var leftNode))
        {
            comparisonNode = new MongoBinaryExpression(
                MapComparisonOperator(FlipComparison(bin.NodeType)), rightRef, leftNode);
            return true;
        }

        return false;
    }

    // EF-322 SP2: one side of a group predicate comparison — either a KEY access (g.Key / g.Key.Sub;
    // accumulator stays null, the reference reads "_id"[.Name] directly, no $group accumulator needed at
    // all) or a group-level ACCUMULATOR call (g.Count()/g.Sum(...) etc., bound to the reserved output field
    // "__agg0"). A composite WHOLE-key read (bare g.Key over a >1-part key) still declines — TryGetKeyMemberPath's
    // own allowWholeKeyRead: true default already encodes "no single field to compare" for this; passed
    // through unchanged.
    private static bool TryBindGroupSideOperand(
        Expression side,
        ParameterExpression groupingParameter,
        IReadOnlyList<MongoGroupingKeyPart> keyParts,
        bool isComposite,
        MongoExpressionTranslator translator,
        string accumulatorOutputField,
        out MongoGroupAccumulator? accumulator,
        [NotNullWhen(true)] out MongoExpression? reference)
    {
        accumulator = null;
        reference = null;

        if (TryGetKeyMemberPath(side, groupingParameter, keyParts, isComposite, out var keyPath))
        {
            if (keyPath == null)
                return false; // bare g.Key over a composite key — no single field to compare

            reference = new MongoElementRefExpression(keyPath, Unwrap(side).Type);
            return true;
        }

        if (TryBindAccumulator(side, accumulatorOutputField, groupingParameter, translator, out var acc, out _))
        {
            accumulator = acc;
            reference = new MongoElementRefExpression(accumulatorOutputField, Unwrap(side).Type);
            return true;
        }

        return false;
    }
```

Replace `TryBindGroupWherePredicate` (currently lines 754–765) with:

```csharp
    internal static bool TryBindGroupWherePredicate(MongoQueryExpression mongoQ, LambdaExpression predicate)
    {
        var select = mongoQ.Select;
        if (select.PendingGroupKey is not { } keyParts)
            return false;

        var translator = new MongoExpressionTranslator(mongoQ.CollectionExpression.EntityType);
        var isComposite = keyParts.Count == 0 ? false : keyParts.Count > 1 || keyParts[0].Name != null;

        if (!TryBindGroupPredicateComparison(predicate.Body, predicate.Parameters[0], translator,
                keyParts, isComposite, out var accumulator, out var comparisonNode))
            return false;

        select.PendingGroupPredicate = (accumulator, comparisonNode);
        return true;
    }
```

In `TryBindGroupTerminalAggregate` (currently lines 658–740), make two changes. First, add an `isComposite`
local right after the existing `keyParts` guard (around line 663):

```csharp
        var select = mongoQ.Select;
        if (select.PendingGroupKey is not { } keyParts || select.Grouping != null)
            return false;

        var isComposite = keyParts.Count == 0 ? false : keyParts.Count > 1 || keyParts[0].Name != null;
```

Then update the call site inside the `if (predicate != null)` block (currently lines 675–685) to pass the new
parameters:

```csharp
        if (predicate != null)
        {
            if (!TryBindGroupPredicateComparison(predicate.Body, predicate.Parameters[0], translator,
                    keyParts, isComposite, out accumulator, out comparisonNode))
                return false;
        }
```

Finally, replace the consumption block (currently lines 699–723):

```csharp
        select.PendingGroupPredicate = null; // one-shot: consumed above, or never set for a bare terminal.

        if (accumulator != null)
        {
            accumulators.Add(accumulator);

            if (op is MongoAggregateOperator.All)
            {
                // ...
                if (!TryNegateGroupComparison(comparisonNode!, out var negated))
                    return false;
                matchPredicate = negated;
            }
            else
            {
                matchPredicate = comparisonNode;
            }
        }
```

with:

```csharp
        select.PendingGroupPredicate = null; // one-shot: consumed above, or never set for a bare terminal.

        // EF-322 SP2: gate on comparisonNode, not accumulator — a bare KEY comparison (g.Key == "ALFKI") has
        // no accumulator at all, but still produces a valid $match predicate. Gating on accumulator alone
        // would silently DROP a key-only predicate's filter here once TryBindGroupPredicateComparison's
        // key-access arm exists — a correctness regression, not just a missed capability.
        if (comparisonNode != null)
        {
            if (accumulator != null)
                accumulators.Add(accumulator);

            if (op is MongoAggregateOperator.All)
            {
                // All(pred) ≡ no group fails pred. Match the EXACT COMPLEMENT, mirroring
                // NativeCardinalityBinder.TryBindAggregate's row-level All handling: presence of any
                // surviving group (after $group + this $match) means at least one group failed pred.
                if (!TryNegateGroupComparison(comparisonNode, out var negated))
                    return false;
                matchPredicate = negated;
            }
            else
            {
                matchPredicate = comparisonNode;
            }
        }
```

(Keep the rest of the method — `NativeCardinalityBinder.BuildEmptyBehavior` onward — unchanged.)

In `src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoSelectDefinition.cs`, change the `PendingGroupPredicate`
declaration (currently `internal (MongoGroupAccumulator Accumulator, MongoExpression Comparison)?
PendingGroupPredicate { get; set; }`) to make the accumulator nullable:

```csharp
    internal (MongoGroupAccumulator? Accumulator, MongoExpression Comparison)? PendingGroupPredicate { get; set; }
```

- [ ] **Step 4: Run the test file again to confirm everything passes**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10" -v quiet
dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~NativeGroupByBinderTests"
```

Expected: all tests in the file PASS, including every pre-existing test and the three new/changed ones from
Step 1.

- [ ] **Step 5: Run the whole Unit suite**

```bash
dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj \
  -c "Debug EF10" --no-build
```

Expected: PASS, 0 failed (this changes a shared helper — `TryBindGroupPredicateComparison` — used by two
call sites; confirm nothing else in the suite depended on its old signature or the accumulator's old
non-nullability).

- [ ] **Step 6: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeGroupByBinder.cs \
        src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoSelectDefinition.cs \
        tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/NativeGroupByBinderTests.cs
git commit -m "EF-322: add key-access arm to GroupBy predicate comparison; fix accumulator-nullability trap"
```

---

### Task 2: Consume the HAVING predicate in `TryBindGroupProjection`, emit it in `MongoSelectLowerer`

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoSelectDefinition.cs` — add
  `GroupHavingPredicate`
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeGroupByBinder.cs` —
  `TryBindGroupProjection` (currently lines 197–333)
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoSelectLowerer.cs` — the `$group` block
  (currently lines 260–311)
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/NativeGroupByBinderTests.cs`

**Interfaces:**
- Consumes: Task 1's `MongoSelectDefinition.PendingGroupPredicate` shape —
  `(MongoGroupAccumulator? Accumulator, MongoExpression Comparison)?`.
- Produces: `MongoSelectDefinition.GroupHavingPredicate` — `MongoExpression?`, `null` for every query that
  never took this path. `MongoSelectLowerer` reads it; no other file needs to.

- [ ] **Step 1: Write the failing tests**

In `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/NativeGroupByBinderTests.cs`, find the
`// ── TryBindGroupProjection ──` section (search for it — it follows the `TryBindGroupKey` section) and add
these two tests anywhere within it:

```csharp
    [Fact]
    public void HAVING_where_then_select_binds_grouping_with_having_predicate()
    {
        var mongoQ = BoundScalarKeyQuery();
        Expression<Func<IGrouping<string, Order>, bool>> wherePred = g => g.Count() > 4;
        Assert.True(NativeGroupByBinder.TryBindGroupWherePredicate(mongoQ, wherePred));

        Expression<Func<IGrouping<string, Order>, object>> proj =
            g => new { g.Key, Count = g.Count() };
        Assert.True(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out _));

        // TWO accumulators: the HAVING's own ("__agg0") and the Select's own ("Count") — a harmless
        // redundant $group field, same precedent as this file's existing orderAccumulators handling.
        Assert.Equal(2, mongoQ.Select.Grouping!.Accumulators.Count);
        Assert.Equal("__agg0", mongoQ.Select.Grouping.Accumulators[0].OutputField);
        Assert.Equal("Count", mongoQ.Select.Grouping.Accumulators[1].OutputField);

        var match = Assert.IsType<MongoBinaryExpression>(mongoQ.Select.GroupHavingPredicate);
        Assert.Equal(MongoBinaryOperator.GreaterThan, match.Operator);
        var left = Assert.IsType<MongoElementRefExpression>(match.Left);
        Assert.Equal("__agg0", left.Path);
        Assert.Null(mongoQ.Select.PendingGroupPredicate);
    }

    [Fact]
    public void HAVING_key_comparison_then_select_binds_grouping_with_no_extra_accumulator()
    {
        var mongoQ = BoundScalarKeyQuery();
        Expression<Func<IGrouping<string, Order>, bool>> wherePred = g => g.Key == "ALFKI";
        Assert.True(NativeGroupByBinder.TryBindGroupWherePredicate(mongoQ, wherePred));

        Expression<Func<IGrouping<string, Order>, object>> proj =
            g => new { g.Key, c = g.Count() };
        Assert.True(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out _));

        // Only the Select's OWN accumulator ("c") — the HAVING key comparison needed none of its own.
        var acc = Assert.Single(mongoQ.Select.Grouping!.Accumulators);
        Assert.Equal("c", acc.OutputField);

        var match = Assert.IsType<MongoBinaryExpression>(mongoQ.Select.GroupHavingPredicate);
        Assert.Equal(MongoBinaryOperator.Equal, match.Operator);
        var left = Assert.IsType<MongoElementRefExpression>(match.Left);
        Assert.Equal("_id", left.Path);
    }
```

- [ ] **Step 2: Run the test file to confirm the new tests fail**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10" -v quiet
dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~NativeGroupByBinderTests"
```

Expected: both new tests FAIL — `TryBindGroupProjection` currently returns `false` whenever
`PendingGroupPredicate` is set (the exact shape this test constructs), so `Assert.True(...TryBindGroupProjection...)`
fails first.

- [ ] **Step 3: Implement the production fix**

In `src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoSelectDefinition.cs`, add a new property right after
`GroupOrderOp`'s declaration (currently ending around line 709):

```csharp
    /// <summary>
    /// A <c>$match</c> predicate to emit immediately after the <c>$group</c> stage and before its flattening
    /// <c>$project</c> — the ordinary "HAVING" case: a <c>Where</c> composed between <c>GroupBy(key)</c> and
    /// the terminal <c>Select</c> (e.g. <c>GroupBy(key).Where(o =&gt; o.Count() &gt; 4).Select(g =&gt; new
    /// { g.Key, Count = g.Count() })</c>). Resolved from <see cref="PendingGroupPredicate"/> by
    /// <c>NativeGroupByBinder.TryBindGroupProjection</c>. Deliberately separate from
    /// <see cref="PostGroupPredicate"/> (the EF-449 bare-<c>GroupBy</c>-terminal-aggregate's own post-group
    /// filter — a query with NO <c>Select</c> at all) and from <see cref="PostGroupOps"/> (ops composed AFTER
    /// the flattening <c>$project</c>, filtering the grouped OUTPUT rows, not group internals).
    /// <see langword="null"/> for every query that never took this path.
    /// </summary>
    internal MongoExpression? GroupHavingPredicate { get; set; }
```

In `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeGroupByBinder.cs`, in `TryBindGroupProjection`,
replace the decline block (currently lines 205–215):

```csharp
        // EF-449: a HAVING Where was stashed between the GroupBy and this Select (e.g. GroupBy(key)
        // .Where(o => o.Count() > 4).Select(g => new { g.Key, Count = g.Count() })) — recognized by
        // NativeSlotPopulator's Where carve-out on the assumption it might be feeding a terminal
        // Count/LongCount/Any (NativeGroupByBinder.TryBindGroupTerminalAggregate), the ONLY consumer that
        // clears it. A Select reaching here instead means that assumption was wrong: this predicate has NO
        // native $match-after-$group mechanism on the flattening-$project path this method builds, so
        // silently finalizing Grouping without applying it would silently DROP the HAVING filter and return
        // every group. Decline so the whole query falls back to driver-LINQ, matching this shape's
        // pre-existing (pre-EF-449) behavior.
        if (select.PendingGroupPredicate != null)
            return false;
```

with:

```csharp
        // EF-322 SP2: a HAVING Where stashed between GroupBy and this Select (e.g. GroupBy(key)
        // .Where(o => o.Count() > 4).Select(g => new { g.Key, Count = g.Count() })) — recognized by
        // NativeSlotPopulator's Where carve-out (NativeGroupByBinder.TryBindGroupWherePredicate). Consumed
        // here: its own accumulator (null for a bare key comparison) is folded into this grouping's
        // accumulators below, and its comparison becomes GroupHavingPredicate, emitted by MongoSelectLowerer
        // as a $match immediately after $group. Cleared unconditionally right away (one-shot, mirroring
        // TryBindGroupTerminalAggregate's own consume discipline) so no stale state survives if this method
        // declines later for an unrelated reason.
        var havingPredicate = select.PendingGroupPredicate;
        select.PendingGroupPredicate = null;
```

Then replace the final commit lines (currently):

```csharp
        select.Grouping = new MongoGrouping(keyParts, [..orderAccumulators, ..accumulators]);
        select.GroupOrderOp = resolvedOrderings.Count > 0 ? new MongoSortOp(resolvedOrderings) : null;
        select.PendingGroupOrderings = null;
```

with:

```csharp
        var havingAccumulators = havingPredicate?.Accumulator is { } havingAcc
            ? new[] { havingAcc }
            : Array.Empty<MongoGroupAccumulator>();
        select.Grouping = new MongoGrouping(keyParts, [..orderAccumulators, ..havingAccumulators, ..accumulators]);
        select.GroupHavingPredicate = havingPredicate?.Comparison;
        select.GroupOrderOp = resolvedOrderings.Count > 0 ? new MongoSortOp(resolvedOrderings) : null;
        select.PendingGroupOrderings = null;
```

(Everything else in `TryBindGroupProjection` — the flatten loop, `bareLeafAlias`, the return — stays unchanged.)

In `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoSelectLowerer.cs`, inside the `if (select.Grouping
is { } grouping && select.SetOperation == null)` block (currently lines 260–311), insert a new block right after
the existing `PostGroupPredicate` block and before the `GroupOrderOp` block:

```csharp
            if (select.PostGroupPredicate is { } postGroupPredicate)
            {
                stages.Add(new MongoMatchStage(postGroupPredicate));
            }

            // EF-322 SP2: the ordinary HAVING case — a Where composed between GroupBy(key) and the terminal
            // Select, resolved by NativeGroupByBinder.TryBindGroupProjection into GroupHavingPredicate. Must
            // run BEFORE GroupOrderOp's sort (HAVING decides which groups exist; ORDER BY only orders the
            // survivors — SQL's own evaluation order) and BEFORE the flatten $project (the predicate may
            // reference an accumulator/_id field the Select itself doesn't project, e.g. a key-only
            // comparison alongside a Sum-only Select).
            if (select.GroupHavingPredicate is { } havingPredicate)
            {
                stages.Add(new MongoMatchStage(havingPredicate));
            }

            if (select.GroupOrderOp is { } groupOrderOp)
            {
                AppendSortStages(groupOrderOp, stages, sortFields);
            }
```

- [ ] **Step 4: Run the test file again to confirm everything passes**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10" -v quiet
dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~NativeGroupByBinderTests"
```

Expected: all tests in the file PASS.

- [ ] **Step 5: Run the whole Unit suite**

```bash
dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj \
  -c "Debug EF10" --no-build
```

Expected: PASS, 0 failed.

- [ ] **Step 6: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoSelectDefinition.cs \
        src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeGroupByBinder.cs \
        src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoSelectLowerer.cs \
        tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/NativeGroupByBinderTests.cs
git commit -m "EF-322: emit native \$match for a GroupBy HAVING clause"
```

---

### Task 3: Verify the target spec-test methods, add functional coverage, run every suite

**Files:**
- Test (read-only unless Step 3 finds a diff):
  `tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/NorthwindGroupByQueryMongoTest.cs`
- Modify: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeGroupByTests.cs` — add 2 new tests
  (no existing test in this file exercises the true "Where between GroupBy and Select" HAVING shape — confirmed
  by grep; the file's existing `GroupBy_HAVING_on_...` tests filter the Select's OUTPUT instead, a different,
  already-working shape this plan does not touch)

**Interfaces:**
- Consumes: Task 1 + Task 2's production changes — this task is pure verification plus new test coverage, no
  further production code.

- [ ] **Step 1: Confirm the 3 target methods' CURRENT baselines (before touching anything)**

```bash
grep -n "GroupBy_filter_count\b\|GroupBy_filter_count_OrderBy_count_Select_sum\|GroupBy_filter_key\b" \
  tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/NorthwindGroupByQueryMongoTest.cs
```

Read each method's current body. As of this plan's writing, the driver-LINQ fallback ALREADY emits a `$match`
whose shape happens to closely resemble what native will produce (`GroupBy_filter_key`'s baseline is
`{"$match": {"_id": "ALFKI"}}` — a plain field match; `GroupBy_filter_count`'s is `{"$match": {"$expr":
{"$gt": ["$__agg0", 4]}}}` — note the SAME `"__agg0"` naming this plan's accumulator uses). `GroupBy_filter_count_
OrderBy_count_Select_sum`'s current baseline uses a much more complex driver-internal `_document`/`$replaceRoot`
sort trick that native will NOT reproduce (native emits a plain `$sort` on `"__agg0"`/`"_id"` via `GroupOrderOp`)
— expect THIS one's baseline to change; the other two may already match byte-for-byte. Confirm which is which
in Step 3 rather than assuming.

- [ ] **Step 2: Confirm Docker (or `MONGODB_URI`) is available**

```bash
docker info >/dev/null 2>&1 && echo "docker OK" || echo "need MONGODB_URI or Docker"
```

If neither is available, stop and ask the user to start Docker or set `MONGODB_URI`.

- [ ] **Step 3: Run the 3 target methods under default (`Native`) mode; regenerate only the baseline(s) that changed**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10" -v quiet
dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~NorthwindGroupByQueryMongoTest&(FullyQualifiedName~GroupBy_filter_count|FullyQualifiedName~GroupBy_filter_key)" \
  --logger "console;verbosity=normal" > /tmp/sp2-native.log 2>&1
tail -40 /tmp/sp2-native.log
```

(The filter substring `GroupBy_filter_count` matches both `GroupBy_filter_count` and
`GroupBy_filter_count_OrderBy_count_Select_sum` — `~` is a Contains match, both are covered; `GroupBy_filter_key`
covers the third.)

For any method whose `AssertMql` fails (baseline mismatch, not a data/behavior failure — confirm the failure is
`Assert.Equal() Failure: Strings differ`, not something else), regenerate just that one:

```bash
EF_TEST_REWRITE_BASELINES=1 dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~NorthwindGroupByQueryMongoTest.<MethodName>"
git diff tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/NorthwindGroupByQueryMongoTest.cs
```

Confirm the diff is a plausible `$group`/`$match`[/`$sort`]/`$project` shape (a `$match` stage between the
`$group` and the flattening `$project`, referencing `"__agg0"` or `"_id"` directly — never wrapped in an extra
`$group`), then rebuild and rerun WITHOUT the env var to confirm green.

- [ ] **Step 4: Run the same 3 methods under `MONGODB_EF_NATIVE_ONLY=1`**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10" -v quiet
MONGODB_EF_NATIVE_ONLY=1 dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~NorthwindGroupByQueryMongoTest&(FullyQualifiedName~GroupBy_filter_count|FullyQualifiedName~GroupBy_filter_key)" \
  --logger "console;verbosity=normal" > /tmp/sp2-nativeonly.log 2>&1
tail -20 /tmp/sp2-nativeonly.log
```

Expected: all 3 methods (6 with async) now PASS under `NativeOnly` — previously they threw
`NativeTranslationNotSupportedException`.

- [ ] **Step 5: Re-measure the whole class's `NativeOnly` failure count**

```bash
MONGODB_EF_NATIVE_ONLY=1 dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~NorthwindGroupByQueryMongoTest" \
  --logger "console;verbosity=normal" > /tmp/sp2-full-nativeonly.log 2>&1
tail -8 /tmp/sp2-full-nativeonly.log
grep -E "^\s*Failed MongoDB" /tmp/sp2-full-nativeonly.log | sed -E 's/^\s*Failed //; s/\(async:.*//' | sed 's/ *$//' | sort -u
```

Expected: failure count drops from 50 (SP1's ending count) to 44 (50 − 6). If it's not exactly 44, or the
method-name list doesn't match "SP1's 25 methods minus these 3," investigate before proceeding — don't accept
a different number without understanding why (SP1's final review found exactly this kind of surprise; see its
ledger for the pattern of how to investigate).

- [ ] **Step 6: Add the two missing functional-level HAVING tests**

Open `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeGroupByTests.cs`. Find
`GroupBy_anonymous_key_only_with_no_aggregate_goes_native` (the last test in the file) and add these two tests
immediately after it, before the file's closing `}`:

```csharp
    [Fact]
    public void GroupBy_HAVING_on_accumulator_before_select_goes_native_under_native_only()
    {
        // EF-322 SP2: a Where composed BETWEEN GroupBy(key) and the terminal Select (a true HAVING clause,
        // referencing g.Count() on the IGrouping directly) — distinct from this file's existing
        // GroupBy_HAVING_on_..._matches_driver_linq tests, whose Where runs AFTER the Select over the
        // flattened alias instead (a different, already-working shape).
        using var db = CreateContext(SeedOrders(), MongoQueryMode.NativeOnly,
            nameof(GroupBy_HAVING_on_accumulator_before_select_goes_native_under_native_only));

        var result = db.Entities
            .GroupBy(o => o.Country)
            .Where(g => g.Count() > 1)
            .Select(g => new { g.Key, Count = g.Count() })
            .AsEnumerable()
            .OrderBy(r => r.Key)
            .ToList();

        Assert.Equal([("UK", 2), ("US", 2)], result.Select(r => (r.Key, r.Count)).ToArray());
    }

    [Fact]
    public void GroupBy_HAVING_on_key_before_select_goes_native_under_native_only()
    {
        using var db = CreateContext(SeedOrders(), MongoQueryMode.NativeOnly,
            nameof(GroupBy_HAVING_on_key_before_select_goes_native_under_native_only));

        var result = db.Entities
            .GroupBy(o => o.Country)
            .Where(g => g.Key == "FR")
            .Select(g => new { g.Key, Count = g.Count() })
            .ToList();

        var row = Assert.Single(result);
        Assert.Equal("FR", row.Key);
        Assert.Equal(1, row.Count);
    }
```

(`SeedOrders()` in this file has US=2, UK=2, FR=1 rows — confirmed by reading the existing
`GroupBy_scalar_key_with_count_and_sum_goes_native` test's own assertions in the same file.)

- [ ] **Step 7: Run the new functional tests, then the whole Functional suite**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10" -v quiet
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~NativeGroupByTests" --logger "console;verbosity=normal" \
  > /tmp/sp2-functional-groupby.log 2>&1
tail -10 /tmp/sp2-functional-groupby.log
```

Expected: PASS, 0 failed, including the 2 new tests.

```bash
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --no-build --logger "console;verbosity=normal" > /tmp/sp2-functional-full.log 2>&1
tail -8 /tmp/sp2-functional-full.log
```

Expected: PASS, 0 failed (this is the FULL suite, not just `NativeGroupByTests` — SP1's final review found a
prior slice had never run this at all; do not skip it).

- [ ] **Step 8: Run the full Specification suite (not just the GroupBy class)**

```bash
dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --logger "console;verbosity=normal" > /tmp/sp2-spec-full.log 2>&1
tail -8 /tmp/sp2-spec-full.log
```

Expected: PASS, 0 failed.

- [ ] **Step 9: Run all three suites on EF8 and EF9**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF8" -v quiet
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF9" -v quiet

for CFG in EF8 EF9; do
  echo "=== $CFG unit ==="
  dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj -c "Debug $CFG" --no-build 2>&1 | tail -5
  echo "=== $CFG spec (GroupBy class) ==="
  dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj -c "Debug $CFG" --no-build --filter "FullyQualifiedName~NorthwindGroupByQueryMongoTest" 2>&1 | tail -5
  echo "=== $CFG functional (GroupBy/Distinct) ==="
  dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj -c "Debug $CFG" --no-build --filter "FullyQualifiedName~NativeGroupByTests|FullyQualifiedName~NativeDistinctTests" 2>&1 | tail -5
done
```

Expected: PASS, 0 failed, on both EF8 and EF9, for all three commands.

- [ ] **Step 10: Commit any baseline changes from Step 3, plus the new functional tests**

```bash
git add tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/NorthwindGroupByQueryMongoTest.cs \
        tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeGroupByTests.cs
git commit -m "EF-322: verify SP2 HAVING slice — baselines, functional coverage, full suites"
```

---

## After this plan

This plan covers SP2 only (3 of the remaining tests, per the design doc's corrected scope). SP3–SP9 each get
their own plan when their turn comes, using this slice's post-landing `NativeOnly` failure count (44, confirmed
in Task 3 Step 5) as their new baseline. Before starting SP3's plan, re-verify SP3's own target tests' shapes
against the actual EF Core base-test source the same way this plan's own investigation corrected SP2's scope —
don't assume the design doc's original bucketing is exact without a quick re-check.
