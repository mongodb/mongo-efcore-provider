# Native query path: profile, fixes and findings (branch EF-322c)

Run: 2026-10-14, AMD Ryzen Threadripper PRO 5975WX, Windows 11, .NET 10, Release EF10, BenchmarkDotNet
(InProcessEmitToolchain, 3 warmup / 10 iterations, MemoryDiagnoser). Server: `mongodb/mongodb-atlas-local:8`
(MongoDB 8.3.11) in Docker, single node. Headline set (ORDER of 10,000 flat rows, 100 products) and extended set
(accounts 200, orders 10,000, wides 5,000, mediums 10,000).

**Noise.** Each set was run twice per state. Allocations are identical run to run (within 0.1 KB). Times vary 1-3% in
most rows and up to about 8% in the worst (OrderByTake driver 3.07-3.53 ms across sittings). Differences under
roughly 8% should be read as noise. Times from earlier result files came from an Apple M4 Max and are not comparable
with these; allocations are.

## Main vs EF-322c vs driver-only (same machine, same container)

Three states, all Release EF10, all measured today:

- **main** (`66d2bcb1`, built in a separate worktree): EF with the driver-LINQ path. Main has no native translator, so this
  is what a user gets today. Mean of two BDN runs.
- **EF-322c start** (`6ce7f707`, native): the branch before this work. Mean of two runs, taken earlier in the day.
- **EF-322c final** (`90d5930d`, native): mean of two runs.
- **Driver-only (hand-written)**: the benchmarks' hand-written `IMongoCollection`/driver-LINQ code. This is a reference
  point, **not a lower bound**: it uses the driver's own serializers and POCO materialization, a different machinery from
  the provider's compiled shaper, so native can and does beat it on some shapes. Main-sitting and branch-sitting
  driver-only runs agree within noise (e.g. Wide 383-384 vs 396 ms), so one value from the branch sitting is shown.

| Shape | main (EF, driver-LINQ) | EF-322c start (native) | EF-322c final (native) | Driver-only (hand-written) |
|---|---|---|---|---|
| WhereToList | 26.7 ms / 22.1 MB | 10.0 ms / 2.69 MB | 9.7 ms / 1.73 MB | 9.7 ms / 1.55 MB |
| WholeEntity, no tracking | 51.9 ms / 44.1 MB | 18.9 ms / 5.30 MB | 16.3 ms / 3.40 MB | 17.1 ms / 3.06 MB |
| WholeEntity, tracked | 67.2 ms / 50.0 MB | ~33 ms / 11.2 MB | 31.0 ms / 9.34 MB | none |
| OrderByTake100 | 3.48 ms / 0.51 MB | 3.4 ms / 0.11 MB | 3.2 ms / 0.09 MB | 3.1 ms / 0.06 MB |
| ReferenceInclude | 178.8 ms / 50.4 MB | 127 ms / 9.4 MB | 66.6 ms / 3.78 MB | 57.6 ms / 7.16 MB |
| ComplexQuery | 108.3 ms / 56.8 MB | 50.3 ms / 11.3 MB | 40.0 ms / 3.7 MB | 46.8 ms / 6.0 MB |
| WideWholeEntity | 1222 ms / 1342 MB | ~425 ms / 104 MB | 379 ms / 27.8 MB | 396 ms / 63.8 MB |
| NestedWholeEntity | 457 ms / 348 MB | ~168 ms / 68.5 MB | 119 ms / 23.2 MB | 149 ms / 37.9 MB |
| MediumWholeEntity (40 props, added here) | not on main | not measured | 81 ms / 8.0 MB | 87 ms / 14.1 MB |
| Async Where / WholeEntity / RefInclude | not on main | not measured | 9.6 / 16.0 / 66.2 ms | 9.5 / 16.9 ms / n/a |

(Allocations in the table are KB/1024 for the headline shapes; the sources are the BDN tables in
`scratchpad/out/{main,final}/` and the earlier baselines in `scratchpad/out/`.)

Final native against main: 2.7x faster on Where, 3.2x WholeEntity, 2.2x tracked, 1.1x OrderByTake100 (fixed-cost
dominated), 2.7x ReferenceInclude, 2.7x Complex, 3.2x Wide, 3.8x Nested; allocation 13x, 13x, 5.4x, 5.7x, 13x, 15x,
48x and 15x lower. Versus the hand-written driver code, final native is within about 4% in time on Where, WholeEntity
and OrderByTake100, 16% slower on ReferenceInclude (the server-side form, see below), and faster on Complex, Wide, Nested
and Medium.

Branch-sitting driver-LINQ (EF `DriverLinq` mode on the branch, for reference): Where 27 ms / 22.0 MB, WholeEntity
53.7 ms / 44.0 MB, ReferenceInclude 153 ms / 45 MB, Complex 95.5 ms / 51 MB, Wide 1250 ms / 1.3 GB, Nested 374 ms /
316 MB. Its allocation matches main on flat shapes and is lower on Include shapes (Fix 2 and the shared materializer
changes apply to it too, Nested is 316 vs 348 MB).

