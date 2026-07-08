# Native Scalar Cardinality (SP4) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Push scalar cardinality / aggregate operators (`First*`/`Single*`/`Count`/`LongCount`/`Any`/`All`/`Sum`/`Min`/`Max`/`Average`) server-side on the native query path, retiring the `ExecuteScalar` and `resultCardinality != Enumerable` cutouts, with zero regressions (every unsupported shape falls back to driver-LINQ).

**Architecture:** Two mechanisms split by result shape. **Entity reducers** synthesize a `$limit` into the existing `Select.Limit` slot and reuse the existing entity shaper unchanged — EF Core's base `ShapedQueryCompilingExpressionVisitor` supplies the cardinality reduction over the returned `IEnumerable<T>`. **Scalar aggregates** get new `$count`/`$group` stage IR, a DOM scalar shaper reading a single `v` field, and an explicit empty-input contract that yields exactly one element (matching the driver path's `[result]` contract the base reduction relies on).

**Tech Stack:** C# / .NET (net8.0 EF8·EF9, net10.0 EF10), EF Core provider internals, MongoDB C# driver (BSON/aggregate only — no driver-LINQ on the native path), xUnit + plain `Assert.*` (no FluentAssertions in tests).

## Global Constraints

- Preserve file BOMs on every edited/created file.
- `src/` is `<Nullable>enable</Nullable>` — annotate all new types.
- Multi-EF: code must build under `Debug EF8`, `Debug EF9`, `Debug EF10`. Use `#if EF8 || EF9` / `#if !EF8` guards only where an API differs (none expected in this plan, but `GetParameterValues` already bridges EF10 `Parameters` vs EF8/EF9 `ParameterValues`).
- All new query code lives under `src/MongoDB.EntityFrameworkCore/Query/`; follow the existing NativeTranslation patterns (BSON-free lowerer, renderer/factory own BSON).
- Copyright header (Apache 2.0, "Copyright 2023-present MongoDB Inc.") on every new file — copy verbatim from any existing file in the same folder.
- Internal-only surface: every new type/member is `internal` (no public API changes; this sub-project adds none).
- The **only reliable "went native" test signal is `MongoQueryMode.NativeOnly`** — a representable shape *succeeds*, a fall-back shape *throws* `NativeTranslationNotSupportedException`. MQL shape alone cannot prove native vs fallback for `$limit`-shaped queries.
- Build one EF version: `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"`. Test one class: `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build --filter "FullyQualifiedName~ClassName"`.
- Commit after every task (frequent commits). Do **not** push or squash — the user drives that (stacked-PR workflow).

---

## File Structure

**New files:**
- `src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoCardinality.cs` — the cardinality/aggregate IR descriptor + enums (reducer kind, aggregate operator, empty-input behavior).
- `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeCardinalityBinder.cs` — populates the cardinality descriptor from the QMTEV `Translate*` overrides (mirrors `NativeProjectionBinder`).
- `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/Stages/MongoCountStage.cs` — `$count` stage IR.
- `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/Stages/MongoGroupAccumulatorStage.cs` — `$group{_id:null,v:{<acc>:…}}` stage IR.

**Modified files:**
- `src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoSelectDefinition.cs` — add `Cardinality` slot + `NativeRoute.ScalarAggregate`; update `Route`.
- `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeSlotPopulator.cs` — add the twelve cardinality operators to `IsNativeRepresentableSlotOperator`.
- `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs` — reducer/aggregate `Translate*` overrides delegate to `NativeCardinalityBinder`.
- `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoSelectLowerer.cs` — lower the aggregate slot to the new stages.
- `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoPipelineFactory.cs` — render the new stages.
- `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoShapedQueryCompilingExpressionVisitor.cs` — relax the cardinality gate (`TryBuildNativeFactory` + streaming gate), intercept `ScalarAggregate` in `VisitProjectedQuery`, add the scalar-aggregate executor.
- `src/MongoDB.EntityFrameworkCore/Storage/MongoClientWrapper.cs` — gate the `ExecuteScalar` short-circuit on `NativePipeline == null`.
- `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md` — document the new native slice.

**Test files:**
- `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoCardinalityRouteTests.cs`
- `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/AggregateStageRenderingTests.cs`
- `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeCardinalityTests.cs` (reducers + aggregates, incl. `NativeOnly` and empty-input matrix)

---

## Task 1: Cardinality IR descriptor + `ScalarAggregate` route

**Files:**
- Create: `src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoCardinality.cs`
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoSelectDefinition.cs`
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoCardinalityRouteTests.cs`

**Interfaces:**
- Produces:
  - `enum MongoReducerKind { First, FirstOrDefault, Single, SingleOrDefault }`
  - `enum MongoAggregateOperator { Count, LongCount, Sum, Min, Max, Average, Any, All }`
  - `enum MongoEmptyAggregateBehavior { DefaultValue, ReturnNull, Throw }`
  - `sealed class MongoCardinality` with `Reducer` (nullable `MongoReducerKind`), `Aggregate` (nullable `MongoAggregateOperator`), `Selector` (`MongoExpression?`), `EmptyBehavior` (`MongoEmptyAggregateBehavior`), `EmptyValue` (`object?`), `ResultType` (`Type`).
  - `MongoSelectDefinition.Cardinality` get/set (`MongoCardinality?`)
  - `NativeRoute.ScalarAggregate` enum member
  - `Route` returns `ScalarAggregate` when `Cardinality?.Aggregate` is set (and not `_hasUnsupportedOperator`).

- [ ] **Step 1: Write the failing test**

Create `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoCardinalityRouteTests.cs` (copy the copyright header + `using` style from a sibling unit test):

