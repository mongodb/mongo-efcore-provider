# Native query: set operations — Intersect / Except (whole-entity, terminal) — EF-347

**Ticket:** EF-347 (remaining SP6 relational operators) — set-ops Intersect/Except slice. Epic EF-322.
(Union/Concat shipped as the earlier set-ops slice, `750268a`. Confirm on PR whether this rides EF-347 or a
dedicated sub-ticket.)
**Type:** New native coverage. **Behavior change is additive**: `Intersect`/`Except` currently hard-fail
translation in *every* `MongoQueryMode` (their `Translate*` overrides return `null` → EF's
`NotTranslatedExpression` → `InvalidOperationException`); supported shapes now go native, and every
unsupported shape still hard-fails exactly as today (there is **no** driver-LINQ fallback for these — see
Verification). Per the versioning rubric the exception type of an unsupported shape is not part of the
contract.
**Stacked on:** the current native-stack rolling tip `origin/NativeQueryOngoing` = `1c2063c` (branch
`EF-347-setops-intersect-except`).

## Position in the larger scope (A → B → C)

The user's full goal is native `Intersect`/`Except` **plus** post-set-op composition (Count/aggregates and
Where/OrderBy/paging) **plus** projected-operand set ops, all four set operators (`Union`/`Concat`/
`Intersect`/`Except`) behaving consistently. That is too large and too seam-hazardous for one commit, so it
is decomposed into three stacked slices, each its own spec/plan/review:

- **Slice A (this spec):** `Intersect` + `Except`, whole-entity, **terminal-only**. The foundation — the
  synthesis MQL. Prerequisite for B and C.
- **Slice B (later):** relax terminal-only — post-set-op Count/aggregates and Where/OrderBy/paging, for **all
  four** set ops (retrofitting Union/Concat). The "make set ops non-terminal" work; the composition-seam
  hazard slice.
- **Slice C (later):** projected-operand set ops, for all four.

## Background

Union/Concat (slice 2) attach a `MongoSetOperation` (`Kind` + the second operand's own
`MongoSelectDefinition` + its collection name) to `source1.Select.SetOperation`, mark `IsSetOp = true`
(joining the shared `HasTerminalOperator` gate), keep `Route == WholeEntity`, and lower to a terminal
`$unionWith` (Union additionally emits a `$group{_id:"$$ROOT"}` + `$replaceRoot` full-document dedup). The
QMTEV helper `TryTranslateSetOperation(source1, source2, kind)` is **already kind-agnostic** and always
returns a non-null shaped query (native on success, `source1` marked non-native on guard-trip → graceful
fallback). `TranslateUnion`/`TranslateConcat` are thin wrappers over it; `TranslateIntersect`/
`TranslateExcept` still return `null`.

