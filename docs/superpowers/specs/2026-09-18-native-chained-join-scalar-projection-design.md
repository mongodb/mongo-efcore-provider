# Native chained-join scalar/computed projection leaves — design

**Ticket:** to be filed (working title: native chained-join scalar projection). Motivated by
`NorthwindMiscellaneousQueryMongoTest.Join_Customers_Orders_Orders_Skip_Take_Same_Properties`, which today
falls back to driver-LINQ under `MongoQueryMode.Native` and throws `NativeTranslationNotSupportedException`
under `NativeOnly`.

## Problem

`Join_Customers_Orders_Orders_Skip_Take_Same_Properties` is:

```csharp
(from o in ss.Set<Order>()
 join ca in ss.Set<Customer>() on o.CustomerID equals ca.CustomerID
 join cb in ss.Set<Customer>() on o.CustomerID equals cb.CustomerID
 orderby o.OrderID
 select new
 {
     o.OrderID,
     CustomerIDA = ca.CustomerID,
     CustomerIDB = cb.CustomerID,
     ContactNameA = ca.ContactName,
     ContactNameB = cb.ContactName
 }).Skip(10).Take(5)
```

Confirmed live (`MONGODB_EF_NATIVE_ONLY=1`, single-test filter) that this throws
`NativeTranslationNotSupportedException: Query projects a non-entity result` — a full fallback.

