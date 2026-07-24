# Native filtered-inner owned `SelectMany` Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make a filtered owned-collection `SelectMany` (`from o in q from i in o.Items.Where(pred) select …`, inner-element-only `pred`) go native — for the projected (both spellings) and bare-whole-element result shapes — by peeling the inner user filter off the owned nav and emitting it as a `$match` on the unwound element.

**Architecture:** A shared peel/build helper in `NativeSelectManyBinder` strips user-authored `Where` layers off the owned nav (owned collections nav-expand to a bare member access, so there is no FK-correlation to isolate — unlike the reference slice), translates each predicate against the owned target entity type, prefixes its field refs with the owned unwind path (e.g. `Items`), ANDs them, and stores the result on the existing `MongoUnwindSource.Filter`. Both owned binders (`TryBind`, `TryBindBareNavUnwind`) call it. The lowerer already emits the filter `$match` kind-agnostically after the owned `$unwind` and before `$replaceRoot`/`$project`, so it needs no logic change.

**Tech Stack:** C#, EF Core provider internals, xUnit (plain `Assert.*`, no FluentAssertions), MongoDB aggregation pipeline.

## Global Constraints

- `<Nullable>enable</Nullable>` on `src/` — annotate new types accordingly.
- All touched types are `internal`; this change is additive and not a breaking change (filtered owned `SelectMany` declines today, in every mode).
- Multi-EF: expect **no `#if`** (identical EF8/EF9/EF10). Task 1's spike explicitly checks the arriving tree on all three; full 3-version `/test-all` runs before squash.
- **No oracle (Task 1 spike, confirmed).** A filtered owned `SelectMany` has **no** working driver-LINQ fallback for any form — the driver throws `InvalidOperationException` the moment any `Where` sits inside the collection selector (it translates only an *unfiltered* embedded SelectMany). So every supported shape is proven via `MongoQueryMode.NativeOnly` succeeding + expected-in-memory result-set assertions (no `Native == DriverLinq` parity), and every declined filter (correlated-beyond-outer, computed operator) hard-fails in **every** mode — like the reference SelectMany / Intersect-Except families.
- Preserve file BOMs.
- Build a single EF version: `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"`. Run one class: `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build --filter "FullyQualifiedName~ClassName"`.
- Subagent-driven-development, **stop after every task** for review.

---

## File Structure

- `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeSelectManyBinder.cs` — **modify**: add private `PeelOwnedInnerWhere` + `TryBuildOwnedInnerFilter` helpers; wire `TryBind` and `TryBindBareNavUnwind` to peel/capture the inner filter.
- `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoSelectLowerer.cs` — **modify (comment only)**: correct the stale "reference only for now" comment on the `Filter` `$match` block to note owned is now populated too.
- `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md` — **modify**: as-built note.
- `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/NativeSelectManyBinderTests.cs` — **modify**: flip `Inner_where_before_select_returns_false`; add bare-nav filtered / stacked / correlated-beyond-outer / computed-decline tests.
- `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoSelectLowererTests.cs` — **modify**: owned-filter `$match`-placement test.
- `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeSelectManyTests.cs` — **modify**: end-to-end filtered projected (both forms) / bare-element / stacked / zero-rows / parametrized-outer / decline tests.

Reused unchanged (no edits): `MongoUnwindSource.Filter`, `MongoFieldPrefixRewriter`, `ReferencesParameter`, `UnwrapAsQueryable`, `TryGetMemberAccess`, `UnwrapLambdaFromQuote`.

---

## Task 1: Spike — confirm the arriving collection-selector tree

**Files:**
- Create: `.superpowers/sdd/EF-347-owned-filtered-inner-spike.md` (gitignored; NOT committed)

**Interfaces:**
- Produces (findings only, no production code): for `from o in q from i in o.Items.Where(i => i.Price > 0) select …`, the arriving collection-selector tree for (a) the inner-`Select` form (`o.Items.Where(pred).Select(i => …)`), (b) the explicit-result-selector / query-syntax form, (c) the bare whole-element form (`select i`); whether stacked `.Where(p1).Where(p2)` arrives nested (`Where(Where(nav, p1), p2)`) and whether the innermost source is a bare owned-nav member access / `EF.Property` (NOT an FK-correlated `EntityQueryRootExpression` subquery); any EF8/EF9/EF10 divergence; and whether the driver-LINQ oracle actually translates the **filtered projected** form.

- [ ] **Step 1: Add a temporary probe dump to both owned binders**

In `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeSelectManyBinder.cs`, temporarily add at the very top of BOTH `TryBind` and `TryBindBareNavUnwind` (REMOVE before Task 2):

```csharp
if (Environment.GetEnvironmentVariable("EF_SPIKE_DUMP") == "1")
    System.Console.Error.WriteLine("SELECTMANY-OWNED-SELECTOR: " + collectionSelector.Body);
```

- [ ] **Step 2: Add a temporary probe test that runs the filtered owned shapes**

Add a throwaway `[Fact]` to `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeSelectManyTests.cs` that, under `MongoQueryMode.DriverLinq`, runs each shape inside a `try`/`catch` (swallowing any exception) so the binder is entered and the dump fires:

```csharp
[Fact]
public void SPIKE_dump_filtered_owned_selectors()
{
    var seed = SeedOwners();
    using var db = CreateContext(seed, MongoQueryMode.DriverLinq, nameof(SPIKE_dump_filtered_owned_selectors));
    void Try(Action a) { try { a(); } catch { /* dumping only */ } }
    Try(() => db.Entities.SelectMany(o => o.Items.Where(i => i.Price > 0).Select(i => new { o.Name, i.Price })).ToList());
    Try(() => db.Entities.SelectMany(o => o.Items.Where(i => i.Price > 0), (o, i) => new { o.Name, i.Price }).ToList());
    Try(() => db.Entities.SelectMany(o => o.Items.Where(i => i.Price > 0)).Select(i => i).ToList());
    Try(() => db.Entities.SelectMany(o => o.Items.Where(i => i.Price > 0).Where(i => i.Name != "x"), (o, i) => new { o.Name, i.Price }).ToList());
}
```

- [ ] **Step 3: Run the probe on EF10, capture the trees**

Run:
```bash
EF_SPIKE_DUMP=1 dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --filter "FullyQualifiedName~NativeSelectManyTests.SPIKE_dump_filtered_owned_selectors" 2>&1 | grep SELECTMANY-OWNED-SELECTOR
```
Expected: one or more `SELECTMANY-OWNED-SELECTOR: …` lines per shape. Record verbatim. Confirm the inner-`Select` form shows `Select(Where(<nav>, pred), lambda)`, the explicit/bare forms show `Where(<nav>, pred)`, and `<nav>` unwraps to a bare `o.Items` member access or `EF.Property(o, "Items")` — no `EntityQueryRootExpression`.

- [ ] **Step 4: Repeat on EF8 and EF9**

Run the same command with `-c "Debug EF8"` and `-c "Debug EF9"`. Record any differences.

- [ ] **Step 5: Probe the projected-form oracle**

Determine whether the driver-LINQ fallback translates the filtered projected form: temporarily assert the inner-`Select` projected query succeeds under explicit `MongoQueryMode.DriverLinq` (returns rows), inside the probe test. Record the result — it decides whether Task 4's projected tests assert `Native == DriverLinq` parity or only `NativeOnly` + in-memory.

- [ ] **Step 6: Record findings and remove the probe**

Write `.superpowers/sdd/EF-347-owned-filtered-inner-spike.md` with the captured trees per EF version, the verdict (bare-nav member access, no FK correlation; nested stacked `Where`; per-form shape), and the oracle finding. Remove the temporary `EF_SPIKE_DUMP` dump lines from both binders AND delete the `SPIKE_dump_filtered_owned_selectors` test.

- [ ] **Step 7: STOP for review**

Report findings. Task 2 below is written to peel any number of nested `Where` layers regardless; the controller confirms the primary path matches the spike and that no EF-version divergence needs an `#if`.

---

## Task 2: Shared peel/build helpers + wire both owned binders

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeSelectManyBinder.cs`
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/NativeSelectManyBinderTests.cs`

**Interfaces:**
- Consumes: `MongoFieldPrefixRewriter.Rewrite(MongoExpression, string) → MongoExpression`; `MongoUnwindSource.Filter` (`MongoExpression?`, settable); `ReferencesParameter(Expression, ParameterExpression) → bool`; `UnwrapAsQueryable(Expression) → Expression`; `TryGetMemberAccess(Expression, out Expression, out string) → bool`; `MongoExpressionTranslator(IEntityType).TryTranslate(Expression, out MongoExpression?)`; `UnwrapLambdaFromQuote()` (extension); `MongoBinaryExpression(MongoBinaryOperator, MongoExpression, MongoExpression)` — all existing.
- Produces: private `PeelOwnedInnerWhere(Expression source, List<LambdaExpression> userPredicates) → Expression` and private `TryBuildOwnedInnerFilter(IReadOnlyList<LambdaExpression> userPredicates, IEntityType innerEntityType, string unwindPath, ParameterExpression outerParam, out MongoExpression? filter) → bool`. `TryBind` and `TryBindBareNavUnwind` now bind a filtered inner (setting `UnwindSource.Filter`) and still decline correlated-beyond-outer / translator-unsupported filters (return false, no mutation).

**PREREQUISITE:** Task 1 spike confirmed the arriving shape (bare-nav member access, nested stacked `Where`).

- [ ] **Step 1: Flip the existing decline test and add new binder tests**

In `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/NativeSelectManyBinderTests.cs`:

(a) **Replace** `Inner_where_before_select_returns_false` (lines ~175–184) with a binding test:

```csharp
    [Fact]
    public void Inner_where_before_select_binds_with_filter()
    {
        var mongoQ = TestQuery();
        var collectionSelector = Build((Owner o) =>
            o.Items.AsQueryable().Where(i => i.Price > 0).Select(i => new { o.Name, i.Price }));

        Assert.True(NativeSelectManyBinder.TryBind(mongoQ, collectionSelector));

        Assert.Equal("Items", mongoQ.Select.UnwindSource!.InnerScopePath);
        // Filter field ref is prefixed with the owned unwind path.
        var binary = Assert.IsType<MongoBinaryExpression>(mongoQ.Select.UnwindSource!.Filter);
        Assert.Equal("Items.Price", Assert.IsType<MongoFieldExpression>(binary.Left).ElementName);
        // The projection still binds.
        Assert.Equal(2, mongoQ.Select.Projection.Count);
    }
```

