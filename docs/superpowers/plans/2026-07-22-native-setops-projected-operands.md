# Projected-operand set operations (EF-347 set-ops slice C1) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make a set operation whose operands are terminal SP3 anonymous/DTO member-access projections — `A.Select(projA).Union(B.Select(projB))` and the Concat/Intersect/Except equivalents, operands possibly over different collections — go native, emitting each operand's `$project` ahead of the combine.

**Architecture:** Approach A (minimal provenance flag). `MongoSetOperation` gains a `bool OperandsProjected`, set at `TryTranslateSetOperation` time when both operands are plain projected selects. The gate grows a second acceptance path (`IsPlainProjectedSelect` + `ProjectionShapesMatch`) that drops the whole-entity path's `EntityType`-equality requirement. `MongoSelectLowerer` branches on the flag to emit source1's `$project` before the set-op stage, the operand's `$project` into the nested pipeline, and to suppress the trailing-projection fall-through. Dedup (`$group{_id:$$ROOT}`) and Intersect/Except source-tagging are unchanged — they already operate over whatever the operand pipelines produce, so they now operate over projected documents.

**Tech Stack:** C# / EF Core provider, MongoDB C# driver LINQ v3, xUnit (plain `Assert.*`, no FluentAssertions). Multi-EF via `EF8`/`EF9`/`EF10` build configurations.

## Global Constraints

- Preserve file BOMs; `src/` obeys `<Nullable>enable</Nullable>`; `<NoWarn>EF1001</NoWarn>` (internal-API consumption is intentional).
- Multi-EF: this slice needs **no** `#if` guards (all changes are provider-internal, version-agnostic) — but every task's final check is a build under the target configuration, and the finalize task runs all three via `/test-all`.
- **Native default is not a breaking change** — the native translator becoming the path for a new shape, and any changed emitted MQL, is intended and non-breaking (`MongoSetOperation`/`MongoSelectDefinition`/lowerer are all `internal`).
- **The only reliable "goes native" signal is `MongoQueryMode.NativeOnly`** — a query that goes native succeeds under `NativeOnly`; a fallback shape throws `NativeTranslationNotSupportedException`. Asserting `$unionWith` MQL under `Native` does NOT prove native (fallback can emit the same shape).
- **Subagent-driven, STOP for review after every task.** Do not batch tasks.
- Branch: `EF-347-setops-projected-operands` (currently at `5a69858`, the design commit, stacked on the rolling tip `cb48685`).
- Tests run serially (assembly-level parallelization disabled). Each functional test uses a uniquely-named collection.
- Run tests with `MONGODB_URI` and `ATLAS_URI` unset (isolated atlas-local container per process).

---

## File Structure

- `src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoSetOperation.cs` — **modify.** Add the `OperandsProjected` flag (Task 2).
- `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs` — **modify.** Add the projected-operand acceptance path + `IsPlainProjectedSelect` + `ProjectionShapesMatch` (Task 2).
- `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoSelectLowerer.cs` — **modify.** Emit each operand's projection ahead of the combine; suppress the fall-through Projection block for projected operands (Task 2).
- `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md` — **modify.** Document projected-operand set ops as native; mark C1 as the final set-ops slice; update the deferred lists (Task 5).
- `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeSetOpsTests.cs` — **modify.** Characterization (Task 1), same-collection native (Task 2), different-collection native + two-entity context (Task 3), hazards/deferred/post-composition + flip C2/slice-B deferred tests (Task 4).
- Northwind specification tests (`tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/`) — **re-baseline only if a projected-operand set-op override exists whose MQL now changes** (Task 5).

Each task ends by building the touched project under `-c "Debug EF10"` and running the affected `NativeSetOpsTests` filter. The finalize task runs all three EF versions.

---

## Task 1: Characterize current behavior + de-risk EF-upstream reachability

**Purpose:** Lock the *pre-C1* behavior in tests (the TDD "red" baseline these later tasks flip to green) and — critically — confirm that EF Core's `NavigationExpandingExpressionVisitor` does **not** reject projected-operand set ops (same- or different-collection) upstream before this provider's translator runs. Prior slices found EF rejects *whole-entity* mismatched-type operands upstream ("Incompatible sources used for set operation"); we must confirm projected operands sharing a common anonymous type are NOT rejected, or the different-collection capability is unreachable and Task 3 narrows.

