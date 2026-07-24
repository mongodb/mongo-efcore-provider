# Native correlated-beyond-FK inner-filter reference `SelectMany` (EF-347)

## Summary

Make a cross-collection **reference** `SelectMany` whose inner `.Where(pred)` references the
**outer** entity *beyond* the FK correlation go **native**, for both result shapes and for
stacked/mixed filters. Today such a filter hard-fails in every mode: the
`ReferencesParameter` guard in `NativeSelectManyBinder.TryBindReferenceNavUnwind` declines any
peeled user predicate that structurally references the outer `SelectMany` parameter.

Example (previously hard-failed, now native):

```csharp
db.Owners.SelectMany(o => o.Refs.Where(r => r.Tag == o.Name), (o, r) => new { o.Name, r.Tag });
```

This is the next slice in the EF-347 SelectMany tail, stacked directly on the reference
filtered-inner slice (`6b9b973`) and the owned filtered-inner slice (`6ae61ac`). It reuses the
`MongoUnwindSource.Filter` post-`$unwind` `$match` machinery those slices built; the only new
work is translating a correlated (two-scope) predicate.

## Scope

**In scope — goes native:**

- A reference-collection `SelectMany` whose inner filter references the outer entity beyond the
  FK — `o.Refs.Where(r => r.Tag == o.Name)` — for:
  - a **projected** result (`(o, r) => new { o.Name, r.Tag }` / query-syntax / DTO), and
  - a **bare-entity** result (`(o, r) => r` / `from o … from r in o.Refs.Where(pred) select r`).
- **Full predicate breadth** for the correlated conjunct — whatever the existing
  `MongoExpressionTranslator` accepts: all six field-to-field comparisons
  (`==`/`!=`/`<`/`>`/`<=`/`>=`) and arithmetic — because the two-scope translation reuses the
  translator's own operator dispatch and the renderer's per-subtree dialect mixing.
- **Mixed** conjuncts in one `.Where` — `o.Refs.Where(r => r.Tag != "Widget" && r.Tag == o.Name)`
  (the inner-only conjunct stays a plain `$match` term, the correlated conjunct becomes `$expr`).
- **Stacked** filters — `o.Refs.Where(p1).Where(p2)` where either/both are correlated (ANDed).

**Out of scope / unchanged (still deferred):**

- The **owned** correlated-beyond-outer inner filter (the fast-follow — owned collections
  nav-expand to a bare member access with no FK subquery; a separate slice touching
  `TryBuildOwnedInnerFilter`).
- A **nested** reference `SelectMany` (`from o from r in o.Refs from x in r.SubRefs`).
- A **computed** projection leaf.
- **Cross-scope arithmetic *within a single operand*** of the correlated comparison
  (`r.Price > o.Budget * r.Discount` — one side mixing both scopes). This is an exotic shape; if
  the chosen two-scope mechanism cannot resolve it cleanly it declines (hard-fails, no oracle),
  and it is an accepted deferral. Confirmed / finalized in Task 1.

## Spike (done)

Findings: `.superpowers/sdd/EF-347-correlated-inner-spike.md`. Verbatim trees captured for five
probes across EF8/EF9/EF10 (byte-identical). Verdict:

- **(a) NESTED, not folded** — the correlated user filter is a plain outer `Where` wrapping the
  FK-correlation `Where`: `Where(Where(root, fkPred), userPred)`, same nesting as the inner-only
  filtered-inner slice. The existing peel loop already collects it.
- **(b) The outer reference is a plain member access on the outer param** — `o.Name` renders as a
  `MemberExpression` on a `ParameterExpression` that is `ReferenceEquals` to
  `collectionSelector.Parameters[0]` (proven by the existing `ReferencesParameter` decline
  firing). Identity-based routing is viable.
- **(c) Shadow case keeps distinct parameter instances** — `r.Name == o.Name` renders with the
  inner `Where` param (`RefItem`) and the outer param (`RefOwner`) as two **distinct** instances
  despite the shared member name. Parameter-identity routing disambiguates unambiguously; name
  shadowing is not a tree-level problem.
- **(d) A mixed conjunct is ONE `Where` layer with an `AndAlso` body** —
  `((r0.Tag != "Widget") AndAlso (r0.Tag == r.Name))`, not split across layers.
