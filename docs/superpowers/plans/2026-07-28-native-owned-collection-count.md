# Native owned-collection `.Count` in a predicate — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make an owned-collection element count compared against a value — `Where(b => b.Posts.Count > 2)` —
translate natively, for all six comparison operators, both operand orders, both a constant and a parameterized
threshold, and the `.Count` / `.Count()` / `.LongCount()` spellings.

**Architecture:** One dialect-neutral AST shape, `MongoBinaryExpression(op, MongoSizeExpression(path), value)`,
produced by `MongoExpressionTranslator.TranslateOperand`. The **renderer** then picks the tier: a size-vs-integer-
constant comparison renders in the query dialect as an array-index existence test
(`{"Posts.2": {$exists: true}}`), and everything else falls through the existing `RenderAsExpr` catch-all to
`{$expr: {$gt: [{$size: {$ifNull: ["$Posts", []]}}, …]}}`. Bare `Any()` is re-expressed as `Count >= 1` so array
cardinality has exactly one representation.

**Tech Stack:** C# / .NET (net8.0 for EF8+EF9, net10.0 for EF10), xUnit, MongoDB C# driver, EF Core 8/9/10.

**Spec:** `docs/superpowers/specs/2026-07-28-native-owned-collection-count-design.md`

## Global Constraints

- **Branch:** `EF-322-owned-collection-count-native`, already cut off the native tip `c19c99b`. Design doc
  already committed as `fd6a7c0`.
- **No `#if`.** Every type touched is `internal`; EF8/EF9/EF10 behavior must be identical.
- **No public API, no annotation key, no `Mongo:`-prefixed metadata, no new AST node.**
- **Nullable reference types are enabled** on `src/` — annotate accordingly.
- **Preserve file BOMs** (the repo-wide rule in `AGENTS.md`). Verified during Task 2: the `Query/` files this
  plan touches do **not** currently have BOMs (they start `/* ` = `2f2a20`), so there is none to preserve here —
  just don't introduce or strip one. Re-check with `head -c3 <file> | xxd -p` if you rewrite a file wholesale.
- **Unit tests use plain xUnit `Assert.*`.** FluentAssertions is not referenced by any test project.
- **`NativeOnly` is the only reliable "went native" signal.** MQL shape does not distinguish native from
  driver-LINQ for a `$match`. Never assert nativeness by inspecting MQL alone.
- **Commit after every task.** Commit messages start with `EF-322: `.
- Build one EF version: `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"`.
- Run functional tests with **both `MONGODB_URI` and `ATLAS_URI` unset** so each run gets its own isolated
  `mongodb/mongodb-atlas-local` container.

---

## File Structure

| File | Responsibility | Task |
|---|---|---|
| `src/.../Query/Expressions/MongoSizeExpression.cs` | The `$size` value node; gains `NullSafe` | 2 |
| `src/.../Query/NativeTranslation/MongoAggregationExpressionRenderer.cs` | `$expr` tier — `$size` honouring `NullSafe` | 2 |
| `src/.../Query/NativeTranslation/MongoFieldPrefixRewriter.cs` | Prefix a size node's array path | 2 |
| `src/.../Query/NativeTranslation/MongoQueryLanguageRenderer.cs` | Constant tier + the admissibility classifier | 3, 5 |
| `src/.../Query/NativeTranslation/MongoExpressionNegator.cs` | Exact complement of a size comparison | 4 |
| `src/.../Query/Expressions/MongoElemMatchExpression.cs` | `ElementPredicate` becomes non-nullable | 5 |
| `src/.../Query/NativeTranslation/MongoExpressionTranslator.cs` | Recognition, left-normalization, `Not` arm, bare-`Any()` arm | 5, 6 |
| `tests/.../UnitTests/Query/NativeTranslation/MongoQueryLanguageRendererTests.cs` | Constant-tier + classifier rows | 3, 5 |
| `tests/.../UnitTests/Query/NativeTranslation/MongoExpressionNegatorTests.cs` | Inversion + closure property | 4, 5 |
| `tests/.../UnitTests/Query/NativeTranslation/MongoFieldPrefixRewriterTests.cs` | Size-node prefixing | 2 |
| `tests/.../FunctionalTests/Query/NativeOwnedCollectionCountTests.cs` | **New** — end-to-end natives, MQL, declines, differential matrix | 7, 8 |
| `src/.../Query/AGENTS.md` | As-built note | 9 |
| `docs/native-query-status-EF-322.md` | Status-report update | 9 |

**Dependency order.** Task 2 must land before Task 6, because `MongoFieldPrefixRewriter` currently **throws** on
an unknown node (`MongoFieldPrefixRewriter.cs:46`) and an owned `SelectMany` inner filter containing a count
would reach it — the same load-bearing ordering the `Any` slice recorded. Tasks 3 and 4 must land before Task 5,
because the bare-`Any()` unification depends on both the constant-tier rendering and the negator's inversion.

---

## Task 1: Throwaway spike — EF tree shape and index behavior

**No production code. No commit to `src/`.** Deliverable is a findings document plus a GO / NO-GO verdict.

**Files:**
- Create: `.superpowers/sdd/EF-322-owned-collection-count-spike.md` (gitignored scratch)
- Temporary probe test (delete before finishing): `tests/.../FunctionalTests/Query/CountSpikeProbeTests.cs`

- [ ] **Step 1: Capture the EF expression-tree shape for every count spelling, on all three EF versions**

Write a temporary probe that dumps the tree the native translator actually receives. The fastest reliable way is
a throwaway test that sets a breakpoint-equivalent — add a temporary `Console.WriteLine(be.ToString())` plus
`be.NodeType`/`be.Left.NodeType`/`be.Left.GetType().Name` at the top of
`MongoExpressionTranslator.TranslateComparison`, run the probe, record the output, then revert the instrumentation.

Probe these six queries against an owned-collection model (`mb.Entity<Blog>().OwnsMany(b => b.Posts)`):

```csharp
db.Entities.Where(b => b.Posts.Count > 2).ToList();          // .Count property, inline literal
db.Entities.Where(b => b.Posts.Count() > 2).ToList();        // Count() call
db.Entities.Where(b => b.Posts.LongCount() > 2L).ToList();   // LongCount() call
db.Entities.Where(b => 2 < b.Posts.Count).ToList();          // reversed operand order
var threshold = 2;
db.Entities.Where(b => b.Posts.Count > threshold).ToList();  // captured local
db.Entities.Where(b => b.Posts.Any(p => p.Comments.Count > 1)).ToList();  // nested in a quantifier
```

Record, for each: whether the count arrives as a `MemberExpression` on `List<T>`/`ICollection<T>` or as an
`Enumerable.Count`/`Queryable.Count` `MethodCallExpression`; whether the source is wrapped in `AsQueryable()`;
whether the source hops are `MemberExpression` or `EF.Property(...)` calls; and whether the threshold is a
`ConstantExpression` or an EF query parameter. Run the probe under `-c "Debug EF8"`, `-c "Debug EF9"` and
`-c "Debug EF10"` and note any difference.

**Why this is blocking:** the `Any` slice recorded "always the `Queryable` spelling, always `Quote`-wrapped,
always exactly one `AsQueryable()`", and the `All` slice then found a shape with none of those properties. Assume
nothing.

- [ ] **Step 2: Measure whether `{"path.n": {$exists: true}}` is genuinely IXSCAN-capable**

Seed ~200 documents with an embedded `Posts` array of varying length, create a multikey index
(`db.coll.createIndex({"Posts.Rank": 1})` and a plain `{"Posts": 1}`), then run a `queryPlanner` explain for each
of the four relational forms:

```javascript
db.coll.find({ "Posts.2": { $exists: true  } }).explain("queryPlanner")
db.coll.find({ "Posts.1": { $exists: true  } }).explain("queryPlanner")
db.coll.find({ "Posts.1": { $exists: false } }).explain("queryPlanner")
db.coll.find({ "Posts.2": { $exists: false } }).explain("queryPlanner")
```

Record the winning plan stage (`IXSCAN` vs `COLLSCAN`) and the index bounds for each.

**This item cannot change the design** — the constant tier is required regardless, for `$elemMatch` legality and
for missing/null-array correctness. If the answer is COLLSCAN, §3.1 of the design doc gets **corrected** (as the
`All` slice corrected its own index claim) rather than the approach changing.

- [ ] **Step 3: Write the findings document**

Record, in `.superpowers/sdd/EF-322-owned-collection-count-spike.md`: the six tree shapes with any EF-version
differences; the four explain results; and an explicit **GO / NO-GO** verdict. NO-GO only if a tree shape is
found that the Task 6 matcher provably cannot recognize without a design change.

Also record explicitly that spike item 3 from the design doc (a property-less parameter threshold) was
**resolved by inspection, not measurement**: `MongoValueRenderer.cs:52-53` already handles a
`MongoParameterExpression` with a `null` `ForSerialization` via
`placeholders.CreatePlaceholder(parameter.Name, serializer: null)`.

- [ ] **Step 4: Delete the probe test and revert all instrumentation**

Run: `git status --short`
Expected: only the untracked (gitignored) findings file. No modification to any `src/` file.

---

## Task 2: `MongoSizeExpression` gains `NullSafe`; the `$expr` renderer and prefix rewriter learn it

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoSizeExpression.cs`
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoAggregationExpressionRenderer.cs:51`
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoFieldPrefixRewriter.cs:45-48`
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoFieldPrefixRewriterTests.cs`

**Interfaces:**
- Produces: `MongoSizeExpression(string fieldName, Type type, bool nullSafe = false)` with
  `public bool NullSafe { get; }`. `nullSafe: false` keeps today's `{$size: "$path"}` rendering byte-identical;
  `nullSafe: true` renders `{$size: {$ifNull: ["$path", []]}}`.

- [ ] **Step 1: Write the failing tests**

Add to `MongoFieldPrefixRewriterTests.cs`:

```csharp
    [Fact]
    public void Prefixes_the_array_path_of_a_size_node()
    {
        var rewritten = MongoFieldPrefixRewriter.Rewrite(
            new MongoSizeExpression("Comments", typeof(int), nullSafe: true), "Posts");

        var size = Assert.IsType<MongoSizeExpression>(rewritten);
        Assert.Equal("Posts.Comments", size.FieldName);
        Assert.True(size.NullSafe);
    }

    [Fact]
    public void Prefixes_a_size_node_inside_a_comparison()
    {
        var comparison = new MongoBinaryExpression(
            MongoBinaryOperator.GreaterThan,
            new MongoSizeExpression("Comments", typeof(int), nullSafe: true),
            new MongoConstantExpression(2, null));

        var rewritten = Assert.IsType<MongoBinaryExpression>(
            MongoFieldPrefixRewriter.Rewrite(comparison, "Posts"));

        Assert.Equal("Posts.Comments", Assert.IsType<MongoSizeExpression>(rewritten.Left).FieldName);
    }
```

Add to `MongoQueryLanguageRendererTests.cs`:

```csharp
    [Fact]
    public void Renders_a_null_safe_size_in_the_expr_dialect_with_ifNull()
    {
        // $size against a MISSING or explicitly-null embedded array is a HARD SERVER ERROR that aborts the
        // whole aggregate — the same failure mode the driver's own count translation has. $ifNull maps both
        // states to [], giving 0, which is what LINQ's Count answers for a missing embedded array.
        var pred = new MongoBinaryExpression(
            MongoBinaryOperator.GreaterThan,
            new MongoSizeExpression("Posts", typeof(int), nullSafe: true),
            new MongoParameterExpression("__n", null));

        var rendered = new MongoQueryLanguageRenderer().Render(pred, new PlaceholderTable());

        var expr = rendered.AsBsonDocument["$expr"].AsBsonDocument;
        var size = expr["$gt"].AsBsonArray[0].AsBsonDocument;
        Assert.Equal(
            BsonDocument.Parse("{ $size: { $ifNull: [ '$Posts', [] ] } }"),
            size);
    }

    [Fact]
    public void Renders_a_non_null_safe_size_without_ifNull_so_the_lookup_alias_form_is_unchanged()
    {
        // The projected reference-collection Count path constructs MongoSizeExpression with the DEFAULT
        // nullSafe: false, because a $lookup output alias is always an array. Several committed spec
        // baselines pin { "$size" : "$_lookup_Orders" }; this test is what keeps them from moving.
        var rendered = MongoAggregationExpressionRenderer.Render(
            new MongoSizeExpression("_lookup_Orders", typeof(int)), new PlaceholderTable());

        Assert.Equal(BsonDocument.Parse("{ $size: '$_lookup_Orders' }"), rendered);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run:
```bash
dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj \
  -c "Debug EF10" --filter "FullyQualifiedName~MongoFieldPrefixRewriterTests|FullyQualifiedName~MongoQueryLanguageRendererTests"
