# Native Join With a Filtered Inner Side Implementation Plan

> **For agentic workers:** execute task-by-task; steps use checkbox (`- [ ]`) syntax.

**Goal:** `Perform_identity_resolution_reuses_same_instances_across_joins` (EF10, 4 theory rows) passes under
`NativeOnly` with **correct rows** (the inner `Where` honored), and the same machinery makes a filtered join inner native
in general, without ever letting a non-native path silently drop the filter.

**Spec:** `docs/superpowers/specs/2026-10-08-native-join-filtered-inner-reference-include-design.md` — read it first,
especially "Spike results" (the inner reference `Include` needs no code; the guard is mandatory).

**Architecture:** carry the inner `Where` on the join's `LookupExpression` as `InnerPredicate`; render it inside the
`$lookup` `pipeline` at pipeline-build time through the placeholder table; refuse (never fall back) wherever a
non-native consumer would otherwise drop it.

**Tech stack / constraints:** as the parity plan (`2026-10-01-native-linq-parity.md`, Global Constraints): branch
`EF-322c`; commits `EF-322: ...`; preserve BOMs and line endings (several `src/` files are CRLF, so edit with the Edit
tool, not scripts that assume `\n`); `#if` for EF8/EF9 differences; tests run with `MONGODB_URI`/`ATLAS_URI` unset; every
admission-gate change gets a **full-suite** run on EF8, EF9 and EF10; every new guard test is proven to discriminate by
**mutation**.

**Spike artifacts (outside the repo):** the prototype diff and probe are in the Claude scratchpad
(`spike-prototype.diff`, `ZzSpikeJoinInnerFilterProbe.cs.txt`). They are a reference, not a base to commit: the prototype
skips `MarkSawNonBareJoinInner` wholesale and has no guard.

## File map

| File | Change |
|---|---|
| `src/.../Query/Expressions/LookupExpression.cs` | `InnerPredicate`, `HasInnerPredicate`, `LookupPipelineKind.FilteredJoinInner` |
| `src/.../Query/Expressions/MongoSelectDefinition.cs` | `IsFilterOnlyCollectionScan` (single shared predicate) |
| `src/.../Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs` | `TranslateJoinCore` eligibility + attach predicate; `TryConfirmReferenceIncludeChain` decline |
| `src/.../Query/NativeTranslation/MongoPipelineFactory.cs` | `RenderLookup` takes renderer + placeholders, renders `InnerPredicate` |
| `src/.../Query/NativeTranslation/MongoSelectLowerer.cs` | audit every `HasPipeline`/`PipelineKind` branch (~470-520) |
| `src/.../Query/Visitors/MongoEFToLinqTranslatingExpressionVisitor.cs` | refuse a pending lookup with `HasInnerPredicate` |
| `src/.../Query/NativeTranslation/NativeProjectionBinder.cs` | check `PipelineStages.Count` use (~2739) for the new kind |
| `tests/.../FunctionalTests/Query/NativeJoinFilteredInnerTests.cs` | new, three-mode tests |
| `tests/.../SpecificationTests/Query/NorthwindMiscellaneousQueryMongoTest.cs` | drop the `IsNativeOnly` branch, new baseline |
| `src/.../Query/AGENTS.md`, `BREAKING-CHANGES.md`, `docs/superpowers/findings/2026-10-01-native-parity/{g6-joins,main-issues}.md` | docs |

---

## Task 1: Failing tests (red)

**Files:** Create `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeJoinFilteredInnerTests.cs`.

Model (from the spike probe): `Cust { string Id; string Name }`, `Ord { int Id; int Total; string? CustId; Cust? Cust }`
with `HasOne(o => o.Cust).WithMany().HasForeignKey(o => o.CustId)`, separate collections, a unique model-cache key per
context (pattern: `NativeFilterJoinBeforeIncludeTests`). Raw `BsonDocument` seeds: customers `A1, A2, B1`; orders
`1(A1,10) 2(A1,20) 3(A1,600) 4(A2,700) 5(B1,30)`, plus order `6(Total 40, CustId "ZZ")` whose customer does not exist
(dangling FK) and order `7(Total 50, CustId null)`.

