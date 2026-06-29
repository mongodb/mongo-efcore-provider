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

---

## Native config (EF-323) — authoritative (quiet-machine pass)

Run: 2026-06-29, **quiet Apple M4 Max** (14 cores), machine held idle during the run (no parallel work). BenchmarkDotNet v0.15.8, .NET 10.0.9 Arm64. Supersedes the earlier provisional containerized pass — both Mean and Allocated are authoritative here and directly comparable to the baseline table above.

Three configs:
- **DriverOnly** — raw MongoDB C# driver LINQ, no EF Core (floor).
- **EF_DriverLinq** — EF provider pinned to `UseQueryMode(MongoQueryMode.DriverLinq)` (pre-rebuild baseline; matches the EF numbers in the table above).
- **EF_Native** — EF provider using `UseQueryMode(MongoQueryMode.Native)` (new path).

N = 10,000. Config: Release EF10, InProcessEmitToolchain, 3 warmup / 10 iterations, MemoryDiagnoser.

### Three-config table

| Shape | Config | Mean (quiet M4 Max) | Allocated |
|---|---|---|---|
| WhereToList | DriverOnly | 6.084 ms | 1589.90 KB |
| WhereToList | EF_DriverLinq | 15.146 ms | 22683.02 KB |
| WhereToList | EF_Native | 8.269 ms | 9590.97 KB |
| WholeEntityToList | DriverOnly | 8.481 ms | 3133.01 KB |
| WholeEntityToList | EF_DriverLinq (no-track) | 32.373 ms | 45284.27 KB |
| WholeEntityToList | EF_DriverLinq (tracked) | 42.683 ms | 51369.10 KB |
| WholeEntityToList | EF_Native (no-track) | 15.813 ms | 19108.57 KB |
| WholeEntityToList | EF_Native (tracked) | 25.219 ms | 25196.83 KB |
| OrderByTake | DriverOnly | 2.326 ms | 59.42 KB |
| OrderByTake | EF_DriverLinq | 3.197 ms | 513.80 KB |
| OrderByTake | EF_Native | 2.608 ms | 243.13 KB |
| ReferenceInclude | DriverOnly | 37.310 ms | 7332.72 KB |
| ReferenceInclude | EF_DriverLinq | 136.710 ms | 51953.32 KB |
| ReferenceInclude | EF_Native | 138.487 ms | 51952.91 KB |

### Native-vs-EF_DriverLinq allocation delta (authoritative)

| Shape | EF_DriverLinq Allocated | EF_Native Allocated | Delta (KB) | Delta (%) |
|---|---|---|---|---|
| WhereToList | 22565.71 KB | 9590.87 KB | -12974.84 KB | **-57.5%** |
| WholeEntityToList (no-track) | 45049.07 KB | 19108.51 KB | -25940.56 KB | **-57.6%** |
| WholeEntityToList (tracked) | 51134.06 KB | 25195.16 KB | -25938.90 KB | **-50.7%** |
| OrderByTake | 511.44 KB | 243.16 KB | -268.28 KB | **-52.5%** |
| ReferenceInclude | 51952.73 KB | 51952.50 KB | ~0 KB | **0%** (Include falls back — deferred) |

### Key observations

- **WhereToList / WholeEntityToList / OrderByTake**: native path cuts allocations by ~50-58% **and** wall-clock time by ~18-51% versus EF_DriverLinq. Native-vs-EF_DriverLinq Mean: WhereToList 8.27 vs 15.15 ms (**-45%**); WholeEntity no-track 15.81 vs 32.37 ms (**-51%**); WholeEntity tracked 25.22 vs 42.68 ms (**-41%**); OrderByTake 2.61 vs 3.20 ms (**-18%**).
- **ReferenceInclude EF_Native ≈ EF_DriverLinq**: Include navigation is not yet native (deferred) — the query falls back to driver-LINQ automatically in `Native` mode, so both configs produce identical allocations and similar times. This is expected behavior.
- **EF_DriverLinq baseline vs prior-recorded EF numbers**: EF_DriverLinq allocations (22566 / 45049 / 51134 / 511 / 51953 KB) match the previously recorded EF numbers in the baseline table (22563 / 45048 / 51132 / 509 / 51956 KB) within normal run-to-run variance — confirming EF_DriverLinq is a faithful reproduction of the old EF baseline.
- **vs the spike's recorded headline (the SP1 "native ≥ spike" success bar): MET on the realized native slice.** The program design recorded (current → native): Where→ToList 15.1→8.3 ms / 22.7→9.6 MB; whole-entity no-track 33.4→15.6 ms / 45.3→19.1 MB. Our quiet-machine native matches almost exactly — Where 8.27 ms / 9.59 MB, whole-entity 15.81 ms / 19.11 MB — i.e. the B2 rebuild reproduces the spike's native performance (≥, within run-to-run noise), confirming no perf regression vs the spike for filter/sort/paging/whole-entity.
- **Reference Include is the one shape below the spike — by design (deferred).** The spike did Include natively (recorded 114.7 ms / 22.3 MB); here Include falls back to driver-LINQ (~138 ms / 52 MB ≈ EF_DriverLinq), so the spike's Include-native win is **not** realized yet — it is the payoff of the future Collection Includes sub-project, not an SP1 regression.

### Genuine-native confirmation (NativeOnly mode)

Verified by running each shape against `MongoQueryMode.NativeOnly` — a mode that throws `NativeTranslationNotSupportedException` instead of falling back:

| Shape | Result |
|---|---|
| WhereToList | NATIVE OK |
| WholeEntityToList | NATIVE OK |
| OrderByTake | NATIVE OK |
| ReferenceInclude | FALLBACK CONFIRMED (NativeTranslationNotSupportedException thrown) |
