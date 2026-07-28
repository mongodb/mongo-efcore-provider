# Native owned correlated-beyond-outer inner-filter `SelectMany` Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make an embedded owned-collection `SelectMany` whose inner `.Where(pred)` references the outer owner (`o.Items.Where(i => i.Name == o.Name)`) go native, emitting the correlated conjunct as a `$expr` field-to-field comparison in the post-`$unwind` `$match`, across all owned result shapes.

**Architecture:** A near-exact mirror of the just-shipped reference correlated-beyond-FK slice, applied to the OWNED path. Reuse the existing optional two-scope mode of `MongoExpressionTranslator` (member rooted on the outer param by `ReferenceEquals` → outer entity type at document root; else inner entity type with the unwind-path prefix). Flip `NativeSelectManyBinder.TryBuildOwnedInnerFilter`'s `ReferencesParameter` decline into a router (correlated layer → two-scope translate; inner-only layer → existing translate + `MongoFieldPrefixRewriter`). Lowerer/renderer unchanged.

**Tech Stack:** C# / .NET (net8.0 for EF8/EF9, net10.0 for EF10), EF Core provider internals, MongoDB C# driver, xUnit (plain `Assert.*`, no FluentAssertions).

**Design doc:** `docs/superpowers/specs/2026-07-24-native-selectmany-owned-correlated-design.md`
**Spike:** `.superpowers/sdd/EF-347-owned-correlated-spike.md`

## Global Constraints

- **Branch:** `EF-347-selectmany-owned-correlated` (already cut off tip `933eb94`; design committed `2531cb4`). Do NOT switch branches.
- **No `#if` EF-version guards** — the spike confirmed identical EF8/EF9/EF10 behavior.
- `<Nullable>enable</Nullable>` on `src/`. Preserve file BOMs. All touched production types stay `internal`.
- Unit tests use **plain xUnit `Assert.*`** (no FluentAssertions). Tests run serially; run functional tests with **both `MONGODB_URI` and `ATLAS_URI` unset** (isolated atlas-local container).
- **No-partial-mutation-on-decline:** a binder returning `false` must leave `mongoQueryExpression` untouched (no `UnwindSource`, no projections).
- **Uniform hard-fail, no oracle (spike-confirmed):** a correlated-beyond-outer owned `SelectMany` has NO driver-LINQ oracle for ANY projection shape — every decline hard-fails in every mode; every native success is proven via `MongoQueryMode.NativeOnly` + an expected in-memory result set, NEVER `Native == DriverLinq` parity. Do NOT write graceful-fallback (`Native`/`DriverLinq` succeed) assertions for any correlated shape.
- **No new mismatched-CLR-type guard** (EF-221 known interaction): the correlated field-to-field comparison inherits value-equality semantics.
- Commit after each task. Controller runs the full 3-version `/test-all` foreground and drives squash + push at the end (not part of these tasks).

---

### Task 1: Route correlated layers in `TryBuildOwnedInnerFilter`

Flip the `ReferencesParameter` decline in the owned filter builder into a router: a peeled inner-`Where` layer referencing the outer owner is translated with the two-scope `MongoExpressionTranslator` (inner fields prefixed with the unwind path, outer fields at document root) and ANDed onto `MongoUnwindSource.Filter`; inner-only layers keep the existing path. Thread the owner entity type through from both owned binders.

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeSelectManyBinder.cs` — `TryBuildOwnedInnerFilter` (~lines 533-554), and its two call sites (`TryBind` ~line 113, `TryBindBareNavUnwind` ~line 151).
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/NativeSelectManyBinderTests.cs`

**Interfaces:**
- Consumes: the two-scope constructor `MongoExpressionTranslator(IEntityType innerEntityType, ParameterExpression outerParam, IEntityType outerEntityType, string innerPrefix)` (already exists from the reference slice).
- Produces: `TryBuildOwnedInnerFilter` gains a trailing `IEntityType outerEntityType` parameter (inserted before `out MongoExpression? filter`).

