# Native SelectMany computed-leaf (EF-347 — single-scope arithmetic)

## Summary

Make a numeric-arithmetic (`+ - * / %`) projection leaf inside a **SelectMany trailing projection**
go native, rendering as a computed field in the `$project` that follows the `$unwind`/`$lookup`,
reusing the arithmetic machinery the plain-`Select` slice (`ad72ae2`) shipped. Example:

```csharp
from o in ctx.Owners from i in o.Items select new { o.Name, Doubled = i.Price * 2 }
// $unwind Items → $project: { Name: "$Name", Doubled: { $multiply: ["$Items.Price", 2] }, _id: 0 }
```

Today every SelectMany trailing projection carrying an arithmetic leaf falls back to driver-LINQ
(owned forms with a driver oracle) or hard-fails in every mode (reference forms, no oracle). This is
the immediately-following slice deferred by the plain-`Select` arithmetic computed-projection design
(`docs/superpowers/specs/2026-07-25-native-computed-projection-arithmetic-design.md`, "Deferred:
SelectMany computed-leaf — reuses this slice's `TryTranslateValue`, but applied in the separate
two-scope `NativeSelectManyBinder.TryBindTransparentIdentifierProjection`").

## Motivation

`NativeSelectManyBinder.TryBindTransparentIdentifierProjection` — the trailing-projection binder for
the owned explicit/query-syntax form, owned bare-nav, reference, nested-reference, and filtered
SelectMany variants — admits a projection member only when it is a **bare member access**
(`argExpr is MemberExpression member`, resolved to a scope via `TryResolveScopeDepth`). Any arithmetic
leaf (`i.Price * 2`, `i.Price * i.Qty`) fails that shape check and the whole projection declines.

The plain terminal `Select` gained arithmetic-computed-leaf support in `ad72ae2` via
`MongoExpressionTranslator.TryTranslateValue`. That method — the arithmetic operand machinery plus
Guard A (integer division) and Guard B (converter/representation) — is scope-agnostic and directly
reusable here. This slice lifts the "bare member only" restriction in the SelectMany binder for a
single-scope numeric arithmetic leaf, matching the breadth the plain `Select` already has.

## Scope

**In scope — single-scope arithmetic leaves in `TryBindTransparentIdentifierProjection`:**

- A projection leaf whose top node is a `BinaryExpression` with an arithmetic `NodeType`
  (`Add`/`Subtract`/`Multiply`/`Divide`/`Modulo`) — the SAME top-node gate
  `NativeProjectionBinder.TryTranslateLeaf` uses.
- Every scope-rooted member operand in that one leaf resolves to the **same** transparent-identifier
  scope: all-inner (`i.Price * i.Qty`, `i.Price * 2`), or all-outer, or a nested single scope.
- Applies across every form that routes through this binder: owned explicit/query-syntax, owned
  bare-nav (whole-element aside), reference (single- and two-level nested), and the filtered variants
  of each. Mixed member-access + arithmetic leaves in one projection are fine (each leaf is bound
  independently).

**Out of scope — declines cleanly (no behavior change vs. today):**

- **Cross-scope** arithmetic in a single leaf (`o.Discount * i.Price`, spanning outer + inner, or two
  different nested scopes). Deferred follow-on — needs per-member multi-scope assembly like the
  correlated-beyond-FK two-scope translator, generalized to N scopes.
- The **inner-`Select` form** (`o.Items.Select(i => new { X = i.Price * 2 })`) bound by the separate
  `NativeSelectManyBinder.TryBind`, not this binder. Stays hard-fail. Deferred.
- The **non-arithmetic computed long tail** — string concat/methods, date-part extraction, `Math.*`,
  type-changing casts, unary negation. Rejected by `TryTranslateValue`'s numeric gate exactly as on the
  plain-`Select` path.
- **Integer-result division** — guarded out by `TryTranslateValue`'s Guard A, same as the plain path.
- **Converter / non-default-`BsonRepresentation` operands** — guarded out by Guard B, same as plain.

## What already exists (why this slice is small)

1. **The arithmetic value translator — DONE.** `MongoExpressionTranslator.TryTranslateValue`
   (`ad72ae2`) already turns an arithmetic expression over member/constant/parameter operands into a
   `MongoBinaryExpression` tree, applying Guard A and Guard B. It takes a single scope's translator
   (one `IEntityType`), which is exactly what a single-scope leaf needs.
2. **The prefix rewriter — DONE.** `MongoFieldPrefixRewriter.Rewrite(MongoExpression, string)` already
   walks a translated `MongoBinaryExpression`/`MongoUnaryExpression`/`MongoFieldExpression` tree and
   prefixes every field-ref's element name with a scope path (`Price` → `_lookup_Refs.Price`, or
   `Items.Price`). It was built for filter conjuncts and handles exactly the node set an arithmetic
   leaf produces.
