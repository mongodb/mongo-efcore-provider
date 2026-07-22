# Native query: set-op trailing projection — a `Select` after a whole-entity set op (all four) — EF-347

**Ticket:** EF-347 (remaining SP6 relational operators) — set-ops slice C2. Epic EF-322.
(Set-ops shipped so far: Union/Concat `750268a`, Intersect/Except `84e67c7`, post-composition `1e3adc9`.)
**Type:** New native coverage, additive. A terminal anonymous/DTO member-access `Select` composed AFTER a
whole-entity set op currently falls back to driver-LINQ (Union/Concat — correct results, throws only under
`NativeOnly`) or hard-fails in every mode (Intersect/Except — no driver-LINQ oracle). This slice makes the
supported projection shape go native; every unsupported shape keeps its current behavior.

Per the versioning rubric the exact emitted MQL and the internal execution path are not part of the contract,
and the exception type of an unsupported shape is not part of the contract.

**Stacked on:** the native-stack rolling tip `origin/NativeQueryOngoing` = `1e3adc9` (branch off it).

## Position in the larger scope (set-ops A → B → C1/C2)

Set ops decomposed into: slice A (`Intersect`/`Except` whole-entity terminal, `84e67c7`), slice B
(post-set-op composition — Where/OrderBy/paging/aggregates/reducers, `1e3adc9`), and slice C (projection +
set op), which was further split at brainstorming into two stacked sub-slices:

- **C2 (this spec):** a **trailing projection** — a `Select` applied AFTER a whole-entity set op
  (`Union(wholeA, wholeB).Select(x => new {...})`). The `$project` is emitted *after* the set-op stage.
- **C1 (next):** **projected operands** — the projection is *inside* each operand
  (`Set<A>().Select(...).Union(Set<B>().Select(...))`), with the harder correctness story (operands may be
  different collections; the operand-acceptance and same-entity-type gates relax; Intersect/Except source-
  tagging runs over the projected `$$ROOT`). C1 is the last set-ops slice.

C2 is deliberately first: it is the smaller piece, reusing slice B's `TrailingOps` fall-through and the
existing SP3 projection binder + shaper unchanged.

## Background — why C2 is almost free after slice B

A whole-entity set op attaches a `MongoSetOperation` to source1's `Select` and sets `IsSetOp`. Slice B made
the lowerer's set-op block **fall through** (no early return): it emits the set-op stage, then `TrailingOps`,
then continues to the `UnwindSource`/`Grouping`/`Projection`/`Cardinality` blocks. For a set-op query
`UnwindSource`/`Grouping` are null and were skipped; slice B left `Projection` empty. The `Projection` block
(`MongoSelectLowerer.Lower`, stage 6) already emits a `$project` when `Projection.Count > 0`:

```
PipelineOps (source1 pre-set-op) → set-op stage → TrailingOps (slice B) → $project (if Projection populated)
```

So the lowerer **already** emits a trailing `$project` after the set-op stage the moment `Projection` is
populated — no lowerer change is needed. The shaper is also unchanged: `TranslateSelect` builds the SP3
projection shaper by rewriting the selector over source1's whole-entity shaper
(`_projectionBindingExpressionVisitor.Translate`), exactly as for a plain `Set<X>().Select(proj)` — it is
agnostic to the set-op stage that produced the documents.

**What blocks it today** is a single guard. For `Union(A,B).Select(x => new {x.Name})`, `TranslateSelect`'s
non-grouped projection branch reaches the post-terminal guard and marks the query non-native because
`HasTerminalOperator` is true (`IsSetOp`):

```csharp
// MongoQueryableMethodTranslatingExpressionVisitor.TranslateSelect, ~line 253
if (mongoQueryExpression.Select.HasTerminalOperator)
    mongoQueryExpression.Select.MarkNotNativelyRepresentable();
else if (!NativeProjectionBinder.TryPopulateNativeProjection(mongoQueryExpression, selector))
    mongoQueryExpression.Select.MarkNotNativelyRepresentable();
```

That guard exists to stop a projected `Select` after a **GroupBy/Distinct** terminal from appending
field-refs onto an already-populated `Projection`/`Grouping` and emitting a flatten `$project` over fields
that no longer exist after the `$group` (silent nulls). A set-op terminal has no such `Grouping`, and its
post-set-op rows are whole entities of source1's type, so pushing a `$project` down is safe and correct.

## The hazard this slice introduces, and its closure

