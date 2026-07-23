# Native bare owned-collection-element SelectMany — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make a bare whole owned-collection-*element* result from `SelectMany`
(`from o in q from i in o.Items select i`, where `Items` is an embedded owned collection) go
native via `$unwind` → `$replaceRoot` + a root-level entity shaper, replacing today's
`NotSupportedException`.

**Architecture:** The bare-nav bind already sets `UnwindSource` and builds an element
`StructuralTypeShaperExpression`; this slice adds a `WholeElement` provenance flag, a
`$replaceRoot` lowering stage, a spike-determined materializer change so the owned element
materializes from the (re-rooted) document, and QMTEV recognition that routes the whole-inner
selector to native while keeping the whole-outer (`select o`) and reference forms declining.

**Tech Stack:** C# / .NET (net8.0 for EF8/EF9, net10.0 for EF10), EF Core provider internals,
MongoDB C# driver, xUnit (+ FluentAssertions in functional tests; plain `Assert.*` in unit tests).

## Global Constraints

- Build configurations, not TFMs: `Debug|Release EF8`, `Debug|Release EF9`, `Debug|Release EF10`. Build one with `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"`.
- `src/` is `<Nullable>enable</Nullable>` — annotate new types. `<NoWarn>EF1001</NoWarn>` (provider consumes EF internals intentionally).
- Cross-version code uses `EF8`/`EF9`/`EF10` symbols (`#if EF8 || EF9`, `#if !EF8`). Build **all three** before claiming done — a prior slice hit an EF8-only `CS9174`.
- Preserve file BOMs. All touched production types are `internal` — no public-API/breaking-change surface.
- Tests run serially. Prefer `MONGODB_URI`/`ATLAS_URI` unset (atlas-local container, isolated per process).
- This slice is **owned-collection whole-INNER-element only**, **terminal-only**, **DOM-only**. `select o` (whole-outer), reference bare-entity, computed leaf, filtered inner, nested SelectMany, and any post-SelectMany composition all stay deferred/declining.
- No driver-LINQ oracle for this shape — prove correctness under `MongoQueryMode.NativeOnly` against expected in-memory results (the pattern used by reference SelectMany / Intersect-Except).
- Branch `EF-347-selectmany-bare-owned`, already created off `bcf5b30`; design commit `672c830`.

---

### Task 1: Spike — owned-from-root materialization + EF tracking semantics

**Investigation only.** No production code kept. Deliverable: a findings note that specifies the
**exact** materializer change (file, method, edit) Task 3 will apply, and the tracking contract.

**Files:**
- Create: `.superpowers/sdd/EF-347-bare-owned-selectmany-spike.md` (findings)
- Investigate (do not keep edits): `Query/Visitors/MongoProjectionBindingRemovingExpressionVisitor.cs`, `Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs` (`TranslateSelect` lines ~305–320, `BuildBareNavWrappedShaper` ~1307), `Query/NativeTranslation/MongoSelectLowerer.cs` (UnwindSource block ~122)

- [ ] **Step 1: Reproduce the current crash and capture the pipeline.** In a throwaway change,
  narrow the `TranslateSelect` decline so `from o in q from i in o.Items select i` (owned) routes
  native (set nothing else), build EF10, and run a scratch query under `NativeOnly` against a
  seeded `Owner`/`Item` context. Capture: the emitted pipeline (via `TestMqlLoggerFactory` /
  logging), and the exact exception + stack (`KeyNotFoundException("… 'bsonDoc' …")` expected).

- [ ] **Step 2: Trace how the owned element shaper is materialized.** Follow
  `MongoProjectionBindingRemovingExpressionVisitor` for the element's
  `StructuralTypeShaperExpression` (built by `BuildBareNavWrappedShaper` — a root
  `ProjectionBindingExpression(new ProjectionMember(), ValueBuffer)` over `UnwindSource.InnerEntityType`).
  Determine where it expects a nested owner `bsonDoc` and what it would take to read the owned
  type's properties from the **root** document instead (after a `$replaceRoot` promotes the
  element to root). Prototype the minimal change and confirm the query returns the right rows.

