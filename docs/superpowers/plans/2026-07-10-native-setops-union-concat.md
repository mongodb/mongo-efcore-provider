# Native Set-ops Union/Concat (EF-347 slice 2) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Translate whole-entity, terminal `Union`/`Concat` to a native `$unionWith` pipeline (Union adds full-document `$$ROOT` dedup), introducing the shared-placeholder nested-pipeline machinery; everything else falls back gracefully.

**Architecture:** Approach A — attach a `MongoSetOperation` to source1's `MongoSelectDefinition`; `Route` stays `WholeEntity` (same shaper); terminal-only via adding `IsSetOp` to the shared post-terminal guard (`HasTerminalGrouping` → renamed `HasTerminalOperator`). Lowerer appends a new `MongoUnionWithStage`; factory renders `$unionWith` with the operand pipeline into the shared placeholder table.

**Tech Stack:** C# / EF Core provider, xUnit (plain `Assert.*`, no FluentAssertions). Multi-EF via `Debug EF8|EF9|EF10` configs.

## Global Constraints

- **Additive + non-breaking.** Supported shapes go native; every unsupported shape **falls back gracefully to driver-LINQ** (throws only under `MongoQueryMode.NativeOnly`). `Union`/`Concat` today already fall back gracefully — do NOT regress that into a hard `NotTranslated` failure.
- **`Native == DriverLinq` results** (query results unchanged); MQL and native-vs-driver path are not contract.
- `<Nullable>enable</Nullable>`; preserve BOMs; new members `internal`/`private` (no public-API break).
- Build config strings contain spaces: `-c "Debug EF10"`. Tests run serially.
- Namespaces: `MongoSelectDefinition`, `MongoQueryExpression`, `MongoSetOperation`, `NativeRoute` ∈ `MongoDB.EntityFrameworkCore.Query.Expressions`; stages ∈ `MongoDB.EntityFrameworkCore.Query.NativeTranslation.Stages`; lowerer/factory ∈ `MongoDB.EntityFrameworkCore.Query.NativeTranslation`; QMTEV ∈ `MongoDB.EntityFrameworkCore.Query.Visitors`.
- Design: `docs/superpowers/specs/2026-07-10-native-setops-union-concat-design.md`.

## Scope (verbatim from the design)

**In:** `Union`/`Concat` where both operands materialize **whole entities of the same entity type**, both are **plain natively-lowerable whole-entity selects** (may carry their own `Where`/`OrderBy`/`Skip`/`Take`), and the set op is **terminal**.
**Out → graceful fallback:** `Intersect`/`Except`; projected/scalar set ops; any operator after the union (incl. chained `a.Union(b).Union(c)`); an operand that carries a projection/grouping/cardinality/its own set op/lookups(Include)/`VectorSearch`; different-entity-type operands.

---

### Task 1: Establish the driver baseline + IR (`MongoSetOperation`, `IsSetOp`, `HasTerminalOperator`)

**Files:**
- Create: `src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoSetOperation.cs`
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoSelectDefinition.cs`
- Modify (mechanical rename `HasTerminalGrouping`→`HasTerminalOperator`): `src/.../Query/NativeTranslation/NativeSlotPopulator.cs`, `src/.../Query/NativeTranslation/NativeCardinalityBinder.cs`, `src/.../Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs`
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoSetOperationRouteTests.cs`

**Interfaces:**
- Produces:
  - `internal enum MongoSetOperationKind { Concat, Union }`
  - `internal sealed class MongoSetOperation` with `MongoSetOperationKind Kind`, `MongoSelectDefinition OperandSelect`, `string OperandCollectionName` (ctor sets all three; all get-only).
  - `MongoSelectDefinition.SetOperation` (`internal MongoSetOperation? { get; set; }`), `MongoSelectDefinition.IsSetOp` (`internal bool { get; set; }`), and `HasTerminalOperator` (renamed from `HasTerminalGrouping`, now `=> IsGroupBy || IsDistinct || IsSetOp || Grouping != null`).

- [ ] **Step 1: Establish the driver baseline (documents the verification bar; no code)**

Run a whole-entity `Union` and `Concat` against the current tip under default `Native` mode (which today falls back to driver-LINQ) and record the outcome:

