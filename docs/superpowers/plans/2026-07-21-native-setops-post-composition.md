# Native Set-Op Post-Composition (Slice B) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `Where`/`OrderBy`/`ThenBy`/`Skip`/`Take`, scalar aggregates, and `First`/`Single` reducers composed **after** a native whole-entity set operation (`Union`/`Concat`/`Intersect`/`Except`) execute as native MongoDB aggregation stages appended after the set-op stage, instead of falling back to driver-LINQ (Union/Concat) or hard-failing (Intersect/Except).

**Architecture:** Approach 1 — a second ordered op list. `MongoSelectDefinition` gains `TrailingOps`; its five op-merge methods write to `PipelineOps` normally but to `TrailingOps` once a `SetOperation` has been attached (the "active list" flips at set-op attach time). The lowerer emits `PipelineOps` → set-op stage → `TrailingOps` → the `Cardinality` stage. Two catch-all guard sites (`NativeSlotPopulator`, `NativeCardinalityBinder`) are relaxed by `!IsSetOpTerminalOnly` so the target operators fall through to record into `TrailingOps` for a set-op-only terminal; every deferred operator keeps its own untouched `HasTerminalOperator` guard.

**Tech Stack:** C# / .NET (net8.0 for EF8/EF9, net10.0 for EF10), EF Core provider internals, MongoDB C# driver, xUnit (plain `Assert.*`, no FluentAssertions).

## Global Constraints

- `<Nullable>enable</Nullable>` on `src/` — annotate new members accordingly.
- **Preserve file BOMs** on every edited file.
- Multi-EF: this slice is behaviorally identical across EF8/EF9/EF10 — **no `#if` guards** are introduced. Build/test each version via the `-c "Debug EF8|EF9|EF10"` configuration.
- Tests are **plain xUnit `Assert.*`** — FluentAssertions is not referenced. Unit tests need no DB; functional tests hit a real MongoDB (leave `MONGODB_URI` and `ATLAS_URI` unset → isolated atlas-local container).
- `<NoWarn>EF1001</NoWarn>` — consuming EF internal APIs is expected.
- Stacked-PR workflow: this branch is stacked off the native rolling tip `84e67c7`; the whole slice lands as **one squashed commit** at the end (Task 6), with a `-presquash` backup branch kept until merge (per the native-stack workflow).
- The **only reliable "goes native" signal** for filter/sort/paging is `MongoQueryMode.NativeOnly` succeeding (an identical fallback pipeline would throw). Prove native this way; do not rely on MQL substring shape for filter/sort/paging.

**Build once before starting** (subsequent test runs use `--no-build` where possible):
```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"
```

---

### Task 1: IR — `TrailingOps`, active-list routing, `IsSetOpTerminalOnly`

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoSelectDefinition.cs`
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoSelectDefinitionTrailingOpsTests.cs` (create)

**Interfaces:**
- Consumes: existing `MongoSelectDefinition` members — `SetOperation` (`MongoSetOperation?`), `IsSetOp`, `IsGroupBy`, `IsDistinct`, `Grouping`, `UnwindSource`, the five merge methods (`AddPredicateConjunct`/`StartOrReplaceSort`/`AppendThenBy`/`AppendSkip`/`AppendLimit`), `MongoSelectOp` subtypes (`MongoMatchOp`/`MongoSortOp`/`MongoSkipOp`/`MongoLimitOp`).
- Produces: `IReadOnlyList<MongoSelectOp> TrailingOps` (public), `internal bool IsSetOpTerminalOnly`. After this task the merge methods route to `TrailingOps` whenever `SetOperation != null`.

- [ ] **Step 1: Write the failing test**

Create `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoSelectDefinitionTrailingOpsTests.cs` (copy the exact license header + BOM from `MongoSetOperationRouteTests.cs` in the same folder):

```csharp
using MongoDB.EntityFrameworkCore.Query.Expressions;
using Xunit;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query.NativeTranslation;

public class MongoSelectDefinitionTrailingOpsTests
{
    private static MongoSelectDefinition WithSetOp()
    {
        var select = new MongoSelectDefinition();
        // Record a pre-set-op op first (source1's own), then attach the set op.
        select.AddPredicateConjunct(new MongoConstantExpression(true, forSerialization: null));
        select.SetOperation = new MongoSetOperation(
            MongoSetOperationKind.Union, new MongoSelectDefinition(), "customers");
        select.IsSetOp = true;
        return select;
    }

    [Fact]
    public void Ops_before_set_op_go_to_PipelineOps()
    {
        var select = new MongoSelectDefinition();
        select.AddPredicateConjunct(new MongoConstantExpression(true, forSerialization: null));
        Assert.Single(select.PipelineOps);
        Assert.Empty(select.TrailingOps);
    }

    [Fact]
    public void Ops_after_set_op_go_to_TrailingOps()
    {
        var select = WithSetOp();
        // The pre-set-op predicate stayed in PipelineOps; a new predicate now lands in TrailingOps.
        select.AddPredicateConjunct(new MongoConstantExpression(false, forSerialization: null));
        Assert.Single(select.PipelineOps);
        Assert.Single(select.TrailingOps);
        Assert.IsType<MongoMatchOp>(select.TrailingOps[0]);
    }

    [Fact]
    public void Trailing_sort_skip_limit_route_to_TrailingOps()
    {
        var select = WithSetOp();
        select.StartOrReplaceSort(new MongoOrdering(new MongoConstantExpression(0, forSerialization: null), true));
        select.AppendSkip(new MongoConstantExpression(1, forSerialization: null));
        select.AppendLimit(new MongoConstantExpression(2, forSerialization: null));
        Assert.Collection(select.TrailingOps,
            op => Assert.IsType<MongoSortOp>(op),
            op => Assert.IsType<MongoSkipOp>(op),
            op => Assert.IsType<MongoLimitOp>(op));
    }

    [Fact]
    public void IsSetOpTerminalOnly_true_for_a_plain_set_op()
        => Assert.True(WithSetOp().IsSetOpTerminalOnly);

    [Fact]
    public void IsSetOpTerminalOnly_false_when_no_set_op()
        => Assert.False(new MongoSelectDefinition().IsSetOpTerminalOnly);

    [Fact]
    public void IsSetOpTerminalOnly_false_when_also_grouped()
    {
        var select = WithSetOp();
        select.IsGroupBy = true; // defensive: a mixed terminal must not count as set-op-only
        Assert.False(select.IsSetOpTerminalOnly);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run:
```bash
dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj \
  -c "Debug EF10" --filter "FullyQualifiedName~MongoSelectDefinitionTrailingOpsTests"
