# EF-368 — native single-level reference `Include` (design)

> **Status.** Design, approved section-by-section 2026-08-04. Supersedes the "Implications for slicing"
> section of `2026-08-03-native-reference-include-spike-findings-architecture.md`. That spike's
> *measurements* stand; its recommended decomposition does not, for the reasons in §1 and §9.
>
> **Branch.** `EF-368`, stacked on `NativeQueryOngoing` (PR #324). Requires the prerequisite in §2.

---

## 1. Why this design differs from the spike

The spike named **one blocking unknown**: does the flat driver-LINQ fallback produce correct results for a
*lone* reference `Include` when a real reference `LookupExpression` is registered? It answered **NO**, and
the reason was that the flat path dropped user-composed operators and emitted an unconditionally left-outer
`$unwind`.

Both of those are EF-369 and EF-370 — **fixed**. `StripJoinForLookup` now applies to every join chain, and
its own comment records why that became possible: each pending lookup carries its own
`PreserveNullAndEmptyArrays`, taken from the LINQ join operator EF produced, so *"whether EF synthesized a
join from a required navigation or the user wrote one stops mattering — both are inner"*.

So the blocking unknown flips to **YES**, and it flips **by construction rather than by luck**: the two
defects EF-370 deliberately left open cannot reach this slice.

| Open defect | Requires | Reachable here? |
|---|---|---|
| EF-373 — operator interleaved between joins is hoisted above both lookups | **two** joins | No — one join |
| EF-372 — third hop emits an unprefixed `localField`, drops every row | **three** hops | No — one hop |

They are excluded by the slice's own scope, not by a guard this slice has to write and defend. That is why
this design does **not** need the spike's contingency (a mode-aware alias decision deferred past the gate,
which it correctly called a shaper-architecture refactor).

## 2. Prerequisite — port EF-370 onto the native branch, at the tip

The code this slice needs lives in two **disjoint** populations:

| Needed | `NativeQueryOngoing` | EF-370 / main-line |
|---|---|---|
| `GetStreamingReferenceLookups`, `Lookups`, `IsStreamableReference`, native lowerer | present | absent |
| `PreserveNullAndEmptyArrays`, `_injectAfterBaseSourceLookups` (composed-operator reattachment) | absent | present |

Neither branch alone can host the slice. Main is not available as an integration point for the foreseeable
future (owner ruling, 2026-08-04), so **EF-370's work is redone on the native branch** rather than inherited
from main.

- **At the tip**, not the base of the stack. Owner left this open; tip avoids rebasing 47 commits for no
  benefit, since conflicts with main are resolved whenever they arise either way.
- Mechanically: `git cherry-pick 4a9ec84` onto the native tip. The two conflicts are already measured —
  `LookupExpression.cs` is an **adjacent-insertion collision** (native adds `IsNativeCollectionLookup`,
  EF-370 adds `PreserveNullAndEmptyArrays`; keep both members), and `docs/failing-spec-tests.md` needs
  judgment, not a mechanical resolution.
- Lands as one squashed slice commit **on `NativeQueryOngoing` itself**, not inside `EF-368` — it is
  EF-370's work, not this slice's, and PR #324 should carry it independently of whether EF-368 ships.
  `EF-368` then rebases onto the advanced native tip and the slice proper starts from there.
- PR #328 stays open as its own main-bound PR. If the native branch merges first, main inherits EF-370 from
  there instead; the duplication is accepted.

**Two pieces of fallout the port owns, both absent on main:**

1. `RequiredNavigationUnwindTests` contains a **collection**-Include case. Collection Include is already
   native on this branch (EF-339), so that test takes the native path here and may need a different MQL
   baseline than the driver-LINQ one EF-370 pinned.
2. EF-367 made `AssertTranslationFailed` strict across 234 call sites in the four Northwind Include suites.
   Those sites have never compiled against EF-370's changed row semantics. Each side is green in isolation;
   the combined tree has never been run. This must be a real test pass, not a merge-clean check.

## 3. Architecture — one state change, three consumers

The slice adds **no new emission mechanism**. It makes one query-expression state change and lets three
existing consumers follow.

For a recognized single-level reference `Include`, register:

```csharp
new LookupExpression(navigation, forceUnwind: true) { PreserveNullAndEmptyArrays = isLeftOuter }
```

`UsesDriverJoinFields` — `_innerCollections.Count > 0 && !_pendingLookups.Any(l => l.ForceUnwind)` — then
computes `false`. This is exactly what `TranslateJoinCore`'s `isSecondOrLaterJoin` block already does for
multi-join queries, and that block's comment states the intended architecture: *"Rather than toggle a
mutable flag, we register the forced-unwind lookups; `UsesDriverJoinFields` is then computed from that state
and never contradicts the emitted pipeline."* This slice opts the single-reference case into an existing,
already-exercised mode.

| Consumer | Reads | Status |
|---|---|---|
| Native lowerer | `IsStreamableReference` arm → `$lookup` + `$unwind` on `_lookup_<Nav>` | built; spike rendered it end-to-end |
| DOM shaper | `GetCrossCollectionFieldName`'s `else` → the `_lookup_<Nav>` alias | built; was on the wrong side of the flag |
| driver-LINQ fallback | `StripJoinForLookup` → flat `$lookup`s | built; EF-370 made it correct |

**Correction (final fix wave, Finding 5): the pseudo-code above is NOT what shipped, and the Task-8 note that
followed it described a discriminator the code does not use.** The shipped registration site
(`TryConfirmReferenceInclude`) reads:

```csharp
new LookupExpression(navigation, forceUnwind: true)
{
    PreserveNullAndEmptyArrays = !navigation.ForeignKey.IsRequired
}
```

There is **no `isLeftOuter` in hand there** — the confirm runs on the trailing `Select`, not on the join — and
`ForeignKey.IsRequired` is **not** "exactly EF-370's discriminator": §9 below records that EF-370 measured
`IsRequired` **alone insufficient** in general and uses the LINQ operator where observable, with `IsRequired`
only as a fallback. The shipped behaviour is nonetheless **correct, and correct for a reason specific to this
site**: the recognizer admits only EF's own nav-expansion shape for a single-level reference `Include`, and
nav-expansion emits `Queryable.Join` for a **required** navigation and `LeftJoin` for an **optional** one — so
for this one admitted shape the operator and `IsRequired` **coincide by construction**. It is not a general
substitute; `TranslateJoinCore` keeps using `isLeftOuter` for every other join, and if the recognizer is ever
widened past nav-expansion shapes this equivalence must be re-derived rather than assumed.

