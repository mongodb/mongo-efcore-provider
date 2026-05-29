# EF-117 — Cross-collection `Include` implementation plan

## Context

EF Core's eager-loading operators — `Include`, `ThenInclude`, and their
`EF.Property` / string overloads — let users say "when you load entity A,
also load related entity B" so the materialized graph is complete and the
navigation property is populated before the `DbContext` is disposed. See
[Loading related data — eager loading](https://learn.microsoft.com/en-us/ef/core/querying/related-data/eager).

The MongoDB EF Core provider currently supports `Include` only for
**embedded** navigations (owned types serialized into the same BSON
document — handled by [`MongoNavigationExtensions.IsEmbedded()`][embed]
in `Extensions/MongoNavigationExtensions.cs` and by the existing
`IncludeReference` / `IncludeCollection` helper pair in
[`MongoProjectionBindingRemovingExpressionVisitor`][visitor]). Any
`Include` whose target lives in a **separate collection** is rejected at
`Visitors/MongoProjectionBindingRemovingExpressionVisitor.cs:135-142`
with:

```
Including navigation 'Navigation' is not supported as the navigation is
not embedded in same resource.
```

This is the EF-117 issue. It manifests as **~499 failing specification
tests** (see `docs/failing-spec-tests.md`) — every override in the
`NorthwindInclude*QueryMongoTest` family asserts the current failure,
plus large knock-on failures in `NorthwindQueryFiltersQueryMongoTest`,
`NorthwindQueryTaggingQueryMongoTest`, and a few mapping tests that
include navigation properties as part of their assertions.

The intended outcome: cross-collection `Include` and `ThenInclude` work
end-to-end for all non-many-to-many shapes (reference and collection
navigations, both directions of the FK, arbitrary depth), with the
existing embedded-types path preserved and many-to-many (`ISkipNavigation`)
explicitly rejected with a clean message tracked under a follow-up ticket.

[embed]: ../src/MongoDB.EntityFrameworkCore/Extensions/MongoNavigationExtensions.cs
[visitor]: ../src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoProjectionBindingRemovingExpressionVisitor.cs

## Strategy

**Fan-out queries first**, with an opt-in `$lookup` (single-pipeline)
alternative left for a follow-up. This mirrors EF Core's in-memory
provider, fits naturally into the existing shaper architecture, and reuses
the `IncludeReference` / `IncludeCollection` / `GenerateFixup` helpers
that are already in the provider (originally ported from the in-memory
provider).

The mechanics, per `IncludeExpression` whose navigation is **not**
embedded and **not** a skip navigation:

1. **Compile time** — alongside compiling the principal's shaper, build
   a *parameterized* "include loader" for each cross-collection include:
   a `Func<MongoQueryContext, principal, IEnumerable<TRelated>>` (and an
   async sibling) that:
   - reads the principal's PK / FK values directly off the materialized
     CLR instance,
   - runs an inner `MongoExecutableQuery` against the related entity's
     collection, with `$match` parameterized by those key values,
   - returns the materialized related entities (using the standard
     entity shaper, so identity resolution and tracking apply).
2. **Shaper time** — after the principal is materialized, invoke the
   loader and pass the result to `IncludeReference` (single related) or
   `IncludeCollection` (collection navigation). The existing helpers do
   the navigation assignment, inverse-navigation fixup, and
   `SetIsLoaded` bookkeeping.

`ThenInclude` falls out naturally — each `IncludeExpression`'s
`NavigationExpression` can itself contain further `IncludeExpression`
nodes, and the inner shaper is compiled by the same machinery
recursively, so a per-include sub-shaper can carry its own nested
sub-loaders.

### Why fan-out first, `$lookup` later

| | Fan-out | `$lookup` |
|---|---|---|
| Round-trips | 1 + N (one per include) | 1 |
| Driver-LINQ plumbing | none new — each sub-query goes through the existing `MongoExecutableQuery` path | new — needs a typed lookup translator the driver's LINQ v3 provider doesn't expose ergonomically |
| Identity resolution | works for free via the standard entity shaper | requires custom handling of joined sub-docs |
| Memory | bounded by *each* result set | larger (Cartesian-like blow-up for chained `Include`s) |

For small principal result sets (the common `FirstOrDefault().Include(...)`
shape, paged lists, etc.) fan-out is often **faster** in wall-clock terms
than `$lookup` because each sub-query reads only the rows it needs and
the principal query reads no related-document payload. `$lookup` wins
when the principal set is large and the related sets are small per
principal. We intend to keep **both** strategies in the final shipping
provider, selectable per query — see "Follow-up work" below.

### Many-to-many

`ISkipNavigation` (the EF Core surface for many-to-many relationships)
is **out of scope**. The fix replaces the current "not embedded" error
with a clear `InvalidOperationException` citing a new follow-up ticket
when the navigation is a skip navigation; spec-test overrides are
re-tagged accordingly.

### Filtered `Include`

`Include(c => c.Orders.Where(...))` is **out of scope** for this branch
and will be tracked separately. The fan-out architecture supports it
naturally (filters merge into the include sub-query's `$match`), so the
follow-up is purely additive.

## Staging

Each stage stops for review. The verification command for every stage is
either a focused `dotnet test` filter or — at stage boundaries — the
`/test-all` skill across EF8/EF9/EF10. The `failing-spec-tests.md` count
is the running tally of remaining `EF-117` overrides.

---

### Stage 0 — Plumbing, scaffolding, M2M guard

**Goal.** Lift the blanket "not embedded" rejection, partition the
include cases (embedded / cross-collection / skip-nav), wire the
skip-nav guard, and add narrow failing functional tests that drive the
later stages without depending on the spec-test mega-fixtures.

**Changes.**

- `src/.../Query/Visitors/MongoProjectionBindingRemovingExpressionVisitor.cs`
  - Both `IncludeExpression` branches (the `VisitExtension` case ~line 133
    and the `VisitMethodCall(Select... include lambda)` case ~line 311):
    classify `includeExpression.Navigation`:
    1. **Skip navigation** → throw a new `InvalidOperationException`
       referencing follow-up ticket (TBD JIRA, e.g. `EF-117b`).
    2. **Embedded** (existing `IsEmbedded()` check) → existing path
       unchanged.
    3. **Cross-collection** → defer to a new code path that records the
       include for compile-time sub-query generation (Stage 1+).
       During Stage 0 this branch throws a *placeholder* exception
       (`NotImplementedException("EF-117: cross-collection Include — Stage N pending")`)
       so the tests we add below have a stable failure mode.
- New file `src/.../Query/Visitors/MongoIncludeCompiler.cs` — empty
  scaffold for the include-loader compilation that Stages 1-3 fill in.
  Document its responsibilities in a leading comment (no executable
  code yet beyond the type declaration and a TODO list).
- `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/` — add a new
  `IncludeTests.cs` file modeled on the existing `WhereTests.cs` /
  `OwnedEntityTests.cs` patterns. Stage 0 contributes four `[Fact]`
  shells that **all assert the placeholder failure** for now:
  - `Include_reference_dependent_to_principal_throws_pending`
  - `Include_collection_principal_to_dependents_throws_pending`
  - `ThenInclude_chain_throws_pending`
  - `Include_skip_navigation_throws_not_supported` — asserts the
    *final* M2M message that ships, not a placeholder.

These tests will flip from "asserts the placeholder" to "asserts
success" as each subsequent stage lands, giving us a stable per-stage
green/red signal independent of the spec-test fixtures.

**Verification.**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~IncludeTests"
```

The four new tests pass. No regression in the existing embedded-types
tests (`OwnedEntityTests`, `Mapping/OwnedEntityElementNameTests`,
`Update/OwnedNavigationPropertyCrudTests`).

**Stop for review.**

---

### Stage 1 — Reference navigation, dependent → principal

**Goal.** `query.Include(o => o.Customer)` — a reference navigation
whose FK lives on the principal entity (`Order.CustomerId` → `Customer`).
Single level, no `ThenInclude`. Synchronous and asynchronous paths both
work. Tracking and no-tracking both work.

**Changes.**

- `src/.../Query/Visitors/MongoIncludeCompiler.cs` — implement the
  cross-collection sub-query builder:
  - **Input**: an `IncludeExpression`, the surrounding
    `QueryCompilationContext`, and the principal `IEntityType`.
  - **Output**: a struct (or record) carrying
    - the target collection name,
    - a parameterized `MongoExecutableQuery` for the related collection
      (`$match` keyed on the FK values; parameters live on
      `QueryContext.Parameters` under sentinel names),
    - a compiled "key extractor" `Func<TPrincipal, TKey>` that reads the
      FK values off the materialized principal,
    - the entity shaper for the related type (compiled by the standard
      pipeline — re-enters `MongoShapedQueryCompilingExpressionVisitor`
      for the related-entity sub-shape).
- `src/.../Query/Visitors/MongoProjectionBindingRemovingExpressionVisitor.cs`
  — for the cross-collection branch:
  - Add the include to a new `_pendingCrossCollectionIncludes` list
    (mirrors `_pendingIncludes` but with the compiled loader payload).
  - In `AddIncludes(...)`, after materializing the principal, emit
    code that:
    1. calls the loader,
    2. invokes the existing `IncludeReference` helper with the result.
  - `IncludeReference` itself is unchanged — it already accepts a
    materialized `TRelated relatedEntity`.
- `src/.../Query/Visitors/MongoShapedQueryCompilingExpressionVisitor.cs`
  — thread the cross-collection-include loaders through
  `CompileShapedQuery` so they end up captured in the compiled shaper's
  closure.
- `src/.../Query/MongoExecutableQuery.cs` — may need a new constructor
  flavor or `AdditionalState` entry to carry sub-queries. Prefer keeping
  the principal `MongoExecutableQuery` shape unchanged and instead
  storing sub-loaders as captured closures in the shaper lambda (the
  loader holds its own `MongoExecutableQuery`).
- **Sub-query execution path.** The loader, when invoked, must:
  - bind FK values into the surrounding `QueryContext.Parameters` (so
    the LINQ-v3 driver pipeline parameterizes correctly),
  - execute the sub-query (`MongoClient.GetCollection<TRelated>(...).AsQueryable()` +
    `Where` on the parameters),
  - apply the related-entity shaper to each `BsonDocument`,
  - return the materialized sequence.
  Reuse `MongoClientWrapper.Execute` (the existing entry from
  `MongoShapedQueryCompilingExpressionVisitor.TranslateQuery`) so MQL
  logging, transaction binding, and retries Just Work for sub-queries
  too.
- **Async.** Provide an `IncludeReferenceAsync` companion to the
  existing `IncludeReference`. The shaper picks the async path when the
  query result cardinality / `CancellationToken` is async-flavored.

**MQL shape expected.**

```text
Orders.{ "$match" : { ... } }
Customers.{ "$match" : { "_id" : { "$in" : [<FK values of materialized principals>] } } }
```

Note: for a single principal we can use `_id : value`; for an
enumerable result we batch the FK lookup with `$in`. Decide between
"one sub-query per principal" and "batched `$in`" — *batched* is
the in-memory provider's effective behavior because the shaper sees
the whole result set; for MongoDB we should do the same.

**Verification.**

```bash
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~IncludeTests.Include_reference_dependent_to_principal"
```

The corresponding spec tests start passing — focus the spec-test
verification on the tracked-/no-tracking-reference cases:

```bash
dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~NorthwindIncludeQueryMongoTest.Include_reference"
```

For each spec test that now translates, drop the `Assert.ThrowsAsync` /
`AssertTranslationFailed` wrapper, assert the new MQL baseline, and
delete the `// Fails: Include issue EF-117` line (per
`docs/failing-spec-tests.md`'s guidance).

**Stop for review.**

---

### Stage 2 — Collection navigation, principal → dependents

**Goal.** `query.Include(c => c.Orders)` — a collection navigation
where the FK lives on the dependent entity (`Order.CustomerId`). Single
level, no `ThenInclude`.

**Changes.**

- Extend `MongoIncludeCompiler` to handle the collection case:
  - sub-query filter is `{ <fk-field> : { "$in" : [<principal PKs>] } }`
    (or `{ <fk-field> : <pk> }` for single-principal queries),
  - the loader returns `IEnumerable<TRelated>` grouped by FK value so the
    shaper can dispatch each group to the correct principal,
  - or, alternatively (preferred for symmetry with EF in-memory): the
    loader materializes a flat sequence and the shaper indexes into it
    by FK match — same as `IncludeCollection`'s existing handling but
    sourced from the sub-query instead of from a nested
    `BsonArrayProjection`.
- `IncludeCollection` is reused unchanged.
- Verify `IClrCollectionAccessor.GetOrCreate(...)` is invoked for empty
  collection navigations (the existing helper already does this at
  line ~610 — make sure cross-collection includes hit that path too).
- Handle ordering: collection navigation results retain insertion order
  (no explicit `$sort` is needed unless the user added one via filtered
  Include, which is out of scope here).

**Verification.**

```bash
dotnet test ... --filter "FullyQualifiedName~IncludeTests.Include_collection"
dotnet test ... --filter "FullyQualifiedName~NorthwindIncludeQueryMongoTest.Include_collection"
```

Spec-test overrides for the collection-include shapes flip from
"asserts failure" to "asserts MQL". Expect on the order of 60-80 spec
tests to flip in this stage. Update `failing-spec-tests.md`'s EF-117
count.

**Stop for review.**

---

### Stage 3 — `ThenInclude` chains and multi-level cycles

**Goal.** `query.Include(c => c.Orders).ThenInclude(o => o.OrderDetails)`,
`query.Include(o => o.Customer).ThenInclude(c => c.Address)` (where
`Address` may be owned/embedded — confirm interaction with the
existing embedded path), and cycles like
`query.Include(c => c.Orders).ThenInclude(o => o.Customer)`.

**Changes.**

- `MongoIncludeCompiler` becomes recursive: when an
  `IncludeExpression.NavigationExpression` is itself a shape that
  contains further `IncludeExpression` nodes, the inner shaper is
  compiled with its own nested cross-collection-include loaders.
- Identity-resolution: tracked queries naturally deduplicate via the
  state manager; `NoTrackingWithIdentityResolution` already uses a
  stand-alone state manager (`MongoShapedQueryCompilingExpressionVisitor`
  line 144) — ensure sub-query results flow through that same manager.
- Cycles: verify that a sub-query whose target is the same entity type
  as a higher-up entry in the include path terminates correctly via the
  `SetIsLoaded` bookkeeping in `IncludeReference` /
  `IncludeCollection` (relying on the change tracker to avoid
  re-loading the same key).

**Verification.**

```bash
dotnet test ... --filter "FullyQualifiedName~IncludeTests.ThenInclude"
dotnet test ... --filter "FullyQualifiedName~NorthwindIncludeQueryMongoTest.Include_with_cycle"
dotnet test ... --filter "FullyQualifiedName~NorthwindIncludeQueryMongoTest.ThenInclude"
```

Plus the `Include_references_then_include_*` and
`Include_collection_then_include_collection` families across all four
spec test classes.

**Stop for review.**

---

### Stage 4 — Tracking-mode coverage and edge cases

**Goal.** All three `QueryTrackingBehavior` values produce correct
results across reference, collection, and `ThenInclude` shapes; explicit
`.AsTracking()` / `.AsNoTracking()` overrides honored; cycles + tracking
work correctly; empty includes (no matching related entities) leave
references `null` and collections initialized but empty.

**Changes.**

- Primarily verification, not new code — most of this should already
  work because `IncludeReference` / `IncludeCollection` correctly
  branch on `entry == null` for tracking vs. no-tracking. Expect to
  uncover at most small fixes in the loader's parameter binding or in
  how the stand-alone state manager is plumbed.
- Add targeted functional tests for any gap surfaced by the spec-test
  sweep.

**Verification.**

```bash
dotnet test ... --filter "FullyQualifiedName~NorthwindIncludeNoTrackingQueryMongoTest"
dotnet test ... --filter "FullyQualifiedName~NorthwindIncludeQueryMongoTest.Include_with_cycle_does_not_throw"
```

**Stop for review.**

---

### Stage 5 — Spec-test sweep, EF8/9/10 stability, docs

**Goal.** Eliminate the EF-117 backlog across all four
`NorthwindInclude*QueryMongoTest` classes plus the knock-on failures in
`NorthwindQueryFiltersQueryMongoTest`, `NorthwindQueryTaggingQueryMongoTest`,
and `Mapping/BuiltInDataTypesMongoTest`. Run `/test-all` to verify EF8,
EF9, EF10 are stable.

**Changes.**

- For every previously-failing spec test override that now translates:
  remove the `Assert.ThrowsAsync<InvalidOperationException>` /
  `AssertTranslationFailed` wrapper, supply the captured-MQL baseline
  via `AssertMql(...)`, drop the `// Fails: Include issue EF-117` line.
  Use `EF_TEST_REWRITE_BASELINES=1` (per `TestMqlLoggerFactory`) for
  bulk baseline capture, then hand-review each pipeline string for
  correctness.
- For any spec tests that **don't** translate after Stage 5's
  implementation — e.g. filtered Include (deferred), or M2M Include
  (deferred) — retag from `EF-117` to the new follow-up ticket. Update
  `docs/failing-spec-tests.md` (move the entries to the new ticket row,
  reduce the EF-117 count).
- Verify EF version conditionals where the spec base differs between
  EF8/EF9/EF10 (`#if EF8`, `#if !EF8 && !EF9`) — at least one test
  (`Include_collection_with_right_join_clause_with_filter`) is already
  guarded; others may surface.

**Verification.**

```bash
# Per EF version
dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build

# All three EF versions in parallel
# (invoke the /test-all skill)
```

After Stage 5 the EF-117 count in `failing-spec-tests.md` should be **0**
(any remaining Include-related failures retagged to the new follow-up
tickets for filtered Include and many-to-many).

**Stop for review** — this is the merge point for the EF-117 ticket.

---

## Critical files

| File | Stage(s) | Role |
|---|---|---|
| `src/.../Query/Visitors/MongoProjectionBindingRemovingExpressionVisitor.cs` | 0, 1, 2, 3 | Lifts the embedded-only restriction; dispatches embedded vs. cross-collection vs. skip-nav |
| `src/.../Query/Visitors/MongoShapedQueryCompilingExpressionVisitor.cs` | 1 | Threads sub-query loaders through shaper compilation |
| `src/.../Query/Visitors/MongoIncludeCompiler.cs` (new) | 0–3 | Owns cross-collection sub-query compilation |
| `src/.../Query/MongoExecutableQuery.cs` | 1 | May gain new `AdditionalState` entries for sub-queries |
| `src/.../Query/QueryingEnumerable.cs` | 1 | Reused as-is for the principal query; sub-queries reuse the same execution path indirectly |
| `src/.../Extensions/MongoNavigationExtensions.cs` | 0 | `IsEmbedded()` already correctly partitions embedded vs. not — no change expected, but verify |
| `tests/.../FunctionalTests/Query/IncludeTests.cs` (new) | 0–4 | Per-stage focused functional tests |
| `tests/.../SpecificationTests/Query/NorthwindInclude*QueryMongoTest.cs` (4 files) | 1–5 | Override-cleanup sweep |
| `tests/.../SpecificationTests/Query/NorthwindQueryFiltersQueryMongoTest.cs`, `NorthwindQueryTaggingQueryMongoTest.cs`, `Mapping/BuiltInDataTypesMongoTest.cs` | 5 | Knock-on cleanups (these tests fail today because *their* fixtures or assertions touch Include) |
| `docs/failing-spec-tests.md` | 5 | Drop the EF-117 row (or move it to the follow-up ticket) |

## Reusable building blocks already present

- **`IncludeReference<TIncludingEntity, TIncludedEntity>` and
  `IncludeCollection<TIncludingEntity, TIncludedEntity>`** at
  `MongoProjectionBindingRemovingExpressionVisitor.cs:513-613`. The
  helper signatures take an already-materialized related entity (or
  enumerable) and do the navigation assignment + `SetIsLoaded` +
  inverse-navigation fixup. Cross-collection includes feed the
  materialized sub-query output into these helpers — no changes to the
  helpers themselves are expected.
- **`GenerateFixup(...)`** at `MongoProjectionBindingRemovingExpressionVisitor.cs:615-640`
  — synthesizes the inverse-navigation fixup lambda. Reused as-is.
- **`MongoNavigationExtensions.IsEmbedded()`** —
  `Extensions/MongoNavigationExtensions.cs`. Existing partition between
  embedded and not-embedded; reused.
- **`MongoShapedQueryCompilingExpressionVisitor.TranslateQuery<TSource>`**
  at `Visitors/MongoShapedQueryCompilingExpressionVisitor.cs:160-186`.
  The sub-query loader reuses this entry to get a parameterized
  `IQueryable<TRelated>` and then an `IMongoQueryProvider` for
  execution.
- **`TestMqlLoggerFactory.AssertBaseline(...)`** at
  `tests/.../SpecificationTests/Utilities/TestMqlLoggerFactory.cs` and
  the `AssertMql(...)` helper in each spec-test class. Already handles
  multi-statement assertions (one per query), which fan-out includes
  need.

## Follow-up work (out of scope for EF-117)

- **`$lookup` (server-side join) as an opt-in alternative.** Track
  separately. Strategy: provide a per-query selector — likely
  re-purposing EF Core's `AsSplitQuery()` / `AsSingleQuery()` operators
  (relational-only today, but plumbed through `IQueryable` extension
  methods we can intercept in `MongoQueryableMethodTranslatingExpressionVisitor`)
  — so users can pick the strategy without changing query shape. The
  fan-out infrastructure built here is the default; `$lookup` becomes
  an alternative driver-LINQ-v3 sub-tree replacing the loader closure.
- **Filtered `Include`.** Track separately. The fan-out architecture
  supports it natively: `Where` / `OrderBy` / `Skip` / `Take` from the
  filtered-include lambda compose into the sub-query.
- **Many-to-many (`ISkipNavigation`).** Track separately. Requires a
  two-step fan-out (join table → target collection) or a `$lookup` with
  a `let` and a nested pipeline.

## End-to-end verification

After each stage:

```bash
# Stage-local functional tests
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~IncludeTests"

# Stage-local spec tests
dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~NorthwindInclude<stage-specific filter>"
```

At the Stage 5 merge point:

```bash
# Full sweep, all three EF versions in parallel
# (invoke the /test-all skill)
```

Connection is taken from `MONGODB_URI` (already running locally per the
user's setup). The `failing-spec-tests.md` EF-117 count drops to zero
(modulo deferred filtered-Include and M2M reassignments).