```
Expected: FAIL — compile errors (`TrailingOps` / `IsSetOpTerminalOnly` do not exist) or assertion failures (ops still land in `PipelineOps`).

- [ ] **Step 3: Add the trailing list + active-list routing**

In `MongoSelectDefinition.cs`, immediately after the `_pipelineOps` field/`PipelineOps` property (around line 43–50), add:

```csharp
    // ── Trailing ops (post-set-op composition, EF-347 slice B) ────────────────────
    // A SECOND ordered filter/sort/page list, emitted by the lowerer AFTER the set-op stage. A set op is
    // terminal for everything except the operators relaxed in slice B (Where/OrderBy/ThenBy/Skip/Take +
    // aggregates/reducers); those record here instead of PipelineOps so they filter/sort/page the COMBINED
    // set-op result, not source1's pre-set-op rows. The flip point is single and well-defined: once
    // SetOperation is attached, ActiveOps is _trailingOps (see below).
    private readonly List<MongoSelectOp> _trailingOps = [];

    /// <summary>
    /// The ordered filter/sort/page operations recorded AFTER a set op was attached (EF-347 slice B). The
    /// lowerer emits these verbatim after the set-op stage. Empty for every non-set-op query and for a set op
    /// with no post-composition.
    /// </summary>
    public IReadOnlyList<MongoSelectOp> TrailingOps => _trailingOps;

    /// <summary>
    /// The op list the five merge methods currently target: <see cref="TrailingOps"/> once a set op has been
    /// attached (so post-set-op ops are trailing), otherwise <see cref="PipelineOps"/> (source1's own /
    /// pre-terminal ops). The single flip point for the post-set-op composition machinery.
    /// </summary>
    private List<MongoSelectOp> ActiveOps => SetOperation != null ? _trailingOps : _pipelineOps;
```

Then change the five merge methods to target `ActiveOps` instead of `_pipelineOps`. Replace their bodies:

```csharp
    public void AddPredicateConjunct(MongoExpression conjunct)
    {
        var ops = ActiveOps;
        if (ops.Count > 0 && ops[^1] is MongoMatchOp match)
            ops[^1] = new MongoMatchOp(
                new MongoBinaryExpression(MongoBinaryOperator.AndAlso, match.Predicate, conjunct));
        else
            ops.Add(new MongoMatchOp(conjunct));
    }

    public void StartOrReplaceSort(MongoOrdering first)
    {
        var ops = ActiveOps;
        if (ops.Count > 0 && ops[^1] is MongoSortOp)
            ops[^1] = new MongoSortOp([first]);
        else
            ops.Add(new MongoSortOp([first]));
    }

    public void AppendThenBy(MongoOrdering next)
    {
        var ops = ActiveOps;
        if (ops.Count > 0 && ops[^1] is MongoSortOp sort)
            ops[^1] = new MongoSortOp([.. sort.Orderings, next]);
        else
            ops.Add(new MongoSortOp([next]));
    }

    public void AppendSkip(MongoExpression count) => ActiveOps.Add(new MongoSkipOp(count));

    public void AppendLimit(MongoExpression count) => ActiveOps.Add(new MongoLimitOp(count));
```

Leave the XML doc comments on those methods intact (they still describe the merge semantics — now applied to whichever list is active). Add one sentence to the `AddPredicateConjunct` doc: `Targets <see cref="TrailingOps"/> once a set op is attached (EF-347 slice B), else <see cref="PipelineOps"/>.` (and similarly a brief note is fine on the others, optional).

`HasPaging`/`HasOrdering`/`HasLimit` stay scanning `_pipelineOps` only — **do not** change them. Add a one-line comment above `HasPaging`:
```csharp
    // HasPaging/HasOrdering/HasLimit deliberately scan _pipelineOps only: they gate a PRE-terminal GroupBy
    // (NativeGroupByBinder), which is unreachable after a set op (a trailing GroupBy is rejected by
    // HasTerminalOperator), so they must not see the post-set-op _trailingOps (EF-347 slice B).