- [ ] **Step 3: Determine EF tracking semantics for a bare owned entity.** Owned entities are
  dependents and normally can't be tracked standalone (the existing decline tests use
  `AsNoTracking()`). Establish empirically: does the query work tracked, does EF force
  no-tracking, or must the provider/test use `AsNoTracking()` / a query filter? Note EF8 vs
  EF9 vs EF10 differences. Record the required handling (e.g. "tests run `AsNoTracking()`", or
  "works tracked because …").

- [ ] **Step 4: Decide Approach A vs B and write the findings note.** Record: (a) confirmed
  Approach (A = standard shaper pointed at root; B = bespoke re-rooted shaper) with rationale;
  (b) the **concrete** materializer edit for Task 3 — exact file, method, and code; (c) the
  tracking contract for the tests; (d) any EF-version guard needed. Revert all throwaway edits
  (`git checkout .`), then commit only the findings note.

```bash
git checkout -- src/   # discard all spike prototype edits
git add .superpowers/sdd/EF-347-bare-owned-selectmany-spike.md
git commit -m "EF-347: spike findings — bare owned-element SelectMany materialization + tracking"
```

**STOP for review.** The A-vs-B decision and the concrete materializer edit are confirmed here
before any production change lands.

---

### Task 2: `WholeElement` flag, `MongoReplaceRootStage`, and `$replaceRoot` lowering

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoUnwindSource.cs`
- Create: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/Stages/MongoReplaceRootStage.cs`
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoSelectLowerer.cs:122-130`
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoPipelineFactory.cs:88-103`
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoSelectLowererTests.cs`

**Interfaces:**
- Produces: `MongoUnwindSource.WholeElement` (`bool`, settable, default `false`);
  `MongoReplaceRootStage(string newRoot)` with `NewRoot` (`string`) property;
  lowerer emits `[MongoUnwindFieldStage, MongoReplaceRootStage]` for an owned `WholeElement`
  unwind source with empty `Projection`.
- Consumes: nothing new.

- [ ] **Step 1: Write the failing lowerer unit test.** Add to `MongoSelectLowererTests.cs`
  (mirror Test 15 `UnwindSource_lowers_to_unwind_then_project_stage_in_order`):

```csharp
// ── Owned whole-element SelectMany lowers to $unwind then $replaceRoot (EF-347 bare-owned) ──
[Fact]
public void WholeElement_UnwindSource_lowers_to_unwind_then_replaceRoot_stage_in_order()
{
    var query = TestSelect();
    var unwind = MongoUnwindSource.Owned("Items", innerEntityType: null!);
    unwind.WholeElement = true;
    query.Select.UnwindSource = unwind;

    var stages = new MongoSelectLowerer().Lower(query);

    Assert.Collection(stages,
        s => Assert.Equal("Items", Assert.IsType<MongoUnwindFieldStage>(s).ElementPath),
        s => Assert.Equal("Items", Assert.IsType<MongoReplaceRootStage>(s).NewRoot));
}
```

- [ ] **Step 2: Run it, verify it fails.**

Run: `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --filter "FullyQualifiedName~WholeElement_UnwindSource_lowers"`
Expected: FAIL — `MongoUnwindSource` has no `WholeElement` setter and `MongoReplaceRootStage` does not exist (compile error).

- [ ] **Step 3: Add the `WholeElement` flag.** In `MongoUnwindSource.cs`, add after the `Lookup`
  property (settable provenance flag — set post-construction by `TranslateSelect`, mirroring
  `MongoSetOperation.OperandsProjected`):

```csharp
    /// <summary>
    /// <see langword="true"/> when the trailing SelectMany selector returns the WHOLE inner
    /// element entity (e.g. <c>from o in q from i in o.Items select i</c>) rather than a member
    /// projection. Set by <c>TranslateSelect</c> once it recognizes the whole-inner-entity
    /// selector; drives the lowerer to append a <c>$replaceRoot</c> that promotes the unwound
    /// element to the root document. Owned-only in this slice (reference is deferred).
    /// </summary>
    public bool WholeElement { get; set; }
