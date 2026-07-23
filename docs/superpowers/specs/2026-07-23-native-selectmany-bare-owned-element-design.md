# EF-347 — Native bare owned-collection-element `SelectMany` result

Date: 2026-07-23
Branch: (stacked off `origin/NativeQueryOngoing` tip `bcf5b30`)
Epic: EF-322 native LINQ query rewrite · Ticket: EF-347 (remaining SP6 relational operators)

## Summary

Make a **bare whole owned-collection-element** result from `SelectMany` go native — the
canonical `from o in q from i in o.Items select i` (and equivalents), where `Items` is an
**embedded owned collection** and the query returns the whole element entity, not a projection.

Today this exact shape throws `NotSupportedException` at translation time in **every**
`MongoQueryMode` (`MongoQueryableMethodTranslatingExpressionVisitor.TranslateSelect`, the
whole-inner-entity decline at lines ~305–314). This slice replaces that decline, for the
whole-**inner**-element case only, with a native `$unwind` → `$replaceRoot` pipeline plus a
root-level entity shaper for the unwound element.

This is the first of the deferred SelectMany "bare-entity result" shapes; reference
(cross-collection) bare-entity is a separate follow-up that will reuse the shaper built here.

## Scope

### In scope

A bare whole owned-collection **element** result, native, in all three user spellings that
EF's nav-expansion normalizes to the identical tree (`UnwindSource` set, `Projection` empty,
trailing selector `ti => ti.Inner`):

- `q.SelectMany(o => o.Items)` (1-arg)
- `from o in q from i in o.Items select i` (query syntax)
- `SelectMany(o => o.Items, (o, i) => i)` (explicit result selector, bare inner)

`Items` is an embedded **owned** collection navigation. **Terminal-only.** DOM materialization
(owned/collection elements are streaming-ineligible, gated off by `StreamingEligibility`),
consistent with every prior SelectMany slice.

### Explicitly deferred (unchanged by this slice)

- **`select o`** — the whole **outer** entity, replicated once per element (trailing selector
  `ti => ti.Outer`). Different shaper (the owner document, no `$replaceRoot`). The existing
  `NotSupportedException` is **narrowed** to fire for this case (and any other non-`Inner`
  whole-entity trailing selector) only.
- **Reference** (cross-collection, non-owned) bare-entity result — a follow-up slice; it will
  reuse this slice's root-level element shaper over a `$lookup` + inner-join `$unwind` source.
- **Computed projection leaf**, **filtered / correlated-beyond-FK inner**, **nested SelectMany**
  — all keep their current behavior.
- **Any operator composed after** the bare-entity SelectMany — keeps hard-failing via the
  existing SelectMany-after-terminal guard (`TranslateSelectMany` returns `null` when
  `HasTerminalOperator`; committed as `4e30ad2`).

### Behavior change — additive, not a break

The whole-inner-entity shape hard-failed (`NotSupportedException`) in every mode before this
slice; it now goes native with correct results. Per the versioning rubric (AGENTS.md):
hard-fail → native, with unchanged query results, is not a breaking change, and the emitted MQL
is not part of the contract. All touched types are `internal`.

## Approach (Approach A — `$replaceRoot` + root-level entity shaper)

Chosen over B (bespoke re-rooted shaper) and C (no `$replaceRoot`, materialize the element in
its natural nested slot): A produces the natural MongoDB shape (`$unwind` → `$replaceRoot`) and
reuses the standard whole-entity DOM shaper, just pointed at root. B is the pivot if the
standard shaper cannot be made to read the owned type from root; the spike (Task 1) decides.

### 1. Recognition — QMTEV

Most of the shaper wiring already exists:

- `NativeSelectManyBinder.TryBindBareNavUnwind` already fires for `o => o.Items`, sets
  `MongoSelectDefinition.UnwindSource` (Owned), and leaves `Projection` empty.
