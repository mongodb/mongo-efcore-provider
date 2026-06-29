# Perf baseline — main (current provider + driver-only floor)

Env: BenchmarkDotNet v0.15.8, macOS Tahoe 26.5.1 (25F80) [Darwin 25.5.0]
     Apple M4 Max, 1 CPU, 14 logical and 14 physical cores
     .NET SDK 10.0.301
       [Host] : .NET 10.0.9 (10.0.9, 10.0.926.27113), Arm64 RyuJIT armv8.0-a

Config: Release EF10, InProcessEmitToolchain, 3 warmup / 10 iterations, MemoryDiagnoser.
Runs: headline set 2026-06-23, extended set 2026-06-24 (same host/config; ComplexQuery DriverOnly re-run 2026-06-24 after pipeline + materialization fix).

Note: every row is **current-provider baseline** + the **driver-only floor**. EF-Native columns are
added per shape as later sub-projects gain native support for it.

## Headline cases (`--` default)

N = 10,000.

| Shape | Config | Mean | Allocated |
|---|---|---|---|
| WhereToList | DriverOnly | 7.273 ms | 1589.87 KB |
| WhereToList | EF | 14.643 ms | 22563.32 KB |
| WholeEntityToList | DriverOnly | 8.160 ms | 3133.36 KB |
| WholeEntityToList | EF (no-track) | 31.831 ms | 45047.81 KB |
| WholeEntityToList | EF (tracked) | 38.366 ms | 51132.49 KB |
| OrderByTake | DriverOnly | 2.435 ms | 59.48 KB |
| OrderByTake | EF | 3.343 ms | 509 KB |
| ReferenceInclude | DriverOnly | 37.887 ms | 7333.73 KB |
| ReferenceInclude | EF | 143.064 ms | 51956 KB |

## Extended cases (`--extended`)

ACCOUNT_N=200, ORDER_N=10,000, WIDE_N=5,000. Order shape: 3 lines × 2 discounts.

| Method                   | Config     | Mean      | Error     | StdDev    | Allocated  |
|--------------------------|------------|----------:|----------:|----------:|-----------:|
| ComplexQuery             | DriverOnly |  30.57 ms |  0.435 ms |  0.287 ms |    5.97 MB |
| ComplexQuery             | EF         |  61.49 ms |  0.655 ms |  0.390 ms |   56.82 MB |
| WideWholeEntity          | DriverOnly | 225.85 ms |  3.328 ms |  1.980 ms |   63.85 MB |
| WideWholeEntity          | EF         | 705.60 ms | 17.207 ms | 11.381 ms | 1342.21 MB |
| NestedWholeEntity        | DriverOnly |  80.11 ms |  1.865 ms |  1.234 ms |   37.89 MB |
| NestedWholeEntity        | EF         | 238.71 ms |  6.500 ms |  4.299 ms |  347.68 MB |

## Caveats

- Measured with BenchmarkDotNet's InProcess toolchain (no separate process boundary — a known accuracy trade-off vs the default out-of-process runner; adequate for relative floor-vs-overhead).
- Every operation is a real round-trip to a single local mongod; numbers are host-specific and not portable across environments (a shared/Atlas target shows higher variance). 3 warmup / 10 iterations.
- Each EF op constructs a new DbContext (realistic per-unit-of-work usage) while the driver reuses one IMongoCollection — a deliberate asymmetry.
- EF (tracked) measures change-tracking/snapshot overhead, not relationship fixup (FlatItem has no navigations).
- Aborted runs leak GUID-named `ef_bench_*` databases (cleanup runs only in GlobalCleanup).
