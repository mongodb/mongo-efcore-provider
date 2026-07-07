# Native query: projection pushdown (server-side `$project`) — design

**Date:** 2026-07-05 · **Branch:** `EF-331` (stacked on `EF-330` → `EF-329` → `EF-323`) · **JIRA:** EF-331
**Program:** native LINQ query provider rebuild (epic EF-322), sub-project 3 (SP3).
**Program docs:** `2026-06-23-native-query-provider-overview.md` / `2026-06-23-native-query-provider-design.md` (§ *Sub-projects 2–7*, item 3).

> **Reviewer:** this is a docs-only design PR. Read it for the *what* and *why*; it goes through
> design-review sign-off before any implementation, per the project's design-review-PR process.

## TL;DR

- Today every projected query (`Select` producing a non-entity result) falls back to the driver's
  LINQ V3 provider. SP3 makes the **native pipeline emit `$project` itself** for the common
  **terminal push-down** projection shapes, shapes the result client-side, and retires the
  `ExecuteProjectedQuery` cutout for those shapes.
- Scope: **terminal anonymous-type and DTO/member-init** projections whose every leaf is a **plain
  member access** (`Select(x => new { x.Name, x.Age })`). Everything else — bare scalar, computed
  leaves, mixed (entity-reference) projections, non-terminal projections — keeps falling back to
  driver-LINQ. **Zero regressions.**
  - *Scope narrowed during planning:* the earlier draft also listed bare scalar (`x => x.Name`) and
    computed leaves. Both were deferred to follow-ons: computed leaves aren't captured as single
    aliased projections by the binding visitor today (they're recombined client-side), and a bare
    scalar resolves to a null alias that the reused DOM shaper treats as "whole document." Pulling
    either in would mean modifying shared binding/shaper code; out of scope for this focused slice.
- Representation: add a `Projection` list to `MongoSelectDefinition` (the native scalar IR extracted
  in EF-330), mirroring relational `SelectExpression.Projection`. Lower it to a new `$project` stage;
  render each expression with the existing `MongoAggregationExpressionRenderer`; materialize via the
  existing DOM `MongoProjectionBindingRemovingExpressionVisitor`.
- All-or-nothing per query: one untranslatable leaf ⇒ the whole query falls back. No partial pushdown.

## Current state (what SP3 changes)

Projected queries route through `MongoShapedQueryCompilingExpressionVisitor.VisitProjectedQuery`
(`shapedQueryExpression` whose projected type is **not** an entity type). There are two existing
routes, **both driver-LINQ**:

1. **Push-down path** — `ProjectionAnalyzer.CanPushDown(shaper) == true` (no entity references, no
   untranslatable string-as-`IEnumerable` operators): the projection is handed wholesale to the
   driver's LINQ V3 provider via `ExecuteProjectedQuery<TSource,TResult>`, which builds the `$project`
   and deserializes into the projected CLR type.
2. **Mixed path** — projection contains entity/navigation references LINQ V3 can't express: the
   `Select` is stripped from the captured chain, the driver returns full `BsonDocument`s, and the
   client-side `MongoMixedProjectionBindingRemovingExpressionVisitor` shaper applies the projection.

Under `MongoQueryMode.NativeOnly`, **any** projected query throws
`NativeTranslationNotSupportedException` (it is outside the native parity slice — a coverage failure).

SP3 introduces a **third route**: a native `$project` pipeline + DOM projection shaper for the
push-down shapes whose leaves are all natively translatable. The mixed path and the fallback for
untranslatable push-down shapes are unchanged.

## Scope

**In:**
- **Terminal** projections — `Select` is the outermost operator, optionally under a single no-arg
  cardinality reducer (`First`/`Single`/…), exactly as `StripPushedDownSelect` already recognizes.
- **Anonymous-type** (`Select(x => new { x.Name, x.Age })`) and **DTO / member-init**
  (`Select(x => new Dto { Name = x.Name, City = x.City })`) projections **whose every leaf is a plain
  member access**. The member name is the output alias (matching what the DOM shaper reads by).

**Out (deferred to follow-ons — still driver-LINQ fallback):**
- **Bare scalar** projections (`Select(x => x.Name)`) — resolve to a null projection alias that the
  reused DOM shaper treats as the whole document; supporting them means a synthetic alias plus a change
  to the shared `MongoProjectionBindingRemovingExpressionVisitor`. Deferred.
- **Computed / method-call** leaves (`new { Total = x.Price * x.Qty }`, string ops, etc.) — the binding
  visitor decomposes these into per-operand reads recombined client-side, so there is no single aliased
  projection to render into `$project`; capturing them requires modifying the shared binding visitor.
  Deferred (a natural next slice, reusing the EF-329 `MongoAggregationExpressionRenderer`).
- **Mixed** projections containing whole-entity / navigation references (`ProjectionAnalyzer`
  continues to route these to the mixed client-side shaper).
- **Non-terminal** projections — operators composed *after* the `Select` (`.Select(...).Where(...)`,
  `.Select(...).OrderBy(...)`) that reference projected aliases. These need multi-stage
  alias resolution and `$project`-vs-`$match`/`$sort` re-ordering; deferred. (Post-`Select` `Where`/
  `OrderBy` fall back naturally — they can't translate against the entity type.)
- **Streaming** projection materialization (kept DOM-only here; the direct stream→POCO end state is
  SP7 — materializer perf).
- **Scalar cardinality** (`Count`/`First`/`Any`/aggregates as `$count`/`$group`) — SP4.

**All-or-nothing per query.** If any projected leaf is not natively translatable, or the shape is
mixed / non-terminal, the projection slot is left empty, `IsNativeRepresentable` is `false`, and the
existing driver-LINQ fallback fires. There is no partial (per-leaf) pushdown — worst case is a missed
optimization plus fallback, never a wrong result.

## Architecture

The pieces mirror how EF-323/EF-329 built the filter/sort/paging path: populate a dialect-neutral IR
slot in the QMTEV, lower it to a typed stage, render the stage to BSON once at compile time, and shape
the result client-side.

### 1. IR representation — `MongoSelectDefinition.Projection`

Add a projection list to `MongoSelectDefinition` (the native scalar IR, EF-330), symmetric with
relational `SelectExpression.Projection`:

- A new lightweight `internal sealed` value type `MongoProjection` holding `(string Alias,
  MongoExpression Expression)` — the dialect-neutral projected expression plus its output element
  name.
- `MongoSelectDefinition` gains `IReadOnlyList<MongoProjection> Projection` (get-only, backed by a
  private list) and an `AddProjection(string alias, MongoExpression expression)` mutator the QMTEV
  calls (same mutate-in-place discipline as `AddPredicateConjunct`/`AppendOrdering`).
- Empty `Projection` ⇒ no `$project` stage ⇒ the current whole-entity behavior is preserved byte-for-byte
  (the entity path never populates it).

`MongoProjection` / the projection list stay **dialect-neutral** — no BSON, no dialect choice — exactly
like the rest of the `MongoExpression` tree. Dialect is the renderer's concern (below).

### 2. Translation flow — QMTEV population + gate routing

- **Population.** When the QMTEV reaches a terminal `Select`, it walks the projection shaper tree
  (the same tree `ProjectionAnalyzer` inspects). For each projected leaf it calls
  `MongoExpressionTranslator` (the existing `TryTranslate`/`TryTranslateField` used for predicate and
  key-selector bodies — the **acceptance set is unchanged**) to produce a `MongoExpression`, assigns
  a stable output alias, and records it via `Select.AddProjection`. The corresponding shaper leaf is
  rewritten to a `ProjectionBindingExpression` bound to that alias so the DOM shaper (step 4) can read
  it back.
  - Alias scheme: use the projected member/element name where one exists (anonymous/DTO members carry
    a name); synthesize a deterministic alias (e.g. `_p0`, `_p1`) for positional/scalar leaves.
    Aliases must be stable across recompilations of the same query shape (EF caches compiled queries
    by tree shape) and must not collide with `_id` unless `_id` is deliberately projected.
- **Bail-out.** If any leaf fails to translate, or the shape is mixed / non-terminal, the QMTEV leaves
  `Select.Projection` empty and sets `IsNativeRepresentable = false`. No projection state is
  half-populated.
- **Gate routing (`VisitProjectedQuery`).** Add a **native branch** ahead of the existing push-down /
  mixed branches: when `mongoQueryExpression.Select.Projection` is non-empty **and**
  `IsNativeRepresentable`, compile the native pipeline (lower + render + `MongoPipelineFactory`) plus a
  **DOM projection shaper**, instead of `ExecuteProjectedQuery`. Otherwise the existing routing is
  unchanged:
  - `CanPushDown` + translatable ⇒ **new native branch**.
  - `CanPushDown` + *not* translatable ⇒ existing `ExecuteProjectedQuery` driver-LINQ path.
  - not `CanPushDown` (mixed) ⇒ existing mixed client-side shaper.
  - `NativeOnly` + projected + not natively translatable ⇒ still throws (coverage signal), exactly as
    today.

  Because `VisitProjectedQuery`'s projected type is non-entity, this reuses `CompileShapedQuery`'s
  native-pipeline plumbing but supplies the projection binding-remover as the shaper factory.

### 3. Lowering & rendering

- **Stage IR.** New `MongoProjectStage` (typed IR under `NativeTranslation/Stages/`) carrying the
  ordered `MongoProjection` list. `MongoSelectLowerer` appends it in **canonical position — last**:
  `$match → $sort → $skip → $limit → $lookup/$unwind → $project`. Terminal-only scope guarantees the
  projection is the final logical operation, so last is always correct; no re-ordering vs. `$match`/
  `$sort` is needed (that's the deferred non-terminal case). The lowerer stays BSON-free.
- **Rendering.** `MongoPipelineFactory.Create` walks the new stage into
  `{ $project: { <alias>: <expr>, …, _id: 0 } }`, rendering each `MongoProjection.Expression` with the
  existing **`MongoAggregationExpressionRenderer`** (aggregation-expression dialect: field refs as
  `"$elementName"`, `$add`/`$eq`/… operators). `_id: 0` is emitted to suppress the default `_id`
  unless `_id` is among the projected aliases. Parameters flow through the shared `PlaceholderTable`
  exactly as in predicates, so the B2 compile-once / bind-per-execution template holds for projections
  too.
  - Rationale for the aggregation dialect (not the `$match`/query dialect): `$project` values are
    aggregation expressions; `MongoAggregationExpressionRenderer` is already the renderer for that
    dialect and already handles field refs, arithmetic, and placeholder-bound parameters. No new
    renderer is introduced.

### 4. Materialization — DOM projection shaper (reuse)

Reuse the existing **`MongoProjectionBindingRemovingExpressionVisitor`**. The `BsonDocument`s the
cursor returns are now keyed by our projection aliases; the binding-remover rewrites each
`ProjectionBindingExpression` into a concrete read of its alias from the `BsonDocument` and
reconstructs the `NewExpression` / `MemberInit` / scalar result. This is the same DOM shaper the entity
path uses, driven by alias/index rather than entity element names.

No streaming path in SP3 — an eligible projection shape still materializes via the DOM shaper over the
(native) `BsonDocument` pipeline. Direct stream→POCO for projections is SP7.

## Interaction & correctness notes

- **Whole-entity path untouched.** The entity path never populates `Select.Projection`, so its lowering
  and rendering are byte-for-byte unchanged. Projection is purely additive.
- **Parameterized correctness (B2).** A projected computed expression referencing a captured
  variable (`Select(x => x.Price * factor)`) binds through `PlaceholderTable` like any predicate
  parameter — the template is rendered once and re-bound per execution. New unit tests must prove a
  single compiled projection template binds correctly across executions with different parameter values
  (the cache-correctness case B2 introduced).
- **EF query cache.** Aliases and rendered `$project` shape are part of the compiled query keyed by
  tree shape; they must be deterministic across recompilations of the same shape.
- **Native-path proof.** Per the Query area's testing rule, the rendered MQL shape does **not** by
  itself prove a query went native. Native-vs-fallback is asserted via `NativeOnly` (succeeds when
  native-capable; throws otherwise) and via the `MONGODB_EF_NATIVE_ONLY=1` spec coverage instrument.

## Verification / success bar

1. **Zero regressions** — full FunctionalTests + SpecificationTests green in `Native` mode on **EF10
   and EF8**.
2. **Coverage grows, none shrink** — the `MONGODB_EF_NATIVE_ONLY=1` spec pass-set grows (projections
   that previously threw now run native); no previously-passing spec regresses.
3. **New unit tests** asserting rendered `$project` MQL for each shape (scalar / anonymous / DTO /
   computed), **including parameterized binding across multiple executions with different values**.
4. **Native-path assertions** via `NativeOnly` succeed/throw for representative in-scope (succeed) and
   out-of-scope (throw / fall back) projection shapes.

## Non-goals (explicit)

- Mixed / entity-reference projections (stay on the mixed client-side shaper).
- Non-terminal projections (operators after `Select`).
- The computed operator long tail (`ToUpper`/`Substring`/date-parts/`Math.*`/casts).
- Streaming projection materialization (SP7).
- Scalar cardinality — `Count`/`First`/`Any`/aggregates (SP4).
- No public-surface change: all new types (`MongoProjection`, `MongoProjectStage`, the
  `MongoSelectDefinition.Projection` members) are `internal`; switching internal query representation
  and which path a supported query takes is not a break (AGENTS.md versioning rubric).

## Sub-project sequencing

Stacked branch `EF-331` on `EF-330` (which is on `EF-329` → `EF-323`). Rebase/retarget as the stack
merges to `main`. SP4 (scalar cardinality) stacks on SP3.