- [ ] **Step 1: Convert the two existing owned decline unit tests + add coverage (write them failing)**

In `NativeSelectManyBinderTests.cs`: **convert** `Inner_where_correlated_beyond_outer_returns_false` (~line 207) and `Bare_nav_with_correlated_beyond_outer_where_returns_false` (~line 245) into binds-with-`$expr`-filter tests, and **add** a shadow, a mixed-conjunct, and an unsupported-operator-declines test. Reuse the existing `Build(...)` lambda helper, `TestQuery()`, and the `Owner`/`Item` entities (Owner: `Id`/`Name`/`Items`; Item: `Name` (shadows Owner.Name) / `Price`). Match the exact assertion style of the neighboring `TryBind`/`TryBindBareNavUnwind` tests.

Replace `Inner_where_correlated_beyond_outer_returns_false` with:

```csharp
    [Fact]
    public void Inner_where_correlated_beyond_outer_binds_with_expr_filter()
    {
        // A user filter referencing the OUTER owner (i.Name == o.Name) now goes native: the correlated
        // conjunct is two-scope-translated (inner field prefixed with the unwind path, outer field at document
        // root) and stored on Filter as a field-to-field comparison the renderer emits as $expr. Item.Name
        // shadows Owner.Name, so routing MUST be by parameter identity, not name.
        var mongoQ = TestQuery();
        var collectionSelector = Build((Owner o) =>
            o.Items.AsQueryable().Where(i => i.Name == o.Name).Select(i => new { o.Name, i.Price }));

        Assert.True(NativeSelectManyBinder.TryBind(mongoQ, collectionSelector));

        var bin = Assert.IsType<MongoBinaryExpression>(mongoQ.Select.UnwindSource!.Filter);
        Assert.Equal(MongoBinaryOperator.Equal, bin.Operator);
        Assert.Equal("Items.Name", Assert.IsType<MongoFieldExpression>(bin.Left).ElementName);   // inner, prefixed
        Assert.Equal("Name", Assert.IsType<MongoFieldExpression>(bin.Right).ElementName);          // outer, root
    }

    [Fact]
    public void Inner_where_mixed_inner_and_correlated_conjunct_binds()
    {
        // One .Where whose body ANDs an inner-only conjunct with a correlated one.
        var mongoQ = TestQuery();
        var collectionSelector = Build((Owner o) =>
            o.Items.AsQueryable().Where(i => i.Name != "x" && i.Name == o.Name).Select(i => new { o.Name, i.Price }));

        Assert.True(NativeSelectManyBinder.TryBind(mongoQ, collectionSelector));
        var and = Assert.IsType<MongoBinaryExpression>(mongoQ.Select.UnwindSource!.Filter);
        Assert.Equal(MongoBinaryOperator.AndAlso, and.Operator);
        // inner-only conjunct: inner field prefixed
        var left = Assert.IsType<MongoBinaryExpression>(and.Left);
        Assert.Equal("Items.Name", Assert.IsType<MongoFieldExpression>(left.Left).ElementName);
        // correlated conjunct: inner prefixed vs outer root
        var right = Assert.IsType<MongoBinaryExpression>(and.Right);
        Assert.Equal("Items.Name", Assert.IsType<MongoFieldExpression>(right.Left).ElementName);
        Assert.Equal("Name", Assert.IsType<MongoFieldExpression>(right.Right).ElementName);
    }

    [Fact]
    public void Inner_where_unsupported_correlated_operator_returns_false_without_mutation()
    {
        // i.Name.ToUpper() == o.Name — the two-scope translation rejects the operator, so the bind declines
        // cleanly with no partial mutation.
        var mongoQ = TestQuery();
        var collectionSelector = Build((Owner o) =>
            o.Items.AsQueryable().Where(i => i.Name.ToUpper() == o.Name).Select(i => new { o.Name, i.Price }));

        Assert.False(NativeSelectManyBinder.TryBind(mongoQ, collectionSelector));
        Assert.Null(mongoQ.Select.UnwindSource);
    }
```

