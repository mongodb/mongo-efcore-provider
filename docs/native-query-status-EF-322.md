# Native LINQ Translation (EF-322) — Status Report

*Generated 2026-07-26 · branch tip `1dd7862` on `NativeQueryOngoing` (stacked on `main`, unmerged).*
*Test measurements below are point-in-time against the **EF10 specification suite** at that tip.*

---

## 1. The epic in one line

Epic **EF-322 — "Native LINQ query provider (ground-up rebuild)"** replaces the *translation* half of
the Query subsystem: the provider builds MongoDB aggregation pipelines (MQL) itself from a canonical query
AST and uses the C# driver only to *execute* them (BSON, cursors, sessions, transactions). Driver-LINQ
remains as a gated fallback (`MongoQueryMode.DriverLinq`) until native reaches parity, then the delegation
code is deleted.

**Native is already the default execution path.** Per the provider's versioning rubric this is *not* a
breaking change: query results are unchanged, any shape native does not support falls back automatically,
and `UseQueryMode(MongoQueryMode.DriverLinq)` restores the previous path.

Query modes:

- `Native` (default) — build the pipeline natively; silently fall back to driver-LINQ for unsupported shapes.
- `DriverLinq` — always use the driver's LINQ provider (the pre-EF-322 path).
- `NativeOnly` — native or bust; throw `NativeTranslationNotSupportedException` instead of falling back
  (a diagnostic mode — a full run is a "what actually goes native" report).

---

## 2. Sub-project scoreboard (7 planned)

| SP | Scope | Ticket | Status |
|---|---|---|---|
| SP1 | AST foundation — filter / sort / paging | EF-323 | ✅ Done |
| SP2 | Predicate breadth — `$expr` renderer + operator long tail | EF-329 | ✅ Done |
| SP3 | Projection pushdown — server-side `$project` | EF-331 | ✅ Done |
| SP4 | Scalar cardinality — Count / First / Any / aggregates | EF-336 | ✅ Done |
| SP5 | Collection Includes | EF-339 | 🟡 Flat collection Include done; several shapes deferred |
| SP6 | Remaining operators — GroupBy, SelectMany, set-ops, Distinct, OfType, non-canonical paging | EF-344 / EF-347 | 🟡 Largely done; VectorSearch + long tail deferred |
| SP7 | Materializer perf — one-pass stream → POCO | — | ⛔ Not started (the capstone) |

Refactor interludes (not user-facing): EF-330 (extract `MongoSelectDefinition`), EF-332 (separate the
native-translation layer from QMTEV), EF-334 (centralize the is-native gate into `ClassifyNativeDisposition`).

**Delivery mechanics.** Native sub-projects ship as stacked branches on `NativeQueryOngoing`, one squashed
commit each: SP1 → SP2 → SP3 → SP4 → SP5 → SP6 (GroupBy / set-ops / Distinct / OfType) → EF-347 SelectMany
slices → tip `1dd7862`. Nothing is merged to `main` yet — the whole native stack lands at parity/cutover.

---

## 3. What's native today

- **Filtering / sorting / paging (SP1–SP2).** Single-collection whole-entity queries generate the
  `BsonDocument[]` pipeline directly. Predicate breadth: nullable equality / `== null` (IS NULL),
  collection `Contains` → `$in`/`$nin`, `string.StartsWith`/`EndsWith`/`Contains` → `$regularExpression`,
  and field-to-field / arithmetic comparisons → `$expr`. Predicate rendering prefers an index-usable query
  dialect; `$expr` is the last resort.
- **Projection (SP3 + EF-347 arithmetic slice).** Terminal anonymous-type / DTO projections over top-level
  member accesses → native `$project`. **Numeric-arithmetic computed leaves** (`+ - * / %`, e.g.
  `new { Total = o.Price * o.Qty }`) go native via `MongoExpressionTranslator.TryTranslateValue`, in a plain
  terminal `Select`, after a whole-entity set-op, and as a set-op operand.
- **Scalar cardinality (SP4).** Entity reducers (`First`/`FirstOrDefault`/`Single`/`SingleOrDefault`) via
  synthesized `$limit`; scalar aggregates (`Count`/`LongCount`/`Any`/`All`/`Sum`/`Min`/`Max`/`Average`) via
  `$count`/`$group` with an explicit empty-input contract (`MongoEmptyAggregateBehavior`).
- **Collection Include (SP5).** Single-level collection `Include` + projected collection `.Count` (`$size`)
  via a flat `$lookup` (no `$unwind`), materialized through the DOM shaper.
- **GroupBy (SP6 / EF-344).** `GroupBy(key).Select(aggregate)` → `$group` + flattening `$project`.
  Scalar/composite/DTO keys; `Count`/`LongCount`/`Sum`/`Average`/`Min`/`Max` accumulators over plain field-refs.
- **Set operations, Distinct, OfType (SP6 / EF-347).** Whole-entity terminal `Union`/`Concat` and
  `Intersect`/`Except` (source-tagging pipeline); projected `Distinct` (degenerate `$group`);
  `OfType<TDerived>()` over TPH (discriminator `$eq`/`$in` conjunct).
