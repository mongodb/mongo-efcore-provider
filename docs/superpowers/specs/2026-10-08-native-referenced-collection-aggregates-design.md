# Native aggregates over a cross-collection collection (GroupJoin, correlated subqueries, navigations) — Design

**Ticket:** EF-322 (native LINQ pipeline). Origin: `GroupJoin` with an aggregate in its result selector
(`c.GroupJoin(o, ..., (c, g) => new { c.Id, N = g.Count(), T = g.Sum(o => o.Total) })`) fails with "could not be
translated" in every query mode. The probe below shows that is one symptom of a broader gap, so this spec covers the gap.

Items marked **[probed]** were observed on EF10 on 2026-10-08 with a throwaway differential harness (a LINQ-to-Objects
oracle over the same data, run in `NativeOnly`, `Native` and `DriverLinq`; model `Cust`/`Ord`, orders keyed by `CustId`,
a customer with no orders, a dangling FK, a null FK). Items marked **[verified]** were read in code. **[to verify]** items
are what the spike (plan step 1) must settle.

## Summary

* EF Core rewrites `GroupJoin(...)` with an aggregate over the group into a **correlated subquery in a `Select`**:
  `Customers.Select(c => new { c.Id, N = Orders.Where(o => c.Id != null && c.Id == o.CustId).Count() })` **[probed]**
  (captured chain printed). `TranslateGroupJoin` is never reached for these queries. So `GroupJoin` aggregates, explicit
  correlated subqueries, and `c.Orders.<aggregate>` through a collection navigation are **the same problem**:
  an aggregate over a *referenced* (cross-document) collection.
* The provider supports almost none of it today. Of 16 forms written through a navigation (`x.Orders.Count`, `.Sum`,
  `.Min`, `.Max`, `.Average`, `.Any`, `.All`, `Count(pred)`, filtered `Count`, `FirstOrDefault`, `ToList`, two aggregates,
  `Where(Any(pred))`, `OrderBy(Count)`), **only** a bare projected `Count` and `Where(Count > n)` work natively
  **[probed]**. Owned (embedded) collections support all of them.
* **A correctness bug sits in the one path that does work** (Phase 0): the projected-`Count` recognizer binds any
  `Orders.Where(<anything>).Count()` to the `Orders` navigation's foreign key, ignoring the written predicate. A
  `GroupJoin` keyed on `Name == Tag` returns the counts for `Id == CustId`. Silent wrong rows in `Native` and
  `DriverLinq`. It should be fixed first and independently.

## Evidence

### What works and fails today **[probed]**

`G` = written as `GroupJoin` (EF rewrites to a correlated subquery); `N` = written through the collection navigation. "Loud"
= a translation exception, never wrong rows. Results identical for the nav-less model unless noted.

| Shape | With `Cust.Orders` navigation | Without a navigation |
|---|---|---|
| `Count` in `Select` (G01, N01) | **works** (`NativeOnly` declines `GroupJoin` form; fallback answers) | loud |
| `Where(Count() > 1)` (N10, G20, G21) | **works** | loud |
| `Sum`, `Min`, `Max`, `Average` (G02-05, N02-05) | loud | loud |
| `Any()`, `Any(pred)`, `All(pred)` (G06-08, N06-08) | loud | loud |
| `Count(pred)`, filtered `Count`, inner `Where` (G09, G22, N09, N15) | loud | loud |
| `FirstOrDefault` of the group, `ToList` of the group (G12-13, N12-13) | loud | loud |
| Two aggregates in one projection (G15, N14) | loud | loud |
| `Where(Any(pred))`, `OrderBy(Count)` (G18-19, N11, N16) | `NativeOnly` declines; others loud ("Unsupported cross-DbSet query") | same |
| **`GroupJoin` on non-key properties, `Count` (G14)** | **WRONG ROWS** (`A1:3 A2:1 B1:1`, expected `A1:2 A2:0 B1:0`) | loud |

### Phase 0 root cause **[verified]**