Two independent gaps compound here (traced by reading `NativeJoinScopeProjectionBinder.cs` and
`NativeSlotPopulator.cs` directly, confirmed against the query's actual method-call order):

1. **A trailing `Select` over a 2+-join chain whose leaves are scalar/computed values (not whole-entity
   references) is unconditionally declined.** `NativeJoinScopeProjectionBinder.TryBindProjection`'s
   "ORDINARY (scalar/computed) leaf" arm:
   ```csharp
   if (scope.Levels.Count > 1)
   {
       return false;
   }
   ```
   This is deliberate, existing behavior, not a regression — it is exactly the boundary the
   `2026-09-07-native-chained-join-scope` plan's own "Explicitly out of scope" list drew: *"A trailing
   `Select` over a chain mixing a whole-entity leaf with a scalar or computed leaf... and a bare scalar leaf
   trailing a chain... this ticket does not widen either restriction, only extends the existing
   whole-entity-leaves-only shape across a chain."* **This design is that follow-up.**

2. **`Skip`/`Take` composed *after* a confirming join-scope `Select` are unconditionally declined**,
   independent of (1) — `NativeSlotPopulator`'s "Post-CONFIRMED-JOIN slot-operator guard" (EF-392):
   ```csharp
   if (mongoQ.Select.HasConfirmedJoinLookup && IsSevenSlotOperator(methodDefinition))
   {
       mongoQ.Select.MarkNotNativelyRepresentable();
       return;
   }
   ```
   The target query's `.Skip(10).Take(5)` sit after the projecting `Select`, so this guard fires
   regardless of whether (1) is fixed.

**This design covers gap (1) only.** Gap (2) is a separate, independently-testable restriction with its own
hazard analysis (paging the joined+projected rows vs. paging outer rows before the `$lookup`) and is left for
a follow-up ticket/plan. Consequently, fixing this design's scope alone does **not** flip
`Join_Customers_Orders_Orders_Skip_Take_Same_Properties` itself to native — see Scope/Out, below. It does
flip the identical shape *without* trailing `Skip`/`Take`.

## Why the existing restriction is safe to lift (and why it was drawn this way originally)

`NativeJoinScopeTranslator`'s own flat, depth-1-only entry points (`TryTranslateCore`, backing
`TryTranslateValue`/`TryTranslatePredicate`) resolve a leaf by comparing the root parameter's `Outer`/`Inner`
member CLR **types** against the recorded scope's `OuterEntityType`/`Levels[0].InnerEntityType`. That
type-comparison approach has a documented residual gap once a chain is involved (see
`NativeJoinScopeTranslator.cs`'s own "RESIDUAL GAP" remarks on `TryTranslateCore`): a chained second join
whose own **flat** `TransparentIdentifier<TOuter,TInner>` can coincidentally match the **first** join's
recorded Outer/Inner types, silently resolving a leaf against the wrong join's `$lookup` alias. This is why
the projection binder declines the scalar-leaf case outright for `Levels.Count > 1` today, rather than
attempting `TryTranslateValue` and risking that hazard.

The whole-entity leaf case already sidesteps this hazard by using a **different, safe** mechanism:
`MongoTransparentScopeResolver.TryResolveScopeDepth`, which walks the **actual member-name chain**
(`"Outer"`/`"Inner"` hops) rather than comparing CLR types, and is immune to the type-coincidence gap by
construction (it never looks at a member's declaring CLR type at all — only at hop *names*, resolved by
parameter identity at the root). `NativeJoinScopeTranslator.TryTranslateRootScopeOnly` (added by the chained-
join-scope plan for `Where`/`OrderBy`) already reuses this same resolver, restricted to accepting only scope
index 0.

**This design generalizes that same safe mechanism** (not the hazardous flat one) to resolve a scalar/computed
leaf against **any single** scope index in the chain — reusing
`MongoTransparentScopeResolver.ScopeRerootingVisitor` exactly as `TryTranslateRootScopeOnly` does, just without
restricting the accepted `ResolvedScope` to `0`.

## Scope

**In:**

- A wrapped (`new {...}`/`MemberInit`) `Select` trailing a `Join`/`LeftJoin` chain of depth ≥ 2 (depth 1 is
  unaffected — already supported), where **every leaf is a scalar or computed value rooted at exactly one
  scope in the chain** (the root, or any single join's Inner side) — e.g. `ca.ContactName`, `o.OrderID`,
  `cb.CustomerID * 2`. "Rooted at exactly one scope" means the leaf's own free variables all resolve to the
  *same* scope index once the `Outer`/`Inner` hop chain is peeled off — mirrors exactly what
  `NativeJoinScopeTranslator.TryTranslateValue` already allows *within* a single scope at depth 1 (a plain
  field, or `+ - * / %` arithmetic over one or more fields of that one entity).
- A leaf that is itself a computed expression combining more than one field from the *same* scope (e.g.
  `Combo = ca.CustomerID + ca.ContactName.Length` if such an expression were otherwise translatable) — no new
  restriction beyond what the existing single-scope translator already accepts.
- Mixing a scalar/computed chain leaf with a whole-entity chain leaf in the same projection (e.g.
  `new { ca, cb.ContactName }` over a 2-join chain) — the existing whole-entity-leaf
  sibling-readability guard (`MongoFieldExpression`/`MongoOuterFieldExpression`/`MongoElementRefExpression`
  are the only admissible sibling shapes) already accepts a plain field leaf; this design's leaves produce
  exactly that shape, so no new guard is needed there, only verification.
- Depth-1 behavior is completely unaffected — `Levels.Count == 1` keeps using the existing
  `NativeJoinScopeTranslator.TryTranslateValue` entry point unchanged (which itself supports leaves computed
  from **both** Outer and Inner, at depth 1 only — see Out, below).

**Out (explicitly deferred, not silently unsupported):**

- **A single leaf whose value is computed across *more than one* scope in a chain** (e.g.
  `Mixed = ca.CustomerID + cb.CustomerID`, or anything mixing the root with a join's Inner side in one
  expression). Depth 1 supports this (via the two-scope `MongoExpressionTranslator` constructor, since there
  are only ever two possible scopes there); a chain does not gain it here. `MongoTransparentScopeResolver`
  already reports this shape via `CrossScope`, so it declines cleanly (falls back, throws only under
  `NativeOnly`) rather than silently mistranslating. A future ticket that wants this would need an N-scope-
  aware `MongoExpressionTranslator`, not merely reusing the existing two-scope one — out of scope here.
- **`Skip`/`Take`/`Where`/`OrderBy`/`ThenBy` composed *after* the confirming chain-scalar `Select`.** Blocked
  today by `NativeSlotPopulator`'s unconditional `HasConfirmedJoinLookup` guard (EF-392), which this design
  does not touch. This is the reason `Join_Customers_Orders_Orders_Skip_Take_Same_Properties` itself does not
  flip to native from this design alone — tracked as a separate follow-up.
- **A nested wrapped leaf** (`CustomerIdCopy = new { Id = ca.CustomerID }`) over a chain — already separately
  gated to `Levels.Count == 1` a few lines above the arm this design touches
  (`native-join-scope-nested-projection` ticket); unaffected and unwidened here.
- Widening per-join eligibility conjuncts themselves — unchanged, inherited as-is.

## Design

### Component 1 — Extract the safe single-scope-resolution helper (refactor only, no behavior change)

`NativeJoinScopeTranslator.TryTranslateRootScopeOnly` today inlines: build one synthetic
`ParameterExpression` per scope level (indices `0..Levels.Count`), run
`MongoTransparentScopeResolver.ScopeRerootingVisitor` over the body, reject on `CrossScope` or a resolved
scope other than `0`, reject if any reference to `rootParam` survived rewriting outside the hop chain
(`ReferencesParameterOutsideHopChain`), then translate the rewritten body against `scope.OuterEntityType`.

Extract the "build synthetic params, reroot, validate — but don't yet judge *which* scope is acceptable"
portion into a private helper:

```csharp
// NativeJoinScopeTranslator.cs — new private helper
/// <summary>
/// Rewrites <paramref name="body"/>'s <c>Outer</c>/<c>Inner</c> hop chain (rooted at <paramref
/// name="rootParam"/>) onto one synthetic parameter per <paramref name="scope"/> level, exactly as <see
/// cref="MongoTransparentScopeResolver.ScopeRerootingVisitor"/> does for <c>SelectMany</c>'s own chained
/// scopes. Succeeds only when the WHOLE body resolves to a SINGLE scope index (no <c>CrossScope</c>) and no
/// reference to <paramref name="rootParam"/> survives the rewrite outside that hop chain (the same parity
/// guard <see cref="TryTranslateRootScopeOnly"/> already applied inline). Callers judge whether the returned
/// <paramref name="scopeIndex"/> is one they accept — this helper itself has no opinion on which index is
/// valid, matching <see cref="MongoTransparentScopeResolver.TryResolveScopeDepth"/>'s own division of labor.
/// </summary>
private static bool TryRerootToSingleScope(
    MongoJoinScope scope, ParameterExpression rootParam, Expression body,
    out int scopeIndex, [NotNullWhen(true)] out Expression? rewritten)
{
    scopeIndex = -1;
    rewritten = null;

    // Carries forward TryTranslateRootScopeOnly's own "Final-review fix (M2)" guard: rootParam.Type must
    // actually be a TransparentIdentifier, or a body reached from some other call site whose parameter merely
    // happens to expose members named "Outer"/"Inner" could be mis-walked.
    if (!rootParam.Type.IsTransparentIdentifierType())
    {
        return false;
    }

    var sourceCount = scope.Levels.Count;

    var scopeParams = new ParameterExpression[sourceCount + 1];
    scopeParams[0] = Expression.Parameter(scope.OuterEntityType.ClrType, "rootScope");
    for (var i = 0; i < sourceCount; i++)
    {
        scopeParams[i + 1] = Expression.Parameter(scope.Levels[i].InnerEntityType.ClrType, $"innerScope{i}");
    }

    var visitor = new MongoTransparentScopeResolver.ScopeRerootingVisitor(
        rootParam, hopNames: ["Outer", "Inner"], sourceCount, scopeParams);
    var candidate = visitor.Visit(body);

    if (visitor.CrossScope
        || visitor.ResolvedScope is not { } resolved
        || ReferencesParameterOutsideHopChain(candidate, rootParam))
    {
        return false;
    }

    scopeIndex = resolved;
    rewritten = candidate;
    return true;
}
```

Rewrite `TryTranslateRootScopeOnly` to call this helper and reject anything but `scopeIndex == 0`:

```csharp
public static bool TryTranslateRootScopeOnly(
    MongoJoinScope scope, ParameterExpression rootParam, Expression body, bool valueMode,
    [NotNullWhen(true)] out MongoExpression? result)
{
    result = null;
    if (!TryRerootToSingleScope(scope, rootParam, body, out var scopeIndex, out var rewritten) || scopeIndex != 0)
    {
        return false;
    }

    var translator = new MongoExpressionTranslator(scope.OuterEntityType);
    return valueMode
        ? translator.TryTranslateValue(rewritten, out result)
        : translator.TryTranslate(rewritten, out result);
}
```

This is a pure refactor — every existing `TryTranslateRootScopeOnly` unit test must still pass unchanged
(same accept/decline verdicts, same result shapes), proven before anything new is added (Task 1 below).

### Component 2 — `NativeJoinScopeTranslator.TryTranslateSingleScope`: the new entry point

Add a sibling entry point that accepts **any** single resolved scope index, not just `0`, and builds the
matching translator for whichever scope it resolved to:

```csharp
// NativeJoinScopeTranslator.cs — new public entry point, alongside TryTranslateRootScopeOnly
/// <summary>
/// Resolves a scalar/computed leaf rooted at ANY SINGLE scope in a chained join — the root, or any one join's
/// Inner side — never a leaf that spans more than one scope (see <see cref="TryRerootToSingleScope"/>'s
/// <c>CrossScope</c> rejection). Used by <see cref="NativeJoinScopeProjectionBinder"/>'s ordinary-leaf arm once
/// <c>scope.Levels.Count &gt; 1</c>, in place of the flat, type-comparing depth-1 entry points
/// (<see cref="TryTranslateValue"/>/<see cref="TryTranslatePredicate"/>), whose own documented RESIDUAL GAP is
/// specifically about chains. This method never falls into that gap: resolution is by the ACTUAL member-name
/// hop chain (<see cref="MongoTransparentScopeResolver"/>), never by comparing a scope's recorded CLR type
/// against some OTHER level's type. See
/// docs/superpowers/specs/2026-09-18-native-chained-join-scalar-projection-design.md.
/// </summary>
public static bool TryTranslateSingleScope(
    MongoJoinScope scope, ParameterExpression rootParam, Expression body, bool valueMode,
    [NotNullWhen(true)] out MongoExpression? result)
{
    result = null;
    if (!TryRerootToSingleScope(scope, rootParam, body, out var scopeIndex, out var rewritten))
    {
        return false;
    }

    MongoExpressionTranslator translator;
    if (scopeIndex == 0)
    {
        // Root scope: same single-scope translator TryTranslateRootScopeOnly already uses — unprefixed
        // MongoFieldExpression results.
        translator = new MongoExpressionTranslator(scope.OuterEntityType);
    }
    else
    {
        var level = scope.Levels[scopeIndex - 1];

        // Reuse the EXISTING two-scope MongoExpressionTranslator constructor (built for SelectMany's
        // correlated inner filter, and already reused by depth-1's TryTranslateCore) — but with a THROWAWAY
        // outer parameter that never appears anywhere in `rewritten` (TryRerootToSingleScope already proved
        // the whole body resolved to scope index scopeIndex, never scope 0, so no synthetic root-scope
        // parameter survives into `rewritten` either). MongoExpressionTranslator.TryResolveMember's isOuter
        // check is by REFERENCE IDENTITY (`ReferenceEquals(param, _outerParam)`) — since the throwaway
        // parameter is referenced nowhere, isOuter is always false for every member `rewritten` contains, so
        // every resolution goes through the ordinary entity/prefix branch: scope.Levels[k-1].InnerEntityType,
        // prefixed with scope.Levels[k-1].InnerPrefix. This is exactly the prefixing depth-1's own Inner-side
        // leaves already get, just re-derived for an arbitrary level instead of always level 0.
        var unusedOuterParam = Expression.Parameter(scope.OuterEntityType.ClrType, "unusedOuterScope");
        translator = new MongoExpressionTranslator(
            level.InnerEntityType, unusedOuterParam, scope.OuterEntityType, level.InnerPrefix);
    }

    return valueMode
        ? translator.TryTranslateValue(rewritten, out result)
        : translator.TryTranslate(rewritten, out result);
}
```

**Why the throwaway-outer-param trick is safe, not a hack:** `MongoExpressionTranslator`'s two-scope
constructor was built for a case (`NativeSelectManyBinder`'s correlated inner filter, and depth-1's own
`TryTranslateCore`) where the caller genuinely has two DIFFERENT populated scopes and needs both. Here we
only ever have one real scope (`rewritten` was already proven, by `TryRerootToSingleScope`, to reference
*only* `scopeParams[scopeIndex]`) — the "outer" slot is structurally unreachable, not merely unused by
convention. `TryResolveMember`'s `isOuter` check (`MongoExpressionTranslator.Members.cs:134`) is a bare
`ReferenceEquals`, so a parameter that never appears in the tree can never accidentally satisfy it. This is
the identical trick `NativeSelectManyBinder`-style "single scope via a two-scope constructor" would use if it
needed a lone inner scope with a prefix and no real outer counterpart; it just hasn't needed to before.

**What this does NOT newly support (verify, don't assume):** a leaf that is itself a *multi-hop* dotted path
on the resolved non-root scope (e.g. `ca.SomeOwnedNav.Foo`) still declines, unchanged — `TryResolveMember`'s
fast path only matches a single hop off a parameter; anything deeper falls to
`TryResolveOwnedFieldPath`/`TryBeginOwnedHopWalk`, whose own two-scope-mode guard
(`MongoExpressionTranslator.Members.cs:326-328`) already declines any chain not rooted on the recorded
`_outerParam` — which our throwaway param, by construction, never is. This is *identical* to depth-1's
existing behavior for a multi-hop Inner-side leaf (already declines today), so this design introduces no new
capability there and no new gap.

### Component 3 — `NativeJoinScopeProjectionBinder`: wire in the new entry point

Replace the "ORDINARY (scalar/computed) leaf" arm's outright decline for `Levels.Count > 1`:

```csharp
// current
if (scope.Levels.Count > 1)
{
    return false;
}

if (!NativeJoinScopeTranslator.TryTranslateValue(scope, rootParam, leafBody, out var computedLeaf))
{
    return false;
}
```

with a branch on chain depth, mirroring the `Where`/`OrderBy` arms' existing depth-1-vs-chain structure in
`NativeSlotPopulator`:

```csharp
MongoExpression? computedLeaf;
if (scope.Levels.Count > 1)
{
    if (!NativeJoinScopeTranslator.TryTranslateSingleScope(scope, rootParam, leafBody, valueMode: true, out computedLeaf))
    {
        return false; // one untranslatable/cross-scope leaf declines the whole projection — no partial commit
    }
}
else if (!NativeJoinScopeTranslator.TryTranslateValue(scope, rootParam, leafBody, out computedLeaf))
{
    return false;
}
```

No other change to this method: the staging list (`staged`), alias bookkeeping (`seenAliases`), the
whole-entity-leaf sibling-readability guard, and `ConfirmEntireChain` are all untouched — a chain-scalar leaf
produces a `MongoFieldExpression` (or `MongoBinaryExpression` for a computed one), which the existing
sibling-readability guard already treats correctly (a `MongoFieldExpression` is an admissible sibling of a
whole-entity leaf; a `MongoBinaryExpression` is not, exactly as at depth 1 today — see
`NativeJoinScopeProjectionBinderTests.Declines_a_computed_leaf_beside_a_whole_entity_leaf` for the existing
depth-1 pin of that same rule, which this design does not change).

### Data flow (target query, minus the out-of-scope trailing `Skip`/`Take`)

```
Join #1 (Order ⋈ Customer as ca) → TranslateJoinCore: eligible → JoinScope = 1-level chain
Join #2 (Order ⋈ Customer as cb) → TranslateJoinCore: eligible, join #1 also eligible →
             JoinScope REBUILT as a 2-level chain (unchanged — Component 2 of the prior chained-join-scope
             design; this design adds nothing here)
OrderBy(o.OrderID) → NativeSlotPopulator: JoinScope.Levels.Count > 1 → TryTranslateRootScopeOnly (unchanged
             — o.OrderID is scope index 0, the root) → StartOrReplaceSort
[pending selector] → Select(x => new { OrderID = x.Outer.Outer.OrderID, CustomerIDA = x.Outer.Inner.CustomerID,
             CustomerIDB = x.Inner.CustomerID, ContactNameA = x.Outer.Inner.ContactName,
             ContactNameB = x.Inner.ContactName }):
             TranslateSelect → IsSingleEligibleNativeJoinScope (unchanged: Levels.Count == Joins.Count == 2)
             → NativeJoinScopeProjectionBinder.TryBindProjection → per leaf:
                 - TryResolveScopeDepth(leafBody, ...) fails for every leaf here (none is a BARE Outer/Inner
                   access — every leaf has a further ".OrderID"/".CustomerID"/".ContactName" hop), so none
                   takes the whole-entity-leaf branch
                 - falls through (scope.Levels.Count == 1 nested-leaf check also declines — no further
                   TryGetProjectionMembers on a plain member access) to the ORDINARY leaf arm (Component 3,
                   NEW): TryTranslateSingleScope resolves each leaf to its own scope index (0, 1, 1, 2, 2) and
                   stages a MongoFieldExpression (unprefixed for OrderID; prefixed with Levels[0].InnerPrefix
                   for CustomerIDA/ContactNameA; prefixed with Levels[1].InnerPrefix for
                   CustomerIDB/ContactNameB)
             → on success: no whole-entity leaf staged, so the sibling-readability guard is a no-op; commit
               all 5 projections, ConfirmEntireChain (both $lookups registered, both levels confirmed)
Lower → MongoSelectLowerer: $sort (root-scope-only, safe before any $lookup) emitted first, then both
             $lookup/$unwind pairs, then the $project with all 5 fields.
```

## Durable invariants this design must not violate

(See `Query/AGENTS.md` for the full list; the ones most load-bearing here:)

- **Scope resolution by parameter identity, never member name.** `MongoTransparentScopeResolver` already
  satisfies this; `TryTranslateSingleScope` adds no name-based shortcut.
- **A gate must call the same structural predicate a companion rewrite relies on.** `TryTranslateSingleScope`
  reuses the identical `TryRerootToSingleScope`/`ScopeRerootingVisitor` machinery
  `TryTranslateRootScopeOnly` already uses — no looser, restated copy.
- **The RESIDUAL GAP this design specifically closes for a NEW call site.** `NativeJoinScopeTranslator.cs`'s
  own remarks warn that "the first caller to translate [a non-root scope] WITHOUT the `Where` arm's blanket
  `ReferencesInnerScope` block ... must either add a per-join identity check or re-validate the scope against
  the actual join being bound." `NativeJoinScopeProjectionBinder`'s whole-entity-leaf arm already closed this
  one layer down (by resolving via member-name-chain shape, never CLR type). `TryTranslateSingleScope` closes
  it the same way, for the SAME reason: it never falls through to the flat, type-comparing
  `TryTranslateCore`/`TryTranslateValue` for `Levels.Count > 1`, at all.
