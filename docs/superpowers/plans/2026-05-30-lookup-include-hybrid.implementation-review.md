# Review of the hybrid `$lookup` Include implementation

Reviewer: claude (with full EF-117 staged-implementation context).
Date: 2026-05-31.
Reviewed branch: `EF-117c` (24 commits ahead of `EF-117b`, ending at
`53662cd`).
Reviewed against: `docs/superpowers/plans/2026-05-30-lookup-include-hybrid.md`
(revised post my plan-review) and my own
`…-hybrid.review.md` blocker / substantive / refinement list.

## Verdict

**Approve.** The implementation is clean, the multi-EF spec baseline
holds at zero failures, the open follow-ups are honestly tagged, and
the executor exceeded the plan in two important spots (over-rejection
correctness, dead-code removal). I have **2 small nits** worth a
follow-up tidy, **3 substantive callouts to highlight in any review-pack
write-up** (positive — they're behavioral wins), and **0 blocking
concerns**.

This review caught nothing the executor or the reviewer iterations
already addressed.

## Verification — the test bar is the strongest signal

| Configuration | Spec tests | Functional / Unit |
|---|---|---|
| Debug EF8  | **0** failed / 4714 passed, 11 skipped | — |
| Debug EF9  | **0** failed / 4858 passed, 11 skipped | — |
| Debug EF10 | **0** failed / 4418 passed, 14 skipped | `IncludeTests` + `OwnedEntityTests` 98 / 98; `UnitTests` 269 / 269 |

`IncludeTests` grew from 10 → 28 test methods, with one method per
distinct shape (single-`$lookup` collection, `$lookup`+`$unwind`
reference, two `$lookup`s for reference+collection on same root,
nested `$lookup` for reference→collection chain, three-level mixed
chain, self-referencing reference, composite-PK-member dotted lookup,
filtered-include pipeline-`$lookup`, filtered-include user-`Where` fan-out,
fan-out tracking propagation, and the nav-referencing-Where throw cases).
Run locally on EF10. Independently verified by re-running the suites.

## Blocker disposition (from my plan review)

| Plan-review item | Status |
|---|---|
| **B1** Branch name conflict | ✅ Branched `EF-117c` off `EF-117b`. Iter-1 and iter-2 review fixes (`8aaf8e5`, `ade279b`) inherited. |
| **B2** Floor must be full spec on EF8/9/10 | ✅ Floor recorded as such in `d3ccd95`; the implementation's end state preserves it on all three. |
| **B3** Stage 1.3 unit-MQL test unworkable | ✅ Dropped; replaced with a feasible `LookupExpression` field-derivation unit test (`tests/.../Query/Expressions/LookupExpressionTests.cs`, 146 lines) and a `MongoQueryExpression.PendingLookups` test (`MongoQueryExpressionTests.cs`, 88 lines). Both are pure data tests that don't need a live `MongoClient`. |

## Substantive disposition (from my plan review)

| Plan-review item | Status |
|---|---|
| **S1** Surface the N+1 and identity-resolution wins | ✅ The overview gained an explicit `## Strategy update: the server-side $lookup hybrid (current)` section that calls out the routing change and includes a shape-by-shape table. Original fan-out-only sections are explicitly marked as superseded. *Possible polish*: the `AsNoTrackingWithIdentityResolution` correctness win (single-materialization-fixes-identity) could be called out more loudly in the "wins" framing — it's currently in the loader-comment in `MongoIncludeCompiler`, but not in the overview. Optional. |
| **S2** `ApplyTrackingBehavior` is fan-out-only | ✅ Documented in inline comments in both `LoadCollection` and `LoadReference` ("Fan-out-only: this sub-query is a SEPARATE materialization…"). Test `Include_collection_then_include_collection_as_no_tracking_tracks_nothing` exercises tracking under the fan-out fallback path. |
| **S3** Convention change gating criteria | ✅ Convention change **deliberately NOT ported**. Documented in the overview under "The convention decision (recorded explicitly)" with the persisted-document-shape rationale and a follow-up note. `MongoRelationshipDiscoveryConvention.cs` retains its pre-EF-117 logic; verified. |
| **S4** Stage 4 spike — chained `$lookup`s vs. `_outer/_inner` | ✅ Path A picked. The `docs/EF-117-include-implementation-progress.md` "Stage 4 — multi-level Include design spike" section is excellent: documents the actual prototype BSON output (reference→collection works, reference+collection works, collection→collection clobbers the array — confirming the server-side limitation), and explains why chained `$lookup`s avoid B's `~400+ lines of join-result rewriting`. Implementation: a new `ReferenceChainJoinUnwrapper` for the multi-level case keeps the existing single-level `IncludeJoinUnwrapper` narrow. |
| **S5** `IncludeJoinUnwrapper` interaction with `$lookup` | ✅ Extended (not replaced) to recognize the multi-Include-on-same-root shape (reference + collection on the same root). The reference part is unwrapped into a plain `Select(p => IncludeExpression(p, default(TInner), refNav))` while sibling collection Includes are preserved with rewritten `o.Outer` → `p` references — verified by the `Include_reference_and_collection_emits_two_lookups_in_single_query_and_materializes` test. |
| **S6** ~5× spec-sweep budget + per-class discipline | ✅ Visible in commit history: ~24 commits with substantive per-suite changes, not one mass rewrite. The four `NorthwindInclude*QueryMongoTest` files net-shrank by ~15k lines (fan-out baselines replaced by `$lookup` baselines). |
| **S7** AsSplitQuery as opt-in | ✅ Scoped out as optional Stage 9 follow-up per the response doc; not implemented (acceptable per the agreed decision). |
| **S8** 16 MB BSON document limit caveat | ✅ Documented in the overview under "Honest design caveat — server-side `$lookup` for collections". Names cluster-memory/CPU as the cost shift. |

## Refinements disposition

R1–R10 from my plan review: all applied. Specifically:

- **R3 code-style consistency** is honored — `IncludeJoinUnwrapper` and `ReferenceChainJoinUnwrapper` both use canonical `QueryableMethods.Select` / shared `IsJoinOrLeftJoin` and the structural TransparentIdentifier check from iteration 2. Grep confirms no `Method.Name == "Select"` etc. in the new code.
- **R6 BREAKING-CHANGES.md** has clean entries for the behavior change *and* all three new exception-type changes (M2M `InvalidOperationException` with new message; shadow / composite key now `NotSupportedException`; nav-referencing filtered Include `NotSupportedException`).
- **R9 dead-code cleanup** — final cleanup commit `53662cd` explicitly removes `UsesDriverJoinFields` (the dead property that path-B would have needed). Verified by grep: no remaining references.

## Things the executor did *beyond* the plan that are worth calling out

These are not items I flagged — they emerged during implementation and
are genuine correctness improvements:

### W1. Replacing silent wrong-results with a loud, accurately-attributed throw

When a filtered collection Include's user `Where` predicate references
ANOTHER navigation (e.g. `Include(c => c.Orders.Where(o => o.Customer.Name == "Alfreds"))`),
EF expands the predicate over a transparent-identifier element type
that the provider can neither render server-side nor recompose onto the
fan-out loader. *Before this work, the include would silently return
unfiltered results* (the predicate was dropped). The implementation
now throws `NotSupportedException` with a precise message
(`HasUntranslatableUserWhereInclude` in `MongoIncludeCompiler`).

The careful bit: the same OUTSIDE-the-FK-correlation-Where can also be
produced by a model query filter on the dependent (entity-level
`HasQueryFilter`), and the implementation honestly admits this is an
**over-rejection** of an otherwise-supportable shape — but it's
preferable to the silent-wrong-results status quo, so the throw stays.
The message names the dependent type and attributes the cause to
either trigger so it cannot mislead the user. There are explicit tests
asserting both the throw and the message accuracy
(`Filtered_collection_Include_with_nav_referencing_Where_throws_rather_than_under_filtering`
and an "honest message" test for the query-filter case).

This is a real correctness win and deserves prominent mention in
release notes.

### W2. The `RewriteCollectionIncludeForLookup` register-after-success ordering

`AddLookup` is called *inside* the rewrite helpers, AFTER
`TryResolveOuterEntityProjection` succeeds — not before. The comment
(at `MongoProjectionBindingExpressionVisitor.cs:220`) calls out exactly
why: "a fallback to fan-out leaves no orphan lookup registered (which
would otherwise emit a `$lookup` whose `_lookup_<Nav>` output nothing
reads)." This is exactly the kind of subtle ordering bug that's easy
to miss; the existing commit message `60b5090` shows this came out of
code review and was deliberately tightened. Good defensive engineering.

