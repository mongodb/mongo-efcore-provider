# Native Flat Collection Includes (SP5) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the native aggregation-pipeline path emit a single-level collection `$lookup` (array, no `$unwind`) and materialize it via the DOM shaper, so `Include(x => x.Orders)` and projected `Select(x => x.Orders.Count)` no longer fall back to driver-LINQ.

**Architecture:** Single-level reference Include is already native (SP1: `MongoSelectLowerer.AppendLookupStages` emits `$lookup`+`$unwind` for `IsStreamableReference` lookups). Collection lookups hit the `!IsStreamableReference` guard and throw, so `TryBuildNativeFactory` returns `null` and the query falls back. This plan adds a parallel `IsNativeCollectionLookup` case that emits a `$lookup` with **no** `$unwind` (the collection stays an array under `_lookup_<Nav>`), and reuses the existing DOM collection materializer (`MongoProjectionBindingRemovingExpressionVisitor.IncludeCollection`) — which already reads `_lookup_<Nav>` on the driver-LINQ path — to shape the array. Streaming stays off (collection navs are `StreamingEligibility`-ineligible), so this is DOM-only. Projected `.Count` reuses the existing `InjectAfterRoot` lookup registration plus a new `$size` native projection. Folds in EF-334 (centralize/generalize the out-of-`Route` lookup gate condition).

**Tech Stack:** C#, EF Core (EF8/EF9/EF10 configs), MongoDB C# driver, xUnit (plain `Assert.*` in unit tests; `AssertMql` in functional/spec tests).

## Global Constraints

- Preserve file BOMs; `<Nullable>enable</Nullable>` on all `src/` code — annotate new members.
- Multi-EF: build/test under `Debug EF10` during development; **all three** (`EF8`/`EF9`/`EF10`) must be green before the final commit. Use `#if EF8 || EF9` / `#if !EF8` only where a signature actually differs.
- Native default and changed emitted MQL are **not** breaking changes (versioning rubric); query **results** must be unchanged.
- The **only** reliable "goes native" signal is `MongoQueryMode.NativeOnly`: native-capable ⇒ succeeds; fallback ⇒ throws `NativeTranslationNotSupportedException`. MQL shape alone does not prove native for structurally-identical fallback pipelines.
- Do **not** add these operators to the QMTEV `VisitMethodCall` switch (Include is handled via nav-expansion/projection binding, not slot population).
- Out of scope (must still throw under `NativeOnly` / fall back under `Native`): nested/transitive `ThenInclude`, filtered Include, collection-of-collection, EF-317 `$lookup` string-match cleanup. Do not touch `StreamingEligibility`'s collection rejection — streaming of collection arrays is deferred to SP7.
- Commit style: one commit per task during development; the branch is squashed to a single PR-style commit at the end (kept as `EF-339`, stacked on `b74271a`). Keep an `EF-339-presquash` safety branch until merge.

---

## File Structure

- `src/MongoDB.EntityFrameworkCore/Query/Expressions/LookupExpression.cs` — add `IsNativeCollectionLookup` predicate (Task 1).
- `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoSelectLowerer.cs` — `AppendLookupStages`: emit collection `$lookup` with no `$unwind` (Task 1).
- `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/` — gate + materialization tests (Tasks 1, 2, 4). Mirror the existing `QueryModeGate*` and Include functional tests for exact fixture/helper signatures.
- `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoShapedQueryCompilingExpressionVisitor.cs` — generalize/centralize the lookup gate condition (Task 3, EF-334).
- `src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoSelectDefinition.cs` — Task 3 fold target (if state is surfaced here).
- Native projected `.Count` (`$size`) — Task 4:
  - `src/MongoDB.EntityFrameworkCore/Query/Expressions/Mongo*Expression.cs` — a `MongoSizeExpression` node (or reuse a unary) for `$size` over a field ref.
  - `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoAggregationExpressionRenderer.cs` — render `$size`.
  - `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeProjectionBinder.cs` — recognize the projected collection-count subtree; emit the `$size` projection over `_lookup_<Nav>`.
  - `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoSelectLowerer.cs` — ensure the `InjectAfterRoot` collection lookup is emitted for the count.
