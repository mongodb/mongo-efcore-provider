# EF-411 scoping spike — the sole-cause tranche, re-derived

Spike, not implementation. No `src/` changes. Branch: `NativeQueryOngoing` @ `f7ad384a` (clean).

## 0. Why re-derive at all

EF-411's own description says: *"The sole-cause partition is not additive and is inflated by every
inner-node group... Re-derive rather than restate before scheduling."* The ticket's 82/84/42/40/34
breakdown was measured on **2026-08-07/08**, against a tree that is now ~2.5 weeks and dozens of
slices behind `NativeQueryOngoing`'s tip. Critically, a large fraction of intervening work — the
whole EF-347 sub-project (set-ops A→C1/C2, projected/bare Distinct, quantifiers, computed leaves),
EF-359 (filtered owned-collection Count), EF-405 (bare-projection tier 2), and the 14-ticket
small-feature-gap close on 2026-08-26 — landed **directly inside** three of EF-411's five named
areas (scalar-aggregate, Distinct, set operations). The headline finding of this spike is that the
282-case estimate is now substantially stale, not merely imprecisely partitioned.

## 1. Method

Full solution build (`Debug EF10`), then the standard two-sweep subtraction against the
specification suite (`docs/native-query-status-EF-322.md` §7.4/§9.0 method), both `MONGODB_URI`
and `ATLAS_URI` unset (isolated atlas-local container).

- `Native` (default): **4593 passed / 0 failed / 17 skipped** — MEASURED, byte-identical to every
  prior baseline in this file back to 2026-08-06. No `AssertMql` baseline moved.
- `MONGODB_EF_NATIVE_ONLY=1` (`NativeOnly`): **2507 passed / 2086 failed / 17 skipped** — MEASURED.
  This is the current re-derived denominator for "what still needs driver-LINQ", matching
  `docs/native-query-status-EF-322.md` line 1339's expectation ("2427 → 2507").

I did **not** rebuild the `[CallerMemberName]`/decline-site instrumentation the original spikes
used (`docs/native-query-status-EF-322.md` §9.0) — that is a throwaway-worktree, multi-hour
exercise, and the failure messages on this tree are almost entirely the two generic strings
(`"Query is not natively representable..."` / `"Query projects a non-entity result..."`, 789+628 of
the 2086), which carry no decline-site attribution on their own. Keyword-bucketing the 2086 failing
test **names** was tried and abandoned as unreliable: e.g. 224 names contain "GroupBy", 158 contain
"Join", 468 contain "Include" — these overlap almost totally with the already-separately-tracked
EF-392/EF-393 gaps, so a name-keyword count cannot isolate EF-411's five areas from those two
without exactly the per-site instrumentation this spike is skipping. **Do not reuse the raw keyword
counts anywhere — they are recorded in scratch only, not in this file, because they are not
attributable.**

Instead, each of the five named areas was re-derived from **source** — reading the actual gate/binder
code and its own doc-comments in `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md` (which this
branch keeps meticulously current, including MEASURED/INFERRED/UNVERIFIED tags per shape) — and
cross-checked against a handful of targeted probe queries. This is qualitative, not a case count,
and is flagged as such throughout.

## 2. Area-by-area re-derivation

### 2a. Scalar-aggregate binder — LARGELY ALREADY CLOSED

`NativeCardinalityBinder` (`TryBindReducer`/`TryBindAggregate`) already ships native
`First`/`FirstOrDefault`/`Single`/`SingleOrDefault` reducers and
`Count`/`LongCount`/`Any`/`All`/`Sum`/`Min`/`Max`/`Average` aggregates, including predicate-injecting
forms after `Take`/`Skip` (EF-347 Task 3) and `All`/conjunctive-`All` via the negator (EF-322,
closing EF-335). What remains, per the area's own `AGENTS.md` note ("Scalar cardinality (EF-SP4)"):

- `Contains`/`ElementAt`/`Last` — **no native form exists**, `TranslateElementAtOrDefault` /
  `TranslateLastOrDefault` unconditionally return `null` (confirmed by reading
  `MongoQueryableMethodTranslatingExpressionVisitor.cs`). These overlap with bucket 2d
  ("no-binder operators") below — same code shape, same decline mechanism.
