# Mongo query AST foundation — overview

**Date:** 2026-06-20 · **Branch:** `EF-322-native-query` (off `main`) · **JIRA:** EF-322
**Full detail:** `2026-06-20-mongo-query-ast-foundation-design.md` · **Program:** `2026-06-23-native-query-provider-design.md` (this is sub-project 1)

> **Reviewer:** read this for the *what* and *why*. Use the design doc to drill into the *how*.

## TL;DR

- Sub-project 1 of the native query rebuild: replace the *translation* half of Query with a real query AST + pipeline generator.
- Scope is **parity** with what the spike already runs natively — single-collection filter / sort / paging + single-level reference Include over whole-entity results. Nothing more.
- AST model **A3 (hybrid)**: logical-slot `MongoSelectExpression` → typed `MongoStage[]` IR → `BsonDocument[]`.
- Pipeline generation **B2**: lower/render once at compile time into a cached factory; bind parameters per execution.
- Consequence of B2: the native-vs-driver decision moves to **compile time** (deterministic), replacing the spike's per-execution try/catch.
- Driver-LINQ stays the gated fallback; Include is kept as-is, with its clean-up deferred to a later sub-project.
- Bar: **zero regressions**, strict-mode native coverage **must not shrink**, benchmarks **≥** spike.

## What we are doing

Today the QMTEV returns `null` for `Where` / `OrderBy` / `Skip` / `Take` / …, so those operators
survive as a raw LINQ chain that the native path re-walks on every execution. Instead, the QMTEV
populates a structured AST, and a lowerer + renderer consume it to build the pipeline once at compile
time.

Kept unchanged: the streaming materializer, the DOM shaper, the query-mode config option, and the
test/benchmark rig. Superseded: `MongoPipelineTranslator` / `MongoPredicateTranslator` (the
per-execution chain re-walkers).

## Why this shape

- **A3** (over a flat stage-list or direct-to-BSON): a `SelectExpression`-aligned slot model gives the
  future operator set a home and isolates the Mongo-specific concerns — canonicalization, the query vs
  `$expr` dialects, future pushdown — at one lowering boundary.
- **B2** (over per-execution regeneration): translation leaves the hot path (a real slice of the perf
  headline) and matches EF's relational idiom.
- **Parity, not breadth:** the foundation reproduces the spike's exact native acceptance set via the
  structured slots. Everything else is a later sub-project.

## Scope

- **In:** filter (`$match`), sort (`$sort`), paging (`$skip`/`$limit`), single-level reference Include
  (`$lookup`/`$unwind`); whole-entity results.
- **Out:** predicate breadth / `$expr`, projection pushdown, scalar cardinality, collection / nested /
  filtered Includes, `GroupBy` / `SelectMany` / set ops / `Distinct` / `OfType` / `VectorSearch`,
  non-canonical paging, and the per-row double-pass materializer fix.

## What to review vs. where to drill in

| To review… | Read… |
|---|---|
| Scope, decisions (A3/B2), parity bar | **this doc** |
| AST taxonomy + QMTEV slot population | design § *AST node taxonomy*, *Population in the QMTEV* |
| Lowering / rendering / the dialect boundary | design § *Lowering, rendering & the dialect boundary* |
| B2 parameter binding | design § *Parameter binding* |
| Gate / fallback / strict mode | design § *Pipeline selection, fallback & the gate* |
| Why Include is kept as-is for now | design § *Include handling (deferred clean-up)* |