```

- [ ] **Step 4: Add `IsSetOpTerminalOnly`**

In `MongoSelectDefinition.cs`, immediately after the `HasTerminalOperator` property (around line 218), add:

```csharp
    /// <summary>
    /// <see langword="true"/> when the ONLY terminal on this select is a set operation — i.e. a set op is
    /// attached and no grouping/distinct/unwind terminal is (EF-347 slice B). A set op only ever attaches to a
    /// plain whole-entity select, so <see cref="IsSetOp"/> already implies the rest; the explicit conjunction
    /// is defensive so the slice-B guard relaxation can never accidentally open a GroupBy/Distinct/SelectMany
    /// terminal. Used to relax the two catch-all post-terminal guards (NativeSlotPopulator,
    /// NativeCardinalityBinder) for the operators composed after a set op, while every deferred operator's own
    /// HasTerminalOperator guard stays tripped.
    /// </summary>
    internal bool IsSetOpTerminalOnly
        => IsSetOp && !IsGroupBy && !IsDistinct && Grouping == null && UnwindSource == null;
```

- [ ] **Step 5: Run test to verify it passes**

Run:
```bash
dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj \
  -c "Debug EF10" --filter "FullyQualifiedName~MongoSelectDefinitionTrailingOpsTests"
```
Expected: PASS (6 tests). Also re-run `MongoSetOperationRouteTests` to confirm no regression:
```bash
dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~MongoSetOperationRouteTests"
```
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoSelectDefinition.cs \
        tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoSelectDefinitionTrailingOpsTests.cs
git commit -m "EF-347 slice B task 1: TrailingOps + active-list routing + IsSetOpTerminalOnly"
```

---

### Task 2: Lowerer — emit `TrailingOps` after the set-op stage, fall through to `Cardinality`

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoSelectLowerer.cs`
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoSelectLowererTests.cs`

**Interfaces:**
- Consumes: `MongoSelectDefinition.TrailingOps` (Task 1), `MongoSelectDefinition.Cardinality`, existing stage types (`MongoMatchStage`/`MongoSortStage`/`MongoSkipStage`/`MongoLimitStage`/`MongoUnionWithStage`/`MongoSetDifferenceStage`/`MongoCountStage`), `MongoCardinality.ForAggregate`.
- Produces: no new public surface; changes `Lower`'s emission so trailing ops + a trailing cardinality stage follow the set-op stage.

- [ ] **Step 1: Write the failing test**

Append to `MongoSelectLowererTests.cs` (before the final closing brace). The existing `SetOperation_appends_union_stage_after_canonical_stages` test at ~line 247 shows the construction pattern (`TestSelect()` returns a `MongoQueryExpression`; `.Select` is the definition):

```csharp
    // ── EF-347 slice B: trailing ops emit AFTER the set-op stage; cardinality falls through ──

    [Fact]
    public void Trailing_ops_lower_after_the_set_op_stage()
    {
        var query = TestSelect();
        // source1's own pre-set-op predicate:
        query.Select.AddPredicateConjunct(new MongoConstantExpression(true, null));
        query.Select.SetOperation = new MongoSetOperation(
            MongoSetOperationKind.Union, new MongoSelectDefinition(), "customers");
        query.Select.IsSetOp = true;
        // post-set-op (trailing) sort — routes to TrailingOps because SetOperation is now attached:
        query.Select.StartOrReplaceSort(new MongoOrdering(new MongoConstantExpression(0, null), true));

        var stages = new MongoSelectLowerer().Lower(query);

        // $match (pre-set-op) → $unionWith (set op) → $sort (trailing), in that order.
        Assert.Collection(stages,
            s => Assert.IsType<MongoMatchStage>(s),
            s => Assert.IsType<MongoUnionWithStage>(s),
            s => Assert.IsType<MongoSortStage>(s));
    }

    [Fact]
    public void Trailing_cardinality_lowers_after_the_set_op_stage()
    {
        var query = TestSelect();
        query.Select.SetOperation = new MongoSetOperation(
            MongoSetOperationKind.Intersect, new MongoSelectDefinition(), "customers");
        query.Select.IsSetOp = true;
        query.Select.Cardinality = MongoCardinality.ForAggregate(
            MongoAggregateOperator.Count, selector: null, MongoEmptyAggregateBehavior.DefaultValue,
            emptyValue: 0, typeof(int), presenceOnly: false, presentValue: null);

        var stages = new MongoSelectLowerer().Lower(query);

        // set-difference stage (Intersect/Except) → $count. The lowerer must NOT early-return after the
        // set-op stage — it must fall through to the Cardinality block.
        Assert.Collection(stages,
            s => Assert.IsType<MongoSetDifferenceStage>(s),
            s => Assert.IsType<MongoCountStage>(s));
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run:
```bash
dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj \
  -c "Debug EF10" --filter "FullyQualifiedName~MongoSelectLowererTests.Trailing"
```
Expected: FAIL — the current `Lower` does `return stages;` right after the set-op stage, so the trailing `$sort`/`$count` are never emitted (the collection has 2 stages, not 3 / the cardinality stage is missing).

- [ ] **Step 3: Emit trailing ops + fall through in the lowerer**

In `MongoSelectLowerer.cs`, in the `if (select.SetOperation is { } setOp)` block (around lines 76–90), **replace the `return stages;`** with a trailing-ops emission and let control fall through:

```csharp
        if (select.SetOperation is { } setOp)
        {
            var operandStages = new List<MongoPipelineStage>();
            AppendSelectOpStages(setOp.OperandSelect.PipelineOps, operandStages);
            if (setOp.Kind is MongoSetOperationKind.Intersect or MongoSetOperationKind.Except)
            {
                stages.Add(new MongoSetDifferenceStage(setOp.Kind, operandStages, setOp.OperandCollectionName));
            }
            else
            {
                stages.Add(new MongoUnionWithStage(
                    operandStages, setOp.OperandCollectionName, dedup: setOp.Kind == MongoSetOperationKind.Union));
            }

            // EF-347 slice B: post-set-op composition. Trailing $match/$sort/$skip/$limit emit AFTER the
            // set-op stage (they operate on the COMBINED result), then fall through to the Cardinality block
            // for a post-set-op aggregate/reducer. A set op only attaches to a plain whole-entity select, so
            // UnwindSource/Grouping/Projection are all empty here and their blocks below are skipped; a future
            // slice composing one of those after a set op MUST revisit this precedence.
            AppendSelectOpStages(select.TrailingOps, stages);
            // NB: no early return — control continues to the Cardinality block.
        }
