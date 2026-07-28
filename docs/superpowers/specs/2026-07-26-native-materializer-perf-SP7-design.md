# Native materializer perf (SP7) — design

*Epic EF-322 — "Native LINQ query provider (ground-up rebuild)", sub-project 7 (the capstone).*
*Branch `EF-322-SP7-materializer-perf`, stacked on the native tip `1dd7862` (`NativeQueryOngoing`, unmerged).*
*A JIRA number should be filed for SP7 (the scoreboard lists it as `—`); this doc will be updated with it.*

---

## 1. Problem

The native read path is DOM-free but **double-pass**. For a streaming-eligible query today:

1. The driver deserializes each cursor row into a `RawBsonDocument` object (**pass 1**).
2. `MongoStreamingEntityMaterializerRewriter` opens a *fresh* `BsonBinaryReader` + `ByteBufferStream` +
   `BsonDeserializationContext` over that row's bytes and reads forward into the POCO (**pass 2**).

The per-row object graph pass 1 allocates is the largest remaining slice of the native-vs-driver gap on
no-tracking reads. Authoritative benchmark (`benchmarks/.../results/perf-baseline.md`, quiet M4 Max,
Release EF10, N=10,000):

| Shape | DriverOnly (floor) | EF_Native (today) |
|---|---:|---:|
| WholeEntityToList (no-track) | 3,133 KB | 19,109 KB |
| WholeEntityToList (tracked) | 3,133 KB | 25,195 KB |
| WhereToList | 1,590 KB | 9,591 KB |
| OrderByTake | 59 KB | 243 KB |
| ReferenceInclude | 7,333 KB | 51,953 KB *(falls back to driver-LINQ — not native yet)* |

The driver-only floor fuses read+materialize into one class-map serializer call straight off the batch
buffer (bytes → entity). SP7 closes the gap by giving EF's materializer the same shape.

## 2. Goal & success criteria

**Goal.** Collapse the double-pass into one pass — make deserialization *be* materialization — and then
broaden which query shapes use the streaming materializer at all.

This is a **perf and coverage** sub-project only. It changes *how* native rows become POCOs, never *which
queries* are native (that was SP1–SP6) and never query *results*.

**Success bar (agreed): approach the driver-only floor.**

- On the shapes that already stream (whole-entity no-track/tracked, `Where`, `OrderByTake`), EF_Native
  allocations move meaningfully toward the driver-only floor, accepting the inherent EF-shaper tax
  (compiled-shaper delegate, `MaterializationContext`, per-sub-entity blocks) as the residual.
- Measured by the existing 3-config benchmark harness (`DriverOnly` / `EF_DriverLinq` / `EF_Native`),
  Release EF10, `InProcessEmitToolchain`, `MemoryDiagnoser`.
- **Zero** spec/functional/unit regressions across EF8 / EF9 / EF10.
- The `NativeOnly` pass-set is unchanged in Phase 1 (no eligibility change) and only *grows* in Phase 2
  (more shapes stream; none stop being native).

## 3. Reference: EF Core's Cosmos streaming materializer

The Cosmos provider (in `dotnet/efcore`) reads directly from the query response stream into the POCO — a
genuine one-pass stream→POCO — and is the closest existing reference. Studied
`CosmosShapedQueryCompilingExpressionVisitor.QueryingEnumerable.cs`, `.cs`, and
`.ShaperProcessingExpressionVisitor.cs`. Key findings:

1. **The cursor yields raw bytes, not a per-row DOM.** Each item is `ReadOnlyMemory<byte>` sliced forward
   off the response buffer (`_data = _data.Value.Slice(bytesConsumed)`). No `JObject`/`JsonElement` per
   row.
2. **`QueryContext` is a normal shaper-delegate parameter** —
   `delegate T Shaper<T>(QueryContext, ReadOnlyMemory<byte> data, int ordinal, out int bytesConsumed)`.
   There is **no** threading-through-the-deserializer trick. This matches the MongoDB provider's *existing*
   `Func<QueryContext, row, TResult>` shaper model, so the "how do we get `QueryContext` into
   `IBsonSerializer.Deserialize`" concern is not the real obstacle.
3. **The row bytes are retained and re-readable.** The shaper wraps them in a `JsonReaderData` and spins up
   fresh `Utf8JsonReader`s off it — one per read-pass, restored from a saved reader state.
