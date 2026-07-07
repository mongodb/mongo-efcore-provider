# EF-332 — Separate the native-translation layer from the EF QMTEV

**JIRA:** EF-332 (Task, epic EF-322 — native LINQ query provider)
**Type:** Pure internal refactor. No behavior change, no public-surface break.
**Stacking:** Branch `EF-332`, cut off `EF-331` (SP3 tip). Chain: `main → EF-323-impl → EF-329 → EF-330 → EF-331 → EF-332`.

## Motivation

The EF-331 (projection-pushdown) code review surfaced a set of interrelated separation-of-concerns
problems introduced across EF-323 / EF-329 / EF-331:

1. **Native-translation logic has accreted inside the EF Core query dispatcher.**
   `MongoQueryableMethodTranslatingExpressionVisitor` (the QMTEV) now carries
   `TryPopulateNativeProjection` (projection shape-walk, alias-collision detection, `$project` alias
   semantics) and the seven-arm `PopulateNativeSlots` (+ its catch-all and `IsNativeRepresentableSlotOperator`
   whitelist). These are native-translation concerns living inline in the EF dispatcher — it is drifting
   toward a god-class straddling two layers.

2. **Value serialization is duplicated across the two dialect renderers, and has already drifted.**
   `MongoAggregationExpressionRenderer.RenderValue`/`SerializeConstant` re-implement
   `MongoQueryLanguageRenderer.RenderValue`/`ToBsonValue`. A code comment admits it must "serialize exactly
   as in the query renderer" — a correctness coupling enforced only by hand. The copies have **already
   diverged**: the query renderer's `ToBsonValue` wraps serializer failures in
   `NativeTranslationNotSupportedException` (mapping `InvalidCastException`/`FormatException`/
   `OverflowException`/`InvalidOperationException`); the aggregation renderer's `SerializeConstant` has **no**
   such wrapper, so a field-to-field/arithmetic constant that fails serialization throws a raw
   `InvalidCastException` instead of the diagnostic exception.

3. **The is-native decision keys off two signals that can disagree.**
   Projection routing in `MongoShapedQueryCompilingExpressionVisitor` reads *both* `queryMode` +
   `Select.Projection.Count` *and* `Select.IsNativeRepresentable`. A comment (`MongoShapedQueryCompilingExpressionVisitor.cs:184`)
   concedes the `IsNativeRepresentable` flag "does not always flip" for pushed-down projections, so the
   projected-path gate deliberately keys off routing state rather than the flag. Two signals for one decision
   invite silent-wrong-result drift.

4. **`MongoPipelineFactory.RenderProject` instantiates a fresh renderer** (`new MongoAggregationExpressionRenderer()`)
   instead of using the threaded renderer, and `MongoQueryLanguageRenderer` holds its own separate
   `_aggRenderer` instance.

All affected types are `internal`. Per the AGENTS.md versioning rubric, which internal execution path a
supported query takes — and the exact MQL emitted — is not part of the public contract, so this refactor is
not a breaking change.

## Goals / Acceptance

- The QMTEV no longer contains native-slot / projection-translation bodies — it delegates to extracted types.
- The two dialect renderers share **one** value-rendering path (with the diagnostics wrapper unified).
- The is-native decision has **one** authoritative, documented predicate.
- No behavior change: full FunctionalTests + SpecificationTests green in `Native` mode on EF8/EF9/EF10;
  the `MONGODB_EF_NATIVE_ONLY=1` pass-set is **unchanged** vs. the EF-331 baseline.

## Out of scope

- Dropping the `MongoExpression : System.Linq.Expressions.Expression` base (a separate judgment call —
  revisit only if/when a visitor-driven transform needs it; deferred to later sub-projects SP4–7).
- Converting `MongoQueryLanguageRenderer` itself to a static class. It stays an instance threaded through
  `MongoPipelineFactory.Create` (existing seam, out of scope). Only the *aggregation* renderer goes static
  (see §4) because it becomes stateless.
- Any change to which queries go native or to emitted MQL.

## Design

### 1. Extract slot/projection population out of the QMTEV — two new types

Both are `static` classes under `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/`, mirroring the
current `static` shape of the methods being moved. Each operates on the `MongoQueryExpression` /
`MongoSelectDefinition` passed in as arguments (no state).

- **`NativeSlotPopulator`** — owns:
  - `PopulateNativeSlots(ShapedQueryExpression, MethodInfo, MethodCallExpression)` — the seven-arm slot
    lowering (`Where`/`OrderBy`/`OrderByDescending`/`ThenBy`/`ThenByDescending`/`Skip`/`Take`) plus the
    `else` catch-all that records the non-native signal (see §2 for the renamed field).
  - `IsNativeRepresentableSlotOperator(MethodInfo)` — the nine-operator whitelist (seven slot operators +
    `Select` + `OfType`) that suppresses the catch-all.

