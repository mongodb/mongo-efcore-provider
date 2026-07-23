# Native bare whole reference-entity `SelectMany` result — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make a bare whole reference-entity result from a cross-collection reference-collection `SelectMany` (`from c in q from o in c.Orders select o`) go native, emitting `$lookup` → inner-join `$unwind` → plain `$replaceRoot` plus the standard root-level entity shaper.

**Architecture:** Reuse the owned bare-element slice's `WholeElement` recognition/shaper machinery (`dfda01e`) and slice 5's reference `$lookup`+`$unwind` machinery (`afb4964`). The one seam both left open — `WholeElement` on a `Reference`-kind `MongoUnwindSource` — is filled by: (a) admitting `Kind == Reference` at the QMTEV whole-inner-entity gate, (b) a **plain** `$replaceRoot` variant (no `$mergeObjects` sentinels — reference entities have real stored keys), (c) a **kind-aware** `IsWholeElementRepresentable` (narrow the nav guard to eager-loaded navs for reference; skip the owned-only sentinel/shadow-key checks). Materialization is expected to reuse the proven root shaper untouched — confirmed by a Task 1 spike.

**Tech Stack:** C#, EF Core (EF8/EF9/EF10 multi-target via `EF8`/`EF9`/`EF10` define constants), MongoDB C# driver, xUnit (+ plain `Assert.*`, no FluentAssertions in tests).

## Global Constraints

- **No `#if` version guards** — behavior is identical across EF8/EF9/EF10 (all touched types `internal`); a prior slice hit an EF8-only `CS9174`, so build all three early.
- **Preserve file BOMs**; `<Nullable>enable</Nullable>` on `src/` — annotate new members.
- **Not a breaking change** — hard-fail → native, results unchanged, MQL is not contract (AGENTS.md rubric). All touched types are `internal`.
- **No driver-LINQ oracle** — reference SelectMany hard-fails in every mode and the driver's LINQ v3 rejects cross-collection SelectMany; prove correctness via `MongoQueryMode.NativeOnly` succeeding + expected in-memory result set (never `Native == DriverLinq` parity).
- **Terminal-only** — any operator after the bare-entity reference SelectMany keeps hard-failing via the existing `TranslateSelectMany` `HasTerminalOperator` guard (`4e30ad2`); do not touch it.
- **Run test suites FOREGROUND** (no backgrounding / no `&`), rebuild before `--no-build`, and grep ALL three per-assembly `Passed:`/`Failed!` summary blocks (a solution `dotnet test` emits three) — a recurring stall/false-green trap.
- **Stop for review after EVERY task** (subagent-driven-development, two-stage review).
- Reuse slice 5's `RefOwnerItemDbContext`/`RefOwner`/`RefItem` fixture in `NativeSelectManyTests.cs`; add a new fixture entity only for the eager-loaded-nav decline test (Task 5).

---

### Task 1: Spike — confirm materialization, tracking, nav-guard semantics (THROWAWAY)

**Files:**
- Create (throwaway, NOT committed as production): a scratch test or `Program` exercising the queries.
- Create: `.superpowers/sdd/EF-347-ref-bare-entity-spike.md` (findings; gitignored per prior slices).

**Interfaces:**
- Consumes: nothing.
- Produces: findings that CONFIRM Approach A (or pivot to B) and finalize the Task 4/5 guard + tracking specifics. No production code survives this task.

This task is a spike: build the smallest possible harness (a temporary `[Fact]` in `NativeSelectManyTests.cs`, or a scratch console) that forces the reference bare-entity path native by hand and observes behavior. **Do not commit any production change.** Answer each question in the findings doc.

- [ ] **Step 1: Establish the current failure and the normalized tree.** Run the three bare spellings under `Native`:
  - `db.Owners.SelectMany(o => o.Refs)`
  - `from o in db.Owners from r in o.Refs select r`
  - `db.Owners.SelectMany(o => o.Refs, (o, r) => r)`

  Confirm all three throw the whole-inner `NotSupportedException` today, and (via a breakpoint/log in `TranslateSelect`) that all three reach the gate with `UnwindSource.Kind == Reference`, `Projection.Count == 0`, and a `ti => ti.Inner` selector — i.e. they normalize identically (as the owned three spellings do). Record whether `TryBindReferenceNavUnwind` fires for all three.