```csharp
using System;
using MongoDB.EntityFrameworkCore.Query.Expressions;
using Xunit;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query.NativeTranslation;

public class MongoCardinalityRouteTests
{
    [Fact]
    public void No_cardinality_routes_whole_entity()
        => Assert.Equal(NativeRoute.WholeEntity, new MongoSelectDefinition().Route);

    [Fact]
    public void Reducer_with_limit_routes_whole_entity()
    {
        var select = new MongoSelectDefinition
        {
            Limit = new MongoConstantExpression(1, forSerialization: null),
            Cardinality = new MongoCardinality { Reducer = MongoReducerKind.First, ResultType = typeof(object) }
        };
        Assert.Equal(NativeRoute.WholeEntity, select.Route);
    }

    [Fact]
    public void Aggregate_routes_scalar_aggregate()
    {
        var select = new MongoSelectDefinition
        {
            Cardinality = new MongoCardinality
            {
                Aggregate = MongoAggregateOperator.Count,
                EmptyBehavior = MongoEmptyAggregateBehavior.DefaultValue,
                EmptyValue = 0,
                ResultType = typeof(int)
            }
        };
        Assert.Equal(NativeRoute.ScalarAggregate, select.Route);
    }

    [Fact]
    public void Unsupported_operator_beats_aggregate()
    {
        var select = new MongoSelectDefinition
        {
            Cardinality = new MongoCardinality { Aggregate = MongoAggregateOperator.Count, ResultType = typeof(int) }
        };
        select.MarkNotNativelyRepresentable();
        Assert.Equal(NativeRoute.Fallback, select.Route);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --filter "FullyQualifiedName~MongoCardinalityRouteTests"`
Expected: FAIL — `MongoCardinality` / `NativeRoute.ScalarAggregate` do not exist (compile error).

- [ ] **Step 3: Create the descriptor file**

Create `src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoCardinality.cs` (copyright header + BOM):

```csharp
using System;

namespace MongoDB.EntityFrameworkCore.Query.Expressions;

/// <summary>The cardinality reducer applied to an entity result.</summary>
internal enum MongoReducerKind { First, FirstOrDefault, Single, SingleOrDefault }

/// <summary>The scalar aggregate applied to a query.</summary>
internal enum MongoAggregateOperator { Count, LongCount, Sum, Min, Max, Average, Any, All }

/// <summary>What a scalar aggregate yields when the server returns no rows (empty input).</summary>
internal enum MongoEmptyAggregateBehavior { DefaultValue, ReturnNull, Throw }

/// <summary>
/// Native-translation IR for a terminal cardinality / aggregate operator. Exactly one of
/// <see cref="Reducer"/> (entity reducers: First/Single) or <see cref="Aggregate"/> (scalar aggregates)
/// is set. Populated by <c>NativeCardinalityBinder</c> from the QMTEV; read by the gate, lowerer, and shaper.
/// </summary>
internal sealed class MongoCardinality
{
    /// <summary>Reducer kind for entity reducers; null for scalar aggregates.</summary>
    public MongoReducerKind? Reducer { get; set; }

    /// <summary>Aggregate operator for scalar aggregates; null for entity reducers.</summary>
    public MongoAggregateOperator? Aggregate { get; set; }

    /// <summary>The aggregate selector field ref (Sum(x=>x.Price) → "$price"), or null.</summary>
    public MongoExpression? Selector { get; set; }

    /// <summary>How the scalar path resolves an empty result set.</summary>
    public MongoEmptyAggregateBehavior EmptyBehavior { get; set; }

    /// <summary>The typed value yielded on empty input when <see cref="EmptyBehavior"/> is DefaultValue.</summary>
    public object? EmptyValue { get; set; }

    /// <summary>The CLR result type of the terminal operator.</summary>
    public Type ResultType { get; set; } = typeof(object);
}
```

- [ ] **Step 4: Wire the slot + route into `MongoSelectDefinition`**

In `MongoSelectDefinition.cs`, add after the Projection region (around line 107):

```csharp
    // ── Cardinality / aggregate ───────────────────────────────────────────────────

    /// <summary>
    /// The terminal cardinality reducer or scalar aggregate, or <see langword="null"/> for a plain
    /// enumerable result. Set by <c>NativeCardinalityBinder</c>.
    /// </summary>
    public MongoCardinality? Cardinality { get; set; }
```

Update the `Route` property (lines 129-132) to:

```csharp
    internal NativeRoute Route
        => _hasUnsupportedOperator ? NativeRoute.Fallback
            : Cardinality?.Aggregate != null ? NativeRoute.ScalarAggregate
            : _projections.Count > 0 ? NativeRoute.Projection
            : NativeRoute.WholeEntity;
```

Add the enum member to `NativeRoute` (after `Projection`):

```csharp
    /// <summary>Native pipeline ending in a scalar aggregate ($count / $group) producing a single value.</summary>
    ScalarAggregate
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --filter "FullyQualifiedName~MongoCardinalityRouteTests"`
Expected: PASS (4 tests).

- [ ] **Step 6: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoCardinality.cs \
        src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoSelectDefinition.cs \
        tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoCardinalityRouteTests.cs
git commit -m "EF-SP4: Cardinality IR descriptor + ScalarAggregate route"
```

---

## Task 2: Entity reducers (First / FirstOrDefault / Single / SingleOrDefault)

This task also **verifies the plan's load-bearing assumption** — that EF's base reduction runs over the `IEnumerable<T>` returned by `ExecuteShapedQuery`. The reducer functional tests (empty→throw, >1→throw) passing *is* that verification. If they fail with a wrong type/no reduction, STOP and re-evaluate (the design's fallback is an explicit `Enumerable.First/Single` wrapper in `CompileShapedQuery`).

**Files:**
- Create: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeCardinalityBinder.cs`
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs` (`TranslateFirstOrDefault` line 631, `TranslateSingleOrDefault` line 842)
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeSlotPopulator.cs` (`IsNativeRepresentableSlotOperator`)
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoShapedQueryCompilingExpressionVisitor.cs` (`TryBuildNativeFactory` line ~405; streaming gate line ~277)
- Modify: `src/MongoDB.EntityFrameworkCore/Storage/MongoClientWrapper.cs` (`Execute` line 92)
- Test: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeCardinalityTests.cs`

**Interfaces:**
- Consumes: `MongoCardinality`, `MongoReducerKind`, `MongoSelectDefinition.Cardinality`, `MongoConstantExpression`.
- Produces:
  - `NativeCardinalityBinder.TryBindReducer(MongoQueryExpression mongoQ, MongoReducerKind kind, Type resultType) : bool` — sets `Select.Limit` (1 for First*, 2 for Single*) and `Select.Cardinality`; returns false (caller marks non-native) when a limit is already present.

- [ ] **Step 1: Write the failing test**

Create `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeCardinalityTests.cs`. Model it on existing functional query tests (look at a sibling in that folder for the `TemporaryDatabaseFixture` / context-setup pattern and `AssertMql` helper). Seed a small collection and assert reducer behavior under `Native` and `NativeOnly`:

```csharp
// (headers/usings per sibling tests; class uses the shared functional fixture)
[Fact]
public void First_returns_first_and_goes_native()
{
    using var db = CreateContext(seed: new[] { 1, 2, 3 }, mode: MongoQueryMode.NativeOnly);
    var first = db.Entities.OrderBy(e => e.Value).First();
    Assert.Equal(1, first.Value); // succeeds under NativeOnly ⇒ went native
}

[Fact]
public void First_on_empty_throws_sequence_contains_no_elements()
{
    using var db = CreateContext(seed: Array.Empty<int>(), mode: MongoQueryMode.Native);
    var ex = Assert.Throws<InvalidOperationException>(() => db.Entities.First());
    Assert.Contains("no elements", ex.Message);
}

[Fact]
public void FirstOrDefault_on_empty_returns_null()
{
    using var db = CreateContext(seed: Array.Empty<int>(), mode: MongoQueryMode.Native);
    Assert.Null(db.Entities.FirstOrDefault());
}

[Fact]
public void Single_with_two_matches_throws_more_than_one()
{
    using var db = CreateContext(seed: new[] { 5, 5 }, mode: MongoQueryMode.Native);
    var ex = Assert.Throws<InvalidOperationException>(() => db.Entities.Where(e => e.Value == 5).Single());
    Assert.Contains("more than one", ex.Message);
}

[Fact]
public void SingleOrDefault_on_empty_returns_null()
{
    using var db = CreateContext(seed: Array.Empty<int>(), mode: MongoQueryMode.Native);
    Assert.Null(db.Entities.SingleOrDefault());
}

[Fact]
public void First_after_Take_falls_back()
{
    using var db = CreateContext(seed: new[] { 1, 2, 3 }, mode: MongoQueryMode.NativeOnly);
    // Limit already populated by Take ⇒ reducer not representable ⇒ NativeOnly throws.
    Assert.Throws<NativeTranslationNotSupportedException>(() => db.Entities.Take(2).First());
}
```

> Implementer note: reuse the existing functional-test entity/fixture rather than inventing `CreateContext`/`Entities` if a suitable one exists in the folder; the assertions (values, exception messages, NativeOnly succeed/throw) are the contract.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --filter "FullyQualifiedName~NativeCardinalityTests"`
Expected: FAIL — reducers currently fall back (`NativeOnly` cases throw where they should succeed / vice-versa).

- [ ] **Step 3: Create `NativeCardinalityBinder` (reducer part)**

Create `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeCardinalityBinder.cs`:

```csharp
using System;
using MongoDB.EntityFrameworkCore.Query.Expressions;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>
/// Populates <see cref="MongoSelectDefinition.Cardinality"/> from the QMTEV's cardinality/aggregate
/// Translate overrides (mirrors <see cref="NativeProjectionBinder"/>). Returns false when the operator
/// is not natively representable; the caller then marks the query non-native.
/// </summary>
internal static class NativeCardinalityBinder
{
    internal static bool TryBindReducer(MongoQueryExpression mongoQ, MongoReducerKind kind, Type resultType)
    {
        var select = mongoQ.Select;

        // A user Take/Skip already populated the limit slot; composing a reducer limit on top is not
        // representable in canonical order. Fall back rather than reconcile two limits.
        if (select.Limit != null)
            return false;

        var limit = kind is MongoReducerKind.Single or MongoReducerKind.SingleOrDefault ? 2 : 1;
        select.Limit = new MongoConstantExpression(limit, forSerialization: null);
        select.Cardinality = new MongoCardinality { Reducer = kind, ResultType = resultType };
        return true;
    }
}
```

- [ ] **Step 4: Wire the QMTEV reducer overrides**

In `MongoQueryableMethodTranslatingExpressionVisitor.cs`, replace the inert `TranslateFirstOrDefault` (line ~631) and `TranslateSingleOrDefault` (line ~842). EF passes `returnDefault` and (possibly) a `predicate`; if a predicate is present, push it into the predicate slot first — but EF's normalizer usually rewrites predicate overloads to `Where(pred).First()`, so a non-null predicate here is the uncommon case: mark non-native and fall back.

```csharp
protected override ShapedQueryExpression? TranslateFirstOrDefault(
    ShapedQueryExpression source, LambdaExpression? predicate, Type returnType, bool returnDefault)
{
    var mongoQ = (MongoQueryExpression)source.QueryExpression;
    if (predicate != null
        || !NativeCardinalityBinder.TryBindReducer(mongoQ,
               returnDefault ? MongoReducerKind.FirstOrDefault : MongoReducerKind.First, returnType))
    {
        mongoQ.Select.MarkNotNativelyRepresentable();
    }
    return base.TranslateFirstOrDefault(source, predicate, returnType, returnDefault);
}
```

Mirror for `TranslateSingleOrDefault` with `Single`/`SingleOrDefault`. Match the exact override signature already present in the file (it currently returns `null`; keep whatever base call yields the entity shaper — verify against the existing override parameter list before editing).

> Note: the base call must still produce the entity-shaped `ShapedQueryExpression` with the correct `ResultCardinality`. Confirm `base.TranslateFirstOrDefault` returns a usable shaped query (the current override returns `null` to force driver-LINQ; we now want the entity path). If base returns null, construct the reshaped source the same way the entity path expects — inspect how `ResultCardinality` is set at line ~152 (`GetResultCardinality`) which already runs after the override in `VisitMethodCall`.

- [ ] **Step 5: Add the reducers to the native whitelist**

In `NativeSlotPopulator.IsNativeRepresentableSlotOperator`, add (matching by `QueryableMethods` constants):

```csharp
           || methodDefinition == QueryableMethods.FirstWithoutPredicate
           || methodDefinition == QueryableMethods.FirstOrDefaultWithoutPredicate
           || methodDefinition == QueryableMethods.SingleWithoutPredicate
           || methodDefinition == QueryableMethods.SingleOrDefaultWithoutPredicate
