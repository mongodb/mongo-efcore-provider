# Native query-translation foundation (sub-project 1) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stand up the first working native read path — a real Mongo query expression tree + lowerer + renderer + compile-time pipeline factory, plus the native execution path / streaming materializer / DOM shaper / query-mode gate — at **parity** with the spike's native slice (single-collection filter / sort / paging + single-level reference Include over whole-entity results), with driver-LINQ as the gated fallback for everything else.

**Architecture:** The QMTEV stops returning `null` for `Where`/`OrderBy`/`ThenBy`/`Skip`/`Take`/`Select`; instead it populates logical slots on a `MongoSelectExpression` (custom `Expression`, EF `SelectExpression`-style). A `MongoSelectLowerer` turns those slots into a typed `MongoPipelineStage[]` IR; a `MongoQueryLanguageRenderer` renders that to `BsonDocument[]` **once at compile time** into a `MongoPipelineFactory` whose `MongoParameterExpression` placeholders bind per execution. The native-vs-driver decision moves to compile time (deterministic, on `IsNativeRepresentable` + lowering/rendering success), replacing the spike's per-execution try/catch. The streaming materializer, DOM shaper, dual-shaper enumerable, and native execution branch in `MongoClientWrapper.Execute` are **reproduced faithfully** from the spike (reference, not ported wholesale).

**Tech Stack:** C# / .NET (`net10.0` for EF10, `net8.0` for EF8/EF9), EF Core 8/9/10 (multi-config build), MongoDB C# driver (LINQ v3 + raw `MongoDB.Bson`), xUnit + FluentAssertions, BenchmarkDotNet (EF-324 harness).

## Reference material (read before starting)

- **Design (the *how*):** `docs/superpowers/specs/2026-06-20-mongo-query-ast-foundation-design.md`.
- **Overview (the *what/why*):** `docs/superpowers/specs/2026-06-20-mongo-query-ast-foundation-overview.md`.
- **Program design (SP0–SP7 context):** `docs/superpowers/specs/2026-06-23-native-query-provider-design.md` (on `main` since EF-322 merged).
- **Spike — REFERENCE ONLY, do NOT port wholesale; reproduce the named files faithfully:** branch `spike/low-level-provider`. Read with `git show "origin/spike/low-level-provider:<path>"` (quote the whole arg — zsh treats a bare `$VAR:path` as a history modifier). The native code lives under `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/`.

## Global Constraints

