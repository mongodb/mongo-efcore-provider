# Native GroupBy fallback reduction — design

## Goal

Reduce the number of `NorthwindGroupByQueryMongoTest` tests that currently pass only via the driver-LINQ
fallback, by extending `NativeGroupByBinder` (and its collaborators) to natively translate more `GroupBy`
shapes. Each shape below is a genuine capability gap, not a bug: the query already produces correct results
today via the fallback; going native must not change results (`Native == DriverLinq` invariant), only the
execution path (per the top-level AGENTS.md rubric: which path a query takes is not a breaking change).

## Measurement method

MQL shape cannot prove a query went native (`Query/AGENTS.md`). Ground truth comes from running the class
under `MONGODB_EF_NATIVE_ONLY=1`, where a would-be fallback throws `NativeTranslationNotSupportedException`
instead of silently executing via the driver:

```bash
MONGODB_EF_NATIVE_ONLY=1 dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/... \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~NorthwindGroupByQueryMongoTest"
```

As of this design: **513 tests, 72 failures (36 distinct test methods × async)**. All 36 are plain
`await base.X(async)` overrides — none pre-existing declines — so all 36 currently rely on the fallback for
correctness today.

## Scope

6 of the 36 methods (`GroupBy_required_navigation_member_Aggregate`,
`GroupBy_with_group_key_access_thru_navigation`, `GroupBy_multi_navigation_members_Aggregate`,
`Join_GroupBy_Aggregate`, `Self_join_GroupBy_Aggregate`, `Join_groupby_anonymous_orderby_anonymous_projection`)
group a key or Select reached through a required reference navigation, or compose `GroupBy` directly on a
`Join`/self-join result. This needs `NativeGroupByBinder` to cooperate with the native join-scope machinery
(EF-322, in flight on another branch) — **deferred**, own follow-up design once that work lands.

The 6 deferred methods were delivered by `docs/superpowers/plans/2026-09-28-native-groupby-over-join-scope.md`
(`MongoSelectDefinition.GroupByJoinScope`), which also carried nine `AssertTranslationFailed` overrides native
as a knock-on: `GroupBy_optional_navigation_member_Aggregate`, `GroupBy_principal_key_property_optimization`,
`GroupBy_with_group_key_access_thru_nested_navigation`, `GroupJoin_GroupBy_Aggregate_2`,
`GroupJoin_GroupBy_Aggregate_3`, `GroupJoin_GroupBy_Aggregate_4`, `GroupJoin_GroupBy_Aggregate_5`, and
`GroupBy_Min_Where_optional_relationship`/`_2` (originally mode-split on EF10 — native under default, still
declining under `NativeOnly` because the post-group `Where` wasn't native yet; on EF8/EF9 they declined in every
mode. **Delivered** via `docs/superpowers/plans/2026-09-29-native-groupby-post-group-where-and-push-list.md`:
post-group `Where` over a keyed GroupBy's projected alias is now native, so both methods run the identical
pipeline on every EF version and mode — the mode/version split and the `AssertTranslationFailed` overrides were
removed). Paging recorded ahead of a non-1:1 grouped join declines rather than defers — deferring it past the
`$lookup` would silently change group counts.

This design covers the remaining **30 methods**, split into 9 independently shippable slices (SP1–SP9),
stacked in the order below (each depends on the previous landing, per this repo's stacked-PR convention —
squash to `NativeQueryOngoing`, one squashed commit per slice).

Every slice's exit criterion: its tests pass under both `MongoQueryMode.Native` and `MongoQueryMode.NativeOnly`;
`AssertMql` baselines regenerated (`EF_TEST_REWRITE_BASELINES=1`, scoped `--filter`, then diffed); a
differential-correctness unit test added under `tests/.../UnitTests/Query/NativeTranslation/` per the "MQL
shape can't prove native" rule (assert the native result equals an in-memory LINQ oracle, not just that MQL
was emitted).

## SP1 — Key-selector generalization (11 tests)

