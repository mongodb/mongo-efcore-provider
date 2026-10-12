# EF8/EF9 pre-existing test failures — HANDOFF

Branch: `native-EF-322-Native-LINQ-rebased`, pushed to `origin/EF-322-Native-LINQ-rebased` at tip `99b97be5`
(clean, no local changes ahead of/behind origin as of this handoff).

## What this session did (context, not the task)

Finished and pushed two GroupBy-native-translation slices:
- SP1-SP7 of the native GroupBy fallback-reduction design doc, squashed to `31441f03`
  (`docs/superpowers/specs/2026-09-24-native-groupby-fallback-reduction-design.md`), plus two follow-up
  fixes (`e4a88cce` doc-comment warning, `b441659a` Include-baseline regen after a shared-renderer ripple).
- A follow-on slice closing SP7's one deliberately-descoped gap (nested-construction GroupBy projection
  member, `Odata_groupby_empty_key`), squashed to `99b97be5`. JIRA ticket
  [EF-455](https://jira.mongodb.org/browse/EF-455) documents it (closed, matching the EF-447 precedent it
  built on).

Both slices are fully verified on EF10 (0 failures, full suites). EF8/EF9 each show the SAME 12 failures,
confirmed **pre-existing at the origin tip `9a649b2c`** (i.e. before either of this session's slices touched
anything) — verified directly by checking out `9a649b2c` alone into an isolated worktree and re-running these
exact tests on EF8 and EF9. Neither GroupBy slice caused or touches these.

## The task: fix these 12 pre-existing EF8/EF9-only failures

All 12 are in the Specification suite, all EF8-AND-EF9 (never EF10 — worth investigating why the split exists;
likely an `#if EF8 || EF9` / `#if !EF8` guard somewhere in the query pipeline that behaves differently, or a
nav-expansion difference between EF8/9 and EF10). They land in two buckets:

### Bucket A — `NorthwindMiscellaneousQueryMongoTest`, baseline/MQL-shape mismatch (10 failures)

- `OrderBy_object_type_server_evals` (async: True/False)
- `Projection_skip_take_projection` (async: True/False)
- `Projection_take_projection` (async: True/False)
- `Projection_skip_projection` (async: True/False)
- `Contains_over_concatenated_columns_both_fixed_length` (async: True/False)

All fail with `Assert.Equal() Failure: Strings differ` — NOT a data/behavior failure, a stale `AssertMql`
baseline. Example (`OrderBy_object_type_server_evals`):

```
Expected: "Orders.{ "$project" : { "_outer" : "$$ROO"...
Actual:   "Orders.{ "$lookup" : { "from" : "Customer"...
```

The **actual** pipeline now does a `$lookup` where the **baseline** expects a `$project`/`$$ROOT` shape — this
looks like a genuine STRUCTURAL pipeline change (not just a `$literal`-wrap ripple like this session's own two
fixes), most likely caused by origin's own recent commits at the tip of this branch before this session started:
`9a649b2c` ("native EF8/EF9 reference-Include join-scope, conditional ORDER BY, and reference-Include
materialization"), `37f615e5` ("admit MongoMathExpression as a bare-projection synthetic-alias leaf"), or
`c1d10c57` ("native EF.Functions.Like translation and Math/MathF translation"). Start by reading those three
commits' diffs — one of them likely changed how a correlated/owned-navigation projection lowers to `$lookup`
vs. the old `$project`+`$$ROOT` pattern for EF8/EF9 specifically.

**First question to answer:** is the ACTUAL (new) MQL correct (i.e. this is a legitimate shape change plus a
stale baseline — just regenerate), or is it a genuine behavior regression (wrong results)? Check with
`AssertQuery`'s own data assertion first — these tests use `AssertMql` typically as the LAST call after the
data assertion already passed (per `tests/MongoDB.EntityFrameworkCore.SpecificationTests/AGENTS.md`: "Data-gated
by construction ... a data/behavior failure never reaches the rewriter"). Since these fail at the `AssertMql`
call (not earlier), the data assertion already passed — meaning this is very likely a legitimate baseline-only
staleness, not a data bug. But confirm this reasoning against the actual base-test code before regenerating
blindly.

If confirmed baseline-only: regenerate via
`EF_TEST_REWRITE_BASELINES=1 dotnet test ... --filter "FullyQualifiedName~<Method>"` per
`tests/MongoDB.EntityFrameworkCore.SpecificationTests/AGENTS.md`, diff each change carefully (this session's own
`$literal`-wrap fixes twice needed exactly this), and confirm the new MQL shape is sane before committing.

### Bucket B — `NorthwindFunctionsQueryMongoTest.Select_mathf_round` (2 failures)

Fails with `Assert.ThrowsAny() Failure: No exception was thrown` — the test asserts
`AssertNativeTranslationFailedAsync(...)`, expecting this shape to correctly DECLINE (throw), but it now
SUCCEEDS instead. This is very likely a consequence of origin's own `c1d10c57` ("native EF.Functions.Like
translation and Math/MathF translation") commit widening Math/MathF translation to admit `MathF.Round` when
this test was written assuming it still declined. If the new native translation is CORRECT (produces the right
answer), this is a good-news case: update the test to assert success instead of failure, per the same "test
premise now false" pattern this session hit twice in the GroupBy work (SP7's Task 1 and Task 2 each rewrote a
pre-existing test whose "this shape declines" premise had become false). If the new translation is WRONG
(produces incorrect rounding, e.g. a floating-point precision mismatch between MongoDB's `$round` and .NET's
`MathF.Round`), this is a real bug in the origin commit — decline it properly instead (this branch's
established remedy for every such finding all session: decline, don't silently mis-resolve).

## Where to start

1. Read `9a649b2c`, `37f615e5`, `c1d10c57`'s diffs (`git show <hash>`) to understand what changed and why.
2. For Bucket A: confirm baseline-only via the reasoning above, then regenerate and diff carefully.
3. For Bucket B: determine correct-vs-wrong for `MathF.Round`'s new native translation, then either update the
   test's assertion (if correct) or add a decline guard (if wrong).
4. Re-run full EF8 and EF9 suites (`dotnet test ... -c "Debug EF8"` / `"Debug EF9"`, no filter) to confirm 0
   failures, then EF10 too (should remain 0, unaffected).
5. This branch tracks `origin/EF-322-Native-LINQ-rebased` — squash to one commit and push when done, following
   this session's own convention (see `31441f03`, `99b97be5` for style/message shape). Ask before merging.

## Test logs from this session (for reference, not authoritative — re-run fresh)

`/tmp/test-all-ef8-v2.log`, `/tmp/test-all-ef9-v2.log`, `/tmp/test-all-ef10-v2.log` — may be cleaned up by the
time you read this; the failure descriptions above are captured directly from them.