`MongoProjectionBindingExpressionVisitor.TryBindProjectedCollectionNavigationCount` accepts
`Queryable.Count(Queryable.Where(DbSet<Target>(), <predicate>))` and calls `ResolveCollectionNavigation`
(`MongoProjectionBindingExpressionVisitor.Lookup.cs:345`). That method returns the navigation whenever exactly **one**
collection navigation targets the entity type (`candidates.Count <= 1`), **without examining the predicate**. It registers
an `InjectAfterRoot` lookup on the navigation's FK. Two more sites trust it:

* `MongoEFToLinqTranslatingExpressionVisitor.TryRewriteCollectionNavigationCount` (~1058) rewrites the `Count(Where(...))`
  to `$size` of that lookup by matching the lookup **by target entity type only**.
* The executed MQL for the `Name == Tag` GroupJoin and the `Id == CustId` GroupJoin is byte-identical.

The native matcher is already strict (`NativeCorrelationMatcher.TryMatchCorrelatedCollection`: FK name must equal the
dependent property, the outer operand must be that FK's principal key, one optional null guard on that same key) and
declined G14 correctly; the bind-side recognizer simply never calls it. This is the same family as the F11 finding
(`docs/superpowers/plans/2026-10-01-native-linq-parity.md`, Review Focus #6), fixed on the native side only.

## Goals and non-goals

**Goals.** (1) No silent wrong rows for a correlated `Where(...).Count()`. (2) Native translation of the scalar aggregates
and quantifiers above over a referenced collection, with or without a collection navigation, in a projection, a predicate
and an ordering. (3) Every non-native consumer either answers correctly or refuses loudly.

**Non-goals (first cut).** `GroupJoin` followed by `SelectMany(DefaultIfEmpty)` (already a `LeftJoin`; unchanged);
composite-key correlations; correlation through an owned navigation or a second hop; a group *element* projection that
materializes entities (`ToList` of entities is the existing collection-Include machinery, listed under slice 4).

## Design

### Phase 0: make the correlation recognizer strict (bug fix, ships first)

Replace `ResolveCollectionNavigation`'s count-based shortcut with the strict match. The bind side holds the outer entity as
a `StructuralTypeShaperExpression` (the lambda parameter already substituted), whereas
`TryMatchCorrelatedCollection` identifies the outer operand by `ParameterExpression` identity, so generalize its outer-operand
test to a predicate (`Func<Expression, bool> isOuterRoot`), with the two existing callers passing a parameter-identity
check and this one passing a shaper-identity check, keeping one definition of "this is the FK correlation". Requirements:

* FK name equals the dependent property; the outer operand resolves, by `IProperty` identity, to the navigation's principal
  key; at most one null guard, on that same key; no other conjunct. Anything else declines the projection (it falls to the
  existing "could not be translated" path: loud).
* `TryRewriteCollectionNavigationCount` (the bridge) must match the lookup that **this exact `Where`** staged, not the first
  lookup of the target type. Carry the staged lookup (or its alias) on the recognizer result instead of re-deriving it by type.
* The two-navigations-to-one-type disambiguation (`byForeignKey`) becomes unreachable once the match is strict; delete it.

Tests (red first): G14 (`Name == Tag`) must not return FK counts; `Orders.Where(o => o.Total > 5).Count()` as a user subquery
must not bind to `c.Orders`; correlation on a non-key outer member (`o.CustId == c.Name`); both operand orders; the two
`Count` forms that work today keep working (N01, N10, G01, G20, G21). Mutation: restore the shortcut, see G14 fail.

### Phase 1: a referenced collection as an array source

The owned-collection machinery already turns `Any`, `All`, `Count(pred)`, `Sum`, `Min`, `Max`, `Average`, `FirstOrDefault` into
aggregation-dialect nodes over an array path (`TryResolveOwnedCollectionPath` in `MongoExpressionTranslator.Members.cs` ->
`MongoSizeExpression`, `MongoFilteredSizeExpression`, `MongoQuantifierExpression`, `MongoArrayReduceExpression`, `NativeGroupByBinder`'s
reduce ops) **[verified]**. A referenced collection differs only in *where the array comes from*: a `$lookup` into
`_lookup_<Nav>` that is **not** unwound, which `NativeCorrelationMatcher.TryBuildReferenceCollectionCountLookup` already stages
(`LookupExpression` with `InjectAfterRoot`, `IsNativeCollectionLookup`) and reads with `MongoSizeExpression("_lookup_<Nav>")`.

So the design is to give the existing owned-collection translators a **second array-source resolver**:

```
ResolveCollectionSource(expression) ->
    Owned(path, elementType)                       // today
  | Referenced(stagedLookup, elementType, extraPredicate?)   // new
```

* A `Referenced` source is produced by the (now strict) correlation matcher from `Queryable.Where(DbSet<T>(), corr [&& rest])`
  or `... .Select(selector)`. It stages the lookup through `TryStageNativeCollectionLookup` (recognizers stage into locals and
  commit only after every gate passes; mutate-then-decline is forbidden: Query/AGENTS.md).
* `rest` (an extra conjunct, as in `GroupJoin(o.Where(..))`, G22, or `Where(Count(pred))`) is applied with `$filter` over the
  array **after** the lookup, never as a `$match` inside the lookup sub-pipeline: aggregation expressions go through the
  placeholder table, so a captured variable is read per execution, whereas `LookupExpression.PipelineStages` is baked BSON
  (see the filtered-join-inner spec). This reuses `MongoFilteredSizeExpression`/`MongoElemMatchExpression` unchanged.
* Element fields are read exactly as owned-element fields are (relative to the element), so serializer and
  value-converter handling (`StoredSerialization`, `StoredOrdering.PreservesClrOrderingForAggregateAndSort`) carries over; an
  aggregate over a converted or non-default-represented property declines, as it does for owned collections.

### Empty-group and null semantics (decide once, test everywhere)

A customer with no orders is the common case, and `$lookup` always yields an array (empty), unlike a missing owned array.

| Operator | Empty group | Rule |
|---|---|---|
| `Count`, `Count(pred)` | `0` | `$size` / `$size($filter)` |
| `Sum` | `0` | `$sum` over an empty array is `0` |
| `Min`/`Max`/`Average` over `T?` | `null` | `$min`/`$max`/`$avg` answer `null` on empty |
| `Min`/`Max`/`Average` over non-nullable `T` | **throws** `InvalidOperationException` (as LINQ does) | read side throws on `null`, following the existing `ThrowsOnNull` read modes in Query/AGENTS.md |
| `Any()` / `Any(pred)` | `false` | `$gt: [$size, 0]` |
| `All(pred)` | `true` | `$size($filter(not pred)) == 0` |
| `FirstOrDefault` | `default` | existing `CorrelatedReducer` lookup (`$match`/`$sort`/`$limit: 1`) is reusable when the predicate has no parameter |

### Correlation without a navigation

When no collection navigation matches (G-series on the nav-less model; also every `GroupJoin` on non-FK keys), the matcher
builds a raw-key lookup the way the explicit-join path does (`BuildRawKeyJoinLookup`): a single `(outer property,
inner property)` pair, scalar, `StoredSerialization.StoredAlike`, otherwise decline. Composite keys decline.

**Null keys differ from `Join` here.** EF's rewrite adds `c.Key != null &&` to the subquery predicate **[probed]**
(captured chain above), so an outer row with a null key must match **nothing**, whereas `$lookup` matches a null/missing
`localField` to null/missing foreign values (the documented, intentional behavior for explicit `Join`; EF's relational join
predicate is null-compensated and so matches nulls). The staged lookup therefore needs a guard when the outer key is
nullable and the predicate carries the null check: the array is replaced by `[]` for a null/missing local key
(`$cond: [{$eq: [{$ifNull: ["$<local>", null]}, null]}, [], "$<alias>"]` before the read), keeping the indexable
`localField`/`foreignField` form. Only the *guarded* form is accepted; an unguarded nullable-key subquery declines rather than
guess which null semantics the author meant.

### Positions

* **Projection** (`Select`): scalar aggregate leaves (slice 1); element leaves `FirstOrDefault`/`ToList` (slice 4).
* **Predicate** (`Where`): `Any(pred)`, `Count(pred) op n`, `Sum(..) op n` over the array, as the existing
  `NativeReferenceCollectionCountPredicateBinder` does for `Count`; it flips `ActiveOps` so the `$match` lands after the lookup
  (`PostJoinOps`). Declines with a join scope, set op, `Grouping` or an unwind, as that binder does.
* **Ordering**: `OrderBy(Count)`/`OrderBy(Sum)` via the computed-sort path (`__sort0`) over the array.

### Non-native consumers (the guard that must ship with the feature)

The driver-LINQ bridge reads the staged array through `Mql.Field` and re-applies `Enumerable.Count`. For each new leaf kind
(`Sum`, `Min`, `Max`, `Average`, `Any`, `All`, filtered `Count`) the bridge either implements the same rewrite
(`TryRewriteCollectionNavigationCount` generalizes to `TryRewriteCollectionNavigationAggregate`, since the driver can run
`Enumerable.Sum` over an array field) or **refuses** (`NativeTranslationNotSupportedException`) when it meets a lookup staged
for an aggregate it cannot rewrite. Slice 1 ships the refusal; the rewrite is slice 5. A bridge that reads the *whole*
unfiltered array (or the wrong lookup) is the failure to avoid (Phase 0 is exactly that bug).

## Slices

| # | Slice | Done when |
|---|---|---|
| 0 | Strict correlation matcher on the bind side and in the bridge (Phase 0) | G14 and the user-subquery cases no longer return FK counts; N01/N10/G01/G20/G21 unchanged; full suite green |
| 1 | Projected `Sum`/`Min`/`Max`/`Average`/`Any`/`All`/`Count(pred)` over a referenced collection **with a navigation**; bridge refuses | N02-N09, N14, N15 native with oracle rows incl. the empty group; `NativeOnly` green |
| 2 | Predicate and ordering positions | N10-N11, N16, G18-G19 |
| 3 | Navigation-less correlation + null-key guard | G01-G09, G14-G15, G20-G22 on the nav-less model; null-outer-key test |
| 4 | Element leaves: `FirstOrDefault`, `ToList` | N12-N13, G12-G13 |
| 5 | Bridge parity (`TryRewriteCollectionNavigationAggregate`) so `Native` fallback and `DriverLinq` answer instead of refusing | slice-1..4 shapes pass in all three modes |
| 6 | Spec pins | flip `NorthwindJoinQueryMongoTest.GroupJoin_aggregate_anonymous_key_selectors*` (`// Fails: GroupJoin shape not translated EF-X016`) and the navigation-count spec pins where the oracle agrees; composite anonymous keys stay declined |

Each slice is TDD (tests red first), three-mode, with raw `BsonDocument` seeds (empty group, dangling FK, null FK, a
non-FK-keyed `GroupJoin`), a parameterized-filter-twice test per new filter shape, EF8/EF9/EF10 runs, and mutation checks
for the gate and the null guard.

## Risks

| Risk | Mitigation |
|---|---|
| Phase 0 tightens a recognizer other queries rely on | The strict matcher is the native one already; full suite on 3 versions; the cases that change are exactly the ones returning wrong rows today |
| Null-key subquery semantics (`GroupJoin` excludes null keys, `Join` does not) are confused | Guarded form only; explicit test pair (null outer key in `GroupJoin` vs `Join`) |
| A new staged array lookup collides with an Include of the same navigation | `IsBareCountSizeSource` and `RenamedToAvoidJoinCollision` exist for exactly this; extend `TryStageNativeCollectionLookup`'s collision rule to the new kinds |
| `Min`/`Max`/`Average` empty-group behavior differs between nullable and non-nullable | Table above; read-mode tests for both |
| `$filter` over a large looked-up array is slower than a filtered sub-pipeline | Acceptable first cut; an optimization (push constant-only conjuncts into the lookup `pipeline`) can follow once measured |
| EF8/EF9 shape differences (`LeftJoin` shim) | Run all three versions per slice; the GroupJoin-to-subquery rewrite is EF's own nav-expansion, but confirm on EF8/EF9 in the spike |

## Spike (plan step 1, no commits)

1. Confirm on EF8 and EF9 that the same `GroupJoin` aggregates are rewritten to the correlated subquery (the probe ran on EF10).
2. Print the captured chain for each G/N shape to fix the exact tree each recognizer must accept (null guard shape, `Select`
   inside `Sum`, `DefaultIfEmpty` variants), and whether `TranslateGroupJoin` is ever reached with a group-using result
   selector (it forwards to `TranslateJoinCore(isLeftOuter: true)`, which substitutes `g` as a single entity: only valid under
   `SelectMany(DefaultIfEmpty)`; add a refusal if it can be reached otherwise).
3. Prototype slice 1 for `Sum` and `Any` over the navigation model to confirm the owned-collection translators accept a
   lookup-alias array path unchanged.

## Status and deviations (2026-10-12)

| # | Slice | Status |
|---|---|---|
| 0 | Strict correlation matcher | Done |
| 1 | Projected aggregates and quantifiers | Done |
| 2 | Predicate and ordering positions | Done |
| 3 | Navigation-less correlation + null-key guard | Done |
| 4 | Element leaves: `FirstOrDefault`, `ToList` | Done (native), with constant `Skip`/`Take` |
| 5 | Bridge parity | Done for aggregates, quantifiers, counts, predicates and orderings; element leaves still refuse |
| 6 | Spec pins | Done for every pin that can flip; the composite anonymous-key `GroupJoin` pins are covered by functional tests (below) |

Where the build differs from the design above:

* **Element filters ride in the lookup, not in a `$filter` after it.** The design said a filter must never be a `$match` inside the lookup
  because `LookupExpression.PipelineStages` is baked BSON. Slice 3 solved that differently: the filter is `LookupExpression.InnerPredicate`,
  translated once and rendered through the placeholder table at pipeline-build time, so a parameter is still read per execution, and the
  array that comes back holds only the matching elements. The ordering and the `FirstOrDefault` limit are typed `ElementSort`/
  `ElementLimit` (plus `ElementSkip`) on the lookup, not `PipelineStages`, which would make it a non-native lookup.
* **Element leaves use the keyed array for navigation and navigation-less correlations alike.** The correlation is the strict
  `TryMatchRawKeyCorrelation`, never the navigation, so `c.Orders.OrderBy(..).ToList()` and the same off a `GroupJoin` group are one code path.
  The materializing shaper has no navigation (a plain list), so a filtered list is never fixed up into the principal's own collection.
* **The bridge reads PLAIN arrays only and stages its own.** Because the binder folds a filter into the lookup it stages, the bridge cannot
  use that array; it stages an unfiltered keyed array for each correlation of the query root and applies the filter as an `Enumerable.Where`
  over it (`StageBridgeOwnedLookups`). A subquery that correlates with a non-root document, and any set-operation operand other than the first,
  are never resolved there (they would read the wrong rows, or fail at the server).
* **A relational comparison of a nullable aggregate is null-guarded in the bridge**, because the aggregation dialect orders null below every
  number and the empty group would otherwise match `Max(..) < n`.

Still open:

* Element leaves (`ToList`/`FirstOrDefault`) in the bridge: they refuse (`IsNativeOnlyArray`). Answering them needs the non-native shaper to
  read a filtered, ordered array, which `TryBindProjectedCollectionNavigation` supports only for a plain `Include`-shaped list.
* A parameterized `Skip`/`Take` (a captured variable) declines: the element stages are constants.
* Each element leaf with a distinct (filter, ordering, paging) stages its own array, even when an aggregate over the same group reads a plain
  one. Deriving them from one array with `$filter`/`$sortArray`/`$slice` would save the extra `$lookup`s.

Composite keys (added after the table above): a `GroupJoin` on an anonymous-type key, and an explicit subquery correlated on several
equalities, are a composite-key keyed array in the native translator and the bridge, with aggregates, filters, ordering, paging and element
leaves (`NativeCorrelationMatcher.TryMatchCorrelationKeys`). EF's rewrite guards every outer part with `!= null`, so a `GroupJoin`'s group is
empty for an outer row with a null part; that is EF's rewritten query and is followed, which differs from LINQ-to-objects on the same
anonymous key. The upstream spec pins `GroupJoin_aggregate_anonymous_key_selectors*` stay, for three reasons that are not the key shape: a CONSTANT key
part (`new { c.CustomerID, c.City } equals new { o.CustomerID, City = "London" }`, `Year = 1996`: a condition on the outer row alone), a
COMPUTED inner key part (`o.OrderDate.Value.Year`), and an aggregate over a computed selector (`Sum(o => o.CustomerID.Length)`; the
aggregate arms take one numeric element property). Each is a separate extension.