**Tests:** `GroupBy_Property_Select_Key_Count`, `GroupBy_Property_Select_Key_with_constant`,
`GroupBy_Property_Select_Sum_Min_Key_Max_Avg`, `GroupBy_aggregate_followed_another_GroupBy_aggregate`,
`GroupBy_anonymous_key_type_mismatch_with_aggregate`, `GroupBy_with_grouping_key_DateTime_Day`,
`GroupBy_param_Select_Sum_Min_Key_Max_Avg`, `GroupBy_param_with_element_selector_Select_Sum{,2,3}`,
`GroupBy_param_with_element_selector_Select_Sum_Min_Key_Max_Avg`.

**Gap:** `NativeGroupByBinder.TryBindGroupKey`'s `switch (keySelector.Body)` only recognizes a bare
`MemberExpression`, an all-member-access `NewExpression`, a zero-arg `new{}`, or a `ConstantExpression`;
everything else hits `default: return false`. Three distinct shapes fall into that default today:

1. **`EF.Property<T>(o, "CustomerID")`** — a `MethodCallExpression`, not a `MemberExpression`.
2. **A computed key part** — `o.OrderDate.Value.Year`/`.Value.Day`, a cast (`(int?)o.OrderDate.Value.Year`) —
   translatable as a *value* but not a plain field (`TryTranslateField` fails; `TryTranslateValue` — already
   used by `TryBindAccumulator` for computed accumulator operands — succeeds).
3. **A captured/closure-parameter key** — `var a = 2; GroupBy(o => a)` — the body is whatever shape
   `NativeQueryParameter.TryGetQueryParameterName` recognizes, not a member of the entity at all. Semantically
   identical to the existing `ConstantExpression` case (every row groups into one bucket) except the `_id`
   value is a query parameter, not a literal — bind via `MongoParameterExpression`, not `MongoConstantExpression`.

**Approach:** add a case (or a fallthrough) that tries, in order: `NativeQueryParameter` recognition →
`translator.TryTranslateField` (covers `EF.Property`, since the translator's own field resolution already
understands it — confirm during implementation) → `translator.TryTranslateValue` (computed). Apply the same
three-way fallback inside the composite-key loop (`case NewExpression { Members: ... }`) per key part, not just
at the top level.

**Caveat carried over from the existing `ConstantExpression` case:** a computed/parameter key part has no
backing `IProperty`, so `HasDefaultKeySerialization` doesn't apply — same reasoning already used for the
literal-constant case's comment ("no property to check for default serialization; a raw literal is inherently
safe to read back"). A computed value is equally safe: it's read back generically, with no value-converter/
`BsonRepresentation` in the picture at all.

**Downstream effect:** `GroupBy_aggregate_followed_another_GroupBy_aggregate`'s failure is *entirely* explained
by its first `GroupBy`'s computed key part (`.OrderDate.Value.Year`); the nested-`GroupBy`-on-`GroupBy`
plumbing (`PriorGrouping`/`SnapshotPriorGroupingForNestedGroupBy`) already exists and needs no new work — this
test is a pure beneficiary of SP1, confirm with a test run rather than re-implementing anything.

## SP2 — HAVING: post-`$group` `$match` (3 tests — corrected from 4; see note)

**Tests:** `GroupBy_filter_count`, `GroupBy_filter_count_OrderBy_count_Select_sum`, `GroupBy_filter_key`.

**Correction (pre-implementation re-check):** `GroupBy_count_filter` was originally bucketed here too, but its
actual shape is `Select(o => new {..., Name = "Order"}).GroupBy(o => o.Name).Select(g => new {..., Count =
g.Count()}).Where(o => o.Count > 0)` — the `Where` filters the ALREADY-FLATTENED Select output (`o.Count`, a
projected alias), not an `IGrouping` HAVING predicate, and `GroupBy`'s own KEY (`o.Name`) must resolve against
the PRIOR anonymous `Select`'s own projected alias (`Name` isn't a real `Order` property) — a capability this
design doesn't have a bucket for yet (closer to Bucket I's "GroupBy over an already-projected source" than to
HAVING). Moved out of SP2's scope; needs its own bucket/slice once that capability exists — do not fold it back
into SP2 without first confirming the OTHER three targets' shapes the same way (all three are directly on
`Set<Order>` with no prior `Select`, confirmed by re-reading `NorthwindGroupByQueryTestBase.cs`).

