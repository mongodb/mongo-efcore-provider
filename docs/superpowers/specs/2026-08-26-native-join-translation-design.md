# Native `Join`/`LeftJoin` translation — design (EF-392, chunk A)

**Status:** BLOCKED upstream, superseded. Task 1 (a genuinely live refactor) landed and remains. Tasks 2–5's classification/registration scaffolding was initially kept as documented-dormant code, then removed entirely (2026-08-26, post-merge) once the maintainer decided against carrying permanently-unreachable code — see "Blocker found during implementation" below. Tasks 6–8 were revised in place to test the resulting permanent-fallback behavior rather than native execution.
**Ticket:** [EF-392](https://jira.mongodb.org/browse/EF-392) — joins / cross-collection navigation breadth (aggregate breadth ticket; 373 measured declining cases across 5 decline sites, measured on a different, older lineage — see "On the 373 figure" below).

## Blocker found during implementation (2026-08-26)

Task 6's implementer discovered that EF Core's `NavigationExpandingExpressionVisitor` normalizes **every** `Join`/`LeftJoin` call — a genuine user-authored one and an Include-lowering-generated one alike — into the identical `TransparentIdentifier(Outer, Inner)` result-selector shape before `TranslateJoinCore` ever sees it. The "flattened vs. whole-side-capture" distinction this design's Scope section is built on (and the "`TryConfirmReferenceInclude` has already declined it as Include sugar" precondition in the Data flow section below) does not exist at that point — both shapes are indistinguishable there. This is an EF Core limitation, not a bug in this provider, and is expected to be addressed upstream by an eventual EF Core nav-expansion change (untracked as of this writing — no upstream issue number).

**Decision (ruled by the human maintainer, not a provider-side workaround):** do not attempt to differentiate the two shapes ourselves — no candidate/confirm handshake, no downstream re-classification. Genuine two-sided `Join`/`LeftJoin` translation stays on the existing, already-correct driver-LINQ fallback path indefinitely, until/unless EF Core's nav-expansion changes. Task 4's `TransparentIdentifier`-shape decline in `MongoJoinBinder.TryBindJoin` is therefore not a stopgap to fix later — given the two shapes are provably indistinguishable, declining on that shape is the **correct permanent behavior**, at the acceptable cost of also declining every genuine join (which was already falling back to driver-LINQ successfully before this design existed, so nothing regresses).

**Consequence:** Tasks 2 (`MongoJoinScope`), 3 (`MongoJoinBinder`'s classification/registration body), and 5 (`MongoSelectLowerer`'s new lowering arm) were provably unreachable — nothing could ever reach the classification loop past the Task 4 guard. This was initially kept in the tree as documented-dormant scaffolding, on the theory that it might become reachable again if EF Core's nav-expansion behavior changes upstream. **The maintainer subsequently reversed that decision (2026-08-26) and had it removed** — `MongoJoinScope.cs` and `MongoJoinBinder.cs` were deleted, `MongoSelectDefinition.JoinScope`/its `HasTerminalOperator` disjunct were removed, the `TranslateJoinCore` wiring call was removed, and `MongoSelectLowerer.AppendLookupStages`'s added arm was removed (its removal restores the pre-existing `IsStreamableReference` arm as the first-matching handler for reference-Include lookups, which is exactly the behavior that arm had before this design's Task 5 reordered it — verified safe by the full regression suite). If this is ever revisited after an EF Core nav-expansion fix, the design in this document (including the resolved API questions — `MongoExpressionTranslator.TryTranslateValue`, and reuse of `MongoTransparentScopeResolver` at `sourceCount: 1`) is still valid; it would need to be re-implemented from this spec rather than reactivated from dormant code. A follow-up JIRA ticket, [EF-439](https://jira.mongodb.org/browse/EF-439), tracks revisiting this once upstream changes.

This does **not** affect Include-shaped joins, which are handled by the separate, unrelated `TryConfirmReferenceInclude` mechanism and continue to work natively exactly as before this design.

## Problem

Today, `MongoQueryableMethodTranslatingExpressionVisitor.TranslateJoinCore` handles every `Join`/`LeftJoin`/`GroupJoin` call, but its native-lowering ambition stops at confirming **Include-shaped** joins (a plain reference navigation attach, via `TryConfirmReferenceInclude`). Any join whose result selector combines fields from **both** sides into a genuinely new shape (`.Join(...).Select(x => new { x.o.Name, x.i.Total })`, or a `Where`/`OrderBy` reading both sides) — i.e. a *real* relational join, not Include sugar — always falls back to the driver-LINQ bridge. `NativeSlotPopulator` and the projection binder don't recognize the `TransparentIdentifier(Outer, Inner)` shape a join result selector produces, and decline (this is the ticket's two largest decline sites: `NativeSlotPopulator.PopulateNativeSlots` and `TranslateSelect`, ~265 of the reported 373 cases combined).

`NativeSelectManyBinder` already solved the structurally identical problem for `SelectMany`'s own `TransparentIdentifier(Outer, Inner)` shape: a two-scope translator that resolves member access by **parameter identity** (never member name — a durable cross-cutting invariant of this codebase) into an outer field ref or a prefixed inner field ref.

## On the 373 figure

The number in EF-392 was measured on `e1fb753d` (2026-08-07), on the `NativeQueryOngoing` lineage — a different, unsquashed history than the current branch's `2dc9444` squash (2026-08-24), which is **not** a descendant of that measurement commit. Substantial join/Include work already landed in the squash (the AGENTS.md capability summary now documents single-level reference/collection Include as native, which post-dates the measurement). **Treat 373 as a stale upper bound, not a current count** — this design targets the *capability gap* (real two-sided joins have no native path at all), not a specific case count. Re-measuring exactly is out of scope for this design; it's cheap to re-derive post-implementation via the existing `MONGODB_EF_NATIVE_ONLY=1` spec-suite instrument.

## Scope

*(superseded — see "Blocker found during implementation" above)*

**In:**
- A single `Join` or `LeftJoin` call (one join level), where:
  - `TryConfirmReferenceInclude` has already declined it as Include sugar (i.e., this is a genuine two-sided join, not a navigation attach).
  - The existing single-level `$lookup` eligibility constraints hold: bare-collection-scan inner (`IsBareCollectionScan`), no query filter on the target, simple FK/PK key selectors (the same constraints reference-Include already enforces via `MarkSawNonBareJoinInner`/`AnalyzeKeySelectorTarget`).
- `Where`/`OrderBy`/`Select` composed immediately after the join, reading fields from either or both scopes (`Outer`, prefixed `Inner`), translated via the two-scope resolver.
- Correct join semantics: `Join` → `$lookup` + `$unwind(preserveNullAndEmptyArrays: false)` (inner join, drops unmatched); `LeftJoin` → `$unwind(preserve: true)`.
- Mixed-scope predicates/projections rendered via `$expr` (the existing aggregation-expression dialect), matching how `SelectMany`'s correlated predicates already render.

**Out (explicitly deferred, not silently unsupported):**
- `GroupJoin`'s own array/grouped result shape (`.GroupJoin(...)` without a flattening `SelectMany`) — owned by EF-436, not duplicated here.
- Chained/nested joins (a second `Join` composed onto this join's result, or a join whose inner is itself a join) — declines to fallback, same as today. A follow-up increment once this lands.
- Widening the `$lookup` eligibility constraints themselves (query-filtered targets, composite non-PK keys, computed key selectors) — that's EF-368/Include-breadth territory (EF-392 chunk B), not this chunk.
- Any join whose outer or inner source is `GroupBy`- or `Distinct`-sourced — already handled by existing `MarkGroupByFallbackUnsafe`/`MarkNotNativelyRepresentable` calls in `TranslateJoinCore`; unchanged by this work.

## Design

*(superseded — see "Blocker found during implementation" above)*

### Components

1. **Shared two-scope resolver extraction.** Factor the parameter-identity scope-resolution logic currently embedded in `NativeSelectManyBinder` (the machinery behind `TryBindTransparentIdentifierProjection`'s outer-vs-prefixed-inner field routing) into a small shared internal helper. Both `NativeSelectManyBinder` and the new join binder call it. This is a behavior-preserving refactor for `SelectMany` — `NativeSelectManyTests` must stay green with zero MQL changes.

2. **`MongoJoinBinder` (new type, `NativeTranslation/`).** Entry point invoked from `TranslateJoinCore` immediately after `TryConfirmReferenceInclude` has declined the shape as Include sugar (so this only ever runs for a genuine two-sided join). Responsibilities:
   - Re-check the same single-level eligibility gates reference-Include already computed (`IsBareCollectionScan`, `!_sawNonBareJoinInner`, resolvable FK/PK) — read, not re-implement, existing `MongoSelectDefinition` state.
   - On success: register the join as a native two-scope source on `MongoSelectDefinition` (see below) and let subsequent `Where`/`OrderBy`/`Select` route through the shared two-scope resolver.
   - On failure: return `null` / call `MarkNotNativelyRepresentable()` (graceful fallback — Join has a working driver-LINQ bridge, so this is never a hard decline, unlike `SelectMany`'s no-baseline family).

3. **`MongoSelectDefinition` — new join-scope registration.** Add a join-scope marker (mirroring `UnwindSources`) so `HasTerminalOperator` covers "composed after a native join" — extending the existing post-terminal-gating invariant that already protects `SelectMany`/`GroupBy`/`Distinct`/set-ops. Every operator with its own `Translate*` override must gate on this exactly like the others; a gate that's narrower than what the join binder actually handles is the specific silent-wrong-data trap this codebase's own AGENTS.md calls out.

4. **`MongoSelectLowerer.AppendLookupStages`.** New branch alongside the existing reference-Include/collection-Include/SelectMany-flatten branches: for a confirmed native join, emit `MongoLookupStage` + `MongoUnwindStage` with `preserveNullAndEmptyArrays` set from the join's `isLeftOuter` flag (already tracked in `JoinInfo`). No new stage types — reuses the existing ones.

5. **Rendering.** No renderer changes anticipated — a mixed-scope predicate/projection is a field-to-field comparison or arithmetic expression over two field refs (one prefixed), which `MongoAggregationExpressionRenderer` already renders via `$expr`. The two-scope resolver is responsible for producing correctly-prefixed `MongoExpression` field refs; rendering itself is unchanged.

### Data flow

*(superseded — see "Blocker found during implementation" above)*

```
Join/LeftJoin call
  → TranslateJoinCore (unchanged: resolve navigation, build JoinInfo, existing eligibility marks)
      → TryConfirmReferenceInclude: is this Include sugar?
          yes → existing native Include path (unchanged)
          no  → MongoJoinBinder.TryBindJoin (NEW)
                    eligible  → register join-scope on MongoSelectDefinition; shaper carries
                                TransparentIdentifier(Outer, Inner) tagged as a native two-scope source
                    ineligible → MarkNotNativelyRepresentable() → driver-LINQ fallback (unchanged)
  → subsequent Where/OrderBy/Select
      → NativeSlotPopulator / projection binder: recognize the join-scope tag,
        route field access through the shared two-scope resolver (by parameter identity)
  → MongoSelectLowerer.AppendLookupStages: emit $lookup + $unwind(preserve = isLeftOuter)
```

### Error handling

Unchanged from today's contract for Join: every unsupported shape (chained joins, GroupJoin's array shape, widened `$lookup` eligibility) falls back gracefully to the existing driver-LINQ bridge — never a hard decline, since Join already has a correct, working non-native path. This differs from `SelectMany`'s reference-collection family (no driver-LINQ baseline at all) — do not copy that hard-decline pattern here.

### Testing

*(superseded — see "Blocker found during implementation" above)*

- **`NativeJoinTests` (new, functional)** — asserts the newly-native shapes succeed under `MongoQueryMode.NativeOnly` (the only reliable "went native" signal, per `Query/AGENTS.md` — MQL shape alone doesn't prove it, since fallback can emit structurally identical `$lookup`/`$unwind`).
- **Differential-correctness theory fixture** — native result vs. an in-memory LINQ oracle evaluated over the same `Expression`, covering matched/unmatched/left-null rows (the established pattern from `NativeOwnedCollectionAllTests`).
- **Unit tests** for the extracted shared two-scope resolver, plus `MongoJoinBinder`'s eligibility gating (mirroring `NativeSelectManyBinder`'s existing unit-test shape).
- **Regression guard:** existing `NativeSelectManyTests` and all reference-Include tests (`Ef372DeepReferenceIncludeTests`, `Ef373InterleavedPagingTests`, `Ef379RootNavigationMisroutingTests`) must stay green with zero MQL changes — the shared-resolver extraction and the new `AppendLookupStages` branch are both purely additive.
- **Explicit non-goals get a declining regression test too** (chained joins, GroupJoin's array shape) so a future change can't silently "fix" them into a wrong-data trap without a deliberate decision.

## Coordination with concurrent EF-411 work

A separate agent is implementing EF-411 (scalar-aggregate binder, Distinct, set operations, no-binder operators, **post-terminal guards**) concurrently on this repo. `MongoSelectDefinition.cs` is a likely shared touch point — EF-411's "post-terminal guards" stream and this design's new join-scope marker both modify the same terminal-operator gating logic. Mitigate by:
- Landing the shared two-scope resolver extraction as its own small, early commit (pure refactor, no new capability) to reduce the diff surface before the join-scope addition lands.
- Rebasing/checking `MongoSelectDefinition.cs` and `HasTerminalOperator`'s call sites against `main` immediately before merging, not just at the start.
- No other files are expected to overlap (`TranslateJoinCore`, the new `MongoJoinBinder`, `MongoSelectLowerer.AppendLookupStages` are join-specific; EF-411's areas are `NativeGroupByBinder`/`NativeCardinalityBinder`/set-op routing).
