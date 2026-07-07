# Native Projection Pushdown ($project) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the native query pipeline emit a server-side `$project` for terminal anonymous-type / DTO projections of plain member accesses, and materialize the result via the existing DOM shaper — retiring the driver-LINQ cutout for those shapes.

**Architecture:** Add a projection IR (`MongoProjection`) to `MongoSelectDefinition`; the QMTEV's `TranslateSelect` populates it from the raw selector (member name = alias) when every leaf is a translatable member access; the lowerer appends a `$project` stage last; the pipeline factory renders it (aggregation-expression dialect, `_id: 0`); the gate routes representable projections through the native factory + the existing `MongoProjectionBindingRemovingExpressionVisitor`, which reads each field by alias. Anything outside the slice keeps falling back to driver-LINQ.

**Tech Stack:** C# / .NET (net8.0 for EF8/EF9, net10.0 for EF10), EF Core query pipeline, MongoDB C# driver (BSON), xUnit (plain `Assert.*`, no FluentAssertions).

## Global Constraints

- Build configurations, not TFMs: `Debug EF10` (net10.0), `Debug EF8` / `Debug EF9` (net8.0). Validate against `Debug EF10` and `Debug EF8` at minimum.
- `<Nullable>enable</Nullable>` on `src/` — annotate all new types.
- All new types are `internal` — no public-surface change (switching internal query representation and which execution path a supported query takes is explicitly not a break per `AGENTS.md`).
- Preserve file BOMs on files that already have them; the native-translation `Expressions/` and `NativeTranslation/` files were authored **without** BOM — match the sibling file you copy from.
- Tests run **serially**; unit tests need no DB. Functional/gate tests need a MongoDB (atlas-local container auto-boots when `MONGODB_URI`/`ATLAS_URI` are unset).
- **Native-path proof rule:** rendered MQL shape does NOT distinguish native from driver-LINQ. Assert native via `MongoQueryMode.NativeOnly` (succeeds when native-capable; throws `NativeTranslationNotSupportedException` otherwise). Assert fallback by asserting `NativeOnly` throws.
- Scope is **terminal anonymous-type and DTO/member-init projections whose leaves are plain member accesses**. Deferred (must keep falling back): bare scalar (`x => x.Name`), computed leaves (`x => new { T = x.A * x.B }`), mixed projections (entity/navigation references), non-terminal projections, streaming projection materialization.

---

## File Structure

**Create:**
- `src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoProjection.cs` — the projection IR value type (alias + expression). One responsibility: pair an output element name with its dialect-neutral source expression.
- `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/Stages/MongoProjectStage.cs` — typed `$project` stage IR carrying the ordered projection list.

**Modify:**
- `src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoSelectDefinition.cs` — add `Projection` list + `AddProjection`.
- `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoSelectLowerer.cs` — append `MongoProjectStage` last when the projection list is non-empty.
- `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoPipelineFactory.cs` — add the `$project` case + `RenderProject`.
- `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs` — `TranslateSelect` attempts native projection population.
- `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoShapedQueryCompilingExpressionVisitor.cs` — native projection branch in `VisitProjectedQuery`; `allowStreaming` param on `CompileShapedQuery`.
- `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md` — document the projection slice.

**Test:**
- `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoSelectDefinitionTests.cs`
- `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoSelectLowererTests.cs`
- `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoPipelineFactoryTests.cs`
- `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/SlotPopulationTests.cs`
- `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeGateRoutingTests.cs`

---

## Task 1: Projection IR on `MongoSelectDefinition`

**Files:**
- Create: `src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoProjection.cs`
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoSelectDefinition.cs`
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoSelectDefinitionTests.cs`

**Interfaces:**
- Produces: `readonly record struct MongoProjection(string Alias, MongoExpression Expression)`; `MongoSelectDefinition.Projection` (`IReadOnlyList<MongoProjection>`, get-only); `MongoSelectDefinition.AddProjection(MongoProjection projection)` (void, appends).

- [ ] **Step 1: Write the failing test**

Add to `MongoSelectDefinitionTests.cs`:

```csharp
    [Fact]
    public void AddProjection_appends_in_order_to_Projection()
    {
        var select = TestSelect();
        var a = new MongoProjection("Name", new MongoConstantExpression(1, null));
        var b = new MongoProjection("Age", new MongoConstantExpression(2, null));

        select.AddProjection(a);
        select.AddProjection(b);

        Assert.Equal(2, select.Projection.Count);
        Assert.Equal("Name", select.Projection[0].Alias);
        Assert.Equal("Age", select.Projection[1].Alias);
    }

    [Fact]
    public void New_select_has_empty_projection()
        => Assert.Empty(TestSelect().Projection);
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj -c "Debug EF10" --filter "FullyQualifiedName~MongoSelectDefinitionTests"`
Expected: FAIL — `MongoProjection` / `AddProjection` / `Projection` do not exist (compile error).

- [ ] **Step 3: Create `MongoProjection.cs`**

Copy the license header from `MongoOrdering.cs` (no BOM). Full file:

```csharp
/* Copyright 2023-present MongoDB Inc.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 * http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

namespace MongoDB.EntityFrameworkCore.Query.Expressions;

/// <summary>
/// A single output field of a native <c>$project</c> stage: an output element name (<paramref name="Alias"/>)
/// paired with the dialect-neutral <see cref="MongoExpression"/> that produces its value.
/// </summary>
/// <param name="Alias">The output element name in the projected document — matches the projection alias the
/// DOM shaper reads by (the anonymous-type / DTO member name).</param>
/// <param name="Expression">The dialect-neutral source expression (e.g. a <see cref="MongoFieldExpression"/>).</param>
internal readonly record struct MongoProjection(string Alias, MongoExpression Expression);
```

- [ ] **Step 4: Add the projection slot to `MongoSelectDefinition.cs`**

After the private `_orderings` field (`private readonly List<MongoOrdering> _orderings = [];`), add:

```csharp
    private readonly List<MongoProjection> _projections = [];
```

Before the `// ── Native-representable gate ─` region, add a new region:

```csharp
    // ── Projection ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The output fields of a server-side <c>$project</c> stage, in order. Empty means no projection
    /// (whole-entity results) — the entity path never populates this.
    /// </summary>
    public IReadOnlyList<MongoProjection> Projection => _projections;

    /// <summary>
    /// Appends <paramref name="projection"/> to the projection list.
    /// </summary>
    public void AddProjection(MongoProjection projection)
        => _projections.Add(projection);
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj -c "Debug EF10" --filter "FullyQualifiedName~MongoSelectDefinitionTests"`
Expected: PASS (all `MongoSelectDefinitionTests`).

- [ ] **Step 6: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoProjection.cs \
        src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoSelectDefinition.cs \
        tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoSelectDefinitionTests.cs
git commit -m "EF-331: Add MongoProjection IR to MongoSelectDefinition"
```

---

## Task 2: `MongoProjectStage` + lowerer emits it last

**Files:**
- Create: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/Stages/MongoProjectStage.cs`
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoSelectLowerer.cs`
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoSelectLowererTests.cs`

**Interfaces:**
- Consumes: `MongoSelectDefinition.Projection` (Task 1).
- Produces: `MongoProjectStage : MongoPipelineStage` with `IReadOnlyList<MongoProjection> Projections { get; }` ctor `MongoProjectStage(IReadOnlyList<MongoProjection> projections)`.

- [ ] **Step 1: Write the failing test**

Add to `MongoSelectLowererTests.cs`:

```csharp
    [Fact]
    public void Projection_lowers_to_a_project_stage_last()
    {
        var query = TestSelect();
        query.Select.AddPredicateConjunct(new MongoConstantExpression(true, null));
        query.Select.AddProjection(new MongoProjection("Name", new MongoConstantExpression(0, null)));

        var stages = new MongoSelectLowerer().Lower(query);

        Assert.IsType<MongoMatchStage>(stages[0]);
        var project = Assert.IsType<MongoProjectStage>(stages[^1]);
        Assert.Single(project.Projections);
        Assert.Equal("Name", project.Projections[0].Alias);
    }

    [Fact]
    public void No_projection_produces_no_project_stage()
    {
        var query = TestSelect();
        query.Select.AddPredicateConjunct(new MongoConstantExpression(true, null));

        var stages = new MongoSelectLowerer().Lower(query);

        Assert.DoesNotContain(stages, s => s is MongoProjectStage);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj -c "Debug EF10" --filter "FullyQualifiedName~MongoSelectLowererTests"`
Expected: FAIL — `MongoProjectStage` does not exist (compile error).

