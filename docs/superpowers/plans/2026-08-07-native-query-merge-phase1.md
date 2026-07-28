# Native-query merge, phase 1 — deferral tracking and the stream-1 spike

> **⚠ HISTORICAL EXECUTION RECORD — EXECUTED AND COMPLETE. DO NOT PLAN FROM ITS NUMBERS.** *(Note added
> 2026-08-07 on the final whole-phase review.)* This plan was written **before** the stream-1 spike it
> commissions in Task 3, and it is deliberately left as written so the record of what was executed stays
> intact. Consequently its figures are the pre-spike ones and are **stale by design** — "three streams" (there
> are four), "+922 → 82.2%" (measured: **+904 → 3331/4075 = 81.7%**), "588 cases" (measured: **580**, with a
> realistic yield of **474 sole-cause / ≈508 after all of stream 1 / ≈570 after streams 1 and 2**), and the
> straggler row's "~12" (**12 = 8 to EF-397 + 4 to EF-382**). **The current plan of record is
> `docs/superpowers/specs/2026-08-07-native-query-merge-plan-design.md`; the current measurement is
> `docs/superpowers/specs/2026-08-07-stream1-translator-breadth-spike.md`; the resuming agent's entry point is
> `docs/native-query-status-EF-322.md`.**

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or
> superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Discharge the "good tracking" half of the merge bar, and produce the measured decomposition that
stream 1's implementation plan will be written from.

**Architecture:** Spec `docs/superpowers/specs/2026-08-07-native-query-merge-plan-design.md` targets +922 spec
cases (59.6% → 82.2%) across three streams. Stream 1 (translator breadth, 588 cases, ~20 features in one
1478-line class) is too large to plan without measurement, so this plan files the deferral tickets and then
spikes stream 1. **Stream 1's implementation plan is written from Task 3's output, not guessed here.**

**Tech Stack:** C# / EF Core provider, xUnit + FluentAssertions, MongoDB C# driver, TestContainers
(`mongodb/mongodb-atlas-local`), JIRA via MCP.

## Global Constraints

Copied verbatim from the spec's §8, and from the conventions this branch is held to. **Every task's
requirements implicitly include this section.**

- Branch **`NativeQueryOngoing`**; slices go on their own branch, squash to one commit, ff-only onto the
  rolling branch. **Never force-push.** Keep a `-presquash` backup.
- Commit and PR titles start with a JIRA number: `EF-1234: Description`.
- Full solution green on **EF8, EF9 and EF10** (`dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"`, and
  the EF8/EF9 equivalents).
- Both `MONGODB_URI` and `ATLAS_URI` **unset** — TestContainers boots a real `mongodb/mongodb-atlas-local`, so
  Atlas-gated tests run for real and each `dotnet test` process gets its own container.
- **NEVER pipe `dotnet test` through `tail` or `head`** — it masks the exit code and truncates per-project
  summaries. Redirect to a file and read the file.
- **Rebuild before every measurement run**, including after reverting a mutation. A task in this branch
  measured stale binaries and had to redo an entire round.
- **Zero `#if` lines added or removed in `.cs` under `src/`.** The tracked-file grep misses new files — check
  them directly.
- **Classify `Assert.Throws` failures FIRST** when bucketing by message; the message quotes the inner
  exception and a naive substring match over-counted by 149.
- **Measure wins by message TRANSITION, not by failing-name set** — a name-set diff reported 2 wins where
  there were 74 in slice 3a.
- Baseline at `e1fb753d` (EF10): default `Native` **4593 passed / 0 failed / 17 skipped**;
  `MONGODB_EF_NATIVE_ONLY=1` **2427 passed / 2166 failed / 17 skipped**.
- Each subagent uses its **own uniquely-named scratchpad subdirectory** under
  `/private/tmp/claude-502/-Users-arthur-vickers-code-mongo-efcore-provider/64deed06-9987-4415-a02f-41f99679c826/scratchpad/`.
  Do not clean up other agents' directories. Remove any worktree you create; verify with `git worktree list`.
  The three `.claude/worktrees/agent-*` worktrees belong to other sessions — leave them alone.
