# Plan — Extract `MongoSelectExpression` from `MongoQueryExpression`

> **Tracking:** JIRA **EF-330** (under program epic EF-322).
> **Scheduling (not yet started):** do this **after SP1 (EF-323) and SP2 (EF-329) merge to main**, and **before the next native feature sub-project (SP3, projection pushdown)** — not on the stacked feature branches. Pure internal refactor, no behavior change, no public-surface break.
> **Group-3 (lookup/inner-collection) extraction is out of scope here and folds into EF-317 / the collection-Includes sub-project (SP5)**, not a separate refactor.

**Status:** ready for implementation (awaiting its scheduling slot per the note above; goes through normal design sign-off before coding)
**Area:** Query (`src/MongoDB.EntityFrameworkCore/Query/`)
**Reviewer:** `query-reviewer`, plus `api-stability-reviewer` (all types involved are `internal`, so no public-surface break — confirm).

## Background

`MongoQueryExpression` is a partial class spread over three files that has accreted four
responsibilities:

1. EF `Expression` node + **fallback-path projection mapping** (`MongoQueryExpression.cs`:
   `_projectionMapping`, `_projection`, `AddToProjection`, `ApplyProjection`, `GetMappedProjection`,
   `ReplaceProjectionMapping`, `CollectionExpression`, `CapturedExpression`).
2. **Native filter/sort/paging IR** (`MongoQueryExpression.NativeSlots.cs`: `Predicate`,
   `AddPredicateConjunct`, `Orderings`/`ResetOrderings`/`AppendOrdering`, `Limit`, `Offset`,
   `IsNativeRepresentable`, and the `Lookups` accessor).
3. **Cross-collection `$lookup` state machine** (`MongoQueryExpression.Lookup.cs`: `_pendingLookups`,
   `_innerCollections`, `GetPendingLookups`, `OrderLookupsByDependency`,
   `GetStreamingReferenceLookups`, `AddLookup`, `InnerCollections`, `IsJoinQuery`,
   `UsesDriverJoinFields`, `AddInnerCollection`).

`NativeSlots.cs`:20-24 explicitly records that group (2) is "what the design document calls
`MongoSelectExpression`", implemented in-place "to avoid churning the QMTEV, shaper, and factory
plumbing." This plan finishes that deferred extraction.

## Scope decision (READ THIS FIRST — it constrains the whole change)

A source census (grep of every member usage across `src/` and `tests/`) shows the three groups have
**very different coupling**:

- **Group (2), the native scalar slots** — `Predicate`, `Orderings`, `Limit`, `Offset`,
  `IsNativeRepresentable` — are touched **only by the native path**: QMTEV `PopulateNativeSlots`,
  `MongoSelectLowerer`, the gate (`MongoShapedQueryCompilingExpressionVisitor.TryBuildNativeFactory`),
  and native unit tests. **Cleanly extractable.**

- **Group (3), the lookup/inner-collection state** — is **shared with the driver-LINQ fallback
  path**. `UsesDriverJoinFields`, `GetPendingLookups`, `AddLookup`, `InnerCollections`,
  `AddInnerCollection` are consumed by the fallback projection-binding visitors
  (`MongoProjectionBindingRemovingExpressionVisitor`, `MongoMixedProjectionBindingRemovingExpressionVisitor`,
  `MongoProjectionBindingExpressionVisitor` + `.Lookup.cs`) and by QMTEV join handling — **not just
  the native path.** It is also flagged `TODO(EF-317)` for removal/reshaping once the driver ships
  native `LeftJoin`.

**Therefore this plan extracts ONLY group (2).** Moving group (3) is deliberately **out of scope** —
it is entangled with the fallback shaper and is slated for separate rework under EF-317. Attempting
to move it here would touch the fallback path and balloon the blast radius. Leave `Lookup.cs` on
`MongoQueryExpression` unchanged.

The result is composition: `MongoQueryExpression` **has-a** `MongoSelectExpression Select`. The
lookup state stays on `MongoQueryExpression`; the native scalar IR lives on `Select`.

## Target design

New file `src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoSelectExpression.cs`:

```csharp
namespace MongoDB.EntityFrameworkCore.Query.Expressions;

/// <summary>
/// The native-translation logical query IR (filter / sort / paging) for a single collection —
/// the "MongoSelectExpression" of the EF-323 design. Populated by the QMTEV, read by the gate and
/// the lowerer. Dialect-neutral: holds MongoExpression nodes, never BSON.
/// </summary>
internal sealed class MongoSelectExpression
{
    private readonly List<MongoOrdering> _orderings = [];

    public MongoExpression? Predicate { get; set; }
    public void AddPredicateConjunct(MongoExpression conjunct) => /* unchanged body */;

    public IReadOnlyList<MongoOrdering> Orderings => _orderings;
    public void ResetOrderings(MongoOrdering first) { _orderings.Clear(); _orderings.Add(first); }
    public void AppendOrdering(MongoOrdering next) => _orderings.Add(next);

    public MongoExpression? Limit { get; set; }
    public MongoExpression? Offset { get; set; }

    public bool IsNativeRepresentable { get; set; } = true;
}
```