- A **computed** aggregate selector (`Sum(x => x.V * 2)`) — the selector must be a plain member
  access; `NativeGroupByBinder.IsGroupingSource`/accumulator binding rejects anything else.
- Reducer/aggregate **streaming** — explicitly carved out as **EF-414** (SP7 Phase 2), not part of
  EF-411's scope.

**Re-derived verdict: this area's genuinely-open slice is narrow** — `ElementAt`/`Last` (no binder
at all) and a computed-selector aggregate. The 82-case estimate almost certainly counted cases now
closed by EF-347/EF-SP4 landing after the estimate was taken. UNVERIFIED exact count; INFERRED
narrow from source reading.

### 2b. Distinct — CLOSED BY DESIGN, not a gap

Per the "Projected `Distinct`" and bare-projection-boundary notes: projected `Distinct` is native
(EF-347 slice 1), and **bare-scalar and whole-entity `Distinct` are declined *deliberately***
(`IsBareProjection` conjunct on `TryBindDistinctFromProjection`) — binding either would flip `Route`
to `GroupBy` and revert the bare alias to `null` **after the emit side has committed**, which was
MEASURED to produce an `ArgumentException` under `Native`. This is documented as "a measured
correctness narrowing", not an open gap: opening it is a known regression, already tried and
reverted once (see the "BOTH NARROWINGS ARE LOAD-BEARING" paragraph in `Query/AGENTS.md`).

**Re-derived verdict: Distinct is essentially DONE for this stream.** The 84-case (64 sole-cause)
estimate is stale — most of it was closed by EF-347 slice 1 landing days after the estimate. What's
left (bare/whole-entity Distinct) is a **deliberate decline**, not a scheduling candidate.

### 2c. Set operations — COMPLETE per the area's own AGENTS.md

The AGENTS.md note is explicit: *"With slice C1 done, the set-ops decomposition is now COMPLETE:
A (whole-entity terminal, Intersect/Except) → B (post-composition, all four) → C1 (projected
operands, all four) / C2 (trailing projection, all four)."* Remaining declines (bare-scalar/entity-ref
operand, mixed operand pairs, chained set op, `GroupBy`/`OfType`/`SelectMany` after a set op) are
each individually documented as **intentional terminal-scope boundaries**, not oversights — e.g. a
chained `Union.Union` is declined at `TryTranslateSetOperation`'s own scope gate "not a gap
discovered later... the intentional remaining terminal-only scope."

**Re-derived verdict: this area is DONE.** The 42-case estimate predates slices B/C1/C2, all of
which shipped after 2026-08-07. Re-scheduling this bucket would mean re-opening declines the branch
has already measured and rejected as unsafe (the `IsBareProjection`/`OperandsProjected` guards
exist precisely because widening them caused live silent-wrong-data bugs).

### 2d. No-binder operators — the one area that's still genuinely open

Confirmed by reading the QMTEV overrides directly: `TranslateReverse`, `TranslateSkipWhile`,
`TranslateTakeWhile`, `TranslateElementAtOrDefault`, `TranslateLastOrDefault`,
`TranslateDefaultIfEmpty` all **unconditionally `=> null`** — no binder was ever written for any of
these. This is a real, live, narrow-and-well-defined gap: six LINQ operators with zero native
representation, each falling back to driver-LINQ (correctly, presumably — not verified here) and
declining cleanly under `NativeOnly`. `Zip`/`Chunk`/`Append`/`Prepend`/`SequenceEqual` were not
found as QMTEV overrides at all (grep found no matches) — EF Core's base
`QueryableMethodTranslatingExpressionVisitor` likely doesn't route these through the same override
surface, or this provider doesn't implement the override at all; UNVERIFIED which.

**Re-derived verdict: this is the one area of the five that plausibly still matches its ticket-time
size**, because nothing in the intervening EF-347/EF-359/EF-405 work touched any of these six
operators — they are structurally unrelated (no shared code path with computed-projection, set-op,
or Distinct work). 40-case estimate: plausible, UNVERIFIED.

