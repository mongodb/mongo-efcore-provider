# Native query: SelectMany over a cross-collection reference collection — projected (EF-347 slice 5)

**Ticket:** EF-347 (remaining SP6 relational operators) — SelectMany slice 3 (cross-collection reference, projected).
Epic EF-322. (Slices 3–4 shipped owned-collection SelectMany, inner-`Select` then explicit-result-selector/query-syntax.)
**Type:** New native coverage, **purely additive**. Cross-collection reference SelectMany hard-fails in every mode today; this makes the projected form succeed natively. Nothing that works today changes.
**Stacked on:** EF-347 slice 4 (`d2cacc5`), the current native-stack tip.

## Background (established by a spike — `.superpowers/sdd/refselectmany-spike.md`)

A **reference** collection nav (`Customer.Orders`, a separate collection joined by FK — `HasMany().WithOne().HasForeignKey()`, NOT `OwnsMany`) is real and reaches `TranslateSelectMany`, but it is **not** the bare-nav shape the owned binders match. The spike established:

1. **The `collectionSelector` is a correlated subquery, not a bare nav.** EF hands `TranslateSelectMany` a `collectionSelector` of `Orders.Where(o => c.pk == o.fk)` — `DbSet<Order>().Where(<FK correlation>)` over the target's own `EntityQueryRootExpression`. The reference case is therefore *inherently correlated*.
2. **No driver-LINQ baseline.** It throws `InvalidOperationException` ("could not be translated") in `Native`, `DriverLinq`, and `NativeOnly` alike (`TranslateSelectMany` returns `null` before the gate). Additive; verify vs expected in-memory results.
3. **The projected + query-syntax forms throw identically** and, once accepted, normalize like slices 3–4 (bare-ish collection selector + trivial `TransparentIdentifier` result selector + a separate trailing `Select(ti => new {ti.Outer.X, ti.Inner.Y})`).
4. **The FK correlation is recoverable** both from the `Where` predicate (`outer.pk == inner.fk`) and independently via `INavigation.ForeignKey` (as `LookupExpression` already does for Include: `LocalField=_id`, `ForeignField=<fk>`). The native emit needs a **`ForceUnwind:true` `$lookup` + `$unwind`** (Include's array-preserving `$lookup` is the wrong semantics for a flatten).

**Strong precedent:** `NativeProjectionBinder.TryTranslateProjectedCollectionCount` already recognizes this exact correlated-`Where`-over-`EntityQueryRoot` shape and matches the FK navigation (for projected `c.Orders.Count`). Its correlation helpers (`TryGetCorrelationEqualitySides`, the FK-nav candidate matching) are the reuse target.

## Scope

**In:** SelectMany over a **cross-collection reference (non-embedded) single-level collection nav**, **projected form only** — `q.SelectMany(c => c.Orders, (c,o) => new {c.Name, o.Total})` and query syntax `from c in q from o in c.Orders select new {c.Name, o.Total}`, trailing projection = terminal `new {…}`/DTO of top-level member accesses of `ti.Outer`/`ti.Inner`. Emits:
```
<outer $match/$sort/$skip/$limit>, { $lookup: { from: "orders", localField: "_id", foreignField: "cust_id", as: "_lookup_Orders" } },
{ $unwind: "$_lookup_Orders" }, { $project: { _id: 0, Name: "$Name", Total: "$_lookup_Orders.Total" } }
```
**Out → hard-fail (`null`, unchanged):** bare-entity result (`SelectMany(c => c.Orders)` → `Order` entities — needs `$replaceRoot` + a reference-entity shaper); a **user-filtered/correlated inner** (`c.Orders.Where(userPred)` — an extra predicate conjunct beyond the FK correlation); computed/non-member leaves; nested/multi-level; owned already handled by slices 3–4.

## Design

### Recognition — a reference-nav branch in the SelectMany binder

Add `NativeSelectManyBinder.TryBindReferenceNavUnwind(mongoQ, collectionSelector)`:
1. `collectionSelector.Body` must be `Queryable.Where(EntityQueryRootExpression <target>, predicate)` (unwrap `AsQueryable` if present). Reuse the Count binder's correlation recognition: `TryGetCorrelationEqualitySides(predicate.Body, …)` + the FK-nav candidate match (a single-property FK, `TargetEntityType == rootExpression.EntityType`, matching the correlated side) — but require a **reference** collection nav (`IsCollection && !IsEmbedded()`), the mirror of the Count binder's `!IsEmbedded()` filter. An **extra predicate conjunct** beyond the FK correlation (a user `Where`) makes `TryGetCorrelationEqualitySides` reject → fall back (defers the filtered-inner case).
2. On success: register `mongoQ.AddLookup(new LookupExpression(navigation, forceUnwind: true))` and set the generalized `MongoUnwindSource` (below) to `Reference` kind with `innerScopePath = LookupExpression.GetLookupAlias(navigation)` (i.e. `_lookup_<Nav>`) and `innerEntityType = navigation.TargetEntityType`. Return `true`.

**Extract the correlation helpers** currently `private` in `NativeProjectionBinder` (`TryGetCorrelationEqualitySides`, `TryExtractEqualitySides`, `IsNullGuard`, `GetRootParameter`, and the FK-candidate match) into a shared internal helper (e.g. `NativeCorrelationMatcher`) used by BOTH the Count binder and this one — DRY, and it keeps the correlation logic in one place. (Behavior-preserving for the Count binder — verify its tests.)

### IR — generalize `MongoUnwindSource`

`MongoUnwindSource` (slice 3/4: `ElementPath` + `InnerEntityType`) gains a **kind** and a renamed **`InnerScopePath`**:
- `MongoUnwindSourceKind { Owned, Reference }`.
- `Owned` — `InnerScopePath` = the embedded element name; lowerer emits `MongoUnwindFieldStage` (`$unwind "$<path>"`). (Existing behavior; `ElementPath` → `InnerScopePath`.)
- `Reference` — `InnerScopePath` = `_lookup_<Nav>`; the `$lookup`+`$unwind` come from the registered `LookupExpression` (lowerer, below). Carry the `LookupExpression` (or navigation) so the lowerer can emit it.

The two-scope binder (`TryBindTransparentIdentifierProjection`) is **unchanged** — it already prefixes inner members with `UnwindSource.InnerScopePath` (`$<path>.<field>`); for `Reference` that path is `_lookup_<Nav>`, so `ti.Inner.Total` → `$_lookup_Orders.Total` with no binder change. `Route` stays `Projection`.

### Lowerer — a ForceUnwind-collection branch

`MongoSelectLowerer`: for `UnwindSource.Kind == Owned`, emit `MongoUnwindFieldStage` (as slices 3/4). For `Kind == Reference`, emit the reference `$lookup` + `$unwind` (flatten). **`AppendLookupStages` today throws for a force-unwound collection lookup** (it handles only `IsStreamableReference` [$lookup+$unwind] and `IsNativeCollectionLookup` [$lookup array, `!ForceUnwind`]). Add a branch: a collection lookup with `ForceUnwind` → `MongoLookupStage` + `MongoUnwindStage` (the flatten). The reference `$project` (from `Select.Projection`) follows, reading `_lookup_<Nav>.<field>`. Order: outer canonical stages → `$lookup` → `$unwind` → `$project`, then return (terminal).

### `TranslateSelectMany` + `TranslateSelect`

- `TranslateSelectMany`: try the owned bare-nav / inner-`Select` binders (slices 3/4), then `TryBindReferenceNavUnwind`; on success build the `TransparentIdentifier(Outer=source shaper, Inner=item shaper)` wrapped shaper exactly as slice 4 (the item shaper is the reference target's `StructuralTypeShaperExpression`, type-check-only). Else `null`.
- `TranslateSelect`: the slice-4 pending-SelectMany branch (`UnwindSource != null && Projection empty` → `TryBindTransparentIdentifierProjection` → `BuildSelectManyResultShaper`) is **unchanged** — it already handles the reference case because `UnwindSource` is set and the inner prefix comes from `InnerScopePath`. The whole-entity `ti => ti.Inner`/`ti => ti.Outer` clean-decline (slice 4) also applies unchanged (bare-entity reference SelectMany hard-declines cleanly — deferred).

### Shaper

DOM projection shaper (reused) — reads the `$project` aliases. No reference-entity materialization (that's the deferred bare-entity form).

## Correctness hazards (explicitly guarded)

1. **Don't regress the projected-`Count` binder** — it shares the correlation helpers being extracted into `NativeCorrelationMatcher`. The extraction must be behavior-preserving (verify the `Count`-projection tests). The reference-SelectMany binder requires `!IsEmbedded` (reference) exactly as the Count binder does.
2. **The ForceUnwind-collection lowerer branch must not affect Include** — the existing `IsStreamableReference` (reference-Include streaming) and `IsNativeCollectionLookup` (array collection Include, `!ForceUnwind`) branches are untouched; the new branch keys on `IsCollection && ForceUnwind`, a shape Include never produces.
3. **Two-scope inner prefix** — `ti.Inner.<m>` → `$_lookup_<Nav>.m` (Reference) vs `$<elementPath>.m` (Owned); the generalized `InnerScopePath` carries the right one. Test a reference case + confirm the owned cases still emit the element path.
4. **Filtered-inner rejection** — `c.Orders.Where(userPred)` (a real extra predicate) must reject (defers to a later slice), not silently drop the filter. The `TryGetCorrelationEqualitySides` exactly-one-correlation guard handles this (as it does for filtered `Count`).
5. **Multi-version** (EF8/EF9/EF10) — slice 4 hit an EF8-only CS9174; avoid newer-only overloads.

## Non-goals (deferred)

Bare-entity reference SelectMany (`$replaceRoot` + a reference-entity shaper — a real tracked entity from a `$lookup`); filtered/correlated inner (`c.Orders.Where(userPred)`); computed/scalar projection leaves; nested/multi-level (`ThenInclude`-style, collection-of-collection); `GroupJoin`/explicit `Join` (separate).

## Verification

- **Expected-results correctness** (no baseline): projected reference SelectMany (explicit-result-selector + query-syntax) returns the correct flattened join rows (asserted vs expected in-memory), incl. a principal with zero children (inner-join → no rows) and a shared outer/inner member name.
- **`NativeOnly` proves native**; out-of-scope shapes (bare-entity, filtered-inner, computed leaf) hard-fail (assert still throws; bare-entity → the clean `NotSupportedException` from slice 4).
- **MQL:** `$lookup` (correct `from`/`localField`/`foreignField`/`as`) → `$unwind "$_lookup_<Nav>"` → `$project` with `$Name` (outer) and `$_lookup_<Nav>.Total` (inner).
- **Owned SelectMany (slices 3–4) + projected `Count` + reference Include unchanged** (hazards 1–3).
- **Full 3-version `/test-all`** green; `MONGODB_EF_NATIVE_ONLY=1` spec sweep — the Northwind SelectMany tests are cross-collection reference, so this slice MAY move some to native; confirm zero regressions.

## Files touched (anticipated)

- `src/…/Query/Expressions/MongoUnwindSource.cs` — `Kind`, `InnerScopePath` (rename), carry `LookupExpression`/nav for Reference.
- `src/…/Query/NativeTranslation/NativeCorrelationMatcher.cs` (new) — correlation helpers extracted from `NativeProjectionBinder`; both binders consume it.
- `src/…/Query/NativeTranslation/NativeProjectionBinder.cs` — consume `NativeCorrelationMatcher` (behavior-preserving).
- `src/…/Query/NativeTranslation/NativeSelectManyBinder.cs` — `TryBindReferenceNavUnwind`; two-scope binder unchanged.
- `src/…/Query/NativeTranslation/MongoSelectLowerer.cs` — Reference-kind branch (ForceUnwind-collection `$lookup`+`$unwind`).
- `src/…/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs` — `TranslateSelectMany` reference branch (build the wrapped shaper).
- `src/…/Query/AGENTS.md` — as-built note; deferred-list update.
- Tests: unit (correlation matcher, reference binder, lowerer `$lookup`+`$unwind`) + functional (both forms, expected results, `NativeOnly`, empty-children, shared member name, hard-fail: bare-entity/filtered-inner/computed).

## Follow-ups

Bare-entity reference SelectMany (+ its shaper/`$replaceRoot`); filtered/correlated inner; computed/scalar projections; nested/multi-level SelectMany; then Intersect/Except, non-canonical Skip/Take, SP7. Plus the pre-existing driver-LINQ `Concat().OfType<T>()` NRE.
