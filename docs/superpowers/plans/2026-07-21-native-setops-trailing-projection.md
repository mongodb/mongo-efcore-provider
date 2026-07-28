# Native Set-Op Trailing Projection (Slice C2) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make a terminal anonymous/DTO member-access `Select` composed after a whole-entity set op (`Union(A,B).Select(x => new {…})`, all four set ops) push down a native trailing `$project` instead of falling back to driver-LINQ (Union/Concat) or hard-failing (Intersect/Except).

**Architecture:** Approach A — reuse everything. The lowerer already emits a `$project` after the set-op stage once `Projection` is populated (slice B's set-op-block fall-through reaches the `Projection` block), and the SP3 projection shaper is agnostic to the preceding set-op stage. So the change is two coupled edits: (1) fold `&& Projection.Count == 0` into `MongoSelectDefinition.IsSetOpTerminalOnly` (this both enables the relaxation — `Projection` is empty at push-down time — and closes the composition-after-projection seam once it is populated); (2) relax the `TranslateSelect` non-grouped-projection post-terminal guard with `&& !IsSetOpTerminalOnly`. No lowerer/shaper logic change.

**Tech Stack:** C# / .NET (net8.0 for EF8/EF9, net10.0 for EF10), EF Core provider internals, MongoDB C# driver, xUnit (plain `Assert.*`, no FluentAssertions).

## Global Constraints

- `<Nullable>enable</Nullable>` on `src/` — annotate new members accordingly.
- Preserve file BOMs on every edited file (the two `src/` files and `NativeSetOpsTests.cs` have no BOM today — match each file's existing header exactly; do not add or remove a BOM).
- Multi-EF: behaviorally identical across EF8/EF9/EF10 — **no `#if` guards** introduced. Build/test per version via `-c "Debug EF8|EF9|EF10"`.
- Tests are **plain xUnit `Assert.*`** — FluentAssertions is not referenced. Unit tests need no DB; functional tests hit a real MongoDB — leave `MONGODB_URI` and `ATLAS_URI` unset so TestContainers boots an isolated `mongodb/mongodb-atlas-local` container (Docker required; first run pays a one-time image pull).
- `<NoWarn>EF1001</NoWarn>` — consuming EF internal APIs is expected.
- Stacked-PR workflow: this branch is stacked off the native rolling tip `1e3adc9`; the whole slice lands as **one squashed commit** at the end (Task 3), with a `-presquash` backup branch kept until merge.
- The only reliable "goes native" signal is `MongoQueryMode.NativeOnly` succeeding; a fallback would throw. Prove native this way (Union/Concat additionally get a `Native == DriverLinq` parity oracle; Intersect/Except have no oracle — assert a literal expected result set).

**Build once before starting:**
```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"
```

---

### Task 1: Enable the trailing projection + close the composition seam

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoSelectDefinition.cs` (the `IsSetOpTerminalOnly` property, ~line 262)
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs` (the `TranslateSelect` non-grouped-projection post-terminal guard, ~line 253)
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoSelectLowerer.cs` (comment only, the slice-B set-op-block note ~lines 90-96)
- Test (unit): `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoSelectDefinitionTrailingOpsTests.cs`
- Test (functional): `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeSetOpsTests.cs`

**Interfaces:**
- Consumes (already in the tree): `MongoSelectDefinition.IsSetOpTerminalOnly`, `.Projection` (`IReadOnlyList<MongoProjection>`), `.AddProjection(MongoProjection)`; `MongoProjection` is `readonly record struct MongoProjection(string Alias, MongoExpression Expression)`; `NativeProjectionBinder.TryPopulateNativeProjection(MongoQueryExpression, LambdaExpression)`; the functional-test helpers in `NativeSetOpsTests` (`SeedCollection(name)`, `Make(collection, mode)`, `Item` with `int Value`/`string Name`, `SeedItems()` = Value 1..5).
- Produces: after this task, a supported trailing member-projection after a set-op-only terminal goes native; a projection populated on a set-op query makes `IsSetOpTerminalOnly` false (closing post-projection composition).

- [ ] **Step 1: Write the failing unit test (the seam-closure IR change)**

Append to `MongoSelectDefinitionTrailingOpsTests.cs` (after the existing `IsSetOpTerminalOnly_false_when_also_grouped` test, before the class close). It needs `using MongoDB.EntityFrameworkCore.Query.Expressions;` — already present in that file.

```csharp
    [Fact]
    public void IsSetOpTerminalOnly_false_when_a_projection_is_populated()
    {
        var select = WithSetOp();
        // A trailing projection was pushed down: the set op is no longer the ONLY thing done, so a
        // subsequent operator must NOT be treated as set-op-terminal-only (it would resolve against the
        // entity type and mis-place / mis-bind — the composition-after-projection seam this closes).
        select.AddProjection(new MongoProjection("N", new MongoConstantExpression(0, forSerialization: null)));
        Assert.False(select.IsSetOpTerminalOnly);
    }
```

- [ ] **Step 2: Run the unit test to verify it fails**

Run:
```bash
dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj \
  -c "Debug EF10" --filter "FullyQualifiedName~MongoSelectDefinitionTrailingOpsTests.IsSetOpTerminalOnly_false_when_a_projection_is_populated"
```
Expected: FAIL — current `IsSetOpTerminalOnly` ignores `Projection`, so it returns `true` and the assertion fails.

- [ ] **Step 3: Fold `Projection.Count == 0` into `IsSetOpTerminalOnly`**

In `MongoSelectDefinition.cs`, change the property body (~line 262-263) from:
```csharp
    internal bool IsSetOpTerminalOnly
        => IsSetOp && !IsGroupBy && !IsDistinct && Grouping == null && UnwindSource == null;
```
to:
```csharp
    internal bool IsSetOpTerminalOnly
        => IsSetOp && !IsGroupBy && !IsDistinct && Grouping == null && UnwindSource == null && Projection.Count == 0;
```
And extend the XML-doc summary (the block ~line 253-261) — append after the existing "…GroupBy/Distinct/SelectMany terminal." sentence:
```
    /// The <c>Projection.Count == 0</c> conjunct (EF-347 slice C2) makes this read as "a set op is the ONLY
    /// thing done so far": it stays true while a trailing projection is being pushed down (Projection is still
    /// empty at that moment, so TranslateSelect admits the projection), then flips to false once the projection
    /// is populated — so any operator composed AFTER the trailing projection falls back rather than resolving
    /// against the entity type (the composition-after-projection seam).
```

- [ ] **Step 4: Run the unit test to verify it passes**

Run:
```bash
dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj \
  -c "Debug EF10" --filter "FullyQualifiedName~MongoSelectDefinitionTrailingOpsTests"
```
Expected: PASS — the new test plus the 3 pre-existing `IsSetOpTerminalOnly_*` tests (the whole `IsSetOpTerminalOnly_true_for_a_plain_set_op` still passes: `WithSetOp()` populates no projection).

- [ ] **Step 5: Write the failing functional test (the native trailing projection)**

Append to `NativeSetOpsTests.cs` (near the other post-composition tests). This asserts the feature works end-to-end and fails today because `TranslateSelect`'s guard still marks it non-native.

```csharp
    // EF-347 slice C2: a trailing anonymous/DTO member-access Select after a whole-entity set op now goes
    // native (a $project after the set-op stage). Union has a driver-LINQ baseline → assert Native==DriverLinq
    // parity; NativeOnly succeeding proves the native path was taken.
    [Fact]
    public void Select_after_union_goes_native()
    {
        var collection = SeedCollection(nameof(Select_after_union_goes_native));
        using var nativeOnlyDb = Make(collection, MongoQueryMode.NativeOnly);
        using var driverDb = Make(collection, MongoQueryMode.DriverLinq);

        List<int> Run(SingleEntityDbContext<Item> db) =>
            db.Entities.Where(i => i.Value <= 3).Union(db.Entities.Where(i => i.Value >= 3))
                .Select(i => new { N = i.Value })
                .ToList().Select(x => x.N).OrderBy(v => v).ToList();

        var native = Run(nativeOnlyDb); // NativeOnly succeeding proves native
        Assert.Equal([1, 2, 3, 4, 5], native); // {1,2,3} U {3,4,5} deduped, projected to Value
        Assert.Equal(Run(driverDb), native);
    }

    // No driver-LINQ oracle for Intersect/Except → assert the literal expected set under NativeOnly.
    [Fact]
    public void Select_after_intersect_goes_native()
    {
        var collection = SeedCollection(nameof(Select_after_intersect_goes_native));
        using var db = Make(collection, MongoQueryMode.NativeOnly);
        // {1,2,3} Intersect {3,4,5} = {3}; projected to Value.
        var result = db.Entities.Where(i => i.Value <= 3).Intersect(db.Entities.Where(i => i.Value >= 3))
            .Select(i => new { N = i.Value })
            .ToList();
        Assert.Equal([3], result.Select(x => x.N).OrderBy(v => v));
    }
```

- [ ] **Step 6: Run the functional tests to verify they fail**

Run (leave `MONGODB_URI`/`ATLAS_URI` unset):
```bash
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --filter "FullyQualifiedName~NativeSetOpsTests.Select_after_union_goes_native|FullyQualifiedName~NativeSetOpsTests.Select_after_intersect_goes_native"
```
Expected: FAIL — both throw `NativeTranslationNotSupportedException` under `NativeOnly` (the `TranslateSelect` guard still marks the trailing projection non-native).

- [ ] **Step 7: Relax the `TranslateSelect` guard**

In `MongoQueryableMethodTranslatingExpressionVisitor.cs`, in the non-grouped projection branch (~line 253), change:
```csharp
            if (mongoQueryExpression.Select.HasTerminalOperator)
            {
                mongoQueryExpression.Select.MarkNotNativelyRepresentable();
            }
```
to:
```csharp
            // EF-347 slice C2: a set-op-ONLY terminal is EXEMPT — a trailing anonymous/DTO member-access Select
            // after a whole-entity set op pushes down a $project (emitted after the set-op stage by the lowerer's
            // Projection block, via the slice-B fall-through). IsSetOpTerminalOnly requires Projection.Count == 0,
            // so once this projection is populated a SECOND projection (or any post-projection operator) is no
            // longer set-op-terminal-only and correctly falls back here. A GroupBy/Distinct/SelectMany terminal
            // (IsSetOpTerminalOnly false) still marks non-native, exactly as before.
            if (mongoQueryExpression.Select.HasTerminalOperator && !mongoQueryExpression.Select.IsSetOpTerminalOnly)
            {
                mongoQueryExpression.Select.MarkNotNativelyRepresentable();
            }
```
Do **not** touch the `IsSingleLevelCollectionIncludeSelector && HasTerminalOperator` guard just above it (the hoisted-shared-Include case stays a graceful fallback).

- [ ] **Step 8: Update the stale lowerer comment (comment only)**

In `MongoSelectLowerer.cs`, the slice-B note in the set-op block (~lines 90-96) currently says the intermediate blocks are empty and "a future slice composing one of those after a set op MUST revisit this precedence." Replace that sentence's tail so it reads (keep the surrounding lines):
```csharp
            // EF-347 slice B: post-set-op composition. Trailing $match/$sort/$skip/$limit emit AFTER the
            // set-op stage (they operate on the COMBINED result), then fall through to the Projection block
            // (EF-347 slice C2: a trailing anonymous/DTO Select after a set op populates Select.Projection,
            // emitted here as a $project after the set-op stage and TrailingOps) and the Cardinality block
            // (post-set-op aggregate/reducer). UnwindSource/Grouping stay empty for a set-op query and their
            // blocks are skipped.
```
(No logic change — the `Projection` block at stage 6 already emits the `$project`.)

- [ ] **Step 9: Run the functional tests to verify they pass**

Run:
```bash
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --filter "FullyQualifiedName~NativeSetOpsTests"
```
Expected: PASS — the two new native tests pass, and the whole `NativeSetOpsTests` class stays green **except** the two slice-B deferred-projection tests that now behave oppositely (`Select_after_union_falls_back_gracefully` and `Select_after_intersect_hard_fails_in_every_mode`) — those are flipped in Task 2. If only those two fail (because the shape now goes native), that is expected; proceed. If anything else fails, stop and investigate.

- [ ] **Step 10: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoSelectDefinition.cs \
        src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs \
        src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoSelectLowerer.cs \
        tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoSelectDefinitionTrailingOpsTests.cs \
        tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeSetOpsTests.cs
git commit -m "EF-347 slice C2: trailing projection after a set op goes native (+ Projection.Count seam closure)"
```

---

### Task 2: Lock the seam + fallback split + spec re-baseline + docs

**Files:**
- Test: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeSetOpsTests.cs`
- Modify (spec overrides, as needed): `tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/NorthwindSetOperationsQueryMongoTest.cs`
- Modify: `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md`

**Interfaces:**
- Consumes: everything from Task 1.
- Produces: regression coverage that the composition-after-projection seam is closed and the fallback split holds; up-to-date docs.

- [ ] **Step 1: Flip the two slice-B deferred-projection tests (now native)**

In `NativeSetOpsTests.cs`, find `Select_after_union_falls_back_gracefully` and replace the whole method with a parity-native version (Union has an oracle):
```csharp
    // EF-347 slice C2: a trailing projection after Union now goes native (was a graceful fallback in slice B).
    [Fact]
    public void Select_after_union_goes_native_parity()
    {
        var collection = SeedCollection(nameof(Select_after_union_goes_native_parity));
        using var nativeOnlyDb = Make(collection, MongoQueryMode.NativeOnly);
        using var driverDb = Make(collection, MongoQueryMode.DriverLinq);

        List<string> Run(SingleEntityDbContext<Item> db) =>
            db.Entities.Where(i => i.Value <= 3).Union(db.Entities.Where(i => i.Value >= 3))
                .Select(i => new { i.Name })
                .ToList().Select(x => x.Name).OrderBy(n => n).ToList();

        var native = Run(nativeOnlyDb);
        Assert.Equal(5, native.Count); // 5 distinct entities → 5 projected rows
        Assert.Equal(Run(driverDb), native);
    }
```
Then find `Select_after_intersect_hard_fails_in_every_mode` and replace the whole method with a result-set native version (no oracle):
```csharp
    // EF-347 slice C2: a trailing projection after Intersect now goes native (was a hard-fail in slice B).
    [Fact]
    public void Select_after_intersect_goes_native_result_set()
    {
        var collection = SeedCollection(nameof(Select_after_intersect_goes_native_result_set));
        using var db = Make(collection, MongoQueryMode.NativeOnly);
        // {1,2,3} Intersect {3,4,5} = {3}; projected to Name.
        var result = db.Entities.Where(i => i.Value <= 3).Intersect(db.Entities.Where(i => i.Value >= 3))
            .Select(i => new { i.Name })
            .ToList();
        var single = Assert.Single(result);
        Assert.Equal("Three", single.Name);
    }
```

- [ ] **Step 2: Add the compose + Union dedup-then-project + wrong-scope tests**

Append these to `NativeSetOpsTests.cs`:
```csharp
    // Slice-B trailing Where composes with a slice-C2 trailing projection: filter the combined result, then
    // project. $match (trailing) lands before $project (both after the set-op stage).
    [Fact]
    public void Where_then_Select_after_union_goes_native()
    {
        var collection = SeedCollection(nameof(Where_then_Select_after_union_goes_native));
        using var nativeOnlyDb = Make(collection, MongoQueryMode.NativeOnly);
        using var driverDb = Make(collection, MongoQueryMode.DriverLinq);

        List<int> Run(SingleEntityDbContext<Item> db) =>
            db.Entities.Where(i => i.Value <= 3).Union(db.Entities.Where(i => i.Value >= 3))
                .Where(i => i.Value >= 2).Select(i => new { N = i.Value })
                .ToList().Select(x => x.N).OrderBy(v => v).ToList();

        var native = Run(nativeOnlyDb);
        Assert.Equal([2, 3, 4, 5], native);
        Assert.Equal(Run(driverDb), native);
    }

    // Union dedups WHOLE ENTITIES before the projection, so two distinct entities that project to the same
    // value both survive (a duplicate projected value) — matching BCL Union(...).Select(...).
    [Fact]
    public void Union_dedups_entities_then_projects_keeping_duplicate_projected_values()
    {
        var collection = SeedCollection(nameof(Union_dedups_entities_then_projects_keeping_duplicate_projected_values));
        using var nativeOnlyDb = Make(collection, MongoQueryMode.NativeOnly);
        using var driverDb = Make(collection, MongoQueryMode.DriverLinq);

        // Both operands together (deduped) = the 5 distinct entities {1,2,3,4,5}; projecting a constant maps
        // all 5 to the same value, so 5 rows survive (Select does not dedup).
        List<int> Run(SingleEntityDbContext<Item> db) =>
            db.Entities.Where(i => i.Value <= 3).Union(db.Entities.Where(i => i.Value >= 3))
                .Select(i => new { K = 1 })
                .ToList().Select(x => x.K).ToList();

        var native = Run(nativeOnlyDb);
        Assert.Equal(5, native.Count);
        Assert.All(native, k => Assert.Equal(1, k));
        Assert.Equal(Run(driverDb).Count, native.Count);
    }
```

- [ ] **Step 3: Add the composition-after-projection seam tests**

These prove the `Projection.Count == 0` closure. Append to `NativeSetOpsTests.cs`:
```csharp
    // EF-347 slice C2 seam: an operator AFTER the trailing projection is NOT native (IsSetOpTerminalOnly is
    // false once Projection is populated). Union falls back gracefully (correct under Native, throws under
    // NativeOnly); Intersect hard-fails in every mode (no driver-LINQ oracle).
    [Fact]
    public void Where_after_trailing_projection_on_union_falls_back_gracefully()
    {
        var collection = SeedCollection(nameof(Where_after_trailing_projection_on_union_falls_back_gracefully));
        using (var nativeOnlyDb = Make(collection, MongoQueryMode.NativeOnly))
        {
            Assert.Throws<NativeTranslationNotSupportedException>(() =>
                nativeOnlyDb.Entities.Where(i => i.Value <= 3).Union(nativeOnlyDb.Entities.Where(i => i.Value >= 3))
                    .Select(i => new { N = i.Value }).Where(x => x.N >= 2).ToList());
        }
        using var nativeDb = Make(collection, MongoQueryMode.Native);
        var result = nativeDb.Entities.Where(i => i.Value <= 3).Union(nativeDb.Entities.Where(i => i.Value >= 3))
            .Select(i => new { N = i.Value }).Where(x => x.N >= 2).ToList();
        Assert.Equal([2, 3, 4, 5], result.Select(x => x.N).OrderBy(v => v));
    }

    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.DriverLinq)]
    [InlineData(MongoQueryMode.NativeOnly)]
    public void Where_after_trailing_projection_on_intersect_hard_fails_in_every_mode(MongoQueryMode mode)
    {
        var collection = SeedCollection(nameof(Where_after_trailing_projection_on_intersect_hard_fails_in_every_mode) + mode);
        using var db = Make(collection, mode);
        Assert.ThrowsAny<Exception>(() =>
            db.Entities.Where(i => i.Value <= 3).Intersect(db.Entities.Where(i => i.Value >= 3))
                .Select(i => new { N = i.Value }).Where(x => x.N >= 2).ToList());
    }

    // A SECOND trailing projection is also post-projection composition → same split.
    [Fact]
    public void Second_projection_after_union_falls_back_gracefully()
    {
        var collection = SeedCollection(nameof(Second_projection_after_union_falls_back_gracefully));
        using (var nativeOnlyDb = Make(collection, MongoQueryMode.NativeOnly))
        {
            Assert.Throws<NativeTranslationNotSupportedException>(() =>
                nativeOnlyDb.Entities.Where(i => i.Value <= 3).Union(nativeOnlyDb.Entities.Where(i => i.Value >= 3))
                    .Select(i => new { N = i.Value }).Select(x => new { M = x.N }).ToList());
        }
        using var nativeDb = Make(collection, MongoQueryMode.Native);
        var result = nativeDb.Entities.Where(i => i.Value <= 3).Union(nativeDb.Entities.Where(i => i.Value >= 3))
            .Select(i => new { N = i.Value }).Select(x => new { M = x.N }).ToList();
        Assert.Equal(5, result.Count);
    }
