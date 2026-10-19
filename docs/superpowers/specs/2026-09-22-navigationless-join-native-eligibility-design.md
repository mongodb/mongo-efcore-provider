# Navigation-less join native eligibility — design

**Ticket:** EF-322.

## Problem

`NorthwindKeylessEntitiesQueryMongoTest.Entity_mapped_to_view_on_right_side_of_join` falls back to
driver-LINQ today. The query is an ad-hoc key-equality left join with no model navigation between the two
sides:

```csharp
from o in ss.Set<Order>()
join pv in ss.Set<ProductView>() on o.CustomerID equals pv.CategoryName into grouping
from pv in grouping.DefaultIfEmpty()
select new { Order = o, ProductView = pv }
```

`Order` has no navigation to `ProductView` (a keyless view-mapped entity) — the join exists purely because
the LINQ query says so, not because the model does.

Root-caused (see conversation history / `MONGODB_EF_NATIVE_ONLY=1` run): the entire native join-scope
mechanism (`JoinInfo.IsNativelyEligible` → `MongoSelectDefinition.JoinScope` → `NativeJoinScopeProjectionBinder`)
is gated on `JoinInfo.Navigation != null`, in
`MongoQueryableMethodTranslatingExpressionVisitor.TranslateJoinCore`:

```csharp
joinInfo.IsNativelyEligible =
    joinInfo.Navigation is { } eligibleNavigation
    && innerQueryExpression.Select.IsBareCollectionScan
    && !outerQueryExpression.Select.IsGroupBy && !innerQueryExpression.Select.IsGroupBy
    && !outerQueryExpression.Select.IsDistinct && !innerQueryExpression.Select.IsDistinct
    && !(joinInfo.IsLeftOuter && eligibleNavigation.IsCollection)
    && JoinLookupImplementsKeySelectors(joinInfo, outerQueryExpression, outerKeySelector, innerKeySelector);
```

With `Navigation == null`, the whole conjunction short-circuits `false`, so `JoinScope` is never built,
`MongoSelectDefinition.Route` resolves to `NativeRoute.Fallback` (via `HasUnconfirmedCandidateJoin`), and
`MongoShapedQueryCompilingExpressionVisitor.VisitProjectedQuery` throws under `MongoQueryMode.NativeOnly`
("Query projects a non-entity result and MongoQueryMode.NativeOnly forbids the driver-LINQ fallback").

**This is narrower than it looks.** EF-377 (see `NavigationlessJoinChainTests.cs`,
`RebindInnerShaperToOuterQuery`'s `else if (fkPropertyName != null)` branch, lines ~2731-2755 of
`MongoQueryableMethodTranslatingExpressionVisitor.cs`) already builds a correct `joinInfo.Lookup` directly
from the raw key-selector property names when no navigation resolves — `EntityTypeObjectAccessExpression`
instead of `NavigationObjectAccessExpression`, alias `_lookup_<InnerEntityType>` instead of
`_lookup_<Navigation>`. That machinery is exercised for *every* join today, native-eligible or not — EF-377
fixed it because the driver-LINQ fallback bridge's chain-flattening also consumes this bookkeeping. So the
"how do I represent this join as a `$lookup`" problem is already solved; only the "is this join allowed to
flip `Route` away from `Fallback`" gate excludes it.

The only other place `Navigation` is dereferenced in this path is `JoinLookupImplementsKeySelectors`
(`joinInfo.Navigation!.DeclaringEntityType`, a null-forgiving `!` currently safe only because the caller
short-circuits on `Navigation != null` first). That method exists to guard against a **resolved-but-wrong**
navigation — protecting against `Navigation` having been picked by a looser fallback (`GetNavigations()
.FirstOrDefault(n => n.TargetEntityType == innerEntityType)`) that doesn't actually match the written key
selector. For the navigation-less branch there is no equivalent risk: `joinInfo.Lookup`'s `LocalField`/
`ForeignField` were built **directly from the same `outerKeySelector`/`innerKeySelector`** passed to
`JoinLookupImplementsKeySelectors`, so if `RebindInnerShaperToOuterQuery` successfully resolved both
properties (`joinInfo.Lookup != null`), the two are guaranteed to agree — no re-derivation needed.

