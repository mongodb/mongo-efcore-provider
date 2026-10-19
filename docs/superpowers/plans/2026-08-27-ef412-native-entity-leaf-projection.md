# EF-412: Native entity leaf inside a projection — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `Select(c => new { c, Total = c.Age * c.Score })` — a whole-entity leaf mixed with a computed/scalar sibling — go fully native (`MongoSelectDefinition.Route == NativeRoute.Projection`, server-side `$project`), instead of permanently routing to the driver-LINQ/DOM fallback shaper the way it does today.

**Architecture:** Emit the entity leaf as a `$$ROOT`-valued field in the native `$project` stage (reusing the existing dialect-neutral `MongoElementRefExpression` node, which already renders `Path` as `"$" + Path`, so `Path = "$ROOT"` renders `"$$ROOT"` for free). On the read side, register the resulting nested sub-document as the entity's own document context in `MongoProjectionBindingRemovingExpressionVisitor`'s `_projectionBindings` map — the exact mechanism the collection-Include array case already uses to give a nested shaper its own document scope — so EF Core's own (unmodified) entity materializer reads the entity's members from that nested document instead of the top-level one.

**Tech Stack:** C#, MongoDB Aggregation Pipeline ($project, $$ROOT), EF Core query pipeline extension points (`StructuralTypeShaperExpression`, `ProjectionBindingExpression`, `EntityProjectionExpression`).

**Spec:** No separate spec doc — this plan *is* the design; Task 0 is a spike that validates the design's one open risk before the rest of the plan is trusted.

## Global Constraints

- Scope is the **ROOT entity leaf only** (the query's own root, e.g. `c` in `Select(c => new { c, ... })`). A navigation-entity leaf (`Select(o => new { o.Customer, o.Total })`) is explicitly OUT of scope — it needs a different alias (the nav's own field path, not `$$ROOT`) and different read-side owner-mapping; file a follow-up ticket for it once this lands, do not fold it in here.
- `EF-356` ("mixed whole-entity + computed-arithmetic projection returns silently wrong values") is **already fixed** on the fallback path (commit `fc7117bb`). This plan must not regress that fix's correctness — only move the same shape from fallback to native. `NativeComputedProjectionTests.Mixed_whole_entity_and_computed_leaf_returns_the_correct_computed_value` is the existing regression test; it must keep passing, and gains a new assertion that the query is native (see Task 4).
- **RULED (post-Task-1) on the green-suite bar's granularity:** Tasks 1-3 are one vertical slice (emit → bind → read) of a single mechanism; Task 1 alone (emit side only) is EXPECTED to leave the binder admitting a shape the read side can't yet materialize, so a temporary batch of end-to-end failures between Task 1 and Task 3 is normal, not a regression — Task 1's own implementer measured and explained exactly this (10 end-to-end failures, all in the target shape, zero elsewhere). The 0-failures bar is enforced at the Task 3 boundary (once emit+bind+read all land) and again at Task 4/5, not after Task 1 or Task 2 individually. A Task 1/2 review should confirm the failure set is limited to the target shape (no unrelated regression) rather than demanding zero failures outright. Build all three EF configs (`EF8`/`EF9`/`EF10`) before considering the plan complete — this repo ships across three EF majors from one source tree.
- Do not widen the `NativeProjectionBinder.TryTranslateLeaf` gate beyond exactly `StructuralTypeShaperExpression` whose `StructuralType` is the query's own root `IEntityType` — a wider match risks silently admitting a nav-entity or TPH-derived-type leaf this plan has not validated.
- This repo's own convention (see `Query/AGENTS.md` and prior slices EF-401/EF-405/EF-331): **a decomposition/size estimate from a status doc is unreliable until proven on a prototype.** Task 0 exists specifically to de-risk this before Tasks 1-5 are trusted at face value.

---

### Task 0: Spike — prove the mechanism on one shape

**Files:**
- Modify (throwaway/experimental, revert or keep behind a flag by end of task): `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeProjectionBinder.cs`, `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoProjectionBindingExpressionVisitor.cs`, `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoProjectionBindingRemovingExpressionVisitor.cs`
- Test: a scratch xunit test targeting exactly `Select(c => new { c, Total = c.Age * c.Score })` (copy the body of `NativeComputedProjectionTests.Mixed_whole_entity_and_computed_leaf_returns_the_correct_computed_value`, but assert `NativeOnly` succeeds rather than throws)