Relaxing that guard opens the recurring **composition-after-a-new-terminal** seam. After
`Union(A,B).Select(c => new {N = c.Name})`, `Projection` is populated but `IsSetOp` is still true. As defined
in slice B, `IsSetOpTerminalOnly = IsSetOp && !IsGroupBy && !IsDistinct && Grouping == null &&
UnwindSource == null` does **not** inspect `Projection`. So a subsequent operator — `Union(A,B).Select(proj)
.Where(x => x.N == …)` — would see `IsSetOpTerminalOnly == true`, be routed by slice B's relaxed guards into
`TrailingOps`, and resolve `x.N` against the **entity** type. Result: a `$match` emitted *before* the
`$project` (wrong stage placement), or — when a projected alias coincidentally matches an entity property
name sourced from a different field (`new {Name = c.FirstName}` then `.Where(x => x.Name == …)`) — a filter
bound to the wrong field (silent wrong data).

**Closure:** fold `&& Projection.Count == 0` into `IsSetOpTerminalOnly`:

```csharp
internal bool IsSetOpTerminalOnly
    => IsSetOp && !IsGroupBy && !IsDistinct && Grouping == null && UnwindSource == null && Projection.Count == 0;
```

This reads as "a set op is the *only* thing done so far." It is load-bearing in two directions:
- **Enables C2:** when `TranslateSelect` evaluates the relaxed guard, `Projection` is still empty (the
  projection is being populated *now*), so `IsSetOpTerminalOnly` is true and the trailing projection pushes
  down.
- **Closes the seam:** once `Projection` is populated, `IsSetOpTerminalOnly` becomes false, so every
  post-projection operator (a `Where`/`OrderBy`/paging/aggregate/reducer via slice B's `!IsSetOpTerminalOnly`
  guards, a second `Select` via this same `TranslateSelect` guard) is rejected → Union/Concat fall back
  gracefully, Intersect/Except hard-fail in every mode. Post-projection composition is deferred, not native.

Slice B shapes never carry a `Projection` (whole-entity set op + slot/aggregate/reducer), so `Projection.Count
== 0` always holds for them and their behavior is unchanged.

## Design (Approach A)

### IR — `Expressions/MongoSelectDefinition.cs`
Add `&& Projection.Count == 0` to `IsSetOpTerminalOnly` (above). No other IR change.

### QMTEV — `TranslateSelect` guard
Relax the post-terminal guard in the non-grouped projection branch:
```csharp
if (mongoQueryExpression.Select.HasTerminalOperator && !mongoQueryExpression.Select.IsSetOpTerminalOnly)
    mongoQueryExpression.Select.MarkNotNativelyRepresentable();
else if (!NativeProjectionBinder.TryPopulateNativeProjection(mongoQueryExpression, selector))
    mongoQueryExpression.Select.MarkNotNativelyRepresentable();
```
For a set-op-only terminal this lets `TryPopulateNativeProjection` populate `Projection` (member-access
anonymous/DTO leaves → field-refs against source1's entity type — correct, since post-set-op rows are whole
entities of that type). `Route` resolves to `Projection`. The `IsSingleLevelCollectionIncludeSelector &&
HasTerminalOperator` guard just above (the hoisted-shared-Include case, `Union(A,B).Select(x => Include(x))`)
is **not** relaxed — a trailing Include after a set op stays a graceful fallback, unchanged.

