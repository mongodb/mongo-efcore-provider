# EF-322 `VectorSearch` slice — design

*Written 2026-08-06 against `NativeQueryOngoing` @ `dcdfa7e2` (clean). Task 1 of the slice: **design only, no
`src/` change**. Inputs: `2026-08-06-vectorsearch-spike-findings.md` (the Task-0 spike, primary),
`2026-08-06-vectorsearch-slice-handoff.md` §8 as amended, `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md`,
`docs/native-query-status-EF-322.md` §4/§5/§9.8, `.claude/agents/vector-search-reviewer.md`.*

**Tagging convention, applied strictly** (same vocabulary as the spike, so the two documents compose):

- **MEASURED** — I ran it *this session*; the command and output are in §11.
- **READ** — established by reading source at `dcdfa7e2`; no execution.
- **CITED** — measured by the spike or the handoff, not re-run by me.
- **INFERRED** — a conclusion drawn from MEASURED/READ/CITED facts, not itself observed.
- **UNVERIFIED** — I did not establish it. Stated so nobody mistakes it for settled.

**Two things this document does NOT do.** It does not re-derive the spike's settled inputs (server first-stage
constraint, the driver-builder-at-`Build`-time approach, slot-style over `Translate*`-override, bucket
attribution, `MongoElementRefExpression` rendering). And it does not change the preprocessor's
`VectorSearch` extraction/re-insertion — see §9.1, because that is on the vector-search reviewer's
*escalate-to-user* list and the answer is "untouched".

---

## 0. Executive summary

| | |
|---|---|
| **Target** | ~~**96 of 112**~~ **AS BUILT: 92 of 112** currently-failing `MONGODB_EF_NATIVE_ONLY=1` vector cases (MEASURED at the end of Task 6, by failing-test-name SET against a base worktree). **20** are left failing: the 16 named in the Appendix **plus** the 4 `VectorSearch_with_complex_pre_filter` cases (see the correction box above §8 Task 5). Default `Native` unmoved at 114 passed / 0 failed / 4 skipped, zero baseline diffs. |
| **The one new mechanism** | A **deferred `Build`-time stage slot** in `MongoPipelineFactory` (§3). Everything else is a variation on an existing pattern. |
| **The one shared component** | `VectorSearchStageBuilder` — extracted from `ProcessVectorSearch` so BOTH paths run the same validation, index resolution, diagnostics and driver call, in the same order, across the same reflection boundary (§4). This is what makes exception parity structural rather than a thing to remember. |
| **How the two gates are kept in sync** | Gate 1's signal changes from `containsVectorSearch` to **`hasUnboundVectorSearch`** = "the captured chain has a `VectorSearch` and the slot populator did **not** bind it". The gates then cannot open independently — opening gate 2 (populating the slot) is what closes gate 1, and *not* opening gate 2 leaves gate 1 shut (§6). The silent-wrong-data state is unreachable by construction, not merely by commit ordering. |
| **`__score` read-back risk** | **RESOLVED BY READING, not left open** (§7). It is the same generic raw-alias read (`BsonBinding.CreateGetElementValue`) already shipped for native count leaves, native arithmetic leaves and the GroupBy flatten; `TryResolveFieldAccess` has explicit `EF.Property` **and** `Mql.Field` arms that return `Property: null` for an unmapped name, which is exactly the `__score` case. No probe needed. |
| **`limit: 0` today** | **MEASURED** end-to-end through EF at `dcdfa7e2`: `System.Reflection.TargetInvocationException` wrapping `ArgumentOutOfRangeException: Value is not greater than 0: 0. (Parameter 'limit')`. It is thrown from `ProcessVectorSearch`'s reflection `Invoke` of the driver builder, *before* any I/O. Parity is achieved by keeping the reflection boundary in the shared builder (§4.4). |
| **Breaking change** | **VERIFIED NONE in Task 6, and no `BREAKING-CHANGES.md` entry.** Of the 15 `src/` files the slice touches, only two exist at `v10.0.2` / `v9.1.2` / `v8.4.2` (`MongoEFToLinqTranslatingExpressionVisitor.cs`, `MongoShapedQueryCompilingExpressionVisitor.cs`) and both are `internal sealed class` at the tag and at HEAD; `MongoQueryableExtensions` is byte-identical to all three tags; `MongoQueryMode.cs` exists at none of them. See §10. |
| **Decision the owner must see** | The ruling "the 8 score-projection cases are IN" and the ruling "`Mql.Field` gets a targeted decline" **collide**: 4 of those 8 (`VectorSearch_with_projection_of_score`) are spelled with `Mql.Field`. §7.4 states the reconciliation I designed for and the alternative. |

---

## 1. What changes, in one table

| # | File | Change | Reachable when |
|---|---|---|---|
| 1 | `Query/NativeTranslation/MongoNativeBuildContext.cs` **(new)** | Per-execution build context: parameter values, serializer factory, query logger, the `AdditionalState` dictionary. | Task 1 |
| 2 | `Query/NativeTranslation/MongoPipelineFactory.cs` | Template slots become "document **or** deferred builder"; new `Build(in MongoNativeBuildContext)`; the old `Build(parameterValues)` throws if a deferred slot exists. | Task 1 |
| 3 | `Query/VectorSearchStageBuilder.cs` **(new)** | `Resolve(...)` + `CreateStage(...)` + `RenderStage(...)`, extracted verbatim from `ProcessVectorSearch`. | Task 2 |
| 4 | `Query/Visitors/MongoEFToLinqTranslatingExpressionVisitor.cs` | `ProcessVectorSearch` becomes a thin caller of #3. **Behaviour byte-identical.** | Task 2 |
| 5 | `Query/Expressions/MongoVectorSearch.cs` **(new)** | The IR record for the slot. | Task 3 |
| 6 | `Query/Expressions/MongoSelectDefinition.cs` | `internal MongoVectorSearch? VectorSearch { get; set; }`. **Not** part of `HasTerminalOperator`. | Task 3 |
| 7 | `Query/NativeTranslation/Stages/MongoVectorSearchStage.cs`, `…/MongoVectorSearchScoreStage.cs` **(new)** | Two stage IR types. | Task 3 |
| 8 | `Query/NativeTranslation/MongoSelectLowerer.cs` | A block **ahead of** `AppendSelectOpStages`. | Task 3 |
| 9 | `Query/Visitors/MongoShapedQueryCompilingExpressionVisitor.cs` | `TranslateQuery` uses the context `Build` overload and threads `AdditionalState`; `ClassifyNativeDisposition`'s third parameter becomes `hasUnboundVectorSearch`; `TryBuildNativeFactory`'s stale comment rewritten. | Tasks 3 (plumbing) / 4 (gate) |
| 10 | `Query/NativeTranslation/NativeSlotPopulator.cs` | A `call.IsVectorSearch()` branch that binds the slot (or declines); `IsNativeRepresentableSlotOperator` note. | Task 4 |
| 11 | `Query/NativeTranslation/NativeProjectionBinder.cs` | A `__score` leaf branch, gated on the vector slot being populated. | Task 5 |
| 12 | `tests/.../UnitTests/Query/NativeTranslation/NativeDispositionTests.cs` | Two tests renamed + one added. | Task 4 |
| 13 | `tests/.../FunctionalTests/Query/NativeVectorSearchTests.cs` **(new)** | The behavioural net (§9). | Tasks 4–6 |
| 14 | `Query/AGENTS.md`, `docs/native-query-status-EF-322.md` | As-built note + the set-op/score-order hazard note + the corrected "not native at all" entries. | Task 6 |

**Not changed, deliberately:** `MongoQueryTranslationPreprocessor` (§9.1); the QMTEV's near-duplicate
`ContainsVectorSearch` and the two set-op scope gates that consume it (§9.2); `MongoQueryableExtensions`;
`VectorQueryOptions`; `MongoExecutableQuery`'s public shape; anything under `Metadata/` or `Storage/`.

---

## 2. Where the stage comes from, end to end

