# Handoff — the `VectorSearch` slice of the EF-322 native-query rewrite

*Written 2026-08-06 for an agent with no prior context. Every factual claim below was re-verified against the
tree at `51a94d4f` on the date written, unless it is explicitly tagged **INFERRED** or **UNVERIFIED**. That
distinction is load-bearing in this repo: a confidently-wrong handoff is worse than a thin one. Where a claim
comes from an existing document rather than my own check, the document is named.*

---

## 0. START HERE

### 0.1 Branch state

- Repo: `/Users/arthur.vickers/code/mongo-efcore-provider`, branch **`NativeQueryOngoing`**, clean at
  **`51a94d4f`** ("EF-366: remove a BREAKING-CHANGES entry for a change no released package can observe").
- This branch is the rolling, **unmerged** native-query stack (draft PR #324). Everything lands here.
- The joins / reference-`Include` work stream (step 1 of the cutover order) is substantially delivered
  through **EF-379**. `VectorSearch` is **step 2** — the next scheduled slice.

### 0.2 Read these, in this order

1. **`docs/native-query-status-EF-322.md`** — the consolidated status report. Read §9.8 (execution order —
   it explicitly names `VectorSearch` as step 2 and says the slice opens with a Task-0 spike), then §9.1
   (where the 112 vector tests sit in the coverage gap), then §4/§5 for what else still falls back.
   **Caveat the doc states about itself: §7's counts are point-in-time.** I re-measured the vector-specific
   ones — see §3 below; they still hold exactly.
2. **`src/MongoDB.EntityFrameworkCore/Query/AGENTS.md`** — the Query-area contract. It is very long; the
   parts that matter here are the "Pipeline at a glance" diagram, the "Key entry points" list (especially
   `MongoSelectLowerer`, `MongoPipelineFactory`, `PlaceholderTable`), and the **"Common pitfalls"** section
   (the native catch-all whitelist, and "MQL shape cannot prove a query went native").
3. **`AGENTS.md`** (repo root) — build/test commands, the versioning rubric, and the "what does *not* count as
   a break" list (the native translator becoming the default path, and the exact emitted MQL, are both
   explicitly carved out).
4. **`.claude/agents/vector-search-reviewer.md`** — the read-only reviewer that will review this slice. Short.
   Its "Escalate to user" list includes *"Change to the preprocessor extraction/re-insertion of
   `VectorSearch`"*, which this slice will almost certainly touch.
5. Optional but recommended: **`docs/superpowers/specs/2026-07-31-groupby-join-uncorrelated-inner-decline-design.md`
   §2.7** — a worked cautionary example of getting a breaking-change judgement wrong by measuring on the
   development branch instead of the release tags. Two paragraphs; read them before writing any
   `BREAKING-CHANGES.md` entry.

### 0.3 The FIRST task: a Task-0 spike (do this before designing anything)

> **Read §8 first — as of 2026-08-06 the owner has ruled on all six open questions, and three of those rulings
> narrow the spike below.** In particular: the bare-scalar `Select` bucket is **out** of this slice, `__score`
> is **in**, and both diagnostics **must keep working**. Where the text below still presents one of those as
> undecided, §8 wins.

The status doc (§9.8 step 2) already prescribes a Task-0 spike, on the grounds that `$vectorSearch` must be
the first pipeline stage and that cuts against the lowerer's stage ordering. My research says the stage-order
problem is **real but the smaller of the two problems**, and the spike should be scoped wider than the doc
implies. Concrete questions, in priority order:

**Q1 — Server constraint, measured, not assumed.** Confirm against a live atlas-local container that a
`$vectorSearch` preceded by any other stage is rejected, and record the exact server error text. I did **not**
run this (see §2.1: I have strong circumstantial evidence but no direct measurement). Also probe whether
`$sort` / `$skip` / `$limit` composed *after* `$vectorSearch` behave (nothing in the repo covers those — only
`$match` and `$project` are covered; see §2.3).

**Q2 — The parameterization problem, which I believe is the real blocker and which the status doc does not
mention at all.** By the time the driver-LINQ bridge runs, the query vector, the limit and the
`VectorQueryOptions` have all been turned into **EF query parameters**, not constants — verified, see §2.4.
The bridge copes because it re-runs **per execution** with a live `QueryContext`. The native path does not
work that way: it renders a pipeline template **once at compile time** and only substitutes `PlaceholderTable`
sentinels per execution (the "B2" design; see `Query/AGENTS.md`). So the spike must answer:
  - Can a `PlaceholderTable` sentinel stand in for a whole `queryVector` array, and for the `limit`, inside a
    `$vectorSearch` stage body? (`PlaceholderTable` + `MongoValueRenderer` are the machinery; I did not test
    an array-valued or option-object-valued substitution.)
  - Index-name resolution, the `VectorSearchNeedsIndex` warning, and the `Exact`-plus-`NumberOfCandidates`
    validation currently all read the **runtime** option value (§2.4). Where do they move to on a native path
    — compile time with a partial answer, or `Build(parameterValues)` time?
  - Cheapest viable answer to check first: can the native path just call
    `PipelineStageDefinitionBuilder.VectorSearch(...)` (what the bridge already does) at `Build` time to
    produce the stage `BsonDocument`, rather than growing a hand-written renderer? **INFERRED that this is
    possible; not tested.**

**Q3 — What "make it native" has to cover to be worth anything.**