**Files:**
- Test: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeSetOpsTests.cs`

**Interfaces:**
- Consumes: existing `Item`, `SeedCollection`, `Make`, `Run` helpers in `NativeSetOpsTests`.
- Produces: nothing consumed by later tasks — these are characterization tests. Some are *deleted/flipped* in Task 4 once the behavior changes; that is expected and called out there.

- [ ] **Step 1: Add a same-collection projected-operand characterization test documenting current graceful fallback (Union)**

Add to `NativeSetOpsTests` (near the existing `Projected_union_falls_back`):

```csharp
// EF-347 slice C1 CHARACTERIZATION (pre-implementation): a SAME-collection projected-operand Union
// currently falls back gracefully (Projected_union_falls_back already covers this). This test documents
// that DIFFERENT-collection projected operands are REACHABLE (not rejected by EF upstream) and today fall
// back gracefully too. Task 3 flips this to native. If this test THROWS "Incompatible sources used for set
// operation" (a plain InvalidOperationException) instead of returning results, EF rejects the shape upstream
// and the different-collection capability is unreachable — stop and report before proceeding.
[Fact]
public void C1_characterization_different_collection_projected_union_is_reachable_and_falls_back_today()
{
    using var db = MakeTwoEntity(MongoQueryMode.Native);
    // Two DIFFERENT entity types / collections projecting to the SAME anonymous shape {string Label}.
    var result = db.Lefts.Select(l => new { Label = l.Name })
        .Union(db.Rights.Select(r => new { Label = r.Title }))
        .ToList();
    // Lefts: "a","b"; Rights: "b","c" -> Union dedups to {a,b,c}
    Assert.Equal(3, result.Count);
    Assert.Equal(new[] { "a", "b", "c" }, result.Select(x => x.Label).OrderBy(s => s).ToArray());
}
```

- [ ] **Step 2: Add the minimal two-entity context + seed helper this test needs**

Add to `NativeSetOpsTests` (a two-entity context modelled on the existing `LinkedItemDbContext`, but two *independent* entity types with no navigation between them):

```csharp
public class Left { public ObjectId Id { get; set; } public string Name { get; set; } = ""; }
public class Right { public ObjectId Id { get; set; } public string Title { get; set; } = ""; }

private class TwoEntityDbContext : DbContext
{
    private readonly string _lefts;
    private readonly string _rights;
    private readonly MongoQueryMode _mode;

    public TwoEntityDbContext(TemporaryDatabaseFixture db, string lefts, string rights, MongoQueryMode mode)
        : base(new DbContextOptionsBuilder<TwoEntityDbContext>()
            .UseMongoDB(db.Client, db.MongoDatabase.DatabaseNamespace.DatabaseName, o => o.UseQueryMode(mode))
            .ReplaceService<IModelCacheKeyFactory, IgnoreTwoEntityCacheKeyFactory>()
            .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
            .Options)
    {
        _lefts = lefts;
        _rights = rights;
        _mode = mode;
    }

    public DbSet<Left> Lefts { get; set; } = null!;
    public DbSet<Right> Rights { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Left>().ToCollection(_lefts);
        modelBuilder.Entity<Right>().ToCollection(_rights);
    }

    private sealed class IgnoreTwoEntityCacheKeyFactory : IModelCacheKeyFactory
    {
        private static int _count;
        public object Create(DbContext context, bool designTime) => Interlocked.Increment(ref _count);
    }
}

private TwoEntityDbContext MakeTwoEntity(MongoQueryMode mode)
{
    var suffix = Guid.NewGuid().ToString("N")[..8];
    var leftsName = TemporaryDatabaseFixtureBase.CreateCollectionName("C1Lefts") + suffix;
    var rightsName = TemporaryDatabaseFixtureBase.CreateCollectionName("C1Rights") + suffix;
    database.MongoDatabase.GetCollection<Left>(leftsName).InsertMany(
    [
        new Left { Id = ObjectId.GenerateNewId(), Name = "a" },
        new Left { Id = ObjectId.GenerateNewId(), Name = "b" },
    ]);
    database.MongoDatabase.GetCollection<Right>(rightsName).InsertMany(
    [
        new Right { Id = ObjectId.GenerateNewId(), Title = "b" },
        new Right { Id = ObjectId.GenerateNewId(), Title = "c" },
    ]);
    return new TwoEntityDbContext(database, leftsName, rightsName, mode);
}
```

- [ ] **Step 3: Build the FunctionalTests project (EF10)**

Run:
```bash
dotnet build tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj -c "Debug EF10"
```
Expected: build succeeds.

- [ ] **Step 4: Run the characterization test to confirm the shape is REACHABLE and green today**

Run:
```bash
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --no-build \
  --filter "FullyQualifiedName~NativeSetOpsTests.C1_characterization_different_collection_projected_union_is_reachable_and_falls_back_today"
```
Expected: **PASS** (returns 3 rows under `Native` via driver-LINQ fallback). If instead it throws `InvalidOperationException` "Incompatible sources used for set operation", EF rejects the shape upstream — **STOP and report**: the different-collection capability is unreachable and Task 3 must narrow to same-collection only.

- [ ] **Step 5: Commit**

```bash
git add tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeSetOpsTests.cs
git commit -m "EF-347 C1: characterize projected-operand set ops + two-entity test context"
```

---

## Task 2: Native translation for projected operands (all four set ops, core)

**Purpose:** The complete native path. IR flag + gate acceptance + lowerer emission land together — they are only jointly testable (the gate accepting projected operands without the lowerer handling them would emit a wrong pipeline). Proven via `NativeOnly` success + `Native == DriverLinq` parity (Union/Concat) and result-set assertions (Intersect/Except), all over **same-collection** operands.

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoSetOperation.cs`
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs` (`TryTranslateSetOperation` ~lines 1440-1500)
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoSelectLowerer.cs` (`Lower`, the `SetOperation` block ~lines 76-98 and the fall-through Projection block ~lines 136-139)
- Test: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeSetOpsTests.cs`

**Interfaces:**
- Consumes: `MongoSetOperation(kind, operandSelect, operandCollectionName)` ctor (extended below); `MongoSelectDefinition.Projection` (`IReadOnlyList<MongoProjection>`); `MongoProjection` (`readonly record struct { string Alias; MongoExpression Expression; }`); `MongoProjectStage(IReadOnlyList<MongoProjection>)`; `NativeRoute.Projection`; existing `IsPlainWholeEntitySelect`/`ContainsVectorSearch`.
- Produces:
  - `MongoSetOperation` ctor now `(MongoSetOperationKind kind, MongoSelectDefinition operandSelect, string operandCollectionName, bool operandsProjected = false)`; new `bool OperandsProjected { get; }` property.
  - `private static bool IsPlainProjectedSelect(MongoQueryExpression mongo)` and `private static bool ProjectionShapesMatch(IReadOnlyList<MongoProjection> p1, IReadOnlyList<MongoProjection> p2)` in the QMTEV.

- [ ] **Step 1: Write the failing native-proof tests (same-collection, all four)**

Add to `NativeSetOpsTests`:

```csharp
// ── EF-347 slice C1: projected OPERANDS go native (same collection) ──────────────────────────────

