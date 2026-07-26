# Native SelectMany computed-leaf Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make a single-scope numeric-arithmetic (`+ - * / %`) projection leaf inside a SelectMany trailing projection go native, reusing `MongoExpressionTranslator.TryTranslateValue` and `MongoFieldPrefixRewriter`.

**Architecture:** Add one branch to `NativeSelectManyBinder.TryBindTransparentIdentifierProjection` (the trailing-projection binder shared by the owned explicit/query-syntax, owned bare-nav, reference, nested-reference, and filtered SelectMany forms). When a projection member is an arithmetic `BinaryExpression`, re-root the whole subtree onto the resolved scope's synthetic parameter (a small `ExpressionVisitor`), translate via that scope's `TryTranslateValue`, and prefix inner-scope field refs with the unwind path. No lowerer/renderer/emit/shaper changes are anticipated — a spike (Task 1) proves read-back before any production edit.

**Tech Stack:** C# / EF Core provider internals, xUnit + plain `Assert.*` (no FluentAssertions), MongoDB aggregation `$project`.

## Global Constraints

- Spec: `docs/superpowers/specs/2026-07-25-native-selectmany-computed-leaf-design.md`.
- Preserve file BOMs. All `src/` code obeys `<Nullable>enable</Nullable>` — annotate accordingly.
- No `#if` — behavior must be identical across EF8/EF9/EF10 (all touched types are `internal`).
- No public-API surface changes; no new annotation keys; not a breaking change (native execution path only; results unchanged).
- Tests run **serially**; each test uses a uniquely-named database/collection via the existing `CreateContext`/`CreateRefContext` helpers.
- **The only reliable "goes native" signal is `MongoQueryMode.NativeOnly` succeeding** (fallback would throw `NativeTranslationNotSupportedException`). MQL shape alone does not prove native.
- Prove read-back by comparing **materialized values** against an in-memory oracle — emitted MQL looking right is NOT sufficient (the plain-`Select` arithmetic slice shipped correct MQL but wrong data before its shaper fix was found).
- Single squashed commit for the implementation, plain FF onto `origin/NativeQueryOngoing`, keep a `-presquash` backup, per the stacked-PR workflow.

---

### Task 1: Spike — prove read-back and the re-rooting mechanism (throwaway)

**Files:**
- Modify (throwaway): `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeSelectManyBinder.cs`
- Create (throwaway): `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/SpikeComputedLeafTests.cs`