- [ ] **Step 3: Create `MongoProjectStage.cs`**

Copy the header/structure from `Stages/MongoSortStage.cs` (no BOM). Full file:

```csharp
/* Copyright 2023-present MongoDB Inc.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 * http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System.Collections.Generic;
using MongoDB.EntityFrameworkCore.Query.Expressions;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation.Stages;

/// <summary>
/// Represents a <c>$project</c> aggregation stage that reshapes each document into the projected fields.
/// </summary>
internal sealed class MongoProjectStage : MongoPipelineStage
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MongoProjectStage"/> class.
    /// </summary>
    /// <param name="projections">The ordered output fields of the projection.</param>
    public MongoProjectStage(IReadOnlyList<MongoProjection> projections)
    {
        Projections = projections;
    }

    /// <summary>
    /// Gets the ordered output fields of the projection.
    /// </summary>
    public IReadOnlyList<MongoProjection> Projections { get; }
}
```

- [ ] **Step 4: Append the stage in `MongoSelectLowerer.Lower`**

In `MongoSelectLowerer.cs`, after the `AppendLookupStages(query, stages);` call (currently the last statement before `return stages;`), add:

```csharp
        // 6. $project — server-side projection (terminal member-access anonymous/DTO Select). Last in
        // canonical order: the projection is the final logical operation for the SP3 terminal slice.
        if (select.Projection.Count > 0)
        {
            stages.Add(new MongoProjectStage(select.Projection));
        }
```

(`select` is the local `var select = query.Select;` already at the top of `Lower`.)

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj -c "Debug EF10" --filter "FullyQualifiedName~MongoSelectLowererTests"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/Stages/MongoProjectStage.cs \
        src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoSelectLowerer.cs \
        tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoSelectLowererTests.cs
git commit -m "EF-331: Lower projection slot to a trailing \$project stage"
```

---

## Task 3: Render `$project` in the pipeline factory

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoPipelineFactory.cs`
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoPipelineFactoryTests.cs`

**Interfaces:**
- Consumes: `MongoProjectStage` (Task 2), `MongoAggregationExpressionRenderer.Render(MongoExpression, PlaceholderTable) : BsonValue`, `MongoFieldExpression(IProperty, string elementName)`.
- Produces: a `{ $project: { <alias>: <aggExpr>, _id: 0 } }` document; `_id: 0` is omitted only when an alias is literally `_id`.

- [ ] **Step 1: Write the failing test**

Add to `MongoPipelineFactoryTests.cs`:

```csharp
    [Fact]
    public void Project_stage_renders_alias_to_field_ref_with_id_suppressed()
    {
        var nameProperty = GetProperty<Customer>("Name");
        var stage = new MongoProjectStage(new[]
        {
            new MongoProjection("Name", new MongoFieldExpression(nameProperty, "Name"))
        });

        var factory = MongoPipelineFactory.Create(new MongoPipelineStage[] { stage }, new MongoQueryLanguageRenderer());
        var pipeline = factory.Build(new Dictionary<string, object?>());

        Assert.Single(pipeline);
        var project = pipeline[0]["$project"].AsBsonDocument;
        Assert.Equal("$Name", project["Name"].AsString);
        Assert.Equal(0, project["_id"].AsInt32);
    }
```

(Add `using MongoDB.EntityFrameworkCore.Query.NativeTranslation.Stages;` to the file's usings if not already present.)

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj -c "Debug EF10" --filter "FullyQualifiedName~MongoPipelineFactoryTests.Project_stage"`
Expected: FAIL — `MongoPipelineFactory does not support stage type 'MongoProjectStage'` (thrown by the `RenderStage` default arm).

- [ ] **Step 3: Add the `$project` case + `RenderProject`**

In `MongoPipelineFactory.cs`, add a case to the `RenderStage` switch (alongside `MongoUnwindStage unwind => …`):

```csharp
            MongoProjectStage project => RenderProject(project, placeholders),
```

Then add the method next to `RenderSort`:

```csharp
    private static BsonDocument RenderProject(MongoProjectStage stage, PlaceholderTable placeholders)
    {
        var aggRenderer = new MongoAggregationExpressionRenderer();
        var body = new BsonDocument();
        foreach (var projection in stage.Projections)
        {
            body.Add(projection.Alias, aggRenderer.Render(projection.Expression, placeholders));
        }

        // Suppress the default _id unless the projection deliberately emits an "_id" output field.
        if (!body.Contains("_id"))
        {
            body.Add("_id", 0);
        }

        return new BsonDocument("$project", body);
    }
```

