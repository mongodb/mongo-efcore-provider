# Native-Translation Layer Separation (EF-332) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extract native-translation logic out of the EF query dispatcher, unify the two dialect renderers' value-serialization path, and collapse the is-native decision to one authoritative predicate — with no behavior change.

**Architecture:** Pure internal refactor of the `Query/NativeTranslation` cluster. Three new `static` helper types (`MongoValueRenderer`, `NativeSlotPopulator`, `NativeProjectionBinder`) receive code moved verbatim out of `MongoQueryableMethodTranslatingExpressionVisitor` and the two renderers. `MongoSelectDefinition` gains a computed `NativeRoute` that the gate reads instead of a raw mutable bool. `MongoAggregationExpressionRenderer` becomes stateless/static.

**Tech Stack:** C# / .NET (net8.0 for EF8/EF9, net10.0 for EF10), MongoDB C# driver, xUnit (plain `Assert.*`, no FluentAssertions in tests), EF Core internal APIs.

## Global Constraints

- All affected types are `internal`; this is **not** a public-surface or breaking change (AGENTS.md rubric: internal execution path + emitted MQL are not contract).
- **No behavior change.** The acceptance invariant is that the `MONGODB_EF_NATIVE_ONLY=1` spec pass/fail set is **unchanged** vs. the EF-331 baseline, and all FunctionalTests + SpecificationTests stay green on EF8/EF9/EF10.
- `<Nullable>enable</Nullable>` on `src/` — annotate all new types.
- Preserve file BOMs. New `.cs` files must start with the Apache license header (copy verbatim from any sibling in `Query/NativeTranslation/`).
- Multi-EF: build/test each of `Debug EF8`, `Debug EF9`, `Debug EF10`. No new `#if` guards are expected; if a moved method needs one, preserve exactly what was there.
- Branch `EF-332` (already created, stacked on `EF-331`). `nuget.config` stays uncommitted. Frequent commits, one logical change each.
- Build one EF version: `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"`. Test filter: `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build --filter "..."`.

---

## File Structure

**New files (all under `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/`):**
- `MongoValueRenderer.cs` — static; the single value-node → `BsonValue` path (constant/parameter), with the unified diagnostics wrapper.
- `NativeSlotPopulator.cs` — static; `PopulateNativeSlots`, `PagingAlreadyApplied`, `IsNativeRepresentableSlotOperator`, `TranslateCountExpression`.
- `NativeProjectionBinder.cs` — static; `TryPopulateNativeProjection`.

**Modified files:**
- `Query/NativeTranslation/MongoAggregationExpressionRenderer.cs` — becomes `static`; delegates value rendering; loses `RenderValue`/`SerializeConstant`.
- `Query/NativeTranslation/MongoQueryLanguageRenderer.cs` — delegates value rendering; loses `RenderValue`/`ToBsonValue` and the `_aggRenderer` field.
- `Query/NativeTranslation/MongoPipelineFactory.cs` — `RenderProject` calls the static aggregation renderer.
- `Query/Expressions/MongoSelectDefinition.cs` — add `NativeRoute` enum + computed `Route`; rename `IsNativeRepresentable` → internal `MarkNotNativelyRepresentable()` / `_hasUnsupportedOperator`.
- `Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs` — delegate to the extracted types; update the two remaining write sites (`TranslateSelect`, `TranslateOfType`).
- `Query/Visitors/MongoShapedQueryCompilingExpressionVisitor.cs` — read `Select.Route`.
- `Query/AGENTS.md` — narrative update.
- Doc-comment sweep across `MongoInExpression`, `MongoRegexExpression`, `MongoAggregationExpressionRenderer`, `PlaceholderTable`, `MongoSelectLowerer`, `MongoPipelineFactory`, `MongoExpressionTranslator`, `MongoExpression`, `MongoStreamingEntityMaterializerRewriter`, `LookupExpression`.

**Task order rationale:** Renderer consolidation (Tasks 1–2) is independent. Extractions (Tasks 3–4) are pure moves verifiable by the existing suite. The `NativeRoute` semantic change (Task 5) lands *after* the extractions so the flag write-sites are renamed once, in their final homes. Docs (Task 6) and full-matrix verification (Task 7) close out.

---

## Task 1: Introduce `MongoValueRenderer` and unify the diagnostics wrapper

**Files:**
- Create: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoValueRenderer.cs`
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoValueRendererTests.cs` (create)

