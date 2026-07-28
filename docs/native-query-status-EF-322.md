# Native LINQ Translation (EF-322) — Status Report

*Generated 2026-07-26 · last updated 2026-07-28 · branch tip `b087957` on `NativeQueryOngoing` (stacked on
`main`, unmerged).*
*Test measurements below are point-in-time against the **EF10 specification suite**. The §7 two-sweep totals
were **re-measured at `b087957`**; the §7.1/§7.2 breakdowns are still the `1dd7862` measurement, adjusted only
for the two tests that have since flipped (see the note in §7).*

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
| SP7 | Materializer perf — one-pass stream → POCO | — | 🟡 **Phase 1 done** (one-pass materializer, `e38587f`); Phase 2 (streaming breadth) not started |

Beyond the seven planned sub-projects, an **owned-data (embedded-document) work stream** has since landed as
five further stacked slices — these were not a planned SP, but they are where native coverage grew most after
SP7 Phase 1:

| Slice | Scope | Commit |
|---|---|---|
| 1 | Owned single-reference whole-entity queries go native (+ stream) | `690b487` |
| 2 | Owned-collection whole-entity goes native (+ streams) | `275c90e` |
| 3 | Owned single-ref **sub-property** predicates / sorts / projections (dotted paths) | `2a9b56e` |
| 4 | Owned-collection **`Any`** quantifier predicates → `$elemMatch` | `791037b` |
| 5 | Owned-collection **`All`** quantifier predicates → negated `$elemMatch`; **closes EF-335** | `b087957` |
| 6 | Owned-collection **`.Count`** in a predicate — array-index `$exists` (constant tier) / null-safe `$size` inside `$expr` (parameterized/degenerate tier) | `7532b15` |

Refactor interludes (not user-facing): EF-330 (extract `MongoSelectDefinition`), EF-332 (separate the
native-translation layer from QMTEV), EF-334 (centralize the is-native gate into `ClassifyNativeDisposition`).

**Delivery mechanics.** Native sub-projects ship as stacked branches on `NativeQueryOngoing`, one squashed
commit each: SP1 → SP2 → SP3 → SP4 → SP5 → SP6 (GroupBy / set-ops / Distinct / OfType) → EF-347 SelectMany
slices → `1dd7862` → SP7 Phase 1 (`e38587f`) → the five owned-data slices above → tip `b087957`. Nothing is
merged to `main` yet — the whole native stack lands at parity/cutover.

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
- **Materialization: one-pass streaming (SP7 Phase 1, `e38587f`).** The native streaming materializer is now
  *one-pass*: a per-execution `MongoEntityMaterializerSerializer<TEntity>` is the `Aggregate<TEntity>` output
  serializer, so deserialize **is** materialize — no intermediate `RawBsonDocument`, no second reader/context.
  Allocation vs pre-SP7 native: whole-entity no-track 19.1→5.4 MB (−72%, ~1.73× the raw-driver floor), `Where`
  9.6→2.8 MB, tracked 25.2→11.5 MB; wall-clock ≈ the driver floor. Materialization-only — **zero** query-shape,
  result or eligibility change.
- **Owned (embedded) whole-entity queries (owned-data slices 1–2).** A whole-entity query over an entity with
  owned single-reference navigations (`OwnsOne`, nested) *or* owned collections (`OwnsMany`, incl. mixed and
  shared-CLR-type) now goes native — previously *always* fell back. Root cause was the **gate**, not
  materialization: EF auto-includes owned navs as `Select(x => IncludeExpression(x, nav))`, which matched no
  pass-through predicate. Flat / mixed / shared / empty owned collections **stream** via SP7 Phase 1; a
  collection whose element carries a further navigation, and collection-of-collection, route to native DOM.
- **Owned single-reference sub-property dotted paths (owned-data slice 3).** A predicate, sort key, or
  projection leaf reaching *through* owned single-reference navs to a scalar leaf — `Where(e => e.Home.City == x)`,
  `OrderBy(e => e.Home.Geo.Country)`, `Select(e => new { e.Home.City })` — resolves to a dotted document path.
  One shared gate (`TryResolveMember`) lights up all three surfaces at once.