**Correction (Task 8, measured against the shipped tree): the `$unwind` semantics did NOT come free.**
The *value* to use was known up front (per the paragraph immediately above) — but
`MongoUnwindStage`'s constructor parameter `preserveNullAndEmptyArrays` **defaults to `true`**, and the
native reference-Include arm in `MongoSelectLowerer` originally called it with no explicit argument, so the
recognized value was silently discarded and every reference Include unwound as a left join regardless of
`isLeftOuter`. A one-line `MongoSelectLowerer` change — passing `lookup.PreserveNullAndEmptyArrays` explicitly
at the reference-Include call site (see `MongoSelectLowerer.cs`'s `AppendLookupStages`) — was required for the
flag to be read at all. So this section's original claim ("No shaper change, no lowerer change, no new
expression node") is right about the shaper and the expression node, but wrong about the lowerer: a lowerer
change was needed, just a small one, not a new emission mechanism.

### 3.1 Registration site — a departure from the spike

The spike proposed registering in `TranslateJoinCore`. That does not work cleanly: the trailing
`Select(ti => Include(ti.Outer, Nav, ti.Inner))` is what identifies the shape, and it has not been visited
when the join is translated. Recognizing at the join would mean matching on the key selectors alone, which
a **user-authored** `Join` can also match — flipping the fallback MQL for user joins and re-baselining the
56 `NorthwindJoinQueryMongoTest` cases, well outside the agreed scope.

**Register in `TranslateSelect`**, where the `IncludeExpression` is in hand. Safe because nothing reads
`UsesDriverJoinFields` between the two points: the join site uses the `_lookup_<Nav>` alias unconditionally,
and both the shaper and the EF-to-driver-LINQ bridge are constructed later, in the compiling visitor. User
joins are untouched by construction rather than by a guard.

### 3.2 How the join is admitted without un-marking anything

There is an ordering problem: `PopulateNativeSlots` runs on the **join** node, which is visited *before* the
trailing `Select`. So the gate must decide on the join before the `IncludeExpression` that identifies the
shape has been seen — and `MongoSelectDefinition._hasUnsupportedOperator` is documented **"never unset"**, so
"mark non-native at the join, un-mark at the Select" is not available and must not be introduced.

Resolve it the way this codebase already resolves the same class of problem — **compute the decision from
state instead of toggling a flag**, exactly as `UsesDriverJoinFields` does:

- The slot populator's join arm records a **candidate**: `MarkSawCandidateReferenceIncludeJoin()`. It does
  *not* mark non-native.
- `TranslateSelect` records **confirmation** when the recognizer matches:
  `MarkReferenceIncludeConfirmed()`.
- `Route` computes `Fallback` when `_sawCandidateJoin && !_referenceIncludeConfirmed`.

This is **default-deny**: a user join with no trailing `Include`, or one whose `Include` fails any conjunct,
never gets confirmed and therefore falls back. Nothing is ever unset, and no state is mutated after the fact.

**Files touched:** `MongoQueryableMethodTranslatingExpressionVisitor.cs` (recognizer, pass-through
predicate, registration), `NativeTranslation/NativeSlotPopulator.cs` (candidate-join arm), and
`Expressions/MongoSelectDefinition.cs` (the two provisional signals and the `Route` term). No shaper change,
no new expression node — but see the correction in §3 above: a small `MongoSelectLowerer` change WAS needed,
to thread `lookup.PreserveNullAndEmptyArrays` through to `MongoUnwindStage` explicitly (its constructor
parameter defaults to `true`, so the recognized `isLeftOuter` value would otherwise be silently discarded).

## 4. Scope — settled decisions

| Decision | Ruling |
|---|---|
| Gate breadth | **Pending Include navigations only.** Not nav-expansion `LeftJoin` generally (~382 fallback-served tests carry an `_outer` baseline). |
| Sibling reference Includes | **Decline.** Two joins is where EF-373 lives; declining keeps it unreachable. Costs ~16 spec cases. |
| EF8/EF9 optional-FK reference Include | **Keep the asymmetry.** Recognize the shape but leave the `#if !EF8 && !EF9` `LeftJoin` dispatch alone; optional-FK Include still hard-fails there (EF-X020), as EF-370 already asserts deliberately. Follow-up ticket. |
| Multi-level / `ThenInclude` | **Out.** The larger ~212-case prize sits behind a single `else throw` in `AppendLookupStages`; deliberate follow-up, and it needs EF-372 first. |

## 5. The recognizer

Three parts, all checkable in `TranslateSelect`:

1. The selector body is `IncludeExpression { Navigation: INavigation nav, EntityExpression: MemberExpression
   { Member.Name: "Outer", Expression: ParameterExpression p } }` where **`p` is the selector's own lambda
   parameter** (a *single* hop — see §5.1), its `NavigationExpression` is the sibling `ti.Inner`, and the
   parameter's type name starts with `TransparentIdentifier`.
2. `!nav.IsCollection && !nav.IsEmbedded()`, `nav.DeclaringEntityType` is the query root, and
   `nav.TargetEntityType` is the join's inner collection entity type.

   **As-built note (final fix wave, Finding 2): the second and third clauses of conjunct 2 were ABSENT from
   the shipped code until that wave** — neither `IsSingleLevelReferenceIncludeSelector` nor
   `TryConfirmReferenceInclude` checked them; only "exactly one inner collection" (conjunct 4) was enforced,
   never that the single inner collection *is* the confirmed navigation's target. They are now present, in
   `TryConfirmReferenceInclude`, as
   `navigation.DeclaringEntityType == mongoQueryExpression.CollectionExpression.EntityType` and
   `mongoQueryExpression.InnerCollections.ContainsKey(navigation.TargetEntityType)`. The hazard they close:
   `TranslateJoinCore` resolves the navigation it keys the shaper's projection on independently (by FK-property
   name, with a `FirstOrDefault(n => n.TargetEntityType == inner)` fallback), while the confirm site emits
   `as: "_lookup_<navFromInclude>"` — a divergence would leave the shaper reading a field nothing wrote. Not
   demonstrated reachable; added because it is cheap and a decline is always correct (fall back, right rows).
   **Correction (measured false, do not repeat): this clause does NOT decline a TPH derived-type Include
   target whose join inner collection is keyed on a different type in the hierarchy.** A reviewer probed
   exactly that shape — a derived-type Include target, filter removed, otherwise this design's own test model
   — and found it is **admitted natively**: `NativeOnly` succeeds, no decline. EF does not record a
   discriminator predicate on the join's inner select for this shape, so `IsBareCollectionScan` (conjunct 5)
   is `true` and never fires either. This is not a data bug and not a regression: the one shape where the
   missing discriminator could matter (a required nav typed to the derived type whose FK points at a
   base-type document) throws `InvalidOperationException` identically under `Native`, `DriverLinq` and
   `NativeOnly` — no mode divergence, no silent wrong rows — and the superseded metadata guard this conjunct
   replaced never checked discriminators either, so this is not a narrowing this fix wave introduced.
