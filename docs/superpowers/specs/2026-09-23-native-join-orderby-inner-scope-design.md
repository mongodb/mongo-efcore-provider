# Native `OrderBy`/`ThenBy` over a single-level join's Inner scope — design

**Ticket:** EF-322 (native LINQ pipeline). Motivated by
`NorthwindMiscellaneousQueryMongoTest.Join_Customers_Orders_Projection_With_String_Concat_Skip_Take`, which
falls back to driver-LINQ under `MongoQueryMode.Native` and throws `NativeTranslationNotSupportedException`
under `NativeOnly`.

## Problem

The query is a navigation-less key-equality join, ordered by the joined (Inner) side:

```csharp
from c in Customers
join o in Orders on c.CustomerID equals o.CustomerID
orderby o.OrderID
select new { Contact = c.ContactName + " " + c.ContactTitle, o.OrderID }
```
`.Skip(10).Take(5)`

Confirmed live (`MONGODB_EF_NATIVE_ONLY=1`, single-test filter): this throws
`NativeTranslationNotSupportedException` — a full fallback. So do its two undecorated siblings in the same
test class, `Join_Customers_Orders_Skip_Take` and `Join_Customers_Orders_Skip_Take_followed_by_constant_projection`
— same `orderby o.OrderID`, same exception. **The string concatenation in the target test's own name is a red
herring**: `NativeJoinScopeTranslator.TryTranslateValue` already translates `c.ContactName + " " +
c.ContactTitle` fine (`MongoExpressionTranslator.TranslateStringConcat`), and the Select-side projection
binder (`NativeJoinScopeProjectionBinder`) never even gets a chance to run — the query is already routed to
`NativeRoute.Fallback` by the time it would.

Tracing why (`NativeTranslation/NativeSlotPopulator.cs`'s `OrderBy`/`ThenBy` arm, `PopulateSortSlot`):

```csharp
else if (mongoQ.Select.JoinScope is { Levels.Count: 1 } singleLevelScope
         && !NativeJoinScopeTranslator.ReferencesInnerScope(keySelector.Parameters[0], keySelector.Body)
         && NativeJoinScopeTranslator.TryTranslateValue(...))
    record(new MongoOrdering(joinSortKey, ascending));
else if (mongoQ.Select.JoinScope is { Levels.Count: > 1 } chainedScope
         && NativeJoinScopeTranslator.TryTranslateRootScopeOnly(...))
    record(new MongoOrdering(chainedSortKey, ascending));
else
    mongoQ.Select.MarkNotNativelyRepresentable();
```

Both join-scope arms are **Outer/root-side-only** — the depth-1 arm explicitly excludes anything referencing
Inner (`!ReferencesInnerScope`); the chained arm's `TryTranslateRootScopeOnly` structurally cannot resolve
Inner at all. `o.OrderID` references the join's Inner side, so neither arm fires, nothing else matches, and
`MarkNotNativelyRepresentable()` routes the whole query to `Fallback`.

This is the sibling gap to the one `2026-09-08-native-join-where-inner-scope-design.md` already closed for
`Where`: that design widened `Where`'s slot arm to accept a general Inner-referencing predicate (deferred
into `PostJoinOps`, confirmed by `MarkJoinInnerAccessConfirmedFromWhere`). `OrderBy`/`ThenBy` never received
the equivalent widening — `ReferencesInnerScope`'s doc comment there is explicit that only the `Where` call
site was restricted "for now".

## Why this is safe to widen the same way

`NativeJoinScopeTranslator.TryTranslateValue` (the ordinary-leaf value translator, already used by the
Outer-only arm and guarded there by `!ReferencesInnerScope`) is the same general-purpose two-scope translator
`Where`'s Inner arm already trusted — it resolves an Inner-scoped member through
`MongoJoinScope.Levels[0].InnerPrefix`, the same alias the `$lookup`'s `As` produces. Nothing about
*translating* an Inner-referencing sort key is unbuilt; the gap is purely that `PopulateSortSlot` never
tries it and never routes the result to `PostJoinOps`.

**A `$sort` needs no cardinality carve-out that `$match` didn't already need.** Reordering the
(post-`$unwind`) rows changes neither their count nor their identity, regardless of whether the join is 1:1
or 1:N — so, unlike the paging (`$skip`/`$limit`) case that
`2026-09-22-native-post-join-paging-design.md` had to reason about row-count preservation for, a sort key
resolving against Inner is safe to defer into `PostJoinOps` for *any* join cardinality. This design still
mirrors `Where`'s narrower, already-reviewed restriction (excluding a genuine collection navigation) rather
than widening further than needed to fix the motivating test — see Scope.

