# Filtered `Count(pred)` over an owned collection — Implementation Plan (EF-359)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make a filtered count over an owned (embedded) collection work — natively as a `$project` leaf and in a predicate, and at least correctly on the fallback path in its bare spelling — closing EF-359, a hard failure in all three query modes today.

**Architecture:** A new `MongoFilteredSizeExpression` IR node (a *sibling* of `MongoSizeExpression`, never a flag on it) renders as `{$size: {$filter: {input: {$ifNull: […]}, as: …, cond: …}}}` in the aggregation dialect. Recognition widens `MongoExpressionTranslator.TryMatchCountExpression` to the predicated `Count`/`LongCount` overloads; the element predicate is translated by a fresh element-scoped translator behind the existing `ReferencesEnclosingScope` guard and admitted only if a new `MongoAggregationExpressionRenderer.CanRender` classifier accepts it. Two sites in `MongoProjectionBindingExpressionVisitor` widen in lockstep with the translator.

**Tech Stack:** C#, EF Core 8/9/10 (`Debug EF8|EF9|EF10` build configurations), MongoDB C# driver, xUnit (plain `Assert.*` — FluentAssertions is not referenced by the test projects).

## Global Constraints

- **Design spec:** `docs/superpowers/specs/2026-07-30-native-filtered-count-design.md`. Read it before Task 1. Where this plan and the spec disagree, the spec's §3 (sibling-node decision) and §6 (declines) win; report the discrepancy.
- **Branch:** `EF-359`, stacked on `NativeQueryOngoing` tip `33fdc58`. Commit per task. Do **not** squash, rebase or push — the human owner does that at the end.
- **Nullable:** `src/` is `<Nullable>enable</Nullable>`. Annotate new types accordingly.
- **No `#if`:** no EF-version-conditional code is expected anywhere in this slice. If you think you need one, stop and report.
- **Everything new is `internal`.** No public API surface changes, so no `BREAKING-CHANGES.md` entry (see spec §9 — do **not** add one by analogy with EF-358).
- **Preserve file BOMs.**
- **Build one version:** `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"`. Tests: `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build --filter "FullyQualifiedName~<ClassName>"`.
- **Run tests with `MONGODB_URI` and `ATLAS_URI` unset** so TestContainers boots an isolated `mongodb/mongodb-atlas-local` per test process. Docker required.
- **"Goes native" can only be proven by `MongoQueryMode.NativeOnly` succeeding.** MQL shape does not prove it — a fallback often emits a structurally identical pipeline. Asserting a shape *declines* means asserting it throws `NativeTranslationNotSupportedException` under `NativeOnly`.
- **Never record bare pass counts in comments or docs.** Record what was run and the outcome. (Three irreconcilable counts from one earlier branch is why.)
- **Prove a guard has teeth by deleting it and watching a test go red.** A test that stays green with its guard removed is vacuous and must be strengthened, not accepted.

---

## File Structure

**Create:**

| File | Responsibility |
|---|---|
| `src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoFilteredSizeExpression.cs` | The IR node: array path + element predicate. Deliberately a sibling of `MongoSizeExpression`, so every `is MongoSizeExpression` site fails closed. |
| `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeOwnedCollectionFilteredCountTests.cs` | All functional coverage for the three shapes, their declines, and the differential oracle. |

**Modify:**

| File | Change |
|---|---|
| `src/.../Query/NativeTranslation/MongoAggregationExpressionRenderer.cs` | New `$filter` arm; optional `elementVariable` threaded through `Render`; new `CanRender` classifier. |
| `src/.../Query/NativeTranslation/MongoFieldPrefixRewriter.cs` | New case: prefix `ArrayPath` only. |
| `src/.../Query/NativeTranslation/MongoExpressionTranslator.cs` | `TryMatchCountExpression` widening; the filtered branch in `TranslateOperand`. |
| `src/.../Query/NativeTranslation/NativeProjectionBinder.cs` | Node-kind gate widens to admit the new node. |
| `src/.../Query/Visitors/MongoProjectionBindingExpressionVisitor.cs` | `IsCanonicalCountWithoutPredicate` → `IsCanonicalCount` (both arities); EF-357 arm widened to the predicated overloads. |
| `src/MongoDB.EntityFrameworkCore/EnumerableMethods.cs` | Add `CountWithPredicate` / `LongCountWithPredicate` to the provider's port. |
| `tests/.../UnitTests/Query/NativeTranslation/MongoAggregationExpressionRendererTests.cs` | Rendering + `CanRender` unit coverage. |
| `tests/.../UnitTests/Query/NativeTranslation/MongoFieldPrefixRewriterTests.cs` | Prefix-rewriter case. |
| `tests/.../UnitTests/Query/NativeTranslation/MongoExpressionTranslatorTests.cs` | Recognition + decline unit coverage. |
| `tests/.../FunctionalTests/Query/NativeOwnedCollectionCountTests.cs` | Flip the pinning test in Task 3. |
| `src/.../Query/AGENTS.md`, `docs/native-query-status-EF-322.md`, the design doc's §8 | Documentation, Task 6. |

---

## Task 0: Spike — measure the current behaviour and the server's rules (GO/NO-GO)

**No production code.** Findings gate §4 and §6 of the design.

**Files:**
- Create: `docs/superpowers/plans/2026-07-30-native-filtered-count-spike-findings.md`

**Interfaces:**
- Consumes: nothing.
- Produces: a findings document Tasks 1–4 read. Specifically: the accepted `$filter` `as` variable-name rule, the nested-variable scoping form, the current failure mode of each of shapes A/B/C, and whether `RenderNode` throws or declines for an unmatched node in the `$elemMatch` element position.

- [ ] **Step 1: Measure the current failure for all three shapes, in all three modes**

Write a throwaway functional test class (delete it before committing — its *findings* are the deliverable, not the file). Model it on `NativeOwnedCollectionCountTests`: reuse that file's `Blog`/`Post`/`Comment`/`Home`/`Note` model, its `BlogModel` model-builder action, its `CreateContext` helper and its `Seed`/`LenRow` row builders by copying them into the throwaway file.

For each of:
- **A** `db.Entities.AsNoTracking().Select(b => new { b.Title, N = b.Posts.Count(p => p.Rank > 0) }).ToList()`
- **B** `db.Entities.AsNoTracking().Where(b => b.Posts.Count(p => p.Rank > 0) > 2).ToList()`
- **C** `db.Entities.AsNoTracking().Select(b => b.Posts.Count(p => p.Rank > 0)).ToList()`

and each of `Native`, `DriverLinq`, `NativeOnly`, record: does it throw? Exception **type**, message, and the top provider frame in the stack trace. If it succeeds, record the values returned and the captured MQL (use `CreateContextWithLogging`/`AssertMql` from `NativeOwnedCollectionCountTests` as the idiom).

**B's disposition is the important unknown** — the status doc records it as never re-measured. If B already returns correct results via fallback, that makes B a fallback→native flip and its differential-oracle rows (Task 5) mandatory rather than merely valuable.

- [ ] **Step 2: Verify the server-side rendering, directly against MongoDB**

Not through EF — use the driver's `IMongoCollection<BsonDocument>.Aggregate` with a hand-built pipeline, over documents covering: `Posts` with 3 matching / 1 matching / 0 matching elements, `Posts: []`, `Posts` **absent**, and `Posts: null`.

```csharp
var pipeline = new[]
{
    new BsonDocument("$project", new BsonDocument
    {
        { "Title", "$Title" },
        { "N", new BsonDocument("$size", new BsonDocument("$filter", new BsonDocument
            {
                { "input", new BsonDocument("$ifNull", new BsonArray { "$Posts", new BsonArray() }) },
                { "as", "e" },
                { "cond", new BsonDocument("$gt", new BsonArray { "$$e.Rank", 0 }) }
            })) }
    })
};
```

Record: does every row return a number (no aborted command)? Is the absent/null row `0`? **Then re-run with the `$ifNull` removed** and record what happens — this is the measurement that makes "`$ifNull` is mandatory, not defensive" a fact rather than an inherited claim.

- [ ] **Step 3: Establish the `as` variable-name rules and nested scoping**

Still hand-built pipelines. Record which of `"e"`, `"ee"`, `"_e"`, `"E"` the server accepts as an `as` name. Then verify a **nested** filtered count — an outer `$filter` over `Posts` whose `cond` contains an inner `{$size: {$filter: {input: {$ifNull: ["$$e.Comments", []]}, as: "ee", cond: {$gt: ["$$ee.Age", 0]}}}}` — returns the expected counts. The naming scheme Task 1 implements (`"e"`, then `outer + "e"` per nesting level) depends on this.

- [ ] **Step 4: Establish the `$elemMatch` decline path**