## Confirmed by spike

A 2-hunk change was tried directly and confirmed correct before writing this plan:

1. In `TranslateJoinCore`'s `IsNativelyEligible` computation, drop the `Navigation is { }` requirement, keep
   the `IsCollection` check conditional on a resolved navigation:
   ```csharp
   joinInfo.IsNativelyEligible =
       innerQueryExpression.Select.IsBareCollectionScan
       && !outerQueryExpression.Select.IsGroupBy && !innerQueryExpression.Select.IsGroupBy
       && !outerQueryExpression.Select.IsDistinct && !innerQueryExpression.Select.IsDistinct
       && !(joinInfo.IsLeftOuter && joinInfo.Navigation is { IsCollection: true })
       && JoinLookupImplementsKeySelectors(joinInfo, outerQueryExpression, outerKeySelector, innerKeySelector);
   ```
2. In `JoinLookupImplementsKeySelectors`, short-circuit `true` when `joinInfo.Navigation == null` (after the
   existing `joinInfo.Lookup is not { } lookup => return false` guard), for the reason above — trust the
   already-built `Lookup` instead of re-deriving an anchor entity type from a null navigation.

Result: `Entity_mapped_to_view_on_right_side_of_join` goes fully native (confirmed under
`MONGODB_EF_NATIVE_ONLY=1` — no exception; the pipeline is a genuine `$lookup`/`$unwind`, not the driver-LINQ
`_outer`/`_inner` bridge shape) and produces correct results (the test's own `base.…` call asserts against
the EF in-memory LINQ oracle, which passed). `NativeJoinScopeProjectionBinder`'s existing whole-entity-leaf
handling (EF-444) required **no changes** — it consumes `MongoJoinScope`/`MongoJoinScopeLevel`, which are
built from `InnerEntityType`/`Alias`/`IsLeftOuter` and never reference `Navigation`.

**Blast radius, also measured by the spike:** re-running the wider join/spec suite with the same 2-hunk
change showed several *other* existing tests flip from the driver-LINQ fallback shape to a native pipeline
too — at minimum `NorthwindJoinQueryMongoTest.Join_same_collection_force_alias_uniquefication` and
`.GroupJoin_customers_employees_shadow`. Both failed only on their **MQL baseline string** (still asserting
the old `_outer`/`_inner` driver-bridge shape) — per `Query/AGENTS.md`, a fallback→native transition with
unchanged results is explicitly *not* a breaking change and MQL shape is never contract. This means the full
`NorthwindJoinQueryMongoTest`/`NorthwindGroupJoinQueryMongoTest`-family baselines need regenerating, not just
the one target test's.

## Scope

**In:**
- Widening `IsNativelyEligible`/`JoinLookupImplementsKeySelectors` as above, for **depth-1** joins (a single
  `Join`/`LeftJoin`/`GroupJoin`+`SelectMany(DefaultIfEmpty)` with no chained second join).
- Extending it to **navigation-less joins inside a chain** (a `Join` composed onto a prior `Join`'s result,
  where either or both hops lack a navigation) — already partially proven by `NavigationlessJoinChainTests`
  at the fallback-bookkeeping level (EF-377); this design additionally makes such chains native-eligible.
  `MongoJoinScope`/`MongoJoinScopeLevel` construction (`outerQueryExpression.Joins.All(j =>
  j.IsNativelyEligible)`) already has no `Navigation` dependency, so this should fall out of the same change,
  but needs its own explicit test — chains are exactly where EF-377's bugs originally hid.
- Regenerating every spec-suite MQL baseline that changes shape as a result (fallback → native), auditing
  each diff to confirm it's genuinely fallback→native and not an accidental behavior change.
- A differential-correctness functional test (native result vs. in-memory LINQ oracle) for the navigation-less
  join family, covering matched/unmatched/left-null rows — the established pattern
  (`NativeOwnedCollectionAllTests` etc.) — since `IsNativelyEligible` is exactly the kind of gate this
  codebase's `AGENTS.md` calls a "silently wrong rows" risk if under-verified.