MongoDB has **no** direct intersect/except pipeline stage. But both operands of a set op are constrained to
the **same entity type** (`mongo1.CollectionExpression.EntityType == mongo2.CollectionExpression.EntityType`),
and an entity type maps to exactly one collection — so both operands are two filtered/sorted/paged views of
the **same collection**. Documents therefore have identical shape and field order, which makes full-document
`$$ROOT` value-equality well-defined (exactly the assumption Union's dedup already relies on) and lets us
synthesize intersection/difference with `$unionWith` + `$group`.

## Scope (decided at brainstorming)

**In:** `Intersect` and `Except` where (identical to the Union/Concat slice)
- both operands materialize **whole entities of the same entity type**, and
- both operands are **plain, natively-lowerable whole-entity selects** (they may carry their own
  `Where`/`OrderBy`/`Skip`/`Take`), and
- the set operation is **terminal** — it is the last operator in the query.

**Out (hard-fails translation in every mode, unchanged from today):**
- **Projected** set operations (either operand an anonymous/DTO/scalar projection) — slice C.
- **Post-set-op composition** — any operator after the set op (`Where`/`OrderBy`/`Skip`/`Take`/`Count`/
  aggregates/another set op, incl. chained `a.Intersect(b).Intersect(c)`) — slice B.
- An operand that is not a plain whole-entity select (its own projection, grouping, scalar cardinality, its
  own set op, or a `VectorSearch`).
- Operands of different entity types.

Because `Intersect`/`Except` have no driver-LINQ fallback, an out-of-scope shape reaches EF's
`NotTranslatedExpression` path and throws in every mode — it does **not** gracefully fall back the way an
out-of-scope Union/Concat does. See Verification.

## MQL target (Approach 2 — source-tagging)

`ctx.Set<Customer>().Where(a).Intersect(ctx.Set<Customer>().Where(b))`:
```
[ { $match: <a> },
  { $group:   { _id: "$$ROOT" } },
  { $project: { _id: 0, _doc: "$_id", _a: { $literal: true }, _b: { $literal: false } } },
  { $unionWith: { coll: "customers", pipeline: [
      { $match: <b> },
      { $group:   { _id: "$$ROOT" } },
      { $project: { _id: 0, _doc: "$_id", _a: { $literal: false }, _b: { $literal: true } } } ] } },
  { $group:   { _id: "$_doc", _a: { $max: "$_a" }, _b: { $max: "$_b" } } },
  { $match:   { _a: true, _b: true } },        // Intersect;  Except → { _a: true, _b: false }
  { $replaceRoot: { newRoot: "$_id" } } ]      // the re-unify $group put _doc under _id
```

Why it is correct and safe:
- **Dedup per operand** (`$group{_id:"$$ROOT"}`) gives each distinct document at most one tagged row per
  side, matching BCL `Intersect`/`Except` set semantics.
- **Tag under sibling fields.** The real document is carried under `_doc`; `_a`/`_b` are *siblings* of
  `_doc`, so they can never collide with a real entity field (all real fields live under `_doc.*`). This
  mirrors how Union already stashes the whole document under `_id`.
- **`$group` by `_doc`** re-unifies the two tagged streams by full-document value; `$max` over the booleans
  collapses the two sides (BSON sort order `false < true`, so `$max{false,true} = true`).
- **Final `$match` is the only difference between the two operators:** `Intersect` keeps `_a && _b`; `Except`
  keeps `_a && !_b` (in A, not in B). `$replaceRoot` restores the plain document.
- **Result order is not guaranteed** (the `$group` stages reorder) — this matches a database set operation
  and matches how Union already behaves; tests assert set-equality or append an `OrderBy`.

Rejected alternatives: **`$lookup` anti-/semi-join** (whole-`$$ROOT` `$expr` equality can't use an index →
potential O(|A|·|B|), and drives the `$lookup` code in a mode unlike any FK lookup the provider builds);
**count-based** (`$unionWith` + `$group` count + `$match{n:2}` — works for `Intersect` only; an Except doc
and a B-only doc both have count 1, so `Except` needs tagging regardless — asymmetric for no benefit).

## Design (extend the Union/Concat machinery)

### IR

- `Expressions/MongoSetOperation.cs` — extend `MongoSetOperationKind` with `Intersect` and `Except`. The
  `MongoSetOperation` holder itself is unchanged (kind + operand `MongoSelectDefinition` + operand collection
  name); a source-tagged synthesis needs no extra IR state (the tagging is entirely a lowering concern).
- `MongoSelectDefinition` — **unchanged.** `SetOperation`, `IsSetOp`, `Route == WholeEntity`, and the shared
  `HasTerminalOperator` gate all already exist and already cover Intersect/Except the moment they attach a
  `MongoSetOperation`.

### QMTEV — `TranslateIntersect` / `TranslateExcept`

Route both through the **existing** `TryTranslateSetOperation`, exactly like Union/Concat:
```csharp
protected override ShapedQueryExpression? TranslateIntersect(ShapedQueryExpression source1, ShapedQueryExpression source2)
    => TryTranslateSetOperation(source1, source2, MongoSetOperationKind.Intersect);

protected override ShapedQueryExpression? TranslateExcept(ShapedQueryExpression source1, ShapedQueryExpression source2)
    => TryTranslateSetOperation(source1, source2, MongoSetOperationKind.Except);
```
`TryTranslateSetOperation` needs **no change** — it is kind-agnostic (attaches the `MongoSetOperation`,
sets `IsSetOp`, applies `IsPlainWholeEntitySelect` + same-entity-type guards, always returns non-null). Add
`Intersect`/`Except` to `IsNativeRepresentableSlotOperator` (the whitelist) so the `NativeSlotPopulator`
catch-all does not clobber the override's decision, exactly as Union/Concat are listed.

**One consequence to accept and document:** for Union/Concat, a guard-tripped (out-of-scope) shape marks
`source1` non-native and returns it → graceful driver-LINQ fallback. For Intersect/Except there is no
working driver-LINQ fallback (the driver's LINQ v3 provider does not translate cross-view Intersect/Except
here — confirm as the plan's first step), so a `MarkNotNativelyRepresentable()`-and-return-`source1` on an
out-of-scope shape would route to a fallback that then fails at execution. `TryTranslateSetOperation`
currently always calls `MarkNotNativelyRepresentable()` (never returns `null`), which is correct for
Union/Concat but wrong for Intersect/Except. **Resolution:** on the guard-trip path, decide by kind — for
`Union`/`Concat` keep the current graceful `MarkNotNativelyRepresentable()` + return `source1`; for
`Intersect`/`Except` return `null` so the shape reaches EF's `NotTranslatedExpression` hard-fail directly (a
clean, contract-consistent decline in every mode — matching how reference `SelectMany` handles its
no-baseline shapes). This is a small, localized branch in `TryTranslateSetOperation` on the guard-fail path
only; the success path is unchanged.

### Lowerer — `MongoSelectLowerer`

The lowerer already appends a terminal `MongoUnionWithStage` when `select.SetOperation != null`. Branch on
`SetOperation.Kind`:
- `Concat` / `Union` — unchanged (Union sets the existing `dedup` flag).
- `Intersect` / `Except` — emit the source-tagging sequence: the outer operand's own canonical stages
  (already emitted), then `$group{_id:"$$ROOT"}` + tag-`$project` for the A side, the `$unionWith` (operand
  lowered recursively via the same `AppendSelectOpStages`/`AppendCanonicalStages` helper, with its own
  dedup-`$group` + tag-`$project` inside the nested pipeline), then the re-unify `$group{$max}`, the
  discriminating `$match`, and the final `$replaceRoot`.

The cleanest fit with the existing typed-stage model is a small **new stage type** carrying the kind and the
recursively-lowered operand stages (see below), rather than threading six ad-hoc stages through the lowerer.
The lowerer stays BSON-free; all BSON lives in the factory.

### New stage type — `NativeTranslation/Stages/MongoSetDifferenceStage`

A sibling of `MongoUnionWithStage`, holding `MongoSetOperationKind Kind` (`Intersect` | `Except`),
`IReadOnlyList<MongoPipelineStage> OperandStages`, and `string OperandCollectionName`. BSON-free.
(`MongoUnionWithStage` stays dedicated to the plain `$unionWith`(+dedup) Union/Concat shape; the tagging
pipeline is different enough — it wraps *both* sides in dedup+tag `$project`s and adds re-unify/match/
replaceRoot — that a separate stage type reads more clearly than overloading `MongoUnionWithStage` with a
mode flag. The lowerer picks the stage type by kind.)

### Renderer / factory — `MongoPipelineFactory`

New `RenderStage` case `MongoSetDifferenceStage s => RenderSetDifference(s, renderer, placeholders)`, which
emits the full document sequence of Approach 2:
1. `$group {_id:"$$ROOT"}` (A dedup)
2. `$project {_id:0, _doc:"$_id", _a:{$literal:true}, _b:{$literal:false}}`
3. `$unionWith {coll:<OperandCollectionName>, pipeline:[ <operand stages>, $group{_id:"$$ROOT"},
   $project{_id:0, _doc:"$_id", _a:{$literal:false}, _b:{$literal:true}} ]}`
4. `$group {_id:"$_doc", _a:{$max:"$_a"}, _b:{$max:"$_b"}}`
5. `$match {_a:true, _b:true}` (Intersect) or `{_a:true, _b:false}` (Except)
6. `$replaceRoot {newRoot:"$_id"}` (the re-unify `$group` put `_doc` under `_id`)

Like `RenderUnionWith`, the operand stages are rendered through the **same `RenderStage` recursion and the
same shared `PlaceholderTable`** as the outer pipeline, so a parameter captured in the operand's predicate
substitutes correctly at `factory.Build(parameterValues)` time. `RenderSetDifference` returns the sequence of
`BsonDocument`s; the `Create` stage-walk already flattens a multi-document stage result (Union relies on the
same). The `_doc`/`_a`/`_b` field names are fixed literals emitted by the renderer.

### Shaper

**Unchanged.** After the final `$replaceRoot {newRoot:"$_doc"}` each row is a plain whole-entity document of
`source1`'s type, so `source1`'s existing DOM/streaming whole-entity shaper materializes every result row —
no new shaper, exactly as Union/Concat.

## Correctness hazards (explicitly guarded)

1. **Post-set-op composition** — the recurring "operator after the new terminal" wrong-data class. Already
   handled: `IsSetOp` is in the shared `HasTerminalOperator` gate, so every post-terminal entry point
   (`NativeSlotPopulator`'s seven slot operators, `NativeCardinalityBinder.TryBindAggregate`/`TryBindReducer`,
   `TranslateGroupBy`, `TranslateSelect`'s guards, `TranslateOfType`, `TranslateSelectMany`,
   `TryBindDistinctFromProjection`) already rejects anything composed after an Intersect/Except with no new
   guard code — Intersect/Except reuse the identical `IsSetOp` provenance Union/Concat set. Because there is
   no driver-LINQ fallback, these composed shapes hard-fail in every mode (they do not gracefully fall back);
   regression tests lock one seam per operator.
2. **Field-name collision** — `_doc`/`_a`/`_b` are siblings of the wrapped document, never nested with real
   fields, so no collision with entity element names. (Documented; a test with an entity that *has* a field
   literally named `_a`/`_b`/`_doc` confirms the wrapping isolates them.)
3. **Full-document value-equality** — both operands are the same collection, so field order is consistent and
   `$group` by `$$ROOT`/`$_doc` groups equal documents correctly (same assumption Union's dedup relies on).
4. **Parameter substitution across the nested pipeline** — shared placeholder table (as Union); a
   parametrized operand predicate must substitute at `Build` time. Tested with a parametrized operand.
5. **Guard-trip decline path** — an out-of-scope Intersect/Except must return `null` (hard-fail), not
   `MarkNotNativelyRepresentable()` + return `source1` (which would route to a non-working fallback). The
   kind-branched decline (QMTEV section) is the fix; tested by asserting out-of-scope shapes throw in
   `Native`/`DriverLinq`/`NativeOnly` alike.

## Non-goals (later slices / never)

- Post-set-op composition going native (slice B).
- Projected / scalar set operations (slice C).
- Chained set operations going native (slice B).
- `SelectMany` combined with a set op.
- Reusing `ClassifyNativeDisposition` for the operand-nativeness guard (possible future unification; slice 2
  deliberately kept the conservative `IsPlainWholeEntitySelect` subset).

## Files touched

- `src/…/Query/Expressions/MongoSetOperation.cs` — add `Intersect`/`Except` to `MongoSetOperationKind`.
- `src/…/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs` — `TranslateIntersect`/
  `TranslateExcept` route through `TryTranslateSetOperation`; add `Intersect`/`Except` to
  `IsNativeRepresentableSlotOperator`; kind-branch the guard-fail decline in `TryTranslateSetOperation`
  (`null` for Intersect/Except, `MarkNotNativelyRepresentable()` + `source1` for Union/Concat).
- `src/…/Query/NativeTranslation/MongoSelectLowerer.cs` — emit `MongoSetDifferenceStage` for
  `Intersect`/`Except` (recursive operand lowering).
- `src/…/Query/NativeTranslation/Stages/MongoSetDifferenceStage.cs` — new stage type.
- `src/…/Query/NativeTranslation/MongoPipelineFactory.cs` — `RenderSetDifference` + shared-placeholder
  threading.
- `src/…/Query/AGENTS.md` — document the Intersect/Except slice, the source-tagging MQL, and the
  no-driver-baseline / hard-fail-in-every-mode behavior (updating the "`Intersect`/`Except` hard-fail
  unconditionally" statements now that supported shapes go native).
- Tests: unit (lowerer/renderer for the tagged pipeline, IR/guard), functional/spec (correct result set vs
  expected data, `NativeOnly` succeed/throw, parametrized-operand substitution, composition-seam hard-fails,
  field-name-collision isolation).

## Verification

- **No driver-LINQ baseline — establish this as the plan's first step.** Today `TranslateIntersect`/
  `TranslateExcept` return `null` → hard-fail in every mode, so there is no `Native == DriverLinq` oracle.
  The plan's first step probes whether the driver's LINQ v3 provider can translate a cross-view
  Intersect/Except at all; the design assumes it cannot (matching the historical `null` choice and the
  reference-`SelectMany` precedent). **If it turns out the driver *can***, we additionally get a parity
  oracle and can relax the out-of-scope decline back to graceful fallback — but the design does not depend on
  it. **Primary correctness bar:** `Native` produces the **correct result set** asserted against expected
  in-memory (LINQ-to-Objects) data — set-equality (unordered) or with a trailing `OrderBy` — over cases:
  disjoint operands, fully-overlapping operands, partial overlap, duplicate rows within an operand, empty
  operand(s), parametrized operand predicate.
- **`NativeOnly` proves native:** supported Intersect/Except succeed under `MongoQueryMode.NativeOnly`; every
  out-of-scope shape (projected, post-set-op op, non-lowerable operand, mismatched type) throws.
- **Composition-seam regression tests** (hazard 1) — one operator-after-set-op per shape hard-fails in every
  mode.
- **Field-name-collision test** (hazard 2).
- **Full 3-version `/test-all`** green (EF8/EF9/EF10), plus the `MONGODB_EF_NATIVE_ONLY=1` spec sweep showing
  the net increase in native-covered set-op tests with zero regressions.

## Follow-ups

- **Slice B:** post-set-op composition (Count/aggregates + Where/OrderBy/paging) for all four set ops.
- **Slice C:** projected-operand set operations for all four.
