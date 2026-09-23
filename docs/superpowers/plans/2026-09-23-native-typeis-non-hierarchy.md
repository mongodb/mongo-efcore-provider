# Native `is`-operator (TypeBinaryExpression) Translation for Non-Hierarchy Entities — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give `MongoExpressionTranslator` a native translation for the C# `is` operator (`TypeBinaryExpression` / `ExpressionType.TypeIs`) against a non-hierarchy root entity type, so `Where_is_conditional` and `Where_Is_on_same_type` go native instead of falling back to driver-LINQ.

**Architecture:** `c is Customer` against a root entity type with no TPH hierarchy (no base type, no derived types) is compile-time-decidable: the query root can only ever be exactly `Customer`, so the comparison collapses to a constant `true`/`false`, exactly like the existing `root.GetType() == typeof(T)` native translator (`MongoExpressionTranslator.EntityType.cs`, added in commit `30ae1e7`). This plan adds a sibling partial-class file, `MongoExpressionTranslator.TypeIs.cs`, following that file's exact shape, and wires one new case into `MongoExpressionTranslator.TranslateNode`'s dispatch switch. A hierarchy-scoped `is` check (real discriminator predicate) stays out of scope and continues to decline to driver-LINQ, matching `GetType()`'s own scoping.

**Tech Stack:** C# / .NET, EF Core provider internals (`MongoExpressionTranslator`, `MongoExpression` IR, `MongoQueryLanguageRenderer`), xUnit spec tests (`AssertMql`), this repo's `EF_TEST_REWRITE_BASELINES` baseline-regeneration workflow.

**Spec:** No separate design doc — this is a small, single-precedent-following change; this plan doc is self-contained (see "Background" below for what would otherwise be a spec).

## Background (would-be spec)

Current state (as investigated on branch `EF-322-Native-LINQ-rebased`):