- **SelectMany (SP6 / EF-347 — the just-finished tail):**
  - Owned-collection `SelectMany` projecting element members (inner-`Select`, explicit, query-syntax, bare-element)
  - Owned filtered-inner and owned correlated-beyond-outer
  - Reference (FK-correlated) `SelectMany`: projected + bare-whole-entity, inner-element filter, filter
    correlated beyond the FK
  - Nested (exactly two-level) reference `SelectMany` → two chained `$lookup` + `$unwind`
  - Single-scope arithmetic computed leaf inside a SelectMany trailing projection (the final tail item, tip `1dd7862`)

---

## 4. What still falls back to driver-LINQ

**By design (correct results under `Native`/`DriverLinq`; throws only under `NativeOnly`):**

- **Computed long tail** — string transforms (`ToUpper`/`Substring`), date-part extraction, `Math.*`,
  type-changing casts, integer-result `Divide` (Guard A: `$divide` is non-truncating).
- **Reference `Include`**, nested/transitive `ThenInclude`, filtered `Include`, collection-of-collection
  Include (lookup/streaming machinery built but dormant).
- **Non-native GroupBy shapes** — computed keys (`g.OrderDate.Year`), computed accumulator operands, bare
  `GroupBy(key)` terminating on `IGrouping`, user `resultSelector`, post-group slot operators
  (Where/OrderBy/Skip/Take as HAVING), correlated / cross-collection keys.
- Bare-scalar & whole-entity `Distinct`; non-whole-entity / non-terminal / mismatched set-ops.
- Contains / ElementAt / Last; computed aggregate selectors; `All` with a comparison predicate (EF-335).
- Guarded-out for correctness: value-converter / non-default `BsonRepresentation` operands (arithmetic,
  GroupBy keys, Distinct keys, OfType discriminators).

**Hard-fails in every mode (no driver-LINQ oracle):** cross-collection SelectMany forms outside the native
slice, three-level+ nested SelectMany, whole-outer SelectMany, and any operator composed *after* a native
SelectMany (shaper-rebuild limitation).

**Not native at all:** `VectorSearch`; non-TPH `OfType`.

---

## 5. Deferred items still on the epic

- **SP7 — Materializer perf (the capstone, not started).** One-pass stream → POCO with no intermediate BSON
  objects. Reducer/aggregate and collection-array Include materialization still route through the DOM shaper,
  not the streaming shaper (`BsonRowReader` null / non-nullable-list gap). Reference-Include streaming
  machinery is built but dormant.
- **Parity cutover.** Once native reaches parity: retire the driver-LINQ fallback and delete the delegation code.
- **Minor SelectMany follow-ons (EF-347 leftovers):** cross-scope computed leaf (`o.Discount * i.Price`),
  the inner-`Select`-form computed-leaf binder.

---

## 6. Carried tickets (filed during EF-347, all open)

| Ticket | Type | Summary | Severity |
|---|---|---|---|
| **EF-353** | Task | Native bare owned-element SelectMany can't materialize **nested owned members** — currently a clean decline (`GetNavigations().Any()` guard); lifting it needs a re-rooted projection mapping | Feature gap, clean decline |
| **EF-354** | Bug | `SelectMany(o => o.Items, (o,i) => o)` (whole-outer, explicit method-call spelling) **crashes** ("Id missing") instead of declining cleanly; query-syntax spelling already declines | Loud crash, not wrong data |
| **EF-355** | Bug | Filtered reference SelectMany: folded-predicate split in `TrySplitCorrelation` can **silently drop a `!= null` inner filter** → returns all children | Silent wrong data, **latent/unreachable** today (EF emits nested, not folded, shape) |
| **EF-356** | Bug | Mixed whole-entity + computed-arithmetic projection (`new { c, Total = c.Age * c.Score }`) returns **silently wrong** values (`Score²`) — mixed shaper has no `BinaryExpression` handling | Silent wrong data, **pre-existing**, pinned by a documenting test |

Of these, **EF-356** (reachable today under `Native`) and **EF-355** (latent) are the two that produce (or
could produce) *silent* wrong data.

---

## 7. Which tests require driver-LINQ to pass — empirical measurement

Measured by the two-sweep subtraction on the **EF10 specification suite** at tip `1dd7862`:

> `{ tests requiring driver-LINQ } = { pass under Native } − { pass under NativeOnly }`

| Mode | Passed | Failed | Skipped | Total |
|---|---:|---:|---:|---:|
| `Native` (default) | **4589** | 0 | 19 | 4608 |
| `NativeOnly` (fallback removed) | **2192** | **2397** | 19 | 4608 |

Because `Native` fails **zero** tests, every `NativeOnly` failure was a `Native` pass — so the set is exact:

> **2397 spec tests currently require the driver-LINQ fallback to pass.** They go green under `Native`
> only by silently falling back; remove the fallback and they throw `NativeTranslationNotSupportedException`.

Scope note: spec suite, EF10, at this tip. The functional `Native*` tests self-parametrize across modes so
they don't count here; unit tests don't touch a database.