[Fact]
public void Projected_operand_union_goes_native()
{
    var collection = SeedCollection(nameof(Projected_operand_union_goes_native));
    using var nativeOnlyDb = Make(collection, MongoQueryMode.NativeOnly); // NativeOnly => proves native
    using var driverDb = Make(collection, MongoQueryMode.DriverLinq);

    static object Q(SingleEntityDbContext<Item> db) =>
        db.Entities.Where(i => i.Value <= 3).Select(i => new { i.Name })
            .Union(db.Entities.Where(i => i.Value >= 3).Select(i => new { i.Name }))
            .OrderBy(x => x.Name).ToList();

    var native = Q(nativeOnlyDb);   // would throw NativeTranslationNotSupportedException on fallback
    Assert.Equal(((System.Collections.IList)Q(driverDb)).Count, ((System.Collections.IList)native).Count);
}

[Fact]
public void Projected_operand_concat_goes_native()
{
    var collection = SeedCollection(nameof(Projected_operand_concat_goes_native));
    using var nativeOnlyDb = Make(collection, MongoQueryMode.NativeOnly);
    using var driverDb = Make(collection, MongoQueryMode.DriverLinq);

    static object Q(SingleEntityDbContext<Item> db) =>
        db.Entities.Where(i => i.Value <= 3).Select(i => new { i.Name })
            .Concat(db.Entities.Where(i => i.Value >= 3).Select(i => new { i.Name }))
            .OrderBy(x => x.Name).ToList();

    var native = (System.Collections.IList)Q(nativeOnlyDb);
    Assert.Equal(((System.Collections.IList)Q(driverDb)).Count, native.Count); // Concat: 3 + 3 = 6 (Value==3 in both)
}

[Fact]
public void Projected_operand_intersect_goes_native_result_set()
{
    var collection = SeedCollection(nameof(Projected_operand_intersect_goes_native_result_set));
    using var db = Make(collection, MongoQueryMode.NativeOnly); // Intersect has no driver oracle -> NativeOnly proves native
    var result = db.Entities.Where(i => i.Value <= 3).Select(i => new { i.Name })
        .Intersect(db.Entities.Where(i => i.Value >= 3).Select(i => new { i.Name }))
        .Select(x => x.Name).ToList();
    Assert.Equal(new[] { "Three" }, result); // only Value==3 (Name "Three") is in both operands
}

