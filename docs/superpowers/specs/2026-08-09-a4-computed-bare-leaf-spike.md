# EF-322 stream 1, slice A4 — the computed bare projection leaf spike

*Run 2026-08-09 in ONE throwaway worktree at `86a5396f`, carrying an env-gated tier-2 prototype plus two
env-gated instrumentation hooks (created, used and removed; `git worktree list` was checked before and after —
the three `.claude/worktrees/agent-*` worktrees belong to other sessions and were neither created nor touched).
The main tree finishes with only this file added. Inputs:
`docs/superpowers/specs/2026-08-07-stream1-translator-breadth-spike.md` §3 row 2 and §7 row A4 (the cited
sizing); `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md`, the step-3a note's
**"TIER 2 (computed bare leaves) WAS BUILT, MEASURED, AND REVERTED"** paragraph (the authoritative as-built
record); `NativeProjectionBinder.TryDeriveDocumentPathAlias`, `MongoSelectDefinition`'s alias-override carrier,
`MongoShapedQueryCompilingExpressionVisitor.ShouldStripBareProjectionOnFallback`.*

**Tagging convention, applied strictly.** Every claim below is one of:
**MEASURED** (produced by a run this session; the method is §7 and the numbers are reproducible) ·
**READ** (established by reading source at `86a5396f`; no execution) ·
**INFERRED** (drawn from MEASURED/READ facts, not itself observed) ·
**UNVERIFIED** (not established — said so explicitly).

**Trap compliance, stated up front.**
(a) **Every routing claim is a `MongoQueryMode.NativeOnly` run that succeeds or throws** — never an MQL shape.
MQL appears in this document only where the question *is* the emitted shape (§2, §3), and each such line is
captioned as a shape pin, not a routing proof.
(b) **The prototype is behind an environment gate and every A/B ran from ONE build.** `MONGODB_EF_SPIKE_A4=1`
enables tier 2; unset/`0` reproduces the shipped tree. **The gate is demonstrated live**: with it off the
prototype's own probe rows go from `OK[2,0,0,0,1]` to
`NativeTranslationNotSupportedException` under `NativeOnly` (§5.1), and the committed functional suite goes from
**8 failures to 0** (§5.3) — i.e. turning the gate off is what makes the tree green again.
(c) **Sizing is judged by MESSAGE TRANSITION, never by pass count.** The slice-B trap applies here in a second
form: 4 of the 6 converting cases move `Failed → Failed` with a *different* message (a stale `AssertMql`
baseline), so a count-only reading of the `NativeOnly` axis reports **+2** where the real answer is **6** (§1).
(d) **Ragged fixtures are used throughout, and the array states are stated explicitly.** Every count measurement
in this document runs over five rows: `Posts` **present(2)**, **empty array**, **element MISSING**, **explicit
BSON null**, **present(1)**. The seed self-checks all five states against the stored documents before any query
runs, so "missing" and "explicitly null" cannot silently degrade into one state.
(e) `MONGODB_URI` and `ATLAS_URI` were both unset, so TestContainers booted its own
`mongodb/mongodb-atlas-local`. The three EF configurations were never built in parallel.

---

## Headline — seven findings, ordered by how much each changes A4's plan

1. **The contradiction resolves in favour of the as-built note. A4 converts SIX specification cases, not 28 —
   and the 6 matches the step-3a note's "6–7 further `NativeOnly` wins" almost exactly.** MEASURED, by message
   transition over all 4610 results, from one build, gate off vs gate on: **2 `Failed→Passed`**
   (`NorthwindSelectQueryMongoTest.Explicit_cast_in_arithmetic_operation_is_preserved`, both `async` legs) plus
   **4 `Failed→Failed` with a changed message** (`Projecting_count_of_navigation_which_is_generic_list` and
   `…_generic_collection`, both `async` legs each) — those four now go native with **correct data** and fail only
   on a stale `AssertMql` baseline. `2 + 4 = 6`. **0 `Passed→Failed`** on that axis. The stream-1 spike's
   **28 sole-cause / 54 total is not reproduced**, and §1 explains the gap by measurement rather than by
   assertion. **A4 is a ~6-case slice, and every plan built on 28 must be re-priced.**

2. **The prerequisite STILL REPRODUCES, at today's tip, with all four shipped slices in place — and EF-362's
   change to `ShouldStripBareProjectionOnFallback` did not touch it.** MEASURED: with tier 2 enabled,
   `Where(b => b.Title.StartsWith(capturedLocal)).Select(b => b.Posts.Count)` throws `MongoCommandException`
   under the **default `Native` mode**, where the shipped tree returns `[2,0,0,0,1]`. The emitted MQL is the
   difference in one line — native `{"$size": {"$ifNull": ["$Posts", []]}}` vs the driver's bare
   `{"$size": "$Posts"}`. EF-362 re-keyed the strip from `BareProjectionTier` to `HasDocumentPathAliasOverride`,
   and both predicates are **false** for a `Synthetic`-tier override, so the un-stripped driver push-down is
   reached exactly as before. **Two committed functional tests catch it** (§5.3). §2.