3. **The emit side — DONE.** `MongoProjection` entries render arithmetic through
   `MongoAggregationExpressionRenderer` (`{ $multiply: [...] }`), proven end-to-end by the plain-`Select`
   slice. The SelectMany `$project` uses the same rendering path.
4. **The scope resolver — DONE.** `TryResolveScopeDepth` already maps a `ti.<Outer/Inner hops>` chain
   to a scope index (0 = root/owner, k = the k-th unwound source), generalized to N scopes by the
   nested-reference slice.

The only new code is one branch in `TryBindTransparentIdentifierProjection` plus a small re-rooting
`ExpressionVisitor`.

## Mechanism

`TryBindTransparentIdentifierProjection` (`NativeSelectManyBinder.cs`, ~line 508) currently loops over
`(alias, argExpr)` projection members and requires each `argExpr` to be a bare `MemberExpression`.

Add a second branch, reached when the member-access branch's shape check fails and `argExpr` is
instead a `BinaryExpression` with an arithmetic `NodeType`:

1. **Single-scope resolution + re-rooting (one pass).** Run a re-rooting `ExpressionVisitor` over
   `argExpr`. In `VisitMember`, attempt `TryResolveScopeDepth(node.Expression, ti, sources.Count, out
   var k)`:
   - On success, this is a scope-rooted leaf: replace the node with
     `Expression.MakeMemberAccess(scopeParams[k], node.Member)`, record `k` in a seen-scopes set, and
     do NOT recurse into `node.Expression` (the `ti.<hops>` chain is consumed).
   - On failure, recurse normally.
   After the walk, if the seen-scopes set has **more than one distinct scope** (cross-scope) or is
   empty (no scope-rooted operand — e.g. a pure-constant leaf that the arithmetic top-node gate should
   already have made rare), **`return false`** (decline; no mutation).