- `BuildBareNavWrappedShaper` already builds a `StructuralTypeShaperExpression` for the owned
  element (`UnwindSource.InnerEntityType`) via a root `ProjectionBindingExpression`
  (`new ProjectionMember()`, `ValueBuffer`), wrapped as `TransparentIdentifier(Outer, Inner)`.
- For the trailing `ti => ti.Inner` select, the generic fold at `TranslateSelect` lines
  ~319–321 already resolves `TransparentIdentifier(outer, item).Inner` → the element shaper.

The change is to **stop throwing and route to native** at the lines ~305–314 decline site:

- When `UnwindSource != null && Projection.Count == 0` and the trailing selector is the
  **whole-inner** shape (`ti => ti.Inner`), set a new provenance flag on the unwind source
  (`MongoUnwindSource.WholeElement = true`) and fall through to the generic fold (which produces
  the element shaper) instead of throwing.
- The **whole-outer** shape (`ti => ti.Outer`) and any other non-`Inner` whole-entity trailing
  selector keep throwing the (now narrowed) `NotSupportedException`.

`Route` stays `NativeRoute.WholeEntity` (`UnwindSource` set, `Projection` empty), so the gate
goes native with **no new disposition signal** — `WholeElement` only tells the lowerer to emit
`$replaceRoot` and (via the existing `HasTerminalOperator` membership of `UnwindSource != null`)
keeps the shape terminal-only.

Distinguishing `Inner` from `Outer`: the existing `IsTransparentIdentifierMemberAccessSelector`
already recognizes the `ti => ti.<Member>` family; this slice adds a check on which member
(`Inner` vs. `Outer`).

### 2. Lowering — `MongoSelectLowerer` + `MongoPipelineFactory`

After the owned `$unwind` (`MongoUnwindFieldStage` on the embedded array element path), when
`UnwindSource.WholeElement` is set, append a `$replaceRoot: { newRoot: "$<unwindPath>" }` so each
unwound element becomes the root document.

- New typed stage `MongoReplaceRootStage` (`NativeTranslation/Stages/`) carrying the new-root
  field path, rendered by a new `MongoPipelineFactory` arm — mirroring the inline `$replaceRoot`
  already produced by `MongoPipelineFactory.RenderSetDifference`.
- The `Lower` `UnwindSource` block emits `MongoUnwindFieldStage` (owned, as today) then, for
  `WholeElement`, `MongoReplaceRootStage`. The projected owned SelectMany path (`Projection`
  populated) is unaffected — it still emits `$unwind` → `$project` with no `$replaceRoot`.

### 3. Materialization (spike-gated) — the crux

After `$replaceRoot`, the owned element is the root document. The element's
`StructuralTypeShaperExpression` must materialize the owned entity type from **root**, reading
its properties by their own element names — instead of crashing looking for an owning-entity
`bsonDoc` context (the `KeyNotFoundException("… 'bsonDoc' …")` the current fallback hits).

**Task 1 is a spike** to determine, against the real code:

- How `MongoProjectionBindingRemovingExpressionVisitor` (and the DOM entity materializer it
  drives) is keyed for an owned `StructuralTypeShaperExpression`, and the minimal change to make
  it read the owned type from the root `BsonDocument` rather than a nested owner sub-document.
- EF Core's **semantics** for a bare owned-entity result: whether EF tracks it, forces
  no-tracking, or requires the query/entity marked a particular way — and whether any provider
  action is needed to match EF's expectation. (Owned entities are dependents and normally cannot
  be tracked independently of their owner; the tree nonetheless reaches our translator with a
  `StructuralTypeShaperExpression` for the element, so EF is willing to shape it — the spike
  confirms the tracking contract.)

The spike's outcome confirms Approach A (standard shaper pointed at root) or pivots to Approach
B (a bespoke re-rooted owned shaper reading properties from root explicitly). Either way the
lowering and recognition above are unchanged.

## Verification

