# Native join-scope whole-entity leaf: unwrap `IncludeExpression` — Design

**Ticket:** EF-322, Phase 2 Group B (root cause A).

## Problem

`NativeJoinScopeProjectionBinder.TryBindProjection` recognizes a whole-entity leaf in a wrapped
`new {...}`/`MemberInit` `Select` composed over a native join scope (`ti.Outer`/`ti.Inner`, at any depth in a
chain) via `MongoTransparentScopeResolver.TryResolveScopeDepth`. That resolver only matches a BARE member-access
chain (a pure run of `"Outer"` hops, optionally ending in one `"Inner"`).

When the SAME scope member is also the target of an `Include(...)`/`ThenInclude(...)`, EF Core's nav-expansion
hands the leaf to this binder as an `Microsoft.EntityFrameworkCore.Query.IncludeExpression` wrapping that same
`ti.Outer`/`ti.Inner` access, not the bare access itself. `TryResolveScopeDepth` never unwraps this node, so the
leaf fails whole-entity recognition, falls through to the ordinary scalar/computed-leaf arm, and fails there too
(`NativeJoinScopeTranslator.TryTranslateValue` has no `IncludeExpression` case) — declining the WHOLE projection.
Confirmed via two isolated spikes (built, run, reverted — not committed):

1. `Orders.Include(o => o.Customer).Select(o => new { o, o.CustomerID })` — Include on the OUTER/root side of
   the join a reference Include itself synthesizes (Customer). Fails identically to Northwind's
   `Include_reference_when_entity_in_projection`.
2. `Owners.LeftJoin(Orders.Include(o => o.OrderLines), owner => owner.Id, order => order.OwnerId, (owner, order)
   => new { owner, order })` — Include on the INNER side of an explicit `LeftJoin`, targeting a nav (`OrderLines`)
   UNRELATED to the join's own key. Fails identically to Northwind's
   `Outer_identifier_correctly_determined_when_doing_include_on_right_side_of_left_join`.

Removing the `Include` from either reproduction makes the identical wrapped whole-entity projection succeed under
`MongoQueryMode.NativeOnly` today — isolating the gap to the `IncludeExpression` wrapper itself, independent of
which side of the join carries it and independent of whether the Include's nav is the join's own nav or an
unrelated one.

## Root cause, precisely

`TryBindProjection`'s member loop (`NativeJoinScopeProjectionBinder.cs`, inside `foreach (var (alias, leafBody) in
members)`) calls:

```csharp
if (MongoTransparentScopeResolver.TryResolveScopeDepth(
        leafBody, rootParam, hopNames: ["Outer", "Inner"], sourceCount: scope.Levels.Count, out var scopeIndex))
```

`leafBody` here is the raw member value from `selector.Body.TryGetProjectionMembers(...)`. For a plain
`new { o }` (no Include), `leafBody` is exactly `ti.Outer` (a `MemberExpression`). For `new { o }` where `o` is
also the target of `.Include(...)`, `leafBody` is `IncludeExpression { EntityExpression: ti.Outer, Navigation:
<the included nav> }` (possibly nested — a `ThenInclude` chain nests another `IncludeExpression` inside
`NavigationExpression`, per `IncludeExpression`'s own shape, matching `TryWalkIncludeChain`'s existing walk in
`MongoQueryableMethodTranslatingExpressionVisitor.cs`). `TryResolveScopeDepth` has no `IncludeExpression` case, so
it declines outright for this leaf; there is no separate, wrong resolution to worry about — just a false
negative.

## Why the materialization side needs no change

The concern going in was that fixing recognition alone might not be enough — the join's Include-fold step
(`MongoQueryableMethodTranslatingExpressionVisitor.TranslateSelect`, the `wrappedLeafJoin` arm) does:

```csharp
var foldedJoinBody = ReplacingExpressionVisitor.Replace(
    selector.Parameters.Single(), source.ShaperExpression, selector.Body);
return source.UpdateShaperExpression(
    BuildSelectManyResultShaper(mongoQueryExpression, selector.Body, foldedJoinBody));
