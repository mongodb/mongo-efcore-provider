---
area: Query / LINQ translation
scope: ["src/MongoDB.EntityFrameworkCore/Query/**"]
reviewer-agent: query-reviewer
adjacent-areas: [Storage, Metadata, Serializers, "C# driver LINQ v3"]
---

# Query — AGENTS.md

## Scope

Translates `IQueryable<T>` into aggregation pipelines. **Two paths**, chosen at compile time by
`MongoQueryMode`:

1. **Native (default).** Provider builds the `BsonDocument[]` pipeline itself from a structured query tree,
   runs it via `IMongoCollection<>.Aggregate`.
2. **Driver-LINQ (fallback).** Everything outside the native slice goes to the driver's LINQ v3 provider.

**Which path a query takes, the exact MQL emitted, and the exception type for an unsupported shape are not
contract.** Keep per-feature history out of this file — it belongs in git and tests.

## Pipeline at a glance

```
IQueryable<T>  (EF Core)
   │
   ▼  MongoQueryCompilationContext           (preserves original LINQ tree; carries MongoQueryMode)
   ▼  MongoQueryTranslationPreprocessor      (hoist final predicates; fold closed Where over a local Contains collection; lift VectorSearch out before nav expansion)
   ▼  MongoQueryableMethodTranslatingExpressionVisitor (QMTEV)
   │      ├─ accepts only Queryable / MongoQueryableExtensions / MongoDB.Driver.Linq.MongoQueryable
   │      ├─ delegates slot population to NativeSlotPopulator, projection binding to NativeProjectionBinder
   │      └─ always also captures the raw method chain (CapturedExpression) for the fallback
   ▼  MongoQueryTranslationPostprocessor     (apply final ProjectionMapping)
   ▼  MongoShapedQueryCompilingExpressionVisitor  ── THE GATE (compile time, honors MongoQueryMode) ──
   │   if Native & Select.Route != Fallback & lower/render succeed → NATIVE:
   │      MongoSelectLowerer  : PipelineOps → MongoPipelineStage[]
   │      MongoQueryLanguageRenderer + PlaceholderTable : predicate → $match BSON; params → sentinels
   │      MongoPipelineFactory.Create : render once into a template + placeholder table
   │      streaming shaper if eligible, else DOM
   │   else → DRIVER-LINQ FALLBACK: MongoEFToLinqTranslatingExpressionVisitor rewrites CapturedExpression
   ▼  MongoExecutableQuery + QueryingEnumerable → MongoClientWrapper.Execute → IAsyncCursor → shaper
```

Template is built once at compile time; only `factory.Build(parameterValues)` runs per execution.
Native-vs-driver and streaming-vs-DOM are compile-time-deterministic.

## Key entry points

| File | Responsibility |
|---|---|
| `MongoQueryTranslationPreprocessor` | Extracts `VectorSearch(...)` before nav-expansion, re-inserts after (nav expansion crashes on it). |
| `Visitors/MongoQueryableMethodTranslatingExpressionVisitor` | LINQ-method dispatcher; allowed-method-source set; delegates slot/projection work; captures final chain; carries bulk `ExecuteUpdate`/`ExecuteDelete`. |
| `Visitors/MongoShapedQueryCompilingExpressionVisitor` | The gate. `ClassifyNativeDisposition` → `{Native, Fallback, HardDecline}` from `Select.Route`, `IsGroupByFallbackUnsafe`, unbound vector search. Streamability is a separate axis. |
| `Expressions/MongoSelectDefinition` | Native logical IR: ordered `PipelineOps` (+`TrailingOps` once a set op attaches), `Projection`, `Cardinality`, `Grouping`, `VectorSearch`. `Route` is the authoritative is-native signal. Dialect-neutral. |
| `Expressions/MongoQueryExpression` (+`.Lookup.cs`) | Has-a `MongoSelectDefinition`, cross-collection `$lookup` state, retained `CapturedExpression`/`ProjectionMapping` for fallback. |
| `Expressions/MongoExpression` + subtypes | Dialect-agnostic node hierarchy; `VisitChildren` is a no-op, every consumer hand-rolls a `switch`. |
| `NativeTranslation/MongoExpressionTranslator` | EF predicate/key/value body → `MongoExpression`; returns `false` outside its acceptance set. |
| `NativeTranslation/MongoSelectLowerer` | Native IR → `MongoPipelineStage[]` (BSON-free); owns lookup-eligibility guards. |
| `NativeTranslation/MongoQueryLanguageRenderer` | Renders **query dialect** (`$match`); keeps `&&`/`\|\|` at query level, wraps only non-expressible subtrees in `$expr` for indexability. |
| `NativeTranslation/MongoAggregationExpressionRenderer` | Renders **aggregation-expression dialect** (`$expr`/`$project`) — field-to-field comparisons, arithmetic. |
| `NativeTranslation/MongoPipelineFactory` | Stages → `BsonDocument[]` template + placeholder table; normalizes paging (`NormalizePagingStages`). |
| `NativeTranslation/Native{SlotPopulator,ProjectionBinder,CardinalityBinder,GroupByBinder,SelectManyBinder,JoinScope*}` | Per-shape recognizers populating the IR. |
| `NativeTranslation/MongoStreamingEntityMaterializerRewriter` + `StreamingEligibility` | One-pass forward-only `IBsonReader` → POCO; deserialization *is* materialization. |
| `Visitors/MongoProjectionBindingRemovingExpressionVisitor` (+`Mixed…`) | DOM read side; `Mixed` sibling reads whole un-projected documents (fallback/mixed leg). |
| `Query/MongoIncludeFixups` | `Include` navigation fix-up shared by both read paths. |
| `ExpressionExtensionMethods` | Shared expression-shape helpers. |