3. The join's key selectors are exactly the FK correlation for `nav`, **single-property** —
   `LookupExpression`'s constructor reads only `Properties[0]` / `PrincipalKey.Properties[0]`, so a
   composite FK or composite PK declines.
4. Exactly one inner collection, and no forced-unwind reference lookup already registered (§5.2).
5. **The join's INNER select is a bare collection scan** (`MongoSelectDefinition.IsBareCollectionScan`,
   recorded on the outer select by `TranslateJoinCore` via `MarkSawNonBareJoinInner`). Added by the final fix
   wave (Finding 1), replacing a metadata `nav.TargetEntityType.GetQueryFilter() != null` test that missed a
   TPH-root-inherited filter and an EF10 *named* filter and so admitted, in **every** mode, a shape the flat
   `$lookup` cannot filter. The flat `$lookup` carries no sub-pipeline at all, so the inner side must be the
   whole target collection and nothing else; keying on the inner select's own shape closes query filters in
   all three spellings (anonymous, TPH-root-inherited, and EF10 named) by construction instead of by
   enumerating metadata shapes. **It does NOT close TPH discriminator narrowing** — a TPH derived-type
   Include target is currently admitted natively (see the correction on conjunct 2 above); this produces no
   measured wrong data and is not new to this branch.

### 5.1 The disjointness argument, corrected — conjunct 1 is load-bearing

The spike justified admitting this shape with *"EF never wraps a user result selector in an
`IncludeExpression`"*. **That is measured FALSE and must not be used.** A user join with a downstream
`Include`:

```csharp
db.Orders.Join(db.Customers, o => o.CustomerId, c => c.Id, (o, c) => o).Include(o => o.Customer)
```

**does** produce a trailing `Select(ti => Include(Entity: ti.Outer.Outer, Navigation: Customer, ti.Inner))`
over a user join. It differs from the nav-expansion shape only by the **double hop** (`ti.Outer.Outer`) and
by `InnerCollections.Count == 2`.

Consequences, both already folded into the predicate above:

- Conjunct 1's requirement that the `MemberExpression`'s `Expression` be the lambda **parameter itself** —
  a single `.Outer` hop — declines the user-join-with-`Include` shape above (a predicate matching merely
  `Member.Name == "Outer"` would admit it, because the outermost hop of `ti.Outer.Outer` is also named
  `Outer`).
- Conjunct 4's single-inner-collection requirement is a second line of defence for the same shape.

**Correction (Task 8, measured against the shipped tree): conjunct 1 is NOT uniquely load-bearing for the
reachable shapes checked — it is defence in depth, alongside an independent mechanism, not the sole gate.**
For the reachable user-join-with-`Include` query above, Task 4's candidate/confirmed join counter
(`MarkSawCandidateReferenceIncludeJoin`/`MarkReferenceIncludeConfirmed`, §3.2) *also* declines it on its own:
the user join records a candidate join, and because this query's `Include` is a double-hop
(`ti.Outer.Outer`) it never reaches `MarkReferenceIncludeConfirmed`, so `Route` resolves to `Fallback`
independent of conjunct 1. The two mechanisms are redundant for this shape, not additive. And for the
same-target **sibling** reference-Include shape (§5.2), `TryConfirmReferenceInclude` — where conjunct 1
lives — is never even reached at all: the tree there is a **nested** `IncludeExpression` that fails the
recognizer's earlier structural check (conjunct 1's own `IncludeExpression { EntityExpression: MemberExpression
{ ... } }` match) before conjunct 1's parameter-identity clause is evaluated. So "load-bearing, not
defence-in-depth" overstated conjunct 1's role for the shapes actually measured; the corrected claim is that
it is defence-in-depth, backed by the join-counter mechanism, for the user-join-with-`Include` case — and
whether conjunct 1 is uniquely load-bearing for some *other*, unmeasured shape remains unproven, not settled
either way.