`--verify-native`: NATIVE OK on all seven shapes. `--smoke` and `--extended-smoke` pass.

## Shipped fixes (same-sitting before/after in each commit)

| Commit | Change | Effect |
|---|---|---|
| `c66ba972` | Profile loop (`--profile`), async twins, BenchmarkSwitcher args | tooling only |
| `a877c2b4` | `MongoElementNameDecoder`: trie-backed `INameDecoder` replaces the string-allocating `ReadName` in the streaming materializer | WholeEntity 5430 to 3476 KB; Wide 106.8 to 28.5 MB, median 432 to 389 ms; Where 2753 to 1776 KB; Nested 70.1 to 56.1 MB |
| `852962aa` | `MongoIncludeFixups` skips EF's `SetIsLoadedWhenNoTracking` unless an `ILazyLoader` service property exists | Nested 56.1 to 23.7 MB, 165 to 127 ms; Complex 9.2 to 3.8 MB; RefInclude 7.46 to 3.86 MB |
| `c6f2cc01` | 40-property `MediumEntity` benchmarks, `--server-only`, profile flags | shows no dispatch regression at realistic width |
| `90d5930d` | A reference `$lookup` on `_id` renders `$match{as.0:{$exists:true}}` + `$set{as:{$arrayElemAt:[..,0]}}` (inner) or `$set` only (left-outer) instead of `$unwind` | RefInclude 120 to 67 ms (driver-only 56 ms); Complex 44.1 to 39.7 ms |

Tests per fix: new unit tests (name decoder 5, lazy-loader 3, lookup factory 2, lowerer 2) with a mutation check on each
gate; 278 + 2 spec baselines and 9 functional assertions updated for the new `$lookup` shape. Full Unit + Functional +
Spec on EF8 (13,245 passed), EF9 (13,604) and EF10 (13,413), 0 failed.

## Per-shape findings

### WhereToList, WholeEntityToList (flat)
Hot frames after the fixes: driver cursor and BSON read dominate; provider materializer is a small share. Allocation
gap to the driver-only run is now 186 KB (Where) and 345 KB (WholeEntity) at 10k rows, about 35 B/row for WholeEntity. Before fix 1 the top
allocator was the per-element-name `string` from `ReadName` (about 2.3 MB at 10k rows). The remaining overhead is EF's
`ConcurrencyDetector` (F2). Where time equals the driver-only run.

### WholeEntity tracked
31 ms against 16 ms no-track. The whole difference (+6 MB, +14 ms at 10k) is EF `StateManager.StartTrackingFromQuery`:
`InternalEntityEntry`, `Snapshot<6>`, identity map dictionary growth. Not provider-fixable (F3).

### OrderByTake100
Fixed-cost dominated (3 ms, 91 KB vs 63 KB). The 28 KB difference is the per-query fixed cost (F4).

### ReferenceInclude
The 2.2x gap was **server side**, not client CPU: `$lookup` followed by `$unwind` makes the server leave its
slot-based `$lookup` and fall back to the classic engine. Raw timings, 10k rows:

| Server | `$lookup` only | `$lookup`+`$unwind` | `$lookup`+`$match`+`$set` |
|---|---|---|---|
| 8.3 (atlas-local) | 48 ms | 113 ms | 61 ms |
| 8.0 (mongo:8.0 rs) | 60 ms | 199 ms | 75 ms |
| 7.0 | ~435 ms | ~435 ms | +4% (left-outer) / +11% (inner) |

Native scaled linearly before the fix (2.5 / 13.7 / 123 ms at 100 / 1000 / 10,000 rows vs driver 1.4 / 6.7 / 55.5),
so there was no client fixed or quadratic term. With a filter on the joined field after the join the old form took 328
ms vs 50 ms for the new one; with sort+limit 103 vs 50 ms. The semantics of the new form were checked in mongosh
including empty arrays, null and missing foreign keys. Allocations (3.9 MB) are now below the 7.3 MB driver-only run; the cause was not investigated (a different materialization path, not a better result than a limit).

### ComplexQuery
Native since the earlier slice; after fixes 2 and 3 it is below the driver-only run (3.7 vs 6.0 MB, 40 vs 47 ms).

### WideWholeEntity, MediumWholeEntity
Wide: element-name strings (H1) were the 40 MB gap; now 27.8 MB against 63.8 MB for the driver-only run, and time is 4% under it.
Medium (40 properties, closest to a realistic entity): 8.0 MB / 81 ms vs 14.1 MB / 87 ms; no dispatch regression.

### NestedWholeEntity
Per-include `SetIsLoadedWhenNoTracking` iterators (about 355 B per include per entity) plus name strings were the
38 MB gap. Now 23.2 MB vs 37.9 MB driver-only, 119 vs 149 ms.

### Async
Refuted (H7): async twins equal sync in time and bytes on Where, WholeEntity and ReferenceInclude.

## Especially inefficient (including things not fixed)

