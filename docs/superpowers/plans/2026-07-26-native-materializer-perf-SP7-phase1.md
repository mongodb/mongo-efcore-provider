# SP7 Phase 1 — one-pass materializer to the floor — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Collapse the native streaming read from two passes to one — make a custom `IBsonSerializer<TEntity>` (EF's compiled materializer reading forward off the cursor's own `IBsonReader`) be the Aggregate output serializer — and then make the per-property reads allocation-free, so whole-entity no-track/tracked, `Where`, and `OrderByTake` approach the driver-only allocation floor.

**Architecture:** The spike (`docs/superpowers/specs/2026-07-26-native-materializer-perf-SP7-spike-findings.md`) proved: (1) the driver accepts a custom output serializer (`PipelineDefinition<BsonDocument,BsonDocument>.As<BsonDocument,BsonDocument,TEntity>(serializer)` → `coll.Aggregate(session, p)`); (2) `RawBsonDocument` merely wraps the batch buffer, so ~86% of the gap is the *second* pass; (3) deserialize-is-materialize is correct and cuts −36% (19.1→12.3 MB) but lands at 3.9× the 3,133 KB floor because `BuildTypedRead` still allocates a fresh `BsonDeserializationContext` per-property-per-row and boxes each read. So Phase 1 has two optimizations: **T2** (one-pass seam) then **T3** (allocation-free per-property reads). The tracked path additionally needs the state manager initialized *before* the cursor is created (the driver eagerly deserializes batch 1 during `.Aggregate()`).

**Tech Stack:** .NET 10 / EF Core 10 (and EF8/EF9 via build configs), MongoDB C# driver 3.9.0 (BSON serializers, `IMongoCollection.Aggregate`, `IBsonReader`), BenchmarkDotNet (InProcess, MemoryDiagnoser).

## Global Constraints