- **(e) Stacked correlated → nested layers** — `Where(Where(Where(root, fkPred), p1), p2)`.
- **(f) EF8/EF9/EF10 identical — no `#if` needed.**
- **No surprises** — nav-expansion adds no join for the outer ref, does not rewrite `o.Name` into
  a subquery/constant, does not fold predicates.

Naming caveat (from the spike): EF renames the lambda params in the captured tree — the outer
param prints as `r` and the inner `Where` param as `r0`. Routing uses the `ParameterExpression`
reference (`collectionSelector.Parameters[0]`), never the printed name, so this is cosmetic.

## Approach (A — post-`$unwind` `$expr` `$match`, chosen)

The FK correlation stays in the `$lookup` join keys (localField `_id` / foreignField `OwnerId`).
The user's correlated predicate becomes an additional term ANDed onto the existing
`MongoUnwindSource.Filter`, emitted as the post-`$unwind` `$match` the filtered-inner slice
already built:

```
$lookup(_id/OwnerId → _lookup_Refs)
  → $unwind(_lookup_Refs, preserveNullAndEmptyArrays: false)
  → $match({ $and: [ <inner-only terms>, { $expr: { $eq: ["$_lookup_Refs.Tag", "$Name"] } } ] })
  → $replaceRoot | $project
```

Rejected alternative (**B**): pushing the whole correlation into a `let`/`pipeline` `$lookup`.
More MongoDB-idiomatic but requires a brand-new correlated-`$lookup` rendering path, reworks how
the FK correlation is emitted, and reuses none of the just-built `Filter` machinery — a premature
optimization, much larger.

### Core new component — a two-scope, identity-routed translation

