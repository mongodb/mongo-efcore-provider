# Native query-translation foundation (sub-project 1) — overview

**Date:** 2026-06-20 · **Branch:** `EF-323-ast-foundation` (off `main`) · **JIRA:** EF-323
**Full detail:** `2026-06-20-mongo-query-ast-foundation-design.md` · **Program:** `2026-06-23-native-query-provider-design.md` (this is sub-project 1)

> **Reviewer:** read this for the *what* and *why*. Use the design doc to drill into the *how*.

## TL;DR

- Sub-project 1 of the native query rebuild: replace the *translation* half of Query with a real query expression tree + pipeline generator.
- Scope is **parity** with what the spike already runs natively — single-collection filter / sort / paging over whole-entity results. (Native single-level reference Include was in the original target but is **deferred as-built**; see *Scope*.) Nothing more.
- Query representation **A3 (hybrid)**, custom `Expression` types: logical-slot `MongoSelectExpression` → typed `MongoPipelineStage[]` IR → `BsonDocument[]`.
- Pipeline generation **B2**: lower/render once at compile time into a cached factory; bind parameters per execution.
- Consequence of B2: the native-vs-driver decision moves to **compile time** (deterministic), replacing the spike's per-execution try/catch.
- Driver-LINQ stays the gated fallback; Include is kept as-is, with its clean-up deferred to a later sub-project.
- Bar: **zero regressions**, native-only-mode coverage **must not shrink**, benchmarks **≥** spike (the
  benchmark yardstick is sub-project 0 — EF-324, in review as PR #321, not yet merged; SP1 builds on
  that branch and rebases to `main` when it lands).

## What we are doing

Today the QMTEV returns `null` for `Where` / `OrderBy` / `Skip` / `Take` / …, so those operators
survive as a raw LINQ chain that the native path re-walks on every execution. Instead, the QMTEV
populates a structured query expression tree, and a lowerer + renderer consume it to build the pipeline once at compile
time.

The spike is **reference only** — nothing is ported. Since no native machinery exists on main yet,
this sub-project also builds the native execution path, streaming materializer, DOM shaper, and
config-option gate fresh (spike as reference); the MQL-assertion + spec-conformance test infra already
on main is reused as-is. The spike's `MongoPipelineTranslator` / `MongoPredicateTranslator` chain
re-walkers are not reproduced — the new query translation is in place from the start.

## Why this shape

- **A3** (over a flat stage-list or direct-to-BSON): a `SelectExpression`-aligned slot model of custom `Expression` types gives the
  future operator set a home and isolates the Mongo-specific concerns — canonicalization, the query vs
  `$expr` dialects, future pushdown — at one lowering boundary.
- **B2** (over per-execution regeneration): translation leaves the hot path (a real slice of the perf
  headline) and matches EF's relational idiom.
- **Parity, not breadth:** the foundation reproduces the spike's exact native acceptance set via the
  structured slots. Everything else is a later sub-project.

## Scope

- **In:** filter (`$match`), sort (`$sort`), paging (`$skip`/`$limit`); whole-entity results.
- **Deferred (as-built 2026-06-27):** native single-level reference Include (`$lookup`/`$unwind`) — EF
  expands it to a `LeftJoin`, which the gate treats as non-native, so it falls back to driver-LINQ
  (correct results; throws under `NativeOnly`). The lookup machinery is built but dormant; the
  Collection Includes sub-project activates it. So the realized native slice is filter / sort / paging.
- **Out:** predicate breadth / `$expr`, projection pushdown, scalar cardinality, collection / nested /
  filtered Includes, `GroupBy` / `SelectMany` / set ops / `Distinct` / `OfType` / `VectorSearch`,
  non-canonical paging, and the per-row double-pass materializer fix.

## What to review vs. where to drill in

| To review… | Read… |
|---|---|
| Scope, decisions (A3/B2), parity bar | **this doc** |
| Query-translation types + QMTEV slot population | design § *Query-translation types*, *Population in the QMTEV* |
| Lowering / rendering / the dialect boundary | design § *Lowering, rendering & the dialect boundary* |
| B2 parameter binding | design § *Parameter binding* |
| Gate / fallback / native-only mode | design § *Pipeline selection, fallback & the gate* |
| Why Include is kept as-is for now | design § *Include handling (deferred clean-up)* |
