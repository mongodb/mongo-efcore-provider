# EF-368 — re-measured pre-slice `NativeOnly` baseline

Measured on branch `EF-368` after rebasing onto the ported `NativeQueryOngoing` tip
`7af4190b` (`EF-370: correct required-navigation $unwind semantics and stop dropping
composed operators (ported)`). The rebase produced no conflicts; the four EF-368
docs-only commits (spike findings, design, plan, plan-implementation-detail) now sit
directly on top of `7af4190b`.

## Commands run (verbatim, in order)

Build:

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"
```

Baseline test run — **`MONGODB_EF_NATIVE_ONLY=1` was added** to the brief's Step 2
command. The brief's literal command (no env var beyond `MONGODB_URI=`/`ATLAS_URI=`)
runs the four Include classes under the default `Native` mode (driver-LINQ fallback
allowed) and returns **0 failures** — it cannot measure what this slice is meant to
close, because the fallback masks every native gap. The step's own title
("pre-slice `NativeOnly` baseline") and the design doc
(`docs/superpowers/specs/2026-08-04-native-reference-include-design.md:277`, *"the
measurement task is therefore a `NativeOnly` sweep on the ported base"*) both call
for the `NativeOnly` mode flip, which the codebase gates behind
`MONGODB_EF_NATIVE_ONLY=1` (see
`tests/MongoDB.EntityFrameworkCore.SpecificationTests/Utilities/MongoTestStore.cs:45`
and `tests/MongoDB.EntityFrameworkCore.SpecificationTests/AGENTS.md`). The command
actually run:

```bash
MONGODB_URI= ATLAS_URI= MONGODB_EF_NATIVE_ONLY=1 dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build \
  --filter "FullyQualifiedName~NorthwindInclude|FullyQualifiedName~NorthwindEFPropertyInclude|FullyQualifiedName~NorthwindStringInclude|FullyQualifiedName~NorthwindIncludeNoTracking" \
  --logger "trx;LogFileName=/tmp/ef368-baseline-EF10.trx"
```

EF major: **EF10** (`-c "Debug EF10"`), matching the brief.

Overall run outcome (all four Include classes, every method, both `async` values):
**Failed: 498, Passed: 454, Skipped: 0, Total: 952.**

## Extracting the in-scope cases

The brief's Step-3 extraction script does a **substring** match (`m in testName`)
against the seven method-name fragments. Because `"Include_reference"` is a
substring of several *out-of-scope* method names in the same classes (e.g.
`Include_reference_GroupBy_Select`, `Include_reference_Join_GroupBy_Select`,
`Include_reference_SelectMany_GroupBy_Select`), running that script verbatim
over-matches: **192 hits** (140 Failed / 52 Passed) instead of the intended
7 methods × 4 classes × 2 `async` = 56.

Re-running the extraction with an exact method-name match (parsing the method name
between the last `.` and the `(async: ...)` suffix, then checking set membership
against the seven names) gives exactly **56 cases**, matching the spike's strict
count.

## Raw outcome counts (in-scope, exact match, 56 cases)

| Outcome | Count |
|---|---|
| Failed | **56** |
| Passed | 0 |

All 56 in-scope cases fail under `NativeOnly` on the ported base. None pass.

## Per-test breakdown

Methods (7), each failing across all four `NorthwindInclude*` classes, both
`async: False` and `async: True` (56 = 7 × 4 × 2):

- `Include_reference`
- `Include_reference_alias_generation`
- `Include_reference_with_filter`
- `Include_reference_with_filter_reordered`
- `Include_reference_distinct_is_server_evaluated`
- `Include_reference_single_or_default_when_no_result`
- `Include_empty_reference_sets_IsLoaded`

Classes:

- `NorthwindIncludeQueryMongoTest`
- `NorthwindEFPropertyIncludeQueryMongoTest`
- `NorthwindStringIncludeQueryMongoTest`
- `NorthwindIncludeNoTrackingQueryMongoTest`

Every one of the 56 (method × class × async) combinations is `Failed`.

## Assessment against the spike's expectation

The spike's Appendix B strict count ("Axis 1: `NativeOnly` Failed → Passed") was
**56 = 7 methods × 4 classes × 2 async**, measured at `365391f`. The re-measurement
here, taken after EF-366, EF-367, and the EF-370 port (at ported tip `7af4190b`),
reproduces **the same 56, all still `Failed` under `NativeOnly`** — the count did
not move. This is consistent with the spike's expectation: the design doc predicted
the number *could* have shifted because of the three intervening changes, but for
this specific 56-case set it did not. The number is not "copied forward" here — it
was independently re-measured on the ported base and happens to match.

This is the exit criterion for Task 8; the spike's figure of 56 was measured at
`365391f`, before EF-366, EF-367 and the EF-370 port, and is not used.