**Interfaces:**
- Produces: `internal static class MongoValueRenderer` with `internal static BsonValue RenderValue(MongoExpression node, PlaceholderTable placeholders)`.

The two existing `RenderValue` bodies are identical except that the query renderer's `ToBsonValue` wraps serializer failures in `NativeTranslationNotSupportedException` and the aggregation renderer's `SerializeConstant` does not. The unified helper uses the **wrapping** version (strictly safer; the aggregation path gains the diagnostic).

- [ ] **Step 1: Write the failing test**

Create `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoValueRendererTests.cs` (BOM + license header; copy the header from an existing unit test file). The test asserts the *new* behavior — a constant whose serialization fails surfaces as `NativeTranslationNotSupportedException`, and a plain constant/parameter renders as before:

```csharp
using System;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Query.Expressions;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;
using Xunit;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query.NativeTranslation;

public class MongoValueRendererTests
{
    [Fact]
    public void RenderValue_property_less_constant_creates_bson_value()
    {
        var placeholders = new PlaceholderTable();
        var node = new MongoConstantExpression(42, forSerialization: null);

        var result = MongoValueRenderer.RenderValue(node, placeholders);

        Assert.Equal(BsonValue.Create(42), result);
    }

    [Fact]
    public void RenderValue_property_less_parameter_creates_placeholder()
    {
        var placeholders = new PlaceholderTable();
        var node = new MongoParameterExpression("p0", forSerialization: null);

        var result = MongoValueRenderer.RenderValue(node, placeholders);

        Assert.NotNull(result); // a sentinel placeholder value was produced
    }

    [Fact]
    public void RenderValue_unsupported_node_throws_native_not_supported()
    {
        var placeholders = new PlaceholderTable();
        var node = new MongoFieldExpression("x", property: null!);

        Assert.Throws<NativeTranslationNotSupportedException>(
            () => MongoValueRenderer.RenderValue(node, placeholders));
    }
}
```

> Note: If `MongoConstantExpression`/`MongoParameterExpression`/`MongoFieldExpression` ctor signatures differ, match the existing usages in `MongoQueryLanguageRenderer.cs` (constants: `constant.Value`, `constant.ForSerialization`; parameters: `parameter.Name`, `parameter.ForSerialization`; field: `MongoFieldExpression` with an `ElementName`). Adjust the field-construction line to whatever produces a node that is neither constant nor parameter.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10" 2>&1 | tail -5`
Expected: compile error — `MongoValueRenderer` does not exist.

- [ ] **Step 3: Create `MongoValueRenderer`**

Create `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoValueRenderer.cs` (BOM + Apache header copied from `MongoAggregationExpressionRenderer.cs`):

```csharp
using System;
using Microsoft.EntityFrameworkCore.Metadata;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Query.Expressions;
using MongoDB.EntityFrameworkCore.Serializers;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>
/// Renders a <see cref="MongoConstantExpression"/> or <see cref="MongoParameterExpression"/> value node
/// to a <see cref="BsonValue"/>. Shared by both dialect renderers (<see cref="MongoQueryLanguageRenderer"/>
/// and <see cref="MongoAggregationExpressionRenderer"/>) so a constant and a parameter of the same value
/// always emit identical BSON, and so the serializer-failure diagnostics are applied uniformly.
/// </summary>
internal static class MongoValueRenderer
{
    /// <summary>
    /// Renders <paramref name="node"/> (a constant or parameter) to a <see cref="BsonValue"/>, recording
    /// parameters as placeholders in <paramref name="placeholders"/>.
    /// </summary>
    /// <param name="node">The value node to render.</param>
    /// <param name="placeholders">The placeholder table that records parameter sentinels.</param>
    /// <returns>The rendered <see cref="BsonValue"/> (a concrete value or a placeholder sentinel).</returns>
    /// <exception cref="NativeTranslationNotSupportedException">
    /// Thrown when <paramref name="node"/> is not a value node, or a constant cannot be serialized.
    /// </exception>
    internal static BsonValue RenderValue(MongoExpression node, PlaceholderTable placeholders)
    {
        switch (node)
        {
            case MongoConstantExpression constant:
                return constant.ForSerialization is null
                    ? BsonValue.Create(constant.Value)
                    : ToBsonValue(constant.ForSerialization, constant.Value);

            case MongoParameterExpression parameter:
                if (parameter.ForSerialization is null)
                    return placeholders.CreatePlaceholder(parameter.Name, serializer: null);
                var info = BsonSerializerFactory.GetPropertySerializationInfo(parameter.ForSerialization);
                return placeholders.CreatePlaceholder(parameter.Name, info.Serializer);

            default:
                throw new NativeTranslationNotSupportedException(
                    $"Cannot render value node of type '{node.GetType().Name}'.");
        }
    }

    // Serializes value to a BsonValue using the property's serializer, coercing the CLR type first so the
    // serializer's hard cast succeeds. Coerces to the property's CLR type (compile-time path); the factory
    // coerces to the serializer's ValueType — these differ for value-converted properties, so the caller's
    // IProperty target is used. Serializer failures are surfaced as NativeTranslationNotSupportedException
    // so the query falls back (or throws under NativeOnly) rather than crashing with a raw cast error.
    private static BsonValue ToBsonValue(IProperty property, object? value)
    {
        var info = BsonSerializerFactory.GetPropertySerializationInfo(property);
        try
        {
            value = BsonValueSerializer.Coerce(property.ClrType, value);
            return BsonValueSerializer.SerializeThroughWriter(info.Serializer, value);
        }
        catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException
                                       or InvalidOperationException)
        {
            throw new NativeTranslationNotSupportedException(
                $"Native predicate translation cannot serialize the constant value for property '{property.Name}'.");
        }
    }
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10" && dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build --filter "FullyQualifiedName~MongoValueRendererTests"`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoValueRenderer.cs \
        tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoValueRendererTests.cs
