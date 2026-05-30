# Response to review of `2026-05-30-lookup-include-hybrid.md`

Author of response: plan author (Claude)
Date: 2026-05-30
Responding to: `2026-05-30-lookup-include-hybrid.review.md`

Thanks — this was a high-quality review and the verifiable claims checked out
against the repo (branch state, commit `ade279b`'s zero-failure baseline, absence
of a translator unit-test harness). The plan has been revised. Disposition below;
**accepted** unless stated otherwise. One genuine scoping **pushback** (S7) and two
**refinements** where I diverged slightly from the suggested fix.

## Blocking

| # | Disposition | What changed in the plan |
|---|---|---|
| **B1** Branch already exists | ✅ Accepted (verified: `EF-117b` is current HEAD with `4ce46d0`/`8aaf8e5`/`ade279b`) | Task 0.1 now branches **`EF-117c` off `EF-117b`** to inherit the review fixes. Branch name updated in Critical-context §3, Conventions, and Execution-handoff. |
| **B2** Floor too narrow | ✅ Accepted (verified zero-failure baseline) | Task 0.1 Step 2 now runs the **full spec project on EF8/EF9/EF10** via `/test-all`, records all three counts, and every later stage's regression gate says "full spec floor (0 failures)" not "NorthwindInclude". |
| **B3** Translator unit-MQL test impractical | ✅ Accepted, with **refinement** | Confirmed no standalone translator harness exists. Dropped the translator-MQL test from Stage 1.3 (now compile/wire-only). **Refinement:** I kept unit coverage at the *feasible* level — a `LookupExpressionTests` that asserts field derivation (`From`/`LocalField`/`ForeignField`/`As`, composite `_id.<field>` pathing), which only needs a model. Added to Task 1.1. |

## Substantive

| # | Disposition | What changed |
|---|---|---|
| **S1** Under-sells two wins | ✅ Accepted | New **"Why this matters"** section up top: N+1 elimination *and* the `AsNoTrackingWithIdentityResolution` correctness fix (framed as correctness, not just perf). |
| **S2** Tracking propagation post-hybrid | ✅ Accepted | Stage 7 Step 3 now states `ApplyTrackingBehavior` is **fan-out-only** (must not be deleted), documents that `$lookup` inherits tracking implicitly, and adds a fan-out-fallback `AsNoTracking` + collection→collection test. |
| **S3** Convention criteria too weak | ✅ Accepted | Task 8.1 rewritten: the deciding factor is a **persisted-document-shape diff** for a *convention-discovered* (no fluent config) relationship, not suite pass/fail; if ported, classified as a **Behavior break** per AGENTS.md, not just API. |
| **S4** De-risk Stage 4 `_outer`/`_inner` | ✅ Accepted | New **Task 4.0 spike**: prototype direct chained `$lookup`s (no driver `LeftJoin`, no prefixing) and decide before porting. If the spike fails, the fallback port includes the unwrapper-gating sub-task you suggested. |
| **S5** `IncludeJoinUnwrapper` × `$lookup` reference path | ✅ Accepted | New Stage 3 Step 2 (runs *before* writing the route): verify the unwrapper precedes the classifier, that `LookupExpression` is built from `INavigation` metadata (not the `default(TInner)`-rewritten `NavigationExpression`), and that the shaper reads the alias. Gate the unwrapper if any check fails. |
| **S6** Rebaseline scope ≈ 5× | ✅ Accepted | Stage 8.2 rewritten: explicit ~5× budget warning citing the rewriter non-idempotence (discovery #6), a **per-class rebaseline wrapper script**, and **one sub-commit per suite** (incl. non-Include suites whose Include MQL changed). |
| **S7** `AsSplitQuery` not planned | ⚠️ **Pushback (scoped out, not dropped)** | The user explicitly chose **automatic** hybrid routing, so an explicit operator is out of the requested scope. I added it as **Stage 9 (OPTIONAL / follow-up)** with the design sketch (map `QuerySplittingBehavior` in `ChooseStrategy`) and noted it's the escape hatch for the S8 payload-size risk — but it is deliberately *not* built here. If you want it in-scope, that's a one-line change to promote Stage 9. |
| **S8** High-cardinality / 16 MB payload | ✅ Accepted | Added an **"Honest design caveat"** to the rationale (nested-array blow-up, 16 MB BSON limit, cost shifts to cluster), and Stage 8.3 records it in `EF-117-include-overview.md`. Connected it to the Stage 9 escape-hatch rationale. |

## Refinements

| # | Disposition | What changed |
|---|---|---|
| **R1** Don't duplicate the test model | ✅ Accepted, with **refinement** | Dropped the separate `LookupIncludeTests.cs`. **Refinement on placement:** materialization tests **extend the existing `IncludeTests.cs`**; MQL-content assertions live **in the spec suites** (where `AssertMql`/`TestMqlLoggerFactory` are already wired) rather than rebuilding the MQL logger hook in the functional project, as your note implied might be needed. |
| **R2** `Default` constant misleading | ✅ Accepted | Removed `IncludeStrategyDefaults.Default`; Stage 0 test now calls `ChooseStrategy(...)` directly on a real include fixture. |
| **R3** Port to A's canonical idiom | ✅ Accepted | New porting-convention rule in Critical-context §4: use canonical `QueryableMethods` reference-equality + structural `TransparentIdentifier` checks; never B's string-name patterns. Referenced from the Stage 1 port step. |
| **R4** Factor `HasNestedInclude` | ✅ Accepted | Task 2.2 now factors one lazy `EnumerateNestedIncludes` helper shared by `ExtractIncludeChainPath` and `HasNestedInclude`. |
| **R5** Respect prior review resolutions | ✅ Accepted | Critical-context §3/§4 reference inheriting and respecting `8aaf8e5`/`ade279b` (I referenced the commits rather than the `review-EF-117b*.md` filenames, to avoid a stale path). |
| **R6** BREAKING-CHANGES debt | ✅ Accepted | New **Task 0.1b**, explicitly framed as **pre-existing debt** (M2M/shadow-key exception-type changes already shipped), cleaned up before the hybrid adds more. |
| **R7** MongoDB min version for pipeline `$lookup` | ✅ Accepted | Critical-context §7: pipeline `$lookup` needs 3.6+, well under the driver minimum; Stage 6 confirms against the compat matrix. |
| **R8** Self-referencing nav | ✅ Accepted | Stage 3 Step 5 adds a `Staff.Manager` self-join test (same-collection `$lookup`, `localField ≠ foreignField`); flags special-casing if it fails. |
| **R9** Dead-code threading | ✅ Accepted | Stage 7 Step 4: explicit decision to either wire `BsonSerializerFactory`/`QueryContextParameter` into the `$lookup` shaper reads or delete the threading. |
| **R10** MQL tests need live MongoDB | ✅ Accepted | Critical-context §6 reinforced: Docker required for **every MQL-asserting stage (2–8)**, not just once. |

## Net effect

All 3 blockers resolved; 7 of 8 substantive items folded into stage steps (S7 scoped
to optional Stage 9); all 10 refinements applied. The two places I diverged from the
literal suggestion (B3 → keep a *feasible* unit test; R1 → MQL asserts in spec suites,
not a rebuilt functional hook) are refinements in the same spirit, not disagreements.

The single open decision for you: **S7 / Stage 9** — leave `AsSplitQuery` as an
out-of-scope follow-up (current state), or promote it into this deliverable?