### 2e. Post-terminal guards — mostly intentional invariants, not a coverage gap

The `HasTerminalOperator` family (`IsGroupBy || IsDistinct || IsSetOp || Grouping != null ||
UnwindSource != null`) is a **correctness mechanism**, documented at length as an "invariant: EVERY
post-group/post-terminal operator entry point must gate on..." — its declines are deliberate
(prevents e.g. a post-group `Where` resolving against the wrong entity and silently returning wrong
data). EF-408 (closed 2026-08-26) already closed the two known unverified gaps in this family
(the TPH derived-type and differing-entity-type reserved-name cases). What's left is documented as
"the intentional remaining terminal-only scope" in the same places set-ops declines are.

**Re-derived verdict: little to no real headroom here.** The 34-case (22 sole-cause) estimate is
almost certainly the same population set-ops slice B/C1/C2 already absorbed (post-terminal
composition after Union/Concat/Intersect/Except), which is why it shows up as "co-blocked on stream
2" cases in the original merge-plan doc (line 133: "co-blocked on stream 2 (set ops 32, Distinct 26,
aggregate 4)"). Those numbers are themselves now stale for the same reason as 2b/2c above.

## 3. EF-392 (joins) overlap — none found

None of the five areas' genuine remaining gaps (2a's `ElementAt`/`Last`/computed-selector, 2d's six
no-binder operators) touch join/lookup machinery at all — they operate on a single collection with
no `$lookup` involved. The declines in 2b/2c/2e are terminal-scope guards, unrelated to
cross-collection navigation. **No file-level or code-path overlap with EF-392's decline sites**
(`NativeSlotPopulator.PopulateNativeSlots:107/118`, `TranslateSelect:280`, the `$lookup` hard throw,
`TranslateSelect:230`, or the Join/GroupJoin `Route == Fallback` case) was found. Safe to work in
parallel with an EF-392 agent in another clone.

## 4. Recommendation

**Do not schedule EF-411 as a 5-slice, 282-case tranche — re-scope the ticket first.** Three of its
five named areas (Distinct, set operations, and most of scalar-aggregate) are already closed or
deliberately declined by work that landed after the ticket was filed. The only area with a plausible,
still-open, well-scoped gap is **2d, the no-binder operators** — six LINQ methods
(`Reverse`/`SkipWhile`/`TakeWhile`/`ElementAtOrDefault`/`LastOrDefault`/`DefaultIfEmpty`) with zero
native binder. That is a single, narrow, independent slice, cheap to scope precisely (a
prototype-A/B measurement, per this branch's own now-repeated "size a slice by a prototype, never by
the decomposition table" lesson), and it does not conflict with EF-392's concurrent work.

Recommended order if EF-411 continues to be worked:

1. **No-binder operators (2d)** — the one real remaining gap. Needs its own case-count
   re-measurement via a prototype A/B, not the cited 40.
2. **Scalar-aggregate residual (2a)** — `ElementAt`/`Last`/computed-selector aggregate — small,
   shares the `NativeCardinalityBinder` file with (1)'s `ElementAtOrDefault`/`LastOrDefault`, so
   scoping them together may be cheaper than two separate slices.
3. Close EF-411 (or split it into a much smaller replacement ticket) rather than scheduling 2b/2c/2e
   as work — they are DONE or deliberately-declined, and re-opening any of the declines named in
   §2b/§2c risks reintroducing a measured regression this branch has already fixed once.

## 5. What this spike did NOT do

- Did not re-measure exact case counts for 2a/2d via instrumentation or a full prototype — only
  qualitative source-level re-derivation plus the two-sweep total. Anyone scheduling (1) or (2)
  above should run a small standalone prototype (as EF-403/EF-405/etc. did) before writing a design
  doc, per this branch's own repeated "sole-cause is a leverage proxy, not a guarantee" lesson.
- Did not verify whether `Zip`/`Chunk`/`Append`/`Prepend`/`SequenceEqual` are reachable via this
  provider's LINQ surface at all (no QMTEV override was found for any of them) — UNVERIFIED whether
  EF Core even routes them here, or whether they're simply never called by any test in the corpus.
