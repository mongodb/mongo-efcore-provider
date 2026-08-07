# EF-401 (stream 1, slice B) — the computed-sort-key spike

*Run 2026-08-07 in two throwaway worktrees at `4adafc2c` — one carrying the `$set` prototype (§3–§7), one
PRISTINE and carrying only an instrumentation hook (§5.1.1). Both created, used and removed; `git worktree list`
verified before and after — the three `.claude/worktrees/agent-*` worktrees belong to other sessions and were
neither created nor touched by this session. The main tree finished with only this file added. Inputs:
`docs/superpowers/specs/2026-08-07-stream1-translator-breadth-spike.md` §4.3 and §7 (the question, and the 92);
`docs/superpowers/specs/2026-08-07-native-query-merge-plan-design.md` §3 and §7 (the checkpoint the answer
feeds); `MongoPipelineFactory.RenderSort`, `MongoSelectLowerer`, `NativeSlotPopulator`,
`MongoStreamingEntityMaterializerRewriter`, `MongoProjectionBindingRemovingExpressionVisitor`,
`Stages/MongoVectorSearchScoreStage.cs`.*

**Tagging convention, applied strictly.** Every claim below is one of:
**MEASURED** (produced by a run this session; the method is §9 and the numbers are reproducible) ·
**READ** (established by reading source at `4adafc2c`; no execution) ·
**INFERRED** (drawn from MEASURED/READ facts, not itself observed) ·
**UNVERIFIED** (not established — said so explicitly).

**Trap compliance, stated up front.** (a) Every routing claim is a `MongoQueryMode.NativeOnly` run that
succeeds or throws — never an MQL shape, which cannot prove a query went native. (b) **Every claim that
depends on the prototype was A/B-controlled**: the prototype is behind an env gate (`MONGODB_EF_SPIKE_B=0`
disables the populator fall-through), and the same test binary was run both ways. With the gate off, **12 of the
28 probe cases go red** — every one of the twelve shaper/`ThenBy` shapes of §3 and §6.1 — so nothing below is an
artifact of a change that was not actually live. (The other 16 are the explain probe, which builds its pipelines
by hand, and the 15 per-group rows of §5, which report rather than assert.) (c) Every
count-based claim was cross-checked against the failure MESSAGE, not the count alone; §4 exists because the
count alone said the opposite of the truth. (d) `MONGODB_URI` and `ATLAS_URI` were both unset, so TestContainers
booted its own `mongodb/mongodb-atlas-local`.

---

## Headline — seven findings (numbered 1–6, with 4b), in order of how much each changes slice B's plan

1. **A synthetic `$set` sort field survives every shaper, untouched. The spike's central UNVERIFIED is closed
   GREEN.** All five shapes in the brief's table — whole entity streaming, whole entity DOM (TPH), a projection
   after the sort, paging after the sort, and a tracking query — return the right rows in the right order with
   every mapped scalar intact, under `NativeOnly`. **MEASURED, by execution, per shaper**, with the streaming
   premise (`StreamingEligibility.IsEligible`) and the DOM premise (`!IsEligible` + `GetDirectlyDerivedTypes()`)
   each asserted rather than assumed. §3.

2. **The `$unset` is NOT required by either shaper — and should be emitted anyway, for a reason that has
   nothing to do with shapers.** With the `$unset` suppressed, all four re-run shapes still pass, including the
   tracking round-trip (the synthetic element is not written back on `SaveChanges`). **MEASURED.** *(Four of the
   five: S4 paging was not re-run without the `$unset`. That is immaterial to the claim — paging is not a shaper,
   it is a `$skip`/`$limit` pair that never reads the document — but the gap is stated rather than left silent.)*
   But a
   whole-document `$$ROOT` operation downstream of the sort — a `Union` dedup, an `Intersect`/`Except` source
   tag — would fold the synthetic element into the comparison key, and a set-op operand is allowed to carry a
   sort. Keep the stage; the justification is set-op hygiene, not materialization. §3.3, §7. (The set-op
   hazard is INFERRED from the rendering, not executed.)

3. **Slice B does NOT deliver zero on its own. It delivers 12 specification cases — and the raw pass/fail count
   says 0, because slice B re-bases the very MQL baselines of the tests it converts.** MEASURED, by message
   transition across a four-run spec A/B: on the `MONGODB_EF_NATIVE_ONLY=1` axis the counts are **2461/2132/17
   before and after, byte-identical**, and yet **exactly 12 cases move from
   `NativeTranslationNotSupportedException: "Query is not natively representable"` to an `AssertMql` baseline
   mismatch** — i.e. they now go native, with correct data (the base result assertion runs before `AssertMql`),
   and only a stale committed baseline keeps them red. This is the *same* both-axes trap already recorded in
   `Query/AGENTS.md` for the owned-collection `All` slice. §4.

4. **The remaining ≈80 of the 92 are blocked by the translator at the shared `TranslateOperand` entry point, so
   slice B genuinely is the multiplier the plan says it is — but at least 36 of them ride on an obligation NO
   existing document names.** With slice B in place a computed sort key is built by `TryTranslateValue` and
   rendered by `MongoAggregationExpressionRenderer`, whose `CanRender` admits only field/element refs,
   constants/parameters, binary operators over 13 listed operators, and the two size nodes — **not**
   `MongoInExpression`, `MongoRegexExpression`, `MongoElemMatchExpression` or `MongoUnaryExpression`. A
   predicate-only slice can ship a node kind that lives purely in the query dialect; **a sort key cannot** —
   `$set` is an aggregation-expression context. The stream-1 spike §7 already imposes this on slices that
   introduce a **new node kind** (so A9's 10 and A12's 22 are covered), but **A6 (18) and A13 (18) introduce no
   new node kind** — `MongoInExpression` and `MongoUnaryExpression` already exist — so they fall outside that
   note entirely and would ship with their sort columns silently dead. **At least 36 genuinely unnamed; at least
   68 needing an aggregation arm in total.** READ + MEASURED. §5.2, §7.

4b. **And "needs its A slice" understates it for two groups: the sort key's OPERANDS are often unsupported too.**
   Instrumenting the pristine populator across the whole spec suite (692 declining sort-key occurrences, 64
   distinct expressions) shows **all 18 `Not` keys are `Not(<param>.Contains(…))`** — needing A13 *and* A6 *and*
   slice B — and **50 of 52 `?:` keys** carry a method-call test, a transparent identifier or a `Convert`
   in the test. `??` is the one
   large group measured clean (38 of 38 at the root over supported operands). MEASURED. §5.1.1.

