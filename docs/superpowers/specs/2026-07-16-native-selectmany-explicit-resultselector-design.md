# Native query: SelectMany over an owned collection — explicit result selector / query syntax (EF-347 slice 4)

**Ticket:** EF-347 (remaining SP6 relational operators) — SelectMany slice 2 (explicit-result-selector / query-syntax form). Epic EF-322.
(Slice 3 shipped the inner-`Select` owned form + all the `$unwind`/`$project` plumbing.)
**Type:** New native coverage, **purely additive**. This form hard-fails in every mode today (slice 3 deferred it); this makes it succeed natively. Nothing that works today changes.
**Stacked on:** EF-347 slice 3 SelectMany (`baac509`), the current native-stack tip.

## Background

Slice 3 made the **inner-`Select`** owned-collection SelectMany native (`o.SelectMany(o => o.Items.Select(i => new {o.X, i.Y}))`) and deferred the **explicit-result-selector / query-syntax** form. This slice does that deferred form:
- `owners.SelectMany(o => o.Items, (o,i) => new {o.Name, i.Price})`, and
- `from o in owners from i in o.Items select new {o.Name, i.Price}`.

Both are equivalent and, per slice 3's probe + this slice's de-risk spike (`.superpowers/sdd/explicit-selectmany-spike.md`), EF normalizes **both byte-identically** to:
1. `TranslateSelectMany(collectionSelector = o => o.Items.AsQueryable(), resultSelector = (o,i) => TransparentIdentifier(Outer=o, Inner=i))` — a **bare** owned nav (no nested `Select`) + a trivial transparent-identifier result selector, then
2. a separate `TranslateSelect(selector = ti => new {Name = ti.Outer.Name, Price = ti.Inner.Price})`.

Slice 3's `NativeSelectManyBinder.TryBind` **rejects** the bare-nav collection selector (it requires a nested `Select`), so this form currently returns `null` → EF base `NotTranslated` → hard-fails. This slice makes `TranslateSelectMany` accept the bare-nav form and makes the **trailing `TranslateSelect`** bind the real projection.

### Spike findings (they fix the approach)

- In the trailing `TranslateSelect`, `selector` is `ti => new {…ti.Outer.Name…, …ti.Inner.Price…}` with **`ti` a single `TransparentIdentifier`-typed lambda parameter**, and each leaf is a **nested member access on the param**: `MemberExpression(MemberExpression(ti, "Outer"), "Name")`. It is **not** pre-folded — EF substitutes the source shaper only later, inside `TranslateSelect`'s own `Replace(param, source.ShaperExpression, body)`. So the projection binder runs on the raw `ti.Outer.X` / `ti.Inner.Y` form.
- `source.ShaperExpression` (what `TranslateSelectMany` must produce) is `new TransparentIdentifier(Outer = <owner StructuralTypeShaperExpression>, Inner = <item StructuralTypeShaperExpression>)`.
- The explicit-result-selector and query-syntax forms are byte-identical.

## Scope

**In:** owned (embedded) single-level collection SelectMany, **explicit-result-selector + query-syntax forms**, where the trailing projection is a terminal `new {…}` / DTO of **top-level member accesses of `ti.Outer` and/or `ti.Inner`**. Emits (identical to slice 3):
```
<outer $match/$sort/$skip/$limit>, { $unwind: "$Items" }, { $project: { _id: 0, Name: "$Name", Price: "$Items.Price" } }
```
**Out → hard-fail (`null`, unchanged):** cross-collection reference nav; correlated/filtered inner; computed/non-member-access leaves; a projection referencing neither `ti.Outer` nor `ti.Inner` cleanly; nested/multi-level SelectMany. (Bare owned-entity results remain out — a projection is always required.)

This slice **retires "explicit-result-selector / query-syntax form" from slice 3's deferred list.**

## Design (Approach A — state-gated two-scope binding in the trailing `TranslateSelect`)

### IR

