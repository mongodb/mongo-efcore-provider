# Handoff — native perf profiling (branch EF-322c)

You are picking up a planned slice in `D:\code\provider2` (MongoDB EF Core provider), branch `EF-322c`.

**Do this:** execute `docs/superpowers/plans/2026-10-14-native-perf-profiling.md` using
`superpowers:executing-plans`, inline, no worktrees.

**What it is:** profile every existing benchmark (headline and extended, driver-only vs EF driver-LINQ vs EF native),
find big low-cost performance wins in the native path and implement those, and report anything especially
inefficient even when it can't be fixed cheaply. Investigation first: every claim needs a measurement. The plan has
seven hypotheses (H1 to H7); they are leads from reading numbers, not facts.

**Start here, in order of likely payoff:**
1. `ReferenceInclude` is 2.5x slower than the driver floor in time (80 vs 32 ms) with near-equal allocation. Client
   CPU, not server. Biggest time gap.
2. Tracked `WholeEntityToList` is 2.1x time and 3.7x allocation over the floor.
3. Per-query fixed allocation (~47 KB over the floor for a 100-row query) and the async path, which has never been
   benchmarked.
4. Allocation gaps with time already at the floor: Wide (+40 MB, suspect element-name strings), Nested (+30 MB,
   suspect per-element temporaries), ComplexQuery, Where.

**State:** `EF-322c` is pushed through `6ce7f707`. Working tree clean apart from the two new docs (plan and this file,
uncommitted). The previous slice (benchmark gaps) is done and recorded in
`benchmarks/MongoDB.EntityFrameworkCore.Benchmarks/results/2026-10-14-benchmark-gaps-fixed.md`; that file has the
"Reproduce" commands and the current numbers.

**Environment:** Windows 11, Docker is running. Use the `mongodb/mongodb-atlas-local:8` container for benchmarks and
tests, with `MONGODB_URI` and `ATLAS_URI` unset for the tests. Never the local mongod. Release EF10 for benchmarks.
Times from the earlier headline results came from a different machine (Apple M4 Max); only allocations compare
across machines. Take your own baseline first, twice, and state the noise band.

**Rules you must follow:**
- Read `AGENTS.md` (root) and `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md` first.
- Any `src/` change needs: failing test first where behavior can be pinned, full Unit, Functional and Spec suites
  green on EF8, EF9 and EF10 plus the NativeOnly spec on EF10, and mutation checks on any gate or eligibility change.
- Implement a fix only if it clears the plan's threshold (15% time or 30% allocation on a shape, or ~20 KB fixed per
  query, under ~150 lines, no public API change, no semantic change). Everything else goes in the report.
- Preserve BOMs and line endings in `src/`. Write edit scripts to a file; multi-line Bash heredocs break on quoting.
- Commit messages start `EF-322: `, with the attribution line from your session reminder. Commit per fix.
- **Do not push, squash or force anything.** git/gh auth is broken here; fetch works, pushing is the user's job.
  The plan's old squash-with-`-presquash`-backup step is still open and is the user's call.
- Out of scope: new query translations, write paths, vector search, encryption, and the
  `QueryableEncryptionSchemaGenerator.AddSchemaFields` stack overflow (another agent has that).

**Deliverable:** `benchmarks/MongoDB.EntityFrameworkCore.Benchmarks/results/<date>-native-perf-profile.md`, with a
baseline table, a section per shape (top allocators and hot frames, floor vs provider overhead), an explicit
"especially inefficient" section, shipped fixes with same-sitting before/after, deferred findings ranked by gain per
cost, and an exact "Not done" list. Keep `--verify-native` showing NATIVE OK on every shape.

**Final message to the user** should list: commits, before/after numbers, what you could not measure, the deferred
ranking, and every ruling you made.