```
Expected: compile error — `MongoSizeExpression` has no three-argument constructor and no `NullSafe` property.

- [ ] **Step 3: Add `NullSafe` to `MongoSizeExpression`**

Replace the constructor and add the property. Also update the class summary, which currently claims the node is
used only for a projected collection-navigation `Count` over a `_lookup_<Nav>` alias:

```csharp
/// <summary>
/// Represents a native <c>{ $size: … }</c> aggregation expression over a named array field, identified by its
/// dotted document path.
/// </summary>
/// <remarks>
/// <para>
/// Two uses, distinguished by <see cref="NullSafe"/>:
/// </para>
/// <list type="bullet">
/// <item>
/// A projected collection-navigation <c>Count</c> (<c>select new { ..., OrderCount = c.Orders.Count }</c>),
/// where <see cref="FieldName"/> is the synthetic <c>_lookup_&lt;Nav&gt;</c> array field written by the matching
/// <see cref="LookupExpression"/>. A <c>$lookup</c> always writes an array, so <see cref="NullSafe"/> is
/// <see langword="false"/> and the rendering is the plain <c>{ $size: "$path" }</c>.
/// </item>
/// <item>
/// An OWNED (embedded) collection's element count used in a predicate (<c>Where(b =&gt; b.Posts.Count &gt; 2)</c>),
/// where <see cref="FieldName"/> is the embedded array's dotted path. An embedded array can be MISSING or
/// explicitly BSON <c>null</c>, and <c>$size</c> against either is a hard server error that aborts the whole
/// aggregate — so <see cref="NullSafe"/> is <see langword="true"/> and the rendering wraps the path in
/// <c>$ifNull</c>, mapping both states to <c>[]</c> (count 0, which is what LINQ answers for a missing embedded
/// array).
/// </item>
/// </list>
/// <para>
/// This does not wrap a <see cref="MongoFieldExpression"/> because that node requires a backing
/// <see cref="Microsoft.EntityFrameworkCore.Metadata.IProperty"/>, and neither an array navigation nor a
/// <c>$lookup</c> alias has one.
/// </para>
/// </remarks>
internal sealed class MongoSizeExpression : MongoExpression
{
    /// <summary>
    /// Creates a <see cref="MongoSizeExpression"/> over the named array field.
    /// </summary>
    /// <param name="fieldName">The array's dotted document path (e.g. <c>_lookup_Orders</c>, <c>Posts</c>).</param>
    /// <param name="type">The CLR type of the resulting count (typically <see cref="int"/> or <see cref="long"/>).</param>
    /// <param name="nullSafe">
    /// <see langword="true"/> to render <c>{ $size: { $ifNull: [ "$path", [] ] } }</c> — required for an
    /// embedded array, which may be missing or explicitly null. <see langword="false"/> (the default) renders
    /// the plain <c>{ $size: "$path" }</c>, preserving the emitted MQL of the projected-<c>Count</c> path.
    /// </param>
    public MongoSizeExpression(string fieldName, Type type, bool nullSafe = false)
    {
        FieldName = fieldName;
        Type = type;
        NullSafe = nullSafe;
    }

    /// <summary>The array's dotted document path this <c>$size</c> is computed over.</summary>
    public string FieldName { get; }

    /// <summary>
    /// Whether the array path is wrapped in <c>$ifNull</c> so a missing or explicitly-null array counts as
    /// empty instead of aborting the aggregate. See the class remarks.
    /// </summary>
    public bool NullSafe { get; }

    /// <inheritdoc />
    public override Type Type { get; }
}
```

- [ ] **Step 4: Honour `NullSafe` in the aggregation renderer**

In `MongoAggregationExpressionRenderer.cs`, replace the line-51 arm with a delegation:

```csharp
            MongoSizeExpression size => RenderSize(size),
```

and add the helper next to `RenderBinary`:

```csharp
    // A missing or explicitly-null array makes $size a hard server error that aborts the whole aggregate, so an
    // EMBEDDED array path is wrapped in $ifNull (count 0 — what LINQ answers for a missing embedded array). A
    // $lookup output alias is always an array, so that path keeps the plain form and its committed spec
    // baselines stay byte-identical. See MongoSizeExpression's remarks.
    private static BsonValue RenderSize(MongoSizeExpression size)
        => size.NullSafe
            ? new BsonDocument("$size",
                new BsonDocument("$ifNull", new BsonArray { "$" + size.FieldName, new BsonArray() }))
            : new BsonDocument("$size", "$" + size.FieldName);
```

- [ ] **Step 5: Teach the prefix rewriter the size node**

In `MongoFieldPrefixRewriter.Rewrite`, add before the `MongoConstantExpression or MongoParameterExpression` arm:

```csharp
            // A size node's FieldName is a document path like any field reference, so it prefixes the same way.
            // This case is LOAD-BEARING, not defensive: an owned SelectMany's inner filter reaches Rewrite, so a
            // count inside one (SelectMany(b => b.Posts.Where(p => p.Comments.Count > 1), …)) would otherwise
            // hit the throw below — turning a clean decline into a crash inside pre-existing code.
            MongoSizeExpression s => new MongoSizeExpression(prefix + "." + s.FieldName, s.Type, s.NullSafe),
```

- [ ] **Step 6: Run the tests to verify they pass**

Run:
```bash
dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj \
  -c "Debug EF10" --filter "FullyQualifiedName~MongoFieldPrefixRewriterTests|FullyQualifiedName~MongoQueryLanguageRendererTests"
```
Expected: PASS, zero failures.

- [ ] **Step 7: Verify the projected-`Count` spec baselines have not moved**

Run:
```bash
dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --filter "FullyQualifiedName~NorthwindSelectQueryMongoTest"
```
Expected: PASS. Any `{ "$size" : "$_lookup_Orders" }` baseline failure means the default `nullSafe: false` was
not preserved somewhere.

- [ ] **Step 8: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoSizeExpression.cs \
        src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoAggregationExpressionRenderer.cs \
        src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoFieldPrefixRewriter.cs \
        tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoFieldPrefixRewriterTests.cs \
        tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoQueryLanguageRendererTests.cs
git commit -m "EF-322: MongoSizeExpression gains NullSafe; \$expr renderer and prefix rewriter learn it"
```

---

## Task 3: The constant tier — array-index rendering and the admissibility classifier

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoQueryLanguageRenderer.cs` (dispatch at
  `:74-89`, `IsQueryDialectRenderable` at `:339-362`, plus new helpers)
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoQueryLanguageRendererTests.cs`

**Interfaces:**
- Consumes: `MongoSizeExpression(path, type, nullSafe)` from Task 2.
- Produces: `private static BsonDocument? TryRenderSizeComparison(MongoBinaryExpression binary)` — the single
  definition of both "is this admissible in the query dialect?" and "what does it render to". `RenderNode` and
  `IsQueryDialectRenderable` both call it, so the classifier and the renderer **cannot drift**.

- [ ] **Step 1: Write the failing tests**

Add to `MongoQueryLanguageRendererTests.cs`. `SizeOf`/`Count` are local helpers to keep the rows readable:

```csharp
    // --- Array cardinality: the query-dialect array-index existence form ---

    private static MongoBinaryExpression Count(MongoBinaryOperator op, object threshold)
        => new(op,
            new MongoSizeExpression("Posts", typeof(int), nullSafe: true),
            new MongoConstantExpression(threshold, null));

    private static BsonValue RenderCount(MongoBinaryOperator op, object threshold)
        => new MongoQueryLanguageRenderer().Render(Count(op, threshold), new PlaceholderTable());

    [Fact]
    public void Renders_count_greater_than_as_index_exists()
        => Assert.Equal(
            BsonDocument.Parse("{ 'Posts.2': { $exists: true } }"),
            RenderCount(MongoBinaryOperator.GreaterThan, 2));

    [Fact]
    public void Renders_count_greater_than_or_equal_as_one_lower_index_exists()
        => Assert.Equal(
            BsonDocument.Parse("{ 'Posts.1': { $exists: true } }"),
            RenderCount(MongoBinaryOperator.GreaterThanOrEqual, 2));

    [Fact]
    public void Renders_count_less_than_as_one_lower_index_absent()
        => Assert.Equal(
            BsonDocument.Parse("{ 'Posts.1': { $exists: false } }"),
            RenderCount(MongoBinaryOperator.LessThan, 2));

    [Fact]
    public void Renders_count_less_than_or_equal_as_index_absent()
        => Assert.Equal(
            BsonDocument.Parse("{ 'Posts.2': { $exists: false } }"),
            RenderCount(MongoBinaryOperator.LessThanOrEqual, 2));

    [Fact]
    public void Renders_count_equal_as_a_merged_two_key_document()
        // C == 2 ⇔ more than 1 AND at most 2. CombineAnd merges the two distinct keys into one document.
        => Assert.Equal(
            BsonDocument.Parse("{ 'Posts.1': { $exists: true }, 'Posts.2': { $exists: false } }"),
            RenderCount(MongoBinaryOperator.Equal, 2));

    [Fact]
    public void Renders_count_equal_zero_as_a_single_absent_index()
        // C == 0 needs only the upper bound — and it is TRUE for a missing or explicitly-null array, which is
        // what LINQ answers. { Posts: { $size: 0 } } would wrongly answer false for both.
        => Assert.Equal(
            BsonDocument.Parse("{ 'Posts.0': { $exists: false } }"),
            RenderCount(MongoBinaryOperator.Equal, 0));

    [Fact]
    public void Renders_count_not_equal_as_an_or_of_the_two_flips()
        => Assert.Equal(
            BsonDocument.Parse(
                "{ $or: [ { 'Posts.1': { $exists: false } }, { 'Posts.2': { $exists: true } } ] }"),
            RenderCount(MongoBinaryOperator.NotEqual, 2));

    [Fact]
    public void Renders_count_not_equal_zero_as_a_single_present_index()
        => Assert.Equal(
            BsonDocument.Parse("{ 'Posts.0': { $exists: true } }"),
            RenderCount(MongoBinaryOperator.NotEqual, 0));

    [Fact]
    public void Renders_a_long_threshold_in_the_index_form()
        => Assert.Equal(
            BsonDocument.Parse("{ 'Posts.2': { $exists: true } }"),
            RenderCount(MongoBinaryOperator.GreaterThan, 2L));

    [Theory]
    // Tautologies and contradictions: no index arithmetic is possible, so these are NOT admissible in the
    // query dialect and must route to $expr, which handles them correctly and generally.
    [InlineData(MongoBinaryOperator.GreaterThanOrEqual, 0)]   // always true
    [InlineData(MongoBinaryOperator.GreaterThan, -1)]         // always true
    [InlineData(MongoBinaryOperator.LessThan, 0)]             // always false
    [InlineData(MongoBinaryOperator.LessThanOrEqual, -1)]     // always false
    [InlineData(MongoBinaryOperator.Equal, -1)]               // always false
    public void Degenerate_count_thresholds_route_to_expr(MongoBinaryOperator op, int threshold)
    {
        var rendered = RenderCount(op, threshold).AsBsonDocument;

        Assert.True(rendered.Contains("$expr"));
        Assert.False(MongoQueryLanguageRenderer.IsQueryDialectRenderable(Count(op, threshold)));
    }

    [Fact]
    public void A_non_integer_count_threshold_routes_to_expr()
    {
        // C# permits `b.Posts.Count > 2.5` by promoting the int count. There is no index form for it.
        var rendered = RenderCount(MongoBinaryOperator.GreaterThan, 2.5).AsBsonDocument;

        Assert.True(rendered.Contains("$expr"));
        Assert.False(MongoQueryLanguageRenderer.IsQueryDialectRenderable(
            Count(MongoBinaryOperator.GreaterThan, 2.5)));
    }

    [Fact]
    public void A_parameterized_count_threshold_is_not_query_dialect_renderable()
    {
        // This is what makes a parameterized count nested inside $elemMatch decline with NO new guard: the
        // quantifier arm already gates its child on this classifier, and $expr inside $elemMatch is a hard
        // server error.
        var parameterized = new MongoBinaryExpression(
            MongoBinaryOperator.GreaterThan,
            new MongoSizeExpression("Posts", typeof(int), nullSafe: true),
            new MongoParameterExpression("__n", null));

        Assert.False(MongoQueryLanguageRenderer.IsQueryDialectRenderable(parameterized));
    }

    [Fact]
    public void An_admissible_count_comparison_is_query_dialect_renderable()
        => Assert.True(MongoQueryLanguageRenderer.IsQueryDialectRenderable(
            Count(MongoBinaryOperator.GreaterThan, 2)));

    [Fact]
    public void A_count_comparison_composes_inside_an_elem_match()
    {
        // The whole point of the constant tier: pure query dialect, so it is legal inside $elemMatch, where
        // $expr is a hard server error. The inner array path is ELEMENT-relative ("Comments"), as $elemMatch
        // requires.
        var pred = new MongoElemMatchExpression(
            "Posts",
            new MongoBinaryExpression(
                MongoBinaryOperator.GreaterThan,
                new MongoSizeExpression("Comments", typeof(int), nullSafe: true),
                new MongoConstantExpression(1, null)),
            negated: false);

        var rendered = new MongoQueryLanguageRenderer().Render(pred, new PlaceholderTable());

        Assert.Equal(
            BsonDocument.Parse("{ Posts: { $elemMatch: { 'Comments.1': { $exists: true } } } }"),
            rendered);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run:
```bash
dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj \
  -c "Debug EF10" --filter "FullyQualifiedName~MongoQueryLanguageRendererTests"
