# Native Conditional Expression and Date-Part Translation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the native (non-driver-LINQ) MongoDB EF Core query translator support (1) conditional/ternary expressions and (2) `DateTime`/`DateTimeOffset` member access and date-part extraction, as computed values usable in projections, sort keys, and arithmetic operands — closing `Query/AGENTS.md`'s documented "computed long tail" gap for these two shapes.

**Architecture:** Two new dialect-neutral IR node kinds (`MongoConditionalExpression`, `MongoDatePartExpression`) plus one supporting node (`MongoDateTimeOffsetLocalExpression`) are added to `Query/Expressions/`. `MongoExpressionTranslator.TranslateOperand` — the single existing choke point for every computed value (projection leaf, sort key, arithmetic operand) — gets two new recognizer cases that build these nodes. `MongoAggregationExpressionRenderer` gets matching `Render`/`CanRender` arms. `NativeProjectionBinder` gets small, additive wiring so a projection leaf of either new shape is admitted; `MongoProjectionBindingExpressionVisitor` needs one new case for `ConditionalExpression` only — a date-part member chain already rides the visitor's pre-existing unconditional bare-`MemberExpression` case. No new query-dialect (`$match`) support is added — both shapes always render via `$expr`, exactly like the existing `MongoConvertExpression`.

**Tech Stack:** C#, MongoDB Aggregation Pipeline (`$cond`, `$dateAdd`, `$year`/`$month`/`$dayOfMonth`/`$hour`/`$minute`/`$second`/`$millisecond`/`$dayOfWeek`/`$dayOfYear`, `$dateTrunc`), xUnit, EF Core query pipeline internals.

**Spec:** `docs/superpowers/specs/2026-08-27-native-conditional-datepart-design.md`

## Global Constraints

