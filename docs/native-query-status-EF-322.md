# Native LINQ Translation (EF-322) — Status Report

*Generated 2026-07-26 · **last updated 2026-08-08** · currently on `NativeQueryOngoing` at or above `2ad8524a`,
stacked on `main` and unmerged. (The header said `2431bbf0` until this revision; stream 1 tranche 1 has landed
four commits since — `16bf9a20`, `1d164597`, `4adafc2c`, `2ad8524a` — see §2. Before that it said `99d74735`,
and before that `EF-322-step3a` @ `1c470704` above tip `9065acfc`. **The §9 measurements below were taken at
`99d74735` and that provenance is unchanged** — only the header's "where the branch is" claim moved, and §8
records where the two spec axes now stand.)*

> **UPDATED 2026-08-08 — STREAM 1 TRANCHE 1 HAS LANDED (EF-398), INCLUDING SLICE B (EF-401).** Slice 0 (the
> `partial` split), **A2** (`EF.Property` leaf, **34** `NativeOnly` wins MEASURED), **A5**
> (`Nullable.Value`/`HasValue`, **0** wins MEASURED), the slice-B spike, and **slice B itself** (computed sort
> keys via `$set`/`$sort`/`$unset`, **12** wins MEASURED — a RE-ATTRIBUTION inside the 474, so ≈508 does not
> move). `NativeOnly` **2427/2166/17 → 2473/2120/17**; default `Native`
> **4593/0/17**, unmoved. New material is in **§2** (the tranche table and three findings), **§8** (the running
> position against the checkpoint) and **§9.8** (steps 2 and 3, corrected in place). **Three things to carry
> away before planning the next tranche:**
> 1. **The spike's "sole-cause" figure is unreliable for any feature group whose feature is an INNER node** —
>    A5 sized 36 sole-cause and converted 0, because fixing an inner node RELABELS cases instead of closing
>    them. The merge plan's **≈508** post-stream-1 checkpoint is a sum of those figures and is inflated by an
>    unmeasured amount. §2 finding (1).
> 2. **Slice B delivers 12 on its own, but that is a RE-ATTRIBUTION of cases already inside the 474** — ≈508
>    and ≤3257 do not move and the 12 must **not** be added to them. §2 finding (2).
> 3. **At least 36 sort-position cases need an AGGREGATION-dialect renderer arm that no document named** —
>    A6 (18) and A13 (18) introduce no new node kind, so the stream-1 spike's existing obligation misses them.
>    §2 finding (3).

> **UPDATED 2026-08-07 — §9 REWRITTEN AS-MEASURED (EF-391, first half).** Every count in §9 was re-derived at
> `99d74735` by instrumenting the *decline sites* rather than bucketing failure messages, and reproducing the
> baseline exactly on both axes. **Three things to carry away before reading further:**
> 1. **§7 is now a historical snapshot.** Its totals (2395 / 1742 / 651) are superseded by §9's
>    **2166 / 1598 / 518 / 50**. §7.4's *method* is still current and is what was re-run.
> 2. **The binding constraint on retirement is not what this document has said for weeks.** The largest single
>    lever is `MongoExpressionTranslator` **expression breadth** — ≈588 of the 1598 real gaps, spanning
>    predicates, sort keys and projection leaves as one capability — and it has no slice, no ticket and no
>    place in the execution order. §9.7 is re-ranked and §9.8 gains steps 4–11.
>    ***AMENDED 2026-08-07 by the stream-1 spike***
>    (`docs/superpowers/specs/2026-08-07-stream1-translator-breadth-spike.md`)***: the measured figure is 580;
>    its realistic yield is 474 sole-cause, ≈508 after all of stream 1, ≈570 once stream 2 has also landed;
>    and it is TWO capabilities, not one — sort key needs a computed-sort slice no translator arm can reach.
>    Plan from §8's plan-of-record paragraph and §9.8's sequence, not from the ≈588.***
> 3. **Two long-standing labels measured out FALSE**: the "66 `ArgumentOutOfRangeException` — a
>    materialization/shaper gap" bucket is 48 `AssertMql` baseline failures and *zero* shaper bugs, and
>    "composite-PK member access … Northwind does not cover it" is 116 measured cases, 112 of them inside the
>    reference-`Include` suites.
>
> **Also settled by measurement: the public-API worry in §9.4 is a non-issue.** The whole `MongoQueryMode`
> surface exists at **none** of `v10.0.2` / `v9.1.2` / `v8.4.2`, so removing it at cutover restores the
> released shape rather than breaking it.

> **UPDATED 2026-08-06 for §9.8 steps 2 and 3a.** Two slices have landed since `9065acfc`: the `VectorSearch`
> slice (step 2) and **step 3a, the bare-projection boundary**. §2 gains a table for them; §4, §5, §8, §9.1 and
> §9.8 are corrected in place. **Two things to carry away before reading further:** step 3 is **four** slices
> (3a done; 3b/3c/3d outstanding), and 3a carries this epic's **first `BREAKING-CHANGES.md` entry for a
> native-routing flip** — the other flips were rubric carve-outs, this one changes a materialized value.

