# Native LINQ query provider — project design

**Date:** 2026-06-23
**Status:** Design; productization in progress
**Branch:** `EF-322-native-query` (off `main`) · **JIRA:** EF-322
**Spike reference:** `spike/low-level-provider` (61-commit proof-of-concept; kept for reference, not merged)

---

## Scope & companion docs

**This is the full, agent-facing design — the complete *how*.** For the terse *what / why* (the
human-review entry point) read the companion **`2026-06-23-native-query-provider-overview.md`**; for
sub-project 1's detailed design read **`2026-06-20-mongo-query-ast-foundation-design.md`**.

In one line: replace the *translation* half of the Query subsystem — build MongoDB aggregation
pipelines ourselves from a canonical query AST and use the driver only to execute them — delivered as
a sequence of zero-regression sub-projects, with the driver-LINQ path kept as a gated,
user-selectable fallback until native reaches parity.

The sections below give the case for *why* this is the right call, then what the spike proved (so we
don't re-derive it), the target architecture in detail, the keep-vs-rebuild inventory, and the
per-sub-project plan.

---

## Why this rebuild

The current provider delegates LINQ translation to the driver's LINQ-v3 provider. That was a
reasonable bootstrap, but it caps two things we care about: performance and conformance.

### Performance / allocation

Headline numbers from the spike (validated against `origin/main` within ~1.4%; full table in the
spike's `benchmarks/.../results/2026-06-20-headline-three-config.md`):

| Shape (N=10k) | Driver-only (no EF) | Current provider (main) | Native (spike) |
|---|---|---|---|
| Where→ToList | 5.8 ms / 1.6 MB | 15.1 ms / 22.7 MB | 8.3 ms / 9.6 MB |
| Whole-entity ToList (no-track) | 8.3 ms / 3.1 MB | 33.4 ms / 45.3 MB | 15.6 ms / 19.1 MB |
| Whole-entity ToList (tracked) | — | 43.0 ms / 51.4 MB | 25.4 ms / 25.2 MB |
| Reference Include→ToList | 38.8 ms / 7.9 MB | 138.3 ms / 52.0 MB | 114.7 ms / 22.3 MB |

- **Native vs current provider:** ~51–58% less allocation on every shape; ~45–53% faster on
  allocation-heavy reads. Closes ~60–70% of the time/alloc gap to the raw driver on
  materialization-heavy shapes.
- **Native vs driver-only:** still ~4–6× allocation above the raw-driver floor — that residual is
  EF's model/shaper/tracking/Include machinery, the headroom for future work (the per-row double-pass
  is the largest single piece; see "What the spike proved").

### Conformance ceiling (the stronger argument)

The driver's LINQ provider (a) lacks operators (no `LeftJoin` translator, etc.) and (b) was **not
built to EF Core semantics**. So a class of EF Core spec tests fail or are `// Fails`-flagged
*because of the driver's LINQ provider*, and they are painful or impossible to fix without changing
the driver. A native translator written **to the EF Core spec tests** removes that ceiling: it lowers
the achievable-conformance limit from `min(MongoDB-can-express, driver-LINQ-supports-AND-matches-EF)`
down to just **`MongoDB-can-express`** — the only limit worth having.

> **Conformance reality check.** "the suite passes" during the spike means the *currently-enabled
> subset* of EF Core spec tests (overridden and not `// Fails`-flagged). That is **not** full spec
> conformance — many spec tests are unimplemented or `// Fails`. The real success metric for
> productization is **EF Core spec conformance**, not the spike's "native coverage of enabled tests"
> (a progress proxy, not the goal).

---

## What the spike proved (de-risked unknowns — start knowing these)

The spike (`spike/low-level-provider`, sub-projects A→E + Includes) exists to prove feasibility and
quantify the win. Its **code is reference, not code to preserve wholesale**; its **test/benchmark rig
and its learnings are the durable deliverable.** Established facts:

- **DOM-free streaming materialization works.** A forward-only `IBsonReader` materializer, reusing
  EF's structural-type-materializer injection via `ValueBufferTryReadValue` interception, is correct
  against the enabled spec suite and yields the allocation win. It is modeled on EF's relational
  JSON-column streaming materializer (`JsonEntityMaterializerRewriter`); because `IBsonReader` is a
  heap object it needs none of the `Utf8JsonReaderManager` ref-struct machinery.
- **Native pipeline execution works.** Build the pipeline as raw `BsonDocument[]` and run
  `IMongoCollection<BsonDocument>.Aggregate(session, pipeline)` → `IAsyncCursor` → streaming shaper
  (via Storage's `MongoClientWrapper.Execute`). No driver join/LINQ operator is needed:
  predicate/sort/paging → `$match`/`$sort`/`$skip`/`$limit`, and single-level reference Include →
  `$lookup`/`$unwind`, are all expressible as raw stages we build ourselves.
- **`RawBsonDocument` as a random-access row is a measured DEAD END** (+68–82% alloc — it re-scans
  per field). It only pays off *behind* a forward reader. (Spike result `2026-06-17-dom-free-C.md`.)
- **EF query-parameter shapes differ by version** — EF10 `QueryParameterExpression` / `Parameters`
  vs EF8/EF9 `ParameterExpression` / `ParameterValues`. Guard with `#if`.

### Known residual overhead (the per-row double-pass)

The spike's streaming path still allocates a per-row object graph the raw driver does not — the
largest part of the remaining native-vs-driver gap on no-tracking reads (~6× alloc; whole-entity
no-track 19.1 MB native vs 3.1 MB driver-only). **Cause:** to fit EF's `QueryingEnumerable` "cursor
yields `TSource` rows → shaper maps each row" model, the cursor yields a **`RawBsonDocument` per
row**, and the materializer opens a **fresh `BsonBinaryReader` + `ByteBufferStream` +
`BsonDeserializationContext` per row** — a *second* pass (bytes → `RawBsonDocument` → entity). The
driver-only path fuses read+materialize into one class-map serializer call straight off the batch
buffer (bytes → entity).

The materializer *logic* is correct and worth keeping; its **row-feeding** is what to redesign. The
endgame is that **EF reads directly from the server's cursor stream into the POCO with no
intermediate BSON object and no second copy** — the same one-pass shape the driver-only path gets
from its class-map serializer (its 3.1 MB floor). The mechanism: make the **cursor's own
`IBsonSerializer<TSource>.Deserialize` *be* EF's compiled materializer**, reading forward off the
batch buffer straight into the entity, so deserialization *is* materialization. That removes both the
per-row `RawBsonDocument` object and the per-row fresh `BsonBinaryReader` / `ByteBufferStream` /
`BsonDeserializationContext` the spike opens today. The more inherent "EF tax" that remains
(compiled-shaper delegate, `MaterializationContext`, per-sub-entity materializer blocks) is smaller.
Apportionment is reasoned, not yet profiled — **profile (dotnet-trace / allocation profiler) before
optimizing** (sub-project 7).

### Class-(c) intrinsic limits — reconnaissance (CONFIRM against spec tests)

Candidate EF-Core semantics MongoDB may not express — i.e. tests that would be `// Fails` on *any*
architecture. Treat as a hypothesis list to validate early; distinguish true class-c from class-b
("expressible with effort"):

- **Null / three-valued logic.** MongoDB `$eq:null` matches missing fields; comparisons type-bracket.
  EF/C# null and `!=` semantics diverge. Some expressible with `$type`/`$exists` guards (class-b,
  awkward); some genuinely divergent (class-c). The spike already falls back on nullable-equality.
- **Cross-type comparison & sort order.** Mixed-type fields sort by BSON type order, not .NET order.
- **String collation / case / culture.** `$regex` covers `StartsWith`/`Contains`;
  `StringComparison` / culture / case-insensitive semantics don't map cleanly to MongoDB collation.
- **DateTime/DateTimeOffset/TimeSpan.** UTC storage, `DateTimeKind`, offset preservation.
- **Decimal/numeric.** Decimal128 vs .NET decimal mostly OK; mixed int/double/decimal arithmetic +
  overflow differ.
- **Translate-or-throw contract.** Some spec tests assert a *specific exception* for untranslatable
  queries; the native provider must match EF's translate-or-throw behavior (right exception type).
- **Unspecified result order.** Results without `$sort` are unordered; some tests assume order.

### Native coverage baseline (strict mode, EF10, 2026-06-20) — a progress proxy

Running the suite in **strict native mode** (throws instead of falling back; failures = "still needs
driver LINQ") gives ~**64% SpecificationTests Query / ~82% FunctionalTests Query** fully native. The
biggest remaining buckets map directly onto sub-projects 2–6 below. (The spike drove this via the
`MONGODB_EF_NATIVE_QUERY=force` env var; productization replaces that with the config option's strict
mode — see "Pipeline selection, fallback & the gate".)

---

## Target architecture

Two architectural decisions, taken during the spike's productization brainstorming and **locked**:

- **AST structural model = A3 (hybrid).** A logical-slot `MongoSelectExpression` (EF
  `SelectExpression`-aligned, so the team's instincts transfer and future operators have a home) that
  **lowers to** a typed stage-list IR (`MongoStage[]`, mirroring MongoDB's real pipeline), which then
  **renders to** `BsonDocument[]`. Mongo-specific concerns — canonicalization, the two BSON dialects
  (query/match language vs aggregation-expression `$expr`), and future pushdown — live at the
  **lowering boundary**, not smeared across the visitor or the renderer. (Rejected: A1 = slots
  straight to BSON, too little dialect separation; A2 = bare stage-list, furthest from EF's model.)

- **Pipeline generation = B2 (compile-time template + per-execution bind).** The AST is built once at
  compile time, so lower/render it **once** into a cached `MongoPipelineFactory` whose parameter
  placeholders are bound to live values per execution. This moves translation off the hot path and is
  EF-relational-idiomatic. (Rejected: B1 = per-execution regeneration, the spike's approach, re-pays
  generation cost every run.)

A consequence of B2: the native-vs-driver decision **moves to compile time** and becomes
deterministic (on `IsNativeRepresentable` + lowering/rendering success), replacing the spike's
per-execution try/catch. Strict mode still surfaces gaps — just earlier.

### Compile-time flow

```
QMTEV  (MongoQueryableMethodTranslatingExpressionVisitor)
   │   Translate{Where,OrderBy,ThenBy,Skip,Take,Select,Join…} POPULATE slots
   │   (instead of returning null); STILL captures the raw chain for fallback
   ▼
MongoSelectExpression           [NEW] canonical AST (logical slots); evolves the shallow
   │                                  MongoQueryExpression into the structured IR
   │   MongoSelectLowerer        [NEW]
   ▼
IReadOnlyList<MongoStage>       [NEW] typed stage IR (Match/Sort/Skip/Limit/Lookup/Unwind)
   │   MongoQueryLanguageRenderer [NEW] (+ $expr renderer seam, stubbed at foundation)
   ▼
MongoPipelineFactory           [NEW] the B2 template: rendered stages + placeholder table
   │   per execution: factory.Build(parameterValues) → BsonDocument[]
   ▼
MongoClientWrapper.Execute  (Storage, unchanged) → IAsyncCursor → streaming/DOM shaper (kept)
```

### The AST node taxonomy

**`MongoSelectExpression`** — logical slots, built in the QMTEV; the evolution of today's
`MongoQueryExpression` (it *keeps* `CapturedExpression` and the `ProjectionMapping` plumbing so the
fallback and shaper binding keep working, and *adds* the structured slots that become the source of
truth for the native path):

```
MongoCollectionExpression        Source       // root collection (kept type)
MongoExpression?                 Predicate    // conjunction of Where bodies
IReadOnlyList<MongoOrdering>     Orderings    // ThenBy order preserved
MongoExpression?                 Limit        // Take
MongoExpression?                 Offset       // Skip
ProjectionMapping                Projection   // carried; client-side at parity (no $project yet)
IReadOnlyList<LookupExpression>  Lookups      // single-level reference Includes (kept type)
Expression?                      CapturedExpression     // raw LINQ chain, for driver-LINQ fallback
bool                             IsNativeRepresentable  // false ⇒ compile-time fallback
```

**`MongoExpression`** (the `SqlExpression`-analog; **dialect-agnostic**): `MongoFieldExpression`
(element name via `IProperty.GetElementName()`), `MongoConstantExpression` (literal, serialized
through the property's `IBsonSerializer`; baked into the template at compile time),
`MongoParameterExpression` (**the B2 placeholder**, bound per execution), `MongoBinaryExpression`
(parity set `==, !=, <, <=, >, >=, &&, ||`), `MongoUnaryExpression` (`Not`, bare-bool).

**`MongoStage`** (typed IR, 1:1 with a rendered pipeline document):
`MongoMatchStage`, `MongoSortStage`, `MongoSkipStage`, `MongoLimitStage`, `MongoLookupStage`,
`MongoUnwindStage`. Canonical lowering order: `$match → $sort → $skip → $limit → $lookup/$unwind`. An
operator arriving out of canonical order (e.g. `Where` after `Take`) sets
`IsNativeRepresentable = false` at parity; pushdown-into-subquery is the documented extension
(sub-project 6).

### Lowering, rendering & the dialect boundary

- **`MongoSelectLowerer`**: `MongoSelectExpression → MongoStage[]`, reading slots in canonical order,
  dropping empties. Lookups append `$lookup` + `$unwind` (the kept `NativeLookupStages` logic, same
  reference-only / no-pipeline-stages guards).
- **`MongoQueryLanguageRenderer`**: `MongoExpression → BSON` in the **query/match** dialect
  (`{field:{$gt:v}}`, `$and`/`$or`, bare-bool, `$ne`) — the only dialect the foundation needs
  (`$match` only). The AND-merge / field-collision logic carries over from
  `MongoPredicateTranslator.CombineAnd`.
- **`$expr` (aggregation-expression) renderer**: a stubbed seam that throws "not yet implemented" at
  the foundation; it is the entry point for the predicate-breadth sub-project. **Dialect choice is
  made in the lowerer, never in the QMTEV or the node types** — nodes stay dialect-neutral.

### Parameter binding (B2 mechanics)

Rendering walks the stage IR once, emitting `BsonDocument`s with **placeholder sentinels** where a
`MongoParameterExpression` sits, recording `(location, parameter name, serializer)` in a placeholder
table. The `MongoPipelineFactory` (rendered template + table) is produced once at compile time and
captured into the compiled shaper. Per execution, `factory.Build(parameterValues)` clones the
template and substitutes each placeholder, serializing through the recorded serializer. `#if` guards
bridge EF10 `QueryContext.Parameters` vs EF8/EF9 `QueryContext.ParameterValues`. Constants are baked
at compile time. *This clone-and-substitute is the perf delta over the spike's per-execution
re-walk.*

### Pipeline selection, fallback & the gate

**Which pipeline runs is a user-level configuration option on the DbContext options builder — never
an environment variable.** It is surfaced as an enum, `MongoQueryMode` (the public face of the
spike's internal `NativeQueryMode`), set when configuring the provider, e.g.
`options.UseMongoDB(client, db).UseQueryMode(MongoQueryMode.Native)`:

- `Native` — use the native pipeline, **automatically falling back to driver-LINQ per query** for
  shapes the native path cannot yet represent. This is the **default once the work ships**.
- `DriverLinq` — always use the legacy driver-LINQ pipeline. This is the **regression escape hatch**:
  even after native ships as the default, a user who hits a native-path regression can set this and
  get exactly the previous implementation back, per-DbContext, with no change beyond the option.
- `NativeStrict` — native only; **throw instead of falling back**. The coverage/diagnostic mode the
  test rig uses — it replaces the spike's `MONGODB_EF_NATIVE_QUERY=force` env var. A non-representable
  query throws at compile time, so the failure set is the "what still isn't native" report.

The option is part of **service-provider identity** (via the `MongoOptionsExtension` flag), so the
modes never collide in EF's compiled-query cache.

At compile time, in `MongoShapedQueryCompilingExpressionVisitor`: under `Native`, if
`IsNativeRepresentable` **and** lowering/rendering succeed ⇒ compile the native path, else ⇒ compile
the driver-LINQ path from `CapturedExpression` (the per-query fallback). Under `NativeStrict` that
fallback is a throw. Under `DriverLinq` the native path is never attempted. The `streaming` /
`DispatchingQueryingEnumerable` dual-shaper discipline is preserved.

**Why a config option (not a build-time or env switch):** native ships on by default, but the legacy
pipeline stays in the box and stays selectable for the whole transition. A regression in the new path
is then a one-line config change for the user — not a downgrade, a recompile, or a wait for a patch
release. That selectable fallback is precisely what lets us make native the default with confidence.

---

## Keep vs rebuild inventory

**Rebuild (the translation subsystem), ground-up on the AST:**
- The structured slot population in `MongoQueryableMethodTranslatingExpressionVisitor` (where
  `Translate{Where,OrderBy,Skip,Take,…}` return `null` today).
- The AST, lowerer, renderer, expression translator, and pipeline factory (the `[NEW]` types above).

**Delete (delegation scar tissue — removed, not refactored, once native covers their slice):**
- `Query/Visitors/MongoEFToLinqTranslatingExpressionVisitor.cs` (+`.LeftJoin.cs`, ~1,800 LOC) — exists
  only to rewrite the EF tree into a *driver-LINQ* tree (`Mql.Field`, `.As<serializer>`,
  `AppendStage`).
- The `LeftJoin → Join + LeftJoinResult` rewrite and the two-join-shape reconciliation
  (`_outer`/`_inner` driver shape vs `_lookup_<Nav>` native shape; `UsesDriverJoinFields`;
  `GetStreamingReferenceLookups()`).
- The shallow `MongoQueryExpression` / `CapturedExpression` raw-chain IR (after `MongoSelectExpression`
  fully supersedes it).
- The per-execution chain re-walkers `MongoPipelineTranslator` / `MongoPredicateTranslator` (replaced
  by the lowerer + renderer + `MongoExpressionTranslator`).

**Keep (carry forward, do not rewrite):**
- **The streaming materializer** — `MongoStreamingEntityMaterializerRewriter` + `BsonRowReader` +
  the `StreamingEligibility` predicate. Delegation-independent and EF-idiomatic; the source of the
  allocation win. (Its per-row feeding is redesigned in sub-project 7, not its logic.)
- **The DOM shaper**, the query-mode mechanism (now surfaced as a config option — see "Pipeline
  selection, fallback & the gate"), the `DispatchingQueryingEnumerable` dual-shaper.
- **Storage / update / transactions / serialization / CSFLE / metadata / value-generation** — all
  independent of the LINQ-delegation question.
- **The public query-mode config option** on `MongoDbContextOptionsBuilder` + its
  `MongoOptionsExtension` flag (the user-level gate). Resolved as an enum (`Native` / `DriverLinq` /
  `NativeStrict` — see "Pipeline selection, fallback & the gate"), not a bool, so it also expresses
  the strict diagnostic mode.
- **The entire test + benchmark rig** — the EF spec-conformance harness, `AssertMql`, the
  strict-mode native-coverage instrument, the benchmark project + baselines. The most
  valuable asset; path-agnostic.

The driver-LINQ path stays alive as the gated fallback during the rebuild and is retired at parity.

---

## Sub-project 1: AST foundation (designed, ready to plan)

**Scope:** stand up the AST + lowerer + renderer + pipeline factory and reach **parity** with the
spike's current native slice — single-collection filter / sort / paging + single-level reference
Include over whole-entity results. Driver-LINQ remains the gated fallback for everything else.
**Not** in scope: predicate breadth, projection pushdown, scalar cardinality, collection includes, the
materializer perf fix (those are sub-projects 2–7).

**QMTEV slot population** (the `Translate*` overrides that return `null` today):

| Override | Action |
|---|---|
| `TranslateWhere` | translate body via `MongoExpressionTranslator` → `MongoExpression`; AND-combine into `Predicate`. Un-translatable ⇒ `IsNativeRepresentable = false`, return `source`. |
| `TranslateOrderBy`/`TranslateThenBy` (+ descending) | append `MongoOrdering(keySelector, ascending)`; `OrderBy` resets, `ThenBy` appends. |
| `TranslateSkip`/`TranslateTake` | set `Offset`/`Limit` to a constant/parameter; enforce canonical order (Skip-before-Take, single each) else not-representable. |
| `TranslateSelect` | pure entity-materialization Select ⇒ no-op (full docs, client shaper). A *projecting* Select ⇒ not-representable (deferred to sub-project 3). |
| Join / Include | single-level reference lookups populate `Lookups`; anything else ⇒ not-representable. |

**Coexistence rule (critical):** the QMTEV *always still captures the raw chain*
(`CapturedExpression`) regardless of slot population, so the driver-LINQ fallback remains intact. The
set of "un-translatable" nodes is exactly where the spike falls back today; element-name resolution
and serializer-based value coercion carry over verbatim from `NativeExpressionHelpers` /
`MongoPredicateTranslator`. Only *where* the decision is made moves; the **acceptance set is
unchanged**.

**Success bar:** (1) full FunctionalTests + SpecificationTests green in `Auto` mode on EF10 **and**
EF8; (2) strict-mode native coverage **must not shrink** vs the spike (~64% spec / ~82% functional
Query); (3) new unit tests asserting slot population and rendered MQL for the parity operators
(`AssertMql`), **including parameterized queries proving the template binds correctly across
executions with different values** (the cache-correctness case B2 introduces); (4) benchmark headline
re-run (`Release EF10`, InProcess) native alloc/time **≥** the spike's.

---

## Sub-projects 2–7 (planned later, in order)

Each follows the same discipline (zero regressions, no coverage shrink, fallback behind it). Sizes
are rough; sequence is by dependency and by strict-mode coverage payoff.

2. **Predicate breadth + `$expr` renderer.** Build the aggregation-expression renderer behind the
   stubbed seam; extend `MongoExpressionTranslator` over the long tail — string methods,
   `Contains`/`IN`, computed expressions, date parts, member casts, nullable-equality. Largest
   strict-mode coverage bucket.
3. **Projection pushdown (`$project`).** Server-side projection for scalar / anonymous / DTO
   `Select`; retire the `ExecuteProjectedQuery` / `nativeMode:Off` cutout.
4. **Scalar cardinality.** `Count`/`First`/`Single`/`Any`/aggregates as `$count`/`$limit`/`$group`;
   retire the `ExecuteScalar` cutout.
5. **Collection Includes.** Collection / nested / filtered Includes beyond single-level reference
   (the corresponding `$lookup` shapes + collection materializer paths).
6. **Remaining operators.** `GroupBy` → `$group`, `SelectMany`, set operations, `Distinct`,
   `OfType` / type tests, `VectorSearch`, non-canonical `Skip`/`Take` (pushdown-into-subquery — the
   documented extension of the canonical-order rule).
7. **Materializer perf — direct stream→POCO, zero double copy.** The end state: EF reads **directly
   from the server's cursor stream into the POCO instance in a single forward pass**, with **no
   intermediate `BsonDocument`/`RawBsonDocument` and no second copy**. Today the native path is
   DOM-free but double-pass: the driver deserializes each row to a `RawBsonDocument` (pass 1), then
   the materializer opens a fresh `BsonBinaryReader`/`ByteBufferStream`/`BsonDeserializationContext`
   over its bytes and reads forward into the entity (pass 2). The fix collapses these into one pass by
   making the **cursor's own `IBsonSerializer<TSource>.Deserialize` *be* EF's compiled materializer** —
   it reads forward off the cursor batch buffer straight into the entity, so deserialization *is*
   materialization. This is structurally the driver-only class-map path with EF's shaper swapped in,
   and it targets the raw-driver allocation floor (the spike's largest remaining native-vs-driver
   gap). **Profile first** (dotnet-trace / allocation profiler) to confirm the apportionment between
   this double-pass and the inherent EF-shaper tax before building.

**Endgame:** when native reaches parity, retire the driver-LINQ fallback and delete the scar tissue
listed above. Triage the class-(c) list throughout so the conformance target stays honest.

---

## How to run things

- **MongoDB:** a replica set is required (`SaveChanges` uses transactions).
  `docker run -d --name ef-bench-mongo -p 27017:27017 mongo:8 --replSet rs0`, then
  `rs.initiate({_id:'rs0',members:[{_id:0,host:'localhost:27017'}]})`. Tests/benchmarks read
  `MONGODB_URI`.
- **Build:** configurations, not plain Debug/Release — `dotnet build MongoDB.EFCoreProvider.sln -c
  "Debug EF10"` (also validate `Debug EF8`).
- **Tests (per-assembly — a combined solution run causes spurious shared-DB cross-assembly
  failures):** `MONGODB_URI=... dotnet test tests/<assembly>/*.csproj -c "Debug EF10" --filter
  "FullyQualifiedName~Query"`.
- **Strict-mode coverage instrument:** configure the query mode to strict native (the config
  option's `NativeStrict` value) in the test context to make non-native queries throw — the failure
  set is the "what still isn't native" report. No env var; this replaces the spike's
  `MONGODB_EF_NATIVE_QUERY=force`.
- **Benchmarks:** `benchmarks/MongoDB.EntityFrameworkCore.Benchmarks/`, `-c "Release EF10"`,
  **InProcess** toolchain (the default BenchmarkDotNet toolchain breaks on the config-conditional
  csproj). `dotnet run -c "Release EF10"` = headline three-config set; `-- --query` = per-shape set;
  `-- --smoke` = fast correctness check.

---

## References

All spike artifacts live on `spike/low-level-provider` (kept for reference; not merged):

- **Program design:** `docs/superpowers/specs/2026-06-16-low-level-provider-migration-design.md`.
- **Sub-project specs/plans:** `docs/superpowers/specs|plans/2026-06-1{6,7,8}-*` (benchmark baseline
  A; native MQL B; DOM-free C/C′; owned collections D; native-query default + harden E; native
  reference Include).
- **Measurements:** `benchmarks/.../results/2026-06-1{6,7,8}-*.md` and
  `2026-06-20-headline-three-config.md`.
- **Spike Query code** (reference — keep the materializer, study but replace the translation):
  `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/` + the gate edits in
  `MongoShapedQueryCompilingExpressionVisitor.cs`.

On this branch, the sub-project-1 detail lives in
`docs/superpowers/specs/2026-06-20-mongo-query-ast-foundation-design.md`.

**Carried-over cleanup:** `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md` is stale — it still states
the provider "sits on top of the driver's LINQ v3 provider … does not generate aggregation BSON
itself," which the native path contradicts. Update it to describe the rebuilt architecture as the
native path lands.

