# Native GroupBy SP6: Post-Group Pipeline Composition Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Move `Union_simple_groupby` and `GroupBy_skip_0_take_0_aggregate` off the driver-LINQ fallback — 2
`NorthwindGroupByQueryMongoTest` methods, dropping the `NativeOnly` GroupBy-class failure count from 26
(SP5's ending count, after its own bonus fix) to 24.

**Architecture:** Re-verified both target tests against the actual EF Core base-class source
(`test/EFCore.Specification.Tests/Query/NorthwindGroupByQueryTestBase.cs` at tag `v10.0.11`) and against this
branch's own current code paths (via a read-only investigation) — both match the design doc's own bucketing
exactly, but the actual code-level gap in each case is a **single small guard relaxation** plus (for Skip/Take)
a **new pending-state carve-out mirroring an existing one** — not the larger mechanism either the design doc's
"Approach" text or first glance suggests:

1. **`Union_simple_groupby`** (`Where(...).Union(Where(...)).GroupBy(c => c.City).Select(g => new { g.Key,
   Total = g.Count() })`) — a GroupBy composed AFTER a completed `Union`. Root cause, confirmed by direct
   read: `MongoQueryableMethodTranslatingExpressionVisitor.TranslateGroupBy`'s own guard
   (`if (hadTerminalGrouping && !hasFinalizedPriorGrouping)`) has no exemption for a **set-op-only** terminal,
   unlike `TranslateSelect`'s sibling guard a few hundred lines above it (which already exempts
   `IsSetOpTerminalOnly`) — so `TryBindGroupKey` is never even attempted; the query is marked non-native
   before any GroupBy-specific binding runs. Once that guard is relaxed, a SECOND, independent problem
   surfaces: `MongoSelectLowerer`'s existing `if (select.Grouping is { } grouping && select.SetOperation ==
   null)` block (the ONLY place that emits a `$group` for a finalized `Grouping`) explicitly declines to fire
   whenever ANY `SetOperation` is present — including this shape, where the `SetOperation`'s own
   `OperandsProjected` branch never emitted `Grouping` earlier (that branch only fires when `OperandsProjected`
   is true, which it is not for two plain whole-entity `Union` operands). The method has **no early return**
   between the `SetOperation` block and this one, so — once the guard's condition is narrowed to exactly the
   case it actually needs to prevent (a projected-Distinct operand that already emitted `Grouping` earlier) —
   this SAME, already-correctly-positioned branch fires in exactly the right pipeline position (after
   `$unionWith`, after `TrailingOps`) with no new branch, no new pipeline-stage type, and no new IR.
2. **`GroupBy_skip_0_take_0_aggregate`** (`Where(...).GroupBy(o => o.CustomerID).Skip(0).Take(0).Select(g =>
   new { g.Key, Total = g.Count() })`, `assertEmpty: true`) — `Skip`/`Take` composed on a bare, still-ungrouped
   `GroupBy(key)` result declines via `NativeSlotPopulator`'s general post-terminal guard (the same one every
   post-group slot operator hits, with three existing exemptions: Where/OrderBy/ThenBy). This needs a FOURTH
   exemption, mirroring the existing `PendingGroupOrderings`/`GroupOrderOp` mechanism (stash the raw
   `Skip`/`Take` count on the ungrouped result, consumed once the terminal Select finalizes `Grouping`) — but
   simpler than orderings, because a paging count (unlike an ordering key selector, which may be an aggregate
   needing a not-yet-existent `$group` accumulator) needs NO deferred resolution: it is always translatable
   immediately via the SAME `TranslateCountExpression` helper the ordinary (non-GroupBy) `Skip`/`Take` arms
   already use. `MongoPipelineFactory.NormalizePagingStages` (confirmed by direct read) already rewrites a
   `$limit: 0` to the impossible-`$match` shape generically, at ANY pipeline position, at `Build` time — so
   the zero-Take case needs **no GroupBy-specific handling at all** once the paging is correctly routed to a
   post-`$group`/pre-flatten position.

**Tech Stack:** C#, EF Core 8/9/10 provider internals, xUnit (plain `Assert.*`), MongoDB aggregation pipeline
(`$group`, `$unionWith`, `$skip`, `$limit`).

**Spec:** `docs/superpowers/specs/2026-09-24-native-groupby-fallback-reduction-design.md` (SP6 section) — this
plan's own Architecture text above supersedes that section's "Approach" paragraphs for the reasons given
there (both gaps turned out smaller/more targeted on direct investigation); the spec's test list and priority
ordering still apply.

## As-built deviations

1. **Task 2 Step 9's premise about `Skip`/`Take` composed AFTER the terminal Select was wrong.** The step text
   called this "an ALREADY-supported, pre-existing `PostGroupOps` shape" and said the third functional test
   (`GroupBy_Select_then_Skip_0_take_0_unaffected_by_pending_paging_carveout`) should assert `Assert.Empty`.
   Running it showed this shape still declines today for a genuine `GroupBy` (unrelated to this plan —
   `PostGroupOps`'s own EF-322 carve-out is scoped to a projected Distinct only). The committed test instead
   asserts `NativeTranslationNotSupportedException`.
2. **KNOWN BUG discovered during the Task 1 fix round** (`MongoSelectLowerer.cs`, `SetOperation.OperandsProjected`
   branch, ~line 146): a GroupBy composed on top of a projected-Distinct-operand Union/Concat reads
   `select.Grouping` there expecting the operand's own pre-combine grouping, but
   `SnapshotPriorGroupingForNestedGroupBy` has already moved it aside and overwritten `Grouping` with the outer
   GroupBy's — so the OUTER grouping's `$group` is wrongly emitted before the `$unionWith`. Pre-existing,
   untriggered by this plan's own two edits; tagged `EF-TBD`, not fixed as part of SP6.
3. **This fix wave's own three declines** (post-merge review, 2026-09-27): `Skip`/`Take` composed on a bare
   `GroupBy(key)` result, followed by a later `Where`(HAVING)/`OrderBy`/bare terminal aggregate/nested `GroupBy`
   in the SAME chain, used to either silently drop the paging or apply it in the wrong pipeline order (wrong
   data under `MongoQueryMode.Native`, not merely a missed-native-optimization). All three now decline cleanly
   instead — see `NativeSlotPopulator.cs`'s Where/OrderBy carve-outs, `NativeGroupByBinder.TryBindGroupTerminalAggregate`,
   and `MongoQueryableMethodTranslatingExpressionVisitor.TranslateGroupBy`'s nested-GroupBy branch.

## Global Constraints

- **`Native == DriverLinq` invariant** — every target test's `AssertQuery`/`AssertMql` must keep passing under
  `MongoQueryMode.Native` exactly as it does today (via fallback), in addition to newly passing under
  `MongoQueryMode.NativeOnly`.
- **No new `MongoExpression`, pipeline-stage, or `MongoSelectOp` subtype.** Task 1 reuses the EXISTING
  `MongoGroupStage`/`MongoProjectStage` emission the lowerer already has for a finalized `Grouping`. Task 2
  reuses the EXISTING `MongoSkipOp`/`MongoLimitOp` (siblings of `MongoSortOp` under `MongoSelectOp`,
  `Expressions/MongoSelectOp.cs`) and the existing `AppendSelectOpStages` helper the lowerer already uses for
  `PostGroupOps`/`TrailingOps`/`PostJoinOps`.
- **Both tasks touch `MongoSelectLowerer.cs`'s SAME lowering method, at two DIFFERENT, non-overlapping
  insertion points** (Task 1: the `select.Grouping is { } grouping && select.SetOperation == null` guard,
  currently ~line 271; Task 2: immediately after the `GroupOrderOp` sort block, currently ~line 307). Task 2's
  implementer must re-locate its insertion point by the `GroupOrderOp`/comment text, not by the absolute line
  number given here — Task 1 will have already shifted it slightly.
- **A shape this plan doesn't recognize must DECLINE, never silently mis-resolve** — same discipline every
  prior SP established.
- `src/` is nullable-enabled — no new warnings.
- Unit tests use plain xUnit `Assert.*` — FluentAssertions is not referenced in this repo.

## Review Focus

- **A GroupBy composed after `Intersect`/`Except` (not `Union`/`Concat`)** must keep declining — Task 1's own
  guard relaxation is scoped to `IsSetOpTerminalOnly` (which `MongoSelectDefinition.IsSetOp` covers for ALL
  set-op kinds), so double-check whether `Intersect`/`Except` composed with a following GroupBy is even
  reachable as `IsSetOpTerminalOnly` today, and if so, confirm Task 1's own test proves it goes native too (if
  the mechanism generalizes safely) or explicitly stays out of scope (if `Intersect`/`Except`'s own "excluded
  from nesting entirely" rule, per `Query/AGENTS.md`'s set-ops invariant, makes the combination structurally
  unreachable) — do not leave this unverified either way.
- **A projected-Distinct operand of a Union, followed by a GroupBy** (e.g. `Select(o => new
  {...}).Distinct().Union(...).GroupBy(...)`) — the EXISTING `OperandsProjected` branch in
  `MongoSelectLowerer` already emits ITS OWN `Grouping` before the `$unionWith`; Task 1's relaxed guard at
  the ~line 271 branch must not double-emit that grouping a second time. The plan's own guard rewrite
  (`select.SetOperation is not { OperandsProjected: true }`) is designed to prevent exactly this — Task 1's
  own test must prove the double-emit doesn't happen, not just that the NEW shape works.
- **Repeated `Skip`/`Take` on a bare `GroupBy(key)` result** (e.g. `.GroupBy(key).Skip(1).Skip(2).Select(...)`)
  — Task 2's carve-out must record BOTH into `PendingGroupPaging` in arrival order (matching the ordinary,
  non-GroupBy `PipelineOps` paging's own "repeated paging is natively representable" behavior, pinned by
  `SlotPopulationTests.Repeated_paging_is_native_representable`), not overwrite the first with the second.
- **`Skip`/`Take` composed on a bare `GroupBy(key)` result whose ordering shape is UNSUPPORTED** (a
  `PendingGroupOrderings` entry the eventual `TryBindGroupProjection` call declines) must still decline the
  WHOLE query cleanly — Task 2's own carve-out records `PendingGroupPaging` unconditionally (a paging count
  never itself fails to translate for the shapes this ticket needs), so confirm the ALREADY-EXISTING failure
  path (an unsupported ordering or accumulator shape making `TryBindGroupProjection` return `false`) still
  correctly abandons the whole native attempt, including the recorded `PendingGroupPaging`, rather than
  partially applying it.
- **`Skip`/`Take` composed AFTER the terminal Select** (the OPPOSITE composition order — e.g.
  `GroupBy(key).Select(g => new {...}).Skip(0)`) must be completely unaffected by Task 2's new carve-out — it
  is a pre-existing, ALREADY-covered shape (`PostGroupOps`, gated on `Grouping != null`), and Task 2's new
  carve-out's own `Grouping == null` condition must not accidentally widen to catch it too.

---

### Task 1: GroupBy composed after a completed Union/Concat

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs`
  — `TranslateGroupBy` (currently lines ~2505-2564)
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoSelectLowerer.cs` — the `Grouping`
  emission block (currently ~line 271)
- Test: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeSetOpsTests.cs` (no dedicated unit-test
  harness exists for `TranslateGroupBy`'s own guard interacting with a completed `SetOperation` — the closest
  unit file, `MongoSetOperationRouteTests.cs`, only tests `MongoSelectDefinition.Route`/`HasTerminalOperator`
  classification from hand-built state, never a real GroupBy-after-Union LINQ tree — so this task is verified
  end-to-end via a functional test, matching this repo's own convention for set-op coverage)

**Interfaces:**
- Consumes: `MongoSelectDefinition.IsSetOpTerminalOnly : bool` (existing, `Expressions/MongoSelectDefinition.cs`
  line ~798 — `IsSetOp && !IsGroupBy && !IsDistinct && Grouping == null && UnwindSources.Count == 0 &&
  Projection.Count == 0`), `MongoSelectDefinition.SetOperation : MongoSetOperation?` (existing, has-a
  `OperandsProjected : bool`).
- Produces: nothing new for later tasks — Task 3 verifies this shape end-to-end.

- [ ] **Step 1: Write the failing functional test**

Open `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeSetOpsTests.cs`. It tests against a
private `Item(Id, Name, Value)` entity, seeded by `SeedCollection(name)` (5 items, unique `Name`s "One".."Five",
`Value` 1..5), with a context built via `Make(collection, mode)` (returns `SingleEntityDbContext<Item>`,
`db.Entities`). Find its LAST test method (search for the final `[Fact]`/`[Theory]` before the file's closing
`}`) and add this test immediately after it, before the closing brace:

```csharp
    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.NativeOnly)]
    public void GroupBy_after_Union_goes_native(MongoQueryMode mode)
    {
        // Union_simple_groupby's exact shape: Where(...).Union(Where(...)).GroupBy(key).Select(aggregate).
        // Union of {Value<=3} ({One,Two,Three}) and {Value>=3} ({Three,Four,Five}) dedups the shared
        // Value==3 document (Three) — 5 distinct items, unique Names, so GroupBy(Name) yields 5 groups of 1.
        var collection = SeedCollection(nameof(GroupBy_after_Union_goes_native) + mode);
        using var db = Make(collection, mode);

        var result = db.Entities
            .Where(i => i.Value <= 3)
            .Union(db.Entities.Where(i => i.Value >= 3))
            .GroupBy(i => i.Name)
            .Select(g => new { g.Key, Total = g.Count() })
            .OrderBy(r => r.Key)
            .ToArray();

        Assert.Equal(5, result.Length);
        Assert.All(result, r => Assert.Equal(1, r.Total));
        Assert.Equal(["Five", "Four", "One", "Three", "Two"], result.Select(r => r.Key).ToArray());
    }
```

- [ ] **Step 2: Run the test to confirm it fails under `NativeOnly`**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10" -v quiet
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~GroupBy_after_Union_goes_native" \
  --logger "console;verbosity=normal"
```

Expected: `GroupBy_after_Union_goes_native(NativeOnly)` FAILS with `NativeTranslationNotSupportedException`
(`TranslateGroupBy`'s guard marks the query non-native before `TryBindGroupKey` ever runs).
`GroupBy_after_Union_goes_native(Native)` PASSES already (driver-LINQ fallback already gives the right
answer).

- [ ] **Step 3: Relax the `TranslateGroupBy` guard**

In `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs`, find
`TranslateGroupBy` (currently line ~2505). Locate this block (currently lines 2523-2527):

```csharp
        // The guard must read state as it stood BEFORE this GroupBy call — captured here, before the
        // unconditional IsGroupBy assignment below (both the guard branch and the normal-binding branch set
        // IsGroupBy, so it is hoisted above the if/else; reading Select.HasTerminalOperator AFTER that
        // assignment would always be true and defeat the guard).
        var hadTerminalGrouping = mongoQueryExpression.Select.HasTerminalOperator;
```

Add a second captured local immediately after it, for the SAME reason (must read BEFORE `IsGroupBy` flips to
true at line ~2547, which would make `IsSetOpTerminalOnly`'s own `!IsGroupBy` conjunct always false):

```csharp
        var hadTerminalGrouping = mongoQueryExpression.Select.HasTerminalOperator;

        // EF-322 SP6: a GroupBy composed directly on a COMPLETED set-op terminal (Union/Concat/Intersect/
        // Except, no grouping/projection/lookup of its own — IsSetOpTerminalOnly) is not the Distinct/prior-
        // grouping overwrite hazard hadTerminalGrouping exists to catch: the set op's own operands never set
        // Grouping (a plain whole-entity Union/Concat operand, per TranslateSetOperation's own admission
        // guard), so there is nothing for TryBindGroupKey to silently overwrite. Mirrors TranslateSelect's own
        // sibling exemption for the identical reason (search this file for IsSetOpTerminalOnly to find it).
        var wasSetOpTerminalOnly = mongoQueryExpression.Select.IsSetOpTerminalOnly;
```

Then find the guard itself (currently lines 2549-2552):

```csharp
        if (hadTerminalGrouping && !hasFinalizedPriorGrouping)
        {
            mongoQueryExpression.Select.MarkNotNativelyRepresentable();
        }
```

Change it to:

```csharp
        if (hadTerminalGrouping && !hasFinalizedPriorGrouping && !wasSetOpTerminalOnly)
        {
            mongoQueryExpression.Select.MarkNotNativelyRepresentable();
        }
```

- [ ] **Step 4: Run the test again — confirm it still fails, now inside `TryBindGroupKey`/the lowerer**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10" -v quiet
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~GroupBy_after_Union_goes_native" \
  --logger "console;verbosity=normal"
```

Expected: `NativeOnly` STILL fails, but the exception (or wrong/empty result) now comes from further downstream
— `TryBindGroupKey`/`TryBindGroupProjection` now run and (assuming they succeed, since `c.City` is an ordinary
member access against the entity) set `Grouping`, but `MongoSelectLowerer`'s emission guard at ~line 271 still
silently skips emitting the `$group` stage entirely (because `SetOperation != null`), so the query likely
throws deserializing a missing field, or the pipeline is simply wrong — either way, still red. This step exists
to confirm Step 3 alone is insufficient (proving Step 5 is actually needed), not to reach green yet.

- [ ] **Step 5: Relax the lowerer's `Grouping` emission guard**

In `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoSelectLowerer.cs`, find the block (currently
line 271):

```csharp
        if (select.Grouping is { } grouping && select.SetOperation == null)
```

Change the condition to admit a set-op terminal EXCEPT the one case that already emitted `Grouping` earlier
(the `OperandsProjected` branch a few dozen lines above, inside the `if (select.SetOperation is { } setOp)`
block) — read the existing comment immediately above this line (currently lines 264-270) first, and extend it
rather than replacing it wholesale, since its explanation of the double-emit hazard is still accurate and now
needs one more sentence:

```csharp
        // EF-322: SetOperation == null guards against DOUBLE-emitting a projected-Distinct-operand set op's
        // own Grouping. That shape's Select.Grouping is STILL set here (TryTranslateSetOperation never
        // clears it — it is source1's OWN Distinct, unrelated to the set-op machinery), and the SetOperation
        // block above the UnwindSource check falls through to here rather than returning — it already
        // emitted this exact $group + flattening $project itself, immediately before the set-op stage, once
        // per the OperandsProjected branch. Re-emitting it here would produce a spurious SECOND $group over
        // the already-combined/deduped result.
        //
        // EF-322 SP6: a GroupBy composed AFTER a completed Union/Concat (select.Grouping is the OUTER,
        // post-set-op grouping, never the OperandsProjected branch's own pre-combine one) is NOT that hazard —
        // OperandsProjected is false for two plain whole-entity operands, so the branch above never ran, and
        // this IS the only place this Grouping can be emitted. The method has no early return between the
        // SetOperation block and here (see its own "no early return" comment), so this fires in exactly the
        // right position: after $unionWith/dedup and after TrailingOps, before this Grouping's own flatten
        // $project just below.
        if (select.Grouping is { } grouping && select.SetOperation is not { OperandsProjected: true })
```

- [ ] **Step 6: Run the test again to confirm it passes**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10" -v quiet
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~GroupBy_after_Union_goes_native" \
  --logger "console;verbosity=normal"
```

Expected: both `Native` and `NativeOnly` PASS.

- [ ] **Step 7: Write the Review Focus regression test — a projected-Distinct operand ahead of the Union**

Add this test immediately after `GroupBy_after_Union_goes_native` in the same file:

```csharp
    [Fact]
    public void GroupBy_after_Union_of_projected_Distinct_operand_does_not_double_emit_grouping()
    {
        // Review Focus: a projected-Distinct operand's OWN Grouping (emitted by the OperandsProjected branch,
        // ahead of the $unionWith) must not be re-emitted by this task's relaxed guard at the SAME time as
        // the outer post-Union GroupBy's own Grouping — confirms the two coexist correctly, not just that the
        // simple case works. Names are already unique in SeedCollection's data, so Distinct(Name) on each
        // side is a no-op; Union of the same 5-name set with itself dedups back to 5, and GroupBy(Name)
        // yields 5 groups of 1 — the same expected shape as GroupBy_after_Union_goes_native, but reached
        // through a projected-Distinct operand on BOTH sides of the Union instead of a plain Where.
        var collection = SeedCollection(nameof(GroupBy_after_Union_of_projected_Distinct_operand_does_not_double_emit_grouping));
        using var db = Make(collection, MongoQueryMode.NativeOnly);

        var result = db.Entities
            .Select(i => new { i.Name })
            .Distinct()
            .Union(db.Entities.Select(i => new { i.Name }).Distinct())
            .GroupBy(x => x.Name)
            .Select(g => new { g.Key, Total = g.Count() })
            .OrderBy(r => r.Key)
            .ToArray();

        Assert.Equal(5, result.Length);
        Assert.All(result, r => Assert.Equal(1, r.Total));
    }
```

- [ ] **Step 8: Run this test; if it declines instead of going native, see the note below**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10" -v quiet
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~double_emit_grouping" --logger "console;verbosity=normal"
```

If this test throws `NativeTranslationNotSupportedException` for a reason UNRELATED to this task (e.g. the
projected-Distinct-operand admission gate, `IsPlainDistinctSelect`, declines this exact shape for a reason
that predates this plan), that is a PRE-EXISTING limitation of set-op operand eligibility, not something
Task 1 is scoped to fix — replace the test's operand shape with the simplest one that DOES reach
`OperandsProjected: true` (grep `MongoQueryableMethodTranslatingExpressionVisitor` for
`IsPlainDistinctSelect` to find its exact admission criteria first), while keeping the test's actual point
(a projected-Distinct operand's `Grouping` coexisting with an outer post-Union `Grouping` without a
double-emit). Record whatever adjustment was needed, and why, in the implementer's report.

- [ ] **Step 9: Write and pin whichever outcome is correct for `Intersect`/`Except` composed with a GroupBy**

Review Focus: `IsSetOpTerminalOnly` is defined in terms of `IsSetOp` generically (`MongoSelectDefinition.cs`,
covers Union/Concat/Intersect/Except alike), so Step 3's guard relaxation ALSO exempts a GroupBy composed
after a completed `Intersect`/`Except` from `TranslateGroupBy`'s own guard — but Intersect/Except lower via a
structurally different set-difference/source-tagging pipeline (see `AppendSetOpChainStages` and
`NativeSetOpsTests.cs`'s own class-level doc comment), not the plain `$unionWith [+ dedup]` shape Steps 1-8
exercised. Add ONE test proving whichever outcome actually happens today; don't guess which:

```csharp
    [Fact]
    public void GroupBy_after_Intersect_result_is_correct_under_native_and_either_goes_native_or_declines_cleanly()
    {
        var collection = SeedCollection(nameof(GroupBy_after_Intersect_result_is_correct_under_native_and_either_goes_native_or_declines_cleanly));
        using var nativeDb = Make(collection, MongoQueryMode.Native);

        // {1,2,3} ∩ {2,3,4} = {2,3} (Two, Three) — 2 distinct names, so GroupBy(Name) yields 2 groups of 1
        // regardless of which path (native or fallback) answers it.
        var native = nativeDb.Entities
            .Where(i => i.Value <= 3)
            .Intersect(nativeDb.Entities.Where(i => i.Value >= 2 && i.Value <= 4))
            .GroupBy(i => i.Name)
            .Select(g => new { g.Key, Total = g.Count() })
            .OrderBy(r => r.Key)
            .ToArray();

        Assert.Equal(2, native.Length);
        Assert.All(native, r => Assert.Equal(1, r.Total));

        using var nativeOnlyDb = Make(collection, MongoQueryMode.NativeOnly);
        var query = nativeOnlyDb.Entities
            .Where(i => i.Value <= 3)
            .Intersect(nativeOnlyDb.Entities.Where(i => i.Value >= 2 && i.Value <= 4))
            .GroupBy(i => i.Name)
            .Select(g => new { g.Key, Total = g.Count() })
            .OrderBy(r => r.Key);

        // Run once under NativeOnly to observe which outcome actually happens, then keep ONLY the branch
        // that matches: either it succeeds (replace this try/catch with a plain Assert.Equal(native,
        // query.ToArray()) proving it went native), or it throws NativeTranslationNotSupportedException
        // (replace with Assert.Throws<NativeTranslationNotSupportedException>(() => query.ToArray()) proving
        // it declines cleanly rather than silently returning wrong data). Do not leave the try/catch in the
        // final committed test — pick the one real branch and delete the other.
        try
        {
            Assert.Equal(native, query.ToArray());
        }
        catch (NativeTranslationNotSupportedException)
        {
            // Acceptable outcome too — but only if this catch block is what the real run actually hits.
        }
    }
```

Run it, observe which branch actually executes, then edit the test to keep only that branch (delete the
`try`/`catch`, keep the matching single assertion) before committing. Either outcome is acceptable for this
plan — Intersect/Except going native too is a welcome bonus, and a clean decline is exactly as correct as
long as it does not silently return wrong rows under `MongoQueryMode.Native`. Record which outcome was
observed in the implementer's report.

- [ ] **Step 10: Run the whole Functional suite**

```bash
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --no-build
```

Expected: PASS, 0 failed.

- [ ] **Step 11: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs \
        src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoSelectLowerer.cs \
        tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeSetOpsTests.cs
git commit -m "EF-322: translate a GroupBy composed after a completed Union/Concat"
```

---

### Task 2: Skip/Take composed between GroupBy and its terminal Select

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoSelectDefinition.cs` — add
  `PendingGroupPaging`/`GroupPagingOps` (mirroring `PendingGroupOrderings`/`GroupOrderOp`, currently lines
  ~702-727)
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeSlotPopulator.cs` — add a Skip/Take
  carve-out (mirroring the OrderBy/ThenBy carve-out, currently lines 72-100)
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeGroupByBinder.cs` —
  `TryBindGroupProjection` (consume `PendingGroupPaging` into `GroupPagingOps`)
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoSelectLowerer.cs` — emit
  `GroupPagingOps` immediately after the existing `GroupOrderOp` block (currently ~line 307)
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/SlotPopulationTests.cs` (the
  slot-population side — proving `GroupBy(key).Skip(n)` no longer trips the general post-terminal guard) and
  `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/NativeGroupByBinderTests.cs` (the
  consumption side — proving `TryBindGroupProjection` turns `PendingGroupPaging` into `GroupPagingOps`,
  mirroring this file's own existing `Pending_order_by_key_resolves_to_id_reference`-style tests for
  `PendingGroupOrderings`)

**Interfaces:**
- Consumes: `MongoSkipOp(MongoExpression Count)` / `MongoLimitOp(MongoExpression Count)` (existing sealed
  records, `Expressions/MongoSelectOp.cs`), `NativeSlotPopulator.TranslateCountExpression(Expression) :
  MongoExpression?` (existing private static helper, same file, ~line 661 — accessible since the new carve-out
  lives in the same class), `AppendSelectOpStages(IReadOnlyList<MongoSelectOp> ops, List<MongoPipelineStage>
  stages, ...)` (existing, `MongoSelectLowerer.cs` — called identically for `PostGroupOps`/`TrailingOps`/
  `PostJoinOps`, e.g. `AppendSelectOpStages(select.PostGroupOps, stages, sortFields);`).
- Produces: `MongoSelectDefinition.PendingGroupPaging : List<MongoSelectOp>?` (settable, mirrors
  `PendingGroupOrderings`), `MongoSelectDefinition.GroupPagingOps : IReadOnlyList<MongoSelectOp>` (read-only,
  backed by a private list, mirrors the `PostGroupOps`/`TrailingOps` pattern, defaults to `[]`) — Task 3
  consumes neither directly; they are internal plumbing this task's own tests exercise.

- [ ] **Step 1: Write the failing unit tests (slot population + consumption)**

Open `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/SlotPopulationTests.cs`. It builds a
query and translates it via the file's own `TranslateToMongoQuery<T>(q => ...)` helper (see
`GroupBy_key_with_aggregate_Select_routes_native_GroupBy`/`GroupBy_without_terminal_Select_falls_back_
without_throwing` for the exact pattern against its private `Customer` entity, which has `Age`/`Name`
members). Find `GroupBy_without_terminal_Select_falls_back_without_throwing` (search for that method name)
and add this test immediately after it:

```csharp
    [Fact]
    public void Skip_on_bare_GroupBy_result_defers_to_PendingGroupPaging_without_marking_non_native()
    {
        // EF-322 SP6: Skip/Take composed directly on the still-ungrouped GroupBy(key) result must be
        // deferred (PendingGroupPaging), not declined by the general post-terminal guard — mirrors this
        // file's own OrderBy/ThenBy carve-out proof for the identical composition position. Route is still
        // Fallback here (same as GroupBy_without_terminal_Select_falls_back_without_throwing, immediately
        // above) because no terminal Select ever finalizes Grouping in THIS query either — the point of this
        // test is that HasUnsupportedOperator stays false and the Skip was actually recognized and deferred,
        // not silently declined via the catch-all MarkNotNativelyRepresentable (which would ALSO leave Route
        // at Fallback, so Route alone cannot distinguish the two — HasUnsupportedOperator and
        // PendingGroupPaging are the actual discriminators here).
        var mongoQuery = TranslateToMongoQuery<Customer>(q => q.GroupBy(c => c.Age).Skip(0));

        Assert.False(mongoQuery.Select.HasUnsupportedOperator);
        var op = Assert.Single(mongoQuery.Select.PendingGroupPaging!);
        Assert.Equal(0, Assert.IsType<MongoConstantExpression>(Assert.IsType<MongoSkipOp>(op).Count).Value);
    }
```

Then open `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/NativeGroupByBinderTests.cs`.
Find `Pending_order_by_key_resolves_to_id_reference` (search for that method name) and add these two tests
immediately after the whole "pending OrderBy/ThenBy" test group (search for the next `// ──` section-comment
header after it, and insert before that header):

```csharp
    // ── TryBindGroupProjection: pending Skip/Take (composed before the Select) ──────────────────

    [Fact]
    public void Pending_paging_resolves_to_GroupPagingOps_in_arrival_order()
    {
        var mongoQ = BoundScalarKeyQuery();
        mongoQ.Select.PendingGroupPaging =
        [
            new MongoSkipOp(new MongoConstantExpression(0, forSerialization: null)),
            new MongoLimitOp(new MongoConstantExpression(0, forSerialization: null))
        ];

        Expression<Func<IGrouping<string, Order>, object>> proj =
            g => new { g.Key, Total = g.Count() };
        Assert.True(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out _));

        Assert.Collection(mongoQ.Select.GroupPagingOps,
            op => Assert.Equal(0, Assert.IsType<MongoConstantExpression>(Assert.IsType<MongoSkipOp>(op).Count).Value),
            op => Assert.Equal(0, Assert.IsType<MongoConstantExpression>(Assert.IsType<MongoLimitOp>(op).Count).Value));
        Assert.Null(mongoQ.Select.PendingGroupPaging);
    }

    [Fact]
    public void No_pending_paging_leaves_GroupPagingOps_empty()
    {
        var mongoQ = BoundScalarKeyQuery();
        Expression<Func<IGrouping<string, Order>, object>> proj =
            g => new { g.Key, Total = g.Count() };
        Assert.True(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, out _));

        Assert.Empty(mongoQ.Select.GroupPagingOps);
    }
```

- [ ] **Step 2: Run both test files to confirm the new tests fail to compile/fail**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10" -v quiet
```

Expected: build FAILS — `PendingGroupPaging`/`GroupPagingOps` don't exist yet on `MongoSelectDefinition`. This
confirms Steps 1's tests correctly target not-yet-existing members before any production code exists.

- [ ] **Step 3: Add `PendingGroupPaging`/`GroupPagingOps` to `MongoSelectDefinition`**

In `src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoSelectDefinition.cs`, find `PendingGroupOrderings`
and `GroupOrderOp` (currently lines 702-727). Add, immediately after `GroupOrderOp`'s closing `}` (currently
line 727):

```csharp
    /// <summary>
    /// Raw, already-translated <c>Skip</c>/<c>Take</c> ops (<see cref="MongoSkipOp"/>/<see cref="MongoLimitOp"/>)
    /// composed directly on the still-ungrouped <c>GroupBy(key)</c> result (before the terminal <c>Select</c>
    /// that finalizes <see cref="Grouping"/>) — e.g. <c>GroupBy(o =&gt; o.CustomerID).Skip(0).Take(0)</c>.
    /// Recorded by <c>NativeSlotPopulator.PopulateNativeSlots</c>'s pending-paging carve-out, in arrival order
    /// (mirrors the ordinary, non-GroupBy <see cref="PipelineOps"/> paging's own "repeated paging is natively
    /// representable" behavior). Unlike <see cref="PendingGroupOrderings"/>, a paging count needs NO deferred
    /// resolution — it never references a not-yet-existent <c>$group</c> accumulator — so each entry here is
    /// already a fully-formed <see cref="MongoSelectOp"/> by the time it lands here; the "pending" naming is
    /// only about WHEN it may be committed (only once the terminal Select actually finalizes the grouping),
    /// not about needing further translation. Consumed and cleared by
    /// <c>NativeGroupByBinder.TryBindGroupProjection</c>, which moves it verbatim into
    /// <see cref="GroupPagingOps"/>. <see langword="null"/> for every query that never took this path.
    /// </summary>
    internal List<MongoSelectOp>? PendingGroupPaging { get; set; }

    private List<MongoSelectOp> _groupPagingOps = [];

    /// <summary>
    /// The <c>$skip</c>/<c>$limit</c> ops to run immediately AFTER <see cref="GroupOrderOp"/>'s sort (ORDER BY
    /// orders the groups; SKIP/TAKE then pages the ordered result) and BEFORE the flattening <c>$project</c> —
    /// resolved from <see cref="PendingGroupPaging"/> by <c>NativeGroupByBinder.TryBindGroupProjection</c>.
    /// Empty for every query that never took this path.
    /// </summary>
    public IReadOnlyList<MongoSelectOp> GroupPagingOps
    {
        get => _groupPagingOps;
        internal set => _groupPagingOps = [.. value];
    }