> **CORRECTED 2026-08-06 by the Task-0 spike, and this was my worst error in this document.** I wrote that
> "106 of the 112 failures are *composed* shapes, not the bare `VectorSearch(...)` call". **That is false.**
> Attributed case by case, the 82-test bucket is **70 bare `VectorSearch(...)` calls, 8 `preFilter`, and 4
> `.Where`** — exactly one test method composes a `Where`. (Independently re-verified: `pre_filter` is 4
> methods of which 2 are `Skip`ped → 2 × 2 classes × 2 async = 8; the only `.Where` not followed by a `Select`
> is one method → 4; 82 − 12 = 70.) There is no "82-test `Where` bucket"; **the majority of the prize is the
> bare call.** Worse, `.Where` is never the blocker for *any* of the 112 — the 24 projection-bucket tests all
> compose the same `.Where`, a shape the native translator already handles. See
> `2026-08-06-vectorsearch-spike-findings.md` §Q3. Everything below this box is the original, pre-spike text.

106 of the 112 failures are *composed*
shapes, not the bare `VectorSearch(...)` call (§3). Establish, per shape, what the native path is missing:
  - `.Where(pred)` after `VectorSearch` — the 82-test bucket. Probably the cheapest win.
  - `.Select(e => e.Author)` — a **bare-scalar** projection, which is the SP3-wide bare-projection boundary,
    not a vector-search problem. This is a chunk of the 24-test bucket and may not be winnable in this slice
    at all. Decide early; it changes the slice's size estimate dramatically.
  - `__score` — read via `Mql.Field(e, "__score", …)` **and** via `EF.Property<double>(e, "__score")`. This is
    a synthetic field with **no backing `IProperty`** and no native analogue today (§2.5). The native IR does
    have a node for exactly this shape (`MongoElementRefExpression` — a raw path read with no `IProperty`),
    which is a promising lead. **INFERRED; not tried.**

**Q4 — What the two existing gates actually do.** Read §1 before you design. The status doc's one-line
account of the gate is incomplete, in a way that matters for scoping.

**Deliverable of Task 0:** a spike-findings doc under `docs/superpowers/specs/`, following the shape of
`2026-08-05-EF-379-spike-findings.md`, then a design doc, then implementation. **Stop after the spike for the
owner's review** (§4).

---

## 1. Current disposition — VERIFIED, and it CONTRADICTS the received account

The belief I was asked to check was: *the preprocessor lifts the `VectorSearch(...)` call out of the tree
before nav-expansion and re-inserts it after; because the call never reaches slot population, the gate
detects it via `ContainsVectorSearch(CapturedExpression)` in `ClassifyNativeDisposition` and routes the whole
query to driver-LINQ.*

**The lift/re-insert half is exactly right. The "never reaches slot population" half is FALSE, and the
consequence is that there are TWO independent gates, not one.**

### 1.1 What is verified true