### W3. The shared `LookupExpression.GetAlias` invariant

The shaper-side `EntityProjectionExpression.BindNavigation` and the
producer-side `LookupExpression` constructor both build `_lookup_<Nav>`
via the same `LookupExpression.GetAlias(navigation)` static method.
This is the single source of truth for the field name the `$lookup`
stage writes and the shaper reads. Without this, the two could drift
silently (write to `_lookup_Orders`, read from `_lookupOrders` —
empty arrays, silent wrong results). The decision to centralize is
explicit in the type's XML doc.

## Nits — non-blocking

### N1. Stale comment in `IncludeStrategy.cs`

```csharp
/// <summary>Client-side fan-out: one sub-query per principal (EF-117 Stage 1-4).</summary>
ClientFanOut
```

"EF-117 Stage 1-4" was accurate when fan-out was the only path. It is
now the **fallback**, not the original primary path. Suggest:
*"Client-side fan-out: one sub-query per principal (used as the
fallback when the server-side `$lookup` path can't express the include
shape)."* Trivially editable.

### N2. The pre-existing stale class-level doc on `MongoIncludeCompiler`

```csharp
/// <para>
/// Stage 1 implements collection navigations on the principal side
/// (e.g. <c>Customer.Orders</c>). Reference navigations on the dependent
/// side (e.g. <c>Order.Customer</c>) require JOIN-unwrap handling and land
/// in Stage 2; ThenInclude chains land in Stage 3.
/// </para>
```