All other user-join spellings measured — plain `Join`, BCL `LeftJoin`, `GroupJoin`,
`GroupJoin` + `DefaultIfEmpty` — carry no `IncludeExpression` at all. The remaining verification task is
therefore narrower than the spike's: re-confirm the double-hop shape and these two conjuncts against the
**ported** base, rather than re-litigating whether user joins can carry an `IncludeExpression` (they can).

### 5.2 Correction to the spike's sibling guard

The spike proposed guarding siblings with `InnerCollections.Count > 1`. **That does not hold.**
`_innerCollections` is a `Dictionary<IEntityType, MongoCollectionExpression>` and `AddInnerCollection` is
`TryGetValue`-keyed, so two reference Includes targeting the **same entity type** (`Doc.Author` and
`Doc.Editor` → `Person`) collapse to one entry and `Count` stays `1`. The guard would pass and admit a shape
§4 declines.

The reliable guard is **"decline if a forced-unwind reference lookup is already registered on this query"**,
which catches ordinary siblings and the same-target pair alike. This also matters because explicit
registration removes the old target-type synthesis bail-out that used to protect the same-target model *by
accident* — so it becomes a deliberate decline rather than an emergent one.

### 5.3 Decline list

Each with a tripwire test asserting the disposition stays byte-identical **and** that `NativeOnly` throws —
proving the decline is a decline, not a silent pass.

- Sibling / second reference `Include`, including same-target (§5.2).
- `ThenInclude` / transitive reference (`LocalField` already `_lookup_`-prefixed).
- Composite FK or composite PK.
- Source with `HasTerminalOperator` (the invariant every own-`Translate`-override operator must honour).
- Reference `Include` + collection `Include` on one query.
- Filtered `Include` — unreachable for a reference, kept as defence-in-depth.
- Grouped / `Distinct` / `VectorSearch` sources, via existing guards.
- **A query filter on the target, in ANY spelling** — the target's own anonymous filter, one inherited from a
  TPH root, or an EF10 *named* one — plus any other inner side that would need a `$lookup` sub-pipeline.
  Declined by conjunct 5 (final fix wave, Finding 1). Tripwires:
  `NativeReferenceIncludeTests.Query_filter_on_the_included_target_still_declines`,
  `.Query_filter_inherited_from_a_TPH_root_on_the_included_target_declines`, and (EF10)
  `.Named_query_filter_on_the_included_target_declines` — the last two mutation-proved against the reverted
  guard, where each returns 2 rows where 1 is correct, under `Native` **and** `DriverLinq` alike.

## 6. Data flow

For `db.Orders.Where(o => …).Include(o => o.Customer)`, required FK. Filter/sort/paging stay ahead of the
join, which is already the canonical lowerer order (`AppendSelectOpStages` then `AppendLookupStages`) and
already matches what nav-expansion produces — no reordering work.

**Native (new capability):**

```
{ $match: … },
{ $lookup: { from: "Customers", localField: "CustomerId", foreignField: "_id", as: "_lookup_Customer" } },
{ $unwind: { path: "$_lookup_Customer", preserveNullAndEmptyArrays: false } }
```

**driver-LINQ fallback (changed shape, same results).** Was
`{$project:{_outer:"$$ROOT",_id:0}}, {$lookup:{… as:"_inner"}}, {$unwind:"$_inner"}, {$project:…}`; now the
flat form via `StripJoinForLookup`, with the same `preserveNullAndEmptyArrays` decision.

### 6.1 The safety property