- `Where_Is_on_same_type` (`ss.Set<Customer>().Where(c => c is Customer)`, `Customer` has no base/derived types) and `Where_is_conditional` (`ss.Set<Product>().Where(p => p is Product ? false : true)`, `Product` has no base/derived types) both currently pass in `tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/NorthwindWhereQueryMongoTest.cs` (lines 1280 and 1378 respectively) — but only via the driver-LINQ fallback, because `MongoExpressionTranslator.TranslateNode` (`src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.cs`) has no case for `TypeBinaryExpression`/`ExpressionType.TypeIs` anywhere; a bare `TypeBinaryExpression` falls to the `default:` arm (~line 1275), which only tries `TryResolveMember` (a `TypeBinaryExpression` is never a member access) and declines.
- `Where_Is_on_same_type`'s predicate body IS a bare `TypeBinaryExpression`. `Where_is_conditional`'s predicate body is `ConditionalExpression{Test: TypeBinaryExpression, IfTrue: false, IfFalse: true}` — the ternary-as-predicate case (`MongoExpressionTranslator.cs` ~line 948, added for `Where_ternary_boolean_condition_negated`) is already native-capable, but it calls `TryTranslate` on `conditional.Test`, which declines because of the missing `TypeBinaryExpression` case — so the whole ternary currently declines too.
- `TypeBinary_short_circuit` (adjacent test, line 1358) is NOT a counterexample: its predicate is `o => customer is Order` where `customer` is a captured local, not the query parameter. EF Core's own parameter-extraction pass folds that whole non-source-referencing subtree to a literal `ConstantExpression{bool}` before this translator ever runs, so it never reaches a `TypeBinaryExpression` node at all — it hits the existing "literal boolean predicate root" case (~line 1257). This plan's change cannot regress it because the new case is never reached for that shape.
- The exact precedent to mirror is `MongoExpressionTranslator.EntityType.cs` (`TryTranslateGetTypeComparison`), wired in at `MongoExpressionTranslator.cs` lines 759–763. It: (1) requires `SelfParam is not null` (the Where lambda's own parameter must be set — set by `NativeSlotPopulator.PopulateNativeSlots` before translation runs); (2) requires the type-checked operand to be reference-identical to `SelfParam` after `Unwrap`; (3) declines for a hierarchy type (`_entityType.BaseType is not null || _entityType.GetDirectlyDerivedTypes().Any()`); (4) otherwise emits `new MongoConstantExpression(<bool>, forSerialization: null)`.
- `MongoQueryLanguageRenderer.RenderNode` already has a dedicated arm for a root `MongoConstantExpression{Value: bool}` (`MongoQueryLanguageRenderer.cs` lines 94–100): `true` → `new BsonDocument()` (empty filter, matches every doc); `false` → `new BsonDocument("_id", new BsonDocument("$type", -1))` (the "impossible BSON type" always-false idiom, also used by `MongoPipelineFactory`'s own `$limit:0` rewrite). No renderer change is needed — this arm already exists and already produces the same idiom the driver-LINQ fallback happens to also produce for `Where_is_conditional`'s current baseline.
- Confirmed precedent baselines already on this branch for the identical constant-fold shape (`GetType_on_non_hierarchy1..4`, `NorthwindWhereQueryMongoTest.cs` lines 1680–1718): `true` → `Customers.{ "$match" : { } }`; `false` → `Customers.{ "$match" : { "_id" : { "$type" : -1 } } }`.
- **What is NOT yet known and must be confirmed empirically, not hand-guessed:** the exact final `AssertMql` baseline string for `Where_is_conditional` once its `TypeBinaryExpression` test node goes native. The ternary (`ConditionalExpression`) wrapping it has no query-dialect form of its own and always renders through the `$expr` aggregation-expression catch-all (`MongoConditionalExpression`'s own remarks), so the resulting MQL shape is a `$cond`-based `$expr`, structurally different from `Where_Is_on_same_type`'s bare-constant shape and from the current fallback's `{"_id":{"$type":-1}}` sentinel — even though both happen to be always-false. This plan's Task 2 regenerates it via `EF_TEST_REWRITE_BASELINES=1` rather than guessing the string, per this repo's documented baseline-regeneration workflow (`tests/MongoDB.EntityFrameworkCore.SpecificationTests/AGENTS.md`).

## Global Constraints

- Preserve file BOMs on any file touched (per root `AGENTS.md`).
- `src/` is nullable-enabled — the new file must annotate accordingly (mirror `MongoExpressionTranslator.EntityType.cs`'s existing nullable usage exactly).
- Do not hand-write MQL baseline strings for anything beyond the simplest, already-precedented shape (`Where_Is_on_same_type`'s `true` case) — regenerate via `EF_TEST_REWRITE_BASELINES=1` and diff, per `tests/MongoDB.EntityFrameworkCore.SpecificationTests/AGENTS.md`.
- Build/test against EF10 configuration during development (`-c "Debug EF10"`); run `/test-all` (or equivalent three-EF-version pass) before considering the change complete, since `Query/AGENTS.md`'s multi-EF guard concerns apply to this file tree.
- First commit message starts with the JIRA number for this epic: `EF-322: <description>` (per root `AGENTS.md` commit convention and this epic's existing commit history, e.g. `30ae1e7 EF-322: native GetType() comparison against a non-hierarchy entity`).

---

## Task 1: Add `TryTranslateTypeIs` and wire it into `TranslateNode`

**Files:**
- Create: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.TypeIs.cs`
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.cs:759-763` (insert a new case immediately after the existing `getTypeEq` case)
- Modify (test, baseline update): `tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/NorthwindWhereQueryMongoTest.cs:1280-1288` (`Where_Is_on_same_type`)

**Interfaces:**
- Consumes: `MongoExpressionTranslator`'s existing private state `SelfParam` (`ParameterExpression?`, set by the caller before `TryTranslate` runs), `_entityType` (`IEntityType`, the translator's constructor-injected root entity type), `Unwrap(Expression)` (existing private helper that strips conversions), `MongoConstantExpression` (existing IR node type, constructor `MongoConstantExpression(object? value, IProperty? forSerialization)`).
- Produces: a new private instance method `bool TryTranslateTypeIs(TypeBinaryExpression typeBinary, out MongoExpression? result)`, called from `TranslateNode`'s switch. Nothing outside this task depends on this method's name — it is only referenced from the one new switch case added in this same task.

- [ ] **Step 1: Write the failing test**

  Change the existing `Where_Is_on_same_type` override in `tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/NorthwindWhereQueryMongoTest.cs` (currently lines 1280–1288) from:

  ```csharp
  public override async Task Where_Is_on_same_type(bool async)
  {
      await base.Where_Is_on_same_type(async);

      AssertMql(
          """
          Customers.
          """);
  }
  ```

  to the predicted native baseline (mirroring `GetType_on_non_hierarchy1`'s `true`-case baseline exactly):

  ```csharp
  public override async Task Where_Is_on_same_type(bool async)
  {
      await base.Where_Is_on_same_type(async);

      AssertMql(
          """
          Customers.{ "$match" : { } }
          """);
  }
  ```

- [ ] **Step 2: Run test to verify it fails**

  ```bash
  dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"
  dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
    -c "Debug EF10" --no-build --filter "FullyQualifiedName~NorthwindWhereQueryMongoTest.Where_Is_on_same_type"
  ```

  Expected: FAIL — the actual captured MQL is still `Customers.` (no `$match` stage), because the translator has not been changed yet; the assertion mismatch is the "failing test" for this task.

- [ ] **Step 3: Write the minimal implementation**

  Create `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.TypeIs.cs`:

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

  using System.Linq;
  using System.Linq.Expressions;

  namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

  /// <summary>
  /// <see cref="MongoExpressionTranslator"/> — <c>root is T</c> (<see cref="TypeBinaryExpression"/>),
  /// scoped to a NON-hierarchy root entity type.
  /// </summary>
  /// <remarks>
  /// Sibling of <c>MongoExpressionTranslator.EntityType.cs</c>'s <c>root.GetType() == typeof(T)</c> handling —
  /// same reasoning, different C# syntax. Without this, every <c>c is T</c> predicate over the query root fell
  /// back to driver-LINQ.
  /// <para>
  /// <b>Scope, deliberately narrow.</b> Only a root entity type with NO hierarchy (no base type and no directly
  /// derived types) is handled here — for such a type, the query root can only ever be exactly that CLR type,
  /// so <c>root is T</c> collapses to a constant <see langword="true"/>/<see langword="false"/>, needing no
  /// discriminator field at all. A TPH hierarchy needs a genuine discriminator-value predicate (mirroring
  /// <c>TryBuildDiscriminatorPredicate</c>, used for <c>OfType&lt;T&gt;</c>) and is out of scope here — it
  /// declines and keeps falling back.
  /// </para>
  /// </remarks>
  internal sealed partial class MongoExpressionTranslator
  {
      private bool TryTranslateTypeIs(TypeBinaryExpression typeBinary, out MongoExpression? result)
      {
          result = null;

          if (SelfParam is null)
              return false;

          if (!ReferenceEquals(Unwrap(typeBinary.Expression), SelfParam))
              return false;

          // Hierarchy types need a real discriminator predicate, not a compile-time constant — decline and let
          // this keep falling back to driver-LINQ (see the type's own remarks).
          if (_entityType.BaseType is not null || _entityType.GetDirectlyDerivedTypes().Any())
              return false;

          var matches = typeBinary.TypeOperand.IsAssignableFrom(_entityType.ClrType);
          result = new MongoConstantExpression(matches, forSerialization: null);
          return true;
      }
  }
  ```

  In `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.cs`, insert a new case immediately after the existing `getTypeEq` case (currently lines 759–763), so the block reads:

  ```csharp
              // `root.GetType() == typeof(T)` / `!=` against a non-hierarchy root entity — collapses to a
              // compile-time constant true/false. See MongoExpressionTranslator.EntityType.cs.
              case BinaryExpression { NodeType: ExpressionType.Equal or ExpressionType.NotEqual } getTypeEq
                  when TryTranslateGetTypeComparison(getTypeEq, out var getTypeComparison):
                  return getTypeComparison;

              // `root is T` against a non-hierarchy root entity — collapses to a compile-time constant
              // true/false, same reasoning as the GetType() case just above. See
              // MongoExpressionTranslator.TypeIs.cs.
              case TypeBinaryExpression { NodeType: ExpressionType.TypeIs } typeIs
                  when TryTranslateTypeIs(typeIs, out var typeIsResult):
                  return typeIsResult;
  ```

- [ ] **Step 4: Run test to verify it passes**

  ```bash
  dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"
  dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
    -c "Debug EF10" --no-build --filter "FullyQualifiedName~NorthwindWhereQueryMongoTest.Where_Is_on_same_type"
  ```

  Expected: PASS.

- [ ] **Step 5: Run the adjacent regression check**

  Confirm `TypeBinary_short_circuit` (the constant-folded-local-variable shape) is unaffected, since it must never reach the new case:

  ```bash
  dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
    -c "Debug EF10" --no-build --filter "FullyQualifiedName~NorthwindWhereQueryMongoTest.TypeBinary_short_circuit"
  ```

  Expected: PASS, with baseline unchanged (`Orders.{ "$match" : { "$expr" : false } }`).

- [ ] **Step 6: Commit**

  ```bash
  git add src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.TypeIs.cs \
          src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.cs \
          tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/NorthwindWhereQueryMongoTest.cs
  git commit -m "EF-322: native 'is' comparison against a non-hierarchy entity"
  ```

## Task 2: Regenerate and confirm the `Where_is_conditional` baseline

**Files:**
- Modify (test, baseline update only — no source change): `tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/NorthwindWhereQueryMongoTest.cs:1378-1386` (`Where_is_conditional`)

**Interfaces:**
- Consumes: Task 1's `TryTranslateTypeIs` (already wired into `TranslateNode`; no further production code changes in this task) and the pre-existing `ConditionalExpression{Type: bool}` ternary-as-predicate case (`MongoExpressionTranslator.cs` ~line 948), which now succeeds on its `Test` because of Task 1.
- Produces: nothing consumed by later tasks — this is the last task in this plan.

- [ ] **Step 1: Confirm the test currently fails after Task 1's change (this task's "failing test")**

  Task 1's translator change already altered `Where_is_conditional`'s behavior (its ternary's `Test` now translates natively instead of declining), so its existing baseline is now stale without any edit needed to provoke a failure:

  ```bash
  dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"
  dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
    -c "Debug EF10" --no-build --filter "FullyQualifiedName~NorthwindWhereQueryMongoTest.Where_is_conditional"
  ```

  Expected: FAIL — the data assertion (`await base.Where_is_conditional(async)`) still passes (both the old fallback and the new native translation are semantically always-false, so `assertEmpty: true` is satisfied either way), but the `AssertMql` baseline comparison fails because the actual MQL is no longer `Products.{ "$match" : { "_id" : { "$type" : -1 } } }`.

- [ ] **Step 2: Regenerate the baseline**

  ```bash
  EF_TEST_REWRITE_BASELINES=1 dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
    -c "Debug EF10" --no-build --filter "FullyQualifiedName~NorthwindWhereQueryMongoTest.Where_is_conditional"
  ```

  This still reports the test as FAILED (that is the documented signal a rewrite happened — see `tests/MongoDB.EntityFrameworkCore.SpecificationTests/AGENTS.md`). Inspect the rewritten `AssertMql(...)` body in `NorthwindWhereQueryMongoTest.cs` via `git diff` before trusting it: confirm it is a `$match`/`$expr` shape consistent with a `$cond`-rendered always-false ternary (per this plan's Background section), and that the collection name is still `Products`.

- [ ] **Step 3: Rebuild and rerun without the rewrite flag to confirm green**

  ```bash
  dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"
  dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
    -c "Debug EF10" --no-build --filter "FullyQualifiedName~NorthwindWhereQueryMongoTest.Where_is_conditional"
  ```

  Expected: PASS.

- [ ] **Step 4: Run the full Northwind Where suite to catch any other collateral baseline drift**

  ```bash
  dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
    -c "Debug EF10" --no-build --filter "FullyQualifiedName~NorthwindWhere"
  ```

  Expected: PASS for all. If anything else regresses, do NOT blanket-rewrite the whole suite — investigate that specific test individually (per the "scope the run" caveat in `tests/MongoDB.EntityFrameworkCore.SpecificationTests/AGENTS.md`).

- [ ] **Step 5: Full three-EF-version pass**

  Invoke the `/test-all` skill (or run the EF8/EF9 equivalents of the commands above manually) to confirm no version-specific regression, per `Query/AGENTS.md`'s multi-EF guard concerns.

- [ ] **Step 6: Commit**

  ```bash
  git add tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/NorthwindWhereQueryMongoTest.cs
  git commit -m "EF-322: regenerate Where_is_conditional baseline for native 'is' translation"
  ```

## Self-Review

- **Spec coverage:** Both target tests (`Where_Is_on_same_type` in Task 1, `Where_is_conditional` in Task 2) are covered. The adjacent non-regression case (`TypeBinary_short_circuit`) is explicitly checked in Task 1 Step 5. The out-of-scope hierarchy case is explicitly declined by the guard clause and documented as intentionally out of scope (matching the `GetType()` precedent) — no task needed for it.
- **Placeholder scan:** No TBD/TODO; every code step has full, concrete code; the one intentionally-unknown value (the exact `Where_is_conditional` MQL string) is not placeholder-guessed — Task 2 has explicit steps to derive it via the repo's own regeneration tool rather than leaving it blank.
- **Type consistency:** `TryTranslateTypeIs(TypeBinaryExpression typeBinary, out MongoExpression? result)` matches the call-site pattern used for `TryTranslateGetTypeComparison` in the `TranslateNode` switch (same `when` clause shape, same `out MongoExpression?` signature, same `MongoConstantExpression(bool, forSerialization: null)` construction).