git commit -m "EF-332: Add shared MongoValueRenderer with unified diagnostics"
```

---

## Task 2: Route both renderers through `MongoValueRenderer`; make the aggregation renderer static

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoAggregationExpressionRenderer.cs`
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoQueryLanguageRenderer.cs`
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoPipelineFactory.cs:115-131`

**Interfaces:**
- Consumes: `MongoValueRenderer.RenderValue` (Task 1).
- Produces: `MongoAggregationExpressionRenderer` becomes `internal static class`; its `Render` becomes `internal static BsonValue Render(MongoExpression node, PlaceholderTable placeholders)`.

- [ ] **Step 1: Convert `MongoAggregationExpressionRenderer` to static and delegate value rendering**

In `MongoAggregationExpressionRenderer.cs`:
- Change the declaration to `internal static class MongoAggregationExpressionRenderer`.
- Make `Render` and `RenderBinary` `static`.
- In the `Render` switch, replace the `RenderValue(node, placeholders)` arm call with `MongoValueRenderer.RenderValue(node, placeholders)`.
- Delete the private `RenderValue` and `SerializeConstant` methods entirely.
- Remove now-unused `using`s (`Microsoft.EntityFrameworkCore.Metadata`, `MongoDB.EntityFrameworkCore.Serializers`) if the build flags them.

Resulting `Render`:

```csharp
public static BsonValue Render(MongoExpression node, PlaceholderTable placeholders)
    => node switch
    {
        MongoFieldExpression field => "$" + field.ElementName,
        MongoConstantExpression or MongoParameterExpression => MongoValueRenderer.RenderValue(node, placeholders),
        MongoBinaryExpression binary => RenderBinary(binary, placeholders),
        _ => throw new NativeTranslationNotSupportedException(
            $"MongoAggregationExpressionRenderer does not support node type '{node.GetType().Name}'.")
    };
```

- [ ] **Step 2: Update `MongoQueryLanguageRenderer` to delegate and drop its aggregation-renderer field**

In `MongoQueryLanguageRenderer.cs`:
- Delete the field `private readonly MongoAggregationExpressionRenderer _aggRenderer = new();`.
- Change `RenderAsExpr` to call the static renderer:
  ```csharp
  private BsonDocument RenderAsExpr(MongoExpression node, PlaceholderTable placeholders)
      => new BsonDocument("$expr", MongoAggregationExpressionRenderer.Render(node, placeholders));
  ```
