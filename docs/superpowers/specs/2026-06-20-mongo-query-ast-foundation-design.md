# Mongo query AST foundation — design

**Date:** 2026-06-20
**Status:** Approved; pending implementation plan
**Branch:** `EF-322-native-query` (off `main`) · **JIRA:** EF-322
**Overview / review entry point:** `2026-06-20-mongo-query-ast-foundation-overview.md` — read that for
the terse *what / why*; this doc is the full *how*.
**Program:** sub-project 1 of the ground-up native LINQ query rebuild. See
`2026-06-23-native-query-provider-design.md` for the program-level decision, the keep-vs-rebuild
inventory, and the class-(c) intrinsic-limits reconnaissance. This spec implements that design's
"Sub-project 1: AST foundation" in full (the canonical AST + pipeline generator, the slot
population that feeds it, and the compile-time gate that selects it).

## Goal

Replace the *translation* half of the Query subsystem with a real, canonical MongoDB query AST and a
pipeline generator that consumes it — at **parity** with what the spike already runs natively
(single-collection filter / sort / paging + single-level reference Include over whole-entity
results). The driver-LINQ path remains the gated fallback for everything outside that slice. The
materialization half (streaming materializer, DOM shaper), the query-mode config option (the gate),
and the test/benchmark rig are kept.

The success bar is **zero regressions** and **no shrink** in native strict-mode coverage: the same
query shapes that go native today must still go native, now via the structured AST rather than by
re-walking the captured LINQ chain.

## Why this shape (decisions taken during brainstorming)

The current native path (`MongoPipelineTranslator` / `MongoPredicateTranslator`) **re-walks the raw
captured LINQ `MethodCallExpression` chain** (`MongoQueryExpression.CapturedExpression`) on every
execution, because `MongoQueryableMethodTranslatingExpressionVisitor` returns `null` for
`Where`/`OrderBy`/`Skip`/`Take`/… and never structures them. Translation logic is therefore
duplicated (native re-walker *and* the driver-LINQ re-walker) and the predicate translator covers
only a tiny operator slice. The foundational fix is to **populate a structured AST in the QMTEV**
(where it returns `null` today) and have the generator consume *that*.

Two architectural decisions were taken explicitly:

- **AST structural model = A3 (hybrid).** A logical-slot `MongoSelectExpression` (EF
  `SelectExpression`-aligned, so the team's instincts transfer and the future operator set has a
  home) that **lowers to** an explicit typed stage-list IR (`MongoStage[]`, mirroring MongoDB's real
  pipeline), which then **renders to** `BsonDocument[]`. The Mongo-specific concerns —
  canonicalization, the two BSON dialects (query/match language vs aggregation-expression `$expr`),
  and future pushdown — live at the lowering boundary, not smeared across the visitor or the
  renderer. Rejected: A1 (slots rendered straight to BSON — less separation for the dialect concern)
  and A2 (a bare stage-list AST — furthest from EF's mental model; projection/cardinality semantics
  must be re-derived).

- **Pipeline generation timing = B2 (compile-time template + per-execution bind).** Because the AST
  is built once at compile time, lower/render it **once** into a cached pipeline *factory* whose
  parameter placeholders are bound to live values per execution. This moves translation off the hot
  path (a real slice of the perf headline) and is EF-relational-idiomatic. Rejected: B1
  (per-execution regeneration — parity with the spike, but re-pays generation cost every run).

A consequence of B2: the native-vs-driver decision **moves to compile time** and becomes
deterministic (on `IsNativeRepresentable` + lowering/rendering success), replacing the spike's
per-execution try/catch in `TranslateQuery`. Strict mode still surfaces gaps — just earlier.

## Architecture

Compile-time flow (new types marked **[NEW]**; kept types marked *(kept)*):