Read `MongoQueryLanguageRenderer.RenderNode` and `IsQueryDialectRenderable` (around `MongoQueryLanguageRenderer.cs:433`) and determine, **by reading then confirming with a unit test against a hand-built tree**, what happens to a `MongoBinaryExpression` whose `Left` is a node neither arm matches — specifically whether `IsQueryDialectRenderable` returns `false` (clean decline) or `RenderNode` throws. Design §6's nested-in-quantifier row (`b.Posts.Any(p => p.Comments.Count(c => …) > 1)` must decline, because `$expr` inside `$elemMatch` is a hard server error) depends on the answer. A hand-built tree is fine here; you can use `MongoSizeExpression` wrapped in something unmatched, or wait until Task 1 exists and re-check — say which you did.

- [ ] **Step 5: Record an explain plan for shape B's rendering**

Seed ~200 documents with an index on `Posts.Rank`, run the hand-built `$expr`-over-`$size`-over-`$filter` `$match` with `explain("queryPlanner")`, and record the winning plan stage (COLLSCAN expected — record whatever it actually is, and whether any IXSCAN candidate was even generated).

- [ ] **Step 6: Write the findings document and give a GO/NO-GO**

Structure it as: one section per step, each stating what was **run** and what was **observed**, then a final "Design deltas" section naming anything in the spec that the measurements contradict. State GO or NO-GO explicitly. If any measurement contradicts spec §3 or §6, that is a NO-GO pending the human owner's decision — say so rather than adapting the design yourself.

- [ ] **Step 7: Delete the throwaway test file and commit the findings**

```bash
git add docs/superpowers/plans/2026-07-30-native-filtered-count-spike-findings.md
git commit -m "EF-359: spike findings — current failure modes, \$filter rendering, variable scoping"
git status --short   # must show no leftover throwaway test file
```

---

## Task 1: The IR node, its rendering, and the `CanRender` classifier

Rendering layer only — nothing recognizes a filtered count yet, so no functional behaviour changes.

**Files:**
- Create: `src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoFilteredSizeExpression.cs`
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoAggregationExpressionRenderer.cs`
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoFieldPrefixRewriter.cs`
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoAggregationExpressionRendererTests.cs`
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoFieldPrefixRewriterTests.cs`

**Interfaces:**
- Consumes: Task 0's findings (the `as` naming rule and the nested-variable form).
- Produces, and later tasks depend on these exact signatures:
  - `internal sealed class MongoFilteredSizeExpression : MongoExpression` with constructor `(string arrayPath, MongoExpression elementPredicate, Type type)` and properties `string ArrayPath`, `MongoExpression ElementPredicate`, `override Type Type`.
  - `MongoAggregationExpressionRenderer.Render(MongoExpression node, PlaceholderTable placeholders, string? elementVariable = null)` — the third parameter is **new and optional**; every existing call site keeps its current behaviour.
  - `static bool MongoAggregationExpressionRenderer.CanRender(MongoExpression node)` — `public` (the class is `internal static`), called from `MongoExpressionTranslator` in Task 2.

- [ ] **Step 1: Write the failing rendering tests**

In `MongoAggregationExpressionRendererTests.cs`. Match the file's existing idiom for constructing a `PlaceholderTable` and asserting on rendered BSON.

```csharp
[Fact]
public void Filtered_size_renders_as_size_over_filter_with_ifNull()
{
    var placeholders = new PlaceholderTable();
    var node = new MongoFilteredSizeExpression(
        "Posts",
        new MongoBinaryExpression(
            MongoBinaryOperator.GreaterThan,
            new MongoElementRefExpression("Rank", typeof(int)),
            new MongoConstantExpression(0, forSerialization: null)),
        typeof(int));

    var rendered = MongoAggregationExpressionRenderer.Render(node, placeholders);

    Assert.Equal(
        BsonDocument.Parse(
            """
            { "$size": { "$filter": {
                "input": { "$ifNull": ["$Posts", []] },
                "as": "e",
                "cond": { "$gt": ["$$e.Rank", 0] } } } }
            """),
        rendered);
}

[Fact]
public void Nested_filtered_size_gives_each_level_its_own_variable()
{
    var placeholders = new PlaceholderTable();
    var inner = new MongoBinaryExpression(
        MongoBinaryOperator.GreaterThan,
        new MongoFilteredSizeExpression(
            "Comments",
            new MongoBinaryExpression(
                MongoBinaryOperator.GreaterThan,
                new MongoElementRefExpression("Age", typeof(int)),
                new MongoConstantExpression(0, forSerialization: null)),
            typeof(int)),
        new MongoConstantExpression(1, forSerialization: null));

    var rendered = MongoAggregationExpressionRenderer.Render(
        new MongoFilteredSizeExpression("Posts", inner, typeof(int)), placeholders);

    // The INNER array path is element-relative to the OUTER variable, and the inner element
    // predicate is relative to the inner variable. Getting either wrong reads the wrong array.
    var json = rendered.ToJson();
    Assert.Contains("\"$$e.Comments\"", json);
    Assert.Contains("\"$$ee.Age\"", json);
}

[Fact]
public void Existing_nodes_render_unchanged_when_no_element_variable_is_in_scope()
{
    var placeholders = new PlaceholderTable();
    var rendered = MongoAggregationExpressionRenderer.Render(
        new MongoSizeExpression("Posts", typeof(int), nullSafe: true), placeholders);

    Assert.Equal(
        BsonDocument.Parse("""{ "$size": { "$ifNull": ["$Posts", []] } }"""),
        rendered);
}
```

The third test is a **regression tripwire**: it pins that adding the optional parameter moves no existing MQL. Also add `CanRender` tests:

```csharp
[Theory]
[MemberData(nameof(RenderableNodes))]
public void CanRender_admits_exactly_what_Render_renders(MongoExpression node)
{
    Assert.True(MongoAggregationExpressionRenderer.CanRender(node));
    // Non-vacuous: prove Render really does handle it, so the two cannot drift silently.
    _ = MongoAggregationExpressionRenderer.Render(node, new PlaceholderTable());
}

[Theory]
[MemberData(nameof(UnrenderableNodes))]
public void CanRender_declines_what_Render_would_throw_on(MongoExpression node)
{
    Assert.False(MongoAggregationExpressionRenderer.CanRender(node));
    Assert.Throws<NativeTranslationNotSupportedException>(
        () => MongoAggregationExpressionRenderer.Render(node, new PlaceholderTable()));
}
```

`RenderableNodes` must cover: a field ref, an element ref, a constant, a parameter, each comparison operator, `AndAlso`, `OrElse`, each arithmetic operator, a `MongoSizeExpression`, and a `MongoFilteredSizeExpression`. `UnrenderableNodes` must cover the node kinds the renderer has **no** arm for — a `MongoRegexExpression`, a `MongoInExpression`, a `MongoUnaryExpression{Not}`, a `MongoElemMatchExpression`, and a `MongoFilteredSizeExpression` whose *element predicate* is one of those (which proves `CanRender` recurses).

- [ ] **Step 2: Run the tests and confirm they fail**

```bash
dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" \
  --filter "FullyQualifiedName~MongoAggregationExpressionRendererTests"
```
Expected: compile error — `MongoFilteredSizeExpression` and `CanRender` do not exist.

- [ ] **Step 3: Create the node**

```csharp
/// <summary>
/// Represents the element count of an owned (embedded) array field FILTERED by a per-element predicate —
/// <c>b.Posts.Count(p =&gt; p.Rank &gt; 0)</c> — rendering as <c>{ $size: { $filter: … } }</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is deliberately a SIBLING of <see cref="MongoSizeExpression"/> rather than a flag on it, and the
/// reason is silent wrong data.</b> Four sites match on <c>is MongoSizeExpression</c>, and three of them must
/// NOT fire for a filtered count:
/// <c>MongoQueryLanguageRenderer.TryRenderSizeComparison</c> would render an integer-constant comparison as an
/// array-index existence test (<c>{"Posts.2": {$exists: true}}</c>) — which answers the UNFILTERED count's
/// question, i.e. the wrong rows, with no error;
/// <c>MongoQueryLanguageRenderer.IsQueryDialectRenderable</c> would admit it inside <c>$elemMatch</c>, where
/// <c>$expr</c> is a hard server error;
/// and <c>MongoExpressionNegator</c> would INVERT the operator, which is the exact complement only because the
/// rendered <c>$exists</c> form partitions the value space — the <c>$expr</c> form's operators do not.
/// As a distinct type all three fail CLOSED by construction: a pattern naming
/// <see cref="MongoSizeExpression"/> simply does not match. With a flag, each would be wrong by default and
/// right only if a future editor remembered a guard.
/// </para>
/// <para>
/// There is no <c>NullSafe</c> flag. <see cref="MongoSizeExpression"/> carries one because its unfiltered form
/// is shared with the projected reference-collection count, whose array is a <c>$lookup</c> output and therefore
/// always present. A filtered count has no such analogue, so the <c>$ifNull</c> wrap is unconditional —
/// <c>$size</c>/<c>$filter</c> over a MISSING or explicitly-null array is a hard server error that aborts the
/// whole aggregate, not a wrong answer.
/// </para>
/// <para>
/// <see cref="ElementPredicate"/>'s field paths are ELEMENT-relative by construction (it is translated by a
/// fresh element-scoped <c>MongoExpressionTranslator</c>), which is what lets the renderer address them through
/// the <c>$filter</c> variable — and why <c>MongoFieldPrefixRewriter</c> must prefix
/// <see cref="ArrayPath"/> only, exactly as it does for <see cref="MongoElemMatchExpression"/>.
/// </para>
/// </remarks>
internal sealed class MongoFilteredSizeExpression : MongoExpression
{
    public MongoFilteredSizeExpression(string arrayPath, MongoExpression elementPredicate, Type type)
    {
        ArrayPath = arrayPath;
        ElementPredicate = elementPredicate;
        Type = type;
    }

    /// <summary>The array's dotted document path, relative to the enclosing scope.</summary>
    public string ArrayPath { get; }

    /// <summary>The per-element predicate, with ELEMENT-relative field paths.</summary>
    public MongoExpression ElementPredicate { get; }

    /// <inheritdoc />
    public override Type Type { get; }
}
```

