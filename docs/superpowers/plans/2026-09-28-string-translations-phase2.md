# String Translations Phase 2 (Native-Only) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or
> superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for
> tracking.

**Goal:** Make all 101 `StringTranslationsMongoTest` tests run on the native (EF-side) LINQ path, so the class
passes under `MONGODB_EF_NATIVE_ONLY=1`. Today 49 pass natively and 52 pass only via driver-LINQ fallback.

**Architecture:** Additive recognizers in `MongoExpressionTranslator` (predicate arms in `TranslateNode`, value
arms in `TranslateOperand`), three new aggregation-only IR nodes (`MongoSubstringExpression`,
`MongoReplaceExpression`, `MongoStringCompareExpression`), two new `MongoRegexKind` members (`Exact`, `Pattern`),
a widened bare-projection allowlist in `NativeProjectionBinder`, and client-side re-application of
`ToLower`/`ToUpper` in projections, reusing the existing string-sequence-leaf mechanism. Every new node follows the
Phase 1 `MongoTrimExpression` precedent (all seven dispatchers + `MongoExpressionNodeCoverageTests`).

**Tech Stack:** C# / .NET, EF Core 10 query pipeline, MongoDB aggregation (`$substrCP`, `$replaceAll`, `$cmp`,
`$indexOfCP`, `$toString`, `$regexMatch`, `$literal`), xUnit, `EF_TEST_REWRITE_BASELINES`.

**Spec:** No separate design doc. The spec is the empirical scoping below plus the five decisions the user
approved on 2026-09-28 (see "Semantic decisions").

### Scoping data (measured 2026-09-28, EF10, `MONGODB_EF_NATIVE_ONLY=1`)

| Gap | Tests |
|---|---|
| `CompareTo` / `string.Compare` | `CompareTo_nested`, `CompareTo_simple_more_than_one`, `CompareTo_simple_one`, `CompareTo_simple_zero`, `CompareTo_with_parameter`, `Compare_multi_predicate`, `Compare_nested`, `Compare_simple_more_than_one`, `Compare_simple_one`, `Compare_simple_zero`, `Compare_to_multi_predicate`, `Compare_with_parameter` (12) |
| `Substring` | `Substring`, `Substring_with_one_arg_with_constant`, `Substring_with_one_arg_with_parameter`, `Substring_with_one_arg_with_zero_startIndex`, `Substring_with_two_args_with_IndexOf`, `Substring_with_two_args_with_parameter`, `Substring_with_two_args_with_zero_length`, `Substring_with_two_args_with_zero_startIndex` (8) |
| `IndexOf` overloads | `IndexOf_Char`, `IndexOf_with_one_parameter_arg_char`, `IndexOf_with_constant_starting_position`, `IndexOf_with_constant_starting_position_char`, `IndexOf_with_parameter_starting_position`, `IndexOf_with_parameter_starting_position_char`, `IndexOf_after_ToString`, `IndexOf_over_ToString` (8) |
| `char` args to `Contains`/`StartsWith`/`EndsWith` | `Contains_Literal_Char`, `StartsWith_Literal_Char`, `StartsWith_Parameter_Char`, `EndsWith_Literal_Char`, `EndsWith_Parameter_Char` (5) |
| `Replace` | `Replace`, `Replace_Char`, `Replace_using_property_arguments`, `Replace_with_empty_string` (4) |
| `Equals(…, StringComparison)` | `Equals_with_Ordinal`, `Equals_with_OrdinalIgnoreCase`, `Static_Equals_with_Ordinal`, `Static_Equals_with_OrdinalIgnoreCase` (4) |
| `string.Concat` method | `Concat_method_comparison`, `Concat_method_comparison_2`, `Concat_method_comparison_3` (3) |
| `IsNullOrEmpty` / `IsNullOrWhiteSpace` | `IsNullOrEmpty`, `IsNullOrEmpty_negated`, `IsNullOrWhiteSpace` (3) |
| `ToLower` / `ToUpper` | `ToLower`, `ToUpper` (2) |
| `Regex.IsMatch(field, pattern)` | `Regex_IsMatch` (1) |
| Bare bool `Select` | `Contains_Column`, `Contains_negated` (2) |

`Contains_Column`, `Contains_negated`, `IsNullOrEmpty`, `IsNullOrEmpty_negated`, `ToLower` and `ToUpper` each also
contain a bare `Select(...)` query; those halves are closed by Tasks 9 and 11.

Upstream test bodies: `https://github.com/dotnet/efcore/blob/v10.0.11/test/EFCore.Specification.Tests/Query/Translations/StringTranslationsTestBase.cs`
(fetch with `gh api -H "Accept: application/vnd.github.raw" "repos/dotnet/efcore/contents/test/EFCore.Specification.Tests/Query/Translations/StringTranslationsTestBase.cs?ref=v10.0.11"`).

### Semantic decisions (approved)

1. **`CompareTo` / `string.Compare` are translated as binary (ordinal) comparisons**, consistent with native
   string `==` and `OrderBy`. .NET's culture-sensitive ordering is not reproduced. The `StringComparison`/`bool
   ignoreCase` overloads of `string.Compare` decline (their `Ordinal` results are not limited to -1/0/1).
2. **`ToLower`/`ToUpper`:** in `Where`, `x.S.ToLower() == "const"` becomes an anchored case-insensitive regex, and
   folds to `false` when the constant isn't already in the target case. In `Select`, the raw string is projected
   and the method is re-applied client-side (exact .NET semantics). `$toLower`/`$toUpper` are never emitted
   (ASCII-only).
3. **`Equals(…, OrdinalIgnoreCase)`:** anchored regex with the `i` option against a constant/parameter;
   field-to-field declines (`$strcasecmp` is ASCII-only). Culture-sensitive `StringComparison` values decline.
4. **`Substring`/`IndexOf` use code points** (`$substrCP`/`$indexOfCP`), consistent with native `Length`
   (`$strLenCP`). Astral characters differ from .NET's UTF-16 indexing; accepted.
5. **`ToString()` only for integral receivers** (`int`, `long`, `short`, `byte`), zero-arg only.

## Global Constraints

- Branch: `EF-322-Native-LINQ-rebased`. Commit messages: `EF-322: <description>`.
- `StringTranslationsMongoTest` is EF10-only (whole file is `#if !EF8 && !EF9`). The same scenarios exist in
  `NorthwindFunctionsQueryMongoTest` for EF8/EF9; Task 12 handles their baseline fallout.
- **Never hand-write an `AssertMql` baseline.** Regenerate:
  `EF_TEST_REWRITE_BASELINES=1 dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj -c "Debug EF10" --no-build --filter "FullyQualifiedName~StringTranslationsMongoTest.<Method>"`,
  then `git diff`, delete any stray `*.cs.tmp` the rewriter leaves, rebuild, rerun without the var. If the
  rewriter can't place a baseline (it fails inside `#if` blocks), copy the "Actual" MQL from the failure message.
- **A test is "flipped" only when it passes both normally and under `MONGODB_EF_NATIVE_ONLY=1`.** Every task ends by
  running its tests both ways.
- **Every new `MongoExpression` subtype needs all seven dispatchers** (`Query/AGENTS.md`):
  `MongoAggregationExpressionRenderer.Render` + `CanRender`, `MongoExpressionTranslator.AllFieldsDefaultSerialized`,
  `MongoQueryLanguageRenderer.IsQueryDialectRenderable` (explicit `=> false`), `MongoFieldPrefixRewriter.Rewrite`,
  `MongoExpressionNegator` (catch-all decline is fine, but the coverage row must say `false`), and a sample + expected
  rows in `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoExpressionNodeCoverageTests.cs`.
  New value nodes also go in `NativeProjectionBinder`'s three allowlists (`TryTranslateLeaf`'s `value is ...` list,
  `TryDeriveSyntheticAlias`, `IsArrayFreeComputedSubtree`).
- **String constants/parameters inside new aggregation operators are `$literal`-wrapped** via
  `MongoAggregationExpressionRenderer.RenderBranch`. An unwrapped `"$..."` string is read as a field path.
- Receivers must resolve through `TryTranslateValue`/`TryTranslateField`, never assume default serialization.
  New value nodes add an `AllFieldsDefaultSerialized` arm covering every operand.
- After any `src/` change: `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"` (full, not `--no-build`).
- Run commands from the repo root. Unit tests: `tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj`.
  Functional: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj`.
  Spec: `tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj`.
  Leave `MONGODB_URI`/`ATLAS_URI` unset (Docker test containers).

## Review Focus

- **`$`-prefixed string constants in new aggregation operands** (`$indexOfCP` needle, `$substrCP`,
  `$replaceAll` find/replacement, `$cmp`): `"$String"` as a literal must not read field `String`. Pinned in
  Tasks 2, 4, 5 and 10 by render tests asserting `{ "$literal" : "$String" }`.
- **Nullable receivers for `CompareTo`/`string.Compare`:** .NET orders `null` before every string
  (`string.Compare(null, "a") == -1`), but a folded query-dialect `{ S: { $lt: "a" } }` never matches null. Only
  fold when the receiver is a non-nullable property and the argument a non-null constant; otherwise use `$cmp`.
  Pinned in Task 10.
- **`char` parameters that are regex metacharacters** (`'.'`, `'$'`, `'^'`): must be stringified and escaped per
  execution, so `EndsWith(dot)` matches a literal `.`. Pinned in Task 1.
- **`x.S.ToLower() == "Seattle"`** (constant not already lowercase) must return no rows, not case-insensitive
  matches. Pinned in Task 11.
- **Missing vs. null field in a bare-bool projection** (`Select(n => string.IsNullOrEmpty(n.S))`): .NET sees
  `null` for both, but aggregation `$eq: [ "$S", null ]` is false for a missing field. Pinned in Task 9 with a
  document that lacks the field.

---

## Task 1: `char` arguments to `StartsWith`/`EndsWith`/`Contains` and `char` placeholders

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.MethodCalls.cs` (`TryMatchRegexMethod`)
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.cs` (regex arm of `TranslateNode`, ~line 771)
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoPipelineFactory.cs` (`SerializeParameter`, ~lines 741 and 747)
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoExpressionTranslatorCharArgumentTests.cs` (create)
- Test: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeStringCharArgumentTests.cs` (create)
- Test: `tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/Translations/StringTranslationsMongoTest.cs` (baselines)

**Interfaces:**
- Produces: `MongoExpressionTranslator.TranslateCharAsString(Expression node) : MongoExpression?`. For a `char`
  constant it returns `MongoConstantExpression(ch.ToString(), forSerialization: null)`, for a `char` query
  parameter `MongoParameterExpression(name, forSerialization: null)`, otherwise `null`. Tasks 2 and 5 reuse it.
- Produces: `MongoPipelineFactory.SerializeParameter` renders a `char` raw value as a one-character `BsonString`
  (regex placeholders and serializer-less value placeholders).

- [ ] **Step 1: Write the failing unit tests**

```csharp
// tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoExpressionTranslatorCharArgumentTests.cs
// (license header as in MongoExpressionTranslatorTrimTests.cs)
using System;
using System.Linq.Expressions;
using MongoDB.EntityFrameworkCore.Query.Expressions;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;
using MongoDB.EntityFrameworkCore.UnitTests.TestUtilities;
using Xunit;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query.NativeTranslation;

public class MongoExpressionTranslatorCharArgumentTests
{
    private class Widget
    {
        public int Id { get; set; }
        public string Text { get; set; } = "";
    }

    private static MongoExpressionTranslator BuildTranslator()
    {
        using var db = SingleEntityDbContext.Create<Widget>();
        return new MongoExpressionTranslator(db.Model.FindEntityType(typeof(Widget))!);
    }

    [Theory]
    [InlineData("StartsWith", MongoRegexKind.StartsWith)]
    [InlineData("EndsWith", MongoRegexKind.EndsWith)]
    [InlineData("Contains", MongoRegexKind.Contains)]
    public void Char_constant_argument_becomes_a_one_char_string_term(string method, MongoRegexKind kind)
    {
        var w = Expression.Parameter(typeof(Widget), "w");
        var body = Expression.Call(
            Expression.Property(w, nameof(Widget.Text)),
            typeof(string).GetMethod(method, [typeof(char)])!,
            Expression.Constant('e'));

        Assert.True(BuildTranslator().TryTranslate(body, out var result));
        var regex = Assert.IsType<MongoRegexExpression>(result);
        Assert.Equal(kind, regex.Kind);
        Assert.Equal("e", Assert.IsType<MongoConstantExpression>(regex.Term).Value);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj -c "Debug EF10" --filter "FullyQualifiedName~MongoExpressionTranslatorCharArgumentTests"`
