# Native Query Predicate Breadth (`$expr` renderer + core operators) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Broaden the native query translator to render a core tranche of predicate operators — nullable equality, collection `Contains` (`$in`/`$nin`), string `StartsWith`/`EndsWith`/`Contains` (`$regex`), and field-to-field / arithmetic comparisons — standing up the `$expr` aggregation-expression renderer behind the seam SP1 stubbed.

**Architecture:** A second BSON dialect (aggregation expressions) is added alongside the existing query/match dialect. A new `MongoAggregationExpressionRenderer` produces `$expr` bodies; `MongoQueryLanguageRenderer` gains a per-subtree boundary that keeps `&&`/`||` at the query level (each branch independently indexable) and wraps only the smallest non-query-expressible subtree in `{ $expr: … }`. `MongoExpressionTranslator` is extended to accept the new operator shapes; everything still outside them returns `false` and falls back to driver-LINQ exactly as in SP1.

**Tech Stack:** C# / .NET (net8.0 for EF8/EF9, net10.0 for EF10), EF Core internals, MongoDB C# driver (BSON only), xUnit (plain `Assert.*` — FluentAssertions is not referenced in the test projects).

## Global Constraints

- **Index-first (governing principle):** a node is *query-native* iff any semantically-correct query-dialect rendering exists, and that rendering is always preferred; `$expr` is used only when no correct query-dialect rendering exists.
- **Zero regressions:** full FunctionalTests + SpecificationTests green in `Native` mode on EF10 and EF8; nothing native in SP1 becomes non-native.
- **No `NativeOnly` coverage shrink**; net native coverage must grow by this tranche.
- `<Nullable>enable</Nullable>` on `src/` — annotate all new types.
- Preserve file BOMs on existing files.
- Multi-EF: guard any signature that differs across EF8/EF9/EF10 with `#if EF8 || EF9` / `#if !EF8`. This plan's code is EF-version-neutral unless a step says otherwise.
- Build one version: `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"`. Test one class: `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build --filter "FullyQualifiedName~ClassName"`.
- Commit messages start with `EF-329:`.
- The only reliable native-vs-fallback signal is `NativeOnly` mode (success ⇒ native; `NativeTranslationNotSupportedException` ⇒ fallback). MQL shape alone cannot prove native for shapes the fallback renders identically.

---

### Task 1: Extend the node model (arithmetic operators + `$in` and regex nodes)

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoExpression.cs` (extend `MongoBinaryOperator`)
- Create: `src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoInExpression.cs`
- Create: `src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoRegexExpression.cs`
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoExpressionTests.cs`

**Interfaces:**
- Consumes: `MongoExpression`, `MongoFieldExpression`, `MongoBinaryOperator` (SP1).
- Produces:
  - `MongoBinaryOperator` gains `Add, Subtract, Multiply, Divide, Modulo`.
  - `MongoInExpression(MongoFieldExpression Field, MongoExpression Values, bool Negated) : MongoExpression` — `Values` is a `MongoConstantExpression` (inline collection) or `MongoParameterExpression` (captured collection). `Type => typeof(bool)`.
  - `MongoRegexExpression(MongoFieldExpression Field, MongoRegexKind Kind, MongoExpression Term, bool Negated) : MongoExpression` where `enum MongoRegexKind { StartsWith, EndsWith, Contains }`. `Term` is a `MongoConstantExpression`/`MongoParameterExpression` of `string`. `Type => typeof(bool)`.

- [ ] **Step 1: Extend the operator enum.** In `MongoExpression.cs`, add the arithmetic members to `MongoBinaryOperator` after `OrElse`:

```csharp
internal enum MongoBinaryOperator
{
    Equal,
    NotEqual,
    LessThan,
    LessThanOrEqual,
    GreaterThan,
    GreaterThanOrEqual,
    AndAlso,
    OrElse,
    Add,
    Subtract,
    Multiply,
    Divide,
    Modulo
}
```

Note: `MongoBinaryExpression.Type` already returns `Left.Type` for non-logical operators, which is correct for arithmetic (numeric operand type).

- [ ] **Step 2: Create `MongoInExpression.cs`** (copy the license header + BOM from a sibling file):

```csharp
using System;
using System.Linq.Expressions;

namespace MongoDB.EntityFrameworkCore.Query.Expressions;

/// <summary>
/// Represents a collection-membership test (<c>collection.Contains(x.Field)</c>) rendered as
/// <c>$in</c> (or <c>$nin</c> when <see cref="Negated"/>). Always query-dialect and index-usable.
/// </summary>
internal sealed class MongoInExpression : MongoExpression
{
    public MongoInExpression(MongoFieldExpression field, MongoExpression values, bool negated)
    {
        Field = field;
        Values = values;
        Negated = negated;
    }

    /// <summary>The document field being tested for membership.</summary>
    public MongoFieldExpression Field { get; }

    /// <summary>The candidate values: a <c>MongoConstantExpression</c> (inline) or <c>MongoParameterExpression</c> (captured).</summary>
    public MongoExpression Values { get; }

    /// <summary><see langword="true"/> for <c>$nin</c> (negated membership).</summary>
    public bool Negated { get; }

    /// <inheritdoc />
    public override Type Type => typeof(bool);
}
```