Replace `Bare_nav_with_correlated_beyond_outer_where_returns_false` with:

```csharp
    [Fact]
    public void Bare_nav_with_correlated_beyond_outer_where_binds_with_expr_filter()
    {
        var mongoQ = TestQuery();
        var collectionSelector = Build((Owner o) => o.Items.AsQueryable().Where(i => i.Name == o.Name));

        Assert.True(NativeSelectManyBinder.TryBindBareNavUnwind(mongoQ, collectionSelector));
        var bin = Assert.IsType<MongoBinaryExpression>(mongoQ.Select.UnwindSource!.Filter);
        Assert.Equal(MongoBinaryOperator.Equal, bin.Operator);
        Assert.Equal("Items.Name", Assert.IsType<MongoFieldExpression>(bin.Left).ElementName);
        Assert.Equal("Name", Assert.IsType<MongoFieldExpression>(bin.Right).ElementName);
    }
```

(If the existing tests assert `Assert.Null(mongoQ.Select.UnwindSource)` after the decline, keep that shape only in the unsupported-operator test; the converted binds-tests assert the Filter instead.)

- [ ] **Step 2: Run the new tests to verify they fail**

Run:
```bash
dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj \
  -c "Debug EF10" --filter "FullyQualifiedName~NativeSelectManyBinderTests.Inner_where_correlated_beyond_outer_binds_with_expr_filter|FullyQualifiedName~NativeSelectManyBinderTests.Inner_where_mixed_inner_and_correlated_conjunct_binds|FullyQualifiedName~NativeSelectManyBinderTests.Bare_nav_with_correlated_beyond_outer_where_binds_with_expr_filter"
```
Expected: FAIL — the binds tests fail (correlated layers currently decline). The unsupported-operator test may already pass (still declines) but must stay green after the change.

- [ ] **Step 3: Add `outerEntityType` param and route the correlated branch**

In `NativeSelectManyBinder.cs`, replace the `TryBuildOwnedInnerFilter` per-layer loop (~lines 533-554) with:

```csharp
    private static bool TryBuildOwnedInnerFilter(
        IReadOnlyList<LambdaExpression> userPredicates, IEntityType innerEntityType, string unwindPath,
        ParameterExpression outerParam, IEntityType outerEntityType, out MongoExpression? filter)
    {
        filter = null;
        if (userPredicates.Count == 0)
            return true;

        var innerTranslator = new MongoExpressionTranslator(innerEntityType);
        foreach (var userPredicate in userPredicates)
        {
            if (userPredicate.Parameters.Count != 1)
                return false;

            MongoExpression conjunct;
            if (ReferencesParameter(userPredicate.Body, outerParam))
            {
                // Correlated-beyond-outer: translate with the two-scope translator — inner fields prefixed with
                // the unwind path (Items.Name), outer fields at document root (Name), routed by PARAMETER
                // IDENTITY (never by name, so Item.Name and Owner.Name never conflate). Used directly — NOT
                // blanket-prefixed. Renders as $expr. Declines cleanly (no mutation) if the operator is
                // unsupported; a correlated owned SelectMany has no driver-LINQ oracle, so a decline hard-fails
                // every mode.
                var twoScope = new MongoExpressionTranslator(innerEntityType, outerParam, outerEntityType, unwindPath);
                if (!twoScope.TryTranslate(userPredicate.Body, out var correlated))
                    return false;
                conjunct = correlated;
            }
            else
            {
                if (!innerTranslator.TryTranslate(userPredicate.Body, out var expr))
                    return false;
                conjunct = MongoFieldPrefixRewriter.Rewrite(expr!, unwindPath);
            }

            filter = filter == null
                ? conjunct
                : new MongoBinaryExpression(MongoBinaryOperator.AndAlso, filter, conjunct);
        }
        return true;
    }
```

