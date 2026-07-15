# Native query: set operations — Union / Concat (whole-entity, terminal) — EF-347 slice 2

**Ticket:** EF-347 (remaining SP6 relational operators) — set-ops Union/Concat slice. Epic EF-322.
(Slice 1 = native OfType + projected Distinct, already shipped. Confirm on PR whether this rides EF-347
or a dedicated sub-ticket.)
**Type:** New native coverage. **Behavior change is additive + non-breaking**: shapes that previously fell
back to driver-LINQ now go native; every unsupported shape still falls back (throws only under
`NativeOnly`). Query *results* are unchanged (verified `Native == DriverLinq`).
**Stacked on:** EF-334 (`7d00914`), the current native-stack tip on `origin/NativeQueryOngoing`.

## Background

`Union`/`Concat`/`Intersect`/`Except` currently return `null` from their QMTEV `Translate*` overrides, so
they fall back to the driver-LINQ provider (which does handle them). This slice introduces the first
**sub-pipeline** capability — rendering a second query as a nested pipeline operand — and uses it for the
two tractable set operations, `Union` and `Concat`, over whole entities.

`Concat` and `Union` map to MongoDB's `$unionWith`:
- **Concat** (no dedup): `source1 pipeline …, { $unionWith: { coll: <source2 coll>, pipeline: [ <source2 stages> ] } }`
- **Union** (set dedup): the same `$unionWith`, then a full-document dedup:
  `{ $group: { _id: "$$ROOT" } }, { $replaceRoot: { newRoot: "$_id" } }`

`Intersect`/`Except` have no direct MQL operator over document streams (they need `$lookup`/`$group`
tricks) and are a separate, harder follow-up slice.

## Scope (decided at brainstorming)

**In:** `Union` and `Concat` where
- both operands materialize **whole entities of the same entity type**, and
- both operands are **plain, natively-lowerable whole-entity selects** (they may carry their own
  `Where`/`OrderBy`/`Skip`/`Take` slots), and
- the set operation is **terminal** — it is the last operator in the query.

**Out (falls back to driver-LINQ, graceful; `NativeOnly` throws):**
- `Intersect` / `Except` (no direct MQL operator — separate slice).
- **Projected** set operations (either operand is an anonymous/DTO/scalar projection).
- **Post-union composition** — any operator applied *after* the union (`Where`/`OrderBy`/`Skip`/`Take`/
  `Count`/aggregates/another set op, incl. chained `a.Union(b).Union(c)`).
- An operand that is not a plain whole-entity select — one that itself carries a projection, grouping,
  scalar cardinality, its own set operation, or a `VectorSearch`.
- Operands of **different entity types**.

## MQL target

`ctx.Set<Customer>().Where(a).Concat(ctx.Set<Customer>().Where(b))`:
```
[ { $match: <a> },
  { $unionWith: { coll: "customers", pipeline: [ { $match: <b> } ] } } ]
```
`…Union(…)` appends:
```
  { $group: { _id: "$$ROOT" } },
  { $replaceRoot: { newRoot: "$_id" } }
```

## Design (Approach A — reuse the whole-entity path + an `IsSetOp` terminal flag)

### IR

- New `Expressions/MongoSetOperation` — a small dialect-neutral holder:
  `MongoSetOperationKind Kind` (`Concat` | `Union`), the operand's `MongoSelectDefinition OperandSelect`,
  and `string OperandCollectionName`.
- `MongoSelectDefinition` gains `internal MongoSetOperation? SetOperation { get; set; }` and an
  `internal bool IsSetOp` provenance flag (set when a set operation is attached).
- `Route` is **unchanged** and stays `NativeRoute.WholeEntity` for a set-op query: the result shaper is
  identical to a whole-entity query (both operands are the same entity type), so it compiles through the
  existing `TryBuildNativeFactory` whole-entity path with no new gate branch and no new shaper.
