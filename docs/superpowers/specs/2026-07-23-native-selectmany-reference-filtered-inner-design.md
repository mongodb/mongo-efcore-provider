# Native filtered-inner reference `SelectMany` (EF-347)

**Date:** 2026-07-23
**Epic:** EF-322 (native LINQ query rewrite) → EF-347 (remaining SP6 relational operators) → SelectMany tail
**Branch (stacked on `NativeQueryOngoing` tip `0f53f0c`):** `EF-347-selectmany-ref-filtered-inner`

## Summary

Make a **filtered** cross-collection reference `SelectMany` go native:

```csharp
from o in q from r in o.Refs.Where(r => r.Total > 100) select new { o.X, r.Y }   // projected
from o in q from r in o.Refs.Where(r => r.Total > 100) select r                   // bare-entity
```

Today the unfiltered reference SelectMany goes native — projected via slice 5, bare whole-inner-entity via the `0f53f0c` slice. Adding a `Where` on the inner collection currently **hard-declines in every mode**: the collection selector arrives as a correlated subquery with an extra predicate, and `NativeCorrelationMatcher.TryMatchCorrelatedCollection` deliberately rejects the extra conjunct (so the user filter is never silently dropped). This slice admits an **inner-element-only** filter and emits it as a `$match` on the unwound element.

## Scope

**In scope**

- Navigation kind: **reference** (cross-collection, non-owned) collection navigation only.
- Result shapes: **both** projected (`select new { … }`) and bare whole-inner entity (`select r`). The filter recognition and `$match` emission are shared across both, so both come from the one mechanism.
- Filter reach: **inner-element-only** predicate — references only the unwound inner element (`r.Total > 100 && r.Name != null`).
- Stacked filters: `o.Refs.Where(p1).Where(p2)` — combine with AND.

**Out of scope (unchanged — keep hard-failing / declining exactly as today)**

- **Correlated-beyond-FK** inner (`r.Total > o.Threshold` — predicate references outer members beyond the FK). Needs cross-scope `$expr`; deferred.
- **Owned-collection** filtered inner (`o.Items.Where(pred)`).
- Computed / translator-unsupported filter operators.
- Computed projection leaves, nested reference `SelectMany`.
- Any operator composed **after** the filtered SelectMany (still hard-fails via the existing SelectMany-after-terminal `HasTerminalOperator` guard).

## Approach

**Approach A — post-unwind `$match` (chosen).** Recognize the residual inner predicate in `TryBindReferenceNavUnwind`, translate it against the inner (target) entity type with the existing `MongoExpressionTranslator`, prefix its field refs with the `_lookup_<Nav>` scope, store it on `MongoUnwindSource`, and have the lowerer emit a `MongoMatchStage` right after the reference `$unwind` and before `$replaceRoot`/`$project`.

Rejected alternatives:

- **Approach B — pipeline `$lookup` with an inner `$match`** (keeps the predicate un-prefixed inside a `let`/`pipeline` `$lookup`). Rewrites the existing reference-lookup rendering (shared with Include) and turns the FK join-key into an `$expr`; far larger blast radius for no result benefit.
- **Approach C — pre-unwind `$filter`** over the `_lookup_<Nav>` array before `$unwind`. Needs the aggregation-expression dialect with a `$$this` variable and buys nothing over a plain post-unwind `$match`.

Approach A does **not** touch the shared `NativeCorrelationMatcher` contract: the peel/split of the user predicate from the FK correlation happens in the SelectMany binder, and the matcher is still only ever fed the isolated FK-correlation predicate — so filtered `Count` (`c.Orders.Where(pred).Count()`) keeps falling back exactly as today.

## Mechanism

### Insertion point (confirmed by reading the lowerer)

The reference `$lookup` + inner-join `$unwind` (`preserveNullAndEmptyArrays: false`) are emitted by `MongoSelectLowerer.AppendLookupStages` at **stage 2**, before the `UnwindSource` block. After them the joined element sits at `_lookup_<Nav>`. So the filter `$match` slots in at the **top of the `UnwindSource` block** — after the (upstream) reference `$unwind`, before the `WholeElement` `$replaceRoot` or the projected `$project`. One insertion covers both result shapes.

Net MQL:

```
$lookup(localField _id, foreignField <fk>, as _lookup_<Nav>)
$unwind($_lookup_<Nav>, preserveNullAndEmptyArrays: false)
$match({ "_lookup_<Nav>.<field>": … })          ← NEW (this slice)
$replaceRoot({ newRoot: "$_lookup_<Nav>" })      (bare-entity)   OR
$project({ … })                                  (projected)
```

### Recognition — `NativeSelectManyBinder.TryBindReferenceNavUnwind`