- [ ] **Step 3: Create `MongoRegexExpression.cs`:**

```csharp
using System;
using System.Linq.Expressions;

namespace MongoDB.EntityFrameworkCore.Query.Expressions;

/// <summary>The kind of substring test a <see cref="MongoRegexExpression"/> encodes.</summary>
internal enum MongoRegexKind
{
    StartsWith,
    EndsWith,
    Contains
}

/// <summary>
/// Represents a string prefix/suffix/substring test rendered as a query-dialect <c>$regex</c>.
/// <see cref="MongoRegexKind.StartsWith"/> anchors the pattern (<c>^…</c>) so the index stays usable.
/// </summary>
internal sealed class MongoRegexExpression : MongoExpression
{
    public MongoRegexExpression(MongoFieldExpression field, MongoRegexKind kind, MongoExpression term, bool negated)
    {
        Field = field;
        Kind = kind;
        Term = term;
        Negated = negated;
    }

    public MongoFieldExpression Field { get; }
    public MongoRegexKind Kind { get; }

    /// <summary>The search term: a <c>MongoConstantExpression</c> or <c>MongoParameterExpression</c> of string.</summary>
    public MongoExpression Term { get; }

    /// <summary><see langword="true"/> for a negated match (<c>!s.StartsWith(...)</c>).</summary>
    public bool Negated { get; }

    /// <inheritdoc />
    public override Type Type => typeof(bool);
}
```

- [ ] **Step 4: Add construction tests** to `MongoExpressionTests.cs`:

```csharp
[Fact]
public void MongoInExpression_exposes_operands()
{
    var prop = GetProperty<Customer>("Age");
    var field = new MongoFieldExpression(prop, "Age");
    var values = new MongoConstantExpression(new[] { 1, 2, 3 }, prop);
    var expr = new MongoInExpression(field, values, negated: true);

    Assert.Same(field, expr.Field);
    Assert.Same(values, expr.Values);
    Assert.True(expr.Negated);
    Assert.Equal(typeof(bool), expr.Type);
}

[Fact]
public void MongoRegexExpression_exposes_operands()
{
    var prop = GetProperty<Customer>("Name");
    var field = new MongoFieldExpression(prop, "Name");
    var term = new MongoConstantExpression("A", prop);
    var expr = new MongoRegexExpression(field, MongoRegexKind.StartsWith, term, negated: false);

    Assert.Equal(MongoRegexKind.StartsWith, expr.Kind);
    Assert.Equal(typeof(bool), expr.Type);
}
```

(Add a `string Name` property to the test `Customer` model if the existing one lacks it.)

- [ ] **Step 5: Build + run.** `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"` then `dotnet test ... --no-build --filter "FullyQualifiedName~MongoExpressionTests"`. Expected: PASS.

- [ ] **Step 6: Commit.**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/Expressions/ tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoExpressionTests.cs
git commit -m "EF-329: Add arithmetic operators and \$in/regex query-expression nodes"
```

---

### Task 2: Aggregation-expression renderer (`$expr` dialect)

**Files:**
- Create: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoAggregationExpressionRenderer.cs`
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoAggregationExpressionRendererTests.cs`

**Interfaces:**
- Consumes: `MongoExpression` subtypes, `PlaceholderTable`, `BsonSerializerFactory`, `BsonValueSerializer` (SP1).
- Produces: `internal sealed class MongoAggregationExpressionRenderer` with
  `BsonValue Render(MongoExpression node, PlaceholderTable placeholders)` returning the *inner* aggregation expression (no `$expr` wrapper). Field refs render as `"$<elementName>"`; comparisons as `{ $op: [left, right] }`; arithmetic as `{ $add|$subtract|$multiply|$divide|$mod: [left, right] }`; constants/parameters via the same serialization/placeholder path as the query renderer.

- [ ] **Step 1: Write failing tests** in `MongoAggregationExpressionRendererTests.cs` (mirror the harness in `MongoQueryLanguageRendererTests.cs`: nested `Customer` model with `int Age`, `int Score`, `GetProperty<T>`):

```csharp
[Fact]
public void Renders_field_to_field_comparison()
{
    var age = GetProperty<Customer>("Age");
    var score = GetProperty<Customer>("Score");
    var expr = new MongoBinaryExpression(
        MongoBinaryOperator.Equal,
        new MongoFieldExpression(age, "Age"),
        new MongoFieldExpression(score, "Score"));

    var rendered = new MongoAggregationExpressionRenderer().Render(expr, new PlaceholderTable());

    Assert.Equal(BsonValue.Create(BsonDocument.Parse("{ $eq: ['$Age', '$Score'] }")), rendered);
}

