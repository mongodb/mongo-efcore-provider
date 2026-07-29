# Native owned-collection count projections + EF-357 — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make an unfiltered owned-collection count leaf inside an anonymous/DTO projection emit a native `$project` (`{$size: {$ifNull: ["$path", []]}}`), and close EF-357 so the bare `Select(b => b.Posts.Count)` form returns correct results instead of throwing in every query mode.

**Architecture:** Two independent changes in the Query area. (II) The *native* half adds one accept branch to `NativeProjectionBinder.TryTranslateLeaf` (keyed on the leaf translating to a `MongoSizeExpression`) plus one projection-member registration in `MongoProjectionBindingExpressionVisitor.VisitMethodCall` gated on `Route == NativeRoute.Projection` — no new IR node, pipeline stage, or renderer arm, because the arithmetic form `Count * 2` already renders and reads back through exactly this machinery. (I) The *crash-fix* half adds a `Queryable.Count`/`LongCount`-over-a-`CollectionShaperExpression` case beside the existing `Queryable.Select` case in the same visitor, rebuilding against the `Enumerable` overload — the same move that case already makes.

**Tech Stack:** C#, EF Core 8/9/10 (build configurations `Debug EF8` / `Debug EF9` / `Debug EF10`), MongoDB C# driver, xUnit.

**Design doc:** `docs/superpowers/specs/2026-07-28-native-owned-collection-count-projections-design.md`

## Global Constraints

- **Branch:** `EF-322-owned-collection-count-projection`, stacked on native tip `1b4c1d6` (`origin/NativeQueryOngoing`). Do not rebase onto `main` — `main` has none of the native work.
- **Preserve file BOMs** on every edited file.
- `src/` is `<Nullable>enable</Nullable>` — annotate accordingly.
- **No `#if` EF-version guards are expected.** Every touched type is `internal`; behavior must be identical on EF8/EF9/EF10. If a version guard seems necessary, stop and report rather than adding one.
- **Tests use plain xUnit `Assert.*`.** FluentAssertions is not referenced by the test projects.
- **Native routing is proven only by `MongoQueryMode.NativeOnly` succeeding.** MQL shape does not distinguish native from fallback and must never be used as the routing proof.
- **`nullSafe: true` at the new call site only.** `MongoSizeExpression.NullSafe` keeps its `false` default so the EF-339 reference-collection `$lookup` rendering stays byte-identical.
- **Leave `MONGODB_URI` and `ATLAS_URI` unset** when running tests, so each run gets its own isolated `mongodb/mongodb-atlas-local` container.
- Tests run serially (assembly-level parallelization is disabled). Do not enable it.
- Commit messages start with `EF-322:`.
- **Stop for user review after every task.** Do not batch tasks.

## File Structure

| File | Responsibility | Change |
|---|---|---|
| `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeProjectionBinder.cs` | Decides whether a projection leaf is natively representable | Modify: one new accept branch in `TryTranslateLeaf` |
| `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoProjectionBindingExpressionVisitor.cs` | Builds the projection shaper (runs in all query modes) | Modify: one projection-member registration in `VisitMethodCall` (II); one `Count`/`LongCount` case in the `Queryable` switch (I) |
| `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeOwnedCollectionCountTests.cs` | Owned-collection count coverage, predicate and now projection | Modify: new projection tests; flip the EF-357 documenting test |
| `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md` | As-built area documentation | Modify: extend the `.Count` note; correct the bare-form claim |
| `docs/native-query-status-EF-322.md` | Epic status report | Modify: slice table, §3/§4/§5, EF-357 row in §6 |
| `docs/superpowers/specs/2026-07-28-native-owned-collection-count-projections-spike-findings.md` | Task 1 measured findings | Create |

---

### Task 1: Slice-0 de-risking spike (throwaway)

Three claims this plan rests on come from code reading, not measurement. Measure them before any production edit. **All spike code is discarded at the end of this task**; only the findings document is committed.

**Files:**
- Create (temporary, discarded): scratch test methods in `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/SpikeCountProjectionTests.cs`
- Create (committed): `docs/superpowers/specs/2026-07-28-native-owned-collection-count-projections-spike-findings.md`

**Interfaces:**
- Consumes: nothing.
- Produces: measured answers to Q1–Q3 below, which Task 2 and Task 3 read to set their expected test values. No code.

- [ ] **Step 1: Create the scratch test file**

Copy the fixture scaffolding (`Blog`/`Post`/`Comment`/`Home`/`Note`, `BlogModel`, `Row`, `LenRow`, `PostDoc`, `Seed`, `SeedLengths`, `CreateContext`, `UniqueCollectionName`) from `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeOwnedCollectionCountTests.cs:39-219` into a new class `SpikeCountProjectionTests`, then add:

```csharp
    [Fact]
    public void Q1_anonymous_wrapped_count_current_behaviour()
    {
        var collection = SeedLengths(nameof(Q1_anonymous_wrapped_count_current_behaviour));

        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq, MongoQueryMode.NativeOnly })
        {
            using var db = CreateContext(collection, mode, BlogModel);
            try
            {
                var rows = db.Entities.AsNoTracking()
                    .Select(b => new { b.Title, N = b.Posts.Count })
                    .ToList().OrderBy(r => r.Title).ToList();
                Console.WriteLine($"Q1 {mode}: OK -> {string.Join(",", rows.Select(r => $"{r.Title}={r.N}"))}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Q1 {mode}: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    [Fact]
    public void Q3_driver_oracle_for_wrapped_count()
    {
        // Same query, DriverLinq only, split across the full-matrix seed and the well-formed-only seed, to find
        // out whether the driver's own count rendering survives missing / explicitly-null arrays.
        foreach (var (label, collection) in new[]
                 {
                     ("full", SeedLengths(nameof(Q3_driver_oracle_for_wrapped_count) + "full")),
                     ("wellformed", SeedWellFormed(nameof(Q3_driver_oracle_for_wrapped_count) + "wf"))
                 })
        {
            using var db = CreateContext(collection, MongoQueryMode.DriverLinq, BlogModel);
            try
            {
                var rows = db.Entities.AsNoTracking()
                    .Select(b => new { b.Title, N = b.Posts.Count })
                    .ToList().OrderBy(r => r.Title).ToList();
                Console.WriteLine($"Q3 {label}: OK -> {string.Join(",", rows.Select(r => $"{r.Title}={r.N}"))}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Q3 {label}: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
```

