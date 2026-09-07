# Native chained `Join`/`LeftJoin` scope — Where/OrderBy/whole-entity-leaves projection/terminal — design

**Ticket:** to be filed (working title: native chained-join scope). Motivated by
`NorthwindMiscellaneousQueryMongoTest.Multiple_joins_Where_Order_Any`, which today falls back to
driver-LINQ under `MongoQueryMode.Native` and throws `NativeTranslationNotSupportedException` under
`NativeOnly`.

## Problem

`Multiple_joins_Where_Order_Any` is:

```csharp
ss.Set<Customer>()
    .Join(ss.Set<Order>(), c => c.CustomerID, o => o.CustomerID, (cr, or) => new { cr, or })
    .Join(ss.Set<OrderDetail>(), e => e.or.OrderID, od => od.OrderID, (e, od) => new { e.cr, e.or, od })
    .Where(r => r.cr.City == "London")
    .OrderBy(r => r.cr.CustomerID)
    // .Any() appended by AssertAny
```

Confirmed live (`MONGODB_EF_NATIVE_ONLY=1`, single-test filter) that this throws
`NativeTranslationNotSupportedException: Query projects a non-entity result` — a full fallback, not a
partial one.

Two independent gaps compound here, not one:

1. **The existing single-join native-join-scope mechanism caps at exactly one join.**
   `MongoQueryableMethodTranslatingExpressionVisitor.IsSingleEligibleNativeJoinScope` hard-gates on
   `Joins.Count != 1` (`Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs:806`), and
   `NativeJoinScopeTranslator` only recognizes a **flat**, single-hop `TransparentIdentifier(Outer, Inner)`
   shape (its own class remarks: "one hop each — never nested, unlike SelectMany's chained scopes"). For
   two chained `Join` calls the root parameter at the `Where`/`OrderBy` is
   `TransparentIdentifier<TransparentIdentifier<Customer, Order>, OrderDetail>` — a **nested** shape neither
   piece understands. This is the gap `NativeJoinScopeTranslator`'s own "RESIDUAL GAP" comment
   (lines 87-100 of that file) already documents and defers.