3. **BUT the prerequisite is far NARROWER than the note implies, and it is NOT A4's whole scope.** MEASURED,
   same run, same fixture: of the four computed bare shapes, only the **unfiltered owned-collection `.Count`**
   fails. **Arithmetic, the FILTERED count `Count(pred)`, and a narrowing cast all survive the late-decline
   route with correct values**, because the driver renders them as `$multiply` / `$sum`-over-`$map` / `$toInt`
   — none of which aborts on a missing or explicitly-null array. And the **reference**-collection count (the
   shape that actually converts in the spec suite) is safe too: its `$lookup` always writes an array. So the
   blocker gates **one shape**, and that shape contributes **zero** of A4's six cases. §2.2.

4. **Fixing it is SMALL, and the fix is measured, not designed on paper.** The driver renders
   `Select(b => (b.Posts ?? new List<Post>()).Count)` as **`{"$size": {"$ifNull": ["$Posts", []]}}` — byte-identical
   to what native emits — and it returns `[2,0,0,0,1]` over the ragged seed.** MEASURED with no EF in the loop
   (`collection.AsQueryable()`), so it is a property of the driver's LINQ provider, not of this provider's
   bridge. The prerequisite therefore reduces to a **null-coalescing rewrite of the pushed-down bare `.Count`
   body in `CapturedExpression`**, which only the fallback route ever reads. **The obvious alternative does NOT
   work and would have been the natural first attempt**: the `?:` spelling renders `$cond` and **still aborts**
   — MongoDB evaluates the untaken branch. §3.

5. **Tier 2 also breaks the `DriverLinq` escape hatch, and that is a rubric-level fact, not a footnote.**
   MEASURED: with tier 2 on, `Select(b => b.Posts.Count)` under **explicit `MongoQueryMode.DriverLinq`** aborts
   with `MongoCommandException` **even with no late decline**, where the shipped tree returns `[2,0,0,0,1]`.
   Populating `Projection` at translation time flips `ProjectionAnalyzer.CanPushDown` from false to true, so the
   driver renders the bare Select instead of the mixed path folding it client-side. The versioning rubric's
   carve-out for the native default is *conditional* on `UseQueryMode(DriverLinq)` restoring the previous path;
   for this one shape, tier 2 as prototyped breaks that condition. The §3 fix closes this leg too (it rewrites
   the captured chain, which is what `DriverLinq` executes). §2.3.

6. **Both facts the revert deliberately kept are RE-VERIFIED at today's tip — and the `_v` finding is sharper
   than recorded.** (a) The tier conditional is proven in **both** directions by mutation from one build:
   forcing the strip **on** breaks only tier 2 (all four computed shapes →
   `Document element '_v' is missing but required`) and leaves tier 1 green; forcing it **off** breaks only
   tier 1 (`Title` throws loudly, `Note` returns `[null,null,null,null,null]` **silently**) and leaves tier 2
   unaffected. (b) The `_v` collision is **UNREACHABLE** — on a model storing a real `int` property at element
   `_v`, every route returns correct values with no collision guard present. **But the reason recorded is
   incomplete, and the correction matters**: unreachability is a consequence of the *tier conditional*, not of
   the alias choice. MEASURED — force the strip on for that same model and the late route returns
   `[100,200,300,400,500]`, the stored `_v` values, where `[2,0,0,0,1]` is correct: **silent wrong data**. §4.

7. **`ProjectionAliasTier.Synthetic` and the reserved-alias machinery are confirmed in the tree and unreached,
   so re-enabling tier 2 behind a gate is genuinely cheap — the prototype is 15 lines.** READ + MEASURED: the
   enum member, the tier-carrying override table, the write-once `Add`, and the tier-conditional strip all
   exist; the only edit needed to reach `Synthetic` is a widening of `TryDeriveDocumentPathAlias`'s decline. The
   whole prototype is one `if`, one helper predicate and one variable. §4.3, §7.

---

## 1. The 4× contradiction, reconciled by measurement

### 1.1 The two claims

| source | claim |
|---|---|
| `…2026-08-07-stream1-translator-breadth-spike.md` §3 row 2 / §7 row A4 | A4 = **54 total / 28 sole-cause**, `solB` 0, cited 54 |
| `Query/AGENTS.md`, step-3a note | tier 2 "measured **6–7 further `NativeOnly` wins**", from a prototype that was actually built and run |

### 1.2 The measurement

MEASURED. EF10 specification suite, one build, gate off vs gate on, compared as
`(testName → outcome, message)` **sets** over all 4610 results.

| axis | gate OFF | gate ON |
|---|---|---|
| `MONGODB_EF_NATIVE_ONLY=1` | **2501** passed / **2092** failed / 17 skipped | **2503** / **2090** / 17 |
| default `Native` | **4593** / **0** / 17 | **4589** / **4** / 17 |

Transitions on the `NativeOnly` axis:

| transition | cases | which |
|---|---:|---|
| `Failed → Passed` | **2** | `NorthwindSelectQueryMongoTest.Explicit_cast_in_arithmetic_operation_is_preserved` (`async: False`, `async: True`) |
| `Failed → Failed`, **message changed** | **4** | `NorthwindSelectQueryMongoTest.Projecting_count_of_navigation_which_is_generic_list` ×2 async; `…_which_is_generic_collection` ×2 async |
| `Passed → Failed` | **0** | — |

The 4 message-changed cases move from
`NativeTranslationNotSupportedException: "Query projects a non-entity result…"` to an `AssertMql` string
mismatch. `AssertMql` runs **after** the base EF Core result assertion, and those same 4 tests appear as the
only `Passed → Failed` on the default-`Native` axis with the identical `AssertMql` message — so **their data
assertion passes in both modes** and only a stale committed baseline keeps them red. They are conversions.

**Converting cases = 2 + 4 = 6. Converting tests = 3.** MEASURED.

The re-based MQL, captured by running that one test with `EF_TEST_REWRITE_BASELINES=1` in the throwaway
worktree (a stage-order change only, and the `$lookup` is still there):

```
- Customers.{ "$lookup" : {…, "as" : "_lookup_Orders" } }, { "$sort" : { "_id" : 1 } }, { "$project" : { "_v" : { "$size" : "$_lookup_Orders" }, "_id" : 0 } }
+ Customers.{ "$sort" : { "_id" : 1 } }, { "$lookup" : {…, "as" : "_lookup_Orders" } }, { "$project" : { "_v" : { "$size" : "$_lookup_Orders" }, "_id" : 0 } }
```

**Verdict: 6, and the as-built note is right. The cited 28 is not reproduced.** MEASURED.

### 1.3 Why the cited figure is 4–9× too large — measured, not asserted

Two independent causes, and the first is the larger.

**Cause A — the classifier's bucket is not A4's scope.** READ. §3 row 2 of the stream-1 spike labels its bucket
*"translator already resolves it as a VALUE (reverted tier 2)"* — i.e. `TryTranslateValue` succeeds on the
leaf. That predicate is **blind to the selector-body shape**: it counts a computed leaf inside a
`new {…}`/DTO construction identically to a bare one. A **wrapped** computed leaf has been native since EF-347
(arithmetic), EF-359 (filtered count), the count-projection slice (count) and EF-403 (cast) — so a wrapped
VALUE_OK case that still fails is failing for some **other** reason and is not A4's to fix. A4's scope is the
**bare** body only.

**Cause B — the enclosing query, not the leaf, is the blocker.** MEASURED, by instrumenting the bare-body
branch of `NativeProjectionBinder` and running the whole EF10 spec suite under `MONGODB_EF_NATIVE_ONLY=1`:

| bare-body outcome (translation FIRINGS, not cases) | count |
|---|---:|
| bare selector body reached the binder's bare branch | **1155** |
| …leaf not translatable at all (`TryTranslateValue` fails) | **670** |
| …leaf translated, has a document path ⇒ **tier 1**, already native | **399** *(by subtraction; INFERRED)* |
| …leaf translated, **no** document path ⇒ **tier-2-shaped** | **86** |
| …leaf translated, no document path, and tier 2 would still decline | **0** |

**86 tier-2-shaped translations exist in the corpus and 6 cases convert.** Firings are not cases — one test can
translate its query more than once, and one query can carry more than one bare `Select` — so these two numbers
are on different bases and the ratio is not a conversion rate. What the table does establish is that the
population of A4-shaped leaves is **86 firings of only eight distinct expressions**, not 54 independent cases:

| distinct tier-2-shaped bare leaf | firings | node kind |
|---|---:|---|
| `[EntityQueryRoot].Where(o1 => Property(o,…))` (a reference-collection `Count` subquery) | 24 + 10 + 4 + 2 = **40** | `MongoSizeExpression` |
| `(o.OrderID + 1)` | 24 | `MongoBinaryExpression` |
| `(o0.OrderID + 1)` | 12 | `MongoBinaryExpression` |
| `(o.OrderID * 2)` | 8 | `MongoBinaryExpression` |
| `(Convert(o.OrderID, Decimal) / Convert((o.OrderID + 1000), Decimal))` | 2 | `MongoBinaryExpression` |
| | **86** | 46 binary + 40 size |

The 2 decimal-division firings are exactly the 2 `Failed→Passed` cases; the 40 size firings are the family the
4 message-changed cases come from. **The 44 remaining arithmetic firings convert nothing.** Their lambda
parameters (`o0`, `o1`, `o2`) are inner-scope identifiers, so these bare `Select`s sit inside a subquery whose
OUTER query is what declines — the same enclosing-blocker mechanism that made slice A5 realize 0 of its 36.
**INFERRED** from the parameter naming plus the zero conversions; which specific outer construct blocks each is
**UNVERIFIED**.

**This is the third consecutive slice whose sole-cause figure over-predicted** (A2 34/44, A5 0/36, A1 28/56,
now A4 6/28). Together with A1's lesson — that the yield can live at a decline site no slice document names —
the rule stands: **size a slice by a prototype A/B, never by the decomposition table.**

### 1.4 What A4 is worth relative to the remaining slices