```
Expected: the eight index-form tests FAIL — they currently render `$expr` (the catch-all) instead of the
array-index document. The degenerate/parameterized tests should already pass (they assert the `$expr` route),
which is fine.

- [ ] **Step 3: Add the constant-tier renderer and threshold parser**

Add a new section to `MongoQueryLanguageRenderer.cs`, immediately after `RenderElemMatch`:

```csharp
    // ------------------------------------------------------------------
    // Array cardinality — the query-dialect array-index existence form
    // ------------------------------------------------------------------

    /// <summary>
    /// Renders a comparison between an array's element count and an INTEGER CONSTANT to the query dialect,
    /// or returns <see langword="null"/> when the comparison has no query-dialect form (a parameterized,
    /// non-integral, or degenerate threshold — all of which route to <c>$expr</c> instead).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The form is an array-index existence test: <c>{"path.k": {$exists: true}}</c> is true for exactly those
    /// documents whose array has MORE THAN <c>k</c> elements, and <c>$exists: false</c> for AT MOST <c>k</c>.
    /// Every operator is expressed by choosing <c>k</c>; <c>==</c> and <c>!=</c> combine two of them through
    /// the existing <see cref="CombineAnd"/> / <see cref="CombineOr"/> helpers.
    /// </para>
    /// <para>
    /// <b>Why not query-dialect <c>$size</c>:</b> it can only express an exact size, it cannot use an index,
    /// and — decisively — <c>{path: {$size: 0}}</c> does NOT match a document whose array is MISSING or
    /// explicitly <c>null</c>, where LINQ's <c>Count == 0</c> is <see langword="true"/> (EF materializes a
    /// missing embedded array as an empty list). The index form is correct for all three states for free,
    /// because none of them has an element at any index. This is the same reasoning that rejected
    /// <c>{path: {$ne: []}}</c> for bare <c>Any()</c>.
    /// </para>
    /// <para>
    /// <b>This method is the single definition of admissibility.</b>
    /// <see cref="IsQueryDialectRenderable"/> calls it rather than re-deriving the condition, so the
    /// classifier and the renderer cannot drift — which matters because the three-way
    /// negator/classifier/renderer contract (see <see cref="MongoExpressionNegator"/>) depends on them
    /// agreeing exactly.
    /// </para>
    /// <para>
    /// A rejected threshold needs no clamping and no decline: a tautology (<c>C &gt;= 0</c>), a contradiction
    /// (<c>C &lt; 0</c>), a non-integral threshold (<c>C &gt; 2.5</c>, which C# permits by promoting the
    /// count) and a parameterized one all fall through to <see cref="RenderAsExpr"/>, which renders every one
    /// of them correctly. Returning <see langword="null"/> here therefore loses only the index, never
    /// correctness.
    /// </para>
    /// </remarks>
    private static BsonDocument? TryRenderSizeComparison(MongoBinaryExpression binary)
    {
        if (binary.Left is not MongoSizeExpression size
            || binary.Right is not MongoConstantExpression { Value: { } rawThreshold }
            || !TryGetIntegerThreshold(rawThreshold, out var n))
        {
            return null;
        }

        // MoreThan(k) ⇔ count >= k + 1;  AtMost(k) ⇔ count <= k.
        return binary.Operator switch
        {
            MongoBinaryOperator.GreaterThan when n >= 0 => MoreThan(size.FieldName, n),
            MongoBinaryOperator.GreaterThanOrEqual when n >= 1 => MoreThan(size.FieldName, n - 1),
            MongoBinaryOperator.LessThan when n >= 1 => AtMost(size.FieldName, n - 1),
            MongoBinaryOperator.LessThanOrEqual when n >= 0 => AtMost(size.FieldName, n),
            // C == 0 needs only the upper bound; C == n (n >= 1) is "more than n-1 AND at most n".
            MongoBinaryOperator.Equal when n == 0 => AtMost(size.FieldName, 0),
            MongoBinaryOperator.Equal when n >= 1
                => CombineAnd(MoreThan(size.FieldName, n - 1), AtMost(size.FieldName, n)),
            // C != 0 needs only the lower bound; C != n (n >= 1) is "at most n-1 OR more than n".
            MongoBinaryOperator.NotEqual when n == 0 => MoreThan(size.FieldName, 0),
            MongoBinaryOperator.NotEqual when n >= 1
                => CombineOr(AtMost(size.FieldName, n - 1), MoreThan(size.FieldName, n)),
            _ => null
        };
    }

    private static BsonDocument MoreThan(string arrayPath, int index)
        => new($"{arrayPath}.{index}", new BsonDocument("$exists", true));

    private static BsonDocument AtMost(string arrayPath, int index)
        => new($"{arrayPath}.{index}", new BsonDocument("$exists", false));

    // An array index is a path SEGMENT, so only an integral, in-range threshold has an index form. A
    // floating-point threshold is rejected even when whole-valued (2.0) — the $expr tier renders it correctly,
    // and accepting it here would add a rounding decision for no coverage gain.
    private static bool TryGetIntegerThreshold(object raw, out int value)
    {
        switch (raw)
        {
            case int i: value = i; return true;
            case long l when l is >= int.MinValue and <= int.MaxValue: value = (int)l; return true;
            case short s: value = s; return true;
            case byte b: value = b; return true;
            case sbyte sb: value = sb; return true;
            case ushort us: value = us; return true;
            case uint ui when ui <= int.MaxValue: value = (int)ui; return true;
            default: value = 0; return false;
        }
    }
```

- [ ] **Step 4: Wire the dispatch and the classifier**

In `RenderNode` (`:74-89`), add immediately after the `IsQueryNativeComparison` arm:

```csharp
            MongoBinaryExpression sizeComparison
                when TryRenderSizeComparison(sizeComparison) is { } arrayIndexForm => arrayIndexForm,
```

In `IsQueryDialectRenderable` (`:339-362`), add immediately **before** the
`MongoBinaryExpression comparison => IsQueryNativeComparison(comparison)` arm (order matters — the general
comparison arm would otherwise claim the node first and answer `false`):

```csharp
            // An array-count comparison against an admissible integer constant. Calls the renderer rather than
            // re-deriving its condition, so the two cannot drift. A parameterized or degenerate threshold
            // answers false here, which is exactly what declines it inside $elemMatch.
            MongoBinaryExpression sizeComparison when TryRenderSizeComparison(sizeComparison) is not null
                => true,
```

- [ ] **Step 5: Run the tests to verify they pass**

Run:
```bash
dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj \
  -c "Debug EF10" --filter "FullyQualifiedName~MongoQueryLanguageRendererTests"
```
Expected: PASS, zero failures.

- [ ] **Step 6: Run the whole unit suite to catch cross-class flips**

Run:
```bash
dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj -c "Debug EF10"
```
Expected: PASS, zero failures. (A filtered run scoped to one class misses cross-class flips — the lesson from
the EF-347 computed-leaf slice.)

- [ ] **Step 7: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoQueryLanguageRenderer.cs \
        tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoQueryLanguageRendererTests.cs
git commit -m "EF-322: render an array-count comparison as an array-index existence test"
```

---

## Task 4: The negator — exact complement of a count comparison

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionNegator.cs` (add a case in
  `TryNegateCore`, and extend the class remarks)
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoExpressionNegatorTests.cs`

**Interfaces:**
- Consumes: `MongoSizeExpression` (Task 2), `MongoQueryLanguageRenderer.IsQueryDialectRenderable` (Task 3).
- Produces: `MongoExpressionNegator.TryNegate` now succeeds for a size-vs-admissible-constant comparison,
  returning the same comparison with the operator **inverted**.

- [ ] **Step 1: Write the failing tests**

Add to `MongoExpressionNegatorTests.cs`:

```csharp
    private static MongoBinaryExpression Count(MongoBinaryOperator op, int threshold)
        => new(op,
            new MongoSizeExpression("Posts", typeof(int), nullSafe: true),
            new MongoConstantExpression(threshold, null));

    // NOTE ON TEST SHAPE: `MongoBinaryOperator` is internal, and a public [Theory] method cannot expose an
    // internal type in its signature (CS0051) while the test class stays public. This file already solved that
    // — see the four `Relational_operators_are_not_wrapped_never_inverted_*` [Fact]s, each delegating to a
    // private helper. Follow that established idiom; do NOT try [Theory]/[InlineData] or a [MemberData]
    // returning `TheoryData<MongoBinaryOperator, …>` (same accessibility problem).

    // THE EXCEPTION TO THE RELATIONAL RULE. A count comparison renders as { "path.k": { $exists: … } }, and
    // $exists DOES partition the document set — every document either has path.k or does not. So inverting the
    // operator is the EXACT complement here, unlike a relational comparison on a scalar field, where
    // { $gt: 5 } and { $lte: 5 } both fail to match a missing field and inversion would silently mis-answer
    // All(). Same test, opposite answer, because the rendered form differs.
    private static void AssertCountComparisonIsInvertedNotWrapped(
        MongoBinaryOperator op, MongoBinaryOperator expected)
    {
        Assert.True(MongoExpressionNegator.TryNegate(Count(op, 2), out var negated));

        // A MongoBinaryExpression with the inverted operator — NOT a MongoUnaryExpression($not) wrap.
        var comparison = Assert.IsType<MongoBinaryExpression>(negated);
        Assert.Equal(expected, comparison.Operator);
        Assert.IsType<MongoSizeExpression>(comparison.Left);
    }

    [Fact]
    public void Count_comparison_inverts_GreaterThan_to_LessThanOrEqual()
        => AssertCountComparisonIsInvertedNotWrapped(
            MongoBinaryOperator.GreaterThan, MongoBinaryOperator.LessThanOrEqual);

    [Fact]
    public void Count_comparison_inverts_GreaterThanOrEqual_to_LessThan()
        => AssertCountComparisonIsInvertedNotWrapped(
            MongoBinaryOperator.GreaterThanOrEqual, MongoBinaryOperator.LessThan);

    [Fact]
    public void Count_comparison_inverts_LessThan_to_GreaterThanOrEqual()
        => AssertCountComparisonIsInvertedNotWrapped(
            MongoBinaryOperator.LessThan, MongoBinaryOperator.GreaterThanOrEqual);

    [Fact]
    public void Count_comparison_inverts_LessThanOrEqual_to_GreaterThan()
        => AssertCountComparisonIsInvertedNotWrapped(
            MongoBinaryOperator.LessThanOrEqual, MongoBinaryOperator.GreaterThan);

    [Fact]
    public void Count_comparison_inverts_Equal_to_NotEqual()
        => AssertCountComparisonIsInvertedNotWrapped(
            MongoBinaryOperator.Equal, MongoBinaryOperator.NotEqual);

    [Fact]
    public void Count_comparison_inverts_NotEqual_to_Equal()
        => AssertCountComparisonIsInvertedNotWrapped(
            MongoBinaryOperator.NotEqual, MongoBinaryOperator.Equal);

    [Fact]
    public void The_admitted_count_set_is_closed_under_inversion()
    {
        // The safety property that makes delegating the rule to the negator sound: every inverse of an
        // admissible count comparison is ITSELF admissible, so the negator can never hand the renderer a form
        // the classifier rejects. Written as one property test over the whole admitted set rather than a Fact
        // per row — the claim IS "for all of these", and a per-row failure message keeps it diagnosable.
        (MongoBinaryOperator Op, int Threshold)[] admitted =
        [
            (MongoBinaryOperator.GreaterThan, 0), (MongoBinaryOperator.GreaterThan, 5),
            (MongoBinaryOperator.GreaterThanOrEqual, 1), (MongoBinaryOperator.GreaterThanOrEqual, 5),
            (MongoBinaryOperator.LessThan, 1), (MongoBinaryOperator.LessThan, 5),
            (MongoBinaryOperator.LessThanOrEqual, 0), (MongoBinaryOperator.LessThanOrEqual, 5),
            (MongoBinaryOperator.Equal, 0), (MongoBinaryOperator.Equal, 5),
            (MongoBinaryOperator.NotEqual, 0), (MongoBinaryOperator.NotEqual, 5)
        ];

        foreach (var (op, threshold) in admitted)
        {
            var because = $"{op} vs {threshold}";
            var original = Count(op, threshold);
            Assert.True(MongoQueryLanguageRenderer.IsQueryDialectRenderable(original), because);

            Assert.True(MongoExpressionNegator.TryNegate(original, out var negated), because);
            Assert.True(MongoQueryLanguageRenderer.IsQueryDialectRenderable(negated), because);

            // Involution: negating twice returns the original operator.
            Assert.True(MongoExpressionNegator.TryNegate(negated, out var twice), because);
            Assert.Equal(op, Assert.IsType<MongoBinaryExpression>(twice).Operator);
        }
    }

    [Fact]
    public void A_parameterized_count_comparison_declines()
    {
        // The negator's entry gate is IsQueryDialectRenderable, and the $expr tier is not query dialect.
        // Inversion WOULD be exact there (both $expr operands are always numbers, thanks to $ifNull), so this
        // is an accepted coverage gap — !(Count > @param) falls back — not a correctness compromise.
        var parameterized = new MongoBinaryExpression(
            MongoBinaryOperator.GreaterThan,
            new MongoSizeExpression("Posts", typeof(int), nullSafe: true),
            new MongoParameterExpression("__n", null));

        Assert.False(MongoExpressionNegator.TryNegate(parameterized, out _));
    }

    [Fact]
    public void A_degenerate_count_comparison_declines()
    {
        Assert.False(MongoExpressionNegator.TryNegate(
            Count(MongoBinaryOperator.GreaterThanOrEqual, 0), out _));
    }

    [Fact]
    public void A_count_comparison_negates_inside_a_conjunction_via_de_morgan()
    {
        var conjunction = new MongoBinaryExpression(
            MongoBinaryOperator.AndAlso,
            Count(MongoBinaryOperator.GreaterThan, 2),
            Count(MongoBinaryOperator.LessThan, 9));

        Assert.True(MongoExpressionNegator.TryNegate(conjunction, out var negated));

        var or = Assert.IsType<MongoBinaryExpression>(negated);
        Assert.Equal(MongoBinaryOperator.OrElse, or.Operator);
        Assert.Equal(MongoBinaryOperator.LessThanOrEqual, Assert.IsType<MongoBinaryExpression>(or.Left).Operator);
        Assert.Equal(MongoBinaryOperator.GreaterThanOrEqual, Assert.IsType<MongoBinaryExpression>(or.Right).Operator);
    }
```

