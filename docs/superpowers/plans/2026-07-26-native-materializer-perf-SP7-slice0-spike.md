# SP7 slice-0 de-risking spike — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Settle the three gating unknowns for SP7's one-pass materializer *before any production edit*, producing a written findings doc + a go/no-go recommendation that the Phase-1 plan will be written against.

**Architecture:** This is a **throwaway spike**, not shippable code. Work on a scratch branch cut from the SP7 branch tip; hack the streaming execution path directly; measure with the existing 3-config benchmark harness plus targeted allocation probes; write findings; then **discard all code changes** (only the findings doc survives). It deliberately does *not* follow TDD — a spike answers questions with disposable code and measurements, not regression tests. The Phase-1 plan (written afterward) is where TDD applies.

**Tech Stack:** .NET 10 / EF Core 10, MongoDB C# driver (LINQ v3 + BSON), BenchmarkDotNet (InProcessEmitToolchain, MemoryDiagnoser), the provider's native translation path (`NativeTranslation/`).

## Global Constraints

- Build configurations, not TFMs: `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"` (spike work targets EF10 only; multi-version comes in Phase 1).
- Benchmarks run `-c "Release EF10"`, **InProcess** toolchain (the default out-of-process runner breaks on the config-conditional csproj), from `benchmarks/MongoDB.EntityFrameworkCore.Benchmarks/`.
- A replica set / mongod is required; benchmarks read `MONGODB_URI` (else `mongodb://localhost:27017`).
- The authoritative floor numbers to beat/approach are in `benchmarks/.../results/perf-baseline.md`: whole-entity no-track **3,133 KB** driver-only vs **19,109 KB** EF_Native.
- **No production code is committed from this spike.** The only artifact committed is the findings doc under `.superpowers/sdd/` (spike-doc convention; excluded from any shippable slice commit).
- Never call `IMongoCollection<>.Aggregate(...)` from Query in real code — but the spike MAY hack `MongoClientWrapper.Execute` (Storage) directly, since it is throwaway.

---

### Task 1: Scratch branch + apportionment profile (unknown (b))

Answer: *where does the 19.1 → 3.1 MB gap actually come from* — the per-row `RawBsonDocument` object (pass 1) vs the fresh `BsonBinaryReader`/`ByteBufferStream`/`BsonDeserializationContext` (pass 2) — and does `RawBsonDocument` copy the document bytes or wrap the batch buffer?