(`MongoProjectStage` is in `...NativeTranslation.Stages`, already imported by the factory; `MongoAggregationExpressionRenderer` is in `...NativeTranslation`, the factory's own namespace.)

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj -c "Debug EF10" --filter "FullyQualifiedName~MongoPipelineFactoryTests"`
Expected: PASS (new test + existing factory tests).

- [ ] **Step 5: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoPipelineFactory.cs \
        tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoPipelineFactoryTests.cs
git commit -m "EF-331: Render \$project stage in the pipeline factory"
```

---

## Task 4: Populate the projection slot in `TranslateSelect`

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs`
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/SlotPopulationTests.cs`

**Interfaces:**
- Consumes: `MongoSelectDefinition.AddProjection` (Task 1), `MongoExpressionTranslator(IEntityType)` + `TryTranslateField(Expression, out MongoFieldExpression?) : bool`.
- Produces: after a terminal member-access anonymous/DTO `Select`, `mongoQ.Select.Projection` is populated (aliases = member names) and `IsNativeRepresentable` stays `true`; for any other projection shape `Projection` stays empty and `IsNativeRepresentable` is set `false`.

**Background:** `TranslateSelect` currently sets `IsNativeRepresentable = false` for every non-transparent-identifier selector (line ~178). Replace that unconditional flip with an attempt to populate the native projection; only flip the flag when population fails. The existing shaper-building lines (`ReplacingExpressionVisitor.Replace(...)` + `_projectionBindingExpressionVisitor.Translate(...)` + `UpdateShaperExpression`) stay unchanged — the DOM shaper still needs them.

- [ ] **Step 1: Write the failing test**

Add to `SlotPopulationTests.cs`. Extend the `Customer` model already used there — it has `Id`, `Age`, `Name`, sufficient for these tests.

```csharp
    [Fact]
    public void Anonymous_member_projection_populates_projection_slot()
    {
        var mongoQuery = TranslateToMongoQuery<Customer>(q => q.Select(c => new { c.Name, c.Age }));

        Assert.True(mongoQuery.Select.IsNativeRepresentable);
        Assert.Equal(2, mongoQuery.Select.Projection.Count);
        Assert.Equal("Name", mongoQuery.Select.Projection[0].Alias);
        Assert.Equal("Age", mongoQuery.Select.Projection[1].Alias);
        Assert.IsType<MongoFieldExpression>(mongoQuery.Select.Projection[0].Expression);
    }

    [Fact]
    public void Computed_member_projection_is_not_native()
    {
        var mongoQuery = TranslateToMongoQuery<Customer>(q => q.Select(c => new { Doubled = c.Age * 2 }));

        Assert.False(mongoQuery.Select.IsNativeRepresentable);
        Assert.Empty(mongoQuery.Select.Projection);
    }

    [Fact]
    public void Bare_scalar_projection_is_not_native()
    {
        var mongoQuery = TranslateToMongoQuery<Customer>(q => q.Select(c => c.Name));

        Assert.False(mongoQuery.Select.IsNativeRepresentable);
        Assert.Empty(mongoQuery.Select.Projection);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj -c "Debug EF10" --filter "FullyQualifiedName~SlotPopulationTests"`
Expected: FAIL — `Anonymous_member_projection_populates_projection_slot` fails (`IsNativeRepresentable` is false and `Projection` empty under current behavior).

- [ ] **Step 3: Change `TranslateSelect` to attempt native population**

In `MongoQueryableMethodTranslatingExpressionVisitor.cs`, replace this block inside `TranslateSelect`:

```csharp
        var mongoQueryExpression = (MongoQueryExpression)source.QueryExpression;
        if (!IsTransparentIdentifierSelector(selector))
        {
            mongoQueryExpression.Select.IsNativeRepresentable = false;
        }
```

with:

```csharp
        var mongoQueryExpression = (MongoQueryExpression)source.QueryExpression;
        if (!IsTransparentIdentifierSelector(selector))
        {
            // Native projection pushdown (SP3): a terminal anonymous-type / DTO projection whose leaves are
            // all plain member accesses is lowered to a $project stage. Anything else (bare scalar, computed
            // leaves, entity references, non-member bindings) is not natively representable and falls back.
            if (!TryPopulateNativeProjection(mongoQueryExpression, selector))
            {
                mongoQueryExpression.Select.IsNativeRepresentable = false;
            }
        }
```

- [ ] **Step 4: Add the `TryPopulateNativeProjection` helper**

Add this private static method near `PopulateNativeSlots` (both operate on `MongoSelectDefinition` via `mongoQ.Select`):

```csharp
    // Attempts to populate the native $project slot from a terminal member-access anonymous/DTO selector.
    // Returns true (and fills mongoQ.Select.Projection) only when EVERY leaf is a plain member access the
    // translator can resolve to a document field; otherwise returns false and leaves the slot empty.
    private static bool TryPopulateNativeProjection(MongoQueryExpression mongoQ, LambdaExpression selector)
    {
        var translator = new MongoExpressionTranslator(mongoQ.CollectionExpression.EntityType);
        var projections = new List<MongoProjection>();

        switch (selector.Body)
        {
            case NewExpression newExpression
                when newExpression.Members != null
                     && newExpression.Members.Count == newExpression.Arguments.Count
                     && newExpression.Arguments.Count > 0:
                for (var i = 0; i < newExpression.Arguments.Count; i++)
                {
                    if (!translator.TryTranslateField(newExpression.Arguments[i], out var field))
                        return false;
                    projections.Add(new MongoProjection(newExpression.Members[i].Name, field));
                }
                break;

            case MemberInitExpression memberInit
                when memberInit.NewExpression.Arguments.Count == 0
                     && memberInit.Bindings.Count > 0:
                foreach (var binding in memberInit.Bindings)
                {
                    if (binding is not MemberAssignment assignment)
                        return false;
                    if (!translator.TryTranslateField(assignment.Expression, out var field))
                        return false;
                    projections.Add(new MongoProjection(binding.Member.Name, field));
                }
                break;

            default:
                return false;
        }

        foreach (var projection in projections)
            mongoQ.Select.AddProjection(projection);
        return true;
    }
```

Ensure these usings exist at the top of the file (add any missing): `using System.Collections.Generic;`, `using System.Linq.Expressions;`, `using MongoDB.EntityFrameworkCore.Query.Expressions;`, `using MongoDB.EntityFrameworkCore.Query.NativeTranslation;`.

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj -c "Debug EF10" --filter "FullyQualifiedName~SlotPopulationTests"`
Expected: PASS (all three new tests + existing slot tests).

- [ ] **Step 6: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs \
        tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/SlotPopulationTests.cs
git commit -m "EF-331: Populate native projection slot in TranslateSelect"
```

---

## Task 5: Route native projections through the gate

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoShapedQueryCompilingExpressionVisitor.cs`
- Test: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeGateRoutingTests.cs`

**Interfaces:**
- Consumes: `MongoSelectDefinition.Projection` + `IsNativeRepresentable` (Tasks 1/4), the native factory built inside `CompileShapedQuery` (via `TryBuildNativeFactory`), `MongoProjectionBindingRemovingExpressionVisitor(IEntityType, MongoQueryExpression, ParameterExpression, QueryTrackingBehavior)`.
- Produces: a native execution path (`ExecuteShapedQuery` with a non-null `MongoPipelineFactory`) for representable projections; `CompileShapedQuery` gains a `bool allowStreaming = true` parameter (projection path passes `false`).

**Background:** projected queries are diverted into `VisitProjectedQuery` before reaching `CompileShapedQuery`. Add a native branch at the **top** of `VisitProjectedQuery` (before the `NativeOnly` throw) so representable projections go native and, crucially, do **not** throw under `NativeOnly`. Streaming must be disabled for projections (the streaming rewriter is entity-oriented; the result type is the projected type).

- [ ] **Step 1: Write the failing test**

Add to `NativeGateRoutingTests.cs`. Match the file's existing harness for building a context in a given `MongoQueryMode` and running a query (follow the patterns already in that file — a `NativeOnly` context and `AssertMql`/execution helpers). Add:

```csharp
    [Fact]
    public void Anonymous_member_projection_runs_native_under_NativeOnly()
    {
        // Under NativeOnly a driver-LINQ fallback throws; success proves the $project went native.
        using var db = /* existing helper to create a context with MongoQueryMode.NativeOnly */;
        var results = db.Set<Customer>()
            .Where(c => c.Age > 21)
            .Select(c => new { c.Name, c.Age })
            .ToList();

        Assert.NotNull(results);
    }

    [Fact]
    public void Computed_projection_throws_under_NativeOnly()
    {
        using var db = /* existing helper to create a context with MongoQueryMode.NativeOnly */;
        Assert.Throws<NativeTranslationNotSupportedException>(
            () => db.Set<Customer>().Select(c => new { Doubled = c.Age * 2 }).ToList());
    }
```

> Implementer note: use the concrete context/mode-setup and entity model already present in `NativeGateRoutingTests.cs` rather than the pseudo-comment above. If the file has an `AssertMql` helper, additionally assert the native pipeline contains a `$project` stage for the first test.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj -c "Debug EF10" --filter "FullyQualifiedName~NativeGateRoutingTests.Anonymous_member_projection_runs_native_under_NativeOnly"`
Expected: FAIL — currently `VisitProjectedQuery` throws `NativeTranslationNotSupportedException` under `NativeOnly` for any projection.

- [ ] **Step 3: Add the `allowStreaming` parameter to `CompileShapedQuery`**

Change the signature:

```csharp
    private MethodCallExpression CompileShapedQuery(
        ShapedQueryExpression shapedQueryExpression,
        MongoQueryExpression mongoQueryExpression,
        IEntityType rootEntityType,
        Func<ParameterExpression, QueryTrackingBehavior, System.Linq.Expressions.ExpressionVisitor> createBindingRemover,
        bool allowStreaming = true)
```

Change the `streaming` computation to honour it:

```csharp
        var streaming = allowStreaming
            && nativeFactory != null
            && shapedQueryExpression.ResultCardinality == ResultCardinality.Enumerable
            && StreamingEligibility.IsEligible(rootEntityType)
            && AllPendingLookupsAreStreamableReferences(mongoQueryExpression);
```

(Existing callers pass no fourth-positional extra, so they keep `allowStreaming: true` by default — no change to the entity/mixed call sites.)

- [ ] **Step 4: Add the native projection branch in `VisitProjectedQuery`**

Insert at the very start of `VisitProjectedQuery`, immediately after `VerifyNoClientConstant(shapedQueryExpression.ShaperExpression);`:

```csharp
        var queryMode = ((MongoQueryCompilationContext)QueryCompilationContext).QueryMode;

        // Native projection pushdown (SP3): a terminal member-access anonymous/DTO Select was lowered to a
        // $project slot in the QMTEV. Emit it as a native pipeline and shape the projected documents with the
        // DOM binding-removing shaper (which reads each field by its projection alias). Placed before the
        // NativeOnly guard so a representable projection succeeds natively instead of being rejected.
        if (queryMode != MongoQueryMode.DriverLinq
            && mongoQueryExpression.Select.IsNativeRepresentable
            && mongoQueryExpression.Select.Projection.Count > 0)
        {
            return CompileShapedQuery(shapedQueryExpression, mongoQueryExpression, rootEntityType,
                (bsonDoc, behavior) => new MongoProjectionBindingRemovingExpressionVisitor(
                    rootEntityType, mongoQueryExpression, bsonDoc, behavior),
                allowStreaming: false);
        }
```

Then reuse `queryMode` in the existing `NativeOnly` guard (replace the inline `((MongoQueryCompilationContext)QueryCompilationContext).QueryMode == MongoQueryMode.NativeOnly` with `queryMode == MongoQueryMode.NativeOnly`).

> Correctness note: `CompileShapedQuery` calls `TryBuildNativeFactory`. For the controlled member-access `$project` the lower/render always succeeds, so `nativeFactory` is non-null and the native pipeline is used. If a future shape reached here with a factory that failed to build, `CompileShapedQuery` would emit the driver-LINQ DOM path over `CapturedExpression` (which still carries the `Select`) — correct results, just not native. That is an acceptable safety net, not the expected path for this slice.

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj -c "Debug EF10" --filter "FullyQualifiedName~NativeGateRoutingTests"`
Expected: PASS (both new tests + existing gate-routing tests).

- [ ] **Step 6: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoShapedQueryCompilingExpressionVisitor.cs \
        tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeGateRoutingTests.cs
git commit -m "EF-331: Route representable projections through the native \$project pipeline"
```

---

## Task 6: End-to-end verification, regression sweep, and docs

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md`
- Test: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/ProjectionTests.cs` (add a native-execution correctness test)

**Interfaces:** none new — this task proves the slice works end-to-end and updates docs.

- [ ] **Step 1: Add an end-to-end correctness test**

Add to `ProjectionTests.cs`, following that file's existing fixture/context pattern (default `Native` mode). Assert both the materialized values AND (if the file has an `AssertMql` helper) that the pipeline contains `$project`:

```csharp
    [Fact]
    public void Anonymous_projection_returns_correct_values_and_projects_server_side()
    {
        // <use the file's existing seeded fixture + context>
        var result = context.Set<Customer>()
            .Where(c => c.Age > 21)
            .Select(c => new { c.Name, c.Age })
            .OrderBy(c => c.Name)
            .ToList();

        Assert.All(result, r => Assert.False(string.IsNullOrEmpty(r.Name)));
        // If AssertMql is available in this fixture, assert the emitted pipeline contains a "$project" stage.
    }
```

> Implementer note: replace `context`/`Customer`/fixture references with the concrete types the file already uses. The point is a real round-trip that returns projected anonymous instances under the default `Native` mode.

- [ ] **Step 2: Run the projection + native query functional tests**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj -c "Debug EF10" --filter "FullyQualifiedName~ProjectionTests|FullyQualifiedName~Native|FullyQualifiedName~QueryModeGate"`
Expected: PASS (no regressions; new test green).

- [ ] **Step 3: Full regression — build + test all three EF versions**

Invoke the `/test-all` skill (builds + tests EF8/EF9/EF10 in parallel). Expected: no new failures vs. the pre-change baseline on any target.

- [ ] **Step 4: Native-coverage sweep (must grow, none regress)**

Run the SpecificationTests with the native-only coverage instrument and compare the pass/fail set to `main` baseline:

Run: `MONGODB_EF_NATIVE_ONLY=1 dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj -c "Debug EF10"`
Expected: the pass-set is a **superset** of the pre-change pass-set — anonymous/DTO member-access projections that previously threw now pass; nothing that previously passed regresses.

- [ ] **Step 5: Update `Query/AGENTS.md`**

In the "As-built scope" note, record that native now covers **terminal anonymous-type / DTO member-access projections** via a `$project` stage (bare scalar, computed leaves, mixed, and non-terminal projections still fall back). Add a bullet under the native-translation key entry points for `MongoProjectStage` and the `MongoSelectDefinition.Projection` slot, and note `TranslateSelect` now populates it (mentioning that the seven-operator `PopulateNativeSlots` whitelist is unchanged — `Select` is handled in its own `TranslateSelect` override, as the existing pitfall note already states).

- [ ] **Step 6: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/AGENTS.md \
        tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/ProjectionTests.cs
git commit -m "EF-331: End-to-end projection test + document the native \$project slice"
```

---

## Self-Review notes (coverage against the spec)

- **IR representation** (spec §1) → Task 1 (`MongoProjection` + `MongoSelectDefinition.Projection`).
- **Translation flow / QMTEV population** (spec §2) → Task 4 (`TranslateSelect` → `TryPopulateNativeProjection`).
- **Gate routing** (spec §2) → Task 5 (`VisitProjectedQuery` native branch, `NativeOnly` no longer rejects representable projections).
- **Lowering & rendering** (spec §3) → Task 2 (`MongoProjectStage` last) + Task 3 (`RenderProject`, `_id: 0`, aggregation dialect).
- **DOM materialization** (spec §4) → Task 5 reuses `MongoProjectionBindingRemovingExpressionVisitor` (reads by alias).
- **Parameterized correctness (B2)** — SP3 renders field-ref-only `$project` bodies (no parameters in the projection itself); parameters still only appear in `$match`, already covered by existing factory tests. No new parameterized projection case is in scope (computed leaves are deferred).
- **Verification** (spec §5) → Task 6 (all-EF regression, `MONGODB_EF_NATIVE_ONLY` coverage grows, native-proof via `NativeOnly`).
- **Scope narrowing vs. spec:** the spec's "computed breadth" and bare-scalar shapes were deferred during planning (computed leaves aren't captured as single projections today, and bare-scalar's null-alias doesn't reuse the DOM shaper cleanly). Update the spec's Scope section to match before implementation (see handoff).
