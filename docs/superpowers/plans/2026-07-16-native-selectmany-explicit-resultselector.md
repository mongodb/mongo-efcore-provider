# Native SelectMany explicit result selector / query syntax (EF-347 slice 4) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the explicit-result-selector + query-syntax owned-collection SelectMany (`owners.SelectMany(o => o.Items, (o,i) => new {o.Name, i.Price})` / `from o … from i in o.Items select …`) translate natively to `$unwind` + `$project`, reusing slice 3's plumbing; every other SelectMany shape keeps hard-failing.

**Architecture:** EF normalizes both forms to a **bare-nav** `SelectMany(o => o.Items.AsQueryable(), (o,i) => TransparentIdentifier(o,i))` + a separate trailing `TranslateSelect(ti => new {ti.Outer.X, ti.Inner.Y})` (spike-confirmed: the trailing selector is nested member-access on the transparent-id param, NOT pre-folded). So: `TranslateSelectMany` accepts the bare-nav owned form (sets `UnwindSource` + a `TransparentIdentifier(Outer, Inner)` shaper, no projection yet); a **state-gated** branch in the trailing `TranslateSelect` binds the projection two-scope (`ti.Outer.<m>` → `$m`, `ti.Inner.<m>` → `$<unwindPath>.m`) and builds the projected shaper. `Route == Projection`; slice 3's `$unwind`/`$project` lowerer/renderer/shaper are reused unchanged.

**Tech Stack:** C# / EF Core provider, xUnit (plain `Assert.*`). Multi-EF via `Debug EF8|EF9|EF10`.

## Global Constraints

- **Purely additive; no regression.** This form hard-fails in every mode today; unsupported shapes keep returning `null` → EF base `NotTranslated`. No graceful driver-LINQ fallback for SelectMany.
- **No parity baseline** — verify against expected in-memory results.
- **Do not regress slice 3's inner-`Select` form** (populates `Projection` in `TranslateSelectMany`; its trailing `ti => ti.Inner` is `IsTransparentIdentifierMemberAccessSelector`) nor SP3/Distinct/GroupBy projections (no `UnwindSource`).
- **Multi-version:** must build+pass on EF8/EF9/EF10 — slice 3 hit an EF8-only CS9174 from a newer `ReplacingExpressionVisitor.Replace` overload; avoid collection-expression target-typing to non-constructible types and newer-only overloads. Run the full 3-version /test-all before declaring done.
- Never materialize a bare owned entity (a member-access projection is always required).
- `<Nullable>enable</Nullable>`; no-BOM sibling convention; new members `internal`/`private`.
- Build configs contain spaces: `-c "Debug EF10"`. Field-ref: `MongoFieldExpression(IProperty, string elementName)` renders `$<elementName>` (dotted path allowed). Owned element path: `navigation.TargetEntityType.GetContainingElementName()`.
- Design: `docs/superpowers/specs/2026-07-16-native-selectmany-explicit-resultselector-design.md`.

## Scope

**In:** owned single-level collection SelectMany, explicit-result-selector + query-syntax forms, trailing projection = terminal `new {…}`/DTO of top-level member accesses of `ti.Outer` / `ti.Inner`. **Out → hard-fail (unchanged):** cross-collection reference nav, correlated/filtered inner, computed/non-member leaves, nested/multi-level, bare owned-entity result. Retires the "explicit-result-selector / query-syntax form" from slice 3's deferred list.

---

### Task 1: `UnwindSource.InnerEntityType` + the two-scope transparent-identifier projection binder (the crux)

A pure, state-driven binder that populates `Projection` from a `ti => new {ti.Outer.X, ti.Inner.Y}` selector given a query whose `Select.UnwindSource` is set. Unit-tested in isolation.

**Files:**
- Modify: `src/…/Query/Expressions/MongoUnwindSource.cs` (add `InnerEntityType`)
- Modify: `src/…/Query/NativeTranslation/NativeSelectManyBinder.cs` (add the transparent-id projection binder; reuse its existing two-scope field-ref logic)
- Modify: `src/…/Query/Visitors/…MongoQueryableMethodTranslatingExpressionVisitor.cs` slice-3 bare-nav bind must now pass `InnerEntityType` when it sets `UnwindSource` — but the bare-nav *acceptance* is Task 2; here just extend the ctor + slice-3 inner-`Select` call site to pass the inner entity type.
- Test: `tests/…/UnitTests/Query/NativeTranslation/NativeSelectManyBinderTests.cs` (add cases)

**Interfaces produced:**
- `MongoUnwindSource(string elementPath, IEntityType innerEntityType)`; `IEntityType InnerEntityType { get; }`.
- `internal static bool NativeSelectManyBinder.TryBindTransparentIdentifierProjection(MongoQueryExpression mongoQ, LambdaExpression selector)` — requires `mongoQ.Select.UnwindSource != null` and `Select.Projection` empty; binds each leaf `ti.Outer.<m>`→`$m` / `ti.Inner.<m>`→`$<path>.m`; populates `Projection`; returns `true`. Returns `false` (select untouched) otherwise.