- Tag every documented claim **MEASURED / CITED / INFERRED / UNVERIFIED**. This project has had six documents
  corrected for confidently-wrong claims; provenance is part of the claim.

---

## File Structure

| File | Responsibility |
|---|---|
| JIRA `EF` project (6 new issues) | the deferral tracking half of the merge bar |
| `docs/native-query-status-EF-322.md` | §9.8 execution order replaced by the spec's sequence; deferred rows gain ticket numbers |
| `docs/superpowers/specs/2026-08-07-stream1-translator-breadth-spike.md` | **new** — Task 3's measured decomposition of the 588 |
| `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.cs` | read-only in this plan (1478 lines); the spike reports whether it needs splitting before stream 1 edits it |

No `src/` changes in this plan. Tasks 1–2 are tracking; Task 3 is measurement in a throwaway worktree.

---

### Task 1: File the six deferral tickets

**Files:**
- Create: 6 JIRA issues in project `EF`
- Modify: none

**Interfaces:**
- Consumes: the deferral table in spec §4.
- Produces: six issue keys, referenced by Task 2 when it rewrites the status doc's execution order.

Existing tickets that must **not** be duplicated: **EF-247** (non-constant regex, status `Blocked`),
**EF-355**, **EF-375**, **EF-376**, **EF-377**, **EF-380**, **EF-381** (joins-stream residuals), **EF-382**
(`arrayField.Contains(constant)`), **EF-390** (dotted owned-hop scalar), **EF-391** (bucket re-attribution).

- [ ] **Step 1: Confirm the JIRA MCP tools are reachable**

Call `mcp__jira__jira_get_issue` for `EF-391`. If it errors, the MCP server's token is missing from the
session environment — stop and report; do not proceed to create issues blind.

- [ ] **Step 2: Create the six issues**

For each row below: call `mcp__jira__jira_create_issue` with `project_key: "EF"`, the summary given, the issue
type given, and `description: "Placeholder - full description follows in an update."` **This two-call pattern
is required** — this JIRA instance stores `create_issue` descriptions raw and only converts Markdown on
`update_issue`.

| # | Summary | Type | Cases |
|---|---|---|---:|
| 1 | `Native translation: joins and cross-collection navigation breadth` | Task | 373 |
| 2 | `Native translation: GroupBy breadth` | Task | 130 |
| 3 | `Native translation: composite primary-key member access` | Task | 116 |
| 4 | `Native translation: composition relaxations after a bare projection (step 3d)` | Task | 18 |
| 5 | `Native translation: Not over an unsupported subtree` | Task | 8 |
| 6 | `Native translation: residual single-shape operator gaps` | Task | ~12 |

- [ ] **Step 3: Fill each description via `mcp__jira__jira_update_issue`**

Each description must contain, in this order: the measured case count and that it was measured at `e1fb753d`
by decline-site instrumentation; the **sole-cause** count where it differs materially from the total (issue 3
is 116 total but only **12** sole-cause — say so, because its real yield is far below its headline); that the
shape **falls back and returns correct results today**, so this is a coverage gap and not a correctness defect;
and a pointer to `docs/superpowers/specs/2026-08-07-native-query-merge-plan-design.md` §4 and status-doc §9.

Use `h2.` for headings and `{code:c#}` for code — **not** Markdown `##` or triple backticks. Do not use `#` for
numbered lists; it renders as an `h1`. Use `* *(1)*` style instead.

- [ ] **Step 4: Verify each issue rendered correctly**

