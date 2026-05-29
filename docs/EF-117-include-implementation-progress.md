# EF-117 — Cross-collection `Include` implementation progress

Companion to [`EF-117-include-implementation-plan.md`](EF-117-include-implementation-plan.md).
One section per stage as it lands. Each section records what shipped, how it
was verified, and any design notes that matter for the stages that follow.

---

## Stage 0 — Plumbing, scaffolding, M2M guard

### Changes

- `src/.../Query/Visitors/MongoIncludeCompiler.cs` (new) — scaffold with
  `ClassifyIncludeNavigation(IncludeExpression)` helper that partitions the
  three cases:
  - **Embedded** — returns the navigation; existing path unchanged.
  - **Skip-nav** (`ISkipNavigation`) — throws `InvalidOperationException` with
    the final M2M message citing the EF-117 follow-up.
  - **Cross-collection** — throws `InvalidOperationException` preserving the
    legacy message so the ~500 existing spec-test overrides stay green; the
    navigation name (`<Entity>.<Nav>`) and EF-117 reference are appended.
- `src/.../Query/Visitors/MongoProjectionBindingRemovingExpressionVisitor.cs` —
  both `IncludeExpression` branches now call the shared classifier.
- `src/.../Query/Visitors/MongoProjectionBindingExpressionVisitor.cs` — same,
  for the earlier-running visitor that was missed on the first pass.
- `tests/.../FunctionalTests/Query/IncludeTests.cs` (new) — four Stage 0
  shells:
  - `Include_reference_dependent_to_principal_throws_pending` — asserts the
    current "could not be translated" wrapping; flips in Stage 1.
  - `Include_collection_principal_to_dependents_throws_pending` — asserts
    legacy message + EF-117 ref + navigation name; flips in Stage 2.
  - `ThenInclude_chain_throws_pending` — Customer → Order → Item chain;
    flips in Stage 3.
  - `Include_skip_navigation_throws_not_supported` — asserts the **final**
    M2M message; this is permanent behavior.

### Verification (EF10, `MONGODB_URI` pointing at local docker)

| Suite | Result |
|---|---|
| New `IncludeTests` | 4 / 4 pass |
| `NorthwindInclude*QueryMongoTest` + `NorthwindQueryFilters*` + `NorthwindQueryTagging*` | 518 / 518 pass — no spec-test churn |
| `OwnedEntityTests` | 70 / 70 pass — embedded include path preserved |
| `UnitTests` | 260 / 260 pass |
| `LoggingTests.Vector_query_warning_logged_*` | 4 failures, **pre-existing**; confirmed against a clean stash. Unrelated to Include. |

### Design notes for later stages

