# Native query: set-op post-composition — Where/OrderBy/paging + aggregates/reducers (all four set ops) — EF-347

**Ticket:** EF-347 (remaining SP6 relational operators) — set-ops slice B. Epic EF-322.
(Slices shipped so far: Union/Concat `750268a`, Intersect/Except `84e67c7`. Confirm on PR whether this rides
EF-347 or a dedicated sub-ticket.)
**Type:** New native coverage. **Behavior is additive** and splits by operator family:
- **Union/Concat:** post-set-op composition currently **falls back gracefully** to driver-LINQ (correct
  results under `Native`/`DriverLinq`, throws only under `NativeOnly`). This slice widens which of those
  shapes execute *natively* — a coverage change, **not** a result change (the driver-LINQ path is the oracle).
- **Intersect/Except:** post-set-op composition currently **hard-fails in every mode** (no driver-LINQ
  baseline — see slice A). This slice makes the supported shapes go native; every still-unsupported shape
  keeps hard-failing exactly as today.

Per the versioning rubric, the exact emitted MQL and which internal path a supported query takes are not part
of the contract, and the exception type of an unsupported shape is not part of the contract.

**Stacked on:** the native-stack rolling tip `origin/NativeQueryOngoing` = `84e67c7` (branch off it as usual).

## Position in the larger scope (A → B → C)

Set ops were decomposed into three stacked slices (see the slice-A design doc,
`2026-07-20-native-setops-intersect-except-design.md`):

- **Slice A (shipped, `84e67c7`):** `Intersect`/`Except`, whole-entity, terminal-only. The synthesis MQL.
- **Slice B (this spec):** relax terminal-only — post-set-op composition for **all four** set ops
  (retrofitting Union/Concat, which today only fall back gracefully). The "make set ops non-terminal" work;
  the composition-seam hazard slice.
- **Slice C (later):** projected-operand set operations, for all four.

## Scope (decided at brainstorming)

**In** — the following operators composed **after** a whole-entity, natively-representable set op
(`Union`/`Concat`/`Intersect`/`Except`), for all four:

- `Where` → a trailing `$match`.
- `OrderBy`/`OrderByDescending`/`ThenBy`/`ThenByDescending` → a trailing `$sort` (imposing an order the set op
  itself does not guarantee).
- `Skip`/`Take` → trailing `$skip`/`$limit`.
- Scalar aggregates `Count`/`LongCount`/`Sum`/`Min`/`Max`/`Average`/`Any`/`All` → a trailing
  `$count`/`$group`/`$limit` (via the existing `NativeCardinalityBinder` machinery, including the
  `All`/predicate-bearing `Count`/`Any` predicate injection).
- Entity reducers `First`/`FirstOrDefault`/`Single`/`SingleOrDefault` → a trailing `$limit` (1, or 2 for the
  `Single` family) plus EF Core's own base cardinality reduction over the returned `IEnumerable<T>`.

These may be **chained** (e.g. `a.Union(b).Where(p).OrderBy(k).Skip(1).Take(2)`), and compose with the set
op's *own* operand-side and source1-side filter/sort/paging.

**Out (deferred — unchanged behavior):**
- **Trailing projection** — a `Select` after the set op (`a.Union(b).Select(x => new {...})`). Deferred to
  slice C (projection machinery). Union/Concat keep falling back gracefully; Intersect/Except keep
  hard-failing.
- **Trailing `Distinct`** — `a.Union(b).Distinct()`. Deferred (its own terminal machinery). Same fallback
  split.
- **Chained set operations** — `a.Union(b).Union(c)`, `a.Intersect(b).Except(c)`, etc. Still terminal:
  `IsPlainWholeEntitySelect` rejects source1 once `IsSetOp` is set, so the second set op is out of scope.
- **Trailing `GroupBy`, `OfType`, `SelectMany`** — all keep their existing post-terminal guards.
- **Projected / scalar-projected set operations** — slice C.

## Background — why post-composition is a hazard, and where trailing stages must go

