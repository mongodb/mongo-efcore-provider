# Native flat collection Includes — design (EF-339 / SP5)

**Date:** 2026-07-08 · **Branch:** `EF-339` (off `b74271a`, the EF-336/SP4 tip) · **JIRA:** EF-339
**Epic:** EF-322 (native LINQ query rewrite) · **Overview:** `2026-06-23-native-query-provider-overview.md`

> **Reviewer:** read this for the *what* and *why* of the fifth native sub-project. The overview doc's
> §"Ship as a sequence of sub-projects" lists this as #5 (Collection Includes); this doc scopes it to
> the **flat** slice.

## TL;DR

- Make the native aggregation-pipeline path emit a **single-level collection `$lookup`** and materialize
  the resulting array itself, so `Include(x => x.Orders)` no longer falls back to driver-LINQ.
- Also cover **projected collection `.Count`** (`Select(x => x.Orders.Count)`).
- **DOM-only** materialization (reproduce the existing `IncludeCollection` fixup over the `_lookup_<Nav>`
  array) — streaming of collection arrays is deferred to the materializer-perf sub-project (SP7).
- **Fold in EF-334** (route-centralization): generalize the out-of-`Route` lookup gate and move it into
  `MongoSelectDefinition.Route`.
- Everything else stays on the driver-LINQ fallback (throws under `NativeOnly`): nested/transitive
  `ThenInclude`, filtered Include, collection-of-collection. **EF-317** (`$lookup` string-match hardening
  / plumbing cleanup) is a **separate follow-on commit**, not part of this one.

## Where the native path stands today

Single-level **reference** Include is already native (SP1). `MongoSelectLowerer.AppendLookupStages`
emits a `MongoLookupStage` + `MongoUnwindStage` pair for each **streamable reference** lookup
(`LookupExpression.IsStreamableReference` — a reference nav, no sub-pipeline, no transitive `_lookup_`
local field). Reference Include materializes via the streaming path when the entity is
streaming-eligible, otherwise via the native DOM shaper.

**Collection** lookups are rejected in two places and route to driver-LINQ:

- `MongoSelectLowerer.AppendLookupStages` — the per-lookup guard throws
  `NativeTranslationNotSupportedException` for any lookup where `!IsStreamableReference` (a collection
  lookup has `IsCollection == true`, so it fails this test). This makes `TryBuildNativeFactory` return
  `null`, so even the native **DOM** path is unavailable and the query falls back to driver-LINQ.
- `MongoShapedQueryCompilingExpressionVisitor.AllPendingLookupsAreStreamableReferences` — an
  additional gate condition (checked *outside* `MongoSelectDefinition.Route`, alongside
  `StreamingEligibility`) that keeps a collection-lookup query off the **streaming** path.

The collection-`$lookup` **binding** machinery already exists and is used on the driver-LINQ path:
`MongoProjectionBindingExpressionVisitor.Lookup.cs` builds a `CollectionShaperExpression` over the
`_lookup_<Nav>` array (and, for projected `.Count`, the `InjectAfterRoot` `$lookup` +
`TryBindProjectedCollectionNavigationCount`). The DOM collection materializer
(`MongoProjectionBindingRemovingExpressionVisitor.IncludeCollection`) already fills a `List<TElement>`
and runs the collection fixup. SP5 makes the **native pipeline** produce the same `_lookup_<Nav>` array
these consumers already expect.

## Scope

**In:**

1. **Single-level collection Include** — `Include(x => x.Orders)` emitted natively as a `$lookup`
   **without** a following `$unwind` (the collection stays an array under `_lookup_<Nav>`), materialized
   via the **DOM shaper**.
2. **Projected collection `.Count`** — `Select(x => x.Orders.Count)` via the `InjectAfterRoot` `$lookup`
   + `$size` shape the driver-LINQ path already produces.
3. **EF-334 route-centralization** — generalize the out-of-`Route` lookup gate to "all lookups are
   natively renderable (reference *or* flat collection)" and fold it into `MongoSelectDefinition.Route`.

**Out (stays on driver-LINQ fallback; throws under `NativeOnly`):**

- Nested / transitive `ThenInclude` (reference-under-collection, collection-under-collection).
- Filtered Include (`Include(x => x.Orders.Where(...))`).
- Collection-of-collection.
- **Streaming** of collection arrays — deferred to SP7 (materializer perf). `StreamingEligibility`
  continues to reject collection navs, so collection-Include queries take the native **DOM** path.
- **EF-317** (`_lookup_<Nav>` string-match hardening / `$lookup`+`$unwind` plumbing tidy) — a separate
  follow-on commit, so this stays a tight feature commit.