- Extend `MongoUnwindSource` (slice 3, currently just `ElementPath`) to also carry the **inner (owned) entity type** — `IEntityType InnerEntityType` (or the `INavigation`) — so the trailing projection binder can build the inner-scope translator. `ElementPath` stays the `$unwind` path.
- No new `Route` value: the trailing `TranslateSelect` populates the existing `Projection` slot → `Route` resolves to `Projection`, reusing slice 3's lowerer/renderer/shaper.

### `TranslateSelectMany` — accept the bare-nav owned form

Add a branch (alongside slice 3's inner-`Select` binding):
1. If `collectionSelector.Body` (after unwrapping `AsQueryable()`) is a **bare** `MemberExpression` on the outer param resolving to an **owned collection nav** (`IsCollection && TargetEntityType.IsOwned()`), and `resultSelector` is the trivial `(o,i) => TransparentIdentifier(Outer=o, Inner=i)` — set `mongoQ.Select.UnwindSource = new MongoUnwindSource(elementPath, innerEntityType)` and do **not** populate `Projection` (the projection is in the trailing Select).
2. Build the source shaper as `TransparentIdentifier(Outer = source.ShaperExpression, Inner = <item StructuralTypeShaperExpression>)` (mirroring the shape the spike observed; reuse slice 3's `BuildSelectManyWrappedShaper` construction, with `Inner` = the raw item entity shaper rather than a projected shaper).
3. Return that shaped query. (slice 3's inner-`Select` branch is unchanged; the two are distinguished by whether `collectionSelector.Body` is a nested `Select` vs a bare nav.)

### `NativeProjectionBinder` — the SelectMany two-scope branch (the shared change, state-gated)

When `TranslateSelect` runs its native-projection binding (`TryPopulateNativeProjection`) and the source `Select` has **`UnwindSource != null` with an empty `Projection`** (the pending-explicit-SelectMany state — distinct from slice 3's inner-`Select` form, which already populated `Projection` and whose trailing `ti => ti.Inner` is handled by the existing transparent-identifier-member-access shaper fold, not this path), bind each projection leaf as a **nested transparent-identifier member access**:
- Leaf shape `MemberExpression(MemberExpression(ti, scope), member)` where `ti` is the selector's single param.
- `scope == "Outer"` → resolve `member` via the **outer** entity translator (`MongoExpressionTranslator(CollectionExpression.EntityType)`) → field ref `$member`.
- `scope == "Inner"` → resolve `member` via the **inner** entity translator (`MongoExpressionTranslator(UnwindSource.InnerEntityType)`), then re-wrap with the unwind path prefixed: `new MongoFieldExpression(innerField.Property, UnwindSource.ElementPath + "." + innerField.ElementName)` — **exactly slice 3's `NativeSelectManyBinder` inner-scope logic.**
- Any other leaf shape (not a `ti.Outer.<m>` / `ti.Inner.<m>` nested member access) → return `false` → the projection is not native → hard-fail (unchanged).

**Scoping (the correctness-critical part).** This branch fires **only** when the source has the pending `UnwindSource`-with-empty-`Projection` state. A normal SP3 projection (`p => new {p.X}`, param is the entity), a projected `Distinct`, or a GroupBy projection has no `UnwindSource`, so the branch is never entered — they take the existing single-scope path unchanged. Within the branch, a leaf that isn't the `ti.Outer`/`ti.Inner` nested shape rejects (hard-fail), so the branch can't silently mis-bind a non-SelectMany shape. This is the same state-gated discipline as slice 3's `MongoProjectionBindingExpressionVisitor` `Route == Projection` gate and set-ops' `IsSetOp` guard.

### `TranslateSelect` — build the projected shaper

For the pending-SelectMany case, after the binder populates `Projection`, build the result shaper by rewriting each `ti.Outer.<m>` / `ti.Inner.<m>` leaf onto a `ProjectionBindingExpression` reading its registered alias (mirroring slice 3's `BuildSelectManyResultShaper` / `BindSelectManyMember`, and GroupBy's `BindGroupMember`) — the existing DOM projection shaper then reads each alias by name. No new shaper.

### Lowerer / renderer / shaper

Unchanged from slice 3 — `UnwindSource` set + `Projection` populated → `$unwind` then `$project`; `Route == Projection`; DOM projection shaper. Terminal-only via `HasTerminalOperator` (already includes `UnwindSource`).

## Correctness hazards (explicitly guarded)

1. **Shared-binder scoping (highest):** the SelectMany two-scope branch must be unreachable for non-SelectMany projections (gated on `UnwindSource != null && Projection empty`) and must reject any non-`ti.Outer`/`ti.Inner` leaf within the branch. A whole-branch review + the full spec suite (which exercises SP3/Distinct/GroupBy projections heavily) are the net — same as slices 2–3.
2. **Not regressing slice 3's inner-`Select` form:** that form populates `Projection` in `TranslateSelectMany`, so its trailing `ti => ti.Inner` Select sees `Projection` non-empty → does NOT enter the pending branch. Verify slice 3's tests still pass.
3. **Owned vs reference:** the bare-nav branch accepts only owned collection navs (a reference nav needs `$lookup`, not `$unwind`) → reject.
4. **Two-scope collision:** `ti.Inner.X` where the inner has a member same-named as an outer member must render `$Items.X`, never `$X`. Test a shared member name (as slice 3 did).
5. **`$unwind` empty/absent semantics:** inner-join flatten (owner with empty/missing collection → no rows) — reuse slice 3's coverage.

## Non-goals (deferred)

Cross-collection reference SelectMany (`$lookup` + `$unwind`); correlated/filtered inner (`from o … from i in o.Items.Where(…)`); computed/scalar projection leaves; bare owned-entity results; multi-level/chained SelectMany.

## Verification

- **Expected-results correctness** (no parity baseline — SelectMany hard-fails today): the explicit-result-selector form AND the query-syntax form return the correct flattened rows (asserted vs expected in-memory), incl. empty/absent collection, and a shared outer/inner member name.
- **`NativeOnly` proves native**; every out-of-scope shape hard-fails (assert still throws).
- **MQL** assertion: `$unwind` + `$project` with `$Name` (outer) and `$Items.Price` (inner).
- **Slice 3's inner-`Select` tests unchanged** (hazard 2).
- **Full 3-version `/test-all`** green (EF8/EF9/EF10 — watch for EF8/EF9-only API hazards, as slice 3 hit a CS9174); `MONGODB_EF_NATIVE_ONLY=1` spec sweep — this form MAY finally move some Northwind SelectMany spec tests to native (they use the query-syntax/result-selector form, though over cross-collection refs, so likely still deferred); confirm zero regressions.

## Files touched (anticipated)

- `src/…/Query/Expressions/MongoUnwindSource.cs` — add `InnerEntityType` (or `INavigation`).
- `src/…/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs` — `TranslateSelectMany` bare-nav branch + `TranslateSelect` pending-SelectMany shaper build.
- `src/…/Query/NativeTranslation/NativeSelectManyBinder.cs` (or `NativeProjectionBinder.cs`) — the two-scope transparent-identifier projection binding (reuse slice 3's field-ref logic; keep it in a SelectMany-owned helper to avoid enlarging the shared `NativeProjectionBinder` surface).
- `src/…/Query/AGENTS.md` — as-built note; extend the deferred-shapes list (retire this form) + document the pending-`UnwindSource` binder branch in the invariant family.
- Tests: unit (the two-scope transparent-id binder) + functional (both forms, expected results, `NativeOnly` succeed/throw, shared member name, empty/absent, hard-fail seams).

## Follow-ups

Cross-collection reference SelectMany; correlated/filtered inner; computed/scalar projections; multi-level SelectMany; then the non-SelectMany remainder (Intersect/Except, non-canonical Skip/Take) and SP7. Plus the pre-existing driver-LINQ `Concat().OfType<T>()` NRE.
