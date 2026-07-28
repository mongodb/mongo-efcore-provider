# EF-322 `VectorSearch` slice — Task 0 spike findings

*Run 2026-08-06 against `NativeQueryOngoing` at `8e5ae4a5` (clean). Input: the handoff
`docs/superpowers/specs/2026-08-06-vectorsearch-slice-handoff.md`, whose §8 owner rulings scope this spike.*

**Tagging convention, applied strictly.** Every claim below is one of:
**MEASURED** (I ran it, this session, and the output is quoted or reproducible from §7's commands) ·
**READ** (established by reading source at `8e5ae4a5`; no execution) ·
**INFERRED** (a conclusion drawn from MEASURED/READ facts, but not itself observed) ·
**UNVERIFIED** (I did not establish it — stated so the next agent does not mistake it for settled) ·
**CITED** (measured by the handoff's author, not re-run by me).

**Trap-2 compliance, stated up front.** The handoff warns that once this slice emits `$vectorSearch` natively,
MQL shape stops proving a query went native. **No claim in this document rests on MQL shape as a routing
signal.** My probes are server-side (raw driver against a live atlas-local container) and renderer-level
(`MongoPipelineFactory` / `MongoAggregationExpressionRenderer` driven directly); none of them ran an EF query
through the gate at all. The one routing measurement I made is a `MONGODB_EF_NATIVE_ONLY=1` spec run, which is
the sanctioned signal.

---

## Headline — four findings, in order of how much they change the slice

1. **Q3 is where the surprise is, and it is a correction to the handoff, not a refinement.
   The 82-test bucket is NOT a `.Where(...)` bucket.** MEASURED, by attributing every failing case in the
   `NativeOnly` `.trx` to its test method: the 82 are **70 bare `VectorSearch(...)` whole-entity cases, 8
   `preFilter` cases, and 4 `.Where`-composed cases.** Exactly one method in the whole suite
   (`VectorSearch_floats_before_where`) composes a `Where`. The handoff's §0.3 Q3 ("`.Where(pred)` after
   `VectorSearch` — the 82-test bucket") and §3's framing ("106 of the 112 failures are *composed* shapes, not
   the bare `VectorSearch(...)` call") are both wrong on this point. **The bare call is the prize.**
2. **The `PipelineStageDefinitionBuilder.VectorSearch(...)`-at-`Build`-time approach WORKS — MEASURED, and it
   is not merely the cheapest option, it is close to forced.** A driver `PipelineStageDefinition<T,T>` renders
   to a plain `BsonDocument` via `Render(new RenderArgs<T>(serializer, registry))`, correct for ANN, ENN,
   `preFilter`, and — decisively — for **binary** query vectors, which are BSON binary subtype `09` and which
   the hand-render path **cannot** produce (`BsonValue.Create(QueryVector)` and
   `BsonValue.Create(BinaryVectorFloat32)` both throw, MEASURED). 36 of the 82 cases are binary.
3. **`PlaceholderTable` substitution inside a `$vectorSearch` body also works** (MEASURED, including a whole
   array-valued `queryVector` and a scalar `limit`, rebuilt twice from one template with different values) —
   **but it is not sufficient on its own**, because the *shape* of the stage body (whether `exact` or
   `numCandidates` is present at all, and which index name is used) depends on a **runtime** `VectorQueryOptions`
   value, and value substitution cannot express structural variance. So the slice needs a **`Build`-time stage
   *constructor*, not a `Build`-time value *substitution*.** That is one genuinely new mechanism in
   `MongoPipelineFactory`, and it is the slice's main architectural cost.
4. **Ruling 2 (`__score` in scope) currently has no in-scope test that would prove it.** MEASURED: every test
   that reads `__score` is in the 24-case "projects a non-entity result" bucket, which ruling 3 puts out of
   scope. Emitting the `$addFields` companion is needed for MQL-baseline parity; the *read* half pays off only
   if the owner also moves the 8 `new { e.Author, Score = … }` cases in scope. **That is the one question this
   spike surfaces that only the owner can answer** (§8).

---

## Q1 — the server constraint, MEASURED

Ran against a purpose-booted `mongodb/mongodb-atlas-local:latest` container (host port 63039), collection
`vsspike.books`, 4 documents, a real `vectorSearch` search index `FloatsIndex` (2-dim cosine on `Floats`, plus
a `filter` field on `is_published`), polled to `status: "READY"`. Driven through `mongosh`, i.e. no EF and no
C# driver in the path. Full script and output: §7.

### Q1a — a stage before `$vectorSearch`: rejected, uniformly

| Pipeline | Result |
|---|---|
| `$match` → `$vectorSearch` | **FAIL** |
| `$limit` → `$vectorSearch` | **FAIL** |
| `$sort` → `$vectorSearch` | **FAIL** |
| `$addFields` → `$vectorSearch` | **FAIL** |
| `$project` → `$vectorSearch` | **FAIL** |
| `$vectorSearch` → `$vectorSearch` | **FAIL** |

**Exact server error, identical in all six cases** (`codeName` / `code` / message):

```
Location40602 (40602) : $vectorSearch is only valid as the first stage in a pipeline
```

So the handoff's §2.1 "UNVERIFIED BY ME" is now closed: the rule is real, it is enforced by the server, and it
is a hard command failure, not a silent degradation.

### Q1b/Q1c — composition *after* `$vectorSearch`: everything the repo does not currently test also works

The handoff's §2.3 correctly notes that **no test in the repo composes `OrderBy`, `Skip`, `Take`, `First`,
`Count`, `Distinct`, `Include` or a set-op after a `VectorSearch`** (I re-checked this by grep — READ, agrees).
I probed the server side of all of them:

| Composed after `$vectorSearch` + `$addFields` | Result |
|---|---|
| `$match` | OK (3 of 4 rows) |
| `$sort` | OK |
| `$skip` | OK |
| `$limit` | OK |
| `$sort` + `$skip` + `$limit` | OK |
| `$count` | OK |
| `$group{_id:"$$ROOT"}` + `$replaceRoot` (the `Union`/`Distinct` dedup shape) | OK — **but reorders rows** |
| `$unionWith` | OK |
| `$lookup` | OK |
| `$project` | OK |
| `$unwind` | OK |

**MEASURED, and worth carrying into the design:** the dedup `$group{_id:"$$ROOT"}` returned
`D, C, A, B` where score order is `A, B, C, D` — i.e. **any native shape that dedups by whole document destroys
vector-score ordering**, and with `$addFields{__score}` present the score is itself part of `$$ROOT` and so part
of the dedup key. That is a concrete reason to keep `Distinct`/`Union` composed after a `VectorSearch` out of
this slice (see Q4's answer on the QMTEV duplicate).

### Q1d — sub-pipelines

| Shape | Result |
|---|---|
| `$vectorSearch` inside a **`$unionWith`** sub-pipeline | **OK** (allowed) |
| `$vectorSearch` inside a **`$lookup`** sub-pipeline | **FAIL** — `Location51047 (51047) : $vectorSearch is not allowed within a $lookup's sub-pipeline` |

INFERRED consequence: "first stage in *a* pipeline" is scoped per-pipeline, not per-command — a `$unionWith`
operand's own pipeline counts. Not needed by this slice, but it bounds any future set-op work.

### Q1e — option validation, and where it actually fires

| Shape | Result |
|---|---|
| `exact: true` alone | OK |
| `exact: true` **+** `numCandidates` | **FAIL** — `UnknownError (8) : … "numCandidates" must be omitted when "exact" is set to true` |
| `filter: {…}` (the `preFilter`) | OK, filters correctly |
| **a non-existent index name** | **OK, 0 rows, no error** |

Two things follow, both MEASURED:

- The `exact`+`numCandidates` combination is rejected in **three** places, and the provider's own guard is the
  outermost: the provider throws `InvalidOperationException` at translate time; **the C# driver itself throws
  `ArgumentException("Number of candidates must be omitted for exact nearest neighbor search (ENN).")`** the
  moment `PipelineStageDefinitionBuilder.VectorSearch(...)` is called with both set; and the server rejects it
  as above. This matters for `VectorSearch_throws_if_num_candidates_set_for_exact_search` (2 of the 6
  `Assert.Throws` cases): if the native path builds the stage through the driver, the provider's own guard must
  stay **ahead** of the driver call or the exception type changes.
- **A wrong index name is silent** — zero rows, no error. That is exactly why `VectorSearchNeedsIndex` and
  `VectorSearchReturnedZeroResults` exist, and it is why ruling 4 (both diagnostics must keep working) is not
  bookkeeping.

---

## Q2 — parameterization / per-execution asymmetry

### Q2.1 — the cheapest option, checked first: is `PipelineStageDefinitionBuilder.VectorSearch(...)` usable at `Build` time?

**YES. MEASURED.** Using exactly the reflection shape `ProcessVectorSearch` already uses, then rendering with
`stage.Render(new RenderArgs<T>(documentSerializer, BsonSerializer.SerializerRegistry))` and reading
`.Document`:

| Input | Rendered `BsonDocument` | Executed against the live index |
|---|---|---|
| ANN, `NumberOfCandidates = 40` | `{"$vectorSearch":{"path":"Floats","limit":4,"numCandidates":40,"index":"FloatsIndex","queryVector":[1.0,0.0]}}` | OK, 4 rows, **score order** `A(1.0000), B(0.9969), C(0.5000), D(0.0000)` |
| ENN, `Exact = true` | `{"$vectorSearch":{…,"exact":true,…}}` | OK, same score order |
| ANN + `Filter = ExpressionFilterDefinition<T>(b => b.IsPublished)` | `{…,"filter":{"is_published":true},…}` | OK, 3 rows, score order |
| ANN + `Filter = BsonDocumentFilterDefinition<T>(new BsonDocument("is_published", true))` | `{…,"filter":{"is_published":true},…}` | OK, 3 rows, score order |
| `Exact = true` **and** `NumberOfCandidates = 40` | — | driver throws `ArgumentException` (see Q1e) |
| a **binary** `QueryVector` (`BinaryVectorFloat32`) | `{…,"queryVector":{"$binary":{"base64":"JwAAAIA/AAAAAA==","subType":"09"}}}` | (render only) |

Three things this buys, each MEASURED:

- **The rendered document is byte-shaped exactly like the committed baselines** (`path, limit, numCandidates,
  index, filter, queryVector`, in that order) — INFERRED consequence: **reusing the driver builder means the
  70+ committed `AssertMql` baselines for the bare shapes need no re-baselining at all.** That is a large,
  otherwise-invisible cost avoided.
- **Binary vectors come for free.** `queryVector` for a binary vector is BSON binary subtype `09`, and the only
  thing that knows how to produce it is the driver's own `QueryVector` serialization. **A hand-written renderer
  cannot**: `BsonValue.Create(QueryVector)` throws
  `ArgumentException: .NET type MongoDB.Driver.QueryVector cannot be mapped to a BsonValue`, and
  `BsonValue.Create(BinaryVectorFloat32)` throws the same (both MEASURED). **36 of the 82 in-scope cases are
  binary**, so this is not an edge.
- **`BsonDocumentFilterDefinition<T>` works**, so the `preFilter` does **not** have to go through the
  driver-LINQ bridge visitor. The native `MongoQueryLanguageRenderer` already produces a `$match`-dialect
  `BsonDocument`; handing that straight to `VectorSearchOptions<T>.Filter` renders identically. That removes
  the one dependency on `MongoEFToLinqTranslatingExpressionVisitor` a native vector path would otherwise have.
  **UNVERIFIED:** whether the *native* renderer's output for the repo's actual `complex_pre_filter` shape is
  byte-identical to the bridge's — my probe used a raw `BsonDocument` I wrote, not one the native renderer
  produced from that test's predicate.

**UNVERIFIED, and a design task should close it:** I rendered with the driver's default class-map serializer
for a throwaway POCO, not with the provider's own `bsonSerializerFactory.GetEntitySerializer(entityType)`.
INFERRED that it is interchangeable — `RenderArgs<T>` takes an `IBsonSerializer<T>` and the provider's entity
serializer is one — and `TranslateQuery` already has the factory in hand. Low risk, but not measured.

### Q2.2 — can a `PlaceholderTable` sentinel stand in for an array-valued `queryVector`, a scalar `limit`, an option object?

**Array and scalar: YES, MEASURED. Option object: NO — and the reason is structural, not a gap.**

I added a throwaway `MongoVectorSearchStage` + one `MongoPipelineFactory.RenderStage` arm in a scratch
worktree, rendering the body with `MongoValueRenderer.RenderValue` for `queryVector` and `limit`, then called
`Create` once and `Build` several times:

```
B1 built: [{ "$vectorSearch" : { "index":"FloatsIndex","path":"Floats","queryVector":[1.0,0.0],"limit":4,"numCandidates":40 } }]
   executed -> A(1.0000), B(0.9969), C(0.5000), D(0.0000)
B2 rebuilt (same factory, different values): queryVector [0.0,1.0], limit 2
   executed -> C(1.0000), B(0.5552)
B3 exact:true + baked filter -> A(1.0000), C(0.5000), D(0.0000)
```

- **A whole array substitutes correctly.** `MongoPipelineFactory.SubstituteValue` deep-walks every
  `BsonDocument`/`BsonArray` position and tests for the sentinel *before* recursing (READ, and MEASURED to work
  here), so a sentinel is position-agnostic — nothing about `$match` is special. It worked with the runtime
  value supplied both as a `BsonArray` and as a raw `float[]` (`BsonValue.Create` handles both).
- **`MongoValueRenderer`'s `serializer: null` path (`BsonValue.Create`) is the limiting factor.** It handles
  `float[]`/`double[]`/`int`. It **throws** for `QueryVector` and for `BinaryVectorFloat32` — and the value EF
  actually puts in `parameterValues` is a `QueryVector` (READ: `MongoQueryableExtensions.VectorSearch` takes
  `QueryVector` and does `Expression.Constant(queryVector)`). So this route needs either an unwrap step or a
  bespoke serializer, and for binary vectors a bespoke serializer that reproduces subtype `09`.
- **`MongoPipelineFactory.Build`'s `ValidatePagingStages` does NOT false-fire** on the `$vectorSearch` body's
  own `limit` field (MEASURED: `Build` with `limit = 0` returned normally, no `ArgumentOutOfRangeException`).
  It keys on a **top-level** `$limit`/`$skip` element name, and `$vectorSearch` nests `limit` one level down.
  Good — but INFERRED corollary: a `limit: 0` therefore reaches the server unvalidated, which is a behaviour
  difference from `Take(0)` worth a deliberate decision.

**Why substitution alone is not enough (the decisive point).** The `$vectorSearch` body's *shape* varies with
the runtime option value in ways no value substitution can express:

| Varies | Depends on | Expressible by a sentinel? |
|---|---|---|
| `queryVector` value | runtime parameter | yes |
| `limit` value | runtime parameter | yes |
| `filter` presence/content | the compile-time `preFilter` lambda | yes (bake at compile time) |
| **`exact` key present, or `numCandidates` key present** | runtime `VectorQueryOptions.Exact` / `.NumberOfCandidates` | **no — key presence, not value** |
| **`index` value** | runtime `VectorQueryOptions.IndexName`, **or**, when that is null, a model lookup that can *throw* or *warn* | **no — and it can fail** |

So the answer to the handoff's Q2 is: **build the stage document at `Build(parameterValues)` time.**
`MongoPipelineFactory` today renders the entire template at `Create` time (READ); the slice needs a *deferred*
template slot — conceptually a `Func<IReadOnlyDictionary<string, object?>, BsonDocument>` that `Build` invokes
in place. That is the one new mechanism, and it is where the driver builder from Q2.1 naturally lives.

### Q2.3 — where index resolution and the two diagnostics live on a native path

**`Build`-time is sufficient — MEASURED-adjacent (READ from source), and the reason is that
`TranslateQuery` runs per execution, not at compile time.**

READ, `MongoShapedQueryCompilingExpressionVisitor.TranslateQuery`: the native branch does
`nativeFactory.Build(GetParameterValues(queryContext))` with a **live `QueryContext`**. So at that point the
provider has everything `ProcessVectorSearch` has today: the runtime option value, the model, and
`mongoQueryContext.QueryLogger`. Concretely:

| Concern | Where it lives today | Where it must live natively | Status |
|---|---|---|---|
| `Exact` + `NumberOfCandidates` validation | `ProcessVectorSearch`, before the driver call | `Build`-time, still **before** the driver call (Q1e: the driver throws its own `ArgumentException` otherwise) | READ |
| Index-name resolution (single index / none / multiple) | `ProcessVectorSearch` | `Build`-time — needs the model, which is reachable from `QueryContext.Context.Model` | READ |
| `VectorSearchNeedsIndex` warning | `_queryContext.QueryLogger` | same logger, reachable from `MongoQueryContext` in `TranslateQuery` | READ |
| `VectorSearchReturnedZeroResults` | `GetOnZeroResultsAction` reads `eq.AdditionalState[VectorQueryProperty]` / `[VectorQueryIndexName]` | **`AdditionalState` must be populated on the native branch** | READ — this is the actual bug the handoff predicted |

**READ, and it is exactly as the handoff describes:** `TranslateQuery`'s native branch constructs
`new MongoExecutableQuery(…, new(new Dictionary<string, object>()))` — an **empty** `AdditionalState` — at
`MongoShapedQueryCompilingExpressionVisitor.cs:910`. `GetOnZeroResultsAction` indexes into it unconditionally,
so on a native path a zero-result vector query would throw `KeyNotFoundException` rather than log. **INFERRED
(not executed, because nothing goes native today):** this is the failure, not a missing warning.

Two things soften it, both READ:

- `GetOnZeroResultsAction` keys off `queryExpression.CapturedExpression`, which the native path **still
  populates** (the QMTEV always captures the chain — READ, `Query/AGENTS.md` "Pipeline at a glance" and the
  `CapturedExpression` pitfall). So the *detection* half already works natively; only the *payload* is missing.
- It is wired at exactly **two** call sites (`:1001` in `ExecuteProjectedQuery`, `:1046` in
  `ExecuteShapedQuery`), both of which already receive the `MongoExecutableQuery` the native branch builds.

**Zero new `#if` lines are needed — this closes a risk the handoff flagged.** The handoff (§2.4) warns about
the live `#if EF8 || EF9` in `ProcessVectorSearch`'s `ParamValue`, and §7's bar is *zero `#if` lines added or
removed under `src/`*. Reading the query-parameter **name** off `Arguments[3..5]` needs the same EF8/EF9-vs-EF10
discrimination — **but the provider already has a version-agnostic helper for exactly this**:
`NativeTranslation/NativeQueryParameter.TryGetQueryParameterName(Expression, out string?)`, whose own doc
comment says the version difference "is encapsulated here so the native translator's call sites stay
version-agnostic" (READ). Use it and the bar is met.

---

## Q3 — what the in-scope buckets actually need

### Q3.1 — the bucket attribution, MEASURED (and it corrects the handoff)

Reproduced the handoff's baseline exactly — `MONGODB_EF_NATIVE_ONLY=1`, EF10 spec suite, both `MONGODB_URI`
and `ATLAS_URI` unset (TestContainers booted its own atlas-local container):
**112 failed / 2 passed / 4 skipped / 118 total**, splitting **82 / 24 / 6** by message. Byte-for-byte the
handoff's table.

I then attributed every failing case to its test method. **This is the part the handoff did not do, and it is
where its framing breaks:**

| The 82 "not natively representable" cases | Cases | Shape |
|---|---:|---|
| `VectorSearch_floats`, `_doubles`, `_Memory_*`, `_ReadOnlyMemory_*` (6 methods × 2 classes) | 24 | **bare call**, whole entity, different CLR vector spellings |
| `VectorSearch_binary_*` (9 methods × 2 classes) | 36 | **bare call**, whole entity, **binary** vectors |
| `VectorSearch_on_nested_reference` (2 methods) | 8 | **bare call**, owned sub-document path (`Preface.Floats`) |
| `VectorSearch_with_num_candidates` (ANN class only) | 2 | **bare call** |
| `VectorSearch_with_bool_pre_filter` + `_with_complex_pre_filter` | 8 | **`preFilter` argument** |
| `VectorSearch_floats_before_where` | **4** | **`.Where(e => e.IsPublished)` composed after** |
| **total** | **82** | |

**So: bare call 70, `preFilter` 8, `.Where` 4.** The handoff's "106 of the 112 failures are *composed* shapes,
not the bare `VectorSearch(...)` call" is **REFUTED** — 70 of 112 are precisely the bare call. The owner's
ruling 1 ("bare call + the 82-test `.Where(...)` bucket") is still the right scope; the *reason* it is a good
scope is different from the one recorded.

### Q3.2 — the 24 bucket, and the `.Where` red herring

| The 24 "projects a non-entity result" cases | Cases | Projection leaf |
|---|---:|---|
| `VectorSearch_with_projection` | 4 | `.Select(e => e.Author)` — **bare scalar** |
| `VectorSearch_with_projection_of_score` | 4 | `new { e.Author, Score = Mql.Field(e, "__score", DoubleSerializer.Instance) }` |
| `VectorSearch_with_projection_of_score_using_EF_Property` | 4 | `new { e.Author, Score = EF.Property<double>(e, "__score") }` |
| `VectorSearch_with_projection_of_entity_and_score` | 4 | `new { Book = e, Score = Mql.Field(…) }` — **mixed entity + scalar** |
| `..._of_entity_and_score_using_EF_Property` | 4 | `new { Book = e, Score = EF.Property<double>(…) }` — **mixed** |
| `VectorSearch_with_projection_of_constructed_entity_and_score` | 4 | `new { Book = new Book { … }, Score = … }` — **entity construction** |

**MEASURED (by reading the tests, then confirming they are all in the 24 bucket): every one of these 24 also
composes the *same* `.Where(e => e.Title.Contains("Action") || e.Title.Contains("DbContext"))`.** That
predicate is `string.Contains` with constant terms in an `||` — a shape the native translator already supports
(`MongoRegexExpression` + `$or`, READ, `Query/AGENTS.md` EF-329 note). **So `.Where`-after-`VectorSearch` is
never the blocker for any of the 112.** The blocker for all 24 is the projection.

**Only 4 of the 24 are the bare-scalar shape ruling 3 defers.** Of the other 20: 12 are the mixed /
entity-constructing shapes that are unambiguously the SP3-wide boundary (step 3), and **8 —
`new { e.Author, Score = … }` — are an ordinary anonymous member-access projection with one synthetic leaf**,
which is the shape `NativeProjectionBinder.TryPopulateNativeProjection` already handles apart from the score
leaf. Those 8 are the only tests in the entire suite that would prove ruling 2.

### Q3.3 — `__score`: `MongoElementRefExpression` is the right vehicle for the emit side

**MEASURED:** `MongoAggregationExpressionRenderer.Render(new MongoElementRefExpression("__score",
typeof(double)), placeholders)` renders to the string `"$__score"` — exactly the `$project` leaf value the
baselines assert (`{"Author": "$Author", "Score": "$__score", "_id": 0}`). Executed end to end against the live
index: OK, 4 rows.

So `MongoElementRefExpression` **is** the right lead, as ruling 2 guessed. It already exists for the GroupBy
flatten (`_id.<Name>` reads with no backing `IProperty`), which is the same "raw path, no property" need.

**MEASURED, and it gives the design an option:** `$meta: "vectorSearchScore"` can be read **directly** in a
later `$project` with **no** `$addFields` companion at all —
`[$vectorSearch, {$project:{Author:"$Author", Score:{$meta:"vectorSearchScore"}, _id:0}}]` returns 4 correct
rows. So the companion is not load-bearing for a projection. It **is** what every committed baseline asserts,
so emitting it unconditionally (as `ProcessVectorSearch` does) is the cheaper choice — omitting it would force
70+ re-baselines.

**Is the unconditional `$addFields{__score}` safe for the native *streaming* materializer?** INFERRED **yes**,
from source: `MongoStreamingEntityMaterializerRewriter.BuildFillLoop` builds a name-dispatch if-chain whose
**base case is `reader.SkipValue()`** (READ, `MongoStreamingEntityMaterializerRewriter.cs:416`), so an
unrecognised top-level element such as `__score` is skipped rather than mis-read. **UNVERIFIED by execution** —
nothing goes native today, so I could not run a whole-entity vector query through the streaming path.
Flag this for the slice's own tests.

### Q3.4 — `Mql.Field`: recommend a **targeted decline**, not a native meaning

`Mql.Field(e, "__score", DoubleSerializer.Instance)` is a **driver** API (`MongoDB.Driver.Mql`), whose whole
purpose is to address a document element that EF's model does not know about, on the driver-LINQ path. Two
options:

- **Give it a native meaning:** recognise `Mql.Field(param, <const name>, <serializer>)` in
  `NativeProjectionBinder.TryTranslateLeaf` and emit a `MongoElementRefExpression(name, T)`. Mechanically
  small (the emit side is proven in Q3.3), but it is a **general** widening — `Mql.Field` can name *any*
  element, not just `__score`, so admitting it natively opens read-back questions (serializer honouring,
  value-converted properties) that have nothing to do with vector search.
- **Decline it** (leave `TryTranslateLeaf` returning false ⇒ graceful fallback), and — if the owner brings the
  8 tests in scope — recognise **only** `EF.Property<double>(e, "__score")` and the literal element name
  `"__score"` natively.

**Recommendation: decline `Mql.Field` for this slice.** Reasons: (a) it costs nothing, because those tests are
in the out-of-scope 24 bucket anyway; (b) a decline is a graceful fallback with correct results, the disposition
they already have; (c) admitting a general driver-element-addressing API into the native projection binder is a
projection-long-tail (step 3) decision, not a vector-search one. **UNVERIFIED:** whether declining `Mql.Field`
while admitting `EF.Property` would leave the two sibling tests with *different* dispositions in a way that
looks arbitrary to a reader — worth a sentence in the design either way.

---

## Q4 — the two gates, and the operator style

### Q4.1 — slot-style or `Translate*`-override-style? **Slot-style, and the choice is nearly forced.**

**READ, and this is the load-bearing fact: there is no `Translate*`-override option for `VectorSearch`.**
`MongoQueryableMethodTranslatingExpressionVisitor.VisitMethodCall`'s `switch (method.Name)` (`:100–152`) does
exactly one thing for every arm — `base.VisitMethodCall(methodCallExpression)` — and EF Core's base
`QueryableMethodTranslatingExpressionVisitor` dispatches only methods it knows. There is no
`TranslateVectorSearch` hook, and `MongoQueryableExtensions.VectorSearch` is not a `QueryableMethods` member.
The "`Translate*`-override style" that `Select`/`OfType`/`Distinct`/`Union` use is therefore **not available**;
the only alternative to a slot would be a provider-authored branch doing the work inline inside that switch,
which is neither of the two documented styles.

Meanwhile the call **already lands** in `NativeSlotPopulator.PopulateNativeSlots` (`:156`) — READ, because
`"VectorSearch"` matches no `case` label and the switch has no default — which is where the second gate fires
today. That is the natural seam.

**Recommendation, concretely:**

- Add a **dedicated slot** on `MongoSelectDefinition` (e.g. `VectorSearch`), **not** an entry in `PipelineOps`.
  Rationale: `AppendSelectOpStages` emits `PipelineOps` **verbatim in arrival order**, and since `VectorSearch`
  is root-only by API construction (READ: both public overloads take `this DbSet<TSource> source`) it would
  *happen* to arrive first — but "first" would then be incidental rather than structural, and the whole point
  of the stage is that first-ness is a hard server constraint (Q1a). A dedicated slot emitted by its own block
  makes the invariant explicit.
- Populate it from a new branch in `PopulateNativeSlots`, keyed on the existing internal
  `methodCallExpression.IsVectorSearch()` (not on a `QueryableMethods` constant, which does not exist for it).
- Add it to `IsNativeRepresentableSlotOperator` so the catch-all stops firing — same as every other native
  operator (`Query/AGENTS.md`'s "native catch-all whitelist must stay in sync" pitfall).
- Do **not** let it set `HasTerminalOperator`. `VectorSearch` is a root anchor, not a terminal; a `.Where`
  composed after it must still record into `PipelineOps` normally.

### Q4.2 — the near-duplicate `ContainsVectorSearch` in the QMTEV: **leave it alone**

`MongoQueryableMethodTranslatingExpressionVisitor`'s private `ContainsVectorSearch` (`:2543`) is consumed only
by `IsPlainWholeEntitySelect` (`:2463`) and `IsPlainProjectedSelect` (`:2503`) — the **set-operation operand**
scope gates (READ). Recommendation: **do not change it in this slice.**

- No test in the repo composes a set operation with a `VectorSearch` (READ, agrees with handoff §2.3).
- Q1c MEASURED a real hazard: `Union`'s dedup is `$group{_id:"$$ROOT"}`, which (a) **reorders rows**, destroying
  score order, and (b) with the `$addFields{__score}` companion present makes the **score itself part of the
  dedup key**, so two otherwise-identical documents with different scores would not dedup.
- Leaving the exclusion in place keeps set-ops-over-vector-search on the driver-LINQ fallback, which is its
  current, working disposition.

The gate that **must** change is `MongoShapedQueryCompilingExpressionVisitor.ClassifyNativeDisposition`'s
`containsVectorSearch` branch. Note its pure 4-argument overload is unit-tested
(`NativeDispositionTests.Vector_search_is_fallback_even_when_route_is_native`,
`Hard_decline_takes_precedence_over_vector_search` — READ), so those two tests are re-baseline work, not
incidental.

### Q4.3 — is the lowerer change genuinely minor? **CONFIRMED, by reading.**

The handoff's §2.2 marks this **INFERRED**. It is right. `MongoSelectLowerer.Lower` (`:57–64`) is:

```csharp
var select = query.Select;
var stages = new List<MongoPipelineStage>();

// 1. $match / $sort / $skip / $limit ops, emitted verbatim …
AppendSelectOpStages(select.PipelineOps, stages);
```

A new block between the `stages` declaration and `AppendSelectOpStages` is a single-site, few-line insertion
with no reordering of anything else. **READ:** two new stage IR types are needed (`$vectorSearch` and
`$addFields`; the `Stages/` directory has 15 types and neither), plus two `MongoPipelineFactory.RenderStage`
arms — and note `RenderStage`'s `switch` has **no default arm**, it throws
`NativeTranslationNotSupportedException` for an unknown stage, so a missing arm fails loudly rather than
silently. (I exercised exactly this path in the scratch worktree by adding a throwaway stage + arm; it worked
first time.)

---

## What the slice would actually win

| Bucket | Cases | In scope per the rulings? |
|---|---:|---|
| Bare call — plain/Memory/ReadOnlyMemory float & double | 24 | **yes** |
| Bare call — binary vectors | 36 | **yes** (nothing binary-specific in the query pipeline; the driver builder handles the wire form) |
| Bare call — owned sub-document path | 8 | **yes** |
| Bare call — explicit `numCandidates` | 2 | **yes** |
| `preFilter` | 8 | **yes** (ruling 1) |
| `.Where` composed after | 4 | **yes** (ruling 1) — and Q1c MEASURED that `$match` after `$vectorSearch` is fine |
| `VectorSearch_logs_for_zero_results` | 4 | **yes** (ruling 4) |
| `VectorSearch_throws_if_num_candidates_set_for_exact_search` | 2 | **yes** (ruling 4 / Q1e) |
| **subtotal** | **88** | |
| Projection bucket | 24 | **no** (ruling 3), *except* possibly the 8 `{ member, score }` cases — see §8 |
| **total suite** | **112** | |

INFERRED: **88 of 112** (79%). With the 8 `{ member, score }` cases, **96 of 112** (86%).

---

## Recommended slice shape and size

**Shape (a design task should own the detail; this is the skeleton):**

1. **`MongoPipelineFactory`: a deferred template slot.** `Create` keeps rendering everything else at compile
   time; one slot holds a callback invoked by `Build(parameterValues)` and spliced in as the **first** stage.
   *(The one genuinely new mechanism. Everything else is a variation on existing patterns.)*
2. **A shared `VectorSearch` stage builder**, extracted from `ProcessVectorSearch` so **both** paths call one
   implementation: validate `Exact`+`NumberOfCandidates`, resolve the index (throw / warn / pick), build
   `VectorSearchOptions<T>` (with a `BsonDocumentFilterDefinition<T>` from the natively-rendered `preFilter`),
   call `PipelineStageDefinitionBuilder.VectorSearch(...)`, `Render(RenderArgs<T>)`, return the `BsonDocument`
   **plus** the `(IProperty, indexName)` pair for `AdditionalState`. Extracting rather than duplicating is what
   keeps the two paths from drifting on index resolution and the diagnostics.
3. **IR + lowerer:** a `MongoSelectDefinition.VectorSearch` slot, a `MongoVectorSearchStage` and a
   `MongoAddFieldsStage`, a block ahead of `AppendSelectOpStages`, and two `RenderStage` arms.
4. **Gates:** a `PopulateNativeSlots` branch + `IsNativeRepresentableSlotOperator` entry; delete/condition
   `ClassifyNativeDisposition`'s `containsVectorSearch` branch and re-baseline the two `NativeDispositionTests`
   cases. Leave the QMTEV duplicate alone.
5. **Diagnostics:** populate `AdditionalState` on `TranslateQuery`'s native branch; route the
   `VectorSearchNeedsIndex` warning through `mongoQueryContext.QueryLogger`.
6. **Tests:** a new functional `NativeVectorSearchTests` (every "goes native" claim proved by a `NativeOnly`
   run that **succeeds**, never by MQL shape; every data assertion pinning **score order or the score itself**,
   never the row count — trap 1); plus a streaming-materializer test for the `__score` extra field (Q3.3's
   unverified half).

**Size: comparable to EF-368 / EF-372 — a medium slice, not a small one.** The reasons it is *not* larger than
those: the emitted MQL should be unchanged (so no spec re-baselines), binary vectors and the owned-path
`path` come free from the driver builder, `NativeQueryParameter` removes the `#if` risk, and the lowerer change
is a few lines. The reasons it is *not* smaller: the deferred-`Build` mechanism is new, and the diagnostics
re-plumbing (ruling 4) touches three files and is genuinely per-execution work. Expect **4–6 implementation
tasks** after a design doc, with task 1 being the deferred-template mechanism on its own.

---

## §8 — The one question only the owner can answer

**Should the 8 `new { e.Author, Score = … }` cases (`VectorSearch_with_projection_of_score` and
`…_using_EF_Property`, 4 each) come into this slice?**

Why it needs a ruling rather than a judgement call:

- Ruling 2 says `__score` is **in** scope and asks for "a native read". Ruling 3 puts the whole 24-case bucket
  **out**. **MEASURED: those two rulings collide** — every test that reads `__score` is in the 24 bucket, so as
  the rulings currently stand the slice would implement a `__score` read that **no test exercises**, and ship
  it unproven. That is the opposite of this branch's stated standard.
- The 8 are not the SP3 bare-projection boundary that ruling 3 is really about. They are an ordinary anonymous
  member-access projection (`NativeProjectionBinder` already handles that shape) with **one** synthetic leaf,
  and the emit side of that leaf is MEASURED working (Q3.3). The other 16 — 4 bare scalar, 12 mixed /
  entity-constructing — genuinely are step 3.
- The cost of including them is bounded and specific: recognise `EF.Property<double>(e, "__score")` in
  `NativeProjectionBinder.TryTranslateLeaf` → `MongoElementRefExpression`, plus the DOM shaper read-back for an
  alias with no backing `IProperty` (**UNVERIFIED** — I did not test the read-back half; the GroupBy flatten
  does the analogous thing, so it is plausible, but it is the piece that could surprise).

**Three coherent answers**, any of which is fine as long as it is deliberate:

- **(a) Include the 8.** Slice wins 96/112, and ruling 2 gets a test.
- **(b) Keep them out and drop the `__score` *read* from the slice** — still emit the `$addFields` companion
  (needed for baseline parity), but do not build a native `__score` read. Slice wins 88/112, ships nothing
  unproven.
- **(c) Keep them out and build the read anyway**, accepting it is unproven until step 3. *(Not recommended.)*

A secondary, much smaller question, flagged only so it is not decided by accident: **should a `limit: 0` be
validated client-side?** MEASURED, `Build`'s `ValidatePagingStages` will not catch it inside a `$vectorSearch`
body, so `VectorSearch(..., limit: 0)` would go to the server rather than throw `ArgumentOutOfRangeException`
the way `Take(0)` does. **UNVERIFIED** what the current driver-LINQ path does with `limit: 0`.

---

## §7 — Reproduction

Everything below was run this session. Mutations lived in a throwaway worktree
(`git worktree add <scratch>/wt 8e5ae4a5`), which has been **removed** (`git worktree list` verified — the
three remaining `agent-*` worktrees belong to other sessions). The main tree is clean apart from this file.
The probe container has been removed (`docker rm -f`).

```bash
# ── Q1: raw server probe, no EF, no C# driver ────────────────────────────────────────────────
docker run -d -P --name vs-spike-task0-atlas mongodb/mongodb-atlas-local:latest
docker port vs-spike-task0-atlas                     # -> 27017/tcp -> 0.0.0.0:63039
docker exec -i vs-spike-task0-atlas mongosh --quiet < setup.js    # seed 4 docs + createSearchIndex, poll to READY
docker exec -i vs-spike-task0-atlas mongosh --quiet --eval "$(cat probe1.js)"

# ── Q3.1: the bucket attribution (both MONGODB_URI and ATLAS_URI UNSET) ──────────────────────
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"
MONGODB_EF_NATIVE_ONLY=1 dotnet test \
  tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~VectorSearch" \
  --logger "trx;LogFileName=vs-nativeonly.trx" --results-directory <scratch>
# -> Failed: 112, Passed: 2, Skipped: 4, Total: 118    (matches the handoff exactly)
# then a python pass over the .trx grouping Failed cases by message AND by test method.
# NB: classify Assert.Throws BEFORE "not natively representable" — the Assert.Throws message
#     QUOTES the inner exception, so a naive check counts 88 instead of 82.

# ── Q2 / Q3.3: renderer + placeholder probes, in the throwaway worktree ──────────────────────
git worktree add <scratch>/wt 8e5ae4a5
#   (1) NEW src/.../Query/NativeTranslation/Stages/MongoVectorSearchStage.cs   (throwaway IR)
#   (2) MongoPipelineFactory.RenderStage: one added arm + a RenderVectorSearch helper
#   (3) NEW tests/.../FunctionalTests/Query/Ef322VectorSpikeTests.cs   (probes A, B, C, D)
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"
VS_SPIKE_URI="mongodb://localhost:63039/?directConnection=true" \
MONGODB_URI="mongodb://localhost:63039/?directConnection=true" ATLAS_URI=Disabled \
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/... -c "Debug EF10" --no-build \
  --filter "FullyQualifiedName~Ef322VectorSpikeTests" --logger "console;verbosity=detailed"
git worktree remove --force <scratch>/wt
docker rm -f vs-spike-task0-atlas
```

**One deliberate deviation from the environment rules, recorded honestly.** The Q2/Q3.3 probe run set
`MONGODB_URI`/`ATLAS_URI` at my own already-booted atlas-local container instead of letting TestContainers boot
one. That container **is** `mongodb/mongodb-atlas-local`, so Atlas Search was real; the reason was determinism
and speed for a throwaway probe. **The Q3.1 measurement — the only one whose numbers this document relies on —
was run with both variables UNSET, per the rules,** and reproduced the published baseline exactly.

### Files that were mutated and are now gone

| File | Change | Fate |
|---|---|---|
| `src/…/NativeTranslation/Stages/MongoVectorSearchStage.cs` | new throwaway IR | removed with the worktree |
| `src/…/NativeTranslation/MongoPipelineFactory.cs` | one `RenderStage` arm + `RenderVectorSearch` | removed with the worktree |
| `tests/…/FunctionalTests/Query/Ef322VectorSpikeTests.cs` | 4 probe methods | removed with the worktree |

Nothing under `src/` in the main tree was touched.