INFERRED from §1.2 and the stream-1 spike's §7 table. A4's cited rank was 4th of 20 by sole-cause (28). At a
measured 6, and with a prerequisite of its own, it ranks **below every remaining slice whose cited sole-cause
exceeds 6 and which has no prerequisite** — which, on the cited figures, is most of them. **A4 should not be
scheduled on the strength of the 28.** See §6 for what it is still worth doing.

---

## 2. Does the prerequisite still reproduce? — YES, and it is narrower than recorded

### 2.1 The reproduction

MEASURED, EF10, one build, tier-2 gate ON, over the five-state ragged fixture. Every cell is a real execution;
the "shipped tree" column is the same probe with the gate OFF.

| query | mode | gate OFF (shipped) | gate ON (tier 2) |
|---|---|---|---|
| `Select(b => b.Posts.Count)` | `Native` | `OK[2,0,0,0,1]` | `OK[2,0,0,0,1]` |
| `Select(b => b.Posts.Count)` | `NativeOnly` | throws (decline) | **`OK[2,0,0,0,1]` — goes native** |
| `Select(b => b.Posts.Count)` | `DriverLinq` | `OK[2,0,0,0,1]` | **`MongoCommandException`** |
| `Where(StartsWith(param)).Select(b => b.Posts.Count)` | `Native` | `OK[2,0,0,0,1]` | **`MongoCommandException`** |
| `Where(StartsWith(param)).Select(b => b.Posts.Count)` | `DriverLinq` | `OK[2,0,0,0,1]` | **`MongoCommandException`** |

The captured-local `StartsWith` is the standard late-decline lever: the native renderer refuses a parameterized
regex term, so `TryBuildNativeFactory` returns `null` *after* the alias-addressed shaper has been committed.

The emitted MQL, as a **shape pin only** (it proves nothing about routing):

| route | `$project` body |
|---|---|
| native | `{ "_v" : { "$size" : { "$ifNull" : ["$Posts", []] } }, "_id" : 0 }` |
| driver-LINQ push-down (the un-stripped fallback) | `{ "_v" : { "$size" : "$Posts" }, "_id" : 0 }` |

**So the recorded mechanism is exactly right and is unchanged by step 3a, EF-362, slice B or slice A1.** READ:
`ShouldStripBareProjectionOnFallback` returns `select.HasDocumentPathAliasOverride`, which iterates the override
table for a `DocumentPath` tier; a `Synthetic`-tier bare override makes it `false`, so no strip happens and the
driver's push-down survives. EF-362 widened *which* `DocumentPath` overrides strip (bare **and** named); it did
not change what `Synthetic` does.

### 2.2 …but it gates ONE shape, and that shape earns none of A4's six cases

MEASURED, same run, same fixture, same late-decline lever:

| bare computed leaf | native (early) | LATE-decline route, default `Native` | driver's fallback rendering |
|---|---|---|---|
| `b.Posts.Count` (owned, unfiltered) | `OK[2,0,0,0,1]` | **`MongoCommandException`** | `{$size: "$Posts"}` |
| `b.Posts.Count(p => p.PostId > 0)` (owned, filtered) | `OK[2,0,0,0,1]` | `OK[2,0,0,0,1]` | `{$sum: {$map: {input: "$Posts", …}}}` |
| `b.Rank * 2` (arithmetic) | `OK[2,4,6,8,10]` | `OK[2,4,6,8,10]` | `{$multiply: ["$Rank", 2]}` |
| `(int)b.Weight` (narrowing cast) | `OK[1,2,3,4,5]` | `OK[1,2,3,4,5]` | `{$toInt: "$Weight"}` |

`$sum` over a `$map` whose `input` is missing yields `0` rather than aborting; `$multiply`/`$toInt` never touch
an array. Only bare `$size` aborts. **INFERRED, and the direction is safe:** the reference-collection count that
supplies 4 of A4's 6 cases reads a `$lookup` output, which always exists, so it is not exposed either — its
native rendering is `{"$size": "$_lookup_Orders"}` with no `$ifNull`, and the spec suite passes its data
assertion.

### 2.3 The `DriverLinq` escape hatch stops working, and that has rubric consequences

MEASURED (row 3 of §2.1). With tier 2 on, the bare owned `.Count` aborts under **explicit `DriverLinq`** with no
late decline involved. INFERRED mechanism, from reading `VisitProjectedQuery`: with `Projection` populated the
shaper is already index-bound, `ProjectionAnalyzer.CanPushDown` accepts it, and the query takes the
**push-down** path (driver renders the whole captured chain, bare `$size` and all) instead of the **mixed** path
(`StripPushedDownSelect` → whole documents → client-side fold) it takes when the projection is not native.

`AGENTS.md`'s versioning rubric records that the native default is not a break *because* results are unchanged,
unsupported shapes fall back, and `UseQueryMode(DriverLinq)` restores the previous path. For this one shape,
tier 2 as prototyped violates the first and third clauses at once. **The §3 fix closes this leg as well**,
because it rewrites `CapturedExpression`, which is precisely what `DriverLinq` executes.