Update the XML-doc summary above the method: change the "Declines … if any predicate references the outer parameter (correlated-beyond-outer …)" sentence to describe the new routing — a correlated layer is now translated two-scope (inner prefixed, outer at root, by identity) and rendered as `$expr`; it declines only if the operator is unsupported.

- [ ] **Step 4: Thread `outerEntityType` from both call sites**

In `TryBind` (~line 113):
```csharp
        if (!TryBuildOwnedInnerFilter(userPredicates, navigation.TargetEntityType, unwindPath, outerParam, outerEntityType, out var filter))
            return false;
```
In `TryBindBareNavUnwind` (~line 151):
```csharp
        if (!TryBuildOwnedInnerFilter(userPredicates, navigation.TargetEntityType, unwindPath, outerParam, outerEntityType, out var filter))
            return false;
```
(Both methods already declare `var outerEntityType = mongoQ.CollectionExpression.EntityType;` at ~line 76 / ~line 144 — reuse it, do not re-declare.)

- [ ] **Step 5: Run the binder tests to verify they pass**

Run:
```bash
dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj \
  -c "Debug EF10" --filter "FullyQualifiedName~NativeSelectManyBinderTests"
```
Expected: PASS — the converted/new tests pass and every pre-existing binder test (inner-only filtered `o.Items.Where(i => i.Price > 0)`, stacked, projected-transparent-identifier, reference tests) stays green (inner-only path byte-unchanged).

- [ ] **Step 6: EF8 build check + commit**

Run: `dotnet build src/MongoDB.EntityFrameworkCore/MongoDB.EntityFrameworkCore.csproj -c "Debug EF8"` — expect clean.

```bash
git add src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeSelectManyBinder.cs \
        tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/NativeSelectManyBinderTests.cs
git commit -m "EF-347: Route correlated-beyond-outer owned SelectMany filters to native \$expr"
```

---

### Task 2: End-to-end functional tests (real DB)

Convert the functional decline test to a native-success test and add real-DB coverage for the correlated owned shapes across all forms.

**Files:**
- Modify: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeSelectManyTests.cs` — the `SeedOwners()` seed (~line 134), the `Filtered_owned_correlated_beyond_outer_hard_fails_in_every_mode` test.

**Interfaces:**
- Consumes: the native binding from Task 1 (black-box).

- [ ] **Step 1: Make the seed discriminating**

`SeedOwners()` (~line 134) currently has no owner whose `Name` equals one of its items' `Name` (Alice→Widget/Gadget, Bob→empty, Carol→Thing), so `i.Name == o.Name` would match nothing — a vacuous test. ADD a discriminating owner whose Name matches one of its items and not another, e.g.:

```csharp
        new()
        {
            Id = ObjectId.GenerateNewId(), Name = "Match",
            Items =
            [
                new Item { Name = "Match", Price = 3m },   // i.Name == o.Name  → included
                new Item { Name = "NoMatch", Price = 4m }, // i.Name != o.Name  → excluded
            ],
        },
