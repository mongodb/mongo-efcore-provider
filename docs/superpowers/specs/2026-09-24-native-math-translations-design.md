# Native `Math`/`MathF` translation for `MathTranslationsTestBase` — design

**Ticket:** EF-322 (native LINQ pipeline). Track A, phase 1 of
`docs/superpowers/plans/2026-09-24-misc-native-linq-roadmap.md` — the first slice of EF10's wholly-unimplemented
`Query.Translations` test-base family (`MongoComplianceTest.IgnoredTestBases`, `#if !EF8 && !EF9` block).

## Problem

`Microsoft.EntityFrameworkCore.Query.Translations.MathTranslationsTestBase<TFixture>` (upstream, 64 test
methods covering `Math.*`/`MathF.*`/`double.RadiansToDegrees`/`float.DegreesToRadians` etc.) has no Mongo
implementation at all, and is explicitly ignored by `MongoComplianceTest` for EF10. `Math.*` has **zero**
native translator support anywhere in `src/` today (confirmed by grep — no `"Abs"`/`"Ceiling"`/`"Sqrt"` etc. in
`NativeTranslation/`); the EF8/EF9-only predecessor file (`NorthwindFunctionsQueryMongoTest.cs`) shows these
functions currently pass only via silent driver-LINQ fallback for `Math.*` (and fail even via the driver for
`MathF.*`/`Math.Min`/`Max`/`Degrees`/`Radians` — tickets `EF-237`/`EF-238`/`EF-240` in that file).

Unlike every prior EF-322 phase, this one has a **second, prerequisite problem**: the upstream test base
doesn't use the Northwind model. It's built on `BasicTypesQueryFixtureBase<BasicTypesEntity>` — a flat,
scalar-only entity (`Byte/Short/Int/Long/Float/Double/Decimal/String/DateTime/DateOnly/TimeOnly/
DateTimeOffset/TimeSpan/Bool/Guid/ByteArray/Enum/FlagsEnum`, no navigations) shared by **every** `Translations`
test base (String, Misc, the 5 temporal bases, the 4 `Operators` bases, ByteArray/Enum/Guid) — nothing for it
exists in this provider yet. Building it now is a one-time cost that unlocks all of Track A, not just Math.

## Architecture

### 1. Shared fixture: `MongoBasicTypesQueryFixture`

`BasicTypesEntity`/`NullableBasicTypesEntity`/`BasicTypesContext`/`BasicTypesData` ship compiled in the
`Microsoft.EntityFrameworkCore.Specification.Tests` package (namespace
`Microsoft.EntityFrameworkCore.TestModels.BasicTypesModel`) — no need to redefine them. `BasicTypesQueryFixtureBase`
(also upstream, in `Microsoft.EntityFrameworkCore.Query.Translations`) already implements `SeedAsync`,
`GetExpectedData`, `EntitySorters`, `EntityAsserters` concretely. Only the Mongo-specific wiring is new:

`tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/Translations/MongoBasicTypesQueryFixture.cs`
(new folder, mirroring upstream's own `Query/Translations` layout), modeled directly on
`Query/NorthwindQueryMongoFixture.cs`:

- `StoreName` → `TestDatabaseNamer.GetUniqueDatabaseName("BasicTypes")`.
- `InitializeAsync` → `TestServer.GetOrInitializeTestServerAsync(...)` + `MongoTestStoreFactory`, same pattern.
- `OnModelCreating` → `modelBuilder.Entity<BasicTypesEntity>().ToCollection("BasicTypesEntities")` and the
  `NullableBasicTypesEntity` equivalent (needed even though Math tests never query it — `SeedAsync` inserts
  both, so both must be mappable, or seeding throws before any test runs).
- `TestMqlLoggerFactory` property, `ShouldLogCategory`, `UsePooling => false` — copied verbatim from the
  Northwind fixture; these aren't Northwind-specific.
- **No `TModelCustomizer` generic parameter** — `BasicTypesQueryFixtureBase` isn't generic over one (unlike
  `NorthwindQueryFixtureBase<TModelCustomizer>`), so this fixture is simpler: a single concrete class, not a
  generic one instantiated with `NoopModelCustomizer` at each test class's declaration.