[Fact]
public void Projected_operand_except_goes_native_result_set()
{
    var collection = SeedCollection(nameof(Projected_operand_except_goes_native_result_set));
    using var db = Make(collection, MongoQueryMode.NativeOnly);
    var result = db.Entities.Where(i => i.Value <= 3).Select(i => new { i.Name })
        .Except(db.Entities.Where(i => i.Value >= 3).Select(i => new { i.Name }))
        .Select(x => x.Name).OrderBy(s => s).ToList();
    Assert.Equal(new[] { "One", "Two" }, result); // Value 1,2 (<=3) minus Value 3 (in second) = One, Two
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run:
```bash
dotnet build tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj -c "Debug EF10" && \
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~NativeSetOpsTests.Projected_operand_"
```
Expected: FAIL — the `NativeOnly` queries throw `NativeTranslationNotSupportedException` (projected operands not yet accepted / lowered).

- [ ] **Step 3: Add the `OperandsProjected` flag to `MongoSetOperation`**

In `src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoSetOperation.cs`, extend the constructor and add the property:

```csharp
public MongoSetOperation(
    MongoSetOperationKind kind, MongoSelectDefinition operandSelect, string operandCollectionName,
    bool operandsProjected = false)
{
    Kind = kind;
    OperandSelect = operandSelect;
    OperandCollectionName = operandCollectionName;
    OperandsProjected = operandsProjected;
}

public MongoSetOperationKind Kind { get; }
public MongoSelectDefinition OperandSelect { get; }
public string OperandCollectionName { get; }

/// <summary>
/// EF-347 slice C1: <c>true</c> when both operands were plain projected selects
/// (<see cref="MongoSelectDefinition.Projection"/> populated) at the time the set op was attached, so each
/// operand's own <c>$project</c> is part of ITS pipeline and must be emitted BEFORE the combine — source1's
/// ahead of the set-op stage, the operand's inside the nested pipeline (see <c>MongoSelectLowerer.Lower</c>).
/// <c>false</c> for a whole-entity set op (slices A/B) or a trailing projection after a set op (slice C2),
/// where any <c>$project</c> is emitted AFTER the combine.
/// </summary>
public bool OperandsProjected { get; }
```

Also update the class-level XML doc comment (the "Whole-entity, terminal-only (EF-347 slice 2)" line) to note projected operands are now also supported (slice C1).

- [ ] **Step 4: Add the projected-operand acceptance path + helpers to the QMTEV**

In `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs`, inside `TryTranslateSetOperation`, add the projected path immediately after the existing whole-entity `if` block (before the `Intersect`/`Except` `null`-decline block):

```csharp
// EF-347 slice C1: projected operands. Both operands are plain projected selects (a Select-projection is
// the SOLE terminal on each). The EntityType-equality gate above does NOT apply — projected operands may be
// different collections that project to the same shape; ProjectionShapesMatch guards the shape compatibility
// instead (a correctness guard, not just an optimization: the dedup / source-tagging compare whole projected
// documents by value, so mismatched alias sets would mis-compare). EF Core rejects incompatible operand
// shapes upstream, so a mismatch is defense-in-depth.
if (IsPlainProjectedSelect(mongo1) && IsPlainProjectedSelect(mongo2)
    && ProjectionShapesMatch(mongo1.Select.Projection, mongo2.Select.Projection))
{
    mongo1.Select.SetOperation = new MongoSetOperation(
        kind, mongo2.Select, mongo2.CollectionExpression.CollectionName, operandsProjected: true);
    mongo1.Select.IsSetOp = true;
    return source1;
}
```

Then add the two helpers next to `IsPlainWholeEntitySelect`:

```csharp
// A plain projected select: a terminal anonymous/DTO member-access Select is the SOLE thing done (SP3
// Projection populated, Route == Projection) — no grouping, scalar cardinality, its own set op, SelectMany
// ($unwind), cross-collection lookups (Include), join, or a lifted-out VectorSearch. The projected analogue
// of IsPlainWholeEntitySelect (EF-347 slice C1). Note this checks UnwindSource == null, which the
// whole-entity sibling currently omits (a documented latent gap) — the new predicate is deliberately stricter.
private static bool IsPlainProjectedSelect(MongoQueryExpression mongo)
    => mongo.Select.Route == NativeRoute.Projection
       && mongo.Select.Projection.Count > 0
       && mongo.Select.SetOperation == null
       && !mongo.Select.IsSetOp
       && mongo.Select.Grouping == null
       && mongo.Select.Cardinality == null
       && mongo.Select.UnwindSource == null
       && !mongo.IsJoinQuery
       && mongo.Lookups.Count == 0
       && !ContainsVectorSearch(mongo.CapturedExpression);

// The two operands' projected shapes must have identical top-level alias SETS (same count, same alias names).
// The output documents' fields are exactly these aliases, and Union dedup / Intersect-Except source-tagging
// compare whole projected documents by value — mismatched alias sets would compare structurally-different
// documents and silently mis-dedup / mis-tag. Compares alias sets only, NOT the underlying field-refs, so
// e.g. new {N = a.Name} and new {N = b.Title} correctly match (both produce {N: ...}); each operand's own
// $project maps its own source field to the shared alias. EF Core rejects incompatible operand shapes
// upstream (a shared common anonymous type is required for the set op to compile), so a mismatch here is
// defense-in-depth against that guarantee ever weakening.
private static bool ProjectionShapesMatch(
    IReadOnlyList<MongoProjection> p1, IReadOnlyList<MongoProjection> p2)
{
    if (p1.Count != p2.Count)
    {
        return false;
    }

    var aliases = new HashSet<string>(p1.Count);
    foreach (var projection in p1)
    {
        aliases.Add(projection.Alias);
    }

    foreach (var projection in p2)
    {
        if (!aliases.Contains(projection.Alias))
        {
            return false;
        }
    }

    return true;
}
```

If `MongoProjection` is not already visible in this file, add `using MongoDB.EntityFrameworkCore.Query.Expressions;` (it likely is — the file already references `mongo.Select.Projection.Count`).

- [ ] **Step 5: Emit each operand's projection in the lowerer**

In `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoSelectLowerer.cs`, in the `SetOperation` block of `Lower`, add the two projection emissions. Replace:

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
```

with:

```csharp
        if (select.SetOperation is { } setOp)
        {
            // EF-347 slice C1: projected operands. Each operand's own $project is part of ITS pipeline and
            // must be emitted BEFORE the combine — source1's ahead of the set-op stage (appended to `stages`
            // right after source1's PipelineOps above), the operand's inside the nested `operandStages`. The
            // dedup ($group{_id:$$ROOT}) and Intersect/Except source-tagging then operate over the PROJECTED
            // documents (correct: BCL dedups/compares the projected values). Contrast slice C2 (a trailing
            // projection over the COMBINED result), where OperandsProjected is false and select.Projection is
            // emitted AFTER the set-op stage by the fall-through Projection block below.
            if (setOp.OperandsProjected)
            {
                stages.Add(new MongoProjectStage(select.Projection));
            }

            var operandStages = new List<MongoPipelineStage>();
            AppendSelectOpStages(setOp.OperandSelect.PipelineOps, operandStages);
            if (setOp.OperandsProjected)
            {
                operandStages.Add(new MongoProjectStage(setOp.OperandSelect.Projection));
            }

            if (setOp.Kind is MongoSetOperationKind.Intersect or MongoSetOperationKind.Except)
            {
                stages.Add(new MongoSetDifferenceStage(setOp.Kind, operandStages, setOp.OperandCollectionName));
            }
            else
            {
                stages.Add(new MongoUnionWithStage(
                    operandStages, setOp.OperandCollectionName, dedup: setOp.Kind == MongoSetOperationKind.Union));
            }
```

- [ ] **Step 6: Suppress the fall-through Projection block for projected operands**

Still in `MongoSelectLowerer.Lower`, the stage-6 Projection block. Replace:

```csharp
        if (select.Projection.Count > 0)
        {
            stages.Add(new MongoProjectStage(select.Projection));
        }
```

with:

```csharp
        // A projected-operand set op (slice C1) already emitted source1's $project above, ahead of the
        // set-op stage — don't re-emit it here. A trailing projection after a set op (slice C2, OperandsProjected
        // false) and a plain projected Select (no set op) both still emit here.
        if (select.Projection.Count > 0 && !(select.SetOperation?.OperandsProjected ?? false))
        {
            stages.Add(new MongoProjectStage(select.Projection));
        }
```

- [ ] **Step 7: Build src + tests (EF10)**

Run:
```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"
```
Expected: build succeeds, no warnings-as-errors.

- [ ] **Step 8: Run the native-proof tests to verify they pass**

Run:
```bash
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~NativeSetOpsTests.Projected_operand_"
```
Expected: **PASS** — all four (Union/Concat parity, Intersect/Except result-set) succeed under `NativeOnly`.

- [ ] **Step 9: Run the full `NativeSetOpsTests` class to confirm no regression to slices A/B/C2**

Run:
```bash
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~NativeSetOpsTests"
```
Expected: **PASS** for all pre-existing tests (Task 1's `Projected_union_falls_back` still passes — same-collection with `OrderBy`? No: `Projected_union_falls_back` has no trailing op and is a bare projected Union → it now GOES NATIVE, so its `NativeOnly` `Assert.Throws` will FAIL here). **This is expected** — do not fix it in this task; it is flipped in Task 4. Note it and continue. (If any OTHER pre-existing test fails, stop and investigate.)

- [ ] **Step 10: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoSetOperation.cs \
        src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs \
        src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoSelectLowerer.cs \
        tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeSetOpsTests.cs
git commit -m "EF-347 C1: native projected-operand set ops (all four, same-collection)"
```

---

## Task 3: Different-collection operands (the marquee capability)

**Purpose:** Confirm and test that operands over **different collections/entity types** (projecting to the same anonymous shape) go native — the headline C1 capability. If Task 1's probe confirmed reachability, this needs **no new production code** (the projected path already dropped the `EntityType`-equality gate); it is test coverage plus flipping the Task 1 characterization test to native.

**Files:**
- Test: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeSetOpsTests.cs`

**Interfaces:**
- Consumes: `MakeTwoEntity`, `Left`, `Right`, `TwoEntityDbContext` (from Task 1); the projected-operand gate (from Task 2).
- Produces: nothing consumed later.

- [ ] **Step 1: Flip the Task 1 characterization test to a native-proof test**

Replace the body of `C1_characterization_different_collection_projected_union_is_reachable_and_falls_back_today` — rename it and switch to `NativeOnly`:

```csharp
[Fact]
public void Different_collection_projected_operand_union_goes_native()
{
    using var nativeOnlyDb = MakeTwoEntity(MongoQueryMode.NativeOnly); // NativeOnly => proves native
    var result = nativeOnlyDb.Lefts.Select(l => new { Label = l.Name })
        .Union(nativeOnlyDb.Rights.Select(r => new { Label = r.Title }))
        .Select(x => x.Label).OrderBy(s => s).ToList();
    Assert.Equal(new[] { "a", "b", "c" }, result); // Lefts {a,b} ∪ Rights {b,c} = {a,b,c}
}
```

- [ ] **Step 2: Add different-collection Intersect + Except native-proof tests**

```csharp
[Fact]
public void Different_collection_projected_operand_intersect_goes_native_result_set()
{
    using var db = MakeTwoEntity(MongoQueryMode.NativeOnly);
    var result = db.Lefts.Select(l => new { Label = l.Name })
        .Intersect(db.Rights.Select(r => new { Label = r.Title }))
        .Select(x => x.Label).ToList();
    Assert.Equal(new[] { "b" }, result); // {a,b} ∩ {b,c} = {b}
}

[Fact]
public void Different_collection_projected_operand_except_goes_native_result_set()
{
    using var db = MakeTwoEntity(MongoQueryMode.NativeOnly);
    var result = db.Lefts.Select(l => new { Label = l.Name })
        .Except(db.Rights.Select(r => new { Label = r.Title }))
        .Select(x => x.Label).ToList();
    Assert.Equal(new[] { "a" }, result); // {a,b} \ {b,c} = {a}
}
```

- [ ] **Step 3: Add the dedup-over-projected-values correctness test (same collection, distinct entities projecting equal)**

This proves C1's dedup is over projected values (distinct entities projecting to equal values collapse) — matching BCL and deliberately different from C2's whole-entity dedup:

```csharp
[Fact]
public void Projected_operand_union_dedups_over_projected_values_not_whole_entities()
{
    // Two DISTINCT entities (different Id/Value) sharing Name "Dup". A PROJECTED-OPERAND Union over {Name}
    // dedups the projected value -> ONE row. (Contrast C2's trailing projection, where whole entities dedup
    // first so both survive -> TWO rows: Union_dedups_by_whole_entity_then_projects.)
    var collection = SeedCollectionWithDuplicateNames(nameof(Projected_operand_union_dedups_over_projected_values_not_whole_entities));
    using var db = Make(collection, MongoQueryMode.NativeOnly);
    var result = db.Entities.Select(i => new { i.Name })
        .Union(db.Entities.Select(i => new { i.Name }))
        .ToList();
    Assert.Single(result);
    Assert.Equal("Dup", result[0].Name);
}
```

- [ ] **Step 4: Add a per-operand-filter parity test (each operand carries its own Where + a captured parameter)**

Proves each operand's `PipelineOps` lower ahead of its `$project`, and a captured parameter in an operand substitutes correctly:

```csharp
[Fact]
public void Projected_operand_union_with_per_operand_filter_and_parameter_goes_native()
{
    var collection = SeedCollection(nameof(Projected_operand_union_with_per_operand_filter_and_parameter_goes_native));
    var lo = 2;
    var hi = 4;
    using var nativeOnlyDb = Make(collection, MongoQueryMode.NativeOnly);
    using var driverDb = Make(collection, MongoQueryMode.DriverLinq);

    System.Collections.IList Q(SingleEntityDbContext<Item> db) =>
        db.Entities.Where(i => i.Value <= lo).Select(i => new { i.Name })
            .Union(db.Entities.Where(i => i.Value >= hi).Select(i => new { i.Name }))
            .OrderBy(x => x.Name).ToList();

    var native = Q(nativeOnlyDb);
    Assert.Equal(Q(driverDb).Count, native.Count); // Value<=2 (One,Two) ∪ Value>=4 (Four,Five) = 4
}
```

- [ ] **Step 5: Build + run**

Run:
```bash
dotnet build tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj -c "Debug EF10" && \
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --no-build \
  --filter "FullyQualifiedName~NativeSetOpsTests.Different_collection_projected_operand|FullyQualifiedName~NativeSetOpsTests.Projected_operand_union_dedups|FullyQualifiedName~NativeSetOpsTests.Projected_operand_union_with_per_operand_filter"
```
Expected: **PASS** for all five.

- [ ] **Step 6: Commit**

```bash
git add tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeSetOpsTests.cs
git commit -m "EF-347 C1: different-collection projected operands + dedup-over-projected-values coverage"
```

---

## Task 4: Hazards, deferred-shape fallback split, post-composition auto-closure; flip C2/slice-B deferred tests

**Purpose:** Prove the guards and the auto-closed post-composition seam, keep the deferred shapes falling back / hard-failing as designed, and flip the slice-B/C2 tests that named projected-operand set ops as the deferred gap.

**Files:**
- Test: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeSetOpsTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 2-3.
- Produces: nothing consumed later.

- [ ] **Step 1: Flip `Projected_union_falls_back` → native**

Replace the existing `Projected_union_falls_back` test (it named projected-operand Union as a fallback gap; it now goes native):

```csharp
[Fact]
public void Projected_operand_union_bare_goes_native()
{
    // Formerly Projected_union_falls_back — a bare projected-operand Union (no trailing op) now goes NATIVE
    // (EF-347 slice C1). NativeOnly succeeds instead of throwing.
    var collection = SeedCollection(nameof(Projected_operand_union_bare_goes_native));
    using var nativeOnlyDb = Make(collection, MongoQueryMode.NativeOnly);
    var result = nativeOnlyDb.Entities.Where(i => i.Value <= 3).Select(i => new { i.Name })
        .Union(nativeOnlyDb.Entities.Where(i => i.Value >= 3).Select(i => new { i.Name }))
        .ToList();
    Assert.Equal(5, result.Count); // {One,Two,Three} ∪ {Three,Four,Five} = 5 distinct Names
}
```

- [ ] **Step 2: Flip `Projected_intersect_hard_fails_in_every_mode` → native result-set**

Locate `Projected_intersect_hard_fails_in_every_mode` (the `[Theory]` over all three modes, ~line 247-262). A bare projected-operand Intersect now goes native; replace it with a native result-set test (keep a DriverLinq/NativeOnly note that Intersect has no driver oracle):

```csharp
[Fact]
public void Projected_operand_intersect_bare_goes_native_result_set()
{
    // Formerly Projected_intersect_hard_fails_in_every_mode — a bare projected-operand Intersect now goes
    // NATIVE (EF-347 slice C1). Result asserted against expected in-memory data (no driver oracle for
    // Intersect); NativeOnly proves native, default Native gives the same result.
    var collection = SeedCollection(nameof(Projected_operand_intersect_bare_goes_native_result_set));
    foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.NativeOnly })
    {
        using var db = Make(collection, mode);
        var result = db.Entities.Where(i => i.Value <= 3).Select(i => new { i.Name })
            .Intersect(db.Entities.Where(i => i.Value >= 3).Select(i => new { i.Name }))
            .Select(x => x.Name).ToList();
        Assert.Equal(new[] { "Three" }, result);
    }
}
```

- [ ] **Step 3: Add the deferred-shape fallback-split tests (bare-scalar / computed / mixed operands)**

```csharp
// ── EF-347 slice C1 deferred shapes keep current behavior (fall back / hard-fail) ────────────────