Copy the licence header and BOM convention from `MongoSizeExpression.cs`.

- [ ] **Step 4: Thread `elementVariable` through the renderer and add the two new members**

In `MongoAggregationExpressionRenderer.cs`:

```csharp
public static BsonValue Render(MongoExpression node, PlaceholderTable placeholders, string? elementVariable = null)
    => node switch
    {
        MongoFieldExpression field => FieldRef(field.ElementName, elementVariable),
        MongoElementRefExpression elementRef => FieldRef(elementRef.Path, elementVariable),
        MongoConstantExpression or MongoParameterExpression => MongoValueRenderer.RenderValue(node, placeholders),
        MongoBinaryExpression binary => RenderBinary(binary, placeholders, elementVariable),
        MongoSizeExpression size => RenderSize(size, elementVariable),
        MongoFilteredSizeExpression filtered => RenderFilteredSize(filtered, placeholders, elementVariable),
        _ => throw new NativeTranslationNotSupportedException(
            $"MongoAggregationExpressionRenderer does not support node type '{node.GetType().Name}'.")
    };

// Inside a $filter's cond the enclosing document is no longer addressable as "$path" — the element is bound to
// a variable, so a field of it is "$$<var>.<path>". elementVariable is null everywhere else, which is what
// keeps every pre-existing call site's emitted MQL byte-identical.
private static BsonValue FieldRef(string path, string? elementVariable)
    => elementVariable is null ? "$" + path : "$$" + elementVariable + "." + path;

private static BsonValue RenderFilteredSize(
    MongoFilteredSizeExpression node, PlaceholderTable placeholders, string? elementVariable)
{
    // Each nesting level needs its own variable name. Deriving it from the enclosing one ("e", "ee", "eee")
    // keeps them distinct without threading a counter, and keeps every name lowercase-initial, which is what
    // the server requires of an $filter `as` name (Task 0 step 3).
    var variable = elementVariable is null ? "e" : elementVariable + "e";

    return new BsonDocument("$size",
        new BsonDocument("$filter", new BsonDocument
        {
            // $ifNull is MANDATORY: $filter over a missing or explicitly-null array is a hard server error that
            // aborts the whole aggregate command. [] yields 0, which is what LINQ answers for a missing array.
            { "input", new BsonDocument("$ifNull", new BsonArray { FieldRef(node.ArrayPath, elementVariable), new BsonArray() }) },
            { "as", variable },
            { "cond", Render(node.ElementPredicate, placeholders, variable) }
        }));
}
```

`RenderSize` and `RenderBinary` both take the new parameter and pass it down; `RenderSize` uses `FieldRef` for its path so a plain `$size` nested inside a `cond` (from `Count(p => p.Comments.Count > 1)`) addresses `$$e.Comments`.

Then the classifier, placed directly below `Render` with a comment tying it to the existing contract:

```csharp
/// <summary>
/// Returns whether <see cref="Render"/> would render <paramref name="node"/> without throwing.
/// </summary>
/// <remarks>
/// <b>This method and <see cref="Render"/> must be changed together.</b> It is the aggregation-dialect
/// counterpart of <c>MongoQueryLanguageRenderer.IsQueryDialectRenderable</c>, and it exists for the same
/// reason: a caller that builds a node the renderer cannot express turns a clean translate-time DECLINE into a
/// render-time throw. For a filtered count that matters specifically — the shapes this gates
/// (<c>Select(b =&gt; new { N = b.Posts.Count(pred) })</c> and its bare spelling) have NO working fallback to
/// land on, so a render-time throw makes the query fail DIFFERENTLY from how it fails today rather than
/// identically, which is the disposition this slice is obliged to preserve for anything it does not fix.
/// </remarks>
public static bool CanRender(MongoExpression node)
    => node switch
    {
        MongoFieldExpression or MongoElementRefExpression => true,
        MongoConstantExpression or MongoParameterExpression => true,
        MongoBinaryExpression binary
            => IsRenderableOperator(binary.Operator) && CanRender(binary.Left) && CanRender(binary.Right),
        MongoSizeExpression => true,
        MongoFilteredSizeExpression filtered => CanRender(filtered.ElementPredicate),
        _ => false
    };
```

`IsRenderableOperator` must list exactly the operators `RenderBinary`'s own switch maps — read that switch and mirror it; do not assume every `MongoBinaryOperator` member is covered.

- [ ] **Step 5: Add the prefix-rewriter case**

In `MongoFieldPrefixRewriter.Rewrite`'s switch, directly after the `MongoSizeExpression` case:

```csharp
// Prefix the ARRAY path only, for the same reason as MongoElemMatchExpression above: the element predicate's
// field paths are ELEMENT-relative (that is what the $filter variable addresses), so rewriting them would
// mis-address every field inside the $filter.
MongoFilteredSizeExpression f => new MongoFilteredSizeExpression(prefix + "." + f.ArrayPath, f.ElementPredicate, f.Type),
```

Add the matching unit test to `MongoFieldPrefixRewriterTests.cs`, asserting both halves — the array path **is** prefixed and the element predicate is **not**:

```csharp
[Fact]
public void Filtered_size_prefixes_the_array_path_only()
{
    var node = new MongoFilteredSizeExpression(
        "Comments",
        new MongoBinaryExpression(
            MongoBinaryOperator.GreaterThan,
            new MongoElementRefExpression("Age", typeof(int)),
            new MongoConstantExpression(0, forSerialization: null)),
        typeof(int));

    var rewritten = Assert.IsType<MongoFilteredSizeExpression>(MongoFieldPrefixRewriter.Rewrite(node, "Posts"));

    Assert.Equal("Posts.Comments", rewritten.ArrayPath);
    Assert.Same(node.ElementPredicate, rewritten.ElementPredicate);
}
```

- [ ] **Step 6: Run the unit tests and confirm they pass**

```bash
dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" \
  --filter "FullyQualifiedName~MongoAggregationExpressionRendererTests|FullyQualifiedName~MongoFieldPrefixRewriterTests"
```
Expected: PASS.

- [ ] **Step 7: Confirm nothing else moved**

```bash
dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build \
  --filter "FullyQualifiedName~UnitTests.Query"
```
Expected: 0 failures. The optional parameter must not have changed any existing rendering — if any MQL-asserting test moved, stop and report, because that contradicts the additive-parameter claim.

- [ ] **Step 8: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoFilteredSizeExpression.cs \
        src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoAggregationExpressionRenderer.cs \
        src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoFieldPrefixRewriter.cs \
        tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/
git commit -m "EF-359: MongoFilteredSizeExpression, its \$filter rendering, and a CanRender classifier"
```

---

## Task 2: Recognize a predicated count — shape B goes native

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.cs` (`TryMatchCountExpression` ~line 950; the count branch in `TranslateOperand` ~line 627)
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoExpressionTranslatorTests.cs`
- Test: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeOwnedCollectionFilteredCountTests.cs` (create)

**Interfaces:**
- Consumes: `MongoFilteredSizeExpression(string, MongoExpression, Type)` and `MongoAggregationExpressionRenderer.CanRender(MongoExpression)` from Task 1.
- Produces: `TryMatchCountExpression(Expression node, out Expression? source, out LambdaExpression? predicate)` — the third parameter is new; `null` means the predicate-less form. Task 3 and Task 4 do not call it, but Task 3 depends on `TranslateOperand` returning a `MongoFilteredSizeExpression` for a predicated count leaf.

- [ ] **Step 1: Write the failing functional tests for shape B**

Create `NativeOwnedCollectionFilteredCountTests.cs`. **Copy** the model classes (`Blog`, `Post`, `Comment`, `Home`, `Note`, `TitleCount`), `BlogModel`, `CreateContext`, `CreateContextWithLogging`, `AssertMql`, `UniqueCollectionName`, `Seed`, `Row`, `PostDoc`, `LenRow` and `RowWithNotes` from `NativeOwnedCollectionCountTests.cs` — that file's `Post.Rank`/`Heading`/`Other` are deliberately nullable so the missing-field state is reachable, and its `Post.Title` deliberately collides with `Blog.Title` so the correlated-predicate guard is exercised on an input that would otherwise be accepted. Do not simplify either property away.

