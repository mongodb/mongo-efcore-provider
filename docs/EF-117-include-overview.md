# EF-117 cross-collection Include — implementation overview

A landing page for anyone picking this work up cold — for review, bug
fixing, or building the follow-up tickets. The full per-stage write-up
is in [`EF-117-include-implementation-progress.md`](EF-117-include-implementation-progress.md);
the original staging plan is in
[`EF-117-include-implementation-plan.md`](EF-117-include-implementation-plan.md).

## What was built

Eager loading via `Include` / `ThenInclude` across collection boundaries
in the MongoDB EF Core provider. Before this work, the provider only
supported `Include` for owned (embedded) navigations — anything that
crossed a collection boundary threw at translation time.

After Stages 1–5 the provider supports the four non-skip
`Include` shapes:

- **Collection, principal → dependents** (`Customer.Orders` to a separate
  Orders collection)
- **Reference, dependent → principal** (`Order.Customer` to a separate
  Customers collection)
- **`ThenInclude` chains** of arbitrary depth with mixed
  reference/collection legs (`Customer.Orders.ThenInclude(o => o.Items)`)
- **All three `QueryTrackingBehavior` modes** propagate from the
  outer query into the include sub-queries

Many-to-many (skip navigations) is out of scope and throws a clear
"not yet supported" error.

## Strategy: fan-out, not `$lookup`

For each cross-collection `IncludeExpression`, the provider issues a
separate sub-query against the related collection, materializes the
related entities through the normal EF pipeline, and lets EF's fixup
machinery wire the navigation properties. This mirrors EF Core's
in-memory provider rather than the relational providers' JOIN
approach.

Why fan-out vs. server-side `$lookup`:

- MongoDB's `$lookup` is awkward to express through EF Core's
  expression-tree shape for cross-collection includes.
- The C# driver's LINQ v3 provider does not expose a typed lookup
  hook for arbitrary entity types ergonomically.
- Fan-out reuses the existing `IncludeReference` / `IncludeCollection`
  helpers in the provider for fixup and `SetIsLoaded`, so no new
  shaper machinery was needed.
- Routing each sub-query through `DbContext.Set<TRelated>().Where(...)`
  means **every EF mapping applies for free** — element names, value
  converters, discriminators, owned-type nesting all just work
  identically to a stand-alone DbSet query.

The original staging plan called for `$lookup` as a later opt-in
alongside fan-out. That follow-up is still planned but unstarted.

## Architecture and call graph

```
DbSet<Customer>.Include(c => c.Orders).ToList()
   │
   ▼  MongoQueryTranslationPreprocessor.Process
   │      ├─ base.Process — EF Core nav-expansion produces IncludeExpression
   │      │   (for collection includes) OR Queryable.Join + Select(IncludeExpression)
   │      │   (for dependent → principal references)
   │      ├─ IncludeJoinUnwrapper — rewrites the synthetic Join+Select
   │      │   into a uniform Select(p => IncludeExpression(p, default, nav))
   │      └─ (existing) VectorSearchExtractor/Replacer
   │
   ▼  MongoQueryableMethodTranslatingExpressionVisitor
   │      (unchanged — pipeline goes through as for any other query)
   │
   ▼  MongoProjectionBindingExpressionVisitor (translation-time)
   │      ├─ ClassifyIncludeNavigation: dispatches embedded / cross-collection / skip-nav
   │      └─ For cross-collection includes, skips visiting NavigationExpression
   │
   ▼  MongoShapedQueryCompilingExpressionVisitor.CompileShapedQuery
   │      Threads QueryContextParameter, BsonSerializerFactory,
   │      QueryTrackingBehavior into the projection-binding-removing visitor
   │
   ▼  MongoProjectionBindingRemovingExpressionVisitor.AddInclude (shaper-time)
   │      ├─ Embedded: existing path (BsonDocument traversal)
   │      └─ Cross-collection: BuildCrossCollectionLoaderCall
   │          ├─ ExtractIncludeChainPath (for ThenInclude)
   │          └─ Emits Expression.Call to LoadCollection / LoadReference
   │
   ▼  Compiled shaper runs per principal BsonDocument
          │
          ▼  MongoIncludeCompiler.LoadCollection / LoadReference
                 │ ApplyTrackingBehavior  ── outer's QueryTrackingBehavior
                 │ dbSet.Include(chainPath)  ── recursive for ThenInclude
                 │ dbSet.Where(r => EF.Property(...) == fkValue).ToList()
                 │   (re-enters the provider's pipeline; preprocessor runs again)
                 │
                 ▼  Returns IEnumerable<TRelated>
                 │
                 ▼  Existing IncludeReference / IncludeCollection helpers
                      do navigation assignment + inverse-nav fixup +
                      SetIsLoaded via the state manager
```