- Delete the private `RenderValue` and `ToBsonValue` methods.
- Replace every internal call to `RenderValue(...)` with `MongoValueRenderer.RenderValue(...)`, and every call to `ToBsonValue(prop, val)` with the shared path. The `ToBsonValue` call sites (bool-field `trueValue`, the `$in`-array item loop) need a `BsonValue` from an `IProperty` + raw value — wrap those in a `MongoConstantExpression` and call `MongoValueRenderer.RenderValue`:
  ```csharp
  // was: var trueValue = ToBsonValue(field.Property, true);
  var trueValue = MongoValueRenderer.RenderValue(
      new MongoConstantExpression(true, field.Property), placeholders);
  ```
  ```csharp
  // was: array.Add(ToBsonValue(constant.ForSerialization!, item));
  array.Add(MongoValueRenderer.RenderValue(
      new MongoConstantExpression(item, constant.ForSerialization!), placeholders));
  ```
  Confirm the `MongoConstantExpression` ctor is `(object? value, IProperty? forSerialization)` — match the existing construction in this file. Remove any now-unused `using`s.

- [ ] **Step 3: Update `MongoPipelineFactory.RenderProject`**

In `MongoPipelineFactory.cs`, `RenderProject` (line ~115): delete `var aggRenderer = new MongoAggregationExpressionRenderer();` and change the body call to the static method:

```csharp
private static BsonDocument RenderProject(MongoProjectStage stage, PlaceholderTable placeholders)
{
    var body = new BsonDocument();
    foreach (var projection in stage.Projections)
    {
        body.Add(projection.Alias, MongoAggregationExpressionRenderer.Render(projection.Expression, placeholders));
    }

    if (!body.Contains("_id"))
    {
        body.Add("_id", 0);
    }

    return new BsonDocument("$project", body);
}
```

- [ ] **Step 4: Build and run the native + gate tests**

Run: `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10" && dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build --filter "FullyQualifiedName~NativeTranslation|FullyQualifiedName~QueryModeGate"`
Expected: PASS, no regressions. (The `$expr` and `$project` rendering paths now route through the shared value renderer.)

- [ ] **Step 5: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoAggregationExpressionRenderer.cs \
        src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoQueryLanguageRenderer.cs \
        src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoPipelineFactory.cs
git commit -m "EF-332: Route both dialect renderers through MongoValueRenderer; make aggregation renderer static"
```

---

## Task 3: Extract `NativeSlotPopulator` from the QMTEV

**Files:**
- Create: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeSlotPopulator.cs`
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs` (remove moved methods; delegate at call site line ~151)

**Interfaces:**
- Produces: `internal static class NativeSlotPopulator` with:
  - `internal static void PopulateNativeSlots(ShapedQueryExpression shapedQuery, MethodInfo methodDefinition, MethodCallExpression call)`
  - `internal static bool IsNativeRepresentableSlotOperator(MethodInfo methodDefinition)`

This is a **pure move** — no logic changes (the `IsNativeRepresentable` flag stays as-is here; it is renamed in Task 5). `PagingAlreadyApplied` and `TranslateCountExpression` move too (they are used only by `PopulateNativeSlots`).

- [ ] **Step 1: Create `NativeSlotPopulator.cs` with the moved code**

Create the file (BOM + Apache header). Move verbatim from the QMTEV: `PopulateNativeSlots` (lines ~679–776), `PagingAlreadyApplied` (~782–783), `IsNativeRepresentableSlotOperator` (~787–796), and `TranslateCountExpression` (~1048–1057). Add the `using`s the moved bodies need: `System.Linq.Expressions`, `System.Reflection`, `Microsoft.EntityFrameworkCore.Query`, `Microsoft.EntityFrameworkCore.Query.QueryableMethods` container namespace, `MongoDB.EntityFrameworkCore.Query.Expressions`, and whatever namespace `UnwrapLambdaFromQuote` / `NativeQueryParameter` live in (copy the QMTEV's `using` block as the starting set and prune).

```csharp
namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>
/// Populates the native-translation slots (<see cref="Expressions.MongoSelectDefinition"/> Predicate /
/// Orderings / Offset / Limit) on a <see cref="Expressions.MongoQueryExpression"/> for the seven
/// slot-bearing LINQ operators, and owns the whitelist that suppresses the non-native catch-all. Extracted
/// from the QMTEV (EF-332) so native-translation logic no longer lives inside the EF query dispatcher.
/// </summary>
internal static class NativeSlotPopulator
{
    // PopulateNativeSlots, PagingAlreadyApplied, IsNativeRepresentableSlotOperator, TranslateCountExpression
    // moved here verbatim; change each moved method's visibility so PopulateNativeSlots and
    // IsNativeRepresentableSlotOperator are `internal static` (the visitor calls both) and keep
    // PagingAlreadyApplied / TranslateCountExpression `private static`.
}
```

Make `PopulateNativeSlots` and `IsNativeRepresentableSlotOperator` `internal static`; keep `PagingAlreadyApplied` and `TranslateCountExpression` `private static`.

- [ ] **Step 2: Delete the moved methods from the QMTEV and delegate**

In `MongoQueryableMethodTranslatingExpressionVisitor.cs`:
- Delete `PopulateNativeSlots`, `PagingAlreadyApplied`, `IsNativeRepresentableSlotOperator`, and `TranslateCountExpression`.
- At the call site (line ~151) change `PopulateNativeSlots(shapedQueryExpression, methodDefinition, methodCallExpression);` to `NativeSlotPopulator.PopulateNativeSlots(shapedQueryExpression, methodDefinition, methodCallExpression);`.
- If `IsNativeRepresentableSlotOperator` is referenced anywhere else in the QMTEV, repoint to `NativeSlotPopulator.IsNativeRepresentableSlotOperator`. (Grep confirms the only reference is inside the moved `PopulateNativeSlots`, so no other repoint is expected.)

- [ ] **Step 3: Build and run native + gate tests**

Run: `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10" && dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build --filter "FullyQualifiedName~NativeTranslation|FullyQualifiedName~QueryModeGate"`
Expected: PASS — behavior identical, code relocated.