```

- [ ] **Step 4: Add the Skip/Take carve-out to `NativeSlotPopulator`**

In `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeSlotPopulator.cs`, find the OrderBy/ThenBy
carve-out (currently lines 72-100, ending with its own `return;` at line 99). Add a new carve-out immediately
after it (before the "Post-group slot-operator guard" comment that currently starts at line 102):

```csharp
        // EF-322 SP6: Skip/Take composed DIRECTLY on the still-ungrouped GroupBy(key) result — e.g.
        // GroupBy(o => o.CustomerID).Skip(0).Take(0) — mirrors the OrderBy/ThenBy carve-out immediately
        // above, but needs NO deferred resolution: a paging count is always translatable right now via the
        // SAME TranslateCountExpression helper the ordinary (non-GroupBy) Skip/Take arms below already use —
        // it never references a $group accumulator that doesn't exist yet. Scoped to Grouping == null (not
        // yet finalized) for the identical reason as the OrderBy/ThenBy carve-out: the OPPOSITE composition
        // order (Skip/Take composed AFTER the terminal Select, over a finalized Grouping) must keep falling
        // through unchanged into PostGroupOps, handled elsewhere.
        if (mongoQ.Select.IsGroupBy && mongoQ.Select.Grouping == null && mongoQ.Select.PendingGroupKey != null
            && (methodDefinition == QueryableMethods.Skip || methodDefinition == QueryableMethods.Take))
        {
            var count = TranslateCountExpression(call.Arguments[1]);
            if (count is null)
            {
                mongoQ.Select.MarkNotNativelyRepresentable();
                return;
            }

            MongoSelectOp op = methodDefinition == QueryableMethods.Skip
                ? new MongoSkipOp(count)
                : new MongoLimitOp(count);
            (mongoQ.Select.PendingGroupPaging ??= []).Add(op);
            return;
        }