```
VectorSearch(root, propertyLambda, preFilter, queryVector, limit, options)
  │  preprocessor lifts it before nav-expansion, re-inserts after   ← UNCHANGED (§9.1)
  ▼
QMTEV.VisitMethodCall  → falls through the switch (no case)
  → NativeSlotPopulator.PopulateNativeSlots
        NEW: `else if (call.IsVectorSearch())`
              translate preFilter with MongoExpressionTranslator  (decline ⇒ MarkNotNativelyRepresentable)
              check queryVector/limit/options are parameter-or-constant nodes (decline otherwise)
              Select.VectorSearch = new MongoVectorSearch(...)          ← GATE 2 OPENS HERE
  ▼
gate: ClassifyNativeDisposition(Route, IsFallbackWrongData, hasUnboundVectorSearch, mode)
      hasUnboundVectorSearch = ContainsVectorSearch(CapturedExpression) && Select.VectorSearch is null
                                                                        ← GATE 1 IS THE SAME FACT
  ▼
MongoSelectLowerer.Lower
      NEW block, BEFORE AppendSelectOpStages:
        if (select.VectorSearch is { } vs) { stages.Add(new MongoVectorSearchStage(vs));
                                             stages.Add(new MongoVectorSearchScoreStage()); }
      then $match/$sort/$skip/$limit, $lookup, terminals — all unchanged
  ▼
MongoPipelineFactory.Create
      MongoVectorSearchStage       → a DEFERRED SLOT (a Func<MongoNativeBuildContext, BsonDocument>),
                                     closing over the preFilter rendered NOW into the shared PlaceholderTable
      MongoVectorSearchScoreStage  → the constant { $addFields: { __score: { $meta: "vectorSearchScore" } } }
  ▼
MongoPipelineFactory.Build(in MongoNativeBuildContext)   ← per execution, inside TranslateQuery
      invoke the deferred slot → VectorSearchStageBuilder.Resolve + CreateStage + RenderStage
        · Exact+NumberOfCandidates guard   (throws InvalidOperationException, as today)
        · member resolution                (throws InvalidOperationException, as today)
        · index resolution / VectorSearchNeedsIndex warning via context.QueryLogger
        · writes AdditionalState[VectorQueryProperty] / [VectorQueryIndexName]
        · PipelineStageDefinitionBuilder.VectorSearch(...)  via the SAME reflection Invoke as today
        · .Render(new RenderArgs<T>(entitySerializer, registry)).Document
      then SubstituteDocument over the whole array (so preFilter placeholders resolve)
  ▼
MongoExecutableQuery { NativePipeline = …, AdditionalState = the filled dictionary }
  ▼
QueryingEnumerable → on zero rows, GetOnZeroResultsAction reads AdditionalState → VectorSearchReturnedZeroResults
```

**Why the vector block is placed before `AppendSelectOpStages` and nowhere else.** `$vectorSearch` must be the
first stage in *a* pipeline (CITED: spike Q1a — `Location40602`, uniformly, for all six preceding-stage shapes
probed). `MongoSelectLowerer.Lower` has no prepend concept and every other stage is appended in arrival order;
a dedicated block at the very top is the only position that makes first-ness structural rather than incidental
(READ). It is also why the slot is a **dedicated `MongoSelectDefinition.VectorSearch` field and not a
`PipelineOps` entry** — `AppendSelectOpStages` emits `PipelineOps` verbatim in arrival order, so a
`VectorSearch` recorded there would only *happen* to come first (CITED: spike Q4.1; READ, confirmed at
`MongoSelectLowerer.cs:62-64`).

---

## 3. The deferred `Build`-time stage slot

### 3.1 Why value substitution alone is not enough

CITED (spike Q2.2): `PlaceholderTable` substitution works fine inside a `$vectorSearch` body, including a
whole array-valued `queryVector`, but the **shape** of the body varies with a runtime `VectorQueryOptions` in
ways a value sentinel cannot express — whether the `exact` key or the `numCandidates` key is present at all,
and which `index` value is used (which additionally can *throw* or *warn*). So the body must be **constructed**
at `Build` time, not merely **substituted**.

MEASURED this session, and it makes the case sharper than the spike put it: the driver's builder **derives
`numCandidates` from `limit`** when the caller leaves it null —

```
idx, no numCandidates, limit=4 → { "$vectorSearch": { …, "limit": 4, "numCandidates": 40, … } }
idx, no numCandidates, limit=3 → { "$vectorSearch": { …, "limit": 3, "numCandidates": 30, … } }
```

— i.e. `numCandidates = limit * 10`, and `limit` is a **runtime parameter**. A hand-written renderer would have
to reproduce that derivation (and the field ORDER `path, limit, numCandidates, index, filter, queryVector`) to
keep 70+ committed `AssertMql` baselines. Reusing the driver builder is not a shortcut; it is the thing that
makes "zero re-baselining" true.

### 3.2 The shape

```csharp
// Query/NativeTranslation/MongoNativeBuildContext.cs   (new, internal)
internal readonly record struct MongoNativeBuildContext(
    IReadOnlyDictionary<string, object?> ParameterValues,
    BsonSerializerFactory SerializerFactory,
    IDiagnosticsLogger<DbLoggerCategory.Query> QueryLogger,
    IDictionary<string, object> AdditionalState);
```

`MongoPipelineFactory`'s template becomes a list of slots, each either a baked `BsonDocument` (everything that
exists today) or a `Func<MongoNativeBuildContext, BsonDocument>`:

```csharp
public BsonDocument[] Build(IReadOnlyDictionary<string, object?> parameterValues);   // existing signature
public BsonDocument[] Build(in MongoNativeBuildContext context);                     // new
```

- The existing overload **throws `InvalidOperationException`** when any deferred slot is present, rather than
  silently emitting a pipeline with a hole. Keeping it means the ~30 existing `MongoPipelineFactory` unit tests
  and any other caller are untouched.
- `Build` runs the deferred slot **first**, splices its document into position, and then runs the existing
  `SubstituteDocument` walk over the **whole** result array. That is what lets a `preFilter` closing over a
  captured local work: the pre-filter is rendered at `Create` time into the **shared** `PlaceholderTable`, its
  sentinels ride inside the `BsonDocumentFilterDefinition` the driver embeds verbatim, and the ordinary
  substitution pass resolves them afterwards. (INFERRED from READ of `SubstituteValue`, which tests for the
  sentinel *before* recursing and is therefore position-agnostic; CITED as MEASURED-to-work inside a
  `$vectorSearch` body by spike Q2.2.)
- `ValidatePagingStages` is **left exactly as it is**. It keys on a *top-level* `$limit`/`$skip` element name
  and does not see inside a `$vectorSearch` body (CITED spike Q2.2; and see §4.4 — extending it would *break*
  `limit: 0` parity, not fix it).

**Layering note for the reviewer.** `MongoPipelineFactory`'s own doc comment currently says "No
EF-version-conditional code appears here; bridging `QueryContext.Parameters` (EF10) vs `ParameterValues`
(EF8/EF9) is the caller's responsibility." `MongoNativeBuildContext` preserves that: it carries an already-
bridged `IReadOnlyDictionary<string, object?>`, and `TranslateQuery` builds it using the existing
`GetParameterValues(queryContext)` helper, which owns the one `#if` (READ,
`MongoShapedQueryCompilingExpressionVisitor.cs:953-958`). **Zero `#if` lines are added or removed under
`src/`.**

---

## 4. `VectorSearchStageBuilder` — the shared component, and why it is shared

### 4.1 Extract, do not duplicate

Both paths must agree on: the `Exact`+`NumberOfCandidates` guard *and its position*, member resolution, index
resolution (throw / warn / pick-the-single-one), the `VectorSearchOptions<T>` construction, and the driver call.
Duplicating any of that is how the two paths drift on the diagnostics — the exact failure the owner's ruling 4
exists to prevent. One implementation, two callers.

### 4.2 The split, and why it is three methods rather than one

```csharp
internal static class VectorSearchStageBuilder
{
    // NO reflection. Exceptions surface unwrapped, exactly as ProcessVectorSearch throws them today.
    internal static ResolvedVectorSearch Resolve(
        IEntityType entityType,
        LambdaExpression propertyLambda,
        VectorQueryOptions? options,
        IDiagnosticsLogger<DbLoggerCategory.Query> queryLogger);

    // Reflection: VectorSearchOptions<T> property sets + PipelineStageDefinitionBuilder.VectorSearch Invoke.
    // Identical shape and identical (default) BindingFlags to today ⇒ identical exception wrapping.
    internal static object CreateStage(
        IEntityType entityType,
        LambdaExpression propertyLambda,
        ResolvedVectorSearch resolved,
        object? filterDefinition,   // ExpressionFilterDefinition<T> (bridge) or BsonDocumentFilterDefinition<T> (native)
        QueryVector queryVector,
        int limit);

    // Native only: PipelineStageDefinition<T,T>.Render(new RenderArgs<T>(serializer, registry)).Document
    internal static BsonDocument RenderStage(object stage, IEntityType entityType, IBsonSerializer entitySerializer);

    internal readonly record struct ResolvedVectorSearch(IPropertyBase Member, VectorQueryOptions Options);
}
```

`Resolve` is deliberately reflection-free **because exception identity depends on it**: today the
`Exact`+`NumberOfCandidates` guard throws `InvalidOperationException` from ordinary code, *before* the
reflection `Invoke`, and `VectorSearch_throws_if_num_candidates_set_for_exact_search` asserts
`Assert.ThrowsAsync<InvalidOperationException>` (READ, `VectorSearchExactMongoTest.cs:323-333`). Moving that
guard inside a reflection-invoked generic would wrap it in `TargetInvocationException` and turn 2 currently
green cases red **on both paths**. The three-way split is the mechanical guarantee that it cannot happen.