`MongoSelectDefinition` (as of EF-347) holds an ordered `PipelineOps` list (`MongoMatchOp`/`MongoSortOp`/
`MongoSkipOp`/`MongoLimitOp`) emitted verbatim in arrival order. A set op attaches a `MongoSetOperation`
(kind + operand `MongoSelectDefinition` + operand collection name) to `Select.SetOperation`, sets
`IsSetOp = true`, and the lowerer emits (`MongoSelectLowerer.Lower`):

```
1. AppendSelectOpStages(select)            // source1's OWN pre-set-op $match/$sort/$skip/$limit
2. AppendLookupStages(query)               // no-op: a set-op operand carries no lookups
3. set-op terminal stage:                  // $unionWith (+dedup) | source-tagging $group/$unionWith/$match/$replaceRoot
      return stages;                        //  <-- EARLY RETURN — nothing may follow
```

The outer select's `PipelineOps` therefore emit **before** the set-op stage — they are source1's own
pre-set-op filter/sort (e.g. `ctx.Set<X>().Where(a).Union(...)` → `$match{a}` before `$unionWith`). A
post-set-op `Where` recorded into that same `PipelineOps` list would emit a `$match` **before** the set-op
stage, filtering only source1's rows instead of the combined result — the composition-seam wrong-data bug
this slice must avoid. Post-set-op ops need a **separate** list that emits **after** the set-op stage.

MongoDB pipelines are linear: appending `$match`/`$sort`/`$skip`/`$limit`/`$count`/`$group` after the set-op
stage operates on the combined whole-entity document stream, which is exactly the required semantics. The
set-difference (Intersect/Except) shape ends in `$replaceRoot{newRoot:"$_id"}` and the Union shape ends in
`$unionWith` (+ optional dedup `$group`+`$replaceRoot`); in both cases the rows after the terminal stage are
plain whole-entity documents of source1's type, so trailing stages compose cleanly with no shaper change.

## Design (Approach 1 — a trailing op list with active-target routing)

### IR — `Expressions/MongoSelectDefinition.cs`

- Add a private `_trailingOps` (`List<MongoSelectOp>`) and a read-only `TrailingOps` view, mirroring
  `_pipelineOps`/`PipelineOps`.
