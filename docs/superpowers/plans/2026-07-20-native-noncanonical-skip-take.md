# Native non-canonical Skip/Take Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the native query translator handle non-canonical `Skip`/`Take` (operator-after-paging, Take-before-Skip, repeated paging) plus predicate-injecting aggregates after paging, by replacing `MongoSelectDefinition`'s fixed single-slot filter/sort/page model with an ordered stage list emitted verbatim.

**Architecture:** `MongoSelectDefinition` currently holds single `Predicate`/`Orderings`/`Offset`/`Limit` slots that the lowerer emits in one fixed order (`$match→$sort→$skip→$limit`), and `NativeSlotPopulator` rejects any non-canonical arrangement to driver-LINQ. We replace those slots with one ordered `List<MongoSelectOp>` (`Match`/`Sort`/`Skip`/`Limit`), append to it in arrival order with merge rules that reproduce today's MQL for canonical queries, and emit it verbatim. MongoDB's pipeline is inherently sequential, so no sub-pipeline is needed. This is a coverage/perf change, not a correctness fix: the driver-LINQ fallback already returns correct results for every targeted shape and is the verification oracle.

**Tech Stack:** C# / .NET, EF Core provider internals, MongoDB C# driver, xUnit (plain `Assert.*`, no FluentAssertions).

**Spec:** `docs/superpowers/specs/2026-07-20-native-noncanonical-skip-take-design.md`

## Global Constraints

- **Branch:** `EF-347-noncanonical-paging`, stacked off native tip `4e30ad2`. One squashed commit at the end (backup branch first). Do not push — the user drives the fast-forward push to `origin/NativeQueryOngoing`.
- **Multi-EF:** Builds under `Debug EF8` / `Debug EF9` / `Debug EF10`. This change touches no EF-version-specific API, so **no `#if EF8/EF9/EF10` guards are expected** — if you reach for one, stop and reconsider.
- **Nullable:** `src/` is `<Nullable>enable</Nullable>` — annotate accordingly.
- **Visibility:** All new types/members are `internal` (the native translator is internal surface).
- **Preserve file BOMs** on every edited file.
- **Tests are serial** (`DisableTestParallelization`); use plain xUnit `Assert.*`.
- **MQL is not a contract** (`AGENTS.md` versioning rubric + [[native-default-not-a-break]]): faithful emission will reorder some currently-native queries' emitted MQL. That is expected and non-breaking. The correctness bar is **results parity**, verified against the driver-LINQ oracle and by a `MONGODB_EF_NATIVE_ONLY` spec sweep whose native pass-set only **grows**.
- **Verify via full 3-version `/test-all`** (the `test-all` skill) before declaring any task with a behavioral gate done — touched-class-only runs have repeatedly missed EF8/EF9-only breaks and spec-baseline deltas on this stack.

## File Structure