`entityType` is a **parameter**, not re-derived inside the helper — each caller keeps its own authoritative
input (the bridge has `_queryContext.Context.Model.FindEntityType(_source.Type.TryGetItemType())`; the native
path has `mongoQueryExpression.CollectionExpression.EntityType`). Same reasoning, same precedent, as EF-373's
`ResolveJoinLookup` keeping the inner CLR type as a parameter (READ, `Query/AGENTS.md` EF-373 note).

### 4.3 The `preFilter`, and the one byte-identity question

The native path builds a `BsonDocumentFilterDefinition<T>` from the natively rendered `$match` document, so it
needs **no driver-LINQ bridge visitor** (CITED spike Q2.1, MEASURED to render identically to an
`ExpressionFilterDefinition`; re-MEASURED this session — see §11).

The spike left "is the native renderer's output byte-identical to the bridge's for the repo's actual
`complex_pre_filter`?" **UNVERIFIED**. I have now settled it **by reading**, and the answer is yes, for both
in-scope shapes:

| Test | Committed baseline `filter` | Native renderer, traced by hand |
|---|---|---|
| `VectorSearch_with_bool_pre_filter` | `{ "is_published" : true }` | `RenderBareField` → `new BsonDocument(field.ElementName, true)` ⇒ `{ "is_published": true }` — **match** (READ, `MongoQueryLanguageRenderer.cs:204-210`) |
| `VectorSearch_with_complex_pre_filter` | `{ "$and" : [ { "comments" : "Froody" }, { "$or" : [ { "Pages" : { "$gt" : 500 } }, { "is_published" : true } ] } ] }` | `CombineAnd(left, right)`: `right`'s single key is `"$or"`, which **starts with `$`** ⇒ the mergeable branch is refused and `{ "$and": [left, right] }` is returned in operand order — **match** (READ, `MongoQueryLanguageRenderer.cs:474-503`) |

**INFERRED consequence: no MQL re-baselining anywhere in this slice.** Task 4's verification asserts that
directly (spec suite under default `Native`, zero baseline diffs) rather than trusting this trace.

The two **skipped** pre-filter tests (`…_on_nested_reference`) emit `parentFilter`, not `filter` (READ,
`VectorSearchMongoTest.cs:240,250`). They are skipped in both modes, are not part of the 112, and this slice
does not touch them.

**Decline rule.** The native translator's predicate set is narrower than the bridge's (no `ToUpper()`, no
date parts, …). A `preFilter` outside it makes `NativeSlotPopulator` call `MarkNotNativelyRepresentable()` — a
graceful fallback with correct results, throwing only under `NativeOnly`. That is the established disposition
for every other native decline in this area.

### 4.4 `limit: 0` — MEASURED, and the parity rule

**MEASURED this session, end-to-end through EF at `dcdfa7e2`** (§11, probe 2 — a standalone console app
referencing the freshly built `Debug EF10` provider assembly, pointed at an unreachable server so the control
proves the guard fires before any I/O):

| Query | Observed today (driver-LINQ path, default `Native`) |
|---|---|
| `VectorSearch(…, limit: 0, …)` | `System.Reflection.TargetInvocationException` → inner `System.ArgumentOutOfRangeException: Value is not greater than 0: 0. (Parameter 'limit')` |
| `VectorSearch(…, limit: -1, …)` | same, `…: -1. (Parameter 'limit')` |
| `VectorSearch(…, limit: 4, …)` *(control)* | reaches server selection ⇒ `TimeoutException` — so the guard above really does fire pre-I/O |
| `Take(0)` *(control, native path)* | `ArgumentOutOfRangeException: Take must be positive; got 0. (Parameter 'count')` — a **different** exception, from `MongoPipelineFactory.ValidatePagingStages` |

**The owner's decision is that the native path must match whatever driver-LINQ does today. It does, for free,
and here is the mechanism:** the exception originates inside `PipelineStageDefinitionBuilder.VectorSearch`
(MEASURED directly at the driver level too — probe 1) and is wrapped because `ProcessVectorSearch` reaches that
builder through `MethodInfo.Invoke` with default binding flags (READ,
`MongoEFToLinqTranslatingExpressionVisitor.cs:600-606`). `VectorSearchStageBuilder.CreateStage` keeps that
same `Invoke` with the same default flags, and **both** paths call it. Parity is structural.

**Two consequences that must be written into the implementation, because both are ways to accidentally break
it:**

1. Do **not** add `BindingFlags.DoNotWrapExceptions` to `CreateStage`'s `Invoke`. It would change today's
   observable exception on the *released* driver-LINQ path from `TargetInvocationException` to
   `ArgumentOutOfRangeException`. `VectorSearch(…, limit: 0)` is reachable at `v10.0.2`/`v9.1.2`/`v8.4.2`
   (`MongoQueryableExtensions` exists there unchanged — CITED handoff §7), so that *would* be observable to an
   upgrading consumer.
2. Do **not** extend `MongoPipelineFactory.ValidatePagingStages` to look inside a `$vectorSearch` body. That
   would produce the `Take(0)`-style `ArgumentOutOfRangeException("count")` instead, i.e. it would *create* a
   divergence where none exists.

Pinned by `NativeVectorSearchTests.Limit_zero_throws_identically_in_every_mode` (§9.3, test 12), asserting the
outer type **and** the inner type/message, across `Native`, `DriverLinq` and `NativeOnly`.

---

## 5. The IR, the slot, and the lowerer

### 5.1 `MongoVectorSearch` (the slot's payload)

```csharp
// Query/Expressions/MongoVectorSearch.cs   (new, internal)
internal sealed record MongoVectorSearch(
    LambdaExpression PropertyLambda,     // Arguments[1], unwrapped from its quote
    MongoExpression? PreFilter,          // translated at compile time from Arguments[2]; null when absent
    Expression QueryVectorArgument,      // Arguments[3] — a query parameter or a constant
    Expression LimitArgument,            // Arguments[4]
    Expression OptionsArgument);         // Arguments[5]
```

Holding the three raw argument nodes (rather than pre-extracted values) mirrors `ProcessVectorSearch`'s
`ParamValue<T>(index)` exactly, and resolves them at `Build` time via
`NativeQueryParameter.TryGetQueryParameterName` — the version-agnostic helper whose own doc comment says the
EF8/EF9-vs-EF10 split "is encapsulated here so the native translator's call sites stay version-agnostic"
(READ). **That is the whole of the zero-`#if` story** (CITED spike Q2.3, re-confirmed by READ).

Holding a `LambdaExpression` in the IR is consistent with what the IR already does elsewhere (the
`MongoUnwindSource`/binder family carry EF expression nodes), and it is required: the driver builder takes an
`Expression<Func<TDoc,TField>>` and derives the document path from it — which is how
`e => e.Preface.Floats` becomes `"Preface.Floats"` for free (READ of the current bridge; CITED for the
rendered path).

### 5.2 The slot on `MongoSelectDefinition`

```csharp
internal MongoVectorSearch? VectorSearch { get; set; }
```

**It must NOT join `HasTerminalOperator`.** `VectorSearch` is a root anchor, not a terminal: a `.Where`
composed after it must keep recording into `PipelineOps` normally (CITED spike Q4.1; and
`VectorSearch_floats_before_where` depends on it). `Route` is unaffected — a bare vector search stays
`NativeRoute.WholeEntity`; a vector search plus a bound `$project` stays `NativeRoute.Projection`.

### 5.3 Two stage IR types, and why the `$addFields` one carries no BSON

```csharp
internal sealed class MongoVectorSearchStage(MongoVectorSearch Search)  : MongoPipelineStage;
internal sealed class MongoVectorSearchScoreStage                       : MongoPipelineStage;  // no payload
```

`MongoVectorSearchScoreStage` is a **marker**: `MongoPipelineFactory` renders it to the fixed
`{ "$addFields": { "__score": { "$meta": "vectorSearchScore" } } }`. Giving it a generic
`MongoAddFieldsStage(BsonDocument)` payload would put BSON into the lowerer, violating the area's stated
"the lowerer is BSON-free" invariant (READ, `Query/AGENTS.md` pitfalls). It is emitted **unconditionally**, as
`ProcessVectorSearch` does today (READ, `AddScoreField`) — dropping it would force 70+ re-baselines, and the
spike MEASURED that `$meta` can also be read directly in a later `$project`, so the companion is a
baseline-parity choice, not a correctness one.

`MongoPipelineFactory.RenderStage`'s `switch` has **no default arm** — it throws
`NativeTranslationNotSupportedException` for an unknown stage — so a missing arm fails loudly rather than
silently dropping a stage (READ, `MongoPipelineFactory.cs:119-121`; CITED as exercised by the spike).