```

Prefer ADDING this owner over renaming Alice/Bob/Carol. Adding an owner grows the total item count, which may break existing owned tests that assert exact counts — the full-class run in Step 4 is the safety net; if an existing test breaks, adjust the added seed data (values/count) rather than editing unrelated tests. Most existing owned tests recompute their expected result from the seed in-memory and are robust to added rows.

- [ ] **Step 2: Convert the decline test to native-success + add coverage**

Replace `Filtered_owned_correlated_beyond_outer_hard_fails_in_every_mode` with a native-success test and add the sibling cases below. Read `CreateContext` / `CreateContextWithLogging` / the neighboring owned filtered-inner + bare-owned tests for the exact helper signatures, seed access, MQL-capture mechanism, and `AsNoTracking()` handling — match them; do not invent helpers. Every correlated success test uses `MongoQueryMode.NativeOnly` + an in-memory oracle (per Global Constraints — NO parity, NO graceful-fallback assertions).

```csharp
    [Fact]
    public void Owned_correlated_beyond_outer_inner_select_form_goes_native()
    {
        // o.Items.Where(i => i.Name == o.Name) — correlated beyond the owner/element pair. NativeOnly + in-memory
        // oracle (no driver-LINQ oracle for any correlated owned shape, per the spike).
        var seed = SeedOwners();
        using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(Owned_correlated_beyond_outer_inner_select_form_goes_native));

        var result = db.Entities
            .SelectMany(o => o.Items.Where(i => i.Name == o.Name).Select(i => new { o.Name, i.Price }))
            .AsEnumerable().OrderBy(x => x.Name).ThenBy(x => x.Price).ToList();

        var expected = seed
            .SelectMany(o => o.Items.Where(i => i.Name == o.Name).Select(i => new { o.Name, i.Price }))
            .OrderBy(x => x.Name).ThenBy(x => x.Price).ToList();
        Assert.Equal(expected, result);
        Assert.NotEmpty(result); // non-vacuous
    }

    [Fact]
    public void Owned_correlated_beyond_outer_explicit_form_goes_native()
    {
        var seed = SeedOwners();
        using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(Owned_correlated_beyond_outer_explicit_form_goes_native));

        var result = db.Entities
            .SelectMany(o => o.Items.Where(i => i.Name == o.Name), (o, i) => new { o.Name, i.Price })
            .AsEnumerable().OrderBy(x => x.Name).ThenBy(x => x.Price).ToList();

        var expected = seed
            .SelectMany(o => o.Items.Where(i => i.Name == o.Name), (o, i) => new { o.Name, i.Price })
            .OrderBy(x => x.Name).ThenBy(x => x.Price).ToList();
        Assert.Equal(expected, result);
        Assert.NotEmpty(result);
    }

    [Fact]
    public void Owned_correlated_beyond_outer_bare_whole_element_goes_native()
    {
        // Bare-whole-element result requires AsNoTracking() (owned element without owner). Match how the existing
        // bare-owned tests apply it.
        var seed = SeedOwners();
        using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(Owned_correlated_beyond_outer_bare_whole_element_goes_native));

        var result = db.Entities.AsNoTracking()
            .SelectMany(o => o.Items.Where(i => i.Name == o.Name))
            .AsEnumerable().Select(i => i.Price).OrderBy(p => p).ToList();

        var expected = seed
            .SelectMany(o => o.Items.Where(i => i.Name == o.Name))
            .Select(i => i.Price).OrderBy(p => p).ToList();
        Assert.Equal(expected, result);
        Assert.NotEmpty(result);
    }

    [Fact]
    public void Owned_correlated_beyond_outer_stacked_where_goes_native()
    {
        var seed = SeedOwners();
        using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(Owned_correlated_beyond_outer_stacked_where_goes_native));

        var result = db.Entities
            .SelectMany(o => o.Items.Where(i => i.Name == o.Name).Where(i => i.Price > 0m), (o, i) => new { o.Name, i.Price })
            .AsEnumerable().OrderBy(x => x.Name).ThenBy(x => x.Price).ToList();

        var expected = seed
            .SelectMany(o => o.Items.Where(i => i.Name == o.Name).Where(i => i.Price > 0m), (o, i) => new { o.Name, i.Price })
            .OrderBy(x => x.Name).ThenBy(x => x.Price).ToList();
        Assert.Equal(expected, result);
        Assert.NotEmpty(result);
    }

    [Fact]
    public void Owned_correlated_beyond_outer_excluding_all_children_contributes_no_rows()
    {
        var seed = SeedOwners();
        using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(Owned_correlated_beyond_outer_excluding_all_children_contributes_no_rows));

        var result = db.Entities
            .SelectMany(o => o.Items.Where(i => i.Name == o.Name && i.Price < 0m), (o, i) => new { o.Name, i.Price })
            .ToList();

        Assert.Empty(result);
    }

    [Fact]
    public void Owned_correlated_beyond_outer_composes_with_parametrized_outer_predicate()
    {
        var seed = SeedOwners();
        using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(Owned_correlated_beyond_outer_composes_with_parametrized_outer_predicate));

        var cutoff = "Bob";
        var result = db.Entities.Where(o => o.Name != cutoff)
            .SelectMany(o => o.Items.Where(i => i.Name == o.Name), (o, i) => new { o.Name, i.Price })
            .AsEnumerable().OrderBy(x => x.Name).ThenBy(x => x.Price).ToList();

        var expected = seed.Where(o => o.Name != cutoff)
            .SelectMany(o => o.Items.Where(i => i.Name == o.Name), (o, i) => new { o.Name, i.Price })
            .OrderBy(x => x.Name).ThenBy(x => x.Price).ToList();
        Assert.Equal(expected, result);
        Assert.NotEmpty(result);
    }

    [Fact]
    public void Owned_correlated_beyond_outer_emits_expr_match_after_unwind()
    {
        // MQL assertion: the correlated conjunct renders as $expr in the post-$unwind $match, comparing the
        // unwind-path-prefixed inner field to the root outer field. Use the file's existing MQL-capture helper
        // (CreateContextWithLogging + the ExecutedMqlQuery log accessor) exactly as the neighboring
        // Filtered_owned_selectmany_emits_match_after_unwind / *_emits_* tests do; assert the captured MQL
        // contains "$unwind", "$match", "$expr", "Items.Name", and "$Name".
    }