(b) **Add** an unfiltered-`Filter`-is-null assertion to the existing `Nested_select_binds_unwind_and_two_scope_projection` (after line ~86, the `InnerScopePath` assertion):

```csharp
        Assert.Null(mongoQ.Select.UnwindSource!.Filter);
```

(c) **Add** these tests after the `Inner_where_before_select_binds_with_filter` test:

```csharp
    [Fact]
    public void Inner_stacked_where_ands_together_binds_with_filter()
    {
        var mongoQ = TestQuery();
        var collectionSelector = Build((Owner o) =>
            o.Items.AsQueryable().Where(i => i.Price > 0).Where(i => i.Name != "x").Select(i => new { o.Name, i.Price }));

        Assert.True(NativeSelectManyBinder.TryBind(mongoQ, collectionSelector));

        var filter = Assert.IsType<MongoBinaryExpression>(mongoQ.Select.UnwindSource!.Filter);
        Assert.Equal(MongoBinaryOperator.AndAlso, filter.Operator);
    }

    [Fact]
    public void Inner_where_correlated_beyond_outer_returns_false()
    {
        // A user filter referencing the OUTER parameter (o.Name) is correlated-beyond-outer — the
        // ReferencesParameter guard rejects it BEFORE translation, so the whole bind declines with no mutation.
        var mongoQ = TestQuery();
        var collectionSelector = Build((Owner o) =>
            o.Items.AsQueryable().Where(i => i.Name != o.Name).Select(i => new { o.Name, i.Price }));

        Assert.False(NativeSelectManyBinder.TryBind(mongoQ, collectionSelector));
        Assert.Null(mongoQ.Select.UnwindSource);
        Assert.Empty(mongoQ.Select.Projection);
    }

    [Fact]
    public void Bare_nav_with_inner_where_binds_with_filter()
    {
        var mongoQ = TestQuery();
        var collectionSelector = Build((Owner o) => o.Items.AsQueryable().Where(i => i.Price > 0));

        Assert.True(NativeSelectManyBinder.TryBindBareNavUnwind(mongoQ, collectionSelector));

        Assert.Equal("Items", mongoQ.Select.UnwindSource!.InnerScopePath);
        var binary = Assert.IsType<MongoBinaryExpression>(mongoQ.Select.UnwindSource!.Filter);
        Assert.Equal("Items.Price", Assert.IsType<MongoFieldExpression>(binary.Left).ElementName);
    }

    [Fact]
    public void Bare_nav_without_where_binds_with_null_filter()
    {
        var mongoQ = TestQuery();
        Expression<Func<Owner, IQueryable<Item>>> collectionSelector = o => o.Items.AsQueryable();

        Assert.True(NativeSelectManyBinder.TryBindBareNavUnwind(mongoQ, collectionSelector));
        Assert.Equal("Items", mongoQ.Select.UnwindSource!.InnerScopePath);
        Assert.Null(mongoQ.Select.UnwindSource!.Filter);
    }

    [Fact]
    public void Bare_nav_with_correlated_beyond_outer_where_returns_false()
    {
        var mongoQ = TestQuery();
        var collectionSelector = Build((Owner o) => o.Items.AsQueryable().Where(i => i.Name != o.Name));

        Assert.False(NativeSelectManyBinder.TryBindBareNavUnwind(mongoQ, collectionSelector));
        Assert.Null(mongoQ.Select.UnwindSource);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --filter "FullyQualifiedName~NativeSelectManyBinderTests"`
Expected: FAIL — the flipped/added tests fail (filter not captured; `TryBind`/`TryBindBareNavUnwind` currently return false for a `Where` layer).

- [ ] **Step 3: Add the two shared helpers**

In `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeSelectManyBinder.cs`, add these two private static methods (place them next to `UnwrapAsQueryable`, near the bottom of the class):

