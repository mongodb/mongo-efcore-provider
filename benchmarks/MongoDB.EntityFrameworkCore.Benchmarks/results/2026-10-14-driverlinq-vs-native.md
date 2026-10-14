# Driver-LINQ vs Native — fresh comparison (branch EF-322c)

Run: 2026-10-14, Apple M4 Max, .NET 10, Release EF10, InProcessEmitToolchain, 3 warmup / 10 iterations,
MemoryDiagnoser. Server: `mongodb/mongodb-atlas-local:8` container (single-node replica set, Docker) — **not**
the local mongod used for the 2026-06 baselines, so absolute times are not comparable with `perf-baseline.md`;
compare configs within this table only. Timing is directional (machine not held idle); Allocated is deterministic.

Configs: **DriverOnly** (raw driver, floor), **EF_DriverLinq** (`UseQueryMode(DriverLinq)`),
**EF_Native** (`UseQueryMode(Native)`). The extended set gained the EF_DriverLinq/EF_Native split in this change.

## Headline (N = 10,000)

| Shape | Config | Mean | Allocated |
|---|---|---:|---:|
| WhereToList | DriverOnly | 5.61 ms | 1589.56 KB |
| WhereToList | EF_DriverLinq | 15.61 ms | 22370.86 KB |
| WhereToList | EF_Native | 5.76 ms | 2750.88 KB |
| WholeEntityToList | DriverOnly | 8.48 ms | 3132.54 KB |
| WholeEntityToList | EF_DriverLinq (no-track) | 35.55 ms | 44658.07 KB |
| WholeEntityToList | EF_DriverLinq (tracked) | 42.24 ms | 50742.35 KB |
| WholeEntityToList | EF_Native (no-track) | 9.26 ms | 5429.89 KB |
| WholeEntityToList | EF_Native (tracked) | 17.93 ms | 11518.46 KB |
| OrderByTake | DriverOnly | 2.44 ms | 59.16 KB |
| OrderByTake | EF_DriverLinq | 3.04 ms | 508.71 KB |
| OrderByTake | EF_Native | 2.49 ms | 106.42 KB |
| ReferenceInclude | DriverOnly | 32.30 ms | 7332.06 KB |
| ReferenceInclude | EF_DriverLinq | 101.93 ms | 48500.65 KB |
| ReferenceInclude | EF_Native | 80.20 ms | 9646.75 KB |

## Extended (ACCOUNT_N=200, ORDER_N=10,000, WIDE_N=5,000)

| Shape | Config | Mean | Allocated |
|---|---|---:|---:|
| ComplexQuery | DriverOnly | 27.30 ms | 5.97 MB |
| ComplexQuery | EF_DriverLinq | 57.36 ms | 56.72 MB |
| ComplexQuery | EF_Native | 57.77 ms | 56.72 MB |
| WideWholeEntity | DriverOnly | 233.05 ms | 63.87 MB |
| WideWholeEntity | EF_DriverLinq | 712.79 ms | 1342.23 MB |
| WideWholeEntity | EF_Native | 1131.36 ms | 104.34 MB |
| NestedWholeEntity | DriverOnly | 84.56 ms | 37.88 MB |
| NestedWholeEntity | EF_DriverLinq | 218.51 ms | 346.99 MB |
| NestedWholeEntity | EF_Native | 220.98 ms | 346.98 MB |

## Findings

- **Headline shapes: native is a large win.** Allocations drop 79–88% vs EF_DriverLinq (Where 22.4→2.8 MB,
  whole-entity 44.7→5.4 MB, OrderByTake 509→106 KB); whole-entity no-track time 35.6→9.3 ms (≈ DriverOnly 8.5).
  Matches the 2026-07 SP7 numbers.
- **ReferenceInclude is now native** (was a fallback at the 06-29 baseline): 48.5 MB → 9.6 MB, 102 → 80 ms.
- **ComplexQuery is identical in both modes — it falls back.** `--verify-native` (NativeOnly) rejects it. Bisected:
  `Orders.Include(o => o.Account)` alone declines under NativeOnly (so does Include+Where, Include+OrderBy), while
  owned-scalar Where and owned OrderBy/ThenBy/Skip/Take are native. `Reviews.Include(Product)` is native, so the
  gap is specific to this Order→Account Include (Order has owned types and a collection-of-owned graph). Not
  investigated further or fixed here — candidate bug/gap to file.
- **NestedWholeEntity shows no native win** (346.99 vs 346.98 MB). Whole-entity `ToList` of the deep owned graph
  *passes* NativeOnly, yet allocation is identical to driver-LINQ — suspicious: either the one-pass materializer
  doesn't engage for owned graphs, or the same path is used. Worth a follow-up.
- **WideWholeEntity: native allocates 92% less (104 MB vs 1342 MB) but is ~59% SLOWER (1131 vs 713 ms)** —
  a wall-clock regression for 400-property entities. Likely per-property read cost in the one-pass materializer.
  Single run, noisy; re-run on a quiet machine before filing.

## Reproduce

```bash
docker run -d --rm --name bench-mongo -p 27555:27017 mongodb/mongodb-atlas-local:8
export MONGODB_URI="mongodb://localhost:27555/?directConnection=true"
dotnet build benchmarks/MongoDB.EntityFrameworkCore.Benchmarks -c "Release EF10"
cd <scratch dir> && dotnet <path>/MongoDB.EntityFrameworkCore.Benchmarks.dll            # headline
cd <scratch dir> && dotnet <path>/MongoDB.EntityFrameworkCore.Benchmarks.dll --extended
dotnet <path>/MongoDB.EntityFrameworkCore.Benchmarks.dll --verify-native                # NativeOnly check
```