```

This requires `AppendSelectOpStages` to take an op list rather than a `MongoSelectDefinition`. Change its signature and body (around lines 164–178) from taking `MongoSelectDefinition select` / iterating `select.PipelineOps` to taking the list directly:

```csharp
    private static void AppendSelectOpStages(IReadOnlyList<MongoSelectOp> ops, List<MongoPipelineStage> stages)
    {
        foreach (var op in ops)
        {
            stages.Add(op switch
            {
                MongoMatchOp m => new MongoMatchStage(m.Predicate),
                MongoSortOp s => new MongoSortStage(s.Orderings),
                MongoSkipOp k => new MongoSkipStage(k.Count),
                MongoLimitOp l => new MongoLimitStage(l.Count),
                _ => throw new NativeTranslationNotSupportedException(
                    $"Unknown select op '{op.GetType().Name}'.")
            });
        }
    }
```

Update the **stage-1** call site (around line 64) from `AppendSelectOpStages(select, stages);` to `AppendSelectOpStages(select.PipelineOps, stages);`. (The operand call site inside the set-op block is already updated above to `setOp.OperandSelect.PipelineOps`.) Update the doc-comment on `AppendSelectOpStages` to say it takes an op list, shared by the outer `PipelineOps`, the operand's `PipelineOps`, and the outer `TrailingOps`.

- [ ] **Step 4: Run test to verify it passes**

Run:
```bash
dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj \
  -c "Debug EF10" --filter "FullyQualifiedName~MongoSelectLowererTests"
```
Expected: PASS — the two new tests plus all existing lowerer tests (the set-op / canonical / projection / grouping tests must still pass — they exercise the refactored `AppendSelectOpStages`).

- [ ] **Step 5: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoSelectLowerer.cs \
        tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoSelectLowererTests.cs
git commit -m "EF-347 slice B task 2: lowerer emits TrailingOps after set-op stage, falls through to cardinality"
```

---

### Task 3: Relax `NativeSlotPopulator` — Where/OrderBy/ThenBy/Skip/Take after a set op

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeSlotPopulator.cs`
- Test: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeSetOpsTests.cs`

**Interfaces:**
- Consumes: `MongoSelectDefinition.IsSetOpTerminalOnly` (Task 1); the trailing-op emission (Task 2).
- Produces: after this task the seven slot operators, composed after a set op, record into `TrailingOps` and go native (Union/Concat gain native coverage; Intersect/Except gain native coverage where they previously hard-failed).

- [ ] **Step 1: Write the failing tests**

In `NativeSetOpsTests.cs`, **first repurpose the existing tests that now flip to native**, then add new coverage.

(a) **Repurpose `Except_then_Where_hard_fails_in_every_mode`** (currently ~line 249) — a trailing `Where` after `Except` now goes native. Replace the whole method with:

```csharp
    // EF-347 slice B: a trailing Where after Except now goes native (no driver-LINQ baseline for
    // Intersect/Except, so assert the result set vs expected in-memory data + prove native via NativeOnly).
    [Fact]
    public void Except_then_Where_goes_native()
    {
        var collection = SeedCollection(nameof(Except_then_Where_goes_native));
        using var db = Make(collection, MongoQueryMode.NativeOnly);
        // {1,2,3} Except {3,4,5} = {1,2}; then Where(Value >= 2) = {2}. If the $match wrongly emitted BEFORE
        // the set-difference stage this would still be {2} by coincidence, so this test is backed by the
        // seam-discriminating paging/Count tests below — it exists to prove Except+Where goes native at all.
        var result = db.Entities.Where(i => i.Value <= 3).Except(db.Entities.Where(i => i.Value >= 3))
            .Where(i => i.Value >= 2).ToList();
        Assert.Equal([2], result.Select(i => i.Value).OrderBy(v => v));
    }
```

(b) **Repurpose `Where_after_union_falls_back`** (currently ~line 521) — now native, with a Union parity oracle. Replace the whole method with:

```csharp
    // EF-347 slice B: a trailing Where after Union now goes native. Union HAS a driver-LINQ baseline, so
    // assert Native == DriverLinq parity.
    [Fact]
    public void Where_after_union_goes_native()
    {
        var collection = SeedCollection(nameof(Where_after_union_goes_native));
        using var nativeOnlyDb = Make(collection, MongoQueryMode.NativeOnly);
        using var driverDb = Make(collection, MongoQueryMode.DriverLinq);

        List<int> Run(SingleEntityDbContext<Item> db) =>
            db.Entities.Where(i => i.Value <= 3).Union(db.Entities.Where(i => i.Value >= 3))
                .Where(i => i.Value >= 2)
                .ToList().Select(i => i.Value).OrderBy(v => v).ToList();

        var native = Run(nativeOnlyDb); // NativeOnly succeeding proves it went native
        Assert.Equal([2, 3, 4, 5], native);
        Assert.Equal(Run(driverDb), native);
    }
```