```csharp
    /// <summary>
    /// Peels user-authored <c>Where(...)</c> layers off an owned collection selector's source down to the bare
    /// owned-nav member access, collecting each layer's predicate lambda into <paramref name="userPredicates"/>.
    /// Owned collections nav-expand to a bare member access (<c>o.Items</c>), NOT an FK-correlated subquery, so
    /// EVERY <c>Where</c> here is an inner-element user filter — there is no FK-correlation <c>Where</c> to stop
    /// at (unlike <see cref="TryBindReferenceNavUnwind"/>). Returns the source with all <c>Where</c> layers
    /// removed (the bare owned nav for an accepted shape); the caller validates it via <see cref="TryGetMemberAccess"/>.
    /// </summary>
    private static Expression PeelOwnedInnerWhere(Expression source, List<LambdaExpression> userPredicates)
    {
        var current = UnwrapAsQueryable(source);
        while (current is MethodCallExpression
               {
                   Method: { Name: nameof(System.Linq.Queryable.Where), DeclaringType: var decl },
                   Arguments: [var whereSource, var predArg]
               }
               && decl == typeof(System.Linq.Queryable))
        {
            userPredicates.Add(predArg.UnwrapLambdaFromQuote());
            current = UnwrapAsQueryable(whereSource);
        }
        return current;
    }

    /// <summary>
    /// Translates each peeled owned inner-element predicate against <paramref name="innerEntityType"/>, prefixes
    /// its field refs with <paramref name="unwindPath"/> (e.g. <c>Items</c>, so <c>Price</c> becomes
    /// <c>Items.Price</c> — the unwound owned element sits at that path before <c>$replaceRoot</c>/<c>$project</c>),
    /// and ANDs them into one <paramref name="filter"/>. Returns <see langword="true"/> with
    /// <paramref name="filter"/> <see langword="null"/> when there are no predicates (the unfiltered case), so
    /// callers can invoke it unconditionally. Declines (<see langword="false"/>, no mutation) if any predicate
    /// references the outer parameter (correlated-beyond-outer — <see cref="ReferencesParameter"/>, load-bearing
    /// for the same by-name-mis-scope reason documented on that guard) or the translator rejects it
    /// (computed / unsupported operator).
    /// </summary>
    private static bool TryBuildOwnedInnerFilter(
        IReadOnlyList<LambdaExpression> userPredicates, IEntityType innerEntityType, string unwindPath,
        ParameterExpression outerParam, out MongoExpression? filter)
    {
        filter = null;
        if (userPredicates.Count == 0)
            return true;

        var innerTranslator = new MongoExpressionTranslator(innerEntityType);
        foreach (var userPredicate in userPredicates)
        {
            if (userPredicate.Parameters.Count != 1
                || ReferencesParameter(userPredicate.Body, outerParam)
                || !innerTranslator.TryTranslate(userPredicate.Body, out var expr))
                return false;
            var prefixed = MongoFieldPrefixRewriter.Rewrite(expr!, unwindPath);
            filter = filter == null
                ? prefixed
                : new MongoBinaryExpression(MongoBinaryOperator.AndAlso, filter, prefixed);
        }
        return true;
    }
```

- [ ] **Step 4: Wire `TryBind` (inner-`Select` form)**

In `TryBind` (lines ~52–113), replace the source-resolution + final mutation. Change the `navExpr` line (currently `var navExpr = UnwrapAsQueryable(selectSource);`) to peel first:

```csharp
        // Peel any user Where(...) layers off the owned nav (o.Items.Where(pred).Select(...)); owned collections
        // are a bare member access, so every Where is an inner-element user filter (no FK correlation).
        var userPredicates = new List<LambdaExpression>();
        var navExpr = PeelOwnedInnerWhere(selectSource, userPredicates);
        if (!TryGetMemberAccess(navExpr, out var navRoot, out var navName) || !ReferenceEquals(navRoot, outerParam))
            return false;
```

(This replaces the existing `var navExpr = UnwrapAsQueryable(selectSource);` + its `TryGetMemberAccess` guard on lines ~68–70.)

Then, after the projection-building loop (after line ~107, `projections.Add(...)`), REPLACE the final three lines (the `mongoQ.Select.UnwindSource = MongoUnwindSource.Owned(...)` + `foreach` + `return true` at lines ~109–112) with:

```csharp
        if (!TryBuildOwnedInnerFilter(userPredicates, navigation.TargetEntityType, unwindPath, outerParam, out var filter))
            return false;

        var unwind = MongoUnwindSource.Owned(unwindPath, navigation.TargetEntityType);
        unwind.Filter = filter;
        mongoQ.Select.UnwindSource = unwind;
        foreach (var p in projections)
            mongoQ.Select.AddProjection(p);
        return true;
```

> Building the filter here — after the projection list is validated but before any mutation — keeps the "no partial mutation on decline" invariant: `projections` is a local list, and `AddProjection`/`UnwindSource` are only assigned in the final block.

- [ ] **Step 5: Wire `TryBindBareNavUnwind` (explicit / query-syntax / bare-element form)**

In `TryBindBareNavUnwind` (lines ~126–143), change the `navExpr` line to peel first and set the filter at the end. Replace the body from `var navExpr = ...` through the final `return true;` with:

```csharp
        var userPredicates = new List<LambdaExpression>();
        var navExpr = PeelOwnedInnerWhere(collectionSelector.Body, userPredicates);
        if (!TryGetMemberAccess(navExpr, out var navRoot, out var navName) || !ReferenceEquals(navRoot, outerParam))
            return false;

        var outerEntityType = mongoQ.CollectionExpression.EntityType;
        var navigation = outerEntityType.FindNavigation(navName);
        if (navigation is not { IsCollection: true } || !navigation.TargetEntityType.IsOwned())
            return false;
        if (navigation.TargetEntityType.GetContainingElementName() is not { } unwindPath)
            return false;

        if (!TryBuildOwnedInnerFilter(userPredicates, navigation.TargetEntityType, unwindPath, outerParam, out var filter))
            return false;

        var unwind = MongoUnwindSource.Owned(unwindPath, navigation.TargetEntityType);
        unwind.Filter = filter;
        mongoQ.Select.UnwindSource = unwind;
        return true;
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --filter "FullyQualifiedName~NativeSelectManyBinderTests"`
Expected: PASS (all binder tests, including the unchanged unfiltered ones with `Filter == null`).

