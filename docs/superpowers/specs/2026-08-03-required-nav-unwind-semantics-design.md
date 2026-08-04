# Design — required-navigation `$unwind` semantics (EF-370)

**Status:** implemented. **Tickets:** EF-370 **and EF-369**, closed together by one change.
**Branch:** `EF-370`, off `upstream/main` (`58e05a0`).

**Originally planned as slice 1 of 2**, with EF-369 (composed operators discarded for a multi-join Include) to
follow as an independent, churn-free slice. **That plan was abandoned during implementation, on measurement:**
slice 1 alone does not merely fail to fix EF-369, it converts a loud failure into silent wrong data. The two are
one unit and ship together. §4A records the measurements, and the wrong reasoning behind the original split is
kept rather than erased — it is the useful part of the record.

## 1. The defect

The cross-collection `$lookup` path hard-codes `preserveNullAndEmptyArrays: true` on the `$unwind` it emits for
a flattened join. That is left-outer-join semantics.

EF Core's navigation expansion lowers a **required** reference navigation to an inner `Queryable.Join` and an
**optional** one to a `LeftJoin`. The provider ignores that distinction, so a required navigation is executed as
a left-outer join. MongoDB enforces no referential integrity, so a document whose foreign key matches nothing is
an ordinary data state — and such a row is currently **returned with a null navigation instead of being
excluded**.

The same applies to a user-authored `Join`, which is unambiguously an inner join and is likewise executed as
left-outer.

### Measured

`NorthwindJoinQueryMongoTest.Where_join_orderby_join_select` returns **2145 rows where 2143 is correct**. The two
extra rows are principals whose join key matched nothing, preserved by the unwind and then surviving into the
result.

## 2. Scope: unreleased, which is what makes this affordable

The affected code arrived with `efb5f25` ("EF-117: Cross-collection Include / navigations / joins", PR #309,
2026-06-11). `git merge-base --is-ancestor efb5f25 v10.0.2` is false, and the hard-coded sites are absent from
`v8.4.2`, `v9.1.2` and `v10.0.2`.

So no upgrading consumer can observe a change, and per the versioning rubric in `AGENTS.md` this is **not a
breaking change** — even though it changes result sets, which would normally be the most serious category. We
are free to make the semantics correct rather than bug-compatible. **That window closes at the next release**,
which is the argument for doing it now rather than deferring.

## 3. The three `$unwind` sites, and which of them changes

| # | Site | Serves | Current | Proposed |
|---|---|---|---|---|
| 1 | `MongoEFToLinqTranslatingExpressionVisitor.LeftJoin.cs`, `EmitLookupStages` | the flat-lookup path: multi-join Includes **and** user joins | hard-coded `true` | read from the lookup |
| 2 | same file, `TryBuildDriverNativeLeftJoinPipeline` | single-reference **`LeftJoin`** only (reached from one call site inside the `LeftJoin` handling) | hard-coded `true` | **unchanged — already correct** |
| 3 | `MongoProjectionBindingExpressionVisitor.Lookup.cs`, `AddReferenceLookupStages` | a reference `ThenInclude` nested inside a **collection** Include's sub-pipeline | hard-coded `true` | **unchanged in this slice — see §7.1** |

Site 2 needs no change: that builder exists specifically to emit a left-outer pipeline for a `LeftJoin`, so
`true` is correct by construction. An earlier draft of the EF-370 ticket listed it as a site to change; that was
imprecise and is corrected here.

## 4. Design

Add a settable `PreserveNullAndEmptyArrays` property to `LookupExpression`, **defaulting to `true`**, and read it
at site 1.

The default matters: every non-join registration site wants left-outer semantics (an `Include` must not drop
principals), so defaulting to `true` means only the join path needs to think about this, and a future
registration site that forgets about the flag gets the conservative behaviour.

Thread an `isLeftOuter` argument from the three existing overrides into `TranslateJoinCore`:

| Override | `isLeftOuter` |
|---|---|
| `TranslateJoin` | `false` |
| `TranslateLeftJoin` | `true` |
| `TranslateGroupJoin` | `true` |

### 4.1 The discriminator: operator first, `ForeignKey.IsRequired` as fallback

**The LINQ operator is authoritative wherever it is in hand.** `ForeignKey.IsRequired` alone is *insufficient*,
and this was measured rather than reasoned: `Where_join_orderby_join_select` joins `Customers` to `Orders` on
`CustomerID`, which resolves to the `Customer.Orders` navigation, whose foreign key `Order.CustomerID` is
**nullable**. An `IsRequired`-keyed rule therefore still emits a preserving unwind for it and still returns 2145.

