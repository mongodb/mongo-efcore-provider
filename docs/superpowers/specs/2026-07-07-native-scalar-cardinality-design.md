# Native scalar cardinality — SP4 design

**Date:** 2026-07-07 · **Branch:** `EF-SP4-scalar-cardinality` (off the EF-332 tip `4574384`) · **Epic:** EF-322 (native LINQ query rewrite) · **Roadmap:** sub-project #4

> Reviewer: read this for the *what* and *why* of native scalar cardinality. Cross-refs: the epic overview `2026-06-23-native-query-provider-overview.md`, the foundation design `2026-06-23-native-query-provider-design.md` (§ Sub-project 4), and the predicate-breadth / projection-pushdown / layer-separation specs for the machinery this builds on.

## TL;DR

- Push the scalar cardinality / aggregate operators server-side on the native path instead of falling back to driver-LINQ: `Count`/`LongCount`/`First`/`FirstOrDefault`/`Single`/`SingleOrDefault`/`Any`/`All`/`Sum`/`Min`/`Max`/`Average`.
- Two mechanisms, split by result shape:
  - **Entity reducers** (`First*`/`Single*`) synthesize a `$limit` and reuse the existing entity shaper unchanged; EF Core's base cardinality reduction supplies the exact LINQ semantics.
  - **Scalar aggregates** (`Count`/`Any`/`All`/`Sum`/`Min`/`Max`/`Average`) get new `$count`/`$group`/`$limit` IR stages and a new DOM scalar shaper, plus an explicit empty-input contract.
- Retire the two cutouts the roadmap names: the `ExecuteScalar` short-circuit in `MongoClientWrapper.Execute` and the `resultCardinality != Enumerable → null` gate in `TryBuildNativeFactory`.
- Zero regressions: every shape the native path cannot express falls back to driver-LINQ (throws under `NativeOnly`), exactly as today. `DriverLinq` mode restores the previous path wholesale.
- Scalar shaper is **DOM now** (single tiny result document); one-pass streaming for scalars is deferred to SP7 (materializer perf). Consistent with the roadmap sequencing.

## Scope

**In (native-representable):**

