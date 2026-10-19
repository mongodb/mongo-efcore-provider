# SP6 — COMPLETE

Branch: `native-EF-322-Native-LINQ-rebased`. Plan: `docs/superpowers/plans/2026-09-25-native-groupby-sp6-post-group-composition.md`.

**Status: SP6 is done.** All 3 plan tasks landed, the final-review Critical finding was fixed and verified
by mutation, and a fourth doorway into the same bug class (discovered by the re-review itself, then fixed on
the owner's explicit "fix it now" instruction) is also fixed and verified. Nothing outstanding.

## Full commit history for this slice (oldest first)

`ea162a74` Task 1 (GroupBy after Union/Concat) → `d8e8442e` Task 1 fix-round (escalated a genuine, unrelated
pre-existing bug via a `KNOWN BUG` code comment, not fixed — see below) → `c5f7a15b` Task 2 (Skip/Take
between GroupBy and its terminal Select) → `4c6c38ee` Task 3 verify → `754b8934` plan doc → `d4a78071`
handoff draft 1 → `772f3d70` final-review fix wave (3 silent-wrong-data declines, verified by mutation) →
`a7e3d033` handoff draft 2 (recording the re-review's own new finding) → `17104c26` the 4th-doorway fix
(tighten `IsPlainGroupBySelect`, verified by mutation).

## What SP6 delivered

- `Union_simple_groupby` and `GroupBy_skip_0_take_0_aggregate` (the plan's own 2 target spec tests) now go
  native. `NativeOnly` GroupBy-class failure count: 26 → 22 (raw test count; 13 → 11 method names).
- Along the way, closed a silent-wrong-data bug class this session's own Task 2 introduced (a bare `GroupBy`
  with `Skip`/`Take` composed before its terminal Select could silently drop or misorder paging in four
  distinct places — a bare-terminal-aggregate, a later Where/OrderBy on the same result, a nested second
  GroupBy, and — the one found last — a `Union`/`Concat` operand). All four are now declined cleanly instead
  of silently mis-resolving.
- Also closed, as a side effect of the same gate fix, a genuinely OLDER pre-existing gap (from an earlier
  "SP2" slice): a `GroupHavingPredicate` (a HAVING `Where`) on a `Union`/`Concat` operand was also silently
  dropped. That's now declined too.

## Known, deliberately NOT fixed, documented instead

`MongoSelectLowerer.cs` has a `KNOWN BUG` comment (added during Task 1's own fix round, tagged `EF-TBD`) at
its `SetOperation.OperandsProjected` branch: a projected-Distinct operand's own grouping gets emitted in the
wrong pipeline position (after `$unionWith` instead of before it) when an outer `GroupBy` composes on top of
a `Union`/`Concat` of two projected-Distinct operands — this crashes loudly ("Document element ... is
missing") rather than returning silently wrong data, and was ruled out of SP6's scope (genuinely pre-existing,
unrelated to this branch's own work). No ticket was filed for it during this session — worth doing before
someone else hits the crash cold.

## Next steps (not started this session)

- File a ticket for the `KNOWN BUG` comment above if one doesn't already exist.
- SP7-SP9 per the design doc (`docs/superpowers/specs/2026-09-24-native-groupby-fallback-reduction-design.md`)
  — re-verify each slice's target-test shapes against the actual EF Core base-test source before planning,
  same discipline every slice through SP6 has needed. Note the SP7 NAME COLLISION already flagged in the
  plan doc's own "After this plan" section and in memory `sp7-planning`/`native-cutover-order` — confirm
  which SP7 is meant before reusing the name.
- **This branch tracks `origin/EF-322-Native-LINQ-rebased`, not `main`.** Ask the user before merging,
  pushing, or squashing anything — the standing workflow on this project squashes each slice onto a rolling
  branch (`NativeQueryOngoing`), and the owner has previously chosen "keep as-is for now" rather than
  integrate immediately at the end of a slice. Don't assume the answer this time either.
