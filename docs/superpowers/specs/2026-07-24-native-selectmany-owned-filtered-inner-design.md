# Native filtered-inner owned `SelectMany` (EF-347)

**Date:** 2026-07-24
**Epic:** EF-322 (native LINQ query rewrite) → EF-347 (remaining SP6 relational operators) → SelectMany tail
**Branch (stacked on `NativeQueryOngoing` tip `6b9b973`):** `EF-347-selectmany-owned-filtered-inner`

## Summary

Make a **filtered** owned-collection `SelectMany` go native — the embedded-collection analog of
the just-shipped reference filtered-inner slice (`6b9b973`):

```csharp
from o in q from i in o.Items.Where(i => i.Total > 100) select new { o.X, i.Y }   // projected
from o in q from i in o.Items.Where(i => i.Total > 100) select i                  // bare-element
```

Today the *unfiltered* owned SelectMany goes native (slices 3–4: projected via `$unwind` + `$project`;
bare whole-element via `$unwind` + `$replaceRoot`). Adding a `Where` on the inner owned collection
currently **declines** in both owned binders — `TryBind` requires the collection-selector body to be
exactly `Queryable.Select(nav, innerLambda)`, and `TryBindBareNavUnwind` requires it to unwrap directly
to the bare owned nav; a `.Where(pred)` layer makes both fail their structural match and return `false`.
This slice peels the inner user filter off the owned nav and emits it as a `$match` on the unwound
element, reusing the `MongoUnwindSource.Filter` field, `MongoFieldPrefixRewriter`, and the kind-agnostic
lowerer `$match` emission that the reference slice already built.

## Scope

**In scope**

- Navigation kind: **owned** (embedded) collection navigation only.
- Result shapes:
  - **Projected**, both user spellings owned SelectMany normalizes to:
    - inner-`Select` form (`o => o.Items.Where(pred).Select(i => new { … })`) — bound by `TryBind`.
    - explicit-result-selector / query-syntax form (`SelectMany(o => o.Items.Where(pred), (o, i) => new { … })`
      / `from o in q from i in o.Items.Where(pred) select new { … }`) — bound by `TryBindBareNavUnwind`
      + the separate trailing `Select` (`TryBindTransparentIdentifierProjection`).
  - **Bare whole-element** (`from o in q from i in o.Items.Where(pred) select i`, and the two equivalent
    spellings) — bound by `TryBindBareNavUnwind` + `WholeElement`.
- Filter reach: **inner-element-only** predicate — references only the unwound inner element
  (`i.Total > 100 && i.Name != null`).
- Stacked filters: `o.Items.Where(p1).Where(p2)` — combine with AND.

**Out of scope (unchanged — keep declining exactly as today)**

- **Correlated-beyond-outer** inner (`i.X > o.Y` — predicate references outer members). Declines via the
  `ReferencesParameter` guard; deferred.
- **Reference-collection** filtered inner — already shipped (`6b9b973`).
- Computed / translator-unsupported filter operators.
- Computed projection leaves, nested owned `SelectMany`.
- Any operator composed **after** the filtered SelectMany (still declines via the existing
  SelectMany-after-terminal `HasTerminalOperator` guard).

## Approach

**Approach A — peel `Where` layers off the owned nav, reuse everything downstream (chosen).** Recognize
the residual inner predicate(s) by peeling user-authored `Where` layers off the owned nav member access
in both owned binders, translate each against the owned target entity type with the existing
`MongoExpressionTranslator`, prefix its field refs with the owned **unwind path** (e.g. `Items`), AND them,
and store on `MongoUnwindSource.Filter`. The lowerer already emits a `MongoMatchStage` for that filter
right after the owned `$unwind` and before `$replaceRoot`/`$project` — **kind-agnostically** — so no
lowerer, IR, or rewriter change is needed.

Rejected alternatives:

- **Approach B — pre-`$unwind` `$filter`** over the embedded `Items` array before `$unwind`. Needs the
  aggregation-expression dialect with a `$$this` variable and buys nothing over a plain post-`$unwind`
  `$match`.
- **Approach C — a new owned-specific filter field / lowerer arm.** Redundant: `MongoUnwindSource.Filter`
  and the lowerer `$match` are already kind-agnostic; adding an owned-specific path would duplicate them.

### Why this is simpler than the reference slice

Unlike a reference collection (which nav-expands to an **FK-correlated subquery**
`Where(EntityQueryRootExpression<Target>, o => o.pk == i.fk)` and needs `NativeCorrelationMatcher` +
`TrySplitCorrelation` to isolate the FK conjunct), an owned collection is read as a **bare nav member
access** (`o.Items`). A filtered owned inner is therefore just `Where(o.Items, userPred)` — there is **no
FK correlation to split**, no `NativeCorrelationMatcher`, no folded-conjunct case. The peel loop simply
strips `Where` layers until the source is the bare owned nav (`TryGetMemberAccess == outerParam`), exactly
where the two owned binders already expect it.

## Mechanism

### Insertion point (confirmed by mapping the lowerer)

The owned `$unwind` (`MongoUnwindFieldStage` on `unwind.InnerScopePath`, e.g. `$Items`) is emitted **inside**
the `if (select.UnwindSource is { } unwind)` block. The `Filter` `$match` is emitted immediately after it
(kind-agnostic — the existing code checks `unwind.Filter`, not `unwind.Kind`), before the `WholeElement`
`$replaceRoot` or the projected `$project`. So the owned filter slots into the **already-existing** position:

```
$unwind({ path: "$Items", includeArrayIndex: "__ord" (WholeElement only) })
$match({ "Items.<field>": … })                    ← reused (populated by this slice for owned)
$replaceRoot({ newRoot: "$Items", $mergeObjects sentinels })   (bare-element)   OR
$project({ … reads $Items.<field> … })                          (projected)
```

Post-`$unwind`, the element's fields remain at `Items.<field>` (the array field name persists; `$replaceRoot`
has not run yet, and in the projected case never runs). So the filter must be prefixed with the unwind path —
the **same** `Items.` prefix `TryTranslateScopedField`/`MongoFieldPrefixRewriter` already apply to owned
projection field refs.

### Recognition — shared peel/build helper + the two owned binders

Factor a private helper in `NativeSelectManyBinder` that both binders call:

1. **Peel.** Given the collection-selector source expression (`selectSource` for `TryBind`;
   `collectionSelector.Body` for `TryBindBareNavUnwind`), while it unwraps
   (`UnwrapAsQueryable`) to a `Queryable.Where(innerSource, predicate)` whose `innerSource` is **not yet**
   the bare owned nav member access, collect the predicate lambda and descend to `innerSource`. Stop when
   the source unwraps to the bare owned nav (`TryGetMemberAccess(navExpr, out navRoot, out navName) &&
   ReferenceEquals(navRoot, outerParam)`). Return the collected predicates + the bare nav expression.

   EF's nav-expansion emits **nested** `Where(Where(nav, p1), p2)` for stacked `.Where(p1).Where(p2)`
   (spike-confirmed on EF8/EF9/EF10) — the loop peels each layer. There is no folded (`p1 && p2` in one
   `Where`) case to handle here the way the reference slice's `TrySplitCorrelation` does, because there is
   no FK conjunct to separate; a genuinely folded single-`Where` predicate `i => p1 && p2` is one predicate
   the inner translator handles as a single `MongoBinaryExpression(AndAlso, …)`.

2. **Build filter.** After the caller resolves `navigation` (owned collection) and
   `unwindPath = navigation.TargetEntityType.GetContainingElementName()`, translate each peeled predicate
   against the inner target entity type (`new MongoExpressionTranslator(navigation.TargetEntityType)`),
   AND them into one `MongoExpression`, and prefix every field ref via
   `MongoFieldPrefixRewriter.Rewrite(expr, unwindPath)`. Decline (return `false`, no mutation) if any
   predicate references the outer parameter (`ReferencesParameter(pred.Body, outerParam)` —
   correlated-beyond-outer) or the inner translator rejects it (computed / unsupported operator).