```

(Use the exact `QueryableMethods` member names — verify in the EF Core `QueryableMethods` class. The `*WithPredicate` variants are normalized away, but if a predicate overload can reach here, leaving it off the whitelist means the catch-all correctly marks it non-native.)

- [ ] **Step 6: Relax the cardinality gate**

In `MongoShapedQueryCompilingExpressionVisitor.TryBuildNativeFactory` (line ~405), replace the unconditional `if (resultCardinality != ResultCardinality.Enumerable) return null;` so a representable reducer/aggregate is allowed. The authoritative signal is `Select.Route != NativeRoute.Fallback`; the old cardinality check becomes redundant for representable shapes. Change to only bail when the route is `Fallback` (the existing `Route == Fallback` check just below already handles that) — i.e. **delete** the `resultCardinality != Enumerable → null` early return, and let the `Route` check gate. Re-read the method body first; keep the vector-search and lookup-streamability checks intact.

In the streaming-gate expression (line ~277) relax to allow reducer cardinalities:

```csharp
        var streaming = allowStreaming
            && nativeFactory != null
            && shapedQueryExpression.ResultCardinality != ResultCardinality.Enumerable
                is false // placeholder — see below
```

Actually replace the `== ResultCardinality.Enumerable` clause with a helper allowing Enumerable **or** a reducer cardinality:

```csharp
            && IsStreamableCardinality(shapedQueryExpression.ResultCardinality)
```

and add:

```csharp
    private static bool IsStreamableCardinality(ResultCardinality cardinality)
        => cardinality is ResultCardinality.Enumerable
            or ResultCardinality.Single or ResultCardinality.SingleOrDefault;
```

- [ ] **Step 7: Retire the `ExecuteScalar` cutout for native reducers**

In `MongoClientWrapper.Execute<T>` (line 92), gate the short-circuit on the absence of a native pipeline:

```csharp
        if (executableQuery.Cardinality != ResultCardinality.Enumerable
            && executableQuery.NativePipeline is null)
            return ExecuteScalar<T>(executableQuery);
```

Now a native reducer (Cardinality = Single, `NativePipeline` set) flows into the `NativePipeline` block and returns the cursor enumerable; EF's base reduction reduces it.

- [ ] **Step 8: Run tests to verify they pass**

Run: `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --filter "FullyQualifiedName~NativeCardinalityTests"`
Expected: PASS (reducer tests). If `First_on_empty` / `Single_with_two_matches` do NOT throw, the base-reduction assumption is wrong — STOP and add the explicit reduction wrapper (see task preamble).

- [ ] **Step 9: Guard against regressions**

Run: `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build --filter "FullyQualifiedName~Query"`
Expected: no previously-passing query test regresses.

- [ ] **Step 10: Commit**

```bash
git add -A
git commit -m "EF-SP4: Native entity reducers (First/Single) + retire ExecuteScalar/cardinality cutouts"
```

---

## Task 3: Aggregate stage IR + lowering + rendering

**Files:**
- Create: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/Stages/MongoCountStage.cs`
- Create: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/Stages/MongoGroupAccumulatorStage.cs`
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoSelectLowerer.cs`
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoPipelineFactory.cs`
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/AggregateStageRenderingTests.cs`

**Interfaces:**
- Consumes: `MongoCardinality`, `MongoAggregateOperator`, `MongoExpression`, `MongoConstantExpression`, `MongoFieldExpression`, `MongoAggregationExpressionRenderer`, `PlaceholderTable`.
- Produces:
  - `sealed class MongoCountStage : MongoPipelineStage` with `string OutputField` (= `"v"`).
  - `sealed class MongoGroupAccumulatorStage : MongoPipelineStage` with `string Accumulator` (`"$sum"`/`"$min"`/`"$max"`/`"$avg"`), `MongoExpression Operand`, `string OutputField` (= `"v"`).
  - Rendering: `{ $count: "v" }` and `{ $group: { _id: null, v: { <acc>: <operand> } } }`.

- [ ] **Step 1: Write the failing test**

Create `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/AggregateStageRenderingTests.cs`:

```csharp
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Query.Expressions;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation.Stages;
using Xunit;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query.NativeTranslation;

public class AggregateStageRenderingTests
{
    private static BsonDocument[] Render(params MongoPipelineStage[] stages)
        => MongoPipelineFactory.Create(stages, new MongoQueryLanguageRenderer())
            .Build(new System.Collections.Generic.Dictionary<string, object?>());

    [Fact]
    public void Count_renders_count_stage()
    {
        var result = Render(new MongoCountStage("v"));
        Assert.Equal(BsonDocument.Parse("{ $count: 'v' }"), result[0]);
    }

    [Fact]
    public void Sum_renders_group_stage()
    {
        var result = Render(new MongoGroupAccumulatorStage("$sum",
            new MongoFieldExpression("price", /* matching MongoFieldExpression ctor */ null!), "v"));
        Assert.Equal(BsonDocument.Parse("{ $group: { _id: null, v: { $sum: '$price' } } }"), result[0]);
    }
}
```

> Implementer: match the real `MongoFieldExpression` constructor (inspect the type — it carries element name + serializer/type info). Use the field-ref form the aggregation renderer already emits (`"$price"`).

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --filter "FullyQualifiedName~AggregateStageRenderingTests"`
Expected: FAIL — stage types don't exist.

- [ ] **Step 3: Create the stage types**

`MongoCountStage.cs` (copyright + BOM):

```csharp
namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation.Stages;

/// <summary>Represents a <c>$count</c> aggregation stage producing a single count document.</summary>
internal sealed class MongoCountStage : MongoPipelineStage
{
    public MongoCountStage(string outputField) => OutputField = outputField;

    /// <summary>The output field name holding the count (conventionally "v").</summary>
    public string OutputField { get; }
}
```

`MongoGroupAccumulatorStage.cs`:

```csharp
using MongoDB.EntityFrameworkCore.Query.Expressions;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation.Stages;

/// <summary>
/// Represents a single-accumulator <c>$group</c> over the whole input (<c>_id: null</c>) — the shape used
/// by Sum/Min/Max/Average. Produces one document <c>{ _id: null, &lt;OutputField&gt;: { &lt;acc&gt;: &lt;operand&gt; } }</c>.
/// </summary>
internal sealed class MongoGroupAccumulatorStage : MongoPipelineStage
{
    public MongoGroupAccumulatorStage(string accumulator, MongoExpression operand, string outputField)
    {
        Accumulator = accumulator;
        Operand = operand;
        OutputField = outputField;
    }