- **`NativeProjectionBinder`** — owns:
  - `TryPopulateNativeProjection(MongoQueryExpression, LambdaExpression) → bool` — the `$project` shape-walk
    (NewExpression / MemberInit arms), the case-insensitive alias-collision `HashSet` guard, and field
    resolution via `MongoExpressionTranslator`. Returns `true` and fills `Select.Projection` only when every
    leaf is a plain resolvable member access.

The QMTEV keeps thin delegating call sites:
- `VisitMethodCall` fall-through calls `NativeSlotPopulator.PopulateNativeSlots(...)`.
- `TranslateSelect` calls `NativeProjectionBinder.TryPopulateNativeProjection(...)`.

`IsTransparentIdentifierSelector` stays in the QMTEV (it is an EF-dispatch concern — deciding whether a
`Select` is an EF join-rewrite artifact vs. a user projection — not native translation).

### 2. Single is-native signal — `NativeRoute` on `MongoSelectDefinition`

Introduce a computed routing property that is the single source of truth read by the gate:

```csharp
internal enum NativeRoute
{
    Fallback,     // an unsupported operator was seen → driver-LINQ fallback (or throw under NativeOnly)
    WholeEntity,  // native pipeline over whole-entity results
    Projection    // native pipeline ending in a $project (pushed-down projection)
}
```

- **Population side (unchanged logic, renamed field).** The mutable `bool IsNativeRepresentable` is renamed
  to an internal, intent-revealing negative flag — `bool _hasUnsupportedOperator` (default `false`) with the
  polarity inverted so the accumulating write sites read `_hasUnsupportedOperator = true`. This flag remains
  a population detail; it is **not** read directly by the gate any more.
- **Computed route:**
  ```csharp
  internal NativeRoute Route =>
      _hasUnsupportedOperator ? NativeRoute.Fallback
      : Projection.Count > 0  ? NativeRoute.Projection
      : NativeRoute.WholeEntity;
  ```
- **Gate reads `Route` only.** `MongoShapedQueryCompilingExpressionVisitor`:
  - The pushed-down-projection branch fires on `queryMode != DriverLinq && Select.Route == NativeRoute.Projection`.
  - The whole-entity native branch fires on `Route == NativeRoute.WholeEntity` (subject to the existing
    streaming/DOM and lookup gates, unchanged).
  - `Route == NativeRoute.Fallback` → driver-LINQ fallback, or throw under `NativeOnly`.

**Reconciling the "flag doesn't flip" case.** Today's concern is a projection realized entirely in the
driver-LINQ shaper (via `ProjectionAnalyzer.CanPushDown`) where `Select.Projection` was never populated. Such
a query has `Projection.Count == 0`, so its `Route` is `WholeEntity` or `Fallback` — it never enters the
native-`$project` branch, and it flows to the existing `ProjectionAnalyzer.CanPushDown` push-down path
exactly as it does now. The behavior is identical; the difference is that the decision is now one predicate
with a documented meaning instead of a `Projection.Count`/`queryMode`/`IsNativeRepresentable` trio.

**`NativeOnly` ordering preserved.** The `NativeOnly` guard that currently throws for projected non-entity
results (`MongoShapedQueryCompilingExpressionVisitor.cs:188`) keeps its position *after* the
`Route == Projection` branch, so a representable pushed-down projection still succeeds natively under
`NativeOnly` while a shaper-only projection still surfaces as a coverage failure. This ordering will be
re-verified against the `NativeOnly` gate tests.

### 3. Shared value renderer — `MongoValueRenderer` (static)

New `static class MongoValueRenderer` under `Query/NativeTranslation/`:

```csharp
internal static class MongoValueRenderer
{
    internal static BsonValue RenderValue(MongoExpression node, PlaceholderTable placeholders);
    // handles MongoConstantExpression (via ToBsonValue) and MongoParameterExpression (placeholder),
    // throws NativeTranslationNotSupportedException for anything else.
    private static BsonValue ToBsonValue(IProperty property, object? value);
    // the UNIFIED helper: coerce to property CLR type, serialize through writer,
    // and wrap InvalidCastException/FormatException/OverflowException/InvalidOperationException
    // in NativeTranslationNotSupportedException (the query-renderer semantics, now applied to both dialects).
}
```

- `MongoQueryLanguageRenderer.RenderValue`/`ToBsonValue` are removed; call sites (`RenderNode`,
  the bool-field and `$in`-array paths) call `MongoValueRenderer.RenderValue` / the shared value helper.
