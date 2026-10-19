# Native post-join paging (`Skip`/`Take` recorded before a confirmed join) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make a `Join`/`LeftJoin` (single or chained) whose confirming `Select` is followed by `Skip`/`Take`
go native, when the join isn't already in the existing narrow left-outer-reference-navigation "safe to page
before `$lookup`" set — today `IsSingleEligibleNativeJoinScope` declines the whole join confirmation outright
in that case, forcing a driver-LINQ fallback (or a hard throw under `NativeOnly`).

**Architecture:** EF Core hoists a trailing `Skip`/`Take` (and anything composed before it, in the same
batch) to record into `Select.PipelineOps` *before* a join's pending result selector runs — this is measured,
documented fact already relied on elsewhere in this codebase, not new territory. Instead of declining when
that recorded paging isn't already safe to leave before `$lookup`/`$unwind`, move the WHOLE `PipelineOps`
snapshot (verbatim, in order) into a new deferred list that the lowerer emits immediately after the join's
`$lookup`/`$unwind` block(s) — the exact slot the existing `PostJoinOps` list already occupies for an
unrelated (`Where`-confirmed) trigger — and let the join confirm normally.

**Tech Stack:** C#/.NET, EF Core provider internals (`src/MongoDB.EntityFrameworkCore/Query/`), xUnit.

**Spec:** `docs/superpowers/specs/2026-09-22-native-post-join-paging-design.md`

## Global Constraints

- Multi-EF targeting: code under `src/` must build clean under EF8/EF9/EF10 (`Debug EF8`/`EF9`/`EF10`
  configurations). Nothing in this plan touches an EF-version-conditional API, so no new `#if` guards are
  expected — verify this assumption in the final task.
- This plan does **not** touch the reducer branch (`Select.Cardinality?.Reducer != null`) of
  `IsSingleEligibleNativeJoinScope` — it must keep declining exactly as today when a join isn't in the
  1:1-safe set. Do not fold the reducer case into the same deferred-ops mechanism without its own separate
  design; `NativeCardinalityBinder.TryBindReducer` has its own, different, already-measured reachable hazard.
- This plan does **not** touch `NativeSlotPopulator`'s `HasConfirmedJoinLookup` post-confirmation guard. A
  slot operator genuinely composed AFTER the confirming `Select` (not hoisted ahead of it by EF) must keep
  declining — this is a different, already out-of-scope case per the native-chained-join-scalar-projection
  plan's own pinned regression test (`Chained_join_scalar_leaf_projection_with_trailing_paging_still_declines_under_NativeOnly`
  in `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeJoinTests.cs` — **that test must still
  pass after this plan lands**, because ITS shape composes `Skip`/`Take` in a way that... — read Task 1's own
  note below before assuming this plan's fix makes it go native too; verify, don't assume, exactly which
  shapes actually get hoisted).
- The existing 1:1-safe carve-out's MQL baselines (`NorthwindMiscellaneousQueryMongoTest.Projection_take_projection`,
  `.Projection_skip_projection`, `.Projection_skip_take_projection`) must remain byte-for-byte unchanged — that
  path is untouched by this plan; any diff there means the new branching in
  `IsSingleEligibleNativeJoinScope` accidentally changed the safe-case behavior too.
- `MONGODB_EF_NATIVE_ONLY=1` is the only reliable "did this actually go native" signal — MQL-shape assertions
  under default `Native` mode do not distinguish native from a structurally-identical fallback pipeline (see
  `Query/AGENTS.md`, Common Pitfalls).

---

## File structure

- Modify `src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoSelectDefinition.cs` — add
  `PostLookupPagingOps` (a fourth deferred-ops list, alongside `TrailingOps`/`PostJoinOps`/`PostGroupOps`) and
  `DeferPipelineOpsPastConfirmedJoin()`.
- Modify `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs`
  — `IsSingleEligibleNativeJoinScope`'s combined `HasPaging || Cardinality?.Reducer != null` branch, split
  into a reducer branch (unchanged) and a paging branch (defer instead of decline).