```

> Note for the implementer: `CreateContext`/`CreateContextWithLogging` signatures, the MQL-capture accessor, and the `AsNoTracking()` idiom are all defined earlier in this same file — read the neighboring owned tests (`Bare_owned_*`, `Filtered_owned_*`, `*_emits_match_after_unwind`) and match them exactly rather than inventing helpers. The `Owned_correlated_beyond_outer_emits_expr_match_after_unwind` MQL assertion in particular must mirror the existing `*_emits_*` capture mechanism.

- [ ] **Step 3: Run the correlated functional tests (EF10)**

Run:
```bash
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --filter "FullyQualifiedName~NativeSelectManyTests.Owned_correlated"
```
Expected: PASS.

- [ ] **Step 4: Run the full `NativeSelectManyTests` class (EF10) to catch seed-change regressions**

Run:
```bash
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --filter "FullyQualifiedName~NativeSelectManyTests"
```
Expected: PASS — the added "Match" owner didn't disturb existing owned/reference tests (fix any exact-count assertions the new owner shifts, per Step 1).

- [ ] **Step 5: Commit**

```bash
git add tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeSelectManyTests.cs
git commit -m "EF-347: Functional tests for native correlated-beyond-outer owned SelectMany"
```

---

### Task 3: As-built docs

Document the newly-native shape and the guard-turned-router in `Query/AGENTS.md`, correct the owned-filtered-inner note's oracle claim as it applies to the correlated case, and fold the pre-existing m6 owned-note staleness.

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md`

- [ ] **Step 1: Add the as-built note + refresh owned-note wording**