```

- [ ] **Step 5: Consume `PendingGroupPaging` in `NativeGroupByBinder.TryBindGroupProjection`**

In `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeGroupByBinder.cs`, find where
`PendingGroupOrderings` is consumed and cleared (search for `select.PendingGroupOrderings = null;` — currently
line 342, immediately after `select.GroupOrderOp = ...` is set). Add immediately after that line:

```csharp
        select.GroupPagingOps = select.PendingGroupPaging ?? [];
        select.PendingGroupPaging = null;
```

- [ ] **Step 6: Emit `GroupPagingOps` in `MongoSelectLowerer`**

In `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoSelectLowerer.cs`, find the `GroupOrderOp`
emission block (currently lines 300-307):

```csharp
            // OrderBy/ThenBy composed directly on the ungrouped GroupBy result (before the terminal Select) —
            // resolved by NativeGroupByBinder.TryBindGroupProjection into GroupOrderOp. Must run BEFORE the
            // flatten $project below: an ordering aggregate the Select doesn't project (e.g. orders by
            // Count() but projects Sum()) would no longer be readable once the flatten $project drops it.
            if (select.GroupOrderOp is { } groupOrderOp)
            {
                AppendSortStages(groupOrderOp, stages, sortFields);
            }
```

Add immediately after this block, before the `if (select.Projection.Count > 0)` flatten `$project` block that
follows it:

```csharp
            // EF-322 SP6: Skip/Take composed directly on the ungrouped GroupBy result (before the terminal
            // Select) — resolved by NativeGroupByBinder.TryBindGroupProjection into GroupPagingOps. Must run
            // AFTER GroupOrderOp's sort (SKIP/TAKE pages the ORDERED result — SQL's own evaluation order) and
            // BEFORE the flatten $project (paging reduces the number of GROUP documents; the flatten only
            // reshapes each surviving one). A zero Take needs no special handling here — MongoPipelineFactory
            // .NormalizePagingStages rewrites a $limit: 0 at ANY pipeline position generically, at Build time.
            if (select.GroupPagingOps.Count > 0)
            {
                AppendSelectOpStages(select.GroupPagingOps, stages, sortFields);
            }