## Key files

| File | Role |
|---|---|
| [`src/.../Query/Visitors/MongoIncludeCompiler.cs`](../src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoIncludeCompiler.cs) | New. Classifier, chain-path extractor, runtime `LoadCollection` and `LoadReference` helpers, tracking-behavior propagator. |
| [`src/.../Query/MongoQueryTranslationPreprocessor.cs`](../src/MongoDB.EntityFrameworkCore/Query/MongoQueryTranslationPreprocessor.cs) | Extended with `IncludeJoinUnwrapper` — rewrites EF nav-expansion's synthetic `Join+Select(IncludeExpression)` into a plain `Select(IncludeExpression)`. |
| [`src/.../Query/Visitors/MongoProjectionBindingRemovingExpressionVisitor.cs`](../src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoProjectionBindingRemovingExpressionVisitor.cs) | `AddInclude` now dispatches embedded vs. cross-collection. New `BuildCrossCollectionLoaderCall` / `BuildCollectionLoaderCall` / `BuildReferenceLoaderCall` emit the compile-time `Expression.Call` to the runtime helpers. |
| [`src/.../Query/Visitors/MongoProjectionBindingExpressionVisitor.cs`](../src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoProjectionBindingExpressionVisitor.cs) | The earlier translation-time visitor. Cross-collection includes preserve the `IncludeExpression` shape but skip the EF-generated sub-query inside `NavigationExpression`. |
| [`src/.../Query/Visitors/MongoShapedQueryCompilingExpressionVisitor.cs`](../src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoShapedQueryCompilingExpressionVisitor.cs) | Threads new constructor arguments (`QueryContextParameter`, `BsonSerializerFactory`, `QueryTrackingBehavior`) into both projection-binding-removing visitors. |
| [`src/.../Query/Visitors/MongoMixedProjectionBindingRemovingExpressionVisitor.cs`](../src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoMixedProjectionBindingRemovingExpressionVisitor.cs) | Mirror constructor for the mixed-projection path. |
| [`tests/.../FunctionalTests/Query/IncludeTests.cs`](../tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/IncludeTests.cs) | 9 focused tests covering each Include shape and tracking-mode propagation. |

## Hard-won discoveries

Things that were surprising during implementation and that future
maintainers should know:

### 1. EF nav-expansion emits two completely different shapes for reference vs. collection

- `Customers.Include(c => c.Orders)` (principal → dependents, collection)
  produces a clean `IncludeExpression(StructuralTypeShaperExpression,
  MaterializeCollectionNavigationExpression, navigation)` that lands
  on `MongoProjectionBindingExpressionVisitor.VisitExtension`'s
  `IncludeExpression` branch.

- `Orders.Include(o => o.Customer)` (dependent → principal, reference)
  is rewritten into a synthetic `Queryable.Join` with a
  `TransparentIdentifier` result selector followed by
  `Select(o => IncludeExpression(o.Outer, o.Inner, nav))`. **The
  `IncludeExpression` is inside a Select's lambda**, not at the top
  level. The provider's translator never sees the bare Include
  unless the synthetic JOIN is unwrapped first.

The asymmetry surprised the original Stage 1 effort — see the
`IncludeJoinUnwrapper` write-up in the progress doc.

### 2. The `IncludeExpression.NavigationExpression` is provider-rewritable

For cross-collection includes the provider deliberately *does not*
visit `NavigationExpression` — the loader uses `INavigation` metadata
(target collection, FK property, PK property) instead. The original
contents of `NavigationExpression` were an EF-generated sub-query the
provider's translator couldn't process anyway.

This means `IncludeExpression.NavigationExpression` is more of a
"hint" than a load-bearing expression for this provider — anything
type-correct works as a placeholder. Stage 2 substitutes
`Expression.Default(typeof(TInner))` for the unwrapped reference
case, and nothing downstream cares.

### 3. `ThenInclude` is encoded as nested `Select(t => IncludeExpression(...))` inside the outer's `NavigationExpression`

For `Customers.Include(c => c.Orders).ThenInclude(o => o.Items)`,
the outer `IncludeExpression.NavigationExpression` is a
`MaterializeCollectionNavigationExpression` whose `Subquery` ends in
`Select(t => IncludeExpression(t, t.Items, Order.Items))`. The chain
nests one layer per `ThenInclude`.