### 5.4 The lowerer insertion

```csharp
var select = query.Select;
var stages = new List<MongoPipelineStage>();

// 0. $vectorSearch MUST be the first stage in the pipeline (server rule, Location40602). It is a dedicated
//    slot rather than a PipelineOps entry precisely so first-ness is structural, not incidental.
if (select.VectorSearch is { } vectorSearch)
{
    stages.Add(new MongoVectorSearchStage(vectorSearch));
    stages.Add(new MongoVectorSearchScoreStage());
}

// 1. $match / $sort / $skip / $limit ops, emitted verbatim in arrival order …
AppendSelectOpStages(select.PipelineOps, stages);
```

That is the entire lowerer change (READ, insertion point `MongoSelectLowerer.cs:60-64`). Every terminal branch
below it (`SetOperation`, `UnwindSource`, `Grouping`, `Projection`, `Cardinality`) is untouched and still
appends after, so first-ness holds for every shape that reaches the lowerer.

**Shapes that cannot reach the lowerer with a vector search attached, and therefore need no guard** (READ):
`Union`/`Concat`/`Intersect`/`Except` operands are rejected by the QMTEV's `IsPlainWholeEntitySelect` /
`IsPlainProjectedSelect`, both of which already test the QMTEV's own `ContainsVectorSearch`;
`Distinct` is rejected by `TryBindDistinctFromProjection`'s `Projection.Count == 0` check for a whole-entity
source. Each of those declines gracefully to driver-LINQ, exactly as today. §9.2 records why that is the right
outcome and not merely the current one.

---

## 6. The two gates — opened together, by construction

### 6.1 What the gates are today

CITED (handoff §1.2, MEASURED by mutation) and re-confirmed by READ:

- **Gate 2, which fires first:** `NativeSlotPopulator.PopulateNativeSlots`' catch-all. `VectorSearch` is in
  `AllowedQueryableExtensions`, is in neither the `VisitMethodCall` switch nor
  `IsNativeRepresentableSlotOperator`, so `MarkNotNativelyRepresentable()` runs and `Route` is `Fallback`
  before the disposition is ever consulted.
- **Gate 1:** `ClassifyNativeDisposition`'s `route == NativeRoute.Fallback || containsVectorSearch`.
- **Opening both without emitting the stage returns silently wrong data** — the right row count, in insertion
  order instead of score order, no exception.

### 6.2 The change, and why it makes the bad state unreachable

`ClassifyNativeDisposition`'s third parameter changes meaning:

```csharp
// was:  bool containsVectorSearch
// now:  bool hasUnboundVectorSearch
private static NativeDisposition ClassifyNativeDisposition(MongoQueryExpression q, MongoQueryMode mode)
    => ClassifyNativeDisposition(
        q.Select.Route,
        q.Select.IsFallbackWrongData,
        ContainsVectorSearch(q.CapturedExpression) && q.Select.VectorSearch is null,   // ← the whole change
        mode);
```

The pure 4-argument overload's body is **unchanged**; only the meaning of the flag it receives changes.

This is not cosmetic. It converts "two gates that must be opened in step" into **one fact read twice**:

- Slot bound ⇒ `hasUnboundVectorSearch` is false ⇒ gate 1 open **and** gate 2 open **and** the lowerer has a
  slot to emit from. There is no ordering to get wrong.
- Slot not bound (the binder declined, or a future edit removes the branch, or the extraction/re-insertion
  changes shape so the call never reaches the populator) ⇒ `Route` is `Fallback` **and**
  `hasUnboundVectorSearch` is true ⇒ graceful driver-LINQ fallback. **The captured chain still carries the
  `VectorSearch`, so the fallback executes it correctly.**
- The dangerous state — native route, no stage emitted — requires `Select.VectorSearch is null` *and*
  `Route != Fallback`, which the populator branch makes contradictory (it either binds the slot or marks the
  query non-representable; there is no third exit).

**This also answers the sequencing requirement in a stronger form than sequencing does.** Task ordering (§8)
still puts the IR + lowerer before the gate so that no commit even *transiently* has a populated slot with no
lowering; but even after the slice ships, a later edit that deletes the lowerer block or the populator branch
degrades to a fallback rather than to wrong data.

### 6.3 `NativeSlotPopulator`

A new branch **before** the catch-all, keyed on the existing internal `methodCallExpression.IsVectorSearch()`
(there is no `QueryableMethods` constant for it, so `IsNativeRepresentableSlotOperator` — which takes only a
`MethodInfo` — cannot express it):

```csharp
else if (call.IsVectorSearch())
{
    if (!NativeVectorSearchBinder.TryBind(mongoQ, call))
        mongoQ.Select.MarkNotNativelyRepresentable();
}
```

`TryBind` declines (mutating nothing) when: a `preFilter` the native translator rejects; any of
`Arguments[3..5]` being neither a query parameter (`NativeQueryParameter.TryGetQueryParameterName`) nor a
`ConstantExpression`; or `Select.VectorSearch` already set (a second `VectorSearch` — impossible via the public
API, cheap to fail closed on).

`IsNativeRepresentableSlotOperator` is left alone **and a comment says why**: the area's "native catch-all
whitelist must stay in sync" pitfall exists so a new native operator is not silently clobbered by the
catch-all; here the explicit branch above it means the catch-all is never reached, and the whitelist has no
`MethodInfo` to hold. A reviewer will look for this; make the comment say it, at both sites.

### 6.4 `NativeDispositionTests` — what the two tests become

| Today | Becomes | Assertion |
|---|---|---|
| `Vector_search_is_fallback_even_when_route_is_native` | `Unbound_vector_search_is_fallback_even_when_route_is_native` | **unchanged** — `Classify(WholeEntity, hasUnboundVectorSearch: true) == Fallback`. It now pins the *silent-drop guard* rather than "vector search is never native", and the test comment must say that. |
| `Hard_decline_takes_precedence_over_vector_search` | `Hard_decline_takes_precedence_over_unbound_vector_search` | **unchanged** — `Classify(Fallback, isFallbackWrongData: true, hasUnboundVectorSearch: true, Native) == HardDecline`. |

The helper's parameter is renamed `containsVectorSearch` → `hasUnboundVectorSearch`. (Note the file already
carries a `TODO(CSHARP-6017)` block describing a future rename of the *other* parameter; this rename is
independent of it and should not be folded in.)

**One test is added, and its weakness is stated rather than hidden.** `Bound_vector_search_is_native` —
`Classify(WholeEntity, hasUnboundVectorSearch: false) == Native` — is *degenerate at the pure level*: it is
`WholeEntity_is_native` with an explicit `false`. It is worth having as documentation of intent, but it is not
the discriminator. The real discrimination for "bound ⇒ native" is:

- end-to-end, `NativeVectorSearchTests` under `MongoQueryMode.NativeOnly` **succeeding** (§9.3), and
- the **mutation** in Task 4's verification: force `TryBind` to `return false` and confirm those tests flip
  from pass to `NativeTranslationNotSupportedException` under `NativeOnly` while still returning **correct,
  score-ordered** data under `Native` (i.e. the fallback is intact).

---

## 7. `__score` — emit and read-back

### 7.1 Emit (settled)

CITED (spike Q3.3, MEASURED and executed):
`MongoAggregationExpressionRenderer.Render(new MongoElementRefExpression("__score", typeof(double)), …)`
renders to the string `"$__score"` — exactly the `$project` leaf value the committed baselines assert
(`{ "Author": "$Author", "Score": "$__score", "_id": 0 }`).

### 7.2 Read-back — the "one known unverified risk", RESOLVED BY READING

The handoff flags the DOM shaper read-back for an alias with no backing `IProperty` as **UNVERIFIED** and "the
piece most likely to surprise". It is not, and here is the whole chain at `dcdfa7e2` (all READ):

1. **Registration.** `MongoProjectionBindingExpressionVisitor.Visit` has a
   `MethodCallExpression when IsScalarMethodPropertyAccess(...)` case that stores the raw call in
   `_projectionMapping[member]` and returns a `ProjectionBindingExpression`
   (`MongoProjectionBindingExpressionVisitor.cs:183-188`). `IsScalarMethodPropertyAccess` returns **true** for
   `EF.Property` whose name is not a navigation, and **true** for any `Mql.Field` (`:1173-1194`). It is **not**
   gated on `Route`, so it fires identically on the native projection path.
