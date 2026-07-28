# Slice A4 — the computed bare projection leaf (tier 2)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or
> superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Make a **computed bare projection leaf** native — `Select(b => b.Posts.Count)`,
`Select(b => b.Posts.Count(p => …))`, `Select(x => x.A * 2)`, `Select(x => (int)x.D)` — closing a blocker
recorded and deferred since step 3a. **6 specification cases.**

**Architecture:** Step 3a made a *path-addressable* bare leaf native under an alias that IS the leaf's document
path. "Tier 2" would extend that to a **computed** leaf, which has no document path, under a reserved `_v`
alias and `ProjectionAliasTier.Synthetic`. It was **built, measured and reverted** because a bare `.Count` over
a missing or explicitly-null array aborts with `MongoCommandException` under the **default `Native` mode** on
the late-decline route — the un-stripped fallback is the driver's push-down, and the driver renders a bare
`$size` where native renders `$size` over `$ifNull`. This plan ships the prerequisite first, then the
capability.

**Tech Stack:** C# / EF Core provider (EF8/EF9/EF10 via build configurations), xUnit with plain `Assert.*`
(FluentAssertions is **not** referenced in the test projects), MongoDB C# driver, TestContainers.

**Written from:** `docs/superpowers/specs/2026-08-09-a4-computed-bare-leaf-spike.md` — every figure and design
decision below is that spike's, cited by section. **Branch tip:** `85c07ba8`.

**Sizing, MEASURED and deliberately re-priced:** the stream-1 spike's **54 total / 28 sole-cause is NOT
reproduced**. A4 converts **6** cases (2 `Failed→Passed` + 4 `Failed→Failed`-with-changed-message behind stale
baselines). Do not plan against 28. This is the **fourth** consecutive slice whose cited figure over-predicted
(A2 34/44, A5 0/36, A1 28/56, A4 6/28).

---

## Global Constraints

**Every task's requirements implicitly include this section.**

- Rolling branch **`NativeQueryOngoing`**; this slice on its own branch, squashed to ONE commit, ff'd on. Keep
  the `-presquash` backup. **Do not push** — the owner pushes.
- Commit titles start with the JIRA number from Task 1.
- Full solution green on **EF8, EF9 and EF10**. **Build the three configurations SEQUENTIALLY** (~7 s each) —
  parallel builds race on `obj/project.assets.json` and emit bogus `CS0104`/`CS0115`.
- **Launch every long run from a detached `nohup`'d script and poll the log yourself in a loop.** A run started
  via the sandbox's `run_in_background` dies if an unrelated foreground bash call times out. **Never pipe a
  test run through `head`/`tail`.**
- **Never overlap a rebuild with an in-flight `--no-build` sweep** — the spike discarded an entire A/B set that
  way.
- Both `MONGODB_URI` and `ATLAS_URI` unset. Re-run any `VectorSearch` `TimeoutException` in isolation.
- **Zero `#if` lines added or removed in `.cs` under `src/`.** Check new files directly.
- **Preserve each file's BOM state.** `src/.../Query/NativeTranslation/` has none; `tests/.../FunctionalTests/Query/` does.
- Every guard test **mutation-verified**, both directions, red counts recorded.
- **Assert VALUES, never absence-of-throw and never a row count.**
- **Nativeness is proven only by a `MongoQueryMode.NativeOnly` run that succeeds**; a decline only by one that
  throws `NativeTranslationNotSupportedException`.
- **Measure spec movement by MESSAGE TRANSITION, never the pass count** — 4 of this slice's 6 cases are
  `Failed→Failed` with a changed message and are invisible to a count.
- Tag every documented claim **MEASURED / CITED / INFERRED / UNVERIFIED**; **re-sum every prose count from the
  table beside it**.
- **An unrelated uncommitted one-character whitespace edit** to
  `docs/superpowers/specs/2026-08-07-native-query-merge-plan-design.md` is the owner's. Leave it uncommitted —
  explicit `git add` paths, **never `git add -A`**.