- The five merge methods — `AddPredicateConjunct`, `StartOrReplaceSort`, `AppendThenBy`, `AppendSkip`,
  `AppendLimit` — write to whichever list is **active**:
  ```csharp
  private List<MongoSelectOp> ActiveOps => SetOperation != null ? _trailingOps : _pipelineOps;
  ```
  The flip point is well-defined and single: `TryTranslateSetOperation` assigns `SetOperation`, so every op
  recorded *after* that lands in `_trailingOps`; every op recorded *before* (source1's own) stays in
  `_pipelineOps`. The **operand** select (`mongo2.Select`, stored as `SetOperation.OperandSelect`) never has a
  `SetOperation` assigned, so its own `PipelineOps` are unaffected — the operand still lowers via
  `AppendSelectOpStages` over its `PipelineOps` exactly as in slices A/2.
  - The tail-merge semantics carry over unchanged, now scoped to `ActiveOps`: consecutive trailing `Where`s
    still AND into one `$match` (`AddPredicateConjunct` merges into the tail op if it is a `MongoMatchOp`),
    `OrderBy` after a trailing `OrderBy` still resets (`StartOrReplaceSort` replaces a tail `MongoSortOp`),
    `ThenBy` extends the tail trailing sort, and repeated trailing `Skip`/`Take` append in order.
- Add `internal bool IsSetOpTerminalOnly => IsSetOp && !IsGroupBy && !IsDistinct && Grouping == null &&
  UnwindSource == null;`. A set op only ever attaches to a plain whole-entity select
  (`IsPlainWholeEntitySelect` — no grouping/projection/cardinality/unwind), so `IsSetOp` already implies the
  other four are clear; the conjunction is explicit/defensive so the guard relaxation below can never
  accidentally open a GroupBy/Distinct/SelectMany terminal.
- `HasPaging`/`HasOrdering` keep scanning `_pipelineOps` **only**. They gate a *pre-terminal* `GroupBy`
  (`NativeGroupByBinder.TryBindGroupKey`), which is unreachable after a set op (a trailing `GroupBy` is
  rejected by `HasTerminalOperator` — see below), so they must not see trailing ops. Document this.
- `HasTerminalOperator` is **unchanged** (`IsGroupBy || IsDistinct || IsSetOp || Grouping != null ||
  UnwindSource != null`) — it stays the "is there any terminal?" predicate. The relaxation is expressed at
  each guard site as `HasTerminalOperator && !IsSetOpTerminalOnly`, so the *deferred* post-terminal shapes
  (trailing Select/Distinct/GroupBy/SelectMany/OfType, chained set ops) keep tripping it exactly as today.

### Guards — selectively open, for a set-op-only terminal

- `NativeTranslation/NativeSlotPopulator.cs` — the post-terminal reject at the top of `PopulateNativeSlots`
  becomes:
  ```csharp
  if (mongoQ.Select.HasTerminalOperator && !mongoQ.Select.IsSetOpTerminalOnly
      && IsPostGroupSlotOperator(methodDefinition))
  {
      mongoQ.Select.MarkNotNativelyRepresentable();
      return;
  }
  ```
  When the terminal is a set op, the seven slot operators fall through to their existing arms; their merge
  calls (`AddPredicateConjunct`/`StartOrReplaceSort`/`AppendThenBy`/`AppendSkip`/`AppendLimit`) now target
  `_trailingOps` via `ActiveOps`. A trailing op whose *body* is untranslatable (e.g. `Where` with an
  unsupported predicate) still calls `MarkNotNativelyRepresentable()` inside its arm — Union/Concat then fall
  back gracefully, Intersect/Except hard-fail (consistent with the no-baseline split).
- `NativeTranslation/NativeCardinalityBinder.cs` — both guards become
  `if (select.HasTerminalOperator && !select.IsSetOpTerminalOnly)`:
  - `TryBindReducer`: `select.AppendLimit(...)` records the reducer `$limit` into `_trailingOps`;
    `Cardinality = ForReducer(...)`. No lowerer reducer-case needed — the reducer is purely the trailing
    `$limit` + EF's base cardinality reduction (as pre-set-op).
  - `TryBindAggregate`: the `All`/predicate-bearing `Count`/`Any` predicate injection
    (`AddPredicateConjunct`) records into `_trailingOps` — landing **after** the set-op stage and after any
    trailing paging, never hoisted ahead of it; `Cardinality = ForAggregate(...)`. Setting `Cardinality`
    flips `Route` to `ScalarAggregate`, which is correct — the result is a scalar. The lowerer still emits the
    set-op stage (it keys off `SetOperation`, not `Route`), then the aggregate stage (see below).

### Lowerer — `NativeTranslation/MongoSelectLowerer.cs`

In the `if (select.SetOperation is { } setOp)` block, replace `return stages;` with a trailing-op emission and
**fall through**:
```csharp
if (select.SetOperation is { } setOp)
{
    // ... emit MongoSetDifferenceStage | MongoUnionWithStage (unchanged) ...
    AppendTrailingOpStages(select, stages);   // post-set-op $match/$sort/$skip/$limit, in arrival order
    // fall through to the Cardinality block for a post-set-op aggregate
}
```
`AppendTrailingOpStages` is `AppendSelectOpStages`'s body over `select.TrailingOps` (extract the shared
`foreach (op ...) switch { ... }` into a helper taking an `IReadOnlyList<MongoSelectOp>`). After the set-op
block falls through, the intermediate blocks are all skipped because a set op guarantees they are null/empty
(`UnwindSource == null`, `Grouping == null`, `Projection.Count == 0` — all enforced by
`IsPlainWholeEntitySelect` on both operands, and no trailing Select/SelectMany/GroupBy/Distinct is in scope to
set them). Execution reaches the `Cardinality` block (stage 7), which emits the post-set-op `$count`/`$group`
after the set-op stage and trailing ops. Add an assertion or defensive comment that `UnwindSource`/`Grouping`/
`Projection` are empty in the set-op-plus-fall-through path, so a future slice that composes one of those
after a set op is forced to revisit this ordering rather than silently emit stages in the wrong precedence.

