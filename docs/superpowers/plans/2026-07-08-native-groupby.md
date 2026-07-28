# Native GroupBy aggregation ($group) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Translate `GroupBy(key).Select(g => <aggregate>)` into a native `$group` aggregation-pipeline stage on the provider's native query path, so grouping-with-aggregates no longer hard-fails translation.

**Architecture:** EF Core's two-step GroupBy contract on the native path (Approach A). `TranslateGroupBy` records the key on the query IR and returns a grouped `ShapedQueryExpression`; the following `TranslateSelect` binds the aggregate projection into `$group` accumulators. Pipeline: `$match → $group → $project`. Unsupported shapes mark the query non-native and fall back to driver-LINQ (throw under `NativeOnly`). Reuses the EF-336 accumulator renderer; adds a multi-accumulator/keyed `$group` stage and a grouped-row result shaper.

**Tech Stack:** C#, EF Core (EF8/EF9/EF10 via build configs), MongoDB C# driver, xUnit + FluentAssertions. Build config `Debug EF10` for the primary loop.

## Global Constraints

- Multi-EF targeting via **build configurations**, not TFMs: build with `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"` (also validate `Debug EF8`, `Debug EF9`). Version-conditional code uses `EF8`/`EF9`/`EF10` define constants.
- `src/` obeys `<Nullable>enable</Nullable>` — annotate all new types.
- Preserve file BOMs on edited files.
- Tests run **serially** (parallelization disabled assembly-wide). Each functional test uses a uniquely-named database via the `CreateContext` helper pattern.
- MethodInfo matching MUST use canonical constants: `Microsoft.EntityFrameworkCore.Query.QueryableMethods` for top-level Queryable dispatch, `EnumerableMethods` inside projection/group selectors. Open vs. constructed generic methods compare unequal — compare against `GetGenericMethodDefinition()`.
- The native path becoming the default for GroupBy and the exact emitted MQL are implementation details, **not** breaking changes (per the provider's versioning rubric). Results must be unchanged vs. the driver-LINQ fallback.
- **Zero regressions:** full EF8/EF9/EF10 suites stay green; the `MONGODB_EF_NATIVE_ONLY=1` spec sweep shows **+passing, zero regressions**.
- Ships as **one squashed commit** on branch `EF-344` (stacked on `86eef4f`). Keep an `EF-344-presquash` safety branch until merge. Frequent commits during development; squash at the end.

## File Structure

**Create:**
- `src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoGrouping.cs` — immutable group IR (key + accumulators), modeled on `MongoCardinality`.
- `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/Stages/MongoGroupStage.cs` — pipeline stage carrying a non-null `_id` key expression + a list of accumulators.
- `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeGroupByBinder.cs` — binds key selector + aggregate projection into `MongoGrouping`.
- `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeGroupByTests.cs` — parity + NativeOnly functional tests.

**Modify:**
- `src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoSelectDefinition.cs` — add `Grouping` slot + `NativeRoute.GroupBy` + `Route` arm.
- `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoPipelineFactory.cs` — add `MongoGroupStage` render arm (`RenderKeyedGroup`).
- `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoSelectLowerer.cs` — add the group branch (emit `$group` + `$project`).
- `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs` — implement `TranslateGroupBy`; add grouped-source branch to `TranslateSelect`; adjust the GroupBy switch arm.
- `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoShapedQueryCompilingExpressionVisitor.cs` — route `NativeRoute.GroupBy` through the enumerable projection execution path.
- Test files under `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/`: `MongoSelectDefinitionTests.cs`, `AggregateStageRenderingTests.cs` (or a new `GroupStageRenderingTests.cs`), `MongoSelectLowererTests.cs`.
- Spec-test MQL baselines under `tests/MongoDB.EntityFrameworkCore.SpecificationTests/` as needed (regen with `EF_TEST_REWRITE_BASELINES=1`).

---

### Task 1: Group IR type + query-IR slot + route

**Files:**
- Create: `src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoGrouping.cs`
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoSelectDefinition.cs`
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoSelectDefinitionTests.cs`

**Interfaces:**
- Produces: `MongoGrouping` (immutable) with `IReadOnlyList<MongoGroupingKeyPart> Key`, `IReadOnlyList<MongoGroupAccumulator> Accumulators`; record `MongoGroupingKeyPart(string? Name, MongoExpression FieldRef)` (`Name == null` ⇒ scalar single-part key rendered directly as `_id`; non-null ⇒ composite `_id.<Name>`); record `MongoGroupAccumulator(string OutputField, string Operator, MongoExpression? Operand)` (`Operand == null` ⇒ count, renders `$sum: 1`). `MongoSelectDefinition.Grouping { get; set; }`; `NativeRoute.GroupBy`.
- Consumes: `MongoExpression` (existing), `MongoFieldExpression` (existing).

- [ ] **Step 1: Write the failing test**

Add to `MongoSelectDefinitionTests.cs`:

```csharp
    [Fact]
    public void Route_is_GroupBy_when_grouping_set()
    {
        var select = new MongoSelectDefinition();
        select.Grouping = new MongoGrouping(
            new[] { new MongoGroupingKeyPart(null, new MongoFieldExpression(property: null!, elementName: "country")) },
            new[] { new MongoGroupAccumulator("Count", "$sum", null) });

        Assert.Equal(NativeRoute.GroupBy, select.Route);
    }

    [Fact]
    public void Route_is_Fallback_even_when_grouping_set_if_marked_not_native()
    {
        var select = new MongoSelectDefinition();
        select.Grouping = new MongoGrouping(
            new[] { new MongoGroupingKeyPart(null, new MongoFieldExpression(property: null!, elementName: "country")) },
            new[] { new MongoGroupAccumulator("Count", "$sum", null) });
        select.MarkNotNativelyRepresentable();

        Assert.Equal(NativeRoute.Fallback, select.Route);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests -c "Debug EF10" --filter "FullyQualifiedName~MongoSelectDefinitionTests"`
Expected: FAIL — `MongoGrouping`/`MongoGroupingKeyPart`/`MongoGroupAccumulator`/`NativeRoute.GroupBy`/`Grouping` do not exist (compile error).

- [ ] **Step 3: Create `MongoGrouping.cs`**

```csharp
// (preserve the standard MongoDB Apache-2.0 file header + BOM used by sibling files in this folder)
using System.Collections.Generic;

namespace MongoDB.EntityFrameworkCore.Query.Expressions;

/// <summary>
/// Dialect-neutral IR for a native <c>$group</c>: the grouping key and the accumulators produced over each group.
/// </summary>
internal sealed class MongoGrouping(
    IReadOnlyList<MongoGroupingKeyPart> key,
    IReadOnlyList<MongoGroupAccumulator> accumulators)
{
    /// <summary>Grouping key parts. A single part with <see cref="MongoGroupingKeyPart.Name"/> null is a scalar key
    /// rendered directly as <c>_id</c>; named parts render as a composite <c>_id</c> sub-document.</summary>
    public IReadOnlyList<MongoGroupingKeyPart> Key { get; } = key;

    /// <summary>Accumulators, one per aggregate output field.</summary>
    public IReadOnlyList<MongoGroupAccumulator> Accumulators { get; } = accumulators;

    public bool IsCompositeKey => Key.Count != 1 || Key[0].Name != null;
}

/// <summary>One part of a grouping key. <paramref name="Name"/> is null for a scalar (single-part) key.</summary>
internal sealed record MongoGroupingKeyPart(string? Name, MongoExpression FieldRef);

/// <summary>One <c>$group</c> accumulator. <paramref name="Operand"/> is null for count (<c>$sum: 1</c>).</summary>
internal sealed record MongoGroupAccumulator(string OutputField, string Operator, MongoExpression? Operand);
```

- [ ] **Step 4: Add the slot + route arm to `MongoSelectDefinition.cs`**

Add the property beside `Cardinality` (after line ~121):

```csharp
    /// <summary>The native grouping ($group), or null when the query does not group.</summary>
    internal MongoGrouping? Grouping { get; set; }
```

Change the `Route` computed property (currently lines 130–147) — add the `GroupBy` arm after the `ScalarAggregate` check:

```csharp
    internal NativeRoute Route
        => _hasUnsupportedOperator ? NativeRoute.Fallback
            : Cardinality?.Aggregate != null ? NativeRoute.ScalarAggregate
            : Grouping != null ? NativeRoute.GroupBy
            : _projections.Count > 0 ? NativeRoute.Projection
            : NativeRoute.WholeEntity;
```

Add the enum member to `NativeRoute` (after `ScalarAggregate`):

```csharp
    ,

    /// <summary>Native pipeline ending in a keyed <c>$group</c> producing a grouped-aggregate sequence.</summary>
    GroupBy
```

(Insert as a proper enum member — i.e. add `, GroupBy` with its doc comment; keep existing members intact.)

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests -c "Debug EF10" --filter "FullyQualifiedName~MongoSelectDefinitionTests"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoGrouping.cs \
        src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoSelectDefinition.cs \
        tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoSelectDefinitionTests.cs
git commit -m "EF-344: add MongoGrouping IR + GroupBy route slot"
```

---

### Task 2: `MongoGroupStage` + renderer

**Files:**
- Create: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/Stages/MongoGroupStage.cs`
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoPipelineFactory.cs`
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/AggregateStageRenderingTests.cs`

**Interfaces:**
- Consumes: `MongoGrouping`, `MongoGroupingKeyPart`, `MongoGroupAccumulator` (Task 1); `MongoAggregationExpressionRenderer.Render(MongoExpression, PlaceholderTable)` (existing).
- Produces: `MongoGroupStage(MongoGrouping grouping)` with `MongoGrouping Grouping { get; }`; renders `{ $group: { _id: <key>, <out>: { <op>: <operand|1> }, ... } }`.

- [ ] **Step 1: Write the failing test**

Add to `AggregateStageRenderingTests.cs` (reuse its `Render(params MongoPipelineStage[])` helper):

```csharp
    [Fact]
    public void Keyed_group_scalar_key_renders()
    {
        var grouping = new MongoGrouping(
            new[] { new MongoGroupingKeyPart(null, new MongoFieldExpression(property: null!, elementName: "country")) },
            new[]
            {
                new MongoGroupAccumulator("Count", "$sum", null),
                new MongoGroupAccumulator("Total", "$sum", new MongoFieldExpression(property: null!, elementName: "amount")),
            });

        var result = Render(new MongoGroupStage(grouping));

        Assert.Equal(
            BsonDocument.Parse("{ $group: { _id: '$country', Count: { $sum: 1 }, Total: { $sum: '$amount' } } }"),
            result[0]);
    }

    [Fact]
    public void Keyed_group_composite_key_renders()
    {
        var grouping = new MongoGrouping(
            new[]
            {
                new MongoGroupingKeyPart("Country", new MongoFieldExpression(property: null!, elementName: "country")),
                new MongoGroupingKeyPart("Year", new MongoFieldExpression(property: null!, elementName: "year")),
            },
            new[] { new MongoGroupAccumulator("Count", "$sum", null) });

        var result = Render(new MongoGroupStage(grouping));

        Assert.Equal(
            BsonDocument.Parse("{ $group: { _id: { Country: '$country', Year: '$year' }, Count: { $sum: 1 } } }"),
            result[0]);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests -c "Debug EF10" --filter "FullyQualifiedName~AggregateStageRenderingTests"`
Expected: FAIL — `MongoGroupStage` undefined (compile error).

- [ ] **Step 3: Create `MongoGroupStage.cs`**

```csharp
// (preserve the standard MongoDB Apache-2.0 file header + BOM used by MongoGroupAccumulatorStage.cs)
using MongoDB.EntityFrameworkCore.Query.Expressions;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation.Stages;

/// <summary>A keyed <c>$group</c> stage: non-null <c>_id</c> key expression plus one or more accumulators.</summary>
internal sealed class MongoGroupStage(MongoGrouping grouping) : MongoPipelineStage
{
    public MongoGrouping Grouping { get; } = grouping;
}
```

- [ ] **Step 4: Add the render arm to `MongoPipelineFactory.cs`**

In `RenderStage` (the switch at ~lines 80–93), add before the `_ =>` default arm:

```csharp
            MongoGroupStage keyedGroup => RenderKeyedGroup(keyedGroup, placeholders),
```

Add the method beside `RenderGroup` (~line 134):

```csharp
    private static BsonDocument RenderKeyedGroup(MongoGroupStage stage, PlaceholderTable placeholders)
    {
        var grouping = stage.Grouping;

        BsonValue id;
        if (grouping.IsCompositeKey)
        {
            var idDoc = new BsonDocument();
            foreach (var part in grouping.Key)
                idDoc.Add(part.Name, MongoAggregationExpressionRenderer.Render(part.FieldRef, placeholders));
            id = idDoc;
        }
        else
        {
            id = MongoAggregationExpressionRenderer.Render(grouping.Key[0].FieldRef, placeholders);
        }

        var group = new BsonDocument { { "_id", id } };
        foreach (var acc in grouping.Accumulators)
        {
            var operand = acc.Operand is null
                ? (BsonValue)1
                : MongoAggregationExpressionRenderer.Render(acc.Operand, placeholders);
            group.Add(acc.OutputField, new BsonDocument(acc.Operator, operand));
        }

        return new BsonDocument("$group", group);
    }
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests -c "Debug EF10" --filter "FullyQualifiedName~AggregateStageRenderingTests"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/Stages/MongoGroupStage.cs \
        src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoPipelineFactory.cs \
        tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/AggregateStageRenderingTests.cs
git commit -m "EF-344: render keyed multi-accumulator \$group stage"
```

---

### Task 3: Lowerer group branch

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoSelectLowerer.cs`
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoSelectLowererTests.cs`

**Interfaces:**
- Consumes: `MongoSelectDefinition.Grouping` (Task 1); `MongoGroupStage` (Task 2).
- Produces: when `Grouping != null`, `Lower` appends a `MongoGroupStage` after `$match` (and no other terminal); a `$project` is NOT needed because the `$group` output field names already equal the result aliases (the binder in Task 4 names accumulator output fields with the projection aliases and reads the key back from `_id`). Canonical order: `$match → $group`.

- [ ] **Step 1: Write the failing test**

Add to `MongoSelectLowererTests.cs` (mirror the existing lowerer-test construction of a `MongoQueryExpression`/`MongoSelectDefinition` — copy the setup helper already used by the aggregate lowering tests in this file):

```csharp
    [Fact]
    public void Lowers_grouping_to_group_stage_after_match()
    {
        var query = /* build a MongoQueryExpression whose Select has a Predicate and a Grouping,
                        using the same helper the Cardinality lowering tests in this file use */;
        query.Select.Grouping = new MongoGrouping(
            new[] { new MongoGroupingKeyPart(null, new MongoFieldExpression(property: null!, elementName: "country")) },
            new[] { new MongoGroupAccumulator("Count", "$sum", null) });

        var stages = new MongoSelectLowerer().Lower(query);

        Assert.Collection(stages,
            s => Assert.IsType<MongoMatchStage>(s),
            s => Assert.IsType<MongoGroupStage>(s));
    }
```

> Implementer note: match the exact `MongoQueryExpression` construction used by the existing `MongoSelectLowererTests` aggregate cases (they already set `Select.Cardinality` and a predicate). Reuse that helper verbatim; only swap `Cardinality` for `Grouping`.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests -c "Debug EF10" --filter "FullyQualifiedName~MongoSelectLowererTests"`
Expected: FAIL — no `MongoGroupStage` emitted (the grouping slot is ignored).

- [ ] **Step 3: Add the group branch in `Lower`**

In `MongoSelectLowerer.Lower`, immediately before the scalar-aggregate terminal region (before "// 7. Scalar aggregate terminal stage", ~line 98), add:

```csharp
        // 6b. Keyed $group terminal (GroupBy(key).Select(aggregate)).
        if (select.Grouping is { } grouping)
        {
            stages.Add(new MongoGroupStage(grouping));
            return stages;
        }
```

This returns before the projection/aggregate regions (a grouped query has no separate `Projection`/`Cardinality` terminal — the group stage is terminal, and paging/orderings after the group are out of scope so no later stages apply). Confirm no `$sort`/`$skip`/`$limit` were appended earlier for a grouped query: the binder (Task 4) rejects (marks non-native) any query that carries orderings/paging alongside a grouping, so by the time lowering runs a `GroupBy`-route query has only `$match` + `$group`. Add an assertion-style guard is unnecessary; the route guarantees it.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests -c "Debug EF10" --filter "FullyQualifiedName~MongoSelectLowererTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoSelectLowerer.cs \
        tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoSelectLowererTests.cs
git commit -m "EF-344: lower grouping slot to \$group stage"
```

---

### Task 4: `NativeGroupByBinder`

**Files:**
- Create: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeGroupByBinder.cs`
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/NativeGroupByBinderTests.cs` (create)

**Interfaces:**
- Consumes: `MongoQueryExpression` (with `.Select` = `MongoSelectDefinition`, `.CollectionExpression.EntityType`); `MongoExpressionTranslator` (ctor takes `IEntityType`; `TryTranslateField(Expression, out MongoFieldExpression)`); `MongoGrouping` etc. (Task 1).
- Produces:
  - `static bool TryBindGroupKey(MongoQueryExpression mongoQ, LambdaExpression keySelector)` — validates the key is a scalar field-ref or a `NewExpression` of field-refs; stashes the parsed key parts on the binder-owned pending state (store on a new `MongoSelectDefinition.PendingGroupKey` set of `MongoGroupingKeyPart` — add this internal property in this task). Returns false (⇒ caller marks non-native) otherwise.
  - `static bool TryBindGroupProjection(MongoQueryExpression mongoQ, LambdaExpression resultSelector, Expression groupingParameterShaper)` — walks the result `NewExpression`/`MemberInitExpression`; each member must be the key (`g.Key` / `g.Key.<sub>`) or `g.<Aggregate>(x => x.Field)` / `g.Count()`; builds `MongoGroupAccumulator`s (output field = member name) and finalizes `mongoQ.Select.Grouping = new MongoGrouping(pendingKey, accumulators)`. Returns false otherwise.
- Guard: if `mongoQ.Select.HasPaging` or `mongoQ.Select.Orderings.Count > 0`, return false (post-group/pre-existing paging+group not in scope).

- [ ] **Step 1: Write the failing tests**

Create `NativeGroupByBinderTests.cs`. These are white-box binder tests; construct the `MongoQueryExpression` with the same helper the other `NativeTranslation` binder tests use. Cover: scalar key + Count binds (Grouping set, one accumulator `$sum`/null operand); composite key binds two key parts; `Sum(x => x.Field)` binds `$sum` with a field-ref operand; computed key (`x => x.Date.Year`) returns false and leaves `Grouping` null; computed operand (`Sum(x => x.A * x.B)`) returns false; presence of paging returns false.

```csharp
    [Fact]
    public void Scalar_key_and_count_binds()
    {
        var mongoQ = /* build MongoQueryExpression for entity type with fields Country, Amount */;
        // g => g.Country
        LambdaExpression key = /* x => x.Country */;
        Assert.True(NativeGroupByBinder.TryBindGroupKey(mongoQ, key));

        // g => new { Count = g.Count() }
        LambdaExpression proj = /* g => new { Count = g.Count() } */;
        Assert.True(NativeGroupByBinder.TryBindGroupProjection(mongoQ, proj, groupingParameterShaper: null!));

        var grouping = mongoQ.Select.Grouping!;
        Assert.Single(grouping.Key);
        Assert.Null(grouping.Key[0].Name);
        Assert.Collection(grouping.Accumulators,
            a => { Assert.Equal("Count", a.OutputField); Assert.Equal("$sum", a.Operator); Assert.Null(a.Operand); });
    }

    [Fact]
    public void Computed_key_returns_false()
    {
        var mongoQ = /* ... */;
        LambdaExpression key = /* x => x.OrderDate.Year */;
        Assert.False(NativeGroupByBinder.TryBindGroupKey(mongoQ, key));
        Assert.Null(mongoQ.Select.Grouping);
    }
```

> Implementer note: the LINQ lambdas above are illustrative. Build them the way the existing `MongoExpressionTranslatorTests` / `NativeCardinalityBinder`-adjacent tests build parameter lambdas over a test entity type. If constructing a real `IGrouping`-typed lambda for the projection is impractical in a pure unit test, cover the projection binding via the functional NativeOnly tests in Task 6 instead and keep the unit tests focused on `TryBindGroupKey` + accumulator parsing helpers. Do not fake acceptance.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests -c "Debug EF10" --filter "FullyQualifiedName~NativeGroupByBinderTests"`
Expected: FAIL — `NativeGroupByBinder` undefined.

- [ ] **Step 3: Implement `NativeGroupByBinder`**

Model on `NativeCardinalityBinder`. Skeleton:

```csharp
// (standard header + BOM)
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Query;
using MongoDB.EntityFrameworkCore.Query.Expressions;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

internal static class NativeGroupByBinder
{
    internal static bool TryBindGroupKey(MongoQueryExpression mongoQ, LambdaExpression keySelector)
    {
        var select = mongoQ.Select;
        if (select.HasPaging || select.Orderings.Count > 0)
            return false;

        var translator = new MongoExpressionTranslator(mongoQ.CollectionExpression.EntityType);

        var parts = new List<MongoGroupingKeyPart>();
        switch (keySelector.Body)
        {
            case NewExpression newExpr when newExpr.Members is { Count: > 0 }:
                for (var i = 0; i < newExpr.Arguments.Count; i++)
                {
                    if (newExpr.Arguments[i] is not MemberExpression
                        || !translator.TryTranslateField(newExpr.Arguments[i], out var field))
                        return false;
                    parts.Add(new MongoGroupingKeyPart(newExpr.Members[i].Name, field));
                }
                break;

            case MemberExpression:
                if (!translator.TryTranslateField(keySelector.Body, out var scalarField))
                    return false;
                parts.Add(new MongoGroupingKeyPart(null, scalarField));
                break;

            default:
                return false; // computed / unsupported key
        }

        select.PendingGroupKey = parts;
        return true;
    }

    internal static bool TryBindGroupProjection(
        MongoQueryExpression mongoQ, LambdaExpression resultSelector, Expression groupingParameterShaper)
    {
        var select = mongoQ.Select;
        if (select.PendingGroupKey is not { } keyParts)
            return false;

        var translator = new MongoExpressionTranslator(mongoQ.CollectionExpression.EntityType);
        var accumulators = new List<MongoGroupAccumulator>();

        // Result body must be NewExpression (anonymous) or MemberInitExpression (DTO).
        if (!TryGetProjectionBindings(resultSelector.Body, out var bindings))
            return false;

        foreach (var (memberName, valueExpr) in bindings)
        {
            if (IsGroupKeyAccess(valueExpr, resultSelector.Parameters[0]))
                continue; // key member — read back from _id in the shaper (Task 6)

            if (!TryBindAccumulator(valueExpr, memberName, resultSelector.Parameters[0], translator, out var acc))
                return false;
            accumulators.Add(acc);
        }

        if (accumulators.Count == 0)
            return false; // pure key regroup with no aggregate — treat as unsupported here (falls back)

        select.Grouping = new MongoGrouping(keyParts, accumulators);
        return true;
    }

    // TryGetProjectionBindings: NewExpression -> (member.Name, arg); MemberInitExpression -> (assignment member, expr).
    // IsGroupKeyAccess: MemberExpression rooted at the grouping parameter's .Key (whole key or a composite sub-member).
    // TryBindAccumulator: match g.Count()/g.LongCount() (EnumerableMethods.CountWithoutPredicate / LongCount) -> ("$sum", null);
    //   g.Sum/Average/Min/Max(sel) (EnumerableMethods.Sum*/Average*/Min*/Max*) where sel.Body is a MemberExpression
    //   translatable via translator.TryTranslateField -> ("$sum"/"$avg"/"$min"/"$max", fieldRef); else false.
}
```

Implement the three private helpers exactly as their comments describe. Match aggregate method calls against `Microsoft.EntityFrameworkCore.Query.EnumerableMethods` canonical constants (the group aggregates are `Enumerable.*` calls over the grouping), comparing `GetGenericMethodDefinition()`. For `IsGroupKeyAccess`, recognize `g.Key` (the whole key — only valid for a scalar key) and `g.Key.<Sub>` (composite sub-member matching a `PendingGroupKey` part name).

Add to `MongoSelectDefinition.cs`:

```csharp
    /// <summary>Group key parsed by TryBindGroupKey, consumed by TryBindGroupProjection. Not part of Route.</summary>
    internal IReadOnlyList<MongoGroupingKeyPart>? PendingGroupKey { get; set; }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests -c "Debug EF10" --filter "FullyQualifiedName~NativeGroupByBinderTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeGroupByBinder.cs \
        src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoSelectDefinition.cs \
        tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/NativeGroupByBinderTests.cs
git commit -m "EF-344: NativeGroupByBinder — key + accumulator binding"
```

---

### Task 5: QMTEV wiring (TranslateGroupBy + TranslateSelect + switch arm)

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs`
- Test: covered by the functional NativeOnly tests in Task 6 (this task's own check is that translation no longer hard-throws and that a supported group produces a `GroupBy`-route query).

**Interfaces:**
- Consumes: `NativeGroupByBinder.TryBindGroupKey` / `TryBindGroupProjection` (Task 4).
- Produces: `TranslateGroupBy` returns a grouped `ShapedQueryExpression` (using the base class's grouping shaper); `TranslateSelect` routes a grouped source to `TryBindGroupProjection`.

- [ ] **Step 1: Implement `TranslateGroupBy`**

Replace the inert override (lines 671–673) with:

```csharp
    protected override ShapedQueryExpression? TranslateGroupBy(ShapedQueryExpression source, LambdaExpression keySelector,
        LambdaExpression? elementSelector, LambdaExpression? resultSelector)
    {
        // Native path supports GroupBy(key).Select(aggregate). elementSelector (IGrouping element shaping) and a
        // fused resultSelector are not natively bound here; defer to the base class to build the standard grouping
        // shaper, then mark non-native if the key itself is not natively representable.
        var translated = base.TranslateGroupBy(source, keySelector, elementSelector, resultSelector);
        if (translated is null)
            return null;

        var mongoQ = (MongoQueryExpression)translated.QueryExpression;
        if (elementSelector != null || resultSelector != null
            || !NativeGroupByBinder.TryBindGroupKey(mongoQ, keySelector))
        {
            mongoQ.Select.MarkNotNativelyRepresentable();
        }

        return translated;
    }
```

> Implementer note: verify what `base.TranslateGroupBy` returns for this provider (it builds a `ShapedQueryExpression` whose shaper is a `GroupByShaperExpression`). If the base returns null for the MongoQueryExpression shape, instead construct the grouped shaped query directly following the base-class pattern — but first confirm by stepping through a `GroupBy(k).Select(agg)` test. Keep the "mark non-native on unsupported" behavior either way so the query falls back rather than hard-throwing.

- [ ] **Step 2: Route grouped source in `TranslateSelect`**

In `TranslateSelect` (lines 165–193), before the existing `NativeProjectionBinder` block, add a grouped-source branch:

```csharp
        var mongoQueryExpression = (MongoQueryExpression)source.QueryExpression;

        if (source.ShaperExpression is Microsoft.EntityFrameworkCore.Query.GroupByShaperExpression)
        {
            if (!NativeGroupByBinder.TryBindGroupProjection(mongoQueryExpression, selector, source.ShaperExpression))
            {
                mongoQueryExpression.Select.MarkNotNativelyRepresentable();
            }
            // fall through to build the shaper below (the group-result shaper is wired in Task 6)
        }
        else if (!IsTransparentIdentifierSelector(selector) && !IsSingleLevelCollectionIncludeSelector(selector))
        {
            if (!NativeProjectionBinder.TryPopulateNativeProjection(mongoQueryExpression, selector))
            {
                mongoQueryExpression.Select.MarkNotNativelyRepresentable();
            }
        }
```

(Remove the now-duplicated `var mongoQueryExpression = ...` line that followed; keep the shaper-building tail of the method unchanged for now — Task 6 replaces the grouped shaper build.)

- [ ] **Step 3: Adjust the GroupBy switch arm**

The GroupBy case (lines 133–147) currently returns `NotTranslatedExpression` when the base doesn't return a shaped query. With `TranslateGroupBy` now returning a shaped query, GroupBy will flow through normally. Verify the switch arm still routes GroupBy through `base.VisitMethodCall` and no longer produces `NotTranslatedExpression` for a supported group. No code change may be needed here — confirm by test. If GroupBy is currently ONLY in the "bubble through for better error" arm and that arm still returns `NotTranslatedExpression` on any non-shaped result, leave it: `TranslateGroupBy` now always returns a shaped query (native or marked-fallback), so the arm passes it through.

- [ ] **Step 4: Build + smoke**

Run: `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"`
Expected: builds. (End-to-end behavior is asserted in Task 6.)

- [ ] **Step 5: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs
git commit -m "EF-344: wire GroupBy translation into QMTEV (key + grouped Select)"
```

---

### Task 6: Grouped-row result shaper + execution routing

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoShapedQueryCompilingExpressionVisitor.cs`
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs` (finish the grouped shaper build in `TranslateSelect`)
- Test: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeGroupByTests.cs` (create)

**Interfaces:**
- Consumes: `NativeRoute.GroupBy` (Task 1); the lowered `$group` pipeline (Tasks 2–3); the bound `MongoGrouping` (Task 4).
- Produces: an enumerable result where each `$group` output document is shaped into the anonymous/DTO result — key members read from `_id` (or `_id.<Name>`), accumulator members from the accumulator output fields.

This is the one genuinely new shaper. It reuses `MongoProjectionBindingRemovingExpressionVisitor` (which already reads fields by alias from a per-row `BsonDocument`) by making the grouped `Select`'s shaper bind each result member to a projection alias:
- accumulator members → their `OutputField` alias (a top-level field in the `$group` doc);
- scalar key member (`g.Key`) → the `_id` field;
- composite key sub-member (`g.Key.X`) → `_id.X`.

- [ ] **Step 1: Write the failing functional tests**

Create `NativeGroupByTests.cs` mirroring `NativeCardinalityTests.cs` (same `CreateContext(seed, MongoQueryMode, dbName)` helper shape, seeding a small entity set with `Country`/`Year`/`Amount`). Cover, under both `Native` (parity) and `NativeOnly` (goes-native):

```csharp
    [Fact]
    public void GroupBy_scalar_key_with_count_and_sum_goes_native()
    {
        using var db = CreateContext(SeedOrders(), MongoQueryMode.NativeOnly,
            nameof(GroupBy_scalar_key_with_count_and_sum_goes_native));

        var result = db.Orders
            .GroupBy(o => o.Country)
            .Select(g => new { Country = g.Key, Count = g.Count(), Total = g.Sum(o => o.Amount) })
            .OrderBy(r => r.Country)   // client-side ordering of the materialized result for a stable assert
            .AsEnumerable()
            .ToList();

        // succeeds under NativeOnly => went native; values correct
        Assert.Equal(expectedCountryGroups, result.Select(r => (r.Country, r.Count, r.Total)));
    }

    [Fact]
    public void GroupBy_composite_key_goes_native() { /* g => new { g.Country, g.Year } */ }

    [Fact]
    public void GroupBy_computed_key_falls_back()
    {
        using var db = CreateContext(SeedOrders(), MongoQueryMode.NativeOnly, nameof(GroupBy_computed_key_falls_back));
        Assert.Throws<NativeTranslationNotSupportedException>(() =>
            db.Orders.GroupBy(o => o.OrderDate.Year).Select(g => new { g.Key, C = g.Count() }).ToList());
    }

    [Fact]
    public void GroupBy_computed_operand_falls_back()
    {
        using var db = CreateContext(SeedOrders(), MongoQueryMode.NativeOnly, nameof(GroupBy_computed_operand_falls_back));
        Assert.Throws<NativeTranslationNotSupportedException>(() =>
            db.Orders.GroupBy(o => o.Country).Select(g => new { g.Key, T = g.Sum(o => o.Amount * 2) }).ToList());
    }

    [Fact]
    public void Bare_grouping_sequence_falls_back()
    {
        using var db = CreateContext(SeedOrders(), MongoQueryMode.NativeOnly, nameof(Bare_grouping_sequence_falls_back));
        Assert.Throws<NativeTranslationNotSupportedException>(() =>
            db.Orders.GroupBy(o => o.Country).ToList());
    }

    [Fact]
    public void GroupBy_results_match_driver_linq()
    {
        // Native path parity: run the same query under MongoQueryMode.Native and assert values.
    }
```

Note the client-side `OrderBy` on the *materialized* result is fine (LINQ-to-objects after `AsEnumerable`); a server-side post-group `OrderBy` would instead fall back — do not put OrderBy before the terminal in the NativeOnly tests.

- [ ] **Step 2: Run tests to verify they fail**

Run: `MONGODB_URI= ATLAS_URI= dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests -c "Debug EF10" --filter "FullyQualifiedName~NativeGroupByTests"`
Expected: FAIL — supported-group tests throw/there is no group shaper yet; fallback tests may already pass.

- [ ] **Step 3: Finish the grouped shaper build in `TranslateSelect`**

Where Task 5 left the grouped branch "falling through", build the shaper so each result member binds to a projection alias that `MongoProjectionBindingRemovingExpressionVisitor` can read from the `$group` output document. Concretely: register a `MongoProjection` per accumulator (`alias = OutputField`, `MongoExpression = MongoFieldExpression(elementName: OutputField)`) and per key member (`alias = "_id"` or `"_id.<Name>"`), then build the shaper via the same `_projectionBindingExpressionVisitor.Translate(mongoQueryExpression, newSelectorBody)` tail the method already uses — replacing `g.Key`/`g.<Agg>(...)` sub-expressions with `ProjectionBindingExpression`s pointing at those aliases.

> Implementer note: this mirrors how `NativeProjectionBinder` + `MongoProjectionBindingRemovingExpressionVisitor` already cooperate for ordinary projections (Task-8 exploration point 8/9). Follow that exact mechanism; do not invent a parallel shaper. Reading `_id.<Name>` may require the by-name nested read path in `MongoProjectionBindingRemovingExpressionVisitor.CreateGetValueExpression` — verify it supports dotted/nested aliases; if not, emit a `$project` in the lowerer (Task 3) that flattens `_id`/`_id.<Name>` and accumulator fields to top-level result aliases, and bind to those flat aliases instead. Prefer the `$project`-flatten approach if nested-alias reads are not already supported — it keeps the shaper on the well-trodden top-level-alias path.

- [ ] **Step 4: Route `NativeRoute.GroupBy` through enumerable execution**

In `MongoShapedQueryCompilingExpressionVisitor`, ensure a `Route == GroupBy` query takes the **enumerable projection** execution path (per-row DOM shaper via `MongoProjectionBindingRemovingExpressionVisitor`), NOT `ExecuteAggregate`/`SingleValueEnumerable` (which is single-value). Add `NativeRoute.GroupBy` wherever the routing switch currently admits `NativeRoute.Projection`/`WholeEntity` for the enumerable path, and confirm `TryBuildNativeFactory` (not `TryBuildAggregateFactory`) builds the factory for it. Under `NativeOnly`, an unbindable group must still throw `NativeTranslationNotSupportedException` via the existing `ThrowIfNativeOnlyForbidsFallback` gate.

- [ ] **Step 5: Run tests to verify they pass**

Run: `MONGODB_URI= ATLAS_URI= dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests -c "Debug EF10" --filter "FullyQualifiedName~NativeGroupByTests"`
Expected: PASS (all shapes: native successes + fallbacks).

- [ ] **Step 6: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoShapedQueryCompilingExpressionVisitor.cs \
        src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs \
        tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeGroupByTests.cs
git commit -m "EF-344: grouped-row result shaper + GroupBy execution routing"
```

---

### Task 7: Regression sweep, baselines, EF8/EF9

**Files:**
- Modify (as needed): spec-test MQL baselines under `tests/MongoDB.EntityFrameworkCore.SpecificationTests/`.

- [ ] **Step 1: Full EF10 unit + functional suites**

Run: `MONGODB_URI= ATLAS_URI= dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests -c "Debug EF10"` then the FunctionalTests project.
Expected: green.

- [ ] **Step 2: Native-only spec sweep (zero regressions)**

Run the SpecificationTests with `MONGODB_EF_NATIVE_ONLY=1`. Compare passing count to the pre-branch baseline.
Expected: **+passing (GroupBy shapes now native), zero regressions.** If any GroupBy MQL baseline drifted, regenerate with `EF_TEST_REWRITE_BASELINES=1` and confirm the diff is drift-only (same results).

- [ ] **Step 3: Validate EF8 and EF9 build + affected tests**

Run: `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF8"` and `-c "Debug EF9"`, then the Query-filtered tests for each.
Expected: green. Add `#if`/`EF8`/`EF9` guards only if a base-class GroupBy API differs across versions (verify `base.TranslateGroupBy` / `GroupByShaperExpression` signatures match across EF8/9/10; guard if not).

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "EF-344: regenerate GroupBy spec baselines; EF8/EF9 validation"
```

---

### Task 8: Docs + squash

- [ ] **Step 1: Update Query AGENTS.md**

Add a short note to `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md` documenting that `GroupBy(key).Select(aggregate)` is native (scalar+composite keys, standard field-ref accumulators) and that computed keys/operands, bare `IGrouping`, and post-group operators fall back. Keep claims accurate — do not overstate.

- [ ] **Step 2: Backup + squash to one commit**

```bash
git branch -f EF-344-presquash HEAD
git reset --soft 86eef4f
git commit -F docs/superpowers/plans/EF-344-commit-message.txt   # PR-style message; see the design doc TL;DR
git diff --quiet EF-344-presquash HEAD && echo "content-identical OK"
```

(Write the PR-style commit message from the design doc's TL;DR + scope. Exclude the pre-existing unstaged `nuget.config` change and untracked scratch docs — the soft reset leaves them out.)

- [ ] **Step 3: Final verification**

Run the EF10 suites once more post-squash; confirm green. Do NOT push — the user drives the force-push to `origin/NativeQueryOngoing`.

---

## Self-Review notes

- **Spec coverage:** scalar+composite keys (Tasks 1,2,4), standard field-ref accumulators (Tasks 2,4), `$match→$group→$project` (Tasks 3,6), fallback-not-throw for unsupported shapes (Tasks 5,6), no post-group ops / no IGrouping (guarded in Task 4 binder + asserted in Task 6), EF-337 caveat (documented, not fixed), NativeOnly proof + zero-regression sweep (Tasks 6,7). All spec sections map to a task.
- **Known risk / split point:** Task 6 (result shaper) is the least mechanically-determined; the plan gives two concrete routes (nested-alias read vs. `$project`-flatten) and prefers the flatten route. If Task 6 proves larger than a single reviewable unit during execution, split it: 6a `$project`-flatten in the lowerer + top-level-alias binding; 6b execution routing.
- **Type consistency:** `MongoGrouping`/`MongoGroupingKeyPart`/`MongoGroupAccumulator` names and the `Operand == null ⇒ $sum:1` convention are used identically across Tasks 1,2,3,4. `NativeRoute.GroupBy` used in Tasks 1,6.