4. **Re-readability is load-bearing.** A single stored value can feed multiple projections ("duplicated
   shaper"), so a single live forward-only reader would be consumed after the first projection. Within one
   pass, materialization is strict forward-only (read token → match name → deserialize → advance).

**The design-critical takeaway (a constraint not previously accounted for): some shapes read the row more
than once.** A pure "read once straight off the cursor's single reader" is safe only for genuinely
single-pass shapes; multi-pass shapes need retained, re-openable bytes.

The saving grace: **the MongoDB streaming path today is already strictly single-forward-pass**
(`MongoStreamingEntityMaterializerRewriter.BuildFillLoop` reads each row once through one reader). So the
currently-eligible shapes are all single-pass — which is exactly what makes the aggressive Phase-1
mechanism safe for them.

## 4. Mechanism

Two variants, split by phase (agreed):

### Phase 1 — deserialize-is-materialize (single-pass, straight off the cursor reader)

For the shapes that already stream (all single-pass):

- A per-execution custom **`IBsonSerializer<TEntity>`** wraps EF's compiled streaming materializer, reading
  forward off the deserialization context's own `IBsonReader` directly into the POCO.
- It is constructed **per query execution**, capturing the `QueryContext` — so tracking / identity
  resolution / `StateManager` all work exactly as they do today. It is **never cached across queries**.
- `IMongoCollection.Aggregate<TEntity>(pipeline)` uses this serializer as the pipeline's **output
  serializer**, so the cursor yields fully-materialized entities. No per-row `RawBsonDocument`, no second
  reader. Deserialization *is* materialization.
- `QueryingEnumerable` degenerates to the outer projection/tracking tail (or identity).
- **Reuse, not rewrite.** The body of `MongoStreamingEntityMaterializerRewriter` (plans, fill loops,
  owned refs, owned collections, lookup refs, include fixups) is reused verbatim. Only the *entry point*
  changes: the reader comes from a parameter (`context.Reader`) instead of `BsonRowReader.Open(row)`.

### Phase 2 — retained-bytes, shaper opens the reader (multi-pass, the Cosmos pattern)

For newly-eligible shapes that need a second read pass:

- Retain the document as re-openable bytes (`ReadOnlyMemory<byte>` / the raw slice) rather than forcing a
  single forward pass.
- The shaper opens fresh forward `BsonBinaryReader`s over those bytes per pass, exactly as Cosmos opens
  fresh `Utf8JsonReader`s off its `JsonReaderData`.
- `QueryContext` stays a normal shaper parameter.

### Seam impact (Query → Storage, within the internal `MongoExecutableQuery` contract)

- `MongoExecutableQuery.Streaming` (bool) becomes / is joined by a carrier for the per-execution output
  serializer (or a factory that takes `QueryContext`).
- `MongoClientWrapper.Execute`'s streaming branch runs `Aggregate<TEntity>` with that output serializer
  instead of `Aggregate<RawBsonDocument>` + `.ToEnumerable()`.
- The compile-time native-vs-driver and streaming-vs-DOM decisions in
  `MongoShapedQueryCompilingExpressionVisitor.CompileShapedQuery` are preserved unchanged — SP7 only
  changes the *implementation* of the streaming shaper, not the decision to use it.

## 5. Slice-0 de-risking spike (throwaway)

A throwaway branch, discarded after producing a written findings doc that feeds the Phase-1 design. It
must settle three things **before any production edit** (matching the "profile first" mandate in the
program design doc §7 and the "spike-first de-risks silent-wrong-data" lesson):

- **(a) Seam feasibility.** Can a per-execution custom `IBsonSerializer<TEntity>` be handed to
  `IMongoCollection.Aggregate` so the cursor yields materialized entities (or raw slices), *with*
  session/transaction binding intact?
- **(b) Apportionment profile.** Split the 19.1 → 3.1 MB gap between the per-row `RawBsonDocument`
  (pass 1) and the fresh reader/context objects (pass 2). Confirm whether `RawBsonDocument` copies the
  document bytes or wraps the batch buffer.
- **(c) Floor proof.** A hacked deserialize-is-materialize path approaches the driver-only floor on
  whole-entity no-track via the existing 3-config benchmark.

**Gate.** If (a) is blocked (the driver won't accept a custom output serializer on `Aggregate`), Phase 1
pivots to a cheaper-bytes variant — still `RawBsonDocument`, but eliminate the per-row reader/context
re-allocation — rather than the full floor. Success bar is re-negotiated in that case.

## 6. Phase 1 — one-pass perf foundation

Shapes: the currently-eligible, single-pass ones. Slices (each an independently reviewable stacked
commit; stop for review after each per the subagent-driven workflow):

- **P1.1 — Parameterize the reader source (pure no-op refactor).** Change
  `MongoStreamingEntityMaterializerRewriter` to take its `IBsonReader` from a parameter rather than calling
  `BsonRowReader.Open(row)` internally. Still fed by the existing `RawBsonDocument` path; **zero** behavior
  change. Green on its own.
- **P1.2 — Custom output serializer + seam wiring.** Introduce the `IBsonSerializer<TEntity>` that wraps
  EF's compiled materializer (reading off `context.Reader`), built per-execution capturing `QueryContext`.
  Carry it across `MongoExecutableQuery`; run it in `MongoClientWrapper.Execute`'s streaming branch via
  `Aggregate<TEntity>`. Retire the `RawBsonDocument` streaming path for eligible shapes. Preserve
  `NativeOnly` / fallback and the streaming-vs-DOM compile-time decision.
- **P1.3 — Validate + benchmark.** `/test-all` EF8/9/10 green; re-run the 3-config benchmarks; add the
  one-pass `EF_Native` column to `perf-baseline.md`; confirm floor-approach + zero regressions.

## 7. Phase 2 — broaden streaming eligibility

Built on the Phase-1 foundation, using the retained-bytes/re-open pattern for multi-pass shapes.
**Provisional order** — re-ranked after the spike + Phase 1, and the exact gap set re-verified against the
new foundation (some gaps may be reshaped or subsumed). Each closes a current DOM-fallback gap:

- **P2.1 — Reducer/aggregate results.** `First`/`Single`/scalar aggregates currently materialize via the
  DOM shaper (`allowStreaming: false`). Addresses the status report's "`BsonRowReader` null /
  non-nullable-list gap".
- **P2.2 — Collection-array Include.** Single-level collection `Include` is native but DOM-shaped today
  (forced `allowStreaming: false`); route it through streaming.
- **P2.3 — Activate the dormant reference-Include streaming machinery.** The lowerer `$lookup`/`$unwind`,
  `GetStreamingReferenceLookups`, and the lookup-reference plan in the rewriter are built but dormant.
  Biggest headline (`ReferenceInclude` is 52 MB vs the 7.3 MB driver-only floor).

## 8. Non-goals

- **Parity cutover** (retiring the driver-LINQ fallback and deleting the delegation code) — a separate
  follow-on, not SP7.
- The **DOM shaper** and the **driver-LINQ path** stay as they are — the fallback must keep working.
- **No new query-translation coverage.** SP7 does not make any new *query shape* native; it changes only
  materialization.

## 9. Testing & verification

- **Correctness, per slice:** full `/test-all` (spec + functional + unit) across EF8/9/10, run foreground
  with per-version isolated testcontainers. A single massive background run can hang — run foreground.
- **`NativeOnly` invariant:** the pass-set is unchanged in Phase 1 and only grows in Phase 2; nothing that
  was native stops being native.
- **Streamed-vs-DOM parity:** a streamed entity graph must equal the DOM-shaped one (values, owned refs,
  owned collections, includes, required-field/missing-field throws, null handling).
- **Perf:** 3-config benchmarks (Release EF10, InProcess, `MemoryDiagnoser`); update `perf-baseline.md`
  with the SP7 column; success = floor-approach on the Phase-1 shapes.
- **Multi-version:** `#if` discipline for the EF10 query-parameter shapes (`QueryParameterExpression` /
  `Parameters` vs EF8/EF9 `ParameterExpression` / `ParameterValues`); all touched types stay `internal`.

## 10. Risks

- **Driver rejects a custom output serializer on `Aggregate`** → the slice-0 spike gates this; fallback is
  the cheaper-bytes variant (§5 gate).
- **Tracking / identity resolution under deserialize-is-materialize** → the serializer is per-execution and
  captures `QueryContext`; it is never cached across queries.
- **Driver-version forward-compat** (`DRIVER_VERSION` CI override) → the seam must survive driver upgrades;
  covered by the compat test matrix.
- **Session / transaction binding** on `Aggregate<TEntity>` must be preserved (it flows through
  `MongoExecutableQuery.Session` today).
- **Re-readability regressions in Phase 2** → any newly-eligible multi-pass shape must use retained bytes,
  not a consumed single reader (the Cosmos constraint in §3).

## 11. Open questions (resolved by the spike, recorded here so they are not re-litigated)

- Does `RawBsonDocument` copy document bytes per row, or wrap the batch buffer? (Determines how much
  headroom Phase 1 actually buys.)
- Can the custom output serializer be supplied per-execution (via `PipelineDefinition<TInput,TOutput>`
  output serializer, `Aggregate<TOutput>`, or an `As<T>(serializer)` hook) without disturbing session
  binding or MQL logging?
- What is the true apportionment between pass-1 (`RawBsonDocument`) and pass-2 (reader/context) in the
  19.1 → 3.1 MB gap?