[Fact]
public void Renders_arithmetic_operand()
{
    var age = GetProperty<Customer>("Age");
    var score = GetProperty<Customer>("Score");
    // Age + Score > 5
    var expr = new MongoBinaryExpression(
        MongoBinaryOperator.GreaterThan,
        new MongoBinaryExpression(MongoBinaryOperator.Add,
            new MongoFieldExpression(age, "Age"),
            new MongoFieldExpression(score, "Score")),
        new MongoConstantExpression(5, age));

    var rendered = new MongoAggregationExpressionRenderer().Render(expr, new PlaceholderTable());

    Assert.Equal(BsonValue.Create(BsonDocument.Parse("{ $gt: [ { $add: ['$Age', '$Score'] }, 5 ] }")), rendered);
}
```

- [ ] **Step 2: Run to confirm failure.** `dotnet build ... -c "Debug EF10"` fails (type missing). Expected: compile error `MongoAggregationExpressionRenderer` not found.

- [ ] **Step 3: Implement the renderer.** Create `MongoAggregationExpressionRenderer.cs` (license header + BOM):

```csharp
using System;
using Microsoft.EntityFrameworkCore.Metadata;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Query.Expressions;
using MongoDB.EntityFrameworkCore.Serializers;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>
/// Renders a dialect-agnostic <see cref="MongoExpression"/> subtree to a MongoDB
/// <em>aggregation expression</em> (the body that sits inside <c>{ $expr: … }</c>).
/// Used only for subtrees that have no correct query-dialect rendering (field-to-field
/// comparisons, arithmetic operands); the query renderer wraps the result in <c>$expr</c>.
/// </summary>
internal sealed class MongoAggregationExpressionRenderer
{
    public BsonValue Render(MongoExpression node, PlaceholderTable placeholders)
        => node switch
        {
            MongoFieldExpression field => "$" + field.ElementName,
            MongoConstantExpression or MongoParameterExpression => RenderValue(node, placeholders),
            MongoBinaryExpression binary => RenderBinary(binary, placeholders),
            _ => throw new NativeTranslationNotSupportedException(
                $"MongoAggregationExpressionRenderer does not support node type '{node.GetType().Name}'.")
        };

    private BsonValue RenderBinary(MongoBinaryExpression binary, PlaceholderTable placeholders)
    {
        var op = binary.Operator switch
        {
            MongoBinaryOperator.Equal => "$eq",
            MongoBinaryOperator.NotEqual => "$ne",
            MongoBinaryOperator.LessThan => "$lt",
            MongoBinaryOperator.LessThanOrEqual => "$lte",
            MongoBinaryOperator.GreaterThan => "$gt",
            MongoBinaryOperator.GreaterThanOrEqual => "$gte",
            MongoBinaryOperator.AndAlso => "$and",
            MongoBinaryOperator.OrElse => "$or",
            MongoBinaryOperator.Add => "$add",
            MongoBinaryOperator.Subtract => "$subtract",
            MongoBinaryOperator.Multiply => "$multiply",
            MongoBinaryOperator.Divide => "$divide",
            MongoBinaryOperator.Modulo => "$mod",
            _ => throw new NativeTranslationNotSupportedException(
                $"Unsupported aggregation operator '{binary.Operator}'.")
        };

        var left = Render(binary.Left, placeholders);
        var right = Render(binary.Right, placeholders);
        return new BsonDocument(op, new BsonArray { left, right });
    }

