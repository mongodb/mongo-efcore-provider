# Native Intersect / Except (set-ops slice A) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Translate whole-entity, terminal `Intersect`/`Except` to a native MongoDB aggregation pipeline (source-tagging `$unionWith` + `$group`), instead of hard-failing translation.

**Architecture:** Extend the existing Union/Concat set-op machinery (`MongoSetOperation` on `source1.Select`, `IsSetOp` terminal gate, `Route == WholeEntity`, recursive operand lowering, shared placeholder table). Add two `MongoSetOperationKind` values, a new `MongoSetDifferenceStage`, and a `RenderSetDifference` that emits the source-tagging pipeline. Since Mongo has no intersect/except stage and both operands are always the same collection, we synthesize set intersection/difference via per-operand `$group{_id:"$$ROOT"}` dedup + a `_a`/`_b` source tag + a re-unify `$group{$max}` + a discriminating `$match` + `$replaceRoot`.

**Tech Stack:** C#/.NET (net8.0 for EF8/EF9, net10.0 for EF10), EF Core provider internals, MongoDB C# driver (BSON only — native path bypasses driver LINQ), xUnit (plain `Assert.*`, no FluentAssertions).

## Global Constraints

- Multi-EF targeting via build **configurations**, not TFMs: `Debug|Release EF8`, `Debug|Release EF9`, `Debug|Release EF10`. Build a single version: `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"`.
- `src/` is `<Nullable>enable</Nullable>` — annotate new types. `<NoWarn>EF1001</NoWarn>` (EF internal APIs are intentionally consumed).
- **Preserve file BOMs.** (New `src/` files match sibling files; new test files match their siblings — the existing unit test file has a BOM, most functional files do not.)
- Tests run **serially** (assembly-level `DisableTestParallelization`). Each functional test uses a uniquely-named collection.
- This is **additive**: `Intersect`/`Except` currently hard-fail in every mode; supported shapes now go native, unsupported shapes still hard-fail. Exception type of an unsupported shape is not part of the contract.
- Branch: `EF-347-setops-intersect-except`, already cut off the rolling tip `1c2063c`. Commit per step; do **not** push (the user drives the push).
- **Run suites FOREGROUND** (no `&`, no background), **rebuild before testing**, and when running a solution-level `dotnet test` grep **all** per-assembly `Passed:`/`Failed!` summary lines (a solution run emits three). Do not delegate `/test-all` to a nested agent.

---

### Task 1: Driver-baseline probe (spike — decides the guard-decline design)

**Why first:** The design assumes the driver's LINQ v3 provider **cannot** translate a cross-view `Intersect`/`Except` (matching the historical `null` choice and the reference-`SelectMany` precedent), so an out-of-scope shape must hard-fail (`return null`) rather than fall back gracefully. If that assumption is wrong (the driver *can* translate them), we get a parity oracle and should relax the decline to graceful fallback — a design change requiring the user. Confirm before building.

**Files:**
- Temporarily modify (do NOT commit): `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs:1013,948` (`TranslateIntersect`/`TranslateExcept`).

- [ ] **Step 1: Temporarily route Intersect/Except to graceful fallback**

In `MongoQueryableMethodTranslatingExpressionVisitor.cs`, temporarily change the two overrides so an out-of-scope shape falls back to driver-LINQ instead of returning `null`:

```csharp
protected override ShapedQueryExpression? TranslateIntersect(ShapedQueryExpression source1, ShapedQueryExpression source2)
{
    var mongo1 = (MongoQueryExpression)source1.QueryExpression;
    mongo1.Select.MarkNotNativelyRepresentable();   // TEMP PROBE — force the driver-LINQ fallback
    return source1;
}
```
(Same for `TranslateExcept`.)

- [ ] **Step 2: Add a throwaway probe test and run it**

Add a temporary `[Fact]` to `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeSetOpsTests.cs` that runs a plain whole-entity `Intersect` under `MongoQueryMode.Native` (which now hits the driver-LINQ fallback) and simply observes:

```csharp
[Fact]
public void PROBE_driver_intersect()
{
    var collection = SeedCollection(nameof(PROBE_driver_intersect));
    using var db = Make(collection, MongoQueryMode.Native);
    var ex = Record.Exception(() =>
        db.Entities.Where(i => i.Value <= 3).Intersect(db.Entities.Where(i => i.Value >= 3)).ToList());
    // Observe: does the driver-LINQ fallback THROW (no oracle -> design holds) or RETURN (oracle exists)?
    Assert.NotNull(ex); // EXPECTED per the design assumption; if this fails, the driver CAN do it
}
```

Build EF10 and run:
```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"
dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build --filter "FullyQualifiedName~NativeSetOpsTests.PROBE_driver_intersect"
```
Expected (design assumption): the fallback throws → the test passes → **no driver oracle exists**.

- [ ] **Step 3: Record the finding, revert the probe**

Revert BOTH the temporary override change and the probe test (`git checkout -- <both files>`), leaving the tree clean.

- **If the probe threw (no oracle):** proceed to Task 2 exactly as written (guard-decline returns `null`).
- **If the probe RETURNED a result (oracle exists):** **STOP and report to the user** — the design's `null`-decline should be relaxed to the Union/Concat graceful `MarkNotNativelyRepresentable()` path, and verification gains a `Native == DriverLinq` parity bar. Do not proceed without the user's confirmation of the changed decline design.

- [ ] **Step 4: Commit (nothing to commit — tree is clean after revert)**

No commit. Report the probe outcome in the task summary.

---

### Task 2: Core native translation (enum + stage + lowerer + factory + QMTEV wiring)

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoSetOperation.cs` (extend enum)
- Create: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/Stages/MongoSetDifferenceStage.cs`
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoSelectLowerer.cs:76-82`
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoPipelineFactory.cs:71-80` (Create loop) + new `RenderSetDifference`
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs` (`TranslateIntersect`/`TranslateExcept:948,1013`, `TryTranslateSetOperation:1433`)
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeSlotPopulator.cs:171-172` (whitelist)
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoSetOperationRouteTests.cs` (route unit tests)
- Test: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeSetOpsTests.cs` (native-success MQL-shape + result tests)

**Interfaces:**
- Consumes: `MongoSetOperation(MongoSetOperationKind, MongoSelectDefinition, string)`, `MongoSelectDefinition.SetOperation`/`.IsSetOp`/`.Route`/`.MarkNotNativelyRepresentable()`, `MongoUnionWithStage`, `MongoPipelineFactory.Create`, `MongoQueryLanguageRenderer`, `PlaceholderTable`, `RenderStage`, `TryTranslateSetOperation`, `IsPlainWholeEntitySelect`, `NativeSlotPopulator.IsNativeRepresentableSlotOperator`.
- Produces: `MongoSetOperationKind.Intersect`/`.Except`; `MongoSetDifferenceStage(MongoSetOperationKind kind, IReadOnlyList<MongoPipelineStage> operandStages, string operandCollectionName)` with `.Kind`/`.OperandStages`/`.OperandCollectionName`; `TryTranslateSetOperation` now returns `ShapedQueryExpression?`.

- [ ] **Step 1: Write the failing unit route tests**

Append to `MongoSetOperationRouteTests.cs`:

```csharp
    [Fact]
    public void Intersect_setoperation_keeps_WholeEntity_route_and_is_terminal()
    {
        var select = new MongoSelectDefinition
        {
            SetOperation = new MongoSetOperation(MongoSetOperationKind.Intersect, OperandSelect(), "customers"),
            IsSetOp = true
        };
        Assert.Equal(NativeRoute.WholeEntity, select.Route);
        Assert.True(select.HasTerminalOperator);
    }

    [Fact]
    public void Except_setoperation_holds_kind()
    {
        var setOp = new MongoSetOperation(MongoSetOperationKind.Except, OperandSelect(), "orders");
        Assert.Equal(MongoSetOperationKind.Except, setOp.Kind);
    }
```

- [ ] **Step 2: Run to verify it fails**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"
```
Expected: **compile error** — `MongoSetOperationKind` has no `Intersect`/`Except` member.

- [ ] **Step 3: Extend the enum**

In `MongoSetOperation.cs`, add to `MongoSetOperationKind` (after `Union`):

```csharp
    /// <summary><c>Intersect</c> — documents present (by full-document value) in BOTH operands (deduped).</summary>
    Intersect,

    /// <summary><c>Except</c> — distinct documents of the first operand not present in the second.</summary>
    Except