**No driver-LINQ oracle.** The whole-inner-entity fallback crashes with the `'bsonDoc'`
`KeyNotFoundException` too (documented in the QMTEV decline comment), so there is no working
driver-LINQ baseline. Correctness is proven **against expected in-memory results under
`MongoQueryMode.NativeOnly`** — the same no-oracle pattern as reference SelectMany (slice 5) and
Intersect/Except (set-ops slice A): assert the native query succeeds under `NativeOnly` and its
result set equals the expected elements.

Functional tests (`tests/.../FunctionalTests/Query/NativeSelectManyTests.cs`), reusing the
existing owned `Owner`/`Item` fixture where possible:

- All three spellings (1-arg, query-syntax, explicit-result-selector) return the flattened
  element set, native under `NativeOnly`.
- An owner with **zero** items contributes zero rows (inner-flatten semantics — every prior
  owned SelectMany slice asserts this).
- Elements with **nested owned members** materialize their full subtree.
  > **AS-BUILT NOTE (2026-07-23):** found infeasible in this slice, per user decision at the
  > Task-3 review (accepted narrowed scope) — kept here, not deleted, as the record of original
  > intent. A nested owned reference/collection under the element does NOT materialize via this
  > mechanism: the re-rooted element shaper still binds through the query's root
  > `ProjectionMember`, which resolves to the OUTER (owner) entity's own `EntityProjectionExpression`,
  > not the re-rooted element's — so a nested navigation reaches EF's own auto-`IncludeExpression`
  > machinery and throws `InvalidOperationException`. `IsWholeElementRepresentable`
  > (`Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs`) declines this shape
  > cleanly at translation time instead (a `NotSupportedException`, in every `MongoQueryMode`),
  > converting that confusing runtime crash into the same clean decline every other unsupported
  > whole-entity shape gets. Deferred to a follow-up ticket, not delivered here. The slice as
  > shipped covers **flat/scalar owned elements** (no navigations of their own) — including
  > **shared-type** owned collections (one owned CLR type reused by multiple owners) — see
  > `NativeSelectManyTests.Bare_owned_whole_inner_element_with_nested_owned_reference_member_declines_cleanly`
  > and `Bare_owned_whole_inner_element_over_shared_clr_type_goes_native`.
- A shared outer/inner member name proves the element reads root-relative (no owner-scope leak).
- `select o` (whole outer) still throws (narrowed decline).
- Composition after the bare-entity SelectMany still hard-fails in every mode.

**Suite bar:** full 3-version `/test-all` (EF8/EF9/EF10) green with 0 failures before squash,
plus a `NativeOnly` spec sweep showing the change is purely additive (zero regressions).

## Files (anticipated)

- `Query/Expressions/MongoUnwindSource.cs` — `WholeElement` flag.
- `Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs` — narrow the
  whole-inner-entity decline; route `ti => ti.Inner` to native.
- `Query/NativeTranslation/Stages/MongoReplaceRootStage.cs` — new typed stage.
- `Query/NativeTranslation/MongoSelectLowerer.cs` — emit `$replaceRoot` for `WholeElement`.
- `Query/NativeTranslation/MongoPipelineFactory.cs` — render `MongoReplaceRootStage`.
- Materializer file(s) per the spike outcome (`MongoProjectionBindingRemovingExpressionVisitor`
  and/or a bespoke shaper) — determined by Task 1.
- `Query/AGENTS.md` — as-built note under the owned SelectMany section.
- `tests/.../FunctionalTests/Query/NativeSelectManyTests.cs` — functional coverage.

## Risks

- **Owned materialization from root** (primary) — de-risked by the Task 1 spike before any
  production change; Approach B is the pivot.
- **EF tracking semantics for a bare owned entity** — resolved by the spike; may require marking
  the result no-tracking or similar. Multi-version (EF8/EF9/EF10) behavior must be checked (a
  prior slice hit an EF8-only `CS9174` / API-shape difference — run the full 3-version build
  early).
- **`select o` / whole-outer regression** — the narrowed decline must still fire for every
  non-`Inner` whole-entity trailing selector; covered by a retained test.