Post-hoc prefixing **cannot** work: translating a mixed predicate against the inner scope
mis-resolves the outer `o.Name` by name (the shadow hazard — the inner translator resolves member
accesses by name only, so `o.Name` would resolve as the inner element's own `Name`). Identity
routing must therefore happen **during** member resolution — which is what `MongoExpressionTranslator`
owns.

**Mechanism:** extend `MongoExpressionTranslator` with an **optional** outer scope
(`outerParam` + outer `IEntityType`, plus the inner param and the `_lookup_<Nav>` prefix). During
member-access field resolution:

- if the access roots on `outerParam` (by `ReferenceEquals`) → resolve against the **outer**
  entity type and emit a **root** field ref (`$Name`);
- otherwise → resolve against the **inner** entity type and emit an `_lookup_<Nav>`-**prefixed**
  ref (`$_lookup_Refs.Tag`).

When no outer scope is configured (every existing caller), behavior is **byte-identical** to
today. This reuses *all* the translator's operator dispatch — comparisons, `AndAlso`/`OrElse`,
arithmetic, `In`, regex — which is exactly where the "full breadth" comes from, and produces a
correctly-scoped `MongoExpression` in a single pass. The **renderer then does per-subtree dialect
mixing** (the index-first dialect rule already implemented): inner-only conjuncts render as plain
`$match` terms, and only the correlated field-to-field subtree is wrapped in `$expr`. No manual
`AndAlso` splitting is needed — the translator resolves each leaf's scope and the renderer draws
the dialect boundary, so finding (d) (the mixed `AndAlso` body) is handled naturally.

The prefixing for a correlated predicate happens **inside** the two-scope translation (fields come
out already correctly scoped), so the blanket `MongoFieldPrefixRewriter` is **not** applied on this
path (it is still used, unchanged, for the inner-only predicate path).

> Implementation note for Task 1: the alternative to extending the shared translator is a dedicated
> side-wise helper (translate each side of a correlated comparison in its own single scope and
> reassemble), which is more contained but duplicates operator dispatch and cannot express
> cross-scope-within-a-side. The extend-the-translator option is preferred for reuse/breadth; the
> plan's first task validates that the extension is strictly additive for all existing callers
> before building on it.

### Guard relaxation

`ReferencesParameter` flips from **decline** to **route**. In `TryBindReferenceNavUnwind` (and its
`TrySplitCorrelation` folded path), for each peeled user predicate:

- if it does **not** reference the outer param → existing inner-translate + blanket-prefix path,
  unchanged;
- if it **does** reference the outer param → translate it with the two-scope translator; on success
  AND the result onto `Filter`; on failure (an unsupported operator, or a shape the two-scope
  translation cannot resolve) → return `false` with **no mutation** of `mongoQueryExpression`
  (the no-partial-mutation-on-decline invariant), so `TranslateSelectMany` falls through and
  returns `null`.

### Lowerer / renderer — unchanged

`MongoSelectLowerer.Lower`'s `UnwindSource` block already emits `Filter` as a `$match` after the
reference `$lookup`+`$unwind` and before `$replaceRoot`/`$project`, kind-agnostic; it composes with
both the projected `$project` and the bare-entity `$replaceRoot` for free. The renderer already
supports field-to-field `$expr` and per-subtree dialect mixing. Task 1 confirms the `Filter` renders
through `MongoQueryLanguageRenderer` (the dialect-mixing `$match` renderer), not a path that cannot
emit `$expr`.

## Decline behavior & no oracle

Cross-collection reference `SelectMany` has **no driver-LINQ oracle** (the driver's own LINQ v3
provider rejects it), so every decline — including a correlated predicate the two-scope translation
cannot handle — **hard-fails in every mode** (`Native`/`DriverLinq`/`NativeOnly`), consistent with
the whole reference-SelectMany family. A native success is proven via `MongoQueryMode.NativeOnly`
succeeding plus an expected-in-memory result-set assertion, not `Native == DriverLinq` parity.

## Known interaction — EF-221 (mismatched-CLR-type equality)

The correlated conjunct is a field-to-field comparison, so it inherits the provider's existing,
intentional **value-equality** semantics for mismatched-CLR-type comparisons (tracked as
`EF-221`; see `docs/failing-spec-tests.md` and the maintainers' `providerconvert.md` position). If
two correlated fields have different numeric CLR types, the native `$expr` performs a value
comparison (returns value-matched rows) rather than EF's boxing-empty semantics — **the same
behavior already produced and defended for single-scope field-to-field comparisons (EF-329)**, and
purely additive here (the shape hard-fails today). **No new guard** is added; this is recorded as a
known interaction, not a new gap. (The reference fixture uses `string`/`ObjectId` fields, so no
mismatch actually arises in the tests.)

## Not a breaking change

All touched types are `internal`. The change is hard-fail → native for a previously-unsupported
shape; query results for previously-supported shapes are unchanged; the emitted MQL is not part of
the contract (per the versioning rubric). The optional-outer-scope extension to
`MongoExpressionTranslator` is strictly additive (single-scope callers byte-identical).

## Files (anticipated)

- `src/…/Query/NativeTranslation/MongoExpressionTranslator.cs` — optional identity-routed outer
  scope for member-access resolution (strictly additive).
- `src/…/Query/NativeTranslation/NativeSelectManyBinder.cs` — relax `ReferencesParameter` from
  decline to route; wire the two-scope translation into `TryBindReferenceNavUnwind` (and the
  `TrySplitCorrelation` folded path) for correlated peeled predicates.
- `src/…/Query/AGENTS.md` — as-built note (correlated-beyond-FK now native; the guard is now a
  router; EF-221 known interaction).
- `tests/…/FunctionalTests/Query/NativeSelectManyTests.cs` — convert the existing
  `Reference_form_correlated_beyond_fk_inner_hard_fails_in_every_mode` decline test into native
  success + shadow / mixed / stacked / bare-entity / zero-match / parametrized-outer coverage; MQL
  assertion for `$expr` in the post-`$unwind` `$match`.
- `tests/…/UnitTests/Query/NativeTranslation/…` — unit coverage for the two-scope translation
  (identity routing, shadow disambiguation, additive single-scope behavior).

## Verification

- Full 3-version `/test-all` (EF8/EF9/EF10) GREEN 0-fail before squash — the controller runs it
  foreground (per the process lesson), summing all three per-assembly summary blocks.
- `MongoQueryMode.NativeOnly` spec sweep — no regressions.
- Subagent-driven development, one task at a time, **stop for review after every task**.
- Final opus whole-branch review; squash to one commit with a full PR-style message; back up
  `EF-347-selectmany-correlated-beyond-fk-presquash` before squashing; the user drives the
  fast-forward push of `origin/NativeQueryOngoing` (`6ae61ac` → new tip).