- `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md` — as-built scope update (Task 5).

---

## Task 1: Emit a flat collection `$lookup` (no `$unwind`) in the native lowerer

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Expressions/LookupExpression.cs` (after `IsStreamableReference`, ~line 130)
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoSelectLowerer.cs:124-150` (`AppendLookupStages`)
- Test: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/` — a new gate test mirroring the existing `QueryModeGate*` class (find it with `grep -rl "NativeOnly" tests/.../FunctionalTests/Query`)

**Interfaces:**
- Produces: `LookupExpression.IsNativeCollectionLookup` (bool) — consumed by the lowerer in this task and by Task 4's count emission.

- [ ] **Step 1: Write the failing test**

In the gate test class (mirror the sibling `NativeOnly` Include tests for the exact `DbContext`/model helpers — the Northwind-style model with `Customer.Orders` is already used by existing Include tests):

```csharp
[Fact]
public void Single_level_collection_Include_runs_native()
{
    using var db = /* mirror sibling: context configured with UseQueryMode(MongoQueryMode.NativeOnly) */;

    // Should NOT throw NativeTranslationNotSupportedException under NativeOnly.
    var results = db.Customers.Include(c => c.Orders).ToList();

    Assert.NotEmpty(results);
    Assert.Contains(results, c => c.Orders.Count > 0);
}

