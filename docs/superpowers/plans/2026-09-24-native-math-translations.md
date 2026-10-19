# Native Math/MathF Translation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or
> superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for
> tracking.

**Goal:** Give `MathTranslationsTestBase`'s 64 EF10 conformance tests (`Math.*`/`MathF.*`/
`double.RadiansToDegrees`/`float.DegreesToRadians`) native MongoDB aggregation-pipeline translation, and stand
up the shared `BasicTypesEntity`-based test fixture every other future EF10 `Query.Translations` phase will
also depend on.

**Architecture:** One new dialect-agnostic IR node, `MongoMathExpression{Function, Operands, Type}` (mirrors
`MongoRegexExpression{Kind, ...}` — one node type, an enum discriminates the ~30 concrete functions, not 30
sealed types), recognized in `MongoExpressionTranslator.TranslateOperand` (the general numeric/computed-operand
dispatcher `TryTranslateDateAdd`/`TryMatchIndexOfMethod` already live in — Math functions are operands inside a
larger comparison/projection, never boolean predicate roots themselves) and rendered exclusively through the
aggregation-expression ($expr) dialect via a method-name→MQL-operator lookup table, since MongoDB has a
near-1:1 native operator for almost every case.

**Tech Stack:** C# / .NET, EF Core provider internals (`MongoExpressionTranslator`, `MongoExpression` IR,
`MongoAggregationExpressionRenderer`), xUnit spec tests (`AssertQuery`/`AssertQueryScalar`/`AssertMql`), this
repo's `EF_TEST_REWRITE_BASELINES` baseline-regeneration workflow.

**Spec:** `docs/superpowers/specs/2026-09-24-native-math-translations-design.md`

## Global Constraints