Add a seed whose rows differ in **how many elements match the predicate**, not just in array length:

```csharp
// Rows differ in the number of elements SATISFYING the predicate — the input space a filtered count is
// sensitive to, and the axis LenRow alone cannot control (its ranks are 0..n-1, so "Rank > 0" and length are
// correlated). Each row's Posts carry `matching` elements with Rank = 5 and `nonMatching` with Rank = -5.
private static BsonDocument MatchRow(string title, int matching, int nonMatching)
{
    var posts = new BsonArray();
    for (var i = 0; i < matching; i++) posts.Add(PostDoc(rank: 5, heading: "m" + i));
    for (var i = 0; i < nonMatching; i++) posts.Add(PostDoc(rank: -5, heading: "n" + i));
    return Row(title, posts);
}

private static BsonDocument[] MatchRows() =>
[
    MatchRow("none", 0, 3), MatchRow("one", 1, 2), MatchRow("three", 3, 0),
    Row("empty", new BsonArray()), Row("missing", null), Row("null", BsonNull.Value)
];
```

```csharp
[Fact]
public void Filtered_count_predicate_goes_native()
{
    var collection = Seed(nameof(Filtered_count_predicate_goes_native), MatchRows());
    using var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel);

    var titles = db.Entities.AsNoTracking()
        .Where(b => b.Posts.Count(p => p.Rank > 0) > 1)
        .Select(b => b.Title).OrderBy(t => t).ToList();

    Assert.Equal(["three"], titles);
}

[Theory]
[InlineData(0, new[] { "empty", "missing", "none", "null", "one", "three" })]
[InlineData(1, new[] { "one", "three" })]
[InlineData(3, new[] { "three" })]
public void Filtered_count_predicate_is_correct_for_every_threshold(int threshold, string[] expected)
{
    var collection = Seed($"thresh_{threshold}", MatchRows());
    using var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel);

    var titles = db.Entities.AsNoTracking()
        .Where(b => b.Posts.Count(p => p.Rank > 0) >= threshold)
        .Select(b => b.Title).OrderBy(t => t).ToList();

    Assert.Equal(expected, titles);
}

[Fact]
public void Filtered_count_predicate_emits_expr_over_size_over_filter_never_an_array_index_test()
{
    var collection = Seed(
        nameof(Filtered_count_predicate_emits_expr_over_size_over_filter_never_an_array_index_test), MatchRows());
    using var db = CreateContextWithLogging(collection, MongoQueryMode.NativeOnly, BlogModel, out var spy);

    _ = db.Entities.AsNoTracking().Where(b => b.Posts.Count(p => p.Rank > 0) > 2).ToList();

    var mql = spy.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery);
    Assert.Contains("$filter", mql);
    // THE TIER-1 FAIL-CLOSED TRIPWIRE. An array-index existence test answers the UNFILTERED count's question,
    // so if MongoFilteredSizeExpression is ever collapsed into MongoSizeExpression this returns the wrong rows
    // with no error. Goes red the moment that happens. See MongoFilteredSizeExpression's remarks.
    Assert.DoesNotContain("Posts.2", mql);
    Assert.DoesNotContain("$exists", mql);
}

[Fact]
public void Filtered_count_predicate_with_a_parameterized_threshold_goes_native()
{
    var collection = Seed(nameof(Filtered_count_predicate_with_a_parameterized_threshold_goes_native), MatchRows());
    using var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel);
    var threshold = 1;

    var titles = db.Entities.AsNoTracking()
        .Where(b => b.Posts.Count(p => p.Rank > 0) > threshold)
        .Select(b => b.Title).OrderBy(t => t).ToList();

    Assert.Equal(["three"], titles);
}

[Fact]
public void Filtered_count_predicate_through_an_owned_reference_hop_goes_native()
{
    var collection = Seed(
        nameof(Filtered_count_predicate_through_an_owned_reference_hop_goes_native),
        RowWithNotes("two", 2), RowWithNotes("none", 0));
    using var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel);

    var titles = db.Entities.AsNoTracking()
        .Where(b => b.Home.Notes.Count(n => n.Length > 0) > 0)
        .Select(b => b.Title).ToList();

    Assert.Equal(["two"], titles);
}
```

Also write the declines, each asserting the shape throws under `NativeOnly` **and** (where a fallback exists) returns correct rows under `Native`:

```csharp
[Fact]
public void Correlated_element_predicate_declines_and_falls_back_to_correct_rows()
{
    // Post.Title deliberately collides with Blog.Title: the element-scoped translator resolves members by NAME,
    // so without ReferencesEnclosingScope this would silently retarget b.Title at the ELEMENT and return the
    // wrong rows under the default Native mode. The guard is what makes this a decline instead.
    var collection = Seed(
        nameof(Correlated_element_predicate_declines_and_falls_back_to_correct_rows),
        Row("x", new BsonArray { PostDoc(rank: 1, heading: "h") }));

    using (var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel))
    {
        Assert.Throws<NativeTranslationNotSupportedException>(
            () => db.Entities.AsNoTracking().Where(b => b.Posts.Count(p => p.Title == b.Title) > 0).ToList());
    }

    using (var db = CreateContext(collection, MongoQueryMode.Native, BlogModel))
    {
        // "p" (PostDoc's Title) != "x" (the Blog's Title), so the correct answer is NO rows. An element-scoped
        // misresolution would compare the element's own Title to itself and return the row.
        Assert.Empty(db.Entities.AsNoTracking().Where(b => b.Posts.Count(p => p.Title == b.Title) > 0).ToList());
    }
}

[Fact]
public void Element_predicate_outside_the_renderable_set_declines()
{
    var collection = Seed(
        nameof(Element_predicate_outside_the_renderable_set_declines),
        Row("x", new BsonArray { PostDoc(rank: 1, heading: "hello") }));
    using var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel);

    // A regex predicate has no aggregation-dialect rendering (CanRender declines it), so the whole predicate
    // declines at TRANSLATE time rather than throwing at render time.
    Assert.Throws<NativeTranslationNotSupportedException>(
        () => db.Entities.AsNoTracking()
            .Where(b => b.Posts.Count(p => p.Heading!.StartsWith("h")) > 0).ToList());
}

[Fact]
public void Primitive_element_collection_filtered_count_declines()
{
    var collection = Seed(nameof(Primitive_element_collection_filtered_count_declines), RowWithTags("x", "a", "bb"));
    using var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel);

    // Tags is a mapped primitive-collection PROPERTY, not a navigation — TryResolveOwnedCollectionPath's
    // final-hop check declines it.
    Assert.Throws<NativeTranslationNotSupportedException>(
        () => db.Entities.AsNoTracking().Where(b => b.Tags.Count(t => t.Length > 1) > 0).ToList());
}

[Fact]
public void Filtered_count_nested_inside_a_quantifier_declines_and_the_unfiltered_form_still_goes_native()
{
    var collection = Seed(
        nameof(Filtered_count_nested_inside_a_quantifier_declines_and_the_unfiltered_form_still_goes_native),
        Row("x", new BsonArray { PostWithComments("h", 2) }));

    using var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel);

    // $expr is a HARD SERVER ERROR inside $elemMatch, so a filtered count there must decline at translate
    // time — if it were admitted the whole query would throw at EXECUTION time under the default Native mode.
    Assert.Throws<NativeTranslationNotSupportedException>(
        () => db.Entities.AsNoTracking()
            .Where(b => b.Posts.Any(p => p.Comments.Count(c => c.Age > 0) > 1)).ToList());

    // REGRESSION TRIPWIRE: the UNFILTERED count in the same position renders as an array-index test and must
    // stay native.
    Assert.Single(db.Entities.AsNoTracking().Where(b => b.Posts.Any(p => p.Comments.Count > 1)).ToList());
}
```

`PostWithComments` and `RowWithTags` are further helpers to copy from `NativeOwnedCollectionCountTests.cs`.

- [ ] **Step 2: Run them and confirm they fail**

```bash
dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" \
  --filter "FullyQualifiedName~NativeOwnedCollectionFilteredCountTests"
```
Expected: the native-routing tests fail (`NativeTranslationNotSupportedException` — the shape is not recognized yet). The **decline** tests may already pass; note which, because a test that passes before the feature exists cannot prove the guard it names, and Step 6 must prove those specifically.

- [ ] **Step 3: Widen `TryMatchCountExpression`**