```

- [ ] **Step 7: Run both unit test files to confirm everything passes**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10" -v quiet
dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~SlotPopulationTests|FullyQualifiedName~NativeGroupByBinderTests"
```

Expected: all tests PASS, including every pre-existing test and the three new ones from Step 1.

- [ ] **Step 8: Run the whole Unit suite**

```bash
dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj \
  -c "Debug EF10" --no-build
```

Expected: PASS, 0 failed.

- [ ] **Step 9: Write functional coverage for the target shape, including the Review Focus edge cases**

Open `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeGroupByTests.cs`. Find its LAST test
method and add these three tests immediately after it, before the closing brace:

```csharp
    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.NativeOnly)]
    public void GroupBy_skip_0_take_0_goes_native_and_returns_empty(MongoQueryMode mode)
    {
        // GroupBy_skip_0_take_0_aggregate's exact shape.
        var seed = SeedOrders();
        using var db = CreateContext(seed, mode, nameof(GroupBy_skip_0_take_0_goes_native_and_returns_empty) + mode);

        var result = db.Entities
            .GroupBy(o => o.Country)
            .Skip(0)
            .Take(0)
            .Select(g => new { g.Key, Total = g.Count() })
            .ToArray();

        Assert.Empty(result);
    }

    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.NativeOnly)]
    public void GroupBy_repeated_Skip_on_bare_result_goes_native(MongoQueryMode mode)
    {
        // Review Focus: repeated Skip on a bare GroupBy result must accumulate in arrival order, not
        // overwrite. Skip(1) then Skip(1) again over 3 distinct countries (US/UK/FR) skips 1+1=2, leaving 1.
        var seed = SeedOrders();
        using var db = CreateContext(seed, mode, nameof(GroupBy_repeated_Skip_on_bare_result_goes_native) + mode);

        var result = db.Entities
            .GroupBy(o => o.Country)
            .Skip(1)
            .Skip(1)
            .Select(g => new { g.Key, Total = g.Count() })
            .ToArray();

        Assert.Single(result);
    }

    [Fact]
    public void GroupBy_Select_then_Skip_0_take_0_unaffected_by_pending_paging_carveout()
    {
        // Review Focus: paging composed AFTER the terminal Select (the OPPOSITE order — an
        // ALREADY-supported, pre-existing PostGroupOps shape) must be completely unaffected by this task's
        // new pre-Select carve-out.
        var seed = SeedOrders();
        using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(GroupBy_Select_then_Skip_0_take_0_unaffected_by_pending_paging_carveout));

        var result = db.Entities
            .GroupBy(o => o.Country)
            .Select(g => new { g.Key, Total = g.Count() })
            .Skip(0)
            .Take(0)
            .ToArray();

        Assert.Empty(result);
    }
```