Both modes emit `_lookup_<Nav>`, so the DOM shaper **reads the same field in either mode**. Native-vs-
fallback is decided *after* the shaper is built, which is normally the governing hazard in this area — but a
shaper reading `_lookup_<Nav>` is correct whichever way the gate later goes. This is the reason for choosing
this approach over making the shaper alias-aware only on the native path, which is unsafe for exactly that
reason and is recorded as such in `Query/AGENTS.md`.

**This section used to end "There is nothing mode-dependent left to get wrong." That sentence overstated the
property and is deleted, not annotated.** It is true of the **shaper** and false of the **pipeline**:
confirming a reference `Include` registers a `ForceUnwind` lookup at *translation* time, which changes the
**fallback's** emitted MQL too (the driver `LeftJoin` form is replaced by the flat `StripJoinForLookup` shape,
§6 above). So `DriverLinq` is neither an independent oracle for this shape nor an escape hatch from a wrong
admission — `StripJoinForLookup` strips whatever the join carried, including a query filter's own `Where`.
That is precisely why the final fix wave's Finding 1 (an incomplete query-filter guard) was wrong in **all
three** modes rather than only under `Native`, and why its replacement is keyed on the join's inner select
shape at the point the candidate is recorded rather than on anything read later. A future admissibility
widening here must be judged on the fallback pipeline as well as the shaper.

### 6.2 Two hazards neutralized, not merely mitigated

- **Silent-null read-back.** A missing `_lookup_<Nav>` yields a null navigation with no exception. For a
  **required** navigation this can no longer arise: the inner `$unwind` drops the row, which is the correct
  answer. For an **optional** navigation a null navigation *is* the correct answer.
- **Row-count divergence.** Gone, because both paths read the same `LookupExpression.PreserveNullAndEmptyArrays`
  flag (set from `!ForeignKey.IsRequired` at the confirm site — see the §3 correction for why that is sound
  for this shape specifically).

### 6.3 Streaming is partly in scope — state it, don't discover it

`IsStreamableReference` stays true for a registered forced-unwind reference lookup (`ForceUnwind` is not one
of its conjuncts). So on a **unidirectional** model — where `StreamingEligibility` measured
`IsEligible = True` — the one-pass streaming materializer **will fire** for this shape via
`LookupReferencePlan`, which already reads `_lookup_<Nav>`. Bidirectional models (Northwind's case, and the
ordinary case) stay on the DOM shaper, because eligibility rejects a reference target carrying an inverse
collection.

Consequence: the slice must **test both materializers**. Lifting the eligibility rejection remains a
separate slice.

## 7. Testing

- **Differential tests are the gate.** The ported `RequiredNavigationUnwindTests` already seeds dangling
  FKs; add mode assertions — `Native == DriverLinq == NativeOnly`, row for row, for required and optional
  navigations. This is the assertion that would have caught the row-count divergence, and it now costs an
  attribute rather than a new fixture.
- **Both materializers**, per §6.3: a bidirectional model (DOM) and a unidirectional one (streaming).
  Testing only the Northwind-shaped case would leave the streaming path untested while believing it out of
  scope.
- **Guards track capability, not model shape.** Required-navigation cases run on **all three majors** — a
  required nav lowers to `Queryable.Join`, which dispatches everywhere. Only optional-FK cases carry
  `#if !EF8 && !EF9`, and they assert the EF-X020 asymmetry rather than merely accommodating it. (EF-370's
  lesson: a suite was gated because its *fixture* used nullable FKs, so an ungated fix had zero coverage on
  two majors.)
- **Pin both MQL shapes** — native and fallback — per EF-370's precedent.
- **Every decline gets a tripwire** (§5.3).

### 7.1 Exit criteria are re-measured, not inherited

The spike's "56 cases move, 56 re-baselines" was measured against `NativeQueryOngoing` at `365391f` —
**before** EF-366, EF-367, and the EF-370 port, each of which moves the baseline. The plan's first
measurement task is therefore a `NativeOnly` sweep on the **ported** base; that number becomes the exit
criterion.

This is stated explicitly because the failure mode is specific and recent: copying a figure measured at one
scope into a design as another scope's exit criteria produced a plan that looked shippable and was not.