```csharp
private static bool TryMatchCountExpression(
    Expression node,
    [NotNullWhen(true)] out Expression? source,
    out LambdaExpression? predicate)
{
    source = null;
    predicate = null;

    if (node.Type != typeof(int) && node.Type != typeof(long))
        return false;

    switch (node)
    {
        case MemberExpression { Member: PropertyInfo { Name: nameof(List<int>.Count) }, Expression: { } receiver }:
            source = UnwrapAsQueryable(receiver);
            return true;

        case MethodCallExpression { Arguments.Count: 1 } call
            when call.Method.Name is nameof(Enumerable.Count) or nameof(Enumerable.LongCount)
                 && (call.Method.DeclaringType == typeof(Enumerable)
                     || call.Method.DeclaringType == typeof(Queryable)):
            source = UnwrapAsQueryable(call.Arguments[0]);
            return true;

        // The PREDICATED overloads (EF-359). Matched by canonical MethodInfo rather than by name — unlike the
        // arm above, which is left exactly as it was so no shipped path's behaviour moves. Generic methods are
        // compared as DEFINITIONS: an open definition and a constructed instantiation are never reference-equal.
        case MethodCallExpression { Arguments.Count: 2 } call when IsCanonicalCountWithPredicate(call.Method):
            source = UnwrapAsQueryable(call.Arguments[0]);
            predicate = call.Arguments[1].UnwrapLambdaFromQuote();
            return true;

        default:
            return false;
    }
}

// The Queryable spelling quotes its lambda and the Enumerable spelling does not; UnwrapLambdaFromQuote above
// handles both, so both declaring types are admitted here.
private static bool IsCanonicalCountWithPredicate(MethodInfo method)
{
    if (!method.IsGenericMethod)
        return false;

    var definition = method.GetGenericMethodDefinition();
    return definition == QueryableMethods.CountWithPredicate
        || definition == QueryableMethods.LongCountWithPredicate
        || definition == EnumerableMethods.CountWithPredicate
        || definition == EnumerableMethods.LongCountWithPredicate;
}
```

`EnumerableMethods.CountWithPredicate`/`LongCountWithPredicate` do not exist yet — add them to the provider's port in `src/MongoDB.EntityFrameworkCore/EnumerableMethods.cs`, following the file's existing `GetMethod` idiom:

```csharp
CountWithPredicate = GetMethod(
    nameof(Enumerable.Count), 1,
    types => [typeof(IEnumerable<>).MakeGenericType(types[0]), typeof(Func<,>).MakeGenericType(types[0], typeof(bool))]);

LongCountWithPredicate = GetMethod(
    nameof(Enumerable.LongCount), 1,
    types => [typeof(IEnumerable<>).MakeGenericType(types[0]), typeof(Func<,>).MakeGenericType(types[0], typeof(bool))]);
```

plus the two `public static MethodInfo … { get; }` declarations beside `CountWithoutPredicate`.

Update the other `TryMatchCountExpression` call site(s) to pass the new out parameter — `grep -n "TryMatchCountExpression" src/` and fix each. A site that does not care about the predicate must pass `out var pred` and **decline when `pred is not null`**, not discard it: silently ignoring a predicate would count the whole array.

- [ ] **Step 4: Add the filtered branch to `TranslateOperand`**

Replace the existing count branch (currently `if (TryMatchCountExpression(node, out var countSource) && TryResolveOwnedCollectionPath(countSource, out var arrayPath, out _))`), keeping every word of the surrounding comment block — it documents the by-name-collision and by-name-retarget invariants and is still accurate:

```csharp
if (TryMatchCountExpression(node, out var countSource, out var countPredicate)
    && TryResolveOwnedCollectionPath(countSource, out var arrayPath, out var countElementType))
{
    if (countPredicate is null)
        return new MongoSizeExpression(arrayPath, node.Type, nullSafe: true);

    // A FILTERED count (EF-359). The element predicate is translated exactly as a quantifier's is — same
    // correlated-scope guard, same element-scoped child translator — so it inherits both invariants rather
    // than re-deriving them.
    //
    // The correlated guard is LOAD-BEARING, not defensive: single-scope TryResolveMember resolves a member by
    // NAME with no parameter-identity check, so an enclosing-scoped access whose name also exists on the
    // element would be silently retargeted AT THE ELEMENT — wrong rows under the default Native mode, where
    // the pre-slice fallback was correct. Note a $filter cond CAN legally reference the enclosing document
    // (unlike $elemMatch, which cannot at all), so correlated support is a deferrable capability here rather
    // than an impossibility — it needs a two-scope element translator.
    if (ReferencesEnclosingScope(countPredicate.Body, countPredicate.Parameters[0]))
        return null;

    var countElementTranslator = new MongoExpressionTranslator(countElementType);
    if (!countElementTranslator.TryTranslate(countPredicate.Body, out var elementPredicate))
        return null;

    // Decline at TRANSLATE time for anything the aggregation renderer cannot express. Letting Render throw
    // instead would make the projection spellings of this shape fail differently from how they fail today
    // (they have no working fallback to land on), and would surface a render-time throw under NativeOnly
    // instead of a clean decline.
    if (!MongoAggregationExpressionRenderer.CanRender(elementPredicate))
        return null;

    return new MongoFilteredSizeExpression(arrayPath, elementPredicate, node.Type);
}
```

- [ ] **Step 5: Run the tests**

```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10" && \
dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build \
  --filter "FullyQualifiedName~NativeOwnedCollectionFilteredCountTests"
```
Expected: PASS.

- [ ] **Step 6: Prove the two new guards have teeth**

For each, make the change, rebuild, run the class, record which tests go red, then **revert**:

1. Delete the `ReferencesEnclosingScope` check → `Correlated_element_predicate_declines_and_falls_back_to_correct_rows` must go red, and specifically its `Native`-mode assertion (the wrong-rows half), not only the `NativeOnly` half. If only the `NativeOnly` half moves, the test is not proving the wrong-data hazard and must be strengthened.
2. Delete the `CanRender` check → `Element_predicate_outside_the_renderable_set_declines` must go red.

Record what you ran and what happened in the commit message. Do **not** leave either mutation in the tree.

- [ ] **Step 7: Confirm shape A's failure is still byte-identical**

```bash
dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build \
  --filter "FullyQualifiedName~NativeOwnedCollectionCountTests"
```
Expected: 0 failures — including `Filtered_count_projection_is_a_known_preexisting_hard_fail_in_every_mode`. Shape A must still fail identically after this task: the projection binder's node-kind gate still names only `MongoSizeExpression`, so the leaf still declines and the pre-existing crash is unchanged. If that test moved, the two sides are **not** widening in lockstep — stop and report.

- [ ] **Step 8: Full functional Query sweep, then commit**

```bash
dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build \
  --filter "FullyQualifiedName~FunctionalTests.Query"
```
Expected: 0 failures.

```bash
git add src/ tests/
git commit -m "EF-359: recognize a predicated owned-collection count; the predicate spelling goes native"
```

---

## Task 3: Shape A — the filtered count as a native projection leaf

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/NativeProjectionBinder.cs` (the node-kind gate, ~line 287)
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoProjectionBindingExpressionVisitor.cs` (the registration block ~line 406; `IsCanonicalCountWithoutPredicate` ~line 939)
- Modify: `tests/.../FunctionalTests/Query/NativeOwnedCollectionCountTests.cs` (flip the pinning test)
- Test: `tests/.../FunctionalTests/Query/NativeOwnedCollectionFilteredCountTests.cs`

**Interfaces:**
- Consumes: `MongoFilteredSizeExpression` (Task 1); `TranslateOperand` returning it for a predicated count (Task 2).
- Produces: `MongoProjectionBindingExpressionVisitor.IsCanonicalCount(MethodInfo)` — renamed from `IsCanonicalCountWithoutPredicate`, now admitting both arities of `Count`/`LongCount` on both `Queryable` and `Enumerable`. Task 4 reads it.

- [ ] **Step 1: Write the failing tests for shape A**