Expected: FAIL (`TryTranslate` returns false).

- [ ] **Step 3: Accept `char` in `TryMatchRegexMethod`**

In `MongoExpressionTranslator.MethodCalls.cs`, change the argument switch:

```csharp
        switch (call.Arguments.Count)
        {
            case 1 when call.Arguments[0].Type == typeof(string) || call.Arguments[0].Type == typeof(char):
                break;
```

Update the method's `<summary>` to say "`(string)` or `(char)`".

- [ ] **Step 4: Add `TranslateCharAsString` and use it for `char` terms**

Add to `MongoExpressionTranslator.MethodCalls.cs`:

```csharp
    /// <summary>
    /// A <see langword="char"/> constant or query parameter as a one-character string operand. BSON has no char
    /// type and the default char serialization is Int32, so string operators need the string form.
    /// </summary>
    private static MongoExpression? TranslateCharAsString(Expression node)
    {
        if (node is ConstantExpression { Value: char ch })
            return new MongoConstantExpression(ch.ToString(), forSerialization: null);

        if (node.Type == typeof(char) && NativeQueryParameter.TryGetQueryParameterName(node, out var name))
            return new MongoParameterExpression(name, forSerialization: null);

        return null;
    }
```

In `MongoExpressionTranslator.cs`, regex arm (`case MethodCallExpression call when TryMatchRegexMethod(...)`),
replace `var termNode = TranslateValue(Unwrap(termExpr), property);` with:

```csharp
                var termNode = termExpr.Type == typeof(char)
                    ? TranslateCharAsString(Unwrap(termExpr))
                    : TranslateValue(Unwrap(termExpr), property);
                if (termNode is null && termExpr.Type == typeof(char))
                    return null; // a computed char (e.g. c.Text[0]) has no regex-term form
```

- [ ] **Step 5: Stringify `char` in `SerializeParameter`**

In `MongoPipelineFactory.SerializeParameter`:

```csharp
        if (regexKind is not null)
        {
            var term = rawValue is char ch ? ch.ToString() : (string)rawValue!;
            var pattern = MongoRegexPatternBuilder.BuildPattern(term, regexKind.Value);
            return new BsonRegularExpression(pattern, regexCaseInsensitive ? "is" : "s");
        }

        // Property-less primitive (e.g. Skip/Take count): serialize via BsonValue.Create. A char has no BSON form;
        // it only reaches here as a string-operator operand (TranslateCharAsString), so it is a one-char string.
        if (serializer is null)
            return rawValue is char c ? new BsonString(c.ToString()) : BsonValue.Create(rawValue);
```

- [ ] **Step 6: Run unit tests**

Build EF10, then rerun Step 2's command. Expected: PASS.

- [ ] **Step 7: Functional test for parameterized metacharacters**

```csharp
// tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeStringCharArgumentTests.cs
// (license header and usings as in NativeStringJoinDollarSeparatorTests.cs)
namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// A <c>char</c> argument to StartsWith/EndsWith/Contains is a one-character literal, escaped per execution when
/// parameterized, so a regex metacharacter like <c>'.'</c> matches only itself.
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeStringCharArgumentTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    public class Row
    {
        public ObjectId Id { get; set; }
        public string Label { get; set; } = "";
        public string Text { get; set; } = "";
    }

    [Fact]
    public void Parameterized_metacharacter_char_matches_literally()
    {
        using var context = CreateSeededContext(nameof(Parameterized_metacharacter_char_matches_literally));

        var dot = '.';
        var endsWithDot = context.Entities.AsNoTracking().Where(x => x.Text.EndsWith(dot)).Select(x => x.Label).ToList();
        var containsDollar = context.Entities.AsNoTracking().Where(x => x.Text.Contains('$')).Select(x => x.Label).ToList();

        Assert.Equal(["dot"], endsWithDot);
        Assert.Equal(["dollar"], containsDollar);
    }

    private SingleEntityDbContext<Row> CreateSeededContext(string testName)
    {
        var collection = database.MongoDatabase.GetCollection<Row>(
            TemporaryDatabaseFixtureBase.CreateCollectionName(testName) + Guid.NewGuid().ToString("N")[..8]);
        collection.InsertMany(
        [
            new Row { Label = "dot", Text = "end." },
            new Row { Label = "any", Text = "endX" },
            new Row { Label = "dollar", Text = "a$b" }
        ]);

        return SingleEntityDbContext.Create(
            collection,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(MongoQueryMode.NativeOnly);
            });
    }
}
```

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj -c "Debug EF10" --filter "FullyQualifiedName~NativeStringCharArgumentTests"`
Expected: PASS. (Without Step 5 the parameter case throws `InvalidCastException`; without escaping, `"endX"`
would match `.`.)

- [ ] **Step 8: Regenerate spec baselines and verify native**

For each of `Contains_Literal_Char`, `StartsWith_Literal_Char`, `StartsWith_Parameter_Char`,
`EndsWith_Literal_Char`, `EndsWith_Parameter_Char`: regenerate per Global Constraints, then run
`MONGODB_EF_NATIVE_ONLY=1 dotnet test <spec csproj> -c "Debug EF10" --no-build --filter "FullyQualifiedName~StringTranslationsMongoTest&(FullyQualifiedName~_Char)"`.
Expected: the five tests PASS both ways. (The MQL is likely unchanged from the driver's.)

- [ ] **Step 9: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/NativeTranslation tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoExpressionTranslatorCharArgumentTests.cs tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeStringCharArgumentTests.cs tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/Translations/StringTranslationsMongoTest.cs
git commit -m "EF-322: native char arguments to StartsWith/EndsWith/Contains"
```

---

## Task 2: `IndexOf(char)` and `IndexOf(value, startIndex)`

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoStringIndexOfExpression.cs` (add optional `Start`)
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.MethodCalls.cs` (`TryMatchIndexOfMethod`)
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.cs` (`TranslateOperand` IndexOf arm ~line 1466; `AllFieldsDefaultSerialized` ~line 480)
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoAggregationExpressionRenderer.cs` (Render ~line 114, CanRender ~line 190)
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoFieldPrefixRewriter.cs` (~line 89)
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoExpressionTranslatorIndexOfTests.cs` (create)
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoAggregationExpressionRendererTests.cs` (add a render test)

**Interfaces:**
- Consumes: `TranslateCharAsString` (Task 1).
- Produces: `MongoStringIndexOfExpression(MongoExpression haystack, MongoExpression needle, MongoExpression? start = null)`
  with `Start` property; renders `{ $indexOfCP: [h, n] }` or `{ $indexOfCP: [h, n, start] }`.

- [ ] **Step 1: Write failing tests**

```csharp
// MongoExpressionTranslatorIndexOfTests.cs (header/usings/Widget/BuildTranslator as in Task 1's test file)
public class MongoExpressionTranslatorIndexOfTests
{
    // ... Widget + BuildTranslator as in MongoExpressionTranslatorCharArgumentTests ...

    [Fact]
    public void IndexOf_char_translates_with_a_one_char_string_needle()
    {
        Expression<Func<Widget, int>> selector = w => w.Text.IndexOf('e');

        Assert.True(BuildTranslator().TryTranslateValue(selector.Body, out var result));
        var indexOf = Assert.IsType<MongoStringIndexOfExpression>(result);
        Assert.Equal("e", Assert.IsType<MongoConstantExpression>(indexOf.Needle).Value);
        Assert.Null(indexOf.Start);
    }

    [Fact]
    public void IndexOf_with_start_index_carries_the_start_operand()
    {
        Expression<Func<Widget, int>> selector = w => w.Text.IndexOf("e", 2);

        Assert.True(BuildTranslator().TryTranslateValue(selector.Body, out var result));
        var indexOf = Assert.IsType<MongoStringIndexOfExpression>(result);
        Assert.Equal(2, Assert.IsType<MongoConstantExpression>(indexOf.Start).Value);
    }