Also copy `SeedWellFormed` (`NativeOwnedCollectionCountTests.cs:218-219`) into the scratch class.

- [ ] **Step 2: Run Q1 and Q3 and record the raw output**

Run:
```bash
dotnet build tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj -c "Debug EF10"
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~SpikeCountProjectionTests" --logger "console;verbosity=detailed"
```

Expected: the tests pass (they swallow exceptions); the answers are in the console output. Record verbatim.

**Q1** — does the anonymous-wrapped count crash today? The plan's reading says `ArgumentException` in all three modes. If instead it succeeds under `Native`/`DriverLinq`, Task 2 becomes fallback→native and its tests must add a `Native == DriverLinq` parity leg.

**Q3** — is there a driver-LINQ oracle at all, and does it survive missing/null arrays?

- [ ] **Step 3: Temporarily apply the Task 3 fix and answer Q2**

Apply *only* the Task 3 production edit (see Task 3, Step 3 — the `Queryable.Count`/`LongCount` case), then add and run:

```csharp
    [Fact]
    public void Q2_bare_count_behaviour_once_unblocked()
    {
        var collection = SeedLengths(nameof(Q2_bare_count_behaviour_once_unblocked));

        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq, MongoQueryMode.NativeOnly })
        {
            using var db = CreateContext(collection, mode, BlogModel);
            try
            {
                var counts = db.Entities.AsNoTracking().Select(b => b.Posts.Count).ToList().OrderBy(n => n).ToList();
                Console.WriteLine($"Q2 {mode}: OK -> {string.Join(",", counts)}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Q2 {mode}: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
```

Run the same command as Step 2. Record: does the bare form return `0,0,0,1,2,3`? Under which modes? Is `NativeOnly` a clean `NativeTranslationNotSupportedException` (expected — a bare-scalar projection is `Route == Fallback`, and going native was explicitly out of scope)? Capture the emitted MQL from the log to determine whether the count was computed server-side (`$size` present) or client-side (whole `Posts` array fetched).

- [ ] **Step 4: Write the findings document**

Create `docs/superpowers/specs/2026-07-28-native-owned-collection-count-projections-spike-findings.md` with one section per question, each stating the question, the exact command run, the verbatim output, and the verdict. Add a final "Consequences for the plan" section listing every place Task 2–5 must change if a measurement contradicted the plan's assumption. State plainly which claims were CONFIRMED and which were MEASURED FALSE — a false one is a finding about the plan, not about the measurement.

- [ ] **Step 5: Discard all spike code**

```bash
git checkout -- src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoProjectionBindingExpressionVisitor.cs
rm tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/SpikeCountProjectionTests.cs
git status --short
```

Expected: the only untracked/modified file is the findings document.

- [ ] **Step 6: Commit the findings**

```bash
git add docs/superpowers/specs/2026-07-28-native-owned-collection-count-projections-spike-findings.md
git commit -m "EF-322: spike findings for owned-collection count projections"
```

**STOP for user review.**

---

### Task 2: Native owned-collection count projection leaf

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeProjectionBinder.cs` (`TryTranslateLeaf`, after the arithmetic branch at `:135-141`)
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoProjectionBindingExpressionVisitor.cs` (`VisitMethodCall`, after the `TryBindProjectedCollectionNavigationCount` block ending at `:327`)
- Test: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeOwnedCollectionCountTests.cs`

**Interfaces:**
- Consumes: `MongoExpressionTranslator.TryTranslateValue(Expression, out MongoExpression)` — already reachable and already yields `MongoSizeExpression(arrayPath, type, nullSafe: true)` for an owned-collection count via `TranslateOperand` (`MongoExpressionTranslator.cs:581-585`). `MongoSizeExpression` is in namespace `MongoDB.EntityFrameworkCore.Query.Expressions`, already imported by `NativeProjectionBinder.cs:21`.
- Produces: an owned-collection count leaf inside a `NewExpression` or `MemberInitExpression` projection populates `Select.Projection` with a `MongoSizeExpression`, so `Route` resolves to `NativeRoute.Projection`. Task 4 relies on this for `.Count`, `.Count()`, `.LongCount()`, and owned single-reference hops.

- [ ] **Step 1: Write the failing tests**

Append to `NativeOwnedCollectionCountTests.cs`, immediately after `Arithmetic_projection_leaf_containing_a_count_goes_native` (which ends at `:440`):

```csharp
    [Fact]
    public void Owned_collection_count_projection_leaf_goes_native()
    {
        // The plain sibling of Arithmetic_projection_leaf_containing_a_count_goes_native above: `Count` on its
        // own, not wrapped in arithmetic. Before this slice the arithmetic form was native while the plain form
        // was not — the count reached TranslateOperand only as an operand of something else.
        var collection = SeedLengths(nameof(Owned_collection_count_projection_leaf_goes_native));

        using var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel);

        var rows = db.Entities.AsNoTracking()
            .Select(b => new { b.Title, N = b.Posts.Count })
            .ToList().OrderBy(r => r.Title).ToList();

        Assert.Equal(
            [("len0", 0), ("len1", 1), ("len2", 2), ("len3", 3), ("missing", 0), ("null", 0)],
            rows.Select(r => (r.Title, r.N)).ToArray());
    }

    [Fact]
    public void Owned_collection_count_projection_emits_a_null_safe_size()
    {
        // $ifNull is MANDATORY, not defensive: $size against a missing or explicitly-null array is a hard server
        // error that aborts the whole aggregate, not merely a wrong answer. The "missing" and "null" rows in this
        // seed are what would abort without it.
        var collection = SeedLengths(nameof(Owned_collection_count_projection_emits_a_null_safe_size));

        using var db = CreateContextWithLogging(collection, MongoQueryMode.NativeOnly, BlogModel, out var spyLogger);

        _ = db.Entities.AsNoTracking().Select(b => new { b.Title, N = b.Posts.Count }).ToList();

        var mql = spyLogger.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery);
        Assert.Contains("$project", mql);
        Assert.Contains("$size", mql);
        Assert.Contains("$ifNull", mql);
        Assert.Contains("Posts", mql);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run:
```bash
dotnet build tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj -c "Debug EF10"
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~NativeOwnedCollectionCountTests.Owned_collection_count_projection"
```

Expected: both FAIL with `ArgumentException` — "Expression of type 'System.Collections.Generic.List\`1[…Post]' cannot be used for parameter of type 'System.Linq.IQueryable\`1[…Post]' of method 'Int32 Count[Post](System.Linq.IQueryable\`1[…Post])' (Parameter 'arg0')". The Task 1 spike CONFIRMED this exact failure in all three query modes. **If the observed failure differs, stop and report** — the plan's model of this code path is wrong and Task 2's design needs revisiting before implementing.

Two consequences of the spike that this task depends on, recorded so they are not re-derived:

- **No `Native == DriverLinq` parity leg is possible or needed.** The shape throws in all three modes today, so there is no working behavior to be parity with. The in-memory oracle (Task 4) is the gate.
- **The native form will be MORE correct than the fallback**, and that is intended. `$ifNull` makes the native count `0` for a missing or explicitly-null array, matching whole-entity materialization; the fallback path throws `ArgumentNullException` on those same rows (see Task 3). Do not "reconcile" the two by weakening the native side.

- [ ] **Step 3: Add the binder accept branch**

In `NativeProjectionBinder.cs`, insert immediately after the arithmetic branch (after the closing `}` of the `if` at `:135-141`) and before `result = null!;`:

```csharp
        // Owned (embedded) collection count leaf (EF-322): `new { N = b.Posts.Count }`. The same
        // TryMatchCountExpression + TryResolveOwnedCollectionPath pair the arithmetic branch above already
        // reaches through TryTranslateValue — a count inside `Count * 2` has been native since the .Count
        // predicate slice — just with no arithmetic wrapped around it.
        //
        // Gate on the resulting NODE KIND, not on "TryTranslateValue succeeded". That is the same hazard the
        // arithmetic branch's binary-top-node restriction exists for: a bare constant/parameter leaf renders as a
        // bare value that $project misreads as an INCLUSION FLAG ({X: 1}) rather than a literal. { $size: ... } is
        // a document, so it is safe exactly where a bare value is not. Widening this to any translated value would
        // reintroduce that bug.
        //
        // Ordering: this runs AFTER the arithmetic branch so an arithmetic leaf containing a count is still bound
        // by that branch (as a MongoBinaryExpression) and TryTranslateValue is not called twice for it.
        if (translator.TryTranslateValue(leafExpression, out var value) && value is MongoSizeExpression size)
        {
            result = size;
            return true;
        }
```

- [ ] **Step 4: Add the projection-member registration**

In `MongoProjectionBindingExpressionVisitor.cs`, insert immediately after the `TryBindProjectedCollectionNavigationCount` block (after the closing `}` at `:327`) and before the `if (methodCallExpression.TryGetEFPropertyArguments(...))` block at `:329`:

```csharp
        // An OWNED (embedded) collection-navigation count leaf in a native projection (EF-322):
        // `select new { ..., N = b.Posts.Count }`. Register the whole Count/LongCount call as ONE projection
        // member, exactly like the arithmetic case in Visit above. Without this, the walk continues to the
        // generic fall-through below, which calls methodCallExpression.Update(...) with a
        // CollectionShaperExpression typed List<T> against a parameter typed IQueryable<T>; BCL expression
        // validation rejects that with ArgumentException — in EVERY query mode, since this fold runs before
        // MongoQueryMode is ever read (that is EF-357, fixed separately for the bare-scalar form below).
        //
        // POSITION IS LOAD-BEARING, in both directions:
        //  * It must come AFTER TryBindProjectedCollectionNavigationCount above. A projected REFERENCE-collection
        //    count (EF-339) is also a 1-arg Queryable.Count call and is also Route == Projection, so a
        //    Route-only guard cannot tell the two apart — the earlier binder claiming it first is what keeps the
        //    $lookup + $size path intact.
        //  * It must come BEFORE the generic Queryable switch below, whose Count case (EF-357) rebuilds the call
        //    against the Enumerable overload for CLIENT-side counting. That is the right answer for a fallback
        //    shape and the wrong one here, where the count is pushed into $project.
        //
        // The Route == Projection guard is load-bearing for the same reason it is on the arithmetic case:
        // NativeProjectionBinder sets Route = Projection only when EVERY leaf is natively representable, so a
        // mixed or fallback shape must fall through untouched.
        if (_queryExpression.Select.Route == NativeRoute.Projection
            && methodCallExpression.Arguments.Count == 1
            && methodCallExpression.Method.Name is nameof(Enumerable.Count) or nameof(Enumerable.LongCount)
            && (methodCallExpression.Method.DeclaringType == typeof(Enumerable)
                || methodCallExpression.Method.DeclaringType == typeof(Queryable)))
        {
            var countProjectionMember = GetCurrentProjectionMember();
            _projectionMapping[countProjectionMember] = methodCallExpression;
            return new ProjectionBindingExpression(_queryExpression, countProjectionMember, methodCallExpression.Type);
        }
```

