# String Translations Phase 1 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or
> superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for
> tracking.

**Goal:** Stand up `StringTranslationsTestBase<MongoBasicTypesQueryFixture>` as a real Mongo spec-test class
(`StringTranslationsMongoTest.cs`) and close the native-translation gaps it exposes, **excluding** the 5 tests
that depend on native `GroupBy` (other agents' active work on this branch). This is Track A, phase 2 of
`docs/superpowers/plans/2026-09-24-native-math-translations.md`'s roadmap (`docs/superpowers/plans/
2026-09-24-misc-native-linq-roadmap.md` — Math, phase 1, is done: 68/68 passing, removed from
`MongoComplianceTest.IgnoredTestBases`).

**Scoping method:** Per the roadmap's own prescribed empirical method (exact test bodies aren't guessable from
the compiled `Microsoft.EntityFrameworkCore.Specification.Tests` NuGet package), a throwaway
`StringTranslationsMongoTest : StringTranslationsTestBase<MongoBasicTypesQueryFixture>` with **no overrides**
was already created and run against a real container (`MONGODB_URI`/`ATLAS_URI` unset). Result: **76/100
pass** (native or driver-LINQ fallback already produces correct results), **24 fail**. Of the 24, **5** use
`GroupBy` and are out of scope here; **19** are genuine, non-`GroupBy`, non-`$lookup` gaps — the scope of this
plan.

**Architecture:** Five independent, additive slices to `MongoExpressionTranslator` and its supporting
`MongoExpression` IR, following the exact precedent `MongoExpressionTranslator.Like.cs` (EF.Functions.Like) and
`MongoExpressionTranslator.Math.cs`/`MongoMathExpression` (Math/MathF) already established on this branch:
1. `Trim`/`TrimStart`/`TrimEnd`'s zero-arg and `char`/`char[]`-arg overloads → MQL `$trim`/`$ltrim`/`$rtrim`
   with a `chars` option.
2. `StartsWith`/`EndsWith`/`Contains`'s 2-arg `(string, StringComparison)` overload → extend the existing
   `MongoRegexExpression`/`MongoRegexKind` machinery with a case-insensitive flag for `Ordinal`/
   `OrdinalIgnoreCase`; explicitly decline (translation-failure, not silent wrong-data) the four
   culture-sensitive `StringComparison` members.
3. `string.FirstOrDefault()`/`LastOrDefault()` (single-char extraction) → `$substrCP`/`$strLenCP`.
4. `Regex.IsMatch(constantString, fieldPattern)` (arguments reversed from the already-native
   `Regex.IsMatch(field, constantPattern)` shape) → new arm in the existing Regex translator.
5. `string.Join(separator, arrayLiteralOfFieldsAndConstants)` (non-aggregate — no `GroupBy` involved) →
   `$concat` with `$ifNull` for `null` elements.

**Tech Stack:** C# / .NET, MongoDB aggregation pipeline (`$trim`/`$ltrim`/`$rtrim`, `$substrCP`, `$strLenCP`,
`$regexMatch`, `$concat`, `$ifNull`), xUnit spec tests, `EF_TEST_REWRITE_BASELINES` baseline regeneration.

**Spec:** No separate design doc — the empirical scoping above (real pass/fail data from a real run) **is**
the spec, per this repo's own Track-A methodology.

## Global Constraints

- Branch: `EF-322-Native-LINQ-rebased`. Commit messages: `EF-322: <description>`.
- **EF10-only.** `StringTranslationsTestBase<>`/`BasicTypesQueryFixtureBase` don't exist in the EF8/EF9-pinned
  `Microsoft.EntityFrameworkCore.Specification.Tests` package. `StringTranslationsMongoTest.cs`'s entire
  contents must be wrapped in `#if !EF8 && !EF9` / `#endif` (whole-file guard), exactly like
  `MathTranslationsMongoTest.cs`. The fixture (`MongoBasicTypesQueryFixture.cs`) already exists — reuse it,
  no changes needed.
- **Never hand-write an `AssertMql` baseline body.** For every test getting real MQL assertions, write
  `AssertMql();` (empty — the correct RED state), then regenerate via `EF_TEST_REWRITE_BASELINES=1 dotnet test
  tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~StringTranslationsMongoTest.<Method>"`, then
  `git diff` to confirm before trusting it, then rebuild and rerun without the var to confirm green.
- **Excluded from this plan (do not touch):** `Join_over_non_nullable_column`, `Join_over_nullable_column`,
  `Join_with_predicate`, `Join_with_ordering`, `Concat_aggregate` — all five call `.GroupBy(...)` before the
  string aggregate; wrap each in `AssertTranslationFailed` with a `// Fails: depends on native GroupBy
  (other agent's work) EF-149` comment, exactly like `NorthwindAggregateOperatorsQueryMongoTest
  .Contains_inside_aggregate_function_with_GroupBy`'s existing convention. Never implement native `GroupBy`
  support as part of this plan even if a task would be easier that way.
- A new `MongoExpression` subtype (none needed here — Tasks 2-5 reuse/extend `MongoRegexExpression` and
  ordinary field/constant nodes) would need all seven dispatchers per `Query/AGENTS.md`; since no new subtype
  is introduced, this constraint doesn't add work, but re-verify before deviating from the plan.
- After every task touching `src/`, run a full (not `--no-build`) `dotnet build MongoDB.EFCoreProvider.sln -c
  "Debug EF10"` before testing.
- Test project reference for `--filter`: `MongoDB.EntityFrameworkCore.SpecificationTests.csproj`.

## Review Focus

- **A value-converted/non-default-`BsonRepresentation` string property used inside `Trim`/`StartsWith`/etc.**
  — every existing native string-predicate translator (`TryMatchRegexMethod`) already requires
  `call.Object.Type == typeof(string)` and resolves through `TryTranslateField`, which already declines a
  converted field; each new task must keep using that same resolution path, never read a raw BSON value
  assuming default representation.
- **`OrdinalIgnoreCase` must not silently produce `Ordinal` results.** The case-insensitive flag added in
  Task 2 must actually change the rendered `$regularExpression` options; a copy-paste that always renders
  `"s"` would make `Contains_with_StringComparison_OrdinalIgnoreCase` pass with the WRONG (case-sensitive)
  semantics on data that happens not to need case-folding — pin with a fixture value that only matches
  case-insensitively.
- **`FirstOrDefault()`/`LastOrDefault()` on an EMPTY string** must match .NET's own `default(char)` (`'\0'`)
  contract, not throw or return an empty string silently compared wrong — `BasicTypesEntity.String` is
  non-nullable/non-empty in the fixture data, but the translator itself must not assume that; check what
  `$substrCP` returns for an out-of-range start on an empty/short string and handle explicitly if it's not
  `""`.
- **`Regex.IsMatch(constant, field)` where the field's value is not a valid regex** (e.g. contains unescaped
  regex metacharacters like `.` or `(` that happen to be literal in the test data) — MongoDB's `$regexMatch`
  interprets the field's string as a live pattern with no escaping; confirm this matches upstream's own
  semantics (upstream also treats the second `Regex.IsMatch` argument as a real pattern, not a literal, so no
  translation-side escaping is expected) rather than assuming it needs `Regex.Escape`-equivalent treatment.
