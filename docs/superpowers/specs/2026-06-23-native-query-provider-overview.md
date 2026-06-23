# Native LINQ query provider — overview

**Date:** 2026-06-23 · **Branch:** `EF-322-native-query` (off `main`) · **JIRA:** EF-322
**Full detail:** `2026-06-23-native-query-provider-design.md` · **Spike reference:** `spike/low-level-provider`

> **Reviewer:** read this for the *what* and *why*. Use the design doc to drill into the *how*.

## TL;DR

- We translate EF queries to MongoDB pipelines **ourselves**; the driver only executes (BSON, cursors, sessions, transactions). It is no longer the LINQ engine.
- Two reasons: **perf** (~50% less allocation, ~45–53% faster on heavy reads) and the **conformance ceiling** (the driver's LINQ provider can't express the EF semantics we need).
- Architecture: a **Mongo query AST** → typed **stage IR** → `BsonDocument[]`, built **once at compile time**, parameter-bound per execution.
- Spike = **reference only**; everything is rebuilt fresh on main. Reproduce its streaming materializer faithfully, build the translation on the AST, drop the driver-LINQ delegation at parity.
- Pipeline choice is a **user config option** — `Native` (default), `DriverLinq` (revert), `NativeStrict` (diagnostic). No environment variables.
- Endgame: read **stream → POCO in one pass, no double copy**.
- Delivered in sub-projects, each at **zero regressions** with driver-LINQ as the fallback behind it.

## What we are doing

Replace the *translation* half of the Query subsystem. Build the aggregation pipeline from a query
AST; use the driver only to run it. The spike is **reference only** — everything here is rebuilt fresh
on main (the streaming materializer reproduced faithfully, the translation built on the AST). Drop the
driver-LINQ delegation once native is at parity.

Ship as a sequence of sub-projects:

0. **Benchmark + baselines** — a perf benchmark harness + recorded current-provider baseline, plus a conformance-baseline snapshot. No product code; the yardstick for every later stage. *(do first)*
1. **AST foundation** — the first working native read path, rebuilt fresh: native execution + streaming materializer + config-option gate + the AST translation, at parity (filter / sort / paging + single-level reference Include). *(designed, ready to plan)*
2. **Predicate breadth** — the `$expr` renderer and the operator long tail.
3. **Projection pushdown** — server-side `$project`.
4. **Scalar cardinality** — `Count` / `First` / `Any` / aggregates.
5. **Collection Includes** — collection / nested / filtered.
6. **Remaining operators** — `GroupBy`, `SelectMany`, set ops, `Distinct`, `OfType`, `VectorSearch`, non-canonical paging.
7. **Materializer perf** — one-pass stream → POCO (no per-row BSON object). Profile first.

At parity: retire the driver-LINQ fallback and delete the delegation code.

## Why

**Performance** (N=10k; full table in the design doc's *Why this rebuild*):

| Shape | Current provider | Native |
|---|---|---|
| Where→ToList | 15.1 ms / 22.7 MB | 8.3 ms / 9.6 MB |
| Whole-entity (no-track) | 33.4 ms / 45.3 MB | 15.6 ms / 19.1 MB |
| Reference Include | 138.3 ms / 52.0 MB | 114.7 ms / 22.3 MB |

**Conformance ceiling.** The driver's LINQ provider lacks operators and was not built to EF Core
semantics, so a class of spec tests can't pass while we delegate to it — and can't be fixed without
owning translation. Owning it lowers the limit to "what MongoDB can express," the only limit worth
having.

## What to review vs. where to drill in

| To review… | Read… |
|---|---|
| The decision, scope, and plan | **this doc** |
| Architecture — AST, lowering, compile-time pipeline, the config option | design § *Target architecture* |
| The perf/conformance case | design § *Why this rebuild* |
| Spike learnings (de-risked unknowns) | design § *What the spike proved* |
| Keep-vs-rebuild boundaries | design § *Keep vs rebuild inventory* |
| Sub-project 1 (the work that's ready to start) | design § *Sub-project 1* + `2026-06-20-mongo-query-ast-foundation-design.md` |