## Architecture

### 1. Lowerer — emit a collection `$lookup` (no `$unwind`)

`MongoSelectLowerer.AppendLookupStages` gains a **native collection lookup** case beside the reference
case. Introduce a predicate on `LookupExpression` — `IsNativeCollectionLookup` (mirror of
`IsStreamableReference`): a **single-level** collection nav, **no** sub-pipeline, **no** transitive
`_lookup_` local field. For such a lookup, emit a `MongoLookupStage` **and no `MongoUnwindStage`** — the
collection remains an array under `_lookup_<Nav>`. Lookups that are neither a streamable reference nor a
native collection lookup (nested, filtered, collection-of-collection) still throw
`NativeTranslationNotSupportedException` (surfaced under `NativeOnly`, routed to fallback otherwise).

The join-coverage guard (fewer lookups than inner collections ⇒ throw) is preserved.

### 2. DOM read-back

The native pipeline's output document must carry the collection under the exact `_lookup_<Nav>` alias
the existing `CollectionShaperExpression` / `MongoProjectionBindingRemovingExpressionVisitor.IncludeCollection`
consumer reads. Verify the alias produced by the lowerer/renderer (`LookupExpression.GetLookupAlias`)
matches what the binding built on the driver-LINQ path, and that `IncludeCollection` reads the array out
of the native `BsonDocument`, fills `List<TElement>`, and runs the navigation fixup. No new shaper — the
existing DOM collection materializer is the target shape.

### 3. Projected collection `.Count`

Reproduce the shape `TryBindProjectedCollectionNavigationCount` produces on the driver-LINQ path: an
`InjectAfterRoot` `$lookup` for the collection, then the count surfaced as `$size` of the looked-up
array in the projected scalar. The lowerer emits the injected `$lookup`; the projection carries the
`$size` computation.

### 4. Gate / routing (EF-334 fold)

Once the lowerer stops throwing for flat collection lookups, `TryBuildNativeFactory` succeeds and the
native **DOM** path is chosen automatically (streaming stays off via `StreamingEligibility`). As part of
this, **generalize** `AllPendingLookupsAreStreamableReferences` to
`AllPendingLookupsAreNativelyRenderable` ("reference *or* flat collection") and **move** that decision
into `MongoSelectDefinition.Route`, so `Route` becomes the single authoritative is-native signal for the
lookup dimension too (the route-centralization tracked as EF-334). This requires surfacing the
`$lookup` state (currently on `MongoQueryExpression`) to `Route`; that plumbing is the crux of the fold.

> Note: `ContainsVectorSearch` remains a separate out-of-`Route` gate condition — EF-334 as scoped here
> folds only the lookup condition.

## Testing

Per the Query area's testing guidance, **MQL shape alone does not prove native** for shapes whose
fallback pipeline is structurally identical — the reliable signal is `MongoQueryMode.NativeOnly`
(native-capable ⇒ succeeds; fallback ⇒ throws `NativeTranslationNotSupportedException`).

- **Unit** (`tests/.../UnitTests/Query/NativeTranslation/`): the collection `$lookup`-**no**-`$unwind`
  MQL shape; the projected `.Count` `$lookup` + `$size` shape; `IsNativeCollectionLookup` predicate
  boundaries; nested/filtered still route to fallback.
- **Functional** (real DB): end-to-end materialization of `Include(x => x.Orders)` and projected
  `.Count` — correct arrays and counts, tracking and no-tracking.
- **`NativeOnly` proof:** flat collection Include + projected `.Count` **succeed** under `NativeOnly`;
  nested / filtered / collection-of-collection **throw**.
- **Spec sweep:** `MONGODB_EF_NATIVE_ONLY=1` must show **+passing, zero regressions**. EF8/EF9/EF10
  suites green.

## Delivery

Single squashed commit on branch `EF-339`, stacked on `b74271a` (EF-336/SP4), one PR-style message
(doubles as the stacked PR description). Subagent-driven development, stopping after every task for
review. Keep an `EF-339-presquash` safety branch until merge. Native becoming the default path for
collection Include and the changed emitted MQL are implementation details, not breaking changes (per the
provider's versioning rubric) — query results are unchanged.

## Follow-ups (not in this commit)

- **EF-317** — `_lookup_<Nav>` string-match hardening + `$lookup`/`$unwind` plumbing cleanup (separate
  stacked commit on top of this one).
- **SP5b / later** — nested/transitive `ThenInclude`, filtered Include, collection-of-collection.
- **SP7** — one-pass streaming of collection arrays (materializer perf).