Drop the stray `Assert.IsType<MongoUnaryExpression>(negated, exactMatch: false);` line from the first test if the
project's xUnit version does not have that overload — its intent (assert the result is NOT a `$not` wrap) is
already carried by the `Assert.IsType<MongoBinaryExpression>` above it.

- [ ] **Step 2: Run the tests to verify they fail**

Run:
```bash
dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj \
  -c "Debug EF10" --filter "FullyQualifiedName~MongoExpressionNegatorTests"
```
Expected: the inversion, closure and De Morgan tests FAIL (`TryNegate` returns `false` — a size comparison
reaches `TryNegateCore`'s `default: return false`). The two decline tests should already pass.

- [ ] **Step 3: Add the size-comparison case**

In `MongoExpressionNegator.TryNegateCore`, add immediately after the existing
`case MongoBinaryExpression comparison when IsQueryNativeComparison(comparison)` block:

```csharp
            // AN ARRAY-COUNT COMPARISON IS INVERTED, NOT $not-WRAPPED — the documented exception to the
            // relational rule in the class remarks, and it is the SAME test with the opposite answer.
            //
            // A count comparison renders as { "path.k": { $exists: true|false } } (see
            // MongoQueryLanguageRenderer.TryRenderSizeComparison), and $exists DOES partition the document
            // set: every document either has path.k or does not. So for a fixed k the two forms are exact
            // complements and inverting the operator is exact. Contrast a relational comparison on a scalar
            // field, where NEITHER { $gt: 5 } nor { $lte: 5 } matches a missing field, so inverting would
            // report All == true where LINQ says false.
            //
            // Inverting is also safe with respect to the output-domain invariant: the admitted set is CLOSED
            // under inversion (C > n ↔ C <= n, C >= n ↔ C < n, C == n ↔ C != n each preserve the "every
            // required array index >= 0" condition), so the result is renderable whenever the input was —
            // and TryNegate's entry gate has already established that it was.
            case MongoBinaryExpression { Left: MongoSizeExpression } sizeComparison:
            {
                var inverted = sizeComparison.Operator switch
                {
                    MongoBinaryOperator.GreaterThan => MongoBinaryOperator.LessThanOrEqual,
                    MongoBinaryOperator.GreaterThanOrEqual => MongoBinaryOperator.LessThan,
                    MongoBinaryOperator.LessThan => MongoBinaryOperator.GreaterThanOrEqual,
                    MongoBinaryOperator.LessThanOrEqual => MongoBinaryOperator.GreaterThan,
                    MongoBinaryOperator.Equal => MongoBinaryOperator.NotEqual,
                    MongoBinaryOperator.NotEqual => MongoBinaryOperator.Equal,
                    // An arithmetic operator is not a predicate; nothing to complement.
                    _ => (MongoBinaryOperator?)null
                };

                if (inverted is null)
                    return false;

                negated = new MongoBinaryExpression(
                    inverted.Value, sizeComparison.Left, sizeComparison.Right);
                return true;
            }
```

- [ ] **Step 4: Extend the class remarks**

In `MongoExpressionNegator`'s `<remarks>`, immediately after the existing "Why relational operators are
`$not`-wrapped but `$eq`/`$ne` are inverted" paragraph, add:

```csharp
/// <para>
/// <b>The one documented exception — an ARRAY-COUNT comparison is inverted.</b> The rule above is not "relational
/// operators are always wrapped"; it is "ask whether the RENDERED pair partitions". An array-count comparison
/// (<c>MongoSizeExpression</c> on the left) renders as <c>{"path.k": {$exists: true|false}}</c>, and
/// <c>$exists</c> DOES partition — every document either has <c>path.k</c> or does not — so inverting the
/// operator is the exact complement for that family, and the admitted set is closed under inversion. Getting
/// this backwards in either direction matters: wrapping where inversion is exact merely loses an index, but
/// inverting where it is not exact returns wrong rows.
/// </para>
```

- [ ] **Step 5: Run the tests to verify they pass**

Run:
```bash
dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj -c "Debug EF10"
```
Expected: PASS, zero failures across the whole unit suite.

- [ ] **Step 6: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionNegator.cs \
        tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoExpressionNegatorTests.cs
git commit -m "EF-322: negate an array-count comparison by inverting the operator"
```

---

## Task 5: Unify bare `Any()` with `Count >= 1`

This task changes **shipped** behavior representation. The bar is byte-identical emitted MQL, and the existing
`Any`/`All` suites are the regression net.

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoElemMatchExpression.cs`
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.cs:311-312`
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoQueryLanguageRenderer.cs`
  (`RenderElemMatch` at `:305-317`, `IsQueryDialectRenderable` at `:359-360`)
- Test: `MongoQueryLanguageRendererTests.cs:565-584`, `:770`; `MongoExpressionNegatorTests.cs:184`, `:312`

**Interfaces:**
- Consumes: Task 3's constant tier and Task 4's inversion.
- Produces: `MongoElemMatchExpression(string arrayPath, MongoExpression elementPredicate, bool negated)` —
  `elementPredicate` is now **non-nullable**. Bare `Any()` no longer constructs this node at all.

- [ ] **Step 1: Write the failing tests**

Replace the two bare-`Any()` renderer tests (`MongoQueryLanguageRendererTests.cs:564-584`) with tests over the
new representation, keeping the SAME expected MQL:

```csharp
    [Fact]
    public void Renders_bare_Any_as_array_index_exists()
    {
        // Bare Any() IS "Count >= 1" and is represented as exactly that, so the two cannot render differently.
        // { "Posts.0": { $exists: true } } is index-usable AND correct for an empty array, a MISSING field, and
        // an explicitly-null one ({ Posts: { $ne: [] } } would wrongly match the last two).
        var pred = new MongoBinaryExpression(
            MongoBinaryOperator.GreaterThanOrEqual,
            new MongoSizeExpression("Posts", typeof(int), nullSafe: true),
            new MongoConstantExpression(1, null));

        var rendered = new MongoQueryLanguageRenderer().Render(pred, new PlaceholderTable());

        Assert.Equal(BsonDocument.Parse("{ 'Posts.0': { $exists: true } }"), rendered);
    }

    [Fact]
    public void Renders_negated_bare_Any_as_array_index_not_exists()
    {
        // !Any() needs no dedicated handling: the negator inverts >= to <, giving Count < 1.
        var bareAny = new MongoBinaryExpression(
            MongoBinaryOperator.GreaterThanOrEqual,
            new MongoSizeExpression("Posts", typeof(int), nullSafe: true),
            new MongoConstantExpression(1, null));

        Assert.True(MongoExpressionNegator.TryNegate(bareAny, out var negated));

        var rendered = new MongoQueryLanguageRenderer().Render(negated, new PlaceholderTable());

        Assert.Equal(BsonDocument.Parse("{ 'Posts.0': { $exists: false } }"), rendered);
    }
```

Update the two negator-test sites (`MongoExpressionNegatorTests.cs:184`, `:312`) that construct
`new MongoElemMatchExpression("Comments", elementPredicate: null, negated: false)`: replace each with the
`Count >= 1` shape above (path `"Comments"`). Update `MongoQueryLanguageRendererTests.cs:770` the same way.

Read each of those five call sites before editing and preserve the surrounding assertion's intent — for the
negator's involution/renderability sweep at `:312`, the new node belongs in the same collection of "supported
set" members.

- [ ] **Step 2: Run the tests to verify they fail**

Run:
```bash
dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj \
  -c "Debug EF10" --filter "FullyQualifiedName~MongoQueryLanguageRendererTests|FullyQualifiedName~MongoExpressionNegatorTests"
```
Expected: PASS already for the renderer tests (Tasks 3-4 made the new representation work) — that is the point:
the new representation produces identical MQL. The tests still needed rewriting because the OLD construction is
about to become uncompilable.

- [ ] **Step 3: Make `ElementPredicate` non-nullable**

In `MongoElemMatchExpression.cs`, change the constructor parameter and the property, and update the doc comments
that describe the bare-`Any()` case:

```csharp
/// <summary>
/// Represents an existential quantifier over an embedded (owned) array field: at least one element of
/// <see cref="ArrayPath"/> satisfies <see cref="ElementPredicate"/>, optionally negated. Renders to
/// <c>$elemMatch</c>.
/// </summary>
```

```csharp
    /// <param name="elementPredicate">
    /// The predicate each candidate element is tested against, with ELEMENT-RELATIVE field paths.
    /// </param>
    public MongoElemMatchExpression(string arrayPath, MongoExpression elementPredicate, bool negated)
```

```csharp
    /// <summary>
    /// The predicate each candidate element is tested against, with ELEMENT-RELATIVE field paths.
    /// </summary>
    public MongoExpression ElementPredicate { get; }
```

Add a remarks paragraph recording where the bare form went:

```csharp
/// <para>
/// A BARE <c>Any()</c> is deliberately NOT represented by this node: it is exactly <c>Count &gt;= 1</c>, so it
/// is translated as an array-count comparison over <see cref="MongoSizeExpression"/> and renders through the
/// same array-index existence form (<c>{"path.0": {$exists: true}}</c>). Keeping one representation for array
/// cardinality is what makes <c>Any()</c> and <c>Count &gt;= 1</c> structurally identical rather than
/// coincidentally equal, and it is why this node's predicate is non-nullable.
/// </para>
```

- [ ] **Step 4: Translate bare `Any()` as a count comparison**

In `MongoExpressionTranslator.TranslateNode`'s quantifier arm, replace lines 311-312:

```csharp
                if (elementLambda is null)
                {
                    // A bare Any() IS "at least one element", i.e. Count >= 1 — the same predicate, rendered by
                    // the same array-index existence form ({ "path.0": { $exists: true } }). Representing it as
                    // a count comparison keeps ONE representation for array cardinality, and !Any() then falls
                    // out of the negator's inversion (Count < 1) with no dedicated code.
                    return new MongoBinaryExpression(
                        MongoBinaryOperator.GreaterThanOrEqual,
                        new MongoSizeExpression(arrayPath, typeof(int), nullSafe: true),
                        new MongoConstantExpression(1, forSerialization: null));
                }
```

- [ ] **Step 5: Drop the null branches from the renderer and classifier**

In `RenderElemMatch`, delete the `if (elemMatch.ElementPredicate is null)` early return and the second
`<para>` of its doc comment (the one describing the bare form), keeping the rest intact.

In `IsQueryDialectRenderable`, delete the `MongoElemMatchExpression { ElementPredicate: null } => true,` arm so
only this remains:

```csharp
            MongoElemMatchExpression elemMatch => IsQueryDialectRenderable(elemMatch.ElementPredicate),
```

- [ ] **Step 6: Build and run the full unit suite**

Run:
```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"
dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj -c "Debug EF10"
```
Expected: build succeeds (any remaining compile error points at a bare-`Any()` construction site the step-1 sweep
missed); unit suite PASSES with zero failures.

- [ ] **Step 7: Prove the byte-identity bar against the shipped quantifier suites**

Run:
```bash
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --filter "FullyQualifiedName~NativeOwnedCollectionPredicateTests|FullyQualifiedName~NativeOwnedCollectionAllTests"
```
Expected: PASS, zero failures, **with no test edits**. These suites include MQL assertions on the bare-`Any()`
form and the full differential matrix — they are the regression net for this refactor. If any test here needs
editing, the unification changed behavior and must be corrected, not re-baselined.

- [ ] **Step 8: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoElemMatchExpression.cs \
        src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.cs \
        src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoQueryLanguageRenderer.cs \
        tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoQueryLanguageRendererTests.cs \
        tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoExpressionNegatorTests.cs
git commit -m "EF-322: unify bare Any() with Count >= 1 (one representation for array cardinality)"
```

---

## Task 6: Translator recognition — the eligibility change

**This is the task that changes what goes native.** It MUST run the functional suite, not only unit tests — a
task that was 635/635 green on units still shipped a red functional test in the `All` slice.

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.cs`
  (`TranslateNode`'s `Not` arm at `:243-268`, `TranslateComparison`'s general branch at `:436-451`,
  `TranslateOperand` at `:510-543`, plus a new `TryMatchCountExpression` helper)
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoExpressionTranslatorTests.cs`

**Interfaces:**
- Consumes: `MongoSizeExpression` (Task 2), the constant tier (Task 3), the negator's inversion (Task 4).
- Produces: `Where(b => b.Posts.Count > 2)` and the other shapes in §2 of the spec now translate. The count is a
  `MongoExpression` **operand**, so a count-vs-count comparison and arithmetic over a count also translate, via
  the `$expr` tier.

- [ ] **Step 1: Write the failing tests**

Add to `MongoExpressionTranslatorTests.cs`. **No new fixtures are needed** — reuse what the file already has:
the existing `TryTranslateBlogPredicate(Expression<Func<OwnedBlog, bool>>)` helper (`:1446`, which returns
`MongoExpression?` — `null` on decline), the `OwnedBlog` model built by `GetOwnedBlogEntityType()` (`:142`, which
already configures `OwnsMany(b => b.Posts)`, `OwnsOne(b => b.Address).OwnsMany(a => a.Notes)`, and a primitive
`List<string> Tags`), and the `Order` fixture, which already declares a mapped `int Count` property (`:73`) —
exactly what the name-collision test needs.

```csharp
    [Fact]
    public void Translates_owned_collection_Count_greater_than_constant()
    {
        var translated = TryTranslateBlogPredicate(b => b.Posts.Count > 2);

        var comparison = Assert.IsType<MongoBinaryExpression>(translated);
        Assert.Equal(MongoBinaryOperator.GreaterThan, comparison.Operator);
        var size = Assert.IsType<MongoSizeExpression>(comparison.Left);
        Assert.Equal("Posts", size.FieldName);
        Assert.True(size.NullSafe);
    }

    [Fact]
    public void Translates_owned_collection_Count_call_form()
        => Assert.NotNull(TryTranslateBlogPredicate(b => b.Posts.Count() > 2));

    [Fact]
    public void Translates_owned_collection_LongCount_call_form()
        => Assert.NotNull(TryTranslateBlogPredicate(b => b.Posts.LongCount() > 2L));

    [Fact]
    public void Normalizes_a_reversed_count_comparison_so_the_size_node_is_on_the_left()
    {
        // The query renderer's array-index form recognizes only size-on-the-left, so the translator mirrors.
        var comparison = Assert.IsType<MongoBinaryExpression>(
            TryTranslateBlogPredicate(b => 2 < b.Posts.Count));

        Assert.IsType<MongoSizeExpression>(comparison.Left);
        Assert.Equal(MongoBinaryOperator.GreaterThan, comparison.Operator);
    }

    [Fact]
    public void Translates_a_count_reached_through_an_owned_reference_hop()
    {
        var comparison = Assert.IsType<MongoBinaryExpression>(
            TryTranslateBlogPredicate(b => b.Address.Notes.Count > 1));

        Assert.Equal("Address.Notes", Assert.IsType<MongoSizeExpression>(comparison.Left).FieldName);
    }

    [Fact]
    public void Negates_a_count_comparison_by_inverting_rather_than_wrapping()
    {
        var comparison = Assert.IsType<MongoBinaryExpression>(
            TryTranslateBlogPredicate(b => !(b.Posts.Count > 2)));

        Assert.Equal(MongoBinaryOperator.LessThanOrEqual, comparison.Operator);
    }

    [Fact]
    public void A_count_inside_a_quantifier_resolves_element_relatively()
    {
        // The element-scoped child translator resolves the inner array relative to the ELEMENT ("Comments"),
        // not the root ("Posts.Comments") — which is what the enclosing $elemMatch expects.
        var elemMatch = Assert.IsType<MongoElemMatchExpression>(
            TryTranslateBlogPredicate(b => b.Posts.Any(p => p.Comments.Count > 1)));

        var inner = Assert.IsType<MongoBinaryExpression>(elemMatch.ElementPredicate);
        Assert.Equal("Comments", Assert.IsType<MongoSizeExpression>(inner.Left).FieldName);
    }

    [Fact]
    public void A_mapped_scalar_property_named_Count_is_not_mistaken_for_a_cardinality_expression()
    {
        // A mapped scalar property named `Count` must still resolve as a FIELD. NOTE (measured in Task 6 fix
        // rounds 1-2): this does NOT pin the call-site ordering — `o.Count > 2` is resolved by
        // TranslateComparison's first branch and never reaches TranslateOperand, and even on the
        // TranslateOperand path, moving count recognition ahead of TryResolveMember turns no test red. The real
        // protection is structural, in TryResolveOwnedCollectionPath (see Task 9's doc note).
        var translator = NewTranslator(GetEntityType<Order>());

        Assert.True(translator.TryTranslate(
            PredicateBody<Order>(o => o.Count > 2), out var translated));

        var comparison = Assert.IsType<MongoBinaryExpression>(translated);
        Assert.Equal("Count", Assert.IsType<MongoFieldExpression>(comparison.Left).ElementName);
    }

    [Fact]
    public void A_predicated_Count_declines()
        // Count(pred) has no array-index form and needs $expr over $filter — a separate slice.
        => Assert.Null(TryTranslateBlogPredicate(b => b.Posts.Count(p => p.Rank > 1) > 2));

    [Fact]
    public void A_primitive_collection_Count_declines()
        // TryResolveOwnedCollectionPath requires an embedded collection NAVIGATION; Tags is a property.
        => Assert.Null(TryTranslateBlogPredicate(b => b.Tags.Count > 2));
```

- [ ] **Step 2: Run the tests to verify they fail**

Run:
```bash
dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj \
  -c "Debug EF10" --filter "FullyQualifiedName~MongoExpressionTranslatorTests"
```
Expected: the six positive tests FAIL (`TryTranslate` returns `false`); the three decline tests PASS already.

- [ ] **Step 3: Add the count matcher**

Add to `MongoExpressionTranslator.cs`, next to `TryMatchQuantifierMethod`. Note `PropertyInfo` needs
`using System.Reflection;` — check the file's existing usings first.

```csharp
    /// <summary>
    /// Matches an element-count expression over a collection — the <c>Count</c> property on the collection
    /// itself, or a PARAMETERLESS <c>Count()</c>/<c>LongCount()</c> call — and yields the collection SOURCE
    /// with any <c>AsQueryable()</c> wrapper stripped.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Both shapes are live, for different callers</b> (Task 1 spike, measured on EF8/EF9/EF10): EF's own
    /// preprocessing normalizes a <c>.Count</c> PROPERTY access into the method-call form before the native
    /// translator ever sees it, so every real query arrives as
    /// <c>Queryable.Count(EF.Property(b, "Posts").AsQueryable())</c> — the <c>Count</c> property and
    /// <c>Count()</c> call are byte-identical trees by then. The <see cref="MemberExpression"/> arm is
    /// nonetheless required, because a HAND-BUILT expression tree (a unit test calling
    /// <c>TryTranslate</c> on a raw C# lambda, which bypasses EF's pipeline entirely) does carry a real
    /// <c>List&lt;T&gt;.Count</c> member access. This mirrors why
    /// <see cref="TryMatchQuantifierMethod"/> accepts the <see cref="Enumerable"/> spelling as well as
    /// <see cref="Queryable"/>: so a hand-built tree translates identically to an EF-produced one.
    /// </para>
    /// <para>
    /// A PREDICATED <c>Count(source, predicate)</c> is deliberately NOT matched: it has no array-index form and
    /// would need <c>$expr</c> over <c>$filter</c>, which is a separate slice. Rejecting it here keeps it on the
    /// driver-LINQ path, which translates it correctly.
    /// </para>
    /// <para>
    /// This matcher is PURE and must stay that way: the spike observed
    /// <see cref="TranslateComparison"/> being entered twice per query, so any recognition hung off it has to be
    /// idempotent.
    /// </para>
    /// <para>
    /// <b>This matcher is name-based and therefore not sufficient on its own.</b> An entity may legitimately
    /// have a mapped scalar property called <c>Count</c>. What makes that safe is the CALL SITE ordering in
    /// <see cref="TranslateOperand"/>: count recognition runs only AFTER
    /// <see cref="TryResolveMember"/> has failed, so a real mapped <c>Count</c> field always wins. Do not move
    /// this recognition ahead of that call.
    /// </para>
    /// </remarks>
    private static bool TryMatchCountExpression(Expression node, [NotNullWhen(true)] out Expression? source)
    {
        source = null;

        // Only int/long-valued nodes can be a count; this cheaply excludes unrelated members named "Count".
        if (node.Type != typeof(int) && node.Type != typeof(long))
            return false;

        switch (node)
        {
            // b.Posts.Count — the ICollection<T>/List<T> Count property. Reached only by a HAND-BUILT tree
            // (see the remarks); EF itself normalizes this into the call form below.
            case MemberExpression { Member: PropertyInfo { Name: nameof(List<int>.Count) }, Expression: { } receiver }:
                source = UnwrapAsQueryable(receiver);
                return true;

            // Enumerable/Queryable.Count(source) / LongCount(source) — the parameterless overloads only. This is
            // the shape EVERY real EF query arrives in, for both the .Count property and the .Count() call.
            case MethodCallExpression { Arguments.Count: 1 } call
                when call.Method.Name is nameof(Enumerable.Count) or nameof(Enumerable.LongCount)
                     && (call.Method.DeclaringType == typeof(Enumerable)
                         || call.Method.DeclaringType == typeof(Queryable)):
                source = UnwrapAsQueryable(call.Arguments[0]);
                return true;

            default:
                return false;
        }
    }
```

- [ ] **Step 4: Recognize a count as an operand**

In `TranslateOperand`, insert immediately **after** the `TryResolveMember` block (`:521-522`) and before the
arithmetic block:

```csharp
        // An OWNED-collection element count — b.Posts.Count / .Count() / .LongCount(). The renderer decides the
        // dialect: a comparison against an admissible integer constant becomes an array-index existence test,
        // anything else routes to $expr with a null-safe $size (see MongoQueryLanguageRenderer).
        //
        // Ordering note (CORRECTED in Task 6 fix round 2 — the original claim that this ordering was
        // load-bearing was measured false): running after TryResolveMember is defence-in-depth and an efficiency
        // guard, NOT the safety property. The actual protection is structural, in TryResolveOwnedCollectionPath:
        // it requires a chain rooted at the query parameter with at least one hop, every non-final hop to be an
        // embedded single reference, and the FINAL hop to be an embedded collection NAVIGATION. A mapped
        // scalar's receiver is an entity, never a collection, so a name collision cannot resolve. It also
        // declines outright in two-scope mode, so a cross-scope count stays out of scope for free.
        if (TryMatchCountExpression(node, out var countSource)
            && TryResolveOwnedCollectionPath(countSource, out var arrayPath, out _))
        {
            return new MongoSizeExpression(arrayPath, node.Type, nullSafe: true);
        }
```

- [ ] **Step 5: Normalize a reversed count comparison**

In `TranslateComparison`'s general branch, replace the final `return` (`:450`) with:

```csharp
        // Normalize a count-vs-value comparison so the size node is on the LEFT, mirroring the operator: the
        // query renderer's array-index form recognizes only that orientation. Field-to-field and arithmetic
        // comparisons are deliberately NOT mirrored — they render inside $expr, where operand order matters.
        if (rightOperand is MongoSizeExpression
            && leftOperand is MongoConstantExpression or MongoParameterExpression)
        {
            var mirroredOp = MapComparisonOperator(Mirror(be.NodeType));
            if (mirroredOp is null)
                return null;

            return new MongoBinaryExpression(mirroredOp.Value, rightOperand, leftOperand);
        }

        return new MongoBinaryExpression(generalOp.Value, leftOperand, rightOperand);
```

- [x] **Step 6: Delegate `Not` over a count comparison to the negator — MOVED TO TASK 5, ALREADY DONE**

**Do not implement this step; it is already in the tree.** It was originally planned here, but Task 5's
bare-`Any()` unification cannot pass its own acceptance criterion without it: once `Any()` becomes `Count >= 1`,
`!Any()` needs this routing or it falls through to a generic `MongoUnaryExpression(Not, …)` that `RenderUnary`
throws on, regressing shipped behavior (`NativeOwnedCollectionPredicateTests
.Negated_owned_collection_Any_goes_native`). The two tasks' requirements were mutually unsatisfiable in the
planned order, so the routing was pulled forward into Task 5 fix round 1 by human ruling.

For reference, the code that landed in Task 5 — in `TranslateNode`'s `Not` arm, after the
`MongoElemMatchExpression` flip and before the nullable-bool guard:

```csharp
                if (operand is MongoBinaryExpression { Left: MongoSizeExpression })
                    return MongoExpressionNegator.TryNegate(operand, out var countComplement)
                        ? countComplement
                        : null;
```

Task 6 still OWNS the test that pins the user-facing behavior
(`Negates_a_count_comparison_by_inverting_rather_than_wrapping`, step 1) — keep it. It should pass immediately
rather than starting red, which is the expected consequence of the move.

- [ ] **Step 7: Confirm the last walker needs no change**

Read `MongoExpressionTranslator.AllFieldsDefaultSerialized` (`:197-203`). It is the seventh entry on the design
doc's walker checklist and the only one that needs **no** edit: its `_ => true` catch-all already passes a
`MongoSizeExpression` through, which is correct — a count carries no property serialization, so there is no
converter or `BsonRepresentation` to diverge on. Add a one-line comment recording that so a future reader does
not mistake the omission for an oversight:

```csharp
            // A MongoSizeExpression falls into the catch-all below, deliberately: an array COUNT carries no
            // property serialization, so there is no converter / BsonRepresentation for it to diverge on.
```

- [ ] **Step 8: Run the unit tests to verify they pass**

Run:
```bash
dotnet test tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj -c "Debug EF10"
```
Expected: PASS, zero failures across the whole unit suite.

- [ ] **Step 9: Run the FULL functional suite — this task changes eligibility**

Run:
```bash
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10"
```
Expected: zero failures. A previously-declining shape going native can flip a test that asserted the decline —
if one flips, verify the new behavior is CORRECT (compare `Native` against `DriverLinq` values) before updating
the assertion. Never delete a `Throws` assertion without that check.

- [ ] **Step 10: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/NativeTranslation/MongoExpressionTranslator.cs \
        tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/MongoExpressionTranslatorTests.cs
git commit -m "EF-322: owned-collection .Count in a predicate goes native"
```

---

## Task 7: Functional tests — natives, MQL, declines

**Files:**
- Create: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeOwnedCollectionCountTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 2-6.
- Produces: the `Blog`/`Post`/`Comment`/`Home`/`Note` model, row builders, seeds and the four assertion helpers
  that Task 8's differential matrix also uses.

- [ ] **Step 1: Create the test class scaffolding**

Copy the scaffolding from `NativeOwnedCollectionAllTests.cs:1-130` **verbatim** — the license header, the usings,
`[XUnitCollection("QueryTests")]`, `CreateContext`, `CreateContextWithLogging`, `AssertMql`,
`UniqueCollectionName`, the `Blog`/`Post`/`Comment`/`Home`/`Note` model classes and `BlogModel` — changing only
the class name to `NativeOwnedCollectionCountTests` and the summary to:

```csharp
/// <summary>
/// EF-322: an element COUNT over an OWNED (embedded) collection navigation, compared against a value, translates
/// natively — as an array-index existence test for an integer-constant threshold, and as $expr over a null-safe
/// $size otherwise. Each admitted shape asserts a NativeOnly routing proof; each excluded shape asserts a clean
/// decline.
/// </summary>
```

Then add count-specific row builders and seeds:

```csharp
    // Rows differ only in ARRAY LENGTH (0-3) plus the three "no elements" states, because that is the entire
    // input space a cardinality predicate is sensitive to. Element FIELD values are irrelevant here — unlike
    // the Any/All slices, where the element predicate was the thing under test.
    private static BsonDocument LenRow(string title, int length)
    {
        var posts = new BsonArray();
        for (var i = 0; i < length; i++)
            posts.Add(PostDoc(rank: i, heading: "h" + i));
        return Row(title, posts);
    }

    private static BsonDocument PostDoc(int? rank, string? heading)
        => new()
        {
            { "Rank", rank.HasValue ? rank.Value : BsonNull.Value },
            { "Heading", heading is null ? BsonNull.Value : heading },
            { "Other", 0 }, { "Title", "p" }, { "Comments", new BsonArray() }
        };

    private static BsonDocument PostWithComments(string heading, int commentCount)
    {
        var comments = new BsonArray();
        for (var i = 0; i < commentCount; i++)
            comments.Add(new BsonDocument { { "Age", i } });
        return new BsonDocument
        {
            { "Rank", 0 }, { "Heading", heading }, { "Other", 0 }, { "Title", "p" }, { "Comments", comments }
        };
    }

    // Home/Tags are always seeded present-but-empty: both are separate required properties on Blog, and a
    // document missing them fails materialization with an unrelated error the moment a predicate returns the
    // row as a full Blog.
    private static BsonDocument Row(string title, BsonValue? posts)
    {
        var doc = new BsonDocument
        {
            { "_id", ObjectId.GenerateNewId() }, { "Title", title },
            { "Home", new BsonDocument { { "Notes", new BsonArray() } } },
            { "Tags", new BsonArray() }
        };
        if (posts is not null)
            doc.Add("Posts", posts);
        return doc;
    }

    private static BsonDocument RowWithNotes(string title, int noteCount)
    {
        var notes = new BsonArray();
        for (var i = 0; i < noteCount; i++)
            notes.Add(new BsonDocument { { "Length", i } });
        return new BsonDocument
        {
            { "_id", ObjectId.GenerateNewId() }, { "Title", title },
            { "Home", new BsonDocument { { "Notes", notes } } },
            { "Posts", new BsonArray() }, { "Tags", new BsonArray() }
        };
    }

    private static BsonDocument RowWithTags(string title, params string[] tags)
        => new()
        {
            { "_id", ObjectId.GenerateNewId() }, { "Title", title },
            { "Home", new BsonDocument { { "Notes", new BsonArray() } } },
            { "Posts", new BsonArray() },
            { "Tags", new BsonArray(tags) }
        };

    private IMongoCollection<Blog> Seed(string name, params BsonDocument[] rows)
    {
        var coll = database.MongoDatabase.GetCollection<BsonDocument>(UniqueCollectionName(name));
        coll.InsertMany(rows);
        return database.MongoDatabase.GetCollection<Blog>(coll.CollectionNamespace.CollectionName);
    }

    // Every array LENGTH state a cardinality predicate can distinguish, plus the three "no elements" states.
    // "missing" and "null" are the rows a query-dialect $size form would get WRONG (neither matches $size: 0,
    // but LINQ's Count is 0 for both).
    private IMongoCollection<Blog> SeedLengths(string name)
        => Seed(name,
            LenRow("len0", 0), LenRow("len1", 1), LenRow("len2", 2), LenRow("len3", 3),
            Row("missing", posts: null), Row("null", BsonNull.Value));

    // Rows whose Posts is a real, non-null ARRAY. The driver's own count translation renders $size under $expr
    // and ABORTS the aggregate on a missing or explicitly-null array, so the DriverLinq oracle leg can only run
    // against these rows — the same two-seed split the Any/All slices established.
    private IMongoCollection<Blog> SeedWellFormed(string name)
        => Seed(name, LenRow("len0", 0), LenRow("len1", 1), LenRow("len2", 2), LenRow("len3", 3));
```

Finally copy the four assertion helpers from `NativeOwnedCollectionAllTests.cs:262-317` verbatim:
`AssertNativeAndParity`, `AssertDeclinesCleanly`, `AssertNativeOnlyMatches`.

- [ ] **Step 2: Write the native-routing tests**

```csharp
    [Theory]
    [InlineData(0, new[] { "len1", "len2", "len3" })]
    [InlineData(1, new[] { "len2", "len3" })]
    [InlineData(2, new[] { "len3" })]
    [InlineData(3, new string[0])]
    public void Count_greater_than_goes_native(int threshold, string[] expected)
    {
        var collection = SeedLengths($"gt{threshold}");
        var titles = AssertNativeOnlyMatches(collection, q => q.Where(b => b.Posts.Count > threshold));
        Assert.Equal(expected, titles);
    }

    [Fact]
    public void Count_equal_zero_matches_empty_missing_and_null_arrays()
    {
        // The decisive correctness row: LINQ's Count == 0 is TRUE for an empty, a MISSING and an explicitly-null
        // array (EF materializes a missing embedded array as an empty list). A query-dialect { $size: 0 } form
        // would match only "len0" — which is why $size was rejected as the primary rendering.
        var collection = SeedLengths(nameof(Count_equal_zero_matches_empty_missing_and_null_arrays));

        var titles = AssertNativeOnlyMatches(collection, q => q.Where(b => b.Posts.Count == 0));

        Assert.Equal(new[] { "len0", "missing", "null" }, titles);
    }

    [Fact]
    public void Count_equal_nonzero_goes_native()
    {
        var collection = SeedLengths(nameof(Count_equal_nonzero_goes_native));
        var titles = AssertNativeOnlyMatches(collection, q => q.Where(b => b.Posts.Count == 2));
        Assert.Equal(new[] { "len2" }, titles);
    }

    [Fact]
    public void Count_not_equal_goes_native()
    {
        var collection = SeedLengths(nameof(Count_not_equal_goes_native));
        var titles = AssertNativeOnlyMatches(collection, q => q.Where(b => b.Posts.Count != 2));
        Assert.Equal(new[] { "len0", "len1", "len3", "missing", "null" }, titles);
    }

    [Fact]
    public void Count_less_than_goes_native()
    {
        var collection = SeedLengths(nameof(Count_less_than_goes_native));
        var titles = AssertNativeOnlyMatches(collection, q => q.Where(b => b.Posts.Count < 2));
        Assert.Equal(new[] { "len0", "len1", "missing", "null" }, titles);
    }

    [Fact]
    public void Count_less_than_or_equal_goes_native()
    {
        var collection = SeedLengths(nameof(Count_less_than_or_equal_goes_native));
        var titles = AssertNativeOnlyMatches(collection, q => q.Where(b => b.Posts.Count <= 1));
        Assert.Equal(new[] { "len0", "len1", "missing", "null" }, titles);
    }

    [Fact]
    public void Count_greater_than_or_equal_goes_native()
    {
        var collection = SeedLengths(nameof(Count_greater_than_or_equal_goes_native));
        var titles = AssertNativeOnlyMatches(collection, q => q.Where(b => b.Posts.Count >= 2));
        Assert.Equal(new[] { "len2", "len3" }, titles);
    }

    [Fact]
    public void Count_call_form_and_LongCount_go_native()
    {
        var collection = SeedLengths(nameof(Count_call_form_and_LongCount_go_native));

        Assert.Equal(new[] { "len2", "len3" },
            AssertNativeOnlyMatches(collection, q => q.Where(b => b.Posts.Count() > 1)));
        Assert.Equal(new[] { "len2", "len3" },
            AssertNativeOnlyMatches(collection, q => q.Where(b => b.Posts.LongCount() > 1L)));
    }

    [Fact]
    public void Reversed_operand_order_goes_native()
    {
        var collection = SeedLengths(nameof(Reversed_operand_order_goes_native));
        var titles = AssertNativeOnlyMatches(collection, q => q.Where(b => 1 < b.Posts.Count));
        Assert.Equal(new[] { "len2", "len3" }, titles);
    }

    [Fact]
    public void A_parameterized_threshold_goes_native_via_the_expr_tier()
    {
        var collection = SeedLengths(nameof(A_parameterized_threshold_goes_native_via_the_expr_tier));
        var threshold = 1;

        var titles = AssertNativeOnlyMatches(collection, q => q.Where(b => b.Posts.Count > threshold));

        // $ifNull is what keeps the missing/null rows from aborting the aggregate — without it this query
        // throws instead of returning rows.
        Assert.Equal(new[] { "len2", "len3" }, titles);
    }

    [Fact]
    public void Negated_count_comparison_goes_native()
    {
        var collection = SeedLengths(nameof(Negated_count_comparison_goes_native));
        var titles = AssertNativeOnlyMatches(collection, q => q.Where(b => !(b.Posts.Count > 1)));
        Assert.Equal(new[] { "len0", "len1", "missing", "null" }, titles);
    }

    [Fact]
    public void Count_through_an_owned_reference_hop_goes_native()
    {
        var collection = Seed(nameof(Count_through_an_owned_reference_hop_goes_native),
            RowWithNotes("notes0", 0), RowWithNotes("notes2", 2));

        var titles = AssertNativeOnlyMatches(collection, q => q.Where(b => b.Home.Notes.Count > 1));

        Assert.Equal(new[] { "notes2" }, titles);
    }

    [Fact]
    public void Count_inside_a_quantifier_goes_native()
    {
        // The constant tier is pure query dialect, so it is legal inside $elemMatch — where $expr is a hard
        // server error. This is the shape that would fail at EXECUTION time if the tier choice were wrong.
        var collection = Seed(nameof(Count_inside_a_quantifier_goes_native),
            Row("few", new BsonArray { PostWithComments("a", 1) }),
            Row("many", new BsonArray { PostWithComments("a", 3) }));

        var titles = AssertNativeOnlyMatches(
            collection, q => q.Where(b => b.Posts.Any(p => p.Comments.Count > 2)));

        Assert.Equal(new[] { "many" }, titles);
    }

    [Fact]
    public void Arithmetic_projection_leaf_containing_a_count_goes_native()
    {
        // AN UNPLANNED INCIDENTAL WIDENING, surfaced by the Task 6 review and pinned here so a future change
        // cannot silently withdraw it. Because the count is recognized as an ORDINARY OPERAND in
        // TranslateOperand, an arithmetic projection leaf containing one now reaches
        // NativeProjectionBinder.TryTranslateLeaf's arithmetic branch and goes native — even though a BARE
        // embedded-collection projection (Select(b => b.Posts.Count)) is still deferred. The count renders in
        // the $expr/aggregation dialect here (a $project leaf is not a $match), so the null-safe $size applies
        // and a missing/null array yields 0 rather than aborting the aggregate.
        var collection = SeedLengths(nameof(Arithmetic_projection_leaf_containing_a_count_goes_native));

        using var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel);

        var doubled = db.Entities.AsNoTracking()
            .Select(b => new { b.Title, X = b.Posts.Count * 2 })
            .ToList().OrderBy(r => r.Title).ToList();

        Assert.Equal(
            [("len0", 0), ("len1", 2), ("len2", 4), ("len3", 6), ("missing", 0), ("null", 0)],
            doubled.Select(r => (r.Title, r.X)).ToArray());
    }

    [Fact]
    public void Count_inside_an_owned_SelectMany_inner_filter_goes_native()
    {
        // The MongoFieldPrefixRewriter case added in Task 2 is LOAD-BEARING, not defensive: an owned
        // SelectMany's inner filter reaches Rewrite, and the count's array path is ELEMENT-relative
        // ("Comments"), which the rewriter must prefix to "Posts.Comments" to address the $unwind-ed element.
        // Without that case this shape THROWS inside pre-existing code instead of working — the same emergent
        // capability (and the same ordering hazard) the Any slice recorded for its own $elemMatch case.
        var collection = Seed(nameof(Count_inside_an_owned_SelectMany_inner_filter_goes_native),
            Row("blog", new BsonArray { PostWithComments("few", 1), PostWithComments("many", 3) }));

        using var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel);

        var headings = db.Entities.AsNoTracking()
            .SelectMany(b => b.Posts.Where(p => p.Comments.Count > 2), (b, p) => new { p.Heading })
            .ToList().Select(x => x.Heading).OrderBy(h => h).ToList();

        Assert.Equal(new[] { "many" }, headings);
    }

    [Fact]
    public void Count_predicate_matches_driver_linq_on_well_formed_rows()
    {
        var collection = SeedWellFormed(nameof(Count_predicate_matches_driver_linq_on_well_formed_rows));
        var titles = AssertNativeAndParity(collection, q => q.Where(b => b.Posts.Count > 1));
        Assert.Equal(new[] { "len2", "len3" }, titles);
    }

    [Fact]
    public void Count_predicate_is_correct_for_a_tracking_query()
    {
        var collection = SeedLengths(nameof(Count_predicate_is_correct_for_a_tracking_query));
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel);

        var titles = db.Entities.Where(b => b.Posts.Count > 1).ToList()
            .Select(b => b.Title).OrderBy(t => t).ToList();

        Assert.Equal(new[] { "len2", "len3" }, titles);
    }
```

- [ ] **Step 3: Write the MQL assertions**

```csharp
    [Theory]
    [InlineData(">", "{ \"Posts.2\" : { \"$exists\" : true } }")]
    [InlineData(">=", "{ \"Posts.1\" : { \"$exists\" : true } }")]
    [InlineData("<", "{ \"Posts.1\" : { \"$exists\" : false } }")]
    [InlineData("<=", "{ \"Posts.2\" : { \"$exists\" : false } }")]
    public void Count_comparison_emits_the_array_index_form(string op, string expectedMatch)
    {
        var collection = SeedWellFormed($"mql{op.Length}{op[0]}");
        using var db = CreateContextWithLogging(collection, MongoQueryMode.NativeOnly, BlogModel, out var spy);

        var query = op switch
        {
            ">" => db.Entities.AsNoTracking().Where(b => b.Posts.Count > 2),
            ">=" => db.Entities.AsNoTracking().Where(b => b.Posts.Count >= 2),
            "<" => db.Entities.AsNoTracking().Where(b => b.Posts.Count < 2),
            _ => db.Entities.AsNoTracking().Where(b => b.Posts.Count <= 2)
        };
        query.ToList();

        AssertMql(spy, expectedMatch);
    }

    [Fact]
    public void Count_equality_emits_a_merged_two_key_match()
    {
        var collection = SeedWellFormed(nameof(Count_equality_emits_a_merged_two_key_match));
        using var db = CreateContextWithLogging(collection, MongoQueryMode.NativeOnly, BlogModel, out var spy);

        db.Entities.AsNoTracking().Where(b => b.Posts.Count == 2).ToList();

        AssertMql(spy, "{ \"Posts.1\" : { \"$exists\" : true }, \"Posts.2\" : { \"$exists\" : false } }");
    }

    [Fact]
    public void A_parameterized_threshold_emits_expr_with_a_null_safe_size()
    {
        var collection = SeedWellFormed(nameof(A_parameterized_threshold_emits_expr_with_a_null_safe_size));
        using var db = CreateContextWithLogging(collection, MongoQueryMode.NativeOnly, BlogModel, out var spy);
        var threshold = 1;

        db.Entities.AsNoTracking().Where(b => b.Posts.Count > threshold).ToList();

        AssertMql(spy, "$ifNull");
        AssertMql(spy, "$size");
        AssertMql(spy, "$expr");
    }

    [Fact]
    public void Bare_Any_still_emits_the_index_zero_form()
    {
        // The bare-Any() unification's byte-identity bar, asserted from the user-facing side.
        var collection = SeedWellFormed(nameof(Bare_Any_still_emits_the_index_zero_form));
        using var db = CreateContextWithLogging(collection, MongoQueryMode.NativeOnly, BlogModel, out var spy);

        db.Entities.AsNoTracking().Where(b => b.Posts.Any()).ToList();

        AssertMql(spy, "{ \"Posts.0\" : { \"$exists\" : true } }");
    }

    [Fact]
    public void Negated_bare_Any_still_emits_the_index_zero_absent_form()
    {
        var collection = SeedWellFormed(nameof(Negated_bare_Any_still_emits_the_index_zero_absent_form));
        using var db = CreateContextWithLogging(collection, MongoQueryMode.NativeOnly, BlogModel, out var spy);

        db.Entities.AsNoTracking().Where(b => !b.Posts.Any()).ToList();

        AssertMql(spy, "{ \"Posts.0\" : { \"$exists\" : false } }");
    }
```

If the captured MQL spacing differs from these literals, capture the actual string once and match it — the
assertion is `Assert.Contains`, so the expected fragment must match the driver's own BSON-to-string spacing
exactly.

- [ ] **Step 4: Write the decline tests**

```csharp
    [Fact]
    public void A_predicated_Count_declines_and_falls_back_to_correct_rows()
    {
        // Count(pred) has no array-index form; it needs $expr over $filter — a separate slice.
        var collection = SeedWellFormed(nameof(A_predicated_Count_declines_and_falls_back_to_correct_rows));

        var titles = AssertDeclinesCleanly(
            collection, q => q.Where(b => b.Posts.Count(p => p.Rank > 0) > 1));

        // len2 has ranks {0,1} → one passes; len3 has {0,1,2} → two pass.
        Assert.Equal(new[] { "len3" }, titles);
    }

    [Fact]
    public void A_primitive_collection_Count_declines_and_falls_back_to_correct_rows()
    {
        // TryResolveOwnedCollectionPath requires an embedded collection NAVIGATION; Tags is a mapped
        // primitive-collection PROPERTY. Deferred deliberately — the right slice lights up Any/All/.Count for
        // primitive collections together.
        var collection = Seed(nameof(A_primitive_collection_Count_declines_and_falls_back_to_correct_rows),
            RowWithTags("notags"), RowWithTags("twotags", "a", "b"));

        var titles = AssertDeclinesCleanly(collection, q => q.Where(b => b.Tags.Count > 1));

        Assert.Equal(new[] { "twotags" }, titles);
    }

    [Fact]
    public void A_parameterized_count_inside_a_quantifier_declines_and_falls_back_to_correct_rows()
    {
        // $expr is a HARD SERVER ERROR inside $elemMatch, so the parameterized tier must decline there rather
        // than emit an unrunnable query. IsQueryDialectRenderable does that with no dedicated guard.
        var collection = Seed(
            nameof(A_parameterized_count_inside_a_quantifier_declines_and_falls_back_to_correct_rows),
            Row("few", new BsonArray { PostWithComments("a", 1) }),
            Row("many", new BsonArray { PostWithComments("a", 3) }));
        var threshold = 2;

        var titles = AssertDeclinesCleanly(
            collection, q => q.Where(b => b.Posts.Any(p => p.Comments.Count > threshold)));

        Assert.Equal(new[] { "many" }, titles);
    }

    [Fact]
    public void A_negated_parameterized_count_declines_and_falls_back_to_correct_rows()
    {
        // The accepted asymmetry: Count <= @param is native, but !(Count > @param) declines, because the
        // negator is gated on query-dialect renderability. A coverage gap, not a correctness one.
        var collection = SeedWellFormed(
            nameof(A_negated_parameterized_count_declines_and_falls_back_to_correct_rows));
        var threshold = 1;

        var titles = AssertDeclinesCleanly(collection, q => q.Where(b => !(b.Posts.Count > threshold)));

        Assert.Equal(new[] { "len0", "len1" }, titles);
    }
```

- [ ] **Step 5: Run the new tests**

Run:
```bash
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --filter "FullyQualifiedName~NativeOwnedCollectionCountTests"
```
Expected: PASS, zero failures.

If `A_parameterized_count_inside_a_quantifier_declines_...` or
`A_negated_parameterized_count_declines_...` fails because the DriverLinq oracle leg aborts on a missing/null
array, switch that test's seed to `SeedWellFormed` (as the `All` slice did for exactly this reason) and adjust
the expected titles.

- [ ] **Step 6: Commit**

```bash
git add tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeOwnedCollectionCountTests.cs
git commit -m "EF-322: functional tests for native owned-collection .Count predicates"
```

---

## Task 8: Differential matrix and mutation checks

**Files:**
- Modify: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeOwnedCollectionCountTests.cs`

**Interfaces:**
- Consumes: Task 7's seeds and helpers.

- [ ] **Step 1: Add the differential matrix**

```csharp
    // ------------------------------------------------------------------
    // Differential matrix — the primary correctness bar for the index arithmetic
    // ------------------------------------------------------------------
    //
    // An off-by-one in the index arithmetic, or a wrong negation direction, returns WRONG ROWS rather than
    // declining — and the driver-LINQ oracle cannot cover the missing/null-array rows (its own count
    // translation aborts the aggregate on such a document). So the oracle here is IN-MEMORY LINQ over the
    // materialized entities: the SAME expression is sent to the server and, compiled, evaluated client-side.
    // Using one expression for both legs is what makes this a real differential test rather than two
    // hand-written predicates that can silently disagree.

    public static TheoryData<string, Expression<Func<Blog, bool>>> CountMatrixCases()
    {
        var data = new TheoryData<string, Expression<Func<Blog, bool>>>();

        // ---- CONSTANT tier: literal thresholds, written out because a literal cannot come from a loop ----
        //
        // These MUST be inline literals. A captured loop variable becomes an EF query PARAMETER, which routes
        // to the $expr tier — so a loop here would exercise the index arithmetic in ZERO rows, leaving the
        // off-by-one risk (the whole reason this matrix exists) completely untested. Thresholds 0/1/2 cover
        // every boundary the arithmetic distinguishes: 0 is the degenerate/upper-bound-only case, 1 is the
        // bare-Any() equivalence point, 2 is a generic interior value.
        data.Add("const-gt0", b => b.Posts.Count > 0);
        data.Add("const-gt1", b => b.Posts.Count > 1);
        data.Add("const-gt2", b => b.Posts.Count > 2);
        data.Add("const-gte0", b => b.Posts.Count >= 0);   // tautology → $expr tier
        data.Add("const-gte1", b => b.Posts.Count >= 1);
        data.Add("const-gte2", b => b.Posts.Count >= 2);
        data.Add("const-lt0", b => b.Posts.Count < 0);     // contradiction → $expr tier
        data.Add("const-lt1", b => b.Posts.Count < 1);
        data.Add("const-lt2", b => b.Posts.Count < 2);
        data.Add("const-lte0", b => b.Posts.Count <= 0);
        data.Add("const-lte1", b => b.Posts.Count <= 1);
        data.Add("const-lte2", b => b.Posts.Count <= 2);
        data.Add("const-eq0", b => b.Posts.Count == 0);
        data.Add("const-eq1", b => b.Posts.Count == 1);
        data.Add("const-eq2", b => b.Posts.Count == 2);
        data.Add("const-eq4", b => b.Posts.Count == 4);    // above every seeded length → empty result
        data.Add("const-ne0", b => b.Posts.Count != 0);
        data.Add("const-ne1", b => b.Posts.Count != 1);
        data.Add("const-ne2", b => b.Posts.Count != 2);

        // ---- PARAMETERIZED tier: a captured local per iteration, exercising $expr + $ifNull ----
        for (var n = 0; n <= 3; n++)
        {
            var t = n;   // captured ⇒ an EF query parameter ⇒ the $expr tier
            data.Add($"param-gt{t}", b => b.Posts.Count > t);
            data.Add($"param-gte{t}", b => b.Posts.Count >= t);
            data.Add($"param-lt{t}", b => b.Posts.Count < t);
            data.Add($"param-lte{t}", b => b.Posts.Count <= t);
            data.Add($"param-eq{t}", b => b.Posts.Count == t);
            data.Add($"param-ne{t}", b => b.Posts.Count != t);
        }

        // Negations, the call forms, a nested count, and a reversed order.
        data.Add("not-gt1", b => !(b.Posts.Count > 1));
        data.Add("not-eq2", b => !(b.Posts.Count == 2));
        data.Add("call-gt1", b => b.Posts.Count() > 1);
        data.Add("longcount-gt1", b => b.Posts.LongCount() > 1L);
        data.Add("reversed", b => 1 < b.Posts.Count);
        data.Add("nested-count", b => b.Posts.Any(p => p.Comments.Count > 0));
        data.Add("and", b => b.Posts.Count > 0 && b.Posts.Count < 3);
        data.Add("or", b => b.Posts.Count == 0 || b.Posts.Count == 3);

        // Any/All regression rows: these paths must be COMPLETELY unaffected by the slice.
        data.Add("any-bare", b => b.Posts.Any());
        data.Add("negated-any-bare", b => !b.Posts.Any());
        data.Add("any-pred", b => b.Posts.Any(p => p.Rank > 0));
        data.Add("all-pred", b => b.Posts.All(p => p.Rank >= 0));

        return data;
    }

    [Theory]
    [MemberData(nameof(CountMatrixCases))]
    public void Count_result_equals_the_in_memory_oracle_for_every_array_length_and_state(
        string name, Expression<Func<Blog, bool>> predicate)
    {
        var collection = Seed($"diff_{name}", DifferentialRows());

        // Oracle: materialize every row, then evaluate the SAME predicate in memory.
        List<string> expected;
        using (var db = CreateContext(collection, MongoQueryMode.Native, BlogModel))
        {
            expected = db.Entities.AsNoTracking().ToList()
                .Where(predicate.Compile()).Select(b => b.Title).OrderBy(t => t).ToList();
        }

        // Server: the query must go NATIVE (NativeOnly is the only reliable signal) and agree exactly.
        List<string> actual;
        using (var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel))
        {
            actual = db.Entities.AsNoTracking().Where(predicate).ToList()
                .Select(b => b.Title).OrderBy(t => t).ToList();
        }

        Assert.Equal(expected, actual);
    }

    // Every array LENGTH boundary crossed with the three "no elements" states, plus a row carrying comments so
    // the nested-count case can discriminate.
    private static BsonDocument[] DifferentialRows() =>
    [
        LenRow("len0", 0), LenRow("len1", 1), LenRow("len2", 2), LenRow("len3", 3),
        Row("missing", posts: null),
        Row("null", BsonNull.Value),
        Row("withComments", new BsonArray { PostWithComments("a", 2) }),
        Row("emptyComments", new BsonArray { PostWithComments("a", 0) }),
    ];