- [ ] **Step 7: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeSelectManyBinder.cs \
  tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/NativeSelectManyBinderTests.cs
git commit -m "EF-347: owned SelectMany binders capture inner-element filter"
```

- [ ] **Step 8: STOP for review**

---

## Task 3: Lowerer — verify + document the kind-agnostic owned filter `$match`

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoSelectLowerer.cs` (comment only)
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoSelectLowererTests.cs`

**Interfaces:**
- Consumes: `MongoUnwindSource.Owned(string, IEntityType)`, `MongoUnwindSource.Filter`, `MongoUnwindSource.WholeElement` (all existing); `MongoSelectLowerer.Lower(MongoQueryExpression) → IReadOnlyList<MongoPipelineStage>`.
- Produces: no logic change — a unit test proving the lowerer already emits an owned `Filter` `$match` after the owned `$unwind` and before `$replaceRoot`/`$project`, plus a corrected comment.

- [ ] **Step 1: Write the owned-filter placement test**

In `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoSelectLowererTests.cs`, add (mirror the existing owned-unwind tests at lines ~309 and ~332 for the `MongoUnwindSource.Owned("Items", innerEntityType: null!)` construction — a projected owned unwind adds a `Projection` so the `$project` tail is emitted):

```csharp
    [Fact]
    public void Owned_UnwindSource_with_filter_lowers_match_after_unwind_before_project()
    {
        var query = TestSelect();
        var unwind = MongoUnwindSource.Owned("Items", innerEntityType: null!);
        unwind.Filter = new MongoConstantExpression(true, null);
        query.Select.UnwindSource = unwind;
        query.Select.AddProjection(new MongoProjection("X", new MongoConstantExpression(1, null)));

        var stages = new MongoSelectLowerer().Lower(query).ToList();

        var unwindIndex = stages.FindIndex(s => s is MongoUnwindFieldStage);
        var matchIndex = stages.FindIndex(s => s is MongoMatchStage);
        var projectIndex = stages.FindIndex(s => s is MongoProjectStage);
        Assert.True(unwindIndex >= 0 && matchIndex > unwindIndex, "filter $match must follow the owned $unwind");
        Assert.True(projectIndex > matchIndex, "filter $match must precede the $project");
    }

    [Fact]
    public void Owned_WholeElement_UnwindSource_with_filter_lowers_match_after_unwind_before_replaceRoot()
    {
        var query = TestSelect();
        var unwind = MongoUnwindSource.Owned("Items", innerEntityType: null!);
        unwind.WholeElement = true;
        unwind.Filter = new MongoConstantExpression(true, null);
        query.Select.UnwindSource = unwind;

        var stages = new MongoSelectLowerer().Lower(query).ToList();

        var unwindIndex = stages.FindIndex(s => s is MongoUnwindFieldStage);
        var matchIndex = stages.FindIndex(s => s is MongoMatchStage);
        var replaceRootIndex = stages.FindIndex(s => s is MongoReplaceRootStage);
        Assert.True(unwindIndex >= 0 && matchIndex > unwindIndex, "filter $match must follow the owned $unwind");
        Assert.True(replaceRootIndex > matchIndex, "filter $match must precede the $replaceRoot");
    }
```

If `ToList()` / `FindIndex` need imports, `System.Linq` and `System.Collections.Generic` are already used across this test file; add `using` only if the build complains.

- [ ] **Step 2: Run the tests to verify they pass immediately**

Run: `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --filter "FullyQualifiedName~MongoSelectLowererTests"`
Expected: **PASS** — the lowerer's `if (unwind.Filter is { } filter)` block is already kind-agnostic, so these pass with no production change. (This is the point of the task: prove the reuse claim. If they FAIL, the design assumption is wrong — STOP and report.)

- [ ] **Step 3: Correct the stale lowerer comment**

In `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoSelectLowerer.cs`, update the comment on the `Filter` `$match` block (lines ~129–132). Replace the trailing sentence "EF-347 filtered-inner slice — reference only for now." with:

```csharp
            // Already scope-prefixed by the binder (reference: "_lookup_Refs.Total"; owned: "Items.Total").
            // EF-347 filtered-inner slice — populated for BOTH reference (TryBindReferenceNavUnwind) and owned
            // (TryBind / TryBindBareNavUnwind); the emission here is kind-agnostic.
```

- [ ] **Step 4: Run the lowerer tests again to confirm still green**

Run: `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --filter "FullyQualifiedName~MongoSelectLowererTests"`
Expected: PASS (comment change is inert).

- [ ] **Step 5: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoSelectLowerer.cs \
  tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoSelectLowererTests.cs
git commit -m "EF-347: prove + document owned filtered-inner \$match lowering"
```

- [ ] **Step 6: STOP for review**

---

## Task 4: End-to-end functional tests + `AGENTS.md`