Do **not** use `NativeModeAssert.NativeAndExpected`: it asserts the DriverLinq leg equals the expectation, and here the
DriverLinq leg is expected to *throw*. Write a small local helper `RunAllModes` that runs `NativeOnly`, `Native` and
`DriverLinq` and asserts per mode.

- [ ] **Step 1: tests.** Each asserts `NativeOnly` and `Native` equal the hand-computed rows; `DriverLinq` throws
  (`NativeTranslationNotSupportedException` from the bridge guard, or `ExpressionNotSupportedException` where the driver
  itself rejects the shape — assert `ThrowsAny<Exception>` and record the type in a comment):
  1. `Filter_only_inner_of_an_inner_join` — `Cust ⨝ Ord.Where(Total < 500)` → `A1:1, A1:2, B1:5`.
  2. `Filter_only_inner_of_a_left_join` (`GroupJoin…DefaultIfEmpty`) → `A1:1, A1:2, A2:null, B1:5`.
  3. `Filter_and_reference_Include_on_the_inner` (the spec shape, with `c.Id.StartsWith("A")` on the outer) → rows
     `A1:1, A1:2`, `Cust` populated, `ReferenceEquals(c, o.Cust)` under `AsNoTrackingWithIdentityResolution`.
  4. `Reference_Include_on_a_bare_inner_is_unchanged` — control (no filter): all five matching orders, native today.
  5. `Parameterized_inner_filter_is_read_per_execution` — one `DbContext`, one lambda, `cutoff` = 25, 500, 5, `null` →
     `{1,2}`, `{1,2,5}`, `{}`, `{}` (use `NativeModeAssert.TwiceWithDifferentValues`, once per native mode).
  6. `Conjunction_in_the_inner_filter` (`Total < 500 && Id != 2`).
  7. `Dangling_fk_and_null_fk_orders_are_excluded_by_the_join` (orders 6 and 7 never appear; control for the filter).
- [ ] **Step 2: declining pins** (each: `NativeOnly` throws `NativeTranslationNotSupportedException`; `Native` equals
  `DriverLinq` where the driver can answer, otherwise both throw): inner `OrderBy(...).Take(1)`, inner `Select(o => new {...})`
  projection, inner `Distinct()`.
- [ ] **Step 3: run** `--filter FullyQualifiedName~NativeJoinFilteredInnerTests` on EF10. Expected: tests 1, 2, 3, 5, 6 FAIL
  under `NativeOnly` (refusal), 4 and 7 PASS. Record the actual pass/fail list in the commit message.
- [ ] **Step 4: commit** — `EF-322: failing tests for native joins with a filtered inner side`.

## Task 2: Part A with its guard (one commit)

Enabling eligibility without the guard regresses `DriverLinq` (spike finding 3), so these go together.

- [ ] **Step 1: `LookupExpression`.** Add `internal MongoExpression? InnerPredicate { get; set; }`,
  `internal bool HasInnerPredicate => InnerPredicate is not null`, and `LookupPipelineKind.FilteredJoinInner` (write-once,
  like the other kinds; it must not be set when the kind is already `DiscriminatorNarrowed`). Update the enum's doc comment.
  Doc the lack of `VisitChildren` (rendering is by hand-rolled switch).
- [ ] **Step 2: shared predicate.** In `MongoSelectDefinition` add `IsFilterOnlyCollectionScan`: every
  `IsBareCollectionScan` conjunct except `_pipelineOps.Count == 0`, plus `_pipelineOps.Count > 0` and every op is a
  `MongoMatchOp`. Write it as the *single* definition: refactor `IsBareCollectionScan` into a private
  `HasNoOperatorsOtherThanPipelineOps` base so the two cannot drift.