(c) **Repurpose `OrderBy_after_union_falls_back`** (~line 542) and **`Skip_take_after_union_falls_back`** (~line 563). Replace each with a native+parity version. For `OrderBy_after_union_falls_back` → rename to `OrderBy_after_union_goes_native`:

```csharp
    [Fact]
    public void OrderBy_after_union_goes_native()
    {
        var collection = SeedCollection(nameof(OrderBy_after_union_goes_native));
        using var nativeOnlyDb = Make(collection, MongoQueryMode.NativeOnly);
        using var driverDb = Make(collection, MongoQueryMode.DriverLinq);

        List<int> Run(SingleEntityDbContext<Item> db) =>
            db.Entities.Where(i => i.Value <= 3).Union(db.Entities.Where(i => i.Value >= 3))
                .OrderBy(i => i.Value)
                .ToList().Select(i => i.Value).ToList();

        var native = Run(nativeOnlyDb);
        Assert.Equal([1, 2, 3, 4, 5], native); // already sorted by the native $sort, no in-memory re-sort
        Assert.Equal(Run(driverDb), native);
    }
```

For `Skip_take_after_union_falls_back` → rename to the **seam-discriminating** `Paging_after_union_goes_native` (this is the hazard-1 discriminator — paging the combined ordered stream differs completely from paging source1):

```csharp
    [Fact]
    public void Paging_after_union_goes_native()
    {
        var collection = SeedCollection(nameof(Paging_after_union_goes_native));
        using var nativeOnlyDb = Make(collection, MongoQueryMode.NativeOnly);
        using var driverDb = Make(collection, MongoQueryMode.DriverLinq);

        // Union = {1,2,3,4,5} ordered; Skip(1).Take(2) = {2,3}. Paging source1 ({1,2,3}) would give {2,3}
        // too here — so ALSO assert the Count discriminator in Task 4. This case proves paging composes.
        List<int> Run(SingleEntityDbContext<Item> db) =>
            db.Entities.Where(i => i.Value <= 3).Union(db.Entities.Where(i => i.Value >= 3))
                .OrderBy(i => i.Value).Skip(1).Take(2)
                .ToList().Select(i => i.Value).ToList();

        var native = Run(nativeOnlyDb);
        Assert.Equal([2, 3], native);
        Assert.Equal(Run(driverDb), native);
    }
```

(d) **Split `IntersectComposedOps`** (~line 262) — `OrderBy`/`Skip`/`Take` now go native; only `GroupBy` still hard-fails. Change the `IntersectComposedOps` member data to keep **only** `GroupBy` (and the test method name stays `Intersect_then_op_hard_fails_under_native`):

```csharp
    public static IEnumerable<object[]> IntersectComposedOps() => new[]
    {
        // Only DEFERRED operators remain here (EF-347 slice B): GroupBy after a set op still hard-fails.
        // OrderBy/Skip/Take moved to Intersect_then_paging_goes_native below.
        new object[] { "GroupBy", (Func<IQueryable<Item>, object>)(q => q.GroupBy(i => i.Value).Select(g => g.Key).ToList()) },
    };
```

Add a new native+result-set test for the flipped Intersect operators (no baseline → in-memory expected):

```csharp
    [Fact]
    public void Intersect_then_paging_goes_native()
    {
        var collection = SeedCollection(nameof(Intersect_then_paging_goes_native));
        using var db = Make(collection, MongoQueryMode.NativeOnly);
        // {1,2,3} Intersect {2,3,4} = {2,3}; OrderBy(Value).Take(1) = {2}.
        var result = db.Entities.Where(i => i.Value <= 3).Intersect(db.Entities.Where(i => i.Value >= 2 && i.Value <= 4))
            .OrderBy(i => i.Value).Take(1).ToList();
        Assert.Equal([2], result.Select(i => i.Value));
    }
```

(e) Add a **parametrized trailing predicate** test (hazard 3):

```csharp
    [Fact]
    public void Union_then_parametrized_trailing_Where_goes_native()
    {
        var collection = SeedCollection(nameof(Union_then_parametrized_trailing_Where_goes_native));
        using var db = Make(collection, MongoQueryMode.NativeOnly);
        var threshold = 4;
        var result = db.Entities.Where(i => i.Value <= 3).Union(db.Entities.Where(i => i.Value >= 3))
            .Where(i => i.Value >= threshold).ToList();
        Assert.Equal([4, 5], result.Select(i => i.Value).OrderBy(v => v));
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run (leave `MONGODB_URI`/`ATLAS_URI` unset for an isolated container):
```bash
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --filter "FullyQualifiedName~NativeSetOpsTests"
```
Expected: FAIL — the repurposed/new native tests throw `NativeTranslationNotSupportedException` under `NativeOnly` (the slot operator after a set op still trips the un-relaxed guard).

- [ ] **Step 3: Relax the `NativeSlotPopulator` guard**

In `NativeSlotPopulator.cs`, the post-terminal guard at the top of `PopulateNativeSlots` (currently `if (mongoQ.Select.HasTerminalOperator && IsPostGroupSlotOperator(methodDefinition))`) — add `&& !IsSetOpTerminalOnly`:

```csharp
        if (mongoQ.Select.HasTerminalOperator && !mongoQ.Select.IsSetOpTerminalOnly
            && IsPostGroupSlotOperator(methodDefinition))
        {
            mongoQ.Select.MarkNotNativelyRepresentable();
            return;
        }
