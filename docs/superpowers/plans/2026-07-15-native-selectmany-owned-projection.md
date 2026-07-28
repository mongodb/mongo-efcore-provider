# Native SelectMany over an owned collection → projection (EF-347 slice 3) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Translate the **inner-`Select` form** of owned/embedded-collection SelectMany — `SelectMany(o => o.Items.Select(i => new {…}))` — to a native `$unwind` + `$project` pipeline; every other SelectMany shape keeps hard-failing (unchanged).

**Architecture:** `TranslateSelectMany` recognizes an owned-collection `SelectMany` whose collection selector nests a member-access `Select` (`o => o.Items.AsQueryable().Select(i => new {o.X, i.Y}))`), parses that nested projection with a **two-scope** binder (outer closed-over param → `$field`, inner `Select` param → `$<unwindPath>.field`), sets `UnwindSource` + the existing `Projection` slot, and returns a projected shaped query (`Route == Projection`, existing DOM projection shaper). Purely additive: SelectMany throws in all modes today, so no parity baseline and no graceful fallback — unsupported shapes return `null` (base → `NotTranslated`, as today).

**Tech Stack:** C# / EF Core provider, xUnit (plain `Assert.*`). Multi-EF via `Debug EF8|EF9|EF10`.

## Post-probe revision (2026-07-15) — READ THIS

Task 1's probe (see `.superpowers/sdd/task-1-report.md`) established that EF normalizes **both** SelectMany forms to the `SelectManyWithCollectionSelector` overload with a **trivial `TransparentIdentifier(Outer, Inner)`** result selector:
- **Explicit-result-selector / query-syntax form** (`SelectMany(o => o.Items, (o,i) => proj)`, `from o … from i in o.Items select …`): the user projection is a **separate subsequent `.Select(ti => …)`** over a transparent identifier — NOT visible in `TranslateSelectMany`. **Deferred** (returns `null` → hard-fails, unchanged) — a later slice; it needs transparent-identifier handling + a shared `NativeProjectionBinder` change.
- **Inner-`Select` form** (`SelectMany(o => o.Items.Select(i => proj))`): the projection is **nested inside the collection selector** as `Property(o,Nav).AsQueryable().Select(inner => proj)`; `resultSelector` is the trivial transparent identifier and there is no subsequent Select. **This slice handles exactly this form** — self-contained in `TranslateSelectMany`, no transparent-identifier/shared-binder machinery.

Confirmed facts the tasks rely on: `collectionSelector.Body` is wrapped in `Queryable.AsQueryable()` (must unwrap); the owned collection's `$unwind` element path is `navigation.TargetEntityType.GetContainingElementName()` (a `MongoEntityTypeExtensions` method, not on `INavigation`); `SelectManyWithCollectionSelector` is already in the `VisitMethodCall` switch bubble-through group (→ `base.VisitMethodCall` → `TranslateSelectMany`).

## Global Constraints