**Files:**
- Create (scratch, throwaway): a branch `sp7-spike` off `EF-322-SP7-materializer-perf`.
- Inspect: `src/MongoDB.EntityFrameworkCore/Storage/MongoClientWrapper.cs:99-124` (the native streaming branch: `rawCollection.Aggregate(rawPipeline)` → `.ToEnumerable()`).
- Inspect: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/BsonRowReader.cs` (`new ByteBufferStream(row.Slice, ownsBuffer: false)` — no copy at *reader-open* time; confirms the copy question is about `RawBsonDocument` construction, not reader-open).
- Reference: `benchmarks/MongoDB.EntityFrameworkCore.Benchmarks/HeadlineBenchmarks.cs` (`WholeEntityToList_EF_Native_NoTracking`).

**Interfaces:**
- Produces (for Task 4's findings doc): `passOneAllocKB` (RawBsonDocument-only), `passTwoAllocKB` (reader/context delta), `rawBsonDocumentCopiesBytes` (bool), all for whole-entity no-track N=10,000.

- [ ] **Step 1: Cut the scratch branch**

```bash
git checkout EF-322-SP7-materializer-perf
git checkout -b sp7-spike
```

- [ ] **Step 2: Determine `RawBsonDocument` copy behavior by inspection**

Find the driver's `RawBsonDocumentSerializer.Deserialize` (in the `MongoDB.Bson` package sources or via decompilation of the referenced driver assembly). Record whether it copies the document slice into a freshly-allocated buffer or wraps the cursor batch buffer. Note the driver version from `Versions.props`.

Record the finding (copy vs wrap) — this determines whether pass 1 is a byte-copy (large) or just an object-wrapper (smaller).

- [ ] **Step 3: Add a "pass-1-only" probe benchmark**

In `HeadlineBenchmarks.cs`, add a throwaway benchmark that runs the *native streaming pipeline* but discards each row without materializing (drains the `RawBsonDocument` cursor and counts). This isolates pass-1 allocation. The simplest hack: temporarily expose a way to get the native `BsonDocument[]` for the whole-entity query (or hand-write the equivalent `{}` pipeline against the `FlatItems` collection as `RawBsonDocument`), then:

```csharp
[Benchmark] public int WholeEntity_PassOneOnly_RawBsonDoc()
{
    var raw = _client.GetDatabase(_dbName).GetCollection<RawBsonDocument>("FlatItems");
    var count = 0;
    foreach (var doc in raw.Aggregate<RawBsonDocument>(new BsonDocument[0]).ToEnumerable())
    {
        using (doc) { count++; }
    }
    return count;
}
```

- [ ] **Step 4: Run the apportionment measurement**

Run: `cd benchmarks/MongoDB.EntityFrameworkCore.Benchmarks && MONGODB_URI=... dotnet run -c "Release EF10" -- --filter "*WholeEntity*"`
Expected: three allocation numbers — `DriverOnly` (~3,133 KB), `PassOneOnly_RawBsonDoc` (X KB), `EF_Native_NoTracking` (~19,109 KB).

Compute and record: `passOneAllocKB = X − ~3133`; `passTwoAllocKB = ~19109 − X`. This is the apportionment.

- [ ] **Step 5: Record the Task-1 findings**

Append to a running scratch notes file (`.superpowers/sdd/EF-322-SP7-spike.md`): the copy-vs-wrap answer, the driver version, and the three-way apportionment. **No commit** (scratch).

---

### Task 2: Custom-output-serializer-on-Aggregate seam (unknown (a))

Answer: *can a custom `IBsonSerializer<TEntity>` be supplied to `IMongoCollection.Aggregate` so the cursor yields `TEntity`, with session/transaction binding intact?* This is the make-or-break gate for Approach A.

**Files:**
- Modify (scratch, throwaway): `src/MongoDB.EntityFrameworkCore/Storage/MongoClientWrapper.cs` (the `executableQuery.Streaming` branch, ~line 108-116).
- Reference: `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoShapedQueryCompilingExpressionVisitor.cs:833` — the provider already uses `driverQueryable.As((IBsonSerializer<TEntity>)bsonSerializerFactory.GetEntitySerializer(entityType))` on the driver-LINQ path, a working precedent for supplying a custom serializer.

**Interfaces:**
- Produces (for Task 4 + Task 3): `seamWorks` (bool), `workingSeamApi` (which overload: `PipelineDefinition` output-serializer, `IAggregateFluent.As(serializer)`, or neither), `sessionBindingComposes` (bool).

- [ ] **Step 1: Write a trivial pass-through custom serializer**

Add a throwaway `IBsonSerializer<FlatItem>` that delegates to the registry serializer but logs/counts each `Deserialize` call — enough to prove the cursor is routing through *our* instance:

```csharp
internal sealed class SpikePassThroughSerializer<T>(IBsonSerializer<T> inner) : SerializerBase<T>
{
    public static int DeserializeCount;
    public override T Deserialize(BsonDeserializationContext context, BsonDeserializationArgs args)
    { DeserializeCount++; return inner.Deserialize(context, args); }
    public override void Serialize(BsonSerializationContext c, BsonSerializationArgs a, T v) => inner.Serialize(c, a, v);
}
```

- [ ] **Step 2: Hack the streaming branch to run `Aggregate<TEntity>` with the custom serializer**

In `MongoClientWrapper.Execute`'s `executableQuery.Streaming` branch, replace the `RawBsonDocument` cursor with an attempt to run the same `loggedStages` pipeline yielding `TEntity` via the custom serializer. Try, in order, until one compiles and runs:

(a) `PipelineDefinition` with an explicit output serializer:
```csharp
var coll = Database.GetCollection<BsonDocument>(executableQuery.CollectionNamespace.CollectionName);
PipelineDefinition<BsonDocument, T> p = PipelineDefinition<BsonDocument, BsonDocument>
    .Create(loggedStages)
    .As<BsonDocument, BsonDocument, T>(new SpikePassThroughSerializer<T>(registrySerializer));