[Fact]
public void Bare_scalar_operand_union_falls_back_gracefully()
{
    // Bare-scalar operand (Select(i => i.Name), no anonymous/DTO) never populates Projection -> not a plain
    // projected select -> graceful fallback for Union (throws only under NativeOnly).
    var collection = SeedCollection(nameof(Bare_scalar_operand_union_falls_back_gracefully));
    using (var nativeOnlyDb = Make(collection, MongoQueryMode.NativeOnly))
    {
        Assert.Throws<NativeTranslationNotSupportedException>(() =>
            nativeOnlyDb.Entities.Select(i => i.Name)
                .Union(nativeOnlyDb.Entities.Select(i => i.Name)).ToList());
    }

    using var nativeDb = Make(collection, MongoQueryMode.Native);
    Assert.Equal(5, nativeDb.Entities.Select(i => i.Name)
        .Union(nativeDb.Entities.Select(i => i.Name)).ToList().Count);
}

[Fact]
public void Computed_leaf_operand_union_falls_back_gracefully()
{
    // Computed projection leaf (i.Value * 2) is not the SP3 member-access surface -> operand not a plain
    // projected select -> graceful fallback for Union.
    var collection = SeedCollection(nameof(Computed_leaf_operand_union_falls_back_gracefully));
    using (var nativeOnlyDb = Make(collection, MongoQueryMode.NativeOnly))
    {
        Assert.Throws<NativeTranslationNotSupportedException>(() =>
            nativeOnlyDb.Entities.Select(i => new { Doubled = i.Value * 2 })
                .Union(nativeOnlyDb.Entities.Select(i => new { Doubled = i.Value * 2 })).ToList());
    }

    using var nativeDb = Make(collection, MongoQueryMode.Native);
    Assert.NotEmpty(nativeDb.Entities.Select(i => new { Doubled = i.Value * 2 })
        .Union(nativeDb.Entities.Select(i => new { Doubled = i.Value * 2 })).ToList());
}
```

- [ ] **Step 4: Add the post-composition auto-closure tests**

```csharp
[Fact]
public void Where_after_projected_operand_union_falls_back_gracefully()
{
    // Post-composition after a projected-operand set op: Projection.Count > 0 -> IsSetOpTerminalOnly is
    // false -> the trailing Where is rejected (auto-closed, no new guard). Union falls back gracefully.
    var collection = SeedCollection(nameof(Where_after_projected_operand_union_falls_back_gracefully));
    using (var nativeOnlyDb = Make(collection, MongoQueryMode.NativeOnly))
    {
        // NOTE: if EF fuses this Where back into the operands (pending-selector pushdown, as seen for
        // trailing projections in C2), it may instead go native. If this Assert.Throws FAILS because the
        // query succeeded, replace with a Native==DriverLinq parity assertion and rename to
        // "..._goes_native_via_ef_predicate_pushdown" (mirroring the C2 finding). Verify empirically.
        Assert.Throws<NativeTranslationNotSupportedException>(() =>
            nativeOnlyDb.Entities.Select(i => new { i.Name, i.Value })
                .Union(nativeOnlyDb.Entities.Select(i => new { i.Name, i.Value }))
                .Where(x => x.Value > 2).ToList());
    }
}