- [ ] **Step 3: `TranslateJoinCore`.** After `RebindInnerShaperToOuterQuery`, when `IsFilterOnlyCollectionScan`, the
  lookup exists, `CompositeKey == null`, and `PipelineKind == None` (so no TPH narrowing), set `InnerPredicate` to the
  `AndAlso` conjunction of the ops' predicates and `PipelineKind = FilteredJoinInner`; otherwise keep today's behavior
  (`MarkSawNonBareJoinInner`, ineligible). `joinInfo.IsNativelyEligible` uses `IsBareCollectionScan || <attached>`.
  Keep `MarkSawNonBareJoinInner` for everything that is not attached, so the flat Include-chain path is unchanged.
- [ ] **Step 4: `TryConfirmReferenceIncludeChain`.** Decline when a chain level's own join lookup `HasInnerPredicate`
  (spike finding 4). The explicit join in the spec shape is not a chain level, so test 3 still composes.
- [ ] **Step 5: rendering.** `MongoPipelineFactory.RenderStage` passes `renderer` and `placeholders` to `RenderLookup`;
  for `HasInnerPredicate` emit the `localField`/`foreignField`/`pipeline: [{ $match: renderer.Render(pred, placeholders) }]`
  form. Audit `MongoSelectLowerer.AppendLookupStages` (~470-520): the `lookup.IsReference && !lookup.HasPipeline` branch
  must still emit lookup + `$unwind` (preserving flag from the join) for a navigation-less or reference-nav lookup;
  decide explicitly for a *collection-navigation* join lookup (`ForceUnwind`, `Navigation.IsCollection`). Audit
  `NativeProjectionBinder` ~2739 (`PipelineStages.Count`) and every `PipelineKind` `switch`; add an explicit arm, never a
  default fall-through.
- [ ] **Step 6: the guard.** In `MongoEFToLinqTranslatingExpressionVisitor`'s constructor (all three call sites pass the
  pending lookups), throw `NativeTranslationNotSupportedException` when any pending lookup `HasInnerPredicate`, with a
  message naming the filtered join inner and saying the driver-LINQ path cannot honor it. One predicate, called here only.
  Verify the late-fallback path (`Native` mode, native shaper committed, a later operator declines) with a test that forces
  a late decline (pattern: `NativeArrayProjectionTests`); it must throw rather than return unfiltered rows. If a
  late-decline route cannot be constructed for a filtered join, record that in the test file's comments.
- [ ] **Step 7: tests green** (Task 1 set). Then **mutations**, each reverted after seeing red: (a) skip rendering
  `InnerPredicate` → test 1 returns unfiltered rows; (b) make the guard a no-op → the `DriverLinq` legs return rows
  instead of throwing; (c) bake the predicate (render with an empty `PlaceholderTable` and a value snapshot) → test 5 repeats
  the first rows; (d) drop the `TryConfirmReferenceIncludeChain` decline → a query-filtered `Include` target goes native.
- [ ] **Step 8: full suite** (all projects) on EF8, EF9, EF10. Expected new failures are exactly the pinned-unsupported
  tests in the spec's finding 5; any other failure is a bug in this work (re-check the two unreproduced ones in finding 6).
  Do **not** fix those pins in this commit if it makes the diff unreviewable; list them in the commit body and fix in Task 4.
- [ ] **Step 9: commit** — `EF-322: native join with a filtered inner side; refuse non-native paths that would drop the filter`.

## Task 3: The spec test

- [ ] **Step 1:** In `NorthwindMiscellaneousQueryMongoTest.Perform_identity_resolution_reuses_same_instances_across_joins`
  remove the `IsNativeOnly` refusal branch and the stale `// Fails` comment. Run the 4 rows under `NativeOnly`
  (`MONGODB_EF_QUERY_MODE=NativeOnly`) and default; both must pass.