- **Baseline (EF10) at `85c07ba8`, MEASURED:** `Native` **4593 / 0 / 17**; `MONGODB_EF_NATIVE_ONLY=1`
  **2501 / 2092 / 17**.

### Ragged fixtures are mandatory in this slice

The entire revert turned on **missing** and **explicitly-null** arrays. Any count-leaf measurement seeded only
with well-formed arrays is worthless here. Every fixture must carry all four array states — present, empty,
explicitly BSON null, and element absent — and every test must say which states it exercises.

### The `DriverLinq` escape hatch is a rubric-level obligation in this slice

MEASURED (spike §2.3): with tier 2 as prototyped, `Select(b => b.Posts.Count)` under **explicit
`MongoQueryMode.DriverLinq`** aborts with `MongoCommandException` even with **no** late decline — populating
`Projection` flips `ProjectionAnalyzer.CanPushDown` from false to true, so the driver renders the bare `Select`
instead of the mixed path folding it client-side. The versioning rubric's carve-out for the native default is
*conditional* on `DriverLinq` restoring the previous path. **Every task that admits a new shape must assert the
explicit-`DriverLinq` leg**, not just `Native` and `NativeOnly`.

---

## File Structure

| File | Responsibility |
|---|---|
| `src/.../Query/NativeTranslation/NativeProjectionBinder.cs` | the tier-2 admission (node-kind gate + `Synthetic` alias override), and the `CapturedExpression` rewrite registration |
| `src/.../Query/Expressions/MongoQueryExpression.cs` | read-only here — `CapturedExpression` is the mutation target; **do not change its shape** |
| `tests/.../FunctionalTests/Query/NativeComputedBareProjectionTests.cs` | **new** — the ragged-fixture net for all four computed leaf kinds × four array states × three modes |
| `tests/.../FunctionalTests/Query/NativeOwnedCollectionCountTests.cs` · `NativeOwnedCollectionFilteredCountTests.cs` · `NativeCastTests.cs` | the six tripwires to flip (Task 5) |
| `tests/.../UnitTests/Query/NativeTranslation/NativeProjectionBinderBareBodyTests.cs` | the admission gate and the rewrite, at unit level |
| `src/.../Query/AGENTS.md` · `docs/native-query-status-EF-322.md` | the as-built note and the slice row |

---

### Task 1: File the A4 ticket

**Files:** one JIRA issue in project `EF`. No repository change.

- [ ] **Step 1: Confirm the MCP tools are reachable** — `mcp__jira__jira_get_issue` for `EF-404`. If it errors,
  the token is missing from the session environment; stop and report. Load via `ToolSearch`
  (`select:mcp__jira__jira_create_issue,mcp__jira__jira_update_issue,mcp__jira__jira_get_issue,mcp__jira__jira_link_to_epic`)
  if not listed.

- [ ] **Step 2: Create it, then fill it.** `issue_type: "Task"`, summary
  `Native translation: computed bare projection leaf (stream 1 slice A4, tier 2)`, description
  `"Placeholder - full description follows in an update."` **The two-call pattern is required** — this instance
  stores `create_issue` descriptions raw and converts Markdown only on `update_issue`. Use `h2.` headings and
  `{code:c#}`, never Markdown `##` or triple backticks; never `#` for a numbered list.

  The description must carry: the MEASURED yield **6** cases and that the stream-1 spike's CITED **54 / 28** is
  **not reproduced** (both labelled); that tier 2 was **built, measured and reverted** at step 3a and this
  re-attempt ships its recorded prerequisite first; that the prerequisite is a null-coalescing rewrite of the
  pushed-down `.Count` body, MEASURED to render byte-identically to native; and that the shape currently
  fetches whole documents and folds client-side. Link to epic `EF-322`.