```

- [ ] **Step 4: Create the stage type**

Create `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/Stages/MongoSetDifferenceStage.cs` (copy the license header + `using`/namespace from `MongoUnionWithStage.cs`; no BOM, matching the sibling):

```csharp
using System.Collections.Generic;
using MongoDB.EntityFrameworkCore.Query.Expressions;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation.Stages;

/// <summary>
/// A synthesized set-difference / set-intersection terminal (LINQ <c>Intersect</c>/<c>Except</c>). MongoDB
/// has no direct intersect/except stage; because both operands are the SAME collection (same entity type),
/// the renderer emits a source-tagging pipeline: each side is deduped (<c>$group{_id:"$$ROOT"}</c>) and
/// tagged (<c>_a</c>/<c>_b</c>) via <c>$unionWith</c>, re-unified by full document (<c>$group{$max}</c>),
/// discriminated (<c>$match</c>), and unwrapped (<c>$replaceRoot</c>). <see cref="Kind"/> selects the final
/// <c>$match</c> (Intersect: in both; Except: in the first, not the second). BSON-free, like every stage.
/// </summary>
internal sealed class MongoSetDifferenceStage : MongoPipelineStage
{
    public MongoSetDifferenceStage(
        MongoSetOperationKind kind, IReadOnlyList<MongoPipelineStage> operandStages, string operandCollectionName)
    {
        Kind = kind;
        OperandStages = operandStages;
        OperandCollectionName = operandCollectionName;
    }

    public MongoSetOperationKind Kind { get; }
    public IReadOnlyList<MongoPipelineStage> OperandStages { get; }
    public string OperandCollectionName { get; }
}
```

- [ ] **Step 5: Branch the lowerer on kind**

In `MongoSelectLowerer.cs`, replace the `MongoUnionWithStage` add inside the `if (select.SetOperation is { } setOp)` block (currently lines 78-81):

```csharp
        if (select.SetOperation is { } setOp)
        {
            var operandStages = new List<MongoPipelineStage>();
            AppendSelectOpStages(setOp.OperandSelect, operandStages);
            if (setOp.Kind is MongoSetOperationKind.Intersect or MongoSetOperationKind.Except)
            {
                stages.Add(new MongoSetDifferenceStage(setOp.Kind, operandStages, setOp.OperandCollectionName));
            }
            else
            {
                stages.Add(new MongoUnionWithStage(
                    operandStages, setOp.OperandCollectionName, dedup: setOp.Kind == MongoSetOperationKind.Union));
            }
            return stages;
        }
```

Add `using MongoDB.EntityFrameworkCore.Query.NativeTranslation.Stages;` if not already present (check the top of the file — `MongoUnionWithStage` is already referenced, so the `using` exists).

- [ ] **Step 6: Add the renderer branch + `RenderSetDifference` to the factory**

In `MongoPipelineFactory.cs`, extend the `Create` loop (lines 71-77):

```csharp
        foreach (var stage in stages)
        {
            if (stage is MongoUnionWithStage unionWith)
                template.AddRange(RenderUnionWith(unionWith, renderer, placeholders));
            else if (stage is MongoSetDifferenceStage setDiff)
                template.AddRange(RenderSetDifference(setDiff, renderer, placeholders));
            else
                template.Add(RenderStage(stage, renderer, placeholders));
        }