- [ ] **Step 1: Write failing tests**
Add to `NativeSelectManyBinderTests.cs`. Build the owner/item model (reuse the existing test's model idiom). Construct a query whose `Select.UnwindSource = new MongoUnwindSource("Items", itemEntityType)`, and a selector `ti => new { Name = ti.Outer.Name, Price = ti.Inner.Price }` where `ti` is a single param of a synthesized transparent-identifier-shaped type with `Outer: Owner` / `Inner: Item` members (build the nested `MemberExpression(MemberExpression(ti,"Outer"),"Name")` tree explicitly). Assert:
- `TryBindTransparentIdentifierProjection` returns `true`, `Projection` has `Name → elementName "Name"` and `Price → "Items.Price"`.
- shared member name (`new { OuterName = ti.Outer.Name, InnerName = ti.Inner.Name }`) → `OuterName → "Name"`, `InnerName → "Items.Name"`.
- returns `false` (untouched) for: `UnwindSource == null`; a leaf that is not `ti.Outer.<m>`/`ti.Inner.<m>` (e.g. `ti.Outer` bare, or a computed `ti.Inner.Price * 2`, or a member off neither scope); an entity-valued leaf.

Run `--filter "FullyQualifiedName~NativeSelectManyBinderTests"` — Expected: compile failure.

- [ ] **Step 2: Extend `MongoUnwindSource`**
```csharp
using Microsoft.EntityFrameworkCore.Metadata;
// …
internal sealed class MongoUnwindSource
{
    public MongoUnwindSource(string elementPath, IEntityType innerEntityType)
    {
        ElementPath = elementPath;
        InnerEntityType = innerEntityType;
    }

    public string ElementPath { get; }

    /// <summary>The owned (inner) entity type unwound from <see cref="ElementPath"/> — used to resolve
    /// ti.Inner member accesses to element names in the trailing SelectMany projection (EF-347 slice 4).</summary>
    public IEntityType InnerEntityType { get; }
}
```
Update slice 3's inner-`Select` bind site (`NativeSelectManyBinder.TryBind`, the `mongoQ.Select.UnwindSource = new MongoUnwindSource(unwindPath)` line) to pass `navigation.TargetEntityType` as the second arg. Build EF10 to confirm no other ctor callers break.

- [ ] **Step 3: Implement `TryBindTransparentIdentifierProjection`**
```csharp
// EF-347 slice 4: the explicit-result-selector / query-syntax owned SelectMany. TranslateSelectMany accepted
// the bare-nav form (set UnwindSource, empty Projection) and produced a TransparentIdentifier(Outer, Inner)
// shaper; EF's trailing Select is `ti => new { ti.Outer.X, ti.Inner.Y }` (nested member access on the
// transparent-id param, NOT pre-folded — spike-confirmed). Bind it two-scope: ti.Outer.<m> via the outer
// entity translator ($m); ti.Inner.<m> via the inner (owned) entity translator, unwind-path-prefixed
// ($<path>.m) — the SAME inner-scope logic TryBind uses for the inner-Select form.
internal static bool TryBindTransparentIdentifierProjection(MongoQueryExpression mongoQ, LambdaExpression selector)
{
    if (mongoQ.Select.UnwindSource is not { } unwind || mongoQ.Select.Projection.Count > 0)
        return false;
    if (selector.Parameters.Count != 1)
        return false;
    var ti = selector.Parameters[0];

    if (!TryReadProjection(selector.Body, out var members))   // reuse slice 3's NewExpression/MemberInit reader
        return false;

    var outerTranslator = new MongoExpressionTranslator(mongoQ.CollectionExpression.EntityType);
    var innerTranslator = new MongoExpressionTranslator(unwind.InnerEntityType);
    var projections = new List<MongoProjection>();
    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    foreach (var (alias, argExpr) in members)
    {
        // Leaf must be ti.<scope>.<member> — a member access whose target is ti.Outer or ti.Inner.
        if (argExpr is not MemberExpression { Expression: MemberExpression scopeAccess } member
            || scopeAccess.Expression != ti
            || scopeAccess.Member.Name is not ("Outer" or "Inner"))
            return false;

        MongoFieldExpression field;
        if (scopeAccess.Member.Name == "Outer")
        {
            if (!outerTranslator.TryTranslateField(member, out var f)) return false;
            field = f;
        }
        else
        {
            if (!innerTranslator.TryTranslateField(member, out var innerF)) return false;
            field = new MongoFieldExpression(innerF.Property, unwind.ElementPath + "." + innerF.ElementName);
        }

        if (!seen.Add(alias)) return false;
        projections.Add(new MongoProjection(alias, field));
    }

    foreach (var p in projections) mongoQ.Select.AddProjection(p);
    return true;
}
```
> Note: `TryTranslateField` resolves a `MemberExpression` against the translator's entity type by member name (it does not care about the `ti.Outer`/`ti.Inner` root — confirm by reading how the inner-`Select` binder already calls it). If it inspects the member's `Expression`, adapt by re-rooting the member onto a synthetic parameter of the scope's entity type before calling. Confirm in Step 1's test that the real `ti.Outer.Name` tree resolves; if not, re-root and note it.

- [ ] **Step 4: Run tests + commit**
Focused filter — Expected: PASS. `git commit -am "EF-347: UnwindSource.InnerEntityType + two-scope transparent-id SelectMany projection binder"`.

---

### Task 2: Wire it end-to-end — bare-nav `TranslateSelectMany` + pending-SelectMany `TranslateSelect`

**Files:**
- Modify: `src/…/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs`
- Test: `tests/…/FunctionalTests/Query/NativeSelectManyTests.cs` (add explicit-form cases)

- [ ] **Step 1: Write failing functional tests**
Add to `NativeSelectManyTests` (reuse the OwnsMany fixture). Cover:
- explicit result selector `q.SelectMany(o => o.Items, (o,i) => new { o.Name, i.Price })` and query syntax `from o in q from i in o.Items select new { o.Name, i.Price }` — BOTH succeed under `NativeOnly`, correct results (== expected in-memory), MQL `{$unwind:"$Items"}` + `$project` `$Name`/`$Items.Price`.
- shared outer/inner member name → correct distinct values.
- empty/absent owned collection → no rows.
- DTO/MemberInit result selector.
- hard-fail (assert still throws): a computed leaf (`(o,i) => new { X = i.Price * 2 }`), a reference-nav SelectMany, `…SelectMany(…, (o,i) => new{…}).Where(…)` (non-terminal).

Run after build — Expected: native-success cases fail (this form hard-fails today).

- [ ] **Step 2: `TranslateSelectMany` — accept the bare-nav owned form**
In `TranslateSelectMany`, before the current `NativeSelectManyBinder.TryBind` (inner-`Select`) call, add the bare-nav branch:
```csharp
// EF-347 slice 4: the explicit-result-selector / query-syntax form arrives as a BARE owned nav collection
// selector (o => o.Items.AsQueryable(), no nested Select) + a trivial TransparentIdentifier(Outer,Inner)
// resultSelector; the real projection is the SEPARATE trailing Select. Set UnwindSource here and hand EF the
// TransparentIdentifier(Outer, Inner) shape it expects; the trailing TranslateSelect binds the projection.
if (NativeSelectManyBinder.TryBindBareNavUnwind(mongoQueryExpression, collectionSelector))
{
    var itemShaper = /* a StructuralTypeShaperExpression for UnwindSource.InnerEntityType — build it the way
                        EF builds an entity shaper for a nav target; study CreateShapedQueryExpression /
                        how the inner shaper was formed in the slice-4 spike stub */;
    var wrapped = ReplacingExpressionVisitor.Replace(
        resultSelector.Parameters[0], source.ShaperExpression,
        ReplacingExpressionVisitor.Replace(resultSelector.Parameters[1], itemShaper, resultSelector.Body));
    return source.UpdateShaperExpression(wrapped);
}
// … existing inner-Select branch (TryBind) unchanged …
```
Add `NativeSelectManyBinder.TryBindBareNavUnwind(mongoQ, collectionSelector)`: unwrap `AsQueryable`, require a bare `MemberExpression` owned collection nav (NOT a nested `Select` — that is the inner-`Select` form), set `mongoQ.Select.UnwindSource = new MongoUnwindSource(path, navigation.TargetEntityType)`; return `true`; else `false`.
> The item shaper construction is the one genuinely fiddly bit — build it exactly as the slice-4 de-risk spike's throwaway stub did (`.superpowers/sdd/explicit-selectmany-spike.md` records that `source.ShaperExpression` must be `TransparentIdentifier(Outer=owner shaper, Inner=item StructuralTypeShaperExpression)`). Reuse `EntityShaperExpression`/`StructuralTypeShaperExpression` construction already used elsewhere in this visitor for nav targets; do not hand-roll.

- [ ] **Step 3: `TranslateSelect` — pending-SelectMany projection branch (before the post-terminal guard)**
The trailing `ti => new {ti.Outer.X, ti.Inner.Y}` selector currently reaches the `else if` at (current) line 221 and, because `UnwindSource` makes `HasTerminalOperator` true, hits `MarkNotNativelyRepresentable()` (line ~238–240). Intercept it first. Add, near the top of `TranslateSelect`'s projection handling (before the line-207 Include guard / line-221 else-if):
```csharp
// EF-347 slice 4: the trailing projection of an explicit-result-selector / query-syntax owned SelectMany.
// UnwindSource is set with no Projection yet; bind ti.Outer/ti.Inner two-scope, build the projected shaper
// (by-alias, like the inner-Select form / GroupBy), and skip the generic fold below.
if (mongoQueryExpression.Select.UnwindSource != null
    && mongoQueryExpression.Select.Projection.Count == 0
    && NativeSelectManyBinder.TryBindTransparentIdentifierProjection(mongoQueryExpression, selector))
{
    var shaper = BuildSelectManyResultShaper(mongoQueryExpression, selector.Body);   // reuse slice 3's helper
    return source.UpdateShaperExpression(shaper);
}
```
`BuildSelectManyResultShaper` (slice 3) rewrites a `new{…}`/`MemberInit` body onto `ProjectionBindingExpression`s by alias via `BindSelectManyMember` — it works over any member-access argument, so it handles the `ti.Outer.X`/`ti.Inner.Y` leaves as-is (the alias is the projected member name, matching what `TryBindTransparentIdentifierProjection` registered). Confirm the aliases line up (member name on both sides).
> If `BuildSelectManyResultShaper`/`BindSelectManyMember` need the *raw* leaf expression's type only (they do — `AddToProjection(valueExpression, alias)` uses it for dedup/CLR type), the `ti.Outer.X` leaf's `.Type` is the correct CLR type, so no change needed. Verify.

- [ ] **Step 4: Run tests + regression + commit**
Build EF10, run `--filter "FullyQualifiedName~NativeSelectMany|FullyQualifiedName~QueryModeGate"` — Expected: PASS (both explicit forms native + correct; slice 3 inner-`Select` tests still green; hard-fail cases throw). `git commit -am "EF-347: Native explicit-result-selector / query-syntax owned SelectMany ($unwind + $project)"`.

---

### Task 3: Composition-seam + coverage tests + AGENTS.md + full verification

- [ ] **Step 1: Seam + coverage tests** — add to `NativeSelectManyTests`: each operator after the explicit-form SelectMany hard-fails as expected (Where/OrderBy/Skip/Take/GroupBy/another SelectMany throw; `Count` — per slice 3 — gracefully falls back to a correct count, throws only under NativeOnly, assert the exact count); a parametrized-outer-predicate end-to-end test. Run — Expected: PASS.
- [ ] **Step 2: AGENTS.md** — extend the SelectMany as-built note: the explicit-result-selector / query-syntax owned form is now native (bare-nav `TranslateSelectMany` sets `UnwindSource` + TransparentId shaper; trailing `TranslateSelect` two-scope-binds `ti.Outer`/`ti.Inner` via the pending-`UnwindSource`-empty-`Projection` state gate). Remove it from the deferred list. Document the new `TranslateSelect` pending-SelectMany branch in the post-terminal-invariant family (it runs BEFORE the post-terminal guard, gated on `UnwindSource != null && Projection empty`). Commit.
- [ ] **Step 3: Full verification (controller-run; no commit)** — full 3-version `/test-all` green (watch EF8/EF9 API hazards); `MONGODB_EF_NATIVE_ONLY=1` spec sweep vs `baac509` (this form may move some Northwind SelectMany spec tests native; confirm zero regressions). Report counts.

---

## Self-Review

**1. Spec coverage:** IR + two-scope transparent-id binder, the crux (Task 1); bare-nav `TranslateSelectMany` accept + pending `TranslateSelect` branch + shaper, end-to-end (Task 2); seam/coverage + docs + full verification (Task 3). Both forms + hard-fail-preserved + slice-3-not-regressed covered. Hazards: shared-binder scoping (state gate, Task 1/2 + review), slice-3-inner-Select-not-regressed (Projection-non-empty discriminator, Task 2 tests), owned-vs-reference (Task 2 reject test), two-scope collision (Task 1 shared-member test), `$unwind` empty (Task 3). ✓

**2. Placeholder scan:** the two `>` implementer notes (item-shaper construction; `TryTranslateField` re-rooting) are genuine confirm-against-code items with named locations (the spike stub; the inner-`Select` binder), not vague requirements. All other code steps show complete code. ✓

**3. Type consistency:** `MongoUnwindSource(string, IEntityType)` + `InnerEntityType`; `TryBindTransparentIdentifierProjection(MongoQueryExpression, LambdaExpression)`; `TryBindBareNavUnwind(MongoQueryExpression, LambdaExpression)`; reuse of `TryReadProjection`/`BuildSelectManyResultShaper`/`BindSelectManyMember` (slice 3) and `MongoFieldExpression(IProperty, string)`. `TranslateSelect` calls the binder then `BuildSelectManyResultShaper`. ✓