If `System.Linq` or `MongoDB.EntityFrameworkCore.Query.Expressions` (for `NativeRoute`) is not already imported in this file, add the `using`. Preserve the file BOM.

- [ ] **Step 5: Run the tests to verify they pass**

Run the same command as Step 2. Expected: both PASS.

- [ ] **Step 6: Run the whole owned-count and projection-adjacent suites for regressions**

Run:
```bash
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~NativeOwnedCollectionCountTests|FullyQualifiedName~QueryModeGateIncludeTests|FullyQualifiedName~OwnedEntityTests|FullyQualifiedName~NativeProjectionTests"
```

Expected: all PASS. In particular `Arithmetic_projection_leaf_containing_a_count_goes_native` and the reference-collection tests `QueryModeGateIncludeTests.Projected_collection_Count*` must be green — those two are the direct regression risks of Steps 3 and 4 respectively.

- [ ] **Step 7: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeProjectionBinder.cs \
        src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoProjectionBindingExpressionVisitor.cs \
        tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeOwnedCollectionCountTests.cs
git commit -m "EF-322: owned-collection count projection leaf goes native"
```

**STOP for user review.**

---

### Task 3: EF-357 — the bare count projection stops crashing at translation time

> **Post-spike scope (user decision, Task 1).** This ships the NARROW fix: correct values for documents whose
> embedded array is present, the missing/explicitly-null `ArgumentNullException` pinned as a documented
> residual, and a new ticket filed for the projection-path null normalization. EF-357 is resolved as
> **partial**, not closed outright. Do not widen this task to fix the shaper null — that changes results for
> collection projections that work today and is its own decision.

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoProjectionBindingExpressionVisitor.cs` (the `method.DeclaringType == typeof(Queryable)` switch at `:431-454`)
- Test: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeOwnedCollectionCountTests.cs` (replace `Bare_embedded_collection_Count_projection_is_a_known_preexisting_limitation` at `:442-461`)

**Interfaces:**
- Consumes: `EnumerableMethods.CountWithoutPredicate` and `EnumerableMethods.LongCountWithoutPredicate` (`src/MongoDB.EntityFrameworkCore/EnumerableMethods.cs:105-106`) — both already exist. `QueryableMethods.CountWithoutPredicate` / `.LongCountWithoutPredicate` come from EF Core.
- Produces: nothing consumed by later tasks. Task 4 asserts the (I)/(II) disjointness this task creates.

- [ ] **Step 1: Replace the EF-357 documenting test with a failing behavior test**

Replace the whole of `Bare_embedded_collection_Count_projection_is_a_known_preexisting_limitation` (`:442-461`) with:

```csharp
    [Fact]
    public void Bare_embedded_collection_Count_projection_returns_correct_counts_for_present_arrays()
    {
        // EF-357, PARTIALLY closed — see the residual pinned by the companion test below before editing this one.
        //
        // This shape used to throw ArgumentException in EVERY query mode — not a graceful fallback, no data at
        // all — because the projection-binding shaper fold (which runs at TRANSLATION time, before
        // MongoQueryMode is read) rebuilt Queryable.Count over a CollectionShaperExpression typed List<T> and BCL
        // expression validation rejected the mismatch. It predates the whole EF-322 native work stream.
        //
        // It is deliberately NOT native: a bare-scalar terminal projection never populates Select.Projection — a
        // pre-existing SP3-wide boundary, not a count-specific one — so Route stays Fallback and NativeOnly still
        // declines cleanly. Closing EF-357 was about correct results, not about routing.
        //
        // MEASURED (Task 1 spike, not assumed): the count is folded CLIENT-SIDE over the fetched document — the
        // emitted pipeline is aggregate([]), with no $size and no $project. The driver's LINQ provider is never
        // asked to render the count, because the rebuilt Enumerable.Count runs over an already-materialized
        // collection shaper. This seed is deliberately SeedWellFormed for the reason the companion test explains.
        var collection = SeedWellFormed(
            nameof(Bare_embedded_collection_Count_projection_returns_correct_counts_for_present_arrays));

        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq })
        {
            using var db = CreateContext(collection, mode, BlogModel);
            var counts = db.Entities.AsNoTracking().Select(b => b.Posts.Count).ToList().OrderBy(n => n).ToList();
            Assert.Equal(new[] { 0, 1, 2, 3 }, counts);
        }

        using (var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel))
        {
            Assert.Throws<NativeTranslationNotSupportedException>(
                () => db.Entities.AsNoTracking().Select(b => b.Posts.Count).ToList());
        }
    }

    [Fact]
    public void Bare_embedded_collection_Count_projection_still_throws_for_a_missing_or_null_array()
    {
        // THE DOCUMENTED RESIDUAL of EF-357, pinned deliberately rather than left to be rediscovered.
        //
        // The fix above resolves the TRANSLATION-time crash. It does not make this shape work for a document
        // whose embedded array is MISSING or explicitly BSON null: the PROJECTION path's CollectionShaperExpression
        // materializes null rather than an empty list for those two states, so the client-side fold calls
        // Enumerable.Count(null) and throws ArgumentNullException.
        //
        // The asymmetry is the real finding, and it is NOT count-specific: whole-entity materialization of the
        // very same documents yields Posts.Count == 0 for all three states (empty / missing / explicit null).
        // Only the projection path fails to normalize. Normalizing it would change results for collection
        // projections that work today (Select(b => b.Posts) currently returns null for these rows), so it is
        // tracked as its own ticket rather than widened into this slice.
        //
        // Note the native wrapped form is unaffected and CORRECT for all three states — $ifNull maps a
        // missing/null array to 0 server-side, never reaching this client-side fold.
        var collection = SeedLengths(
            nameof(Bare_embedded_collection_Count_projection_still_throws_for_a_missing_or_null_array));

        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq })
        {
            using var db = CreateContext(collection, mode, BlogModel);
            Assert.Throws<ArgumentNullException>(
                () => db.Entities.AsNoTracking().Select(b => b.Posts.Count).ToList());
        }
    }