```csharp
[Fact]
public void Filtered_count_projection_goes_native()
{
    var collection = Seed(nameof(Filtered_count_projection_goes_native), MatchRows());
    using var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel);

    var rows = db.Entities.AsNoTracking()
        .Select(b => new { b.Title, N = b.Posts.Count(p => p.Rank > 0) })
        .OrderBy(r => r.Title).ToList();

    Assert.Equal(
        [("empty", 0), ("missing", 0), ("none", 0), ("null", 0), ("one", 1), ("three", 3)],
        rows.Select(r => (r.Title, r.N)).ToList());
}

[Fact]
public void Filtered_count_projection_emits_size_over_filter_in_project()
{
    var collection = Seed(nameof(Filtered_count_projection_emits_size_over_filter_in_project), MatchRows());
    using var db = CreateContextWithLogging(collection, MongoQueryMode.NativeOnly, BlogModel, out var spy);

    _ = db.Entities.AsNoTracking().Select(b => new { b.Title, N = b.Posts.Count(p => p.Rank > 0) }).ToList();

    var mql = spy.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery);
    Assert.Contains("$project", mql);
    Assert.Contains("$filter", mql);
    Assert.Contains("$ifNull", mql);
}

[Fact]
public void Filtered_LongCount_projection_goes_native()
{
    var collection = Seed(nameof(Filtered_LongCount_projection_goes_native), MatchRows());
    using var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel);

    var rows = db.Entities.AsNoTracking()
        .Select(b => new { b.Title, N = b.Posts.LongCount(p => p.Rank > 0) })
        .OrderBy(r => r.Title).ToList();

    Assert.Equal([0L, 0L, 0L, 0L, 1L, 3L], rows.Select(r => r.N).ToList());
}

[Fact]
public void Filtered_count_projection_into_a_named_dto_goes_native()
{
    // The DTO spelling reaches NativeProjectionBinder's MemberInit branch, which the anonymous-type tests
    // do not.
    var collection = Seed(nameof(Filtered_count_projection_into_a_named_dto_goes_native), MatchRows());
    using var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel);

    var rows = db.Entities.AsNoTracking()
        .Select(b => new TitleCount { Title = b.Title, N = b.Posts.Count(p => p.Rank > 0) })
        .OrderBy(r => r.Title).ToList();

    Assert.Equal([0, 0, 0, 0, 1, 3], rows.Select(r => r.N).ToList());
}

[Fact]
public void Filtered_count_projection_alongside_sibling_leaves_goes_native()
{
    var collection = Seed(nameof(Filtered_count_projection_alongside_sibling_leaves_goes_native), MatchRows());
    using var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel);

    var rows = db.Entities.AsNoTracking()
        .Select(b => new { b.Title, Filtered = b.Posts.Count(p => p.Rank > 0), All = b.Posts.Count })
        .OrderBy(r => r.Title).ToList();

    // The two counts must differ for the row where they can — otherwise a filtered/unfiltered mix-up passes.
    var none = rows.Single(r => r.Title == "none");
    Assert.Equal(0, none.Filtered);
    Assert.Equal(3, none.All);
}

[Fact]
public void Arithmetic_over_a_filtered_count_projection_goes_native()
{
    var collection = Seed(nameof(Arithmetic_over_a_filtered_count_projection_goes_native), MatchRows());
    using var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel);

    var rows = db.Entities.AsNoTracking()
        .Select(b => new { b.Title, N = b.Posts.Count(p => p.Rank > 0) * 2 })
        .OrderBy(r => r.Title).ToList();

    Assert.Equal([0, 0, 0, 0, 2, 6], rows.Select(r => r.N).ToList());
}

[Fact]
public void Filtered_count_projection_through_an_owned_reference_hop_goes_native()
{
    var collection = Seed(
        nameof(Filtered_count_projection_through_an_owned_reference_hop_goes_native),
        RowWithNotes("two", 2), RowWithNotes("none", 0));
    using var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel);

    var rows = db.Entities.AsNoTracking()
        .Select(b => new { b.Title, N = b.Home.Notes.Count(n => n.Length > 0) })
        .OrderBy(r => r.Title).ToList();

    Assert.Equal([("none", 0), ("two", 1)], rows.Select(r => (r.Title, r.N)).ToList());
}
```

- [ ] **Step 2: Run them and confirm they fail**

Expected: `InvalidOperationException` containing "could not be translated" — the crash EF-359 names.

- [ ] **Step 3: Widen the projection binder's node-kind gate**

In `NativeProjectionBinder.TryTranslateLeaf`, keeping the whole existing comment block (its measurements about the `0`/`false` constant abort remain accurate and are the reason this stays a node-kind gate):

```csharp
// EF-359 adds MongoFilteredSizeExpression, a SIBLING node for a predicated count. It is admitted here for the
// same reason a plain $size is — it renders as a DOCUMENT, so $project cannot read it as an inclusion flag —
// and it is a separate node kind rather than a flag precisely so the query-dialect Tier-1 renderer, the dialect
// classifier and the negator all keep failing closed for it. See MongoFilteredSizeExpression's remarks.
if (translator.TryTranslateValue(leafExpression, out var value)
    && value is MongoSizeExpression or MongoFilteredSizeExpression)
{
    result = value;
    return true;
}
```

- [ ] **Step 4: Widen the visitor's canonical-count predicate and its registration block**

Rename `IsCanonicalCountWithoutPredicate` → `IsCanonicalCount` and widen it:

```csharp
private static bool IsCanonicalCount(MethodInfo method)
{
    if (!method.IsGenericMethod)
        return false;

    var definition = method.GetGenericMethodDefinition();
    return definition == QueryableMethods.CountWithoutPredicate
        || definition == QueryableMethods.LongCountWithoutPredicate
        || definition == QueryableMethods.CountWithPredicate
        || definition == QueryableMethods.LongCountWithPredicate
        || definition == EnumerableMethods.CountWithoutPredicate
        || definition == EnumerableMethods.LongCountWithoutPredicate
        || definition == EnumerableMethods.CountWithPredicate
        || definition == EnumerableMethods.LongCountWithPredicate;
}
```

Update the call site at ~line 407 to the new name. Update that block's XML/inline comment: the existing paragraph on arity ("Arity is not a separate conjunct: it is implied by the canonical constants… take exactly one parameter") is now **wrong** and must be rewritten, not left — the admitted set now spans both arities, and the reason arity needs no separate check is that the canonical constants pin it either way. Update `IsCanonicalCount`'s own doc comment for the same reason.

- [ ] **Step 5: Run the tests**

Expected: PASS. If a test fails at *shaper* rather than translation time (`InvalidOperationException` from `GetProjectionIndex`, or `ArgumentNullException` at materialization), the two sides are not agreeing — the binder populated a projection member the visitor did not register, or vice versa. Report rather than patching around it.

- [ ] **Step 6: Flip the pinning test**

`NativeOwnedCollectionCountTests.Filtered_count_projection_is_a_known_preexisting_hard_fail_in_every_mode` now fails, correctly — it pins the bug this task fixes. Replace it with a test asserting the new behaviour, and say in its comment what it used to pin and that EF-359 closed it:

```csharp
[Fact]
public void Filtered_count_projection_now_goes_native_EF359()
{
    // This test used to pin EF-359: Select(b => new { b.Title, N = b.Posts.Count(p => p.Rank > 0) }) threw
    // InvalidOperationException identically under Native, DriverLinq and NativeOnly — a translation-time crash
    // inside MongoProjectionBindingExpressionVisitor.Translate, before MongoQueryMode was read. EF-359 fixed
    // it; the shape now emits { $size: { $filter: … } } in $project. Full coverage lives in
    // NativeOwnedCollectionFilteredCountTests; this case remains here so the file that documented the bug
    // records its closure.
    var collection = SeedLengths(nameof(Filtered_count_projection_now_goes_native_EF359));

    using var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel);
    var rows = db.Entities.AsNoTracking()
        .Select(b => new { b.Title, N = b.Posts.Count(p => p.Rank > 0) }).ToList();

    Assert.NotEmpty(rows);
}
```

Check `SeedLengths`'s rows before asserting values — `LenRow` gives element ranks `0..n-1`, so `Rank > 0` counts `length - 1` for a non-empty row. Assert the actual expected numbers rather than only `NotEmpty` if the seed makes that clean.

- [ ] **Step 7: Prove the node-kind gate still has teeth**

Widen it to plain `TryTranslateValue` success (drop the `is MongoSizeExpression or MongoFilteredSizeExpression` test), rebuild, and run `NativeOwnedCollectionCountTests` — `Constant_projection_leaf_is_not_admitted_by_the_count_binder_gate` must go red (4 rows). Revert. This confirms Task 3's widening did not defeat the pre-existing guard.

- [ ] **Step 8: Sweep and commit**

```bash
dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build \
  --filter "FullyQualifiedName~FunctionalTests.Query"
```
Expected: 0 failures.

```bash
git add src/ tests/
git commit -m "EF-359: a filtered owned-collection count goes native as a projection leaf"
```

---

## Task 4: Shape C — the bare spelling stops crashing

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoProjectionBindingExpressionVisitor.cs` (the EF-357 arm in the `Queryable` switch, ~line 588)
- Test: `tests/.../FunctionalTests/Query/NativeOwnedCollectionFilteredCountTests.cs`

**Interfaces:**
- Consumes: `EnumerableMethods.CountWithPredicate`/`LongCountWithPredicate` (Task 2); `IsCanonicalCount` (Task 3).
- Produces: nothing later tasks depend on.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public void Bare_filtered_count_projection_returns_correct_values_on_the_fallback_path()
{
    // NOT native, and not for a count-specific reason: a bare (non-`new {…}`) selector body never populates
    // Select.Projection at all — the SP3-wide bare-projection boundary, the same one that keeps
    // Select(b => b.Posts.Count) and Select(b => b.Posts) on the fallback path. What this task fixes is that
    // the shape CRASHED there; it now folds the count client-side and returns correct values.
    var collection = Seed(nameof(Bare_filtered_count_projection_returns_correct_values_on_the_fallback_path), MatchRows());

    foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq })
    {
        using var db = CreateContext(collection, mode, BlogModel);
        var counts = db.Entities.AsNoTracking()
            .OrderBy(b => b.Title)
            .Select(b => b.Posts.Count(p => p.Rank > 0)).ToList();

        Assert.Equal([0, 0, 0, 0, 1, 3], counts);
    }
}

[Fact]
public void Bare_filtered_count_projection_declines_cleanly_under_NativeOnly()
{
    var collection = Seed(nameof(Bare_filtered_count_projection_declines_cleanly_under_NativeOnly), MatchRows());
    using var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel);

    Assert.Throws<NativeTranslationNotSupportedException>(
        () => db.Entities.AsNoTracking().Select(b => b.Posts.Count(p => p.Rank > 0)).ToList());
}

[Fact]
public void Bare_filtered_count_projection_folds_client_side()
{
    var collection = Seed(nameof(Bare_filtered_count_projection_folds_client_side), MatchRows());
    using var db = CreateContextWithLogging(collection, MongoQueryMode.Native, BlogModel, out var spy);

    _ = db.Entities.AsNoTracking().Select(b => b.Posts.Count(p => p.Rank > 0)).ToList();

    // Measured, not inferred: the emitted pipeline is empty — no $project and no $size — so the whole
    // document including the entire array is fetched and counted in process, exactly like the bare
    // UNFILTERED count has done since owned-data slice 7.
    var mql = spy.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery);
    Assert.Contains("aggregate([])", mql);
}

[Fact]
public void Bare_filtered_count_projection_with_a_captured_parameter()
{
    var collection = Seed(nameof(Bare_filtered_count_projection_with_a_captured_parameter), MatchRows());
    var threshold = 0;
    using var db = CreateContext(collection, MongoQueryMode.Native, BlogModel);

    var counts = db.Entities.AsNoTracking()
        .OrderBy(b => b.Title)
        .Select(b => b.Posts.Count(p => p.Rank > threshold)).ToList();

    Assert.Equal([0, 0, 0, 0, 1, 3], counts);
}
```