**Delivered.** `GroupBy_count_filter`'s bucket — a post-group `Where` over a keyed GroupBy's projected Select
alias, resolved by output alias rather than key-part name or entity property — shipped via
`docs/superpowers/plans/2026-09-29-native-groupby-post-group-where-and-push-list.md` (`MongoProjectedAliasScope`;
see Query `AGENTS.md`'s durable-invariants bullet). `GroupBy_count_filter` goes native on all three EF versions,
in both default and `NativeOnly` mode.

**Gap:** `TryBindGroupProjection` explicitly declines whenever `select.PendingGroupPredicate != null` — a
documented, deliberate EF-449 gap: "this predicate has NO native $match-after-$group mechanism on the
flattening-$project path this method builds." (`GroupBy_filter_key`'s `Where(o => o.Key == "ALFKI")` is a key
comparison rather than an accumulator comparison — needs the predicate-comparison binder to grow a key-access
arm too, not just accumulator comparisons; see SP9's identical need for `All(a => a.Key == ...)`, so land that
arm once and share it.)

**Approach:** a HAVING predicate is a `$match` stage inserted between `$group` and the flattening `$project` —
this is genuinely new plumbing (`PostGroupOps` is emitted *after* the flatten, for a different purpose — ops
composed on an already-finalized grouping's *output* rows, not a pre-flatten filter on group internals — do
not reuse it for this). Add a dedicated slot (e.g. `MongoSelectDefinition.GroupHavingPredicate`) populated from
`PendingGroupPredicate` inside `TryBindGroupProjection` instead of declining, and have `MongoSelectLowerer`
emit a `MongoMatchStage` referencing the accumulator's own output field (post-`$group`, pre-flatten fields are
addressable directly, no `$expr` needed unless the predicate is compound). `GroupBy_filter_count_OrderBy_count_
Select_sum` additionally chains `.OrderBy(o => o.Count()).ThenBy(o => o.Key)` after the `Where` — that's the
existing `PendingGroupOrderings` mechanism and should compose without new work once the Where itself is
unblocked.

