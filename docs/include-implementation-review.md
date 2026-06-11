# Include implementation — architectural analysis, review, and fix plan (branch EF-117k)

> Baseline: SpecificationTests (EF10) = **853 failed / 3565 passed / 14 skipped**.
> 429 unique failing methods, tagged in-code with `// Failed:`. This document explains
> *why* they fail and how to fix them.

## 1. Architecture — how Include is implemented

The provider does **not** generate aggregation BSON itself; it builds a *driver-LINQ*
expression and hands it to the MongoDB C# driver's LINQ v3 provider. Include is bolted
onto that bridge. Pipeline (additions in **bold**):

```
IQueryable<T>
  ▼ MongoQueryableMethodTranslatingExpressionVisitor   — dispatch; builds MongoQueryExpression
  │     **TranslateJoin/LeftJoin/GroupJoin → TranslateJoinCore → RebindInnerShaperToOuterQuery**
  ▼ MongoProjectionBindingExpressionVisitor             — **Include-in-projection, ThenInclude,
  │                                                        filtered-include pipeline extraction**
  ▼ MongoShapedQueryCompilingExpressionVisitor          — **builds inner $lookup sources**
  ▼ MongoEFToLinqTranslatingExpressionVisitor           — **emits $lookup / driver LeftJoin / $unwind**
  ▼ MongoProjectionBindingRemovingExpressionVisitor     — **reads _outer/_inner/_lookup_* from BsonDocument**
```

### Two join strategies
1. **Driver-native `LeftJoin`** for the *first* reference Include. The driver restructures
   the document into `{ _outer: <root>, _inner: <related> }`.
2. **`$lookup` + (optional) `$unwind`** for collection Includes and *every join after the first*.
   Output lands in a field named `_lookup_<NavigationName>`.

### Key state (`MongoQueryExpression`)
- `PendingLookups : List<LookupExpression>` — `$lookup` stages, deduped **by `As` string only**.
- `InnerCollections : Dictionary<IEntityType, …>` — join inners (**unordered**).
- `UsesDriverJoinFields : bool` — single flag meaning "document is in `_outer`/`_inner` shape".
- Projections carry aliases via `ObjectAccessExpression.Name` and `LookupExpression.As`.

### The alias contract (the crux)
The strings `_inner`, `_outer`, `_lookup_<Name>` are the contract between the **pipeline that
writes fields** and the **shaper that reads them**. That contract is **recomputed independently
at ≥3 writer sites and 1 reader site**:
- `LookupExpression` ctor → `As = "_lookup_" + navigation.Name`
- `EntityProjectionExpression.BindNavigation` → `"_lookup_" + navigation.Name`
- `RebindInnerShaperToOuterQuery` → literal `"_inner"`, then string-matched `"_inner" → "_lookup_*"` rewrite
- reader `MongoProjectionBindingRemovingExpressionVisitor` → `Name == "_inner"`, `Name.StartsWith("_lookup_")`, `"_outer"`, **all gated on `UsesDriverJoinFields`**

There is no single source of truth, and the coordinating flag is a single boolean toggled
*mid-translation*. This is the root cause of the two largest failure clusters.

## 2. Detailed review — findings (ranked)

### CRITICAL

