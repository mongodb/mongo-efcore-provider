# Benchmark Gaps: Wide Dispatch, Nested Streaming, Include Beside Owned — Implementation Plan

> **For agentic workers:** execute task-by-task; steps use checkbox (`- [ ]`) syntax. Do the steps in order: Step 2's
> payoff is only visible after Step 3 lands, and Step 1 is independent and the safest.

**Goal:** close the three gaps the 2026-10-14 driver-LINQ-vs-native benchmark exposed, so that **no extended benchmark
shape has `EF_Native` slower than `EF_DriverLinq`, and `ComplexQuery` goes native end to end**.

| # | Gap | Today (EF10, N as in benchmark) | Target |
|---|---|---|---|
| 1 | `WideWholeEntity` native is *slower* than driver-LINQ | native 974 ms vs driver-LINQ 622 ms vs DriverOnly 224 ms (20 interleaved runs; benchmark single run: 1131 / 713 / 233) | ≈ 226 ms (≈ DriverOnly) |
| 2 | `NestedWholeEntity` native allocates the same as driver-LINQ | 346.98 MB vs 346.99 MB (floor 37.9 MB) | ≈ 70 MB (≈ 1.8× floor; **extrapolated** from a 2,000-row probe) |
| 3 | `ComplexQuery` (`Orders.Include(o => o.Account)` + owned filter/sort + paging) silently falls back | 57.77 ms / 56.72 MB, identical to driver-LINQ; `--verify-native` reports FALLBACK | native under `NativeOnly`; allocations fall once Step 2 is in |

**Source of truth for the evidence:** `benchmarks/MongoDB.EntityFrameworkCore.Benchmarks/results/2026-10-14-driverlinq-vs-native.md`
(benchmark numbers, committed as `ce2a9c01` on `EF-322c`) and this plan. The three investigations that produced the
root causes below were read-only experiments on **scratch copies** of the source; their prototype diffs are preserved in
`docs/superpowers/findings/2026-10-14-benchmark-gaps/` (see "Prototype artifacts"). **They are references, not code to
commit** (one carries debug `Console.WriteLine`s). None of the prototypes was run against the unit, functional or spec
suites, and only EF10 was exercised.

**Architecture note:** the native path decides two things independently. (a) *Routing*: whether the query is natively
representable (`Select.Route` → `ClassifyNativeDisposition` → `TryBuildNativeFactory`, which throws under `NativeOnly`
and falls back to the driver-LINQ bridge under `Native`). (b) *Materialization*: whether the entity can use the SP7
one-pass streaming materializer (`StreamingEligibility.IsEligible` → `MongoStreamingEntityMaterializerRewriter`) or the
older DOM shaper (`BsonDocument` + `MongoProjectionBindingRemovingExpressionVisitor` + `BsonBinding`, the same machinery
driver-LINQ uses). Gap 1 is in the streaming rewriter's generated code. Gap 2 is an eligibility guard on (b). Gap 3 is a
recognizer on (a). A query can pass `NativeOnly` yet still take the DOM shaper, which is why Gap 2 shows no allocation win.

## Global constraints

Same as `docs/superpowers/plans/2026-10-01-native-linq-parity.md` § Global Constraints. The ones that bite here:

- **Branch `EF-322c`** (check `git branch --show-current` before each task; it can change under a session). No git
  worktrees. Commit subjects start `EF-322: `. **Never force-push.** The repo's rule: finish a slice by squashing to one
  commit, keeping a `-presquash` backup branch, fast-forwarding onto `EF-322c`, pushing without force. Pull/rebase first
  if the remote moved (it did during the previous push).
- **Tests run with `MONGODB_URI` and `ATLAS_URI` unset** (per-process Atlas-local containers; needs Docker). Use `/test-all`
  for EF8/EF9/EF10. **A change to a gate, allow-list, eligibility predicate or recognizer needs a FULL-SUITE run (all test
  projects, all three EF versions), not per-class runs** (EF-436 lesson). Steps 2 and 3 are such changes.
- **Every new guard/fix test is proven to discriminate by mutation**: revert the one-line fix, see the test fail, restore.
  A green run proves nothing about branch coverage.