```

**Do not "simplify" the constant block into a loop.** The two blocks exercise two different renderers, and which
one runs is decided by whether the threshold is an inline literal or a captured local. Collapsing the literals
into a loop silently moves all 19 rows onto the `$expr` tier and leaves the index arithmetic untested — the
precise gap this matrix exists to close. Task 1's spike confirms which form EF produces for each; if the spike
found that an inline literal is *also* parameterized, say so in the task report rather than deleting the block,
because that would mean the constant tier is unreachable from ordinary LINQ and the slice's premise needs
revisiting.

- [ ] **Step 2: Run the matrix**

Run:
```bash
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --filter "FullyQualifiedName~NativeOwnedCollectionCountTests"
```
Expected: PASS, zero failures.

- [ ] **Step 3: Mutation check 1 — the negation direction**

Temporarily change `MongoExpressionNegator`'s size case to `$not`-wrap instead of inverting:

```csharp
                negated = new MongoUnaryExpression(MongoUnaryOperator.Not, sizeComparison);
                return true;
```

Run the matrix. Record which rows go red (expect the `not-*` and `negated-any-bare` rows to fail — either with
wrong rows or a render throw). **Then revert.**

- [ ] **Step 4: Mutation check 2 — an off-by-one in the index arithmetic**

Temporarily change `TryRenderSizeComparison`'s `GreaterThan` arm to `MoreThan(size.FieldName, n - 1)`.

Run the matrix. Record which rows go red (expect every `gt*` row plus `reversed`). **Then revert.**

- [ ] **Step 5: Mutation check 3 — the missing `$ifNull`**

Temporarily change `MongoAggregationExpressionRenderer.RenderSize` to always emit the plain
`{$size: "$path"}` form.

Run the matrix. Expect the parameterized rows to fail with an aborted aggregate (a `MongoCommandException`),
**not** a wrong row — that distinction is the point of the check. **Then revert.**

- [ ] **Step 6: Confirm all three mutations are reverted and the suite is green**

Run:
```bash
git diff --stat
dotnet test tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj \
  -c "Debug EF10" --filter "FullyQualifiedName~NativeOwnedCollection"
