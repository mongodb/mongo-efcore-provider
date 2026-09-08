# EF-411 no-binder-operators prototype — scoping, not implementation

Spike only. No `src/` changes. Branch: `NativeQueryOngoing` @ `60df72a5` (clean).

## 0. Directive

The EF-411 scoping spike (`2026-08-26-ef411-scoping-spike.md`) found the one genuinely-open
area left in EF-411 is six zero-binder QMTEV overrides — `Reverse`, `SkipWhile`, `TakeWhile`,
`ElementAtOrDefault`, `LastOrDefault`, `DefaultIfEmpty` (all confirmed `=> null` by reading
`MongoQueryableMethodTranslatingExpressionVisitor.cs`) — plus the closely-related scalar-aggregate
residual `ElementAt`/`Last` (no QMTEV override exists for either; EF Core normalizes both to their
`OrDefault` sibling before this provider ever sees them — confirmed by grep, no `TranslateElementAt`/
`TranslateLast` override exists in the file). This spike sizes that work via a prototype/design sketch
and a corpus measurement, per this branch's repeated "size a slice by a prototype, never by the
decomposition table" lesson.

## 1. Method

Read `Query/AGENTS.md` for the native gate architecture, `HasTerminalOperator` guard family, and
`MongoAggregationExpressionRenderer.CanRender` gaps before sketching anything. Confirmed all six
target overrides are unconditional `=> null` (`MongoQueryableMethodTranslatingExpressionVisitor.cs`
lines 1419, 1422, 2210, 2224, 2430, 2436). Did **not** build a throwaway prototype pipeline or run a
live server probe — the corpus measurement below made that unnecessary: there is essentially nothing
in the test corpus to prototype against for four of the six shapes.

## 2. Corpus measurement — MEASURED, and it is the headline finding

Grepped `*.cs` under both `SpecificationTests` and `FunctionalTests` (excluding `bin/`/`TestResults/`
noise, which pollutes a naive grep with hundreds of false hits from compiled XML doc comments and
`.trx` result files):

| Operator | Spec suite (.cs) | Functional suite (.cs) |
|---|---:|---:|
| `Reverse()` | 1 | 4 |
| `SkipWhile` | 0 | 0 |
| `TakeWhile` | 0 | 0 |
| `ElementAtOrDefault` | 0 | 0 |
| `ElementAt` | 0 | 0 |
| `LastOrDefault` | 1 | 0 |
| `Last(` (bare) | 1 | 8 |
| `DefaultIfEmpty` | 1 (a comment) | 6 |

**`SkipWhile`, `TakeWhile`, `ElementAt` and `ElementAtOrDefault` have ZERO occurrences anywhere in
this repo's test suites.** The ticket's cited "40 cases" for this bucket cannot be reconciled against
this corpus — there is no existing test that currently declines through any of these four operators,
so implementing native support for them would flip **zero** existing `NativeOnly` failures. Any case
count for these four would have to come from tests written from scratch, not from unlocking existing
coverage. This is a different and worse shortfall than the previous four slices' "cited > measured"
pattern (A2 34/44, A5 0/36, A1 28/56, A4 6/28) — those had a real but smaller yield; these four have
literally nothing in the corpus to yield.

`Reverse`, `LastOrDefault`/`Last`, and `DefaultIfEmpty` DO have real (if small) functional-test
presence — roughly 5, 9, and 6 call sites respectively — and (UNVERIFIED here, not re-run) presumably
pass today via the driver-LINQ fallback, since nothing in the classification doc or status doc lists
them as a known crash/decline family.

## 3. Design sketch per operator

- **`Reverse()`** — MongoDB has no "reverse row order" pipeline stage; `$reverseArray` operates on an
  array field, not on the document stream. The only sound native form is: if the select already has a
  trailing `MongoSortOp` (an explicit `OrderBy`/`ThenBy` chain), flip every ordering's direction
  (ascending↔descending) — this is the exact complement of the original order, so it's an exact
  translation, not an approximation. If there is **no** explicit order, `Reverse()` over an
  otherwise-unordered LINQ source has undefined result order to begin with (same as any RDBMS
  provider), so the honest move is to decline rather than invent a `$natural: -1` sort (unreliable,
  and arguably a correctness hazard if ever relied on). **Feasible, narrow, cheap** — but only for the
  ordered case, and the corpus has no test distinguishing ordered-then-Reverse from bare Reverse.

- **`SkipWhile`/`TakeWhile`** — no direct aggregation-pipeline equivalent. Doing this natively needs a
  boundary-index computation (e.g. `$setWindowFields` with a running flag for "predicate still true"
  reduced to a first-false rank, then `$match`/`$filter` on that rank) — real design work, a new IR
  shape, a new renderer arm, and MongoDB-version sensitivity (`$setWindowFields` requires 5.0+, and
  this codebase already declined `$sortArray` on <5.2 in EF-405 for exactly this kind of
  version-gating concern). Given zero corpus presence, this is not a slice to build now — **recommend
  declining and re-ticketing separately**, explicitly framed as "no sane native form without
  `$setWindowFields`", not folded into a bag of "cheap remaining work."