There is exactly one site where the operator is not in hand: the retroactive re-registration of a **prior** join
that was rendered driver-natively as a single reference and is now being flattened because a second join
appeared. By then the earlier operator is gone. There, `ForeignKey.IsRequired` is the correct model-derived
fallback — a required foreign key can never be unmatched *in the model's intent*, which is the inner-join case.

`AddLookup` de-duplicates on the `As` alias, so a lookup already registered by its own join keeps the flag that
join gave it; the fallback only supplies one that was never registered.

### 4.2 Why this dissolves EF-369's hardest problem rather than working around it

EF-369's own prototype had to distinguish an EF-synthesized join from a user-authored one, in order to know
which composed operators to reattach. That distinction is **genuinely undecidable**: both are `Queryable.Join`
with a transparent-identifier selector over a bare root, and in both cases the join keys are a real FK/PK pair.
A fix gated to all-`LeftJoin` chains avoids the question but leaves the required-navigation variant broken on
all three majors — measured, still wrong after that patch.

Once the unwind semantics follow the join kind, **the distinction stops mattering**, because a synthesized inner
`Join` and a user inner `Join` should both emit an inner unwind. That is why this slice goes first.

### 4.3 Per-execution state (a wart fixed in passing)

The prototype initially set an `InjectAfterRoot` flag on `LookupExpression` — compile-time state owned by
`MongoQueryExpression` — from a per-execution visitor. That is replaced by a per-execution
`HashSet<LookupExpression>` on the visitor (`_injectAfterBaseSourceLookups`), with the tail-append site
excluding anything already emitted early via one `IsInjectedEarly(lookup)` helper.
`LookupExpression.InjectAfterRoot` keeps its compile-time meaning and is no longer written during
execution.

**Correction (review):** those reattach lookups are emitted immediately above the join chain's **base
source**, not at the root. Emitting them at the root put a row-dropping inner `$unwind` *above* operators
the user wrote below the joins, so a base-source `Skip`/`Take`/`Distinct` saw a different row set than it
should — silently wrong data, and inconsistent with the same query without a composed operator. Both
shapes are now pinned by row-identity tests in
`RequiredNavigationUnwindTests.Base_source_paging_is_applied_before_a_required_navigations_inner_unwind`
(and its `_without_composition` control).

This is needed by the reattach (§4B), and lands with it. Compile-time state written per execution is the kind of
thing that should not survive review unremarked in any case.

## 4A. Why the two slices were merged — measured during implementation

**This supersedes the "independently correct, so it can ship alone" claim in the original header and the exit
criteria in §5.3 (restated in §5.4).** The `$unwind` design in §4 held up unchanged and is implemented as
written; only the *sequencing* claim was wrong.

§5.3's figures ("2143 rows", "four EF-216 tests pass", "`Select_Where_Navigation_Deep` passes") were taken from
the prototype spike's FU-B, which measured the **unified** change — slice 1 *plus* the EF-369 reattach with its
all-`LeftJoin` gate removed. They were transplanted here as if slice 1 alone produced them. It does not. Measured
on the implemented slice-1 branch, all three majors:

| Design claimed | Actually measured for slice 1 alone |
|---|---|
| `Where_join_orderby_join_select` → 2143 rows | **0 rows.** On `upstream/main` it *throws*; with slice 1 it silently returns empty. |
| Four EF-216 tests pass | All four fail: 2155 rows against 112 / 112 / 352 / 40 expected. |
| `Select_Where_Navigation_Deep` passes | Still throws; its `ThrowsAnyAsync` override still passes. |
| `Multiple_joins_Where_Order_Any` path change (2+2) | Does not occur. |
| `Comparing_collection_navigation_to_null_complex` exception change (2) | Does not occur. |

**Mechanism.** In these shapes the flat `$lookup`s are tail-appended *after* a `$project` that renames the root
to `Outer`/`Inner`, so `localField: "_id"` resolves against a field that no longer exists and the lookup matches
nothing. That dangling `localField` is the pre-existing defect the EF-369 reattach fixes, by injecting the
lookups immediately after the root. Until then the array is always empty, and flipping its `$unwind` from
preserving to non-preserving turns "every row, with a null navigation" into **no rows at all**.

