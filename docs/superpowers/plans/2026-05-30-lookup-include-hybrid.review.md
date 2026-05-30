# Review of `2026-05-30-lookup-include-hybrid.md`

Reviewer: claude (carrying full EF-117 implementation context)
Date: 2026-05-30
Reviewed plan: `docs/superpowers/plans/2026-05-30-lookup-include-hybrid.md`
Reference doc: `~/code/provider2/include-implementations-comparison.md`

## Verdict

The plan's **strategic direction is correct** and its **staging is sound**:
hybrid `$lookup`-default + fan-out fallback is the right end state, the
incremental gating preserves green-bar at each step, and the reference
to B's source by file+line is honest about what's a port vs. invention.
Approving the direction. There are **3 blocking issues** that need
resolution before Stage 0 can run, **8 substantive concerns** that
should shape the work, and several smaller refinements.

The plan also under-sells two genuine wins of the hybrid model — they
should be called out in the rationale: $lookup naturally fixes A's
known `AsNoTrackingWithIdentityResolution` gap, and it eliminates the
N+1 round-trip cost that's the biggest perf risk in the shipping A.

---

## Blocking — must resolve before Stage 0

### B1. Branch already exists

> Task 0.1: `git checkout EF-117a; git checkout -b EF-117b`

`EF-117b` is **already the current branch** (post-review iterations 1
and 2 — commits `8aaf8e5` "Address review findings" and `ade279b`
"Address remaining nits" landed there). `git checkout -b EF-117b`
will fail with *"a branch named 'EF-117b' already exists."*

**Options:**
- Use a different branch name (`EF-117c`, `EF-117-lookup`).
- Or build on top of the existing `EF-117b` (which already contains
  the review fixes from `8aaf8e5` / `ade279b` that we want to keep).

Recommendation: branch as `EF-117c` off the current `EF-117b` head so
the iteration-1/2 fixes are inherited. Reflect this in the plan.

### B2. Stage 0 "regression floor" command runs only one suite

> "Run: dotnet test … --filter \"FullyQualifiedName~NorthwindInclude\""
> "Expected: PASS (record the passing count — this is the regression floor)"

That filter covers only the 4 NorthwindInclude* spec classes. The
**actual current zero-failure baseline spans the entire spec project
and three EF version targets** (verified in commit `ade279b`):

| Config | Result |
|---|---|
| Debug EF8  | 0 / 4714 passed, 11 skipped |
| Debug EF9  | 0 / 4858 passed, 11 skipped |
| Debug EF10 | 0 / 4418 passed, 14 skipped |

Plus IncludeTests (10/10 on EF10), OwnedEntityTests (70/70), UnitTests
(260/260). A Stage-0 floor that only checks NorthwindInclude on EF10
**will not catch regressions in NorthwindQueryFilters*,
NorthwindWhere*, NorthwindSelect*, Mapping/BuiltInDataTypesMongoTest,
or anything not under NorthwindInclude\*** — all of which had EF-117
overrides updated in the staged work and could be broken by the port.

**Fix:** Stage 0 floor command must run the full spec project on EF10
*and* invoke `/test-all` on EF8/EF9 once to confirm the multi-EF
baseline. Record all three configurations' counts.

### B3. Stage 1.3 unit MQL test is impractical as specified

> "add a unit-level MQL test that constructs a `MongoQueryExpression`,
> calls `AddLookup(new LookupExpression(nav))`, runs the translator,
> and asserts the produced pipeline JSON contains `"$lookup"`"

There is **no existing unit-test harness** for `MongoEFToLinqTranslatingExpressionVisitor`
that constructs a `MongoQueryExpression` standalone. The translator
needs a `QueryContext` (live MongoClient via `MongoClientWrapper`),
the `BsonSerializerFactory`, and the full `MongoQueryCompilationContext`
— `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/` does not have a
"build a query expression and translate it" pattern. Confirmed by
grepping `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/`: every
existing test queries through `DbContext` end-to-end.