- **Branch base:** implementation is cut from **`EF-324-benchmarks`** (PR #321, not yet merged) so SP0's benchmark harness is present; rebase onto `main` once EF-324 merges. The docs already live on `EF-323-ast-foundation` off `main`.
- **Multi-EF targeting via build *configurations*, not TFMs:** `Debug|Release EF8`, `Debug|Release EF9`, `Debug|Release EF10`. Build a single version with the **quoted** config: `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"`. Conditional code uses the `EF8` / `EF9` / `EF10` define constants. Common guards: `#if EF8 || EF9` (legacy) and `#if !EF8` (EF9+).
- **`<Nullable>enable</Nullable>`** on all `src/` code — annotate new types accordingly.
- **`<NoWarn>EF1001</NoWarn>`** — consuming EF Core internal APIs is intentional.
- **Preserve BOMs** on any existing file that has one. New files need no BOM.
- **Tests run serially** (`[assembly: CollectionBehavior(DisableTestParallelization = true)]`); each functional test gets a unique DB via `TestDatabaseNamer.GetUniqueDatabaseName()`.
- **Functional/Specification tests need MongoDB** (replica set — `SaveChanges` uses transactions). `MONGODB_URI` / `ATLAS_URI`, else Docker auto-spins. Benchmarks need a replica set via `MONGODB_URI`.
- **Async:** new async methods take a `CancellationToken` and flow it through unchanged; library code uses `ConfigureAwait(false)`.
- **QueryContext parameter access guard** (reused verbatim from `MongoShapedQueryCompilingExpressionVisitor.cs:621-633`):
  ```csharp
  #if EF8 || EF9
      // queryContext.ParameterValues.TryGetValue(param.Name, out var value)
  #else
      // queryContext.Parameters[queryParam.Name]  (QueryParameterExpression)
  #endif
  ```
- **Don't break the bulk seam.** `ExecuteUpdate`/`ExecuteDelete` cross to Storage as *behavior* (`MongoBulkPlan` delegates), not as data. This plan does not touch it.
- **Parity, not breadth.** Anything outside the slice (predicate breadth, `$project` pushdown, scalar cardinality, collection/nested/filtered Includes, `GroupBy`/`SelectMany`/set ops/`Distinct`/`OfType`/`VectorSearch`, non-canonical paging) must set `IsNativeRepresentable = false` and fall back — not be half-translated.

---

## File Structure

All new translator code under `src/MongoDB.EntityFrameworkCore/Query/`.

- `NativeTranslation/NativeTranslationNotSupportedException.cs` — **[NEW]** internal signal for un-representable shapes (reproduce spike).
- `NativeTranslation/MongoQueryMode.cs` — **[NEW]** the gate's effective-mode resolution (replaces spike's `NativeQueryMode` + env var with the public enum).
- `Expressions/MongoExpression.cs` (+ `MongoFieldExpression`, `MongoConstantExpression`, `MongoParameterExpression`, `MongoBinaryExpression`, `MongoUnaryExpression`, `MongoBinaryOperator`/`MongoUnaryOperator` enums) — **[NEW]** dialect-agnostic `SqlExpression`-analog hierarchy.
- `Expressions/MongoOrdering.cs` — **[NEW]** `(MongoExpression KeySelector, bool Ascending)`.
- `Expressions/MongoSelectExpression.cs` — **[NEW]** query-expression-tree root with logical slots; evolves `MongoQueryExpression`.
- `NativeTranslation/Stages/Mongo{Match,Sort,Skip,Limit,Lookup,Unwind}Stage.cs` (+ `MongoPipelineStage.cs` base) — **[NEW]** typed stage IR.
- `NativeTranslation/MongoExpressionTranslator.cs` — **[NEW]** EF predicate/key-selector body → `MongoExpression` (carries `NativeExpressionHelpers` element-name + serializer coercion).
- `NativeTranslation/MongoSelectLowerer.cs` — **[NEW]** `MongoSelectExpression → MongoPipelineStage[]` (absorbs `NativeLookupStages` emission).
- `NativeTranslation/MongoQueryLanguageRenderer.cs` — **[NEW]** `MongoExpression → $match BSON` (query dialect; `CombineAnd` from `MongoPredicateTranslator`).
- `NativeTranslation/MongoExprRenderer.cs` — **[NEW]** stubbed `$expr` (aggregation-expression) renderer seam; throws "not yet implemented".
- `NativeTranslation/MongoPipelineFactory.cs` — **[NEW]** rendered template + placeholder table; `Build(parameterValues) → BsonDocument[]`.
- `NativeTranslation/{MongoStreamingEntityMaterializerRewriter,BsonRowReader,StreamingEligibility}.cs` — **[NEW, reproduced faithfully from spike]**.
- `NativeTranslation/DispatchingQueryingEnumerable.cs` — **[NEW, reproduced]** dual-shaper.
- Modify: `Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs` — slot population.
- Modify: `Visitors/MongoShapedQueryCompilingExpressionVisitor.cs` — the compile-time gate + native shaper compilation.
- Modify: `MongoExecutableQuery.cs` — add the `MongoPipelineFactory` (native pipeline carrier).
- Modify: `QueryingEnumerable.cs` — native source row lifecycle.
- Modify: `Storage/MongoClientWrapper.cs` — native `Aggregate` branch (reproduce spike `:95-115`).
- Modify: `Infrastructure/MongoOptionsExtension.cs`, `Infrastructure/MongoDbContextOptionsBuilder.cs`, `Query/MongoQueryCompilationContext.cs` (+ its factory) — the public `MongoQueryMode` option.
- Modify: `Query/AGENTS.md` — describe the rebuilt architecture (carried-over cleanup).
- Delete (only after the native path covers the slice — Task 16): `NativeTranslation/MongoPipelineTranslator.cs`, `NativeTranslation/MongoPredicateTranslator.cs` (if reproduced as scaffolding) — superseded by lowerer + renderer + `MongoExpressionTranslator`.
- Tests: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/*` (new unit tests), `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/QueryModeOptionTests.cs`.
- Benchmarks (EF-324 harness): `benchmarks/MongoDB.EntityFrameworkCore.Benchmarks/HeadlineBenchmarks.cs` (+ `BenchmarkConfig.cs`) — add the **native** config; `results/perf-baseline.md` — append native numbers.

---

## Task 1: Branch setup + native-translation scaffolding

**Files:**
- Create: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeTranslationNotSupportedException.cs`
- Reference: spike `NativeTranslation/NativeTranslationNotSupportedException.cs`

**Interfaces:**
- Produces: `internal sealed class NativeTranslationNotSupportedException : Exception` with `(string message)` ctor — thrown by `MongoExpressionTranslator`/lowerer/renderer when a node can't be represented natively; caught at the gate to flip to the driver path.

- [ ] **Step 1: Cut the implementation branch from EF-324**

```bash
git fetch origin EF-324-benchmarks
git switch -c EF-323-impl origin/EF-324-benchmarks
# Bring the EF-323 design + this plan onto the impl branch:
git checkout EF-323-ast-foundation -- docs/superpowers/specs/2026-06-20-mongo-query-ast-foundation-design.md \
  docs/superpowers/specs/2026-06-20-mongo-query-ast-foundation-overview.md \
  docs/superpowers/plans/2026-06-25-mongo-query-ast-foundation-plan.md
```

- [ ] **Step 2: Verify the EF-324 harness is present**

Run: `ls benchmarks/MongoDB.EntityFrameworkCore.Benchmarks/HeadlineBenchmarks.cs benchmarks/MongoDB.EntityFrameworkCore.Benchmarks/results/perf-baseline.md`
Expected: both paths exist.

- [ ] **Step 3: Build the solution to confirm a green baseline**

Run: `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"`
Expected: Build succeeded.

- [ ] **Step 4: Add the exception type (reproduce from spike)**

```csharp
internal sealed class NativeTranslationNotSupportedException : Exception
{
    public NativeTranslationNotSupportedException(string message) : base(message) { }
}
```
(Use the spike file's license header; namespace `MongoDB.EntityFrameworkCore.Query.NativeTranslation`.)

- [ ] **Step 5: Build + commit**

Run: `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"` → Build succeeded.
```bash
git add src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeTranslationNotSupportedException.cs docs/
git commit -m "EF-323: Scaffold native-translation foundation (branch off EF-324)"
```

---

## Task 2: Public `MongoQueryMode` config option + gate plumbing

**Files:**
- Create: `src/MongoDB.EntityFrameworkCore/Infrastructure/MongoQueryMode.cs` (the public enum)
- Create: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoQueryMode.cs` (effective-mode resolution helper — name it `MongoQueryModeResolver` to avoid clashing with the enum)
- Modify: `src/MongoDB.EntityFrameworkCore/Infrastructure/MongoOptionsExtension.cs`
- Modify: `src/MongoDB.EntityFrameworkCore/Infrastructure/MongoDbContextOptionsBuilder.cs`
- Modify: `src/MongoDB.EntityFrameworkCore/Query/MongoQueryCompilationContext.cs` + `Query/Factories/MongoQueryCompilationContextFactory.cs`
- Reference: spike `MongoOptionsExtension.cs:233-239` (`UseNativeQuery`), `MongoDbContextOptionsBuilder.cs:49-53` (`UseNativeQuery`), `MongoQueryCompilationContextFactory.cs:49`, `NativeTranslation/NativeQueryMode.cs:36-49`
- Test: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/QueryModeOptionTests.cs`

**Interfaces:**
- Produces:
  - `public enum MongoQueryMode { Native, DriverLinq, NativeOnly }` (default `Native`).
  - `MongoOptionsExtension.QueryMode { get; }` + `WithQueryMode(MongoQueryMode)` (clone pattern, like `WithDatabaseName`); folded into `ServiceProviderHash`/`ShouldUseSameServiceProvider`/`LogFragment`/`PopulateDebugInfo` (annotation key `"Mongo:QueryMode"`).
  - `MongoDbContextOptionsBuilder.UseQueryMode(MongoQueryMode mode)`.
  - `MongoQueryCompilationContext.QueryMode { get; }` (read from the extension in the factory).

**Note (API stability):** `MongoQueryMode` and `UseQueryMode` are net-new public surface — additive, not a break. The annotation key `"Mongo:QueryMode"` is new (no compiled-model break vs. the latest release). The spike's `MONGODB_EF_NATIVE_QUERY` env var and internal `NativeQueryMode` are **not** reproduced — the public enum replaces them per the design.

- [ ] **Step 1: Write the failing test**

```csharp
public class QueryModeOptionTests
{
    [Fact]
    public void QueryMode_defaults_to_Native()
    {
        var options = new DbContextOptionsBuilder<DbContext>();
        new MongoDbContextOptionsBuilder(options); // no UseQueryMode call
        var ext = options.Options.FindExtension<MongoOptionsExtension>();
        ext!.QueryMode.Should().Be(MongoQueryMode.Native);
    }

    [Fact]
    public void UseQueryMode_round_trips_through_the_extension()
    {
        var options = new DbContextOptionsBuilder<DbContext>();
        var optionsExtension = new MongoOptionsExtension().WithConnectionString("mongodb://localhost");
        ((IDbContextOptionsBuilderInfrastructure)options).AddOrUpdateExtension(optionsExtension);
        new MongoDbContextOptionsBuilder(options).UseQueryMode(MongoQueryMode.DriverLinq);
        options.Options.FindExtension<MongoOptionsExtension>()!.QueryMode.Should().Be(MongoQueryMode.DriverLinq);
    }
}
```
(Mirror the existing options-test setup pattern in `tests/.../FunctionalTests/` — check a sibling test for the exact `MongoDbContextOptionsBuilder` construction idiom and adapt.)

- [ ] **Step 2: Run it; verify it fails to compile (`MongoQueryMode`/`UseQueryMode`/`QueryMode` undefined)**

Run: `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --filter "FullyQualifiedName~QueryModeOptionTests"`
Expected: compile failure — symbols not defined.

- [ ] **Step 3: Add the enum + option, modeled on the spike's `UseNativeQuery` plumbing**

`Infrastructure/MongoQueryMode.cs`:
```csharp
namespace MongoDB.EntityFrameworkCore.Infrastructure;

/// <summary>Selects how the provider translates LINQ queries to MongoDB.</summary>
public enum MongoQueryMode
{
    /// <summary>Native translation when representable; driver-LINQ fallback otherwise. (Default.)</summary>
    Native,
    /// <summary>Always use the driver's LINQ provider (the pre-rebuild behavior).</summary>
    DriverLinq,
    /// <summary>Native translation only; throw at compile time on an un-representable query (diagnostic).</summary>
    NativeOnly
}
```
In `MongoOptionsExtension`: add `public virtual MongoQueryMode QueryMode { get; private set; } = MongoQueryMode.Native;`, copy it in the copy-ctor, add `WithQueryMode(...)` (clone-and-set, like `WithDatabaseName` at `:115`), and fold it into `ServiceProviderHash` / `ShouldUseSameServiceProvider` / `LogFragment` / `PopulateDebugInfo` exactly where the spike folds `UseNativeQuery` (`:301`, `:313`, `:356`, `:322`).
In `MongoDbContextOptionsBuilder`: add `UseQueryMode(MongoQueryMode mode)` returning `WithOption(e => e.WithQueryMode(mode))` (match the existing builder method idiom).

- [ ] **Step 4: Plumb the mode onto `MongoQueryCompilationContext`**

Add `public MongoQueryMode QueryMode { get; }` to `MongoQueryCompilationContext`; set it in `MongoQueryCompilationContextFactory.Create` from `dependencies...FindExtension<MongoOptionsExtension>()?.QueryMode ?? MongoQueryMode.Native` (mirror spike factory `:49`).

- [ ] **Step 5: Run the test; verify it passes**

Run: `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --filter "FullyQualifiedName~QueryModeOptionTests"`
Expected: PASS (2 tests).

- [ ] **Step 6: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Infrastructure/ src/MongoDB.EntityFrameworkCore/Query/ tests/
git commit -m "EF-323: Add public MongoQueryMode option (Native/DriverLinq/NativeOnly)"
```

---

## Task 3: `MongoExpression` hierarchy (dialect-agnostic nodes)

**Files:**
- Create: `src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoExpression.cs` (abstract base + the operator enums)
- Create: `.../Expressions/MongoFieldExpression.cs`, `MongoConstantExpression.cs`, `MongoParameterExpression.cs`, `MongoBinaryExpression.cs`, `MongoUnaryExpression.cs`, `MongoOrdering.cs`
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoExpressionTests.cs`

**Interfaces:**
- Produces:
  - `internal abstract class MongoExpression : Expression { public override Type Type { get; } public override ExpressionType NodeType => ExpressionType.Extension; }`
  - `MongoFieldExpression(IProperty property, string elementName)` — exposes `Property`, `ElementName`.
  - `MongoConstantExpression(object? value, IProperty? forSerialization)` — `Value`, `ForSerialization`.
  - `MongoParameterExpression(string name, IProperty? forSerialization)` — `Name`, `ForSerialization` (the B2 placeholder).
  - `enum MongoBinaryOperator { Equal, NotEqual, LessThan, LessThanOrEqual, GreaterThan, GreaterThanOrEqual, AndAlso, OrElse }`
  - `MongoBinaryExpression(MongoBinaryOperator op, MongoExpression left, MongoExpression right)` — `Operator`, `Left`, `Right`.
  - `enum MongoUnaryOperator { Not }`
  - `MongoUnaryExpression(MongoUnaryOperator op, MongoExpression operand)` — `Operator`, `Operand`.
  - `readonly record struct MongoOrdering(MongoExpression KeySelector, bool Ascending)`.
- Consumes: `IProperty` (Metadata).

- [ ] **Step 1: Write the failing test**

```csharp
public class MongoExpressionTests
{
    [Fact]
    public void Binary_node_exposes_operator_and_operands()
    {
        var left = new MongoConstantExpression(1, forSerialization: null);
        var right = new MongoConstantExpression(2, forSerialization: null);
        var bin = new MongoBinaryExpression(MongoBinaryOperator.LessThan, left, right);

        bin.Operator.Should().Be(MongoBinaryOperator.LessThan);
        bin.Left.Should().BeSameAs(left);
        bin.Right.Should().BeSameAs(right);
        bin.NodeType.Should().Be(ExpressionType.Extension);
    }

    [Fact]
    public void Ordering_carries_key_and_direction()
    {
        var key = new MongoConstantExpression(0, null);
        var ordering = new MongoOrdering(key, Ascending: false);
        ordering.KeySelector.Should().BeSameAs(key);
        ordering.Ascending.Should().BeFalse();
    }
}
```

- [ ] **Step 2: Run; verify fail to compile**

Run: `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --filter "FullyQualifiedName~MongoExpressionTests"`
Expected: compile failure.

- [ ] **Step 3: Implement the node hierarchy**

Implement each as `internal sealed` subclass of `MongoExpression`. `MongoExpression` overrides `VisitChildren` to return `this` by default (leaf nodes); `MongoBinaryExpression`/`MongoUnaryExpression` override `VisitChildren` to visit operands and rebuild on change (the EF `SqlExpression` idiom). Keep them **dialect-agnostic** — no BSON here. Annotate nullability (`object?`, `IProperty?`). See `Expressions/MongoQueryExpression.cs` for the existing custom-`Expression` style in this codebase.

- [ ] **Step 4: Run; verify pass**

Run: `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --filter "FullyQualifiedName~MongoExpressionTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/Expressions/ tests/
git commit -m "EF-323: Add dialect-agnostic MongoExpression node hierarchy"
```

---

## Task 4: `MongoSelectExpression` (logical slots)

**Files:**
- Create: `src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoSelectExpression.cs`
- Reference: `Expressions/MongoQueryExpression.cs` (the type it evolves) + `MongoQueryExpression.Lookup.cs`
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoSelectExpressionTests.cs`

**Interfaces:**
- Produces (slots per design doc § *`MongoSelectExpression`*):
  - `MongoCollectionExpression Source { get; }`
  - `MongoExpression? Predicate { get; set; }`
  - `IReadOnlyList<MongoOrdering> Orderings { get; }` + `void ResetOrderings(MongoOrdering first)` + `void AppendOrdering(MongoOrdering next)`
  - `MongoExpression? Limit { get; set; }`, `MongoExpression? Offset { get; set; }`
  - `ProjectionMapping Projection { get; }` (carried; client-side at parity)
  - `IReadOnlyList<LookupExpression> Lookups { get; }` (fed by the kept reconstruction — Task 13)
  - `Expression? CapturedExpression { get; set; }` (raw LINQ chain — coexistence rule)
  - `bool IsNativeRepresentable { get; set; }` (default `true`; QMTEV flips to `false` on any non-parity shape)
  - `void AddPredicateConjunct(MongoExpression conjunct)` — AND-combines into `Predicate`.
- Consumes: `MongoCollectionExpression`, `LookupExpression`, `MongoExpression`, `MongoOrdering` (Task 3).

**Note:** `MongoSelectExpression` keeps `CapturedExpression` + projection plumbing so the existing fallback + shaper binding keep working; it *adds* the structured slots and becomes the source of truth for the native path. Decide during implementation whether to extend `MongoQueryExpression` in place or introduce `MongoSelectExpression` as its successor — the design frames it as the evolution of `MongoQueryExpression`; prefer extending the existing type's slot set to minimize churn to the QMTEV/shaper plumbing, renaming only if a clean cut is lower-risk. Record the choice in the commit message.

- [ ] **Step 1: Write the failing test**

```csharp
public class MongoSelectExpressionTests
{
    [Fact]
    public void AddPredicateConjunct_ANDs_into_a_single_predicate()
    {
        var select = TestSelect();          // helper builds a MongoSelectExpression over a stub collection
        var a = new MongoConstantExpression(true, null);
        var b = new MongoConstantExpression(true, null);

        select.AddPredicateConjunct(a);
        select.AddPredicateConjunct(b);

        select.Predicate.Should().BeOfType<MongoBinaryExpression>()
            .Which.Operator.Should().Be(MongoBinaryOperator.AndAlso);
    }

    [Fact]
    public void New_select_is_native_representable_by_default()
        => TestSelect().IsNativeRepresentable.Should().BeTrue();
}
```

- [ ] **Step 2: Run; verify fail**. Run: `dotnet test ... --filter "FullyQualifiedName~MongoSelectExpressionTests"` → compile failure.

- [ ] **Step 3: Implement the slots.** `AddPredicateConjunct(c)` sets `Predicate = Predicate is null ? c : new MongoBinaryExpression(AndAlso, Predicate, c)`. Orderings backed by a `List<MongoOrdering>` (`ResetOrderings` clears+adds; `AppendOrdering` adds).

- [ ] **Step 4: Run; verify pass.** Expected: PASS.

- [ ] **Step 5: Commit.**
```bash
git add src/MongoDB.EntityFrameworkCore/Query/Expressions/ tests/
git commit -m "EF-323: Add MongoSelectExpression logical-slot query tree"
```

---

## Task 5: `MongoExpressionTranslator` (EF body → `MongoExpression`)

**Files:**
- Create: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.cs`
- Reference: spike `NativeTranslation/MongoPredicateTranslator.cs` (body-walking half) + `NativeExpressionHelpers.cs` (element-name resolution + serializer value coercion — **carry over verbatim, already correct**)
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoExpressionTranslatorTests.cs`

**Interfaces:**
- Produces: `internal sealed class MongoExpressionTranslator` with
  `bool TryTranslate(Expression efBody, [NotNullWhen(true)] out MongoExpression? result)` — returns `false` (and `result = null`) for any node in the un-translatable set (nullable-equality, numeric casts on the member side, method calls, dictionary/list access, composite-PK member access, unserializable values) instead of throwing.
- Consumes: `MongoFieldExpression`/`MongoConstant`/`MongoParameter`/`MongoBinary`/`MongoUnary` (Task 3); `IProperty.GetElementName()`, `LookupExpression.GetFieldPath` (for `_id.<prop>` composite-key field paths).

**Note:** The acceptance set must be **exactly** where the spike falls back today — no wider, no narrower. Member access maps to `MongoFieldExpression` via the carried-over element-name resolution; key-property paths use `LookupExpression.GetFieldPath` (`_id.<prop>` for composite PKs) per the design's *Composite keys* note. A `QueryParameterExpression` (EF10) / prefixed `ParameterExpression` (EF8/EF9) maps to `MongoParameterExpression`; a captured constant maps to `MongoConstantExpression`.

- [ ] **Step 1: Write the failing tests**

```csharp
public class MongoExpressionTranslatorTests
{
    [Fact]
    public void Translates_simple_comparison_to_a_field_op()
    {
        // x => x.Age > 21  over an entity with int Age mapped to element "Age"
        var (body, prop) = Predicate<Customer>(c => c.Age > 21);
        var translator = NewTranslator<Customer>();

        translator.TryTranslate(body, out var mongo).Should().BeTrue();
        var bin = mongo.Should().BeOfType<MongoBinaryExpression>().Subject;
        bin.Operator.Should().Be(MongoBinaryOperator.GreaterThan);
        bin.Left.Should().BeOfType<MongoFieldExpression>().Which.ElementName.Should().Be("Age");
        bin.Right.Should().BeOfType<MongoConstantExpression>().Which.Value.Should().Be(21);
    }

    [Fact]
    public void Conjunction_maps_to_AndAlso()
    {
        var (body, _) = Predicate<Customer>(c => c.Age > 21 && c.Age < 65);
        NewTranslator<Customer>().TryTranslate(body, out var mongo).Should().BeTrue();
        mongo.Should().BeOfType<MongoBinaryExpression>().Which.Operator.Should().Be(MongoBinaryOperator.AndAlso);
    }

    [Fact]
    public void Unsupported_node_reports_not_translatable()
    {
        // method call on the member side — outside the parity acceptance set
        var (body, _) = Predicate<Customer>(c => c.Name.StartsWith("A"));
        NewTranslator<Customer>().TryTranslate(body, out var mongo).Should().BeFalse();
        mongo.Should().BeNull();
    }
}
```
(Build the `Predicate<T>(...)` / `NewTranslator<T>()` helpers against a minimal in-memory `IModel` — see existing UnitTests that construct an `IModel`/`IEntityType` for translator tests and copy the pattern.)

- [ ] **Step 2: Run; verify fail.** `--filter "FullyQualifiedName~MongoExpressionTranslatorTests"` → compile failure.

- [ ] **Step 3: Implement** as an `ExpressionVisitor`-style recursive translator: map `BinaryExpression` comparison/logical nodes to `MongoBinaryExpression`, `UnaryExpression(Not)`/bare-bool to `MongoUnaryExpression`, member access to `MongoFieldExpression`, parameters to `MongoParameterExpression`, constants to `MongoConstantExpression`. Any node outside the set ⇒ `return false`. Lift element-name + serializer coercion helpers from `NativeExpressionHelpers` verbatim.

- [ ] **Step 4: Run; verify pass.** Expected: PASS (3 tests).

- [ ] **Step 5: Commit.**
```bash
git add src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.cs tests/
git commit -m "EF-323: Add MongoExpressionTranslator (EF body -> MongoExpression)"
```

---

## Task 6: QMTEV slot population

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs` (the `Translate*` overrides at `:763-804` that return `null` today; `CapturedExpression` set at `:152`)
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/SlotPopulationTests.cs`

**Interfaces:**
- Consumes: `MongoExpressionTranslator` (Task 5), `MongoSelectExpression` slots (Task 4).
- Produces: populated slots on the `MongoSelectExpression` per the design's *Population in the QMTEV* table.

| Override | Action |
|---|---|
| `TranslateWhere` | `TryTranslate` body → `MongoExpression`; `AddPredicateConjunct`. Un-translatable ⇒ `IsNativeRepresentable = false`, return `source`. |
| `TranslateOrderBy`/`TranslateThenBy` (+ descending) | translate key selector; `OrderBy` ⇒ `ResetOrderings`, `ThenBy` ⇒ `AppendOrdering`. Un-translatable key ⇒ not-representable. |
| `TranslateSkip`/`TranslateTake` | set `Offset`/`Limit` to `MongoConstant`/`MongoParameter`; enforce canonical order (Skip-before-Take, single each) else not-representable. |
| `TranslateSelect` | pure entity-materialization Select ⇒ no-op (existing behavior at `:159-175`). A *projecting* Select ⇒ `IsNativeRepresentable = false` (deferred to SP3). |

**Coexistence rule (critical):** every override still returns a valid `ShapedQueryExpression` and the QMTEV still sets `CapturedExpression` at `:152` regardless of slot population — the driver-LINQ fallback must stay intact. Setting a slot must never *remove* the captured chain.

- [ ] **Step 1: Write the failing tests**

```csharp
public class SlotPopulationTests
{
    [Fact]
    public void Where_populates_the_predicate_slot()
    {
        var select = TranslateToSelect<Customer>(q => q.Where(c => c.Age > 21));
        select.Predicate.Should().NotBeNull();
        select.IsNativeRepresentable.Should().BeTrue();
        select.CapturedExpression.Should().NotBeNull("the raw chain is always captured");
    }

    [Fact]
    public void OrderBy_then_ThenBy_preserves_order()
    {
        var select = TranslateToSelect<Customer>(q => q.OrderBy(c => c.Age).ThenByDescending(c => c.Name));
        select.Orderings.Should().HaveCount(2);
        select.Orderings[0].Ascending.Should().BeTrue();
        select.Orderings[1].Ascending.Should().BeFalse();
    }

    [Fact]
    public void Where_after_Take_is_not_native_representable()
    {
        var select = TranslateToSelect<Customer>(q => q.Take(10).Where(c => c.Age > 21));
        select.IsNativeRepresentable.Should().BeFalse();
        select.CapturedExpression.Should().NotBeNull("fallback chain still captured");
    }

    [Fact]
    public void Projecting_Select_is_not_native_representable()
    {
        var select = TranslateToSelect<Customer>(q => q.Select(c => c.Name));
        select.IsNativeRepresentable.Should().BeFalse();
    }
}
```
(`TranslateToSelect<T>(Func<IQueryable<T>,IQueryable>)` drives the real EF query pipeline to the post-QMTEV `MongoSelectExpression` — model it on how existing UnitTests invoke the translation visitors; if no such harness exists, build a minimal one that compiles a query against an `IModel` and runs `MongoQueryableMethodTranslatingExpressionVisitor`.)

- [ ] **Step 2: Run; verify fail.** `--filter "FullyQualifiedName~SlotPopulationTests"` → assertions fail (slots not populated yet).

- [ ] **Step 3: Implement the overrides.** Replace the `return null` bodies (`:785-804`) with slot population per the table; flip `IsNativeRepresentable = false` and `return source` on any un-translatable input or non-canonical order. Do **not** alter `:152` capture.

- [ ] **Step 4: Run; verify pass.** Expected: PASS (4 tests).

- [ ] **Step 5: Build EF8 too (signatures differ across versions).** Run: `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF8"` → Build succeeded. Add `#if` guards if any `Translate*` signature differs (see `TranslateExecuteUpdate` EF8/EF9 split at `:621-632` for the pattern).

- [ ] **Step 6: Commit.**
```bash
git add src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs tests/
git commit -m "EF-323: Populate native query slots in the QMTEV"
```

---

## Task 7: Typed stage IR (`MongoPipelineStage`)

**Files:**
- Create: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/Stages/MongoPipelineStage.cs` (abstract base) + `MongoMatchStage.cs`, `MongoSortStage.cs`, `MongoSkipStage.cs`, `MongoLimitStage.cs`, `MongoLookupStage.cs`, `MongoUnwindStage.cs`
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoPipelineStageTests.cs`

**Interfaces:**
- Produces (plain typed stages — **not** `Expression`s; 1:1 with a rendered pipeline doc):
  - `internal abstract class MongoPipelineStage`
  - `MongoMatchStage(MongoExpression Predicate)`, `MongoSortStage(IReadOnlyList<MongoOrdering> Orderings)`, `MongoSkipStage(MongoExpression Offset)`, `MongoLimitStage(MongoExpression Limit)`, `MongoLookupStage(LookupExpression Lookup)`, `MongoUnwindStage(LookupExpression Lookup)`.

- [ ] **Step 1: Write the failing test**
```csharp
public class MongoPipelineStageTests
{
    [Fact]
    public void Match_stage_carries_its_predicate()
    {
        var predicate = new MongoConstantExpression(true, null);
        new MongoMatchStage(predicate).Predicate.Should().BeSameAs(predicate);
    }
}
```
- [ ] **Step 2: Run; verify fail.** → compile failure.
- [ ] **Step 3: Implement** as simple records/sealed classes deriving from `MongoPipelineStage`.
- [ ] **Step 4: Run; verify pass.**
- [ ] **Step 5: Commit.**
```bash
git add src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/Stages/ tests/
git commit -m "EF-323: Add typed MongoPipelineStage IR"
```

---

## Task 8: `MongoSelectLowerer` (slots → stage IR)

**Files:**
- Create: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoSelectLowerer.cs`
- Reference: spike `NativeTranslation/NativeLookupStages.cs` (the `$lookup`/`$unwind` *emission* logic + reference-only / no-pipeline-stages / no-`_lookup_`-localField guards — move it here)
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoSelectLowererTests.cs`

**Interfaces:**
- Produces: `internal sealed class MongoSelectLowerer { IReadOnlyList<MongoPipelineStage> Lower(MongoSelectExpression select); }`
- Consumes: `MongoSelectExpression` slots (Task 4), stage IR (Task 7), `LookupExpression` emission guards.

**Note:** Canonical order `$match → $sort → $skip → $limit → $lookup/$unwind`; drop empty slots (no predicate ⇒ no `MongoMatchStage`). At the foundation, `Lookups` is still fed by the kept reconstruction (Task 13) — the lowerer just consumes the resulting list.

- [ ] **Step 1: Write the failing tests**
```csharp
public class MongoSelectLowererTests
{
    [Fact]
    public void Empty_slots_lower_to_no_stages()
        => new MongoSelectLowerer().Lower(TestSelect()).Should().BeEmpty();

    [Fact]
    public void Filter_sort_paging_lower_in_canonical_order()
    {
        var select = TestSelect();
        select.AddPredicateConjunct(new MongoConstantExpression(true, null));
        select.AppendOrdering(new MongoOrdering(new MongoConstantExpression(0, null), true));
        select.Offset = new MongoConstantExpression(5, null);
        select.Limit = new MongoConstantExpression(10, null);

        var stages = new MongoSelectLowerer().Lower(select);

        stages.Select(s => s.GetType()).Should().ContainInOrder(
            typeof(MongoMatchStage), typeof(MongoSortStage), typeof(MongoSkipStage), typeof(MongoLimitStage));
    }
}
```
- [ ] **Step 2: Run; verify fail.** → assertions fail.
- [ ] **Step 3: Implement** the canonical-order walk; append `MongoLookupStage`+`MongoUnwindStage` for each `LookupExpression` (port `NativeLookupStages` emission + guards).
- [ ] **Step 4: Run; verify pass.**
- [ ] **Step 5: Commit.**
```bash
git add src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoSelectLowerer.cs tests/
git commit -m "EF-323: Add MongoSelectLowerer (slots -> stage IR)"
```

---

## Task 9: `MongoQueryLanguageRenderer` + stubbed `$expr` seam

**Files:**
- Create: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoQueryLanguageRenderer.cs`
- Create: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExprRenderer.cs` (stub seam)
- Reference: spike `MongoPredicateTranslator.cs` — `CombineAnd` (field merge, operator-doc merge, `$and` fallback on collision) + the query/match dialect shape (`{field:{$gt:v}}`, `$and`/`$or`, bare-bool, `$ne`)
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoQueryLanguageRendererTests.cs`

**Interfaces:**
- Produces:
  - `internal sealed class MongoQueryLanguageRenderer { BsonValue Render(MongoExpression predicate, PlaceholderTable placeholders); }` — emits `$match`-dialect BSON, recording `MongoParameterExpression` sites into `placeholders` (Task 10 owns `PlaceholderTable`); constants serialized inline via the property's `IBsonSerializer`.
  - `internal sealed class MongoExprRenderer` — throws `NativeTranslationNotSupportedException("$expr rendering not yet implemented")`; wired into the lowerer's dialect-selection seam (entry point for SP2).
- Consumes: `MongoExpression` (Task 3), `NativeTranslationNotSupportedException` (Task 1).

**Note:** Dialect choice is made by the caller (lowerer), never in the renderer or the node types. For parity only the query/match dialect is built; the `$expr` renderer stays a throwing stub.

- [ ] **Step 1: Write the failing tests** (render against an inline placeholder table; assert the BSON shape)
```csharp
public class MongoQueryLanguageRendererTests
{
    [Fact]
    public void Renders_greater_than_in_query_dialect()
    {
        var field = new MongoFieldExpression(AgeProperty, "Age");
        var pred = new MongoBinaryExpression(MongoBinaryOperator.GreaterThan, field, new MongoConstantExpression(21, AgeProperty));
        var rendered = new MongoQueryLanguageRenderer().Render(pred, new PlaceholderTable());
        rendered.Should().Be(BsonDocument.Parse("{ Age: { $gt: 21 } }"));
    }

    [Fact]
    public void Merges_two_ranges_on_one_field()
    {
        var field = new MongoFieldExpression(AgeProperty, "Age");
        var pred = new MongoBinaryExpression(MongoBinaryOperator.AndAlso,
            new MongoBinaryExpression(MongoBinaryOperator.GreaterThan, field, new MongoConstantExpression(21, AgeProperty)),
            new MongoBinaryExpression(MongoBinaryOperator.LessThan, field, new MongoConstantExpression(65, AgeProperty)));
        var rendered = new MongoQueryLanguageRenderer().Render(pred, new PlaceholderTable());
        rendered.Should().Be(BsonDocument.Parse("{ Age: { $gt: 21, $lt: 65 } }"));
    }
}
```
- [ ] **Step 2: Run; verify fail.** → compile failure (`PlaceholderTable` from Task 10 not yet present — implement Task 10's `PlaceholderTable` type first if needed, or stub it minimally here and complete in Task 10). Expected: FAIL.
- [ ] **Step 3: Implement** the query-dialect renderer; port `CombineAnd`/field-merge from `MongoPredicateTranslator`. Add the throwing `MongoExprRenderer` stub.
- [ ] **Step 4: Run; verify pass.**
- [ ] **Step 5: Commit.**
```bash
git add src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoQueryLanguageRenderer.cs src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExprRenderer.cs tests/
git commit -m "EF-323: Add query-dialect renderer + stubbed \$expr seam"
```

---

## Task 10: `MongoPipelineFactory` (B2 template + per-execution bind)

**Files:**
- Create: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoPipelineFactory.cs` (+ `PlaceholderTable`)
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoPipelineFactoryTests.cs`

**Interfaces:**
- Produces:
  - `internal sealed class PlaceholderTable` — records `(pipeline location, parameter name, IBsonSerializer)` entries; `int Add(string parameterName, IBsonSerializer serializer)` returns a sentinel id used in the template.
  - `internal sealed class MongoPipelineFactory { BsonDocument[] Build(IReadOnlyDictionary<string, object?> parameterValues); }` — produced once at compile time from the rendered template + `PlaceholderTable`; clones the template and substitutes each placeholder per execution, serializing through the recorded serializer. Constants are baked into the template.
- Consumes: rendered template (Task 9), `PlaceholderTable`.

**Note:** This is the perf delta over the spike — lowering/rendering leave the hot path; `Build` is clone-and-substitute. Bridge `QueryContext.Parameters` (EF10) vs `QueryContext.ParameterValues` (EF8/EF9) at the call site that produces `parameterValues` (the global guard), not inside `Build`.

- [ ] **Step 1: Write the failing test (the cache-correctness case)**
```csharp
public class MongoPipelineFactoryTests
{
    [Fact]
    public void Same_template_binds_different_parameter_values_across_executions()
    {
        // Build a factory whose $match is { Age: { $gt: <param p0> } } with an Int32 serializer.
        var factory = BuildAgeGreaterThanFactory(parameterName: "p0");

        var first  = factory.Build(new Dictionary<string, object?> { ["p0"] = 21 });
        var second = factory.Build(new Dictionary<string, object?> { ["p0"] = 40 });

        first[0].Should().Be(BsonDocument.Parse("{ $match: { Age: { $gt: 21 } } }"));
        second[0].Should().Be(BsonDocument.Parse("{ $match: { Age: { $gt: 40 } } }"));
        // template is not mutated between builds:
        factory.Build(new Dictionary<string, object?> { ["p0"] = 21 })[0]
            .Should().Be(first[0]);
    }
}
```
- [ ] **Step 2: Run; verify fail.** → compile/assertion failure.
- [ ] **Step 3: Implement** `PlaceholderTable` + `MongoPipelineFactory.Build` (deep-clone template, substitute sentinels, serialize each value via the recorded serializer into a `BsonValue`).
- [ ] **Step 4: Run; verify pass.** Expected: PASS.
- [ ] **Step 5: Commit.**
```bash
git add src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoPipelineFactory.cs tests/
git commit -m "EF-323: Add MongoPipelineFactory (compile-time template + per-exec bind)"
```

---

## Task 11: Native execution path (`MongoExecutableQuery` + `MongoClientWrapper`)

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/MongoExecutableQuery.cs` (add the native pipeline carrier)
- Modify: `src/MongoDB.EntityFrameworkCore/Storage/MongoClientWrapper.cs` (native `Aggregate` branch)
- Modify: `src/MongoDB.EntityFrameworkCore/Query/QueryingEnumerable.cs` (native source row lifecycle)
- Reference: spike `Storage/MongoClientWrapper.cs:95-115` (the `executableQuery.NativePipeline is { } stages` branch over `RawBsonDocument`), spike `MongoExecutableQuery` (`NativePipeline`/`Session`/`Streaming` additions), spike `QueryingEnumerable` (generic-over-`TSource` row disposal)
- Test: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativePipelineExecutionTests.cs` (real DB)

**Interfaces:**
- Produces: `MongoExecutableQuery` gains a `MongoPipelineFactory? NativePipeline` (or equivalent carrier per the design's "`MongoExecutableQuery` gains the `MongoPipelineFactory`"); when set, `MongoClientWrapper.Execute` runs `collection.Aggregate(rawPipeline)` instead of the driver-LINQ `Provider.Execute`. The per-execution `NativePipeline` documents = `factory.Build(parameterValues)`.
- Consumes: `MongoPipelineFactory` (Task 10).

**Note:** Reproduce the spike's native branch faithfully (raw `BsonDocument[]` pipeline → `Aggregate` → cursor). Storage's `Execute` still receives a `BsonDocument[]`; the boundary is unchanged in spirit. Keep the driver-LINQ branch intact for fallback.

- [ ] **Step 1: Write the failing functional test**
```csharp
public class NativePipelineExecutionTests : IClassFixture<TemporaryDatabaseFixture>
{
    [Fact]
    public void Where_executes_natively_and_returns_correct_rows()
    {
        // seed Customers; query in Native mode; assert results AND assert the captured MQL is a
        // raw $match pipeline (via TestMqlLoggerFactory / AssertMql), not a driver-LINQ pipeline.
    }
}
```
(Use the existing `AssertMql` + `TemporaryDatabaseFixture` helpers; copy a sibling functional Query test's fixture setup.)
- [ ] **Step 2: Run; verify fail.** Expected: FAIL (no native carrier wired).
- [ ] **Step 3: Implement** the `MongoExecutableQuery` carrier + the `MongoClientWrapper.Execute` native branch (reproduce spike `:95-115`) + `QueryingEnumerable` source handling.
- [ ] **Step 4: Run; verify pass.** Expected: PASS.
- [ ] **Step 5: Commit.**
```bash
git add src/MongoDB.EntityFrameworkCore/Query/MongoExecutableQuery.cs src/MongoDB.EntityFrameworkCore/Storage/MongoClientWrapper.cs src/MongoDB.EntityFrameworkCore/Query/QueryingEnumerable.cs tests/
git commit -m "EF-323: Add native BsonDocument[] execution path"
```

---

## Task 12: Streaming materializer (reproduce faithfully)

**Files:**
- Create: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoStreamingEntityMaterializerRewriter.cs`, `BsonRowReader.cs`, `StreamingEligibility.cs`
- Reference (reproduce, do not redesign): spike files of the same names; spike specs `docs/superpowers/specs/2026-06-18-streaming-materializer-design.md` and `2026-06-18-streaming-owned-collections-design.md`
- Test: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/StreamingMaterializerTests.cs`

**Interfaces:**
- Produces: `StreamingEligibility.IsEligible(IEntityType)` (single-property PK, owned-reference-only navigations, recursive); `BsonRowReader` (forward-only `IBsonReader` over `RawBsonDocument`); `MongoStreamingEntityMaterializerRewriter` (rewrites the EF materializer to read via the reader, no DOM build).
- Consumes: native cursor rows (Task 11).

**Note:** The known per-row double-pass overhead is **out of scope** (SP7) — reproduce as-is, do not "fix" it; just don't regress it.

- [ ] **Step 1: Write the failing test** — whole-entity no-track read returns correct POCOs via the streaming path; assert eligibility gates a non-eligible entity (multi-prop PK) to the DOM path.
- [ ] **Step 2: Run; verify fail.**
- [ ] **Step 3: Reproduce** the three files faithfully from the spike (preserve their structure; adapt namespaces/`#if` only).
- [ ] **Step 4: Run; verify pass.**
- [ ] **Step 5: Commit.**
```bash
git add src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/ tests/
git commit -m "EF-323: Reproduce streaming materializer + eligibility"
```

---

## Task 13: DOM shaper, dual-shaper enumerable, and Lookups slot feeding

**Files:**
- Create: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/DispatchingQueryingEnumerable.cs`
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoShapedQueryCompilingExpressionVisitor.cs` (compile both streaming + DOM shapers; choose per `StreamingEligibility`)
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoSelectExpression.cs` — feed `Lookups` from the **kept** `MongoQueryExpression.GetStreamingReferenceLookups()` / `InnerCollections` reconstruction (`Lookup.cs`)
- Reference: spike `DispatchingQueryingEnumerable` + spike `MongoShapedQueryCompilingExpressionVisitor.CompileShapedQuery`; current `MongoQueryExpression.Lookup.cs` (`GetPendingLookups`/`UsesDriverJoinFields`/`GetFieldPath`)
- Test: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeIncludeTests.cs`

**Interfaces:**
- Produces: `DispatchingQueryingEnumerable<TResult>` dispatching streaming vs DOM; the `Lookups` slot populated for single-level reference Includes.
- Consumes: `StreamingEligibility` (Task 12), the kept lookup reconstruction.

**Note (deferred clean-up):** At the foundation, `Lookups` is fed by the existing `GetStreamingReferenceLookups()` reconstruction (kept alongside the driver-LINQ fallback). Structural `Lookups` population — and deleting `GetStreamingReferenceLookups()`/`UsesDriverJoinFields`/inner-collection tracking — is the **Collection Includes sub-project's** job, not this one. Hold the spike's single-level-reference-Include acceptance set exactly; add zero new Include work.

- [ ] **Step 1: Write the failing test** — `.Include(o => o.Customer)` (reference nav) executes natively via `$lookup`+`$unwind`; assert the MQL and the materialized graph. A collection Include must fall back (not native).
- [ ] **Step 2: Run; verify fail.**
- [ ] **Step 3: Implement** the dual-shaper + wire `Lookups` from the kept reconstruction; lowerer (Task 8) consumes it.
- [ ] **Step 4: Run; verify pass.**
- [ ] **Step 5: Commit.**
```bash
git add src/MongoDB.EntityFrameworkCore/Query/ tests/
git commit -m "EF-323: Add DOM/streaming dual-shaper + single-level reference Include lowering"
```

---

## Task 14: Compile-time gate

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoShapedQueryCompilingExpressionVisitor.cs` (`VisitShapedQuery` `:131-153`; `TranslateQuery` `:277-314`)
- Test: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/QueryModeGateTests.cs`

**Interfaces:**
- Consumes: `MongoQueryCompilationContext.QueryMode` (Task 2), `MongoSelectExpression.IsNativeRepresentable` (Task 4/6), `MongoSelectLowerer`+renderer+factory (Tasks 8-10), `CapturedExpression` (driver fallback).

**Gate semantics (design § *Pipeline selection*):**
- `Native`: if `IsNativeRepresentable` **and** lowering/rendering succeed ⇒ compile the native path (pipeline factory + streaming/DOM shaper); else compile the driver-LINQ path from `CapturedExpression`.
- `NativeOnly`: a non-representable query (or a lowering/rendering failure) **throws at compile time** (`NativeTranslationNotSupportedException`) — the coverage instrument.
- `DriverLinq`: never attempt native.

**Note:** This replaces the spike's per-execution try/catch in `TranslateQuery` with a deterministic compile-time decision. Preserve the `streaming`/`DispatchingQueryingEnumerable` discipline unchanged.

- [ ] **Step 1: Write the failing tests**
```csharp
public class QueryModeGateTests
{
    [Fact] public void Native_mode_uses_native_for_representable_query() { /* AssertMql shows raw pipeline */ }
    [Fact] public void Native_mode_falls_back_for_unrepresentable_query() { /* projecting Select -> driver pipeline */ }
    [Fact] public void NativeOnly_mode_throws_on_unrepresentable_query()
    { /* Assert.Throws on a projecting Select under NativeOnly */ }
    [Fact] public void DriverLinq_mode_never_goes_native() { /* even Where uses driver pipeline */ }
}
```
- [ ] **Step 2: Run; verify fail.**
- [ ] **Step 3: Implement** the gate at the compile-time entry; wrap lowering/rendering in a try that catches `NativeTranslationNotSupportedException` and falls back (or rethrows under `NativeOnly`).
- [ ] **Step 4: Run; verify pass.**
- [ ] **Step 5: Commit.**
```bash
git add src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoShapedQueryCompilingExpressionVisitor.cs tests/
git commit -m "EF-323: Compile-time native-vs-driver gate honoring MongoQueryMode"
```

---

## Task 15: Full-suite validation (zero regressions, no coverage shrink)

**Files:** no product code unless a regression is found.

- [ ] **Step 1: Run the full functional + specification suites on EF10, `Native` mode (default)**

Run: `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10"`
Expected: green (fallback covers the non-parity remainder). Triage any failure as a regression and fix in the owning task's area.