- [ ] **Step 2:** Regenerate the baseline with `EF_TEST_REWRITE_BASELINES=1` and a tight `--filter` (see
  SpecificationTests `AGENTS.md`), `git diff`, rebuild, rerun without the variable. **Assert by eye** that the first
  `$lookup` has `"pipeline" : [{ "$match" : { "OrderID" : { "$lt" : 10500 } } }]` — the base test cannot check it.
- [ ] **Step 3:** Differential runner for EF10 (`tests/tools/native-parity-diff.sh EF10 <out> --gate`) from Git Bash; the
  spec project's `regress` list must no longer contain this test. Commit — `EF-322: Perform_identity_resolution... goes native with the inner Where honored`.

## Task 4: Flip the pinned-unsupported tests

For each test below, change the override to call the base test (so EF's in-memory oracle verifies the rows), regenerate its
baseline, and remove its `// Fails` marker only if the base test passes. A base-test row mismatch is a bug in Task 2.

- Spec: `NorthwindMiscellaneousQueryMongoTest.Join_take_count_works`;
  `NorthwindJoinQueryMongoTest.{GroupJoin_DefaultIfEmpty2, GroupJoin_SelectMany_subquery_with_filter,
  GroupJoin_SelectMany_subquery_with_filter_and_DefaultIfEmpty}`;
  `NorthwindQueryFiltersQueryMongoTest.{Included_many_to_one_query, Included_many_to_one_query2}`.
- Functional: `NativeJoinTests.Query_filtered_join_target_still_declines_cleanly_in_NativeOnly` (rename; it now goes native).
  `NativeReferenceIncludeTests.{Query_filter_on_the_included_target_still_declines, Named_query_filter_on_the_included_target_declines}`
  must **keep** declining (finding 4); if they flip, the Task 2 step 4 decline is wrong.
- [ ] One commit per spec class or one for all, whichever keeps diffs reviewable — `EF-322: flip pins for join shapes that are now native`.

## Task 5: Remaining pins and docs

- [ ] Declining pins for composite anonymous key with a filtered inner, TPH-derived inner target with a filter, collection
  `Include` combined with a filtered inner, and `ThenInclude` on the inner reference `Include` (each: `NativeOnly` throws,
  `Native` equals `DriverLinq` or both throw).
- [ ] `src/.../Query/AGENTS.md`: add invariants — "A filtered join inner is carried as `LookupExpression.InnerPredicate` and
  rendered in the `$lookup` sub-pipeline through the placeholder table"; "any non-native consumer of a lookup with
  `HasInnerPredicate` refuses"; "a filtered inner is admitted only as an explicit join, never as an Include chain level".
- [ ] `BREAKING-CHANGES.md`: the join-inner `Where` + `Include` row-set correction on EF10 (v10.0.4 returned unfiltered
  rows); explicit `DriverLinq` now throws for a filtered join inner that previously dropped the filter (with `Include`).
- [ ] `g6-joins.md` section 3.7 and `main-issues.md` M22: loud without an `Include`, silent with one; native honors the
  filter. Commit — `EF-322: document filtered join inner support`.

## Task 6: Verification

- [ ] `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF8|EF9|EF10"`; the `/test-all` skill for the full suite on all
  three versions (expected: only the intentional flips changed; baselines regenerated, nothing else).
- [ ] Differential runner on EF8, EF9, EF10 with `--gate`; ends `regress=0`. (EF8/EF9 `LeftJoin` shim shapes are a known
  separate gap: if one regresses, record it rather than widening scope.)
- [ ] Report: tests flipped, mutations run, any shape left declining.

## Self-review against the spec

Part A (Task 2), guard (Task 2 step 6), Include-chain decline (step 4), Part B tests only (Task 1 test 3/4), spec test
(Task 3), pin flips (Task 4), out-of-scope pins (Tasks 1 step 2 and 5), docs and breaking change (Task 5). Open question left
to Task 2 step 6: whether a late-decline route exists for a filtered join; the test records the answer.