- **Owned-collection quantifier predicates — `Any` *and* `All` (owned-data slices 4–5).** `Any()`/`Any(pred)`
  and `All(pred)` over an owned collection navigation, negated forms, nesting in either order, and collections
  reached through owned single-reference hops. `Any(pred)` → `$elemMatch`; bare `Any()` → an array-index
  `{"path.0": {$exists}}` test (correct for empty/missing/null arrays alike, unlike `{$ne: []}`); `All(pred)` →
  `{path: {$not: {$elemMatch: ¬pred}}}`, which is also correct for empty/missing/null arrays because LINQ's
  `All` is vacuously true there. An owned `SelectMany` whose inner filter is itself an owned-sub-collection
  `Any` — previously a hard fail in every mode with no driver-LINQ oracle — also goes native.
  **The `All` half rests on a new exact-complement negator (`MongoExpressionNegator`), which also closed
  EF-335** (top-level `All` with a comparison predicate). Its central rule, verified against a live server:
  `$eq`/`$ne` may be **inverted** because they partition every BSON value, but the four relational operators
  must be **`$not`-wrapped**, because `{$gt: 5}` and `{$lte: 5}` do *not* partition — neither matches a field
  that is missing, null, or of another BSON type, so inverting them would report `All == true` where LINQ says
  false. Index note: root-scope `{f: {$not: {$gt: v}}}` is IXSCAN, but the owned-collection `All` form is a
  COLLSCAN — a deliberate correctness-over-index trade (the index-friendly alternative returns wrong answers),
  and the already-shipped `!Any(...)` form scans equally. See
  `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md` for both quantifier notes.
- **Owned-collection `.Count` in a predicate (owned-data slice 6).** `Where(b => b.Posts.Count > 2)` and all
  six comparison operators, both operand orders, `.Count`/`.Count()`/`.LongCount()`, a constant *or*
  parameterized threshold, a count reached through owned single-reference hops, and a count nested inside a
  quantifier's element predicate now go native — previously always fell back. A constant threshold renders as
  the query-dialect array-index existence test (`{"path.k": {$exists: true|false}}`) — the same family bare
  `Any()` already used, now unified with it as `Count >= 1`; a parameterized or degenerate threshold renders
  `$expr` over a null-safe `$size` (`$ifNull` maps a missing/`null` array to `[]`, since bare `$size` against
  either is a hard server error). Negation *inverts* the operator (an exact complement, since `$exists`
  partitions the value space) rather than `$not`-wrapping it, the documented exception to the `All` slice's
  `$not`-wrap rule. Index note, as measured (not assumed): all four relational array-index forms come back
  **COLLSCAN** with both a collection- and leaf-level multikey index present — the form is still required
  regardless, since it is the only one legal inside `$elemMatch` and the only one correct for missing/null
  arrays. See `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md`'s dedicated `.Count` note for the full
  two-tier mechanism, the negator exception, and the settled finding that EF Core rewrites `Count() > 0` into
  `Any()` upstream (so that one spelling is unreachable via ordinary LINQ for the constant-tier `GreaterThan`
  arm at `n = 0`).

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
- Contains / ElementAt / Last; computed aggregate selectors. (**`All` with a comparison predicate — EF-335 —
  is NO LONGER on this list: closed by the owned-data slice 5 negator.**)
- Guarded-out for correctness: value-converter / non-default `BsonRepresentation` operands (arithmetic,
  GroupBy keys, Distinct keys, OfType discriminators).
- **Owned-collection predicate/projection long tail (EF-322), as it stands after the `Any`, `All`, *and*
  `.Count` slices:** an embedded-collection projection (`Select(b => b.Posts.Count)`, an array projection), a
  non-query-dialect owned-collection element predicate (field-to-field / arithmetic — no query-dialect form to
  put inside `$elemMatch`, and for `All` no exact complement either), a **correlated** element predicate (one
  referencing the enclosing entity — declined by a dedicated guard, because `$elemMatch` cannot reference the
  enclosing document at all), and a two-scope (cross-scope, inside a `SelectMany`) owned quantifier. An
  owned-COLLECTION intermediate hop in a dotted sub-property path also still declines (slice 3 covers
  single-reference hops only).

