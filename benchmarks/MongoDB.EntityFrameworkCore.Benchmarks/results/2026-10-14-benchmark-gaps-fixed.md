# Benchmark gaps fixed — wide dispatch, nested streaming, Include beside owned (branch EF-322c)

Run: 2026-10-14, AMD Ryzen Threadripper PRO 5975WX, .NET 10, Release EF10, InProcessEmitToolchain, 3 warmup /
10 iterations, MemoryDiagnoser. Server: `mongodb/mongodb-atlas-local:8` container. Extended set only
(ACCOUNT_N=200, ORDER_N=10,000, WIDE_N=5,000), plus `--verify-native`. The headline set was **not** re-run.

The "before" numbers are from `2026-10-14-driverlinq-vs-native.md`, measured on a different machine (Apple M4 Max).
**Times are therefore not comparable across the two files; allocations are deterministic and are.** Within the "after"
table every shape was measured in the same run, so the within-row comparisons below are valid. The machine was not
otherwise loaded, but timing is still directional.

## Before / after

| Shape | Config | Before (M4 Max) | After (this run) |
|---|---|---:|---:|
| ComplexQuery | EF_DriverLinq | 57.36 ms / 56.72 MB | 107.05 ms / 56.47 MB |
| ComplexQuery | EF_Native | 57.77 ms / 56.72 MB (fell back) | 52.52 ms / 11.28 MB |
| ComplexQuery | DriverOnly | 27.30 ms / 5.97 MB | 49.15 ms / 5.97 MB |
| WideWholeEntity | EF_DriverLinq | 712.79 ms / 1342.23 MB | 1362.64 ms / 1342.17 MB |
| WideWholeEntity | EF_Native | 1131.36 ms / 104.34 MB | 450.84 ms / 104.28 MB |
| WideWholeEntity | DriverOnly | 233.05 ms / 63.87 MB | 423.48 ms / 63.81 MB |
| NestedWholeEntity | EF_DriverLinq | 218.51 ms / 346.99 MB | 435.29 ms / 347.68 MB |
| NestedWholeEntity | EF_Native | 220.98 ms / 346.98 MB | 179.19 ms / 68.50 MB |
| NestedWholeEntity | DriverOnly | 84.56 ms / 37.88 MB | 160.98 ms / 37.88 MB |

`--verify-native` (NativeOnly): all seven shapes report `NATIVE OK`, including `ComplexQuery` (was FALLBACK).

## Acceptance

- **No extended shape has EF_Native slower than EF_DriverLinq.** ComplexQuery 52.5 vs 107.1 ms, WideWholeEntity
  450.8 vs 1362.6 ms, NestedWholeEntity 179.2 vs 435.3 ms.
- **WideWholeEntity ≈ DriverOnly:** EF_Native is 450.8 ms against DriverOnly 423.5 ms in the same run (about 6% above).
  Before, native was slower than driver-LINQ by about 59% on the other machine.
- **NestedWholeEntity allocation:** 68.50 MB against the 37.88 MB driver floor (about 1.8x), down from 346.98 MB (-80%).
- **ComplexQuery is native under NativeOnly** and allocates 11.28 MB against 56.47 MB for driver-LINQ (-80%).

## Changes

- `be5f1bf4` — streaming materializer dispatches element names through one `Expression.Switch` instead of a linear
  `string.Equals` chain (the wide-entity gap).
- `a71d56bd` — owned-collection elements that own further navigations stream; `StreamingEligibility` admits owned
  subtrees inside an element (the nested gap).
- `77c626e6` — an owned auto-include sibling is transparent to the reference-Include recognizer (the ComplexQuery gap).

## Not done

- The headline set was not re-run, so the headline table in `2026-10-14-driverlinq-vs-native.md` is unchanged.
- The planned ~40-property benchmark class (to expose a regression at realistic widths) was not added.
- The 2,000-row vs 10,000-row discrepancy for NestedWholeEntity (native ~10 MB below driver-LINQ at 2,000 rows, equal at
  10,000) was not re-measured; the "before" state no longer exists to measure it against.
- Only EF10 was benchmarked.

## Reproduce

```bash
docker run -d --rm --name bench-mongo -p 27556:27017 mongodb/mongodb-atlas-local:8
export MONGODB_URI="mongodb://localhost:27556/?directConnection=true"
dotnet build benchmarks/MongoDB.EntityFrameworkCore.Benchmarks -c "Release EF10"
cd <scratch dir> && dotnet <path>/MongoDB.EntityFrameworkCore.Benchmarks.dll --extended
dotnet <path>/MongoDB.EntityFrameworkCore.Benchmarks.dll --verify-native
```
