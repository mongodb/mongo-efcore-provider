# EF Core Native LINQ: Guide for reviewers

## 1. The 60-second version

An EF Core provider turns a LINQ query into a MongoDB **aggregation pipeline** (a list of stages such as `$match`, `$sort`, `$limit`) and turns the returned BSON document data back into C# objects.

Previously, the provider handed the LINQ expression to the **MongoDB C# driver's LINQ provider** for execution. This work adds the **native translator**, which builds the BSON query itself. The driver path remains as an automatic fallback for anything native cannot represent.

```
LINQ query
│
▼
 EF Core frontend
(navigation expansion, parameter extraction)
│
▼
 Provider uses:
IR (MongoSelectDefinition)
EF LINQ expression tree
│
▼
Native/Driver Gate
│
----------------
│             │
▼             ▼
Native      Fallback
│             │
▼             ▼
     IR         Driver LINQ
│             │
▼             │
Pipeline stages     │     .
│             │
▼             │
BSON template       │     .
│             │
▼             ▼
   Execute query    Execute query  .
│             │
▼             ▼
              Stream results  (Sometimes BSON)          .
│             │
----------------
│
Objects
```

Three ideas carry most of the design:

1. **Decline, never guess.** Every native step either succeeds exactly or *declines*, and a declined query falls back. The dangerous bug is not a crash but a native translation that accepts a shape it mishandles and returns **silently wrong rows**.
2. **Compile once, bind per execution.** The pipeline is rendered once into a **BSON** ***template*** with placeholder sentinels. Each execution clones it and substitutes parameter values.
3. **The write side and the read side must agree.** The pipeline *emits* documents with particular field names and shapes; a separately built *shaper* *reads* them. A disagreement yields nulls or empty collections, not errors.

---

## 2. Motivation and principles

### 2.1 Motivations

| **Motivation** | **Evidence** |
|---|---|
| **Capability.** The driver's LINQ provider cannot express some EF shapes: cross-collection joins, collection or multi-hop `$lookup`, Include, owned-array projections. | `MongoQueryExpression.Lookup.cs` |
| **C# semantics.** The driver sometimes answers a different question than the C# expression asks. | `See BREAKING-CHANGES.md`: a dropped narrowing cast, `null` vs missing in projections, derived-type queries reading sibling types. The native path deliberately returns the C#-correct answer. |
| **Performance.** The fallback re-runs bridge translation plus the driver's translation on every execution, and materializes through an intermediate DOM. | Fallback translation happens in `TranslateQuery` per execution. Native builds once and has a one-pass *streaming* materializer. `benchmarks/.../perf-baseline.md` measures EF against driver-only. |
| **Control.** Provider-specific features (`$vectorSearch`, exact MQL shape) are easier to own. | `NativeVectorSearchBinder`, `MongoQueryMode`. |

Where compatibility with established behavior matters more than purity, the code keeps the driver's behavior. Example: "driver-LINQ is the oracle" for how a malformed document reads back (`AGENTS.md`, "Read modes").

### 2.2 Constraints that shaped the design

- **EF Core owns the front end.** Navigation expansion, parameter extraction, tracking and materialization are EF's. The provider plugs in at fixed seams (section 3.1), so native is built *beside* EF's shaper machinery, not instead of it.
- **EF normalizes shapes before the provider sees them.** A user `Join`, an `Include` and a `SelectMany` all arrive as a `TransparentIdentifier(Outer, Inner)` result selector, often indistinguishable from each other. The provider therefore uses *candidate then confirm* handshakes (section 7.3).
- **Whether a query is native is only known at the end.** Any later operator can decline. So every operator both records itself into the native IR *and* leaves the EF-shaped query and captured chain intact for the fallback.
- **Correctness failures are silent.** The server accepts an almost-right pipeline. Hence the "stage locally, commit only after every gate passes" rule, the structural-classification rules and the exhaustive-dispatch tests (section 9).
- **Three EF versions ship in parallel** (EF8, EF9, EF10, via `#if EF8 || EF9` / `#if !EF8`). EF's own rewrites differ across versions in places, for example the GroupJoin composite-key guard and the LeftJoin shim.
- **Native is not a break.** Native becoming the default is explicitly *not* a break: results are unchanged, unsupported shapes fall back, and `UseQueryMode(MongoQueryMode.DriverLinq)` restores the old path (`AGENTS.md`, "Versioning").

### 2.3 Design principles you will see everywhere

1. **A dialect-neutral IR.** `MongoSelectDefinition` and `MongoExpression` nodes contain no BSON. A *lowerer* produces typed stages (still BSON-free), and only the *renderer/factory* emits BSON. This keeps translation logic testable without a server and keeps the layering enforceable (the lowerer has no `MongoDB.Bson` using).
2. **Recorded ops, not a rewritten tree.** Filter, sort and paging are appended to ordered op lists in arrival order instead of being merged into a normal form. Arrival order is emission order, which is how non-canonical sequences like `Skip().Take().Where()` stay correct.
3. **Default-deny.** The route is derived from the IR state. Unconfirmed candidates and unsupported operators force `Fallback`. Nothing becomes native by omission.
4. **Shared predicates, not restated ones.** Where emit and read sides, or gate and fix, must agree, both call the *same* predicate (`TryMatchOwnedArraySelectLeaf`, `StoredOrdering.PreservesClrOrdering`, `LookupExpression.IsJoinWithNativeOnlyInnerFilter`). A gate that restates a condition drifts, and drift means wrong rows.
5. **Compile-time vs run-time split.** Constants are baked into the template. Parameters become placeholders. Clock members (`DateTime.UtcNow`) and non-deterministic calls are never baked (section 8).

---

## 3. The system at a glance

### 3.1 Phases and where the native code sits

EF Core compiles a query in four phases, each customized through a factory (`Query/Factories/`). Steps marked "native work" hold the native code. Some work happens at compile time, once per query shape, and is cached by EF:

```
 1 Preprocess
   MongoQueryTranslationPreprocessor
   hoist, fold and narrow; lift VectorSearch out around navigation expansion
     │
     ▼
 2 Translate  (native work)
   MongoQueryableMethodTranslatingExpressionVisitor (QMTEV)
   per operator, NativeSlotPopulator and the Native*Binders fill the IR
   (MongoSelectDefinition); the captured expression chain is kept for the fallback
     │
     ▼
 3 Postprocess
   MongoQueryTranslationPostprocessor
   ApplyProjection, DateTimeKind check
     │
     ▼
 4 Compile  (native work)
   MongoShapedQueryCompilingExpressionVisitor (MSQCEV)
   gate: ClassifyNativeDisposition
     │
     ├─► native:   MongoSelectLowerer → stages → MongoPipelineFactory.Create → template,
     │             plus a compiled shaper (DOM reader or one-pass streaming reader)
     │
     └─► fallback: compiled shaper for the driver path
     │
     ▼
   Result: an expression that calls ExecuteShapedQuery(...)
```

Other work must be done on each query execution:

```
 ExecuteShapedQuery(...)
     │
     ▼
 TranslateQuery
     │
     ├─► native:   factory.Build(parameters) → BsonDocument[]
     │
     └─► fallback: bridge → driver LINQ provider
     │
     ▼
 MongoClientWrapper.Execute → collection.Aggregate
     │
     ▼
 QueryingEnumerable → shaper → objects
```