**The motivating join is navigation-less** (`join o in Orders on c.CustomerID equals o.CustomerID`, no model
navigation between `Customer`/`Order` for this key pair) — `JoinInfo.Navigation` is `null`. Per
`2026-09-22-navigationless-join-native-eligibility-design.md`, navigation-less joins are already
natively-eligible (`JoinScope` is already built for this query); the gap is purely in the `OrderBy` slot arm,
not in join eligibility. `Where`'s existing Inner arm gates on `Lookup is { Navigation.IsCollection: false }`,
a property pattern that requires a **non-null** `Navigation` — so it would *also* exclude a navigation-less
join, coincidentally not what today's `Where` regression tests exercise. `LookupExpression` already has the
null-safe form of this exact check: `IsReference => Navigation is not { IsCollection: true }` ("a
navigation-less lookup is always treated as a reference"). The new `OrderBy` arm uses `lookup.IsReference`
directly instead of re-deriving the null-unsafe pattern, so it covers the navigation-less case the target
test needs without changing `Where`'s own (separately-scoped, unchanged) gate.

## Scope

**In:**

- An `OrderBy`/`OrderByDescending`/`ThenBy`/`ThenByDescending` sort key — any expression
  `NativeJoinScopeTranslator.TryTranslateValue` can already build, pure-Inner or mixed Outer/Inner — composed
  directly over a single-level (`JoinScope.Levels.Count == 1`) join whose `Lookup.IsReference` is true (a
  resolved non-collection navigation, **or** no navigation at all — a key-equality join).
- Deferring that sort (and anything recorded after it) into `PostJoinOps`, reusing the exact mechanism
  `Where`'s Inner arm already established (`ActiveOps` flip), generalized to a name that no longer says
  "FromWhere" now that a second call site uses it.

**Out (unchanged, not silently widened):**

- Chained (`Levels.Count > 1`) join scopes — `OrderBy`/`ThenBy` stay root-scope-only there; a chained Inner
  sort key is a separate, unexamined shape (mirroring `TryTranslateRootScopeOnly`'s existing chain
  restriction) and not needed by the motivating test.
- A genuine collection navigation (`Navigation.IsCollection == true`) — same conservative exclusion `Where`'s
  arm already applies, for the same reason (unexamined 1:N post-`$unwind` shape), even though the "why is
  this safe" section above suggests sorting itself wouldn't need it. Kept narrow on purpose: widen it in a
  follow-up with its own regression test if a real query needs it, not speculatively here.
- `Where`'s own gate (`Lookup is { Navigation.IsCollection: false }`) — untouched; only the new `OrderBy` arm
  uses `IsReference`. Reconciling the two to share one helper is a tidiness opportunity, not required by this
  fix, and left alone to keep this change minimal.

## Design

### Component 1 — generalize the `PostJoinOps` confirmation flag's name

`MongoSelectDefinition._joinInnerAccessConfirmedFromWhere` / `JoinInnerAccessConfirmedFromWhere` /
`MarkJoinInnerAccessConfirmedFromWhere()` already describe a routing decision ("flip `ActiveOps` to
`PostJoinOps` from this point on"), not a `Where`-specific fact — the sibling design's own Component 1 said
so. Adding a second (`OrderBy`) call site is the point at which the `FromWhere` suffix stops being accurate,
so this design finishes that rename (internal-only; no public surface; not a break):

```csharp
private bool _joinInnerAccessConfirmed;

internal bool JoinInnerAccessConfirmed => _joinInnerAccessConfirmed;

internal void MarkJoinInnerAccessConfirmed() => _joinInnerAccessConfirmed = true;
```

Updates the two existing `Where`-arm call sites, the one read site
(`MongoQueryableMethodTranslatingExpressionVisitor.cs`'s trailing-Select confirmation check), and doc
comments/test comments that name the old identifier. No behavioral change to either existing call site.

### Component 2 — new `OrderBy`/`ThenBy` slot arm for a general Inner-referencing sort key

In `NativeSlotPopulator.PopulateSortSlot`, add a new arm after the existing single-level Outer-only arm
(ordering matters the same way it does for `Where`: try the narrower/cheaper Outer-only translation first,
since most sort keys *are* Outer-side and the Inner arm's extra gate gets skipped by short-circuit):

```csharp
else if (mongoQ.Select.JoinScope is { Levels.Count: 1 } innerSortScope
         && mongoQ.Joins.Count == 1
         && mongoQ.Joins[0].Lookup is { IsReference: true }
         && NativeJoinScopeTranslator.TryTranslateValue(
             innerSortScope, keySelector.Parameters[0], keySelector.Body, out var innerSortKey))
{
    mongoQ.Select.MarkJoinInnerAccessConfirmed();
    record(new MongoOrdering(innerSortKey, ascending));
}
```

`TryTranslateValue` is the same call the existing Outer-only arm already makes — the *only* difference is
this arm no longer pre-checks `!ReferencesInnerScope`, and confirms Inner access before recording. A pure-
Outer key never reaches this arm (the existing arm above already consumed it), so this purely adds
Inner/mixed coverage.

**Deliberately does NOT call `AddLookup`/`MarkReferenceIncludeConfirmed`/`MarkJoinLookupConfirmed`** — same
division of labor as `Where`'s Inner arm: the trailing `Select` (or, for this family of tests, the
join-scope projection binder) still owns confirming the join itself. Confirming here too would double-count
`MongoSelectDefinition`'s confirmed/candidate join counters.

### Component 3 — no lowerer change

`MongoSelectLowerer` already emits `PostJoinOps` unconditionally right after the `$lookup`/`$unwind` block,
driven purely by reading the list — it does not branch on *why* `PostJoinOps` is non-empty. A sort landing
there needs no lowerer change, exactly as the `Where` design already established.

### Data flow (motivating test)

```
Join      → TranslateJoinCore: no navigation resolved; RebindInnerShaperToOuterQuery still builds
              joinInfo.Lookup from the raw key-selector properties (EF-377); IsNativelyEligible true
              (navigation-less joins already eligible, 2026-09-22 design) → JoinScope = 1-level chain
OrderBy   → NativeSlotPopulator.PopulateSortSlot: plain-field/computed-Outer arm declines (key references
              Inner); NEW arm: JoinScope.Levels.Count==1, Joins.Count==1, Lookup.IsReference (Navigation is
              null ⇒ true), TryTranslateValue resolves o.OrderID via Levels[0].InnerPrefix →
              MarkJoinInnerAccessConfirmed flips ActiveOps to PostJoinOps → StartOrReplaceSort lands the
              $sort there
Select    → NativeJoinScopeProjectionBinder.TryBindProjection: Contact (computed, Outer-only) and OrderID
              (scalar, Inner) each resolve to exactly one scope → both admitted; ConfirmEntireChain confirms
              the join (AddLookup, MarkReferenceIncludeConfirmed) — unaffected by the OrderBy arm, which
              never touches confirmation itself
Skip/Take → PagingOps recorded after ActiveOps already points at PostJoinOps (flipped by OrderBy) → land in
              PostJoinOps too, after the $sort
Lower     → $lookup/$unwind → PostJoinOps ($sort, $skip, $limit, in that order) → final $project
```

Route computation: the join's own candidate/confirmed counters are satisfied by the Select-side binder alone,
exactly as before this change — the `OrderBy` arm only affects *where* the sort op lands, never the
confirmation bookkeeping.

## Durable invariants this design must not violate

- **Confirmation stays owned by the Select-side arm** — this design does not add a second confirmation call
  site, mirroring the `Where` design's own Component 2 rationale.
- **`PipelineOps` vs `PostJoinOps` ordering is load-bearing** — a sort/paging op recorded before Inner access
  is confirmed must never reference the not-yet-materialized Inner alias; this design only ever moves ops
  recorded *after* the Inner-referencing `OrderBy` into `PostJoinOps`, never reorders unrelated ops.
- **MQL shape is not contract** — `Join_Customers_Orders_Skip_Take`,
  `Join_Customers_Orders_Skip_Take_followed_by_constant_projection`, and
  `Join_Customers_Orders_Projection_With_String_Concat_Skip_Take` all move from the driver-LINQ bridge shape
  (`_outer`/`_inner` projected, `$sort`/`$skip`/`$limit`, then final `$project`) to a genuine native
  `$lookup`/`$unwind`/`$sort`/`$skip`/`$limit`/`$project` pipeline — baselines are rebaselined, not preserved.

## Verification plan

1. Pin current behavior (already done): all three target tests throw `NativeTranslationNotSupportedException`
   under `MONGODB_EF_NATIVE_ONLY=1`.
2. Implement Components 1-2.
3. Re-run the same three tests under `MONGODB_EF_NATIVE_ONLY=1` — expect all to pass (native).
4. Rebaseline `AssertMql` for all three under default `Native` mode (`EF_TEST_REWRITE_BASELINES=1`, scoped
   `--filter`), rebuild, confirm green without the var, and manually diff each baseline to confirm it's
   genuinely fallback→native (not a row-count/content change).
5. Add unit coverage mirroring `JoinScopeWhereSlotPopulationTests`: an Inner-scope `OrderBy` over a
   navigation-based reference join goes native; the same over a navigation-less join goes native; the same
   over a collection navigation still declines gracefully (`Route == Fallback`, `PipelineOps` empty).
6. Run the full `Query` functional + specification suites (EF10 at minimum) under both default and
   `NativeOnly` mode to check for regressions/newly-exposed declines, same as the sibling `Where` design's
   own verification step.