- [ ] **Step 2: Force the path native by hand and confirm materialization from root.** Temporarily patch (locally, uncommitted) the gate at `MongoQueryableMethodTranslatingExpressionVisitor.cs:319-321` to also admit `Kind == Reference`, and the lowerer to append a plain `$replaceRoot: { newRoot: "$_lookup_Refs" }` (hand-built `BsonDocument` is fine for the spike). Run `db.Owners.SelectMany(o => o.Refs)` under `NativeOnly` (AsNoTracking) and confirm it returns the correct `RefItem` set (Alice's 2 + Carol's 1 = 3 rows; Bob's 0 contribute none). Capture the emitted MQL.

- [ ] **Step 3: Confirm the lazy inverse nav (`RefItem.Owner`) materializes.** `RefItem` has an inverse `Owner` navigation. Confirm the bare `RefItem` shapes correctly with `Owner == null` (no `Include`) and does NOT crash the re-rooted shaper (the owned crash was specifically an *auto-included* nested nav; a lazy back-reference should be fine). This is the load-bearing finding for narrowing the nav guard.

- [ ] **Step 4: Confirm tracking.** Run the same query WITHOUT `AsNoTracking` (default tracking) under `NativeOnly`. Confirm the returned `RefItem`s are tracked (`db.ChangeTracker.Entries<RefItem>()` non-empty, one per row), and that mutating one + `SaveChanges()` persists. Record whether EF requires any provider action (it should not — reference entities are ordinary trackable entities, unlike owned).

- [ ] **Step 5: Confirm the driver-LINQ fallback has no oracle.** Run `db.Owners.SelectMany(o => o.Refs).ToList()` under explicit `DriverLinq`; confirm it throws (the driver's LINQ v3 rejects cross-collection SelectMany, per slice 5). Record the exception type.

- [ ] **Step 6: Write findings + revert.** Write `.superpowers/sdd/EF-347-ref-bare-entity-spike.md` answering Steps 1-5 and stating APPROACH A CONFIRMED (or the pivot). Revert ALL local production edits (`git checkout -- src/`); confirm `git status` shows no `src/` changes. STOP for controller review — the controller confirms/adjusts Tasks 2-5 from the findings before proceeding.

---

### Task 2: `MongoReplaceRootStage` plain variant + factory render

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/Stages/MongoReplaceRootStage.cs`
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoPipelineFactory.cs:103-112`
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoPipelineFactoryTests.cs`

**Interfaces:**
- Consumes: `MongoReplaceRootStage(string newRoot)` (existing).
- Produces: `MongoReplaceRootStage(string newRoot, bool mergeOwnerKeySentinels = true)` — `mergeOwnerKeySentinels == true` renders the existing owned `$mergeObjects` form; `false` renders a plain `{ $replaceRoot: { newRoot: "$<NewRoot>" } }`. New read-only property `bool MergeOwnerKeySentinels`.

- [ ] **Step 1: Write the failing test.** In `MongoPipelineFactoryTests.cs`, add a sibling to `ReplaceRootStage_renders_dollar_replaceRoot_with_mergeObjects_owner_key_and_ordinal`:

```csharp
[Fact]
public void ReplaceRootStage_plain_renders_dollar_replaceRoot_with_bare_newRoot()
{
    var stages = new List<MongoPipelineStage>
    {
        new MongoReplaceRootStage("_lookup_Refs", mergeOwnerKeySentinels: false)
    };
    var factory = MongoPipelineFactory.Create(stages, new MongoQueryLanguageRenderer());

    var result = factory.Build(new Dictionary<string, object?>());

    Assert.Single(result);
    Assert.Equal(
        BsonDocument.Parse("{ $replaceRoot: { newRoot: \"$_lookup_Refs\" } }"),
        result[0]);
}
```

- [ ] **Step 2: Run test to verify it fails.**

Run: `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"` then `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build --filter "FullyQualifiedName~MongoPipelineFactoryTests.ReplaceRootStage_plain_renders"`
Expected: FAIL — compile error (`mergeOwnerKeySentinels` param does not exist yet).

- [ ] **Step 3: Add the constructor param + property to `MongoReplaceRootStage`.**

```csharp
internal sealed class MongoReplaceRootStage : MongoPipelineStage
{
    public MongoReplaceRootStage(string newRoot, bool mergeOwnerKeySentinels = true)
    {
        NewRoot = newRoot;
        MergeOwnerKeySentinels = mergeOwnerKeySentinels;
    }

    public string NewRoot { get; }

    /// <summary>
    /// <see langword="true"/> (owned bare-element SelectMany): render
    /// <c>{ newRoot: { $mergeObjects: [ "$&lt;NewRoot&gt;", { __ownerKey: "$_id", __ord: "$__ord" } ] } }</c>,
    /// carrying the owner key + array ordinal in so the owned element's shadow key materializes non-null.
    /// <see langword="false"/> (reference bare-entity SelectMany): render a plain
    /// <c>{ newRoot: "$&lt;NewRoot&gt;" }</c> — a reference entity carries its own real stored key, so no
    /// sentinel merge is needed.
    /// </summary>
    public bool MergeOwnerKeySentinels { get; }

    public const string OwnerKeyField = "__ownerKey";
    public const string OrdinalField = "__ord";
}
```

(Update the class-level XML doc summary to mention both modes.)

- [ ] **Step 4: Render both arms in `MongoPipelineFactory`.** Replace the `MongoReplaceRootStage replaceRoot => …` arm (lines 103-112) with:

```csharp
MongoReplaceRootStage replaceRoot => new BsonDocument("$replaceRoot",
    new BsonDocument("newRoot", replaceRoot.MergeOwnerKeySentinels
        ? new BsonDocument("$mergeObjects", new BsonArray
        {
            "$" + replaceRoot.NewRoot,
            new BsonDocument
            {
                { MongoReplaceRootStage.OwnerKeyField, "$_id" },
                { MongoReplaceRootStage.OrdinalField, "$" + MongoReplaceRootStage.OrdinalField }
            }
        })
        : (BsonValue)("$" + replaceRoot.NewRoot))),
```

- [ ] **Step 5: Run both render tests to verify they pass.**

Run: `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"` then `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build --filter "FullyQualifiedName~MongoPipelineFactoryTests.ReplaceRootStage"`
Expected: PASS — both `ReplaceRootStage_plain_renders_dollar_replaceRoot_with_bare_newRoot` and the existing `..._with_mergeObjects_owner_key_and_ordinal` (regression guard on the default-`true` owned form).

- [ ] **Step 6: Commit.**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/Stages/MongoReplaceRootStage.cs \
        src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoPipelineFactory.cs \
        tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoPipelineFactoryTests.cs
git commit -m "EF-347: MongoReplaceRootStage plain (non-sentinel) $replaceRoot variant for reference"
```

---

### Task 3: Lowerer emits plain `$replaceRoot` for `Reference` + `WholeElement`

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoSelectLowerer.cs:122-141`
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoSelectLowererTests.cs`

**Interfaces:**
- Consumes: `MongoReplaceRootStage(newRoot, mergeOwnerKeySentinels)` (Task 2); `MongoUnwindSource.Reference(...)` with `WholeElement = true`.
- Produces: a lowered stage sequence for a `Reference` + `WholeElement` select ending in `$lookup` → `$unwind`(preserve:false) → plain `$replaceRoot`.

- [ ] **Step 1: Write the failing test.** In `MongoSelectLowererTests.cs`, add after Test 16 (`Reference_UnwindSource_lowers_to_lookup_then_unwind_then_project_stage_in_order`):

```csharp
// Test 17: bare whole reference-ENTITY SelectMany (EF-347 ref-bare-entity slice). Like Test 16,
// AppendLookupStages emits $lookup + $unwind(preserve:false) first; then WholeElement drives a PLAIN
// $replaceRoot (no $mergeObjects — a reference entity has a real stored key), and there is NO trailing
// $project (Projection is empty for a whole-entity result).
[Fact]
public void WholeElement_Reference_UnwindSource_lowers_to_lookup_then_unwind_then_plain_replaceRoot()
{
    var (query, navigation) = TestReferenceSelect();
    var lookup = new LookupExpression(navigation, forceUnwind: true);
    query.AddLookup(lookup);
    var unwind = MongoUnwindSource.Reference(
        LookupExpression.GetLookupAlias(navigation), navigation.TargetEntityType, lookup);
    unwind.WholeElement = true;
    query.Select.UnwindSource = unwind;

    var stages = new MongoSelectLowerer().Lower(query);

    Assert.Collection(stages,
        s => Assert.Same(lookup, Assert.IsType<MongoLookupStage>(s).Lookup),
        s =>
        {
            var u = Assert.IsType<MongoUnwindStage>(s);
            Assert.Same(lookup, u.Lookup);
            Assert.False(u.PreserveNullAndEmptyArrays);
        },
        s =>
        {
            var rr = Assert.IsType<MongoReplaceRootStage>(s);
            Assert.Equal(LookupExpression.GetLookupAlias(navigation), rr.NewRoot);
            Assert.False(rr.MergeOwnerKeySentinels);
        });
}
```

- [ ] **Step 2: Run test to verify it fails.**

Run: `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"` then `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build --filter "FullyQualifiedName~MongoSelectLowererTests.WholeElement_Reference_UnwindSource"`
Expected: FAIL — no `$replaceRoot` is appended for a `Reference` + `WholeElement` source (the current `WholeElement` block emits the owned sentinel-merge stage, or the `Assert.Collection` sees only `$lookup`/`$unwind`).

- [ ] **Step 3: Emit the kind-appropriate `$replaceRoot` in the lowerer.** In `MongoSelectLowerer.cs`, the `UnwindSource` block (currently lines 122-141), replace the `WholeElement` sub-block:

```csharp
if (select.UnwindSource is { } unwind)
{
    if (unwind.Kind == MongoUnwindSourceKind.Owned)
        stages.Add(new MongoUnwindFieldStage(
            unwind.InnerScopePath,
            includeArrayIndex: unwind.WholeElement ? MongoReplaceRootStage.OrdinalField : null));

    if (unwind.WholeElement)
    {
        // Bare whole-inner-element SelectMany: promote the unwound element to root.
        // Owned (embedded, shadow key): $mergeObjects the owner key + array ordinal in under sentinel
        // fields so the owned element's shadow key materializes non-null (EF-347 bare-owned spike).
        // Reference (cross-collection): the $lookup + $unwind were already appended by AppendLookupStages
        // above; a reference entity carries its own real stored key, so a PLAIN $replaceRoot suffices —
        // no sentinel merge (EF-347 ref-bare-entity slice).
        stages.Add(new MongoReplaceRootStage(
            unwind.InnerScopePath,
            mergeOwnerKeySentinels: unwind.Kind == MongoUnwindSourceKind.Owned));
        return stages;
    }

    if (select.Projection.Count > 0)
        stages.Add(new MongoProjectStage(select.Projection));
    return stages;
}
```

- [ ] **Step 4: Run the lowerer tests to verify they pass.**

Run: `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"` then `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build --filter "FullyQualifiedName~MongoSelectLowererTests"`
Expected: PASS — the new Test 17 plus the existing owned `WholeElement_UnwindSource_lowers_to_unwind_then_replaceRoot_stage_in_order` (regression: owned still emits the sentinel-merge form + `includeArrayIndex`) and Reference Test 16 (regression: projected reference still emits `$lookup`→`$unwind`→`$project`, no `$replaceRoot`).

- [ ] **Step 5: Commit.**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoSelectLowerer.cs \
        tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoSelectLowererTests.cs
git commit -m "EF-347: lower Reference+WholeElement SelectMany to $lookup/$unwind/plain-\$replaceRoot"
```

---

### Task 4: QMTEV recognition + kind-aware `IsWholeElementRepresentable` → native (success)

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs:314-345` (gate) and `:556-563` (`IsWholeElementRepresentable`)
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoUnwindSource.cs` (`WholeElement` doc — no longer "Owned-only")
- Test: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeSelectManyTests.cs`

**Interfaces:**
- Consumes: `MongoUnwindSource` with `Kind == Reference` (set by the existing `TryBindReferenceNavUnwind`); the lowering from Tasks 2-3.
- Produces: the bare reference-entity SelectMany goes native (`WholeElement = true` set for `Reference` too); `IsWholeElementRepresentable(innerEntityType, kind)` — kind-aware (eager-loaded-nav check for reference; blanket nav + sentinel/shadow-key checks for owned).

- [ ] **Step 1: Write the failing success tests.** In `NativeSelectManyTests.cs`, add the three-spellings success test and the two semantic tests. First locate and REPLACE the existing hard-fail test `Reference_form_bare_entity_result_hard_fails_in_every_mode` (≈lines 815-830) with the go-native version, and add the siblings:

```csharp
[Fact]
public void Reference_form_bare_entity_result_goes_native_all_three_spellings()
{
    using var db = CreateRefContext(MongoQueryMode.NativeOnly,
        nameof(Reference_form_bare_entity_result_goes_native_all_three_spellings), out _, out var items);

    var expectedTags = items.Select(i => i.Tag).OrderBy(t => t).ToList(); // Alice(2)+Carol(1)=3; Bob(0) none

    // 1-arg
    var oneArg = db.Owners.SelectMany(o => o.Refs).AsEnumerable().Select(r => r.Tag).OrderBy(t => t).ToList();
    // query syntax
    var querySyntax = (from o in db.Owners from r in o.Refs select r)
        .AsEnumerable().Select(r => r.Tag).OrderBy(t => t).ToList();
    // explicit result selector
    var explicitRs = db.Owners.SelectMany(o => o.Refs, (o, r) => r)
        .AsEnumerable().Select(r => r.Tag).OrderBy(t => t).ToList();

    // Succeeding under NativeOnly is itself the "went native" signal.
    Assert.Equal(expectedTags, oneArg);
    Assert.Equal(expectedTags, querySyntax);
    Assert.Equal(expectedTags, explicitRs);
}

[Fact]
public void Reference_form_bare_entity_owner_with_zero_children_contributes_no_rows()
{
    using var db = CreateRefContext(MongoQueryMode.NativeOnly,
        nameof(Reference_form_bare_entity_owner_with_zero_children_contributes_no_rows), out _, out var items);

    var result = db.Owners.SelectMany(o => o.Refs).AsEnumerable().Select(r => r.Id).OrderBy(x => x).ToList();
    var expected = items.Select(i => i.Id).OrderBy(x => x).ToList(); // Bob contributes nothing (inner join)

    Assert.Equal(expected, result);
    Assert.Equal(3, result.Count);
}

[Fact]
public void Reference_form_bare_entity_reads_root_relative_not_owner_scoped()
{
    // RefItem.Name deliberately shares its member name with RefOwner.Name. A bare-entity result is the
    // RefItem, so r.Name must be the ITEM's Name ("WidgetName"/…), read from the re-rooted document — NOT
    // the owner's Name leaking through.
    using var db = CreateRefContext(MongoQueryMode.NativeOnly,
        nameof(Reference_form_bare_entity_reads_root_relative_not_owner_scoped), out _, out var items);

    var names = db.Owners.SelectMany(o => o.Refs).AsEnumerable().Select(r => r.Name).OrderBy(n => n).ToList();
    var expected = items.Select(i => i.Name).OrderBy(n => n).ToList();

    Assert.Equal(expected, names);
    Assert.DoesNotContain("Alice", names); // no owner-Name leak
}
```

- [ ] **Step 2: Run to verify they fail.**

Run: `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"` then `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build --filter "FullyQualifiedName~NativeSelectManyTests.Reference_form_bare_entity"`
Expected: FAIL — the queries throw `NotSupportedException` under `NativeOnly` (gate still admits `Owned` only).

- [ ] **Step 3: Admit `Kind == Reference` at the gate.** In `MongoQueryableMethodTranslatingExpressionVisitor.cs`, relax the gate condition (line ~319-321):

```csharp
if (wholeEntityMember is { Member.Name: "Inner" }
    && wholeElementCandidateUnwind.Kind is MongoUnwindSourceKind.Owned or MongoUnwindSourceKind.Reference
    && IsWholeElementRepresentable(wholeElementCandidateUnwind.InnerEntityType, wholeElementCandidateUnwind.Kind))
{
    // Bare whole-inner-element SelectMany — owned (embedded) OR reference (cross-collection). The lowerer
    // emits $unwind → $replaceRoot (owned: $mergeObjects sentinel form; reference: plain, after the
    // $lookup+$unwind) and materializes the element from the re-rooted document; fall through to the
    // generic shaper fold below, which resolves TransparentIdentifier(outer, item).Inner to the element
    // shaper BuildBareNavWrappedShaper already built.
    wholeElementCandidateUnwind.WholeElement = true;
}
```

Update the surrounding comment block (lines ~285-313) to say the whole-inner case routes native for **owned AND reference**, and the `else if` decline now covers only whole-OUTER (`select c`) and an unrepresentable element (eager-loaded nav for reference; nav/sentinel/shadow-key for owned). Update the thrown message text to drop "or a whole-inner-entity result from a REFERENCE (non-owned) collection navigation" (now supported) and instead mention "a reference collection element with an eager-loaded navigation".

- [ ] **Step 4: Make `IsWholeElementRepresentable` kind-aware.** Change its signature and body (lines ~556-563):

```csharp
private static bool IsWholeElementRepresentable(IEntityType innerEntityType, MongoUnwindSourceKind kind)
{
    // Reference: a plain lazy inverse back-reference (e.g. RefItem.Owner) is never auto-included and
    // shapes fine as null — reject only an EAGER-LOADED navigation (which reaches EF's IncludeExpression
    // machinery and binds against the re-rooted shaper's wrong ProjectionMember, the owned-slice crash).
    // Owned: every owned nav is eager-loaded, so the blanket check is equivalent — keep it as the minimal,
    // lowest-risk form. The sentinel-collision / shadow-key-serialization checks below exist ONLY to protect
    // the owned $mergeObjects sentinel merge + synthesized owner/ordinal shadow keys; reference merges no
    // sentinels and has no owned-type shadow keys, so they apply for Owned only.
    if (kind == MongoUnwindSourceKind.Reference)
        return !innerEntityType.GetNavigations().Any(n => n.IsEagerLoaded);

    return !innerEntityType.GetNavigations().Any()
           && innerEntityType.GetProperties().All(p =>
               p.GetElementName() is not (MongoReplaceRootStage.OwnerKeyField or MongoReplaceRootStage.OrdinalField))
           && innerEntityType.GetComplexProperties().All(c =>
               GetComplexPropertyElementName(c) is not (MongoReplaceRootStage.OwnerKeyField or MongoReplaceRootStage.OrdinalField))
           && innerEntityType.GetProperties().Where(p => p.IsOwnedTypeKey())
               .All(NativeGroupByBinder.HasDefaultKeySerialization);
}
```

Update the method's XML doc: note it is now kind-aware and why the reference path narrows to eager-loaded navs. Update the `MongoUnwindSource.WholeElement` XML doc (drop "Owned-only in this slice (reference is deferred)").

- [ ] **Step 5: Run the success tests to verify they pass.**

Run: `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"` then `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build --filter "FullyQualifiedName~NativeSelectManyTests.Reference_form_bare_entity"`
Expected: PASS — all three success tests. (The fixture's `RefItem.Owner` is a lazy nav, so it is NOT eager-loaded and the entity is representable.)

- [ ] **Step 6: Add an MQL assertion test.** Add a logging-based test proving the emitted pipeline shape:

```csharp
[Fact]
public void Reference_form_bare_entity_emits_lookup_unwind_plain_replaceRoot()
{
    using var db = CreateRefContextWithLogging(MongoQueryMode.NativeOnly,
        nameof(Reference_form_bare_entity_emits_lookup_unwind_plain_replaceRoot),
        out _, out _, out var spyLogger);

    _ = db.Owners.SelectMany(o => o.Refs).ToList();

    var mql = spyLogger.GetLatestMql(); // use the same MQL-capture helper the sibling reference tests use
    Assert.Contains("$lookup", mql);
    Assert.Contains("$unwind", mql);
    Assert.Contains("$replaceRoot", mql);
    Assert.DoesNotContain("$mergeObjects", mql); // plain replaceRoot, not the owned sentinel form
}
```

> **Note for implementer:** mirror the exact MQL-capture mechanism the existing reference logging tests in this file use (`CreateRefContextWithLogging` + the `SpyLoggerProvider`); adjust `GetLatestMql()` to the real helper name/shape used elsewhere in the file. Do not invent a new capture path.

- [ ] **Step 7: Run to verify + commit.**

Run: `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"` then `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build --filter "FullyQualifiedName~NativeSelectManyTests.Reference_form_bare_entity"`
Expected: PASS — all four Task-4 tests.

```bash
git add src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs \
        src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoUnwindSource.cs \
        tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeSelectManyTests.cs
git commit -m "EF-347: native bare whole reference-entity SelectMany result (all three spellings)"
```

---

### Task 5: Tracking + decline/edge coverage

**Files:**
- Test: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeSelectManyTests.cs`
- Modify (fixture only): add an eager-loaded-nav reference entity to exercise the narrowed guard's decline path.

**Interfaces:**
- Consumes: the native path (Task 4). No new production code beyond the fixture — Task 4's guard already decides the decline; this task proves it and the tracking capability.
- Produces: functional coverage for tracking, eager-loaded-nav decline, whole-outer decline (retained), composition-after hard-fail (retained).

- [ ] **Step 1: Write the tracking test.**

```csharp
[Fact]
public void Reference_form_bare_entity_tracking_query_returns_tracked_entities()
{
    // Unlike an owned collection element (EF refuses to track it without its owner — see
    // Bare_SelectMany_tracking_query_throws_InvalidOperationException_in_every_mode), a reference entity is
    // an ordinary trackable entity with its own real key. A tracking query returns tracked instances.
    using var db = CreateRefContext(MongoQueryMode.NativeOnly,
        nameof(Reference_form_bare_entity_tracking_query_returns_tracked_entities), out _, out var items);

    var tracked = db.Owners.SelectMany(o => o.Refs).ToList(); // default tracking (no AsNoTracking)

    Assert.Equal(3, tracked.Count);
    Assert.Equal(3, db.ChangeTracker.Entries<RefItem>().Count());
    Assert.All(tracked, r => Assert.Equal(EntityState.Unchanged, db.Entry(r).State));

    // A mutation + SaveChanges round-trips (proves these are real tracked entities).
    var first = tracked[0];
    first.Tag = "MutatedTag";
    db.SaveChanges();
    using var verifyDb = CreateRefContextForExisting(db); // re-open on the same collections; see note
    Assert.Contains("MutatedTag", verifyDb.Refs.AsEnumerable().Select(r => r.Tag));
}
```

> **Note for implementer:** re-opening a second context on the SAME collections needs a helper — either capture the collection names from `CreateRefContext` (add an `out (string Owners, string Refs) collections` overload) or verify persistence by re-querying `db.Refs` after `db.ChangeTracker.Clear()` in the same context. Pick whichever matches existing patterns in the file; do NOT seed a fresh database (that would lose the mutation). If `EntityState`/`db.Entry` needs a using, add `using Microsoft.EntityFrameworkCore;`.

- [ ] **Step 2: Add a fixture entity with an eager-loaded navigation + its decline test.** Add a small reference fixture whose child entity eager-loads a further navigation (via `.AutoInclude()` in `OnModelCreating` or `[AutoInclude]`), so `IsWholeElementRepresentable`'s reference branch rejects it. Keep it minimal and local to the test file:

```csharp
[Fact]
public void Reference_form_bare_entity_with_eager_loaded_navigation_declines_cleanly_in_every_mode()
{
    // A reference element that EAGER-LOADS a nav reaches EF's IncludeExpression machinery against the
    // re-rooted shaper's wrong ProjectionMember (the owned-slice crash class) — declined cleanly at
    // translation time via IsWholeElementRepresentable's narrowed reference nav guard, in every mode.
    foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq, MongoQueryMode.NativeOnly })
    {
        using var db = CreateEagerRefContext(mode,
            nameof(Reference_form_bare_entity_with_eager_loaded_navigation_declines_cleanly_in_every_mode) + mode);
        Assert.Throws<NotSupportedException>(() => db.Parents.SelectMany(p => p.EagerChildren).ToList());
    }
}
```

> **Note for implementer:** add the `CreateEagerRefContext` fixture (a `DbSet<EagerParent>`/`DbSet<EagerChild>` where `EagerChild` has a nav configured `.Navigation(x => x.Something).AutoInclude()`), mirroring `RefOwnerItemDbContext`'s structure. If a lazy back-reference is somehow ALSO rejected (contradicting the spike), STOP — the spike finding was wrong and the guard needs revisiting.

- [ ] **Step 3: Confirm the whole-outer decline is retained.** Add (or confirm an existing) test that `select c` / `(c, o) => o`'s outer still throws:

```csharp
[Fact]
public void Reference_form_whole_outer_result_still_declines_cleanly_in_every_mode()
{
    foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq, MongoQueryMode.NativeOnly })
    {
        using var db = CreateRefContext(mode,
            nameof(Reference_form_whole_outer_result_still_declines_cleanly_in_every_mode) + mode, out _, out _);
        Assert.Throws<NotSupportedException>(() => db.Owners.SelectMany(o => o.Refs, (o, r) => o).ToList());
    }
}
```

- [ ] **Step 4: Confirm composition-after still hard-fails.** Add a test that an operator after the bare reference SelectMany hard-fails in every mode (reuses the SelectMany-after-terminal guard):

```csharp
[Fact]
public void Reference_form_bare_entity_followed_by_Where_hard_fails_in_every_mode()
{
    foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq, MongoQueryMode.NativeOnly })
    {
        using var db = CreateRefContext(mode,
            nameof(Reference_form_bare_entity_followed_by_Where_hard_fails_in_every_mode) + mode, out _, out _);
        Assert.ThrowsAny<Exception>(() => db.Owners.SelectMany(o => o.Refs).Where(r => r.Tag != "").ToList());
    }
}
```

- [ ] **Step 5: Run all Task-5 tests to verify they pass.**

Run: `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"` then `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build --filter "FullyQualifiedName~NativeSelectManyTests.Reference_form"`
Expected: PASS — tracking, eager-loaded-nav decline, whole-outer decline, composition hard-fail, plus all Task-4 success tests still green.