### 3.2 The three layers inside the native path

| **Layer** | **What it holds** | **Key types** | **Knows about BSON?** |
|---|---|---|---|
| **Emit side: IR** | A logical query: ordered ops, projection, grouping, joins, terminal operator. | `MongoQueryExpression`, `MongoSelectDefinition`, `MongoExpression` subclasses (`Query/Expressions/`) | No |
| **Emit side: lowering and rendering** | Typed stages, then a BSON template with placeholders. | `MongoSelectLowerer`, `Stages/*`, `MongoPipelineFactory`, two renderers | Only the renderer and factory |
| **Read side** | A compiled function from a returned document (or reader) to an entity or projection. | `MongoProjectionBindingExpressionVisitor`, `...RemovingExpressionVisitor`, `MongoStreamingEntityMaterializerRewriter` | Yes, reads BSON stream |

### 3.3 Query modes and the gate

`MongoQueryMode` is set with `UseQueryMode(...)` on the options builder.

| **Mode** | **Behavior** |
|---|---|
| `Native` (default) | Native when representable, driver fallback otherwise. |
| `NativeOnly` | Throws `NativeTranslationNotSupportedException` at compile time instead of falling back. This is the **only reliable signal that a query went native**, because the MQL often matches the fallback's. |
| `DriverLinq` | Skips native entirely. Explicit opt-in, and restores pre-branch behavior. |

The gate `ClassifyNativeDisposition` (in MSQCEV) is a pure function of `(Route, IsFallbackWrongData, hasUnboundVectorSearch, mode)`:

| **Disposition** | **Meaning** |
|---|---|
| `Native` | Build a native pipeline. |
| `Fallback` | Use driver LINQ (throws under `NativeOnly`). |
| `HardDecline` | Throw in `Native` **and** `NativeOnly`, because the driver fallback is known to return wrong rows (today: GroupBy combined with a Join). Only explicit `DriverLinq` opts in. |

`MongoSelectDefinition.Route` is the authoritative is-native signal, derived (never stored) from IR state:

| **Route** | **When** | **Pipeline ends with** |
|---|---|---|
| `Fallback` | An unsupported operator was marked, an unconfirmed candidate join remains, or certain illegal combinations exist | n/a |
| `ScalarAggregate` | Terminal `Count`/`Sum`/`Min`/`Max`/`Average`/`Any`/`All` | `$count` / `$group` / `$limit 1` |
| `GroupBy` | A bound `$group` | `$group` plus a flattening `$project` |
| `Projection` | A pushed-down `Select` | `$project` |
| `WholeEntity` | None of the above (the fall-through value) | whole documents |

A native `Route` is not the last word: the lowerer or factory can still throw `NativeTranslationNotSupportedException`. In `Native` mode that is a **late fallback** (`TryBuildPipeline` returns null). In `NativeOnly` it is an error. The read side must then be able to read the driver's output, which is why several `Strip...OnFallback` paths exist (section 6.4).

### 3.4 Dividing line: what is *not* contract

Per `AGENTS.md`: which path a query takes, the exact MQL, and the exception type for an *unsupported* shape are not public contract. Results and the exception type for *supported* operations are.

---

## 4. One query, end to end

Take an `Orders` query that includes `Customer` and filters `CustomerID.StartsWith("F")`. The MQL is a real baseline from `NorthwindIncludeQueryMongoTest.Include_reference`:

```
Orders.{ "$match" : { "CustomerID" : { "$regularExpression" : { "pattern" : "^F", "options" : "s" } } } },
       { "$lookup" : { "from" : "Customers", "localField" : "CustomerID", "foreignField" : "_id", "as" : "_lookup_Customer" } },
       { "$unwind" : { "path" : "$_lookup_Customer", "preserveNullAndEmptyArrays" : true } }
```

1. **Preprocess.** The provider's preprocessor first runs its own passes (section 5.1). EF's navigation expansion then rewrites `Include(o => o.Customer)` into a `LeftJoin` with a `TransparentIdentifier(Outer, Inner)` result selector and a trailing `Select` that wraps an `IncludeExpression`.

2. **Translate (QMTEV).** `VisitMethodCall` walks the call chain from the root up. For each call it:

   - lets the source be visited first;
   - keeps `_finalExpression` (the original chain) and writes it to `CapturedExpression` after every call;
   - calls `NativeSlotPopulator.PopulateNativeSlots`.

EF itself translates nothing for `Where`; the shaper is unchanged. The predicate goes through `MongoExpressionTranslator` into a `MongoMatchOp` on `Select.PipelineOps`. The `LeftJoin` is recorded as a **candidate** reference-Include join. In `TranslateSelect` the trailing `Include` selector matches the reference-Include recognizer, which **confirms** it and registers the `$lookup`. Unconfirmed, `Route` would be `Fallback`.

3. **Postprocess.** `ApplyProjection` finalizes the read-side projection mapping.

4. **Compile (MSQCEV).** `ClassifyNativeDisposition` returns `Native`. `MongoSelectLowerer.Lower` emits typed stages: `$match`, then the lookup block (`$lookup` and `$unwind` with `preserveNullAndEmptyArrays` for left-outer). `MongoPipelineFactory.Create` renders each into BSON. The `StartsWith` term is a constant, so the regex is baked in; a parameter would leave a placeholder and build the regex per execution. A shaper is compiled that reads the root fields plus `_lookup_Customer` and fixes up the navigation.

5. **Execute.** `ExecuteShapedQuery` → `TranslateQuery` → `nativeFactory.Build(parameters)` → `MongoExecutableQuery { NativePipeline }` → `MongoClientWrapper.Execute` → `collection.Aggregate(...)` → `QueryingEnumerable` applies the shaper to each document.

Two more real shapes for orientation (also from baselines):

```
GroupBy(o => o.CustomerID).Select(g => g.Count())
  Orders.{ "$group" : { "_id" : "$CustomerID", "_v" : { "$sum" : 1 } } }, { "$project" : { "_v" : "$_v", "_id" : 0 } }
```

```
Select(o => cond ? o.OrderID : -o.OrderID)        // bare projection leaf
  Orders.{ "$project" : { "_v" : { "$cond" : { ... } }, "_id" : 0 } }
```

`_v` is the synthetic alias (`SyntheticBareProjectionAlias`) for a bare selector body, and the first example of emit/read pairing: the shaper must read `_v` because the pipeline emits `_v`.

---

## 5. The emit side in detail

### 5.1 Preprocessing, capturing and populating

