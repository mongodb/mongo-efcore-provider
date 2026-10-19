# Native translation for a bare reference-collection-nav list projected inline

*(JIRA ticket not yet filed — per explicit instruction, filing is deferred until this design is approved.)*

## Problem

`Multi_level_includes_are_applied_with_skip` (and its `_take` sibling) in
`NorthwindIncludeQueryMongoTest` fail under `MongoQueryMode.NativeOnly`
(`MONGODB_EF_NATIVE_ONLY=1`) with:

```
NativeTranslationNotSupportedException: Query projects a non-entity result and
MongoQueryMode.NativeOnly forbids the driver-LINQ fallback.
```

The query:

```csharp
from c in ss.Set<Customer>().Include(e => e.Orders).ThenInclude(e => e.OrderDetails)
where c.CustomerID.StartsWith("A")
orderby c.CustomerID
select new { c.CustomerID, Orders = c.Orders.ToList() }
```

reduced to its essential shape: `Customer.Orders` (a reference, cross-collection
navigation, separately `.Include()`'d with a `ThenInclude(OrderDetails)`) is *also*
materialized as a list inside an anonymous projection. Under default `Native` mode
this silently falls back to driver-LINQ (right answer, wrong path — the checked-in
`AssertMql` for this test looks native-shaped only because Include's own `$lookup`
machinery is shared between both paths). This declines today because nothing in
`NativeProjectionBinder` (the translate-time emit side) recognizes `c.Orders.ToList()`
as a representable projection leaf, so `Select.Route` falls to `NativeRoute.Fallback`
for the whole query — the gate at
`MongoShapedQueryCompilingExpressionVisitor.cs:337` then throws under `NativeOnly`.

## Investigation findings (why the design is smaller than it first looked)

Three empirical findings materially shaped this design — each corrects an assumption
that would otherwise have led to over-building:

1. **The read side already exists and already works, unconditionally, in every
   `MongoQueryMode`.** `MongoProjectionBindingExpressionVisitor.Lookup.cs:51`'s
   `TryBindProjectedCollectionNavigation` already recognizes
   `c.Orders.ToList()`/`.ToArray()`/`.ToHashSet()`, already handles a nested
   `ThenInclude` (`ExtractThenIncludesFromSubquery`), already builds the correct
   `ObjectArrayProjectionExpression`/`CollectionShaperExpression` reading the fixed
   `_lookup_<Nav>` path off the top-level result document, and already registers the
   `LookupExpression` via `AddLookup`. This runs as an ordinary recursive rewrite over
   the selector's `NewExpression` arguments while building the query's
   `ShaperExpression` — **it is not fallback-only code**, and it is *why* the
   currently-committed `AssertMql` for this test already shows the `Orders` `$lookup`
   with its nested `OrderDetails` `$lookup` in the pipeline today, even on the
   declining/fallback path. The only gap is translate-time: `NativeProjectionBinder`
   never told `Select.Route` this member was representable, so the whole query
   declines before this already-correct machinery gets a chance to matter.

2. **The expression shape is NOT what EF-449's reducer feature saw.** Confirmed
   empirically (a throwaway unit test capturing the real tree, then reverted — not
   inferred from precedent, since EF-449's own postmortem shows guessing here has
   burned this codebase before): a bare `c.Orders.ToList()` arrives as
   `Enumerable.ToList(Queryable.Where(EntityQueryRootExpression<Order>, fkPredicate))`;
   with a `ThenInclude` present it arrives as
   `Enumerable.ToList(Queryable.Select(Queryable.Where(EntityQueryRootExpression<Order>, fkPredicate), identityOrIncludeSelector))`.
   No navigation name survives anywhere in either shape — same family of finding as
   EF-449 (the navigation must be resolved from the FK-correlation predicate's shape,
   e.g. via `NativeCorrelationMatcher.TryMatchCorrelatedCollection`, the same helper the
   existing `c.Orders.Count` leaf already uses — not by walking a member-access chain).

3. **No new `$project`-stage plumbing is needed.** `MongoPipelineFactory.RenderProject`
   builds the terminal `$project` stage strictly from `Select.Projection`'s explicit
   alias list (confirmed: no lookup-passthrough mechanism exists there today). But
   retaining a field through `$project` is *already* just "add another ordinary entry
   to the same list" — this is exactly the existing `_id`-owner-key-retention pattern
   (`NativeProjectionBinder.cs:302-335`, added for the owned-array leaf) already uses:
   `projections.Add(new MongoProjection("_id", ...))` is a plain passthrough entry with
   no dedicated mechanism of its own. So this feature's own passthrough is symmetric: add
   one `MongoProjection(LookupExpression.GetLookupAlias(navigation), new
   MongoElementRefExpression(LookupExpression.GetLookupAlias(navigation), elementListType))`
   entry (deduped per navigation, in case two projection leaves reference the same nav).
   `$project`'s output keys are arbitrary strings, so keeping the field under its own
   system name (`_lookup_Orders`), rather than the C# member's chosen alias (`Orders`),
   is exactly what lets the independently-built shaper (finding 1) keep reading the path
   it always reads, regardless of what the user named the projection member.

## Scope

**In scope (this ticket):**
- `nav.ToList()` / `nav.ToArray()` / `nav.ToHashSet()` — bare, no `Where`/`OrderBy` of
  its own — for a reference (non-owned) collection navigation, as a projection leaf
  (wrapped in `new {...}` alongside other leaf kinds, or bare under the existing `_v`
  reserved-alias tier).
- The navigation may *also* carry a separate `.Include()`/`.ThenInclude()` chain
  elsewhere in the same query (this is the failing test's exact shape) — the leaf
  dedupes against that existing `LookupExpression`/registration rather than emitting a
  second `$lookup`. (In practice this dedup is already handled by the existing,
  unconditional bind-side `AddLookup`/alias-keyed registration — the new translate-time
  recognizer doesn't need its own separate dedup logic; see Design §2.)
- Composes freely with other already-native leaf kinds in the same projection
  (whole-root-entity leaf, owned-array leaf, a correlated-reducer/count leaf over a
  *different* nav) — no interaction expected, but confirm empirically (Testing).

**Out of scope (deferred to a follow-up ticket, filed separately once this ships):**
- `nav.Where(predicate).ToList()` / `nav.OrderBy(...).ToList()` — a `Where`/`OrderBy`
  directly on the projected navigation. This is **not just a native gap** —
  `MongoProjectionBindingExpressionVisitor.Lookup.cs`'s `ExtractFilteredIncludePipeline`
  only tolerates the synthetic FK-correlation `Where`; any other predicate throws
  `InvalidOperationException` **in every `MongoQueryMode` today**, and any
  `OrderBy`/`Skip`/`Take` forces `LookupPipelineKind.FallbackOnly` (permanent fallback,
  no native path at all). Fixing this is a materially larger, mostly-separate effort
  (new predicate-to-`$match` translation on the filtered-Include path, a new
  `LookupPipelineKind`, a new `AppendLookupStages` branch) — same reasoning EF-449 used
  splitting off EF-216. **This split was discussed with and confirmed by the ticket
  owner.**
- A further `.Select(...)` on the nav (scalar/DTO-shaped list rather than whole
  entities) — no existing shaper for that shape either; separate, larger feature.
- Two-hop nav chains, `Skip`/`Take` inside the nav chain, owned/embedded collections
  (already covered by the existing owned-array-leaf mechanism, untouched by this
  ticket).

## Design

### 1. Shape recognition must be shared with the bind side, not duplicated

The translate-time (emit) recognizer and the already-existing bind-time reader
(`TryBindProjectedCollectionNavigation`) must agree on **exactly** the same accepted
shape. Recognizing a shape at translate time that the bind side does not actually build
a shaper for is a silent-crash-or-wrong-data hazard (the query would be marked
`Route = Projection`, native, and then have no shaper for that member); recognizing a
narrower shape than the bind side accepts only costs an optimization, never
correctness. Per this codebase's own recurring lesson ("a gate wider than what the
rewrite handles is a silent-wrong-data trap"), the two must not be independently
maintained pattern-matches that can drift.

**Approach:** extract the structural shape-matching already inside
`TryBindProjectedCollectionNavigation` (the `ToList`/`ToArray`/`ToHashSet` →
optional `Select` (`ThenInclude`) → mandatory `Where`-over-`EntityQueryRootExpression`
walk, plus the `NativeCorrelationMatcher.TryMatchCorrelatedCollection`-style navigation
resolution) into a shared, side-effect-free `TryMatch` helper returning the resolved
`INavigation` (and enough shape info to rebuild), called by **both**:
- The new `NativeProjectionBinder` recognizer (emit side, §2) — read-only, no
  registration.
- `TryBindProjectedCollectionNavigation` itself (bind side, unchanged behavior,
  refactored to call the shared matcher instead of inlining the walk).

*(Exact current structure of `TryBindProjectedCollectionNavigation`'s pattern match —
and how cleanly it separates from the registration/shaper-building it currently does
in the same method — needs confirming against the live code during implementation;
the shared-helper extraction may turn out to need a different split than described
here. This is a refactor-for-reuse, not new logic.)*

### 2. Emit side: new `NativeProjectionBinder` recognizer

New arm, alongside `TryGetCorrelatedReducerLeaf`/`TryGetDocumentConstructionLeaf`,
called from `TryTranslateLeaf`. On a shape match (via §1's shared helper) against a
collection, reference (non-owned) navigation:

- Declines (leaf not recognized, ordinary fallback-eligible decline — this family
  *does* have a driver-LINQ oracle, unlike EF-449's reducer, since today's fallback
  already handles this shape) if the matched shape carries its own `Where`/`OrderBy`
  (out of scope, §Scope) — i.e. the recognizer accepts only the bare
  `ToList`/`ToArray`/`ToHashSet` (optionally `ThenInclude`-wrapped) shape, nothing more.
- On acceptance: adds **one** passthrough entry to `Select.Projection` —
  `new MongoProjection(LookupExpression.GetLookupAlias(navigation), new
  MongoElementRefExpression(LookupExpression.GetLookupAlias(navigation),
  elementListType))` — deduped by alias the same way `_id`-retention already dedupes
  (`seenAliases.Add(...)`), so two sibling leaves over the same nav (or this leaf plus
  an existing Include on the same nav) don't double-add.
- Returns "accepted, no `MongoElementRefExpression` for *this* member" — the C# member
  itself (`Orders`) contributes nothing of its own to `Select.Projection`; its read
  comes entirely from the pre-existing bind-side rewrite (finding 1), which — being an
  ordinary recursive `ExpressionVisitor`-style rewrite over the ORIGINAL selector tree,
  not an alias-indexed lookup — already produces the correct `CollectionShaperExpression`
  subtree in exactly the position this member occupies in the rebuilt `NewExpression`,
  independent of anything `Select.Projection` does. No `_projectionMapping` plumbing is
  needed for this member (needs confirming empirically that this rewrite is reached
  unconditionally regardless of the whole-select `Route` — see §4/Residual risks).

### 3. Dedup against a separately-`.Include()`'d navigation

No new dedup logic is needed on the emit side: `AddLookup`'s existing alias-keyed
dedup (already used by every other lookup-registering feature, per the durable
invariants in Query's `AGENTS.md`) already ensures a bare `.ToList()` leaf and a
separate `.Include()` on the same nav land on the *same* `LookupExpression` rather than
each building their own — because both paths resolve to the identical navigation
resolve and the identical alias (`GetLookupAlias`). The new recognizer just needs to
avoid **also** trying to build a competing lookup itself (it doesn't build one at all —
see §2, it only adds a `Select.Projection` passthrough entry once the bind side has
already registered/will register the real `LookupExpression`).

### 4. Read side: no new code, verify the existing rewrite is actually reached

Per finding 1, no new bind-side or lowerer code is expected. What must be verified
during implementation, not assumed:

- That `MongoProjectionBindingExpressionVisitor`'s rewrite of this member (building the
  `ObjectArrayProjectionExpression`/`CollectionShaperExpression` subtree) is reached
  identically regardless of whether the whole select ultimately routes native or
  fallback — the "unconditional" claim in finding 1 needs confirming for the *native*
  path specifically, not just observed as already true for fallback (which is all the
  currently-passing, non-`NativeOnly` test run proves).
- That the **native** `MongoProjectionBindingRemovingExpressionVisitor` (as opposed to
  the mixed/fallback removing visitor, which is the only one that has ever actually
  been exercised on an embedded `ObjectArrayProjectionExpression`/`CollectionShaperExpression`
  subtree until now, since this exact combination — `Route == Projection` *and* an
  embedded collection-Include shaper — has never previously reached the native path)
  correctly rewrites that subtree's field reads against the retained
  `_lookup_Orders` alias. This is the single largest unverified assumption in this
  design; if it does *not* already work, the fix is expected to be narrow (teaching
  that visitor's existing array-handling arm to also accept a reference-collection
  lookup's alias, mirroring however it already reads an *owned* array leaf) but must be
  confirmed, not assumed, before considering this design complete.

## Decline matrix

- **A `Where`/`OrderBy` on the projected nav** — declines (out of scope, split to
  follow-up ticket); falls back exactly as today, no behavior change.
- **Two-hop nav chains, owned/embedded collections, a further `.Select(...)` on the
  nav** — declines, unaffected by this ticket.
- **TPH-derived nav target** — no new decline needed: the existing bind-side machinery
  already handles TPH-narrowed collection Includes today (per Query `AGENTS.md`'s
  existing pitfall note on discriminator narrowing); this ticket doesn't touch that
  path, only adds a translate-time acknowledgment that the shape is representable.

## Testing

- New unit tests (`NativeTranslation/`) covering the recognizer's accept/decline
  matrix: bare `.ToList()`/`.ToArray()`/`.ToHashSet()`, bare + separate `.Include()` on
  the same nav (the failing test's shape), bare + separate `.Include().ThenInclude()`,
  two sibling leaves over the same nav (dedup), a `Where`/`OrderBy`-bearing nav decline,
  a whole-root-entity leaf beside this leaf (composition), an owned-array leaf beside
  this leaf (composition).
- Flip `NorthwindIncludeQueryMongoTest.Multi_level_includes_are_applied_with_skip` /
  `_skip_take` (and their `NorthwindStringIncludeQueryMongoTest`/
  `NorthwindEFPropertyIncludeQueryMongoTest`/`NorthwindIncludeNoTrackingQueryMongoTest`
  overrides, if they cover the same base method) from the current fallback-passing
  state to a `NativeOnly`-mode pass, confirming genuinely native (MQL shape alone
  cannot prove this, per Query `AGENTS.md`'s own stated pitfall).
- `MONGODB_EF_NATIVE_ONLY=1` full spec-suite run before/after: expect these tests
  (and only these, plus any other currently-failing test that happens to share this
  exact bare-list shape) to flip `Failed → Passed`, zero `Passed → Failed`.
- Regression: confirm the existing driver-LINQ fallback path (`MongoQueryMode.DriverLinq`
  explicitly) is byte-for-byte unaffected — this ticket must not change the fallback's
  own behavior, only add a native path alongside it.
- Multi-EF-version: no `#if` expected; confirm during implementation (matches every
  other entry in this file's history).

## Residual risks / open items to confirm empirically before/during implementation

1. Whether `MongoProjectionBindingExpressionVisitor`'s existing rewrite is reached
   identically on the native path (§4) — the single biggest unverified assumption.
2. The exact current structure of `TryBindProjectedCollectionNavigation` may not split
   as cleanly into "match" vs. "register/build" as §1 assumes — the shared-helper
   extraction's shape should be treated as a sketch, not a commitment, until the
   method is read directly during implementation.
3. Whether the native `MongoProjectionBindingRemovingExpressionVisitor` needs any new
   arm at all for reading an embedded array-shaper subtree via a retained lookup alias,
   versus already handling it via existing owned-array-leaf machinery (§4).