- **Create** `src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoSelectOp.cs` — the dialect-neutral ordered-op IR (`MongoSelectOp` + `MongoMatchOp`/`MongoSortOp`/`MongoSkipOp`/`MongoLimitOp`). Lives beside the other logical IR (`MongoOrdering`, `MongoGrouping`, `MongoCardinality`), mirroring the established "logical IR in `Expressions/`, lowered stages in `NativeTranslation/Stages/`" split.
- **Modify** `src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoSelectDefinition.cs` — replace the four single slots with the ordered list + merge methods + computed accessors.
- **Modify** `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoSelectLowerer.cs` — rewrite `AppendCanonicalStages` (rename → `AppendSelectOpStages`) to walk the list.
- **Modify** `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeSlotPopulator.cs` — new merge-method call sites; (Task 2) remove canonical-order guards.
- **Modify** `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeCardinalityBinder.cs` — new API for the reducer limit; (Task 3) remove the paging guard.
- **Modify** `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeGroupByBinder.cs` — `Orderings.Count > 0` → `HasOrdering`.
- **Modify** tests: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/{SlotPopulationTests,MongoSelectLowererTests}.cs`; `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/QueryModeGateTests.cs`.
- **Modify** `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md` — as-built note (Task 4).

---

## Task 1: Ordered-op IR swap (faithful emission, results-preserving)

Replace the single slots with the ordered list and wire every consumer to it, **keeping the canonical-order guards in place** so no new shapes are enabled yet. This isolates the foundational IR change from the capability change. The only observable effect is that some currently-native queries emit reordered (result-identical) MQL.

**Files:**
- Create: `src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoSelectOp.cs`
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoSelectDefinition.cs`
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoSelectLowerer.cs:152-177` (and the call site at `:76`)
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeSlotPopulator.cs:100-149`
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeCardinalityBinder.cs:57-61`
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeGroupByBinder.cs:54`
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoSelectDefinitionTests.cs` (create), `SlotPopulationTests.cs`, `MongoSelectLowererTests.cs`

**Interfaces:**
- Produces (new `MongoSelectDefinition` API, consumed by Tasks 2-3 and the lowerer):
  - `IReadOnlyList<MongoSelectOp> PipelineOps { get; }`
  - `void AddPredicateConjunct(MongoExpression conjunct)` — extend the tail op if it is a `MongoMatchOp` (AND-combine via `MongoBinaryOperator.AndAlso`), else append a new `MongoMatchOp`.
  - `void StartOrReplaceSort(MongoOrdering first)` — if the tail op is a `MongoSortOp`, replace it with `new MongoSortOp([first])`; else append.
  - `void AppendThenBy(MongoOrdering next)` — tail op is a `MongoSortOp` (LINQ typing guarantees it); replace it with its orderings plus `next`.
  - `void AppendSkip(MongoExpression count)` / `void AppendLimit(MongoExpression count)` — append the op.
  - `bool HasPaging { get; }` — any `MongoSkipOp` or `MongoLimitOp`. `bool HasOrdering { get; }` — any `MongoSortOp`. `bool HasLimit { get; }` — any `MongoLimitOp`.
  - Removed: `Predicate`, `Offset`, `Limit`, `Orderings` properties; `ResetOrderings`, `AppendOrdering` methods.
- Consumes: existing `MongoExpression`, `MongoBinaryExpression`, `MongoBinaryOperator.AndAlso`, `MongoOrdering`, and the stage types `MongoMatchStage(MongoExpression)`, `MongoSortStage(IReadOnlyList<MongoOrdering>)`, `MongoSkipStage(MongoExpression)`, `MongoLimitStage(MongoExpression)`.

- [ ] **Step 1: Create the op IR.** Create `MongoSelectOp.cs` (copy the license header + BOM from a sibling file like `MongoOrdering.cs`):

```csharp
using System.Collections.Generic;

namespace MongoDB.EntityFrameworkCore.Query.Expressions;

/// <summary>
/// One filter/sort/page operation in a <see cref="MongoSelectDefinition"/>'s ordered pipeline. The list of
/// these is emitted verbatim by the lowerer, so their order IS the emitted stage order — this is what lets
/// the native path represent non-canonical Skip/Take (operator-after-paging, Take-before-Skip, repeated
/// paging). Dialect-neutral logical IR (holds <see cref="MongoExpression"/>, never BSON), like
/// <see cref="MongoOrdering"/> / <see cref="MongoGrouping"/>.
/// </summary>
internal abstract record MongoSelectOp;

/// <summary>A <c>$match</c> predicate.</summary>
internal sealed record MongoMatchOp(MongoExpression Predicate) : MongoSelectOp;

/// <summary>A <c>$sort</c> over one or more orderings.</summary>
internal sealed record MongoSortOp(IReadOnlyList<MongoOrdering> Orderings) : MongoSelectOp;

/// <summary>A <c>$skip</c> offset.</summary>
internal sealed record MongoSkipOp(MongoExpression Count) : MongoSelectOp;

/// <summary>A <c>$limit</c> cap.</summary>
internal sealed record MongoLimitOp(MongoExpression Count) : MongoSelectOp;
```

- [ ] **Step 2: Write failing unit tests for the merge rules.** Create `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoSelectDefinitionTests.cs` (license header + BOM; `namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;` per sibling test files). Use a tiny helper to build leaf expressions:

```csharp
using System.Collections.Generic;
using MongoDB.EntityFrameworkCore.Query.Expressions;
using Xunit;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

public class MongoSelectDefinitionTests
{
    private static MongoConstantExpression Const(int v) => new(v, forSerialization: null);
    private static MongoOrdering Asc() => new(Const(1), ascending: true);