2. **There is no confirming operator that handles a MULTI-LEVEL whole-entity chain projection.** EF Core's
   nav-expansion defers a join's result selector as a "pending selector" applied *last* — even a query with
   no explicit trailing `.Select()` at all gets one synthesized, projecting the raw `new{...}` shape the
   join's own result-selector lambdas built
   (confirmed directly by `JoinScopeWhereSlotPopulationTests`'s own class remarks: "EF Core's own pipeline
   always appends a trailing identity Select over the join's raw anonymous-type result even when the user's
   query has no explicit `.Select()` at all"). For `Multiple_joins_Where_Order_Any` that synthesized selector
   is (informally) `x => new { cr = x.Outer.Outer, or = x.Outer.Inner, od = x.Inner }` — a WRAPPED projection
   whose three leaves are *all whole entities*, spanning the root scope and both joins' Inner sides. The
   existing wrapped-projection confirming arm, `NativeJoinScopeProjectionBinder.TryBindProjection`, already
   handles exactly this leaf shape (whole-entity leaves, any mix of Outer/Inner) for a **single** join
   (`NativeJoinTests.Both_whole_entity_leaves_projection_goes_native_under_NativeOnly` pins the depth-1 case)
   — but it is gated by the same `IsSingleEligibleNativeJoinScope`/`Joins.Count != 1` restriction as
   everything else, and its own internal leaf-to-alias resolution
   (`IsTransparentIdentifierOuterOrInnerAccess`-based) only understands the flat, one-hop shape. So this
   query's *actual* confirming operator is the implicit trailing Select, and the gap is that neither the
   eligibility gate nor the projection binder understands a chain — **not** an absence of any confirming
   operator. (An earlier draft of this design mis-diagnosed this as "no confirming operator at all" and
   proposed a root-scope-only `Where`/`OrderBy`-only fix; that is necessary but not sufficient — see Component
   6 below, added after tracing the actual implicit-Select shape through `JoinScopeWhereSlotPopulationTests`.)

3. **`OrderBy`/`ThenBy` never attempt join-scope translation at all**, even for the existing single-join case.
   `NativeSlotPopulator`'s `OrderBy`/`OrderByDescending` arm only tries the plain single-scope
   `MongoExpressionTranslator` (built from `mongoQ.CollectionExpression.EntityType`, i.e. the OUTER root type)
   and `TryTranslateComputedSortKey` — neither understands a `TransparentIdentifier`-shaped key-selector
   parameter. So `Join(...).OrderBy(x => x.Outer.Foo)` declines today regardless of chain depth.

None of this is a regression — it is the documented, deliberate scope boundary from the original join work
(EF-392/EF-444): chained joins and OrderBy-over-a-join-scope were explicitly left out, and the no-`Select`
shape was never exercised because nothing needed it until this ticket.

## Scope

**In:**

- `Join`/`LeftJoin` chains of depth ≥ 1 (so the existing depth-1 shapes keep working unchanged), where every
  join in the chain independently satisfies the *existing* single-join eligibility conjuncts
  (`JoinLookupImplementsKeySelectors`, a resolved navigation, a bare-collection-scan inner, not
  `GroupBy`/`Distinct`-sourced, and — for a left-outer join — a non-collection navigation).
- A `Where`/`OrderBy`/`ThenBy` chain, in any order and any count, whose predicates/key selectors reference
  **only the outermost root scope** (`r.cr.City`, `r.cr.CustomerID` — i.e., a pure `.Outer.Outer...Outer`
  walk down to the root entity, never touching any join's Inner side). This is a deliberate, narrower
  restriction than "any scope" — see Design, `NativeJoinScopeTranslator generalization`.
- A terminal reducer or scalar aggregate (`Any()`, `Count()`, `First()`, etc.) composed directly after the
  chain's `Where`/`OrderBy`/implicit-or-explicit trailing `Select`.
- **The implicit or explicit WRAPPED whole-entity-leaves-only trailing `Select`** a chained join always
  arrives with (Component 6) — every leaf must be a bare whole-entity reference to some scope in the chain
  (root or any join's Inner side), mirroring the existing depth-1 `NativeJoinScopeProjectionBinder` shape
  exactly, just resolved against an N-level chain instead of one level.
- Correct `$lookup`/`$unwind` emission for every join in the chain (already generically handled by
  `MongoSelectLowerer.AppendLookupStages` iterating `MongoQueryExpression.Lookups` — confirmed by reading;
  no lowerer change anticipated for this scope).

**Out (explicitly deferred, not silently unsupported):**

- A trailing `Select` over a chain mixing a whole-entity leaf with a **scalar or computed** leaf (e.g.
  `new { cr, or, Total = od.UnitPrice * od.Quantity }`), and a **bare scalar** leaf trailing a chain
  (`Select(x => x.Inner.Total)`) — both already decline at depth 1 too (see AGENTS.md's EF-444 carve-out and
  `Where_after_join_still_declines_under_NativeOnly_pending_the_Select_side_binder`), and this ticket does not
  widen either restriction, only extends the existing whole-entity-leaves-only shape across a chain.
- A `Where`/`OrderBy` predicate that reaches any join's **Inner** side, chained or not — this ticket does not
  widen the existing single-join Outer-only restriction on `Where` (`NativeJoinScopeTranslator.ReferencesInnerScope`),
  it only extends that SAME restriction across a chain.
- Widening the per-join eligibility conjuncts themselves (query-filtered targets, composite non-PK keys,
  computed key selectors) — unchanged, inherited as-is from the existing single-join mechanism.
- `GroupJoin`'s own array/grouped result shape — unaffected, owned elsewhere (EF-436).

## Design

### Component 1 — `MongoJoinScope` becomes chain-capable

Today `MongoSelectDefinition.JoinScope` is a single nullable `MongoJoinScope` describing exactly one join.
Replace the stored shape with an ordered, immutable chain:

```csharp
// Expressions/MongoJoinScope.cs
internal sealed class MongoJoinScope(
    IEntityType outerEntityType, IReadOnlyList<MongoJoinScopeLevel> levels)
{
    /// <summary>The root entity type at the base of the chain (e.g. Customer).</summary>
    public IEntityType OuterEntityType { get; } = outerEntityType;

    /// <summary>One entry per join, in the order the joins were written, root-most first.</summary>
    public IReadOnlyList<MongoJoinScopeLevel> Levels { get; } = levels;
}

/// <summary>One join level in a (possibly chained) native join scope.</summary>
internal sealed class MongoJoinScopeLevel(IEntityType innerEntityType, string innerPrefix, bool isLeftOuter)
{
    public IEntityType InnerEntityType { get; } = innerEntityType;
    public string InnerPrefix { get; } = innerPrefix;
    public bool IsLeftOuter { get; } = isLeftOuter;
}
```

`Levels.Count == 1` is exactly today's shape — existing single-join callers (the bare/wrapped `Select` arms,
`NativeJoinScopeProjectionBinder`) are updated to read `scope.Levels[0]` where they used to read the flat
`scope.InnerEntityType`/`scope.InnerPrefix`/`scope.IsLeftOuter` directly. This is a mechanical rename at 2-3
call sites (`IsSingleEligibleNativeJoinScope`, `NativeJoinScopeProjectionBinder`), not a behavior change for
depth 1.

### Component 2 — eager chain METADATA at join-registration time; confirmation stays deferred to Select

Depth-1 today already separates two things that are easy to conflate: (a) the `JoinScope` **metadata** —
which entity types/aliases a `Where`/`Select` *could* resolve against — is built EAGERLY, unconditionally, at
`TranslateJoinCore` time (`MongoQueryableMethodTranslatingExpressionVisitor.cs:2213-2223`), gated only on this
one join's own eligibility; and (b) **confirmation** — actually registering the `$lookup`
(`AddLookup`)/flipping `Route` away from `Fallback` (`MarkReferenceIncludeConfirmed`/`MarkJoinLookupConfirmed`)
— is deferred until a *consuming* `Select` arm (`IsSingleEligibleNativeJoinScope` plus one of the two
`TranslateSelect` arms) actually succeeds. That separation exists so that a join which is never consumed by a
recognized shape doesn't perturb the driver-LINQ fallback's own document shape for no benefit (see
`MongoJoinScope`'s own remarks, and the pinned test `Recording_join_scope_does_not_change_driver_LINQ_fallback_MQL`).
This ticket keeps that separation and generalizes only the metadata side to a chain.

Replace the existing eligibility check + single assignment at lines 2213-2223 (gated on
`outerQueryExpression.Select.JoinScope == null`, so only the FIRST join could ever record one) with: compute
THIS join's own eligibility unconditionally (drop the `JoinScope == null` restriction on the eligibility
computation itself — store the verdict on `JoinInfo` as a new `IsNativelyEligible` bool, set once per join,
using exactly today's four conjuncts: a resolved navigation, not a left-outer collection nav,
`JoinLookupImplementsKeySelectors`, and neither side `GroupBy`/`Distinct`-sourced); then, if every join
recorded so far on `outerQueryExpression.Joins` (this one included) is `IsNativelyEligible`, (re)build
`outerQueryExpression.Select.JoinScope` as a `MongoJoinScope` chain covering all of them — re-derived from
`Joins` each time a join is added (not incrementally patched), matching how
`RebindInnerShaperToOuterQuery`'s own transitive-hop resolution just above this block already re-walks
`Joins` by position. If any join so far is ineligible, `JoinScope` is left as whatever it was (`null`, or a
shorter chain covering only a leading eligible run — see the note below) — no confirmation call is made here,
same as depth-1 today.

Nothing here calls `MarkReferenceIncludeConfirmed`/`MarkJoinLookupConfirmed`/`AddLookup` beyond what
`TranslateJoinCore`'s existing (unrelated) `isSecondOrLaterJoin` fallback-shape block already does — those
stay exactly where they are today, at the Select-side confirming arms, generalized in Component 6 to accept
a chain instead of exactly one join.

**Note on partial eligibility:** if joins 1 and 3 in a three-join chain are eligible but join 2 is not, this
component does not attempt a "chain with a hole" — `JoinScope` simply never gets (re)built past the first
ineligible join (the `Joins.All(...)` check fails the moment any prior join is ineligible), so the whole
chain is treated as unconfirmable, exactly like a single ineligible join today. No partial/split-chain
scope is in scope for this ticket.

### Component 3 — `NativeJoinScopeTranslator` generalized via `MongoTransparentScopeResolver`

`NativeSelectManyBinder` already solved chained-scope member resolution generically:
`MongoTransparentScopeResolver.TryResolveScopeDepth`/`ScopeRerootingVisitor`
(`NativeTranslation/MongoTransparentScopeResolver.cs`) peel a chain of `hopNames[0]` ("Outer") hops down to
the root parameter, for an arbitrary `sourceCount`, returning `scopeIndex == 0` for the root and `scopeIndex
== k` for the k-th level's own Inner element. This is exactly the shape a chained join's result selector
produces (`TransparentIdentifier<TransparentIdentifier<Customer, Order>, OrderDetail>` is structurally
identical to `SelectMany`'s own chained-scope shape), so reuse it rather than extending
`NativeJoinScopeTranslator`'s bespoke flat-shape check.

For **this ticket's scope** (`Where`/`OrderBy` may only touch scope index 0, never any Inner), the resolver
is used narrowly:

```csharp
// NativeTranslation/NativeJoinScopeTranslator.cs — TryTranslateCore, replacing the flat-shape check
internal static bool TryTranslateRootScopeOnly(
    MongoJoinScope scope, ParameterExpression rootParam, Expression body, bool valueMode,
    [NotNullWhen(true)] out MongoExpression? result)
{
    result = null;
    var detector = new MongoTransparentScopeResolver.ScopeRerootingVisitor(
        rootParam, hopNames: ["Outer", "Inner"], sourceCount: scope.Levels.Count,
        scopeParams: BuildScopeParams(scope, rootParam));
    var rewritten = detector.Visit(body);

    // Every scope-rooted access in this body must resolve to index 0 (the root), and at least one must
    // have been found — mirrors ScopeSplittingVisitor's SawScopedAccess/SawUnscopedRootAccess pair, but
    // generalized: CrossScope is set the moment more than one distinct index is seen, and a lone resolved
    // index of anything other than 0 is rejected explicitly (ScopeRerootingVisitor itself doesn't know
    // which index is "root" — that's a caller-level policy for this ticket's restricted scope, not a
    // resolver-level one, since a future Select-side widening will want every index, not just 0).
    if (detector.CrossScope || detector.ResolvedScope is not 0)
        return false;

    var rootParamReplacement = Expression.Parameter(scope.OuterEntityType.ClrType, "rootScope");
    var reroot = new RootOnlyRebinder(rewritten, rootParamReplacement); // rewrites the scope-0 synthetic param back to a plain root parameter
    var translator = new MongoExpressionTranslator(scope.OuterEntityType);
    return valueMode
        ? translator.TryTranslateValue(reroot, out result)
        : translator.TryTranslate(reroot, out result);
}
```

(`BuildScopeParams`/`RootOnlyRebinder` are small helpers spelled out in the plan's Task 4 — omitted here for
brevity; the essential point is that `ScopeRerootingVisitor` already does 100% of the walking/rejection work,
so this ticket's addition is thin.)

The existing `TryTranslatePredicate`/`TryTranslateValue`/`ReferencesInnerScope` entry points are kept (used
unchanged by the depth-1 arms), and this new `TryTranslateRootScopeOnly` entry point is what `Where` and the
new `OrderBy`/`ThenBy` arms call once `scope.Levels.Count > 1`. `ReferencesInnerScope`'s existing depth-1
check is reused as-is for `Levels.Count == 1` (no behavior change there); for `Levels.Count > 1` the new
`TryTranslateRootScopeOnly` path replaces it (its own `CrossScope`/`ResolvedScope is not 0` checks are a
strict superset of what `ReferencesInnerScope` checks for one level).

### Component 4 — `NativeSlotPopulator`: generalize `Where`, add `OrderBy`/`ThenBy`

`Where`'s existing join-scope arm (`NativeSlotPopulator.cs:159-163`) calls
`NativeJoinScopeTranslator.ReferencesInnerScope`/`TryTranslatePredicate` directly against the flat
`MongoJoinScope`. Change it to branch on `joinScope.Levels.Count`: `== 1` keeps calling the existing
depth-1 entry points unchanged; `> 1` calls the new `TryTranslateRootScopeOnly` (predicate mode).

`OrderBy`/`OrderByDescending`/`ThenBy`/`ThenByDescending` currently have no join-scope arm at all
(`NativeSlotPopulator.cs:167-189`). Add one, structured identically to `Where`'s: after the existing
`translator.TryTranslateField`/`TryTranslateComputedSortKey` attempts fail, if
`mongoQ.Select.JoinScope is { } joinScope`, try the join-scope value-mode translation (depth-1: existing
`NativeJoinScopeTranslator.TryTranslateValue` guarded by `!ReferencesInnerScope`; depth>1: the new
`TryTranslateRootScopeOnly` in value mode) before falling through to `MarkNotNativelyRepresentable()`.

### Component 5 — terminal reducer/aggregate: no change anticipated, verify only

`NativeCardinalityBinder.TryBindAggregate`/`TryBindReducer` don't special-case join scopes at all — a bare
`Any()`/`Count()` with no selector just needs `Route` to not be `Fallback`, which Components 1-2 already
achieve. `TryBindReducer`'s own `HasConfirmedJoinLookup` gate (guards against a reducer's `$limit` landing
before an unconfirmed join's `$lookup`) is unaffected — it fires the same way whether the confirmed scope
chain has one level or several. No source change expected here; Task 6 in the plan is a verification step,
not an implementation step — if it turns out something *does* need changing, that's new information the
plan's execution should surface, not something to guess at now.

### Component 6 — the Select-side confirming gate and `NativeJoinScopeProjectionBinder`, generalized to a chain

**Gate.** `IsSingleEligibleNativeJoinScope` (`MongoQueryableMethodTranslatingExpressionVisitor.cs:797-816`)
hard-requires `Joins.Count == 1`. Relax that to: `mongoQueryExpression.Select.JoinScope is { } scope &&
scope.Levels.Count == mongoQueryExpression.Joins.Count` (i.e., Component 2 successfully built a chain
covering *every* join on this select — the partial-chain note above means this is false whenever any join
was ineligible) `&& !HasUnsupportedOperator && !HasTerminalOperator && UnwindSource == null`. The per-join
`candidate.Lookup`/left-outer-collection-nav re-check that follows in the existing method becomes a loop over
`mongoQueryExpression.Joins` instead of a single `Joins[0]` read; the paging/reducing conjuncts
(`HasPaging`/`Cardinality`, further down in the same method) are unaffected — they already reason about the
whole select, not about `Joins.Count`.

**Projection binder.** `NativeJoinScopeProjectionBinder.TryBindProjection` (depth-1 today) walks a `new{...}`/`MemberInit` selector's
members, and for each one recognizes a bare whole-entity `Outer`/`Inner` leaf via
`IsTransparentIdentifierOuterOrInnerAccess` (declaring-type-based, not name-based). Generalize the per-leaf
recognition step to use `MongoTransparentScopeResolver.TryResolveScopeDepth` (hop names `["Outer","Inner"]`,
`sourceCount = scope.Levels.Count`) instead of the flat one-hop check: a leaf whose body resolves to
`scopeIndex == 0` is the root entity (`scope.OuterEntityType`, no alias prefix — read straight off the root
document, exactly like today's bare Outer leaf); a leaf resolving to `scopeIndex == k` (`1 <= k <=
Levels.Count`) is `scope.Levels[k-1]`'s Inner entity, staged under `scope.Levels[k-1].InnerPrefix` exactly
like today's bare Inner leaf. A leaf whose body does **not** resolve to exactly one scope index (i.e., a
computed/mixed leaf, or one touching more than one scope) is rejected exactly as today — this ticket's
generalization only widens *which single scope index* a whole-entity leaf may name, not what a leaf may
otherwise contain.

Every level named by at least one leaf must have its own `$lookup` registered (`AddLookup`) — for a chain
this is already guaranteed unconditionally by Component 2's eager confirmation (every join in the chain gets
a scope level, and Component 2 already calls `AddLookup` for every join once the whole chain is eligible), so
`TryBindProjection`'s existing per-leaf `AddLookup` call becomes redundant-but-harmless for the chain case
(`AddLookup` already dedupes by alias) and stays exactly as-is for the depth-1 case where confirmation is
still deferred to this method.

This is the actual confirming operator for `Multiple_joins_Where_Order_Any` — the query's implicit trailing
Select is a three-leaf, all-whole-entity, two-level-chain projection (`cr` at scope 0, `or` at scope 1, `od`
at scope 2), which is precisely the shape this generalization admits.

### Data flow

```
Join #1  → TranslateJoinCore: AddJoin, resolve navigation/$lookup for level 1; this join eligible? →
             JoinScope = 1-level chain (unchanged from today's depth-1 behavior)
Join #2  → TranslateJoinCore: AddJoin, resolve navigation/$lookup for level 2; isSecondOrLaterJoin block
             (unchanged): AddLookup for both, for the FALLBACK document shape only; separately (Component 2,
             NEW): this join ALSO eligible, and join #1 already was → JoinScope REBUILT as a 2-level chain
             (metadata only — no confirm/AddLookup/Route change here)
Where    → NativeSlotPopulator: JoinScope.Levels.Count > 1 → TryTranslateRootScopeOnly (predicate mode)
             → AddPredicateConjunct if resolved to scope index 0 (root); MarkNotNativelyRepresentable otherwise
             (JoinScope metadata already available — built eagerly at join time, same as depth-1 today)
OrderBy  → NativeSlotPopulator (NEW arm): same TryTranslateRootScopeOnly, value mode → StartOrReplaceSort
[pending selector] → EF Core nav-expansion applies join #2's deferred result selector LAST, as an implicit
             or explicit Select(x => new { cr = x.Outer.Outer, or = x.Outer.Inner, od = x.Inner }):
             TranslateSelect → IsSingleEligibleNativeJoinScope (Component 6, WIDENED: Joins.Count == 2 ==
             JoinScope.Levels.Count, not == 1) → NativeJoinScopeProjectionBinder.TryBindProjection
             (Component 6, WIDENED: each leaf resolved via MongoTransparentScopeResolver to scope index
             0/1/2 instead of the flat Outer/Inner check) → on success: AddLookup both levels (dedupes with
             the fallback-shape AddLookup above), MarkReferenceIncludeConfirmed() x2, MarkJoinLookupConfirmed()
Any()    → NativeCardinalityBinder.TryBindAggregate: unaffected, Route already ScalarAggregate/Projection
Lower    → MongoSelectLowerer: PipelineOps ($match, $sort) emitted first (unchanged ordering — root-scope-only
             predicates/keys are always safe to lower before any $lookup), then AppendLookupStages walks
             query.Lookups and emits both $lookup/$unwind pairs (already-generic, unchanged), then the
             projection/aggregate terminal.
```

Note the native pipeline's stage **order** differs from the fallback baseline currently committed for
`Multiple_joins_Where_Order_Any` (which emits both `$lookup`/`$unwind` pairs *before* `$match`, since that's
the driver-LINQ bridge's own shape) — the native pipeline emits `$match`/`$sort` *first*, which is both
correct (the predicate/sort only ever touch the root scope, materialized from the start) and more efficient
(filters before joining). Per the repo's own versioning rubric, the exact emitted MQL for a supported query
is not contract, so the existing `AssertMql(...)` baseline is expected to be rebaselined
(`EF_TEST_REWRITE_BASELINES=1`), not preserved.

## Durable invariants this design must not violate

(See `Query/AGENTS.md` for the full list; the ones most load-bearing here:)

- **Scope resolution by parameter identity, never member name** — `MongoTransparentScopeResolver` already
  satisfies this; the new code must not add a name-based shortcut anywhere.
- **A gate must call the same structural predicate a companion rewrite relies on.** The chain-eligibility
  re-walk in Component 2 must reuse `JoinLookupImplementsKeySelectors` etc. exactly as the depth-1 arms do —
  not restate a looser copy.
- **`HasConfirmedJoinLookup`'s post-confirmation slot-operator/reducer guards** (already generic over "is
  *a* join confirmed", not "is exactly one confirmed") apply unchanged to a confirmed chain — no changes
  needed there, but Task 7 in the plan must exercise `Take`/`Skip`/`First` after a confirmed chain to confirm
  the existing guard still fires (it should, since it doesn't inspect `Levels.Count`).
