# Native LINQ — benchmark profiling and low-cost performance wins

**Branch:** `EF-322c` (repo `D:\code\provider2`, the MongoDB EF Core provider). Local commits only; the user pushes.
**Predecessor:** `2026-10-14-benchmark-gaps.md` (closed three gaps; results in
`benchmarks/MongoDB.EntityFrameworkCore.Benchmarks/results/2026-10-14-benchmark-gaps-fixed.md`).
**Execution:** inline, no worktrees (repo rule). Use `superpowers:executing-plans`.

## Goal

Profile every existing benchmark, native vs the raw-driver floor, and answer two questions:

1. Is there a **big performance gain for relatively low cost** still sitting in the native path? Implement those.
2. Is anything **especially inefficient**, even if it cannot be fixed cheaply? Call it out, with evidence and a cost
   estimate, so the user can decide.

This is investigation first. Do not guess at fixes. Every claim in the report needs a measurement behind it.

## Where we stand (EF10, atlas-local container, Threadripper PRO 5975WX, 2026-10-14)

Only the extended set was re-measured after the last fixes. The headline numbers below are from an Apple M4 Max
and are not comparable in time with the extended ones. Allocations are deterministic and comparable everywhere.

| Shape | Native | Driver-only floor | Gap |
|---|---|---|---|
| ReferenceInclude (10k reviews) | 80.2 ms / 9.6 MB | 32.3 ms / 7.3 MB | **2.5x time**, 1.3x alloc |
| WholeEntityToList tracked | 17.9 ms / 11.5 MB | 8.5 ms / 3.1 MB | 2.1x time, 3.7x alloc |
| WholeEntityToList no-track | 9.3 ms / 5.4 MB | 8.5 ms / 3.1 MB | 1.7x alloc |
| WhereToList | 5.8 ms / 2.75 MB | 5.6 ms / 1.59 MB | 1.7x alloc |
| OrderByTake (100 rows) | 2.49 ms / 106 KB | 2.44 ms / 59 KB | ~47 KB fixed per query |
| ComplexQuery | 52.5 ms / 11.3 MB | 49.2 ms / 6.0 MB | 1.9x alloc |
| WideWholeEntity (400 props) | 451 ms / 104 MB | 423 ms / 64 MB | 1.6x alloc, 40 MB over |
| NestedWholeEntity | 179 ms / 68.5 MB | 161 ms / 37.9 MB | 1.8x alloc, 30 MB over |

Reading the table: where time is already within ~10% of the floor, the remaining prize is allocation, not time. The
two **time** outliers are `ReferenceInclude` and the tracked variant. Start there.

## Hypotheses to test (leads, not facts)

Each is a guess from reading numbers, not from profiling. Confirm or kill each one.

- **H1 Wide: element-name strings.** Native reads each element name as a new `string` (`reader.ReadName()`); the
  driver's class-map serializer uses a name decoder / trie that avoids the allocation. 5,000 docs x 400 props is
  2M names. 40 MB / 2M is ~20 B each, which is about one short string. Look at `BuildFillLoop` / `BuildDispatch` in
  `MongoStreamingEntityMaterializerRewriter.cs` and what they pass to the reader.
- **H2 ReferenceInclude: client CPU, not server.** Allocation is close to the floor but time is 2.5x. The `$lookup`
  cost on the server is the same for both. Suspect per-row work in the include shaper or fixup (`MongoIncludeFixups.cs`,
  `Query/Visitors/...Include.cs`), or the joined document being parsed through an intermediate `BsonDocument`.
- **H3 Tracked: ValueBuffer and StateManager.** A tracking query needs a `ValueBuffer` or equivalent per row. If the
  native streaming path builds a boxed `object[]` per row on the tracked path, that is the 6 MB over no-track.
  Separate EF-inherent cost (`InternalEntityEntry`, identity map) from provider-added cost.