In `Query/AGENTS.md`:
1. Add a note (after the "Filtered inner-element OWNED `SelectMany`" note) covering: an owned-collection `SelectMany` whose inner filter references the outer owner now goes native, all owned result shapes, full breadth. Mechanism: `TryBuildOwnedInnerFilter`'s `ReferencesParameter` check is now a ROUTER — a correlated layer is two-scope-translated (inner prefixed with the unwind path, outer at document root, by `ReferenceEquals` identity NOT name, so `Item.Name`/`Owner.Name` never conflate) and stored on `MongoUnwindSource.Filter`; inner-only layers keep the existing translate + `MongoFieldPrefixRewriter` path. Lowerer/renderer unchanged (owned `$unwind` in place → `$match` with `$expr`, `$Items.Name` vs `$Name`, before `$replaceRoot`/`$project`). **Uniform hard-fail, no oracle for ANY projection shape** (spike-confirmed — the correlation, not the projection, breaks driver-LINQ; this DIFFERS from the non-correlated filtered-inner case, where an inner-only projection has an oracle). EF-221 value-equality inherited; no `#if`.
2. In the "Filtered inner-element OWNED `SelectMany`" note, update the "Still deferred (not native)" tail so **correlated-beyond-outer is NO LONGER listed** — the remaining owned deferrals are a computed projection leaf and a nested owned `SelectMany`. Also note that the oracle-depends-on-projection claim in that note applies to the NON-correlated filtered-inner case; a correlated-beyond-outer filter has no oracle for any shape (pointer to the new note).
3. **Fold the m6 staleness:** in the "Bare whole OWNED-collection-element `SelectMany`" note, the deferred list says "a FILTERED/correlated-beyond-FK inner … still out of scope". Correct it — owned FILTERED-inner shipped in `6ae61ac` and owned correlated-beyond-outer ships in THIS slice, so both are now native; the remaining owned deferrals are a computed projection leaf and a nested owned `SelectMany`. (Use "correlated-beyond-outer" terminology for the owned form, not "correlated-beyond-FK" — owned collections have no FK.)

- [ ] **Step 2: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/AGENTS.md
git commit -m "EF-347: AGENTS.md as-built note for native correlated-beyond-outer owned SelectMany"
```

---

## Finalization (controller-driven, after Task 3 review)

Not plan tasks — the controller runs these:

1. **Full 3-version `/test-all` foreground** (EF8/EF9/EF10), summing all three per-assembly blocks — GREEN 0-fail required.
2. **NativeOnly spec sweep** (`MONGODB_EF_NATIVE_ONLY=1`) — no regressions vs the `933eb94` baseline (2192P; the correlated owned shape isn't in Northwind, so zero delta expected).
3. **Opus whole-branch review** (`933eb94..HEAD`).
4. **Squash** to one commit above `933eb94`: back up `git branch -f EF-347-selectmany-owned-correlated-presquash HEAD`, then `git reset --soft 933eb94` + one `git commit -F <message>`; verify `git diff --quiet EF-347-selectmany-owned-correlated-presquash HEAD`. Keep the presquash backup until merge.
5. **User drives the fast-forward push** of `origin/NativeQueryOngoing` (`933eb94` → new tip) — verify FF first, then plain `git push` (no `--force`).

## Self-Review

- **Spec coverage:** correlated routing in the owned path → Task 1. All owned result shapes (inner-Select, explicit, bare-element, stacked) → Task 1 (shared site) + Task 2 (functional). Uniform hard-fail / NativeOnly-proof → Global Constraints + Task 2 (all correlated tests NativeOnly, no parity). Identity-not-name / shadow → Task 1 unit + Task 2 shadow via `Match`/`Match` seed. Lowerer/renderer unchanged → grounded (Filter → MongoMatchStage → RenderMatch), no task. EF-221 no guard → Global Constraints + Task 3 note. m6 fold → Task 3.
- **Placeholder scan:** all code steps carry real code. The `Owned_correlated_beyond_outer_emits_expr_match_after_unwind` MQL assertion is delegated to "match the neighboring `*_emits_*` capture helper" because that helper is local to the test file and must be matched, not invented.
- **Type consistency:** `TryBuildOwnedInnerFilter`'s new `IEntityType outerEntityType` param and the two-scope `MongoExpressionTranslator(innerEntityType, outerParam, outerEntityType, unwindPath)` call match the real signatures read during planning. `MongoUnwindSource.Filter`, `MongoBinaryExpression.Operator/Left/Right`, `MongoFieldExpression.ElementName` all match.