```

- [ ] **Step 4: Create `MongoReplaceRootStage`.** New file (copy the BOM/licence header from
  `MongoUnwindFieldStage.cs`):

```csharp
namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation.Stages;

/// <summary>
/// A <c>$replaceRoot</c> that promotes a field (an already-<c>$unwind</c>'d element) to the root
/// document — <c>{ $replaceRoot: { newRoot: "$&lt;NewRoot&gt;" } }</c>. Used by a bare
/// whole-inner-element owned SelectMany (EF-347) so the unwound owned element becomes the query's
/// root result document.
/// </summary>
internal sealed class MongoReplaceRootStage : MongoPipelineStage
{
    public MongoReplaceRootStage(string newRoot) => NewRoot = newRoot;
    public string NewRoot { get; }
}
```

- [ ] **Step 5: Emit `$replaceRoot` in the lowerer.** In `MongoSelectLowerer.cs`, replace the
  UnwindSource block body (lines ~122–130) so a `WholeElement` source appends the replace-root
  after the unwind and returns (no projection for the whole-element case):

```csharp
        if (select.UnwindSource is { } unwind)
        {
            if (unwind.Kind == MongoUnwindSourceKind.Owned)
                stages.Add(new MongoUnwindFieldStage(unwind.InnerScopePath));

            if (unwind.WholeElement)
            {
                // Bare whole-inner-element SelectMany: promote the unwound element to root.
                // (Reference whole-element is deferred; its $lookup+$unwind is appended earlier by
                // AppendLookupStages, and this $replaceRoot would still apply — but WholeElement is
                // only set for owned in this slice.)
                stages.Add(new MongoReplaceRootStage(unwind.InnerScopePath));
                return stages;
            }

            if (select.Projection.Count > 0)
                stages.Add(new MongoProjectStage(select.Projection));
            return stages;
        }
```

- [ ] **Step 6: Render `MongoReplaceRootStage`.** In `MongoPipelineFactory.RenderStage`'s switch
  (line ~88), add the arm (before the `_ =>` default):

```csharp
            MongoReplaceRootStage replaceRoot =>
                new BsonDocument("$replaceRoot", new BsonDocument("newRoot", "$" + replaceRoot.NewRoot)),
```

- [ ] **Step 7: Add a pipeline-factory render test.** In `MongoPipelineFactoryTests.cs`, add a
  test that a `MongoReplaceRootStage("Items")` renders to `{ $replaceRoot: { newRoot: "$Items" } }`
  (mirror the file's existing single-stage render tests — match their construction/assert style).

- [ ] **Step 8: Run the unit tests, verify they pass.**

Run: `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --filter "FullyQualifiedName~MongoSelectLowererTests|FullyQualifiedName~MongoPipelineFactoryTests"`
Expected: PASS (including the two new tests).

- [ ] **Step 9: Build all three EF versions.**

Run: `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF8" && dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF9" && dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"`
Expected: all succeed (no `CS9174`/nullable warnings-as-errors).

- [ ] **Step 10: Commit.**

```bash
git add src/ tests/MongoDB.EntityFrameworkCore.UnitTests/
git commit -m "EF-347: WholeElement flag + MongoReplaceRootStage + \$replaceRoot lowering"
```

**STOP for review.**

---

### Task 3: QMTEV recognition + materialization + end-to-end functional tests

Route the whole-inner-entity owned selector to native (setting `WholeElement`), apply the
spike-confirmed materializer edit, narrow the existing decline to whole-outer / non-`Inner`, and
prove it end-to-end under `NativeOnly`.

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs:305-317` (+ a new `IsWholeInnerEntitySelector` helper near `IsTransparentIdentifierMemberAccessSelector` ~425)
- Modify: the materializer file named in the Task 1 findings note (`.superpowers/sdd/EF-347-bare-owned-selectmany-spike.md`) — apply that note's exact edit
- Test: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeSelectManyTests.cs`