- **Multi-version:** must build & pass under `Debug EF8`, `Debug EF9`, `Debug EF10`. Guard version-divergent surfaces with `#if` (`EF8`/`EF9`/`EF10`). EF10 uses `QueryParameterExpression`/`Parameters`; EF8/9 `ParameterExpression`/`ParameterValues` — the streaming shaper already lives behind existing guards; do not widen them casually. All new types stay `internal`.
- **This changes materialization only, never results or query shapes.** No new native query coverage; `NativeOnly` pass-set must be unchanged (nothing that streamed stops streaming; nothing new starts being native). Streamed entity graphs must be byte-for-byte equal to the DOM-shaped ones (values, owned refs, owned collections, includes, required/missing-field throws, null handling).
- **Streaming-vs-DOM and native-vs-driver stay compile-time decisions** in `MongoShapedQueryCompilingExpressionVisitor` (`ClassifyNativeDisposition` / `AllPendingLookupsAreStreamable`). Do not move them to runtime.
- **Benchmarks:** `-c "Release EF10"`, InProcess toolchain, MemoryDiagnoser, from `benchmarks/MongoDB.EntityFrameworkCore.Benchmarks/`. Env: `MONGODB_URI=mongodb://localhost:27017/?replicaSet=rs0` (the spike's `ef-sp7-mongo` container). Floor to approach (whole-entity no-track, N=10,000): **3,133 KB**; today's native: **19,108 KB**; one-pass-only milestone (T2): ~**12,306 KB**.
- **Proving native:** MQL shape cannot prove native — assert `MongoQueryMode.NativeOnly` succeeds (or, for streaming specifically, assert the streamed values equal the DOM values under both modes). Streaming unit tests live in `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/`; functional in `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/`.
- **Delivery:** stacked-PR workflow — decide at finish (per `superpowers:finishing-a-development-branch`) whether T1–T4 land as one squashed commit or stacked commits; keep a `-presquash` backup. Do not push without the user's go.

---

### Task 1 (P1.1): Parameterize the streaming reader source — pure no-op refactor

Make `MongoStreamingEntityMaterializerRewriter` obtain its `IBsonReader` from a parameter instead of hard-calling `BsonRowReader.Open(row)`. Behavior is identical: the streaming compile path still opens the reader from the `RawBsonDocument` row and passes it in. This isolates the entry-point change T2 needs, with zero behavior change, validated by the existing streaming suite staying green.

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoStreamingEntityMaterializerRewriter.cs` (`Rewrite` ~204-246; the `_row` field + `OpenMethod`/`ReadStartDocument` prelude).
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoShapedQueryCompilingExpressionVisitor.cs` (streaming branch ~427-443 — the caller that constructs the rewriter with `rawRowParameter`).
- Test: existing `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/` streaming/materialization tests + `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/` (no new tests; this is a refactor).

**Interfaces:**
- Produces (for T2): `MongoStreamingEntityMaterializerRewriter.Rewrite(Expression injectedBody, ParameterExpression readerParameter)` — the rewriter no longer owns `BsonRowReader.Open`; the caller supplies a `ParameterExpression` of type `IBsonReader` and is responsible for opening/positioning/disposing it. The row-level prelude (`OpenMethod`, `ReadStartDocument`, the `TryFinally` dispose) moves to the caller for the RawBsonDocument path (kept identical for now).

- [ ] **Step 1: Baseline the existing streaming tests (they must stay green).**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/*.csproj -c "Debug EF10" --filter "FullyQualifiedName~Query" 2>&1 | tail -5` (with `MONGODB_URI` unset so a testcontainer spins up, or the spike container URI). Record the pass count — it is the refactor's regression oracle.

- [ ] **Step 2: Extract the reader source.**

In `MongoStreamingEntityMaterializerRewriter`, change the constructor to stop taking `_row` and change `Rewrite` to accept the reader as a parameter. Move the `Expression.Assign(_reader, Expression.Call(OpenMethod, _row))`, the `ReadStartDocument`, and the `TryFinally`/`Dispose` scaffolding OUT of `Rewrite` and into a small helper at the call site that keeps the RawBsonDocument behavior identical. Concretely, `Rewrite` receives an already-assignable `IBsonReader` and its body becomes: `ReadStartDocument` (if the caller hasn't) → fill loop → `ReadEndDocument` → rewritten body. Keep the dispose/open on the RawBsonDocument caller side so nothing observable changes yet.

- [ ] **Step 3: Update the caller (RawBsonDocument path) to open + pass the reader.**

In `CompileShapedQuery`'s streaming branch, wrap: open `BsonRowReader.Open(rawRow)` into a local `IBsonReader`, pass that `ParameterExpression` to `Rewrite`, and keep the `TryFinally` dispose around the whole thing (as today). The compiled shaper stays `Func<QueryContext, RawBsonDocument, TResult>`.

- [ ] **Step 4: Build all three EF versions.**

Run: `for c in "Debug EF8" "Debug EF9" "Debug EF10"; do dotnet build MongoDB.EFCoreProvider.sln -c "$c" 2>&1 | tail -2; done`
Expected: all build.

- [ ] **Step 5: Re-run the streaming tests — same pass count as Step 1.**

Run the Step-1 command again. Expected: identical pass count (pure refactor, no behavior change).

- [ ] **Step 6: Commit.**

```bash
git add -A && git commit -m "EF-322: SP7 P1.1 - parameterize streaming materializer reader source (no-op refactor)"
```

- [ ] **Step 7: STOP for review.** Reviewer confirms the refactor is behavior-preserving (the RawBsonDocument path is byte-identical; the only change is where the reader is opened).

---

### Task 2 (P1.2): One-pass custom output serializer + tracked-path state-manager ordering

Introduce a per-execution `IBsonSerializer<TEntity>` whose `Deserialize` runs the compiled materializer off `context.Reader`; wire it across the Query→Storage seam so the cursor yields entities directly; and initialize the state manager before the cursor is created so tracked queries work. Milestone result: whole-entity no-track AND tracked go native, correct, at ~12.3 MB.

**Files:**
- Create: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoEntityMaterializerSerializer.cs` — `internal sealed class MongoEntityMaterializerSerializer<TEntity> : SerializerBase<TEntity>` wrapping `Func<MongoQueryContext, IBsonReader, TEntity>` + the captured `MongoQueryContext`.
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoStreamingEntityMaterializerRewriter.cs` — add a compile path producing `Func<MongoQueryContext, IBsonReader, TEntity>` reading one document off the passed reader (`ReadStartDocument … ReadEndDocument`, no `RawBsonDocument`, no dispose — the driver owns the cursor reader).
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoShapedQueryCompilingExpressionVisitor.cs` — streaming branch: compile the reader-based shaper, build a factory that (given a `MongoQueryContext`) produces the serializer, and carry it onto `MongoExecutableQuery`.
- Modify: `src/MongoDB.EntityFrameworkCore/Query/MongoExecutableQuery.cs` — carry an optional `Func<MongoQueryContext, object>` (or a typed carrier) that produces the output serializer, alongside `Streaming`.
- Modify: `src/MongoDB.EntityFrameworkCore/Storage/MongoClientWrapper.cs` — the `Streaming` branch: when the one-pass serializer factory is present, run `Aggregate<TEntity>` with it via `PipelineDefinition.As<BsonDocument,BsonDocument,TEntity>(serializer)`; else the existing RawBsonDocument path (kept as fallback for shapes T3 hasn't covered / non-eligible).
- Modify: `src/MongoDB.EntityFrameworkCore/Query/QueryingEnumerable.cs` — call `InitializeStateManager` **before** `MongoClient.Execute<TSource>` (currently line 169 vs 178), OR pass the initialized state into Execute, so the eager batch-1 deserialize sees a live StateManager.
- Test: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/` (new: no-track + tracked streamed-vs-DOM parity; owned refs/collections; missing-required throw); `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/`.

**Interfaces:**
- Consumes: `MongoStreamingEntityMaterializerRewriter.Rewrite(injectedBody, readerParameter)` (T1).
- Produces (for T3): `MongoEntityMaterializerSerializer<TEntity>.Deserialize(BsonDeserializationContext context, …)` calls `_shaper(_queryContext, context.Reader)`; the per-property reads it drives live in `BuildTypedRead` (T3's target).

- [ ] **Step 1: Write the failing parity + tracked tests.**

In a new `tests/.../FunctionalTests/Query/NativeMaterializerOnePassTests.cs`: (a) whole-entity no-track — assert the streamed result set equals `AsNoTracking()` DOM result and succeeds under `MongoQueryMode.NativeOnly`; (b) whole-entity **tracked** — assert entities are tracked and a mutate+`SaveChanges` round-trips; (c) an owned-reference + owned-collection entity streams correct nested values; (d) a required-but-missing field throws the same `InvalidOperationException` as the DOM path. Write them to fail first (the one-pass path doesn't exist yet — they can target a feature flag or the new serializer once stubbed).

- [ ] **Step 2: Run to verify they fail.**

Run: `dotnet test tests/.../FunctionalTests/*.csproj -c "Debug EF10" --filter "FullyQualifiedName~NativeMaterializerOnePass"`
Expected: FAIL (one-pass path not wired).

- [ ] **Step 3: Add the reader-based compile path to the rewriter.**

Add `Rewrite`-equivalent producing a body that binds `_reader` to the passed `IBsonReader` parameter and reads exactly one document (`ReadStartDocument` → fill loop → `ReadEndDocument`), returning the entity — no open, no dispose (the cursor owns the reader). Compile to `Func<MongoQueryContext, IBsonReader, TEntity>` at the gate.

- [ ] **Step 4: Add `MongoEntityMaterializerSerializer<TEntity>`.**

```csharp
internal sealed class MongoEntityMaterializerSerializer<TEntity>(
    Func<MongoQueryContext, IBsonReader, TEntity> shaper, MongoQueryContext queryContext)
    : SerializerBase<TEntity>
{
    public override TEntity Deserialize(BsonDeserializationContext context, BsonDeserializationArgs args)
        => shaper(queryContext, context.Reader);
}
```

- [ ] **Step 5: Carry the serializer factory across the seam + wire Execute.**

On `MongoExecutableQuery`, add an optional serializer-factory carrier. In `CompileShapedQuery`'s streaming branch, when eligible, build the factory `qc => new MongoEntityMaterializerSerializer<TEntity>(compiledShaper, qc)` and set it. In `MongoClientWrapper.Execute`'s `Streaming` branch, when the factory is present: `var serializer = factory(queryContext); PipelineDefinition<BsonDocument,BsonDocument> basePipe = loggedStages; var p = basePipe.As<BsonDocument,BsonDocument,TEntity>(serializer); var cursor = session is {} s ? coll.Aggregate(s, p) : coll.Aggregate(p); return (IEnumerable<T>)cursor.ToEnumerable();`

- [ ] **Step 6: Fix the state-manager ordering (tracked path).**

In `QueryingEnumerable.MoveNextHelper`, move `_queryContext.InitializeStateManager(_standAloneStateManager)` to **before** the `MongoClient.Execute<TSource>(...)` call (which creates the cursor and eagerly deserializes batch 1). Confirm this is harmless for the DOM/driver-LINQ paths (they materialize lazily in the shaper). Add an inline comment referencing the eager-batch-1 reason.

- [ ] **Step 7: Run the tests to verify they pass.**

Run the Step-2 command. Expected: PASS (no-track + tracked + owned + missing-required all green).

- [ ] **Step 8: Benchmark the milestone (correctness of the −36% claim).**

Run: `cd benchmarks/MongoDB.EntityFrameworkCore.Benchmarks && MONGODB_URI=mongodb://localhost:27017/?replicaSet=rs0 dotnet run -c "Release EF10" -- --filter "*WholeEntityToList_EF_Native*"`
Expected: no-track allocation ≈ 12,300 KB (down from 19,108 KB), correct counts. Record it.

- [ ] **Step 9: Commit.**

```bash
git add -A && git commit -m "EF-322: SP7 P1.2 - one-pass deserialize-is-materialize + tracked state-manager ordering"
```

- [ ] **Step 10: STOP for review.** Reviewer checks: streamed==DOM parity (esp. owned/required-missing), tracked round-trip, NativeOnly pass-set unchanged, the state-manager reorder is safe for all paths, and the RawBsonDocument fallback path still exists for non-eligible shapes.

---

### Task 3 (P1.3): Allocation-free per-property typed reads — reach toward the floor

Eliminate the residual ~9.2 MB: stop allocating a fresh `BsonDeserializationContext` per property per row, and stop boxing each read. Reuse one context (the incoming one) and call the **generic** `IBsonSerializer<T>.Deserialize` returning `T` directly.

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoStreamingEntityMaterializerRewriter.cs` — `BuildTypedRead` (~1129-1154): today it calls `CreateRootMethod` (fresh `BsonDeserializationContext.CreateRoot`) per property and `IBsonSerializer.Deserialize` returning boxed `object`.
- Test: existing correctness tests from T2 must stay green; add a unit test asserting a mixed-type entity (int/long/string/bool/double/decimal/nullable) round-trips identical values.

**Interfaces:**
- Consumes: the reader-based materializer (T2). The incoming `BsonDeserializationContext` is available (thread it from the serializer into the compiled shaper, or reconstruct one per row and reuse across properties).

- [ ] **Step 1: Confirm the allocation-free mechanism (short investigation, record in the commit body).**

Verify two things against driver 3.9.0: (a) one `BsonDeserializationContext` can be reused to deserialize successive elements of the same document as the reader advances (the driver's own class-map serializer does this) — so create it **once per row**, not per property; (b) for each property, the serializer from `BsonSerializerFactory.GetPropertySerializationInfo(property).Serializer` can be cast to `IBsonSerializer<TValue>` (TValue = the stored CLR type) and its typed `Deserialize` called to avoid boxing; identify the fallback when it is not generic (value-converter/representation serializers) — keep the boxed path for those only.

- [ ] **Step 2: Write the failing allocation/correctness test.**

Add a unit/functional test that materializes a wide mixed-type entity and asserts every field equals the seed (guards against a typed-read positioning bug). It passes today (correctness) — so instead gate the *win* on the benchmark in Step 5; this test is the correctness net for the rewrite.

- [ ] **Step 3: Rewrite `BuildTypedRead` to reuse the context + typed deserialize.**

Hoist context creation to once-per-row (a local `BsonDeserializationContext` bound in the fill-loop prelude, or reuse the serializer's incoming `context`). For each property, emit a call to the generic `IBsonSerializer<TValue>.Deserialize(context, default)` returning `TValue` (no `Convert`-from-`object`); keep the explicit-BSON-null short-circuit (`ReadNull` → `default(T)`) exactly as today. Fall back to the boxed non-generic path only for non-`IBsonSerializer<T>` serializers.

- [ ] **Step 4: Run all T2 + T3 correctness tests.**

Run: `dotnet test tests/.../FunctionalTests/*.csproj -c "Debug EF10" --filter "FullyQualifiedName~NativeMaterializerOnePass" && dotnet test tests/.../UnitTests/*.csproj -c "Debug EF10" --filter "FullyQualifiedName~NativeTranslation"`
Expected: PASS (values identical; no regressions).

- [ ] **Step 5: Benchmark — the floor-approach gate.**

Run the Step-8 (T2) benchmark command. Expected: no-track allocation moves substantially below 12,306 KB toward the 3,133 KB floor. Record the number and the multiple-of-floor. (Success bar: "approach the floor" — meaningfully closer than the 3.9× the one-pass-only path left.)

- [ ] **Step 6: Commit.**

```bash
git add -A && git commit -m "EF-322: SP7 P1.3 - allocation-free per-property typed reads (reuse context, no boxing)"
```

- [ ] **Step 7: STOP for review.** Reviewer checks the typed-vs-boxed fallback boundary (value-converter/representation properties still correct), the once-per-row context reuse doesn't corrupt reader position, and the benchmark delta.

---

### Task 4 (P1.4): Three-version validation, benchmark record, AGENTS.md as-built

**Files:**
- Modify: `benchmarks/MongoDB.EntityFrameworkCore.Benchmarks/results/perf-baseline.md` — add the SP7 one-pass `EF_Native` column for whole-entity no-track/tracked, Where, OrderByTake.
- Modify: `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md` — as-built note: the streaming materializer is now one-pass (custom output serializer; deserialize-is-materialize; allocation-free per-property reads); the state-manager-before-cursor ordering; the RawBsonDocument fallback retained for non-eligible shapes.

**Interfaces:** none (validation + docs).

- [ ] **Step 1: Full 3-version test sweep (controller runs foreground, per the process lesson).**

Invoke the `/test-all` skill (EF8/EF9/EF10, foreground, per-version isolated testcontainers). Expected: all green; record per-version pass counts. Confirm the `NativeOnly` spec pass-set is unchanged vs the branch base (no eligibility regression).

- [ ] **Step 2: Full benchmark pass + record.**

Run: `cd benchmarks/MongoDB.EntityFrameworkCore.Benchmarks && MONGODB_URI=mongodb://localhost:27017/?replicaSet=rs0 dotnet run -c "Release EF10" -- --filter "*WholeEntityToList*|*WhereToList*|*OrderByTake*"`. Update `perf-baseline.md` with the SP7 numbers and the delta-vs-floor and delta-vs-prior-native.

- [ ] **Step 3: Write the AGENTS.md as-built note + commit docs.**

```bash
git add -A && git commit -m "EF-322: SP7 P1.4 - perf-baseline SP7 column + Query AGENTS.md one-pass as-built note"
```

- [ ] **Step 4: STOP for review.** Report the 3-version results, the `NativeOnly`-unchanged confirmation, and the final benchmark table (how close to the floor). After approval: whole-branch review, then squash/stack + push per the stacked-PR workflow (keep a `-presquash` backup; do not push without the user's go).

---

## Self-Review

- **Spec coverage:** §4 mechanism (Approach A) → T2; §"why one-pass misses the floor" → T3; §"tracked design constraint" → T2 Step 6; success bar (approach floor) → T3 Step 5 + T4 Step 2; non-goals (no new query coverage, DOM/fallback intact) → Global Constraints + T2 keeps the RawBsonDocument fallback; multi-version → T1 Step 4 + T4 Step 1.
- **Placeholder scan:** the one deliberate known-unknown is T3's typed-vs-boxed serializer boundary — handled by T3 Step 1 (a bounded lead-off investigation with a concrete fallback), not left vague.
- **Type consistency:** `MongoStreamingEntityMaterializerRewriter.Rewrite(injectedBody, readerParameter)` (T1) is consumed by T2; `MongoEntityMaterializerSerializer<TEntity>` (T2) drives `BuildTypedRead` (T3); the serializer-factory carrier on `MongoExecutableQuery` (T2 Step 5) is read by `MongoClientWrapper.Execute` (T2 Step 5).
- **Delivery note:** T1+T2 are the correct one-pass milestone (−36%); T3 is the floor-approach; if T3's benchmark underdelivers, T1+T2 still ship as a real win and T3/per-property reads becomes a follow-on (spec §5 fallback).