- **The lift.** `MongoQueryTranslationPreprocessor.Process` calls
  `VectorSearchExtractor.RemoveVectorSearchCalls(query, out var removed)`, then `base.Process(query)` (which is
  where EF's nav-expansion runs), then `VectorSearchReplacer.ReplaceVectorSearchCalls(query, removed)`. The
  in-file comment states the reason: *"Nav expansion throws for IQueryable methods that it is not aware of"*.
  The extractor only lifts when `methodCallExpression.IsVectorSearch() && methodCallExpression.Arguments[0] is
  QueryRootExpression` — i.e. only at the query root. (File:
  `src/MongoDB.EntityFrameworkCore/Query/MongoQueryTranslationPreprocessor.cs`.)
- **`VectorSearch` is root-only by API construction, not merely by convention.** Both public overloads in
  `Microsoft.EntityFrameworkCore.MongoQueryableExtensions` (file
  `src/MongoDB.EntityFrameworkCore/Extensions/MongoQueryableExtensions.cs`) take `this DbSet<TSource> source`,
  so `.Where(...).VectorSearch(...)` does not compile. The "optional pre-`Where`" the area docs mention is the
  **`preFilter` argument**, not a chained `Where` — and it is rendered *inside* the `$vectorSearch` stage's own
  `filter` option, not as a separate `$match` (verified from the committed baseline for
  `VectorSearch_with_complex_pre_filter`).
- **The gate signal exists as described.** `MongoShapedQueryCompilingExpressionVisitor.ContainsVectorSearch`
  walks `CapturedExpression`'s method chain; the private
  `ClassifyNativeDisposition(MongoQueryExpression, MongoQueryMode)` overload feeds its result into the pure
  4-argument `ClassifyNativeDisposition(route, isFallbackWrongData, containsVectorSearch, mode)`, whose second
  branch is `if (route == NativeRoute.Fallback || containsVectorSearch) return NativeDisposition.Fallback;`.
  A near-duplicate private `ContainsVectorSearch` also exists in
  `MongoQueryableMethodTranslatingExpressionVisitor` (used by the set-op scope predicates
  `IsPlainWholeEntitySelect` / `IsPlainProjectedSelect`); its own comment says it is a deliberate local copy.
- **The disposition is a graceful `Fallback`, never a `HardDecline`.** So under default `Native` and explicit
  `DriverLinq` the query runs correctly on the driver-LINQ path; only `NativeOnly` throws.

### 1.2 What is verified FALSE — the second gate

The `VectorSearch` call **does** reach `NativeSlotPopulator.PopulateNativeSlots`, and is marked non-native
there, independently of `ContainsVectorSearch`.

Mechanism, read from source: `MongoQueryableMethodTranslatingExpressionVisitor.VisitMethodCall` accepts any
method whose `DeclaringType` is in `AllowedQueryableExtensions` — which includes
`typeof(MongoQueryableExtensions)`. `VectorSearch` is not in the `switch`, so it falls straight through to the
unconditional `NativeSlotPopulator.PopulateNativeSlots(shapedQueryExpression, methodDefinition,
methodCallExpression)` call. Its generic method definition is not listed in
`NativeSlotPopulator.IsNativeRepresentableSlotOperator`, so the final catch-all arm fires
`mongoQ.Select.MarkNotNativelyRepresentable()` — driving `Route` to `NativeRoute.Fallback`.

**Measured, by mutation, in a throwaway worktree at `51a94d4f`** (worktree created, measured, and removed; the
repo is clean):

| Mutation | Result for `VectorSearchMongoTest.VectorSearch_floats` under `MONGODB_EF_NATIVE_ONLY=1` |
|---|---|
| none (baseline) | `NativeTranslationNotSupportedException: Query is not natively representable…` |
| `ContainsVectorSearch` branch disabled in `ClassifyNativeDisposition` | **identical** exception, same message, thrown from `TryBuildNativeFactory` → `ThrowIfNativeOnlyForbidsFallback` |
| *both* that branch **and** the `NativeSlotPopulator` catch-all disabled for `MongoQueryableExtensions` methods | **the query goes native and returns SILENTLY WRONG DATA** |

That third row is the important one, and it is the single most useful safety fact in this document. With both
gates off, `VectorSearch_floats` returned **4 books — the right count — in insertion order instead of by
vector score**:

```
Expected: ["Programming Entity Framework: Code First", "Programming Entity Framework",
           "Entity Framework Core in Action", "Programming Entity Framework: DbContext"]
Actual:   ["Entity Framework Core in Action", "Programming Entity Framework: DbContext",
           "Programming Entity Framework", "Programming Entity Framework: Code First"]
```

and `VectorSearch_floats_before_where` returned the right 2 rows in the wrong order. **No exception, correct
row count, wrong answer** — the vector search is simply dropped, because the native lowerer reads only the
logical slots and never the captured chain. (The source comment in `TryBuildNativeFactory` predicts exactly
this: *"it would silently drop the vector search"*. That prediction is now measured.)

### 1.3 What this means for the slice

- **Removing one gate accomplishes nothing** — the other still declines. Any design that says "delete the
  `ContainsVectorSearch` branch" is incomplete.
- **Removing both without emitting a `$vectorSearch` stage produces silent wrong data with a plausible row
  count.** Row count does not discriminate. Any test for this slice must pin the **order/identity of the
  returned rows**, never the count. (This is the same lesson recorded for EF-372/EF-379 in `Query/AGENTS.md`.)
- The status doc's §9.8 gate table row 5 says the gate is *"`ClassifyNativeDisposition`'s
  `ContainsVectorSearch` branch"*. **That is incomplete and should be corrected as part of this slice** — the
  second gate is `NativeSlotPopulator`'s catch-all, and per the "native catch-all whitelist must stay in sync"
  pitfall in `Query/AGENTS.md`, a new native operator must be added to `IsNativeRepresentableSlotOperator`
  *and* given a lowering. Whether `VectorSearch` becomes a slot-style operator or a `Translate*`-override-style
  one is a design question for the spike.

---

## 2. The architectural complication(s)

### 2.1 `$vectorSearch` must be the first stage — evidence, and what I did NOT verify

**UNVERIFIED BY ME:** I did not run a pipeline with a stage before `$vectorSearch` and observe a server error.
The "must be first" rule is MongoDB server documentation, not something I measured. Q1 of the spike should
close this.

**What I did verify:** every committed MQL baseline in
`tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/VectorSearchMongoTest.cs` and
`…/VectorSearchExactMongoTest.cs` places `$vectorSearch` first, with no exceptions, and those baselines are
produced by real runs against a real Atlas-Search-capable server (the whole class passes — §3). Example:

```
Books.{ "$vectorSearch" : { "path" : "Floats", "limit" : 4, "numCandidates" : 40, "index" : "FloatsIndex",
        "queryVector" : [...] } },
      { "$addFields" : { "__score" : { "$meta" : "vectorSearchScore" } } },
      { "$match" : { "is_published" : true } }
```

So: **yes, the driver-LINQ path already emits `$vectorSearch` first** (the answer to that part of the brief),
and it always appends a second stage, `$addFields { __score: { $meta: "vectorSearchScore" } }`, unconditionally
— see the `AddScoreField` static in `MongoEFToLinqTranslatingExpressionVisitor`.

### 2.2 How `MongoSelectLowerer` currently orders stages, and what would have to change

Read `MongoSelectLowerer.Lower` (in
`src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoSelectLowerer.cs`). Its order is fixed and
documented in-place:

1. `AppendSelectOpStages(select.PipelineOps, stages)` — the ordered `$match`/`$sort`/`$skip`/`$limit` op list,
   emitted **verbatim in arrival order** (EF-347 removed the old fixed canonical order; arrival order *is*
   emission order).
2. `AppendLookupStages(query, stages)` — `$lookup` / `$unwind`.
3. Set-operation terminal (`$unionWith` + dedup, or the `Intersect`/`Except` source-tagging pipeline), then
   `select.TrailingOps`.
4. `UnwindSource` terminal — `$unwind`, optional `$match`, then either `$replaceRoot` (returns early) or
   `$project` (returns early).
5. `Grouping` terminal — `$group` + flatten `$project` (returns early).
6. `Projection` — `$project`.
7. `Cardinality` — `$count` / `$group` accumulator / `$limit`.

**There is no "prepend" concept anywhere in this method, and no stage that is emitted before step 1.** A stage
that must precede everything therefore needs a new, explicit slot ahead of `AppendSelectOpStages` — the
structure accommodates that easily (it is the *first* thing `Lower` does, so a new block above it is a
one-line insertion); the ordering problem is genuinely small. **INFERRED, from reading the method: this is a
minor change, and the status doc's framing of stage ordering as the headline risk overstates it.** The real
risk is §2.4.

**What does not exist yet, verified by listing
`src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/Stages/`:** there is **no** `$vectorSearch` stage IR
and **no** `$addFields` stage IR. The 15 stage types are `Count`, `GroupAccumulator`, `Group`, `Limit`,
`Lookup`, `Match`, `Project`, `ReplaceRoot`, `SetDifference`, `Skip`, `Sort`, `UnionWith`, `UnwindField`,
`Unwind`, plus the `MongoPipelineStage` base. `MongoPipelineFactory`'s `RenderStage` switch has an arm per
type and no default fall-through for an unknown stage. Both a stage type and a factory arm would be new.

### 2.3 Coexistence with user-composed stages — VERIFIED for `$match` and `$project`, not for the rest

From the committed, passing baselines:

| Composed shape | Emitted pipeline (after `$vectorSearch` + `$addFields`) |
|---|---|
| `.Where(e => e.IsPublished)` | `$match` |
| `.Where(...).Select(e => e.Author)` | `$match`, `$project { "_v": "$Author", "_id": 0 }` |
| `.Where(...).Select(e => new { e.Author, Score = Mql.Field(e,"__score",…) })` | `$match`, `$project { "Author": "$Author", "Score": "$__score", "_id": 0 }` |
| `preFilter:` argument | folded into `$vectorSearch.filter` — **no separate stage** |
| vector property on an owned sub-document | `$vectorSearch.path = "Preface.Floats"` |

**No test in the repo composes `OrderBy`, `Skip`, `Take`, `First`, `Count`, `Distinct`, `Include` or a
set-operation after a `VectorSearch`** — I grepped `VectorSearchMongoTestBase.cs` for all of them and found
none. So their behaviour on either path is **unverified**, and a native slice that starts recording ops into
`PipelineOps` after a `$vectorSearch` opens shapes nothing currently exercises. Worth a probe in Task 0.

### 2.4 The parameterization / per-execution asymmetry — the constraint I think is actually binding

**VERIFIED.** In `MongoEFToLinqTranslatingExpressionVisitor`'s local `ProcessVectorSearch`, the query vector,
limit and options are read as **EF query parameters**, not constants:

```csharp
#if EF8 || EF9
    TValue? ParamValue<TValue>(int index)
        => (TValue?)_queryContext.ParameterValues[((ParameterExpression)methodCallExpression.Arguments[index]).Name!];
#else
    TValue? ParamValue<TValue>(int index)
        => (TValue?)_queryContext.Parameters[((QueryParameterExpression)methodCallExpression.Arguments[index]).Name];
#endif
```

Even though `MongoQueryableExtensions.VectorSearch` builds the tree with `Expression.Constant(queryVector)` /
`Expression.Constant(limit)` / `Expression.Constant(options, typeof(VectorQueryOptions?))`, EF's own parameter
extraction has converted all three into query parameters by the time the bridge sees them. **Note also that
this is a live `#if EF8 || EF9` in the vector-search path — preserve it; the slice's verification bar is zero
`#if` lines added or removed under `src/` (§4).**

The bridge can do this because **it runs per execution.** Verified from
`MongoShapedQueryCompilingExpressionVisitor.TranslateQuery<TEntity>`: it takes a live `QueryContext`, and on
the fallback path constructs a fresh `MongoEFToLinqTranslatingExpressionVisitor` and re-translates every time.
On the native path the same method does only `nativeFactory.Build(GetParameterValues(queryContext))` — the
template was rendered once at compile time.

Consequences the design has to face:

- The `$vectorSearch` body cannot simply be baked at compile time. It needs `PlaceholderTable` sentinels for
  at least `queryVector` and `limit`, and something for the options.
- **Index resolution happens against runtime values today.** `ProcessVectorSearch` reads
  `concreteOptions.IndexName`; if null it looks up the model's vector indexes for that property and either
  picks the single one, or throws *"the vector index for this query could not be found…"* / *"multiple vector
  indexes are defined for this property…"*; if non-null but unmatched it logs
  `VectorSearchNeedsIndex`. It also throws for `Exact: true` combined with a non-null `NumberOfCandidates`.
- **The zero-results diagnostic is coupled to the fallback path.**
  `MongoShapedQueryCompilingExpressionVisitor.GetOnZeroResultsAction` recognises a `VectorSearch` call on
  `CapturedExpression` and returns an action that reads `MongoExecutableQuery.VectorQueryProperty` and
  `VectorQueryIndexName` out of `AdditionalState` — and **`AdditionalState` is populated by
  `ProcessVectorSearch` in the bridge**. A native path emits `NativePipeline` with an empty `AdditionalState`
  (see `TranslateQuery`'s native branch, which constructs `new(new Dictionary<string, object>())`), so
  `VectorSearchReturnedZeroResults` would throw a `KeyNotFoundException` or silently stop firing unless the
  slice re-plumbs it. Two spec tests depend on this warning (`VectorSearch_logs_for_zero_results`, ×2 classes
  ×2 async = 4 cases).

---

## 3. Size of the prize — RE-MEASURED at `51a94d4f`, not restated

The status doc §9.1 claims "106 of the 112 are inside the 1742". **I re-measured it and the number reproduces
exactly.** The doc is not stale on this point.

**How measured.** Both `MONGODB_URI` and `ATLAS_URI` left unset, so TestContainers booted a real
`mongodb/mongodb-atlas-local` container and the Atlas-gated tests **ran for real** (a container was created
and torn down; `docker ps` before and after confirms it is gone). Command, run twice, `Debug EF10`, no
`tail`/`head` piping:

```bash
dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~VectorSearch" \
  --logger "trx;LogFileName=vs-native.trx" --results-directory <dir>
# and again with MONGODB_EF_NATIVE_ONLY=1
```

**Results:**

| Mode | Failed | Passed | Skipped | Total |
|---|---:|---:|---:|---:|
| default `Native` | **0** | 114 | 4 | 118 |
| `MONGODB_EF_NATIVE_ONLY=1` | **112** | 2 | 4 | 118 |

- The 4 skipped are `VectorSearch_with_bool_pre_filter_on_nested_reference` and
  `VectorSearch_with_complex_pre_filter_on_nested_reference` (×2 classes), skipped in the source with
  `[ConditionalTheory(Skip = "Pre-filter on nested reference returns wrong results; pending C# driver 3.9.0 fix")]`.
  **They are skipped in both modes and are not part of the prize.**
- The 2 passing under `NativeOnly` are `Check_all_tests_overridden` (×2 classes) — a `[ConditionalFact]` that
  runs no query.

**Bucketed by failure message, from the `NativeOnly` `.trx`:**

| Count | Message |
|---:|---|
| 82 | `NativeTranslationNotSupportedException : Query is not natively representable…` |
| 24 | `NativeTranslationNotSupportedException : Query projects a non-entity result…` |
| 6 | `Assert.Throws() Failure: Exception type was not an exact match` |
| **112** | **total** |

This is byte-for-byte the table in status-doc §9.1. **106 (82 + 24) are genuine data-bucket failures — the
count stands; do not re-derive it.**

**One nuance worth carrying, which the "6 are exception-shape bookkeeping" framing loses.** The 6 are:
`VectorSearch_logs_for_zero_results` (×2 classes ×2 async = 4) and
`VectorSearch_throws_if_num_candidates_set_for_exact_search` (×1 class ×2 async = 2). All six fail because
the expected `InvalidOperationException` is pre-empted by `NativeTranslationNotSupportedException` at the
gate. The zero-results four are **not** pure bookkeeping — they exercise the diagnostic plumbing described in
§2.4, which a native path would have to reproduce. Budget them as real work, not as re-baselines.

**Functional-test surface: there is none for vector *search*.** I inventoried every test file mentioning
`BinaryVector|VectorIndex|VectorSearch|VectorSimilarity`; the only files exercising the `VectorSearch(...)`
query API are the three spec files. The functional suite covers vector *indexes*
(`FunctionalTests/Storage/IndexTests.cs`), binary-vector *mapping*
(`FunctionalTests/Mapping/ClrTypeMappingTests.cs`, `…/PropertyBuilderExtensionTests.cs`) and the *logging*
definitions (`FunctionalTests/LoggingTests.cs`). **So the spec suite is the entire behavioural oracle for this
feature, and this slice will probably need to add a functional `Native*` test file of its own** — every other
slice on this branch has one (e.g. `NativeReferenceIncludeTests`, `Ef379RootNavigationMisroutingTests`).

---

## 4. Testing constraints and how to get a working environment

Verified against `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Utilities/TestServer.cs`,
`…/TestContainersTestServer.cs`, `…/MongoCondition.cs`,
`tests/MongoDB.EntityFrameworkCore.SpecificationTests/Utilities/MongoConditionAttribute.cs`, and the root
`AGENTS.md` "Testing" section.

- **Leave both `MONGODB_URI` and `ATLAS_URI` unset.** That is not just the recommendation, it is what makes
  these tests run at all. Docker is required.
- `TestServer.GetOrInitializeTestServerAsync(MongoCondition.IsAtlas)` resolves the Atlas server from
  `ATLAS_URI`; when that variable is unset it constructs a `TestContainersTestServer`, which boots
  **`mongodb/mongodb-atlas-local`** via `MongoDbAtlasBuilder` (with a 5-minute bounded start so a stuck
  readiness probe fails fast). **This container really does run Atlas Vector Search — verified: with both
  variables unset, all 114 non-skipped vector tests passed against it.** The image is ~2 GB; it was already
  cached locally on this machine.
- `ATLAS_URI="Disabled"` makes `TestServer.SupportsAtlas` return `false`;
  `MongoConditionAttribute.IsMetAsync` then reports the condition unmet and **every** `[MongoCondition(
  MongoCondition.IsAtlas)]` class — which is both `VectorSearchMongoTest` and `VectorSearchExactMongoTest` —
  is skipped wholesale. If you see 118 skips, that is why.
- Pointing `ATLAS_URI` at a plain `mongod` / replica set is worse than useless here: the tests will *run* and
  fail, because there is no Atlas Search.
- Each `dotnet test` **process** gets its own container on a random host port (`TestServer` caches per-process
  via double-checked locking), so parallel agents do not collide.

**How vector indexes are created and waited on — file by file:**

- Definition: `VectorSearchMongoTestBase.VectorSearchFixtureBase.OnModelCreating` declares them with
  `b.HasIndex(e => e.Floats, "FloatsIndex").IsVectorIndex(VectorSimilarity.Cosine, 2)` (and an owned-entity
  one on `Preface.Floats` with `AllowsFiltersOn(...)`).
- Trigger: `tests/MongoDB.EntityFrameworkCore.SpecificationTests/Utilities/MongoTestStore.InitializeAsync`
  calls `databaseCreator.EnsureCreatedAsync()`. `MongoDatabaseCreationOptions` (file
  `src/MongoDB.EntityFrameworkCore/Metadata/MongoDatabaseCreationOptions.cs`) defaults
  `CreateMissingVectorIndexes: true`, `WaitForVectorIndexes: true`, `IndexCreationTimeout: null` (documented
  as 60 s default; zero means no timeout), so index creation **and** the wait happen automatically.
- Implementation: `src/MongoDB.EntityFrameworkCore/Storage/MongoDatabaseCreator.cs` —
  `CreateMissingVectorIndexes()` → `CreateMissingAtlasIndexes(forVectors: true)` (lists existing search
  indexes per collection, builds `CreateSearchIndexModel`s, `SearchIndexes.CreateMany`), and
  `WaitForVectorIndexes(TimeSpan?)` → `WaitForAtlasIndexes(..., forVectors: true)`, a polling loop over
  `SearchIndexes.List()` that throws on `status == "FAILED"`, logs `WaitingForVectorIndex(remaining)` while
  not `READY`, and honours the timeout. Both have `…Async` twins in the same file.
- Public surface: `MongoDatabaseFacadeExtensions.CreateMissingVectorIndexes` /
  `…Async` / `WaitForVectorIndexes` / `…Async` (`src/MongoDB.EntityFrameworkCore/Extensions/`).
- Direct index tests live in `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Storage/IndexTests.cs`, which
  drives the create/wait pair explicitly with
  `new MongoDatabaseCreationOptions(CreateMissingVectorIndexes: false, WaitForVectorIndexes: false)`.

`CRYPT_SHARED_LIB_PATH` is irrelevant to this slice (encryption only).

---

## 5. What already exists — the inventory you are building ON

**Query (the part this slice changes):**

| File | Role |
|---|---|
| `Extensions/MongoQueryableExtensions.cs` | the two public `VectorSearch<TSource,TProperty>` overloads (root-of-`DbSet`, with/without `preFilter`), the private one that builds the tree, and `internal static bool IsVectorSearch(this MethodCallExpression)` |
| `Query/MongoQueryTranslationPreprocessor.cs` | `VectorSearchExtractor` / `VectorSearchReplacer` — the lift-before-nav-expansion / re-insert-after dance |
| `Query/Visitors/MongoEFToLinqTranslatingExpressionVisitor.cs` | `ProcessVectorSearch` — the entire current implementation: option validation, index resolution, `VectorSearchOptions<T>` construction by reflection, `PipelineStageDefinitionBuilder.VectorSearch`, `MongoQueryable.AppendStage`, and the `AddScoreField` `$addFields` static |
| `Query/Visitors/MongoShapedQueryCompilingExpressionVisitor.cs` | `ContainsVectorSearch`, `ClassifyNativeDisposition` (both overloads), `TryBuildNativeFactory`'s vector-search comment, `GetOnZeroResultsAction` |
| `Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs` | `AllowedQueryableExtensions` (admits `MongoQueryableExtensions`), a local duplicate `ContainsVectorSearch` used by `IsPlainWholeEntitySelect`/`IsPlainProjectedSelect` |
| `Query/MongoExecutableQuery.cs` | `VectorQueryProperty` / `VectorQueryIndexName` `AdditionalState` keys |
| `Query/NativeTranslation/NativeSlotPopulator.cs` | the catch-all + `IsNativeRepresentableSlotOperator` whitelist — **the second gate (§1.2)** |

**Metadata / Storage / Diagnostics (the part this slice should NOT need to change — INFERRED):**

- `Metadata/VectorQueryOptions.cs` — `readonly record struct VectorQueryOptions(string? IndexName = null,
  int? NumberOfCandidates = null, bool Exact = false)`.
- `Metadata/VectorIndexOptions.cs`, `Metadata/VectorIndexBuilder.cs`, ``Metadata/VectorIndexBuilder`.cs``,
  `Extensions/MongoIndexBuilderExtensions.IsVectorIndex(...)`, `Extensions/MongoIndexExtensions`,
  `Metadata/InternalIndexExtensions` (→ `CreateSearchIndexModel`, `SearchIndexType.VectorSearch`),
  `Metadata/Conventions/IndexNamingConvention`.
- Binary vectors: `Metadata/Conventions/BsonAttributes/BinaryVectorAttributeConvention.cs`,
  `Extensions/MongoPropertyBuilderExtensions.HasBinaryVectorDataType`, `Extensions/MongoPropertyExtensions`,
  `Serializers/BsonSerializerFactory`, `Storage/MongoTypeMappingSource`. The annotation keys are
  `Mongo:VectorIndexOptions` and `Mongo:BinaryVectorDataType` (`Metadata/MongoAnnotationNames.cs`).
- Storage: `Storage/MongoDatabaseCreator.cs` + `Storage/IMongoDatabaseCreator.cs` (create/wait, sync + async).
- Diagnostics: `MongoEventId.VectorSearchNeedsIndex`, `MongoEventId.VectorSearchReturnedZeroResults`, and
  their `MongoLoggerExtensions` / `MongoLoggingDefinitions` entries. `MongoDbContextOptionsExtensions` promotes
  `VectorSearchNeedsIndex` to `WarningBehavior.Throw` by default.

**Tests:**

- `tests/…/SpecificationTests/Query/VectorSearchMongoTestBase.cs` (1013 lines — the shared body, the `Book`
  and `Preface` model, the `VectorSearchFixtureBase`, and the seed).
- `tests/…/SpecificationTests/Query/VectorSearchMongoTest.cs` (ANN — emits `numCandidates`) and
  `…/VectorSearchExactMongoTest.cs` (ENN — emits `"exact": true`). Both are
  `[MongoCondition(MongoCondition.IsAtlas)]` and both override every base method to `AssertMql(...)`.
- `tests/…/UnitTests/Query/NativeTranslation/NativeDispositionTests.cs` — **the unit tests this slice will
  have to change.** Two are directly about vector search:
  `Vector_search_is_fallback_even_when_route_is_native` and `Hard_decline_takes_precedence_over_vector_search`.
- `tests/…/UnitTests/Metadata/VectorIndexOptionsTests.cs`, `…/UnitTests/Infrastructure/MongoModelValidatorTests.cs`.

---

## 6. Scope boundaries — what the docs say, and what they are SILENT on

**What the docs actually commit to.** Status doc §9.8 step 2 and §9.1 both describe the goal in one sentence:
*"Making it native means getting the lifted call back down to the lowerer as a `$vectorSearch` stage."* The
§9.8 gate table row 5 says the same. **Neither §9.8, §9.1, §4 nor §5 says anything about pre-filtering, score
projection, `exact`/`numCandidates`, quantization or binary vectors as slice scope.** The 106-test figure is
the only sizing signal, and — per §3 — those 106 are dominated by *composed* shapes, so a slice that only
handles the bare call would move very few of them. **[FALSE — corrected in the §0.3 box: 70 of the 82 are the
bare call, and a bare-call slice moves most of the prize. The conclusion this sentence was used to support
(take more than the bare call) still happens to be right, but for the `preFilter`/`exact` reasons below, not
this one.]**

**IN scope, on the plain reading of the docs plus the measurement:**

- Emit `$vectorSearch` as the first native pipeline stage (new stage IR + `MongoPipelineFactory` arm + a slot
  ahead of `AppendSelectOpStages` in the lowerer).
- Emit the `$addFields { __score }` companion stage — it is unconditional on the existing path, and dropping
  it would change results for every `__score` test.
- The two gates in §1, both of them.
- ~~`.Where(...)` composed after the search — that is where the 82-test bucket lives.~~ **Wrong on both counts**
  (see the §0.3 box): the 82 are overwhelmingly bare calls, and `.Where` after a `VectorSearch` is already
  handled by the native translator, so it needs no work of its own in this slice.
- **`exact` / `numCandidates` are not optional extras.** `VectorSearchExactMongoTest` is *half the test class
  count* (56 of 112) and differs from `VectorSearchMongoTest` only in emitting `"exact": true` instead of
  `"numCandidates"`. Any slice that ignores `Exact` leaves 56 tests behind.
- **The pre-filter is likewise not optional.** It is an *argument*, rendered inside the stage; you cannot emit
  a correct `$vectorSearch` body for those tests without handling it.

**OUT of scope, with reasons:**

- **Binary vectors and quantization.** These are a *serialization* concern (`BinaryVectorDataType` selects the
  wire encoding at the property serializer) and a *stored-data* concern. The 12 `VectorSearch_binary_*` tests
  differ from their non-binary siblings only in which property is searched; nothing in the query pipeline is
  binary-specific. **INFERRED** from the inventory in §5 and from those tests' MQL baselines being structurally
  identical to the non-binary ones.
- **Vector index creation / waiting** (`Storage/MongoDatabaseCreator`) — untouched by query translation.
- The other cutover steps: projection long tail (step 3), bulk-path bridge (step 4), re-baselining and
  public-API decisions (step 5).
- **EF-380 and EF-381** — the two residuals left by the EF-379 slice (a reachable `Unclassifiable`
  fall-through, and reinstating a withdrawn decline with a better discriminator). **They are join work, not
  VectorSearch work.** Named here only so you do not mistake them for part of this slice.

**Genuinely silent in the docs — take these to the owner (§8), do not decide them yourself:**

- Whether the **bare-scalar `Select`** shapes in the 24-test bucket are in scope, given that they are the
  SP3-wide bare-projection boundary (status-doc §9.8 step 3) rather than anything vector-specific.
- Whether **`__score`** is in scope. It is a synthetic, unmapped field read two ways
  (`Mql.Field` and `EF.Property<double>`), and `Mql.Field` is a *driver* API — a native path would have to
  give it a different meaning or decline it.
- Whether the **zero-results / needs-index diagnostics** must keep working on the native path in this slice,
  or may temporarily decline.

---

## 7. Settled process and verification bar — carried from the owner, not re-litigable

*These come from the owner directly. They are recorded here so the next agent does not re-derive or re-open
them.*

**Process.**

- This project uses **subagent-driven development, a fresh subagent per task**.
- The default is to **STOP after every task for the owner's review.** That gate was dropped part-way through
  the EF-379 slice, but the revocation was **per-branch and per-slice**: the **VectorSearch slice starts with
  the gate back ON** unless the owner says otherwise.
- Slices ship as **ONE squashed commit** onto `NativeQueryOngoing`, with a `<TICKET>-presquash` backup branch
  kept until PR #324 merges. **Never force-push `NativeQueryOngoing`.**
- Commit/PR titles start with the JIRA number: `EF-1234: Description`.

**Verification bar (as applied to EF-379; expected here).**

- Full solution green on **EF8, EF9 and EF10**.
- The EF10 **spec suite compared under `MONGODB_EF_NATIVE_ONLY=1`** against a base worktree, **by
  failing-test-name SET, not by count**.
- **Zero `#if` lines added or removed under `src/`** (`git diff <base> HEAD -- src/`). Note §2.4: there is an
  existing `#if EF8 || EF9` in `ProcessVectorSearch`'s `ParamValue` helper — preserve it.
- Every guard test **mutation-verified**: a test that stays green when you break the code it guards is
  worthless. (§1.2 shows what a useful mutation looks like.)
- **Never pipe `dotnet test` through `tail`/`head`** — it masks the exit code and truncates per-project
  summaries. Redirect to a file and read it.

**Settled, do not re-raise.**

- The native translator becoming the **default** execution path is **not** a breaking change. The rubric is in
  the root `AGENTS.md`; results are unchanged, unsupported shapes fall back automatically, and
  `UseQueryMode(MongoQueryMode.DriverLinq)` restores the previous path.
- Breaks are measured against the **latest RELEASED tag**, never `upstream/main` and never the development
  branch. A measurement on `NativeQueryOngoing` does not establish reachability on a released package. The
  worked cautionary example is
  `docs/superpowers/specs/2026-07-31-groupby-join-uncorrelated-inner-decline-design.md` §2.7.
  **I verified the baseline for you:** latest overall is **`v10.0.2`**; latest per line are `v10.0.2`,
  `v9.1.2`, `v8.4.2`. At all three, `MongoQueryableExtensions.VectorSearch` (both public overloads) and
  `VectorQueryOptions(IndexName, NumberOfCandidates, Exact)` exist **unchanged from HEAD**, and
  `src/MongoDB.EntityFrameworkCore/Infrastructure/MongoQueryMode.cs` **does not exist at any of them**
  (`git ls-tree` verified). So every mode-dependent statement in this document is vacuous at the published
  baseline: on a released package, vector search only ever runs on the driver-LINQ path. **INFERRED
  consequence:** as long as *results* are unchanged, making vector search native cannot be observable to an
  upgrading consumer, and no `BREAKING-CHANGES.md` entry is warranted — but verify per member against the tag
  before concluding that, don't take it from here.
- **EF-317 is throwaway.** Build what the slice needs; do not design around it.

**The EF-379 trap, and whether it transplants here — it DOES, with a twist.**

The trap is: *"MQL shape cannot prove a query went native"* — only a `NativeOnly` run distinguishes native
from a fallback that emits identical MQL. It is recorded in `Query/AGENTS.md`'s "Common pitfalls".

**It applies to vector search, and the twist is that it does not apply *yet*.** Today, `$vectorSearch` in the
captured MQL is in fact a reliable *fallback* signal, because only `ProcessVectorSearch` can emit it —
verified by §3's two runs (every vector test passes under `Native` and throws under `NativeOnly`, so all 114
`Native` passes are fallback executions). **The moment this slice emits `$vectorSearch` natively, that
inference dies**: both paths will emit a structurally identical stage, and an `AssertMql` baseline containing
`$vectorSearch` will prove nothing about which path ran. Plan for it now — every "goes native" assertion in
this slice must be a `MongoQueryMode.NativeOnly` run that *succeeds*, not an MQL match. This is exactly what
happened to reference `Include` after EF-370 (documented in `Query/AGENTS.md`'s reference-Include note), so
there is precedent to follow rather than invent.

---

## 8. Open questions for the owner — ANSWERED 2026-08-06

*All six are now ruled on by the owner. These are decisions, not suggestions: treat them the way §7's settled
items are treated, and do not re-open them without the owner saying so.*

1. **Slice scope: bare call only, or composed shapes too?** → **Bare call + the `Where` bucket** *(as the
   option was worded to the owner)*. The slice emits `$vectorSearch` (with `preFilter` folded into the stage
   body) and the `$addFields { __score }` companion natively, handles **both** `numCandidates` and `exact` (per
   §6, `Exact` alone is 56 of the 112). The 24-test bare-scalar `Select` bucket is **not** in this slice
   (see 3).

   **The rationale offered with this option was wrong; the ruling survives it.** The option was sold as
   "targets the 82-test `Where` bucket". Per the §0.3 correction box there is no such bucket — the 82 are 70
   bare calls, 8 `preFilter`, 4 `.Where` — and `.Where` needs no work at all. What the ruling actually buys is
   the bare call plus `preFilter` plus `exact`/`numCandidates`, which the spike sizes at **88 of 112** (96 if
   the 8 `{ member, score }` cases are added — see the question in 2/3 below). So the chosen scope is *better*
   value than it was pitched as, and the boundary it draws is unchanged.
2. **Is `__score` in scope?** → **Yes, in scope.** Emit the `$addFields` companion and give `__score` a native
   read; `MongoElementRefExpression` (raw path, no `IProperty`) is the lead to try first. `Mql.Field` is a
   *driver* API and will need either a native meaning or a targeted decline — the spike should say which.
3. **Are the bare-scalar `Select` shapes in scope?** → **No — deferred to step 3** (the projection long tail).
   They are the SP3-wide bare-projection boundary, not a vector-search problem. Expect the 24-test bucket to
   survive this slice.
4. **Must the two diagnostics keep working on the native path?** → **Yes, both must keep working.** Index
   resolution and the zero-results plumbing move to `Build(parameterValues)` time so that behaviour under the
   default mode is unchanged and the four `VectorSearch_logs_for_zero_results` cases pass natively. Budget this
   as real work, not bookkeeping.
5. **Should the status doc's gate table (§9.8 row 5) be corrected?** → **Already done**, ahead of the slice, in
   commit `d54b86f7`. `docs/native-query-status-EF-322.md` now names both gates in two places. No action left.
6. **Does the "stop after every task" gate stay ON?** → **ON for the whole slice.** Spike → owner review →
   design → owner review → implementation, stopping after every task. Do not assume the EF-379 mid-slice
   revocation carries over.

---

## Appendix — reproduction commands used for this note

```bash
# build once
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"

# the two measurement runs (both MONGODB_URI and ATLAS_URI unset)
dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~VectorSearch" \
  --logger "trx;LogFileName=vs-native.trx" --results-directory <dir>

MONGODB_EF_NATIVE_ONLY=1 dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~VectorSearch" \
  --logger "trx;LogFileName=vs-nativeonly.trx" --results-directory <dir>

# the §1.2 mutations were run in a throwaway worktree, since removed:
#   git worktree add <scratch>/wt 51a94d4f
#   (1) MongoShapedQueryCompilingExpressionVisitor.ClassifyNativeDisposition:
#         `route == NativeRoute.Fallback || containsVectorSearch`
#       -> `route == NativeRoute.Fallback || (containsVectorSearch && false)`
#   (2) NativeSlotPopulator.PopulateNativeSlots final arm:
#         `else if (!IsNativeRepresentableSlotOperator(methodDefinition))`
#       -> `... && methodDefinition.DeclaringType?.Name != "MongoQueryableExtensions")`
#   git worktree remove --force <scratch>/wt

# release-tag baseline checks
gh release list --limit 100 --json tagName,isLatest
git show v10.0.2:src/MongoDB.EntityFrameworkCore/Extensions/MongoQueryableExtensions.cs
git ls-tree v10.0.2 src/MongoDB.EntityFrameworkCore/Infrastructure/MongoQueryMode.cs   # empty => absent
```
