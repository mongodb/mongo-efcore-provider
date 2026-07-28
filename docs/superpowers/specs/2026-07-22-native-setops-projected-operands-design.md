# Native query: projected-operand set operations — `Select(...).Union(Select(...))` (all four) — EF-347

**Ticket:** EF-347 (remaining SP6 relational operators) — set-ops slice C1, the **final** set-ops slice. Epic EF-322.
(Set-ops shipped so far: Union/Concat `750268a`, Intersect/Except `84e67c7`, post-composition `1e3adc9`,
trailing projection `cb48685`.)
**Type:** New native coverage, additive. A set operation whose **operands are projected**
(`A.Select(projA).Union(B.Select(projB))`) currently falls back to driver-LINQ (Union/Concat — correct results,
throws only under `NativeOnly`) or hard-fails in every mode (Intersect/Except — no driver-LINQ oracle). This
slice makes the supported projected-operand shape go native; every unsupported shape keeps its current behavior.

Per the versioning rubric the exact emitted MQL and the internal execution path are not part of the contract,
and the exception type of an unsupported shape is not part of the contract.

**Stacked on:** the native-stack rolling tip `origin/NativeQueryOngoing` = `cb48685` (branch off it).

## Position in the larger scope (set-ops A → B → C1/C2)

Set ops decomposed into: slice A (`Intersect`/`Except` whole-entity terminal, `84e67c7`), slice B
(post-set-op composition — Where/OrderBy/paging/aggregates/reducers, `1e3adc9`), and slice C (projection +
set op), split at brainstorming into two stacked sub-slices:

- **C2 (done, `cb48685`):** a **trailing projection** — a `Select` applied AFTER a whole-entity set op
  (`Union(wholeA, wholeB).Select(x => new {...})`). The `$project` is emitted *after* the set-op stage, over
  the combined result. Whole-entity operands, dedup over whole entities.
- **C1 (this spec):** **projected operands** — the projection is *inside* each operand
  (`A.Select(...).Union(B.Select(...))`). Each operand's `$project` is emitted *before* the combine, operands
  may be different collections, and dedup runs over the *projected* documents. C1 is the last set-ops slice.

C1 is genuinely harder than C2: C2 reused slice B's `TrailingOps` fall-through and the SP3 projection binder +
shaper with **no** lowerer change; C1 requires real lowerer changes to place each operand's projection ahead
of the combine and to lower the operand's own projection into the nested `$unionWith`/set-difference pipeline.

## Distinction from C2 — why the lowerer cannot infer the two apart

A trailing projection (C2) and a projected operand (C1) both leave `Projection.Count > 0` **and**
`SetOperation != null` on source1's `Select`. The lowerer therefore cannot tell them apart from state alone —
it needs explicit provenance. C1 adds a `bool OperandsProjected` to `MongoSetOperation`, set `true` at
`TryTranslateSetOperation` time when both operands are projected selects, and the lowerer branches on it.

| | C2 (trailing) | C1 (projected operands) |
|---|---|---|
| Where the `$project` sits | AFTER the set-op stage (over combined result) | BEFORE the combine, once per operand |
| Union dedup is over | whole entities (`$$ROOT` before projection) | projected values (`$$ROOT` after projection) |
| Operands | same collection, whole-entity | may be **different** collections |
| `OperandsProjected` | `false` | `true` |

Both dedup behaviours match BCL: `Union(wholeA, wholeB).Select(p)` dedups whole entities then projects (C2);
`A.Select(p).Union(B.Select(p))` dedups the projected values (C1). The set-op stages (`$group{_id:$$ROOT}`
dedup for Union, the source-tagging pipeline for Intersect/Except) are already agnostic to whether `$$ROOT` is
a whole entity or a projected document — they operate on whatever the operand pipelines produce — so **no**
dedup/source-tagging rendering change is needed; only the *placement* of each operand's `$project` changes.

## Design (Approach A — minimal provenance flag)