- Modify `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoSelectLowerer.cs` — emit
  `select.PostLookupPagingOps` immediately after `select.PostJoinOps` in the existing step-2b block.
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/Expressions/` (new or existing
  `MongoSelectDefinition`-focused test file — check for one first; if none exists, create
  `MongoSelectDefinitionTests.cs`) for `DeferPipelineOpsPastConfirmedJoin`.
- Test: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeJoinTests.cs` — new end-to-end
  `NativeOnly` cases (the target shape, a differential-oracle correctness proof with unmatched rows, and a
  depth-1 whole-entity-leaf case).
- Test: `tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/NorthwindMiscellaneousQueryMongoTest.cs`
  — rebaseline `Join_Customers_Orders_Orders_Skip_Take_Same_Properties`.
- Modify `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md` — capability note (append only).

---

### Task 1: Pin current behavior with a failing `NativeOnly` test

**Files:**
- Test: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeJoinTests.cs`

**Interfaces:**
- Consumes: `MongoQueryMode.NativeOnly`, `CreateContext(seed, mode, name)`, `SeedOwnersOrdersAndLines()`
  (already in this file).
- Produces: a red test (`Chained_join_scalar_leaf_projection_with_paging_goes_native_under_NativeOnly`) that
  Task 2 turns green.

- [ ] **Step 1: Write the failing test**

Add to `NativeJoinTests.cs`, near the other `Chained_join_scalar_leaf_projection_*` tests (search for
`Chained_join_scalar_leaf_projection_with_trailing_paging_still_declines_under_NativeOnly` and add this
alongside it):

```csharp
[Fact]
public void Chained_join_scalar_leaf_projection_with_paging_goes_native_under_NativeOnly()
{
    // Native-post-join-paging plan (2026-09-22). Identical shape to
    // Chained_join_scalar_leaf_projection_with_trailing_paging_still_declines_under_NativeOnly (a plain,
    // non-left-outer Join chain, scalar leaves, then Skip/Take) — this plan is what flips it. EF Core hoists
    // the Skip/Take ahead of the chain's pending selector, so IsSingleEligibleNativeJoinScope sees
    // Select.HasPaging == true at confirmation time; since neither join here is a left-outer reference nav,
    // this plan's fix defers the whole recorded PipelineOps snapshot to run after both $lookup/$unwind pairs
    // instead of declining the join outright.
    var seed = SeedOwnersOrdersAndLines();
    using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
        nameof(Chained_join_scalar_leaf_projection_with_paging_goes_native_under_NativeOnly));

    var results = db.Owners
        .Join(db.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
        .Join(db.OrderLines, e => e.r.Id, l => l.OrderId, (e, l) => new
        {
            OwnerName = e.o.Name,
            OrderTotal = e.r.Total,
            LineSku = l.Sku
        })
        .Skip(1).Take(2)
        .ToList();

    Assert.Equal(2, results.Count);
}
```

- [ ] **Step 2: Run it and confirm it fails for the expected reason**

Run:
```bash
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~Chained_join_scalar_leaf_projection_with_paging_goes_native_under_NativeOnly"
```

Expected: FAIL with `NativeTranslationNotSupportedException: Query projects a non-entity result`. Confirm the
stack trace bottoms out at `VisitProjectedQuery`/`ThrowIfNativeOnlyForbidsFallback` (meaning `Route` never
reached `Projection` — the join confirmation itself declined, matching this plan's own root-cause analysis)
rather than some other exception. If it fails for a DIFFERENT reason, stop and re-diagnose via
`superpowers:systematic-debugging` before proceeding — the spec's root-cause section is based on a live
measurement against a specific commit; if the codebase has drifted, re-measure rather than assume the spec is
still accurate.

- [ ] **Step 3: Confirm the SIBLING already-pinned decline test is unaffected**

Run:
```bash
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~Chained_join_scalar_leaf_projection_with_trailing_paging_still_declines_under_NativeOnly"
```

Expected: PASS (still throws) — this sibling test's `Skip`/`Take` are composed in EXACTLY the same LINQ
position as the one you just added; if this ALSO started failing (throwing something unexpected, or
succeeding), that would mean the two tests are not as similar as this plan assumes — stop and read both test
bodies side by side before continuing. (They are expected to currently both decline for the same reason today;
Task 2 is what's expected to separate their outcomes — re-verify this expectation only if this step surprises
you.)

- [ ] **Step 4: Commit**

```bash
git add tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeJoinTests.cs
git commit -m "test: pin chained-join scalar-leaf projection with paging as currently falling back to driver-LINQ"
```

---

### Task 2: Implement the deferred-paging mechanism end to end

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoSelectDefinition.cs`
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs`
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoSelectLowerer.cs`
- Test: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeJoinTests.cs` (flip Task 1's test,
  plus a differential-oracle correctness proof)

**Interfaces:**
- Produces: `MongoSelectDefinition.PostLookupPagingOps` (public getter) and
  `MongoSelectDefinition.DeferPipelineOpsPastConfirmedJoin()` (internal). `IsSingleEligibleNativeJoinScope`
  calls the latter instead of returning `false` when every join's own 1:1-safety check fails and
  `Select.Cardinality?.Reducer` is null. `MongoSelectLowerer.Lower` emits the new list right after
  `PostJoinOps`.

This task is one unit: the three production changes have no independently-observable effect until all three
land together (the new list does nothing until something writes to it; the gate change does nothing
observable until the lowerer emits what it moved) — implement and test them as one commit, not three.

- [ ] **Step 1: Add the new list and move method to `MongoSelectDefinition`**

Find the existing `PostJoinOps` block (search for `// ── Post-join ops`) and add a new block immediately
after it, before the `// ── Post-group ops` block:

```csharp
// ── Post-lookup paging ops (paging hoisted ahead of a confirmed join by EF Core) ────
// A list populated exactly once, by DeferPipelineOpsPastConfirmedJoin, when
// MongoQueryableMethodTranslatingExpressionVisitor.IsSingleEligibleNativeJoinScope finds Select.HasPaging
// true at confirmation time (EF Core hoists a trailing Skip/Take — and anything composed before it in the
// same batch — ahead of a join's pending result selector) and the join is NOT already in the narrow
// pre-lookup-safe (left-outer, non-collection navigation) set. See
// docs/superpowers/specs/2026-09-22-native-post-join-paging-design.md for why moving the WHOLE PipelineOps
// snapshot (not just the paging ops) here is safe.
private readonly List<MongoSelectOp> _postLookupPagingOps = [];

/// <summary>
/// The ordered filter/sort/page operations moved out of <see cref="PipelineOps"/> by
/// <see cref="DeferPipelineOpsPastConfirmedJoin"/>. The lowerer emits these verbatim immediately after the
/// confirmed join's own <c>$lookup</c>/<c>$unwind</c> block(s), before any <c>$project</c>. Empty for every
/// query that never took this path.
/// </summary>
public IReadOnlyList<MongoSelectOp> PostLookupPagingOps => _postLookupPagingOps;

/// <summary>
/// Moves the ENTIRE current <see cref="PipelineOps"/> snapshot into <see cref="PostLookupPagingOps"/>, in
/// order, and clears <see cref="PipelineOps"/>. Called exactly once, by
/// <c>IsSingleEligibleNativeJoinScope</c>, at the moment it decides a join with recorded pre-confirmation
/// paging is eligible only because the paging is being relocated.
/// </summary>
internal void DeferPipelineOpsPastConfirmedJoin()
{
    _postLookupPagingOps.AddRange(_pipelineOps);
    _pipelineOps.Clear();
}
```

- [ ] **Step 2: Split the `IsSingleEligibleNativeJoinScope` paging/reducer check**