- [ ] **Step 6: Commit.**

```bash
git add tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeSelectManyTests.cs
git commit -m "EF-347: reference bare-entity SelectMany — tracking + decline/edge coverage"
```

---

### Task 6: Finalize — docs, full 3-version suite, spec sweep

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md` (as-built note under the reference SelectMany section; deferred-list update)
- Verify only: full solution, all three EF versions.

**Interfaces:**
- Consumes: everything above.
- Produces: the as-built documentation + green verification bar; nothing new is depended on.

- [ ] **Step 1: Update `Query/AGENTS.md`.** Under the "Cross-collection reference `SelectMany` — projected (EF-347 slice 5)" note, add a new as-built note documenting: the bare whole reference-entity result now goes native (all three spellings) via `$lookup`→`$unwind`(preserve:false)→plain `$replaceRoot`; the gate admits `Kind == Reference` and `IsWholeElementRepresentable` is now kind-aware (reference narrows the nav guard to eager-loaded navs; owned keeps the blanket + sentinel/shadow-key checks); reference materialization reuses the kind-agnostic shaper branch unchanged (real stored key ⇒ no sentinel read, `IsOwnedTypeKey()`-gated); **tracking is supported** (contrast owned); no driver-LINQ oracle ⇒ NativeOnly-proven; still deferred: reference entity with an eager-loaded nav (shared EF-353 root cause), whole-outer `select c`, filtered/correlated-beyond-FK inner, computed leaf, nested reference SelectMany, any op composed after. Update the slice-5 note's "Still deferred/hard-fail" list to move "bare-entity reference result" from deferred to done.

- [ ] **Step 2: Full 3-version `/test-all` (FOREGROUND).** Invoke the `/test-all` skill (or run each config foreground, teeing to a log). Confirm 0 failures on all three, and grep ALL three per-assembly `Passed:`/`Failed!` blocks per config.

Run (per config, foreground):
```
dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF8"  2>&1 | tee /tmp/ef8.log
dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF9"  2>&1 | tee /tmp/ef9.log
dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" 2>&1 | tee /tmp/ef10.log
```
Expected: `Failed: 0` in every per-assembly block of all three logs.

- [ ] **Step 3: `NativeOnly` spec sweep — prove purely additive.** Run the spec suite with `MONGODB_EF_NATIVE_ONLY=1` on the new tip and on the parent `dfda01e`, and diff the pass/fail sets.

Run:
```
MONGODB_EF_NATIVE_ONLY=1 dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests -c "Debug EF10" 2>&1 | tee /tmp/sweep-new.log
```
Expected: pass set ≥ baseline (`dfda01e`), zero regressions. The Northwind SelectMany tests are cross-collection reference — a bare-entity shape MAY newly pass; confirm no previously-passing test now fails. Record the delta.

- [ ] **Step 4: Commit the docs.**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/AGENTS.md
git commit -m "EF-347: AGENTS.md as-built — native bare reference-entity SelectMany"
```