**Fix:** drop the unit-MQL test from Stage 1.3 and rely on Stage 2's
functional test for `$lookup` emission verification. Stage 1.3 only
needs to confirm the code compiles and is wired into the translator
call chain — no test required at that stage. Note the trade-off:
later regression on `AppendLookupStages` (without a routing change)
won't be caught by Stage 1 alone, but Stage 2 catches it within ~one
task of the port landing.

---

## Substantive — should shape the work

### S1. Plan under-sells two key wins

Two of the strongest selling points for the hybrid are missing from
the rationale:

- **$lookup fixes the `AsNoTrackingWithIdentityResolution` gap.**
  A's `docs/EF-117-include-overview.md` calls this out as a known
  limitation of fan-out (each sub-query has its own materialization
  scope; identity resolution doesn't span outer + sub-queries). $lookup
  is a single materialization, so identity resolution works for free
  there. This is a *functional correctness* improvement, not just
  perf — add it to the goals.

- **$lookup eliminates the N+1 round-trip cost.** A's overview
  documents this as the highest-priority follow-up perf issue
  (Northwind `Orders.Include(o => o.Customer)` = 830 round-trips).
  $lookup turns this into 1 round-trip. The plan mentions this once
  in passing; it should be the headline rationale.

**Fix:** Add a "Why this matters" subsection up top calling out both
wins explicitly, so reviewers understand the *correctness* impact of
the hybrid is not just performance.

### S2. Tracking-mode propagation in the post-hybrid world is under-specified

Stage 4 of the staged work added `ApplyTrackingBehavior` to
`MongoIncludeCompiler` so the outer's per-query
`AsNoTracking` / `AsNoTrackingWithIdentityResolution` propagates to
the sub-query (because each sub-query is a fresh
`dbContext.Set<T>()`).

For the $lookup path this plumbing is **not needed** — the entities
are materialized through the same query as the outer, so the outer's
tracking mode applies automatically. But for the fan-out *fallback*
(collection→collection ThenInclude, M2M), `ApplyTrackingBehavior`
remains essential.

The plan doesn't:
- State that `ApplyTrackingBehavior` is fan-out-only.
- Document that the $lookup path inherits tracking behavior implicitly.
- Add a test asserting AsNoTracking + collection→collection ThenInclude
  (the fallback path) still propagates correctly.

**Fix:** Stage 7 should explicitly retain the
`ApplyTrackingBehavior` plumbing (don't strip it as "dead code") and
add a fan-out fallback tracking test.

### S3. Convention change decision needs richer criteria

> Task 8.1: "If everything passes → do not port the convention change …"

This decision rule is incomplete. The convention change in B
(`MongoRelationshipDiscoveryConvention.ShouldBeOwnedType` returning
`false` when a type is already an independent entity) changes default
*model-discovery* behavior. The current Stage 0 spec tests configure
relationships **explicitly** via fluent API (`HasMany().WithOne()`),
so they won't surface the difference. But users writing fresh code
without explicit fluent config get a different document shape
depending on whether the convention change is in.

The criterion "does the spec suite pass" is necessary but not
sufficient. The plan should also:

- Identify which user-facing scenarios convention-discover a
  cross-collection relationship (vs. embedded). Currently the
  provider's convention treats any entity-type-targeted nav as
  embedded; this is the default that B changes.
- Check whether there are any IncludeTests (or new tests added in this
  port) that don't configure relationships explicitly — those would
  silently embed without the change.
- If convention change is needed, the plan correctly flags
  `BREAKING-CHANGES.md` and `MongoRelationshipDiscoveryConventionTests`.
  But it should also note that the change affects **persisted
  document shape for new entities**, which is a Behavior break per
  the AGENTS.md versioning rules, not just an API break.

**Fix:** Sharpen Task 8.1's decision criteria. Add a concrete test:
"build a model with no explicit relationship config, save and query;
confirm document shape matches A's current behavior or document the
new shape."

### S4. Multi-level reference chain port (Stage 4) risk is acknowledged but not de-risked

> "Known risk to call out at review time: the multi-level
> `_outer`/`_inner` prefixing (Stage 4) is the most fragile port …
> Budget extra review there."

Calling out risk is good but doesn't de-risk it. B's `_outer`/`_inner`
machinery exists because B chose to keep the driver's `LeftJoin` in
the tree for multi-level chains — the prefixing rule then encodes
which side of the driver-LeftJoin a navigation's declaring type sits
on. A different design — chaining $lookups directly on top of each
other (each new $lookup reads from the previous $lookup's output
field) — avoids the TransparentIdentifier prefixing entirely.

**Suggestion:** before Stage 4, evaluate whether chained $lookups
without going through `LeftJoin` is feasible. If yes, that's a much
simpler model than B's port. If no, document why $lookup chaining
alone is insufficient (server-side semantics? join semantics
mismatch?). Either way the decision should be explicit in the plan.

If `LeftJoin` + `_outer`/`_inner` is chosen, add a sub-task to verify
that A's `IncludeJoinUnwrapper` (which rewrites *single-level* synthetic
joins) does not interfere with multi-level driver-LeftJoin shapes B
relies on. The unwrapper must be gated to only fire for the patterns
the fan-out path needs.

### S5. `IncludeJoinUnwrapper` interaction with `$lookup` reference path

Stage 3 says: *"Keep A's `IncludeJoinUnwrapper` so the
`IncludeExpression` is surfaced uniformly, then register a
`LookupExpression(navigation)`."*

This is plausible but needs verification. A's
`IncludeJoinUnwrapper` rewrites
`Join/LeftJoin + Select(IncludeExpression(o.Outer, o.Inner, nav))` →
`Select(p => IncludeExpression(p, default(TInner), nav))`. The
rewrite **drops the inner entity from the tree** (substitutes
`default(TInner)`) because the fan-out path materializes the inner
via `LoadReference`. For the $lookup path:

- Does dropping the inner break the shaper's reads of `_lookup_<Nav>`?
  (The shaper is supposed to read from the lookup alias, not the
  default expression — so probably no — but it should be verified.)
- Does the `LeftJoin` case still need to be unwrapped, or does B's
  $lookup builder need the original `LeftJoin` shape preserved?

**Fix:** Add a Stage 3 sub-task to confirm `IncludeJoinUnwrapper`
runs *before* the `ServerLookup` classifier decides, and the
`LookupExpression` builder uses navigation metadata (not the rewritten
`IncludeExpression.NavigationExpression`). This is the same separation
A's `BuildCrossCollectionLoaderCall` relies on — should hold here too.

### S6. Spec test rebaseline scope is much larger than the plan suggests

> Stage 2 step 5: "Update any spec override whose MQL baseline changed
> from a fan-out `$match` to a `$lookup`"

The plan treats this as a per-stage cleanup task. In reality, every
spec test that exercised the routed shape **and had a captured MQL
baseline** needs its baseline rewritten. From the Stage 5 progress doc:
~150 overrides were converted during the staged work, each with a
captured MQL baseline. When Stage 2/3/4/5/6 routes these shapes to
$lookup, *every one* of those baselines becomes stale.

The provider's experience with the EF baseline rewriter
(documented in `docs/EF-117-include-overview.md` discovery #6) is that
running the rewriter on the whole project twice corrupts ~530
baselines. The mitigation — running per-class via `--filter` — works
but takes time and discipline.

**Fix:** Stage 8.2 ("full spec sweep + ledger") is currently treated as
a single task. It's really ~5 hours of per-class rebaselining with
high risk of corruption if rushed. Either:
- Split into per-suite sub-tasks (one commit per Northwind class).
- Or write a wrapper script that runs `EF_TEST_REWRITE_BASELINES=1`
  per-class in sequence with build steps between, mirroring the
  approach that worked in the staged work.

Either way, budget this as ~5x the apparent effort and call it out as
a known time sink.

### S7. AsSplitQuery / opt-out is mentioned but not planned

> "Neither implements split query (`AsSplitQuery` /
> `NorthwindSplitIncludeQueryMongoTest` is absent in both)."

The hybrid is a natural place to add split-query semantics: `$lookup`
becomes "single query," fan-out becomes "split query," and
`AsSplitQuery()` / `AsSingleQuery()` operators can drive the routing
explicitly. The plan's `ChooseStrategy` is the right hook.

The plan doesn't include this. Recommend adding it as Stage 9 (or
explicitly out-of-scope follow-up) — once both paths are working and
routing is centralized, exposing the operator is small and gives
users an escape hatch for the cases where $lookup's payload-blowup
risk outweighs its round-trip advantage.