[Theory]
[InlineData(MongoQueryMode.Native)]
[InlineData(MongoQueryMode.DriverLinq)]
[InlineData(MongoQueryMode.NativeOnly)]
public void Op_after_projected_operand_intersect_hard_fails_in_every_mode(MongoQueryMode mode)
{
    // Intersect has no driver-LINQ baseline, so post-composition after a projected-operand Intersect
    // hard-fails in EVERY mode (same as slices A/B/C2's Intersect deferral).
    var collection = SeedCollection(nameof(Op_after_projected_operand_intersect_hard_fails_in_every_mode) + mode);
    using var db = Make(collection, mode);
    Assert.ThrowsAny<Exception>(() =>
        db.Entities.Select(i => new { i.Name, i.Value })
            .Intersect(db.Entities.Select(i => new { i.Name, i.Value }))
            .Where(x => x.Value > 2).ToList());
}
```

- [ ] **Step 5: Build + run the affected tests**

Run:
```bash
dotnet build tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj -c "Debug EF10" && \
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~NativeSetOpsTests"
```
Expected: **PASS** for the whole `NativeSetOpsTests` class (all flips + new tests + all pre-existing A/B/C2 tests). If `Where_after_projected_operand_union_falls_back_gracefully` fails because EF fused the predicate (query succeeded), apply the in-comment fallback (parity + rename), rebuild, re-run.

- [ ] **Step 6: Commit**

```bash
git add tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeSetOpsTests.cs
git commit -m "EF-347 C1: hazards, deferred-shape split, post-composition auto-closure; flip C2/slice-B deferred tests"
```

---

## Task 5: Docs, spec re-baseline, full 3-version verification, finalize

**Purpose:** Document the slice as-built, re-baseline any affected Northwind spec MQL, and prove green across all three EF versions plus the `NativeOnly` spec sweep.

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md`
- Possibly modify: Northwind specification-test overrides (only if a projected-operand set-op override exists whose MQL changed)

