# Reference Include materialization gap — Design

**Status: IMPLEMENTED.** See `docs/superpowers/plans/2026-09-27-reference-include-materialization.md` for the
executed plan and `docs/superpowers/findings/2026-09-27-shape3-code-path-findings.md` for the Task 1
investigation findings (Hypothesis A confirmed) that this design's fix was built on. Do not re-open this as an
active investigation.

**Ticket:** EF-322, follow-up (working name: "root cause A3" — a sibling to
`2026-09-25-native-join-scope-include-materialization-design.md`'s root cause A2, but for **reference**
Includes rather than collection Includes).

**Depends on / relates to:**
- `docs/superpowers/specs/2026-09-24-native-join-scope-include-wrapped-whole-entity-leaf-design.md` (root
  cause A — recognition fix, committed).
- `docs/superpowers/specs/2026-09-25-native-join-scope-include-materialization-design.md` (root cause A2 —
  materialization fix for **collection** Includes, committed, plan
  `docs/superpowers/plans/2026-09-25-native-join-scope-include-materialization.md`). That plan's final
  whole-branch review is what surfaced this doc's problem: it caught that the A2 fix's unwrap in
  `BindResultMember` originally fired for reference Includes too (with no lookup-registration logic for
  them), turning a loud crash into silent wrong data. That specific symptom is **already fixed** (commit
  `0b0a7a6d` — the unwrap now only takes the fixed path for an all-collection Include chain; a reference
  Include anywhere in the chain falls through to the pre-existing, loud-failure fallback). **This doc is
  about making reference Includes actually work, not just fail safely.**

## Problem

Reference Includes (`Order.Customer`, `Order.Owner` — a required or optional single-entity navigation, as
opposed to a collection navigation like `Owner.Orders`) do not materialize correctly in at least three related
but structurally different shapes. Confidence level and evidence are marked per shape — this doc's own Task 1
should be a spike to nail down the parts marked "hypothesized," not proceed on them as given.

### Shape 1 — CONFIRMED: reference Include inside a native join-scope leaf now declines loudly (was: silent null)

```csharp
db.Orders.Include(o => o.Owner).Join(db.Owners, o => o.OwnerId, w => w.Id, (o, w) => new { o, w.Name })
```