2. **Alias.** `MongoQueryExpression.ApplyProjection` builds each `ProjectionExpression` with
   `projectionMember.Last?.Name` (`MongoQueryExpression.cs:98-112`) — the anonymous-type member name,
   `"Score"`. `NativeProjectionBinder` derives its `MongoProjection` alias from
   `newExpression.Members[i].Name` — **the same string**. The two alias spaces agree by construction, and
   `NativeProjectionBinder`'s existing case-insensitive `seenAliases` guard already declines the one shape
   (`AddToProjection`'s disambiguating counter) where they could diverge.
3. **Read.** `MongoProjectionBindingRemovingExpressionVisitor`'s `ProjectionBindingExpression` case calls
   `TryResolveFieldAccess(projection.Expression)`. That method has an explicit `EF.Property` arm **and** an
   explicit `Mql.Field` arm, and both return `Property: null` when the name resolves to no `IProperty`
   (`:696-731`) — which is exactly `__score`. With `Property == null` it falls to
   `BsonBinding.CreateGetElementValue(DocParameter, projection.Alias, bindingType)` (`:128-131`) — a raw read
   of the named element at the requested CLR type, via
   `BsonSerializerFactory.CreateTypeSerializer(typeof(double))` (`BsonBinding.cs:190-191, 258-…`).

**And this is not a new path.** It is the same branch already shipped and exercised, on the *native* route, by
the arithmetic computed leaf (`Query/AGENTS.md`: "the DOM shaper reads it back raw by alias; no emit-side or
read-back changes were needed"), by the owned-collection count leaf (whose `projection.Expression` is likewise
a `MethodCallExpression` with no backing property), and by the GroupBy flatten. `__score` differs from those
in nothing but the CLR type of the leaf, and `double` is already reachable through an arithmetic leaf.

**Residual, stated honestly.** What is *not* verified by execution is the composite: a vector query, natively
routed, whose `$project` contains a `$__score` leaf, materializing. That is what
`NativeVectorSearchTests` tests 6 and 7 (§9.3) exist to prove, asserting the **score values**, not merely
non-null. No separate probe is warranted; the functional test *is* the measurement, and it is cheap.

### 7.3 Emit side — `NativeProjectionBinder.TryTranslateLeaf`

A new branch, placed with the other non-`MemberExpression` leaf branches:

```csharp
// The synthetic vector-search score. Admitted ONLY when this query actually emits the
// $addFields{__score} companion — otherwise the alias would name an element nothing writes.
if (mongoQ.Select.VectorSearch is not null
    && TryRecognizeVectorScoreLeaf(leafExpression, outerParameter, out var scoreType))
{
    result = new MongoElementRefExpression(MongoVectorSearchScoreStage.ScoreField, scoreType);
    return true;
}
```

> **CORRECTION FOLDED BACK FROM TASK 5 — "rooted on the selector's own parameter" is not a raw reference
> comparison, and the design did not anticipate this.** An entity owning an eager-loaded navigation — every
> owned navigation is one by EF convention, and the specification suite's `Book` owns a `Preface` — has its
> auto-include injected around the very expression the projection reads through, so the `Mql.Field` spelling
> arrives as `Mql.Field(IncludeExpression(e, Preface), "__score", …)` while the `EF.Property` spelling arrives
> with a bare parameter. Comparing the raw receiver by reference therefore admitted ONE spelling and silently
> declined the other **on exactly the models that carry owned data — invisible to a fixture without owned
> data; only the specification suite caught it.** `IsSelectorParameter` peels `IncludeExpression` layers first
> (mirroring `TryGetWholeEntityMemberAccess`), which is safe because an include layer changes what is
> MATERIALIZED from the row, never which document the element is read out of.

`TryRecognizeVectorScoreLeaf` accepts exactly two spellings, both rooted on the selector's own parameter, both
naming the literal `"__score"`, and both typed `double` (or `double?`):

- `EF.Property<double>(param, "__score")`
- `Mql.Field<TDoc, double>(param, "__score", <any serializer constant>)`

Everything else — any other element name, any other CLR type, a non-parameter receiver — returns false and the
whole projection declines gracefully to driver-LINQ.

**Three guards, each load-bearing for a different reason:**

| Guard | Prevents |
|---|---|
| `Select.VectorSearch is not null` | A plain (non-vector) native query projecting `EF.Property<double>(e, "__score")` would emit an alias reading an element no stage writes. Today that shape falls back; with this guard it still does. |
| literal `"__score"` only | Keeps a *general* driver element-addressing capability out of the native projection binder. Admitting arbitrary `Mql.Field` names opens serializer-honouring and value-converter questions that belong to the step-3 projection long tail, not here (CITED spike Q3.4). |
| CLR type `double`/`double?` only | The native read-back **ignores** `Mql.Field`'s serializer argument and reads through `CreateTypeSerializer(type)`. `$meta: "vectorSearchScore"` always yields a BSON double, so `double` is exact; any other requested type could diverge from what the driver's own serializer would have produced. |

### 7.4 **THE DECISION THE OWNER SHOULD SEE — the `Mql.Field` ruling collides with the 8-cases ruling**

The brief states both as settled:

> **IN:** … the **8** score-projection cases (`VectorSearch_with_projection_of_score`, `…_using_EF_Property`,
> 4 each).
> … `Mql.Field` gets a **targeted decline**.

**MEASURED by reading the tests:** `VectorSearch_with_projection_of_score` is spelled
`new { e.Author, Score = Mql.Field(e, "__score", DoubleSerializer.Instance) }`
(`VectorSearchMongoTestBase.cs:438`). `…_using_EF_Property` is the `EF.Property` spelling (`:460`). So **4 of
the 8 in-scope cases are the `Mql.Field` spelling**. A blanket `Mql.Field` decline makes the stated target of
96/112 unreachable — it would be 92/112.

**How I reconciled it, and it is the reading under which both statements are true:** "targeted decline" =
decline `Mql.Field` *as a general element-addressing API*, admitting **only** the one literal synthetic element
this slice is about. That is also precisely what the spike itself proposed in the same breath as recommending
the decline: *"recognise **only** `EF.Property<double>(e, "__score")` and the literal element name `"__score"`
natively"* (spike Q3.4). §7.3 implements that.

**If the owner meant the stricter reading** — no `Mql.Field` recognition at all, in any form — then:
drop `Mql.Field` from `TryRecognizeVectorScoreLeaf`, the target becomes **92 of 112**, and the 20 remaining
failures are the 16 in §8 plus the 4 `VectorSearch_with_projection_of_score` cases. Nothing else in the design
changes. The spike also flagged (UNVERIFIED) that leaving two sibling tests with *different* dispositions may
read as arbitrary; under the reading I designed for, that asymmetry does not arise, which is a further reason
to prefer it.

> **RESOLVED — this is not an open question, and the reading above is the right one. Target is 96/112** — *and that FIGURE is stale, for an unrelated reason found later: the 4 `VectorSearch_with_complex_pre_filter` cases decline on predicate breadth, so the as-built figure is **92/112**. The RULING below (both `__score` spellings are in scope) is unaffected and stands; only the arithmetic moved. See the correction box above §8 Task 5.*
> The stricter reading was never on the table: the question the owner actually ruled on described `__score` as
> *"read both via the driver's `Mql.Field` and via `EF.Property<double>`"*, and the answer was "in scope". So
> ruling 2 already covers **both spellings** by construction. "Targeted decline" entered the record from spike
> Q3.4, where it means declining `Mql.Field` as a *general* element-addressing API — admitting the general API
> is a step-3 decision. Recognising exactly the literal `"__score"` element is inside ruling 2, not an
> exception to it. **Do not re-raise this; implement §7.3 as designed.**

---

## 8. Task breakdown — one subagent per task

Ordered so the "both gates open, no stage emitted" state is **never committed**, and so the
`AdditionalState`-missing window (which would be a `KeyNotFoundException` regression under the *default* mode)
never exists either.

> Every task's verification runs with **both `MONGODB_URI` and `ATLAS_URI` unset** (TestContainers boots
> `mongodb/mongodb-atlas-local`, so the Atlas-gated tests run for real), redirects `dotnet test` to a file
> (never `tail`/`head`), and uses its **own** uniquely-named scratchpad subdirectory.

### Task 1 — the deferred `Build`-time stage slot

`MongoNativeBuildContext`; `MongoPipelineFactory` slots become document-or-deferred; new
`Build(in MongoNativeBuildContext)`; existing `Build(parameterValues)` throws when a deferred slot exists.
No IR, no caller changes, **no behaviour change**.

*Verify:* new unit tests under `tests/.../UnitTests/Query/NativeTranslation/` using a **fake** deferred slot —
(a) the deferred document lands at its stage position; (b) sentinels *inside* the deferred document are
substituted by the same pass; (c) the old overload throws when a deferred slot is present; (d)
`ValidatePagingStages` still ignores a `limit` nested one level down. Full solution green on EF10.
**Mutation:** make the deferred slot's output bypass `SubstituteDocument` and confirm (b) goes red.

