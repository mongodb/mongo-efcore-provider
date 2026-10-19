# EF-421: Correlated Element Predicates Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make a correlated owned-collection element predicate — `b.Posts.Count(p => p.X == b.Y) > n`, `b.Posts.Any(p => p.X == b.Y)`, `b.Posts.All(p => p.X == b.Y)` — natively representable instead of declining to driver-LINQ.

**Architecture:** Capture each root-level lambda's own `ParameterExpression` on the `MongoExpressionTranslator` instance built for it (`_selfParam`). When an owned-collection element predicate is found to reference a free parameter, check by `ReferenceEquals` whether that parameter is the enclosing translator's `_selfParam`; on a match, build a two-scope child translator (mirroring the existing `NativeSelectManyBinder` pattern) instead of declining. Two new dialect-agnostic node types carry the result: `MongoOuterFieldExpression` (a field reference that always resolves at document root, even inside a `$filter`/`$map` scope) and `MongoQuantifierExpression` (`$anyElementTrue`/`$allElementsTrue` over `$map`, for quantifiers — `$elemMatch` cannot reference the enclosing document at all).

**Tech Stack:** C#, EF Core 8/9/10 provider internals, MongoDB aggregation pipeline (BSON), xUnit (FunctionalTests project, real MongoDB via TestContainers).

**Spec:** `docs/superpowers/specs/2026-08-28-ef421-correlated-element-predicates-design.md`

## Global Constraints

- Scope by parameter identity (`ReferenceEquals`), never by member name — the standing invariant this codebase has paid for before.
- A correlation that does not match the immediate enclosing `_selfParam` (nested two-or-more scopes deep) must continue to decline outright — never approximate.
- `MongoOuterFieldExpression` and `MongoQuantifierExpression` must both be classified as aggregation-only (`IsQueryDialectRenderable` → `false`) — `$expr` is a hard server error inside `$elemMatch`.
- No `#if EF8`/`EF9`/`EF10` expected anywhere in this change — pure aggregation-pipeline generation.
- Every existing (uncorrelated) `Any`/`All`/`Count(pred)`/`SelectMany` test must keep passing unmodified — this is a pure widening, not a rewrite of existing paths.
- Follow existing file conventions exactly: copyright header block, `internal sealed class` for expression nodes, XML doc comments in the established style, no FluentAssertions (plain xUnit `Assert.*`) in tests.

---

## File Structure