[Fact]
public void Single_level_collection_Include_emits_lookup_without_unwind()
{
    using var db = /* mirror sibling: context with UseQueryMode(MongoQueryMode.Native) + AssertMql capture */;

    var _ = db.Customers.Include(c => c.Orders).ToList();

    // A collection $lookup produces an array field; there must be NO $unwind on _lookup_Orders.
    AssertMql(/* the captured pipeline */,
        expectedContains: "\"$lookup\"",
        expectedNotContains: "\"$unwind\": { \"path\": \"$_lookup_Orders\"");
    // (Mirror the exact AssertMql signature used by sibling tests.)
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --filter "FullyQualifiedName~Single_level_collection_Include"`
Expected: FAIL — `Single_level_collection_Include_runs_native` throws `NativeTranslationNotSupportedException` ("only single-level reference includes").

- [ ] **Step 3: Add `IsNativeCollectionLookup` to `LookupExpression`**

Insert after `IsStreamableReference` (after line 130):

```csharp
    /// <summary>
    /// A single-level collection Include the native pipeline can emit as a <c>$lookup</c> array (no
    /// <c>$unwind</c>) and the DOM collection materializer can read back from a root-level
    /// <c>_lookup_&lt;Nav&gt;</c> field: a collection nav, no filtered-Include pipeline stages, not
    /// force-unwound (an explicit Join is not a collection Include), and un-prefixed — its <see cref="As"/>
    /// equals the plain <c>_lookup_&lt;Nav&gt;</c> alias, which excludes the driver-LeftJoin
    /// (<c>_outer</c>/<c>_inner</c>) and flat-nested (<c>_lookup_&lt;Nav&gt;._lookup_&lt;Coll&gt;</c>) shapes
    /// that remain fallback-only in this sub-project.
    /// </summary>
    public bool IsNativeCollectionLookup
        => Navigation.IsCollection
           && !HasPipeline
           && !ForceUnwind
           && As == GetLookupAlias(Navigation);
```

- [ ] **Step 4: Emit the collection `$lookup` (no `$unwind`) in the lowerer**

Replace the `foreach` body in `AppendLookupStages` (lines 136-149):

```csharp
        foreach (var lookup in lookups)
        {
            if (lookup.IsStreamableReference)
            {
                stages.Add(new MongoLookupStage(lookup));
                stages.Add(new MongoUnwindStage(lookup));
            }
            else if (lookup.IsNativeCollectionLookup)
            {
                // Collection Include: keep the joined documents as an array under _lookup_<Nav>
                // (no $unwind). The DOM collection materializer reads the array back and runs the
                // IncludeCollection fixup, exactly as on the driver-LINQ path.
                stages.Add(new MongoLookupStage(lookup));
            }
            else
            {
                throw new NativeTranslationNotSupportedException(
                    $"Native pipeline does not support lookup for navigation '{lookup.Navigation.Name}' " +
                    "(only single-level reference and single-level collection includes).");
            }
        }
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --filter "FullyQualifiedName~Single_level_collection_Include"`
Expected: PASS. If `_runs_native` passes but materialization is wrong, that's Task 2's concern — this task only proves the pipeline is emitted and accepted natively.

- [ ] **Step 6: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/Expressions/LookupExpression.cs \
        src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoSelectLowerer.cs \
        tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/
git commit -m "EF-339: Emit flat collection \$lookup (no \$unwind) in native lowerer"
```

---

## Task 2: Materialize flat collection Include via the native DOM shaper (end-to-end)

**Files:**
- Test: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/` — extend the Include materialization functional tests.
- Modify (only if a gap surfaces): `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoProjectionBindingRemovingExpressionVisitor.cs` (`IncludeCollection` / `AddInclude`).

**Interfaces:**
- Consumes: the native collection `$lookup` from Task 1. The DOM path is chosen automatically once `TryBuildNativeFactory` succeeds (`nativeFactory != null`) and `streaming` is `false` (collection navs are `StreamingEligibility`-ineligible). No gate change is required for native selection — `AllPendingLookupsAreStreamableReferences` gates streaming only.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public void Native_collection_Include_materializes_arrays_tracking()
{
    using var db = /* Native mode, tracking */;
    var customers = db.Customers.Include(c => c.Orders).OrderBy(c => c.CustomerId).ToList();

    var withOrders = customers.Single(c => c.CustomerId == "KNOWN_ID_WITH_ORDERS");
    Assert.Equal(EXPECTED_ORDER_COUNT, withOrders.Orders.Count);

    var noOrders = customers.Single(c => c.CustomerId == "KNOWN_ID_NO_ORDERS");
    Assert.NotNull(noOrders.Orders);   // empty collection still initialized
    Assert.Empty(noOrders.Orders);
}

[Fact]
public void Native_collection_Include_materializes_arrays_no_tracking()
{
    using var db = /* Native mode */;
    var customers = db.Customers.AsNoTracking().Include(c => c.Orders).ToList();
    Assert.Contains(customers, c => c.Orders.Count > 0);
}
```

Use the exact known ids / counts from the existing Include functional tests' seed data (grep the sibling test for the fixture and reuse its constants).

- [ ] **Step 2: Run tests to verify they fail (or pass)**

Run: `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --filter "FullyQualifiedName~Native_collection_Include_materializes"`
Expected: Most likely **PASS** already — the DOM collection materializer (`IncludeCollection`) reads `_lookup_<Nav>` and the native pipeline now produces that array (Task 1). If they FAIL with a wrong shape / missing array, diagnose per Step 3.

- [ ] **Step 3: Fix any read-back gap (only if Step 2 failed)**

If the array is not read back, confirm the native document field name matches the shaper's expectation: the native `$lookup` writes to `LookupExpression.As` (`_lookup_<Nav>` for the un-prefixed flat case), and the driver-LINQ binding (`MongoProjectionBindingExpressionVisitor` IncludeExpression collection branch) built the `CollectionShaperExpression` reading the same `_lookup_<Nav>` alias (since `UsesDriverJoinFields` is `false` for a lone collection Include, no `_outer`/`_inner` prefix is applied). Align the read site in `MongoProjectionBindingRemovingExpressionVisitor` to the native alias if and only if a divergence is observed. Do not restructure the shaper.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --filter "FullyQualifiedName~Native_collection_Include_materializes"`
Expected: PASS.

- [ ] **Step 5: Add nested/filtered stay-fallback regression tests**

```csharp
[Fact]
public void Nested_ThenInclude_still_falls_back()
{
    using var db = /* NativeOnly */;
    Assert.Throws<NativeTranslationNotSupportedException>(
        () => db.Customers.Include(c => c.Orders).ThenInclude(o => o.OrderDetails).ToList());
}

[Fact]
public void Filtered_Include_still_falls_back()
{
    using var db = /* NativeOnly */;
    Assert.Throws<NativeTranslationNotSupportedException>(
        () => db.Customers.Include(c => c.Orders.Where(o => o.Freight > 0)).ToList());
}
```

Run: `dotnet test ... --filter "FullyQualifiedName~still_falls_back"` → Expected: PASS (these still throw under `NativeOnly`).

- [ ] **Step 6: Commit**

```bash
git add tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/ src/MongoDB.EntityFrameworkCore/Query/Visitors/
git commit -m "EF-339: Materialize flat collection Include via native DOM shaper"
```

---

## Task 3: EF-334 — centralize/generalize the out-of-`Route` lookup gate condition

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoShapedQueryCompilingExpressionVisitor.cs:631-644` (`AllPendingLookupsAreStreamableReferences`) and its call site (lines 308-312).

**Interfaces:**
- Consumes: the lookup state on `MongoQueryExpression` (`GetStreamingReferenceLookups`, `IsJoinQuery`, `InnerCollections`).
- Produces: a single, correctly-named streamability predicate; the streaming decision reads it.

Context: after Task 1, a collection-Include query goes native-**DOM** with no gate change (streaming is already off because collection navs are `StreamingEligibility`-ineligible). The EF-334 fold here is a **naming/centralization** cleanup, not a behavior change: the predicate `AllPendingLookupsAreStreamableReferences` is a *streaming* gate (it decides streaming-vs-DOM, alongside `StreamingEligibility`), not a native-vs-driver gate. Make that explicit and centralize the streaming decision.

- [ ] **Step 1: Write the failing/guard test**

Add a test asserting the behavior is unchanged after the rename (a query with a collection Include is native-DOM, not streaming; a plain reference Include still streams). Mirror any existing streaming-vs-DOM assertion in the gate test class. If none exists, assert via `NativeOnly` success for both shapes and correct results.

```csharp
[Fact]
public void Collection_Include_is_native_but_not_streaming()
{
    using var db = /* NativeOnly */;
    // Succeeds (native), and results are correct — proves native-DOM, not fallback.
    var customers = db.Customers.Include(c => c.Orders).ToList();
    Assert.Contains(customers, c => c.Orders.Count > 0);
}
```

- [ ] **Step 2: Run to verify current state**

Run: `dotnet test ... --filter "FullyQualifiedName~Collection_Include_is_native_but_not_streaming"`
Expected: PASS (from Task 1/2). This test pins behavior so the refactor can't regress it.

- [ ] **Step 3: Rename + document the predicate**

Rename `AllPendingLookupsAreStreamableReferences` → `AllPendingLookupsAreStreamable`, update its call site (lines 308-312) and its XML doc to state plainly: this is the **streaming** gate — it returns `true` only when every join is a single-level *reference* lookup the streaming reader can read back; a native *collection* lookup is deliberately **not** streamable and takes the native-DOM path. Add a one-line comment at the `streaming = ...` expression noting that native-vs-driver is decided upstream by `TryBuildNativeFactory` (lowerer), and this predicate only gates streaming-vs-DOM.

Keep the body identical (still `referenceLookups.All(l => l.IsStreamableReference)`), since a collection lookup must stay non-streamable.

- [ ] **Step 4: Run the full gate + Include suite to verify no behavior change**

Run: `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --filter "FullyQualifiedName~Include|FullyQualifiedName~QueryModeGate"`
Expected: PASS, no change in pass set.

- [ ] **Step 5: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoShapedQueryCompilingExpressionVisitor.cs
git commit -m "EF-339: EF-334 centralize/clarify streaming lookup gate predicate"
```

> Note: full relocation of the `$lookup` streamability state onto `MongoSelectDefinition` (so `Route` owns all three conditions) remains the residual of EF-334; it is entangled with the fallback shaper's `_outer`/`_inner` state and is deferred with EF-317. Record this in the commit message / AGENTS.md update.

---

## Task 4: Native projected collection `.Count` (`$size`)

**Files:**
- Create/Modify: `src/MongoDB.EntityFrameworkCore/Query/Expressions/` — a `MongoSizeExpression` node (field-ref → array size).
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoAggregationExpressionRenderer.cs` — render `$size`.
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeProjectionBinder.cs` — recognize the projected collection-count subtree and bind a `$size` projection over `_lookup_<Nav>`.
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoSelectLowerer.cs` — ensure the `InjectAfterRoot` collection lookup (registered by `TryBindProjectedCollectionNavigationCount`) is emitted before the `$project` that reads its `$size`.
- Test: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/`.

**Interfaces:**
- Consumes: `LookupExpression.IsNativeCollectionLookup` (Task 1 — the `InjectAfterRoot` count lookup is a collection lookup and must satisfy it), `LookupExpression.InjectAfterRoot`, `LookupExpression.GetLookupAlias`.
- Produces: `MongoSizeExpression` (a `MongoExpression` subtype wrapping a `MongoFieldExpression` for the `_lookup_<Nav>` array).

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public void Projected_collection_Count_runs_native()
{
    using var db = /* NativeOnly */;
    var counts = db.Customers.Select(c => new { c.CustomerId, OrderCount = c.Orders.Count }).ToList();
    Assert.Contains(counts, x => x.OrderCount > 0);
}

[Fact]
public void Projected_collection_Count_emits_size_over_lookup()
{
    using var db = /* Native mode + AssertMql */;
    var _ = db.Customers.Select(c => new { c.CustomerId, OrderCount = c.Orders.Count }).ToList();
    AssertMql(/* captured */, expectedContains: "\"$lookup\"", andContains: "\"$size\"");
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test ... --filter "FullyQualifiedName~Projected_collection_Count"`
Expected: FAIL — `_runs_native` throws `NativeTranslationNotSupportedException` (the count projection is not yet natively bound, so `Route` is `Fallback` or the lowerer can't emit `$size`).

- [ ] **Step 3: Add the `MongoSizeExpression` node**

Create the node in `Expressions/` mirroring the existing `MongoUnaryExpression`/`MongoFieldExpression` file conventions (BOM, `Nullable`, `internal sealed`):

```csharp
/// <summary>
/// Represents the aggregation-expression <c>{ $size: "$field" }</c> — the element count of an array
/// field (used for projected collection-navigation counts, e.g. <c>c.Orders.Count</c> over the
/// <c>_lookup_&lt;Nav&gt;</c> array). Renders only in the aggregation-expression dialect.
/// </summary>
internal sealed class MongoSizeExpression : MongoExpression
{
    public MongoSizeExpression(MongoExpression operand, Type type) : base(type)
        => Operand = operand;

    /// <summary>The array field whose size is taken (a <see cref="MongoFieldExpression"/>).</summary>
    public MongoExpression Operand { get; }
}
```

(Match the exact `MongoExpression` base constructor signature — check `MongoUnaryExpression` for the `Type`/`base(...)` shape.)

- [ ] **Step 4: Render `$size` in the aggregation-expression renderer**

In `MongoAggregationExpressionRenderer`, add a case in the node dispatch for `MongoSizeExpression`:

```csharp
            MongoSizeExpression size
                => new BsonDocument("$size", Render(size.Operand)),
```

(Use the renderer's existing recursive `Render`/`RenderNode` entry point name for `size.Operand`.)

- [ ] **Step 5: Bind the projected count natively**

In `NativeProjectionBinder`, when populating the projection, recognize the projected collection-count leaf. The driver-LINQ path leaves it as the original `Queryable.Count(DbSet.Where(fkEquality))` `MethodCallExpression` in the projection mapping (see `TryBindProjectedCollectionNavigationCount`) and registers a `LookupExpression { InjectAfterRoot = true }` via `AddLookup`. Reuse that registration: when a projection member's bound expression is that count subtree AND a matching `InjectAfterRoot` collection `LookupExpression` was registered for the navigation, emit a `MongoProjection` entry mapping the alias to `new MongoSizeExpression(new MongoFieldExpression(LookupExpression.GetLookupAlias(navigation), ...), typeof(int/long))`. If the subtree is not recognized, call `MarkNotNativelyRepresentable()` (fall back) — do not silently drop it.

- [ ] **Step 6: Ensure the `InjectAfterRoot` lookup is emitted**

Confirm `AppendLookupStages` emits the `InjectAfterRoot` collection lookup (it satisfies `IsNativeCollectionLookup` — collection, no pipeline, not force-unwound, un-prefixed `As`). Because the native canonical order already places `$lookup` **before** `$project` (`MongoSelectLowerer.Lower` step 5 then step 6), the `$size` in the `$project` reads an already-present array — no special `InjectAfterRoot` placement is needed on the native path (unlike driver-LINQ). Add a code comment stating this. If the count query also has a user `$match` that references the array (out of scope), it must still fall back — verify it does.

- [ ] **Step 7: Run tests to verify they pass**

Run: `dotnet test ... --filter "FullyQualifiedName~Projected_collection_Count"`
Expected: PASS.

- [ ] **Step 8: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/ tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/
git commit -m "EF-339: Native projected collection Count via \$size over \$lookup array"
```

---

## Task 5: Full regression sweep + documentation

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md` (as-built scope note).

- [ ] **Step 1: Run the full query suite on EF10**

Run: `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --filter "FullyQualifiedName~Query"`
Expected: PASS.

- [ ] **Step 2: Run the native-only spec sweep**

Run: `MONGODB_EF_NATIVE_ONLY=1 dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests -c "Debug EF10"`
Expected: **+ passing (collection-Include spec tests now native), zero regressions** vs the pre-branch baseline. Record the delta count.

- [ ] **Step 3: Run the full suite on EF8 and EF9**

Run: `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF8"` then `-c "Debug EF9"` (or invoke the `/test-all` skill).
Expected: PASS on all three.

- [ ] **Step 4: Update `Query/AGENTS.md`**

Update the as-built scope note: single-level **collection** Include and projected collection `.Count` are now native (DOM); reference Include is native via the streaming path (correct the stale "reference Include is deferred/dormant" prose). State that nested/transitive `ThenInclude`, filtered Include, collection-of-collection, and collection-array **streaming** remain fallback/deferred (SP5b / SP7), and that EF-317 + the residual EF-334 state-relocation are follow-ups.

- [ ] **Step 5: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/AGENTS.md
git commit -m "EF-339: Update Query AGENTS.md as-built scope for collection Includes"
```

---

## Self-Review notes (for the executor)

- **Spec coverage:** flat collection Include (Tasks 1-2), projected `.Count` (Task 4), DOM-only/no-streaming (Tasks 2-3), EF-334 fold (Task 3), out-of-scope stays-fallback (Task 2 Step 5). EF-317 explicitly deferred.
- **Test harness signatures are not verbatim.** The functional-test bodies above are representative — the exact `DbContext`/fixture/`AssertMql` signatures must be **mirrored from the adjacent existing tests** (the `QueryModeGate*` classes and the existing Include functional tests). Read the sibling test before writing each test; reuse its fixture, seed constants (known customer ids / order counts), and `AssertMql` overload.
- **Key insight — no gate change needed for native selection.** Collection Include goes native-DOM the moment the lowerer stops throwing (Task 1), because native-vs-driver is decided by `TryBuildNativeFactory` (the lowerer), and streaming is independently gated off by `StreamingEligibility` (collection navs ineligible). Task 3 (EF-334) is therefore a clarify/rename, not a behavior change — do not accidentally make collection lookups "streamable."
- **`IsNativeCollectionLookup` discriminator** is `As == GetLookupAlias(Navigation)`: this un-prefixed check is what excludes the nested `_outer`/`_inner` and flat `_lookup_<Nav>._lookup_<Coll>` shapes (which keep their prefixed `As`) — those must stay fallback.
- **Type consistency:** `IsNativeCollectionLookup` (bool), `MongoSizeExpression.Operand` (`MongoExpression`), `LookupExpression.GetLookupAlias` (static, `_lookup_<Nav>`). Verify the `MongoExpression` base ctor and the renderer's recursive entry-point name against current source before compiling.