- **`ElementAtOrDefault`/`ElementAt`** — mechanically the easiest of the six: `$skip: index` +
  `$limit: 1`, the same shape `NativeCardinalityBinder.TryBindReducer` already uses for
  `First`/`FirstOrDefault`. `ElementAt` (non-default) needs the existing paging validation extended to
  throw for a negative index (mirroring `MongoPipelineFactory.Build`'s existing `$skip ≥ 0` guard) and
  a materialization-time throw-if-absent for out-of-range, matching BCL semantics. **Feasible and
  cheap to build** — but zero corpus impact per §2, so it would ship with no regression net beyond
  hand-written tests.

- **`LastOrDefault`/`Last`** — same ordering caveat as `Reverse`: only sound with an explicit prior
  `MongoSortOp`, via the identical flip-direction-then-`$limit:1` trick, reusing the ordinary
  `FirstOrDefault` reducer machinery once the order is flipped. Unordered `Last()` is undefined-order
  in LINQ generally and should decline, matching `Reverse`'s policy. **Feasible**, and this is the one
  operator in the six with a real (if modest) corpus presence (~9 call sites).

- **`DefaultIfEmpty()`** (bare, root-level — not the `SelectMany`/`GroupJoin` flatten use, which is a
  different, already-handled code path per the Query AGENTS.md SelectMany notes) — not a pipeline
  concern at all; it's a materialization-time concern: if the cursor yields zero documents, materialize
  one default-valued `T` instead of an empty sequence. This is architecturally closer to
  `MongoEmptyAggregateBehavior` (the existing empty-input contract for scalar aggregates) than to a
  new pipeline stage — plausibly a new `EmptyBehavior`-style hook at the shaper/materialization layer,
  not the lowerer/renderer. **Feasible but is NOT a query-translation change** — it would touch the
  compiling visitor / shaper, a different part of the pipeline than the other five, and deserves its
  own design pass rather than being bundled as "the same kind of fix."

## 4. Recommendation

**Do not schedule this as one six-operator slice.** The six no-binder operators split into three
tiers by architecture and corpus impact, not by superficial similarity:

1. **Zero corpus impact, cheap to build:** `ElementAt`/`ElementAtOrDefault`. Build only if the goal is
   raw capability-completeness (e.g. for a future hand-written regression suite), not to move any
   existing coverage number.
2. **Small real corpus, cheap to build for the ordered case, needs a decline path for the unordered
   case:** `Reverse`, `LastOrDefault`/`Last`. The best candidate for an actual slice, since it is the
   only pairing with both a real (if small) test footprint and a clean, narrow design.
3. **Decline and re-ticket separately, do not build now:** `SkipWhile`/`TakeWhile` (no sane native
   form without new pipeline machinery, zero corpus presence to justify the investment) and
   `DefaultIfEmpty` (belongs to the materialization layer, not the translator/lowerer/renderer this
   ticket's other five operators share — a different kind of change entirely).

**Restate the scalar-aggregate residual (`ElementAt`/`Last`, non-`OrDefault`):** these never reach the
QMTEV at all — EF Core normalizes them upstream to the `OrDefault` forms — so there is nothing
separate to implement for them once the `OrDefault` siblings go native; they were never an independent
work item, just EF-411's own imprecise naming of the same capability.

**Overall verdict for EF-411:** even the narrowest defensible reading of "the one remaining open area"
turns out to be mostly a capability with no test corpus behind it. If EF-411 continues, the only
piece worth a real design doc and implementation slice is `Reverse`/`LastOrDefault`/`Last` for the
ordered case. Everything else should either be declined-and-ticketed on its own (`SkipWhile`/
`TakeWhile`, `DefaultIfEmpty`) or built opportunistically with no expectation of moving any existing
number (`ElementAt`/`ElementAtOrDefault`).

## 5. What this spike did NOT do

- Did not build or run any prototype code — the corpus measurement made a code-level A/B unnecessary
  for four of the six operators (nothing to measure a translation of).
- Did not verify whether the ~5/~9/~6 existing `Reverse`/`Last(OrDefault)`/`DefaultIfEmpty` call sites
  currently pass under the default `Native` mode via fallback, or whether any of them already exercise
  a decline; that would be Task 0 of an actual implementation slice, not this scoping pass.
- Did not investigate `$setWindowFields` MongoDB-version availability in this codebase's supported
  server range, needed before any real `SkipWhile`/`TakeWhile` design.