- **Purely additive; no regression.** Unsupported SelectMany shapes (incl. the explicit-result-selector form) keep throwing exactly as today (`TranslateSelectMany` → `null` → EF base `NotTranslated`). No graceful driver-LINQ fallback for SelectMany (the driver can't translate it either).
- **No parity baseline** — verify against **expected in-memory results**, not `Native == DriverLinq`.
- **Never materialize a bare owned entity** — only member-access projections (anonymous/DTO). An entity-valued projection leaf must reject.
- `<Nullable>enable</Nullable>`; no-BOM sibling convention; new members `internal`/`private`.
- Build configs contain spaces: `-c "Debug EF10"`. Tests run serially.
- Field-ref rendering: `MongoFieldExpression(IProperty property, string elementName)` renders `$<elementName>`; a nested path is just an `elementName` with a dot (`"Items.Price"`).
- Namespaces: `MongoSelectDefinition`/`MongoUnwindSource`/`MongoFieldExpression`/`MongoProjection` ∈ `…Query.Expressions`; binders/translator ∈ `…Query.NativeTranslation`; QMTEV ∈ `…Query.Visitors`.
- Design: `docs/superpowers/specs/2026-07-15-native-selectmany-owned-projection-design.md` (see its post-probe revision note).

## Scope

**In:** owned (embedded) single-level collection SelectMany, **inner-`Select` form only** —
`q.SelectMany(o => o.Items.Select(i => new {o.X, i.Y}))` where the nested `Select`'s body is a `new{…}`/DTO
of top-level member accesses of the outer (`o`, closed over) and inner (`i`, the `Select` param) only.
Emits `<outer $match/$sort/$skip/$limit>, {$unwind:"$Items"}, {$project:{X:"$X", Y:"$Items.Y"}}`.
**Out → hard-fail (`null`, unchanged):** explicit-result-selector / query-syntax form (deferred — needs
transparent-identifier handling); bare owned-entity result; reference (non-owned) nav; correlated/filtered
inner (`Where` on the inner); computed/non-member-access leaves; non-terminal (operator after); nested/
multi-level.

---

### Task 1: Probe + IR + `$unwind` stage + lowerer/renderer plumbing — DONE (commit 23f7428)

Delivered `MongoUnwindSource` IR, `MongoSelectDefinition.UnwindSource` + `HasTerminalOperator` extension,
`MongoUnwindFieldStage`, the lowerer unwind→project terminal branch, and the factory `$unwind` render — all
unit-tested (51/51 focused, 464 full unit) and reviewed clean. The plumbing is independent of the binding
approach below; no rework needed. (The probe findings are folded into the revision note above.)

---

### Task 2: `NativeSelectManyBinder` — inner-`Select` recognition + two-scope projection binding (the crux)

Recognize the inner-`Select` owned-collection form and populate `UnwindSource` + `Projection`. Pure binder
logic, unit-tested against constructed expressions.

**Files:**
- Create: `src/…/Query/NativeTranslation/NativeSelectManyBinder.cs`
- Test: `tests/…/UnitTests/Query/NativeTranslation/NativeSelectManyBinderTests.cs`

**Interfaces produced:** `internal static bool NativeSelectManyBinder.TryBind(MongoQueryExpression mongoQ, LambdaExpression collectionSelector)` — on success sets `mongoQ.Select.UnwindSource` + populates `mongoQ.Select.Projection`, returns `true`; else leaves the select untouched, returns `false`.

**Consumed:** `MongoExpressionTranslator(IEntityType)` + `TryTranslateField(Expression, out MongoFieldExpression)`; `MongoFieldExpression(IProperty, string)`; `MongoProjection(string, MongoExpression)`; `ExpressionExtensions.UnwrapLambdaFromQuote()` (used elsewhere in this namespace).

- [ ] **Step 1: Write failing binder tests**
Create `NativeSelectManyBinderTests.cs`. Build a model (`Owner` with `OwnsMany(o => o.Items)`, `Item` with scalars `Name`/`Price`) — reuse the model-building idiom other NativeTranslation unit tests use to get an `IEntityType` (READ `NativeGroupByBinderTests`/`MongoExpressionTranslatorTests` first). Construct `collectionSelector = (Owner o) => o.Items.AsQueryable().Select(i => new { o.Name, Price = i.Price })` (build the expression tree explicitly, matching the probe's shape: a `Queryable.Select` over `Queryable.AsQueryable(o.Items)`). Assert:
- `TryBind` returns `true`, `Select.UnwindSource.ElementPath == "Items"`, `Select.Projection` has `Name → elementName "Name"` and `Price → elementName "Items.Price"`.
- **Shared-member-name:** `Select(i => new { OuterName = o.Name, InnerName = i.Name })` → `OuterName → "Name"`, `InnerName → "Items.Name"`.
- Returns `false` (select untouched) for: a reference (non-owned) collection nav; an entity-valued leaf (`i => new { X = i }` or `i => i`); a computed leaf (`i => new { X = i.Price * 2 }`); a collection selector whose body is NOT a nested `Select` (the explicit-result-selector form — body is just `o.Items.AsQueryable()`); an inner `Where` before the `Select`.

Run `--filter "FullyQualifiedName~NativeSelectManyBinderTests"` — Expected: compile failure.

- [ ] **Step 2: Implement `NativeSelectManyBinder`**
```csharp
/* <license header, no BOM> */
using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Query;   // QueryableMethods / Queryable
using MongoDB.EntityFrameworkCore.Extensions;   // UnwrapLambdaFromQuote, GetContainingElementName
using MongoDB.EntityFrameworkCore.Query.Expressions;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>
/// Binds the INNER-Select form of an owned-collection SelectMany (EF-347 slice 3) —
/// <c>o => o.Items.AsQueryable().Select(i => new { o.X, i.Y })</c> — to a native <c>$unwind</c> +
/// <c>$project</c>: sets <see cref="MongoSelectDefinition.UnwindSource"/> and populates
/// <see cref="MongoSelectDefinition.Projection"/>. Outer (closed-over) members resolve to root field refs
/// (<c>$X</c>); inner (Select-parameter) members resolve to the unwound element, prefixed with the unwind
/// path (<c>$Items.Y</c>). Returns false (select untouched) for any other shape — the caller returns null
/// and EF hard-fails translation, as before. The explicit-result-selector form (body is a bare
/// <c>Property(o,Nav).AsQueryable()</c>, projection in a separate subsequent Select) is out of scope and
/// rejected here (body is not a nested Select).
/// </summary>
internal static class NativeSelectManyBinder
{
    internal static bool TryBind(MongoQueryExpression mongoQ, LambdaExpression collectionSelector)
    {
        var outerParam = collectionSelector.Parameters[0];

        // Body must be Queryable.Select(<source>, innerLambda).
        if (collectionSelector.Body is not MethodCallExpression
            {
                Method: { Name: nameof(System.Linq.Queryable.Select), DeclaringType: var selDecl },
                Arguments: [var selectSource, var innerLambdaArg]
            }
            || selDecl != typeof(System.Linq.Queryable))
            return false;

        // <source> must be Queryable.AsQueryable(o.Nav) — unwrap AsQueryable to the nav member.
        var navExpr = UnwrapAsQueryable(selectSource);
        if (navExpr is not MemberExpression { Expression: ParameterExpression navRoot } navMember
            || !ReferenceEquals(navRoot, outerParam))
            return false;

        var outerEntityType = mongoQ.CollectionExpression.EntityType;
        var navigation = outerEntityType.FindNavigation(navMember.Member.Name);
        if (navigation is not { IsCollection: true } || !navigation.TargetEntityType.IsOwned())
            return false;
        var unwindPath = navigation.TargetEntityType.GetContainingElementName();

        var innerLambda = innerLambdaArg.UnwrapLambdaFromQuote();
        if (innerLambda.Parameters.Count != 1)
            return false;
        var innerParam = innerLambda.Parameters[0];

        if (!TryReadProjection(innerLambda.Body, out var members))
            return false;

        var outerTranslator = new MongoExpressionTranslator(outerEntityType);
        var innerTranslator = new MongoExpressionTranslator(navigation.TargetEntityType);
        var projections = new List<MongoProjection>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (alias, argExpr) in members)
        {
            if (argExpr is not MemberExpression member || member.Expression is not ParameterExpression root)
                return false;

            MongoFieldExpression field;
            if (ReferenceEquals(root, outerParam))
            {
                if (!outerTranslator.TryTranslateField(member, out var f)) return false;
                field = f;
            }
            else if (ReferenceEquals(root, innerParam))
            {
                if (!innerTranslator.TryTranslateField(member, out var innerF)) return false;
                field = new MongoFieldExpression(innerF.Property, unwindPath + "." + innerF.ElementName);
            }
            else
            {
                return false;
            }

            if (!seen.Add(alias)) return false;
            projections.Add(new MongoProjection(alias, field));
        }

        mongoQ.Select.UnwindSource = new MongoUnwindSource(unwindPath);
        foreach (var p in projections)
            mongoQ.Select.AddProjection(p);
        return true;
    }

    private static Expression UnwrapAsQueryable(Expression e)
        => e is MethodCallExpression { Method.Name: nameof(System.Linq.Queryable.AsQueryable), Arguments: [var inner] }
            ? inner : e;

    // new {...} (NewExpression with Members) or a parameterless MemberInit — mirrors NativeProjectionBinder.
    private static bool TryReadProjection(Expression body, out IReadOnlyList<(string Alias, Expression Arg)> members)
    {
        members = null!;
        var list = new List<(string, Expression)>();
        switch (body)
        {
            case NewExpression ne when ne.Members != null && ne.Members.Count == ne.Arguments.Count && ne.Arguments.Count > 0:
                for (var i = 0; i < ne.Arguments.Count; i++) list.Add((ne.Members[i].Name, ne.Arguments[i]));
                break;
            case MemberInitExpression mi when mi.NewExpression.Arguments.Count == 0 && mi.Bindings.Count > 0:
                foreach (var b in mi.Bindings)
                {
                    if (b is not MemberAssignment ma) return false;
                    list.Add((b.Member.Name, ma.Expression));
                }
                break;
            default:
                return false;
        }
        members = list;
        return true;
    }
}
```
> Notes: (a) confirm `UnwrapLambdaFromQuote` is the existing helper name/namespace (grep — used in `NativeProjectionBinder`); if the inner lambda arrives already-unquoted, `UnwrapLambdaFromQuote` should be a no-op or cast — adapt. (b) `TryTranslateField` rejecting `_id`/composite/represented props is the desired reject behavior. (c) if the probe test in Step 1 shows a slightly different tree (e.g. an extra `Convert`), add the minimal unwrap and note it.

- [ ] **Step 3: Run binder tests + commit**
Focused filter — Expected: PASS (incl. shared-member-name + all rejections). Commit `EF-347: NativeSelectManyBinder — inner-Select owned-collection two-scope projection binding`.

---

### Task 3: QMTEV wiring — `TranslateSelectMany` → native; functional tests

**Files:**
- Modify: `src/…/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs`
- Modify: `src/…/Query/NativeTranslation/NativeSlotPopulator.cs` (whitelist)
- Test: `tests/…/FunctionalTests/Query/NativeSelectManyTests.cs` (new)

- [ ] **Step 1: Write failing functional tests**
New `NativeSelectManyTests` with an `OwnsMany` fixture (`Owner`→`Items`). Cover the inner-`Select` form:
- `q.SelectMany(o => o.Items.Select(i => new { o.Name, i.Price }))` succeeds under `NativeOnly`; results == expected in-memory; MQL contains `{$unwind:"$Items"}` then `$project` with `$Name` and `$Items.Price`.
- empty/absent owned collection → those owners contribute no rows (inner-join semantics).
- shared outer/inner member name → correct distinct values.
- **hard-fail (assert still throws — no regression):** the explicit-result-selector form `q.SelectMany(o => o.Items, (o,i) => new { o.Name, i.Price })`; bare `q.SelectMany(o => o.Items)`; a computed leaf; `q.SelectMany(o => o.Items.Select(...)).Where(...)` (non-terminal).

Run after build — Expected: the native-success cases fail (SelectMany hard-fails today).

- [ ] **Step 2: Implement `TranslateSelectMany`**
Replace the collection-selector overload body (currently `=> null`):
```csharp
protected override ShapedQueryExpression? TranslateSelectMany(
    ShapedQueryExpression source, LambdaExpression collectionSelector, LambdaExpression resultSelector)
{
    // Slice 3: only the INNER-Select owned-collection form (projection nested in the collection selector).
    // The explicit-result-selector form's projection is a separate subsequent Select carrying a trivial
    // TransparentIdentifier here — not handled → null → EF base NotTranslated (hard-fail, unchanged).
    var mongoQ = (MongoQueryExpression)source.QueryExpression;
    if (!NativeSelectManyBinder.TryBind(mongoQ, collectionSelector))
        return null;

    return BuildProjectedSelectManyShapedQuery(source, mongoQ, resultSelector);
}
```
Implement `BuildProjectedSelectManyShapedQuery` to return a projected `ShapedQueryExpression` (`Route == Projection`) whose shaper is the existing DOM projection shaper reading `Select.Projection` aliases — mirror how `TranslateSelect`'s native-projection path (and `TranslateGroupBy`) construct a projected shaped query. **Determine the result element type and shaper wiring from the actual expression** (the probe found the inner-`Select` form's `resultSelector` is `(o,c) => TransparentIdentifier(Outer=o, Inner=c)` with `c` the nested projection's anonymous type, and no subsequent Select — so the result element the shaper must produce is that anonymous projection `c`; confirm this against a run and reuse the existing projection-shaper construction rather than inventing one). Validate via the Step-1 functional tests (they assert the actual returned rows).

- [ ] **Step 3: Whitelist**
`SelectManyWithCollectionSelector` is already in the `VisitMethodCall` switch bubble group (→ base → the override). But `PopulateNativeSlots` runs AFTER the switch for the native (non-null) result and its catch-all would clobber the decision, so add to `NativeSlotPopulator.IsNativeRepresentableSlotOperator`:
```csharp
   || methodDefinition == QueryableMethods.SelectManyWithCollectionSelector
```
(Unsupported shapes returned `null` → the switch body returns `NotTranslated` before `PopulateNativeSlots`, so they are unaffected.)

- [ ] **Step 4: Run tests + commit**
Build EF10, run `--filter "FullyQualifiedName~NativeSelectMany|FullyQualifiedName~QueryModeGate"` — Expected: PASS (native success + correct results; hard-fail cases still throw). Commit `EF-347: Native inner-Select owned-collection SelectMany translation ($unwind + $project)`.

---

### Task 4: Composition-seam hard-fail tests + AGENTS.md + full verification

- [ ] **Step 1: Composition-seam tests**
Add to `NativeSelectManyTests`: each operator AFTER the SelectMany still hard-fails (throws) — `Where`/`OrderBy`/`Skip`/`Take`/`Count`/`GroupBy`/another `SelectMany`. Plus a parametrized-outer-predicate end-to-end test (`q.Where(o => o.K == p).SelectMany(o => o.Items.Select(i => new {o.Name, i.Price}))`) asserting correct results. Run — Expected: PASS.

- [ ] **Step 2: AGENTS.md**
Add an as-built paragraph: native **inner-`Select`** owned-collection SelectMany → `$unwind` + `$project` via `MongoUnwindSource` + `MongoUnwindFieldStage` + the two-scope `NativeSelectManyBinder`; `Route` stays `Projection`; `UnwindSource` joins `HasTerminalOperator`. State it is **additive** (SelectMany hard-failed in all modes; unsupported shapes — incl. the explicit-result-selector / query-syntax form — still hard-fail, NOT a graceful fallback). List deferred shapes. Add `SelectManyWithCollectionSelector` to the `IsNativeRepresentableSlotOperator` pitfall list. Commit.

- [ ] **Step 3: Full verification (controller-run; no commit)**
Full 3-version `/test-all` green; `MONGODB_EF_NATIVE_ONLY=1` spec sweep vs `750268a` (coverage delta, zero regressions). Report counts.

---

## Self-Review

**1. Spec coverage:** IR/plumbing (Task 1, done); inner-`Select` two-scope binder, the crux (Task 2); `TranslateSelectMany` wiring + whitelist + native end-to-end + hard-fail-preserved (Task 3); seam hard-fails + docs + full verification (Task 4). Explicit-result-selector form explicitly deferred (rejected by the binder's nested-`Select` requirement → hard-fail). Additive/no-baseline verification throughout. Hazards: post-SelectMany composition (Task 4), owned-vs-reference (Task 2 rejections), `$unwind` empty semantics (Task 3 empty-collection test), two-scope collision (Task 2 shared-member-name test). ✓

**2. Placeholder scan:** the two implementer-notes (unwrap-helper name; result-element-type/shaper wiring for the TransparentId form) are genuine confirm-against-code/probe items with named locations, not vague requirements. Production code steps show complete code. ✓

**3. Type consistency:** `NativeSelectManyBinder.TryBind(MongoQueryExpression, LambdaExpression)` (single collectionSelector arg — the revision); `MongoUnwindSource`/`UnwindSource`/`MongoUnwindFieldStage` from Task 1; `MongoFieldExpression(IProperty, string)`; `MongoProjection(string, MongoExpression)`. `TranslateSelectMany` calls `TryBind(mongoQ, collectionSelector)` and ignores the trivial `resultSelector`. ✓