Add a temporary throwaway test OR use an existing functional Query test harness to execute:
`ctx.Set<Customer>().Where(c => c.X).Union(ctx.Set<Customer>().Where(c => c.Y)).ToList()`.
Run it; note in the task report whether it **succeeds** (driver-LINQ implements `$unionWith`) or **throws** (translation failure).
- If it SUCCEEDS → later tasks assert `Native == DriverLinq` parity via `AssertQuery`.
- If it THROWS → later tasks assert `Native` against **expected in-memory results** (the feature is purely additive).
Record which case holds; the later functional tests (Task 4/5) follow it. Delete the throwaway probe.

- [ ] **Step 2: Write the failing IR tests**

Create `MongoSetOperationRouteTests.cs` (copy the license header from a sibling test file to preserve the BOM):

```csharp
using MongoDB.EntityFrameworkCore.Query.Expressions;
using Xunit;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query.NativeTranslation;

public class MongoSetOperationRouteTests
{
    private static MongoSelectDefinition OperandSelect()
        => new();

    [Fact]
    public void SetOperation_keeps_WholeEntity_route()
    {
        var select = new MongoSelectDefinition
        {
            SetOperation = new MongoSetOperation(MongoSetOperationKind.Union, OperandSelect(), "customers"),
            IsSetOp = true
        };
        Assert.Equal(NativeRoute.WholeEntity, select.Route);
    }

    [Fact]
    public void IsSetOp_marks_HasTerminalOperator()
    {
        var select = new MongoSelectDefinition { IsSetOp = true };
        Assert.True(select.HasTerminalOperator);
    }

    [Fact]
    public void No_terminal_operator_by_default()
        => Assert.False(new MongoSelectDefinition().HasTerminalOperator);

    [Fact]
    public void SetOperation_holds_kind_operand_and_collection()
    {
        var operand = OperandSelect();
        var setOp = new MongoSetOperation(MongoSetOperationKind.Concat, operand, "orders");
        Assert.Equal(MongoSetOperationKind.Concat, setOp.Kind);
        Assert.Same(operand, setOp.OperandSelect);
        Assert.Equal("orders", setOp.OperandCollectionName);
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj -c "Debug EF10" --filter "FullyQualifiedName~MongoSetOperationRouteTests"`
Expected: compile failure — `MongoSetOperation`, `SetOperation`, `IsSetOp`, `HasTerminalOperator` do not exist.

- [ ] **Step 4: Create `MongoSetOperation.cs`**

```csharp
/* <copy the standard license header + BOM from a sibling Expressions/*.cs file> */

namespace MongoDB.EntityFrameworkCore.Query.Expressions;

/// <summary>The kind of set operation captured on a <see cref="MongoSelectDefinition"/>.</summary>
internal enum MongoSetOperationKind
{
    /// <summary><c>Concat</c> — <c>$unionWith</c> with no de-duplication.</summary>
    Concat,

    /// <summary><c>Union</c> — <c>$unionWith</c> followed by full-document (<c>$$ROOT</c>) de-duplication.</summary>
    Union
}

/// <summary>
/// A terminal set operation (<c>Union</c>/<c>Concat</c>) attached to the outer
/// <see cref="MongoSelectDefinition"/>. The second operand is captured as its own plain whole-entity
/// <see cref="MongoSelectDefinition"/> (rendered as the nested <c>$unionWith</c> pipeline against
/// <see cref="OperandCollectionName"/>). Whole-entity, terminal-only (EF-347 slice 2).
/// </summary>
internal sealed class MongoSetOperation
{
    public MongoSetOperation(MongoSetOperationKind kind, MongoSelectDefinition operandSelect, string operandCollectionName)
    {
        Kind = kind;
        OperandSelect = operandSelect;
        OperandCollectionName = operandCollectionName;
    }

    public MongoSetOperationKind Kind { get; }
    public MongoSelectDefinition OperandSelect { get; }
    public string OperandCollectionName { get; }
}
```

- [ ] **Step 5: Wire `MongoSelectDefinition`**

In `MongoSelectDefinition.cs`, in the terminal/grouping region, add the fields and rename the predicate:

```csharp
/// <summary>The terminal set operation (Union/Concat), when this select is a set-op query (EF-347 slice 2).</summary>
internal MongoSetOperation? SetOperation { get; set; }

/// <summary>
/// <see langword="true"/> when a terminal set operation is attached. A SEPARATE provenance flag (like
/// <see cref="IsDistinct"/>) that joins the post-terminal guard <see cref="HasTerminalOperator"/> so any
/// operator applied AFTER the union falls back (terminal-only scope).
/// </summary>
internal bool IsSetOp { get; set; }
```