    [Fact]
    public void Consecutive_predicates_merge_into_one_match_op()
    {
        var s = new MongoSelectDefinition();
        s.AddPredicateConjunct(Const(1));
        s.AddPredicateConjunct(Const(2));

        var op = Assert.IsType<MongoMatchOp>(Assert.Single(s.PipelineOps));
        Assert.IsType<MongoBinaryExpression>(op.Predicate); // AndAlso of the two conjuncts
    }

    [Fact]
    public void Predicate_after_sort_appends_a_separate_match_op()
    {
        var s = new MongoSelectDefinition();
        s.StartOrReplaceSort(Asc());
        s.AddPredicateConjunct(Const(1));

        Assert.Collection(s.PipelineOps,
            o => Assert.IsType<MongoSortOp>(o),
            o => Assert.IsType<MongoMatchOp>(o));
    }

    [Fact]
    public void Consecutive_order_by_replaces_the_sort_op()
    {
        var s = new MongoSelectDefinition();
        s.StartOrReplaceSort(Asc());
        s.StartOrReplaceSort(new MongoOrdering(Const(2), ascending: false));

        var op = Assert.IsType<MongoSortOp>(Assert.Single(s.PipelineOps));
        Assert.False(Assert.Single(op.Orderings).Ascending);
    }

    [Fact]
    public void Then_by_extends_the_current_sort_op()
    {
        var s = new MongoSelectDefinition();
        s.StartOrReplaceSort(Asc());
        s.AppendThenBy(new MongoOrdering(Const(2), ascending: false));

        var op = Assert.IsType<MongoSortOp>(Assert.Single(s.PipelineOps));
        Assert.Equal(2, op.Orderings.Count);
    }

    [Fact]
    public void Take_before_skip_records_both_ops_in_arrival_order()
    {
        var s = new MongoSelectDefinition();
        s.AppendLimit(Const(10));
        s.AppendSkip(Const(5));

        Assert.Collection(s.PipelineOps,
            o => Assert.IsType<MongoLimitOp>(o),
            o => Assert.IsType<MongoSkipOp>(o));
        Assert.True(s.HasPaging);
        Assert.True(s.HasLimit);
    }
}
```

- [ ] **Step 3: Run to verify failure.** Expected: compile errors (`AddPredicateConjunct` old signature still on old slots; `PipelineOps`/`StartOrReplaceSort`/etc. undefined).

Run: `dotnet build tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj -c "Debug EF10"`
Expected: FAIL (missing members).

- [ ] **Step 4: Rewrite `MongoSelectDefinition` slots → ordered list.** In `MongoSelectDefinition.cs`, delete the `Predicate` property (49), `AddPredicateConjunct` (57-60), the `Orderings`/`ResetOrderings`/`AppendOrdering` block (64-82), and the `Limit`/`Offset`/`HasPaging` block (86-100). Replace with:

```csharp
    // ── Ordered filter/sort/page pipeline ─────────────────────────────────────────
    private readonly List<MongoSelectOp> _pipelineOps = [];

    /// <summary>
    /// The ordered filter/sort/page operations, emitted verbatim by the lowerer. Arrival order IS emission
    /// order — this is what represents non-canonical Skip/Take. Terminal shapes (projection/grouping/
    /// cardinality/set-op/unwind) still follow this block; see <see cref="Route"/> and the lowerer.
    /// </summary>
    public IReadOnlyList<MongoSelectOp> PipelineOps => _pipelineOps;

    /// <summary>
    /// ANDs <paramref name="conjunct"/> into the tail <see cref="MongoMatchOp"/> if the last op is one
    /// (so consecutive Where's merge into a single $match); otherwise appends a new <see cref="MongoMatchOp"/>
    /// at the current tail (so a Where/OfType/aggregate-predicate applied AFTER a sort or paging lands as a
    /// later $match — the sequential semantics MongoDB's pipeline gives us).
    /// </summary>
    public void AddPredicateConjunct(MongoExpression conjunct)
    {
        if (_pipelineOps.Count > 0 && _pipelineOps[^1] is MongoMatchOp match)
            _pipelineOps[^1] = new MongoMatchOp(
                new MongoBinaryExpression(MongoBinaryOperator.AndAlso, match.Predicate, conjunct));
        else
            _pipelineOps.Add(new MongoMatchOp(conjunct));
    }

    /// <summary>
    /// OrderBy: if the tail op is a <see cref="MongoSortOp"/>, REPLACE it (a fresh primary sort, reproducing
    /// the previous ResetOrderings semantics, so <c>OrderBy(a).OrderBy(b)</c> keeps only b); otherwise append
    /// a new sort (e.g. an OrderBy after paging).
    /// </summary>
    public void StartOrReplaceSort(MongoOrdering first)
    {
        if (_pipelineOps.Count > 0 && _pipelineOps[^1] is MongoSortOp)
            _pipelineOps[^1] = new MongoSortOp([first]);
        else
            _pipelineOps.Add(new MongoSortOp([first]));
    }

    /// <summary>ThenBy: extends the current (tail) sort. LINQ typing guarantees an OrderBy/ThenBy immediately
    /// precedes a ThenBy, so the tail op is a <see cref="MongoSortOp"/>.</summary>
    public void AppendThenBy(MongoOrdering next)
    {
        var sort = (MongoSortOp)_pipelineOps[^1];
        _pipelineOps[^1] = new MongoSortOp([.. sort.Orderings, next]);
    }

    /// <summary>Skip → append a <see cref="MongoSkipOp"/>.</summary>
    public void AppendSkip(MongoExpression count) => _pipelineOps.Add(new MongoSkipOp(count));

    /// <summary>Take (and the synthesized reducer limit) → append a <see cref="MongoLimitOp"/>.</summary>
    public void AppendLimit(MongoExpression count) => _pipelineOps.Add(new MongoLimitOp(count));

    /// <summary><see langword="true"/> when any $skip or $limit op is present.</summary>
    internal bool HasPaging => _pipelineOps.Exists(o => o is MongoSkipOp or MongoLimitOp);

    /// <summary><see langword="true"/> when any $sort op is present.</summary>
    internal bool HasOrdering => _pipelineOps.Exists(o => o is MongoSortOp);

    /// <summary><see langword="true"/> when any $limit op is present.</summary>
    internal bool HasLimit => _pipelineOps.Exists(o => o is MongoLimitOp);