5. **§4.3's "ambiguous 6" stay ambiguous: the 92–98 range is UNCHANGED.** A `Convert`-over-a-bare-member sort
   key (`(double)x.A`, `(int)x.D`) is already native today with no `$set` at all — `TryTranslateField` calls
   `Unwrap`, which strips any `Convert` unconditionally (MEASURED, both A/B arms). But that shape **never occurs
   in the suite's declining population**: instrumenting the pristine populator over the whole spec suite finds
   18 declining `Convert`-rooted sort keys, all of them `Convert` over a **query parameter** (10) or over a
   **transparent identifier** (8) — not one over a bare mapped member. So the probe's representative was
   unrepresentative of the cases being sized, and the narrowing to 92–94 that the first version of this document
   claimed is **WITHDRAWN**. §5.1.1, §10. *(This citation read "§5.2" until a review caught it: the 18-declining-
   `Convert` measurement is §5.1.1's table row, not §5.2, which is entirely about `CanRender` obligations.)* That same `Unwrap` raises a separate, pre-existing question about
   narrowing casts in sort position — §8, UNVERIFIED, not a slice-B concern.

6. **A computed sort key is a COLLSCAN, and — the part that is not obvious — merely emitting a `$set` before a
   `$sort` destroys index-backed sorting even for a sort key that is a plain field path.** MEASURED with a
   `queryPlanner` explain over 400 documents with indexes on both sorted fields: a plain `{$sort: {A: 1}}` is
   `IXSCAN A_1`; the identical `{$sort: {A: 1}}` preceded by an unrelated `$set` is `COLLSCAN`. So a MIXED sort
   does *not* keep its field key index-usable, and the design must not emit the `$set` when no key is computed.
   A `$match` ahead of the sort still uses its index normally. §6.

---

## 1. The question, and what was already established

The stream-1 spike (§4.3, READ) established that `MongoPipelineFactory.RenderSort` hard-throws for any
`MongoOrdering.KeySelector` that is not a `MongoFieldExpression`, because MQL `$sort` accepts field paths only,
while the IR is already general (`KeySelector` is typed `MongoExpression`); and that `NativeSlotPopulator`'s
`OrderBy`/`ThenBy` arms call `TryTranslateField` and mark the query non-native when it returns false. None of
that is re-derived here.

Its §9 left one blocking UNVERIFIED: *whether a synthetic `$set` sort field survives the whole-entity DOM and
streaming shapers untouched.* Answering it is this spike's job, because slice B cannot be designed without it
and the merge plan's ≈508 post-stream-1 checkpoint rests on slice B converting the 92.

---

## 2. The emission site (Step 2) — three candidates, one rejected outright

`MongoSelectLowerer.AppendSelectOpStages` walks `Select.PipelineOps` and emits each op **verbatim, in arrival
order** (EF-347). A computed sort key needs `$set` → `$sort` → `$unset` **adjacent and correctly ordered inside
that sequence**, so it cannot be appended anywhere else in `Lower`. READ.

| candidate | verdict | cost |
|---|---|---|
| **(a) In the `MongoSortOp` arm of `AppendSelectOpStages`** | **RECOMMENDED** — what the prototype does | Two new typed stages + two renderer arms + one `RenderSort` arm. Adjacency and ordering are structural: the three stages are produced together, at the one site, and cannot be separated. **One edit covers three call sites for free** — the outer query's `PipelineOps`, a set-op operand's `PipelineOps`, and the post-set-op `TrailingOps` all route through this same shared helper (READ). |
| **(b) The populator records extra ops** | **REJECT** | It breaks the EF-347 merge rules. `StartOrReplaceSort` replaces the tail op *if it is a `MongoSortOp`*, and `AppendThenBy` extends the tail sort; with `$set`/`$unset` ops bracketing the sort the tail is the `$unset`, so both merge methods silently stop matching. Fixing that means changing the arrival-order IR contract itself. READ. |
| **(c) A composite stage** — `MongoSortStage` carries the computed keys and the factory expands it to three documents | Viable variant | No new stage types, and there is precedent: `MongoPipelineFactory.Create` already expands one stage into several via `template.AddRange` for `MongoUnionWithStage`/`MongoSetDifferenceStage` (READ). Concentrates emission in the renderer. Costs a shape change to `MongoSortStage`, which every existing consumer and unit test touches. The lowerer still has to allocate the synthetic names and rewrite the ordering keys either way, so the responsibility split is unchanged. |

**`Stages/MongoVectorSearchScoreStage.cs` is ANALOGOUS, not reusable.** READ: it is a payload-free MARKER whose
whole point is that `MongoPipelineFactory` renders it to a *fixed* `{"$addFields": {"__score": {"$meta":
"vectorSearchScore"}}}`, precisely so no BSON enters the lowerer. A computed sort key needs a payload (name →
`MongoExpression`), so it needs a payload-bearing stage. The closer precedent is `MongoProjectStage`, which
already carries alias/expression pairs and is rendered through `MongoAggregationExpressionRenderer` — the
prototype's `MongoAddFieldsStage` is modelled on that, and the "lowerer stays BSON-free" invariant holds.

### 2.1 The synthetic field name

`__sortN`. It follows the established convention exactly — `MongoVectorSearchScoreStage.ScoreField` is
`"__score"`, `MongoReplaceRootStage.OwnerKeyField` is `"__ownerKey"`, `.OrdinalField` is `"__ord"` (READ) — a
double-underscore prefix on a name no element-name convention in this provider produces.

**Collision is a real hazard here, and the precedent says so.** `IsWholeElementRepresentable` carries a
sentinel-collision guard on the owned bare-element path *because* `$mergeObjects` silently overwrites a
same-named real field there (READ, `Query/AGENTS.md`). `$set` has the same overwrite semantics. The mitigating
difference is that `$unset` removes the field again before anything reads the document — but only if the
`$unset` ships (finding 2), and only for the top-level read. **Recommendation: the slice should carry the same
kind of guard** — decline (or suffix-disambiguate) if any mapped property of the root entity type has an element
name matching the chosen synthetic name. Not measured; INFERRED from the `$mergeObjects` precedent.

**One as-built defect to NOT copy: the counter must be per-`Lower`-invocation, not process-global.** The
prototype used a process-global `Interlocked` counter, and it shows: the same spec test emitted `__sort3` on one
case and `__sort4` on its `async` twin (MEASURED — see the §4 failure messages). A global counter makes the
emitted MQL non-deterministic across runs, which would make every committed `AssertMql` baseline unstable.

---

## 3. Step 4 — does it survive the shapers? Measured, per shaper

Prototype: `MongoAddFieldsStage` + `MongoUnsetStage`, a `RenderSort` arm accepting a `MongoElementRefExpression`
key, the lowerer's `MongoSortOp` arm emitting the three stages, and `NativeSlotPopulator`'s `OrderBy`/`ThenBy`
arms falling through to `TryTranslateValue` (gated on `MongoAggregationExpressionRenderer.CanRender`) when
`TryTranslateField` returns false. Query shape: `OrderBy(x => x.A + x.B)` over a four-row fixture seeded so that
**sum order, A order, B order, insertion order and label order are all mutually distinct** — a row-count or
insertion-order result cannot pass.

*(The brief suggested `x.A ?? x.B`. That shape is not reachable: `Coalesce` is capability-A slice A12 and
`TranslateOperand` declines it, MEASURED in §5. Numeric arithmetic is the widest key `TryTranslateValue` can
build today and exercises the identical `$set`/`$sort`/`$unset` machinery.)*

Every row below is `MongoQueryMode.NativeOnly` — success *is* the routing proof.

| # | shape | premise asserted | result | verdict |
|---|---|---|---|---|
| S1 | whole entity, **streaming-eligible** | `StreamingEligibility.IsEligible(Doc)` = **true**, asserted | order `L4,L2,L3,L1` = sum order; every `A`, `B`, `Id` correct | **PASS** |
| S2 | whole entity, **DOM** (TPH: a derived sibling) | `GetDirectlyDerivedTypes().Any()` = true **and** `IsEligible` = **false**, both asserted | same order, same values | **PASS** |
| S3 | **projection** after the computed sort | — | order preserved; `$project` emits only `Label`/`A`, no synthetic leak, no lost field | **PASS** |
| S4 | **paging** after the computed sort | — | `.Skip(1).Take(2)` → `L2,L3`, i.e. the sum-ordered page, not the insertion-ordered one | **PASS** |
| S5 | **tracking** query | — | 4 tracked entries, all `Unchanged`; re-running the query returns the SAME instances (identity resolution); mutate + `SaveChanges` round-trips and **no `__sort*` element is written back** | **PASS** |

MEASURED, all five. The streaming case is the one the brief flagged: the one-pass materializer's forward
name-dispatch must `SkipValue()` an element the entity model does not know about. It does — and the base case
it exercises is the same one `__score` exercises today (READ, `MongoStreamingEntityMaterializerRewriter`
`BuildFillLoop`'s `SkipValueMethod` fall-through). This spike executes it for a second, differently-typed
synthetic element.

### 3.1 The emitted pipelines (MEASURED, captured MQL)

```
S1  aggregate([{ "$set" : { "__sortN" : { "$add" : ["$A", "$B"] } } },
               { "$sort" : { "__sortN" : 1 } },
               { "$unset" : ["__sortN"] }])

S3  ... $set, $sort, $unset, { "$project" : { "Label" : "$Label", "A" : "$A", "_id" : 0 } }

S4  ... $set, $sort, $unset, { "$skip" : 1 }, { "$limit" : 2 }
```

### 3.2 Is the `$unset` required? — NO, measured both ways

The prototype gates the `$unset` behind `MONGODB_EF_SPIKE_NO_UNSET=1`, and four of the five shapes were re-run
with it suppressed (S1b streaming, S2b DOM/TPH, S3b projection, S5b tracking). **All four pass identically**,
and S5b additionally confirms nothing is written back on `SaveChanges`. MEASURED. The captured MQL confirms the
stage really was absent (`Assert.DoesNotContain("$unset", mql)` over the logged pipeline).

So neither shaper needs it: an unmapped top-level element is skipped by the streaming reader and ignored by the
DOM reader alike.

### 3.3 …and it should ship anyway

**INFERRED, not measured.** The synthetic element survives into the document *stream*, so anything downstream
that operates on the WHOLE document sees it. Two such operations exist on the native path today (READ): `Union`'s
dedup (`$group {_id: "$$ROOT"}`) and `Intersect`/`Except`'s source tagging (`$group {_id: "$_doc"}`). A set-op
operand is explicitly allowed to carry filter/sort/paging (`IsPlainWholeEntitySelect`), so a computed sort inside
an operand without the `$unset` would fold the synthetic value into the comparison key and change set semantics.
One stage per query is a cheap price for making that structurally impossible. This is the same
reasoning-from-rendering that the array-projection slice's `_id`-in-`$$ROOT` finding rests on — and that one WAS
measured to change Union/Intersect/Except row counts, so the mechanism is not hypothetical even though this
particular composition was not executed here.

---

## 4. Step 6, part 1 — slice B's standalone yield, MEASURED, and why the count says the opposite

Four full EF10 specification runs from one build, differing only by two environment variables
(`MONGODB_EF_SPIKE_B` ∈ {0,1} × `MONGODB_EF_NATIVE_ONLY` ∈ {0,1}). 4610 tests each.

| axis | slice B off | slice B on |
|---|---|---|
| `MONGODB_EF_NATIVE_ONLY=1` | 2461 passed / 2132 failed / 17 skipped | **2461 / 2132 / 17 — identical** |
| default `Native` | 4593 / **0** / 17 | 4581 / **12** / 17 |

The `noB` figures reproduce the documented post-A5 baseline (2461/2132/17 and 4593/0/17) exactly, which is the
control that the harness is measuring the right thing.

**Compared as SETS, not counts:** on the `NativeOnly` axis, `Failed→Passed` is **empty** and `Passed→Failed` is
**empty** — but **12 cases have a different failure MESSAGE**, and the transition is the whole finding:

> `NativeTranslationNotSupportedException : Query is not natively representable`
> → `Assert.Equal() Failure: Strings differ … Expected: "…{ "$project" : { "_id" : 0, "_d"…  Actual: "…{ "$set" : { "__sortN" : …`

All 12 are pure `AssertMql` baseline mismatches. `AssertMql` runs **after** the base EF Core result assertion,
so the data assertions passed in every one. The 12 Native-axis failures are the same 12 cases, seen from the
other side. **There are no data regressions on either axis.**

The 12, by test and by feature group (MEASURED from the emitted `$set` body):

| test (× 2 async cases) | emitted `$set` | group |
|---|---|---|
| `NorthwindAggregateOperatorsQueryMongoTest.OrderBy_client_Take` | `{"__sortN": 42}` | A3 bare constant |
| `NorthwindMiscellaneousQueryMongoTest.OrderBy_parameter` | `{"__sortN": 5}` | A3 bare parameter |
| `NorthwindMiscellaneousQueryMongoTest.Skip_orderby_const` | `{"__sortN": true}` | A3 bare constant |
| `NorthwindMiscellaneousQueryMongoTest.OrderBy_true` | `{"__sortN": true}` | A3 bare constant |
| `NorthwindMiscellaneousQueryMongoTest.OrderBy_integer` | `{"__sortN": 3}` | A3 bare constant |
| `NorthwindMiscellaneousQueryMongoTest.OrderBy_arithmetic` | `{"__sortN": {"$subtract": …}}` | arithmetic (A11/A14) |

**10 A3 cases + 2 arithmetic = 12.** The 10 is an exact match for §5.1's independently-derived *"A3 carries 10
of them while being 100% sole-cause"*, and for §4.3's *"bare const 10"* row inside the 92 — two measurements
from different instruments agreeing to the case.

**Consequence for the plan.** §7's *"Slice B — 92 cases enabled … 0 delivered alone"* is **false as measured**:
**12 are delivered by slice B alone**, because a bare constant, a bare query parameter and numeric arithmetic
are already inside `TranslateOperand`'s acceptance set — the only thing that was missing for them was the sort
position's ability to carry a non-field key.

**These 12 are a RE-ATTRIBUTION between slices, not 12 new cases — do not add them to ≈508.** The stream-1 spike
already counts them inside its 474 (its §3 row 5 gives bare constant/parameter `sole` 40 / `solB` 10 — exactly
the 10 A3 cases here — and the 2 arithmetic sit inside A11/A14). So the post-stream-1 checkpoint and the ≤3257
counterfactual are both **unchanged**. What moves is the per-slice attribution: **slice B's own gate is 12
rather than 0, and A3's marginal yield once slice B has shipped first is 30, not 40.** Taking both at their
headline numbers double-counts those 10 cases. The sequencing consequence is real in the other direction too:
**slice B is not a pure multiplier and has a non-zero gate of its own**, which makes it a far better-instrumented
slice than "expect zero" implies. See §7 for the same statement in the task-split context.

**Corollary about instrumentation, which is the durable part.** Judging slice B on the `NativeOnly` pass count
alone would have reported **0 of 92** and could plausibly have got the slice cancelled. Slice B changes emitted
MQL for precisely the tests it converts, so its wins are invisible on that axis until the baselines are
re-written. **Slice B's own acceptance gate must be the message-transition diff plus a `EF_TEST_REWRITE_BASELINES`
pass, never the raw pass count.** This is the identical trap `Query/AGENTS.md` already records for the
owned-collection `All` slice ("any future spec-delta inventory must check BOTH axes per test"), reached from the
opposite direction: there the spike missed a *regression* by looking at one axis; here it would have missed the
*wins*.

---

## 5. Step 6, part 2 — do the other 80 convert? Per-group, with an A/B control

Fifteen representative sort keys, each run under `NativeOnly` **twice from the same binary** — once with the
prototype live and once with `MONGODB_EF_SPIKE_B=0`. The second column is what makes the first meaningful.

| feature group (§4.3 row) | slice B **off** | slice B **on** | verdict |
|---|---|---|---|
| bare constant — `OrderBy(x => 1)` (A3, 10 in the 92) | declines | **native, `$set`** | **slice B alone** |
| bare parameter — `OrderBy(x => k)` (A3) | declines | **native, `$set`** | **slice B alone** |
| `Add` arithmetic — `x.A + x.B` (A11/A14, 4+4) | declines | **native, `$set`** | **slice B alone** |
| `Multiply` — `x.A * x.B` | declines | **native, `$set`** | **slice B alone** |
| `Convert` over a bare member, widening — `(double)x.A` | **native, PLAIN `$sort`** | native, PLAIN `$sort` | already native — but **this shape does not occur in the declining population**, see §5.1.1 *(read "§5.2" until a review caught it — §5.2 is about `CanRender`)* |
| `Convert` over a bare member, narrowing — `(int)x.D` | **native, PLAIN `$sort`** | native, PLAIN `$sort` | as above; the narrowing question is §8 |
| `??` — `x.Score ?? x.A` (A12, 22 in the 92) | declines | declines | needs **A12 + B** |
| `Not` — `!x.Flag` (A13, 18) | declines | declines | needs **A13 + B** |
| client `Contains` — `set.Contains(x.A)` (A6, 18) | declines | declines | needs **A6 + B** |
| `?:` — `x.A > 2 ? 1 : 0` (A9, 10) | declines | declines | needs **A9 + B** |
| `Convert` over comparison — `x.A > 2` (A1, 4) | declines | declines | needs **A1 + B** |
| string concat `Add` — `x.Label + x.Label` (A14) | declines | declines | needs **A14 + B** |
| `Negate` — `-x.A` (A17) | declines | declines | needs **A17 + B** |
| other member — `x.Label.Length` (ambiguous 2) | declines | declines | needs **A19 + B** |
| constructed value — `new {x.A, x.B}` (A7, 2) | declines | declines | needs **A7 + B** |

MEASURED. **The A/B control is what licenses the table**: the two columns come from the same binary, differing
only by an environment variable, and the gate is demonstrably live — with it off, the twelve shaper/`ThenBy`
tests of §3 and §6.1 all go red. The fifteen rows of this table are reporting probes rather than assertions, so
they pass in both arms; what they report is the difference above, and exactly four of them change.

**One trap, recorded because it nearly cost a wrong conclusion.** Every decline reports the *same* generic
message (`"Query projects a non-entity result and MongoQueryMode.NativeOnly forbids the driver-LINQ fallback"`),
which says nothing about *which* clause declined. The discrimination comes entirely from the controls: the
native rows use the identical trailing `.Select(x => x.Label)` and succeed, so the projection is not what
declines. A message-based classification here would have been worthless — the same naming/message trap recorded
for slice A2's shortfall analysis.

### 5.1 Slice B's exposure, restated on the measured basis

| | cases | basis |
|---|---:|---|
| delivered by slice B **alone** | **12** | MEASURED (§4) |
| enabled by slice B, blocked on a capability-A slice | **≈80** | INFERRED — 92 floor minus the 12, with each group's blocker MEASURED as a `TryTranslateValue` decline (§5) |
| §4.3's "ambiguous 6" | **unresolved** | the narrowing to 92–94 that this document first claimed is **WITHDRAWN** — see §5.1.1 and §10 |
| range for the 92 | **92 ≤ N ≤ 98, unchanged** | as §4.3 left it; **UNVERIFIED** |

**Per feature group — is the sort-position case blocked by the sort rendering ALONE, or by an inner node as
well?** The brief asks for this per group, because slice A5 taught this plan that a group can size at 36 and
convert 0. Counts in the first column are §4.3's, quoted not re-derived; the verdicts are this spike's.

| group (§4.3 count inside the 92) | blocked by the sort rendering **alone**? | tag |
|---|---|---|
| bare constant / parameter (A3, **10**) | **YES — sort rendering only, nothing else.** Proven by conversion: all 10 flip on slice B alone (§4). | **MEASURED** |
| `Add` / other arithmetic (A11 + A14, **8**) | **YES for the 2 that converted** (`OrderBy_arithmetic`); the other 6 declined for a further reason. Some `MongoBinaryOperator` values may also fall outside `IsRenderableOperator`'s 13. | **MEASURED** (2) / **UNVERIFIED** (6) |
| `??` `Coalesce` (A12, **22**) | **NO — needs A12 as well**, but that is the *only* addition: 38 of 38 declining occurrences have `??` at the root over supported operands (member/constant). The cleanest group on the board. | **MEASURED** |
| `Not` (A13, **18**) | **NO — needs A13 *and* A6 as well.** All 18 declining occurrences are `Not(<param>.Contains(…))`: the operand is itself an unsupported feature. §5.1.1. | **MEASURED** |
| client `Contains` (A6, **18**) | **NO — needs A6 as well.** 18 declining occurrences are bare `Contains`; a further 18 sit inside the `Not` keys above and belong to that row. | **MEASURED** |
| `?:` `Conditional` (A9, **10**) | **NO — needs A9, and for nearly all cases more.** Of 52 declining occurrences only **2** are over plainly-supported operands; 16 have a method-call test, 32 a transparent identifier (a join blocker, not breadth), 2 a `Convert` in the test. | **MEASURED** |
| `Convert`-over-comparison (A1, **4**) | **NO — needs A1 as well.** | **MEASURED** that both blockers are present |
| constructed value (A7, **2**) | **NO — needs A7 as well**, and A7 introduces a new node kind, so an aggregation-dialect arm too (§5.2). | **MEASURED** that both blockers are present |
| **will any of the above convert once its A slice ships?** | **UNVERIFIED for every row.** Not inheritable from §4.3's number; §5.1.1 says per group *why* the uncertainty differs. | **UNVERIFIED** |

### 5.1.1 Does the ≈80 carry the A5 exposure? MEASURED — and the answer is "not the way A5 did, but yes in a sibling form"

*(This subsection REPLACES an argument the first version of this document made and that was wrong. It claimed
a sort key has "no enclosing construct" because "the sort position is the outermost node by construction". That
conflates two different things: the sort *position* being outermost says nothing about whether the *labelled
feature node* is outermost **within the key expression**. A5's defect was never positional — its classifier
descends to the first failing child and cannot see an enclosure inside the same expression, so
`OrderBy(o => (o.A ?? o.B).Year)` would label as `??`, count sole-cause, and convert nothing when A12 ships.
Every §5 probe above uses the labelled node **bare**, so on its own §5 measures only that each group's bare
form needs A + B. The paragraph was replaced rather than softened, and the question was measured instead.)*

**Method.** A second throwaway worktree at `4adafc2c`, **pristine — no slice-B prototype at all**, with one
instrumentation hook: `NativeSlotPopulator`'s `OrderBy`/`ThenBy` arms append the declining key selector's
`NodeType`, CLR type and `ToString()` to a file whenever `TryTranslateField` returns false. One full EF10
specification run on the default `Native` axis (4593 passed / 0 failed / 17 skipped — the pristine baseline, so
nothing was perturbed). **692 declined sort-key occurrences, 64 distinct key expressions.** MEASURED.

*(Basis caveat, so this is not misread as a re-derivation of §4.3. These are declining **occurrences** across
the whole suite — every `OrderBy`/`ThenBy` whose key is not field-shaped, including keys inside cases whose
first decline site is elsewhere, and counted once per executed case (so async twins count twice). They are NOT
§4.3's sole-cause case counts and the two must not be summed or compared row-for-row. What this population can
answer, and what it is used for here, is the **shape** question: given a declining sort key of group G, is G at
the root, and are G's operands themselves supported?)*

| group | occurrences | feature **at the root** | feature **ENCLOSED** | operands themselves supported? |
|---|---:|---:|---:|---|
| `??` `Coalesce` | 38 | **38** | **0** | **YES** — 26 × `(c.Region ?? "ZZ")`, 12 × `(p.UnitPrice ?? 0)`: member and constant, both already inside `TranslateOperand`'s acceptance set |
| `Not` | 18 | 18 | 0 | **NO** — all 18 are `Not(<param>.Contains(c.CustomerID))`; the operand is *another* unsupported feature |
| `Contains` | 36 | 18 | **18** | the 18 enclosed ones are exactly the operands of the 18 `Not` keys above |
| `?:` `Conditional` | 52 | 52 | 0 | **mostly NO** — 16 × `IIF(c.CustomerID.StartsWith("S"), 1, 2)` (method-call test); 32 × `IIF((o.Inner != null), o.Inner.City/CustomerID, "")` (transparent identifier — a join shape, a different blocker entirely); 2 with a `Convert` in the test; only 2 × `IIF((c.Region == null), "ZZ", c.Region)` over plainly-supported operands. 16 + 32 + 2 + 2 = 52 |
| bare constant | 24 | 24 | 0 | n/a — this is the group slice B already converts (§4) |
| `Convert` | 18 | 18 | 0 | **NO** — 10 × `Convert(<param>, …)`, 8 × `Convert(o.Outer.OrderID/OrderDate, Object)` (transparent identifier). §5.2 |

**How much of the 692 this table covers, stated because the headline figure and the table do not match.** The
six rows sum to `38 + 18 + 36 + 52 + 24 + 18` = **186**, of which the `Contains` row's 18 *enclosed* entries are
the operands of the 18 `Not` keys rather than separate logged occurrences — so the table classifies
`18 + 18 + 38 + 52 + 24 + 18` = **168 distinct declining occurrences, 24.3% of the 692**, leaving **524
unclassified**. The rows above are the groups the ≈80 question needed answered, selected on that basis and not
as a partition; **what the remaining 524 consist of is NOT established by this spike — UNVERIFIED.** The
population is per-executed-case (async twins count twice) and includes keys inside cases whose first decline
site is elsewhere, so the remainder is expected to be dominated by shapes outside stream 1's scope, but that
expectation was not measured and no count is claimed for it.

**The literal A5 failure mode — an enclosure above the labelled node — is measured at ZERO for `??`, `Not`,
`?:` and `Convert`, and at 18 of 36 for `Contains` (all 18 enclosed in the `Not` keys).** So the classifier's
label is the root node for essentially the whole declining population, and §4.3's "`Not` 18 / `Contains` 18"
rows are **disjoint** cases (18 `Not(Contains)` keys labelled `Not`, plus 18 bare `Contains` keys), not the same
18 counted twice.

**But a SIBLING exposure is pervasive, and it is the honest form of the reviewer's concern.** The labelled node
sits at the root while its *operands* carry further unsupported features:

- **all 18 `Not` cases need A13 *and* A6 *and* slice B** — three things, not two. My §5 probe used `!x.Flag`,
  a bare `Not` over a supported boolean; **no case of that shape exists in the suite**, so that probe row is
  unrepresentative and should not be read as evidence about A13's yield;
- **50 of the 52 `?:` occurrences** need something beyond a plain conditional — re-summed exactly from the raw
  table: 16 have a method-call test (`StartsWith`), 32 a transparent identifier (a join-shape blocker, not
  translator breadth at all), 2 a `Convert` inside the test; only **2** are over plainly-supported operands
  (16 + 32 + 2 + 2 = 52);
- `??` is the one large group measured **clean**: 38 of 38 at the root with supported operands, so A12 + slice B
  really is the whole requirement for its sort column.

**Net effect on the ≈80.** The A5 lesson applies — not because the labelled node is hidden under an enclosure,
but because being at the root does not make the *sub-tree* translatable. A slice sized on "how many cases carry
feature G" over-states its sort-position yield wherever G's operands are themselves unsupported. On this
measurement that is severe for `Not` and `?:`, and absent for `??`. **The conversion of the ≈80 remains
UNVERIFIED** (§10) — this measurement narrows *why* it is uncertain, per group, rather than removing the
uncertainty.

### 5.2 The new obligation each capability-A slice inherits — the most actionable finding for the A slices

READ (`MongoAggregationExpressionRenderer.CanRender`, at `4adafc2c`): the aggregation dialect admits
`MongoFieldExpression`, `MongoElementRefExpression`, `MongoConstantExpression`/`MongoParameterExpression`,
`MongoBinaryExpression` over the listed operators, `MongoSizeExpression`, `MongoFilteredSizeExpression` — and
**nothing else**. Not `MongoInExpression`, not `MongoRegexExpression`, not `MongoElemMatchExpression`, not
`MongoUnaryExpression`.

A `$set` body is an aggregation expression. Therefore:

> **A capability-A slice whose sort column is to count must add an arm to `MongoAggregationExpressionRenderer`
> (`Render` *and* `CanRender`, which the file's own contract requires be changed together) — not only to
> `MongoQueryLanguageRenderer` / `IsQueryDialectRenderable`.** A node kind that exists only in the query dialect
> can serve a predicate but can never serve a computed sort key.

Concretely: **A6** (`Contains` → `MongoInExpression`) needs an aggregation `$in`; **A13** (`Not` →
`MongoUnaryExpression`) needs `$not`; **A9** (`?:`) needs `$cond`; **A12** (`??`) needs `$ifNull`. That is
**at least 18 + 18 + 10 + 22 = 68 of the 92** riding on an aggregation-dialect arm. A floor, not a total: A7
(constructed value, 2) introduces a new node kind and so needs one too, and A11 (other operator, 4) may use
`MongoBinaryOperator` values outside `IsRenderableOperator`'s 13. INFERRED from the READ arm list plus the
MEASURED declines.

**How much of that is genuinely UNNAMED, and where the existing note actually lives.** *(Corrected — the first
version of this document attributed the obligation note to the merge-plan spec. It is not there: that document
has zero hits for `IsQueryDialectRenderable`, `RenderNode` or `MongoAggregationExpressionRenderer`. The note is
the **stream-1 spike's §7**, `2026-08-07-stream1-translator-breadth-spike.md:559–563`, and its full text —
which the first version elided behind a `…` — **already requires**
`MongoAggregationExpressionRenderer.Render`+`CanRender`.)* Its scope is *"every slice that **introduces a new
`MongoExpression` node kind** (A9, A12, A14, and possibly A1)". So:

| | cases | already covered by the stream-1 spike §7 note? |
|---|---:|---|
| A9 `?:` (`$cond`, new node kind) | 10 | **YES** |
| A12 `??` (`$ifNull`, new node kind) | 22 | **YES** |
| A6 `Contains` | 18 | **NO** — `MongoInExpression` **already exists** (READ, `Query/Expressions/MongoInExpression.cs`), so A6 introduces no new node kind and falls outside the note's scope entirely |
| A13 `Not` | 18 | **NO** — `MongoUnaryExpression` **already exists** (READ, `Query/Expressions/MongoUnaryExpression.cs`), same reason |
| A7 constructed value | 2 | not enumerated by the note, though it does introduce a node kind |

**The genuinely unnamed obligation is therefore at least A6 18 + A13 18 = 36, not 68** — and that is the
*stronger* finding, precisely because neither slice introduces a new node kind. A reader following the existing
note would conclude that A6 and A13 need no renderer work at all, and would ship them with their sort columns
silently dead.

The prototype already conditions the populator fall-through on `CanRender`, which is the right shape: a node the
aggregation renderer cannot express declines cleanly at translate time instead of throwing at render time.

---

## 6. Step 5 — `ThenBy`, MIXED sorts, and the measured index cost

### 6.1 One `$set` per sort stage, not per ordering — MEASURED

| query | emitted MQL |
|---|---|
| `OrderBy(A).ThenBy(A+B)` | `{"$set": {"__s": {"$add": [...]}}}, {"$sort": {"A": 1, "__s": 1}}, {"$unset": ["__s"]}` |
| `OrderBy(A+B).ThenBy(Label)` | `{"$set": {"__s": {...}}}, {"$sort": {"__s": 1, "Label": 1}}, {"$unset": ["__s"]}` |
| `OrderBy(A+B).ThenBy(A*B)` | `{"$set": {"__s1": {"$add": …}, "__s2": {"$multiply": …}}}, {"$sort": {"__s1": 1, "__s2": 1}}, {"$unset": ["__s1", "__s2"]}` |

**One `$set` and one `$unset` per `MongoSortStage`, carrying every computed key of that stage.** That falls out
of emitting in the `MongoSortOp` arm (candidate (a)), and it is the right answer: `MongoSortOp` already holds all
of an `OrderBy`/`ThenBy` chain's orderings as one op (EF-347), so the three stages bracket the whole sort. Row
order is correct in all three (the mixed cases were seeded with deliberate ties so the secondary key is
load-bearing: `OrderBy(A).ThenBy(A+B)` → `M2,M1,M4,M3` vs `OrderBy(A+B).ThenBy(Label)` → `M2,M4,M1,M3`, two
different orders over the same four rows).

**A field key still renders as a plain path.** MEASURED — `{"$sort": {"A": 1, "__s": 1}}`, not a synthetic field
for both. Only the computed key goes through `$set`.

### 6.2 …but that does NOT keep it index-usable. MEASURED with `queryPlanner`

400 documents, indexes on `A` and on `B`, `explain` at `queryPlanner` verbosity:

| pipeline | winning plan |
|---|---|
| `{$sort: {A: 1}}` | **IXSCAN `A_1`** (via FETCH) |
| `{$sort: {A: 1}}, {$limit: 5}` | **IXSCAN `A_1`** |
| `$set` → `{$sort: {__s: 1}}` → `$unset` (the computed sort) | **COLLSCAN** |
| `$set` → `{$sort: {A: 1, __s: 1}}` → `$unset` (the MIXED sort) | **COLLSCAN** |
| `$match {A: {$gt: 10}}` → `$set` → `{$sort: {A: 1, __s: 1}}` → `$unset` | **IXSCAN `A_1`** (the `$match` uses it; the sort is a blocking sort) |
| `$set` → `{$sort: {A: 1}}` → `$unset` — **a purely FIELD sort with an unrelated `$set` in front** | **COLLSCAN** |

The last row is the one worth carrying forward. **The `$set` alone disqualifies the index-backed sort**, even
when the sort key is a plain indexed field path. So:

- a MIXED sort does **not** preserve index usability for its field key — §6.1's "renders as a plain path" is
  true of the MQL and false of the plan;
- the design **must not** emit a `$set` when no key of the stage is computed (the prototype already only emits
  it when at least one is — this measurement is what makes that a requirement rather than a tidiness);
- a `$match` ahead of the sort keeps its own index, so the common filter-then-sort shape is not disproportionately
  penalised.

This is the same correctness-over-index trade the branch has taken before, and it is smaller in kind: the
owned-collection `All` slice's `{$not: {$elemMatch: …}}` is also a measured COLLSCAN, and the `.Count`
array-index form likewise. The alternative here is not a cheaper plan — it is not supporting computed sort keys
at all. Record it; do not treat it as a blocker.

---

## 7. Proposed task split for slice B (EF-401)

Sized against what the prototype actually needed. The whole prototype is **172 diff lines** across three edited
files and two new ones.

| task | content | gate |
|---|---|---|
| **0** | *(nothing — this spike is task 0 and it is done.)* | this document |
| **1** | `MongoAddFieldsStage` + `MongoUnsetStage` (typed IR, BSON-free, payload of `(string, MongoExpression)` modelled on `MongoProjectStage`); `MongoPipelineFactory` arms for both; `RenderSort` accepting a `MongoElementRefExpression` key. Synthetic-name allocation **local to the `Lower` invocation** (§2.1). | unit tests on the renderer arms; three-EF-version build |
| **2** | `MongoSelectLowerer`'s `MongoSortOp` arm emits `$set` → `$sort` → `$unset`, and **only when at least one key of the stage is computed** (§6.2). Collision guard on the synthetic name against the root entity type's element names (§2.1). | unit tests incl. the no-computed-key path emitting a bare `$sort`, mutation-verified |
| **3** | `NativeSlotPopulator`'s `OrderBy`/`ThenBy` arms fall through to `TryTranslateValue`, gated on `MongoAggregationExpressionRenderer.CanRender` (§5.2). | the functional net below |
| **4** | Functional net: the five shaper shapes of §3 (each asserting its streaming/DOM PREMISE), the three `ThenBy`/mixed shapes of §6.1, the no-`$set`-when-not-needed pin, and a tracking round-trip asserting no `__sort*` is persisted. **Every data assertion pins ORDER, never a row count** — a dropped `$sort` returns the right count in insertion order. | mutation-verified: disabling the populator fall-through must turn the net red |
| **5** | Spec sweep on **both** axes, judged by **message transition** (§4), then `EF_TEST_REWRITE_BASELINES` for the 12 re-bases, then re-run to confirm they land `Failed→Passed` on the `NativeOnly` axis and green on the `Native` axis. | 12 conversions; 0 `Passed→Failed`; three-EF-version solution green |

**Emission-site decision: candidate (a)** — the `MongoSortOp` arm of `AppendSelectOpStages` — for the reason in
§2: it is the only site where adjacency and ordering inside the verbatim arrival-order sequence are structural
rather than maintained, and one edit covers `PipelineOps`, a set-op operand's ops, and `TrailingOps`.

**Keep the `$unset`** (§3.3), on set-op-hygiene grounds, and say so in the code comment — otherwise the next
reader will measure that no shaper needs it and delete it.

**Expected spec delta for the slice: 12 cases (6 tests) `Failed→Passed` on the `NativeOnly` axis after
re-baselining, 12 `AssertMql` re-bases on the `Native` axis, zero data changes.** The **message transition and
the zero data changes are MEASURED**; the `Failed→Passed` landing is **INFERRED** from it — no
`EF_TEST_REWRITE_BASELINES` pass was run. It could not usefully have been: the prototype's process-global name
counter (§2.1) emits a different `__sortN` on different runs, so any baseline written from it would not have
been stable. Re-baselining is task 5's job, after task 1 makes the counter deterministic.

**And the 12 is a RE-ATTRIBUTION, not an addition. It must not be added to ≈508.** The stream-1 spike already
counts these same cases inside its 474 — its §3 row 5 gives bare constant/parameter `sole` 40 with `solB` 10
(exactly the 10 A3 cases measured here), and the 2 arithmetic cases sit inside A11/A14. So **the ≈508
post-stream-1 checkpoint does not move**, and neither does the ≤3257 counterfactual. What changes is the
*attribution between slices*: slice B's own gate is 12 rather than 0, and **A3's marginal yield once slice B has
already shipped is 30, not 40** (its §7 `−B` column, which is the correct figure to plan A3 against in that
order). Anyone re-deriving stream 1's total from the per-slice table must take A3 at 30 if slice B is counted at
12 — taking both at their headline numbers double-counts those 10 cases.

---

## 8. One pre-existing question this spike surfaced and did NOT settle

`TryTranslateField` calls `Unwrap(keySelectorBody)`, and `Unwrap` strips **any** `Convert`/`ConvertChecked`
unconditionally, with no widening/narrowing check (READ; the same asymmetry `TryTranslateValue`'s own comment
calls out as the reason *it* deliberately does not `Unwrap` its top-level node). So `OrderBy(x => (int)x.D)`
over a `double` field is already native today and sorts by the **raw double**, not by the truncated value.

For most data those two orders coincide, and this spike's fixture does not discriminate them (`D` = 2.5 / 7.25 /
1.0 truncate to 2 / 7 / 1 — same order). **UNVERIFIED whether a fixture that does discriminate them (e.g. 1.4
and 1.6, which tie at `(int)` but not raw) produces a different order from in-memory LINQ or from driver-LINQ.**

It is **not a slice-B concern** — the behaviour is identical with the prototype disabled — but it is adjacent
enough that whoever builds A1 (casts) should settle it, since A1's whole subject is which `Convert`s may be
unwrapped and which may not.

---

## 9. Method — how to reproduce

Throwaway worktree at `4adafc2c`; `MONGODB_URI` and `ATLAS_URI` unset (TestContainers `mongodb/mongodb-atlas-local`).
Two env gates were compiled into the prototype so one build could be A/B-compared:

- `MONGODB_EF_SPIKE_B=0` — disables the `NativeSlotPopulator` fall-through (pristine sort behaviour).
- `MONGODB_EF_SPIKE_NO_UNSET=1` — omits the `$unset` stage.

```bash
# functional probe, both arms, from one build
for v in 1 0; do
  env MONGODB_EF_SPIKE_B=$v dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests \
    -c "Debug EF10" --no-build --filter "FullyQualifiedName~SpikeBComputedSortProbe" \
    --logger "console;verbosity=detailed"
done

# four spec runs, from the same build
for b in 0 1; do for n in 1 0; do
  env MONGODB_EF_SPIKE_B=$b MONGODB_EF_NATIVE_ONLY=$n \
    dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests -c "Debug EF10" --no-build \
    --logger "trx;LogFileName=b${b}_n${n}.trx"
done; done
```

Then compare the `.trx` files by `(testName → outcome, first 120 chars of message)`, and report the
**message-transition** classes, not the counts. §4 is entirely invisible to a count comparison.

**The §5.1.1 instrumentation run**, in a SECOND, pristine worktree (no prototype): `NativeSlotPopulator`'s
`OrderBy`/`ThenBy` arms append `NodeType`, CLR type and `Expression.ToString()` to
`$MONGODB_EF_SPIKE_SORTLOG` whenever `TryTranslateField` returns false; one full EF10 spec run on the default
`Native` axis (4593/0/17 — the pristine baseline, confirming the hook perturbs nothing); then classify the
resulting 692 rows by root `NodeType` and by whether the feature also appears nested. Reproduce by re-adding the
hook — it is four lines plus a lock.

Index measurement: `db.runCommand({explain: {aggregate: <coll>, pipeline: […], cursor: {}}, verbosity:
"queryPlanner"})` over 400 seeded documents with single-field indexes on both sorted fields, reading
`queryPlanner.winningPlan.stage` / `.inputStage.indexName` (and `stages[0].$cursor.queryPlanner…` for the
pipelines the server splits at a `$cursor`).

---

## 10. What is UNVERIFIED

- **Whether the ≈80 slice-B-enabled cases actually convert once their capability-A slice ships.** Each was
  measured to be blocked by `TryTranslateValue` *in addition to* the sort position; §5.1.1 measures, per group,
  whether the key's own operands are supported — and for `Not` (0 of 18) and `?:` (2 of 52) they are not, so
  those groups need more than one A slice. Whether the A slices are the *last* blocker is still not established
  and must not be inherited from §4.3's 92. §5.1, §5.1.1.
- **The identity of §4.3's six "ambiguous" cases, and hence the 92–98 range.** The first version of this
  document narrowed the ceiling to 94 on the strength of two `Convert`-over-bare-member probes. That narrowing
  is **WITHDRAWN**: §5.1.1's instrumentation shows the declining population contains no `Convert`-over-bare-member
  sort key at all (all 18 are over a parameter or a transparent identifier), so the probes were unrepresentative
  of the cases being sized. **The range stands at 92 ≤ N ≤ 98, exactly as §4.3 left it.** Settling it needs the
  four cases located by name, which this spike did not do.
- **Whether a `Convert` over a COMPUTED operand** (`(double)(x.A + x.B)`) exists anywhere in the suite's sort
  keys — it would be a genuine slice-B case. None appears in the 692 declining occurrences, but those are
  declines at `TryTranslateField`; a `Convert` over a computed operand that resolves some other way would not
  appear there. Not established.
- **Whether the 18 of the 92 that are not stream-1 sole-cause sit in the co-blocked buckets** — the merge plan's
  own open item (§3, *"the other 18 are never located"*). This spike did not locate them either; the
  counterfactual stays an upper bound.
- **The set-op hygiene argument for keeping the `$unset`** is reasoned from the rendering, not executed. No
  `Union`-with-a-computed-sort-operand query was run. §3.3.
- **The synthetic-name collision guard** is recommended from the `$mergeObjects` precedent, not measured. No
  model with a property whose element name is `__sortN` was built. §2.1.
- **Narrowing-cast sort keys** (§8) — the fixture used cannot discriminate raw-vs-truncated order.
- **Nothing here was run on EF8 or EF9.** The prototype adds no `#if` and touches only version-agnostic
  expression-tree and BSON code, so identical behaviour is INFERRED, not measured.
- **Concurrency of the synthetic counter** was not exercised; the per-invocation-counter requirement in §2.1 is
  derived from the observed non-determinism in the spec MQL, not from a race.