**Interfaces:**
- Consumes: existing `TryBindTransparentIdentifierProjection` loop, `TryResolveScopeDepth`, `MongoExpressionTranslator.TryTranslateValue`, `MongoFieldPrefixRewriter.Rewrite`, `MongoUnwindSource.InnerScopePath`.
- Produces: a go/no-go finding for the plan (specifically: does the SelectMany by-index shaper clobber a computed leaf's value, the way the plain-`Select` visitor did?). Nothing is kept.

- [ ] **Step 1: Add a rough arithmetic branch to the binder (throwaway).**

In `TryBindTransparentIdentifierProjection`, inside the `foreach (var (alias, argExpr) in members)` loop, replace the current `if (argExpr is not MemberExpression member || !TryResolveScopeDepth(...)) return false;` guard with a bare-member-else-arithmetic structure that, for an arithmetic `BinaryExpression`, walks the subtree re-rooting each `ti.<hops>.<m>` onto `scopeParams[k]` (requiring one shared scope), calls `translators[k].TryTranslateValue`, and prefixes via `MongoFieldPrefixRewriter` when `k > 0`. A quick inline implementation is fine here — this code is discarded. (Task 2 has the clean version.)

- [ ] **Step 2: Write a scratch live test covering the four in-scope forms.**

```csharp
// SpikeComputedLeafTests.cs — THROWAWAY. Uses the same fixtures/helpers as NativeSelectManyTests.
[Fact]
public void Spike_owned_explicit_arithmetic_leaf()
{
    var seed = SeedOwners();
    using var db = CreateContext(seed, MongoQueryMode.NativeOnly, nameof(Spike_owned_explicit_arithmetic_leaf));
    var result = db.Entities.SelectMany(o => o.Items, (o, i) => new { i.Name, Doubled = i.Price * 2m })
        .AsEnumerable().OrderBy(x => x.Doubled).ToList();
    var expected = seed.SelectMany(o => o.Items, (o, i) => new { i.Name, Doubled = i.Price * 2m })
        .OrderBy(x => x.Doubled).ToList();
    Assert.Equal(expected, result); // MUST compare VALUES, not just count — this is the clobber probe.
}
```

Add analogous scratch tests for: filtered owned (`o.Items.Where(i => i.Price > 6m)` + `i.Price * 2m`), reference (`db.Owners.SelectMany(o => o.Refs, (o, r) => new { o.Name, Doubled = r.Score * 2 })`, using `CreateRefContext`), and nested reference if the fixture supports it. Every assertion compares full projected values against the in-memory oracle.

- [ ] **Step 3: Run the spike tests under a live MongoDB.**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj -c "Debug EF10" --filter "FullyQualifiedName~SpikeComputedLeaf"`
Expected: all pass with correct VALUES. **If any returns wrong values while emitting a plausible `$project`, that is the shaper clobber** — record exactly which form, whether the SelectMany by-index shaper needs an analogous `BinaryExpression` fix, and STOP to revise the plan (the slice is then no longer shaper-free).

- [ ] **Step 4: Record findings, then discard the spike.**

Write findings to `.superpowers/sdd/EF-347-selectmany-computed-leaf-spike.md` (does the shaper clobber? does EF deliver the leaf intact per form? does the re-rooting handle nested scopes? does the owned inner-only oracle still hold?). Then revert all spike edits:

```bash
git checkout src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeSelectManyBinder.cs
rm tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/SpikeComputedLeafTests.cs
git add .superpowers/sdd/EF-347-selectmany-computed-leaf-spike.md && git commit -m "EF-347: SelectMany computed-leaf spike findings"
```

- [ ] **Step 5: STOP for review.** Report whether the mechanism is shaper-free (as designed) or needs a shaper fix. Await approval before Task 2.

---

### Task 2: Implement the single-scope arithmetic computed-leaf branch

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeSelectManyBinder.cs` (the `TryBindTransparentIdentifierProjection` loop, ~line 536-551; add the helper + nested visitor)
- Test: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeSelectManyTests.cs`
- Test fixture: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeSelectManyTests.cs` — add a numeric property to `NestLeaf` (nested-reference fixture) + seed it, to enable the nested (scope k=2) computed-leaf test. The spike could not exercise a deeper-than-k=1 computed leaf because the nested fixture had no numeric field (user-approved addition).

**Interfaces:**
- Consumes: `TryResolveScopeDepth(Expression?, ParameterExpression, int, out int)` (existing private static); `MongoExpressionTranslator.TryTranslateValue(Expression, out MongoExpression?)`; `MongoFieldPrefixRewriter.Rewrite(MongoExpression, string)`; `MongoUnwindSource.InnerScopePath`.
- Produces: no new public/internal surface — the change is confined to `TryBindTransparentIdentifierProjection`'s acceptance set.

- [ ] **Step 1: Repurpose the two flipping fallback tests to assert native (they now fail).**

In `NativeSelectManyTests.cs`, replace `Explicit_result_selector_form_computed_leaf_falls_back_gracefully_except_under_NativeOnly` (line ~497) with a native-asserting test (the leaf `i.Price * 2m` is single inner scope, and the owned inner-only projection has a driver oracle → assert `Native == DriverLinq` parity AND `NativeOnly` succeeds):

```csharp
[Fact]
public void Explicit_result_selector_form_computed_arithmetic_leaf_goes_native()
{
    // EF-347 SelectMany computed-leaf: a single-scope (inner-only) arithmetic leaf in the trailing
    // projection now binds natively via TryBindTransparentIdentifierProjection's arithmetic branch
    // (reusing TryTranslateValue). Owned inner-only projection HAS a driver oracle, so assert parity
    // across Native/DriverLinq AND that NativeOnly succeeds (the "went native" signal).
    var seed = SeedOwners();
    var expected = seed.SelectMany(o => o.Items, (o, i) => new { X = i.Price * 2m })
        .OrderBy(r => r.X).ToList();

    foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq, MongoQueryMode.NativeOnly })
    {
        using var db = CreateContext(seed, mode,
            nameof(Explicit_result_selector_form_computed_arithmetic_leaf_goes_native) + mode);
        var result = db.Entities.SelectMany(o => o.Items, (o, i) => new { X = i.Price * 2m })
            .AsEnumerable().OrderBy(r => r.X).ToList();
        Assert.Equal(expected, result);
    }
}
```

Replace `Filtered_owned_computed_projection_leaf_falls_back_gracefully_except_under_NativeOnly` (line ~806) with the filtered-native analogue:

```csharp
[Fact]
public void Filtered_owned_computed_arithmetic_leaf_goes_native()
{
    // A FILTERED owned SelectMany whose trailing projection has a single-scope (inner-only) arithmetic
    // leaf (i.Price * 2m) now goes native (the $match from the inner Where is emitted before the
    // computed $project). Inner-only projection has a driver oracle → parity + NativeOnly succeeds.
    var seed = SeedOwners();
    var expected = seed.SelectMany(o => o.Items.Where(i => i.Price > 6m), (o, i) => new { X = i.Price * 2m })
        .OrderBy(r => r.X).ToList();

    foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq, MongoQueryMode.NativeOnly })
    {
        using var db = CreateContext(seed, mode,
            nameof(Filtered_owned_computed_arithmetic_leaf_goes_native) + mode);
        var result = db.Entities.SelectMany(o => o.Items.Where(i => i.Price > 6m), (o, i) => new { X = i.Price * 2m })
            .AsEnumerable().OrderBy(r => r.X).ToList();
        Assert.Equal(expected, result);
    }
}
```

- [ ] **Step 2: Add the new native / decline / mixed coverage tests (also failing).**

Append to `NativeSelectManyTests.cs` (near the other reference tests for the reference ones):

```csharp
[Fact]
public void Reference_form_computed_arithmetic_leaf_goes_native()
{
    // Reference SelectMany has NO driver oracle, so prove native via NativeOnly + expected in-memory set,
    // and assert the computed $project MQL. r.Score * 2 is single inner scope → $_lookup_Refs.Score.
    using var db = CreateRefContextWithLogging(MongoQueryMode.NativeOnly,
        nameof(Reference_form_computed_arithmetic_leaf_goes_native), out var owners, out var items, out var spyLogger);

    var result = db.Owners.SelectMany(o => o.Refs, (o, r) => new { o.Name, Doubled = r.Score * 2 })
        .AsEnumerable().OrderBy(x => x.Name).ThenBy(x => x.Doubled).ToList();

    var expected = owners.SelectMany(o => items.Where(r => r.OwnerId == o.Id), (o, r) => new { o.Name, Doubled = r.Score * 2 })
        .OrderBy(x => x.Name).ThenBy(x => x.Doubled).ToList();

    Assert.Equal(expected, result);

    var message = spyLogger.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery);
    Assert.Contains("\"Name\" : \"$Name\"", message);
    Assert.Contains("$multiply", message);
    Assert.Contains("$_lookup_Refs.Score", message);
}

[Fact]
public void Two_field_single_scope_computed_leaf_goes_native()
{
    // Two field-refs in one leaf, both inner scope → both get the Items. prefix. Owned inner-only has an oracle.
    var seed = SeedOwners();
    var expected = seed.SelectMany(o => o.Items, (o, i) => new { Sq = i.Price * i.Price })
        .OrderBy(r => r.Sq).ToList();

    foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq, MongoQueryMode.NativeOnly })
    {
        using var db = CreateContext(seed, mode, nameof(Two_field_single_scope_computed_leaf_goes_native) + mode);
        var result = db.Entities.SelectMany(o => o.Items, (o, i) => new { Sq = i.Price * i.Price })
            .AsEnumerable().OrderBy(r => r.Sq).ToList();
        Assert.Equal(expected, result);
    }
}

[Fact]
public void Mixed_member_and_computed_leaf_projection_goes_native()
{
    // One bare member (o.Name) + one arithmetic leaf (i.Price * 2m) in the same projection — both aliases correct.
    var seed = SeedOwners();
    var expected = seed.SelectMany(o => o.Items, (o, i) => new { o.Name, Doubled = i.Price * 2m })
        .OrderBy(r => r.Name).ThenBy(r => r.Doubled).ToList();

    foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq, MongoQueryMode.NativeOnly })
    {
        using var db = CreateContext(seed, mode, nameof(Mixed_member_and_computed_leaf_projection_goes_native) + mode);
        var result = db.Entities.SelectMany(o => o.Items, (o, i) => new { o.Name, Doubled = i.Price * 2m })
            .AsEnumerable().OrderBy(r => r.Name).ThenBy(r => r.Doubled).ToList();
        Assert.Equal(expected, result);
    }
}

[Fact]
public void Nested_reference_single_scope_computed_leaf_goes_native()
{
    // A single-scope arithmetic leaf at the DEEPEST scope (k=2, the leaf) of a two-level nested reference
    // SelectMany — proves the deeper InnerScopePath prefixing (_lookup_Leaves.Height). No driver oracle
    // (cross-collection) → prove via Native + NativeOnly + expected in-memory set.
    foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.NativeOnly })
    {
        using var db = CreateNestContext(mode,
            nameof(Nested_reference_single_scope_computed_leaf_goes_native) + mode, out var owners, out var mids, out var leaves);

        var result = (from o in db.Owners from m in o.Mids from l in m.Leaves
                      select new { o.Name, m.Tag, Doubled = l.Height * 2 })
            .AsEnumerable().OrderBy(x => x.Name).ThenBy(x => x.Tag).ThenBy(x => x.Doubled).ToList();

        var expected = (from o in owners
                        from m in mids.Where(m => m.OwnerId == o.Id)
                        from l in leaves.Where(l => l.MidId == m.Id)
                        select new { o.Name, m.Tag, Doubled = l.Height * 2 })
            .OrderBy(x => x.Name).ThenBy(x => x.Tag).ThenBy(x => x.Doubled).ToList();

        Assert.Equal(expected, result);
        Assert.Equal(3, result.Count);
    }
}

[Fact]
public void Cross_scope_computed_leaf_declines_and_hard_fails_in_every_mode()
{
    // o.Threshold * r.Score spans OUTER + INNER → single-scope check declines → whole projection declines.
    // Reference form has no driver oracle → hard-fail in every mode (the retained single-scope boundary).
    foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq, MongoQueryMode.NativeOnly })
    {
        using var db = CreateRefContext(mode,
            nameof(Cross_scope_computed_leaf_declines_and_hard_fails_in_every_mode) + mode, out _, out _);
        Assert.ThrowsAny<Exception>(() =>
            db.Owners.SelectMany(o => o.Refs, (o, r) => new { Combined = o.Threshold * r.Score }).ToList());
    }
}

[Fact]
public void Integer_division_computed_leaf_declines_and_hard_fails_in_every_mode()
{
    // r.Score / 2 is an integer-result division → Guard A in TryTranslateValue declines → projection declines.
    // Reference form has no oracle → hard-fail in every mode.
    foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq, MongoQueryMode.NativeOnly })
    {
        using var db = CreateRefContext(mode,
            nameof(Integer_division_computed_leaf_declines_and_hard_fails_in_every_mode) + mode, out _, out _);
        Assert.ThrowsAny<Exception>(() =>
            db.Owners.SelectMany(o => o.Refs, (o, r) => new { Half = r.Score / 2 }).ToList());
    }
}
```

- [ ] **Step 2b: Add the numeric field to the nested-reference fixture (for the nested test above).**

In `NativeSelectManyTests.cs`, add a numeric property to `NestLeaf` (the class at ~line 1017) and seed distinct values on the three leaves `CreateNestContext` builds (Red/Blue/Green at ~lines 1123-1125):

```csharp
// in class NestLeaf:
public int Height { get; set; }

// at the three seeded leaves (lines ~1123-1125), add a distinct Height each, e.g.:
var leafA1a = new NestLeaf { Id = ObjectId.GenerateNewId(), Label = "Red",   MidId = midA1.Id, Height = 1 };
var leafA1b = new NestLeaf { Id = ObjectId.GenerateNewId(), Label = "Blue",  MidId = midA1.Id, Height = 2 };
var leafA2a = new NestLeaf { Id = ObjectId.GenerateNewId(), Label = "Green", MidId = midA2.Id, Height = 3 };
```

Leave any other `NestLeaf` seed instances (e.g. `leaf1`/`leaf2` in other nested tests) at the default `Height = 0` — they are unaffected. This is a purely additive fixture change; existing nested tests do not reference `Height`.

- [ ] **Step 3: Run the new/repurposed tests to confirm they fail.**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj -c "Debug EF10" --filter "FullyQualifiedName~NativeSelectManyTests&(FullyQualifiedName~computed_arithmetic|FullyQualifiedName~single_scope_computed|FullyQualifiedName~computed_leaf_projection|FullyQualifiedName~Cross_scope_computed|FullyQualifiedName~Integer_division_computed)"`
Expected: the four "goes_native" tests FAIL under `NativeOnly` (currently declines → throws); the two decline tests already PASS (they hard-fail as asserted, both before and after — they guard the boundary).

- [ ] **Step 4: Implement the arithmetic branch + re-rooting visitor.**

In `NativeSelectManyBinder.cs`, replace the loop body of `TryBindTransparentIdentifierProjection` (currently ~lines 536-551) with:

```csharp
foreach (var (alias, argExpr) in members)
{
    MongoExpression projected;

    if (argExpr is MemberExpression member
        && TryResolveScopeDepth(member.Expression, ti, sources.Count, out var scopeIndex))
    {
        var rerooted = Expression.MakeMemberAccess(scopeParams[scopeIndex], member.Member);
        if (!translators[scopeIndex].TryTranslateField(rerooted, out var field))
            return false;

        projected = scopeIndex > 0
            ? new MongoFieldExpression(field.Property, sources[scopeIndex - 1].InnerScopePath + "." + field.ElementName)
            : field;
    }
    else if (argExpr is BinaryExpression { NodeType: ExpressionType.Add or ExpressionType.Subtract
                 or ExpressionType.Multiply or ExpressionType.Divide or ExpressionType.Modulo }
             && TryTranslateSingleScopeComputedLeaf(argExpr, ti, sources, translators, scopeParams, out var computed))
    {
        projected = computed;
    }
    else
    {
        return false;
    }

    if (!seen.Add(alias)) return false;
    projections.Add(new MongoProjection(alias, projected));
}
```

Add the helper and nested visitor to the class (place after `TryResolveScopeDepth`):

```csharp
/// <summary>
/// Translates a SINGLE-SCOPE arithmetic computed projection leaf (EF-347 SelectMany computed-leaf) — every
/// scope-rooted member operand (<c>ti.Outer…</c>/<c>ti.Inner…</c>) in the leaf must resolve to the SAME scope.
/// Re-roots the whole arithmetic subtree onto that scope's synthetic parameter, reuses
/// <see cref="MongoExpressionTranslator.TryTranslateValue"/> (arithmetic assembly + the integer-division and
/// converter/representation guards), then prefixes inner-scope field refs with the unwind path via
/// <see cref="MongoFieldPrefixRewriter"/> — exactly as the bare-member branch prefixes a single field. Declines
/// (returns <see langword="false"/>, with NO mutation) for a cross-scope leaf, a leaf with no scope-rooted
/// operand, or anything <see cref="MongoExpressionTranslator.TryTranslateValue"/> rejects. Cross-scope leaves
/// (e.g. <c>o.Discount * i.Price</c>) are a deferred follow-on.
/// </summary>
private static bool TryTranslateSingleScopeComputedLeaf(
    Expression leaf,
    ParameterExpression ti,
    IReadOnlyList<MongoUnwindSource> sources,
    MongoExpressionTranslator[] translators,
    ParameterExpression[] scopeParams,
    [NotNullWhen(true)] out MongoExpression? result)
{
    result = null;

    var visitor = new ScopeRerootingVisitor(ti, sources.Count, scopeParams);
    var rerooted = visitor.Visit(leaf);
    if (visitor.CrossScope || visitor.ResolvedScope is not { } scope)
        return false;

    if (!translators[scope].TryTranslateValue(rerooted, out var computed))
        return false;

    result = scope > 0
        ? MongoFieldPrefixRewriter.Rewrite(computed, sources[scope - 1].InnerScopePath)
        : computed;
    return true;
}

/// <summary>
/// Rewrites every scope-rooted transparent-identifier member access (<c>ti.Outer…/ti.Inner…</c>) in an
/// arithmetic leaf onto the matching per-scope synthetic parameter, recording the single scope it resolves to
/// (or flagging <see cref="CrossScope"/> if operands span more than one). A member whose accessor chain is not
/// a scope-rooted transparent-identifier chain is left untouched (so a constant/parameter operand still reaches
/// <see cref="MongoExpressionTranslator.TryTranslateValue"/> unchanged).
/// </summary>
private sealed class ScopeRerootingVisitor(ParameterExpression ti, int sourceCount, ParameterExpression[] scopeParams)
    : ExpressionVisitor
{
    public int? ResolvedScope { get; private set; }
    public bool CrossScope { get; private set; }

    protected override Expression VisitMember(MemberExpression node)
    {
        if (TryResolveScopeDepth(node.Expression, ti, sourceCount, out var scope))
        {
            if (ResolvedScope is { } prior && prior != scope)
                CrossScope = true;
            ResolvedScope = scope;
            return Expression.MakeMemberAccess(scopeParams[scope], node.Member);
        }

        return base.VisitMember(node);
    }
}
```

Ensure `using System.Diagnostics.CodeAnalysis;` (for `NotNullWhen`) and `using System.Linq.Expressions;` are present at the top of the file (they are already used elsewhere in this file; add only if the build complains).

- [ ] **Step 5: Run the full `NativeSelectManyTests` class green.**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj -c "Debug EF10" --filter "FullyQualifiedName~NativeSelectManyTests"`
Expected: PASS — the four new "goes_native" tests pass; the two decline tests still pass; and the retained regression guards are unchanged and green: `Reference_form_computed_leaf_hard_fails_in_every_mode` (string concat `r.Tag + "!"`, still declines) and `Computed_leaf_hard_fails_in_every_mode` (inner-`Select` form `TryBind`, out of scope, still hard-fails).

- [ ] **Step 6: Commit.**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeSelectManyBinder.cs \
        tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeSelectManyTests.cs
git commit -m "EF-347: Native single-scope arithmetic computed leaf in SelectMany projection"
```

- [ ] **Step 7: STOP for review.**

---

### Task 3: Documentation — AGENTS.md as-built note + wording correction

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md`

**Interfaces:** none (docs only).

- [ ] **Step 1: Add a dedicated as-built note.**

Add a "**Native SelectMany computed-leaf (EF-347).**" note (mirroring the other EF-347 SelectMany notes' style) describing: the arithmetic branch added to `TryBindTransparentIdentifierProjection`; the `ScopeRerootingVisitor` single-scope re-rooting; the reuse of `TryTranslateValue` (Guards A/B) + `MongoFieldPrefixRewriter`; that it applies across owned explicit/query-syntax, owned bare-nav, reference, nested-reference, and filtered forms; the disposition (owned inner-only → graceful parity where an oracle exists, reference → NativeOnly + expected-set); and the precise deferred set (cross-scope leaves; the inner-`Select` form's `TryBind`; the non-arithmetic long tail; integer division via Guard A).

- [ ] **Step 2: Correct the plain-`Select` arithmetic note's deferred wording.**

In the "**Native arithmetic computed projections (EF-347).**" note, update the "**Deferred:** a computed leaf inside a `SelectMany` … planned as the next slice" sentence to state that a **single-scope** arithmetic computed leaf inside a SelectMany trailing projection now goes native (via `TryBindTransparentIdentifierProjection`), while **cross-scope** leaves and the inner-`Select` form's `TryBind` remain deferred. Cross-reference the new note.

- [ ] **Step 3: Commit.**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/AGENTS.md
git commit -m "EF-347: Doc native SelectMany computed-leaf"
```

- [ ] **Step 4: STOP for review.**

---

### Task 4: Full 3-version verification + native spec sweep

**Files:** none (verification only).

**Interfaces:** none.

- [ ] **Step 1: Run the `/test-all` skill (build + test EF8, EF9, EF10).**

Controller runs the three per-version isolated testcontainer runs in the foreground (per the process lesson — a single massive background run can hang). Confirm all three GREEN with pass counts ≥ the `ad72ae2` baseline and **zero** new spec-baseline failures.

- [ ] **Step 2: Run the `NativeOnly` spec sweep.**

Run: `MONGODB_EF_NATIVE_ONLY=1 dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj -c "Debug EF10" --no-build --filter "FullyQualifiedName~SpecificationTests"`
Expected: no new native-path failures vs. the current native baseline (a Northwind SelectMany arithmetic-projection case may flip from throwing to passing — record it if so; do not manufacture a flip).

- [ ] **Step 3: STOP for review.** Report the 3-version results and any spec flips. After approval: whole-branch review, then squash the implementation to one commit and push per the stacked-PR workflow (plain FF onto `origin/NativeQueryOngoing`, keep a `-presquash` backup).

---

## Self-Review

**Spec coverage:**
- Single-scope arithmetic branch in `TryBindTransparentIdentifierProjection` → Task 2 Step 4. ✓
- `ScopeRerootingVisitor` single-scope enforcement (cross-scope declines) → Task 2 Step 4 + `Cross_scope_computed_leaf_declines…` test. ✓
- Reuse `TryTranslateValue` (Guards A/B) → Task 2 Step 4 + `Integer_division_computed_leaf_declines…` (Guard A) test. ✓
- `MongoFieldPrefixRewriter` inner-scope prefixing → Task 2 Step 4 + `Two_field_single_scope…` / reference-MQL tests. ✓
- Emit side unchanged (renders via `MongoAggregationExpressionRenderer`) → no task needed; verified end-to-end Task 1 + Task 2 Step 5. ✓
- Read-back proof (shaper clobber risk) → Task 1 spike (gates whether the slice is shaper-free). ✓
- Disposition of the two flips → Task 2 Step 1. Retained declines (string concat, inner-`Select` form) → Task 2 Step 5. ✓
- Owned oracle → parity; reference no-oracle → NativeOnly+set → Task 2 Steps 1-2. ✓
- AGENTS.md as-built note + plain-`Select` note wording fix → Task 3. ✓
- 3-version verification + native spec sweep → Task 4. ✓

**Placeholder scan:** No TBD/TODO; every code step shows complete code; the deferred set is explicit and enforced by the single-scope check + the arithmetic-binary top-node gate. ✓

**Type consistency:** `TryTranslateSingleScopeComputedLeaf(Expression, ParameterExpression, IReadOnlyList<MongoUnwindSource>, MongoExpressionTranslator[], ParameterExpression[], out MongoExpression?)`; `ScopeRerootingVisitor.ResolvedScope` (`int?`) / `.CrossScope` (`bool`); `TryResolveScopeDepth(Expression?, ParameterExpression, int, out int)`; `MongoExpressionTranslator.TryTranslateValue(Expression, out MongoExpression?)`; `MongoFieldPrefixRewriter.Rewrite(MongoExpression, string)`; `MongoUnwindSource.InnerScopePath` (`string`) — all consistent with the current codebase and across tasks. ✓