- Branch: `EF-322-Native-LINQ-rebased`. Every commit message is JIRA-numbered: `EF-322: <description>`.
- This entire feature is **EF10-only** — `MathTranslationsTestBase<>` and `BasicTypesQueryFixtureBase` don't
  exist in the EF8/EF9-pinned `Microsoft.EntityFrameworkCore.Specification.Tests` package versions at all.
  Both new test files (`MongoBasicTypesQueryFixture.cs`, `MathTranslationsMongoTest.cs`) must have their ENTIRE
  contents wrapped in `#if !EF8 && !EF9` / `#endif` (whole-file guard, immediately after the license header,
  mirroring `NorthwindFunctionsQueryMongoTest.cs`'s `#if EF8 || EF9` guard for the symmetric case) — without
  this, EF8/EF9 builds fail to resolve the types.
- `<Nullable>enable</Nullable>` applies to all new `src/` code — annotate accordingly.
- **Never hand-write an `AssertMql` baseline body.** Every baseline in this plan starts as `AssertMql();`
  (empty — the correct, intentional RED state this repo's own workflow uses, not a placeholder) and gets
  filled in only via `EF_TEST_REWRITE_BASELINES=1`, confirmed by `git diff` before trusting it, per
  `tests/MongoDB.EntityFrameworkCore.SpecificationTests/AGENTS.md`. Because this whole file is EF10-only (no
  `#if` region), the rewriter's known bug (silently no-ops inside an `#if EF8||EF9` block, discovered during
  the prior `EF.Functions.Like` phase) does not apply here — the in-place rewrite is expected to work cleanly.
- **A new `MongoExpression` subtype must be wired into all seven dispatchers**, per `Query/AGENTS.md`'s durable
  invariants and enforced by `MongoExpressionNodeCoverageTests`: `MongoQueryLanguageRenderer.Render` (no code
  change needed — its default arm already delegates to `$expr` safely), `MongoQueryLanguageRenderer
  .IsQueryDialectRenderable` (needs `=> false`), `MongoAggregationExpressionRenderer.Render` (needs a case),
  `MongoAggregationExpressionRenderer.CanRender` (needs a case), `MongoExpressionNegator.TryNegate` (no code
  change needed — default arm returns `null`/declines safely), `MongoFieldPrefixRewriter`'s private `Rewrite`
  (needs a case), and `MongoExpressionTranslator.AllFieldsDefaultSerialized` (needs a case — **this one is not
  optional**: its default arm is `_ => true`, permissive, so without an explicit recursive case a
  value-converted/non-default-represented field operand would be silently treated as safe to run raw `$abs`/
  etc. against, which is exactly the wrong-data bug class this method exists to prevent). Task 2 covers all
  five real code changes in one commit; Tasks 3-8 add no new dispatcher touch-points, only new enum members
  and lookup-table entries flowing through the machinery Task 2 already wired.
- After every task, `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build` is NOT what to run —
  build fresh (`dotnet build ... -c "Debug EF10"`, no `--no-build`) since every task touches `src/`.
- Test project reference for `--filter`: `MongoDB.EntityFrameworkCore.SpecificationTests.csproj`.

## Review Focus

- **A value-converted numeric property used inside `Math.Abs`/etc.** — must decline to fallback (`$abs` on a
  provider value that isn't the raw model value would silently be wrong), not run natively and answer wrong.
  Task 2's `AllFieldsDefaultSerialized` case is what prevents this; Task 2's own test suite doesn't have a
  value-converted `BasicTypesEntity` property to exercise it against, so this is flagged here rather than
  silently assumed — see Task 2 Step 7 for the targeted unit test that pins it directly.
- **`Math.Round(x, digits)`'s rounding mode vs MongoDB's `$round`** — .NET's `Math.Round` defaults to
  banker's rounding (`MidpointRounding.ToEven`); MongoDB's `$round` also rounds half-to-even by default — but
  this must be CONFIRMED against real data in Task 4, not assumed, per the spec's own flagged risk.
  `Round_with_digits_decimal/_double/_float` are exactly the tests that will surface a divergence if one
  exists.
- **`Math.Sign` on a value exactly equal to zero** — the upstream test bodies only assert `> 0`, so a
  sign-of-zero bug could hide; Task 5 adds an explicit unit-level check that `MongoMathExpression{Sign}`
  renders a `$switch` with an explicit `default: 0` branch (not just two `$cond` cases that could fall through
  wrong), not merely relying on the spec test's own coverage.
- **Nested `Math.Max(Math.Max(a, b), c)`** — the design claims nesting "falls out for free" from the IR being
  recursively composable; Task 6 must actually run `Max_nested`/`Max_nested_twice`/`Min_nested`/
  `Min_nested_twice` (not skip them as "obviously fine") since a subtle renderer bug (e.g. not recursing into
  `Operands` correctly) would only surface with real nesting, not the flat `Max`/`Min` cases.
- **`Truncate_project_and_order_by_it_twice{,2,3}`** — projecting a `MongoMathExpression` into an anonymous
  type and then `OrderBy`/`OrderByDescending`/`ThenBy` on that projected member is a DIFFERENT code path
  (`NativeProjectionBinder`'s computed-leaf-projection list, then re-resolving that projected alias as a sort
  key) from a `Where` predicate using the same function — Task 7 exercises this explicitly rather than assuming
  Task 2's projection-binder wiring alone covers it.

---

## Task 1: Stand up `MongoBasicTypesQueryFixture` and the full (native-support-free) `MathTranslationsMongoTest` stub

**Files:**
- Create: `tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/Translations/MongoBasicTypesQueryFixture.cs`
- Create: `tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/Translations/MathTranslationsMongoTest.cs`

**Interfaces:**
- Produces: `MongoBasicTypesQueryFixture` (public, no generic parameters — `BasicTypesQueryFixtureBase` isn't
  generic over a model customizer, unlike `NorthwindQueryFixtureBase<TModelCustomizer>`), consumed by every
  later task's test class declaration `MathTranslationsMongoTest : MathTranslationsTestBase<MongoBasicTypesQueryFixture>`.
- Consumes: nothing from earlier tasks (this is Task 1).

This task's "test" IS the deliverable for this task — there is no separate native-code RED/GREEN cycle yet.
The RED state is `Check_all_tests_overridden` passing but every individual Math test failing on its `AssertMql`
assertion (expected empty, actual real MQL from the driver-LINQ fallback) — proving the fixture, seeding, and
BSON round-trip of every scalar type on `BasicTypesEntity` all work, with zero native Math support yet.

- [ ] **Step 1: Create the fixture**

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

#if !EF8 && !EF9

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.Translations;
using Microsoft.EntityFrameworkCore.TestModels.BasicTypesModel;
using Microsoft.EntityFrameworkCore.TestUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;

namespace MongoDB.EntityFrameworkCore.SpecificationTests.Query.Translations;

public class MongoBasicTypesQueryFixture : BasicTypesQueryFixtureBase
{
    protected override string StoreName { get; } = TestDatabaseNamer.GetUniqueDatabaseName("BasicTypes");

    private ITestStoreFactory? _testStoreFactory;

    protected override ITestStoreFactory TestStoreFactory
        => _testStoreFactory!;

    public TestServer TestServer { get; private set; } = null!;

    public override async Task InitializeAsync()
    {
        TestServer = await TestServer.GetOrInitializeTestServerAsync(MongoCondition.None);
        _testStoreFactory = new MongoTestStoreFactory(TestServer);

        await base.InitializeAsync();
    }

    protected override bool UsePooling
        => false;

    public TestMqlLoggerFactory TestMqlLoggerFactory
        => (TestMqlLoggerFactory)ServiceProvider.GetRequiredService<ILoggerFactory>();

    protected override bool ShouldLogCategory(string logCategory)
        => logCategory == DbLoggerCategory.Query.Name;

    protected override void OnModelCreating(ModelBuilder modelBuilder, DbContext context)
    {
        base.OnModelCreating(modelBuilder, context);

        modelBuilder.Entity<BasicTypesEntity>().ToCollection("BasicTypesEntities");
        modelBuilder.Entity<NullableBasicTypesEntity>().ToCollection("NullableBasicTypesEntities");
    }
}

#endif
```

- [ ] **Step 2: Create the full test-class stub with all 64 overrides**

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

#if !EF8 && !EF9

using Microsoft.EntityFrameworkCore.Query.Translations;
using Microsoft.EntityFrameworkCore.TestUtilities;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using Xunit.Abstractions;

namespace MongoDB.EntityFrameworkCore.SpecificationTests.Query.Translations;

public class MathTranslationsMongoTest : MathTranslationsTestBase<MongoBasicTypesQueryFixture>
{
    public MathTranslationsMongoTest(MongoBasicTypesQueryFixture fixture, ITestOutputHelper testOutputHelper)
        : base(fixture)
    {
        Fixture.TestMqlLoggerFactory.Clear();
        //Fixture.TestMqlLoggerFactory.SetTestOutputHelper(testOutputHelper);
    }

    [ConditionalFact]
    public virtual void Check_all_tests_overridden()
        => TestHelpers.AssertAllMethodsOverridden(GetType());

    public override async Task Abs_decimal() { await base.Abs_decimal(); AssertMql(); }
    public override async Task Abs_int() { await base.Abs_int(); AssertMql(); }
    public override async Task Abs_double() { await base.Abs_double(); AssertMql(); }
    public override async Task Abs_float() { await base.Abs_float(); AssertMql(); }
    public override async Task Ceiling() { await base.Ceiling(); AssertMql(); }
    public override async Task Ceiling_float() { await base.Ceiling_float(); AssertMql(); }
    public override async Task Floor_decimal() { await base.Floor_decimal(); AssertMql(); }
    public override async Task Floor_double() { await base.Floor_double(); AssertMql(); }
    public override async Task Floor_float() { await base.Floor_float(); AssertMql(); }
    public override async Task Exp() { await base.Exp(); AssertMql(); }
    public override async Task Exp_float() { await base.Exp_float(); AssertMql(); }
    public override async Task Power() { await base.Power(); AssertMql(); }
    public override async Task Power_float() { await base.Power_float(); AssertMql(); }
    public override async Task Round_decimal() { await base.Round_decimal(); AssertMql(); }
    public override async Task Round_double() { await base.Round_double(); AssertMql(); }
    public override async Task Round_float() { await base.Round_float(); AssertMql(); }
    public override async Task Round_with_digits_decimal() { await base.Round_with_digits_decimal(); AssertMql(); }
    public override async Task Round_with_digits_double() { await base.Round_with_digits_double(); AssertMql(); }
    public override async Task Round_with_digits_float() { await base.Round_with_digits_float(); AssertMql(); }
    public override async Task Truncate_decimal() { await base.Truncate_decimal(); AssertMql(); }
    public override async Task Truncate_double() { await base.Truncate_double(); AssertMql(); }
    public override async Task Truncate_float() { await base.Truncate_float(); AssertMql(); }
    public override async Task Truncate_project_and_order_by_it_twice() { await base.Truncate_project_and_order_by_it_twice(); AssertMql(); }
    public override async Task Truncate_project_and_order_by_it_twice2() { await base.Truncate_project_and_order_by_it_twice2(); AssertMql(); }
    public override async Task Truncate_project_and_order_by_it_twice3() { await base.Truncate_project_and_order_by_it_twice3(); AssertMql(); }
    public override async Task Log() { await base.Log(); AssertMql(); }
    public override async Task Log_float() { await base.Log_float(); AssertMql(); }
    public override async Task Log_with_newBase() { await base.Log_with_newBase(); AssertMql(); }
    public override async Task Log_with_newBase_float() { await base.Log_with_newBase_float(); AssertMql(); }
    public override async Task Log10() { await base.Log10(); AssertMql(); }
    public override async Task Log10_float() { await base.Log10_float(); AssertMql(); }
    public override async Task Log2() { await base.Log2(); AssertMql(); }
    public override async Task Sqrt() { await base.Sqrt(); AssertMql(); }
    public override async Task Sqrt_float() { await base.Sqrt_float(); AssertMql(); }
    public override async Task Sign() { await base.Sign(); AssertMql(); }
    public override async Task Sign_decimal() { await base.Sign_decimal(); AssertMql(); }
    public override async Task Sign_int() { await base.Sign_int(); AssertMql(); }
    public override async Task Sign_float() { await base.Sign_float(); AssertMql(); }
    public override async Task Max() { await base.Max(); AssertMql(); }
    public override async Task Max_nested() { await base.Max_nested(); AssertMql(); }
    public override async Task Max_nested_twice() { await base.Max_nested_twice(); AssertMql(); }
    public override async Task Min() { await base.Min(); AssertMql(); }
    public override async Task Min_nested() { await base.Min_nested(); AssertMql(); }
    public override async Task Min_nested_twice() { await base.Min_nested_twice(); AssertMql(); }
    public override async Task Degrees() { await base.Degrees(); AssertMql(); }
    public override async Task Degrees_float() { await base.Degrees_float(); AssertMql(); }
    public override async Task Radians() { await base.Radians(); AssertMql(); }
    public override async Task Radians_float() { await base.Radians_float(); AssertMql(); }
    public override async Task Acos() { await base.Acos(); AssertMql(); }
    public override async Task Acos_float() { await base.Acos_float(); AssertMql(); }
    public override async Task Acosh() { await base.Acosh(); AssertMql(); }
    public override async Task Asin() { await base.Asin(); AssertMql(); }
    public override async Task Asin_float() { await base.Asin_float(); AssertMql(); }
    public override async Task Asinh() { await base.Asinh(); AssertMql(); }
    public override async Task Atan() { await base.Atan(); AssertMql(); }
    public override async Task Atan_float() { await base.Atan_float(); AssertMql(); }
    public override async Task Atanh() { await base.Atanh(); AssertMql(); }
    public override async Task Atan2() { await base.Atan2(); AssertMql(); }
    public override async Task Atan2_float() { await base.Atan2_float(); AssertMql(); }
    public override async Task Cos() { await base.Cos(); AssertMql(); }
    public override async Task Cos_float() { await base.Cos_float(); AssertMql(); }
    public override async Task Cosh() { await base.Cosh(); AssertMql(); }
    public override async Task Sin() { await base.Sin(); AssertMql(); }
    public override async Task Sin_float() { await base.Sin_float(); AssertMql(); }
    public override async Task Sinh() { await base.Sinh(); AssertMql(); }
    public override async Task Tan() { await base.Tan(); AssertMql(); }
    public override async Task Tan_float() { await base.Tan_float(); AssertMql(); }
    public override async Task Tanh() { await base.Tanh(); AssertMql(); }

    private void AssertMql(params string[] expected)
        => Fixture.TestMqlLoggerFactory.AssertBaseline(expected);
}

#endif
```

- [ ] **Step 3: Build and run — confirm the fixture/seeding infra works, and see the expected RED state**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"
```

Expected: builds clean (0 errors). If it does NOT — e.g. a `BasicTypesEntity` property fails to map — STOP and
fix the model/serialization issue here, before any Math-specific work; that would be a `Ruling` about this
task, not a later task's problem.

```bash
unset MONGODB_URI ATLAS_URI
dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~MathTranslationsMongoTest"
```

Expected: `Check_all_tests_overridden` PASSES (proves every upstream method is overridden — if this fails,
you're missing a method the upstream base class declares that isn't in the list above; re-check the upstream
`MathTranslationsTestBase.cs`'s method list before proceeding). Every OTHER test FAILS — but on the
`AssertMql` assertion (`Assert.Empty` / mismatch), never on a data assertion, a serialization exception, or a
seeding exception. If ANY test throws something other than an `AssertMql`-related xUnit assertion failure
(e.g. a BSON serialization exception during seeding), that is the empirical fixture risk the spec flagged —
STOP, diagnose, and fix it as part of this task.

- [ ] **Step 4: Commit**

```bash
git add tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/Translations/MongoBasicTypesQueryFixture.cs \
        tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/Translations/MathTranslationsMongoTest.cs
git commit -m "EF-322: stand up MongoBasicTypesQueryFixture and MathTranslationsMongoTest stub"
```

## Task 2: `MongoMathExpression` IR node, all seven dispatchers, and native `Math.Abs`/`MathF.Abs`

**Files:**
- Create: `src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoMathExpression.cs`
- Create: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.Math.cs`
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoAggregationExpressionRenderer.cs`
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoQueryLanguageRenderer.cs`
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoFieldPrefixRewriter.cs`
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.cs`
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeProjectionBinder.cs`
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoExpressionTranslatorMathTests.cs` (new)
- Modify: `tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/Translations/MathTranslationsMongoTest.cs:Abs_decimal,Abs_int,Abs_double,Abs_float`

**Interfaces:**
- Produces: `internal enum MongoMathFunction` (all ~30 members needed by later tasks, defined now even though
  only `Abs` is wired into the translator this task — the renderer's switch is exhaustive over the WHOLE enum
  from this task, so later tasks only add translator recognition, never touch the renderer's switch shape
  again except adding new `case` arms to it); `internal sealed class MongoMathExpression(MongoMathFunction
  Function, IReadOnlyList<MongoExpression> Operands, Type Type) : MongoExpression`; `private bool
  TryTranslateMath(Expression node, bool allowNumericWidening, out MongoExpression? result)` on
  `MongoExpressionTranslator`, wired into `TranslateOperand`.
- Consumes: `MongoExpressionTranslator.TranslateOperand` (existing, the call site), `MongoExpression`'s base
  contract (existing).

- [ ] **Step 1: Write the failing tests (already exist from Task 1 — confirm which four are this task's target)**

Task 1 already wrote `Abs_decimal`/`Abs_int`/`Abs_double`/`Abs_float`. No new test code to write; this step is
recording which four tests this task turns GREEN.

- [ ] **Step 2: Run the four target tests to confirm the current RED state**

```bash
unset MONGODB_URI ATLAS_URI
dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~MathTranslationsMongoTest.Abs_"
```

Expected: FAIL — `AssertMql()` (empty) doesn't match the actual driver-LINQ-fallback MQL captured for each.

- [ ] **Step 3: Create the IR node**

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

using System;
using System.Collections.Generic;

namespace MongoDB.EntityFrameworkCore.Query.Expressions;

/// <summary>
/// Every <c>Math</c>/<c>MathF</c> function (plus <c>double.RadiansToDegrees</c>/<c>float.DegreesToRadians</c>)
/// this provider translates natively. One node type keyed by this enum, not one sealed type per function —
/// see <see cref="MongoMathExpression"/>'s own remarks.
/// </summary>
internal enum MongoMathFunction
{
    Abs, Ceiling, Floor, Exp, Sqrt, Truncate,
    Round, RoundDigits,
    Ln, Log10, Log2, LogNewBase,
    DegreesToRadians, RadiansToDegrees,
    Acos, Acosh, Asin, Asinh, Atan, Atanh, Cos, Cosh, Sin, Sinh, Tan, Tanh,
    Pow, Atan2, Max, Min,
    Sign
}

/// <summary>
/// A <c>Math</c>/<c>MathF</c> function call, rendered exclusively in the aggregation-expression dialect (no
/// query-dialect form — a numeric function over a field can never be a <c>$match</c> key-value shape).
/// </summary>
/// <remarks>
/// One node type for every function, discriminated by <see cref="Function"/> — mirrors
/// <c>MongoRegexExpression</c>'s <c>Kind</c> discriminator, not <c>MongoDateAddExpression</c>'s one-type-per-
/// concept split, because every function here shares the exact same shape (1 or 2 <see cref="MongoExpression"/>
/// operands in, one MQL operator or small fixed expression out) and a per-function type would be 30 sealed
/// classes differing only in a rendered string.
/// <para>
/// <see cref="Type"/> is passed in explicitly by the translator (<c>call.Method.ReturnType</c>), not inferred
/// from an operand — unlike <c>MongoDateAddExpression</c> where the operand's own type IS the result type,
/// most of these functions change CLR type from their operand (e.g. <c>Math.Sign(double) : int</c>).
/// </para>
/// </remarks>
internal sealed class MongoMathExpression(
    MongoMathFunction function, IReadOnlyList<MongoExpression> operands, Type type)
    : MongoExpression
{
    /// <summary>Which function this call represents.</summary>
    public MongoMathFunction Function { get; } = function;

    /// <summary>
    /// The function's arguments, already translated. 1 element for a unary function (<c>Abs</c>, trig, etc.),
    /// 2 for a binary one (<c>Pow</c>, <c>Atan2</c>, <c>Max</c>, <c>Min</c>), 1 or 2 for <c>Round</c>
    /// (<see cref="MongoMathFunction.Round"/> vs <see cref="MongoMathFunction.RoundDigits"/>) and <c>Log</c>
    /// (<see cref="MongoMathFunction.Ln"/> vs <see cref="MongoMathFunction.LogNewBase"/>).
    /// </summary>
    public IReadOnlyList<MongoExpression> Operands { get; } = operands;

    /// <inheritdoc />
    public override Type Type { get; } = type;
}
```

- [ ] **Step 4: Wire the translator recognizer**

Create `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.Math.cs`:

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
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using MongoDB.EntityFrameworkCore.Query.Expressions;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>
/// <see cref="MongoExpressionTranslator"/> — <c>Math</c>/<c>MathF</c>/<c>double.RadiansToDegrees</c>/
/// <c>float.DegreesToRadians</c> function calls.
/// </summary>
/// <remarks>
/// This task (EF-322) wires up only <see cref="MongoMathFunction.Abs"/>; later tasks add more entries to
/// <see cref="UnaryFunctionsByName"/>/<see cref="BinaryFunctionsByName"/> and more special-cased branches
/// below, but never touch the renderer/dispatcher wiring again — that's all already exhaustive over the whole
/// <see cref="MongoMathFunction"/> enum from this task's Step 5 onward.
/// </remarks>
internal sealed partial class MongoExpressionTranslator
{
    /// <summary>
    /// <c>Math.X(double)</c>/<c>MathF.X(float)</c> unary function names that map 1:1 onto a
    /// <see cref="MongoMathFunction"/> of the identical shape (one operand in, one MQL operator out).
    /// </summary>
    private static readonly Dictionary<string, MongoMathFunction> UnaryFunctionsByName = new()
    {
        [nameof(Math.Abs)] = MongoMathFunction.Abs
    };

    private bool TryTranslateMath(Expression node, bool allowNumericWidening, [NotNullWhen(true)] out MongoExpression? result)
    {
        result = null;

        if (node is not MethodCallExpression call || call.Method.DeclaringType != typeof(Math) && call.Method.DeclaringType != typeof(MathF))
            return false;

        if (call.Arguments.Count != 1 || !UnaryFunctionsByName.TryGetValue(call.Method.Name, out var function))
            return false;

        var operand = TranslateOperand(call.Arguments[0], allowNumericWidening: true);
        if (operand is null)
            return false;

        result = new MongoMathExpression(function, [operand], call.Method.ReturnType);
        return true;
    }
}
```

Wire it into `TranslateOperand` in `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.cs`, immediately after the `TryMatchStringLength` block (confirmed at the point right before the `TryResolveMember` call — search for `if (TryMatchStringLength(node, out var lengthReceiver))` and insert directly after its closing brace):

```csharp
        // A Math/MathF function call (`Math.Abs(x)`, etc.). Same reasoning as the DateAdd/IndexOf arms above:
        // a MethodCallExpression is never matched by TryResolveMember below.
        if (TryTranslateMath(node, allowNumericWidening, out var math))
            return math;
```

- [ ] **Step 5: Wire the five dispatcher touch-points that need code (two need none — see Global Constraints)**

In `MongoAggregationExpressionRenderer.cs`, add the `Render` case (find the `MongoStringLengthExpression length => ...` line and add immediately after it):

```csharp
            MongoMathExpression math => RenderMath(math, placeholders, elementVariable),
```

And the private method (add near `RenderDateAdd`):

```csharp
    private static BsonValue RenderMath(MongoMathExpression node, PlaceholderTable placeholders, string? elementVariable)
    {
        BsonValue Arg(int i) => Render(node.Operands[i], placeholders, elementVariable);

        return node.Function switch
        {
            MongoMathFunction.Abs => new BsonDocument("$abs", Arg(0)),
            _ => throw new NativeTranslationNotSupportedException(
                $"Unhandled {nameof(MongoMathFunction)} '{node.Function}'.")
        };
    }
```

Add the `CanRender` case (find `MongoStringLengthExpression length => CanRender(length.Operand),` — if no such
line exists, add adjacent to the other unary-operand cases):

```csharp
            MongoMathExpression math => math.Operands.All(CanRender),
```

(`System.Linq` is already `using`d in this file — confirmed at the top.)

In `MongoQueryLanguageRenderer.cs`, add to the `IsQueryDialectRenderable` switch, alongside
`MongoStringLengthExpression => false,`:

```csharp
            MongoMathExpression => false,
```

In `MongoFieldPrefixRewriter.cs`, add to the private `Rewrite` switch, alongside the `MongoStringLengthExpression sl => ...` line:

```csharp
            MongoMathExpression m => new MongoMathExpression(
                m.Function, m.Operands.Select(o => Rewrite(o, prefix)).ToList(), m.Type),
```

(Add `using System.Linq;` to this file if not already present — check the file's usings first.)

In `MongoExpressionTranslator.cs`, add to the `AllFieldsDefaultSerialized` switch, alongside the
`MongoStringLengthExpression length => AllFieldsDefaultSerialized(length.Operand),` line:

```csharp
            // Same reasoning as MongoStringLengthExpression immediately above: a Math/MathF function runs
            // $abs/etc. directly against each operand's raw BSON representation, so a value-converted/
            // non-default-represented field underneath would run the function against the WRONG value.
            MongoMathExpression math => math.Operands.All(AllFieldsDefaultSerialized),
```

In `NativeProjectionBinder.cs`, add `MongoMathExpression` to the computed-leaf-projection type list (the `or`
chain containing `MongoDateAddExpression`):

```csharp
            && (value is MongoSizeExpression or MongoFilteredSizeExpression or MongoConvertExpression
                    or MongoConditionalExpression or MongoDatePartExpression or MongoDateTimeOffsetLocalExpression
                    or MongoElementRefExpression or MongoDateAddExpression or MongoCoalesceExpression
                    or MongoMathExpression
```

- [ ] **Step 6: Build and run the four target tests to verify RED→GREEN**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"
```

Expected: builds clean. If `MongoExpressionNodeCoverageTests` (unit test project) fails to build/run due to a
missing dispatcher arm, that test's failure message names exactly which dispatcher is missing — fix it before
proceeding, don't skip the test.

```bash
unset MONGODB_URI ATLAS_URI
EF_TEST_REWRITE_BASELINES=1 dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~MathTranslationsMongoTest.Abs_"
```

Expected: reports FAILED (the rewrite-happened signal). `git diff` the test file to confirm each of the four
`AssertMql()` calls was replaced with real MQL containing `"$abs"`, then:

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"
dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~MathTranslationsMongoTest.Abs_"
MONGODB_EF_NATIVE_ONLY=1 dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~MathTranslationsMongoTest.Abs_"
```

Expected: PASS both times — the second run (`NativeOnly`) is what actually proves this went native, not just
that the MQL shape matches (per `Query/AGENTS.md`'s "MQL shape cannot prove a query went native" pitfall).

- [ ] **Step 7: Add the value-converted-field regression unit test (Review Focus item)**

```csharp
// tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoExpressionTranslatorMathTests.cs
using MongoDB.EntityFrameworkCore.Query.Expressions;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;
using Xunit;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query.NativeTranslation;

public class MongoExpressionTranslatorMathTests
{
    [Fact]
    public void A_math_expression_over_a_non_default_serialized_field_is_not_treated_as_safe()
    {
        // A bare MongoFieldExpression whose IProperty reports non-default serialization must make
        // AllFieldsDefaultSerialized answer false when wrapped in a MongoMathExpression — the explicit
        // recursive case added in this task, not the switch's permissive `_ => true` default.
        var nonDefaultField = FakeNonDefaultSerializedField();
        var math = new MongoMathExpression(MongoMathFunction.Abs, [nonDefaultField], typeof(double));

        Assert.False(MongoExpressionTranslator.AllFieldsDefaultSerialized(math));
    }
}
```

Since `FakeNonDefaultSerializedField()` needs a real `IProperty` double with non-default `BsonRepresentation`,
build it the same way `MongoExpressionNodeCoverageTests.PostProperty(..., valueConverted: true)` already does
for its own converted-field samples — read that method's implementation
(`tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoExpressionNodeCoverageTests.cs`,
the `PostProperty` helper near line 112) and reuse the same in-memory model-building approach (a throwaway
`IEntityType`/`IProperty` pair with a `HasConversion<string>()`-equivalent annotation) rather than hand-rolling
a new one — copy its exact technique into this new test file's own private helper.

Run:

```bash
dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj \
  -c "Debug EF10" --filter "FullyQualifiedName~MongoExpressionTranslatorMathTests"
```

Expected: PASS.

- [ ] **Step 8: Full Query regression sweep**

```bash
dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build --filter "FullyQualifiedName~Query"
dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj -c "Debug EF10" --no-build
```

Expected: PASS, 0 failures (this touches shared dispatcher switches used by every other native shape).

- [ ] **Step 9: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoMathExpression.cs \
        src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.Math.cs \
        src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoAggregationExpressionRenderer.cs \
        src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoQueryLanguageRenderer.cs \
        src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoFieldPrefixRewriter.cs \
        src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.cs \
        src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeProjectionBinder.cs \
        tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoExpressionTranslatorMathTests.cs \
        tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/Translations/MathTranslationsMongoTest.cs
git commit -m "EF-322: native Math.Abs/MathF.Abs translation, MongoMathExpression IR + dispatcher wiring"
```

## Task 3: Remaining simple unary functions (`Ceiling`/`Floor`/`Exp`/`Sqrt`/`Truncate`)

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.Math.cs`
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoAggregationExpressionRenderer.cs`
- Modify: `tests/.../Query/Translations/MathTranslationsMongoTest.cs:Ceiling,Ceiling_float,Floor_decimal,Floor_double,Floor_float,Exp,Exp_float,Sqrt,Sqrt_float,Truncate_decimal,Truncate_double,Truncate_float`

**Interfaces:**
- Consumes: `UnaryFunctionsByName` (Task 2), `RenderMath`'s switch (Task 2) — both extended, not replaced.
- Produces: nothing new for later tasks.

- [ ] **Step 1: Confirm the 12 target tests are currently RED**

```bash
unset MONGODB_URI ATLAS_URI
dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~MathTranslationsMongoTest.Ceiling|FullyQualifiedName~MathTranslationsMongoTest.Floor|FullyQualifiedName~MathTranslationsMongoTest.Exp|FullyQualifiedName~MathTranslationsMongoTest.Sqrt|FullyQualifiedName~MathTranslationsMongoTest.Truncate_decimal|FullyQualifiedName~MathTranslationsMongoTest.Truncate_double|FullyQualifiedName~MathTranslationsMongoTest.Truncate_float"
```

Expected: all FAIL on `AssertMql()`.

- [ ] **Step 2: Extend the lookup table**

In `MongoExpressionTranslator.Math.cs`, extend `UnaryFunctionsByName`:

```csharp
    private static readonly Dictionary<string, MongoMathFunction> UnaryFunctionsByName = new()
    {
        [nameof(Math.Abs)] = MongoMathFunction.Abs,
        [nameof(Math.Ceiling)] = MongoMathFunction.Ceiling,
        [nameof(Math.Floor)] = MongoMathFunction.Floor,
        [nameof(Math.Exp)] = MongoMathFunction.Exp,
        [nameof(Math.Sqrt)] = MongoMathFunction.Sqrt,
        [nameof(Math.Truncate)] = MongoMathFunction.Truncate
    };
```

- [ ] **Step 3: Extend the renderer switch**

In `RenderMath` (`MongoAggregationExpressionRenderer.cs`):

```csharp
        return node.Function switch
        {
            MongoMathFunction.Abs => new BsonDocument("$abs", Arg(0)),
            MongoMathFunction.Ceiling => new BsonDocument("$ceil", Arg(0)),
            MongoMathFunction.Floor => new BsonDocument("$floor", Arg(0)),
            MongoMathFunction.Exp => new BsonDocument("$exp", Arg(0)),
            MongoMathFunction.Sqrt => new BsonDocument("$sqrt", Arg(0)),
            MongoMathFunction.Truncate => new BsonDocument("$trunc", Arg(0)),
            _ => throw new NativeTranslationNotSupportedException(
                $"Unhandled {nameof(MongoMathFunction)} '{node.Function}'.")
        };
```

- [ ] **Step 4: Build, regenerate baselines, verify GREEN under both modes**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"
unset MONGODB_URI ATLAS_URI
EF_TEST_REWRITE_BASELINES=1 dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~MathTranslationsMongoTest.Ceiling|FullyQualifiedName~MathTranslationsMongoTest.Floor|FullyQualifiedName~MathTranslationsMongoTest.Exp|FullyQualifiedName~MathTranslationsMongoTest.Sqrt|FullyQualifiedName~MathTranslationsMongoTest.Truncate_decimal|FullyQualifiedName~MathTranslationsMongoTest.Truncate_double|FullyQualifiedName~MathTranslationsMongoTest.Truncate_float"
git diff tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/Translations/MathTranslationsMongoTest.cs
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"
dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~MathTranslationsMongoTest.Ceiling|FullyQualifiedName~MathTranslationsMongoTest.Floor|FullyQualifiedName~MathTranslationsMongoTest.Exp|FullyQualifiedName~MathTranslationsMongoTest.Sqrt|FullyQualifiedName~MathTranslationsMongoTest.Truncate_decimal|FullyQualifiedName~MathTranslationsMongoTest.Truncate_double|FullyQualifiedName~MathTranslationsMongoTest.Truncate_float"
MONGODB_EF_NATIVE_ONLY=1 dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~MathTranslationsMongoTest.Ceiling|FullyQualifiedName~MathTranslationsMongoTest.Floor|FullyQualifiedName~MathTranslationsMongoTest.Exp|FullyQualifiedName~MathTranslationsMongoTest.Sqrt|FullyQualifiedName~MathTranslationsMongoTest.Truncate_decimal|FullyQualifiedName~MathTranslationsMongoTest.Truncate_double|FullyQualifiedName~MathTranslationsMongoTest.Truncate_float"
```

Expected: rewrite reports FAILED (signal), diff shows real `$ceil`/`$floor`/`$exp`/`$sqrt`/`$trunc` MQL, both
subsequent runs PASS.

- [ ] **Step 5: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.Math.cs \
        src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoAggregationExpressionRenderer.cs \
        tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/Translations/MathTranslationsMongoTest.cs
git commit -m "EF-322: native Math.Ceiling/Floor/Exp/Sqrt/Truncate translation"
```

## Task 4: `Round`/`Round(x, digits)` and `Log`/`Log(x, newBase)`/`Log10`/`Log2`

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.Math.cs`
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoAggregationExpressionRenderer.cs`
- Modify: `tests/.../Query/Translations/MathTranslationsMongoTest.cs:Round_decimal,Round_double,Round_float,Round_with_digits_decimal,Round_with_digits_double,Round_with_digits_float,Log,Log_float,Log_with_newBase,Log_with_newBase_float,Log10,Log10_float,Log2`

**Interfaces:**
- Consumes: `MongoMathExpression.Operands` (Task 2) — this task is the first to construct a node with 2
  operands (`RoundDigits`, `LogNewBase`), proving the list-based `Operands` design handles both arities without
  a shape change.
- Produces: nothing new for later tasks.

`Round` and `Log` both need arg-count disambiguation (1 arg vs 2), unlike Task 3's uniform 1-arg lookup — so
this task extends `TryTranslateMath` itself, not just the lookup table.

- [ ] **Step 1: Confirm the 13 target tests are currently RED**

```bash
unset MONGODB_URI ATLAS_URI
dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~MathTranslationsMongoTest.Round|FullyQualifiedName~MathTranslationsMongoTest.Log"
```

Expected: all FAIL on `AssertMql()`.

- [ ] **Step 2: Extend `TryTranslateMath` for the arg-count-dependent functions**

In `MongoExpressionTranslator.Math.cs`, replace the body of `TryTranslateMath` (keep the existing
unary-lookup fallthrough at the end) with:

```csharp
    private bool TryTranslateMath(Expression node, bool allowNumericWidening, [NotNullWhen(true)] out MongoExpression? result)
    {
        result = null;

        if (node is not MethodCallExpression call || call.Method.DeclaringType != typeof(Math) && call.Method.DeclaringType != typeof(MathF))
            return false;

        if (call.Method.Name == nameof(Math.Round) && call.Arguments.Count is 1 or 2)
            return TryTranslateRoundOrLog(call, allowNumericWidening, call.Arguments.Count == 1 ? MongoMathFunction.Round : MongoMathFunction.RoundDigits, out result);

        if (call.Method.Name == nameof(Math.Log) && call.Arguments.Count is 1 or 2)
            return TryTranslateRoundOrLog(call, allowNumericWidening, call.Arguments.Count == 1 ? MongoMathFunction.Ln : MongoMathFunction.LogNewBase, out result);

        if (call.Arguments.Count != 1 || !UnaryFunctionsByName.TryGetValue(call.Method.Name, out var function))
            return false;

        var operand = TranslateOperand(call.Arguments[0], allowNumericWidening: true);
        if (operand is null)
            return false;

        result = new MongoMathExpression(function, [operand], call.Method.ReturnType);
        return true;
    }

    private bool TryTranslateRoundOrLog(
        MethodCallExpression call, bool allowNumericWidening, MongoMathFunction function,
        [NotNullWhen(true)] out MongoExpression? result)
    {
        result = null;

        var first = TranslateOperand(call.Arguments[0], allowNumericWidening: true);
        if (first is null)
            return false;

        if (call.Arguments.Count == 1)
        {
            result = new MongoMathExpression(function, [first], call.Method.ReturnType);
            return true;
        }

        var second = TranslateOperand(call.Arguments[1], allowNumericWidening: true);
        if (second is null)
            return false;

        result = new MongoMathExpression(function, [first, second], call.Method.ReturnType);
        return true;
    }
```

Add to `UnaryFunctionsByName`:

```csharp
        [nameof(Math.Log10)] = MongoMathFunction.Log10,
        [nameof(Math.Log2)] = MongoMathFunction.Log2
```

- [ ] **Step 3: Extend the renderer switch**

```csharp
            MongoMathFunction.Round => new BsonDocument("$round", Arg(0)),
            MongoMathFunction.RoundDigits => new BsonDocument("$round", new BsonArray { Arg(0), Arg(1) }),
            MongoMathFunction.Ln => new BsonDocument("$ln", Arg(0)),
            MongoMathFunction.Log10 => new BsonDocument("$log10", Arg(0)),
            MongoMathFunction.Log2 => new BsonDocument("$log", new BsonArray { Arg(0), 2 }),
            MongoMathFunction.LogNewBase => new BsonDocument("$log", new BsonArray { Arg(0), Arg(1) }),
```

- [ ] **Step 4: Build, regenerate baselines, verify GREEN under both modes**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"
unset MONGODB_URI ATLAS_URI
EF_TEST_REWRITE_BASELINES=1 dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~MathTranslationsMongoTest.Round|FullyQualifiedName~MathTranslationsMongoTest.Log"
git diff tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/Translations/MathTranslationsMongoTest.cs
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"
dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~MathTranslationsMongoTest.Round|FullyQualifiedName~MathTranslationsMongoTest.Log"
```

Expected: rewrite signal, then GREEN. **Read the actual data assertion results for
`Round_with_digits_decimal`/`_double`/`_float` carefully** — per the Review Focus item, if MongoDB's `$round`
rounding mode diverges from .NET's `Math.Round(x, digits)` for any midpoint case in the seeded
`BasicTypesData`, the DATA assertion (not just `AssertMql`) will fail with a value mismatch, not merely a
baseline mismatch.

- **If they pass:** proceed — the rounding modes agree for this data, ledger nothing further needed.
- **If `Round_with_digits_*` fails on data (not baseline):** this is a genuine behavioral divergence, not a
  bug in this task's code. Revert just the `RoundDigits` lookup/render additions (keep plain `Round`), leave
  `Round_with_digits_decimal/_double/_float` calling `AssertTranslationFailed(() => base.X())` instead (declining
  to fallback for that specific 2-arg shape only), and note this as a `Ruling` in the implementation ledger:
  "MongoDB `$round`'s digit-rounding mode diverges from .NET `Math.Round(x, digits)` for at least one seeded
  value — declined rather than shipped as a silently-wrong native translation." Do NOT force a match by
  guessing at a workaround (e.g. manual half-up correction) without confirming the exact divergence rule first.

```bash
MONGODB_EF_NATIVE_ONLY=1 dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~MathTranslationsMongoTest.Round|FullyQualifiedName~MathTranslationsMongoTest.Log"
```

Expected: PASS (or, per the branch above, PASS for everything except the declined `RoundDigits` tests, which
should throw `NativeTranslationNotSupportedException` under this mode).

- [ ] **Step 5: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.Math.cs \
        src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoAggregationExpressionRenderer.cs \
        tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/Translations/MathTranslationsMongoTest.cs
git commit -m "EF-322: native Math.Round/Log translation (including the 2-arg overloads)"
```

## Task 5: `Math.Sign` (`$switch`, no direct MQL operator)

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.Math.cs`
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoAggregationExpressionRenderer.cs`
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoExpressionTranslatorMathTests.cs`
- Modify: `tests/.../Query/Translations/MathTranslationsMongoTest.cs:Sign,Sign_decimal,Sign_int,Sign_float`

**Interfaces:**
- Consumes: `RenderMath` (Task 2/3/4) — this task adds the one function needing a MULTI-DOCUMENT render (a
  `$switch` with two branches and a default), not a single-operator `BsonDocument("$op", ...)` one-liner.
- Produces: nothing new for later tasks.

- [ ] **Step 1: Confirm the 4 target tests are currently RED**

```bash
unset MONGODB_URI ATLAS_URI
dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~MathTranslationsMongoTest.Sign"
```

Expected: all FAIL on `AssertMql()`.

- [ ] **Step 2: Write the unit test for the `$switch` shape (Review Focus item — zero must hit the default)**

```csharp
    [Fact]
    public void Sign_renders_as_a_switch_with_an_explicit_zero_default()
    {
        var field = FakeDoubleField(); // build the same way as the earlier fake-field helper, default-serialized
        var math = new MongoMathExpression(MongoMathFunction.Sign, [field], typeof(int));

        var rendered = MongoAggregationExpressionRenderer.Render(math, new PlaceholderTable());

        var switchDoc = Assert.IsType<BsonDocument>(rendered)["$switch"].AsBsonDocument;
        var branches = switchDoc["branches"].AsBsonArray;
        Assert.Equal(2, branches.Count);
        Assert.Equal(0, switchDoc["default"].AsInt32);
    }
```

- [ ] **Step 3: Extend `UnaryFunctionsByName` and the renderer**

```csharp
        [nameof(Math.Sign)] = MongoMathFunction.Sign
```

```csharp
            MongoMathFunction.Sign => new BsonDocument("$switch", new BsonDocument
            {
                {
                    "branches", new BsonArray
                    {
                        new BsonDocument { { "case", new BsonDocument("$gt", new BsonArray { Arg(0), 0 }) }, { "then", 1 } },
                        new BsonDocument { { "case", new BsonDocument("$lt", new BsonArray { Arg(0), 0 }) }, { "then", -1 } }
                    }
                },
                { "default", 0 }
            }),
```

- [ ] **Step 4: Build and run the unit test**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"
dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~Sign_renders_as_a_switch"
```

Expected: PASS.

- [ ] **Step 5: Regenerate baselines, verify GREEN under both modes**

```bash
unset MONGODB_URI ATLAS_URI
EF_TEST_REWRITE_BASELINES=1 dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~MathTranslationsMongoTest.Sign"
git diff tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/Translations/MathTranslationsMongoTest.cs
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"
dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~MathTranslationsMongoTest.Sign"
MONGODB_EF_NATIVE_ONLY=1 dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~MathTranslationsMongoTest.Sign"
```

Expected: rewrite signal, both runs PASS.

- [ ] **Step 6: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.Math.cs \
        src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoAggregationExpressionRenderer.cs \
        tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoExpressionTranslatorMathTests.cs \
        tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/Translations/MathTranslationsMongoTest.cs
git commit -m "EF-322: native Math.Sign translation via \$switch"
```

## Task 6: Binary functions — `Pow`/`Atan2`/`Max`/`Min` (including nested `Max`/`Min`)

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.Math.cs`
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoAggregationExpressionRenderer.cs`
- Modify: `tests/.../Query/Translations/MathTranslationsMongoTest.cs:Power,Power_float,Max,Max_nested,Max_nested_twice,Min,Min_nested,Min_nested_twice`

**Interfaces:**
- Consumes: `TranslateOperand`'s own recursion (existing) — `Max_nested`/`Min_nested` require `TranslateOperand`
  to recurse into a NESTED `Math.Max(...)`/`Math.Min(...)` call as one of the outer call's own arguments, which
  falls out automatically because `TryTranslateMath` is itself reached FROM `TranslateOperand`, so translating
  argument N of an outer Math call by calling `TranslateOperand` again naturally re-enters this same
  recognizer for an inner one. No special nesting code needed — this task's job is mostly to prove that.
- Produces: `BinaryFunctionsByName` lookup table, consumed by nothing later (this is the last group needing one).

- [ ] **Step 1: Confirm the 8 target tests are currently RED**

```bash
unset MONGODB_URI ATLAS_URI
dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~MathTranslationsMongoTest.Power|FullyQualifiedName~MathTranslationsMongoTest.Max|FullyQualifiedName~MathTranslationsMongoTest.Min"
```

Expected: all FAIL on `AssertMql()`.

- [ ] **Step 2: Add the binary-function lookup and wire it into `TryTranslateMath`**

In `MongoExpressionTranslator.Math.cs`, add:

```csharp
    private static readonly Dictionary<string, MongoMathFunction> BinaryFunctionsByName = new()
    {
        [nameof(Math.Pow)] = MongoMathFunction.Pow,
        [nameof(Math.Atan2)] = MongoMathFunction.Atan2,
        [nameof(Math.Max)] = MongoMathFunction.Max,
        [nameof(Math.Min)] = MongoMathFunction.Min
    };
```

In `TryTranslateMath`, add a branch before the final unary-lookup fallthrough (after the `Round`/`Log` checks):

```csharp
        if (call.Arguments.Count == 2 && BinaryFunctionsByName.TryGetValue(call.Method.Name, out var binaryFunction))
        {
            var left = TranslateOperand(call.Arguments[0], allowNumericWidening: true);
            var right = left is null ? null : TranslateOperand(call.Arguments[1], allowNumericWidening: true);
            if (left is null || right is null)
                return false;

            result = new MongoMathExpression(binaryFunction, [left, right], call.Method.ReturnType);
            return true;
        }
```

- [ ] **Step 3: Extend the renderer switch**

```csharp
            MongoMathFunction.Pow => new BsonDocument("$pow", new BsonArray { Arg(0), Arg(1) }),
            MongoMathFunction.Atan2 => new BsonDocument("$atan2", new BsonArray { Arg(0), Arg(1) }),
            MongoMathFunction.Max => new BsonDocument("$max", new BsonArray { Arg(0), Arg(1) }),
            MongoMathFunction.Min => new BsonDocument("$min", new BsonArray { Arg(0), Arg(1) }),
```

- [ ] **Step 4: Build, regenerate baselines, verify GREEN under both modes — including the nested cases**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"
unset MONGODB_URI ATLAS_URI
EF_TEST_REWRITE_BASELINES=1 dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~MathTranslationsMongoTest.Power|FullyQualifiedName~MathTranslationsMongoTest.Max|FullyQualifiedName~MathTranslationsMongoTest.Min"
git diff tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/Translations/MathTranslationsMongoTest.cs
```

**Read the `Max_nested`/`Max_nested_twice`/`Min_nested`/`Min_nested_twice` rewritten baselines specifically** —
confirm each shows a NESTED `$max`/`$min` document (e.g. `{"$max": [{"$max": [...]}, ...]}`), not a flattened
or truncated one, per the Review Focus item.

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"
dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~MathTranslationsMongoTest.Power|FullyQualifiedName~MathTranslationsMongoTest.Max|FullyQualifiedName~MathTranslationsMongoTest.Min"
MONGODB_EF_NATIVE_ONLY=1 dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~MathTranslationsMongoTest.Power|FullyQualifiedName~MathTranslationsMongoTest.Max|FullyQualifiedName~MathTranslationsMongoTest.Min"
```

Expected: rewrite signal, both runs PASS.

- [ ] **Step 5: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.Math.cs \
        src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoAggregationExpressionRenderer.cs \
        tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/Translations/MathTranslationsMongoTest.cs
git commit -m "EF-322: native Math.Pow/Atan2/Max/Min translation, including nested Max/Min"
```

## Task 7: `Degrees`/`Radians` and the 12 trig functions

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.Math.cs`
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoAggregationExpressionRenderer.cs`
- Modify: `tests/.../Query/Translations/MathTranslationsMongoTest.cs:Degrees,Degrees_float,Radians,Radians_float,Acos,Acos_float,Acosh,Asin,Asin_float,Asinh,Atan,Atan_float,Atanh,Cos,Cos_float,Cosh,Sin,Sin_float,Sinh,Tan,Tan_float,Tanh`

**Interfaces:**
- Consumes: `UnaryFunctionsByName` (Task 2) — this task's trig functions all fit the plain 1-arg lookup shape;
  `Degrees`/`Radians` need a SEPARATE small lookup since they're declared on `double`/`float` directly, not
  `Math`/`MathF`.
- Produces: nothing new for later tasks — this is the last translator-extension task; Task 8 tests behavior
  already implemented, not new functions.

- [ ] **Step 1: Confirm the 22 target tests are currently RED**

```bash
unset MONGODB_URI ATLAS_URI
dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~MathTranslationsMongoTest.Degrees|FullyQualifiedName~MathTranslationsMongoTest.Radians|FullyQualifiedName~MathTranslationsMongoTest.Acos|FullyQualifiedName~MathTranslationsMongoTest.Asin|FullyQualifiedName~MathTranslationsMongoTest.Atan|FullyQualifiedName~MathTranslationsMongoTest.Cos|FullyQualifiedName~MathTranslationsMongoTest.Sin|FullyQualifiedName~MathTranslationsMongoTest.Tan"
```

Expected: all FAIL on `AssertMql()`.

- [ ] **Step 2: Extend `UnaryFunctionsByName` with the 12 trig functions**

```csharp
        [nameof(Math.Acos)] = MongoMathFunction.Acos,
        [nameof(Math.Acosh)] = MongoMathFunction.Acosh,
        [nameof(Math.Asin)] = MongoMathFunction.Asin,
        [nameof(Math.Asinh)] = MongoMathFunction.Asinh,
        [nameof(Math.Atan)] = MongoMathFunction.Atan,
        [nameof(Math.Atanh)] = MongoMathFunction.Atanh,
        [nameof(Math.Cos)] = MongoMathFunction.Cos,
        [nameof(Math.Cosh)] = MongoMathFunction.Cosh,
        [nameof(Math.Sin)] = MongoMathFunction.Sin,
        [nameof(Math.Sinh)] = MongoMathFunction.Sinh,
        [nameof(Math.Tan)] = MongoMathFunction.Tan,
        [nameof(Math.Tanh)] = MongoMathFunction.Tanh
```

- [ ] **Step 3: Add the `double.RadiansToDegrees`/`float.DegreesToRadians` recognizer**

These are declared on `double`/`float` themselves (.NET 8+ static members), not `Math`/`MathF`, so they need a
separate branch in `TryTranslateMath` (add before the `declaringType != typeof(Math) && ... != typeof(MathF)`
guard, as its own early check):

```csharp
    private static readonly Dictionary<string, MongoMathFunction> AngleConversionsByName = new()
    {
        ["RadiansToDegrees"] = MongoMathFunction.RadiansToDegrees,
        ["DegreesToRadians"] = MongoMathFunction.DegreesToRadians
    };
```

At the top of `TryTranslateMath`, before the `Math`/`MathF` declaring-type guard:

```csharp
        if (node is MethodCallExpression angleCall
            && (angleCall.Method.DeclaringType == typeof(double) || angleCall.Method.DeclaringType == typeof(float))
            && angleCall.Arguments.Count == 1
            && AngleConversionsByName.TryGetValue(angleCall.Method.Name, out var angleFunction))
        {
            var angleOperand = TranslateOperand(angleCall.Arguments[0], allowNumericWidening: true);
            if (angleOperand is null)
                return false;

            result = new MongoMathExpression(angleFunction, [angleOperand], angleCall.Method.ReturnType);
            return true;
        }
```

- [ ] **Step 4: Extend the renderer switch**

```csharp
            MongoMathFunction.DegreesToRadians => new BsonDocument("$degreesToRadians", Arg(0)),
            MongoMathFunction.RadiansToDegrees => new BsonDocument("$radiansToDegrees", Arg(0)),
            MongoMathFunction.Acos => new BsonDocument("$acos", Arg(0)),
            MongoMathFunction.Acosh => new BsonDocument("$acosh", Arg(0)),
            MongoMathFunction.Asin => new BsonDocument("$asin", Arg(0)),
            MongoMathFunction.Asinh => new BsonDocument("$asinh", Arg(0)),
            MongoMathFunction.Atan => new BsonDocument("$atan", Arg(0)),
            MongoMathFunction.Atanh => new BsonDocument("$atanh", Arg(0)),
            MongoMathFunction.Cos => new BsonDocument("$cos", Arg(0)),
            MongoMathFunction.Cosh => new BsonDocument("$cosh", Arg(0)),
            MongoMathFunction.Sin => new BsonDocument("$sin", Arg(0)),
            MongoMathFunction.Sinh => new BsonDocument("$sinh", Arg(0)),
            MongoMathFunction.Tan => new BsonDocument("$tan", Arg(0)),
            MongoMathFunction.Tanh => new BsonDocument("$tanh", Arg(0)),
```

(This exhausts `MongoMathFunction` — every member now has a `RenderMath` case; the `_ => throw` default arm
should be unreachable from here on, kept only as the defensive per-file convention this codebase uses
elsewhere, e.g. `MongoRegexPatternBuilder.BuildPattern`'s own `_ => throw`.)

- [ ] **Step 5: Build, regenerate baselines, verify GREEN under both modes**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"
unset MONGODB_URI ATLAS_URI
EF_TEST_REWRITE_BASELINES=1 dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~MathTranslationsMongoTest.Degrees|FullyQualifiedName~MathTranslationsMongoTest.Radians|FullyQualifiedName~MathTranslationsMongoTest.Acos|FullyQualifiedName~MathTranslationsMongoTest.Asin|FullyQualifiedName~MathTranslationsMongoTest.Atan|FullyQualifiedName~MathTranslationsMongoTest.Cos|FullyQualifiedName~MathTranslationsMongoTest.Sin|FullyQualifiedName~MathTranslationsMongoTest.Tan"
git diff tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/Translations/MathTranslationsMongoTest.cs
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"
dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~MathTranslationsMongoTest.Degrees|FullyQualifiedName~MathTranslationsMongoTest.Radians|FullyQualifiedName~MathTranslationsMongoTest.Acos|FullyQualifiedName~MathTranslationsMongoTest.Asin|FullyQualifiedName~MathTranslationsMongoTest.Atan|FullyQualifiedName~MathTranslationsMongoTest.Cos|FullyQualifiedName~MathTranslationsMongoTest.Sin|FullyQualifiedName~MathTranslationsMongoTest.Tan"
MONGODB_EF_NATIVE_ONLY=1 dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~MathTranslationsMongoTest.Degrees|FullyQualifiedName~MathTranslationsMongoTest.Radians|FullyQualifiedName~MathTranslationsMongoTest.Acos|FullyQualifiedName~MathTranslationsMongoTest.Asin|FullyQualifiedName~MathTranslationsMongoTest.Atan|FullyQualifiedName~MathTranslationsMongoTest.Cos|FullyQualifiedName~MathTranslationsMongoTest.Sin|FullyQualifiedName~MathTranslationsMongoTest.Tan"
```

Expected: rewrite signal, both runs PASS.

- [ ] **Step 6: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.Math.cs \
        src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoAggregationExpressionRenderer.cs \
        tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/Translations/MathTranslationsMongoTest.cs
git commit -m "EF-322: native Degrees/Radians and trig function translation"
```

## Task 8: Projected-and-ordered `Truncate` (`Truncate_project_and_order_by_it_twice{,2,3}`)

**Files:**
- Modify: `tests/.../Query/Translations/MathTranslationsMongoTest.cs:Truncate_project_and_order_by_it_twice,Truncate_project_and_order_by_it_twice2,Truncate_project_and_order_by_it_twice3`

**Interfaces:**
- Consumes: `MongoMathExpression` fully wired (Tasks 2-7) — this task adds NO new translator/renderer code; it
  verifies (per the Review Focus item) that `Select(b => new { A = Math.Truncate(b.Double) }).OrderBy(r => r.A)`
  — projecting a Math result into an anonymous type, then sorting by the projected member — works through
  `NativeProjectionBinder`'s computed-leaf-projection path (Task 2, Step 5) end-to-end, not just a bare `Where`.
- Produces: nothing for later tasks — this is the last test-only task before compliance wiring.

- [ ] **Step 1: Confirm the 3 target tests are currently RED**

```bash
unset MONGODB_URI ATLAS_URI
dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~Truncate_project_and_order_by_it_twice"
```

Expected: FAIL on `AssertMql()` — the DATA assertion inside `base.X()` should already pass (Task 3 made
`Truncate` translate correctly for a `Where`; this task is testing whether the SAME translation also works
correctly when the result is projected-then-sorted, a structurally different code path per this task's own
Interfaces note). If the DATA assertion itself fails here (not just `AssertMql`), that is a real gap in Task
2's `NativeProjectionBinder` wiring or in how a projected computed member gets re-resolved as a later sort
key — diagnose via `systematic-debugging`, don't just force the baseline to match wrong output.

- [ ] **Step 2: Regenerate baselines and verify GREEN under both modes**

```bash
EF_TEST_REWRITE_BASELINES=1 dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~Truncate_project_and_order_by_it_twice"
git diff tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/Translations/MathTranslationsMongoTest.cs
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"
dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~Truncate_project_and_order_by_it_twice"
MONGODB_EF_NATIVE_ONLY=1 dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~Truncate_project_and_order_by_it_twice"
```

Expected: rewrite signal, both runs PASS. If `NativeOnly` throws `NativeTranslationNotSupportedException`
instead, this shape declines to fallback rather than going native — change these three overrides to
`AssertTranslationFailed(() => base.X())` and ledger a `Ruling` explaining the observed decline point (which
pipeline stage/binder rejected it), rather than spending further time forcing it native; the other 61 tests
are the deliverable this plan commits to, not this specific ordering-of-a-projected-computed-member shape.

- [ ] **Step 3: Commit**

```bash
git add tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/Translations/MathTranslationsMongoTest.cs
git commit -m "EF-322: confirm native Truncate translation survives project-then-order-by"
```

## Task 9: Un-ignore the compliance entry and final regression sweep

**Files:**
- Modify: `tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoComplianceTest.cs:192`

**Interfaces:**
- Consumes: every prior task's completed translation (all 64 tests passing).
- Produces: nothing — this is the last task.

- [ ] **Step 1: Confirm all 64 Math tests pass under both modes before touching the compliance list**

```bash
unset MONGODB_URI ATLAS_URI
dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~MathTranslationsMongoTest"
MONGODB_EF_NATIVE_ONLY=1 dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~MathTranslationsMongoTest"
```

Expected: PASS, 0 failures, both runs (allowing for any `Ruling`-documented declines from Tasks 4/8, which
should show as an intentional `AssertTranslationFailed` pass, not a failure).

- [ ] **Step 2: Remove the compliance-test exclusion**

In `tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoComplianceTest.cs`, delete this line (currently
line 192, inside the `#if !EF8 && !EF9` block):

```csharp
        typeof(Microsoft.EntityFrameworkCore.Query.Translations.MathTranslationsTestBase<>),
```

- [ ] **Step 3: Run the compliance test itself**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"
dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~MongoComplianceTest"
```

Expected: PASS — confirms `MathTranslationsTestBase<>` is no longer reported as an un-implemented test base.

- [ ] **Step 4: Full three-EF-version regression**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF9"
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF8"
unset MONGODB_URI ATLAS_URI
dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build --filter "FullyQualifiedName~Query"
dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF9" --no-build --filter "FullyQualifiedName~Query"
dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF8" --no-build --filter "FullyQualifiedName~Query"
dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj -c "Debug EF9" --no-build
dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj -c "Debug EF8" --no-build
```

Expected: PASS on all six runs, 0 failures. (EF8/EF9 builds/tests confirm the whole-file `#if !EF8 && !EF9`
guards on the two new test files correctly exclude them without breaking anything else — the new `src/`
translator/renderer code has no EF-version conditionals at all, so it compiles unconditionally into all three.)

- [ ] **Step 5: Commit**

```bash
git add tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoComplianceTest.cs
git commit -m "EF-322: un-ignore MathTranslationsTestBase from MongoComplianceTest"
```

## Self-Review

- **Spec coverage:** Fixture (Task 1), IR node + all 7 dispatchers (Task 2), every function group the spec's
  operator table lists (Tasks 3-7), the projection/ordering combination the spec didn't explicitly call out as
  its own task but the Review Focus section adds (Task 8), compliance un-ignore (Task 9, matches spec's final
  step). The spec's two flagged empirical risks (fixture round-trip, `Round` digit-rounding-mode divergence)
  each have an explicit stop-and-diagnose (not guess) branch in Task 1 and Task 4 respectively.
- **Placeholder scan:** No TBD/TODO. Every `AssertMql()` empty-call is the documented, intentional RED state
  (explained once in Global Constraints, not repeated as an unexplained gap per task). The two "if this
  diverges, decline instead" branches (Tasks 4, 8) give the exact fallback code shape
  (`AssertTranslationFailed(() => base.X())`) rather than "handle it somehow".
- **Type consistency:** `MongoMathExpression(MongoMathFunction, IReadOnlyList<MongoExpression>, Type)`'s
  constructor signature is identical across every task that constructs one (Tasks 2, 4, 6, 7 all use
  `new MongoMathExpression(function, [operands...], call.Method.ReturnType)`). `TryTranslateMath`'s signature
  (`Expression node, bool allowNumericWidening, out MongoExpression? result`) matches
  `TryTranslateDateAdd`'s existing signature exactly, and is called the same way from `TranslateOperand` in
  Task 2 Step 4. `UnaryFunctionsByName`/`BinaryFunctionsByName`/`AngleConversionsByName` are all
  `Dictionary<string, MongoMathFunction>`, consistent everywhere they're extended (Tasks 3, 4, 6, 7).
- **Review Focus:** All five items each have their owning task and the specific test/step that exercises them
  (value-converted field → Task 2 Step 7 unit test; `Round` rounding mode → Task 4 Step 4's explicit
  data-assertion-vs-baseline-assertion distinction; `Sign` at zero → Task 5 Step 2's explicit `default: 0`
  assertion; nested `Max`/`Min` → Task 6 Step 4's explicit nested-shape read; projected+ordered `Truncate` →
  Task 8 in full).
