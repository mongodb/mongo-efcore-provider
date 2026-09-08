# Native `Where` over a single-level join's Inner scope — design

**Ticket:** EF-322 (native LINQ pipeline). Motivated by
`NorthwindAsNoTrackingQueryMongoTest.Applied_after_navigation_expansion`, which today falls back to
driver-LINQ under `MongoQueryMode.Native` and throws `NativeTranslationNotSupportedException` under
`NativeOnly`.

## Problem

`Applied_after_navigation_expansion` is:

```csharp
ss.Set<Order>().Where(o => o.Customer.City != "London").AsNoTracking()
```

EF Core's nav-expansion lowers the reference-navigation predicate onto a `LeftJoin` before the provider ever
sees it — informally `Orders.LeftJoin(Customers, ..., (o, c) => new TI(Outer: o, Inner: c)).Where(ti =>
ti.Inner.City != "London").Select(ti => ti.Outer)`. Confirmed live
(`MONGODB_EF_NATIVE_ONLY=1`, single-test filter) that this throws
`NativeTranslationNotSupportedException` — a full fallback, not a partial one.

Tracing why (`NativeTranslation/NativeSlotPopulator.cs`'s `Where` arm):

- The arm is deliberately **Outer-side-only**: `NativeJoinScopeTranslator.ReferencesInnerScope` blocks any
  predicate touching `ti.Inner` from becoming a `$match`, because `PipelineOps` (`$match`) lower *before* the
  `$lookup`/`$unwind` that materializes the join's Inner side — a naive `$match` on Inner there would filter
  on a field that doesn't exist yet.
- The **only** carved-out exception is `NativeJoinScopeTranslator.TryMatchInnerNullCheck` (added by the
  reference-Include null-check feature, EF-322): it recognizes exactly `ti.Inner == null` / `!= null`,
  defers the `$match` into a new `PostJoinOps` list (lowered immediately after the `$lookup`/`$unwind`), and
  leaves the join's actual confirmation (`AddLookup`/`MarkReferenceIncludeConfirmed`) to the trailing
  `Select(ti => ti.Outer)` that EF's nav-expansion always synthesizes right after a join — see that arm's own
  remarks (`NativeSlotPopulator.cs:175-196`) for why confirming there, not in `Where`, avoids double-counting
  `MongoSelectDefinition`'s confirmed/candidate join counters.
- A general Inner-scoped comparison — `ti.Inner.City != "London"` — matches neither the Outer-only arm nor
  the null-check arm, so it falls straight through to `MarkNotNativelyRepresentable()`.
- This is a known, intentional gap, not an oversight: `IsSingleEligibleNativeJoinScope`'s doc comment
  (`MongoQueryableMethodTranslatingExpressionVisitor.cs:780-793`) calls out exactly this shape —
  `Join(…).Where(x => x.Inner.Foo == …).Select(x => x.Inner)` — as declining on purpose, backed by a
  regression test (`NorthwindJoinQueryMongoTest.GroupJoin_Where`) proving the driver-LINQ fallback returns
  the *correct* rows for it today. The 2026-09-07 chained-join-scope design explicitly kept "a
  `Where`/`OrderBy` predicate that reaches any join's Inner side" **out of scope** for the same reason.
  Widening this safely — not just removing the guard — is what this design covers.

## Why the existing null-check arm is safe to generalize

`NativeJoinScopeTranslator.TryTranslatePredicate` (the ordinary two-scope predicate translator used by the
Outer-only arm, guarded there by `!ReferencesInnerScope`) is **already general-purpose**: its own doc comment
states "mixed Outer/Inner predicates translate fine structurally", and it resolves an Inner-scoped member
through `MongoJoinScope.Levels[0].InnerPrefix` — the same alias the eventual `$lookup`'s `As` will produce,
whether that's the driver-native `_inner` shape or a flat `_lookup_<Navigation>` field. Nothing about
*translating* an Inner-referencing predicate is unsafe or unbuilt; the gap is purely that `Where`'s slot arm
never calls it for anything but the null-check shape, and never routes the result to `PostJoinOps`.

Missing/null-safety (`_inner.City` when no matching Customer exists, i.e. a left-outer join whose `$unwind`
preserved the row) is likewise already handled generically: the provider's `$ne`/`$eq` partitioning rule
(`Query/AGENTS.md`'s negator invariant — "$eq/$ne may be inverted because they partition every BSON value
including missing/null") already gives `missing != "London"` the same `true` that C# null-propagation gives
`null.City != "London"`. This is exercised today by `NativeJoinScopeTranslatorTests.Translates_mixed_scope_equality_predicate`
for the translation step in isolation; nothing new is needed there.

So the fix is **routing**, not translation: teach the `Where` slot arm to (a) recognize a general
Inner-referencing predicate over an eligible single-level join scope, (b) translate it with the existing
`TryTranslatePredicate`, and (c) defer it into `PostJoinOps` exactly the way the null-check arm already does
— without touching join confirmation, which stays owned by the trailing `Select`.

## Scope

**In:**

- A `Where` predicate — pure-Inner or mixed Outer/Inner, any comparison `MongoExpressionTranslator` can
  already build — composed directly over a single-level (`JoinScope.Levels.Count == 1`) reference navigation
  join (`Navigation.IsCollection == false`), whatever `Where`'s own eligibility already guarantees
  (`JoinScope` only exists when `TranslateJoinCore` already verified navigation resolution,
  `JoinLookupImplementsKeySelectors`, non-`GroupBy`/`Distinct`-sourced, and not left-outer-over-collection —
  see `MongoQueryableMethodTranslatingExpressionVisitor.cs:2285-2308`).
- Deferring that `$match` (and anything recorded after it — a trailing reducer's `$limit`, in particular)
  into `PostJoinOps`, mirroring the null-check arm exactly.
- Generalizing the `MongoSelectDefinition` flag/doc-comments the null-check arm currently owns
  (`ReferenceIncludeNullCheckConfirmed`) so both call sites share one mechanism, since they do the identical
  thing (flip `ActiveOps`) for the identical reason.

**Out (unchanged, not silently widened):**

- Chained (`Levels.Count > 1`) join scopes — `Where`/`OrderBy` stay root-scope-only there, per the existing
  2026-09-07 design; this ticket only touches the `Levels.Count == 1` arm.
- Collection navigations (`Navigation.IsCollection == true`) — same conservative restriction the null-check
  arm already applies, for the same reason (a 1:N `$unwind`'s post-join filtering semantics are a different,
  unexamined shape).
- Widening `Where`'s eligibility conjuncts themselves — inherited as-is from `JoinScope`'s own construction.
- `OrderBy`/`ThenBy` over Inner — not requested by any known failing shape; `ReferencesInnerScope` there is
  untouched.

## Design

### Component 1 — generalize the `PostJoinOps` confirmation flag

`MongoSelectDefinition._referenceIncludeNullCheckConfirmed` / `ReferenceIncludeNullCheckConfirmed` /
`MarkReferenceIncludeNullCheckConfirmed()` currently name the null-check shape specifically, even though their
only job is "flip `ActiveOps` to `PostJoinOps` from this point on" — a routing decision, not a null-check
fact. Rename (internal-only, no public surface, not a break per the repo's own rubric) to describe the
general trigger:

```csharp
private bool _joinInnerAccessConfirmedFromWhere;

internal bool JoinInnerAccessConfirmedFromWhere => _joinInnerAccessConfirmedFromWhere;

internal void MarkJoinInnerAccessConfirmedFromWhere() => _joinInnerAccessConfirmedFromWhere = true;
```

`ActiveOps` and `PostJoinOps`'s own doc comments are updated to say "a `Where` predicate referencing a
join's Inner side (a null check, or a general comparison) confirmed this select's join without a confirming
Select reaching it" instead of naming only the null check. No behavioral change to either existing call site
— the null-check arm calls the renamed method instead of the old one.

### Component 2 — new `Where` slot arm for a general Inner-referencing predicate

In `NativeSlotPopulator.PopulateNativeSlots`'s `Where` handling, add a new arm immediately after the existing
null-check arm (ordering matters: the null-check recognizer is narrower and must get first refusal, since
`TryTranslatePredicate` cannot represent a bare "compare the whole joined entity to null" and will correctly
decline that shape, but trying the general arm first would just waste a translation attempt on every null
check — try the cheap, specific recognizer first):

```csharp
else if (mongoQ.Select.JoinScope is { Levels.Count: 1 } innerScope
         && mongoQ.Joins.Count == 1
         && mongoQ.Joins[0].Lookup is { Navigation.IsCollection: false }
         && NativeJoinScopeTranslator.TryTranslatePredicate(
             innerScope, predicate.Parameters[0], predicate.Body, out var innerPredicateNode))
{
    // Flip BEFORE AddPredicateConjunct — mirrors the null-check arm immediately above.
    mongoQ.Select.MarkJoinInnerAccessConfirmedFromWhere();
    mongoQ.Select.AddPredicateConjunct(innerPredicateNode);
}
```

No new eligibility re-check beyond what `JoinScope`'s mere existence already guarantees (see Scope, above) —
this mirrors the null-check arm's own reasoning (`Joins.Count == 1` makes `Joins[0]` safe to read directly,
since `JoinScope` is only ever recorded for the first/only join at this depth).

**Deliberately does NOT call `AddLookup`/`MarkReferenceIncludeConfirmed`/`MarkJoinLookupConfirmed`** — for
the exact reason the null-check arm's own comment gives: EF's nav-expansion always inserts a mandatory
`Select(ti => ti.Outer)` immediately after the join's result, and that unwrap's existing arm
(`IsTransparentIdentifierMemberAccessSelector` + `IsSingleEligibleNativeJoinScope`, in `TranslateSelect`) is
what actually confirms/registers the join, whether or not a `Where` preceded it. Confirming here too would
increment `MongoSelectDefinition`'s confirmed-join counter a second time against the single candidate
`MarkSawCandidateReferenceIncludeJoin` recorded for this join, permanently tripping
`HasUnconfirmedCandidateJoin` (`_candidateReferenceIncludeJoins != _confirmedReferenceIncludes`) and forcing
`Fallback` — the exact double-count hazard the null-check arm's comment documents from an earlier attempt.

### Component 3 — no lowerer change

`MongoSelectLowerer.Lower` already emits `PostJoinOps` unconditionally right after the `$lookup`/`$unwind`
block (`MongoSelectLowerer.cs:101-106`), driven purely by reading the list — it does not branch on *why*
`PostJoinOps` is non-empty. A general predicate landing there needs no lowerer change.

### Data flow

```
LeftJoin  → TranslateJoinCore: AddJoin, resolve navigation/$lookup; join eligible (non-collection,
              non-grouped/distinct source, keys implement) → JoinScope = 1-level chain (unchanged)
Where     → NativeSlotPopulator: ReferencesInnerScope-guarded Outer-only arm declines (references Inner);
              TryMatchInnerNullCheck declines (not a null comparison); NEW arm: JoinScope.Levels.Count==1,
              Joins.Count==1, non-collection navigation, TryTranslatePredicate succeeds (resolves
              `ti.Inner.City` via Levels[0].InnerPrefix) → MarkJoinInnerAccessConfirmedFromWhere flips
              ActiveOps to PostJoinOps → AddPredicateConjunct lands the $match there
[pending Select(ti => ti.Outer)] → TranslateSelect: IsTransparentIdentifierMemberAccessSelector +
              IsSingleEligibleNativeJoinScope (unaffected by the Where arm — checks JoinScope/Joins state,
              not the confirmation flag) → AddLookup, MarkReferenceIncludeConfirmed (exactly once),
              MarkJoinLookupConfirmed
Lower     → PipelineOps (empty here) → $lookup/$unwind → PostJoinOps ($match on the joined field) →
              whole-entity shaper
```

Route computation: `_candidateReferenceIncludeJoins == 1` (recorded once, at the `LeftJoin` slot arm) equals
`_confirmedReferenceIncludes == 1` (recorded once, by the trailing `Select`) → `HasUnconfirmedCandidateJoin`
false → `Route` native (no `HasUnsupportedOperator` set anywhere on this path).

## Durable invariants this design must not violate

- **A gate must call the same structural predicate a companion rewrite relies on.** The new arm re-derives
  nothing about join eligibility — it only reads `JoinScope`/`Joins`, both already gated by
  `TranslateJoinCore`'s existing conjuncts.
- **Negator/missing-value partitioning** (`Query/AGENTS.md`) — unchanged; `TryTranslatePredicate` reuses the
  existing `MongoExpressionTranslator`/negator, not new comparison logic.
- **Confirmation stays deferred to the Select arm** — this design explicitly avoids introducing a second
  confirmation call site, per Component 2's rationale.
- **MQL shape is not contract** — the emitted pipeline changes (from the fallback's
  `$project`/`$lookup`/`$unwind`/`$project`/`$match` shape to native `$lookup`/`$unwind`/`$match`), so
  `NorthwindAsNoTrackingQueryMongoTest.Applied_after_navigation_expansion`'s `AssertMql` baseline is expected
  to be rebaselined (`EF_TEST_REWRITE_BASELINES=1`), not preserved.

## Verification plan

1. Pin current behavior: confirm (already done) that `Applied_after_navigation_expansion` throws
   `NativeTranslationNotSupportedException` under `MONGODB_EF_NATIVE_ONLY=1`.
2. Implement Components 1-2.
3. Re-run the same test under `MONGODB_EF_NATIVE_ONLY=1` — expect it to pass (native).
4. Rebaseline `AssertMql` for the test under default `Native` mode
   (`EF_TEST_REWRITE_BASELINES=1`, scoped `--filter`), then rebuild and confirm green without the var.
5. Run the full `Query` functional + specification suites (EF10 at minimum) to check for regressions,
   including a `MONGODB_EF_NATIVE_ONLY=1` pass over the same suites to look for any newly-exposed decline
   (the null-check feature's own commit measured this the same way).