Rename `HasTerminalGrouping` to `HasTerminalOperator` and extend it:

```csharp
/// <summary>
/// <see langword="true"/> when this select already carries a terminal operator whose output a following
/// operator must not be silently emitted before/around: a native grouping (<see cref="IsGroupBy"/>), a
/// projected <see cref="IsDistinct"/>, a terminal set operation (<see cref="IsSetOp"/>), or a finalized
/// <see cref="Grouping"/>. Every post-terminal entry point gates on this.
/// </summary>
internal bool HasTerminalOperator => IsGroupBy || IsDistinct || IsSetOp || Grouping != null;
```

`Route` is unchanged: a set-op query has no `Grouping`/`Cardinality`/`Projection`, so `Route` naturally resolves to `WholeEntity`.

- [ ] **Step 6: Mechanical rename across callers**

Rename every `HasTerminalGrouping` reference to `HasTerminalOperator` in `NativeSlotPopulator.cs`, `NativeCardinalityBinder.cs`, and `MongoQueryableMethodTranslatingExpressionVisitor.cs` (e.g. `TranslateGroupBy`'s `hadTerminalGrouping = mongoQueryExpression.Select.HasTerminalGrouping`). Grep to confirm none remain:
`grep -rn "HasTerminalGrouping" src/` → expect no matches.

- [ ] **Step 7: Run tests to verify they pass + build**

Run:
```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10" -v quiet
dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj -c "Debug EF10" --no-build --filter "FullyQualifiedName~MongoSetOperationRouteTests"
```
Expected: build OK; 4/4 pass.

- [ ] **Step 8: Commit**

```bash
git add -A && git commit -m "EF-347: Set-op IR — MongoSetOperation + IsSetOp; HasTerminalGrouping->HasTerminalOperator"
```

---

### Task 2: `MongoUnionWithStage` + lowerer append (recursive operand lowering)

**Files:**
- Create: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/Stages/MongoUnionWithStage.cs`
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoSelectLowerer.cs`
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoSelectLowererTests.cs` (add cases)

**Interfaces:**
- Consumes: `MongoSetOperation`, `MongoSelectDefinition` (Task 1).
- Produces: `internal sealed class MongoUnionWithStage : MongoPipelineStage` with `IReadOnlyList<MongoPipelineStage> OperandStages`, `string OperandCollectionName`, `bool Dedup`. Lowerer emits it as the terminal stage when `select.SetOperation != null`.

- [ ] **Step 1: Write the failing lowerer tests**

Add to `MongoSelectLowererTests.cs` (match its existing construction style; use `MongoFieldExpression`/`MongoConstantExpression` for operand slots as elsewhere in that file):

```csharp
[Fact]
public void SetOperation_appends_union_stage_after_canonical_stages()
{
    var operand = new MongoSelectDefinition();   // empty operand → no inner stages
    var query = MongoQueryExpressionTestHelper.WholeEntity();   // or the file's existing helper to build a MongoQueryExpression
    query.Select.SetOperation = new MongoSetOperation(MongoSetOperationKind.Concat, operand, "customers");
    query.Select.IsSetOp = true;

    var stages = new MongoSelectLowerer().Lower(query);

    var union = Assert.IsType<MongoUnionWithStage>(Assert.Single(stages));
    Assert.Equal("customers", union.OperandCollectionName);
    Assert.False(union.Dedup);
    Assert.Empty(union.OperandStages);
}

[Fact]
public void Union_sets_dedup_and_lowers_operand_predicate()
{
    var operand = new MongoSelectDefinition { Predicate = /* a MongoBinaryExpression field==const, as built elsewhere in this test file */ };
    var query = MongoQueryExpressionTestHelper.WholeEntity();
    query.Select.SetOperation = new MongoSetOperation(MongoSetOperationKind.Union, operand, "customers");
    query.Select.IsSetOp = true;

    var stages = new MongoSelectLowerer().Lower(query);

    var union = Assert.IsType<MongoUnionWithStage>(Assert.Single(stages));
    Assert.True(union.Dedup);
    Assert.IsType<MongoMatchStage>(Assert.Single(union.OperandStages));   // operand predicate → $match inside the union pipeline
}
```

> Implementer note: use the exact `MongoQueryExpression`/`MongoSelectDefinition` construction helpers already used by the other tests in `MongoSelectLowererTests.cs` (do not invent a helper name — match the file). If the outer query also has a `Where`, assert the outer `$match` precedes the `MongoUnionWithStage`.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test ...UnitTests... -c "Debug EF10" --filter "FullyQualifiedName~MongoSelectLowererTests"`
Expected: compile failure — `MongoUnionWithStage` does not exist.

- [ ] **Step 3: Create `MongoUnionWithStage.cs`**

```csharp
/* <license header + BOM copied from a sibling Stages/*.cs> */

using System.Collections.Generic;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation.Stages;

/// <summary>
/// A <c>$unionWith</c> aggregation stage: unions the current pipeline output with the result of running
/// <see cref="OperandStages"/> against <see cref="OperandCollectionName"/>. When <see cref="Dedup"/> is
/// set (LINQ <c>Union</c>), the renderer follows it with a full-document (<c>$$ROOT</c>) de-duplication.
/// </summary>
internal sealed class MongoUnionWithStage : MongoPipelineStage
{
    public MongoUnionWithStage(IReadOnlyList<MongoPipelineStage> operandStages, string operandCollectionName, bool dedup)
    {
        OperandStages = operandStages;
        OperandCollectionName = operandCollectionName;
        Dedup = dedup;
    }

    public IReadOnlyList<MongoPipelineStage> OperandStages { get; }
    public string OperandCollectionName { get; }
    public bool Dedup { get; }
}
```

- [ ] **Step 4: Lowerer — extract canonical stages, append the union stage**

In `MongoSelectLowerer.cs`, extract the existing `$match → $sort → $skip → $limit` block (current lines ~60–82) into a private helper so it can lower the operand too:

```csharp
private static void AppendCanonicalStages(MongoSelectDefinition select, List<MongoPipelineStage> stages)
{
    if (select.Predicate != null) stages.Add(new MongoMatchStage(select.Predicate));
    if (select.Orderings.Count > 0) stages.Add(new MongoSortStage(select.Orderings));
    if (select.Offset != null) stages.Add(new MongoSkipStage(select.Offset));
    if (select.Limit != null) stages.Add(new MongoLimitStage(select.Limit));
}
```

Replace those four inline blocks in `Lower` with `AppendCanonicalStages(select, stages);`. Then, immediately after the `AppendLookupStages(query, stages);` call (stage 5) and BEFORE the grouping/projection/cardinality blocks, add:

```csharp
// Set operation terminal ($unionWith [+ dedup]). Guaranteed terminal and whole-entity by the QMTEV
// guard (the operand is a plain whole-entity select — no grouping/projection/cardinality/lookups), so
// nothing follows it and the operand lowers to canonical stages only.
if (select.SetOperation is { } setOp)
{
    var operandStages = new List<MongoPipelineStage>();
    AppendCanonicalStages(setOp.OperandSelect, operandStages);
    stages.Add(new MongoUnionWithStage(operandStages, setOp.OperandCollectionName, dedup: setOp.Kind == MongoSetOperationKind.Union));
    return stages;
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet build ... -c "Debug EF10" -v quiet` then `dotnet test ...UnitTests... -c "Debug EF10" --no-build --filter "FullyQualifiedName~MongoSelectLowererTests"`
Expected: PASS (existing lowerer tests + the 2 new ones).

- [ ] **Step 6: Commit**

```bash
git add -A && git commit -m "EF-347: MongoUnionWithStage + lowerer append (recursive operand lowering)"
```

---

### Task 3: Factory — `RenderUnionWith` (nested pipeline, shared placeholders, dedup) + paging validation

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoPipelineFactory.cs`
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoPipelineFactoryTests.cs` (add cases)

**Interfaces:**
- Consumes: `MongoUnionWithStage` (Task 2).
- Produces: rendered `{ $unionWith: { coll, pipeline } }` (+ `$group {_id:"$$ROOT"}`, `$replaceRoot {newRoot:"$_id"}` when `Dedup`); operand stages rendered into the SAME `PlaceholderTable`; `ValidatePagingStages` also validates paging inside the nested pipeline.

- [ ] **Step 1: Write the failing factory tests**

Add to `MongoPipelineFactoryTests.cs` (match existing style — the file builds stage lists and asserts on `Create(...).Build(...)` output):

```csharp
[Fact]
public void Concat_renders_unionWith_without_dedup()
{
    var stages = new MongoPipelineStage[]
    {
        new MongoUnionWithStage(new MongoPipelineStage[0], "customers", dedup: false)
    };
    var pipeline = MongoPipelineFactory.Create(stages, NewRenderer()).Build(NoParams());
    Assert.Single(pipeline);
    var unionWith = pipeline[0]["$unionWith"].AsBsonDocument;
    Assert.Equal("customers", unionWith["coll"].AsString);
    Assert.Empty(unionWith["pipeline"].AsBsonArray);
}

[Fact]
public void Union_appends_dollarRoot_dedup_group_and_replaceRoot()
{
    var stages = new MongoPipelineStage[]
    {
        new MongoUnionWithStage(new MongoPipelineStage[0], "customers", dedup: true)
    };
    var pipeline = MongoPipelineFactory.Create(stages, NewRenderer()).Build(NoParams());
    Assert.Equal(3, pipeline.Count);
    Assert.True(pipeline[0].Contains("$unionWith"));
    Assert.Equal("$$ROOT", pipeline[1]["$group"]["_id"].AsString);
    Assert.Equal("$_id", pipeline[2]["$replaceRoot"]["newRoot"].AsString);
}

[Fact]
public void Operand_predicate_renders_inside_the_union_pipeline()
{
    var operand = new MongoPipelineStage[] { new MongoMatchStage(/* field == const, as built elsewhere in this file */) };
    var stages = new MongoPipelineStage[] { new MongoUnionWithStage(operand, "customers", dedup: false) };
    var pipeline = MongoPipelineFactory.Create(stages, NewRenderer()).Build(NoParams());
    var innerPipeline = pipeline[0]["$unionWith"]["pipeline"].AsBsonArray;
    Assert.True(innerPipeline[0].AsBsonDocument.Contains("$match"));
}
```

> Implementer note: reuse the exact renderer/param helpers already present in `MongoPipelineFactoryTests.cs` (`NewRenderer`/`NoParams` are placeholders for whatever the file already uses). Add a test with a **parametrized** operand predicate asserting the nested `$match` contains a placeholder sentinel pre-`Build` and the substituted value post-`Build` — proving the shared placeholder table (mirror how the file tests outer-pipeline parameter substitution).

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test ...UnitTests... -c "Debug EF10" --filter "FullyQualifiedName~MongoPipelineFactoryTests"`
Expected: fail — `MongoPipelineFactory` throws `NativeTranslationNotSupportedException` for the unknown `MongoUnionWithStage` (the `RenderStage` default arm), or a compile error if the test references helpers not yet present.

- [ ] **Step 3: `Create` — handle a stage that expands to multiple docs**

In `MongoPipelineFactory.Create`, change the template build so the union stage can emit multiple documents while every existing stage stays one-doc (do NOT alter the other `RenderX` methods):

```csharp
public static MongoPipelineFactory Create(
    IReadOnlyList<MongoPipelineStage> stages,
    MongoQueryLanguageRenderer renderer)
{
    var placeholders = new PlaceholderTable();
    var template = new List<BsonDocument>(stages.Count);

    foreach (var stage in stages)
    {
        if (stage is MongoUnionWithStage unionWith)
            template.AddRange(RenderUnionWith(unionWith, renderer, placeholders));
        else
            template.Add(RenderStage(stage, renderer, placeholders));
    }

    return new MongoPipelineFactory(template, placeholders);
}
```
(`_template` is already `IReadOnlyList<BsonDocument>`, so a `List<BsonDocument>` is assignable — confirm the ctor/field type; if it is `BsonDocument[]`, call `template.ToArray()`.)

- [ ] **Step 4: Add `RenderUnionWith`**

```csharp
// Renders a $unionWith over the operand's nested pipeline into the SAME placeholder table (so a
// parameter inside the operand substitutes at Build time), then, for Union, the full-document dedup.
private static IEnumerable<BsonDocument> RenderUnionWith(
    MongoUnionWithStage stage,
    MongoQueryLanguageRenderer renderer,
    PlaceholderTable placeholders)
{
    var innerPipeline = new BsonArray();
    foreach (var operandStage in stage.OperandStages)
        innerPipeline.Add(RenderStage(operandStage, renderer, placeholders));   // shared placeholders

    yield return new BsonDocument("$unionWith", new BsonDocument
    {
        { "coll", stage.OperandCollectionName },
        { "pipeline", innerPipeline }
    });

    if (stage.Dedup)
    {
        yield return new BsonDocument("$group", new BsonDocument("_id", "$$ROOT"));
        yield return new BsonDocument("$replaceRoot", new BsonDocument("newRoot", "$_id"));
    }
}
```
(Operand stages are only `$match`/`$sort`/`$skip`/`$limit` — the guard forbids nested set-ops — so `RenderStage` handles them all.)

- [ ] **Step 5: Recurse `ValidatePagingStages` into the nested pipeline**

So an operand `Take(0)`/`Skip(-1)` throws the client-side `ArgumentOutOfRangeException` matching driver-LINQ. In `ValidatePagingStages`, after the existing top-level `$limit`/`$skip` checks, recurse into a `$unionWith` pipeline:

```csharp
if (stage.TryGetValue("$unionWith", out var unionWithValue)
    && unionWithValue.AsBsonDocument.TryGetValue("pipeline", out var innerPipeline))
{
    ValidatePagingStages(innerPipeline.AsBsonArray.Select(d => d.AsBsonDocument).ToArray());
}
```
(Add `using System.Linq;` if not present.)

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet build ... -c "Debug EF10" -v quiet` then `dotnet test ...UnitTests... -c "Debug EF10" --no-build --filter "FullyQualifiedName~MongoPipelineFactoryTests"`
Expected: PASS (existing + new, incl. the parametrized-operand and `Take(0)`-in-operand cases).

- [ ] **Step 7: Commit**

```bash
git add -A && git commit -m "EF-347: Factory RenderUnionWith (shared-placeholder nested pipeline + dedup + nested paging validation)"
```

---

### Task 4: QMTEV wiring — `TranslateUnion`/`TranslateConcat` + guards + dispatch + whitelist

This is the integration keystone: it makes Union/Concat go native end-to-end while preserving graceful fallback.

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs`
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeSlotPopulator.cs` (add `Union`/`Concat` to `IsNativeRepresentableSlotOperator`)
- Test: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/` (new set-ops functional test class — model on an existing native functional Query test, e.g. the `QueryModeGate*` or a Northwind-style class)

**Interfaces:**
- Consumes: `MongoSetOperation`, `IsSetOp` (Task 1).
- Produces: native Union/Concat translation; guard `TryTranslateSetOperation`.

**Critical wiring facts (confirmed in source):**
- The switch in `VisitMethodCall` (cases ~102–139) shares ONE body that calls `base.VisitMethodCall` → the `Translate*` overrides. `Union`/`Concat` are NOT currently in it, so they fall through and gracefully fall back (returning source1 + captured chain). `TranslateUnion`/`TranslateConcat` returning `null` is currently dead code.
- To go native, **add `Union`/`Concat` to that switch case group** so `base.VisitMethodCall` visits source2 and calls the overrides. Then the overrides MUST return a **non-null** shaped query always — native on success, source1-marked-non-native on guard-trip — because the shared body returns `NotTranslatedExpression` (hard fail) if the override returns `null`. This mirrors `TranslateGroupBy` exactly.
- Because `PopulateNativeSlots` (line ~153) runs after the switch for every operator, **add `Union`/`Concat` to `IsNativeRepresentableSlotOperator`** so its catch-all does not clobber the override's decision.

- [ ] **Step 1: Write the failing functional tests** (use the baseline decision from Task 1 Step 1)

Create a functional test class (model on an existing native Query functional test for fixture/context setup and the `AssertMql`/`AssertQuery`/`NativeOnly` helpers). Cover:

```csharp
// Native success under NativeOnly (proves it goes native)
[Fact] public async Task Union_whole_entity_goes_native() { /* set1.Where(a).Union(set2.Where(b)) under NativeOnly succeeds; results == expected */ }
[Fact] public async Task Concat_whole_entity_goes_native() { /* ...Concat... succeeds under NativeOnly; Concat keeps duplicates */ }

// Parity (if Task 1 found the driver implements it) OR expected-results (if it throws today)
[Fact] public async Task Union_matches_baseline() { /* Native results == DriverLinq results (or == expected) */ }

// Graceful fallback (throws under NativeOnly, succeeds under Native) for out-of-scope shapes
[Fact] public async Task Intersect_falls_back() { /* Intersect throws under NativeOnly */ }
[Fact] public async Task Projected_union_falls_back() { /* set1.Select(x=>new{...}).Union(...) throws under NativeOnly */ }
[Fact] public async Task Operand_with_include_falls_back() { /* ...Union(set2.Include(x=>x.Nav)) throws under NativeOnly */ }
```
Assert the emitted MQL for a native Union contains `$unionWith` and (for Union) `$group`/`$replaceRoot`; Concat contains `$unionWith` and NO `$group`. Follow the design's verification section for the exact parity-vs-expected choice.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet build ... -c "Debug EF10" -v quiet` then the new class filter. Expected: the native/NativeOnly cases fail (Union/Concat still fall back today, so `NativeOnly` throws instead of succeeding).

- [ ] **Step 3: Implement `TranslateUnion`/`TranslateConcat` + `TryTranslateSetOperation`**

Replace the two `=> null` bodies (`TranslateConcat` ~line 823, `TranslateUnion` ~line 1137):

```csharp
protected override ShapedQueryExpression? TranslateConcat(ShapedQueryExpression source1, ShapedQueryExpression source2)
    => TryTranslateSetOperation(source1, source2, MongoSetOperationKind.Concat);

protected override ShapedQueryExpression? TranslateUnion(ShapedQueryExpression source1, ShapedQueryExpression source2)
    => TryTranslateSetOperation(source1, source2, MongoSetOperationKind.Union);
```

Add the shared helper (near the other Translate helpers):

```csharp
// Native whole-entity, terminal Union/Concat -> a $unionWith on source1's select. ALWAYS returns a
// non-null shaped query (source1): native when both operands are plain natively-lowerable whole-entity
// selects of the same type, otherwise source1 marked non-native so the query falls back GRACEFULLY to
// driver-LINQ (throws only under NativeOnly). Never returns null (would become a hard NotTranslated in
// the VisitMethodCall switch body). Mirrors TranslateGroupBy's always-non-null contract.
private ShapedQueryExpression TryTranslateSetOperation(
    ShapedQueryExpression source1, ShapedQueryExpression source2, MongoSetOperationKind kind)
{
    var mongo1 = (MongoQueryExpression)source1.QueryExpression;
    var mongo2 = (MongoQueryExpression)source2.QueryExpression;

    if (IsPlainWholeEntitySelect(mongo1) && IsPlainWholeEntitySelect(mongo2)
        && mongo1.CollectionExpression.EntityType == mongo2.CollectionExpression.EntityType)
    {
        mongo1.Select.SetOperation = new MongoSetOperation(kind, mongo2.Select, mongo2.CollectionExpression.CollectionName);
        mongo1.Select.IsSetOp = true;
    }
    else
    {
        mongo1.Select.MarkNotNativelyRepresentable();
    }

    // Same-entity-type both sides -> source1's whole-entity shaper materializes every union row.
    return source1;
}

// A plain whole-entity select: filter/sort/paging slots only — no projection, grouping, scalar
// cardinality, its own set op, cross-collection lookups (Include), or a lifted-out VectorSearch.
private static bool IsPlainWholeEntitySelect(MongoQueryExpression mongo)
    => mongo.Select.Route == NativeRoute.WholeEntity
       && mongo.Select.SetOperation == null
       && !mongo.Select.IsSetOp
       && mongo.Select.Grouping == null
       && mongo.Select.Cardinality == null
       && mongo.Select.Projection.Count == 0
       && !mongo.IsJoinQuery
       && mongo.Lookups.Count == 0
       && !ContainsVectorSearch(mongo.CapturedExpression);
```

`ContainsVectorSearch` is currently a `private static` in `MongoShapedQueryCompilingExpressionVisitor`. Add a small equivalent in this visitor (walk the captured chain for `call.IsVectorSearch()` via the existing `MongoQueryableExtensions` helper) — do NOT make the gate's private method public. Keep it local and minimal (a 6-line static mirroring the gate's walk). Note this duplication in the task report (the design flags a future unification).

