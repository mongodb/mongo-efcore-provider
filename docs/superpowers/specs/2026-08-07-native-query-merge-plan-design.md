# EF-322 — plan reset: what ships in the first native-query release

*Written 2026-08-07 against `NativeQueryOngoing` at `95162c86`. This supersedes the "land at parity/cutover"
plan recorded in draft PR #324 and in `docs/native-query-status-EF-322.md` §9.8.*

*Every number here is **MEASURED** at `95162c86` unless tagged otherwise. The measurement method is recorded in
status-doc §9.0 and must be reused for the checkpoint in §7 — this project has twice had a plan built on a
bucket label that was wrong by 40%, so a number's provenance is part of the number.*

---

## 1. What changed, and why this document exists

The branch has been built toward a single event: reach parity with driver-LINQ, retire it, merge. That plan is
withdrawn. Two owner decisions replace it:

1. **Merge early, at reasonable coverage, and track the rest.** The bar is **~80% coverage, no major
   architectural issues, and good tracking of the remaining work.** Correctness gaps are acceptable *if* they
   are well-defined, uncommon, and tracked.
2. **The driver path is not removed in this release.** It ships as-is and is retired later. How it is
   eventually deprecated is a **separate project** and is explicitly out of scope here — this plan makes no
   public-surface changes to the query-mode API.

Decision 2 is the one that reshapes the engineering, and its consequences are easy to miss — see §6.

---

## 2. Where native actually stands

| | Cases |
|---|---:|
| EF10 specification cases run | 4593 |
| Passing under `MONGODB_EF_NATIVE_ONLY=1` | **2427** |
| Failing | 2166 |

The 2166 partitions three ways (MEASURED by decline-site instrumentation, five sweeps, each reproducing the
baseline exactly):

| Partition | Cases | Meaning |
|---|---:|---|
| **(a) genuine coverage gaps** | **1598** | needs driver-LINQ for correct *results* — the only partition that gates coverage |
| (b) fails in every mode | 518 | unsupported in native *and* driver-LINQ; closing them is not native work |
| (c) bookkeeping | 50 | `AssertMql` baseline diffs a re-baseline resolves |

**Addressable surface = 4593 − 518 = 4075. Native handles 2427 of it — 59.6%.**

Reaching 80% needs **+833**. The plan below delivers **+922 → 82.2%**, deliberately leaving ~90 cases of margin
(§7 explains why the margin is not optional).

---

## 3. What ships: three streams

### Stream 1 — translator breadth (**588 cases**)

The largest single item on the board, and it has had no ticket, no slice and no place in the execution order —
because it was split across two buckets that looked like separate work.

The same ~20 features recur in **predicate**, **sort-key** and **projection-leaf** position: casts (72),
tuple/anonymous comparison (50), `EF.Property` (48), bare constant/parameter (40), `Nullable.Value` (38),
client-collection `Contains` (36), entity equality (34), `??` (32), string concat (32), `?:` (26), and a tail.
The bucket labels attributed them to "predicate breadth" (368) and "the projection long tail" (220)
respectively. **They are one capability**, and `MongoExpressionTranslator` is the one place that has to learn
them.

588 is 37% of the entire coverage gap.

#### AMENDED 2026-08-07, after the stream-1 spike measured this stream

The spike (`docs/superpowers/specs/2026-08-07-stream1-translator-breadth-spike.md`, commit `6f11c5c4`)
re-derived the above and **confirmed the "one capability" claim for two of the three positions and REFUTED it
for the third.** Predicate and projection-leaf converge on literally the same method — `TranslateOperand`,
called from `TranslateComparison` and `TryTranslateValue`, differing only by `allowNumericWidening` — which is
stronger than this section claimed. **Sort key is a different capability**: `TryTranslateField` returns a
`MongoFieldExpression` and `RenderSort` hard-throws for anything else, because MQL `$sort` takes field paths
only.

**Consequently a computed-sort slice is added to stream 1** (owner ruling, 2026-08-07). It is IR, lowerer and
renderer work — `$set` + `$sort` + `$unset` — **not** a translator arm, and it is the one piece of stream 1
that is not translator breadth at all. It **enables ≥92 cases and delivers none by itself**, so it must be
sequenced before the sort-position feature slices rather than measured on its own.

**Measured yield, replacing the 588 above as the number to plan against:**

| | cases | closes when |
|---|---:|---|
| sole-cause, one feature | **474** | that feature's slice ships |
| co-blocked on stream 2 (set ops 32, `Distinct` 26, aggregate 4) | 62 | **streams 1 + 2 both land** |
| needs two stream-1 features | 34 | both slices ship |
| additionally blocked by **deferred** work | 12 | **never, this release** |

So stream 1's bucket yields **≈474 after stream 1 alone** and **≈570 once streams 1 and 2 have both landed**.
There is **no double-count** with stream 2 — the buckets are partitioned by *first* decline site — but there
is a real **dependency** that this document did not previously record.

### Stream 2 — the sole-cause tranche (**282 cases**)