Design notes for the implementer:

- **Do NOT derive from `System.Linq.Expressions.Expression`.** The `MongoExpression` base already
  ships dead/inconsistent visitor plumbing (`VisitChildren` no-ops); do not repeat that here. This is
  a plain data-holder — a plain `internal sealed class` is correct despite the "Expression" name
  (kept for continuity with the design doc). Add a short comment saying so.
- **The `Lookups` accessor stays on `MongoQueryExpression`** (it recomputes from group-3 lookup
  state via `GetStreamingReferenceLookups()`). Do not move it to `Select`.
- Move `MongoBinaryOperator`/`MongoUnaryOperator` are NOT involved (they live in `MongoExpression.cs`).

`MongoQueryExpression` gains, in `MongoQueryExpression.cs`:

```csharp
public MongoSelectExpression Select { get; } = new();
```

Then **delete `MongoQueryExpression.NativeSlots.cs` entirely** (its members move to
`MongoSelectExpression`), and change the base class declaration from
`internal sealed partial class MongoQueryExpression : Expression` — it stays partial because
`Lookup.cs` remains a partial file. (Keep `partial`.)

## Step-by-step

### 1. Create `MongoSelectExpression.cs`
Move the five slot members verbatim from `NativeSlots.cs` (bodies unchanged). Preserve the file BOM
and license header. Do not include `Lookups`.

### 2. Wire composition into `MongoQueryExpression.cs`
Add `public MongoSelectExpression Select { get; } = new();`. Delete `NativeSlots.cs`. Keep the
`Lookups` accessor by moving it from `NativeSlots.cs` **into `Lookup.cs`** (it belongs with the
lookup state it recomputes — `GetStreamingReferenceLookups`).

### 3. Update the writer: QMTEV `PopulateNativeSlots`
`Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs`. All native-slot writes go
through `.Select`. Exact sites (from census):
- L178: `mongoQueryExpression.IsNativeRepresentable = false;` → `.Select.IsNativeRepresentable`
- L540: `((MongoQueryExpression)source.QueryExpression).IsNativeRepresentable = false;` → `...).Select.IsNativeRepresentable`
- L619, 627, 636, 645, 652, 661, 668, 674, 681, 687, 698: `mongoQ.IsNativeRepresentable` → `mongoQ.Select.IsNativeRepresentable`
- L625: `mongoQ.AddPredicateConjunct(...)` → `mongoQ.Select.AddPredicateConjunct(...)`
- L643: `mongoQ.ResetOrderings(...)` → `mongoQ.Select.ResetOrderings(...)`
- L659: `mongoQ.AppendOrdering(...)` → `mongoQ.Select.AppendOrdering(...)`
- L666, 672-674, 679, 685-687, 707: `mongoQ.Offset`/`mongoQ.Limit` → `mongoQ.Select.Offset`/`.Select.Limit`
  (includes `PagingAlreadyApplied` at L707).

`AddInnerCollection`/`AddLookup`/`InnerCollections` calls in QMTEV (L777, 844, 876, 889, 893, 904)
are group-3 — **leave unchanged.**

### 4. Update the gate: `MongoShapedQueryCompilingExpressionVisitor.cs`
- L395: `!mongoQueryExpression.IsNativeRepresentable` → `!mongoQueryExpression.Select.IsNativeRepresentable`
- L408: same `.Select.IsNativeRepresentable`
- L459, 463-464, 514, 516, 524 (`GetStreamingReferenceLookups`, `IsJoinQuery`, `InnerCollections`,
  `GetPendingLookups`) are group-3 — **leave unchanged.**

### 5. Update the reader: `MongoSelectLowerer.cs`
Signature currently `Lower(MongoQueryExpression select)`. Keep taking `MongoQueryExpression` (it
still needs `Lookups`/`IsJoinQuery`/`InnerCollections`, which stay on the parent). Rename the
parameter `select` → `query` for clarity, then:
- L54, 56: `select.Predicate` → `query.Select.Predicate`
- L60, 62: `select.Orderings` → `query.Select.Orderings`
- L66, 68: `select.Offset` → `query.Select.Offset`
- L72, 74: `select.Limit` → `query.Select.Limit`
- L89 `select.Lookups`, L93 `select.IsJoinQuery`/`select.InnerCollections`, and `AppendLookupStages`
  param → these are group-3, read from the parent: `query.Lookups`, `query.IsJoinQuery`,
  `query.InnerCollections`. (Not `.Select`.)