In `MongoQueryableMethodTranslatingExpressionVisitor.cs`, find the exact block (search for
`Select.HasPaging || mongoQueryExpression.Select.Cardinality?.Reducer != null`):

```csharp
if (mongoQueryExpression.Select.HasPaging || mongoQueryExpression.Select.Cardinality?.Reducer != null)
{
    foreach (var level in mongoQueryExpression.Joins)
    {
        if (level.Lookup is not { } levelLookup
            || !(level.IsLeftOuter && levelLookup.Navigation is { IsCollection: false }))
        {
            return false;
        }
    }
}
```

Replace it with:

```csharp
if (mongoQueryExpression.Select.Cardinality?.Reducer != null)
{
    // Reducer case (First/FirstOrDefault/Single/...): unchanged — out of scope for the native-post-join-
    // paging plan. See that plan's own spec for why this branch is NOT folded into the deferred-ops
    // mechanism below.
    foreach (var level in mongoQueryExpression.Joins)
    {
        if (level.Lookup is not { } levelLookup
            || !(level.IsLeftOuter && levelLookup.Navigation is { IsCollection: false }))
        {
            return false;
        }
    }
}
else if (mongoQueryExpression.Select.HasPaging)
{
    var everyJoinPreLookupSafe = true;
    foreach (var level in mongoQueryExpression.Joins)
    {
        if (level.Lookup is not { } levelLookup
            || !(level.IsLeftOuter && levelLookup.Navigation is { IsCollection: false }))
        {
            everyJoinPreLookupSafe = false;
            break;
        }
    }

    // Every join already safe for the existing before-$lookup placement (native-post-join-paging plan):
    // keep it there, unchanged — preserves today's MQL baseline for Projection_take_projection/
    // Projection_skip_projection/Projection_skip_take_projection exactly. Otherwise, defer the whole
    // recorded PipelineOps snapshot to run after $lookup/$unwind instead of declining the join outright.
    if (!everyJoinPreLookupSafe)
    {
        mongoQueryExpression.Select.DeferPipelineOpsPastConfirmedJoin();
    }
}
```

Also update this method's own doc comment (the paragraph discussing "PAGING/REDUCING RECORDED BEFORE THIS
ARM CONFIRMS" — search for that heading) to note that the paging branch NOW defers rather than always
declining; leave the reducer discussion in that comment unchanged.

- [ ] **Step 3: Emit the new list in the lowerer**

In `MongoSelectLowerer.cs`, find the existing step-2b block (search for
`AppendSelectOpStages(select.PostJoinOps, stages, sortFields);`) and add the new call immediately after it,
still inside the `if (select.SetOperation == null)` block:

```csharp
AppendLookupStages(query, stages);
AppendSelectOpStages(select.PostJoinOps, stages, sortFields);
AppendSelectOpStages(select.PostLookupPagingOps, stages, sortFields);
```

- [ ] **Step 4: Build**

Run: `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"`
Expected: builds clean.

- [ ] **Step 5: Run Task 1's test — expected green now**

Run:
```bash
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~Chained_join_scalar_leaf_projection_with_paging_goes_native_under_NativeOnly"
```
Expected: PASS.

- [ ] **Step 6: Add the crux correctness proof — paging must apply to the JOINED row count, not the outer one**