- [ ] **Step 2: Run the full suites on EF8**

Run: `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF8"`
Expected: green. (Use the `/test-all` skill to cover EF8/EF9/EF10 in parallel.)

- [ ] **Step 3: Confirm native-only-mode coverage does not shrink vs the spike**

Run the suites under `NativeOnly` (set the test context's `UseQueryMode(MongoQueryMode.NativeOnly)` via the test fixture's options hook) and confirm the set of green tests ≥ the spike's recorded acceptance set (~64% spec / ~82% functional Query). Record the measured percentages in the commit message.
Expected: no shrink.

- [ ] **Step 4: Commit any regression fixes**
```bash
git commit -am "EF-323: Fix regressions surfaced by full-suite validation"
```

---

## Task 16: Remove superseded re-walkers

**Files:**
- Delete: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoPipelineTranslator.cs`, `MongoPredicateTranslator.cs` (only if they were reproduced as transitional scaffolding; if never reproduced, skip — they don't exist on `main`).
- Keep (until the Includes sub-project): `MongoEFToLinqTranslatingExpressionVisitor` (+ `.LeftJoin.cs`) and `MongoQueryExpression.Lookup.cs`'s reconstruction — still the fallback + `Lookups` feeder.

- [ ] **Step 1: Confirm nothing references the re-walkers**

Run: `git grep -n "MongoPipelineTranslator\|MongoPredicateTranslator" src/`
Expected: no references outside the files being deleted (the lowerer + renderer + `MongoExpressionTranslator` replaced them).

- [ ] **Step 2: Delete + build**

Run: `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"` → Build succeeded.