### Task 2 — extract `VectorSearchStageBuilder`; bridge refactored to use it

`Resolve` / `CreateStage` / `RenderStage`; `ProcessVectorSearch` becomes a thin caller. The reflection boundary
and the guard ordering are preserved **exactly** (§4.2). No native use yet.

*Verify:* the whole point of this task is **zero observable change**, so verify it as such —
`dotnet test … --filter "FullyQualifiedName~VectorSearch"` on the EF10 spec suite under **default `Native`**:
`114 passed / 0 failed / 4 skipped`, and `git diff` shows **no** change to any `AssertMql` baseline.
Re-run the §11 probe-2 `limit: 0` measurement before and after and require byte-identical output
(`TargetInvocationException` → `ArgumentOutOfRangeException("limit")`). Full solution green on EF8, EF9, EF10.
**Mutation:** move the `Exact`+`NumberOfCandidates` guard inside `CreateStage`'s reflection boundary and confirm
`VectorSearch_throws_if_num_candidates_set_for_exact_search` goes red — that is the proof the split is
load-bearing and not decoration.

### Task 3 — IR, lowerer, render arms, `AdditionalState` plumbing (all still unreachable)

`MongoVectorSearch`; `MongoSelectDefinition.VectorSearch` (**not** in `HasTerminalOperator`);
`MongoVectorSearchStage` + `MongoVectorSearchScoreStage`; the two `MongoPipelineFactory` arms (vector ⇒
deferred slot closing over the compile-time-rendered pre-filter; score ⇒ the constant document); the lowerer
block ahead of `AppendSelectOpStages`; `TranslateQuery` switched to the context `Build` overload and threading
the filled `AdditionalState` dictionary into `MongoExecutableQuery`.

**Nothing populates the slot yet, so behaviour is unchanged.** Putting the diagnostics plumbing *here* rather
than after Task 4 is the point: fold it later and there is a window in which a natively-routed zero-result
vector query throws `KeyNotFoundException` under the **default** mode.

*Verify:* unit tests that hand-build a `MongoSelectDefinition` with a `VectorSearch` slot plus a `Where` and
assert the lowered stage sequence — `$vectorSearch`, `$addFields`, `$match`, in that order — and that the
rendered `$vectorSearch` body is byte-equal to the committed baseline for `VectorSearch_floats`. Spec suite
unchanged on **both** axes (default `Native` and `MONGODB_EF_NATIVE_ONLY=1`) — the `NativeOnly` VectorSearch
failure count must still be **112**, because nothing has opened yet. Full solution green ×3.
**Mutation:** move the vector block to *after* `AppendSelectOpStages` and confirm the stage-order unit test
goes red.

### Task 4 — open both gates (the first behaviour change), plus the diagnostics proof

`NativeVectorSearchBinder.TryBind` + the `NativeSlotPopulator` branch; `ClassifyNativeDisposition`'s
`containsVectorSearch` → `hasUnboundVectorSearch`; `TryBuildNativeFactory`'s stale vector-search comment
rewritten; `NativeDispositionTests` renames + the added test (§6.4). First slice of
`NativeVectorSearchTests` (§9.3 tests 1–5, 9–13, 16).

*Verify:* EF10 spec `--filter "FullyQualifiedName~VectorSearch"` under `MONGODB_EF_NATIVE_ONLY=1`:
**112 → 24 failures**, and the 24 must be exactly the projection bucket, compared **by failing-test-name SET**
against a base worktree, not by count. Default `Native`: still `114 passed / 0 failed / 4 skipped` with **zero
baseline diffs**. Full solution green ×3.
**Mutations, all required:** (a) force `TryBind` to `return false` ⇒ the `NativeOnly` tests flip to
`NativeTranslationNotSupportedException` while the `Native` tests still return correct, score-ordered rows;
(b) delete the `&& q.Select.VectorSearch is null` conjunct ⇒ nothing should change (it is currently
equivalent), which is *expected* and must be **stated, not hidden** — the conjunct's teeth are mutation (c);
(c) delete the lowerer's vector block while leaving the populator branch ⇒ the tests must fail **loudly**
(row-order assertions), never pass; this is the silent-wrong-data tripwire.

> **NUMBERS CORRECTED after Task 4 (measured, `2e2e65cc`).** Task 4's residual is **28, not 24**:
> the 24-test projection bucket **plus** `VectorSearch_with_complex_pre_filter` ×4. That pre-filter is
> `arrayField.Contains(constant)`, which `MongoExpressionTranslator` does not support — §4.3 traced the
> *renderer* and concluded byte-identity, but **translation declines before the renderer is ever reached**, so
> the trace was correct and irrelevant. The decline is graceful (correct rows under default `Native`; throws
> only under `NativeOnly`) and is pinned by `NativeVectorSearchTests` test 16. Widening the translator is a
> cross-cutting predicate-breadth change that would flip routing for **every** non-vector query using that
> shape, so it is explicitly **not** this slice's work.
>
> **Consequences: Task 5 goes 28 → 20, not 24 → 16. The slice's final target is 92 of 112, not 96.** The 20
> residual is the 16 of §8 plus these 4. Every other number in this document that says 96, 24 or 16 is stale
> in exactly this way.

### Task 5 — the `__score` projection leaf (the 8)

`TryRecognizeVectorScoreLeaf` + the `NativeProjectionBinder.TryTranslateLeaf` branch, with the three guards of
§7.3. `NativeVectorSearchTests` tests 6, 7, 14, 15.

*Verify:* `MONGODB_EF_NATIVE_ONLY=1` VectorSearch filter: **28 → 20 failures** (corrected; was written as
24 → 16), the 20 being §9's 16 plus the 4 `complex_pre_filter` cases, by name set. Default `Native` unchanged, no baseline diffs. Full solution green ×3.
**Mutations:** (a) drop the `Select.VectorSearch is not null` guard ⇒ test 15 goes red; (b) widen the element
name from the `"__score"` literal to any string ⇒ test 14 goes red; (c) drop the `double` type restriction ⇒
add a case in test 14 that goes red.

### Task 6 — sweeps, documentation, break check, and the hazard note