```

Add `RenderSetDifference` next to `RenderUnionWith` (after line 227):

```csharp
    // Renders a synthesized Intersect/Except as a source-tagging pipeline. Both operands are the SAME
    // collection, so full-document ($$ROOT) value-equality is well-defined. Each side is deduped and tagged
    // (_a for the outer/first operand, _b for the inner/second), unioned, re-unified by full document
    // ($group{_id:"$_doc"}), then discriminated by the final $match. Intersect keeps rows present in both
    // (_a && _b); Except keeps rows in the first operand only (_a && !_b). The operand stages render into the
    // SAME placeholder table (a parameter inside the operand substitutes at Build time). _a/_b are siblings
    // of the wrapped document (under _doc), so they never collide with real entity fields.
    private static IEnumerable<BsonDocument> RenderSetDifference(
        MongoSetDifferenceStage stage,
        MongoQueryLanguageRenderer renderer,
        PlaceholderTable placeholders)
    {
        static BsonDocument Tag(bool a, bool b) => new("$project", new BsonDocument
        {
            { "_id", 0 },
            { "_doc", "$_id" },
            { "_a", new BsonDocument("$literal", a) },
            { "_b", new BsonDocument("$literal", b) }
        });

        // Outer (first operand) side: dedup + tag as _a.
        yield return new BsonDocument("$group", new BsonDocument("_id", "$$ROOT"));
        yield return Tag(a: true, b: false);

        // Inner (second operand) side, rendered into the shared placeholder table, itself deduped + tagged.
        var innerPipeline = new BsonArray();
        foreach (var operandStage in stage.OperandStages)
            innerPipeline.Add(RenderStage(operandStage, renderer, placeholders));   // shared placeholders
        innerPipeline.Add(new BsonDocument("$group", new BsonDocument("_id", "$$ROOT")));
        innerPipeline.Add(Tag(a: false, b: true));
        yield return new BsonDocument("$unionWith", new BsonDocument
        {
            { "coll", stage.OperandCollectionName },
            { "pipeline", innerPipeline }
        });

        // Re-unify by full document; collapse the side flags (BSON false < true, so $max over the group is
        // "present on that side").
        yield return new BsonDocument("$group", new BsonDocument
        {
            { "_id", "$_doc" },
            { "_a", new BsonDocument("$max", "$_a") },
            { "_b", new BsonDocument("$max", "$_b") }
        });

        // Discriminate. Intersect: in both (_b true). Except: in the first only (_b false).
        var keepInB = stage.Kind == MongoSetOperationKind.Intersect;
        yield return new BsonDocument("$match", new BsonDocument { { "_a", true }, { "_b", keepInB } });

        // Restore the plain document (the re-unify $group put _doc under _id).
        yield return new BsonDocument("$replaceRoot", new BsonDocument("newRoot", "$_id"));
    }
```

Add `using MongoDB.EntityFrameworkCore.Query.Expressions;` at the top of `MongoPipelineFactory.cs` if `MongoSetOperationKind` is not already resolvable (check existing usings first).

- [ ] **Step 7: Wire the QMTEV overrides + kind-branch the guard-decline**

In `MongoQueryableMethodTranslatingExpressionVisitor.cs`, change the two overrides:

```csharp
    protected override ShapedQueryExpression? TranslateExcept(ShapedQueryExpression source1, ShapedQueryExpression source2)
        => TryTranslateSetOperation(source1, source2, MongoSetOperationKind.Except);
```
```csharp
    protected override ShapedQueryExpression? TranslateIntersect(ShapedQueryExpression source1, ShapedQueryExpression source2)
        => TryTranslateSetOperation(source1, source2, MongoSetOperationKind.Intersect);
```

Change `TryTranslateSetOperation`'s return type to nullable and kind-branch the guard-fail path:

```csharp
    private ShapedQueryExpression? TryTranslateSetOperation(
        ShapedQueryExpression source1, ShapedQueryExpression source2, MongoSetOperationKind kind)
    {
        var mongo1 = (MongoQueryExpression)source1.QueryExpression;
        var mongo2 = (MongoQueryExpression)source2.QueryExpression;

        if (IsPlainWholeEntitySelect(mongo1) && IsPlainWholeEntitySelect(mongo2)
            && mongo1.CollectionExpression.EntityType == mongo2.CollectionExpression.EntityType)
        {
            mongo1.Select.SetOperation = new MongoSetOperation(kind, mongo2.Select, mongo2.CollectionExpression.CollectionName);
            mongo1.Select.IsSetOp = true;
            return source1;
        }

        // Out of scope. Union/Concat have a working driver-LINQ fallback, so mark non-native and return
        // source1 -> graceful fallback (throws only under NativeOnly). Intersect/Except have NO driver-LINQ
        // fallback (Task 1 probe confirmed the driver's LINQ v3 provider does not translate a cross-view
        // Intersect/Except), so returning source1 would route to a fallback that then fails at execution;
        // instead return null so the shape reaches EF's NotTranslatedExpression path and hard-fails cleanly
        // in every mode (mirroring how reference SelectMany declines its no-baseline shapes).
        if (kind is MongoSetOperationKind.Intersect or MongoSetOperationKind.Except)
        {
            return null;
        }

        mongo1.Select.MarkNotNativelyRepresentable();
        return source1;
    }