- [ ] **Step 4: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeSlotPopulator.cs \
        src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs
git commit -m "EF-332: Extract NativeSlotPopulator from the QMTEV"
```

---

## Task 4: Extract `NativeProjectionBinder` from the QMTEV

**Files:**
- Create: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeProjectionBinder.cs`
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs` (remove `TryPopulateNativeProjection`; delegate in `TranslateSelect`)

**Interfaces:**
- Produces: `internal static class NativeProjectionBinder` with `internal static bool TryPopulateNativeProjection(MongoQueryExpression mongoQ, LambdaExpression selector)`.

Pure move of `TryPopulateNativeProjection` (QMTEV lines ~226–280). `IsTransparentIdentifierSelector` **stays in the QMTEV** (it is EF-dispatch logic, not native translation).

- [ ] **Step 1: Create `NativeProjectionBinder.cs`**

Create the file (BOM + Apache header). Move `TryPopulateNativeProjection` verbatim; make it `internal static`. Add `using`s: `System.Collections.Generic`, `System.Linq.Expressions`, `Microsoft.EntityFrameworkCore.Query`, `MongoDB.EntityFrameworkCore.Query.Expressions`.

```csharp
namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>
/// Attempts to populate the native <c>$project</c> slot (<see cref="Expressions.MongoSelectDefinition"/>
/// Projection) from a terminal member-access anonymous/DTO selector. Extracted from the QMTEV (EF-332).
/// Returns <see langword="true"/> (and fills <c>Select.Projection</c>) only when every leaf is a plain
/// member access the translator resolves to a document field; otherwise leaves the slot empty.
/// </summary>
internal static class NativeProjectionBinder
{
    // TryPopulateNativeProjection moved here verbatim as `internal static`.
}
```

- [ ] **Step 2: Delete the method from the QMTEV and delegate in `TranslateSelect`**

In `MongoQueryableMethodTranslatingExpressionVisitor.cs`:
- Delete `TryPopulateNativeProjection`.
- In `TranslateSelect` (line ~181) change `if (!TryPopulateNativeProjection(mongoQueryExpression, selector))` to `if (!NativeProjectionBinder.TryPopulateNativeProjection(mongoQueryExpression, selector))`.

- [ ] **Step 3: Build and run native + gate + projection tests**

Run: `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10" && dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build --filter "FullyQualifiedName~NativeTranslation|FullyQualifiedName~QueryModeGate|FullyQualifiedName~Projection"`
Expected: PASS.

- [ ] **Step 4: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeProjectionBinder.cs \
        src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs
git commit -m "EF-332: Extract NativeProjectionBinder from the QMTEV"
```

---

## Task 5: Collapse the is-native decision to a computed `NativeRoute`

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoSelectDefinition.cs:108-117`
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeSlotPopulator.cs` (write sites)
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs` (write sites in `TranslateSelect`, `TranslateOfType`)
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoShapedQueryCompilingExpressionVisitor.cs:170-172,188,413,426`
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoSelectDefinitionTests.cs` (create)