```

(Keep `using System.Collections.Generic;` and `using System.Diagnostics;` — `System.Linq` is not needed since `List.Exists` is used instead of `Any`.)

- [ ] **Step 5: Rewrite the lowerer to walk the list.** In `MongoSelectLowerer.cs`, replace `AppendCanonicalStages` (152-177) with:

```csharp
    /// <summary>
    /// Appends the ordered filter/sort/page stages ($match / $sort / $skip / $limit) for
    /// <paramref name="select"/> in their recorded order. Shared between the outer query and a set-operation
    /// operand (<see cref="MongoSetOperation.OperandSelect"/>), which is a plain whole-entity select.
    /// </summary>
    private static void AppendSelectOpStages(MongoSelectDefinition select, List<MongoPipelineStage> stages)
    {
        foreach (var op in select.PipelineOps)
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

Rename the two call sites: line 61 `AppendCanonicalStages(select, stages);` → `AppendSelectOpStages(select, stages);` and line 76 `AppendCanonicalStages(setOp.OperandSelect, operandStages);` → `AppendSelectOpStages(setOp.OperandSelect, operandStages);`.

- [ ] **Step 6: Update `NativeSlotPopulator` call sites** (no guard removal yet). In `NativeSlotPopulator.cs`:
  - OrderBy arm (103): `mongoQ.Select.ResetOrderings(new MongoOrdering(keyNode, ascending));` → `mongoQ.Select.StartOrReplaceSort(new MongoOrdering(keyNode, ascending));`
  - ThenBy arm (119): `mongoQ.Select.AppendOrdering(new MongoOrdering(keyNode, ascending));` → `mongoQ.Select.AppendThenBy(new MongoOrdering(keyNode, ascending));`
  - Skip arm (126-135): keep the guard `if (mongoQ.Select.Offset != null || mongoQ.Select.Limit != null)` but rewrite as `if (mongoQ.Select.HasPaging)` and the body `mongoQ.Select.Offset = TranslateCountExpression(...)` → translate into a local, null-check, then `mongoQ.Select.AppendSkip(count)`:

```csharp
        else if (methodDefinition == QueryableMethods.Skip)
        {
            // Enforce canonical order: Skip once, before Take. (Relaxed in Task 2.)
            if (mongoQ.Select.HasPaging)
            {
                mongoQ.Select.MarkNotNativelyRepresentable();
            }
            else
            {
                var count = TranslateCountExpression(call.Arguments[1]);
                if (count is null)
                    mongoQ.Select.MarkNotNativelyRepresentable();
                else
                    mongoQ.Select.AppendSkip(count);
            }
        }
```

  - Take arm (137-149): same shape, guard `if (mongoQ.Select.HasLimit)`, body `AppendLimit(count)`:

```csharp
        else if (methodDefinition == QueryableMethods.Take)
        {
            if (mongoQ.Select.HasLimit)
            {
                mongoQ.Select.MarkNotNativelyRepresentable();
            }
            else
            {
                var count = TranslateCountExpression(call.Arguments[1]);
                if (count is null)
                    mongoQ.Select.MarkNotNativelyRepresentable();
                else
                    mongoQ.Select.AppendLimit(count);
            }
        }
```

  (The Where arm's `AddPredicateConjunct` at 85 is unchanged. `PagingAlreadyApplied` at 176-177 still reads `HasPaging` — unchanged.)

- [ ] **Step 7: Update `NativeCardinalityBinder`** (reducer limit only; the paging guard is removed in Task 3). In `NativeCardinalityBinder.cs`:
  - Line 57: `if (select.Limit != null)` → `if (select.HasLimit)`
  - Line 61: `select.Limit = new MongoConstantExpression(limit, forSerialization: null);` → `select.AppendLimit(new MongoConstantExpression(limit, forSerialization: null));`
  - Leave lines 113-114 (`injectsPredicate && select.HasPaging`) and the `AddPredicateConjunct` calls (127, 136) as-is.

- [ ] **Step 8: Update `NativeGroupByBinder`.** Line 54: `if (select.HasPaging || select.Orderings.Count > 0)` → `if (select.HasPaging || select.HasOrdering)`. (Line 348's `select.HasPaging` is unchanged.)

- [ ] **Step 9: Update the two existing unit-test files to the new API.**
  - `SlotPopulationTests.cs:135`: `Assert.NotNull(mongoQ.Select.Predicate);` → `Assert.IsType<MongoMatchOp>(Assert.Single(mongoQ.Select.PipelineOps));`
  - `SlotPopulationTests.cs:148-150` (two orderings from `OrderBy().ThenByDescending()`): replace with reading the single `MongoSortOp`:

```csharp
        var sort = Assert.IsType<MongoSortOp>(Assert.Single(mongoQ.Select.PipelineOps));
        Assert.Equal(2, sort.Orderings.Count);
        Assert.True(sort.Orderings[0].Ascending);
        Assert.False(sort.Orderings[1].Ascending);
```

  - `MongoSelectLowererTests.cs:93-94`: `select.Select.Offset = new MongoConstantExpression(5, null);` / `select.Select.Limit = new MongoConstantExpression(10, null);` → `select.Select.AppendSkip(new MongoConstantExpression(5, null));` / `select.Select.AppendLimit(new MongoConstantExpression(10, null));`. Confirm this test builds its predicate/sort via `AddPredicateConjunct`/`StartOrReplaceSort` (update any direct `.Predicate =`/`.Orderings` writes the same way, appending in the canonical order the test asserts: match, sort, skip, limit).
  - `:157`: `select.Select.Offset = offset;` → `select.Select.AppendSkip(offset);`
  - `:173`: `select.Select.Limit = limit;` → `select.Select.AppendLimit(limit);`
  - Any other `.Predicate =` / `.Orderings` / `.Offset =` / `.Limit =` writes in these two files: convert to the append/merge methods (search both files for `.Offset`, `.Limit`, `.Predicate`, `.Orderings`, `ResetOrderings`, `AppendOrdering`).

- [ ] **Step 10: Build + run the unit suite (all three EF versions).**

Run: `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"` then `dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj -c "Debug EF10" --no-build`
Expected: PASS (new `MongoSelectDefinitionTests` + updated slot/lowerer tests). Then repeat the build for `-c "Debug EF8"` and `-c "Debug EF9"` to confirm no version-specific breakage (no `#if` should be needed).

- [ ] **Step 11: Full 3-version `/test-all` + results-parity check.** Invoke the `test-all` skill. This is the critical refactor gate. Expected: **zero test failures** on all three EF versions, EXCEPT `AssertMql` baseline mismatches for currently-native queries whose operator order differs from canonical (e.g. `.OrderBy(k).Where(p)` now emits `$sort,$match` instead of `$match,$sort`). Those are **result-identical MQL reorderings** — confirm each failing spec's *data* assertion still passes (the failure is only the MQL string) before re-baselining.

- [ ] **Step 12: Re-baseline the churned specs (data-gated).** For each spec whose only failure is the reordered MQL, re-baseline with a tight filter:

```bash
EF_TEST_REWRITE_BASELINES=1 dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj -c "Debug EF10" --no-build --filter "FullyQualifiedName~<Class>.<Method>"
```

`git diff` the rewritten baselines: every change must be a pure stage REORDER (same stages, different order) or an equivalent `$match`-split — never a changed predicate/value or a dropped/added stage. Revert and investigate anything else. Then rebuild + re-run without the env var to confirm green. **A `MONGODB_EF_NATIVE_ONLY=1` spec sweep must show the SAME native pass-set as before this task** (no shape newly passes or fails — Task 1 only reorders MQL).

- [ ] **Step 13: Commit.**

```bash
git add -A && git commit -m "EF-347: Replace filter/sort/page slots with an ordered select-op list (behavior-preserving)"
```

---

## Task 2: Enable non-canonical slot operators

Remove the canonical-order guards so `Where`/`OrderBy`/`ThenBy` after paging, `Take` before `Skip`, and repeated paging go native. Correctness is by MongoDB's sequential pipeline semantics; verify against the driver-LINQ oracle.

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeSlotPopulator.cs` (remove guards)
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/SlotPopulationTests.cs`, `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/QueryModeGateTests.cs`

**Interfaces:**
- Consumes: the Task 1 `MongoSelectDefinition` API (`PipelineOps`, `AppendSkip`/`AppendLimit`, `AddPredicateConjunct`, `StartOrReplaceSort`).

- [ ] **Step 1: Write failing unit tests** asserting the populator records the correct ordered `PipelineOps` for the three families. Add to `SlotPopulationTests.cs` (follow the existing arrange pattern in that file for building a `ShapedQueryExpression` and calling `NativeSlotPopulator.PopulateNativeSlots`; mirror an existing multi-operator test). Assert, for `.Take(10).Skip(5)`:

```csharp
        Assert.Collection(mongoQ.Select.PipelineOps,
            o => Assert.IsType<MongoLimitOp>(o),
            o => Assert.IsType<MongoSkipOp>(o));
        Assert.False(mongoQ.Select.Route == NativeRoute.Fallback);
```

  and for `.Skip(1).Where(p)`: a `MongoSkipOp` then a `MongoMatchOp`, `Route != Fallback`; and for `.Skip(2).Take(3).Skip(1)`: `MongoSkipOp, MongoLimitOp, MongoSkipOp`, `Route != Fallback`.

- [ ] **Step 2: Run to verify failure.** Run: `dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj -c "Debug EF10" --filter "FullyQualifiedName~SlotPopulationTests"`. Expected: FAIL — these currently hit `MarkNotNativelyRepresentable()` so `Route == Fallback`.

- [ ] **Step 3: Remove the canonical-order guards** in `NativeSlotPopulator.cs`:
  - Where arm (77-81): delete the `if (PagingAlreadyApplied(mongoQ)) { MarkNotNativelyRepresentable(); return; }` block.
  - OrderBy arm (94-98) and ThenBy arm (110-114): delete the same block.
  - Skip arm: delete the `if (mongoQ.Select.HasPaging) { MarkNotNativelyRepresentable(); } else { ... }` wrapper, keeping only the translate-and-`AppendSkip` body:

```csharp
        else if (methodDefinition == QueryableMethods.Skip)
        {
            var count = TranslateCountExpression(call.Arguments[1]);
            if (count is null)
                mongoQ.Select.MarkNotNativelyRepresentable();
            else
                mongoQ.Select.AppendSkip(count);
        }
```

  - Take arm: same — delete the `if (mongoQ.Select.HasLimit)` wrapper, keep translate-and-`AppendLimit`.
  - Delete the now-unused `PagingAlreadyApplied` method (176-177) and update the comment block above it.
  - **Leave the post-terminal `HasTerminalOperator` / `IsPostGroupSlotOperator` guard (65-69) untouched** — a slot operator after a GroupBy/Distinct/set-op/SelectMany still falls back.

- [ ] **Step 4: Run unit tests to verify pass.** Run: `dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj -c "Debug EF10" --no-build --filter "FullyQualifiedName~SlotPopulationTests"`. Expected: PASS.

- [ ] **Step 5: Flip the existing gate tests** in `QueryModeGateTests.cs` — these shapes now go native. Update the block at 206-274:
  - The three `Native_*_returns_correct_rows_via_fallback` tests still assert the same result data (keep the `Assert.Equal([...])`), but the queries now execute natively. Rename each dropping `_via_fallback` (e.g. `Native_where_after_skip_returns_correct_rows`), and update the leading comment (the "must fall back" rationale no longer holds — it now goes native and is correct by sequential emission).
  - The `NativeOnly_where_after_skip_throws` / `NativeOnly_order_after_skip_throws` (and the analogous `_after_take`) tests must **flip**: rename to `NativeOnly_*_succeeds` and replace `Assert.Throws<NativeTranslationNotSupportedException>(() => query.ToList());` with executing the query and asserting the correct rows (copy the expected sequence from the paired `Native_*` test). This is the primary "goes native" signal.

- [ ] **Step 6: Add new native functional tests** to `QueryModeGateTests.cs` for the families not already covered: `Take` before `Skip` (`.OrderBy(c => c.Score).Take(3).Skip(1)`), and repeated paging (`.OrderBy(c => c.Score).Skip(1).Take(2).Skip(1)`). For each, run under `MongoQueryMode.NativeOnly`, assert it does **not** throw, and assert the exact expected rows (derive them from the seed in `SeedCustomers`). Follow the arrange pattern of the existing tests in this file.

- [ ] **Step 7: Full 3-version `/test-all` + NativeOnly sweep.** Invoke `test-all`. Expected: zero failures on all three EF versions. Run a `MONGODB_EF_NATIVE_ONLY=1` spec sweep and confirm the native pass-set has **grown** (Northwind paging/OrderBy specs that previously fell back now pass) with **no regressions** (nothing that passed now fails). Re-baseline only if a newly-native spec asserts MQL and the native pipeline differs from the prior fallback MQL — for filter/sort/paging they are structurally identical, so little or no churn is expected; scrutinize any diff.

- [ ] **Step 8: Commit.**

```bash
git add -A && git commit -m "EF-347: Native non-canonical Skip/Take (operator-after-paging, Take-before-Skip, repeated paging)"
```

---

## Task 3: Relax cardinality-after-paging

Let a predicate-injecting aggregate after paging (`Take(n).All/Count/Any(pred)`) go native: the injected `$match` now lands after the `$skip`/`$limit` via the ordered list.

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeCardinalityBinder.cs:105-114`
- Test: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/QueryModeGateTests.cs`

**Interfaces:**
- Consumes: Task 1's ordered list (the `AddPredicateConjunct` at 127/136 already appends to the tail, i.e. after any paging op).

- [ ] **Step 1: Write a failing functional test.** In `QueryModeGateTests.cs`, add a `NativeOnly` test for `Take(n).All(pred)`. With the `SeedCustomers` data sorted by Score (Alice 10, Bob 20, Carol 30, Dave 40), `OrderBy(Score).Take(2).All(c => c.Score < 15)` sees only {Alice, Bob} and is `false` (Bob=20). Assert it does not throw under `NativeOnly` and returns `false`. Add a second: `OrderBy(Score).Take(2).Count(c => c.Score > 15)` == 1 (only Bob among the first two).

- [ ] **Step 2: Run to verify failure.** Run: `dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj -c "Debug EF10" --filter "FullyQualifiedName~QueryModeGateTests"`. Expected: FAIL — currently throws `NativeTranslationNotSupportedException` under `NativeOnly` (the paging guard forces fallback).

- [ ] **Step 3: Remove the paging guard.** In `NativeCardinalityBinder.TryBindAggregate`, delete lines 112-114 (`var injectsPredicate = ...; if (injectsPredicate && select.HasPaging) return false;`) and rewrite the comment (105-111) to state that the injected predicate is appended to the ordered list AFTER any paging via `AddPredicateConjunct`, so `Take(n).All(pred)` correctly sees only the first `n` rows. Leave the `HasTerminalOperator` guard (91-92) untouched.

- [ ] **Step 4: Run to verify pass.** Run: `dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj -c "Debug EF10" --no-build --filter "FullyQualifiedName~QueryModeGateTests"`. Expected: PASS.

- [ ] **Step 5: Flip any existing fallback tests.** Search the test suite for existing coverage of predicate-injecting aggregates after paging (`grep -rn "Take(.*)\.All\|Skip(.*)\.All\|Take(.*)\.Count(" tests/`); if any assert fallback/throw-under-NativeOnly, flip them to assert native success with correct results, mirroring Task 2 Step 5.

- [ ] **Step 6: Full 3-version `/test-all` + NativeOnly sweep.** Invoke `test-all`. Expected: zero failures; NativeOnly native pass-set grows for these aggregate-after-paging shapes, no regressions.

- [ ] **Step 7: Commit.**

```bash
git add -A && git commit -m "EF-347: Native predicate-injecting aggregates after paging (Take(n).All/Count/Any(pred))"
```

---

## Task 4: Docs + final verification

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md`

- [ ] **Step 1: Add an as-built note to `Query/AGENTS.md`.** In the native as-built section (alongside the OfType/Distinct/Set-ops notes), add a "Non-canonical Skip/Take (EF-347)" paragraph documenting: the ordered `MongoSelectOp` list replacing single slots; that arrival order = emission order; the merge rules (consecutive Where merge; OrderBy replace-tail-sort; ThenBy extend); that this is coverage not correctness (driver-LINQ was already correct); that emitted MQL for currently-native reorder cases changed (not a break per rubric); the cardinality-after-paging relaxation; and the retained boundaries (Select/GroupBy after non-canonical paging still fall back; the `HasTerminalOperator` guard still applies). Also update the two stale references that describe the old model: the pitfall bullet naming "`Predicate`/`Orderings`/`Offset`/`Limit` slots" and the `NativeCardinalityBinder` bullet describing the `select.Offset != null || select.Limit != null` paging guard.

- [ ] **Step 2: Final full 3-version `/test-all`.** Invoke `test-all`. Expected: zero failures on EF8/EF9/EF10. Capture the pass/skip counts per assembly.

- [ ] **Step 3: Final `MONGODB_EF_NATIVE_ONLY=1` spec sweep.** Confirm the native pass-set grew across Tasks 2-3 with zero regressions versus the base `4e30ad2`.

- [ ] **Step 4: Commit.**

```bash
git add -A && git commit -m "EF-347: Document native non-canonical Skip/Take (Query AGENTS.md)"
```

---

## After all tasks: review, squash, hand off

1. Final whole-branch review (opus) — `EF-347-noncanonical-paging` vs `4e30ad2`. Focus: the IR swap is result-safe (no dropped/duplicated ops; set-op operand path intact), the removed guards left the `HasTerminalOperator` post-terminal gate untouched, and no `#if` crept in.
2. `git branch -f EF-347-noncanonical-paging-presquash <tip>` (backup), then squash to one commit above `4e30ad2`; verify byte-identical to pre-squash; final 3-version `/test-all` on the squashed tip.
3. Give the user the fast-forward push command (`git push origin <newtip>:NativeQueryOngoing`, no `--force` — parent is `4e30ad2` = remote tip). The user drives the push.

## Self-Review notes

- **Spec coverage:** §1 IR → Task 1; §2 populator → Tasks 1 (wire) + 2 (guards); §3 cardinality → Task 3; §4 lowerer → Task 1 Step 5; §5 verification/faithful-emission → Task 1 Steps 11-12 + every task's `/test-all`; §6 scope (Select/GroupBy-after-paging stay fallback) → preserved by leaving `HasTerminalOperator` and the projection/group binders untouched; blast-radius consumers (`NativeGroupByBinder`, `IsPlainWholeEntitySelect`, OfType) → Task 1 Steps 5-8 (`IsPlainWholeEntitySelect` reads only `Route`/terminals, so it needs no change — noted, not modified).
- **Type consistency:** method names (`AddPredicateConjunct`, `StartOrReplaceSort`, `AppendThenBy`, `AppendSkip`, `AppendLimit`, `PipelineOps`, `HasPaging`/`HasOrdering`/`HasLimit`) and op records (`MongoMatchOp`/`MongoSortOp`/`MongoSkipOp`/`MongoLimitOp`) are used identically across the IR type, populator, cardinality binder, lowerer, and tests.