    [Fact]
    public void IndexOf_with_StringComparison_declines()
    {
        Expression<Func<Widget, int>> selector = w => w.Text.IndexOf("e", StringComparison.OrdinalIgnoreCase);

        Assert.False(BuildTranslator().TryTranslateValue(selector.Body, out _));
    }
}
```

In `MongoAggregationExpressionRendererTests.cs`, follow that file's existing render-helper pattern (read the file
first) to assert that
`new MongoStringIndexOfExpression(field, new MongoConstantExpression("$Text", null), new MongoConstantExpression(2, null))`
renders `{ "$indexOfCP" : ["$Text", { "$literal" : "$Text" }, 2] }`.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test <unit csproj> -c "Debug EF10" --filter "FullyQualifiedName~MongoExpressionTranslatorIndexOfTests|FullyQualifiedName~MongoAggregationExpressionRendererTests"`
Expected: compile error (`Start` doesn't exist), then FAIL.

- [ ] **Step 3: Extend the node**

```csharp
internal sealed class MongoStringIndexOfExpression(
    MongoExpression haystack, MongoExpression needle, MongoExpression? start = null) : MongoExpression
{
    public MongoExpression Haystack { get; } = haystack;
    public MongoExpression Needle { get; } = needle;

    /// <summary>Code-point start index (<c>IndexOf(value, startIndex)</c>), or <see langword="null"/>.</summary>
    public MongoExpression? Start { get; } = start;

    public override Type Type => typeof(int);
}
```

(Keep the file's existing doc comments; match its existing constructor style if it isn't a primary constructor.)

- [ ] **Step 4: Widen the matcher**

Replace `TryMatchIndexOfMethod` with:

```csharp
    /// <summary>
    /// Recognizes <c>string.IndexOf(string|char)</c> and <c>IndexOf(string|char, int startIndex)</c> (<c>$indexOfCP</c>,
    /// code points). <see cref="StringComparison"/> and count overloads fall through.
    /// </summary>
    private static bool TryMatchIndexOfMethod(
        Expression node,
        [NotNullWhen(true)] out Expression? receiver,
        [NotNullWhen(true)] out Expression? term,
        out Expression? start)
    {
        receiver = null;
        term = null;
        start = null;

        if (node is not MethodCallExpression call || call.Method.IsStatic || call.Object is null
            || call.Object.Type != typeof(string) || call.Method.Name != nameof(string.IndexOf))
            return false;

        if (call.Arguments.Count is < 1 or > 2
            || (call.Arguments[0].Type != typeof(string) && call.Arguments[0].Type != typeof(char))
            || (call.Arguments.Count == 2 && call.Arguments[1].Type != typeof(int)))
            return false;

        receiver = call.Object;
        term = call.Arguments[0];
        start = call.Arguments.Count == 2 ? call.Arguments[1] : null;
        return true;
    }
```

Update every caller (`grep -n TryMatchIndexOfMethod src`) to pass `out var start` (discard with `out _` where the
start isn't used, but a caller that ignores a non-null start must decline).

- [ ] **Step 5: Translate, render, rewrite, serialize-check**

`TranslateOperand` arm:

```csharp
        if (TryMatchIndexOfMethod(node, out var indexOfReceiver, out var indexOfTerm, out var indexOfStart))
        {
            var haystack = TranslateOperand(indexOfReceiver, allowNumericWidening);
            var needle = haystack is null ? null
                : indexOfTerm.Type == typeof(char) ? TranslateCharAsString(indexOfTerm)
                : TranslateOperand(indexOfTerm, allowNumericWidening);
            MongoExpression? start = null;
            if (needle is not null && indexOfStart is not null)
            {
                start = TranslateOperand(indexOfStart, allowNumericWidening);
                if (start is null)
                    return null;
            }

            return haystack is not null && needle is not null
                ? new MongoStringIndexOfExpression(haystack, needle, start)
                : null;
        }
```

Renderer `Render` arm (string operands `$literal`-wrapped):

```csharp
            MongoStringIndexOfExpression indexOf
                => new BsonDocument("$indexOfCP", indexOf.Start is null
                    ? new BsonArray
                    {
                        Render(indexOf.Haystack, placeholders, elementVariable),
                        RenderBranch(indexOf.Needle, placeholders, elementVariable)
                    }
                    : new BsonArray
                    {
                        Render(indexOf.Haystack, placeholders, elementVariable),
                        RenderBranch(indexOf.Needle, placeholders, elementVariable),
                        Render(indexOf.Start, placeholders, elementVariable)
                    }),
```

`CanRender`: `MongoStringIndexOfExpression indexOf => CanRender(indexOf.Haystack) && CanRender(indexOf.Needle) && (indexOf.Start is null || CanRender(indexOf.Start)),`

`AllFieldsDefaultSerialized`: add `&& (indexOf.Start is null || AllFieldsDefaultSerialized(indexOf.Start))`.

`MongoFieldPrefixRewriter`:
`MongoStringIndexOfExpression io => new MongoStringIndexOfExpression(Rewrite(io.Haystack, prefix), Rewrite(io.Needle, prefix), io.Start is null ? null : Rewrite(io.Start, prefix)),`

- [ ] **Step 6: Run unit tests plus the node-coverage suite**

Run: `dotnet test <unit csproj> -c "Debug EF10" --filter "FullyQualifiedName~IndexOf|FullyQualifiedName~MongoExpressionNodeCoverageTests|FullyQualifiedName~MongoAggregationExpressionRendererTests"`
Expected: PASS. Some existing IndexOf MQL baselines elsewhere may now show `$literal`; they are regenerated in Task 12.

- [ ] **Step 7: Spec baselines + native verification**

Regenerate and verify (normal + `MONGODB_EF_NATIVE_ONLY=1`): `IndexOf_Char`, `IndexOf_with_one_parameter_arg_char`,
`IndexOf_with_constant_starting_position`, `IndexOf_with_constant_starting_position_char`,
`IndexOf_with_parameter_starting_position`, `IndexOf_with_parameter_starting_position_char`. Also rerun the
existing already-native IndexOf tests (`--filter "FullyQualifiedName~StringTranslationsMongoTest.IndexOf"`)
and regenerate any baseline whose needle became `$literal`-wrapped.

- [ ] **Step 8: Commit** — `git commit -am "EF-322: native IndexOf(char) and IndexOf(value, startIndex)"` (add new test files first).

---

## Task 3: Integral `ToString()` and the `string.Concat` method

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.MethodCalls.cs` (two new helpers)
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.cs` (`TranslateOperand`, before the `TryResolveMember` arm)
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoExpressionTranslatorToStringConcatTests.cs` (create)

**Interfaces:**
- Consumes: existing `MongoConvertExpression(operand, typeof(string))` (renders `$toString`) and
  `TranslateConcatOperand` / `MongoConcatExpression`.
- Produces: value translations used by Tasks 5 and 10 (`c.Int.ToString()` inside `Replace`).

- [ ] **Step 1: Failing tests**

```csharp
public class MongoExpressionTranslatorToStringConcatTests
{
    private class Widget
    {
        public int Id { get; set; }
        public string Text { get; set; } = "";
        public int Count { get; set; }
        public double Ratio { get; set; }
    }

    private static MongoExpressionTranslator BuildTranslator()
    {
        using var db = SingleEntityDbContext.Create<Widget>();
        return new MongoExpressionTranslator(db.Model.FindEntityType(typeof(Widget))!);
    }

    [Fact]
    public void Int_ToString_translates_to_toString_convert()
    {
        Expression<Func<Widget, string>> selector = w => w.Count.ToString();

        Assert.True(BuildTranslator().TryTranslateValue(selector.Body, out var result));
        var convert = Assert.IsType<MongoConvertExpression>(result);
        Assert.Equal(typeof(string), convert.Type);
        Assert.IsType<MongoFieldExpression>(convert.Operand);
    }

    [Fact]
    public void Double_ToString_declines()
    {
        Expression<Func<Widget, string>> selector = w => w.Ratio.ToString();

        Assert.False(BuildTranslator().TryTranslateValue(selector.Body, out _));
    }

    [Fact]
    public void Int_ToString_with_format_declines()
    {
        Expression<Func<Widget, string>> selector = w => w.Count.ToString("D4");

        Assert.False(BuildTranslator().TryTranslateValue(selector.Body, out _));
    }

    [Fact]
    public void String_Concat_method_flattens_to_one_concat()
    {
        Expression<Func<Widget, string>> selector = w => string.Concat("A", "B", w.Text);

        Assert.True(BuildTranslator().TryTranslateValue(selector.Body, out var result));
        var concat = Assert.IsType<MongoConcatExpression>(result);
        Assert.Equal(3, concat.Operands.Count);
    }
}
```

- [ ] **Step 2: Run to verify failure** — filter `FullyQualifiedName~MongoExpressionTranslatorToStringConcatTests`. Expected: FAIL.

- [ ] **Step 3: Implement**

In `MongoExpressionTranslator.MethodCalls.cs`:

```csharp
    /// <summary>
    /// Zero-arg <c>ToString()</c> on an integral receiver, whose <c>$toString</c> output matches .NET's invariant
    /// integer formatting. Floating-point, decimal, bool and date receivers decline: <c>$toString</c> formats them
    /// differently from .NET.
    /// </summary>
    private static bool TryMatchIntegralToString(Expression node, [NotNullWhen(true)] out Expression? receiver)
    {
        receiver = null;
        if (node is not MethodCallExpression { Method.Name: nameof(ToString), Object: { } obj, Arguments.Count: 0 })
            return false;

        var type = obj.Type;
        if (type != typeof(int) && type != typeof(long) && type != typeof(short) && type != typeof(byte))
            return false;

        receiver = obj;
        return true;
    }

    /// <summary>
    /// <c>string.Concat(string, string[, string[, string]])</c>: same semantics as the <c>+</c> operator, so the
    /// operands go through <see cref="TranslateConcatOperand"/>. Object/array/span overloads decline.
    /// </summary>
    private MongoExpression? TryTranslateStringConcatMethod(Expression node, bool allowNumericWidening)
    {
        if (node is not MethodCallExpression { Method.IsStatic: true } call
            || call.Method.DeclaringType != typeof(string)
            || call.Method.Name != nameof(string.Concat)
            || call.Arguments.Count is < 2 or > 4
            || call.Method.GetParameters().Any(p => p.ParameterType != typeof(string)))
            return null;

        var operands = new List<MongoExpression>();
        foreach (var argument in call.Arguments)
        {
            var operand = TranslateConcatOperand(argument, allowNumericWidening);
            if (operand is null)
                return null;

            operands.AddRange(operand is MongoConcatExpression nested ? nested.Operands : [operand]);
        }

        return new MongoConcatExpression(operands);
    }
```

In `TranslateOperand`, immediately before `if (TryResolveMember(node, ...))`:

```csharp
        // Integral x.ToString() → $toString (see TryMatchIntegralToString for the admissible receivers).
        if (TryMatchIntegralToString(node, out var toStringReceiver))
        {
            var toStringOperand = TranslateOperand(toStringReceiver, allowNumericWidening);
            return toStringOperand is null || !AllFieldsDefaultSerialized(toStringOperand)
                ? null
                : new MongoConvertExpression(toStringOperand, typeof(string));
        }

        // string.Concat(a, b[, c[, d]]) — the method spelling of `+`.
        if (TryTranslateStringConcatMethod(node, allowNumericWidening) is { } concatMethod)
            return concatMethod;
```

- [ ] **Step 4: Run unit tests** — Expected: PASS.

- [ ] **Step 5: Spec baselines + native verification** for `Concat_method_comparison`, `Concat_method_comparison_2`,
`Concat_method_comparison_3`, `IndexOf_after_ToString`, `IndexOf_over_ToString`.

- [ ] **Step 6: Commit** — `EF-322: native integral ToString() and string.Concat method`.

---

## Task 4: `Substring(startIndex[, length])`

**Files:**
- Create: `src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoSubstringExpression.cs`
- Modify: `MongoExpressionTranslator.MethodCalls.cs` (matcher), `MongoExpressionTranslator.cs` (`TranslateOperand`,
  `AllFieldsDefaultSerialized`), `MongoAggregationExpressionRenderer.cs` (Render, CanRender), `MongoQueryLanguageRenderer.cs`
  (`IsQueryDialectRenderable` explicit `=> false`), `MongoFieldPrefixRewriter.cs`, `NativeProjectionBinder.cs` (three allowlists)
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoExpressionTranslatorSubstringTests.cs` (create)
- Test: `MongoExpressionNodeCoverageTests.cs` (sample + 8 expected rows), `MongoAggregationExpressionRendererTests.cs`

**Interfaces:**
- Produces: `MongoSubstringExpression(MongoExpression source, MongoExpression start, MongoExpression? length)`,
  `Type == typeof(string)`. Renders `{ $substrCP: [source, start, length] }`; with no length,
  `length = { $subtract: [ { $strLenCP: source }, start ] }`. Task 10 uses it inside `CompareTo`.

- [ ] **Step 1: Failing tests**

```csharp
public class MongoExpressionTranslatorSubstringTests
{
    // Widget { int Id; string Text } + BuildTranslator as in Task 1

    [Fact]
    public void Substring_two_args_translates()
    {
        Expression<Func<Widget, string>> selector = w => w.Text.Substring(1, 2);

        Assert.True(BuildTranslator().TryTranslateValue(selector.Body, out var result));
        var substring = Assert.IsType<MongoSubstringExpression>(result);
        Assert.Equal(1, Assert.IsType<MongoConstantExpression>(substring.Start).Value);
        Assert.Equal(2, Assert.IsType<MongoConstantExpression>(substring.Length).Value);
    }

    [Fact]
    public void Substring_one_arg_has_null_length()
    {
        Expression<Func<Widget, string>> selector = w => w.Text.Substring(1);

        Assert.True(BuildTranslator().TryTranslateValue(selector.Body, out var result));
        Assert.Null(Assert.IsType<MongoSubstringExpression>(result).Length);
    }
}
```

Render test (in `MongoAggregationExpressionRendererTests.cs`, using that file's helper): one-arg form renders
`{ "$substrCP" : ["$Text", 1, { "$subtract" : [{ "$strLenCP" : "$Text" }, 1] }] }`.

Coverage test: add sample `new MongoSubstringExpression(headingField, new MongoConstantExpression(1, null), length: null)`
and rows (copy the `MongoTrimExpression` block's values exactly: `Agg.CanRender=true`, `Agg.Render=rendered`,
`AllFieldsDefaultSerialized=true`, `(converted)=false`, `Negator.TryNegate=false`, `PrefixRewriter.Rewrite=rendered`,
`QL.IsQueryDialectRenderable=false`, `QL.Render=rendered`).

- [ ] **Step 2: Run to verify failure** — compile error, then FAIL.

- [ ] **Step 3: Create the node**

```csharp
// MongoSubstringExpression.cs (license header)
using System;

namespace MongoDB.EntityFrameworkCore.Query.Expressions;

/// <summary>
/// <c>string.Substring(start[, length])</c> as <c>$substrCP</c> (code points, like <c>$strLenCP</c> for
/// <c>Length</c>). Aggregation dialect only. Out-of-range arguments return a truncated/empty string rather than
/// throwing as .NET does.
/// </summary>
internal sealed class MongoSubstringExpression(MongoExpression source, MongoExpression start, MongoExpression? length)
    : MongoExpression
{
    public MongoExpression Source { get; } = source;
    public MongoExpression Start { get; } = start;

    /// <summary>The length, or <see langword="null"/> for "to the end" (rendered from <c>$strLenCP</c>).</summary>
    public MongoExpression? Length { get; } = length;

    public override Type Type => typeof(string);
}
```

- [ ] **Step 4: Matcher + translation**

```csharp
    // MethodCalls.cs
    private static bool TryMatchSubstring(
        Expression node, [NotNullWhen(true)] out Expression? receiver, [NotNullWhen(true)] out Expression? start,
        out Expression? length)
    {
        receiver = start = length = null;
        if (node is not MethodCallExpression { Method.Name: nameof(string.Substring), Object: { } obj } call
            || obj.Type != typeof(string) || call.Arguments.Count is < 1 or > 2)
            return false;

        receiver = obj;
        start = call.Arguments[0];
        length = call.Arguments.Count == 2 ? call.Arguments[1] : null;
        return true;
    }
```

`TranslateOperand` (next to the Trim arm):

```csharp
        if (TryMatchSubstring(node, out var substringReceiver, out var substringStart, out var substringLength))
        {
            var source = TranslateOperand(substringReceiver, allowNumericWidening);
            var start = source is null ? null : TranslateOperand(substringStart, allowNumericWidening);
            if (start is null)
                return null;

            MongoExpression? length = null;
            if (substringLength is not null && (length = TranslateOperand(substringLength, allowNumericWidening)) is null)
                return null;

            return new MongoSubstringExpression(source!, start, length);
        }
```

- [ ] **Step 5: The seven dispatchers + allowlists**

- Renderer `Render`:
  ```csharp
            MongoSubstringExpression substring => RenderSubstring(substring, placeholders, elementVariable),
  ```
  ```csharp
    private static BsonValue RenderSubstring(MongoSubstringExpression node, PlaceholderTable placeholders, string? elementVariable)
    {
        var source = RenderBranch(node.Source, placeholders, elementVariable);
        var start = Render(node.Start, placeholders, elementVariable);
        var length = node.Length is not null
            ? Render(node.Length, placeholders, elementVariable)
            : new BsonDocument("$subtract", new BsonArray { new BsonDocument("$strLenCP", source), start });
        return new BsonDocument("$substrCP", new BsonArray { source, start, length });
    }
  ```
- `CanRender`: `MongoSubstringExpression s => CanRender(s.Source) && CanRender(s.Start) && (s.Length is null || CanRender(s.Length)),`
- `AllFieldsDefaultSerialized`: `MongoSubstringExpression s => AllFieldsDefaultSerialized(s.Source) && AllFieldsDefaultSerialized(s.Start) && (s.Length is null || AllFieldsDefaultSerialized(s.Length)),`
- `IsQueryDialectRenderable`: `MongoSubstringExpression => false,`
- Prefix rewriter: `MongoSubstringExpression s => new MongoSubstringExpression(Rewrite(s.Source, prefix), Rewrite(s.Start, prefix), s.Length is null ? null : Rewrite(s.Length, prefix)),`
- `NativeProjectionBinder`: add `or MongoSubstringExpression` to the `value is ...` list (~line 656) and to gate 1c3
  (~line 1381); in `IsArrayFreeComputedSubtree` add
  `MongoSubstringExpression s => IsArrayFreeComputedSubtree(s.Source) && IsArrayFreeComputedSubtree(s.Start) && (s.Length is null || IsArrayFreeComputedSubtree(s.Length)),`

- [ ] **Step 6: Run unit tests** — filter `Substring|MongoExpressionNodeCoverageTests|MongoAggregationExpressionRendererTests`. Expected: PASS.

- [ ] **Step 7: Spec baselines + native verification** for the 8 `Substring*` tests.

- [ ] **Step 8: Commit** — `EF-322: native string.Substring`.

---

## Task 5: `Replace(string, string)` and `Replace(char, char)`

**Files:**
- Create: `src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoReplaceExpression.cs`
- Modify: same dispatcher set as Task 4
- Test: `MongoExpressionTranslatorReplaceTests.cs` (create), coverage + renderer tests
- Test: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeStringReplaceTests.cs` (create)

**Interfaces:**
- Consumes: `TranslateCharAsString` (Task 1), integral `ToString` (Task 3).
- Produces: `MongoReplaceExpression(MongoExpression input, MongoExpression find, MongoExpression replacement)`,
  `Type == typeof(string)`. Renders
  `{ $replaceAll: { input, find, replacement: { $ifNull: [replacement, ""] } } }`, with string constants/parameters
  `$literal`-wrapped.

- [ ] **Step 1: Verify server semantics first (record results in the node's doc comment)**

Add a temporary functional test that runs, against the test container,
`db.coll.aggregate([{ $project: { a: { $replaceAll: { input: "abc", find: "", replacement: "X" } }, b: { $replaceAll: { input: "abc", find: "b", replacement: null } } } }])`
through `database.MongoDatabase.GetCollection<BsonDocument>(...).Aggregate<BsonDocument>(...)`, and print the
results. .NET: `"abc".Replace("", "X")` throws `ArgumentException`; `"abc".Replace("b", null)` returns `"ac"`.
Expected: the null replacement yields `null` on the server, hence the `$ifNull` wrap. Whatever `find: ""` does,
.NET throws there, so any server result is acceptable; record it in the doc comment. Delete the temporary test.

- [ ] **Step 2: Failing tests**

```csharp
public class MongoExpressionTranslatorReplaceTests
{
    // Widget { int Id; string Text; int Count } + BuildTranslator as in Task 3

    [Fact]
    public void Replace_strings_translates()
    {
        Expression<Func<Widget, string>> selector = w => w.Text.Replace("Sea", "Rea");

        Assert.True(BuildTranslator().TryTranslateValue(selector.Body, out var result));
        var replace = Assert.IsType<MongoReplaceExpression>(result);
        Assert.Equal("Sea", Assert.IsType<MongoConstantExpression>(replace.Find).Value);
    }

    [Fact]
    public void Replace_chars_translates_to_one_char_strings()
    {
        Expression<Func<Widget, string>> selector = w => w.Text.Replace('S', 'R');

        Assert.True(BuildTranslator().TryTranslateValue(selector.Body, out var result));
        var replace = Assert.IsType<MongoReplaceExpression>(result);
        Assert.Equal("R", Assert.IsType<MongoConstantExpression>(replace.Replacement).Value);
    }

    [Fact]
    public void Replace_with_StringComparison_declines()
    {
        Expression<Func<Widget, string>> selector = w => w.Text.Replace("a", "b", StringComparison.OrdinalIgnoreCase);

        Assert.False(BuildTranslator().TryTranslateValue(selector.Body, out _));
    }
}
```

Render test: `new MongoReplaceExpression(textField, new MongoConstantExpression("$Text", null), new MongoConstantExpression("x", null))`
renders `{ "$replaceAll" : { "input" : "$Text", "find" : { "$literal" : "$Text" }, "replacement" : { "$ifNull" : [{ "$literal" : "x" }, ""] } } }`.

Functional test `NativeStringReplaceTests` (pattern from Task 1's functional test): rows `{ Text = "Seattle" }`,
`{ Text = "$Text" }`. Assert under `NativeOnly` that
`Where(x => x.Text.Replace("$Text", "hit") == "hit")` returns only the `"$Text"` row, and that
`Where(x => x.Text.Replace("Sea", null!) == "ttle")` returns the Seattle row (.NET treats a null replacement as "").

- [ ] **Step 3: Run to verify failure.**

- [ ] **Step 4: Node, matcher, translation**

```csharp
// MongoReplaceExpression.cs
/// <summary>
/// <c>string.Replace(oldValue, newValue)</c> / <c>Replace(oldChar, newChar)</c> as <c>$replaceAll</c> (ordinal,
/// all occurrences). A null <see cref="Replacement"/> means "" (.NET), rendered via <c>$ifNull</c>. An empty
/// <see cref="Find"/> throws in .NET; the server returns <c>(fill in from Step 1)</c>.
/// </summary>
internal sealed class MongoReplaceExpression(MongoExpression input, MongoExpression find, MongoExpression replacement)
    : MongoExpression
{
    public MongoExpression Input { get; } = input;
    public MongoExpression Find { get; } = find;
    public MongoExpression Replacement { get; } = replacement;
    public override Type Type => typeof(string);
}
```

```csharp
    // MethodCalls.cs
    private static bool TryMatchReplace(
        Expression node, [NotNullWhen(true)] out Expression? receiver, [NotNullWhen(true)] out Expression? find,
        [NotNullWhen(true)] out Expression? replacement)
    {
        receiver = find = replacement = null;
        if (node is not MethodCallExpression { Method.Name: nameof(string.Replace), Object: { } obj } call
            || obj.Type != typeof(string) || call.Arguments.Count != 2
            || call.Arguments[0].Type != call.Arguments[1].Type
            || (call.Arguments[0].Type != typeof(string) && call.Arguments[0].Type != typeof(char)))
            return false;

        receiver = obj;
        find = call.Arguments[0];
        replacement = call.Arguments[1];
        return true;
    }
```

`TranslateOperand`:

```csharp
        if (TryMatchReplace(node, out var replaceReceiver, out var replaceFind, out var replaceWith))
        {
            MongoExpression? Operand(Expression e)
                => e.Type == typeof(char) ? TranslateCharAsString(e) : TranslateOperand(e, allowNumericWidening);

            var input = TranslateOperand(replaceReceiver, allowNumericWidening);
            var find = input is null ? null : Operand(replaceFind);
            var with = find is null ? null : Operand(replaceWith);
            return with is null ? null : new MongoReplaceExpression(input!, find!, with);
        }
```

- [ ] **Step 5: Dispatchers + allowlists** (as Task 4, with these bodies):

```csharp
            MongoReplaceExpression replace
                => new BsonDocument("$replaceAll", new BsonDocument
                {
                    { "input", RenderBranch(replace.Input, placeholders, elementVariable) },
                    { "find", RenderBranch(replace.Find, placeholders, elementVariable) },
                    { "replacement", new BsonDocument("$ifNull", new BsonArray
                        { RenderBranch(replace.Replacement, placeholders, elementVariable), "" }) }
                }),
```

`CanRender`/`AllFieldsDefaultSerialized`/`IsArrayFreeComputedSubtree`: all three operands. Prefix rewriter
rebuilds with all three rewritten. `IsQueryDialectRenderable => false`. Coverage sample
`new MongoReplaceExpression(headingField, new MongoConstantExpression("a", null), new MongoConstantExpression("b", null))`
with the Trim-equivalent rows.

- [ ] **Step 6: Run unit + functional tests.** Expected: PASS.

- [ ] **Step 7: Spec baselines + native verification** for `Replace`, `Replace_Char`, `Replace_using_property_arguments`,
`Replace_with_empty_string`.

- [ ] **Step 8: Commit** — `EF-322: native string.Replace`.

---

## Task 6: `Equals` with `StringComparison` (instance and static)

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoRegexExpression.cs` (add `MongoRegexKind.Exact`)
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoRegexPatternBuilder.cs` (`Exact` → `^escaped$`)
- Modify: `MongoAggregationExpressionRenderer.cs` (`RenderRegexAsExpr`: `Exact` arm)
- Modify: `MongoExpressionTranslator.cs` (`TranslateNode`: new arm **before** the existing `Method.Name: nameof(object.Equals)` arms)
- Create: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.StringEquals.cs`
- Test: `MongoExpressionTranslatorStringEqualsTests.cs` (create); `NativeStringCaseInsensitiveMatchTests.cs` (add cases)

**Interfaces:**
- Produces: `MongoRegexKind.Exact` (whole-string match). Task 11 reuses it with `caseInsensitive: true`.

- [ ] **Step 1: Failing tests**

```csharp
public class MongoExpressionTranslatorStringEqualsTests
{
    // Widget { int Id; string Text; string Other } + BuildTranslator

    [Fact]
    public void Equals_Ordinal_is_plain_equality()
    {
        Expression<Func<Widget, bool>> predicate = w => w.Text.Equals("Seattle", StringComparison.Ordinal);

        Assert.True(BuildTranslator().TryTranslate(predicate.Body, out var result));
        Assert.Equal(MongoBinaryOperator.Equal, Assert.IsType<MongoBinaryExpression>(result).Operator);
    }

    [Fact]
    public void Static_Equals_OrdinalIgnoreCase_is_an_exact_case_insensitive_regex()
    {
        Expression<Func<Widget, bool>> predicate = w => string.Equals(w.Text, "seattle", StringComparison.OrdinalIgnoreCase);

        Assert.True(BuildTranslator().TryTranslate(predicate.Body, out var result));
        var regex = Assert.IsType<MongoRegexExpression>(result);
        Assert.Equal(MongoRegexKind.Exact, regex.Kind);
        Assert.True(regex.CaseInsensitive);
    }

    [Fact]
    public void Field_to_field_OrdinalIgnoreCase_declines()
    {
        Expression<Func<Widget, bool>> predicate = w => w.Text.Equals(w.Other, StringComparison.OrdinalIgnoreCase);

        Assert.False(BuildTranslator().TryTranslate(predicate.Body, out _));
    }

    [Theory]
    [InlineData(StringComparison.CurrentCulture)]
    [InlineData(StringComparison.InvariantCultureIgnoreCase)]
    public void Culture_comparisons_decline(StringComparison comparison)
    {
        var w = Expression.Parameter(typeof(Widget), "w");
        var body = Expression.Call(
            Expression.Property(w, nameof(Widget.Text)),
            typeof(string).GetMethod(nameof(string.Equals), [typeof(string), typeof(StringComparison)])!,
            Expression.Constant("x"), Expression.Constant(comparison));

        Assert.False(BuildTranslator().TryTranslate(body, out _));
    }
}
```

In `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeStringCaseInsensitiveMatchTests.cs`, add a
`NativeOnly` test asserting `x.Name.Equals("éCOLE", StringComparison.OrdinalIgnoreCase)` matches a stored
`"École"` row, so non-ASCII folding works (follow that file's existing seed/assert pattern).

- [ ] **Step 2: Run to verify failure.**

- [ ] **Step 3: `Exact` kind**

`MongoRegexKind`: add

```csharp
    /// <summary>
    /// Whole-string equality (<c>^term$</c>), used for <c>Equals(…, OrdinalIgnoreCase)</c> and
    /// <c>ToLower()/ToUpper() == constant</c> with <see cref="MongoRegexExpression.CaseInsensitive"/>.
    /// </summary>
    Exact,
```

`MongoRegexPatternBuilder.BuildPattern`: `MongoRegexKind.Exact => "^" + escaped + "$",`

`RenderRegexAsExpr` (field-to-field only reaches here for case-sensitive `Exact`, which the translator never builds;
still make the switch total):
`MongoRegexKind.Exact => new BsonDocument("$eq", new BsonArray { field, term }),`

Update the `CanRender` comment that says "these four kinds" to list the kinds explicitly.

- [ ] **Step 4: Translator arm**

```csharp
// MongoExpressionTranslator.StringEquals.cs (license header)
using System;
using System.Linq.Expressions;
using MongoDB.EntityFrameworkCore.Query.Expressions;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>
/// <see cref="MongoExpressionTranslator"/>: <c>a.Equals(b, StringComparison)</c> and
/// <c>string.Equals(a, b, StringComparison)</c>. Ordinal is <c>$eq</c>; OrdinalIgnoreCase is an anchored
/// case-insensitive regex against a constant/parameter (field-to-field declines: <c>$strcasecmp</c> is ASCII-only).
/// Culture comparisons decline.
/// </summary>
internal sealed partial class MongoExpressionTranslator
{
    private bool TryTranslateStringEqualsWithComparison(MethodCallExpression call, out MongoExpression? result)
    {
        result = null;
        if (call.Method.Name != nameof(string.Equals) || call.Method.DeclaringType != typeof(string))
            return false;

        Expression left, right, comparisonArg;
        if (call.Object is not null && call.Arguments.Count == 2)
            (left, right, comparisonArg) = (call.Object, call.Arguments[0], call.Arguments[1]);
        else if (call.Object is null && call.Arguments.Count == 3)
            (left, right, comparisonArg) = (call.Arguments[0], call.Arguments[1], call.Arguments[2]);
        else
            return false;

        if (comparisonArg is not ConstantExpression { Value: StringComparison comparison })
            return false;

        switch (comparison)
        {
            case StringComparison.Ordinal:
                result = TranslateComparisonCore(left, right, ExpressionType.Equal);
                return result is not null;

            case StringComparison.OrdinalIgnoreCase:
                // Field on either side; the other must be a constant/parameter.
                if (!TryResolveMember(Unwrap(left), out var property, out var fieldPath, out var isOuter))
                {
                    (left, right) = (right, left);
                    if (!TryResolveMember(Unwrap(left), out property, out fieldPath, out isOuter))
                        return false;
                }

                if (isOuter || property.ClrType != typeof(string))
                    return false;

                var term = TranslateValue(Unwrap(right), property);
                if (term is null)
                    return false; // field-to-field or computed: no exact case-insensitive form

                result = new MongoRegexExpression(
                    new MongoFieldExpression(property, fieldPath!), MongoRegexKind.Exact, term, negated: false,
                    caseInsensitive: true);
                return true;

            default:
                return false;
        }
    }
}
```

In `TranslateNode`, before the first `Method.Name: nameof(object.Equals)` arm:

```csharp
            // string.Equals with an explicit StringComparison; see MongoExpressionTranslator.StringEquals.cs.
            case MethodCallExpression stringEqualsCall
                when TryTranslateStringEqualsWithComparison(stringEqualsCall, out var stringEquals):
                return stringEquals;
```

(If `TryResolveMember`'s `fieldPath` out parameter is non-nullable, drop the `!`. Match the regex arm's usage.)

Also check: `MongoPipelineFactory`'s regex placeholder path must handle `Exact` (it calls `BuildPattern`, so it does).

- [ ] **Step 5: Run unit + functional tests.** Expected: PASS.

- [ ] **Step 6: Spec baselines + native verification** for `Equals_with_Ordinal`, `Equals_with_OrdinalIgnoreCase`,
`Static_Equals_with_Ordinal`, `Static_Equals_with_OrdinalIgnoreCase`.

- [ ] **Step 7: Commit** — `EF-322: native string.Equals with StringComparison`.

---

## Task 7: `IsNullOrEmpty` / `IsNullOrWhiteSpace` (predicate side)

**Files:**
- Modify: `MongoExpressionTranslator.cs` (`TranslateNode`: new arm before the regex arm)
- Test: `MongoExpressionTranslatorIsNullOrTests.cs` (create)

**Interfaces:**
- Consumes: existing `== null`, `== ""`, `OrElse`, `Trim()` translations; nothing new is produced.
- Produces: the predicate forms Task 9 projects.

Approach: rewrite to the equivalent LINQ expression and translate that, so all existing null/missing handling applies.
`IsNullOrEmpty(s)` ≡ `s == null || s == ""`. `IsNullOrWhiteSpace(s)` ≡ `s == null || s.Trim() == ""`
(native `Trim` uses .NET's whitespace set).

- [ ] **Step 1: Failing tests**

```csharp
public class MongoExpressionTranslatorIsNullOrTests
{
    // Widget { int Id; string? Text } + BuildTranslator

    [Fact]
    public void IsNullOrEmpty_translates_to_null_or_empty_disjunction()
    {
        Expression<Func<Widget, bool>> predicate = w => string.IsNullOrEmpty(w.Text);

        Assert.True(BuildTranslator().TryTranslate(predicate.Body, out var result));
        Assert.Equal(MongoBinaryOperator.OrElse, Assert.IsType<MongoBinaryExpression>(result).Operator);
    }

    [Fact]
    public void IsNullOrWhiteSpace_uses_native_trim()
    {
        Expression<Func<Widget, bool>> predicate = w => string.IsNullOrWhiteSpace(w.Text);

        Assert.True(BuildTranslator().TryTranslate(predicate.Body, out var result));
        var or = Assert.IsType<MongoBinaryExpression>(result);
        var trimCompare = Assert.IsType<MongoBinaryExpression>(or.Right);
        Assert.IsType<MongoTrimExpression>(trimCompare.Left);
    }
}
```

- [ ] **Step 2: Run to verify failure.**

- [ ] **Step 3: Implement**

```csharp
            // string.IsNullOrEmpty(s) / IsNullOrWhiteSpace(s): translate the equivalent `s == null || s == ""`
            // (`s.Trim() == ""` for whitespace) so the existing null/missing semantics apply.
            case MethodCallExpression { Method.IsStatic: true, Arguments: [var nullOrArg] } nullOrCall
                when nullOrCall.Method.DeclaringType == typeof(string)
                     && nullOrCall.Method.Name is nameof(string.IsNullOrEmpty) or nameof(string.IsNullOrWhiteSpace):
            {
                var tested = nullOrCall.Method.Name == nameof(string.IsNullOrWhiteSpace)
                    ? (Expression)Expression.Call(nullOrArg, typeof(string).GetMethod(nameof(string.Trim), Type.EmptyTypes)!)
                    : nullOrArg;

                return TranslateNode(Expression.OrElse(
                    Expression.Equal(nullOrArg, Expression.Constant(null, typeof(string))),
                    Expression.Equal(tested, Expression.Constant(string.Empty))));
            }
```

- [ ] **Step 4: Run unit tests.** Expected: PASS.

- [ ] **Step 5: Spec** — `IsNullOrWhiteSpace` should now flip (regenerate + verify). `IsNullOrEmpty` and
`IsNullOrEmpty_negated` still fail under NativeOnly on their `Select` half; confirm their `Where` half's MQL is
native by regenerating their baselines now (the test passes normally) and leave the NativeOnly flip to Task 9.

- [ ] **Step 6: Commit** — `EF-322: native string.IsNullOrEmpty/IsNullOrWhiteSpace predicates`.

---

## Task 8: `Regex.IsMatch(field, constantPattern[, options])`

**Files:**
- Modify: `MongoRegexExpression.cs` (add `MongoRegexKind.Pattern` + `RegexOptions` string)
- Modify: `MongoRegexPatternBuilder.cs`, `MongoQueryLanguageRenderer.cs` (`RenderRegex` options), `MongoAggregationExpressionRenderer.cs` (`RenderRegexAsExpr`)
- Modify: `MongoExpressionTranslator.Regex.cs` (`TryTranslateRegexIsMatch`: forward shape)
- Test: `MongoExpressionTranslatorRegexIsMatchTests.cs` (add forward-shape tests)

**Interfaces:**
- Produces: `MongoRegexKind.Pattern` (term is a raw, unescaped pattern) and `MongoRegexExpression.PatternOptions`
  (`string`, default `""`), used only for `Pattern`.

- [ ] **Step 1: Failing tests** (add to the existing file, following its setup)

```csharp
    [Fact]
    public void Forward_IsMatch_with_constant_pattern_translates_to_Pattern_kind()
    {
        Expression<Func<Widget, bool>> predicate = w => Regex.IsMatch(w.Text, "^S");

        Assert.True(BuildTranslator().TryTranslate(predicate.Body, out var result));
        var regex = Assert.IsType<MongoRegexExpression>(result);
        Assert.Equal(MongoRegexKind.Pattern, regex.Kind);
        Assert.Equal("", regex.PatternOptions);
    }

    [Fact]
    public void Forward_IsMatch_maps_IgnoreCase_and_Multiline()
    {
        Expression<Func<Widget, bool>> predicate
            = w => Regex.IsMatch(w.Text, "^s", RegexOptions.IgnoreCase | RegexOptions.Multiline);

        Assert.True(BuildTranslator().TryTranslate(predicate.Body, out var result));
        Assert.Equal("im", Assert.IsType<MongoRegexExpression>(result).PatternOptions);
    }

    [Theory]
    [InlineData(RegexOptions.RightToLeft)]
    [InlineData(RegexOptions.ECMAScript)]
    [InlineData(RegexOptions.NonBacktracking)]
    public void Forward_IsMatch_with_unsupported_options_declines(RegexOptions options)
    {
        var w = Expression.Parameter(typeof(Widget), "w");
        var body = Expression.Call(
            typeof(Regex).GetMethod(nameof(Regex.IsMatch), [typeof(string), typeof(string), typeof(RegexOptions)])!,
            Expression.Property(w, nameof(Widget.Text)), Expression.Constant("^S"), Expression.Constant(options));

        Assert.False(BuildTranslator().TryTranslate(body, out _));
    }
```

Also a query-dialect render test (in `MongoQueryLanguageRendererTests.cs`, following its helper) asserting
`{ "Text" : { "$regularExpression" : { "pattern" : "^S", "options" : "" } } }`.

- [ ] **Step 2: Run to verify failure.**

- [ ] **Step 3: Implement**

- `MongoRegexKind.Pattern` with doc: "`Regex.IsMatch(field, pattern)`: the term is a live .NET pattern passed to
  PCRE unchanged (dialect differences, e.g. `\p{...}` classes, are the caller's). Options come from
  `PatternOptions`."
- `MongoRegexExpression`: add optional ctor parameter `string patternOptions = ""` and property `PatternOptions`.
- `MongoRegexPatternBuilder.BuildPattern`: `MongoRegexKind.Pattern => term,` (before the `Regex.Escape`
  line, return `term` unescaped for `Pattern`).
- `MongoQueryLanguageRenderer.RenderRegex` constant branch options:
  `regex.Kind == MongoRegexKind.Pattern ? regex.PatternOptions : regex.Kind == MongoRegexKind.Like ? "is" : regex.CaseInsensitive ? "is" : "s"`.
- `RenderRegexAsExpr`: `MongoRegexKind.Pattern => new BsonDocument("$regexMatch", new BsonDocument { { "input", field }, { "regex", term }, { "options", regex.PatternOptions } }),`
- In `TryTranslateRegexIsMatch`, before the existing reversed-shape logic, add the forward shape: first argument
  resolves via `TryResolveMember` to a string property (not outer), second is a `ConstantExpression { Value: string }`,
  optional third is `ConstantExpression { Value: RegexOptions }`. Map options:

```csharp
    private static string? MapRegexOptions(RegexOptions options)
    {
        const RegexOptions ignorable = RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture;
        const RegexOptions mapped = RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Singleline
                                    | RegexOptions.IgnorePatternWhitespace;
        if ((options & ~(ignorable | mapped)) != 0)
            return null; // RightToLeft, ECMAScript, NonBacktracking change matching semantics

        return (options.HasFlag(RegexOptions.IgnoreCase) ? "i" : "")
               + (options.HasFlag(RegexOptions.Multiline) ? "m" : "")
               + (options.HasFlag(RegexOptions.Singleline) ? "s" : "")
               + (options.HasFlag(RegexOptions.IgnorePatternWhitespace) ? "x" : "");
    }
```

  Build `new MongoRegexExpression(new MongoFieldExpression(property, fieldPath), MongoRegexKind.Pattern, new MongoConstantExpression(pattern, null), negated: false, patternOptions: mapped)`.
  A parameterized pattern declines (no placeholder support for raw patterns in this task).

- [ ] **Step 4: Run unit tests.** Expected: PASS.

- [ ] **Step 5: Spec baselines + native verification** for `Regex_IsMatch`. Also rerun `Regex_IsMatch_constant_input`
(reversed shape) and the `MongoExpressionNodeCoverageTests` suite.

- [ ] **Step 6: Commit** — `EF-322: native Regex.IsMatch(field, pattern)`.

---

## Task 9: Bare boolean and integer projections of string predicates

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.cs` (`TranslateOperand` predicate hand-off ~line 1597)
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeProjectionBinder.cs` (three allowlists)
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoAggregationExpressionRenderer.cs` (`RenderRegexAsExpr` constant/parameter terms → `$regexMatch`)
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoPipelineFactory.cs` only if Step 3's placeholder check requires it
- Test: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeStringPredicateProjectionTests.cs` (create)
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/NativeProjectionBinderBareBodyTests.cs` (add cases)

**Interfaces:**
- Consumes: Tasks 1, 6, 7, 8 predicate nodes.
- Produces: `Select(x => <string predicate>)` and `Select(x => x.S.Length / IndexOf(...))` on `NativeRoute.Projection`.

- [ ] **Step 1: Failing functional tests**

```csharp
/// <summary>
/// Bare boolean/integer projections of string predicates (<c>Select(x =&gt; x.Text.Contains("a"))</c>) run
/// natively and agree with LINQ-to-objects, including for a document whose field is missing.
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeStringPredicateProjectionTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    public class Row
    {
        public ObjectId Id { get; set; }
        public int Order { get; set; }
        public string? Text { get; set; }
    }

    [Fact]
    public void Contains_and_negated_contains_projections_match_oracle()
    {
        var (context, rows) = Seed(nameof(Contains_and_negated_contains_projections_match_oracle));
        using (context)
        {
            Assert.Equal(rows.Select(r => r.Text != null && r.Text.Contains("eat")),
                context.Entities.OrderBy(x => x.Order).Select(x => x.Text!.Contains("eat")).ToList());
            Assert.Equal(rows.Select(r => !(r.Text != null && r.Text.Contains("eat"))),
                context.Entities.OrderBy(x => x.Order).Select(x => !x.Text!.Contains("eat")).ToList());
        }
    }

    [Fact]
    public void Case_insensitive_contains_projection_folds_non_ascii()
    {
        var (context, _) = Seed(nameof(Case_insensitive_contains_projection_folds_non_ascii));
        using (context)
        {
            var result = context.Entities.OrderBy(x => x.Order)
                .Select(x => x.Text!.Contains("ÉCO", StringComparison.OrdinalIgnoreCase)).ToList();
            Assert.Equal([false, true, false, false], result);
        }
    }

    [Fact]
    public void IsNullOrEmpty_projection_treats_missing_as_null()
    {
        var (context, _) = Seed(nameof(IsNullOrEmpty_projection_treats_missing_as_null));
        using (context)
        {
            var result = context.Entities.OrderBy(x => x.Order).Select(x => string.IsNullOrEmpty(x.Text)).ToList();
            Assert.Equal([false, false, true, true], result);
        }
    }

    [Fact]
    public void Length_projection_goes_native()
    {
        var (context, _) = Seed(nameof(Length_projection_goes_native));
        using (context)
        {
            Assert.Equal([7, 5], context.Entities.Where(x => x.Order < 2).OrderBy(x => x.Order)
                .Select(x => x.Text!.Length).ToList());
        }
    }

    // Order 0: "Seattle", 1: "école", 2: "" , 3: field missing (inserted as a raw BsonDocument).
    private (SingleEntityDbContext<Row>, Row[]) Seed(string testName)
    {
        var name = TemporaryDatabaseFixtureBase.CreateCollectionName(testName) + Guid.NewGuid().ToString("N")[..8];
        var collection = database.MongoDatabase.GetCollection<Row>(name);
        var rows = new[] { new Row { Order = 0, Text = "Seattle" }, new Row { Order = 1, Text = "école" }, new Row { Order = 2, Text = "" } };
        collection.InsertMany(rows);
        database.MongoDatabase.GetCollection<BsonDocument>(name)
            .InsertOne(new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Order", 3 } });

        var context = SingleEntityDbContext.Create(
            collection,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(MongoQueryMode.NativeOnly);
            });
        return (context, [.. rows, new Row { Order = 3, Text = null }]);
    }
}
```

(The `Contains` oracle only covers the non-null rows; if EF's shaper throws for the missing row on `Contains`,
restrict that test to `Where(x => x.Order < 3)`. The missing-row behavior that matters is `IsNullOrEmpty`.)

- [ ] **Step 2: Run to verify failure** — NativeOnly throws "Query projects a non-entity result".

- [ ] **Step 3: Hand bool-typed string predicates from `TranslateOperand` to `TranslateNode`**

Replace the "client-collection Contains or a Not" hand-off with:

```csharp
        // A predicate used as a value (a computed sort key OrderBy(x => !x.Flag), or a bare boolean projection
        // Select(x => x.Name.StartsWith("A"))): hand off to TranslateNode. Limited to Contains, Not, string
        // predicate methods, logical and/or, and comparisons; aggregation renderability is decided by CanRender.
        if (node is MethodCallExpression containsCall && TryMatchContainsMethod(containsCall, out _, out _))
            return TranslateNode(node);

        if (node is MethodCallExpression { Type: var predicateType } predicateCall && predicateType == typeof(bool)
            && (TryMatchRegexMethod(predicateCall, out _, out _, out _, out _)
                || predicateCall.Method.DeclaringType == typeof(string)
                || predicateCall.Method.DeclaringType == typeof(System.Text.RegularExpressions.Regex)))
            return TranslateNode(node);

        if (node is UnaryExpression { NodeType: ExpressionType.Not }
            or BinaryExpression { NodeType: ExpressionType.AndAlso or ExpressionType.OrElse })
            return TranslateNode(node);
```

- [ ] **Step 4: Render constant/parameter regex terms exactly in the aggregation dialect**

In `RenderRegexAsExpr`, before the `$toLower` fold, route constant/parameter terms to `$regexMatch` with the same
pattern and options the query dialect uses (exact case folding, no `$literal` hazard):

```csharp
        if (regex.Kind is not (MongoRegexKind.IsMatch or MongoRegexKind.Pattern)
            && regex.Term is MongoConstantExpression { Value: string } or MongoParameterExpression)
        {
            var options = regex.Kind == MongoRegexKind.Like || regex.CaseInsensitive ? "is" : "s";
            BsonValue regexValue = regex.Term is MongoConstantExpression { Value: string literal }
                ? new BsonRegularExpression(MongoRegexPatternBuilder.BuildPattern(literal, regex.Kind), options)
                : placeholders.CreateRegexPlaceholder(((MongoParameterExpression)regex.Term).Name, regex.Kind, regex.CaseInsensitive);
            var match = new BsonDocument("$regexMatch", new BsonDocument { { "input", field }, { "regex", regexValue } });
            return regex.Negated ? new BsonDocument("$not", new BsonArray { match }) : match;
        }
```

`$regexMatch` returns `false` for a null/missing input, matching the query dialect. Now the `Like` kind also has an
`$expr` rendering, so change `CanRender`'s `MongoRegexExpression { Kind: MongoRegexKind.Like } => false` to
`MongoRegexExpression { Kind: MongoRegexKind.Like, Term: not (MongoConstantExpression { Value: string } or MongoParameterExpression) } => false`
and update the comment. Confirm with `grep -n "CreateRegexPlaceholder" src` that the placeholder substitution in
`MongoPipelineFactory.Build` walks `$project` stages as well as `$match`; if it only walks `$match`, extend the walk.

- [ ] **Step 5: Admit the leaves in `NativeProjectionBinder`**

- `TryTranslateLeaf` `value is ...` list (~line 653): add `or MongoRegexExpression or MongoStringLengthExpression
  or MongoStringIndexOfExpression or MongoBinaryExpression { Operator: MongoBinaryOperator.Equal or MongoBinaryOperator.NotEqual or MongoBinaryOperator.LessThan or MongoBinaryOperator.LessThanOrEqual or MongoBinaryOperator.GreaterThan or MongoBinaryOperator.GreaterThanOrEqual or MongoBinaryOperator.AndAlso or MongoBinaryOperator.OrElse }
  or MongoUnaryExpression { Operator: MongoUnaryOperator.Not }`.
- `TryDeriveSyntheticAlias`: new gate 1c4 for the same set, `when IsArrayFreeComputedSubtree(leaf) && MongoAggregationExpressionRenderer.CanRender(leaf)`.
- `IsArrayFreeComputedSubtree`: add
  `MongoRegexExpression r => IsArrayFreeComputedSubtree(r.Field) && IsArrayFreeComputedSubtree(r.Term),`
  `MongoStringLengthExpression l => IsArrayFreeComputedSubtree(l.Operand),`
  `MongoStringIndexOfExpression io => IsArrayFreeComputedSubtree(io.Haystack) && IsArrayFreeComputedSubtree(io.Needle) && (io.Start is null || IsArrayFreeComputedSubtree(io.Start)),`
  `MongoUnaryExpression u => IsArrayFreeComputedSubtree(u.Operand),`
  `MongoElementRefExpression => true,` (the regex `Field` can be a Distinct alias ref).
  (`MongoBinaryExpression` is already there.)

Add unit tests to `NativeProjectionBinderBareBodyTests.cs`, following that file's existing pattern, asserting
`Select(w => w.Text.StartsWith("a"))` and `Select(w => w.Text.Length)` bind to `NativeRoute.Projection` with a
synthetic alias.

- [ ] **Step 6: Null/missing semantics check for `IsNullOrEmpty`**

Run the functional test. If `IsNullOrEmpty_projection_treats_missing_as_null` fails because aggregation
`$eq: ["$Text", null]` is false for a missing field, fix it in the aggregation renderer's comparison rendering for
`x == null`: find how the existing native `Select(x => x.Text == null)` renders (`grep -n "NullSafe" src`). If it
already uses `$ifNull`/`NumericTypeBracket` NullSafe, IsNullOrEmpty inherits it. Otherwise make `RenderBinary` render
`Equal`/`NotEqual` against a null constant as `{ $eq: [ { $ifNull: [ lhs, null ] }, null ] }`, and add a renderer
unit test for it.

- [ ] **Step 7: Run unit + functional tests.** Also run the whole `FullyQualifiedName~Native` functional subset and
the full `UnitTests` project: the Step 3 hand-off widens which sort keys go native, so check for regressions.

- [ ] **Step 8: Spec baselines + native verification** for `Contains_Column`, `Contains_negated`, `IsNullOrEmpty`,
`IsNullOrEmpty_negated`. Then run the whole class normally to catch other baselines whose `$project`/`$sort` MQL
changed, and regenerate those.

- [ ] **Step 9: Commit** — `EF-322: native bare boolean/integer projections of string predicates`.

---

## Task 10: `CompareTo` / `string.Compare`

**Files:**
- Create: `src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoStringCompareExpression.cs`
- Create: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.StringCompare.cs`
- Modify: `MongoExpressionTranslator.cs` (`TranslateNode`: arm before `case BinaryExpression be when IsComparison(be.NodeType)`), plus the seven dispatchers and `NativeProjectionBinder` allowlists
- Test: `MongoExpressionTranslatorStringCompareTests.cs` (create), coverage + renderer tests
- Test: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeStringCompareTests.cs` (create)

**Interfaces:**
- Consumes: Tasks 4 (Substring) and 5 (Replace) for the nested spec tests; `TranslateComparisonCore`, `Mirror`.
- Produces: `MongoStringCompareExpression(MongoExpression left, MongoExpression right)`, `Type == typeof(int)`,
  renders `{ $cmp: [left, right] }` (string operands `$literal`-wrapped).

Folding table for `cmp OP k` (after normalizing the call to the left, with `Mirror` when it was on the right):

| k \ OP | `==` | `!=` | `>` | `>=` | `<` | `<=` |
|---|---|---|---|---|---|---|
| -1 | `<` | `>=` | `>=` | true | false | `<` |
| 0 | `==` | `!=` | `>` | `>=` | `<` | `<=` |
| 1 | `>` | `<=` | false | `>` | `<=` | true |
| other k | `k∉{-1,0,1}`: `==`→false, `!=`→true, `>`/`>=`→(k<0 ? true : false), `<`/`<=`→(k>0 ? true : false) |

`true`/`false` fold to `MongoConstantExpression(bool)`. That fold is always exact, because `$cmp` and .NET's culture
`Compare` both return only -1/0/1. A relational fold (`<`, `==`, ...) is used only when **fold-safe**: the receiver
is a `MongoFieldExpression` whose property `IsNullable == false`, and the argument translates to a non-null string
`MongoConstantExpression`. Otherwise emit `MongoBinaryExpression(op, new MongoStringCompareExpression(l, r), new MongoConstantExpression(k, null))`,
which renders in `$expr` with BSON ordering (null first, like .NET).

- [ ] **Step 1: Failing tests**

```csharp
public class MongoExpressionTranslatorStringCompareTests
{
    private class Widget
    {
        public int Id { get; set; }
        public string Text { get; set; } = "";
        public string? Maybe { get; set; }
    }

    // BuildTranslator as in Task 1 (SingleEntityDbContext.Create<Widget>(); Maybe is nullable, Text required)

    [Fact]
    public void CompareTo_eq_1_on_required_field_with_constant_folds_to_greater_than()
    {
        Expression<Func<Widget, bool>> predicate = w => w.Text.CompareTo("Seattle") == 1;

        Assert.True(BuildTranslator().TryTranslate(predicate.Body, out var result));
        Assert.Equal(MongoBinaryOperator.GreaterThan, Assert.IsType<MongoBinaryExpression>(result).Operator);
    }

    [Fact]
    public void Constant_on_left_is_mirrored()
    {
        Expression<Func<Widget, bool>> predicate = w => -1 == w.Text.CompareTo("Seattle");

        Assert.True(BuildTranslator().TryTranslate(predicate.Body, out var result));
        Assert.Equal(MongoBinaryOperator.LessThan, Assert.IsType<MongoBinaryExpression>(result).Operator);
    }

    [Fact]
    public void Compare_eq_42_folds_to_false()
    {
        Expression<Func<Widget, bool>> predicate = w => string.Compare(w.Text, "Seattle") == 42;

        Assert.True(BuildTranslator().TryTranslate(predicate.Body, out var result));
        Assert.Equal(false, Assert.IsType<MongoConstantExpression>(result).Value);
    }

    [Fact]
    public void Nullable_receiver_uses_cmp()
    {
        Expression<Func<Widget, bool>> predicate = w => string.Compare(w.Maybe, "a") == -1;

        Assert.True(BuildTranslator().TryTranslate(predicate.Body, out var result));
        Assert.IsType<MongoStringCompareExpression>(Assert.IsType<MongoBinaryExpression>(result).Left);
    }

    [Fact]
    public void Compare_with_StringComparison_declines()
    {
        Expression<Func<Widget, bool>> predicate = w => string.Compare(w.Text, "a", StringComparison.Ordinal) == 0;

        Assert.False(BuildTranslator().TryTranslate(predicate.Body, out _));
    }
}
```

Functional test `NativeStringCompareTests` (NativeOnly), rows `Maybe = null`, `"a"`, `"b"`: assert
`Where(x => string.Compare(x.Maybe, "b") == -1)` returns the null row and `"a"`, matching
`rows.Where(r => string.Compare(r.Maybe, "b", StringComparison.Ordinal) < 0)`. Render test:
`new MongoStringCompareExpression(field, new MongoConstantExpression("$Text", null))` →
`{ "$cmp" : ["$Text", { "$literal" : "$Text" }] }`. Coverage sample + Trim-equivalent rows.

- [ ] **Step 2: Run to verify failure.**

- [ ] **Step 3: Node**

```csharp
/// <summary>
/// <c>a.CompareTo(b)</c> / <c>string.Compare(a, b)</c> as <c>$cmp</c>: binary (ordinal) BSON ordering, with null
/// before every string as in .NET. .NET's culture-sensitive ordering is deliberately not reproduced (consistent with
/// native string <c>==</c>/<c>OrderBy</c>). Aggregation dialect only; used when a relational fold isn't exact (see
/// <c>MongoExpressionTranslator.StringCompare.cs</c>).
/// </summary>
internal sealed class MongoStringCompareExpression(MongoExpression left, MongoExpression right) : MongoExpression
{
    public MongoExpression Left { get; } = left;
    public MongoExpression Right { get; } = right;
    public override Type Type => typeof(int);
}
```

- [ ] **Step 4: Translator**

```csharp
// MongoExpressionTranslator.StringCompare.cs (license header, usings: System, System.Linq.Expressions, Expressions)
internal sealed partial class MongoExpressionTranslator
{
    /// <summary>
    /// <c>a.CompareTo(b) OP k</c> / <c>string.Compare(a, b) OP k</c> with a constant int <c>k</c>. Folds to a plain
    /// comparison or a constant where exact (see the table in the String Translations Phase 2 plan); otherwise
    /// compares <c>$cmp</c> to <c>k</c>.
    /// </summary>
    private bool TryTranslateStringCompare(BinaryExpression comparison, out MongoExpression? result)
    {
        result = null;
        var op = comparison.NodeType;
        Expression callSide = comparison.Left, constantSide = comparison.Right;
        if (!IsStringCompareCall(callSide, out _, out _))
        {
            (callSide, constantSide) = (comparison.Right, comparison.Left);
            op = Mirror(op);
        }

        if (!IsStringCompareCall(callSide, out var a, out var b) || Unwrap(constantSide) is not ConstantExpression { Value: int k })
            return false;

        bool? constantFold = (k, op) switch
        {
            (-1, ExpressionType.GreaterThanOrEqual) or (1, ExpressionType.LessThanOrEqual) => true,
            (-1, ExpressionType.LessThan) or (1, ExpressionType.GreaterThan) => false,
            (not (-1 or 0 or 1), ExpressionType.Equal) => false,
            (not (-1 or 0 or 1), ExpressionType.NotEqual) => true,
            (< -1, ExpressionType.GreaterThan or ExpressionType.GreaterThanOrEqual) => true,
            (> 1, ExpressionType.GreaterThan or ExpressionType.GreaterThanOrEqual) => false,
            (< -1, ExpressionType.LessThan or ExpressionType.LessThanOrEqual) => false,
            (> 1, ExpressionType.LessThan or ExpressionType.LessThanOrEqual) => true,
            _ => null
        };
        if (constantFold is { } folded)
        {
            result = new MongoConstantExpression(folded, forSerialization: null);
            return true;
        }

        ExpressionType? relational = (k, op) switch
        {
            (0, _) => op,
            (-1, ExpressionType.Equal) or (-1, ExpressionType.LessThanOrEqual) => ExpressionType.LessThan,
            (-1, ExpressionType.NotEqual) or (-1, ExpressionType.GreaterThan) => ExpressionType.GreaterThanOrEqual,
            (1, ExpressionType.Equal) or (1, ExpressionType.GreaterThanOrEqual) => ExpressionType.GreaterThan,
            (1, ExpressionType.NotEqual) or (1, ExpressionType.LessThan) => ExpressionType.LessThanOrEqual,
            _ => null
        };

        if (relational is { } relOp && IsFoldSafe(a, b))
        {
            result = TranslateComparisonCore(a, b, relOp);
            return result is not null;
        }

        var left = TranslateOperand(a);
        var right = left is null ? null : TranslateOperand(b);
        if (right is null || !AllFieldsDefaultSerialized(left!) || !AllFieldsDefaultSerialized(right))
            return false;

        result = new MongoBinaryExpression(
            MapComparisonOperator(op), new MongoStringCompareExpression(left!, right),
            new MongoConstantExpression(k, forSerialization: null));
        return true;
    }

    private static bool IsStringCompareCall(Expression node, out Expression a, out Expression b)
    {
        a = b = null!;
        switch (Unwrap(node))
        {
            case MethodCallExpression { Method.Name: nameof(string.CompareTo), Object: { Type: var t } obj, Arguments: [var arg] }
                when t == typeof(string) && arg.Type == typeof(string):
                (a, b) = (obj, arg);
                return true;
            case MethodCallExpression { Method.Name: nameof(string.Compare), Object: null, Arguments: [var x, var y] } call
                when call.Method.DeclaringType == typeof(string):
                (a, b) = (x, y);
                return true;
            default:
                return false;
        }
    }

    // A relational fold renders as query-dialect { f: { $op: "c" } }, which never matches a null field; .NET orders
    // null first. So fold only when neither side can be null.
    private bool IsFoldSafe(Expression a, Expression b)
        => TryResolveMember(Unwrap(a), out var property, out _, out var isOuter)
           && !isOuter && !property.IsNullable
           && Unwrap(b) is ConstantExpression { Value: string };
}
```

`MapComparisonOperator` is the existing `ExpressionType → MongoBinaryOperator` mapper near line 1357. Use its real
name (`grep -n "ExpressionType.GreaterThan => MongoBinaryOperator.GreaterThan" src`). `TranslateOperand`'s default
arguments allow the one-argument call. In `TranslateNode`, before `case BinaryExpression be when IsComparison(be.NodeType):`:

```csharp
            // a.CompareTo(b) / string.Compare(a, b) against a constant; see MongoExpressionTranslator.StringCompare.cs.
            case BinaryExpression stringCompare when IsComparison(stringCompare.NodeType)
                                                     && TryTranslateStringCompare(stringCompare, out var stringCompareResult):
                return stringCompareResult;
```

- [ ] **Step 5: Dispatchers + allowlists** — `Render`:
`MongoStringCompareExpression cmp => new BsonDocument("$cmp", new BsonArray { RenderBranch(cmp.Left, placeholders, elementVariable), RenderBranch(cmp.Right, placeholders, elementVariable) }),`
`CanRender`/`AllFieldsDefaultSerialized`/`IsArrayFreeComputedSubtree`: both operands; `IsQueryDialectRenderable => false`;
prefix rewriter rebuilds; `NativeProjectionBinder` allowlists; coverage rows.

- [ ] **Step 6: Run unit + functional tests.** Expected: PASS.

- [ ] **Step 7: Spec baselines + native verification** for the 12 `Compare*`/`CompareTo*` tests. Expect `$cmp` in
`CompareTo_with_parameter`/`Compare_with_parameter` (the argument is a parameter, so not fold-safe) and in the
`*_nested` tests.

- [ ] **Step 8: Commit** — `EF-322: native CompareTo/string.Compare (binary ordering)`.

---

## Task 11: `ToLower`/`ToUpper` — regex in `Where`, client re-application in `Select`

**Files:**
- Create: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.CaseMapping.cs`
- Modify: `MongoExpressionTranslator.cs` (`TranslateNode` arm before the ordinary comparison arm)
- Modify: `NativeProjectionBinder.cs` (`IsStringSequenceMaterializationCall` → add `IsClientReappliedStringCall`; `TryTranslateLeaf` string-sequence branch; the three `hasStringSequenceLeaf |=` sites)
- Modify: `Visitors/MongoProjectionBindingExpressionVisitor.cs` (~line 217), `Visitors/MongoProjectionBindingRemovingExpressionVisitor.cs` (~line 205), `Visitors/MongoMixedProjectionBindingRemovingExpressionVisitor.cs` (`TryBindStringSequenceLeaf` ~line 383)
- Modify: `Expressions/MongoSelectDefinition.cs` (`HasStringSequenceProjectionLeaf` doc: now also covers case-mapping leaves)
- Test: `MongoExpressionTranslatorCaseMappingTests.cs` (create), `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeStringCaseMappingTests.cs` (create)

**Interfaces:**
- Consumes: `MongoRegexKind.Exact` (Task 6).
- Produces: `NativeProjectionBinder.IsClientReappliedStringCall(MethodCallExpression) : bool`. True for
  string-sequence calls (existing) and for zero-arg `ToLower`/`ToUpper`/`ToLowerInvariant`/`ToUpperInvariant` on a
  string.

- [ ] **Step 1: Failing tests**

```csharp
public class MongoExpressionTranslatorCaseMappingTests
{
    // Widget { int Id; string Text } + BuildTranslator

    [Fact]
    public void ToLower_eq_lowercase_constant_is_exact_case_insensitive_regex()
    {
        Expression<Func<Widget, bool>> predicate = w => w.Text.ToLower() == "seattle";

        Assert.True(BuildTranslator().TryTranslate(predicate.Body, out var result));
        var regex = Assert.IsType<MongoRegexExpression>(result);
        Assert.Equal(MongoRegexKind.Exact, regex.Kind);
        Assert.True(regex.CaseInsensitive);
        Assert.False(regex.Negated);
    }

    [Fact]
    public void ToLower_eq_constant_with_uppercase_folds_to_false()
    {
        Expression<Func<Widget, bool>> predicate = w => w.Text.ToLower() == "Seattle";

        Assert.True(BuildTranslator().TryTranslate(predicate.Body, out var result));
        Assert.Equal(false, Assert.IsType<MongoConstantExpression>(result).Value);
    }

    [Fact]
    public void ToUpper_ne_uppercase_constant_is_negated_regex()
    {
        Expression<Func<Widget, bool>> predicate = w => "SEATTLE" != w.Text.ToUpper();

        Assert.True(BuildTranslator().TryTranslate(predicate.Body, out var result));
        Assert.True(Assert.IsType<MongoRegexExpression>(result).Negated);
    }

    [Fact]
    public void ToLower_as_a_value_declines()
    {
        Expression<Func<Widget, string>> selector = w => w.Text.ToLower();

        Assert.False(BuildTranslator().TryTranslateValue(selector.Body, out _));
    }
}
```

Functional `NativeStringCaseMappingTests` (NativeOnly), rows `"Seattle"`, `"École"`, `null` (nullable `Text`):
- `Select(x => x.Text!.ToLower())` equals `rows.Select(r => r.Text?.ToLower())`, so `"école"` is lowercased
  correctly and null stays null.
- `Where(x => x.Text!.ToLower() == "école")` returns the École row.
- `Where(x => x.Text!.ToUpper() == "Seattle")` returns nothing.

- [ ] **Step 2: Run to verify failure.**

- [ ] **Step 3: `Where` side**

```csharp
// MongoExpressionTranslator.CaseMapping.cs
/// <summary>
/// <see cref="MongoExpressionTranslator"/>: <c>x.S.ToLower()/ToUpper() == constant</c> (either side, <c>==</c> or
/// <c>!=</c>). <c>$toLower</c>/<c>$toUpper</c> are ASCII-only, so instead: a constant not already in the target
/// case can never be equal (folds to false/true); otherwise an anchored case-insensitive regex.
/// </summary>
internal sealed partial class MongoExpressionTranslator
{
    private bool TryTranslateCaseMappingComparison(BinaryExpression comparison, out MongoExpression? result)
    {
        result = null;
        if (comparison.NodeType is not (ExpressionType.Equal or ExpressionType.NotEqual))
            return false;

        var (callSide, constantSide) = IsCaseMappingCall(comparison.Left, out _, out _)
            ? (comparison.Left, comparison.Right)
            : (comparison.Right, comparison.Left);

        if (!IsCaseMappingCall(callSide, out var receiver, out var toUpper)
            || Unwrap(constantSide) is not ConstantExpression { Value: string constant }
            || !TryResolveMember(Unwrap(receiver), out var property, out var fieldPath, out var isOuter)
            || isOuter || property.ClrType != typeof(string))
            return false;

        var negated = comparison.NodeType == ExpressionType.NotEqual;
        var mapped = toUpper ? constant.ToUpperInvariant() : constant.ToLowerInvariant();
        if (mapped != constant)
        {
            result = new MongoConstantExpression(negated, forSerialization: null);
            return true;
        }

        result = new MongoRegexExpression(
            new MongoFieldExpression(property, fieldPath!), MongoRegexKind.Exact,
            new MongoConstantExpression(constant, property), negated, caseInsensitive: true);
        return true;
    }

    private static bool IsCaseMappingCall(Expression node, out Expression receiver, out bool toUpper)
    {
        receiver = null!;
        toUpper = false;
        if (Unwrap(node) is not MethodCallExpression { Object: { Type: var t } obj, Arguments.Count: 0, Method.Name: var name }
            || t != typeof(string))
            return false;

        toUpper = name is nameof(string.ToUpper) or nameof(string.ToUpperInvariant);
        if (!toUpper && name is not (nameof(string.ToLower) or nameof(string.ToLowerInvariant)))
            return false;

        receiver = obj;
        return true;
    }
}
```

(Invariant mapping is used for the translate-time check. The regex match is culture-free, so current-culture
special cases such as Turkish dotted I are accepted differences; say so in the class doc.)

In `TranslateNode`, next to Task 10's arm (before the ordinary comparison arm):

```csharp
            case BinaryExpression caseMapping when TryTranslateCaseMappingComparison(caseMapping, out var caseMappingResult):
                return caseMappingResult;
```

- [ ] **Step 4: `Select` side — generalize the string-sequence leaf**

In `NativeProjectionBinder`:

```csharp
    /// <summary>
    /// A call over a string field that is pushed down as the raw string and re-applied client-side: the
    /// string-sequence calls (<see cref="IsStringSequenceMaterializationCall"/>), and zero-arg
    /// <c>ToLower</c>/<c>ToUpper</c>/<c>ToLowerInvariant</c>/<c>ToUpperInvariant</c>, whose server forms are ASCII-only.
    /// </summary>
    internal static bool IsClientReappliedStringCall(MethodCallExpression call)
        => IsStringSequenceMaterializationCall(call) || IsCaseMappingCall(call);

    internal static bool IsCaseMappingCall(MethodCallExpression call)
        => call is { Object.Type: var t, Arguments.Count: 0 } && t == typeof(string)
           && call.Method.Name is nameof(string.ToLower) or nameof(string.ToUpper)
               or nameof(string.ToLowerInvariant) or nameof(string.ToUpperInvariant);

    /// <summary>The string receiver of a client-reapplied call (instance receiver or the single static argument).</summary>
    internal static Expression ClientReappliedSource(MethodCallExpression call)
        => call.Object ?? call.Arguments[0];
```

Replace `IsStringSequenceMaterializationCall` with `IsClientReappliedStringCall` in: the `TryTranslateLeaf`
string-sequence branch (use `ClientReappliedSource(call)` instead of `call.Arguments[0]`), the three
`hasStringSequenceLeaf |=` sites, `MongoProjectionBindingExpressionVisitor` (~line 217),
`MongoProjectionBindingRemovingExpressionVisitor` (~line 207) and `MongoMixedProjectionBindingRemovingExpressionVisitor.TryBindStringSequenceLeaf`.

In both removing visitors, the re-application `Expression.Call(stringSequenceCall.Method, rawStringRead)` must become:

```csharp
                        Expression reapplied = stringSequenceCall.Object is null
                            ? Expression.Call(stringSequenceCall.Method, rawStringRead)
                            : Expression.Condition(
                                Expression.Equal(rawStringRead, Expression.Constant(null, typeof(string))),
                                Expression.Constant(null, typeof(string)),
                                Expression.Call(rawStringRead, stringSequenceCall.Method));
                        return reapplied;
```

(Null propagates, matching server-side function semantics. The string-sequence branch keeps its current behavior.)
Also keep the `TryResolveFieldAccess(...)` argument in step with `ClientReappliedSource(...)`.

- [ ] **Step 5: Run unit + functional tests**, plus the existing `NativeStringSequenceProjectionTests` and
`NativeProjectedCollectionListLeafTests` (regression check for the shared mechanism). Expected: PASS.

- [ ] **Step 6: Spec baselines + native verification** for `ToLower`, `ToUpper`. The `Select` baseline becomes a
plain field `$project` (no `$toLower`).

- [ ] **Step 7: Commit** — `EF-322: native ToLower/ToUpper (regex in Where, client re-application in Select)`.

---

## Task 12: Whole-class native verification, EF8/EF9 fallout, full sweep

**Files:**
- Modify: `tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/NorthwindFunctionsQueryMongoTest.cs` (and any other spec file whose baselines moved)
- Modify: `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md` (only if a new durable invariant emerged; for example
  "string constants in new aggregation operands are `$literal`-wrapped")

- [ ] **Step 1: Whole class under NativeOnly (EF10)**

Run: `MONGODB_EF_NATIVE_ONLY=1 dotnet test <spec csproj> -c "Debug EF10" --no-build --filter "FullyQualifiedName~StringTranslationsMongoTest"`
Expected: `Failed: 0, Passed: 101`. Any failure means a task is incomplete; fix it in the owning task's files.

- [ ] **Step 2: EF10 full spec + functional + unit** (normal mode). Regenerate every moved baseline with a tight
`--filter` per test, review the diffs, rerun.

- [ ] **Step 3: EF8 and EF9**

Build and run the spec project for `Debug EF8` and `Debug EF9`. Expect `NorthwindFunctionsQueryMongoTest` string
tests (`String_Compare_*`, `String_CompareTo_*`, `Substring_*`, `Replace*`, `IndexOf*`, `IsNullOrEmpty*`,
`IsNullOrWhiteSpace*`, `ToLower`/`ToUpper`, `Regex_IsMatch*`, `String_Contains*`/`StartsWith*`/`EndsWith*` with
char args) to show native MQL. A test that previously asserted `AssertTranslationFailed` and now succeeds must be
converted to a real override with a regenerated baseline. Baselines inside `#if EF8`/`#if EF9` blocks may need the
"Actual" copied from the failure message (the rewriter can't place them). Compare each with EF10's baseline for
the same shape.

- [ ] **Step 4: `/test-all`** (all three versions, full solution). Expected: 0 failures.

- [ ] **Step 5: Commit** — `EF-322: regenerate baselines after native string translations phase 2`.