```

Add to the guard's comment block:
```csharp
        // EF-347 slice B: a set-op-only terminal is EXEMPT — the seven slot operators composed after a set op
        // fall through to their arms below and record into TrailingOps (MongoSelectDefinition.ActiveOps flips
        // once SetOperation is attached), so they filter/sort/page the COMBINED result and emit after the
        // set-op stage. Only a set-op-ONLY terminal is exempt: a GroupBy/Distinct/SelectMany terminal (or a
        // mixed one) still trips this guard and falls back. The deferred own-override operators (Select/
        // Distinct/GroupBy/SelectMany/OfType, chained set ops) each keep their own untouched HasTerminalOperator
        // guard, so they stay terminal after a set op.
```

- [ ] **Step 4: Run tests to verify they pass**

Run:
```bash
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --filter "FullyQualifiedName~NativeSetOpsTests"
```
Expected: PASS — all repurposed + new slot-operator tests, plus the still-hard-fail `Intersect_then_op_hard_fails_under_native` (now GroupBy-only), plus every unchanged set-op test (native-success, result-set, parity, field-collision, operand-with-include fallback).

- [ ] **Step 5: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeSlotPopulator.cs \
        tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeSetOpsTests.cs
git commit -m "EF-347 slice B task 3: Where/OrderBy/paging after a set op go native"
```

---

### Task 4: Relax `NativeCardinalityBinder` — aggregates + reducers after a set op

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeCardinalityBinder.cs`
- Test: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeSetOpsTests.cs`

**Interfaces:**
- Consumes: `MongoSelectDefinition.IsSetOpTerminalOnly` (Task 1); trailing-op emission + fall-through-to-cardinality (Task 2).
- Produces: after this task `Count`/`LongCount`/`Sum`/`Min`/`Max`/`Average`/`Any`/`All` and `First`/`FirstOrDefault`/`Single`/`SingleOrDefault` composed after a set op go native.

- [ ] **Step 1: Write the failing tests**

(a) **Repurpose `Count_after_union_falls_back`** (~line 588) — the seam-discriminating aggregate (combined count ≠ source1 count). Rename to `Count_after_union_goes_native`:

```csharp
    // EF-347 slice B, hazard-1 discriminator: Count over the COMBINED union (5) differs from source1's
    // count (3), so a mis-placed pre-$unionWith $count would return the wrong number. Union has a baseline.
    [Fact]
    public void Count_after_union_goes_native()
    {
        var collection = SeedCollection(nameof(Count_after_union_goes_native));
        using var nativeOnlyDb = Make(collection, MongoQueryMode.NativeOnly);
        using var driverDb = Make(collection, MongoQueryMode.DriverLinq);

        int Run(SingleEntityDbContext<Item> db) =>
            db.Entities.Where(i => i.Value <= 3).Union(db.Entities.Where(i => i.Value >= 3)).Count();

        var native = Run(nativeOnlyDb);
        Assert.Equal(5, native); // {1,2,3} U {3,4,5} deduped
        Assert.Equal(Run(driverDb), native);
    }
```

(b) Add an **Intersect/Except Count** (no baseline → literal expected) — also a seam discriminator:

```csharp
    [Fact]
    public void Count_after_intersect_goes_native()
    {
        var collection = SeedCollection(nameof(Count_after_intersect_goes_native));
        using var db = Make(collection, MongoQueryMode.NativeOnly);
        // {1,2,3} Intersect {3,4,5} = {3}; Count = 1 (source1 count would be 3).
        var count = db.Entities.Where(i => i.Value <= 3).Intersect(db.Entities.Where(i => i.Value >= 3)).Count();
        Assert.Equal(1, count);
    }
```

(c) Add a **predicate-bearing aggregate** after a set op (exercises the `AddPredicateConjunct`-into-trailing path):

```csharp
    [Fact]
    public void Count_with_predicate_after_union_goes_native()
    {
        var collection = SeedCollection(nameof(Count_with_predicate_after_union_goes_native));
        using var nativeOnlyDb = Make(collection, MongoQueryMode.NativeOnly);
        using var driverDb = Make(collection, MongoQueryMode.DriverLinq);

        int Run(SingleEntityDbContext<Item> db) =>
            db.Entities.Where(i => i.Value <= 3).Union(db.Entities.Where(i => i.Value >= 3)).Count(i => i.Value >= 3);

        var native = Run(nativeOnlyDb);
        Assert.Equal(3, native); // {1,2,3,4,5}, Value>=3 → {3,4,5}
        Assert.Equal(Run(driverDb), native);
    }
```

(d) Add a **reducer** after a set op:

```csharp
    [Fact]
    public void First_after_union_ordered_goes_native()
    {
        var collection = SeedCollection(nameof(First_after_union_ordered_goes_native));
        using var nativeOnlyDb = Make(collection, MongoQueryMode.NativeOnly);
        using var driverDb = Make(collection, MongoQueryMode.DriverLinq);

        int Run(SingleEntityDbContext<Item> db) =>
            db.Entities.Where(i => i.Value <= 3).Union(db.Entities.Where(i => i.Value >= 3))
                .OrderBy(i => i.Value).First().Value;

        var native = Run(nativeOnlyDb);
        Assert.Equal(1, native);
        Assert.Equal(Run(driverDb), native);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run:
```bash
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --filter "FullyQualifiedName~NativeSetOpsTests"
```
Expected: FAIL — the aggregate/reducer after a set op throws under `NativeOnly` (the `NativeCardinalityBinder` guard still rejects a set-op terminal).

- [ ] **Step 3: Relax both `NativeCardinalityBinder` guards**

In `NativeCardinalityBinder.cs`, change the guard in **`TryBindReducer`** (currently `if (select.HasTerminalOperator)`, ~line 52) and in **`TryBindAggregate`** (currently `if (select.HasTerminalOperator)`, ~line 91) to:

```csharp
        if (select.HasTerminalOperator && !select.IsSetOpTerminalOnly)
        {
            return false;
        }