Stage 3 walks this structure to produce a dot-separated path
(`"Items"`, `"Items.Tag"`, etc.) and feeds it to
`dbSet.Include(string)` on the recursive sub-query — which re-enters
the provider's pipeline and gets unwrapped/handled again at every
level. No bespoke recursion in the visitor.

### 4. `EntitySerializer.Deserialize` throws `NotImplementedException` by design

The provider's `IBsonSerializer<TEntity>` implementation
intentionally does not deserialize directly — entity materialization
goes through EF Core's value-buffer-based shaper instead. So
**hand-rolled sub-queries via `IMongoCollection<TEntity>.Find(...)`
fail at deserialization** for any entity with non-default EF mappings.

Stage 1's first implementation attempt did exactly this and broke
the moment it hit Northwind (`Order.OrderID` → `_id` is an EF mapping
the driver's default class map doesn't know about). The current
implementation routes through `DbContext.Set<TRelated>().Where(...)`
instead, which goes through the full EF pipeline and gets every
mapping for free.

### 5. The driver throws `NotImplementedException` (no message) for "operation while a cursor is iterating"

An earlier attempt at the sub-query path used the driver's LINQ v3
queryable directly inside the shaper, while the outer cursor was
still open. The driver throws `NotImplementedException` deep in
`RetryableReadOperationExecutor.Execute` for this pattern (with no
error message — pure stack trace).

Routing through `DbContext.Set<TRelated>()` works because EF's
pipeline materializes each sub-query result eagerly into a list,
closing its cursor before returning.

### 6. EF Core's baseline rewriter (`EF_TEST_REWRITE_BASELINES=1`) is not idempotent across whole-suite runs

Running the rewriter once on the whole spec test project produced a
clean set of baselines. Running it a *second* time corrupted ~530
baselines and pushed the suite runtime from ~35 seconds to 31
minutes. Stage 5's workaround was to run the rewriter once
*per spec class* via `--filter`, so each class only sees its own
MQL. This is the only safe way to use the rewriter today.

### 7. Tests sharing fixtures need `ReplaceService<IModelCacheKeyFactory, IgnoreCacheKeyFactory>`

Multiple test methods in `IncludeTests` use the same DbContext type
with different per-test collection-name suffixes. Without
`IgnoreCacheKeyFactory`, EF caches the model from the first test
and reuses it for the second — collection names get masked, and the
second test reads/writes the wrong collection. `SingleEntityDbContext`
already does this; the new test-local contexts mirror it.

### 8. `AsNoTracking` does not propagate from outer to sub-query without explicit plumbing

`dbContext.Set<TRelated>()` inherits the *context-default* tracking
behavior, not the outer query's per-query override. So
`db.Customers.AsNoTracking().Include(c => c.Orders)` would still
track Orders unless `LoadCollection` explicitly applies
`AsNoTracking()` to its sub-query. Stage 4 plumbs
`QueryTrackingBehavior` through the loader and applies it via
`ApplyTrackingBehavior`.

## Known limitations and follow-up work

### Out-of-scope features

These currently produce wrong results or unhandled exceptions and
need separate tickets:

- **Filtered Include** — `Include(c => c.Orders.Where(...).OrderBy(...).Take(N))`.
  The fan-out architecture supports this naturally; the implementation
  needs to lift the filtering / ordering / paging out of the
  navigation lambda and apply it to the loader's sub-query.
- **Many-to-many (`ISkipNavigation`)** — Include of skip navigations
  throws a clean `InvalidOperationException` referencing this
  follow-up. Implementation would either traverse a join entity in
  two fan-out steps or use a `$lookup`-with-`let` pipeline.
- **Include over set operations** — `db.Customers.Union(other).Include(c => c.Orders)`.
  Set operations aren't supported in general (see EF-220); Include on
  top of them adds another layer that would need careful unwrap.
- **Include with client filter** — When the user has called something
  like `.AsEnumerable().Where(...).Include(...)`, the Include lands
  on a non-translatable source. The current behavior is failure at a
  different layer than expected; cleanup needs both the underlying
  client-eval ticket and Include-aware handling here.
- **Composite keys / shadow FK or PK** — `GetClrPropertyOrThrow`
  emits a clear "not yet supported" error for both. The runtime
  helpers would need an alternative key-extraction path (via
  `InternalEntityEntry`) and a multi-column predicate builder.

### Known limitation of the fan-out approach

- **N+1 round-trips.** `LoadCollection` and `LoadReference` issue one
  sub-query per principal/dependent. For Northwind's 830 Orders +
  `Include(o => o.Customer)` that's 830 round-trips. The natural fix:
  buffer the outer materialization, batch the FK values across all
  principals, and issue one `Where(c => fks.Contains(c.Id))` per
  navigation level — caching results by key. This is the in-memory
  provider's effective behavior and what relational providers do as
  AsSplitQuery. **The biggest perf win available** and the highest-
  priority follow-up.