**Hard-fails in every mode (no driver-LINQ oracle):** cross-collection SelectMany forms outside the native
slice, three-level+ nested SelectMany, whole-outer SelectMany, and any operator composed *after* a native
SelectMany (shaper-rebuild limitation).

**Not native at all:** `VectorSearch`; non-TPH `OfType`.

---

## 5. Deferred items still on the epic

- **SP7 Phase 2 — streaming breadth (Phase 1 landed; Phase 2 not started).** Phase 1 delivered the one-pass
  materializer (see §3). Still deferred: **reducer/aggregate** streaming, **collection-Include array**
  streaming, and **reference-Include** streaming — the last blocked behind making reference `Include` native at
  all. Those shapes still route through the DOM shaper, not the streaming one. Also minor: delete the
  now-dead `RawBsonDocument` branch + `BsonRowReader`, which Phase 1 made unreachable.
- **Parity cutover.** Once native reaches parity: retire the driver-LINQ fallback and delete the delegation code.
- **Minor SelectMany follow-ons (EF-347 leftovers):** cross-scope computed leaf (`o.Discount * i.Price`),
  the inner-`Select`-form computed-leaf binder.
- **Owned-collection predicate follow-ons (EF-322), as they stand after slices 4–6:** embedded-collection/array
  projections remain deferred (see §4) — `All` and `.Count`-in-predicate no longer do. A **correlated**
  element predicate needs more than a two-scope translator: `$elemMatch` cannot reference the enclosing
  document, so it would have to render as a top-level `$expr` over `$filter`/`$allElementsTrue`. Relativizing
  the owned single-reference dotted-path scalar resolver (`TryResolveOwnedFieldPath`) the way the quantifier
  resolver is scoped would let a two-scope owned dotted access work without its current blanket decline.

---

## 6. Carried tickets (filed during EF-347, all open)

| Ticket | Type | Summary | Severity |
|---|---|---|---|
| **EF-353** | Task | Native bare owned-element SelectMany can't materialize **nested owned members** — currently a clean decline (`GetNavigations().Any()` guard); lifting it needs a re-rooted projection mapping | Feature gap, clean decline |
| **EF-354** | Bug | `SelectMany(o => o.Items, (o,i) => o)` (whole-outer, explicit method-call spelling) **crashes** ("Id missing") instead of declining cleanly; query-syntax spelling already declines | Loud crash, not wrong data |
| **EF-355** | Bug | Filtered reference SelectMany: folded-predicate split in `TrySplitCorrelation` can **silently drop a `!= null` inner filter** → returns all children | Silent wrong data, **latent/unreachable** today (EF emits nested, not folded, shape) |
| **EF-356** | Bug | Mixed whole-entity + computed-arithmetic projection (`new { c, Total = c.Age * c.Score }`) returns **silently wrong** values (`Score²`) — mixed shaper has no `BinaryExpression` handling | Silent wrong data, **pre-existing**, pinned by a documenting test |
| **EF-357** | Bug | Bare embedded-collection `.Count` projection (`Select(b => b.Posts.Count)`) throws `ArgumentException` in **every** query mode — a `MongoProjectionBindingExpressionVisitor` gap, not a native decline | Hard fail every mode, **pre-existing** (reproduced on `main`), pinned by a documenting test |

Of these, **EF-356** (reachable today under `Native`) and **EF-355** (latent) are the two that produce (or
could produce) *silent* wrong data.

---

## 7. Which tests require driver-LINQ to pass — empirical measurement

Measured by the two-sweep subtraction on the **EF10 specification suite**, re-measured at tip `7532b15`
(the `.Count`-in-a-predicate slice) with **zero further delta** from `b087957` — see the note below the table:

> `{ tests requiring driver-LINQ } = { pass under Native } − { pass under NativeOnly }`

| Mode | Passed | Failed | Skipped | Total |
|---|---:|---:|---:|---:|
| `Native` (default) | **4589** | 0 | 19 | 4608 |
| `NativeOnly` (fallback removed) | **2194** | **2395** | 19 | 4608 |

Because `Native` fails **zero** tests, every `NativeOnly` failure was a `Native` pass — so the set is exact:

> **2395 spec tests currently require the driver-LINQ fallback to pass.** They go green under `Native`
> only by silently falling back; remove the fallback and they throw `NativeTranslationNotSupportedException`.

