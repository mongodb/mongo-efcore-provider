# Native query: SelectMany over an owned collection → projection (EF-347 slice 3)

**Ticket:** EF-347 (remaining SP6 relational operators) — SelectMany slice 1 (owned-collection, projected).
Epic EF-322. (Slice 1 = OfType + projected Distinct; slice 2 = set-ops Union/Concat.)
**Type:** New native coverage, **purely additive**. SelectMany is entirely unsupported today (it throws in
*every* mode); this makes one shape succeed natively. Nothing that works today changes.
**Stacked on:** EF-347 slice 2 set-ops (`750268a`), the current native-stack tip.

## Post-probe revision (2026-07-15) — scope narrowed to the inner-`Select` form

Task 1's implementation probe (`.superpowers/sdd/task-1-report.md`) found that EF normalizes **both**
SelectMany forms to `SelectManyWithCollectionSelector` with a **trivial `TransparentIdentifier(Outer,Inner)`**
result selector — so the design's original "two-scope binding reads the user result selector inside
`TranslateSelectMany`" mechanism does not match reality. The user projection arrives either as a **separate
subsequent `.Select(ti => …)`** (explicit-result-selector / query-syntax form) or **nested inside the
collection selector** as `Property(o,Nav).AsQueryable().Select(i => proj)` (inner-`Select` form).

**This slice is narrowed to the inner-`Select` form only** (user decision): `TranslateSelectMany` parses the
nested `Select`'s member-access projection directly (outer closed-over param → `$field`, inner `Select` param
→ `$<unwindPath>.field`), sets `UnwindSource` + `Projection`, returns a projected shaped query — self-contained,
no transparent-identifier / shared-`NativeProjectionBinder` machinery. The **explicit-result-selector /
query-syntax form is deferred** (returns `null` → hard-fails, unchanged) to a later slice that adds
transparent-identifier handling. The `$unwind` element path accessor is confirmed:
`navigation.TargetEntityType.GetContainingElementName()`. The IR/stage/lowerer/renderer plumbing (Task 1,
committed) is unaffected. The implementation plan reflects this narrowed approach; the sections below describe
the original (broader) framing and are superseded on these two points.

## Background (established by a spike — `.superpowers/sdd/selectmany-spike.md`)

`SelectMany` is **entirely unsupported** in the provider today. Both `TranslateSelectMany` overloads return
`null`, so EF Core's **base** `QueryableMethodTranslatingExpressionVisitor` fails translation with
`InvalidOperationException: "… could not be translated"` **before** the provider's native-vs-driver gate is
ever reached. Verified identical under default `Native`, explicit `DriverLinq`, and `NativeOnly` — the
driver's LINQ v3 provider never sees these queries. Consequences that shape this slice:

1. **No parity baseline.** Unlike set-ops (where driver-LINQ worked), there is no `Native == DriverLinq` to
   match. The feature is additive; correctness is verified against **expected in-memory results**.