### S8. Performance implications under high cardinality unaddressed

`$lookup` for collection navigations produces a *nested array per
principal*. For `Customer.Orders` over 100 customers averaging 100
orders each, the response is 100 documents × ~100 nested orders =
~10k embedded docs in a single response. MongoDB has a 16MB BSON
document size limit; an unrestricted `$lookup` chain can blow past it.

The plan should:
- Add a Stage 6 (or 7) note about response-size considerations for
  collection `$lookup`s on high-cardinality data.
- Identify whether B's tests stress this (likely not — Northwind is
  small).
- Document that `$lookup` is server-side and shifts cost from
  round-trips to memory/CPU on the MongoDB cluster.

This isn't blocking the hybrid, but it's an honest design caveat
that should appear in the rationale + `docs/EF-117-include-overview.md`
follow-up.

---

## Refinements — smaller improvements

### R1. `LookupIncludeTests.cs` duplicates `IncludeTests.cs` model

> Task 0.3: "copy the `Customer`/`Order` setup and `CreateContext`
> pattern from `IncludeTests.cs`. Repeat the model here — do not share"

Duplicating the model across two files for the same Customer/Order
shape adds maintenance cost. Suggestion: extend `IncludeTests.cs`
with `[Fact]`s that assert MQL pipelines (using a `TestMqlLoggerFactory`
hook). The existing `IncludeTests` already has `Customer`/`Order`
classes, fixtures, and the right `IgnoreCacheKeyFactory` plumbing.