    /// <summary>The MQL accumulator operator ("$sum" / "$min" / "$max" / "$avg").</summary>
    public string Accumulator { get; }

    /// <summary>The value expression fed to the accumulator (field ref, or a constant for count-style sums).</summary>
    public MongoExpression Operand { get; }

    /// <summary>The output field name (conventionally "v").</summary>
    public string OutputField { get; }
}
```

- [ ] **Step 4: Render the stages in `MongoPipelineFactory`**

Add cases to `RenderStage` (line ~80):

```csharp
            MongoCountStage count => new BsonDocument("$count", count.OutputField),
            MongoGroupAccumulatorStage group => RenderGroup(group, placeholders),
```

and add the helper:

```csharp
    private static BsonDocument RenderGroup(MongoGroupAccumulatorStage stage, PlaceholderTable placeholders)
        => new BsonDocument("$group", new BsonDocument
        {
            { "_id", BsonNull.Value },
            { stage.OutputField, new BsonDocument(
                stage.Accumulator, MongoAggregationExpressionRenderer.Render(stage.Operand, placeholders)) }
        });
```

> Note: `ValidatePagingStages` only inspects `$limit`/`$skip`, so `$count`/`$group` pass through untouched. Confirm no `$limit` validation trips for the `Any`/`All` `$limit:1` (1 > 0, fine).

- [ ] **Step 5: Lower the aggregate slot**

In `MongoSelectLowerer.Lower`, after the `$project` block (line ~92) add aggregate lowering. Any/All use `$limit:1` (All also needs the negated predicate as an extra `$match` — but predicate negation is bound into the predicate slot in Task 4, so here only the terminal stage is emitted):

```csharp
        // 7. Scalar aggregate terminal stage ($count / $group / $limit for Any/All).
        var cardinality = select.Cardinality;
        if (cardinality?.Aggregate is { } aggregate)
        {
            stages.Add(aggregate switch
            {
                MongoAggregateOperator.Count or MongoAggregateOperator.LongCount
                    => new MongoCountStage("v"),
                MongoAggregateOperator.Sum
                    => new MongoGroupAccumulatorStage("$sum", cardinality.Selector!, "v"),
                MongoAggregateOperator.Min
                    => new MongoGroupAccumulatorStage("$min", cardinality.Selector!, "v"),
                MongoAggregateOperator.Max
                    => new MongoGroupAccumulatorStage("$max", cardinality.Selector!, "v"),
                MongoAggregateOperator.Average
                    => new MongoGroupAccumulatorStage("$avg", cardinality.Selector!, "v"),
                MongoAggregateOperator.Any or MongoAggregateOperator.All
                    => new MongoLimitStage(new MongoConstantExpression(1, forSerialization: null)),
                _ => throw new NativeTranslationNotSupportedException(
                    $"Unsupported aggregate operator '{aggregate}'.")
            });
        }
```

Add the needed `using MongoDB.EntityFrameworkCore.Query.NativeTranslation.Stages;` (already present) and confirm `MongoConstantExpression` is reachable (it's in `Expressions`, already imported).

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --filter "FullyQualifiedName~AggregateStageRenderingTests"`
Expected: PASS (2 tests).

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "EF-SP4: Aggregate stage IR ($count/$group) + lowering + rendering"
```

---

## Task 4: Aggregate binder + QMTEV overrides

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeCardinalityBinder.cs`
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs` (`TranslateCount`/`TranslateLongCount`/`TranslateAny`/`TranslateAll`/`TranslateSum`/`TranslateMin`/`TranslateMax`/`TranslateAverage`, lines 567-596)
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeSlotPopulator.cs` (`IsNativeRepresentableSlotOperator`)
- Test: extend `MongoCardinalityRouteTests.cs` (binder unit tests, no DB)

**Interfaces:**
- Consumes: `MongoExpressionTranslator` (`TryTranslateField`, `TryTranslate`), `MongoCardinality`, `MongoAggregateOperator`, `MongoEmptyAggregateBehavior`.
- Produces:
  - `NativeCardinalityBinder.TryBindAggregate(MongoQueryExpression mongoQ, MongoAggregateOperator op, LambdaExpression? selector, LambdaExpression? predicate, Type resultType) : bool`

- [ ] **Step 1: Write the failing test**

Add to `MongoCardinalityRouteTests.cs` — a binder-level test that a member-access selector binds and a computed selector does not:

```csharp
[Fact]
public void TryBindAggregate_member_selector_binds_field()
{
    // Arrange a MongoQueryExpression over a test entity type with a numeric "Price" property.
    // (Reuse the unit-test model helper used by other NativeTranslation unit tests to build a
    //  MongoQueryExpression / entity type; see MongoExpressionTranslator unit tests for the pattern.)
    // Assert TryBindAggregate(..., Sum, x=>x.Price, null, typeof(decimal)) == true
    //   and Select.Cardinality.Selector is a MongoFieldExpression for "price".
}

[Fact]
public void TryBindAggregate_computed_selector_returns_false()
{
    // Assert TryBindAggregate(..., Sum, x=>x.Price*2, null, typeof(decimal)) == false
}
```

> Implementer: if no lightweight `MongoQueryExpression` unit fixture exists, prefer covering the selector-binding contract through the functional aggregate tests in Task 5 instead and keep Task 4's unit test to the enum→empty-behavior mapping (below), which needs no model.

Add an unambiguous no-model test for the empty-behavior mapping helper:

```csharp
[Theory]
[InlineData(MongoAggregateOperator.Count, typeof(int))]
[InlineData(MongoAggregateOperator.Sum, typeof(long))]
public void EmptyBehavior_for_count_and_sum_is_default_value(MongoAggregateOperator op, System.Type t)
{
    var c = NativeCardinalityBinder.BuildEmptyBehavior(op, t, out var value, out var behavior);
    Assert.True(c);
    Assert.Equal(MongoEmptyAggregateBehavior.DefaultValue, behavior);
    Assert.NotNull(value);
}

[Fact]
public void EmptyBehavior_for_min_nonnullable_is_throw()
{
    NativeCardinalityBinder.BuildEmptyBehavior(MongoAggregateOperator.Min, typeof(int), out _, out var behavior);
    Assert.Equal(MongoEmptyAggregateBehavior.Throw, behavior);
}

[Fact]
public void EmptyBehavior_for_min_nullable_is_return_null()
{
    NativeCardinalityBinder.BuildEmptyBehavior(MongoAggregateOperator.Min, typeof(int?), out _, out var behavior);
    Assert.Equal(MongoEmptyAggregateBehavior.ReturnNull, behavior);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --filter "FullyQualifiedName~MongoCardinalityRouteTests"`