**Purpose:** Confirm three specific claims before committing to the full task breakdown. Each corresponds to a concrete code location already located during design:

1. **Emit side is representable today.** `MongoElementRefExpression` (`src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoElementRefExpression.cs`) renders in `MongoAggregationExpressionRenderer.cs:57` as `FieldRef(elementRef.Path, elementVariable)` → `"$" + Path` when `elementVariable` is `null`. Confirm `new MongoElementRefExpression("$ROOT", entityType.ClrType)` really does render the `$project` stage as `{"c": "$$ROOT", ...}` (dump the built `BsonDocument[]` pipeline in the test, e.g. via `MongoQueryFilterExtensions`/`AssertMql`-style pipeline capture, or a debugger breakpoint in `MongoPipelineFactory.RenderProject`).
2. **Bind-side registration point exists.** `MongoProjectionBindingExpressionVisitor.VisitExtension`'s `case StructuralTypeShaperExpression structuralTypeShaperExpression:` (currently at `MongoProjectionBindingExpressionVisitor.cs:266-287`) unconditionally builds via `EntityProjectionExpression`/`_queryExpression.Projection[]` (EF's own root-document-relative entity binding), regardless of `Select.Route`. Confirm adding a `when _queryExpression.Select.Route == NativeRoute.Projection && structuralTypeShaperExpression.StructuralType == mongoQ.CollectionExpression.EntityType` arm ABOVE the existing case — mirroring the already-shipped arithmetic-leaf pattern at `MongoProjectionBindingExpressionVisitor.cs:176-181` (`_projectionMapping[currentProjectionMember] = ...; return new ProjectionBindingExpression(...)`) — lets `NativeProjectionBinder.TryTranslateLeaf` see and admit this leaf on the SAME pass that already admits the `Total` arithmetic sibling (verify ordering: does `NativeProjectionBinder` run before or after this visitor executes today? Read `MongoQueryableMethodTranslatingExpressionVisitor`'s call sequence to confirm — the arithmetic/cast leaf guards already depend on `Route` being set at bind time, so it should already be established, but confirm rather than assume).
3. **Read-side registration point works.** `MongoProjectionBindingRemovingExpressionVisitor`'s `CollectionShaperExpression` case (`MongoProjectionBindingRemovingExpressionVisitor.cs` — the block that sets `_projectionBindings[accessExpression] = jObjectParameter` and `_ownerMappings[accessExpression] = (...)` *before* calling `Visit(collectionShaperExpression.InnerShaper)`) is the existing precedent for "give a nested shaper its own document scope." Confirm the analogous move — read the alias's BsonDocument sub-value via `BsonBinding.CreateGetValueExpression(DocParameter, alias, required: true, typeof(BsonDocument))`, register it in `_projectionBindings` keyed by whatever access expression the stashed `StructuralTypeShaperExpression`'s own `EntityProjectionExpression` reads resolve against, then `Visit` that shaper — produces a correctly-shaped entity reading `Name`/`Age`/`Score` from the NESTED `$$ROOT` sub-document rather than the outer projected document.

**Steps:**

- [ ] **Step 1:** Write the scratch test (see Files above) asserting `db.Entities.Select(c => new { c, Total = c.Age * c.Score })` under `MongoQueryMode.NativeOnly` returns correct values and does NOT throw `NativeTranslationNotSupportedException`.
- [ ] **Step 2:** Run it. Expected: FAIL (currently declines to fallback / throws under `NativeOnly`).
- [ ] **Step 3:** Prototype claim 1 — add the `NativeProjectionBinder.TryTranslateLeaf` branch (see Task 1 below for the exact code) and, with a debugger or a temporary `Console.WriteLine`, confirm the emitted `$project` body literally contains `{"c": "$$ROOT"}`.
- [ ] **Step 4:** Prototype claim 2 — add the bind-side `StructuralTypeShaperExpression` guard in `MongoProjectionBindingExpressionVisitor`. Confirm (via debugger) that `NativeProjectionBinder.TryTranslateLeaf` is reached for the `c` leaf and returns `true`, and that `Select.Route` ends up `NativeRoute.Projection` (not `Fallback`) for this query.
- [ ] **Step 5:** Prototype claim 3 — add the read-side registration in `MongoProjectionBindingRemovingExpressionVisitor`. Run the scratch test.
- [ ] **Step 6:** Iterate steps 3-5 until the scratch test passes. Record the ACTUAL code that worked — it will very likely differ in small but load-bearing ways from the sketch above (this repo's own history — EF-401/EF-405/EF-331 spikes — shows the first-guess site is often not quite the real one). Write down: exact guard conditions, exact `_projectionBindings`/`_ownerMappings` keys used, and any additional visitor (e.g. `BsonDocumentInjectingExpressionVisitor`, which pre-visits the shaper tree before the removing visitor and may need its own case) that had to change.
- [ ] **Step 7:** Run the FULL EF10 suite (`dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build`) to check the prototype hasn't silently broken an existing entity-projection shape (GroupBy key entity, reference-Include entity, SelectMany element entity all also flow through `StructuralTypeShaperExpression`/`EntityProjectionExpression` — the new guard must be narrow enough not to catch them). Fix any regression before proceeding.
- [ ] **Step 8:** Do NOT commit the spike as-is. Use its findings to write Tasks 1-4 for real (adjust file/line references and code below to match what actually worked), then `git checkout -- <the three files>` to discard the exploratory version before starting Task 1 with a clean failing-test-first cycle.

---

> **REVISED AFTER TASK 0'S SPIKE (see `.superpowers/sdd/2026-08-27-ef412-native-entity-leaf-projection/task-0-report.md` for full derivation).** The spike found four code sites, not three, and refuted the read-side design (Claim 3) entirely — the real read-side mechanism needed no new dictionary registration at all, but a DIFFERENT read-side fix was needed on the fallback leg instead. Tasks 1-3 below are rewritten from the spike's proven, mutation-tested findings. Do not use the original (pre-spike) sketch.

### Task 1: Emit side — recognize the root-entity leaf in `NativeProjectionBinder`

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeProjectionBinder.cs`
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/` (check first whether a `NativeProjectionBinderTests`-equivalent already exists: `grep -rn "TryPopulateNativeProjection\|TryTranslateLeaf" tests/MongoDB.EntityFrameworkCore.UnitTests`; if the binder is only exercised indirectly today, a `NativeProjectionBinderBareBodyTests`-style file already exists per the spike report §1.A — extend the same style)

**Interfaces:**
- Consumes: `TryTranslateLeaf`'s existing parameters, PLUS a new defaulted parameter `bool allowWholeRootEntityLeaf = false`.
- Produces: on match, `result` is `new MongoElementRefExpression("$ROOT", mongoQ.CollectionExpression.EntityType.ClrType)`; `isArrayLeaf` stays `false`.

- [ ] **Step 1: Write the failing test** — a unit test asserting `TryPopulateNativeProjection` for selector `c => new { c, Total = c.Age * c.Score }` returns `true` with a `MongoProjection("c", MongoElementRefExpression { Path: "$ROOT" })` entry. ALSO write the negative-control test up front (do not skip it — it is what caught the load-bearing gate in the spike): `NativeProjectionBinderBareBodyTests.Bare_whole_entity_parameter_is_declined_by_the_arm`-equivalent, asserting the BARE-body selector `c => c` does NOT get admitted by this new arm (it must keep taking the pre-existing `WholeEntity`-route path, untouched).
- [ ] **Step 2: Run tests to verify they fail** — the positive test fails (`TryTranslateLeaf` returns `false` for the entity leaf); the negative control should currently PASS trivially (nothing admits it yet) — note that and move on, it becomes a real regression guard once Step 3 lands.
- [ ] **Step 3: Write minimal implementation** — add both the new parameter and the new arm to `TryTranslateLeaf` (currently starting at line 299; place the new arm immediately after the plain-scalar/`EF.Property` arm, before the vector-search-score arm):

```csharp
    private static bool TryTranslateLeaf(
        MongoQueryExpression mongoQ,
        MongoExpressionTranslator translator,
        ParameterExpression outerParameter,
        Expression leafExpression,
        string alias,
        List<LookupExpression> pendingLookups,
        out MongoExpression result,
        out bool isArrayLeaf,
        bool allowWholeRootEntityLeaf = false)
    {
        ...
        // (after the plain-scalar/EF.Property member-access arm, before the vector-search-score arm)

        // A WHOLE-ROOT-ENTITY leaf — `new { c, Total = ... }`. The selector's own parameter
        // (possibly wrapped in EF auto-include layers) projected as `$$ROOT`. Gated to WRAPPED
        // selector bodies only (allowWholeRootEntityLeaf), never the bare-body arm — a bare
        // `c => c` must keep taking the pre-existing WholeEntity route, not this one.
        if (allowWholeRootEntityLeaf
            && IsSelectorParameter(leafExpression, outerParameter)
            && leafExpression.Type == mongoQ.CollectionExpression.EntityType.ClrType)
        {
            result = new MongoElementRefExpression("$ROOT", mongoQ.CollectionExpression.EntityType.ClrType);
            return true;
        }
```

  `IsSelectorParameter` is an EXISTING private helper already in this file (~line 564, written for the vector-score leaf) — reuse it, do not re-implement: it does `RemoveConvert()`, peels any number of `IncludeExpression` layers (EF's auto-includes for owned navigations arrive wrapped), then `ReferenceEquals`-checks against the selector's own parameter.

  Then pass `allowWholeRootEntityLeaf: true` from ONLY the two WRAPPED-body call sites — the `NewExpression` loop (~line 86) and the `MemberInitExpression` loop (~line 108):

```csharp
if (!TryTranslateLeaf(mongoQ, translator, selector.Parameters[0], newExpression.Arguments[i], alias,
        pendingLookups, out var leaf, out var isArrayLeaf, allowWholeRootEntityLeaf: true))
```

  Leave every other call site (in particular the bare-body branch around line ~158) with the default `false` — this is exactly what keeps the negative-control test passing.

- [ ] **Step 4: Run tests to verify they pass** — both the positive test and the negative control. Expected: PASS.
- [ ] **Step 5: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeProjectionBinder.cs tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/
git commit -m "EF-412: NativeProjectionBinder recognizes the root-entity leaf in a wrapped projection"
```

---

### Task 2: Bind side — register the entity leaf as one projection member

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoProjectionBindingExpressionVisitor.cs` — TWO sites, both required (the spike found the second one crashes five existing functional tests if skipped):
  1. `VisitExtension`'s `case StructuralTypeShaperExpression` (currently line 266)
  2. `VisitMethodCall`'s `switch (shaperExpression.ValueBufferExpression)` (currently line ~522, inside the `TryGetEFPropertyArguments` branch)
- Test: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeComputedProjectionTests.cs` (new test below) AND a full run of `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/ProjectionTests.cs` (regression check for site 2 — do not skip; the spike found real crashes there)

**Interfaces:**
- Consumes: `MongoQueryExpression.Select.Route` (already `NativeRoute.Projection` by the time this visitor runs when Task 1's arm admitted the leaf — binder runs first, confirmed by the spike at `MongoQueryableMethodTranslatingExpressionVisitor.TranslateSelect` lines 450 vs 538, same method, no branch between).
- Produces: a `StructuralTypeShaperExpression` whose `ValueBufferExpression` is a `ProjectionBindingExpression` with `Index == null` (member-bound, NOT index-bound) — Task 3 depends on this being member-bound specifically.

- [ ] **Step 1: Write the failing test** — add to `NativeComputedProjectionTests.cs`:

```csharp
[Fact]
public void Mixed_whole_entity_and_computed_leaf_goes_native()
{
    var (collection, _) = SeedCustomers(nameof(Mixed_whole_entity_and_computed_leaf_goes_native));
    using var db = CreateContext(collection, [], MongoQueryMode.NativeOnly);

    // Must not throw NativeTranslationNotSupportedException under NativeOnly.
    var results = db.Entities.Select(c => new { c, Total = c.Age * c.Score }).OrderBy(r => r.c.Name).ToList();

    Assert.Equal(["Alice", "Bob", "Carol"], results.Select(r => r.c.Name).ToArray());
    Assert.Equal([14, 400, -14], results.Select(r => r.Total).ToArray());
}
```

- [ ] **Step 2: Run test to verify it fails** — expected: FAIL (throws `NativeTranslationNotSupportedException`, since the bind side has not yet been taught to admit this leaf).
- [ ] **Step 3a: Write minimal implementation, site 1** — in `VisitExtension`, add a NEW arm ABOVE the existing `case StructuralTypeShaperExpression structuralTypeShaperExpression:` (line 266):

```csharp
// A whole-root-entity leaf inside a NATIVE projection ($project emits {"c": "$$ROOT"}).
case StructuralTypeShaperExpression nativeRootShaper
    when _queryExpression.Select.Route == NativeRoute.Projection
         && nativeRootShaper.StructuralType == _queryExpression.CollectionExpression.EntityType
         && nativeRootShaper.ValueBufferExpression is ProjectionBindingExpression { Index: null } rootBinding:
    {
        var entityProj = (EntityProjectionExpression)_queryExpression.GetMappedProjection(
            rootBinding.ProjectionMember);
        var member = GetCurrentProjectionMember();
        _projectionMapping[member] = entityProj;
        return nativeRootShaper.Update(
            new ProjectionBindingExpression(_queryExpression, member, typeof(ValueBuffer)));
    }
```

  **Do NOT use `AddToProjection` here** (unlike the array-leaf/count-leaf/arithmetic-leaf arms which DO call it) — `MongoQueryExpression.ApplyProjection` returns early once `Projection.Any()`, which would strand every SIBLING leaf's own pending projection-member rewrite. Register into `_projectionMapping` by `ProjectionMember` (member-bound) exactly as shown; this is the established pattern documented in this same file's `TryBindNativeArrayProjection` remarks (~line 1176).

  The `Index: null` conjunct keeps this arm disjoint from the pre-existing arm's own "already bound by index (e.g. from join rebinding)" case, which must keep taking the old path untouched.

- [ ] **Step 3b: Write minimal implementation, site 2 (not optional — skipping this crashes 5 existing tests)** — in `VisitMethodCall`, inside the `TryGetEFPropertyArguments` branch's `switch (shaperExpression.ValueBufferExpression)` (~line 522), add a NEW arm ABOVE the pre-existing `case ProjectionBindingExpression innerProjectionBindingExpression:` (which unconditionally derefs `.Index.Value` and will throw `InvalidOperationException: Nullable object must have a value` for the member-bound binding site 1 now produces, whenever the leaf entity has its own auto-included/owned navigation):

```csharp
// A whole-root-entity leaf in a native projection is bound by ProjectionMember
// (Index == null, per site 1 above), so resolve through the LOCAL mapping in that case —
// _queryExpression.GetMappedProjection is not populated yet; ReplaceProjectionMapping only
// copies _projectionMapping into the query expression at the end of Translate.
case ProjectionBindingExpression { Index: null } memberBoundBinding:
    innerEntityProjection = (EntityProjectionExpression)(
        _projectionMapping.TryGetValue(memberBoundBinding.ProjectionMember, out var localEntityProj)
            ? localEntityProj
            : _queryExpression.GetMappedProjection(memberBoundBinding.ProjectionMember));
    break;
```

  Get the local-dictionary-first lookup right — the spike's first attempt called `_queryExpression.GetMappedProjection` unconditionally and threw `KeyNotFoundException`, because site 1's mapping lives only in the visitor's own local `_projectionMapping` until `Translate`'s tail runs `ReplaceProjectionMapping`.

  There may be OTHER `.Index.Value` / `Projection[index]` sites reachable from a whole-entity shaper beyond this one — the spike found this one empirically, on its test corpus, and flags that as a limit, not a guarantee. Grep `\.Index\.Value` and `Projection\[` across `MongoProjectionBindingExpressionVisitor.cs` and `MongoProjectionBindingRemovingExpressionVisitor.cs` and decide each hit; fix any other unconditional index-deref the same way.

- [ ] **Step 4: Run test to verify it passes** — expected: it may still fail here with a DIFFERENT error (a read-side issue), since Task 3 has not landed — that is fine and expected; confirm the failure is no longer `NativeTranslationNotSupportedException`.
- [ ] **Step 5: Run the full `ProjectionTests.cs` file** — `dotnet test --filter "FullyQualifiedName~FunctionalTests.Query.ProjectionTests"`. Expected: 0 failures (this is the regression check for site 2 — the spike found 5 failures here without it: `Select_projection_entity_and_scalar_field`, `…_and_multiple_scalar_fields`, `…_and_ef_property`, `Select_projection_to_constructor_initializer`, `Select_projection_entity_to_named_container_with_scalar`).
- [ ] **Step 6: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoProjectionBindingExpressionVisitor.cs tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeComputedProjectionTests.cs
git commit -m "EF-412: bind side registers the root-entity leaf as a native projection member"
```

---

### Task 3: Read side + the fallback-leg fix + late-fallback coverage

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoProjectionBindingRemovingExpressionVisitor.cs`
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoMixedProjectionBindingRemovingExpressionVisitor.cs`
- Test: `Mixed_whole_entity_and_computed_leaf_goes_native` (from Task 2, now made to pass in ALL THREE query modes) plus a new forced-late-decline test (Step 6 below)

**IMPORTANT — the original plan's read-side design (registering into `_projectionBindings`/`_ownerMappings`, mirroring the collection-shaper case) was REFUTED by the spike, by mutation, not by reading. No such registration is needed for the native `NativeOnly`/`Native` path — the EXISTING `VisitBinary` code (in the `RootReferenceExpression` case) already reads the nested `$$ROOT` sub-document correctly, because `fieldName = projection.Alias` already resolves to `"c"` and `CreateGetValueExpression(DocParameter, "c", ...)` already gets the right nested document. Do not re-add that dead-end registration. The REAL read-side gap is on the FALLBACK leg (explicit `DriverLinq`, which works correctly TODAY and must not regress) — there the shaper is handed a WHOLE, un-projected document, so the `"c"` alias names nothing and must be ignored.**

- [ ] **Step 1: Confirm (do not re-derive) that the native leg already works** — with Tasks 1-2 landed, run `Mixed_whole_entity_and_computed_leaf_goes_native` (`MongoQueryMode.NativeOnly`). Expected: **PASS already**, with no read-side change. If it does not pass, stop and compare against the spike report's §4 trace (`.superpowers/sdd/.../task-0-report.md`) step by step before writing new code — the mechanism is proven, so a failure here means something in Task 1/2's actual landed code diverged from the spike.
- [ ] **Step 2: Write the failing test for the fallback leg** — add to `NativeComputedProjectionTests.cs`:

```csharp
[Theory]
[InlineData(MongoQueryMode.Native)]
[InlineData(MongoQueryMode.DriverLinq)]
public void Mixed_whole_entity_and_computed_leaf_works_in_every_mode(MongoQueryMode mode)
{
    var (collection, _) = SeedCustomers(nameof(Mixed_whole_entity_and_computed_leaf_works_in_every_mode) + mode);
    using var db = CreateContext(collection, [], mode);

    var results = db.Entities.Select(c => new { c, Total = c.Age * c.Score }).OrderBy(r => r.c.Name).ToList();

    Assert.Equal(["Alice", "Bob", "Carol"], results.Select(r => r.c.Name).ToArray());
    Assert.Equal([14, 400, -14], results.Select(r => r.Total).ToArray());
}
```

- [ ] **Step 3: Run it to verify the `DriverLinq` case fails** — expected error: `InvalidOperationException: Field 'c' required but not present in BsonDocument for a 'Customer'` (the mixed shaper reads the whole un-projected document under explicit `DriverLinq`, and `"c"` names no element there). The `Native` case should already pass (it takes the same native path Step 1 confirmed).
- [ ] **Step 4: Write minimal implementation** — in `MongoProjectionBindingRemovingExpressionVisitor.cs`, add (near the top of the class, alongside other protected members):

```csharp
/// <summary>True for the fallback shaper, which sees whole un-projected documents.</summary>
protected virtual bool ReadsUnprojectedDocuments => false;

/// <summary>Whether <paramref name="alias"/> is a native whole-root-entity ("$$ROOT") leaf.</summary>
private bool IsWholeRootEntityAlias(string? alias)
    => alias != null
       && _queryExpression.Select.Projection.Any(
           p => p.Alias == alias && p.Expression is MongoElementRefExpression { Path: "$ROOT" });
```

  Then in `VisitBinary`'s `case RootReferenceExpression:` (inside the `EntityProjectionExpression` else-branch, ~line 430), add the null-out BEFORE the existing `innerAccessExpression = DocParameter;` line's fallout is used to resolve `fieldName`:

```csharp
case RootReferenceExpression:
    innerAccessExpression = DocParameter;
    // On the FALLBACK route the shaper is handed WHOLE, un-projected documents, so a
    // whole-root-entity leaf's "$$ROOT" alias names no element and must resolve to the
    // document itself rather than a non-existent "c" field.
    if (ReadsUnprojectedDocuments && IsWholeRootEntityAlias(fieldName))
    {
        fieldName = null;
    }
    // ... existing _ownerMappings / _ordinalMappings propagation below, unchanged
```

  Then in `MongoMixedProjectionBindingRemovingExpressionVisitor.cs`, override the new virtual:

```csharp
/// <inheritdoc />
protected override bool ReadsUnprojectedDocuments => true;
```

- [ ] **Step 5: Run test to verify it passes** — both `[InlineData]` cases of `Mixed_whole_entity_and_computed_leaf_works_in_every_mode`. Expected: PASS.
- [ ] **Step 6: Write and resolve the late-fallback case.** **Ruling (controller, recorded in the SDD ledger): this is in scope for Task 3, not deferred to a follow-up ticket.** The spike flagged an UNVERIFIED risk: when a query is routed native at translate time (`Route == Projection`) but `TryBuildNativeFactory` declines MID-COMPILE (the same "late native-factory decline" mechanism documented in `Ef362OwnedHopArrayProjectionTests.cs`'s remarks — e.g. triggered by a captured-local `string.StartsWith` elsewhere in the same query), the pipeline may fall through to the driver-LINQ bridge while still using the NATIVE (non-mixed) removing visitor — which would hit the exact "Field 'c' required" failure Step 3 just fixed for the EXPLICIT `DriverLinq` case, but this time under the DEFAULT `Native` mode. Force this condition the same way EF-362 did (a parameterized `Where` using a captured local before the projection, e.g. `.Where(c => c.Name.StartsWith(namePrefix)).Select(c => new { c, Total = c.Age * c.Score })` with `namePrefix` a captured local, not a constant) and add a test asserting correct results under default `Native` mode. If it fails, extend the SAME late-fallback-strip mechanism EF-362 already generalized (see that file's own remarks, and `NativeProjectionBinder`'s `namedAliasOverrides`/alias-override registration) to also recognize a `$$ROOT`-bound alias, so the late-fallback path routes through the mixed visitor (or otherwise nulls the alias) the same way the explicit-`DriverLinq` case now does. If it already passes, the test still stands as the regression pin proving the risk was unfounded — either outcome is an acceptable result of this step, but the test must exist and must pass before this task is done.
- [ ] **Step 7: Run the full EF10 suite** — `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build`. Expected, per the spike's own measured baseline-vs-prototype delta: **21 failures beyond the pre-existing baseline, ALL of which are expected and must be individually triaged, not treated as regressions:**
  - **18 spec `AssertMql` baseline mismatches** (list in the spike report §5.1) — the upstream data assertions in each already pass; only the captured MQL string changed (a `$project` stage now appears). Re-baseline these with the repo's `EF_TEST_REWRITE_BASELINES=1` mechanism, do not hand-edit the expected MQL strings.
  - **2 tests that newly PASS and must have their overrides rewritten**: `NorthwindSelectQueryMongoTest.ToList_Count_in_projection_works` (× 2 async) currently asserts `AssertTranslationFailed(...)`; replace that assertion with `await base.ToList_Count_in_projection_works(async);` (the spike verified this passes with correct data) and remove the "EF-X001" stale-comment reference.
  - **1 unit test that pins the OLD decline and must be inverted, not "fixed"**: `SlotPopulationTests.Mixed_whole_entity_and_arithmetic_leaves_do_not_populate_projection` currently asserts `Route == Fallback`; invert it to assert `Route == Projection` (rename the test to reflect the new behavior), and ADD a sibling test proving a genuinely-still-unsupported entity-leaf shape (e.g. a nav-entity leaf, which is out of scope per Global Constraints) still declines to `Fallback` — so the removed assertion's INTENT (some entity leaves still decline) is not lost, only the specific shape that changed.
  - Any OTHER failure beyond these 21 is a genuine regression — stop and fix it before proceeding.
- [ ] **Step 8: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoProjectionBindingRemovingExpressionVisitor.cs src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoMixedProjectionBindingRemovingExpressionVisitor.cs tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeComputedProjectionTests.cs tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/NorthwindSelectQueryMongoTest.cs tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/SlotPopulationTests.cs
git commit -m "EF-412: read side handles the root-entity leaf on the native and fallback legs, including the late-fallback case"
```

---

### Task 4: Breadth tests and regression pin

**Files:**
- Modify: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeComputedProjectionTests.cs`

**Purpose:** The original EF-412 sizing ("52 cases") comes from a status doc that no longer exists on this branch and has proven unreliable for other slices in this codebase (see this plan's Global Constraints). Rather than trust that number, add explicit coverage for the realistic variations of "root entity leaf mixed with something else" and let the existing spec/functional suites reveal any others via their normal `NativeOnly` pass:

- [ ] **Step 1:** Add a test for the entity leaf alongside a plain scalar member sibling (not just an arithmetic one): `Select(c => new { Entity = c, Name = c.Name })` under `NativeOnly`, asserting correct values.
- [ ] **Step 2:** Add a test for the entity leaf alongside a projected owned-collection `.Count`: `Select(c => new { c, PostCount = c.Posts.Count })` under `NativeOnly` (exercises the interaction between this new branch and the existing count-leaf branch in the same `NewExpression`).
- [ ] **Step 3:** Add a test asserting the entity leaf ALONE, with no sibling (`Select(c => new { c })`), still goes native — this is the degenerate single-leaf case and must not be confused with the pre-existing bare `Select(c => c)` `WholeEntity` route (a different `NativeRoute` value entirely; confirm both still work side by side).
- [ ] **Step 4:** Add a test with the entity leaf and NO ordering by an entity member, to catch any accidental coupling to `OrderBy` in the existing `Mixed_whole_entity_and_computed_leaf_goes_native` test.
- [ ] **Step 5:** Run the full three-EF-version suite via the `/test-all` skill (or manually build+test EF8/EF9/EF10 sequentially per the build-parallelism host trap recorded in this repo's memory — build each config sequentially, then run the three `dotnet test --no-build` passes in parallel). Expected: 0 failures across all three.
- [ ] **Step 6:** Run the full `SpecificationTests` suite specifically under `MONGODB_EF_NATIVE_ONLY=1` (if this env var / test convention exists — check `tests/.../Utilities/TestServer.cs` for the actual mechanism referenced in EF-417's finding) to surface any spec-suite case that now newly passes natively where it previously only passed via fallback — these are bonus wins worth noting in the Jira closeout, not required to add explicitly as new tests.
- [ ] **Step 7: Commit**

```bash
git add tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeComputedProjectionTests.cs
git commit -m "EF-412: breadth tests for the native root-entity-leaf projection"
```

---

### Task 5: Documentation and Jira closeout

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md`

- [ ] **Step 1:** Update the `Query/AGENTS.md` line(s) describing `NativeRoute.Projection`/`NativeProjectionBinder` (currently around line 90-91, and any entry documenting "an entity leaf isn't natively representable" if one exists — search for it, it may only live in the test comment) to record that a ROOT-entity leaf is now natively representable via the `$$ROOT` mechanism, and that a NAV-entity leaf is explicitly still out of scope (name the follow-up ticket once filed).
- [ ] **Step 2:** Run the full EF10 suite one more time as a final sanity check.
- [ ] **Step 3:** File a follow-up Jira ticket for the nav-entity-leaf case (out of scope per Global Constraints) if one does not already exist, and cross-link it from EF-412's resolution comment.
- [ ] **Step 4:** Update EF-412 in Jira: comment with the actual measured win count (from Task 4's breadth tests plus any spec-suite bonus wins found in Task 4 Step 6), transition to whatever status this repo uses for "code-complete on a branch, not yet merged/released" (see EF-419/EF-362 precedent comments for the phrasing convention), and correct the description's now-satisfied "must fix EF-356" framing if not already done.
- [ ] **Step 5: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/AGENTS.md
git commit -m "EF-412: document the native root-entity-leaf projection mechanism"
```

---

## Self-Review

**Spec coverage:** EF-412's two-part ask (fix EF-356 — already done pre-plan; make the entity leaf native) is covered: Tasks 1-3 are the native-path mechanism, Task 4 is breadth, Task 5 is closeout. The nav-entity-leaf variant mentioned in the ticket's original "52 cases" is explicitly descoped with a named follow-up action (Task 5 Step 3), not silently dropped.

**Placeholder scan:** Task 0 is deliberately exploratory (a spike, not a placeholder) and says so; every other task has concrete code. The one honestly-uncertain spot (Task 3 Step 3's "resolve via whatever lookup Task 2's mapping makes available") is flagged as depending on Task 0's spike output, which is the correct way to carry forward a genuine unknown in this domain — not a placeholder for laziness, but a pointer to where the answer will already exist by the time this step is executed.

**Type consistency:** `MongoElementRefExpression("$ROOT", ...)`, `NativeRoute.Projection`, `_projectionBindings`/`_ownerMappings` keyed by `ValueBufferExpression`, and `MongoProjection(alias, expression)` are used consistently across Tasks 1-3, matching their existing definitions read directly from source during this plan's preparation (`MongoElementRefExpression.cs`, `MongoSelectDefinition.cs`'s `Route` property, `MongoProjectionBindingRemovingExpressionVisitor.cs`'s collection-shaper precedent).