```

Both tests encode Task 1's measurements exactly. **If either behaves differently, stop and report rather than adjusting the assertion** — the spike measured all of it directly, so a divergence means something changed between Task 1 and now.

- [ ] **Step 2: Run the test to verify it fails**

Run:
```bash
dotnet build tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj -c "Debug EF10"
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~Bare_embedded_collection_Count_projection"
```

Expected: BOTH FAIL with `ArgumentException` ("Expression of type 'System.Collections.Generic.List\`1[...Post]' cannot be used for parameter of type 'System.Linq.IQueryable\`1[...Post]'") — including the residual test, which expects `ArgumentNullException` but gets the translation-time `ArgumentException` until the fix lands. That is the point: before the fix, every row state fails the same way; after it, the two states separate.

- [ ] **Step 3: Add the `Count`/`LongCount` case**

In `MongoProjectionBindingExpressionVisitor.cs`, inside the `switch (method.Name)` at `:431`, add after the `nameof(Queryable.Select)` case (which ends at `:453`):

```csharp
                // EF-357: Count/LongCount over a materialized collection shaper. EF hands us
                // Queryable.Count(IQueryable<T>), but the visited source is a CollectionShaperExpression whose
                // Type is the navigation's CLR type (List<T>) — and MatchTypes below refuses to insert a
                // conversion for any target type with an item type, so the generic fall-through's
                // methodCallExpression.Update(...) throws ArgumentException from BCL expression validation. Since
                // this fold runs before MongoQueryMode is read, that crash fired in Native, DriverLinq and
                // NativeOnly alike. Rebuilding against the Enumerable overload — exactly what the Select case
                // above already does for the same source shape — counts the materialized collection instead.
                //
                // Deliberately narrow. The underlying defect is MatchTypes (see below), which strands First/Any/
                // Sum/... on the same fall-through; repairing that changes type coercion on a path every
                // projection walks, in all three modes, so it is left as a follow-on. This case can only fire on
                // a shape that throws today.
                //
                // Unreachable for a NATIVE count projection: that is claimed earlier in VisitMethodCall by the
                // Route == Projection registration, which pushes the count into $project instead.
                case nameof(Queryable.Count)
                    when genericMethod == QueryableMethods.CountWithoutPredicate:
                case nameof(Queryable.LongCount)
                    when genericMethod == QueryableMethods.LongCountWithoutPredicate:
                    if (visitedSource is not CollectionShaperExpression countShaper)
                    {
                        return null;
                    }

                    return Expression.Call(
                        (method.Name == nameof(Queryable.Count)
                            ? EnumerableMethods.CountWithoutPredicate
                            : EnumerableMethods.LongCountWithoutPredicate)
                        .MakeGenericMethod(method.GetGenericArguments()),
                        countShaper);
```

- [ ] **Step 4: Run the test to verify it passes**

Run the same command as Step 2. Expected: PASS.

- [ ] **Step 5: Run the surrounding suites**

Run:
```bash
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~Query"
```

Expected: all PASS.

- [ ] **Step 6: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoProjectionBindingExpressionVisitor.cs \
        tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeOwnedCollectionCountTests.cs
git commit -m "EF-322: EF-357 - bare embedded-collection Count projection no longer fails translation"
```

**STOP for user review.**

---

### Task 4: Breadth, the differential oracle, and disjointness

**Files:**
- Test: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeOwnedCollectionCountTests.cs`

**Interfaces:**
- Consumes: everything Tasks 2 and 3 produced. Uses the existing helpers `Seed`, `SeedLengths`, `RowWithNotes`, `DifferentialRows` (`:748-755`), `CreateContext`.
- Produces: the coverage that lets Task 5 update the documentation with measured, not assumed, claims.

- [ ] **Step 1: Write the breadth and oracle tests**

Add a DTO next to the `Blog`/`Post` fixture classes (after `Note`, `:124`):

```csharp
    // A named DTO, not an anonymous type, so the SAME Expression<Func<Blog, TitleCount>> can be sent to the
    // server AND compiled for the in-memory oracle. It also exercises NativeProjectionBinder's MemberInit
    // branch, which the anonymous-type tests do not reach.
    public class TitleCount
    {
        public string Title { get; set; } = "";
        public int N { get; set; }
    }