```
QMTEV  (MongoQueryableMethodTranslatingExpressionVisitor)
   │   Translate{Where,OrderBy,ThenBy,Skip,Take,Select,Join…} POPULATE slots
   │   (instead of returning null); STILL captures the raw chain for fallback
   ▼
MongoSelectExpression           [NEW] canonical AST (logical slots); replaces the shallow
   │                                  MongoQueryExpression as the structured IR
   │   MongoSelectLowerer        [NEW]
   ▼
IReadOnlyList<MongoStage>       [NEW] typed stage IR (Match/Sort/Skip/Limit/Lookup/Unwind)
   │   MongoQueryLanguageRenderer [NEW] (+ $expr renderer seam, stubbed)
   ▼
MongoPipelineFactory           [NEW] the B2 template: rendered stages + placeholder table
   │   per execution: factory.Build(parameterValues) → BsonDocument[]
   ▼
MongoClientWrapper.Execute  (Storage, unchanged) → IAsyncCursor → streaming/DOM shaper (kept)
```

### New types (all under `src/MongoDB.EntityFrameworkCore/Query/`)

`NativeTranslation/` is promoted from spike scaffolding to the real translator home.

- `Expressions/MongoSelectExpression` — AST root with logical slots (replaces `MongoQueryExpression`
  as the structured IR).
- `Expressions/MongoExpression` + subtypes (`MongoFieldExpression`, `MongoConstantExpression`,
  `MongoParameterExpression`, `MongoBinaryExpression`, `MongoUnaryExpression`) — the
  `SqlExpression`-analog hierarchy, **dialect-agnostic**.
- `Expressions/MongoOrdering` — `(MongoExpression keySelector, bool ascending)`.
- `NativeTranslation/Stages/Mongo{Match,Sort,Skip,Limit,Lookup,Unwind}Stage` — the typed stage IR.
- `NativeTranslation/MongoSelectLowerer` — `MongoSelectExpression → MongoStage[]`.
- `NativeTranslation/MongoQueryLanguageRenderer` — `MongoExpression → $match BSON` (query/match
  dialect). The `$expr` (aggregation-expression) renderer is a stubbed seam.
- `NativeTranslation/MongoExpressionTranslator` — EF predicate/key-selector body → `MongoExpression`
  (replaces the body-walking half of `MongoPredicateTranslator`).
- `NativeTranslation/MongoPipelineFactory` — holds rendered template + placeholder metadata;
  `Build(parameterValues) → BsonDocument[]`.

### Kept / superseded

- *Kept unchanged:* the streaming materializer (`MongoStreamingEntityMaterializerRewriter`,
  `BsonRowReader`, `StreamingEligibility`), the DOM shaper, the query-mode mechanism and its config
  option (`Native` / `DriverLinq` / `NativeStrict`), the `DispatchingQueryingEnumerable` dual-shaper,
  and Storage's `MongoClientWrapper.Execute`.
- *Superseded and removed once the AST covers their slice:* `MongoPipelineTranslator` and
  `MongoPredicateTranslator` (the per-execution chain re-walkers). The lowerer + renderer +
  `MongoExpressionTranslator` replace them.
- *Kept as fallback until full parity (later specs):* `MongoEFToLinqTranslatingExpressionVisitor`
  (+`.LeftJoin.cs`) — the driver-LINQ bridge — and the join-shape reconstruction in
  `MongoQueryExpression.Lookup.cs` (`InnerCollections`, `UsesDriverJoinFields`,
  `GetStreamingReferenceLookups()`), which still feeds the foundation's `Lookups` slot (see
  *Include handling* below). Both are deleted in the Includes sub-project, not here.

### Relationship to the driver's AST (and why we don't reuse it)

The MongoDB C# driver's LINQ v3 provider has its own internal query AST
(`MongoDB.Driver.Linq.Linq3Implementation.Ast` — `AstStage` / `AstFilter` / `AstExpression` / …). We
do **not** build on it:

- It is **internal** to the driver — not public API, so the provider cannot take a supported
  dependency on it.