- [ ] **Step 3: Re-run the full EF10 suite**

Run: `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10"` → green.

- [ ] **Step 4: Commit.**
```bash
git commit -am "EF-323: Remove superseded per-execution chain re-walkers"
```

---

## Task 17: Benchmark — add native config + re-run headline (EF-324 harness)

**Files:**
- Modify: `benchmarks/MongoDB.EntityFrameworkCore.Benchmarks/HeadlineBenchmarks.cs` (+ `BenchmarkConfig.cs` / `Program.cs` as needed) — add the **native** config alongside EF-324's DriverOnly + EF-current.
- Modify: `benchmarks/MongoDB.EntityFrameworkCore.Benchmarks/results/perf-baseline.md` — append the native numbers.
- Reference: spike benchmark harness (three-config) on `spike/low-level-provider`; the EF-324 plan `docs/superpowers/plans/2026-06-23-benchmark-harness-baselines.md`.

**Note:** Requires a MongoDB replica set via `MONGODB_URI`. EF10-only harness; quoted config `"Release EF10"`; InProcess toolchain.

- [ ] **Step 1: Smoke the harness**

Run: from `benchmarks/MongoDB.EntityFrameworkCore.Benchmarks/`, `dotnet run -c "Release EF10" -- --smoke`
Expected: correctness gate passes.