**Trap to avoid:** `TryBindGroupPredicateComparison` (shared by `TryBindGroupWherePredicate`, feeding
`PendingGroupPredicate`, and by `TryBindGroupTerminalAggregate`'s own direct-predicate arm) currently returns
its bound `MongoGroupAccumulator` as `[NotNullWhen(true)]` — every existing caller assumes a successful bind
always produces an accumulator. Adding the key-access arm breaks that assumption (a key comparison has no
accumulator at all). `TryBindGroupTerminalAggregate`'s own consumption currently gates entirely on
`accumulator != null` before ever setting its `$match` predicate — if that gate isn't ALSO updated in the same
change to key off `comparisonNode != null` instead, a bare-`GroupBy(key).Where(g => g.Key == X).Any()`-shaped
query would flip from a safe DECLINE (today, since the key-access arm doesn't exist) to silently BINDING
successfully while dropping the filter entirely (a correctness regression, not just a missed capability) — the
exact class of bug the SP1 final review caught. Fix both call sites in the same commit; do not add the
key-access arm without touching `TryBindGroupTerminalAggregate`'s consumption logic too, even though SP2's own
target tests never reach that path.

## SP3 — Conditional/filtered accumulators (4 tests)

**Tests:** `GroupBy_group_Distinct_Select_Distinct_aggregate`, `GroupBy_group_Where_Select_Distinct_aggregate`,
`GroupBy_constant_with_where_on_grouping_with_aggregate_operators`, `GroupBy_multiple_Count_with_predicate`.

**Gap:** `TryBindAccumulator`/`TryBindDistinctAccumulator` require the aggregate's source to be exactly `g`
(via `IsGroupingSource`) or exactly `g.Select(selector).Distinct()`. Two related but distinct shapes are
missing:

1. **`g.Count(predicate)`** (`GroupBy_multiple_Count_with_predicate` needs *two*, with different predicates,
   in the same Select) — a predicated `Count` reduces to `$sum: {$cond: [pred, 1, 0]}`.
2. **`g.Where(pred).Min/Max/Sum/Average(...)`** (`GroupBy_constant_with_where_on_grouping_with_aggregate_
   operators`) — the aggregate's source is a `Where` over `g`, not `g` directly; the operand itself must be
   `$cond`-wrapped (or the accumulator skipped for non-matching rows via a sentinel, matching `Min`/`Max`/
   `Sum`/`Average`'s respective identity/skip value).
3. **An extra `Where`/`Distinct` hop before `.Select(...).Distinct().Op()`** (the two `_group_..._Select_
   Distinct_aggregate` tests: `g.Distinct().Select(...).Distinct().Max()` and `g.Where(...).Select(...)
   .Distinct().Max()`) — loosen `TryBindDistinctAccumulator`'s source-chain check to walk through one leading
   `Where`/`Distinct` call before the required `Select(...).Distinct()`, translating the leading `Where`'s
   predicate into the same `$cond` mechanism as (2).

**Approach:** introduce one shared "conditional accumulator" builder used by all three shapes — given a
predicate (possibly none) and an underlying accumulator shape, produce the `$cond`-wrapped `$group`
accumulator expression. `GroupBy_constant_with_where_on_grouping_with_aggregate_operators`'s predicate
(`i => 1 == g.Key`) is a comparison against the *group's own key* — translate `g.Key` inside the predicate via
the same key-ref resolution `TryGetKeyMemberPath` already provides (it's always true within this test's
degenerate single-group case, but the translation must be general, not special-cased to that fact).

## SP4 — Computed Select projection over key/accumulators (2 tests)

**Tests:** `GroupBy_aggregate_projecting_conditional_expression_based_on_group_key`,
`GroupBy_orderby_projection_with_coalesce_operation`.

**Gap:** a Select-projection member's value must currently be *exactly* a key access (`TryGetKeyMemberPath`) or
*exactly* an accumulator call (`TryBindAccumulator`) — never a computed expression combining one with a
constant: `g.Key == null ? "is null" : "is not null"` and `x.Key ?? "Unknown"`.

**Approach:** generalize the per-member binding in `TryBindGroupProjection`'s flatten loop so a member value can
be an arbitrary translatable expression tree whose *leaves* resolve to a key-ref or an accumulator-ref — i.e.
recursively translate the body, treating any `g.Key`/`g.Key.Sub` sub-expression as a
`MongoElementRefExpression("_id"[.Sub])` and any recognized accumulator call as its own flattened
`MongoElementRefExpression`, then run the ordinary aggregation-expression translator (ternary/coalesce/
arithmetic) over the resulting tree. This is conceptually the reducer piece SP3's `$cond` accumulators also
need (an aggregation-expression tree with key/accumulator leaves) — check for shared plumbing between SP3 and
SP4 during implementation; they may collapse into fewer new types than the count suggests.

## SP5 — Outer scalar reduce over a per-group projection (2 tests)

**Tests:** `MinMax_after_GroupBy_aggregate`, `LongCount_after_GroupBy_aggregate`.

**Gap:** `TryBindGroupTerminalAggregate` only handles a **bare** `GroupBy(key)` result with no intervening
`Select` (its own doc comment is explicit about this). These two wrap `.GroupBy(key).Select(g => scalar)` in an
*outer* `Min()`/`Max()`/`LongCount()` — a second, key-less reduction over the first `$group`'s already-flattened
per-group scalar.

**Approach:** when `NativeCardinalityBinder`'s post-terminal guard sees a scalar aggregate whose source is an
already-finalized `Grouping` (non-bare, has its own Select/flatten) rather than a bare one, emit a second,
key-less `$group` stage (`_id: null`) reducing the flattened output field — mirrors the shape
`TryBindDistinctTerminalAggregate` already uses for a bare-scalar-projected `Distinct().Max()` (a degenerate
one-key-part group reduced further), but the source here is a *real* multi-row grouping, not a distinct-derived
one.

## SP6 — Post-group pipeline composition (2 tests)

**Tests:** `Union_simple_groupby`, `GroupBy_skip_0_take_0_aggregate`.

**Gap (a):** `Union(...).GroupBy(...)` — whatever currently prevents `Grouping` from attaching once
`SetOperation` is set needs identifying (check `MongoSelectDefinition`'s mutual-exclusion guards and
`MongoSelectLowerer`'s stage-ordering code) and relaxing: emit `$group` after the `$unionWith` stage.

**Gap (b):** `Skip(0).Take(0)` composed **between** `GroupBy` and `Select` (i.e. before `Grouping` is
finalized) is currently unrecognized entirely — there is no `PendingGroupPaging` analog to the existing
`PendingGroupOrderings` mechanism `NativeSlotPopulator`'s pre-Select carve-out already uses for `OrderBy`/
`ThenBy` composed in the same position. Add one: stash `Skip`/`Take` the same way, consume it in
`TryBindGroupProjection` to emit `$skip`/`$limit` between `$group` and the flattening `$project`. Separately,
confirm `MongoPipelineFactory`'s "`$limit > 0`" validation (which throws `ArgumentOutOfRangeException` rather
than declining) gets the same zero-`Take` short-circuit-to-empty-result handling the non-GroupBy paging path
already has (`Query/AGENTS.md`'s "non-positive paging" pitfall) — `Take(0)` must produce an empty result set,
not a build-time crash.

## SP7 — DTO keys & empty-key readback (3 tests)

**Tests:** `GroupBy_Dto_as_key_Select_Sum`, `Odata_groupby_empty_key`, `GroupBy_empty_key_Aggregate_Key`.

**Gap (a):** `GroupBy(o => new NominalType { CustomerID = ..., EmployeeID = ... })` and
`GroupBy(e => new NoGroupByWrapper())` (OData's empty-key wrapper type) are `MemberInitExpression`, not
`NewExpression` — `TryBindGroupKey`'s switch has no case for it. Add one that walks `.Bindings` (each an
`Assignment` to a member) the same way the `NewExpression` case walks `.Arguments`/`.Members`.
`Odata_groupby_empty_key`'s wrapper has zero bindings — treat identically to the existing zero-arg `new{}`
case (single group). Its Select also constructs a *nested* object initializer
(`new NoGroupByAggregationWrapper { Container = new LastInChain { ... } }`) — confirm `TryBindGroupProjection`'s
flatten (`TryGetProjectionMembers`) already handles nested `MemberInitExpression` result shapes, or extend it
alongside the key-side fix.

**Gap (b):** `GroupBy_empty_key_Aggregate_Key`'s key is the *already-supported* zero-arg `new{}`
(`GroupBy(o => new { })`), but its Select does a bare `g.Key` read — `TryGetKeyMemberPath` deliberately returns
`path = null` for `keyParts.Count == 0` (comment: "Only a zero-part (empty new{}) key has no single field to
read it back from"), which the flatten loop then declines on (`if (keyPath == null) return false`). But `_id`
*is* directly readable here — it's the degenerate empty document `{}`, which is exactly the value an empty
anonymous-type key should read back as. Likely a small, targeted change: return `path = "_id"` for the
zero-part case instead of `null` (double check this doesn't collide with the *composite, `allowWholeKeyRead:
false*` case, which must keep declining for a different reason — read the surrounding comment carefully before
touching the shared conditional).

## SP8 — Group elements materialized as a list (1 test)

**Delivered** via `docs/superpowers/plans/2026-09-29-native-groupby-post-group-where-and-push-list.md`: a new
`$push` accumulator (`NativeGroupByBinder.TryBindPushAccumulator`), a null-safe wrap for nullable/reference
element types (missing and null both read back as `null`), and a scalar-element allowlist that declines entity
and owned-type elements. `GroupBy_selecting_grouping_key_list` goes native on all three EF versions, in both
default and `NativeOnly` mode. See Query `AGENTS.md`'s durable-invariants bullet.

**Test:** `GroupBy_selecting_grouping_key_list` — `g.Select(e => e.CustomerID).ToList()`.

**Gap:** no `$push` (non-dedup array) accumulator kind exists — only `$addToSet` (dedup, via
`TryBindDistinctAccumulator`) followed by a scalar reduce (`MongoArrayReduceExpression`/`MongoSizeExpression`).
This test needs the **raw array itself** as the projected member value, not a reduction of it.

**Approach:** new accumulator op (`$push`) parallel to the existing `$addToSet`, and a flatten read that
resolves to the array field directly (no reduce). The DOM shaper needs to materialize a `List<T>`-typed
group-projection member from a BSON array field — check whether the existing array-handling in the ordinary
(non-GroupBy) projection shaper (`MongoProjectionBindingRemovingExpressionVisitor`/array projection tests
referenced in `Query/AGENTS.md`'s "Mixed-projection alias agreement" invariant) already has machinery to lean
on, or whether this needs new shaper code — likely the largest single unit of new work in this design relative
to its one-test payoff; confirm scope before starting.

## SP9 — GroupBy over a projected source + key-only terminal predicate (1 test, lowest priority)

**Test:** `Select_GroupBy_All` — `.Select(o => new ProjectedType{...}).GroupBy(a => a.Customer).All(a => a.Key
== "ALFKI")`.

**Gap:** two compounding issues: (1) the `GroupBy`'s source is an already-*projected* (DTO) sequence, not the
root entity — the key selector must resolve `a.Customer` against the projection's own output schema, likely via
the same `DistinctAliasScope`/`PriorGrouping`-style resolution used elsewhere in this file for a `GroupBy`
nested on a prior stage's output, not the entity directly; (2) the terminal `All`'s predicate
(`a.Key == "ALFKI"`) is a **key** comparison, not an accumulator comparison — `TryBindGroupPredicateComparison`
only recognizes the latter. Land the key-comparison arm once (SP2 needs the identical capability for
`GroupBy_filter_key`'s `Where(o => o.Key == "ALFKI")`) and share it here.

**Recommendation:** given the one-test payoff and two compounding, narrow gaps, treat this as a candidate to
drop from scope entirely if SP1–SP8 already deliver the bulk of the reduction — revisit after measuring the
post-SP8 `NativeOnly` failure count.

## Safety invariants carried through every slice

- **Native == DriverLinq.** Every slice ships with a differential-correctness unit test against an in-memory
  LINQ oracle, not just an `AssertMql` baseline — MQL shape proves nothing about correctness or nativeness.
- **A recognizer must not mutate then decline** (`Query/AGENTS.md`) — every new binder path stages into locals
  and commits only once every gate has passed, matching the existing commit-block discipline in
  `TryBindGroupProjection`.
- **`HasDefaultKeySerialization`/generic-`_id`-readback caveats** apply to every new key-part shape in SP1 and
  SP7 exactly as they already do to the existing member/constant cases — a value-converted or non-default-
  represented property must still decline (fall back), not silently read back the wrong value.
- Re-run the full `NorthwindGroupByQueryMongoTest` class under `MONGODB_EF_NATIVE_ONLY=1` after each slice to
  confirm its target count moved and nothing else regressed — per the branch-review-coverage lesson, a green
  suite alone does not prove a fix's actual reach; recount the `NativeOnly` failures from the log, don't assume.