**Interfaces:**
- Consumes: `MongoUnwindSource.WholeElement` (Task 2); the concrete materializer edit + tracking
  contract from the Task 1 findings note.
- Produces: native execution of the owned whole-inner-entity SelectMany in all three spellings.

- [ ] **Step 1: Write the failing functional test (all three spellings, `NativeOnly`).** Add to
  `NativeSelectManyTests.cs`, following the file's `CreateContext(seed, mode, name)` /
  `SingleEntityDbContext<Owner>` pattern and applying the Task-1 tracking contract (add
  `AsNoTracking()` iff the spike says so). `SeedOwners()` yields three items total across two
  owners plus one single-item owner — assert the flattened element set:

```csharp
[Fact]
public void Bare_owned_whole_inner_element_goes_native_all_three_spellings()
{
    var seed = SeedOwners();

    using var oneArg = CreateContext(seed, MongoQueryMode.NativeOnly,
        nameof(Bare_owned_whole_inner_element_goes_native_all_three_spellings) + "OneArg");
    var r1 = oneArg.Entities.SelectMany(o => o.Items).Select(i => i.Name).OrderBy(n => n).ToList();

    using var query = CreateContext(seed, MongoQueryMode.NativeOnly,
        nameof(Bare_owned_whole_inner_element_goes_native_all_three_spellings) + "Query");
    var r2 = (from o in query.Entities from i in o.Items select i)
        .Select(i => i.Name).OrderBy(n => n).ToList();

    using var explicitSel = CreateContext(seed, MongoQueryMode.NativeOnly,
        nameof(Bare_owned_whole_inner_element_goes_native_all_three_spellings) + "Explicit");
    var r3 = explicitSel.Entities.SelectMany(o => o.Items, (o, i) => i)
        .Select(i => i.Name).OrderBy(n => n).ToList();

    var expected = seed.SelectMany(o => o.Items).Select(i => i.Name).OrderBy(n => n).ToList();
    Assert.Equal(expected, r1);
    Assert.Equal(expected, r2);
    Assert.Equal(expected, r3);
}
```

> Note: the trailing `.Select(i => i.Name)` is a scalar projection over the materialized element
> used only to read a value for assertion. If the spike shows this trailing scalar Select routes
> the query away from the whole-element path (i.e. it is no longer a bare-element result), assert
> instead over the materialized entities directly — e.g. `.ToList()` then project in memory:
> `oneArg.Entities.SelectMany(o => o.Items).AsEnumerable().Select(i => i.Name)…`. Prefer whichever
> the spike confirms actually exercises the `$unwind`→`$replaceRoot` native path; the assertion
> target is the same element set either way.

- [ ] **Step 2: Run it, verify it fails.**

Run: `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --filter "FullyQualifiedName~Bare_owned_whole_inner_element_goes_native"`
Expected: FAIL — currently `NotSupportedException` (the decline at `TranslateSelect` lines ~305–314).

- [ ] **Step 3: Add the whole-inner-entity selector helper.** In the QMTEV, near
  `IsTransparentIdentifierMemberAccessSelector` (~line 425):

```csharp
    // The whole-INNER-entity trailing selector of a bare-nav SelectMany: `ti => ti.Inner`.
    // Distinct from the whole-OUTER form (`ti => ti.Outer`, i.e. `select o`), which stays declined.
    private static bool IsWholeInnerEntitySelector(LambdaExpression selector)
        => selector.Parameters.Count == 1
           && selector.Body is MemberExpression { Member.Name: "Inner" } member
           && member.Expression == selector.Parameters[0];
```

- [ ] **Step 4: Route whole-inner to native; keep whole-outer / others declining.** Replace the
  decline block (`TranslateSelect` lines ~305–317) so the whole-inner owned case sets
  `WholeElement` and falls through to the generic shaper fold (lines ~319–321), while whole-outer
  and any other `ti.<Member>` selector keeps throwing and computed leaves keep falling back:

```csharp
        if (mongoQueryExpression.Select.UnwindSource is { } wholeElementUnwind
            && mongoQueryExpression.Select.Projection.Count == 0)
        {
            if (IsWholeInnerEntitySelector(selector)
                && wholeElementUnwind.Kind == MongoUnwindSourceKind.Owned)
            {
                // Bare whole-inner-element owned SelectMany (e.g. `from o in q from i in o.Items
                // select i`). Emit $unwind → $replaceRoot (lowerer) and materialize the owned
                // element from the re-rooted document; fall through to the generic shaper fold
                // below, which resolves TransparentIdentifier(outer, item).Inner to the element
                // shaper BuildBareNavWrappedShaper already built. Reference + whole-outer stay
                // declined (below); see the SelectMany as-built note in Query/AGENTS.md.
                wholeElementUnwind.WholeElement = true;
            }
            else if (IsTransparentIdentifierMemberAccessSelector(selector))
            {
                // Whole-OUTER (`select o`) or any other whole-entity member selector: no working
                // translation in any mode (a bare outer/entity has no re-rooted element shaper).
                // Narrowed from the previous decline, which covered the whole-inner case too.
                throw new NotSupportedException(
                    "Projecting a whole entity other than an owned collection element from a "
                    + "SelectMany (e.g. 'from o in q from i in o.Items select o') is not supported. "
                    + "Project members instead, e.g. 'from o in q from i in o.Items select new "
                    + "{ o.Name, i.SomeProperty }', or project the element with 'select i'.");
            }
            else
            {
                mongoQueryExpression.Select.MarkNotNativelyRepresentable();
            }
        }
```

- [ ] **Step 5: Apply the spike's materializer edit.** Apply the exact edit recorded in
  `.superpowers/sdd/EF-347-bare-owned-selectmany-spike.md` (Approach A: point the owned-element
  `StructuralTypeShaperExpression` materialization at the root document after `$replaceRoot`;
  Approach B: the bespoke re-rooted shaper). Include any EF-version guard the note specifies.

- [ ] **Step 6: Run the new functional test, verify it passes.**

Run: `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --filter "FullyQualifiedName~Bare_owned_whole_inner_element_goes_native"`
Expected: PASS — all three spellings return the expected element set under `NativeOnly`.

- [ ] **Step 7: Add the edge-case + boundary functional tests.** Add tests (same fixture / `NativeOnly`):
  - `Bare_owned_whole_inner_element_owner_with_zero_items_contributes_no_rows` — an owner with an
    empty `Items` list contributes zero rows (inner-flatten semantics).
  - `Bare_owned_whole_inner_element_materializes_nested_owned_members` — if the `Item` fixture has
    a nested owned member, assert it round-trips; otherwise extend the fixture minimally to prove it.
  - `Bare_owned_whole_inner_element_reads_root_relative_not_owner_scoped` — leverage the existing
    shared `Name` member on `Owner`/`Item`: assert the returned elements carry the **Item**'s
    `Name`, proving root-relative reads (no owner-scope leak).

- [ ] **Step 8: Update the existing decline tests to the new behavior.** In `NativeSelectManyTests.cs`:
  - `Whole_inner_entity_form_declines_cleanly_in_every_mode_AsNoTracking` (~line 1435) and the
    owned `Bare_SelectMany_…` (~line 1178): these assert the OWNED whole-inner form throws — flip
    them to assert native success (or delete + supersede with the Step-1/Step-7 tests), keeping a
    retained assertion that the **whole-outer** `select o` / `(o,i) => o` form STILL throws
    `NotSupportedException`.
  - Reference-form throws (~lines 800–822): MUST stay throwing (reference is out of scope) — leave
    unchanged; if their shared comments referenced the owned form's decline, update the prose.

- [ ] **Step 9: Run the full `NativeSelectManyTests` class, verify green.**

Run: `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --filter "FullyQualifiedName~NativeSelectManyTests"`
Expected: PASS (new tests green; flipped tests green; reference + whole-outer declines intact).

- [ ] **Step 10: Build + run `NativeSelectManyTests` on EF8 and EF9.**

Run: `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF8" && dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF8" --no-build --filter "FullyQualifiedName~NativeSelectManyTests"` (repeat for EF9)
Expected: PASS on both (guards against an EF8-only build/behavior gap).

- [ ] **Step 11: Commit.**

```bash
git add src/ tests/MongoDB.EntityFrameworkCore.FunctionalTests/
git commit -m "EF-347: native bare owned-collection-element SelectMany (whole-inner, all three spellings)"
```

**STOP for review.**

---

### Task 4: Finalize — docs, full suite, native-only sweep

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md` (owned SelectMany as-built note)