**Preprocessor** (`MongoQueryTranslationPreprocessor.Process`, before EF's navigation expansion). These passes run for every mode, so they affect the fallback too:

- `FinalPredicateHoistingVisitor`: rewrites `Count(pred)`, `LongCount(pred)` and `Any(pred)` into `Where(pred).Count()` etc., so the predicate is translated like any other `Where`.
- `EntityFrameworkDetourExpressionVisitor`: stops EF rewriting `e.Children == null` into `e == null`.
- `LocalCollectionFilterFoldingVisitor`: folds a closed `Enumerable.Where(localList, ...)` feeding `Contains` into a plain list so it becomes an indexable `$in`. A parameter source is re-filtered per execution, and a clock or non-deterministic predicate is never folded at compile time.
- Vector search lift-out: removes the `VectorSearch` call before navigation expansion (which crashes on it) and re-inserts it afterwards. Re-check this if the passes are reordered.
- `DerivedEntityRootNarrower`: rewrites `Set<Dog>()` to `Set<Animal>().OfType<Dog>()` so one discriminator predicate applies in every mode.

**Capturing and populating.** Files: `Visitors/MongoQueryableMethodTranslatingExpressionVisitor*.cs` (QMTEV, five partials), `NativeTranslation/NativeSlotPopulator.cs`, `Native*Binder.cs`.

- **QMTEV bypasses most of EF's per-operator** **`Translate*`** **dispatch.** `VisitMethodCall` forwards to EF's base dispatch only for operators that reshape the query: `Select`, `OfType`, `Distinct`, `Union`/`Concat`, aggregates, `Join`/`GroupJoin`/`LeftJoin`, `GroupBy`, `SelectMany`, `Contains`, `Except`/`Intersect`. For `Where`, `OrderBy`, `ThenBy`, `Skip`, `Take` and the reducers the `Translate*` overrides are **deliberately inert** (`=> null`) and `PopulateNativeSlots` records the op. Routing them through both would double-populate.
- **`NativeSlotPopulator.PopulateNativeSlots`** is a ladder of arms, each keyed on the operator and the current IR state. Order matters. Before its normal arms it runs the *post-terminal guards*: after `GroupBy`, projected `Distinct`, a set op or `SelectMany`, a later operator is only native if an arm explicitly handles it, otherwise `MarkNotNativelyRepresentable()`.
- **Binders** (`NativeProjectionBinder`, `NativeGroupByBinder`, `NativeCardinalityBinder`, `NativeSelectManyBinder`, `NativeJoinScopeProjectionBinder`, `NativeReferencedCollectionBinder`, `NativeVectorSearchBinder`, ...) each own one family of shapes. Contract: **stage into locals, commit to the IR only after every gate passes, never mutate then decline.** A half-applied IR would mislead the gate and the fallback.
- **`TranslateSelect`** is the biggest dispatch: an ordered ladder of arms (group result shaper, `SelectMany` transparent-identifier projection, reference/collection Include confirmation, join-scope leaves, generic projection). The recent commits on this branch extract arms from it (`PendingProjectionStaging`, per-selector binder sessions).

### 5.2 The IR

`MongoQueryExpression` is the node EF sees. It is an `Expression`, so it can sit inside EF's `ShapedQueryExpression`. It carries three views of one query:

| **View** | **Holds** | **Used by** |
|---|---|---|
| EF-facing | Projection mapping, projection list, `ApplyProjection` | The shaper (both paths) |
| `CapturedExpression` | The EF-translated LINQ chain | The driver fallback |
| `Select` (`MongoSelectDefinition`) | The native logical query | The gate, lowerer and read side |

Cross-collection `$lookup` state (`_pendingLookups`, `JoinInfo`, `_joins`) lives on `MongoQueryExpression`, not `Select`. It is entangled with the fallback's document shape.

`MongoSelectDefinition` (a plain data holder, "Definition" because it is not an `Expression`) holds:

- **Ordered op lists.** `MongoMatchOp`/`MongoSortOp`/`MongoSkipOp`/`MongoLimitOp`/`MongoDistinctOp` are recorded into one of five lists, chosen by `ActiveOps` from the query's current state. The lowerer places each list at a fixed point in the pipeline:

| **List** | **Emitted** | **Why it exists** |
|---|---|---|
| `PipelineOps` | before lookups | the default |
| `PostJoinOps` | after the `$lookup`/`$unwind` block | a `Where`/`OrderBy` that reads the join's Inner side |
| `PostLookupPagingOps` | after the join block | paging EF hoisted ahead of a join's result selector, deferred past it |
| `PostGroupOps` | after `$group`'s flattening `$project` | filters, ordering and paging over grouped or distinct output |
| `TrailingOps` | after a set op | operators composed on the combined result |

- **Projection** (`MongoProjection`: alias + `MongoExpression`) and alias overrides (`ProjectionAliasTier`).

- **Terminal structures.** `Grouping` (`$group`), `Cardinality` (reducer or scalar aggregate), `SetOperations`, `UnwindSource` (native `SelectMany`), `VectorSearch`, `JoinScope`.

- **Flags**, of three kinds the name alone does not reveal (documented on the class):

  - *derived*: computed getters such as `HasPaging` and `Route`;
  - *recorded*: set-once facts with a `Mark*` method, such as `HasUnsupportedOperator`, which cannot be unset;
  - *shape flags*: plain settable properties a binder assigns at its commit point, such as `HasArrayProjectionLeaf`.

`MongoExpression` is the dialect-neutral scalar/predicate tree: dozens of node types in `Query/Expressions/` (field, parameter, binary, `$in`, regex, size, conditional, date part, ...). Two properties to know:

- `VisitChildren` is a no-op, so nodes are not traversed with the usual visitor machinery. Every consumer (negator, both renderers, translator, ...) hand-rolls a `switch`. `MongoExpressionNodeCoverageTests` reflects over every subtype × dispatcher to catch a new node that a consumer forgot. It is blind to shape-conditional dispatch (for example `MongoRegexExpression` constant vs field-to-field).
- Parameters (`MongoParameterExpression`) and constants carry a serialization hint so they render through the right property serializer.

### 5.3 The expression translator

**Files:** `NativeTranslation/MongoExpressionTranslator*.cs` (17 partial files, ~5.4k lines).

Converts an EF lambda body into a `MongoExpression`. The contract: `TryTranslate*` **returns false and never throws** for an unsupported shape, so the caller can decline.

- **Scope resolves by parameter identity.** The translator may resolve members against the root entity, an outer/inner pair (correlated subquery, `SelectMany`, joins) or a projected alias scope (`DistinctAliasScope`, `ProjectedAliasScope`). It decides by `ReferenceEquals` on the lambda parameter, never by member name, because names collide across types and aliases.
- **Stored form matters.** A property with a value converter or non-default `BsonRepresentation` is stored differently from its CLR value. Equality is safe (the constant goes through the same serializer). Ordering, relational comparison and aggregates run on the stored form, so they **decline** for such properties (`StoredOrdering`, `StoredSerialization`; the driver bridge refuses too).
- Many sub-translations live in partials by topic: `StringCompare`, `Regex`, `Math`, `Trim`, `TypeIs`, `EntityEquality`, `OwnedEquality`, `Dictionary`, `FieldPresence`, `CaseMapping`, ...

### 5.4 Lowering

**Files:** `MongoSelectLowerer.cs`, `Stages/*.cs`.

`Lower(query)` converts the IR into an ordered `IReadOnlyList<MongoPipelineStage>`. The fixed order is: vector search stages → `PipelineOps` → lookups and post-join ops → set-op stages (with lookups deferred until after the combine) → unwind source → prior grouping → `$group` and its post-group stages → terminal `$project` → cardinality stages (`$count`, `$group` accumulator, `$limit 1`, last-row `$group` + `$replaceRoot`).

The lowerer is the single place that fixes stage *order*, where several wrong-rows bugs live (for example, paging deferred past a 1:N join). Being BSON-free, it is unit-tested directly (`MongoSelectLowererTests`). It throws `NativeTranslationNotSupportedException` for an unsupported lookup shape, which becomes the late fallback.

### 5.5 Rendering and the pipeline factory

**Files:** `MongoPipelineFactory.cs`, `PlaceholderTable.cs`, `MongoQueryLanguageRenderer.cs`, `MongoAggregationExpressionRenderer.cs`, `MongoValueRenderer.cs`, `RuntimeParameterValues.cs`, `CompositeKeyClauses.cs`.

**Two dialects.** MongoDB has two expression languages, and they treat null and missing differently:

| **Dialect** | **Looks like** | **Used for** |
|---|---|---|
| **Query dialect** | `{ field: { $gt: 5 } }` | `$match`. Can use indexes. Relational operators only match values of the same type bracket and do not match missing/null. |
| **Aggregation dialect** | `{ $gt: ["$field", 5] }` inside `$expr`, `$project`, `$group` | Computed values, field-to-field comparisons. Orders null/missing *below every value*, so a bare `$lt` answers true for a missing field. |

`MongoQueryLanguageRenderer` renders predicates in query dialect and wraps any subtree that has no correct query form in `$expr` (via `MongoAggregationExpressionRenderer`). Predicates like `IsQueryDialectRenderable` and `IsQueryNativeComparison` are shared with the negator and translator so they cannot drift.

**Factory.** `MongoPipelineFactory.Create` renders every stage once into a `StageSlot` (a ready `BsonDocument`, or a *deferred* builder for a stage whose shape depends on runtime state, such as `$vectorSearch`). A parameter renders as a **placeholder sentinel** `{ "__mongoef_param__": <index> }`, with the details (serializer, array/regex/entity-key flags) recorded in `PlaceholderTable`. Per execution:

1. `Build(context)` first wraps the parameter values so runtime-evaluated parameters (clock) are computed now.
2. Each slot is deep-cloned (or its deferred builder is run).
3. `SubstituteDocument` walks the clone replacing sentinels with serialized values. Entity-key, regex, array-element, composite-key and null-aware cases are handled in `SerializeParameter`.
4. `NormalizePagingStages` rewrites `$limit: 0` to an always-false `$match` (the server rejects `$limit: 0`) and throws on negative `Skip`/`Take`, including inside `$lookup` and `$unionWith` sub-pipelines.

**Why sentinels instead of re-lowering:** a sentinel is just a BSON document, so substitution is a tree walk with no further translation. Sentinels must never start with `$`, because one renderer arm treats a leading-`$` document as an operator document.

### 5.6 The read side

**Files:** `MongoProjectionBindingExpressionVisitor*.cs`, `MongoProjectionBindingRemovingExpressionVisitor.cs`, `MongoMixedProjectionBindingRemovingExpressionVisitor.cs`, `MongoStreamingEntityMaterializerRewriter.cs`, `BsonDocumentInjectingExpressionVisitor.cs`, `StreamingEligibility.cs`.

EF builds a **shaper**: an expression tree that turns one row into an entity or projection, reading values through `ProjectionBindingExpression`s. The provider turns that into code that reads BSON, in two steps:

1. **Binding visitor** (`MongoProjectionBindingExpressionVisitor`, runs inside `TranslateSelect`). Maps each projection member to a read index or alias and records aliases so emit and read agree. When it reaches a collection `IncludeExpression` it registers that Include's `$lookup` (`AddLookup`).
2. **Binding-removing visitor** (runs in `CompileShapedQuery`) replaces each binding with an actual BSON read.

There are three readers:

| **Reader** | **Used when** | **Notes** |
|---|---|---|
| **DOM reader** (`MongoProjectionBindingRemovingExpressionVisitor`) | All routes, including projection, GroupBy and joins | Reads from a `BsonDocument` by alias or path. The general reader for native output. |
| **Streaming reader** (`MongoStreamingEntityMaterializerRewriter`) | Whole-entity, enumerable results where `StreamingEligibility` and `AllPendingLookupsAreStreamable` hold (flat, owned reference and simple owned collections; single-level reference lookups) | One forward pass over the cursor's `IBsonReader`. The compiled shaper *becomes the output serializer's* `Deserialize`, so the cursor yields finished entities. Falls back to DOM if the rewriter throws (except under `NativeOnly`). |
| **Mixed reader** (`MongoMixedProjectionBindingRemovingExpressionVisitor`) | The driver fallback when a projection mixes entity references and scalars | The `Select` is stripped, so the driver returns whole documents and the shaper projects client-side. |

**The emit/read agreement is the core invariant of this layer.** An alias emitted in `$project` must be the name the shaper reads, an owned array leaf's alias must equal the navigation's document path, and every client-evaluated leaf must be marked so server-side operators decline. The IR carries explicit alias override tables (`AddProjectionAliasOverride`, write-once) because two aliases for one member would make the emitted key and the read name disagree.

Read-modes for a non-nullable value that comes back null or missing (`Plain`, `ThrowOnNull`, `ThrowOnMalformedNull`, `DefaultOnMalformedMissing`) are a decision table whose oracle is driver-LINQ (section 8.4).

### 5.7 Execution

`ExecuteShapedQuery` (a static method referenced by the compiled expression) calls `TranslateQuery`:

- **Native:** `nativeFactory.Build(MongoNativeBuildContext)` yields `BsonDocument[]`. These are wrapped in a `MongoExecutableQuery` with `NativePipeline`, `Session` (ambient transaction), `Streaming` and `OutputSerializer`.
- **Fallback:** a `MongoEFToLinqTranslatingExpressionVisitor` rewrites `CapturedExpression` into driver LINQ, which the driver provider executes.

`QueryingEnumerable` initializes EF's state manager (tracking), then calls `MongoClientWrapper.Execute`, which runs `collection.Aggregate(...)` (Storage owns this; Query never calls the collection API directly). Each row goes through the shaper. A null row (from a scalar reducer with no match) yields `default`.

### 5.8 The fallback bridge

**Files:** `MongoEFToLinqTranslatingExpressionVisitor*.cs` (about 4.6k lines).

Rewrites the EF chain into something the driver LINQ provider can translate: resolves query-context parameters, peels `Join`s into flat `$lookup` + `$unwind`, rewrites EF's `LeftJoin`, appends pending lookups, and so on.

Why it matters to a native reviewer: **native-only IR state can be invisible to the bridge.** Example: a filtered join inner carried on a lookup's `InnerPredicate` would be silently dropped by the bridge and return unfiltered rows. The bridge constructor therefore *refuses* (throws) when it sees such a lookup, using the same predicates the native side uses (`IsJoinWithNativeOnlyInnerFilter`, `HasElementStagesWithoutLeaf`). Every new native-only lookup feature needs a matching refusal. A miss means wrong rows on the fallback path.

---

## 6. The compile-time gate and the fallback contract

### 6.1 How a query becomes `Fallback`

`MarkNotNativelyRepresentable()` is sticky (set once, never unset). Other forced-fallback causes are unconfirmed candidate joins, `PendingGroupKey` without a finalized grouping, and `ReferenceCollectionCountPredicateConfirmed` combined with a set op, grouping, join scope or unwind.

### 6.2 Three decline levels

| **Level** | **Where** | **Effect** |
|---|---|---|
| Binder or translator returns false | QMTEV/binders | Caller marks `Fallback`. Query still runs on the driver. |
| `Route` is native but lowering or factory throws | `TryBuildPipeline` | Late fallback in `Native`, error in `NativeOnly`. |
| `HardDecline` | gate | Throws in every mode but explicit `DriverLinq`. |

### 6.3 Post-terminal gating

After a *terminal* operator (`GroupBy`, projected `Distinct`, set op, `SelectMany` unwind: `HasTerminalOperator`), the rows no longer look like entities. A later operator that resolves members against the entity would emit a stage before the terminal and read fields that no longer exist, or the reverse. Every operator after a terminal must explicitly decide whether it can stay native. A `Translate*` override bypasses the shared gates and must replicate the check.

### 6.4 Late fallback and read-side stripping

When the native factory declines late, the shaper was already built to read native aliases. Several paths strip the pushed-down `Select` from the captured chain (`StripPushedDownSelect`, `ShouldStripBareProjectionOnFallback`, `createFallbackBindingRemover`) so the driver returns whole documents the shaper can read. These paths are tier-conditional and easy to get wrong. A new alias kind may need a case.

---

## 7. Features

Short notes per feature. Each shows the shape, the main mechanism and the main trap. `AGENTS.md` has the rules.

### 7.1 Filters, ordering, paging

- `Where` → `MongoMatchOp` (predicates AND-merged into a tail match, else a new one). `OrderBy` replaces a tail sort, `ThenBy` extends it, and a duplicate bare-field key is dropped (a duplicate `$sort` field is a server error, CSHARP-5690).
- `Reverse`/`Last` flip a trailing sort (MQL has no reverse). An unordered `Last` uses `$group $last` + `$replaceRoot`.
- **Traps:** `Skip` and `Take` are order-sensitive, so ops are never reordered. `Take(0)` and `$limit: 0`: see 5.5. Negation must be an exact complement or decline (`MongoExpressionNegator`).

### 7.2 Projections

- `NativeProjectionBinder` stages one `MongoProjection` per leaf (`new { ... }`, `new Dto(...)`, member-init, bare value). All leaves are admitted or the whole projection declines.
- **Leaf kinds:** scalar and computed, whole-entity, owned array or reference, nested construction, client-evaluated (shaper computes it; the server never sees it), client-conditional, string-sequence, positional container.
- **Traps:** alias agreement (5.6). Client-evaluated and case-mapped leaves must make later value-reading operators decline. Nested constructions flatten under a last-name alias rule. Three flags (`HasClientEvaluatedProjectionLeaf`, `HasClientCaseMappingProjectionLeaf`, `HasClientConditionalProjectionLeaf`) gate `Distinct`, set ops and value operators.

### 7.3 Joins, Include and `$lookup`

**Reference** **`Include`** **and user** **`Join`****/****`LeftJoin`****/****`GroupJoin`** **look identical** after EF's navigation expansion: `TransparentIdentifier(Outer, Inner)` plus a result selector. The provider handles this with a *two-phase handshake*:

1. **Record** (`NativeSlotPopulator`, `TranslateJoinCore`): every `Join`/`GroupJoin`/`LeftJoin` call bumps a *candidate* counter (`MarkSawCandidateReferenceIncludeJoin`) and builds a `JoinInfo` (target, left-outer?, navigation, alias `_lookup_<Nav>`, forced-unwind `LookupExpression`) plus a `MongoJoinScope` of levels. Nothing is registered with the query yet.
2. **Confirm** (`TranslateSelect`: the reference-Include recognizer, or `NativeJoinScopeProjectionBinder.ConfirmEntireChain` for user joins): only when the consuming `Select` is bound natively is the `$lookup` registered and the confirmation counter bumped. `HasUnconfirmedCandidateJoin` is `candidates != confirmations`, and `Route` is `Fallback` while it holds. Registering eagerly would change the fallback's document shape (`UsesDriverJoinFields`).

Mechanism: `$lookup` + `$unwind` (`preserveNullAndEmptyArrays` = left-outer). A collection `Include` registers its `$lookup` when the binding visitor reaches its `IncludeExpression` (5.6).

**The hard problems here:**

- *Paging vs joins.* EF hoists `Skip`/`Take`/`Where`/`OrderBy` ahead of a join's result selector. Paging before a row-changing join must stay ahead of the `$lookup`. Paging written after applies to joined rows. See `JoinInfo.PagingBeforeItMustStayAhead`, `DeferPipelineOpsPastConfirmedJoin` and `HasPagingRecordedBetweenJoins`. `NativeJoinScopeGate.EvaluateSingleJoinScope` is the pure check; `TryAdmit...` also mutates.
- *Alias collisions* between a join and an Include on the same navigation.
- *Filtered inner* (`InnerPredicate`) lives on the lookup, renders through the placeholder table, and must never reach the bridge.
- *Navigation-less joins* (key equality with no model navigation) are native only if both keys resolve as simple raw keys.
- *Correlation matching* (`NativeCorrelationMatcher`): turning `db.Orders.Where(o => o.CustomerId == c.X)` into `c.Orders` requires proving `c.X` is the navigation's FK principal key. Any other outer member silently reads the wrong rows.
- *TPH targets* narrow by discriminator inside the lookup.

### 7.4 Aggregates and quantifiers over a referenced collection

`c.Orders.Any(...)`, `.Count(pred)`, `.Sum(o => o.Price)`, GroupJoin aggregates and element leaves like `c.Orders.OrderBy(..).Take(3).ToList()` become operators over a **non-unwound** **`$lookup`** **array** (`_lookup_<Nav>` and `_fN`-suffixed variants).

- `MongoExpressionTranslator.ReferencedCollectionResolver` maps a correlated subquery to the array. `NativeReferencedCollectionBinder` handles `Where`/`OrderBy` sites and `NativeReferencedElementMatcher` the element leaves, both shared by emit and read sides.
- **Empty group:** `$sum` is 0; `$min`/`$max`/`$avg` are null and read strictly for non-nullable results, so they throw like LINQ.
- Commit rule: the lookup is staged, and registered only once the whole predicate or key translated. Then a trailing sort is relocated and recording flips to `PostJoinOps`.
- The fallback bridge answers these leaves itself and refuses combinations it cannot (`ReferencedCollection.cs`).

### 7.5 GroupBy

`NativeGroupByBinder` works in two steps: bind the key (`PendingGroupKey`), then bind the terminal projection or aggregate (`Grouping`). The lowerer emits `$group` then a flattening `$project` that lifts `_id` sub-keys and accumulators to the aliases the shaper reads.

- `Where` between `GroupBy` and `Select` is a HAVING `$match` between `$group` and the flatten. Ordering and paging on the group sit before the flatten (`GroupOrderOp`, `GroupPagingOps`), and operators after `Select` go to `PostGroupOps` and resolve against the `Select`'s **output aliases**.
- Nested `GroupBy` on a finalized grouping snapshots the first into `Prior*`.
- **Traps:** GroupBy + Join is a `HardDecline`. GroupBy over a *join scope* needs a transparent-identifier key and may read an unmatched left-outer side (declines unless every operator propagates missing as null). A composite `_id` omits a missing sub-key, so renderers `$ifNull`-normalize each part. Case mapping inside a grouped result throws on the driver path (`ThrowIfDriverLinqCaseMapsGroupedResult`).

### 7.6 Set operations

`Union`, `Concat`, `Intersect`, `Except` form a **tree**; each `Union`'s dedup belongs to its own link (`A.Concat(B.Union(C))` differs from a left chain). Lowered to `$unionWith` and set-difference stages. Operands read through *source1's* shaper, so projected operands must align aliases and stored serialization. Ops between links go to `TrailingOps`, then move to `PrecedingOps` of the next link.

### 7.7 `SelectMany` and unwind

Owned collection `SelectMany` → `$unwind` + `$project` (`MongoUnwindSource`). `SelectMany` over a reference collection is a lookup plus unwind. A whole-element selector adds `$replaceRoot` and roots the shaper at the element type.

### 7.8 Reducers and scalar aggregates

`NativeCardinalityBinder` populates `MongoCardinality`.

- Reducers (`First`/`Single`/`Last`...) synthesize a `$limit`. EF's own cardinality reduction still applies the throw-on-empty and throw-on-multiple semantics.
- Scalar aggregates: `Count` → `$count`, `Sum`/`Min`/`Max`/`Average` → `$group` accumulator, `Any`/`All` → `$limit 1`. `MongoEmptyAggregateBehavior` encodes empty-input semantics (default value, null or throw) per operator and result type.
- Min/Max read back through the *property's* serializer (`NativeAggregateReadBack`) so enums, `DateOnly` and Local `DateTime` round-trip.

### 7.9 Other

- **Vector search** (`$vectorSearch`) has a dedicated slot, because it must be the first stage. The preprocessor lifts the call out around nav-expansion (which crashes on it) and puts it back. A deferred `StageSlot` builds it at execution.
- **OfType /** **`is`** **/** **`GetType()`** share `MongoDiscriminatorPredicate` (one discriminator test, abstract types contribute no value).
- **Owned entities** are embedded documents. Predicates use dotted paths and `$elemMatch`/`$filter`/`$size`, and owned equality is a per-property conjunction. Primitive collections and dictionaries have query-dialect-only forms.
- **Bulk operations** (`ExecuteUpdate`/`ExecuteDelete`) share the QMTEV (`NonQuery.cs`) and compile to a `MongoBulkPlan` of delegates that Storage executes (`MongoBulkPlanBuilder`).
- **Strings, math, dates:** many functions have native forms. `ToLower`/`ToUpper` are *never* emitted natively (the server's are ASCII-only): `Where` becomes an anchored case-insensitive regex, `Select` projects the raw string and re-applies the mapping client-side.

---

## 8. Cross-cutting semantics (where most bugs live)

### 8.1 Null vs missing

BSON distinguishes a field that is `null` from one that is *absent*; C# has only `null`. The two dialects order and compare them differently, and `$size` of a missing array is a server error. Consequences in the code:

- The aggregation dialect orders null/missing below everything, so renderers conjoin a `$gt: [x, null]` guard when a value may be null.
- `$ifNull` around `$size`/`$filter` is mandatory.
- Non-nullable values that may be missing (an unmatched left-join side) get no guard, so conditions over them decline.
- A null parameter for a non-nullable property serializes as BSON null (`SerializeNullAware`) so it matches null *and* missing rows, as the driver did.

### 8.2 Exact complement or decline

`$lt/$lte/$gt/$gte` do not partition missing/null, so the negation of `x > 5` is `$not: {$gt: 5}`, not `$lte`. `MongoExpressionNegator` inverts `$eq`/`$ne`, `$not`-wraps relationals, and De Morgans `&&`/`||`. If it cannot be exact, it declines. `$expr` inside `$elemMatch` is a server error, so negation output must also be query-dialect renderable.

### 8.3 String literals

Every string constant or parameter in the aggregation dialect is `$literal`-wrapped; otherwise user input like `"$Field"` is read as a field path. New operand positions must go through `RenderBranch`/`RenderOperand`. Parameterized `StartsWith`/`EndsWith`/`Contains` are escaped and anchored at `Build` time.

### 8.4 Reading back null and missing (D-F10 and the read-mode table)

For a non-nullable value read from a field that is null or missing, native follows driver-LINQ's behavior: on the `Projection` route a *missing* bare stored scalar reads `default(T)` and an explicit null throws; whole-entity reads stay strict; computed leaves over a null-propagating operator throw. The *exception type* is not contract, but value-vs-throw is.

### 8.5 Stored form, converters and read-back kinds

- Converters and non-default `BsonRepresentation`: equality is safe, ordering, aggregates and many reads are not (`StoredOrdering`, `StoredSerialization.HasDefaultKeySerialization`).
- `DateTime` with `Local` kind must be read back through the property's serializer. Values computed from it (`AddDays`, `.Date`) have no property to read through, so they decline. This is checked in the postprocessor (`NativeDateTimeKindReadBack`).

### 8.6 Plan caching, parameters and non-determinism

The template is built once and reused across executions with different parameter values, so anything that must vary per execution must be a placeholder, never baked.

- Clock members (`DateTime.Now/UtcNow/Today`, `DateTimeOffset.Now/UtcNow`) become runtime-evaluated parameters (`RuntimeClock`).
- Non-deterministic calls (`Guid.NewGuid`, `Random`) throw at compile time in every mode (`NonDeterministicCalls`). Local-collection folding in the preprocessor also refuses to fold them.
- `Skip`/`Take` values may be parameters. A zero or negative value is validated at `Build`, not compile.

### 8.7 EF version differences

EF8/EF9 versus EF10 differ in the `LeftJoin` shim, GroupJoin composite-key guards, `ParameterValues` vs `Parameters` and materializer injection. A change that passes on one version may not on another. Run `/test-all`.

---

## 9. Reviewing this code

### 9.1 Strategy for a very large diff

- **Review by layer, not by commit.** Suggested order: `Expressions/MongoSelectDefinition.cs` (the vocabulary, plus the `Route` property) → `MSQCEV.ClassifyNativeDisposition`/`CompileShapedQuery`/`TranslateQuery` (the spine) → `NativeSlotPopulator` → one binder end to end → `MongoSelectLowerer` → `MongoPipelineFactory` → one renderer → the read side for the same feature → the bridge refusal list.
- **Pick a feature and trace emit → read → fallback.** For a new shape ask three questions: what does the gate admit, what does lowering emit, what does the shaper read?
- **Skim** **`Query/AGENTS.md`** **invariants first.** Each one is a past wrong-rows bug.

### 9.2 Failure classes to look for

| **Failure** | **Smell in a diff** |
|---|---|
| **Gate wider than the fix** | A condition in the gate that restates the binder's condition instead of calling its predicate. |
| **Mutate then decline** | A recognizer that writes to `Select` or lookups before its last `return false`. |
| **Operator after a terminal** | A new `Translate*` override or populator arm that does not check `HasTerminalOperator`. |
| **Member resolved by name** | `.Name ==` or CLR-type matching where a parameter reference is available. |
| **Emit/read disagreement** | A new alias, flag or leaf kind handled by the binder but not by the read-side visitor, the mixed reader or the late-fallback strip. |
| **Native-only state the bridge cannot see** | A new lookup feature without a bridge-constructor refusal. |
| **Bare** **`$lt`****/****`$gt`****,** **`$not`****, truthiness** | A renderer arm that ignores `MayBeNull`, a negation that is not an exact complement, or a bool with non-default serialization (`HasConversion<string>()` stores truthy `"False"`) used as a bare truth value. |
| **Unwrapped string in aggregation dialect** | A string constant rendered without `$literal`. |
| **Baked runtime value** | A clock, random or per-execution value captured into the template. |
| **Order-dependent arm** | A new arm inserted into a ladder (`TranslateSelect`, `PopulateNativeSlots`, `TranslateNode`) without checking what it now shadows. |
| **Paging across a join** | A skip/limit placed in `PipelineOps` when a row-changing join sits behind it, or the reverse. |
| **Stored form assumed default** | Ordering/aggregation on a converted or non-default-represented property without calling the shared predicate. |
| **New node, forgotten consumer** | A new `MongoExpression` subtype missing from the negator, classifier or either renderer. The coverage test catches most of these. |

### 9.3 Questions for any change

1. Is the native/fallback decision still made in the *same* place with the *same* predicate?
2. If it declines, is IR state unchanged, or `MarkNotNativelyRepresentable()` called?
3. Does `NativeOnly` throw where the author expects?
4. Does the template stay valid across parameter values (including null, empty and large lists)?
5. What happens for ragged data: field missing, null, empty array, wrong type?
6. Is there a differential test against an in-memory LINQ oracle?
7. What does the fallback do with the same query? Same answer, or a refusal?
8. Does it hold on EF8, EF9 and EF10?

### 9.4 Tests and how to prove "went native"

| **Project** | **Role** | **Location** |
|---|---|---|
| UnitTests | IR, translator, renderer, lowerer, factory: no database | `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/` (`NativeTranslation/`) |
| FunctionalTests | Real server. Differential theories compare native results to an in-memory oracle over ragged data (`NativeOwnedCollectionAllTests`, `NativeModeAssert`). | `tests/.../FunctionalTests/Query/` (~170 files) |
| SpecificationTests | EF Core's conformance suite plus MQL baselines (`AssertMql`) | `tests/.../SpecificationTests/Query/` |

- **MQL shape does not prove native.** Use `MongoQueryMode.NativeOnly`, or set `MONGODB_EF_QUERY_MODE=NativeOnly` to flip every spec and functional context.
- `tests/tools/native-parity-diff.sh` runs the suites under `DriverLinq` and `NativeOnly` and diffs outcomes: *regress* means it passes on the driver but fails natively, *improve* is the reverse.
- Baselines regenerate with `EF_TEST_REWRITE_BASELINES=1` and a tight `--filter`. See `tests/.../SpecificationTests/AGENTS.md`.
- Agents: `.claude/agents/query-reviewer.md` plus the cross-cutting reviewers run via `/review-ef-core-provider`.

---

## 10. Known weak spots

From an earlier architecture review that is not part of the repository. Re-check against the current tree, because the branch is refactoring these (recent commits extract `TranslateSelect` arms and move projection staging into `PendingProjectionStaging`).

- **Large ordered ladders.** `TranslateSelect`, `PopulateNativeSlots` and `MongoExpressionTranslator.TranslateNode` depend on arm order.
- **A mutable IR.** `MongoSelectDefinition` has many public setters and shape flags. "Stage then commit" is a convention, not enforced.
- **BSON in the IR.** `LookupExpression.PipelineStages` is `List<BsonDocument>`, so lookup rendering splits between the IR and `RenderLookup`.
- **Native/fallback decision spread out.** The gate is the first of several decisions: the late fallback, `VisitProjectedQuery`'s own ladder and about 20 `Route ==` reads in the read side.
- **Parallel capability predicates.** `CanRender`/`Render` and `IsQueryDialectRenderable`/`RenderNode` must stay in lockstep.
- **No compile-time exhaustiveness** across `MongoExpression` nodes (only the reflection test).
- **Big partial classes** (QMTEV ~3.7k lines, `MongoExpressionTranslator` across 17 files, the bridge ~4.6k) behave as several classes.
- **Duplicated read decision tables** across the DOM, mixed and streaming readers (DateTime kind, enum, TimeSpan).

---

## 11. Glossary

**LINQ and EF Core**

| **Term** | **Meaning** |
|---|---|
| **LINQ** | C#'s query syntax and operators (`Where`, `Select`, `OrderBy`, ...). |
| **`IQueryable<T>`** | A LINQ source whose operators build an *expression tree* (data describing the query) instead of running immediately. A **provider** later interprets that tree. |
| **Expression tree** | An in-memory tree of nodes (method call, member access, lambda, constant, ...) that represents code as data. |
| **LINQ provider / EF Core provider** | The component that turns an expression tree into a store's query language and reads results back. This repo is the MongoDB provider for EF Core. |
| **Query translation / compilation** | EF's multi-phase conversion of a LINQ tree into executable code. Done once per query *shape* and cached. |
| **Preprocessor, QMTEV, postprocessor, shaped-query compiler** | EF's four translation phases. QMTEV (*QueryableMethodTranslatingExpressionVisitor*) translates each LINQ operator; the compiler produces the final executable expression. |
| **Navigation expansion** | The preprocessing step that rewrites navigation access (`o.Customer.Name`, `Include`) into joins/subqueries before the provider sees them. |
| **Navigation / reference vs collection navigation** | A model property pointing to related entity/entities. A *reference* is one entity, a *collection* is many. |
| **`Include`** **/** **`IncludeExpression`** | Asks EF to load a related entity along with the main one; nav expansion represents it as an `IncludeExpression` in the shaper. |
| **Owned entity** | An entity stored *inside* its owner's document (embedded sub-document or array), not in its own collection. |
| **TPH (table per hierarchy)** | All types of an inheritance hierarchy share one collection, told apart by a **discriminator** field. |
| **Shaper** | The compiled function turning one result row into an entity or projection. |
| **`ShapedQueryExpression`** | EF's pair of "the store query" and "the shaper". |
| **`ProjectionBindingExpression`** **/ projection member** | A placeholder in the shaper meaning "read this projected value", resolved to a concrete read at compile time. |
| **Value converter** | EF mapping that stores a property as a different type than its CLR type (an enum as a string). |
| **Materialization** | Creating the .NET object from stored data. |
| **Tracking / identity resolution** | EF's change tracking, and de-duplicating the same entity into one instance. |
| **Parameter extraction (funcletization)** | EF replaces captured C# variables in a query with parameters so one compiled query serves many values. |
| **`TransparentIdentifier`** | A compiler/EF-generated anonymous pair (`Outer`, `Inner`) carrying both sides through `Join`, `SelectMany` and `let`. |
| **`Join`** **/** **`GroupJoin`** **/** **`LeftJoin`** **/** **`SelectMany`** | Combine two sequences. `GroupJoin` keeps matches as a group, `LeftJoin` keeps unmatched left rows, `SelectMany` flattens a nested collection. |
| **Correlated subquery** | A subquery that refers to the outer row (`c.Orders.Where(o => o.Total > c.Limit)`). |
| **Terminal operator** | An operator that ends the row-by-row pipeline and changes what rows are (`GroupBy`, `Distinct` over a projection, set ops, `SelectMany`) or yields a single value (`First`, `Count`). |
| **Reducer / scalar aggregate** | `First`/`Single`/`Last` pick one row; `Count`/`Sum`/`Min`/`Max`/`Average`/`Any`/`All` produce one value. |

**MongoDB**

| **Term** | **Meaning** |
|---|---|
| **Document / BSON** | A MongoDB record, and its binary JSON-like format. |
| **Collection** | A group of documents, roughly a table. |
| **Cursor** | The server-side result stream the driver reads in batches. |
| **Aggregation pipeline** | An ordered list of **stages** that transform documents. |
| **Stage** | One step: `$match` (filter), `$sort`, `$skip`, `$limit`, `$project` (shape output), `$group` (group and aggregate), `$lookup` (join another collection), `$unwind` (one row per array element), `$unionWith`, `$replaceRoot`, `$vectorSearch`. |
| **MQL** | MongoDB Query Language, the stage and operator vocabulary. |
| **Query dialect vs aggregation dialect** | `$match` filters can use the find-style form (`{a: {$gt: 5}}`) or `$expr` with aggregation operators (`{$gt: ["$a", 5]}`). They differ on null/missing ordering and on index use. |
| **Null vs missing** | A field set to null versus a field not present. They are different in BSON and often compare differently. |
| **`_id`** | The primary key field. |
| **`$$ROOT`** | The whole current document inside an expression. |
| **`$expr`****,** **`$elemMatch`****,** **`$literal`** | `$expr` embeds an aggregation expression in a `$match`; `$elemMatch` tests array elements (query dialect only, and `$expr` inside it is an error); `$literal` marks a value as data, not a field path. |
| **Atlas Search / vector search** | Atlas-only features. Vector search finds nearest neighbors via `$vectorSearch`. |
| **BSON serializer /** **`BsonRepresentation`** | Code that converts a C# value to and from BSON. `BsonRepresentation` stores a value as a non-default BSON type (an int as a string, say). |
| **Driver** | The MongoDB C# driver, which has its own LINQ provider. |

**This codebase**

| **Term** | **Meaning** |
|---|---|
| **Native translator / path** | This branch's own LINQ-to-pipeline translation. |
| **Driver-LINQ / fallback / bridge** | The path that rewrites the EF chain and lets the driver's LINQ provider build the pipeline. The *bridge* is the rewriter (`MongoEFToLinqTranslatingExpressionVisitor`). |
| **IR (intermediate representation)** | A data model between the LINQ tree and BSON: `MongoSelectDefinition` and the `MongoExpression` tree. |
| **Dialect-neutral** | Not tied to a specific MQL form. The same node can render to either dialect. |
| **Binder** | A class (`Native*Binder`) that recognizes one family of LINQ shapes and fills the IR, or declines. |
| **Slot / op** | A place in the IR for an operator's effect, usually an entry in an ordered op list. |
| **Decline /** **`MarkNotNativelyRepresentable`** | A native step saying "I cannot handle this exactly". The query falls back. |
| **Route / disposition / gate** | `Route` is derived IR state (`Fallback`/`WholeEntity`/`Projection`/`ScalarAggregate`/`GroupBy`); disposition adds the mode and wrong-data checks; the *gate* is the compile-time decision. |
| **Hard decline** | A shape where the fallback is known to return wrong rows, so it throws in every mode but `DriverLinq`. |
| **Late fallback** | Falling back after the gate said native, because lowering or factory construction declined. |
| **Lowerer** | `MongoSelectLowerer`: IR to ordered, typed (BSON-free) stages. |
| **Renderer** | Typed stage or expression to BSON (query-dialect and aggregation-dialect renderers). |
| **Template / placeholder (sentinel)** | The pre-rendered BSON pipeline, with parameter sites marked by `{ "__mongoef_param__": n }`, filled per execution by `Build`. |
| **Emit side / read side** | Producing the pipeline versus the shaper that reads its output. They must agree on aliases and shapes. |
| **Alias** | The output field name in `$project`/`$group`. `_v` is the synthetic alias for a bare selector. |
| **`_lookup_<Nav>`** | The conventional output field of a `$lookup` for navigation `Nav`. |
| **Candidate / confirm** | Join recording in two steps: noted at `Join`, registered only when the consuming `Select` is bound natively. |
| **Join scope** | The `Outer`/`Inner` levels of a recognized join chain, used to resolve members by scope. |
| **Element leaf** | A projected list or first element of a referenced collection (`c.Orders.Where(..).ToList()`). |
| **DOM / streaming / mixed reader** | Read from a `BsonDocument`; read forward-only from the cursor in one pass; read from whole documents when the driver path projects client-side. |
| **Oracle** | A trusted reference answer used in tests, such as in-memory LINQ over the same data, or driver-LINQ. |
| **Differential test** | Run the same query natively and against the oracle and compare. |
| **Baseline** | An expected-MQL string asserted by `AssertMql`. |
| **Silently wrong rows** | The failure this design is organized to prevent: a query that runs and returns plausible but incorrect results. |

---

## 12. Where things are

| **Concern** | **Location (under** **`src/MongoDB.EntityFrameworkCore/Query/`****)** |
|---|---|
| Phase customization (factories, preprocessor, postprocessor) | `Factories/`, `MongoQueryTranslationPreprocessor.cs`, `MongoQueryTranslationPostprocessor.cs`, `MongoQueryCompilationContext.cs` |
| Operator translation (QMTEV) | `Visitors/MongoQueryableMethodTranslatingExpressionVisitor*.cs` (core, `Include`, `Join`, `SetOps`, `NonQuery`) |
| Recording ops, binders | `NativeTranslation/NativeSlotPopulator.cs`, `Native*Binder.cs`, `NativeJoinScope*.cs`, `NativeCorrelationMatcher.cs` |
| IR | `Expressions/MongoQueryExpression*.cs`, `MongoSelectDefinition.cs`, `MongoSelectOp.cs`, `MongoExpression` subclasses, `LookupExpression.cs`, `JoinInfo.cs` |
| Expression translation | `NativeTranslation/MongoExpressionTranslator*.cs`, `MongoExpressionNegator.cs`, `StoredOrdering.cs`, `StoredSerialization.cs` |
| Gate, compile, execute | `Visitors/MongoShapedQueryCompilingExpressionVisitor.cs`, `QueryingEnumerable.cs`, `MongoExecutableQuery.cs`; `Storage/MongoClientWrapper.Execute` |
| Lowering, rendering | `NativeTranslation/MongoSelectLowerer.cs`, `Stages/`, `MongoPipelineFactory.cs`, `PlaceholderTable.cs`, `MongoQueryLanguageRenderer.cs`, `MongoAggregationExpressionRenderer.cs` |
| Read side | `Visitors/MongoProjectionBinding*.cs`, `MongoMixedProjectionBinding...cs`, `NativeTranslation/MongoStreamingEntityMaterializerRewriter.cs`, `StreamingEligibility.cs`, `NativeDateTimeKindReadBack.cs`, `NativeAggregateReadBack.cs` |
| Fallback bridge | `Visitors/MongoEFToLinqTranslatingExpressionVisitor*.cs` |
| Runtime values | `RuntimeClock.cs`, `NonDeterministicCalls.cs`, `NativeTranslation/RuntimeParameterValues.cs`, `NativeQueryParameter.cs` |
| Docs | `Query/AGENTS.md` (invariants); `BREAKING-CHANGES.md`; `tests/.../SpecificationTests/AGENTS.md` |