Before `0b0a7a6d`: silently returned `Owner = null` for every row under `MongoQueryMode.NativeOnly`, even
rows with a real matching `Owner` — no exception. After `0b0a7a6d`: throws (a `FormatException` in the one
reproduction tested — see `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeJoinTests.cs`,
`Whole_entity_leaf_that_is_also_reference_included_declines_rather_than_silently_returning_null`, committed
in `0b0a7a6d`). This is now **safe** (a loud failure, not silent wrong data) but not **useful** — the shape
still doesn't work. Root cause, precisely: `MongoQueryableMethodTranslatingExpressionVisitor.BindResultMember`
(the wrapped-leaf-join arm's per-member binder) only knows how to register a `$lookup` for a **collection**
Include — that logic lives entirely inside `MongoProjectionBindingExpressionVisitor.cs`'s `case
IncludeExpression` block's `if (!includableNavigation.IsEmbedded() && includableNavigation.IsCollection)`
branch. A reference Include never reaches any lookup-registration code at all when folded through this
specific arm.

### Shape 2 — UNVERIFIED, PARKED at A2's final review: the same gap may exist for the join's INNER leaf

```csharp
db.Owners.Join(db.Orders.Include(r => r.Owner), o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
```

`NativeJoinScopeProjectionBinder.cs:232-234` has the **same** unguarded `IncludeExpression` unwrap
(`while (unwrappedLeafBody is IncludeExpression include) { unwrappedLeafBody = include.EntityExpression; }`)
on the **recognition/staging** side — it decides whether to stage this leaf as a native `$project` entry, with
no `IsCollection` check either. If it stages a reference-Include-wrapped Inner leaf as natively representable,
but `BindResultMember`'s **materialization**-side fix (Shape 1, above) declines the corresponding member on
the read side, the two halves disagree: the pipeline may already assume success (added lookups, staged
`Select.Projection` entries) while the shaper independently punts. Whether this produces a clean decline, a
crash, or something worse has **not been verified** — the A2 plan's final reviewer flagged this as reachable-
in-principle but did not confirm it's reachable-in-practice (unknown whether EF's nav-expansion, or this
provider's own join-scope construction, actually produces this exact combination for any real query shape).

### Shape 3 — CONFIRMED broken, mechanism NOT YET TRACED: two real EF Core spec tests

```csharp
// Include_reference_when_entity_in_projection (EF Core's own NorthwindIncludeQueryTestBase.cs):
ss.Set<Order>().Where(o => o.CustomerID!.StartsWith("F")).Include(o => o.Customer)
    .Select(o => new { o, o.CustomerID });

// Outer_identifier_correctly_determined_when_doing_include_on_right_side_of_left_join (same file):
// a LEFT JOIN with a MIXED reference + collection Include on the right side.
```

Confirmed via a temporary worktree (during the A2 plan's Task 4): both tests **already failed before any of
this session's work**, with a genuine data-correctness error (not a stale-baseline `AssertMql` mismatch) —
`Include_reference_when_entity_in_projection` expects `"FOLKO"`, gets `null`; the left-join test expects a
populated collection, gets an empty one. Confirmed via the A2 plan's final review: **both fail in the default
query mode too, not just under `MONGODB_EF_NATIVE_ONLY=1`** — since native is the default on this branch, real
users hit this. Confirmed to affect (at least) `NorthwindIncludeQueryMongoTest`, `NorthwindStringIncludeQueryMongoTest`,
`NorthwindEFPropertyIncludeQueryMongoTest`, and `NorthwindIncludeNoTrackingQueryMongoTest` (all 4 sibling
classes — the 4th was found only by the final reviewer, whose 3-class filter had missed it initially).

**What is NOT yet confirmed:** which code path actually handles this query. The `Include_reference_when_entity_in_projection`
shape has no explicit `.Join()`/`.LeftJoin()` in the user's LINQ. The stale, currently-recorded `AssertMql`
baseline (`NorthwindIncludeQueryMongoTest.cs:559` — see the A2 design doc's own "Root cause, precisely"
section for the literal MQL) shows an `"_outer"`/`"_inner"`-keyed pipeline. Two different hypotheses fit this
evidence, and this doc's Task 1 must distinguish between them, not assume:

- **Hypothesis A:** EF Core's nav-expansion lowers a reference-Include-in-a-projection into an internal join
  construct (this is a well-known EF Core pattern for *required* reference navigations specifically), which
  this provider's `IsSingleEligibleNativeJoinScope`/`NativeJoinScopeProjectionBinder` machinery recognizes —
  meaning this shape reaches the SAME `BindResultMember` arm Shape 1 above already covers, and the `"_outer"`/
  `"_inner"` naming reflects some OTHER provider-internal join representation (`MongoQueryExpression.UsesDriverJoinFields`)
  rather than a different recognizer entirely.
- **Hypothesis B:** this shape is recognized by the **completely separate**
  `MongoQueryableMethodTranslatingExpressionVisitor.TryGetReferenceIncludeChain` /
  `TryConfirmReferenceIncludeChain` arm (`TranslateSelect`, ~lines 417-430), which exists specifically for a
  reference-Include chain and has its OWN lookup-registration logic (~lines 1771 onward) — entirely
  independent of anything either the A2 plan or root cause A ever touched. Under this hypothesis, Shapes 1-2
  above and Shape 3 are UNRELATED bugs that happen to look similar, and fixing Shape 1/2 (already done, for
  Shape 1) has zero bearing on Shape 3.

The A2 design doc's original author (a prior session) assumed Hypothesis A without checking — that
assumption is exactly what led to Shape 1's fix being written against the wrong target for months of this
ticket's history (root cause A's original design doc used an near-identical hand-built spike,
`Orders.Include(o => o.Customer).Select(o => new { o, o.CustomerID })`, and assumed it was equivalent to this
real spec test without ever running the real test to confirm). **Do not repeat that mistake here** — Task 1
of this doc's own follow-up plan must empirically determine which hypothesis holds before writing any fix.

### Shape 4 — CONFIRMED broken, no join scope involved at all, separate from Shapes 1-3

> **Correction (post-investigation):** the claims below that Shape 4 has "no join scope involved at all" and
> is "likely the SAME bug as Shape 3's Hypothesis B" were disproven. `docs/superpowers/findings/2026-09-27-shape3-code-path-findings.md`'s
> Shape 4 section confirms Shape 4 DOES reach the join-scope arms (`IsSingleEligibleNativeJoinScope` /
> `NativeJoinScopeProjectionBinder.TryBindProjection` / `BindResultMember`) — Hypothesis A, not B — the same
> code path as Shapes 1-3, not a separate bug.

```csharp
db.Orders.Include(o => o.Owner).Select(o => new { o, o.OwnerId })
```

Found as a control test during the A2 investigation (not committed, described in the A2 design doc's
"Non-goals" section). This query has **no join at all** — no `.Join()`, no `.LeftJoin()`, and (if Shape 3's
Hypothesis B is confirmed, or even under Hypothesis A if EF only lowers reference Includes to a join
specifically inside a *filtered* or otherwise more complex query) may not trigger any internal join either.
The pipeline visibly runs a `$lookup`/`$unwind` for `Owner`, but `Owner` comes back empty on every row. This
is structurally the shape `TryGetReferenceIncludeChain` exists for (`Orders.Include(o => o.Owner)`, no
collection Include, a pure reference chain) — meaning this control test is likely the SAME bug as Shape 3's
Hypothesis B, just isolated without the `Where`/multi-sibling-class complexity of the real spec test.

## Why these four shapes are grouped in one doc

They all present the same OBSERVABLE symptom (a reference-Include navigation comes back null/empty when it
should be populated) but may have up to three DISTINCT root causes (`BindResultMember`'s wrapped-leaf-join
arm; `NativeJoinScopeProjectionBinder`'s staging-side unwrap; `TryConfirmReferenceIncludeChain`'s own,
completely separate machinery). Grouping them lets one investigation determine which shapes share a cause
and which don't, rather than three separate tickets independently re-discovering the same triage work. The
resulting PLAN, once the triage is done, may still split into 2-3 independent fix tasks if the shapes turn out
unrelated — that's a normal plan-shape decision, not something to pre-judge here.

## Investigation plan (what Task 1 of the follow-up implementation plan must establish)

1. **Trace Shape 3's actual code path.** Add temporary diagnostic logging (or use a debugger / the
   `SpyLoggerProvider` + `MongoEventId.ExecutedMqlQuery` technique used throughout the A2 investigation) to
   determine, for `Include_reference_when_entity_in_projection` specifically: does execution reach
   `TryGetReferenceIncludeChain`/`TryConfirmReferenceIncludeChain`, or `IsSingleEligibleNativeJoinScope`/
   `NativeJoinScopeProjectionBinder.TryBindProjection`, or neither (i.e., does it fall back to driver-LINQ
   entirely, and the bug is in the DRIVER's own translation, not this provider's)? A breakpoint or a
   temporary `Console.WriteLine` at the top of each candidate arm is sufficient; remove it before committing
   anything.
2. **If Hypothesis A (join-scope arm):** Shape 3 IS (or is closely related to) Shape 1/2, and the SAME
   `BindResultMember`/`NativeJoinScopeProjectionBinder` machinery both already-fixed shapes touch. The
   remaining work is making the reference-Include case ACTUALLY WORK (not just decline loudly) — see
   "Sketch of a possible fix, if Hypothesis A holds" below.
3. **If Hypothesis B (`TryConfirmReferenceIncludeChain` arm):** Shape 3 (and Shape 4, and possibly the
   left-join mixed-Include half of Shape 3) is a genuinely separate, unrelated bug in code neither root cause
   A nor A2 ever touched. Read `TryConfirmReferenceIncludeChain`'s full body
   (`MongoQueryableMethodTranslatingExpressionVisitor.cs:1771` onward) and whatever shaper-building code runs
   after it (the method does NOT `return` after confirming — execution falls through to whatever generic
   shaper-fold logic sits at the end of `TranslateSelect`; find and read that too) to find why a CONFIRMED
   chain still materializes the reference nav as null. This is unexplored territory — no existing design doc
   analyzes it.
4. **Separately, verify whether Shape 2 (Inner leaf) is reachable at all.** Try to construct a real query
   shape that produces `NativeJoinScopeProjectionBinder.TryBindProjection` staging a reference-Include-wrapped
   Inner leaf successfully, then observe what `BindResultMember`'s now-declining unwrap does with it — does
   the pipeline/shaper mismatch (staged-as-native vs. materialized-as-fallback) produce a clean decline, or a
   crash, or (worst case) silently wrong data? If unreachable, say so explicitly and close this shape as a
   non-issue; if reachable, it needs the SAME `allCollectionIncludes`-style guard applied to
   `NativeJoinScopeProjectionBinder.cs`'s own unwrap (the STAGING side), symmetrically with what
   `BindResultMember` (the MATERIALIZATION side) already got in commit `0b0a7a6d`.

## Sketch of a possible fix, if Hypothesis A holds (Shapes 1/2/3 share a cause)

**Not a design, just an idea to evaluate seriously, not implement blindly.** A reference Include's own
`$lookup` registration, when reached from `MongoProjectionBindingExpressionVisitor.cs`'s `case
IncludeExpression`'s non-collection branch (`_includedNavigations.Push(...); base.VisitExtension(includeExpression); ...`),
currently does nothing but visit children — no `LookupExpression`/`AddLookup` call at all in that branch,
unlike the collection branch just above it. Whether this is because reference Includes are ALWAYS expected to
be handled by some OTHER, earlier mechanism (`TryConfirmReferenceIncludeChain`, or the ordinary reference-nav
join machinery that already works for a *bare* `Select(o => o)` reference Include — confirmed working by the
pre-existing, passing `Optional_reference_Include_goes_native_with_a_left_outer_unwind` /
`Required_reference_Include_goes_native_with_an_inner_unwind` tests in `NativeReferenceIncludeTests.cs`) or a
genuine gap needs tracing before any fix is written. If it's a gap, the fix likely mirrors the collection
branch's shape (build a `LookupExpression`, register it, rewrite the `IncludeExpression` into something
`MongoProjectionBindingRemovingExpressionVisitor`/`MongoStreamingEntityMaterializerRewriter` can already
read for an ordinary reference Include) — but do not assume this without confirming those two existing,
working reference-Include tests' own code path is even the SAME arm being discussed here (Review Focus below).

## Scope boundaries

- **Do not re-litigate root cause A or A2** (collection-Include recognition and materialization) — both are
  committed, tested, and out of scope for further changes here except the symmetry check in Investigation
  step 4 above.
- **The EF8/EF9 LeftJoin-shim gap** (`docs/superpowers/plans/2026-09-24-ef8-ef9-leftjoin-shim-native-include.md`)
  is a separate, already-scoped ticket. If this doc's investigation finds it's ENTANGLED with Shape 3 (e.g.
  the internal join EF Core inserts for a required reference Include IS the same EF8/EF9 shim that plan
  targets), say so explicitly and coordinate rather than duplicating work — do not assume they're unrelated
  just because they were filed separately.

## Review Focus (for whoever writes the implementation plan / reviews it)

- **The two ALREADY-PASSING reference-Include tests must stay passing.**
  `Optional_reference_Include_goes_native_with_a_left_outer_unwind` and
  `Required_reference_Include_goes_native_with_an_inner_unwind` (`NativeReferenceIncludeTests.cs`) prove a
  *bare* `Select(o => o)` reference Include already works correctly. Any fix here must not be able to touch
  that path, or must be proven not to regress it — trace whether that working path shares ANY code with
  whichever arm Task 1's investigation identifies for Shape 3, before writing a fix that could affect both.
- **A reference Include mixed with a collection Include in the SAME chain** (Shape 3's left-join test is
  exactly this — "MIXED reference + collection Include") is its own combinatorial case, handled by
  `TryGetMixedReferenceAndCollectionIncludeChain` — distinct from the pure-reference case
  (`TryGetReferenceIncludeChain`) and the pure-collection case (root cause A2). Don't assume a fix for one
  automatically covers the mixed case.
- **`ThenInclude` chains** (a reference Include followed by another reference or collection Include on the
  SAME target) are untested by anything found in this investigation — if Task 1's fix touches shared
  Include-handling code, check whether a `ThenInclude` chain is affected, even if no existing test exercises
  it (add one if the fix's own risk surface reaches that shape).

## Testing strategy

- **Task 1 must be a pure investigation/spike** — no production code changes — that ends with a definitive
  answer to "which hypothesis (A or B) holds for Shape 3," written up before any fix task starts.
- Whatever shapes turn out to share a cause get ONE fix + ONE set of functional tests (mirroring the A2
  plan's own promote-a-spike-to-a-permanent-test pattern); shapes that turn out unrelated get split into
  separate follow-up plans rather than forced into one.
- The two real target spec tests (`Include_reference_when_entity_in_projection`,
  `Outer_identifier_correctly_determined_when_doing_include_on_right_side_of_left_join`, across all 4
  sibling classes, confirmed including `NorthwindIncludeNoTrackingQueryMongoTest`) are the ultimate
  acceptance criteria, exactly as they were for the ORIGINAL root-cause-A ticket — this doc's whole point is
  finally making them pass with correct data, not just a clean baseline.