```

Add to each guard's comment (both methods):
```csharp
        // EF-347 slice B: a set-op-only terminal is EXEMPT — an aggregate/reducer composed after a set op
        // binds here and goes native. The reducer $limit / aggregate-injected predicate record into
        // TrailingOps (ActiveOps flips once SetOperation is attached), landing AFTER the set-op stage, and the
        // lowerer emits the $count/$group/$limit after the set-op stage (it keys off SetOperation, not Route,
        // and no longer early-returns). A GroupBy/Distinct/SelectMany terminal still falls back.
```

Note for the implementer: setting `Cardinality` flips `Route` to `ScalarAggregate` while `SetOperation` stays attached — this is expected. The lowerer (Task 2) emits the set-op stage from `SetOperation` regardless of `Route`, then the cardinality stage; the gate's scalar-result path (`ExecuteAggregate`) reads `Route`, not the presence/absence of a set op. If any test surfaces a gate assumption that a `ScalarAggregate` route has no set op, fix it there and note it in the commit — the design flagged this as the one gate-interaction to verify.

- [ ] **Step 4: Run tests to verify they pass**

Run:
```bash
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --filter "FullyQualifiedName~NativeSetOpsTests"
```
Expected: PASS — all new aggregate/reducer tests plus every prior test.

- [ ] **Step 5: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeCardinalityBinder.cs \
        tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeSetOpsTests.cs
git commit -m "EF-347 slice B task 4: aggregates + reducers after a set op go native"
```

---

### Task 5: Lock the deferred seams + spec re-baseline + docs

**Files:**
- Test: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeSetOpsTests.cs`
- Modify (spec overrides, as needed): `tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/NorthwindSetOperationsQueryMongoTest.cs`, `tests/.../Query/NorthwindBulkUpdatesMongoTest.cs`
- Modify: `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md`

**Interfaces:**
- Consumes: everything from Tasks 1–4.
- Produces: regression coverage that deferred operators still fall back (Union) / hard-fail (Intersect/Except) after a set op, and up-to-date docs.

- [ ] **Step 1: Add deferred-seam regression tests**

In `NativeSetOpsTests.cs`, confirm/keep the existing still-deferred tests (they must remain, unchanged in intent):
- `Chained_union_falls_back` (chained set op — deferred).
- `GroupBy_after_union_falls_back` (GroupBy — deferred).
- `OfType_after_union_falls_back` (OfType — deferred).
- `Intersect_then_op_hard_fails_under_native` (now GroupBy-only, per Task 3).

Add two explicit deferred-seam tests for the split fallback contract:

```csharp
    // EF-347 slice B: a trailing Select (projection) is DEFERRED to slice C. Union falls back gracefully;
    // Intersect hard-fails (no baseline). One test per fallback family.
    [Fact]
    public void Select_after_union_falls_back_gracefully()
    {
        var collection = SeedCollection(nameof(Select_after_union_falls_back_gracefully));
        using (var nativeOnlyDb = Make(collection, MongoQueryMode.NativeOnly))
        {
            Assert.Throws<NativeTranslationNotSupportedException>(() =>
                nativeOnlyDb.Entities.Where(i => i.Value <= 3).Union(nativeOnlyDb.Entities.Where(i => i.Value >= 3))
                    .Select(i => new { i.Name }).ToList());
        }
        using var nativeDb = Make(collection, MongoQueryMode.Native);
        var result = nativeDb.Entities.Where(i => i.Value <= 3).Union(nativeDb.Entities.Where(i => i.Value >= 3))
            .Select(i => new { i.Name }).ToList();
        Assert.Equal(5, result.Count);
    }

    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.DriverLinq)]
    [InlineData(MongoQueryMode.NativeOnly)]
    public void Select_after_intersect_hard_fails_in_every_mode(MongoQueryMode mode)
    {
        var collection = SeedCollection(nameof(Select_after_intersect_hard_fails_in_every_mode) + mode);
        using var db = Make(collection, mode);
        Assert.ThrowsAny<Exception>(() =>
            db.Entities.Where(i => i.Value <= 3).Intersect(db.Entities.Where(i => i.Value >= 3))
                .Select(i => new { i.Name }).ToList());
    }
```

Run:
```bash
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --filter "FullyQualifiedName~NativeSetOpsTests"
```
Expected: PASS (all set-op tests — flipped, new, and still-deferred).

- [ ] **Step 2: Re-baseline the Northwind set-op spec suite**

Some Northwind set-operation spec tests compose an operator after `Union`/`Concat`/`Intersect`/`Except` and were marked `AssertTranslationFailed` (or carry an out-of-date `AssertMql`) because the composition used to fall back. Find them and re-baseline:

```bash
# See which set-op spec tests exist and how they currently assert:
grep -n "Union\|Concat\|Intersect\|Except\|AssertTranslationFailed\|AssertMql" \
  tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/NorthwindSetOperationsQueryMongoTest.cs
```

For each now-native composition (a set op followed by an in-scope trailing operator), change `AssertTranslationFailed(...)` to an `AssertMql(...)` and regenerate the baseline:

```bash
EF_TEST_REWRITE_BASELINES=1 dotnet test \
  tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --filter "FullyQualifiedName~NorthwindSetOperationsQueryMongoTest"