**Files:**
- Modify: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeSelectManyTests.cs`
- Modify: `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md`

**Interfaces:**
- Consumes: the full mechanism (Tasks 2–3) end-to-end; the existing `Owner`/`Item` owned fixture, `SeedOwners()`, `CreateContext(Owner[], MongoQueryMode, string)`, `CreateContextWithLogging(...)`, `db.Entities`.
- Produces: the observable behavior — filtered projected (both forms) + bare-element owned SelectMany go native; correlated-beyond-outer / computed filters decline and hard-fail in every mode (no driver-LINQ oracle).

**Fixture facts (from the file):** `Owner { ObjectId Id; string Name; List<Item> Items }`, `Item { string Name; decimal Price }`. `SeedOwners()` yields Alice (Widget 9.99, Gadget 19.99), Bob (empty), Carol (Thing 5). `Item.Name` deliberately shadows `Owner.Name`.

- [ ] **Step 1: Add the filtered projected tests (both forms)**

In `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeSelectManyTests.cs`, add (place after the existing owned projected tests, before the reference-context region at line ~530):

```csharp
    [Fact]
    public void Inner_select_form_filtered_goes_native()
    {
        var seed = SeedOwners();
        using var db = CreateContext(seed, MongoQueryMode.NativeOnly, nameof(Inner_select_form_filtered_goes_native));

        var result = db.Entities
            .SelectMany(o => o.Items.Where(i => i.Price > 6m).Select(i => new { o.Name, i.Price }))
            .AsEnumerable().OrderBy(x => x.Name).ThenBy(x => x.Price).ToList();

        var expected = seed
            .SelectMany(o => o.Items.Where(i => i.Price > 6m).Select(i => new { o.Name, i.Price }))
            .OrderBy(x => x.Name).ThenBy(x => x.Price).ToList();

        Assert.Equal(expected, result);
        Assert.DoesNotContain(result, x => x.Price <= 6m); // "Thing" (5) excluded
    }

    [Fact]
    public void Explicit_result_selector_form_filtered_goes_native()
    {
        var seed = SeedOwners();
        using var db = CreateContext(seed, MongoQueryMode.NativeOnly, nameof(Explicit_result_selector_form_filtered_goes_native));

        var result = db.Entities
            .SelectMany(o => o.Items.Where(i => i.Price > 6m), (o, i) => new { o.Name, i.Price })
            .AsEnumerable().OrderBy(x => x.Name).ThenBy(x => x.Price).ToList();

        var expected = seed
            .SelectMany(o => o.Items.Where(i => i.Price > 6m), (o, i) => new { o.Name, i.Price })
            .OrderBy(x => x.Name).ThenBy(x => x.Price).ToList();

        Assert.Equal(expected, result);
    }

    [Fact]
    public void Filtered_owned_selectmany_emits_match_after_unwind_before_project()
    {
        var seed = SeedOwners();
        using var db = CreateContextWithLogging(seed, MongoQueryMode.NativeOnly,
            nameof(Filtered_owned_selectmany_emits_match_after_unwind_before_project), out var spyLogger);

        _ = db.Entities
            .SelectMany(o => o.Items.Where(i => i.Price > 6m), (o, i) => new { o.Name, i.Price })
            .ToList();

        var message = spyLogger.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery);
        Assert.Contains("$unwind", message);
        Assert.Contains("$match", message);
        Assert.Contains("Items.Price", message); // filter is scope-prefixed with the owned unwind path
    }
```

> No `Native == DriverLinq` parity variant — the Task 1 spike confirmed the driver-LINQ fallback throws on any filtered owned SelectMany, so there is no oracle. The `NativeOnly` tests above are the proof a shape goes native.

- [ ] **Step 2: Add the bare whole-element filtered test**

```csharp
    [Fact]
    public void Bare_owned_whole_inner_element_filtered_goes_native()
    {
        var seed = SeedOwners();
        using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(Bare_owned_whole_inner_element_filtered_goes_native));

        var result = db.Entities.AsNoTracking()
            .SelectMany(o => o.Items.Where(i => i.Price > 6m))
            .AsEnumerable().Select(i => i.Name).OrderBy(n => n).ToList();

        var expected = seed.SelectMany(o => o.Items.Where(i => i.Price > 6m))
            .Select(i => i.Name).OrderBy(n => n).ToList();

        Assert.Equal(expected, result);
        Assert.DoesNotContain("Thing", result); // Price 5 excluded
    }