Expected: FAIL — `BuildEmptyBehavior`/`TryBindAggregate` don't exist.

- [ ] **Step 3: Add the aggregate binder methods**

Extend `NativeCardinalityBinder`:

```csharp
    internal static bool TryBindAggregate(
        MongoQueryExpression mongoQ,
        MongoAggregateOperator op,
        System.Linq.Expressions.LambdaExpression? selector,
        System.Linq.Expressions.LambdaExpression? predicate,
        Type resultType)
    {
        var select = mongoQ.Select;
        var translator = new MongoExpressionTranslator(mongoQ.CollectionExpression.EntityType);

        MongoExpression? operand = null;
        if (op is MongoAggregateOperator.Sum or MongoAggregateOperator.Min
               or MongoAggregateOperator.Max or MongoAggregateOperator.Average)
        {
            // Selector must be a plain member access → field ref. Computed selectors fall back.
            if (selector?.Body is not System.Linq.Expressions.MemberExpression
                || !translator.TryTranslateField(selector.Body, out operand))
                return false;
        }

        if (op is MongoAggregateOperator.All)
        {
            // All(pred) ≡ no element fails pred. Push the NEGATED predicate as a $match, then $limit:1;
            // presence of any surviving row ⇒ All is false. Requires a natively renderable negation.
            if (predicate is null)
                return false;
            var negated = System.Linq.Expressions.Expression.Not(predicate.Body);
            if (!translator.TryTranslate(negated, out var negatedNode))
                return false;
            select.AddPredicateConjunct(negatedNode);
        }
        else if (predicate != null)
        {
            // Count(pred)/Any(pred) — normalizer usually rewrites to Where+op, but handle defensively.
            if (!translator.TryTranslate(predicate.Body, out var predNode))
                return false;
            select.AddPredicateConjunct(predNode);
        }

        BuildEmptyBehavior(op, resultType, out var emptyValue, out var emptyBehavior);
        select.Cardinality = new MongoCardinality
        {
            Aggregate = op,
            Selector = operand,
            EmptyBehavior = emptyBehavior,
            EmptyValue = emptyValue,
            ResultType = resultType
        };
        return true;
    }

    // Maps each aggregate's empty-input semantics to the BCL LINQ contract.
    internal static bool BuildEmptyBehavior(
        MongoAggregateOperator op, Type resultType, out object? emptyValue, out MongoEmptyAggregateBehavior behavior)
    {
        emptyValue = null;
        switch (op)
        {
            case MongoAggregateOperator.Count:
                emptyValue = 0; behavior = MongoEmptyAggregateBehavior.DefaultValue; return true;
            case MongoAggregateOperator.LongCount:
                emptyValue = 0L; behavior = MongoEmptyAggregateBehavior.DefaultValue; return true;
            case MongoAggregateOperator.Any:
                emptyValue = false; behavior = MongoEmptyAggregateBehavior.DefaultValue; return true;
            case MongoAggregateOperator.All:
                emptyValue = true; behavior = MongoEmptyAggregateBehavior.DefaultValue; return true;
            case MongoAggregateOperator.Sum:
                // Sum over empty is 0 (typed), including nullable numeric → 0, not null.
                emptyValue = TypedZero(resultType); behavior = MongoEmptyAggregateBehavior.DefaultValue; return true;
            case MongoAggregateOperator.Min:
            case MongoAggregateOperator.Max:
            case MongoAggregateOperator.Average:
                behavior = System.Nullable.GetUnderlyingType(resultType) != null
                    ? MongoEmptyAggregateBehavior.ReturnNull
                    : MongoEmptyAggregateBehavior.Throw;
                return true;
            default:
                behavior = MongoEmptyAggregateBehavior.Throw; return false;
        }
    }

    private static object TypedZero(Type resultType)
    {
        var t = System.Nullable.GetUnderlyingType(resultType) ?? resultType;
        return System.Convert.ChangeType(0, t);
    }
```

- [ ] **Step 4: Wire the QMTEV aggregate overrides**

Replace the eight `ReshapeShaperExpression`-based overrides (lines 567-596) so they also attempt native binding, then fall through to the reshape (which keeps the driver-LINQ fallback shaper). Example for `TranslateCount`:

```csharp
protected override ShapedQueryExpression TranslateCount(ShapedQueryExpression source, LambdaExpression? predicate)
{
    var mongoQ = (MongoQueryExpression)source.QueryExpression;
    if (!NativeCardinalityBinder.TryBindAggregate(mongoQ, MongoAggregateOperator.Count, null, predicate, typeof(int)))
        mongoQ.Select.MarkNotNativelyRepresentable();
    return ReshapeShaperExpression(source, typeof(int));
}
```

Apply the same pattern to `TranslateLongCount` (`LongCount`, `typeof(long)`), `TranslateAny` (`Any`, selector null, predicate arg), `TranslateAll` (`All`, selector null, `predicate`), `TranslateSum`/`TranslateMin`/`TranslateMax`/`TranslateAverage` (pass `selector`, `resultType`). Keep the `ReshapeShaperExpression(source, resultType)` tail so a non-representable shape still produces the scalar binding for the fallback.

- [ ] **Step 5: Add the aggregate operators to the whitelist**

In `IsNativeRepresentableSlotOperator`, add the aggregate method definitions (verify exact `QueryableMethods` names):

```csharp
           || methodDefinition == QueryableMethods.CountWithoutPredicate
           || methodDefinition == QueryableMethods.LongCountWithoutPredicate
           || methodDefinition == QueryableMethods.AnyWithoutPredicate
           || methodDefinition == QueryableMethods.All
           || QueryableMethods.IsSumWithoutSelector(methodDefinition) || QueryableMethods.IsSumWithSelector(methodDefinition)
           || methodDefinition == QueryableMethods.MinWithoutSelector || methodDefinition == QueryableMethods.MinWithSelector
           || methodDefinition == QueryableMethods.MaxWithoutSelector || methodDefinition == QueryableMethods.MaxWithSelector
           || QueryableMethods.IsAverageWithoutSelector(methodDefinition) || QueryableMethods.IsAverageWithSelector(methodDefinition)
```