```

Then append these tests:

```csharp
    public static TheoryData<string, Expression<Func<Blog, TitleCount>>> CountProjectionShapes() => new()
    {
        { "property", b => new TitleCount { Title = b.Title, N = b.Posts.Count } },
        { "call", b => new TitleCount { Title = b.Title, N = b.Posts.Count() } },
        { "arithmetic", b => new TitleCount { Title = b.Title, N = b.Posts.Count * 2 } },
    };

    [Theory]
    [MemberData(nameof(CountProjectionShapes))]
    public void Count_projection_equals_the_in_memory_oracle_for_every_array_length_and_state(
        string name, Expression<Func<Blog, TitleCount>> selector)
    {
        // The differential gate, mirroring Count_result_equals_the_in_memory_oracle_for_every_array_length_and_
        // state for the predicate half: the SAME Expression object is sent to the server and compiled for
        // client-side evaluation, so the two sides cannot silently diverge the way two hand-written projections
        // can. The seed's missing / explicitly-null Posts rows are the ones a bare $size would abort on.
        var collection = Seed($"projdiff_{name}", DifferentialRows());

        List<(string Title, int N)> expected;
        using (var db = CreateContext(collection, MongoQueryMode.Native, BlogModel))
        {
            var compiled = selector.Compile();
            expected = db.Entities.AsNoTracking().ToList()
                .Select(compiled).Select(r => (r.Title, r.N)).OrderBy(r => r.Title).ToList();
        }

        List<(string Title, int N)> actual;
        using (var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel))
        {
            actual = db.Entities.AsNoTracking().Select(selector)
                .ToList().Select(r => (r.Title, r.N)).OrderBy(r => r.Title).ToList();
        }

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void LongCount_projection_leaf_goes_native()
    {
        var collection = SeedLengths(nameof(LongCount_projection_leaf_goes_native));

        using var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel);

        var rows = db.Entities.AsNoTracking()
            .Select(b => new { b.Title, N = b.Posts.LongCount() })
            .ToList().OrderBy(r => r.Title).ToList();

        Assert.Equal(
            [("len0", 0L), ("len1", 1L), ("len2", 2L), ("len3", 3L), ("missing", 0L), ("null", 0L)],
            rows.Select(r => (r.Title, r.N)).ToArray());
    }

    [Fact]
    public void Count_projection_through_an_owned_reference_hop_goes_native()
    {
        // b.Home.Notes.Count — TryResolveOwnedCollectionPath walks the owned single-reference hop and yields the
        // dotted array path "Home.Notes", the same breadth the predicate half covers.
        var collection = Seed(nameof(Count_projection_through_an_owned_reference_hop_goes_native),
            RowWithNotes("none", 0), RowWithNotes("one", 1), RowWithNotes("three", 3));

        using var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel);

        var rows = db.Entities.AsNoTracking()
            .Select(b => new { b.Title, N = b.Home.Notes.Count })
            .ToList().OrderBy(r => r.Title).ToList();

        Assert.Equal(
            [("none", 0), ("one", 1), ("three", 3)],
            rows.Select(r => (r.Title, r.N)).ToArray());
    }

    [Fact]
    public void Count_projection_alongside_sibling_leaves_goes_native()
    {
        var collection = SeedLengths(nameof(Count_projection_alongside_sibling_leaves_goes_native));

        using var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel);

        var rows = db.Entities.AsNoTracking()
            .Select(b => new { b.Title, N = b.Posts.Count, Doubled = b.Posts.Count * 2, Notes = b.Home.Notes.Count })
            .ToList().OrderBy(r => r.Title).ToList();

        Assert.Equal(
            [("len0", 0, 0, 0), ("len1", 1, 2, 0), ("len2", 2, 4, 0), ("len3", 3, 6, 0),
                ("missing", 0, 0, 0), ("null", 0, 0, 0)],
            rows.Select(r => (r.Title, r.N, r.Doubled, r.Notes)).ToArray());
    }

    [Fact]
    public void Bare_and_wrapped_count_projections_take_different_paths_from_the_same_model()
    {
        // The (I)/(II) disjointness proof. The two halves of this slice fire on the same LINQ construct in the
        // same model and must not collide: the WRAPPED form populates Select.Projection (Route == Projection) and
        // is pushed into $project, so NativeOnly succeeds; the BARE form is a bare-scalar projection that never
        // populates Projection (Route == Fallback), so NativeOnly declines — and only the EF-357 Enumerable.Count
        // rebuild applies. Ordering inside VisitMethodCall is what keeps them apart; assert it, don't assume it.
        var collection = SeedLengths(nameof(Bare_and_wrapped_count_projections_take_different_paths_from_the_same_model));

        using (var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel))
        {
            var wrapped = db.Entities.AsNoTracking()
                .Select(b => new { N = b.Posts.Count }).ToList().Select(r => r.N).OrderBy(n => n).ToList();
            Assert.Equal(new[] { 0, 0, 0, 1, 2, 3 }, wrapped);

            Assert.Throws<NativeTranslationNotSupportedException>(
                () => db.Entities.AsNoTracking().Select(b => b.Posts.Count).ToList());
        }
    }

    [Fact]
    public void Filtered_count_projection_still_declines()
    {
        // Deliberately out of scope for this slice (see the design doc §3): a predicated count would render as
        // $size over $filter, which the aggregation dialect CAN express — unlike the predicate half, where the
        // blocker was that $elemMatch admits no $expr. It is deferred for cost, not impossibility, so pin the
        // current decline to make a future slice's flip visible.
        var collection = SeedLengths(nameof(Filtered_count_projection_still_declines));

        using (var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel))
        {
            Assert.Throws<NativeTranslationNotSupportedException>(
                () => db.Entities.AsNoTracking()
                    .Select(b => new { b.Title, N = b.Posts.Count(p => p.Rank > 0) }).ToList());
        }

        using (var db = CreateContext(collection, MongoQueryMode.Native, BlogModel))
        {
            var rows = db.Entities.AsNoTracking()
                .Select(b => new { b.Title, N = b.Posts.Count(p => p.Rank > 0) })
                .ToList().OrderBy(r => r.Title).ToList();
            Assert.Equal(
                [("len0", 0), ("len1", 0), ("len2", 1), ("len3", 2), ("missing", 0), ("null", 0)],
                rows.Select(r => (r.Title, r.N)).ToArray());
        }
    }