Call `mcp__jira__jira_get_issue` for each new key and read the `description` back. Confirm no literal `##`,
no ` ``` `, and no unintended `h1.` headings. Fix with a further `update_issue` if any is wrong.

- [ ] **Step 5: Audit the four correctness gaps that ship**

Spec §4 admits **EF-380**, **EF-375**, **EF-390** and **EF-355** as shipping correctness gaps, under the
owner's policy that they be *well-defined, uncommon and tracked*. That policy is only satisfied if each ticket
actually says so. For each of the four, `mcp__jira__jira_get_issue` and confirm the description carries:

1. a **reproduction** — the query shape, the model shape it needs, and the observed-vs-expected result;
2. an explicit statement of whether the failure is **silent** (wrong data, no exception) or **loud**;
3. the mode(s) it reproduces under — default `Native`, `NativeOnly`, `DriverLinq`.

Where any of the three is missing, add it with `mcp__jira__jira_update_issue`. **Do not soften the wording**:
if a ticket describes silent wrong data, the description must say "silent wrong data" in those words, because
this is the class of defect this branch has repeatedly shipped past reviewers. EF-390 is the model to copy —
it already carries all three.

If a ticket turns out to describe something *common* rather than uncommon, that breaks the spec's admission
criterion. **Stop and report it** rather than filing it away — it may need to come into the plan the way
EF-356 did.

- [ ] **Step 6: Commit nothing**

No repository change in this task. Record the six new keys, and the outcome of the Step 5 audit, in your
report — Task 2 needs the keys.

---

### Task 2: Replace the status doc's execution order with the merge plan

**Files:**
- Modify: `docs/native-query-status-EF-322.md` — §9.8 (execution order), §8 (bottom line), the deferred rows in §4/§5

**Interfaces:**
- Consumes: Task 1's six issue keys; spec §3, §4, §9.
- Produces: a status doc whose plan of record is the merge plan, not parity/cutover.

§9.8 currently reads as an order that ends in retiring driver-LINQ. That is no longer the plan of record: the
driver path **ships** in the first release and is retired later as a separate project.

- [ ] **Step 1: Read the current §9.8 and §8**

Run: `grep -n "^### 9.8" -A 60 docs/native-query-status-EF-322.md`

- [ ] **Step 2: Rewrite §9.8 as the merge sequence**

Replace the ordered list with spec §9's seven steps. **Correct in place** — quote what the old order said and
state what replaced it, as this document does elsewhere; do not delete the old order silently. State
explicitly that **joins (373) and GroupBy (130) are deferred**, that this reverses the previous ranking which
put joins first, and that the reversal follows from the measured attribution in §9.

- [ ] **Step 3: Add ticket numbers to every deferred row**

Every row in spec §4's table gets its issue key from Task 1 (or its existing key: EF-247 for regex). A
deferred row without a key fails the merge bar's "good tracking" requirement.

- [ ] **Step 4: Update §8's bottom line**

State the target (82.2%), the three streams, and that retirement is a later phase. Keep the existing measured
triples; do not re-derive them here.

- [ ] **Step 5: Verify no test movement**

Run:
```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10" > /tmp/b.log 2>&1; echo "build=$?"
dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build > /tmp/nat.log 2>&1; grep -E "Passed!|Failed:" /tmp/nat.log
```
Expected: `Failed: 0, Passed: 4593, Skipped: 17`. This task is docs-only; any movement is a red flag to
investigate, not to explain away.

- [ ] **Step 6: Commit**

```bash
git add docs/native-query-status-EF-322.md
git commit -m "EF-322: replace the cutover execution order with the merge plan"
```

---

### Task 3: Spike stream 1 — decompose the 588

**Files:**
- Create: `docs/superpowers/specs/2026-08-07-stream1-translator-breadth-spike.md`
- Read: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.cs` (1478 lines)
- Modify (throwaway worktree only): the instrumentation described in status-doc §9.0

**Interfaces:**
- Consumes: spec §3 stream 1; the §9.0 measurement method.
- Produces: the per-feature-group table (group, total cases, sole-cause, position(s), what the translator
  needs) and a proposed slice split. **Stream 1's implementation plan is written from this and cannot be
  written before it.**

