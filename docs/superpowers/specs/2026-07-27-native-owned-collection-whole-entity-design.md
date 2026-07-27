# Native owned-collection whole-entity queries — design (EF-322 Phase 2)

*Date: 2026-07-27 · Branch: `EF-322-owned-collection-whole-entity-native` (stacked on native tip `690b487` / `origin/NativeQueryOngoing`).*

## 1. One-line summary

A whole-entity query over an entity that has one or more **owned collection** navigations
(`OwnsMany`, e.g. `Blog { List<Post> Posts }`) now goes **native** and **streams** via the SP7
one-pass materializer — previously it always fell back to driver-LINQ. This is the next Phase-2 slice
after owned single-**reference** whole-entity (`690b487`).

## 2. Problem and root cause

Owned collections embed as a BSON **array** inside the owner document (no `$lookup`). Owned navigations
are always eager-loaded by EF Core convention, so nav-expansion injects a synthetic
`Select(b => IncludeExpression(b, Posts))` for the auto-include. That `IncludeExpression` body matches
**none** of `TranslateSelect`'s pass-through predicates:

- `IsSingleLevelCollectionIncludeSelector` — requires `!navigation.IsEmbedded()` (a cross-collection
  `$lookup` collection Include), so an *embedded* owned collection is rejected.
- `IsOwnedEmbeddedReferenceIncludeSelector` — rejects `navigation.IsCollection`
  (`MongoQueryableMethodTranslatingExpressionVisitor.cs:663`), so an owned *collection* is rejected.
- `IsTransparentIdentifierSelector` / `IsTransparentIdentifierMemberAccessSelector` — don't match an
  `IncludeExpression` body.

So the `Select` falls into the projection else-branch, `NativeProjectionBinder.TryPopulateNativeProjection`
rejects the `IncludeExpression` body (not an anonymous/DTO construction), `MarkNotNativelyRepresentable()`
is called, and `Route = Fallback`. **The gate is the sole blocker** — exactly the same root cause as the
owned single-reference slice.

## 3. Key finding from code exploration (to be *confirmed* by the spike, not assumed)

The downstream machinery already supports embedded owned collections:

- **`StreamingEligibility.IsEligible`** admits owned collections. It rejects only *non-owned* collections
  (`!navigation.TargetEntityType.IsOwned() && navigation.IsCollection`) and *collection-of-collection*
  (`navigation.IsCollection && target.GetNavigations().Any(n => n.IsCollection)`). Its summary doc-comment
  ("No owned collections") is **stale** — the code contradicts it, and it explicitly contemplates an owned
  collection element's composite key (owner FK + synthesized ordinal).
- **`MongoStreamingEntityMaterializerRewriter.BuildPlan`** fully materializes owned collections
  (`CollectionPlan`, element plan, 1-based ordinal counter, `List<TElement>` accumulator, fill-loop) —
  lines ~320-337. Its class-level and `BuildPlan` doc-comments ("Owned collections … are rejected") are
  **stale**; only *non-owned* collections actually throw (`NativeTranslationNotSupportedException`,
  lines ~296-301).

**Warning sign, and why the spike is mandatory:** the doc/code contradiction means this owned-collection
streaming path may never have been exercised end-to-end for a *whole-entity* query (owned-collection
whole-entity has always fallen back before reaching the rewriter). The spike must **prove** the path fires
and is correct — it must not merely observe that the machinery compiles.

## 4. Scope (YAGNI)

**In scope**

- A whole-entity query (`db.Blogs`, with `Where`/`OrderBy`/`Skip`/`Take` filter/sort/paging as usual)
  where the root entity has ≥1 owned collection navigation (`OwnsMany`).
- Owned collection **alongside** an owned reference on the same entity (currently rejected as a whole by
  `IsOwnedEmbeddedReferenceIncludeSelector` because it requires *every* nav in the chain to be
  `!IsCollection`).
- Nested owned reference **inside** a collection element (`Blog { Posts[] { Author (OwnsOne) } }`).
- Streams via the SP7 one-pass path where `StreamingEligibility` admits; DOM shaper otherwise
  (collection-of-collection).

**Out of scope (unchanged; separate, deferred gaps)**

- Owned-collection **sub-property** predicates/projections — `Where(e => e.Posts.Count > 3)`,
  `Select(e => e.Posts.Count)`, `Select(e => new { e.Posts })`. A different code path
  (`MongoExpressionTranslator` / `NativeProjectionBinder` member-rooted-on-parameter), untouched here.
- Non-owned / reference collection `Include` (`$lookup` collection-Include path — separate).
- TPH hierarchies that also own a collection — already route native via the DOM shaper (same as plain
  TPH); `StreamingEligibility` independently excludes TPH from *streaming*. No change needed.
- Collection-of-collection **streaming** — `StreamingEligibility` rejects it; it must still materialize
  correctly via **DOM** (verified in the matrix) but does not stream. Making it stream is a follow-on.

## 5. Approach — A (chosen): generalize the one gate predicate

Rename `IsOwnedEmbeddedReferenceIncludeSelector` → **`IsOwnedEmbeddedIncludeSelector`** and drop the
`navigation.IsCollection` rejection, so it admits a chain of one-or-more `IncludeExpression` layers where
**every** navigation is `IsEmbedded()` (collection *or* reference) and the innermost `EntityExpression` is
the lambda's own parameter. Route stays `WholeEntity`; the `Select` falls through to the ordinary
whole-entity shaper fold. Streaming-vs-DOM is decided by the **untouched** `StreamingEligibility`.

```csharp
// before (line 663): reject collections and non-embedded
if (navigation.IsCollection || !navigation.IsEmbedded())
    return false;

// after: reject only non-embedded; embedded collection OR reference both admitted
if (!navigation.IsEmbedded())
    return false;
```