```

- [ ] **Step 4: Add the deferred-shape fallback tests (bare-scalar + computed leaf)**

```csharp
    // Deferred (unchanged): a BARE-SCALAR trailing projection is never pushed down (SP3 does not push a bare
    // scalar), so it falls back gracefully after Union.
    [Fact]
    public void Bare_scalar_projection_after_union_falls_back_gracefully()
    {
        var collection = SeedCollection(nameof(Bare_scalar_projection_after_union_falls_back_gracefully));
        using (var nativeOnlyDb = Make(collection, MongoQueryMode.NativeOnly))
        {
            Assert.Throws<NativeTranslationNotSupportedException>(() =>
                nativeOnlyDb.Entities.Where(i => i.Value <= 3).Union(nativeOnlyDb.Entities.Where(i => i.Value >= 3))
                    .Select(i => i.Value).ToList());
        }
        using var nativeDb = Make(collection, MongoQueryMode.Native);
        var result = nativeDb.Entities.Where(i => i.Value <= 3).Union(nativeDb.Entities.Where(i => i.Value >= 3))
            .Select(i => i.Value).OrderBy(v => v).ToList();
        Assert.Equal([1, 2, 3, 4, 5], result);
    }

    // Deferred (unchanged): a COMPUTED-leaf trailing projection is not SP3-representable → graceful fallback.
    [Fact]
    public void Computed_leaf_projection_after_union_falls_back_gracefully()
    {
        var collection = SeedCollection(nameof(Computed_leaf_projection_after_union_falls_back_gracefully));
        using (var nativeOnlyDb = Make(collection, MongoQueryMode.NativeOnly))
        {
            Assert.Throws<NativeTranslationNotSupportedException>(() =>
                nativeOnlyDb.Entities.Where(i => i.Value <= 3).Union(nativeOnlyDb.Entities.Where(i => i.Value >= 3))
                    .Select(i => new { Doubled = i.Value * 2 }).ToList());
        }
        using var nativeDb = Make(collection, MongoQueryMode.Native);
        var result = nativeDb.Entities.Where(i => i.Value <= 3).Union(nativeDb.Entities.Where(i => i.Value >= 3))
            .Select(i => new { Doubled = i.Value * 2 }).ToList();
        Assert.Equal(5, result.Count);
    }