```

- [ ] **Step 8: Add Intersect/Except to the native-representable whitelist**

In `NativeSlotPopulator.cs`, in `IsNativeRepresentableSlotOperator`, after the `QueryableMethods.Concat` line (172):

```csharp
           || methodDefinition == QueryableMethods.Intersect
           || methodDefinition == QueryableMethods.Except
```

- [ ] **Step 9: Write the failing functional native-success tests**

In `NativeSetOpsTests.cs`, add (after `Concat_whole_entity_goes_native`). The seed is values 1..5; `Where(<=3)` ∩ `Where(>=3)` = {3}; `Where(<=3)` \ `Where(>=3)` = {1,2}:

```csharp
    [Fact]
    public void Intersect_whole_entity_goes_native()
    {
        var collection = SeedCollection(nameof(Intersect_whole_entity_goes_native));
        var logs = new List<string>();
        using var db = MakeWithLogs(collection, MongoQueryMode.NativeOnly, logs);

        // Set op stays TERMINAL (a queryable .OrderBy after it composes past the terminal gate and would
        // fall back / throw under NativeOnly). Result order is not guaranteed, so sort the materialized list.
        var result = db.Entities.Where(i => i.Value <= 3).Intersect(db.Entities.Where(i => i.Value >= 3)).ToList();

        Assert.Equal([3], result.Select(i => i.Value).OrderBy(v => v)); // present in both {1,2,3} and {3,4,5}

        var mql = Mql(logs);
        Assert.Contains("$unionWith", mql);
        Assert.Contains("$replaceRoot", mql);
        Assert.Contains("_doc", mql);   // the source-tagging shape
    }

    [Fact]
    public void Except_whole_entity_goes_native()
    {
        var collection = SeedCollection(nameof(Except_whole_entity_goes_native));
        var logs = new List<string>();
        using var db = MakeWithLogs(collection, MongoQueryMode.NativeOnly, logs);

        var result = db.Entities.Where(i => i.Value <= 3).Except(db.Entities.Where(i => i.Value >= 3)).ToList();

        Assert.Equal([1, 2], result.Select(i => i.Value).OrderBy(v => v)); // in {1,2,3}, not in {3,4,5}

        var mql = Mql(logs);
        Assert.Contains("$unionWith", mql);
        Assert.Contains("$replaceRoot", mql);
    }
```

- [ ] **Step 10: Build and run the new unit + functional tests**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"
dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build \
  --filter "FullyQualifiedName~MongoSetOperationRouteTests|FullyQualifiedName~NativeSetOpsTests.Intersect_whole_entity_goes_native|FullyQualifiedName~NativeSetOpsTests.Except_whole_entity_goes_native"
```
Expected: all PASS. (The `NativeOnly`-succeeds signal proves the query went native; the `_doc`/`$unionWith`/`$replaceRoot` MQL confirms the source-tagging shape.)

- [ ] **Step 11: Commit**

```bash
git add -A
git commit -m "EF-347: Native Intersect/Except via source-tagging \$unionWith pipeline"
```

---

### Task 3: Correctness, boundary & composition-seam tests (functional + spec)