**AS-BUILT OUTCOME (final fix wave, Finding 6) — this subsection previously carried the re-measured 56 as an
open exit criterion and no result, so a reader of the design alone would conclude the slice under-delivered.**
Of the re-measured **56** `NativeOnly` candidates, **32 pass** and **24 decline cleanly** for three named
out-of-scope gaps:

1. a predicate over a **composite-PK component** of the Include's own entity;
2. a whole-entity **`Distinct()`** after the `Include`;
3. a **reducer predicate over the included navigation** itself.

32 sits at the **bottom of the band the spike predicted, 32–56** — the spread exists because **3 of the 7
methods** in the sweep compose an extra operator on top of the `Include`, and any such composition can push a
case into one of the three gaps above. That band appears nowhere else in the design or the plan, which is why
recording it here matters: the exit criterion is "every mover accounted for", and all 24 non-movers are.

Exit criteria proper:

1. Zero test failures on **all three** EF majors (`/test-all`).
2. The re-measured `NativeOnly` delta matches the sweep, with every mover accounted for.
3. Every decline in §5.2 has a passing tripwire.
4. `docs/failing-spec-tests.md` restates **only** the rows this slice moves. The totals column reconciles
   under no rule and its basis is lost — do not patch it row-by-row.

## 8. Out of scope

Sibling / same-target reference Includes; `ThenInclude` / multi-level; nav-expansion `LeftJoin` generally;
EF8/EF9 optional-FK; lifting `StreamingEligibility`'s inverse-collection rejection; fixing EF-372 or EF-373.

## 9. Spike claims that are stale, and doc corrections

**Stale in the spike** (its measurements were taken at `365391f`, read-only, pre-EF-370):

- The blocking unknown's answer (NO → **YES**, §1).
- The recommended registration site (`TranslateJoinCore` → `TranslateSelect`, §3.1).
- The sibling guard (`InnerCollections.Count > 1` is not sound, §5.2).
- The disjointness justification ("EF never wraps a user result selector in an `IncludeExpression`") — **measured false**, §5.1.
- "No fixture has a dangling-FK seed" — EF-370 built one (`RequiredNavigationUnwindTests`).
- The 2026-08-03 owner decision to *"accept the EF8/EF9 optional-FK improvement if it falls out free"* is
  **superseded** by the 2026-08-04 ruling in §4: keep the asymmetry, file a follow-up.
- Q-B's recommendation to prefer `ForeignKey.IsRequired` **over** the LINQ operator as the unwind
  discriminator is superseded by EF-370, which measured `IsRequired` alone insufficient and uses the operator
  where observable with `IsRequired` only as fallback. This design inherits EF-370's `isLeftOuter`.
- File/line citations for `GetStreamingReferenceLookups`, `Lookups`, `IsStreamableReference` and
  `LookupExpression`: these survive as *behaviour* but their line numbers do not survive the EF-370 port.
  **Cite behaviour, not line numbers.**

**Two doc corrections to fold in**, each to be re-checked against the ported base in case EF-370 already
fixed it:

1. `Query/AGENTS.md` says reference `Include` nav-expands to a `LeftJoin`. Measured: `Queryable.Join` for a
   **required** FK, `LeftJoin` only for an **optional** one, on all three majors — and that distinction is
   the source of the whole unwind hazard, so the imprecision is not cosmetic.
2. `IsTransparentIdentifierMemberAccessSelector`'s XML doc claims `TranslateJoinCore` unconditionally marks
   the outer side non-native. It does not; `NativeSlotPopulator`'s catch-all does.

## 10. Risks

| Risk | Mitigation |
|---|---|
| A user join CAN carry a trailing `IncludeExpression` (measured) — a loose recognizer would admit it | Conjuncts 1 and 4 are load-bearing and tested directly; re-confirm on the ported base (§5.1) |
| The port's combined tree has never been run — EF-367's 234 strict sites vs EF-370's changed row semantics | The port is its own task with a full three-major run (§2) |
| Flat fallback for a **single** join has never been executed (it was multi-join-only) | First implementation task verifies it under `DriverLinq` before the native gate opens |
| Baseline churn larger than any recent slice (~56 assertions in both modes) | Re-measured sweep (§7.1); pin both shapes so churn is visible, not silent |