2. **Translate via the resolved scope's translator.** `translators[k].TryTranslateValue(rerooted, out
   var computed)`. On `false` (non-numeric, integer division, converter/representation — Guards A/B),
   **`return false`**.
3. **Prefix inner scopes.** If `k > 0`, `computed = MongoFieldPrefixRewriter.Rewrite(computed,
   sources[k - 1].InnerScopePath)` — turning `Price` into `_lookup_Refs.Price` / `Items.Price`,
   exactly as the bare-member branch does manually for a single field (line 547).
4. **Record.** `if (!seen.Add(alias)) return false;` then `projections.Add(new MongoProjection(alias,
   computed));` — same alias-dedup and list the bare-member branch uses.

The existing no-partial-mutation invariant holds: `mongoQ.Select.AddProjection` is only called after
the whole `members` loop succeeds (line 553), so any `return false` mid-loop leaves the query
untouched and `TranslateSelectMany` falls through to `null`/`MarkNotNativelyRepresentable()` per the
form's existing decline path.

**No changes** to the lowerer, renderer, `MongoProjection`, `MongoUnwindSource`, or any shaper are
anticipated — see the open questions for the read-back risk the spike must close before that
"no shaper change" claim is trusted.

## Disposition of existing tests

| Test | Leaf | Form / binder | Before | After |
|---|---|---|---|---|
| `Explicit_result_selector_form_computed_leaf_falls_back_gracefully_except_under_NativeOnly` | `i.Price * 2` | owned explicit, this binder | graceful fallback | **native**; has driver oracle → assert `Native == DriverLinq` parity |
| `Filtered_owned_computed_projection_leaf_falls_back_gracefully_except_under_NativeOnly` | `X = i.Price * 2` | owned filtered, this binder | graceful fallback | **native**; oracle → parity |
| `Reference_form_computed_leaf_hard_fails_in_every_mode` | `r.Tag + "!"` | reference, this binder | hard-fail every mode | **unchanged** (string concat is non-arithmetic; still declines) |
| `Computed_leaf_hard_fails_in_every_mode` | `X = i.Price * 2` | owned inner-`Select`, `TryBind` | hard-fail every mode | **unchanged** (different binder; out of scope) |

For the two flips, the test is REPURPOSED to assert the new native behavior (rename off
`_falls_back_gracefully_except_under_NativeOnly`; under `NativeOnly` it now SUCCEEDS, and assert the
correct computed values, not merely "no throw"). New coverage to add:

- **Reference arithmetic leaf** (`r.Total * 2`) — currently hard-fails; flips to native. No driver
  oracle → prove via `MongoQueryMode.NativeOnly` succeeding + expected in-memory result set.
- **Two-operand single-scope** (`i.Price * i.Qty`) and **all-outer** (`o.A * o.B`) leaves go native.
- **Cross-scope** leaf (`o.Discount * i.Price`) declines — owned: graceful fallback with correct
  values; reference: hard-fail every mode (no oracle). Assert the retained decline explicitly.
- **Integer division** (`i.Whole / i.Divisor`, integer result) and **string-concat** leaves still
  decline (Guards / numeric gate), same disposition as their form's other declines.
- A **mixed** projection (`new { o.Name, Doubled = i.Price * 2 }`) — one bare member + one arithmetic
  leaf — goes native, both aliases correct.

## Open questions for the spike (Task 1)

The plain-`Select` arithmetic slice (`ad72ae2`) hit a **silent-wrong-data** shaper clobber that a
spike caught before shipping: `MongoProjectionBindingExpressionVisitor` decomposed `c.Age * c.Score`
into two `MemberExpression` visits that overwrote the same `ProjectionMember` slot, yielding
`(Age*Score)²`. That was fixed with a NEW `BinaryExpression` case gated on `Route == Projection`. The
SelectMany trailing projection binds through a **different** shaper path (the by-index
`ProjectionBindingExpression { Index: not null }` pass-through — see the EF-347 slice 3 note in
`Query/AGENTS.md`), so it is NOT obvious whether the same clobber can occur here. The spike MUST:

1. **Prove read-back, not just emit.** Drive a live query for each in-scope shape (owned explicit,
   filtered, reference, nested) under `NativeOnly` and compare the materialized values against an
   in-memory oracle — the emitted `$project` looking right is NOT sufficient (the plain-slice bug
   emitted correct MQL and still returned wrong data). If a clobber exists, identify whether the
   SelectMany shaper needs its own analogous fix (and, if so, this slice is no longer shaper-free —
   revise the plan).
2. **Confirm EF delivers the leaf intact.** Verify EF's nav-expansion / pending-selector machinery
   passes the arithmetic leaf through to `TryBindTransparentIdentifierProjection` unfused, for each
   form — the set-ops slices showed EF sometimes fuses/pushes trailing composition before the
   provider sees it.
3. **Confirm the re-rooting visitor handles nested chains.** For a two-level nested reference
   (`ti.Outer.Inner.<m>` → scope 1, `ti.Inner.<m>` → scope 2), verify the visitor re-roots each
   operand onto the right `scopeParams[k]` and the single-scope check correctly ACCEPTS an all-one-
   scope nested leaf and DECLINES a genuinely cross-scope one.
4. **Confirm the owned driver oracle.** Re-verify that the owned explicit/filtered inner-only
   arithmetic leaf still has a working driver-LINQ oracle (the filtered-inner note ties oracle
   existence to the projection shape) so the `Native == DriverLinq` parity assertion is valid.

## Files (anticipated)

- `Query/NativeTranslation/NativeSelectManyBinder.cs` — new arithmetic branch in
  `TryBindTransparentIdentifierProjection` + the private re-rooting `ExpressionVisitor`.
- Tests: `FunctionalTests/Query/NativeSelectManyTests.cs` — repurpose the two flipping tests; add the
  new native + decline + mixed cases above.
- `Query/AGENTS.md` — new "Native SelectMany computed-leaf (EF-347)" as-built note; update the plain-
  `Select` arithmetic note's "Deferred: SelectMany computed-leaf … planned as the next slice" wording
  to reflect that single-scope arithmetic now goes native (cross-scope + inner-`Select` form remain
  deferred).

**Interfaces:** none new. `TryTranslateValue`, `MongoFieldPrefixRewriter.Rewrite`, `TryResolveScopeDepth`
are all reused unchanged; all touched types are `internal`, so no public-API or breaking-change surface,
and no `#if` (identical EF8/EF9/EF10 behavior).

## Verification

- 3-version `/test-all` (EF8/EF9/EF10) green, pass counts ≥ the `ad72ae2` baseline.
- `MONGODB_EF_NATIVE_ONLY=1` spec sweep — no native regression vs. baseline.
- Squashed to one commit, plain FF onto `origin/NativeQueryOngoing`, `-presquash` safety branch kept
  per the stacked-PR workflow.