- The literal substring `Including navigation 'Navigation' is not supported`
  had to be preserved verbatim — the `'Navigation'` is the EF property name
  (a `nameof` quirk in the original code, not the navigation's actual name)
  and ~500 spec-test overrides match on it. New context (`<Entity>.<Nav>`
  + EF-117 ref) is appended. The full string is replaced by an MQL-assertion
  baseline as each test starts translating.
- The dependent → principal reference test (`Order.Customer`) currently
  fails *before* reaching the classifier — EF's nav-expansion rewrites it
  into a shape `MongoQueryableMethodTranslatingExpressionVisitor` rejects.
  Stage 1 must route this path through the cross-collection include
  machinery so the classifier (and thence the loader) sees it.
- `HasMany().WithMany()` builds cleanly through `MongoModelValidator` — no
  extra guard is needed for the M2M case; the classifier alone is enough.

---

## Stage 1 — Collection navigation, principal → dependents (core complete)

> **Re-staged from the original plan.** Plan called this Stage 2 with
> reference (dependent → principal) as Stage 1. A pre-implementation
> probe showed EF Core's nav-expansion rewrites the reference case into
> a synthetic `Queryable.Join` with a `TransparentIdentifier` result
> selector — that JOIN never reaches the classifier and requires a
> separate unwrap pass. Swapped the order so Stage 1 ships the smaller,
> contained piece first; Stage 2 then handles the JOIN unwrap.
> See `EF-117-include-implementation-plan.md` for the updated stages.

### Changes

- `src/.../Query/Visitors/MongoIncludeCompiler.cs` — added
  `IsCrossCollection(INavigation)` discriminator,
  `GetClrPropertyOrThrow` for shadow-FK rejection, and
  `LoadCollection<TPrincipal, TRelated>(...)` runtime helper. The
  loader runs the sub-query through `DbContext.Set<TRelated>().Where(...)`
  so the full EF translation pipeline (element names, value converters,
  discriminator, etc.) applies identically to include results as to a
  stand-alone DbSet query.
- `src/.../Query/Visitors/MongoProjectionBindingExpressionVisitor.cs` —
  cross-collection `IncludeExpression`s are recognised but their
  `NavigationExpression` (an EF-generated sub-query against the related
  DbSet) is deliberately not visited. The loader gets everything from
  the navigation metadata.
- `src/.../Query/Visitors/MongoProjectionBindingRemovingExpressionVisitor.cs`
  — `AddInclude` now dispatches on `IsCrossCollection`. Cross-collection
  collection navigations emit a call to `LoadCollection` whose result is
  threaded into the existing `IncludeCollection` helper for fixup +
  `SetIsLoaded`. Constructor extended to receive the shaper's
  `QueryContext` parameter and `BsonSerializerFactory`.
- `src/.../Query/Visitors/MongoMixedProjectionBindingRemovingExpressionVisitor.cs`
  — constructor mirrors the parent.
- `src/.../Query/Visitors/MongoShapedQueryCompilingExpressionVisitor.cs`
  — threads `QueryContextParameter` and `_bsonSerializerFactory` through
  both shaper-compilation paths.
- `tests/.../FunctionalTests/Query/IncludeTests.cs` — flipped the
  collection test from placeholder to a materialization assertion (seed
  Customer/Order, query with `.Include(c => c.Orders)`, assert orders
  loaded and inverse navigation set). Per-test collection names so
  parallel re-runs don't collide.

### Verification (Debug EF10)

| Suite | Result |
|---|---|
| New `IncludeTests` | 4 / 4 pass — collection Include materializes, reference still pending (Stage 2), M2M throws final error, ThenInclude loads outer only (Stage 3 pending) |
| `OwnedEntityTests` | 70 / 70 pass — embedded include path preserved |
| `UnitTests` | 260 / 260 pass |
| Full `SpecificationTests` | 4299 / 4432 pass (14 skipped, 119 failed) — significant improvement from the post-implementation state of 167+ failing |

### Spec-test override sweep

Two passes:

1. **Bulk pattern replacement.** A Python script replaced 157 instances
   of the legacy "throws this exception with this message" override
   pattern with `await base.X(async)` so the EF Core base test runs and
   asserts result correctness against the in-memory model.
2. **Baseline rewrite.** Ran the suite with
   `EF_TEST_REWRITE_BASELINES=1` (multiple passes) so the
   Roslyn-based baseline rewriter captured the actual MQL pipelines into
   each test's `AssertMql(...)` call.

What remains failing:

- ~98 tests in the four `NorthwindInclude*QueryMongoTest` suites that
  exercise sub-features Stage 1 doesn't cover: `ThenInclude` inner
  loads (Stage 3), filtered Include (separate ticket), dependent →
  principal reference (Stage 2), tracking duplicates when the same
  entity is included via multiple paths, and a handful with
  test-specific quirks (cyclic includes, complex projections).
- ~21 tests in other Northwind suites (Set ops, Select, Where, Misc,
  Compiled, Filters) that use Include as part of a larger query and
  hit the same Stage 1 limitations.

These follow the same shape: the override still asserts the legacy
throw (replaced above) or asserts a stale MQL baseline. They need
per-test investigation — either to update the assertion to match the
actual partial-stage behavior, or to be fixed by a later stage's
implementation. This is the bulk of what Stage 5's sweep was meant
to do; it's been started early but isn't finished.

### Design notes for later stages

- **Stage 1 deliberately reuses the DbContext-level query path** rather
  than building a sub-shaper directly. This was the second iteration of
  the loader: a first attempt that bypassed EF (`IMongoCollection.Find`
  with the driver's default class map) materialized fine for the
  IncludeTests model but blew up on Northwind because the entity has
  `OrderID` (not `Id`) as its key and the driver-default class map
  didn't know about EF's `_id` mapping. Routing through
  `DbContext.Set<TRelated>().Where(...)` gives Northwind the full EF
  pipeline for free.
- **Tracking-mode fixup** relies on EF's state manager: when a
  cross-collection sub-query goes through `DbContext.Set`, tracked
  queries auto-attach the related entities and the state manager wires
  up the navigation property from matching FKs. `IncludeCollection`'s
  tracking branch just enumerates the result to trigger that fixup.
- **80 newly-failing spec tests** are overrides that previously
  asserted the throw the classifier used to emit. Each one needs to
  either (a) be deleted so the base test runs and asserts both result
  shape and MQL, or (b) keep the override but flip to `AssertMql(...)`
  with the captured baseline. This is the Stage 5 sweep starting early
  — it's mechanical but voluminous and is the bulk of the remaining
  Stage 1 work.
- **ThenInclude is silently dropped at Stage 1**: when the outer
  `IncludeExpression.NavigationExpression` carries a nested
  `IncludeExpression` for the chained navigation, the loader-based
  cross-collection path skips visiting `NavigationExpression`
  entirely. Stage 3 wires recursion so the inner navigation is loaded.
  The Stage 1 test `ThenInclude_chain_outer_collection_loads_inner_pending`
  captures this — outer Orders load, inner Items don't — so the
  behavior is at least known and asserted, not a silent surprise.
- **Composite keys + shadow FK/PK** throw a clear "follow-up" error
  via `GetClrPropertyOrThrow`. Both are mentioned in the plan as
  out-of-scope corners; they get clean error messages today and will be
  addressed in a later stage.

---

## Stage 2 — Reference navigation, dependent → principal (JOIN unwrap)

### Changes

- `src/.../Query/MongoQueryTranslationPreprocessor.cs` — new
  `IncludeJoinUnwrapper` visitor runs after `base.Process(query)`. It
  matches the synthetic
  `Queryable.Join(...).Select(o => IncludeExpression(o.Outer, o.Inner, nav))`
  shape EF Core's nav-expansion generates for dependent → principal
  reference Include and rewrites it to
  `<outerSource>.Select(p => IncludeExpression(p, default(TInner), nav))`.
  Subsequent stages of the pipeline then see the same Include shape they
  do for collection navigations.
- `src/.../Query/Visitors/MongoIncludeCompiler.cs` — added
  `LoadReference<TPrincipal, TRelated>` runtime helper. Issues
  `dbSet.Where(r => EF.Property<TKey>(r, pkName).Equals(fkValue)).FirstOrDefault()`
  per dependent, materializing the related principal through the full
  EF pipeline.
- `src/.../Query/Visitors/MongoProjectionBindingRemovingExpressionVisitor.cs`
  — `BuildCrossCollectionLoaderCall` now dispatches on
  `navigation.IsCollection`: collection navs call
  `BuildCollectionLoaderCall` (the Stage 1 path), reference navs call
  the new `BuildReferenceLoaderCall`. The reference loader extracts the
  FK from the materialized dependent and looks up the principal by PK.
- `tests/.../FunctionalTests/Query/IncludeTests.cs` — flipped the
  reference test from "asserts the legacy 'could not be translated'
  failure" to a materialization assertion (seed Customers/Orders,
  query `db.Orders.Include(o => o.Customer)`, assert every Order's
  Customer is populated and identity resolution gives shared Customer
  instances for orders that share an FK). Added
  `ReplaceService<IModelCacheKeyFactory, IgnoreCacheKeyFactory>` to all
  test-local contexts so per-test collection-name suffixes aren't
  invalidated by EF's model cache.

### Verification (Debug EF10)

| Suite | Result |
|---|---|
| `IncludeTests` | 4 / 4 pass — reference now materializes; collection still works; ThenInclude outer-only still asserts the Stage 1 partial behavior; M2M still throws the final error |
| `OwnedEntityTests` | 70 / 70 pass |
| `UnitTests` | 260 / 260 pass |
| Full `SpecificationTests` | 4291 / 4432 pass (14 skipped, 127 failed) — 8 *new* failures vs Stage 1's 119. These are cases where Stage 2 now successfully materializes the reference Include but the spec-test override still asserts `AssertTranslationFailed` — expected progressions that need per-test override updates in the Stage 5 sweep |

### Design notes for later stages

- **The JOIN-unwrap rewrite happens in the preprocessor, not in
  `TranslateJoin`.** Originally planned to override `TranslateJoin`
  inside `MongoQueryableMethodTranslatingExpressionVisitor`. That would
  have required producing a `TransparentIdentifier`-shaped
  `ShapedQueryExpression` and letting the subsequent `Select` project
  it back down — invasive and required understanding how the base
  visitor expects to compose with such results. The preprocessor
  rewrite is dramatically simpler: rewrite the tree *before* the
  Translator sees it and the rest of the pipeline doesn't notice the
  Join was ever there. User-written Joins are unaffected because the
  pattern-match requires both a TransparentIdentifier-typed selector
  parameter and an `IncludeExpression` body — neither appears in
  user-written joins.
- **`default(TInner)` in the rewritten IncludeExpression is a
  placeholder.** Cross-collection projection-binding never visits
  `IncludeExpression.NavigationExpression`; the loader extracts what it
  needs from `INavigation` metadata. The default keeps the tree
  structurally valid for any later visitor that needs the type but
  never gets evaluated at runtime.
- **Performance caveat for large dependent sets.** `LoadReference`
  issues one sub-query per dependent. For Northwind's 830 Orders, that
  is 830 round-trips against the Customers collection for a single
  `db.Orders.Include(o => o.Customer)` — observably slow in the
  specification suite. A natural follow-up is to batch the FK values
  and do `Where(c => fks.Contains(c.Id))` once per principal type,
  caching the results by FK in the query context. This is also the
  natural shape Stage 1's collection path *should* take (it currently
  also does one sub-query per principal). Both deferred to a later
  perf-pass ticket.
- **Spec-test override cleanup is two-pass and brittle.** A first
  bulk-replace pass converted ~157 `Assert.ThrowsAsync` overrides to
  `await base(async); AssertMql(...)` (Stage 1 commit), and a second
  pass would convert ~270 `AssertTranslationFailed` overrides
  similarly. The
  `EF_TEST_REWRITE_BASELINES=1` rewriter is *not idempotent* across
  runs in practice — running it a second time corrupted hundreds of
  baselines and caused the suite to take 31 minutes (from ~35s). This
  commit holds the spec-test files at the Stage 1 baseline state on
  purpose; the `AssertTranslationFailed` cleanup and full Stage 5
  sweep belong in a focused follow-up commit where the rewrite is run
  exactly once with a clean before/after diff.