**So slice 1 in isolation converts a loud failure into silent wrong data** — the precise outcome §6 lists as the
risk to avoid. The two changes are interdependent for correctness: the unwind fix needs the reattach to have put
the lookups where their `localField` resolves. At the slice-1-only commit the measured position was: skips delta 0
on every project and major, failures +2, and the failing set difference exactly `Where_join_orderby_join_select`.

**Decision: merge the slices.** This document therefore also owns the EF-369 reattach (§4B), and §8's non-goals
are corrected accordingly.

### 4A.1 A suspected second defect in §4.1's fallback — RAISED BY INSPECTION, THEN MEASURED FALSE

**Kept as a record of a wrong call, because it was nearly acted on.**

The concern, reached by reading rather than running: the `!ForeignKey.IsRequired` fallback is evaluated for
**collection** navigations as well as reference ones (`GetNavigations()` returns whichever navigation targets the
prior inner entity type, and for a join over a one-to-many that is the collection side). For a collection
navigation `IsRequired` describes whether the *dependent's* foreign key is required, not whether a principal must
have children — so a collection Include reaching that fallback with a required dependent FK looked like it would
get an inner `$unwind` and **drop childless principals**, contradicting §4's own rationale for defaulting the flag
to `true`. The prescribed remedy was to narrow the rule to reference navigations.

The narrowing was implemented and then **measured, and it is wrong on both halves.** Model: `Order` with a
required-FK `Lines` collection and a required-FK `Buyer` reference; one order (`O4`) has no lines, another (`O3`)
has a dangling `buyer_id`.

| Shape | Narrowed rule | Unnarrowed rule | Correct |
|---|---|---|---|
| `Orders.Include(o => o.Lines).Include(o => o.Buyer)` | `O1, O2, O4` | `O1, O2, O4` — **identical** | `O1, O2, O4` |
| `Orders.Join(Lines, …).Join(Buyers, …)` | `O1/L1, O1/L5, O1/L6, O2/L2, `**`O4/`** | `O1/L1, O1/L5, O1/L6, O2/L2` | the unnarrowed set |

1. **A collection `Include` never reaches this site**, so there were no principals to protect. Its `$lookup` is
   registered by `MongoProjectionBindingExpressionVisitor`, which does not consult the fallback — which is why
   the first row is unchanged by the narrowing. The childless principal `O4` survives either way.
2. **The only shape that does arrive with a collection navigation is a user-authored inner `Join` over a
   one-to-many**, where dropping a principal with no children is the *correct* answer. There the narrowing emits
   a spurious `O4/` row.

So the rule stays as §4.1 specifies, keyed on the foreign key for collection and reference navigations alike, and
both shapes above are now pinned by tests
(`RequiredNavigationUnwindTests.Collection_Include_beside_a_required_reference_Include_preserves_childless_principals`
and `…User_authored_Join_over_a_collection_navigation_is_inner`). The generalisable lesson: "which navigation does
`GetNavigations()` hand back, and does this site even see the shape I am worried about" is a question to answer by
running, not by reading.

## 4B. The EF-369 reattach (folded in by the §4A merge decision)

`StripJoinForLookup` discarded any operator sitting at or above the innermost join of a multi-join Include chain,
silently returning unfiltered results. It now flattens the chain, keeps everything below the innermost join as the
base source, drops the join nodes and the EF-synthesized `Select` that unpacks the `TransparentIdentifier`, and
**reattaches** every other operator — rewriting each lambda from `.Outer`/`.Inner` reads into
`Mql.Field(root, "_lookup_<Nav>", serializer)` reads, with the identifier depth keyed off the lambda parameter's
own transparent-identifier nesting rather than the join count. An operator that cannot be rewritten makes the
method return `null`, so the join survives and translation fails loudly rather than dropping a filter.

**No all-`LeftJoin` gate.** The prototype needed one only because the emitted `$lookup` stages were
unconditionally left-outer and so could not reproduce an explicit `Join`'s inner semantics. With §4 in place the
synthesized-versus-user-`Join` distinction — which the spike showed to be genuinely undecidable — stops mattering,
because both are inner. This is the payoff §4.2 predicted, and it held.

**Ordering.** The reattached stages read the lookups' output fields, so all forced-unwind lookups are injected
immediately after the root source — *all* of them, not only those the rewritten lambdas read: a transitive
`localField` points into an earlier lookup's unwound output, and any tail-appended remainder would land after a
scalar terminal such as `Count`. Per §4.3 that decision is per-execution visitor state, not a write to
`LookupExpression.InjectAfterRoot`.