**`TryBind` (inner-`Select` form).** Peel `Where` layers off `selectSource` before the existing
`TryGetMemberAccess`/owned-nav checks run on the peeled bare nav; after resolving `unwindPath`, build the
filter and set `UnwindSource.Filter` alongside the existing `MongoUnwindSource.Owned(...)` + projections.
The projection binding (two-scope, `TryTranslateScopedField`) is unchanged.

**`TryBindBareNavUnwind` (explicit / query-syntax / bare-element form).** Peel `Where` layers off
`collectionSelector.Body` before the existing bare-nav match; build the filter and set `UnwindSource.Filter`.
The trailing projected `Select` (`TryBindTransparentIdentifierProjection`) and the `WholeElement` gate are
unchanged.

Both binders mutate `mongoQ` only **after** the filter is successfully built — a declined filter leaves the
query untouched (no partial mutation).

### Filter translation & prefix-rewrite

Identical to the reference slice, except the prefix is the owned **unwind path** (`Items`) rather than the
`_lookup_<Nav>` lookup alias. `MongoFieldPrefixRewriter` is reused verbatim: it recurses Binary/Unary/In/Regex,
passes constants/parameters through, and fails closed on any other node.

### Storage & lowering

- `MongoUnwindSource.Filter` (reused) — populated by both owned binders now, in addition to the reference
  binder.
- `MongoSelectLowerer` — **unchanged**. Its `if (unwind.Filter is { } filter)` block already emits
  `new MongoMatchStage(filter)` after the owned `$unwind` and before `$replaceRoot`/`$project`. The stale
  "reference only for now" comment at that block is corrected to note owned is now populated too.

## Guards / clean declines

All declines make the owned binder return `false` with **no mutation**; `TranslateSelectMany` then returns
`null`. Whether that decline hard-fails or falls back depends on the projection (see the oracle note below).

**Driver-LINQ oracle depends on the PROJECTION shape, not the inner `Where`.**
> **AS-BUILT CORRECTION (Task 5, `Filtered_owned_computed_projection_leaf_falls_back_gracefully_except_under_NativeOnly`):**
> the Task 1 spike concluded "no oracle for a filtered owned SelectMany", but it only probed OUTER-referencing
> projections. The real determinant is what the result projects. The driver's LINQ v3 provider CANNOT translate
> a filtered owned SelectMany whose result references the OUTER entity (`(o, i) => new {o.Name, i.Price}` — a
> cross-scope owner flatten) or returns the whole element (`select i` — the `'bsonDoc'` crash), so those shapes
> have **no oracle** → proven via `MongoQueryMode.NativeOnly` succeeding + expected-in-memory assertions (no
> `Native == DriverLinq` parity); but it CAN translate one whose projection reads ONLY the inner element
> (`(o, i) => new {X = i.Price * 2}` → `Items.Where(...).Select(i => i.Price * 2)`), so that shape HAS a working
> oracle. The inner `Where` alone never removes the oracle. **Consequence for declines:** a decline with an
> outer-referencing (or whole-element) projection hard-fails in **every** mode; a decline with an inner-only
> projection (e.g. a computed projection leaf) falls back **gracefully** (`Native`/`DriverLinq` return correct
> values, only `NativeOnly` throws). Verified: the fallback returns CORRECT data, not silently-wrong.

Specific declines:

- Filter referencing the outer parameter (`i.X > o.Y`, correlated-beyond-outer) → `ReferencesParameter`
  rejects **before** translation. This guard is load-bearing for the same reason as in the reference slice:
  `MongoExpressionTranslator` resolves members by NAME only, so an outer member sharing a property name with
  the inner owned type would silently mis-scope without it.