## Durable invariants

Rules that cost real bugs to learn. Breaking one usually produces **silently wrong rows**, not a failure.

- **Every string constant/parameter rendered in the aggregation dialect is `$literal`-wrapped** — an unwrapped
  `"$..."` string is read as a field path, so user input like `"$PasswordHash"` compares against that field.
  `MongoAggregationExpressionRenderer.RenderBranch` always wraps (branches, constructed members);
  `RenderOperand` wraps string/array/document constants and every parameter (comparison, `$concat`, `$in`,
  string-operator operands). A new operand position must go through one of them.
- **Post-terminal gating.** After a terminal operator (`GroupBy`, `Distinct`, a set op, `SelectMany` unwind —
  `HasTerminalOperator`), every later operator must decide explicitly whether it can still go native. Any
  operator with its own `Translate*` override bypasses the shared gates and must replicate the check itself.
- **No driver-LINQ oracle for some shapes**: `SelectMany` over a reference collection, `Intersect`/`Except`,
  the correlated-reducer projection leaf, `Reverse`. These hard-fail (return `null`) in every `MongoQueryMode`
  rather than falling back — don't mark them non-native.
- **Scope resolves by parameter identity, never member name.** Route by `ReferenceEquals` against the scope's
  parameter (shared property names across types is the regression test). Shared primitive:
  `MongoExpressionTranslator.TryBeginOwnedHopWalk`.
- **Multi-scope join projections.** A trailing `Select` over a `Joins.Count >= 2` chain is native only for a
  whole-entity leaf or a leaf resolving to exactly one chain scope; multi-scope or nested wrapped leaves decline.
- **Navigation-less joins** are native-eligible iff `RebindInnerShaperToOuterQuery`'s raw-key branch resolved
  both keys (`JoinInfo.Lookup != null`). A navigation resolved to the *wrong* target (its `$lookup` doesn't
  reproduce the written simple key equality) is discarded and rebuilt by that raw-key branch; a non-simple key
  keeps it and still declines (`JoinLookupImplementsKeySelectors`). Raw-key fields use
  `LookupExpression.GetFieldPath` (a composite-PK component lives at `_id.<Name>`). See `NativeJoinTests`,
  `NativeCompositeKeyJoinTests`.
- **Paging ahead of a join's confirming `Select`.** EF Core hoists `Skip`/`Take`/`Where`/`OrderBy` ahead of the
  join's result selector; the recorded `PipelineOps` are deferred to run after the join, unless the join is in
  the left-outer-reference-navigation "safe to page before `$lookup`" set. Reducers there decline. Paging
  recorded before any join, ahead of a join that may multiply rows (collection navigation or navigation-less),
  stays ahead of the `$lookup`; if paging was also recorded after the join, the query declines. Paging recorded
  *between* two joins of a chain has no native position (the deferred snapshot runs after every `$lookup`), so it
  declines too (`HasPagingRecordedBetweenJoins`).