> The `Sum`/`Average` helpers are predicate-style checks, so `IsNativeRepresentableSlotOperator` may need to accept these via `||` calls rather than `==`. Keep the method signature returning `bool`.

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --filter "FullyQualifiedName~MongoCardinalityRouteTests"`
Expected: PASS.

Build all three EF versions to catch guard issues:
Run: `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF8"` and `-c "Debug EF9"` and `-c "Debug EF10"`.
Expected: all succeed.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "EF-SP4: Aggregate binder + QMTEV cardinality overrides"
```

---

## Task 5: Scalar-aggregate native path + DOM scalar shaper + empty-input contract

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoShapedQueryCompilingExpressionVisitor.cs` (`VisitProjectedQuery` line ~157; add `ExecuteAggregate<TResult>`)
- Test: extend `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeCardinalityTests.cs`

**Interfaces:**
- Consumes: `MongoCardinality`, `NativeRoute.ScalarAggregate`, `TranslateQuery<TEntity>`, `MongoPipelineFactory`, `BsonSerializerFactory`, `MongoClientWrapper.Execute<BsonDocument>`.
- Produces: `ExecuteAggregate<TResult>(...)` returning a one-element `IEnumerable<TResult>` (the empty-input contract), plus the compile-time intercept in `VisitProjectedQuery`.

- [ ] **Step 1: Write the failing test**

Extend `NativeCardinalityTests.cs` with the full aggregate + empty-input matrix:

```csharp
[Fact]
public void Count_goes_native_and_counts()
{
    using var db = CreateContext(seed: new[] { 1, 2, 3 }, mode: MongoQueryMode.NativeOnly);
    Assert.Equal(3, db.Entities.Count());
}

[Fact]
public void Count_on_empty_is_zero()
{
    using var db = CreateContext(seed: System.Array.Empty<int>(), mode: MongoQueryMode.Native);
    Assert.Equal(0, db.Entities.Count());
}

[Fact]
public void Sum_on_empty_is_zero()
{
    using var db = CreateContext(seed: System.Array.Empty<int>(), mode: MongoQueryMode.Native);
    Assert.Equal(0, db.Entities.Sum(e => e.Value));
}

[Fact]
public void Any_true_and_false()
{
    using var db = CreateContext(seed: new[] { 1 }, mode: MongoQueryMode.NativeOnly);
    Assert.True(db.Entities.Any());
    using var empty = CreateContext(seed: System.Array.Empty<int>(), mode: MongoQueryMode.Native);
    Assert.False(empty.Entities.Any());
}

[Fact]
public void All_over_empty_is_true()
{
    using var db = CreateContext(seed: System.Array.Empty<int>(), mode: MongoQueryMode.Native);
    Assert.True(db.Entities.All(e => e.Value > 0));
}

[Fact]
public void Min_on_empty_nonnullable_throws()
{
    using var db = CreateContext(seed: System.Array.Empty<int>(), mode: MongoQueryMode.Native);
    var ex = Assert.Throws<System.InvalidOperationException>(() => db.Entities.Min(e => e.Value));
    Assert.Contains("no elements", ex.Message);
}

[Fact]
public void Max_and_average_go_native()
{
    using var db = CreateContext(seed: new[] { 2, 4, 6 }, mode: MongoQueryMode.NativeOnly);
    Assert.Equal(6, db.Entities.Max(e => e.Value));
    Assert.Equal(4.0, db.Entities.Average(e => e.Value));
}

[Fact]
public void Computed_selector_sum_falls_back()
{
    using var db = CreateContext(seed: new[] { 1, 2 }, mode: MongoQueryMode.NativeOnly);
    Assert.Throws<NativeTranslationNotSupportedException>(() => db.Entities.Sum(e => e.Value * 2));
}

[Fact]
public void Count_emits_count_stage_mql()
{
    using var db = CreateContext(seed: new[] { 1 }, mode: MongoQueryMode.Native);
    _ = db.Entities.Count();
    AssertMql("{ \"$count\" : \"v\" }"); // adjust to the folder's AssertMql format
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --filter "FullyQualifiedName~NativeCardinalityTests"`
Expected: FAIL — aggregates still route to `ExecuteProjectedQuery` (driver-LINQ) or throw under `NativeOnly`.

- [ ] **Step 3: Intercept `ScalarAggregate` in `VisitProjectedQuery`**

In `MongoShapedQueryCompilingExpressionVisitor.VisitProjectedQuery`, before the `ThrowIfNativeOnlyForbidsFallback` guard (line ~186) and after the existing `NativeRoute.Projection` block, add:

```csharp
        if (queryMode != MongoQueryMode.DriverLinq
            && mongoQueryExpression.Select.Route == NativeRoute.ScalarAggregate)
        {
            var cardinality = mongoQueryExpression.Select.Cardinality!;
            return Expression.Call(null,
                ExecuteAggregateMethodInfo.MakeGenericMethod(rootEntityType.ClrType, cardinality.ResultType),
                QueryCompilationContext.QueryContextParameter,
                Expression.Constant(rootEntityType),
                Expression.Constant(_bsonSerializerFactory),
                Expression.Constant(mongoQueryExpression),
                Expression.Constant(_contextType),
                Expression.Constant(_threadSafetyChecksEnabled),
                Expression.Constant(cardinality));
        }
```

- [ ] **Step 4: Add `ExecuteAggregate<TEntity, TResult>` + its `MethodInfo`**