This is the test that actually justifies the fix (not just "doesn't throw", but "returns the right rows").
Add to `NativeJoinTests.cs`, reusing the existing `SeedOwnersAndOrdersWithUnmatchedRows()` seed helper if its
row ORDER already produces a discriminating result, or — following the EXACT precedent already established
in this file for the analogous reducer hazard (search for `SeedOrderlessOwnerFirst` and read its own comment:
*"An Owner with NO Orders inserted FIRST... it makes an un-gated reducer's pre-$lookup {$limit: 1} keep the
order-less owner, which the 1:N $unwind then drops entirely, turning a wrong-row hazard into an observable
empty result"*) — add a new seed helper with the SAME technique for the paging case: an Order with a
DANGLING (unmatched) `OwnerId` inserted FIRST, followed by a matched Order, so that:
- **Broken (pre-fix) behavior** would emit `$limit` before `$lookup`, keeping the dangling order as the
  page's row, which the inner join then drops entirely → an empty (or wrong) result.
- **Fixed behavior** emits `$lookup`/`$unwind` first (dropping the dangling order, which has no match), THEN
  pages the correctly-joined result → the expected row.

```csharp
[Fact]
public void Paging_after_a_confirmed_join_applies_to_the_joined_row_count_not_the_outer_one_under_NativeOnly()
{
    // Native-post-join-paging plan. The crux correctness proof: an Order with a DANGLING OwnerId inserted
    // FIRST, a matched Order second. A plain (non-left-outer) Join over these drops the dangling order
    // entirely — exactly ONE joined row survives. Skip(0).Take(1) must return that ONE surviving row. If
    // paging were (incorrectly) still applied BEFORE the $lookup — the bug this plan fixes — {$limit: 1}
    // would keep the dangling order (first in insertion order), which the join then drops, returning ZERO
    // rows instead of one. Mirrors the exact technique SeedOrderlessOwnerFirst already established in this
    // file for the analogous reducer hazard (First_after_a_confirmed_join_declines_cleanly_under_NativeOnly's
    // sibling correctness test) — same idea, applied to paging instead of a reducer's $limit.
    var seed = SeedDanglingOrderFirstThenMatched(); // add this helper near SeedOrderlessOwnerFirst

    using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
        nameof(Paging_after_a_confirmed_join_applies_to_the_joined_row_count_not_the_outer_one_under_NativeOnly));

    var result = db.Orders
        .Join(db.Owners, r => r.OwnerId, o => o.Id, (r, o) => new { r.Total, o.Name })
        .Skip(0).Take(1)
        .ToList();

    Assert.Single(result);
    // The dangling order's Total must NOT be the one returned — it has no matching owner and must have
    // been dropped by the join before paging ever ran.
    Assert.NotEqual(seed.Orders[0].Total, result[0].Total);
}
```

Write `SeedDanglingOrderFirstThenMatched()` following the exact style of the neighboring `SeedOrderlessOwnerFirst`
(a `Seed` with one `Order` whose `OwnerId` is a freshly-generated `ObjectId` matching no `Owner`, inserted at
index 0, and one genuinely-matched `Order`/`Owner` pair after it).

- [ ] **Step 7: Run it and confirm it passes**

Run:
```bash
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~Paging_after_a_confirmed_join_applies_to_the_joined_row_count_not_the_outer_one_under_NativeOnly"
```
Expected: PASS. If it FAILS, do not weaken the assertion — this is exactly the wrong-data hazard the whole
plan exists to close; stop and re-diagnose via `superpowers:systematic-debugging`.

- [ ] **Step 8: Confirm the untouched 1:1-safe carve-out's MQL baselines are unchanged**

Run:
```bash
dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~Projection_take_projection|FullyQualifiedName~Projection_skip_projection|FullyQualifiedName~Projection_skip_take_projection"
```
Expected: PASS, with the EXACT same MQL baseline as before this task (these tests assert the specific pipeline
shape via `AssertMql` — do NOT run `EF_TEST_REWRITE_BASELINES=1` here; if any of these three fails, it means
`everyJoinPreLookupSafe` is being computed differently than before for this shape, which should not happen —
Step 2 preserved the exact same loop/conjuncts, only wrapping what happens on failure).

- [ ] **Step 9: Confirm the sibling still-declining test remains correctly declining**

Run:
```bash
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~Chained_join_scalar_leaf_projection_with_trailing_paging_still_declines_under_NativeOnly"
```
Expected: PASS (still throws) — per the Global Constraints, this plan does not touch
`NativeSlotPopulator`'s post-CONFIRMATION guard, only paging hoisted BEFORE confirmation. If this test starts
passing (the query now succeeds) instead of throwing, that means this plan's fix reached further than
intended — stop and read both this test's LINQ shape and Task 1's new test's shape side by side to understand
why they diverged, before deciding whether that's a welcome bonus or a sign the scope leaked.

- [ ] **Step 10: Run the full join/slot-populator/projection-binder suites for a broader regression check**

Run:
```bash
dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build --filter "FullyQualifiedName~NativeJoin"
```
Expected: PASS, zero regressions.

- [ ] **Step 11: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoSelectDefinition.cs \
        src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs \
        src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoSelectLowerer.cs \
        tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeJoinTests.cs
git commit -m "feat: defer paging hoisted ahead of a confirmed join instead of declining it"
```

---

### Task 3: Depth-1 whole-entity-leaf coverage

**Files:**
- Test: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeJoinTests.cs`

**Interfaces:**
- Consumes: everything from Task 2. The fix is leaf-shape-agnostic (it lives entirely in the confirming
  gate and the lowerer, decoupled from which Select-side arm binds the projection) — this task proves that
  explicitly for the ORIGINAL, simplest case the native-join work started with: a single `Join`, a
  whole-entity leaf, then paging.

- [ ] **Step 1: Write the test**

```csharp
[Fact]
public void Depth1_whole_entity_leaf_join_with_paging_goes_native_under_NativeOnly()
{
    // Native-post-join-paging plan. The SIMPLEST case this fix covers: a single, plain (non-left-outer)
    // Join, a bare whole-entity leaf Select (`x => x.r`, no wrapping new{}), then Skip/Take — previously
    // declined by IsSingleEligibleNativeJoinScope's HasPaging conjunct exactly like the chain-scalar case.
    var seed = SeedOwnersOrdersAndLines();
    using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
        nameof(Depth1_whole_entity_leaf_join_with_paging_goes_native_under_NativeOnly));

    var results = db.Owners
        .Join(db.Orders, o => o.Id, r => r.OwnerId, (o, r) => r)
        .Skip(1).Take(1)
        .ToList();

    Assert.Single(results);
}
```

- [ ] **Step 2: Run it**

Run:
```bash
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~Depth1_whole_entity_leaf_join_with_paging_goes_native_under_NativeOnly"
```
Expected: PASS. If it still declines, that means the fix's leaf-shape-agnosticism assumption (stated in this
task's own header) is wrong — stop and re-read `IsSingleEligibleNativeJoinScope`'s call sites (both the bare
whole-entity-leaf arm and `NativeJoinScopeProjectionBinder.TryBindProjection`) to find what differs for this
shape before changing anything.

- [ ] **Step 3: Commit**

```bash
git add tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeJoinTests.cs
git commit -m "test: pin depth-1 whole-entity-leaf join with paging as newly native"
```

---

### Task 4: Rebaseline the motivating spec test, full multi-EF regression, and documentation

**Files:**
- Modify: `tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/NorthwindMiscellaneousQueryMongoTest.cs`
- Modify: `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md`

**Interfaces:**
- Consumes: everything from Tasks 2-3.
- Produces: `Join_Customers_Orders_Orders_Skip_Take_Same_Properties` passes under default `Native` mode with
  an updated `AssertMql(...)` baseline, AND under `MONGODB_EF_NATIVE_ONLY=1` (proving it actually goes
  native).

- [ ] **Step 1: Run the target spec test under `NativeOnly` first, standalone**

Run:
```bash
MONGODB_EF_NATIVE_ONLY=1 dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~NorthwindMiscellaneousQueryMongoTest.Join_Customers_Orders_Orders_Skip_Take_Same_Properties"
```
Expected: PASS (both `async: true/false`) — this is the one true confirmation that BOTH this plan's and the
native-chained-join-scalar-projection plan's combined work closes out the original motivating gap.

- [ ] **Step 2: Rebaseline under default `Native` mode**

Run:
```bash
EF_TEST_REWRITE_BASELINES=1 dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~NorthwindMiscellaneousQueryMongoTest.Join_Customers_Orders_Orders_Skip_Take_Same_Properties"
```

Then `git diff` the rewritten `AssertMql(...)`. Per the spec's Data Flow section, expect: both
`$lookup`/`$unwind` pairs, THEN the `$sort`/`$skip`/`$limit` (the deferred ops), THEN the final `$project`
with the five scalar fields — NOT the old fallback shape (both lookups, then `$project`, with no native
`$sort`/`$skip`/`$limit` stages at all — the fallback pushes those into the driver-LINQ bridge instead). If
the diff shows anything else, stop and re-check Task 2's wiring before accepting the rewrite.

- [ ] **Step 3: Rebuild and re-run without the rewrite var to confirm it's genuinely green**

Run: `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"` then
```bash
dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~NorthwindMiscellaneousQueryMongoTest.Join_Customers_Orders_Orders_Skip_Take_Same_Properties"
```
Expected: PASS.

- [ ] **Step 4: Full multi-EF regression**

Run, for EF10 first:
```bash
MONGODB_EF_NATIVE_ONLY=1 dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build
dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build
```
Expected: the target test flips `Failed -> Passed` in the `NativeOnly` run; zero `Passed -> Failed` anywhere
else (the only pre-existing, unrelated failures should match whatever this branch's current known set is —
check via `git log`/the branch's own recent history rather than assuming a specific count, since other
in-flight work on this branch may have changed it since this plan was written).

Then repeat the full-suite run for EF8 and EF9:
```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF8"
dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF8" --no-build
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF9"
dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF9" --no-build
```
Expected: zero regressions, no `#if` divergence needed (confirm rather than assume).

If anything else flips to `Failed`, bisect by reverting Task 3 first, then Task 2.

- [ ] **Step 5: Update `Query/AGENTS.md`**

Add ONE or TWO sentences (not a new section) near wherever the chained-join-scalar-projection capability note
already lives (added by the prior plan), stating that `Skip`/`Take` hoisted ahead of a join's confirming
`Select` (the common case — EF Core hoists it there) now also goes native when the join isn't already in the
existing left-outer-reference-navigation "safe to page before `$lookup`" set — the whole recorded
`PipelineOps` snapshot is deferred to run after the join instead. Note that a reducer (`First`/etc.) in the
same position, and a slot operator GENUINELY composed after the confirming Select (not hoisted), still fall
back. Follow the file's own stated "no per-feature history" policy — keep it to the durable rule, not a
narrative; do not add a plan name, spec path, or test name inline (per the lesson already learned and
recorded in this same file by the prior plan's own final-review fix).

- [ ] **Step 6: Commit**

```bash
git add tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/NorthwindMiscellaneousQueryMongoTest.cs \
        src/MongoDB.EntityFrameworkCore/Query/AGENTS.md
git commit -m "test: rebaseline Join_Customers_Orders_Orders_Skip_Take_Same_Properties; docs: record native post-join paging"
```

---

## Explicitly out of scope (do not implement in this plan)

- A reducer (`First`/`FirstOrDefault`/`Single`/`SingleOrDefault`) composed where paging would otherwise be
  deferred — `IsSingleEligibleNativeJoinScope`'s reducer branch keeps declining unchanged.
- A `Where`/`OrderBy`/`ThenBy`/`Skip`/`Take` composed AFTER the confirming `Select` runs (a genuine
  post-confirmation operator, not one hoisted ahead of it) — `NativeSlotPopulator`'s `HasConfirmedJoinLookup`
  guard is untouched.
- Proving a specific join's cardinality statically (e.g. a unique-FK check) to keep paging in its original,
  more efficient pre-`$lookup` position for a wider set of joins — irrelevant once paging is simply moved to
  run after the join.