- **The gate must call the fix's predicate, never restate it** (Query/AGENTS.md; EF-405 lesson, found 3×).
- New native shapes are tested in three modes: `NativeOnly` (goes native), `Native` (same results), explicit
  `UseQueryMode(MongoQueryMode.DriverLinq)` (oracle). Use `NativeModeAssert.NativeAndParity`
  (`tests/MongoDB.EntityFrameworkCore.FunctionalTests/Utilities/NativeModeAssert.cs`) where the driver oracle is correct;
  hand-computed expectations over **raw `BsonDocument` seeds** (missing / explicit null / empty / populated) where it is
  not, because materialization normalizes missing owned collections to `[]` (EF-358).
- **Preserve file BOMs and line endings.** Several `src/` files are CRLF — edit with the Edit tool, not scripts that
  assume `\n`. Version-conditional code uses `#if EF8 || EF9` / `#if !EF8`.
- Spec MQL baselines are field-order-sensitive; regenerate with `EF_TEST_REWRITE_BASELINES=1` scoped by `--filter`, then
  `git diff`, rebuild, rerun. (See `tests/MongoDB.EntityFrameworkCore.SpecificationTests/AGENTS.md`.)
- Breaking-change rubric (root AGENTS.md): which path a query takes and the exact MQL are **not** breaks. Result changes
  and new refusals of supported operations are. Record any behaviour change in `BREAKING-CHANGES.md`.
- Subagents: unique scratch subdir each; benchmarks/timing runs only when nothing else is loading the machine.

## Benchmark reproduction (use for every "before/after" number)

```bash
docker run -d --rm --name bench-mongo -p 27555:27017 mongodb/mongodb-atlas-local:8   # replica set; needs directConnection
export MONGODB_URI="mongodb://localhost:27555/?directConnection=true"                # for the benchmark process ONLY
dotnet build benchmarks/MongoDB.EntityFrameworkCore.Benchmarks -c "Release EF10"
mkdir -p /tmp/<unique-scratch> && cd /tmp/<unique-scratch>                            # run from a scratch dir, not the repo
DLL="<repo>/benchmarks/MongoDB.EntityFrameworkCore.Benchmarks/bin/Release EF10/net10.0/MongoDB.EntityFrameworkCore.Benchmarks.dll"
dotnet "$DLL"                  # headline set (~2.5 min)
dotnet "$DLL" --extended       # extended set (~5 min)
dotnet "$DLL" --verify-native  # NativeOnly check per shape (prints NATIVE OK / FALLBACK)
docker stop bench-mongo
```

Allocation columns are deterministic; timing is directional unless the machine is idle. The atlas-local container is a
different server from the June local-mongod baselines, so **compare configs within one run only**. For Step 1 the
investigation used a stricter harness (20 interleaved iterations, `GC.Collect` before each, one warm-up call) — the
numbers above came from that; its program is not in the repo (it lived in a scratch dir), so reproduce with a Wide-only
`--filter` run of `ExtendedBenchmarks` or write a small equivalent.

---

## Step 1 — Wide-entity dispatch: replace the per-element `string.Equals` chain with a `switch`

**Root cause (verified by experiment; no profiler was available, so the per-call cost estimate is inference).**
`src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoStreamingEntityMaterializerRewriter.cs`,
`BuildFillLoop` (~lines 344–420) generates, per document, a loop that calls `ReadName` then runs a nested chain of
`Expression.IfThenElse(string.Equals(_name, "<element>"), read, <next>)` ending in `SkipValue` (line ~347). `Dispatch`
(~lines 454–458) emits one `string.Equals` per property; the chain is assembled at ~lines 366, 375, 382, 391
(properties, owned references, lookup references, owned collections). With ~401 properties the chain averages ~200
compares per element ⇒ ~80,000 per document ⇒ ~400 M for 5,000 documents. That arithmetic matches the ~750 ms gap to
DriverOnly. JIT is ruled out (first call 1100 ms vs 974 ms steady state); transport is ~22 ms; allocation is only
104 MB. Flat entities (≤ ~10 properties) never noticed.

