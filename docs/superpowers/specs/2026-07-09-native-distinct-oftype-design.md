# Native Distinct + OfType — design (EF-347 slice 1)

**Date:** 2026-07-09 · **Branch:** `EF-347` (off `7a9a702`, the EF-344/SP6-GroupBy tip) · **JIRA:** EF-347
**Epic:** EF-322 (native LINQ query rewrite) · **Overview:** `2026-06-23-native-query-provider-overview.md`

> **Reviewer:** read this for the *what* and *why* of the first slice of EF-347 ("remaining SP6
> relational operators"). EF-347 is a heterogeneous grab-bag; this doc scopes **only** the two operators
> that fit the existing single-flat-pipeline native model — `Distinct` (projected) and `OfType` — and
> explicitly defers everything that needs new sub-pipeline / multi-source machinery.

## TL;DR

- Make the native aggregation-pipeline path translate **projected `Distinct`** (`Select(proj).Distinct()`)
  and **`OfType<Derived>()`** instead of falling back to the driver-LINQ provider.
- **Distinct** is modeled as a *degenerate GroupBy*: group by the projected value (key-only, **zero
  accumulators**), then flatten `_id` back to the result. Reuses the EF-344 `$group` renderer, flatten
  `$project`, grouped result shaper, and — crucially — the EF-344 **post-group guards**, so anything
  after `Distinct` falls back with no new guard code.
- **OfType** emits a **discriminator `$match`** conjunct (`$eq` for a leaf type, `$in` over the subtree
  for an intermediate type). The shaper is already re-typed today; the only change is emitting the
  predicate natively instead of marking the query non-native. OfType composes with other operators
  (it is just a `$match`).
- **Out of scope (defer to a later EF-347 slice / foundational sub-project):** whole-entity `.Distinct()`;
  any operator *after* `Distinct`; set operations; `SelectMany`; non-canonical `Skip`/`Take`. These need
  nested-pipeline / second-source / non-canonical-order machinery the native layer does not have.

## Why only these two

The native layer is a **single flat pipeline over one collection** — there is no nested/sub-pipeline,
subquery, or second-source representation (`NativeRoute` = `Fallback`/`WholeEntity`/`Projection`/
`ScalarAggregate`/`GroupBy`; the stage IR is a fixed flat set; `$lookup` is flat-only; `$unionWith`
exists only in the driver-LINQ fallback bridge). Of EF-347's operators, only projected `Distinct`
(one more `$group`) and `OfType` (one more `$match`) are expressible as a single extra flat stage. Set
operations, cross-`DbSet` `SelectMany`, and non-canonical `Skip`/`Take` all require the missing
sub-pipeline/multi-source foundation and are their own larger sub-project.

## Where the native path stands today

- `Distinct` — `TranslateDistinct` is inert (`=> null`); not whitelisted in
  `NativeSlotPopulator.IsNativeRepresentableSlotOperator`, so the catch-all marks the query non-native →
  driver-LINQ fallback (correct results; throws under `NativeOnly`).
- `OfType` — `TranslateOfType` already **re-types the shaper** to the derived type
  (`UpdateShaperExpression(... WithType(derived))`) but then calls `MarkNotNativelyRepresentable()`,
  deferring the discriminator filter to the driver-LINQ path (comment: a native pipeline would return
  every document and the DOM shaper would fail to materialize a sibling type). It is whitelisted and
  throws `NotSupportedException` for complex (non-entity) target types.
- The EF-344 grouping machinery — `MongoGrouping` IR on `MongoSelectDefinition.Grouping`,
  `NativeRoute.GroupBy`, `MongoGroupStage` + `MongoPipelineFactory.RenderKeyedGroup`, the
  `MongoSelectLowerer` group branch, the flatten `$project`, the grouped-row DOM result shaper, and the
  post-group `IsGroupBy` guards in `NativeSlotPopulator` + `NativeCardinalityBinder` — all already exist
  and are reused here.

## Scope

**In:**

1. **Projected `Distinct`** — `Select(proj).Distinct()` (scalar, anonymous, or DTO projection) →
   `$group(_id: <projection>)` + flatten `$project(_id → result)`.
2. **`OfType<Derived>()`** — full-hierarchy discriminator narrowing: `$eq` for a leaf target, `$in` over
   the target's subtree (target + descendants) for an intermediate target; emitted as a native `$match`
   conjunct. Composes with other native operators.

**Out (falls back to driver-LINQ; throws under `NativeOnly`):**

- **Whole-entity `.Distinct()`** (no preceding projection) — dedup-entire-documents semantics deferred.
- **Any operator after `Distinct`** — `Where`/`OrderBy`/`Skip`/`Take`/`Count`/aggregate applied after the
  `Distinct` (inherited fallback via the `IsGroupBy` guards).
- **Set operations** (`Union`/`Concat`/`Intersect`/`Except`), **`SelectMany`**, **non-canonical
  `Skip`/`Take`** — need sub-pipeline / multi-source / non-canonical-order machinery (separate slice).
- **`OfType`** where the discriminator can't be resolved (non-TPH, no discriminator) or a complex
  non-entity target type.

## Architecture

### 1. Distinct as a degenerate GroupBy

`Select(proj).Distinct()` is semantically *"group by the projected value, take the key"*. `TranslateSelect`
already binds the terminal member-access projection into `MongoSelectDefinition.Projection` (native
`$project`). `TranslateDistinct` (new implementation) converts that bound projection into a **key-only
`MongoGrouping`**: each projection member (`alias → MongoExpression`) becomes a composite `_id` key part
keyed by its alias, with **zero accumulators**; it moves this onto `Select.Grouping` and clears the
`Projection` slot. The existing lowerer group branch then emits `$group(_id: <key>)` + the flatten
`$project(_id → result)`, and the existing grouped-row DOM shaper materializes the result.

`Distinct` is whitelisted in `NativeSlotPopulator.IsNativeRepresentableSlotOperator` so the catch-all does
not clobber the `TranslateDistinct` decision. `NativeGroupByBinder`'s "≥1 accumulator" rule is relaxed for
this distinct entry point (a dedicated helper / flag — the GroupBy *user* path still requires an
aggregate). Setting `Select.Grouping` sets `IsGroupBy`, so the EF-344 post-group guards
(`NativeSlotPopulator` + `NativeCardinalityBinder`) already force fallback for any operator after
`Distinct` — no new guard code.

Preconditions for native (else `MarkNotNativelyRepresentable()`): a native `Projection` is present (not
whole-entity), and no `Grouping`/`Cardinality`/paging is already set. Projection keys carrying a value
converter or non-default `BsonRepresentation` are rejected via the existing
`NativeGroupByBinder.HasDefaultKeySerialization` (the generic `_id` readback would otherwise diverge from
the configured serializer — the same guard EF-344 uses for group keys).

### 2. OfType as a discriminator `$match`

`TranslateOfType` keeps its existing shaper re-typing and replaces the unconditional
`MarkNotNativelyRepresentable()` with: build the discriminator predicate from the target `IEntityType`'s
metadata — the discriminator element name plus the set of discriminator values for the target's subtree
(target + all descendants) — and add it via `AddPredicateConjunct` (a native `$match`). A single value
renders `$eq`; multiple values render `$in`. Only if the discriminator cannot be resolved (non-TPH, no
discriminator) does it fall back. Because this is just another `$match` conjunct in the canonical
`$match → …` order, OfType composes with preceding and following native operators and needs no
post-operator guard.

**Materialization assumption (plan step 1 verifies):** the native whole-entity DOM shaper already
materializes TPH derived types polymorphically (a native query over a TPH base already returns the correct
mixed derived types via the stored discriminator). OfType is then purely additive — the `$match` narrows
which rows return; materialization is already discriminator-driven. If that does **not** hold, OfType
native requires shaper work and this slice rescopes to Distinct-only.

## Edge cases

- **Scalar-projection Distinct** (`Select(x => x.City).Distinct()`) → `$group(_id: "$City")`; result read
  from `_id`. **Composite/anonymous** → `_id` sub-document, flattened to result aliases (reuses the
  GroupBy flatten path).
- **Distinct-after-Distinct**, **Distinct after GroupBy / aggregate / paging** → fall back (guarded by
  `IsGroupBy` / the existing paging + grouping guards).
- **Represented/converted projection keys** in Distinct → reject → fall back (`HasDefaultKeySerialization`).
- **OfType leaf vs intermediate** → `$eq` vs `$in` over the subtree.
- **OfType to the base / same type** (sometimes emitted by nav-expansion) → predicate over all
  discriminator values (or none) = effectively no filter; correct and harmless.
- **OfType then Distinct** composes: `$match(discriminator) → $project → $group` (OfType is a pre-Distinct
  filter).

## Testing

Per the Query area's guidance, **MQL shape alone does not prove native** — the dispositive instruments are
`MongoQueryMode.NativeOnly` ("went native" ⇒ succeeds; fallback ⇒ throws
`NativeTranslationNotSupportedException`) and **Native-vs-DriverLinq result parity** (catches wrong data /
a lossy pipeline).

- **Parity + NativeOnly** for: scalar / composite / anonymous projected Distinct; OfType leaf; OfType
  intermediate (`$in` subtree); OfType composed with `Where`/`Select`/`OrderBy`; OfType then Distinct.
- **Fallback (Native correct) + NativeOnly-throws** for: whole-entity `.Distinct()`; every post-Distinct
  operator (`Where`/`OrderBy`/`Skip`/`Take`/`Count`/aggregate after `Distinct`); represented-key Distinct;
  non-TPH / unresolved-discriminator OfType.
- **Zero-regression** spec sweep (`MONGODB_EF_NATIVE_ONLY=1`, +passing) and full EF8/EF9/EF10 suites green.

## Delivery

Single squashed commit on branch `EF-347`, stacked on `7a9a702` (EF-344/SP6-GroupBy), one PR-style message
per the native-rewrite stacked-PR workflow. Subagent-driven development, stopping after every task. Keep
an `EF-347-presquash` safety branch until merge. Native becoming the default path for these operators and
the emitted MQL are implementation details, not breaking changes (per the versioning rubric) — query
results are unchanged.

## Follow-ups (not in this slice)

- **Later EF-347 slices / a foundational sub-project:** set operations, `SelectMany`, non-canonical
  `Skip`/`Take` — all gated on new sub-pipeline / second-source / non-canonical-order machinery.
- Whole-entity `.Distinct()` and post-`Distinct` operators (once a general post-terminal composition model
  exists).
- **EF-348** — native Atlas `VectorSearch` (`$vectorSearch`), split out of EF-347.