> **⚠ READ THIS BEFORE TRUSTING ANY SHA BELOW (added 2026-08-05).** The stack was rebased onto
> `upstream/main` = `58e05a0e` after most of this document was written, so **every SHA cited in §2 and in the
> hash-bookkeeping note below is a pre-rebase hash.** Those objects still exist — the safety branch
> `NativeQueryOngoing-prerebase` keeps them alive — but **none of them is on the current branch**, so
> `git merge-base --is-ancestor <sha> HEAD` fails for all of them. The commit *subjects* are unchanged, so map
> old → new with `git log --oneline upstream/main..HEAD` and match on subject. Four mappings already
> established: slice 7 `cfe873e`→`9b641549`, EF-358 fix `7c199e4`→`46e5a3f8`, slice 8 `33fdc58`→`d63659fc`,
> slice 9 `229294f`→`3483fc60`. The remaining rows have not been re-derived.
>
> **Also stale: §7's measurements.** (Superseded again 2026-08-07 — see the banner at the top of §7 and
> §9.) They were taken at pre-rebase tip `229294f`; **seven** slices have landed
> since (§2's joins table — this read "six" until EF-379 landed). Treat §7's numbers as a floor of unknown
> tightness, not as current, and re-measure per §7.4 before relying on them. (EF-379 specifically moved neither
> spec axis — see §2 — so it does not by itself widen the gap between §7 and reality.)
*Hash bookkeeping, corrected at this revision: the previous header cited tip `1b4c1d6` with slices 7–9 sitting
on unsquashed side branches. **All three have since been squashed onto `NativeQueryOngoing`**, so they now have
real, citable SHAs and the §2 slice table records them: slice 7 = `cfe873e` (was `f163392` + `0cb1b1b`),
slice 8 = `33fdc58` (was "branch `EF-360`"), slice 9 = `229294f` (was "branch `EF-359`"). The EF-358 fix,
which is a bug fix rather than a slice and which the old table did not list at all, is `7c199e4`, and sits
between slices 7 and 8. Earlier corrections retained for the record: `b087957` is slice 5 (`All` predicates),
and `7532b15` was never in the shipped history — it exists only on the pre-squash safety branch
`EF-322-owned-collection-count-native-presquash`.*
*Test measurements below are point-in-time against the **EF10 specification suite**. §7 was **fully
re-measured at this tip (`229294f`) on 2026-07-31** — both sweeps re-run from scratch, and §7.1 *and* §7.2
re-derived from the fresh `nativeonly.trx` rather than carried forward. The totals and the entire per-class
table reproduced the slice-7 figures exactly; §7.2's buckets shifted slightly and are re-stated there.*
*Branch position (2026-08-05): **53 commits ahead of `upstream/main` and 0 behind**. The rebase onto `58e05a0e`
(EF-317, C# driver 3.10.0) that the previous revision listed as outstanding **has been done** — `upstream/main`
is now an ancestor of the tip.*

---

## 1. The epic in one line

Epic **EF-322 — "Native LINQ query provider (ground-up rebuild)"** replaces the *translation* half of
the Query subsystem: the provider builds MongoDB aggregation pipelines (MQL) itself from a canonical query
AST and uses the C# driver only to *execute* them (BSON, cursors, sessions, transactions). ~~Driver-LINQ
remains as a gated fallback (`MongoQueryMode.DriverLinq`) until native reaches parity, then the delegation
code is deleted.~~

**CORRECTED 2026-08-07 on the final whole-phase review — that last clause is a flat restatement of the
withdrawn parity/cutover plan, and it is the FOURTH such survivor found in this document.** It survived here,
unqualified, in the very first section a resuming agent reads, while §5, §8 and §9 were all corrected around
it. What replaced it: **driver-LINQ remains as a gated fallback (`MongoQueryMode.DriverLinq`) and SHIPS in
this release.** The plan of record is to merge at **~80% coverage** with the driver path intact and the
remaining gaps tracked — not to reach parity first. Retiring the fallback and deleting the delegation code is
a **separate, later, out-of-scope project** with no date attached to it. Full detail in §8 ("the plan of
record, stated once") and `docs/superpowers/specs/2026-08-07-native-query-merge-plan-design.md`; §9 remains
accurate but describes that later phase, not this release.

**Native is already the default execution path.** Per the provider's versioning rubric this is *not* a
breaking change: query results are unchanged, any shape native does not support falls back automatically,
and `UseQueryMode(MongoQueryMode.DriverLinq)` restores the previous path.

Query modes:

- `Native` (default) — build the pipeline natively; silently fall back to driver-LINQ for unsupported shapes.
- `DriverLinq` — always use the driver's LINQ provider (the pre-EF-322 path).
- `NativeOnly` — native or bust; throw `NativeTranslationNotSupportedException` instead of falling back
  (a diagnostic mode — a full run is a "what actually goes native" report).

---

## 2. Sub-project scoreboard (7 planned)

| SP | Scope | Ticket | Status |
|---|---|---|---|
| SP1 | AST foundation — filter / sort / paging | EF-323 | ✅ Done |
| SP2 | Predicate breadth — `$expr` renderer + operator long tail | EF-329 | ✅ Done |
| SP3 | Projection pushdown — server-side `$project` | EF-331 | ✅ Done |
| SP4 | Scalar cardinality — Count / First / Any / aggregates | EF-336 | ✅ Done |
| SP5 | Collection Includes | EF-339 | 🟡 Flat collection Include done; several shapes deferred |
| SP6 | Remaining operators — GroupBy, SelectMany, set-ops, Distinct, OfType, non-canonical paging | EF-344 / EF-347 | 🟡 Largely done; long tail deferred (**`VectorSearch` is no longer deferred — delivered 2026-08-06, see §4**) |
| SP7 | Materializer perf — one-pass stream → POCO | — | 🟡 **Phase 1 done** (one-pass materializer, `e38587f`); Phase 2 (streaming breadth) not started |

Beyond the seven planned sub-projects, an **owned-data (embedded-document) work stream** has since landed as
nine further stacked slices — these were not a planned SP, but they are where native coverage grew most after
SP7 Phase 1. (This paragraph read "eight" until slice 9 landed.)

| Slice | Scope | Commit |
|---|---|---|
| 1 | Owned single-reference whole-entity queries go native (+ stream) | `690b487` |
| 2 | Owned-collection whole-entity goes native (+ streams) | `275c90e` |
| 3 | Owned single-ref **sub-property** predicates / sorts / projections (dotted paths) | `2a9b56e` |
| 4 | Owned-collection **`Any`** quantifier predicates → `$elemMatch` | `791037b` |
| 5 | Owned-collection **`All`** quantifier predicates → negated `$elemMatch`; **closes EF-335** | `b087957` |
| 6 | Owned-collection **`.Count`** in a predicate — array-index `$exists` (constant tier) / null-safe `$size` inside `$expr` (parameterized/degenerate tier) | `1b4c1d6` |
| 7 | Owned-collection **`.Count` as a PROJECTION leaf** → `{$size: {$ifNull: […]}}` in `$project`; **partially resolved EF-357 at the time** (bare-scalar form no longer fails translation) — EF-357 was later **fully** closed by EF-358, see below | `cfe873e` |
| — | *(not a slice — the EF-358 bug fix)* A missing or explicitly-null embedded array materializes as an **empty collection** on every path, mode and cardinality; closes EF-357's residual | `7c199e4` |
| 8 | Owned-collection **ARRAY leaf as a PROJECTION leaf** → the array projected by alias inside `$project` (`Select(b => new { b.Title, b.Posts })`); carries **EF-360** (re-characterised) and files **EF-362** | `33fdc58` |
| 9 | Owned-collection **FILTERED `.Count(pred)`** → `{$size: {$filter: {input: {$ifNull: […]}, as: "e", cond: …}}}`, native both in a predicate (`$expr` tier only) and as a `$project` leaf; **closes EF-359**; files **EF-365** | `229294f` |

### The joins / reference-`Include` work stream (§9.8 step 1) — seven slices, 2026-08-03…05

*(This heading read "six" until EF-379 landed. So did the header's stale-§7 warning and §9.8's status
paragraph; all three are recounted rather than left to drift.)*

This is the *execution order's* step 1 and it is now substantially delivered. All SHAs in this table are
post-rebase and **are** on the current branch.

| Slice | Scope | Commit |
|---|---|---|
| — | **EF-366** — decline a join whose inner sequence is paged (CSHARP-6017). A fix wave found a **second doorway** via `Distinct`; the guard belongs at the shared site, not per entry point | `0162b737` |
| — | **EF-367** — make the four `Include` spec suites fail on wrong data. Filed on a premise ("~40 masked failures") that **measured false — the real answer was zero**; kept because the suites genuinely could not detect wrong data | `5dfb1653` |
| — | **EF-370** — correct required-navigation `$unwind` semantics and stop dropping composed operators (ported) | `7af4190b` |
| 1 | **EF-368** — single-level reference `Include` goes native. The first joins slice | `34a02067` |
| 2 | **EF-372** — scope a transitive join's `$lookup.localField` at any depth, **or decline**. Fixes silent 0 rows at hop 3+ | `6a7a5f3c` |
| 3 | **EF-373** — emit a join's `$lookup` on the correct side of an interleaved operator. Fixes a silently wrong page when `Skip`/`Take` sits *between* two joins | `9dd6fc15` |
| 4 | **EF-379** — classify a join hop root-vs-transitive (`ClassifyJoinHop`, from `outerKeySelector.Body` alone) *before* consulting the root's navigations, so a transitive hop skips BOTH root tiers and reaches EF-372's prefix-or-decline resolver. Fixes a silently null navigation in every mode; files **EF-380** and **EF-381** | `9065acfc` |

**Slice 4's verification, recorded because the fix moved no spec number and would otherwise look unevidenced.**
EF8 **746 unit / 4708 spec / 2490 functional**; EF9 **746 / 5021 / 2538**; EF10 **739 / 4593 / 2593** — 0
failures each. EF10 `NativeOnly` spec: 2260 passed / 2333 failed / 17 skipped, with the **failing-test-name SET
byte-identical to base** (compared by NAME, not by count — a count match is not the same claim). Zero `#if`
lines added or removed under `src/`. Seven mutations were run against the new 19-case
`Ef379RootNavigationMisroutingTests`: **A** force-transitive → 11 red; **B** gate tier 1 only (the fix as
FILED) → 8; **C** gate tier 2 only → 5; **D** re-add the withdrawn decline → 7; **E** declaring-type conjunct
forced false → 8; **F** declaring-type conjunct forced true → **0 red**, recorded honestly as *unfalsified
hardening* rather than dressed up as covered (no fixture in the tree exposes a non-transparent-identifier
member named `Outer`/`Inner`); **G** "skip the root tiers only if a candidate exists" → 2 red, and G is the
UNIQUE discriminator for the third control fixture, which is why that fixture is not a duplicate of the two
doorways.

Three design facts from these slices that are expensive to re-derive *(this read "Two" until EF-379 added the
third)*:

- **"Decline" on the transitive-join path is a HARD translation failure, not a graceful fallback.** EF-372
  returns `null` (EF Core's own translation-failure path), which fails in *every* `MongoQueryMode` including
  explicit `DriverLinq` — `UseQueryMode` is not an escape hatch. A graceful `MarkNotNativelyRepresentable()`
  was tried and **measured strictly worse**: the un-rebound inner shaper reaches materialization and throws in
  both modes, because the decline is only ever reached once an EARLIER join's `$lookup` has already been
  registered — at translation time, before `MongoQueryMode` is read — so both paths are already committed to
  the flat shape. (This used to read "because a transitive hop is always a second-or-later join". That is
  FALSE — an owned `SelectMany` produces a transparent identifier too, so a `TransitiveHop` can occur at the
  FIRST join — and it was the premise behind a decline EF-379 shipped and then withdrew as a measured
  regression; see the EF-379 note in `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md`.) This matches how
  reference `SelectMany` and `Intersect`/`Except` already decline.
- **A pure `$sort` relocation has no observable effect on results.** `$sort` and a fan-out `$unwind` commute
  with respect to key order (`$unwind` preserves input order and expands each document into an adjacent
  equal-key run), so only an **MQL stage-order pin** discriminates EF-373's fix — a row-order assertion cannot.
  Measured: reverting to the pre-EF-373 contiguous group turns the stage-order pin red while the ordered data
  assertion still passes.
- **What separates a ROOT hop from a TRANSITIVE one is the member-name COMPOSITION of the key selector's
  receiver — nothing else, and three plausible alternatives are each MEASURED wrong (EF-379).** Not chain
  DEPTH (`s.Outer.Outer` is a genuine root hop; `j.Outer.Inner` is transitive at the same depth); not the
  receiver's CLR TYPE (a self-referencing model shares one between root and intermediate); and not
  `outer.ShaperExpression`, which the design note this slice started from assumed was required and which the
  spike refuted — the decision is fully decidable from `outerKeySelector` alone, and the shipped slice does not
  thread the shaper. (`ShaperExpression` remains the *more robust* source for the intermediate `IEntityType`;
  that is robustness, not necessity.) Also measured, and the reason the fix is broader than EF-379 was filed:
  **both root tiers misfire INDEPENDENTLY** — gating the FK-name tier alone (the filed fix direction) leaves
  both doorway fixtures red, because the TARGET-TYPE-only tier misfires with no name collision anywhere in the
  model.

These slices filed **seven** defects, all in the same neighbourhood and all unreleased — **EF-375, EF-376,
EF-377, EF-378, EF-379**, plus **EF-380** and **EF-381** filed by the EF-379 slice itself. **This sentence read
"five defects … EF-375, EF-376, EF-377, EF-378, EF-379" and is corrected rather than deleted: EF-379 is no
longer among the open ones — it is FIXED by slice 4 above (`9065acfc`)** — and the two residuals that fix left
behind are now tickets of their own. See §6 and §9.5; the ordering consequences are in §9.8.

No JIRA number was filed for slice 7's native-projection half. Two bugs it *measured* were filed: **EF-358**
(the projection-path null-collapse gap, whose closure also fully closed EF-357 — see §4 and §6) and **EF-359**
(filtered `Count(pred)` in a projection hard-fails in every mode). **Both are now CLOSED** — EF-358 by its own
slice, EF-359 by slice 9 above, which in turn filed **EF-365**. See §6.

### The cutover work stream after joins — §9.8 steps 2 and 3, 2026-08-06

| Step | Scope | Status |
|---|---|---|
| 2 | **`VectorSearch` goes native** — a dedicated `MongoSelectDefinition.VectorSearch` slot emitted ahead of every other stage, plus a deferred `Build`-time stage slot calling the driver's own stage builder per execution. `NativeOnly` `VectorSearch` failures **112 → 20**; default `Native` unmoved, zero baseline diffs | ✅ Done — see §4 |
| 3a | **The bare-projection boundary** — a terminal `Select` whose body is the leaf itself (`Select(p => p.Name)`, `Select(b => b.Posts)`) now populates `Projection` and emits a native `$project`, under an alias that IS the leaf's root-relative document path. **74 `NativeOnly` cases won, zero regressions**; `Native` 4593/0/17 unchanged, `NativeOnly` 2352/2241/17 → **2427/2166/17** (the triples are **75** apart while the win count is 74, and both are right: the 75th transition is `Multiple_queries`, already failing at base and passing only because its override was rewritten — 74 is the genuine feature win, 75 the raw transition count). Carries **EF-362** (`OwnsOne`-hop array leaves) as its Task 4 | ✅ Done — see §4, §5 and the two notes in `Query/AGENTS.md` |
| 3b / 3c / 3d | The rest of step 3 — **not started**. See §9.8 | ⬜ Outstanding |

**Step 3a is the first slice in this epic to carry a `BREAKING-CHANGES.md` entry for a native-routing flip, and
that is worth flagging because everything before it was carved out by the versioning rubric.** Verified by
executing against the published packages `v10.0.2` / `v9.1.2` / `v8.4.2`: projecting a **required
(non-nullable) property whose stored element is absent or explicitly BSON `null`** returned the CLR default at
every release tag and now throws `InvalidOperationException` under the default `Native` mode, because the value
is read by the provider's own shaper (which enforces required-property presence) instead of by the driver's
lenient deserializer. The entry is scoped to the **class**, covering the WRAPPED spelling too — that half
landed earlier in this same unreleased cycle and was undocumented. A whole-entity read of those documents
already threw at every released version, so this makes the two read paths agree, and
`UseQueryMode(MongoQueryMode.DriverLinq)` restores the old values. **Nothing else in 3a is a break**, and
specifically no entry was added for tier 2 (below), which measured throw-before/throw-after.

**Tier 2 — computed bare leaves — was built, measured and REVERTED, and its real prerequisite is recorded so it
is not re-attempted blind.** Widening the bare arm to size / filtered-size / arithmetic leaves under a reserved
`_v` alias won 6–7 further `NativeOnly` cases, but its cost was **not** confined to the explicit `DriverLinq`
escape hatch as designed: a bare `.Count` over a missing or explicitly-null array aborts with
`MongoCommandException` under the **default `Native` mode** whenever the native factory declines late, because
the un-stripped fallback is the driver's push-down and **the driver renders a bare `$size` where native renders
`$size` over `$ifNull`**. **The prerequisite for tier 2's return is therefore that the late-fallback path can
emit `$ifNull` itself rather than inheriting the driver's bare `$size`** — not a wider node-kind gate. Two
findings from that task survive the revert: the `_v` collision is **measured unreachable**, and the
tier-conditional fallback strip is proven in both directions (forcing it on breaks only tier 2, forcing it off
breaks only tier 1).

### Stream 1, tranche 1 — translator breadth (the merge plan's stream 1), 2026-08-08

*This is the first tranche of §9.8's merge-plan **step 2**. All SHAs in this table are post-rebase and **are** on
the current branch. The umbrella JIRA key for the tranche is **EF-398**.*

| Slice | Scope | Commit | Status |
|---|---|---|---|
| 0 | **Split `MongoExpressionTranslator` into three `partial` files** (**EF-398**) — a pure file move ahead of the feature slices, so the per-feature diffs are readable | `16bf9a20` | ✅ Done. **0 cases won, by design.** `Native` 4593/0/17 and `NativeOnly` 2427/2166/17 both unmoved. Proven a pure move by a **sorted-line diff showing zero removed and zero modified lines**, not by "the tests still pass". No `BREAKING-CHANGES.md` entry (the type is `internal`) |
| A2 | **A top-level `EF.Property<T>(param, "Name")` leaf resolves as a field**, in predicate, sort-key *and* projection position (**EF-399**) | `1d164597` | ✅ Done. **34 `NativeOnly` wins MEASURED, 0 regressions**, against a **CITED** estimate of 44. `Native` 4593/0/17 unmoved; `NativeOnly` 2427/2166/17 → **2461/2132/17**. **Six** specification `AssertMql` baselines re-based — four where the `$project` alias moved from the driver's `_v` to the element name, two where the predicate dialect moved from `$expr` to the query dialect — all six independently verified as correct and expected. **No `BREAKING-CHANGES.md` entry**, confirmed by **executing** a probe against the published `v10.0.2` / `v9.1.2` / `v8.4.2` packages rather than inferring it from the branch |
| A5 | **`Nullable<T>.Value` peels to the underlying field** in the same three positions, and **`Nullable<T>.HasValue` becomes the `!= null` node** in **predicate position only** (**EF-400**) — "the same three positions" for `HasValue` was wrong: the `HasValue` arm lives in `TranslateNode`, the predicate entry point, so a `HasValue` PROJECTION leaf deliberately declines, and that decline is pinned by `NativeNullableMemberTests.HasValue_as_a_projection_leaf_still_declines_and_is_unchanged_by_this_slice` | `4adafc2c` | ✅ Done. **0 `NativeOnly` wins MEASURED, 0 regressions**, against a **CITED** estimate of 36. Both axes byte-identical across the slice: `Native` 4593/0/17, `NativeOnly` 2461/2132/17. The zero is a property of the *sizing metric*, not of the fix — see finding (1) below. **One defect shipped and was closed by the tranche's final fix wave** (see the row below): a **value-converted** `.Value` PROJECTION leaf read the RAW stored value, silently, under the default `Native` mode — the peel makes the emit side address the field, but the read side cannot see through a `.Value` receiver, so the property (and its converter) came back null. **No `BREAKING-CHANGES.md` entry**, confirmed by executing against the same three published packages |
| — | *(not a slice — the **slice-B spike**, documentation only)* The computed-sort-key capability: emission site, shaper survival, standalone yield, and the per-group conversion question (**EF-401**) | `2ad8524a` | ✅ Done. Findings doc: `docs/superpowers/specs/2026-08-08-computed-sort-key-spike.md`. No `src/` change, no spec movement, no `BREAKING-CHANGES.md` entry |
| — | *(not a slice — the tranche's **final fix wave**)* Declines a value-converted `Nullable<T>.Value` projection leaf so it can no longer read the raw stored value; plus the documentation corrections propagated into this section, the plan, the two upstream spikes and `Query/AGENTS.md` | *the tranche's final commit — `EF-400: decline a value-converted nullable .Value projection leaf`* | ✅ Done. MEASURED with `ValueConverter<int,int>(v => v*2, v => v/2)`, stored `14`, correct CLR `7`: both `new { V = x.Converted.Value }` and the bare spelling returned **14** under `Native` **and** `NativeOnly` before the fix, while the plain-member control and a whole-entity read both returned 7. The fix is a **DECLINE** to driver-LINQ (which throws for that mapping, exactly as the released packages do), keyed on the existing `NativeGroupByBinder.HasDefaultKeySerialization`; the root fix — teaching the read side to peel `.Value` so emit and read agree by construction — is **EF-402**, and the guard is to be removed when it lands. Mutation-verified: deleting the new disjunct turns exactly **1 of 91** cases red with `returned 14,4`. Both axes unmoved: `Native` 4593/0/17, `NativeOnly` 2461/2132/17. No `BREAKING-CHANGES.md` entry |
| B | **A COMPUTED sort key goes native**, via `$set` → `$sort` → `$unset` over a synthetic `__sortN` field (**EF-401**) — arithmetic, a bare constant, a captured parameter or an owned-collection `Count` in `OrderBy`/`OrderByDescending`/`ThenBy`/`ThenByDescending`. A bare constant/parameter body is `$literal`-wrapped (a `$`-prefixed string would otherwise read as a FIELD PATH — silent wrong ORDER under the default `Native` mode) | *the slice's squashed commit — `EF-401: sort by a computed key via $set/$sort/$unset (stream 1, slice B)`* | ✅ Done. **12 `NativeOnly` wins MEASURED (6 tests × 2 async), 0 regressions** — `Failed→Passed` = those 12, `Passed→Failed` = **empty**. `NativeOnly` **2461/2132/17 → 2473/2120/17**; `Native` returns to **4593/0/17** after re-baselining. **12 `AssertMql` re-bases**, the SAME 6 overrides on **all three** EF majors (their baselines are single un-`#if`'d literals, and the 12 *Actual* strings are byte-identical across EF8/EF9/EF10, so ONE `EF_TEST_REWRITE_BASELINES` pass re-bases all three). **THE 12 ARE A RE-ATTRIBUTION INSIDE THE ALREADY-COUNTED 474, NOT AN ADDITION — do NOT add them to the ≈508 checkpoint, which does not move.** Checkable: 10 of the 12 are the `$literal`-wrapped bare constant/parameter cases, i.e. exactly group **A3**, whose §5.1 row is 40 sole-cause **of which 10 need slice B** — so **A3's marginal yield once slice B has shipped is `40 − 10` = 30, not 40**; the other 2 are `OrderBy_arithmetic` × async. **ACCEPTANCE-GATE TRAP:** the `NativeOnly` pass count was **byte-identical before and after the slice landed** (2461/2132/17 both sides) because the slice re-bases the baselines of exactly the tests it converts — only 12 failure MESSAGES changed. A count-only gate reads this slice as delivering nothing; compare `(testName → outcome, message)` as SETS. **No `BREAKING-CHANGES.md` entry**, confirmed against the release TAGS (`MongoQueryMode` does not exist at `v10.0.2`/`v9.1.2`/`v8.4.2`, so a released package ran all six through driver-LINQ; the change is fallback → native with unchanged results **and unchanged row order**, plus changed emitted MQL) |
| A1 | **A cast / `Convert` operand goes native** — the four `$toX` targets `int`/`long`/`double`/`decimal` — in comparison operands, `$expr` field-to-field and arithmetic operands, and as a projection leaf, with a widening cast ABSORBED under a conditional constant rule, an identity-like cast (enum ↔ underlying, `char → int`, convert to `object`) admitted, and a narrowing cast against a constant falling through to `$expr` (**EF-403**) | `39a0ffdc` + its whole-branch-review **fix wave** as a second commit on top (`EF-403: type-bracket a relational cast comparison, and record two measured divergences`) | ✅ Done. **28 `NativeOnly` wins MEASURED AS SHIPPED, 0 regressions** (the slice measured **30** before its fix wave; the fix wave gives 2 back, deliberately — see below) — measured by MESSAGE TRANSITION over all 4610 results against a freshly re-measured baseline at the slice base `fd6bd8ba`: `Failed→Passed` = 28, `Passed→Failed` = **empty**, `Failed→Failed`-with-a-different-message = **0**, 0 added/removed. `NativeOnly` **2473/2120/17 → 2501/2092/17**; default `Native` **4593/0/17** at base and at HEAD, compared as a SET. **ZERO `AssertMql` literals re-based** in the slice as shipped — the one the slice moved (`NorthwindWhereQueryMongoTest.Decimal_cast_to_double_works`, both async cases) is moved BACK by the fix wave. **NOT the CITED 72 total / 56 sole-cause — 28 is ~50%, and the gap is CHARACTERISED rather than itemised** (the A1 spike's §5.5 predicted 28, or 30 with a two-case re-baseline, from a direct prototype A/B): the projection column (CITED 14) and the sort column (CITED 8) each deliver **zero** specification cases, so both are correctness/breadth work, and the predicate column's remainder sits behind blockers outside A1. **This slice also FIXED A LIVE SILENT-WRONG-ORDER DEFECT** — a cast-bearing sort key sorted by the RAW stored value, with `(uint)x.I`/`(short)x.I` genuine order REVERSALS and no exception under the default `Native` mode — and **found and closed a within-slice silent-wrong-data regression of its own** (the `$expr` fall-through read raw stored values for a value-converted property; default `Native` went from throwing to returning one wrong row, measured base-vs-HEAD). **A `BREAKING-CHANGES.md` ENTRY WAS ADDED BY THE FIX WAVE** ("Query results can differ for a numeric cast in a `Where` clause", under 8.5.0 / 9.2.0 / 10.1.0). Every touched type is `internal` and `MongoQueryMode` does not exist at `v10.0.2`/`v9.1.2`/`v8.4.2`, but the rubric's carve-out for the native default is a CONDITIONAL — results unchanged, unsupported shapes fall back, `DriverLinq` restores the previous path — and for this shape the first is false and the second inapplicable. The entry covers the two remaining observable deltas: a narrowing cast vs. a constant now returns the C#-correct rows, and an out-of-range value now raises a server error that ABORTS THE WHOLE QUERY (MEASURED; the released packages returned rows). **One divergence is recorded rather than carved out**: for a narrowing cast vs a constant native returns the CLR answer where the published packages return the driver's (`[a,b,c]` vs `[a,b,c,e]`, verified by EXECUTING a probe against all three published packages) — an owner ruling, restorable with `UseQueryMode(DriverLinq)`, and the one place the native default's "results are unchanged" justification does not hold. **FIX WAVE (one commit on top of `39a0ffdc`): it closed a LIVE silent-wrong-data defect** — the `$expr` fall-through un-type-bracketed a relational comparison, so `<`/`<=` over a NULLABLE property returned stored-`null` and MISSING-element rows that the type-bracketed query dialect (and the released packages) exclude; closed by declining the fall-through for the four relational operators over a nullable property, which is what costs the 2 cases above. It also corrected two claims the slice shipped as MEASURED (the overflow boundary, now measured and PER-QUERY in blast radius; and a `TryRenderSizeComparison` reachability claim this slice itself falsified, together with the incidental widening behind it), pinned `$toInt`'s truncate-toward-zero rounding, and filed **EF-404** for the pre-existing, shipped, cast-free field-to-field `$expr` path. See `Query/AGENTS.md` item 16 |

**Re-summed from the table above rather than restated from a report.** Wins, one term per row:
`0 + 34 + 0 + 0 + 0 + 12 + 28` = **74** (the two non-slice rows — the slice-B spike and the final fix wave —
win nothing by construction; the fix wave's own change is a DECLINE, and it moved neither axis). The
`NativeOnly` triple moves **2427/2166/17 → 2501/2092/17**, i.e. **+74 passed / −74 failed / skipped
unchanged**, which agrees with the win column exactly — unlike step 3a, this tranche has no `Failed→Passed`
transition that is not a feature win. (A2 also produced **2** `Failed→Failed` transitions with a *different*
message — `NorthwindQueryFiltersQueryMongoTest.Find`, both `async` cases, advanced to their next blocker, a
parameterized regex term. Those are progress, not wins, and they move neither the triple nor the win count.)
The default `Native` axis reads **4593 / 0 / 17** in every row, so the tranche's `Native` delta is **0**.
`BREAKING-CHANGES.md` entries added: **0 of 6 rows**. Against the CITED estimates the three SIZED feature
slices realized **`34 + 0 + 28` = 62 of `44 + 36 + 56` = 136**; slice B is deliberately **excluded from that
ratio**, because its 12 are a re-attribution of cases the ≈508 checkpoint has already counted elsewhere (see
the slice-B row above and finding (2) below) — adding them to a realized-vs-estimate ratio would double-count
them. **A1's 28 carries the same "not the CITED figure" caveat as A2's 34 and A5's 0, and for a NEW reason
worth carrying into the next slice's planning: sole-cause has now under-delivered three times (A2 34/44,
A5 0/36, A1 28/56), and A1 adds a second failure mode to finding (1)'s — the decomposition spike's classifier
can point at the wrong DECLINE SITE entirely, not merely stop at the minimal failing subtree.** A1's yield
lives at `TranslateComparison`/`HasNumericConvert`, which no A1 write-up named; a plan written against the site
the documents pointed at (`TranslateOperand`'s `Convert` branch) would have delivered **0 of 28**. **Size a
slice by a prototype A/B, not by the table** — A1's own spike did exactly that and predicted 28 (30 with a
two-case re-baseline) against the CITED 56, which is what the slice then measured.

**Three findings from this tranche, recorded here because each one changes how a later slice should be planned
or sized.**

**(1) The stream-1 spike's "sole-cause" metric is structurally unreliable for any feature group whose feature
is an INNER node of the expression tree — and A5 is the proof.** MEASURED. That spike's classifier is a
minimal-failing-subtree search: it descends to the first failing child, stops, and records **one** decline site
and **one** feature — which is exactly what makes the figure "sole-cause". It structurally cannot see that the
*enclosing* construct is also unsupported. A5 sized **38 total / 36 sole-cause** and converted **zero**, because
Northwind's `Nullable.Value` occurrences are abundant but universally *enclosed* (`o.OrderDate.Value.Year` and
kin). The arithmetic that clinches it: `NorthwindSelectQueryTestBase.cs:1019–1082` holds **ten** bare
DateTime-component projection siblings, and `10 × 2` (the `[Theory]` async/sync pair) = **20** — exactly the
spike's own `` `Nullable.Value` | `NorthwindSelect` 20 `` concentration figure; the eleventh sibling,
`Select_datetime_DayOfWeek_component` (`:1067`), carries an `(int)` cast and is counted in the `casts` group
instead, which is why the figure is 20 and not 22. **The consequence, and it is the one that outlives the
slice: fixing an inner-node group RELABELS its cases into another group rather than closing them.** The spike's
disjoint partition is therefore **not stable under fixes** and its per-group figures are **not additive**, so
the merge plan's **≈508** post-stream-1 checkpoint is inflated by every inner-node group's sole-cause count.
Realized against estimate so far: **A2 34/44, A5 0/36 — `34 + 0` = 34 of `44 + 36` = 80.** Treat any inner-node
group's figure as an upper bound on shapes PRESENT, never as a count of cases that will turn green. Full detail
is in the slice-A5 as-built note in `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md`; the consequence for the
checkpoint is carried into §8 and §9.8 step 3.

**(2) Slice B is NOT "0 delivered alone" — it delivers 12 — but that is a RE-ATTRIBUTION, not an addition.**
MEASURED by the slice-B spike (§4), by *message transition* across a four-run A/B: on the `NativeOnly` axis the
counts are byte-identical before and after, and yet exactly **12** cases move from
`NativeTranslationNotSupportedException` to an `AssertMql` baseline mismatch — they go native with correct data,
and only a stale committed baseline keeps them red. The 12 are **10** A3 bare-constant/parameter cases plus
**2** arithmetic cases (`10 + 2` = 12), and **all 12 are already counted inside the spike's 474 sole-cause**.
So **≈508 and ≤3257 do not move, and the 12 must not be added to them.** What *does* change is a sequencing
figure: if slice B ships first, **A3's marginal yield is `40 − 10` = 30, not 40** — corrected in §9.8 step 2.
Three further slice-B facts worth carrying: a synthetic `$set` sort field **survives all five shaper shapes**
(MEASURED, per shaper); the `$unset` is **not required** for materialization (MEASURED both ways — it should
still ship, for set-op hygiene); and emitting a `$set` in front of a `$sort` **disqualifies index-backed sorting
even for a plain field key** (MEASURED with `queryPlanner`: `{$sort: {A: 1}}` alone is `IXSCAN A_1`, the same
sort preceded by an unrelated `$set` is `COLLSCAN`), so the design must not emit the `$set` when no key is
computed.

**(3) A merge-plan obligation nobody had named: at least 36 of the 92 sort-position cases need an
AGGREGATION-dialect renderer arm ON TOP OF their predicate work.** READ (`MongoAggregationExpressionRenderer.
CanRender` at `4adafc2c`) plus MEASURED declines: the aggregation dialect admits field/element refs,
constants/parameters, binary operators over 13 listed operators and the two size nodes — and **not**
`MongoInExpression`, `MongoRegexExpression`, `MongoElemMatchExpression` or `MongoUnaryExpression`. A `$set` body
is an aggregation expression, so a node kind that lives only in the query dialect can serve a predicate but can
**never** serve a computed sort key. The stream-1 spike's §7 already imposes this obligation on slices that
introduce a **new node kind**, which covers **A9** (10) and **A12** (22) — `10 + 22` = 32 already covered. It
does **not** cover **A6** (18, `Contains` → `MongoInExpression`) or **A13** (18, `Not` → `MongoUnaryExpression`),
because both node kinds **already exist** and so neither slice introduces one; `18 + 18` = **36 genuinely
unnamed**, and a reader following the existing note would ship both with their sort columns silently dead. The
total needing an aggregation arm is at least `36 + 32` = **68 of the 92** — a floor, not a total.

Refactor interludes (not user-facing): EF-330 (extract `MongoSelectDefinition`), EF-332 (separate the
native-translation layer from QMTEV), EF-334 (centralize the is-native gate into `ClassifyNativeDisposition`).

**Delivery mechanics.** Native sub-projects ship as stacked branches on `NativeQueryOngoing`, one squashed
commit each: SP1 → SP2 → SP3 → SP4 → SP5 → SP6 (GroupBy / set-ops / Distinct / OfType) → EF-347 SelectMany
slices → `1dd7862` → SP7 Phase 1 (`e38587f`) → owned-data slices 1–6 → `1b4c1d6` → slice 7 (`cfe873e`) →
EF-358 fix (`7c199e4`) → slice 8 (`33fdc58`) → slice 9 (`229294f`) → **the joins work stream: EF-366
(`0162b737`) → EF-367 (`5dfb1653`) → EF-370 (`7af4190b`) → EF-368 (`34a02067`) → EF-372 (`6a7a5f3c`) → EF-373
(`9dd6fc15`) → EF-379 (`9065acfc`)** → *(cutover steps 2 and 3a)* → **stream 1 tranche 1: EF-398
(`16bf9a20`) → EF-399 (`1d164597`) → EF-400 (`4adafc2c`) → EF-401 (`2ad8524a`) → **slice A1: EF-403
(`39a0ffdc`), plus its whole-branch-review fix wave as a SECOND commit directly on top — the current tip**.
*(This chain read "EF-401 (`2ad8524a`, the current tip)" until slice A1 landed, and "EF-379 (`9065acfc`, the
current tip)" before tranche 1.)* *(The pre-joins portion of this chain is
pre-rebase hashes — see the header warning; the joins portion and everything after it is post-rebase and is on
the branch.)* **The "as of 2026-07-31 there is no unsquashed work in flight" statement holds with ONE
documented exception at the tip** — every slice through `39a0ffdc` is one squashed
commit on the branch, and slice A1 additionally carries its fix-wave commit unsquashed on top (deliberately:
the slice was already squashed and fast-forwarded, so the fix wave is added rather than amended). ~~Nothing is merged to
`main` yet — the whole native stack lands at parity/cutover.~~ **SUPERSEDED 2026-08-07 — that was the
withdrawn cutover plan.** Nothing is merged to `main` yet, but per §8's plan of record the stack now lands at
**merge** (~80% coverage, driver path shipping alongside it), not at parity with driver-LINQ retired.

---

## 3. What's native today

- **Filtering / sorting / paging (SP1–SP2).** Single-collection whole-entity queries generate the
  `BsonDocument[]` pipeline directly. Predicate breadth: nullable equality / `== null` (IS NULL),
  collection `Contains` → `$in`/`$nin`, `string.StartsWith`/`EndsWith`/`Contains` → `$regularExpression`,
  and field-to-field / arithmetic comparisons → `$expr`. Predicate rendering prefers an index-usable query
  dialect; `$expr` is the last resort.
- **Projection (SP3 + EF-347 arithmetic slice).** Terminal anonymous-type / DTO projections over top-level
  member accesses → native `$project`. **Numeric-arithmetic computed leaves** (`+ - * / %`, e.g.
  `new { Total = o.Price * o.Qty }`) go native via `MongoExpressionTranslator.TryTranslateValue`, in a plain
  terminal `Select`, after a whole-entity set-op, and as a set-op operand.
- **Scalar cardinality (SP4).** Entity reducers (`First`/`FirstOrDefault`/`Single`/`SingleOrDefault`) via
  synthesized `$limit`; scalar aggregates (`Count`/`LongCount`/`Any`/`All`/`Sum`/`Min`/`Max`/`Average`) via
  `$count`/`$group` with an explicit empty-input contract (`MongoEmptyAggregateBehavior`).
- **Collection Include (SP5).** Single-level collection `Include` + projected collection `.Count` (`$size`)
  via a flat `$lookup` (no `$unwind`), materialized through the DOM shaper.
- **GroupBy (SP6 / EF-344).** `GroupBy(key).Select(aggregate)` → `$group` + flattening `$project`.
  Scalar/composite/DTO keys; `Count`/`LongCount`/`Sum`/`Average`/`Min`/`Max` accumulators over plain field-refs.
- **Set operations, Distinct, OfType (SP6 / EF-347).** Whole-entity terminal `Union`/`Concat` and
  `Intersect`/`Except` (source-tagging pipeline); projected `Distinct` (degenerate `$group`);
  `OfType<TDerived>()` over TPH (discriminator `$eq`/`$in` conjunct).
- **SelectMany (SP6 / EF-347 — the just-finished tail):**
  - Owned-collection `SelectMany` projecting element members (inner-`Select`, explicit, query-syntax, bare-element)
  - Owned filtered-inner and owned correlated-beyond-outer
  - Reference (FK-correlated) `SelectMany`: projected + bare-whole-entity, inner-element filter, filter
    correlated beyond the FK
  - Nested (exactly two-level) reference `SelectMany` → two chained `$lookup` + `$unwind`
  - Single-scope arithmetic computed leaf inside a SelectMany trailing projection (the final tail item, tip `1dd7862`)
- **Materialization: one-pass streaming (SP7 Phase 1, `e38587f`).** The native streaming materializer is now
  *one-pass*: a per-execution `MongoEntityMaterializerSerializer<TEntity>` is the `Aggregate<TEntity>` output
  serializer, so deserialize **is** materialize — no intermediate `RawBsonDocument`, no second reader/context.
  Allocation vs pre-SP7 native: whole-entity no-track 19.1→5.4 MB (−72%, ~1.73× the raw-driver floor), `Where`
  9.6→2.8 MB, tracked 25.2→11.5 MB; wall-clock ≈ the driver floor. Materialization-only — **zero** query-shape,
  result or eligibility change.
- **Owned (embedded) whole-entity queries (owned-data slices 1–2).** A whole-entity query over an entity with
  owned single-reference navigations (`OwnsOne`, nested) *or* owned collections (`OwnsMany`, incl. mixed and
  shared-CLR-type) now goes native — previously *always* fell back. Root cause was the **gate**, not
  materialization: EF auto-includes owned navs as `Select(x => IncludeExpression(x, nav))`, which matched no
  pass-through predicate. Flat / mixed / shared / empty owned collections **stream** via SP7 Phase 1; a
  collection whose element carries a further navigation, and collection-of-collection, route to native DOM.
- **Owned single-reference sub-property dotted paths (owned-data slice 3).** A predicate, sort key, or
  projection leaf reaching *through* owned single-reference navs to a scalar leaf — `Where(e => e.Home.City == x)`,
  `OrderBy(e => e.Home.Geo.Country)`, `Select(e => new { e.Home.City })` — resolves to a dotted document path.
  One shared gate (`TryResolveMember`) lights up all three surfaces at once.
- **Owned-collection quantifier predicates — `Any` *and* `All` (owned-data slices 4–5).** `Any()`/`Any(pred)`
  and `All(pred)` over an owned collection navigation, negated forms, nesting in either order, and collections
  reached through owned single-reference hops. `Any(pred)` → `$elemMatch`; bare `Any()` → an array-index
  `{"path.0": {$exists}}` test (correct for empty/missing/null arrays alike, unlike `{$ne: []}`); `All(pred)` →
  `{path: {$not: {$elemMatch: ¬pred}}}`, which is also correct for empty/missing/null arrays because LINQ's
  `All` is vacuously true there. An owned `SelectMany` whose inner filter is itself an owned-sub-collection
  `Any` — previously a hard fail in every mode with no driver-LINQ oracle — also goes native.
  **The `All` half rests on a new exact-complement negator (`MongoExpressionNegator`), which also closed
  EF-335** (top-level `All` with a comparison predicate). Its central rule, verified against a live server:
  `$eq`/`$ne` may be **inverted** because they partition every BSON value, but the four relational operators
  must be **`$not`-wrapped**, because `{$gt: 5}` and `{$lte: 5}` do *not* partition — neither matches a field
  that is missing, null, or of another BSON type, so inverting them would report `All == true` where LINQ says
  false. Index note: root-scope `{f: {$not: {$gt: v}}}` is IXSCAN, but the owned-collection `All` form is a
  COLLSCAN — a deliberate correctness-over-index trade (the index-friendly alternative returns wrong answers),
  and the already-shipped `!Any(...)` form scans equally. See
  `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md` for both quantifier notes.
- **Owned-collection `.Count` in a predicate (owned-data slice 6).** `Where(b => b.Posts.Count > 2)` and all
  six comparison operators, both operand orders, `.Count`/`.Count()`/`.LongCount()`, a constant *or*
  parameterized threshold, a count reached through owned single-reference hops, and a count nested inside a
  quantifier's element predicate now go native — previously always fell back. A constant threshold renders as
  the query-dialect array-index existence test (`{"path.k": {$exists: true|false}}`) — the same family bare
  `Any()` already used, now unified with it as `Count >= 1`; a parameterized or degenerate threshold renders
  `$expr` over a null-safe `$size` (`$ifNull` maps a missing/`null` array to `[]`, since bare `$size` against
  either is a hard server error). Negation *inverts* the operator (an exact complement, since `$exists`
  partitions the value space) rather than `$not`-wrapping it, the documented exception to the `All` slice's
  `$not`-wrap rule. Index note, as measured (not assumed): all four relational array-index forms come back
  **COLLSCAN** with both a collection- and leaf-level multikey index present — the form is still required
  regardless, since it is the only one legal inside `$elemMatch` and the only one correct for missing/null
  arrays. See `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md`'s dedicated `.Count` note for the full
  two-tier mechanism, the negator exception, and the settled finding that EF Core rewrites `Count() > 0` into
  `Any()` upstream (so that one spelling is unreachable via ordinary LINQ for the constant-tier `GreaterThan`
  arm at `n = 0`).
- **Owned-collection `.Count` as a PROJECTION leaf (owned-data slice 7).** An owned-collection count appearing
  as a *leaf* inside a terminal anonymous-type or DTO projection — `Select(b => new { b.Title, N = b.Posts.Count })`,
  the named-DTO spelling, `.LongCount()`, a count reached through owned single-reference hops
  (`b.Home.Notes.Count`), and several count leaves side by side — now goes native as
  `{$size: {$ifNull: ["$path", []]}}` inside `$project`. It reuses the `MongoSizeExpression(nullSafe: true)`
  node and the renderer arm slice 6 added: no new expression node, no new renderer arm. Before this slice the
  shape threw `ArgumentException` in **all three** query modes (measured on unmodified `src/`; the earlier
  documentation had implied only the *bare-scalar* form hard-failed — see §4). The binder gate accepts the leaf
  only when the translated node **is** a `MongoSizeExpression`, not merely when translation succeeded. Keeping
  that gate narrow is still the right call, but the MECHANISM this file gave for it was wrong and is corrected
  here: it used to say a bare constant/parameter leaf "renders as a bare value, which `$project` reads as an
  inclusion flag (`{X: 1}`) rather than a literal" — implying silently wrong data. As MEASURED (gate widened to
  plain `TryTranslateValue` success, then the tests run): `X = 5` and a captured-parameter leaf return
  **correct** values, folded client-side, leaving only a junk `X: 5` in the emitted `$project`; `X = 0` and
  `X = false` instead **abort the command** — `MongoCommandException: Invalid $project :: caused by :: Cannot do
  exclusion on field X in inclusion projection` — because `$project` reads `0`/`false` as an EXCLUSION flag. So
  what the narrow node-kind gate keeps out is a hard abort on a falsy constant, not a silent misread; see
  `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md`'s count-projection note for the full measurement and the
  test that now pins it. The bare-scalar form
  (`Select(b => b.Posts.Count)`) is deliberately *not* native — a bare-scalar projection body never populates
  `Projection`, which is the SP3-wide bare-scalar boundary, not a count-specific one; see §4 for what it does
  instead. **One divergence worth knowing, measured and pinned:** for this shape `DriverLinq` does *not* return
  equivalent results to `Native` on ragged data. Present arrays agree; a **missing or explicitly-null** array
  makes `DriverLinq` raise `MongoCommandException` ("the argument to `$size` must be an array"), because the
  driver's LINQ provider renders a bare server-side `$size` with no `$ifNull`, while native's `$ifNull` form
  answers 0. Before this slice both modes threw, so nothing regressed — but `UseQueryMode(DriverLinq)` is not an
  equivalent-results escape hatch here, and driver-LINQ is also where a projection mixing a count leaf with a
  binder-declined leaf lands under the default `Native` mode.
- **Owned-collection ARRAY leaf as a PROJECTION leaf (owned-data slice 8, `33fdc58`).** An owned
  entity-COLLECTION navigation appearing as a *leaf* inside a terminal anonymous-type or DTO projection —
  `Select(b => new { b.Title, b.Posts })` — now emits a server-side `$project`
  (`{ $project: { Title: "$Title", Posts: "$Posts", _id: "$_id" } }`) and reads the array back from the
  projection **alias**, instead of falling back to `aggregate([])` and folding the projection client-side over
  whole documents. **Results are unchanged** — a bandwidth and allocation win, not a correctness fix. Two new
  `internal` types (`ArrayAliasProjectionExpression` and a shared `IArrayProjectionExpression`, the latter also
  implemented by the pre-existing `ObjectArrayProjectionExpression`); **no new array-source code was needed** on
  the read-back side, contrary to what the design predicted. Admissibility turns on two rules, both
  found by MEASUREMENT rather than reasoned ahead — though not by the same kind of bug: rule (1) via a measured
  **silent** wrong-data bug, rule (2) via a measured **throw** whose silent variant is mechanism-derived (not
  executed) and pinned by the colliding-alias test: **alias agreement** (the leaf's alias must equal the
  navigation target's containing element name, and the navigation must be declared on the query root) and
  **sibling readability** (when an array leaf is present every non-array sibling must also be readable off a
  whole, un-projected document). Both exist because the shaper is alias-addressed from *translation* time while
  native-vs-fallback is decided *later*, so a fallback hands that shaper a whole document. Declined and still
  fallback: the bare spelling (§5), an `OwnsOne` hop (**EF-362**), a renamed alias or element, a
  non-whole-document-readable sibling (which, as the final review named explicitly, ALWAYS includes a
  primary-key sibling — a root PK's element name is always `_id` while its alias is the CLR name, so no
  ORDINARY naming choice can satisfy the rule; a `_id = b.Id` alias spelling *would* satisfy it — read, not
  executed — and is an untested admitted case for a future slice), a reference (non-owned) collection, an element type carrying its own
  **eager-loaded** navigation (**EF-360** — narrowed at the final review from "any navigation", which also
  over-declined an ordinary `WithOwner` lazy inverse back-reference), and a projected set-op operand. Zero spec
  delta on both axes; three-version sweep 0 failures. *This entry is a summary only* — the full as-built mechanism, every guard with the bug that
  motivated it, the measured set-op flips, and the coverage gaps live in
  `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md`'s array-valued-projections note.
- **Owned-collection FILTERED `.Count(pred)` (owned-data slice 9, `229294f`).** A *predicated* count over
  an owned collection is now native in two positions: in a **predicate**
  (`Where(b => b.Posts.Count(p => p.Rank > 0) > 2)`, all six comparison operators, either operand order,
  constant or parameterized threshold, through owned single-reference hops) and as a **projection leaf**
  (`Select(b => new { b.Title, N = b.Posts.Count(p => p.Rank > 0) })`, plus `LongCount`, the named-DTO
  spelling, sibling leaves, and — as an unplanned incidental widening of the pre-existing EF-347 arithmetic
  branch — an arithmetic wrapper, `new { X = b.Posts.Count(pred) * 2 }`). A **third** unplanned widening, found at
  the whole-branch review, also went native: a filtered count inside an **owned `SelectMany`'s inner filter**
  (`SelectMany(b => b.Posts.Where(p => p.Comments.Count(pred) > 1), (b, p) => new { p.Heading })`), which emits a
  top-level `$match` after the `$unwind` — measured to have hard-failed in all three modes at the branch base, so
  a hard-fail → native fix rather than a routing flip. Both render the same
  `{$size: {$filter: {input: {$ifNull: ["$path", []]}, as: "e", cond: …}}}`, always through the
  `$expr`/aggregation tier: unlike the *unfiltered* `.Count`, a filtered count has **no** query-dialect
  array-index (`$exists`) form, and that absence is enforced structurally — a new sealed sibling node,
  `MongoFilteredSizeExpression`, rather than a flag on `MongoSizeExpression`, so the Tier-1 renderer, the
  query-dialect classifier and the negator all fail **closed** by construction. (A flag would have let Tier 1
  answer the *unfiltered* count's question — wrong rows, silently, under default `Native`.) The predicate
  spelling previously fell back with correct results; the projection spelling previously **crashed** in every
  mode (**EF-359**, now closed). Two things worth carrying forward: `$ifNull` is mandatory rather than
  defensive (without it a missing or explicitly-null array is a hard server error that aborts the aggregate),
  and a relational or `== null` element predicate over a **nullable** element field can return a different
  *number* from in-memory LINQ on ragged data — native and `DriverLinq` agree with each other, both differ from
  LINQ, because one BSON total order (`missing < null < numbers`) distinguishes two values the CLR collapses
  into a single `null`. That divergence is an **accepted, documented owner ruling**, not a defect. See
  `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md`'s "EF-359 AS BUILT" note for the full as-built account,
  every decline, and the residuals that still hard-fail.
- **Atlas Vector Search (cutover step 2, 2026-08-06).** `VectorSearch(...)` emits a native `$vectorSearch`
  first stage from a dedicated `MongoSelectDefinition.VectorSearch` slot the lowerer writes *ahead of*
  `AppendSelectOpStages`, so first-ness is structural rather than incidental; the stage BODY is built per
  execution by the driver's own `PipelineStageDefinitionBuilder.VectorSearch` (it varies with runtime options,
  so it must be constructed, not substituted), with the fixed `$addFields{__score}` companion. Covers a bare
  vector search, a `preFilter` the native predicate translator can express, `exact`/`numCandidates`, a `Where`
  composed after it, and a `__score` projection leaf. `MONGODB_EF_NATIVE_ONLY=1` `VectorSearch` failures
  **112 → 20**, and **→ 16** after step 3a. No `AssertMql` baseline moved. See §4 for the residual.
- **Bare projections (cutover step 3a, 2026-08-06).** A terminal `Select` whose body IS the leaf —
  `Select(p => p.Name)`, `Select(o => o.OrderID)`, `Select(b => b.Tags)`, `Select(b => b.Posts)` — populates
  `Projection` and emits a native `$project` aliased by the leaf's own root-relative document path (which is
  what keeps one alias-addressed shaper correct against a projected and an un-projected document alike, on the
  late-fallback route). **74 `NativeOnly` cases won, zero regressions**; 78 `AssertMql` baselines re-based as
  the `$project` key moved from the driver's `_v` to the element name; the epic's first `BREAKING-CHANGES.md`
  entry for a native-routing flip. A bare projection followed by a cardinality operator or by
  `Skip`/`Take`/`Where`/`OrderBy` also goes native, as an incidental widening. See §4 for what still declines.

---

## 4. What still falls back to driver-LINQ

**By design (correct results under `Native`/`DriverLinq`; throws only under `NativeOnly`):**

- **Computed long tail** — string transforms (`ToUpper`/`Substring`), date-part extraction, `Math.*`,
  type-changing casts, integer-result `Divide` (Guard A: `$divide` is non-truncating).
- ~~Reference `Include`~~, nested/transitive `ThenInclude`, filtered `Include`, collection-of-collection
  Include (lookup/streaming machinery built but dormant). **Corrected 2026-08-07: single-level reference
  `Include` shipped native (EF-368, `34a02067`) and struck from this bullet.** What remains —
  `ThenInclude` breadth, filtered `Include`, collection-of-collection, and the general `Join`/`GroupJoin`/
  `LeftJoin` — is **deferred under the 2026-08-07 merge plan** and tracked as **EF-392** (373 cases; see §8
  and §9.8).
- **Non-native GroupBy shapes** — computed keys (`g.OrderDate.Year`), computed accumulator operands, bare
  `GroupBy(key)` terminating on `IGrouping`, user `resultSelector`, post-group slot operators
  (Where/OrderBy/Skip/Take as HAVING), correlated / cross-collection keys. **Deferred under the 2026-08-07
  merge plan; tracked as EF-393** (130 cases; see §8 and §9.8).
- Bare-scalar & whole-entity `Distinct`; non-whole-entity / non-terminal / mismatched set-ops.
- Contains / ElementAt / Last; computed aggregate selectors. (**`All` with a comparison predicate — EF-335 —
  is NO LONGER on this list: closed by the owned-data slice 5 negator.**)
- Guarded-out for correctness: value-converter / non-default `BsonRepresentation` operands (arithmetic,
  GroupBy keys, Distinct keys, OfType discriminators).
- **Owned-collection predicate/projection long tail (EF-322), as it stands after the `Any`, `All`,
  `.Count`-in-a-predicate *and* `.Count`-as-a-projection-leaf slices:** ~~an embedded-collection **array**
  projection (`Select(b => b.Posts)`)~~ — **struck: step 3a made the bare array projection NATIVE (2026-08-06).**
  This entry originally read `Select(b => b.Posts.Count)`; the **count** half moved out when slice 7 landed, and
  the **array** half has now moved out too. What remains on this bullet is: a
  non-query-dialect owned-collection element predicate (field-to-field / arithmetic — no query-dialect form to
  put inside `$elemMatch`, and for `All` no exact complement either), a **correlated** element predicate (one
  referencing the enclosing entity — declined by a dedicated guard, because `$elemMatch` cannot reference the
  enclosing document at all), and a two-scope (cross-scope, inside a `SelectMany`) owned quantifier. An
  owned-COLLECTION intermediate hop in a dotted sub-property path also still declines (slice 3 covers
  single-reference hops only).
- **Bare-scalar owned-collection count** (`Select(b => b.Posts.Count)`). This entry previously read
  "hard-fails in every mode"; slice 7 changed that and the wording is corrected here. It no longer fails
  translation. It is still not native — **but the REASON is corrected again by step 3a, and the distinction
  matters because the old reason no longer exists.** This read "(bare-scalar projection bodies never populate
  `Projection`)". Since step 3a a bare body *does* populate `Projection` for a path-addressable leaf; a COUNT is
  a computed leaf with no document path to use as an alias, so it is declined by
  `NativeProjectionBinder.TryDeriveDocumentPathAlias`. That is the reverted **tier 2** (§2) — so this shape moves
  when tier 2 returns, not when some further bare-projection work lands. It takes
  the fallback path — and there, as measured, the count is folded **client-side**: the emitted pipeline is
  `aggregate([])`, no `$project` and no `$size`, so the whole document including the entire array is fetched
  and counted in process. Results are **correct for every array state** — a missing or explicitly-null stored
  array used to throw `ArgumentNullException` at materialization; the EF-358 fix (2026-07-29, see the standalone
  fact below) closed that residual, so it now returns `0` like every other path. `NativeOnly` declines cleanly.

**Hard-fails in every mode (no driver-LINQ oracle):** cross-collection SelectMany forms outside the native
slice, three-level+ nested SelectMany, whole-outer SelectMany, and any operator composed *after* a native
SelectMany (shaper-rebuild limitation). Also — measured by slice 7, pre-existing — an **interposed operator**
(`Distinct`/`Take`/`Reverse`/`DefaultIfEmpty`/`Concat`) between an owned-collection `Select` and a terminal
operator (duplicate-key `ArgumentException` from `_collectionShaperMapping.Add`; recorded as a comment on the
EF-322 epic).

**The whole-shape "filtered count in a projection" entry has MOVED OFF this list — corrected in place, not
annotated beside its stale text.** It used to read: a filtered count in a projection
(`Select(b => new { N = b.Posts.Count(p => p.Rank > 0) })`) throws `InvalidOperationException` identically under
`Native`, `DriverLinq` and `NativeOnly` (**EF-359**). That was accurate when slice 7 measured it and is no
longer: owned-data slice 9 (`229294f`) made that shape **native**, and closed EF-359 — see §3 and §6. The
precise disposition of what is left of the family, all of it NARROWER than the shape that moved:

- **Native:** the wrapped projection leaf (`new { N = b.Posts.Count(pred) })`, `LongCount`, the named-DTO
  spelling, sibling leaves, owned single-reference hops, an arithmetic wrapper (`new { X = ...Count(pred) * 2 }`),
  and the predicate spelling (`Where(b => b.Posts.Count(pred) > 2)`).
- **Falls back gracefully** (correct results under `Native`/`DriverLinq`, throws only under `NativeOnly`): in
  the *predicate* position — a correlated element predicate, a non-renderable element predicate, a
  primitive-element collection, a filtered count nested inside a quantifier, a negated filtered-count
  comparison; and a reference (non-owned) collection filtered count anywhere.
- **Still hard-fails in every mode** (`InvalidOperationException` at translation time, so `NativeOnly` gets the
  identical exception rather than a clean decline): in the *projection* position — a non-renderable element
  predicate (**EF-365**, where removing the `CanRender` guard would turn this into a working fallback — measured),
  a correlated element predicate (wrapped *and* bare), a primitive-element collection, the
  `Posts.Where(pred).Count()` spelling, a bare spelling whose predicate closes over a captured local, and
  arithmetic over a *bare* count (`Select(b => b.Posts.Count(pred) * 2)` — the count call is not the root; the
  *wrapped* arithmetic form above is native).
- **Not native, correct values:** the bare spelling `Select(b => b.Posts.Count(pred))` — folded client-side over
  `aggregate([])`. **This read "the SP3-wide bare-projection boundary, not a count-specific one"; step 3a lifted
  that boundary, so the current reason is the narrower one above** — a computed bare leaf has no document path
  to alias, i.e. the reverted tier 2.

**CLOSED (EF-358, 2026-07-29) — and the root cause is corrected here, not just the status.** This paragraph
used to describe the gap as a whole-entity-vs-projection split: whole-entity materialization normalizes a
missing/explicitly-null embedded array to an empty list, the projection path does not. **That framing was
measured false.** Pre-fix, *nothing* normalized on *any* path — `MongoProjectionBindingRemovingExpressionVisitor.
IncludeCollection` skips its fixup loop entirely when `relatedEntities` is `null`, so a materialized navigation
kept whatever the CLR class's own field initializer left behind (`null` for a plain `{ get; set; }`, `[]` for one
written `= []`). The earlier "whole-entity normalizes" reading was that initializer masking, not provider
behavior — the probe model that produced it happened to declare `Blog.Posts = []`. **Post-fix, behavior is
uniform and initializer-independent on every path** (whole-entity, `Include`, bare and wrapped projection),
every query mode, every cardinality: a missing or explicitly-null stored array now materializes as an empty
collection everywhere. Mechanism: the null-collapse conditional was deleted from
`BsonDocumentInjectingExpressionVisitor`'s `CollectionShaperExpression` case, and a `Coalesce` to an empty
`BsonArray` was added at the point of use in `MongoProjectionBindingRemovingExpressionVisitor`'s
`CollectionShaperExpression` case, feeding `PopulateCollection` through the navigation's own
`IClrCollectionAccessor` (so a non-`List` navigation is correct for free). The cross-visitor contract — the
injector's assignment must keep a `UnaryExpression` right-hand side, because `VisitBinary` hard-casts it — is
why the coalesce sits at the point of use rather than folded into the injector's own assignment; folding it in
throws `InvalidCastException` for every collection shaper in every mode. Consequences: the bare-scalar count
fallback now returns `0` instead of throwing `ArgumentNullException`, and **EF-357 is now FULLY closed** (see
§6). Primitive-collection *properties* are unaffected — that is a property-serializer path, not a
`CollectionShaperExpression`. See the rewritten note in `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md` for
the full mechanism, the parity-claim split (bare vs. wrapped count), and the `TypeAs` conflation.

**Atlas Vector Search (EF-322 VectorSearch slice, 2026-08-06).** `VectorSearch` used to be listed here as "not
native at all". **It no longer is.** A bare vector search, one carrying a `preFilter` the native predicate
translator can express, `exact`/`numCandidates` options, a `Where` composed after it, and a `__score`
projection leaf all emit natively — via a **deferred `Build`-time stage slot** on `MongoPipelineFactory` that
invokes the driver's own `PipelineStageDefinitionBuilder.VectorSearch` per execution (so the emitted body,
including the `numCandidates = limit * 10` derivation and field order, is byte-identical to the bridge's and
**no `AssertMql` baseline moved**), plus the fixed `$addFields{__score}` companion. The `MONGODB_EF_NATIVE_ONLY=1`
`VectorSearch` filter went **112 → 20** failures (92 fixed); default `Native` is unchanged at 114/0/4. The two
independent gates were collapsed into one fact read twice (`hasUnboundVectorSearch`), so the silent-wrong-data
state — both gates open, no stage emitted, right row count in insertion order — is unreachable by construction.
**Still not native — ~~16~~ 12 projection-bucket cases, updated 2026-08-06 by step 3a:** this read "the 16
remaining projection-bucket cases (4 bare-scalar, 12 mixed/entity-constructing — the SP3-wide bare-projection
boundary and the entity-leaf gap …)". **Step 3a lifted the bare-projection boundary, so the 4 bare-scalar cases
are now native and the projection-bucket residual is 12**, all of them mixed/entity-constructing (the
entity-leaf gap, which is step 3b's). The total `VectorSearch` residual is therefore **16, not 20**: those 12
plus the 4
`VectorSearch_with_complex_pre_filter` cases (**tracked as EF-382**; blocked on `MongoExpressionTranslator` not supporting
`arrayField.Contains(constant)` — a cross-cutting predicate-breadth gap deliberately not fixed in that slice).
Both decline gracefully: correct, score-ordered rows under default `Native`, throwing only under `NativeOnly`.
See the "Atlas Vector Search" note in `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md` for the mechanism, the
three `__score` guards, and the set-op dedup hazard.

**Bare projections (EF-322 step 3a, 2026-08-06).** A terminal `Select` whose body is the leaf itself used to be
listed here across half a dozen entries as "the SP3-wide bare-projection boundary". **It no longer is, for a
path-addressable leaf.** `Select(p => p.Name)`, `Select(o => o.OrderID)`, `Select(b => b.Tags)` and
`Select(b => b.Posts)` now populate `Projection` and emit a native `$project`, under an alias that **is** the
leaf's root-relative document path — which is what keeps one alias-addressed shaper correct against a projected
document and an un-projected one alike, on the late-fallback route. **74 `NativeOnly` specification cases won,
zero regressions**, and 78 `AssertMql` baselines re-based as the `$project` key moved from the driver's `_v` to
the element name. **What still falls back from a bare body:** a **computed** leaf (a count, a filtered count,
arithmetic — the reverted tier 2, §2); a **DOTTED** leaf (`Select(b => b.Home.City)`); a bare projected
**set-op operand** and a bare **`Distinct`**, both narrowed out by measured correctness guards rather than by
scope preference (a bare operand changes what `$$ROOT` is for a set op's dedup/source-tag comparison — 12 MQL
diffs and `Intersect_non_entity`/`Except_non_entity` flipping from throwing to *answering* without the guard;
a bare `Distinct` flips `Route` to `GroupBy` after the emit side has committed, reverting the alias to `null`
and handing the shaper whole `BsonDocument`s — 4 cases hard-failing from a passing base without it). This is
slice **3d** (18 cases); **deferred under the 2026-08-07 merge plan and tracked as EF-395** (see §8 and
§9.8). A bare
projection **followed by a cardinality operator** (`Select(b => b.Title).Count()`/`.First()`) or by slot
operators (`Skip`/`Take`/`Where`/`OrderBy`) goes native and is correct — an incidental widening that arrives
with 3a. See the two step-3a notes in `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md` for the alias scheme,
the four derivation sites plus the fifth reader, and why there is deliberately **no fail-loud invariant**.

**Not native at all:** non-TPH `OfType`.

---

## 5. Deferred items still on the epic

- **SP7 Phase 2 — streaming breadth (Phase 1 landed; Phase 2 not started).** Phase 1 delivered the one-pass
  materializer (see §3). Still deferred: **reducer/aggregate** streaming, **collection-Include array**
  streaming, and **reference-Include** streaming — the last blocked behind making reference `Include` native at
  all. Those shapes still route through the DOM shaper, not the streaming one. Also minor: delete the
  now-dead `RawBsonDocument` branch + `BsonRowReader`, which Phase 1 made unreachable.
- ~~**Parity cutover.** Once native reaches parity: retire the driver-LINQ fallback and delete the delegation
  code.~~ **SUPERSEDED 2026-08-07 — that was the withdrawn cutover plan.** §8 states the plan of record: the
  driver path **ships** in this release regardless of coverage, and retiring it is decoupled from reaching
  parity — it becomes a separate, later-phase project scheduled independently (§9 is now that project's
  inventory, not this epic's next step).
- **Minor SelectMany follow-ons (EF-347 leftovers):** cross-scope computed leaf (`o.Discount * i.Price`),
  the inner-`Select`-form computed-leaf binder.
- **Owned-collection follow-ons (EF-322), as they stand after slices 4–9 — in the order they are actually
  nearest.** "Embedded-collection projections" is no longer the nearest one; slice 7 took the count leaf
  natively, slice 8 took the wrapped ARRAY leaf, and **slice 9 took the filtered `Count(pred)`, which this list
  used to rank FIRST — that bullet is struck and the rest re-ranked in place** (it read: "Filtered `Count(pred)`
  in a projection (EF-359) … a bug fix of the same shape as EF-357 … the *graceful-fallback* assumption was
  written before the measurement and does not [stand]". Both the crash characterization and the `$size`-over-
  `$filter` rendering prediction held up; the shape is now native and EF-359 is closed — see §3, §4 and §6).
  What is nearest now:
  1. **Array projections — the WRAPPED spelling is DONE** (owned-data slice 8, `33fdc58`, 2026-07-30).
     `Select(b => new { b.Title, b.Posts })` and the DTO equivalent now emit a server-side `$project` and read
     the array back from the projection alias. **This bullet used to say array projections were "blocked on the
     DOM-shaper mechanism alone" — that was correct for the wrapped spelling (now done) and was NEVER correct
     for the bare spelling**, which carries a second, independent block; the two are separated below. It also
     named the `ObjectArrayProjectionExpression` hard cast as the blocker, which was wrong on its own terms: the
     cast did need widening, but the mixed/fallback path never reached that arm (it takes the inline
     `ObjectArrayProjectionExpression` arm), so nothing was failing there. See
     `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md` for the as-built mechanism, the two admissibility rules
     (alias agreement and sibling readability, each found via a live silent-wrong-data bug), and the declines.
  2. ✅ **Bare array projection** (`Select(b => b.Posts)`) — **DONE, EF-322 step 3a, 2026-08-06.** This bullet
     read "still fallback, and **not** for an array-specific reason: a bare (non-`new {...}`) selector body never
     populates `Projection` at all, which is the SP3-wide bare-projection boundary … Lifting the boundary is one
     piece of work covering bare scalars, bare entities and bare arrays alike." **The diagnosis held exactly**,
     and step 3a lifted the boundary as one piece of work: the bare array now emits
     `{$project: {Posts: "$Posts"}}` and is read back by that alias.
  3. 🟡 **Bare-scalar projection pushdown** — **DONE for a path-addressable leaf** (step 3a; the same change as
     bullet 2). What is left of this bullet is narrower and is now two separate things, neither of them "the
     boundary": a bare **computed** leaf (the reverted **tier 2** — §2 records its real prerequisite, that the
     late-fallback path must emit `$ifNull` rather than inherit the driver's bare `$size`), and a bare **dotted**
     leaf (`Select(b => b.Home.City)`, which additionally needs the dotted-SCALAR read below).
  4. ✅ **`OwnsOne`-hop array leaf (EF-362)** — **DONE, step 3a Task 4, 2026-08-06.** This bullet's prediction of
     the mechanism was half right and is corrected rather than deleted. It read: "a path-preserving `$project`
     emitting `{"Home.Notes": "$Home.Notes"}` (which MongoDB renders as nested output) **plus keeping the
     document-path read, rather than switching to alias-addressed**, is what it would take." The first half is
     what shipped and the nested-output claim was verified directly; the second half is **not** what shipped and
     was not needed — the read stays alias-addressed and the alias is simply *made* the dotted document path, so
     the two are the same read. The one thing the design did not predict was a **fifth** reader of the alias
     carrier (the late-fallback strip), whose too-narrow key produced silently empty collections under the
     default mode until it was widened; see `Query/AGENTS.md`.
     - **(4a) The dotted-SCALAR read — the open half of EF-362's gap, pinned but NOT fixed.**
     `Select(b => new { b.Home.City, b.Home.Notes })` declines and the fallback it lands on returns
     `City` = **null** under `Native` and `DriverLinq` alike, while the array beside it is correct.
     `BsonBinding.GetPropertyValueAtElement` builds a `BsonSerializationInfo` with a single-segment `ElementName`
     and a null `ElementPath`, so a dotted scalar name is a literal-key lookup; only the ARRAY read got the
     segment walk. **Pre-existing and byte-identical at step 3a's base** — neither caused nor closed by that
     slice — and pinned as *measured*, not as correct, by
     `Ef362OwnedHopArrayProjectionTests.Owned_hop_SCALAR_leaf_alongside_the_array_leaf_is_still_declined_and_still_loses_the_scalar`.
     It is also what keeps a bare `Select(b => b.Home.City)` declining. ~~**No ticket has been filed for it.**~~
     **STALE — corrected 2026-08-07: it is now tracked as EF-390** (filed by `99d74735`). Note the category:
     this is a **silent-wrong-data** item reachable under the default `Native` mode, which is why §9.5 carries
     it rather than §5 alone.

  5. **A non-renderable element predicate in a filtered `Count(pred)` projection (EF-365).** Newly filed by
     slice 9. `Select(b => new { N = b.Posts.Count(p => p.Heading.StartsWith("h")) })` hard-fails in every mode,
     and — measured — it does so *because* of a guard (`MongoAggregationExpressionRenderer.CanRender`) whose
     removal makes `Native`/`DriverLinq` return correct values and `NativeOnly` decline cleanly. The guard has no
     correctness role; it was retained on scope grounds. See §6.

  A **correlated** element predicate needs more than a two-scope translator **for the QUANTIFIERS**: `$elemMatch`
  cannot reference the enclosing document, so it would have to render as a top-level `$expr` over
  `$filter`/`$allElementsTrue`. **For a filtered `Count(pred)` the situation is different and easier — recorded
  here because this paragraph previously implied one blanket limit:** a `$filter` `cond` *can* reference the
  enclosing document (`{$gt: ["$$e.Rank", "$Threshold"]}` is legal), so slice 9's correlated decline is a
  deferrable *capability* needing only a two-scope element translator, not an architectural impossibility.
  Relativizing
  the owned single-reference dotted-path scalar resolver (`TryResolveOwnedFieldPath`) the way the quantifier
  resolver is scoped would let a two-scope owned dotted access work without its current blanket decline.

---

## 6. Carried tickets (EF-353…357 filed during EF-347; EF-358/359 during owned-data slice 7; EF-360/362 carried by owned-data slice 8; EF-365 filed by owned-data slice 9; EF-357/EF-358/EF-359 now closed)

**JIRA STATE — reconciled 2026-07-31.** JIRA and this document had drifted apart; the sweep below has been
done, so the two now agree. Current state:

| Ticket | JIRA status | Why |
|---|---|---|
| EF-335, EF-357, EF-358, EF-359 | **`In Code Review`** | Fixed in code and reviewed, but on the **unmerged** stack |
| EF-360, EF-362, EF-365 | `Backlog` / `Needs Triage` | Genuinely still open |
| EF-375, EF-376, EF-377 | `Needs Triage` (filed 2026-08-05) | Genuinely still open — filed by the joins slices, see below |
| EF-378 | `Needs Triage` — **should be closed as a duplicate of EF-375** | Measured to be the same defect; the transition has not been done |
| **EF-379** | `Needs Triage` — **stale; should be `In Code Review`** | **FIXED in code by joins slice 4, `9065acfc`.** This row read "Genuinely still open" alongside EF-375/376/377 and that is now FALSE. The JIRA transition (and the fixing-commit comment the other fixed tickets carry) **has not been done** — §9.6 |
| **EF-380, EF-381** | `Needs Triage` (filed 2026-08-05, by the EF-379 slice) | Genuinely still open — the two residuals EF-379 left behind, see below |
| EF-322 (epic) | `In Progress` | — |
| EF-247 | `Blocked` | Check what it is blocked on before scheduling (§9.1 item 4) |

**`In Code Review`, deliberately, not `Closed`.** None of these fixes has shipped — the whole native stack is
unmerged — so closing them would assert something untrue and leave a `Closed` ticket with no fix version.
**They should be moved to `Closed` when the stack merges**; that step stays on the cutover checklist. Each
carries a comment recording the fixing commit, the mechanism, and explicitly why it is not closed yet.

**Two summaries were CORRECTED, because they stated root causes that were subsequently measured false** — this
matters beyond tidiness, since a reader who goes to JIRA first would otherwise be misled by the ticket title
itself:

- **EF-360** was *"Anonymous projection with an entity-collection leaf throws `ArgumentException` in every
  query mode"*. Slice 8 made exactly that shape native. Now: *"Projection with a collection leaf whose
  ELEMENT TYPE has its own eager-loaded navigation…"*, matching the row below.
- **EF-358** was *"Projection path materializes null…; whole-entity materialization normalizes to an empty
  list"*. There is no such split — nothing normalized on any path pre-fix, and the apparent normalization was
  CLR field-initializer masking. Now: *"A missing or explicitly-null embedded array materializes as null
  instead of an empty collection"*.

**The SEVEN tickets filed by the joins slices (2026-08-05) — this heading said FIVE, and both the count and
EF-379's row below are corrected in place rather than deleted.** All are on the unreleased native join path — at
`v10.0.2`/`v9.1.2`/`v8.4.2` any cross-collection `Include`/`Join` throws, `TranslateJoin` is `=> null`, and
`LookupExpression`/`_lookup_` do not exist in `src/` — so none is a breaking change and none needs a
`BREAKING-CHANGES.md` entry. (EF-379 was independently re-verified against the release TAGS on its own slice:
at all three tags `TranslateSelect` explicitly *throws* on a `TransparentIdentifier` selector parameter and
`Infrastructure/MongoQueryMode.cs` does not exist at all, so every mode-dependence statement about these
defects is vacuous at the published baseline.)

| Ticket | Defect | Symptom today |
|---|---|---|
| **EF-375** | Two joins onto the **same target entity type** collapse to one `_innerCollections` entry (`IEntityType`-keyed), so flattening never fires | Throws; **or silently wrong** — see §9.5 |
| **EF-376** | Lookup aliases are **navigation-name-only** and `AddLookup` de-dups on `As`, so sibling `ThenInclude`s collapse into one `$lookup` | Declines cleanly |
| **EF-377** | A chained `Join` whose first hop has **no model navigation** has no identity to scope the second hop under | Declines cleanly (was silently 0 rows pre-EF-372) |
| **EF-378** | **Duplicate of EF-375.** Filed as "two sibling reference `Include`s without `ThenInclude`" | See EF-375 |
| **EF-379** | A root navigation to a transitive hop's **target type** misroutes the hop into the root-level branch. **This row said "Silently wrong data"; that is no longer the state.** | **FIXED — joins slice 4, `9065acfc`.** `ClassifyJoinHop` decides root-vs-transitive from `outerKeySelector.Body` alone and a transitive hop skips **both** root tiers. Residuals EF-380/EF-381 below |
| **EF-380** | **Newly filed by the EF-379 slice.** The `Unclassifiable` fall-through is REACHABLE, and one family arriving there is genuinely transitive: a `ThenInclude` nested under an **OWNED** hop, receiver `Property(Property(o.Inner, "Address"), "RegionId")`. It still takes the root tiers | **Silently wrong data — a SURVIVING instance of exactly what EF-379 was filed for.** See §9.5 |
| **EF-381** | **Newly filed by the EF-379 slice.** Reinstate the withdrawn decline for a transitive hop with no resolvable intermediate, with a discriminator that separates a JOIN-produced `Inner` from a `SelectMany`-produced one | Today: no decline; the self-referencing chain crashes loudly at materialization instead of failing cleanly |

A measured spike on 2026-08-05 **refuted** the natural hypothesis that these share one root cause. Seven
decision sites are involved and the tickets do not map one-to-one onto them; EF-375, EF-376 and EF-379 are
mutually independent, proved by instrumentation. "Key by navigation path" is a coherent *direction* but a
fiction as one fix, because **nothing in the IR records a path** — a `NavigationObjectAccessExpression` has
`parent = RootReferenceExpression` even for a hop-3 navigation — and path identity is reachable at every
*write* site and at no *read* site (`GetLookupAlias` is re-derived at ~10 read sites plus both bridge
resolvers). EF-377 is not in the family at all: it has *no* key, not a weak one. **The EF-379 fix is a
data point FOR that refutation, not against it:** it shipped as a standalone change to one method and closed
nothing else in the family — EF-375's `IEntityType`-keyed blind spot in particular is untouched, and it bit
EF-379 twice over (the withdrawn decline's `InnerCollections.Count > 1` rescue was unusable precisely because
the self-referencing case is also `Count == 1`).

Three ticket-level corrections worth knowing before picking any of them up, each measured:

- **EF-378 as filed is false.** Sibling reference `Include`s onto *different* target types work correctly. The
  precondition is the *same* target type; the absence of `ThenInclude` is incidental.
- **EF-379's stated fix direction is wrong — CONFIRMED by the shipped fix, which went the other way, and the
  defect turned out BROADER than filed.** The FK-*name* match tier misfires too on a name collision, so "prefer
  the FK-name match" is not the fix. The discriminator must be the FK's **receiver** — and not its member-chain
  depth either, since a genuine root hop was measured arriving as `Property(d.Outer.Outer, …)`. What the fix
  added on top of the prediction: the **TARGET-TYPE-only tier misfires INDEPENDENTLY**, with no name collision
  present anywhere in the model, so **both** root tiers had to be gated — a mutation gating tier 1 alone (i.e.
  the ticket's own fix direction, implemented exactly) leaves both doorway fixtures red. The receiver's CLR
  *type* is not a discriminator either (a self-referencing model shares one between root and intermediate), and
  `outer.ShaperExpression` — which the pre-implementation design note assumed had to be threaded in — is not
  needed at all.
- **All five ORIGINAL tickets' line numbers are stale**, written against pre-rebase `34a02067`. The JIRA
  comments name sites by method instead. (EF-380/EF-381, filed later, follow the same name-by-method rule.)

This is the **third** time in this area that a separately-filed symptom turned out to be another doorway into
one defect — EF-366 via `GroupBy` and `Distinct`, EF-372 via `ThenInclude` and chained `Join`, and now EF-378
into EF-375. Worth assuming next time rather than discovering.

*Both tickets' original **descriptions** are left intact as the historical bug report; the correction lives in
a comment on each, which is where a reader will look for it.*

| Ticket | Type | Summary | Severity |
|---|---|---|---|
| **EF-353** | Task | Native bare owned-element SelectMany can't materialize **nested owned members** — currently a clean decline (`GetNavigations().Any()` guard); lifting it needs a re-rooted projection mapping | Feature gap, clean decline |
| **EF-354** | Bug | `SelectMany(o => o.Items, (o,i) => o)` (whole-outer, explicit method-call spelling) **crashes** ("Id missing") instead of declining cleanly; query-syntax spelling already declines | Loud crash, not wrong data |
| **EF-355** | Bug | Filtered reference SelectMany: folded-predicate split in `TrySplitCorrelation` can **silently drop a `!= null` inner filter** → returns all children | Silent wrong data, **latent/unreachable** today (EF emits nested, not folded, shape) |
| **EF-356** | Bug | Mixed whole-entity + computed-arithmetic projection (`new { c, Total = c.Age * c.Score }`) returns **silently wrong** values (`Score²`) — mixed shaper has no `BinaryExpression` handling | Silent wrong data, **pre-existing**, pinned by a documenting test |
| **EF-357** | Bug | **CLOSED** (`7c199e4`, 2026-07-29). Bare embedded-collection `.Count` projection (`Select(b => b.Posts.Count)`) threw `ArgumentException` in **every** query mode — a `MongoProjectionBindingExpressionVisitor` gap, not a native decline. Owned-data slice 7 (`0cb1b1b`) fixed the translation-time `ArgumentException` and made present arrays return correct counts, leaving a missing/explicitly-null-array `ArgumentNullException` at materialization as a residual (that residual was EF-358). The EF-358 fix closed that residual, so `Select(b => b.Posts.Count)` now returns correct counts for every array state | Was: hard fail every mode. Now: correct for every array state |
| **EF-358** | Bug | **CLOSED** (`7c199e4`, 2026-07-29). Root cause corrected during investigation — it is **not** a whole-entity-vs-projection split; pre-fix, nothing normalized a missing/explicitly-null embedded array on *any* path, and the apparent whole-entity normalization was CLR field-initializer masking (`MongoProjectionBindingRemovingExpressionVisitor.IncludeCollection` skips its fixup loop when `relatedEntities` is `null`). Fix: delete the null-collapse conditional from `BsonDocumentInjectingExpressionVisitor`'s `CollectionShaperExpression` case; add a `Coalesce` to an empty `BsonArray` at the point of use in `MongoProjectionBindingRemovingExpressionVisitor`'s `CollectionShaperExpression` case. Result is uniform, initializer-independent normalization on every path/mode/cardinality; closes EF-357's residual. See §4 and `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md` for the full mechanism | Was: runtime throw / inconsistent shape. Now: closed |
| **EF-359** | Bug | **CLOSED** (`229294f`, owned-data slice 9, 2026-07-30). Filtered `Count(pred)` in a projection (`Select(b => new { N = b.Posts.Count(p => p.Rank > 0) })`) threw `InvalidOperationException` in **all three** query modes — a translation-time crash in `MongoProjectionBindingExpressionVisitor.Translate`, before `MongoQueryMode` is read; same shape of defect as EF-357, **not** the graceful decline earlier docs assumed. Mechanism of the fix, in one line: recognize the predicated `Count`/`LongCount` overloads by canonical `MethodInfo` at both the translator and the projection-binding sites, represent the result as a new sealed sibling node `MongoFilteredSizeExpression` (never a flag on `MongoSizeExpression`, so the Tier-1 array-index renderer, the query-dialect classifier and the negator all fail closed), and render `{$size: {$filter: {input: {$ifNull: […]}, as: "e", cond: …}}}`. The predicate spelling went native too; the bare spelling now folds client-side with correct values instead of crashing. Narrower residuals remain (§4), one of them filed as EF-365 | Was: hard fail every mode. Now: native, closed |
| **EF-365** | Bug | **Newly filed by owned-data slice 9.** A **non-renderable element predicate** in a filtered `Count(pred)` *projection* — e.g. `Select(b => new { N = b.Posts.Count(p => p.Heading.StartsWith("h")) })` — hard-fails with `InvalidOperationException` in all three modes, where a graceful fallback is demonstrably available. MEASURED: with `MongoAggregationExpressionRenderer.CanRender` gating the branch (as shipped) the query crashes in every mode; with the check removed, `Native` and `DriverLinq` return the **correct** value and `NativeOnly` declines cleanly. So the guard *preserves* a pre-existing crash — it has **no correctness role** (the `$expr`-inside-`$elemMatch` hazard is `IsQueryDialectRenderable`'s job) and the design doc's justification for it is measured false. It was retained in EF-359 on scope grounds only. Fix = delete the call site, then the now-callerless classifier and its unit tests, and re-baseline the pinned residual-decline test. **Breadth still to verify before that ships:** only `StartsWith` was measured — `Contains`/`$in`, unary `Not`, a bare nullable bool, and a MIXED projection (a declining leaf beside an admitted one, where the driver may not emit the alias the shaper reads) are UNVERIFIED | Hard fail every mode, **pre-existing**, pinned by a documenting test |
| **EF-360** | Bug | **STILL OPEN, and RE-CHARACTERISED here — it is *not* "an anonymous projection with an entity-collection leaf throws".** That framing was disproved by owned-data slice 8, which made exactly that shape native. The actual defect: an anonymous **or** DTO projection whose collection leaf's **ELEMENT TYPE has a navigation of its own** throws `ArgumentException` ("does not match the corresponding member type") in **every** query mode, in `MongoProjectionBindingExpressionVisitor.VisitNew`, via the `Queryable.Select`-rebuild → `MatchTypes` short-circuit (`MatchTypes` returns the `List<T>`-typed shaper untouched for an `IQueryable<T>` target, so BCL validation throws at the `newExpression.Update(newArguments)` call). **Cited by METHOD, not by line:** earlier docs quoted `MongoProjectionBindingExpressionVisitor.cs:661`, which this slice's own additions to that file made stale — `:661` is now `return null!;`. It reproduces for a nested owned **collection** and a nested owned **single reference** alike, and the **bare** spelling of the same query on the same model works. It fires at shaper-BUILD time, before `MongoQueryMode` is read, so the mode is irrelevant. Slice 8 declines the shape explicitly (`IsNativeArrayProjectionLeaf`'s element-navigation conjunct) and keeps the failure **byte-identical**, verified by an A-B probe; that conjunct is currently defence-in-depth over a pre-existing structural decline in `TryResolveOwnedCollectionPath` (positive-control-verified) and is documented as such in `Query/AGENTS.md` so it is not deleted as dead code. Same fall-through root cause as EF-357/EF-359 | Hard fail every mode, **pre-existing**, pinned by documenting tests |
| **EF-362** | Task | **Newly filed by owned-data slice 8.** `OwnsOne`-hop array leaf: `Select(b => new { b.Title, b.Home.Notes })` stays a clean decline (falls back, correct results; throws only under `NativeOnly`), pinned by a mutation-verified tripwire test. It is **not** a relaxation of slice 8's rules — for a hop the `$project` alias is necessarily FLAT (`"Notes"`) while the document path is NESTED (`"Home.Notes"`), so the alias-agreement invariant *cannot* hold and lifting the conjunct alone would return a silently EMPTY collection on any fallback path. Needs a path-preserving `$project` (`{"Home.Notes": "$Home.Notes"}`, which MongoDB renders as nested output) plus retaining the document-path read instead of switching to alias-addressed | Feature gap, clean decline |

Of these, **EF-356** (reachable today under `Native`) and **EF-355** (latent) are the two that produce (or
could produce) *silent* wrong data. Confirmed unaffected by the EF-358 fix: neither EF-356 nor EF-355 touches
a `CollectionShaperExpression`'s null/missing-array handling — EF-356 is a mixed-shaper arithmetic-leaf gap and
EF-355 is a predicate-folding gap in `TrySplitCorrelation`, both orthogonal to the two edits EF-358 made.

Also recorded as a **comment on the EF-322 epic** rather than its own ticket (a family of shapes, sharing the
fall-through root cause that EF-357 and EF-359 also had): an interposed `Distinct`/`Take`/`Reverse`/`DefaultIfEmpty`/`Concat`
between an owned-collection `Select` and a terminal operator hard-fails at translation in every mode with a
duplicate-key `ArgumentException` from `_collectionShaperMapping.Add`. Pre-existing; neither caused nor fixed by
slice 7.

---

## 7. Which tests require driver-LINQ to pass — empirical measurement

> ⚠️ **STALE AS A SNAPSHOT — every total in §7.1, §7.2 and §7.3 is a `229294f` figure and has been superseded
> by the 2026-08-07 re-measurement at `99d74735` in §9.** Two slices landed in between (`VectorSearch`,
> step 3a). Current totals: `Native` **4593 / 0 / 17**, `NativeOnly` **2427 / 2166 / 17** (was 4589/0/19 and
> 2194/2395/19), and the coverage-gap split is **1598 / 518 / 50**, not 1742 / 651 / 2 — see §9.1, which also
> records the two §7.2 bucket LABELS that measured out false. **§7.4's method is still correct and is what was
> re-run**; only the numbers below are historical. The current per-class breakdown is in the box after §7.1.

Measured by the two-sweep subtraction on the **EF10 specification suite**. **Fully re-measured at the current
tip `229294f` on 2026-07-31** — both sweeps re-run from scratch after slices 8 and 9 landed, with §7.1 and §7.2
re-derived from the fresh `nativeonly.trx` (previous revisions carried §7.2 forward from `1dd7862`). The
totals and every row of §7.1 reproduced the slice-7 figures **exactly**; §7.2 is restated below from the fresh
data. (Historical note retained: an earlier revision cited `7532b15` for the `.Count`-in-a-predicate slice;
that hash is not in the shipped history — it exists only on the pre-squash safety branch
`EF-322-owned-collection-count-native-presquash`, and the squashed slice-6 commit is `1b4c1d6`.)

> `{ tests requiring driver-LINQ } = { pass under Native } − { pass under NativeOnly }`

| Mode | Passed | Failed | Skipped | Total |
|---|---:|---:|---:|---:|
| `Native` (default) | **4589** | 0 | 19 | 4608 |
| `NativeOnly` (fallback removed) | **2194** | **2395** | 19 | 4608 |

Because `Native` fails **zero** tests, every `NativeOnly` failure was a `Native` pass — so the set is exact:

> **2395 spec tests currently require the driver-LINQ fallback to pass.** They go green under `Native`
> only by silently falling back; remove the fallback and they throw `NativeTranslationNotSupportedException`.

**Delta since the last measurement (slice 7, `ab886fa`): ZERO.** Slices 8 and 9 moved neither total, and the
per-class table is unchanged row for row. Before that, the only movement in the whole owned-data stream was
**2 tests** at slice 5 (`NativeOnly` 2192 → 2194 passing, 2397 → 2395 failing): both
`NorthwindAggregateOperatorsQueryMongoTest.Select_All` (`async: True`/`False`), which went native via the
EF-335 negator. Its `Native`-mode MQL baseline was re-baselined in that slice too — the driver fallback's
trailing `{ "$project" : { "_id" : 0, "_v" : null } }` stage disappears, which is the signature of native
routing. Results were unchanged.

So the figures above have now reproduced identically at **five consecutive tips** — `b087957` (slice 5),
`1b4c1d6` (slice 6), `ab886fa` (slice 7), and now `229294f` (slices 8 + 9 inclusive). Expected, and the reason
is structural rather than a coincidence to be re-investigated each time: **Northwind has no owned collections
or owned sub-property coverage**, so nothing in the owned-data work stream can touch these tests. See the
closing caution in §8 — a flat spec number here does not mean a slice achieved nothing.

**Both axes were checked per test on this sweep, not just the aggregate:** `Native` produced an **empty**
failure list (so no `Native`-mode MQL baseline moved — a baseline that had changed would surface as a failure
against its checked-in string), the two runs covered an identical 4608-test name set, and the per-class
`NativeOnly` breakdown in §7.1 was re-derived from the fresh `nativeonly.trx` and matched all 24 rows. Two
cautions for whoever re-measures next:

- **A total of 4608 is correct.** One intermediate measurement during slice 5 reported 4601 (7 low) and was
  wrong; the figure here reproduced exactly on a fresh base-vs-branch run and again on the final three-version
  sweep. Do not "correct" this table downward without a clean re-measurement.
- **Check both axes per test.** A test can be `NativeOnly`-failing *and* have a `Native`-mode MQL baseline that
  a slice changes — `Select_All` is exactly that. An inventory built only from the `NativeOnly` pass set will
  miss such flips (it missed this one).

Scope note: spec suite, EF10, at this tip. The functional `Native*` tests self-parametrize across modes so
they don't count here; unit tests don't touch a database.

### 7.1 By test class (24 classes)

*Re-derived from the fresh `229294f` `nativeonly.trx` (2026-07-31). Every one of the 24 rows below matched the
figure already in the table, summing to 2395 — this table is now confirmed at slice 7 and again at slice 9.*

| Count | Class |
|---:|---|
| 549 | NorthwindMiscellaneousQueryMongoTest |
| 234 | NorthwindAggregateOperatorsQueryMongoTest |
| 226 | NorthwindWhereQueryMongoTest |
| 208 | NorthwindGroupByQueryMongoTest |
| 198 | NorthwindSelectQueryMongoTest |
| 132 | NorthwindEFPropertyIncludeQueryMongoTest |
| 132 | NorthwindIncludeQueryMongoTest |
| 130 | NorthwindStringIncludeQueryMongoTest |
| 114 | NorthwindSetOperationsQueryMongoTest |
| 104 | NorthwindIncludeNoTrackingQueryMongoTest |
| 78 | NorthwindJoinQueryMongoTest |
| 62 | NorthwindNavigationsQueryMongoTest |
| 56 | VectorSearchMongoTest |
| 56 | VectorSearchExactMongoTest |
| 29 | NorthwindQueryFiltersQueryMongoTest |
| 24 | NorthwindBulkUpdatesMongoTest |
| 14 | NorthwindKeylessEntitiesQueryMongoTest |
| 14 | BuiltInDataTypesMongoTest |
| 12 | NorthwindAsNoTrackingQueryMongoTest |
| 10 | NorthwindDbFunctionsQueryMongoTest |
| 6 | NorthwindChangeTrackingQueryMongoTest |
| 3 | NorthwindCompiledQueryMongoTest |
| 3 | NorthwindAsTrackingQueryMongoTest |
| 1 | NorthwindQueryTaggingQueryMongoTest |

**CURRENT per-class breakdown, re-derived 2026-08-07 at `99d74735` (total 2166).** The table above is the
`229294f` snapshot and is kept for the historical comparison; this is the live one. The right-hand column is
the §9.1 category-(a) subset — the cases that are genuine coverage gaps rather than exception-shape or
baseline bookkeeping.

| All 2166 | of which (a) | Class |
|---:|---:|---|
| 521 | 326 | NorthwindMiscellaneousQueryMongoTest |
| 234 | 146 | NorthwindAggregateOperatorsQueryMongoTest |
| 218 | 146 | NorthwindWhereQueryMongoTest |
| 202 | 162 | NorthwindGroupByQueryMongoTest |
| 180 | 138 | NorthwindSelectQueryMongoTest |
| 118 | 110 | NorthwindEFPropertyIncludeQueryMongoTest |
| 118 | 110 | NorthwindIncludeQueryMongoTest |
| 116 | 108 | NorthwindStringIncludeQueryMongoTest |
| 110 | 96 | NorthwindSetOperationsQueryMongoTest |
| 90 | 82 | NorthwindIncludeNoTrackingQueryMongoTest |
| 72 | 36 | NorthwindNavigationsQueryMongoTest |
| 60 | 52 | NorthwindJoinQueryMongoTest |
| 29 | 23 | NorthwindQueryFiltersQueryMongoTest |
| 24 | 2 | NorthwindBulkUpdatesMongoTest |
| 14 | 13 | BuiltInDataTypesMongoTest |
| 12 | 6 | NorthwindKeylessEntitiesQueryMongoTest |
| 12 | 12 | NorthwindAsNoTrackingQueryMongoTest |
| 10 | 4 | NorthwindDbFunctionsQueryMongoTest |
| 8 | 8 | VectorSearchMongoTest |
| 8 | 8 | VectorSearchExactMongoTest |
| 5 | 5 | NorthwindChangeTrackingQueryMongoTest |
| 3 | 3 | NorthwindAsTrackingQueryMongoTest |
| 2 | 2 | NorthwindCompiledQueryMongoTest |
| **2166** | **1598** | **total** |

Three movements worth naming: `VectorSearchMongoTest` + `VectorSearchExactMongoTest` **112 → 16** (the step-2
slice), `NorthwindQueryTaggingQueryMongoTest` has **left the list entirely**, and
`NorthwindBulkUpdatesMongoTest` holds at 24 — which is itself evidence for §9.2, since the bulk path is
supposed to be driver-LINQ-only and yet 24 of its cases see the native gate.

### 7.2 By *why* they fall back (failure-message buckets)

**Re-derived from the fresh `229294f` sweep (2026-07-31), replacing the carried-forward `1dd7862` figures.**
The three *data* buckets (873 / 794 / 54) and the regex bucket (13) are **unchanged**; the rest shifted, and
the reconciliation is stated below the table rather than left as an apparent contradiction.

| Count | Cause |
|---:|---|
| 881 | **Non-entity projection not natively representable** — computed / scalar / client-eval projection long tail (`Select` shapes, casts, client methods). **CORRECTED 2026-08-06: the count is 881, not 873, and a naive substring bucketing of this message yields 1030 — 149 of those are `Assert.Throws` messages QUOTING the inner exception. Classify `Assert.Throws` failures FIRST. More importantly the LABEL is wrong: only 360 of the 881 have the projection binder as sole cause; 363 decline somewhere else first (GroupBy 130, `Where` predicates 85, scalar-aggregate 56, `OrderBy` keys 24, Distinct 20, post-terminal guards 34, catch-all 4). See `docs/superpowers/specs/2026-08-06-step3-projection-spike-findings.md` §Q2.** |
| 794 | **Query not natively representable** — joins, cross-collection navigation, non-native GroupBy shapes, misc operators |
| 559 | `Assert.Throws` **exception-type mismatch** — feature unsupported in *every* mode; test pins the *driver-LINQ* exception type, native throws `NativeTranslationNotSupportedException` instead |
| 66 | `ArgumentOutOfRangeException` (index) — a materialization / shaper gap surfaced only under `NativeOnly` |
| 54 | **Reference-nav `$lookup` not supported** — reference `Include` / navigation lookups |
| 26 | `Assert.Contains` — expected *error-message text* differs between the two throws |
| 13 | Non-constant regex pattern (EF-247) |
| 8 | Predicate renderer gap (`Not` over unsupported subtree) |
| 2 | `Throws_on_concurrent_query_first` — an MQL-string assertion (`"Customers."` vs `"Customers.{ "$limit" : 1 }"`), not a translation gap at all |
| **2395** | **total** |

Reconciling with the previous revision's numbers, so the differences are not mistaken for regressions:

- **`Assert.Throws` 507 → 559 and `Assert.Contains` 74 → 26** is almost certainly a *classification* difference,
  not a behavior change: this sweep's script buckets on `Assert.Throws` **first**, so a failure whose message
  contains both markers now lands in the `Assert.Throws` row. The pair sums 581 → 585, and the 4-test
  difference is accounted for by the 2 newly-separated `Throws_on_concurrent_query_first` rows plus the 2-test
  `Not`-renderer drop. Treat the *pair* as stable, not each row.
- **`Not` renderer 10 → 8** is real and is the expected direction: owned-data slice 5 taught `RenderUnary` to
  render `Not` over a query-native comparison. The bucket did **not** empty, by design — a `Not` whose operand
  is a conjunction or a nested `Not` still declines.
- The 2 `Throws_on_concurrent_query_first` rows were previously absorbed into an assertion bucket and are
  broken out here because they are categorically different: nothing about them is a coverage gap.

### 7.3 The fallback set splits into two meaningfully different kinds

> **SUPERSEDED 2026-08-07 by §9.1 — the SPLIT survives, the NUMBERS and one of the LABELS do not.** Current:
> **(a) 1598** genuine coverage gaps, **(b) 518** unsupported in every mode, **(c) 50** `AssertMql` baseline
> bookkeeping. In particular the "66 `ArgumentOutOfRangeException` … a materialization / shaper gap" label
> below is **FALSE** — re-measured, all 48 of them (it is 48 now) bottom out in
> `TestMqlLoggerFactory.AssertBaseline`, i.e. they are baseline-length failures, not shaper failures. And 18
> cases that this split filed as "failure shape" are really coverage gaps: driver-LINQ **runs** those queries
> and throws at execution (`TruncationException`, `FormatException`, `MongoCommandException`, …), so native
> declining at translation *is* lost functionality.

*Figures below are the fresh `229294f` counts (previously stated as "~1744 / ~647" from the `1dd7862` sweep).*

- **1742 need driver-LINQ for correct *results*** (the `NativeTranslationNotSupportedException` data buckets:
  873 + 794 + 54 + 13 + 8). These are the genuine coverage gaps — remove the fallback and the user gets an
  exception instead of data. **This is the number that has to reach zero, or a deliberately accepted
  remainder, before driver-LINQ can be retired without regression** — see §9.
- **651 differ only in *failure shape*** (559 exception-type + 26 message + 66 index). These features are
  unsupported in *every* mode (no correct data is produced either way); the tests pass under `Native` only
  because the override pins the *driver-LINQ* exception type/message. Strictly they "require driver-LINQ to
  pass as written," but they aren't lost functionality — ~~at parity cutover these overrides get re-baselined
  to assert the native exception. **This is bookkeeping at cutover, not coverage work**~~ **— STALE ON PLAN
  FRAMING, corrected 2026-08-07 on the final whole-phase review. The banner at the head of §7.3 scopes itself
  to the numbers and one label; this clause is neither, and it outlived both.** The re-baselining does **not**
  happen at this release's merge: under the 2026-08-07 merge plan the driver path **ships**, so these
  overrides keep passing exactly as they do today and nothing is re-baselined. The work is real but belongs to
  the later, separate project that retires driver-LINQ (§8, §9.3). **This is bookkeeping at *retirement*, not
  coverage work, and not a merge-blocker** — but at 651 tests it
  is large enough to schedule deliberately rather than discover.
- **2 are neither** — the `Throws_on_concurrent_query_first` MQL-string assertions.

Representative examples: `All_client`, `Client_method_in_projection_requiring_materialization_1`,
`Cast_on_top_level_projection_brings_explicit_Cast` (projection long tail); `All_after_GroupBy_aggregate`,
`Anonymous_projection_Distinct_GroupBy_Aggregate` (GroupBy shapes); `VectorSearch_Memory_floats`
(vector search).

### 7.4 How to reproduce

```bash
# Build once (both MONGODB_URI and ATLAS_URI unset → isolated atlas-local container per run)
dotnet build tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj -c "Debug EF10"

# Baseline (Native default)
dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --logger "trx;LogFileName=native.trx" --results-directory <dir>

# Native-only (fallback removed)
MONGODB_EF_NATIVE_ONLY=1 dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --logger "trx;LogFileName=nativeonly.trx" --results-directory <dir>

# The tests that require driver-LINQ = the Failed set in nativeonly.trx
# (equivalently: pass-set(native.trx) − pass-set(nativeonly.trx))
```

`MONGODB_EF_NATIVE_ONLY=1` flips every spec context's `DbContextOptionsBuilder` to
`MongoQueryMode.NativeOnly` (`MongoTestStore.AddProviderOptions`), so any query that would otherwise
silently fall back throws instead.

---

## 8. Bottom line

SP1–SP4 are complete; SP5–SP6 are substantially complete with a well-characterized fallback set; the
SelectMany tail (SP6) is finished. **SP7 Phase 1 (the one-pass materializer) has landed**, cutting native
allocation 54–72% to roughly the raw-driver floor. Since then, a **nine-slice owned-data work stream** has
made embedded documents largely native: whole-entity (single-ref and collection), single-ref sub-property dotted
paths, both `Any` and `All` quantifier predicates — the latter closing **EF-335** — `.Count` used in a
predicate, unified with bare `Any()` as one array-cardinality representation, `.Count` as a projection leaf,
an owned entity-**collection** (array) leaf inside a terminal anonymous/DTO projection, and a **filtered**
`.Count(pred)` in both a predicate and a projection leaf — the last closing **EF-359**. (This paragraph said
"six-slice" until slice 8 and "eight-slice" until slice 9; corrected here each time, along with the follow-on
claim below.)

**CORRECTED at this revision — this paragraph used to read "The remaining native work is SP7 Phase 2
(streaming breadth: reducer/aggregate, collection-Include arrays, reference-Include) plus the parity cutover
that retires driver-LINQ." That is true only if "the parity cutover" is read as a single line item, which
badly understates it.** §9 enumerates what parity actually requires. ~~The headline is that the
remaining work is dominated by three things this sentence did not name: **joins** (no native form at all),
**reference `Include`**, and the **computed/client-eval projection long tail** — together the bulk of the 1742
spec tests that still need driver-LINQ for correct results.~~ **CORRECTED AGAIN 2026-08-07 on measurement:
that trio is 373 + 290 = 663 of the 1598, i.e. 41%, not "the bulk". The largest single item is
`MongoExpressionTranslator` expression breadth at ≈588, which this sentence has never named at all** — see
the re-attribution paragraph at the end of this section and §9.7. SP7 Phase 2 is real but is **performance,
not parity**, and does not gate the cutover. One further item is still in no plan: the EF9+ bulk
`ExecuteUpdate`/`ExecuteDelete` path shares the driver-LINQ bridge — **and, measured at this tip, it does more
than share it: it re-enters `TranslateQuery`, the fallback's own entry point, and consumes
`MongoExecutableQuery.Provider`/`.Query`** (§9.2). ~~**The next work is joins,
beginning with reference `Include`**~~ — **STALE as of 2026-08-05, corrected here rather than deleted: that
work stream has landed SEVEN slices** (§2), ending with EF-379, and §9.8's step 1 is now substantially
delivered — `ThenInclude` breadth, filtered `Include`, collection-of-collection and the general join remain
behind it. ~~but the next scheduled work is §9.8's **step 2, `VectorSearch`**~~ — **also STALE as of 2026-08-06:
step 2 (`VectorSearch`) is DONE and so is step 3's first slice, 3a (the bare-projection boundary).**
~~The next scheduled work is §9.8's step 3b, the entity leaf inside a projection.~~ **STALE as of 2026-08-07:
on the corrected ranking the next work is §9.8's new step 4, `MongoExpressionTranslator` breadth, which pays
into predicates, sort keys and 3c at once; 3b follows it (step 5) and still carries the standing ruling that
it must FIX EF-356 rather than pin it.** See §9.8 for the execution order (and the owner ruling that inserted
EF-379 ahead of step 2), and §9.2 for the EF-317 ruling that unblocked step 1.

**CORRECTED at owned-data slice 8 — this paragraph previously named the nearest owned-data follow-on as
"array-valued embedded-collection projections (`Select(b => b.Posts)`), blocked on an alias-driven array
read-back mechanism in the DOM shaper". That is now wrong twice over.** First, the alias-driven read-back
mechanism EXISTS: slice 8 made the **wrapped** spelling (`Select(b => new { b.Title, b.Posts })`) native.
Second, the **bare** spelling was never blocked on that mechanism at all — its blocker is the SP3-wide
bare-projection boundary (a bare selector body never populates `Projection`), exactly as §5 bullet 3 now states;
it falls back and returns correct results. **CORRECTED AGAIN at owned-data slice 9:** this paragraph then named
the nearest owned-data follow-on as **EF-359** (filtered `Count(pred)` in a projection), "a translation-time hard
fail in all three modes and therefore a bug fix rather than a fallback→native widening". That characterization
was right, and slice 9 acted on it — EF-359 is **closed**, the shape is native, and it is no longer a follow-on
at all. **CORRECTED AGAIN at EF-322 step 3a (2026-08-06):** this then named the nearest owned-data follow-ons as
"the **bare** array projection and the SP3-wide bare-projection boundary behind it, the `OwnsOne`-hop array leaf
(**EF-362**), and **EF-365**". **The first three of those are now DONE** — step 3a lifted the bare-projection
boundary (so the bare array projection is native) and its Task 4 shipped EF-362. What is left from that list is
**EF-365** (a
non-renderable element predicate in a filtered-count projection hard-fails where a graceful fallback is
measurably available), plus two things step 3a pinned rather than fixed: the **dotted-SCALAR read**
(§5 bullet 4a, **EF-390**) and the reverted **tier 2** for bare computed leaves (§2). An arithmetic projection leaf containing a count already goes native as an incidental
widening — for a *filtered* count too, also incidentally, and only in the **wrapped** spelling. **This paragraph's parenthetical about the bare-count form was
also STALE; corrected here.** It used to say the bare form "is a separate, pre-existing hard-fail predating this whole work stream" — true
only before owned-data slice 7. Since slice 7 the bare form (`Select(b => b.Posts.Count)`) no longer fails
translation; since EF-358 (2026-07-29) it returns correct results for every array state, including missing or
explicitly-null. See §4 and §6 for the current disposition.

~~Empirically, 2395 EF10 spec tests still lean on the driver-LINQ fallback — **1742** for correct results (real
coverage gaps) and **651** only for the expected exception shape (re-baselined at cutover), plus 2 that are
neither.~~ **RE-MEASURED 2026-08-07 at tip `99d74735`, and the paragraph is corrected in place rather than
annotated beside its stale text. §7 above is a `229294f` snapshot and is now STALE on every total; §9 carries
the current figures.** At this tip **2166** EF10 spec cases lean on the fallback (`Native` 4593/0/17,
`NativeOnly` 2427/2166/17), and re-deriving the split by *decline site* rather than by failure message gives:

| | Cases | What it is |
|---|---:|---|
| **(a)** | **1598** (73.8%) | genuine coverage gaps — remove the fallback and the user gets an exception instead of data |
| **(b)** | **518** (23.9%) | unsupported in **every** mode; the test pins driver-LINQ's exception type/message, so retirement loses nothing |
| **(c)** | **50** (2.3%) | pure `AssertMql` baseline bookkeeping — a re-baseline resolves them |

So the number that gates retirement is **1598, not 1742** — see §9.1 for the derivation, the method, and the
two labels in the old figure that measured out FALSE. The owned-data stream's flat spec number across slices
5–9 still stands, and for the same structural reason: **Northwind has no owned collections**, so that work
stream's coverage gains are proven by the functional `Native*` suites, not by the spec scoreboard. A flat spec
number does not mean a slice achieved nothing — but, per §9, it does mean the owned-data stream is not on the
cutover's critical path.

**MOVED ON 2026-08-08 by stream 1 tranche 1 (§2), corrected here rather than annotated beside the stale text:
the tip triple above is no longer current.** **UPDATED AGAIN once the whole of tranche 1 had landed (slices B
and A1), in place rather than beside — the intermediate `2ad8524a` reading of `NativeOnly` 2461/2132/17 was
correct at that commit and is superseded.** At the tranche tip (slice A1's fix wave included) the `NativeOnly` axis is **2501/2092/17** and
the default `Native` axis is **4593/0/17**, unmoved. So **2092**, not 2166, EF10 spec cases now lean on the
fallback — `2166 − 74` = 2092, the 74 being the tranche's re-summed wins (§2). **The (a)/(b)/(c) split above
has NOT been re-derived at this tip** and is still the `99d74735` decline-site measurement; all 74 are
(a)-type coverage gaps by construction (each went from an exception to data on the `NativeOnly` axis), so (a)
is **1524** on that basis alone — `1598 − 74` — and (b) 518 / (c) 50 are unchanged. That subtraction is
**INFERRED** from the win-type, not re-measured; the re-derivation belongs to §9.8 step 3's checkpoint.

**And the headline of the 2026-08-07 re-attribution.** Partitioned into disjoint owning work streams, the
1598 ranks: **joins / cross-collection 373** (deferred, **EF-392**), **`MongoExpressionTranslator` breadth in
predicate and sort-key position 368**, **projection long tail 290** (3b 52 / 3c 220 / 3d 18 — 3d deferred,
**EF-395**), **GroupBy 130** (deferred, **EF-393**), **composite-PK member access 116** (deferred, **EF-394**),
`Distinct` 84, scalar-aggregate binder 82, set operations 42, no-binder operators 40, post-terminal 34, regex
19 (deferred, **EF-247**, JIRA status `Blocked`), the rest ≤ 8 each (`Not` over an unsupported subtree,
deferred as **EF-396**; of the remaining stragglers, the reducer binder and set-op-with-collection-`Include`
items — 8 cases — are deferred as **EF-397**, and the `VectorSearch` pre-filter item — 4 cases — keeps its
pre-existing **EF-382**, per §9.1 row 13). Two of those rows are the news.
First, **translator breadth is one capability appearing in three positions** — the same ~20 features (casts,
`??`, `?:`, `EF.Property`, `Nullable.Value`, string concat, entity equality, `Contains` over a client
collection, …) account for the 368 *and* for the 220 in the projection column, so **≈588 cases (37% of the
1598) are reachable by one translator work stream** that has no slice, no ticket and no place in §9.8's
execution order. *(The 2026-08-07 stream-1 spike —
`docs/superpowers/specs/2026-08-07-stream1-translator-breadth-spike.md` — re-derived this disjointly and
measured **580**, −1.4%, reproducing every structural row of §9.1 exactly. It also found that "one work
stream" is **two capabilities**, not one: see the correction to the plan-of-record paragraph below.)* Second, **composite-PK member access is 116 measured cases, 112 of them inside the four
reference-`Include` suites** — §9.1 has listed it for weeks as "not in the table at all, because Northwind
does not cover it", and that is FALSE: Northwind's `OrderDetail` has a composite key, and the
`Include_references_*` / `Include_multiple_references_*` families walk straight into it. See §9.1 and §9.7.

**2026-08-07 — the plan of record, stated once, here.** The old plan was "reach parity with driver-LINQ,
retire it, merge." **That plan is withdrawn.** The owner's replacement: merge at **~80% coverage** with no
major architectural issues and good tracking of the rest; the driver path **ships** in this release and is
retired later, as a separate, out-of-scope project (full detail:
`docs/superpowers/specs/2026-08-07-native-query-merge-plan-design.md`). ~~Four streams deliver **82.2%**
(3349/4075 of the addressable surface): **stream 1** translator breadth (588 cases)~~ — **CORRECTED
2026-08-07 on the final whole-phase review, from the stream-1 spike
(`docs/superpowers/specs/2026-08-07-stream1-translator-breadth-spike.md`), whose revised figures had not
reached this document at all. Four streams deliver 81.7% (3331/4075 of the addressable surface):**
**stream 1** translator breadth (**580** measured, not the cited 588 — and its *realistic* yield is **580 total
/ 474 sole-cause**, of which **74 depend on the computed-sort slice**, leaving **400** without it and
**≈508 after all of stream 1 with slice B** (474 + 34 that close within stream 1 once the sort-key multiplier
is in) — judged against **≈570** after streams 1 and 2 together; it
contributes **+570** to the projection, after stream 2 has also landed), **stream 2** the
sole-cause tranche (282 cases), **stream 3** slice 3b / the EF-356 fix (52 cases), and **stream 4** EF-375 —
a targeted correctness fix, contributing 0 cases (its cases sit inside the deferred joins bucket and stay
failing either way). The arithmetic is **2427 + 570 + 282 + 52 = 3331 → 81.7%**, against a bar of
`0.80 × 4075 = 3260` — a margin of **71** cases, not the ~90 the merge plan claimed before the spike ran.
**Stream 1 is not "each feature ships independently" as this document has described it** (see §9.8 step 2):
the spike found it is **two capabilities**, and the second — the **computed-sort slice ("slice B")**, which
is IR/lowerer/renderer work rather than a translator arm — **delivers nothing on its own and is load-bearing
for the bar.** Without it the projection is **≤3257/4075 = ≤79.9%**, i.e. **below 80%**. The owner ruled it
into stream 1 on 2026-08-07. Deferred and tracked: joins **373 (EF-392)**, GroupBy **130 (EF-393)**, composite-PK
**116 (EF-394)**, slice 3d **18 (EF-395)**, non-constant regex **19 (EF-247**, `Blocked`**)**, `Not` over an
unsupported subtree **8 (EF-396)**, and the remaining stragglers **8 (EF-397)** plus the pre-existing
**EF-382** **4** (the `VectorSearch` pre-filter item; §9.1 row 13 — EF-397 does **not** cover it, avoiding a
duplicate ticket) — together the ~12-case "rest ≤ 8 each" bucket above. Two tickets are explicitly *not* deferred and
*not* "correctness gaps that ship" either, because they are being fixed in this plan: **EF-356** (fixed by
stream 3) and **EF-375** (fixed by stream 4, pulled in 2026-08-07 after the Task-1 ticket audit found its
throw fires on an ordinary self-referencing model shape — ordinary, not uncommon, so it failed the bar for a
deferred correctness gap). The correctness gaps that **do** ship, deferred but admitted under the
well-defined/uncommon/tracked bar: **EF-380, EF-390, EF-355** (§9.5). Retiring driver-LINQ — everything §9
enumerates — is **not part of this release**; §9 remains accurate but is now a later-phase document, not the
plan of record. See §9.8 for the sequence.

**2026-08-08 — the running position against the checkpoint, after stream 1 tranche 1 (including slice B and
slice A1).** The measured axes after slice A1 are `Native` **4593 / 0 / 17** (unmoved for the whole tranche,
once slice B's 12 `AssertMql` baselines are re-based — slice A1 as shipped re-bases NONE, its one moved
baseline having been moved back by its fix wave — and for A1 the default axis was
compared as a SET at the slice base and at its tip, not merely by count) and `NativeOnly` **2501 / 2092 / 17**,
up from 2427/2166/17. **Cumulative stream-1 wins so far: 74** — re-summed from §2's tranche table
(`0 + 34 + 0 + 0 + 0 + 12 + 28`), not carried from a report. Of those, **62** are realized against a CITED
estimate of `44 + 36 + 56` = **136** for the three SIZED feature slices; slice B's **12** are excluded from
that ratio because they are a **re-attribution** of cases the ≈508 checkpoint already counts inside the 474
(§2 finding (2)) — **≈508 does not move, and 12 must not be added to it**. **A1's 28 against a CITED 56 is the
third consecutive under-delivery against sole-cause (A2 34/44, A5 0/36, A1 28/56), and its shortfall is
LOCATED rather than guessed:** the projection and sort columns (CITED 14 and 8) each delivered **zero**
specification cases, so both are correctness/breadth work rather than scoreboard work. A1 also adds a second
failure mode to finding (1)'s — the classifier can point at the **wrong decline site entirely**, and a plan
written against the site A1's own documents named would have delivered 0 of 28. The one sizing figure slice B
DOES change is A3's: its marginal yield once slice B has shipped is
`40 − 10` = **30**, not 40. **The checkpoint this is measured against is §9.8 step 3's, and it expects ≈508
after ALL of stream 1 with slice B, ≈400 without slice B, and ≈570 after streams 1 and 2 together.** State
plainly, because it has already been got wrong once in these docs and caught in review: **≈508 at the
post-stream-1 checkpoint is SUCCESS, not a shortfall.** The **588** is the superseded CITED figure, and judging
stream 1 against it would score a normal, expected outcome as an ~80-case miss and expand scope wrongly — do
not do it. **What tranche 1 adds to that picture is that ≈508 is now itself in question**, from a direction the
checkpoint did not anticipate: per §2 finding (1), ≈508 was built by summing sole-cause figures, and an
inner-node feature group's sole-cause figure counts cases that a fix RELABELS rather than closes (A5: 36
sole-cause, 0 converted). Every inner-node group in stream 1 inflates ≈508 by an amount nobody has measured.
**No revised projection is offered here on purpose** — re-deriving it is the checkpoint's own job, using §9.0's
method, and inventing a number now would replace one unmeasured figure with another. What is known: the base
term of §9.8's projection has moved **2427 → 2501**, so **2427 + 570 + 282 + 52 = 3331** holds only if stream
1's remaining contribution is `570 − 74` = **496**, and the 570 rests on the same sole-cause basis finding (1)
undermines. (That subtraction uses **74**, the full measured `NativeOnly` movement, precisely BECAUSE the base
term is a measured axis reading rather than a checkpoint estimate — slice B's 12 really did move the axis, even
though they must not be added to ≈508. Do not conflate the two: the axis moved by 74; the ESTIMATE moved by
`34 + 28` = 62 at most, and by 0 for slice B.) The bar (`0.80 × 4075 = 3260`), the 71-case margin, and slice B's load-bearing role (without it
**≤3257/4075 = ≤79.9%**, below the bar) are all unchanged by this tranche — and per §2 finding (2), slice B's
newly measured standalone yield of **12** is a **re-attribution of cases already inside the 474**, so it must
**not** be added to ≈508 or to ≤3257.

---

## 9. What must be done before driver-LINQ can be retired without regression

**2026-08-07 — this section is no longer the plan of record.** It was written when the plan was "reach parity,
retire driver-LINQ, merge"; that plan is withdrawn (§8). Everything below remains an accurate answer to its
own title — what full retirement of the driver-LINQ fallback actually requires — but retirement is now a
**separate, later-phase project**, scoped and scheduled by the owner independently of the merge this document
otherwise tracks. Read §9 as a forward-looking inventory for that later project, not as what happens next;
for what happens next, see §8's plan-of-record paragraph and §9.8's merge sequence.

*Added 2026-07-31 on the `229294f` two-sweep measurement. **FULLY RE-MEASURED 2026-08-07 at tip `99d74735`**
(EF-391's first half) and rewritten as-measured — every count below was re-derived at this tip, not carried
forward. This section is the answer to "what is left", and it is deliberately separate from §5 ("deferred
items"): §5 lists what the epic still wants; §9 lists what **blocks deleting the fallback**. The two are not
the same set — several §5 items are optional at cutover, and several §9 items appear nowhere in §5.*

> **Read this before trusting any other number in this file.** §7 is a `229294f` snapshot and every total in
> it is now stale — including the **2395 / 1742 / 651** triple that §7.2, §7.3 and (until this revision) §8
> quoted. The 2026-08-06 re-derivation of §7.2's largest row already found its LABEL 41% wrong; this revision
> re-derived **every** bucket the same way and found two further labels false outright (§9.1). Use §9's
> figures. §7's *method* (§7.4) is still correct and is what was re-run.

**The one-line answer, and it has changed.** Native still cannot replace driver-LINQ, but the binding
constraint is no longer the four "things the translator cannot express at all" this section used to name.
Measured by decline site, **the largest single lever is ordinary `MongoExpressionTranslator` expression
breadth** — the same ~20 features recurring in predicate, sort-key and projection-leaf position, ≈588 of the
1598 real gaps — and it has no slice, no ticket, and no place in §9.8. Joins remain the second-largest at 373.
And the *structural* dependency is unchanged and confirmed at this tip: **the EF9+ bulk
`ExecuteUpdate`/`ExecuteDelete` path does not merely share the bridge, it re-enters the fallback's own
translation entry point**, so "delete the fallback" is not even close to "delete the bridge" (§9.2).

### 9.0 How this was measured (so the next re-derivation reproduces it)

Method: §7.4's two-sweep subtraction, plus the spike's decline-site instrumentation
(`docs/superpowers/specs/2026-08-06-step3-projection-spike-findings.md` §Q2), extended.
In a throwaway worktree at `99d74735` (created, used and removed; `git worktree list` verified):

1. `MongoSelectDefinition.MarkNotNativelyRepresentable` gained `[CallerMemberName]`/`[CallerLineNumber]`/
   `[CallerFilePath]`, recording the **first** decline site and the de-duplicated **list** of all sites.
2. `NativeSlotPopulator` recorded, at the `Where` and `OrderBy` declines, the **smallest failing subtree** —
   found by re-invoking the translator on each sub-expression and descending until no child fails, *never*
   descending to a bare `ParameterExpression` (which is never translatable standalone and is the degenerate
   floor of the search — stopping above it is what makes the answer `o.Customer` rather than `o`).
3. A parallel probe classified each `NativeProjectionBinder` decline as `BARE` / `WRAPPED_NEW` /
   `WRAPPED_INIT`, plus the first leaf the *existing* translator cannot already resolve as a field
   (`FIELD_OK` / `VALUE_OK` / `NO_XLATE`) and that leaf's minimal failing node.
4. All of it was appended to the `NativeOnly` throw reasons, so the `.trx` carries per-test attribution.

Five instrumented sweeps were run — four `NativeOnly` (one per instrumentation round) and one `Native` —
**each preceded by a full `dotnet build`**, because a previous task in this slice measured stale binaries and
had to redo a whole round. **Every one reproduced the baseline exactly: `NativeOnly` 2427 passed / 2166 failed
/ 17 skipped, and the `Native` axis 4593 / 0 / 17 with an EMPTY failure list** (so no `Native`-mode `AssertMql`
baseline moved either) — the instrumentation is behaviour-preserving on both axes, and the (a)/(b)/(c)
partition came out **identical in all four** `NativeOnly` rounds. Both `MONGODB_URI` and `ATLAS_URI` were
unset, so an `mongodb/mongodb-atlas-local` container really ran the Atlas-gated tests (the 16 `VectorSearch`
failures below are real Atlas Search results, not skips). All instrumentation lived in the throwaway worktree;
nothing under `src/` in the main tree was touched.

### 9.1 Coverage — the tests that need driver-LINQ for correct *results*

**The partition of the 2166, classified `Assert.Throws` FIRST (its message quotes the inner exception; a naive
substring match over-counted by 149 in the previous round):**

| | Cases | Definition | How identified |
|---|---:|---|---|
| **(a)** | **1598** | **genuine coverage gaps** — the user gets an exception instead of data | 1580 where the raw `NativeTranslationNotSupportedException` escaped to a *data* assertion, **plus 18** where the test pins an *execution-time* exception (`TruncationException`, `FormatException`, `MongoCommandException`, `InvalidCastException`, `TargetInvocationException`) — i.e. driver-LINQ **runs** that query and native cannot express it at all |
| **(b)** | **518** | **fails in every mode** — unsupported in native *and* driver-LINQ, so retirement changes nothing | 488 where the test's own assertion is that the query throws and the expected type is a *translation* failure (`InvalidOperationException` 249, `ExpressionNotSupportedException` 145, `ArgumentException` 10, `NotSupportedException` 2, plus 82 `Assert.Contains` on exception-message text), **plus 30** where driver-LINQ executes but returns **wrong data** and the test pins the mismatch (`Assert.Throws<EqualException>` 28, `XunitException` 2 — `Select_Where_Navigation_Null_Deep`, EF-371) |
| **(c)** | **50** | **`AssertMql` baseline bookkeeping** | every one bottoms out in `TestMqlLoggerFactory.AssertBaseline` — 48 as an index-past-the-end `ArgumentOutOfRangeException`, 2 as a string diff |
| | **2166** | | |

> ~~**1742** tests need driver-LINQ for correct results.~~ **MEASURED 1598 at this tip.** The old figure was
> the sum of five *message* buckets (873 + 794 + 54 + 13 + 8) at a total of 2395; the drop is partly the two
> landed slices (VectorSearch, step 3a) and partly re-classification. Note the direction of travel is real but
> modest: the two slices between them moved (a) by roughly 144 cases.

**Two claims this section carried for weeks measured out FALSE. Corrected in place.**

1. ~~"The **66 `ArgumentOutOfRangeException`** rows … are described as a materialization/shaper gap surfaced
   only under `NativeOnly`. If any of them is really a native shaper bug rather than an expected decline, it
   belongs in §9.1, not here. Not re-investigated at this revision." (§9.3)~~ — **RE-INVESTIGATED AND
   ANSWERED: none of them.** The bucket is now **48**, and **48 of 48** have `TestMqlLoggerFactory.
   AssertBaseline` in the stack. They are `AssertMql` calls indexing past the end of the captured MQL list —
   the test's *query-level* assertion already passed. There is no shaper gap here at all; the label
   "a materialization / shaper gap" was simply wrong. All 48 are category (c).
2. ~~"**Composite-PK member access is not native at all** … Not in the table at all, because Northwind does not
   cover it — genuinely invisible to the counts above."~~ — **FALSE, and it is the fifth-largest item in the
   whole gap.** Northwind's `OrderDetail` has a composite key, and **116 measured (a) cases** decline on
   exactly `MongoExpressionTranslator.TryResolveMember`'s composite-PK guard, with the minimal failing node
   being `OrderDetail.OrderID` (106) or `OrderDetail.ProductID` (10). **112 of the 116 are inside the four
   reference-`Include` suites** (`NorthwindInclude` 32, `NorthwindStringInclude` 32,
   `NorthwindEFPropertyInclude` 32, `NorthwindIncludeNoTracking` 16), the `Include_references_multi_level` /
   `Include_multiple_references_*` families. So composite-PK resolution (`_id.<name>` dotted paths) is **on the
   reference-`Include` critical path**, not an isolated footnote. Sizing caveat, measured: only **12** of the
   116 are sole-cause — the other **104 also decline at the projection binder**, so fixing composite-PK alone
   turns 12 green, and 116 needs it *plus* the projection work.

**(a) = 1598, partitioned into disjoint owning work streams by first decline site.** "Sole-cause" is the count
whose query recorded exactly one decline site — the honest leverage figure for fixing that site alone. **Line
numbers are pristine-source at `99d74735`** (the instrumented worktree's own numbers were 2–6 lines higher;
re-derive rather than trusting these after any edit to those files).

| # | Owning work stream | Cases | Sole-cause | Decline site(s) |
|---|---|---:|---:|---|
| 1 | **Joins / cross-collection** — **deferred under the 2026-08-07 merge plan, EF-392** | **373** | 250 | `NativeSlotPopulator.PopulateNativeSlots:107`/`:118` where the failing node is a `TransparentIdentifier` member or an `EntityQueryRootExpression` (**157**); `TranslateSelect:280` (the projection binder) on the same two node kinds (**108**); the `$lookup` hard throw "Native pipeline does not support lookup for navigation …" (**54**); `TranslateSelect:230`, the reference-`Include` confirm-decline (**36**); `Join`/`GroupJoin` with `Route == Fallback` and no recorded site at all (**18**) |
| 2 | **`MongoExpressionTranslator` breadth — predicate + sort key** | **368** | 316 | `NativeSlotPopulator.PopulateNativeSlots:107`, the `Where` arm (**264** of that site's 507) and `:118`, the `OrderBy` arm (**104** of that site's 134) — both are `MongoExpressionTranslator.TryTranslate` / `TryTranslateField` returning false |
| 3 | **Projection long tail** | **290** | 230 | `TranslateSelect:280` → `NativeProjectionBinder.TryPopulateNativeProjection` — **step 3c** computed/`VALUE_OK` leaf **220** (ships in the merge plan's stream 1), **step 3b** entity leaf **52** (ships as stream 3), **step 3d** narrowed composition **18** (**deferred, EF-395**) |
| 4 | **GroupBy breadth** — **deferred under the 2026-08-07 merge plan, EF-393** | **130** | 50 | `TranslateSelect:197` (62), `TranslateGroupBy:1448` (58, **zero** sole-cause — it always co-occurs), `TranslateGroupBy:1441` (10) |
| 5 | **Composite-PK member access** — **deferred under the 2026-08-07 merge plan, EF-394** | **116** | 12 | `MongoExpressionTranslator.TryResolveMember` / `TryResolveOwnedFieldPath` composite-key guards |
| 6 | **`Distinct`** | **84** | 64 | `TranslateDistinct:1318` — `NativeGroupByBinder.TryBindDistinctFromProjection` declined |
| 7 | **Scalar-aggregate binder** | **82** | 82 | `BindAggregateOrFallback:1368` — `NativeCardinalityBinder.TryBindAggregate` declined. **82 of 82 sole-cause — the largest row in the table where every case turns green on that site alone** |
| 8 | **Set operations** | **42** | 42 | `TryTranslateSetOperation:2448`'s scope gate |
| 9 | **No binder at all** (`Contains`/`ElementAt`/`Last`/`Cast`/…) | **40** | 40 | `NativeSlotPopulator.PopulateNativeSlots:215`, the catch-all |
| 10 | **Post-terminal slot operator** | **34** | 22 | `NativeSlotPopulator.PopulateNativeSlots:94`, the `HasTerminalOperator` guard |
| 11 | **Non-constant regex (EF-247)** | **19** | 19 | `MongoQueryLanguageRenderer` constant-only regex term. *(Was 13; re-measured 19.)* |
| 12 | **`Not` over an unsupported subtree** — **deferred under the 2026-08-07 merge plan, EF-396** | **8** | 8 | `MongoQueryLanguageRenderer.RenderUnary` |
| 13 | Reducer binder / **EF-382** `VectorSearch` pre-filter / set-op + collection `Include` — the reducer binder and the set-op + collection `Include` items are **deferred, EF-397**; the `VectorSearch` pre-filter item keeps its existing EF-382 | **4** each | 4 each | `NativeSlotPopulator.PopulateNativeSlots:171` (reducer); `:204` → `NativeVectorSearchBinder.TryBind`; `TranslateSelect:245` |
| | **total** | **1598** | | |

**Row 2 and row 3 are ONE capability seen twice, and that is the most important thing in this section.**
Breaking rows 2 and 3 down by the *feature* the translator is missing shows the same short list recurring in
all three positions. Measured, across predicate / sort key / projection leaf:

| Feature | predicate | sort key | projection leaf | total |
|---|---:|---:|---:|---:|
| `Convert` / cast operand | 50 | 8 | 14 | **72** |
| a leaf the translator **already** resolves as a VALUE, needing only a synthetic alias (the reverted **tier 2**) | — | — | 54 | **54** |
| constructed value (tuple / anonymous / DTO / list) | 16 | 2 | 32 | **50** |
| `EF.Property` leaf | 38 | 6 | 4 | **48** |
| a bare constant / query parameter as the whole node | 30 | 10 | — | **40** |
| `Nullable.HasValue` / `.Value` | 10 | — | 28 | **38** |
| `Contains` over a client collection | 18 | 18 | — | **36** |
| entity equality (`o == someOrder`) | 34 | — | — | **34** |
| `Coalesce` (`??`) | 2 | 22 | 8 | **32** |
| `Add` (string concat / arithmetic) | 10 | 4 | 18 | **32** |
| other client / BCL method call | 8 | — | 22 | **30** |
| `Conditional` (`?:`) | 2 | 10 | 14 | **26** |
| unary `Negate` | — | — | 18 | **18** |
| other arithmetic / comparison operator | 12 | 4 | 2 | **18** |
| `Not` over a non-native operand | — | 18 | — | **18** |
| `Equals(...)` method | 16 | — | — | **16** |
| array literal | 2 | — | 8 | **10** |
| `GetType` / type test | 8 | — | — | **8** |
| other member access | — | 2 | 4 | **6** |
| `EF.Functions.Like` | 4 | — | — | **4** |
| | | | | **≈590** |

*(≈590 rather than exactly 588 because 2 `VALUE_OK` projection leaves are also joins cases and are counted in
both views; the disjoint work-stream table above is the authoritative partition.)* **A single
`MongoExpressionTranslator` breadth work stream therefore reaches ≈37% of the whole coverage gap.** Nothing
else in this document reaches half that. This is EF-391's substantive finding and it should drive §9.8.

**Where the projection bucket now stands, post-3a** (398 cases with the binder as *first* decline site; 631
touch it anywhere). By selector-body shape: **BARE 222, `WRAPPED_NEW` 156, `WRAPPED_INIT` 20** — i.e. a bare
body is *still* the majority, because 3a admitted only a **path-addressable** leaf and most bare bodies are
computed. By what they need: computed leaf 166, joins 108, **entity leaf 52** (step 3b — this is the 12
`VectorSearch` cases plus 40 others; the leaf presents as `IncludeExpression(<entity>)` or
`MemberInit(<entity>)`, *not* as a `StructuralTypeShaperExpression`), translatable-VALUE leaf 54 (tier 2),
and **18 where every leaf translates as a field and the binder still declines** — the two measured correctness
narrowings 3d owns (bare set-op operand, bare `Distinct`). The computed tail's own head: `Nullable.Value` 28,
unary `Negate` 16, `Conditional` 14, string concat 24, `??` 8.

**`VectorSearch` residual: 16, confirmed at this tip** (8 `VectorSearchMongoTest` + 8
`VectorSearchExactMongoTest`, all inside (a)). **12** are the entity leaf inside a projection (step 3b) —
measured signature `WRAPPED_NEW/NO_XLATE/.../IncludeExpression/Book` (8) and `.../MemberInit/Book` (4) — and
**4** are `VectorSearch_with_complex_pre_filter`, **EF-382**, declining at `NativeVectorSearchBinder.TryBind`
because the predicate translator cannot express `arrayField.Contains(constant)`. That last one is row 2's
work, not vector-specific.

**Still genuinely invisible to these counts** (Northwind does not cover them — and note that "composite-PK"
has just been removed from this list, having been on it wrongly):

- **Non-TPH `OfType`.**
- **The owned-data residuals in §5** (`OwnsOne`-hop dotted SCALAR read / **EF-390**, correlated element
  predicates, two-scope owned quantifiers, owned-collection intermediate hops in dotted paths, **EF-365**).
  Individually small; collectively the tail the nine owned-data slices were eating.
- **A set operation combined with an `Include`** — `IsPlainWholeEntitySelect` and `IsPlainProjectedSelect`
  both require zero lookups. Partly visible (4 cases at `TranslateSelect:245`), mostly not.

### 9.2 The structural blocker: the bulk path shares the bridge — CONFIRMED, and it is worse than stated

Re-verified at `99d74735`. `MongoEFToLinqTranslatingExpressionVisitor` — the EF→driver-LINQ bridge, ~1050
lines plus a ~726-line `LeftJoin` partial — is constructed at exactly three sites, **all** in
`MongoShapedQueryCompilingExpressionVisitor.cs`, and two of them are the bulk path:

| Site | Method | Region |
|---|---|---|
| `:1050` | `TranslateQuery<TEntity>` — the query fallback | all EF versions |
| `:1238` | `BuildFilter<TSource>` — lowers each bulk `Where` body to `Mql.Field` form | `#if !EF8` (lines 1162–1464) |
| `:1355` | `RenderSelfReferencingValue<TSource>` — lowers a self-referencing `SetProperty` value | `#if !EF8` |

**The escalation this revision measured, which §9.2 did not previously state.** `BuildIdDocumentQuery<TSource>`
(:1203) does **not** construct a bridge of its own — it calls **`TranslateQuery<TSource>`**, the fallback's own
entry point, and then consumes the result as
`executableQuery.Provider.CreateQuery<BsonDocument>(executableQuery.Query)`. So the bulk path depends on:

1. the bridge class (`BuildFilter`, `RenderSelfReferencingValue`);
2. **`TranslateQuery` itself** — the whole fallback translation entry point, including the native gate;
3. **`MongoExecutableQuery.Query` and `.Provider`** — the two *public* members §9.4 below calls "dead". They
   are not dead. They are load-bearing for `ExecuteUpdate`/`ExecuteDelete` on EF9 and EF10.

That the bulk path runs through the *native gate* is not theory: **24 `NorthwindBulkUpdatesMongoTest` cases
fail under `MONGODB_EF_NATIVE_ONLY=1`** (2 in (a), 22 exception-shape), which can only happen because the gate
sees those queries.

Consequence, restated with the correction: **retiring the query fallback retires neither the bridge nor
`MongoExecutableQuery`'s driver-LINQ surface.** Either the bulk filter/update translation is rewritten onto
`MongoExpressionTranslator`/`MongoQueryLanguageRenderer` first, or all three survive cutover as bulk-only
infrastructure. That is a real sub-project and it is still not on the SP scoreboard in §2.

No other consumer keeps the bridge alive. `VectorSearchStageBuilder` was extracted *verbatim from* the bridge
and no longer depends on it; `MongoClientWrapper.Execute` cleanly splits `NativePipeline` from
`Provider.CreateQuery(Query)`; `MongoLoggerExtensions.ExecutedMqlQuery` has a `Provider.LoggedStages` overload
*and* a `(CollectionNamespace, BsonDocument[])` overload the native path already uses. The only other
references are two unit tests of the static `DependenciesPrecede` helper.

**The EF-317 ruling is unchanged and still SETTLED (owner, 2026-07-31): EF-317 is essentially THROWAWAY. Build
what joins need; do not design around it.** Do not re-open. The distinction that ruling does *not* collapse
also stands: "throwaway" applies to the **`LeftJoin` partial**, not to the main bridge file, which is what the
bulk path uses.

### 9.3 Test-suite work at cutover — 568 re-baselines, and 16 that need a helper the repo already has

*Re-measured 2026-08-07. The previous figures (651 / "10 cases in 5 classes, 8 on EF10") are superseded; both
are corrected in place below rather than deleted, because the direction of the correction matters.*

- **568 tests differ only in failure shape** — ~~651~~ — split as **518** exception-shape (§9.1 category (b))
  plus **50** `AssertMql` baseline diffs (category (c)). At cutover each gets re-pointed at the native
  exception or re-baselined. Mechanical but large: schedule it, don't discover it.
- ~~"The 66 `ArgumentOutOfRangeException` rows deserve a second look before being filed as bookkeeping … If any
  of them is really a native shaper bug rather than an expected decline, it belongs in §9.1."~~ **DONE, and
  the answer is none.** 48 of 48 are `AssertMql` baseline-index failures (§9.1). This item is closed.
- ~~"**10 spec cases (5 classes) cannot be re-baselined by the usual instrument** … eight exist on the EF10
  axes."~~ **RE-MEASURED: 16 cases in 2 classes on EF10** — `NorthwindBulkUpdatesMongoTest` (14) and
  `NorthwindKeylessEntitiesQueryMongoTest` (2) — **and "cannot be re-baselined" overstates it.** Those two
  classes still use a local `AssertTranslationFailed` that hard-pins `Assert.ThrowsAsync<InvalidOperationException>`
  + `"could not be translated"`. **Thirteen other spec classes already route their shadow through
  `MongoSpecTestHelpers.AssertNativeTranslationFailedAsync`, which accepts the native exception** — which is
  precisely why only 16 of the ~838 `AssertTranslationFailed` references fail under `NativeOnly` at all. The
  cutover work here is *applying an existing pattern to two more classes*, not inventing an instrument.
  `EF_TEST_REWRITE_BASELINES` still does not rewrite exception assertions, so those 16 are hand edits.
- **The mode axis itself disappears.** `MONGODB_EF_NATIVE_ONLY=1` (`MongoTestStore.AddProviderOptions`)
  becomes vacuous, and ~24 functional `Native*` test files that self-parametrize over `MongoQueryMode` lose
  their `Native == DriverLinq` parity leg — today the primary oracle for a large amount of this work. **What
  replaces that oracle needs deciding before the fallback goes, not after.** The in-memory differential oracle
  (used by the `All`, `.Count` and filtered-count slices, where no driver oracle existed) is the obvious
  candidate and is already proven in this codebase. *This is the item on this list with the longest lead time
  and the least visible cost; it is not mechanical.*

### 9.4 Public API decisions — MEASURED against the release tags, and the answer is simpler than feared

*Re-checked 2026-08-07 against `v10.0.2` / `v9.1.2` / `v8.4.2` (the latest non-preview tag on each EF line,
from `gh release list`, not from `upstream/main`). **This project has twice got a break judgement wrong by
measuring on the branch and asserting of the release; this check was done tag-side.***

**The entire query-mode surface has never shipped.** `git grep -c QueryMode <tag> -- src/` returns **zero** at
all three tags, and `Infrastructure/MongoQueryMode.cs` does not exist at any of them. Every member §9.4
previously listed as "a breaking-change judgement" is an **addition made inside this unreleased cycle**:

| Member | At `v10.0.2` / `v9.1.2` / `v8.4.2` | Removing it at cutover |
|---|---|---|
| `public enum MongoQueryMode` | **absent** | not a break |
| `MongoDbContextOptionsBuilder.UseQueryMode(...)` | **absent** | not a break |
| `MongoOptionsExtension.QueryMode` / `WithQueryMode(...)`, and its contributions to `GetServiceProviderHashCode`, `ShouldUseSameServiceProvider`, `PopulateDebugInfo`'s `"Mongo:QueryMode"`, `LogFragment`'s `QueryMode=…` | **absent** | not a break — removal restores the released shape byte for byte |
| `MongoQueryCompilationContext`'s 3-arg primary constructor + `QueryMode` property | **absent** (v10.0.2 has the 2-arg `(dependencies, async)` form, which the branch *preserved* as a delegating overload) | not a break |
| `MongoQueryCompilationContextFactory` 2-arg constructor | **absent** (v10.0.2 has the 1-arg form, which the branch *preserved* with an explicit "do not delete, v10.0.2 compat" remark) | not a break |

So the fear recorded here — "each is a breaking-change judgement", ~8 API members — **does not survive
measurement**. There is exactly **one** genuine constraint left, and it points the other way:

- **`public record MongoExecutableQuery` DID ship, and its positional parameters are byte-identical at all
  three tags and at this tip** (`Query`, `Cardinality`, `Provider`, `CollectionNamespace`, `AdditionalState`;
  everything the native path added — `NativePipeline`, `Session`, `Streaming`, `OutputSerializer` — is
  `internal`). Changing those positional parameters *is* a source+binary break. **And per §9.2 they are not
  dead anyway:** `BuildIdDocumentQuery` consumes `Provider.CreateQuery<BsonDocument>(Query)` for the EF9+ bulk
  path. **The correct call is therefore: leave `MongoExecutableQuery` exactly as it is.** This corrects the
  previous entry, which framed it as the type whose dead members invite removal.

`NativeTranslationNotSupportedException` is `internal sealed`, so the exception users would start seeing is
not currently public. Decide whether it should be before it becomes the only failure mode — **this is now the
only open public-API question in this sub-section.**

### 9.5 Correctness debt that should not survive cutover

Cutover removes the safety net that currently makes some of these benign, so they change priority. Sorted by
category, because the category is what should drive the order. *(JIRA was unreachable from the analysis
environment on 2026-08-07, so the statuses below are **CITED from §6**, not re-verified against the tracker.)*

**Silent wrong data reachable under the DEFAULT `Native` mode — fix before anything else:**

- **EF-356** — mixed whole-entity + computed-arithmetic projection returns silently wrong values (`Score²`).
  Lives in exactly the mixed shaper step **3b** would widen, and 3b **carries a standing ruling that it must
  FIX EF-356, not pin it** (§9.8). It is also the reason 3b is not simply "the next projection slice".
- **EF-390** — a dotted owned-hop **scalar** leaf silently returns `null` (`Select(b => new { b.Home.City,
  b.Home.Notes })` loses `City` while the array beside it is correct). Pre-existing and byte-identical at step
  3a's base; pinned as *measured, not correct* by
  `Ef362OwnedHopArrayProjectionTests.Owned_hop_SCALAR_leaf_alongside_the_array_leaf_is_still_declined_and_still_loses_the_scalar`.
  Newly ticketed by `99d74735` (§5 bullet 4a said "no ticket has been filed" and is corrected there).
- **EF-380** — a `ThenInclude` nested under an **owned (embedded)** hop classifies as `Unclassifiable`, falls
  through to the root tiers and returns a **silently null navigation** under default `Native`. Measured in all
  three modes and byte-identical at EF-379's base, so a survivor, not a regression. The fall-through is
  reachable, measured not assumed (8 hits / 2 shapes functional, 28 hits / 7 shapes spec); other families
  arriving there are correctly root-scoped, so a blanket decline is not the fix.
- **EF-375** — two joins onto the same target entity type. Three symptom classes from three sites: a throw
  (bare same-typed pair), a **silently null navigation**, and **silently wrong values**
  (`TARGET-B|TARGET-B` for `TARGET-A|TARGET-B`). A fix addressing only the first site converts a throw into
  silent wrong data. `Employee.Include(Manager).Include(Mentor)` is enough, and same-typed siblings are
  guaranteed by construction on any self-referencing model. **This one is FIXED BY THE MERGE PLAN — it does
  not ship broken.** It is **stream 4** (§8, §9.8), pulled in on 2026-08-07 precisely because the Task-1
  audit found its trigger is an ordinary self-referencing model shape and so it fails the "uncommon" half of
  the bar for a *deferred* correctness gap. Its slice opens with a separability spike. *(Note added
  2026-08-07 on the final whole-phase review: §8 points at this section as the list of gaps that **ship**, so
  without this sentence — sitting beside EF-356's "3b must FIX it" — §9.5 read alone implied EF-375 ships
  broken. It does not.)*
- **EF-355** — filtered reference `SelectMany` can silently drop a `!= null` inner filter. **Latent** today
  (EF emits the nested, not folded, shape) but the mechanism sits in code the cutover makes load-bearing.

**Loud failures — bad, but they do not corrupt data:**

- **EF-381** — reinstate the transitive-hop-with-no-resolvable-intermediate decline. The shape it would catch
  fails LOUDLY today, at materialization. It is here because the *reason* it is absent is a missing
  discriminator: a transparent identifier is produced by an owned `SelectMany` as well as by a prior join, and
  the first cut hard-failed working first-join queries in `Native` **and** `DriverLinq` (measured: 3 correct
  rows at base, throw at the unfixed tip; 11 firings over 4 shapes). `InnerCollections.Count > 1` is **not** a
  usable gate — the self-referencing case is `Count == 1` too, which is EF-375's `IEntityType`-keyed blind
  spot again.
- **EF-376** — sibling `ThenInclude`s collapse into one `$lookup` (navigation-name-only aliases + `AddLookup`
  de-duping on `As`). Declines cleanly. Must come **after** EF-373's shared lookup resolver.
- **EF-377** — a chained `Join` whose first hop has no model navigation has no identity to scope the second
  hop under. Declines cleanly; **de-linked** from the EF-375/376 family (it has *no* key rather than a weak
  one) and its clean decline is a reasonable resting state.
- **EF-354** — whole-outer `SelectMany` crashes instead of declining cleanly.
- **EF-360, EF-365** — hard-fail in every mode where a graceful path is available. EF-365 is *measured* to
  become a working fallback if one guard is deleted; after cutover there is no fallback to become.
- **The interposed-operator family** (`Distinct`/`Take`/`Reverse`/`DefaultIfEmpty`/`Concat` between an
  owned-collection `Select` and a terminal operator) — recorded as a comment on the EF-322 epic rather than
  its own ticket. Hard-fails in every mode today.

~~**EF-379**~~ — **FIXED, joins slice 4 `9065acfc`.** Two facts from it generalize to everything above and are
kept: **row count does not discriminate a misrouted `$lookup`** (both pipelines return the same number of
rows; a regression test must assert the navigation VALUE, and `!= null` is not enough either, because EF's
identity fix-up can repair the object graph while the `$lookup` matched the wrong field), and the trigger was
an ordinary model shape. Its fix is proven by functional fixtures, not spec movement: **net spec delta ZERO**.

### 9.6 Housekeeping that is cheap now and annoying later

- **JIRA transitions deferred to merge:** EF-335 / EF-357 / EF-358 / EF-359 sit at `In Code Review` and must
  move to `Closed` when the stack lands. **EF-379** is fixed in code (`9065acfc`) but still at `Needs Triage`.
  **EF-378** should be closed as a duplicate of EF-375, and **EF-377** retitled toward "a navigation-less join
  hop has no identity to scope under". None of these has been done. *(JIRA was unreachable on 2026-08-07, so
  this list is CITED from §6 and not re-verified.)*
- **EF-391 is half done.** Its first half — re-deriving every bucket label by decline site — is this revision.
  Its second half is acting on the result: rows 2 and 5 of §9.1's table (translator breadth; composite-PK)
  have no slice, no ticket and no place in §9.8.
- **SP7 Phase 2** (§5) — reducer/aggregate streaming, collection-`Include` array streaming, reference-`Include`
  streaming, and deleting the now-unreachable `RawBsonDocument` branch + `BsonRowReader`. This is performance,
  not correctness: it does **not** block cutover, and should not be allowed to.

### 9.7 The honest summary — RE-RANKED 2026-08-07 on measurement

*This ranking is what §9.7 has looked like since 2026-07-31, and it was built from the old message buckets.
Re-derived by decline site, it is wrong in its first two places. Corrected in full; the old ordering is
recorded beneath so the change is visible.*

Ranked by how much of the 1598 each gates:

1. **`MongoExpressionTranslator` expression breadth — ≈588 (37%); re-measured as 580 by the 2026-08-07
   stream-1 spike** (`docs/superpowers/specs/2026-08-07-stream1-translator-breadth-spike.md`, −1.4%, disjoint
   against disjoint). **Its realistic yield is smaller than either headline: 474 sole-cause, of which 74 need
   the computed-sort slice; ≈508 after all of stream 1; ≈570 once stream 2 has also landed.** The single
   largest lever, and **it appears
   nowhere in the previous ranking, nowhere in §9.8, and has no ticket.** One work stream (casts, `??`, `?:`,
   `EF.Property`, `Nullable.Value`, string concat, entity equality, `Contains` over a client collection,
   constructed-value comparison, `Equals`, `GetType`, array literals) pays into predicates (368 incl. sort
   keys) *and* projection leaves (220) at once, because they are the same `TranslateNode` /
   `TryTranslateLeaf` surface. ~~It is also unusually well-suited to incremental delivery: each feature is
   independently testable and independently shippable.~~ **CORRECTED by the spike:** predicate and
   projection-leaf really are the same method (`TranslateOperand`), which is *stronger* than this claimed —
   but **sort key is a separate capability** that no translator arm can reach, so ~92 of the 104 sort-key
   cases need a **computed-sort slice** (`$set`/`$addFields` + `$sort` + `$unset` — IR, lowerer and renderer
   work). A plan written as "20 independently shippable translator features" **silently under-delivers**;
   the computed-sort slice must be scheduled as its own slice, early.
2. **Joins / cross-collection — 373 (23%). Deferred under the 2026-08-07 merge plan; tracked as EF-392.**
   Partly closed: single-level reference `Include` is native (EF-368), transitive-hop scoping and
   interleaved-operator positioning are fixed (EF-372, EF-373), and root-vs-transitive classification is fixed
   (EF-379). What remains: `ThenInclude` breadth, filtered `Include`, collection-of-collection, the general
   `Join`/`GroupJoin`/`LeftJoin` (no native form at all), and five open defects (EF-376/377, EF-380/381, plus
   EF-378 pending its duplicate closure). **EF-375 was pulled OUT of this list and into the merge plan as a
   targeted correctness fix on 2026-08-07** (the Task-1 audit found its throw fires on an ordinary
   self-referencing model shape, not an uncommon one) — see §3/§9.8 stream 4 of the merge plan. It is a
   defect fix, not a partial un-deferral of joins; the other 373 cases above stay deferred under EF-392.
3. **Projection long tail — 290 (18%).** 3c (computed/value leaf) 220, 3b (entity leaf) 52 — both ship in the
   merge plan (3c inside stream 1, 3b as stream 3) — 3d (narrowed composition) 18, **deferred as EF-395**.
   Note 3c overlaps item 1 almost entirely — doing item 1 largely *is* doing 3c.
4. **GroupBy breadth — 130 (8%), deferred as EF-393**, then **composite-PK — 116 (7%), deferred as EF-394**.
   Composite-PK jumped from "invisible" to fifth on measurement, and it is on the reference-`Include` path
   (§9.1), so it belongs beside item 2.
5. **The small, fully sole-cause items — cheap wins.** Scalar-aggregate binder 82, `Distinct` 84, set
   operations 42, no-binder operators 40, regex 19 (**EF-247**, `Blocked`), `Not` renderer 8 (**deferred as
   EF-396**) — **275 cases, of which 255 are sole-cause**, i.e. turn green on that one site with no second fix.
   Scalar-aggregate, set-ops, no-binder, regex and `Not` are 100% sole-cause; `Distinct` is 64 of 84. Highest
   green-per-line-changed in the plan. Scalar-aggregate/Distinct/set-ops/no-binder ship in the merge plan's
   stream 2; regex and `Not` are deferred (above) because regex is `Blocked` and `Not` has no ticket until now.
6. **The bulk-path bridge dependency (§9.2)** — small in code, still unscheduled, and now known to be broader
   than "share a class" (it re-enters `TranslateQuery` and needs `MongoExecutableQuery.Query`/`Provider`).
7. **Test re-baselining (568) and the oracle replacement (§9.3).** The re-baselining is mechanical. **The
   oracle replacement is not**, and it has the longest lead time of anything on this list.

*Superseded ordering, kept so the correction is legible:* ~~1. Joins + reference `Include`; 2. the
computed/client-eval projection long tail; 3. `VectorSearch` (done); 4. the bulk-path bridge; 5. non-native
`GroupBy`, then the small renderer/regex/`Contains`/composite-PK items; 6. test re-baselining + public-API
decisions.~~ Three things were wrong: translator breadth was never a line item at all, composite-PK was filed
as a footnote when it is 116 cases, and the public-API decisions turn out to be a non-issue (§9.4).

### 9.8 Execution order

**SUPERSEDED 2026-08-07, corrected in place rather than deleted — this whole section was written for the
cutover plan ("reach parity, retire driver-LINQ, merge"). That plan is withdrawn; the owner's replacement is
the merge plan in §8 and in
`docs/superpowers/specs/2026-08-07-native-query-merge-plan-design.md`. The steps below through 3a really did
ship, in this order, and their cases still count. What changed is everything BEYOND step 3: the cutover order
(kept below, struck through in spirit not in fact) put joins first of everything; the merge plan DEFERS joins
outright. That is a full reversal, and it follows from the same measurement this section already used to rank
things (§9.7) — the reversal is new policy applied to old arithmetic, not a new number.**

**STATUS 2026-08-07. Steps 1 (joins, seven slices), 2 (`VectorSearch`) and 3a (the bare-projection boundary)
have shipped. The order BEYOND step 3 is now re-derived on §9.7's corrected ranking and differs from what this
section said.**

| Step | Scope | Status |
|---|---|---|
| **1** | **Joins, as one work stream, starting with reference `Include`** — EF-366, EF-367, EF-370, EF-368, EF-372, EF-373, EF-379 | ✅ **seven slices shipped** (2026-08-03…05). Behind it: `ThenInclude` breadth, filtered `Include`, collection-of-collection, the general join, and EF-375/376/377/380/381 — **the remainder is DEFERRED under the merge plan and tracked as EF-392 (373 cases), except EF-375, pulled out 2026-08-07 as a targeted correctness fix — see the new stream-4 step below, not this step** |
| **2** | **`VectorSearch`** — a dedicated `MongoSelectDefinition.VectorSearch` slot emitted ahead of `AppendSelectOpStages` (first-ness structural), plus a deferred `Build`-time stage slot calling the driver's own stage builder per execution | ✅ **Done** 2026-08-06. `NativeOnly` `VectorSearch` failures **112 → 20 → 16**; `Native` unmoved; no baseline moved |
| **3a** | **The bare-projection boundary** — a bare selector body populates `Projection` and emits a native `$project` aliased by the leaf's own root-relative document path. Carried **EF-362**; the epic's first `BREAKING-CHANGES.md` entry for a native-routing flip | ✅ **Done** 2026-08-06. **74 wins, zero regressions** (`Native` 4593/0/17 unchanged; `NativeOnly` 2352/2241/17 → 2427/2166/17) |
| **3b** | **The entity leaf inside a projection** — `new { Book = e, Score = … }` and friends, on the **mixed shaper**. Holds the remaining **12** `VectorSearch` cases; **52** cases total (measured) | ⬜ Not started. **Ships as merge-plan stream 3. CARRIES A STANDING RULING: 3b must FIX EF-356, not pin it** — see below |
| **3c** | The computed / client-eval leaf long tail — **220** cases, and **it is the projection half of §9.7 item 1**, not a separate feature set | ⬜ Not started. **Ships inside merge-plan stream 1** (translator breadth — 3c overlaps it almost entirely) |
| **3d** | Composition after a bare projection — a bare projected **set-op operand** and a bare **`Distinct`**, both narrowed out of 3a by measured correctness guards. **18** cases | ⬜ **Deferred under the merge plan; tracked as EF-395** |

**3b's standing ruling, unchanged and still binding: 3b must FIX EF-356, not pin it.** EF-356 is a
silent-wrong-data bug (`new { c, Total = c.Age * c.Score }` returns `Score²`) living in exactly the mixed
shaper 3b would widen (§9.5). Shipping an entity-leaf widening over a known-broken shaper is not acceptable,
so the fix comes first within that slice.

**3a's residual, so 3b/3c/3d do not re-discover it:** a bare **computed** leaf (the reverted tier 2 — 54 cases
where the translator *already* produces a value and only a synthetic alias is missing), a bare **dotted** leaf
(which additionally needs the dotted-SCALAR read, **EF-390**), and the two narrowed compositions that are 3d's.

**~~STILL NOT FILED AS WORK ANYWHERE~~ — this flag is now the top of the order, not a footnote.** The
2026-08-06 re-derivation moved 363 of the old "non-entity projection" bucket out of it and named GroupBy,
predicates, scalar-aggregate, `OrderBy` keys, `Distinct` and post-terminal guards as their real owners. The
2026-08-07 re-derivation measured those owners over the *whole* gap rather than inside one message bucket, and
the result is §9.1's table.

**This is where the old "recommended order from step 3 onward" table used to sit. Quoted here rather than
deleted, because the correction that replaced it is the point:**

*Superseded order (the cutover plan): 4. `MongoExpressionTranslator` breadth (≈588); 5. 3b, the entity leaf
incl. the EF-356 fix (52); 6. the sole-cause cheap wins (255 green); 7. joins, resumed — `ThenInclude`
breadth, filtered `Include`, collection-of-collection, the general join, with composite-PK resolution as its
first task (373 + 116); 8. GroupBy breadth (130); 9. the bulk-path rewrite (§9.2); 10. correctness debt (§9.5)
not already forced; 11. test re-baselining (568) + the oracle replacement (§9.3).*

**That order is superseded 2026-08-07 by the owner's merge plan: merge at ~80% coverage with the driver path
shipping, not at parity with it retired** (full detail:
`docs/superpowers/specs/2026-08-07-native-query-merge-plan-design.md` §9). Two changes fall out of the
withdrawal, stated plainly so neither is missed:

- **Old steps 7 and 8 — joins-resumed (373) and GroupBy breadth (130) — are DEFERRED, not scheduled.** This
  reverses step 1 at the very top of this section, which put joins first of everything the epic had left. The
  reversal is not a change of opinion, it is the arithmetic in §9.7: the four streams below reach ~~**82.2%**~~
  **81.7%** (corrected on the final whole-phase review from the stream-1 spike; see the sequence below) —
  past the 80% merge bar — without touching joins or GroupBy at all. Tracked: joins **EF-392** (373 cases),
  GroupBy **EF-393** (130 cases). Composite-PK, previously bundled into old step 7 as "its first task", is
  decoupled from joins and deferred on its own terms: **EF-394** (116 cases, but only **12** sole-cause on its
  own site — §9.1 row 8 — so its real yield without the projection work is much smaller than 116).
- **A new stream is added that the cutover order never had: stream 4, EF-375.** Pulled into the plan
  2026-08-07 after the Task-1 ticket audit found its throw fires on `Employee.Include(Manager).Include
  (Mentor)` — an ordinary self-referencing model shape, not an uncommon one, so it fails the "uncommon" half of
  the bar for a *deferred* correctness gap (§9.5) and has to be fixed instead. It is a **targeted defect fix,
  not a partial un-deferral of joins**: its own location is already known (the agreement check in
  `TryResolveIntermediateLookupPrefix` carries a `TODO(EF-375)`), and the other 373 joins cases stay deferred
  under EF-392 regardless of what this stream finds. Its slice opens with a spike that must confirm the fix is
  actually separable from the joins machinery — **UNVERIFIED**, and a finding to bring back rather than push
  through if it is not. It adds correctness, not coverage: its cases sit inside the already-deferred joins
  bucket, so **the projected total does not move** (this read "**82.2% does not move**"; the projection is
  **81.7%** on the spike's measurement — the claim that stream 4 moves it by zero is unaffected).

**The merge-plan sequence, replacing the superseded table above:**

1. **File the deferral tickets** (§4, §5, §8 above) — a merge-bar item ("good tracking"), and cheapest done
   first: **EF-392** (joins, 373), **EF-393** (GroupBy, 130), **EF-394** (composite-PK, 116), **EF-395** (slice
   3d, 18), **EF-396** (`Not` over an unsupported subtree, 8), **EF-397** (residual single-shape gaps — the
   reducer binder and set-op-with-collection-`Include` items, 8; **not** the `VectorSearch` pre-filter item,
   which keeps its existing **EF-382**, 4 — §9.1 row 13); **EF-247** (non-constant regex, 19) already exists —
   JIRA status `Blocked`, check what it is blocked on
   before scheduling.
2. **Stream 1 — translator breadth**, as several slices split by feature group. ~~(**588** cases: casts 72,
   tuple/anonymous comparison 50, `EF.Property` 48, bare constant/parameter 40, `Nullable.Value` 38,
   client-collection `Contains` 36, entity equality 34, `??` 32, string concat 32, `?:` 26, and a tail). Pays
   into predicates, sort keys **and** 3c simultaneously, because they are the same `TranslateNode` /
   `TryTranslateLeaf` surface; each feature ships independently.~~
   **CORRECTED on the final whole-phase review — those figures are the pre-spike CITED ones and five of them
   moved; and "each feature ships independently" is exactly the framing the spike says will silently
   under-deliver.** MEASURED, `docs/superpowers/specs/2026-08-07-stream1-translator-breadth-spike.md` §3:
   **580** cases, not 588 — casts 72, VALUE_OK/reverted-tier-2 54, `EF.Property` **50** (was 48),
   constructed value **42** (was 50), bare constant/parameter 40, `Nullable.Value` 38, client-collection
   `Contains` 36, `??` 32, string concat 32, entity equality **30** (was 34), other client/BCL call 30, `?:`
   26, other arithmetic/comparison **22** (was 18), `Negate` 18, `Not` 18, `Equals()` 16, `GetType` 8, array
   literal **6** (was 10), other member access 6, `EF.Functions.Like` 4. The five bolded moves net −10, which
   is the whole 590→580 delta. **Sole-cause is 474 of the 580.**
   Predicate and projection-leaf do pay in together — more strongly than this step claimed, since they are
   literally the same method (`TranslateOperand`) — **but sort key is a SEPARATE CAPABILITY.** ~92 of the 104
   sort-key cases need a **computed-sort slice ("slice B")**: `$set`/`$addFields` + `$sort` + `$unset`, i.e.
   IR, lowerer and renderer work, not a translator arm. ~~It **delivers nothing on its own**~~ — **CORRECTED
   2026-08-08 by the slice-B spike (`docs/superpowers/specs/2026-08-08-computed-sort-key-spike.md` §4), which
   MEASURED it delivering 12** (10 A3 bare-constant/parameter + 2 arithmetic, `10 + 2` = 12), invisible to a
   raw pass/fail count because slice B re-bases the very `AssertMql` baselines of the tests it converts.
   **This is a RE-ATTRIBUTION, not an addition: all 12 are already inside the 474, so ≈508 and ≤3257 do not
   move and the 12 must not be added to them.** The one figure it does change is a sequencing one: **if slice B
   ships first, A3's marginal yield is `40 − 10` = 30, not the 40 listed above.** The rest stands —
   **74 of the 474
   sole-cause cases depend on it** (spread across seven groups — A1, A3, A6, A9, A11, A12, A13), and without
   it the whole plan lands **≤3257/4075 = ≤79.9%, below the bar**. **Schedule it early, and do not plan this
   stream as 20 independently-shippable features.**

   **A NEW OBLIGATION ON THE A SLICES, added 2026-08-08 from the slice-B spike §5.2 — nothing named it before,
   and missing it ships a slice with its sort column silently dead.** `MongoAggregationExpressionRenderer.
   CanRender` admits field/element refs, constants/parameters, binary operators over 13 listed operators and the
   two size nodes — **not** `MongoInExpression`, `MongoRegexExpression`, `MongoElemMatchExpression` or
   `MongoUnaryExpression` (READ). A `$set` body is an aggregation expression, so a node kind that exists only in
   the query dialect can serve a predicate but **can never serve a computed sort key**. The stream-1 spike's §7
   already imposes this on slices introducing a **new node kind**, covering **A9** (10) and **A12** (22) —
   `10 + 22` = 32. It does **not** cover **A6** (18) or **A13** (18), whose node kinds already exist, so
   `18 + 18` = **36 cases carry a genuinely unnamed aggregation-arm obligation** and at least `36 + 32` = **68
   of the 92** need one in total (a floor: A7 and possibly A11 add more).

   **STATUS 2026-08-08: tranche 1 of this stream has shipped** — slice 0 (`EF-398`), **A2** (`EF-399`, 34 wins
   MEASURED) and **A5** (`EF-400`, 0 wins MEASURED), plus the slice-B spike (`EF-401`); see §2. The
   measured slice-B-independent tranche is `A1 + A2 + A4 + A5` = **158 SLICE-B-INDEPENDENT** (spike §5.1) — the
   label is corrected here: **sole-cause** for those four is `56 + 44 + 28 + 36` = **164**, and 158 is that
   figure less the 6 of A1's 56 that need slice B — of which this
   tranche shipped `A2 + A5` = `44 + 36` = **80** as SIZED — and realized **34** of it. What is left of that
   tranche is **A1** (casts, 56 sole-cause, of which 6 need slice B, so `56 − 6` = 50 slice-B-independent — the
   highest single yield, and the one whose narrowing guard must **not** simply be relaxed) and **A4** (the
   reverted tier 2, 28 sole-cause, which must not be re-attempted before the late-fallback path can emit
   `$ifNull` itself — the step-3a note in `Query/AGENTS.md`): `50 + 28` = **78**, which is `158 − 80`.
3. **Checkpoint: re-measure**, using §9.0's method, before committing to the rest. "Sole-cause" is a leverage
   proxy, not a guarantee — the lowerer or renderer can still fail once a gate opens. ~~If stream 1's yield
   comes in materially under 588, that is the moment to pull joins or GroupBy back in, not after stream 2 has
   also under-delivered; the ~90-case margin above 80% absorbs a small shortfall, not a large one.~~
   **CORRECTED on the final whole-phase review — that trigger is verbatim the one the merge-plan spec
   explicitly corrects (its §7), and the correction never propagated here. Reading a ~508 result against 588
   would score a normal, expected outcome as an 80-case shortfall and expand scope wrongly.** The trigger, as
   it stands:
   - expect **≈508 after all of stream 1 with slice B** (474 sole-cause + 34 that close within stream 1 —
     32 needing a second stream-1 feature, 2 at the `ThenBy` arm), and **≈400 without slice B** (the
     sole-cause tranche only; the slice-B exposure of the 34 was not measured — UNVERIFIED);
   - expect **≈570 after streams 1 and 2 together** (474 + 34 + 62, the 62 being set ops 32, `Distinct` 26,
     scalar aggregate 4 — they are stream 1's cases but wait on stream 2);
   - **pull deferred work back in only if the POST-STREAM-2 figure lands materially under ≈570.** Not on the
     post-stream-1 figure, which is structurally incomplete by those 62.
   All three are upper bounds. And the margin available to absorb a shortfall is **71** cases
   (3331 − 3260), **not ~90** — that figure was computed from the superseded 3349.

   **AMENDED 2026-08-08 by stream 1 tranche 1 (§2 finding (1)) — the ≈508 above is now itself in question, and
   this checkpoint is the thing that has to answer it.** ≈508 is a SUM of per-group sole-cause figures, and the
   spike's sole-cause classifier is a minimal-failing-subtree search: for a group whose feature is an INNER node
   it labels cases that a fix RELABELS into another group rather than closing. MEASURED: **A5 sized 36
   sole-cause and converted 0**; A2 sized 44 and converted 34; realized so far `34 + 0` = **34 of `44 + 36`
   = 80**. So the sole-cause partition is **not stable under fixes** and its rows are **not additive**, and ≈508
   is inflated by every inner-node group's count by an amount **nobody has measured (UNVERIFIED)**. Two
   instructions follow. First, **this checkpoint must re-derive its own expectation, not just its result** — a
   measured yield below ≈508 is not evidence of under-delivery until the expectation has been rebuilt on a basis
   that separates "shapes present" from "cases that turn green". Second, **do not add slice B's newly measured
   standalone 12 to ≈508**: per §2 finding (2) those 12 are already inside the 474.
4. **Stream 3 — slice 3b** (fixes EF-356 rather than pinning it — the standing ruling above; **52** cases),
   **then stream 4 — EF-375**, spike first, to answer the separability question before committing to the fix.
5. **Stream 2 — the sole-cause tranche** (**282** cases, 250 of them sole-cause: scalar-aggregate binder 82
   (82 sole), `Distinct` 84 (64 sole), set operations 42 (42 sole), no-binder operators 40 (40 sole),
   post-terminal guards 34 (22 sole)). Highest green-per-line-changed in the plan.
6. **Architecture record** (§5) — the three debt items (the bulk-path/bridge coupling, the parity-oracle
   replacement, `ProjectionAliasTier.Synthetic`), decided and recorded, not silently carried. **Under the
   merge plan none of these blocks merge** — the driver path ships, so the bridge stays and the oracle is not
   being retired yet (§9.2, §9.3) — but each still needs an owner and a decision on record, not silence.
7. **Final measurement, status-doc update, merge.**

Streams 2 and 3 are independent of each other; 3b is placed before stream 2 only so the silent-wrong-data
EF-356 fix lands earlier. ~~**Total: 588 + 282 + 52 = +922 → 3349/4075 = 82.2%**~~ **CORRECTED on the final
whole-phase review — that sum used stream 1's CITED headline (588) where the number to plan against is its
MEASURED realistic yield. Total: 570 + 282 + 52 = +904 → 2427 + 904 = 3331/4075 = 81.7%** (stream 4
contributes correctness, not cases — see §8). The **570** is stream 1's bucket *after stream 2 has also
landed*, per the checkpoint in step 3; sequencing stream 2 last (step 5) is what makes 570 the right term to
sum, but it also means the post-stream-1 checkpoint sees ≈508, not 570. Bar: `0.80 × 4075 = 3260`, margin
**71**. Without slice B: **≤3257 = ≤79.9%**, below the bar.

**The owned-data work stream is still not on this list.** Nine slices landed and it is *not* on the merge
critical path (nor was it on the cutover critical path this section used to track) — the spec number was flat
across five consecutive tips, structurally, because Northwind has no owned collections. Do not default back to
it because the machinery is warm.

**Where each gap is gated, by decline site** — re-derived 2026-08-07 with counts attached, so the table sizes
as well as locates. **The caution that has always accompanied it still applies and now has evidence: a row
being "a single site" means the gate is in one place, not that the job is small.** The `sole-cause` column is
the honest leverage figure — how many cases that site's removal turns green *by itself*.

| # | Gap | Cases | Sole-cause | The gate |
|---|---|---:|---:|---|
| 1 | Predicate breadth | 368 (incl. sort keys) | 316 | `NativeSlotPopulator.PopulateNativeSlots`'s `Where` and `OrderBy` arms → `MongoExpressionTranslator.TryTranslate` / `TryTranslateField` |
| 2 | Projection: bare-computed, entity-ref and mixed leaves | 290 | 230 | `NativeProjectionBinder.TryPopulateNativeProjection`. **The bare-scalar half was OPENED by 3a**; what is left is the entity-ref/mixed half (3b), the bare computed leaf (tier 2), and the two narrowed compositions (3d). One site, now **three** decisions; the read side has a **fifth** consumer of the alias carrier (the late-fallback strip in `MongoShapedQueryCompilingExpressionVisitor`) that any widening must be checked against — missing it is silent |
| 3 | Computed long tail (the translator itself) | (inside 1 and 2) | — | `MongoExpressionTranslator.TranslateNode`'s final `return null`, `TranslateOperand`'s cast guard, `NativeProjectionBinder.TryTranslateLeaf`'s final `return null` |
| 4 | Joins — **deferred, EF-392** | 373 | 250 | `NativeSlotPopulator`'s catch-all + `MongoSelectLowerer`'s join-coverage guard + `TranslateSelect`'s reference-`Include` confirm-decline + the `$lookup` hard throw. **GroupBy+Join is the one *hard* decline** (throws under `Native` too, because the driver fallback returns silently-empty joins) |
| 5 | `VectorSearch` | 4 residual (EF-382) | 4 | ✅ **OPENED 2026-08-06 — this row is HISTORY, kept because it records the hazard the fix is shaped around.** There were **TWO independent gates, not one** (measured by mutation): `NativeSlotPopulator`'s catch-all fired first, so `Route` was already `Fallback` before `ContainsVectorSearch` was consulted, and **opening both WITHOUT emitting the stage returns silently wrong data** (correct row count, insertion order, no exception). The fix collapses the two into ONE FACT READ TWICE: an explicit `call.IsVectorSearch()` branch that either binds `Select.VectorSearch` or declines (no third exit), and `hasUnboundVectorSearch = ContainsVectorSearch(CapturedExpression) && Select.VectorSearch is null`. The dangerous state now needs two contradictory conditions. Residual: a `preFilter` outside the native predicate set (**EF-382**), or a non-parameter/non-constant vector/limit/options argument |
| 6 | GroupBy breadth — **deferred, EF-393** | 130 | 50 | five sites: computed keys and computed accumulator operands in `NativeGroupByBinder`, element/result selectors in `TranslateGroupBy`, bare `IGrouping`, and the post-group guards |
| 7 | `Contains` / `ElementAt` / `Last` / `Cast` | 40 | 40 | no binder at all — `NativeSlotPopulator`'s catch-all |
| 8 | Composite-PK member access — **deferred, EF-394** | 116 | **12** | `MongoExpressionTranslator.TryResolveMember` / `TryResolveOwnedFieldPath`. **Sizing caveat: 104 of the 116 also decline at the projection binder**, so this site alone is worth 12 |
| 9 | Parameterized `StartsWith`/`EndsWith`/`Contains` | 19 | 19 | `MongoQueryLanguageRenderer`'s constant-only regex term (**EF-247**, JIRA status `Blocked` — check what it is blocked on before scheduling); also unblocks `!(Count > @param)` via the negator |
| 10 | Scalar-aggregate binder | 82 | **82** | `BindAggregateOrFallback` → `NativeCardinalityBinder.TryBindAggregate`. **82 of 82 sole-cause** (as are rows 7, 9, 12 and projection step 3b — but this is the biggest of them) |
| 11 | `Distinct` | 84 | 64 | `TranslateDistinct` → `NativeGroupByBinder.TryBindDistinctFromProjection` |
| 12 | Set operations | 42 | 42 | `TryTranslateSetOperation`'s scope gate |
| 13 | Post-terminal slot operator | 34 | 22 | `NativeSlotPopulator`'s `HasTerminalOperator` guard |
| 14 | Correlated element predicates in `Any`/`All`/`Count(pred)` | not spec-visible | — | `ReferencesEnclosingScope`; needs `$expr`+`$filter`/`$anyElementTrue` for the quantifiers, a two-scope element translator for the count |