```

`System.Linq.Expressions` is already imported (`:19`).

- [ ] **Step 2: Run them**

Run:
```bash
dotnet build tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj -c "Debug EF10"
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~NativeOwnedCollectionCountTests"
```

Expected: all PASS.

**Two failures are informative rather than fatal and must be reported, not patched away.** If `Filtered_count_projection_still_declines` throws under `Native` (rather than falling back to correct values), the filtered form has no driver-LINQ oracle either and the test becomes a hard-fail-every-mode assertion — record that, it changes the follow-on's difficulty. If the `arithmetic` row of the oracle theory fails while `property` and `call` pass, Step 3 of Task 2 has disturbed the pre-existing incidental widening — that is a genuine regression; stop and report.

- [ ] **Step 3: Commit**

```bash
git add tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeOwnedCollectionCountTests.cs
git commit -m "EF-322: breadth, differential oracle and disjointness coverage for count projections"
```

**STOP for user review.**

---

### Task 5: Validate across all three EF versions, sweep the spec suite, and document

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md`
- Modify: `docs/native-query-status-EF-322.md`

**Interfaces:**
- Consumes: the measured outcomes of Tasks 1–4.
- Produces: the as-built record. Nothing consumes it.

- [ ] **Step 1: Run the full three-version suite**

Invoke the `/test-all` skill (`.claude/skills/test-all/`), which builds and tests `Debug EF8`, `Debug EF9`, and `Debug EF10` in parallel.

Expected: **0 failures on all three**, with a uniform pass-count delta (this slice adds no `#if`, so a non-uniform delta means something is version-conditional that should not be — investigate before proceeding).

- [ ] **Step 2: Sweep the EF10 spec suite on both axes**

```bash
dotnet build tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj -c "Debug EF10"

dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --logger "trx;LogFileName=native.trx" --results-directory /tmp/ef322-projcount

MONGODB_EF_NATIVE_ONLY=1 dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --logger "trx;LogFileName=nativeonly.trx" --results-directory /tmp/ef322-projcount
```

Expected: `Native` 4589 passed / 0 failed / 19 skipped; `NativeOnly` 2194 / 2395 / 19 — unchanged from the baseline in `docs/native-query-status-EF-322.md` §7, because Northwind has no owned collections.

**Check BOTH axes per test, not just the `NativeOnly` pass set.** A test can be `NativeOnly`-failing *and* have a `Native`-mode MQL baseline that a slice changes; an inventory built only from the pass set missed exactly that once (`Select_All`, owned-data slice 5). `Native` failing zero tests is itself the proof no `Native`-mode MQL baseline moved — any baseline that changed shows up as a failure against its checked-in string. If `Native` shows any failure, re-baseline per the `EF_TEST_REWRITE_BASELINES=1` procedure in `tests/MongoDB.EntityFrameworkCore.SpecificationTests/AGENTS.md` and report which tests moved and why.

Note also: `Customers.SelectMany(c => c.Orders)`-style reference-collection tests exercise the `Queryable` switch this slice touched. Confirm `NorthwindSelectQueryMongoTest` and `NorthwindIncludeQueryMongoTest` are unchanged.

- [ ] **Step 3: Extend the `.Count` note in `Query/AGENTS.md`**

In `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md`, in the **"Owned-collection `.Count` in a predicate (EF-322)"** note, make three edits:

1. In its **Deferred, precisely** list, replace the `.Count` BARE in a PROJECTION entry with a statement of what now happens: a count leaf inside an anonymous/DTO projection goes native as `{$size: {$ifNull: […]}}`; the bare-scalar form no longer fails translation and returns correct values for present arrays, but stays on the fallback path (a bare-scalar projection never populates `Projection` — the pre-existing SP3-wide boundary, not a count-specific one), is folded **client-side** over an `aggregate([])` pipeline, and still throws `ArgumentNullException` for a missing or explicitly-null array. EF-357 is **partially** resolved — say so, do not claim a clean close.
2. Add a new paragraph documenting: the binder's node-kind gate and why it is node-kind rather than "any translated value" (the `{X: 1}` inclusion-flag hazard); the `VisitMethodCall` registration and **both** ordering constraints (after `TryBindProjectedCollectionNavigationCount` so a reference-collection count keeps its `$lookup` + `$size` path, before the `Queryable` switch so a native count is not rebuilt for client-side counting); and the EF-357 fix with the `MatchTypes` root cause recorded as an untaken follow-on.
3. **Correct in place** — do not rewrite — the existing statements this slice proved wrong or stale. Follow this file's established correction style: state what the note used to say, and what was measured instead.
   - The `Any` note's claim that an embedded-collection projection is "a MIXED picture" where only the bare form hard-fails (Task 1 Q1 measured what the anonymous-wrapped form actually did), and the same claim repeated in the `.Count` note's deferred list.
   - **The `.Count` note's deferred entry for a FILTERED `Count(pred)`.** It is listed among shapes that fall back; Task 4 measured that a filtered count *projection* — `Select(b => new { N = b.Posts.Count(p => p.Rank > 0) })` — throws `InvalidOperationException` ("could not be translated") identically under `Native`, `DriverLinq` **and** `NativeOnly`. It is a translation-time crash in `MongoProjectionBindingExpressionVisitor.Translate`, pre-existing and unrelated to this slice, not a graceful decline. Pinned by `NativeOwnedCollectionCountTests.Filtered_count_projection_is_a_known_preexisting_hard_fail_in_every_mode`.
   - **The whole-entity-vs-projection null asymmetry** (Task 1 Q4): whole-entity materialization normalizes a missing or explicitly-null embedded array to an empty list; the projection path materializes `null`. Record it as a general property of the projection path, not as a count detail.

- [ ] **Step 4: Update the status report**

In `docs/native-query-status-EF-322.md`:

1. Add slice 7 to the owned-data table in §2 with the new commit hash.
2. Add a bullet to §3 describing the native count projection leaf.
3. In §4, remove the embedded-collection-projection entry's count half from the fallback list, leaving array projections; the bare-scalar count moves from "hard-fails in every mode" to "correct results via a client-side fold for present arrays, still throws `ArgumentNullException` for a missing or explicitly-null array".
4. In §5, drop "embedded-collection projections" as the nearest owned-data follow-on and replace it with what is actually nearest now — filtered `Count(pred)` in a projection, then array projections (blocked on an alias-driven array read-back in the DOM shaper), then bare-scalar projection pushdown. **Characterize the filtered-count follow-on correctly:** it is a pre-existing hard fail in every mode (measured, Task 4), so it is a bug fix of the same shape as EF-357 rather than a fallback→native widening. The design doc's framing of it as "deferred for cost, not impossibility, expressible as `$size` over `$filter`" was written before that measurement — the rendering claim still stands, the graceful-fallback assumption does not.
5. In §6, update **EF-357** to partially resolved (translation crash fixed; missing/null-array runtime failure remains) and add a row for the **new ticket** filed in Step 5 below covering the projection-path null normalization.

8. Add the whole-entity-vs-projection asymmetry to §4 or §5 as a standalone fact, not buried in the count entry: whole-entity materialization normalizes a missing or explicitly-null embedded array to an empty list, the projection path does not. It is a general property of the projection path, it is what limits EF-357's closure, and it is the first thing the array-projection follow-on will hit.
6. Update the §7 measurement note with the re-measured figures from Step 2, stating that both axes were checked.
7. **Fix the stale header while here:** it reads "branch tip `b087957`" and §7 cites `7532b15` for the `.Count` slice, but the actual tip at the start of this slice was `1b4c1d6`. Correct both to the real hashes.

- [ ] **Step 5: Update the JIRA ticket**

**EF-357** is only partially resolved. Comment on it naming the fix (`MongoProjectionBindingExpressionVisitor`, `Queryable.Count`/`LongCount` over a `CollectionShaperExpression`), the branch, and the commit; state that the translation-time `ArgumentException` is gone, that the shape is deliberately not native and why, and that a missing or explicitly-null embedded array still throws `ArgumentNullException` at materialization. Whether to close it or leave it open pending the residual is the ticket owner's call — do not close it silently.

**File TWO new bugs**, both measured by this slice and both pre-existing:

1. **The projection-path null.** The projection path's `CollectionShaperExpression` materializes `null` rather than an empty list for a missing or explicitly-null embedded array, while whole-entity materialization of the same document normalizes it to an empty list. Note in the ticket that fixing it changes observable results for collection projections that work today (`Select(b => b.Posts)` returns `null` for those rows now), so it needs its own verification pass over the array-projection shapes — that is why it was not folded into this slice. This is what limits EF-357's resolution to partial. Cite the Task 1 spike findings doc.

2. **The filtered-count projection hard fail.** `Select(b => new { N = b.Posts.Count(p => p.Rank > 0) })` throws `InvalidOperationException` in all three query modes — a translation-time crash, no correct results anywhere, no driver-LINQ oracle. Same shape of defect as EF-357. Cite `NativeOwnedCollectionCountTests.Filtered_count_projection_is_a_known_preexisting_hard_fail_in_every_mode` and note the exception type is not contract.

Also record, in whichever ticket fits or as a comment on the epic, the **pre-existing interposed-operator gap** the Task 3 review found: a `Distinct`/`Take`/`Reverse`/`DefaultIfEmpty`/`Concat` interposed between an owned-collection `Select` and a terminal operator hard-fails at translation in every mode via `_collectionShaperMapping.Add` throwing a duplicate key. Not caused or fixed by this slice.

If a JIRA number is filed for the native-projection half, add it to the design doc header and the §2 slice table.

- [ ] **Step 6: Commit the documentation**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/AGENTS.md docs/native-query-status-EF-322.md \
        docs/superpowers/specs/2026-07-28-native-owned-collection-count-projections-design.md
git commit -m "EF-322: document native count projections and the EF-357 closure"
```

- [ ] **Step 7: Whole-branch review**

Request a review of the complete branch diff (`git diff 1b4c1d6..HEAD`) via the `/review-ef-core-provider` skill. Every prior slice in this stack found at least one issue the per-task reviews missed; budget for a fix round.

**STOP for user review.**

---

## Self-Review

**Spec coverage.** Design §3 in-scope item (I) → Task 3. Item (II) → Task 2, breadth in Task 4. Design §4.1 binder + visitor → Task 2 Steps 3–4. §4.2 crash fix + disjointness → Task 3 Step 3, Task 4 disjointness test. §5 `$ifNull` mandatory → Task 2 Step 1 MQL test. §5 same-named-scalar hazard → inherited, no code, documented in Task 5 Step 3. §6.1 spike Q1–Q3 → Task 1. §6.2 differential oracle → Task 4 Step 1. §6.3 regression + documentation → Task 2 Step 6, Task 5 Steps 3–4. §6.4 sweeps → Task 5 Steps 1–2. §7 follow-ons → Task 5 Step 4 item 4, plus the `Filtered_count_projection_still_declines` pin.

**Type consistency.** `MongoSizeExpression` (namespace `…Query.Expressions`) is the node produced by `TryTranslateValue` in Task 2 Step 3 and consumed nowhere else. `EnumerableMethods.CountWithoutPredicate` / `.LongCountWithoutPredicate` verified present at `src/MongoDB.EntityFrameworkCore/EnumerableMethods.cs:105-106`. `NativeRoute.Projection` is the enum member used in both the binder-side comment and the visitor guard. `TitleCount` is defined in Task 4 Step 1 and used only there. Test helper names (`SeedLengths`, `SeedWellFormed`, `Seed`, `RowWithNotes`, `DifferentialRows`, `CreateContext`, `CreateContextWithLogging`) all verified against the existing test file.

**Known plan-level uncertainty, deliberately left as a branch rather than a guess:** Task 3 Step 1's expected values depend on Task 1's Q2/Q3 measurements. The instruction is to adjust the test to the measurement and report, not to force the measurement to fit the plan.