- [ ] **Step 2: Run them and confirm they fail**

Record the exception for each. `Bare_filtered_count_projection_folds_client_side`'s `aggregate([])` assertion is a guess about the fallback's pipeline — if the measured pipeline differs, **fix the test to match what was measured** and say so, rather than assuming.

- [ ] **Step 3: Widen the EF-357 arm to the predicated overloads**

```csharp
case nameof(Queryable.Count)
    when genericMethod == QueryableMethods.CountWithoutPredicate:
case nameof(Queryable.LongCount)
    when genericMethod == QueryableMethods.LongCountWithoutPredicate:
    if (visitedSource is not CollectionShaperExpression countShaper)
    {
        break;
    }

    return Expression.Call(
        (method.Name == nameof(Queryable.Count)
            ? EnumerableMethods.CountWithoutPredicate
            : EnumerableMethods.LongCountWithoutPredicate)
        .MakeGenericMethod(method.GetGenericArguments()),
        countShaper);

// EF-359: the same rebuild for the PREDICATED overloads. Unreachable for a NATIVE count projection — the
// Route == Projection registration earlier in VisitMethodCall claims that case and pushes the count into
// $project — so this arm only ever sees a shape that is NOT going native, i.e. the BARE spelling, which the
// SP3-wide bare-projection boundary keeps off the native path regardless. The Enumerable overload takes a
// Func<,>, not an Expression<Func<,>>, so the lambda must be UNQUOTED; UnwrapLambdaFromQuote handles the
// Queryable spelling's Quote and passes an already-bare lambda through.
//
// The lambda body is deliberately NOT re-Visited: the rebuilt Enumerable.Count runs client-side over
// MATERIALIZED elements, so the predicate must stay ordinary CLR code. Visiting it would rewrite its member
// accesses into shaper reads against a document the fold no longer has.
case nameof(Queryable.Count)
    when genericMethod == QueryableMethods.CountWithPredicate:
case nameof(Queryable.LongCount)
    when genericMethod == QueryableMethods.LongCountWithPredicate:
    if (visitedSource is not CollectionShaperExpression filteredCountShaper)
    {
        break;
    }

    return Expression.Call(
        (method.Name == nameof(Queryable.Count)
            ? EnumerableMethods.CountWithPredicate
            : EnumerableMethods.LongCountWithPredicate)
        .MakeGenericMethod(method.GetGenericArguments()),
        filteredCountShaper,
        methodCallExpression.Arguments[1].UnwrapLambdaFromQuote());
```

The decline branch stays `break`, never `return null` — `return null` folds through `MatchTypes(null, typeof(int))` to `Expression.Default(int)` and silently returns `0`. Keep the existing arm's long comment intact and add the new one beside it.

- [ ] **Step 4: Run the tests**

Expected: PASS.

**Contingency for `Bare_filtered_count_projection_with_a_captured_parameter`.** A captured local may arrive as an EF query-parameter node the client-side fold cannot evaluate. If that test fails while the constant-predicate tests pass, do **not** force it: add a guard declining (`break`) when the lambda body contains a query parameter, so that one spelling keeps failing exactly as it does today, rename the test to pin the decline instead, and report the narrowing — an honest smaller fix beats a half-working one.

- [ ] **Step 5: Sweep and commit**

```bash
dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build \
  --filter "FullyQualifiedName~FunctionalTests.Query"
```
Expected: 0 failures.

```bash
git add src/ tests/
git commit -m "EF-359: the bare filtered-count projection folds client-side instead of crashing"
```

---

## Task 5: The differential oracle

The primary correctness bar. A filtered count can return a silently wrong **number**, so an in-memory oracle over the *same* `Expression` is the gate — driver-LINQ is unusable (shapes A and C crash today, and a wrapped count under `DriverLinq` renders a bare `$size` with no `$ifNull` and aborts on ragged data).

**Files:**
- Modify: `tests/.../FunctionalTests/Query/NativeOwnedCollectionFilteredCountTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 1–4.
- Produces: nothing.

- [ ] **Step 1: Write the differential seed**

Cover **array** states × **element** states. The element axis is what the unfiltered `.Count` slice's own differential rows did not need and this one does.

```csharp
// Array states: multi / single / empty / MISSING / explicit BSON null.
// Element states, within the multi rows: predicate matches, does not match, the predicate's field is MISSING,
// and the predicate's field is explicitly BSON null. The last two are the states a $filter cond evaluates
// against a non-existent value, and the ones an in-memory oracle over nullable CLR properties disagrees with
// if the rendering is wrong.
private static BsonDocument[] DifferentialRows() =>
[
    Row("multi_mixed", new BsonArray
    {
        PostDoc(rank: 5, heading: "a"),      // matches Rank > 0
        PostDoc(rank: -5, heading: "b"),     // does not
        PostDoc(rank: null, heading: "c"),   // Rank explicitly null
        NoRankPostDoc("d")                   // Rank field ABSENT
    }),
    Row("multi_all_match", new BsonArray { PostDoc(rank: 1, heading: "a"), PostDoc(rank: 2, heading: "b") }),
    Row("multi_none_match", new BsonArray { PostDoc(rank: -1, heading: "a"), PostDoc(rank: -2, heading: "b") }),
    Row("single_match", new BsonArray { PostDoc(rank: 7, heading: "a") }),
    Row("single_no_match", new BsonArray { PostDoc(rank: -7, heading: "a") }),
    Row("empty", new BsonArray()),
    Row("missing", null),
    Row("null", BsonNull.Value)
];

// PostDoc always writes a Rank element (BsonNull when the argument is null), so a MISSING Rank needs its own
// builder — and missing-vs-null is exactly the distinction this oracle exists to check.
private static BsonDocument NoRankPostDoc(string heading)
    => new() { { "Heading", heading }, { "Other", 0 }, { "Title", "p" }, { "Comments", new BsonArray() } };
```

- [ ] **Step 2: Write the differential theory**

The projection selector must be a **named DTO**, not an anonymous type, so the same `Expression` object can be both sent to the server and compiled — the precedent is `TitleCount` in `NativeOwnedCollectionCountTests`.

```csharp
public static IEnumerable<object[]> FilteredCountSelectors() =>
[
    ["gt", (Expression<Func<Blog, TitleCount>>)(b => new TitleCount { Title = b.Title, N = b.Posts.Count(p => p.Rank > 0) })],
    ["lt", (Expression<Func<Blog, TitleCount>>)(b => new TitleCount { Title = b.Title, N = b.Posts.Count(p => p.Rank < 0) })],
    ["eq", (Expression<Func<Blog, TitleCount>>)(b => new TitleCount { Title = b.Title, N = b.Posts.Count(p => p.Rank == 5) })],
    ["ne", (Expression<Func<Blog, TitleCount>>)(b => new TitleCount { Title = b.Title, N = b.Posts.Count(p => p.Rank != 5) })],
    ["and", (Expression<Func<Blog, TitleCount>>)(b => new TitleCount { Title = b.Title, N = b.Posts.Count(p => p.Rank > 0 && p.Rank < 6) })],
    ["or", (Expression<Func<Blog, TitleCount>>)(b => new TitleCount { Title = b.Title, N = b.Posts.Count(p => p.Rank > 4 || p.Rank < -4) })],
    ["null_check", (Expression<Func<Blog, TitleCount>>)(b => new TitleCount { Title = b.Title, N = b.Posts.Count(p => p.Rank == null) })],
    ["field_to_field", (Expression<Func<Blog, TitleCount>>)(b => new TitleCount { Title = b.Title, N = b.Posts.Count(p => p.Rank > p.Other) })],
    ["arithmetic", (Expression<Func<Blog, TitleCount>>)(b => new TitleCount { Title = b.Title, N = b.Posts.Count(p => p.Rank + 1 > 0) })]
];