**Proven fix (scratch copy only):** replacing the chain with one `Expression.Switch(typeof(void), _name, defaultBody:
SkipValue, comparison: null, cases)` — each property / owned reference / lookup reference / owned collection becomes a
`SwitchCase` keyed by a string constant, bodies wrapped in `Expression.Block(typeof(void), ...)` — took native from
974 ms to **226 ms** (sd 6; DriverOnly 237 in the same run; driver-LINQ 649) and the first call from 1100 to 355 ms.
`Expression.Switch` over strings compiles to a hash / binary-search jump. Prototype diff:
`docs/superpowers/findings/2026-10-14-benchmark-gaps/step1-wide-switch-prototype.diff` (its first hunk is only a BOM
artifact of the copy — ignore it).

**Files:** only `MongoStreamingEntityMaterializerRewriter.cs`. Delete `Dispatch` and `StringEqualsMethod` if nothing else
uses them (the investigation's grep found only the four call sites above — re-verify).

### Task 1.1 — Tests first (they must pass on the *current* code, then still pass after the change)
- [ ] Add `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeWideEntityDispatchTests.cs`:
  - A 50+-property entity (generate the class by hand; keep it small enough to read). Seed **raw `BsonDocument`s** with
    elements in shuffled order, with elements **missing**, and with **unknown extra elements**; assert every property,
    in `NativeOnly`, against hand-computed values and against the `DriverLinq` oracle.
  - Owned reference, owned collection and lookup-reference documents with their elements out of order.
  - **Duplicate-name collision:** the old chain was wrapped last-first, so when two members map to the same element name
    the **last-added entry (a collection) was outermost and won**. `Expression.Switch` throws at tree-build time on
    duplicate case values. Add a test pinning the current precedence (e.g. a `[BsonElement]` collision between an owned
    navigation and a property, if the model builder allows it; if the model builder rejects it before reaching the
    rewriter, record that and instead unit-test the case-dedup helper directly).
- [ ] Run them against current code; record that they pass (they characterize existing behavior). Commit nothing yet.

### Task 1.2 — The change
- [ ] In `BuildFillLoop`, build a `List<SwitchCase>` instead of nesting. **Dedupe by element name keeping the
  last-added** (preserves old precedence). Case bodies must be `void`-typed.
- [ ] Keep the `ReadName` / `SkipValue` / null-handling structure identical; only dispatch changes.
- [ ] Remove dead `Dispatch` / `StringEqualsMethod`.
- [ ] No `#if` expected (pure `System.Linq.Expressions`); confirm EF8/EF9 build anyway.

### Task 1.3 — Verify
- [ ] **Mutation check:** temporarily reintroduce the "first wins" dedupe (or drop dedupe) and confirm the collision test
  fails; restore.
- [ ] Full suite, EF8/EF9/EF10 (`/test-all`).
- [ ] Re-run the Wide benchmark; expect ≈ 226 ms. Add a ~40-property benchmark class so a regression at *normal* width is
  visible (the 400-property case hides cost at realistic widths). Update `results/`.
- [ ] Commit `EF-322: dispatch streaming element names through a switch instead of a linear string.Equals chain`.

**Risks:** duplicate names (above); `Switch` case-body typing; ordinal/case-sensitive comparison is the same as
`string.Equals` (default ordinal) — confirm `comparison: null` keeps that. *Optional later:* an ordered-read fast path
(expect the next element at the next ordinal) could approach the ~22 ms raw floor; **not** needed — native is already at
the DriverOnly floor.

---

## Step 2 — Stream entities whose owned-collection elements have their own navigations

**Root cause (verified by probe).** `MongoShapedQueryCompilingExpressionVisitor.CompileShapedQuery`
(`src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoShapedQueryCompilingExpressionVisitor.cs`, ~lines 569–575) only
takes the streaming path when `StreamingEligibility.IsEligible(rootEntityType)`.
`src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/StreamingEligibility.cs` lines ~71–79 rejects any entity with an
owned collection whose **element type itself has navigations** (`navigation.IsCollection &&
navigation.TargetEntityType.GetNavigations().Any()`); the comment says `FindCollectionShaper` doesn't descend into a
collection element so the rewriter would throw. `Order.Lines` → `LineItem` owns `Meta` (reference) and `Discounts`
(collection), so `Order` is ineligible, falls to the DOM shaper, and allocates exactly like driver-LINQ (identical MQL
`aggregate([])`; only the output serializer differs). It is a **deliberate, conservative guard** whose stated reason is a
real limitation of the rewriter — but the rewriter's own header and `BuildCollectionElementConstructor` already
*anticipate* nested elements ("RewriteMaterializer handles recursively with the collection context threaded through for
the ordinal key"); the implementation just doesn't deliver it. Everything else about `Order` already streams (root, owned
references, nested owned references like `Shipping.Address`, flat-element owned collections, the `Account` lookup).

**Evidence (EF10, 2,000 seeded orders, `GC.GetAllocatedBytesForCurrentThread`, best of 3):**

| Case | Alloc | Per order |
|---|---|---|
| Raw driver POCO `Find().ToList()` | 7.53 MB | 3.9 KB |
| EF DriverLinq | 78.0–78.4 MB | 40.9 KB |
| EF Native / NativeOnly (today) | 68.5–69.5 MB | 36.5 KB |
| EF Native with the prototype below | **13.9 MB** | **7.3 KB** (−80%) |

Isolated shapes (unmodified repo): owned ref + nested owned ref → eligible, 12.99 MB DriverLinq → 3.01 MB native (−77%);
flat-element collection → eligible, 16.91 → 2.68 MB (−84%); collection element with an owned reference → **ineligible**,
29.96 → 29.95 MB; collection element with a nested collection → **ineligible**, 45.39 → 45.38 MB. The cliff is exactly
"owned-collection element with its own navigation". Sampled allocations for today's native Orders path are dominated by
`BsonElement[]`, `BsonDocumentReader`, `BsonDocument`, `BsonDeserializationContext`, `BsonSerializationInfo`,
`List<BsonElement>` — the DOM path.

**Open discrepancy to resolve first:** native allocated ~10 MB *less* than driver-LINQ at 2,000 rows, yet the 10,000-row
benchmark shows them equal (346.99 vs 346.98 MB). Unexplained. Task 2.1 re-measures both before touching code.

**Prototype (scratch copy of two files; EF10 only):**
`docs/superpowers/findings/2026-10-14-benchmark-gaps/step2-nested-streaming-prototype.diff` (**contains debug
`Console.WriteLine`s — do not copy them**). The two real fixes it proves:

1. **`BuildCollectionElementConstructor` (~lines 611–630).** For an element that owns further navigations, EF's navigation
   expression is *not* a `CollectionShaperExpression`; it is `Convert(<ScopeN block holding the CollectionShaper>.Select(o0
   => IncludeExpression(...)), List<T>)`. Find the `CollectionShaperExpression` under `Select.Arguments[0]` and substitute
   the lambda parameter `o0` with `shaper.InnerShaper` in `lambda.Body` (the `IncludeExpression` chain, e.g.
   `Meta>Discounts[]>entity`), then recurse through `RewriteMaterializer` as today.
2. **`ConstructionRewriter.VisitMethodCall` (~lines 927–935).** A doubly-nested element (`Discount`) reads the ordinal key
   of its **parent element** (`LineItemId`, the owned-type ordinal FK to the enclosing `LineItem`). That key has no local.
   Resolve it to the enclosing collection plan's `Counter + 1`, by walking `property.FindFirstPrincipal()` up while
   `IsOwnedTypeKey()` until an `IsOwnedTypeOrdinalKey()` property whose `DeclaringType` matches an enclosing plan's
   element type. The probe used a **static** `List<CollectionPlan>` stack — a real fix must pass the plan stack through
   `RewriteMaterializer` as a parameter (a static is not thread-safe and not re-entrant).

Prototype equivalence check: 100 orders, `JsonSerializer` of the full graph, DriverLinq vs NativeOnly identical for
`AsNoTracking` and tracking (67,481 chars each). **Not** evidence for: tracking identity of owned entities in the state
manager, required-owned-reference throws, null/missing arrays, composite synthesized keys at deeper levels, EF8/EF9.

**Files:** `StreamingEligibility.cs`, `MongoStreamingEntityMaterializerRewriter.cs` (plus tests).

### Task 2.1 — Baseline and characterization
- [ ] Re-measure `NestedWholeEntity` Native vs DriverLinq at 2,000 **and** 10,000 orders; explain (or at least pin) the
  ~10 MB discrepancy so the "after" number is attributable to this change.
- [ ] Write tests on the **current** code that pin behavior the change must preserve (they pass today):
  `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/StreamingEligibilityTests.cs` already exists — extend
  it with the nested shapes (collection element with owned reference; with nested collection; with a **non-owned**
  navigation inside an element — must stay ineligible). Functional: new
  `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeNestedOwnedCollectionStreamingTests.cs` over a model like
  the benchmark's `Order → Lines → {Meta, Discounts}`, in `NativeOnly`/`Native`/`DriverLinq`, covering: populated; empty
  array; **null** array; **missing** array; missing owned reference; **required** owned reference inside an element
  (check current throw behavior and pin it); `AsNoTracking`, tracked, and `AsNoTrackingWithIdentityResolution`; ordinal
  keys of nested owned entities (assert via `ctx.Entry(...).Property("LineItemId")`-style shadow key values where the
  model exposes them). Assert nested **data**, never just counts. Use raw `BsonDocument` seeds for the null/missing legs.
  Reuse `StreamingCollectionNormalizationTests.cs` and `NativeOwnedCollectionWholeEntityTests.cs` as templates.

### Task 2.2 — The change
- [ ] Implement fix 1 (shaper under the `Select`, parameter substitution) cleanly; no debug output.
- [ ] Implement fix 2 with the plan stack passed through `RewriteMaterializer` (no statics).
- [ ] Relax the `StreamingEligibility` guard **only for owned-only subtrees**: recurse into element navigations; still
  return `false` for any **non-owned** navigation inside an element, or for any shape the rewriter cannot yet handle
  (`HasBareJoinInnerEntityLeaf`, Select projecting the owned collection, TPH inside an owned type — verify each is
  either handled or still rejected; do not assume).
- [ ] **The eligibility predicate must be the single source of truth**: if the rewriter has its own throw for an
  unsupported shape, make eligibility call the same check (EF-405 lesson).

### Task 2.3 — Verify
- [ ] **Mutation check:** restore the original `StreamingEligibility` guard; the new unit tests (eligible shapes) and the
  allocation-sensitive functional test must fail; restore. Also mutate fix 2 (e.g. return the wrong counter) and
  confirm the nested-ordinal-key assertions fail.
- [ ] **Full suite EF8/EF9/EF10** — EF8/EF9 are unverified here (`#if EF8 || EF9` paths exist; the `IncludeExpression`
  shape for nested owned collections may differ). `MONGODB_EF_QUERY_MODE=NativeOnly` spec run too (see
  `tests/.../SpecificationTests/AGENTS.md`; spec baselines may legitimately change — review each diff).
- [ ] Re-run `NestedWholeEntity`; expect ≈ 70 MB at 10,000 orders (extrapolated; ≈ 1.8× the 37.9 MB floor — the
  remaining overhead is EF materializing every owned level, out of scope).
- [ ] Commit `EF-322: stream owned-collection elements that own further navigations`.

**Risks:** ordinal-key resolution for deeper/composite synthesized keys (the probe handled only "enclosing element's
counter + 1"); tracking and identity-resolution state-manager behavior; absent/null nested arrays per element (root-level
normalization to `[]` is established — confirm it applies per element); required owned references in elements; EF8/EF9
shape differences.

---

## Step 3 — Reference Include beside an owned sibling goes native

**Root cause (verified by probe with source-copy instrumentation).** For `Orders.Include(o => o.Account)` on a model with
owned types, EF's navigation expansion builds one selector that nests `IncludeExpression`s:
`Include(Account) ← Include(Shipping/Lines, embedded) ← ti.Outer`.
In `src/MongoDB.EntityFrameworkCore/Query/Visitors/`:
- `MongoQueryableMethodTranslatingExpressionVisitor.Include.cs:149` `TryWalkIncludeChain`, lines ~170–173:
  `if (navigation.IsEmbedded()) { return false; }` — doc comment at :146 says "an embedded sibling rejects the chain".
- `MongoQueryableMethodTranslatingExpressionVisitor.cs:409` and `:418`: `TryGetReferenceIncludeChain` and
  `TryGetMixedReferenceAndCollectionIncludeChain` both go through `TryWalkIncludeChain` ⇒ both miss.
- `IsOwnedEmbeddedIncludeSelector` (Include.cs:513, used at .cs:557) requires **every** layer to be embedded ⇒ false
  (outermost is non-embedded Account).
- The selector falls into the generic projection arm (.cs:555); `NativeProjectionBinder.TryPopulateNativeProjection`
  fails and `MarkNotNativelyRepresentable()` is set (.cs:566 — *which call set it is inference*; the probe showed the
  flag set, not the call).
- `TryConfirmReferenceIncludeChain` (which calls `MarkReferenceIncludeConfirmed`) is never reached ⇒
  `Select.HasUnconfirmedCandidateJoin` (`Expressions/MongoSelectDefinition.cs:994`) stays true ⇒ `Select.Route` returns
  Fallback (:1060) ⇒ `ClassifyNativeDisposition` (`MongoShapedQueryCompilingExpressionVisitor.cs:875`) Fallback ⇒
  `TryBuildNativeFactory` (:690–702) throws via `ThrowIfNativeOnlyForbidsFallback` under `NativeOnly`.

**Not a correctness guard — a missing feature (inference):** the sibling-rejects rule looks left over from when the
owned-only recognizer was written; the nearby `ThenInclude` axis already treats embedded hops as transparent (the EF-368
handling at Include.cs:~178–205). `Reviews.Include(r => r.Product)` is native only because `Review` has no owned types.

**Evidence matrix (EF10, NativeOnly):** with the unmodified repo, `Include`, `Include+Where`, `Include+OrderBy` decline
for **every** model variant that has any owned type — `onlyShipping` (one owned reference), `linesNoNested` (one flat
owned collection), `linesMeta`, `linesDiscounts`, `full`, … — and run native on `noOwned`; every `NoInclude` shape is
native. So even *one* owned navigation is enough; nesting is irrelevant to this gap. There is **no decline-reason hook**:
`MarkNotNativelyRepresentable()` stores a bare bool, so the investigation needed instrumented copies. (Consider adding a
reason string behind `NativeOnly` as a small diagnosability follow-up — see "Out of scope / follow-ups".)

**Proven fix (scratch copy only):** in `TryWalkIncludeChain`, replace the `return false` at :170 with: if
`HasNonEmbeddedThenInclude(include)` is false, set `body = include.EntityExpression` and `continue` — i.e. treat the
embedded sibling on the `EntityExpression` spine as transparent, the same rule :178–205 applies on the `ThenInclude`
axis. Reuse `TryFindReferenceBehindOwned` (:448) for an embedded sibling that hides a reference. With that, `Include`,
`Include+OrderBy` and `Where+Include+OrderByDescending+Skip+Take` all ran native under `NativeOnly` on `full`, `noOwned`,
`onlyShipping`, `linesNoNested`, tracked and untracked, and the 5 seeded rows' `Account` name, `Shipping.Carrier` and
`Lines.Count == 2` were correct. **That is thin evidence** (5 rows, a few fields). Prototype diff:
`docs/superpowers/findings/2026-10-14-benchmark-gaps/step3-include-embedded-sibling-prototype.diff`.

**Files:** `MongoQueryableMethodTranslatingExpressionVisitor.Include.cs` (+ doc comment at :143–148). Possibly
`MongoShapedQueryCompilingExpressionVisitor.cs` (`AllPendingLookupsAreStreamable` :916) and `StreamingEligibility` — see
risks.

### Task 3.1 — Tests first
- [ ] Extend/mirror `NativeReferenceIncludeTests.cs`, `NativeJoinOwnedIncludeTests.cs`, `NativeOwnedThenIncludeTests.cs`,
  `QueryModeGateIncludeTests.cs`. New file e.g. `NativeReferenceIncludeBesideOwnedTests.cs`. Model: an entity with a
  reference navigation **plus each of**: an owned reference; an owned flat collection; an owned collection with nested
  owned reference and collection. In `NativeOnly`/`Native`/`DriverLinq`, assert **rows and full nested owned data**
  (not counts), including: with `Where` (owned and non-owned predicate), `OrderBy` (owned member), `Skip`/`Take`,
  `ThenInclude` past the reference, a reference **hosted by an owned type**, the **owned Include listed first**
  (EF's nesting order can vary — this interacts with `Joins.Count != chain.Count` and `TryAdmitFilterJoins` (:374)),
  tracked / no-tracking / identity resolution, and a dangling FK (Include of a missing principal).
- [ ] **Decline pins** that must still fall back correctly: owned sibling **with a collection Include** to another
  collection; an embedded sibling with a **non-embedded include nested behind it** (the `HasNonEmbeddedThenInclude`
  guard). Assert correct results under `Native` and the decline under `NativeOnly`.
- [ ] Unit test of `ClassifyNativeDisposition`/`Route` for the new shape (see `MongoCardinalityRouteTests.cs` for style).

### Task 3.2 — The change
- [ ] Apply the `TryWalkIncludeChain` change. Update the doc comment.
- [ ] **Gate/fix single predicate:** the new admission must call the same predicates `TryConfirmReferenceIncludeChain`
  uses; do not restate.
- [ ] Re-check `AllPendingLookupsAreStreamable` (:916) and `StreamingEligibility`: the root now has owned navigations
  *alongside* a reference lookup. (After Step 2 this combination also reaches nested-element streaming.)
- [ ] Guard against double-counting an owned-type reference include (the "reference behind owned" case).

### Task 3.3 — Verify
- [ ] **Mutation check:** restore the `return false`; the new `NativeOnly` tests must fail; restore. Mutate
  `HasNonEmbeddedThenInclude` to always-false and confirm the decline pin fails.
- [ ] **Full suite EF8/EF9/EF10** + `MONGODB_EF_QUERY_MODE=NativeOnly` spec run; review every changed baseline.
- [ ] `--verify-native` now reports `ComplexQuery: NATIVE OK`. Re-run `ComplexQuery`; allocations should fall (needs
  Step 2 for the streaming win). Also run the **explicit `DriverLinq` pin of the same shapes** — the AGENTS parity plan
  records that registering a new native join shape can change the fallback pipeline at translation time.
- [ ] Commit `EF-322: recognise a reference Include beside owned-sibling includes`.

**Risks:** an embedded sibling with a non-embedded include nested behind it must still decline
(`HasNonEmbeddedThenInclude`); owned-hosted references not double-counted; the streaming gate / eligibility interplay;
nesting order (above); full-suite regressions are the realistic cost (dispatch change ⇒ EF-436-class risk).

---

## Step 4 — Re-benchmark and record

- [ ] Idle machine. Run headline, extended and `--verify-native` per the reproduction block. Update `--verify-native`
  expectations if any shape's status changed.
- [ ] Add `results/<date>-benchmark-gaps-fixed.md` (or a section appended to `2026-10-14-driverlinq-vs-native.md`) with
  before/after for Wide, Nested, ComplexQuery. **Acceptance:** no extended shape with native slower than driver-LINQ;
  Wide ≈ DriverOnly; Nested allocation ≈ 70 MB; ComplexQuery native under `NativeOnly`.
- [ ] Re-derive every prose count from the tables (do not restate numbers from this plan).
- [ ] Finish the slice per repo convention: squash, `-presquash` backup, ff onto `EF-322c`, push **without force**
  after `git fetch` / rebase on the remote tip.

## Out of scope / follow-ups (do not do silently)

- A reason string for `MarkNotNativelyRepresentable()` so `NativeOnly` failures say *which* guard declined (this
  investigation had to instrument source copies). Small, separate slice; propose rather than bundle.
- Wide-entity ordered-read fast path (next element expected at next ordinal) toward the ~22 ms raw fetch floor.
- The remaining ~1.8× over the driver floor for nested graphs (EF materializes every owned level).
- Jira tickets for the three gaps have **not** been filed; ask the owner before filing (Jira create then update; see
  memory notes on the MCP formatting quirks).

## Prototype artifacts

`docs/superpowers/findings/2026-10-14-benchmark-gaps/`:
- `step1-wide-switch-prototype.diff` — the `Expression.Switch` dispatch (first hunk is a BOM artifact).
- `step2-nested-streaming-prototype.diff` — nested-element streaming; **contains debug `Console.WriteLine`s**, static plan
  stack; reference for the two fixes only. (It does not include the `StreamingEligibility.cs` guard removal — that is
  deleting lines ~71–79.)
- `step3-include-embedded-sibling-prototype.diff` — the `TryWalkIncludeChain` change (include the
  `HasNonEmbeddedThenInclude` guard in the real implementation).

Scratch trees from the investigation (`/tmp/inv-include`, `/tmp/inv-nested`, `/tmp/inv-wide`) are ephemeral and may be
gone; everything needed is above or in the diffs. Line numbers throughout are as of `EF-322c` @ `ce2a9c01` and **will
drift** — grep for the symbol names.
