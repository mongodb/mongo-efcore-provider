# Native GroupBy aggregation ($group) — design (EF-344 / SP6)

**Date:** 2026-07-08 · **Branch:** `EF-344` (off `86eef4f`, the EF-339/SP5 tip) · **JIRA:** EF-344
**Epic:** EF-322 (native LINQ query rewrite) · **Overview:** `2026-06-23-native-query-provider-overview.md`

> **Reviewer:** read this for the *what* and *why* of the sixth native sub-project. The overview doc's
> §"Sub-projects 2–7" lists #6 as the "remaining operators" grab-bag (GroupBy, SelectMany, set ops,
> Distinct, OfType, VectorSearch, non-canonical Skip/Take). This SP scopes it to the **GroupBy slice
> only** — the single largest coverage bucket — and defers the rest to later sub-projects.

## TL;DR

- Make the native aggregation-pipeline path translate `GroupBy(keySelector).Select(g => <aggregate>)`
  into a native `$group` stage, so grouping-with-aggregates no longer hard-fails translation.
- **Key selectors:** scalar (`g => g.Country`) and composite/anonymous (`g => new { g.Country, g.Year }`).
  Computed keys (`g => g.Date.Year`) fall back.
- **Accumulators:** `Count()`/`LongCount()` (→ `$sum: 1`) and `Sum`/`Average`/`Min`/`Max` over a **plain
  field reference** (→ `$sum`/`$avg`/`$min`/`$max`). Computed operands (`g.Sum(x => x.Qty * x.Price)`)
  fall back — mirrors the EF-336 scalar-aggregate field-ref-only rule exactly.
- **No post-group operators go native.** `Where` (HAVING) / `OrderBy` / `Skip` / `Take` applied *after*
  the aggregate `Select` fall back. A pre-`GroupBy` `Where` stays native (`$match` before `$group`).
- **No `IGrouping` materializer.** Only the flat key+aggregate *projection* shape is native; bare
  `GroupBy(key)` / `GroupBy(key, elementSelector)` returning `IGrouping<TKey,TElement>` sequences fall
  back.
- **Behavior change:** GroupBy currently **hard-throws** in translation. This SP routes unsupported
  group shapes to the normal driver-LINQ **fallback** instead (throwing only under `NativeOnly`), so
  strictly more queries succeed than today.

## Where the native path stands today

`GroupBy` never reaches the native path *or* the driver-LINQ fallback. In
`MongoQueryableMethodTranslatingExpressionVisitor` (QMTEV) it sits in the "bubble through for a better
error" switch arm; `TranslateGroupBy` returns `null` → `NotTranslatedExpression` → the top-level `Visit`
throws `InvalidOperationException` (TranslationFailed). Unlike `Distinct`/`OfType`/set-ops (which produce
a valid `ShapedQueryExpression` with `Route == Fallback` and *do* fall back), GroupBy produces no shaped
query at all, so it hard-fails. There is no `IGrouping` materializer or grouping-result shaper anywhere
in `Query/`.

The `$group` machinery from EF-336 (SP4) is directly reusable but hardcodes a whole-collection group:

- `NativeTranslation/Stages/MongoGroupAccumulatorStage.cs` — a single accumulator over the whole input
  with `_id: null`.
- `MongoPipelineFactory.RenderGroup` — emits `{ $group: { _id: null, <field>: { <acc>: <operand> } } }`.
- `MongoAggregationExpressionRenderer` — renders operand/`$expr`-dialect expressions (reused for the
  `_id` key expression and accumulator operands).
- `MongoSelectLowerer.Lower` (aggregate-terminal region) — the precedent for emitting a terminal
  `$group`/`$count`.

A native `GroupBy` needs a `_id: <keyExpr>` variant plus **multiple** accumulators, and — because the
result is a *sequence* of grouped rows, not a scalar — its own binding, routing, and result shaper
(the EF-336 `MongoCardinality` scalar path with its empty-behavior semantics is *not* reused).

## Scope

**In:**

1. **`GroupBy(key).Select(aggregate)`** translated to `$match → $group → $project`.
2. **Key selectors:** scalar field-ref (`_id: "$Country"`) and composite/anonymous field-refs
   (`_id: { Country: "$Country", Year: "$Year" }`).