- **Entity reducers:** `First`, `FirstOrDefault`, `Single`, `SingleOrDefault` (no-argument forms — EF's `QueryableMethodNormalizingExpressionVisitor` rewrites the predicate overloads to `Where(pred).First()` before the QMTEV sees them; if a predicate still arrives it is pushed into the predicate slot via the existing translator, or the query falls back).
- **Scalar aggregates:** `Count`, `LongCount`, `Any`, `All`, `Sum`, `Min`, `Max`, `Average`.
- Aggregate **selectors** limited to a plain top-level member access (`Sum(x => x.Price)` → `"$price"`). No-selector forms over an already-projected scalar sequence are in scope where the source scalar resolves to a single field.

**Out (falls back to driver-LINQ, unchanged; throws under `NativeOnly`):**

- `Contains`, `ElementAt`/`ElementAtOrDefault`, `Last`/`LastOrDefault` (sort reversal) — deferred, consistent with the current code that already treats them specially.
- Computed / non-member-access aggregate selectors (`Sum(x => x.Price * x.Qty)`, method-call selectors) — part of the computed long tail, out of scope here.
- `All` predicates whose negation is not natively renderable.
- Any reducer where a paging slot (`Take`/`Skip` + `Limit`) is already populated — conservative fall back rather than compose limits.
- Composite-PK key access (existing strict spike-parity limitation).

## Background — how a scalar result is reduced today

`MongoClientWrapper.Execute<T>` returns `IEnumerable<T>`. For a non-enumerable result it short-circuits into `ExecuteScalar<T>`, which runs the captured driver-LINQ expression (whose tail already contains the user's `.First()`/`.Single()`/`.Count()` call) via `IMongoQueryProvider.Execute<T>` and wraps the single value in a **one-element array** `[result]`.

That one-element contract matters: **EF Core's base `ShapedQueryCompilingExpressionVisitor` applies the cardinality reduction** (`.First()`/`.Single()`/`.SingleOrDefault()`/…) over the `IEnumerable<T>` the provider returns. The provider therefore does not wrap the enumerable itself — it just has to return an enumerable that reduces correctly. This is the single most important fact the design leans on, and it is verified first (see Testing).

Two enforcement points keep cardinality on the driver-LINQ path today, both of which SP4 relaxes:

1. `MongoClientWrapper.Execute` — `if (Cardinality != ResultCardinality.Enumerable) return ExecuteScalar<T>(...)` runs *before* the `NativePipeline` block, so a non-enumerable result can never take the native path.
2. `MongoShapedQueryCompilingExpressionVisitor.TryBuildNativeFactory` — `if (resultCardinality != ResultCardinality.Enumerable) return null` (null factory ⇒ driver-LINQ). Scalar aggregates that reduce to a non-entity value additionally route through `VisitProjectedQuery` → the `ExecuteProjectedQuery` push-down.

## Design

### Mechanism A — entity reducers (`First*` / `Single*`)

The reducers return an *entity*, so they reuse the existing entity streaming/DOM shaper with no shaper changes.

- The QMTEV `TranslateFirstOrDefault` / `TranslateSingleOrDefault` overrides (currently inert `=> null`) populate a new cardinality descriptor on `MongoSelectDefinition` recording the **reducer kind** and set `Select.Limit`:
  - First-family → `$limit: 1`.
  - Single-family → `$limit: 2` (so the base `.Single()`/`.SingleOrDefault()` reduction observes the second row and throws "Sequence contains more than one element").
- The route stays `NativeRoute.WholeEntity`; the entity path in `CompileShapedQuery` is used as-is, except the streaming-eligibility gate — which currently requires `ResultCardinality.Enumerable` — is relaxed to also allow the two reducer cardinalities. (Streaming ≤2 rows is fine; the base reduction consumes the enumerable.)
- The synthesized `$limit` flows through the existing `MongoSelectLowerer` → `MongoLimitStage` → `MongoPipelineFactory` path with no new stage type.
- Empty / multiple-element semantics (including exact exception messages) come entirely from EF's base reduction over the returned rows. The provider adds **no** reduction logic.

**Guard:** if `Select.Limit` is already populated when the reducer is bound (a preceding `Take`), mark not-representable and fall back rather than reconcile two limits.

### Mechanism B — scalar aggregates (`Count`/`Any`/`All`/`Sum`/`Min`/`Max`/`Average`)

These return a *scalar* and need a new server stage plus a new shaper.

**New IR.** `MongoSelectDefinition` gains an aggregate descriptor carrying: the aggregate operator, an optional selector (`MongoExpression` field ref), and the empty-input behavior (below). `Route` gains a new value `NativeRoute.ScalarAggregate`; the computed `Route` returns it when the aggregate descriptor is populated (and no unsupported-operator flag was set).

**New stages** (`NativeTranslation/Stages/`), rendered by `MongoPipelineFactory`'s stage-walk:

| Operator | Terminal stage(s) appended after `$match`/`$sort`/`$skip`/`$limit` |
|---|---|
| `Count` / `LongCount` | `{ $count: "v" }` |
| `Sum` | `{ $group: { _id: null, v: { $sum: <selector> } } }` |
| `Min` / `Max` / `Average` | `{ $group: { _id: null, v: { $min\|$max\|$avg: <selector> } } }` |
| `Any` | `{ $limit: 1 }` |
| `All(pred)` | `{ $match: ¬pred }`, `{ $limit: 1 }` |

`<selector>` is the field ref (`"$price"`) or `1`-equivalent where there is no selector. `All`'s `¬pred` is the negation of the predicate rendered through the existing renderer; if the predicate (or its negation) is not natively renderable, fall back.

**New DOM scalar shaper.** Per the scalar-shaper decision, read the single `v` element from the result `BsonDocument` and deserialize to the result type (reuse `BsonSerializerFactory` / the value serializer). No streaming reader in SP4.

**Empty-input contract.** MongoDB returns *zero* documents for `$count`/`$group` over an empty match, and zero-or-one for the `$limit:1` shapes — but the base reduction expects exactly one element (the `[result]` contract). So the native scalar enumerable must yield **exactly one** value, synthesizing the empty case per operator:

| Operator | Server returned a row | Server returned no row (empty input) |
|---|---|---|
| `Count` / `LongCount` | `v` | `0` |
| `Sum` | `v` | `0` (typed zero, incl. nullable numeric → `0`, not `null`) |
| `Any` | `true` | `false` |
| `All(pred)` | `true` (no ¬pred row survived) | `true` (vacuously true over empty) |
| `Min` / `Max` / `Average`, non-nullable element | `v` | throw `InvalidOperationException("Sequence contains no elements")` |
| `Min` / `Max` / `Average`, nullable element | `v` | `null` |

The empty behavior is carried on the aggregate descriptor and applied where the cursor yields no rows (the existing `_onZeroResults` seam in `QueryingEnumerable`, or an equivalent empty-value factory on the native scalar path). `Any`/`All` need only presence, not the `v` value; `Sum`/`Count` synthesize a typed zero; `Min`/`Max`/`Average` either throw or yield `null` by nullability of the element type. The exception message matches BCL `Enumerable` so spec-suite exception assertions pass.

### Gate & cutout changes

- **`MongoSelectDefinition`** — add the cardinality/aggregate descriptor and the `NativeRoute.ScalarAggregate` route value; `Route` resolves reducers to `WholeEntity` (with `Limit` set) and aggregates to `ScalarAggregate`.
- **QMTEV** — the reducer and aggregate `Translate*` overrides populate the new IR via a new `NativeCardinalityBinder` (mirroring how `TranslateSelect` delegates to `NativeProjectionBinder`), else `MarkNotNativelyRepresentable()`. Add all twelve operators to `IsNativeRepresentableSlotOperator` so the `PopulateNativeSlots` catch-all does not clobber their `Translate*` decision. The overrides still leave `CapturedExpression` intact so the driver-LINQ fallback keeps working when not representable.
- **`MongoClientWrapper.Execute`** — gate the `ExecuteScalar` short-circuit on `NativePipeline == null` so a native scalar/reducer pipeline runs through the `NativePipeline` branch; the driver `ExecuteScalar` path is unchanged for the fallback.
- **`TryBuildNativeFactory`** — relax the `resultCardinality != Enumerable → null` gate so a representable reducer/aggregate builds a native factory; non-representable cardinality still returns null (fallback) / throws under `NativeOnly`.
- **`MongoShapedQueryCompilingExpressionVisitor`** — entity reducers flow through the existing `CompileShapedQuery` entity path (streaming gate relaxed for reducer cardinalities). Scalar aggregates are intercepted in `VisitProjectedQuery` when `Route == NativeRoute.ScalarAggregate`, *before* the `ThrowIfNativeOnlyForbidsFallback` guard and the `ExecuteProjectedQuery` push-down — emitting the native scalar pipeline and DOM scalar shaper (paralleling the existing `NativeRoute.Projection` interception).

## Data flow (aggregate example: `Set<Order>().Where(o => o.Cust == c).Sum(o => o.Total)`)

```
QMTEV: Where → predicate slot; Sum(o=>o.Total) → NativeCardinalityBinder:
        aggregate={op:Sum, selector:"$total", empty:ZeroTyped}; Route=ScalarAggregate
Gate  : Route==ScalarAggregate → native scalar path (not ExecuteProjectedQuery)
Lower : [$match {cust:…}], [$group {_id:null, v:{$sum:"$total"}}]
Render: MongoPipelineFactory template (predicate value → placeholder)
Build : factory.Build(params) → BsonDocument[]
Exec  : MongoClientWrapper.Execute (NativePipeline branch) → cursor
Shape : DOM scalar shaper reads `v`; zero rows → typed 0
Reduce: EF base .Single() over the one-element enumerable → the scalar
```

## Testing & verification

1. **Verify the base-reduction assumption first** (the design's load-bearing fact): a focused test that a native reducer/aggregate returns the correct single value/exception, confirming EF's base applies the reduction over our `IEnumerable<T>`. If this proves false, the design adds an explicit reduction wrapper — decide before building further.
2. **`NativeOnly` is the only reliable "went native" signal.** For each in-scope operator assert it *succeeds* under `NativeOnly`; for each out-of-scope shape assert it *throws* `NativeTranslationNotSupportedException`. MQL-shape assertions alone cannot prove native vs fallback for the reducer `$limit` shapes.
3. **MQL assertions** for the new `$count` / `$group` / `$limit` stages via `AssertMql`.
4. **Empty-input matrix** — one test per row of the empty-input contract table (empty match): `Count`→0, `Sum`→0, `Any`→false, `All`→true, `Min`/`Max`/`Average` non-nullable→throws with the BCL message, nullable→null.
5. **Reducer semantics** — `First`/`FirstOrDefault` empty (throw / default), `Single`/`SingleOrDefault` empty and >1 (throw / default / throw).
6. **Zero-regression sweep** — `MONGODB_EF_NATIVE_ONLY=1` spec suite shows the in-scope operators moving green with no previously-passing test regressing; EF8/EF9/EF10 all build and pass.

Unit tests: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/`. Gate/end-to-end: `QueryModeGate*` functional tests + spec suite.

## Boundaries & non-goals

- No streaming scalar reader (SP7).
- No change to the driver-LINQ fallback path — it remains the safety net for every out-of-scope shape.
- No new public API. `MongoQueryMode` options are unchanged.
- Does not touch the dormant `$lookup`/collection-Include machinery (SP5).
- Emitted MQL and which execution path a supported query takes are implementation details, not contract (per AGENTS.md versioning rubric) — this sub-project changing them is not a breaking change.

## Risks

- **Base-reduction assumption** (mitigated by test 1 running before the build proceeds).
- **Empty-input semantics divergence** from BCL for `Min`/`Max`/`Average` exception messages — mitigated by matching `Enumerable`'s message text and asserting it.
- **`All` predicate negation** correctness — mitigated by falling back when the negation is not cleanly renderable.
- **Cache invalidation** — changing a previously-translatable tree's output invalidates EF's compiled-query cache; acceptable and expected for a coverage-expanding sub-project.