- **`Join_non_aggregate`'s `null` array element** (`new[] { c.String, foo, null, "bar" }`) — `$concat` treats
  any `null`/missing operand as making the WHOLE expression evaluate to `null`, not skip it; the translation
  must wrap each element in `{$ifNull: [elem, ""]}` or the whole result silently becomes `null` instead of
  `"Seattle|foo||bar"`.

---

## Task 1: Stand up `StringTranslationsMongoTest.cs` with baselines for the 76 already-passing tests

**Files:**
- Modify: `tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/Translations/StringTranslationsMongoTest.cs`
  (currently a throwaway scaffold with zero overrides — created during this plan's own scoping investigation;
  replace its contents per this task)
- Modify: `tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoComplianceTest.cs:198` (remove
  `typeof(Microsoft.EntityFrameworkCore.Query.Translations.StringTranslationsTestBase<>)` from
  `IgnoredTestBases` — do this at the END of this task, once every method has an override, not before)

**Interfaces:**
- Consumes: `MongoBasicTypesQueryFixture` (already exists, `Query/Translations/MongoBasicTypesQueryFixture.cs`
  — no changes needed), the shared `AssertTranslationFailed(Func<Task>)` helper already used throughout this
  test project's `QueryTestBase` hierarchy.
- Produces: a compiling, green (for 76/100) or intentionally-red-with-`AssertTranslationFailed` (for the other
  24) test class that Tasks 2-6 flip to green one gap at a time.

- [ ] **Step 1: Replace the scaffold with a full override skeleton**