3. **Accumulators in the projection:** `g.Key` (and composite-key sub-members `g.Key.Country`),
   `g.Count()` / `g.LongCount()` (→ `$sum: 1`), and `g.Sum` / `g.Average` / `g.Min` / `g.Max` over a
   plain field reference.

**Out (falls back to driver-LINQ; throws under `NativeOnly`):**

- Computed group keys (`g => g.Date.Year`, `g => g.Name.ToUpper()`).
- Computed accumulator operands (`g.Sum(x => x.Qty * x.Price)`).
- Bare `GroupBy(key)` / `GroupBy(key, elementSelector)` returning `IGrouping<TKey,TElement>` sequences
  (no `$push` element materialization, no `IGrouping` shaper).
- Any operator applied **after** the aggregate `Select`: `Where` (HAVING), `OrderBy`/`ThenBy`,
  `Skip`/`Take`.
- Aggregate accumulators beyond the standard set (e.g. `g.Aggregate(...)`, custom).

## Architecture

The translation follows EF Core's own two-step GroupBy contract on the native path (Approach A):
`TranslateGroupBy` produces an intermediate *grouped* `ShapedQueryExpression`, and the subsequent
`TranslateSelect` binds the aggregate projection over that grouped source — exactly how the relational
provider models GroupBy. This keeps the fallback story uniform and leaves a clean seam for a later
post-group (HAVING/OrderBy) follow-on SP.

### 1. `MongoSelectDefinition` — group state

Add dialect-neutral group state to the query IR:

- `GroupKey` — either a single field-ref or an ordered list of named field-refs (composite). Drives
  `$group._id`.
- `GroupAccumulators` — an ordered list of `{ OutputField, Accumulator, Operand }` where `Accumulator`
  is one of `$sum`/`$avg`/`$min`/`$max`/count-as-`$sum:1`, and `Operand` is a field-ref (or the literal
  `1` for count).

Add `NativeRoute.GroupBy` to the authoritative `Route` computed property, checked **after**
`ScalarAggregate` and **before** `Projection` (a grouped query has both group state and a projection;
the group route wins). Any `MarkNotNativelyRepresentable()` call still forces `Fallback`.

### 2. `NativeGroupByBinder` (new) — modeled on `NativeCardinalityBinder`

Two entry points:

- `TryBindGroupKey` — called from `TranslateGroupBy`. Validates the key selector is a scalar field-ref
  or an anonymous/`new`-expression composed entirely of field-refs; records `GroupKey`. Computed or
  otherwise-unsupported keys ⇒ `MarkNotNativelyRepresentable()`.
- `TryBindGroupProjection` — called from `TranslateSelect` when the source is a grouped shaped query.
  Walks the result selector members; each must be `g.Key` (or a composite-key sub-member) or a
  supported `g.<Aggregate>(<field-ref>)`. Maps aggregates to `GroupAccumulators`; records how each
  result member reads back from the `$group` output (`_id`/`_id.<sub>`/accumulator output field).
  Anything else ⇒ `MarkNotNativelyRepresentable()`.

### 3. QMTEV

- `TranslateGroupBy` (currently `=> null`, and its hard-throwing switch arm) is reworked: call
  `TryBindGroupKey`, and return a **grouped** `ShapedQueryExpression` carrying a Mongo grouping-shaper
  marker (the shaper the base class expects between GroupBy and the aggregate Select). On an unsupported
  key it still returns a valid shaped query marked non-native, so the query falls back rather than
  hard-throwing.
- `TranslateSelect` gains a grouped-source branch that delegates to `TryBindGroupProjection` instead of
  the ordinary `NativeProjectionBinder` path.

### 4. Stage IR + renderer

Generalize the EF-336 `$group` emission (either extend `MongoGroupAccumulatorStage` or add a
`MongoGroupStage`) to carry an `_id` key expression and a **list** of accumulators. Extend
`MongoPipelineFactory.RenderGroup` to render `_id` from the key IR (scalar `"$field"` or a nested
document for composite keys) and one output field per accumulator. Key expression and accumulator
operands render through the existing `MongoAggregationExpressionRenderer`.

### 5. Lowerer

`MongoSelectLowerer.Lower` gains a group branch (in the terminal region, beside the EF-336 aggregate
emission) that emits the `$group` from the group state, followed by a `$project` mapping `$group`
output fields to the anonymous/DTO result field names. Canonical stage order: `$match → $group →
$project`.