```

`ReplacingExpressionVisitor` substitutes the join's already-built shaper expression for the root parameter
throughout the WHOLE selector body, including inside an `IncludeExpression`'s `EntityExpression` — EF Core's own
`IncludeExpression` supports the standard `VisitChildren` walk, so this substitution already works correctly
today with no code change: `foldedJoinBody`'s corresponding leaf is `IncludeExpression { EntityExpression:
<real Outer/Inner shaper>, Navigation: ... }`, exactly the same shape `Include` produces in every OTHER
(non-join-scope) native/fallback path. `BuildSelectManyResultShaper` treats whatever `foldedJoinBody` hands it
for a given member as that member's shaper subtree verbatim — it does not need to know the leaf is
Include-wrapped to bind it by index correctly, and the Include's OWN `$lookup` (for a nav that is not already the
join's own nav — e.g. `OrderLines` in spike 2) registers later, unconditionally, during native
shaper/projection-binding compilation, exactly as documented for the pre-existing bare single-collection-Include
selector arm (`IsSingleLevelCollectionIncludeSelector`) — this plan widens WHERE that already-working mechanism
is reachable from, not what it does once reached.

This is confirmed structurally, not merely assumed: no other code in the wrapped-leaf `TranslateSelect` arm or in
`BuildSelectManyResultShaper` branches on `leafBody`'s exact CLR node type in a way that would reject or
mishandle an `IncludeExpression` — the ONLY consumer that inspects `leafBody`'s shape for a DECISION (native vs.
fallback) is `TryBindProjection` itself. The implementation plan's tasks must still exercise this end-to-end
(unit-level staging AND a functional round-trip reading real Include'd data back), since "structurally shouldn't
need a change" is a hypothesis to verify with a passing test, not a substitute for one.

## Fix

In `TryBindProjection`'s member loop, unwrap a (possibly nested) `IncludeExpression` down to its innermost
`EntityExpression` BEFORE calling `TryResolveScopeDepth`, and pass the unwrapped expression to that call instead
of `leafBody`. Everything downstream of a successful match (which alias gets which scope's projection: `$$ROOT`
for scope 0, `level.InnerPrefix` for scope k) stays keyed on the leaf's own `alias` exactly as today — the
`IncludeExpression` wrapper is never re-attached to anything `TryBindProjection` itself stages; it only needed to
be SEEN THROUGH for the recognition check.

Unwrap shape, precisely (mirrors `TryWalkIncludeChain`'s existing loop over `IncludeExpression.NavigationExpression`
for a bare/mixed reference chain, but here walking the OTHER axis — `EntityExpression` — since this leaf is
already known to be exactly ONE member, not a chain of sibling `Include`s):

```csharp
var unwrapped = leafBody;
while (unwrapped is IncludeExpression include)
{
    unwrapped = include.EntityExpression;
}
```

A `ThenInclude` chain's `NavigationExpression` is NOT walked here (unlike `TryWalkIncludeChain`) — that chain
hangs off the OUTER `IncludeExpression`, not off `EntityExpression`, and this unwrap only needs to reach the bare
`ti.Outer`/`ti.Inner` access at the bottom of the `EntityExpression` spine to satisfy `TryResolveScopeDepth`. A
`ThenInclude`'s own nested `IncludeExpression` on `NavigationExpression` is left completely untouched (never
unwrapped, never inspected) and reaches the shaper fold unchanged, exactly as a bare reference-Include chain's
`ThenInclude`s already do elsewhere.

## Scope boundaries (deliberately NOT covered by this fix)

- **Root Cause B** (`Include_with_complex_projection_does_not_change_ordering_of_projection` — a `.Where(...)`
  filtering on an already-projected COMPUTED alias, unrelated to `Include` or join scope at all) is explicitly
  out of scope for this plan; tracked as a separate follow-up.
- **A nested wrapped leaf whose OWN inner member is Include-wrapped** (`new { Outer = new { X = ti.Outer } }`
  where the inner `X` is Include'd) is not a shape either spike reproduces or Northwind's two target tests
  exercise; the nested-leaf arm (`scope.Levels.Count == 1 && leafBody.TryGetProjectionMembers(...)`) is a
  SEPARATE recognizer from the whole-entity-leaf arm this plan touches and is left unchanged. If the plan's
  self-review or final review finds a concrete motivating case, note it as a ruling rather than silently
  widening scope.
- **A chain (`scope.Levels.Count > 1`) whole-entity leaf that is ALSO Include-wrapped** is structurally the same
  unwrap-then-resolve fix (this plan's change sits before the `scopeIndex == 0` vs `else` branch, which already
  handles both depth-1 and chain depths identically), so it is covered by the same code change, but the two
  spikes above are both depth-1 (`scope.Levels.Count == 1`) — the plan should add at least one depth->1 unit test
  for chain coverage rather than relying on inference alone.

## Testing strategy

- **Unit** (`tests/.../UnitTests/Query/NativeTranslation/NativeJoinScopeProjectionBinderTests.cs` — extend the
  existing file, mirroring its established whole-entity-leaf test pattern): stage-level proof that
  `TryBindProjection` returns `true` and produces the SAME staged `MongoProjection` list for an Include-wrapped
  leaf as for the bare leaf, at BOTH scope 0 (Outer) and scope k>=1 (Inner), and for a depth->1 chain.
- **Functional** (`tests/.../FunctionalTests/Query/NativeJoinTests.cs` — extend the existing file, reusing its
  `Owner`/`Order`/`OrderLine` fixture and `CreateContext`/`Seed` helpers): both spike shapes above, promoted to
  permanent differential-correctness tests (native NativeOnly result vs. an in-memory LINQ oracle, per Query
  AGENTS.md's own guidance), each asserting the Include'd nav actually materializes (not just that the query
  doesn't throw).
- **Spec** (`NorthwindIncludeQueryMongoTest`, `NorthwindStringIncludeQueryMongoTest`,
  `NorthwindIncludeNoTrackingQueryMongoTest`, `NorthwindEFPropertyIncludeQueryMongoTest` — the same 4 sibling
  classes Phase 2 Group A touched): `Include_reference_when_entity_in_projection` and
  `Outer_identifier_correctly_determined_when_doing_include_on_right_side_of_left_join` regenerate their
  `AssertMql` baselines (their CURRENT recorded baselines are stale driver-LINQ-fallback `_outer`/`_inner` shapes
  captured before this fix; they must be re-verified, not assumed correct, exactly as Phase 2 Group A's plan
  treated `Repro9735`'s pre-existing baseline).
- **Regression sweep**: `MONGODB_EF_NATIVE_ONLY=1` full `~Query` filter across EF8/EF9/EF10, using the
  before/after source-revert diff methodology Phase 2 Group A established, since the `~Query` filter sweeps far
  more tests than this fix touches.

## Non-goals

- Root Cause B (separate follow-up, not this plan).
- Any change to how a collection Include's own `$lookup` registers once reached by the shaper — that mechanism
  is reused as-is, not modified.