### R2. `IncludeStrategyDefaults.Default` constant is misleading

```csharp
internal static class IncludeStrategyDefaults
{
    public const IncludeStrategy Default = IncludeStrategy.ClientFanOut;
}
```

A `Default` constant suggests "this is what you get if you don't
specify." But the actual routing (`ChooseStrategy`) doesn't consult
`IncludeStrategyDefaults.Default` — it makes its own decision per
include. The constant only exists to satisfy the Stage 0
characterization test.

Suggestion: drop the constant. Have the Stage 0 test call
`ChooseStrategy(...)` directly with a representative include and
assert it returns `ClientFanOut`. That's the actual contract.

### R3. Code-style consistency with iteration-2 fixes

The post-review code (commits `8aaf8e5` and `ade279b`) standardized:

- Canonical `QueryableMethods.Select`/`QueryableMethods.Join` reference
  equality (not `Method.Name == nameof(...)`).
- Structural TransparentIdentifier check (not type-name string).

When porting B's code (which uses string-name checks), the porter
must convert to A's idiom. The plan doesn't flag this. **Fix:** add
a porting-convention note: "When porting from B, use canonical
`QueryableMethods` constants and structural parameter checks per the
post-review style; do not copy B's string-name patterns verbatim."

### R4. `HasNestedInclude` should be factored, not duplicated

> "Add `HasNestedInclude` (detects a nested `IncludeExpression` …
> reuse the walking logic already in `ExtractIncludeChainPath`)."

`ExtractIncludeChainPath` returns the path; `HasNestedInclude` returns
a boolean. Don't write a second walker. Factor the shared walking
into a helper that yields each nested `IncludeExpression`'s
`INavigation` lazily; `ExtractIncludeChainPath` joins their names and
`HasNestedInclude` returns `.Any()`.

### R5. Plan references review files that exist in the working tree

The working tree contains `review-EF-117ba.md` and
`review-EF-117bb.md` (review iterations 1 and 2 of the staged EF-117
work). The plan should be aware these exist — they shaped the post-review
fixes that landed in `8aaf8e5` and `ade279b`. The plan's "porting from B"
work should respect the resolutions in those reviews (e.g., don't
re-introduce the `TransparentIdentifier` string check that R3 above
flagged).

