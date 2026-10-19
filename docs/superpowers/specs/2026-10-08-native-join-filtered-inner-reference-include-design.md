# Native join with a filtered inner side and a reference `Include` — Design

**Ticket:** EF-322 (native LINQ pipeline). Closes the last EF10 spec test that passes only under driver-LINQ:
`NorthwindMiscellaneousQueryMongoTest.Perform_identity_resolution_reuses_same_instances_across_joins` (4 theory rows:
`async` × `useAsTracking`). Records finding **M22** / decision **D-D7** of
`docs/superpowers/findings/2026-10-01-native-parity/` (`g6-joins.md` §3.7, `main-issues.md`).

**Owner decision (2026-10-08):** build real native support that honors the inner `Where`. Default-mode rows change
from the driver bridge's (wrong) unfiltered result to the filtered, correct one. Not matching main's bug; not
declining loudly.

Items marked **[verified]** were read in code on 2026-10-08. Items marked **[spiked]** were confirmed by running a
throwaway prototype on EF10 the same day (see "Spike results"). Nothing remains marked as an open hypothesis.

> **Spike outcome (2026-10-08): Part A is validated; Part B is unnecessary.** The first draft of this spec assumed
> the inner reference `Include` was part of the inner query and needed migrating onto the outer query. It is not. See
> "Spike results" below, which supersedes the corresponding statements in the earlier sections.

## Problem

The test query is:

```csharp
from c in context.Customers.Where(c => c.CustomerID.StartsWith("A"))
join o in context.Orders.Where(o => o.OrderID < 10500).Include(o => o.Customer)
    on c.CustomerID equals o.CustomerID
select new { c, o };
```

Today's behavior, by mode **[verified]**:

| Mode | Behavior |
|---|---|
| `NativeOnly` | `NativeTranslationNotSupportedException` ("Query projects a non-entity result"). The spec override asserts that refusal. |
| `Native` / `DriverLinq` | Falls back to driver-LINQ, which **drops `OrderID < 10500`** and returns every order of the matching customers. Same on released v10.0.4; on EF8/EF9 main this test was `AssertTranslationFailed`. |

The base test (`NorthwindMiscellaneousQueryTestBase`) asserts only `Assert.Same` identity of `Customer` instances and
`Assert.Empty(ChangeTracker.Entries())`. It never checks which rows come back, which is why the dropped filter has
gone unnoticed.

### Why native declines

1. `TranslateJoinCore` (`MongoQueryableMethodTranslatingExpressionVisitor.cs`, ~2447–2484) calls
   `Select.IsBareCollectionScan` on the inner select. The inner has a `$match` in `PipelineOps` and a candidate
   reference-Include join, so it is false. The join gets `MarkSawNonBareJoinInner()` and
   `IsNativelyEligible = false`, no `JoinScope` exists, and the trailing `new { c, o }` projection has no arm that can
   confirm it, so `Route` computes `Fallback`.
2. ~~`RebindInnerShaperToOuterQuery` returns early for an `IncludeExpression` inner shaper.~~ **Wrong [spiked]:** at
   `TranslateJoinCore` time the inner shaper is always a plain `StructuralTypeShaperExpression` with no inner joins and
   no pending lookups; the `Include` is not part of the inner query. See "Spike results".

### Why the obvious fix does not work

* **Re-rooting the translated inner predicate under the join alias** (post-`$unwind` `$match` on
  `_lookup_Orders.OrderID`) needs a visitor that rewrites `MongoFieldExpression` names inside an already translated
  `MongoExpression`. `MongoExpression.VisitChildren` returns `this` **[verified]**, so no generic rebasing visitor
  exists, and writing one over ~60 node types is large and fragile. It is also only correct for an inner `Join`: for a
  left join, a post-unwind `$match` would drop outer rows that should be kept.
* **Matching the driver (dropping the filter)** bakes a known wrong-rows bug into the native path.

## Design

Two independent mechanisms, composed:

* **Part A — carry the inner `Where` inside the join's own `$lookup` sub-pipeline.**
* **Part B — register the inner reference `Include` as a transitive lookup on the outer query and bind its shaper.**

### Part A: inner filter as a `$lookup` sub-pipeline

An inner side whose only recorded operations are `MongoMatchOp`s is **not** unrecorded state to be dropped; it is a
predicate over the inner document. The `$lookup` already has the shape to hold it:

```
{ $lookup: { from: "Orders", localField: "_id", foreignField: "CustomerID",
             pipeline: [ { $match: { OrderID: { $lt: 10500 } } } ], as: "_lookup_Orders" } },
{ $unwind: { path: "$_lookup_Orders", preserveNullAndEmptyArrays: false } }
```

Why this is the right place:

* The predicate's field names are already relative to the inner document root, which is exactly the scope inside a
  sub-pipeline. **No rebasing, no alias prefixing, no new visitor.**
* Correct for **both** `Join` (unmatched/filtered-out rows are dropped by the non-preserving `$unwind`, as in EF) and
  `LeftJoin`/`GroupJoin` (the outer row survives with the preserving `$unwind`; the filter applies to which inner rows
  match, which is the LINQ meaning of a filtered inner source).
* The `localField`/`foreignField` + `pipeline` form is already emitted for `LookupPipelineKind.CorrelatedReducer`
  (`MongoPipelineFactory.RenderLookup`, ~417–437) **[verified]**, so the server shape is an existing, indexable one.

**New state.** Add to `LookupExpression`:

* `MongoExpression? InnerPredicate` — the conjunction of the inner `MongoMatchOp` predicates, in recorded order.
* `LookupPipelineKind.FilteredJoinInner` — a new kind, because the existing kinds each have a different
  native-eligibility answer (see the enum's own remarks). Write-once, like `PipelineKind` today.

**Rendering and parameters.** `LookupExpression.PipelineStages` is a `List<BsonDocument>` baked at compile time with
**no per-execution placeholder substitution** (`NativeProjectionBinder.IsConstantOnlyPredicate` exists solely to work
around this; **[verified]**). Re-using `PipelineStages` would therefore restrict the inner `Where` to constants, and
`o.OrderID < cutoff` with a captured variable would either decline or, worse, bake the first value into a cached plan
(Review Focus #2 of the parity plan, F2/F7). So:

* `RenderLookup` gains the `MongoQueryLanguageRenderer` and `PlaceholderTable` already used by the stage renderers and
  renders `InnerPredicate` into the `pipeline: [ { $match } ]` array **at pipeline-build time**, through the same
  path as a top-level `MongoMatchStage`. Parameters, per-execution clock values and `RuntimeParameterValues` then work
  with no new mechanism.
* `PipelineStages` is not used for this kind. `HasPipeline` must be `true` for it (or the kind must be checked
  explicitly) so that `IsStreamableReference`, `IsNativeCollectionLookup` and the lowerer's
  `lookup.IsReference && !lookup.HasPipeline` branch (`MongoSelectLowerer.AppendLookupStages`, ~470) route it correctly;
  see "Gates" below.

**What is admitted.** The inner select qualifies when **all** of:

1. Its `PipelineOps` contain only `MongoMatchOp` (no `MongoSortOp`, `MongoSkipOp`, `MongoLimitOp`,
   `MongoDistinctOp`) — paging or ordering inside the inner changes *which rows* the inner contributes and is not
   expressible as a filter.
2. It has no projections, `Cardinality`, `Grouping`, `SetOperation`, `VectorSearch`, `PostJoinOps`, trailing ops or
   unwind sources, and is not `IsGroupBy`/`IsDistinct`/`IsSetOp`. (This is `IsBareCollectionScan` minus the
   `_pipelineOps.Count == 0` and candidate-Include conjuncts. It must be a **new named predicate on
   `MongoSelectDefinition`**, e.g. `IsFilterOnlyCollectionScan`, written as the shared definition and called by the
   gate, never restated — see Query/AGENTS.md "the gate must call the fix's predicate".)
3. Every predicate renders without referencing an outer scope (`MongoOuterFieldExpression` / `$$ROOT` of the outer).
   An inner source cannot reference the outer query, so this is a defensive check that fails closed.
4. The join's key is a single `localField`/`foreignField` pair (`LookupExpression.CompositeKey == null`) and the
   lookup is a navigation lookup or a navigation-less raw-key lookup that `JoinLookupImplementsKeySelectors` already
   accepts. **Composite anonymous-key lookups** use the `let` + `pipeline` (`$expr`) form; appending the predicate to
   that pipeline is straightforward but is **out of the first slice** (decline, with a test pinning the decline).
5. TPH: if the lookup constructor already gave the lookup a `DiscriminatorNarrowed` `$match`, the kind is
   write-once and cannot also become `FilteredJoinInner`. The combination declines in the first slice, as
   `FilteredInclude` does today (`ExtractFilteredIncludePipeline`).
6. Query filters on the inner entity (`HasQueryFilter`, EF10 named filters) arrive as the same `Where` and are therefore
   *honored* by this design. That is the intended behavior (the bridge's flat `$lookup` silently ignored them; see the
   remarks on `MarkSawNonBareJoinInner`).

**`IsNativelyEligible` / `JoinScope`.** `joinInfo.IsNativelyEligible` in `TranslateJoinCore` currently starts with
`innerQueryExpression.Select.IsBareCollectionScan`. It becomes `IsBareCollectionScan || IsFilterOnlyCollectionScan`,
with the filter moved onto `joinInfo.Lookup` at that point. `MarkSawNonBareJoinInner` is still recorded for the
Include-chain path (`TryConfirmReferenceIncludeChain`), which emits a flat `$lookup` with no sub-pipeline and **must
keep declining** a filtered inner: that is the existing wrong-rows guard and is not changed.

### Part B: the inner reference `Include` — no new work [spiked]

The first draft proposed migrating an inner `IncludeExpression` onto the outer query. The spike disproved the premise: EF
keeps the `Include` as a **pending include applied to the join result** (`Include(ti.Inner, o.Customer)` in the result
selector), so by the time `TranslateJoinCore` runs the inner query is just `Orders.Where(...)`. A reference `Include` on a
**bare** inner side already goes native today (probe P2, all three modes, correct rows, identity preserved), emitting
the transitive lookup:

```
{ $lookup: { from: "Orders", localField: "_id", foreignField: "CustId", as: "_lookup_Ord" } },
{ $unwind: { path: "$_lookup_Ord", preserveNullAndEmptyArrays: false } },
{ $lookup: { from: "Customers", localField: "_lookup_Ord.CustId", foreignField: "_id", as: "_lookup_Cust" } },
{ $unwind: { path: "$_lookup_Cust", preserveNullAndEmptyArrays: true } }
```

The only thing that made the spec test's inner side non-bare was its single `MongoMatchOp`. With Part A carrying that
filter, the existing Include machinery composes unchanged (probe P3: `$lookup` with `pipeline: [{$match}]` followed by the
transitive Customer lookup; correct rows; `ReferenceEquals(c, o.Customer)` true). Part B therefore reduces to **tests that
pin the composition**, not code.

### Gates and invariants (what must change together)

* `IsFilterOnlyCollectionScan` is the single definition; the `TranslateJoinCore` eligibility, the `JoinScope`
  construction, and the lookup registration all call it.
* `TryConfirmReferenceIncludeChain` continues to decline `SawNonBareJoinInner`, and additionally declines when a chain
  level's own join lookup carries an `InnerPredicate` (spike finding 4). A filtered inner is admitted only as an
  *explicit* join in Part A, never as an Include chain level.
* **No non-native consumer may see an `InnerPredicate` lookup unguarded** (spike finding 3): the driver-LINQ bridge and
  any late fallback either apply the predicate or refuse. Add a single predicate (e.g.
  `LookupExpression.HasInnerPredicate`) and call it from the bridge's lookup handling and from
  `ClassifyNativeDisposition`; do not restate the check.
* `LookupExpression` consumers that branch on `HasPipeline` / `PipelineKind`
  (`MongoSelectLowerer.AppendLookupStages` ~470–520, `MongoPipelineFactory.RenderLookup`, `IsStreamableReference`,
  `NativeProjectionBinder` ~2739 `PipelineStages.Count`) each need an explicit decision for `FilteredJoinInner`. Audit
  all `PipelineKind` / `HasPipeline` references in the plan's first task and add the new kind to each switch; none may
  silently fall into a default arm.
* The late-fallback strip (`ProjectionAliasTier` / alias agreement) must still hold: if the native shaper is committed
  and the factory later declines, the alias read and emitted pipeline must agree (Review Focus #4). A `Native`-mode test
  with a forced late decline covers it.
* `PlaceholderTable` must remain empty for constants-only queries and populated for parameterized ones; the
  existing "asserting the PlaceholderTable stayed empty" pattern in `TryGetCorrelatedReducerLeaf` is for
  `PipelineStages` and does **not** apply to this kind, which renders through placeholders.

### Explicitly out of scope

* Inner `OrderBy`/`Skip`/`Take`/`Distinct`/projection/`GroupBy` (stays declined; falls back as today).
* Composite anonymous-key joins with a filtered inner (declines in slice 1).
* TPH-derived inner target with a filter (declines in slice 1).
* A `ThenInclude` on the inner reference Include
  (`Reference_Include_with_a_ThenInclude_on_a_join_scope_leaf_falls_back_with_correct_data` stays as is).
* Collection `Include` combined with a filtered inner (the collection nesting already goes into the join's sub-pipeline;
  combining the two needs the predicate and the nested stages to coexist in one pipeline and is a follow-up, with a
  declining test).
* Query-filtered `Include` targets as Include-chain levels (spike finding 4); they keep declining.
* EF8/EF9 `LeftJoin` shim shapes, which are a separate known gap (see `Query/AGENTS.md`).

## Spike results (2026-10-08, EF10, uncommitted prototype)

Probe model: `Cust { Id, Name }`, `Ord { Id, Total, CustId, Cust }`; customers `A1, A2, B1`; orders
`1(A1,10) 2(A1,20) 3(A1,600) 4(A2,700) 5(B1,30)`. Filter `Total < 500`. The prototype added an `InnerPredicate` to
`LookupExpression`, rendered it inside `RenderLookup` through the existing `MongoQueryLanguageRenderer`/`PlaceholderTable`,
treated a filter-only inner as eligible in `TranslateJoinCore`, and skipped `MarkSawNonBareJoinInner` for it (~60 lines in
4 files; kept outside the repo).

**Before the prototype** (released-equivalent behavior):

| Probe | NativeOnly | Native / DriverLinq |
|---|---|---|
| P1 filter-only `Join` | refuses | **throws `ExpressionNotSupportedException`** (loud) |
| P5 filter-only `GroupJoin...DefaultIfEmpty` | refuses | **throws `ExpressionNotSupportedException`** (loud) |
| P2 `Include` only, bare inner | native, correct | native, correct |
| P3/P4 filter + `Include` | refuses | **silently drops the filter** (orders 3 and 4 returned): M22 |

So M22's silent wrong rows occur **only when an `Include` is present**; a plain filtered inner already fails loudly on
main, and native support turns that failure into correct results.

**With the prototype** (`NativeOnly` and `Native`):

| Probe | Result |
|---|---|
| P1 | `A1:1, A1:2, B1:5` (correct) |
| P3 (spec shape) | `A1:1, A1:2`, `Customer` populated, `ReferenceEquals(c, o.Customer)` true (correct) |
| P4 (captured `cutoff`) | `A1:1, A1:2, B1:5` (correct) |
| P5 left join | `A1:1, A1:2, A2:null, B1:5` (correct: `A2` kept, its only order filtered out) |
| P6 one compiled plan, one `DbContext`, `cutoff` = 25, 500, 5, `null` | `{1,2}`, `{1,2,5}`, `{}`, `{}`: **read per execution, not baked** |

### Findings that change the design

1. **Part B is unnecessary** (above).
2. **Rendering through the placeholder table works** (P6). The `InnerPredicate` approach needs no new parameter
   mechanism; `RenderLookup` only needs the renderer and `PlaceholderTable` threaded in from `RenderStage`.
3. **Non-native consumers of the join must not silently drop the filter.** With the prototype, explicit `DriverLinq`
   mode went from *throwing* for P1/P5 to *silently returning unfiltered rows*, because marking the join eligible builds
   a `JoinScope` and a `Lookup` that the driver-LINQ bridge (`MongoEFToLinqTranslatingExpressionVisitor`) consumes
   without knowing about `InnerPredicate`. The same hazard applies to a **late fallback** from `Native` mode (Review
   Focus #4); that path was not exercised by the spike. Required: any lookup with an `InnerPredicate` that reaches the
   bridge must either apply it or refuse (`FallbackWrongData`, the existing hard-decline classification). Prefer refusing:
   keep P1/P5 loud under `DriverLinq`.
4. **The flat Include-chain path must keep declining a filtered inner.** The prototype skipped `MarkSawNonBareJoinInner`
   entirely, which also let `Orders.Include(o => o.Customer)` with a **query filter on `Customer`** go native
   (`NativeReferenceIncludeTests.Query_filter_on_the_included_target_still_declines`,
   `Named_query_filter_on_the_included_target_declines`). That may be correct, but it is a separate feature with its own
   required-vs-optional-FK semantics. Slice 1 bounds it: `TryConfirmReferenceIncludeChain` declines when a **chain level's
   own** join lookup carries an `InnerPredicate`; the *explicit* join in the spec shape is not a chain level, so P3
   still composes. Promoting query-filtered Include targets is a follow-up.
5. **Blast radius: shapes pinned as "unsupported" start to work.** With the prototype, under `NativeOnly` these
   previously-refusing tests no longer throw (all "No exception was thrown"): spec
   `NorthwindMiscellaneousQueryMongoTest.Join_take_count_works`,
   `NorthwindJoinQueryMongoTest.{GroupJoin_DefaultIfEmpty2, GroupJoin_SelectMany_subquery_with_filter,
   GroupJoin_SelectMany_subquery_with_filter_and_DefaultIfEmpty}`,
   `NorthwindQueryFiltersQueryMongoTest.{Included_many_to_one_query, Included_many_to_one_query2}`; functional
   `NativeJoinTests.Query_filtered_join_target_still_declines_cleanly_in_NativeOnly` (plus the two `NativeReferenceIncludeTests`
   above, which finding 4 keeps declining). Each is an `AssertTranslationFailed`/decline pin whose expectation must be
   flipped. **Flipping is also a free correctness check:** replace the override body with the base call so the EF
   in-memory oracle compares the rows, and treat any mismatch as a bug in this work. Separately, MQL baselines of tests
   that already went native through a filtered inner shift (field-order-sensitive).
6. Two further failures in the first prototype run
   (`NativeJoinTests.Inner_side_Where_that_declines_natively_does_not_confirm_the_join` `[Native, "ToString"]` and
   `NativeGroupByOverJoinTests.Null_propagating_value_over_an_unmatched_left_join_side_stays_native` `["Coalesce"]`) did not
   reproduce on a rerun run on its own. Re-check them during implementation rather than assuming they are noise; the first
   is named for exactly this feature area.

## Semantics

| Shape | Expected result |
|---|---|
| `Join`, inner `Where` | Only (outer, inner) pairs where the inner row satisfies the predicate. |
| `Join`, inner `Where` filters out every match for an outer row | Outer row absent (inner-join semantics). |
| `LeftJoin` / `GroupJoin…DefaultIfEmpty`, inner `Where` | Outer row kept with `null` inner when no inner row passes the filter. |
| Inner row with dangling `CustomerId` + reference `Include(Customer)` | Row kept; `Customer == null` (the Include's unwind preserves). |
| Inner row passing filter, `Include` target missing | Same as above. |
| Parameterized predicate executed twice with different values | Each execution uses its own value; second execution is not served from the first's baked plan. |
| `null` parameter value | Behaves as the equivalent C# comparison; not baked at compile time. |

## Breaking-change assessment

Measured against the latest released assembly (`v10.0.4`, published 2026-09-17; `v9.1.4`, `v8.4.4`) per the root
AGENTS.md rubric:

* The path a supported query takes and the exact MQL are **not** breaks.
* The **row set** of `join … in Orders.Where(…).Include(…)` changes on EF10, from the bridge's unfiltered result (a
  bug, M22) to the filtered one. A result change is a break by the rubric even when it fixes a bug, so add a
  `BREAKING-CHANGES.md` entry describing it as a correctness fix with the affected shape.
* EF8/EF9: main threw `AssertTranslationFailed` for this shape; native newly supports it. That is new support, not a
  break. Confirm per version in the plan (the spec override is shared across versions).
* File M22 against upstream (driver-LINQ bridge drops a join inner's `Where`), per `main-issues.md`.

## Testing

All new shapes are tested in three modes (`NativeOnly`, `Native`, `DriverLinq`), per the parity plan.

* **Oracle.** The driver oracle is *wrong* for this shape, so the tests use hand-computed expectations over raw
  `BsonDocument` seeds (`NativeModeAssert.NativeAndExpected` with `driverKnownWrong: true`, citing M22), not
  `NativeAndParity`.
* **Seeds** (raw, so materialization normalization can't hide gaps): orders on **both sides** of the filter
  (`OrderID` below and at/above the cutoff) for one customer; a customer with no orders; an order with a dangling
  `CustomerId`; an order whose customer exists.
* **Cases:** `Join` and `LeftJoin`/`GroupJoin…DefaultIfEmpty` with the filter alone; filter + reference `Include`;
  `Include` alone (regression: bare inner stays on the existing path); the exact spec shape; parameterized
  predicate run twice with different values and with `null`; a filter on a converted / `BsonRepresentation` property;
  a mixed `&&` predicate; an inner `HasQueryFilter`.
* **Declining pins** (each asserts clean decline under `NativeOnly` and correct data under `Native`): inner `OrderBy`,
  inner `Take`, inner `Distinct`, inner projection, composite anonymous key, TPH-derived target, collection `Include`
  with a filtered inner, `ThenInclude` on the inner reference Include.
* **Mutation checks:** drop the `InnerPredicate` render (rows unfiltered → test fails); make the unwind non-preserving
  for the Include (dangling-FK row vanishes → test fails); bake the predicate at compile time (second parameter value
  returns the first's rows → test fails).
* **Spec:** remove the `IsNativeOnly` refusal branch from the override; regenerate the baseline with
  `EF_TEST_REWRITE_BASELINES=1 … --filter "FullyQualifiedName~Perform_identity_resolution_reuses_same_instances_across_joins"`,
  `git diff`, rebuild, rerun. The new baseline **must** contain the `OrderID < 10500` `$match` inside the first
  `$lookup`'s `pipeline` — assert it explicitly, since the base test cannot.
* **Differential runner:** `tests/tools/native-parity-diff.sh EF10 <out> --gate` should end with `regress=0`.
* **Full suite on EF8, EF9 and EF10** (admission gate and `LookupExpression` rendering change); not per-class runs.

## Risks

| Risk | Mitigation |
|---|---|
| A `PipelineKind`/`HasPipeline` switch silently mishandles the new kind. | Audit task up front; add the kind to every switch with an explicit arm; a unit test per switch site. |
| Driver-LINQ bridge / late fallback silently drops the filter (spike finding 3). | Single `HasInnerPredicate` predicate; refuse in the bridge and in the gate; explicit `DriverLinq` tests for P1/P5 stay loud. |
| Pinned-unsupported shapes start working and their pins fail (spike finding 5). | Flip each pin by calling the base test so the in-memory oracle verifies rows; list them in the plan. |
| Parameter baked into a cached plan. | Render through `PlaceholderTable`; the two-execution test with a mutation check. |
| Query-filter semantics newly honored on an inner side surprise someone. | Documented as the intended fix; covered by an explicit test and the `BREAKING-CHANGES.md` entry. |
| Field-order-sensitive spec baselines elsewhere shift. | Full-suite run; regenerate only what the diff justifies. |

## Plan shape (for whoever writes the implementation plan)

The spike is done; its prototype diff and probe live outside the repo and are not committed.

1. **Part A with its guard, in one commit:** `InnerPredicate`, `FilteredJoinInner`, `IsFilterOnlyCollectionScan`,
   render-time placeholders, the eligibility change, **and** the `HasInnerPredicate` refusal in the bridge/gate and the
   Include-chain decline. Enabling eligibility without the guard regresses `DriverLinq` (finding 3). Failing three-mode
   tests first (probes P1, P3, P4, P5, P6 are the seeds), with mutation checks.
2. **Composition tests** for the inner reference `Include` over a bare and a filtered inner (no code expected).
3. **Spec test:** drop the `IsNativeOnly` refusal branch of
   `Perform_identity_resolution_reuses_same_instances_across_joins`, regenerate the baseline, assert the `$match` is inside
   the first `$lookup`'s `pipeline`.
4. **Flip the pinned-unsupported tests** listed in finding 5, each by calling the base test so rows are verified;
   regenerate any shifted baselines with a tight `--filter`, `git diff`, rebuild, rerun.
5. **Declining pins** for everything out of scope.
6. **Docs:** `Query/AGENTS.md` invariants (the new predicate, the bridge guard, "a filtered inner is admitted only as an
   explicit join"), `BREAKING-CHANGES.md`, and update `g6-joins.md` section 3.7 and `main-issues.md` M22 (loud on main
   without an `Include`, silent with one).
7. **Verification:** full suite on EF8, EF9 and EF10; differential runner with `--gate` ending `regress=0`.