- [ ] **Step 2: Add the native config and re-run the headline set**

Run: `dotnet run -c "Release EF10"`
Expected: native, DriverOnly, EF-current rows for each headline shape (Where→ToList; whole-entity tracked/no-track; reference Include; OrderByTake).

- [ ] **Step 3: Assert the bar (native alloc/time ≥ spike's; ≥ EF-current)**

Compare native rows against the spike's recorded headline and the EF-current baseline already in `perf-baseline.md`. Native alloc/time must be **≥** (no worse than) the spike's; the B2 translation-off-hot-path win is expected upside, not required.

- [ ] **Step 4: Append numbers + commit**
```bash
git add benchmarks/
git commit -m "EF-323: Add native benchmark config + record headline numbers"
```

---

## Task 18: Carried-over cleanup — update Query/AGENTS.md

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md`

**Note:** The current AGENTS.md states the provider "sits on top of the driver's LINQ v3 provider … does not generate aggregation BSON itself." That is no longer true for the native path. Update the *Scope*, *Pipeline at a glance*, and *Key entry points* sections to describe the rebuilt architecture (QMTEV slot population → `MongoSelectExpression` → `MongoSelectLowerer` → stage IR → `MongoQueryLanguageRenderer` → `MongoPipelineFactory` → native `Aggregate`), with driver-LINQ as the gated fallback. Note the `MongoQueryMode` gate. Preserve the BOM if present.

- [ ] **Step 1: Update the doc** to reflect the native path + the retained driver-LINQ fallback boundary.
- [ ] **Step 2: Commit.**
```bash
git add src/MongoDB.EntityFrameworkCore/Query/AGENTS.md
git commit -m "EF-323: Update Query/AGENTS.md for the native translation path"
```

---

## Self-review checklist (run before handing off for execution)

- **Spec coverage:** every component in the design doc's *Architecture* / *New types* has a task — nodes (T3), select tree (T4), translator (T5), QMTEV population (T6), stage IR (T7), lowerer (T8), renderer + `$expr` seam (T9), factory (T10), execution (T11), streaming (T12), DOM/dual-shaper + Lookups (T13), gate (T14), config option (T2). Validation (T15), cleanup (T16/T18), benchmark (T17). ✔
- **Out-of-scope guard:** parity-only enforced via `IsNativeRepresentable = false` in T6 and the gate in T14; projecting Select, predicate breadth, collection Includes, non-canonical paging all fall back. ✔
- **Type consistency:** `IsNativeRepresentable`, `AddPredicateConjunct`, `MongoSelectLowerer.Lower`, `MongoPipelineFactory.Build`, `PlaceholderTable`, `MongoQueryMode` used consistently across tasks. ✔
- **Multi-EF:** EF8 build/test gates in T6 and T15; the parameter-access `#if` guard is a global constraint. ✔
- **Benchmark dependency:** T17 runs on the EF-324-based branch; rebase to `main` once EF-324 merges (Global Constraints). ✔