**Interfaces:**
- Consumes: everything from Tasks 2-4.
- Produces: the finalized, reviewable slice.

- [ ] **Step 1: Update `Query/AGENTS.md`**

In the set-ops as-built notes: (a) add a "projected operands (EF-347 slice C1)" paragraph describing the `OperandsProjected` provenance flag, `IsPlainProjectedSelect`/`ProjectionShapesMatch`, the lowerer emitting each operand's `$project` before the combine, and dedup-over-projected-values (contrast C2's whole-entity dedup); (b) note different-collection operands are supported (the `EntityType`-equality gate does not apply on the projected path); (c) change every place that names "projected-operand set operations ... remain slice C1 — a follow-up" to state C1 is **done** and the set-ops decomposition (A → B → C1/C2) is **complete**; (d) note post-composition after a projected-operand set op stays deferred (auto-closed by `IsSetOpTerminalOnly`'s `Projection.Count == 0`). Keep it consistent with the design spec `docs/superpowers/specs/2026-07-22-native-setops-projected-operands-design.md`.

- [ ] **Step 2: Commit the docs**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/AGENTS.md
git commit -m "EF-347 C1: document projected-operand set ops as native (final set-ops slice)"
```

- [ ] **Step 3: Check for affected Northwind spec baselines under NativeOnly**

Run the NativeOnly set-operations spec sweep for EF10 to see if any previously-fallback projected-operand set-op spec test now goes native (and whether any `AssertMql` baseline changed):
```bash
MONGODB_EF_NATIVE_ONLY=1 dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~SetOperations"
```
Expected: no NEW failures vs. the pre-C1 baseline (net additive — more pass, none regress). If a specific override's data now passes but its `AssertMql` baseline is stale, re-baseline ONLY that override with `EF_TEST_REWRITE_BASELINES=1` scoped to it, `git diff` the result, rebuild, and re-run without the var to confirm green. Commit any re-baseline separately with message `EF-347 C1: re-baseline <Class>.<Method> for native projected-operand set op`.

- [ ] **Step 4: Full 3-version `/test-all`**

Invoke the `/test-all` skill (controller-run, foreground, tee-to-log, sum all three per-assembly summary blocks — per the native-stack process lesson). Expected: **GREEN, 0 failures, all three assemblies each for EF8, EF9, EF10**. Record the pass/skip counts.

- [ ] **Step 5: NativeOnly set-ops spec sweep delta**

Confirm the `MONGODB_EF_NATIVE_ONLY=1` set-ops spec sweep shows a **net increase** in passing tests vs. the pre-C1 tip `cb48685`, with **zero regressions**. Record the before/after counts.

- [ ] **Step 6: Final review checkpoint**

Stop for the whole-branch review (opus) per the native-stack process. Do NOT squash or push until the review is clean and the user directs it. The squash + fast-forward-push-to-`origin/NativeQueryOngoing` mechanics follow the established stacked-PR workflow (backup branch first, verify byte-identical, plain FF push since C1's parent is the remote tip `cb48685`).

---

## Self-Review

**Spec coverage:**
- IR `OperandsProjected` flag → Task 2 Step 3. ✓
- Gate `IsPlainProjectedSelect` + `ProjectionShapesMatch`, `EntityType` gate dropped on projected path → Task 2 Step 4. ✓
- Lowerer: source1 `$project` before combine, operand `$project` into nested pipeline, suppress fall-through → Task 2 Steps 5-6. ✓
- Shaper unchanged → no task needed (verified: reused source1 SP3 shaper). ✓
- Different-collection operands (marquee) → Task 1 (reachability probe) + Task 3. ✓
- Union dedup over projected values → Task 3 Step 3. ✓
- Intersect/Except `$$ROOT` over projected docs → Task 2 Steps 1/8 (result-set) + Task 3 Step 2. ✓
- Post-composition auto-closed → Task 4 Step 4. ✓
- Deferred shapes (bare-scalar/computed/mixed) keep behavior → Task 4 Step 3. ✓
- Flip slice-B/C2 deferred tests → Task 4 Steps 1-2. ✓
- All four set ops → Task 2 (all four) + Task 3 (all four different-collection). ✓
- AGENTS.md docs, C1 = final slice → Task 5 Steps 1-2. ✓
- Spec re-baseline, 3-version /test-all, NativeOnly sweep → Task 5 Steps 3-5. ✓

**Placeholder scan:** No TBD/TODO; every code step shows complete code; the one conditional (Task 4 Step 4 EF-fusion) has an explicit empirical fallback with concrete instructions, mirroring the documented C2 finding. ✓

**Type consistency:** `OperandsProjected` (bool), `MongoSetOperation` 4-arg ctor, `IsPlainProjectedSelect(MongoQueryExpression)→bool`, `ProjectionShapesMatch(IReadOnlyList<MongoProjection>, IReadOnlyList<MongoProjection>)→bool`, `MongoProjection.Alias` (string), `MongoProjectStage(IReadOnlyList<MongoProjection>)`, `select.Projection`/`setOp.OperandSelect.Projection` (IReadOnlyList<MongoProjection>) — all consistent with the verified source. ✓