    // Constants/parameters serialize exactly as in the query renderer so a constant and a
    // parameter of the same value emit identical BSON.
    private BsonValue RenderValue(MongoExpression node, PlaceholderTable placeholders)
    {
        switch (node)
        {
            case MongoConstantExpression constant:
                return constant.ForSerialization is null
                    ? BsonValue.Create(constant.Value)
                    : SerializeConstant(constant.ForSerialization, constant.Value);
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

    private static BsonValue SerializeConstant(IProperty property, object? value)
    {
        var info = BsonSerializerFactory.GetPropertySerializationInfo(property);
        value = BsonValueSerializer.Coerce(property.ClrType, value);
        return BsonValueSerializer.SerializeThroughWriter(info.Serializer, value);
    }
}
```

- [ ] **Step 4: Run tests.** `dotnet test ... --no-build --filter "FullyQualifiedName~MongoAggregationExpressionRendererTests"`. Expected: PASS.

- [ ] **Step 5: Commit.**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoAggregationExpressionRenderer.cs tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoAggregationExpressionRendererTests.cs
git commit -m "EF-329: Add \$expr aggregation-expression renderer"
```

---

### Task 3: Wire the query/`$expr` boundary into `MongoQueryLanguageRenderer`

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoQueryLanguageRenderer.cs`
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoQueryLanguageRendererTests.cs`

**Interfaces:**
- Consumes: `MongoAggregationExpressionRenderer` (Task 2), all node types (Task 1).
- Produces: `MongoQueryLanguageRenderer` now delegates non-query-native comparison subtrees to the agg renderer and wraps them `{ $expr: … }`, while keeping `&&`/`||` at the query level. New private predicate `IsQueryNativePredicate(MongoExpression)`.

- [ ] **Step 1: Write failing tests** in `MongoQueryLanguageRendererTests.cs` (add `int Score`, `string Name` to the test `Customer`):

```csharp
[Fact]
public void Field_to_field_comparison_renders_as_expr()
{
    var age = GetProperty<Customer>("Age");
    var score = GetProperty<Customer>("Score");
    var pred = new MongoBinaryExpression(MongoBinaryOperator.Equal,
        new MongoFieldExpression(age, "Age"), new MongoFieldExpression(score, "Score"));

    var rendered = new MongoQueryLanguageRenderer().Render(pred, new PlaceholderTable());

    Assert.Equal(BsonDocument.Parse("{ $expr: { $eq: ['$Age', '$Score'] } }"), rendered);
}

[Fact]
public void Mixed_and_keeps_indexable_branch_in_query_dialect()
{
    var age = GetProperty<Customer>("Age");
    var score = GetProperty<Customer>("Score");
    // (Age == Score) && (Age > 20)
    var pred = new MongoBinaryExpression(MongoBinaryOperator.AndAlso,
        new MongoBinaryExpression(MongoBinaryOperator.Equal,
            new MongoFieldExpression(age, "Age"), new MongoFieldExpression(score, "Score")),
        new MongoBinaryExpression(MongoBinaryOperator.GreaterThan,
            new MongoFieldExpression(age, "Age"), new MongoConstantExpression(20, age)));

    var rendered = new MongoQueryLanguageRenderer().Render(pred, new PlaceholderTable());

    Assert.Equal(
        BsonDocument.Parse("{ $and: [ { $expr: { $eq: ['$Age', '$Score'] } }, { Age: { $gt: 20 } } ] }"),
        rendered);
}
```

- [ ] **Step 2: Run to confirm failure.** Expected: FAIL (current renderer throws/`RenderComparison` casts `Left` to field and finds a field on the right, mis-rendering).

- [ ] **Step 3: Implement the boundary.** In `MongoQueryLanguageRenderer.cs`:

  1. Add a field `private readonly MongoAggregationExpressionRenderer _aggRenderer = new();`.
  2. Add the classifier:

```csharp
// A predicate subtree is query-native iff it has a correct query-dialect rendering.
// Comparisons are query-native only when exactly one side is a field and the other is a value
// (constant/parameter). Field-to-field and arithmetic operands have no query-dialect form.
private static bool IsQueryNativePredicate(MongoExpression node)
    => node switch
    {
        MongoBinaryExpression { Operator: MongoBinaryOperator.AndAlso or MongoBinaryOperator.OrElse } b
            => IsQueryNativePredicate(b.Left) || IsQueryNativePredicate(b.Right)
               ? true  // handled clause-by-clause; see RenderBinary
               : IsQueryNativePredicate(b.Left) && IsQueryNativePredicate(b.Right),
        MongoBinaryExpression b => IsQueryNativeComparison(b),
        MongoFieldExpression => true,       // bare bool
        MongoUnaryExpression => true,       // Not over bare bool
        MongoInExpression => true,
        MongoRegexExpression => true,
        _ => false
    };

private static bool IsQueryNativeComparison(MongoBinaryExpression b)
    => (b.Left is MongoFieldExpression && b.Right is MongoConstantExpression or MongoParameterExpression);
```

  Note: because the translator always puts the field on the left for query-native comparisons (SP1 `TranslateComparison` mirrors the operator), the check only needs the field-left form. A comparison whose left is *not* a bare field (e.g. an arithmetic `MongoBinaryExpression` or a second field) is not query-native.

  3. Route each predicate node through a dispatcher that wraps non-native leaves. Replace the body of `RenderNode` so it first handles logical nodes clause-by-clause, then for a non-logical node emits either the query-dialect form or an `$expr` wrapper:

```csharp
private BsonValue RenderNode(MongoExpression node, PlaceholderTable placeholders)
    => node switch
    {
        MongoBinaryExpression { Operator: MongoBinaryOperator.AndAlso } a
            => CombineAnd((BsonDocument)RenderNode(a.Left, placeholders), (BsonDocument)RenderNode(a.Right, placeholders)),
        MongoBinaryExpression { Operator: MongoBinaryOperator.OrElse } o
            => CombineOr((BsonDocument)RenderNode(o.Left, placeholders), (BsonDocument)RenderNode(o.Right, placeholders)),
        MongoBinaryExpression comparison when IsQueryNativeComparison(comparison)
            => RenderComparison(comparison, placeholders),
        MongoUnaryExpression unary => RenderUnary(unary, placeholders),
        MongoFieldExpression field => RenderBareField(field, placeholders),
        MongoInExpression inExpr => RenderIn(inExpr, placeholders),               // Task 5
        MongoRegexExpression regex => RenderRegex(regex, placeholders),           // Task 6
        _ => RenderAsExpr(node, placeholders)  // any non-query-native subtree
    };

private BsonDocument RenderAsExpr(MongoExpression node, PlaceholderTable placeholders)
    => new BsonDocument("$expr", _aggRenderer.Render(node, placeholders));
```

  (Remove the now-redundant `RenderBinary` dispatch; `RenderComparison` stays as-is for the query-native case. `RenderIn`/`RenderRegex` are added in Tasks 5–6 — until then leave those two switch arms out so the file compiles, and add them with their tasks.)

- [ ] **Step 4: Run tests.** `dotnet test ... --filter "FullyQualifiedName~MongoQueryLanguageRendererTests"` after rebuild. Expected: PASS, including all pre-existing SP1 renderer tests (no regression).

- [ ] **Step 5: Commit.**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoQueryLanguageRenderer.cs tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoQueryLanguageRendererTests.cs
git commit -m "EF-329: Route non-query-native predicate subtrees through \$expr"
```

---

### Task 4: Nullable equality / inequality (incl. `== null`)

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.cs`
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoExpressionTranslatorTests.cs`
- Functional: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/` (a `QueryModeGate`-style test asserting `NativeOnly` success)

**Interfaces:**
- Consumes: `MongoExpressionTranslator` (SP1), `MongoConstantExpression`/`MongoParameterExpression`.
- Produces: `TranslateComparison` accepts nullable properties for `Equal`/`NotEqual`; `null` operand renders to a `MongoConstantExpression(null, property)`.

- [ ] **Step 1: Write failing translator tests** (add `int? NullableAge` and `string? Name` to the test model):

```csharp
[Fact]
public void Translates_nullable_equality()
{
    var translator = TranslatorFor<Customer>();
    Expression<Func<Customer, bool>> predicate = c => c.NullableAge == 5;
    Assert.True(translator.TryTranslate(predicate.Body, out var result));
    var binary = Assert.IsType<MongoBinaryExpression>(result);
    Assert.Equal(MongoBinaryOperator.Equal, binary.Operator);
}

[Fact]
public void Translates_is_null()
{
    var translator = TranslatorFor<Customer>();
    Expression<Func<Customer, bool>> predicate = c => c.Name == null;
    Assert.True(translator.TryTranslate(predicate.Body, out var result));
    var binary = Assert.IsType<MongoBinaryExpression>(result);
    var constant = Assert.IsType<MongoConstantExpression>(binary.Right);
    Assert.Null(constant.Value);
}
```

(Use the existing `TranslatorFor<T>()`/model helper in `MongoExpressionTranslatorTests.cs`; if not present, add one that builds `new MongoExpressionTranslator(entityType)`.)

- [ ] **Step 2: Run to confirm failure.** Expected: FAIL (`TryTranslate` returns false — SP1's `property.IsNullable` guard rejects both).

- [ ] **Step 3: Implement.** In `MongoExpressionTranslator.TranslateComparison`, remove the nullable-equality fallback guard:

```csharp
// REMOVE this SP1 block:
// if (be.NodeType is ExpressionType.Equal or ExpressionType.NotEqual && property.IsNullable)
//     return null;
```

  In `TranslateValue`, accept a `null` constant (a `ConstantExpression` with `Value == null` already returns `new MongoConstantExpression(null, property)`, which the query renderer serializes to `BsonNull` via the property serializer — verify against driver output). Also relax the bare-bool / `Not` nullable guards *only* for the `== true`/`== false` cases that arrive as comparisons (leave bare nullable-bool member access rejected — a bare nullable bool has three-valued semantics the query dialect does not match; keep that fallback and add a test asserting it still falls back).

- [ ] **Step 4: Add the null-rendering renderer test** in `MongoQueryLanguageRendererTests.cs`:

```csharp
[Fact]
public void Renders_is_null_as_bare_null()
{
    var name = GetProperty<Customer>("Name");
    var pred = new MongoBinaryExpression(MongoBinaryOperator.Equal,
        new MongoFieldExpression(name, "Name"), new MongoConstantExpression(null, name));
    var rendered = new MongoQueryLanguageRenderer().Render(pred, new PlaceholderTable());
    Assert.Equal(BsonDocument.Parse("{ Name: null }"), rendered);
}
```

- [ ] **Step 5: Add a `NativeOnly` functional test** proving nullable equality now goes native (pattern: run under `MongoQueryMode.NativeOnly` and assert it does not throw). Follow `QueryModeGateTests.cs`.

- [ ] **Step 6: Run tests + commit.**

```bash
dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build --filter "FullyQualifiedName~MongoExpressionTranslatorTests|FullyQualifiedName~MongoQueryLanguageRendererTests"
git add -A && git commit -m "EF-329: Native nullable equality and IS NULL predicates"
```

---

### Task 5: Collection `Contains` → `$in` / `$nin` (with array parameter binding)

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.cs`
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoQueryLanguageRenderer.cs` (add `RenderIn`)
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/PlaceholderTable.cs` (array placeholder)
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoPipelineFactory.cs` (array substitution)
- Test: translator + renderer + `MongoPipelineFactoryTests.cs` + a `NativeOnly` functional test

**Interfaces:**
- Consumes: `MongoInExpression` (Task 1), `PlaceholderTable`, `BsonSerializerFactory`.
- Produces:
  - Translator recognizes `Enumerable.Contains`, `List<T>.Contains`, `ICollection<T>.Contains` where the argument is a field and the receiver is a constant/parameter collection → `MongoInExpression`.
  - `PlaceholderTable.CreateArrayPlaceholder(string name, IBsonSerializer elementSerializer)` records an *array* entry.
  - `MongoPipelineFactory` serializes array entries element-wise into a `BsonArray`.
  - `MongoQueryLanguageRenderer.RenderIn` emits `{ field: { $in: [...] } }` / `{ $nin: [...] }`.

- [ ] **Step 1: Write failing renderer test** (inline constant collection):

```csharp
[Fact]
public void Renders_in_for_inline_collection()
{
    var age = GetProperty<Customer>("Age");
    var expr = new MongoInExpression(
        new MongoFieldExpression(age, "Age"),
        new MongoConstantExpression(new[] { 1, 2, 3 }, age),
        negated: false);
    var rendered = new MongoQueryLanguageRenderer().Render(expr, new PlaceholderTable());
    Assert.Equal(BsonDocument.Parse("{ Age: { $in: [1, 2, 3] } }"), rendered);
}
```

- [ ] **Step 2: Confirm failure** (no `RenderIn`; `MongoInExpression` hits the `RenderAsExpr` arm or throws).

- [ ] **Step 3: Implement `RenderIn`** in `MongoQueryLanguageRenderer.cs` and add the `MongoInExpression` arm to `RenderNode` (from Task 3):

```csharp
private BsonDocument RenderIn(MongoInExpression inExpr, PlaceholderTable placeholders)
{
    var op = inExpr.Negated ? "$nin" : "$in";
    var array = RenderInValues(inExpr.Values, placeholders);
    return new BsonDocument(inExpr.Field.ElementName, new BsonDocument(op, array));
}

private BsonValue RenderInValues(MongoExpression values, PlaceholderTable placeholders)
{
    switch (values)
    {
        case MongoConstantExpression { Value: System.Collections.IEnumerable items } constant:
        {
            var array = new BsonArray();
            foreach (var item in items)
                array.Add(ToBsonValue(constant.ForSerialization!, item));
            return array;
        }
        case MongoParameterExpression parameter:
        {
            var info = BsonSerializerFactory.GetPropertySerializationInfo(parameter.ForSerialization!);
            return placeholders.CreateArrayPlaceholder(parameter.Name, info.Serializer);
        }
        default:
            throw new NativeTranslationNotSupportedException("Unsupported $in values node.");
    }
}
```

  (`ToBsonValue` is the existing private helper; make it accessible to `RenderInValues`.)

- [ ] **Step 4: Add array placeholder to `PlaceholderTable.cs`.** Extend the entry tuple with an `IsArray` flag:

```csharp
private readonly List<(string Name, IBsonSerializer? Serializer, bool IsArray)> _entries = [];
public IReadOnlyList<(string Name, IBsonSerializer? Serializer, bool IsArray)> Entries => _entries;

public BsonValue CreatePlaceholder(string parameterName, IBsonSerializer? serializer)
{
    var index = _entries.Count;
    _entries.Add((parameterName, serializer, false));
    return new BsonDocument(SentinelKey, new BsonInt32(index));
}

public BsonValue CreateArrayPlaceholder(string parameterName, IBsonSerializer elementSerializer)
{
    var index = _entries.Count;
    _entries.Add((parameterName, elementSerializer, true));
    return new BsonDocument(SentinelKey, new BsonInt32(index));
}
```

- [ ] **Step 5: Handle array entries in `MongoPipelineFactory.SerializeParameter`:**

```csharp
var (name, serializer, isArray) = _placeholders.Entries[index];
// ... existing missing-key guard ...
if (isArray)
{
    var array = new BsonArray();
    foreach (var element in (System.Collections.IEnumerable)rawValue!)
    {
        var coerced = BsonValueSerializer.Coerce(serializer!.ValueType, element);
        array.Add(BsonValueSerializer.SerializeThroughWriter(serializer, coerced));
    }
    return array;
}
```

- [ ] **Step 6: Recognize `Contains` in the translator.** Add a `MethodCallExpression` case to `TranslateNode` that matches `Contains` (receiver = constant/parameter collection, single argument = a resolvable member) and `!Contains` (via the existing `Not` arm wrapping it — set `Negated`). Return `new MongoInExpression(field, valuesNode, negated)`. Reject when the argument is not a bare field or the collection element type mismatches the property.

- [ ] **Step 7: Add translator test, an array-binding `MongoPipelineFactoryTests` test (two executions with different collections produce different arrays from one template), and a `NativeOnly` functional test.**

- [ ] **Step 8: Run tests + commit.**

```bash
dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build --filter "FullyQualifiedName~NativeTranslation"
git add -A && git commit -m "EF-329: Native collection Contains via \$in/\$nin with array binding"
```

---

### Task 6: String `StartsWith` / `EndsWith` / `Contains` → `$regex`

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.cs`
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoQueryLanguageRenderer.cs` (add `RenderRegex`)
- Test: translator + renderer + `NativeOnly` functional

**Interfaces:**
- Consumes: `MongoRegexExpression`/`MongoRegexKind` (Task 1).
- Produces: translator recognizes `string.StartsWith(string)`, `EndsWith(string)`, `Contains(string)` (single-arg, ordinal) on a field receiver → `MongoRegexExpression`; renderer emits `{ field: { $regex: "<pattern>" } }` with the pattern anchored per kind and the literal escaped.

- [ ] **Step 1: Write failing renderer tests** for the three kinds (constant term):

```csharp
[Fact]
public void Renders_starts_with_as_anchored_regex()
{
    var name = GetProperty<Customer>("Name");
    var expr = new MongoRegexExpression(new MongoFieldExpression(name, "Name"),
        MongoRegexKind.StartsWith, new MongoConstantExpression("A.b", name), negated: false);
    var rendered = new MongoQueryLanguageRenderer().Render(expr, new PlaceholderTable());
    // Regex metacharacters in the literal are escaped; StartsWith anchors with ^.
    Assert.Equal(BsonDocument.Parse("{ Name: { $regex: '^A\\\\.b' } }"), rendered);
}
```

  (EndsWith → `A\.b$`; Contains → `A\.b`. Match the driver-LINQ v3 rendering for these methods — confirm the exact `$regex`/`$options` shape the driver emits and mirror it; adjust the expected strings to match before implementing.)

- [ ] **Step 2: Confirm failure.**

- [ ] **Step 3: Implement `RenderRegex`** and add the `MongoRegexExpression` arm to `RenderNode`:

```csharp
private BsonDocument RenderRegex(MongoRegexExpression regex, PlaceholderTable placeholders)
{
    // Term must be a constant for a baked pattern; a parameterized term still binds a
    // placeholder but the anchor/escape must be applied at bind time (see note).
    if (regex.Term is not MongoConstantExpression { Value: string literal })
        throw new NativeTranslationNotSupportedException("Only constant regex terms are baked; parameterized handled separately.");

    var escaped = System.Text.RegularExpressions.Regex.Escape(literal);
    var pattern = regex.Kind switch
    {
        MongoRegexKind.StartsWith => "^" + escaped,
        MongoRegexKind.EndsWith => escaped + "$",
        MongoRegexKind.Contains => escaped,
        _ => throw new NativeTranslationNotSupportedException($"Unsupported regex kind '{regex.Kind}'.")
    };

    var body = new BsonDocument("$regex", pattern);
    return regex.Negated
        ? new BsonDocument(regex.Field.ElementName, new BsonDocument("$not", body))
        : new BsonDocument(regex.Field.ElementName, body);
}
```

  Note: for a *parameterized* search term the pattern must be built at bind time (escape + anchor around the runtime value). Handle by recording a placeholder with a regex-building serializer, or — if that proves complex — restrict this task to constant terms and let parameterized string-method predicates fall back (document the limitation; still zero-regression). Decide during implementation; the spec's acceptance only requires the constant path plus fallback for the rest.

- [ ] **Step 4: Recognize the string methods in the translator.** Add `MethodCallExpression` cases for `string.StartsWith`/`EndsWith`/`Contains` (single string arg, receiver resolves to a string field) → `MongoRegexExpression`. `!s.StartsWith(...)` sets `Negated` via the `Not` arm.

- [ ] **Step 5: Add translator tests + a `NativeOnly` functional test; run + commit.**

```bash
dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build --filter "FullyQualifiedName~NativeTranslation"
git add -A && git commit -m "EF-329: Native string StartsWith/EndsWith/Contains via \$regex"
```

---

### Task 7: Field-to-field and arithmetic comparisons (translator acceptance)

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.cs`
- Test: translator + `NativeOnly` functional

**Interfaces:**
- Consumes: arithmetic `MongoBinaryOperator`s (Task 1), the `$expr` boundary (Task 3).
- Produces: `TranslateComparison` accepts a member on *both* sides (field-to-field) and arithmetic sub-expressions (`+`/`-`/`*`/`/`/`%`) as comparison operands, producing `MongoBinaryExpression` trees the renderer routes to `$expr`.

- [ ] **Step 1: Write failing translator tests:**

```csharp
[Fact]
public void Translates_field_to_field_comparison()
{
    var translator = TranslatorFor<Customer>();
    Expression<Func<Customer, bool>> predicate = c => c.Age == c.Score;
    Assert.True(translator.TryTranslate(predicate.Body, out var result));
    var binary = Assert.IsType<MongoBinaryExpression>(result);
    Assert.IsType<MongoFieldExpression>(binary.Left);
    Assert.IsType<MongoFieldExpression>(binary.Right);
}

[Fact]
public void Translates_arithmetic_operand()
{
    var translator = TranslatorFor<Customer>();
    Expression<Func<Customer, bool>> predicate = c => c.Age + c.Score > 5;
    Assert.True(translator.TryTranslate(predicate.Body, out var result));
    var cmp = Assert.IsType<MongoBinaryExpression>(result);
    Assert.Equal(MongoBinaryOperator.GreaterThan, cmp.Operator);
    Assert.IsType<MongoBinaryExpression>(cmp.Left); // the $add subtree
}
```

- [ ] **Step 2: Confirm failure** (SP1 requires exactly one member side and rejects arithmetic operands).

- [ ] **Step 3: Implement.** Refactor `TranslateComparison` so each operand is translated by a shared `TranslateOperand(Expression)` that yields a `MongoExpression` for: a member (→ `MongoFieldExpression`), a constant/parameter (→ value node), or an arithmetic `BinaryExpression` (→ `MongoBinaryExpression` with the Add/Subtract/Multiply/Divide/Modulo operator, operands recursively translated). Build the comparison from the two translated operands without the "must be field on exactly one side" restriction. Keep the numeric-cast guard for the *query-native* path only; arithmetic/field-to-field always route to `$expr`, so cast concerns there follow `$expr` numeric semantics — add tests confirming the emitted `$expr` matches driver output for a representative cast case, else keep that case falling back.

- [ ] **Step 4: Add a `NativeOnly` functional test** asserting `c => c.Age == c.Score` and `c => c.Age + c.Score > 5` succeed natively and produce the expected `$expr` MQL. Run + commit.

```bash
dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build --filter "FullyQualifiedName~NativeTranslation"
git add -A && git commit -m "EF-329: Native field-to-field and arithmetic comparisons via \$expr"
```

---

### Task 8: Docs, spec-suite MQL reconciliation, and full regression run

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md`
- Modify: overridden spec tests under `tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/` whose MQL changed
- Modify: `docs/superpowers/specs/2026-06-23-native-query-provider-*.md` sub-project-2 status note (mark in progress/done as appropriate)

- [ ] **Step 1: Update `Query/AGENTS.md`** — document the dual-dialect renderer (`MongoQueryLanguageRenderer` + `MongoAggregationExpressionRenderer`), the index-first per-subtree boundary, that **the renderer (not the lowerer) owns dialect choice** (the documented refinement), and add the new operators to the native-slice description. Update the "As-built scope" paragraph.

- [ ] **Step 2: Run the full spec suite in Native mode, EF10.** `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build --filter "FullyQualifiedName~Query"`. For each newly-native operator whose overridden `AssertMql` expectation changed (moved off driver-LINQ shape), update the expected MQL to the native output. Do **not** weaken assertions to hide a wrong shape — verify each against a real run.

- [ ] **Step 3: Run the `NativeOnly` coverage instrument.** `MONGODB_EF_NATIVE_ONLY=1 dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build --filter "FullyQualifiedName~Query"`. Confirm the pass set **grew** by this tranche and nothing regressed.

- [ ] **Step 4: Full regression, EF10 and EF8.** Run the whole suite for both configurations (or invoke the `/test-all` skill). Expected: all green in `Native` mode.

- [ ] **Step 5: Commit.**

```bash
git add -A && git commit -m "EF-329: Update AGENTS.md and reconcile spec-suite MQL for predicate breadth"
```

---

## Self-review

- **Spec coverage:** `$expr` renderer (Task 2) + boundary (Task 3); nullable equality/`==null` (Task 4); `Contains`→`$in`/`$nin` + array binding (Task 5); string methods→`$regex` (Task 6); field-to-field + arithmetic (Task 7); index-first assertions, AGENTS.md refinement, spec MQL reconciliation, EF8/EF10 regression (Tasks 3/8). Cross-dialect binding: Task 5 (arrays) + reuse elsewhere. All spec sections map to a task.
- **Deferred items** (computed long tail) are explicitly out of scope in the spec and absent here — intentional, not a gap.
- **Type consistency:** `MongoAggregationExpressionRenderer.Render`, `PlaceholderTable.CreateArrayPlaceholder`, `MongoInExpression`/`MongoRegexExpression`/`MongoRegexKind`, and the `Entries` tuple (now 3-arity) are used consistently across tasks; every consumer references the shape defined in its producing task.
- **Known implementation decision points flagged inline** (parameterized regex term in Task 6; representative cast handling in Task 7) — each has a documented zero-regression fallback so a task never blocks on an open question.