| File | Change |
|---|---|
| `src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoOuterFieldExpression.cs` | **Create.** New sealed node: a field reference that always renders at document root. |
| `src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoQuantifierExpression.cs` | **Create.** New sealed node: `$anyElementTrue`/`$allElementsTrue` over `$map`. |
| `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.cs` | **Modify.** `_selfParam` field + constructor param; quantifier arm (~567-628) and `Count(pred)` arm (~1016-1094) gain a correlation-match branch; `TranslateComparison`'s two `TryResolveMember` call sites (~711, ~744) build `MongoOuterFieldExpression` on a match. |
| `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.MethodCalls.cs` | **Modify.** `ReferencesEnclosingScope`/`FreeParameterVisitor` report *which* parameter was found, not just whether one was. |
| `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.Members.cs` | **Modify.** `TryResolveMember` gains an `out bool isOuter`; five call sites in the main file that consume it are updated (bare-bool member, `HasValue`, Contains-item, array-Contains receiver, regex receiver) to decline rather than silently mis-scope when `isOuter` is unexpectedly true; `TryResolveOwnedFieldPath`/`TryResolveOwnedCollectionPath` relativized per spec §6. |
| `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoAggregationExpressionRenderer.cs` | **Modify.** `Render`/`CanRender` gain arms for both new node types. |
| `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoQueryLanguageRenderer.cs` | **Modify.** `IsQueryDialectRenderable` gains explicit `false` arms for both new node types (the catch-all already returns `false`, but an explicit arm documents intent, matching the file's existing style for `MongoConditionalExpression`/`MongoDatePartExpression`). |
| `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeSlotPopulator.cs` | **Modify.** `Where`/`OrderBy`/`OrderByDescending`/`ThenBy`/`ThenByDescending` arms set `translator.SelfParam` from their own lambda before translating. |
| `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeProjectionBinder.cs` | **Modify.** Root translator construction passes `selector.Parameters[0]`. |
| `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeCardinalityBinder.cs` | **Modify.** `TryBindAggregate`'s root translator construction passes `predicate?.Parameters[0]`. |
| `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeVectorSearchBinder.cs` | **Modify.** Root translator construction passes `preFilterLambda.Parameters[0]`. |
| `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeOwnedCollectionCorrelatedTests.cs` | **Create.** New test file (see rationale in Task 8) covering correlated `Count(pred)`/`Any`/`All`. |
| `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeOwnedCollectionAllTests.cs` | **Modify.** `All_with_a_correlated_element_predicate_declines_and_falls_back_to_correct_rows` is superseded (see Task 8). |
| `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeOwnedCollectionFilteredCountTests.cs` | **Modify.** Any existing "declines on correlation" case is superseded (see Task 8). |

---

### Task 1: `_selfParam` capture on `MongoExpressionTranslator` + `FreeParameterVisitor` reports which parameter it found

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.cs:59-79` (constructors)
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.MethodCalls.cs:238-294` (`ReferencesEnclosingScope`/`FreeParameterVisitor`)
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoExpressionTranslatorMethodCallsTests.cs` (create if it does not already exist — check first with `Glob` for any existing `*MethodCalls*Tests.cs` under that directory; if one exists, add to it instead)

**Interfaces:**
- Produces: `MongoExpressionTranslator.SelfParam` (internal, settable, nullable `ParameterExpression`) — the root parameter this single-scope translator was built for, or `null` if none was supplied / this is a two-scope translator.
- Produces: a private static `FreeParameterVisitor` that now exposes `FoundParameter` (the actual free `ParameterExpression`, or `null`), in addition to the existing `FoundFreeParameter` bool.
- Produces: `ReferencesEnclosingScope(Expression body, ParameterExpression elementParameter, out ParameterExpression? found)` — same bool return, plus the found parameter.

Since `FreeParameterVisitor` and `ReferencesEnclosingScope` are `private` to the `partial class MongoExpressionTranslator`, they aren't independently unit-testable through a public surface. Write the test against the existing internal test hook instead: check whether `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/` already has a test file that constructs a `MongoExpressionTranslator` directly and calls `TryTranslate` on hand-built expression trees (this is the established pattern for this class elsewhere in the test suite — search for `new MongoExpressionTranslator(` in that directory). If no test can reach `ReferencesEnclosingScope` without a behavior change (which arrives in Task 3/5), skip the isolated unit test for this task and rely on Task 3/5's differential tests to exercise it — note this explicitly in the commit message rather than fabricating a test that doesn't test real behavior.

- [ ] **Step 1: Check for an existing reachable test surface**

Run: `grep -rn "new MongoExpressionTranslator(" tests/MongoDB.EntityFrameworkCore.UnitTests/`

If this returns a test file with direct access to translator internals (e.g. via `InternalsVisibleTo`), read it to confirm the pattern. If it returns nothing, this class is only exercised via the FunctionalTests end-to-end path — proceed to Step 2 without a Task-1-local unit test; Task 3 and Task 5's differential FunctionalTests are what prove this step's correctness.

- [ ] **Step 2: Modify `FreeParameterVisitor` to report the found parameter**

In `MongoExpressionTranslator.MethodCalls.cs`, replace:

```csharp
    private static bool ReferencesEnclosingScope(Expression body, ParameterExpression elementParameter)
    {
        var visitor = new FreeParameterVisitor(elementParameter);
        visitor.Visit(body);
        return visitor.FoundFreeParameter;
    }

    /// <summary>
    /// Finds a reference to a <see cref="ParameterExpression"/> that is free in the visited expression — i.e.
    /// neither the element parameter it was constructed with, nor bound by a <see cref="LambdaExpression"/>
    /// encountered while descending, nor an EF query parameter. See
    /// <see cref="ReferencesEnclosingScope"/> for why this distinction matters.
    /// </summary>
    private sealed class FreeParameterVisitor(ParameterExpression elementParameter) : ExpressionVisitor
    {
        private readonly List<ParameterExpression> _bound = [elementParameter];

        public bool FoundFreeParameter { get; private set; }
```

with:

```csharp
    /// <summary>
    /// As the single-bool overload, but also reports WHICH parameter was found free — used by the
    /// <c>Count(pred)</c>/<c>Any</c>/<c>All</c> call sites to check, by <see cref="ReferenceEquals"/>, whether
    /// the free parameter is the immediate enclosing translator's own root parameter
    /// (<see cref="MongoExpressionTranslator.SelfParam"/>). A match upgrades the shape from "decline" to
    /// "build a two-scope child translator"; anything else (no match — correlation reaches past the
    /// immediate root) still declines exactly as before.
    /// </summary>
    private static bool ReferencesEnclosingScope(
        Expression body, ParameterExpression elementParameter, out ParameterExpression? found)
    {
        var visitor = new FreeParameterVisitor(elementParameter);
        visitor.Visit(body);
        found = visitor.FoundParameter;
        return visitor.FoundFreeParameter;
    }

    /// <summary>
    /// Finds a reference to a <see cref="ParameterExpression"/> that is free in the visited expression — i.e.
    /// neither the element parameter it was constructed with, nor bound by a <see cref="LambdaExpression"/>
    /// encountered while descending, nor an EF query parameter. See
    /// <see cref="ReferencesEnclosingScope"/> for why this distinction matters.
    /// </summary>
    private sealed class FreeParameterVisitor(ParameterExpression elementParameter) : ExpressionVisitor
    {
        private readonly List<ParameterExpression> _bound = [elementParameter];

        public bool FoundFreeParameter { get; private set; }

        /// <summary>
        /// The first free parameter found, or <see langword="null"/> if none has been found yet. Only the
        /// FIRST one is recorded: a body with two distinct free parameters is already declined by the caller
        /// regardless of which one is reported (neither can be confirmed as the sole enclosing scope), so
        /// there is no need to track more than one.
        /// </summary>
        public ParameterExpression? FoundParameter { get; private set; }
```

Then update `VisitParameter` in the same class:

```csharp
        protected override Expression VisitParameter(ParameterExpression node)
        {
            if (!ContainsByIdentity(node) && !NativeQueryParameter.TryGetQueryParameterName(node, out _))
            {
                FoundFreeParameter = true;
                FoundParameter ??= node;
            }

            return node;
        }
```

- [ ] **Step 3: Add `SelfParam` to `MongoExpressionTranslator`**

In `MongoExpressionTranslator.cs`, replace:

```csharp
    private readonly IEntityType _entityType;
    private readonly ParameterExpression? _outerParam;
    private readonly IEntityType? _outerEntityType;
    private readonly string? _innerPrefix;

    /// <summary>
    /// Creates a single-scope <see cref="MongoExpressionTranslator"/> for the given entity type.
    /// </summary>
    /// <param name="entityType">The entity type whose properties and element names are used during translation.</param>
    public MongoExpressionTranslator(IEntityType entityType)
    {
        _entityType = entityType;
    }
```

with:

```csharp
    private readonly IEntityType _entityType;
    private readonly ParameterExpression? _outerParam;
    private readonly IEntityType? _outerEntityType;
    private readonly string? _innerPrefix;

    /// <summary>
    /// Creates a single-scope <see cref="MongoExpressionTranslator"/> for the given entity type.
    /// </summary>
    /// <param name="entityType">The entity type whose properties and element names are used during translation.</param>
    /// <param name="selfParam">
    /// The root <see cref="ParameterExpression"/> this translator was built for (a <c>Where</c> predicate's,
    /// an <c>OrderBy</c> key selector's, a <c>Select</c> projection's, etc.), or <see langword="null"/> when
    /// none is available. Used ONLY to detect a single-level CORRELATED owned-collection element predicate
    /// (<c>Count(pred)</c>/<c>Any</c>/<c>All</c>) nested inside this translator's own tree: when such a
    /// predicate's free parameter is <see cref="ReferenceEquals"/>-identical to <see cref="SelfParam"/>, the
    /// quantifier/count call sites build a two-scope child translator instead of declining. See EF-421.
    /// </param>
    public MongoExpressionTranslator(IEntityType entityType, ParameterExpression? selfParam = null)
    {
        _entityType = entityType;
        SelfParam = selfParam;
    }

    /// <summary>
    /// The root <see cref="ParameterExpression"/> this SINGLE-SCOPE translator was built for — see the
    /// constructor parameter of the same name. SETTABLE (not constructor-only) because
    /// <see cref="NativeSlotPopulator.PopulateNativeSlots"/> reuses one translator instance across several
    /// slot operators (<c>Where</c>/<c>OrderBy</c>/<c>ThenBy</c>/etc.), each with its own lambda parameter;
    /// that call site sets this field for the duration of each arm rather than constructing a fresh
    /// translator per arm. Always <see langword="null"/> on a two-scope translator (the constructor below
    /// never sets it) — a correlation nested two-or-more scopes deep is out of EF-421's scope and must keep
    /// declining, which requires that a two-scope child never itself exposes a further <see cref="SelfParam"/>.
    /// </summary>
    internal ParameterExpression? SelfParam { get; set; }
```

- [ ] **Step 4: Build**

Run: `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"`
Expected: builds clean (no callers of `ReferencesEnclosingScope`'s old 2-arg overload exist yet outside this file — the two real call sites are updated in Tasks 3 and 5 — but confirm there is no other caller by running `grep -rn "ReferencesEnclosingScope(" src/`).

If the grep finds the two call sites in `MongoExpressionTranslator.cs` (the quantifier arm and the `Count(pred)` arm) still using the old 2-arg form, the build will fail. Fix those two call sites minimally for now (just add a discard, `out _`) so the build passes — Tasks 3 and 5 will replace the discard with the real branch:

```csharp
if (ReferencesEnclosingScope(elementLambda.Body, elementLambda.Parameters[0], out _))
    return null;
```

and

```csharp
if (ReferencesEnclosingScope(countPredicate.Body, countPredicate.Parameters[0], out _))
    return null;
```

- [ ] **Step 5: Run the full existing native-translation test suite to confirm no regression**

Run: `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build --filter "FullyQualifiedName~NativeOwnedCollection"`
Expected: PASS (identical to before this task — pure plumbing, no behavior change yet).

- [ ] **Step 6: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.cs \
        src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.MethodCalls.cs
git commit -m "EF-421: capture root parameter identity on MongoExpressionTranslator"
```

---

### Task 2: `MongoOuterFieldExpression` node type + rendering

**Files:**
- Create: `src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoOuterFieldExpression.cs`
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoAggregationExpressionRenderer.cs:53-88` (`Render`), `:118-153` (`CanRender`)
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoQueryLanguageRenderer.cs:446-496` (`IsQueryDialectRenderable`)
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.cs:339-380` (`AllFieldsDefaultSerialized`)
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoAggregationExpressionRendererTests.cs` (create if none exists — `Glob tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/*Renderer*Tests.cs` first; if a file for this renderer already exists, add to it)

**Interfaces:**
- Produces: `MongoOuterFieldExpression(IProperty Property, string ElementName)` — a `MongoExpression` subtype.
- Consumes (Task 1): nothing yet — this node isn't constructed by the translator until Task 3.

- [ ] **Step 1: Check for an existing renderer unit test file**

Run: `find tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation -iname "*Aggregation*"`

If a file exists, read it fully first to match its exact helper/fixture pattern (a placeholder table, a fake `IProperty`, etc.) before adding to it. If none exists, Step 2's test goes into a new file following the pattern below (modeled on how `MongoFieldExpression`/`MongoElementRefExpression` are constructed and rendered elsewhere in this codebase — a minimal fake `IProperty` via a throwaway EF model, e.g. `new ModelBuilder().Entity<T>().Property(p => p.X).Metadata`).

- [ ] **Step 2: Write the failing test**

```csharp
using Microsoft.EntityFrameworkCore;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Query.Expressions;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;
using Xunit;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query.NativeTranslation;

public class MongoAggregationExpressionRendererTests
{
    private class Widget
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
    }

    private static Microsoft.EntityFrameworkCore.Metadata.IProperty NameProperty()
    {
        var modelBuilder = new ModelBuilder();
        modelBuilder.Entity<Widget>().Property(w => w.Name);
        var model = modelBuilder.Model.FinalizeModel();
        return model.FindEntityType(typeof(Widget))!.FindProperty(nameof(Widget.Name))!;
    }

    [Fact]
    public void MongoOuterFieldExpression_renders_at_document_root_with_no_element_variable()
    {
        var node = new MongoOuterFieldExpression(NameProperty(), "Name");

        var rendered = MongoAggregationExpressionRenderer.Render(node, new PlaceholderTable());

        Assert.Equal((BsonValue)"$Name", rendered);
    }

    [Fact]
    public void MongoOuterFieldExpression_renders_at_document_root_even_inside_a_filter_scope()
    {
        // The whole point of this node: unlike MongoFieldExpression, an elementVariable in scope must NOT
        // change its rendering — it always means "the enclosing document", never "the filter's own element".
        var node = new MongoOuterFieldExpression(NameProperty(), "Name");

        var rendered = MongoAggregationExpressionRenderer.Render(node, new PlaceholderTable(), elementVariable: "e");

        Assert.Equal((BsonValue)"$Name", rendered);
    }

    [Fact]
    public void MongoOuterFieldExpression_can_render()
    {
        Assert.True(MongoAggregationExpressionRenderer.CanRender(new MongoOuterFieldExpression(NameProperty(), "Name")));
    }
}
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests -c "Debug EF10" --no-build --filter "FullyQualifiedName~MongoOuterFieldExpression"`
Expected: FAIL to compile — `MongoOuterFieldExpression` does not exist yet.

- [ ] **Step 4: Create the node type**

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

namespace MongoDB.EntityFrameworkCore.Query.Expressions;

/// <summary>
/// A field reference that always resolves at DOCUMENT ROOT, even when rendered inside a <c>$filter</c>/<c>$map</c>
/// scope that has bound its own element to a variable (see
/// <see cref="NativeTranslation.MongoAggregationExpressionRenderer"/>'s <c>elementVariable</c> parameter).
/// </summary>
/// <remarks>
/// <para>
/// A deliberate SIBLING of <see cref="MongoFieldExpression"/> rather than a flag on it (this codebase's own
/// convention for a node kind needing different handling at multiple existing sites — see
/// <see cref="MongoFilteredSizeExpression"/>'s remarks for the same reasoning). Used by a TWO-SCOPE
/// <see cref="NativeTranslation.MongoExpressionTranslator"/> to resolve a member rooted on its OUTER
/// parameter: outside any <c>$filter</c>/<c>$map</c> (e.g. a correlated <c>SelectMany</c> inner filter,
/// rendered as a top-level <c>$expr</c>) this renders identically to an ordinary <see cref="MongoFieldExpression"/>
/// at the same path — <c>elementVariable</c> is <see langword="null"/> there too. INSIDE a
/// <see cref="MongoFilteredSizeExpression"/>'s <c>$filter</c> or a quantifier's <c>$map</c>, however, an
/// ordinary <see cref="MongoFieldExpression"/> would be misread as "the filter's own element" (rendered
/// <c>"$$" + elementVariable + "." + path</c>) rather than the enclosing document this field actually belongs
/// to — this node exists specifically to keep rendering as <c>"$" + path</c> in that position too.
/// </para>
/// <para>
/// Aggregation-expression-ONLY: it has no query-dialect (<c>$match</c>) form at all, since it only has
/// meaning as an operand inside an aggregation expression that itself distinguishes an inner vs. outer scope.
/// <see cref="NativeTranslation.MongoQueryLanguageRenderer.IsQueryDialectRenderable"/> declines it
/// unconditionally.
/// </para>
/// </remarks>
internal sealed class MongoOuterFieldExpression : MongoExpression
{
    /// <summary>
    /// Creates a <see cref="MongoOuterFieldExpression"/> for the given property.
    /// </summary>
    /// <param name="property">The EF Core <see cref="IProperty"/> this field corresponds to.</param>
    /// <param name="elementName">The document element name, relative to the OUTER (enclosing) document root.</param>
    public MongoOuterFieldExpression(IProperty property, string elementName)
    {
        Property = property;
        ElementName = elementName;
    }

    /// <summary>The EF Core property metadata for this field.</summary>
    public new IProperty Property { get; }

    /// <summary>The document element name, relative to the outer document root.</summary>
    public string ElementName { get; }

    /// <inheritdoc />
    public override Type Type => Property.ClrType;
}
```

- [ ] **Step 5: Add the `Render`/`CanRender` arms**

In `MongoAggregationExpressionRenderer.cs`, add a case to the `Render` switch, immediately after the existing `MongoFieldExpression`/`MongoElementRefExpression` arms:

```csharp
            MongoFieldExpression field => FieldRef(field.ElementName, elementVariable),
            MongoElementRefExpression elementRef => FieldRef(elementRef.Path, elementVariable),
            // Always at document root, REGARDLESS of elementVariable — see the node's own remarks.
            MongoOuterFieldExpression outer => FieldRef(outer.ElementName, elementVariable: null),
```

and to `CanRender`, immediately after the existing `MongoFieldExpression or MongoElementRefExpression => true` arm:

```csharp
            MongoFieldExpression or MongoElementRefExpression or MongoOuterFieldExpression => true,
```

(replacing the existing line, not adding a duplicate).

- [ ] **Step 6: Add the `IsQueryDialectRenderable` arm**

In `MongoQueryLanguageRenderer.cs`, add immediately after the existing `MongoDateTimeOffsetLocalExpression => false,` line:

```csharp
            MongoDateTimeOffsetLocalExpression => false,
            // No query-dialect form at all — see the node's own remarks. Explicit rather than left to the
            // catch-all, matching the style of MongoConditionalExpression/MongoDatePartExpression above.
            MongoOuterFieldExpression => false,
```

- [ ] **Step 7: Add the `AllFieldsDefaultSerialized` arm**

In `MongoExpressionTranslator.cs`, in `AllFieldsDefaultSerialized`, add immediately after the existing `MongoFieldExpression f => ...` arm:

```csharp
            MongoFieldExpression f => NativeGroupByBinder.HasDefaultKeySerialization(f.Property),
            // Same correctness check as the MongoFieldExpression arm above, over the OUTER property instead.
            MongoOuterFieldExpression outerField => NativeGroupByBinder.HasDefaultKeySerialization(outerField.Property),
```

- [ ] **Step 8: Run the test to verify it passes**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests -c "Debug EF10" --no-build --filter "FullyQualifiedName~MongoOuterFieldExpression"`
Expected: PASS (3 tests).

- [ ] **Step 9: Run the full unit test suite**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests -c "Debug EF10" --no-build`
Expected: PASS, no regressions.

- [ ] **Step 10: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoOuterFieldExpression.cs \
        src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoAggregationExpressionRenderer.cs \
        src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoQueryLanguageRenderer.cs \
        src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.cs \
        tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoAggregationExpressionRendererTests.cs
git commit -m "EF-421: add MongoOuterFieldExpression node and rendering"
```

---

### Task 3: `TryResolveMember` reports `isOuter`; correlated `Count(pred)` goes native

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.Members.cs:55-132` (`TryResolveMember`)
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.cs` — every call site of `TryResolveMember` (listed in Step 2 below), plus the `Count(pred)` arm (~1037-1094)
- Test: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeOwnedCollectionCorrelatedTests.cs` (create)

**Interfaces:**
- Consumes (Task 1): `MongoExpressionTranslator.SelfParam`; `ReferencesEnclosingScope(body, elementParameter, out ParameterExpression? found)`.
- Consumes (Task 2): `MongoOuterFieldExpression`.
- Produces: `TryResolveMember(Expression node, out IProperty? property, out string? fieldPath, out bool isOuter)` — every existing caller updated.

- [ ] **Step 1: Write the failing FunctionalTest**

Create `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeOwnedCollectionCorrelatedTests.cs`, modeled directly on `NativeOwnedCollectionAllTests.cs`'s fixture/helper pattern (same `Blog`/`Post` shape, same `AssertNativeOnlyMatches`/`AssertDeclinesCleanly` idiom — copy those private helpers verbatim rather than sharing them across files, matching this codebase's existing per-file duplication convention for these test fixtures):

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
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Infrastructure;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// EF-421: a correlated owned-collection element predicate — one referencing the immediately enclosing
/// entity, e.g. <c>b.Posts.Count(p =&gt; p.Title == b.Title) &gt; 0</c> — now goes native via a two-scope
/// translator, instead of declining to driver-LINQ.
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeOwnedCollectionCorrelatedTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    private static SingleEntityDbContext<T> CreateContext<T>(
        IMongoCollection<T> collection, MongoQueryMode mode, Action<ModelBuilder>? modelBuilderAction)
        where T : class
        => SingleEntityDbContext.Create(
            collection,
            modelBuilderAction: modelBuilderAction,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });

    private string UniqueCollectionName(string name)
        => TemporaryDatabaseFixtureBase.CreateCollectionName(name) + Guid.NewGuid().ToString("N")[..8];

    public class Blog
    {
        public ObjectId Id { get; set; }
        public string Title { get; set; } = "";
        public List<Post> Posts { get; set; } = [];
    }

    public class Post
    {
        // DELIBERATELY COLLIDES with Blog.Title — a mis-scoped (element-scoped) resolution of `b.Title` would
        // retarget at Post.Title and return the wrong rows, making this seed discriminating rather than
        // vacuous. Mirrors NativeOwnedCollectionAllTests' identical seeding rationale.
        public string Title { get; set; } = "";
        public int? Rank { get; set; }
    }

    private static readonly Action<ModelBuilder> BlogModel = mb => mb.Entity<Blog>().OwnsMany(b => b.Posts);

    private IMongoCollection<Blog> Seed(string name, params (string BlogTitle, (string PostTitle, int? Rank)[] Posts)[] rows)
    {
        var coll = database.MongoDatabase.GetCollection<BsonDocument>(UniqueCollectionName(name));
        coll.InsertMany(rows.Select(r => new BsonDocument
        {
            { "_id", ObjectId.GenerateNewId() },
            { "Title", r.BlogTitle },
            { "Posts", new BsonArray(r.Posts.Select(p => new BsonDocument
                {
                    { "Title", p.PostTitle },
                    { "Rank", p.Rank.HasValue ? p.Rank.Value : BsonNull.Value }
                }))
            }
        }));
        return database.MongoDatabase.GetCollection<Blog>(coll.CollectionNamespace.CollectionName);
    }

    private List<string> AssertNativeOnlyMatches(
        IMongoCollection<Blog> collection, Func<IQueryable<Blog>, IQueryable<Blog>> query)
    {
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel);
        return query(db.Entities.AsNoTracking()).ToList().Select(b => b.Title).OrderBy(t => t).ToList();
    }

    [Fact]
    public void Correlated_filtered_Count_predicate_goes_native()
    {
        // "match": a post whose Title equals the OWNER's Title -> Count(pred) == 1 -> > 0 -> included.
        // "other": no post's Title equals the owner's Title ("other" vs post titles "x"/"y") -> excluded.
        var collection = Seed(nameof(Correlated_filtered_Count_predicate_goes_native),
            ("match", [("match", 1), ("x", 2)]),
            ("other", [("x", 1), ("y", 2)]));

        var titles = AssertNativeOnlyMatches(
            collection, q => q.Where(b => b.Posts.Count(p => p.Title == b.Title) > 0));

        Assert.Equal(new[] { "match" }, titles);
    }

    [Fact]
    public void Correlated_Any_goes_native()
    {
        var collection = Seed(nameof(Correlated_Any_goes_native),
            ("match", [("match", 1), ("x", 2)]),
            ("other", [("x", 1), ("y", 2)]));

        var titles = AssertNativeOnlyMatches(
            collection, q => q.Where(b => b.Posts.Any(p => p.Title == b.Title)));

        Assert.Equal(new[] { "match" }, titles);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails for the right reason**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests -c "Debug EF10" --no-build --filter "FullyQualifiedName~NativeOwnedCollectionCorrelatedTests.Correlated_filtered_Count_predicate_goes_native"`
Expected: FAIL with `NativeTranslationNotSupportedException` (the current decline) — `Correlated_Any_goes_native` will also fail the same way; only run/fix `Count` first, `Any` is finished in Task 5.

- [ ] **Step 3: Give `TryResolveMember` an `out bool isOuter`**

In `MongoExpressionTranslator.Members.cs`, replace the signature and body:

```csharp
    private bool TryResolveMember(
        Expression node, [NotNullWhen(true)] out IProperty? property, [NotNullWhen(true)] out string? fieldPath)
    {
        property = null;
        fieldPath = null;
```

with:

```csharp
    private bool TryResolveMember(
        Expression node, [NotNullWhen(true)] out IProperty? property, [NotNullWhen(true)] out string? fieldPath,
        out bool isOuter)
    {
        property = null;
        fieldPath = null;
        isOuter = false;
```

and later in the same method, replace:

```csharp
            default:
                return TryResolveOwnedFieldPath(node, out property, out fieldPath);
        }

        // Two-scope mode: a member rooted on the outer param resolves against the outer entity type at document
        // root; every other member is inner-scoped. Identity (ReferenceEquals), never name — so a member name
        // shared between the two scopes cannot be mis-routed.
        var isOuter = _outerParam is not null && ReferenceEquals(param, _outerParam);
        var scopeType = isOuter ? _outerEntityType! : _entityType;
```

with:

```csharp
            default:
                return TryResolveOwnedFieldPath(node, out property, out fieldPath, out isOuter);
        }

        // Two-scope mode: a member rooted on the outer param resolves against the outer entity type at document
        // root; every other member is inner-scoped. Identity (ReferenceEquals), never name — so a member name
        // shared between the two scopes cannot be mis-routed.
        isOuter = _outerParam is not null && ReferenceEquals(param, _outerParam);
        var scopeType = isOuter ? _outerEntityType! : _entityType;
```

(The local `var isOuter` declaration further down is removed since it's now the out-parameter being assigned.)

- [ ] **Step 4: Update `TryResolveOwnedFieldPath`'s signature (mechanical only for this task — full relativization is Task 6)**

For this task, `TryResolveOwnedFieldPath` keeps its EXISTING two-scope decline (Task 6 relaxes it) but must accept and always set the new out-parameter to satisfy the compiler. In `MongoExpressionTranslator.Members.cs`, change:

```csharp
    private bool TryResolveOwnedFieldPath(
        Expression node, [NotNullWhen(true)] out IProperty? property, [NotNullWhen(true)] out string? fieldPath)
    {
        property = null;
        fieldPath = null;

        if (_outerParam is not null || _innerPrefix is not null)
            return false; // two-scope mode: owned dotted paths are out of scope (declined, falls back)
```

to:

```csharp
    private bool TryResolveOwnedFieldPath(
        Expression node, [NotNullWhen(true)] out IProperty? property, [NotNullWhen(true)] out string? fieldPath,
        out bool isOuter)
    {
        property = null;
        fieldPath = null;
        isOuter = false;

        if (_outerParam is not null || _innerPrefix is not null)
            return false; // two-scope mode: owned dotted paths are out of scope (declined, falls back) — Task 6 relativizes this
```

- [ ] **Step 5: Update every `TryResolveMember`/`TryTranslateField` call site in `MongoExpressionTranslator.cs`**

Run `grep -n "TryResolveMember(" src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.cs` and update each. There are seven sites; apply exactly these rules — **decline (return null) rather than silently building a wrongly-scoped node** at every site except the two in `TranslateComparison`, which are the ones that matter for the ticket's shapes and get the real `MongoOuterFieldExpression` treatment:

**5a. `TryTranslateDateTimeMember` (~line 177):**

```csharp
        if (TryResolveMember(receiver, out var property, out var fieldPath))
        {
            receiverExpr = new MongoFieldExpression(property, fieldPath!);
        }
```

→

```csharp
        if (TryResolveMember(receiver, out var property, out var fieldPath, out var dateTimeIsOuter))
        {
            // A correlated outer-scoped DateTime/DateTimeOffset chain is out of EF-421's scope — decline
            // rather than silently building an inner-scoped field reference for an outer property.
            if (dateTimeIsOuter)
                return false;

            receiverExpr = new MongoFieldExpression(property, fieldPath!);
        }
```

**5b. Array-Contains receiver (~line 521):**

```csharp
            case MethodCallExpression call
                when TryMatchContainsMethod(call, out var arrayReceiver, out var containsItem)
                     && Unwrap(containsItem) is ConstantExpression
                     && TryResolveMember(Unwrap(arrayReceiver), out var arrayProperty, out var arrayFieldPath)
                     && GetEnumerableElementType(arrayProperty.ClrType) is not null:
```

→

```csharp
            case MethodCallExpression call
                when TryMatchContainsMethod(call, out var arrayReceiver, out var containsItem)
                     && Unwrap(containsItem) is ConstantExpression
                     && TryResolveMember(Unwrap(arrayReceiver), out var arrayProperty, out var arrayFieldPath, out var arrayIsOuter)
                     && !arrayIsOuter // an outer-scoped array receiver is out of EF-421's scope — decline
                     && GetEnumerableElementType(arrayProperty.ClrType) is not null:
```

**5c. Collection-membership item (~line 536):**

```csharp
            case MethodCallExpression call when TryMatchContainsMethod(call, out var collectionExpr, out var itemExpr):
            {
                if (!TryResolveMember(Unwrap(itemExpr), out var property, out var fieldPath))
                    return null; // item must resolve to a bare field
```

→

```csharp
            case MethodCallExpression call when TryMatchContainsMethod(call, out var collectionExpr, out var itemExpr):
            {
                if (!TryResolveMember(Unwrap(itemExpr), out var property, out var fieldPath, out var itemIsOuter)
                    || itemIsOuter) // an outer-scoped item is out of EF-421's scope — decline
                    return null; // item must resolve to a bare field
```

**5d. Regex receiver (~line 551):**

```csharp
            case MethodCallExpression call when TryMatchRegexMethod(call, out var kind, out var receiver, out var termExpr):
            {
                if (!TryResolveMember(Unwrap(receiver), out var property, out var fieldPath))
                    return null; // receiver must resolve to a bare string field
```

→

```csharp
            case MethodCallExpression call when TryMatchRegexMethod(call, out var kind, out var receiver, out var termExpr):
            {
                if (!TryResolveMember(Unwrap(receiver), out var property, out var fieldPath, out var receiverIsOuter)
                    || receiverIsOuter) // an outer-scoped receiver is out of EF-421's scope — decline
                    return null; // receiver must resolve to a bare string field
```

**5e. `HasValue` (~line 642):**

```csharp
            case MemberExpression { Member.Name: nameof(Nullable<int>.HasValue), Expression: { } hasValueReceiver }
                when Nullable.GetUnderlyingType(hasValueReceiver.Type) is not null:
            {
                if (!TryResolveMember(Unwrap(hasValueReceiver), out var nullableProperty, out var nullablePath))
                    return null;
```

→

```csharp
            case MemberExpression { Member.Name: nameof(Nullable<int>.HasValue), Expression: { } hasValueReceiver }
                when Nullable.GetUnderlyingType(hasValueReceiver.Type) is not null:
            {
                if (!TryResolveMember(Unwrap(hasValueReceiver), out var nullableProperty, out var nullablePath, out var hasValueIsOuter)
                    || hasValueIsOuter) // an outer-scoped nullable HasValue check is out of EF-421's scope — decline
                    return null;
```

**5f. Bare boolean member default (~line 654):**

```csharp
            default:
                if (TryResolveMember(node, out var boolProp, out var boolPath))
                {
                    // Accept only non-nullable bools; a nullable bool bare access could diverge.
                    if (boolProp!.ClrType != typeof(bool) || boolProp.IsNullable)
                        return null;
                    return new MongoFieldExpression(boolProp, boolPath!);
                }

                return null;
```

→

```csharp
            default:
                if (TryResolveMember(node, out var boolProp, out var boolPath, out var boolIsOuter))
                {
                    // Accept only non-nullable bools; a nullable bool bare access could diverge.
                    if (boolProp!.ClrType != typeof(bool) || boolProp.IsNullable)
                        return null;
                    return boolIsOuter
                        ? new MongoOuterFieldExpression(boolProp, boolPath!)
                        : new MongoFieldExpression(boolProp, boolPath!);
                }

                return null;
```

(This one DOES build `MongoOuterFieldExpression` on a match — a bare correlated bool, `b.Posts.Any(p => b.IsActive)`, is a real shape worth supporting since it costs nothing extra here.)

**5g/5h. `TranslateComparison`'s two call sites (~line 711 and ~line 744) — the ones that matter most:**

```csharp
        if (TryResolveMember(leftUnwrapped, out var leftProperty, out var leftPath) && IsSimpleValue(rightUnwrapped))
```

→

```csharp
        if (TryResolveMember(leftUnwrapped, out var leftProperty, out var leftPath, out var leftIsOuter)
            && IsSimpleValue(rightUnwrapped))
```

and further down in that same branch, replace:

```csharp
                return new MongoBinaryExpression(
                    mongoOp.Value, new MongoFieldExpression(leftProperty, leftPath!), valueExpr);
```

with:

```csharp
                MongoExpression leftField = leftIsOuter
                    ? new MongoOuterFieldExpression(leftProperty, leftPath!)
                    : new MongoFieldExpression(leftProperty, leftPath!);
                return new MongoBinaryExpression(mongoOp.Value, leftField, valueExpr);
```

Symmetrically for the mirrored branch:

```csharp
        else if (TryResolveMember(rightUnwrapped, out var rightProperty, out var rightPath)
                 && IsSimpleValue(leftUnwrapped))
```

→

```csharp
        else if (TryResolveMember(rightUnwrapped, out var rightProperty, out var rightPath, out var rightIsOuter)
                 && IsSimpleValue(leftUnwrapped))
```

and:

```csharp
                return new MongoBinaryExpression(
                    mongoOp.Value, new MongoFieldExpression(rightProperty, rightPath!), valueExpr);
```

→

```csharp
                MongoExpression rightField = rightIsOuter
                    ? new MongoOuterFieldExpression(rightProperty, rightPath!)
                    : new MongoFieldExpression(rightProperty, rightPath!);
                return new MongoBinaryExpression(mongoOp.Value, rightField, valueExpr);
```

> Note: `RenderComparison`/`RenderNode`'s `IsQueryNativeComparison` (`MongoQueryLanguageRenderer.cs:104`) checks `b.Left is MongoFieldExpression` — this is INTENTIONALLY unaffected: a comparison whose left side is now a `MongoOuterFieldExpression` is correctly no longer "query-native" (an outer field has no meaning as a bare `$match` clause), so it correctly routes to the `$expr`/aggregation path via `RenderNode`'s catch-all. No change needed there.

**5i. `TranslateOperand`'s own `TryResolveMember` call (~line 1016):**

```csharp
        if (TryResolveMember(node, out var property, out var fieldPath))
            return new MongoFieldExpression(property, fieldPath!);
```

→

```csharp
        if (TryResolveMember(node, out var property, out var fieldPath, out var operandIsOuter))
        {
            return operandIsOuter
                ? new MongoOuterFieldExpression(property, fieldPath!)
                : new MongoFieldExpression(property, fieldPath!);
        }
```

(This is the operand path used by arithmetic/field-to-field comparisons and computed values — e.g. `p.Rank == b.Threshold + 1` — so it also needs the real branch, not a decline.)

- [ ] **Step 6: Update `TryTranslateField` (sort-key path) — mechanical only**

In `MongoExpressionTranslator.cs`, `TryTranslateField` (~line 108-116):

```csharp
    public bool TryTranslateField(Expression keySelectorBody, [NotNullWhen(true)] out MongoFieldExpression? result)
    {
        result = null;
        if (!TryResolveMember(UnwrapOrderPreserving(keySelectorBody), out var property, out var path))
            return false;

        result = new MongoFieldExpression(property, path);
        return true;
    }
```

→

```csharp
    public bool TryTranslateField(Expression keySelectorBody, [NotNullWhen(true)] out MongoFieldExpression? result)
    {
        result = null;
        if (!TryResolveMember(UnwrapOrderPreserving(keySelectorBody), out var property, out var path, out var isOuter)
            || isOuter) // a sort key is never inside a $filter/$map scope, so an outer-scoped one has no
                         // meaning here — this translator is always the ROOT translator for a sort key
                         // (never itself the two-scope child), so isOuter is always false in practice; the
                         // guard exists so a future caller cannot silently regress this.
            return false;

        result = new MongoFieldExpression(property, path);
        return true;
    }
```

- [ ] **Step 7: Now wire the real correlation branch at the `Count(pred)` call site**

In `MongoExpressionTranslator.cs`, `TranslateOperand`'s `Count(pred)` arm (~line 1037-1094), replace:

```csharp
            if (countPredicate is null)
                return new MongoSizeExpression(arrayPath, node.Type, nullSafe: true);

            // A FILTERED count. The element predicate is translated exactly as a quantifier's is — same
            // correlated-scope guard, same element-scoped child translator.
            //
            // The correlated guard is load-bearing: single-scope TryResolveMember resolves a member by NAME
            // with no parameter-identity check, so an enclosing-scoped access whose name also exists on the
            // element would be silently retargeted at the element — wrong rows under the default Native mode.
            // A $filter cond CAN legally reference the enclosing document (unlike $elemMatch, which cannot at
            // all), so correlated support here is a deferrable capability, not an impossibility.
            if (ReferencesEnclosingScope(countPredicate.Body, countPredicate.Parameters[0]))
                return null;

            var countElementTranslator = new MongoExpressionTranslator(countElementType);
            if (!countElementTranslator.TryTranslate(countPredicate.Body, out var elementPredicate))
                return null;
```

with:

```csharp
            if (countPredicate is null)
                return new MongoSizeExpression(arrayPath, node.Type, nullSafe: true);

            // A FILTERED count. The element predicate is translated exactly as a quantifier's is — same
            // correlated-scope guard, same element-scoped child translator.
            //
            // The correlated guard is load-bearing: single-scope TryResolveMember resolves a member by NAME
            // with no parameter-identity check, so an enclosing-scoped access whose name also exists on the
            // element would be silently retargeted at the element — wrong rows under the default Native mode.
            // A $filter cond CAN legally reference the enclosing document (unlike $elemMatch, which cannot at
            // all), so correlated support here is a deferrable capability, not an impossibility.
            //
            // EF-421: a correlation whose free parameter is IDENTICAL (ReferenceEquals) to THIS translator's
            // own SelfParam is no longer declined outright — it is translated with a two-scope child
            // translator instead, mirroring NativeSelectManyBinder's existing pattern. Any other free
            // parameter (correlation reaching past the immediate enclosing scope) still declines exactly as
            // before — SelfParam is null for a translator that is itself already a two-scope child, so a
            // two-level-deep correlation can never match here.
            MongoExpressionTranslator countElementTranslator;
            if (ReferencesEnclosingScope(countPredicate.Body, countPredicate.Parameters[0], out var countFreeParam))
            {
                if (SelfParam is null || !ReferenceEquals(countFreeParam, SelfParam))
                    return null;

                countElementTranslator = new MongoExpressionTranslator(
                    countElementType, outerParam: SelfParam, outerEntityType: _entityType, innerPrefix: "");
            }
            else
            {
                countElementTranslator = new MongoExpressionTranslator(countElementType);
            }

            if (!countElementTranslator.TryTranslate(countPredicate.Body, out var elementPredicate))
                return null;
```

- [ ] **Step 8: Build**

Run: `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"`
Expected: builds clean.

- [ ] **Step 9: Run the new test to verify it passes**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests -c "Debug EF10" --no-build --filter "FullyQualifiedName~NativeOwnedCollectionCorrelatedTests.Correlated_filtered_Count_predicate_goes_native"`
Expected: PASS. (`Correlated_Any_goes_native` still fails — expected, fixed in Task 5.)

- [ ] **Step 10: Run the full existing native-translation FunctionalTests suite to confirm no regression**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests -c "Debug EF10" --no-build --filter "FullyQualifiedName~NativeOwnedCollection"`
Expected: every PRE-EXISTING test still passes (only the new file's `Correlated_Any_goes_native` fails, expected).

- [ ] **Step 11: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.cs \
        src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.Members.cs \
        tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeOwnedCollectionCorrelatedTests.cs
git commit -m "EF-421: correlated filtered Count(pred) goes native via a two-scope translator"
```

---

### Task 4: `MongoQuantifierExpression` node type + rendering

**Files:**
- Create: `src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoQuantifierExpression.cs`
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoAggregationExpressionRenderer.cs`
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoQueryLanguageRenderer.cs`
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoAggregationExpressionRendererTests.cs` (append)

**Interfaces:**
- Produces: `MongoQuantifierExpression(MongoElementRefExpression ArrayPath, MongoExpression ElementPredicate, MongoQuantifierKind Kind)`. `MongoQuantifierKind` is currently `private` inside `MongoExpressionTranslator.MethodCalls.cs` (Task 5's file) — widen it to `internal` in that same file so this node type (in `Expressions/`, a different namespace) can reference it.

- [ ] **Step 1: Widen `MongoQuantifierKind` to internal**

In `MongoExpressionTranslator.MethodCalls.cs`, change:

```csharp
    /// <summary>Which quantifier <see cref="TryMatchQuantifierMethod"/> matched.</summary>
    private enum MongoQuantifierKind
```

to:

```csharp
    /// <summary>Which quantifier <see cref="TryMatchQuantifierMethod"/> matched.</summary>
    internal enum MongoQuantifierKind
```

- [ ] **Step 2: Write the failing test**

Append to `MongoAggregationExpressionRendererTests.cs`:

```csharp
    [Fact]
    public void MongoQuantifierExpression_Any_renders_as_anyElementTrue_over_map()
    {
        var elementPredicate = new MongoBinaryExpression(
            MongoBinaryOperator.GreaterThan,
            new MongoFieldExpression(NameProperty(), "Rank"),
            new MongoConstantExpression(5, forSerialization: null));
        var node = new MongoQuantifierExpression(
            new MongoElementRefExpression("Posts", typeof(object)), elementPredicate, MongoExpressionTranslator.MongoQuantifierKind.Any);

        var rendered = MongoAggregationExpressionRenderer.Render(node, new PlaceholderTable());

        Assert.Equal(
            BsonDocument.Parse(
                "{ $anyElementTrue: { $map: { input: { $ifNull: ['$Posts', []] }, as: 'this', "
                + "in: { $gt: ['$$this.Rank', 5] } } } }"),
            rendered);
    }

    [Fact]
    public void MongoQuantifierExpression_All_renders_as_allElementsTrue_over_map()
    {
        var elementPredicate = new MongoBinaryExpression(
            MongoBinaryOperator.GreaterThan,
            new MongoFieldExpression(NameProperty(), "Rank"),
            new MongoConstantExpression(5, forSerialization: null));
        var node = new MongoQuantifierExpression(
            new MongoElementRefExpression("Posts", typeof(object)), elementPredicate, MongoExpressionTranslator.MongoQuantifierKind.All);

        var rendered = MongoAggregationExpressionRenderer.Render(node, new PlaceholderTable());

        Assert.Equal(
            BsonDocument.Parse(
                "{ $allElementsTrue: { $map: { input: { $ifNull: ['$Posts', []] }, as: 'this', "
                + "in: { $gt: ['$$this.Rank', 5] } } } }"),
            rendered);
    }

    [Fact]
    public void MongoQuantifierExpression_can_render()
    {
        var node = new MongoQuantifierExpression(
            new MongoElementRefExpression("Posts", typeof(object)),
            new MongoFieldExpression(NameProperty(), "Active"),
            MongoExpressionTranslator.MongoQuantifierKind.Any);

        Assert.True(MongoAggregationExpressionRenderer.CanRender(node));
    }
```

Add `using MongoDB.EntityFrameworkCore.Query.NativeTranslation;` if not already present (needed to reference `MongoExpressionTranslator.MongoQuantifierKind` — internal but same assembly).

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests -c "Debug EF10" --no-build --filter "FullyQualifiedName~MongoQuantifierExpression"`
Expected: FAIL to compile — `MongoQuantifierExpression` does not exist yet.

- [ ] **Step 4: Create the node type**

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
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.Query.Expressions;

/// <summary>
/// Represents a CORRELATED quantifier over an owned (embedded) array — <c>b.Posts.Any(p =&gt; p.X == b.Y)</c>
/// or the <c>All</c> equivalent — whose element predicate references the immediately enclosing entity.
/// Renders as <c>$anyElementTrue</c>/<c>$allElementsTrue</c> over a <c>$map</c>.
/// </summary>
/// <remarks>
/// <para>
/// The UNCORRELATED quantifier path is unchanged: it still uses <see cref="MongoElemMatchExpression"/>
/// (<c>$elemMatch</c>), which is more index-friendly. This node exists ONLY for the correlated case, where
/// <c>$elemMatch</c> cannot reference the enclosing document at all — <c>$anyElementTrue</c>/
/// <c>$allElementsTrue</c> over a <c>$map</c> is the only MQL form that can, since the <c>$map</c>'s own
/// <c>in</c> expression is an ordinary aggregation expression with the enclosing document still reachable.
/// </para>
/// <para>
/// UNLIKE the uncorrelated <c>All</c> path (a NEGATED <see cref="MongoElemMatchExpression"/> over the exact
/// complement, since <c>$elemMatch</c> has no native "for all" form), <c>All</c> here needs NO negation:
/// <c>$allElementsTrue</c> is itself a "for all" operator, so <see cref="ElementPredicate"/> is the predicate
/// translated DIRECTLY, exactly as <see cref="Kind"/><c> == Any</c>'s is. This sidesteps
/// <see cref="NativeTranslation.MongoExpressionNegator"/> entirely for the correlated path.
/// </para>
/// <para>
/// Aggregation-expression-ONLY — no query-dialect form. It has no query-dialect analogue at all (unlike
/// <see cref="MongoElemMatchExpression"/>, which IS query-dialect), so
/// <see cref="NativeTranslation.MongoQueryLanguageRenderer.IsQueryDialectRenderable"/> declines it
/// unconditionally, and the renderer's own top-level "no dialect form → wrap in <c>$expr</c>" fallback
/// (<see cref="NativeTranslation.MongoQueryLanguageRenderer"/>'s <c>RenderNode</c> catch-all) wraps it
/// automatically — no bespoke <c>$expr</c>-wrapping code is needed for this node.
/// </para>
/// </remarks>
internal sealed class MongoQuantifierExpression : MongoExpression
{
    /// <summary>
    /// Creates a <see cref="MongoQuantifierExpression"/>.
    /// </summary>
    /// <param name="arrayPath">
    /// The dotted document path of the embedded array, relative to the enclosing (outer) document root —
    /// e.g. <c>"Posts"</c>. Always OUTER-relative, unlike <see cref="MongoElemMatchExpression.ArrayPath"/>,
    /// because this node exists specifically for the correlated case.
    /// </param>
    /// <param name="elementPredicate">
    /// The predicate each candidate element is tested against, with ELEMENT-RELATIVE field paths for the
    /// inner scope (rendered against the <c>$map</c>'s own <c>as</c> variable) and OUTER-relative
    /// (<see cref="MongoOuterFieldExpression"/>) field paths for anything reaching the enclosing entity.
    /// </param>
    /// <param name="kind">Whether this is an <c>Any</c> or <c>All</c> quantifier.</param>
    public MongoQuantifierExpression(MongoElementRefExpression arrayPath, MongoExpression elementPredicate, MongoQuantifierKind kind)
    {
        ArrayPath = arrayPath;
        ElementPredicate = elementPredicate;
        Kind = kind;
    }

    /// <summary>The dotted document path of the embedded array, relative to the enclosing (outer) document root.</summary>
    public MongoElementRefExpression ArrayPath { get; }

    /// <summary>The predicate each candidate element is tested against.</summary>
    public MongoExpression ElementPredicate { get; }

    /// <summary>Whether this is an <c>Any</c> or <c>All</c> quantifier.</summary>
    public MongoQuantifierKind Kind { get; }

    /// <inheritdoc />
    public override Type Type => typeof(bool);
}
```

- [ ] **Step 5: Add the `Render` arm**

In `MongoAggregationExpressionRenderer.cs`, add a case to the `Render` switch, immediately before the final `_ => throw ...` catch-all:

```csharp
            MongoDatePartExpression datePart => RenderDatePart(datePart, placeholders, elementVariable),
            MongoQuantifierExpression quantifier => RenderQuantifier(quantifier, placeholders, elementVariable),
            _ => throw new NativeTranslationNotSupportedException(
                $"MongoAggregationExpressionRenderer does not support node type '{node.GetType().Name}'.")
```

and add the private method, near `RenderFilteredSize` (same nesting-variable-naming convention):

```csharp
    private static BsonValue RenderQuantifier(
        MongoQuantifierExpression node, PlaceholderTable placeholders, string? elementVariable)
    {
        // Same nesting-variable convention as RenderFilteredSize: each nesting level gets its own $map `as`
        // name, derived from the enclosing one, so nested quantifiers/filters never collide.
        var variable = elementVariable is null ? "e" : elementVariable + "e";

        var map = new BsonDocument("$map", new BsonDocument
        {
            // $ifNull is MANDATORY, same reasoning as RenderFilteredSize/RenderSize: $map over a missing or
            // explicitly-null array is a hard server error. [] yields a $map result of [], and
            // $anyElementTrue/$allElementsTrue over [] answer false/true respectively — exactly LINQ's
            // Any/All-over-an-empty-sequence semantics.
            { "input", new BsonDocument("$ifNull", new BsonArray { Render(node.ArrayPath, placeholders, elementVariable), new BsonArray() }) },
            { "as", variable },
            { "in", Render(node.ElementPredicate, placeholders, variable) }
        });

        var op = node.Kind == MongoExpressionTranslator.MongoQuantifierKind.All ? "$allElementsTrue" : "$anyElementTrue";
        return new BsonDocument(op, map);
    }
```

- [ ] **Step 6: Add the `CanRender` arm**

In `MongoAggregationExpressionRenderer.cs`, add to the `CanRender` switch, immediately before its final `_ => false`:

```csharp
            MongoDatePartExpression datePart => CanRender(datePart.Operand),
            MongoQuantifierExpression quantifier => CanRender(quantifier.ArrayPath) && CanRender(quantifier.ElementPredicate),
            _ => false
```

- [ ] **Step 7: Add the `IsQueryDialectRenderable` arm**

In `MongoQueryLanguageRenderer.cs`, add immediately after the `MongoOuterFieldExpression => false,` arm added in Task 2:

```csharp
            MongoOuterFieldExpression => false,
            MongoQuantifierExpression => false,
```

- [ ] **Step 8: Run the tests to verify they pass**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests -c "Debug EF10" --no-build --filter "FullyQualifiedName~MongoQuantifierExpression"`
Expected: PASS (3 tests). If the exact BSON key ordering in the `Assert.Equal(BsonDocument.Parse(...), rendered)` comparisons doesn't match on the first run, adjust the expected strings to match the ACTUAL rendered output (`Console.WriteLine(rendered.ToJson())` while debugging) rather than fighting `BsonDocument` equality semantics — `BsonDocument.Equals` is order-sensitive, and this plan's hand-written expected strings may not exactly match the dictionary insertion order used above; the important thing verified by the test is the fields/operators present, not this plan's exact string.

- [ ] **Step 9: Run the full unit test suite**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests -c "Debug EF10" --no-build`
Expected: PASS, no regressions.

- [ ] **Step 10: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoQuantifierExpression.cs \
        src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.MethodCalls.cs \
        src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoAggregationExpressionRenderer.cs \
        src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoQueryLanguageRenderer.cs \
        tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoAggregationExpressionRendererTests.cs
git commit -m "EF-421: add MongoQuantifierExpression node and \$anyElementTrue/\$allElementsTrue rendering"
```

---

### Task 5: Correlated `Any`/`All` go native

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.cs:567-628` (quantifier arm in `TranslateNode`)
- Test: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeOwnedCollectionCorrelatedTests.cs` (extend)

**Interfaces:**
- Consumes (Task 1): `SelfParam`, `ReferencesEnclosingScope(..., out found)`.
- Consumes (Task 2, 4): `MongoOuterFieldExpression`, `MongoQuantifierExpression`.

- [ ] **Step 1: `Correlated_Any_goes_native` (already written in Task 3) should still be failing — confirm**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests -c "Debug EF10" --no-build --filter "FullyQualifiedName~Correlated_Any_goes_native"`
Expected: FAIL (`NativeTranslationNotSupportedException`) — this is the RED for this task.

- [ ] **Step 2: Add a correlated `All` test to the same file**

Append to `NativeOwnedCollectionCorrelatedTests.cs`:

```csharp
    [Fact]
    public void Correlated_All_goes_native()
    {
        // "allMatch": every post's Title equals the owner's Title -> All true -> included.
        // "notAll": one post's Title does NOT equal the owner's Title -> All false -> excluded.
        var collection = Seed(nameof(Correlated_All_goes_native),
            ("same", [("same", 1), ("same", 2)]),
            ("mixed", [("mixed", 1), ("other", 2)]));

        var titles = AssertNativeOnlyMatches(
            collection, q => q.Where(b => b.Posts.All(p => p.Title == b.Title)));

        Assert.Equal(new[] { "same" }, titles);
    }

    [Fact]
    public void Correlated_Any_and_All_are_correct_against_an_in_memory_oracle()
    {
        // Differential check, mirroring NativeOwnedCollectionAllTests' own matrix pattern: the SAME
        // expression evaluated in memory over materialized rows must agree with the native (NativeOnly)
        // result — proving this isn't merely "doesn't throw" but answers the correct rows, including the
        // empty-Posts / no-match / all-match / one-mismatch states.
        var collection = Seed(nameof(Correlated_Any_and_All_are_correct_against_an_in_memory_oracle),
            ("same", [("same", 1), ("same", 2)]),
            ("mixed", [("mixed", 1), ("other", 2)]),
            ("nomatch", [("x", 1), ("y", 2)]),
            ("empty", []));

        System.Linq.Expressions.Expression<Func<Blog, bool>>[] predicates =
        [
            b => b.Posts.Any(p => p.Title == b.Title),
            b => b.Posts.All(p => p.Title == b.Title),
            b => b.Posts.Count(p => p.Title == b.Title) > 0
        ];

        foreach (var predicate in predicates)
        {
            List<string> expected;
            using (var db = CreateContext(collection, MongoQueryMode.Native, BlogModel))
            {
                expected = db.Entities.AsNoTracking().ToList()
                    .Where(predicate.Compile()).Select(b => b.Title).OrderBy(t => t).ToList();
            }

            List<string> actual;
            using (var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel))
            {
                actual = db.Entities.AsNoTracking().Where(predicate).ToList()
                    .Select(b => b.Title).OrderBy(t => t).ToList();
            }

            Assert.Equal(expected, actual);
        }
    }
```

- [ ] **Step 3: Run to verify RED**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests -c "Debug EF10" --no-build --filter "FullyQualifiedName~NativeOwnedCollectionCorrelatedTests"`
Expected: `Correlated_filtered_Count_predicate_goes_native` PASSES (from Task 3); `Correlated_Any_goes_native`, `Correlated_All_goes_native`, `Correlated_Any_and_All_are_correct_against_an_in_memory_oracle` FAIL.

- [ ] **Step 4: Implement the correlated quantifier branch**

In `MongoExpressionTranslator.cs`, replace the quantifier arm's correlation check and everything through the `MongoElemMatchExpression` return:

```csharp
                // A CORRELATED element predicate — one reaching outside the element into the enclosing entity —
                // must be declined BEFORE the element-scoped translator ever sees it. See the helper's remarks:
                // the element-scoped translator resolves a member by NAME alone, so an enclosing-scoped access
                // whose name also exists on the element would be silently retargeted at the element. This
                // applies to All exactly as it does to Any.
                if (ReferencesEnclosingScope(elementLambda.Body, elementLambda.Parameters[0]))
                    return null;

                // Translate the element predicate with an ELEMENT-SCOPED translator: its field paths come out
                // element-relative, which is what $elemMatch requires. This is the mirror image of
                // NativeSelectManyBinder.TryBuildOwnedInnerFilter, which translates the same way and then
                // PREFIXES the result with the unwind path.
                var elementTranslator = new MongoExpressionTranslator(elementType);
                if (!elementTranslator.TryTranslate(elementLambda.Body, out var translated))
                    return null;

                MongoExpression child = translated;
                var negated = false;

                if (quantifier is MongoQuantifierKind.All)
                {
                    // All(pred) is true exactly when NO element satisfies ¬pred, i.e. a negated $elemMatch
                    // over the EXACT complement. That form is also correct for an empty, missing, or
                    // explicitly-null array: nothing satisfies the $elemMatch, so the enclosing $not matches
                    // and All is true — which is what LINQ's All over an empty sequence returns.
                    //
                    // A predicate with no exact complement declines the whole quantifier (clean fallback to
                    // driver-LINQ) rather than emitting an approximation, which would return wrong rows.
                    if (!MongoExpressionNegator.TryNegate(child, out var complement))
                        return null;

                    child = complement;
                    negated = true;
                }

                // $expr is not usable inside $elemMatch, and RenderNode's catch-all would silently wrap a
                // non-query-dialect child in $expr. Decline here (translate time) so the query falls back to
                // driver-LINQ instead. For All this is belt-and-braces — the negator gates on the same
                // classifier — but it stays because it is the invariant the renderer's contract depends on.
                if (!MongoQueryLanguageRenderer.IsQueryDialectRenderable(child))
                    return null;

                return new MongoElemMatchExpression(arrayPath, child, negated);
```

with:

```csharp
                // A CORRELATED element predicate — one reaching outside the element into the enclosing entity.
                //
                // EF-421: no longer an outright decline. If the free parameter found is IDENTICAL
                // (ReferenceEquals) to THIS translator's own SelfParam, build a two-scope child translator and
                // emit a MongoQuantifierExpression ($anyElementTrue/$allElementsTrue over $map) instead of the
                // ordinary $elemMatch path below — $elemMatch cannot reference the enclosing document at all,
                // so that dialect is unusable here regardless of scope handling. Any OTHER free parameter
                // (correlation reaching past the immediate enclosing scope) still declines exactly as before.
                if (ReferencesEnclosingScope(elementLambda.Body, elementLambda.Parameters[0], out var quantifierFreeParam))
                {
                    if (SelfParam is null || !ReferenceEquals(quantifierFreeParam, SelfParam))
                        return null;

                    var correlatedElementTranslator = new MongoExpressionTranslator(
                        elementType, outerParam: SelfParam, outerEntityType: _entityType, innerPrefix: "");
                    if (!correlatedElementTranslator.TryTranslate(elementLambda.Body, out var correlatedPredicate))
                        return null;

                    // No negation for All here, unlike the uncorrelated path below: $allElementsTrue is
                    // itself a "for all" operator, so the predicate translates DIRECTLY for both Any and All.
                    if (!MongoAggregationExpressionRenderer.CanRender(correlatedPredicate))
                        return null; // no equivalent graceful degrade exists for this node — decline cleanly

                    return new MongoQuantifierExpression(
                        new MongoElementRefExpression(arrayPath, quantifierSource.Type), correlatedPredicate, quantifier);
                }

                // Translate the element predicate with an ELEMENT-SCOPED translator: its field paths come out
                // element-relative, which is what $elemMatch requires. This is the mirror image of
                // NativeSelectManyBinder.TryBuildOwnedInnerFilter, which translates the same way and then
                // PREFIXES the result with the unwind path.
                var elementTranslator = new MongoExpressionTranslator(elementType);
                if (!elementTranslator.TryTranslate(elementLambda.Body, out var translated))
                    return null;

                MongoExpression child = translated;
                var negated = false;

                if (quantifier is MongoQuantifierKind.All)
                {
                    // All(pred) is true exactly when NO element satisfies ¬pred, i.e. a negated $elemMatch
                    // over the EXACT complement. That form is also correct for an empty, missing, or
                    // explicitly-null array: nothing satisfies the $elemMatch, so the enclosing $not matches
                    // and All is true — which is what LINQ's All over an empty sequence returns.
                    //
                    // A predicate with no exact complement declines the whole quantifier (clean fallback to
                    // driver-LINQ) rather than emitting an approximation, which would return wrong rows.
                    if (!MongoExpressionNegator.TryNegate(child, out var complement))
                        return null;

                    child = complement;
                    negated = true;
                }

                // $expr is not usable inside $elemMatch, and RenderNode's catch-all would silently wrap a
                // non-query-dialect child in $expr. Decline here (translate time) so the query falls back to
                // driver-LINQ instead. For All this is belt-and-braces — the negator gates on the same
                // classifier — but it stays because it is the invariant the renderer's contract depends on.
                if (!MongoQueryLanguageRenderer.IsQueryDialectRenderable(child))
                    return null;

                return new MongoElemMatchExpression(arrayPath, child, negated);
```

- [ ] **Step 5: Build**

Run: `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"`
Expected: builds clean.

- [ ] **Step 6: Run to verify GREEN**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests -c "Debug EF10" --no-build --filter "FullyQualifiedName~NativeOwnedCollectionCorrelatedTests"`
Expected: all 5 tests PASS.

- [ ] **Step 7: Run the full existing native-translation FunctionalTests suite to confirm no regression**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests -c "Debug EF10" --no-build --filter "FullyQualifiedName~NativeOwnedCollection"`
Expected: every test passes, including `NativeOwnedCollectionAllTests.All_with_a_correlated_element_predicate_declines_and_falls_back_to_correct_rows` — **check this one specifically**: it currently asserts a DECLINE (`AssertDeclinesCleanly`) for `b.Posts.All(p => b.Title == "match")`. After this task, that shape now GOES NATIVE, so this specific test will now FAIL (its `NativeOnly` leg no longer throws). This is expected — proceed to Task 8, which retires/rewrites this specific test; do not attempt to "fix" it in this task.

- [ ] **Step 8: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.cs \
        tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeOwnedCollectionCorrelatedTests.cs
git commit -m "EF-421: correlated Any/All quantifiers go native via \$anyElementTrue/\$allElementsTrue"
```

---

### Task 6: Relativize `TryResolveOwnedFieldPath`/`TryResolveOwnedCollectionPath`

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.Members.cs:171-238` (`TryResolveOwnedFieldPath`), `:310-361` (`TryResolveOwnedCollectionPath`)
- Test: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeOwnedCollectionCorrelatedTests.cs` (extend)

**Interfaces:**
- Produces: `TryResolveOwnedCollectionPath(Expression source, out string? arrayPath, out IEntityType? elementType, out bool isOuter)` (new out-param) — check callers (`MongoExpressionTranslator.cs`'s quantifier and `Count`/`TryTranslateOwnedCollectionArray` sites) and thread through with the same decline-unless-vetted discipline as Task 3.

- [ ] **Step 1: Write the failing test — a correlated element predicate reaching through an OUTER owned single-reference hop**

Append to `NativeOwnedCollectionCorrelatedTests.cs`:

```csharp
    public class RefBlog
    {
        public ObjectId Id { get; set; }
        public string Title { get; set; } = "";
        public RefOwner Owner { get; set; } = null!;
        public List<RefPost> Posts { get; set; } = [];
    }

    public class RefOwner
    {
        public string City { get; set; } = "";
    }

    public class RefPost
    {
        public string City { get; set; } = "";
    }

    private static readonly Action<ModelBuilder> RefBlogModel = mb =>
    {
        mb.Entity<RefBlog>().OwnsOne(b => b.Owner);
        mb.Entity<RefBlog>().OwnsMany(b => b.Posts);
    };

    [Fact]
    public void Correlated_Any_through_an_outer_owned_single_reference_hop_goes_native()
    {
        var raw = database.MongoDatabase.GetCollection<BsonDocument>(
            UniqueCollectionName(nameof(Correlated_Any_through_an_outer_owned_single_reference_hop_goes_native)));
        var collection = database.MongoDatabase.GetCollection<RefBlog>(raw.CollectionNamespace.CollectionName);

        using (var seedDb = CreateContext(collection, MongoQueryMode.DriverLinq, RefBlogModel))
        {
            seedDb.Entities.Add(new RefBlog
            {
                Title = "match", Owner = new RefOwner { City = "Springfield" },
                Posts = [new RefPost { City = "Springfield" }, new RefPost { City = "Shelbyville" }]
            });
            seedDb.Entities.Add(new RefBlog
            {
                Title = "nomatch", Owner = new RefOwner { City = "Springfield" },
                Posts = [new RefPost { City = "Shelbyville" }]
            });
            seedDb.SaveChanges();
        }

        using var db = CreateContext(collection, MongoQueryMode.NativeOnly, RefBlogModel);

        var titles = db.Entities.AsNoTracking()
            .Where(b => b.Posts.Any(p => p.City == b.Owner.City))
            .ToList().Select(b => b.Title).ToList();

        Assert.Equal(new[] { "match" }, titles);
    }
```

- [ ] **Step 2: Run to verify RED**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests -c "Debug EF10" --no-build --filter "FullyQualifiedName~Correlated_Any_through_an_outer_owned_single_reference_hop_goes_native"`
Expected: FAIL with `NativeTranslationNotSupportedException` — `TryResolveOwnedFieldPath` still declines two-scope mode outright, so `b.Owner.City` (a 2-hop chain on the OUTER parameter) fails to resolve inside the correlated element translator built in Task 5, and `MongoAggregationExpressionRenderer.CanRender` (or the inner `TryTranslate`) fails.

- [ ] **Step 3: Relativize `TryResolveOwnedFieldPath`**

Replace:

```csharp
    private bool TryResolveOwnedFieldPath(
        Expression node, [NotNullWhen(true)] out IProperty? property, [NotNullWhen(true)] out string? fieldPath,
        out bool isOuter)
    {
        property = null;
        fieldPath = null;
        isOuter = false;

        if (_outerParam is not null || _innerPrefix is not null)
            return false; // two-scope mode: owned dotted paths are out of scope (declined, falls back) — Task 6 relativizes this

        // Collect hop names from the outer (leaf) hop inward; the root must be the query parameter.
        var names = new List<string>();
        var current = node;
        while (TryGetMemberOrEFProperty(current, out var inner, out var name))
        {
            names.Add(name);
            current = inner;
        }

        if (current is not ParameterExpression)
            return false;

        // A single top-level member is handled by TryResolveMember's fast path, never here.
        if (names.Count < 2)
            return false;

        names.Reverse(); // now root-first: [firstNav, ..., leaf]

        var scopeType = _entityType;
        var segments = new List<string>(names.Count);
```

with:

```csharp
    private bool TryResolveOwnedFieldPath(
        Expression node, [NotNullWhen(true)] out IProperty? property, [NotNullWhen(true)] out string? fieldPath,
        out bool isOuter)
    {
        property = null;
        fieldPath = null;
        isOuter = false;

        // A dotted chain reached from an INNER-prefixed scope (SelectMany's unwind prefix) still declines: that
        // shape is a different, still-out-of-scope combination this ticket does not address (a dotted owned
        // path reached from inside a SelectMany element, itself further correlated). Only the "root is the
        // OUTER param" two-scope case is relativized here — see EF-421 §6.
        if (_innerPrefix is not null)
            return false;

        // Collect hop names from the outer (leaf) hop inward; the root must be a parameter.
        var names = new List<string>();
        var current = node;
        while (TryGetMemberOrEFProperty(current, out var inner, out var name))
        {
            names.Add(name);
            current = inner;
        }

        if (current is not ParameterExpression rootParam)
            return false;

        // A single top-level member is handled by TryResolveMember's fast path, never here.
        if (names.Count < 2)
            return false;

        // EF-421: resolve the hop's ROOT identity, mirroring TryResolveMember's own isOuter check — never by
        // name. A two-scope translator whose root is the OUTER param resolves against the outer entity type,
        // at the outer document's OWN root (segments accumulate relative to _outerEntityType, exactly as the
        // existing code below already does relative to _entityType — the scopeType seed is the only change).
        // A single-scope translator (_outerParam is null) is unaffected: isOuter is always false there.
        isOuter = _outerParam is not null && ReferenceEquals(rootParam, _outerParam);
        if (_outerParam is not null && !isOuter)
            return false; // two-scope mode, but rooted on neither known parameter — not reachable in practice,
                           // decline rather than guess which scope it belongs to

        names.Reverse(); // now root-first: [firstNav, ..., leaf]

        var scopeType = isOuter ? _outerEntityType! : _entityType;
        var segments = new List<string>(names.Count);
```

The remainder of the method (the hop-walking loop and leaf construction) is UNCHANGED — it already operates on the local `scopeType`/`segments` variables, which now correctly seed from either scope.

- [ ] **Step 4: Update `TryResolveMember`'s default-branch call to pass through `isOuter`** (already done in Task 3, Step 3 — confirm no further change needed by re-reading that call site)

- [ ] **Step 5: Relativize `TryResolveOwnedCollectionPath`**

Replace:

```csharp
    private bool TryResolveOwnedCollectionPath(
        Expression source,
        [NotNullWhen(true)] out string? arrayPath,
        [NotNullWhen(true)] out IEntityType? elementType)
    {
        arrayPath = null;
        elementType = null;

        if (_outerParam is not null || _innerPrefix is not null)
            return false; // two-scope mode: cross-scope quantifiers are out of scope (declined, falls back)

        // Collect hop names from the outer hop inward; the root must be the query parameter.
        var names = new List<string>();
        var current = source;
        while (TryGetMemberOrEFProperty(current, out var inner, out var name))
        {
            names.Add(name);
            current = inner;
        }

        if (current is not ParameterExpression || names.Count == 0)
            return false;

        names.Reverse(); // now root-first: [ownedRefNav, ..., collectionNav]

        var scopeType = _entityType;
        var segments = new List<string>(names.Count);
```

with:

```csharp
    private bool TryResolveOwnedCollectionPath(
        Expression source,
        [NotNullWhen(true)] out string? arrayPath,
        [NotNullWhen(true)] out IEntityType? elementType,
        out bool isOuter)
    {
        arrayPath = null;
        elementType = null;
        isOuter = false;

        // Same restriction as TryResolveOwnedFieldPath: an inner-prefixed (SelectMany unwind) scope still
        // declines outright — only the "root is the OUTER param" case is relativized.
        if (_innerPrefix is not null)
            return false;

        // Collect hop names from the outer hop inward; the root must be a parameter.
        var names = new List<string>();
        var current = source;
        while (TryGetMemberOrEFProperty(current, out var inner, out var name))
        {
            names.Add(name);
            current = inner;
        }

        if (current is not ParameterExpression rootParam || names.Count == 0)
            return false;

        isOuter = _outerParam is not null && ReferenceEquals(rootParam, _outerParam);
        if (_outerParam is not null && !isOuter)
            return false; // two-scope mode, rooted on neither known parameter — decline

        names.Reverse(); // now root-first: [ownedRefNav, ..., collectionNav]

        var scopeType = isOuter ? _outerEntityType! : _entityType;
        var segments = new List<string>(names.Count);
```

The remainder of the method is UNCHANGED.

- [ ] **Step 6: Update `TryResolveOwnedCollectionPath`'s TWO callers in `MongoExpressionTranslator.cs`**

Run `grep -n "TryResolveOwnedCollectionPath(" src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.cs`. There are three call sites:

**6a. `TryTranslateOwnedCollectionArray` (~line 249):**

```csharp
        var source = UnwrapAsQueryable(expression);
        if (TryResolveOwnedCollectionPath(source, out var arrayPath, out _))
```

→

```csharp
        var source = UnwrapAsQueryable(expression);
        if (TryResolveOwnedCollectionPath(source, out var arrayPath, out _, out var arrayIsOuter) && !arrayIsOuter)
```

(A projection leaf is built by a root-scoped `NativeProjectionBinder` translator, which is never itself a two-scope child — `isOuter` is always false there in practice; the guard exists defensively so a future caller cannot silently regress this, matching the pattern from Task 3's `TryTranslateField`.)

**6b. Quantifier arm (~line 570):**

```csharp
                if (!TryResolveOwnedCollectionPath(Unwrap(quantifierSource), out var arrayPath, out var elementType))
                    return null; // not an owned-collection source rooted at the query parameter
```

→

```csharp
                if (!TryResolveOwnedCollectionPath(Unwrap(quantifierSource), out var arrayPath, out var elementType, out var sourceIsOuter))
                    return null; // not an owned-collection source rooted at the query parameter

                if (sourceIsOuter)
                    return null; // the ARRAY itself being reached through the outer scope is a separate, not-yet-supported shape (e.g. a quantifier over an outer sibling collection) — out of EF-421's scope
```

**6c. `Count(pred)` arm (~line 1038):**

```csharp
        if (TryMatchCountExpression(node, out var countSource, out var countPredicate)
            && TryResolveOwnedCollectionPath(countSource, out var arrayPath, out var countElementType))
        {
```

→

```csharp
        if (TryMatchCountExpression(node, out var countSource, out var countPredicate)
            && TryResolveOwnedCollectionPath(countSource, out var arrayPath, out var countElementType, out var countSourceIsOuter)
            && !countSourceIsOuter) // same out-of-scope note as the quantifier arm above
        {
```

- [ ] **Step 7: Build**

Run: `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"`
Expected: builds clean.

- [ ] **Step 8: Run to verify GREEN**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests -c "Debug EF10" --no-build --filter "FullyQualifiedName~Correlated_Any_through_an_outer_owned_single_reference_hop_goes_native"`
Expected: PASS.

- [ ] **Step 9: Run the full `NativeOwnedCollection*` and `NativeSelectMany*` suites to confirm no regression**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests -c "Debug EF10" --no-build --filter "FullyQualifiedName~NativeOwnedCollection|FullyQualifiedName~NativeSelectMany"`
Expected: every PRE-EXISTING test passes (the one already-known exception from Task 5, `All_with_a_correlated_element_predicate_declines_and_falls_back_to_correct_rows`, is still expected to fail here — resolved in Task 8). `NativeSelectMany*` in particular must be UNCHANGED — this is the regression check for Task 6's edit to a method `NativeSelectManyBinder` does NOT call (only `MongoExpressionTranslator`'s own quantifier/count arms call `TryResolveOwnedCollectionPath`/`TryResolveOwnedFieldPath`), but re-run it anyway since `TryResolveMember`'s two-scope branch (shared code) was touched in Task 3.

- [ ] **Step 10: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.Members.cs \
        src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.cs \
        tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeOwnedCollectionCorrelatedTests.cs
git commit -m "EF-421: relativize TryResolveOwnedFieldPath/TryResolveOwnedCollectionPath for the outer scope"
```

---

### Task 7: Wire `SelfParam` through `NativeProjectionBinder`, `NativeCardinalityBinder`, `NativeVectorSearchBinder`, and `NativeSlotPopulator`'s remaining arms

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeSlotPopulator.cs:41-104`
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeProjectionBinder.cs:43-45`
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeCardinalityBinder.cs:83-102`
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeVectorSearchBinder.cs:52-70`
- Test: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeOwnedCollectionCorrelatedTests.cs` (extend)

**Interfaces:**
- Consumes (Task 1): `MongoExpressionTranslator.SelfParam` (settable).

- [ ] **Step 1: Write the failing tests — correlation reachable from a sort key, a projection, and a scalar-aggregate predicate**

Append to `NativeOwnedCollectionCorrelatedTests.cs`:

```csharp
    [Fact]
    public void Correlated_Count_as_a_computed_sort_key_goes_native()
    {
        var collection = Seed(nameof(Correlated_Count_as_a_computed_sort_key_goes_native),
            ("a", [("a", 1)]),                 // 1 matching post
            ("b", [("x", 1), ("y", 2)]),       // 0 matching posts
            ("c", [("c", 1), ("c", 2)]));      // 2 matching posts

        using var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel);

        var titles = db.Entities.AsNoTracking()
            .OrderBy(b => b.Posts.Count(p => p.Title == b.Title))
            .Select(b => b.Title).ToList();

        Assert.Equal(new[] { "b", "a", "c" }, titles);
    }

    [Fact]
    public void Correlated_Any_inside_a_projection_leaf_goes_native()
    {
        var collection = Seed(nameof(Correlated_Any_inside_a_projection_leaf_goes_native),
            ("match", [("match", 1)]),
            ("nomatch", [("x", 1)]));

        using var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel);

        var results = db.Entities.AsNoTracking()
            .Select(b => new { b.Title, HasMatchingPost = b.Posts.Any(p => p.Title == b.Title) })
            .OrderBy(r => r.Title).ToList();

        Assert.Equal(
            new[] { (Title: "match", HasMatchingPost: true), (Title: "nomatch", HasMatchingPost: false) },
            results.Select(r => (r.Title, r.HasMatchingPost)).ToArray());
    }

    [Fact]
    public void Correlated_All_inside_a_scalar_aggregate_predicate_goes_native()
    {
        var collection = Seed(nameof(Correlated_All_inside_a_scalar_aggregate_predicate_goes_native),
            ("same", [("same", 1), ("same", 2)]),
            ("mixed", [("mixed", 1), ("other", 2)]));

        using var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel);

        // Root-level All(pred) is itself a scalar-aggregate terminal (NativeCardinalityBinder.TryBindAggregate)
        // whose OWN predicate lambda parameter becomes SelfParam for the translator that predicate is built
        // with — so a correlated Any/All/Count(pred) NESTED inside it can match against that root b.
        var allBlogsHaveAMatchingPost = db.Entities.AsNoTracking()
            .All(b => b.Posts.Any(p => p.Title == b.Title));

        Assert.False(allBlogsHaveAMatchingPost); // "mixed" has no post titled "mixed"... wait it does; recompute below
    }
```

> **Author note for the implementer:** the last test's assertion needs to be worked out by hand against the actual seed before finalizing — re-derive it: "same" has a post titled "same" (matches), "mixed" has a post titled "mixed" (matches) too. So `Any(p => p.Title == b.Title)` is true for BOTH rows here, making `All(...)` true, not false. Fix the assertion to `Assert.True(allBlogsHaveAMatchingPost)` before running, or adjust the seed to include a row with no matching post (e.g. add `("neither", [("x", 1), ("y", 2)])` to the seed and keep the `False` assertion). Pick whichever reads more clearly; this is a one-line fix caught at Step 2 (RED) — do not skip re-deriving it by hand, since an aggregate scalar result gives no row-level signal to debug from if the expectation is wrong in the same direction as a real bug.

- [ ] **Step 2: Run to verify RED (and fix the test-data note above first)**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests -c "Debug EF10" --no-build --filter "FullyQualifiedName~Correlated_Count_as_a_computed_sort_key_goes_native|FullyQualifiedName~Correlated_Any_inside_a_projection_leaf_goes_native|FullyQualifiedName~Correlated_All_inside_a_scalar_aggregate_predicate_goes_native"`
Expected: all three FAIL — the sort-key/projection/aggregate root translators don't set `SelfParam` yet, so `ReferencesEnclosingScope`'s match check never succeeds and every one declines to driver-LINQ (which, since it's NOT `NativeOnly`... wait, these tests run under `NativeOnly`/`AsNoTracking` — so a decline surfaces as `NativeTranslationNotSupportedException`, not silently wrong data. Confirm that's the actual failure mode for all three (it should be, since none of these three call sites have been fixed yet).

- [ ] **Step 3: Wire `NativeSlotPopulator`'s `Where`/`OrderBy`/`ThenBy` arms**

In `NativeSlotPopulator.cs`, the shared `translator` instance (line 47) currently has no `SelfParam` set at construction. Update each arm that uses it to set `SelfParam` immediately before translating. Replace:

```csharp
        if (methodDefinition == QueryableMethods.Where)
        {
            // PipelineOps are emitted verbatim in arrival order: a Where (-> $match) applied after paging is
            // recorded after it too, and the lowerer emits ops in that same order — correct by MongoDB's
            // sequential pipeline semantics. No canonical-order guard.
            var predicate = call.Arguments[1].UnwrapLambdaFromQuote();
            if (translator.TryTranslate(predicate.Body, out var predicateNode))
                mongoQ.Select.AddPredicateConjunct(predicateNode);
            else
                mongoQ.Select.MarkNotNativelyRepresentable();
        }
        else if (methodDefinition == QueryableMethods.OrderBy || methodDefinition == QueryableMethods.OrderByDescending)
        {
            var keySelector = call.Arguments[1].UnwrapLambdaFromQuote();
            var ascending = methodDefinition == QueryableMethods.OrderBy;
            if (translator.TryTranslateField(keySelector.Body, out var keyNode))
                mongoQ.Select.StartOrReplaceSort(new MongoOrdering(keyNode, ascending));
            else if (TryTranslateComputedSortKey(translator, keySelector.Body, out var computedKey))
                mongoQ.Select.StartOrReplaceSort(new MongoOrdering(computedKey, ascending));
            else
                mongoQ.Select.MarkNotNativelyRepresentable();
        }
        else if (methodDefinition == QueryableMethods.ThenBy || methodDefinition == QueryableMethods.ThenByDescending)
        {
            var keySelector = call.Arguments[1].UnwrapLambdaFromQuote();
            var ascending = methodDefinition == QueryableMethods.ThenBy;
            if (translator.TryTranslateField(keySelector.Body, out var keyNode))
                mongoQ.Select.AppendThenBy(new MongoOrdering(keyNode, ascending));
            else if (TryTranslateComputedSortKey(translator, keySelector.Body, out var computedKey))
                mongoQ.Select.AppendThenBy(new MongoOrdering(computedKey, ascending));
            else
                mongoQ.Select.MarkNotNativelyRepresentable();
        }
```

with:

```csharp
        if (methodDefinition == QueryableMethods.Where)
        {
            // PipelineOps are emitted verbatim in arrival order: a Where (-> $match) applied after paging is
            // recorded after it too, and the lowerer emits ops in that same order — correct by MongoDB's
            // sequential pipeline semantics. No canonical-order guard.
            var predicate = call.Arguments[1].UnwrapLambdaFromQuote();
            translator.SelfParam = predicate.Parameters[0];
            if (translator.TryTranslate(predicate.Body, out var predicateNode))
                mongoQ.Select.AddPredicateConjunct(predicateNode);
            else
                mongoQ.Select.MarkNotNativelyRepresentable();
        }
        else if (methodDefinition == QueryableMethods.OrderBy || methodDefinition == QueryableMethods.OrderByDescending)
        {
            var keySelector = call.Arguments[1].UnwrapLambdaFromQuote();
            var ascending = methodDefinition == QueryableMethods.OrderBy;
            translator.SelfParam = keySelector.Parameters[0];
            if (translator.TryTranslateField(keySelector.Body, out var keyNode))
                mongoQ.Select.StartOrReplaceSort(new MongoOrdering(keyNode, ascending));
            else if (TryTranslateComputedSortKey(translator, keySelector.Body, out var computedKey))
                mongoQ.Select.StartOrReplaceSort(new MongoOrdering(computedKey, ascending));
            else
                mongoQ.Select.MarkNotNativelyRepresentable();
        }
        else if (methodDefinition == QueryableMethods.ThenBy || methodDefinition == QueryableMethods.ThenByDescending)
        {
            var keySelector = call.Arguments[1].UnwrapLambdaFromQuote();
            var ascending = methodDefinition == QueryableMethods.ThenBy;
            translator.SelfParam = keySelector.Parameters[0];
            if (translator.TryTranslateField(keySelector.Body, out var keyNode))
                mongoQ.Select.AppendThenBy(new MongoOrdering(keyNode, ascending));
            else if (TryTranslateComputedSortKey(translator, keySelector.Body, out var computedKey))
                mongoQ.Select.AppendThenBy(new MongoOrdering(computedKey, ascending));
            else
                mongoQ.Select.MarkNotNativelyRepresentable();
        }
```

(`TryTranslateField`'s own `isOuter` guard from Task 3 Step 6 means setting `SelfParam` here is harmless for the PLAIN sort-key path — `TryTranslateField` never itself becomes a two-scope translator, so `isOuter` there is always false; `SelfParam` only matters for a NESTED `Count(pred)`/`Any`/`All` reached via `TryTranslateComputedSortKey`'s `TryTranslateValue` → `TranslateOperand` path.)

- [ ] **Step 4: Wire `NativeProjectionBinder`**

In `NativeProjectionBinder.cs`, replace:

```csharp
        var translator = new MongoExpressionTranslator(mongoQ.CollectionExpression.EntityType);
```

with:

```csharp
        var translator = new MongoExpressionTranslator(mongoQ.CollectionExpression.EntityType, selector.Parameters[0]);
```

- [ ] **Step 5: Wire `NativeCardinalityBinder.TryBindAggregate`**

In `NativeCardinalityBinder.cs`, replace:

```csharp
        var translator = new MongoExpressionTranslator(mongoQ.CollectionExpression.EntityType);
```

with:

```csharp
        // predicate is null for Sum/Min/Max/Average (which take a selector instead) and for a bare Any()/Count()
        // with no predicate — SelfParam stays null in those cases, which is fine: there is no predicate lambda
        // for a nested Count(pred)/Any/All to correlate against anyway.
        var translator = new MongoExpressionTranslator(mongoQ.CollectionExpression.EntityType, predicate?.Parameters[0]);
```

- [ ] **Step 6: Wire `NativeVectorSearchBinder`**

In `NativeVectorSearchBinder.cs`, replace:

```csharp
            var preFilterLambda = call.Arguments[2].UnwrapLambdaFromQuote();
            if (!new MongoExpressionTranslator(entityType).TryTranslate(preFilterLambda.Body, out preFilter))
```

with:

```csharp
            var preFilterLambda = call.Arguments[2].UnwrapLambdaFromQuote();
            if (!new MongoExpressionTranslator(entityType, preFilterLambda.Parameters[0]).TryTranslate(preFilterLambda.Body, out preFilter))
```

- [ ] **Step 7: Build**

Run: `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"`
Expected: builds clean.

- [ ] **Step 8: Run to verify GREEN**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests -c "Debug EF10" --no-build --filter "FullyQualifiedName~Correlated_Count_as_a_computed_sort_key_goes_native|FullyQualifiedName~Correlated_Any_inside_a_projection_leaf_goes_native|FullyQualifiedName~Correlated_All_inside_a_scalar_aggregate_predicate_goes_native"`
Expected: all three PASS.

- [ ] **Step 9: Run the FULL FunctionalTests suite for one EF version**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests -c "Debug EF10" --no-build`
Expected: every test passes except the ONE known, expected exception carried since Task 5 (`NativeOwnedCollectionAllTests.All_with_a_correlated_element_predicate_declines_and_falls_back_to_correct_rows`) — resolved next in Task 8. If anything ELSE newly fails, stop and investigate before proceeding — `SelfParam` is now set on every root translator in the codebase, so a latent bug here would show up broadly.

- [ ] **Step 10: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeSlotPopulator.cs \
        src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeProjectionBinder.cs \
        src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeCardinalityBinder.cs \
        src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeVectorSearchBinder.cs \
        tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeOwnedCollectionCorrelatedTests.cs
git commit -m "EF-421: capture SelfParam at every root translator construction site (sort key, projection, aggregate, vector search prefilter)"
```

---

### Task 8: Retire the superseded decline test; regression pass; multi-EF-version check

**Files:**
- Modify: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeOwnedCollectionAllTests.cs:518-543`
- Modify: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeOwnedCollectionFilteredCountTests.cs` (check for an analogous correlated-decline test; read the file first)

**Interfaces:** None new — this task only updates test expectations to match the now-correct (native) behavior, and runs the full multi-version regression suite.

- [ ] **Step 1: Read the current state of the superseded test**

Read `NativeOwnedCollectionAllTests.cs`'s `All_with_a_correlated_element_predicate_declines_and_falls_back_to_correct_rows` (around line 518-543, shown in full in this plan's research — reproduced here for reference):

```csharp
    [Fact]
    public void All_with_a_correlated_element_predicate_declines_and_falls_back_to_correct_rows()
    {
        // Post.Title collides with Blog.Title, so a mis-scoped owner-rooted condition would select DIFFERENT
        // rows — which is what makes this decline test discriminating rather than vacuous.
        // ...
        var collection = Seed(nameof(All_with_a_correlated_element_predicate_declines_and_falls_back_to_correct_rows),
            Row("match", new BsonArray { PostDoc(rank: 9, heading: "a", title: "other") }),
            Row("other", new BsonArray { PostDoc(rank: 9, heading: "a", title: "match") }));

        var titles = AssertDeclinesCleanly(collection, q => q.Where(b => b.Posts.All(p => b.Title == "match")));
        Assert.Equal(new[] { "match" }, titles);
    }
```

- [ ] **Step 2: Rewrite it to assert the new (native) behavior**

Replace the method with:

```csharp
    [Fact]
    public void All_with_a_correlated_element_predicate_now_goes_native_since_EF421()
    {
        // SUPERSEDED by EF-421: this shape used to decline outright (see git history for the pre-EF-421
        // version of this test, which asserted AssertDeclinesCleanly). It is now natively representable via
        // a two-scope translator + $allElementsTrue — same seed, same expected rows (the correlated
        // predicate `b.Title == "match"` does not depend on p at all, so All reduces to whether the OWNER's
        // Title is "match" AND the blog has at least one post — "match" qualifies on both counts; "other"
        // fails the Title check). Post.Title collides with Blog.Title, so a mis-scoped (element-rooted)
        // resolution would select DIFFERENT rows, keeping this test discriminating rather than vacuous.
        var collection = Seed(nameof(All_with_a_correlated_element_predicate_now_goes_native_since_EF421),
            Row("match", new BsonArray { PostDoc(rank: 9, heading: "a", title: "other") }),
            Row("other", new BsonArray { PostDoc(rank: 9, heading: "a", title: "match") }));

        var titles = AssertNativeOnlyMatches(collection, q => q.Where(b => b.Posts.All(p => b.Title == "match")));
        Assert.Equal(new[] { "match" }, titles);
    }
```

- [ ] **Step 3: Check `NativeOwnedCollectionFilteredCountTests.cs` for an analogous case**

Run: `grep -n "correlat" tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeOwnedCollectionFilteredCountTests.cs`

If a test asserting a decline for a correlated `Count(pred)` exists, apply the same transformation as Step 2 (swap `AssertDeclinesCleanly` for `AssertNativeOnlyMatches`/`AssertNativeAndParity`, whichever helper that file defines, and update the doc comment). If none exists, note in the commit message that no analogous test was found there (the correlated-Count coverage lives entirely in the new `NativeOwnedCollectionCorrelatedTests.cs` file from Task 3).

- [ ] **Step 4: Also check `NativeOwnedCollectionPredicateTests.cs` (the `Any` sibling) for the same pattern**

Run: `grep -n "correlat" tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeOwnedCollectionPredicateTests.cs`

Apply the same transformation if found.

- [ ] **Step 5: Run the full FunctionalTests suite for EF10**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests -c "Debug EF10" --no-build`
Expected: 100% pass, zero failures, zero skips beyond pre-existing documented skips (e.g. encryption tests without `CRYPT_SHARED_LIB_PATH`).

- [ ] **Step 6: Run the full UnitTests suite for EF10**

Run: `dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests -c "Debug EF10" --no-build`
Expected: 100% pass.

- [ ] **Step 7: Run the `/test-all` skill for full multi-EF-version confirmation**

Invoke the `/test-all` skill (per this repo's `AGENTS.md`: "Build + test all three EF versions in parallel"). Expected: EF8, EF9, and EF10 all pass with an IDENTICAL pass/fail set (no `#if` was added anywhere in this change, so no version-specific divergence is expected — if one appears, investigate before proceeding rather than assuming it's unrelated).

- [ ] **Step 8: Update `Query/AGENTS.md`'s capability summary**

Add a short paragraph to the "Owned navigations" bullet (or a new bullet, matching this file's existing terse-but-precise style) documenting: correlated `Count(pred)`/`Any`/`All` now go native via a two-scope translator (`MongoOuterFieldExpression`) for `Count(pred)` (rendered inside the existing `$filter`) and a new `$anyElementTrue`/`$allElementsTrue`-over-`$map` dialect (`MongoQuantifierExpression`) for quantifiers, since `$elemMatch` cannot reference the enclosing document at all. Reference EF-421. Follow the file's established convention of noting what's now native, what still isn't (correlation nested two-or-more scopes deep), and the one durable invariant worth keeping (`ReferenceEquals` against `SelfParam`, never by name).

- [ ] **Step 9: Final commit**

```bash
git add tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeOwnedCollectionAllTests.cs \
        tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeOwnedCollectionFilteredCountTests.cs \
        tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeOwnedCollectionPredicateTests.cs \
        src/MongoDB.EntityFrameworkCore/Query/AGENTS.md
git commit -m "EF-421: update superseded decline tests and capability docs for correlated element predicates"
```

---

## Self-Review

**Spec coverage:**
- §1 (`_selfParam` capture) → Task 1, Task 7.
- §2 (identifying the correlation target) → Task 1 (`FreeParameterVisitor`), Task 3/5 (the `ReferenceEquals` branch at both call sites).
- §3 (`MongoOuterFieldExpression`) → Task 2, Task 3.
- §4 (correlated `Count(pred)`) → Task 3.
- §5 (`MongoQuantifierExpression`, correlated quantifiers) → Task 4, Task 5.
- §6 (relativizing `TryResolveOwnedFieldPath`/`TryResolveOwnedCollectionPath`) → Task 6.
- §7 (`MongoFieldPrefixRewriter` pass-through) — **investigated during plan-writing, not a separate task**: `MongoFieldPrefixRewriter` (referenced in `MongoElemMatchExpression`'s/`MongoFilteredSizeExpression`'s own doc comments) is only ever invoked by `NativeSelectManyBinder` to prefix an INNER-scope tree with an unwind path; the new correlated Count/quantifier paths never call it (their two-scope translator resolves prefixes internally, and `innerPrefix: ""` for `Count(pred)` means there is nothing to rewrite). Confirmed via `grep -rn "MongoFieldPrefixRewriter" src/` — only `NativeSelectManyBinder.cs` calls it. No plan task touches this file; §7 is a non-issue as the spec itself anticipated.
- Testing section → Tasks 3, 5, 6, 7 (differential/NativeOnly tests), Task 8 (regression + multi-version).
- Risks/invariants → called out inline in Tasks 3, 5 (decline-else-build-two-scope discipline) and enforced by Global Constraints.

**Placeholder scan:** no TBD/TODO; the one open judgment call (Task 7's aggregate-test expected value) is resolved with an explicit worked-out instruction, not left vague — flagged with "Author note" precisely because it's arithmetic that must be hand-verified against the literal seed, not because the design is undecided.

**Type consistency:** `MongoOuterFieldExpression(IProperty Property, string ElementName)` — used consistently across Tasks 2, 3, 6. `MongoQuantifierExpression(MongoElementRefExpression ArrayPath, MongoExpression ElementPredicate, MongoQuantifierKind Kind)` — used consistently across Tasks 4, 5. `TryResolveMember(..., out bool isOuter)` and `TryResolveOwnedCollectionPath(..., out bool isOuter)` / `TryResolveOwnedFieldPath(..., out bool isOuter)` signatures introduced in Tasks 3/6 are used identically at every call site listed in those same tasks. `SelfParam` (not `_selfParam` publicly — the backing storage is a plain auto-property) is named consistently in Tasks 1, 3, 5, 7.