1. **Server `$lookup`+`$unwind` fallback to the classic engine** (8.x): 2-6x server time. Fixed for reference
   includes on `_id` (fix 3). Still present for: collection-valued joins (they need the array), nested sub-pipeline
   unwinds, reference joins on a non-`_id` key, owned-declared navigations, and the driver-LINQ bridge.
2. **EF `ConcurrencyDetector` per `MoveNext` (F2).** Allocates an AsyncLocal `ExecutionContext` +
   `OneElementAsyncLocalValueMap` + boxed Int32 on every row (about 190 B/row), which is 55% of the remaining native
   allocation on flat shapes. With `EnableThreadSafetyChecks(false)`: WholeEntity 3476 to 1601 KB (below the driver-only run),
   Where 1776 to 838 KB, Tracked 9565 to 7689 KB and 29 to 22 ms.
3. **EF identity tracking (F3).** Tracked queries are 2x time and 2.7x allocation of no-track, all EF.
4. **Per-query fixed cost (F4).** Native 1-row query is about 55 KB and 0.73 ms vs 25 KB / 0.60 ms driver-only.
   Of the 55 KB, EF's `DbContext` service scope is about 21 KB (EF-inherent), `QueryCompiler.Execute` 13.6 KB,
   `TranslateQuery` 9.6 KB, `Execute` 22 KB. Provider-avoidable: schema regeneration per context (~3.4 KB), unused typed
   `GetCollection<TEntity>` plus `AsQueryable` in `TranslateQuery` (~5.5 KB), second `GetCollection<BsonDocument>` in
   `Execute` (~5.4 KB). About 9-14 KB total. Wall time is 70% socket wait; provider `TranslateQuery` is about 4% CPU.
5. **Connection-string mode creates a MongoClient per context.** `UseMongoDB(connectionString, ...)` adds about
   15.6 KB per context (70.8 vs 55.2 KB) from client and settings creation. Lifecycle risk, not touched.

## Deferred findings, ranked by gain per cost

| # | Finding | Gain | Cost / risk |
|---|---|---|---|
| 1 | F2: skip the concurrency check for rows served from an already-fetched batch | up to -54% alloc on flat shapes, -25% time tracked | small code, but changes thread-safety-check semantics: **user's call** |
| 2 | F4 trims: cache generated schemas per model, drop unused typed collection in native path, reuse the collection in `Execute` | 9-14 KB/query (16-25% of fixed cost) | below the 20 KB threshold; the typed-collection part needs an additive internal ctor on the public record `MongoExecutableQuery` |
| 3 | Apply the `_id` lookup rewrite to nested sub-pipelines, the driver-LINQ bridge and non-`_id` unique keys | same 2x server-time gain on those shapes | needs uniqueness knowledge for non-`_id` keys; test churn |
| 4 | Per-context MongoClient in connection-string mode | 15.6 KB/context | client lifecycle and disposal semantics |
| 5 | Tracked-query overhead (F3) | up to 14 ms / 6 MB at 10k | in EF Core, not the provider |

## Server version tradeoff accepted for fix 3

The new lookup form is 2-6x faster on 8.x server time, but about 4% (left-outer) to 11% (inner) slower on 7.0, where
the classic engine runs both forms equally and the extra stages cost slightly. The 7.0 figures come from raw server
timing in `ServerOnly.cs`, not full EF runs.

## Not done / not measured

- Only EF10 was benchmarked. EF8 and EF9 were run for correctness only.
- The headline and extended sets were run against one server (atlas-local 8.3.11). The 7.0 and 8.0 numbers above come
  only from the server-only script, not full EF runs.
- Per-op GC counts are in the profile loop output but no systematic GC table was built, and no LOH check was done for
  Wide and Nested.
- NativeOnly spec on EF10 (`MONGODB_EF_QUERY_MODE=NativeOnly`): 4768 passed, 0 failed, 14 skipped.
- Fix 3 was not benchmarked on a sharded cluster or with a non-co-located foreign collection.
- The F2 and F4 mitigations were not implemented or prototyped end to end; the numbers for F2 come from toggling
  `EnableThreadSafetyChecks(false)`, those for F4 from allocation traces.
- The old squash with a `-presquash` backup step stays the user's call. Nothing was pushed.

## Reproduce

```bash
docker run -d --rm --name bench-mongo -p 27556:27017 mongodb/mongodb-atlas-local:8
export MONGODB_URI="mongodb://localhost:27556/?directConnection=true"
dotnet build benchmarks/MongoDB.EntityFrameworkCore.Benchmarks -c "Release EF10"
cd <copy of bin/Release EF10/net10.0>
dotnet MongoDB.EntityFrameworkCore.Benchmarks.dll --filter '*'               # headline
dotnet MongoDB.EntityFrameworkCore.Benchmarks.dll --extended --filter '*'    # extended
dotnet MongoDB.EntityFrameworkCore.Benchmarks.dll --verify-native
dotnet MongoDB.EntityFrameworkCore.Benchmarks.dll --profile ReferenceInclude native --seconds 30 [--rows N] [--connstr] [--nothreadsafety] [--print-commands]
dotnet MongoDB.EntityFrameworkCore.Benchmarks.dll --server-only
```