- [ ] **Step 1: Document the as-built behavior.** In `Query/AGENTS.md`, under the owned-collection
  SelectMany note (the "Owned-collection `SelectMany` — both user-authored forms" block), add a
  paragraph: the whole-inner-element result now goes native (`$unwind` → `$replaceRoot` + root
  entity shaper) for owned collections, in all three spellings; `select o` (whole-outer),
  reference bare-entity, computed leaf, filtered inner, nested, and post-composition stay
  deferred; the decline was narrowed from whole-entity to whole-outer / non-`Inner`; note the
  tracking contract from the spike; no driver-LINQ oracle. Keep it consistent with the existing
  note's wording and cross-references.

- [ ] **Step 2: Full 3-version `/test-all`.** Invoke the `test-all` skill (foreground, per-container,
  tee-to-log, sum all three assembly summary blocks — per the test-all hygiene notes). Confirm
  **0 failures** across EF8/EF9/EF10 (unit + spec + functional). Record the pass/skip counts.

- [ ] **Step 3: `NativeOnly` spec sweep for regressions.** Run the spec suite with
  `MONGODB_EF_NATIVE_ONLY=1` on the new tip and confirm the change is purely additive — no
  previously-passing native spec test regresses (the new capability only adds passes, if any).
  Record the before/after native pass counts.

- [ ] **Step 4: Commit the docs.**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/AGENTS.md
git commit -m "EF-347: document native bare owned-collection-element SelectMany (AGENTS.md)"
```

**STOP for review** — then the standard finalize flow (whole-branch opus review → backup branch →
squash to one commit off `bcf5b30` → final 3-version verify → user fast-forward push to
`origin/NativeQueryOngoing`), per the native-stack-status memory and stacked-PR workflow.

---

## Self-Review

**Spec coverage:**
- Whole-inner owned, all three spellings → Task 3 (recognition) + Task 2 (lowering) + Task 1 (materialization).
- `$unwind` → `$replaceRoot` lowering → Task 2.
- Root-level entity shaper + tracking → Task 1 (spike) applied in Task 3.
- Narrowed `select o` decline → Task 3 Step 4 + Step 8.
- Reference / computed / filtered / nested / post-composition stay deferred → Task 3 Step 8 (retained throws) + no code touching them.
- No-oracle `NativeOnly` verification → Task 3 (functional) + Task 4 (sweep).
- Additive/non-breaking, all-internal → Global Constraints; no public surface touched.
- 3-version bar → Task 2 Step 9, Task 3 Steps 10, Task 4 Step 2.

**Placeholder scan:** The only intentionally-deferred concrete code is the materializer edit
(Task 3 Step 5), which is legitimately produced as a concrete artifact by the Task 1 spike
(findings note names the exact file/method/edit) and gated behind its own stop-for-review — not a
"TODO" but a deliberately spike-first sequencing. Everything else has concrete code/commands.

**Type consistency:** `MongoUnwindSource.WholeElement` (Task 2) is set in Task 3 Step 4 and read
in Task 2 Step 5 (lowerer). `MongoReplaceRootStage(string newRoot)`/`.NewRoot` used consistently
in Task 2 (create, lower, render, test). `IsWholeInnerEntitySelector` defined + used in Task 3.