Add the executor. It builds the native pipeline via `TryBuildNativeFactory`/`TranslateQuery` exactly like the entity path but reads the single `v` field and applies the empty contract, returning a one-element enumerable (so EF's base `.Single()` reduction yields the scalar):

```csharp
private static IEnumerable<TResult> ExecuteAggregate<TEntity, TResult>(
    QueryContext queryContext,
    IReadOnlyEntityType entityType,
    BsonSerializerFactory bsonSerializerFactory,
    MongoQueryExpression queryExpression,
    Type contextType,
    bool threadSafetyChecksEnabled,
    MongoCardinality cardinality)
{
    // Build the native aggregate pipeline (mirrors CompileShapedQuery's factory build). ScalarAggregate is
    // only reached under Native/NativeOnly, and Route == ScalarAggregate guarantees representability, so a
    // native factory is always produced here.
    var factory = BuildAggregateFactory(queryExpression); // extract the lower+render used by TryBuildNativeFactory
    var (mongoQueryContext, executableQuery) = TranslateQueryForAggregate<TEntity>(
        queryContext, entityType, bsonSerializerFactory, queryExpression, factory);

    using var rows = mongoQueryContext.MongoClient
        .Execute<BsonDocument>(executableQuery, out var log).GetEnumerator();

    TResult value;
    if (rows.MoveNext())
    {
        var doc = rows.Current;
        // Any/All: presence of a surviving row. Any → true; All → false (a row failed the predicate).
        value = cardinality.Aggregate switch
        {
            MongoAggregateOperator.Any => (TResult)(object)true,
            MongoAggregateOperator.All => (TResult)(object)false,
            _ => DeserializeScalar<TResult>(doc, "v", bsonSerializerFactory, cardinality.ResultType)
        };
    }
    else
    {
        value = cardinality.EmptyBehavior switch
        {
            MongoEmptyAggregateBehavior.DefaultValue => (TResult)cardinality.EmptyValue!,
            MongoEmptyAggregateBehavior.ReturnNull => default!,
            MongoEmptyAggregateBehavior.Throw =>
                throw new InvalidOperationException("Sequence contains no elements"),
            _ => throw new InvalidOperationException("Sequence contains no elements")
        };
    }

    log();
    return new[] { value };
}
```

Register the `MethodInfo` next to `ExecuteShapedQueryMethodInfo` (line ~940):

```csharp
private static readonly MethodInfo ExecuteAggregateMethodInfo =
    typeof(MongoShapedQueryCompilingExpressionVisitor)
        .GetTypeInfo().GetDeclaredMethods(nameof(ExecuteAggregate)).Single();
```

> Implementer guidance:
> - **`BuildAggregateFactory` / `TranslateQueryForAggregate`**: reuse the existing lower→render→`MongoPipelineFactory.Create` sequence inside `TryBuildNativeFactory` and the `TranslateQuery<TEntity>` native branch (they already exist). Prefer calling `TryBuildNativeFactory(MongoQueryMode.Native, queryExpression, ResultCardinality.Single)` to get the factory, then `TranslateQuery<TEntity>(..., resultCardinality: ResultCardinality.Single, factory, streaming:false, translate: no-op)` — the `translate` delegate is unused on the native branch. Do not duplicate the lowerer logic; extract a small private helper if needed.
> - **`DeserializeScalar<TResult>`**: read `doc["v"]` and convert to `TResult` using the same value-deserialization the projection DOM shaper uses (see `BsonValueSerializer` / `BsonSerializerFactory`). For `Count`/`LongCount` the field is a BSON int32/int64; for `Sum`/`Min`/`Max`/`Average` it matches the accumulator output — coerce to `cardinality.ResultType`.
> - The one-element-array return preserves the `[result]` contract the base reduction depends on (identical to `ExecuteScalar`).

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --filter "FullyQualifiedName~NativeCardinalityTests"`
Expected: PASS (all reducer + aggregate + empty-input tests).

- [ ] **Step 6: Build all EF versions**

Run: `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF8"`, `-c "Debug EF9"`, `-c "Debug EF10"`.
Expected: all succeed.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "EF-SP4: Scalar-aggregate native path + DOM scalar shaper + empty-input contract"
```

---

## Task 6: Full regression sweep + documentation

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md` (document the new native slice)
- Test: whole suite, all three EF versions; the `MONGODB_EF_NATIVE_ONLY=1` spec sweep.

- [ ] **Step 1: Run the full query suite on EF10**

Run: `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build --filter "FullyQualifiedName~Query"`
Expected: green; no previously-passing test regresses. Investigate any new failure before proceeding.

- [ ] **Step 2: Run the native-only spec sweep**

Run: `MONGODB_EF_NATIVE_ONLY=1 dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build --filter "FullyQualifiedName~SpecificationTests"`
Expected: the in-scope cardinality/aggregate spec tests now pass under `NativeOnly` where they previously threw; no spec test that passed before now fails. Record the before/after pass delta.

- [ ] **Step 3: Run the full suite on EF8 and EF9**

Run: `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF8" --filter "FullyQualifiedName~Query"` and the same for `Debug EF9`.
Expected: green.

- [ ] **Step 4: Update `Query/AGENTS.md`**

In the "As-built scope" note, add scalar cardinality to the native slice: entity reducers (`First`/`FirstOrDefault`/`Single`/`SingleOrDefault`) via synthesized `$limit` + base reduction; scalar aggregates (`Count`/`LongCount`/`Any`/`All`/`Sum`/`Min`/`Max`/`Average`) via `$count`/`$group` + DOM scalar shaper + the empty-input contract. Note what still falls back (`Contains`, `ElementAt`, `Last`, computed selectors, non-negatable `All` predicates, reducer-after-`Take`). Note the two retired cutouts (`ExecuteScalar` short-circuit now gated on `NativePipeline == null`; the `resultCardinality != Enumerable` gate relaxed). Add `NativeCardinalityBinder`, `MongoCardinality`, `MongoCountStage`, `MongoGroupAccumulatorStage` to the key-entry-points list. Keep the "MQL shape cannot prove native" pitfall accurate for the new `$limit`/`$count` shapes.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "EF-SP4: Docs + full regression sweep for native scalar cardinality"
```

---

## Self-Review notes (for the executor)

- **Base-reduction assumption** is verified in Task 2 Step 8 — if it fails, both reducers and aggregates need the explicit `Enumerable`-reduction wrapper; the aggregate path already yields exactly one element, so only reducers would change.
- **Empty-input contract** rows are each covered by a Task 5 functional test (`Count`/`Sum`/`Any`/`All`/`Min`-nonnullable; add `Min`-nullable-null and `Average`-empty if the fixture allows nullable numeric properties).
- **`QueryableMethods` member names** (Step 5 of Tasks 2 & 4) must be verified against the actual EF Core class — the plan lists the expected names but the executor confirms them at edit time (they differ subtly across `WithPredicate`/`WithoutPredicate`/`WithSelector`).
- **Zero-regression** is enforced by Task 6; every out-of-scope shape must still fall back (throw only under `NativeOnly`).