- **GroupBy over a join scope** (`MongoSelectDefinition.GroupByJoinScope`, decided purely in `TranslateGroupBy`):
  requires a `TransparentIdentifier` key parameter (a result selector that projected one side must not resolve
  by name against the root entity); the chain is confirmed only at the binder's commit point. Paging recorded
  ahead of a non-1:1 grouped join declines — deferring it past the `$lookup` (fine for projections) silently
  changes group counts. A finalized grouping or Distinct takes precedence over a confirmed join's inner
  access; a non-nullable key part or `$min`/`$max`/`$avg` accumulator that may read an unmatched left-outer
  join side declines rather than answer a plausible default, as does an accumulator condition that may (`$expr`
  orders null below every value), as does any key part or accumulator operand computed over that side unless
  every operator in it propagates missing as EF's null (an allow-list, `MongoGroupElementTranslator
  .PropagatesMissingAsNull`: `$max` skips the missing value). See `NativeGroupByOverJoinTests`.
- **Post-group operators after a keyed GroupBy.Select resolve by the Select's output alias**
  (`MongoProjectedAliasScope`), never key-part name or entity property; only `Where` is native (ordering/paging
  after the grouped Select decline). A pushed list (`g.Select(e => e.X).ToList()`/`ToArray()` → `$push`) is
  null-safe for a nullable/reference element (missing or null both read back as `null`); entity elements decline.
- **Set ops form a tree; each `Union`'s dedup belongs to its own link, never hoisted.**
  `MongoSelectDefinition.SetOperations` is an ordered list where an operand may itself carry a link, so
  whole-entity `Concat`/`Union` nests both directions. Right-nesting (`A.Concat(B.Union(C))`) cannot be
  flattened into a left chain without changing results. Ops recorded between two links land in `TrailingOps`
  (routed there once a set op attaches) but belong to the *new* link as `PrecedingOps` — `AppendSetOperation`
  moves them. `Intersect`/`Except` are excluded from nesting entirely. See `NativeSetOpsTests`.
- **Structural classification beats metadata/depth/CLR-type shortcuts.** Query filters, join-hop depth, and
  self-referencing entity types can all defeat shortcuts — walk the actual tree. Mode-independent;
  `DriverLinq` is not an escape hatch for a misclassification.
- **The negator's contract is exact complement or decline, never approximation.** `$eq`/`$ne` may be inverted
  (they partition every value including missing/null); the four relational operators must be `$not`-wrapped,
  never inverted (they don't partition missing/null). Ask whether the *rendered* pair partitions the value
  space.
- **A negated `&&`/`||` is exactly De Morgan'd via `MongoExpressionNegator`, or declines** — never wrapped in an
  aggregation `$not`. The dialects differ on missing vs. null, on relational ordering of null/missing, and on
  implicit array-element matching, so a `$not` wrap silently changes rows.
- **The aggregation dialect orders null and missing below every value**, so a bare `$lt`/`$lte` (or a `$gt`/`$gte`
  whose right side is null) answers true where C# lifted semantics answer false. `MongoAggregationExpressionRenderer`
  conjoins `$gt: [<lower side>, null]` whenever `MayBeNull` says the lower operand may be null (CLR type, a query
  parameter, or a null-propagating operator over one). That covers HAVING, `$cond` inside `$group`, `$project`
  ternaries and element-scoped `$filter`/`$map` predicates (the guard reads `$$e.<field>`). A value whose CLR type is
  non-nullable but may be *missing* (an unmatched left-join side) gets no guard, so conditions over it must decline.
- **A composite `$group` `_id` omits a missing sub-key**, so `"_id.<Name>"` reads as missing, not null
  (`$eq: [missing, null]` is false), and a missing part and a null part form two groups. `MongoPipelineFactory
  .RenderCompositeKeyPart` `$ifNull`-normalizes every part that may be null, once, for every later read. A nullable
  key over an unmatched join side (`(decimal?)o.Total`) is made null-safe at bind time (its translated field is
  non-nullable), and key-only accumulator conditions, which read the raw field, use `NullSafeKeyRead`.
- **Negator ↔ dialect classifier ↔ both renderers must agree**, enforced by
  `MongoExpressionNodeCoverageTests` (reflection-discovers every `MongoExpression` subtype, checks all seven
  dispatchers). The matrix is keyed by node type, so it's blind to shape-conditional dispatch (e.g.
  `MongoRegexExpression` differs for constant vs. field-to-field `Term`) — those need a dedicated test.
- **`$expr` inside `$elemMatch` is a hard server error.** Anything nesting inside `$elemMatch` must be rejected
  at `IsQueryDialectRenderable`, not fall through to the aggregation renderer.
- **`$ifNull` around `$size`/`$filter` is mandatory** — `$size` against a missing/null array is a hard server
  error, not a wrong answer.
- **A non-default-serialized bool is truthiness-tested and answers the wrong boolean.**
  `HasConversion<string>()` bools store `"True"`/`"False"` — both truthy. Gate `$not`, `$and`/`$or` operands,
  and bare boolean predicate roots via `MongoExpressionTranslator.IsUnsafeTruthinessRoot`.
- **`$toLower`/`$toUpper` are never emitted natively** (ASCII-only). In a `Where` a case mapping becomes an
  anchored case-insensitive regex; in a `Select` the raw string is projected and the mapping re-applied
  client-side. After such a leaf (`MongoSelectDefinition.HasClientCaseMappingProjectionLeaf`), value-reading
  operators decline — the server would read the unmapped value. A member-less construction holding one
  (`new KeyValuePair<,>(x.S.ToUpper(), x.T)`) declines natively when the mixed reader can bind its arguments per member
  (`TryMatchConstructorArgumentMembers`), since its index-based positional shaper can't be read off whole documents.
  Otherwise (parameters naming no member) it stays native and the positional shaper re-applies the call; under
  DriverLinq that shape is pushed down, and the driver rejects the constructor. A grouped result member (or bare body)
  projecting one (`g.Key.ToUpper()`, `g.Max(x => x.S).ToLower()`) stages the receiver in the `$group` output and the
  result shaper re-applies the call (`NativeGroupByBinder.PeelResultMemberCaseMapping`). Any other case mapping in a
  grouped query declines natively, and driver-LINQ would compute it with `$toUpper`/`$toLower`, so every read path about
  to run a grouped query on driver-LINQ throws if one appears anywhere in its `GroupBy` key/element/result selectors, in
  an operator composed after it (accumulator lambdas included), or in any lambda of any operator anywhere in an
  ungrouped source it groups or combines with, through every source argument (a `Select`/`Where`/`OrderBy` before it,
  either side of a set op or join) (`ThrowIfDriverLinqCaseMapsGroupedResult`, via
  `NativeGroupByBinder.ContainsCaseMappingCall`). That also declines shapes the driver would answer correctly (a
  fallen-back `Where(x => x.S.ToUpper() == "ÉCOLE")` before a `GroupBy`, native otherwise); accepted, as released
  versions threw on every GroupBy. Ungrouped queries are untouched.
- **String operators that don't propagate null/missing are null-guarded at render**
  (`MongoAggregationExpressionRenderer.NullPropagating`: `$substrCP` answers `""`, `$strLenCP` and a `$indexOfCP`
  needle error), so a member-style call on a null receiver is null as in EF; `$concat` coalesces each
  possibly-null operand to `""` (C# concatenation). Guarded only when `MayBeNull`, which must see the guarded node.
  A non-nullable-typed value read of a guarded `Length`/`IndexOf`, also through arithmetic/casts/conditionals
  (projection leaf, group key, `$min`/`$max`/`$avg`/`$push` operand, terminal `Min`/`Max`/`Average`), declines
  (`MayBeNullBehindNonNullableType`) rather than read null as `0`. First/LastOrDefault is
  null only as a comparison operand (`RenderComparisonOperand`); a projected value stays `'\0'`.
- **The mixed reader reads a computed scalar leaf whole.** Operand-by-operand binding registers every operand under
  the leaf's one projection member, so each reads as the last bound (`S.IndexOf(T)` as `T.IndexOf(T)`). Outside a
  native projection (and in a native computed leaf's client form) `MongoProjectionBindingExpressionVisitor
  .IsClientComputedLeaf` registers the leaf whole, and the mixed reader re-evaluates it client-side over its own
  property reads (`TryBindClientComputedLeaf`) as written: C#, except that a string instance call on a null receiver
  answers null where its result is nullable, and a nullable cast (or a reference-typed leaf) answers null when a
  document read it null-propagates from is null; a non-nullable value over a null throws, as EF does. The admitted
  node set is explicit (`ClientLeafChecker`: constants, property reads incl. owned hops, string/value-type members,
  string/`Math`/value-type methods, operators, conversions, conditionals); anything else keeps the operand walk. A
  member-less constructor's arguments (`new KeyValuePair<,>(a, b)`) are bound under the members their parameters
  initialize, for the same reason. Where they can't be (parameters naming no member, or an initializer assigning one),
  arguments registering different expressions under the one enclosing member are recorded
  (`MongoQueryExpression.AliasedConstructionMembers`) and the mixed reader declines them loudly.
- **Top-level aggregation `$eq`/`$ne` against null is `$ifNull`-wrapped** (missing ≡ null, as in the query
  dialect and .NET), but not inside a `$filter`/`$map` element scope, where driver-LINQ keeps them distinct.
- **A `NativeComputedLeafExpression` is read whole only by the native alias reader**; every other shaper visitor
  (mixed/DOM reads, driver-LINQ push-down, projection analysis) must use its `ClientExpression`.
- **A node needing different handling at 3+ call sites should be a sealed sibling type, not a bool flag** (e.g.
  `MongoFilteredSizeExpression` beside `MongoSizeExpression`) — a flag leaves sites wrong by default.
- **Element-scoped children must not be prefixed.** `MongoFilteredSizeExpression`/`MongoElemMatchExpression`/
  `MongoQuantifierExpression` bind their own `$filter`/`$elemMatch` variable; `MongoFieldPrefixRewriter`
  prefixes only the array path and declines (never throws) on an unhandled node kind.
- **Mixed-projection alias agreement.** The native shaper is alias-addressed at translation time, but a
  fallback reads whole documents. An array/owned-nav-entity projection leaf's alias must equal the
  navigation's own document path, or a fallback silently returns an empty collection. See
  `NativeArrayProjectionTests`.
- **Guards that decline a shape often become routers, not walls** — a rejection check can later become the
  signal a feature uses to pick between two strategies. Check before loosening one.
- **A recognizer must not mutate then decline.** Stage into locals, commit only once every gate passes
  (`NativeProjectionBinder`'s commit block is the pattern).
- **Scalar DateTime read-back must honour the property's configured `DateTimeKind`.** `NativeDateTimeKindReadBack`
  traces a read-back (shaper alias read, `$group` key, `$min`/`$max`/`$first`/`$last` accumulator, terminal
  scalar) to the backing `IProperty` and reads it through `BsonSerializerFactory`'s property-aware serializer
  (incl. via `MongoElementRefExpression.ValueProperty`). A DateTime-typed value it can't trace to a property fails
  closed and declines, and so does a leaf read back whole through a class map (a composite `g.Key`'s `_id`) whose
  DateTime members come from a kind-sensitive source, since the class map ignores the configured kind.
  `NativeGroupByBinder` rebuilds such a key as a document construction so each part reads kind-aware; only a key
  shape it can't rebuild reaches the decline. A set op reads every combined row through the outer side's leaves, so
  it declines when the operands' leaves differ in kind shape at any kind-sensitive position (including a rebuilt
  key paired with the same key type read whole by class map). Either way, no silently wrong `Kind`/ticks from a
  generic read.
  Deliberately not a `HasDefaultKeySerialization` change — that gate's inputs stay converter + `BsonRepresentation`
  (adding a kind check there would over-decline correct filters over Local-kind properties). Server-side date computations
  (`.Hour`, `.Date`, etc.) over Local-kind properties still compute in UTC on both paths — tracked as EF-459, out
  of scope here.

## Boundaries with adjacent areas

- **vs Storage.** Query produces data (a pipeline or driver-LINQ expression); `MongoClientWrapper.Execute(...)`
  owns execution, cursors, sessions, retries. Never call `IMongoCollection<>.Aggregate(...)` from Query.
- **vs Storage — bulk `ExecuteUpdate`/`ExecuteDelete` crosses as behavior, not data** (`MongoBulkPlan` carries
  translation delegates invoked per execution, since bulk translation is parameter-value-dependent).
- **vs Serializers.** Ask `BsonSerializerFactory` for an `IBsonSerializer`; never instantiate one here.
- **vs Metadata.** Query reads `GetElementName()`/`GetBsonRepresentation()`/`GetDiscriminator*()`, never writes.
- **vs driver LINQ v3.** Only the fallback uses it; we do not build on the driver's internal AST.

## Common pitfalls

- **Known gap: driver-side case mapping the grouped-query decline doesn't reach.** `ThrowIfDriverLinqCaseMapsGroupedResult`
  walks the operator chain only, so a `GroupBy` inside a lambda (a correlated subquery) isn't searched. Chains with no
  `GroupBy` are never declined, so ungrouped driver-LINQ shapes that consume a case mapping (a fallen-back
  `Distinct`/set op over a case-mapped `Select`, `x.S.ToUpper().Length`, `Where(x => x.S.ToUpper() == x.T)`) still run
  `$toUpper`/`$toLower`.

- **MQL shape cannot prove a query went native.** Native and fallback pipelines are often structurally
  identical for filter/sort/paging. The only reliable signal is `NativeOnly` mode.
- **Allowed method sources are strict** — `Queryable`, `MongoQueryableExtensions`,
  `MongoDB.Driver.Linq.MongoQueryable`, plus EF8/EF9's internal `LeftJoin` shim (admitted unconditionally by
  `MethodInfo` identity — no per-call signal survives nav-expansion to distinguish its two source shapes).
- **VectorSearch extraction ordering** — pulled out before nav-expansion, stitched back after; re-check if you
  change preprocessor ordering.
- **`CapturedExpression` must be complete** — set once at the tail of `VisitMethodCall`; setting mid-chain
  truncates the query.
- **`ProjectionMapping` keys must match exactly** what the shaper expects — a mismatch is silent wrong results.
- **`MethodInfo` reference-equality** needs canonical constants (`QueryableMethods`, `EnumerableMethods`); open
  vs. constructed generics compare unequal.
- **Unsupported shapes call `MarkNotNativelyRepresentable()`**, driving `Route` to `Fallback` rather than
  silently emitting a pipeline that drops the operator.
- **Adding a native operator touches two places**: a slot-style operator needs both
  `NativeSlotPopulator.PopulateNativeSlots` and `IsNativeRepresentableSlotOperator`; a `Translate*`-override
  operator needs both the override and `IsNativeRepresentableSlotOperator`.
- **QMTEV's `Translate{Where,OrderBy,ThenBy,Skip,Take}` overrides are inert (`=> null`)** — slot population is
  delegated at the `VisitMethodCall` fall-through instead. Don't route these through the switch without first
  removing their `NativeSlotPopulator` handling, or slots double-populate.
- **The lowerer is BSON-free**; keep `BsonDocument` construction in the renderer/factory only.
- **Non-positive paging** — MongoDB rejects `{$limit: 0}`, so `Build` rewrites it to an always-false `$match`;
  negative `$limit`/`$skip` throw `ArgumentOutOfRangeException`. Normalization covers top level and `$unionWith` inner pipelines, not `$lookup`
  sub-pipelines.
- **TPH `$lookup` joins must narrow by discriminator** when the target is a derived type, or sibling-subtype
  rows leak in (`LookupExpression`'s constructor does this).
- **EF Core query cache** — compiled queries are cached by expression-tree shape; changing a translator's
  output for a previously-translatable tree quietly invalidates user caches.
- **Visitor signatures differ across EF8/EF9/EF10** — guard with `#if` (see root `AGENTS.md`).

## How to test

- **Proving native.** Run under `MongoQueryMode.NativeOnly`, assert success (or throws for a shape that should
  fall back). `MONGODB_EF_NATIVE_ONLY=1` flips every spec context to `NativeOnly`.
- **Differential correctness.** For a native shape that can change results, prefer a `[Theory]` asserting the
  native result equals an in-memory LINQ oracle over ragged/missing/null/empty fixtures — see
  `NativeOwnedCollectionAllTests`, `NativeOwnedCollectionCountTests`, `NativeModeAssert`.
- **MQL baselines are generated** (`EF_TEST_REWRITE_BASELINES`) — see SpecificationTests `AGENTS.md`.
- Unit: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/` (native under `NativeTranslation/`).
- Functional: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/`.

```bash
dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build --filter "FullyQualifiedName~Query"
```