```

> `AsNoTracking()` is required for the bare whole-element owned shape (EF Core's own owned-without-owner tracking guard, per the bare-owned slice) — carry it exactly as the existing `Bare_owned_whole_inner_element_*` tests do; if those tests use a different mechanism to satisfy tracking, match theirs.

- [ ] **Step 3: Add stacked / zero-rows / parametrized-outer tests**

```csharp
    [Fact]
    public void Filtered_owned_stacked_where_ands_together_goes_native()
    {
        var seed = SeedOwners();
        using var db = CreateContext(seed, MongoQueryMode.NativeOnly, nameof(Filtered_owned_stacked_where_ands_together_goes_native));

        var result = db.Entities
            .SelectMany(o => o.Items.Where(i => i.Price > 6m).Where(i => i.Name != "Gadget"), (o, i) => new { o.Name, i.Price })
            .AsEnumerable().OrderBy(x => x.Name).ThenBy(x => x.Price).ToList();

        var expected = seed
            .SelectMany(o => o.Items.Where(i => i.Price > 6m && i.Name != "Gadget"), (o, i) => new { o.Name, i.Price })
            .OrderBy(x => x.Name).ThenBy(x => x.Price).ToList();

        Assert.Equal(expected, result);
    }

    [Fact]
    public void Filtered_owned_excluding_all_children_contributes_no_rows()
    {
        var seed = SeedOwners();
        using var db = CreateContext(seed, MongoQueryMode.NativeOnly, nameof(Filtered_owned_excluding_all_children_contributes_no_rows));

        var result = db.Entities
            .SelectMany(o => o.Items.Where(i => i.Name == "nonexistent"), (o, i) => new { o.Name, i.Price })
            .ToList();

        Assert.Empty(result);
    }

    [Fact]
    public void Filtered_owned_composes_with_parametrized_outer_predicate()
    {
        var seed = SeedOwners();
        using var db = CreateContext(seed, MongoQueryMode.NativeOnly, nameof(Filtered_owned_composes_with_parametrized_outer_predicate));

        var ownerName = "Alice";
        var result = db.Entities
            .Where(o => o.Name == ownerName)
            .SelectMany(o => o.Items.Where(i => i.Price > 6m), (o, i) => new { o.Name, i.Price })
            .AsEnumerable().OrderBy(x => x.Price).ToList();

        var expected = seed.Where(o => o.Name == ownerName)
            .SelectMany(o => o.Items.Where(i => i.Price > 6m), (o, i) => new { o.Name, i.Price })
            .OrderBy(x => x.Price).ToList();

        Assert.Equal(expected, result);
    }
```

- [ ] **Step 4: Add the decline tests**

Task 1 confirmed a filtered owned SelectMany has **no driver-LINQ oracle** (the driver throws on any inner `Where`), so a declined filter hard-fails in **every** mode — `TranslateSelectMany` returns `null` and there is no working fallback to land on.

```csharp
    [Fact]
    public void Filtered_owned_correlated_beyond_outer_hard_fails_in_every_mode()
    {
        // A filter referencing the OUTER entity (i.Name != o.Name) is correlated-beyond-outer — declined by the
        // ReferencesParameter guard. TryBind/TryBindBareNavUnwind return false, TranslateSelectMany returns null,
        // and (no driver-LINQ oracle for a filtered owned SelectMany) it hard-fails in every mode.
        var seed = SeedOwners();
        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq, MongoQueryMode.NativeOnly })
        {
            using var db = CreateContext(seed, mode,
                nameof(Filtered_owned_correlated_beyond_outer_hard_fails_in_every_mode) + mode);
            Assert.ThrowsAny<Exception>(() =>
                db.Entities.SelectMany(o => o.Items.Where(i => i.Name != o.Name), (o, i) => new { o.Name, i.Price }).ToList());
        }
    }

    [Fact]
    public void Filtered_owned_computed_operator_hard_fails_in_every_mode()
    {
        // A filter using an operator the native translator does not support (string.ToUpper) declines the
        // ordinary way (inner translator returns false). Same no-oracle hard-fail in every mode.
        var seed = SeedOwners();
        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq, MongoQueryMode.NativeOnly })
        {
            using var db = CreateContext(seed, mode,
                nameof(Filtered_owned_computed_operator_hard_fails_in_every_mode) + mode);
            Assert.ThrowsAny<Exception>(() =>
                db.Entities.SelectMany(o => o.Items.Where(i => i.Name.ToUpper() == "WIDGET"), (o, i) => new { o.Name, i.Price }).ToList());
        }
    }
```

> If the implementer finds `string.ToUpper()` is unexpectedly translatable natively (unlikely — it is in the computed long tail), swap it for another genuinely-unsupported inner operator confirmed unsupported by `MongoExpressionTranslator`. Verify by running.

- [ ] **Step 5: Run the functional tests**

Run:
```bash
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --filter "FullyQualifiedName~NativeSelectManyTests"
```
Expected: PASS (all new + existing `NativeSelectManyTests`).

- [ ] **Step 6: Update `AGENTS.md`**

In `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md`, add a concise as-built note under the owned-`SelectMany` section (near the bare-owned-element and reference filtered-inner notes), recording: an inner-element-only filtered **owned** `SelectMany` now goes native for the projected (both forms) and bare-whole-element result shapes, via `TryBind`/`TryBindBareNavUnwind` peeling user `Where` layers off the bare owned nav (no FK correlation, no `NativeCorrelationMatcher`/`TrySplitCorrelation` — unlike the reference slice) + `TryBuildOwnedInnerFilter` translating against the owned target type and prefixing with the owned unwind path (`Items.<field>`) via the reused `MongoFieldPrefixRewriter` + storing on the reused `MongoUnwindSource.Filter`; the lowerer's kind-agnostic `Filter` `$match` needed no change. Note there is **no driver-LINQ oracle** for a filtered owned SelectMany (the driver throws on any inner `Where`, confirmed by the Task 1 spike), so correlated-beyond-outer (`ReferencesParameter` guard) and computed-operator declines hard-fail in **every** mode for every form — like the reference SelectMany family; multi-version (no `#if`); still deferred: computed projection leaf, nested owned `SelectMany`.