### 6. Result shaper

The `$group`/`$project` output rows are flat BSON documents. Add a shaper that reads them into the
anonymous/DTO result, modeled on the existing projection DOM shaper
(`MongoProjectionBindingRemovingExpressionVisitor`). Composite-key sub-members read from the `_id`
sub-document; `Count`/`LongCount` deserialize to `int`/`long` respectively. **No** `IGrouping`
materialization (out of scope).

### 7. Routing / fallback

Once binding succeeds and `Route == GroupBy`, `TryBuildNativeFactory` builds the native factory and the
native path is chosen. Unsupported shapes reach `Route == Fallback` and route to driver-LINQ via
`CapturedExpression`, exactly like every other operator; under `MongoQueryMode.NativeOnly` the gate
throws `NativeTranslationNotSupportedException` (the coverage instrument).

## Edge cases

- **Empty result** (no matching rows ⇒ no groups) → empty sequence. A `$group` emits no row for an
  empty group, so there is no per-group empty-behavior concern; EF-336's `EmptyBehavior`/`EmptyValue`
  logic is deliberately **not** reused.
- **Composite key** `_id: { A: "$A", B: "$B" }`; `g.Key.A` in the projection reads `_id.A`.
- **`Count()` vs `LongCount()`** both render `{ $sum: 1 }`; the result type (`int` vs `long`) is handled
  in the shaper's deserialize.
- **Value-converted / non-default `BsonRepresentation` operand fields** carry the same pre-existing,
  shared caveat tracked in **EF-337** (aggregate over such a field can be wrong on *both* native and
  driver-LINQ paths). Not introduced here; documented as a known limitation — reference EF-337 if it
  surfaces, do not attempt a native-only fix in this SP.

## Testing

Per the Query area's testing guidance, **MQL shape alone does not prove native** where the fallback
pipeline is structurally identical — the reliable signal is `MongoQueryMode.NativeOnly` (native-capable
⇒ succeeds; fallback ⇒ throws `NativeTranslationNotSupportedException`).

- **Unit** (`tests/.../UnitTests/Query/NativeTranslation/`): the `$group`/`$project` MQL shape for a
  scalar key + each accumulator; composite key (`_id` sub-document); `g.Key`-only projection;
  count-only; the binder's accept/reject boundaries (computed key, computed operand, post-group op, bare
  `IGrouping` ⇒ reject).
- **Functional** (real DB): end-to-end results parity — correct groups, keys, and aggregate values —
  for scalar and composite keys and each accumulator, tracking and no-tracking.
- **`NativeOnly` proof:** supported group shapes **succeed** under `NativeOnly`; computed key/operand,
  bare `IGrouping`, and post-group `Where`/`OrderBy`/`Skip`/`Take` **throw**.
- **Spec sweep:** `MONGODB_EF_NATIVE_ONLY=1` must show **+passing, zero regressions**. EF8/EF9/EF10
  suites green.

## Delivery

Single squashed commit on branch `EF-344`, stacked on `86eef4f` (EF-339/SP5), one PR-style message
(doubles as the stacked PR description). Subagent-driven development, stopping after every task for
review. Keep an `EF-344-presquash` safety branch until merge. Native becoming the default path for
GroupBy and the changed emitted MQL are implementation details, not breaking changes (per the provider's
versioning rubric) — query results are unchanged; the change from hard-throw to graceful fallback is a
strict improvement.

## Follow-ups (not in this commit)

- **Post-group operators SP** — `Where` (HAVING → `$match` after `$group`), `OrderBy`/`ThenBy`,
  `Skip`/`Take` over the grouped result.
- **Computed keys / computed operands** — extend the key and accumulator-operand rendering through the
  `$expr` aggregation-expression renderer once the computed-expression long tail is broad enough.
- **`IGrouping` materialization** — native `GroupBy(key)` / `GroupBy(key, elementSelector)` returning
  grouped element sequences (`$push` + a new `IGrouping` shaper).
- **Rest of the roadmap's SP6 grab-bag** — `SelectMany`, set operations, `Distinct`, `OfType`/type
  tests, `VectorSearch`, non-canonical `Skip`/`Take` (each its own ticket/SP).
- **EF-337** — the shared value-converted/`BsonRepresentation` aggregate-operand bug (fix in the shared
  layer, not the native path).