### Fallback / no-baseline — consistent with slice A

`TryTranslateSetOperation` is **unchanged** — its guard-decline split (graceful for Union/Concat, `null` for
Intersect/Except) already governs whether a native set op exists at all. This slice only changes what happens
to operators composed *after* a native set op:
- **Union/Concat:** a supported trailing operator goes native (records into `_trailingOps`); an *unsupported*
  trailing shape (deferred operator, or an untranslatable trailing body) marks non-native via its existing
  guard and falls back gracefully to driver-LINQ (correct results, throws only under `NativeOnly`).
- **Intersect/Except:** a supported trailing operator goes native; an unsupported trailing shape marks
  non-native → the gate builds the driver-LINQ fallback from the captured chain → the driver throws because
  it cannot translate the cross-view Intersect/Except (hard-fail in every mode). The `SelectMany`-after-set-op
  case returns `null` directly (`TranslateSelectMany`'s own path), also hard-failing in every mode. Identical
  to the slice-A observable outcome: throws, never wrong data.

No new decline code is needed for the deferred set. The relaxation (`&& !IsSetOpTerminalOnly`) is applied
only at the two catch-all guard sites the slot operators and the cardinality binder pass through. The deferred
operators — `Select`/`Distinct`/`GroupBy`/`SelectMany`/`OfType` — each gate independently in their own
`Translate*` override on the full, un-relaxed `HasTerminalOperator`, which stays tripped after a set op; so
they keep declining exactly as today with no change.

> Precision on the relaxation surface: `IsSetOpTerminalOnly` relaxes only the two *catch-all* guard sites
> that the seven slot operators and the cardinality binder pass through (`NativeSlotPopulator`'s top guard and
> `NativeCardinalityBinder`'s two guards). The own-`Translate`-override operators (`Select`, `Distinct`,
> `GroupBy`, `SelectMany`, `OfType`) each gate independently on `HasTerminalOperator` in their own override
> and are **not** touched by this slice, so they stay terminal after a set op. This is why the deferred set
> needs no new code.

## Correctness hazards (explicitly guarded / tested)

1. **Composition-seam ordering** (the core hazard): a post-set-op `$match`/`$sort`/`$skip`/`$limit`/aggregate
   MUST emit after the set-op stage, over the combined result — not before it, over source1 only. Enforced
   by the `ActiveOps` flip (trailing ops go to `_trailingOps`) + the lowerer emitting `_trailingOps` after the
   set-op stage. Tested by **seam-discriminating** cases where pre- vs post-placement changes the observable
   result — `Count()` (combined count ≠ source1 count) and `OrderBy(k).Skip(1).Take(2)` (paging the combined,
   ordered stream ≠ paging source1). A trailing `Where` alone can coincidentally agree under dedup, so it is
   not sufficient on its own — the Count/paging cases are the discriminators.
2. **Set op stays terminal for deferred operators** — a trailing Select/Distinct/GroupBy/SelectMany/OfType or
   a chained set op must still fall back (Union/Concat) or hard-fail (Intersect/Except), never go native.
   Enforced by leaving those operators' own `HasTerminalOperator` guards untouched; `IsSetOpTerminalOnly`
   relaxes only the slot/cardinality catch-alls. One regression test per deferred operator per fallback split.
3. **Parameter substitution across the trailing stages** — a parameter captured in a trailing `Where`
   predicate (`a.Union(b).Where(x => x.V == captured)`) must substitute at `Build(parameterValues)` time. The
   trailing `$match` renders through the same shared `PlaceholderTable`/renderer as every other stage, so this
   is covered by the existing mechanism; tested with a parametrized trailing predicate.
4. **Aggregate route interaction** — a post-set-op aggregate flips `Route` to `ScalarAggregate` while
   `SetOperation` is still attached. The lowerer must still emit the set-op stage (it keys off `SetOperation`,
   not `Route`) and the gate must still compile the scalar-result path. Verify the gate
   (`ClassifyNativeDisposition` / `ExecuteAggregate`) makes no assumption that a `ScalarAggregate` route has no
   set op — plan Task 1.

## Files touched

- `src/…/Query/Expressions/MongoSelectDefinition.cs` — `_trailingOps`/`TrailingOps`, `ActiveOps` routing in
  the five merge methods, `IsSetOpTerminalOnly`; doc `HasPaging`/`HasOrdering` staying `_pipelineOps`-scoped.
- `src/…/Query/NativeTranslation/NativeSlotPopulator.cs` — relax the top post-terminal guard with
  `&& !IsSetOpTerminalOnly`.
- `src/…/Query/NativeTranslation/NativeCardinalityBinder.cs` — relax both `TryBindReducer`/`TryBindAggregate`
  guards with `&& !select.IsSetOpTerminalOnly`.
- `src/…/Query/NativeTranslation/MongoSelectLowerer.cs` — emit `TrailingOps` after the set-op stage and fall
  through to the `Cardinality` block; extract `AppendTrailingOpStages`.
- `src/…/Query/AGENTS.md` — update the Union/Concat and Intersect/Except set-ops notes: the listed trailing
  operators are no longer terminal-only; document the `_trailingOps`/`ActiveOps` mechanism, the
  `IsSetOpTerminalOnly` relaxation surface, and the retained deferred set.
- Tests: unit (lowerer trailing-stage order + fall-through to Cardinality; IR `ActiveOps` routing;
  `IsSetOpTerminalOnly`), functional/spec (`NativeSetOpsTests` — Union/Concat `Native == DriverLinq` parity,
  Intersect/Except result-set vs in-memory, seam-discriminating Count/paging, parametrized trailing predicate,
  `NativeOnly` native-proofs, deferred-operator fallback/hard-fail split), flipped Northwind post-composition
  set-op spec overrides.

## Verification

- **Union/Concat parity oracle:** every supported post-composition Union/Concat shape produces the SAME
  result under `Native` and `DriverLinq` (the driver-LINQ path is the established baseline). This is the
  primary correctness bar for Union/Concat.
- **Intersect/Except result-set bar (no oracle):** `Native` produces the correct result set asserted against
  expected in-memory (LINQ-to-Objects) data over: disjoint/overlap/dedup/empty operands × each trailing
  operator, unordered set-equality or with a trailing `OrderBy`.
- **`NativeOnly` proves native:** every supported post-composition shape (all four ops) succeeds under
  `MongoQueryMode.NativeOnly`; every deferred shape (trailing Select/Distinct/GroupBy/SelectMany, chained set
  op) throws.
- **Seam-discriminating tests** (hazard 1): `Count()` and `OrderBy(k).Skip(m).Take(n)` after each of the four
  set ops, asserting the combined-stream result (which differs from a source1-only mis-placement).
- **Deferred-operator split tests** (hazard 2): one trailing deferred operator per fallback family — Union
  falls back gracefully, Intersect hard-fails — under `Native`/`DriverLinq`/`NativeOnly`.
- **Full 3-version `/test-all`** green (EF8/EF9/EF10), plus the `MONGODB_EF_NATIVE_ONLY=1` spec sweep showing
  the net increase in native-covered post-composition set-op tests with zero regressions.

## Follow-ups

- **Slice C:** projected-operand set operations, for all four (and, folded in there, a trailing projection
  `Select` after a set op).
- Trailing `Distinct` and trailing `GroupBy` after a set op (deferred; would need the trailing-op mechanism
  extended to the grouping/distinct terminals — out of scope here).