- [ ] **Step 10: Run the new functional tests, then the whole Functional suite**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10" -v quiet
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~NativeGroupByTests" --logger "console;verbosity=normal"
```

Expected: PASS, 0 failed, including the 3 new tests. If `GroupBy_repeated_Skip_on_bare_result_goes_native`'s
expected count doesn't match — read `SeedOrders()`'s actual data and recompute from the REAL seed rather than
adjusting the assertion to whatever the run produces.

```bash
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --no-build
```

Expected: PASS, 0 failed (the FULL suite).

- [ ] **Step 11: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoSelectDefinition.cs \
        src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeSlotPopulator.cs \
        src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeGroupByBinder.cs \
        src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoSelectLowerer.cs \
        tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/SlotPopulationTests.cs \
        tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/NativeGroupByBinderTests.cs \
        tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeGroupByTests.cs
git commit -m "EF-322: translate Skip/Take composed between GroupBy and its terminal Select"
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
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~NorthwindGroupByQueryMongoTest&(FullyQualifiedName~Union_simple_groupby|FullyQualifiedName~GroupBy_skip_0_take_0_aggregate)" \
  --logger "console;verbosity=normal" > /tmp/sp6-native.log 2>&1
tail -60 /tmp/sp6-native.log
```

Both methods' current baselines were captured from the OLD driver-LINQ fallback — the native pipeline this
plan produces is unlikely to match either byte-for-byte (in particular, `Union_simple_groupby`'s Union dedup
is a single `MongoUnionWithStage` with `dedup: true`, not the legacy two-literal-`$group` shape currently in
the baseline; `GroupBy_skip_0_take_0_aggregate`'s zero-Take renders via the SAME `NormalizePagingStages`
impossible-`$match` rewrite the native path already uses elsewhere, not necessarily at the identical pipeline
position as the old `$skip:0`+`$match{_id:{$type:-1}}` pair). For any method whose `AssertMql` fails with
`Assert.Equal() Failure: Strings differ` (a baseline mismatch, NOT a data/behavior failure), regenerate:

```bash
EF_TEST_REWRITE_BASELINES=1 dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~NorthwindGroupByQueryMongoTest.<MethodName>"
git diff tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/NorthwindGroupByQueryMongoTest.cs
```

Confirm each diff shows a pipeline shape consistent with this plan's design, then rebuild and rerun WITHOUT
the env var to confirm green.

- [ ] **Step 3: Run the same 2 methods under `MONGODB_EF_NATIVE_ONLY=1`**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10" -v quiet
MONGODB_EF_NATIVE_ONLY=1 dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~NorthwindGroupByQueryMongoTest&(FullyQualifiedName~Union_simple_groupby|FullyQualifiedName~GroupBy_skip_0_take_0_aggregate)" \
  --logger "console;verbosity=normal" > /tmp/sp6-nativeonly.log 2>&1
tail -20 /tmp/sp6-nativeonly.log
```

Expected: both methods (4 with async) now PASS under `NativeOnly`.

- [ ] **Step 4: Re-measure the whole class's `NativeOnly` failure count, checking CONTENT not just the number**

```bash
MONGODB_EF_NATIVE_ONLY=1 dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~NorthwindGroupByQueryMongoTest" \
  --logger "console;verbosity=normal" > /tmp/sp6-full-nativeonly.log 2>&1
tail -8 /tmp/sp6-full-nativeonly.log
grep -E "^\s*Failed MongoDB" /tmp/sp6-full-nativeonly.log | sed -E 's/^\s*Failed //; s/\(async:.*//' | sed 's/ *$//' | sort -u
```

Expected: failure count drops from 26 (SP5's ending count, INCLUDING its own bonus fix) to 24, and the
method-NAME list is exactly SP5's own remaining names minus these 2. SP5's plan itself did not enumerate its
final 13-minus-bonus-fix list exhaustively by name (the bonus fix reduced it by 2 MORE than SP5's own target),
so re-derive the expected list yourself from THIS run's own baseline: capture the full name list BEFORE
starting Task 1 (a `git stash`-free `MONGODB_EF_NATIVE_ONLY=1` run against the tip of SP5, i.e. before any of
this plan's own commits), and diff THIS step's list against that captured one — do not assume a stale count
from an earlier plan document. If the list differs by anything other than exactly `Union_simple_groupby` and
`GroupBy_skip_0_take_0_aggregate` disappearing, investigate whether it's a genuine bonus fix (a baseline-only
fix, confirm via `AssertMql` mismatch not a data/behavior failure) or a sign this plan's own change touched
something unintended, before assuming either way.

- [ ] **Step 5: Run the full Specification suite (not just the GroupBy class)**

```bash
dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --logger "console;verbosity=normal" > /tmp/sp6-spec-full.log 2>&1
tail -8 /tmp/sp6-spec-full.log
```

Expected: PASS, 0 failed. Pay particular attention to the Set-ops and Distinct spec-test classes (Task 1
touches shared `MongoSelectLowerer` code every set-op query goes through) and to any OTHER GroupBy test whose
Select is followed by paging (Task 2's carve-out is scoped narrowly, but confirm no sibling shape regressed).

- [ ] **Step 6: Run the full Functional suite (not just NativeSetOpsTests/NativeGroupByTests)**

```bash
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --no-build --logger "console;verbosity=normal" > /tmp/sp6-functional-full.log 2>&1
tail -8 /tmp/sp6-functional-full.log
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
  echo "=== $CFG functional (GroupBy + SetOps) ==="
  dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj -c "Debug $CFG" --no-build --filter "FullyQualifiedName~NativeGroupByTests|FullyQualifiedName~NativeSetOpsTests" 2>&1 | tail -5