- **Risk to verify in Task 1, before writing a single Math test:** does the model even build and seed
  correctly? `TimeSpan`/`DateOnly`/`TimeOnly`/`Guid`/`byte[]`/enums must all already have working BSON
  serialization in this provider (they're exercised elsewhere, so expected to work, but this is the first time
  they're ALL on one entity with no per-property configuration) — Task 1 is "stand up the fixture and confirm
  `Check_all_tests_overridden`-equivalent seeding succeeds with zero Math overrides yet", not "write Math
  translators".

### 2. New IR node: `MongoMathExpression`

Following the established pattern for a node needing the same shape across many call sites (mirrors
`MongoRegexExpression{Kind, Field, Term}`, not 35 separate sealed types):

```csharp
internal enum MongoMathFunction
{
    Abs, Ceiling, Floor, Exp, Sqrt, Truncate,          // unary, 1 operand
    Round, RoundDigits,                                 // unary+optional digits — 1 or 2 operands
    Ln, Log10, Log2, LogNewBase,                        // Log() / Log10() / Log2() / Log(x, newBase)
    DegreesToRadians, RadiansToDegrees,
    Acos, Acosh, Asin, Asinh, Atan, Atanh, Cos, Cosh, Sin, Sinh, Tan, Tanh,   // unary trig
    Pow, Atan2, Max, Min,                               // binary, 2 operands
    Sign                                                // unary, rendered via $switch — no direct MQL operator
}

internal sealed class MongoMathExpression(
    MongoMathFunction function, IReadOnlyList<MongoExpression> operands, Type type)
    : MongoExpression
{
    public MongoMathFunction Function { get; } = function;
    public IReadOnlyList<MongoExpression> Operands { get; } = operands;
    public override Type Type { get; } = type;
}
```

`Type` is passed in explicitly (not inferred from an operand) because most of these change CLR type from the
operand (e.g. `Math.Sign(double) : int`), unlike `MongoDateAddExpression` where `StartDate`'s type IS the
result type.

**Aggregation-expression dialect only** — like `MongoDateAddExpression`/`MongoDatePartExpression`, this node
has no query-dialect form (a numeric function over a field can't be a `$match` key-value shape) and must
answer `false` at `MongoQueryLanguageRenderer.IsQueryDialectRenderable`.

**Operator mapping** (`RenderMath` in `MongoAggregationExpressionRenderer.cs`), one `switch` on `Function`:

| `MongoMathFunction` | MQL |
|---|---|
| `Abs`/`Ceiling`/`Floor`/`Exp`/`Sqrt`/`Truncate` | `$abs`/`$ceil`/`$floor`/`$exp`/`$sqrt`/`$trunc` of `Operands[0]` |
| `Round` | `$round: Operands[0]` (1-arg form) |
| `RoundDigits` | `$round: [Operands[0], Operands[1]]` |
| `Ln` | `$ln: Operands[0]` (`Math.Log(x)` — natural log) |
| `Log10` | `$log10: Operands[0]` |
| `Log2` | `$log: [Operands[0], 2]` (no `$log2` operator; base-2 via `$log`'s 2-arg form) |
| `LogNewBase` | `$log: [Operands[0], Operands[1]]` (`Math.Log(x, newBase)`) |
| `DegreesToRadians`/`RadiansToDegrees` | `$degreesToRadians`/`$radiansToDegrees` of `Operands[0]` |
| `Acos`/`Acosh`/`Asin`/`Asinh`/`Atan`/`Atanh`/`Cos`/`Cosh`/`Sin`/`Sinh`/`Tan`/`Tanh` | same-named `$acos`/etc. of `Operands[0]` — MongoDB has all 12 as direct operators |
| `Pow` | `$pow: [Operands[0], Operands[1]]` |
| `Atan2` | `$atan2: [Operands[0], Operands[1]]` |
| `Max`/`Min` | `$max`/`$min: [Operands[0], Operands[1]]` — the **expression** form (an array literal), not the `$group` accumulator form; nested `Math.Max(Math.Max(...), ...)` falls out for free as nested `MongoMathExpression` nodes, no special-casing needed |
| `Sign` | **no direct operator** — `{ "$switch": { "branches": [ { "case": {"$gt":[operand,0]}, "then": 1 }, { "case": {"$lt":[operand,0]}, "then": -1 } ], "default": 0 } }` |

### 3. Translator recognition

New file `MongoExpressionTranslator.Math.cs` (partial class, mirrors `.TypeIs.cs`/`.Like.cs`'s shape), one
`TryTranslateMath(MethodCallExpression call, out MongoExpression? result)` dispatching on:

- `call.Method.DeclaringType == typeof(Math)` or `typeof(MathF)` — same `MongoMathFunction` regardless of
  which type declares the method (both map to identical MQL; the .NET-side distinction is only which CLR
  numeric type the operand/result use, irrelevant to the aggregation pipeline).
- `call.Method.DeclaringType == typeof(double)` or `typeof(float)` with `Method.Name` `RadiansToDegrees`/
  `DegreesToRadians` (the .NET 8+ static interface members `double.RadiansToDegrees(double)` etc. upstream
  uses instead of a `Math.*` call).
- Method-name → `MongoMathFunction` is a static lookup table (`Dictionary<string, MongoMathFunction>` for the
  ~29 unary/binary names sharing 1:1 shape), with `Round`/`Log` handled by argument COUNT (1 vs 2 args) rather
  than name alone, and `Sign` needing no table entry beyond the enum itself.
- Each argument translated via the existing `TranslateValue`/`TryTranslateValue` machinery (already handles
  field/constant/parameter/nested-computed-expression uniformly) — no new argument-resolution code needed.

### 4. The other four dispatcher touch-points

Following `MongoDateAddExpression`'s exact precedent (grep-confirmed, 4 files beyond the IR node + translator
recognizer):

- `MongoAggregationExpressionRenderer.cs`: `Render` case (`RenderMath`, above) + `CanRender` case
  (`CanRender(math) => math.Operands.All(CanRender)`).
- `MongoQueryLanguageRenderer.cs`'s `IsQueryDialectRenderable` switch: `MongoMathExpression => false`.
- `MongoFieldPrefixRewriter.cs`: a case rewriting `Operands` recursively (needed for a Math call inside a
  `$filter`/`$map` element predicate over an owned collection — not exercised by `MathTranslationsTestBase`
  itself, since `BasicTypesEntity` has no collections, but required for dispatcher-coverage completeness per
  `MongoExpressionNodeCoverageTests`, and for any FUTURE query combining Math with an owned collection).
- `NativeProjectionBinder.cs`'s computed-leaf-projection list (~line 1035): add `MongoMathExpression`, needed
  for the `AssertQueryScalar(ss => ss.Set<BasicTypesEntity>().Select(b => Math.Truncate(b.Double)))` shape
  (`Round_decimal`/`_double`/`_float`, `Truncate_decimal`/`_double`/`_float`, `Sign`/`_decimal`/`_int`/`_float`
  each have a scalar-projection half, not just a `Where`).
- `MongoExpressionNodeCoverageTests` (reflection-discovers every `MongoExpression` subtype, checks all seven
  dispatchers per `Query/AGENTS.md`'s durable-invariants list) — this test will fail at build/run time if any
  of the above is missed; treat it as the completeness check, not an extra manual task.

### 5. Test class

`tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/Translations/MathTranslationsMongoTest.cs`:
`MathTranslationsMongoTest : MathTranslationsTestBase<MongoBasicTypesQueryFixture>`. 64 overrides, each
`await base.X(); AssertMql(...)` once native, following this repo's baseline-regeneration workflow
(`EF_TEST_REWRITE_BASELINES=1`) for every baseline — given the EF8/EF9-guard rewriter bug found in the
`EF.Functions.Like` work (silently no-ops inside an `#if` region), and that this whole file is EF10-only (no
`#if` needed — `MathTranslationsTestBase` doesn't exist for EF8/EF9), that bug does not apply here; the
in-place rewriter should work cleanly for every test in this file.

Remove `typeof(Microsoft.EntityFrameworkCore.Query.Translations.MathTranslationsTestBase<>)` from
`MongoComplianceTest.cs`'s `IgnoredTestBases` (line 192) once the class exists and all 64 tests pass.

## Out of scope

- Every other `Translations` test base (String, Misc, temporal ×5, Operators ×4, ByteArray/Enum/Guid) — future
  Track A phases, each gets its own spec+plan, but all reuse `MongoBasicTypesQueryFixture` from this phase.
- `GroupBy`/`Include`/join-subquery machinery — unaffected; `BasicTypesEntity` has no navigations, so this
  phase cannot touch that code at all.
- Widening `Math`/`MathF` support to work INSIDE a `$group` accumulator, a correlated subquery, or a
  `Select` producing an entity (only scalar `Where`/`Select` projections are in this test base) — no test
  requires it, not attempted.

## Risks / open questions for Task 1 to resolve empirically, not guess

- Whether `BasicTypesEntity`'s full property set seeds and round-trips correctly through this provider's BSON
  serialization with zero custom configuration (see the fixture risk note above) — if something doesn't
  round-trip (e.g. `TimeOnly` or `DateOnly` needs an explicit `BsonRepresentation`), fix that as part of Task 1
  before any Math-specific work, and note it as a ruling.
- Whether MongoDB's `$round`/`$trunc` "place" (digits) argument behavves identically to .NET's
  `MidpointRounding.ToEven` default for `Math.Round(x, digits)` — `Round_with_digits_decimal/_double/_float`
  will empirically confirm or refute this; if it diverges, that's a genuine behavioral gap to document (declined
  translation for the digits-overload, not a wrong-answer native implementation), not something to force.