Remaining `NativeVectorSearchTests` (test 8, the streaming `__score`-skip); the `Query/AGENTS.md` as-built note
including the **set-op dedup hazard** (§9.2); `docs/native-query-status-EF-322.md` §4 ("Not native at all:
`VectorSearch`" — no longer true), §5, §9.8 step 2 and the gate table's row 5; the release-tag break check
(§10).

*Verify:* full EF10 spec suite (not just the vector filter) on both axes versus a base worktree, compared by
failing-test-name **set**. `git diff <base> HEAD -- src/ | grep -c '^[+-].*#if'` ⇒ **0**. Full solution green
on EF8, EF9, EF10. `gh release list` → diff each touched public member against the highest non-preview tag on
each line.

---

## 9. Test plan

### 9.1 The reviewer's escalation list — addressed up front

`.claude/agents/vector-search-reviewer.md` escalates on *"Change to the preprocessor extraction/re-insertion of
`VectorSearch`"*. **This design does not change `MongoQueryTranslationPreprocessor` at all.** The lift-before-
nav-expansion / re-insert-after dance, `VectorSearchExtractor`, `VectorSearchReplacer` and their ordering
inside `Process` are untouched (READ of the whole file; the design depends on the re-insertion happening
*before* the QMTEV runs, which it does). The as-built note in `Query/AGENTS.md` must say so in as many words,
so the reviewer can confirm it without re-deriving. The reviewer's other standing items — Atlas gating,
call-site shape, index-creation sync/async parity, `VectorIndexOptions` fields, `BinaryVectorDataType`,
`WaitForVectorIndexes` — are all untouched by this slice; binary vectors work because the *driver's* own
`QueryVector` serialization produces subtype `09` and we reuse it (CITED spike Q2.1, which also MEASURED that a
hand-renderer **cannot**: `BsonValue.Create(QueryVector)` throws).

### 9.2 The hazard note for the future (Task 6 writes this into `Query/AGENTS.md`)

> **Any native shape that de-duplicates by whole document destroys vector-score ordering.** MEASURED by the
> Task-0 spike against a live index: `$group{_id:"$$ROOT"}` + `$replaceRoot` — the `Union`/`Distinct` dedup
> shape — returned `D, C, A, B` where score order is `A, B, C, D`. With the `$addFields{__score}` companion
> present the score is itself part of `$$ROOT`, so it also becomes part of the dedup key and two otherwise-
> identical documents with different scores would not dedup. This slice is not exposed to it — a set operation
> or a whole-entity `Distinct` composed over a `VectorSearch` is declined upstream by the QMTEV's own
> `ContainsVectorSearch` (via `IsPlainWholeEntitySelect`/`IsPlainProjectedSelect`) and by
> `TryBindDistinctFromProjection`'s `Projection.Count == 0` check, so both fall back to driver-LINQ, which is
> their current and working disposition. **Do not "widen" either of those gates for vector search without a
> row-ORDER test.** Row count will not discriminate.

### 9.3 `tests/.../FunctionalTests/Query/NativeVectorSearchTests.cs`

The spike CITED that there are **zero** functional tests for vector *search* — the spec suite is the entire
behavioural oracle. Every other slice on this branch added a `Native*` functional file; this one does too.

Shape: `[XUnitCollection("QueryTests")]`, `IClassFixture<AtlasTemporaryDatabaseFixture>`, `[AtlasFact]` /
`[AtlasTheory]` (the same gating `Storage/IndexTests.cs` uses — READ). A small `Doc` entity with a 2-dim
`float[]` vector, a `bool` flag and a `string` label, `HasIndex(…, "VecIndex").IsVectorIndex(Cosine, 2)`, and
`EnsureCreated` (which creates **and waits for** vector indexes by default — CITED handoff §4).

**Seeding rule, and it is the single most important design constraint on this file: insert the documents in an
order that is NOT score order.** The handoff MEASURED that opening both gates without emitting the stage
returns the **right row count in insertion order**. A seed whose insertion order happens to match score order
makes every test in this file vacuous.

| # | Test | Pins | How "goes native" is proven |
|---|---|---|---|
| 1 | `Bare_vector_search_returns_score_order` | the exact **ordered** labels, plus an explicit assertion that this order differs from the insertion order | `[AtlasTheory]` over `Native` **and** `NativeOnly`; the `NativeOnly` leg **succeeding** is the proof |
| 2 | `Pre_filter_restricts_and_preserves_score_order` | ordered labels of the filtered subset | same |
| 3 | `Exact_search_returns_score_order` (ENN) | ordered labels | same |
| 4 | `Num_candidates_returns_score_order` | ordered labels | same |
| 5 | `Where_after_vector_search_preserves_score_order` | ordered labels | same |
| 6 | `Score_projection_via_EF_Property_goes_native` | the **score values** (descending, `> 0`, and the top score's label) | `NativeOnly` succeeds |
| 7 | `Score_projection_via_Mql_Field_goes_native` | same as 6 | `NativeOnly` succeeds |
| 8 | `Whole_entity_vector_search_streams_and_skips_the_score_field` | `StreamingEligibility.IsEligible(entityType)` is asserted **directly** (premise, not assumed), then the entity's own scalars materialize correctly under `NativeOnly` | `NativeOnly` succeeds — see §9.4 |
| 9 | `Zero_results_logs_the_diagnostic_natively` | the `VectorSearchReturnedZeroResults` message, via `ConfigureWarnings(Throw)` | `NativeOnly` |
| 10 | `Unmatched_index_name_warns_natively` | `VectorSearchNeedsIndex` (already `Throw` by default) | `NativeOnly` |
| 11 | `Exact_with_num_candidates_throws_InvalidOperationException_in_every_mode` | exception **type** + message fragment, under `Native`, `DriverLinq`, `NativeOnly` | n/a — a parity pin |
| 12 | `Limit_zero_throws_identically_in_every_mode` | outer `TargetInvocationException` **and** inner `ArgumentOutOfRangeException` type + `"limit"` param name, under all three modes (§4.4) | n/a — a parity pin |
| 13 | `Vector_search_emits_the_stage_first` | MQL: `$vectorSearch` is stage 0 and `$addFields` stage 1 | **captioned as NOT a routing proof** — see §9.5 |
| 14 | `Mql_Field_for_a_non_score_element_declines` | correct values under `Native`; `NativeTranslationNotSupportedException` under `NativeOnly` | the decline. **CORRECTION FOLDED BACK FROM TASK 5: the non-score element must be DOUBLE-typed** (the fixture grew a real stored `Weight` for this) — declining `Mql.Field(e, "SomeString", …)` proves nothing, because the CLR-TYPE guard would have declined it too. Only a double-typed, really-stored, parameter-rooted element makes the element NAME the sole reason for the decline. The test's second half is the mirror image (`EF.Property<float>(e, "__score")` — name admitted, type the sole decline) |
| 15 | `Score_leaf_without_a_vector_search_declines` | `EF.Property<double>(e, "__score")` on a plain query: unchanged disposition | the guard |
| 16 | `Untranslatable_pre_filter_falls_back_with_correct_rows` | correct rows under `Native`, throws under `NativeOnly` | the graceful decline |

**Every data assertion pins order or score. None pins row count alone.** Where a count is asserted it is
alongside the ordered labels, never instead of them.

### 9.4 The one genuinely open execution risk: streaming + the extra `__score` element — **RESOLVED, it passed first time**

> **MEASURED in Task 6: test 8 PASSED on its first run, on EF8, EF9 and EF10. The pre-designed
> `allowStreaming: false` fallback was NOT applied and does NOT exist in the shipped code.** The test asserts
> `StreamingEligibility.IsEligible` directly as a premise and then every mapped scalar plus the owned
> sub-document; the fixture's `VectorDoc` grew an `OwnsOne Meta` precisely so it mirrors the spec suite's
> `Book`/`Preface` shape. It is **mutation-verified**: replacing `BuildFillLoop`'s `reader.SkipValue()` base
> case with a no-op (leaving the reader positioned at the unconsumed value) turns both of its cases red, along
> with 11 other cases in the file — so the test genuinely exercises the skip rather than passing vacuously.

The spec fixture's `Book` owns a `Preface` (a single owned reference), which since EF-322 Task 2 makes a
whole-entity query native **and streaming-eligible** (READ, `StreamingEligibility.IsEligible`). So the 70
bare-call cases will materialize through the **one-pass streaming materializer**, over documents carrying an
extra top-level `__score` element that the entity model does not know about.

CITED (spike Q3.3, from source): `MongoStreamingEntityMaterializerRewriter.BuildFillLoop`'s name-dispatch
if-chain has `reader.SkipValue()` as its base case, so an unrecognised top-level element is skipped rather than
mis-read. **UNVERIFIED by execution** — nothing goes native today, so it could not be run.

Test 8 is the measurement. **If it fails**, the fallback is narrow and pre-designed: pass
`allowStreaming: false` for a query whose `Select.VectorSearch is not null` at the `CompileShapedQuery` call
site in `VisitShapedQuery`, routing vector queries to the native DOM shaper — a one-line change costing the
streaming allocation win for this shape only, with no correctness impact. Do **not** discover this during the
sweep; run test 8 first in Task 6.

### 9.5 "MQL shape cannot prove a query went native" — the inference dies in Task 4

Today, `$vectorSearch` in captured MQL is a *reliable fallback signal*, because only `ProcessVectorSearch` can
emit it. **The moment Task 4 lands, that inference is dead**: both paths emit a structurally identical stage
(by design — that is what buys zero re-baselining), so an `AssertMql` baseline containing `$vectorSearch`
proves nothing about which path ran.

Therefore, in this slice:

- **Every routing claim is a `MongoQueryMode.NativeOnly` run that succeeds** (or, for a decline, one that
  throws `NativeTranslationNotSupportedException`). Never an MQL match.
- Test 13 exists to pin the **stage order** (a server constraint), and its comment must say in as many words
  that it is *not* a routing proof.
- The spec-suite comparison is by **failing-test-name set** under `MONGODB_EF_NATIVE_ONLY=1`, against a base
  worktree — the sanctioned signal, and the only one.
- This is the same thing that happened to reference `Include` after EF-370; follow that precedent rather than
  inventing one.

---

## 10. Breaking changes

> **TASK 6 OUTCOME — VERIFIED NONE; no `BREAKING-CHANGES.md` entry is warranted.** Baselines re-confirmed
> with `gh release list`: latest overall `v10.0.2`; latest per line `v10.0.2` / `v9.1.2` / `v8.4.2`. Of the 15
> `src/` files the slice touches, **13 do not exist at any of the three tags** (every new IR/stage/binder/
> builder file, plus `MongoPipelineFactory`, `MongoSelectDefinition`, `MongoSelectLowerer`,
> `NativeProjectionBinder`, `NativeSlotPopulator`, `MongoQueryLanguageRenderer`, `PlaceholderTable`). The two
> that do — `MongoEFToLinqTranslatingExpressionVisitor.cs` and `MongoShapedQueryCompilingExpressionVisitor.cs`
> — declare `internal sealed class` **at each tag and at HEAD alike**, so every `public` member inside them is
> a member of an internal type and is not public surface (rubric: internal is never public surface, regardless
> of `InternalsVisibleTo`). Of the members §10 lists below: `MongoQueryableExtensions` is **byte-identical**
> to all three tags; `Metadata/VectorQueryOptions.cs` differs from the tags by **one doc-comment word**
> (pre-existing on the branch, not this slice, record signature unchanged); `MongoExecutableQuery`'s deltas vs
> the tags are all `internal` members and all predate this slice; `MongoEventId`'s deltas vs the tags are five
> pure ADDITIONS unrelated to vector search, and there is **no** `VectorSearch`-related diagnostics delta at
> all. `Infrastructure/MongoQueryMode.cs` is absent at all three tags (`git ls-tree`), so on a released
> package vector search only ever ran on driver-LINQ — every mode-dependent statement in this design is
> vacuous there. Finally the rubric explicitly carves out both "which internal execution path a supported LINQ
> query takes" and "the exact emitted MQL", and results are unchanged (measured: zero spec delta on both axes,
> zero `AssertMql` baseline diffs).

**Expected: none, and no `BREAKING-CHANGES.md` entry — but verify per member against the release TAGS in
Task 6, do not take it from here.** Baselines: `v10.0.2` / `v9.1.2` / `v8.4.2` (CITED handoff §7, which
verified them with `gh release list`; re-verify, tags in a clone go stale).

The reasoning, and what has to be checked:

- `Infrastructure/MongoQueryMode.cs` **does not exist at any of the three tags** (CITED, `git ls-tree`). So at
  the published baseline vector search runs on driver-LINQ, always. Every mode-dependent statement in this
  design is vacuous there, and "which internal execution path a supported LINQ query takes" plus "the exact
  emitted MQL" are both explicitly carved out of the break rubric (root `AGENTS.md`).
- **The one thing that could be a real break is not the native path at all — it is Task 2's refactor of
  `ProcessVectorSearch`.** That path *does* exist at the tags. Task 2's verification (zero spec delta under
  default `Native`, zero baseline diffs, byte-identical `limit: 0` probe output) is exactly the evidence that
  it is behaviour-preserving. §4.4's two "do not" rules are the specific ways it could stop being.