- It is the very thing this rebuild decouples from. The driver's LINQ provider caps conformance
  because it was not written to EF Core semantics (see the program design's *Why this rebuild*);
  adopting its AST would re-import those semantics and operator-coverage limits — defeating the
  purpose.

So the hierarchy here is bespoke and modelled on **EF Core's relational `SqlExpression`**, not on the
driver. What we *do* reuse is the driver's **low-level BSON layer**: the renderer's output is raw
`BsonDocument[]` aggregation stages (`MongoDB.Bson` types — `BsonDocument`, `BsonValue`,
`IBsonSerializer`, `IBsonReader`), executed via `IMongoCollection<…>.Aggregate`. That raw-BSON
pipeline is the stable, public boundary: **our AST → `BsonDocument[]` → `Aggregate`.** The driver is
used as a BSON/cursor/protocol library, never as the translation engine.

## AST node taxonomy

### `MongoSelectExpression` (logical slots; built in the QMTEV)

```
MongoCollectionExpression        Source       // root collection (kept type). Generalizing to a source
                                              //   abstraction for pushdown/nesting is deferred.
MongoExpression?                 Predicate    // conjunction of Where bodies
IReadOnlyList<MongoOrdering>     Orderings    // ThenBy order preserved
MongoExpression?                 Limit        // Take
MongoExpression?                 Offset       // Skip
ProjectionMapping               Projection   // carried; client-side at parity (no $project yet)
IReadOnlyList<LookupExpression>  Lookups      // single-level reference Includes (kept type); at the
                                              //   foundation, fed by the kept reconstruction — see "Include handling"
Expression?                      CapturedExpression  // raw LINQ chain, retained for the driver-LINQ
                                              //   fallback during the transition; retired at full parity
bool                            IsNativeRepresentable  // false ⇒ compile-time fallback to driver path
```

`MongoSelectExpression` is the evolution of today's `MongoQueryExpression`: it keeps
`CapturedExpression` and the `ProjectionMapping` plumbing (so the existing fallback and shaper
binding keep working) and *adds* the structured slots. "Replaces as the structured IR" means the
slots — not the captured chain — become the source of truth for the native path.

### `MongoExpression` (the `SqlExpression`-analog; dialect-agnostic)

- `MongoFieldExpression(IProperty property, string elementName)` — element name via
  `IProperty.GetElementName()` (same source as today).
- `MongoConstantExpression(object? value, IProperty? forSerialization)` — literal; serialized through
  the property's `IBsonSerializer` for correct BSON type/representation. Baked into the template at
  compile time (never a placeholder).
- `MongoParameterExpression(string name, IProperty? forSerialization)` — **the B2 placeholder**;
  bound per execution.
- `MongoBinaryExpression(MongoBinaryOperator op, MongoExpression left, MongoExpression right)` — parity
  set: `==, !=, <, <=, >, >=, &&, ||`.
- `MongoUnaryExpression(MongoUnaryOperator op, MongoExpression operand)` — `Not`, bare-bool.

### `MongoStage` (typed IR; 1:1 with a rendered pipeline document)

`MongoMatchStage(MongoExpression)`, `MongoSortStage(IReadOnlyList<MongoOrdering>)`,
`MongoSkipStage(MongoExpression)`, `MongoLimitStage(MongoExpression)`, `MongoLookupStage(…)`,
`MongoUnwindStage(…)`.

Canonical lowering order: `$match → $sort → $skip → $limit → $lookup/$unwind`. Any operator arriving
out of canonical order (e.g. `Where` after `Take`) sets `IsNativeRepresentable = false` at parity;
pushdown-into-subquery is the documented extension and is deferred. This reproduces the spike's
current native acceptance set exactly, now via structured slots.

## Population in the QMTEV

The `Translate*` overrides that return `null` today become slot-populators:

| Override | Action |
|---|---|
| `TranslateWhere` | translate predicate body via `MongoExpressionTranslator` → `MongoExpression`; AND-combine into `Predicate`. Un-translatable node ⇒ `IsNativeRepresentable = false`, return `source` unchanged. |
| `TranslateOrderBy` / `TranslateThenBy` (+ descending) | append `MongoOrdering(keySelector, ascending)`; `OrderBy` resets the list, `ThenBy` appends. |
| `TranslateSkip` / `TranslateTake` | set `Offset` / `Limit` to a `MongoConstant`/`MongoParameter`; enforce canonical order (Skip-before-Take, single each) else not-representable. |
| `TranslateSelect` | pure entity-materialization Select ⇒ no-op (full docs returned, client shaper). A *projecting* Select ⇒ `IsNativeRepresentable = false` (projection pushdown deferred; driver path handles it, as today). |
| Join / Include | kept as-is for the foundation (see *Include handling* below): the spike's join reconstruction feeds the `Lookups` slot; single-level reference lookups go native, anything else ⇒ not-representable. |

**Coexistence rule (critical):** the QMTEV *always still captures the raw chain*
(`CapturedExpression`) regardless of slot population, so the driver-LINQ fallback remains intact.

### Include handling (deferred clean-up)

Include is **kept as-is, not rebuilt** at the foundation — a deliberate scoping choice. The spike does
not populate `Lookups` cleanly in the QMTEV: `TranslateJoinCore` records the driver-join shape
(`InnerCollections`, `UsesDriverJoinFields`), and the reference lookups are *synthesized* from that
decision by `MongoQueryExpression.GetStreamingReferenceLookups()` at translation time. That
reconstruction is scar tissue the program design marks for deletion — but deleting it cleanly requires
populating `Lookups` structurally from EF's Include/navigation metadata, which is the **Collection
Includes sub-project's** job, not the foundation's.

So for the foundation: the `Lookups` slot is fed by the existing `GetStreamingReferenceLookups()` /
`InnerCollections` reconstruction (kept alongside the driver-LINQ fallback plumbing), and the lowerer
consumes the resulting `Lookups`. This holds the spike's single-level-reference-Include acceptance set
exactly, with **zero new Include work**. Replacing the reconstruction with structural `Lookups`
population — and then deleting `GetStreamingReferenceLookups()` / `UsesDriverJoinFields` / the
`_outer`/`_inner` reconciliation — is deferred to the Includes sub-project.

The set of "un-translatable" nodes is exactly where the spike falls back today (nullable-equality,
numeric casts on the member side, method calls, dictionary/list access, composite-PK member access,
unserializable values). Element-name resolution and serializer-based value coercion are carried over
verbatim from `NativeExpressionHelpers` / `MongoPredicateTranslator` — they are already correct. Only
*where* the native/driver decision is made moves (compile-time slot population vs per-execution chain
re-walk); the **acceptance set is unchanged**.

## Lowering, rendering & the dialect boundary

- **`MongoSelectLowerer`**: `MongoSelectExpression → MongoStage[]`, reading slots in canonical order,
  dropping empties (no predicate ⇒ no `$match`). Lookups append `$lookup` + `$unwind`: the kept
  `NativeLookupStages` *emission* logic (the `$lookup`/`$unwind` BSON shape + the reference-only /
  no-pipeline-stages / no-`_lookup_`-localField guards) moves here. At the foundation its input is
  still the kept `GetStreamingReferenceLookups()` reconstruction (see *Include handling*); the Includes
  sub-project swaps that input for the structurally-populated `Lookups` slot.
- **`MongoQueryLanguageRenderer`**: `MongoExpression → BSON` in the **query/match** dialect
  (`{field:{$gt:v}}`, `$and`/`$or`, bare-bool, `$ne`). This is the only dialect parity needs
  (`$match` only). The AND-merge / field-collision logic from `MongoPredicateTranslator.CombineAnd`
  (field merge, operator-doc merge, `$and` fallback on collision) carries over.
- **`$expr` (aggregation-expression) renderer**: a stubbed type that throws "not yet implemented",
  wired into the lowerer's dialect-selection seam. Establishes the boundary without building it — the
  entry point for the predicate-breadth spec.

Dialect choice (which target a given `MongoExpression` renders to) is made in the lowerer, never in
the QMTEV or the node types — nodes stay dialect-neutral.

## Parameter binding (B2 mechanics)

- Rendering walks the stage IR once, emitting `BsonDocument`s with **placeholder sentinels** where a
  `MongoParameterExpression` sits, and records `(location-in-pipeline, parameter name, serializer)`
  entries in a placeholder table.
- The result is a `MongoPipelineFactory` carrying the rendered template + placeholder table, produced
  **once at compile time** and captured into the compiled shaper alongside today's
  `Expression.Constant(...)` artifacts.
- Per execution, `factory.Build(parameterValues)` clones the template and substitutes each
  placeholder, serializing the value through the recorded serializer (same coercion path as today).
  `#if` guards bridge EF10 `QueryContext.Parameters` vs EF8/EF9 `QueryContext.ParameterValues`, as the
  spike already does.
- Constants are baked into the template at compile time.

This is the perf delta over the spike: lowering + rendering leave the hot path; per-execution work is
a clone-and-substitute.

## Pipeline selection, fallback & the gate

The pipeline is chosen by a **user-level config option** on the DbContext options builder — an enum
(`MongoQueryMode`: `Native` / `DriverLinq` / `NativeStrict` — the public face of the spike's internal
`NativeQueryMode` `Auto` / `Off` / `Force`), **not** an environment variable (this supersedes the
spike's `MONGODB_EF_NATIVE_QUERY` env var). See the project design's "Pipeline
selection, fallback & the gate" for the full option semantics and rationale; the foundation just
needs the compile-time gate to honor it. In `MongoShapedQueryCompilingExpressionVisitor` (compile
time):

- Under `Native`: if `IsNativeRepresentable` **and** lowering/rendering succeed ⇒ compile the native
  path (pipeline factory + streaming/DOM shaper); else ⇒ compile the driver-LINQ path from
  `CapturedExpression` (the per-query fallback, unchanged).
- Under `NativeStrict`: a non-representable query throws at compile time (surfacing the gap) — the
  diagnostic/coverage role the spike's force-mode played, now via the config option.
- Under `DriverLinq`: the native path is never attempted.
- The `streaming` / `DispatchingQueryingEnumerable` dual-shaper discipline is preserved unchanged.
- `MongoExecutableQuery` gains the `MongoPipelineFactory`; the per-execution `NativePipeline` becomes
  `factory.Build(parameterValues)`. Storage's `MongoClientWrapper.Execute` is otherwise untouched — it
  still receives a `BsonDocument[]`.

## Streaming materializer port

Kept, not rewritten (per the handoff). The rewriter + `BsonRowReader` + `StreamingEligibility` are
delegation-independent and bind to the shaper, not the translator; they consume native-pipeline
cursor rows as today. The known per-row double-pass overhead is **out of scope** here (the next
optimization, to be profiled first); this spec must not regress it and does not fix it.

## Testing & validation

Bar: **zero regressions**. Mirrors the spike's rig.

1. Full FunctionalTests + SpecificationTests green in `Native` mode (fallback covers the non-parity
   remainder), on EF10 **and** EF8.
2. strict-mode native coverage **must not shrink** vs the spike's current acceptance set
   (~64% spec / ~82% functional Query): the same shapes go native, via the AST.
3. New unit tests asserting `MongoSelectExpression` slot population and rendered MQL for the parity
   operators (`AssertMql`), including parameterized queries that prove the template binds correctly
   across executions with different parameter values (the cache-correctness case the spike's
   per-execution model sidesteps).
4. Benchmark headline re-run (`Release EF10`, InProcess): native alloc/time must be **≥** the spike's,
   with the B2 translation-off-hot-path improvement as expected (not required) upside.

## Out of scope (later sub-projects)

- Predicate breadth and the `$expr` (aggregation-expression) renderer (string methods, `Contains`/`IN`,
  subqueries, computed, date, member-cast, nullable-equality).
- Projection / `$project` pushdown (server-side projection); projected/anonymous queries fall back, as
  today.
- Scalar cardinality (`Count` / `First` / `Single` / `Any` / aggregates).
- Collection / nested / filtered Includes — and the clean-up single-level reference Include implies:
  structural `Lookups` population in the QMTEV, replacing and then deleting the driver-join
  reconstruction (`GetStreamingReferenceLookups()` / `UsesDriverJoinFields` / inner-collection tracking).
- `GroupBy`, `SelectMany`, set operations, `Distinct`, `OfType`/type-tests, `VectorSearch`,
  non-canonical `Skip`/`Take`.
- Pushdown-into-subquery for non-canonical operator order.
- The per-row double-pass materializer optimization.

## Carried-over cleanup

- Update the stale `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md` (it still states the provider
  "sits on top of the driver's LINQ v3 provider … does not generate aggregation BSON itself") to
  describe the rebuilt architecture.