```
Expected: `git diff --stat` shows only the test file; all `NativeOwnedCollection*` suites PASS.

- [ ] **Step 7: Commit**

```bash
git add tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/NativeOwnedCollectionCountTests.cs
git commit -m "EF-322: differential matrix for owned-collection .Count predicates"
```

Record the three mutation results in the task report — a mutation that does **not** go red means the matrix
cannot detect the bug the design prevents, and that row needs strengthening before the slice is done.

---

## Task 9: Three-version sweep, spec measurement, and documentation

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md`
- Modify: `docs/native-query-status-EF-322.md`
- Possibly modify: spec-test baselines under `tests/.../SpecificationTests/Query/`

- [ ] **Step 1: Run the three-version sweep**

Invoke the `/test-all` skill (builds and tests EF8, EF9, EF10 in parallel).
Expected: **zero failures on all three**, with a uniform pass-count increase (this slice adds no `#if`, so a
non-uniform delta means something is version-conditional and needs investigating). Record the three totals; the
pre-slice baselines are EF8 7552 / EF9 7913 / EF10 7510.

- [ ] **Step 2: Measure the EF10 spec delta on BOTH axes**

Run both sweeps against this branch and against its base (`c19c99b`):

```bash
dotnet build tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj -c "Debug EF10"

dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --logger "trx;LogFileName=native.trx" --results-directory /tmp/count-sweep

MONGODB_EF_NATIVE_ONLY=1 dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --logger "trx;LogFileName=nativeonly.trx" --results-directory /tmp/count-sweep
```