- **Terminal guard:** `IsSetOp` joins the post-terminal gate set. The `HasTerminalGrouping` predicate
  (currently `IsGroupBy || IsDistinct || Grouping != null`) becomes `… || IsSetOp` — and, for accuracy,
  is renamed `HasTerminalOperator`. Every post-terminal entry point already keyed on it
  (`NativeSlotPopulator`'s seven-slot guard, `NativeCardinalityBinder.TryBindAggregate`/`TryBindReducer`,
  `TranslateGroupBy`, `TranslateSelect`'s non-grouped branch, `TranslateDistinct`) therefore rejects any
  operator applied after the union with no per-site change. (This is the recurring "what comes after the
  new terminal" hazard; reusing the existing guard set is exactly how EF-347 slice 1 handled Distinct.)

### QMTEV — `TranslateConcat` / `TranslateUnion`

Both call a shared `TryTranslateSetOperation(source1, source2, kind)`:
1. Recover `mongo1 = (MongoQueryExpression)source1.QueryExpression`, `mongo2` likewise.
2. **Guards — return `null` (graceful fallback) unless all hold:**
   - `source1.ResultCardinality == Enumerable` and same for the element types: `source1` and `source2`
     element types are equal and map to an entity type (whole-entity, not a projection).
   - `mongo1.Select` and `mongo2.Select` are each a **plain whole-entity select**:
     `Route == WholeEntity`, `SetOperation == null` (no chaining), `Grouping == null`,
     `Cardinality == null`, `Projection.Count == 0`, and the operand's `CapturedExpression` contains no
     `VectorSearch` (an operand vector search still returns `WholeEntity` but is not lowerable here).
3. On success: set `mongo1.Select.SetOperation = new MongoSetOperation(kind, mongo2.Select,
   mongo2.CollectionExpression.CollectionName)`, set `mongo1.Select.IsSetOp = true`, and return a
   `ShapedQueryExpression` reusing `source1`'s shaper (same entity type). Construct it directly (the base
   `TranslateUnion`/`TranslateConcat` are abstract — mirror the direct construction already done in
   `TranslateGroupBy`).

`TranslateExcept`/`TranslateIntersect` keep returning `null` (out of scope).

The **operand-nativeness guard is intentionally conservative** (a plain whole-entity select only). It does
*not* reuse the gate's `ClassifyNativeDisposition` — that is a gate-side method and threading it into the
QMTEV is scope creep for this slice; the `Route == WholeEntity` + no-terminal-op + no-vector-search checks
are a sufficient, safe subset (worst case is an over-conservative fallback, never a wrong result). A future
refactor could unify them.

### Lowerer — `MongoSelectLowerer`

When `select.SetOperation != null`, after emitting source1's canonical `$match → $sort → $skip → $limit`
stages, append:
- `new MongoUnionWithStage(operandStages, operandCollectionName, dedup: kind == Union)`, where
  `operandStages` is produced by **recursively lowering** `SetOperation.OperandSelect` (its own canonical
  `$match → $sort → $skip → $limit`).

The union stage is the terminal stage (terminal-only scope guarantees nothing follows it). The dedup flag
tells the renderer to emit the `$group`/`$replaceRoot` pair after `$unionWith` for `Union`.

### New stage type — `NativeTranslation/Stages/MongoUnionWithStage`

Holds `IReadOnlyList<MongoPipelineStage> OperandStages`, `string OperandCollectionName`, `bool Dedup`.
BSON-free, like every other stage.

### Renderer / factory — `MongoPipelineFactory`

- New `RenderStage` switch case: `MongoUnionWithStage u => RenderUnionWith(u, renderer, placeholders)`.
- `RenderUnionWith` builds `{ $unionWith: { coll: <OperandCollectionName>, pipeline: [ <rendered operand
  stages> ] } }` by walking `u.OperandStages` through the **same `RenderStage` recursion and the same
  `placeholders` table** (see below). When `u.Dedup`, it returns **two** documents — the `$unionWith`
  followed by `$group {_id:"$$ROOT"}` and `$replaceRoot {newRoot:"$_id"}`. (The stage-walk in `Create`
  already flattens a stage that yields multiple `BsonDocument`s; if it currently assumes one-doc-per-stage,
  `RenderUnionWith` returns the small sequence and `Create` appends each — a minor, localized adjustment.)

**Shared placeholder table (the one genuinely new bit of sub-pipeline machinery).** The operand's stages
are rendered into the **same `PlaceholderTable`** as the outer pipeline, so a parameter used inside the
`$unionWith` pipeline substitutes correctly at `factory.Build(parameterValues)` time along with the outer
parameters. `RenderUnionWith` therefore receives and threads the outer `placeholders` into the nested
render calls — it does **not** create a fresh table. The `MongoPipelineFactory` template is still built once
at compile time; only sentinel substitution runs per execution, nested pipeline included.

### Shaper

Unchanged. Both operands are the same entity type, so `source1`'s existing DOM/streaming whole-entity
shaper materializes every union row. (Streaming eligibility is orthogonal and unchanged; a set-op query is
compiled through the same whole-entity path.)

## Correctness hazards (explicitly guarded)

1. **Post-union composition** — the recurring "operator after the new terminal" wrong-data class. Handled by
   adding `IsSetOp` to the shared `HasTerminalOperator` guard, so any following operator falls back before
   it can emit a stage that would wrongly precede or misread the `$unionWith`. Regression tests must lock
   the composition seams (Where/OrderBy/Skip/Take/Count/GroupBy/another-Union after a Union → fallback,
   `Native == DriverLinq`).
2. **Non-lowerable operand** — if `source2` is anything but a plain whole-entity select, the whole union
   falls back (the guard rejects it), never emitting a partial/lossy pipeline.
3. **Union dedup semantics** — full-document `$$ROOT` grouping gives LINQ `Union`'s value-equality set
   semantics; verified by `Native == DriverLinq` parity (the driver-LINQ fallback already implements
   `Union`).
4. **Parameter substitution across the nested pipeline** — shared placeholder table (above); a parametrized
   predicate inside the operand must substitute at `Build` time. Test with a parametrized operand predicate.

## Non-goals

- `Intersect` / `Except`.
- Projected / scalar set operations.
- Post-union composition and chained set operations.
- `SelectMany` (the next sub-pipeline slice, which extends this machinery with correlation + `$unwind`).
- Non-canonical `Skip`/`Take` (separate, non-sub-pipeline slice).
- Reusing `ClassifyNativeDisposition` for the operand-nativeness guard (possible future unification).

## Files touched

- `src/…/Query/Expressions/MongoSetOperation.cs` — new IR holder + `MongoSetOperationKind` enum.
- `src/…/Query/Expressions/MongoSelectDefinition.cs` — `SetOperation` field, `IsSetOp` flag, extend/rename
  `HasTerminalGrouping` → `HasTerminalOperator`.
- `src/…/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs` — `TranslateConcat`/
  `TranslateUnion` + shared `TryTranslateSetOperation` + operand guard; add `Concat`/`Union` to
  `IsNativeRepresentableSlotOperator` (so the catch-all doesn't clobber the override's decision).
- `src/…/Query/NativeTranslation/MongoSelectLowerer.cs` — append `MongoUnionWithStage` when
  `SetOperation != null`; recursive operand lowering.
- `src/…/Query/NativeTranslation/Stages/MongoUnionWithStage.cs` — new stage type.
- `src/…/Query/NativeTranslation/MongoPipelineFactory.cs` — `RenderUnionWith` + shared-placeholder threading.
- `src/…/Query/AGENTS.md` — document the set-op slice + the `HasTerminalOperator` rename/invariant.
- Tests: unit (lowerer/renderer for `$unionWith` + dedup, IR/guard) + functional/spec (parity + `NativeOnly`
  succeed/throw, parametrized-operand substitution, composition-seam fallbacks).

## Verification

- **`Native == DriverLinq` parity** for Union/Concat over whole entities (with/without operand predicates,
  parametrized predicates, empty operands, duplicate rows) — the primary correctness bar **if** the
  driver-LINQ v3 provider implements `Union`/`Concat` (it emits `$unionWith`; confirm at implementation).
  **If the driver fallback instead throws** for these today, there is no parity baseline — then the bar is
  **`Native` produces the correct result set** asserted against expected data, and the feature is purely
  additive (a shape that used to throw now succeeds). Establish which case holds as the plan's first step.
- **`NativeOnly` proves native**: supported Union/Concat succeed under `MongoQueryMode.NativeOnly`; every
  out-of-scope shape (Intersect/Except, projected, post-union op, non-lowerable operand) throws
  `NativeTranslationNotSupportedException`.
- **Composition-seam regression tests** (hazard 1) — operator-after-union falls back, parity holds.
- **Full 3-version `/test-all`** green (EF8/EF9/EF10), plus the `MONGODB_EF_NATIVE_ONLY=1` spec sweep
  showing the intended net increase in native-covered set-op tests with zero regressions.

## Follow-ups

- `Intersect` / `Except` (harder — `$lookup`/`$group` set-difference/intersection).
- Post-union composition (stages after `$unionWith`) — the general staged model.
- Projected set operations (projection/shaper parity).
- `SelectMany` (correlated `$lookup` pipeline-form + `$unwind`, reusing this slice's nested-pipeline
  rendering + shared placeholder table).
- Non-canonical `Skip`/`Take` (sequential-stage paging).