- Members to diff against the tag, per EF line: `MongoQueryableExtensions.VectorSearch` (both public
  overloads), `VectorQueryOptions`, `MongoExecutableQuery` (public record — this slice adds no member and
  changes no signature; only the *contents* of `AdditionalState` on a path that did not exist at the tag),
  `MongoEventId.VectorSearchNeedsIndex` / `VectorSearchReturnedZeroResults` and their logging definitions.
- Everything else this slice touches is `internal` — and per the rubric, `internal` is never part of the
  public surface regardless of `InternalsVisibleTo`.
- No `Mongo:`-prefixed annotation key changes; no stored document-shape change (the emitted `$vectorSearch`
  body is byte-identical to today's by construction, §3.1/§4.3).
- The exception type for `limit: 0` is preserved deliberately (§4.4) even though it is arguably an unsupported
  input whose exception type is not contract. Preserving it is free; changing it is not clearly free.

---

## 11. Reproduction — what I actually ran

All probes this session were **outside the repo**, in
`<scratchpad>/vs-design-task1/`, as standalone console projects. **No `src/` file was modified, no worktree was
created** (`git worktree list` shows only the three pre-existing `agent-*` worktrees belonging to other
sessions), and nothing was committed.

```bash
# ── Probe 1: driver-level, MongoDB.Driver 3.10.0 only, no EF, no server ───────────────────────
#   PipelineStageDefinitionBuilder.VectorSearch(...).Render(new RenderArgs<Book>(serializer, registry)).Document
dotnet run -c Release     # <scratchpad>/vs-design-task1/probe/
```

| Input | Result |
|---|---|
| `limit: 4`, `IndexName` only | `{ "$vectorSearch": { "path": "Floats", "limit": 4, "numCandidates": 40, "index": "FloatsIndex", "queryVector": [1.0, 0.0] } }` |
| `limit: 3`, `IndexName` only | `… "limit": 3, "numCandidates": 30 …` ⇒ **`numCandidates = limit * 10`** when unspecified |
| `Exact = true` | `… "limit": 4, "index": …, "exact": true, "queryVector": … }` (no `numCandidates`) |
| `Exact = true` **+** `NumberOfCandidates` | `ArgumentException: Number of candidates must be omitted for exact nearest neighbor search (ENN).` |
| `Filter = BsonDocumentFilterDefinition<Book>({is_published: true})` | `… "index": …, "filter": { "is_published": true }, "queryVector": … }` |
| `Filter = ExpressionFilterDefinition<Book>(b => b.IsPublished)` | same position, `{ "IsPublished": true }` (default class map — the provider supplies its own entity serializer, hence `is_published`) |
| `limit: 0` / `limit: -1` | `ArgumentOutOfRangeException: Value is not greater than 0: 0. (Parameter 'limit')` |

```bash
# ── Probe 2: end-to-end through EF, against the freshly built provider assembly ───────────────
dotnet build src/MongoDB.EntityFrameworkCore/MongoDB.EntityFrameworkCore.csproj -c "Debug EF10"
dotnet run -c Release     # <scratchpad>/vs-design-task1/probe2/  — references
                          #   src/.../bin/Debug EF10/net10.0/MongoDB.EntityFrameworkCore.dll
                          #   + Microsoft.EntityFrameworkCore 10.0.8 + MongoDB.Driver 3.10.0
                          # UseMongoDB("mongodb://127.0.0.1:1/?serverSelectionTimeoutMS=300", …)
```

```
VectorSearch limit=0:  System.Reflection.TargetInvocationException: Exception has been thrown by the target of an invocation.
    inner: System.ArgumentOutOfRangeException: Value is not greater than 0: 0. (Parameter 'limit')
VectorSearch limit=-1: System.Reflection.TargetInvocationException: …
    inner: System.ArgumentOutOfRangeException: Value is not greater than 0: -1. (Parameter 'limit')
VectorSearch limit=4 (control): System.TimeoutException: A timeout occurred after 303ms selecting a server …
Take(0) control: System.ArgumentOutOfRangeException: Take must be positive; got 0. (Parameter 'count')
```

The `limit: 4` control reaching server selection is what proves the `limit: 0` guard fires at translation time,
before any I/O — i.e. the measurement is of the guard, not of an unrelated connection failure.

**Everything else in this document is READ or CITED, and tagged as such.** In particular I did **not** run the
spec suite this session; every `112 / 82 / 24 / 6` figure comes from the spike's measurement and is used, not
re-derived. Each task's verification step re-measures the number it depends on.

---

## Appendix — the 16 that remain failing, and why

All 16 are in the 24-case "Query projects a non-entity result" bucket, and all 16 are the **step 3**
bare-projection boundary rather than anything vector-specific.

| Test method | Cases | Projection | Why it stays out |
|---|---:|---|---|
| `VectorSearch_with_projection` | 4 | `.Select(e => e.Author)` | A **bare-scalar** selector body never populates `Select.Projection` at all — `NativeProjectionBinder.TryPopulateNativeProjection` accepts only a `NewExpression`/`MemberInitExpression` (READ, `NativeProjectionBinder.cs:58-98`). This is the SP3-wide bare-projection boundary that likewise keeps `Select(b => b.Posts.Count)` and `Select(b => b.Posts)` on the fallback path. Lifting it is one structural change covering bare scalars, bare entities and bare arrays together — status-doc §9.8 step 3. |
| `VectorSearch_with_projection_of_entity_and_score` | 4 | `new { Book = e, Score = Mql.Field(…) }` | The `Book = e` leaf is a bare `ParameterExpression`. `TryTranslateLeaf` has no branch for a whole-entity leaf, so the **whole** projection declines. (READ; and `Query/AGENTS.md` records the mixed-whole-entity-plus-computed combination as a separate pre-existing carried gap.) |
| `…_of_entity_and_score_using_EF_Property` | 4 | `new { Book = e, Score = EF.Property<double>(…) }` | Same — the entity leaf, not the score leaf. |
| `VectorSearch_with_projection_of_constructed_entity_and_score` | 4 | `new { Book = new Book { … }, Score = … }` | An entity-constructing `MemberInitExpression` **nested inside** an anonymous member. `TryTranslateLeaf` rejects it; only a *top-level* `MemberInit` is a recognized projection body. |
| **total** | **16** | | |

**They are not blocked by the score leaf.** Three of the four methods contain a `__score` leaf that this slice
*does* make representable; what declines them is the entity/bare leaf sitting beside it. So Task 5's work is
not wasted on them — it is simply not sufficient, and it will not need revisiting when step 3 lifts the
boundary.

**And `.Where` is not the blocker for any of the 112.** All 24 of these compose the same
`Where(e => e.Title.Contains("Action") || e.Title.Contains("DbContext"))`, a shape the native translator
already handles (CITED spike Q3.2). Row counts: 82 (bare + `preFilter` + `.Where`) + 6 (diagnostics/exception
shape) + 8 (score projections) = **96**; 112 − 96 = **16**.