2. **No graceful fallback.** SelectMany is native-or-hard-fail today. An unsupported SelectMany shape must
   **keep hard-failing** (`TranslateSelectMany` returns `null` → base `NotTranslated`), exactly as today — a
   graceful driver-LINQ fallback is not available (the driver path can't translate SelectMany either).
3. **Bare owned entities as top-level results are out of scope.** Owned entity types are owner-dependent;
   whether EF-Mongo can materialize owner-detached owned entities as top-level results is an unresolved
   unknown (unobservable via the spike, since it never translates). This slice **never materializes a bare
   owned entity** — it returns a projection — sidestepping that unknown entirely.

## Scope

**In:** SelectMany over an **owned (embedded) collection** whose result is a **terminal member-access
projection over the outer and inner elements**:
- `q.SelectMany(o => o.Items, (o, i) => new { o.Name, i.Price })` (result-selector overload), and
- the inner-`Select` form `q.SelectMany(o => o.Items.Select(i => new { … }))`,

where the projection body is a `new {…}` / DTO construction over **top-level member accesses** of the outer
(`o`) and/or inner (`i`) parameters only. Emits:
```
<outer $match/$sort/$skip/$limit>, { $unwind: "$Items" }, { $project: { Name: "$Name", Price: "$Items.Price" } }
```

**Out → hard-fail (`TranslateSelectMany` returns `null`; unchanged from today, so no regression):**
- Bare owned-entity result (`SelectMany(o => o.Items)` returning `Item` instances).
- Cross-collection **reference** collection navs (need `$lookup` + `$unwind` — a later slice reusing slice
  2's nested-pipeline machinery).
- **Correlated** inner (`SelectMany(o => o.Items.Where(i => i.X > o.Y))`) or any inner operator beyond a
  single member-access `Select`.
- Computed / non-member-access projection leaves (`i.Price * 2`, method calls) — mirrors the SP3 projection
  boundary.
- **Non-terminal** — any operator after the SelectMany (`SelectMany(...).Where(...)`, `.Count()`, etc.).
- Nested / multi-level unwind (`SelectMany` of a collection-of-collections; chained SelectMany).

## MQL target

`ctx.Set<Owner>().Where(o => o.Active).SelectMany(o => o.Items, (o, i) => new { o.Name, i.Price })`:
```
[ { $match: { Active: true } },
  { $unwind: "$Items" },
  { $project: { _id: 0, Name: "$Name", Price: "$Items.Price" } } ]
```
(`Items` is the owned collection's element name from `IProperty.GetElementName()`-equivalent for the
navigation; after `$unwind`, `$Items` is a single element, so an inner member `i.Price` renders `$Items.Price`
and an outer member `o.Name` renders `$Name`.)

## Design

### IR

- `MongoSelectDefinition` gains `internal MongoUnwindSource? UnwindSource { get; set; }` — a small holder:
  the owned-collection **element path** to unwind (e.g. `"Items"`), and enough scope info for the projection
  binder to resolve the inner parameter. (No new `Route` value: the result-selector output populates the
  existing `Projection` slot, so `Route` resolves to `NativeRoute.Projection`, and the query compiles through
  the existing projection path with the existing DOM projection shaper.)
- `IsSelectMany` / `UnwindSource != null` joins the post-terminal guard `HasTerminalOperator`
  (`IsGroupBy || IsDistinct || IsSetOp || Grouping != null || UnwindSource != null`) so any operator after
  the SelectMany falls back (terminal-only), consistent with set-ops/GroupBy/Distinct.

### QMTEV — `TranslateSelectMany`

A new `NativeSelectManyBinder.TryBind(mongoQueryExpression, collectionSelector, resultSelector)` (mirroring
`NativeGroupByBinder`/`NativeSelectManyBinder` siblings) drives it:
1. Recognize the **collection selector** as a single-level **owned-collection navigation** off the source
   entity (`INavigation.IsCollection && TargetEntityType.IsOwned()`, single level, no filter/correlation).
   Reject (→ `null`) a reference nav, a correlated/filtered inner, or a multi-level path.
2. Recognize the **result selector** as a terminal member-access projection over the outer and inner
   parameters (same acceptance set as the SP3 `NativeProjectionBinder`, extended to two scopes). Reject
   (→ `null`) computed leaves, entity-valued results (incl. a bare inner-entity result), or mixed shapes.
3. On success: set `mongoQueryExpression.Select.UnwindSource` (the owned element path) and populate
   `Select.Projection` via the two-scope binder; build and return the projected `ShapedQueryExpression`
   directly (the base `TranslateSelectMany` is abstract for the collection-selector overload — construct the
   shaped query, mirroring `TranslateGroupBy`).
4. On any rejection: return `null` → EF Core base → `NotTranslated` (hard-fail, unchanged from today).

**Dispatch wiring (confirmed by the plan's first step).** `SelectManyWithCollectionSelector` is already in
the `VisitMethodCall` switch's bubble-through group (→ `base.VisitMethodCall` → the override). The exact
overload and post-nav-expansion expression shape EF hands to `TranslateSelectMany` (which overload carries
the owned-collection SelectMany; whether the inner-`Select` form arrives as the with- or
without-collection-selector overload) is confirmed by a first-plan-step probe, exactly like slice 2's
driver-baseline probe. The mechanism below is independent of which overload carries it.

### Two-scope projection binding (the core new logic)

Extend `NativeProjectionBinder` (or a thin `NativeSelectManyBinder` wrapper over it) so the result selector's
member accesses bind against **two** scopes:
- **outer** parameter member `o.Name` → field ref `$Name` (root document), and
- **inner** parameter member `i.Price` → field ref `$<unwindPath>.Price` (the unwound element).

Everything else is reused from EF-331: the `MongoProjection` alias/field-ref IR, the `$project` render
(`MongoPipelineFactory` `RenderProject`), and the DOM projection shaper
(`MongoProjectionBindingRemovingExpressionVisitor`) reading each alias.

### Stage + lowerer + renderer

- New stage `NativeTranslation/Stages/MongoUnwindFieldStage` holding the element path — distinct from the
  existing `LookupExpression`-tied `MongoUnwindStage` (that one is for reference-Include joins; the owned
  unwind needs no lookup). Renders to `{ $unwind: "$<path>" }`.
- `MongoSelectLowerer`: when `select.UnwindSource != null`, emit the outer canonical stages (via the existing
  `AppendCanonicalStages`), then `MongoUnwindFieldStage`, then the `$project` (from `Select.Projection`) —
  in that order (unwind before project, since the project reads unwound fields).
- `MongoPipelineFactory`: a `RenderUnwindField` case → `{ $unwind: "$<path>" }` (a preserveNullAndEmptyArrays
  decision — default `false`, matching SelectMany's inner-join flatten semantics: owners with an empty
  collection contribute no rows; confirm against expected results in the plan).

### Shaper

Unchanged. The result is an anonymous/DTO projection, so the existing DOM projection shaper materializes each
`$project`ed row — no owned-entity materialization, no new shaper.

## Correctness hazards (explicitly guarded)

1. **Post-SelectMany composition** (the recurring "operator after a new terminal" wrong-data class):
   `UnwindSource` joins `HasTerminalOperator`, so `SelectMany(...).Where(...)` / `.Count()` / `.OrderBy(...)`
   etc. fall back (hard-fail, unchanged). Regression tests must lock each seam.
2. **Owned vs reference detection:** the binder must accept *only* owned (embedded) single-level collection
   navs; a reference nav must reject (→ hard-fail), because a reference nav needs `$lookup` (not `$unwind`) —
   emitting `$unwind "$RefNav"` over a non-embedded nav would read a field that isn't there and silently
   produce wrong/empty results.
3. **`$unwind` empty/missing semantics:** `SelectMany` flattens with inner-join semantics (an owner with no
   items contributes no rows). Plain `$unwind` (no `preserveNullAndEmptyArrays`) matches this. Verify against
   expected results, including owners with an empty and with an absent collection.
4. **Inner-scope binding correctness:** an inner member must render `$<unwindPath>.<field>`, never a
   root-level `$<field>` (which could collide with a same-named outer field) — a two-scope binding bug would
   silently read the wrong field. Test a result selector where outer and inner share a member name.

## Non-goals (deferred follow-ups)

- Bare owned-entity SelectMany (`SelectMany(o => o.Items)` returning `Item`s) — needs an owned-entity-as-
  top-level-result shaper + tracking semantics; its own investigation.
- Cross-collection **reference** SelectMany (`$lookup` + `$unwind`, reusing slice 2's nested-pipeline
  machinery).
- **Correlated** / filtered inner SelectMany (correlated `$lookup` pipeline with `let`).
- Scalar (`SelectMany(o => o.Items.Select(i => i.Price))` → bare scalars) and computed projection leaves.
- Multi-level / chained SelectMany.

## Verification

- **Expected-results correctness** (no parity baseline): supported owned-projection SelectMany returns the
  correct flattened rows (asserted against expected in-memory data) — including owners with empty/absent
  collections (inner-join semantics), and a same-named-outer/inner-member case (hazard 4).
- **`NativeOnly` proves native:** the supported shape succeeds under `MongoQueryMode.NativeOnly`; every
  out-of-scope shape (bare owned entity, reference nav, correlated inner, computed leaf, non-terminal,
  nested) **hard-fails** (assert it still throws — no regression, and not newly wrong-data).
- **MQL assertion:** the supported query emits `$unwind` + `$project` in that order with the correct field
  paths (`$<inner>` under the unwind path, `$<outer>` at root).
- **Full 3-version `/test-all`** green (EF8/EF9/EF10); `MONGODB_EF_NATIVE_ONLY=1` spec sweep shows the
  intended native-coverage delta with zero regressions.
- **First-plan-step probe:** confirm the exact overload + post-nav-expansion expression shape EF hands to
  `TranslateSelectMany` for an owned-collection SelectMany-with-projection, so the binder/dispatch wire to
  the right override.

## Files touched (anticipated)

- `src/…/Query/Expressions/MongoUnwindSource.cs` (new IR holder).
- `src/…/Query/Expressions/MongoSelectDefinition.cs` (`UnwindSource` field; extend `HasTerminalOperator`).
- `src/…/Query/NativeTranslation/NativeSelectManyBinder.cs` (new; owned-collection + two-scope projection binding).
- `src/…/Query/NativeTranslation/NativeProjectionBinder.cs` (two-scope extension) — or fold into the binder above.
- `src/…/Query/NativeTranslation/Stages/MongoUnwindFieldStage.cs` (new stage).
- `src/…/Query/NativeTranslation/MongoSelectLowerer.cs` (emit unwind + project when `UnwindSource` set).
- `src/…/Query/NativeTranslation/MongoPipelineFactory.cs` (`RenderUnwindField`).
- `src/…/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs` (`TranslateSelectMany` → binder; whitelist entry if needed).
- `src/…/Query/AGENTS.md` (as-built note + terminal-guard invariant update).
- Tests: unit (binder/lowerer/renderer for `$unwind`+`$project`, two-scope binding) + functional (expected-results, `NativeOnly` succeed/throw, composition-seam hard-fails, empty/absent collection, shared member name).

## Follow-ups

Bare owned-entity SelectMany (+ its shaper R&D); cross-collection reference SelectMany; correlated/filtered
inner; scalar/computed projections; multi-level/chained SelectMany.