- [ ] **Step 5: Controller finalize (NOT a subagent step).** Whole-branch opus review (`93980ab..HEAD`), squash to one commit above `dfda01e` (backup `EF-347-selectmany-ref-bare-entity-presquash` first; verify byte-identical), re-verify 3-version green on the squashed tip, then hand the user the fast-forward push command (`git push origin <tip>:NativeQueryOngoing` — plain FF, no `--force`; verify `git merge-base --is-ancestor dfda01e <tip>` first). Update the `native-stack-status` memory.

---

## Self-Review

**Spec coverage:**
- Scope in (3 spellings, FK-correlated, terminal, DOM, tracking) → Task 4 (spellings, native) + Task 5 (tracking). ✓
- Recognition (admit `Kind == Reference`) → Task 4 Step 3. ✓
- Lowering (plain `$replaceRoot`) → Task 2 (stage/render) + Task 3 (lowerer). ✓
- Materialization + tracking spike → Task 1. ✓
- Kind-aware `IsWholeElementRepresentable` (narrow nav guard for reference; owned-only sentinel/shadow-key) → Task 4 Step 4. ✓
- Deferred/decline (eager-loaded nav, whole-outer, composition-after) → Task 5 Steps 2-4. ✓
- Verification (NativeOnly + expected results, zero children, root-relative, MQL, 3-version, spec sweep) → Tasks 4, 6. ✓
- Files list (MongoUnwindSource doc, AGENTS.md) → Task 4 Step 4, Task 6 Step 1. ✓

**Placeholder scan:** No "TBD"/"implement later". The two "Note for implementer" blocks (MQL-capture helper name, tracking re-open helper) point at existing patterns to mirror rather than inventing — acceptable, since the exact helper name is discoverable in the file and inventing one would be worse. Fixture details for the eager-loaded-nav entity are described concretely (AutoInclude a nav).

**Type consistency:** `MongoReplaceRootStage(string, bool mergeOwnerKeySentinels = true)` + `bool MergeOwnerKeySentinels` used consistently in Tasks 2/3. `IsWholeElementRepresentable(IEntityType, MongoUnwindSourceKind)` signature defined in Task 4 Step 4 and called with two args in Task 4 Step 3. `MongoUnwindSource.Reference(...)`/`.WholeElement` used consistently. ✓