## 5. Test plan

### 5.1 New coverage

A functional test with a **required**-FK model (`Line` → `Order` → `Buyer`, `Line` → `Product`, every FK
non-nullable and explicitly `IsRequired()`), seeded with a **dangling foreign key** — a row whose FK matches no
document. No existing fixture has such a seed, which is a large part of why this survived.

Assertions must be on **row counts and identities**, not MQL. The whole reason this class of bug persists is
that the wrong MQL is indistinguishable from a legitimately different query.

Cases: required single reference; required two-hop (`ThenInclude`); optional reference (must still preserve);
mixed required-and-optional chain; user-authored `Join` (inner); user-authored `LeftJoin` and `GroupJoin` (both
left-outer); and a collection `Include` (must still preserve — the site-3 boundary).

Run on all three EF majors. Note that the optional-FK cases throw on EF8/EF9 (EF-X020: EF's internal `LeftJoin`
never reaches the provider's translator there), so those cases need `#if !EF8 && !EF9`, while the **required**-FK
cases run everywhere — that asymmetry is itself worth asserting, since misreading it is what made the EF-369
ticket initially understate the bug's reach.

### 5.2 Specification-suite churn: expected, classified, and data-gated

Prototype measurement: **114 case failures on EF10, 22 on EF8/EF9** (10 of the 22 an artefact of temporarily
un-skipping the EF-216 tests).

| Cases | Kind | Disposition |
|---|---|---|
| 104 EF10 + 8 EF8/EF9 | `preserveNullAndEmptyArrays` flips `true` → `false` in the Include suites | Re-baseline. Correct: an inner join is what a required navigation should emit, and what relational EF Core emits. |
| 2 + 2 | `Multiple_joins_Where_Order_Any` MQL path change | Re-baseline; data assertion passes. |
| 4 + 2 | Overrides that now **succeed** where they asserted a translation failure | Rewrite to assert the correct data. These are fixes. |
| 2 | Exception-type change | Not contract for an unsupported shape, per the rubric. |
| 2 | Pre-existing `Select_Where_Navigation_Null_Deep` | Unrelated; EF-371. |

**Zero regressions; no row set got worse.**

The re-baselining is safe because it is **data-gated by construction**: `AssertMql` is the last call in an
override, after `await base.X(async)`, so a baseline can only be regenerated once the data assertion already
passes. Procedure: `EF_TEST_REWRITE_BASELINES=1`, then rebuild and re-run *without* it.

The four-to-six overrides that now succeed must be rewritten **by hand** — `EF_TEST_REWRITE_BASELINES` rewrites
MQL, not exception assertions, so a test asserting a translation failure cannot be repaired mechanically.

### 5.3 Exit criteria — as originally written for slice 1 alone (SUPERSEDED by §5.4)

Retained because §4A's correction is only legible against them.

- Zero failures **and zero skips introduced** on all three majors — not merely "3-version green". The suites
  carry pre-existing skips; the honest criterion is a delta against the recorded baseline.
- Four of the five EF-216 skips un-skipped and passing (`Include_with_multiple_optional_navigations`,
  `Multiple_include_with_multiple_optional_navigations`, `Navigation_from_join_clause_inside_contains`,
  `Navigation_inside_contains_nested`), plus `Select_Where_Navigation_Deep`.
- `Select_Where_Navigation_Null_Deep` remains skipped, with its `Skip` **retargeted** from EF-216 to EF-371.
- `Where_join_orderby_join_select` returns 2143.
- Every re-baselined MQL diff inspected to confirm the only change is the `preserve` flag (or, for the two path
  changes, that the new pipeline is right). A bulk regeneration that is not read is how a real regression hides
  inside 112 mechanical ones.

### 5.4 Exit criteria for the merged change

Same list, with the two corrections §4A forces:

- Zero failures **and zero skips introduced** on all three majors, as a delta against a captured `upstream/main`
  baseline. Un-skipping tests *reduces* the skip count, which is the intended direction.
- The four EF-216 tests un-skipped and passing, plus `Select_Where_Navigation_Deep` — now achievable, because the
  reattach is in the same change. Both `Select_Where_Navigation_Deep` and `Where_join_orderby_join_select` flip
  from asserting a throw to succeeding and must be rewritten **by hand**; `EF_TEST_REWRITE_BASELINES` rewrites
  MQL, not exception assertions.
- `Select_Where_Navigation_Null_Deep` remains skipped, `Skip` retargeted from EF-216 to EF-371, and
  `docs/failing-spec-tests.md` updated for every test whose disposition changes.
- `Where_join_orderby_join_select` returns 2143.
- Every re-baselined MQL diff inspected exhaustively, not sampled. The instrument that worked: map every
  `preserveNullAndEmptyArrays : false` in each rewritten line back to `true` and require the result to be
  byte-identical to the line it replaced; then check *which* `$lookup` aliases flipped against the requiredness of
  their foreign keys. Token-level equality alone would not have distinguished a correct flip from a wrong one.
- Mutation check: with `src/` stashed, the new functional tests must fail. Record which.

## 6. Risks

- **The churn is large enough to hide a regression.** 112 mechanical rewrites are exactly the conditions under
  which a genuine failure gets swept along. Mitigation: the diff inspection in §5.3, plus the fact that data
  assertions gate every rewrite.
- **An inner join drops rows that relational EF would never drop.** In a relational database a required FK is
  constraint-enforced, so an inner join is a no-op semantically. In MongoDB it is not, so this change makes
  dangling-FK rows disappear where they previously appeared with a null navigation. That is the intended
  behaviour — it matches the operator EF chose and what relational EF emits — but it is a real semantic
  commitment and should be a conscious one, not a side effect. It is the main thing to disagree with if you are
  going to disagree with this design.
- **Anything relying on the current left-outer behaviour breaks.** Nothing shipped can, per §2.

## 7. Open questions for review

### 7.1 Should site 3 (nested reference `ThenInclude` under a collection Include) follow the same rule?

The prototype leaves it hard-coded `true`. Two defensible readings:

- **Leave it.** An `Include` should never change the result set, and relational EF Core uses a LEFT JOIN for
  Include regardless of requiredness in some shapes. The unwind there is inside the collection sub-pipeline, so
  a non-preserving unwind would silently drop collection *elements*.
- **Make it consistent.** A required nested reference is the same modelling statement as a required top-level
  one, and leaving the two inconsistent means the emitted semantics depend on where in the Include tree the
  navigation sits. The discriminator would be `IsRequired` (no operator is in hand at that site).

**Recommendation: leave it in this slice, and record the inconsistency explicitly** rather than widening scope
into a second family of shapes with its own churn. But it is a genuine inconsistency and I would rather you
ruled on it than have it discovered later as a surprise.

### 7.2 Should the dangling-FK seed be added to a shared fixture rather than a new one?

No existing fixture has one, and its absence has now hidden two separate bugs (this, and the EF-368 native-path
equivalent). A shared fixture would give every future join/Include test the same exposure. Against: it would
change expected results across many existing tests at once.

**Recommendation: new dedicated fixture for this slice**, and treat "should a shared fixture carry a dangling
FK" as its own question, since retrofitting one is a much larger change than this bug fix.

## 8. Non-goals

- ~~**EF-369's reattach.**~~ **No longer a non-goal** — §4A merged it in, as §4B. It is the other half of this
  change.
- **EF-371** (`Select_Where_Navigation_Null_Deep`): a self-referencing two-hop navigation collapsing to one
  join. Different root cause; not fixed here, and the test's `Skip` is retargeted to it.
- **EF-368** (native reference Include, unmerged native branch). It needs the same *decision* for its own
  lowerer, and §4.1's rule is intended to be the shared answer, but the code is on another branch.
- Widening the bulk source classifier. Recorded on EF-369: the bulk `ExecuteUpdate`/`ExecuteDelete` path is
  protected from that defect only incidentally, by `ClassifyBulkSource` rejecting EF's trailing `Select`. Both
  halves of this change land before any such widening, which the merge guarantees.

## 9. Provenance

Design derived from a prototype spike whose findings, both candidate patches, and retained TRX files are at
`.superpowers/ef369-fix-design-spike.md`, `ef369-unified-fix.diff`, `ef369-fix.diff` (the superseded gated
variant) and `ef369-fix-design-spike-trx/`. Every figure in §1 and §5.2 is from that spike's measurements on the
three EF majors; nothing here is inferred from reading alone except the site-2 and site-3 analysis in §3, which
was derived by reading the call sites and is marked as such.

Two claims in this document were subsequently measured **false** during implementation and are corrected in
place rather than deleted: §5.3's exit criteria (see §4A) and §4A.1's own prescription (see §4A.1). Both were
reached by reading rather than running. The `.superpowers/` spike remains accurate for the *unified* change it
actually measured; the error was in attributing its figures to a slice it did not measure.