- [ ] **Step 4: Add the dispatch cases + whitelist entries**

In `VisitMethodCall`'s switch, add to the case group that shares the `base.VisitMethodCall` body (alongside `Select`/`OfType`/`Distinct` in the "operations that need tweaks" group):

```csharp
case nameof(Queryable.Union) when methodDefinition == QueryableMethods.Union:
case nameof(Queryable.Concat) when methodDefinition == QueryableMethods.Concat:
```

In `NativeSlotPopulator.IsNativeRepresentableSlotOperator`, add:

```csharp
   || methodDefinition == QueryableMethods.Union
   || methodDefinition == QueryableMethods.Concat
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet build ... -c "Debug EF10" -v quiet` then the new functional class filter, plus the gate/native regression filters:
```bash
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/...FunctionalTests.csproj -c "Debug EF10" --no-build --filter "FullyQualifiedName~SetOp|FullyQualifiedName~QueryModeGate"
```
Expected: PASS — native Union/Concat succeed under `NativeOnly` with the right MQL; out-of-scope shapes throw under `NativeOnly`; parity/expected holds.

- [ ] **Step 6: Commit**

```bash
git add -A && git commit -m "EF-347: Native Union/Concat translation ($unionWith) + graceful-fallback guard + dispatch"
```

---

### Task 5: Composition-seam regression tests + AGENTS.md + full verification

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md`
- Test: the functional set-ops class (add composition-seam cases)

- [ ] **Step 1: Composition-seam regression tests (the recurring post-terminal hazard)**

Add cases asserting each operator AFTER a union falls back gracefully (throws under `NativeOnly`, correct results under `Native`), locking the `IsSetOp` → `HasTerminalOperator` guard:

```csharp
[Fact] public async Task Where_after_union_falls_back() { /* set1.Union(set2).Where(p) throws under NativeOnly; Native == expected */ }
[Fact] public async Task OrderBy_after_union_falls_back() { }
[Fact] public async Task Skip_take_after_union_falls_back() { }
[Fact] public async Task Count_after_union_falls_back() { }
[Fact] public async Task Chained_union_falls_back() { /* set1.Union(set2).Union(set3) */ }
[Fact] public async Task GroupBy_after_union_falls_back() { }
```
Also: a **parametrized operand predicate** end-to-end test (`set1.Union(set2.Where(c => c.X == p))` with `p` a captured variable) asserting correct results — exercises the shared placeholder table through real execution.

- [ ] **Step 2: Run the seam tests**

Run the functional class filter under EF10. Expected: PASS.

- [ ] **Step 3: Update `Query/AGENTS.md`**

Add a set-ops paragraph to the as-built scope: native whole-entity terminal `Union`/`Concat` → `$unionWith` (+ `$$ROOT` dedup for Union); the `MongoSetOperation` IR + `MongoUnionWithStage` + shared-placeholder nested-pipeline rendering; `IsSetOp` joins the renamed `HasTerminalOperator` post-terminal guard (document the invariant that set-ops are terminal-only and every post-terminal entry point gates on `HasTerminalOperator`). List the deferred shapes (Intersect/Except, projected, post-union composition/chaining, SelectMany). Update the `IsNativeRepresentableSlotOperator` pitfall list to include `Union`/`Concat`. Keep edits factual and scoped.

- [ ] **Step 4: Commit the doc + tests**

```bash
git add -A && git commit -m "EF-347: Set-ops composition-seam regression tests + AGENTS.md"
```

- [ ] **Step 5: Full verification (controller-run; no commit)**

- Full 3-version `/test-all` green (EF8/EF9/EF10).
- `MONGODB_EF_NATIVE_ONLY=1` spec sweep vs the EF-334 tip `7d00914` baseline: expect the same failures MINUS the set-op tests that now pass, and ZERO new failures/regressions.
- Report the counts.

---

## Self-Review

**1. Spec coverage:** IR + `HasTerminalOperator` (Task 1); `$unionWith` stage + lowerer + operand recursion (Task 2); render + shared placeholders + dedup + nested paging validation (Task 3); QMTEV native wiring + guards + graceful fallback + dispatch/whitelist (Task 4); composition-seam guard tests + docs + full verification (Task 5). Driver-baseline contingency → Task 1 Step 1. Non-goals enforced by `IsPlainWholeEntitySelect` (Task 4) and the `HasTerminalOperator` guard (Task 1). ✓

**2. Placeholder scan:** Test bodies reference file-local helpers (`NewRenderer`/`NoParams`/lowerer construction) with explicit implementer notes to match the existing test file rather than invent names — this is deliberate (those helpers already exist and differ per file), not a spec gap. All production code steps show complete code. No "TBD"/"handle edge cases". ✓

**3. Type consistency:** `MongoSetOperation(MongoSetOperationKind, MongoSelectDefinition, string)`, `MongoSetOperationKind {Concat, Union}`, `MongoSelectDefinition.SetOperation`/`IsSetOp`/`HasTerminalOperator`, `MongoUnionWithStage(IReadOnlyList<MongoPipelineStage>, string, bool)` with `OperandStages`/`OperandCollectionName`/`Dedup`, `TryTranslateSetOperation`/`IsPlainWholeEntitySelect` — used identically across tasks. `RenderUnionWith` returns `IEnumerable<BsonDocument>`; `Create` flattens via `AddRange`. ✓