- Unit-test coverage of `JoinLookupImplementsKeySelectors`'s new `Navigation == null` branch and
  `IsNativelyEligible`'s new "no navigation, still eligible" case, alongside its "navigation resolved but
  `Lookup` build failed (unresolvable property names)" decline case.

**Out (explicitly deferred, not silently unsupported):**
- Widening the underlying `$lookup` eligibility constraints themselves (query-filtered targets, composite
  non-PK keys, computed key selectors) — unrelated to the navigation gate, and already out of scope for the
  existing navigation-based join-scope work.
- `Where`/`OrderBy`/scalar-leaf projection composed after a navigation-less join — those arms
  (`NativeJoinScopeTranslator`, `NativeSlotPopulator`'s join-scope handling) are consumed unchanged by this
  design; if they have their own residual `Navigation` dependency it is a **separate** finding, not fixed
  here (Task 3 below explicitly checks for this and files a follow-up rather than silently expanding scope).
- Any join whose outer or inner source is `GroupBy`-/`Distinct`-sourced — already excluded, unchanged.

## Design

No new types. Two small, targeted edits to existing logic in
`Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs`:

1. `TranslateJoinCore`'s `IsNativelyEligible` assignment: replace the `joinInfo.Navigation is { } eligibleNavigation` requirement with an unconditional pass-through, moving the `IsCollection` exclusion (which only makes sense for a resolved navigation — an ad-hoc join has no "collection navigation" concept at all) onto a null-safe pattern (`joinInfo.Navigation is { IsCollection: true }`).
2. `JoinLookupImplementsKeySelectors`: add a `joinInfo.Navigation == null` early-return-`true` branch immediately after the existing `joinInfo.Lookup is not { } lookup => return false` guard, per the "Confirmed by spike" reasoning above.

### Data flow

```
Join/LeftJoin/GroupJoin+SelectMany(DefaultIfEmpty) call
  → TranslateJoinCore
      → resolve Navigation (existing) — null when no model navigation connects the two sides
      → RebindInnerShaperToOuterQuery (UNCHANGED, EF-377 already handles Navigation == null):
            builds joinInfo.Lookup directly from outerKeySelector/innerKeySelector property names
      → IsNativelyEligible (CHANGED): no longer requires Navigation != null
      → JoinLookupImplementsKeySelectors (CHANGED): trusts joinInfo.Lookup when Navigation == null
      → JoinScope built (UNCHANGED) once every join in Joins is IsNativelyEligible
  → NativeJoinScopeProjectionBinder / NativeJoinScopeTranslator (UNCHANGED): consume JoinScope/
    MongoJoinScopeLevel, never Navigation directly
  → MongoSelectLowerer.AppendLookupStages (UNCHANGED): emits $lookup + $unwind from joinInfo.Lookup
```

### Error handling

Unchanged contract: an ineligible navigation-less join (unresolvable key-selector property names, a
query-filtered/GroupBy/Distinct-sourced side, a left-outer join over what would be a collection navigation —
moot here since there's no navigation) still falls back to the existing, correct driver-LINQ bridge. Never a
hard decline — Join/LeftJoin/GroupJoin already has a working non-native path for every shape.

### Testing

- **`MONGODB_EF_NATIVE_ONLY=1`** against the target spec test and the wider join suite — the only reliable
  "went native" signal (MQL shape alone doesn't prove it).
- **Baseline regeneration** via `EF_TEST_REWRITE_BASELINES=1` for every spec test whose MQL shape flips,
  followed by a **manual diff review** of each (confirm native `_lookup_<X>`/`$unwind` shape, not a
  content/row-count change).
- **Differential-correctness fixture** for the navigation-less join family (matched/unmatched/left-null),
  mirroring `NativeOwnedCollectionAllTests`'s pattern: assert native result equals an in-memory LINQ oracle
  over the same `Expression`.
- **Unit tests** for `JoinLookupImplementsKeySelectors`'s and `IsNativelyEligible`'s new branches.
- **Regression guard:** `NavigationlessJoinChainTests` (functional, currently fallback-only) must stay green
  — and its own doc comment/EF-377 framing should be updated once these chains go native, since "the bare hop
  falls back correctly" stops being the operative behavior for the depth-1 and now possibly chained cases.