done
```

Expected: PASS, 0 failed, on both EF8 and EF9, for all three commands.

- [ ] **Step 8: Commit any baseline changes from Step 2**

```bash
git add tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/NorthwindGroupByQueryMongoTest.cs
git commit -m "EF-322: verify SP6 post-group composition slice — baselines, full suites"
```

---

## After this plan

This plan covers SP6 only (2 tests). SP7–SP9 each get their own plan when their turn comes, using this
slice's post-landing `NativeOnly` failure count (24, confirmed in Task 3 Step 4) as their new baseline. Before
starting SP7's plan, re-verify SP7's own target tests' shapes against the actual EF Core base-test source the
same way this plan's own investigation (and SP2's/SP4's/SP5's own corrections) did — don't assume the design
doc's original bucketing is exact without a quick re-check. Per the design doc, SP7 is "DTO keys &
empty-key readback" (3 tests: `GroupBy_Dto_as_key_Select_Sum`, `Odata_groupby_empty_key`,
`GroupBy_empty_key_Aggregate_Key`) — note the existing `sp7-planning`/`native-cutover-order` memory records
describe a DIFFERENT, larger "SP7" (a materializer-performance capstone) from an earlier phase of this same
overall EF-322 effort; confirm which SP7 is meant before reusing either name, since the numbering has been
reused across two different sub-efforts on this branch.