The spec asserts that ~20 features recur across predicate, sort-key and projection-leaf position and are one
capability. That is **CITED** from the 2026-08-06 analysis, not re-derived. This spike re-derives it.

- [ ] **Step 1: Create a throwaway worktree and re-instrument**

```bash
SCRATCH=<your scratchpad>
git worktree add $SCRATCH/wt e1fb753d
```
Apply the status-doc §9.0 instrumentation in the worktree: `[CallerMemberName]`/`[CallerLineNumber]` on
`MarkNotNativelyRepresentable`, the binder's per-`return false` reasons, and appending both to the `NativeOnly`
throw reason so the `.trx` carries per-test attribution.

- [ ] **Step 2: Verify the instrumentation is behaviour-preserving**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10" > $SCRATCH/b.log 2>&1
MONGODB_EF_NATIVE_ONLY=1 dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --logger "trx;LogFileName=s1.trx" --results-directory $SCRATCH > $SCRATCH/run.log 2>&1
grep -E "Failed:" $SCRATCH/run.log
```
Expected: `Failed: 2166, Passed: 2427, Skipped: 17` — **exactly** the baseline. If it differs, the
instrumentation changed behaviour; fix that before trusting any attribution.

- [ ] **Step 3: Attribute the 588 by feature group**

Parse the `.trx`, classifying `Assert.Throws` **first**. For each feature group the spec names — casts (72),
tuple/anonymous comparison (50), `EF.Property` (48), bare constant/parameter (40), `Nullable.Value` (38),
client-collection `Contains` (36), entity equality (34), `??` (32), string concat (32), `?:` (26), and the tail
— record: total cases, **sole-cause** count, which positions it appears in (predicate / sort-key /
projection-leaf), and whether the spec's count reproduces.

**Report any group whose count does not reproduce.** The spec's figures are CITED; a mismatch here is a
finding, not an error to smooth over.

- [ ] **Step 4: Establish whether these really are one capability**

For at least three groups, determine by reading whether the predicate-position and projection-position paths
share a translation entry point or merely resemble each other. The spec's whole slice split rests on "one
mechanism"; if it is two, say so — that changes the plan.

- [ ] **Step 5: Assess the file**

`MongoExpressionTranslator.cs` is 1478 lines and stream 1 will add ~20 features to it. Recommend explicitly
whether it should be split before or during stream 1, and along what boundary. Do not propose unrelated
refactoring.

- [ ] **Step 6: Write the findings doc**

`docs/superpowers/specs/2026-08-07-stream1-translator-breadth-spike.md`, following the shape of
`2026-08-06-step3-projection-spike-findings.md`. Must contain: the per-group table; the one-capability verdict
with evidence; the file recommendation; a **proposed slice split with a case count per slice**; and every
claim tagged MEASURED / INFERRED / UNVERIFIED.

- [ ] **Step 7: Remove the worktree and verify the tree is clean**

```bash
git worktree remove --force $SCRATCH/wt
git worktree list   # expect only the main tree + the three agent-* worktrees
git status --short  # expect only the new findings doc
```

- [ ] **Step 8: Commit**

```bash
git add docs/superpowers/specs/2026-08-07-stream1-translator-breadth-spike.md
git commit -m "EF-322: spike the translator-breadth decomposition for stream 1"
```

---

## What comes after this plan

**Stream 1's implementation plan is written from Task 3's findings**, one slice per feature group, sized by the
measured per-group counts. It is deliberately not enumerated here: writing bite-sized steps for 20 features
before reading the translator would mean inventing signatures and test bodies, which is the exact failure this
branch has been corrected for repeatedly.

Then, per spec §9: the **re-measurement checkpoint** (mandatory — §7 explains that sole-cause is a leverage
proxy, and if stream 1 under-delivers, that is the moment to pull joins or GroupBy back in), then **slice 3b**
(fixes EF-356), then **stream 2**, then the architecture record and final measurement.

Every one of those stages gets its own plan, written after the stage before it has been measured.