### R6. BREAKING-CHANGES.md placement

The plan mentions adding entries to `BREAKING-CHANGES.md` in Stage 8
for the convention change (if ported) and for the exception-type
changes A's staged work already introduced. The latter — exception-type
changes for M2M and shadow keys — should have landed in the staged
EF-117 work, not in the hybrid plan. They're **already in the
shipping code** as of commit `ade279b` and are still missing from
`BREAKING-CHANGES.md` (the api-stability-reviewer's iter-2 finding).

**Fix:** Add `BREAKING-CHANGES.md` entries to a pre-hybrid clean-up
task (or to Stage 0 of this plan) for changes already in the code,
separately from any new changes the hybrid introduces.

### R7. MongoDB minimum version check for pipeline `$lookup`

Stage 6 uses `$lookup` with `let` + `$expr` + appended pipeline stages.
This requires MongoDB 3.6+. The driver's minimum supported MongoDB
version is currently MongoDB 5.0+ (per the driver's compatibility
matrix), so this is fine — but the plan should confirm explicitly
since pipeline-form `$lookup` is the foundation of Stage 6.

### R8. Self-referencing navigation handling (B has, A doesn't)

The comparison doc shows B handles `Staff.Manager`-style
self-referencing navigations explicitly with a self-join branch;
A has no coverage for this.

The plan doesn't mention self-references. With the hybrid, this
becomes a Stage 3/4-adjacent case: $lookup on the same collection
with `localField` ≠ `foreignField`. Should "just work" with the
ported `LookupExpression` if it doesn't special-case same-collection
joins, but it deserves an explicit test in Stage 3 or 4.

### R9. Dead-code cleanup for the now-unused threading

Stage 1's progress note in `EF-117-include-overview.md` discovery
section mentioned that `BsonSerializerFactory` and
`QueryContextParameter` are threaded into the removing visitor for
the include path but **never actually consumed** (because everything
routes through `Set<T>()`). The plan should call out that the hybrid
work is a natural place to either:
- Delete the unused threading, or
- Use it for the $lookup shaper reads (it might be needed there).

### R10. Test fixtures for B's $lookup-asserting tests need a live MongoDB

The plan's Stage 1.3 — and several later stages — write tests that
assert MQL pipeline content. The provider's `TestMqlLoggerFactory`
captures MQL by running the query, so all these tests need a live
MongoDB. Confirm with the executor that Docker is running before
each MQL-asserting stage (the plan mentions this once at the top
under "MongoDB is required"; reinforce it in each stage that adds
MQL assertions).

---

## Summary

**Approve direction.** The plan correctly identifies the right end
state and stages the work to preserve A's green-bar throughout. The
blocking issues (B1–B3) are mechanical and easily fixed before
starting. The substantive concerns (S1–S8) shape the work but don't
change the overall direction.

**Top three pre-Stage-0 actions:**

1. Fix the branch name (`EF-117c` or build on top of existing `EF-117b`).
2. Broaden the Stage 0 floor to the full spec project on all three EF
   versions, not just NorthwindInclude on EF10.
3. Drop the unworkable unit-MQL test in Stage 1.3; rely on Stage 2's
   functional test for first $lookup-emission verification.

**Top three things to call out in the rationale that the plan currently
under-sells:**

1. $lookup fixes A's documented `AsNoTrackingWithIdentityResolution`
   correctness gap.
2. $lookup eliminates the N+1 round-trip cost that's A's biggest perf
   risk.
3. The plan's Stage 8.2 spec-sweep + ledger update is realistically
   ~5× the effort it appears, because every routed shape needs an MQL
   baseline rewrite under the rewriter's known non-idempotence
   constraint.

**Most important non-blocking risk:** Stage 4's
`_outer`/`_inner` prefixing port from B. Recommend evaluating chained
$lookups (without driver-LeftJoin) as a simpler alternative before
committing to the port.