- **H4 Per-query fixed cost.** `OrderByTake` allocates ~47 KB over the floor for 100 rows. That is DbContext
  creation, service resolution, query-cache key, parameter extraction and pipeline rendering. Real apps run many
  small queries, so this may matter more than any large-result shape. Measure with 0/1/10-row queries.
- **H5 Nested: per-element temporaries.** ~3 KB per order over the floor. `BuildCollectionLoop` (added in the last
  slice) may allocate per-element lists, locals arrays or closures.
- **H6 Where/WholeEntity: result list growth and per-row closures.** ~100-200 B per row over the floor.
- **H7 Async path.** Every benchmark is sync. Most applications call `ToListAsync`. The async enumerator may
  allocate or yield per row. Unmeasured today.

## Method

### Measurement hygiene (do this or the numbers are noise)

- Release EF10, Docker `mongodb/mongodb-atlas-local:8` container (`docker run -d --rm --name bench-mongo -p 27556:27017 ...`,
  `MONGODB_URI=mongodb://localhost:27556/?directConnection=true`). Docker is running. Never use the local mongod.
- Nothing else heavy running. Run each baseline **twice** and record the run-to-run difference. Treat any delta under
  that noise, or under the BenchmarkDotNet error, as zero.
- Compare before/after **back to back on this machine in one sitting**. Never compare across machines for time.
- Allocation (`MemoryDiagnoser`) is the stable signal. Use it to rank; use time to confirm.
- The benchmark project builds with `-c "Release EF10"`. See `results/2026-10-14-benchmark-gaps-fixed.md` "Reproduce".

### Tools

- BenchmarkDotNet is already wired (`BenchmarkConfig.cs`, in-process emit toolchain, memory diagnoser).
- `dotnet-trace` and `dotnet-counters` (install as global tools if absent): CPU sampling and allocation sampling
  (`Microsoft-Windows-DotNETRuntime:...GC...AllocationTick` keyword, `--profile gc-verbose`).
- A dedicated **profile loop** (Task 1) so a tracer can attach to a steady-state process. Do not trace through
  BenchmarkDotNet's in-process host; its noise swamps the signal.
- Server-side truth: run the emitted pipeline with `explain("executionStats")` in the container to see what the
  server spends. Capture the pipeline with the provider's logging or the command-started event.
- Scaling runs: row counts 0, 1, 10, 100, 1,000, 10,000 to separate **fixed cost per query** from **cost per row**.
  The slope is per-row; the intercept is fixed.

## Tasks

Each task ends with its findings written to the report (Task 9). Keep a ledger per `executing-plans`.

### Task 1 — Harness changes (benchmark project only; no `src/` changes)

Files: `benchmarks/MongoDB.EntityFrameworkCore.Benchmarks/Program.cs` and new files beside it.

1. Pass `args` through to `BenchmarkSwitcher` so `--filter` works. Today `Program.cs` calls
   `BenchmarkRunner.Run<T>()` with no args.
2. Add `--profile <shape> <config> [--seconds N]`: seed once, then loop the operation for N seconds (default 30),
   printing ops/sec and a GC-count summary. Shapes: every benchmark method in both classes. Configs: `driver`,
   `driverlinq`, `native`.
3. Add scaling variants: `WholeEntityToList` and `ReferenceInclude` at 0/1/10/100/1,000/10,000 rows (a
   `[Params]` class or the profile loop with `--rows`). Add a `Take(1)` and a zero-row `Where(false)` shape for H4.
4. Add **async** twins of `WhereToList`, `WholeEntityToList` (no-track) and `ReferenceInclude` for native and
   driver-only. This is a new benchmark; label it in the report as new.
5. Add the ~40-property entity benchmark class that the previous plan skipped, if it is cheap. It exposes whether
   the wide-dispatch change regresses realistic widths. 5 and 400 properties are the only widths measured so far.
6. Keep `--verify-native` green. Keep existing benchmark method names stable so old results stay comparable.

Done when: `--smoke`, `--extended-smoke` and `--verify-native` pass; `--filter *WhereToList*` runs only those.