[Theory]
[MemberData(nameof(FilteredCountSelectors))]
public void Filtered_count_projection_equals_the_in_memory_oracle_for_every_array_and_element_state(
    string name, Expression<Func<Blog, TitleCount>> selector)
{
    var collection = Seed($"diff_{name}", DifferentialRows());

    // Oracle: materialize whole entities, then evaluate the SAME selector in memory. Sending one Expression
    // object to both sides is what makes this a differential test rather than two hand-written predicates
    // that can silently diverge.
    List<(string, int)> expected;
    using (var db = CreateContext(collection, MongoQueryMode.Native, BlogModel))
    {
        expected = db.Entities.AsNoTracking().ToList()
            .Select(selector.Compile()).Select(r => (r.Title, r.N)).OrderBy(r => r.Item1).ToList();
    }

    // Server: must go NATIVE (NativeOnly is the only reliable signal) and agree exactly.
    List<(string, int)> actual;
    using (var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel))
    {
        actual = db.Entities.AsNoTracking().Select(selector).ToList()
            .Select(r => (r.Title, r.N)).OrderBy(r => r.Item1).ToList();
    }

    Assert.Equal(expected, actual);
}
```

Add the predicate-spelling counterpart as a second theory over `Expression<Func<Blog, bool>>` predicates (`b.Posts.Count(p => p.Rank > 0) >= k` for `k` in 0..3, plus `== 0`, `!= 0`), following `NativeOwnedCollectionCountTests.Count_result_equals_the_in_memory_oracle_for_every_array_length_and_state` exactly.

- [ ] **Step 3: Run the differential tests**

Expected: PASS for every row. **A failure here is a finding, not a test bug** — investigate the divergence before touching the test. Two divergences are plausible and each needs a decision recorded rather than a silent test edit:
- **`null_check`**: MQL `$eq: [null]` matching semantics for a missing versus explicitly-null field may not agree with the CLR `p.Rank == null` over a nullable `int?`. If they diverge, the honest fix is to make `CanRender` (or the translator) **decline** a null-comparison element predicate and pin the decline, not to weaken the oracle.
- **`field_to_field` / `arithmetic`**: these are aggregation-dialect-native, so they are expected to work — but they also inherit the provider's known value-equality semantics for mismatched CLR numeric types (EF-221). Both fields here are `int?`, so that should not arise; if a row diverges, record which.

If any selector must be declined, move it out of this theory into a decline test with a comment naming what was measured.

- [ ] **Step 4: Prove the oracle is non-vacuous**

Break the rendering deliberately — drop the `$ifNull` from `RenderFilteredSize` — rebuild, and confirm the differential theory goes red on the `missing` and `null` rows (or aborts the aggregate, which is equally conclusive). Revert. An oracle that stays green with the null-safety removed is not testing the states it claims to.

- [ ] **Step 5: Commit**

```bash
git add tests/
git commit -m "EF-359: differential in-memory oracle across array and element states"
```

---

## Task 6: Sweeps and documentation

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md`
- Modify: `docs/native-query-status-EF-322.md`
- Modify: `docs/superpowers/specs/2026-07-30-native-filtered-count-design.md` (add §11 "As-built deltas")

**Interfaces:** Consumes everything. Produces the record the next session reads first.

- [ ] **Step 1: Three-version test sweep**

```bash
# via the /test-all skill, or directly:
for v in EF8 EF9 EF10; do
  dotnet test MongoDB.EFCoreProvider.sln -c "Debug $v" 2>&1 | tail -5
done
```
Requirement: **0 failures on all three.** Record what was run and the outcome — not bare pass counts. Also confirm `git diff --stat` shows no added or removed `#if` lines in `src/`.

- [ ] **Step 2: EF10 spec sweep, BOTH axes**

```bash
dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests -c "Debug EF10" --no-build          # Native
MONGODB_EF_NATIVE_ONLY=1 dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests -c "Debug EF10" --no-build
```

Compare **both** to the branch base (`33fdc58`): the `Native`-mode MQL baselines *and* the `NativeOnly` pass/fail set. Checking only the `NativeOnly` pass set is exactly how the `All` slice missed a flip. Zero delta is the expectation on both axes (Northwind has no owned collections). **A non-zero delta is a finding — report it, do not re-baseline it.**

- [ ] **Step 3: Write the `Query/AGENTS.md` as-built note**

Add a note in the same style and position as the sibling `.Count`-in-a-predicate and count-projection notes. It must state, at minimum:

- The three shapes and their exact dispositions (§2 of the design), including that C is **not** native and why that is the SP3-wide boundary rather than anything count-specific.
- **Why `MongoFilteredSizeExpression` is a sibling type and not a flag** — the four `is MongoSizeExpression` sites and what each would do wrong. This is the single most important sentence in the note.
- That `$ifNull` is mandatory (hard server error, not a wrong answer) — cite the Task 0 measurement.
- The new `CanRender` classifier and the "must change together" contract it joins.
- Every decline from design §6, with the test that pins it, and the nested-in-quantifier decline called out specifically (`$expr` inside `$elemMatch` is a hard server error).
- That a correlated element predicate is a **deferrable capability** here, unlike the quantifier slices' correlated decline — a `$filter` `cond` can reference the enclosing document.
- Anything Task 0 or Tasks 2–5 measured that contradicts an earlier claim, **corrected in place** rather than appended.

Then correct the two sibling notes where they now read stale: the count-in-a-predicate note lists a filtered `Count(pred)` as deferred, and the count-projection note describes EF-359 as an open bug.

- [ ] **Step 4: Update the status doc**

`docs/native-query-status-EF-322.md`: add slice 9 to the §2 slice table; move the filtered-count entry out of §4's "Hard-fails in every mode" list into the native list with its precise disposition; strike EF-359 from §5's nearest-follow-on ordering and re-rank what remains; mark EF-359 as closed in the §6 bug table with the mechanism in one line. Do **not** cite this branch's own commit SHAs (a commit cannot cite itself) — reference the branch name.

- [ ] **Step 5: Add the design doc's as-built deltas section**

A new §11 recording every place the implementation diverged from §§3–7, and every claim the work measured false. A reader of the design alone must not be misled. Include the Task 0 findings that mattered and any decline that turned out broader or narrower than designed.

- [ ] **Step 6: Grep for stale cross-references**

```bash
grep -rn "EF-359" --include=*.md --include=*.cs docs/ src/ tests/
grep -rn "IsCanonicalCountWithoutPredicate" src/ tests/ docs/
```
Every hit that still describes EF-359 as open, or names the renamed method, must be updated. Quote-anchor new cross-references rather than citing line numbers — line numbers rot within a slice.

- [ ] **Step 7: Commit**

```bash
git add src/ tests/ docs/
git commit -m "EF-359: sweeps, AGENTS.md as-built note, status doc and as-built deltas"
```

---

## Self-review notes

**Spec coverage:** §1 → Task 3 (the fix) + Task 6 (documentation). §2 shape A → Task 3; shape B → Task 2; shape C → Task 4. §3 → Task 1 (node + prefix rewriter). §4 → Task 1 (rendering, `elementVariable`, variable naming) + Task 0 steps 2–3, 5. §5's four edits → Task 2 (edits 1–2), Task 3 (edits 3–4a), Task 4 (edit 4b). §6 `CanRender` → Task 1; the decline table → Task 2 (correlated, `CanRender`, primitive, nested-in-quantifier) and Task 3/4 for the projection-side ones. §7 → Task 0 (spike), Task 5 (oracle), Tasks 2/3/5 (mutation tripwires), Task 6 (sweeps). §8 → nothing to implement; Task 6 records it. §9 → Global Constraints (no `#if`, no `BREAKING-CHANGES.md`). §10 → Task 5's oracle and Task 2's Tier-1 tripwire.

**Known gap, deliberate:** the reference-collection filtered count (`Filtered_reference_collection_count_still_falls_back`) has no task — it is out of scope per §8 and its existing test must simply stay green, which the Task 2/3/4 functional sweeps cover.

**Type consistency:** `MongoFilteredSizeExpression(string arrayPath, MongoExpression elementPredicate, Type type)` with `ArrayPath`/`ElementPredicate`/`Type` is used identically in Tasks 1, 2, 3 and the prefix-rewriter test. `Render(node, placeholders, elementVariable)` and `CanRender(node)` are used with those exact signatures in Tasks 1 and 2. `TryMatchCountExpression(node, out source, out predicate)` is defined and called only in Task 2. `IsCanonicalCount` is introduced in Task 3 and referenced in Task 4's interfaces; `IsCanonicalCountWithPredicate` (Task 2, in the translator) is a **different** method in a different file — deliberately, since the translator matches only the predicated arms while the visitor admits both arities.
