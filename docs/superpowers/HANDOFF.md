# Handoff — 2026-09-25 session end

**Branch:** `EF-322-Native-LINQ-rebased`, HEAD `0b0a7a6d`, working tree clean except the untracked docs
listed below. No uncommitted code changes. No stray worktrees (checked — only the two pre-existing
`EF-218`/`EF-233` worktrees and a prunable scratch trial worktree, none related to this work).

## What just finished

**Plan `docs/superpowers/plans/2026-09-25-native-join-scope-include-materialization.md` is DONE.** All 5
tasks complete, every task reviewed, final whole-branch review clean after one fix wave + scoped re-review.
Commits `fd3ede09`..`0b0a7a6d` on this branch. This fixed the *materialization* half of "an Include-wrapped
whole-entity leaf inside a native join-scope projection" (recognition was already fixed by an earlier plan,
commits `d1107a77`/`6e0446a4`) — specifically for **collection** Includes. It went through three escalating
bugs before landing (alias collision → lowerer recognition → projection-member-stack leak), one genuine
regression caught by the full regression sweep (a multi-level Include chain reading truncated collections —
fixed), and one thing the *final* review caught that overturned my own earlier conclusion: the fix's unwrap
originally fired for **reference** Includes too, turning a loud crash into silent wrong data. That's now
fixed (commit `0b0a7a6d` — reference Includes fall back to the old, safe, loud-failure behavior).

The full story, including every ruling made along the way and why, is in the SDD ledger — **preserved on
disk** (I was asked not to delete it): `.superpowers/sdd/2026-09-25-native-join-scope-include-materialization/progress.md`.
That file is git-ignored (SDD workspaces are scratch), so it won't survive `git clean -fdx`, but it will
survive normal use. Read it if you need the blow-by-blow.

## What's next — NOT started yet

**`docs/superpowers/specs/2026-09-25-reference-include-materialization-design.md`** — a follow-up design doc
I just wrote, covering the fact that reference Includes (as opposed to collection Includes) still don't
*work* in several related shapes — they now fail safely instead of silently, but that's it. The doc groups
four symptoms (one fixed-to-fail-safely, one unverified/parked, two confirmed-broken-but-untraced) and is
explicit that its own Task 1 must be a **pure investigation spike, no fix code** — determining which of two
competing hypotheses explains why two real EF Core spec tests
(`Include_reference_when_entity_in_projection`, `Outer_identifier_correctly_determined_when_doing_include_on_right_side_of_left_join`)
fail, before writing anything. This is explicitly flagged in the doc because the *original* root-cause-A
design doc (from a prior session) made exactly this mistake once already — assumed a hand-built spike was
equivalent to the real spec test without ever running the real test, and turned out to be wrong.

**Nothing has executed against this design doc.** No plan has been written for it yet. If asked to continue
this thread, the natural next step is `superpowers:writing-plans` against this design doc (same pattern as
`2026-09-25-native-join-scope-include-materialization.md` was written from its own design doc), then execute
with subagent-driven development.

## Other loose ends sitting in the repo, unrelated to the above

Three OTHER plan docs were recovered earlier this session after a computer shutdown interrupted a previous
session — they're untracked, uncommitted, and **not started**:

- `docs/superpowers/plans/2026-09-24-ef8-ef9-leftjoin-shim-native-include.md` — a real, ready-to-execute plan
  for a genuine EF8/EF9 limitation (EF Core's own nav-expansion lowers an optional reference Include to an
  internal `LeftJoin` shim that this provider's native candidate-join recognizer doesn't know about yet). This
  is the SAME gap that made one of this session's new tests need an `#if !EF8 && !EF9` guard
  (`Whole_inner_entity_leaf_that_is_also_collection_included_goes_native_under_a_left_join`, commit
  `cab78a07`). Worth executing — it's fully specified and self-contained.
- `docs/superpowers/plans/2026-09-24-native-join-scope-include-wrapped-whole-entity-leaf.md` — this is root
  cause A's OWN plan file. Root cause A itself is DONE (commits `d1107a77`/`6e0446a4`, predating this
  session's own work) but this plan file was never cleaned up/deleted after that work finished. Probably safe
  to delete once someone confirms nothing else references it, but I left it alone — not my call to make
  unilaterally.
- `docs/superpowers/plans/2026-09-24-native-orderby-conditional-sort-key.md` — unrelated ticket, appears to
  already be executed (commits reference "conditional ORDER BY" work earlier in this branch's history) —
  same situation as above, probably stale, not verified, not touched.

## If you're picking this up cold

1. `git log --oneline -15` to orient — the commit messages tell the story reasonably well on their own.
2. Read `docs/superpowers/specs/2026-09-25-reference-include-materialization-design.md` if continuing the
   reference-Include thread — it's self-contained.
3. The SDD ledger at `.superpowers/sdd/2026-09-25-native-join-scope-include-materialization/progress.md` has
   the full decision trail for everything already committed, if something there needs re-litigating.
4. Docker needs to be running for any functional/spec test — `docker info` to check, `open -a Docker` to
   start it if not (took about 20s to come up last time it needed starting this session).