### Task 2 — Baseline

Run headline and extended, all three configs, on this machine. Run twice. Save raw output in the scratchpad and the
summary table in the report. This is the "before" for everything after. State the noise band.

### Task 3 — Per-query fixed cost (H4)

Use the 0-row and 1-row shapes. Allocation and time per query for driver-only, driver-LINQ and native. Then
allocation-profile the 1-row native query and list the top allocators by bytes. Split: `DbContext` creation and DI,
query compile or cache lookup, parameter extraction, pipeline render, cursor and enumerator setup, disposal. Report
bytes and microseconds per category. A fixed cost of tens of KB per query is worth fixing if it is mostly avoidable.

### Task 4 — Flat shapes: WhereToList, WholeEntityToList (no-track, tracked), OrderByTake (H3, H6)

- CPU sample and allocation sample the native path on `WholeEntityToList` at 10k rows. List top 15 by bytes and by
  inclusive time. Do the same for the driver-only floor so you subtract what the driver itself must do.
- Tracked vs no-track: attribute the 6 MB and ~8 ms difference. For each part, say whether it is EF Core's
  (`StateManager`, `InternalEntityEntry`, identity map, key comparers) or the provider's (value buffers, boxing,
  per-row delegates, key extraction). Only provider-side parts are fixable here.
- Check result-list growth: does the provider pre-size or stream into `List<T>` the way the driver does?

### Task 5 — ReferenceInclude (H2)

The biggest time gap. Do this carefully.

1. Capture the emitted pipeline. Run it with `explain("executionStats")`. Compare to the hand-written driver
   `$lookup` + `$unwind`. Is the stage order or the projection different? Is the lookup unwound server-side?
2. Time the server alone (pipeline run to completion, discarding documents) vs the full EF path. The client share is
   the target.
3. CPU-profile the native include path. Name the hot frames. Candidates: include shaper delegate, navigation fixup,
   identity resolution on a no-track query, an intermediate `BsonDocument`, `Lookup` of a clr member by reflection.
4. Scaling run: is cost linear in rows, or is there a fixed or quadratic term?

### Task 6 — Wide entity (H1)