### 7.1 By test class (24 classes)

| Count | Class |
|---:|---|
| 549 | NorthwindMiscellaneousQueryMongoTest |
| 236 | NorthwindAggregateOperatorsQueryMongoTest |
| 226 | NorthwindWhereQueryMongoTest |
| 208 | NorthwindGroupByQueryMongoTest |
| 198 | NorthwindSelectQueryMongoTest |
| 132 | NorthwindEFPropertyIncludeQueryMongoTest |
| 132 | NorthwindIncludeQueryMongoTest |
| 130 | NorthwindStringIncludeQueryMongoTest |
| 114 | NorthwindSetOperationsQueryMongoTest |
| 104 | NorthwindIncludeNoTrackingQueryMongoTest |
| 78 | NorthwindJoinQueryMongoTest |
| 62 | NorthwindNavigationsQueryMongoTest |
| 56 | VectorSearchMongoTest |
| 56 | VectorSearchExactMongoTest |
| 29 | NorthwindQueryFiltersQueryMongoTest |
| 24 | NorthwindBulkUpdatesMongoTest |
| 14 | NorthwindKeylessEntitiesQueryMongoTest |
| 14 | BuiltInDataTypesMongoTest |
| 12 | NorthwindAsNoTrackingQueryMongoTest |
| 10 | NorthwindDbFunctionsQueryMongoTest |
| 6 | NorthwindChangeTrackingQueryMongoTest |
| 3 | NorthwindCompiledQueryMongoTest |
| 3 | NorthwindAsTrackingQueryMongoTest |
| 1 | NorthwindQueryTaggingQueryMongoTest |

### 7.2 By *why* they fall back (failure-message buckets)

| Count | Cause |
|---:|---|
| 873 | **Non-entity projection not natively representable** — computed / scalar / client-eval projection long tail (`Select` shapes, casts, client methods) |
| 794 | **Query not natively representable** — joins, cross-collection navigation, non-native GroupBy shapes, misc operators |
| 54 | **Reference-nav `$lookup` not supported** — reference `Include` / navigation lookups |
| 13 | Non-constant regex pattern (EF-247) |
| 10 | Predicate renderer gap (`Not` over unsupported subtree) |
| 507 | `Assert.Throws` **exception-type mismatch** — feature unsupported in *every* mode; test pins the *driver-LINQ* exception type, native throws `NativeTranslationNotSupportedException` instead |
| 74 | `Assert.Contains` — expected *error-message text* differs between the two throws |
| 66 | `ArgumentOutOfRangeException` (index) — a materialization / shaper gap surfaced only under `NativeOnly` |

### 7.3 The 2397 split into two meaningfully different kinds

- **~1744 need driver-LINQ for correct *results*** (the `NativeTranslationNotSupportedException` data
  buckets: 873 + 794 + 54, plus the small regex/renderer ones). These are the genuine coverage gaps —
  remove the fallback and the user gets an exception instead of data.
- **~647 differ only in *failure shape*** (507 exception-type + 74 message + 66 index). These features are
  unsupported in *every* mode (no correct data is produced either way); the tests pass under `Native` only
  because the override pins the *driver-LINQ* exception type/message. Strictly they "require driver-LINQ to
  pass as written," but they aren't lost functionality — at parity cutover these overrides get re-baselined
  to assert the native exception.

Representative examples: `All_client`, `Client_method_in_projection_requiring_materialization_1`,
`Cast_on_top_level_projection_brings_explicit_Cast` (projection long tail); `All_after_GroupBy_aggregate`,
`Anonymous_projection_Distinct_GroupBy_Aggregate` (GroupBy shapes); `VectorSearch_Memory_floats`
(vector search).

### 7.4 How to reproduce

```bash
# Build once (both MONGODB_URI and ATLAS_URI unset → isolated atlas-local container per run)
dotnet build tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj -c "Debug EF10"

# Baseline (Native default)
dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --logger "trx;LogFileName=native.trx" --results-directory <dir>

# Native-only (fallback removed)
MONGODB_EF_NATIVE_ONLY=1 dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --logger "trx;LogFileName=nativeonly.trx" --results-directory <dir>

# The tests that require driver-LINQ = the Failed set in nativeonly.trx
# (equivalently: pass-set(native.trx) − pass-set(nativeonly.trx))
```

`MONGODB_EF_NATIVE_ONLY=1` flips every spec context's `DbContextOptionsBuilder` to
`MongoQueryMode.NativeOnly` (`MongoTestStore.AddProviderOptions`), so any query that would otherwise
silently fall back throws instead.

---

## 8. Bottom line

SP1–SP4 are complete; SP5–SP6 are substantially complete with a well-characterized fallback set; the
SelectMany tail (SP6) is effectively finished. **The remaining native work is SP7 (the one-pass streaming
materializer) plus the parity cutover that retires driver-LINQ.** Empirically, 2397 EF10 spec tests still
lean on the driver-LINQ fallback — roughly 1744 for correct results (real coverage gaps) and ~647 only for
the expected exception shape (re-baselined at cutover).