var cursor = executableQuery.Session is { } s ? coll.Aggregate(s, p) : coll.Aggregate(p);
return cursor.ToEnumerable();
```

(b) fluent `.As(serializer)`:
```csharp
var fluent = executableQuery.Session is { } s ? coll.Aggregate(s) : coll.Aggregate();
foreach (var stage in loggedStages) fluent = fluent.AppendStage<BsonDocument>(stage);
return fluent.As(new SpikePassThroughSerializer<T>(registrySerializer)).ToEnumerable();
```

Record which overload the driver actually accepts.

- [ ] **Step 3: Prove routing + session binding**

Point the benchmark's `WholeEntityToList_EF_Native_NoTracking` at this hacked path (or write a tiny console assertion): run it, then assert `SpikePassThroughSerializer<FlatItem>.DeserializeCount == 10000` (the cursor routed through our serializer). Separately, run a whole-entity query inside an explicit transaction (`ctx.Database.BeginTransaction()`) and confirm it returns rows (session binding composed).

Run: `cd benchmarks/... && MONGODB_URI=... dotnet run -c "Release EF10" -- --filter "*WholeEntityToList_EF_Native_NoTracking*"` (or the console assertion).
Expected: rows returned, DeserializeCount matches, transaction path returns rows.

- [ ] **Step 4: Record the Task-2 findings**

Append to `.superpowers/sdd/EF-322-SP7-spike.md`: `seamWorks`, `workingSeamApi`, `sessionBindingComposes`, and the exact overload signature that worked (or, if none did, the compiler/runtime errors — which triggers the §5 gate fallback). **No commit.**

**GATE:** if `seamWorks == false`, stop the floor-proof (Task 3) and record a recommendation to pivot Phase 1 to the cheaper-bytes variant (eliminate per-row reader/context re-alloc while keeping `RawBsonDocument`). Task 4 still runs.

---

### Task 3: Floor proof — deserialize-is-materialize (unknown (c))

Answer: *does making the compiled materializer be the cursor's serializer approach the driver-only floor?* Only runs if Task 2's gate passed.

**Files:**
- Modify (scratch, throwaway): the custom serializer from Task 2, and the streaming branch in `MongoClientWrapper.Execute`.
- Reference: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoStreamingEntityMaterializerRewriter.cs:204-246` (`Rewrite` builds a `BlockExpression` whose current entry point is `Expression.Assign(_reader, Expression.Call(OpenMethod, _row))` — i.e. `BsonRowReader.Open(row)`). The spike must feed the reader from `context.Reader` instead.

**Interfaces:**
- Consumes: `workingSeamApi` (Task 2).
- Produces (for Task 4): `floorProofAllocKB` (whole-entity no-track under the deserialize-is-materialize path), `approachesFloor` (bool: within a small multiple of 3,133 KB).

- [ ] **Step 1: Make the custom serializer's `Deserialize` run the compiled materializer off `context.Reader`**

The compiled streaming shaper today is `Func<QueryContext, RawBsonDocument, TResult>` and opens its own reader. For the spike, build a variant whose reader is the *incoming* `context.Reader`. The lowest-effort hack: temporarily add an overload of `MongoStreamingEntityMaterializerRewriter.Rewrite` (or a boolean) that skips the `OpenMethod`/`ReadStartDocument` prelude and instead binds `_reader` to a passed-in `IBsonReader` parameter, then compile a `Func<QueryContext, IBsonReader, TEntity>`. Wrap that in the custom serializer:

```csharp
public override T Deserialize(BsonDeserializationContext context, BsonDeserializationArgs args)
    => _compiledShaper(_queryContext, context.Reader);
```

Construct the serializer per-execution capturing the `QueryContext` (thread it via `MongoExecutableQuery` as a scratch field).

- [ ] **Step 2: Run whole-entity no-track through the floor-proof path**

Run: `cd benchmarks/... && MONGODB_URI=... dotnet run -c "Release EF10" -- --filter "*WholeEntityToList*"`
Expected: correct row count (10,000) AND an allocation number for the floor-proof path.

- [ ] **Step 3: Spot-check correctness**

Confirm a handful of materialized `FlatItem`s have correct field values (not just count) — read the first/last entity's properties and compare to the seeded data. This guards against a fast-but-wrong reader position bug.

- [ ] **Step 4: Record the Task-3 findings**

Append to `.superpowers/sdd/EF-322-SP7-spike.md`: `floorProofAllocKB`, the delta vs the 3,133 KB floor and vs today's 19,109 KB, and `approachesFloor`. **No commit.**

---

### Task 4: Findings doc + go/no-go, then discard the spike

**Files:**
- Create (committed): `.superpowers/sdd/EF-322-SP7-spike.md` (promote the scratch notes into a clean findings doc).
- Discard: all scratch code changes on `sp7-spike`.

**Interfaces:**
- Consumes: every recorded finding from Tasks 1–3.
- Produces: a written go/no-go recommendation the Phase-1 plan is written against.

- [ ] **Step 1: Write the clean findings doc**

Structure: (1) apportionment table (pass-1 / pass-2 / floor), (2) `RawBsonDocument` copy-vs-wrap answer + driver version, (3) the seam result — does a custom output serializer work, via which overload, with session binding, (4) floor-proof allocation vs floor, (5) **recommendation**: proceed with Approach A as specced, or pivot to the cheaper-bytes fallback (§5 gate of the spec), with the concrete reason.

- [ ] **Step 2: Verify the doc answers all three spec §11 open questions**

Cross-check against `docs/superpowers/specs/2026-07-26-native-materializer-perf-SP7-design.md` §11: RawBsonDocument copy behavior; can the serializer be supplied per-execution; the pass-1/pass-2 apportionment. Each must have a concrete answer.

- [ ] **Step 3: Commit the findings doc only**

```bash
git add .superpowers/sdd/EF-322-SP7-spike.md
git commit -m "EF-322: SP7 slice-0 spike findings (materializer one-pass feasibility)"
```

- [ ] **Step 4: Discard the throwaway code**

Cherry-pick / copy the findings-doc commit onto `EF-322-SP7-materializer-perf` (docs-only), then delete the scratch branch so no hacked production code survives:

```bash
git checkout EF-322-SP7-materializer-perf
git cherry-pick sp7-spike   # only the docs commit is on sp7-spike after reset; if code was committed too, use `git checkout sp7-spike -- .superpowers/sdd/EF-322-SP7-spike.md` then commit
git branch -D sp7-spike
```

- [ ] **Step 5: Stop for review**

Present the findings + recommendation to the user. **Do not start Phase 1** — the Phase-1 plan is written next, against these findings, and reviewed before implementation (per the design-review-before-implementation process).

---

## After this plan

The findings decide Phase 1's shape:
- **Proceed (Approach A):** write `2026-XX-XX-native-materializer-perf-SP7-phase1.md` covering P1.1 (no-op reader-source refactor of `MongoStreamingEntityMaterializerRewriter`), P1.2 (custom serializer + Query→Storage seam wiring), P1.3 (validate + benchmark) — each a stacked, reviewed slice.
- **Pivot (cheaper-bytes):** re-write Phase 1 around eliminating per-row reader/context re-allocation while keeping `RawBsonDocument`, and re-negotiate the success bar.

Phase 2 (broaden eligibility: reducer/aggregate, collection Include, reference Include streaming) is planned only after Phase 1 lands, since its retained-bytes/re-open design builds on the Phase-1 foundation.
