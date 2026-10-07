# Native `Where` over a single-level join's Inner scope — Implementation Plan

**Goal:** Make `NorthwindAsNoTrackingQueryMongoTest.Applied_after_navigation_expansion` (and the general shape
it represents — a `Where` predicate reaching through a single reference navigation, e.g. `o.Customer.City !=
"London"`) go native instead of falling back to driver-LINQ.

**Spec:** `docs/superpowers/specs/2026-09-08-native-join-where-inner-scope-design.md`

**Architecture:** Generalize the existing reference-Include null-check mechanism
(`MongoSelectDefinition`'s `PostJoinOps`/confirmation-flip flag, added for `ti.Inner == null`) to any
predicate `NativeJoinScopeTranslator.TryTranslatePredicate` can already translate against a single-level join
scope. Routing only — no new predicate-translation logic.

---

## Task 1 — Pin current behavior

- [x] Confirm `Applied_after_navigation_expansion` throws `NativeTranslationNotSupportedException` under
      `MONGODB_EF_NATIVE_ONLY=1` (done during investigation).

## Task 2 — Generalize the confirmation flag

**File:** `src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoSelectDefinition.cs`

- [x] Rename `_referenceIncludeNullCheckConfirmed` → `_joinInnerAccessConfirmedFromWhere`,
      `ReferenceIncludeNullCheckConfirmed` → `JoinInnerAccessConfirmedFromWhere`,
      `MarkReferenceIncludeNullCheckConfirmed()` → `MarkJoinInnerAccessConfirmedFromWhere()`.
- [x] Update the doc comments on `PostJoinOps`, `ActiveOps`, and the renamed members to describe both
      triggers (null check and general Inner-referencing predicate), not just the null check.
- [x] Update the one existing call site (`NativeSlotPopulator`'s null-check arm) to the new name.

## Task 3 — New `Where` slot arm

**File:** `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeSlotPopulator.cs`

- [x] Immediately after the existing null-check `else if` in the `Where` handling, add the new arm from the
      spec's Component 2: `JoinScope.Levels.Count == 1 && Joins.Count == 1 && Joins[0].Lookup is
      { Navigation.IsCollection: false } && NativeJoinScopeTranslator.TryTranslatePredicate(...)` →
      `MarkJoinInnerAccessConfirmedFromWhere()` then `AddPredicateConjunct(...)`.
- [x] Do not call `AddLookup`/`MarkReferenceIncludeConfirmed`/`MarkJoinLookupConfirmed` from this arm (left to
      the trailing `Select`, per the spec's Component 2 rationale).

## Task 4 — Verify and rebaseline

- [x] Re-run `Applied_after_navigation_expansion` under `MONGODB_EF_NATIVE_ONLY=1` — now passes (native).
- [x] Rebaselined its `AssertMql` (and 24 other spec overrides sharing the same shape) via
      `EF_TEST_REWRITE_BASELINES=1`, rebuilt, confirmed green without the var (4536/4536 spec `Query` tests
      pass).
- [x] Ran the full `Query` functional + specification suites (EF10) — no regressions (functional: 1940/1940
      once container-startup flakiness from a first run was excluded by a clean re-run; spec: 4536/4536).
- [x] Ran the same suites under `MONGODB_EF_NATIVE_ONLY=1` — functional suite unchanged (1940/1940); spec
      suite improved from 910 to 884 failures (exactly the 26 tests this change newly makes native), zero
      new failures.
- [x] Built `Debug EF8`/`EF9`/`EF10` — all clean, 0 errors, no `#if` needed (EF-version-agnostic change).