Every one of the base class's ~100 `[Fact]` methods needs an override. For the **19 in-scope gap tests** and
the **5 excluded `GroupBy` tests**, write the override now, fully, using `AssertTranslationFailed` (these are
known-red from the empirical run in this plan's Goal section — no RED-state discovery round-trip needed):

```csharp
// In-scope gaps (Tasks 2-6 close these one at a time):
public override async Task TrimStart_without_arguments()
    // Fails: TrimStart() zero-arg overload EF-X101
    => await AssertTranslationFailed(() => base.TrimStart_without_arguments());

public override async Task TrimStart_with_char_argument()
    // Fails: TrimStart(char) overload EF-X101
    => await AssertTranslationFailed(() => base.TrimStart_with_char_argument());

public override async Task TrimEnd_without_arguments()
    // Fails: TrimEnd() zero-arg overload EF-X101
    => await AssertTranslationFailed(() => base.TrimEnd_without_arguments());

public override async Task TrimEnd_with_char_argument()
    // Fails: TrimEnd(char) overload EF-X101
    => await AssertTranslationFailed(() => base.TrimEnd_with_char_argument());

public override async Task Trim_with_char_argument_in_predicate()
    // Fails: Trim(char) overload EF-X101
    => await AssertTranslationFailed(() => base.Trim_with_char_argument_in_predicate());

public override async Task Trim_with_char_array_argument_in_predicate()
    // Fails: Trim(char[]) overload EF-X101
    => await AssertTranslationFailed(() => base.Trim_with_char_array_argument_in_predicate());

public override async Task StartsWith_with_StringComparison_Ordinal()
    // Fails: StringComparison overload EF-X102
    => await AssertTranslationFailed(() => base.StartsWith_with_StringComparison_Ordinal());

public override async Task StartsWith_with_StringComparison_OrdinalIgnoreCase()
    // Fails: StringComparison overload EF-X102
    => await AssertTranslationFailed(() => base.StartsWith_with_StringComparison_OrdinalIgnoreCase());

public override async Task StartsWith_with_StringComparison_unsupported()
    // Fails: StringComparison overload EF-X102
    => await AssertTranslationFailed(() => base.StartsWith_with_StringComparison_unsupported());

public override async Task EndsWith_with_StringComparison_Ordinal()
    // Fails: StringComparison overload EF-X102
    => await AssertTranslationFailed(() => base.EndsWith_with_StringComparison_Ordinal());

public override async Task EndsWith_with_StringComparison_OrdinalIgnoreCase()
    // Fails: StringComparison overload EF-X102
    => await AssertTranslationFailed(() => base.EndsWith_with_StringComparison_OrdinalIgnoreCase());

public override async Task EndsWith_with_StringComparison_unsupported()
    // Fails: StringComparison overload EF-X102
    => await AssertTranslationFailed(() => base.EndsWith_with_StringComparison_unsupported());

public override async Task Contains_with_StringComparison_Ordinal()
    // Fails: StringComparison overload EF-X102
    => await AssertTranslationFailed(() => base.Contains_with_StringComparison_Ordinal());

public override async Task Contains_with_StringComparison_OrdinalIgnoreCase()
    // Fails: StringComparison overload EF-X102
    => await AssertTranslationFailed(() => base.Contains_with_StringComparison_OrdinalIgnoreCase());

public override async Task Contains_with_StringComparison_unsupported()
    // Fails: StringComparison overload EF-X102
    => await AssertTranslationFailed(() => base.Contains_with_StringComparison_unsupported());

public override Task FirstOrDefault()
    // Fails: string.FirstOrDefault() char extraction EF-X103
    => AssertTranslationFailed(() => base.FirstOrDefault());

public override Task LastOrDefault()
    // Fails: string.LastOrDefault() char extraction EF-X103
    => AssertTranslationFailed(() => base.LastOrDefault());

public override async Task Regex_IsMatch_constant_input()
    // Fails: Regex.IsMatch(constant, field) reversed-argument shape EF-X104
    => await AssertTranslationFailed(() => base.Regex_IsMatch_constant_input());

public override Task Join_non_aggregate()
    // Fails: string.Join over an array literal EF-X105
    => AssertTranslationFailed(() => base.Join_non_aggregate());

// Excluded — depends on native GroupBy, other agents' active work:
public override Task Join_over_non_nullable_column()
    // Fails: depends on native GroupBy (other agent's work) EF-149
    => AssertTranslationFailed(() => base.Join_over_non_nullable_column());

public override Task Join_over_nullable_column()
    // Fails: depends on native GroupBy (other agent's work) EF-149
    => AssertTranslationFailed(() => base.Join_over_nullable_column());

public override Task Join_with_predicate()
    // Fails: depends on native GroupBy (other agent's work) EF-149
    => AssertTranslationFailed(() => base.Join_with_predicate());

public override Task Join_with_ordering()
    // Fails: depends on native GroupBy (other agent's work) EF-149
    => AssertTranslationFailed(() => base.Join_with_ordering());

public override Task Concat_aggregate()
    // Fails: depends on native GroupBy (other agent's work) EF-149
    => AssertTranslationFailed(() => base.Concat_aggregate());
```

For the remaining **76 already-passing** methods, write the mechanical pattern this repo's Track-A convention
uses for every already-native/fallback-correct test (see `MathTranslationsMongoTest.cs` for the identical
pattern applied to `MathTranslationsTestBase`): call `base.<Method>()` then an empty `AssertMql();`, e.g.:

```csharp
public override async Task Equals()
{
    await base.Equals();

    AssertMql();
}

public override async Task Length()
{
    await base.Length();

    AssertMql();
}
```

Repeat for all 76 (the full list is every method in `StringTranslationsTestBase.cs` NOT in the two lists
above — `Equals`, `Equals_with_OrdinalIgnoreCase`, `Equals_with_Ordinal`, `Static_Equals`,
`Static_Equals_with_OrdinalIgnoreCase`, `Static_Equals_with_Ordinal`, `Length`, `ToUpper`, `ToLower`,
`IndexOf`, `IndexOf_Char`, `IndexOf_with_empty_string`, `IndexOf_with_one_parameter_arg`,
`IndexOf_with_one_parameter_arg_char`, `IndexOf_with_constant_starting_position`,
`IndexOf_with_constant_starting_position_char`, `IndexOf_with_parameter_starting_position`,
`IndexOf_with_parameter_starting_position_char`, `IndexOf_after_ToString`, `IndexOf_over_ToString`, `Replace`,
`Replace_Char`, `Replace_with_empty_string`, `Replace_using_property_arguments`, `Substring`,
`Substring_with_one_arg_with_zero_startIndex`, `Substring_with_one_arg_with_constant`,
`Substring_with_one_arg_with_parameter`, `Substring_with_two_args_with_zero_startIndex`,
`Substring_with_two_args_with_zero_length`, `Substring_with_two_args_with_parameter`,
`Substring_with_two_args_with_IndexOf`, `IsNullOrEmpty`, `IsNullOrEmpty_negated`, `IsNullOrWhiteSpace`,
`StartsWith_Literal`, `StartsWith_Literal_Char`, `StartsWith_Parameter`, `StartsWith_Parameter_Char`,
`StartsWith_Column`, `EndsWith_Literal`, `EndsWith_Literal_Char`, `EndsWith_Parameter`,
`EndsWith_Parameter_Char`, `EndsWith_Column`, `Contains_Literal`, `Contains_Literal_Char`, `Contains_Column`,
`Contains_negated`, `Contains_constant_with_whitespace`, `Contains_parameter_with_whitespace`,
`TrimStart_with_char_array_argument`, `TrimEnd_with_char_array_argument`, `Trim_without_argument_in_predicate`,
`Compare_simple_zero`, `Compare_simple_one`, `Compare_with_parameter`, `Compare_simple_more_than_one`,
`Compare_nested`, `Compare_multi_predicate`, `CompareTo_simple_zero`, `CompareTo_simple_one`,
`CompareTo_with_parameter`, `CompareTo_simple_more_than_one`, `CompareTo_nested`, `Compare_to_multi_predicate`,
`Concat_operator`, `Concat_string_int_comparison1`, `Concat_string_int_comparison2`,
`Concat_string_int_comparison3`, `Concat_string_int_comparison4`, `Concat_string_string_comparison`,
`Concat_method_comparison`, `Concat_method_comparison_2`, `Concat_method_comparison_3`, `Regex_IsMatch`).
Count check: 76 methods in this list + 19 gap + 5 excluded = 100, matching the empirical run's `Total tests:
100`.

- [ ] **Step 2: Build**

Run: `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"`
Expected: `Build succeeded`, 0 errors.

- [ ] **Step 3: Regenerate baselines for the 76 passing tests**

Run: `EF_TEST_REWRITE_BASELINES=1 dotnet test
tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj -c
"Debug EF10" --no-build --filter "FullyQualifiedName~StringTranslationsMongoTest"`
Expected: the run reports failures (that's the rewrite signal, per
`tests/MongoDB.EntityFrameworkCore.SpecificationTests/AGENTS.md`) but every `AssertMql();` for the 76 passing
methods gets filled in with real MQL. `git diff` the test file to confirm only those 76 methods' bodies
changed (no accidental edits to the 24 gap methods, whose `AssertTranslationFailed` calls the rewriter cannot
touch — they don't reach an `AssertMql` call at all).

- [ ] **Step 4: Rebuild and confirm green**

Run: `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"` then
`dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj -c "Debug EF10" --no-build --filter "FullyQualifiedName~StringTranslationsMongoTest"`
Expected: `Total tests: 100, Passed: 100` (the 76 via their new baselines, the 24 via `AssertTranslationFailed`
correctly observing a translation failure).

- [ ] **Step 5: Commit**

```bash
git add tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/Translations/StringTranslationsMongoTest.cs
git commit -m "EF-322: stand up StringTranslationsMongoTest with baselines for already-passing shapes"
```

---

## Task 2: `Trim`/`TrimStart`/`TrimEnd` zero-arg and `char`/`char[]`-arg overloads

**Files:**
- Create: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.Trim.cs`
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoTrimExpression.cs` (new file — see below)
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoAggregationExpressionRenderer.cs`
  (add a `case MongoTrimExpression` — this node has **no query-dialect form**, `$trim`/`$ltrim`/`$rtrim` exist
  only in the aggregation-expression `$expr` dialect, exactly like `MongoMathExpression`)
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoQueryLanguageRenderer.cs`
  (`IsQueryDialectRenderable` needs `MongoTrimExpression => false`)
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoFieldPrefixRewriter.cs` (a case, since
  every dispatcher must cover every node type per `Query/AGENTS.md`)
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.cs`
  (`AllFieldsDefaultSerialized` needs a recursive case — **not optional**, see that method's own comment on
  why the default arm is unsafe)
- Modify: `tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/Translations/StringTranslationsMongoTest.cs`
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoExpressionTranslatorTrimTests.cs`

**Interfaces:**
- Produces: `MongoTrimExpression(MongoExpression Source, MongoTrimSide Side, MongoExpression? Chars)` —
  `Side` is `Both | Start | End` (mirrors .NET's own `Trim`/`TrimStart`/`TrimEnd` split), `Chars` is `null` for
  the zero-arg overload or a `MongoConstantExpression` of type `string` built by concatenating a `char` or
  `char[]` argument's constant value(s) (MQL's `$trim`/`$ltrim`/`$rtrim` `chars` option takes a STRING of
  characters to strip, semantically identical to .NET's `Trim(char[])` "strip any of these chars" contract —
  confirmed by reading both APIs' docs, not assumed).
- Consumes: `MongoExpressionTranslator.TryTranslateValue` (existing dispatcher every computed-operand
  recognizer, including `MongoExpressionTranslator.Math.cs`'s Math/MathF arm, is wired into) for the receiver
  string operand.

- [ ] **Step 1: Add the IR node**

```csharp
// src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoTrimExpression.cs
using System;

namespace MongoDB.EntityFrameworkCore.Query.Expressions;

/// <summary>Which side(s) of <see cref="MongoTrimExpression.Source"/> to strip.</summary>
internal enum MongoTrimSide
{
    Both,
    Start,
    End
}

/// <summary>
/// Represents <c>string.Trim()</c>/<c>TrimStart()</c>/<c>TrimEnd()</c> and their <c>char</c>/<c>char[]</c>-arg
/// overloads — MQL <c>$trim</c>/<c>$ltrim</c>/<c>$rtrim</c>, aggregation-expression dialect only (no query-
/// dialect form exists for these).
/// </summary>
internal sealed class MongoTrimExpression : MongoExpression
{
    public MongoTrimExpression(MongoExpression source, MongoTrimSide side, MongoExpression? chars)
    {
        Source = source;
        Side = side;
        Chars = chars;
    }

    /// <summary>The string-typed operand being trimmed.</summary>
    public MongoExpression Source { get; }

    /// <summary>Which side(s) to strip.</summary>
    public MongoTrimSide Side { get; }

    /// <summary>
    /// The characters to strip, as a single string constant (e.g. <c>"Se"</c> for <c>Trim(new[]{'S','e'})</c>),
    /// or <see langword="null"/> for the zero-arg overload (MongoDB's own default: whitespace).
    /// </summary>
    public MongoExpression? Chars { get; }

    /// <inheritdoc />
    public override Type Type => typeof(string);
}
```

- [ ] **Step 2: Write the failing unit test**

```csharp
// tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoExpressionTranslatorTrimTests.cs
using System.Linq.Expressions;
using MongoDB.EntityFrameworkCore.Query.Expressions;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;
using Xunit;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query.NativeTranslation;

public class MongoExpressionTranslatorTrimTests
{
    private sealed class Entity
    {
        public string Text { get; set; } = "";
    }

    [Fact]
    public void TrimStart_zero_arg_translates_to_MongoTrimExpression_Start_with_null_chars()
    {
        var parameter = Expression.Parameter(typeof(Entity), "e");
        var call = Expression.Call(
            Expression.Property(parameter, nameof(Entity.Text)),
            typeof(string).GetMethod(nameof(string.TrimStart), [])!);

        var translator = new MongoExpressionTranslator(TestEntityType.For<Entity>(), parameter);
        Assert.True(translator.TryTranslateValue(call, out var result));
        var trim = Assert.IsType<MongoTrimExpression>(result);
        Assert.Equal(MongoTrimSide.Start, trim.Side);
        Assert.Null(trim.Chars);
    }

    [Fact]
    public void Trim_with_char_array_argument_translates_chars_to_a_joined_string_constant()
    {
        var parameter = Expression.Parameter(typeof(Entity), "e");
        var call = Expression.Call(
            Expression.Property(parameter, nameof(Entity.Text)),
            typeof(string).GetMethod(nameof(string.Trim), [typeof(char[])])!,
            Expression.Constant(new[] { 'S', 'e' }));

        var translator = new MongoExpressionTranslator(TestEntityType.For<Entity>(), parameter);
        Assert.True(translator.TryTranslateValue(call, out var result));
        var trim = Assert.IsType<MongoTrimExpression>(result);
        Assert.Equal(MongoTrimSide.Both, trim.Side);
        var chars = Assert.IsType<MongoConstantExpression>(trim.Chars);
        Assert.Equal("Se", chars.Value);
    }
}
```

(If `TestEntityType.For<Entity>()` doesn't already exist as a helper in this unit test project, use whatever
existing helper the sibling `MongoExpressionTranslator` unit tests in the same directory use to build an
`IEntityType`/parameter pair — check `MongoExpressionTranslatorMathTests.cs` or equivalent for the established
pattern rather than inventing a new one.)

- [ ] **Step 2b: Run to verify it fails**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj -c "Debug EF10" --filter "FullyQualifiedName~MongoExpressionTranslatorTrimTests"`
Expected: FAIL — `MongoTrimExpression` / `TryTranslateValue` doesn't recognize the call yet.

- [ ] **Step 3: Implement the recognizer**

```csharp
// src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.Trim.cs
using System.Linq;
using System.Linq.Expressions;
using MongoDB.EntityFrameworkCore.Query.Expressions;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

internal partial class MongoExpressionTranslator
{
    /// <summary>
    /// Recognizes <c>string.Trim()</c>/<c>TrimStart()</c>/<c>TrimEnd()</c> and their <c>char</c>/<c>char[]</c>
    /// overloads. The driver-LINQ v3 provider only translates the <c>char[]</c>-arg overload of
    /// <c>TrimStart</c>/<c>TrimEnd</c> and the zero-arg overload of <c>Trim</c> (confirmed empirically via
    /// `StringTranslationsMongoTest`'s throwaway scoping run) — every other combination here is genuinely new
    /// capability, not a native conversion of existing fallback behavior.
    /// </summary>
    private bool TryTranslateTrim(MethodCallExpression call, out MongoExpression? result)
    {
        result = null;

        if (call.Method.IsStatic || call.Object is null || call.Object.Type != typeof(string))
            return false;

        MongoTrimSide side;
        switch (call.Method.Name)
        {
            case nameof(string.Trim):
                side = MongoTrimSide.Both;
                break;
            case nameof(string.TrimStart):
                side = MongoTrimSide.Start;
                break;
            case nameof(string.TrimEnd):
                side = MongoTrimSide.End;
                break;
            default:
                return false;
        }

        if (!TryTranslateValue(call.Object, out var source))
            return false;

        MongoExpression? chars = null;
        if (call.Arguments.Count == 1)
        {
            switch (call.Arguments[0])
            {
                case ConstantExpression { Value: char ch }:
                    chars = new MongoConstantExpression(ch.ToString(), forSerialization: null);
                    break;
                case ConstantExpression { Value: char[] chArray }:
                    chars = new MongoConstantExpression(new string(chArray), forSerialization: null);
                    break;
                case NewArrayExpression { Expressions: var elements }
                    when elements.All(e => e is ConstantExpression { Value: char }):
                    chars = new MongoConstantExpression(
                        new string(elements.Select(e => (char)((ConstantExpression)e).Value!).ToArray()),
                        forSerialization: null);
                    break;
                default:
                    // A parameterized/computed char[] has no compile-time string to build; decline rather
                    // than guess (no placeholder-substitution path exists for a computed `chars` operand,
                    // same reasoning as the correlated-reducer leaf's constant-only predicate gate).
                    return false;
            }
        }
        else if (call.Arguments.Count > 1)
        {
            return false;
        }

        result = new MongoTrimExpression(source, side, chars);
        return true;
    }
}
```

Wire `TryTranslateTrim` into the same dispatch point `TryTranslateTrim`'s sibling recognizers
(`TryMatchRegexMethod` for StartsWith/EndsWith/Contains, the Math dispatch for `Math.*`) are called from
inside `MongoExpressionTranslator.TryTranslateValue`'s method-call switch — add a call to `TryTranslateTrim`
there, following the exact same "try this recognizer, return its result if it matched" pattern the existing
recognizers use (read the surrounding 10-15 lines of that switch before adding the call, to match the
established style exactly rather than guessing indentation/ordering).

- [ ] **Step 4: Add the renderer/dispatcher cases**

In `MongoAggregationExpressionRenderer.cs`, add (following the existing `MongoMathExpression` case immediately
above/below it as a style template):

```csharp
MongoTrimExpression trim => RenderTrim(trim),
```

with:

```csharp
private BsonValue RenderTrim(MongoTrimExpression trim)
{
    var op = trim.Side switch
    {
        MongoTrimSide.Both => "$trim",
        MongoTrimSide.Start => "$ltrim",
        MongoTrimSide.End => "$rtrim",
        _ => throw new ArgumentOutOfRangeException(nameof(trim))
    };

    var spec = new BsonDocument("input", Render(trim.Source));
    if (trim.Chars is not null)
    {
        spec.Add("chars", Render(trim.Chars));
    }

    return new BsonDocument(op, spec);
}
```

(Match `Render`'s actual return type / helper naming to whatever the neighboring `MongoMathExpression` case
already uses — copy its exact signature rather than inventing a new one.)

In `MongoQueryLanguageRenderer.IsQueryDialectRenderable`, add `MongoTrimExpression => false,`.

In `MongoFieldPrefixRewriter`'s private `Rewrite` switch, add a case that recurses into `Source` and `Chars`
(mirroring how the `MongoMathExpression` case there recurses into its operands) and returns a new
`MongoTrimExpression` with the rewritten children.

In `MongoExpressionTranslator.AllFieldsDefaultSerialized`, add:

```csharp
MongoTrimExpression trim =>
    AllFieldsDefaultSerialized(trim.Source) && (trim.Chars is null || AllFieldsDefaultSerialized(trim.Chars)),
```

- [ ] **Step 5: Build and run the unit test**

Run: `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"` then
`dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj -c "Debug EF10" --no-build --filter "FullyQualifiedName~MongoExpressionTranslatorTrimTests"`
Expected: PASS.

- [ ] **Step 6: Flip the 6 spec tests and regenerate their baselines**

In `StringTranslationsMongoTest.cs`, change these 6 overrides from `AssertTranslationFailed` to the ordinary
`await base.X(); AssertMql();` pattern: `TrimStart_without_arguments`, `TrimStart_with_char_argument`,
`TrimEnd_without_arguments`, `TrimEnd_with_char_argument`, `Trim_with_char_argument_in_predicate`,
`Trim_with_char_array_argument_in_predicate`.

Run: `EF_TEST_REWRITE_BASELINES=1 dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj -c "Debug EF10" --no-build --filter "FullyQualifiedName~StringTranslationsMongoTest"`, `git diff` to confirm only those 6 methods changed, rebuild, rerun to confirm all 6 now green.

- [ ] **Step 7: Also run under `MONGODB_EF_NATIVE_ONLY=1`**

Run: `MONGODB_EF_NATIVE_ONLY=1 dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj -c "Debug EF10" --no-build --filter "FullyQualifiedName~StringTranslationsMongoTest"`
Expected: the same 6 (plus everything already native) pass — proves these actually went native, not merely
"driver-LINQ now happens to work." If any of the 6 fails only under `NativeOnly`, the recognizer above has a
gap; fix before moving on (do not weaken the assertion to hide it).

- [ ] **Step 8: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoTrimExpression.cs \
        src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.Trim.cs \
        src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoAggregationExpressionRenderer.cs \
        src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoQueryLanguageRenderer.cs \
        src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoFieldPrefixRewriter.cs \
        src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.cs \
        tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoExpressionTranslatorTrimTests.cs \
        tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/Translations/StringTranslationsMongoTest.cs
git commit -m "EF-322: native Trim/TrimStart/TrimEnd char and char[] overloads"
```

---

## Task 3: `StartsWith`/`EndsWith`/`Contains` with an explicit `StringComparison`

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoRegexExpression.cs` (add `CaseInsensitive`)
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.MethodCalls.cs`
  (extend `TryMatchRegexMethod`, lines ~404-434)
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoQueryLanguageRenderer.cs:328-344`
  (`RenderRegex`'s options string)
- Modify: `tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/Translations/StringTranslationsMongoTest.cs`
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoExpressionTranslatorRegexTests.cs`
  (extend if it exists, else create alongside Task 2's new test file)

**Interfaces:**
- Consumes: existing `MongoRegexExpression(MongoExpression field, MongoRegexKind kind, MongoExpression term,
  bool negated)` constructor — extended, not replaced (see Step 1; every existing call site passes the new
  parameter explicitly as `false` to keep current behavior unchanged).
- Produces: `MongoRegexExpression.CaseInsensitive` — read by `RenderRegex`.

- [ ] **Step 1: Add `CaseInsensitive` to `MongoRegexExpression`**

```csharp
public MongoRegexExpression(
    MongoExpression field, MongoRegexKind kind, MongoExpression term, bool negated, bool caseInsensitive = false)
{
    Field = field;
    Kind = kind;
    Term = term;
    Negated = negated;
    CaseInsensitive = caseInsensitive;
}

/// <summary>
/// <see langword="true"/> for a case-insensitive match (<c>StringComparison.OrdinalIgnoreCase</c>).
/// <see langword="false"/> (the default, matching every pre-existing construction site) for the ordinal
/// case-sensitive form.
/// </summary>
public bool CaseInsensitive { get; }
```

Default parameter keeps every existing call site (the `Like` translator, the plain 1-arg `StartsWith`/
`EndsWith`/`Contains` translator from Task 3's own `TryMatchRegexMethod` extension below) compiling unchanged.

- [ ] **Step 2: Write the failing unit test**

```csharp
[Fact]
public void Contains_with_StringComparison_OrdinalIgnoreCase_sets_CaseInsensitive()
{
    var parameter = Expression.Parameter(typeof(Entity), "e");
    var call = Expression.Call(
        Expression.Property(parameter, nameof(Entity.Text)),
        typeof(string).GetMethod(nameof(string.Contains), [typeof(string), typeof(StringComparison)])!,
        Expression.Constant("eattl"),
        Expression.Constant(StringComparison.OrdinalIgnoreCase));

    var translator = new MongoExpressionTranslator(TestEntityType.For<Entity>(), parameter);
    Assert.True(translator.TryTranslate(call, out var result));
    var regex = Assert.IsType<MongoRegexExpression>(result);
    Assert.True(regex.CaseInsensitive);
}

[Fact]
public void Contains_with_StringComparison_CurrentCulture_declines()
{
    var parameter = Expression.Parameter(typeof(Entity), "e");
    var call = Expression.Call(
        Expression.Property(parameter, nameof(Entity.Text)),
        typeof(string).GetMethod(nameof(string.Contains), [typeof(string), typeof(StringComparison)])!,
        Expression.Constant("eattl"),
        Expression.Constant(StringComparison.CurrentCulture));

    var translator = new MongoExpressionTranslator(TestEntityType.For<Entity>(), parameter);
    Assert.False(translator.TryTranslate(call, out _));
}
```

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj -c "Debug EF10" --filter "FullyQualifiedName~CaseInsensitive|FullyQualifiedName~CurrentCulture_declines"`
Expected: FAIL (both) — `TryMatchRegexMethod` still hard-requires `call.Arguments.Count != 1` to decline.

- [ ] **Step 3: Extend `TryMatchRegexMethod`**

```csharp
private static bool TryMatchRegexMethod(
    MethodCallExpression call,
    out MongoRegexKind kind,
    [NotNullWhen(true)] out Expression? receiver,
    [NotNullWhen(true)] out Expression? term,
    out bool caseInsensitive)
{
    kind = default;
    receiver = null;
    term = null;
    caseInsensitive = false;

    if (call.Method.IsStatic || call.Object is null || call.Object.Type != typeof(string))
        return false;

    switch (call.Method.Name)
    {
        case nameof(string.StartsWith):
            kind = MongoRegexKind.StartsWith;
            break;
        case nameof(string.EndsWith):
            kind = MongoRegexKind.EndsWith;
            break;
        case nameof(string.Contains):
            kind = MongoRegexKind.Contains;
            break;
        default:
            return false;
    }

    switch (call.Arguments.Count)
    {
        case 1 when call.Arguments[0].Type == typeof(string):
            break;

        case 2 when call.Arguments[0].Type == typeof(string)
                    && call.Arguments[1] is ConstantExpression { Value: StringComparison comparison }:
            // Only the two ordinal members have a fixed, culture-independent meaning MongoDB's regex engine
            // can reproduce ($regularExpression has no culture-aware collation). CurrentCulture(IgnoreCase)/
            // InvariantCulture(IgnoreCase) decline — these are the *_unsupported tests' correctly-expected
            // outcome, not a gap to close.
            if (comparison is not (StringComparison.Ordinal or StringComparison.OrdinalIgnoreCase))
                return false;

            caseInsensitive = comparison == StringComparison.OrdinalIgnoreCase;
            break;

        default:
            return false;
    }

    receiver = call.Object;
    term = call.Arguments[0];
    return true;
}
```

Update `TryMatchRegexMethod`'s caller(s) to pass `caseInsensitive` through into the constructed
`MongoRegexExpression(field, kind, term, negated, caseInsensitive)`.

- [ ] **Step 4: Thread `CaseInsensitive` into the renderer**

In `MongoQueryLanguageRenderer.RenderRegex` (line ~344), change:

```csharp
body = new BsonRegularExpression(pattern, regex.Kind == MongoRegexKind.Like ? "is" : "s");
```

to:

```csharp
body = new BsonRegularExpression(
    pattern,
    regex.Kind == MongoRegexKind.Like ? "is" : regex.CaseInsensitive ? "is" : "s");
```

(Read the surrounding method first — confirm whether `"s"` for the non-`Like` case is itself load-bearing for
something other than dotall semantics, e.g. multiline anchoring for `StartsWith`/`EndsWith`'s own `^`/`$`
pattern construction; if so, the case-insensitive branch must combine flags, e.g. `"is"` might already be
correct as written above only if `Like`'s own `"is"` already carries whatever `"s"` was doing — verify by
running Step 6 rather than assuming.)

- [ ] **Step 5: Build and run the unit tests**

Run: `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"` then re-run Step 2's tests.
Expected: PASS.

- [ ] **Step 6: Flip the 9 spec tests and regenerate baselines**

Flip `StartsWith_with_StringComparison_Ordinal`, `StartsWith_with_StringComparison_OrdinalIgnoreCase`,
`EndsWith_with_StringComparison_Ordinal`, `EndsWith_with_StringComparison_OrdinalIgnoreCase`,
`Contains_with_StringComparison_Ordinal`, `Contains_with_StringComparison_OrdinalIgnoreCase` to
`await base.X(); AssertMql();`. Leave `StartsWith_with_StringComparison_unsupported`,
`EndsWith_with_StringComparison_unsupported`, `Contains_with_StringComparison_unsupported` on
`AssertTranslationFailed` (they assert the CurrentCulture/InvariantCulture members correctly decline) but
remove their `// Fails:` comment (they're not really failing anymore in the "unimplemented" sense — they're
pinning a permanent, correct decline, same convention as `Where_bitwise_xor` in the misc-roadmap doc).

Regenerate baselines for the 6 flipped tests via `EF_TEST_REWRITE_BASELINES=1` (same procedure as Task 2 Step
6), `git diff`, rebuild, rerun to confirm all 9 pass (6 with real MQL, 3 via `AssertTranslationFailed`).

- [ ] **Step 7: Run under `MONGODB_EF_NATIVE_ONLY=1`**

Same as Task 2 Step 7, filtered to these 9 tests.

- [ ] **Step 8: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoRegexExpression.cs \
        src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.MethodCalls.cs \
        src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoQueryLanguageRenderer.cs \
        tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoExpressionTranslatorRegexTests.cs \
        tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/Translations/StringTranslationsMongoTest.cs
git commit -m "EF-322: native StartsWith/EndsWith/Contains StringComparison.Ordinal(IgnoreCase) overload"
```

---

## Task 4: `string.FirstOrDefault()` / `LastOrDefault()`

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.MethodCalls.cs`
  (new recognizer, alongside the existing `Contains`-over-collection one already in this file)
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoAggregationExpressionRenderer.cs`
  (render as a `$cond`-guarded `$substrCP`, reusing existing BSON-building helpers — no new `MongoExpression`
  subtype needed if it fits as a combination of existing arithmetic/conditional nodes; if the existing IR has
  no conditional/ternary node to compose with, fall back to a small dedicated node exactly like Task 2's
  `MongoTrimExpression` — decide empirically from what `MongoExpressionTranslator.cs`'s existing arithmetic
  translation already exposes before adding a new type)
- Modify: `tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/Translations/StringTranslationsMongoTest.cs`
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoExpressionTranslatorStringOperatorTests.cs`

**Interfaces:**
- Consumes: `MongoExpressionTranslator.TryTranslateValue` (for the string receiver).
- Produces: a `char`-typed `MongoExpression` (whatever concrete shape Step 1's investigation settles on).

- [ ] **Step 1: Determine .NET's exact empty-string contract first**

`"".FirstOrDefault()` and `"".LastOrDefault()` both return `default(char)` = `'\0'`, never throw (confirmed:
`FirstOrDefault`/`LastOrDefault` are the `OrDefault` LINQ family, not `First`/`Last`). MongoDB's `$substrCP`
on an out-of-range start index returns `""` (empty string), not an error and not `"\0"` — so the translation
must explicitly handle the empty-source case rather than assume `$substrCP` alone reproduces the contract.
Confirm this by testing `$substrCP` behavior directly (e.g. via `mongosh` or a throwaway aggregation) before
writing the translator, rather than assuming — pin whatever the real behavior is with the Review Focus test
below.

- [ ] **Step 2: Write the failing unit test**

```csharp
[Fact]
public void FirstOrDefault_over_string_translates_to_a_char_typed_expression()
{
    var parameter = Expression.Parameter(typeof(Entity), "e");
    var call = Expression.Call(
        typeof(Enumerable).GetMethods()
            .Single(m => m.Name == nameof(Enumerable.FirstOrDefault) && m.GetParameters().Length == 1)
            .MakeGenericMethod(typeof(char)),
        Expression.Property(parameter, nameof(Entity.Text)));

    var translator = new MongoExpressionTranslator(TestEntityType.For<Entity>(), parameter);
    Assert.True(translator.TryTranslateValue(call, out var result));
    Assert.Equal(typeof(char), result!.Type);
}
```

(`string.FirstOrDefault()`/`LastOrDefault()` resolve via `string`'s own `IEnumerable<char>` implementation to
`Enumerable.FirstOrDefault<char>(IEnumerable<char>)`/`LastOrDefault<char>` — confirm the exact
`MethodCallExpression` shape EF Core's preprocessor hands the translator empirically (add a debug breakpoint
or print `call.Method` while running the existing failing spec test) rather than guessing the generic method
resolution above is exactly right.)

- [ ] **Step 3: Implement, guided by Step 1's confirmed empty-string behavior**

Write `TryTranslateStringFirstOrLast` (or fold into an existing method-call switch) recognizing
`Enumerable.FirstOrDefault<char>(stringExpr)` / `LastOrDefault<char>(stringExpr)` where `stringExpr.Type ==
typeof(string)`, producing the empty-safe extraction determined in Step 1. Wire into `TryTranslateValue`'s
dispatch, same pattern as Tasks 2-3.

- [ ] **Step 4: Build, run unit test, flip the 2 spec tests, regenerate baselines, run under `NativeOnly`, commit**

Same sub-steps as Task 2/3 (build → unit test green → flip `FirstOrDefault`/`LastOrDefault` in
`StringTranslationsMongoTest.cs` → `EF_TEST_REWRITE_BASELINES=1` → `git diff` → rebuild → rerun →
`MONGODB_EF_NATIVE_ONLY=1` rerun → commit as `EF-322: native string.FirstOrDefault()/LastOrDefault() char extraction`).

---

## Task 5: `Regex.IsMatch(constantString, fieldPattern)` (reversed arguments)

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.cs` (or wherever
  the existing, already-native `Regex.IsMatch(field, constantPattern)` recognizer lives — locate it first with
  `grep -rn "Regex.IsMatch\|RegexOptions" src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/`, since this
  plan's earlier investigation confirmed the forward-argument shape already passes natively but did not
  pin down which file owns it)
- Modify: `tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/Translations/StringTranslationsMongoTest.cs`
- Test: extend whatever unit test file already covers the forward-argument `Regex.IsMatch` shape

**Interfaces:**
- Consumes/produces: the same `MongoRegexExpression`-adjacent or dedicated regex-match IR node the existing
  forward-argument recognizer already builds — this task ADDS a second recognized argument order to the SAME
  output shape, it does not introduce a new node type.

- [ ] **Step 1: Locate the existing `Regex.IsMatch` recognizer**

Run: `grep -rn "IsMatch" src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/*.cs`
Read that method in full before writing anything — this task's implementation is "add a second argument-order
arm to that same method," not a new method, so the exact existing signature and IR node it builds must be
known first (this step intentionally has no invented code, since fabricating it without reading the real
method risks contradicting the existing, already-tested forward-argument path).

- [ ] **Step 2: Write the failing unit test** — mirror whatever test already exists for the forward-argument
`Regex.IsMatch(field, constantPattern)` shape in the same test file, but with a `Regex.IsMatch(constantInput,
fieldPattern)` call expression, asserting the translator still produces the equivalent IR with `input` and
`pattern` swapped relative to the forward case.

- [ ] **Step 3: Extend the recognizer** to also match `Regex.IsMatch(Expression input, Expression pattern)`
where `input` is a `ConstantExpression`/`string` and `pattern` resolves via `TryTranslateField` to a plain
string field (mirroring the existing forward arm's own field/constant role split, just swapped) — build the
IR node with `Field = <the resolved pattern field>` and whatever the existing node represents "the fixed
input string" as, reusing its exact property names (do not invent new ones; Step 1's read tells you what they
are).

- [ ] **Step 4: Build, run unit test, flip `Regex_IsMatch_constant_input`, regenerate baseline, run under
`NativeOnly`, commit** (`EF-322: native Regex.IsMatch with a constant input and a field-valued pattern`).

---

## Task 6: `string.Join(separator, arrayLiteral)` (non-aggregate)

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.MethodCalls.cs`
- Modify: `tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/Translations/StringTranslationsMongoTest.cs`
- Test: extend `MongoExpressionTranslatorStringOperatorTests.cs` from Task 4

**Interfaces:**
- Consumes: `MongoExpressionTranslator.TryTranslateValue` for each array element.
- Produces: a `$concat`-based `MongoExpression` composed from existing arithmetic/computed-value IR (a
  variadic string-concat node likely already exists for the plain `+` operator tests
  `Concat_string_int_comparison*`/`Concat_method_comparison*` in the 76-already-passing list — reuse it rather
  than adding a second concat representation; confirm by reading how `Concat_operator`/`Concat_method_comparison`
  already translate before writing new code).

- [ ] **Step 1: Read how the existing, already-native `Concat_operator`/`Concat_method_comparison*` tests
translate** (`grep -rn "Concat" src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/*.cs`) to find the
existing variadic-concat IR/renderer this task should reuse.

- [ ] **Step 2: Write the failing unit test** for
`string.Join("|", new[] { fieldExpr, constExpr, (string?)null, "bar" })`, asserting it translates to the same
concat IR Step 1 found, with each element wrapped so a `null` element contributes `""` rather than nulling out
the whole `$concat` (per this plan's Review Focus item on exactly this hazard) — build the separator into the
translation by interleaving `sep` between each pair of elements' `$ifNull`-wrapped operands (`string.Join`
has no native MQL equivalent; the wrapped-`$concat`-with-interleaved-separator expansion is the only
representable form for a small, compile-time-fixed-length array literal — this necessarily excludes a
variable-length runtime collection, which the base test class never exercises for this particular test).

- [ ] **Step 3: Implement** `TryTranslateStringJoin` recognizing `string.Join(string separator, T[]
elements)`/`string.Join(string, IEnumerable<string>)` where `elements` is a compile-time `NewArrayExpression`
(decline anything else — a parameterized/runtime array has no fixed arity to interleave a separator into),
translating each element via `TryTranslateValue`, wrapping each in `$ifNull` per Step 2, and building the
interleaved concat.

- [ ] **Step 4: Build, run unit test, flip `Join_non_aggregate`, regenerate baseline, run under `NativeOnly`,
commit** (`EF-322: native string.Join over a fixed-arity array literal`).

---

## Task 7: Remove `StringTranslationsTestBase<>` from `IgnoredTestBases` and full regression

**Files:**
- Modify: `tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoComplianceTest.cs:198`

**Interfaces:** None (closing task).

- [ ] **Step 1: Remove the ignore entry**

Delete the line
`typeof(Microsoft.EntityFrameworkCore.Query.Translations.StringTranslationsTestBase<>),` from
`IgnoredTestBases`.

- [ ] **Step 2: Full spec-suite run for EF10**

Run: `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"` then
`dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj -c "Debug EF10" --no-build`
Expected: no new failures anywhere in the suite (confirms `MongoComplianceTest`'s own reflective coverage
check — which fails if a test base is neither implemented nor ignored — is satisfied, and that no other spec
file's baseline shifted from the `MongoRegexExpression`/`MongoTrimExpression` dispatcher changes).

- [ ] **Step 3: Confirm EF8/EF9 still build clean** (the whole file is `#if`-guarded out for them, but a stray
reference outside the guard would break those configs)

Run: `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF8"` and `-c "Debug EF9"`
Expected: both succeed.

- [ ] **Step 4: Full `/test-all` sweep**

Invoke the `/test-all` skill (builds and tests all three EF versions in parallel) as the final confirmation.

- [ ] **Step 5: Commit**

```bash
git add tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoComplianceTest.cs
git commit -m "EF-322: remove StringTranslationsTestBase from IgnoredTestBases"
```

## Self-Review

- **Spec coverage:** All 19 in-scope gaps from the empirical scoping run are covered (Trim family: Task 2;
  StringComparison overloads: Task 3; FirstOrDefault/LastOrDefault: Task 4; Regex reversed-args: Task 5;
  Join_non_aggregate: Task 6). The 5 `GroupBy`-dependent tests are explicitly excluded per Global Constraints
  and left declining with a ticket comment, matching this repo's established convention for a durable,
  intentionally-deferred gap.
- **Placeholder scan:** Task 5's Step 1 and Task 4's Step 1 are deliberately investigation-first (read the
  real existing code / confirm real `$substrCP` behavior before writing the implementation) rather than
  fabricated code, because guessing either would risk contradicting already-tested, already-native behavior.
  This is the documented exception this skill's own "No Placeholders" guidance allows for ("Lookups: hand
  over the exact command") — not a filled-in-later TBD.
- **Type consistency:** `MongoTrimExpression`/`MongoTrimSide` (Task 2) and `MongoRegexExpression
  .CaseInsensitive` (Task 3) are each introduced once and consumed with the same names throughout their task;
  Tasks 4-6 explicitly defer their exact IR shape to an investigation step rather than inventing inconsistent
  names.
- **Review Focus:** all five items have a concrete pinning step inside the task that owns the code (Task 2
  Step 1/4 for value-converted fields via reused `TryTranslateField`/`AllFieldsDefaultSerialized`; Task 3's
  unit test + Step 6 for `OrdinalIgnoreCase` actually changing behavior; Task 4 Step 1 for the empty-string
  contract; Task 5 Step 1's read-before-write guard; Task 6 Step 2 for the `null`-in-`$concat` hazard).