The class now does much more than that (server-side classifier,
filtered-include translator, fan-out composition builder, etc.). Same
trivial polish — could be rewritten to describe the current end-state
rather than its construction history. Either way harmless.

### N3. Unrelated TransparentIdentifier name-check in `MongoQueryableMethodTranslatingExpressionVisitor.cs:157`

There's a pre-existing `parameterType.Name.StartsWith("TransparentIdentifier")`
in the user-join rejection path. **Not added by this work** and
**not on the Include path** — it's the existing "join operations are
not supported" error message. Flagging only because the iteration-2
fix taught us this pattern is brittle. Worth a separate-PR cleanup at
some point; explicitly not for this branch.

## Spot-checks against the post-iter-2 conventions

| Convention | Status |
|---|---|
| Canonical `QueryableMethods.*` for `Select` / `Join` matches | ✅ Used everywhere in the new code; verified by grep. |
| Structural `TransparentIdentifier` check (`ReferenceEquals(me.Expression, expectedParam)`) | ✅ `IncludeJoinUnwrapper.IsFieldAccessOf` retains the iter-2 pattern. `ReferenceChainJoinUnwrapper` doesn't need TransparentIdentifier matching because it works on the IncludeExpression nesting structure directly. |
| Removal of unused `using MongoDB.Bson.Serialization` collision | ✅ Still removed. `MongoProjectionBindingRemovingExpressionVisitor` base type is `ExpressionVisitor` (not fully-qualified). |

## Documents — load-bearing for future maintenance

- **`docs/EF-117-include-overview.md`** — clear "current behavior wins"
  marker on the original fan-out sections; new "Strategy update"
  section is the canonical description.
- **`docs/EF-117-include-implementation-progress.md`** — the Stage 4
  spike write-up is excellent maintenance documentation. The actual
  pipeline output + the *server-side constraint that ruled out
  collection→collection nesting* is recorded in BSON form, which is
  exactly what a future maintainer needs to understand the design
  choice.
- **`docs/failing-spec-tests.md`** — the EF-117 row is honest about the
  current state, the remaining limitations, the rationale for the
  throw vs silent-wrong-results trade, and the open Option-3 follow-up.
  Densely packed but accurate; could be split into multiple shorter
  follow-up rows if it grows further.
- **`BREAKING-CHANGES.md`** — clean entries for both the behavior
  change and all three new exception-type changes.

## Bottom line

Ship it. The 24 commits read as a careful, well-tested staged
implementation that respected every blocker / substantive concern from
the plan review, executed the design spike honestly (and chose the
simpler path A as recommended), and went *beyond* the plan to fix two
silent-wrong-results bugs the original fan-out path harbored. Test
coverage on the actual functional shapes is strong (28 IncludeTests
methods, each named for the shape it covers). All three EF version
targets pass zero-failure spec runs.

The two stale-comment nits (N1, N2) can be addressed in a follow-up
commit or left as harmless. The unrelated pre-existing
TransparentIdentifier name-check (N3) is genuinely separate from this
work.