---

## 3. What fixing it actually takes — measured, and it is small

### 3.1 The measurement, with no EF in the loop

MEASURED via `collection.AsQueryable()` (the driver's own LINQ v3 provider, no provider bridge involved), over
the same five-state ragged seed:

| driver-LINQ selector | rendered `$project` body | result |
|---|---|---|
| `b => b.Posts.Count` | `{$size: "$Posts"}` | **`MongoCommandException`** |
| `b => (b.Posts ?? new List<Post>()).Count` | **`{$size: {$ifNull: ["$Posts", []]}}`** | **`OK[2,0,0,0,1]`** |
| `b => b.Posts == null ? 0 : b.Posts.Count` | `{$cond: {if: {$eq: ["$Posts", null]}, then: 0, else: {$size: "$Posts"}}}` | **`MongoCommandException`** |
| `b => b.Posts.Count(p => p.PostId > 0)` | `{$sum: {$map: {input: "$Posts", …}}}` | `OK[2,0,0,0,1]` |
| `b => b.Posts.Where(p => p.PostId > 0).Count()` | `{$size: {$filter: {input: "$Posts", …}}}` | **`MongoCommandException`** |

**The `??` spelling produces byte-identically the rendering native emits.** The `?:` spelling does not work —
MongoDB evaluates `$cond`'s untaken branch — and it is the shape a designer would reach for first, so it is
recorded here as a refuted option rather than left to be rediscovered.

### 3.2 The concrete change

INFERRED from §3.1 plus READ of the call graph. *"The late-fallback path must be able to emit `$ifNull` itself"*
resolves to: **rewrite the pushed-down bare `.Count` body inside `MongoQueryExpression.CapturedExpression` into
its null-coalesced form.** Concretely:

- **Where.** One rewrite applied where the bare computed leaf is committed (`NativeProjectionBinder`, alongside
  the alias-override registration) or at the decline site in `CompileShapedQuery`. Registering it at commit time
  is simpler and strictly safer: `CapturedExpression` is read **only** by the driver-LINQ fallback bridge, so an
  unconditional rewrite is inert on the native route and covers the explicit-`DriverLinq` leg of §2.3 as well,
  which a decline-site rewrite would not.
- **What it is NOT.** It is neither a strip decision nor a renderer change. The strip must stay **off** for the
  `Synthetic` tier (§4.1 measures what forcing it on does), and neither renderer is touched.
- **What else routes through it.** `CapturedExpression` is the single input to
  `MongoEFToLinqTranslatingExpressionVisitor` for *every* fallback, and `StripPushedDownSelect` already mutates
  it on two paths (the mixed path unconditionally, the tier-1 late-decline path conditionally). A third mutation
  must be ordered against those two. READ: the tier-1 strip and the mixed-path strip are already documented as
  structurally disjoint (they fire on different branches of `VisitProjectedQuery`); a `Synthetic` rewrite fires
  on neither, so all three remain mutually exclusive. **UNVERIFIED** — no ordering test was run.
- **Scope.** Only the unfiltered collection-navigation `.Count` needs it (§2.2). Whether a *primitive*-collection
  bare `.Count` (`b.Tags.Count`) needs it too is **UNVERIFIED** — it is not natively representable today, so it
  never reaches this path.
- **Cost.** Small: one expression rewrite plus its ragged-fixture coverage. **This prerequisite is NOT larger
  than the slice.** That is the single most useful thing this spike found after the sizing.

### 3.3 The alternative worth recording

The bare `$size` is a latent whole-query abort for **any** driver-LINQ user projecting `.Count` over a field
that may be absent — `Where(pred).Count()` has the same defect (§3.1, last row) and is not a shape this provider
pushes down. Filing it against the C# driver would fix all of them at the source. That is a separate ticket, not
a substitute for §3.2 (the provider cannot wait on it), and no ticket was filed by this spike.

---

## 4. The two facts the revert kept — both re-verified at today's tip

### 4.1 The tier conditional is proven in BOTH directions

MEASURED, by env-gated mutation of `ShouldStripBareProjectionOnFallback` from one build, on the late-decline
route under default `Native` (tier 2 enabled throughout):

| leaf | strip = shipped (tier-conditional) | strip forced **ON** | strip forced **OFF** |
|---|---|---|---|
| tier 1, non-nullable `b.Title` | `OK[p1_two…p5_one]` | `OK` | **throws `Document element 'Title' is missing`** |
| tier 1, nullable `b.Note` | `OK[n1…n5]` | `OK` | **`OK[<null>,<null>,<null>,<null>,<null>]` — SILENT** |
| tier 2, `b.Posts.Count` | `MongoCommandException` (§2) | **`Document element '_v' is missing but required`** | `MongoCommandException` (§2) |
| tier 2, `b.Rank * 2` | `OK[2,4,6,8,10]` | **`Document element '_v' is missing but required`** | `OK[2,4,6,8,10]` |
| tier 2, `b.Posts.Count(pred)` | `OK[2,0,0,0,1]` | **`Document element '_v' is missing but required`** | `OK[2,0,0,0,1]` |
| tier 2, `(int)b.Weight` | `OK[1,2,3,4,5]` | **`Document element '_v' is missing but required`** | `OK[1,2,3,4,5]` |

**Forcing the strip ON breaks only tier 2; forcing it OFF breaks only tier 1.** Exactly as recorded. Note the
tier-1 "off" row is the documented silent half — a nullable leaf returns five nulls with no exception anywhere —
so this measurement also re-confirms that an alias mismatch must be tested by asserting VALUES, never by
absence-of-throw.

### 4.2 The `_v` collision is UNREACHABLE — and the recorded reason is incomplete

MEASURED on a purpose-built model whose `int Stored` property is mapped `HasElementName("_v")`, seeded with a
real stored `_v` of `100…500`, with **no** collision guard anywhere in the tree and tier 2 enabled:

| query | `Native` | `DriverLinq` | `NativeOnly` |
|---|---|---|---|
| `Select(b => b.Stored)` (reads the real `_v`) | `OK[100…500]` | `OK[100…500]` | `OK[100…500]` |
| `Select(b => b.Posts.Count)` | `OK[2,0,0,0,1]` | `MongoCommandException` (§2.3) | **`OK[2,0,0,0,1]`** |
| `Select(b => b.Stored * 2)` — reads `_v`, writes `_v` | `OK[200…1000]` | `OK[200…1000]` | **`OK[200…1000]`** |

The arithmetic row is the interesting one and it reproduces the recorded claim precisely: the emitted `$project`
replaces the document with a single computed `_v` whose **input** is the stored `_v`, and it comes out correct.

**The correction.** The as-built note attributes unreachability to the `$project` always replacing the document.
That is the mechanism on the *native* route only. MEASURED: force the strip ON for this same model and the late
route returns **`OK[100,200,300,400,500]`** where `[2,0,0,0,1]` is correct — **silent wrong data**, because the
shaper then reads `_v` off a whole document and finds the user's real property. So unreachability is a
consequence of the **tier conditional** (§4.1), not of the alias choice. Anyone who ever makes the strip
unconditional reopens the collision, silently. Record it that way.

### 4.3 `Synthetic` and the reserved-alias machinery are in the tree and unreached

READ, confirmed. `ProjectionAliasTier.Synthetic` exists (`MongoSelectDefinition.cs`) with a doc comment naming
exactly this use; `AddProjectionAliasOverride` takes the tier as data; `BareProjectionTier` /
`HasDocumentPathAliasOverride` read it; `ShouldStripBareProjectionOnFallback` is the tier-conditional consumer.
The **only** producer of a tier is `NativeProjectionBinder`, which hard-codes `ProjectionAliasTier.DocumentPath`
at both registration sites — so `Synthetic` is written by nothing.

MEASURED: the whole tier-2 prototype is **one `if` inside the bare branch, one node-kind predicate, and one
local variable** (plus threading the tier through the existing registration call). The A/B is genuinely cheap,
which is what made everything in §1–§4 measurable in a single session.

---

## 5. Sizing the slice, per shape

### 5.1 Does each shape convert? — MEASURED, `NativeOnly` routing

| bare selector body | gate OFF | gate ON | converts? |
|---|---|---|---|
| `b => b.Posts.Count` (owned collection) | declines | **succeeds, `[2,0,0,0,1]`** | **yes** |
| `b => b.Posts.Count(p => …)` (owned, filtered) | declines | **succeeds, `[2,0,0,0,1]`** | **yes** |
| `b => b.Rank * 2` (arithmetic) | declines | **succeeds, `[2,4,6,8,10]`** | **yes** |
| `b => (int)b.Weight` (narrowing cast) | declines | **succeeds, `[1,2,3,4,5]`** | **yes** |
| `c => c.Orders.Count` (reference collection) | declines | **succeeds** (spec suite, §1.2) | **yes** |
| `b => b.Title` (tier 1, control) | succeeds | succeeds | unchanged |

All five computed shapes convert, over all four array states. The A4 brief's three named shapes —
`Select(b => b.Posts.Count)`, `Select(b => b.Posts.Count(p => …))`, `Select(x => x.A * 2)` — are all in.

**Not admitted, deliberately, and each is another slice's:** a **widening** cast (`(long)b.Rank`) is absorbed to
a plain field ref before the gate sees it and so declines for the pre-existing EF-403 reason; a bare
**constant/parameter** leaf is group A3; a **dotted** leaf is EF-362's open scalar half; string concat, method
calls, `Negate`, `?:`, `??`, constructed values all fail `TryTranslateValue` first (they are the 670 firings of
§1.3). MEASURED for the widening cast, READ for the rest.

### 5.2 The scoreboard

MEASURED. **6 specification cases** (3 tests × 2 `async`), listed by name in §1.2. Zero of them is the owned
`.Count` shape the prerequisite blocks — Northwind has no owned collections — so **A4's spec yield and A4's
prerequisite are disjoint**. The prerequisite's cost is paid in the functional suite (§5.3) and in user code,
not on the scoreboard.

**What blocks the rest of the cited 28/54:** §1.3, and it is two things — the cited bucket counts leaves that
are not in A4's scope at all (wrapped bodies, already native), and 44 of the 86 in-scope firings sit inside a
subquery whose outer query declines for an unrelated reason.

### 5.3 Regressions, and the gate demonstrated live

MEASURED, EF10 functional suite, one build:

| | gate OFF | gate ON |
|---|---|---|
| functional | **2743 passed / 0 failed** / 52 skipped | 2735 / **8** / 52 |

The 8 split cleanly, and the split is the reason this run matters:

| kind | count | tests |
|---|---:|---|
| **tripwires that pin the tier-2 decline** — flipping them is the *point* of the slice | 6 | `NativeCastTests.Bare_cast_projection_leaf_declines_and_returns_correct_values`; `NativeOwnedCollectionCountTests.Bare_and_wrapped_count_projections_take_different_paths_from_the_same_model`; `…Bare_embedded_collection_Count_projection_returns_correct_counts_for_present_arrays` (asserts `aggregate([])`); `NativeOwnedCollectionFilteredCountTests.Bare_filtered_count_projection_declines_cleanly_under_NativeOnly`; `…Bare_filtered_count_projection_folds_client_side`; `…Bare_filtered_count_projection_with_a_captured_parameter_declines_cleanly_in_every_mode` |
| **genuine regressions — the §2 prerequisite** | 2 | `NativeOwnedCollectionCountTests.Bare_embedded_collection_Count_projection_returns_zero_for_a_missing_or_null_array`; `ProjectedCollectionNormalizationTests.Bare_count_projection_returns_zero_for_a_missing_or_null_array` — both `MongoCommandException` |

Both regressions are the missing/explicitly-null array. **The committed suite already catches the
prerequisite**, which is the strongest available evidence that it still reproduces, and it is also the live
demonstration that the gate is doing something: with `MONGODB_EF_SPIKE_A4` unset the same binary is green.

One of the six tripwires is a bonus rather than a flip:
`Bare_filtered_count_projection_with_a_captured_parameter_declines_cleanly_in_every_mode` pins a shape that
hard-fails in **every** mode today; with tier 2 it returns data. INFERRED that this is an improvement rather
than a new hazard; **UNVERIFIED** against an oracle.

---

## 6. Proposed task split, with a case count per task

**Total measured yield: 6 specification cases, plus one every-mode hard-fail shape that starts working.**
Sequenced so the correctness work lands before the capability that needs it.

| task | what | spec cases | why this order |
|---|---|---:|---|
| **A4-0** | **The `$ifNull` prerequisite, alone, with no tier-2 admission.** Rewrite the pushed-down bare collection-`.Count` body in `CapturedExpression` to its `??`-coalesced form (§3.2). Ragged-fixture coverage (present / empty / missing / explicit null) on the late-decline route **and** on explicit `DriverLinq`. | **0** | Delivers nothing on its own — the shape is not yet pushed down, so the rewrite is inert. Shipping it first means the tier-2 commit is a pure capability change with the correctness hole already closed, instead of a slice that opens a `MongoCommandException` under the default mode and closes it in the same diff. |
| **A4-1** | **Tier-2 admission for the non-count leaves**: arithmetic `MongoBinaryExpression` and `MongoConvertExpression`, under the reserved `_v` alias and `ProjectionAliasTier.Synthetic`. | **2** | These two node kinds are measured NOT exposed to the prerequisite at all (§2.2), so this task is safe independent of A4-0. It is also the only task that converts a `Failed→Passed` case. |
| **A4-2** | **Tier-2 admission for the two size node kinds** (`MongoSizeExpression`, `MongoFilteredSizeExpression`), plus re-basing the 4 `AssertMql` baselines. | **4** | Depends on A4-0 for the unfiltered owned count. The 4 cases are reference-collection counts, whose baselines move only in stage order. |
| **A4-3** | **Flip the 6 decline tripwires and replace them with routing + ragged-value pins**, each carrying a mandatory parameterized-`Where` late-decline leg. | 0 | §5.3 names all six. They are deliberate tripwires; flipping them is a visible edit, which is the whole reason they exist. |
| | **total** | **6** | |

**Sizing sanity check:** `0 + 2 + 4 + 0 = 6`, which is the §1.2 measured total, and 3 tests × 2 `async` legs.

**Do NOT plan A4 against 28.** And if A4-1 alone were shipped, its yield is **2** — that is worth knowing before
anyone treats "the cheap half" as the majority of the slice.

### 6.1 Recommendation

**A4 is worth doing, but it is a ~6-case slice with a real (if small) prerequisite, and it should be scheduled
on that basis — well below where the cited 28 put it.** The three reasons to do it at all are that the
machinery is already in the tree and unreached (§4.3), that the prerequisite turned out cheap and measured
(§3), and that the shapes it enables (`Select(b => b.Posts.Count)`, `Select(x => x.A * 2)`) are ordinary user
code whose current disposition is a full-document fetch and a client-side fold. The reason not to rush it is
that its scoreboard contribution is 6, and at least one currently-cited slice above it on the board is likely to
be worth more per unit of work — **which slice, and by how much, is UNVERIFIED here and needs the same
prototype-A/B treatment before anyone commits an order.**

**What would change this recommendation to "don't":** if A4-0 turns out to interact badly with the two existing
`CapturedExpression` mutations (§3.2's UNVERIFIED ordering point), the prerequisite stops being small and a
6-case slice stops being worth it. That is the one thing an implementation plan should check first.

---

## 7. How to reproduce

```bash
SCRATCH=<scratchpad>/a4-spike
git worktree add $SCRATCH/wt 86a5396f
```

Three throwaway edits in the worktree, all `internal`, all removed with it:

1. **`NativeTranslation/NativeProjectionBinder.cs`** — in the bare (`default:`) branch, when
   `TryDeriveDocumentPathAlias` declines, admit `MongoSizeExpression` / `MongoFilteredSizeExpression` /
   `MongoConvertExpression` / an arithmetic `MongoBinaryExpression` (match on **`Operator`**, not `NodeType` —
   `MongoExpression` subclasses report `ExpressionType.Extension`, and matching `NodeType` silently admits
   nothing) under alias `"_v"` and `ProjectionAliasTier.Synthetic`. Gated on `MONGODB_EF_SPIKE_A4=1`. Plus an
   `MONGODB_EF_SPIKE_A4_INSTR`-gated file append recording every bare-branch outcome (§1.3).
2. **`Visitors/MongoShapedQueryCompilingExpressionVisitor.cs`** — `ShouldStripBareProjectionOnFallback` honours
   `MONGODB_EF_SPIKE_A4_STRIP=on|off` before falling through to `HasDocumentPathAliasOverride` (§4.1).
3. **`tests/.../FunctionalTests/Query/A4SpikeProbeTests.cs`** (new) — five probes writing an outcome table to
   `MONGODB_EF_SPIKE_A4_REPORT`. The seed self-checks all five array states. `P4` uses
   `collection.AsQueryable()` so the driver's rendering is measured with no EF in the loop.

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"        # ONE build, never in parallel with EF8/EF9

# the probe A/B (four configurations, one build)
for cfg in "0:" "1:" "1:on" "1:off"; do … MONGODB_EF_SPIKE_A4=$g MONGODB_EF_SPIKE_A4_STRIP=$st dotnet test … ; done

# the spec A/B, both axes
MONGODB_EF_NATIVE_ONLY=1 MONGODB_EF_SPIKE_A4=0|1 dotnet test tests/…SpecificationTests… --logger trx
                         MONGODB_EF_SPIKE_A4=0|1 dotnet test tests/…SpecificationTests… --logger trx

# the functional regression A/B
MONGODB_EF_SPIKE_A4=0|1 dotnet test tests/…FunctionalTests… --filter "FullyQualifiedName!~A4SpikeProbeTests"
```

Compare the `.trx` pairs as `(testName → outcome, message)` **sets**. A count-only comparison reports +2 where
the answer is 6.

**One process hazard, recorded because it cost a round:** the first spec A/B was launched and the `src/` binary
was rebuilt while it was still running, so its four runs did not all use the same binary. Every figure in this
document comes from the **second** set of runs, launched after the final build. Do not overlap a rebuild with an
in-flight `--no-build` sweep.

---

## 8. What is UNVERIFIED

- **Which enclosing construct blocks each of the 44 non-converting arithmetic firings** (§1.3). The inner-scope
  parameter naming makes "the bare `Select` is inside a subquery" the obvious reading, but it was not confirmed
  per case, and no per-test attribution was built.
- **Firings vs cases.** The 86/670/1155 figures are translation firings, not test cases. No instrumentation tied
  a firing to a test name, so no conversion *rate* can be computed from them, and none is claimed.
- **The ordering of a third `CapturedExpression` mutation** against `StripPushedDownSelect`'s two existing call
  sites (§3.2). Argued from READ as mutually exclusive; not executed.
- **Whether the `??` rewrite is expressible cleanly for every collection-navigation CLR type** (`HashSet<T>`, a
  custom collection). Only `List<T>` was measured (§3.1).
- **A primitive-collection bare `.Count`** (`b.Tags.Count`) — never reaches this path today, so its exposure to
  the same bare-`$size` abort is untested.
- **Whether the `Bare_filtered_count_projection_with_a_captured_parameter…` improvement is correct** (§5.3). It
  starts returning data where it hard-failed in every mode; no oracle was run against it.
- **Whether the 4 re-based baselines are the only MQL movement on the default-`Native` axis for a *widened* tier
  2.** The prototype admits exactly four node kinds; a shipped slice that admits more would need its own sweep.
- **Nothing here re-derived** the stream-1 spike's own instrumented classifier, the `(b)`/`(c)` partitions, the
  per-test-class table, or any figure for a slice other than A4. Where this document and the stream-1 spike
  disagree about A4, this one is the measurement and that one is the estimate; nothing is claimed about the
  other 19 rows of that table beyond the shared lesson in §1.3.