Today it matches `Where(EntityQueryRootExpression root, fkPred)` directly. Generalize the collection-selector body to peel/split inner user predicates off *before* the FK match. Two shapes, both handled (a spike confirms which EF actually emits — see below):

- **Nested** `Where(Where(root, fkPred), userPred)` (and deeper stacks): peel outer `Where` layers, collect each layer's predicate lambda, until the source is the bare `EntityQueryRootExpression`; the innermost `Where(root, fkPred)` is matched by `NativeCorrelationMatcher.TryMatchCorrelatedCollection(requireEmbedded: false)` exactly as today.
- **Folded** `Where(root, fkPred && userPred)`: split the top-level `AndAlso` conjuncts, find the single FK-correlation conjunct via the matcher, treat the remainder as the user predicate.

The FK correlation itself continues to be resolved to the reference navigation by the unchanged matcher.

### Filter translation & prefix-rewrite

Each collected user predicate lambda `r => <body>` is translated against the **inner target entity type** (`root.EntityType`) with `MongoExpressionTranslator.TryTranslate`. Every resulting `MongoFieldExpression` is then prefix-rewritten to `_lookup_<Nav>.<elementName>` via a small recursive MongoExpression rewriter — generalizing the single-field prefixing `TryTranslateScopedField` already performs. Multiple predicates AND together into one `MongoExpression`.

### Storage & lowering

- `MongoUnwindSource` gains a nullable `Filter` (`MongoExpression?`, already scope-prefixed), threaded through both the `Owned`/`Reference` factories but populated only for Reference in this slice.
- `MongoSelectLowerer.Lower`, inside `if (select.UnwindSource is { } unwind)`: when `unwind.Filter is { } filter`, add `new MongoMatchStage(filter)` after the owned `$unwind` (if any — N/A for Reference, already unwound upstream) and before the `WholeElement`/`Projection` handling.

## Guards / clean declines

All of these decline → the reference-SelectMany no-oracle hard-fail in every mode (`Native`/`DriverLinq`/`NativeOnly`); never a silent wrong result:

- Filter predicate the translator rejects (computed / unsupported operator) → decline.
- Filter referencing outer members beyond the FK (correlated-beyond-FK) → `TryTranslateField` rejects the foreign-rooted member access → decline.
- FK-correlation match ambiguous/unrecognized → the unchanged matcher declines.
- `IsWholeElementRepresentable` (bare-entity eager-nav guard) is orthogonal and unchanged — the filter only adds a `$match`.
- Terminal-only invariant unchanged: the filter is *inside* the collection selector, not an operator after the SelectMany, so no post-terminal guard is affected.

## Multi-version

Expect **no `#if`** — all touched types are internal and behavior is identical across EF8/EF9/EF10. The spike (Task 1) explicitly checks the arriving expression tree on EF8/EF9/EF10, since prior slices hit EF8-only shape / `CS9174` surprises. Full 3-version `/test-all` runs before squash.

## Breaking change

None. All touched types are internal. The change is additive (filtered reference SelectMany hard-fails today, in every mode) and the exact emitted MQL is not part of the contract (per the versioning rubric in `AGENTS.md`).

## Verification

Reference `SelectMany` has **no driver-LINQ oracle** — the driver's LINQ v3 provider rejects a cross-collection `SelectMany`, so supported shapes are proven via `MongoQueryMode.NativeOnly` succeeding + expected in-memory result-set assertions, and unsupported shapes hard-fail in every mode.

Functional `NativeSelectManyTests` (real DB, `RefOwnerItem`-style fixture):

- Filtered **projected** goes native (NativeOnly succeeds; result set matches in-memory).
- Filtered **bare-entity** goes native (NativeOnly succeeds; result set matches in-memory).
- Stacked `.Where(p1).Where(p2)` combines with AND.
- Filter that excludes all children → owner contributes zero rows.
- Filtered SelectMany composes correctly with a parametrized **outer** predicate.
- Clean-decline (hard-fail every mode): correlated-beyond-FK inner; computed / unsupported filter operator.
- Unchanged-behavior guard: filtered `Count` still falls back (matcher contract preserved).

Suite-level:

- Full 3-version `/test-all` green, 0 failures.
- `MONGODB_EF_NATIVE_ONLY=1` spec sweep: no regressions; note any Northwind filtered-SelectMany shape that flips to native.

## Task outline (for the plan)

1. **Spike** — capture the arriving collection-selector tree for a filtered reference SelectMany (nested vs folded), on EF8/EF9/EF10.
2. `MongoUnwindSource.Filter` + the MongoExpression prefix-rewriter.
3. `TryBindReferenceNavUnwind` peel/split + filter translation.
4. Lowerer `$match` emission.
5. Functional tests + clean-decline tests + `AGENTS.md` note.
6. Full 3-version `/test-all` + NativeOnly spec sweep → whole-branch review → squash.