**Interfaces:**
- Produces on `MongoSelectDefinition`: `internal enum NativeRoute { Fallback, WholeEntity, Projection }` (namespace `MongoDB.EntityFrameworkCore.Query.Expressions`), a computed `internal NativeRoute Route { get; }`, and a mutating `internal void MarkNotNativelyRepresentable()`. The `IsNativeRepresentable` property is removed.

- [ ] **Step 1: Write the failing test for `Route`**

Create `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoSelectDefinitionTests.cs` (BOM + license header):

```csharp
using MongoDB.EntityFrameworkCore.Query.Expressions;
using Xunit;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query.NativeTranslation;

public class MongoSelectDefinitionTests
{
    [Fact]
    public void Route_defaults_to_whole_entity()
    {
        var select = new MongoSelectDefinition();
        Assert.Equal(NativeRoute.WholeEntity, select.Route);
    }

    [Fact]
    public void Route_is_projection_when_a_projection_is_added()
    {
        var select = new MongoSelectDefinition();
        select.AddProjection(new MongoProjection("a", new MongoFieldExpression("a", property: null!)));
        Assert.Equal(NativeRoute.Projection, select.Route);
    }

    [Fact]
    public void Route_is_fallback_after_MarkNotNativelyRepresentable()
    {
        var select = new MongoSelectDefinition();
        select.AddProjection(new MongoProjection("a", new MongoFieldExpression("a", property: null!)));
        select.MarkNotNativelyRepresentable();
        Assert.Equal(NativeRoute.Fallback, select.Route);
    }
}
```

> Match `MongoFieldExpression`/`MongoProjection` ctors to their real signatures (see `Query/Expressions/MongoProjection.cs` — `record struct MongoProjection(string Alias, MongoExpression Expression)`).

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10" 2>&1 | tail -5`
Expected: compile errors — `NativeRoute` / `Route` / `MarkNotNativelyRepresentable` do not exist.

- [ ] **Step 3: Update `MongoSelectDefinition`**

Replace the `IsNativeRepresentable` region (lines 108–116) with:

```csharp
    // ── Native-representable gate ─────────────────────────────────────────────────

    private bool _hasUnsupportedOperator;

    /// <summary>
    /// Records that this query contains a shape the native path cannot handle, forcing
    /// <see cref="Route"/> to <see cref="NativeRoute.Fallback"/>. Population-time signal set by the
    /// slot populator / projection binder / QMTEV overrides; never unset.
    /// </summary>
    public void MarkNotNativelyRepresentable()
        => _hasUnsupportedOperator = true;

    /// <summary>
    /// The single authoritative native-execution decision for this query, computed from the populated
    /// slots. <see cref="NativeRoute.Fallback"/> when any unsupported operator was seen; otherwise
    /// <see cref="NativeRoute.Projection"/> when a <c>$project</c> was populated; otherwise
    /// <see cref="NativeRoute.WholeEntity"/>. The compile-time gate reads this and nothing else.
    /// </summary>
    public NativeRoute Route
        => _hasUnsupportedOperator ? NativeRoute.Fallback
            : _projections.Count > 0 ? NativeRoute.Projection
            : NativeRoute.WholeEntity;
```

Add the enum at the end of the file (inside the namespace, after the class):

```csharp
/// <summary>
/// The native-execution route the compile-time gate takes for a query, derived from
/// <see cref="MongoSelectDefinition.Route"/>.
/// </summary>
internal enum NativeRoute
{
    /// <summary>Not natively representable — use the driver-LINQ fallback (or throw under NativeOnly).</summary>
    Fallback,

    /// <summary>Native pipeline over whole-entity results.</summary>
    WholeEntity,

    /// <summary>Native pipeline ending in a pushed-down <c>$project</c>.</summary>
    Projection
}
```

- [ ] **Step 4: Update all former `IsNativeRepresentable = false` write sites**

Replace every `X.Select.IsNativeRepresentable = false;` with `X.Select.MarkNotNativelyRepresentable();`:
- In `NativeSlotPopulator.cs`: all arms of `PopulateNativeSlots` (the `mongoQ.Select.IsNativeRepresentable = false;` lines).
- In `MongoQueryableMethodTranslatingExpressionVisitor.cs`: `TranslateSelect` (line ~183) and `TranslateOfType` (line ~616).