**C1 — `UsesDriverJoinFields` desync → "Field '_outer' required but not present" (~28 tests).**
`RebindInnerShaperToOuterQuery` sets the flag `true` for the first LeftJoin, then on a *second*
join flips it back to `false` and re-registers everything as flat `$lookup` (the "avoid
nested `_outer._outer`" branch, `MongoQueryableMethodTranslatingExpressionVisitor.cs:421-466`).
The emitted pipeline is then flat (no `_outer` wrapper), but projections/shaper paths built
before the toggle still expect `_outer`. The shaper reads a required `_outer` field that the
document doesn't contain → throw at `Storage/BsonBinding.cs:124` (runtime). One mutable bool
cannot represent the real state (N joins, each native-or-lookup, doc nested-or-flat).

**C2 — null `Navigation` on cross-collection access → `NullReferenceException` (~20 tests).**
Cross-collection `_lookup_*` nodes are built with the `ObjectAccessExpression(IEntityType,…)`
ctor, leaving `Navigation == null`. The reader's `_inner`/`_lookup_` cases are gated on
`UsesDriverJoinFields`; when it's `false` at shaper time (same toggle as C1) a `_lookup_*` node
falls through to the generic branch and dereferences `innerObjectAccessExpression.Navigation
.DeclaringEntityType` → NRE (`MongoProjectionBindingRemovingExpressionVisitor.cs:255`).

> C1 + C2 share one defect (the `UsesDriverJoinFields` boolean + recomputed aliases) and
> together account for ~48 failures. Fixing the alias/state model fixes both.

**C3 — join inner source wrapped in `.As(serializer)` → driver rejects it (large slice of the
152 `ExpressionNotSupportedException : …Aggregate([]).As(…)`).**
`CreateInnerSourceTyped` returns `innerCollection.AsQueryable().As(serializer)`
(`MongoShapedQueryCompilingExpressionVisitor.cs:319`). The driver's `Join`/`LeftJoin` translator
requires a *bare* collection IQueryable as the inner operand and rejects an `.As(...)`-wrapped
source (rendered as `Orders.Aggregate([]).As(...)`). *Hypothesis — highest leverage, must be
validated before relying on it.* The `Aggregate([])` itself is benign (driver's rendering of an
empty pipeline); the `.As()` wrapper on the inner operand is the defect.

**C4 — `$lookup` with a dotted `as: "_outer._lookup_Orders"` (wrong/invalid MQL).**
When a reference Include used the driver LeftJoin and a collection Include is then added,
`MongoProjectionBindingExpressionVisitor.cs:194-201` prefixes `LocalField`/`As` with `_outer.`/
`_inner.`. `$lookup` cannot write to a nested dotted output path; the array lands at a top-level
field literally named `_outer._lookup_Orders` (or is rejected), so the shaper finds nothing.

### HIGH (silent wrong results)

**H1 — filtered-Include `Where` predicate silently dropped.**
`ExtractFilteredIncludePipeline` discards the `Where` lambda with the comment "already handled by
the `$lookup` `$match`" (`MongoProjectionBindingExpressionVisitor.cs:773-777`). That `$match` only
encodes the FK join equality. `Include(c => c.Orders.Where(o => o.Total > 100))` returns the
collection **unfiltered**, no error. Only `OrderBy`/`Skip`/`Take` are honored.

**H2 — navigation resolved by target type → ambiguous for multiple navs / self-joins.**
`RebindInnerShaperToOuterQuery` falls back to `GetNavigations().FirstOrDefault(n =>
n.TargetEntityType == innerEntityType)` (`…:413-419, 443-444`). With two navigations to the same
target (`Order.Customer`/some second customer nav, or self-refs `Manager`/`Reports`) it picks an
arbitrary one → wrong FK/alias, silent wrong join. EF already supplies the exact `INavigation` via
the `IncludeExpression`; the code reconstructs it by guessing instead of threading it through.

**H3 — composite foreign keys use `Properties[0]` only.**
`LookupExpression` matches a single `localField`/`foreignField` from `foreignKey.Properties[0]`
(`LookupExpression.cs:47-54`). A genuine multi-column FK is mis-joined (over-matching). Needs a
`$lookup` pipeline with `let` + `$expr/$and` over all key properties, or an explicit rejection.

**H4 — first-join special case + non-deterministic `InnerCollections.Keys.First()`; breaks at >2 joins.**
`…:442` uses `.First()` on an unordered `Dictionary`, and the `_inner → _lookup_*` rewrite
string-matches `Alias == "_inner"` and `break`s after one (`…:454-461`). With ≥2 references or
≥3 joins the "which join was first" decision is undefined and later navigations keep stale aliases.

**H5 — no identity resolution / de-duplication in the collection shaper (wrong results, ~7).**
`IncludeCollection`/`PopulateCollection` (`MongoProjectionBindingRemovingExpressionVisitor.cs:603-725`)
build a fresh collection per parent straight from the `$lookup` array with no identity map,
violating EF's "one CLR instance per key" contract. Combined with reference-leaf ThenIncludes not
being extracted into the nested pipeline, navigations get populated inconsistently.

### MEDIUM

- **M1** `EntityProjectionExpression.Equals` ignores the alias/`Name` (compares only EntityType +
  ParentAccessExpression, `…:183-185`); `AddToProjection` dedups by `Expression.Equals`
  (`MongoQueryExpression.cs:66`) → two projections differing only by lookup alias can merge,
  returning a stale index.
- **M2** `AddLookup` dedups by `As` only (`MongoQueryExpression.cs:103`) → same-named navigations on
  different declaring types in a ThenInclude chain collapse into one `$lookup`.
- **M3** Multiple `OrderBy`/`ThenBy` in a filtered Include emit separate single-key `$sort` stages
  (last wins); `GetSortField` silently falls back to `_id` on an unrecognized key selector.
- **M4** Nested collection-item reference Includes leave a member-based `ProjectionBindingExpression`
  unresolved → `GetConstantValue<int>` throws "invalid object state" (~9 tests,
  `MongoProjectionBindingRemovingExpressionVisitor.cs:732`).
- **M5** `ReplaceProjectionAt`/`AddToProjection` mutate the projection list while other navigations'
  shapers already hold captured indices (order-dependent, latent).

### Triage note on the 242 "could not be translated"
This cluster is **mixed**: (a) genuinely-unsupported shapes that *should* fail and whose tests
assert it (cross-`DbSet` cartesian via `SelectMany`, `GroupBy`-over-Include, `RightJoin`) — these
need only assertion/message cleanup; (b) Include shapes the POC intended to support that still route
through an untranslatable `SelectMany`/correlated-subquery form. Phase 0 must separate the two.

## 3. Plan to verify and fix

Each phase ends with a **verification gate**: a named test filter to run plus the expected
full-suite failure-count delta. Use TDD — add a minimal failing functional test (MQL-assert +
execution) per cluster before changing code.

### Phase 0 — Triage & test scaffolding (no production code)
- Classify all 429 failing methods into: **(A) assertion-only** (feature now works / throws
  differently — the `// Failed: Expected exception not thrown` and `Throws-assertion no longer
  matches` tags), **(B) real code bug**, **(C) genuinely unsupported** (update message/docs only).
  Produce a worksheet keyed by the existing `// Failed:` tags + `failmap.json`.
- Add minimal repro tests (one per cluster C1–C4, H1–H5) under FunctionalTests/Query.
- Gate: worksheet reviewed; repros red.

### Phase 1 — Replace the boolean+string alias model with a structured join plan  *(fixes C1, C2; subsumes M1, M2)*
- Introduce a per-query **join plan**: `INavigation → { Strategy (DriverLeftJoin | Lookup),
  string Alias, string FieldPath, bool Unwind }`, computed **once** and stored on
  `MongoQueryExpression`. Delete `UsesDriverJoinFields` and every ad-hoc string recompute.
- Make `ObjectAccessExpression` for cross-collection results always carry its `INavigation`;
  include the alias in `EntityProjectionExpression.Equals`/`GetHashCode`.
- Shaper reads alias/field from the plan, not from re-derived strings.
- Gate: `~NorthwindIncludeQueryMongoTest` multi-level/multi-reference subset green;
  expect ~48-failure drop (no more `_outer`-missing / NRE).

### Phase 2 — Fix the join inner-source serializer  *(fixes C3)*
- Validate the `.As()` hypothesis with a throwaway prototype (feed bare `AsQueryable()`; apply the
  entity serializer via the result selector / post-join `As`). Confirm the driver translates a
  representative `Join`+`$lookup`.
- Apply the minimal change at the inner-source construction site.
- Gate: `~NorthwindAsNoTrackingQueryMongoTest`, `~NorthwindAsTrackingQueryMongoTest`,
  `~NorthwindChangeTrackingQueryMongoTest` projection-join tests green; large drop in the
  `Aggregate([]).As(...)` cluster.

### Phase 3 — `$lookup` output-path correctness  *(fixes C4)*
- Ensure `$lookup.as` is always a **top-level** field; remove `_outer.`/`_inner.` prefixing of
  `LocalField`/`As`. Localfield resolution must follow the structured plan from Phase 1.
- Gate: collection-after-reference Include tests green.

### Phase 4 — Join/Include semantics  *(fixes H1–H5, M3, M4)*
- **H2**: thread the real `INavigation` from the `IncludeExpression` through instead of guessing.
- **H1**: emit the filtered-Include `Where` as a real `$match` inside the `$lookup` pipeline.
- **H3**: composite-FK `$lookup` via `let`+`$expr/$and`, or explicit clear rejection.
- **H5/M4**: identity map in the collection shaper; resolve nested collection-item bindings to
  integer indices; extract reference-leaf ThenIncludes into the nested pipeline.
- **M3**: merge multi-key `$sort`; throw on unrecognized sort key instead of `_id` fallback.
- Gate: `~NorthwindStringIncludeQueryMongoTest`, `~NorthwindEFPropertyIncludeQueryMongoTest`,
  `~NorthwindNavigationsQueryMongoTest`, `~NorthwindJoinQueryMongoTest` green.

### Phase 5 — Re-baseline & assertion cleanup
- Re-run full EF10 suite. For each now-passing test, remove `// Failed:` (and `// Fails:` where the
  gap is closed) and assert real MQL/results.
- Ensure genuinely-unsupported shapes throw EF's standard translation-failed message (closes the
  EF-X002 family); update `docs/failing-spec-tests.md`.
- Repeat for EF8/EF9 via `/test-all`.
- Gate: full-suite failures reduced to only the (C) genuinely-unsupported set, all tagged/ticketed.

### Key decision for the user
Phase 1 is a **structural rework** of the alias/state model rather than spot-patching. The review
shows spot-patches won't hold (the boolean cannot represent the state space). Recommendation: do
Phase 1 properly. The alternative — incremental patching to chase individual tests green — will
re-break as new shapes are added. Phases 2 and 3 are comparatively localized and could land first
to bank quick wins while Phase 1 is designed.