> Note: fully migrating `Lower` to take a `MongoSelectExpression` (so the lowerer never sees the
> query node) is a **Phase 2** follow-up gated on group-3 extraction; do not attempt it here.

### 6. Update unit tests
Native-slot tests construct a `MongoQueryExpression` and call the slots directly; redirect through
`.Select`. **Do not add convenience pass-through members on `MongoQueryExpression`** — that would
re-create the god-object surface this change removes.
- `tests/.../Query/NativeTranslation/MongoSelectExpressionTests.cs`: L50-51 `select.AddPredicateConjunct`
  → `select.Select.AddPredicateConjunct`; L53 `select.Predicate` → `select.Select.Predicate`; L59
  `TestSelect().IsNativeRepresentable` → `.Select.IsNativeRepresentable`. Consider having
  `TestSelect()` return the `MongoSelectExpression` directly (rename to reflect it), or keep returning
  the query and dereference `.Select` at call sites — implementer's choice, but keep it readable.
- `tests/.../Query/NativeTranslation/MongoSelectLowererTests.cs`: L59-62, 79, 94, 99, 108-109,
  115, 125, 131, 141, 147, 157, 162 — every `select.AddPredicateConjunct`/`.AppendOrdering`/`.Offset`/
  `.Limit`/`.Orderings` → `.Select.*`. (The `sortStage.Orderings`/`skipStage.Offset`/`limitStage.Limit`
  reads at L115/131/147/162/99 are on the **stage** objects — leave unchanged.)
- `tests/.../Query/NativeTranslation/SlotPopulationTests.cs`: L135, 136, 148-150, 160, 171
  (`mongoQ.Predicate`/`.IsNativeRepresentable`/`.Orderings`) → `.Select.*`.
- `MongoPipelineStageTests.cs` (`stage.Predicate/Orderings/Offset/Limit`) and
  `StreamingReferenceLookupsTests.cs` (all group-3) — **no changes.**

### 7. Docs
- Update `NativeSlots.cs`'s old header note (now gone) — instead add the rationale comment to
  `MongoSelectExpression.cs`.
- `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md`: the "Key entry points" bullet for
  `Expressions/MongoQueryExpression (+ .NativeSlots.cs, .Lookup.cs)` should now read
  `(+ .Lookup.cs)` and mention `MongoSelectExpression` as the native IR reached via
  `MongoQueryExpression.Select`. Update the "We extended this type in place rather than adding a
  separate `MongoSelectExpression`" sentence — it's now false.

## Verification

- Build all three EF targets: `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"` (then EF9,
  EF8). The change is EF-version-agnostic (no `#if`), so a green EF10 build + one other is sufficient
  signal, but build all three before completion.
- `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build --filter "FullyQualifiedName~Query"`
  — unit + functional query tests.
- Prove the native path still routes natively (the only reliable signal per AGENTS.md): run the
  `QueryModeGate*` functional tests and the spec suite under `MONGODB_EF_NATIVE_ONLY=1`; the
  pass/fail set must be **identical** to pre-change (this refactor changes no behavior).
- `git grep -n 'NativeSlots'` must return nothing after the change (file deleted, comments updated).

## Risks / gotchas

- **Group-3 entanglement is the trap.** If you find yourself editing a `MongoProjectionBinding*`
  visitor, stop — you've strayed into the fallback lookup state that this plan intentionally leaves
  put. Only the native-scalar-slot call sites listed above should change.
- **No behavior change.** This is a pure move + indirection. Any test whose *result* changes
  indicates a mistranslated call site (e.g. a missed `.Select`), not an intended effect.
- **Preserve file BOMs and license headers** on the new file (repo requirement).
- `MongoQueryExpression` stays `partial` (because `Lookup.cs` remains). Don't drop the modifier.
- The `Select` property is initialized inline (`= new()`) and read-only — the QMTEV mutates the
  `MongoSelectExpression`'s properties, it never reassigns `Select`. Keep it get-only.

## Out of scope (explicit non-goals)

- Moving the `$lookup`/inner-collection state machine (`Lookup.cs`) — deferred to EF-317.
- Changing `MongoSelectLowerer` to take `MongoSelectExpression` instead of `MongoQueryExpression`.
- Touching the dead `MongoExpression : Expression` visitor plumbing, the renderer, or the pipeline
  factory.
- Any fallback-path (driver-LINQ) code.