Note the previous code set `IsNativeRepresentable = true` implicitly by default (never re-set to true anywhere except the default). The `TranslateSelect` success path relied on `Projection.Count > 0` to signal a native projection — that is now expressed by `Route == Projection` automatically, so **no explicit "set true" is needed**. Confirm by grep that no site sets `IsNativeRepresentable = true`:

```bash
grep -rn "IsNativeRepresentable" src/MongoDB.EntityFrameworkCore/   # expect: no matches after edits
```

- [ ] **Step 5: Update the gate to read `Route`**

In `MongoShapedQueryCompilingExpressionVisitor.cs`:
- Native-projection branch (lines ~170–172):
  ```csharp
  if (queryMode != MongoQueryMode.DriverLinq
      && mongoQueryExpression.Select.Route == NativeRoute.Projection)
  ```
  (Drop the separate `IsNativeRepresentable` + `Projection.Count > 0` conjuncts — `Route == Projection` encodes both. Update the explanatory comment at ~184-187 to state the gate now keys off the single `Route` predicate.)
- Whole-entity native branch — wherever the code previously tested `Select.IsNativeRepresentable` for the entity path (lines ~413 and ~426): replace `mongoQueryExpression.Select.IsNativeRepresentable` (positive) with `mongoQueryExpression.Select.Route != NativeRoute.Fallback`, and `!mongoQueryExpression.Select.IsNativeRepresentable` (negative) with `mongoQueryExpression.Select.Route == NativeRoute.Fallback`. Read the surrounding conditions first and preserve their exact boolean sense.
- The `NativeOnly` projected-query guard (line ~188) stays exactly where it is, after the `Route == Projection` branch.

Add `using MongoDB.EntityFrameworkCore.Query.Expressions;` if `NativeRoute` is not already in scope.

- [ ] **Step 6: Build and run native + gate tests**

Run: `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10" && dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build --filter "FullyQualifiedName~NativeTranslation|FullyQualifiedName~QueryModeGate|FullyQualifiedName~MongoSelectDefinitionTests"`
Expected: PASS (including the 3 new `MongoSelectDefinitionTests`).

- [ ] **Step 7: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoSelectDefinition.cs \
        src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeSlotPopulator.cs \
        src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs \
        src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoShapedQueryCompilingExpressionVisitor.cs \
        tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoSelectDefinitionTests.cs
git commit -m "EF-332: Collapse is-native decision to computed MongoSelectDefinition.Route"
```

---

## Task 6: Documentation-consistency sweep + AGENTS.md update

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoInExpression.cs`, `MongoRegexExpression.cs` (ctor `<param>` docs)
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoAggregationExpressionRenderer.cs` (`Render` doc)
- Modify: `PlaceholderTable.cs`, `MongoSelectLowerer.cs`, `MongoPipelineFactory.cs`, `MongoQueryLanguageRenderer.cs`, `MongoExpressionTranslator.cs` (remove stale "Task N" refs)
- Modify: `MongoExpression.cs`, `MongoSelectLowerer.cs`, `MongoExpressionTranslator.cs`, `MongoStreamingEntityMaterializerRewriter.cs` (`<para>` vs bare blank lines)
- Modify: `MongoExpression.cs` (`**bold**` → `<em>`), `LookupExpression.cs` (bare `$lookup` → `<c>$lookup</c>`, fix "stage" mislabel)
- Modify: `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md`

This task is docs/comments only — no behavior change. Fold the whole sweep into one commit.

- [ ] **Step 1: Add missing `<param>` docs**

Add `<summary>`/`<param>` XML docs to the `MongoInExpression` and `MongoRegexExpression` constructors, matching the style of a documented sibling ctor (e.g. `MongoBinaryExpression`). Add a `<summary>`/`<param>`/`<returns>`/`<exception>` block to `MongoAggregationExpressionRenderer.Render` mirroring `MongoQueryLanguageRenderer.Render`.

- [ ] **Step 2: Remove stale "Task N" references**

Grep and replace each ephemeral scheduling reference with the concept (or delete the phrase):

```bash
grep -rn "Task [0-9]" src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/
```
Fix each hit in `PlaceholderTable.cs`, `MongoSelectLowerer.cs`, `MongoPipelineFactory.cs`, `MongoQueryLanguageRenderer.cs`, `MongoExpressionTranslator.cs`.

- [ ] **Step 3: Fix `<para>` / markdown-in-XML**

- In `MongoExpression.cs`, `MongoSelectLowerer.cs`, `MongoExpressionTranslator.cs`, `MongoStreamingEntityMaterializerRewriter.cs`: replace bare blank lines between doc paragraphs with `<para>…</para>`.
- In `MongoExpression.cs`: `**bold**` → `<em>bold</em>`.
- In `LookupExpression.cs`: bare `$lookup` → `<c>$lookup</c>`; fix the comment that calls a data-holder a "stage".

- [ ] **Step 4: Update `Query/AGENTS.md`**

Update the "Common pitfalls" and "Key entry points" sections:
- The catch-all / nine-operator-whitelist pitfall now points at `NativeSlotPopulator` (not "inline in the QMTEV"); projection points at `NativeProjectionBinder`.
- Replace `IsNativeRepresentable` references with the `_hasUnsupportedOperator` + computed `NativeRoute` model, and state the gate reads `Select.Route` as the single is-native signal.
- Note the shared `MongoValueRenderer` and that `MongoAggregationExpressionRenderer` is now static.

- [ ] **Step 5: Build (docs shouldn't break compilation, but XML-doc warnings can)**

Run: `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10" 2>&1 | tail -5`
Expected: build succeeds, no new warnings.