- [ ] **Step 3: Read it back** — confirm no literal `##`, no triple backticks, no unintended `h1.`, no mangled
  angle-bracket generics. Note the tool's read path partially transcodes wiki→Markdown, so uneven emphasis in
  the response is a display artifact; check the `update_issue` echo if in doubt.

- [ ] **Step 4: Report the key.** No repository change.

---

### Task 2: A4-0 — the `$ifNull` prerequisite

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeProjectionBinder.cs`
- Modify: `tests/.../UnitTests/Query/NativeTranslation/NativeProjectionBinderBareBodyTests.cs`
- Create: `tests/.../FunctionalTests/Query/NativeComputedBareProjectionTests.cs`

**Interfaces:** produces a `CapturedExpression` whose pushed-down bare collection-`.Count` body is
null-coalesced. Tasks 3–5 rely on it being in place before they admit the count kinds.

**Yield: 0 specification cases.** It is inert until Task 4 admits the shape — which is exactly why it ships
first, so the capability commit is not one that opens a `MongoCommandException` under the default mode and
closes it in the same diff.

**The fix, MEASURED (spike §3.1):** the driver renders `Select(b => (b.Posts ?? new List<Post>()).Count)` as
`{"$size": {"$ifNull": ["$Posts", []]}}` — **byte-identical to what native emits** — returning `[2,0,0,0,1]`
over the ragged seed. Measured with **no EF in the loop** (`collection.AsQueryable()`), so it is a property of
the driver's LINQ provider, not of this provider's bridge.

**The alternative that does NOT work, and would have been the natural first attempt (spike §3.3):** the `?:`
spelling renders `$cond` and **still aborts** — MongoDB evaluates the untaken branch. Do not use it.

- [ ] **Step 1: THE ORDERING CHECK — and it is a stop condition**

The spike names exactly one thing that could make this slice not worth doing: *"if A4-0 turns out to interact
badly with the two existing `CapturedExpression` mutations, the prerequisite stops being small and a 6-case
slice stops being worth it."*

`CapturedExpression` is the single input to `MongoEFToLinqTranslatingExpressionVisitor` for **every** fallback,
and `StripPushedDownSelect` already mutates it on **two** paths — the mixed path unconditionally, and the
tier-1 late-decline path conditionally. Your rewrite is a third mutation.

Establish, **by execution and not by reading**, that the three are mutually exclusive: construct a query that
reaches each of the three paths and confirm that at most one mutation applies to any single
`CapturedExpression`. The spike argues from source that a `Synthetic` rewrite fires on neither of the other
two branches, but records that as **UNVERIFIED** with no ordering test run.

**If they are not mutually exclusive — if any query can have two mutations applied, or one clobber the other —
STOP and report.** Do not design around it. That is the finding that changes the slice's economics, and the
owner should decide whether a 6-case slice still earns a larger prerequisite.

- [ ] **Step 2: Write the failing unit test for the rewrite**

In `NativeProjectionBinderBareBodyTests`, assert that after binding a bare collection-`.Count` body the
`CapturedExpression`'s projected body is the null-coalesced form, and that a bare **arithmetic** body and a
bare **cast** body are left untouched (they are measured NOT exposed — spike §2.2, so rewriting them would be
unrequested scope).

- [ ] **Step 3: Run it and confirm it fails.** The rewrite does not exist.

- [ ] **Step 4: Implement the rewrite**

Register it where the bare computed leaf is committed in `NativeProjectionBinder`, alongside the alias-override
registration — **not** at the decline site. The spike's reasoning, which you should carry into the code
comment: `CapturedExpression` is read **only** by the driver-LINQ fallback bridge, so an unconditional rewrite
at commit time is inert on the native route **and** covers the explicit-`DriverLinq` leg, which a decline-site
rewrite would not.

Scope it to the **unfiltered collection-navigation `.Count`** only. Whether a *primitive*-collection bare
`.Count` (`b.Tags.Count`) needs it is **UNVERIFIED** — it is not natively representable today, so it never
reaches this path; do not widen to it speculatively, and say so in the comment.

- [ ] **Step 5: Prove it works, by temporarily enabling tier 2 locally**

The rewrite is inert until Task 4, so it cannot be proven by a shipped path yet. **Temporarily** admit the size
node kinds in your working tree (do **not** commit that), then confirm:
- `NativeOwnedCollectionCountTests.Bare_embedded_collection_Count_projection_returns_zero_for_a_missing_or_null_array` and
- `ProjectedCollectionNormalizationTests.Bare_count_projection_returns_zero_for_a_missing_or_null_array`

both stay **green**. The spike MEASURED that both go red with `MongoCommandException` when tier 2 is enabled
*without* the rewrite — so these two committed tests are the prerequisite's real net. Record both results, then
revert the temporary admission and confirm the tree is clean.

- [ ] **Step 6: Add the ragged functional fixture**

Create `NativeComputedBareProjectionTests.cs` (**with a BOM** — check a sibling). Seed **all four array
states**: present, empty, explicitly BSON null, and element absent. It is used in full by Tasks 3–5; here it
carries the prerequisite's own coverage — the late-decline route (a captured local in `string.StartsWith`) and
the explicit-`DriverLinq` leg, both asserting values across all four states.

- [ ] **Step 7: Whole solution on all three EF versions** — 0 failures expected; the rewrite is inert.

- [ ] **Step 8: Both spec axes** — expect **zero movement** on both (`4593/0/17`, `2501/2092/17`). Retain the
  `NativeOnly` TRX; later tasks compare against it.

- [ ] **Step 9: Commit** with explicit paths.

---

### Task 3: A4-1 — tier-2 admission for arithmetic and cast leaves

**Files:** `NativeProjectionBinder.cs`, the two test files above.

**Yield: 2 specification cases** — `NorthwindSelectQueryMongoTest.Explicit_cast_in_arithmetic_operation_is_preserved`, both `async` legs. **These are the slice's only `Failed→Passed`.**

**Safe independent of Task 2:** MEASURED (spike §2.2) that arithmetic and cast leaves are **not** exposed to the
prerequisite — the driver renders them `$multiply` / `$toInt`, neither of which aborts on a missing or
explicitly-null array.

- [ ] **Step 1: Write the failing functional cases** — a bare arithmetic leaf (`Select(x => x.A * 2)`) and a
  bare cast leaf (`Select(x => (int)x.D)`) under `NativeOnly`, across all four array states where applicable,
  plus the mandatory **parameterized-`Where` late-decline leg** and the **explicit-`DriverLinq` leg**, all
  asserting values.

- [ ] **Step 2: Run and confirm they fail** (`NativeOnly` throws today).

- [ ] **Step 3: Admit the two node kinds** — extend the bare arm to `MongoBinaryExpression` and
  `MongoConvertExpression` under the reserved `_v` alias and `ProjectionAliasTier.Synthetic`, via
  `AddProjectionAliasOverride(MongoSelectDefinition.BareProjectionMemberKey, "_v", ProjectionAliasTier.Synthetic)`.

  **A trap the spike hit and recorded:** matching `MongoBinaryExpression { NodeType: … }` **silently admits
  nothing** — `MongoExpression` reports `ExpressionType.Extension`. Match on the `Operator` property.

- [ ] **Step 4: Run them.** Expect pass.

- [ ] **Step 5: Mutation-verify the node-kind gate** — relax it to plain translation success and confirm a
  **constant** leaf is admitted (which it must not be: `$project` reads a bare value as an inclusion/exclusion
  flag and a falsy constant aborts the aggregate). Record the red count.

- [ ] **Step 6: Whole solution on three EF versions; both spec axes by message transition.** Expect **2
  `Failed→Passed`, 0 `Passed→Failed`**, no baseline movement. Report the transition set by name.

- [ ] **Step 7: Commit.**

---

### Task 4: A4-2 — tier-2 admission for the two size kinds

**Files:** as Task 3, plus the 4 specification baselines.

**Yield: 4 specification cases** — `NorthwindSelectQueryMongoTest.Projecting_count_of_navigation_which_is_generic_list` and `…_generic_collection`, both `async` legs each. These are `Failed→Failed`-with-a-changed-message: they go native with **correct data** and fail only on a stale `AssertMql` baseline, so they are **invisible to a pass count**.

**Depends on Task 2** for the unfiltered owned count.

- [ ] **Step 1: Write the failing cases** — a bare unfiltered `.Count` and a bare filtered `.Count(pred)`,
  across **all four array states**, under all three modes, plus the late-decline leg. The four states are the
  point: the prerequisite exists for exactly the missing and null ones.

- [ ] **Step 2: Run and confirm they fail.**

- [ ] **Step 3: Admit `MongoSizeExpression` and `MongoFilteredSizeExpression`** on the same gate.

- [ ] **Step 4: Run, and confirm the prerequisite holds** — the two committed regression tests named in Task 2
  Step 5 must be **green**, not merely unmoved. If either is red, Task 2's rewrite is not covering this path;
  stop and report rather than widening the rewrite here.

- [ ] **Step 5: Re-baseline the 4 cases**

```bash
EF_TEST_REWRITE_BASELINES=1 dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build > $SCRATCH/rebase.log 2>&1
git diff --stat tests/MongoDB.EntityFrameworkCore.SpecificationTests/
```

Expect **only** those two tests' literals. **Read each rewritten baseline** and confirm the change is stage
order only (the spike: "the 4 cases are reference-collection counts, whose baselines move only in stage
order"). If any other baseline moves, or a shape changes beyond stage order, stop and report.

- [ ] **Step 6: Both axes, by message transition** — expect **4 `Failed→Passed`** after re-baselining, 0
  `Passed→Failed`. Re-derive the expected `NativeOnly` triple from your own measured baseline rather than
  trusting a figure.

- [ ] **Step 7: Whole solution on three EF versions. Commit.**

---

### Task 5: A4-3 — flip the six decline tripwires

**Files:** `NativeCastTests.cs`, `NativeOwnedCollectionCountTests.cs`, `NativeOwnedCollectionFilteredCountTests.cs`

**Yield: 0 cases.** These are deliberate tripwires; flipping them is a visible edit, which is why they exist.

The six, MEASURED (spike §5.3):
1. `NativeCastTests.Bare_cast_projection_leaf_declines_and_returns_correct_values`
2. `NativeOwnedCollectionCountTests.Bare_and_wrapped_count_projections_take_different_paths_from_the_same_model`
3. `NativeOwnedCollectionCountTests.Bare_embedded_collection_Count_projection_returns_correct_counts_for_present_arrays` — asserts `aggregate([])`
4. `NativeOwnedCollectionFilteredCountTests.Bare_filtered_count_projection_declines_cleanly_under_NativeOnly`
5. `NativeOwnedCollectionFilteredCountTests.Bare_filtered_count_projection_folds_client_side`
6. `NativeOwnedCollectionFilteredCountTests.Bare_filtered_count_projection_with_a_captured_parameter_declines_cleanly_in_every_mode`

- [ ] **Step 1: Flip each into a capability test**, not a weakened one: assert native routing under
  `NativeOnly` **and** the values, across the array states each already covers. **Rewrite each comment** to
  record that the lock was lifted deliberately, by which slice, and what replaced it. Do not delete any.

- [ ] **Step 2: Give each a parameterized-`Where` late-decline leg** if it does not have one. That leg is the
  only thing in the suite exercising the route where a bare projection's alias miss is silent.

- [ ] **Step 3: Handle tripwire 6 as the special case it is.** It pins a shape that hard-fails in **every**
  mode today; with tier 2 it returns data. The spike records that as INFERRED-an-improvement but **UNVERIFIED
  against an oracle**. Establish an oracle — in-memory LINQ over the same expression — and assert values, or
  say plainly that no oracle exists and pin the measured behaviour as measured rather than as correct.

- [ ] **Step 4: Whole solution on three EF versions; both spec axes (expect zero movement). Commit.**

---

### Task 6: Wrap-up — record, verify, squash

- [ ] **Step 1: Final measurement.** Both axes, re-derived from a measured baseline at the slice's base
  commit, by message transition. Expect **6 `Failed→Passed`** across the slice, `Passed→Failed` **0**, and the
  4 re-based baselines. Re-derive the triples; do not restate them.

- [ ] **Step 2: Three-EF-version whole solution** — 0 failures each.

- [ ] **Step 3: Break check.** Every touched type is `internal` and `MongoQueryMode.cs` does not exist at
  `v10.0.2` / `v9.1.2` / `v8.4.2`. **But do not stop there** — the previous slice's break check came back the
  opposite of what its plan assumed. **Probe by execution against the published packages**: what did
  `Select(b => b.Posts.Count)` and `Select(x => x.A * 2)` return at those tags, over all four array states?
  If any returns a value that this branch now changes, that is a `BREAKING-CHANGES.md` entry — and note the
  step-3a record says *"Do NOT add an entry for tier 2 (reverted, and measured throw-before/throw-after
  anyway)"*, which was true of the **reverted** prototype and must be re-tested for the shipped one.

- [ ] **Step 4: The as-built note** in `src/.../Query/AGENTS.md`. It must carry:
  - that tier 2 **returned**, what its prerequisite was, and that the prerequisite was a `CapturedExpression`
    rewrite MEASURED to render byte-identically to native — plus that the `?:` spelling does **not** work
    because `$cond` evaluates the untaken branch;
  - the **corrected** `_v` finding: unreachability of the collision follows from the **tier conditional**, not
    the alias choice — force the strip on and the late route silently returns the stored `_v` values
    (spike §4.2). The step-3a note's version of this is incomplete and must be corrected in place, not
    duplicated;
  - that the `DriverLinq` escape hatch was broken by the prototype and what closes it;
  - the measured yield **6**, and that the CITED 54/28 is **not reproduced** — with the reason (the cited
    bucket counts leaves the *translator* resolves as a value regardless of selector-body shape, so it swept in
    wrapped bodies native since EF-347/359/403; and 44 of 86 in-scope bare firings sit inside subqueries whose
    outer query is the blocker);
  - the four array states as the mandatory fixture shape for anything touching a count leaf;
  - the `MongoBinaryExpression { NodeType: … }` trap (it matches nothing — `MongoExpression` reports
    `ExpressionType.Extension`);
  - the mutation evidence from every task.

  Tag every claim; re-sum every count from its table.

- [ ] **Step 5: Status-doc row** in `docs/native-query-status-EF-322.md` §2, and update §8's running position.
  **Re-sum §2's realized total from the table** — that total has been computed from a stale figure twice on
  this branch, once inside the wave meant to close a slice out.

- [ ] **Step 6: Squash, fast-forward — do NOT push.** Keep the `-presquash` backup; verify content-identity
  with `git diff --quiet`. The message records the prerequisite, the capability, the re-priced yield with its
  "not 54/28" caveat, and the three-EF-version and both-axis results.

---

## What comes after this plan

**Re-rank before choosing the next slice.** Four consecutive slices have had their cited figures over-predict,
by three *different* mechanisms: the classifier stops at the minimal failing subtree (A5); it can name the
wrong decline site entirely (A1); and it can count a bucket whose membership is not the slice's scope (A4).
The merge plan's remaining arithmetic inherits all three. The remaining capability-A citations — A6 `Contains`
18, A13 `Not` 18, A12 `??` 22, A9 `?:` 10 — should each get the same prototype-A/B treatment **before** an
order is committed, and A6 and A13 additionally need an **aggregation-dialect renderer arm** that no plan
document has budgeted for (recorded in the slice-B note: at least 36 of the 92 sort-position cases).
