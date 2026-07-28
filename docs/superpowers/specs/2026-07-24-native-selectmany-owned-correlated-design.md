# Native owned correlated-beyond-outer inner-filter `SelectMany` (EF-347)

## Summary

Make an embedded **owned-collection** `SelectMany` whose inner `.Where(pred)` references the
**outer owner** beyond the owner/element pair go **native**, across all owned result shapes.
Example (previously hard-failed every mode):

```csharp
db.Entities.SelectMany(o => o.Items.Where(i => i.Name == o.Name), (o, i) => new { o.Name, i.Price });
```

This is the fast-follow to the just-shipped **reference** correlated-beyond-FK slice (`933eb94`).
It is a near-exact mirror: it reuses the optional two-scope mode of `MongoExpressionTranslator`
(built in that slice) and flips the same kind of `ReferencesParameter` decline into a router — but
in the OWNED path (`NativeSelectManyBinder.TryBuildOwnedInnerFilter`), which differs from the
reference path in that an owned collection nav-expands to a **bare member access** (`o.Items`), NOT
an FK-correlated subquery, so there is no FK correlation to isolate and no
`NativeCorrelationMatcher`/`TrySplitCorrelation` involved.

## Scope

**In scope — goes native:** an owned-collection `SelectMany` whose inner filter references the outer
owner (`o.Items.Where(i => i.Name == o.Name)`), for **all** owned result shapes the owned-filtered-
inner slice supports:
- projected inner-`Select` form (`o.Items.Where(pred).Select(i => new {o.Name, i.Price})`),
- projected explicit-result-selector / query-syntax form (`SelectMany(o => o.Items.Where(pred), (o,i) => new {…})`),
- bare-whole-element form (`SelectMany(o => o.Items.Where(pred))` / `select i`, requires `AsNoTracking()` like every bare-owned-element shape),
- stacked `.Where(p1).Where(p2)` (ANDed), and mixed conjuncts (`i.Active && i.Name == o.Name`).

Full predicate breadth (all six field-to-field comparisons + arithmetic), inherited from the two-
scope translator + renderer.

**Out of scope / unchanged (still deferred):** a nested owned `SelectMany` (an owned collection
inside another owned collection); a computed projection leaf (declines the ordinary way); the
reference-form cases are already done (prior slices).

## Spike (done)

Findings: `.superpowers/sdd/EF-347-owned-correlated-spike.md`. Two questions resolved, EF8/9/10
agreeing:

- **Q1 — tree shape: CONFIRMED.** For `o.Items.Where(i => i.Name == o.Name)`,
  `PeelOwnedInnerWhere` peels the `Where` layer and `TryBuildOwnedInnerFilter` sees a user predicate
  `i => (i.Name == o.Name)` whose outer reference `o.Name` roots on a `ParameterExpression` that is
  `ReferenceEquals` to the outer `SelectMany` parameter (dump: `OWNED-USERPRED: body=(o0.Name ==
  o.Name) | refsOuter=True`). Identity-based two-scope routing is viable exactly as in the reference
  slice — no new peel/recognition work, just turning the decline into a route.
- **Q2 — oracle disposition: UNIFORM HARD-FAIL (the important, non-obvious result).** For a
  correlated-beyond-outer filter, the driver-LINQ oracle disappears **regardless of projection
  shape**: all three probes — outer-referencing projection (`(o,i) => new {o.Name, i.Price}`),
  inner-only projection (`(o,i) => new {X = i.Price}`), and bare-whole-element (`select i`) — throw
  `InvalidOperationException` ("could not be translated") on the collection selector itself, before
  any projection is considered, on EF8/EF9/EF10 alike. **This reverses the prior owned-filtered-inner
  finding** that an inner-only projection has an oracle: that held only for a *non-correlated* inner
  filter. Once the inner filter is correlated-beyond-outer, the correlation — not the projection — is
  what breaks driver-LINQ, so there is no oracle for any shape.

**Consequence:** every correlated-beyond-outer owned shape is native-only. A decline hard-fails in
every mode; a native success is proven via `MongoQueryMode.NativeOnly` succeeding + an expected in-
memory result set, never `Native == DriverLinq` parity. There is **no** graceful-fallback disposition
to reason about (unlike the owned-filtered-inner slice, whose inner-only-projection shapes fell back
gracefully) — this is simpler and uniform.

## Approach (mirror via `TryBuildOwnedInnerFilter`, single routing site)

`TryBuildOwnedInnerFilter` (NativeSelectManyBinder.cs, ~line 533) is the one place both owned binders
(`TryBind` inner-`Select` form, `TryBindBareNavUnwind` explicit/query-syntax/bare forms) translate
peeled inner-filter layers. Today its per-layer loop declines any layer referencing the outer param:

```csharp
if (userPredicate.Parameters.Count != 1
    || ReferencesParameter(userPredicate.Body, outerParam)      // <-- correlated-beyond-outer decline
    || !innerTranslator.TryTranslate(userPredicate.Body, out var expr))
    return false;
var prefixed = MongoFieldPrefixRewriter.Rewrite(expr!, unwindPath);
```

Flip the `ReferencesParameter` branch from *decline* to *route*, mirroring
`TryTranslateReferenceFilterLayer` from the reference slice:

- A layer that **references the outer param** → translate the whole layer with the two-scope
  `MongoExpressionTranslator(innerEntityType, outerParam, outerEntityType, unwindPath)`; the inner
  members come out `unwindPath`-prefixed (`Items.Name`) and the outer members at document root
  (`Name`), so the result is used **directly** (NOT blanket-prefixed by `MongoFieldPrefixRewriter`).
- A layer that **does not** → the existing `innerTranslator.TryTranslate` + `MongoFieldPrefixRewriter.Rewrite(expr, unwindPath)` path, unchanged.
- Either translation failing → return `false` with no mutation (no-partial-mutation invariant).

The signature gains one parameter, `IEntityType outerEntityType`, threaded from both owned binders
(each holds `mongoQ`, so passes `mongoQuery.CollectionExpression.EntityType` — the owner entity type;
for a single-level owned `SelectMany` the owner IS the query root). Everything else — result-shape
machinery, both binders' projection/whole-element handling — is untouched; the correlated-filter
support reaches all owned shapes through this one shared site.

**Why not a separate helper like the reference slice's `TryTranslateReferenceFilterLayer`?** The
owned per-layer loop already lives inside `TryBuildOwnedInnerFilter`; the minimal change is to add
the two-scope branch inline (or extract a tiny local helper) rather than restructure. The plan
resolves the exact shape; either way the routing logic is identical to the reference slice's.

**Lowerer / renderer — unchanged.** The owned `$unwind` (in place) puts the element under the
`unwindPath` (`Items`) and keeps the owner's fields at document root, so the correlated `$expr`
compares `$Items.Name` to `$Name`. `MongoSelectLowerer.Lower`'s `UnwindSource` block already emits
`MongoUnwindSource.Filter` as a `$match` after the owned `$unwind` and before `$replaceRoot`/
`$project`, kind-agnostic; the field-to-field subtree renders as `$expr` via the existing
`MongoQueryLanguageRenderer` per-subtree dialect mixing (inner-only conjuncts stay plain `$match`
index-first terms).

**MQL:** `$unwind(Items, includeArrayIndex) → $match({$and:[<inner-only>, {$expr:{$eq:["$Items.Name","$Name"]}}]}) → $replaceRoot|$project`.

## Decline behavior & no oracle

Per the spike, a correlated-beyond-outer owned `SelectMany` has **no driver-LINQ oracle for any
shape**, so every decline — an unsupported correlated operator (`i.Name.ToUpper() == o.Name`), or a
correlated predicate the two-scope translation cannot scope — **hard-fails in every mode**
(`Native`/`DriverLinq`/`NativeOnly`). Native successes are proven via `NativeOnly` + expected in-
memory result set.

## Known interaction — EF-221 (mismatched-CLR-type equality)

The correlated conjunct is a field-to-field comparison, so it inherits the provider's existing value-
equality semantics for mismatched-CLR-type comparisons (tracked as `EF-221`; see
`docs/failing-spec-tests.md` and `providerconvert.md`) — the same behavior already produced for
single-scope field-to-field comparisons and for the reference correlated slice. **No new guard.**
(The owned fixture uses `string`/`decimal`, so no mismatch actually arises in the tests.)

## Not a breaking change

All touched types are `internal`. Hard-fail → native for a previously-unsupported shape; results for
supported shapes unchanged; emitted MQL is not contract. `TryBuildOwnedInnerFilter`'s new parameter
is on an internal method. Identical EF8/9/10 (no `#if`).

## Files (anticipated)

- `src/…/Query/NativeTranslation/NativeSelectManyBinder.cs` — route the correlated layer in
  `TryBuildOwnedInnerFilter` (add `outerEntityType` param; two-scope branch); thread `outerEntityType`
  from `TryBind` and `TryBindBareNavUnwind`.
- `src/…/Query/AGENTS.md` — as-built note (owned correlated-beyond-outer now native; uniform hard-
  fail, no oracle for any shape — correcting the owned-filtered-inner note's projection-dependent
  oracle claim as it applies to the correlated case; the m6 owned-note "FILTERED … out of scope"
  staleness can be cleaned up here too since we're editing the owned notes).
- `tests/…/FunctionalTests/Query/NativeSelectManyTests.cs` — convert
  `Filtered_owned_correlated_beyond_outer_hard_fails_in_every_mode` → native success; add shadow /
  mixed / stacked / bare-whole-element / projected-explicit / zero-match / parametrized-outer /
  MQL `$expr` coverage; add a discriminating owner (Name matching an item's Name).
- `tests/…/UnitTests/Query/NativeTranslation/NativeSelectManyBinderTests.cs` — owned correlated
  binder coverage (routes to `$expr` filter; shadow; unsupported-operator declines with no mutation).

## Verification

- Full 3-version `/test-all` (EF8/EF9/EF10) GREEN 0-fail before squash — controller runs it
  foreground, summing all three per-assembly blocks.
- `MongoQueryMode.NativeOnly` spec sweep — no regressions vs the `933eb94` baseline.
- Subagent-driven development, one task at a time, **stop for review after every task**.
- Final opus whole-branch review; squash to one commit above `933eb94` with a presquash backup; the
  user drives the fast-forward push of `origin/NativeQueryOngoing` (`933eb94` → new tip).