Allocation-profile `WideWholeEntity` native vs driver-only. Count `string` allocations by size and call site. Test H1
directly: read the generated reader loop and confirm what `ReadName` variant it calls. If names are the cost, scope a
fix (name decoder / interned name table keyed on the model's element names). Estimate the saving **before** building
it. If the 40 MB is something else, say what.

Also run the new ~40-property class (Task 1.5) to make sure the switch dispatch behaves at realistic widths.

### Task 7 — Nested and ComplexQuery (H5)

- `NestedWholeEntity`: allocation-profile native vs floor, attribute the 30 MB. Inspect the code generated by
  `BuildCollectionLoop` and `BuildFillLoop` for per-element temporaries (lists, local arrays, closures, boxing).
- `ComplexQuery`: diff the native pipeline against `DriverComplexQuery()`. They should match in stage order. Attribute
  the 5 MB over the floor; the 1500 returned rows carry the whole `Order` graph plus `Account`, so part of it is the
  same nested cost as above.

### Task 8 — Async and cross-cutting (H7)

- Compare the async twins to sync: time and allocation per row. A per-row `await` or `ValueTask` box is a big find.
- Check cursor settings the provider controls: batch size, `AllowDiskUse`, whether it reads through
  `IAsyncCursor<BsonDocument>` / raw `IByteBuffer` or an intermediate document. Note defaults only; do not change
  behavior that affects semantics.
- GC behaviour under the heaviest shapes: gen0/gen1/gen2 counts per op, LOH allocations (large arrays, big lists).
  Wide and Nested should be checked for LOH.

### Task 9 — Triage, implement, report

1. Build a findings table, one row per finding: shape, evidence, measured or estimated gain (time %, allocation %),
   estimated cost (files, lines, risk), EF-Core-inherent or provider-fixable, decision.
2. **Implementation rule.** Implement a fix only if all hold:
   - it gives at least **15% time or 30% allocation** on a shape, or removes at least ~20 KB of fixed cost per query;
   - the estimate is small: roughly under 150 changed lines in `src/`, one area, no public API change;
   - it does not change results, query semantics, or the eligibility or fallback rules, **or** a gate change is
     covered by the plan's mutation and full-suite requirements below.
   Everything else goes in the report, not in code.
3. For each fix: failing test first where behavior can be pinned (allocation tests are fragile; prefer a functional
   test that proves the same results plus a benchmark delta). Commit per fix. Before/after benchmark numbers from the
   same sitting go in the commit body and the report.
4. **Required before the slice is done** for any `src/` change: full Unit, Functional and Spec suites on EF8, EF9 and
   EF10, plus the NativeOnly spec on EF10, all green; mutation checks on any gate or eligibility change (restore the
   old behavior, watch a test fail). Run with Docker (`MONGODB_URI` and `ATLAS_URI` unset).
5. Write `benchmarks/MongoDB.EntityFrameworkCore.Benchmarks/results/<date>-native-perf-profile.md`:
   - Baseline table (with noise band) and machine details.
   - One section per shape: where the time and bytes go, top allocators and hot frames, what is floor (driver must
     do it) vs what is provider overhead.
   - **"Especially inefficient"** section: anything notably wasteful even if not fixable cheaply, each with evidence
     and a cost estimate to fix. Include EF-Core-inherent costs, clearly labelled.
   - Fixes shipped, with before/after.
   - Findings deferred, ranked by gain per cost, so the user can pick the next slice.
   - A "Not done" list. Be exact about what was not measured.
6. Also run `--verify-native`; all shapes must stay `NATIVE OK`.

## Out of scope

- Changing the driver, or driver-level serializer behavior beyond what the provider configures.
- New query translations or widening native eligibility (that is a separate plan).
- Perf work on write paths (`SaveChanges`), vector search, encryption.
- Pushing. Squashing. The user does both.
- The pre-existing `QueryableEncryptionSchemaGenerator.AddSchemaFields` stack overflow on owned types with a
  `WithOwner` back-navigation. Another agent has a handoff for that.

## Constraints and gotchas (learned the hard way on this branch)

- `AGENTS.md` at the repo root and `Query/AGENTS.md` apply. Read them. Multi-version builds: configurations
  `Debug|Release EF8|EF9|EF10`; version-conditional code uses `#if EF8 || EF9` and `#if !EF8`.
- **Preserve BOMs and CRLF** in `src/` files. Many `src/` files are BOM-prefixed; `MongoQueryableMethodTranslatingExpressionVisitor.Include.cs` is BOM + LF.
  Edit with a small Python script written to a file (multi-line heredocs in the Bash tool break on quoting) that
  reads bytes, detects BOM and line endings, and writes them back.
- Commits start with a JIRA number: `EF-322: ...`, and end with the attribution line from the session reminder.
- git/gh auth is broken: fetch works, push is the user's job. Do not force anything.
- One test is known pre-existing flaky on the local mongod only:
  `NativeConditionalAndDateTimeTests.AddDays_by_a_captured_non_nullable_amount_Max_and_Min_over_a_Distinct_alias_go_native`
  (1970 date, timezone offset). It passes under Docker.
- `StreamingEligibility` and the streaming rewriter (`MongoStreamingEntityMaterializerRewriter.cs`,
  `MongoEntityMaterializerSerializer.cs`) are where whole-entity materialization is generated. Grep for the rest.
  A user-declared composite int key on a nested owned element throws in `ConstructionRewriter` today; known, not yours.

## Done means

The report exists and is honest about its gaps. Every shape has a profile, not just a number. The "especially
inefficient" list is explicit. Any shipped fix has before/after numbers from the same sitting and a green three-version
run. Deferred findings are ranked so the user can choose.