**Files:**
- Modify: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeSetOpsTests.cs` (replace the old `Intersect_falls_back`/`Except_falls_back`; add result-set, guard-decline, composition-seam, field-collision tests)
- Modify: `tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/NorthwindSetOperationsQueryMongoTest.cs` (flip the now-native whole-entity Intersect/Except overrides)

**Interfaces:**
- Consumes: everything from Task 2; the test helpers `SeedCollection`, `Make`, `MakeWithLogs`, `Mql`, the `Item` seed (values 1..5).

- [ ] **Step 1: Replace the stale `Intersect_falls_back` / `Except_falls_back` tests**

These two tests (currently asserting `Assert.Throws<InvalidOperationException>` for a plain whole-entity Intersect/Except) are now WRONG — that shape goes native. Delete them (their native replacements landed in Task 2). Verify none remain:

```bash
grep -n "Intersect_falls_back\|Except_falls_back" tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeSetOpsTests.cs
```
Expected: no output.

- [ ] **Step 2: Write the result-set correctness tests (against expected in-memory data — no driver oracle)**

Add to `NativeSetOpsTests.cs`. Cover disjoint, full-overlap, empty operand, and a parametrized operand predicate:

```csharp
    [Fact]
    public void Intersect_disjoint_operands_yields_empty()
    {
        var collection = SeedCollection(nameof(Intersect_disjoint_operands_yields_empty));
        using var db = Make(collection, MongoQueryMode.Native);
        var result = db.Entities.Where(i => i.Value <= 2).Intersect(db.Entities.Where(i => i.Value >= 4)).ToList();
        Assert.Empty(result);
    }

    [Fact]
    public void Except_disjoint_operands_yields_all_of_first()
    {
        var collection = SeedCollection(nameof(Except_disjoint_operands_yields_all_of_first));
        using var db = Make(collection, MongoQueryMode.Native);
        var result = db.Entities.Where(i => i.Value <= 2).Except(db.Entities.Where(i => i.Value >= 4)).ToList();
        Assert.Equal([1, 2], result.Select(i => i.Value).OrderBy(v => v));
    }

    [Fact]
    public void Intersect_full_overlap_yields_deduped_first()
    {
        var collection = SeedCollection(nameof(Intersect_full_overlap_yields_deduped_first));
        using var db = Make(collection, MongoQueryMode.Native);
        var result = db.Entities.Where(i => i.Value >= 1).Intersect(db.Entities.Where(i => i.Value >= 1)).ToList();
        Assert.Equal([1, 2, 3, 4, 5], result.Select(i => i.Value).OrderBy(v => v));
    }

    [Fact]
    public void Except_whole_second_operand_yields_empty()
    {
        var collection = SeedCollection(nameof(Except_whole_second_operand_yields_empty));
        using var db = Make(collection, MongoQueryMode.Native);
        var result = db.Entities.Where(i => i.Value <= 3).Except(db.Entities.Where(i => i.Value >= 1)).ToList();
        Assert.Empty(result);
    }

    [Fact]
    public void Intersect_parametrized_operand_predicate_substitutes()
    {
        var collection = SeedCollection(nameof(Intersect_parametrized_operand_predicate_substitutes));
        using var db = Make(collection, MongoQueryMode.NativeOnly); // NativeOnly => proves it went native (would throw on fallback)
        var threshold = 3;
        var result = db.Entities.Where(i => i.Value <= 3).Intersect(db.Entities.Where(i => i.Value >= threshold)).ToList();
        Assert.Equal([3], result.Select(i => i.Value).OrderBy(v => v)); // captured `threshold` substitutes inside the operand pipeline
    }
```

- [ ] **Step 3: Write the guard-decline (hard-fail-in-every-mode) tests**

Out-of-scope Intersect/Except must hard-fail in EVERY mode (no graceful fallback — no driver oracle). Use a projected operand and a post-set-op operator:

```csharp
    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.DriverLinq)]
    [InlineData(MongoQueryMode.NativeOnly)]
    public void Projected_intersect_hard_fails_in_every_mode(MongoQueryMode mode)
    {
        var collection = SeedCollection(nameof(Projected_intersect_hard_fails_in_every_mode) + mode);
        using var db = Make(collection, mode);
        Assert.ThrowsAny<Exception>(() =>
            db.Entities.Where(i => i.Value <= 3).Select(i => i.Value)
                .Intersect(db.Entities.Where(i => i.Value >= 3).Select(i => i.Value)).ToList());
    }

    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.DriverLinq)]
    [InlineData(MongoQueryMode.NativeOnly)]
    public void Except_then_Where_hard_fails_in_every_mode(MongoQueryMode mode)
    {
        var collection = SeedCollection(nameof(Except_then_Where_hard_fails_in_every_mode) + mode);
        using var db = Make(collection, mode);
        Assert.ThrowsAny<Exception>(() =>
            db.Entities.Where(i => i.Value <= 3).Except(db.Entities.Where(i => i.Value >= 3))
                .Where(i => i.Value > 0).ToList());
    }