Base figures to compare against: `Native` 4589 pass / 0 fail / 19 skip (total 4608); `NativeOnly` 2194 / 2395 / 19.

**Check both axes per test.** A test can be `NativeOnly`-failing *and* have a `Native`-mode MQL baseline this
slice changes — an inventory built only from the `NativeOnly` pass set misses exactly that case, which is how the
`All` slice's spike missed `Select_All`. The bare-`Any()` unification is the specific thing to watch: any
baseline containing a `"path.0": {$exists: …}` document is a candidate even though the MQL is intended to be
identical.

If a baseline legitimately moved, re-baseline it with a **tightly filtered** rewrite run and diff the result:

```bash
EF_TEST_REWRITE_BASELINES=1 dotnet test tests/MongoDB.EntityFrameworkCore.SpecificationTests/MongoDB.EntityFrameworkCore.SpecificationTests.csproj \
  -c "Debug EF10" --no-build --filter "FullyQualifiedName~<Class>.<Method>"
git diff
```

Then rebuild and re-run **without** the variable to confirm the test is genuinely green.

- [ ] **Step 3: Write the `Query/AGENTS.md` as-built note**

Add a new `>` block-quote note after the "Owned-collection `All` quantifier predicates + the shared predicate
negator" note, titled **"Owned-collection `.Count` in a predicate (EF-322)"**, covering:

- What is now native: the six operators, both orders, `.Count`/`.Count()`/`.LongCount()`, constant *and*
  parameterized thresholds, counts through owned single-reference hops, and counts nested inside a quantifier.
- **The two tiers and which renders when** — including the §3.2 index-arithmetic table.
- **Why query-dialect `$size` was rejected:** `{path: {$size: 0}}` does not match a missing or explicitly-null
  array where LINQ's `Count == 0` is true — the same class of error as `{$ne: []}` for bare `Any()`.
- **Why `$ifNull` in the `$expr` tier is mandatory:** `$size` against a missing/null array aborts the whole
  aggregate.
- **The negator exception, prominently:** an array-count comparison is INVERTED, not `$not`-wrapped, because the
  rendered form is `$exists`, which partitions — the same test as the relational rule with the opposite answer —
  and the admitted set is closed under inversion.
- **The classifier calls the renderer** (`IsQueryDialectRenderable` → `TryRenderSizeComparison`), so the
  three-way negator/classifier/renderer contract holds by construction rather than by parallel maintenance.
- **The bare-`Any()` unification:** one representation for array cardinality; `MongoElemMatchExpression`'s
  predicate is now non-nullable; emitted MQL unchanged.
- **`MongoSizeExpression` is shared** with the projected reference-collection `Count`, distinguished by
  `NullSafe`; the default `false` keeps that path's MQL byte-identical.
- **Why a name-based matcher is safe — and the claim that was CORRECTED.** The protection is structural, not
  the call-site ordering: `TryResolveOwnedCollectionPath` requires the chain to be rooted at the query parameter
  with at least one hop and its FINAL hop to be an embedded collection navigation, and a mapped scalar's receiver
  is an entity rather than a collection, so a same-named scalar can never resolve to an array path. Task 6 fix
  round 1 MEASURED this: moving count recognition ahead of `TryResolveMember` turned no test red. Record it this
  way, not as "the ordering is load-bearing" — that was the original (wrong) framing in this plan, and an
  overclaiming safety comment is worse than none.
- **Deferred, precisely** (spec §7): primitive-element collections, filtered `Count(pred)`, reference-collection
  `.Count`, `.Count` in a projection, two-scope counts, and `!(Count > @param)`.
- **Index behavior as MEASURED** in Task 1 step 2 — state the explain result, do not repeat an assumption.
- Not a break, per the versioning rubric: fallback → native with unchanged results.

- [ ] **Step 4: Update the status report**

In `docs/native-query-status-EF-322.md`:
- Add a sixth row to the owned-data slice table in §2.
- Add a bullet to §3 describing what is now native.
- Remove `.Count`-in-a-predicate from the §4 owned-collection long-tail list and from §5's "nearest owned-data
  follow-ons"; leave the embedded-collection projection item.
- Update §7's two-sweep totals and the "delta since" note with the Task 1 / step 2 measurements.
- Update §8's bottom line so the nearest follow-on is now embedded-collection projections.

- [ ] **Step 5: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/AGENTS.md docs/native-query-status-EF-322.md
git commit -m "EF-322: document native owned-collection .Count predicates"
```

- [ ] **Step 6: Final verification before review**

Run `/test-all` once more on the final tree and confirm zero failures on all three EF versions. Then hand off for
the whole-branch review — **not** the per-task reviews — because in each of the last three slices the final
whole-branch review was the only thing that caught the one Critical, each living in an interaction that no
single-task lens could see.

---

## Squash and hand-off (after review)

Per the stacked-PR convention:

```bash
git branch -f EF-322-owned-collection-count-native-presquash HEAD   # keep until the stack merges
git reset --soft c19c99b
git commit -F <message-file>                                        # full PR-style message
git diff --quiet EF-322-owned-collection-count-native-presquash HEAD && echo "tree identical"
```

Push as a plain fast-forward only — verify with a fresh `git fetch` plus
`git merge-base --is-ancestor` that the remote tip is the squashed commit's direct parent. **No `--force`**
unless the branch owner explicitly authorizes a fold.