Consequences of A:

- A **mixed** owned-ref + owned-collection chain on the same entity (currently rejected as a whole) now
  goes native.
- **No** pipeline / lowerer / renderer / shaper / `StreamingEligibility` changes — the native `{}` pipeline
  already returns the embedded array, and both the DOM and one-pass streaming shapers already read it back
  (subject to spike confirmation).
- Fix the two **stale doc-comments** (`StreamingEligibility` summary; rewriter class-level + `BuildPlan`) to
  match the code — they are actively misleading for the next reader.

**Rejected alternatives**

- **B — separate sibling predicate** (`IsOwnedEmbeddedCollectionIncludeSelector`): more code, largely
  duplicative, no benefit; a mixed ref+collection chain would need extra work to admit.
- **C — broaden + explicit DOM-forcing guard in the gate**: redundant — `StreamingEligibility` already
  forces DOM for collection-of-collection.

## 6. Method — spike-led, subagent-driven, stop-for-review each task

Mirrors the reference slice's task shape.

- **T1 — slice-0 throwaway spike (GO/NO-GO gate before any prod edit).**
  - Pin the exact `IncludeExpression` tree EF emits for an owned-collection auto-include on a whole-entity
    query (confirm it is an `IncludeExpression{Navigation:collection, EntityExpression:param}` the gate
    loop walks — not a `CollectionShaperExpression`/`MaterializeCollectionNavigation` wrapper that would
    need separate handling).
  - Confirm approach A makes it go native end-to-end (`NativeOnly` succeeds).
  - Prove **both** DOM and one-pass **streaming** materialize the embedded array correctly across the full
    edge matrix (§7) — compare live materialized values, not just that it runs.
  - Enumerate spec flips; re-verify the `NativeOnly` EF10 spec pass-set stays at the `2192/2397/19`
    baseline (expected: functional-only flip, Northwind has no owned-collection whole-entity coverage).
  - Decide the streaming floor: if the spike finds the streaming rewriter's owned-collection path is
    broken/incomplete for whole-entity, ship native-via-**DOM** now (still a real win) and defer streaming
    as a follow-on — per the approved "native+streaming, DOM fallback" ambition. All spike code discarded.
- **T2 — gate predicate change** (+ the two doc-comment fixes). One production file
  (`MongoQueryableMethodTranslatingExpressionVisitor.cs`), plus the two stale comments.
- **T3 — parity/edge matrix tests** (functional `NativeOwnedCollectionWholeEntityTests` alongside the
  existing `NativeOwnedReferenceWholeEntityTests`; extend/adjust `StreamingEligibilityTests` and any unit
  gate tests that flip). Assert `Native == DriverLinq` parity **and** a `NativeOnly` routing proof per test.
- **T4 — validate.** 3-version `/test-all` (EF8/EF9/EF10, 0-fail) + `NativeOnly` EF10 spec sweep
  re-baseline + Query `AGENTS.md` as-built note.

## 7. Edge / parity matrix (spike + T3)

| Case | Expectation |
|---|---|
| Empty owned collection (`[]`) | materializes to an empty list, not null |
| Missing array element / absent field | matches DOM / driver-LINQ (empty or default per EF contract) |
| Populated collection, multiple elements | ordinal ordering preserved; all elements correct |
| Nested owned reference inside a collection element | inner owned ref materializes correctly |
| Owned collection **and** owned reference on same entity (mixed chain) | both materialize; chain admitted as a whole |
| Shared-CLR-type owned collection | materializes via `UnwindSource.InnerEntityType`-style correct type resolution |
| Collection-of-collection | **DOM** path, still correct (not streamed) |
| Tracking vs. `AsNoTracking()` | both correct; owned entities tracked with owner |
| Streaming path (`NativeOnly`) | eligible shapes stream; ineligible fall to DOM; both correct |

Owned collections have a **driver-LINQ oracle** (owned data round-trips through the driver), so parity is
assertable as `Native == DriverLinq`, plus a `NativeOnly` proof that the query genuinely routes native
(not a structurally-identical fallback).

## 8. Not-a-break / versioning

Eligibility change is fallback→native, **results unchanged** — not a break per the provider rubric
(and "native default / changed emitted MQL are non-breaking" is settled). The flip is expected to be
**functional-only** (functional tests move from asserting fallback to asserting native); the Northwind
spec `NativeOnly` pass-set is expected to stay `2192/2397/19`. Any spec flip found in the spike is updated
**with** correctness verification and the sweep re-baselined. No public API, annotation, or persisted-shape
change; all touched types are `internal`. No `#if` — identical EF8/EF9/EF10 behavior.

## 9. Risks

- **Primary risk:** the doc/code contradiction — the streaming owned-collection path may never have run
  end-to-end for a whole-entity query. Mitigated by the T1 spike's hard GO/NO-GO gate and the DOM floor.
- The `IncludeExpression` shape for an owned collection auto-include *might* differ from the reference case
  (e.g. wrapped in a collection-shaper node) and need more than a gate relax. The spike pins this first;
  if so, the slice scope grows or drops to DOM-only per the ambition decision.
- Streaming an owned collection allocates a `List<TElement>` per row — the SP7 one-pass allocation win is
  naturally smaller for array-bearing shapes. Expected, not a regression.

## 10. Deliverables

- Gate predicate generalization + two doc-comment corrections.
- `NativeOwnedCollectionWholeEntityTests` (functional) + unit/eligibility test adjustments.
- Query `AGENTS.md` as-built note.
- 3-version `/test-all` green; `NativeOnly` spec sweep re-baselined.
- Squashed to one slice commit, plain-FF onto `origin/NativeQueryOngoing`, `-presquash` backup kept.