**Delta since the `1dd7862` measurement: 2 tests** (`NativeOnly` 2192 → 2194 passing, 2397 → 2395 failing).
Both are `NorthwindAggregateOperatorsQueryMongoTest.Select_All` (`async: True`/`False`), which went native via
the EF-335 negator in owned-data slice 5. Its `Native`-mode MQL baseline was also re-baselined in that slice:
the driver fallback's trailing `{ "$project" : { "_id" : 0, "_v" : null } }` stage disappears, which is the
signature of native routing. Results were unchanged.

Everything else measured identical across the two tips — notably, the four owned-data slices *before* slice 5
produced **zero** spec delta, because Northwind has no owned collections or owned sub-property coverage. The
`.Count`-in-a-predicate slice (owned-data slice 6, `7532b15`) measured the **same story a third time**: `Native`
4589/0/19 and `NativeOnly` 2194/2395/19, both axes checked per-test (not just the aggregate), exact match to the
`b087957` figures above — expected, since Northwind still has no owned collections for the new `.Count`
machinery to touch, and `Native` failing zero tests is itself proof no `Native`-mode MQL baseline moved (a
baseline that had changed would show up as a failure against its checked-in string, which none did). Two
cautions for whoever re-measures next:

- **A total of 4608 is correct.** One intermediate measurement during slice 5 reported 4601 (7 low) and was
  wrong; the figure here reproduced exactly on a fresh base-vs-branch run and again on the final three-version
  sweep. Do not "correct" this table downward without a clean re-measurement.
- **Check both axes per test.** A test can be `NativeOnly`-failing *and* have a `Native`-mode MQL baseline that
  a slice changes — `Select_All` is exactly that. An inventory built only from the `NativeOnly` pass set will
  miss such flips (it missed this one).

Scope note: spec suite, EF10, at this tip. The functional `Native*` tests self-parametrize across modes so
they don't count here; unit tests don't touch a database.

### 7.1 By test class (24 classes)

*Counts are the `1dd7862` measurement with the only known change applied: `NorthwindAggregateOperatorsQueryMongoTest`
236 → 234 for the two `Select_All` tests that went native. The per-class attribution was not otherwise
re-derived at `b087957`; the totals in the table above were.*

| Count | Class |
|---:|---|
| 549 | NorthwindMiscellaneousQueryMongoTest |
| 234 | NorthwindAggregateOperatorsQueryMongoTest |
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

*As measured at `1dd7862`. Not re-derived at `b087957`; the 2-test delta above came out of this set, but which
bucket it left was not re-attributed. Note the "Predicate renderer gap (`Not` over unsupported subtree)" bucket
did **not** empty: owned-data slice 5 taught `RenderUnary` to render `Not` over a query-native comparison, but
a `Not` whose operand is a conjunction or a nested `Not` still declines by design.*

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

### 7.3 The fallback set splits into two meaningfully different kinds

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
SelectMany tail (SP6) is finished. **SP7 Phase 1 (the one-pass materializer) has landed**, cutting native
allocation 54–72% to roughly the raw-driver floor. Since then, a six-slice **owned-data work stream** has made
embedded documents largely native: whole-entity (single-ref and collection), single-ref sub-property dotted
paths, both `Any` and `All` quantifier predicates — the latter closing **EF-335** — and `.Count` used in a
predicate, unified with bare `Any()` as one array-cardinality representation.

**The remaining native work is SP7 Phase 2 (streaming breadth: reducer/aggregate, collection-Include arrays,
reference-Include) plus the parity cutover that retires driver-LINQ.** The nearest owned-data follow-on is now
embedded-collection projections (the bare form is a separate, pre-existing hard-fail predating this whole work
stream; an arithmetic projection leaf containing a count already goes native as an incidental widening).

Empirically, 2395 EF10 spec tests still lean on the driver-LINQ fallback — roughly 1744 for correct results
(real coverage gaps) and ~647 only for the expected exception shape (re-baselined at cutover). That number has
barely moved across the owned-data stream, and for a good reason worth remembering: **Northwind has no owned
collections**, so this work stream's coverage gains are proven by the functional `Native*` suites, not by the
spec scoreboard. A flat spec number does not mean a slice achieved nothing.