- [ ] **Step 6: Commit**

```bash
git add -A src/MongoDB.EntityFrameworkCore/Query/
git commit -m "EF-332: NativeTranslation doc-consistency sweep + AGENTS.md update"
```

---

## Task 7: Full-matrix verification (behavior-preservation gate)

**Files:** none (verification only).

- [ ] **Step 1: Build + test all three EF versions**

Invoke the `/test-all` skill, or run manually per version:

```bash
for v in EF8 EF9 EF10; do
  dotnet build MongoDB.EFCoreProvider.sln -c "Debug $v" && \
  dotnet test MongoDB.EFCoreProvider.sln -c "Debug $v" --no-build
done
```
Expected: all green on EF8, EF9, EF10.

- [ ] **Step 2: Confirm the native coverage set is unchanged**

Run the spec suite under the native-only coverage instrument and compare the pass/fail set to the EF-331 baseline:

```bash
MONGODB_EF_NATIVE_ONLY=1 dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build \
  --filter "FullyQualifiedName~SpecificationTests"
```
Expected: identical pass/fail set to EF-331 (the refactor must not move which queries go native). Investigate any delta before proceeding — a change here means the refactor altered behavior.

- [ ] **Step 3: Final residual-reference grep**

```bash
grep -rn "IsNativeRepresentable\|new MongoAggregationExpressionRenderer\|TryPopulateNativeProjection\|PopulateNativeSlots" \
  src/MongoDB.EntityFrameworkCore/ --include=*.cs
```
Expected: `PopulateNativeSlots` / `TryPopulateNativeProjection` appear only inside `NativeSlotPopulator.cs` / `NativeProjectionBinder.cs` (and their delegating call sites); `IsNativeRepresentable` and `new MongoAggregationExpressionRenderer` have zero matches.

- [ ] **Step 4: No commit needed** (verification only). Report results.

---

## Self-Review

- **Spec coverage:** §1 extraction → Tasks 3, 4. §2 `NativeRoute` → Task 5. §3 shared value renderer → Tasks 1, 2. §4 static aggregation renderer / drop ad-hoc instances → Task 2. §5 doc sweep → Task 6. §6 AGENTS.md → Task 6. Verification plan → Task 7. All covered.
- **Placeholders:** none — each code step shows the actual code or an exact move-and-rename instruction with the source lines.
- **Type consistency:** `MongoValueRenderer.RenderValue(MongoExpression, PlaceholderTable)` (Task 1) is consumed unchanged in Task 2. `MongoAggregationExpressionRenderer.Render` static signature (Task 2) is consumed in `MongoPipelineFactory` and `MongoQueryLanguageRenderer` (Task 2). `NativeRoute` / `Route` / `MarkNotNativelyRepresentable` (Task 5) are consumed by the gate in the same task. `PopulateNativeSlots` / `IsNativeRepresentableSlotOperator` (Task 3) and `TryPopulateNativeProjection` (Task 4) signatures match their QMTEV originals.

## Post-implementation (per design doc §Delivery)

After Task 7 is green: request code review (`query-reviewer` + the cross-cutting reviewers via `/review-ef-core-provider`), then squash the branch to a single commit with a PR-style message (keep an `EF-332-presquash` backup until merge), per the stacked-PR workflow.