```

- [ ] **Step 5: Run the full functional class**

Run:
```bash
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --filter "FullyQualifiedName~NativeSetOpsTests"
```
Expected: PASS — all set-op tests (Task 1's two native tests, the flipped pair, the compose/dedup/seam/deferred tests, and every pre-existing test).

- [ ] **Step 6: Re-baseline the Northwind set-op spec suite**

Some Northwind set-operation tests compose a projection after `Union`/`Concat`/`Intersect`/`Except` and were `AssertTranslationFailed` (or carry a stale `AssertMql`) because it used to fall back. Find and re-baseline the ones that are now native (a set op followed by an in-scope anonymous/DTO member projection):
```bash
grep -n "Union\|Concat\|Intersect\|Except\|Select\|AssertTranslationFailed\|AssertMql" \
  tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/NorthwindSetOperationsQueryMongoTest.cs
```
For each now-native shape, change `AssertTranslationFailed(...)` → `AssertMql(...)` and regenerate:
```bash
EF_TEST_REWRITE_BASELINES=1 dotnet test \
  tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --filter "FullyQualifiedName~NorthwindSetOperationsQueryMongoTest"
```
Then rebuild and re-run **without** the env var to confirm green. `git diff` the spec file and **hand-clean** any unclean auto-rewriter output (raw-string blocks / closing `"""` at column 0 / stray blank lines) to match neighboring overrides. Leave genuinely out-of-scope shapes (projected-**operand** set ops = slice C1, bare-scalar/computed projections, post-projection composition, chained set ops) as `AssertTranslationFailed`. If **zero** tests need flipping, that is an acceptable outcome — record it explicitly in the report (as in slice B).
```bash
dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --filter "FullyQualifiedName~NorthwindSetOperationsQueryMongoTest"
```
Expected: PASS.

- [ ] **Step 7: Update the Query AGENTS.md**

In `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md`, in the set-ops as-built notes (the Union/Concat and Intersect/Except sections):
- State that a **trailing anonymous/DTO member-access `Select` after a whole-entity set op** now goes native (a `$project` after the set-op stage, via the lowerer's slice-B fall-through + the SP3 projection binder/shaper — no lowerer/shaper change).
- Document the seam closure: `IsSetOpTerminalOnly` gained `&& Projection.Count == 0`, which both admits the trailing projection (Projection empty at push-down) and makes **post-projection composition** (a `Where`/aggregate/second `Select` after the trailing projection) fall back (Union/Concat) / hard-fail (Intersect/Except).
- Update the deferred list: **projected-operand** set ops (`Select(...).Union(Select(...))`) are slice C1; **bare-scalar / computed-leaf / entity-ref** trailing projections and **post-projection composition** stay deferred.

- [ ] **Step 8: Commit**

```bash
git add tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeSetOpsTests.cs \
        tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/NorthwindSetOperationsQueryMongoTest.cs \
        src/MongoDB.EntityFrameworkCore/Query/AGENTS.md
git commit -m "EF-347 slice C2: lock the post-projection seam, re-baseline spec suite, update AGENTS.md"
```

---

### Task 3: Full 3-version verification + squash + push (stacked-PR workflow)

**Files:** none (verification + git).

- [ ] **Step 1: Full 3-version build + test**

Invoke the `/test-all` skill (builds + tests EF8/EF9/EF10 in parallel, foreground, per-container isolation, summing all three assembly summaries). Confirm **0 failures** across all three. Do not proceed on any failure — fix in the owning task.

- [ ] **Step 2: NativeOnly spec sweep (net native-coverage increase, zero regressions)**

```bash
MONGODB_EF_NATIVE_ONLY=1 dotnet test \
  tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~NorthwindSetOperations"
```
Expected: the now-native trailing-projection set-op tests pass under `NativeOnly`; the pass count does not drop versus the pre-slice tip (the slice is additive at the gate — the guard relaxation only opens paths, and the `Projection.Count == 0` closure only affects post-projection shapes that were never native). A count of remaining "failures" is expected (out-of-scope fallbacks: projected operands = C1, subquery, chained, computed).

- [ ] **Step 3: Whole-branch review**

Request an opus whole-branch review of the slice (merge-base = the slice-C2 start tip `1e3adc9`). Focus: the composition-after-projection seam (is it provably closed by `Projection.Count == 0`?), that only a set-op-only terminal is relaxed (GroupBy/Distinct/SelectMany/hoisted-Include untouched), the Union-dedup-then-project semantics, and no regression to non-set-op projected queries. Address findings in the owning task, then re-run `/test-all`.

- [ ] **Step 4: Squash to one commit + backup, then push**

Per the stacked-PR workflow:
```bash
git branch EF-347-setops-trailing-projection-presquash    # backup (keep until merge)
git reset --soft 1e3adc9
git commit -m "EF-347: Native set-op trailing projection — Select after a whole-entity set op (all four)"
```
Write a full commit body in the style of `1e3adc9` (mechanism: lowerer fall-through already emits the `$project`, guard relaxation + `Projection.Count == 0` seam closure, no lowerer/shaper logic change; scope = slice C2; deferred = projected operands (C1), bare-scalar/computed, post-projection composition). Verify the squashed tree is byte-identical to the pre-squash tip (`git diff <presquash> HEAD` empty) and that `1e3adc9` is an ancestor (plain FF). Then fetch to confirm `origin/NativeQueryOngoing` is still `1e3adc9`, fast-forward-push, and update the `native-stack-status` memory with the new tip. **Confirm with the user before pushing.**

---

## Self-Review (completed during planning)

**Spec coverage:** `IsSetOpTerminalOnly += Projection.Count == 0` → Task 1 Step 3; `TranslateSelect` guard relaxation → Task 1 Step 7; lowerer comment update → Task 1 Step 8; shaper unchanged (no task needed — verified in spec). Native trailing projection all-four → Task 1 (Union/Intersect) + Task 2 Step 1 (flipped pair covers Union/Intersect; Concat/Except share the identical machinery and are exercised via the shared code path — the seam/compose/dedup tests use Union/Intersect as representatives, matching the existing test file's convention). Seam closure → Task 2 Step 3; fallback split (bare-scalar/computed) → Task 2 Step 4; Union dedup-then-project → Task 2 Step 2; wrong-scope binding → covered by the parity tests (a renamed projected member `N`/`M` bound against the entity type would diverge from the driver-LINQ oracle). Spec re-baseline → Task 2 Step 6; AGENTS.md → Task 2 Step 7; 3-version + NativeOnly + review + squash/push → Task 3.

**Placeholder scan:** no TBD/TODO; every code/test step shows complete code and exact commands.

**Type consistency:** `IsSetOpTerminalOnly` (internal bool), `Projection` (`IReadOnlyList<MongoProjection>`), `AddProjection(MongoProjection)`, `MongoProjection(string Alias, MongoExpression Expression)`, `MongoConstantExpression(value, forSerialization)`, and the `NativeSetOpsTests` helpers (`SeedCollection`, `Make`, `Item.Value`/`.Name`) all match the current tree.