- Filter the translator rejects (computed / unsupported operator) → `TryTranslate` returns false → decline.
- `WholeElement` representability (`IsWholeElementRepresentable` — nested-nav guard, sentinel-collision
  guard, owned-key-serialization guard) is orthogonal and unchanged — the filter only adds a `$match`.
- Terminal-only invariant unchanged: the filter is *inside* the collection selector, not an operator after
  the SelectMany, so no post-terminal guard is affected.

## Multi-version

Expect **no `#if`** — all touched types are internal and behavior is identical across EF8/EF9/EF10. The spike
(Task 1) explicitly checks the arriving expression tree on EF8/EF9/EF10, since prior slices hit EF8-only
shape / `CS9174` surprises. Full 3-version `/test-all` runs before squash.

## Breaking change

None. All touched types are internal. The change is additive (filtered owned SelectMany declines today) and
the exact emitted MQL is not part of the contract (per the versioning rubric in `AGENTS.md`).

## Verification

**Oracle depends on the projection shape (see the as-built correction in Guards above).** The tested supported
shapes project the outer entity (`new {o.Name, i.Price}`) or the whole element, which the driver cannot
translate, so they are proven via `MongoQueryMode.NativeOnly` succeeding + expected-in-memory result-set
assertions (no `Native == DriverLinq` parity). Declines with such a projection hard-fail every mode; a decline
with an inner-only projection (computed leaf) falls back gracefully with correct values.

Functional `NativeSelectManyTests` (real DB, reusing the owned `Owner`/`Item` fixture):

- Filtered **projected** goes native — inner-`Select` form.
- Filtered **projected** goes native — explicit-result-selector / query-syntax form.
- Filtered **bare whole-element** goes native (`NativeOnly` succeeds; result set matches in-memory).
- Stacked `.Where(p1).Where(p2)` combines with AND.
- Filter that excludes all children → owner contributes zero rows.
- Filtered SelectMany composes correctly with a parametrized **outer** predicate.
- MQL assertion: `$unwind` → `$match` (`Items.<field>` prefixed) → `$project`/`$replaceRoot`, in that order.
- Clean-decline (outer-referencing projection): correlated-beyond-outer inner; computed / unsupported filter
  operator — both hard-fail in every mode (`ThrowsAny`).
- Clean-decline (inner-only projection): a computed projection leaf (`new {X = i.Price * 2}`) — falls back
  gracefully (`Native`/`DriverLinq` correct, `NativeOnly` throws), driver oracle exists.

Suite-level:

- Full 3-version `/test-all` green, 0 failures (baseline `6b9b973`: EF8 7282/67, EF9 7643/68, EF10 7240/71;
  expect a small positive delta from the new tests, zero regressions).
- `MONGODB_EF_NATIVE_ONLY=1` spec sweep: no regressions; note any Northwind filtered-owned-SelectMany shape
  that flips to native.

## Task outline (for the plan)

1. **Spike** — capture the arriving collection-selector tree for a filtered owned SelectMany (inner-`Select`
   / explicit / bare-element spellings + stacked), on EF8/EF9/EF10; confirm the `Where` source is the bare
   owned nav (member access / `EF.Property`), no correlated subquery; determine whether the driver-LINQ
   oracle translates the filtered projected form.
2. Shared peel/build helper in `NativeSelectManyBinder` + wire `TryBind` and `TryBindBareNavUnwind` to it;
   populate `MongoUnwindSource.Filter`. (Unit tests: binder captures filter for inner-`Select`, bare-nav,
   stacked; declines correlated-beyond-outer and computed operators.)
3. Lowerer comment correction (no logic change) + a unit test asserting the owned filter `$match` slots in
   after the owned `$unwind` and before `$replaceRoot`/`$project`.
4. End-to-end functional tests + `AGENTS.md` as-built note.
5. Full 3-version `/test-all` + NativeOnly spec sweep → whole-branch review → squash.