```

- [ ] **Step 4: Write the composition-seam hard-fail tests (one per operator after the set op)**

The `IsSetOp` terminal gate should reject every operator composed after Intersect/Except. Because there is no driver fallback, they hard-fail in every mode. Add a parameterized test over the seam operators (Count, OrderBy, Skip, Take, another set op, GroupBy):

```csharp
    public static IEnumerable<object[]> IntersectComposedOps() => new[]
    {
        new object[] { "OrderBy", (Func<IQueryable<Item>, object>)(q => q.OrderBy(i => i.Value).ToList()) },
        new object[] { "Skip",    (Func<IQueryable<Item>, object>)(q => q.Skip(1).ToList()) },
        new object[] { "Take",    (Func<IQueryable<Item>, object>)(q => q.Take(1).ToList()) },
        new object[] { "GroupBy", (Func<IQueryable<Item>, object>)(q => q.GroupBy(i => i.Value).Select(g => g.Key).ToList()) },
    };

    [Theory]
    [MemberData(nameof(IntersectComposedOps))]
    public void Intersect_then_op_hard_fails_under_native(string name, Func<IQueryable<Item>, object> compose)
    {
        var collection = SeedCollection(nameof(Intersect_then_op_hard_fails_under_native) + name);
        using var db = Make(collection, MongoQueryMode.Native);
        var q = db.Entities.Where(i => i.Value <= 3).Intersect(db.Entities.Where(i => i.Value >= 3));
        Assert.ThrowsAny<Exception>(() => compose(q));
    }
```

Note that a queryable `.OrderBy` after the set op is itself one of these seam cases — it composes past the terminal gate → `MarkNotNativelyRepresentable()` → driver-LINQ fallback → the driver can't do Intersect/Except → throws under `Native` too (and throws at gate time under `NativeOnly`). That is exactly why every native-success test (Task 2 + Step 2 here) keeps the set op terminal and sorts the materialized list in memory. This test locks that seam.

- [ ] **Step 5: Write the field-name-collision isolation test**

An entity whose document has fields literally named `_a`/`_b`/`_doc` must still work — the tag fields are siblings of `_doc`, real fields live under `_doc.*`. This requires a small dedicated entity. Add:

```csharp
    private class TaggyItem
    {
        public ObjectId Id { get; set; }
        [MongoDB.Bson.Serialization.Attributes.BsonElement("_a")] // a real stored element literally named _a
        public int A { get; set; }
        public int Value { get; set; }
    }
```
**Verify the correct element-name mechanism** before writing this — grep how existing tests force an element name (`GetElementName`/`HasElementName`/driver `[BsonElement]`) and match it; the `[BsonElement]` above is the likely form but confirm the provider honors it. Then seed two overlapping `TaggyItem` sets, run Intersect under `Native`, and assert it returns the expected rows — proving a real `_a` element (which lives *inside* `_doc` after wrapping) does not corrupt the sibling `_a` tag. If wiring a custom element name proves heavy, the hazard is already structurally guarded (real fields nest under `_doc`); downgrade to a code-comment note in `RenderSetDifference` and skip this test, recording that decision in the task summary.

- [ ] **Step 6: Build and run the full NativeSetOpsTests class**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"
dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build --filter "FullyQualifiedName~NativeSetOpsTests"
```
Expected: all PASS. Investigate and fix any failure (do not `Skip`).

- [ ] **Step 7: Flip the now-native spec-suite overrides**

Run the Northwind set-ops spec class under Native and see which Intersect/Except tests now translate:
```bash
dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build --filter "FullyQualifiedName~NorthwindSetOperationsQueryMongoTest"
```
For each whole-entity Intersect/Except test that now passes translation (e.g. `Intersect(bool)`, and possibly `Except(bool)`), convert its override from `AssertTranslationFailed(...)` to the passing form (mirror the existing `Union(bool)` override at line 68 — call `base.<Name>(async)` and `AssertMql(...)` with the emitted pipeline). Leave the genuinely out-of-scope ones (`Union_Intersect`, `Intersect_non_entity`, `Intersect_nested` — subquery/projection shapes) as `AssertTranslationFailed`, updating their `// Fails:` comment if the failure reason changed. **Do not use xUnit `Skip`** — baseline every failure green via `AssertTranslationFailed` + an accurate `// Fails:` comment (project convention). Determine the exact `AssertMql` pipeline by running the test and copying the logged MQL.

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "EF-347: Tests for native Intersect/Except (results, guards, seams, spec)"
```

---

### Task 4: Docs + full 3-version verification

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md` (set-ops note)