- `MongoAggregationExpressionRenderer.RenderValue`/`SerializeConstant` are removed; the `RenderValue`
  switch-arm calls `MongoValueRenderer.RenderValue`.
- **Net correctness fix:** the aggregation dialect now gets the same diagnostics wrapper — a constant that
  fails serialization in a `$expr` subtree throws `NativeTranslationNotSupportedException` (and thus falls
  back / throws-under-NativeOnly consistently) instead of a raw `InvalidCastException`.

Note the coercion-target subtlety documented in the current `ToBsonValue`: the property's CLR type
(compile-time path) differs from the serializer's `ValueType` for value-converted properties, so callers pass
their own coercion target. Both existing call paths coerce to `property.ClrType`, so a single shared helper
preserves current behavior; this is called out so the shared helper's signature keeps `IProperty` (not a
pre-resolved serializer) as its input.

### 4. Make `MongoAggregationExpressionRenderer` static; remove ad-hoc instances

Once `RenderValue`/`SerializeConstant` move to `MongoValueRenderer`, `MongoAggregationExpressionRenderer` has
no fields and only pure methods (`Render`, `RenderBinary`). Convert it to a `static class`. Consequences:

- `MongoQueryLanguageRenderer`'s `_aggRenderer` field is deleted; `RenderAsExpr` calls
  `MongoAggregationExpressionRenderer.Render(...)` statically.
- `MongoPipelineFactory.RenderProject` drops `new MongoAggregationExpressionRenderer()` and calls the static
  method. This dissolves the "thread the single renderer through `MongoPipelineFactory`" item entirely —
  there is no instance to thread.

`MongoQueryLanguageRenderer` remains an instance threaded through `MongoPipelineFactory.Create` (unchanged).

### 5. Documentation-consistency sweep (docs only; touched files)

From the EF-331 XML-doc/markdown review folded into EF-332 scope:

- Add `<param>` docs to the undocumented `MongoInExpression` and `MongoRegexExpression` constructors
  (every sibling in the expression family documents its ctor).
- Document `MongoAggregationExpressionRenderer.Render` (mirror `MongoQueryLanguageRenderer.Render` —
  summary/param/returns/exception).
- Remove stale ephemeral "Task N" references from shipped doc comments in `PlaceholderTable`,
  `MongoSelectLowerer`, `MongoPipelineFactory`, `MongoQueryLanguageRenderer`, `MongoExpressionTranslator` —
  replace with the concept or drop.
- Standardize `<para>` vs bare blank lines in `MongoExpression`, `MongoSelectLowerer`,
  `MongoExpressionTranslator`, `MongoStreamingEntityMaterializerRewriter`.
- Fix markdown-inside-XML-docs: `MongoExpression` `**bold**` → `<em>`; `LookupExpression` bare `$lookup` →
  `<c>$lookup</c>` and the "stage" mislabel on a data-holder.

### 6. AGENTS.md update (`src/MongoDB.EntityFrameworkCore/Query/AGENTS.md`)

- The catch-all / nine-operator-whitelist pitfall narrative now points at `NativeSlotPopulator` (and
  projection at `NativeProjectionBinder`) rather than "inline in the QMTEV".
- Document the `IsNativeRepresentable` → `_hasUnsupportedOperator` + computed `NativeRoute` change and that
  the gate reads `Route` as the single is-native signal.
- Note the single shared `MongoValueRenderer` and that `MongoAggregationExpressionRenderer` is now static.

## Testing / verification

Refactor with no behavior change — the safety net is the existing suite, run at the EF-331 baseline:

1. Build all three EF versions (`/test-all` skill or `dotnet build -c "Debug EF{8,9,10}"`).
2. Full FunctionalTests + SpecificationTests green in `Native` mode on EF8/EF9/EF10.
3. `MONGODB_EF_NATIVE_ONLY=1` spec pass/fail set **unchanged** vs. EF-331 (the "what runs native" report is
   the coverage invariant this refactor must not move).
4. Native unit tests (`tests/.../UnitTests/Query/NativeTranslation/`) and `QueryModeGate*` functional tests
   green — these exercise the routing/gate and value-rendering paths most directly.
5. New/adjusted unit coverage for the unified diagnostics: a `$expr`-subtree constant that fails
   serialization now throws `NativeTranslationNotSupportedException` (previously `InvalidCastException` on the
   aggregation path) — assert the exception type to lock the fix.

## Delivery

Normal design-review-then-implement flow (docs-only PR reviewed and signed off before coding), like EF-330.
At completion, squash to one commit with a full PR-style message (stacked-PR workflow); keep an
`EF-332-presquash` safety backup until merge. `nuget.config` stays uncommitted.