Five small gaps, chosen because **250 of the 282 are sole-cause**: nothing else declines them, so opening one
gate is expected to convert the whole group. Cheapest cases on the board per unit of work.

| Gap | Cases | Sole-cause |
|---|---:|---:|
| Scalar-aggregate binder | 82 | **82** |
| `Distinct` | 84 | 64 |
| Set operations | 42 | 42 |
| No-binder operators | 40 | 40 |
| Post-terminal guards | 34 | 22 |

### Stream 3 — slice 3b, the entity leaf in a projection (**52 cases**)

Already designed as part of step 3. It is in the plan for a correctness reason rather than a coverage one:
**EF-356 lives in the mixed shaper and is silent wrong data on a mainstream projection shape.** The owner's
policy admits deferred correctness gaps only when they are *uncommon*; this one is not. 3b **fixes** EF-356
rather than pinning it (a standing ruling from 2026-08-06).

### Stream 4 — EF-375, the join-key weakness (**correctness, ~0 coverage**)

Pulled in on 2026-08-07 after the Task-1 ticket audit (see §4). It is a **targeted defect fix**, not the joins
stream: EF-375 has a known location — the agreement check in `TryResolveIntermediateLookupPrefix` carries a
`TODO(EF-375)` — and the rest of joins (373 cases) stays deferred under EF-392.

**It adds correctness, not coverage.** Its cases sit inside the deferred joins bucket, so they stay failing
either way and **the 82.2% target is unchanged.** It is in the plan because a defect on
`Include(A).Include(B)` over a self-referencing model is not an uncommon shape, and the owner's policy admits
deferred correctness gaps only when they are.

**UNVERIFIED:** whether the fix is genuinely targeted or drags in the joins machinery. Its slice opens with a
spike that must answer that first — if it is not separable, that is a finding to bring back, not to push
through.

**Total, MEASURED-based and superseding the +922 figure used above:**

| | cases |
|---|---:|
| current | 2427 |
| stream 1's bucket, after streams 1 **and** 2 | +570 |
| stream 2's own first-decline-site cases | +282 |
| stream 3 (slice 3b) | +52 |
| **projected** | **3331 / 4075 = 81.7%** |

**INFERRED** by composing the spike's MEASURED per-bucket table across streams; the spike measured each bucket,
not this sum.

**The computed-sort slice is load-bearing for the bar, not an optimisation.** Without it the ≥92 sort-position
cases do not convert, giving ≈3239/4075 = **79.5% — under the 80% bar.** That is why it was ruled in rather
than deferred. Stream 4 contributes correctness, not cases.

---

## 4. What is deferred, and tracked

Per the "few cases → ticket" rule, plus two large streams the arithmetic says are not needed for 80%:

| Deferred | Cases | Note |
|---|---:|---|
| **Joins / cross-collection** | 373 | the biggest deferral, and the largest departure from the old plan, which ranked it first |
| **GroupBy breadth** | 130 | |
| Composite-PK member access | 116 | only 12 sole-cause, so its real yield is much smaller than 116 |
| Slice 3d — composition relaxations | 18 | depends on 3a, already landed |
| Non-constant regex | 19 | EF-247, JIRA status `Blocked` — check what it is blocked on before scheduling |
| `Not` over an unsupported subtree | 8 | |
| Stragglers | ~12 | 4 each |

**Every row above needs a ticket before merge** — that is a merge-bar item ("good tracking"), not
housekeeping. Existing: EF-382, EF-390, EF-391. The joins stream also carries EF-375/376/377/380/381.

### Correctness gaps that ship

Admitted under the owner's policy — **well-defined, uncommon, tracked**. Each needs a ticket carrying a
reproduction and an explicit statement that it is *silent* rather than loud:

- **EF-380** — silent wrong data under the default mode (from the EF-379 slice's residuals). Amended
  2026-08-07: its original text implied `Native`-only, but its own reproduction table shows `DriverLinq`
  produces the identical wrong result.
- **EF-390** — dotted owned-hop scalar leaf returns `null`; pinned by a test asserting today's wrong behaviour,
  which must be **inverted** when fixed, not deleted.
- **EF-355** — carried.

**Two tickets are explicitly NOT in this list**, both because they failed the *uncommon* half of the criterion:

- **EF-356** — see §3 stream 3.
- **EF-375** — **pulled into the plan 2026-08-07.** The Task-1 audit found its throw symptom fires on
  `Employee.Include(Manager).Include(Mentor)` — its own text calls that "an ordinary modelling pattern", i.e.
  any self-referencing model with two same-typed navigations. Well-defined and tracked, but not uncommon. See
  §3 stream 4.

---

## 5. Architecture — the "no major issues" half of the bar

Three items. None blocks merge; all need a decision recorded rather than a ticket filed and forgotten.

1. **The bulk path calls `TranslateQuery` directly.** `BuildIdDocumentQuery` does not build its own bridge; it
   calls the fallback's own entry point and then uses `MongoExecutableQuery.Query`/`.Provider` — two members
   status-doc §9.4 had written off as dead. So EF9+ `ExecuteUpdate`/`ExecuteDelete` runs through the query
   gate. **Under decision 2 this is no longer a blocker** (the bridge stays), but it is real coupling and
   should be recorded as known debt with an owner.
2. **The parity oracle.** The primary correctness instrument today is `Native == DriverLinq`. Retiring the
   driver path deletes it. **Decision 2 defers this entirely** — but it must be scheduled *before* retirement
   is scheduled, because it has the longest lead time of anything on the board.
3. **`ProjectionAliasTier.Synthetic` is unreachable** after the tier-2 revert. Housekeeping, but a reviewer
   will ask; either remove it or comment it as deliberate.

### No public-API work in this plan

Deprecating the driver path is a **separate project**, deferred by the owner. This plan therefore makes **no
changes to the query-mode public surface** — nothing gains `[Obsolete]`, nothing is renamed or removed.

One fact worth carrying into that later project, MEASURED here so it does not have to be re-derived:
`git grep -c QueryMode` returns **zero** at `v10.0.2`, `v9.1.2` and `v8.4.2`. The entire query-mode surface
(`MongoQueryMode`, `UseQueryMode`, `MongoOptionsExtension.QueryMode`/`WithQueryMode`) is additive within this
unreleased cycle and **has never shipped** — so it can still be shaped freely, and becomes a compatibility
commitment only once it is released.

---

## 6. What decision 2 changes, stated plainly

Because it is easy to carry the old plan's assumptions forward:

- **Retiring driver-LINQ is no longer the goal of this work.** Status-doc §9 is titled "what must be done
  before driver-LINQ can be retired without regression"; it remains accurate and becomes a *later-phase*
  document rather than the plan of record.
- **Coverage gaps are not correctness risks.** Every one of the 1598 falls back and returns correct results
  today. This is why merging at 82% is safe at all.
- **The bulk-path coupling and the oracle replacement both leave the critical path** (§5.1, §5.2).
- **The testing strategy does not change**, and does not need to. The `Native`/`NativeOnly`/`DriverLinq`
  three-axis method stays available for as long as the driver path ships.

---

## 7. Risk, and the one checkpoint that is not optional

**"Sole-cause" is a leverage proxy, not a guarantee.** It means nothing else declined at *population* time. The
lowerer or the renderer can still fail once a gate opens, so stream 2's 282 is an upper bound, not a promise.
Stream 1's 588 carries the same risk in smaller proportion.

**Therefore: re-measure after stream 1, using the §9.0 method, before committing to the rest.**

**CORRECTED 2026-08-07 — the trigger above was wrong and would have misfired.** This section originally said to
pull joins or GroupBy back in "if stream 1's yield comes in materially under 588". The spike measured that the
expected yield after stream 1 **alone** is **≈474**, because 62 cases wait on stream 2, 34 need a second
stream-1 feature, and 12 cannot convert this release at all. **≈474 at the checkpoint is success, not
shortfall.** Judging it against 588 would have triggered a scope expansion that the measurement does not
justify.

The revised trigger: expect **≈474 after stream 1**, and **≈570 after streams 1 and 2 together**. Pull deferred
work back in only if the post-stream-2 figure lands materially under ≈570. Note that 474 is itself an upper
bound — sole-cause means nothing else declined at *population* time, and the lowerer or renderer can still
decline afterwards.

---

## 8. Verification bar

Unchanged from the last two slices, and non-negotiable:

- Full solution green on **EF8, EF9 and EF10**.
- Both axes compared against a base worktree. **Measure wins by message TRANSITION, not by failing-name set** —
  a name-set diff reported 2 wins where there were 74 in slice 3a, because most wins move from one failure
  message to another.
- **Classify `Assert.Throws` failures FIRST** when bucketing; their message quotes the inner exception and a
  naive match over-counted by 149.
- **Zero `#if` lines added or removed in `.cs` under `src/`.**
- Every guard test **mutation-verified**.
- **A parameterized-`Where` leg for every shape**, and attention to **execution order inside tests** — an eager
  `ToList()` on a throwing leg masks a silent one that runs later. Both lessons were learned by shipping
  falsely-green tests in this branch.
- Breaking changes measured by **executing against the published packages**, never inferred from the branch.

---

## 9. Sequence

1. **File the deferral tickets** (§4) — merge-bar item, and cheapest done first.
2. **Stream 1 — translator breadth**, as several slices split by feature group.
3. **Checkpoint: re-measure** (§7).
4. **Stream 3 — slice 3b** (fixes EF-356), then **stream 4 — EF-375**, spike first (§3 stream 4).
5. **Stream 2 — the sole-cause tranche.**
6. **Architecture record** (§5) — the three debt items, decided and recorded, not silently carried.
7. **Final measurement, status-doc update, merge.**

Streams 2 and 3 are independent of each other; 3b is placed before stream 2 only so the silent-wrong-data fix
lands earlier.