Chosen over Approach B (reshaping `MongoSetOperation` to carry both operands' selects symmetrically): A is
reuse-first — one flag plus a localized lowerer branch, no restructuring of the IR or of slices A/B/C2's
rendering, for the same observable result. Consistent with every prior set-ops slice.

### IR — `Expressions/MongoSetOperation.cs`
Add `bool OperandsProjected` (default `false`). When `true`, both operands were plain projected selects at the
time the set op was attached, so each operand's own `Projection` is its operand-pipeline tail rather than a
trailing projection over the combined result.

### QMTEV — `TryTranslateSetOperation` gate (`MongoQueryableMethodTranslatingExpressionVisitor.cs`)
Accept a projected-operand shape in addition to the existing whole-entity shape:

```csharp
// existing whole-entity path (slices A/B/C2) — unchanged
if (IsPlainWholeEntitySelect(mongo1) && IsPlainWholeEntitySelect(mongo2)
    && mongo1.CollectionExpression.EntityType == mongo2.CollectionExpression.EntityType)
{
    mongo1.Select.SetOperation = new MongoSetOperation(kind, mongo2.Select, mongo2.CollectionExpression.CollectionName);
    mongo1.Select.IsSetOp = true;
    return source1;
}

// NEW projected-operand path (slice C1)
if (IsPlainProjectedSelect(mongo1) && IsPlainProjectedSelect(mongo2)
    && ProjectionShapesMatch(mongo1.Select.Projection, mongo2.Select.Projection))
{
    mongo1.Select.SetOperation = new MongoSetOperation(
        kind, mongo2.Select, mongo2.CollectionExpression.CollectionName, operandsProjected: true);
    mongo1.Select.IsSetOp = true;
    return source1;
}

// out-of-scope: kind-conditional decline (unchanged) — Union/Concat graceful, Intersect/Except null hard-fail
```

- **`IsPlainProjectedSelect(mongo)`** — the projected analogue of `IsPlainWholeEntitySelect`:
  `mongo.Select.Route == NativeRoute.Projection && mongo.Select.Projection.Count > 0 && mongo.Select.SetOperation == null
  && !mongo.Select.IsSetOp && mongo.Select.Grouping == null && mongo.Select.Cardinality == null
  && mongo.Select.UnwindSource == null && !mongo.IsJoinQuery && mongo.Lookups.Count == 0
  && !ContainsVectorSearch(mongo.CapturedExpression)`. I.e. a `Select`-projection is the SOLE terminal — no
  grouping/cardinality/own-set-op/SelectMany/lookups/join/VectorSearch. (`Route == Projection` already implies
  `Projection.Count > 0`, but stating both keeps the predicate self-documenting and defensive.) The
  `TrailingOps` list is empty for an operand — a set op has not yet been attached to it, so `ActiveOps` is
  still `PipelineOps` — so no `TrailingOps` check is needed; a projected operand's filter/sort/paging live in
  its own `PipelineOps`, which lower ahead of its `$project` exactly like a standalone projected query.
- **`ProjectionShapesMatch(p1, p2)`** — equal top-level alias sets between the two operands' `MongoProjection`s
  (same count, same alias names). A **correctness guard, not just an optimization gate**: the operand pipelines
  produce documents whose fields are exactly those aliases, and Union dedup / Intersect-Except source-tagging
  compare whole projected documents by value — mismatched alias sets would compare structurally-different
  documents and silently mis-dedup / mis-tag. EF Core's `NavigationExpandingExpressionVisitor` rejects
  set-operation operands with incompatible shapes upstream, so a mismatch is not reachable via ordinary LINQ;
  this check is defense-in-depth that declines cleanly (graceful fallback for Union/Concat, `null` hard-fail
  for Intersect/Except) rather than emitting a wrong pipeline if that upstream guarantee ever changed. It
  compares alias *sets* only, not the underlying field-refs — `A.Select(a => new {N = a.Name})` and
  `B.Select(b => new {N = b.Title})` correctly match (both produce `{N: ...}`); each operand's own `$project`
  maps its own source field to the shared alias.

`TryTranslateSetOperation` still **always** returns non-null for Union/Concat (graceful) and may return `null`
for Intersect/Except (hard-fail, no baseline) — the kind-conditional decline path is unchanged.

### Lowerer — `NativeTranslation/MongoSelectLowerer.cs`
In the `SetOperation` block, when `setOp.OperandsProjected`:

1. **Source1's projection ahead of the combine.** Immediately after the source1 `PipelineOps` block
   (`AppendSelectOpStages(select.PipelineOps, stages)`) and before building the set-op stage, emit
   `stages.Add(new MongoProjectStage(select.Projection))`. This makes the *outer* operand's pipeline
   (source1 ops → `$project`) complete before the combine.
2. **Operand's projection inside the nested pipeline.** After
   `AppendSelectOpStages(setOp.OperandSelect.PipelineOps, operandStages)`, append
   `operandStages.Add(new MongoProjectStage(setOp.OperandSelect.Projection))` so the `$unionWith` /
   set-difference operand pipeline ends in its own `$project`.
3. **Suppress the trailing-Projection fall-through.** The fall-through `Projection` block (stage 6,
   `if (select.Projection.Count > 0) stages.Add(new MongoProjectStage(select.Projection))`) must NOT re-emit
   source1's projection — it was already emitted in step 1. Guard it with `&& !projectionAlreadyEmitted`
   (a local bool set in step 1) or, equivalently, `&& !(select.SetOperation?.OperandsProjected ?? false)`.
   When `OperandsProjected == false` (C2 trailing projection, or a non-set-op query) this block is unchanged,
   so C2 and all prior slices are byte-for-byte unaffected.

The dedup stage (`MongoUnionWithStage(..., dedup: Kind == Union)`) and the `MongoSetDifferenceStage` are
untouched — they render over `operandStages` / the combined result exactly as before; the `$$ROOT` they group
on is simply a projected document now.

### Shaper
Unchanged. `TranslateSelect` on source1 built the SP3 projection shaper by rewriting `projA` over source1's
whole-entity shaper — reading the top-level result aliases by name. `TryTranslateSetOperation` returns source1,
so that shaper is the query's shaper. After the combine + dedup/tagging + (Intersect/Except) `$replaceRoot`,
the result documents still carry exactly those top-level aliases, so the shaper reads them unchanged. It is
agnostic to the set-op stage and to whether the projection was applied per-operand or trailing.

## Correctness hazards (explicitly guarded / tested)

1. **Operand-projection placement** (the core change): source1's `$project` before the set-op stage, the
   operand's `$project` inside the nested pipeline, and no double-emit via the fall-through block. Verified by
   captured MQL and by `NativeOnly` success (a mis-placed or dropped projection would fail materialization).
2. **Different-collection operands with matching projected shape** (the marquee case): the `EntityType`
   equality gate does NOT apply on the projected path; `ProjectionShapesMatch` + EF's upstream compatibility
   admit `A.Select(p).Union(B.Select(p))` across different collections. Mismatched alias sets → graceful
   fallback (Union/Concat) / `null` hard-fail (Intersect/Except).
3. **Union dedup semantics = over projected values** — distinct entities projecting to equal values collapse
   to one row, matching BCL `A.Select(p).Union(B.Select(p))` and deliberately **different** from C2's
   whole-entity dedup. Tested with two distinct entities that project to equal values (expect one row).
4. **Intersect/Except `$$ROOT` value-equality over projected docs** — sound because both operand shapes are
   identical after projection (same aliases, `ProjectionShapesMatch`), so full-projected-document equality is
   well-defined — the same soundness argument slice A made for whole documents, now over projected documents.
   `_a`/`_b` source tags remain siblings of `_doc`, never colliding with a projected alias. Tested with
   disjoint / overlap / dedup / empty operand pairs × the projected shape, asserted against expected in-memory
   (LINQ-to-Objects) results (no driver-LINQ oracle for Intersect/Except).
5. **Post-composition is auto-closed** — an operator composed after a projected-operand set op
   (`A.Select(p).Union(B.Select(p)).Where(...)`) sees `Projection.Count > 0`, so `IsSetOpTerminalOnly` is
   already `false` (its slice-C2 `Projection.Count == 0` conjunct) and the existing two catch-all guard sites
   plus every own-`Translate`-override `HasTerminalOperator` guard reject it — **no new guard code**. Tested
   to confirm: Union/Concat fall back gracefully, Intersect/Except hard-fail every mode.
6. **Deferred shapes keep current behavior:** a bare-scalar/computed-leaf/entity-ref operand never populates
   `Projection` (SP3 rejects it → `Route == Fallback`), so `IsPlainProjectedSelect` rejects it → graceful
   fallback / hard-fail; a mixed whole-entity-operand-vs-projected-operand pair fails both the whole-entity
   path (one operand is projected) and the projected path (one operand is whole-entity) → declines the same way.

## Scope (decided at brainstorming)

**In:** a set operation both of whose operands are a terminal **anonymous-type / DTO member-access** `Select`
(the SP3 projection surface — leaves are all top-level member accesses), for **all four** set ops
(Union/Concat/Intersect/Except). Operands may be the **same or different** collections/entity types, provided
their projected shapes (alias sets) match. Each operand may carry its own filter/sort/paging (in its own
`PipelineOps`, ahead of its `$project`).

**Out (deferred — unchanged behavior):**
- **Bare-scalar / computed-leaf / entity-ref operand projections** (`A.Select(a => a.Name).Union(...)`,
  `A.Select(a => a.X * 2).Union(...)`) — SP3 never pushes these down, so the operand is not a plain projected
  select.
- **Mixed operands** — one whole-entity operand and one projected operand.
- **Post-composition** — any operator after a projected-operand set op (auto-closed by
  `IsSetOpTerminalOnly`'s `Projection.Count == 0`).
- **Chained set ops, `GroupBy`/`OfType`/`SelectMany` after a projected-operand set op** — each still trips its
  own untouched `HasTerminalOperator` guard.

## Fallback / no-baseline — consistent with slices A/B/C2

`TryTranslateSetOperation`'s kind-conditional decline is unchanged: an out-of-scope projected-operand shape
(a deferred operand-projection form, a mismatched shape, a mixed pair) marks the query non-native for
Union/Concat → graceful driver-LINQ fallback (correct results, throws only under `NativeOnly`), and returns
`null` for Intersect/Except → hard-fail in every mode (the driver's LINQ v3 provider cannot translate a
cross-view Intersect/Except).

## Files touched
- `src/…/Query/Expressions/MongoSetOperation.cs` — add `bool OperandsProjected`.
- `src/…/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs` — accept the projected-operand
  shape in `TryTranslateSetOperation`; add `IsPlainProjectedSelect` and `ProjectionShapesMatch`.
- `src/…/Query/NativeTranslation/MongoSelectLowerer.cs` — emit each operand's projection ahead of the combine
  and inside the nested pipeline; suppress the fall-through Projection block when `OperandsProjected`.
- `src/…/Query/AGENTS.md` — document projected-operand set ops as native (mechanism, dedup-over-projected
  semantics, the `OperandsProjected` provenance flag, `IsPlainProjectedSelect`/`ProjectionShapesMatch`),
  mark C1 as the final set-ops slice, update the deferred lists that named projected operands as the C1 gap.
- Tests: functional `NativeSetOpsTests` (Union/Concat projected-operand Native==DriverLinq parity — same- and
  different-collection operands, the dedup-collapse case, per-operand filter; Intersect/Except projected-operand
  result-set + `NativeOnly`; the hazard tests above; deferred bare-scalar/computed/mixed fallback split; the
  post-composition auto-closure tests); flip the slice-B/C2 tests that named projected-operand set ops as
  deferred/fallback; Northwind spec re-baseline.

## Verification
- **Union/Concat parity oracle:** every supported projected-operand shape produces the same result under
  `Native` and `DriverLinq` (same-collection operands, different-collection operands, the dedup-collapse case,
  per-operand-filtered operands).
- **Intersect/Except result-set bar (no oracle):** `Native` produces the correct set asserted against expected
  in-memory (LINQ-to-Objects) data, over disjoint / overlap / dedup / empty operand pairs × the projected shape.
- **`NativeOnly` proves native:** supported projected-operand shapes succeed under `NativeOnly`; every deferred
  shape (bare-scalar/computed/entity-ref operand, mixed pair, post-composition, chained/GroupBy/OfType/SelectMany
  after) throws under `NativeOnly` and — for Intersect/Except — in every mode.
- **Full 3-version `/test-all`** green (EF8/EF9/EF10), plus the `MONGODB_EF_NATIVE_ONLY=1` set-ops spec sweep
  showing the net increase with zero regressions.

## Follow-ups
- With C1 done, the entire set-ops decomposition (A → B → C1/C2) is complete. Remaining EF-347 relational tail
  and then SP7 (streaming materializer capstone) + the parity cutover, per the native-stack plan.
- Bare-scalar/computed-leaf operand projections, and post-composition after a projected-operand set op, if ever
  wanted (both deferred here).