### Lowerer — `NativeTranslation/MongoSelectLowerer.cs`
No logic change. Update the slice-B comment in the set-op block (the one noting "Projection ... are all empty
here ... a future slice ... MUST revisit this precedence") to state that a trailing `Projection` after a set
op is now intentional and correctly emitted by the `Projection` block after the set-op stage and `TrailingOps`.

### Shaper
Unchanged. The SP3 projection shaper reads the top-level result aliases from the projected documents; the
set-op stage that produced them is irrelevant to it.

### Fallback / no-baseline — consistent with slices A/B
`TryTranslateSetOperation`'s kind-conditional decline is untouched. An unsupported trailing projection
(bare-scalar — SP3 does not push bare scalar down, so `TryPopulateNativeProjection` returns false; computed
leaf; entity reference) marks the query non-native → Union/Concat fall back gracefully (correct results,
throw only under `NativeOnly`), Intersect/Except hard-fail in every mode (their captured chain reaches
driver-LINQ, which cannot translate the cross-view Intersect/Except → throws). Post-projection composition
declines the same way, via the `Projection.Count == 0` closure.

## Scope (decided at brainstorming)

**In:** a terminal **anonymous-type / DTO member-access** `Select` (the SP3 projection surface — leaves are
all top-level member accesses) composed after a whole-entity set op, for **all four** set ops. May follow
slice-B trailing `Where`/`OrderBy`/paging (e.g. `Union(A,B).Where(p).Select(proj)`).

**Out (deferred — unchanged behavior):**
- **Bare-scalar** trailing projection (`Union(A,B).Select(c => c.Name)`) — SP3 never pushes a bare scalar down.
- **Computed-leaf** / entity-reference trailing projection.
- **Post-projection composition** — any operator after the trailing projection (`Union(A,B).Select(proj)
  .Where/.OrderBy/.Count/.Select`), closed by the `Projection.Count == 0` addition.
- **Projected-operand** set ops (`Select(...).Union(Select(...))`) — slice C1.

## Correctness hazards (explicitly guarded / tested)

1. **Composition-after-projection** (the primary hazard): closed by `Projection.Count == 0` in
   `IsSetOpTerminalOnly`. Tested by a post-projection `Where`, aggregate, and a second `Select` after a
   trailing projection — Union falls back gracefully, Intersect hard-fails.
2. **Set-op semantics vs projection order:** the set op operates on whole entities *before* the `$project`.
   For `Union`, whole-entity dedup happens over `$$ROOT` before the projection, so two distinct entities that
   project to the same value both survive (a duplicate projected value) — matching BCL `Union(...).Select(...)`
   (the dedup is over entities, not projected values). Tested with a projection that collapses distinct
   entities to equal projected values.
3. **Wrong-scope binding:** the trailing projection's members resolve against source1's entity type (correct —
   post-set-op rows are whole entities of that type). Verified by a projection that renames/reorders members.
4. **Guard precision:** only a set-op-**only** terminal is relaxed; a GroupBy/Distinct/SelectMany terminal (or
   a trailing Include) still falls back. The hoisted-shared-Include guard is untouched.

## Files touched
- `src/…/Query/Expressions/MongoSelectDefinition.cs` — `IsSetOpTerminalOnly` gains `&& Projection.Count == 0`.
- `src/…/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs` — relax the `TranslateSelect`
  non-grouped projection post-terminal guard with `&& !IsSetOpTerminalOnly`.
- `src/…/Query/NativeTranslation/MongoSelectLowerer.cs` — comment-only: revise the slice-B set-op-block note.
- `src/…/Query/AGENTS.md` — document trailing-projection-after-set-op as native, the `Projection.Count == 0`
  seam closure, and the deferred set (bare-scalar/computed/post-projection composition; projected operands = C1).
- Tests: functional `NativeSetOpsTests` (Union/Concat trailing projection Native==DriverLinq parity;
  Intersect/Except trailing projection result-set + `NativeOnly`; `Where`-then-`Select` compose; the three
  hazard tests above; deferred bare-scalar/computed fallback split); flip BOTH slice-B deferred-projection
  tests that now go native — `Select_after_union_falls_back_gracefully` (→ Union parity) and
  `Select_after_intersect_hard_fails_in_every_mode` (→ Intersect result-set + `NativeOnly`); Northwind spec
  re-baseline.

## Verification
- **Union/Concat parity oracle:** every supported trailing-projection Union/Concat shape produces the same
  result under `Native` and `DriverLinq`.
- **Intersect/Except result-set bar (no oracle):** `Native` produces the correct set asserted against expected
  in-memory (LINQ-to-Objects) data, over disjoint/overlap/dedup/empty operands × the trailing projection.
- **`NativeOnly` proves native:** supported trailing projections succeed under `NativeOnly`; every deferred
  shape (bare-scalar, computed leaf, post-projection composition, second projection) throws under `NativeOnly`
  and — for Intersect/Except — in every mode.
- **Full 3-version `/test-all`** green (EF8/EF9/EF10), plus the `MONGODB_EF_NATIVE_ONLY=1` set-ops spec sweep
  showing the net increase with zero regressions.

## Follow-ups
- **Slice C1:** projected-operand set operations for all four (operand-acceptance relaxation, different-
  collection operands, projected `$$ROOT` source-tagging for Intersect/Except). The final set-ops slice.
- Post-projection composition after a set op, and bare-scalar trailing projection, if ever wanted (both
  deferred here).