```
Then rebuild and re-run **without** the env var to confirm green:
```bash
dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --filter "FullyQualifiedName~NorthwindSetOperationsQueryMongoTest"
```
`git diff` the spec file — confirm each changed override flipped from a fallback assertion to a real MQL baseline, and that no still-deferred shape (post-set-op projection, chained set op, GroupBy-after-set-op) was wrongly flipped. Leave genuinely-out-of-scope shapes as `AssertTranslationFailed`. Repeat the same check for `NorthwindBulkUpdatesMongoTest.cs` if it contains set-op composition tests.

Expected: PASS with the regenerated baselines.

- [ ] **Step 3: Update the Query area AGENTS.md**

In `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md`, update the two set-ops as-built notes (the Union/Concat slice-2 note and the Intersect/Except slice-A note):
- State that `Where`/`OrderBy`/`ThenBy`/`Skip`/`Take`, scalar aggregates, and `First`/`Single` reducers composed **after** a set op now go **native** (no longer terminal-only for these operators), via the `TrailingOps`/`ActiveOps` mechanism on `MongoSelectDefinition` and the lowerer emitting trailing stages after the set-op stage then falling through to the cardinality stage.
- State the relaxation surface precisely: `IsSetOpTerminalOnly` relaxes only the two catch-all guard sites (`NativeSlotPopulator` top guard, `NativeCardinalityBinder.TryBindReducer`/`TryBindAggregate`); the deferred own-override operators (`Select`/`Distinct`/`GroupBy`/`SelectMany`/`OfType`) and chained set ops keep their own untouched `HasTerminalOperator` guards and stay terminal.
- Update the "Deferred / falls back" lists: a **trailing projection `Select`** (slice C), a **trailing `Distinct`**, a **chained** set op, and **GroupBy/OfType/SelectMany after a set op** remain deferred — Union/Concat fall back gracefully, Intersect/Except hard-fail in every mode (unchanged split).
- Add `MongoSelectDefinition.TrailingOps` / `ActiveOps` / `IsSetOpTerminalOnly` to the key-entry-points / IR description where `PipelineOps` is documented.

- [ ] **Step 4: Commit**

```bash
git add tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeSetOpsTests.cs \
        tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/NorthwindSetOperationsQueryMongoTest.cs \
        src/MongoDB.EntityFrameworkCore/Query/AGENTS.md
# add NorthwindBulkUpdatesMongoTest.cs too if it changed
git commit -m "EF-347 slice B task 5: lock deferred seams, re-baseline spec suite, update AGENTS.md"
```

---

### Task 6: Full 3-version verification + squash + push (stacked-PR workflow)

**Files:** none (verification + git).

- [ ] **Step 1: Full 3-version build + test**

Invoke the `/test-all` skill (builds + tests EF8/EF9/EF10 in parallel, foreground, summing all three assembly summaries). Confirm **0 failures** across all three versions. Do not proceed on any failure — investigate and fix in the owning task.

- [ ] **Step 2: NativeOnly spec sweep (net native-coverage increase, zero regressions)**

```bash
MONGODB_EF_NATIVE_ONLY=1 dotnet test \
  tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --filter "FullyQualifiedName~NorthwindSetOperations"
```
Expected: the now-native post-composition set-op tests pass under `NativeOnly`; no previously-passing test regresses. (Compare against the pre-slice pass set — new passes are the coverage gain; any new failure is a regression to fix.)

- [ ] **Step 3: Whole-branch review**

Request a review of the whole slice (per the native-stack workflow — an opus-level whole-branch review, or the `query-reviewer` / `/review-ef-core-provider` fan-out). Address findings in the owning task, then re-run `/test-all`.

- [ ] **Step 4: Squash to one commit + backup, then push**

Per the stacked-PR workflow (see the `native-stack-status` / `stacked-pr-workflow` memories):
```bash
# keep a pre-squash safety branch
git branch EF-347-setops-post-composition-presquash
# squash this slice's commits into one, on top of the rolling tip 84e67c7
git reset --soft 84e67c7
git commit -m "EF-347: Native set-op post-composition — Where/OrderBy/paging + aggregates/reducers (all four set ops)"
```
Write a full commit body in the style of `84e67c7` (mechanism, the `TrailingOps`/`ActiveOps` flip, the two relaxed guard sites, the fallback split, scope = slice B, deferred = trailing projection/Distinct/chained → slice C). Then fast-forward-push the rolling tip and update the `native-stack-status` memory with the new tip. Confirm with the user before pushing.

---

## Self-Review (completed during planning)

**Spec coverage:** IR (`TrailingOps`/`ActiveOps`/`IsSetOpTerminalOnly`) → Task 1; lowerer trailing-emit + fall-through → Task 2; `NativeSlotPopulator` relaxation → Task 3; `NativeCardinalityBinder` relaxation → Task 4; fallback split, seam-discriminating tests (Count/paging), parametrized trailing predicate, deferred-seam regression, spec re-baseline, AGENTS.md → Tasks 3/4/5; gate/`ScalarAggregate` interaction → verified in Task 4 Step 3; full 3-version + NativeOnly sweep + squash/push → Task 6. All spec sections map to a task.

**Type consistency:** `TrailingOps` (property), `ActiveOps` (private), `IsSetOpTerminalOnly` (internal), `AppendSelectOpStages(IReadOnlyList<MongoSelectOp>, List<MongoPipelineStage>)` (refactored signature) are used consistently across Tasks 1–4. `MongoCardinality.ForAggregate(op, selector, behavior, emptyValue, type, presenceOnly, presentValue)` matches the signature in `MongoCardinalityRouteTests`.

**Placeholder scan:** no TBD/TODO; every code and test step shows complete code and exact commands.