- [ ] **Step 1: Update the Query AGENTS.md set-ops note**

In `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md`, update the "Set operations — `Union`/`Concat`" note (and the top-of-file scope paragraph that says `Intersect`/`Except` "hard-fail translation unconditionally"). Add a new note documenting:
- Native `Intersect`/`Except` (whole-entity, terminal) via the **source-tagging** MQL (`$group`-dedup + `_a`/`_b` tag + `$unionWith` + re-unify `$group{$max}` + discriminating `$match` + `$replaceRoot`), both operands the same collection so full-document `$$ROOT` equality holds.
- Intersect vs Except differ only in the final `$match` (`_a && _b` vs `_a && !_b`).
- **No driver-LINQ baseline** — unlike Union/Concat, an out-of-scope Intersect/Except **hard-fails in every mode** (`TryTranslateSetOperation` returns `null` for these kinds on a guard-trip, rather than the Union/Concat graceful `MarkNotNativelyRepresentable()` fallback). Every operator composed after is therefore a hard-fail, not a graceful fallback.
- Result order is not guaranteed (the `$group` stages reorder), matching a database set operation and matching Union.
- Correct the earlier statements ("`Intersect`/`Except` remain unsupported, hard-failing translation unconditionally", "`Intersect`/`Except` hard-fail translation unconditionally — in every mode ... never returning results") to reflect that supported whole-entity terminal shapes now go native.
- Mention the A→B→C decomposition: post-set-op composition (slice B) and projected-operand set ops (slice C) are the deferred follow-ups.

Keep the prose consistent with the existing note style (dense, cross-referencing). Do not restate the whole set-ops mechanism — extend it.

- [ ] **Step 2: Commit the docs**

```bash
git add -A
git commit -m "EF-347: Document native Intersect/Except in Query AGENTS.md"
```

- [ ] **Step 3: Full 3-version /test-all**

Invoke the `test-all` skill (controller-run, foreground). It builds + tests EF8/EF9/EF10. Confirm **0 failures** across all three assemblies (unit, spec, functional) — grep every per-assembly `Passed:`/`Failed!` line; a solution run emits three summary blocks per version. Do not accept a partial/single-block summary as proof.

- [ ] **Step 4: NATIVE_ONLY spec sweep (native-coverage delta, zero regressions)**

Run the spec suite for one version with the native-only coverage instrument to confirm the intended increase in native-covered set-op tests and zero regressions:
```bash
MONGODB_EF_NATIVE_ONLY=1 dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build --filter "FullyQualifiedName~NorthwindSetOperationsQueryMongoTest"
```
Expected: the whole-entity Intersect/Except tests now pass under `NativeOnly`; no previously-passing test regresses.

- [ ] **Step 5: Report**

Summarize: probe outcome (Task 1), files touched, the net native-coverage delta, and the full 3-version green counts. This slice is then ready for the whole-branch review (the recurring lesson: the composition-after-terminal seam is caught by whole-branch review, not per-task review — so a whole-branch review pass is expected before squash).

---

## Notes for the implementer

- **The recurring hazard in this codebase is composition AFTER a new terminal** (every prior native slice shipped a silent-wrong-data bug of this class, caught only at whole-branch review). Here it is structurally covered — `IsSetOp` is already in the shared `HasTerminalOperator` gate, so Task 2 adds no new guard — but Task 3's seam tests exist to *prove* it, and a whole-branch review after Task 4 is expected.
- **No driver oracle** — verify results against expected in-memory (LINQ-to-Objects) data, using set-equality or an in-memory sort of the materialized results (not a LINQ `.OrderBy` on the queryable, which would compose after the terminal and fall back / hard-fail). This is the single most error-prone part: keep the set op *terminal* in every native-success assertion.
- **Full 3-version /test-all before any squash** — prior slices repeatedly caught EF8-only build breaks (CS9174 etc.) and EF8/EF9 spec-baseline deltas that EF10-only runs missed.
```