- [ ] **Step 7: Commit**

```bash
git add tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeSelectManyTests.cs \
  src/MongoDB.EntityFrameworkCore/Query/AGENTS.md
git commit -m "EF-347: filtered-inner owned SelectMany functional tests + AGENTS.md"
```

- [ ] **Step 8: STOP for review**

---

## Task 5: Full 3-version verification + finalize

**Files:** none (verification + handoff).

- [ ] **Step 1: Full 3-version `/test-all`**

Invoke the `/test-all` skill (controller runs it in the foreground, per the recorded process lesson). Expected: GREEN, 0 failures, all three assemblies each for EF8/EF9/EF10. Baseline is `6b9b973`: EF8 7282/67, EF9 7643/68, EF10 7240/71 — expect a small positive delta from the new tests, zero regressions.

- [ ] **Step 2: NativeOnly spec sweep**

Run the spec suite with `MONGODB_EF_NATIVE_ONLY=1` and diff the pass set against `6b9b973`. Expected: no regressions; note any Northwind filtered-owned-SelectMany shape that flips to native (likely none — this shape is not in the Northwind suite; the gain is proven by the functional tests).

- [ ] **Step 3: Whole-branch review**

Request an opus whole-branch review (`6b9b973..HEAD`), with the recurring silent-wrong-data / operator-after-terminal hunt (and specifically: does an owned filter ever mis-scope a shared inner/outer member name; does a declined filter ever partially mutate the query). Fold any blocking findings, re-verify green.

- [ ] **Step 4: Squash + handoff**

Back up the pre-squash tip (`git branch -f EF-347-selectmany-owned-filtered-inner-presquash HEAD`), squash the slice to one commit above `6b9b973` (`git reset --soft 6b9b973 && git commit`), verify the tree is byte-identical to the pre-squash tip (`git diff --quiet EF-347-selectmany-owned-filtered-inner-presquash HEAD`), re-run the 3-version `/test-all` on the squashed tip, then give the user the plain fast-forward push command (`git push origin <newtip>:NativeQueryOngoing`).

- [ ] **Step 5: STOP — report the pushable tip to the user.**

---

## Self-Review

**Spec coverage:**
- Owned nav, inner-element-only filter, projected inner-`Select` form → Task 2 (`TryBind` wiring) + Task 4 (`Inner_select_form_filtered_goes_native`). ✓
- Projected explicit-result-selector / query-syntax form → Task 2 (`TryBindBareNavUnwind` wiring) + Task 4 (`Explicit_result_selector_form_filtered_goes_native`). ✓
- Bare whole-element form → Task 2 (`TryBindBareNavUnwind` sets Filter; WholeElement gate unchanged) + Task 4 (`Bare_owned_whole_inner_element_filtered_goes_native`). ✓
- Stacked filters (AND) → Task 2 unit (`Inner_stacked_where_ands_together_binds_with_filter`) + Task 4 functional. ✓
- Correlated-beyond-outer declines → Task 2 unit (both binders) + Task 4 functional. ✓
- Computed/unsupported filter declines → Task 4 functional. ✓
- Filter insertion point (`$match` after owned `$unwind`, before `$replaceRoot`/`$project`), prefixed `Items.<field>` → Task 3 unit (both projected + WholeElement) + Task 4 MQL assertion. ✓
- Lowerer needs no logic change (reuse claim) → Task 3 (tests pass with comment-only change). ✓
- No driver-LINQ oracle (all declines hard-fail every mode) → Task 1 spike confirmed + Task 4 decline tests (`ThrowsAny` every mode). ✓
- Multi-version / no `#if` → Task 1 spike checks; Task 5 `/test-all`. ✓
- No-mutation-on-decline → filter built before mutation in both binders (Task 2 Steps 4–5) + Task 2 unit asserts `UnwindSource == null` on decline. ✓

**Placeholder scan:** The only discovered-at-runtime spot is Task 1's spike findings (inherently not pre-writable); the Task 1 oracle question is now RESOLVED (no oracle — confirmed) and Task 4's tests are written to the confirmed no-oracle behavior. No "TBD"/"handle edge cases"/"similar to Task N".

**Type consistency:** `PeelOwnedInnerWhere(Expression, List<LambdaExpression>) → Expression` and `TryBuildOwnedInnerFilter(IReadOnlyList<LambdaExpression>, IEntityType, string, ParameterExpression, out MongoExpression?) → bool` — used consistently in Tasks 2 (both binders). `MongoUnwindSource.Owned(string, IEntityType)` + `.Filter` (`MongoExpression?`), `MongoFieldPrefixRewriter.Rewrite(MongoExpression, string)`, `MongoBinaryExpression(MongoBinaryOperator, MongoExpression, MongoExpression)`, `ReferencesParameter(Expression, ParameterExpression)`, `MongoConstantExpression(object, forSerialization)` — match the actual source signatures read during planning.