- General `ConditionalExpression` support (any boolean `Test`, any computed-value branches) — not narrowed to a null-check-only shape.
- Full date-part member set mirrored from `MongoEFToLinqTranslatingExpressionVisitor.DateTimeOffsetComponentMembers`: `DateTime`, `LocalDateTime`, `UtcDateTime`, `Date`, `Year`, `Month`, `Day`, `Hour`, `Minute`, `Second`, `Millisecond`, `DayOfWeek`, `DayOfYear` — **except `TimeOfDay`, which is explicitly declined** (resolved from the spec's open question: no single clean MQL composition, and a decline falls back gracefully to the already-working driver-LINQ bridge).
- Both plain `DateTime` and `DateTimeOffset` properties are in scope. `DateTimeOffset` is stored by this provider as a subdocument with `DateTime` (UTC) and `Offset` (minutes) fields (`Storage`/`Serializers` — `DateTimeOffsetSerializer`, `BsonType.Document`).
- A date-part member chain's ultimate receiver must resolve to a plain stored field (via `TryResolveMember`, which already peels a `.Value` hop) — a receiver that is itself an arbitrary computed value (e.g. a conditional branch) has no MQL sub-field-access form and must decline cleanly, not attempt a wrong translation. A chain of *date-member* hops (e.g. `.Value.DateTime.Date`) is fine and must compose correctly — this is the motivating test's exact shape.
- MongoDB's `$dayOfWeek` returns 1 (Sunday)...7 (Saturday); .NET's `DayOfWeek` enum is 0 (Sunday)...6 (Saturday). The renderer must subtract 1 to match .NET numbering — this is a required correctness transform, not optional.
- Neither new node type may ever be admitted by `MongoQueryLanguageRenderer.IsQueryDialectRenderable` (no query-dialect form exists) or negated by `MongoExpressionNegator` (must decline, not mis-negate) — same rule already enforced for `MongoConvertExpression`.
- Zero `Passed → Failed` regressions across the full EF8/EF9/EF10 unit/functional/spec suites (this repo's standing convention for every native-translation change).
- No ticket exists yet for this work — do not reference a JIRA key in commit messages until one is filed; use plain descriptive commit messages (see each task's commit step).

---

## File Map

| File | Change |
|---|---|
| `src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoConditionalExpression.cs` | New |
| `src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoDatePartExpression.cs` | New (also declares the `MongoDatePart` enum) |
| `src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoDateTimeOffsetLocalExpression.cs` | New |
| `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.cs` | Modify `TranslateOperand`; add `TryTranslateDateTimeMember` and `DatePartsByMemberName` |
| `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoAggregationExpressionRenderer.cs` | Modify `Render`/`CanRender` switches |
| `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoQueryLanguageRenderer.cs` | Modify `IsQueryDialectRenderable` (explicit decline arms, documentation-only — catch-all already covers it) |
| `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeProjectionBinder.cs` | Modify `TryTranslateLeaf`'s generic computed-leaf gate |
| `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoProjectionBindingExpressionVisitor.cs` | Modify `Visit`'s switch — one new case for `ConditionalExpression` only |
| `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoConditionalAndDateTimeTranslatorTests.cs` | New |
| `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/NativeProjectionBinderConditionalAndDateTimeTests.cs` | New |
| `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeConditionalAndDateTimeTests.cs` | New |
| `tests/MongoDB.EntityFrameworkCore.SpecificationTests/Mapping/BuiltInDataTypesMongoTest.cs` | Modify (the motivating test's `NativeOnly`-aware override, or removal if it now genuinely always passes) |

---

### Task 1: New IR node types

**Files:**
- Create: `src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoConditionalExpression.cs`
- Create: `src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoDatePartExpression.cs`
- Create: `src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoDateTimeOffsetLocalExpression.cs`
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/Expressions/MongoConditionalAndDatePartExpressionTests.cs`

**Interfaces:**
- Produces: `MongoConditionalExpression(MongoExpression Test, MongoExpression IfTrue, MongoExpression IfFalse)`, `.Type` = `IfTrue.Type`.
- Produces: `MongoDatePart` enum: `Year, Month, Day, Hour, Minute, Second, Millisecond, DayOfWeek, DayOfYear, Date`.
- Produces: `MongoDatePartExpression(MongoExpression Operand, MongoDatePart Part)`, `.Type` = `typeof(int)` except `Date` → `typeof(DateTime)` and `DayOfWeek` → `typeof(DayOfWeek)`.
- Produces: `MongoDateTimeOffsetLocalExpression(MongoFieldExpression Operand)` — **`Operand` is typed as `MongoFieldExpression`, not the general `MongoExpression`**, because the renderer must dot `.DateTime`/`.Offset` onto its element path, which is only valid MQL when the operand is a plain field reference. `.Type` = `typeof(DateTime)`.

- [ ] **Step 1: Write the failing test**

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
using MongoDB.EntityFrameworkCore.Query.Expressions;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query.Expressions;

public class MongoConditionalAndDatePartExpressionTests
{
    [Fact]
    public void MongoConditionalExpression_type_is_the_IfTrue_branch_type()
    {
        var test = new MongoConstantExpression(true, forSerialization: null);
        var ifTrue = new MongoConstantExpression(1, forSerialization: null);
        var ifFalse = new MongoConstantExpression(2, forSerialization: null);

        var conditional = new MongoConditionalExpression(test, ifTrue, ifFalse);

        Assert.Same(test, conditional.Test);
        Assert.Same(ifTrue, conditional.IfTrue);
        Assert.Same(ifFalse, conditional.IfFalse);
        Assert.Equal(typeof(int), conditional.Type);
    }

    [Theory]
    [InlineData(MongoDatePart.Year, typeof(int))]
    [InlineData(MongoDatePart.Month, typeof(int))]
    [InlineData(MongoDatePart.Day, typeof(int))]
    [InlineData(MongoDatePart.Hour, typeof(int))]
    [InlineData(MongoDatePart.Minute, typeof(int))]
    [InlineData(MongoDatePart.Second, typeof(int))]
    [InlineData(MongoDatePart.Millisecond, typeof(int))]
    [InlineData(MongoDatePart.DayOfYear, typeof(int))]
    [InlineData(MongoDatePart.Date, typeof(DateTime))]
    [InlineData(MongoDatePart.DayOfWeek, typeof(DayOfWeek))]
    public void MongoDatePartExpression_type_matches_the_part(MongoDatePart part, Type expectedType)
    {
        var operand = new MongoConstantExpression(DateTime.UtcNow, forSerialization: null);

        var datePart = new MongoDatePartExpression(operand, part);

        Assert.Same(operand, datePart.Operand);
        Assert.Equal(part, datePart.Part);
        Assert.Equal(expectedType, datePart.Type);
    }

    [Fact]
    public void MongoDateTimeOffsetLocalExpression_type_is_DateTime()
    {
        var field = new MongoFieldExpression(property: null!, "DateTimeOffsetField");

        var local = new MongoDateTimeOffsetLocalExpression(field);

        Assert.Same(field, local.Operand);
        Assert.Equal(typeof(DateTime), local.Type);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --filter "FullyQualifiedName~MongoConditionalAndDatePartExpressionTests"`
Expected: FAIL to compile — `MongoConditionalExpression`, `MongoDatePart`, `MongoDatePartExpression`, `MongoDateTimeOffsetLocalExpression` don't exist yet. (`MongoFieldExpression`'s constructor accepting a null `IProperty` for a throwaway test double: check its actual signature first — it takes `(IProperty property, string elementName)`; passing `null!` is fine since this test never calls a method that dereferences `Property`.)

- [ ] **Step 3: Write the implementation**

`src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoConditionalExpression.cs`:

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

namespace MongoDB.EntityFrameworkCore.Query.Expressions;

/// <summary>
/// A ternary conditional (<c>test ? ifTrue : ifFalse</c>), rendered in the aggregation-expression dialect as
/// <c>$cond</c>.
/// </summary>
/// <remarks>
/// <see cref="Test"/> is always rendered via <c>MongoAggregationExpressionRenderer.Render</c> directly, never
/// via <c>MongoQueryLanguageRenderer.RenderNode</c>'s query/aggregation dual-dialect dispatch — a
/// <c>$cond.if</c> lives inside <c>$project</c>'s expression context, where the query (<c>$match</c>) dialect
/// is never valid, the same rule that already governs everything nested inside <c>$expr</c>.
/// <para>
/// This node is deliberately NOT admitted by <c>MongoQueryLanguageRenderer.IsQueryDialectRenderable</c> — it
/// has no query-dialect form, and <c>$expr</c> (which is what a native <c>$project</c>/<c>$match</c> would
/// need to wrap it in) is a hard server error inside <c>$elemMatch</c>.
/// </para>
/// </remarks>
internal sealed class MongoConditionalExpression(MongoExpression test, MongoExpression ifTrue, MongoExpression ifFalse)
    : MongoExpression
{
    /// <summary>The boolean condition.</summary>
    public MongoExpression Test { get; } = test;

    /// <summary>The value when <see cref="Test"/> is true.</summary>
    public MongoExpression IfTrue { get; } = ifTrue;

    /// <summary>The value when <see cref="Test"/> is false.</summary>
    public MongoExpression IfFalse { get; } = ifFalse;

    /// <inheritdoc />
    public override Type Type { get; } = ifTrue.Type;
}
```

`src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoDatePartExpression.cs`:

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

namespace MongoDB.EntityFrameworkCore.Query.Expressions;

/// <summary>
/// The date-part components this provider translates natively. <c>TimeOfDay</c> is deliberately absent — MQL
/// has no single clean composition for it, and a decline falls back gracefully to the existing driver-LINQ
/// bridge (<c>MongoEFToLinqTranslatingExpressionVisitor</c>), which already handles it.
/// </summary>
internal enum MongoDatePart
{
    Year,
    Month,
    Day,
    Hour,
    Minute,
    Second,
    Millisecond,
    DayOfWeek,
    DayOfYear,
    Date
}

/// <summary>
/// Extracts one <see cref="MongoDatePart"/> component from a datetime-valued <see cref="Operand"/>, rendered
/// in the aggregation-expression dialect as the matching MQL date operator (<c>$year</c>, <c>$month</c>,
/// <c>$dayOfMonth</c>, <c>$hour</c>, <c>$minute</c>, <c>$second</c>, <c>$millisecond</c>, <c>$dayOfWeek</c>,
/// <c>$dayOfYear</c>, or <c>$dateTrunc</c> for <see cref="MongoDatePart.Date"/>).
/// </summary>
/// <remarks>
/// <see cref="Operand"/> is deliberately typed as the general <see cref="MongoExpression"/>, not a bare field:
/// every one of MQL's date-extraction operators accepts any date-valued EXPRESSION, not just a field
/// reference, which is what lets this node wrap a <see cref="MongoDateTimeOffsetLocalExpression"/> (the
/// reconstructed local time for a <c>DateTimeOffset</c> source) as well as a plain <c>DateTime</c> field.
/// <para>
/// Like <see cref="MongoConvertExpression"/>, this node has no query-dialect form and must never be admitted
/// by <c>MongoQueryLanguageRenderer.IsQueryDialectRenderable</c>.
/// </para>
/// </remarks>
internal sealed class MongoDatePartExpression(MongoExpression operand, MongoDatePart part) : MongoExpression
{
    /// <summary>The datetime-valued expression to extract a component from.</summary>
    public MongoExpression Operand { get; } = operand;

    /// <summary>Which component to extract.</summary>
    public MongoDatePart Part { get; } = part;

    /// <inheritdoc />
    public override Type Type { get; } = part switch
    {
        MongoDatePart.Date => typeof(DateTime),
        MongoDatePart.DayOfWeek => typeof(DayOfWeek),
        _ => typeof(int)
    };
}
```

`src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoDateTimeOffsetLocalExpression.cs`:

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

namespace MongoDB.EntityFrameworkCore.Query.Expressions;

/// <summary>
/// Reconstructs the "local" <see cref="DateTime"/> from a stored <c>DateTimeOffset</c> field — this provider
/// stores <c>DateTimeOffset</c> as a subdocument with <c>DateTime</c> (UTC) and <c>Offset</c> (minutes)
/// fields (<c>Storage/Serializers.BsonSerializerFactory</c> — the driver's own <c>DateTimeOffsetSerializer</c>,
/// <c>BsonType.Document</c>), so the local value is the sum of the two, rendered via <c>$dateAdd</c>.
/// </summary>
/// <remarks>
/// This is the same reconstruction <c>MongoEFToLinqTranslatingExpressionVisitor</c> already performs for the
/// driver-LINQ bridge (EF-218, working around CSHARP-5296), re-expressed as a native aggregation-expression
/// node.
/// <para>
/// <see cref="Operand"/> is deliberately typed as <see cref="MongoFieldExpression"/>, not the general
/// <see cref="MongoExpression"/> — the renderer dots <c>.DateTime</c>/<c>.Offset</c> onto its element path,
/// which is only valid MQL when the operand is a plain field reference; there is no way to sub-field-access an
/// arbitrary COMPUTED document value without <c>$getField</c>, which this node does not attempt.
/// </para>
/// </remarks>
internal sealed class MongoDateTimeOffsetLocalExpression(MongoFieldExpression operand) : MongoExpression
{
    /// <summary>The <c>DateTimeOffset</c>-typed field to reconstruct the local time from.</summary>
    public MongoFieldExpression Operand { get; } = operand;

    /// <inheritdoc />
    public override Type Type { get; } = typeof(DateTime);
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10" && dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build --filter "FullyQualifiedName~MongoConditionalAndDatePartExpressionTests"`
Expected: PASS, 10 tests.

- [ ] **Step 5: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoConditionalExpression.cs \
        src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoDatePartExpression.cs \
        src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoDateTimeOffsetLocalExpression.cs \
        tests/MongoDB.EntityFrameworkCore.UnitTests/Query/Expressions/MongoConditionalAndDatePartExpressionTests.cs
git commit -m "Add MongoConditionalExpression, MongoDatePartExpression, MongoDateTimeOffsetLocalExpression IR nodes"
```

---

### Task 2: Renderer arms (`MongoAggregationExpressionRenderer`)

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoAggregationExpressionRenderer.cs:53-73` (the `Render` switch) and `:103-134` (the `CanRender` switch)
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoAggregationExpressionRendererDatePartTests.cs`

**Interfaces:**
- Consumes: `MongoConditionalExpression`, `MongoDatePartExpression`, `MongoDateTimeOffsetLocalExpression` from Task 1.
- Produces: `MongoAggregationExpressionRenderer.Render`/`CanRender` now handle all three node kinds.

- [ ] **Step 1: Write the failing test**

First, find the existing unit-test pattern for this renderer:

```bash
grep -n "class MongoAggregationExpressionRendererTests" -A 40 tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoAggregationExpressionRendererTests.cs | head -60
```

Then add a new test file using the same `PlaceholderTable`/`Render`/`CanRender` call pattern that file uses (construct a `PlaceholderTable`, call `MongoAggregationExpressionRenderer.Render(node, placeholders)`, assert on the resulting `BsonDocument`/`BsonValue` via `.ToJson()` or direct traversal):

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
using Microsoft.EntityFrameworkCore.Metadata;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Query.Expressions;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query.NativeTranslation;

public class MongoAggregationExpressionRendererDatePartTests
{
    // Mirrors MongoAggregationExpressionRendererTests' own Customer/GetProperty<T> pattern exactly, so a
    // MongoFieldExpression built here carries a real IProperty rather than a null double — some renderer
    // paths (e.g. AllFieldsDefaultSerialized, reached transitively from CanRender for other node kinds in
    // this same file) do read Property, so a null double would throw a NullReferenceException instead of the
    // renderer's own exception type, masking the actual thing under test.
    private class Row
    {
        public ObjectId Id { get; set; }
        public DateTime Occurred { get; set; }
        public DateTimeOffset OccurredOffset { get; set; }
    }

    private static IProperty GetProperty(string propertyName)
    {
        using var db = SingleEntityDbContext.Create<Row>();
        return db.Model.FindEntityType(typeof(Row))!.FindProperty(propertyName)!;
    }

    private static MongoFieldExpression Field(string propertyName, string elementName)
        => new(GetProperty(propertyName), elementName);

    [Fact]
    public void Conditional_renders_as_cond()
    {
        var placeholders = new PlaceholderTable();
        var node = new MongoConditionalExpression(
            new MongoConstantExpression(true, forSerialization: null),
            new MongoConstantExpression(1, forSerialization: null),
            new MongoConstantExpression(2, forSerialization: null));

        var rendered = MongoAggregationExpressionRenderer.Render(node, placeholders);

        Assert.Equal(
            new BsonDocument("$cond", new BsonDocument { { "if", true }, { "then", 1 }, { "else", 2 } }),
            rendered);
    }

    [Fact]
    public void DateTimeOffsetLocal_renders_as_dateAdd_of_DateTime_and_Offset_subfields()
    {
        var placeholders = new PlaceholderTable();
        var node = new MongoDateTimeOffsetLocalExpression(Field("OccurredOffset", "Occurred"));

        var rendered = MongoAggregationExpressionRenderer.Render(node, placeholders);

        Assert.Equal(
            new BsonDocument("$dateAdd", new BsonDocument
            {
                { "startDate", "$Occurred.DateTime" },
                { "unit", "minute" },
                { "amount", "$Occurred.Offset" }
            }),
            rendered);
    }

    [Theory]
    [InlineData(MongoDatePart.Year, "$year")]
    [InlineData(MongoDatePart.Month, "$month")]
    [InlineData(MongoDatePart.Day, "$dayOfMonth")]
    [InlineData(MongoDatePart.Hour, "$hour")]
    [InlineData(MongoDatePart.Minute, "$minute")]
    [InlineData(MongoDatePart.Second, "$second")]
    [InlineData(MongoDatePart.Millisecond, "$millisecond")]
    [InlineData(MongoDatePart.DayOfYear, "$dayOfYear")]
    public void DatePart_renders_as_the_matching_operator(MongoDatePart part, string operatorName)
    {
        var placeholders = new PlaceholderTable();
        var node = new MongoDatePartExpression(Field("Occurred", "Occurred"), part);

        var rendered = MongoAggregationExpressionRenderer.Render(node, placeholders);

        Assert.Equal(new BsonDocument(operatorName, "$Occurred"), rendered);
    }

    [Fact]
    public void DatePart_DayOfWeek_subtracts_one_to_match_dotnet_numbering()
    {
        // MongoDB's $dayOfWeek returns 1 (Sunday)..7 (Saturday); .NET's DayOfWeek is 0 (Sunday)..6 (Saturday).
        var placeholders = new PlaceholderTable();
        var node = new MongoDatePartExpression(Field("Occurred", "Occurred"), MongoDatePart.DayOfWeek);

        var rendered = MongoAggregationExpressionRenderer.Render(node, placeholders);

        Assert.Equal(
            new BsonDocument("$subtract", new BsonArray { new BsonDocument("$dayOfWeek", "$Occurred"), 1 }),
            rendered);
    }

    [Fact]
    public void DatePart_Date_renders_as_dateTrunc_day()
    {
        var placeholders = new PlaceholderTable();
        var node = new MongoDatePartExpression(Field("Occurred", "Occurred"), MongoDatePart.Date);

        var rendered = MongoAggregationExpressionRenderer.Render(node, placeholders);

        Assert.Equal(
            new BsonDocument("$dateTrunc", new BsonDocument { { "date", "$Occurred" }, { "unit", "day" } }),
            rendered);
    }

    [Fact]
    public void CanRender_is_true_for_all_three_new_node_kinds()
    {
        var conditional = new MongoConditionalExpression(
            new MongoConstantExpression(true, forSerialization: null),
            new MongoConstantExpression(1, forSerialization: null),
            new MongoConstantExpression(2, forSerialization: null));
        var local = new MongoDateTimeOffsetLocalExpression(Field("OccurredOffset", "Occurred"));
        var datePart = new MongoDatePartExpression(Field("Occurred", "Occurred"), MongoDatePart.Year);

        Assert.True(MongoAggregationExpressionRenderer.CanRender(conditional));
        Assert.True(MongoAggregationExpressionRenderer.CanRender(local));
        Assert.True(MongoAggregationExpressionRenderer.CanRender(datePart));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10" && dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build --filter "FullyQualifiedName~MongoAggregationExpressionRendererDatePartTests"`
Expected: FAIL — `Render`/`CanRender` throw `NativeTranslationNotSupportedException` / return `false` for the unhandled node kinds. If `Field(...)` throws a `NullReferenceException` instead (because some code path dereferences `.Property`), stop and read `MongoFieldExpression`'s constructor plus every existing renderer arm that touches `MongoFieldExpression.Property` before proceeding — build a minimal fake `IProperty` fixture matching whatever `MongoAggregationExpressionRendererTests.cs` already uses for its own `MongoFieldExpression` construction instead of `null!`.

- [ ] **Step 3: Write the implementation**

In `MongoAggregationExpressionRenderer.cs`, extend the `Render` switch (currently ending with the `MongoConvertExpression` arm at line ~64-70, right before the `_ => throw` catch-all at line 71):

```csharp
            MongoConvertExpression convert
                => new BsonDocument(
                    MongoConvertExpression.ToOperatorFor(convert.Type)
                        ?? throw new NativeTranslationNotSupportedException(
                            $"MQL has no conversion operator for '{convert.Type.Name}'. A convert to an "
                            + "unrenderable target should have been declined at translate time."),
                    Render(convert.Operand, placeholders, elementVariable)),
            MongoConditionalExpression conditional
                => new BsonDocument("$cond", new BsonDocument
                {
                    { "if", Render(conditional.Test, placeholders, elementVariable) },
                    { "then", Render(conditional.IfTrue, placeholders, elementVariable) },
                    { "else", Render(conditional.IfFalse, placeholders, elementVariable) }
                }),
            MongoDateTimeOffsetLocalExpression local
                => new BsonDocument("$dateAdd", new BsonDocument
                {
                    { "startDate", FieldRef(local.Operand.ElementName + ".DateTime", elementVariable) },
                    { "unit", "minute" },
                    { "amount", FieldRef(local.Operand.ElementName + ".Offset", elementVariable) }
                }),
            MongoDatePartExpression datePart => RenderDatePart(datePart, placeholders, elementVariable),
            _ => throw new NativeTranslationNotSupportedException(
                $"MongoAggregationExpressionRenderer does not support node type '{node.GetType().Name}'.")
```

Add the new `RenderDatePart` helper near `RenderSize`/`RenderFilteredSize` (after `FieldRef`, around line 176):

```csharp
    // MongoDB's $dayOfWeek returns 1 (Sunday)..7 (Saturday); .NET's DayOfWeek enum is 0 (Sunday)..6 (Saturday).
    // The subtraction is mandatory, not defensive — omitting it silently shifts every day of the week by one.
    private static BsonValue RenderDatePart(MongoDatePartExpression node, PlaceholderTable placeholders, string? elementVariable)
    {
        var operand = Render(node.Operand, placeholders, elementVariable);
        return node.Part switch
        {
            MongoDatePart.Year => new BsonDocument("$year", operand),
            MongoDatePart.Month => new BsonDocument("$month", operand),
            MongoDatePart.Day => new BsonDocument("$dayOfMonth", operand),
            MongoDatePart.Hour => new BsonDocument("$hour", operand),
            MongoDatePart.Minute => new BsonDocument("$minute", operand),
            MongoDatePart.Second => new BsonDocument("$second", operand),
            MongoDatePart.Millisecond => new BsonDocument("$millisecond", operand),
            MongoDatePart.DayOfYear => new BsonDocument("$dayOfYear", operand),
            MongoDatePart.DayOfWeek
                => new BsonDocument("$subtract", new BsonArray { new BsonDocument("$dayOfWeek", operand), 1 }),
            MongoDatePart.Date => new BsonDocument("$dateTrunc", new BsonDocument { { "date", operand }, { "unit", "day" } }),
            _ => throw new NativeTranslationNotSupportedException($"Unhandled {nameof(MongoDatePart)} '{node.Part}'.")
        };
    }
```

Extend the `CanRender` switch (currently ending with the `MongoConvertExpression` arm at line ~131-132, right before the `_ => false` catch-all at line 133):

```csharp
            MongoConvertExpression convert
                => MongoConvertExpression.ToOperatorFor(convert.Type) is not null && CanRender(convert.Operand),
            MongoConditionalExpression conditional
                => CanRender(conditional.Test) && CanRender(conditional.IfTrue) && CanRender(conditional.IfFalse),
            MongoDateTimeOffsetLocalExpression local => CanRender(local.Operand),
            MongoDatePartExpression datePart => CanRender(datePart.Operand),
            _ => false
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10" && dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build --filter "FullyQualifiedName~MongoAggregationExpressionRendererDatePartTests"`
Expected: PASS, all cases.

- [ ] **Step 5: Run the full unit test suite to check for regressions**

Run: `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build --filter "FullyQualifiedName~MongoDB.EntityFrameworkCore.UnitTests"`
Expected: PASS, zero regressions (this switch change is purely additive).

- [ ] **Step 6: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoAggregationExpressionRenderer.cs \
        tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoAggregationExpressionRendererDatePartTests.cs
git commit -m "Render MongoConditionalExpression/MongoDatePartExpression/MongoDateTimeOffsetLocalExpression as $cond/date operators/$dateAdd"
```

---

### Task 3: Translation — conditional expressions (`MongoExpressionTranslator.TranslateOperand`)

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.cs:851-880` (`TranslateOperand`, right after the existing `Convert`-handling block)
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoConditionalAndDateTimeTranslatorTests.cs`

**Interfaces:**
- Consumes: `MongoConditionalExpression` (Task 1), `TryTranslate` (existing predicate translator), `TryTranslateValue`/`TranslateOperand` (existing, this task extends them).
- Produces: `TranslateOperand`/`TryTranslateValue` now translate a `ConditionalExpression` C# node into a `MongoConditionalExpression`.

- [ ] **Step 1: Write the failing test**

Create the test file with its own small entity fixture (do not modify the large shared `Order`/`Customer` fixtures in `MongoExpressionTranslatorTests.cs`):

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
using System.Linq.Expressions;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Query.Expressions;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query.NativeTranslation;

public class MongoConditionalAndDateTimeTranslatorTests
{
    private class Row
    {
        public ObjectId Id { get; set; }
        public int Amount { get; set; }
        public bool Flag { get; set; }
        public DateTime Occurred { get; set; }
        public DateTimeOffset? OccurredOffset { get; set; }
    }

    private static (MongoExpressionTranslator Translator, Expression Body) BuildValueBody(
        Expression<Func<Row, object>> valueSelector)
    {
        using var db = SingleEntityDbContext.Create<Row>();
        var entityType = db.Model.FindEntityType(typeof(Row))!;
        var body = valueSelector.Body is UnaryExpression { NodeType: ExpressionType.Convert } unary
            ? unary.Operand
            : valueSelector.Body;
        return (new MongoExpressionTranslator(entityType), body);
    }

    [Fact]
    public void Conditional_with_field_condition_and_constant_branches_translates()
    {
        var (translator, body) = BuildValueBody(r => r.Flag ? 1 : 2);

        Assert.True(translator.TryTranslateValue(body, out var result));

        var conditional = Assert.IsType<MongoConditionalExpression>(result);
        Assert.IsType<MongoFieldExpression>(conditional.Test);
        Assert.Equal(1, Assert.IsType<MongoConstantExpression>(conditional.IfTrue).Value);
        Assert.Equal(2, Assert.IsType<MongoConstantExpression>(conditional.IfFalse).Value);
    }

    [Fact]
    public void Conditional_with_null_check_condition_translates()
    {
        var (translator, body) = BuildValueBody(r => r.OccurredOffset == null ? (DateTime?)null : r.Occurred);

        Assert.True(translator.TryTranslateValue(body, out var result));

        var conditional = Assert.IsType<MongoConditionalExpression>(result);
        // The Test is a comparison (r.OccurredOffset == null), not a bare field.
        Assert.IsType<MongoBinaryExpression>(conditional.Test);
        Assert.Null(Assert.IsType<MongoConstantExpression>(conditional.IfFalse is MongoConstantExpression
            ? conditional.IfFalse : conditional.IfTrue).Value is null ? null : (object?)null);
    }

    [Fact]
    public void Conditional_with_nested_conditional_branch_translates()
    {
        var (translator, body) = BuildValueBody(r => r.Flag ? (r.Amount > 0 ? 1 : 2) : 3);

        Assert.True(translator.TryTranslateValue(body, out var result));

        var outer = Assert.IsType<MongoConditionalExpression>(result);
        Assert.IsType<MongoConditionalExpression>(outer.IfTrue);
    }

    [Fact]
    public void Conditional_declines_when_a_branch_is_unsupported()
    {
        // string.Concat has no native translation at all, so a branch that reaches it must decline the
        // WHOLE conditional, not silently drop that branch.
        var (translator, body) = BuildValueBody(r => r.Flag ? r.Amount : int.Parse("x"));

        Assert.False(translator.TryTranslateValue(body, out _));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10" && dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build --filter "FullyQualifiedName~MongoConditionalAndDateTimeTranslatorTests"`
Expected: FAIL — `TryTranslateValue` currently returns `false` for a `ConditionalExpression` body (no case handles it), so `Conditional_with_field_condition_and_constant_branches_translates` etc. fail their `Assert.True`. `Conditional_declines_when_a_branch_is_unsupported` passes already by accident (everything declines today) — that's fine, it becomes a real regression guard once Step 3 lands.

- [ ] **Step 3: Write the implementation**

In `MongoExpressionTranslator.cs`, insert a new case into `TranslateOperand` immediately after the existing `Convert`-handling `if` block (after line 880's closing brace, before line 882's `if (TryResolveMember(node, ...))`):

```csharp
        // A ternary. Test is translated via the ORDINARY predicate translator (TryTranslate — the same one
        // Where uses), so anything already supported there as a predicate (nullable equality/comparison,
        // field-to-field, etc.) works as a $cond condition too. Both branches recurse through this same
        // method, so a branch may itself be a cast, arithmetic, nested conditional, or date-part expression.
        // A branch that declines declines the WHOLE conditional — there is no partial/fallback rendering for
        // one branch of a $cond.
        if (node is ConditionalExpression conditional)
        {
            if (!TryTranslate(conditional.Test, out var test))
                return null;

            var ifTrue = TranslateOperand(conditional.IfTrue, allowNumericWidening);
            if (ifTrue is null)
                return null;

            var ifFalse = TranslateOperand(conditional.IfFalse, allowNumericWidening);
            if (ifFalse is null)
                return null;

            return new MongoConditionalExpression(test, ifTrue, ifFalse);
        }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10" && dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build --filter "FullyQualifiedName~MongoConditionalAndDateTimeTranslatorTests"`
Expected: PASS, all 4 tests.

- [ ] **Step 5: Run the full unit test suite to check for regressions**

Run: `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build --filter "FullyQualifiedName~MongoDB.EntityFrameworkCore.UnitTests"`
Expected: PASS, zero regressions.

- [ ] **Step 6: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.cs \
        tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoConditionalAndDateTimeTranslatorTests.cs
git commit -m "Translate ConditionalExpression to MongoConditionalExpression in TranslateOperand"
```

---

### Task 4: Translation — date-part member chains (`MongoExpressionTranslator`)

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.cs` (add `TryTranslateDateTimeMember` + `DatePartsByMemberName`; call it from `TranslateOperand`)
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoConditionalAndDateTimeTranslatorTests.cs` (same file as Task 3 — one cohesive test file for both new capabilities)

**Interfaces:**
- Consumes: `MongoDatePartExpression`, `MongoDateTimeOffsetLocalExpression`, `MongoDatePart`, `MongoElementRefExpression` (existing) from Task 1; `TryResolveMember` (existing, private instance method).
- Produces: `TryTranslateDateTimeMember(Expression node, out MongoExpression? result)` — a new private instance method on `MongoExpressionTranslator`, called from `TranslateOperand`.

- [ ] **Step 1: Write the failing test**

Append to `MongoConditionalAndDateTimeTranslatorTests.cs` from Task 3:

```csharp
    [Fact]
    public void Plain_DateTime_Year_translates_directly_with_no_offset_reconstruction()
    {
        var (translator, body) = BuildValueBody(r => r.Occurred.Year);

        Assert.True(translator.TryTranslateValue(body, out var result));

        var datePart = Assert.IsType<MongoDatePartExpression>(result);
        Assert.Equal(MongoDatePart.Year, datePart.Part);
        Assert.IsType<MongoFieldExpression>(datePart.Operand);
    }

    [Fact]
    public void DateTimeOffset_Year_wraps_local_time_reconstruction()
    {
        var (translator, body) = BuildValueBody(r => r.OccurredOffset!.Value.Year);

        Assert.True(translator.TryTranslateValue(body, out var result));

        var datePart = Assert.IsType<MongoDatePartExpression>(result);
        Assert.Equal(MongoDatePart.Year, datePart.Part);
        var local = Assert.IsType<MongoDateTimeOffsetLocalExpression>(datePart.Operand);
        Assert.Equal("OccurredOffset", local.Operand.ElementName);
    }

    [Fact]
    public void DateTimeOffset_Value_DateTime_Date_three_hop_chain_composes()
    {
        // The motivating shape: BuiltInDataTypesMongoTest.Optional_datetime_reading_null_from_database.
        var (translator, body) = BuildValueBody(r => r.OccurredOffset!.Value.DateTime.Date);

        Assert.True(translator.TryTranslateValue(body, out var result));

        var datePart = Assert.IsType<MongoDatePartExpression>(result);
        Assert.Equal(MongoDatePart.Date, datePart.Part);
        var local = Assert.IsType<MongoDateTimeOffsetLocalExpression>(datePart.Operand);
        Assert.Equal("OccurredOffset", local.Operand.ElementName);
    }

    [Fact]
    public void DateTimeOffset_UtcDateTime_reads_the_raw_UTC_subfield_with_no_offset_addition()
    {
        var (translator, body) = BuildValueBody(r => r.OccurredOffset!.Value.UtcDateTime);

        Assert.True(translator.TryTranslateValue(body, out var result));

        var elementRef = Assert.IsType<MongoElementRefExpression>(result);
        Assert.Equal("OccurredOffset.DateTime", elementRef.Path);
    }

    [Fact]
    public void DateTimeOffset_DateTime_alone_returns_the_local_reconstruction_as_a_value()
    {
        var (translator, body) = BuildValueBody(r => r.OccurredOffset!.Value.DateTime);

        Assert.True(translator.TryTranslateValue(body, out var result));

        Assert.IsType<MongoDateTimeOffsetLocalExpression>(result);
    }

    [Fact]
    public void TimeOfDay_declines()
    {
        var (translator, body) = BuildValueBody(r => (object)r.Occurred.TimeOfDay);

        Assert.False(translator.TryTranslateValue(body, out _));
    }

    [Fact]
    public void Conditional_branch_with_a_date_part_translates()
    {
        var (translator, body) = BuildValueBody(
            r => r.OccurredOffset == null ? (DateTime?)null : r.OccurredOffset.Value.DateTime.Date);

        Assert.True(translator.TryTranslateValue(body, out var result));

        var conditional = Assert.IsType<MongoConditionalExpression>(result);
        Assert.IsType<MongoDatePartExpression>(conditional.IfFalse);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10" && dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build --filter "FullyQualifiedName~MongoConditionalAndDateTimeTranslatorTests"`
Expected: FAIL — the 7 new tests fail (`TryTranslateValue` returns `false` for every date-member shape today). `TimeOfDay_declines` passes already (everything declines today) — it becomes a real regression guard once Step 3 lands.

- [ ] **Step 3: Write the implementation**

In `MongoExpressionTranslator.cs`, add the member-name-to-part lookup as a `private static readonly` field near `WideningNumericConversions` (around line 184, same section of static lookup tables):

```csharp
    // Mirrors MongoEFToLinqTranslatingExpressionVisitor.DateTimeOffsetComponentMembers' member set, minus
    // TimeOfDay (see MongoDatePart's own remarks for why that one is excluded), and applies identically to a
    // plain DateTime receiver, which needs none of that visitor's offset-reconstruction dance.
    private static readonly Dictionary<string, MongoDatePart> DatePartsByMemberName = new()
    {
        [nameof(DateTime.Year)] = MongoDatePart.Year,
        [nameof(DateTime.Month)] = MongoDatePart.Month,
        [nameof(DateTime.Day)] = MongoDatePart.Day,
        [nameof(DateTime.Hour)] = MongoDatePart.Hour,
        [nameof(DateTime.Minute)] = MongoDatePart.Minute,
        [nameof(DateTime.Second)] = MongoDatePart.Second,
        [nameof(DateTime.Millisecond)] = MongoDatePart.Millisecond,
        [nameof(DateTime.DayOfWeek)] = MongoDatePart.DayOfWeek,
        [nameof(DateTime.DayOfYear)] = MongoDatePart.DayOfYear,
        [nameof(DateTime.Date)] = MongoDatePart.Date
    };
```

Add the new resolver method, right after `TryTranslateValue` (after its closing brace, around line 146):

```csharp
    /// <summary>
    /// Resolves a <c>DateTime</c>/<c>DateTimeOffset</c> member-access chain (<c>.Year</c>, <c>.Date</c>,
    /// <c>.DateTime</c>, <c>.UtcDateTime</c>, etc.) to a native date expression. Recurses into the receiver so
    /// a multi-hop chain composes correctly — e.g. <c>x.Dto.Value.DateTime.Date</c> resolves the field first
    /// (the <c>.Value</c> hop is peeled generically by <see cref="TryResolveMember"/>), wraps it in
    /// <see cref="MongoDateTimeOffsetLocalExpression"/> for <c>.DateTime</c>, then wraps THAT in
    /// <see cref="MongoDatePartExpression"/> for <c>.Date</c>.
    /// </summary>
    /// <remarks>
    /// The ULTIMATE receiver (the innermost hop) must resolve to a plain stored field via
    /// <see cref="TryResolveMember"/> — a receiver that is itself an arbitrary COMPUTED value (a conditional
    /// branch, arithmetic) has no MQL sub-field-access form for the <c>DateTimeOffset</c> reconstruction case
    /// and declines here rather than being mistranslated. This does not limit chains of date-member hops
    /// themselves (those recurse through this same method), only what the chain may ultimately be rooted on.
    /// </remarks>
    private bool TryTranslateDateTimeMember(Expression node, [NotNullWhen(true)] out MongoExpression? result)
    {
        result = null;
        if (node is not MemberExpression { Expression: { } receiver } member)
            return false;

        var receiverType = Nullable.GetUnderlyingType(receiver.Type) ?? receiver.Type;
        if (receiverType != typeof(DateTime) && receiverType != typeof(DateTimeOffset))
            return false;

        MongoExpression receiverExpr;
        if (TryResolveMember(receiver, out var property, out var fieldPath))
        {
            receiverExpr = new MongoFieldExpression(property, fieldPath!);
        }
        else if (TryTranslateDateTimeMember(receiver, out var nestedDateTimeExpr))
        {
            receiverExpr = nestedDateTimeExpr;
        }
        else
        {
            return false;
        }

        if (receiverType == typeof(DateTimeOffset))
        {
            switch (member.Member.Name)
            {
                case nameof(DateTimeOffset.UtcDateTime):
                    if (receiverExpr is not MongoFieldExpression utcField)
                        return false;
                    result = new MongoElementRefExpression(utcField.ElementName + ".DateTime", typeof(DateTime));
                    return true;

                case nameof(DateTimeOffset.DateTime):
                case nameof(DateTimeOffset.LocalDateTime):
                    if (receiverExpr is not MongoFieldExpression localField)
                        return false;
                    result = new MongoDateTimeOffsetLocalExpression(localField);
                    return true;

                default:
                    if (!DatePartsByMemberName.TryGetValue(member.Member.Name, out var offsetPart))
                        return false;
                    if (receiverExpr is not MongoFieldExpression partField)
                        return false;
                    result = new MongoDatePartExpression(new MongoDateTimeOffsetLocalExpression(partField), offsetPart);
                    return true;
            }
        }

        // Plain DateTime receiver: no offset reconstruction, extract directly.
        if (!DatePartsByMemberName.TryGetValue(member.Member.Name, out var plainPart))
            return false;

        result = new MongoDatePartExpression(receiverExpr, plainPart);
        return true;
    }
```

Then call it from `TranslateOperand`, right after the new `ConditionalExpression` block from Task 3 (before line 882's `if (TryResolveMember(node, ...))` — order relative to that check doesn't matter, since a date-member chain's own `MemberExpression` never matches `TryResolveMember`'s fast path directly, but placing it adjacent to the other new case keeps the two Task-3/4 additions visually grouped):

```csharp
        // A DateTime/DateTimeOffset member-access or date-part chain (.Year, .Date, .DateTime, etc.). Must run
        // regardless of whether TryResolveMember below would also be tried — it never matches this shape
        // anyway (the member's OWN receiver is never the query parameter directly for a date-member chain),
        // but this is placed here to keep it visually adjacent to the ConditionalExpression case above.
        if (TryTranslateDateTimeMember(node, out var dateTimeMember))
            return dateTimeMember;
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10" && dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build --filter "FullyQualifiedName~MongoConditionalAndDateTimeTranslatorTests"`
Expected: PASS, all 11 tests (4 from Task 3 + 7 from this task).

- [ ] **Step 5: Run the full unit test suite to check for regressions**

Run: `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build --filter "FullyQualifiedName~MongoDB.EntityFrameworkCore.UnitTests"`
Expected: PASS, zero regressions.

- [ ] **Step 6: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.cs \
        tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoConditionalAndDateTimeTranslatorTests.cs
git commit -m "Translate DateTime/DateTimeOffset member-access chains to MongoDatePartExpression"
```

---

### Task 5: Projection-binder wiring (`NativeProjectionBinder` + `MongoProjectionBindingExpressionVisitor`)

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeProjectionBinder.cs:478-484` (the generic computed-leaf admission gate)
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoProjectionBindingExpressionVisitor.cs:176-210` (add ONE case, for `ConditionalExpression` — a date-part member chain needs none, see Step 3)
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/NativeProjectionBinderConditionalAndDateTimeTests.cs`

**Interfaces:**
- Consumes: `MongoConditionalExpression`, `MongoDatePartExpression`, `MongoDateTimeOffsetLocalExpression`, `MongoElementRefExpression` (existing) from Tasks 1/3/4.
- Produces: `NativeProjectionBinder.TryPopulateNativeProjection` now admits a `ConditionalExpression` or date-member-chain projection leaf. `MongoProjectionBindingExpressionVisitor.Visit` now additionally registers a whole `ConditionalExpression` for the shaper (a date-member chain already rides its pre-existing bare-`MemberExpression` case).

- [ ] **Step 1: Write the failing test**

Follow the `NativeProjectionBinderEnumCastTests.cs` pattern (constructing a `MongoQueryExpression` over a `SingleEntityDbContext`, calling `NativeProjectionBinder.TryPopulateNativeProjection` directly, and asserting on `mongoQ.Select.Projection`/`mongoQ.Select.Route`):

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
using System.Linq.Expressions;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Query.Expressions;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query.NativeTranslation;

public class NativeProjectionBinderConditionalAndDateTimeTests
{
    private class Row
    {
        public ObjectId Id { get; set; }
        public bool Flag { get; set; }
        public DateTimeOffset? OccurredOffset { get; set; }
    }

    private class RowDto
    {
        public ObjectId Id { get; set; }
        public DateTime? DT { get; set; }
    }

    private static MongoQueryExpression TestQuery()
    {
        using var db = SingleEntityDbContext.Create<Row>();
        return new MongoQueryExpression(db.Model.FindEntityType(typeof(Row))!);
    }

    [Fact]
    public void Conditional_wrapped_projection_leaf_is_admitted()
    {
        var mongoQ = TestQuery();
        Expression<Func<Row, RowDto>> selector = r => new RowDto
        {
            Id = r.Id,
            DT = r.OccurredOffset == null ? (DateTime?)null : r.OccurredOffset.Value.DateTime.Date
        };

        Assert.True(NativeProjectionBinder.TryPopulateNativeProjection(mongoQ, selector));

        Assert.Equal(NativeRoute.Projection, mongoQ.Select.Route);
        var dtProjection = Assert.Single(mongoQ.Select.Projection, p => p.Alias == "DT");
        Assert.IsType<MongoConditionalExpression>(dtProjection.Expression);
    }

    [Fact]
    public void Conditional_bare_projection_leaf_is_admitted()
    {
        var mongoQ = TestQuery();
        Expression<Func<Row, DateTime?>> selector =
            r => r.OccurredOffset == null ? (DateTime?)null : r.OccurredOffset.Value.DateTime.Date;

        Assert.True(NativeProjectionBinder.TryPopulateNativeProjection(mongoQ, selector));

        Assert.Equal(NativeRoute.Projection, mongoQ.Select.Route);
        var projection = Assert.Single(mongoQ.Select.Projection);
        Assert.IsType<MongoConditionalExpression>(projection.Expression);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10" && dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build --filter "FullyQualifiedName~NativeProjectionBinderConditionalAndDateTimeTests"`
Expected: FAIL — `TryPopulateNativeProjection` returns `false` for both (the `TryTranslateLeaf` generic gate's node-kind allowlist doesn't include `MongoConditionalExpression`/`MongoDatePartExpression` yet).

- [ ] **Step 3: Write the implementation**

In `NativeProjectionBinder.cs`, widen the generic computed-leaf gate (currently at line 478-484):

```csharp
        if (translator.TryTranslateValue(leafExpression, out var value)
            && (value is MongoSizeExpression or MongoFilteredSizeExpression or MongoConvertExpression
                    or MongoConditionalExpression or MongoDatePartExpression or MongoDateTimeOffsetLocalExpression
                    or MongoElementRefExpression
                || (leafExpression is UnaryExpression { NodeType: ExpressionType.Convert } && value is MongoFieldExpression)))
        {
            result = value;
            return true;
        }
```

(`MongoConditionalExpression`, `MongoDatePartExpression`, `MongoDateTimeOffsetLocalExpression`, and `MongoElementRefExpression` all render as BSON *documents* or field-reference *strings*, never as a bare scalar `$project` could misread as an inclusion/exclusion flag — the same safety argument this gate's own comment already makes for `MongoSizeExpression`/`MongoConvertExpression`.)

**A date-time member chain needs NO new case here — verified, not assumed.** `MongoProjectionBindingExpressionVisitor.Visit`'s switch already has an unconditional `case MemberExpression memberExpression:` (line 155, no `when` guard, ordered BEFORE the arithmetic/cast cases) that registers the *whole* `MemberExpression` it's given — however deeply nested — as one opaque `_projectionMapping` entry and returns immediately:

```csharp
            case MemberExpression memberExpression:
                var currentProjectionMember = GetCurrentProjectionMember();
                _projectionMapping[currentProjectionMember] = memberExpression;

                return new ProjectionBindingExpression(_queryExpression, currentProjectionMember, expression.Type);
```

A date-member chain like `r.OccurredOffset.Value.DateTime.Date` is, from this visitor's point of view, just one `MemberExpression` (`.Date`, whose receiver happens to itself be nested member accesses) — so this pre-existing case already catches and correctly registers it whole, with no risk of the default walk visiting the receiver separately. The read side needs no change either: `MongoProjectionBindingRemovingExpressionVisitor.TryResolveFieldAccess` walks the receiver via `TryResolveFieldAccessSource`, which returns `(EntityType: null, DocumentExpression: null)` for a receiver that is itself a nested `MemberExpression` (not one of that method's recognized root shapes — `EntityTypedExpression`/`RootReferenceExpression`/`ObjectAccessExpression`/`StructuralTypeShaperExpression`/`ParameterExpression`) — so `fieldAccess.Property` comes back `null`, and `VisitExtension`'s `ProjectionBindingExpression` case falls through to its existing generic raw-alias read (`BsonBinding.CreateGetElementValue(DocParameter, projection.Alias, projectionBindingExpression.Type)`), the exact same path an arithmetic/cast leaf already uses. **Do not add a `MemberExpression` case for this** — it would be unreachable dead code shadowed by the pre-existing catch-all at line 155.

`ConditionalExpression` is NOT a `MemberExpression`, so it is NOT caught by that case — without a dedicated arm it falls through to `default: return base.Visit(expression);`, which recurses into `Test`/`IfTrue`/`IfFalse` independently, the same wrong-data hazard the arithmetic case's own comment describes. Add ONE new case to the `Visit` switch, alongside the existing arithmetic (`BinaryExpression`) and cast (`UnaryExpression{Convert}`) cases (after the cast case, before the `MethodCallExpression methodCallExpression` case around line 212):

```csharp
            // Native conditional projection leaf: register the WHOLE ConditionalExpression node as ONE
            // projection member, exactly like the arithmetic and cast cases above (NOT caught by the earlier
            // unconditional `case MemberExpression memberExpression:` — a ConditionalExpression is not a
            // MemberExpression). Without this, the default recursive walk would visit Test/IfTrue/IfFalse
            // independently, writing the SAME ProjectionMember slot three times and silently producing wrong
            // data. The Route == Projection guard is load-bearing for the same reason as the arithmetic case's:
            // it confines this mapping to a projection NativeProjectionBinder already accepted in full, so a
            // mixed/fallback shape falls through to the ordinary default walk untouched.
            case ConditionalExpression when _queryExpression.Select.Route == NativeRoute.Projection:
                var conditionalMember = GetCurrentProjectionMember();
                _projectionMapping[conditionalMember] = expression;
                return new ProjectionBindingExpression(_queryExpression, conditionalMember, expression.Type);
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10" && dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build --filter "FullyQualifiedName~NativeProjectionBinderConditionalAndDateTimeTests"`
Expected: PASS, both tests.

- [ ] **Step 5: Run the full unit test suite to check for regressions**

Run: `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build --filter "FullyQualifiedName~MongoDB.EntityFrameworkCore.UnitTests"`
Expected: PASS, zero regressions.

- [ ] **Step 6: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeProjectionBinder.cs \
        src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoProjectionBindingExpressionVisitor.cs \
        tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/NativeProjectionBinderConditionalAndDateTimeTests.cs
git commit -m "Wire conditional and date-part projection leaves into NativeProjectionBinder and the shaper visitor"
```

---

### Task 6: Functional differential tests — real server, all three query modes

**Files:**
- Create: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeConditionalAndDateTimeTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 1-5. This task adds no production code — it is the empirical measurement step for the two open risk areas the spec flagged (null handling in date operators; `$dayOfWeek` numbering, already unit-tested in Task 2 but not yet proven against a real server).

- [ ] **Step 1: Write the test file**

Follow the exact `NativeEnumCastProjectionTests.cs` pattern (`[XUnitCollection("QueryTests")]`, `IClassFixture<TemporaryDatabaseFixture>`, `SingleEntityDbContext.Create(fixture, ...)`, asserting `Native`/`DriverLinq`/`NativeOnly` all agree):

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
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Infrastructure;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

[XUnitCollection("QueryTests")]
public class NativeConditionalAndDateTimeTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    public class Row
    {
        public ObjectId Id { get; set; }
        public string Label { get; set; } = "";
        public bool Flag { get; set; }
        public DateTime Occurred { get; set; }
        public DateTimeOffset? OccurredOffset { get; set; }
    }

    // Positive offset (+02:00), negative offset (-05:00), and a null OccurredOffset row — covers the
    // conditional's null branch and both reconstruction directions.
    private static readonly (string Label, bool Flag, DateTime Occurred, DateTimeOffset? OccurredOffset)[] Rows =
    [
        ("a", true, new DateTime(2024, 3, 10, 23, 30, 15, DateTimeKind.Utc),
            new DateTimeOffset(2024, 3, 10, 23, 30, 15, TimeSpan.FromHours(2))),
        ("b", false, new DateTime(2024, 3, 10, 1, 15, 45, DateTimeKind.Utc),
            new DateTimeOffset(2024, 3, 10, 1, 15, 45, TimeSpan.FromHours(-5))),
        ("c", true, new DateTime(2024, 3, 10, 12, 0, 0, DateTimeKind.Utc), null)
    ];

    private IMongoCollection<Row> Seed(string name)
    {
        var collection = database.CreateCollection<Row>(name);
        collection.InsertMany(Rows.Select(r => new Row
        {
            Label = r.Label, Flag = r.Flag, Occurred = r.Occurred, OccurredOffset = r.OccurredOffset
        }));
        return collection;
    }

    private static SingleEntityDbContext<Row> CreateContext(IMongoCollection<Row> collection, MongoQueryMode mode)
        => SingleEntityDbContext.Create(
            collection,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });

    [Fact]
    public void Conditional_null_check_over_DateTimeOffset_matches_across_all_three_modes()
    {
        var collection = Seed(nameof(Conditional_null_check_over_DateTimeOffset_matches_across_all_three_modes));

        using var nativeOnly = CreateContext(collection, MongoQueryMode.NativeOnly);
        var nativeOnlyResult = nativeOnly.Entities.AsNoTracking().OrderBy(x => x.Label)
            .Select(x => new { x.Label, DT = x.OccurredOffset == null ? (DateTime?)null : x.OccurredOffset.Value.DateTime.Date })
            .ToList();

        using var native = CreateContext(collection, MongoQueryMode.Native);
        var nativeResult = native.Entities.AsNoTracking().OrderBy(x => x.Label)
            .Select(x => new { x.Label, DT = x.OccurredOffset == null ? (DateTime?)null : x.OccurredOffset.Value.DateTime.Date })
            .ToList();

        using var driverLinq = CreateContext(collection, MongoQueryMode.DriverLinq);
        var driverLinqResult = driverLinq.Entities.AsNoTracking().OrderBy(x => x.Label)
            .Select(x => new { x.Label, DT = x.OccurredOffset == null ? (DateTime?)null : x.OccurredOffset.Value.DateTime.Date })
            .ToList();

        var inMemoryResult = Rows.OrderBy(r => r.Label)
            .Select(r => (r.Label, DT: r.OccurredOffset == null ? (DateTime?)null : r.OccurredOffset.Value.DateTime.Date))
            .ToList();

        Assert.Equal(inMemoryResult, nativeOnlyResult.Select(r => (r.Label, r.DT)));
        Assert.Equal(nativeOnlyResult.Select(r => (r.Label, r.DT)), nativeResult.Select(r => (r.Label, r.DT)));
        Assert.Equal(nativeResult.Select(r => (r.Label, r.DT)), driverLinqResult.Select(r => (r.Label, r.DT)));
    }

    [Theory]
    [InlineData("Year")]
    [InlineData("Month")]
    [InlineData("Day")]
    [InlineData("Hour")]
    [InlineData("Minute")]
    [InlineData("Second")]
    [InlineData("DayOfWeek")]
    [InlineData("DayOfYear")]
    public void DateTimeOffset_date_part_matches_in_memory_LINQ_under_NativeOnly(string part)
    {
        // A single parameterized test asserting each part's SQL-level correctness would need per-part
        // projection expressions, which C# cannot build from a string at compile time — so this drives one
        // fixed projection covering the part under test via a switch, keeping the test data/oracle shared.
        var collection = Seed(nameof(DateTimeOffset_date_part_matches_in_memory_LINQ_under_NativeOnly) + part);
        using var nativeOnly = CreateContext(collection, MongoQueryMode.NativeOnly);

        var nonNullRows = Rows.Where(r => r.OccurredOffset is not null).ToArray();

        object[] actual = part switch
        {
            "Year" => nativeOnly.Entities.AsNoTracking().Where(x => x.OccurredOffset != null)
                .OrderBy(x => x.Label).Select(x => (object)x.OccurredOffset!.Value.Year).ToArray(),
            "Month" => nativeOnly.Entities.AsNoTracking().Where(x => x.OccurredOffset != null)
                .OrderBy(x => x.Label).Select(x => (object)x.OccurredOffset!.Value.Month).ToArray(),
            "Day" => nativeOnly.Entities.AsNoTracking().Where(x => x.OccurredOffset != null)
                .OrderBy(x => x.Label).Select(x => (object)x.OccurredOffset!.Value.Day).ToArray(),
            "Hour" => nativeOnly.Entities.AsNoTracking().Where(x => x.OccurredOffset != null)
                .OrderBy(x => x.Label).Select(x => (object)x.OccurredOffset!.Value.Hour).ToArray(),
            "Minute" => nativeOnly.Entities.AsNoTracking().Where(x => x.OccurredOffset != null)
                .OrderBy(x => x.Label).Select(x => (object)x.OccurredOffset!.Value.Minute).ToArray(),
            "Second" => nativeOnly.Entities.AsNoTracking().Where(x => x.OccurredOffset != null)
                .OrderBy(x => x.Label).Select(x => (object)x.OccurredOffset!.Value.Second).ToArray(),
            "DayOfWeek" => nativeOnly.Entities.AsNoTracking().Where(x => x.OccurredOffset != null)
                .OrderBy(x => x.Label).Select(x => (object)x.OccurredOffset!.Value.DayOfWeek).ToArray(),
            "DayOfYear" => nativeOnly.Entities.AsNoTracking().Where(x => x.OccurredOffset != null)
                .OrderBy(x => x.Label).Select(x => (object)x.OccurredOffset!.Value.DayOfYear).ToArray(),
            _ => throw new ArgumentOutOfRangeException(nameof(part))
        };

        object[] expected = part switch
        {
            "Year" => nonNullRows.OrderBy(r => r.Label).Select(r => (object)r.OccurredOffset!.Value.Year).ToArray(),
            "Month" => nonNullRows.OrderBy(r => r.Label).Select(r => (object)r.OccurredOffset!.Value.Month).ToArray(),
            "Day" => nonNullRows.OrderBy(r => r.Label).Select(r => (object)r.OccurredOffset!.Value.Day).ToArray(),
            "Hour" => nonNullRows.OrderBy(r => r.Label).Select(r => (object)r.OccurredOffset!.Value.Hour).ToArray(),
            "Minute" => nonNullRows.OrderBy(r => r.Label).Select(r => (object)r.OccurredOffset!.Value.Minute).ToArray(),
            "Second" => nonNullRows.OrderBy(r => r.Label).Select(r => (object)r.OccurredOffset!.Value.Second).ToArray(),
            "DayOfWeek" => nonNullRows.OrderBy(r => r.Label).Select(r => (object)r.OccurredOffset!.Value.DayOfWeek).ToArray(),
            "DayOfYear" => nonNullRows.OrderBy(r => r.Label).Select(r => (object)r.OccurredOffset!.Value.DayOfYear).ToArray(),
            _ => throw new ArgumentOutOfRangeException(nameof(part))
        };

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Plain_DateTime_Date_matches_in_memory_LINQ_under_NativeOnly()
    {
        var collection = Seed(nameof(Plain_DateTime_Date_matches_in_memory_LINQ_under_NativeOnly));
        using var nativeOnly = CreateContext(collection, MongoQueryMode.NativeOnly);

        var actual = nativeOnly.Entities.AsNoTracking().OrderBy(x => x.Label).Select(x => x.Occurred.Date).ToList();
        var expected = Rows.OrderBy(r => r.Label).Select(r => r.Occurred.Date).ToList();

        Assert.Equal(expected, actual);
    }
}
```

- [ ] **Step 2: Run the tests**

Run: `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10" && dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build --filter "FullyQualifiedName~NativeConditionalAndDateTimeTests"`
Expected: PASS against a real (auto-provisioned) MongoDB server. **If any date-part case fails with a server-side error** (not a C# assertion failure) — this is one of the two risk areas the spec flagged as needing empirical measurement. Read the exact server error message:
  - If it's a null/missing-field error from `$dateAdd` or a date-extraction operator: wrap the affected operand in `$ifNull` inside the Task 2 renderer (mirroring the existing `RenderSize`/`RenderFilteredSize` pattern), add a regression test pinning the fix, and re-run.
  - If it's any other server error: stop and investigate before proceeding — do not paper over an unexplained failure with a broad try/catch.

- [ ] **Step 3: Commit**

```bash
git add tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeConditionalAndDateTimeTests.cs
git commit -m "Add functional differential tests for native conditional expressions and date parts"
```

(If Step 2 required a null-handling fix, include the renderer change and its own regression test in this commit, and describe the fix in the commit message instead of the generic one above.)

---

### Task 7: Close out `BuiltInDataTypesMongoTest.Optional_datetime_reading_null_from_database`

**Files:**
- Modify: `tests/MongoDB.EntityFrameworkCore.SpecificationTests/Mapping/BuiltInDataTypesMongoTest.cs`

**Interfaces:**
- Consumes: everything from Tasks 1-6.

- [ ] **Step 1: Confirm the current override still just delegates to `base`**

```bash
grep -n "Optional_datetime_reading_null_from_database" -A 1 tests/MongoDB.EntityFrameworkCore.SpecificationTests/Mapping/BuiltInDataTypesMongoTest.cs
```

Expected (two overrides, one per `#if !EF8`/`#else` branch — see the file's existing structure): `public override Task Optional_datetime_reading_null_from_database() => base.Optional_datetime_reading_null_from_database();` and the EF8 `void` equivalent. No change needed here — this override was never modified to decline under `NativeOnly` (that change was proposed and then explicitly reverted earlier in this same investigation), so it should now simply start passing.

- [ ] **Step 2: Run it under `NativeOnly` on EF10**

Run: `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10" && MONGODB_EF_NATIVE_ONLY=1 dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build --filter "FullyQualifiedName~BuiltInDataTypesMongo"`
Expected: PASS, all 30 tests (this was the last of the six originally-failing tests from the earlier investigation in this session; `Can_read_back_bool_mapped_as_int_through_navigation` was explicitly left alone per prior instruction, and the two enum-cast tests were already fixed and committed under EF-215).

- [ ] **Step 3: Repeat for EF8 and EF9**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF8"
MONGODB_EF_NATIVE_ONLY=1 dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF8" --no-build --filter "FullyQualifiedName~BuiltInDataTypesMongo"
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF9"
MONGODB_EF_NATIVE_ONLY=1 dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF9" --no-build --filter "FullyQualifiedName~BuiltInDataTypesMongo"
```

Expected: PASS on both (EF8/EF9 never had the `Can_read_back_bool_mapped_as_int_through_navigation` gap to begin with — that one's already declared `AssertTranslationFailed` on those two versions — so this test file should now be **fully green under `NativeOnly` on EF8 and EF9**, and green except for the one deliberately-untouched navigation test on EF10).

- [ ] **Step 4: Run the full three-EF-version regression suite**

Run the `/test-all` skill, or manually:

```bash
for cfg in EF8 EF9 EF10; do
  dotnet build MongoDB.EFCoreProvider.sln -c "Debug $cfg"
  dotnet test MongoDB.EFCoreProvider.sln -c "Debug $cfg" --no-build
done
```

Expected: PASS, zero `Passed → Failed` regressions across all three configurations' unit/functional/spec suites.

- [ ] **Step 5: No commit needed for this task**

This task is verification-only — Task 6 (or an earlier task) already committed the change that makes this test pass. If Step 2/3 reveal a failure, return to the relevant earlier task and fix it there (with its own commit), then re-run this task's verification.

---

### Task 8: Non-goal regression tests — `$elemMatch` exclusion and negator decline

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoQueryLanguageRenderer.cs:446-489` (`IsQueryDialectRenderable`) — explicit decline arms for documentation clarity (the catch-all already returns `false`; this makes the decision visible, matching the existing `MongoConvertExpression => false` precedent)
- Test: append to `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/` — either a new small file or an addition to an existing `MongoQueryLanguageRendererTests`/`MongoExpressionNegatorTests` file (check which already exists via `ls tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/ | grep -i "querylanguage\|negator"` before deciding)

**Interfaces:**
- Consumes: `MongoConditionalExpression`, `MongoDatePartExpression` from Task 1; `MongoQueryLanguageRenderer.IsQueryDialectRenderable`, `MongoExpressionNegator.TryNegate` (existing).

- [ ] **Step 1: Write the failing test**

```csharp
    [Fact]
    public void IsQueryDialectRenderable_rejects_a_conditional_expression()
    {
        var conditional = new MongoConditionalExpression(
            new MongoConstantExpression(true, forSerialization: null),
            new MongoConstantExpression(1, forSerialization: null),
            new MongoConstantExpression(2, forSerialization: null));

        Assert.False(MongoQueryLanguageRenderer.IsQueryDialectRenderable(conditional));
    }

    [Fact]
    public void IsQueryDialectRenderable_rejects_a_date_part_expression()
    {
        var datePart = new MongoDatePartExpression(
            new MongoConstantExpression(DateTime.UtcNow, forSerialization: null), MongoDatePart.Year);

        Assert.False(MongoQueryLanguageRenderer.IsQueryDialectRenderable(datePart));
    }

    [Fact]
    public void Negator_declines_a_conditional_expression_rather_than_mis_negating_it()
    {
        var conditional = new MongoConditionalExpression(
            new MongoConstantExpression(true, forSerialization: null),
            new MongoConstantExpression(1, forSerialization: null),
            new MongoConstantExpression(2, forSerialization: null));

        Assert.False(MongoExpressionNegator.TryNegate(conditional, out _));
    }
```

**Note:** these tests already pass today (the catch-all `_ => false` in `IsQueryDialectRenderable`, and `TryNegate`'s own `IsQueryDialectRenderable` gate, already produce this result with zero code changes) — this task is a **pinning/regression** task, not a bug fix. Write the test, confirm it passes immediately, then proceed straight to making the decline explicit for documentation.

- [ ] **Step 2: Run test to verify it already passes**

Run: `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10" && dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build --filter "FullyQualifiedName~<the test class you added these to>"`
Expected: PASS immediately (see the note in Step 1 — there is nothing to "make" pass here, only to pin).

- [ ] **Step 3: Make the decline explicit (documentation-only change)**

In `MongoQueryLanguageRenderer.cs`, add two explicit arms to `IsQueryDialectRenderable` right after the existing `MongoConvertExpression => false` arm (around line 471-474):

```csharp
            // Explicit rather than left to the catch-all, matching MongoConvertExpression above: neither has a
            // query-dialect form, so admitting either here would put $expr inside $elemMatch, a hard server
            // error.
            MongoConditionalExpression => false,
            MongoDatePartExpression => false,
            MongoDateTimeOffsetLocalExpression => false,
```

- [ ] **Step 4: Run the tests again to confirm nothing broke**

Run: `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build --filter "FullyQualifiedName~MongoDB.EntityFrameworkCore.UnitTests"`
Expected: PASS, zero regressions (this change only makes an already-true default explicit).

- [ ] **Step 5: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoQueryLanguageRenderer.cs \
        tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/
git commit -m "Pin that conditional/date-part expressions decline (not mis-negate) in query-dialect/elemMatch position"
```

---

## Final Self-Review Checklist (for whoever executes this plan)

- [ ] Every task's tests pass on EF10 before moving to the next task.
- [ ] Task 7's full three-EF-version sweep shows zero `Passed → Failed` regressions.
- [ ] `MongoDateTimeOffsetLocalExpression.Operand` stayed typed as `MongoFieldExpression` (not widened to `MongoExpression`) — this is a compile-time guarantee the design relies on, not a runtime check to relax later without re-deriving why it's safe.
- [ ] `TimeOfDay` was NOT implemented — confirm the `TimeOfDay_declines` test (Task 4) is still present and still passing after all later tasks land, so a future change can't silently start guessing at its translation without a deliberate decision.
- [ ] No commit message references a JIRA ticket key — file one before merging, per this plan's own Global Constraints note.
