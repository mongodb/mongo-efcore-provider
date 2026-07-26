# SP7 slice-0 spike — findings & Phase-1 recommendation

*Epic EF-322, sub-project SP7 (materializer perf). Companion to the design spec
`2026-07-26-native-materializer-perf-SP7-design.md` and the slice-0 plan
`../plans/2026-07-26-native-materializer-perf-SP7-slice0-spike.md`.*

Throwaway spike, executed against driver + BSON **3.9.0** / EF10 10.0.8, whole-entity no-track, N=10,000,
`MemoryDiagnoser` (deterministic byte allocation; timing not measured — go/no-go is allocation-based).
All spike code was discarded; this doc is the durable output.

---

## TL;DR

- **GO** — Approach A (a custom `IBsonSerializer<TEntity>` as the Aggregate output serializer, so
  deserialize *is* materialize) is **feasible and correct**. The driver accepts it, session/transaction
  binding is intact, and materialized values are correct.
- **But the one-pass fold alone does not reach the floor.** It cuts allocation **−36% (19,108 → 12,306 KB)**
  yet lands at **3.9× the 3,133 KB driver-only floor**, not within 2×.
- **Reaching the floor needs a *second* optimization** the spike newly identified: the materializer
  allocates a fresh `BsonDeserializationContext` **per property, per row** (+boxing) — untouched by
  double-pass elimination. **Phase 1 must include both the one-pass seam AND an allocation-free
  per-property read path** to meet the agreed "approach the floor" bar.

## Unknowns settled (spec §11)

### (b) Where the 19.1 → 3.1 MB gap comes from

`RawBsonDocument` **wraps** the cursor batch buffer — **no per-document byte copy** (confirmed two ways:
decompiling the exact 3.9.0 `RawBsonDocumentSerializer → RawBsonDocument(IByteBuffer) → ReadRawBsonDocument
→ ByteBufferStream.ReadSlice → ByteArrayBuffer.GetSlice` chain, which reuses the same `_bytes` array; and
an allocation experiment showing pass-1 sits *below* the POCO floor, impossible if bytes were copied).

| Measurement (whole-entity no-track, N=10,000) | Allocated |
|---|---:|
| driver-only floor (LINQ → POCO, single pass) | **3,133 KB** |
| pass-1-only (`RawBsonDocument` drain, discard) | **2,572 KB** |
| full native today (double-pass) | **19,108 KB** |

So **pass 1 is cheap** (~2.57 MB; cheaper than the driver floor because it builds no POCO) and **~16.5 MB
(~86%) of native allocation is pass 2** — the fresh reader/context re-open plus EF materialization.
(Correction to the plan's Task-1 formula: the two paths are not nested — pass-1 < floor — so
`passOne = X − floor` is invalid; the well-defined split is pass-1 measured directly and pass-2 = native − pass-1.)

### (a) Can a custom output serializer be supplied to `Aggregate`?

**Yes.** Both overloads compile, run, and route every row through our serializer (`DeserializeCount == 10000`,
real POCOs), with session/transaction binding intact (`Aggregate(session, p)` inside an explicit txn, and an
EF `BeginTransaction()` whole-entity query, both returned all rows). Recommended overload (mirrors today's
`RawBsonDocument` streaming branch):

```csharp
PipelineDefinition<BsonDocument, BsonDocument> basePipe = loggedStages;            // BsonDocument[]
PipelineDefinition<BsonDocument, TEntity> p =
    basePipe.As<BsonDocument, BsonDocument, TEntity>(customSerializer);
var cursor = session is { } s ? coll.Aggregate(s, p) : coll.Aggregate(p);          // IAsyncCursor<TEntity>
```

The fluent `coll.Aggregate().As(customSerializer)` also works. Both are long-standing public driver API in 3.9.0.

### (c) Floor proof — deserialize-is-materialize, one pass

Wired `MongoStreamingEntityMaterializerRewriter` to read off a passed-in `IBsonReader` (instead of
`BsonRowReader.Open(RawBsonDocument)`), compiled to `Func<QueryContext, IBsonReader, TEntity>`, and ran it as
the Aggregate output serializer's `Deserialize`.

| Measurement | Allocated |
|---|---:|
| driver-only floor | 3,133 KB |
| **one-pass floor-proof** | **12,306 KB** |
| today's native (double-pass) | 19,108 KB |

- **−6,802 KB (−36%) vs native** — removes the whole second pass's outer machinery (`RawBsonDocument`
  wrappers + fresh `BsonBinaryReader`/`ByteBufferStream`/root `BsonDeserializationContext`).
- **+9,173 KB (≈3.9×) above the floor** — does **not** reach it.
- **Correctness verified by value, not just count:** first/last `FlatItem` field values exactly match the
  seed (a mis-positioned reader would corrupt these silently).

## Why one-pass alone misses the floor (the key new finding)

The residual ~9.2 MB is a **separate** allocation source the double-pass fold never touched:
`MongoStreamingEntityMaterializerRewriter.BuildTypedRead` allocates a fresh
`BsonDeserializationContext.CreateRoot(...)` **per property, per row** (6 props × 10,000 = 60,000 contexts)
and **boxes** each `IBsonSerializer.Deserialize` result (`object` → `Convert`), plus EF's
`MaterializationContext`/entity-construction tax — none of which the driver's own POCO class-map serializer
pays. **The floor needs an allocation-free per-property typed read path in addition to the one-pass seam.**

## Design constraint surfaced for Phase 1 (tracked queries)

The driver **eagerly materializes the first cursor batch *during* `collection.Aggregate(pipeline)`** — i.e.
it calls the output serializer's `Deserialize` **before** `QueryingEnumerable` reaches
`queryContext.InitializeStateManager(...)`. In the spike a tracked query therefore hit a null `StateManager`
(NRE). This is a wiring-ordering artifact, not a property of Approach A, but **Phase 1 must initialize the
state manager (and any per-execution tracking context the serializer captures) before the cursor executes.**
The no-track path is unaffected.

## Recommendation for Phase 1

Proceed with Approach A, and **scope Phase 1 as two optimizations, not one**:

1. **P1.1 — parameterize the reader source** (no-op refactor of `MongoStreamingEntityMaterializerRewriter` to
   read off a passed-in `IBsonReader`) — as planned; the spike confirms this is the only entry-point change
   the rewriter body needs.
2. **P1.2 — custom output serializer + seam wiring** (deserialize-is-materialize via
   `PipelineDefinition.As<…,TEntity>`), **including initializing the tracking/state-manager context before
   cursor execution** (the constraint above). Expected result on its own: ~12.3 MB (−36%).
3. **P1.3 (NEW, promoted from "inherent EF tax") — allocation-free per-property typed reads**: eliminate the
   per-property `BsonDeserializationContext.CreateRoot` and the boxing in `BuildTypedRead`. This is the slice
   that actually moves 12.3 MB toward the 3.13 MB floor and is required to meet the "approach the floor" bar.
4. **P1.4 — validate + benchmark** across EF8/9/10; update `perf-baseline.md`.

The agreed success bar ("approach the floor") is **achievable but contingent on P1.3** — the one-pass seam
(P1.2) is necessary but not sufficient. If P1.3 proves harder than expected, the fallback is to ship P1.2's
−36% as a real, correct win and treat the per-property read path as a follow-on, re-negotiating the bar.