- **Cross-query identity resolution under
  `AsNoTrackingWithIdentityResolution`.** EF's identity resolution
  works within a single materialization, not across separate
  queries. With fan-out, two dependents pointing at the same
  principal resolve to two distinct related instances under
  no-tracking + identity resolution. Tracking-mode `TrackAll` does
  dedupe correctly via the DbContext state manager (verified by
  `Include_reference_dependent_to_principal_materializes`). Fix
  would require a shared per-outer-query in-memory cache feeding the
  same instances back from each sub-query.

### Spec-test sweep status

Stage 5 reduced `EF-117` overrides from 499 to ~347 in
[`failing-spec-tests.md`](failing-spec-tests.md). The remaining 73
failures across the suite cluster into the out-of-scope categories
above — none are caused by bugs in the implemented Stage 1–4
functionality.

### `$lookup` as an opt-in alternative

The original plan called for `$lookup` (server-side join) as an
alternative strategy alongside fan-out, selectable per-query. This
is unstarted. Likely shape: a per-query operator (perhaps
re-purposing EF Core's `AsSingleQuery()`) that bypasses the
`IncludeJoinUnwrapper` and instead translates the Include into a
`$lookup` pipeline stage. Most useful when the outer result set is
large and the related sets per principal are small.

## Test surface

| Test category | Where | Count |
|---|---|---|
| Focused functional tests | `tests/.../FunctionalTests/Query/IncludeTests.cs` | 9 (each named for the include shape it covers) |
| EF Core spec test conformance | `tests/.../SpecificationTests/Query/NorthwindInclude*QueryMongoTest.cs` (4 files) | ~950 across the four suites |
| Knock-on spec tests | `NorthwindQueryFiltersQueryMongoTest`, `NorthwindQueryTaggingQueryMongoTest`, `NorthwindCompiledQueryMongoTest`, `Mapping/BuiltInDataTypesMongoTest` | ~50 that use Include incidentally |

To verify a change to the Include path:

```bash
# Smoke test — fast (~1 second)
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
    -c "Debug EF10" --no-build --filter "FullyQualifiedName~IncludeTests"

# Spec sweep — ~40 seconds
dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
    -c "Debug EF10" --no-build --filter "FullyQualifiedName~NorthwindInclude"

# Multi-EF
# (invoke the /test-all skill)
```

## Where to dig in for follow-up work

- **Batching perf fix** — start in `MongoIncludeCompiler.LoadCollection`
  and `LoadReference`. The most surgical change buffers principals in a
  thread-local during outer enumeration and runs one bulk
  `Where(p => pks.Contains(p.Id))` per Include level. The outer
  enumeration entry point is `QueryingEnumerable.Enumerator.MoveNextHelper`.
- **Filtered Include** — extend `ExtractIncludeChainPath` to also
  capture the filter / orderby / take lambdas. They're in the
  `Subquery`'s method chain alongside the `Where(o => fk == pk)`.
  Compose them onto the loader's sub-query.
- **`$lookup` opt-in** — would live as a separate
  `MongoIncludeCompiler.LoadCollectionViaLookup` path, dispatched on
  a per-query flag. The hard part is producing a `$lookup` BSON stage
  the driver's LINQ v3 provider understands; might require dropping
  to raw `BsonDocument` pipeline construction.
- **Skip navigations (many-to-many)** — the classifier already throws
  the right error. Replace the throw with a two-step fan-out via the
  join entity (or a `$lookup` with `let`/`pipeline`).

## Commit history

Each stage is a self-contained commit on the `EF-117a` branch:

| Stage | Commit | Description |
|---|---|---|
| Stage 0 | `2d8e82e` | Partition `IncludeExpression`; add M2M guard |
| Stage 1 | `80bd366` | Collection-include fan-out via `LoadCollection` |
| Stage 2 | `641bc6d` | Reference Include via `IncludeJoinUnwrapper` + `LoadReference` |
| Stage 3 | `f6230e8` | `ThenInclude` chains via recursive `Include(path)` |
| Stage 4 | `fc55af0` | Tracking-mode propagation + edge-case coverage |
| Stage 5 | `25c55c0` | Spec-test sweep + multi-EF stability |

Each commit message records the design decision, the diff from the
original plan, and the verification it ran. The per-stage write-up
in [`EF-117-include-implementation-progress.md`](EF-117-include-implementation-progress.md)
captures the same in narrative form.
